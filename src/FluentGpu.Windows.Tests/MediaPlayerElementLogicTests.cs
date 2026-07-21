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
    public void FitVideoRect_None_CentersNativeSizeClampedToArea()
    {
        var r = MediaPlayerElement.FitVideoRect(new RectF(0, 0, 320, 180), new SizeI(100, 100), MediaStretch.None);
        Assert.Equal(100f, r.W, P);
        Assert.Equal(100f, r.H, P);
        Assert.Equal(110f, r.X, P);   // (320-100)/2
        Assert.Equal(40f, r.Y, P);    // (180-100)/2
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

    [Theory]
    [InlineData(true, PlaybackState.Opening, true)]
    [InlineData(true, PlaybackState.Buffering, true)]
    [InlineData(true, PlaybackState.Playing, false)]
    [InlineData(false, PlaybackState.Playing, true)]
    public void ChromePolicy_KeepsEarlyPlayAndPausedStatesVisible(bool playIntent, PlaybackState state, bool forced)
        => Assert.Equal(forced, MediaPlayerElement.ShouldForceChrome(playIntent, state));

    [Theory]
    [InlineData(0, false)]
    [InlineData(420, true)]
    [InlineData(759, true)]
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
}
