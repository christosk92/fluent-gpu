using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




static class HooksSuite
{
    public static void Run(StringTable strings)
    {
        HookChecks();
        HookSurfaceChecks();
        HookSubstrateChecks(strings);
        UnifyChecks(strings);
        ValidationChecks();
        KeyedChecks(strings);
        ReuseGuardChecks(strings);
        PropsChannelChecks(strings);
        PropsGenChecks(strings);
        KeyboardChecks(strings);
        AsyncCommandChecks(strings);
        TimerHookChecks(strings);
        GranularityChecks(strings);
        SliderSignalChecks(strings);
        SliderUnifiedChecks(strings);
        FlowChecks(strings);
        FlowReorderChecks(strings);
        FlowShowRefreshChecks(strings);
        ForTypedChecks(strings);
        ResourceChecks(strings);
        PropNetClobberChecks(strings);
        PropUnionChecks(strings);
        G4dMigrationChecks(strings);
    }

    static void HookChecks()
    {
        var p = new HookProbe();

        p.RenderWithHooks();   // frame 1 (mount)
        var r1 = p.RefBox;
        bool ok1 = p.State == 0 && p.Memo == 10 && p.MemoRuns == 1 && r1!.Value == 7;

        p.Dispatch!(5); p.Dispatch!(3);   // fold: 0+5=5, +3=8 (a reducer dispatch applies to the signal immediately)
        r1!.Value = 42;

        p.RenderWithHooks();   // frame 2 (same dep)
        bool ok2 = p.State == 8 && p.Memo == 10 && p.MemoRuns == 1 && ReferenceEquals(p.RefBox, r1) && p.RefBox!.Value == 42;

        p.Dep = 2;
        p.RenderWithHooks();   // frame 3 (changed dep)
        bool ok3 = p.Memo == 20 && p.MemoRuns == 2;

        Check("14. UseReducer folds dispatches", ok1 && p.State == 8, "0 →(+5,+3)→ 8");
        Check("15. UseMemo recomputes only on dep change", ok2 && ok3, $"memoRuns={p.MemoRuns}");
        Check("16. UseRef persists & is stable", p.RefBox!.Value == 42 && ReferenceEquals(p.RefBox, r1));
    }

    static void HookSurfaceChecks()
    {
        // gate.hooks.autotrack-timing — the auto-tracked body executes in the passive drain, NEVER inline during Flush.
        {
            var rt = new ReactiveRuntime();
            var pr = new AutoEffectProbe(); pr.Context.Runtime = rt;
            pr.RenderWithHooks();
            bool deferredAtMount = pr.Runs == 0;          // not run during render/mount
            PumpEffects(pr.Context);
            bool ranInDrain = pr.Runs == 1;
            pr.A.Value = 1;
            bool notOnWrite = pr.Runs == 1;
            rt.Flush();
            bool notInFlush = pr.Runs == 1;              // Flush schedules + stages, but does NOT run the body inline
            PumpEffects(pr.Context);
            bool ranInPassive = pr.Runs == 2;
            Check("gate.hooks.autotrack-timing", deferredAtMount && ranInDrain && notOnWrite && notInFlush && ranInPassive,
                $"mount-deferred={deferredAtMount} drain={ranInDrain} not-in-flush={notInFlush} passive={ranInPassive}");
        }

        // gate.hooks.autotrack-rearm — reads sigA run 1, sigB run 2 (branch flip); re-fires on sigB, NOT sigA.
        {
            var rt = new ReactiveRuntime();
            var pr = new AutoEffectProbe(); pr.Context.Runtime = rt;
            pr.UseB = false; pr.RenderWithHooks(); PumpEffects(pr.Context);   // run1 reads A
            pr.UseB = true; pr.RenderWithHooks();                            // re-render installs closure2 (reads B)
            pr.A.Value = 1; rt.Flush(); PumpEffects(pr.Context);             // run2 (via still-subscribed A) → now tracks B
            pr.B.Value = 1; rt.Flush(); PumpEffects(pr.Context);             // re-fires on B
            int afterB = pr.Runs;
            pr.A.Value = 2; rt.Flush(); PumpEffects(pr.Context);             // A no longer tracked → no re-fire
            Check("gate.hooks.autotrack-rearm", pr.Runs == 3 && afterB == 3,
                $"runs={pr.Runs} (want 3; A-after-flip must not re-fire)");
        }

        // gate.hooks.effect-cleanup-runs — cleanup before re-run on dep change AND once at unmount.
        {
            var rt = new ReactiveRuntime();
            var cp = new CleanupProbe(); cp.Context.Runtime = rt;
            cp.Dep = 1; cp.RenderWithHooks(); PumpEffects(cp.Context);   // Setup=1, Cleanup=0
            cp.Dep = 2; cp.RenderWithHooks(); PumpEffects(cp.Context);   // cleanup fires, then re-run: Cleanup=1, Setup=2
            bool beforeRerun = cp.Setup == 2 && cp.Cleanup == 1;
            cp.Unmount();
            bool atUnmount = cp.Cleanup == 2;
            Check("gate.hooks.effect-cleanup-runs", beforeRerun && atUnmount, $"setup={cp.Setup} cleanup={cp.Cleanup}");
        }

        // gate.hooks.effect-cleanup-order — multi-effect cleanups run in cell order at unmount.
        {
            var rt = new ReactiveRuntime();
            var mp = new MultiCleanupProbe(); mp.Context.Runtime = rt;
            mp.RenderWithHooks(); PumpEffects(mp.Context);
            mp.Unmount();
            Check("gate.hooks.effect-cleanup-order", mp.Order.Count == 2 && mp.Order[0] == 1 && mp.Order[1] == 2,
                $"order=[{string.Join(",", mp.Order)}]");
        }

        // gate.hooks.deps-empty-mount-once — UseEffect(fn, DepKey.Empty) runs exactly once across N re-renders.
        {
            var rt = new ReactiveRuntime();
            var mo = new MountOnceProbe(); mo.Context.Runtime = rt;
            mo.RenderWithHooks(); PumpEffects(mo.Context);
            for (int i = 0; i < 5; i++) { mo.RenderWithHooks(); PumpEffects(mo.Context); }
            Check("gate.hooks.deps-empty-mount-once", mo.Runs == 1, $"runs={mo.Runs} over 6 renders");
        }

        // gate.hooks.depkey-string-tuple-parity — (string,int) deps re-fire on either changing, not on equal re-supply.
        {
            var rt = new ReactiveRuntime();
            var sp = new StringTupleProbe(); sp.Context.Runtime = rt;
            sp.S = "a"; sp.I = 1; sp.RenderWithHooks(); PumpEffects(sp.Context);   // Runs=1
            sp.RenderWithHooks(); PumpEffects(sp.Context);                         // equal → Runs=1
            sp.S = "b"; sp.RenderWithHooks(); PumpEffects(sp.Context);             // string changed → Runs=2
            sp.I = 2; sp.RenderWithHooks(); PumpEffects(sp.Context);               // int changed → Runs=3
            sp.RenderWithHooks(); PumpEffects(sp.Context);                         // equal → Runs=3
            Check("gate.hooks.depkey-string-tuple-parity", sp.Runs == 3, $"runs={sp.Runs} (want 3)");
        }

        // gate.hooks.depkey-fromref-identity — FromRef re-fires on instance swap, not on an unchanged instance.
        {
            var rt = new ReactiveRuntime();
            var fp = new FromRefProbe(); fp.Context.Runtime = rt;
            fp.RenderWithHooks(); PumpEffects(fp.Context);   // Runs=1
            fp.RenderWithHooks(); PumpEffects(fp.Context);   // same instance → Runs=1
            fp.RenderWithHooks(); PumpEffects(fp.Context);   // still same instance → Runs=1
            bool stable = fp.Runs == 1;
            fp.Obj = new object(); fp.RenderWithHooks(); PumpEffects(fp.Context);   // swapped → Runs=2
            Check("gate.hooks.depkey-fromref-identity", stable && fp.Runs == 2, $"runs={fp.Runs} (want 2 after swap)");
        }

        // gate.signal.setifchanged — false+no-notify on equal, true+notify on change; always-notify notifies on equal set.
        {
            var rt = new ReactiveRuntime();
            var s = new Signal<int>(0);
            int notifs = 0;
            _ = new Effect(rt, () => { _ = s.Value; notifs++; });   // subscribes; runs now (notifs=1)
            bool eqFalse = !s.SetIfChanged(0); rt.Flush(); bool noNotify = notifs == 1;
            bool changeTrue = s.SetIfChanged(5); rt.Flush(); bool didNotify = notifs == 2;
            var an = Signal.AlwaysNotify(0);
            int anNotifs = 0;
            _ = new Effect(rt, () => { _ = an.Value; anNotifs++; });   // notifs -> 1
            bool anTrue = an.SetIfChanged(0); rt.Flush(); bool alwaysNotified = anNotifs == 2;   // equal set still notifies
            Check("gate.signal.setifchanged", eqFalse && noNotify && changeTrue && didNotify && anTrue && alwaysNotified,
                $"eqFalse={eqFalse} noNotify={noNotify} changeTrue={changeTrue} didNotify={didNotify} alwaysNotify={alwaysNotified}");
        }

        // gate.prop.bind-named-ctor — Prop.Bind over a Memo-typed-as-IReadSignal wires a live binding that re-fires on write.
        {
            var rt = new ReactiveRuntime();
            var src = new Signal<int>(3);
            var memo = new Memo<int>(rt, () => src.Value * 2);
            IReadSignal<int> asInterface = memo;
            var prop = Prop.Bind(asInterface);
            bool bound = prop.IsBound && ReferenceEquals(prop.Signal, memo);
            int v1 = prop.Signal!.Peek();       // 6
            src.Value = 10; rt.Flush();
            int v2 = prop.Signal!.Peek();       // 20 — the bound source tracks writes
            Check("gate.prop.bind-named-ctor", bound && v1 == 6 && v2 == 20, $"bound={bound} {v1}->{v2}");
        }
    }

    static void HookSubstrateChecks(StringTable strings)
    {
        // gate.hooks.substrate-conditional — a hook inside `if` gains/loses its cell WITHOUT corrupting its neighbours;
        // the skipped hook's state survives for when the branch is re-entered (the thing positional cursor cells cannot do).
        {
            var rt = new ReactiveRuntime();
            var p = new ConditionalHookProbe(); p.Context.Runtime = rt;
            p.IncludeMiddle = true; p.RenderWithHooks();            // mount: Top=10 Middle=20 Bottom=30
            p.Top.Set(11); p.Middle.Set(21); p.Bottom.Set(31);
            p.RenderWithHooks();
            bool r1 = p.Top.Value == 11 && p.Middle.Value == 21 && p.Bottom.Value == 31;
            p.IncludeMiddle = false; p.RenderWithHooks();          // Middle skipped — Bottom must stay 31 (NOT inherit Middle's cell)
            bool r2 = p.Top.Value == 11 && p.Bottom.Value == 31;
            p.Bottom.Set(32); p.RenderWithHooks();
            bool r3 = p.Top.Value == 11 && p.Bottom.Value == 32;
            p.IncludeMiddle = true; p.RenderWithHooks();           // Middle re-enters — its state (21) is preserved
            bool r4 = p.Middle.Value == 21 && p.Top.Value == 11 && p.Bottom.Value == 32;
            Check("gate.hooks.substrate-conditional a conditional hook gains/loses its cell without shifting neighbours; skipped state survives",
                r1 && r2 && r3 && r4, $"r1={r1} r2={r2} r3={r3} r4={r4} (Top={p.Top.Value} Middle={p.Middle.Value} Bottom={p.Bottom.Value})");
        }

        // gate.hooks.substrate-loop — hooks in a loop keyed per ordinal keep per-iteration state across a count change.
        {
            var rt = new ReactiveRuntime();
            var p = new LoopHookProbe(); p.Context.Runtime = rt;
            p.Count = 3; p.RenderWithHooks();
            p.Sigs[0].Value = 1; p.Sigs[1].Value = 101; p.Sigs[2].Value = 201;
            p.RenderWithHooks();
            bool keep = p.Sigs[0].Peek() == 1 && p.Sigs[1].Peek() == 101 && p.Sigs[2].Peek() == 201;
            p.Count = 4; p.RenderWithHooks();                      // append at end → first 3 keep state, 4th mounts fresh (300)
            bool grow = p.Sigs.Count == 4 && p.Sigs[0].Peek() == 1 && p.Sigs[1].Peek() == 101 && p.Sigs[2].Peek() == 201 && p.Sigs[3].Peek() == 300;
            p.Count = 2; p.RenderWithHooks();                      // remove at end → first 2 keep state
            bool shrink = p.Sigs.Count == 2 && p.Sigs[0].Peek() == 1 && p.Sigs[1].Peek() == 101;
            Check("gate.hooks.substrate-loop looped hooks keyed per ordinal keep per-iteration state across grow/shrink",
                keep && grow && shrink, $"keep={keep} grow={grow} shrink={shrink}");
        }

        // gate.hooks.substrate-alloc — the keyed lookup on a steady re-render allocates 0 bytes in the hot window.
        {
            var rt = new ReactiveRuntime();
            var p = new SteadyHookProbe(); p.Context.Runtime = rt;
            p.RenderWithHooks(); p.RenderWithHooks(); p.RenderWithHooks();   // warm (JIT + mount + dict capacity)
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) p.RenderWithHooks();
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("gate.hooks.substrate-alloc keyed cell lookup is 0-alloc on a steady re-render", delta == 0, $"delta={delta}B/200 renders");
        }

