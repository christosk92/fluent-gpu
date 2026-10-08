using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The presented-flow walk the recorder, hit-testing and PresentedAbsoluteRect share (FlowCursor).</summary>
public sealed class FlowCursorTests
{
    [Fact]
    public void AtRestTheCursorIsInactive()
    {
        var p = default(NodePaint);
        Assert.False(FlowCursor.For(in p).Active);
    }

    [Fact]
    public void AColumnShiftsEachChildByItsEarlierSiblingsDeltas()
    {
        var p = new NodePaint { FlowBits = NodePaint.FlowShiftsBit };
        var c = FlowCursor.For(in p);
        Assert.Equal(0f, c.Step(0, 0f, -50f, out _, out _));
        Assert.Equal(-50f, c.Step(1, 100f, 0f, out _, out _));
        Assert.Equal(-50f, c.Step(2, 130f, -10f, out _, out _));
        Assert.Equal(-60f, c.Step(3, 160f, 0f, out float clipTop, out _));
        Assert.True(float.IsNaN(clipTop));
    }

    [Fact]
    public void AnExitOrphanPushesTheChildrenLaidOutAtItsTop()
    {
        var p = new NodePaint { FlowBits = NodePaint.FlowShiftsBit | NodePaint.FlowOrphanBit, FlowOrphanTop = 40f, FlowOrphanDelta = 70f };
        var c = FlowCursor.For(in p);
        Assert.Equal(0f, c.Step(0, 0f, 0f, out _, out _));    // the row above the closing drawer
        Assert.Equal(70f, c.Step(1, 40f, 0f, out _, out _));  // laid out where the drawer was: still pushed down
        Assert.Equal(70f, c.Step(2, 70f, 0f, out _, out _));
    }

    [Fact]
    public void ABandClipsItsRowsToThePresentedHeightAndShiftsTheSuffix()
    {
        var bands = new RevealBands();
        bands.Set(0, new RevealBand { First = 1, Count = 2, Top = 40f, Extent = 80f, Presented = 20f, Opening = true });
        var c = FlowCursor.For(default(NodePaint));
        c.SetBands(in bands, 0b1, prefix: 0, firstRealized: 0);
        Assert.True(c.Active);
        Assert.Equal(0f, c.Step(0, 0f, 0f, out float t0, out _));
        Assert.True(float.IsNaN(t0));
        Assert.Equal(0f, c.Step(1, 40f, 0f, out float t1, out float b1));
        Assert.Equal(40f, t1);
        Assert.Equal(60f, b1);
        c.Step(2, 80f, 0f, out _, out _);
        Assert.Equal(-60f, c.Step(3, 120f, 0f, out float t3, out _));
        Assert.True(float.IsNaN(t3));
    }

    [Fact]
    public void TwoBandsComposeInLogicalOrderWhateverTheirSlots()
    {
        var bands = new RevealBands();
        bands.Set(2, new RevealBand { First = 0, Count = 1, Top = 0f, Extent = 30f, Presented = 10f });    // −20
        bands.Set(0, new RevealBand { First = 2, Count = 1, Top = 60f, Extent = 30f, Presented = 0f });    // −30
        var c = FlowCursor.For(default(NodePaint));
        c.SetBands(in bands, 0b101, prefix: 0, firstRealized: 0);
        c.Step(0, 0f, 0f, out _, out _);
        Assert.Equal(-20f, c.Step(1, 30f, 0f, out _, out _));
        Assert.Equal(-20f, c.Step(2, 60f, 0f, out float clipTop, out float clipBottom));
        Assert.Equal(40f, clipTop);
        Assert.Equal(40f, clipBottom);
        Assert.Equal(-50f, c.Step(3, 90f, 0f, out _, out _));
    }
}
