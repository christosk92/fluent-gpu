using System.Collections.Generic;
using System.Linq;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>
/// The hosted reactive flush runs to QUIESCENCE every frame (reconciler-hooks.md §0bis). A committed write is applied
/// whole in the frame that flushes it — never presented half-applied across frames because a wall-clock slice ran out —
/// and a frame always makes progress. These gates run under <see cref="TinyPeriodWindow"/>: a display period of four
/// Stopwatch ticks, so any budget derived from the refresh period is already spent when the frame's flush starts
/// (exactly what a loaded box's preemption did to the old 4 ms slice).
/// </summary>
static class ReactiveQuiescenceChecks
{
    /// <summary>The scheduler itself (no host): ordering, recovery, the structural runaway bound, unit timing, 0 alloc.</summary>
    public static void Run()
    {
        StructuralPreemption();
        ExceptionRecovery();
        RunawayCycleIsBrokenAndReported();
        FiniteCascadeDrainsInOneFlush();
        UnitTimingAndAllocation();
    }

    public static void RunHosted(StringTable strings)
    {
        FanOutSettlesInOneFrame(strings);
        KeepAliveParkingSettlesInOneFrame(strings);
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
        var r = runtime.Flush();
        Check("gate.signals.flush-structural a structural effect scheduled by a normal unit preempts the rest of that normal batch; ONE flush drains all three",
            order.SequenceEqual(new[] { 1, 2, 3 }) && !runtime.HasPending && !r.HasPending && r.UnitsRun == 3, $"order=[{string.Join(",", order)}] units={r.UnitsRun}");

        order.Clear();
        last.Schedule(); structural.Schedule();
        runtime.Flush();
        Check("gate.signals.flush-structural-first structural priority is independent of scheduling order",
            order.SequenceEqual(new[] { 2, 3 }), $"order=[{string.Join(",", order)}]");
    }

    static void ExceptionRecovery()
    {
        bool fail = false;
        int runs = 0;
        var runtime = new ReactiveRuntime(() => 0);
        using var first = new Effect(runtime, () => { if (fail) throw new InvalidOperationException("probe"); });
        using var second = new Effect(runtime, () => runs++);
        runs = 0;
        first.Schedule(); second.Schedule();
        fail = true;
        bool caught = false;
        try { runtime.Flush(); }
        catch (InvalidOperationException) { caught = true; }
        bool retained = runtime.HasPending;
        var resumed = runtime.Flush();
        fail = false;
        first.Schedule(); runtime.Flush();
        Check("gate.signals.flush-exception a throw consumes the failing unit, keeps its unread siblings queued for the next flush, and releases the scheduler",
            caught && retained && resumed.UnitsRun == 1 && runs == 1 && !runtime.HasPending,
            $"caught={caught} retained={retained} resumedUnits={resumed.UnitsRun} runs={runs}");
    }

    // The ONE bound on a flush is structural: MaxFlushIterations queue batches. A two-effect ping-pong re-schedules
    // itself forever: it trips there, is broken (its queued work dropped) and is reported (RunawayCount plus one
    // always-on [signals.runaway] line naming the owners); the scheduler stays usable.
    static void RunawayCycleIsBrokenAndReported()
    {
        bool cycle = false;
        int runs = 0;
        var runtime = new ReactiveRuntime(() => 0);
        Effect? second = null;
        using var first = new Effect(runtime, () => { if (!cycle) return; runs++; second!.Schedule(); });
        using var secondOwner = second = new Effect(runtime, () => { if (!cycle) return; runs++; first.Schedule(); });
        cycle = true;
        first.Schedule();
        int reportsBefore = runtime.RunawayCount;
        var tripped = runtime.Flush();
        bool stopped = !runtime.HasPending && !tripped.HasPending && runs == ReactiveRuntime.MaxFlushIterations
            && runtime.RunawayCount == reportsBefore + 1;
        cycle = false;
        first.Schedule(); second.Schedule();
        var recovered = runtime.Flush();
        Check("gate.signals.flush-runaway a self-retriggering cycle trips the structural bound (MaxFlushIterations batches) inside ONE flush, is broken and reported, and fresh writes schedule both effects afterward",
            stopped && recovered.UnitsRun == 2 && !recovered.HasPending,
            $"runs={runs} reports={runtime.RunawayCount - reportsBefore} recoveredUnits={recovered.UnitsRun}");
    }

