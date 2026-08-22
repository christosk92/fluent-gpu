using System;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Controls.Media;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The ENGINE-reported live path: a live URL (an HLS master playlist) handed straight to Media Foundation, with no
/// parsed manifest — so live-ness, the DVR window and the live edge come from the engine rather than from a manifest.
/// Two layers are pinned here: <see cref="LiveSessionRules"/> (the pure thresholds/arithmetic) and
/// <see cref="MfMediaSession"/> driven through a <see cref="FakeVideoEngine"/> (no D3D/MF/DComp device), which is the
/// only way to observe the latch/publish behavior. The manifest-driven live path is covered by
/// <see cref="MfMediaSessionTests"/> and must stay unchanged by any of this.
/// </summary>
public sealed class LiveSessionTests
{
    private static readonly RectF Rect = new(0, 0, 320, 180);

    private static (MfMediaSession session, MediaPlayerCore core, FakeVideoEngine engine) NewSession(
        bool startPaused = true, SourceLiveness liveness = SourceLiveness.Auto)
    {
        var core = new MediaPlayerCore();
        var engine = new FakeVideoEngine();
        var session = new MfMediaSession(engine, new MediaOpenOptions { StartPaused = startPaused, Liveness = liveness });
        session.ConnectSignals(new MediaSignalSink(core));
        return (session, core, engine);
    }

    private static void Pump(MfMediaSession s) => s.PumpVideo(default, Rect, 1f);

    private static VideoBinding NewBinding(out VideoSurfaceRegistry registry)
    {
        registry = new VideoSurfaceRegistry();
        int token = registry.Acquire();
        return new VideoBinding(registry, token);   // internal ctor (Engine InternalsVisibleTo the test assembly)
    }

    /// <summary>Bring a session to "metadata loaded, engine says live, this is the window" deterministically:
    /// RaiseStateChanged is the same off-thread refresh the real engine's event callback performs, so the caches the
    /// pump reads are primed without waiting on the 100 ms poll timer.</summary>
    private static void GoLiveState(MfMediaSession s, FakeVideoEngine e, double start, double end, double position = 0)
    {
        e.MetadataLoaded = true;
        e.IsLiveSource = true;
        e.SeekableRange = (start, end);
        e.CurrentTimeSeconds = position;
        e.RaiseStateChanged();
        Pump(s);
    }

    // ── LiveSessionRules: the pure decisions ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timeline_PublishesWindowEndAsLiveEdge_AndDistanceBehindIt()
    {
        TimelineInfo t = LiveSessionRules.Timeline(60, 180, 175);

        Assert.True(t.IsLive);
        Assert.Equal(TimeSpan.FromSeconds(60), t.SeekableStart);
        Assert.Equal(TimeSpan.FromSeconds(180), t.SeekableEnd);
        Assert.Equal(TimeSpan.FromSeconds(180), t.LiveEdge);
        Assert.Equal(TimeSpan.FromSeconds(5), t.LiveOffset);
        Assert.True(t.IsAtLiveEdge);                       // 5 s behind is inside the 6 s standard-latency tolerance
        Assert.Empty(t.Chapters);
    }

    [Fact]
    public void Timeline_PastTolerance_IsNotAtLiveEdge()
    {
        TimelineInfo t = LiveSessionRules.Timeline(0, 180, 100);

        Assert.Equal(TimeSpan.FromSeconds(80), t.LiveOffset);
        Assert.False(t.IsAtLiveEdge);
    }

    [Fact]
    public void Timeline_UnansweredWindow_StaysLiveWithZeroWindow()
    {
        // (0,0) is "the engine has not answered yet" — it must NOT downgrade the live-ness the engine already
        // confirmed, or the LIVE chip would flicker off for a pump.
        TimelineInfo t = LiveSessionRules.Timeline(0, 0, 0);

        Assert.True(t.IsLive);
        Assert.Equal(TimeSpan.Zero, t.SeekableEnd);
        Assert.Equal(TimeSpan.Zero, t.DvrWindow);
        Assert.True(t.IsAtLiveEdge);
    }

