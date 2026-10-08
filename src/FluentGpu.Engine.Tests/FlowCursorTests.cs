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
}
