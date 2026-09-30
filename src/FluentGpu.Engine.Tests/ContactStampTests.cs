using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The composition-timed contact stamp (<see cref="ContactStamp.ForFrame"/>, scroll-jitter plan A.1): a sample
/// produced on UI frame k is stamped with the present of the first render turn guaranteed to see it — the NEXT tick's
/// present, <c>PresentQpc + RefreshQpc</c> — on every clock the producers see (a paced real-window clock at either
/// present-queue depth, the deterministic headless clock), independent of when the UI ran; with no known refresh it is
/// the producer's own now. Pure arithmetic over <see cref="RefreshLattice"/>-built clocks.</summary>
public sealed class ContactStampTests
{
    private const long Refresh = 83_333;   // 120 Hz at a 10 MHz QPC

    [Fact]
    public void PacedClock_StampsTheNextTicksPresent()
    {
        long tick = 1_000;
        var clock = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: Refresh, nowQpc: tick + 2_000,
                                         lastFrameQpc: 0, seq: 1);
        Assert.Equal(tick + 2 * Refresh, clock.PresentQpc);   // depth 1: the vblank after next
        Assert.Equal(tick + 3 * Refresh, ContactStamp.ForFrame(in clock, clock.NowQpc));
    }

    [Fact]
    public void PacedClock_AtDepthTwo_StampsTheNextTicksPresentUnderTheSameLaw()
    {
        long tick = 5_000_000;
        var clock = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: Refresh, nowQpc: tick + 1_000,
                                         lastFrameQpc: 0, seq: 1, maxFrameLatency: 2);
        Assert.Equal(tick + 3 * Refresh, clock.PresentQpc);
        Assert.Equal(tick + 4 * Refresh, ContactStamp.ForFrame(in clock, clock.NowQpc));
    }

    /// <summary>The rule's point: frame k's stamp IS the present render tick k+1 poses at (<c>RenderPresentSec</c> —
    /// <c>tick + (1 + depth)·refresh</c>), so a render tick always poses exactly on the previous frame's sample.</summary>
    [Fact]
    public void FrameKsStamp_IsTheNextFramesPresent()
    {
        long tick = 7_000_000;
        var k = RefreshLattice.Build(true, tick, Refresh, tick + 300, lastFrameQpc: 0, seq: 1);
        var next = RefreshLattice.Build(true, tick + Refresh, Refresh, tick + Refresh + 4_000, lastFrameQpc: k.FrameQpc, seq: 2);
        Assert.Equal(next.PresentQpc, ContactStamp.ForFrame(in k, k.NowQpc));
    }

    /// <summary>The UI writes a frame's sample anywhere within its interval: the stamp is a function of the frame's tick,
    /// never of the producer's read of now (the old rule, <c>now + round(clamp(PresentQpc − now))</c>, moved with it).</summary>
    [Fact]
    public void PacedStamp_DoesNotDependOnWhenTheProducerRan()
    {
        long tick = 9_000_000;
        var clock = RefreshLattice.Build(true, tick, Refresh, tick + 100, lastFrameQpc: 0, seq: 1);
        long early = ContactStamp.ForFrame(in clock, tick + 100);
        long late = ContactStamp.ForFrame(in clock, tick + Refresh - 1);
        Assert.Equal(early, late);
        Assert.Equal(tick + 3 * Refresh, early);
    }

    [Fact]
    public void NoKnownRefresh_StampsNow()
    {
        const long now = 777_777;
        // No display clock and no measured refresh: the lattice builder leaves RefreshQpc 0.
        var noClock = RefreshLattice.Build(tickAvailable: false, tickQpc: 0, refreshQpc: 0, nowQpc: now, lastFrameQpc: 0, seq: 1);
        Assert.Equal(0L, noClock.RefreshQpc);
        Assert.Equal(now, ContactStamp.ForFrame(in noClock, now));
        Assert.Equal(now, ContactStamp.ForFrame(default(FrameClock), now));
    }

    [Fact]
    public void HeadlessClock_StampsFramePlusTwoRefreshes()
    {
        long frame = 12_345_678;
        var clock = RefreshLattice.Headless(frame, Refresh, seq: 3);
        Assert.Equal(frame + Refresh, clock.PresentQpc);   // headless present: one refresh on
        Assert.Equal(frame + 2 * Refresh, ContactStamp.ForFrame(in clock, clock.NowQpc));
        // ...which is the next headless frame's present — the same one-tick-on relation as a real window.
        var next = RefreshLattice.Headless(frame + Refresh, Refresh, seq: 4);
        Assert.Equal(next.PresentQpc, ContactStamp.ForFrame(in clock, clock.NowQpc));
    }
}
