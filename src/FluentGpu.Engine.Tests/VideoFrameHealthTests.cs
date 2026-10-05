using System.Collections.Generic;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F066 / F198: the pure rules the clear engine (<c>VideoMediaEngine</c>) and the protected runtime's native twin
/// (<c>FrameHealthPolicy.h</c>, exercised by FeedTests) share. Rendered/dropped counters are accumulated across the resets Media
/// Foundation applies after a flush, the rendered-frame watch reports a hang exactly once and only for a source that is really
/// playing, and the swap-chain handle ledger closes every handle the engine took in exactly once, never while it is current and
/// never before it has waited out its grace. Headless: the "closer" is a recorder.
/// </summary>
public sealed class VideoFrameHealthTests
{
    // ── VideoFrameCounters ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Counters_AccumulateTheDeltaOfEachReading()
    {
        var c = new VideoFrameCounters();
        c.Observe(30, 1);
        c.Observe(75, 1);
        c.Observe(120, 4);

        Assert.Equal(120, c.Rendered);
        Assert.Equal(4, c.Dropped);
    }

    [Fact]
    public void Counters_KeepThePreviousRunsTotal_WhenMfResetsThemAfterAFlush()
    {
        var c = new VideoFrameCounters();
        c.Observe(100, 5);

        c.Observe(12, 0);   // a seek flushed the engine: both counters restarted from zero, and the new reading IS the delta
        Assert.Equal(112, c.Rendered);
        Assert.Equal(5, c.Dropped);

        c.Observe(40, 2);
        Assert.Equal(140, c.Rendered);
        Assert.Equal(7, c.Dropped);
    }

    [Fact]
    public void Counters_TreatEitherCounterGoingBackwards_AsAResetOfBoth()
    {
        var c = new VideoFrameCounters();
        c.Observe(100, 10);

        c.Observe(130, 3);   // rendered rose, dropped fell: the engine restarted both, so BOTH readings are fresh deltas

        Assert.Equal(230, c.Rendered);
        Assert.Equal(13, c.Dropped);
    }

    [Fact]
    public void Counters_Rebase_CountsNothingTheWarmEngineAlreadyCarried()
    {
        var c = new VideoFrameCounters();
        c.Observe(500, 20);

        c.Rebase(500, 20);   // the next source reuses the warm engine, whose counters still hold the previous source's totals
        Assert.Equal(0, c.Rendered);
        Assert.Equal(0, c.Dropped);

        c.Observe(520, 21);
        Assert.Equal(20, c.Rendered);
        Assert.Equal(1, c.Dropped);

        c.Observe(8, 0);     // ...and MF's own reset once the new source flushes in is still folded as a restart
        Assert.Equal(28, c.Rendered);
    }

    [Fact]
    public void Counters_Reset_ForgetsEverything()
    {
        var c = new VideoFrameCounters();
        c.Observe(100, 5);

        c.Reset();
        c.Observe(7, 1);

        Assert.Equal(7, c.Rendered);
        Assert.Equal(1, c.Dropped);
    }

    // ── RenderedFrameWatch ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Watch_ReportsAHang_ExactlyOnce_AfterTheTimeoutOfPlayingWithNoRenderedFrame()
    {
        var w = new RenderedFrameWatch();
        long t = 1_000;
        Assert.False(w.Observe(t, playing: true, rendered: 0));                       // the window opens
        Assert.False(w.Observe(t + RenderedFrameWatch.DefaultTimeoutMs - 1, true, 0));
        Assert.True(w.Observe(t + RenderedFrameWatch.DefaultTimeoutMs, true, 0));     // elapsed with still no frame
        Assert.False(w.Observe(t + RenderedFrameWatch.DefaultTimeoutMs * 3, true, 0)); // once, not every turn after
    }

    [Fact]
    public void Watch_IsOver_TheMomentAnyFrameRendered_SoAStaticPictureNeverTrips()
    {
        var w = new RenderedFrameWatch();
        Assert.False(w.Observe(0, true, 0));
        Assert.False(w.Observe(5_000, true, 1));                                       // one frame: the watch is done for this source
        Assert.False(w.Observe(60_000, true, 1));
    }

    [Fact]
    public void Watch_OnlyCountsTimeSpentPlaying_AndRestartsTheWindowWhenPlayResumes()
    {
        var w = new RenderedFrameWatch();
        Assert.False(w.Observe(0, true, 0));
        Assert.False(w.Observe(8_000, true, 0));
        Assert.False(w.Observe(9_000, playing: false, rendered: 0));   // paused / starved / seeking: not counting
        Assert.False(w.Observe(30_000, false, 0));
        Assert.False(w.Observe(30_500, true, 0));                      // resumed: a fresh window from here
        Assert.False(w.Observe(30_500 + RenderedFrameWatch.DefaultTimeoutMs - 1, true, 0));
        Assert.True(w.Observe(30_500 + RenderedFrameWatch.DefaultTimeoutMs, true, 0));
    }

    [Fact]
    public void Watch_Restart_GivesAResizedStreamAFreshWindow_ButOnlyWhileCounting()
    {
        var w = new RenderedFrameWatch();
        w.Restart(500);                                                // not counting yet: a no-op, not a way to start the clock
        Assert.False(w.Observe(1_000, true, 0));
        Assert.False(w.Observe(9_000, true, 0));
        w.Restart(9_000);                                              // UpdateVideoStream re-created the swap chain
        Assert.False(w.Observe(9_000 + RenderedFrameWatch.DefaultTimeoutMs - 1, true, 0));
        Assert.True(w.Observe(9_000 + RenderedFrameWatch.DefaultTimeoutMs, true, 0));
    }

