using FluentGpu.Foundation;
using FluentGpu.Render;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Pixel rule R for the composite erase of a video hole (F078 + F073): X, Y, Right and Bottom are each rounded to the nearest
/// device pixel independently, midpoints away from zero - the same rule the DirectComposition video rect uses, so the erase and
/// the video cover the same pixels. The rest of the erase's shape (strength, per-corner radii) is gated end to end by
/// <c>gate.video.hole-erase-shape</c> in the vertical slice.
/// </summary>
public sealed class VideoHoleEraseShapeTests
{
    [Fact]
    public void EachEdgeRoundsIndependently_NotOriginPlusSize()
    {
        // x: 10.4..20.4 rounds to 10..20 (width 10), but 10.6..20.6 rounds to 11..21 (width 10), and 10.4..20.6 to 10..21 (11):
        // the far edge is rounded on its own, never as origin + rounded size.
        var r = SliceRecorder.WholePx(new RectF(10.4f, 5.6f, 10.2f, 9.5f));
        Assert.Equal(10f, r.X);
        Assert.Equal(6f, r.Y);
        Assert.Equal(21f, r.Right);   // 20.6 -> 21
        Assert.Equal(15f, r.Bottom);  // 15.1 -> 15
        Assert.Equal(11f, r.W);
        Assert.Equal(9f, r.H);
    }

    [Fact]
    public void MidpointsRoundAwayFromZero()
    {
        var r = SliceRecorder.WholePx(new RectF(10.5f, 20.5f, 100f, 50f));
        Assert.Equal(11f, r.X);       // .NET's default (banker's) rounding would give 10
        Assert.Equal(21f, r.Y);       // ... and 20
        Assert.Equal(111f, r.Right);  // 110.5 -> 111
        Assert.Equal(71f, r.Bottom);  // 70.5 -> 71
    }

    [Fact]
    public void WholePixelInput_IsUnchanged()
    {
        var r = SliceRecorder.WholePx(new RectF(12f, 30f, 400f, 225f));
        Assert.Equal(new RectF(12f, 30f, 400f, 225f), r);
    }

    [Fact]
    public void ASliverThinnerThanHalfAPixel_RoundsToEmpty_AndEmptyStaysEmpty()
    {
        Assert.True(SliceRecorder.WholePx(new RectF(10.1f, 10f, 0.3f, 20f)).IsEmpty);
        Assert.True(SliceRecorder.WholePx(default).IsEmpty);
    }
}
