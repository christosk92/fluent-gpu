using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A viewport that mounts while every <see cref="PlanSlots"/> slot is live (parked KeepAlive pages keep theirs) must not
/// be left "bound" to no slot: it stays unbound, so the host's ResolveScrollHandle retries the bind, and a move requested
/// meanwhile lands once a slot frees.
/// </summary>
public sealed class ScrollHandleSlotExhaustionTests
{
    [Fact]
    public void Bind_WithEveryPlanSlotLive_StaysUnboundAndBindsOnceASlotFrees()
    {
        var slots = new PlanSlots();
        for (int i = 0; i < PlanSlots.Capacity; i++)
            Assert.True(slots.Allocate(new ScrollViewportId(100 + i, 1), ScrollPlan.Idle(100 + i, 0.0, 0.0, 10.0)));

        var vp = new ScrollViewportId(500, 1);
        var handle = new ScrollHandle();
        handle.Bind(slots, vp, static () => 0.0, horizontal: false);
        Assert.False(handle.IsBound);   // bound to no slot, the host never retried
        Assert.False(slots.IsLive(vp));

        handle.SetExtent(1000.0, 100.0);
        handle.ScrollTo(300.0, ScrollMove.Immediate);   // requested while starved: latched, not lost

        slots.Release(new ScrollViewportId(100, 1));   // a parked page is evicted
        handle.Bind(slots, vp, static () => 0.0, horizontal: false);   // the host's ResolveScrollHandle retry
        Assert.True(handle.IsBound);
        Assert.True(slots.IsLive(vp));

        handle.SetExtent(1000.0, 100.0);
        Assert.Equal(300.0, handle.OffsetNow, 3);
        handle.ScrollTo(500.0, ScrollMove.Immediate);   // and the viewport scrolls normally afterwards
        Assert.Equal(500.0, handle.OffsetNow, 3);
    }
}
