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
/// M1 tests for <see cref="MfMediaSession"/>: the MF-state → <see cref="MediaSignalSink"/> mapping, the composited-surface
/// handoff, typed error mapping, idempotent transport and clean disposal — all driven through a <see cref="FakeVideoEngine"/>
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
        var session = new MfMediaSession(engine, new MediaOpenOptions { StartPaused = startPaused });
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
        var session = new MfMediaSession(new FakeVideoEngine(), new MediaOpenOptions(), manifest);

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
        var session = new MfMediaSession(new FakeVideoEngine(), new MediaOpenOptions(), manifest);

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
        Assert.Equal(1280, eng.StreamW);
        Assert.Equal(720, eng.StreamH);
        Assert.True(eng.RepaintCalls > 0);

        // Drain the registry into a fake presenter (the render-thread step) and assert the handle actually bound.
        var presenter = new FakeVideoPresenter();
        registry.Drain(presenter, scale: 1f);
        Assert.Equal((nuint)0xBEEF, presenter.LastBoundHandle);
        Assert.True(presenter.LastVisible);
        Assert.Contains(presenter.Calls, c => c.StartsWith("Create("));
        Assert.Contains(presenter.Calls, c => c.StartsWith("Bind("));
    }

    // ── idempotent transport ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Transport_AcceptedSynchronously_NeverThrows_InAnyState()
    {
        var (s, _, eng) = NewSession();   // still Opening (no metadata)

        Assert.True(s.PlayAsync().IsCompletedSuccessfully);
        Assert.True(s.PauseAsync().IsCompletedSuccessfully);
        Assert.True(s.SeekAsync(TimeSpan.FromSeconds(3), SeekMode.Accurate).IsCompletedSuccessfully);
        var ex = Record.Exception(() => { s.SetRate(2.0); s.SetVolume(2.0); s.SetMuted(true); });
        Assert.Null(ex);

        Assert.Equal(2.0, eng.LastRate, 6);
        Assert.Equal(1.0, eng.LastVolume, 6);   // clamped into 0..1
        Assert.True(eng.LastMuted);
    }

    [Fact]
    public void Seek_ClampsToDuration()
    {
        var (s, _, eng) = NewSession();
        eng.MetadataLoaded = true; eng.DurationSeconds = 5; Pump(s);   // publishes _duration = 5s

        _ = s.SeekAsync(TimeSpan.FromSeconds(100), SeekMode.Accurate);
        Assert.Equal(5.0, eng.LastSeek, 6);

        _ = s.SeekAsync(TimeSpan.FromSeconds(-10), SeekMode.Accurate);
        Assert.Equal(0.0, eng.LastSeek, 6);
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
}
