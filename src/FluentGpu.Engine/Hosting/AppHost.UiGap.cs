using System.Diagnostics;
using FluentGpu.Foundation;

namespace FluentGpu.Hosting;

// ── the UI-gap decomposition (always-on; F(i) of the 2026-09-25 artist-page RCA) ─────────────────────────────────────
//
// `FrameStats.SlackMs` / `SlackCause` said a scroll-active frame's raw gap had time no phase measured, and named it by
// elimination: no GC and no long requested wait ⇒ "Preempted". That residual cannot tell a thread that was RUNNING
// (a long posted action, a WndProc handler, a blocking call outside the phases) from one the OS did not SCHEDULE, nor
// either from a wait that overran its own timeout. This partial measures the gap directly instead:
//
//   gap = previous Paint end ─► this Paint's frameStart, and inside it, by segment:
//     the loop's WaitForWork   (NoteLoopWait, from the platform loop: wall time inside vs the timeout it asked for)
//     message dispatch         (RunFrame's PumpInto — every WndProc handler)
//     input dispatch           (RunFrame's InputDispatcher.Dispatch)
//     posted actions           (RunFrame's DrainUiPosts)
//     cold maintenance         (RunFrame's RunColdMaintenance)
//     other                    (the remainder: TickDetachedHosts, the loop's own logging, early-out RunFrames)
//   and across all of it the thread's CYCLE delta (ThreadCycles), converted to running ms at a cycles-per-ms rate
//   calibrated (running maximum) over this host's own measured Paint work.
//
// `UiGapClassifier` (pure, tested) names the gap Busy / Blocked / NotScheduled. Cost: two cycle-counter reads and two
// QPC reads per Paint, one QPC pair per measured segment, a handful of long adds — no allocation, no string.

public sealed partial class AppHost
{
    private long _gapPaintEndQpc;
    private ulong _gapPaintEndCycles;
    private double _gapCyclesPerMs;           // running max, calibrated over measured Paint work (ThreadCycles.Calibrate)
    private long _gapWaitTicks, _gapMessagesTicks, _gapInputTicks, _gapPostsTicks, _gapColdTicks;
    private double _gapWaitRequestedMs;
    private bool _gapWaitInfinite;
    private int _gapWaits;
    private ulong _gapPaintStartCycles;
    private UiGapReport _lastGap;

    /// <summary>The platform loop's wait, measured around <c>IPlatformWindow.WaitForWork</c>: its wall span and the timeout
    /// it requested (negative = infinite). UI thread, between RunFrames. Accumulates into the current gap.</summary>
    public void NoteLoopWait(long startQpc, long endQpc, int requestedMs)
    {
        if (endQpc > startQpc) _gapWaitTicks += endQpc - startQpc;
        if (FrameLedger.Enabled) NoteLedgerWait(startQpc, endQpc, requestedMs);
        if (requestedMs < 0) _gapWaitInfinite = true;
        else _gapWaitRequestedMs += requestedMs;
        _gapWaits++;
    }

    /// <summary>Add the wall time since <paramref name="t0"/> to one gap segment; returns now (the next segment's start).</summary>
    private static long GapSegment(ref long acc, long t0)
    {
        long now = Stopwatch.GetTimestamp();
        acc += now - t0;
        return now;
    }

    /// <summary>Paint start (the frame's <c>frameStart</c>): close the gap since the previous Paint's end, classify it into
    /// <see cref="_lastGap"/>, reset the segment accumulators.</summary>
    private void GapPaintStart(long frameStart)
    {
        ulong cycles = ThreadCycles.Read();
        _gapPaintStartCycles = cycles;
        if (_gapPaintEndQpc != 0 && frameStart > _gapPaintEndQpc)
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            float gapMs = (float)((frameStart - _gapPaintEndQpc) * toMs);
            float waitMs = (float)(_gapWaitTicks * toMs), msgMs = (float)(_gapMessagesTicks * toMs),
                  inputMs = (float)(_gapInputTicks * toMs), postsMs = (float)(_gapPostsTicks * toMs),
                  coldMs = (float)(_gapColdTicks * toMs);
            float runMs = ThreadCycles.ToMs(_gapPaintEndCycles, cycles, _gapCyclesPerMs);
            float requested = _gapWaitInfinite ? -1f : (float)_gapWaitRequestedMs;
            var sample = new UiGapSample(gapMs, requested, waitMs, msgMs, inputMs, postsMs, coldMs, runMs);
            UiGapBreakdown b = UiGapClassifier.Classify(in sample);
            _lastGap = new UiGapReport
            {
                GapMs = gapMs, GapWaitRequestedMs = requested, GapWaitBlockedMs = waitMs, GapMessagesMs = msgMs,
                GapInputMs = inputMs, GapPostsMs = postsMs, GapColdMs = coldMs,
                GapOtherMs = MathF.Max(0f, gapMs - waitMs - msgMs - inputMs - postsMs - coldMs),
                GapRunMs = runMs, GapBlockedMs = b.BlockedMs, GapNotScheduledMs = b.NotScheduledMs, GapOverrunMs = b.OverrunMs,
                GapVerdict = b.Verdict, GapStartQpc = _gapPaintEndQpc, GapWaits = _gapWaits,
            };
        }
        else _lastGap = default;
        _gapWaitTicks = _gapMessagesTicks = _gapInputTicks = _gapPostsTicks = _gapColdTicks = 0;
        _gapWaitRequestedMs = 0;
        _gapWaitInfinite = false;
        _gapWaits = 0;
        PollEngagedPresents();   // the engaged-edge present lines ride the same per-Paint point (AppHost.Engaged.cs)
    }

    /// <summary>Paint end (the frame's stats are published): the next gap starts here; the Paint's own work calibrates
    /// the cycles-per-ms rate (the thread was running for all of it, bar pre-emption, which only lowers a sample).</summary>
    private void GapPaintEnd(long frameStart)
    {
        long now = Stopwatch.GetTimestamp();
        ulong cycles = ThreadCycles.Read();
        if (_gapPaintStartCycles != 0 && cycles > _gapPaintStartCycles)
            _gapCyclesPerMs = ThreadCycles.Calibrate(_gapCyclesPerMs, cycles - _gapPaintStartCycles,
                (now - frameStart) * 1000.0 / Stopwatch.Frequency);
        _gapPaintEndQpc = now;
        _gapPaintEndCycles = cycles;
        ResolveEngagedAwaits();   // AppHost.Engaged.cs: this Paint's publication may be the one a pending flip waits for
    }

    /// <summary>The last closed gap's decomposition (UI thread) — what a slack frame carries in <see cref="FrameStats.UiGap"/>.</summary>
    internal UiGapReport LastUiGap => _lastGap;
}
