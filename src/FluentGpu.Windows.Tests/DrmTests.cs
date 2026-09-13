using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>M5 tests for the protected (PlayReady/DRM) path at the backend seam: <see cref="MfMediaPlayer"/> DRM-vs-clear
/// routing, the <see cref="ProtectedMediaSession"/> state→sink mapping, and <see cref="ProtectedMediaBackend"/> open +
/// prepare. No real CDM / native call — fakes throughout. The license relay (formerly <c>DrmLicenseBridge</c>) is tested
/// on the runtime in <see cref="ProtectedRuntimeTests"/>; the session state machine in depth in
/// <see cref="ProtectedSessionTests"/>.</summary>
public sealed class DrmTests
{
    private const string AxinomMpd = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── MfMediaPlayer routing: DRM source → protected backend; clear → clear session ─────────────────────────────────

    [Fact]
    public void Capabilities_SupportsDrm_ReflectsInjectedBackend()
    {
        Assert.False(new MfMediaPlayer().Capabilities.SupportsDrm);
        Assert.True(new MfMediaPlayer(new FakeDrmBackend()).Capabilities.SupportsDrm);
    }

    [Fact]
    public async Task Open_ProtectedSource_RoutesToDrmBackend_AndFlowsRelay()
    {
        var drm = new FakeDrmBackend();
        var backend = new MfMediaPlayer(() => new FakeVideoEngine(), drm);
        var relay = new Func<LicenseRequest, ValueTask<LicenseResponse>>(_ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 })));
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));

        var session = await backend
            .OpenAsync(source, new MediaOpenOptions { LicenseRelay = relay }, CancellationToken.None)
            .AsTask().WaitAsync(Bound);

        Assert.Same(drm.Session, session);
        Assert.Equal(1, drm.OpenCalls);
        Assert.Same(relay, drm.Session.LastRelay);   // WithDrm relay flows down to the DRM backend
    }

    [Fact]
    public async Task Open_ClearSource_KeepsClearSession_EvenWithDrmBackend()
    {
        var backend = new MfMediaPlayer(() => new FakeVideoEngine(), new FakeDrmBackend());
        var session = await backend
            .OpenAsync(MediaSource.FromUri("http://host/clip.mp4"), new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(Bound);

        Assert.IsType<MfMediaSession>(session);
        await session.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task Open_ProtectedSource_NoDrmBackend_Throws()
    {
        var backend = new MfMediaPlayer(() => new FakeVideoEngine(), null);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        await Assert.ThrowsAsync<NotSupportedException>(() => backend
            .OpenAsync(source, new MediaOpenOptions(), CancellationToken.None).AsTask().WaitAsync(Bound));
    }

    // ── ProtectedMediaSession: player state → MediaSignalSink mapping ────────────────────────────────────────────────

    [Fact]
    public async Task ProtectedSession_MapsState_LoadingToPlaying_AndDrmErrorToDrm()
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(_ => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend.OpenAsync(source, new MediaOpenOptions { StartPaused = true }, Ct)
            .AsTask().WaitAsync(Bound, Ct);

        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        Assert.Equal(1, player.StartCalls);
        Assert.Equal(PlaybackState.Opening, core.State.Peek());

        var vss = Assert.IsAssignableFrom<IVideoSurfaceSession>(session);

        player.SetState(ProtectedVideoState.Loading);
        vss.PumpVideo(default, default, 1f);
        Assert.Equal(PlaybackState.Opening, core.State.Peek());

        player.SetNaturalSize(1920, 1080);
        player.SetDurationMs(5000);
        player.HasSurface = true;
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
        vss.PumpVideo(default, new RectF(0, 0, 640, 360), 1f);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());
        Assert.Equal(TimeSpan.FromMilliseconds(5000), core.Duration.Peek());

        player.SetError("cdm boom");
        player.SetState(ProtectedVideoState.Error);
        vss.PumpVideo(default, default, 1f);
        Assert.Equal(PlaybackState.Failed, core.State.Peek());
        Assert.NotNull(core.Error.Peek());
        Assert.Equal(MediaErrorCategory.Drm, core.Error.Peek()!.Category);
        Assert.Equal("cdm boom", core.Error.Peek()!.Message);
        Assert.Equal(MediaRecovery.NeedsLicense, core.Error.Peek()!.Recovery);

        await session.DisposeAsync().AsTask().WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task ProtectedBackend_BuildsDescriptor_FromAxinomMpd()
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(_ => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend.OpenAsync(source, new MediaOpenOptions(), Ct).AsTask().WaitAsync(Bound, Ct);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Assert.NotNull(player.StartedWith);
        Assert.Equal("https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/video-H264-720-2100k_init.mp4",
            player.StartedWith!.InitUrl);
        Assert.Equal(".m4s", player.StartedWith.SegmentSuffix);
        Assert.Equal(6, player.StartedWith.SegmentCount);
        Assert.Equal(DrmSystem.PlayReady, player.StartedWith.Drm!.System);

        await session.DisposeAsync().AsTask().WaitAsync(Bound, Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProtectedBackend_ThreadsInitialTransportIntent_IntoNativeRequest(bool startPaused)
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(_ => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend
            .OpenAsync(source, new MediaOpenOptions { StartPaused = startPaused }, Ct)
            .AsTask().WaitAsync(Bound, Ct);

        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Assert.NotNull(player.StartedWith);
        Assert.Equal(startPaused, player.StartedWith!.StartPaused);
        Assert.Equal(startPaused ? 0 : 1, player.PlayCalls);

        await session.DisposeAsync().AsTask().WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task ProtectedSession_ForwardsPauseResumeAndSeek_WithoutCommandCoalescing()
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(_ => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend
            .OpenAsync(source, new MediaOpenOptions { StartPaused = true }, Ct)
            .AsTask().WaitAsync(Bound, Ct);

        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        player.SetDurationMs(5_000);
        Assert.IsAssignableFrom<IVideoSurfaceSession>(session).PumpVideo(default, default, 1f);

        // Every verb returns the player's completed task: the acknowledgement is the next native event, not an ack wait.
        ValueTask play = session.PlayAsync();
        Assert.True(play.IsCompletedSuccessfully);
        ValueTask pause = session.PauseAsync();
        Assert.True(pause.IsCompletedSuccessfully);
        ValueTask play2 = session.PlayAsync();
        Assert.True(play2.IsCompletedSuccessfully);
        ValueTask seek = session.SeekAsync(TimeSpan.FromMilliseconds(3_500), SeekMode.Accurate);
        Assert.True(seek.IsCompletedSuccessfully);

        Assert.Equal(2, player.PlayCalls);
        Assert.Equal(1, player.PauseCalls);
        Assert.Equal(1, player.SeekCalls);
        Assert.Equal(3_500, player.LastSeekMs);
        Assert.Equal(SeekMode.Accurate, player.LastSeekMode);
        Assert.True(core.IsPlayRequested.Peek());
        Assert.Equal(TimeSpan.FromMilliseconds(3_500), core.Position.Peek());

        await session.DisposeAsync().AsTask().WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task ProtectedSession_PublishesCatalog_AndForwardsStableQualityId()
    {
        var codec = new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None);
        ProtectedRepresentationDescriptor Rep(string id, int height) => new()
        {
            Id = id,
            Quality = new QualityVariant(id, height * 2_000, new SizeI(height * 16 / 9, height), 30, codec),
            InitUrl = "https://media/" + id + "/init.mp4",
            SegmentBaseUrl = "https://media/" + id + "/",
            SegmentPrefix = "seg-",
            SegmentSuffix = ".m4s",
            SegmentCount = 10,
        };
        var low = Rep("spotify-5", 180);
        var high = Rep("spotify-0", 1080);
        var descriptor = new DashSourceDescriptor
        {
            InitUrl = low.InitUrl,
            SegmentBaseUrl = low.SegmentBaseUrl,
            SegmentPrefix = low.SegmentPrefix,
            SegmentSuffix = low.SegmentSuffix,
            SegmentCount = 10,
            Catalog = new ProtectedAdaptiveCatalog
            {
                Tracks = [new ProtectedTrackDescriptor
                {
                    Id = 1, Kind = FluentGpu.Media.TrackKind.Video, Label = "Video", IsDefault = true,
                    Representations = [low, high],
                }],
            },
        };
        var native = new FakeProtectedVideoPlayer { SupportsAdaptiveSelection = true };
        var backend = new ProtectedMediaBackend(_ => native, descriptor: descriptor);
        var source = MediaSource.FromUri("https://media/manifest").With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend.OpenAsync(source, new MediaOpenOptions(), Ct);
        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        native.SetNaturalSize(320, 180);
        native.HasSurface = true;
        Assert.IsAssignableFrom<IVideoSurfaceSession>(session).PumpVideo(default, default, 1f);

        Assert.Equal(2, core.Qualities.Variants.Count);
        Assert.True(core.Commands.Can(MediaCommandFlags.SelectVideoQuality));
        await session.SelectQualityAsync(QualitySelection.Pin("spotify-0"));
        Assert.Equal("spotify-0", native.LastSelectedRepresentationId);
        Assert.IsAssignableFrom<IVideoSurfaceSession>(session).PumpVideo(default, default, 1f);
        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());
        // The display metadata switches to 1080p, but the DComp swapchain was created at 320x180 and keeps that physical
        // size. Lying to the presenter with 1920x1080 here scales the visual independently from the video hole.
        var delivery = Assert.IsType<VideoDelivery.CompositedSurface>(session.Video);
        Assert.Equal(new SizeI(320, 180), delivery.NaturalSize);

        await session.DisposeAsync();
    }

    // ── IPreparableBackend on the protected path ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProtectedBackend_Prepare_ReturnsReadyHandle_ForMixedQueue()
    {
        var player = new FakeProtectedVideoPlayer { ReadyOnPrefetch = true };
        var backend = new ProtectedMediaBackend(_ => player, defaultRelay: null);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));

        var item = await backend
            .PrepareAsync(source, PrepareContext.For(new MixFormat(48000, 2), NormMode.Off, 0f), Ct)
            .AsTask().WaitAsync(Bound, Ct);

        Assert.Equal(MediaKind.MfVideoOrFile, item.Kind);
        Assert.True(item.IsReady);
        Assert.Null(item.AudioVoice);
        Assert.Same(player, item.BackendHandle);   // the prepared IProtectedVideoPlayer the open attaches
        Assert.Equal(1, player.PrefetchCalls);
        Assert.Equal(0, player.StartCalls);        // a prepare never touches the engine

        await item.DisposeAsync().AsTask().WaitAsync(Bound, Ct);
    }
}