        // gate.ctx.required — UseRequiredContext throws (naming the type) when unprovided; returns the value when
        // provided; survives a parked re-render (resolves via the parked _ctxResolveCache fallback).
        {
            var channel = new Context<string>("dflt");
            var ctx = new RenderContext();
            bool unprovidedThrows = false; string? msg = null;
            try { ctx.UseRequiredContext(channel); } catch (InvalidOperationException ex) { unprovidedThrows = true; msg = ex.Message; }
            bool named = msg is not null && msg.Contains("String");

            var provided = new Signal<object?>("hello");
            ctx.ResolveContextSignal = (a, c) => ReferenceEquals(c, channel) ? provided : null;
            string got = ctx.UseRequiredContext(channel);                    // resolves + caches
            ctx.ResolveContextSignal = (a, c) => null;                       // simulate a PARKED (detached) re-render
            string parked = ctx.UseRequiredContext(channel);                 // reuses the cached provider signal

            var ctx2 = new RenderContext();
            var nullSig = new Signal<object?>(null);
            ctx2.ResolveContextSignal = (a, c) => nullSig;                   // provider carrying null → must also throw
            bool nullThrows = false;
            try { ctx2.UseRequiredContext(channel); } catch (InvalidOperationException) { nullThrows = true; }

            Check("gate.ctx.required throws (named) when unprovided/null; returns the value; survives a parked re-render",
                unprovidedThrows && named && got == "hello" && parked == "hello" && nullThrows,
                $"threw={unprovidedThrows} named={named} got={got} parked={parked} nullThrows={nullThrows}");
        }

        // gate.bind.contract-flip — a bound↔static flip on a REUSED node trips BindContract; a fresh-thunk re-render does NOT.
        {
            bool prevEnabled = BindContract.Enabled, prevThrow = BindContract.ThrowOnViolation;
            BindContract.Enabled = true; BindContract.ThrowOnViolation = false;
            try
            {
                var sig = new Signal<FluentGpu.Foundation.ColorF>(FluentGpu.Foundation.ColorF.Transparent);

                BindContract.Reset();
                var s1 = new SceneStore(); var r1 = new TreeReconciler(s1, strings);
                var e1 = new BoxEl { Width = 10, Height = 10, Fill = FluentGpu.Foundation.ColorF.Transparent };   // static
                var e2 = new BoxEl { Width = 10, Height = 10, Fill = sig };                                       // bound
                r1.ReconcileRoot(e1, null); r1.ReconcileRoot(e2, e1);
                bool staticToBound = BindContract.Violations == 1 && BindContract.LastViolation!.Contains("Fill");

                BindContract.Reset();
                var s2 = new SceneStore(); var r2 = new TreeReconciler(s2, strings);
                var f1 = new BoxEl { Width = 10, Height = 10, Fill = sig };                                       // bound
                var f2 = new BoxEl { Width = 10, Height = 10, Fill = FluentGpu.Foundation.ColorF.Transparent };   // static
                r2.ReconcileRoot(f1, null); r2.ReconcileRoot(f2, f1);
                bool boundToStatic = BindContract.Violations == 1;

                BindContract.Reset();
                var s3 = new SceneStore(); var r3 = new TreeReconciler(s3, strings);
                var g1 = new BoxEl { Width = 10, Height = 10, Fill = Prop.Of(() => FluentGpu.Foundation.ColorF.Transparent) };   // bound (thunk)
                var g2 = new BoxEl { Width = 10, Height = 10, Fill = Prop.Of(() => FluentGpu.Foundation.ColorF.Transparent) };   // FRESH thunk, same shape → NOT a flip
                r3.ReconcileRoot(g1, null); r3.ReconcileRoot(g2, g1);
                bool thunkQuiet = BindContract.Violations == 0;

                Check("gate.bind.contract-flip bound↔static flip on a reused node trips; a fresh-thunk re-render does not",
                    staticToBound && boundToStatic && thunkQuiet,
                    $"staticToBound={staticToBound} boundToStatic={boundToStatic} thunkQuiet={thunkQuiet}");
            }
            finally { BindContract.Enabled = prevEnabled; BindContract.ThrowOnViolation = prevThrow; }
        }

