using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The attested present ledger: DXGI's PAIRED counters only (PresentCount / PresentRefreshCount describe the
/// same last-displayed present), idle vblanks banked by present id and credited when that present is displayed, and the
/// missed-motion-tick run the render thread charges. Pure: no device.</summary>
public sealed class PresentStatisticsLedgerTests
{
    /// <summary>A sample lagging the submit by one present (stale), then catching up, is neither a drop nor a repeat —
    /// the lagging displayed count is never compared against a submitted count.</summary>
    [Fact]
    public void StatsLaggingOnePresentIsNotADrop()
    {
        var l = new PresentStatisticsLedger();
        l.Observe(true, presentCount: 100, presentRefreshCount: 1000, lastPresentId: 101, idleRefreshes: 0);
        l.Observe(true, 101, 1001, 102, 0);
        l.Observe(true, 101, 1001, 103, 0);   // stale sample
        l.Observe(true, 103, 1003, 104, 0);   // caught up
        Assert.Equal(3, l.PresentsDisplayed);
        Assert.Equal(0, l.PresentsDropped);
        Assert.Equal(0, l.VblanksRepeated);
    }

    /// <summary>The first present after an idle second carries 119 idle vblanks. The sample taken right after it still
    /// shows the previous present (lag); the one that finally shows it displayed 120 vblanks later is credited the idle
    /// vblanks banked under its id — no repeat, even though that sample's own submit had no idle gap.</summary>
    [Fact]
    public void IdleVblanksAreCreditedWhenTheLaggingSampleShowsThePresent()
    {
        var l = new PresentStatisticsLedger();
        l.Observe(true, 100, 1000, 101, 0);
        l.Observe(true, 101, 1001, 102, 0);
        l.Observe(true, 102, 1002, 103, 119);   // submitted after 1 s idle; the sample still shows 102
        l.Observe(true, 103, 1122, 104, 0);     // 103 displayed 120 vblanks after 102
        l.Observe(true, 104, 1123, 105, 0);
        Assert.Equal(4, l.PresentsDisplayed);
        Assert.Equal(0, l.PresentsDropped);
        Assert.Equal(0, l.VblanksRepeated);
    }

    /// <summary>A present submitted in time (no idle gap) but shown one vblank late IS a display repeat.</summary>
    [Fact]
    public void ALateLatchIsOneRepeat()
    {
        var l = new PresentStatisticsLedger();
        l.Observe(true, 100, 1000, 101, 0);
        l.Observe(true, 101, 1001, 102, 0);
        l.Observe(true, 102, 1003, 103, 0);   // 102 reached the glass two vblanks after 101
        Assert.Equal(2, l.PresentsDisplayed);
        Assert.Equal(1, l.VblanksRepeated);
        Assert.Equal(0, l.PresentsDropped);
    }

    /// <summary>Two present ids retired on one vblank: one was superseded before display — the genuine silent drop.</summary>
    [Fact]
    public void TwoPresentsOnOneVblankIsOneDrop()
    {
        var l = new PresentStatisticsLedger();
        l.Observe(true, 100, 1000, 101, 0);
        l.Observe(true, 102, 1001, 103, 0);
        Assert.Equal(1, l.PresentsDisplayed);
        Assert.Equal(1, l.PresentsDropped);
        Assert.Equal(0, l.VblanksRepeated);
    }

    /// <summary>A DISJOINT sample resets the baseline: the jump across it counts nothing.</summary>
    [Fact]
    public void DisjointSampleResetsTheBaseline()
    {
        var l = new PresentStatisticsLedger();
        l.Observe(true, 100, 1000, 101, 0);
        l.Observe(false, 0, 0, 102, 0);
        l.Observe(true, 500, 9000, 103, 0);
        l.Observe(true, 501, 9001, 104, 0);
        Assert.Equal(1, l.PresentsDisplayed);
        Assert.Equal(0, l.PresentsDropped);
        Assert.Equal(0, l.VblanksRepeated);
    }

    /// <summary>Before the first present reaches the glass a sample reads zeros: it is no baseline, so the first real
    /// sample (a vblank ordinal counted since boot) is not charged as millions of repeats.</summary>
    [Fact]
    public void ASampleBeforeTheFirstDisplayedPresentIsNoBaseline()
    {
        var l = new PresentStatisticsLedger();
        l.Observe(true, 0, 0, 1, 0);
        l.Observe(true, 1, 17_635_379, 2, 0);
        l.Observe(true, 2, 17_635_380, 3, 0);
        Assert.Equal(1, l.PresentsDisplayed);
        Assert.Equal(0, l.VblanksRepeated);
        Assert.Equal(0, l.PresentsDropped);
    }

    /// <summary>Idle vblanks are counted on the vblank grid: submits early in one interval and late in the next are one
    /// refresh apart (no idle), a skipped interval is one idle vblank, and without an anchor the gap is rounded.</summary>
    [Fact]
    public void IdleRefreshesCountVblankBoundariesBetweenSubmits()
    {
        const long T = 10_000;   // refresh period (QPC ticks); vblanks at 1_000_000 + k·T
        const long V = 1_000_000;
        Assert.Equal(0u, PresentStatisticsLedger.IdleRefreshes(V + 100, V + T + 9_900, V, T));   // adjacent intervals
        Assert.Equal(1u, PresentStatisticsLedger.IdleRefreshes(V + 100, V + 2 * T + 100, V, T)); // one interval skipped
        Assert.Equal(119u, PresentStatisticsLedger.IdleRefreshes(V + 100, V + 120 * T + 100, V, T));
        Assert.Equal(0u, PresentStatisticsLedger.IdleRefreshes(V + 100, V + T + 100, 0, T));      // no anchor: rounded
        Assert.Equal(1u, PresentStatisticsLedger.IdleRefreshes(V + 100, V + 2 * T + 3_000, 0, T));
        Assert.Equal(0u, PresentStatisticsLedger.IdleRefreshes(0, V, V, T));                     // first submit
    }

    /// <summary>The render thread's missed-tick run: consecutive ticks miss nothing, a skipped tick is one, and a pause
    /// in render motion (or an unpaced present) starts a new run so the idle ticks are never charged.</summary>
    [Fact]
    public void MotionTickRunChargesOnlyTicksSkippedWhileMotionIsLive()
    {
        var run = new MotionTickRun();
        Assert.Equal(0, run.Presented(10));
        Assert.Equal(0, run.Presented(11));
        Assert.Equal(1, run.Presented(13));
        run.Break();                              // render motion went idle
        Assert.Equal(0, run.Presented(250));      // the first paced present after idle starts a new run
        Assert.Equal(0, run.Presented(251));
        Assert.Equal(0, run.Presented(0));        // unpaced present ends the run
        Assert.Equal(0, run.Presented(400));
        Assert.Equal(2, run.Presented(403));
    }
}
