using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// gate.bind.rewire-* — the bound→bound half of the bind contract (Reconciler.Rewire.cs; reconciler-hooks.md §0bis).
// A reused node re-rendered with a bound channel whose thunk/signal payload CHANGED evaluates the NEW source; an
// unchanged payload re-runs nothing; the swap itself allocates nothing. Replaces the retired `bind.mount-only.stale`
// probe, which locked the opposite (a fresh thunk ignored) — the root of Wavee's daylist timeline defect: a
// ProgressBar.Create indicator first rendered at a fallback width kept `value × fallback` after its parent re-rendered
// it at the real width, while its static track followed (done segments at 86 %, 64.8 vs 75.3 DIP).
//
// Every probe re-renders its owner through `rr` (read by Build) and feeds the NEW render-time value through a plain
// test-side variable captured by a fresh closure — so no signal the thunk reads ever changes: only the thunk identity
// does, which is exactly what mount-only wiring ignored.
static class BindRewireChecks
{
    public static void Run(StringTable strings)
    {
        WidthChecks(strings);
        PaintAndTextChecks(strings);
        SignalSwapChecks(strings);
        ProgressBarChecks(strings);
        UnchangedPayloadChecks(strings);
        AnchorVisibleChecks(strings);
        AllocChecks(strings);
    }

