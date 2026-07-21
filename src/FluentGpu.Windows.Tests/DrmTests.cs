using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>M5 tests for the protected (PlayReady/DRM) path: the managed <see cref="DrmLicenseBridge"/> relay marshaling
/// + timeout, <see cref="MfMediaPlayer"/> DRM-vs-clear routing, the <see cref="ProtectedMediaSession"/> snapshot→sink
/// mapping, and <see cref="ProtectedMediaBackend"/> open + prepare. No real CDM / native call — fakes throughout.</summary>
public sealed class DrmTests
{
    private const string AxinomMpd = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    // ── DrmLicenseBridge: the relay marshaling logic ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bridge_InvokesRelay_WithChallenge_ReturnsLicense()
    {
        LicenseRequest? seen = null;
        var relay = new Func<LicenseRequest, ValueTask<LicenseResponse>>(req =>
        {
            seen = req;
            return ValueTask.FromResult(new LicenseResponse(new byte[] { 9, 8, 7 }));
        });
        var bridge = new DrmLicenseBridge(relay, DrmSystem.PlayReady, TimeSpan.FromSeconds(2));

        var challenge = new byte[] { 1, 2, 3, 4 };
        var outcome = await Task.Run(() => bridge.Resolve(challenge, "kid-hex")).WaitAsync(Bound);

        Assert.True(outcome.Success);
        Assert.Equal(new byte[] { 9, 8, 7 }, outcome.License);
        Assert.Null(outcome.Error);
        Assert.NotNull(seen);
        Assert.Equal(DrmSystem.PlayReady, seen!.System);
        Assert.Equal("kid-hex", seen.KeyId);
        Assert.Equal(challenge, seen.Challenge.ToArray());
    }

    [Fact]
    public async Task Bridge_RelayThrows_YieldsDrmError_NeverSilentSuccess()
    {
        var relay = new Func<LicenseRequest, ValueTask<LicenseResponse>>(_ => throw new InvalidOperationException("server 403"));
        var bridge = new DrmLicenseBridge(relay, DrmSystem.PlayReady, TimeSpan.FromSeconds(2));

        var outcome = await Task.Run(() => bridge.Resolve(new byte[] { 1 }, null)).WaitAsync(Bound);

        Assert.False(outcome.Success);
        Assert.Null(outcome.License);
        Assert.NotNull(outcome.Error);
        Assert.Equal(MediaErrorCategory.Drm, outcome.Error!.Category);
        Assert.Equal(MediaRecovery.NeedsLicense, outcome.Error.Recovery);
        Assert.Same(outcome.Error, bridge.LastError);
    }

    [Fact]
    public async Task Bridge_RelayTimesOut_YieldsDrmError()
    {
        // A relay that never completes → the bounded wait must fail as a DRM error (block the CDM thread only, never forever).
        var relay = new Func<LicenseRequest, ValueTask<LicenseResponse>>(_ =>
            new ValueTask<LicenseResponse>(new TaskCompletionSource<LicenseResponse>().Task));
        var bridge = new DrmLicenseBridge(relay, DrmSystem.PlayReady, TimeSpan.FromMilliseconds(150));

        var outcome = await Task.Run(() => bridge.Resolve(new byte[] { 1 }, null)).WaitAsync(Bound);

        Assert.False(outcome.Success);
        Assert.Equal(MediaErrorCategory.Drm, outcome.Error!.Category);
        Assert.Equal(MediaRecovery.NeedsLicense, outcome.Error.Recovery);
    }

    [Fact]
    public async Task Bridge_NullRelay_YieldsDrmError()
    {
        var bridge = new DrmLicenseBridge(null, DrmSystem.PlayReady, TimeSpan.FromSeconds(1));
        var outcome = await Task.Run(() => bridge.Resolve(new byte[] { 1 }, null)).WaitAsync(Bound);
        Assert.False(outcome.Success);
        Assert.Equal(MediaErrorCategory.Drm, outcome.Error!.Category);
    }

    [Fact]
    public async Task Bridge_EmptyLicense_YieldsDrmError()
    {
        var relay = new Func<LicenseRequest, ValueTask<LicenseResponse>>(_ =>
            ValueTask.FromResult(new LicenseResponse(Array.Empty<byte>())));
        var bridge = new DrmLicenseBridge(relay, DrmSystem.PlayReady, TimeSpan.FromSeconds(1));
        var outcome = await Task.Run(() => bridge.Resolve(new byte[] { 1 }, null)).WaitAsync(Bound);
        Assert.False(outcome.Success);
        Assert.Equal(MediaErrorCategory.Drm, outcome.Error!.Category);
    }

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

