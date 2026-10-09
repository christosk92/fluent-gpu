using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A measured list's unrealized rows are estimated taller than they are: an End-key glide (or a fling crossing the end)
/// is authored against the estimate-based max, and the rows it reveals measure smaller, so the extent shrinks under the
/// live motion (the corrections land at or below the anchor: Total shrinks, the plan's frame does not shift). The motion
/// must come to rest at the NEW end: left aimed at the old one it shows a blank band past the last row and never
/// settles, keeping the UI and render loops awake.
/// </summary>
public sealed class ShrinkUnderEndMotionTests
{
    private const double Frame = 1.0 / 60.0;
    private const double Viewport = 500.0;

    [Fact]
    public void EndKeyGlide_ContentShrinksUnderIt_SettlesAtTheNewEnd()
    {
        double now = 0.0;
        var handle = new ScrollHandle(new PlanSlots(), new ScrollViewportId(1, 1), () => now);
        handle.SetExtent(10_000.0, Viewport);   // estimate-based end: max 9500
        handle.Key(now, KeyMove.End);
        RunFrames(handle, ref now, 10_000.0, frames: 6);

        double before = handle.OffsetNow;
        handle.SetExtent(9_000.0, Viewport);   // the revealed rows measured smaller: max 8500
        Assert.Equal(before, handle.OffsetNow, 6);   // re-aimed from where it is, no jump

        RunFrames(handle, ref now, 9_000.0, frames: 120);
        Assert.Equal(MotionKind.Idle, handle.Plan.Kind);
        Assert.Equal(8_500.0, handle.OffsetNow, 3);
    }

    [Fact]
    public void FlingAcrossTheEnd_ContentShrinksUnderIt_SettlesAtTheNewEnd()
    {
        double now = 100.0;
        var handle = new ScrollHandle(new PlanSlots(), new ScrollViewportId(2, 1), () => now);
        handle.SetExtent(10_000.0, Viewport);
        handle.ScrollTo(8_000.0, ScrollMove.Immediate);
        handle.ContactBegin(now, 8_000.0);
        for (int i = 1; i <= 6; i++)
        {
            now += 0.008;
            handle.ContactSample(now, 8_000.0 + i * 40.0);   // 5000 DIP/s toward the end
        }
        handle.ContactEnd(now);
        Assert.Equal(MotionKind.Fling, handle.Plan.Kind);
        Assert.Equal(9_500.0, handle.Plan.Dest, 6);   // the edge spring is aimed at the authoring-time max
        RunFrames(handle, ref now, 10_000.0, frames: 6);

        double before = handle.OffsetNow;
        handle.SetExtent(9_300.0, Viewport);   // max 8800: the coast has not reached it yet
        Assert.Equal(before, handle.OffsetNow, 6);
        Assert.Equal(MotionKind.Fling, handle.Plan.Kind);   // still the user's coast, aimed at the new end

        RunFrames(handle, ref now, 9_300.0, frames: 240);
        Assert.Equal(MotionKind.Idle, handle.Plan.Kind);
        Assert.Equal(8_800.0, handle.OffsetNow, 3);
    }

    /// <summary>The host's frame step for one viewport: re-publish the laid-out extent, then the settle rule.</summary>
    private static void RunFrames(ScrollHandle handle, ref double now, double extent, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            now += Frame;
            handle.SetExtent(extent, Viewport);
            handle.SettleIfDue(now);
        }
    }
}
