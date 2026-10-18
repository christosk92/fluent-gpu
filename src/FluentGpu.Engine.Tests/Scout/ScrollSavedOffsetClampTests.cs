using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A pane resting at its end whose viewport grows a few DIP for a moment (a crossing's cross-fade, a head that eases) is clamped to the new end, and a pane
/// torn down in that moment used to be remembered at the clamp: the layout that returned to its old size restored it a few DIP short. The handle keeps the place
/// it was clamped from; any move of the reader's or the app's ends that.
/// </summary>
public sealed class ScrollSavedOffsetClampTests
{
    [Fact]
    public void AClampedRestingPlan_IsSavedWhereItWasLeft_AndAMoveForgetsIt()
    {
        double now = 0.0;
        var handle = new ScrollHandle(new PlanSlots(), new ScrollViewportId(7, 1), () => now);
        handle.SetExtent(264.8, 201.0);                      // max 63.8
        handle.ScrollTo(63.8, ScrollMove.Immediate);
        Settle(handle, ref now, 264.8, 201.0);
        Assert.Equal(63.8, handle.SavedOffset, 3);

        handle.SetExtent(264.8, 203.0);                      // the viewport grew 2: max 61.8, the resting plan is clamped
        Settle(handle, ref now, 264.8, 203.0);
        Assert.Equal(61.8, handle.OffsetNow, 3);
        Assert.Equal(63.8, handle.SavedOffset, 3);           // the place it was left, not the transient clamp

        handle.ScrollTo(20.0, ScrollMove.Immediate);         // the reader moves: nothing outstanding
        Settle(handle, ref now, 264.8, 203.0);
        Assert.Equal(20.0, handle.SavedOffset, 3);
    }

    private static void Settle(ScrollHandle handle, ref double now, double extent, double viewport)
    {
        for (int f = 0; f < 120; f++)
        {
            now += 1.0 / 60.0;
            handle.SetExtent(extent, viewport);
            handle.SettleIfDue(now);
        }
    }

    [Fact]
    public void AnUnclampedPlan_IsSavedWhereItIsShown()
    {
        double now = 0.0;
        var handle = new ScrollHandle(new PlanSlots(), new ScrollViewportId(8, 1), () => now);
        handle.SetExtent(1_000.0, 500.0);
        handle.ScrollTo(120.0, ScrollMove.Immediate);
        Assert.Equal(120.0, handle.SavedOffset, 3);
    }
}
