using FluentGpu.Hosting;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Pure arithmetic gates for <see cref="RefreshLattice.Build"/>: fresh-tick stamping, the stale-tick
/// re-snap onto the lattice (Chromium's SnappedToNextTick — see the method's own doc), the never-rewind clamp, the
/// no-clock software-pace fallback, and the present-latency scaling. Mirrors
/// FluentGpu.VerticalSlice/Suites/ScrollSuite.cs's gate.pace.frame-clock-from-tick headless gate at the unit level.</summary>
public sealed class RefreshLatticeTests
{
    private const long RefreshQpc = 10_000;

    [Fact]
    public void FreshTick_StampsTheTickInstant_AndIsLatticeValid()
    {
        long tick = 1_000_000;
        long now = tick + RefreshQpc / 4;   // well inside the 2-refresh staleness window
        var fc = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: 0, seq: 1);

        Assert.Equal(tick, fc.FrameQpc);
        Assert.True((fc.Flags & FrameClockFlags.LatticeValid) != 0);
        Assert.True((fc.Flags & FrameClockFlags.Unpaced) == 0);
    }

    [Fact]
    public void StaleTick_ReSnapsOntoTheLattice_NotLatticeValid_NotUnpaced()
    {
        long tick = 1_000_000;
        // Stale by 3.5 refresh periods: floor((now - tick) / refresh) = 3, so the re-snapped stamp is tick + 3*refresh
        // (the latest lattice point at-or-before now), not `now` itself and not tick + 4*refresh.
        long now = tick + (long)(3.5 * RefreshQpc);
        var fc = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: 0, seq: 2);

        Assert.Equal(tick + 3 * RefreshQpc, fc.FrameQpc);
        Assert.NotEqual(now, fc.FrameQpc);
        Assert.True((fc.Flags & FrameClockFlags.LatticeValid) == 0);
        Assert.True((fc.Flags & FrameClockFlags.Unpaced) == 0);
    }

    [Fact]
    public void FrameQpc_NeverRewindsBelowLastFrameQpc()
    {
        long tick = 1_000_000;
        long now = tick + RefreshQpc / 2;   // fresh tick, but earlier than the last published frame
        long lastFrameQpc = tick + RefreshQpc;
        var fc = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: lastFrameQpc, seq: 3);

        Assert.Equal(lastFrameQpc, fc.FrameQpc);
    }

    [Fact]
    public void StaleTick_ReSnap_AlsoNeverRewindsBelowLastFrameQpc()
    {
        long tick = 1_000_000;
        long now = tick + (long)(3.5 * RefreshQpc);   // re-snaps to tick + 3*refresh
        long lastFrameQpc = tick + 3 * RefreshQpc + RefreshQpc / 2;   // ahead of the re-snapped stamp
        var fc = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: lastFrameQpc, seq: 4);

        Assert.Equal(lastFrameQpc, fc.FrameQpc);
    }

    [Fact]
    public void NoClock_StampsNow_AndIsUnpaced()
    {
        long now = 2_000_000;
        var fc = RefreshLattice.Build(tickAvailable: false, tickQpc: 0, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: 0, seq: 5);

        Assert.Equal(now, fc.FrameQpc);
        Assert.True((fc.Flags & FrameClockFlags.Unpaced) != 0);
        Assert.True((fc.Flags & FrameClockFlags.LatticeValid) == 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PresentQpc_IsFrameQpcPlusOnePlusMaxFrameLatencyRefreshes(int maxFrameLatency)
    {
        long tick = 1_000_000;
        long now = tick + RefreshQpc / 4;
        var fc = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: 0, seq: 6,
            maxFrameLatency: maxFrameLatency);

        Assert.Equal(fc.FrameQpc + (1 + maxFrameLatency) * RefreshQpc, fc.PresentQpc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public void PresentQpc_ClampsMaxFrameLatencyTo1Through3(int maxFrameLatency)
    {
        long tick = 1_000_000;
        long now = tick + RefreshQpc / 4;
        var fc = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: RefreshQpc, nowQpc: now, lastFrameQpc: 0, seq: 7,
            maxFrameLatency: maxFrameLatency);

        int clamped = System.Math.Clamp(maxFrameLatency, 1, 3);
        Assert.Equal(fc.FrameQpc + (1 + clamped) * RefreshQpc, fc.PresentQpc);
    }
}