    // A FINITE cascade (each effect writes the signal the next one reads, 900 levels deep, one batch per level) is not
    // a runaway: ONE flush applies all of it, nothing is dropped, nothing is reported.
    static void FiniteCascadeDrainsInOneFlush()
    {
        const int Depth = 900;
        var runtime = new ReactiveRuntime(() => 0);
        var sigs = new Signal<int>[Depth + 1];
        for (int i = 0; i <= Depth; i++) sigs[i] = new Signal<int>(0);
        var effects = new List<Effect>(Depth);
        for (int i = 0; i < Depth; i++)
        {
            int k = i;
            effects.Add(new Effect(runtime, () => { sigs[k + 1].Value = sigs[k].Value; }));
        }
        runtime.Flush();
        int reportsBefore = runtime.RunawayCount;
        sigs[0].Value = 7;
        var r = runtime.Flush();
        Check("gate.signals.flush-finite-cascade a 900-level cascade of dependent effects (one batch per level) is applied whole by ONE flush, not mistaken for a runaway",
            sigs[Depth].Value == 7 && !r.HasPending && r.UnitsRun == Depth && runtime.RunawayCount == reportsBefore,
            $"tail={sigs[Depth].Value} units={r.UnitsRun} reports={runtime.RunawayCount - reportsBefore}");
        foreach (var fx in effects) fx.Dispose();
    }