    [Fact]
    public void Timeline_NonFiniteInputs_DegradeToZero()
    {
        TimelineInfo t = LiveSessionRules.Timeline(double.NaN, double.PositiveInfinity, double.NaN);

        Assert.True(t.IsLive);
        Assert.Equal(TimeSpan.Zero, t.SeekableStart);
        Assert.Equal(TimeSpan.Zero, t.SeekableEnd);
    }

    [Fact]
    public void LiveCommands_AlwaysOffersGoLive_AndDropsSeekOnANarrowWindow()
    {
        MediaCommandFlags core = MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate;

        MediaCommandFlags narrow = LiveSessionRules.LiveCommands(core, 114, 120);   // a 6 s true-live window
        Assert.True(narrow.HasFlag(MediaCommandFlags.GoLive));
        Assert.False(narrow.HasFlag(MediaCommandFlags.Seek));
        Assert.True(narrow.HasFlag(MediaCommandFlags.Play));

        MediaCommandFlags dvr = LiveSessionRules.LiveCommands(core, 0, 120);        // a real DVR window
        Assert.True(dvr.HasFlag(MediaCommandFlags.GoLive));
        Assert.True(dvr.HasFlag(MediaCommandFlags.Seek));
    }

    [Fact]
    public void GoLiveTarget_LandsOneSegmentBehindTheEdge_ClampedToTheWindow()
    {
        Assert.Equal(117, LiveSessionRules.GoLiveTarget(0, 120));
        // The backoff never crosses the window's start (a 2 s window: land on the start, not before it).
        Assert.Equal(118, LiveSessionRules.GoLiveTarget(118, 120));
        // No window answered yet: 0 means "do not seek at all" — a seek to 0 on a live source jumps to the OLDEST
        // retained bytes, the exact opposite of going live.
        Assert.Equal(0, LiveSessionRules.GoLiveTarget(0, 0));
    }

    // ── MfMediaSession: latch, publish, transport ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EngineLiveSource_PublishesLiveTimeline_AndKeepsDurationZero()
    {
        var (s, core, e) = NewSession();
        GoLiveState(s, e, 0, 120, position: 30);

        TimelineInfo t = core.Timeline.Peek();
        Assert.True(t.IsLive);
        Assert.Equal(TimeSpan.FromSeconds(120), t.LiveEdge);
        Assert.Equal(TimeSpan.FromSeconds(90), t.LiveOffset);
        Assert.False(t.IsAtLiveEdge);
        // An unbounded source HAS no length: duration stays zero so the seek bar stays hidden.
        Assert.Equal(TimeSpan.Zero, core.Duration.Peek());
    }

    [Fact]
    public void EngineLiveSource_PublishesGoLive_AndDropsSeekWhileTheWindowIsNarrow()
    {
        var (s, core, e) = NewSession();
        GoLiveState(s, e, 114, 120);

        MediaCommandFlags commands = core.Commands.Available.Peek();
        Assert.True(commands.HasFlag(MediaCommandFlags.GoLive));
        Assert.False(commands.HasFlag(MediaCommandFlags.Seek));

        // The window widens as the source retains more: Seek comes back without any other event.
        e.SeekableRange = (0, 200);
        e.RaiseStateChanged();
        Pump(s);
        commands = core.Commands.Available.Peek();
        Assert.True(commands.HasFlag(MediaCommandFlags.Seek));
        Assert.True(commands.HasFlag(MediaCommandFlags.GoLive));
    }

    [Fact]
    public void EngineLiveness_IsLatched_NeverRegressedByAnUnansweredRead()
    {
        var (s, core, e) = NewSession();
        GoLiveState(s, e, 0, 120);
        Assert.True(core.Timeline.Peek().IsLive);

        // A bounded read that expired answers false/(0,0). That is "not answered", never "this became VOD".
        e.IsLiveSource = false;
        e.SeekableRange = (0, 0);
        e.RaiseStateChanged();
        Pump(s);

        TimelineInfo t = core.Timeline.Peek();
        Assert.True(t.IsLive);
        Assert.Equal(TimeSpan.FromSeconds(120), t.LiveEdge);   // the last answered window is kept, not zeroed
    }

