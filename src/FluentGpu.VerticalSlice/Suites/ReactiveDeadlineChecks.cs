using FluentGpu.Hosting;
using FluentGpu.Signals;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class ReactiveDeadlineChecks
{
    public static void Run()
    {
        CarryAndDeduplicate();
        StructuralPreemption();
        ExceptionRecovery();
        RunawayWithinOneFlush();
        GuardResetsPerFlush();
        RebindFlushIsUnbudgeted();
        PullTimingAndAllocation();
    }

    static void CarryAndDeduplicate()
    {
        long now = 0;
        int wakes = 0;
        var runtime = new ReactiveRuntime(() => now) { FrameRequested = () => wakes++ };
        var observed = new List<int>();
        using var first = new Effect(runtime, () => { observed.Add(1); now += 10; });
        using var second = new Effect(runtime, () => { observed.Add(2); now += 10; });
        using var third = new Effect(runtime, () => { observed.Add(3); now += 10; });
        observed.Clear();
        now = 0;
        first.Schedule(); second.Schedule(); third.Schedule();
        wakes = 0;
        var expired = runtime.Flush(0);
        var slice = runtime.Flush(10);
        second.Schedule(); // Already in the carried batch: must not be queued a second time.
        var sameTurn = runtime.Flush(10);
        var next = runtime.Flush(20);
        third.Dispose();
        var last = runtime.Flush(30);
        Check("gate.signals.deadline-carry", expired.UnitsRun == 0 && expired.HasPending
            && slice.UnitsRun == 1 && slice.LongestUnitTicks == 10 && slice.HasPending
            && sameTurn.UnitsRun == 0 && sameTurn.HasPending
            && next.UnitsRun == 1 && last.UnitsRun == 0 && !last.HasPending
            && observed.SequenceEqual(new[] { 1, 2 }) && wakes >= 3,
            "Expired shared deadlines do not replenish; carried entries stay deduplicated; disposed work is skipped.");
    }

    static void StructuralPreemption()
    {
        long now = 0;
        bool running = false;
        var runtime = new ReactiveRuntime(() => now);
        var order = new List<int>();
        using var structural = new Effect(runtime, () => { if (running) { order.Add(2); now += 10; } }, structural: true);
        using var first = new Effect(runtime, () =>
        {
            if (!running) return;
            order.Add(1); now += 10; structural.Schedule();
        });
        using var last = new Effect(runtime, () => { if (running) { order.Add(3); now += 10; } });
        running = true;
        first.Schedule(); last.Schedule();
        runtime.Flush(10);
        runtime.Flush(20);
        runtime.Flush(30);
        Check("gate.signals.deadline-structural", order.SequenceEqual(new[] { 1, 2, 3 }) && !runtime.HasPending,
            "A structural effect scheduled by the last unit of a slice preempts its carried normal siblings.");

        order.Clear(); now = 0;
        last.Schedule(); structural.Schedule();
        runtime.Flush(10);
        runtime.Flush(20);
        Check("gate.signals.deadline-structural-first", order.SequenceEqual(new[] { 2, 3 }),
            "Structural priority is independent of scheduling order on a fresh slice.");
    }

    static void ExceptionRecovery()
    {
        long now = 0;
        bool fail = false;
        int runs = 0;
        var runtime = new ReactiveRuntime(() => now);
        using var first = new Effect(runtime, () => { if (fail) throw new InvalidOperationException("probe"); });
        using var second = new Effect(runtime, () => runs++);
        first.Schedule(); second.Schedule();
        fail = true;
        bool caught = false;
        try { runtime.Flush(10); }
        catch (InvalidOperationException) { caught = true; }
        bool retained = runtime.HasPending;
        runtime.Flush(10);
        fail = false;
        first.Schedule(); runtime.Flush(10);
        Check("gate.signals.deadline-exception", caught && retained && runs == 2 && !runtime.HasPending,
            "A throw consumes the failing unit, preserves siblings, and releases the scheduler for subsequent work.");
    }

    // The runaway guard measures re-entrancy WITHIN one Flush call. A self-retriggering cycle inside a single
    // unbounded flush still trips at 1000 batches and is broken; the scheduler stays usable afterward.
    static void RunawayWithinOneFlush()
    {
        long now = 0;
        bool cycle = false;
        int runs = 0;
        var runtime = new ReactiveRuntime(() => now);
        Effect? second = null;
        using var first = new Effect(runtime, () =>
        {
            if (!cycle) return;
            runs++; second!.Schedule();
        });
        using var secondOwner = second = new Effect(runtime, () =>
        {
            if (!cycle) return;
            runs++; first.Schedule();
        });
        cycle = true;
        first.Schedule();
        var tripped = runtime.Flush(long.MaxValue);
        bool stopped = !runtime.HasPending && !tripped.HasPending && runs == 1000;
        cycle = false;
        first.Schedule(); second.Schedule();
        var recovered = runtime.Flush(long.MaxValue);
        Check("gate.signals.deadline-runaway", stopped && recovered.UnitsRun == 2 && !recovered.HasPending,
            "A two-effect cycle trips inside ONE flush; fresh writes can schedule both effects afterward.");
    }

    // The guard must NOT accumulate across deadline yields. Sustained work that never reaches quiescence (a long
    // fling: every slice yields, every slice leaves more queued) used to reach 1000 carried batches and BailOut,
    // silently dropping every queued computation. It now resets per Flush, so the work keeps landing, one slice
    // at a time, and only the non-quiescent DIAGNOSTIC counter climbs.
    static void GuardResetsPerFlush()
    {
        const int Slices = 4_000;   // 4x the runaway trip point: the old carried guard could not survive this
        long now = 0;
        bool cycle = false;
        int runs = 0;
        var runtime = new ReactiveRuntime(() => now);
        Effect? second = null;
        using var first = new Effect(runtime, () =>
        {
            if (!cycle) return;
            runs++; now++; second!.Schedule();
        });
        using var secondOwner = second = new Effect(runtime, () =>
        {
            if (!cycle) return;
            runs++; now++; first.Schedule();
        });
        cycle = true;
        first.Schedule();
        for (int i = 0; i < Slices && runtime.HasPending; i++) runtime.Flush(now + 1);   // one unit per slice
        bool keptWorking = runs == Slices && runtime.HasPending && runtime.NonQuiescentFlushes == Slices;
        // Quiescence resets the diagnostic; nothing was ever dropped.
        cycle = false;
        var drained = runtime.Flush(long.MaxValue);
        Check("gate.signals.deadline-guard-per-flush",
            keptWorking && drained.UnitsRun == 1 && !runtime.HasPending && runtime.NonQuiescentFlushes == 0,
            "The runaway guard is per-Flush: 4000 yielded slices never drop queued work.");
    }

    // AppHost policy: the turn's FIRST flush is budgeted (AppHost.ReactiveSliceTicks), but the post-realize rebind
    // flushes run to quiescence. Sharing the exhausted deadline gave them ZERO units — realized rows then rendered
    // against stale bindings and HasPending forced another whole frame.
    static void RebindFlushIsUnbudgeted()
    {
        long now = 0;
        bool armed = false;
        var order = new List<int>();
        var runtime = new ReactiveRuntime(() => now);
        using var slow = new Effect(runtime, () => { if (armed) { order.Add(1); now += 100; } });   // one indivisible overrun
        using var leftover = new Effect(runtime, () => { if (armed) order.Add(2); });
        using var rebindA = new Effect(runtime, () => { if (armed) order.Add(3); });
        using var rebindB = new Effect(runtime, () => { if (armed) order.Add(4); });
        armed = true;
        order.Clear();
        slow.Schedule(); leftover.Schedule();

        long deadline = now + 40;                     // the turn's slice; ONE 100-tick unit blows it
        var first = runtime.Flush(deadline);
        bool yielded = first.UnitsRun == 1 && first.HasPending;

        // The realize pass writes its slot signals here.
        rebindA.Schedule(); rebindB.Schedule();
        var starved = runtime.Flush(deadline);        // the OLD shape: share the turn's deadline
        var rebind = runtime.Flush(long.MaxValue);    // AppHost.FlushRebindsToQuiescence

        Check("gate.signals.deadline-rebind-unbudgeted",
            yielded && starved.UnitsRun == 0 && starved.HasPending
            && rebind.UnitsRun == 3 && !rebind.HasPending
            && order.SequenceEqual(new[] { 1, 2, 3, 4 })
            && AppHost.ReactiveSliceTicks(0) == AppHost.ReactiveSliceTicks(System.Diagnostics.Stopwatch.Frequency / 15),
            "A budgeted first flush starves a shared-deadline rebind flush; the unbudgeted one lands every rebind this turn.");
    }

    static void PullTimingAndAllocation()
    {
        long now = 0;
        var runtime = new ReactiveRuntime(() => now);
        var source = new Signal<int>(0);
        using var memo = new Memo<int>(runtime, () => { now += 7; return source.Value; });
        using var effect = new Effect(runtime, () => { _ = memo.Value; now += 3; });
        source.Value = 1;
        var slice = runtime.Flush(now + 5);
        Check("gate.signals.deadline-unit-timing", slice.UnitsRun == 1 && slice.LongestUnitTicks == 10 && !slice.HasPending,
            "A unit is atomic and includes synchronous memo pulls even when it overruns the slice.");

        // Warm every queue swap and the elapsed-deadline path before the allocation sample.
        for (int i = 0; i < 32; i++) { effect.Schedule(); runtime.Flush(now); runtime.Flush(now + 20); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) { effect.Schedule(); runtime.Flush(now); runtime.Flush(now + 20); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.signals.deadline-alloc", allocated == 0, $"Steady deadline flush allocated {allocated} bytes.");
    }
}
