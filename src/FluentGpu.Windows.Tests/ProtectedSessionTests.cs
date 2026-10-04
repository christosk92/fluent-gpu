using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The protected session state machine over a fake native layer: <see cref="ProtectedMediaSession"/> driven by a
/// <see cref="FakeProtectedVideoPlayer"/> into a real <see cref="MediaPlayerCore"/> + <see cref="MediaSignalSink"/>, the
/// pure statics it is built from (<see cref="ProtectedMediaSession.StartBudget"/>,
/// <see cref="ProtectedMediaSession.StartFailureCategory"/>, <see cref="ProtectedMediaSession.ExtrapolatePositionMs"/>,
/// <see cref="ProtectedMediaSession.MapState"/>, <see cref="ProtectedVideoSession.DerivePhase"/>), the surface hand-off
/// through a real <see cref="VideoSurfaceRegistry"/> drained into a <see cref="FakeVideoPresenter"/>, and the
/// <see cref="ProtectedMediaBackend"/> prepare→open hand-off. No CDM, no GPU, no window, no network. The one-shot start
/// deadline floors at 10 s, so it is covered through its pure inputs, never by waiting for it.
/// </summary>
public sealed class ProtectedSessionTests : IAsyncDisposable
{
    private const string InitA = "https://cdn.test/a/init.mp4";
    private const string InitB = "https://cdn.test/b/init.mp4";
    private const string ContentKid = "4060a865887842679cbf91ae5bae1e72";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly RectF Rect = new(0, 0, 640, 360);
    private static readonly PrepareContext Prep = PrepareContext.For(new MixFormat(48000, 2), NormMode.Off, 0f);