    [Fact]
    public void NonLiveSource_PublishesNoLiveTimeline()
    {
        var (s, core, e) = NewSession();
        e.MetadataLoaded = true;
        e.DurationSeconds = 60;
        e.RaiseStateChanged();
        Pump(s);

        Assert.False(core.Timeline.Peek().IsLive);
        Assert.False(core.Commands.Available.Peek().HasFlag(MediaCommandFlags.GoLive));
        Assert.Equal(TimeSpan.FromSeconds(60), core.Duration.Peek());
    }

    [Fact]
    public async Task GoLiveAsync_SeeksOneSegmentBehindTheEdge_Exactly()
    {
        var (s, _, e) = NewSession();
        GoLiveState(s, e, 0, 120);

        await s.GoLiveAsync();

        Assert.Equal(117, e.LastSeek);
        Assert.False(e.LastSeekApproximate);   // landing behind the edge is the failure being fixed
    }

    [Fact]
    public async Task GoLiveAsync_OnAVodSource_DoesNotSeek()
    {
        var (s, _, e) = NewSession();
        e.MetadataLoaded = true;
        e.DurationSeconds = 60;
        e.RaiseStateChanged();
        Pump(s);

        await s.GoLiveAsync();

        Assert.True(double.IsNaN(e.LastSeek));
    }

    [Fact]
    public async Task ResumeAfterPause_OnALiveSource_JumpsToTheLiveEdgeFirst()
    {
        var (s, _, e) = NewSession();
        GoLiveState(s, e, 0, 120);

        // The FIRST play is not a resume: it must honor the open options, not jump.
        await s.PlayAsync();
        Assert.True(double.IsNaN(e.LastSeek));

        await s.PauseAsync();
        e.SeekableRange = (0, 300);        // the window slid on while paused
        e.RaiseStateChanged();
        await s.PlayAsync();

        Assert.Equal(297, e.LastSeek);
        Assert.Equal(2, e.PlayCalls);
    }

    [Fact]
    public async Task ResumeAfterPause_OnAVodSource_DoesNotSeek()
    {
        var (s, _, e) = NewSession();
        e.MetadataLoaded = true;
        e.DurationSeconds = 60;
        e.RaiseStateChanged();
        Pump(s);

        await s.PlayAsync();
        await s.PauseAsync();
        await s.PlayAsync();

        Assert.True(double.IsNaN(e.LastSeek));
    }

