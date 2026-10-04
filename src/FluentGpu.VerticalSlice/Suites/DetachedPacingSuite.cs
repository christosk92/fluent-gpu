using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// Gates for per-target present pacing (F094 / F097). A real detached pop-out cannot present headlessly (a Headless window
/// never spawns a render thread, and the D3D12 present queue is not in this harness), so the DECISIONS behind the fix are
/// gated where they live: the render thread's parent-vs-child motion split (a child's motion must neither take the primary
/// present credit nor mark the parent's tick spent), the child's own pacing evidence and ledger rows, and the per-swapchain
/// present-queue depth a child's policy would set.
/// </summary>
static class DetachedPacingSuite
{
    public static void Run(StringTable strings)
    {
        _ = strings;
        ChildMotionSplitChecks();
        ChildEvidenceChecks();
        PerTargetDepthChecks();
    }

    private sealed class HeldTickClock : IRenderDisplayClock
    {
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => true;
        public void SetActive(bool active) { }
        public long TickSeq => 10;   // one compositor tick, held for the whole check
        public void Dispose() => _tick.Dispose();
    }

    // gate.detached-pacing.child-motion-*: motion that belongs only to the pop-out keeps the loop ticking and drains the child,
    // but the PRIMARY swapchain sees no credit take, no motion present and no spent tick - so a parent publication landing in
    // the same tick presents at once instead of waiting a whole vblank (the defect: every such frame deferred while the
    // pop-out animated).
    static void ChildMotionSplitChecks()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new HeldTickClock();
        byte[] one = [1];
        int takes = 0, motionPresents = 0, drains = 0, submits = 0;
        var rt = new RenderThread(seam, _ => submits++, async: false,
            needsTick: () => true, ownMotion: () => false,
            tick: () => motionPresents++, tickPeriod: () => Stopwatch.Frequency / 120, displayClock: clock,
            takePresentSlot: _ => { takes++; return true; },
            extraDrain: () => drains++);
        try
        {
            for (int i = 0; i < 6; i++) rt.DrainSync();
            Check("gate.detached-pacing.child-motion-takes-no-primary-credit 6 child-motion turns took no primary present slot",
                takes == 0, $"takes={takes}");
            Check("gate.detached-pacing.child-motion-is-not-a-parent-present no motion re-present, nothing counted as the parent's",
                motionPresents == 0 && rt.MotionPresents == 0 && rt.FreshPresents == 0 && rt.SkippedTicks == 0,
                $"ticks={motionPresents} motion={rt.MotionPresents} fresh={rt.FreshPresents} skipped={rt.SkippedTicks}");
            Check("gate.detached-pacing.children-still-drain extraDrain ran on every turn", drains >= 6, $"drains={drains}");

            seam.Publish(one, default, default);
            rt.DrainSync();
            Check("gate.detached-pacing.parent-frame-not-deferred a parent publication in the same tick presented at once (tick not spent by the child)",
                submits == 1 && rt.FreshPresents == 1 && rt.SkippedTicks == 0 && rt.RaceHits == 0,
                $"submits={submits} fresh={rt.FreshPresents} skipped={rt.SkippedTicks} race={rt.RaceHits}");
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }

    // gate.detached-pacing.child-evidence-*: a child's presents have evidence of their own, in the pace line and the ledger.
    static void ChildEvidenceChecks()
    {
        long ms = Stopwatch.Frequency / 1000;
        var pace = new ChildPresentPace(9201);
        pace.BeginWindow();
        bool idleSilent = pace.Describe() is null;
        pace.NoteSlotTake(0, opened: false);
        pace.NoteSlotTake(0, opened: true);
        pace.NotePresent(6 * ms, 2 * ms);
        string? line = pace.Describe();
        Check("gate.detached-pacing.child-evidence-in-pace-line an idle child adds nothing; a presenting child reports its own presents and deferrals",
            idleSilent && line is not null && line.StartsWith("t9201(", StringComparison.Ordinal)
            && line.Contains("presents=1", StringComparison.Ordinal) && line.Contains("deferred=1", StringComparison.Ordinal),
            line ?? "null");

        const ulong seq = 7_100_000UL;
        PresentLedger.RecordChild(9201, seq, 10, 0, Stopwatch.GetTimestamp());
        Check("gate.detached-pacing.child-ledger-row-is-its-own the child's present is findable in the child ring and absent from the primary ring",
            PresentLedger.TryFindChildFirstAtOrAfter(9201, seq, out PresentRecord row) && row.Target == 9201 && row.PublishSeq == seq
            && !PresentLedger.TryFindChildFirstAtOrAfter(9202, seq, out _) && !PresentLedger.TryFindFirstAtOrAfter(seq, out _));
    }

    // gate.detached-pacing.depth-per-target: a child's depth policy sets the CHILD swapchain's queue depth and never the
    // primary's (the device-level depth, MaxFrameLatency, is the primary's).
    static void PerTargetDepthChecks()
    {
        var device = new HeadlessGpuDevice();
        var primary = new HeadlessSwapchain(new Size2(64, 64));
        var child = new HeadlessSwapchain(new Size2(64, 64));
        int now = device.SetPresentQueueDepth(child, 2);
        Check("gate.detached-pacing.depth-per-target the child's depth is its own; the primary swapchain and the device depth are untouched",
            now == 2 && device.PresentQueueDepthOf(child) == 2 && device.PresentQueueDepthOf(primary) == 1
            && ((IGpuDevice)device).MaxFrameLatency == 1,
            $"child={device.PresentQueueDepthOf(child)} primary={device.PresentQueueDepthOf(primary)}");
    }
}