    static void UnitTimingAndAllocation()
    {
        long now = 0;
        var runtime = new ReactiveRuntime(() => now);
        var source = new Signal<int>(0);
        using var memo = new Memo<int>(runtime, () => { now += 7; return source.Value; });
        using var quick = new Effect(runtime, () => { _ = source.Value; now += 2; });
        using var slow = new Effect(runtime, () => { _ = memo.Value; now += 3; });
        source.Value = 1;
        var r = runtime.Flush();
        Check("gate.signals.flush-unit-timing the flush reports its longest unit (synchronous memo pulls included) and WHICH computation it was, so the slow-unit report can name its owner",
            r.UnitsRun == 2 && r.LongestUnitTicks == 10 && ReferenceEquals(r.LongestUnit, slow) && !r.HasPending,
            $"units={r.UnitsRun} longest={r.LongestUnitTicks} isSlow={ReferenceEquals(r.LongestUnit, slow)}");

        for (int i = 0; i < 32; i++) { slow.Schedule(); quick.Schedule(); runtime.Flush(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) { slow.Schedule(); quick.Schedule(); runtime.Flush(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.signals.flush-alloc a steady flush allocates nothing", allocated == 0, $"allocated={allocated}");
    }

    /// <summary>Eight leaves read <see cref="Ping"/>; a reactive effect on the host's runtime mirrors it into
    /// <see cref="Mirror"/>, which a ninth leaf renders — a second batch the first one schedules inside the same flush.</summary>
    sealed class FanOutProbe : Component
    {
        public const int Leaves = 8;
        public readonly Signal<int> Ping = new(0);
        public readonly Signal<int> Mirror = new(0);
        public readonly int[] Renders = new int[Leaves];
        public int SinkSaw = -1;

        public override Element Render()
        {
            var children = new Element[Leaves + 1];
            for (int i = 0; i < Leaves; i++)
            {
                int k = i;
                children[i] = Embed.Comp(() => new Leaf(this, k));
            }
            children[Leaves] = Embed.Comp(() => new Sink(this));
            return new BoxEl { Direction = 1, Children = children };
        }

        sealed class Leaf(FanOutProbe o, int index) : Component
        {
            public override Element Render()
            {
                int v = o.Ping.Value;
                o.Renders[index]++;
                return new BoxEl { Width = 20f, Height = 4f, Children = [Text("leaf" + index + ":" + v)] };
            }
        }

        sealed class Sink(FanOutProbe o) : Component
        {
            public override Element Render()
            {
                o.SinkSaw = o.Mirror.Value;
                return new BoxEl { Width = 20f, Height = 4f };
            }
        }
    }

    static AppHost TinyHost(StringTable strings, Component root, out HeadlessPlatformApp app)
    {
        app = new HeadlessPlatformApp();
        var inner = new HeadlessWindow(new WindowDesc("rx-quiescence", new Size2(200, 200), 1f));
        inner.Show();
        return new AppHost(app, new TinyPeriodWindow(inner), new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
    }

    static bool Pending(AppHost host) => (host.CurrentWakeReasons & WakeReasons.RuntimePending) != 0;

    // ── gate.signals.frame-reaches-quiescence ─────────────────────────────────────────────────────────────────────
    static void FanOutSettlesInOneFrame(StringTable strings)
    {
        var probe = new FanOutProbe();
        using var host = TinyHost(strings, probe, out var app);
        using var _app = app;
        using var mirror = new Effect(host.Reconciler.Runtime, () => { probe.Mirror.Value = probe.Ping.Value; });
        host.RunFrame();
        bool mounted = !Pending(host) && probe.Renders[0] == 1 && probe.Renders[FanOutProbe.Leaves - 1] == 1 && probe.SinkSaw == 0;
        bool eachFrame = true;
        for (int w = 1; w <= 3; w++)
        {
            probe.Ping.Value = w;
            host.RunFrame();
            bool allLeaves = true;
            for (int i = 0; i < FanOutProbe.Leaves; i++) allLeaves &= probe.Renders[i] == 1 + w;
            eachFrame &= allLeaves && probe.SinkSaw == w && !Pending(host);
        }
        Check("gate.signals.frame-reaches-quiescence ONE frame applies a committed write whole — eight re-renders and the second batch the first one schedules (an effect mirroring the write, and the mirror's reader) — with nothing left queued, even when the display period is four Stopwatch ticks (a per-frame budget already spent before the flush starts)",
            mounted && eachFrame,
            $"mounted={mounted} eachFrame={eachFrame} renders=[{string.Join(",", probe.Renders)}] sinkSaw={probe.SinkSaw} pending={Pending(host)}");
    }

    // ── gate.signals.keepalive-parks-in-one-frame ─────────────────────────────────────────────────────────────────
    // The KeepAlive parking contract (gate.unify.scope-keepalive-parks) asserted one RunFrame per step, the way the engine
    // promises it — under the tiny period too: parked ⇒ deferred is a proof only when nothing is left queued.
    static void KeepAliveParkingSettlesInOneFrame(StringTable strings)
    {
        var probe = new ScopeParkProbe();
        using var host = TinyHost(strings, probe, out var app);
        using var _app = app;
        host.RunFrame();
        bool mountA = probe.Renders.GetValueOrDefault("a") == 1 && probe.Constructions.GetValueOrDefault("a") == 1;
        probe.Ping.Value = 1; host.RunFrame();
        bool activeReran = probe.Renders.GetValueOrDefault("a") == 2;
        probe.Route!.Value = "b"; host.RunFrame();
        bool parkedMountB = probe.Renders.GetValueOrDefault("b") == 1 && probe.Renders.GetValueOrDefault("a") == 2;
        probe.Ping.Value = 2; host.RunFrame();
        bool parkedDeferred = probe.Renders.GetValueOrDefault("a") == 2;
        probe.Route.Value = "a"; host.RunFrame();
        bool replayOnce = probe.Renders.GetValueOrDefault("a") == 3 && probe.Constructions.GetValueOrDefault("a") == 1;
        bool quiet = !Pending(host);
        Check("gate.signals.keepalive-parks-in-one-frame KeepAlive parking (mount, re-render, park + mount, deferred write, reactivate-and-replay-once) completes in ONE frame per step at a four-tick display period",
            mountA && activeReran && parkedMountB && parkedDeferred && replayOnce && quiet,
            $"mountA={mountA} activeReran={activeReran} parkedMountB={parkedMountB} parkedDeferred={parkedDeferred} replayOnce={replayOnce} quiet={quiet} rendersA={probe.Renders.GetValueOrDefault("a")} rendersB={probe.Renders.GetValueOrDefault("b")}");
    }
}
