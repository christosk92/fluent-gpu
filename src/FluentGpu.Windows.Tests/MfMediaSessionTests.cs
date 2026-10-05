using System;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Media.Adaptive;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// Tests for <see cref="MfMediaSession"/> v2 (snapshot out, commands in — <c>docs/plans/video-smooth-switching-implementation.md</c>
/// §1.4): the engine-snapshot → <see cref="MediaSignalSink"/> mapping, the composited-surface handoff, typed error
/// mapping, idempotent posted transport and clean (non-blocking) disposal — all driven through a <see cref="FakeVideoEngine"/>
/// so no D3D/MF/DComp device is created. Deterministic (no timers/sleeps); every async assert has a hard 5s timeout.
/// </summary>
public sealed class MfMediaSessionTests
{
    private static readonly RectF Rect = new(0, 0, 320, 180);

    private static (MfMediaSession session, MediaPlayerCore core, FakeVideoEngine engine) NewSession(bool startPaused = true)
    {
        var core = new MediaPlayerCore();
        var sink = new MediaSignalSink(core);
        var engine = new FakeVideoEngine();
        // Epoch 0 matches the fake's default (never-set) Snapshot.SourceEpoch, so PumpVideo's stale-epoch guard passes
        // trivially for every test that is not specifically exercising it (see StaleEpochSnapshot_IsIgnoredByPumpVideo).
        var session = new MfMediaSession(engine, 0, new MediaOpenOptions { StartPaused = startPaused });
        session.ConnectSignals(sink);
        return (session, core, engine);
    }

    private static VideoBinding NewBinding(out VideoSurfaceRegistry registry)
    {
        registry = new VideoSurfaceRegistry();
        int token = registry.Acquire();
        return new VideoBinding(registry, token);   // internal ctor (Engine InternalsVisibleTo the test assembly)
    }

    private static void Pump(MfMediaSession s) => s.PumpVideo(default, Rect, 1f);

    // ── state machine ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConnectSignals_AnnouncesOpening()
    {
        var (_, core, _) = NewSession();
        Assert.Equal(PlaybackState.Opening, core.State.Peek());
    }

    [Fact]
    public void AdaptiveManifest_PublishesTracksQualitiesLiveWindowAndHdrBeforeMetadata()
    {
        var video = new QualityVariant("v1080", 4_000_000, new SizeI(1920, 1080), 60,
            new MediaContentType(Container.Dash, CodecId.Hevc, CodecId.None), HdrFormat.Hdr10);
        var audio = new QualityVariant("a-en", 128_000, SizeI.Zero, 0,
            new MediaContentType(Container.Dash, CodecId.None, CodecId.Aac));
        AdaptiveSegment seg = new(new Uri("https://fixture.test/1.m4s"), 1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        var manifest = new AdaptiveManifest(new Uri("https://fixture.test/live.mpd"), AdaptiveManifestKind.Dash,
            true, true, null, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2), null,
            [
                new AdaptiveTrackGroup("video", AdaptiveTrackType.Video, null, TrackRole.Main,
                    [new AdaptiveRepresentation(video, null, [seg])], true),
                new AdaptiveTrackGroup("audio-en", AdaptiveTrackType.Audio, "en", TrackRole.Main,
                    [new AdaptiveRepresentation(audio, null, [seg])], true),
            ]);
        var core = new MediaPlayerCore();
        var session = new MfMediaSession(new FakeVideoEngine(), 0, new MediaOpenOptions(), manifest);

        session.ConnectSignals(new MediaSignalSink(core));

        Assert.Single(core.Tracks.Video);
        Assert.Single(core.Tracks.Audio);
        Assert.Single(core.Qualities.Variants);
        Assert.True(core.Timeline.Peek().IsLive);
        Assert.Equal(TimeSpan.FromSeconds(12), core.Timeline.Peek().LiveEdge);
        Assert.Equal(HdrFormat.Hdr10, core.VideoColor.Peek().Hdr);
        Assert.Equal(new SizeI(1920, 1080), core.VideoGeometry.Peek().DisplaySize);
    }

