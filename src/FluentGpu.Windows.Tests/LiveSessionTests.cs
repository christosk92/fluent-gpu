using System;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
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

    private static (MfMediaSession session, MediaPlayerCore core, FakeVideoEngine engine) NewSession(bool startPaused = true)
    {
        var core = new MediaPlayerCore();
        var engine = new FakeVideoEngine();
        var session = new MfMediaSession(engine, new MediaOpenOptions { StartPaused = startPaused });
        session.ConnectSignals(new MediaSignalSink(core));
        return (session, core, engine);
    }

    private static void Pump(MfMediaSession s) => s.PumpVideo(default, Rect, 1f);

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
}
