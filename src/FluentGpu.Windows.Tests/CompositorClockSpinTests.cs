using System.Diagnostics;
using System.Threading;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The compositor-clock waiter must never spin a core on a wait that does not block. Measured on a second Wavee
/// instance: <c>DCompositionWaitForCompositorClock</c> returned ~2,300 times every 2 ms, every return but one per tick was
/// an ignored "burst" return, and the waiter re-entered the export at once — the render clock showed
/// <c>ignored=1083794</c> and the process held ~1.1 cores with nothing animating.</summary>
public sealed class CompositorClockSpinTests
{
    private const uint WaitObject0 = 0;

    [Fact]
    public void A_wait_that_never_blocks_switches_to_synthesized_display_rate_ticks_instead_of_spinning()
    {
        long calls = 0;
        using var clock = new Win32CompositorClock(waitForClock: () => { Interlocked.Increment(ref calls); return WaitObject0; },
            refreshPeriodHintQpc: Stopwatch.Frequency / 120);
        clock.Arm();
        Thread.Sleep(600);                                    // detection (≤ one 250 ms window) + synthesized ticks
        long calls0 = Interlocked.Read(ref calls), ticks0 = clock.TickSeq;
        Thread.Sleep(1000);
        long calls1 = Interlocked.Read(ref calls), ticks1 = clock.TickSeq;
        Assert.True(clock.IsAvailable);
        // synthesized: the export is tried at most once per probe interval, never re-entered in a loop
        Assert.True(calls1 - calls0 <= 2, $"export calls in 1 s of synthesized mode = {calls1 - calls0}");
        // and the beat is the window's 120 Hz, not the ~64 Hz a 15.6 ms timer would give, nor a spin
        long ticks = ticks1 - ticks0;
        Assert.InRange(ticks, 100, 130);
    }

    [Fact]
    public void A_blocking_clock_stays_available_and_ticks()
    {
        using var clock = new Win32CompositorClock(waitForClock: () => { Thread.Sleep(8); return WaitObject0; },
            refreshPeriodHintQpc: Stopwatch.Frequency / 120);
        clock.Arm();
        Thread.Sleep(400);
        Assert.True(clock.IsAvailable);
        Assert.True(clock.TickSeq > 10, $"ticks = {clock.TickSeq}");
    }

    [Fact]
    public void A_short_return_storm_is_absorbed_without_latching()
    {
        // The documented noise: around a monitor change a few sub-millisecond returns follow a real tick.
        int n = 0;
        using var clock = new Win32CompositorClock(waitForClock: () =>
        {
            int k = Interlocked.Increment(ref n);
            if (k % 21 == 0) Thread.Sleep(8);   // one real (blocking) tick, then a 20-return storm
            return WaitObject0;
        }, refreshPeriodHintQpc: Stopwatch.Frequency / 120);
        clock.Arm();
        Thread.Sleep(400);
        Assert.True(clock.IsAvailable);
        Assert.True(clock.TickSeq > 10, $"ticks = {clock.TickSeq}");
    }
}
