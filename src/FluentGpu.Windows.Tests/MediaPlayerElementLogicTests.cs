using System;
using FluentGpu.Controls.Media;
using FluentGpu.Foundation;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>M1 tests for <see cref="MediaPlayerElement"/>'s pure presentation logic: the audio-only degrade decision, the
/// <see cref="MediaStretch"/> fit math, the DIP→device hole-punch rect, and transport time formatting. No component
/// mount, no GPU — these are the load-bearing pure functions.</summary>
public sealed class MediaPlayerElementLogicTests
{
    private const int P = 6;   // float assert precision

    [Fact]
    public void IsAudioOnly_TrueIffNoVideo()
    {
        Assert.True(MediaPlayerElement.IsAudioOnly(SizeI.Zero));
        Assert.True(MediaPlayerElement.IsAudioOnly(new SizeI(0, 0)));
        Assert.False(MediaPlayerElement.IsAudioOnly(new SizeI(1920, 1080)));
    }

    [Fact]
    public void FitVideoRect_Uniform_LetterboxesPreservingAspect()
    {
        // 16:9 video into a 400×180 area (wider than 16:9) ⇒ pillarboxed to 320×180, centered.
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 400, 180), new SizeI(1920, 1080), MediaStretch.Uniform);
        Assert.Equal(320f, r.W, P);
        Assert.Equal(180f, r.H, P);
        Assert.Equal(40f, r.X, P);   // (400-320)/2
        Assert.Equal(0f, r.Y, P);
    }

    [Fact]
    public void FitVideoRect_Uniform_ExactAspectFillsArea()
    {
        var r = MediaPlayerElement.FitVideoRect(new RectF(10, 20, 320, 180), new SizeI(1920, 1080), MediaStretch.Uniform);
        Assert.Equal(320f, r.W, P);
        Assert.Equal(180f, r.H, P);
        Assert.Equal(10f, r.X, P);
        Assert.Equal(20f, r.Y, P);
    }

    [Fact]
    public void FitVideoRect_Fill_ReturnsWholeArea()
    {
        var area = new RectF(5, 5, 400, 200);
        Assert.Equal(area, MediaPlayerElement.FitVideoRect(area, new SizeI(1920, 1080), MediaStretch.Fill));
    }

    [Fact]
    public void FitVideoRect_None_CentersNativeSize()
    {
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 320, 180), new SizeI(100, 100), MediaStretch.None);
        Assert.Equal(100f, r.W, P);
        Assert.Equal(100f, r.H, P);
        Assert.Equal(110f, r.X, P);   // (320-100)/2
        Assert.Equal(40f, r.Y, P);    // (180-100)/2
    }

    [Fact]
    public void FitVideoRect_Native_larger_than_area_overflows_centred_without_distortion()
    {
        // True 1:1: no per-axis clamp to the area. A frame larger than the area overflows on both axes (a caller's
        // viewport clip crops the excess — the same road UniformToFill already takes) rather than shrinking to fit.
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 999, 564), new SizeI(1280, 720),
            VideoAspectMode.Native, 0, scale: 1f);
        Assert.Equal(1280f, r.W, P);
        Assert.Equal(720f, r.H, P);
        Assert.Equal(-140.5f, r.X, P);   // (999-1280)/2
        Assert.Equal(-78f, r.Y, P);      // (564-720)/2
    }

    [Fact]
    public void FitVideoRect_Native_is_device_pixels()
    {
        // natural is PIXELS, area is DIP: at scale 1.65 a 1280x720 frame occupies 1280/1.65 x 720/1.65 DIP —
        // never natural size verbatim — regardless of how much larger the area is.
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 2000, 2000), new SizeI(1280, 720),
            VideoAspectMode.Native, 0, scale: 1.65f);
        Assert.Equal(775.75757f, r.W, P);
        Assert.Equal(436.36364f, r.H, P);
    }

    [Fact]
    public void FitVideoRect_UniformToFill_ReturnsOversizedCenteredContentForViewportCrop()
    {
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 320, 180), new SizeI(100, 50), MediaStretch.UniformToFill);
        // Fills at least one axis fully (scale = max(3.2, 3.6) = 3.6 ⇒ height hits 180, width clamps to 320).
        Assert.Equal(180f, r.H, P);
        Assert.Equal(360f, r.W, P);
        Assert.Equal(-20f, r.X, P);
    }

    [Fact]
    public void FitVideoRect_CustomAspect_UsesRequestedDisplayRatio()
    {
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 400, 300), new SizeI(1920, 1080),
            VideoAspectMode.Custom, 1.0);
        Assert.Equal(300f, r.W, P);
        Assert.Equal(300f, r.H, P);
        Assert.Equal(50f, r.X, P);
    }

    [Theory]
    [InlineData(4.0 / 3.0, 400, 300)]
    [InlineData(16.0 / 9.0, 400, 225)]
    [InlineData(2.39, 400, 167.364)]
    public void FitVideoRect_CustomPresets_PreserveRequestedDisplayRatio(double ratio, float expectedW, float expectedH)
    {
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 400, 300), new SizeI(1920, 1080),
            VideoAspectMode.Custom, ratio);
        Assert.Equal(expectedW, r.W, 2);
        Assert.Equal(expectedH, r.H, 2);
        Assert.Equal(200f, r.X + r.W * 0.5f, P);
        Assert.Equal(150f, r.Y + r.H * 0.5f, P);
    }

    [Fact]
    public void LetterboxBars_UniformProducesCenteredPillars_CropProducesNone()
    {
        Span<RectF> bars = stackalloc RectF[4];
        var area = new RectF(0, 0, 400, 180);
        var fit = MediaPlayerElement.FitVideoRect(area, new SizeI(1920, 1080), VideoAspectMode.Uniform, 0);
        int count = MediaPlayerElement.CalculateLetterboxBars(area, fit, bars);
        Assert.Equal(2, count);
        Assert.Equal(new RectF(0, 0, 40, 180), bars[0]);
        Assert.Equal(new RectF(360, 0, 40, 180), bars[1]);

        var crop = MediaPlayerElement.FitVideoRect(area, new SizeI(1920, 1080), VideoAspectMode.UniformToFill, 0);
        Assert.Equal(0, MediaPlayerElement.CalculateLetterboxBars(area, crop, bars));
    }

    // Only USER-VISIBLE stops reveal and hold the chrome. The protected session maps Licensed+Buffering onto
    // PlaybackState.Buffering (sampled every 250 ms) and an ABR quality switch reports buffering too — treating either as
    // a stop is what made the controls pop up mid-playback with no user input. A zero NaturalSize while Opening is a
    // video whose size is not known yet, not an audio-only source.
    [Theory]
    [InlineData(true,  PlaybackState.Playing,   false, ChromePlayback.Playing)]
    [InlineData(true,  PlaybackState.Buffering, false, ChromePlayback.Playing)]    // a rebuffer / ABR switch is not a stop
    [InlineData(true,  PlaybackState.Stalled,   false, ChromePlayback.Playing)]
    [InlineData(true,  PlaybackState.Opening,   true,  ChromePlayback.Playing)]    // size unknown while opening ≠ audio-only
    [InlineData(true,  PlaybackState.Ready,     false, ChromePlayback.Playing)]    // opened, about to play
    [InlineData(true,  PlaybackState.Playing,   true,  ChromePlayback.AudioOnly)]
    [InlineData(false, PlaybackState.Playing,   false, ChromePlayback.Paused)]     // intent wins
    [InlineData(true,  PlaybackState.Paused,    false, ChromePlayback.Paused)]
    [InlineData(true,  PlaybackState.Ended,     false, ChromePlayback.Ended)]
    [InlineData(true,  PlaybackState.Failed,    false, ChromePlayback.Failed)]
    public void ChromePlaybackOf_MapsOnlyUserVisibleStops(bool intent, PlaybackState state, bool audioOnly, ChromePlayback expected)
        => Assert.Equal(expected, MediaPlayerElement.ChromePlaybackOf(intent, state, audioOnly));

    // The compaction threshold dropped 760 -> 420 DIP. At 760 the transport had room for the full right cluster and
    // was collapsing chips into the ellipsis for no reason; 420 is where the left cluster and the time actually stop
    // fitting alongside it. Width 0 (the first layout pass, before the area is measured) must NOT read as compact —
    // that is what made all three chips render and then vanish a frame later.
    [Theory]
    [InlineData(0, false)]
    [InlineData(419, true)]
    [InlineData(420, false)]
    [InlineData(760, false)]
    public void ResponsiveChrome_CollapsesAdvancedCommandsIntoEllipsis(float width, bool compact)
        => Assert.Equal(compact, MediaPlayerElement.IsCompactTransport(width));

    [Fact]
    public void FitVideoRect_DegenerateArea_ReturnsArea()
    {
        var area = new RectF(0, 0, 0, 0);
        Assert.Equal(area, MediaPlayerElement.FitVideoRect(area, new SizeI(1920, 1080), MediaStretch.Uniform));
    }

    [Fact]
    public void ToDeviceRect_ScalesDipByFactor()
    {
        var d = MediaPlayerElement.ToDeviceRect(new RectF(10, 20, 30, 40), 2f);
        Assert.Equal(new RectF(20, 40, 60, 80), d);
        // A non-positive scale is treated as 1.
        Assert.Equal(new RectF(10, 20, 30, 40), MediaPlayerElement.ToDeviceRect(new RectF(10, 20, 30, 40), 0f));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(83, "1:23")]
    [InlineData(600, "10:00")]
    [InlineData(3661, "1:01:01")]
    public void FormatTime_HumanReadable(int seconds, string expected)
        => Assert.Equal(expected, MediaPlayerElement.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FormatTime_NegativeOrUnknown_IsZero()
    {
        Assert.Equal("0:00", MediaPlayerElement.FormatTime(TimeSpan.FromSeconds(-5)));
        Assert.Equal("0:00", MediaPlayerElement.FormatTime(TimeSpan.MinValue));
    }

    [Fact]
    public void VideoAreaMinHeight_Drops_on_host_or_overlay_fullscreen()
    {
        Assert.Equal(160f, MediaPlayerElement.VideoAreaMinHeight(presentingFullscreen: false, decorative: false));
        Assert.Equal(0f, MediaPlayerElement.VideoAreaMinHeight(presentingFullscreen: true, decorative: false));
        Assert.Equal(0f, MediaPlayerElement.VideoAreaMinHeight(presentingFullscreen: false, decorative: true));
    }

    [Fact]
    public void PumpClampsOverflow_skips_crop_and_native_so_both_keep_overflow()
    {
        Assert.True(MediaPlayerElement.PumpClampsOverflow(VideoAspectMode.Uniform));
        Assert.True(MediaPlayerElement.PumpClampsOverflow(VideoAspectMode.Fill));
        Assert.False(MediaPlayerElement.PumpClampsOverflow(VideoAspectMode.Native));
        Assert.False(MediaPlayerElement.PumpClampsOverflow(VideoAspectMode.UniformToFill));
    }

    [Fact]
    public void ClampUniformToViewport_does_not_run_for_crop_overflow()
    {
        var area = new RectF(0, 0, 320, 180);
        var crop = MediaPlayerElement.FitVideoRect(area, new SizeI(100, 50), VideoAspectMode.UniformToFill, 0);
        Assert.True(crop.W > area.W || crop.H > area.H);
        Assert.False(MediaPlayerElement.PumpClampsOverflow(VideoAspectMode.UniformToFill));
    }

    // ── L2-09: the clamp decision (F126) and the scale-aware hole rect (F129) ─────────────────────────────────────────

    [Fact]
    public void ExceedsDirectHost_ignores_a_clip_that_only_cuts_a_correctly_laid_out_video()
    {
        // The video sits inside its host; a scroller or window edge further up only changes the VIEWPORT, which is not an
        // input here, so the safety net must not shrink and re-centre the video away from its hole.
        var host = new RectF(0, 0, 640, 360);
        Assert.False(MediaPlayerElement.ExceedsDirectHost(new RectF(0, 0, 640, 360), host));
        Assert.False(MediaPlayerElement.ExceedsDirectHost(new RectF(40, 0, 560, 360), host));
        Assert.False(MediaPlayerElement.ExceedsDirectHost(new RectF(0.3f, 0, 640f, 360), host));      // sub-half-pixel snap slack
    }

    [Fact]
    public void ExceedsDirectHost_fires_when_the_element_pokes_out_of_its_direct_host()
    {
        var host = new RectF(100, 50, 320, 180);
        Assert.True(MediaPlayerElement.ExceedsDirectHost(new RectF(100, 50, 400, 180), host));    // widened past the host
        Assert.True(MediaPlayerElement.ExceedsDirectHost(new RectF(100, 50, 320, 220), host));    // taller than the host
        Assert.True(MediaPlayerElement.ExceedsDirectHost(new RectF(90, 50, 320, 180), host));     // shifted out on the left
        Assert.True(MediaPlayerElement.ExceedsDirectHost(new RectF(100, 40, 320, 180), host));    // shifted out on the top
    }

    [Fact]
    public void ExceedsDirectHost_then_clamp_shrinks_uniformly_inside_the_host()
    {
        var host = new RectF(0, 0, 320, 180);
        var video = new RectF(0, 0, 640, 360);
        Assert.True(MediaPlayerElement.ExceedsDirectHost(video, host));
        var clamped = MediaPlayerElement.ClampUniformToViewport(video, host);
        Assert.Equal(320f, clamped.W, 0.01f);
        Assert.Equal(180f, clamped.H, 0.01f);
        Assert.False(MediaPlayerElement.ExceedsDirectHost(clamped, host));
    }

    [Fact]
    public void RectFromHoleInsets_is_the_plain_inset_rect_when_the_area_is_unscaled()
    {
        var layout = new RectF(0, 0, 560, 360);
        var area = new RectF(30, 20, 560, 360);   // translated only: same size as its layout bounds
        var rect = MediaPlayerElement.RectFromHoleInsets(area, layout, new Edges4(145, 0, 145, 0));
        Assert.Equal(new RectF(175, 20, 270, 360), rect);
    }

    [Fact]
    public void RectFromHoleInsets_scales_the_insets_under_a_scaled_ancestor()
    {
        var layout = new RectF(0, 0, 560, 360);
        var area = new RectF(0, 0, 280, 180);     // the whole tree painted at half scale
        var rect = MediaPlayerElement.RectFromHoleInsets(area, layout, new Edges4(145, 0, 145, 0));
        Assert.Equal(72.5f, rect.X, 0.01f);
        Assert.Equal(135f, rect.W, 0.01f);
        Assert.Equal(180f, rect.H, 0.01f);
    }

    [Fact]
    public void RectFromHoleInsets_with_an_empty_layout_area_does_not_divide_by_zero()
    {
        var rect = MediaPlayerElement.RectFromHoleInsets(new RectF(5, 6, 100, 50), default, default);
        Assert.Equal(new RectF(5, 6, 100, 50), rect);
    }

    // ── L2-07: the status overlay (F121) and the pure inputs of the render diet (F123 / F134) ──────────────────────────
    // Facts, not Theories: StatusOverlayKind is internal, and a public Theory method cannot take it as a parameter.

    private static readonly PlaybackState[] AllStates = Enum.GetValues<PlaybackState>();

    [Fact]
    public void ChooseStatusOverlay_decorative_or_host_owned_never_shows_anything()
    {
        foreach (var state in AllStates)
            foreach (bool startingUp in new[] { false, true })
                foreach (bool buffering in new[] { false, true })
                {
                    Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
                        MediaPlayerElement.ChooseStatusOverlay(true, false, state, startingUp, 2, buffering, buffering));
                    // Failure included: a host that owns its loading visuals owns its failure visual too.
                    Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
                        MediaPlayerElement.ChooseStatusOverlay(false, true, state, startingUp, 2, buffering, buffering));
                }
    }

    [Fact]
    public void ChooseStatusOverlay_failure_wins_over_startup_and_rebuffer()
    {
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.Failed,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Failed, true, 2, true, true));
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.Failed,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Failed, false, 0, false, false));
    }

    [Fact]
    public void ChooseStatusOverlay_startup_ladder_is_silent_until_the_spinner_delay()
    {
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Opening, true, 0, false, false));
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.Opening,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Opening, true, 1, false, false));
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.Opening,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Buffering, true, 2, true, true));
    }

    [Fact]
    public void ChooseStatusOverlay_visible_opening_ring_hands_straight_over_to_the_rebuffer_pill()
    {
        // the start just ended with the ring already up (phase >= 1) and a rebuffer follows: no 500 ms of nothing between them
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.Buffering,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Buffering, false, 1, true, false));
        // the same inputs with no ring up yet: the pill still waits out its own delay
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Buffering, false, 0, true, false));
        // a ring that is up never conjures a pill when no rebuffer is wanted
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Playing, false, 1, false, false));
    }

    [Fact]
    public void ChooseStatusOverlay_rebuffer_pill_waits_out_its_own_delay()
    {
        // wanted but the 500 ms has not elapsed: nothing (a pill that flashes for 200 ms reports trouble that did not happen)
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Buffering, false, 0, true, false));
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.Buffering,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Buffering, false, 0, true, true));
        // the delay flag alone (a stale latch) never mounts it
        Assert.Equal(MediaPlayerElement.StatusOverlayKind.None,
            MediaPlayerElement.ChooseStatusOverlay(false, false, PlaybackState.Playing, false, 0, false, true));
    }

    [Fact]
    public void BufferingOverlayWanted_seek_and_quality_switch_behind_a_presented_frame_are_silent()
    {
        foreach (var reason in new[] { BufferingReason.Seeking, BufferingReason.QualitySwitch })
        {
            Assert.False(MediaPlayerElement.BufferingOverlayWanted(false, true, reason, PlaybackState.Buffering, framePresented: true));
            // the state alone (a protected seek publishes Buffering) is no reason either
            Assert.False(MediaPlayerElement.BufferingOverlayWanted(false, false, reason, PlaybackState.Buffering, framePresented: true));
            // with NO picture up the same seek is trouble worth a pill
            Assert.True(MediaPlayerElement.BufferingOverlayWanted(false, true, reason, PlaybackState.Buffering, framePresented: false));
        }
    }

    [Fact]
    public void BufferingOverlayWanted_real_stalls_and_rebuffers_are_not_silent()
    {
        foreach (var reason in new[] { BufferingReason.Rebuffering, BufferingReason.NetworkRecovery, BufferingReason.LiveCatchUp,
                     BufferingReason.TrackSwitch })
            Assert.True(MediaPlayerElement.BufferingOverlayWanted(false, true, reason, PlaybackState.Buffering, framePresented: true));
        Assert.True(MediaPlayerElement.BufferingOverlayWanted(false, false, BufferingReason.None, PlaybackState.Stalled, framePresented: true));
        Assert.True(MediaPlayerElement.BufferingOverlayWanted(false, false, BufferingReason.None, PlaybackState.Buffering, framePresented: true));
    }

    [Fact]
    public void BufferingOverlayWanted_never_during_startup_or_steady_playback()
    {
        Assert.False(MediaPlayerElement.BufferingOverlayWanted(true, true, BufferingReason.Rebuffering, PlaybackState.Buffering, framePresented: false));
        Assert.False(MediaPlayerElement.BufferingOverlayWanted(false, false, BufferingReason.None, PlaybackState.Playing, framePresented: true));
        Assert.False(MediaPlayerElement.BufferingOverlayWanted(false, false, BufferingReason.None, PlaybackState.Paused, framePresented: true));
    }

    [Fact]
    public void QuantizeStatusPercent_steps_by_five_percent_and_flags_unknown()
    {
        Assert.Equal(0.0, MediaPlayerElement.QuantizeStatusPercent(0.0), P);
        Assert.Equal(0.5, MediaPlayerElement.QuantizeStatusPercent(0.52), P);
        Assert.Equal(0.55, MediaPlayerElement.QuantizeStatusPercent(0.53), P);
        Assert.Equal(1.0, MediaPlayerElement.QuantizeStatusPercent(0.99), P);
        Assert.Equal(1.0, MediaPlayerElement.QuantizeStatusPercent(1.0), P);
        Assert.Equal(-1.0, MediaPlayerElement.QuantizeStatusPercent(-1.0), P);   // the seek-intent "unknown"
        Assert.Equal(-1.0, MediaPlayerElement.QuantizeStatusPercent(1.5), P);
        Assert.Equal(-1.0, MediaPlayerElement.QuantizeStatusPercent(double.NaN), P);
        // sub-notch churn (one appended segment) lands on the SAME value, which is what cuts the leaf's memo off
        Assert.Equal(MediaPlayerElement.QuantizeStatusPercent(0.401), MediaPlayerElement.QuantizeStatusPercent(0.424), P);
    }

    [Fact]
    public void BufferingKeyOf_ignores_percent_and_buffered_amounts()
    {
        var a = new BufferingInfo(BufferingReason.Rebuffering, 0.10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), false);
        var b = new BufferingInfo(BufferingReason.Rebuffering, 0.85, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), true);
        Assert.Equal(MediaPlayerElement.BufferingKeyOf(a), MediaPlayerElement.BufferingKeyOf(b));
        Assert.NotEqual(MediaPlayerElement.BufferingKeyOf(a),
            MediaPlayerElement.BufferingKeyOf(new BufferingInfo(BufferingReason.Seeking, 0.10, TimeSpan.Zero, TimeSpan.Zero, false)));
        Assert.False(MediaPlayerElement.BufferingKeyOf(BufferingInfo.None).IsBuffering);
        Assert.True(MediaPlayerElement.BufferingKeyOf(a).IsBuffering);
    }

    [Fact]
    public void IsVideoReady_needs_video_and_a_state_past_opening()
    {
        var video = new SizeI(1920, 1080);
        Assert.False(MediaPlayerElement.IsVideoReady(video, PlaybackState.Idle));
        Assert.False(MediaPlayerElement.IsVideoReady(video, PlaybackState.Opening));
        Assert.True(MediaPlayerElement.IsVideoReady(video, PlaybackState.Buffering));
        Assert.True(MediaPlayerElement.IsVideoReady(video, PlaybackState.Playing));
        Assert.False(MediaPlayerElement.IsVideoReady(SizeI.Zero, PlaybackState.Playing));   // audio-only never shows captions over video
    }

    [Fact]
    public void CaptionBaseline_keeps_its_bottom_inset()
        => Assert.Equal(28f, MediaCaptionOverlay.BottomMargin);
}