    private readonly List<IAsyncDisposable> _cleanup = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable d in _cleanup) await d.DisposeAsync();
    }

    private (ProtectedMediaSession Session, MediaPlayerCore Core, FakeProtectedVideoPlayer Player) NewSession(
        bool startPaused = true, TimeSpan startPosition = default)
    {
        var player = new FakeProtectedVideoPlayer();
        var request = new ProtectedVideoRequest { InitUrl = InitA, StartPaused = startPaused, StartPosition = startPosition };
        var session = new ProtectedMediaSession(player, request,
            new MediaOpenOptions { StartPaused = startPaused, StartPosition = startPosition });
        _cleanup.Add(session);
        var core = new MediaPlayerCore();
        session.ConnectSignals(new MediaSignalSink(core));
        return (session, core, player);
    }

    private static DashSourceDescriptor Descriptor(string initUrl) => new()
    {
        InitUrl = initUrl,
        SegmentBaseUrl = initUrl[..(initUrl.LastIndexOf('/') + 1)],
        SegmentPrefix = "seg-",
        SegmentSuffix = ".m4s",
        SegmentCount = 10,
        SegmentLengthMs = 4_000,
        DurationMs = 40_000,
        DefaultKid = ContentKid,
    };

    private static MediaSource SourceFor(string initUrl)
        => MediaSource.FromUri("https://cdn.test/manifest.mpd")
            .With(new DrmConfig(DrmSystem.PlayReady) { SourceDescriptor = Descriptor(initUrl) });

    private static VideoBinding NewBinding(VideoSurfaceRegistry registry) => new(registry, registry.Acquire());

    /// <summary>A backend over a player factory that records every creation and license start, in order.</summary>
    private sealed class BackendRig
    {
        public readonly List<FakeProtectedVideoPlayer> Created = new();
        public readonly List<ProtectedVideoRequest> Ensured = new();
        public readonly List<string> Order = new();
        public readonly ProtectedMediaBackend Backend;
        public bool ReadyOnPrefetch = true;
        public Action<FakeProtectedVideoPlayer>? Configure;

        public BackendRig(Func<LicenseRequest, ValueTask<LicenseResponse>>? defaultRelay = null,
                          DashSourceDescriptor? fallback = null)
        {
            Backend = new ProtectedMediaBackend(req =>
            {
                Order.Add("create:" + req.InitUrl);
                var player = new FakeProtectedVideoPlayer { ReadyOnPrefetch = ReadyOnPrefetch };
                Configure?.Invoke(player);
                Created.Add(player);
                return player;
            }, defaultRelay, fallback, ensureLicense: req =>
            {
                Order.Add("license:" + req.InitUrl);
                Ensured.Add(req);
            });
        }
    }

    // ── connect / start ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConnectSignals_PublishesOpening_AndTheCarriedStartPositionAtOnce()
    {
        var (_, core, player) = NewSession(startPosition: TimeSpan.FromSeconds(83));

        Assert.Equal(PlaybackState.Opening, core.State.Peek());
        Assert.Equal(TimeSpan.FromSeconds(83), core.Position.Peek());   // the seek bar never shows 0:00 for a 1:23 open
        Assert.False(core.IsPlayRequested.Peek());
        Assert.Equal(1, player.StartCalls);
        Assert.Equal(TimeSpan.FromSeconds(83), player.StartedWith!.StartPosition);
        Assert.True(player.StartedWith.StartPaused);
        Assert.Equal(0, player.PumpCalls);   // connect requests a pump; it never runs one itself
    }

    [Fact]
    public void ConnectSignals_WithNoStartPosition_LeavesThePositionAtZero()
    {
        var (_, core, _) = NewSession();
        Assert.Equal(TimeSpan.Zero, core.Position.Peek());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenAsync_CarriesTheStartPositionAndIntent_IntoTheStartedRequest(bool startPaused)
    {
        var rig = new BackendRig();
        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA),
            new MediaOpenOptions { StartPosition = TimeSpan.FromSeconds(83), StartPaused = startPaused }, Ct);
        _cleanup.Add(session);
        var core = new MediaPlayerCore();

        session.ConnectSignals(new MediaSignalSink(core));

        FakeProtectedVideoPlayer player = Assert.Single(rig.Created);
        Assert.Equal(TimeSpan.FromSeconds(83), player.StartedWith!.StartPosition);
        Assert.Equal(startPaused, player.StartedWith.StartPaused);
        Assert.Equal(InitA, player.StartedWith.InitUrl);
        Assert.Equal(TimeSpan.FromSeconds(83), core.Position.Peek());
        Assert.Equal(!startPaused, core.IsPlayRequested.Peek());
    }

    [Fact]
    public async Task OpenAsync_ANegativeStartPosition_OpensAtZero()
    {
        var rig = new BackendRig();
        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA),
            new MediaOpenOptions { StartPosition = TimeSpan.FromSeconds(-5) }, Ct);
        _cleanup.Add(session);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        Assert.Equal(TimeSpan.Zero, Assert.Single(rig.Created).StartedWith!.StartPosition);
    }

    // ── event-driven pumping (no timer) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APlayerPumpRequest_ReRaisesTheSessionsPumpRequested_AndNothingRaisesItWithoutOne()
    {
        var (session, _, player) = NewSession();
        Assert.Equal(1, player.PumpRequestedSubscribers);
        int raised = 0;
        var firstRaise = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PumpRequested += () =>
        {
            Interlocked.Increment(ref raised);
            firstRaise.TrySetResult();
        };

        // No poll timer: with no native event, nothing asks for a pump.
        await Assert.ThrowsAsync<TimeoutException>(() => firstRaise.Task.WaitAsync(TimeSpan.FromMilliseconds(300), Ct));
        Assert.Equal(0, Volatile.Read(ref raised));

        player.RaisePump();
        Assert.Equal(1, Volatile.Read(ref raised));
        player.RaisePump();
        Assert.Equal(2, Volatile.Read(ref raised));

        await session.DisposeAsync();
        Assert.Equal(0, player.PumpRequestedSubscribers);
        player.RaisePump();
        Assert.Equal(2, Volatile.Read(ref raised));
    }

    // ── lifecycle mapping ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadingIsOpening_ThenTheFirstFrameWhilePlaying_IsPlaying_WithNaturalSizeDurationAndCommands()
    {
        var (session, core, player) = NewSession(startPaused: false);

        player.SetState(ProtectedVideoState.Loading);
        player.Phase = ProtectedVideoPhase.Licensing;
        session.PumpVideo(default, Rect, 1f);
        Assert.Equal(PlaybackState.Opening, core.State.Peek());
        Assert.Equal(SizeI.Zero, core.NaturalSize.Peek());
        Assert.Equal(1, player.PumpCalls);

        player.SetNaturalSize(1920, 1080);
        player.SetDurationMs(5_000);
        player.FirstFrameEpoch = 1;
        player.Phase = ProtectedVideoPhase.Playing;
        player.SetPositionMs(0);
        player.PositionQpc = Stopwatch.GetTimestamp();
        player.SetState(ProtectedVideoState.Playing);
        session.PumpVideo(default, Rect, 1f);

        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());
        Assert.Equal(TimeSpan.FromSeconds(5), core.Duration.Peek());
        Assert.True(core.Commands.Can(MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek));
        Assert.False(core.Commands.Can(MediaCommandFlags.SelectVideoQuality));   // no adaptive catalog
        Assert.False(core.Buffering.Peek().IsBuffering);
    }

    [Fact]
    public void Play_ReturnsACompletedTask_AndTheTransportSettlesWhenPlayingArrives()
    {
        var (session, core, player) = NewSession(startPaused: true);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Paused);
        session.PumpVideo(default, Rect, 1f);
        Assert.Equal(PlaybackState.Paused, core.State.Peek());

        ValueTask play = session.PlayAsync();

        Assert.True(play.IsCompletedSuccessfully);   // no ack wait: Playing arriving IS the acknowledgement
        Assert.Equal(1, player.PlayCalls);
        Assert.True(core.IsPlayRequested.Peek());

        ValueTask gate = core.BeginTransport(static () => { });
        session.PumpVideo(default, Rect, 1f);        // still paused natively, play requested
        Assert.Equal(PlaybackState.Buffering, core.State.Peek());
        Assert.False(gate.IsCompleted);

        player.SetState(ProtectedVideoState.Playing);
        session.PumpVideo(default, Rect, 1f);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.True(gate.IsCompleted);
    }

    [Fact]
    public void Seek_PublishesTheTargetAndSeekingAtOnce_HoldsItWhileSeeking_ThenSettlesOnTheLandedPosition()
    {
        var (session, core, player) = NewSession(startPaused: true);
        player.SetDurationMs(60_000);
        player.SetNaturalSize(1280, 720);
        player.FirstFrameEpoch = 1;
        player.SetPositionMs(1_000);
        player.SetState(ProtectedVideoState.Paused);
        session.PumpVideo(default, Rect, 1f);
        Assert.Equal(TimeSpan.FromSeconds(1), core.Position.Peek());

        ValueTask seek = session.SeekAsync(TimeSpan.FromSeconds(31), SeekMode.Keyframe, keyframeMs: 30_000);

        Assert.True(seek.IsCompletedSuccessfully);
        Assert.Equal(31_000, player.LastSeekMs);
        Assert.Equal(SeekMode.Keyframe, player.LastSeekMode);
        Assert.Equal(30_000, player.LastKeyframeHint);
        Assert.Equal(TimeSpan.FromSeconds(31), core.Position.Peek());            // the target, on the calling thread
        Assert.Equal(PlaybackState.Buffering, core.State.Peek());
        Assert.Equal(BufferingReason.Seeking, core.Buffering.Peek().Reason);

        ValueTask gate = core.BeginTransport(static () => { });
        session.PumpVideo(default, Rect, 1f);                                     // in flight: native still says 1 s
        Assert.Equal(TimeSpan.FromSeconds(31), core.Position.Peek());
        Assert.Equal(PlaybackState.Buffering, core.State.Peek());
        Assert.Equal(BufferingReason.Seeking, core.Buffering.Peek().Reason);
        Assert.False(gate.IsCompleted);

        // Seeked: the keyframe at or before the target landed.
        player.LastSeekLandedMs = 30_000;
        player.SetPositionMs(30_000);
        player.PositionQpc = Stopwatch.GetTimestamp();
        player.IsSeeking = false;
        session.PumpVideo(default, Rect, 1f);

        Assert.True(gate.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(30), core.Position.Peek());
        Assert.Equal(PlaybackState.Paused, core.State.Peek());
        Assert.False(core.Buffering.Peek().IsBuffering);
        Assert.Contains(player.Diagnostics, line => line.Contains("seek.landed target=31000 landed=30000"));
    }

    [Fact]
    public void AfterASeekLandsWhilePlaying_ThePublishedPositionIsTheNativeSampleExtrapolated()
    {
        var (session, core, player) = NewSession(startPaused: false);
        player.SetDurationMs(60_000);
        player.FirstFrameEpoch = 1;
        player.PositionQpc = Stopwatch.GetTimestamp();
        player.SetState(ProtectedVideoState.Playing);
        session.PumpVideo(default, Rect, 1f);

        Assert.True(session.SeekAsync(TimeSpan.FromSeconds(20), SeekMode.Accurate).IsCompletedSuccessfully);
        player.LastSeekLandedMs = 20_000;
        player.SetPositionMs(20_000);
        player.PositionQpc = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 2;   // sampled half a second ago
        player.IsSeeking = false;
        session.PumpVideo(default, Rect, 1f);

        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.InRange(core.Position.Peek().TotalMilliseconds, 20_500, 22_500);
    }

    [Fact]
    public void ANativeRebufferAfterTheFirstFrame_PublishesStalled_WithARebufferingReason()
    {
        var (session, core, player) = NewSession(startPaused: false);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
        session.PumpVideo(default, Rect, 1f);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());

        player.ForwardBufferedMs = 0;
        player.SetState(ProtectedVideoState.Buffering);
        session.PumpVideo(default, Rect, 1f);

        Assert.Equal(PlaybackState.Stalled, core.State.Peek());
        // The clear MF session publishes a Rebuffering reason for a stall; the protected one must not clear it to None.
        Assert.Equal(BufferingReason.Rebuffering, core.Buffering.Peek().Reason);
    }

    [Fact]
    public void ANativeError_PublishesOneTypedDrmError_AndFailed()
    {
        var (session, core, player) = NewSession();
        player.SetError("The PlayReady license was not granted (0x8004C600).");
        player.SetState(ProtectedVideoState.Error);

        session.PumpVideo(default, Rect, 1f);

        MediaError? error = core.Error.Peek();
        Assert.NotNull(error);
        Assert.Equal(MediaErrorCategory.Drm, error!.Category);
        Assert.Equal(MediaRecovery.NeedsLicense, error.Recovery);
        Assert.Equal("The PlayReady license was not granted (0x8004C600).", error.Message);
        Assert.Equal(PlaybackState.Failed, core.State.Peek());

        core.SetError(null);
        session.PumpVideo(default, Rect, 1f);
        Assert.Null(core.Error.Peek());   // published once, never re-raised per pump
    }

    [Fact]
    public void AnErrorAFreshRuntimeCures_PublishesARetryableNonDrmError_CarryingTheHresult()
    {
        var (session, core, player) = NewSession();
        int hr = unchecked((int)0x887A0005);   // DXGI_ERROR_DEVICE_REMOVED
        player.ErrorHr = hr;
        player.ErrorNeedsRuntimeRebuild = true;
        player.SetError("The protected-video runtime was reset (0x887A0005); reopen the video.");
        player.SetState(ProtectedVideoState.Error);

        session.PumpVideo(default, Rect, 1f);

        MediaError? error = core.Error.Peek();
        Assert.NotNull(error);
        Assert.Equal(MediaRecovery.Retryable, error!.Recovery);   // the owner reopens it, never "your license failed"
        Assert.NotEqual(MediaErrorCategory.Drm, error.Category);
        Assert.Equal(hr, (int)error.UnderlyingCode!.Value);
        Assert.Equal(PlaybackState.Failed, core.State.Peek());
    }

    [Fact]
    public async Task Dispose_StopsAndDisposesThePlayer_Unsubscribes_AndSilencesTheTransport()
    {
        var (session, _, player) = NewSession();

        await session.DisposeAsync();

        Assert.Equal(1, player.StopCalls);
        Assert.Equal(1, player.DisposeCalls);
        Assert.Equal(0, player.PumpRequestedSubscribers);
        Assert.True(session.PlayAsync().IsCompletedSuccessfully);
        Assert.Equal(0, player.PlayCalls);
        session.PumpVideo(default, Rect, 1f);
        Assert.Equal(0, player.PumpCalls);

        await session.DisposeAsync();
        Assert.Equal(1, player.DisposeCalls);
    }

    [Fact]
    public void VolumeMuteAndRate_ForwardToThePlayer()
    {
        var (session, core, player) = NewSession();

        session.SetVolume(0.5);
        Assert.Equal(0.5f, player.LastVolume);
        session.SetMuted(true);
        Assert.Equal(0f, player.LastVolume);
        Assert.True(core.Muted.Peek());
        session.SetMuted(false);
        Assert.Equal(0.5f, player.LastVolume);
        session.SetVolume(3);
        Assert.Equal(1f, player.LastVolume);

        session.SetRate(2);
        Assert.Equal(2f, player.LastRate);
        session.SetRate(0);
        Assert.Equal(1f, player.LastRate);
    }

    // ── the composited surface ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PumpVideo_WithAValidBindingAndASurface_SizesTheStreamToWhatTheDestinationCanShow()
    {
        var (session, core, player) = NewSession();
        var registry = new VideoSurfaceRegistry();
        VideoBinding binding = NewBinding(registry);
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(3840, 2160);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Loading);

        session.PumpVideo(binding, Rect, 1f);

        Assert.Equal(new SizeI(1280, 720), player.LastStreamSize);   // a 4K rung in a 640-px card allocates the 1/3 bucket, not 4K buffers
        Assert.Equal(1, player.SetStreamSizeCalls);
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);
        Assert.Equal(1280u, presenter.LastContentW);
        Assert.Equal(720u, presenter.LastContentH);
        Assert.Equal(Rect, presenter.LastPlaceRect);
        Assert.True(presenter.LastVisible);
        VideoSurfaceGeometry geometry = core.SurfaceGeometry.Peek();
        Assert.Equal(new SizeI(3840, 2160), geometry.Natural);
        Assert.Equal(new SizeI(1280, 720), geometry.Content);
        Assert.Equal(binding.Token, geometry.Token);
        var delivery = Assert.IsType<VideoDelivery.CompositedSurface>(session.Video);
        Assert.Equal(new SizeI(3840, 2160), delivery.NaturalSize);
    }

    [Fact]
    public void PumpVideo_NeverSizesTheStream_WithoutAValidBindingAndASurface()
    {
        var (session, _, player) = NewSession();
        player.SetNaturalSize(3840, 2160);

        player.HasSurface = true;
        session.PumpVideo(default, Rect, 1f);                                   // an inert (headless) binding
        Assert.Equal(0, player.SetStreamSizeCalls);

        player.HasSurface = false;
        session.PumpVideo(NewBinding(new VideoSurfaceRegistry()), Rect, 1f);    // no swap chain produced yet
        Assert.Equal(0, player.SetStreamSizeCalls);
    }

    [Fact]
    public void PumpVideo_BeforeTheFirstFrameOfThisAttach_PlacesTheSurfaceButNeverShowsIt()
    {
        var (session, core, player) = NewSession(startPaused: false);
        var registry = new VideoSurfaceRegistry();
        var presenter = new FakeVideoPresenter();
        VideoBinding binding = NewBinding(registry);
        player.HasSurface = true;                       // the swap chain exists (LOADEDMETADATA) ...
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1280, 720);
        player.SetState(ProtectedVideoState.Loading);   // ... but its first frame has not landed: it holds the PREVIOUS source's picture

        session.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, scale: 1f);

        Assert.Equal(new SizeI(640, 360), player.LastStreamSize);   // sized and placed already (the 1/2 bucket), so the first frame lands in place
        Assert.Equal(Rect, presenter.LastPlaceRect);
        Assert.False(presenter.LastVisible);
        Assert.Equal(default(VideoSurfaceId), core.VideoSurface.Peek());
        Assert.IsType<VideoDelivery.CompositedSurface>(session.Video);

        player.FirstFrameEpoch = 1;                     // FIRSTFRAMEREADY of this attach
        session.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, scale: 1f);

        Assert.True(presenter.LastVisible);
        Assert.NotEqual(default(VideoSurfaceId), core.VideoSurface.Peek());
    }

    [Fact]
    public void PumpVideo_ASecondAttachWithoutItsFirstFrame_HidesTheSlotAgain()
    {
        var (session, core, player) = NewSession(startPaused: false);
        var registry = new VideoSurfaceRegistry();
        var presenter = new FakeVideoPresenter();
        VideoBinding binding = NewBinding(registry);
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1280, 720);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
        session.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, scale: 1f);
        Assert.True(presenter.LastVisible);

        player.HasFirstFrame = false;                   // re-attached: FirstFrameEpoch still holds the old attach's value
        session.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, scale: 1f);

        Assert.False(presenter.LastVisible);
        Assert.Equal(default(VideoSurfaceId), core.VideoSurface.Peek());
    }

    [Fact]
    public void PumpVideo_WhenTheSurfaceIsDetached_HidesTheSlotAndWithdrawsTheVideoSurface()
    {
        var (session, core, player) = NewSession(startPaused: false);
        var registry = new VideoSurfaceRegistry();
        var presenter = new FakeVideoPresenter();
        VideoBinding binding = NewBinding(registry);
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1280, 720);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
        session.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, scale: 1f);
        Assert.True(presenter.LastVisible);
        Assert.NotEqual(default(VideoSurfaceId), core.VideoSurface.Peek());

        player.HasSurface = false;                      // another session's attach detached this one natively
        session.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, scale: 1f);

        Assert.False(presenter.LastVisible);
        Assert.Equal(default(VideoSurfaceId), core.VideoSurface.Peek());
        Assert.Equal(VideoDelivery.None, session.Video);
    }

    [Fact]
    public void PumpVideo_ANaturalSizeGrow_RaisesTheStreamSize_WhenTheDestinationCanShowIt()
    {
        var (session, core, player) = NewSession(startPaused: false);
        var registry = new VideoSurfaceRegistry();
        VideoBinding binding = NewBinding(registry);
        var big = new RectF(0, 0, 1920, 1080);
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
        player.SetNaturalSize(1280, 720);               // the opening rung
        session.PumpVideo(binding, big, 1f);
        Assert.Equal(new SizeI(1280, 720), player.LastStreamSize);

        player.SetNaturalSize(1920, 1080);              // ABR upgraded: FORMATCHANGE reported the bigger frame
        session.PumpVideo(binding, big, 1f);

        Assert.Equal(new SizeI(1920, 1080), player.LastStreamSize);   // not pinned to the opening rung's swap chain (asked at once)
        Assert.Equal(2, player.SetStreamSizeCalls);
        // ... but the compositor keeps scaling the old buffer by the old size until native echoes the new one (the fake echoes
        // at the next snapshot), so this pump still publishes the opening rung's content size.
        Assert.Equal(new SizeI(1280, 720), core.SurfaceGeometry.Peek().Content);
        session.PumpVideo(binding, big, 1f);
        Assert.Equal(new SizeI(1920, 1080), core.SurfaceGeometry.Peek().Content);
    }

    [Fact]
    public void PumpVideo_WithAnEmptyRect_NeverSizesOrPlacesTheStream()
    {
        var (session, core, player) = NewSession(startPaused: false);
        VideoBinding binding = NewBinding(new VideoSurfaceRegistry());
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1920, 1080);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);

        session.PumpVideo(binding, default, 1f);       // the element's pump before its area is laid out

        Assert.Equal(0, player.SetStreamSizeCalls);    // no 2x1 swap chain
        Assert.True(binding.ContentSize.IsEmpty);
        Assert.False(core.SurfaceGeometry.Peek().IsPlaced);

        session.PumpVideo(binding, Rect, 1f);          // the first laid-out pump sizes it
        Assert.Equal(1, player.SetStreamSizeCalls);
        Assert.Equal(new SizeI(640, 360), player.LastStreamSize);

        session.PumpVideo(binding, default, 1f);       // the area collapsed again: size and placement stay
        Assert.Equal(1, player.SetStreamSizeCalls);
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);
    }

    [Fact]
    public void PumpVideo_AResize_RequestsTheNewBucketOnlyOnceSettled_AndPublishesItOnlyWhenNativeEchoesIt()
    {
        var (session, _, player) = NewSession(startPaused: false);
        long now = 0;
        session.ClockMs = () => now;
        VideoBinding binding = NewBinding(new VideoSurfaceRegistry());
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1920, 1080);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);
        player.EchoesStreamSize = false;               // the test holds the native echo back

        session.PumpVideo(binding, Rect, 1f);
        Assert.Equal(new SizeI(640, 360), player.LastStreamSize);   // the 1/3 bucket
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);
        player.ScriptedAppliedStreamSize = new SizeI(640, 360);

        var grown = new RectF(0, 0, 1000, 562);
        now = 100;
        session.PumpVideo(binding, grown, 1f);         // a resize gesture is under way
        Assert.Equal(1, player.SetStreamSizeCalls);
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);

        now = 350;
        session.PumpVideo(binding, grown, 1f);         // it has been still for 250 ms
        Assert.Equal(2, player.SetStreamSizeCalls);
        Assert.Equal(new SizeI(1440, 810), player.LastStreamSize);   // the 3/4 bucket
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);      // DComp keeps scaling the old buffer by the old size

        now = 400;
        session.PumpVideo(binding, grown, 1f);
        Assert.Equal(2, player.SetStreamSizeCalls);                  // asked once
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);      // not echoed yet

        player.ScriptedAppliedStreamSize = new SizeI(1440, 810);     // native applied it
        session.PumpVideo(binding, grown, 1f);
        Assert.Equal(new SizeI(1440, 810), binding.ContentSize);
    }

    [Fact]
    public void APlacementMove_BindsTheSameHandleThroughTheNewToken_OnTheNextPump_WithNoReopen()
    {
        var (session, core, player) = NewSession(startPaused: false);
        var registry = new VideoSurfaceRegistry();
        var presenter = new FakeVideoPresenter();
        player.HasSurface = true;
        player.SurfaceHandle = 0xBEEF;
        player.SetNaturalSize(1280, 720);
        player.FirstFrameEpoch = 1;
        player.SetState(ProtectedVideoState.Playing);

        VideoBinding docked = NewBinding(registry);
        session.PumpVideo(docked, Rect, 1f);
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);

        // docked → pop-out: the old slot is released and a new token acquired.
        registry.Release(docked.Token);
        VideoBinding popOut = NewBinding(registry);
        Assert.NotEqual(docked.Token, popOut.Token);
        var popOutRect = new RectF(0, 0, 960, 540);
        presenter.Calls.Clear();

        session.PumpVideo(popOut, popOutRect, 1f);
        registry.Drain(presenter, scale: 1f);

        Assert.Equal(new[] { docked.Token, popOut.Token }, player.PumpedTokens);
        Assert.Contains(presenter.Calls, c => c.StartsWith("Destroy(", StringComparison.Ordinal));
        Assert.Contains(presenter.Calls, c => c.StartsWith("Bind(", StringComparison.Ordinal) && c.EndsWith(",0xBEEF)", StringComparison.Ordinal));
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);
        Assert.Equal(popOutRect, presenter.LastPlaceRect);
        Assert.Equal(popOut.Token, core.SurfaceGeometry.Peek().Token);
        Assert.Equal(1, player.StartCalls);   // no open, no re-attach …
        Assert.Equal(0, player.SeekCalls);    // … and no seek
    }

    // ── pure statics ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtrapolatePositionMs_NotPlaying_IsTheSample()
        => Assert.Equal(1_234, ProtectedMediaSession.ExtrapolatePositionMs(1_234, 1_000, playing: false, 1.0, 1_000 + Stopwatch.Frequency));

    [Theory]
    [InlineData(1.0, 1_400L)]
    [InlineData(2.0, 1_800L)]
    [InlineData(0.5, 1_200L)]
    public void ExtrapolatePositionMs_Playing_AddsElapsedTimesRate(double rate, long expected)
    {
        const long sampledAt = 5_000_000;
        Assert.Equal(expected,
            ProtectedMediaSession.ExtrapolatePositionMs(1_000, sampledAt, playing: true, rate, sampledAt + Stopwatch.Frequency * 2 / 5));
    }

    [Theory]
    [InlineData(1.0, 1_500L)]
    [InlineData(2.0, 2_000L)]
    [InlineData(0.5, 1_250L)]
    public void ExtrapolatePositionMs_StopsAtTheCap_WhenNoNewSampleArrives(double rate, long expected)
    {
        // F030: a clock that stopped without the state saying so (TIMEUPDATE went quiet) must not run on from a stale sample.
        const long sampledAt = 5_000_000;
        Assert.Equal(expected,
            ProtectedMediaSession.ExtrapolatePositionMs(1_000, sampledAt, playing: true, rate, sampledAt + Stopwatch.Frequency));
        Assert.Equal(expected,
            ProtectedMediaSession.ExtrapolatePositionMs(1_000, sampledAt, playing: true, rate, sampledAt + Stopwatch.Frequency * 60));
    }

    [Fact]
    public void ExtrapolatePositionMs_AQuarterSecondLater_AddsAQuarterSecond()
    {
        const long sampledAt = 1_000;
        long position = ProtectedMediaSession.ExtrapolatePositionMs(10_000, sampledAt, playing: true, 1.0,
            sampledAt + Stopwatch.Frequency / 4);
        Assert.InRange(position, 10_249, 10_250);
    }

    [Fact]
    public void ExtrapolatePositionMs_AnUnknownTimestamp_OrANowNotAfterTheSample_IsTheSample()
    {
        Assert.Equal(1_234, ProtectedMediaSession.ExtrapolatePositionMs(1_234, 0, playing: true, 1.0, Stopwatch.Frequency * 10));
        Assert.Equal(1_234, ProtectedMediaSession.ExtrapolatePositionMs(1_234, 5_000, playing: true, 1.0, 5_000));
        Assert.Equal(1_234, ProtectedMediaSession.ExtrapolatePositionMs(1_234, 5_000, playing: true, 1.0, 4_000));
    }

    [Fact]
    public void ExtrapolatePositionMs_IsNeverNegative()
    {
        Assert.Equal(0, ProtectedMediaSession.ExtrapolatePositionMs(-500, 0, playing: false, 1.0, 0));
        Assert.Equal(0, ProtectedMediaSession.ExtrapolatePositionMs(-500, 1_000, playing: true, 1.0, 1_000 + Stopwatch.Frequency / 10));
        Assert.Equal(0, ProtectedMediaSession.ExtrapolatePositionMs(1_000, 1_000, playing: true, -4.0, 1_000 + Stopwatch.Frequency));
    }

    [Fact]
    public void StartBudget_WithNoPolicy_IsTheTenSecondFloor()
        => Assert.Equal(TimeSpan.FromSeconds(10), ProtectedMediaSession.StartBudget(null));

    [Theory]
    [InlineData(1_500, 15_000)]
    [InlineData(3_000, 30_000)]
    [InlineData(1_000, 10_000)]
    [InlineData(500, 10_000)]
    [InlineData(0, 10_000)]
    public void StartBudget_IsTenTimesTheInitialPlaybackTarget_FlooredAtTenSeconds(int initialPlaybackMs, int budgetMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(budgetMs),
            ProtectedMediaSession.StartBudget(new BufferPolicy { InitialPlayback = TimeSpan.FromMilliseconds(initialPlaybackMs) }));

    [Fact]
    public void StartBudget_ForTheVodPolicy_IsFifteenSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(15), ProtectedMediaSession.StartBudget(BufferPolicy.Vod));

    [Theory]
    [InlineData(ProtectedVideoPhase.Buffering, MediaErrorCategory.Network)]   // the store never received media
    [InlineData(ProtectedVideoPhase.Licensing, MediaErrorCategory.Drm)]       // the license never became usable
    [InlineData(ProtectedVideoPhase.Attaching, MediaErrorCategory.Drm)]       // the protected topology never produced a frame
    [InlineData(ProtectedVideoPhase.Idle, MediaErrorCategory.Drm)]
    public void StartFailureCategory_NamesWhereTheSwitchStuck(ProtectedVideoPhase phase, MediaErrorCategory category)
        => Assert.Equal(category, ProtectedMediaSession.StartFailureCategory(phase));

    [Theory]
    [InlineData(ProtectedVideoState.Idle, false, PlaybackState.Opening)]
    [InlineData(ProtectedVideoState.Loading, false, PlaybackState.Opening)]
    [InlineData(ProtectedVideoState.Loading, true, PlaybackState.Opening)]
    [InlineData(ProtectedVideoState.Licensed, true, PlaybackState.Opening)]
    [InlineData(ProtectedVideoState.Buffering, true, PlaybackState.Stalled)]
    [InlineData(ProtectedVideoState.Playing, true, PlaybackState.Playing)]
    [InlineData(ProtectedVideoState.Playing, false, PlaybackState.Playing)]
    [InlineData(ProtectedVideoState.Paused, false, PlaybackState.Paused)]
    [InlineData(ProtectedVideoState.Paused, true, PlaybackState.Buffering)]   // play requested, engine not advancing yet
    [InlineData(ProtectedVideoState.Ended, false, PlaybackState.Ended)]
    [InlineData(ProtectedVideoState.Stopped, false, PlaybackState.Idle)]
    [InlineData(ProtectedVideoState.Error, true, PlaybackState.Failed)]
    public void MapState_Table(ProtectedVideoState state, bool playRequested, PlaybackState expected)
        => Assert.Equal(expected, ProtectedMediaSession.MapState(state, playRequested));

    [Theory]
    // state, attached, firstFrame, metadataOrCanPlay, licenseUsable, licenseExpected, bufferedAheadMs → phase
    [InlineData(ProtectedVideoState.Error, true, true, true, true, true, 5_000L, ProtectedVideoPhase.Failed)]
    [InlineData(ProtectedVideoState.Error, false, false, false, false, false, 0L, ProtectedVideoPhase.Failed)]
    [InlineData(ProtectedVideoState.Loading, false, false, true, true, true, 5_000L, ProtectedVideoPhase.Idle)]
    [InlineData(ProtectedVideoState.Playing, true, true, true, false, true, 0L, ProtectedVideoPhase.Playing)]
    [InlineData(ProtectedVideoState.Paused, true, true, true, true, true, 0L, ProtectedVideoPhase.Presenting)]
    [InlineData(ProtectedVideoState.Buffering, true, true, true, true, true, 0L, ProtectedVideoPhase.Presenting)]
    [InlineData(ProtectedVideoState.Loading, true, false, true, false, true, 5_000L, ProtectedVideoPhase.Licensing)]
    [InlineData(ProtectedVideoState.Licensed, true, false, true, true, true, 0L, ProtectedVideoPhase.Attaching)]
    [InlineData(ProtectedVideoState.Loading, true, false, false, false, false, 1_000L, ProtectedVideoPhase.Attaching)]
    [InlineData(ProtectedVideoState.Loading, true, false, false, true, true, 0L, ProtectedVideoPhase.Buffering)]
    [InlineData(ProtectedVideoState.Loading, true, false, false, false, false, 0L, ProtectedVideoPhase.Buffering)]
    public void DerivePhase_Table(ProtectedVideoState state, bool attached, bool firstFrame, bool metadataOrCanPlay,
        bool licenseUsable, bool licenseExpected, long bufferedAheadMs, ProtectedVideoPhase expected)
        => Assert.Equal(expected, ProtectedVideoSession.DerivePhase(state, attached, firstFrame, metadataOrCanPlay,
            licenseUsable, licenseExpected, bufferedAheadMs));

    // ── ProtectedMediaBackend: prepare → open hand-off ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prepare_StartsTheLicenseFirst_PrefetchesTwoSegments_AndIsReadyFromBufferedMedia()
    {
        var rig = new BackendRig();

        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);

        FakeProtectedVideoPlayer player = Assert.Single(rig.Created);
        Assert.Equal(new[] { "license:" + InitA, "create:" + InitA }, rig.Order);
        Assert.Equal(ContentKid, Assert.Single(rig.Ensured).DefaultKid);
        Assert.Equal(1, player.PrefetchCalls);
        Assert.Equal(ProtectedVideoSession.DefaultPrefetchSegments, player.LastPrefetchSegments);
        Assert.Equal(0, player.StartCalls);   // a prepare never touches the engine
        Assert.True(item.IsReady);
        Assert.Same(player, item.BackendHandle);
        Assert.Equal(TimeSpan.FromSeconds(40), item.Duration);
        Assert.Equal(MediaKind.MfVideoOrFile, item.Kind);
        Assert.Null(item.AudioVoice);
    }

    [Fact]
    public async Task Prepare_WithNothingBuffered_IsNotReady()
    {
        var rig = new BackendRig { ReadyOnPrefetch = false };
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);
        Assert.False(item.IsReady);
    }

    [Fact]
    public async Task Prepare_OfAnErroredPlayer_IsNotReady_EvenWithBufferedMedia()
    {
        var rig = new BackendRig { Configure = p => p.SetState(ProtectedVideoState.Error) };
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);
        Assert.Equal(4000, Assert.Single(rig.Created).ForwardBufferedMs);
        Assert.False(item.IsReady);
    }

    [Fact]
    public async Task Prepare_AwaitsThePrefetch()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new BackendRig { Configure = p => p.PrefetchResult = hold };

        Task<IPreparedItem> pending = rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct).AsTask();
        Assert.False(pending.IsCompleted);

        hold.SetResult();
        IPreparedItem item = await pending.WaitAsync(Bound, Ct);
        _cleanup.Add(item);
        Assert.True(item.IsReady);
    }

    [Fact]
    public async Task Prepare_ACanceledPrefetch_StillReturnsTheItem_NotReady()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new BackendRig { Configure = p => p.PrefetchResult = hold };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<IPreparedItem> pending = rig.Backend.PrepareAsync(SourceFor(InitA), Prep, cts.Token).AsTask();
        cts.Cancel();

        IPreparedItem item = await pending.WaitAsync(Bound, Ct);
        _cleanup.Add(item);
        Assert.False(item.IsReady);
        Assert.Same(Assert.Single(rig.Created), item.BackendHandle);
    }

    [Fact]
    public async Task Open_TakesThePreparedPlayerForTheSameInitUrl_ExactlyOnce()
    {
        var rig = new BackendRig();
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);
        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);

        IMediaSession first = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);
        _cleanup.Add(first);
        Assert.Same(prepared, Assert.IsType<ProtectedMediaSession>(first).Player);
        Assert.Single(rig.Created);
        Assert.Single(rig.Ensured);   // the prepare already started this license

        IMediaSession second = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);
        _cleanup.Add(second);
        Assert.Equal(2, rig.Created.Count);
        Assert.NotSame(prepared, Assert.IsType<ProtectedMediaSession>(second).Player);
        Assert.Equal(new[] { "license:" + InitA, "create:" + InitA, "license:" + InitA, "create:" + InitA }, rig.Order);
    }

    [Fact]
    public async Task Open_ForADifferentInitUrl_CreatesANewPlayer_AndLeavesThePreparedOneForItsOwnOpen()
    {
        var rig = new BackendRig();
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);
        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);

        IMediaSession other = await rig.Backend.OpenAsync(SourceFor(InitB), new MediaOpenOptions(), Ct);
        _cleanup.Add(other);
        Assert.Equal(2, rig.Created.Count);
        Assert.Same(rig.Created[1], Assert.IsType<ProtectedMediaSession>(other).Player);
        Assert.Equal(0, prepared.DisposeCalls);

        IMediaSession mine = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);
        _cleanup.Add(mine);
        Assert.Same(prepared, Assert.IsType<ProtectedMediaSession>(mine).Player);
        Assert.Equal(2, rig.Created.Count);
    }

    [Fact]
    public async Task Open_AfterPrepare_StartsThePreparedPlayerAtTheOpensStartPosition()
    {
        var rig = new BackendRig();
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);

        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA),
            new MediaOpenOptions { StartPosition = TimeSpan.FromSeconds(83), StartPaused = false }, Ct);
        _cleanup.Add(session);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);
        Assert.Equal(TimeSpan.FromSeconds(83), prepared.StartedWith!.StartPosition);
        Assert.False(prepared.StartedWith.StartPaused);
    }

    [Fact]
    public async Task DisposingAConsumedPreparedItem_DoesNotDisposeThePlayer()
    {
        var rig = new BackendRig();
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);
        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);

        await item.DisposeAsync();
        Assert.Equal(0, prepared.DisposeCalls);   // the open owns it now

        await session.DisposeAsync();
        Assert.Equal(1, prepared.StopCalls);
        Assert.Equal(1, prepared.DisposeCalls);
    }

    [Fact]
    public async Task DisposingAnUnconsumedPreparedItem_DisposesThePlayer_AndTheNextOpenIsCold()
    {
        var rig = new BackendRig();
        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        FakeProtectedVideoPlayer prepared = Assert.Single(rig.Created);

        await item.DisposeAsync();
        Assert.Equal(1, prepared.DisposeCalls);

        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);
        _cleanup.Add(session);
        Assert.Equal(2, rig.Created.Count);
        Assert.NotSame(prepared, Assert.IsType<ProtectedMediaSession>(session).Player);
        Assert.Equal(2, rig.Ensured.Count);   // a cold open starts the license itself

        await item.DisposeAsync();
        Assert.Equal(1, prepared.DisposeCalls);   // disposal is claimed exactly once
    }

    [Fact]
    public async Task ASecondPrepareForTheSameInitUrl_SupersedesAndDisposesTheFirst()
    {
        var rig = new BackendRig();
        IPreparedItem first = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(first);
        IPreparedItem second = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(second);

        Assert.Equal(1, rig.Created[0].DisposeCalls);
        Assert.Equal(0, rig.Created[1].DisposeCalls);

        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);
        _cleanup.Add(session);
        Assert.Same(rig.Created[1], Assert.IsType<ProtectedMediaSession>(session).Player);
    }

    [Fact]
    public async Task OpenAndPrepare_RequireADrmConfig()
    {
        var rig = new BackendRig();
        MediaSource clear = MediaSource.FromUri("https://cdn.test/clip.mp4");

        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Backend.OpenAsync(clear, new MediaOpenOptions(), Ct).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Backend.PrepareAsync(clear, Prep, Ct).AsTask());
        Assert.Empty(rig.Created);
        Assert.Empty(rig.Ensured);
    }

    [Fact]
    public async Task TheSourcesDescriptor_WinsOverTheBackendsFallback()
    {
        var rig = new BackendRig(fallback: Descriptor(InitB));

        IMediaSession withOwn = await rig.Backend.OpenAsync(SourceFor(InitA), new MediaOpenOptions(), Ct);
        _cleanup.Add(withOwn);
        IMediaSession withNone = await rig.Backend.OpenAsync(
            MediaSource.FromUri("https://cdn.test/other.mpd").With(new DrmConfig(DrmSystem.PlayReady)), new MediaOpenOptions(), Ct);
        _cleanup.Add(withNone);

        Assert.Equal(InitA, rig.Ensured[0].InitUrl);
        Assert.Equal(InitB, rig.Ensured[1].InitUrl);
    }

    [Fact]
    public async Task TheLicenseRelay_IsTheOpensOwn_ElseTheBackendDefault()
    {
        Func<LicenseRequest, ValueTask<LicenseResponse>> fallbackRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 1 }));
        Func<LicenseRequest, ValueTask<LicenseResponse>> openRelay = _ => ValueTask.FromResult(new LicenseResponse(new byte[] { 2 }));
        var rig = new BackendRig(defaultRelay: fallbackRelay);

        IPreparedItem item = await rig.Backend.PrepareAsync(SourceFor(InitA), Prep, Ct);
        _cleanup.Add(item);
        IMediaSession own = await rig.Backend.OpenAsync(SourceFor(InitB), new MediaOpenOptions { LicenseRelay = openRelay }, Ct);
        _cleanup.Add(own);
        IMediaSession none = await rig.Backend.OpenAsync(SourceFor(InitB), new MediaOpenOptions(), Ct);
        _cleanup.Add(none);

        Assert.Same(fallbackRelay, rig.Ensured[0].LicenseRelay);   // a prepare has no per-open options
        Assert.Same(openRelay, rig.Ensured[1].LicenseRelay);
        Assert.Same(fallbackRelay, rig.Ensured[2].LicenseRelay);
    }

    [Fact]
    public async Task PrepareAt_PrefetchesAroundThePosition_AndTheOpenThereTakesThePreparedPlayer()
    {
        var rig = new BackendRig();

        IPreparedItem item = await rig.Backend.PrepareAtAsync(SourceFor(InitA), TimeSpan.FromSeconds(83), Ct);
        _cleanup.Add(item);
        Assert.True(item.IsReady);
        Assert.Equal(TimeSpan.FromSeconds(83), Assert.Single(rig.Ensured).StartPosition);   // the prefetch is AT the song's position

        IMediaSession session = await rig.Backend.OpenAsync(SourceFor(InitA),
            new MediaOpenOptions { StartPosition = TimeSpan.FromSeconds(84) }, Ct);
        _cleanup.Add(session);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));

        FakeProtectedVideoPlayer player = Assert.Single(rig.Created);                       // warm: no second player
        Assert.Equal(TimeSpan.FromSeconds(84), player.StartedWith!.StartPosition);          // the open's own position wins
    }

    [Fact]
    public async Task PrepareAt_ANegativePosition_PreparesFromTheStart()
    {
        var rig = new BackendRig();
        IPreparedItem item = await rig.Backend.PrepareAtAsync(SourceFor(InitA), TimeSpan.FromSeconds(-1), Ct);
        _cleanup.Add(item);
        Assert.Equal(TimeSpan.Zero, Assert.Single(rig.Ensured).StartPosition);
    }

    // ── buffering detail (the clear session's rule) ──────────────────────────────────────────────────────────────────

    [Theory]
    // state, seeking, framePresented → reason
    [InlineData(PlaybackState.Opening, false, false, BufferingReason.Initial)]
    [InlineData(PlaybackState.Buffering, false, false, BufferingReason.Initial)]
    [InlineData(PlaybackState.Buffering, false, true, BufferingReason.Rebuffering)]
    [InlineData(PlaybackState.Stalled, false, true, BufferingReason.Rebuffering)]
    [InlineData(PlaybackState.Buffering, true, true, BufferingReason.Seeking)]
    [InlineData(PlaybackState.Opening, true, false, BufferingReason.Seeking)]
    [InlineData(PlaybackState.Playing, false, true, BufferingReason.None)]
    [InlineData(PlaybackState.Paused, false, true, BufferingReason.None)]
    [InlineData(PlaybackState.Ready, false, false, BufferingReason.None)]
    [InlineData(PlaybackState.Failed, false, false, BufferingReason.None)]
    public void BufferingFor_OpeningBufferingAndStalledAreBuffering_WithTheReasonTheFactsGive(
        PlaybackState state, bool seeking, bool framePresented, BufferingReason reason)
        => Assert.Equal(reason, ProtectedMediaSession.BufferingFor(state, seeking, framePresented, 0, BufferPolicy.Vod).Reason);

    [Fact]
    public void BufferingFor_ProgressIsTheStoreAgainstTheTargetForThatReason()
    {
        BufferPolicy policy = new() { InitialPlayback = TimeSpan.FromSeconds(2), ResumePlayback = TimeSpan.FromSeconds(4) };

        BufferingInfo initial = ProtectedMediaSession.BufferingFor(PlaybackState.Opening, false, false, 1_000, policy);
        Assert.Equal(0.5, initial.Percent, 3);
        Assert.Equal(TimeSpan.FromSeconds(2), initial.TargetAhead);
        Assert.False(initial.CanResume);

        BufferingInfo rebuffer = ProtectedMediaSession.BufferingFor(PlaybackState.Stalled, false, true, 6_000, policy);
        Assert.Equal(1.0, rebuffer.Percent, 3);
        Assert.Equal(TimeSpan.FromSeconds(6), rebuffer.BufferedAhead);
        Assert.True(rebuffer.CanResume);
    }

    [Fact]
    public void OpeningWithNoFrame_PublishesAnInitialReason_NotNone()
    {
        var (session, core, player) = NewSession(startPaused: false);
        player.SetState(ProtectedVideoState.Loading);

        session.PumpVideo(default, Rect, 1f);

        Assert.Equal(PlaybackState.Opening, core.State.Peek());
        Assert.Equal(BufferingReason.Initial, core.Buffering.Peek().Reason);
    }
}
