using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A programmatic glide whose target lies past the content end (a "scroll to bottom", a BringIntoView of a section whose
/// top cannot reach the viewport top, a chevron ScrollBy with less than a page left) glides to today's end and keeps the
/// raw target latched. The host republishes the geometry through SetExtent every frame; that must not replace the glide
/// with a jump to the end, and content that grows mid-flight re-aims the glide from where it is.
/// </summary>
public sealed class ScrollGlidePastEndTests
{
    private static readonly ScrollViewportId Vp = new(7, 1);

    [Fact]
    public void ScrollTo_PastTheEnd_KeepsGliding_UnderThePerFrameExtentRepublish()
    {
        double now = 1.0;
        var h = new ScrollHandle(new PlanSlots(), Vp, () => now);
        h.SetExtent(2_000.0, 500.0);                     // max 1500
        h.ScrollTo(100.0, ScrollMove.Immediate);
        h.ScrollTo(5_000.0);                             // glides to today's max, the raw target stays latched
        Assert.True(h.RestorePending);
        Assert.Equal(1_500.0, h.Plan.Dest);

        for (int f = 1; f <= 3; f++)
        {
            now = 1.0 + f / 60.0;
            h.SetExtent(2_000.0, 500.0);                 // the frame step's unchanged republish
            Assert.Equal(MotionKind.Programmatic, h.Plan.Kind);
            Assert.Equal(SegKind.Glide, h.Plan.S0.Kind);
            double p = h.OffsetNow;
            Assert.True(p > 100.0 && p < 1_499.0, $"frame {f}: shown {p} (a cut to the end shows 1500)");
        }
    }

    [Fact]
    public void BringIntoView_AlignedPastTheEnd_KeepsGliding()
    {
        double now = 1.0;
        var h = new ScrollHandle(new PlanSlots(), Vp, () => now);
        h.SetExtent(2_000.0, 500.0);
        h.BringIntoView(1_800.0, 150.0, align: 0f);      // the last section's top cannot reach the viewport top
        Assert.True(h.RestorePending);
        now = 1.0 + 1.0 / 60.0;
        h.SetExtent(2_000.0, 500.0);
        Assert.Equal(SegKind.Glide, h.Plan.S0.Kind);
        Assert.True(h.OffsetNow < 1_499.0);
    }

    [Fact]
    public void ScrollTo_PastTheEnd_GlidesOnAsTheContentGrows()
    {
        double now = 1.0;
        var h = new ScrollHandle(new PlanSlots(), Vp, () => now);
        h.SetExtent(2_000.0, 500.0);
        h.ScrollTo(100.0, ScrollMove.Immediate);
        h.ScrollTo(5_000.0);

        now = 1.05;
        double before = h.OffsetNow;
        h.SetExtent(3_000.0, 500.0);                     // a list still filling: max 2500, the target still out of reach
        Assert.True(h.RestorePending);
        Assert.Equal(SegKind.Glide, h.Plan.S0.Kind);
        Assert.Equal(2_500.0, h.Plan.Dest);
        Assert.Equal(before, h.OffsetNow, 6);            // re-aimed from where it is, no step

        now = 1.10;
        before = h.OffsetNow;
        h.SetExtent(6_000.0, 500.0);                     // now the target fits: the latch resolves as a glide
        Assert.False(h.RestorePending);
        Assert.Equal(SegKind.Glide, h.Plan.S0.Kind);
        Assert.Equal(5_000.0, h.Plan.Dest);
        Assert.Equal(before, h.OffsetNow, 6);
    }
}
