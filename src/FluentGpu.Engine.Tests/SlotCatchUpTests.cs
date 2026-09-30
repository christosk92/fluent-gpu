using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The render loop's catch-up policy in isolation (<see cref="SlotCatchUp"/>, pure). A clock-paced turn whose present slot
/// is still busy past the grace asks it whether to skip the tick (the previous present missed its vblank and owns this
/// one) or to wait as before. It skips only while frames FIT the early phase (smoothed render work + GPU ≤ 0.7 of the
/// refresh — skipping over-budget frames would halve the rate), and backs off for a second when a catch-up did not hold
/// (a busy slot again within two ticks). The render-loop behaviour built on it is RenderThreadPacingTests.
/// </summary>
public sealed class SlotCatchUpTests
{
    private const double Refresh120 = 8.333;

    private static SlotCatchUp Seeded(double costMs)
    {
        var p = new SlotCatchUp();
        p.Observe(1, costMs - 1);      // work 1 + GPU (cost − 1) = cost
        return p;
    }

    [Fact]
    public void AnUnseededPolicy_NeverSkips()
    {
        var p = new SlotCatchUp();
        Assert.False(p.ShouldSkip(10, Refresh120));   // no frame observed yet: nothing says the early phase can hold
        Assert.False(p.BackingOff(10));
    }

    [Fact]
    public void CheapFrames_SkipTheBusyTick()
    {
        var p = Seeded(3.0);                          // 3 ms of an 8.33 ms refresh
        Assert.Equal(3.0, p.CostEmaMs, 9);
        Assert.True(p.ShouldSkip(10, Refresh120));
    }

    [Fact]
    public void FramesOverTheFitFraction_KeepTheWait()
    {
        var p = Seeded(6.5);                          // > 0.7 × 8.333 = 5.83 ms: the early phase cannot hold
        Assert.False(p.ShouldSkip(10, Refresh120));
        Assert.False(p.BackingOff(10));               // declining an over-budget skip is not a failed catch-up
    }

    [Fact]
    public void TheFitBoundary_IsInclusive()
    {
        Assert.True(Seeded(7.0).ShouldSkip(10, 10.0));     // exactly 0.7 of the refresh still fits
        Assert.False(Seeded(7.01).ShouldSkip(10, 10.0));
    }

    [Fact]
    public void TheCostIsAnEma_OfWorkPlusGpu()
    {
        var p = new SlotCatchUp();
        p.Observe(1.5, 1.5);                          // 3 ms seeds it outright
        Assert.Equal(3.0, p.CostEmaMs, 9);
        p.Observe(3.0, 5.0);                          // 8 ms: 3 + 0.2 × (8 − 3) = 4
        Assert.Equal(4.0, p.CostEmaMs, 9);
    }

    [Fact]
    public void ACatchUpThatDoesNotHold_BacksOffForBackoffTicks()
    {
        var p = Seeded(3.0);
        Assert.True(p.ShouldSkip(10, Refresh120));    // catch-up on tick 10
        Assert.False(p.ShouldSkip(12, Refresh120));   // busy again two ticks later: the early phase did not hold
        Assert.True(p.BackingOff(12));
        Assert.True(p.BackingOff(131));
        Assert.False(p.ShouldSkip(131, Refresh120));  // still inside the window (12 + 120 = 132, exclusive)
        Assert.False(p.BackingOff(132));
        Assert.True(p.ShouldSkip(133, Refresh120));   // the window is over and frames are still cheap: try again
    }

    [Fact]
    public void ACatchUpFollowedByABusySlotBeyondHoldTicks_Held()
    {
        var p = Seeded(3.0);
        Assert.True(p.ShouldSkip(10, Refresh120));
        Assert.True(p.ShouldSkip(10 + SlotCatchUp.HoldTicks + 1, Refresh120));   // a new late frame, not a failed hold
        Assert.False(p.BackingOff(10 + SlotCatchUp.HoldTicks + 1));
    }

    [Fact]
    public void Break_ClearsTheBackoff_AndKeepsTheCost()
    {
        var p = Seeded(3.0);
        Assert.True(p.ShouldSkip(10, Refresh120));
        Assert.False(p.ShouldSkip(12, Refresh120));
        Assert.True(p.BackingOff(20));
        p.Break();                                    // motion ended: the next run starts clean
        Assert.False(p.BackingOff(20));
        Assert.Equal(3.0, p.CostEmaMs, 9);            // the cost EMA survives the break
        Assert.True(p.ShouldSkip(20, Refresh120));    // seeded and cheap, no phase history
    }

    [Fact]
    public void AnUnpacedTick_NeverSkips()
    {
        var p = Seeded(3.0);
        Assert.False(p.ShouldSkip(0, Refresh120));    // tickSeq 0 = no display clock: the tick rule does not apply
    }

    [Theory]
    [InlineData(8.333, 2)]                            // 120 Hz: 1.25 ms rounds up to 2
    [InlineData(16.667, 3)]                           // 60 Hz: 2.5 ms rounds up to 3
    [InlineData(4.1667, 1)]                           // 240 Hz: 0.625 ms, never below 1
    public void GraceMs_IsTheGraceFractionOfTheRefresh_RoundedUpToWholeMs(double refreshMs, int expected)
    {
        Assert.Equal(expected, SlotCatchUp.GraceMs(refreshMs));
    }
}