    [Fact]
    public void AdaptiveTextTracks_DefaultUnselected_CaptionsOff()
    {
        var subs = new QualityVariant("sub-en", 0, SizeI.Zero, 0,
            new MediaContentType(Container.Dash, CodecId.None, CodecId.None));
        var manifest = new AdaptiveManifest(new Uri("https://fixture.test/master.m3u8"), AdaptiveManifestKind.Hls,
            false, false, TimeSpan.FromMinutes(30), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null,
            [
                new AdaptiveTrackGroup("subs-en", AdaptiveTrackType.Text, "en", TrackRole.Subtitles,
                    [new AdaptiveRepresentation(subs, null, Array.Empty<AdaptiveSegment>(), "https://fixture.test/subs.m3u8")],
                    IsDefault: true),
            ]);
        var core = new MediaPlayerCore();
        var session = new MfMediaSession(new FakeVideoEngine(), 0, new MediaOpenOptions(), manifest);

        session.ConnectSignals(new MediaSignalSink(core));

        // Captions must default OFF even when the manifest marks the rendition DEFAULT (only FORCED auto-selects).
        Assert.Single(core.Tracks.Text);
        Assert.Null(core.Tracks.SelectedText.Peek());
    }

    [Fact]
    public void SeekInFlight_PublishesSeekingBuffering_UntilSeeked()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 60; Pump(s);
        _ = s.PlayAsync(); eng.Playing = true; Pump(s);

        _ = s.SeekAsync(TimeSpan.FromSeconds(30), SeekMode.Accurate);
        eng.Seeking = true;                 // MF keeps Playing=true across an in-flight seek
        Pump(s);
        Assert.Equal(PlaybackState.Buffering, core.State.Peek());
        Assert.Equal(BufferingReason.Seeking, core.Buffering.Peek().Reason);