    // ── error surfacing ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1u, MediaErrorKind.Aborted, MediaErrorCategory.Source)]
    [InlineData(2u, MediaErrorKind.Network, MediaErrorCategory.Network)]
    [InlineData(3u, MediaErrorKind.Decode, MediaErrorCategory.Decode)]
    [InlineData(4u, MediaErrorKind.SourceNotSupported, MediaErrorCategory.UnsupportedCodec)]
    [InlineData(5u, MediaErrorKind.Encrypted, MediaErrorCategory.Drm)]
    [InlineData(99u, MediaErrorKind.Unknown, MediaErrorCategory.Source)]
    public void MapError_CarriesThePlatformKindAndHrVerbatim(uint mfErr, MediaErrorKind kind, MediaErrorCategory category)
    {
        const int Hr = unchecked((int)0x80072EE7);

        MediaError error = MfMediaSession.MapError(mfErr, Hr);

        Assert.Equal(kind, error.Kind);
        Assert.Equal(category, error.Category);
        Assert.Equal(Hr, error.UnderlyingCode);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void SessionFault_PublishesTheKindAndHrOnTheErrorSignal()
    {
        var (s, core, e) = NewSession();
        e.HasError = true;
        e.ErrorCode = 2;                                  // MF_MEDIA_ENGINE_ERR_NETWORK
        e.ErrorHr = unchecked((int)0x80072EFD);
        Pump(s);

        MediaError? error = core.Error.Peek();
        Assert.NotNull(error);
        Assert.Equal(MediaErrorKind.Network, error!.Kind);
        Assert.Equal(unchecked((int)0x80072EFD), error.UnderlyingCode);
        Assert.Equal(PlaybackState.Failed, core.State.Peek());
    }

    // ── SourceLiveness: the CALLER's declaration outranks MF's inference ─────────────────────────────────────────────

    [Fact]
    public void ForcedLive_NeverPublishesAFiniteDuration_EvenWhenTheEngineReportsOne()
    {
        // The exact YouTube-live failure: MF reports the sliding DVR window as a FINITE GetDuration (202 s here), the
        // session latched it, and the bar rendered "0:03 / -3:19" with a seek rail mapped onto a length that does not
        // exist. A declared-live source must never publish it — not at first metadata, and not from the LATE DURATION
        // re-read either.
        var (s, core, e) = NewSession(liveness: SourceLiveness.Live);
        e.MetadataLoaded = true;
        e.DurationSeconds = 202;
        e.IsLiveSource = false;              // MF has not answered its own live probe yet — irrelevant, we were TOLD
        e.SeekableRange = (0, 202);
        e.RaiseStateChanged();
        Pump(s);

        Assert.Equal(TimeSpan.Zero, core.Duration.Peek());

        // …and it stays zero as the engine keeps reporting the window as a duration.
        e.DurationSeconds = 240;
        e.SeekableRange = (0, 240);
        e.RaiseStateChanged();
        Pump(s);
        Assert.Equal(TimeSpan.Zero, core.Duration.Peek());
    }

    [Fact]
    public void ForcedLive_PublishesTheLiveTimelineAndCommandsFromFirstMetadata()
    {
        // No probe, no wait: live from construction, so the FIRST metadata pump already carries the live timeline and
        // the live command bitset. This is the window in which a finite duration would otherwise have been published.
        var (s, core, e) = NewSession(liveness: SourceLiveness.Live);
        e.MetadataLoaded = true;
        e.DurationSeconds = 202;
        e.SeekableRange = (0, 200);
        e.CurrentTimeSeconds = 100;
        e.RaiseStateChanged();
        Pump(s);

        TimelineInfo t = core.Timeline.Peek();
        Assert.True(t.IsLive);
        Assert.Equal(TimeSpan.FromSeconds(200), t.LiveEdge);
        Assert.True(t.HasDvrWindow);                       // 200 s ≥ the 30 s DVR threshold
        MediaCommandFlags commands = core.Commands.Available.Peek();
        Assert.True(commands.HasFlag(MediaCommandFlags.GoLive));
        Assert.True(commands.HasFlag(MediaCommandFlags.Seek));
    }

    [Fact]
    public async Task ForcedLive_GoesLiveWithoutAnyEngineProbe()
    {
        var (s, _, e) = NewSession(liveness: SourceLiveness.Live);
        e.MetadataLoaded = true;
        e.IsLiveSource = false;
        e.SeekableRange = (0, 120);
        e.RaiseStateChanged();
        Pump(s);

        await s.GoLiveAsync();

        Assert.Equal(117, e.LastSeek);
    }

    [Fact]
    public void ForcedVod_IgnoresIsLiveSource_AndKeepsItsDuration()
    {
        // The mirror image: a host that KNOWS it resolved a recording must not have a bounded/optimistic platform
        // probe latch live-ness onto it (the latch is one-way and never clears).
        var (s, core, e) = NewSession(liveness: SourceLiveness.Vod);
        e.MetadataLoaded = true;
        e.DurationSeconds = 202;
        e.IsLiveSource = true;
        e.SeekableRange = (0, 202);
        e.RaiseStateChanged();
        Pump(s);

        Assert.False(core.Timeline.Peek().IsLive);
        Assert.False(core.Commands.Available.Peek().HasFlag(MediaCommandFlags.GoLive));
        Assert.Equal(TimeSpan.FromSeconds(202), core.Duration.Peek());
    }

    [Fact]
    public async Task ForcedVod_DoesNotGoLive()
    {
        var (s, _, e) = NewSession(liveness: SourceLiveness.Vod);
        e.MetadataLoaded = true;
        e.IsLiveSource = true;
        e.SeekableRange = (0, 202);
        e.RaiseStateChanged();
        Pump(s);

        await s.GoLiveAsync();

        Assert.True(double.IsNaN(e.LastSeek));
    }

    [Fact]
    public void Auto_IsUnchanged_StillInfersLiveFromTheEngine()
    {
        var (s, core, e) = NewSession();               // Auto is the default
        GoLiveState(s, e, 0, 120);
        Assert.True(core.Timeline.Peek().IsLive);
    }

    // ── Part 11: the presentation geometry (content size == natural size; DComp does the fit) ────────────────────────

    [Fact]
    public void PumpVideo_SizesTheStreamAndTheContentToTheNaturalFrameSize_NotTheDestination()
    {
        // The aspect-ratio bug: the DESTINATION size used to go to both SetVideoStreamRect and SetContentSize, which
        // made DirectComposition an identity map and moved the fit inside MF (UpdateVideoStream letterboxes into the
        // rect with a black border) — so every MediaStretch looked identical. The frame now renders 1:1 at its natural
        // size and DComp scales it into the placed rect, exactly as the protected/PlayReady path does.
        var (s, _, e) = NewSession();
        var binding = NewBinding(out VideoSurfaceRegistry registry);
        e.MetadataLoaded = true; e.NativeW = 640; e.NativeH = 360; e.Handle = 0xF00D;

        // A 16:9 frame placed into a 4:3 rect (what Fill/UniformToFill produce): the destination is NOT the frame.
        s.PumpVideo(binding, new RectF(0, 0, 800, 600), 1f);

        Assert.Equal(640, e.StreamW);
        Assert.Equal(360, e.StreamH);

        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal(640u, presenter.LastContentW);
        Assert.Equal(360u, presenter.LastContentH);
        Assert.Equal(new RectF(0, 0, 800, 600), presenter.LastPlaceRect);   // DComp scales 640×360 → 800×600
    }

    [Theory]
    // Uniform: a 16:9 frame letterboxed inside a 800×600 stage → 800×450, centered.
    [InlineData(MediaStretch.Uniform, 0f, 75f, 800f, 450f)]
    // Fill: the whole stage, distorted.
    [InlineData(MediaStretch.Fill, 0f, 0f, 800f, 600f)]
    // UniformToFill: covers the stage and OVERFLOWS horizontally (clipped by the viewport).
    [InlineData(MediaStretch.UniformToFill, -133.333f, 0f, 1066.667f, 600f)]
    public void PumpVideo_PlacesTheFittedRectPerStretch_WithTheContentSizeUnchanged(
        MediaStretch stretch, float x, float y, float w, float h)
    {
        var area = new RectF(0, 0, 800, 600);
        var natural = new SizeI(640, 360);   // smaller than the stage, so the oversize cap is inert here
        RectF fitted = MediaPlayerElement.FitVideoRect(area, natural, stretch);
        Assert.Equal(x, fitted.X, 2);
        Assert.Equal(y, fitted.Y, 2);
        Assert.Equal(w, fitted.W, 2);
        Assert.Equal(h, fitted.H, 2);

        var (s, _, e) = NewSession();
        var binding = NewBinding(out VideoSurfaceRegistry registry);
        e.MetadataLoaded = true; e.NativeW = 640; e.NativeH = 360; e.Handle = 0xF00D;
        binding.SetViewport(area);
        s.PumpVideo(binding, fitted, 1f);

        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        // The placement IS the fitted rect (the element's geometry survives verbatim) and the content stays the frame,
        // so the compositor's scale is exactly fitted/natural per axis — which is what makes the modes differ at all.
        Assert.Equal(fitted.X, presenter.LastPlaceRect.X, 2);
        Assert.Equal(fitted.Y, presenter.LastPlaceRect.Y, 2);
        Assert.Equal(fitted.W, presenter.LastPlaceRect.W, 2);
        Assert.Equal(fitted.H, presenter.LastPlaceRect.H, 2);
        Assert.Equal(640u, presenter.LastContentW);
        Assert.Equal(360u, presenter.LastContentH);
        // The crop mode's overflow is clipped by the viewport, not by shrinking the frame.
        Assert.Equal(area.W, presenter.LastViewport.W, 2);
    }

    [Fact]
    public void ContentSizeFor_CapsAnOversizedFrameAtTheDestination_PreservingTheRatio()
    {
        // A 4K frame in a 640×360 (DIP) card at scale 1: nobody can see 4K, so the buffers are capped — but by ONE
        // factor on both axes, because the ratio is the input to the fit.
        SizeI capped = MfMediaSession.ContentSizeFor(new SizeI(3840, 2160), new RectF(0, 0, 640, 360), 1f);
        Assert.Equal(640, capped.Width);
        Assert.Equal(360, capped.Height);
        Assert.Equal(3840.0 / 2160.0, (double)capped.Width / capped.Height, 3);

        // The cap follows the MOST magnified axis, so a crop destination never samples a downscaled buffer back up.
        SizeI crop = MfMediaSession.ContentSizeFor(new SizeI(3840, 2160), new RectF(0, 0, 640, 640), 1f);
        Assert.Equal(1138, crop.Width);        // 640/2160 would starve the width; 640/3840 vs 640/2160 → the latter
        Assert.Equal(640, crop.Height);

        // Never UPSCALE the buffer: a small frame in a big rect stays its natural size and DComp scales it.
        Assert.Equal(new SizeI(320, 180), MfMediaSession.ContentSizeFor(new SizeI(320, 180), new RectF(0, 0, 1280, 720), 1f));

        // Device scale counts (a 2× display asks for twice the pixels).
        Assert.Equal(new SizeI(1280, 720), MfMediaSession.ContentSizeFor(new SizeI(1920, 1080), new RectF(0, 0, 640, 360), 2f));

        // No natural size yet ⇒ the destination (the pre-answer fallback, so the surface still presents something).
        Assert.Equal(new SizeI(640, 360), MfMediaSession.ContentSizeFor(SizeI.Zero, new RectF(0, 0, 640, 360), 1f));
    }

    [Fact]
    public void FormatChange_ReQueriesTheNaturalSize_AndRepublishesItOnChange()
    {
        // An ABR variant switch changes the decoded frame size with NO transport transition. Without the presentation
        // epoch nothing re-asks, and the surface composites at the first variant's geometry for the rest of the
        // session (the fit silently wrong from then on).
        var (s, core, e) = NewSession();
        var binding = NewBinding(out _);
        e.MetadataLoaded = true; e.NativeW = 640; e.NativeH = 360; e.Handle = 0xF00D;
        s.PumpVideo(binding, new RectF(0, 0, 1280, 720), 1f);
        Assert.Equal(new SizeI(640, 360), core.NaturalSize.Peek());
        int queriesBefore = e.NativeSizeQueries;

        // A pump with nothing changed must NOT re-ask (the query is a marshaled round-trip onto the engine thread).
        s.PumpVideo(binding, new RectF(0, 0, 1280, 720), 1f);
        Assert.Equal(queriesBefore, e.NativeSizeQueries);

        e.NativeW = 1920; e.NativeH = 1080;
        e.RaiseFormatChange();
        s.PumpVideo(binding, new RectF(0, 0, 1280, 720), 1f);

        Assert.True(e.NativeSizeQueries > queriesBefore);
        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());
        Assert.Equal(1280, e.StreamW);      // re-sized from the NEW natural size, capped at the 1280×720 destination
        Assert.Equal(720, e.StreamH);
    }

    [Fact]
    public void FormatChange_WithTheSameFrameSize_PublishesNothingNew()
    {
        // Most variant switches keep the frame size. Re-publishing an unchanged natural size would churn the
        // element's fit for nothing.
        var (s, core, e) = NewSession();
        var binding = NewBinding(out _);
        e.MetadataLoaded = true; e.NativeW = 1280; e.NativeH = 720; e.Handle = 0xF00D;
        s.PumpVideo(binding, new RectF(0, 0, 1280, 720), 1f);
        int repaints = e.RepaintCalls;

        e.RaiseFormatChange();
        s.PumpVideo(binding, new RectF(0, 0, 1280, 720), 1f);

        Assert.Equal(new SizeI(1280, 720), core.NaturalSize.Peek());
        // One repaint IS expected (the resource may have been rebuilt — that is what RESOURCELOST means), but the
        // size never changed, so the stream size did not move either.
        Assert.Equal(1280, e.StreamW);
        Assert.True(e.RepaintCalls >= repaints);
    }
}