        // gate.signal.backwards-write-tripwire — an effect that reads+writes the same signal trips once; a normal effect does not.
        {
            bool prevEnabled = BackwardsWriteGuard.Enabled, prevThrow = BackwardsWriteGuard.ThrowOnViolation;
            BackwardsWriteGuard.Enabled = true; BackwardsWriteGuard.ThrowOnViolation = false;
            try
            {
                var rt = new ReactiveRuntime();
                BackwardsWriteGuard.Reset();
                var sig = new Signal<int>(5);
                _ = new Effect(rt, () => { int v = sig.Value; sig.Value = v; });   // read (subscribe) then write the SAME signal
                bool tripped = BackwardsWriteGuard.Violations >= 1 && BackwardsWriteGuard.LastViolation!.Contains("Signal");

                BackwardsWriteGuard.Reset();
                var a = new Signal<int>(1); var b = new Signal<int>(2);
                _ = new Effect(rt, () => { int v = a.Value; b.Value = v; });       // reads A, writes B → no convergence risk
                bool clean = BackwardsWriteGuard.Violations == 0;

                Check("gate.signal.backwards-write-tripwire an effect reading+writing the same signal trips once; a normal effect does not",
                    tripped && clean, $"tripped={tripped} clean={clean}");
            }
            finally { BackwardsWriteGuard.Enabled = prevEnabled; BackwardsWriteGuard.ThrowOnViolation = prevThrow; }
        }
    }

    static void UnifyChecks(StringTable strings)
    {
        // gate.unify.signal-free-render-once — a render that reads no signals runs exactly once, even while an unrelated
        // sibling re-renders across many flushes (run-once is a consequence of subscribing to nothing, not a mode).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("unify-once", new Size2(200, 200), 1f));
            window.Show();
            var root = new SignalFreeRoot();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
            host.RunFrame();
            bool mountedOnce = root.Probe.Renders == 1 && root.RootRenders == 1;
            for (int i = 0; i < 5; i++) { root.Unrelated!.Value = i + 1; host.RunFrame(); }
            bool rootReran = root.RootRenders >= 6;          // the root really re-rendered on each unrelated flush…
            bool childStillOnce = root.Probe.Renders == 1;   // …but the signal-free child never did
            Check("gate.unify.signal-free-render-once a signal-free render runs exactly once across unrelated flushes",
                mountedOnce && rootReran && childStillOnce,
                $"childRenders={root.Probe.Renders} rootRenders={root.RootRenders}");
        }

        // gate.unify.retheme-in-place — RethemeAll re-runs a run-once component's render IN PLACE: token color updates,
        // hook state + node identity preserved (no remount). Replaces the deleted ReactiveComponent.InvalidateTree path.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Dark);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("unify-retheme", new Size2(240, 120), 1f));
                window.Show();
                var probe = new RethemeInPlaceProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
                host.RunFrame();
                bool once = probe.Renders == 1;
                probe.SetState!(42); host.RunFrame();
                bool stateWrite = probe.SeenState == 42 && probe.Renders == 2;
                var textNode = FindTextNode(host.Scene, strings, host.Scene.Root, "g4b-themed");
                ColorF darkColor = host.Scene.Paint(textNode).TextColor;

                Tok.Use(ThemeKind.Light);
                host.Reconciler.RethemeAll();
                host.RunFrame();
                var textAfter = FindTextNode(host.Scene, strings, host.Scene.Root, "g4b-themed");
                bool sameNode = !textAfter.IsNull && textAfter == textNode;
                bool colorUpdated = darkColor != Tok.TextPrimary && host.Scene.Paint(textAfter).TextColor == Tok.TextPrimary;
                bool statePreserved = probe.SeenState == 42 && probe.Renders == 3;   // re-rendered in place; state survived
                Check("gate.unify.retheme-in-place RethemeAll re-runs a run-once render in place (color updates; state + node identity preserved)",
                    once && stateWrite && sameNode && colorUpdated && statePreserved,
                    $"once={once} stateWrite={stateWrite} sameNode={sameNode} colorUpdated={colorUpdated} statePreserved={statePreserved} renders={probe.Renders}");
            }
            finally { Tok.Use(saved); }
        }

        // gate.unify.scope-owns-lifetime — unmount == Scope.Dispose(): it disposes the render-effect (the signal it read
        // has zero subscribers afterward) AND runs the hook cleanups exactly once, both via the ONE per-component scope.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("unify-scope", new Size2(200, 200), 1f));
            window.Show();
            var root = new ScopeLifetimeRoot();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
            host.RunFrame();
            var c = root.C;
            bool mounted = c.Renders == 1 && c.Cleanups == 0 && c.Dep.SubscriberCount == 1;
            c.Dep.Value = 1; host.RunFrame();
            bool reran = c.Renders == 2 && c.Cleanups == 0;
            c.Show.Value = false; host.RunFrame();               // unmount the child
            bool cleanedOnce = c.Cleanups == 1;
            bool effectDisposed = c.Dep.SubscriberCount == 0;    // render-effect unsubscribed ⇒ disposed via the scope
            c.Dep.Value = 2; host.RunFrame();                    // a post-unmount write must not re-run or re-clean
            bool inert = c.Renders == 2 && c.Cleanups == 1;
            Check("gate.unify.scope-owns-lifetime unmount disposes the render-effect + runs hook cleanups exactly once, via the scope",
                mounted && reran && cleanedOnce && effectDisposed && inert,
                $"renders={c.Renders} cleanups={c.Cleanups} subs={c.Dep.SubscriberCount}");
        }

        // gate.unify.scope-keepalive-parks — parking does NOT dispose the scope: the page instance + hook state survive,
        // a parked page defers renders (no re-render even when a signal it read changes), and reactivation replays once.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("unify-park", new Size2(200, 200), 1f));
            window.Show();
            var probe = new ScopeParkProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame();
            bool mountA = probe.Renders["a"] == 1 && probe.Constructions["a"] == 1;
            probe.Ping.Value = 1; host.RunFrame();
            bool activeReran = probe.Renders["a"] == 2;                        // active + subscribed → re-renders
            probe.Route!.Value = "b"; host.RunFrame();                         // park "a", mount "b"
            bool parkedMountB = probe.Renders.GetValueOrDefault("b") == 1 && probe.Renders["a"] == 2;
            probe.Ping.Value = 2; host.RunFrame();                             // write a signal "a" read while it is parked
            bool parkedDeferred = probe.Renders["a"] == 2;                     // parked ⇒ deferred, NOT re-rendered
            probe.Route.Value = "a"; host.RunFrame();                          // reactivate "a"
            bool reactivateReplayOnce = probe.Renders["a"] == 3 && probe.Constructions["a"] == 1;   // replayed once; never reconstructed ⇒ scope preserved
            Check("gate.unify.scope-keepalive-parks parking keeps the scope (state survives, parked defers renders, reactivation replays once)",
                mountA && activeReran && parkedMountB && parkedDeferred && reactivateReplayOnce,
                $"rendersA={probe.Renders.GetValueOrDefault("a")} rendersB={probe.Renders.GetValueOrDefault("b")} ctorA={probe.Constructions.GetValueOrDefault("a")}");
        }
    }

    static void ValidationChecks()
    {
        // V1. a rule delegate evaluates allocation-free (the per-keystroke hot path resolves no string, allocates nothing).
        var req = Rules.Required("err.req");
        _ = req("warm");
        long before = GC.GetAllocatedBytesForCurrentThread();
        MsgId last = MsgId.None;
        for (int i = 0; i < 1000; i++) last = req(i % 2 == 0 ? "ok" : "");
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("V1. rule evaluation is zero-alloc", delta == 0 && !last.IsEmpty, $"delta={delta}B/1000");

        // V2. cross-field: an error memo that reads a sibling signal (Rules.Equals) re-validates when the sibling moves.
        var rt = new ReactiveRuntime();
        var pwd = new Signal<string>("secret");
        var confirm = new Signal<string>("secret");
        var eq = Rules.Equals(pwd, "err.match");
        var crossErr = new Memo<FieldError>(rt, () => { var m = eq(confirm.Value); return new FieldError(m, (byte)(m.IsEmpty ? 0 : 1)); });
        bool matchValid = crossErr.Value.IsValid;
        pwd.Value = "changed";                 // a sibling edit must re-validate confirm with no extra wiring
        rt.Flush();
        bool mismatchInvalid = !crossErr.Peek().IsValid;
        Check("V2. cross-field re-validates on sibling change", matchValid && mismatchInvalid);

        // V3. touched-gating: a pristine field hides its error while its UNGATED validity still reports the failure;
        //     a blur (MarkTouched) reveals it.
        var probe = new ValidationProbe { WithForm = true };
        var prt = new ReactiveRuntime();
        probe.Context.Runtime = prt;
        probe.RenderWithHooks();
        prt.Flush();
        bool pristineHidden = probe.EmailField!.Error.Peek().IsValid;   // Required fails but untouched → displayed-valid
        bool ungatedFalse = !probe.EmailField.IsValid.Peek();          // true validity reflects the failure
        probe.EmailField.MarkTouched();
        bool revealed = !probe.EmailField.Error.Peek().IsValid;
        Check("V3. touched-gating hides error until blur (ungated validity unaffected)", pristineHidden && ungatedFalse && revealed);

        // V4. submit gating: an invalid form blocks and reveals every field's error; fixing the value passes.
        bool formInvalid = !probe.Form!.IsValid.Peek();
        bool validateFalse = !probe.Form.Validate();
        prt.Flush();
        bool emailRevealed = !probe.EmailField.Error.Peek().IsValid;
        probe.Email.Value = "filled";
        prt.Flush();
        bool formValid = probe.Form.IsValid.Peek();
        bool validateTrue = probe.Form.Validate();
        Check("V4. submit gating: invalid blocks+reveals, fixed passes", formInvalid && validateFalse && emailRevealed && formValid && validateTrue);

        // V5. a server error (the async-result merge path) displays even on an untouched field, then clears — the
        //     equality-gated signal means an out-of-order async completion can only ever leave the last-written result.
        var p2 = new ValidationProbe { WithForm = false };
        var p2rt = new ReactiveRuntime();
        p2.Context.Runtime = p2rt;
        p2.Email.Value = "ok";                 // sync-valid + untouched
        p2.RenderWithHooks();
        p2rt.Flush();
        bool cleanFirst = p2.EmailField!.Error.Peek().IsValid;
        p2.EmailField.SetServerError(Msg.Key("err.taken"));
        bool serverShows = !p2.EmailField.Error.Peek().IsValid;        // bypasses the touched/submit gate
        p2.EmailField.SetServerError(MsgId.None);
        bool serverClears = p2.EmailField.Error.Peek().IsValid;
        Check("V5. server error bypasses the gate (async merge) and clears", cleanFirst && serverShows && serverClears);

        // V6. fields deregister from the form on unmount (no FormScope leak).
        bool registered = probe.Form.FieldCount == 2;
        probe.Unmount();
        bool deregistered = probe.Form.FieldCount == 0;
        Check("V6. fields deregister from the form on unmount", registered && deregistered, $"{(registered ? 2 : -1)}→{probe.Form.FieldCount}");

        // V7. the [Validatable] SOURCE GENERATOR emits working Validator<T>[] arrays that lower to the same Rules.* path.
        static bool AllPass<T>(Validator<T>[] rules, T value)
        {
            foreach (var r in rules) if (!r(value).IsEmpty) return false;
            return true;
        }
        bool emailGen = !AllPass(SignupRules.Validators.Email, "")        // Required fails
                        && !AllPass(SignupRules.Validators.Email, "bad")  // RegexMatch fails
                        && AllPass(SignupRules.Validators.Email, "a@b.co");
        bool pwdGen = !AllPass(SignupRules.Validators.Password, "1234567") && AllPass(SignupRules.Validators.Password, "12345678");
        bool ageGen = !AllPass(SignupRules.Validators.Age, 5.0) && AllPass(SignupRules.Validators.Age, 30.0);
        Check("V7. [Validatable] generator emits working Validator<T>[] (Required/MinLength/Range/RegexMatch)", emailGen && pwdGen && ageGen);
    }

    static void KeyedChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);

        static Element Row(params string[] keys)
        {
            var ch = new Element[keys.Length];
            for (int i = 0; i < keys.Length; i++) ch[i] = new BoxEl { Key = keys[i], Width = 10, Height = 10 };
            return new BoxEl { Direction = 0, Children = ch };
        }

        var t1 = Row("a", "b", "c");
        recon.ReconcileRoot(t1, null);
        var hA = Child(scene, scene.Root, 0);
        var hB = Child(scene, scene.Root, 1);
        var hC = Child(scene, scene.Root, 2);

        var t2 = Row("c", "a", "b");
        recon.ReconcileRoot(t2, t1);
        bool reordered = Child(scene, scene.Root, 0) == hC && Child(scene, scene.Root, 1) == hA && Child(scene, scene.Root, 2) == hB;
        Check("17. keyed reconcile reorders, preserving identity", reordered, "[a,b,c] → [c,a,b]");

        var t3 = Row("a", "b");
        recon.ReconcileRoot(t3, t2);
        int count = 0;
        for (var c = scene.FirstChild(scene.Root); !c.IsNull; c = scene.NextSibling(c)) count++;
        bool removed = count == 2 && Child(scene, scene.Root, 0) == hA && Child(scene, scene.Root, 1) == hB;
        Check("18. keyed reconcile removes only the dropped key", removed, "[c,a,b] → [a,b]");
    }

    static void ReuseGuardChecks(StringTable strings)
    {
        bool prevEnabled = ReuseGuard.Enabled;
        bool prevThrow = ReuseGuard.ThrowOnViolation;
        ReuseGuard.Enabled = true;             // gate turns the tripwire on regardless of the FG_REUSE_GUARD env
        ReuseGuard.ThrowOnViolation = false;   // count, don't throw (until the strict-mode sub-check)
        try
        {
            static Element Probe(int n) => new BoxEl { Children = [Embed.Comp(() => new FrozenPropProbe { Count = n })] };
            static Element ProbeKeyed(int n) => new BoxEl { Children = [Embed.Comp(() => new FrozenPropProbe { Count = n }) with { Key = "probe:" + n }] };

            // (1) Fires when a frozen scalar changed on a REUSED component (unkeyed positional child, same type).
            ReuseGuard.Reset();
            var scene1 = new SceneStore();
            var recon1 = new TreeReconciler(scene1, strings);
            var a1 = Probe(1); recon1.ReconcileRoot(a1, null);
            var a2 = Probe(5); recon1.ReconcileRoot(a2, a1);
            Check("gate.reuse.frozen-prop-tripwire fires when a reused component's frozen field carries a changed value",
                ReuseGuard.Violations == 1, $"violations={ReuseGuard.Violations} last={ReuseGuard.LastViolation}");

            // (2) Quiet when the value is unchanged, AND quiet when a changed Key REMOUNTS the child (the re-key fix idiom).
            ReuseGuard.Reset();
            var scene2 = new SceneStore();
            var recon2 = new TreeReconciler(scene2, strings);
            var b1 = Probe(3); recon2.ReconcileRoot(b1, null);
            var b2 = Probe(3); recon2.ReconcileRoot(b2, b1);
            bool quietOnSame = ReuseGuard.Violations == 0;
            var c1 = ProbeKeyed(1); recon2.ReconcileRoot(c1, b2);
            var c2 = ProbeKeyed(5); recon2.ReconcileRoot(c2, c1);
            bool quietOnRekey = ReuseGuard.Violations == 0;
            Check("gate.reuse.rekey-and-unchanged do NOT trip the tripwire (remount on Key change; equal value re-render)",
                quietOnSame && quietOnRekey, $"sameQuiet={quietOnSame} rekeyQuiet={quietOnRekey} violations={ReuseGuard.Violations}");

            // (3) Strict mode throws FrozenPropException so a hard-fail path exists for CI/dev.
            ReuseGuard.Reset();
            ReuseGuard.ThrowOnViolation = true;
            bool threw = false;
            var scene3 = new SceneStore();
            var recon3 = new TreeReconciler(scene3, strings);
            var d1 = Probe(1); recon3.ReconcileRoot(d1, null);
            try { var d2 = Probe(9); recon3.ReconcileRoot(d2, d1); }
            catch (FrozenPropException) { threw = true; }
            Check("gate.reuse.strict-throws raises FrozenPropException when ThrowOnViolation is set", threw);

            // (4) Const-gated identically to RenderBudget so the whole facility erases in release.
            Check("gate.reuse.guard-erased ReuseGuard.CompiledIn tracks the DEBUG/FLUENTGPU_DIAG erasure switch (== RenderBudget.CompiledIn)",
                ReuseGuard.CompiledIn == FluentGpu.Hosting.RenderBudget.CompiledIn,
                $"reuse={ReuseGuard.CompiledIn} renderBudget={FluentGpu.Hosting.RenderBudget.CompiledIn}");
        }
        finally
        {
            ReuseGuard.Enabled = prevEnabled;
            ReuseGuard.ThrowOnViolation = prevThrow;
            ReuseGuard.Reset();
        }
    }

    static void PropsChannelChecks(StringTable strings)
    {
        // gate.props.repush-reaches-instance — a CHANGED props record reaches the SAME instance (no remount, node
        // identity preserved), re-rendering it exactly once with the new value.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var child = new PropsChild();
            int ctor = 0;
            Element Tree(int n) => new BoxEl { Children = [Embed.Comp(new PropsPayload(n), () => { ctor++; return child; })] };
            var t1 = Tree(1); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            var nodeBefore = scene.FirstChild(scene.Root);
            bool mount = child.Renders == 1 && child.LastN == 1 && ctor == 1;
            var t2 = Tree(2); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();
            var nodeAfter = scene.FirstChild(scene.Root);
            bool reached = ctor == 1 && nodeBefore == nodeAfter && child.Renders == 2 && child.LastN == 2;
            Check("gate.props.repush-reaches-instance changed props re-render the SAME reused instance once (no remount)",
                mount && reached, $"mount={mount} ctor={ctor} sameNode={nodeBefore == nodeAfter} renders={child.Renders} lastN={child.LastN}");
        }

        // gate.props.equality-gated — a fresh-but-EQUAL-VALUE record (different instance, equal fields) → no re-render.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var child = new PropsChild();
            Element Tree(PropsPayload p) => new BoxEl { Children = [Embed.Comp(p, () => child)] };
            var t1 = Tree(new PropsPayload(7)); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            int afterMount = child.Renders;
            var t2 = Tree(new PropsPayload(7)); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // fresh, equal
            Check("gate.props.equality-gated a fresh-but-equal props record does not re-render the child",
                afterMount == 1 && child.Renders == 1 && child.LastN == 7, $"afterMount={afterMount} renders={child.Renders}");
        }

        // gate.props.ref-shortcircuit — the SAME reference re-supplied → the record Equals is NEVER walked and the child
        // is not re-rendered (the reuse seam short-circuits on ReferenceEquals BEFORE the equality-gated write).
        {
            CountingProps.EqualsCalls = 0;
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var child = new CountingPropsChild();
            var shared = new CountingProps(3);
            Element Tree() => new BoxEl { Children = [Embed.Comp(shared, () => child)] };
            var t1 = Tree(); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            int equalsAfterMount = CountingProps.EqualsCalls;   // seeding the signal never compares
            int rendersAfterMount = child.Renders;
            var t2 = Tree(); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // SAME shared reference re-pushed
            bool noEqualsWalk = CountingProps.EqualsCalls == equalsAfterMount;
            bool noRerender = child.Renders == rendersAfterMount;
            Check("gate.props.ref-shortcircuit same reference re-supplied → no record-Equals walk, no re-render",
                equalsAfterMount == 0 && noEqualsWalk && noRerender, $"equals={CountingProps.EqualsCalls} renders={child.Renders}");
        }

        // gate.props.element-slot-repush — an Element-typed prop (slot) re-pushed → the child subtree reconciles IN PLACE
        // (slot node identity preserved), and a sibling component's UseState in the child survives.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var child = new PropsChild { HostKeeper = true };
            Element Tree(Element slot) => new BoxEl { Children = [Embed.Comp(new PropsPayload(0, slot), () => child)] };
            var slotA = new TextEl("A") { Size = 12f };
            var t1 = Tree(slotA); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            // root box → PropsChild anchor → child's rendered box → slot (TextEl) node.
            NodeHandle SlotNode() => scene.FirstChild(scene.FirstChild(scene.FirstChild(scene.Root)));
            var slotNodeBefore = SlotNode();
            child.Keeper.Bump!(); recon.Runtime.Flush();                            // sibling state 0 → 1
            bool keeperTicked = child.Keeper.Ticks == 1;
            var slotB = new TextEl("B") { Size = 12f };
            var t2 = Tree(slotB); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // re-push a DIFFERENT slot
            var slotNodeAfter = SlotNode();
            bool inPlace = slotNodeBefore == slotNodeAfter;                         // same TextEl node reconciled in place
            bool siblingSurvived = child.Keeper.Ticks == 1;                         // UseState survived the slot re-push
            Check("gate.props.element-slot-repush slot re-push reconciles in place; sibling UseState survives",
                child.Renders == 2 && keeperTicked && inPlace && siblingSurvived,
                $"renders={child.Renders} inPlace={inPlace} keeper={child.Keeper.Ticks}");
        }

        // gate.props.key-remount — same type + equal props but a CHANGED Key → fresh instance (state reset), one level
        // above the reuse seam (the keyed child diff).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            int ctor = 0;
            Element Tree(int key, int n) => new BoxEl { Children = [Embed.Comp(new PropsPayload(n), () => { ctor++; return new PropsChild(); }) with { Key = "k" + key }] };
            var t1 = Tree(1, 5); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            bool mount = ctor == 1;
            var t2 = Tree(2, 5); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // equal props, changed Key
            Check("gate.props.key-remount a changed Key remounts a fresh instance despite equal props",
                mount && ctor == 2, $"ctor={ctor}");
        }

        // gate.props.parked-defers-replays-latest — a props write while parked defers (zero renders), and reactivation
        // replays exactly ONCE reading the LATEST value.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("props-park", new Size2(200, 200), 1f));
            window.Show();
            var probe = new PropsParkProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame();
            var childA = probe.Children["a"];
            bool mountA = childA.Renders == 1 && childA.LastN == 0;
            probe.Route!.Value = "b"; host.RunFrame();                                                   // park "a", mount "b"
            probe.ChildN.Value = 1; childA.Context.PropsSig!.Value = new PropsPayload(1); host.RunFrame();   // push reaches the PARKED entry
            bool deferred1 = childA.Renders == 1;
            probe.ChildN.Value = 2; childA.Context.PropsSig!.Value = new PropsPayload(2); host.RunFrame();   // latest push, still parked
            bool deferred2 = childA.Renders == 1;
            probe.Route.Value = "a"; host.RunFrame();                                                    // reactivate "a" (re-push carries the latest)
            bool replayOnceLatest = childA.Renders == 2 && childA.LastN == 2;
            Check("gate.props.parked-defers-replays-latest parked defers props renders; reactivation replays once with the latest",
                mountA && deferred1 && deferred2 && replayOnceLatest,
                $"renders={childA.Renders} lastN={childA.LastN} deferred={deferred1 && deferred2}");
        }

        // gate.props.single-flush-coalesce — a parent write + props delivery + child re-render all settle within ONE
        // flush (no torn intermediate, nothing owed to the next frame).
        {
            var scene = new SceneStore();
            var rt = new ReactiveRuntime();
            var recon = new TreeReconciler(scene, strings, rt);
            var driver = new Signal<int>(0);
            var parent = new PropsFlushParent(driver);
            recon.MountRoot(parent);                                 // mount: parent+child render once, child sees 0
            bool mount = parent.Renders == 1 && parent.Child.Renders == 1 && parent.Child.LastN == 0;
            driver.Value = 1;                                        // schedule the parent re-render
            rt.Flush();                                              // ONE flush drains parent → delivers → child
            bool coalesced = parent.Renders == 2 && parent.Child.Renders == 2 && parent.Child.LastN == 1 && !rt.HasPending;
            Check("gate.props.single-flush-coalesce parent write + delivery + child re-render settle in one flush",
                mount && coalesced, $"parent={parent.Renders} child={parent.Child.Renders} lastN={parent.Child.LastN} pending={rt.HasPending}");
        }

        // gate.props.zero-hot-alloc — steady-state ref-equal re-pushes add NO subscriber (the render-effect is the sole
        // PropsSig subscriber) and never re-render; the flush hot-window stays 0-alloc.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var child = new PropsChild();
            var shared = new PropsPayload(4);
            var t1 = new BoxEl { Children = [Embed.Comp(shared, () => child)] };
            var t2 = new BoxEl { Children = [Embed.Comp(shared, () => child)] };
            recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            bool oneSubscriber = child.Context.PropsSig!.SubscriberCount == 1;
            for (int w = 0; w < 6; w++) { recon.ReconcileRoot(w % 2 == 0 ? t2 : t1, w % 2 == 0 ? t1 : t2); recon.Runtime.Flush(); }
            bool stillOne = child.Context.PropsSig!.SubscriberCount == 1;
            bool neverRerendered = child.Renders == 1;
            recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // warm a ref-equal delivery (short-circuited)
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) recon.Runtime.Flush();
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("gate.props.zero-hot-alloc ref-equal re-push adds no subscriber, never re-renders; hot-window flush is 0-alloc",
                oneSubscriber && stillOne && neverRerendered && delta == 0,
                $"subs={child.Context.PropsSig!.SubscriberCount} renders={child.Renders} flushDelta={delta}B");
        }

        // gate.props.useprops-throws-propless — UseProps<T> on a propless mount THROWS naming the component + props type;
        // UsePropsOrDefault returns null propless, the value when present.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var thrower = new PropsChild();
            bool threw = false; string msg = "";
            try { recon.ReconcileRoot(new BoxEl { Children = [Embed.Comp(() => thrower)] }, null); recon.Runtime.Flush(); }
            catch (InvalidOperationException ex) { threw = true; msg = ex.Message; }
            bool named = msg.Contains("PropsPayload") && msg.Contains(nameof(PropsChild));

            var scene2 = new SceneStore(); var recon2 = new TreeReconciler(scene2, strings);
            var opt = new OptionalPropsChild();
            recon2.ReconcileRoot(new BoxEl { Children = [Embed.Comp(() => opt)] }, null); recon2.Runtime.Flush();
            bool nullPropless = opt.SawNull;

            var scene3 = new SceneStore(); var recon3 = new TreeReconciler(scene3, strings);
            var opt2 = new OptionalPropsChild();
            recon3.ReconcileRoot(new BoxEl { Children = [Embed.Comp(new PropsPayload(9), () => opt2)] }, null); recon3.Runtime.Flush();
            bool valuePresent = !opt2.SawNull;

            Check("gate.props.useprops-throws-propless UseProps throws (naming component+props) propless; UsePropsOrDefault null propless, value when present",
                threw && named && nullPropless && valuePresent, $"threw={threw} named={named} nullPropless={nullPropless} valuePresent={valuePresent}");
        }
    }

    static void PropsGenChecks(StringTable strings)
    {
        // gate.props.gen.field-level — changing ONE generated (signal-backed) prop re-renders the core once; supplying a
        // FRESH delegate (new lambda, all signal-backed fields unchanged) does NOT re-render; the wired handler (the
        // captured stable forwarder) invokes the NEWEST delegate.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var probe = new PropsGenProbe();
            int pings = 0;
            Element Tree(int count, string label, float alpha, Action? onPing)
                => new BoxEl { Children = [Embed.Comp(new PropsGenProbe.PropsData(count, label, alpha, onPing), () => probe)] };
            var t1 = Tree(1, "a", 0.5f, () => pings++); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            bool mount = probe.Renders == 1 && probe.LastCount == 1;
            var t2 = Tree(2, "a", 0.5f, () => pings++); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // one signal field changed
            bool changedReRenders = probe.Renders == 2 && probe.LastCount == 2;
            Action fresh = () => pings += 10;
            var t3 = Tree(2, "a", 0.5f, fresh); recon.ReconcileRoot(t3, t2); recon.Runtime.Flush();           // only a fresh delegate
            bool freshNoRerender = probe.Renders == 2;
            probe.Wired!.Invoke();                                                                            // stable forwarder → newest
            bool invokesNewest = pings == 10;
            Check("gate.props.gen.field-level a changed signal-prop re-renders; a fresh delegate does not; the forwarder invokes the newest",
                mount && changedReRenders && freshNoRerender && invokesNewest,
                $"mount={mount} changed={changedReRenders} freshNoRerender={freshNoRerender} pings={pings}");
        }

        // gate.props.gen.batch-coalesce — ApplyProps writing TWO changed fields (wrapped in Runtime.Batch at the reuse
        // seam) re-renders the core EXACTLY ONCE, both values landed (no torn intermediate, no double render).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var probe = new PropsGenProbe();
            Element Tree(int c, string l) => new BoxEl { Children = [Embed.Comp(new PropsGenProbe.PropsData(c, l, 0.5f, null), () => probe)] };
            var t1 = Tree(1, "a"); recon.ReconcileRoot(t1, null); recon.Runtime.Flush();
            bool mount = probe.Renders == 1;
            var t2 = Tree(2, "b"); recon.ReconcileRoot(t2, t1); recon.Runtime.Flush();   // two signal fields change at once
            bool once = probe.Renders == 2 && probe.LastCount == 2 && probe.LastLabel == "b";
            Check("gate.props.gen.batch-coalesce ApplyProps writing two changed fields re-renders the core exactly once (Batch wrap)",
                mount && once, $"renders={probe.Renders} count={probe.LastCount} label={probe.LastLabel}");
        }

        // gate.props.gen.bind-direct — a NameProp accessor (AlphaProp) bound to a node channel (Opacity) updates
        // compositor-only when the field changes: FrameStats.Rendered == false, no component re-render.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("props-gen-bind", new Size2(120, 120), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new PropsGenProbe();
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl { Children = [Embed.Comp(new PropsGenProbe.PropsData(1, "a", 0.5f, null), () => probe)] },
            });
            host.RunFrame();
            int rendersAfterMount = probe.Renders;
            float op0 = host.Scene.Paint(probe.Box).Opacity;
            ((IPropsHost)probe).ApplyProps(new PropsGenProbe.PropsData(1, "a", 0.9f, null));   // change ONLY the bound field
            var f = host.RunFrame();
            float op1 = host.Scene.Paint(probe.Box).Opacity;
            bool changed = Near(op1, 0.9f, 0.01f) && !Near(op0, op1, 0.01f);
            bool compositorOnly = !f.Rendered && probe.Renders == rendersAfterMount;
            Check("gate.props.gen.bind-direct AlphaProp bound to Opacity updates compositor-only (Rendered==false, no re-render)",
                changed && compositorOnly, $"op {op0:0.00}->{op1:0.00} rendered={f.Rendered} renders+{probe.Renders - rendersAfterMount}");
        }

        // gate.props.gen.partial-notify — each of three fields notifies ONLY its own subscribers: Count/Label (read in
        // render) re-fire the render-effect while the Alpha bind holds; Alpha (bound only) fires the bind effect while
        // the render-effect is untouched (Rendered==false, no re-render).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("props-gen-partial", new Size2(120, 120), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new PropsGenProbe();
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl { Children = [Embed.Comp(new PropsGenProbe.PropsData(1, "a", 0.5f, null), () => probe)] },
            });
            host.RunFrame();
            int r0 = probe.Renders;
            float opMount = host.Scene.Paint(probe.Box).Opacity;
            ((IPropsHost)probe).ApplyProps(new PropsGenProbe.PropsData(2, "a", 0.5f, null));   // only Count
            var fCount = host.RunFrame();
            bool countIsolated = probe.Renders == r0 + 1 && probe.LastCount == 2
                && Near(host.Scene.Paint(probe.Box).Opacity, opMount, 0.01f) && fCount.Rendered;
            ((IPropsHost)probe).ApplyProps(new PropsGenProbe.PropsData(2, "a", 0.8f, null));   // only Alpha (bound)
            var fAlpha = host.RunFrame();
            bool alphaIsolated = probe.Renders == r0 + 1 && !fAlpha.Rendered
                && Near(host.Scene.Paint(probe.Box).Opacity, 0.8f, 0.01f);
            ((IPropsHost)probe).ApplyProps(new PropsGenProbe.PropsData(2, "b", 0.8f, null));   // only Label
            host.RunFrame();
            bool labelIsolated = probe.Renders == r0 + 2 && probe.LastLabel == "b"
                && Near(host.Scene.Paint(probe.Box).Opacity, 0.8f, 0.01f);
            Check("gate.props.gen.partial-notify each field notifies only its own subscribers (Count/Label->render, Alpha->bind; others untouched)",
                countIsolated && alphaIsolated && labelIsolated,
                $"countIso={countIsolated} alphaIso={alphaIsolated} labelIso={labelIsolated} renders={probe.Renders}");
        }
    }

    static void KeyboardChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var dispatcher = new InputDispatcher(scene);

        bool clicked = false, innerSaw = false; int rootSaw = 0;
        var tree = new BoxEl
        {
            Direction = 0,
            OnKeyDown = a => rootSaw = a.KeyCode,                          // ancestor (bubble target)
            Children =
            [
                new BoxEl { Key = "b1", Width = 20, Height = 20, OnClick = () => clicked = true },
                new BoxEl { Key = "b2", Width = 20, Height = 20, Focusable = true,
                    OnKeyDown = a => { innerSaw = true; if (a.KeyCode == Keys.Escape) a.Handled = true; } },
            ],
        };
        recon.ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var b1 = Child(scene, scene.Root, 0);
        var b2 = Child(scene, scene.Root, 1);

        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
        var f1 = dispatcher.Focused;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
        var f2 = dispatcher.Focused;
        Check("19. Tab cycles focus through focusables", f1 == b1 && f2 == b2, "→ b1 → b2");

        dispatcher.SetFocus(b1);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });   // WinUI: click on key-UP
        Check("20. Enter activates the focused clickable", clicked, "OnClick fired via keyboard");

        dispatcher.SetFocus(b2);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Down) });   // not handled → bubbles
        bool bubbled = innerSaw && rootSaw == Keys.Down;
        innerSaw = false; rootSaw = 0;
        dispatcher.SetFocus(b2);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });  // b2 marks Handled → stops
        bool stopped = innerSaw && rootSaw == 0;
        Check("21. keys bubble to ancestor; Handled stops propagation", bubbled && stopped);
    }

    static void TimerHookChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // ── gate.timer.debounce-trailing: 3 writes within the window ⇒ exactly one trailing commit to the last value ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-debounce", new Size2(200, 120), 1f)); window.Show();
            var probe = new DebounceProbe(100f);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.Paint(0);
            probe.Source.Value = "a"; host.Paint(0);   // arm
            probe.Source.Value = "b"; host.Paint(0);   // re-arm
            probe.Source.Value = "c"; host.Paint(0);   // re-arm — the last value wins
            bool pendingStillInitial = probe.Debounced.Peek() == "";
            string prev = probe.Debounced.Peek();
            int transitions = 0;
            for (int i = 0; i < 16; i++) { host.Paint(0); var v = probe.Debounced.Peek(); if (v != prev) { transitions++; prev = v; } }
            bool committedOnce = transitions == 1 && probe.Debounced.Peek() == "c";
            Check("gate.timer.debounce-trailing 3 writes in the window ⇒ exactly 1 trailing commit to the last value",
                pendingStillInitial && committedOnce, $"pendingInitial={pendingStillInitial} transitions={transitions} value='{probe.Debounced.Peek()}'");
        }

        // ── gate.timer.debounce-flush: Flush() commits immediately + cancels pending; Cancel() never commits ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-flush", new Size2(200, 120), 1f)); window.Show();
            var probe = new DebounceProbe(100f);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.Paint(0);
            probe.Source.Value = "x"; host.Paint(0);      // arm
            bool beforeFlush = probe.Debounced.Peek() == "";
            probe.Handle.Flush();                          // commit NOW + cancel pending
            bool afterFlush = probe.Debounced.Peek() == "x";
            string p = probe.Debounced.Peek(); int extra = 0;
            for (int i = 0; i < 16; i++) { host.Paint(0); if (probe.Debounced.Peek() != p) { extra++; p = probe.Debounced.Peek(); } }
            bool flushNoExtra = extra == 0;

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("timer-cancel", new Size2(200, 120), 1f)); window2.Show();
            var probe2 = new DebounceProbe(100f);
            using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, probe2);
            host2.Paint(0);
            probe2.Source.Value = "y"; host2.Paint(0);     // arm
            probe2.Handle.Cancel();                         // drop the pending fire
            for (int i = 0; i < 16; i++) host2.Paint(0);
            bool cancelNoCommit = probe2.Debounced.Peek() == "";
            Check("gate.timer.debounce-flush Flush() commits immediately + cancels pending; Cancel() never commits",
                beforeFlush && afterFlush && flushNoExtra && cancelNoCommit,
                $"beforeFlush={beforeFlush} afterFlush={afterFlush} flushNoExtra={flushNoExtra} cancelNoCommit={cancelNoCommit}");
        }

        // ── gate.timer.throttle-leading-trailing: leading fires immediately; trailing samples the last value ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-throttle", new Size2(200, 120), 1f)); window.Show();
            var probe = new ThrottleProbe(100f);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.Paint(0);
            probe.Source.Value = 1; host.Paint(0);   // leading → emit immediately
            bool leading = probe.Throttled.Peek() == 1;
            probe.Source.Value = 2; host.Paint(0);   // suppressed (cooldown)
            probe.Source.Value = 3; host.Paint(0);   // suppressed, latest = 3
            bool stillLeading = probe.Throttled.Peek() == 1;
            for (int i = 0; i < 16; i++) host.Paint(0);   // window closes → trailing sample
            bool trailing = probe.Throttled.Peek() == 3;
            Check("gate.timer.throttle-leading-trailing leading emits immediately; trailing samples the last value",
                leading && stillLeading && trailing, $"leading={leading} stillLeading={stillLeading} trailing={trailing} value={probe.Throttled.Peek()}");
        }

        // ── gate.timer.interval-pauses-inactive: ticks while active, pauses while window-inactive, resumes on return ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-interval", new Size2(200, 120), 1f)); window.Show();
            var probe = new IntervalProbe(48f);   // ~3 frames per tick
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.Paint(0);
            for (int i = 0; i < 20; i++) host.Paint(0);
            bool tickedWhileActive = probe.Ticks > 0;
            host.SetWindowActive(false); host.Paint(0);   // flush the activation change → interval pauses
            int atPause = probe.Ticks;
            for (int i = 0; i < 20; i++) host.Paint(0);
            bool pausedNoTicks = probe.Ticks == atPause;
            host.SetWindowActive(true); host.Paint(0);    // resume
            for (int i = 0; i < 20; i++) host.Paint(0);
            bool resumed = probe.Ticks > atPause;
            Check("gate.timer.interval-pauses-inactive interval ticks while active, pauses while window-inactive, resumes",
                tickedWhileActive && pausedNoTicks && resumed, $"active={probe.Ticks - (probe.Ticks - atPause)} atPause={atPause} final={probe.Ticks}");
        }

        // ── gate.timer.unmount-cancels: a due-after-unmount timeout never fires ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-unmount", new Size2(200, 120), 1f)); window.Show();
            var parent = new TimeoutUnmountParent();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, parent);
            host.RunFrame();                        // mount: child arms a 200 ms timeout (due ≈ 200)
            bool notFiredYet = parent.Fires == 0;
            parent.Show.Value = false; host.Paint(0);   // unmount the child BEFORE the fire → RunAllCleanups cancels the timer
            for (int i = 0; i < 24; i++) host.Paint(0);  // advance well past 200 ms
            Check("gate.timer.unmount-cancels a due-after-unmount timeout never fires",
                notFiredYet && parent.Fires == 0, $"notFiredYet={notFiredYet} fires={parent.Fires}");
        }

        // ── gate.timer.quiesce-idle: one pending 5 s timeout — the host wait reflects the due time; no intermediate frame runs ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-quiesce", new Size2(200, 120), 1f)); window.Show();
            var probe = new TimeoutProbe(5000f);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();   // settle the mount (leaves only the pending 5 s timer)
            int wait = host.RecommendedWaitMs();
            bool waitReflectsDue = wait >= 4000 && wait <= 5100;
            bool noTimerWake = (host.CurrentWakeReasons & WakeReasons.Timer) == 0;   // pending, not due
            bool wouldIdle = !host.HasActiveWork;                                     // the loop would fully block
            double clockBefore = host.FrameClockMsForTest;
            bool anyRendered = false;
            for (int i = 0; i < 8; i++) { var f = host.RunFrame(); if (f.Rendered) anyRendered = true; }
            bool noIntermediateFrames = !anyRendered && probe.Fires == 0 && host.FrameClockMsForTest == clockBefore;
            Check("gate.timer.quiesce-idle a pending 5s timeout: the wait reflects the due time and no intermediate frame runs",
                waitReflectsDue && noTimerWake && wouldIdle && noIntermediateFrames,
                $"wait={wait} noTimerWake={noTimerWake} wouldIdle={wouldIdle} rendered={anyRendered} fires={probe.Fires}");
        }

        // ── gate.timer.zero-steady-alloc: an armed timer adds 0 bytes to the hot phase on quiet frames ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-alloc", new Size2(200, 120), 1f)); window.Show();
            var probe = new IntervalProbe(100000f);   // armed the whole time, never fires during the window
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();
            for (int i = 0; i < 6; i++) host.Paint(0);   // warm the drain path (JIT) with the timer armed
            long worst = 0;
            for (int i = 0; i < 10; i++) { var f = host.Paint(0); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
            Check("gate.timer.zero-steady-alloc an armed timer adds 0 bytes to the hot phase on quiet frames",
                worst == 0, $"worst={worst} bytes (armed timers={host.TimersForTest.Count})");
        }

        // ── gate.timer.warm-cadence: frames continue for the hold window after a synthetic input, then quiesce ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("timer-warm", new Size2(200, 120), 1f)); window.Show();
            var probe = new InertBoxProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.WarmCadenceEnabledForTest = true;   // off headless by default (keeps existing idle gates green); the gate opts in
            for (int i = 0; i < 6 && host.HasActiveWork; i++) host.RunFrame();
            bool idleBefore = !host.HasActiveWork;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(10f, 10f), 0, 0));
            host.RunFrame();                          // dispatch → warm-cadence armed for ~1 s
            bool warmLit = (host.CurrentWakeReasons & WakeReasons.WarmCadence) != 0;
            int warmFrames = 0;
            for (int i = 0; i < 200; i++) { if ((host.CurrentWakeReasons & WakeReasons.WarmCadence) == 0) break; host.RunFrame(); warmFrames++; }
            bool held = warmFrames >= 50 && warmFrames <= 75;   // ~1000 ms / 16 ms ≈ 62 frames
            for (int i = 0; i < 8 && host.HasActiveWork; i++) host.RunFrame();
            bool quiescedAfter = !host.HasActiveWork;
            Check("gate.timer.warm-cadence frames continue for the ~1s hold after input, then quiesce",
                idleBefore && warmLit && held && quiescedAfter,
                $"idleBefore={idleBefore} warmLit={warmLit} warmFrames={warmFrames} quiescedAfter={quiescedAfter}");
        }
    }

    static void AsyncCommandChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("async-cmd", new Size2(320, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        // ── single command: lifecycle + re-entry guard ──
        var probe = new AsyncCommandProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        var gate = new TaskCompletionSource<bool>();
        int starts = 0;
        probe.Cmd.Run(_ => { starts++; return gate.Task; });
        bool runningWhilePending = probe.Cmd.IsRunningNow;
        probe.Cmd.Run(_ => { starts++; return Task.CompletedTask; });   // BLOCKED — first is still in flight (op not invoked)
        bool blocked = starts == 1;
        gate.SetResult(true);
        for (int k = 0; k < 16 && probe.Cmd.IsRunningNow; k++) host.RunFrame();
        bool clearedAfter = !probe.Cmd.IsRunningNow;
        Check("AC-1/2. async command: IsRunning tracks the Task, and a re-fire while running is blocked",
            runningWhilePending && blocked && clearedAfter,
            $"runningWhilePending={runningWhilePending} opStarts={starts} clearedAfter={clearedAfter}");

        // ── cancellation: Cancel() clears IsRunning; a late completion of the underlying task does not revive it ──
        var gate2 = new TaskCompletionSource<bool>();
        probe.Cmd.Run(ct => gate2.Task.WaitAsync(ct));
        bool runningC = probe.Cmd.IsRunningNow;
        probe.Cmd.Cancel();
        for (int k = 0; k < 16 && probe.Cmd.IsRunningNow; k++) host.RunFrame();
        bool clearedAfterCancel = !probe.Cmd.IsRunningNow;
        gate2.TrySetResult(true);
        host.RunFrame();
        bool stayedCleared = !probe.Cmd.IsRunningNow;
        Check("AC-3. async command: Cancel() clears IsRunning; a late completion does not revive it",
            runningC && clearedAfterCancel && stayedCleared,
            $"runningC={runningC} clearedAfterCancel={clearedAfterCancel} stayedCleared={stayedCleared}");

        // ── 0-alloc when idle ──
        var fIdle = host.RunFrame();
        Check("AC-5. an idle async-command frame is 0-alloc on the hot phases",
            fIdle.HotPhaseAllocBytes == 0, $"{fIdle.HotPhaseAllocBytes} bytes");

        // ── keyed: per-key independence ──
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("async-cmd-keyed", new Size2(320, 240), 1f));
        window2.Show();
        var kp = new AsyncCommandsProbe();
        using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), fonts, strings, kp);
        host2.RunFrame();
        var g1 = new TaskCompletionSource<bool>();
        var g2 = new TaskCompletionSource<bool>();
        kp.Cmds.Run(1, _ => g1.Task);
        kp.Cmds.Run(2, _ => g2.Task);
        bool bothRunning = kp.Cmds.IsRunningNow(1) && kp.Cmds.IsRunningNow(2);
        g1.SetResult(true);
        for (int k = 0; k < 16 && kp.Cmds.IsRunningNow(1); k++) host2.RunFrame();
        bool oneDoneTwoRunning = !kp.Cmds.IsRunningNow(1) && kp.Cmds.IsRunningNow(2);
        g2.SetResult(true);
        for (int k = 0; k < 16 && kp.Cmds.IsRunningNow(2); k++) host2.RunFrame();
        bool allDone = !kp.Cmds.IsRunningNow(2);
        Check("AC-4. keyed async commands: each key's IsRunning is independent (key 1 finishing leaves key 2 running)",
            bothRunning && oneDoneTwoRunning && allDone,
            $"both={bothRunning} oneDoneTwoRunning={oneDoneTwoRunning} allDone={allDone}");
    }

    static void GranularityChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gran", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        Gran.Counts[0] = 0; Gran.Counts[1] = 0; Gran.Parent = 0;
        using var host = new AppHost(app, window, device, fonts, strings, new GranParent());
        host.RunFrame();
        int p0 = Gran.Parent, a0 = Gran.Counts[0], b0 = Gran.Counts[1];

        var child0 = Child(host.Scene, host.Scene.Root, 0);     // GranChild(0) anchor
        var box0 = Child(host.Scene, child0, 0);                // its rendered clickable box
        var r = host.Scene.AbsoluteRect(box0);
        var c = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        var f = host.RunFrame();

        bool only0 = Gran.Counts[0] == a0 + 1 && Gran.Counts[1] == b0 && Gran.Parent == p0;
        Check("59. setState re-renders ONLY the owning component (granular, not the app)",
            only0 && f.ComponentsRendered == 1 && HasGlyph(device, strings, "c0:1"),
            $"c0+{Gran.Counts[0] - a0} c1+{Gran.Counts[1] - b0} parent+{Gran.Parent - p0} componentsRendered={f.ComponentsRendered}");
    }

    static void SliderSignalChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("slidersig", new Size2(320, 120), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        SliderSignalProbe.Renders = 0;
        var root = new SliderSignalProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        int renders0 = SliderSignalProbe.Renders;

        var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);   // the unified Create wraps a stable root; find the track by role
        var thumbRow = Child(host.Scene, track, 1);
        var thumb = Child(host.Scene, thumbRow, 0);
        float dx0 = host.Scene.Paint(thumb).LocalTransform.Dx;

        root.Sig!.Value = 0.7f;          // a drag would do exactly this
        var f = host.RunFrame();
        float dx1 = host.Scene.Paint(thumb).LocalTransform.Dx;

        bool moved = MathF.Abs(dx1 - dx0) > 50f;                  // ~0.4 * 200 = 80px
        bool noRerender = SliderSignalProbe.Renders == renders0;  // the owning component did NOT re-render
        bool compositorOnly = !f.Rendered;                        // no reconcile + no layout this frame
        Check("60. signal-bound slider: value→transform, NO re-render/reconcile/layout (the slider tank, fixed)",
            moved && noRerender && compositorOnly,
            $"thumbDx {dx0:0}→{dx1:0} renders+{SliderSignalProbe.Renders - renders0} rendered={f.Rendered}");
    }

    static void SliderUnifiedChecks(StringTable strings)
    {
        // gate.ctl.slider.one-api — ranged [min,max] mapping + step snap + the WinUI keyboard, ALL through Slider.Create.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sl-oneapi", new Size2(360, 220), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new SliderUnifiedProbe { Caller = new FloatSignal(0f), Opts = new Slider.SliderOptions { Min = 0f, Max = 100f, Step = 10f, TickFrequency = 20f } };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var p = new Point2(tr.X + 94f, tr.Y + tr.H / 2f);          // raw 0.47 → 47 → step-10 snap → 50
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));   // press+release also focuses the track for the keys
            host.RunFrame();
            float snapped = root.Val;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
            float afterRight = root.Val;                              // +SmallChange (auto range/100 = 1) → 51
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Home)); host.RunFrame();
            float afterHome = root.Val;                               // Minimum → 0
            Check("gate.ctl.slider.one-api ranged [min,max] mapping + step-10 snap + keyboard (SmallChange/Home) all through the single Slider.Create",
                Near(snapped, 50f) && Near(afterRight, 51f) && Near(afterHome, 0f),
                $"snapped={snapped:0.#} right={afterRight:0.#} home={afterHome:0.#}");
        }

        // gate.ctl.slider.automaterialize — value:null scrubs via the control's OWN signal; a caller signal controls it
        // externally (a programmatic write moves the thumb on the compositor bind, no re-render).
        bool ownedScrubs;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sl-auto", new Size2(360, 220), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new SliderUnifiedProbe { Caller = null, Opts = null };   // null value ⇒ control-owned (0..1)
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var p = new Point2(tr.X + 120f, tr.Y + tr.H / 2f);        // 0.6 of length 200
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            host.RunFrame();
            ownedScrubs = Near(root.Val, 0.6f, 0.02f);                 // the internal signal drove onChange
            window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0)); host.RunFrame();
        }
        bool externalMoves; float exDx0 = 0f, exDx1 = 0f; bool exRendered = true;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sl-ext", new Size2(360, 220), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var caller = new FloatSignal(0.2f);
            var root = new SliderUnifiedProbe { Caller = caller, Opts = null };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var thumbRow = Child(host.Scene, track, 1);
            var thumb = Child(host.Scene, thumbRow, 0);
            exDx0 = host.Scene.Paint(thumb).LocalTransform.Dx;
            caller.Value = 0.9f;                                       // external programmatic write
            var f = host.RunFrame();
            exDx1 = host.Scene.Paint(thumb).LocalTransform.Dx;
            exRendered = f.Rendered;
            externalMoves = (exDx1 - exDx0) > 50f && !exRendered;      // moved on the compositor bind, no re-render
        }
        Check("gate.ctl.slider.automaterialize value:null scrubs via its own signal; a caller signal controls it externally (compositor-only)",
            ownedScrubs && externalMoves, $"owned={ownedScrubs} extDx {exDx0:0}→{exDx1:0} rendered={exRendered}");

        // gate.ctl.slider.tooltip-bind — the tooltip opens on press, then drag moves keep FrameStats.Rendered == false.
        // A 0..1 continuous slider formats the readout as "0" across [0,0.5): the thumb rides the compositor bind while
        // the bound tooltip text is unchanged (the effect early-returns on paint.Text == next → no LayoutDirty), so a
        // scrub with the bubble UP stays compositor-only — the bubble follows via OverlayHost.AfterAnimations.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("sl-tip", new Size2(360, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var caller = new FloatSignal(0.1f);
            var root = new SliderUnifiedProbe { Caller = caller, Opts = null };   // tooltip enabled by default
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var thumbRow = Child(host.Scene, track, 1);
            var thumb = Child(host.Scene, thumbRow, 0);
            var p = new Point2(tr.X + 20f, tr.Y + tr.H / 2f);         // value 0.1 → readout "0"
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            host.RunFrame(); host.RunFrame();                          // press → tooltip opens + places (this renders — expected)
            bool opened = HasGlyph(device, strings, "0");
            float dx0 = host.Scene.Paint(thumb).LocalTransform.Dx;
            bool dragCompositorOnly = true;
            for (int i = 1; i <= 3; i++)                               // drag within [0,0.5): readout stays "0"
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(tr.X + 20f + i * 20f, tr.Y + tr.H / 2f), 0, 0));
                var f = host.RunFrame();
                if (f.Rendered) dragCompositorOnly = false;
            }
            float dx1 = host.Scene.Paint(thumb).LocalTransform.Dx;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(tr.X + 80f, tr.Y + tr.H / 2f), 0, 0));
            host.RunFrame();
            bool moved = (dx1 - dx0) > 20f;
            Check("gate.ctl.slider.tooltip-bind tooltip opens on press, then drag moves keep FrameStats.Rendered == false (gate-60 contract WITH the tooltip)",
                opened && dragCompositorOnly && moved, $"opened={opened} compositorOnly={dragCompositorOnly} thumbDx {dx0:0}→{dx1:0}");
        }
    }

    static void FlowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flow", new Size2(320, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        FlowProbe.Renders = 0;
        var root = new FlowProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        int r0 = FlowProbe.Renders;
        bool init = HasGlyph(device, strings, "row0") && HasGlyph(device, strings, "row2") && !HasGlyph(device, strings, "row3") && HasGlyph(device, strings, "SHOWN");

        root.Count!.Value = 5;
        host.RunFrame();
        bool grew = HasGlyph(device, strings, "row4") && FlowProbe.Renders == r0;

        root.Toggle!.Value = false;
        host.RunFrame();
        bool toggled = HasGlyph(device, strings, "HIDDEN") && !HasGlyph(device, strings, "SHOWN") && FlowProbe.Renders == r0;

        Check("61. reactive For/Show restructure the tree with NO parent re-render", init && grew && toggled,
            $"init={init} grew={grew} toggled={toggled} parentRenders+{FlowProbe.Renders - r0}");
    }

    static void FlowReorderChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flowreorder", new Size2(320, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        FlowReorderProbe.Renders = 0;
        var root = new FlowReorderProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        int r0 = FlowReorderProbe.Renders;

        var forHost = Child(host.Scene, host.Scene.Root, 0);
        var rowA = Child(host.Scene, forHost, 0);                 // the "fa" row — identity must survive the reorder
        var b0 = host.Scene.Bounds(rowA);

        var rev = new List<string>(root.Items!.Peek()); rev.Reverse();
        root.Items!.Value = rev;                                  // pure move: no add, no remove, count unchanged
        host.RunFrame();
        var b1 = host.Scene.Bounds(rowA);

        bool lastIsA = Child(host.Scene, forHost, 2) == rowA;     // scene order reversed, node preserved by key
        bool movedInLayout = MathF.Abs(b1.X - b0.X) + MathF.Abs(b1.Y - b0.Y) > 10f;   // and layout actually moved it
        Check("61b. Flow.For pure reorder (reverse) relayouts the rows (key-preserved node moves slots)",
            lastIsA && movedInLayout && FlowReorderProbe.Renders == r0,
            $"rowA ({b0.X:0},{b0.Y:0})→({b1.X:0},{b1.Y:0}) lastIsA={lastIsA} parentRenders+{FlowReorderProbe.Renders - r0}");
    }

    static void FlowShowRefreshChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flowshowrefresh", new Size2(320, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        FlowShowRefreshProbe.Renders = 0;
        var root = new FlowShowRefreshProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();

        var showHost = Child(host.Scene, host.Scene.Root, 0);      // the Show boundary node
        var branch0 = Child(host.Scene, showHost, 0);              // the active branch box — identity must survive the refresh
        bool init = HasGlyph(device, strings, "alpha") && !branch0.IsNull;

        root.Label!.Value = "beta";                               // mutate parent-read state → parent re-renders
        host.RunFrame();
        var branch1 = Child(host.Scene, showHost, 0);
        bool refreshed = HasGlyph(device, strings, "beta") && !HasGlyph(device, strings, "alpha");   // new text, old gone
        bool reRendered = FlowShowRefreshProbe.Renders > 1;       // the parent actually re-rendered (not a Show-internal toggle)
        bool sameNode = branch1 == branch0 && !branch1.IsNull;    // in-place Update, NOT a remount

        root.Show!.Value = false;                                 // the condition still hides the branch afterwards
        host.RunFrame();
        bool hidden = !HasGlyph(device, strings, "beta") && Child(host.Scene, showHost, 0).IsNull;

        Check("61c. parent re-render refreshes a Show boundary's branch in place (new children, same node, still hides)",
            init && refreshed && reRendered && sameNode && hidden,
            $"init={init} refreshed={refreshed} reRendered={reRendered} sameNode={sameNode} hidden={hidden} parentRenders={FlowShowRefreshProbe.Renders}");
    }

    static void ForTypedChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.for.typed-keyed-diff — insert/remove/reorder preserve a row's scene node (⇒ its component state) by key.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("for-keyed", new Size2(240, 400), 1f)); window.Show();
            var probe = new ForKeyedDiffProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var forHost = Child(host.Scene, host.Scene.Root, 0);
            var rowB = Child(host.Scene, forHost, 1);            // the "b" row — its handle must survive every mutation
            bool init = !rowB.IsNull && DirectChildCount(host.Scene, forHost) == 3;

            probe.Items.Value = new[] { "d", "a", "b", "c" };    // insert 'd' at front → b shifts to index 2, node preserved
            host.RunFrame();
            bool afterInsert = Child(host.Scene, forHost, 2) == rowB && DirectChildCount(host.Scene, forHost) == 4;

            probe.Items.Value = new[] { "d", "b", "c" };         // remove 'a' → b at index 1, node preserved
            host.RunFrame();
            bool afterRemove = Child(host.Scene, forHost, 1) == rowB && DirectChildCount(host.Scene, forHost) == 3;

            probe.Items.Value = new[] { "c", "b", "d" };         // pure reorder → b at index 1, node preserved
            host.RunFrame();
            bool afterReorder = Child(host.Scene, forHost, 1) == rowB && DirectChildCount(host.Scene, forHost) == 3;

            Check("gate.for.typed-keyed-diff insert/remove/reorder preserve a row's node (state) by key",
                init && afterInsert && afterRemove && afterReorder,
                $"init={init} insert={afterInsert} remove={afterRemove} reorder={afterReorder}");
        }

        // gate.for.snapshot-single-read — the items source is read EXACTLY ONCE per boundary run (kills the old N+1).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("for-snapshot", new Size2(240, 400), 1f)); window.Show();
            var probe = new ForSnapshotProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            int afterMount = probe.ItemsReads;                   // 1 read for 5 rows (not 6)
            probe.Items.Value = new[] { 1, 2, 3, 4, 5, 6 };
            host.RunFrame();
            int afterChange = probe.ItemsReads;                  // +1 for the structural change
            Check("gate.for.snapshot-single-read the items thunk runs exactly once per boundary run (N rows, 1 read)",
                afterMount == 1 && afterChange == 2, $"afterMount={afterMount} afterChange={afterChange}");
        }

        // gate.for.update-repoints-closures — a parent re-render rebuilds the For with a fresh Row closure; rows must
        // reflect the NEW captured state IN PLACE (same node), not freeze at mount. Pre-fix (ForEl.Update no-op) fails.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("for-update", new Size2(240, 400), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            ForUpdateProbe.Renders = 0;
            var probe = new ForUpdateProbe();
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            host.RunFrame();
            var forHost = Child(host.Scene, host.Scene.Root, 0);
            var row0 = Child(host.Scene, forHost, 0);            // the "k1" row — identity must survive the parent re-render
            bool init = HasGlyph(device, strings, "A1") && !row0.IsNull;

            probe.Prefix!.Value = "B";                           // mutate parent-read state → parent re-renders → fresh Row closure
            host.RunFrame();
            var row0b = Child(host.Scene, forHost, 0);
            bool refreshed = HasGlyph(device, strings, "B1") && !HasGlyph(device, strings, "A1");
            bool reRendered = ForUpdateProbe.Renders > 1;
            bool sameNode = row0b == row0 && !row0b.IsNull;      // in-place update by key, NOT a remount
            Check("gate.for.update-repoints-closures parent re-render re-points the For row closures in place (UpdateShow parity)",
                init && refreshed && reRendered && sameNode,
                $"init={init} refreshed={refreshed} reRendered={reRendered} sameNode={sameNode} renders={ForUpdateProbe.Renders}");
        }

        // gate.for.duplicate-key-tripwire (DEBUG) — two rows sharing a key throw inside Fill, naming the key.
        {
#if DEBUG
            bool threw = false; string detail = "no throw";
            try
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("for-dup", new Size2(200, 120), 1f)); window.Show();
                var probe = new ForDupKeyProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("duplicate key")) { threw = true; detail = "threw naming the dup key"; }
            catch (Exception ex) { detail = "wrong exception: " + ex.GetType().Name; }
            Check("gate.for.duplicate-key-tripwire DEBUG: two rows sharing a key throw inside Fill", threw, detail);