        eng.Seeking = false;
        Pump(s);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.False(core.Buffering.Peek().IsBuffering);
    }

    [Fact]
    public void WaitingWhilePlaying_PublishesRebufferingBuffering_UntilTheStallEnds()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 60; Pump(s);
        _ = s.PlayAsync(); eng.Playing = true; Pump(s);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());

        eng.Waiting = true;                 // MF WAITING: Playing stays true across a starvation
        Pump(s);
        Assert.Equal(PlaybackState.Buffering, core.State.Peek());
        Assert.True(core.Buffering.Peek().IsBuffering);
        Assert.Equal(BufferingReason.Rebuffering, core.Buffering.Peek().Reason);

        eng.Waiting = false;                // PLAYING / CANPLAY / playhead progress cleared it
        Pump(s);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.False(core.Buffering.Peek().IsBuffering);
    }

    [Fact]
    public void WaitingWhilePlaying_StopsProjectingThePositionForward()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 60; Pump(s);
        _ = s.PlayAsync(); eng.Playing = true; eng.CurrentTimeSeconds = 10; Pump(s);

        eng.Waiting = true;                 // republishes the snapshot (a fresh PositionTimestamp) with position 10
        System.Threading.Thread.Sleep(80);  // a stall long enough that a projected clock would visibly run ahead
        Pump(s);
        Assert.Equal(10.0, core.Position.Peek().TotalSeconds, 3);   // frozen with the frame

        eng.Waiting = false;
        System.Threading.Thread.Sleep(80);
        Pump(s);
        Assert.True(core.Position.Peek().TotalSeconds > 10.04);     // projection resumes with the playback
    }

    [Fact]
    public void Metadata_PublishesSizeDurationCommands_AndBecomesReady()
    {
        var (s, core, eng) = NewSession(startPaused: true);
        eng.MetadataLoaded = true;
        eng.DurationSeconds = 10.0;
        eng.NativeW = 1920; eng.NativeH = 1080;

        Pump(s);

        Assert.Equal(PlaybackState.Ready, core.State.Peek());
        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());
        Assert.Equal(10.0, core.Duration.Peek().TotalSeconds, 6);
        Assert.True((core.Commands.Available.Value & MediaCommandFlags.StepFrame) != 0);   // video ⇒ step-frame offered
    }

    [Fact]
    public void NaturalSize_NotYetKnownAtMetadata_IsRetriedUntilTheEngineAnswers()
    {
        // v1's bounded-Invoke "NoAnswer" tri-state is gone (the engine thread now answers directly, synchronously),
        // but the same shape of race still exists for one refresh cycle: GetNativeVideoSize can be asked before it has
        // an answer. NaturalSizeKnown=false models that; latching it as audio-only left a playing video under an
        // eternal "Starting playback…" — MediaPlayerElement treats an empty NaturalSize as audio-only and never
        // punches a video hole.
        var (s, core, eng) = NewSession(startPaused: true);
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.DurationSeconds = 10.0;
        eng.NativeW = 1920; eng.NativeH = 1080;
        eng.NaturalSizeKnown = false;   // the engine has not answered yet

        s.PumpVideo(binding, Rect, 1f);
        Assert.True(core.NaturalSize.Peek().IsEmpty);                                    // nothing learned yet…
        Assert.Equal(0, (int)(core.Commands.Available.Value & MediaCommandFlags.StepFrame));

        eng.NaturalSizeKnown = true;    // …the engine answers
        s.PumpVideo(binding, Rect, 1f);

        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());
        Assert.True((core.Commands.Available.Value & MediaCommandFlags.StepFrame) != 0);
    }

    [Fact]
    public void NaturalSize_AnsweredAudioOnly_IsNotRetried()
    {
        // The other half: an ANSWERED "no video" (0×0, NaturalSizeKnown=true) is authoritative and must never be
        // mistaken for "not known yet".
        var (s, core, eng) = NewSession(startPaused: true);
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.DurationSeconds = 10.0;
        eng.NativeW = 0; eng.NativeH = 0; eng.NaturalSizeKnown = true;

        s.PumpVideo(binding, Rect, 1f);
        s.PumpVideo(binding, Rect, 1f);
        s.PumpVideo(binding, Rect, 1f);

        Assert.True(core.NaturalSize.Peek().IsEmpty);
    }

    [Fact]
    public void PlayThenPause_WalksPlayingToPaused()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 10; Pump(s);   // Ready

        _ = s.PlayAsync();
        eng.Playing = true;                 // model the engine beginning to advance
        Pump(s);
        Assert.Equal(PlaybackState.Playing, core.State.Peek());

        _ = s.PauseAsync();
        eng.Playing = false;
        Pump(s);
        Assert.Equal(PlaybackState.Paused, core.State.Peek());
    }

    [Fact]
    public void PlayRequested_ButEngineNotAdvancing_IsBuffering()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; Pump(s);   // Ready

        _ = s.PlayAsync();
        // engine.Playing stays false → intent-to-play with no advance ⇒ Buffering (transient), never Failed.
        Pump(s);
        Assert.Equal(PlaybackState.Buffering, core.State.Peek());
        Assert.NotEqual(PlaybackState.Failed, core.State.Peek());
    }

    [Fact]
    public void EngineEnded_MapsToEnded()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; Pump(s);
        _ = s.PlayAsync(); eng.Playing = true; Pump(s);

        eng.Playing = false; eng.Ended = true;
        Pump(s);
        Assert.Equal(PlaybackState.Ended, core.State.Peek());
    }

    [Fact]
    public void Position_ProjectedFromPresentationClock()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 30; Pump(s);
        eng.CurrentTimeSeconds = 12.5;
        // Not playing (paused by default here), so PumpVideo does not extrapolate — the read is exact.
        eng.RaiseStateChanged();
        Pump(s);
        Assert.Equal(12.5, core.Position.Peek().TotalSeconds, 3);
    }

    // ── typed error mapping ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2u, MediaErrorCategory.Network, MediaRecovery.NeedsNetwork)]
    [InlineData(3u, MediaErrorCategory.Decode, MediaRecovery.Retryable)]
    [InlineData(4u, MediaErrorCategory.UnsupportedCodec, MediaRecovery.PickLowerQuality)]
    [InlineData(5u, MediaErrorCategory.Drm, MediaRecovery.NeedsLicense)]
    public void EngineError_MapsToTypedMediaError_AndFails(uint mfErr, MediaErrorCategory category, MediaRecovery recovery)
    {
        var (s, core, eng) = NewSession();
        eng.HasError = true; eng.ErrorCode = mfErr; eng.ErrorHr = unchecked((int)0x80004005);
        Pump(s);

        Assert.Equal(PlaybackState.Failed, core.State.Peek());
        var err = core.Error.Peek();
        Assert.NotNull(err);
        Assert.Equal(category, err!.Category);
        Assert.Equal(recovery, err.Recovery);
        Assert.Equal(unchecked((int)0x80004005), (int)err.UnderlyingCode!.Value);
    }

    // ── composited-surface handoff ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Video_IsNoneUntilHandle_ThenCompositedSurface()
    {
        var (s, _, eng) = NewSession();
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0;

        s.PumpVideo(binding, Rect, 1f);
        Assert.IsType<VideoDelivery.AudioOnlyDelivery>(s.Video);   // VideoDelivery.None sentinel — no handle yet

        eng.Handle = 0xABCD;
        s.PumpVideo(binding, new RectF(10, 20, 640, 360), 2f);

        var comp = Assert.IsType<VideoDelivery.CompositedSurface>(s.Video);
        Assert.Equal(new SizeI(1280, 720), comp.NaturalSize);
        Assert.False(comp.IsHdr);
    }

    [Fact]
    public void Handoff_BindsHandleAndSizesStream_ThroughRegistryAndPresenter()
    {
        var (s, _, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xBEEF;

        // Pump with device scale 2 ⇒ stream size = rect(640×360) × 2 = 1280×720 (the presenter clips, does not scale).
        s.PumpVideo(binding, new RectF(10, 20, 640, 360), 2f);
        Assert.True(eng.Commands.TryTakeStreamRect(out int w, out int h));
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
        Assert.True(eng.Commands.TryTakeRepaint());

        // Drain the registry into a fake presenter (the render-thread step) and assert the handle actually bound.
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);
        Assert.True(presenter.LastVisible);
        Assert.Contains(presenter.Calls, c => c.StartsWith("Create("));
        Assert.Contains(presenter.Calls, c => c.StartsWith("Bind("));
    }

    [Fact]
    public void Repaint_IsInvalidationDriven_AndNativeEventsRequestOneFollowingPump()
    {
        var (s, _, eng) = NewSession();
        long now = 0;
        s.ClockMs = () => now;
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xBEEF;
        int requested = 0;
        s.PumpRequested += () => requested++;

        s.PumpVideo(binding, Rect, 1f);       // initial metadata/handle/geometry hand-off
        Assert.True(eng.Commands.TryTakeRepaint());
        Assert.True(eng.Commands.TryTakeStreamRect(out _, out _));   // the hand-off's stream size

        s.PumpVideo(binding, Rect, 1f);       // identical host work is not a repaint
        Assert.False(eng.Commands.TryTakeRepaint());

        eng.RaiseStateChanged();               // MF worker event -> one coalesced caller request, but NOT a repaint
        Assert.Equal(1, requested);
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(eng.Commands.TryTakeRepaint());

        // A new destination does not re-size the stream while it may still be moving (F071): nothing is posted, so nothing
        // is repainted either ...
        s.PumpVideo(binding, new RectF(0, 0, 640, 360), 1f);
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));
        Assert.False(eng.Commands.TryTakeRepaint());
        // ... and once it has held still, the new stream size is asked for once and the frame is repainted at it.
        now += VideoStreamSizeGate.SettleMs;
        s.PumpVideo(binding, new RectF(0, 0, 640, 360), 1f);
        Assert.True(eng.Commands.TryTakeStreamRect(out int w, out int h));
        Assert.Equal((640, 360), (w, h));
        Assert.True(eng.Commands.TryTakeRepaint());
    }

    // ── the stream size: no sizing for an unlaid-out rect (F051), buckets that settle and an echo before the compositor
    //    hears of them (F071) ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PumpVideo_WithAnEmptyRect_PublishesTheNaturalSize_ButNeverSizesOrPlacesTheStream()
    {
        var (s, core, eng) = NewSession();
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1920; eng.NativeH = 1080; eng.Handle = 0xBEEF;

        s.PumpVideo(binding, default, 1f);      // the element's pump before its area is laid out

        Assert.Equal(new SizeI(1920, 1080), core.NaturalSize.Peek());     // state and natural size still publish
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));       // no 2x1 swap chain
        Assert.True(binding.ContentSize.IsEmpty);
        Assert.False(core.SurfaceGeometry.Peek().IsPlaced);

        s.PumpVideo(binding, new RectF(0, 0, 960, 540), 1f);              // the first laid-out pump sizes it
        Assert.True(eng.Commands.TryTakeStreamRect(out int w, out int h));
        Assert.Equal((960, 540), (w, h));

        s.PumpVideo(binding, default, 1f);                                // the area collapsed again
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));
        Assert.Equal(new SizeI(960, 540), binding.ContentSize);           // the last good size stays
    }

    [Fact]
    public void PumpVideo_AResize_RequestsTheNewBucketOnlyOnceSettled_AndPublishesItOnlyWhenTheEngineEchoesIt()
    {
        var (s, _, eng) = NewSession();
        long now = 0;
        s.ClockMs = () => now;
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1920; eng.NativeH = 1080; eng.Handle = 0xBEEF;

        s.PumpVideo(binding, new RectF(0, 0, 640, 360), 1f);
        Assert.True(eng.Commands.TryTakeStreamRect(out int w, out int h));
        Assert.Equal((640, 360), (w, h));                                 // the 1/3 bucket
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);
        eng.EchoStreamRect(640, 360);

        now = 100;
        s.PumpVideo(binding, new RectF(0, 0, 1000, 562), 1f);             // a resize gesture is under way
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);

        now = 350;
        s.PumpVideo(binding, new RectF(0, 0, 1000, 562), 1f);             // it has been still for 250 ms
        Assert.True(eng.Commands.TryTakeStreamRect(out w, out h));
        Assert.Equal((1440, 810), (w, h));                                // the 3/4 bucket
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);           // DComp keeps scaling the old buffer by the old size

        now = 400;
        s.PumpVideo(binding, new RectF(0, 0, 1000, 562), 1f);
        Assert.Equal(new SizeI(640, 360), binding.ContentSize);           // not echoed yet

        eng.EchoStreamRect(1440, 810);
        s.PumpVideo(binding, new RectF(0, 0, 1000, 562), 1f);
        Assert.Equal(new SizeI(1440, 810), binding.ContentSize);          // the engine applied it: now the compositor may use it
    }

    [Fact]
    public void Repaint_IsOwedAfterAPausedSeekCompletes_AndNeverPostedWhilePlaying()
    {
        var (s, _, eng) = NewSession();
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xBEEF;
        s.PumpVideo(binding, Rect, 1f);
        Assert.True(eng.Commands.TryTakeRepaint());        // the initial hand-off

        eng.Seeking = true; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(eng.Commands.TryTakeRepaint());       // a seek in flight owes nothing yet
        eng.Seeking = false; eng.SeekedCount = 1; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        Assert.True(eng.Commands.TryTakeRepaint());        // the landed paused frame is repainted once

        // A fast paused seek: SEEKING and SEEKED both land between two pumps, so the Seeking flag is never observed. The
        // SEEKED counter still moves, so the landed frame is repainted.
        eng.SeekedCount = 2; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        Assert.True(eng.Commands.TryTakeRepaint());
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(eng.Commands.TryTakeRepaint());       // and only once

        eng.Playing = true; eng.Seeking = true; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        eng.Seeking = false; eng.SeekedCount = 3; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(eng.Commands.TryTakeRepaint());       // MF presents its own frames while Playing
        eng.Playing = false; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        Assert.True(eng.Commands.TryTakeRepaint());        // the owed repaint is paid at the first non-Playing pump
    }

    [Fact]
    public void Surface_IsNotPublishedAtBind_OnlyOnceThisSourcesFirstFrameLands()
    {
        var (s, core, eng) = NewSession();
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xBEEF;

        s.PumpVideo(binding, Rect, 1f);                    // handle bound, no frame yet
        Assert.True(core.VideoSurface.Peek().IsNone);
        Assert.Equal(0, s.FirstFrameEpoch);

        eng.FirstFrameTimestamp = 12345; eng.RaiseStateChanged();
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(core.VideoSurface.Peek().IsNone);
        Assert.Equal(1, s.FirstFrameEpoch);

        s.PumpVideo(binding, Rect, 1f);                    // one-shot per session
        Assert.Equal(1, s.FirstFrameEpoch);
    }

    [Fact]
    public void Surface_IsDroppedByAPresentationEpochBump_AndRepublishedOnTheRebind()
    {
        var (s, core, eng) = NewSession();
        var binding = NewBinding(out _);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xAAAA; eng.FirstFrameTimestamp = 7;
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(core.VideoSurface.Peek().IsNone);

        eng.Handle = 0;                                    // the rebuilt swap chain has no handle yet
        eng.RaiseFormatChange();
        s.PumpVideo(binding, Rect, 1f);
        Assert.True(core.VideoSurface.Peek().IsNone);      // the poster covers until the next handle

        eng.Handle = 0xBBBB;
        s.PumpVideo(binding, Rect, 1f);
        Assert.False(core.VideoSurface.Peek().IsNone);     // same source, first frame already seen: back at the rebind
    }

    [Fact]
    public void ConnectSignals_ResetsASurfaceTheSinkStillHeldFromAPreviousSession()
    {
        var core = new MediaPlayerCore();
        var sink = new MediaSignalSink(core);
        sink.VideoSurface(new VideoSurfaceId(1));          // what the previous session of an in-place Switch left behind

        new MfMediaSession(new FakeVideoEngine(), 0, new MediaOpenOptions()).ConnectSignals(sink);

        Assert.True(core.VideoSurface.Peek().IsNone);
    }

    [Fact]
    public void PresentationEpochBump_DropsTheHandle_AndRebindsOnTheNextPump()
    {
        // Required new coverage (plan §7/§1.4 (b)): a FORMATCHANGE/RESOURCELOST — modeled by RaiseFormatChange, the
        // same epoch bump a fresh SetSource also drives — must drop the cached handle so a NEW one gets bound, not
        // left stuck at the first variant's.
        var (s, _, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xAAAA;
        s.PumpVideo(binding, Rect, 1f);

        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, 1f);
        Assert.Equal((nuint)0xAAAA, presenter.LastBoundHandle);

        eng.Handle = 0xBBBB;
        eng.RaiseFormatChange();
        s.PumpVideo(binding, Rect, 1f);
        registry.Drain(presenter, 1f);

        Assert.Equal((nuint)0xBBBB, presenter.LastBoundHandle);
    }

    // ── idempotent, posted transport ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Transport_AcceptedSynchronously_NeverThrows_InAnyState()
    {
        var (s, _, eng) = NewSession();   // still Opening (no metadata)

        Assert.True(s.PlayAsync().IsCompletedSuccessfully);
        Assert.True(s.PauseAsync().IsCompletedSuccessfully);
        Assert.True(s.SeekAsync(TimeSpan.FromSeconds(3), SeekMode.Accurate).IsCompletedSuccessfully);
        var ex = Record.Exception(() => { s.SetRate(2.0); s.SetVolume(2.0); s.SetMuted(true); });
        Assert.Null(ex);

        Assert.True(eng.Commands.TryTakeRate(out double rate));
        Assert.Equal(2.0, rate, 6);
        Assert.True(eng.Commands.TryTakeVolume(out double volume));
        Assert.Equal(1.0, volume, 6);   // clamped into 0..1
        Assert.True(eng.Commands.TryTakeMuted(out bool muted));
        Assert.True(muted);
    }

    [Fact]
    public void TransportPosts_AreLastWins_ObservedByAScriptedDrain()
    {
        // Required new coverage (plan §7/§1.4 (d)): VideoEngineCommandQueue is one coalescing slot PER KIND — a burst
        // of Play/Pause/Play collapses to the LAST post, which is what the (real or fake) engine thread ever sees.
        var (s, _, eng) = NewSession();
        _ = s.PlayAsync();
        _ = s.PauseAsync();
        _ = s.PlayAsync();

        Assert.True(eng.Commands.TryTakeTransport(out bool play));
        Assert.True(play);
        Assert.False(eng.Commands.TryTakeTransport(out _));   // drained — nothing else pending
    }

    [Fact]
    public void Seek_ClampsToDuration()
    {
        var (s, _, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 5; Pump(s);   // publishes _duration = 5s

        _ = s.SeekAsync(TimeSpan.FromSeconds(100), SeekMode.Accurate);
        Assert.True(eng.Commands.TryTakeSeek(out double secsHigh, out _));
        Assert.Equal(5.0, secsHigh, 6);

        _ = s.SeekAsync(TimeSpan.FromSeconds(-10), SeekMode.Accurate);
        Assert.True(eng.Commands.TryTakeSeek(out double secsLow, out _));
        Assert.Equal(0.0, secsLow, 6);
    }

    // ── the two pumps (F132): state needs no element, geometry needs the state half to have adopted the source ───────────

    [Fact]
    public void PumpState_AdvancesStateDurationNaturalSizeAndPosition_WithNoBindingAtAll()
    {
        var (s, core, eng) = NewSession();
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.DurationSeconds = 90; eng.Handle = 0xBEEF;

        s.PumpState();                                                    // no element, no binding, no rect

        Assert.Equal(PlaybackState.Ready, core.State.Peek());
        Assert.Equal(new SizeI(1280, 720), core.NaturalSize.Peek());
        Assert.Equal(TimeSpan.FromSeconds(90), core.Duration.Peek());
        Assert.True(core.VideoSurface.Peek().IsNone);                     // readiness needs a bound handle: that is the geometry half's
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));       // nothing sized, nothing repainted
        Assert.False(eng.Commands.TryTakeRepaint());

        _ = s.PlayAsync(); eng.Playing = true; eng.CurrentTimeSeconds = 12;
        s.PumpState();
        Assert.Equal(PlaybackState.Playing, core.State.Peek());
        Assert.InRange(core.Position.Peek(), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(14));

        eng.HasError = true; eng.ErrorCode = 2;                           // a failure while nothing is mounted still surfaces
        s.PumpState();
        Assert.Equal(PlaybackState.Failed, core.State.Peek());
        Assert.Equal(MediaErrorCategory.Network, core.Error.Peek()!.Category);
    }

    [Fact]
    public void PumpGeometry_BindsAndPlaces_ButPublishesNoState_AndWaitsForTheStateHalf()
    {
        var (s, core, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.DurationSeconds = 90; eng.Handle = 0xBEEF;
        eng.FirstFrameTimestamp = 7;

        s.PumpGeometry(binding, new RectF(10, 20, 640, 360), 2f);         // the state half has not adopted the source yet
        Assert.Equal(PlaybackState.Opening, core.State.Peek());           // …and the geometry half publishes no state of its own
        Assert.Equal(SizeI.Zero, core.NaturalSize.Peek());
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));
        Assert.True(core.VideoSurface.Peek().IsNone);

        s.PumpState();
        Assert.Equal(PlaybackState.Ready, core.State.Peek());
        s.PumpGeometry(binding, new RectF(10, 20, 640, 360), 2f);

        Assert.True(eng.Commands.TryTakeStreamRect(out int w, out int h));
        Assert.Equal((1280, 720), (w, h));
        Assert.False(core.VideoSurface.Peek().IsNone);                    // handle bound + this source's first frame
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);

        s.PumpGeometry(default, new RectF(0, 0, 100, 100), 1f);           // an inert binding does nothing at all
        Assert.False(eng.Commands.TryTakeStreamRect(out _, out _));
    }

    [Fact]
    public void PumpGeometry_LeavesAPresentationEpochTheStateHalfHasNotAdopted_ForTheNextTurn()
    {
        var (s, _, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.Handle = 0xAAAA;
        s.PumpState();
        s.PumpGeometry(binding, Rect, 1f);
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, 1f);
        Assert.Equal((nuint)0xAAAA, presenter.LastBoundHandle);

        eng.Handle = 0xBBBB;
        eng.RaiseFormatChange();
        s.PumpGeometry(binding, Rect, 1f);                                // the epoch moved and the state half has not seen it
        registry.Drain(presenter, 1f);
        Assert.Equal((nuint)0xAAAA, presenter.LastBoundHandle);           // the old handle is not re-bound over a replaced swap chain

        s.PumpState();                                                    // the same raise requests the state pump first…
        s.PumpGeometry(binding, Rect, 1f);                                // …then the element's turn
        registry.Drain(presenter, 1f);
        Assert.Equal((nuint)0xBBBB, presenter.LastBoundHandle);
    }

    [Fact]
    public void PumpVideo_IsTheTwoHalvesInOneCall()
    {
        var (s, core, eng) = NewSession();
        var binding = NewBinding(out var registry);
        eng.MetadataLoaded = true; eng.NativeW = 1280; eng.NativeH = 720; eng.DurationSeconds = 90; eng.Handle = 0xBEEF;

        s.PumpVideo(binding, new RectF(0, 0, 640, 360), 2f);              // one snapshot, state then geometry

        Assert.Equal(TimeSpan.FromSeconds(90), core.Duration.Peek());
        Assert.True(eng.Commands.TryTakeStreamRect(out int w, out int h));
        Assert.Equal((1280, 720), (w, h));
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);
    }

    // ── stale-epoch guard + disposal ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StaleEpochSnapshot_IsIgnoredByPumpVideo()
    {
        // Required new coverage (plan §7/§1.4 (a)): a snapshot whose SourceEpoch does not match this session's own is
        // either not-yet-caught-up (the engine hasn't drained the SetSource command yet) or belongs to a LATER
        // session sharing a warm-reused engine. Either way PumpVideo must not act on it.
        var core = new MediaPlayerCore();
        var sink = new MediaSignalSink(core);
        var engine = new FakeVideoEngine();   // Snapshot.SourceEpoch stays at its default (0)
        var session = new MfMediaSession(engine, sourceEpoch: 1, new MediaOpenOptions { StartPaused = true });
        session.ConnectSignals(sink);
        engine.MetadataLoaded = true; engine.DurationSeconds = 10;

        session.PumpVideo(default, Rect, 1f);

        Assert.Equal(PlaybackState.Opening, core.State.Peek());   // never advanced past ConnectSignals' own publish
    }

    [Fact]
    public async Task Dispose_TearsDownEngine_WithinTimeout()
    {
        var (s, _, eng) = NewSession();
        await s.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, eng.DisposeCalls);

        // Idempotent + inert after dispose.
        Assert.True(s.PlayAsync().IsCompletedSuccessfully);
        s.PumpVideo(default, Rect, 1f);   // no throw
    }

    [Fact]
    public void RendererFrameCounters_AreMappedOntoPlaybackStatistics_AndValueGated()
    {
        var (s, core, eng) = NewSession();
        Pump(s);
        Assert.Equal(PlaybackStatistics.Empty, core.Statistics.Peek());   // nothing counted yet: the seam keeps Empty

        eng.Frames = (120, 3);
        Pump(s);
        PlaybackStatistics stats = core.Statistics.Peek();
        Assert.Equal(120, stats.FramesRendered);
        Assert.Equal(3, stats.FramesDropped);
        Assert.Equal(123, stats.FramesDecoded);   // MF reports no separate decoder count: rendered + dropped

        core.SetStatistics(PlaybackStatistics.Empty);
        Pump(s);
        Assert.Equal(PlaybackStatistics.Empty, core.Statistics.Peek());   // unchanged counters publish nothing

        eng.Frames = (180, 3);
        Pump(s);
        Assert.Equal(180, core.Statistics.Peek().FramesRendered);
    }

    [Fact]
    public void AHungEngine_IsATypedRetryableDecodeError_AndItsCountersStayPublished()
    {
        var (s, core, eng) = NewSession();
        int hr = unchecked((int)0x800705B4);   // HRESULT_FROM_WIN32(ERROR_TIMEOUT): no frame rendered within 10 s of playing
        eng.Frames = (0, 7);
        eng.ErrorCode = 3;                     // MF_MEDIA_ENGINE_ERR_DECODE
        eng.ErrorHr = hr;
        eng.HasError = true;

        Pump(s);

        MediaError? error = core.Error.Peek();
        Assert.NotNull(error);
        Assert.Equal(MediaErrorCategory.Decode, error!.Category);
        Assert.Equal(MediaRecovery.Retryable, error.Recovery);
        Assert.Equal(hr, (int)error.UnderlyingCode!.Value);
        Assert.Equal(PlaybackState.Failed, core.State.Peek());
        Assert.Equal(7, core.Statistics.Peek().FramesDropped);   // published ahead of the error gate
    }

    [Fact]
    public async Task DisposeAsync_InvokesTheReleaseCallback_AndDoesNotDisposeTheEngine()
    {
        // Required new coverage (plan §7/§1.4 (e)): a warm-pooled session (MfMediaPlayer.OpenAsync always supplies a
        // release) returns the engine via the callback instead of disposing it.
        var core = new MediaPlayerCore();
        var sink = new MediaSignalSink(core);
        var engine = new FakeVideoEngine();
        IVideoEngine? released = null;
        var session = new MfMediaSession(engine, 0, new MediaOpenOptions(), release: e => released = e);
        session.ConnectSignals(sink);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(engine, released);
        Assert.Equal(0, engine.DisposeCalls);
    }
}
