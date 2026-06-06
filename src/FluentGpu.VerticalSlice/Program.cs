using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;

// ── The component (authored exactly as in the spec) ───────────────────────────────
sealed class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return VStack(12,
            Heading($"Count: {count}"),
            HStack(8,
                Button.Accent("-", () => setCount(count - 1)),
                Button.Accent("+", () => setCount(count + 1))));
    }
}

// Nested composition: a parent embedding a stateful child component (its own hooks).
sealed class NestChild : Component
{
    public override Element Render()
    {
        var (n, setN) = UseState(0);
        return Button.Accent($"child {n}", () => setN(n + 1));
    }
}

sealed class NestParent : Component
{
    public override Element Render()
        => VStack(8, Heading("parent"), Embed.Comp(() => new NestChild()));
}

// Context: a provider feeds a value to a nested consumer component across the component boundary.
sealed class CtxConsumer : Component
{
    public override Element Render() => Ui.Text($"ctx {UseContext(Slice.NumCtx)}");
}

sealed class CtxParent : Component
{
    public override Element Render()
    {
        var (v, setV) = UseState(7);
        return VStack(4,
            Button.Accent("inc", () => setV(v + 1)),
            Ctx.Provide(Slice.NumCtx, v, Embed.Comp(() => new CtxConsumer())));
    }
}

sealed class HoverProbe : Component
{
    public override Element Render() => Button.Accent("hi", () => { });
}

sealed class AnimProbe : Component
{
    public float Target;
    public float Value;
    public override Element Render() { Value = UseAnimatedValue(Target, 100f); return Ui.Text("x"); }
}

// A component that animates ITS OWN node declaratively — UseSpring seeds a track on the host node (no per-frame re-render).
sealed class SpringProbe : Component
{
    public override Element Render()
    {
        UseSpring(AnimChannel.ScaleX, 1.2f, SpringParams.FromResponse(0.2f, 1f), Array.Empty<object>());
        return new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(0, 128, 255) };
    }
}

// Probe component for the expanded hooks.
sealed class HookProbe : Component
{
    public int Dep = 1, MemoRuns, State, Memo;
    public Action<int>? Dispatch;
    public Ref<int>? RefBox;

    public override Element Render()
    {
        var (s, d) = UseReducer<int, int>((st, a) => st + a, 0);
        var m = UseMemo(() => { MemoRuns++; return Dep * 10; }, Dep);
        var r = UseRef(7);
        State = s; Dispatch = d; Memo = m; RefBox = r;
        return Ui.Text("probe");
    }
}

// ── The harness: run the slice end-to-end on the headless backends + assert ───────
static class Slice
{
    static int s_failures;
    public static readonly Context<int> NumCtx = new(0);