#else
            Check("gate.for.duplicate-key-tripwire DEBUG: two rows sharing a key throw inside Fill", true, "release: DEBUG-only tripwire compiled out");
#endif
        }

        // gate.for.effect-zero-steady-alloc — a settled For adds 0 bytes to the hot phase on a quiet frame.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("for-alloc", new Size2(240, 300), 1f)); window.Show();
            var probe = new ForAllocProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4 && host.HasActiveWork; i++) host.RunFrame();
            for (int i = 0; i < 6; i++) host.Paint(0);           // warm the paths (JIT)
            long worst = 0;
            for (int i = 0; i < 10; i++) { var f = host.Paint(0); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
            Check("gate.for.effect-zero-steady-alloc a settled For adds 0 bytes to the hot phase on a quiet frame",
                worst == 0, $"worst={worst} bytes");
        }
    }

    static void ResourceChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // gate.resource.epoch-ordering — start A (slow), re-key to B (fast); B lands; A completing LATER is dropped by
        // the epoch guard (the loader ignores cancellation, so only the epoch stamp — not the token — can drop A).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-epoch", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe { ObserveCancellation = false };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);   // load A started (deps=0)
            probe.Key.Value = 1; host.RunFrame();                                       // re-key → load B (deps=1)
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 2, 3000);
            probe.Gates[1].SetResult("B");
            bool bLanded = PumpUntil(host, () => probe.Res.Loadable.IsReady && probe.Res.Loadable.Value.Peek() == "B");
            probe.Gates[0].SetResult("A");                                              // the superseded (older-epoch) load completes late
            for (int i = 0; i < 24; i++) host.RunFrame();
            bool aDropped = probe.Res.Loadable.Value.Peek() == "B";
            Check("gate.resource.epoch-ordering out-of-order completion never regresses: B lands, older A is dropped",
                bLanded && aDropped, $"bLanded={bLanded} aDropped={aDropped} value='{probe.Res.Loadable.Value.Peek()}'");
        }

        // gate.resource.refresh-swr — Refresh() keeps Ready(old) visible while fetching (IsFetching true), lands
        // Ready(new); IsFetching toggles false; IsStale flips true after Ready (staleTime=0 default).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-swr", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);
            probe.Gates[0].SetResult("v0");
            bool ready0 = PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v0");
            bool staleAfterReady = probe.Res.IsStale.Peek();                            // staleTime=0 ⇒ stale as soon as Ready
            bool notFetching0 = !probe.Res.IsFetching.Peek();

            probe.Res.Refresh();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 2, 3000);
            bool duringSwr = probe.Res.Loadable.IsReady && probe.Res.Loadable.Value.Peek() == "v0"
                             && probe.Res.IsFetching.Peek() && !probe.Res.IsStale.Peek();
            probe.Gates[1].SetResult("v1");
            bool ready1 = PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v1" && !probe.Res.IsFetching.Peek());
            Check("gate.resource.refresh-swr Refresh keeps Ready(old) while fetching, lands Ready(new); IsFetching/IsStale toggle",
                ready0 && staleAfterReady && notFetching0 && duringSwr && ready1,
                $"ready0={ready0} staleAfterReady={staleAfterReady} notFetching0={notFetching0} duringSwr={duringSwr} ready1={ready1}");
        }

        // gate.resource.refresh-failure-keeps-data — a refresh that FAILS keeps the prior Ready value + sets LastError.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-failkeep", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);
            probe.Gates[0].SetResult("v0");
            PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v0");
            probe.Res.Refresh();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 2, 3000);
            probe.Gates[1].SetException(new InvalidOperationException("boom"));
            bool settled = PumpUntil(host, () => !probe.Res.IsFetching.Peek() && probe.Res.LastError is not null);
            bool keptData = probe.Res.Loadable.IsReady && probe.Res.Loadable.Value.Peek() == "v0";
            bool errored = probe.Res.LastError is not null;
            Check("gate.resource.refresh-failure-keeps-data a failed refresh keeps Ready(old) + sets LastError",
                settled && keptData && errored,
                $"settled={settled} keptData={keptData} err={(errored ? probe.Res.LastError!.Message : "null")}");
        }

        // gate.resource.mutate-optimistic — Mutate writes Ready(optimistic) immediately; the revalidation replaces it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-mutate", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);
            probe.Gates[0].SetResult("v0");
            PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v0");
            probe.Res.Mutate("optimistic");
            bool optimisticNow = probe.Res.Loadable.IsReady && probe.Res.Loadable.Value.Peek() == "optimistic" && probe.Res.IsFetching.Peek();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 2, 3000);
            probe.Gates[1].SetResult("revalidated");
            bool revalidated = PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "revalidated");
            Check("gate.resource.mutate-optimistic Mutate shows the optimistic value immediately; revalidation replaces it",
                optimisticNow && revalidated, $"optimisticNow={optimisticNow} revalidated={revalidated}");
        }

        // gate.resource.keep-previous-data — a deps change with KeepPreviousData keeps the old value + IsFetching visible.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-keepprev", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe { Options = new ResourceOptions { KeepPreviousData = true } };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);
            probe.Gates[0].SetResult("v0");
            PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v0");
            probe.Key.Value = 1; host.RunFrame();                                       // deps change — keep-previous
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 2, 3000);
            bool keptPrev = probe.Res.Loadable.IsReady && probe.Res.Loadable.Value.Peek() == "v0" && probe.Res.IsFetching.Peek();
            probe.Gates[1].SetResult("v1");
            bool landedNew = PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v1");
            Check("gate.resource.keep-previous-data deps change shows the previous value + IsFetching until the new lands",
                keptPrev && landedNew, $"keptPrev={keptPrev} landedNew={landedNew}");
        }

        // gate.resource.deps-rekey-pending — default (no KeepPreviousData): a deps change resets to Pending(seed).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-rekey", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);
            probe.Gates[0].SetResult("v0");
            PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v0");
            probe.Key.Value = 1; host.RunFrame();                                       // deps change — reset to Pending(seed)
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 2, 3000);
            bool pendingSeed = probe.Res.Loadable.IsLoading && probe.Res.Loadable.Value.Peek() == "" && probe.Res.IsFetching.Peek();
            Check("gate.resource.deps-rekey-pending a deps change (default) resets the resource to Pending(seed)",
                pendingSeed, $"isLoading={probe.Res.Loadable.IsLoading} value='{probe.Res.Loadable.Value.Peek()}' fetching={probe.Res.IsFetching.Peek()}");
        }

        // gate.resource.stale-timer — staleTime>0: IsStale stays false after Ready, then flips true once the
        // HostTimerQueue one-shot fires (frame-clock driven; NOT the media clock).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("res-stale", new Size2(200, 120), 1f)); window.Show();
            var probe = new ResourceProbe { Options = new ResourceOptions { StaleTimeMs = 200f } };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            System.Threading.SpinWait.SpinUntil(() => probe.Gates.Count >= 1, 3000);
            probe.Gates[0].SetResult("v0");
            PumpUntil(host, () => probe.Res.Loadable.Value.Peek() == "v0");
            bool freshAfterReady = !probe.Res.IsStale.Peek();                           // still fresh right after Ready
            for (int i = 0; i < 20; i++) host.Paint(0);                                 // advance ~320 ms past the 200 ms stale timer
            bool staleAfterTimeout = probe.Res.IsStale.Peek();
            Check("gate.resource.stale-timer staleTime>0 keeps data fresh until the HostTimerQueue one-shot fires",
                freshAfterReady && staleAfterTimeout, $"freshAfterReady={freshAfterReady} staleAfterTimeout={staleAfterTimeout}");
        }
    }

    static void PropNetClobberChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("prop-net", new Size2(420, 420), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        var rr = new Signal<int>(0);                      // owner re-render trigger (read by Build)
        var fill = new Signal<ColorF>(ColorF.FromRgba(0xE8, 0x3C, 0x3C, 0xFF));
        var op = new Signal<float>(0.8f);
        var w = new Signal<float>(64f);
        var h = new Signal<float>(24f);
        var tx = new Signal<float>(12f);
        var txt = new Signal<string>("t1");
        var col = new Signal<ColorF>(ColorF.FromRgba(0x18, 0xA0, 0x57, 0xFF));
        var tint = new Signal<ColorF>(ColorF.FromRgba(0x2D, 0x7D, 0xF6, 0xFF));
        var canCol = new Signal<ColorF>(ColorF.FromRgba(0xF5, 0xC5, 0x18, 0xFF));
        var hov = new Signal<ColorF>(ColorF.FromRgba(0x00, 0x00, 0x00, 0x12));
        var prs = new Signal<ColorF>(ColorF.FromRgba(0x00, 0x00, 0x00, 0x17));

        NodeHandle nFill = default, nOp = default, nW = default, nH = default, nT = default, nHov = default,
                   wTxt = default, wCol = default, wImg = default, wCan = default;   // wrappers: OnRealized is BoxEl-only
        var root = new W0fStaticProbe
        {
            Build = () =>
            {
                int r = rr.Value;                          // subscribe: rr bump re-renders the owner
                float proof = 0.5f + 0.25f * r;            // reconcile proof: lands in paint.BorderWidth each render
                return new BoxEl
                {
                    Direction = 1, Width = 400, Height = 400, Gap = 2,
                    Children = new Element[]
                    {
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, Fill = Prop.Of(() => fill.Value), OnRealized = nh => nFill = nh },
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, Fill = ColorF.FromRgba(0x20, 0x20, 0x20, 0xFF), Opacity = Prop.Of(() => op.Value), OnRealized = nh => nOp = nh },
                        new BoxEl { Height = 10, BorderWidth = proof, Width = Prop.Of(() => w.Value), OnRealized = nh => nW = nh },
                        new BoxEl { Width = 40, BorderWidth = proof, Height = Prop.Of(() => h.Value), OnRealized = nh => nH = nh },
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, Transform = Prop.Of(() => Affine2D.Translation(tx.Value, 0f)), OnRealized = nh => nT = nh },
                        new BoxEl { Width = 40, Height = 10, BorderWidth = proof, HoverFill = Prop.Of(() => hov.Value), PressedFill = Prop.Of(() => prs.Value), OnRealized = nh => nHov = nh },
                        new BoxEl { OnRealized = nh => wTxt = nh, Children = [ new TextEl("") { Underline = (r & 1) == 1, Text = Prop.Of(() => txt.Value) } ] },
                        new BoxEl { OnRealized = nh => wCol = nh, Children = [ new TextEl("c") { Underline = (r & 1) == 1, Color = Prop.Of(() => col.Value) } ] },
                        new BoxEl { OnRealized = nh => wImg = nh, Children = [ new ImageEl { Width = 24, Height = 24, Placeholder = Prop.Of(() => tint.Value) } ] },
                        // the EditableText shape, post-unification: static+bind coexistence on one channel is now a
                        // COMPILE ERROR (CS1912 duplicate initializer) — the bind owns Color outright; the disabled
                        // ramp stays on its own DisabledColor field (recorder-composited, a different channel).
                        new BoxEl { OnRealized = nh => wCan = nh, Children = [ new TextEl("canary") { DisabledColor = Tok.TextDisabled, Underline = (r & 1) == 1, Color = Prop.Of(() => canCol.Value) } ] },
                    },
                };
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        NodeHandle nTxt = host.Scene.FirstChild(wTxt), nCol = host.Scene.FirstChild(wCol),
                   nImg = host.Scene.FirstChild(wImg), nCan = host.Scene.FirstChild(wCan);

        // fire every bound signal ONCE (new values), no re-render in between
        fill.Value = ColorF.FromRgba(0x8B, 0x3C, 0xC9, 0xFF);
        op.Value = 0.4f; w.Value = 96f; h.Value = 48f; tx.Value = 31f;
        txt.Value = "t2"; col.Value = ColorF.FromRgba(0xE8, 0x8C, 0x1C, 0xFF);
        tint.Value = ColorF.FromRgba(0x18, 0xA0, 0xA0, 0xFF); canCol.Value = ColorF.FromRgba(0x3C, 0x8B, 0xC9, 0xFF);
        hov.Value = ColorF.FromRgba(0x00, 0x00, 0x00, 0x22); prs.Value = ColorF.FromRgba(0x00, 0x00, 0x00, 0x2E);
        host.RunFrame();

        // OWNER re-render: fresh element records, bound signals untouched
        rr.Value = 1;
        host.RunFrame();

        bool reconciled = Near(host.Scene.Paint(nFill).BorderWidth, 0.75f, 0.001f)
                          && (host.Scene.Paint(nTxt).TextDecorations & NodePaint.UnderlineBit) != 0;
        Check("prop-net.reconciled owner re-render actually rewrote columns on the probed nodes",
            reconciled, $"bw={host.Scene.Paint(nFill).BorderWidth} deco={host.Scene.Paint(nTxt).TextDecorations}");

        Check("prop-net.fill bound Fill survives an owner re-render between signal fires",
            host.Scene.Paint(nFill).Fill == fill.Peek(), $"paint={host.Scene.Paint(nFill).Fill} want={fill.Peek()}");
        Check("prop-net.hoverfill bound HoverFill/PressedFill re-fire and survive an owner re-render (theme/palette-reactive row states)",
            host.Scene.Paint(nHov).HoverFill == hov.Peek() && host.Scene.Paint(nHov).PressedFill == prs.Peek(),
            $"hover={host.Scene.Paint(nHov).HoverFill} pressed={host.Scene.Paint(nHov).PressedFill}");
        Check("prop-net.opacity bound Opacity survives an owner re-render",
            Near(host.Scene.Paint(nOp).Opacity, 0.4f, 0.001f), $"paint={host.Scene.Paint(nOp).Opacity}");
        Check("prop-net.width bound Width survives an owner re-render",
            Near(host.Scene.Layout(nW).Width, 96f, 0.001f), $"li={host.Scene.Layout(nW).Width}");
        Check("prop-net.height bound Height survives an owner re-render",
            Near(host.Scene.Layout(nH).Height, 48f, 0.001f), $"li={host.Scene.Layout(nH).Height}");
        Check("prop-net.transform bound Transform survives an owner re-render (identity-gate skips the static)",
            Near(host.Scene.Paint(nT).LocalTransform.Dx, 31f, 0.001f), $"dx={host.Scene.Paint(nT).LocalTransform.Dx}");
        Check("prop-net.text bound Text survives an owner re-render",
            host.Scene.Paint(nTxt).Text == strings.Intern("t2"), $"text-id={host.Scene.Paint(nTxt).Text}");
        Check("prop-net.textcolor bound Color survives an owner re-render between signal fires",
            host.Scene.Paint(nCol).TextColor == col.Peek(), $"paint={host.Scene.Paint(nCol).TextColor} want={col.Peek()}");
        Check("prop-net.placeholder bound Placeholder survives an owner re-render",
            host.Scene.Paint(nImg).Fill == tint.Peek(), $"paint={host.Scene.Paint(nImg).Fill}");
        Check("prop-net.canary bound Color owns the channel outright (static+bind coexistence is now a compile error)",
            host.Scene.Paint(nCan).TextColor == canCol.Peek(), $"paint={host.Scene.Paint(nCan).TextColor} want={canCol.Peek()}");
    }

    static void PropUnionChecks(StringTable strings)
    {
        // Signal-direct: a concrete Signal<T>/FloatSignal assigned straight to the channel property — no user
        // closure; the engine effect reads sig.Value. Paint-only writes must stay compositor-only (Rendered=false).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("prop-signal-direct", new Size2(300, 300), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var op = new Signal<float>(0.6f);
            var col = new Signal<ColorF>(ColorF.FromRgba(0xE8, 0x3C, 0x3C, 0xFF));
            var wf = new FloatSignal(40f);
            var txt = new Signal<string>("sd1");
            NodeHandle box = default, wBox = default, wTxt = default;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Width = 280, Height = 280,
                    Children = new Element[]
                    {
                        new BoxEl { Width = 40, Height = 10, Opacity = op, Fill = col, OnRealized = h => box = h },
                        new BoxEl { Height = 10, Width = wf, Fill = ColorF.FromRgba(0x20, 0x20, 0x20, 0xFF), OnRealized = h => wBox = h },
                        new BoxEl { OnRealized = h => wTxt = h, Children = [ new TextEl("") { Text = txt } ] },
                    },
                },
            });
            host.RunFrame();
            var nTxt = host.Scene.FirstChild(wTxt);
            bool initial = Near(host.Scene.Paint(box).Opacity, 0.6f, 0.001f)
                && host.Scene.Paint(box).Fill == col.Peek()
                && Near(host.Scene.Layout(wBox).Width, 40f, 0.001f)
                && host.Scene.Paint(nTxt).Text == strings.Intern("sd1");
            op.Value = 0.25f; col.Value = ColorF.FromRgba(0x18, 0xA0, 0x57, 0xFF);
            var st = host.RunFrame();
            bool paintOnly = !st.Rendered;                       // opacity/fill bind fires are compositor-only
            wf.Value = 90f; txt.Value = "sd2";
            host.RunFrame();
            Check("prop.signal-direct Signal/FloatSignal assigned straight to Opacity/Fill/Width/Text drive their channels (no closure)",
                initial && Near(host.Scene.Paint(box).Opacity, 0.25f, 0.001f) && host.Scene.Paint(box).Fill == col.Peek()
                && Near(host.Scene.Layout(wBox).Width, 90f, 0.001f) && host.Scene.Paint(nTxt).Text == strings.Intern("sd2"),
                $"initial={initial} op={host.Scene.Paint(box).Opacity} w={host.Scene.Layout(wBox).Width}");
            Check("prop.signal-direct paint-channel signal writes stay compositor-only (Rendered=false)",
                paintOnly, $"rendered={st.Rendered}");
        }

        // Mount-only wiring contract (locked, deliberate): a NEW thunk supplied on a re-render is IGNORED — the
        // mount-captured bind is immortal until unmount. Change the signal's VALUE, not the bind.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-mount-only", new Size2(200, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var rr = new Signal<int>(0);
            NodeHandle box = default;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () =>
                {
                    int r = rr.Value;                            // each render captures a FRESH r in a FRESH thunk
                    // FGRP002: this probe DELIBERATELY captures a signal-value snapshot to prove the reconciler ignores
                    // replacement thunks (the exact anti-pattern the rule flags). Suppressed on purpose.
#pragma warning disable FGRP002
                    return new BoxEl { Width = 40, Height = 10, Opacity = Prop.Of(() => 0.1f + 0.2f * r), OnRealized = h => box = h };
#pragma warning restore FGRP002
                },
            });
            host.RunFrame();
            rr.Value = 1;                                        // re-render: new thunk (r=1) — must be IGNORED
            host.RunFrame();
            Check("bind.mount-only.stale a fresh thunk on re-render is ignored (mount-captured bind is immortal)",
                Near(host.Scene.Paint(box).Opacity, 0.1f, 0.001f), $"op={host.Scene.Paint(box).Opacity} (0.3 would mean re-wiring happened)");
        }
    }

    static void G4dMigrationChecks(StringTable strings)
    {
        // ── gate.props.migration-sweep ────────────────────────────────────────────────────────────────────────────
        {
            // (1) ToggleSwitch: flipping isOn re-pushes to the reused core → the track cross-fades to the accent ON
            //     fill on the SAME track node (a single realize = no remount).
            bool toggleLive;
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g4d-toggle", new Size2(320, 160), 1f)); window.Show();
                var on = new Signal<bool>(false);
                var tracks = new List<NodeHandle>();
                var parts = new TemplateParts();
                parts[ToggleSwitch.PartTrack] = b => b with { OnRealized = h => { if (!tracks.Contains(h)) tracks.Add(h); } };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                    new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(12),
                        Children = [ToggleSwitch.Create(on, parts: parts)] } });
                host.RunFrame();
                bool mount = tracks.Count == 1;
                var track0 = tracks.Count > 0 ? tracks[0] : NodeHandle.Null;
                on.Value = true;
                for (int i = 0; i < 24; i++) host.RunFrame();
                bool noRemount = tracks.Count == 1 && !track0.IsNull;
                bool toAccent = !track0.IsNull && ColorClose(host.Scene.Paint(track0).Fill, Tok.AccentDefault, 0.03f);
                toggleLive = mount && noRemount && toAccent;
            }

            // (2) ProgressRing: flipping isActive re-pushes to the reused core → the SAME ring node starts spinning
            //     (animation tracks appear) with no remount.
            bool ringLive;
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g4d-ring", new Size2(200, 200), 1f)); window.Show();
                var active = new Signal<bool>(false);
                NodeHandle arc = default; int realizes = 0;
                var parts = new TemplateParts();
                parts[ProgressRing.PartRing] = b => b with { OnRealized = h => { arc = h; realizes++; } };
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                    new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(16),
                        Children = [ProgressRing.Indeterminate(isActive: active.Value, parts: parts)] } });
                host.RunFrame();
                var ring0 = host.Scene.Parent(arc);
                bool idleMount = !arc.IsNull && realizes == 1 && !host.Animation.HasTracks(ring0);
                active.Value = true;
                host.RunFrame();
                var ring1 = host.Scene.Parent(arc);
                bool spinLive = realizes == 1 && ring1 == ring0 && host.Animation.HasTracks(ring1);
                ringLive = idleMount && spinLive;
            }

            // (3) ToolTip: Wrap re-pushes new (target,text) slots to the reused core → the wrapped target reconciles IN
            //     PLACE (same node) and its new width lands (a frozen-field ToolTip would keep the mount-time width).
            bool tipLive;
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g4d-tip", new Size2(320, 160), 1f)); window.Show();
                var w = new Signal<float>(100f);
                var targets = new List<NodeHandle>();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                    new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(12),
                        Children = [ToolTip.Wrap(
                            new BoxEl { Width = w.Value, Height = 20f, Fill = Tok.AccentDefault,
                                        OnRealized = h => { if (!targets.Contains(h)) targets.Add(h); } },
                            "tip")] } });
                host.RunFrame();
                var target0 = targets.Count > 0 ? targets[0] : NodeHandle.Null;
                bool mount = targets.Count == 1 && !target0.IsNull && Near(host.Scene.AbsoluteRect(target0).W, 100f, 0.5f);
                w.Value = 160f;
                host.RunFrame(); host.RunFrame();
                bool grewInPlace = targets.Count == 1 && Near(host.Scene.AbsoluteRect(target0).W, 160f, 0.5f);
                tipLive = mount && grewInPlace;
            }

            Check("gate.props.migration-sweep migrated controls deliver live prop updates to the SAME mounted core (ToggleSwitch/ProgressRing/ToolTip, no remount)",
                toggleLive && ringLive && tipLive, $"toggle={toggleLive} ring={ringLive} tooltip={tipLive}");
        }

        // ── gate.guards.control-kit-clean ─────────────────────────────────────────────────────────────────────────
        {
            bool prevBc = BindContract.Enabled, prevBcT = BindContract.ThrowOnViolation;
            bool prevBw = BackwardsWriteGuard.Enabled, prevBwT = BackwardsWriteGuard.ThrowOnViolation;
            BindContract.Enabled = BindContract.CompiledIn; BindContract.ThrowOnViolation = false;
            BackwardsWriteGuard.Enabled = BackwardsWriteGuard.CompiledIn; BackwardsWriteGuard.ThrowOnViolation = false;
            BindContract.Reset(); BackwardsWriteGuard.Reset();
            try
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g4d-guards", new Size2(480, 960), 1f)); window.Show();
                var flip = new Signal<bool>(false);
                var idx = new Signal<int>(0);
                var gen = new Signal<int>(0);
                var enabled = new Signal<bool>(true);
                var combo = new Signal<int>(0);
                var num = new Signal<double>(1);
                var sval = new FloatSignal(0.3f);
                string[] items3 = ["a", "b", "c"];
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                    new W0fStaticProbe
                    {
                        Build = () => new BoxEl
                        {
                            Direction = 1, Width = 440, Gap = 6, Padding = Edges4.All(12),
                            Children =
                            [
                                ToggleSwitch.Create(flip, header: "t"),
                                CheckBox.Create("c", flip, isEnabled: enabled.Value),
                                ProgressRing.Indeterminate(isActive: flip.Value),
                                ProgressBar.Indeterminate(240f, flip.Value ? ProgressBarState.Paused : ProgressBarState.Normal),
                                Slider.Create(sval, _ => { }, new Slider.SliderOptions()),
                                PipsPager.Create(5, idx),
                                Pivot.Create(items3, i => new TextEl("p" + i) { Size = 12f }, selectedIndex: idx),
                                FlipView.Create([new TextEl("0"), new TextEl("1"), new TextEl("2")], selectedIndex: idx),
                                RadioButtons.Create(items3, idx),
                                SelectorBar.Create(items3, idx),
                                BreadcrumbBar.Create(items3),
                                ComboBox.Create(items3, combo, isEnabled: enabled.Value),
                                NumberBox.Create(value: null, options: new NumberBox.NumberBoxOptions { Initial = num.Value, IsEnabled = enabled.Value }),
                                Expander.Create("e", new TextEl("body") { Size = 12f }),
                                SwipeControl.Create("row", System.Array.Empty<SwipeAction>()),
                                SettingsCard.Create(new SettingsCard.Options { Header = "s", Description = "d" }),
                                SettingsExpander.Create(new SettingsExpander.Options { Header = "se" }),
                                ToolTip.Wrap(new TextEl("hover") { Size = 12f }, "tip " + gen.Value),
                            ],
                        },
                    });
                host.RunFrame();
                // Re-push changed props over several re-renders to exercise the reuse/delivery seam (the flip surface).
                for (int r = 0; r < 4; r++)
                {
                    flip.Value = !flip.Value;
                    idx.Value = (idx.Value + 1) % 3;
                    gen.Value = gen.Value + 1;
                    enabled.Value = !enabled.Value;
                    host.RunFrame(); host.RunFrame();
                }
                // Programmatic NumberBox Text write via the value signal (exercises the EditableText SyncFromSignal path).
                num.Value = 7; host.RunFrame(); host.RunFrame();

                bool bcClean = BindContract.Violations == 0;
                bool bwClean = BackwardsWriteGuard.Violations == 0;
                Check("gate.guards.control-kit-clean migrated controls + prop re-push trip ZERO BindContract flips and ZERO backwards-writes",
                    bcClean && bwClean,
                    $"compiledIn={BindContract.CompiledIn} bindFlips={BindContract.Violations} [{BindContract.LastViolation}] backWrites={BackwardsWriteGuard.Violations} [{BackwardsWriteGuard.LastViolation}]");
            }
            finally
            {
                BindContract.Enabled = prevBc; BindContract.ThrowOnViolation = prevBcT;
                BackwardsWriteGuard.Enabled = prevBw; BackwardsWriteGuard.ThrowOnViolation = prevBwT;
            }
        }
    }
}