    // ── ProtectedMediaSession: snapshot → MediaSignalSink mapping ────────────────────────────────────────────────────

    [Fact]
    public async Task ProtectedSession_MapsSnapshot_LoadingToPlaying_AndDrmErrorToDrm()
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(() => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend.OpenAsync(source, new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(Bound);

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

        await session.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task ProtectedBackend_BuildsDescriptor_FromAxinomMpd()
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(() => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend.OpenAsync(source, new MediaOpenOptions(), CancellationToken.None).AsTask().WaitAsync(Bound);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Assert.NotNull(player.StartedWith);
        Assert.Equal("https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/video-H264-720-2100k_init.mp4",
            player.StartedWith!.InitUrl);
        Assert.Equal(".m4s", player.StartedWith.SegmentSuffix);
        Assert.Equal(6, player.StartedWith.SegmentCount);
        Assert.Equal(DrmSystem.PlayReady, player.StartedWith.Drm!.System);

        await session.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProtectedBackend_ThreadsInitialTransportIntent_IntoNativeRequest(bool startPaused)
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(() => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend
            .OpenAsync(source, new MediaOpenOptions { StartPaused = startPaused }, CancellationToken.None)
            .AsTask().WaitAsync(Bound);

        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Assert.NotNull(player.StartedWith);
        Assert.Equal(startPaused, player.StartedWith!.StartPaused);
        Assert.Equal(startPaused ? 0 : 1, player.PlayCalls);

        await session.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task ProtectedSession_ForwardsPauseResumeAndSeek_WithoutCommandCoalescing()
    {
        var player = new FakeProtectedVideoPlayer();
        var backend = new ProtectedMediaBackend(() => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend
            .OpenAsync(source, new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(Bound);

        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        player.SetDurationMs(5_000);
        Assert.IsAssignableFrom<IVideoSurfaceSession>(session).PumpVideo(default, default, 1f);

        await session.PlayAsync().AsTask().WaitAsync(Bound);
        await session.PauseAsync().AsTask().WaitAsync(Bound);
        await session.PlayAsync().AsTask().WaitAsync(Bound);
        await session.SeekAsync(TimeSpan.FromMilliseconds(3_500), SeekMode.Accurate).AsTask().WaitAsync(Bound);

        Assert.Equal(2, player.PlayCalls);
        Assert.Equal(1, player.PauseCalls);
        Assert.Equal(3_500, player.LastSeekMs);
        Assert.True(core.IsPlayRequested.Peek());
        Assert.Equal(TimeSpan.FromMilliseconds(3_500), core.Position.Peek());

        await session.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task ProtectedSession_TransportCompletesOnlyAfterNativeAcknowledgement()
    {
        var player = new FakeProtectedVideoPlayer
        {
            PlayAck = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SeekAck = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var backend = new ProtectedMediaBackend(() => player);
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));
        var session = await backend
            .OpenAsync(source, new MediaOpenOptions { StartPaused = true }, CancellationToken.None)
            .AsTask().WaitAsync(Bound);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Task play = session.PlayAsync().AsTask();
        Assert.False(play.IsCompleted);
        player.PlayAck.SetResult(true);
        await play.WaitAsync(Bound);

        Task seek = session.SeekAsync(TimeSpan.FromSeconds(2), SeekMode.Accurate).AsTask();
        Assert.False(seek.IsCompleted);
        player.SeekAck.SetResult(true);
        await seek.WaitAsync(Bound);

        await session.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    // ── IPreparableBackend on the protected path ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProtectedBackend_Prepare_ReturnsReadyHandle_ForMixedQueue()
    {
        var player = new FakeProtectedVideoPlayer { ReadyOnStart = true };
        var backend = new ProtectedMediaBackend(() => player, defaultRelay: null, prepareTimeout: TimeSpan.FromSeconds(1));
        var source = MediaSource.FromUri(AxinomMpd).With(new DrmConfig(DrmSystem.PlayReady));

        var item = await backend
            .PrepareAsync(source, PrepareContext.For(new MixFormat(48000, 2), NormMode.Off, 0f), CancellationToken.None)
            .AsTask().WaitAsync(Bound);

        Assert.Equal(MediaKind.MfVideoOrFile, item.Kind);
        Assert.True(item.IsReady);
        Assert.Null(item.AudioVoice);
        Assert.IsType<ProtectedMediaSession>(item.BackendHandle);

        await item.DisposeAsync().AsTask().WaitAsync(Bound);
    }
}