    static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
        if (!ok) s_failures++;
    }

    static NodeHandle Child(SceneStore s, NodeHandle parent, int index)
    {
        var c = s.FirstChild(parent);
        for (int i = 0; i < index && !c.IsNull; i++) c = s.NextSibling(c);
        return c;
    }

    static bool HasGlyph(HeadlessGpuDevice dev, StringTable strings, string text)
    {
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == text) return true;
        return false;
    }

    static bool Near(float a, float b) => MathF.Abs(a - b) < 0.5f;
    static bool Near(float a, float b, float tol) => MathF.Abs(a - b) < tol;

    static SceneStore LayoutTree(StringTable strings, Element tree)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(tree, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        return scene;
    }

    // Golden flexbox checks (deterministic, no text): justify-content, flex-grow, align-items.
    static void FlexChecks(StringTable strings)
    {
        // justify space-between: row 300 wide, two 40-wide boxes → x = 0 and 260
        var sb = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 300, Height = 50, Justify = FlexJustify.SpaceBetween,
            Children = [new BoxEl { Width = 40, Height = 20 }, new BoxEl { Width = 40, Height = 20 }],
        });
        var a = sb.AbsoluteRect(Child(sb, sb.Root, 0));
        var b = sb.AbsoluteRect(Child(sb, sb.Root, 1));
        Check("10. justify space-between", Near(a.X, 0) && Near(b.X, 260) && Near(b.W, 40), $"x0={a.X:0.#} x1={b.X:0.#}");

        // flex-grow: row 300 wide, two grow:1 children → each 150 wide at x = 0 and 150
        var gr = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 300, Height = 40,
            Children = [new BoxEl { Grow = 1, Height = 20 }, new BoxEl { Grow = 1, Height = 20 }],
        });
        var g0 = gr.AbsoluteRect(Child(gr, gr.Root, 0));
        var g1 = gr.AbsoluteRect(Child(gr, gr.Root, 1));
        Check("11. flex-grow splits free space", Near(g0.W, 150) && Near(g1.W, 150) && Near(g1.X, 150), $"w0={g0.W:0.#} x1={g1.X:0.#}");

        // align-items center on the cross axis: row 100 tall, child 20 tall → y = 40
        var al = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 200, Height = 100, AlignItems = FlexAlign.Center,
            Children = [new BoxEl { Width = 40, Height = 20 }],
        });
        var ac = al.AbsoluteRect(Child(al, al.Root, 0));
        Check("12. align-items center", Near(ac.Y, 40), $"y={ac.Y:0.#}");

        // padding + gap: column, padding 10, gap 8, two 20-tall children → y = 10 and 38
        var pg = LayoutTree(strings, new BoxEl
        {
            Direction = 1, Width = 100, Height = 200, Padding = Edges4.All(10), Gap = 8,
            Children = [new BoxEl { Width = 40, Height = 20 }, new BoxEl { Width = 40, Height = 20 }],
        });
        var p0 = pg.AbsoluteRect(Child(pg, pg.Root, 0));
        var p1 = pg.AbsoluteRect(Child(pg, pg.Root, 1));
        Check("13. padding + gap stacking", Near(p0.Y, 10) && Near(p1.Y, 38) && Near(p0.X, 10), $"y0={p0.Y:0.#} y1={p1.Y:0.#}");
    }

    // UseReducer / UseMemo / UseRef exercised through a real Component across renders.
    static void HookChecks()
    {
        var p = new HookProbe();

        p.RenderWithHooks();   // frame 1 (mount)
        var r1 = p.RefBox;
        bool ok1 = p.State == 0 && p.Memo == 10 && p.MemoRuns == 1 && r1!.Value == 7;

        p.Dispatch!(5); p.Dispatch!(3);   // fold: 0+5=5, +3=8
        p.Context.FlushPending();
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

    // Keyed reconcile: reorder preserves node identity (state), removal frees only the dropped key.
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

    // Focus + keyboard routing: Tab cycles focus, Enter activates a clickable, keys bubble (Handled stops).
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

    // Animation timelines: eased opacity completes (PaintDirty, never LayoutDirty); translate marks TransformDirty.
    static void AnimChecks()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        var engine = new AnimEngine(scene);

        scene.Paint(node).Opacity = 0f;
        scene.Flags(node) &= ~(NodeFlags.PaintDirty | NodeFlags.LayoutDirty | NodeFlags.TransformDirty);
        engine.Animate(node, AnimChannel.Opacity, 0f, 1f, 100f, Easing.Linear);
        engine.Tick(50f);
        float op = scene.Paint(node).Opacity;
        var fl = scene.Flags(node);
        bool midOk = MathF.Abs(op - 0.5f) < 0.02f && (fl & NodeFlags.PaintDirty) != 0 && (fl & NodeFlags.LayoutDirty) == 0;
        engine.Tick(60f);   // 110ms > 100ms → complete
        bool doneOk = MathF.Abs(scene.Paint(node).Opacity - 1f) < 0.001f && !engine.HasActive;
        Check("22. opacity timeline eases & completes (no relayout)", midOk && doneOk, $"@50ms={op:0.00}");

        scene.Flags(node) &= ~(NodeFlags.TransformDirty | NodeFlags.LayoutDirty);
        engine.Animate(node, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        engine.Tick(25f);
        float dx = scene.Paint(node).LocalTransform.Dx;
        var fl2 = scene.Flags(node);
        bool transOk = MathF.Abs(dx - 25f) < 0.5f && (fl2 & NodeFlags.TransformDirty) != 0 && (fl2 & NodeFlags.LayoutDirty) == 0;
        Check("23. translate timeline marks TransformDirty only", transOk, $"@25ms dx={dx:0.0}");
    }

    // Nested stateful component: renders, owns its own UseState, and re-renders on its own setState.
    static void NestedChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nest", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new NestParent();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        bool mounted = HasGlyph(device, strings, "child 0");

        // parent VStack → [Heading, componentHost]; host → [Button]
        var compHost = Child(host.Scene, host.Scene.Root, 1);
        var btn = Child(host.Scene, compHost, 0);
        var r = host.Scene.AbsoluteRect(btn);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        var f2 = host.RunFrame();

        bool childUpdated = HasGlyph(device, strings, "child 1");
        Check("24. nested component renders & owns state", mounted && f2.ClicksHandled == 1 && childUpdated, "child 0 → click → child 1");
    }

    // UseContext: provider value reaches a nested consumer, and a change propagates on re-render.
    static void ContextChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ctx", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new CtxParent();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        bool c0 = HasGlyph(device, strings, "ctx 7");

        var incBtn = Child(host.Scene, host.Scene.Root, 0);   // VStack child 0 = "inc" button
        var rr = host.Scene.AbsoluteRect(incBtn);
        var center = new Point2(rr.X + rr.W / 2f, rr.Y + rr.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        bool c1 = HasGlyph(device, strings, "ctx 8");

        Check("25. UseContext provides + propagates across components", c0 && c1, "ctx 7 → ctx 8");
    }

    // Hover/pressed visual states: the dispatcher tracks them as node flags following the pointer.
    static void HoverChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hover", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new HoverProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var btn = host.Scene.Root;
        var r = host.Scene.AbsoluteRect(btn);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        var outside = new Point2(r.Right + 50f, r.Bottom + 50f);

        window.QueueInput(new InputEvent(InputKind.PointerMove, center, 0, 0));
        host.RunFrame();
        bool hov = (host.Scene.Flags(btn) & NodeFlags.Hovered) != 0;

        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        host.RunFrame();
        bool prs = (host.Scene.Flags(btn) & NodeFlags.Pressed) != 0;

        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        bool released = (host.Scene.Flags(btn) & NodeFlags.Pressed) == 0;

        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
        host.RunFrame();
        bool unhov = (host.Scene.Flags(btn) & NodeFlags.Hovered) == 0;

        Check("26. hover/pressed states track the pointer", hov && prs && released && unhov, "enter→hover, down→pressed, up→release, leave→unhover");
    }

    // Controls are barebone + a default Fluent style on top, overrideable per-instance (ButtonStyle) or via modifiers.
    static void StyleChecks()
    {
        var s = new ButtonStyle
        {
            Background = ColorF.FromRgba(10, 20, 30),
            Foreground = ColorF.FromRgba(40, 50, 60),
            HoverBackground = ColorF.FromRgba(70, 80, 90),
            CornerRadius = 8f,
        };
        var btn = Button.Accent("x", () => { }, s);
        bool styled = btn.Fill == s.Background
            && btn.HoverFill == s.HoverBackground
            && Near(btn.Corners.TopLeft, 8f)
            && btn.Children[0] is TextEl t && t.Color == s.Foreground;

        var modded = Button.Accent("y", () => { }).Background(ColorF.FromRgba(1, 2, 3)).Rounded(12f);
        bool overridden = modded.Fill == ColorF.FromRgba(1, 2, 3) && Near(modded.Corners.TopLeft, 12f);

        Check("27. controls are user-styleable (ButtonStyle + modifiers)", styled && overridden, "custom style + .Background().Rounded()");
    }

    // UseAnimatedValue eases toward a changed target across renders, then settles (React/framer-style transition).
    static void AnimValueChecks()
    {
        var p = new AnimProbe { Target = 0f };
        p.RenderWithHooks();                 // mount → value = 0
        p.Target = 1f;
        p.RenderWithHooks();                 // target changed → first eased step
        float v1 = p.Value;
        for (int i = 0; i < 20; i++) p.RenderWithHooks();   // advance past the 100ms duration
        float v2 = p.Value;
        Check("28. UseAnimatedValue eases then settles", v1 > 0f && v1 < 1f && Near(v2, 1f), $"step={v1:0.00} settled={v2:0.0}");
    }

    // flex-wrap: a 100-wide row of three 40-wide boxes flows to two lines ([0,1] then [2]).
    static void WrapChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new BoxEl
        {
            Direction = 0, Width = 100, Height = 100, Wrap = true,
            Children =
            [
                new BoxEl { Width = 40, Height = 20 },
                new BoxEl { Width = 40, Height = 20 },
                new BoxEl { Width = 40, Height = 20 },
            ],
        });
        var c0 = scene.AbsoluteRect(Child(scene, scene.Root, 0));
        var c1 = scene.AbsoluteRect(Child(scene, scene.Root, 1));
        var c2 = scene.AbsoluteRect(Child(scene, scene.Root, 2));
        Check("29. flex-wrap flows children to lines", Near(c0.X, 0) && Near(c1.X, 40) && Near(c2.X, 0) && Near(c2.Y, 20), $"c2=({c2.X:0.#},{c2.Y:0.#})");
    }

    // Compositor: the renderer applies a per-node world transform + cumulative opacity (CSS compositor model).
    static void CompositorChecks(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200, Height = 100, OffsetX = 20, OffsetY = 30, Opacity = 0.5f,
            Fill = ColorF.FromRgba(255, 0, 0),
            Children = [new BoxEl { Width = 40, Height = 20, Fill = ColorF.FromRgba(0, 255, 0), Opacity = 0.5f }],
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);

        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        var parent = dev.LastRects[0];   // root box
        var child = dev.LastRects[1];    // nested box
        bool parentOk = Near(parent.Transform.Dx, 20) && Near(parent.Transform.Dy, 30) && Near(parent.Opacity, 0.5f);
        bool childOk = Near(child.Opacity, 0.25f) && child.Transform.Dx >= 20f;   // opacity composes 0.5*0.5; inherits parent offset
        Check("30. compositor: transform + cumulative opacity", parentOk && childOk, $"pOffset=({parent.Transform.Dx:0.#},{parent.Transform.Dy:0.#}) childOpacity={child.Opacity:0.00}");
    }

    // Phase 2 — the generic runtime: keyframes, composite (add), springs, and scroll-driven timelines.
    static void AnimEngineChecks(StringTable strings)
    {
        // eased multi-keyframe tween → composed into LocalTransform
        var s1 = Single(strings);
        var a1 = new AnimEngine(s1);
        a1.Animate(s1.Root, AnimChannel.TranslateX, 0f, 100f, 100f, Easing.Linear);
        a1.Tick(50f);
        float mid = s1.Paint(s1.Root).LocalTransform.Dx;
        a1.Tick(100f);
        float end = s1.Paint(s1.Root).LocalTransform.Dx;
        Check("31. eased keyframe tween + hold", Near(mid, 50f, 1f) && Near(end, 100f, 0.5f), $"mid={mid:0.#} end={end:0.#}");

        // composite Add: two tracks on one channel combine (animation-composition: add)
        var s2 = Single(strings);
        var a2 = new AnimEngine(s2);
        a2.Animate(s2.Root, AnimChannel.TranslateX, 0f, 30f, 100f, Easing.Linear, CompositeOp.Replace);
        a2.Animate(s2.Root, AnimChannel.TranslateX, 0f, 20f, 100f, Easing.Linear, CompositeOp.Add);
        a2.Tick(100f);
        float add = s2.Paint(s2.Root).LocalTransform.Dx;
        Check("32. composite add combines tracks", Near(add, 50f, 0.5f), $"dx={add:0.#}");

        // spring settles to its target (semi-implicit ODE)
        var s3 = Single(strings);
        var a3 = new AnimEngine(s3);
        a3.Spring(s3.Root, AnimChannel.ScaleX, 1.3f, SpringParams.FromResponse(0.2f, 1f), initial: 1.0f);
        for (int i = 0; i < 150; i++) a3.Tick(16f);
        float sx = s3.Paint(s3.Root).LocalTransform.M11;
        Check("33. spring settles to target", Near(sx, 1.3f, 0.02f), $"scaleX={sx:0.###}");

        // scroll-driven timeline: a value source maps to progress (animation-timeline: scroll())
        var s4 = Single(strings);
        var a4 = new AnimEngine(s4);
        float scroll = 0f;
        int clk = a4.Clocks.Register(() => scroll);
        a4.Drive(s4.Root, AnimChannel.Opacity, [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear)], clk, 0f, 100f);
        scroll = 25f; a4.Tick(16f);
        float op25 = s4.Paint(s4.Root).Opacity;
        scroll = 100f; a4.Tick(16f);
        float op100 = s4.Paint(s4.Root).Opacity;
        Check("34. scroll-driven timeline", Near(op25, 0.25f, 0.02f) && Near(op100, 1f, 0.01f), $"op@25={op25:0.00} op@100={op100:0.00}");
    }

    // Phase 3 — declarative hooks: a component animates its own node via UseSpring (composited, no re-render per frame).
    static void AnimHookChecks(StringTable strings)
    {
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, strings) { Anim = anim };
        recon.ReconcileRoot(Embed.Comp(() => new SpringProbe()), null);

        foreach (var c in recon.LiveComponents)   // phase 6.5: drain layout effects → seeds the spring on the host node
        {
            foreach (var e in c.Context.PendingLayoutEffects) e();
            c.Context.PendingLayoutEffects.Clear();
        }
        for (int i = 0; i < 150; i++) anim.Tick(16f);

        var host = scene.FirstChild(scene.Root);   // the SpringProbe's box node
        float sx = scene.Paint(host).LocalTransform.M11;
        Check("35. UseSpring hook seeds + drives the node", !host.IsNull && Near(sx, 1.2f, 0.03f), $"scaleX={sx:0.###}");
    }

    static SceneStore Single(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl { Width = 20, Height = 20, Fill = ColorF.FromRgba(200, 0, 0) }, null);
        return scene;
    }

    static int Main()
    {
        Console.WriteLine("FluentGpu — minimum vertical slice (headless RHI/PAL/Text)\n");

        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("FluentGpu slice", new Size2(480, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new Counter();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        // Frame 1 — mount: window→clear→two button rects (SDF) + three text runs, flex-laid-out.
        var f1 = host.RunFrame();
        Check("1. window + GPU clear + present", device.FrameCount == 1, $"backend={device.BackendName}, clear=#{device.LastClear.R:0.0},{device.LastClear.G:0.0},{device.LastClear.B:0.0}");
        Check("2. rounded-rect primitives (2 buttons × border ring + fill)", device.LastRects.Count == 4, $"rects={device.LastRects.Count}");
        Check("3. text runs (heading + 2 labels)", device.LastGlyphs.Count == 3, $"glyphs={device.LastGlyphs.Count}");
        Check("4. flex layout produced bounds", host.Scene.AbsoluteRect(host.Scene.Root).W > 0, $"rootW={host.Scene.AbsoluteRect(host.Scene.Root).W:0.#}");
        Check("5. reconciler + UseState (initial render)", f1.Rendered && HasGlyph(device, strings, "Count: 0"));

        // Locate the "+" button (VStack → [Heading, HStack] → HStack.[ '-', '+' ]) and click its center.
        var hstack = Child(host.Scene, host.Scene.Root, 1);
        var plus = Child(host.Scene, hstack, 1);
        var r = host.Scene.AbsoluteRect(plus);
        var center = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));

        // Frame 2 — the click→setState→repaint round-trip.
        var f2 = host.RunFrame();
        Check("6. clickable Button → OnClick fired", f2.ClicksHandled == 1, $"hit @ ({center.X:0.#},{center.Y:0.#})");
        Check("7. setState re-rendered the label", f2.Rendered && HasGlyph(device, strings, "Count: 1"), "Count: 0 → Count: 1");

        // Warm, then assert the steady paint half (phases 6–13) is zero managed allocation.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("8. steady frame does no work (memoized)", !steady.Rendered);
        Check("9. ZERO managed alloc on the paint half (phases 6–11)", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");

        FlexChecks(strings);
        HookChecks();
        KeyedChecks(strings);
        KeyboardChecks(strings);
        AnimChecks();
        NestedChecks(strings);
        ContextChecks(strings);
        HoverChecks(strings);
        StyleChecks();
        AnimValueChecks();
        WrapChecks(strings);
        CompositorChecks(strings);
        AnimEngineChecks(strings);
        AnimHookChecks(strings);

        Console.WriteLine();
        if (s_failures == 0) { Console.WriteLine("ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end."); return 0; }
        Console.WriteLine($"{s_failures} CHECK(S) FAILED."); return 1;
    }
}