    static AppHost Host(HeadlessPlatformApp app, StringTable strings, string name, Component root)
    {
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(320, 240), 1f));
        window.Show();
        return new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
    }

    // ── 1. a bound Width thunk capturing a render-time value follows the re-render ─────────────────────────────────────
    static void WidthChecks(StringTable strings)
    {
        var rr = new Signal<int>(0);
        float width = 40f;                                   // the render-time value the fresh thunk captures
        NodeHandle box = default; int realizes = 0;
        Action<NodeHandle> onBox = h => { box = h; realizes++; };
        var probe = new W0fStaticProbe
        {
            Build = () =>
            {
                _ = rr.Value;                                // owner re-render trigger
                float w = width;                             // snapshot of a PLAIN value (no signal) → a fresh closure
                return new BoxEl
                {
                    Direction = 1, Width = 300, Height = 200,
                    Children = [new BoxEl { Height = 10, Width = Prop.Of(() => w), OnRealized = onBox }],
                };
            },
        };
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, "bind-rewire-width", probe);
        host.RunFrame();
        bool mounted = !box.IsNull && Near(host.Scene.Layout(box).Width, 40f, 0.01f) && Near(host.Scene.Bounds(box).W, 40f, 0.01f);

        width = 70f; rr.Value = 1;                           // re-render: NEW thunk (w = 70), nothing it reads changed
        host.RunFrame();
        float li = host.Scene.Layout(box).Width, laid = host.Scene.Bounds(box).W;
        Check("gate.bind.rewire-width a reused BoxEl re-rendered with a NEW bound Width thunk (a fresh closure over a render-time value, no signal change) lays out at the NEW value — same node, no remount",
            mounted && realizes == 1 && Near(li, 70f, 0.01f) && Near(laid, 70f, 0.01f),
            $"mounted={mounted} realizes={realizes} layoutInput={li} laidOut={laid} (40 = the mount-only defect)");
    }

    // ── 2. paint channels (Fill, Opacity) and a layout channel on another element type (TextEl.Text) ───────────────────
    static void PaintAndTextChecks(StringTable strings)
    {
        var rr = new Signal<int>(0);
        ColorF fill = ColorF.FromRgba(0xE8, 0x3C, 0x3C, 0xFF);
        float opacity = 0.8f;
        string label = "before";
        NodeHandle box = default, wrap = default; int realizes = 0;
        Action<NodeHandle> onBox = h => { box = h; realizes++; };
        Action<NodeHandle> onWrap = h => wrap = h;
        var probe = new W0fStaticProbe
        {
            Build = () =>
            {
                _ = rr.Value;
                ColorF f = fill; float o = opacity; string s = label;
                return new BoxEl
                {
                    Direction = 1, Width = 300, Height = 200,
                    Children =
                    [
                        new BoxEl { Width = 40, Height = 10, Fill = Prop.Of(() => f), Opacity = Prop.Of(() => o), OnRealized = onBox },
                        new BoxEl { OnRealized = onWrap, Children = [new TextEl(Prop.Of(() => s))] },
                    ],
                };
            },
        };
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, "bind-rewire-paint", probe);
        host.RunFrame();
        var text = host.Scene.FirstChild(wrap);
        bool mounted = host.Scene.Paint(box).Fill == fill && Near(host.Scene.Paint(box).Opacity, 0.8f, 0.001f)
                       && host.Scene.Paint(text).Text == strings.Intern("before");

        fill = ColorF.FromRgba(0x18, 0xA0, 0x57, 0xFF); opacity = 0.35f; label = "after";
        rr.Value = 1;
        host.RunFrame();
        var paint = host.Scene.Paint(box);
        Check("gate.bind.rewire-paint a reused node re-rendered with NEW bound Fill/Opacity thunks (and a TextEl with a NEW bound Text thunk) paints the NEW values — same nodes",
            mounted && realizes == 1 && host.Scene.FirstChild(wrap) == text
            && paint.Fill == fill && Near(paint.Opacity, 0.35f, 0.001f) && host.Scene.Paint(text).Text == strings.Intern("after"),
            $"mounted={mounted} realizes={realizes} fill={paint.Fill} opacity={paint.Opacity} textId={host.Scene.Paint(text).Text}");
    }

    // ── 3. bound→bound signal swap: the node follows the NEW signal; the OLD one is unsubscribed ───────────────────────
    static void SignalSwapChecks(StringTable strings)
    {
        var rr = new Signal<int>(0);
        var a = new Signal<float>(30f);
        var b = new Signal<float>(55f);
        Signal<float> which = a;
        NodeHandle box = default;
        Action<NodeHandle> onBox = h => box = h;
        var probe = new W0fStaticProbe
        {
            Build = () =>
            {
                _ = rr.Value;
                Signal<float> cur = which;
                return new BoxEl
                {
                    Direction = 1, Width = 300, Height = 200,
                    Children = [new BoxEl { Height = 10, Width = cur, OnRealized = onBox }],   // signal-direct bind
                };
            },
        };
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, "bind-rewire-signal", probe);
        host.RunFrame();
        bool mounted = Near(host.Scene.Layout(box).Width, 30f, 0.01f);

        which = b; rr.Value = 1;                             // re-render binds a DIFFERENT signal instance
        host.RunFrame();
        bool swapped = Near(host.Scene.Layout(box).Width, 55f, 0.01f) && Near(host.Scene.Bounds(box).W, 55f, 0.01f);

        a.Value = 99f;                                       // the OLD signal must no longer drive the node
        host.RunFrame();
        bool oldDead = Near(host.Scene.Layout(box).Width, 55f, 0.01f) && !a.HasSubscribers;

        b.Value = 77f;                                       // the NEW signal drives it live (no re-render needed)
        host.RunFrame();
        bool newLive = Near(host.Scene.Layout(box).Width, 77f, 0.01f) && Near(host.Scene.Bounds(box).W, 77f, 0.01f);
        Check("gate.bind.rewire-signal-swap a re-render that binds a NEW signal instance re-wires the node to it: it follows the new signal, and writing the old one no longer affects the node (unsubscribed)",
            mounted && swapped && oldDead && newLive,
            $"mounted={mounted} swapped={swapped} oldDead={oldDead} (oldSubs={a.SubscriberCount}) newLive={newLive} w={host.Scene.Layout(box).Width}");
    }

    // ── 4. the Wavee regression: ProgressBar.Create at a fallback width, re-rendered at the real width ─────────────────
    static void ProgressBarChecks(StringTable strings)
    {
        var rr = new Signal<int>(0);
        var value = new FloatSignal(1f);
        float barWidth = 64.8f;                              // Responsive.Of's fallback extent
        NodeHandle track = default, fill = default; int fillRealizes = 0;
        Action<NodeHandle> onTrack = h => track = h;
        Action<NodeHandle> onFill = h => { fill = h; fillRealizes++; };
        var parts = new TemplateParts();
        parts[ProgressBar.PartTrack] = t => t with { OnRealized = onTrack };
        parts[ProgressBar.PartFill] = f => f with { OnRealized = onFill };
        var probe = new W0fStaticProbe
        {
            Build = () =>
            {
                _ = rr.Value;
                return new BoxEl
                {
                    Direction = 1, Width = 300, Height = 200,
                    Children = [ProgressBar.Create(value, barWidth, parts: parts)],
                };
            },
        };
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, "bind-rewire-progressbar", probe);
        host.RunFrame();
        bool atFallback = Near(host.Scene.Layout(fill).Width, 64.8f, 0.01f) && Near(host.Scene.Layout(track).Width, 64.8f, 0.01f);

        barWidth = 75.3f; rr.Value = 1;                      // the real width lands → the parent re-renders the bar
        host.RunFrame();
        float fillW = host.Scene.Bounds(fill).W, trackW = host.Scene.Bounds(track).W;
        Check("gate.bind.rewire-progressbar ProgressBar.Create(value=1, 64.8) re-rendered at width 75.3 sizes its bound indicator to 75.3 — equal to its static track (Wavee daylist timeline: done segments stuck at 86 %)",
            atFallback && fillRealizes == 1 && Near(host.Scene.Layout(fill).Width, 75.3f, 0.01f)
            && Near(fillW, 75.3f, 0.01f) && Near(trackW, 75.3f, 0.01f) && Near(fillW, trackW, 0.01f),
            $"atFallback={atFallback} fillRealizes={fillRealizes} indicator={fillW} track={trackW} (64.8 = the mount-only defect)");
    }

    // ── 5. an UNCHANGED payload (same thunk instance / same signal) re-runs nothing; a fresh one re-runs exactly once ──
    static void UnchangedPayloadChecks(StringTable strings)
    {
        var rr = new Signal<int>(0);
        var opSig = new Signal<float>(0.6f);
        Func<float> stableW = () => 42f;                     // ONE delegate instance, reused by every render
        bool fresh = false;
        NodeHandle box = default;
        Action<NodeHandle> onBox = h => box = h;
        var probe = new W0fStaticProbe
        {
            Build = () =>
            {
                int r = rr.Value;
                float v = 42f;                               // captured below only on the `fresh` arm → a new closure
                return new BoxEl
                {
                    Direction = 1, Width = 300, Height = 200,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = 10, BorderWidth = 1f + r,   // a static prop change per render → RecordChanged runs
                            Width = fresh ? Prop.Of(() => v) : (Prop<float>)stableW,
                            Opacity = opSig,                     // the SAME signal every render
                            OnRealized = onBox,
                        },
                    ],
                };
            },
        };
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, "bind-rewire-quiet", probe);
        host.RunFrame();

        rr.Value = 1;                                        // re-render, identical bind payloads
        var quiet = host.RunFrame();
        bool reconciled = Near(host.Scene.Paint(box).BorderWidth, 2f, 0.001f);

        fresh = true; rr.Value = 2;                          // control: the Width thunk is now a fresh closure
        var rewired = host.RunFrame();
        Check("gate.bind.rewire-unchanged-quiet a re-render that reconciles the node (a static prop changed) but hands the SAME thunk instance and the SAME signal fires ZERO bindings; a fresh thunk fires exactly its own binding once, and its equal value writes nothing",
            reconciled && quiet.BindingFires == 0 && rewired.BindingFires == 1 && rewired.BindingWrites == 0
            && Near(host.Scene.Layout(box).Width, 42f, 0.01f) && Near(host.Scene.Paint(box).Opacity, 0.6f, 0.001f),
            $"reconciled={reconciled} quietFires={quiet.BindingFires} rewiredFires={rewired.BindingFires} rewiredWrites={rewired.BindingWrites}");
    }

    // ── 6. a component ANCHOR's bound Visible (WriteAnchorColumns' reuse path) re-wires too ────────────────────────────
    sealed class AnchorLeaf : Component
    {
        public NodeHandle Anchor;
        public override Element Render()
        {
            Anchor = Context.AnchorNode;
            return new BoxEl { Width = 40f, Height = 20f };
        }
    }

    static void AnchorVisibleChecks(StringTable strings)
    {
        var rr = new Signal<int>(0);
        bool shown = true;
        AnchorLeaf? leaf = null; int factories = 0;
        var probe = new W0fStaticProbe
        {
            Build = () =>
            {
                _ = rr.Value;
                bool s = shown;
                return new BoxEl
                {
                    Direction = 0, Width = 300, Height = 40, Gap = 10,
                    Children = [Embed.Comp(() => { factories++; return leaf = new AnchorLeaf(); }) with { Visible = Prop.Of(() => s) }],
                };
            },
        };
        using var app = new HeadlessPlatformApp();
        using var host = Host(app, strings, "bind-rewire-anchor", probe);
        host.RunFrame();
        var anchor = leaf?.Anchor ?? NodeHandle.Null;
        bool visibleAtMount = !anchor.IsNull && !host.Scene.IsCollapsed(anchor);

        shown = false; rr.Value = 1;                         // re-render: the embed's Visible is a NEW thunk returning false
        host.RunFrame();
        bool collapsed = host.Scene.IsCollapsed(anchor);

        shown = true; rr.Value = 2;
        host.RunFrame();
        bool restored = !host.Scene.IsCollapsed(anchor);
        Check("gate.bind.rewire-anchor-visible a reused Embed.Comp(...) anchor re-rendered with a NEW bound Visible thunk collapses/restores on the new value (WriteAnchorColumns re-wires) — same component instance",
            visibleAtMount && collapsed && restored && factories == 1,
            $"visibleAtMount={visibleAtMount} collapsed={collapsed} restored={restored} factories={factories}");
    }

    // ── 7. the swap is allocation-free ─────────────────────────────────────────────────────────────────────────────────
    static void AllocChecks(StringTable strings)
    {
        // (a) The unit itself: re-pointing an existing BindEffect between two elements' thunks and re-running it (which
        //     unlinks + re-tracks its signal source), and a same-payload Rewire (no re-run), allocate 0 bytes once warm.
        {
            var rt = new ReactiveRuntime();
            var sig = new Signal<float>(1f);
            var ea = new BoxEl { Width = Prop.Of(() => sig.Value + 1f) };
            var eb = new BoxEl { Width = Prop.Of(() => sig.Value + 2f) };
            var ea2 = ea with { Height = 5f };               // a `with` clone keeps the SAME Width payload
            float sink = 0f; int fires = 0;
            var fx = new BindEffect<float>(rt, ea, static e => e is BoxEl x ? x.Width : default);
            fx.Start(() => { fires++; sink = fx.Read(); });
            for (int i = 0; i < 8; i++) { fx.Rewire(eb); fx.Rewire(ea); }   // warm (JIT, list capacities)

            int firesBefore = fires;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++) { fx.Rewire(eb); fx.Rewire(ea); }
            long swapBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            bool swapFired = fires - firesBefore == 512 && Near(sink, 2f, 0.001f);

            firesBefore = fires;
            before = GC.GetAllocatedBytesForCurrentThread();
            bool anyRewired = false;
            for (int i = 0; i < 256; i++) anyRewired |= fx.Rewire(ea2) | fx.Rewire(ea);
            long sameBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            bool sameQuiet = !anyRewired && fires == firesBefore;

            fx.Rewire(eb);                                   // live on eb's thunk → a signal write re-fires through it
            sig.Value = 10f;
            rt.Flush();
            bool tracked = Near(sink, 12f, 0.001f) && sig.SubscriberCount == 1;
            fx.Dispose();
            Check("gate.bind.rewire-zero-alloc re-pointing a BindEffect at another element's thunk and re-running it (unlink + re-track) allocates 0 bytes; a same-payload rewire re-runs nothing and allocates 0 bytes; the re-wired effect tracks the new thunk's signal",
                swapBytes == 0 && swapFired && sameBytes == 0 && sameQuiet && tracked,
                $"swapBytes={swapBytes} swapFired={swapFired} sameBytes={sameBytes} sameQuiet={sameQuiet} tracked={tracked} sink={sink}");
        }

        // (b) Through the reconciler: alternating two prebuilt trees that differ ONLY in the bound Width payload costs no
        //     more than alternating two that differ only in a static prop (both run the same WriteColumns; the former
        //     additionally re-wires + re-runs the binding) — the re-wire adds nothing to the update path.
        {
            var scene = new SceneStore();
            var r = new TreeReconciler(scene, strings);
            var sig = new Signal<float>(10f);
            Func<float> ta = () => sig.Value + 1f, tb = () => sig.Value + 2f;
            var a = new BoxEl { Width = ta, Height = 10, BorderWidth = 1f };
            var b = new BoxEl { Width = tb, Height = 10, BorderWidth = 1f };    // differs from `a` ONLY in the payload
            var c = new BoxEl { Width = ta, Height = 10, BorderWidth = 1f };
            var d = new BoxEl { Width = ta, Height = 10, BorderWidth = 2f };    // differs from `c` ONLY in a static prop
            r.ReconcileRoot(a, null);
            Element prev = a;
            for (int i = 0; i < 8; i++) { r.ReconcileRoot(b, prev); r.ReconcileRoot(a, b); prev = a; }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++) { r.ReconcileRoot(b, a); r.ReconcileRoot(a, b); }
            long rewireBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            r.ReconcileRoot(b, a);
            bool onB = Near(scene.Layout(scene.Root).Width, 12f, 0.001f);

            r.ReconcileRoot(c, b);
            bool onC = Near(scene.Layout(scene.Root).Width, 11f, 0.001f);
            for (int i = 0; i < 8; i++) { r.ReconcileRoot(d, c); r.ReconcileRoot(c, d); }
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++) { r.ReconcileRoot(d, c); r.ReconcileRoot(c, d); }
            long staticBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("gate.bind.rewire-reconcile-alloc 256 reconciles that re-wire a bound Width between two prebuilt thunks allocate no more than 256 reconciles that change only a static prop (the re-wire adds 0 bytes to the update path), and land on the right source",
                rewireBytes <= staticBytes && onB && onC,
                $"rewireBytes={rewireBytes} staticBytes={staticBytes} onB={onB} onC={onC}");
        }
    }
}