    [Fact]
    public void Watch_Reset_ArmsItAgainForTheNextSource()
    {
        var w = new RenderedFrameWatch();
        Assert.False(w.Observe(0, true, 0));
        Assert.True(w.Observe(RenderedFrameWatch.DefaultTimeoutMs, true, 0));

        w.Reset();

        Assert.False(w.Observe(20_000, true, 0));
        Assert.True(w.Observe(20_000 + RenderedFrameWatch.DefaultTimeoutMs, true, 0));
    }

    [Fact]
    public void Watch_NeverTrips_WhileTheStatisticsAreUnreadable_AndOnlyOpensItsWindowOnceTheyAre()
    {
        // The engines feed `playing && statsReadable`: a renderer whose GetStatistics fails or returns VT_EMPTY is never judged a hang.
        var w = new RenderedFrameWatch();
        for (long t = 0; t <= RenderedFrameWatch.DefaultTimeoutMs * 5; t += 500) Assert.False(w.Observe(t, false, 0));

        long readable = RenderedFrameWatch.DefaultTimeoutMs * 5 + 500;
        Assert.False(w.Observe(readable, true, 0));
        Assert.False(w.Observe(readable + RenderedFrameWatch.DefaultTimeoutMs - 1, true, 0));
        Assert.True(w.Observe(readable + RenderedFrameWatch.DefaultTimeoutMs, true, 0));
    }

    // ── SwapchainHandleLedger ────────────────────────────────────────────────────────────────────────────────────────

    private static SwapchainHandleLedger NewLedger(List<nuint> closed, long graceMs = 2000)
        => new(h => closed.Add(h), graceMs);

    [Fact]
    public void Ledger_NeverClosesTheCurrentHandle()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed);

        l.Adopt(0x10, nowMs: 0);
        l.Sweep(1_000_000);                  // however long it has been current, a device recovery may still bind it

        Assert.Empty(closed);
        Assert.Equal((nuint)0x10, l.Current);
    }

    [Fact]
    public void Ledger_ARebindClosesTheSupersededHandle_OneReplacementLater()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed);

        l.Adopt(0x10, 0);
        l.Adopt(0x20, 100);                  // FORMATCHANGE: 0x10 is retired, not closed (the render thread may still be binding it)
        Assert.Empty(closed);
        Assert.Equal((nuint)0x10, l.Retired);

        l.Adopt(0x30, 200);                  // one replacement later 0x10 is finally closed
        Assert.Equal(new nuint[] { 0x10 }, closed.ToArray());
        Assert.Equal((nuint)0x20, l.Retired);
        Assert.Equal((nuint)0x30, l.Current);
        Assert.Equal(2, l.OpenCount);
    }

    [Fact]
    public void Ledger_ARetiredHandle_IsClosedOnceItsGraceElapsed()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed, graceMs: 2000);
        l.Adopt(0x10, 0);
        l.Adopt(0x20, 500);

        l.Sweep(500 + 1_999);
        Assert.Empty(closed);
        l.Sweep(500 + 2_000);

        Assert.Equal(new nuint[] { 0x10 }, closed.ToArray());
        Assert.Equal(0u, (uint)l.Retired);
        Assert.Equal((nuint)0x20, l.Current);
    }

    [Fact]
    public void Ledger_ASourceChange_RetiresTheCurrentHandle_AndTheNextOneClosesIt()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed);
        l.Adopt(0x10, 0);

        l.Retire(100);                       // SetSource: the old source's handle describes nothing now
        Assert.Equal(0u, (uint)l.Current);
        Assert.Empty(closed);

        l.Adopt(0x20, 150);                  // the new source's handle
        Assert.Empty(closed);                // 0x10 waits out its grace: the render thread may not have unbound it yet
        l.Sweep(100 + SwapchainHandleLedger.DefaultGraceMs);
        Assert.Equal(new nuint[] { 0x10 }, closed.ToArray());
    }

    [Fact]
    public void Ledger_ARepeatOfTheCurrentValue_IsNotASecondReference()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed);

        l.Adopt(0x10, 0);
        l.Adopt(0x10, 50);                   // MF handed back the identical handle: nothing new to own or close
        l.Adopt(0, 60);                      // a failed query adopts nothing

        Assert.Empty(closed);
        Assert.Equal(1, l.OpenCount);
        l.CloseAll();
        Assert.Equal(new nuint[] { 0x10 }, closed.ToArray());   // closed exactly once
    }

    [Fact]
    public void Ledger_AnIdenticalRetiredValue_IsReinstated_NotClosed()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed);
        l.Adopt(0x10, 0);
        l.Adopt(0x20, 10);                   // 0x10 retired

        l.Adopt(0x10, 20);                   // MF returned 0x10 again: it was never superseded

        Assert.Empty(closed);
        Assert.Equal((nuint)0x10, l.Current);
        Assert.Equal((nuint)0x20, l.Retired);
    }

    [Fact]
    public void Ledger_CloseAll_ClosesEveryHandleItHoldsExactlyOnce()
    {
        var closed = new List<nuint>();
        var l = NewLedger(closed);
        l.Adopt(0x10, 0);
        l.Adopt(0x20, 10);
        l.Adopt(0x30, 20);                   // 0x10 closed by the replacement, 0x20 retired, 0x30 current
        Assert.Equal(new nuint[] { 0x10 }, closed.ToArray());

        l.CloseAll();
        l.CloseAll();                        // idempotent: the engine's dispose may run it twice

        Assert.Equal(new nuint[] { 0x10, 0x20, 0x30 }, closed.ToArray());
        Assert.Equal(0, l.OpenCount);
    }
}
