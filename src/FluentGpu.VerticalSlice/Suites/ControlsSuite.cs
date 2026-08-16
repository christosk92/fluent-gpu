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




static class ControlsSuite
{
    public static void Run(StringTable strings)
    {
        NestedChecks(strings);
        ContextChecks(strings);
        HoverChecks(strings);
        HoverBoundaryChecks(strings);
        MediaCardEngineChecks(strings);
        VideoHoleChecks(strings);
        MediaPlayerElementChecks(strings);
        ControlsChecks(strings);
        RecipeChecks(strings);
        ControlBindChecks(strings);
        ControlKitIdiomChecks(strings);
        DisabledChecks(strings);
        TextRampChecks(strings);
        GradientRampChecks(strings);
        ClipChannelChecks();
        FocusNavChecks(strings);
        InputVocabularyChecks(strings);
        WaveBInputChecks(strings);
        E5DragDropChecks(strings);
        SortableMathChecks();
        SortableSurfaceChecks(strings);
        VirtualDisclosureChecks(strings);
        VirtualDisclosureFastPathChecks(strings);
        FocusRingChecks(strings);
        Wave2ControlChecks(strings);
        RepeatButtonChecks(strings);
        BasicInputControlChecks(strings);
        W1ControlsChecks(strings);
        D2PasswordRevealFocusChecks(strings);
        TextBoxBlurCommitChecks(strings);
        ProgressIndeterminateLifecycleChecks(strings);
        D3ExpanderChecks(strings);
        D3ExpanderWrapReflowChecks(strings);
        D5EditableComboBoxChecks(strings);
        D67SplitButtonFlyoutChecks(strings);
        ExpanderSettingsChecks(strings);
        SettingsExpanderWideContentChecks(strings);
        CardPickerRadioGroupChecks(strings);
        PipsPagerOutputChecks(strings);
        AutoFitTextChecks(strings);
        FontFamilyChecks(strings);
        GradientBorderChecks(strings);
        PolylineStrokeChecks(strings);
        ContextMenuChecks(strings);
        ToolTipStableWrapChecks(strings);
        SemanticZoomChecks(strings);
        AutoSuggestProgrammaticFocusChecks(strings);
    }

    static void SemanticZoomChecks(StringTable strings)
    {
        var zoomOut = MotionRecipes.SemanticZoomOut;
        var zoomIn = MotionRecipes.SemanticZoomIn;
        bool recipes = zoomOut.Dynamics == MotionTok.StandardSpring.ToDynamics()
            && zoomOut.Enter is { Active: true, Sx: 1.08f, Sy: 1.08f, Opacity: 0f, Blur: 0f }
            && zoomOut.Exit is { Active: true, Sx: 0.94f, Sy: 0.94f, Opacity: 0f, Blur: 0f }
            && zoomIn.Enter is { Active: true, Sx: 0.94f, Sy: 0.94f, Opacity: 0f, Blur: 0f }
            && zoomIn.Exit is { Active: true, Sx: 1.08f, Sy: 1.08f, Opacity: 0f, Blur: 0f };
        Check("gate.semantic-zoom.motion uses the standard spring for directional scale+opacity only (no root blur)",
            recipes, $"out={zoomOut.Enter}->{zoomOut.Exit} in={zoomIn.Enter}->{zoomIn.Exit}");

        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("semantic-zoom-control", new Size2(300, 180), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var probe = new SemanticZoomControlProbe();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame();
        bool initial = HasGlyph(device, strings, "semantic-in") && !HasGlyph(device, strings, "semantic-out");

        // Latest-wins: reverse before the first frame can present the staged overview. Only the reversal completes.
        probe.Controller.ZoomOutTo(3);
        probe.Controller.ZoomInTo(8);
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool reversal = HasGlyph(device, strings, "semantic-in") && probe.Started.Count == 2
            && probe.Completed.Count == 1
            && probe.Started[0] is { From: SemanticZoomViewKind.ZoomedIn, To: SemanticZoomViewKind.ZoomedOut,
                                     SourceIndex: 3, DestinationIndex: 13 }
            && probe.Completed[0].OperationId == probe.Started[1].OperationId
            && probe.Completed.All(c => c.OperationId != probe.Started[0].OperationId);

        // A direct controlled-signal write goes through the same staged path. No ItemsView means readiness is immediate.
        probe.IsZoomedOut.Value = true;
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool external = HasGlyph(device, strings, "semantic-out")
            && probe.Completed.Count == 2
            && probe.Completed[^1].To == SemanticZoomViewKind.ZoomedOut;

        // An invalid map removes anchoring, not the view change.
        probe.Controller.ZoomInTo(9);
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool invalidStillSwaps = HasGlyph(device, strings, "semantic-in")
            && probe.Started[^1].DestinationIndex == -1
            && probe.Completed[^1].OperationId == probe.Started[^1].OperationId;

        Check("gate.semantic-zoom.control controlled signal + controller verbs, map/invalid-map, callbacks and rapid reversal are latest-wins",
            initial && reversal && external && invalidStillSwaps,
            $"initial={initial} reversal={reversal} external={external} invalid={invalidStillSwaps} "
            + $"started=[{string.Join(',', probe.Started.Select(static x => x.OperationId))}] "
            + $"completed=[{string.Join(',', probe.Completed.Select(static x => x.OperationId))}]");

        using var disabledApp = new HeadlessPlatformApp();
        var disabledWindow = new HeadlessWindow(new WindowDesc("semantic-zoom-disabled", new Size2(220, 120), 1f));
        disabledWindow.Show();
        var disabledDevice = new HeadlessGpuDevice();
        var disabledProbe = new SemanticZoomControlProbe(canChangeViews: false);
        using var disabledHost = new AppHost(disabledApp, disabledWindow, disabledDevice,
            new HeadlessFontSystem(strings), strings, disabledProbe);
        disabledHost.RunFrame();
        disabledProbe.Controller.ZoomOutTo(4);
        disabledProbe.IsZoomedOut.Value = true;
        for (int i = 0; i < 2; i++) disabledHost.RunFrame();
        bool disabled = HasGlyph(disabledDevice, strings, "semantic-in")
            && disabledProbe.Started.Count == 0 && disabledProbe.Completed.Count == 0;
        Check("gate.semantic-zoom.disabled CanChangeViews=false rejects controller and controlled-signal changes",
            disabled, $"in={HasGlyph(disabledDevice, strings, "semantic-in")} started={disabledProbe.Started.Count}");
    }

    // gate.tooltip.stableWrap — ToolTip.Wrap vs ToolTip.WrapStable, the churn contract.
    //
    // Wrap compares its target by REFERENCE (ToolTipSlots.Equals), which is correct but useless to a parent that
    // rebuilds its children every render: the target is a new instance each time, so every wrapped element re-renders
    // its mounted ToolTip core. A shell that wraps dozens of targets (a compact sidebar rail, a command bar) therefore
    // put ToolTip×N in nearly every idle reconcile flush. WrapStable takes the target as a MOUNT-STABLE factory, so an
    // unchanged (delegate, text) pair compares equal and the re-push short-circuits — while a real TEXT change still
    // re-renders, and the factory (invoked inside the ToolTip's OWN render) still delivers live content.
    static void ToolTipStableWrapChecks(StringTable strings)
    {
        // (a) Wrap — a fresh target per parent render REACHES the mounted core (its new width lands, no remount).
        bool wrapLive;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tt-wrap", new Size2(320, 160), 1f)); window.Show();
            var w = new Signal<float>(100f);
            var targets = new List<NodeHandle>();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Padding = Edges4.All(12),
                        Children =
                        [
                            ToolTip.Wrap(
                                new BoxEl
                                {
                                    Width = w.Value, Height = 20f, Fill = Tok.AccentDefault,
                                    OnRealized = h => { if (!targets.Contains(h)) targets.Add(h); },
                                }, "tip"),
                        ],
                    },
                });
            host.RunFrame();
            var t0 = targets.Count > 0 ? targets[0] : NodeHandle.Null;
            bool mount = targets.Count == 1 && !t0.IsNull && Near(host.Scene.AbsoluteRect(t0).W, 100f);
            w.Value = 160f;
            host.RunFrame(); host.RunFrame();
            wrapLive = mount && targets.Count == 1 && Near(host.Scene.AbsoluteRect(t0).W, 160f);
        }

        // (b)+(c) WrapStable — the same parent churn is SILENT, but a text change is not.
        bool stableMount, stableQuiet, stableTextLive;
        int quietBuilds, liveBuilds;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tt-stable", new Size2(320, 160), 1f)); window.Show();
            var bump = new Signal<int>(0);      // re-renders the PARENT only
            var text = new Signal<string>("tip");
            var targets = new List<NodeHandle>();
            float width = 100f;                 // deliberately NOT a signal: only a ToolTip re-render can observe it
            int builds = 0;
            Element MakeTarget()
            {
                builds++;
                return new BoxEl
                {
                    Width = width, Height = 20f, Fill = Tok.AccentDefault,
                    OnRealized = h => { if (!targets.Contains(h)) targets.Add(h); },
                };
            }
            Func<Element> factory = MakeTarget;   // ONE delegate instance for the whole run — the stability contract

            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Direction = 1, Padding = Edges4.All(12),
                        Children = [new TextEl("gen " + bump.Value) { Size = 10f }, ToolTip.WrapStable(factory, text.Value)],
                    },
                });
            host.RunFrame();
            var t0 = targets.Count > 0 ? targets[0] : NodeHandle.Null;
            stableMount = builds == 1 && targets.Count == 1 && !t0.IsNull
                          && Near(host.Scene.AbsoluteRect(t0).W, 100f);

            // (b) the parent re-renders and re-pushes an EQUAL (delegate, text) pair → the ToolTip must NOT re-render,
            //     so the factory does not run again and the non-reactive width change is (correctly) not observed.
            width = 160f;
            bump.Value = 1;
            host.RunFrame(); host.RunFrame();
            quietBuilds = builds;
            stableQuiet = stableMount && builds == 1 && Near(host.Scene.AbsoluteRect(t0).W, 100f);

            // (c) a TEXT change IS a prop change → the core re-renders in place, the factory runs again inside ITS
            //     render, and the new width lands on the same node (no remount).
            text.Value = "tip2";
            host.RunFrame(); host.RunFrame();
            liveBuilds = builds;
            stableTextLive = builds >= 2 && targets.Count == 1 && Near(host.Scene.AbsoluteRect(t0).W, 160f);
        }

        Check("gate.tooltip.stableWrap ToolTip.Wrap re-renders on a fresh target; WrapStable with the same delegate+text does not, but a text change does",
            wrapLive && stableQuiet && stableTextLive,
            $"wrapLive={wrapLive} mount={stableMount} quiet={stableQuiet}(builds={quietBuilds}) textLive={stableTextLive}(builds={liveBuilds})");

        // gate.tooltip.growFill — the service wrapper is a flex ROW (BoxEl.Direction defaults to 0), so a wrapped
        // auto-width target is MAIN-axis sized inside it: content width, not the wrapper's. A wrapper stretched by a
        // COLUMN parent therefore held a shrink-wrapped target — which is why the sidebar's tooltip-carrying rows (a
        // track row, a missing-entity row, an unavailable action row) painted narrower fill plates than the unwrapped
        // rows above and below them. `grow:` is the OPT-IN cure; the DEFAULT must stay byte-identical, because the
        // dozens of wrapped chips and icon buttons in columns app-wide are content-sized on purpose.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tt-grow", new Size2(400, 200), 1f)); window.Show();
            NodeHandle filled = default, plain = default, sized = default;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    // A COLUMN of fixed width: it cross-stretches each tooltip WRAPPER to 300, so the only question the
                    // gate asks is whether the TARGET inside that wrapper follows.
                    Build = () => new BoxEl
                    {
                        Direction = 1, Width = 300f, Gap = 4f,
                        Children =
                        [
                            // opted in — must fill the column
                            ToolTip.Wrap(new BoxEl
                            {
                                Height = 20f, Fill = Tok.AccentDefault, OnRealized = h => filled = h,
                                Children = [new BoxEl { Width = 40f, Height = 20f }],   // intrinsically 40 wide
                            }, "fill", grow: 1f),
                            // default — must stay at its content width, exactly as before the parameter existed
                            ToolTip.Wrap(new BoxEl
                            {
                                Height = 20f, Fill = Tok.AccentDefault, OnRealized = h => plain = h,
                                Children = [new BoxEl { Width = 40f, Height = 20f }],
                            }, "plain"),
                            // opted in but the target DECLARES a Width — its own size wins, never silently overridden
                            ToolTip.Wrap(new BoxEl
                            {
                                Width = 60f, Height = 20f, Fill = Tok.AccentDefault, OnRealized = h => sized = h,
                            }, "sized", grow: 1f),
                        ],
                    },
                });
            host.RunFrame();
            var scene = host.Scene;
            float wFill = filled.IsNull ? -1f : scene.AbsoluteRect(filled).W;
            float wPlain = plain.IsNull ? -1f : scene.AbsoluteRect(plain).W;
            float wSized = sized.IsNull ? -1f : scene.AbsoluteRect(sized).W;
            Check("gate.tooltip.growFill ToolTip.Wrap(grow:) makes the wrap layout-transparent on the MAIN axis too — the target fills the slot — while the default wrap stays content-sized byte-for-byte and a target with its own Width is never overridden",
                Near(wFill, 300f, 0.5f) && Near(wPlain, 40f, 0.5f) && Near(wSized, 60f, 0.5f),
                $"fill={wFill:0.#}(300) plain={wPlain:0.#}(40) sized={wSized:0.#}(60)");
        }
    }

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

    static void HoverBoundaryChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200f, Height = 100f, OnClick = static () => { },
            Children =
            [
                new BoxEl
                {
                    Width = 200f, Height = 50f, OnClick = static () => { },
                    Children = [new BoxEl { Width = 20f, Height = 20f, Opacity = 0f, HoverOpacity = 1f }],
                },
                new BoxEl
                {
                    Width = 200f, Height = 50f, OnClick = static () => { },
                    Children = [new BoxEl { Width = 20f, Height = 20f, Opacity = 0f, HoverOpacity = 1f }],
                },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);
        var firstRow = scene.FirstChild(scene.Root);
        var secondRow = scene.NextSibling(firstRow);
        var firstReveal = scene.FirstChild(firstRow);
        var secondReveal = scene.FirstChild(secondRow);
        var dispatcher = new InputDispatcher(scene);
        var anim = new AnimEngine(scene);
        dispatcher.OnHoverChanged = anim.SetHover;
        dispatcher.Dispatch([new InputEvent(InputKind.PointerMove, new Point2(100f, 25f), 0, 0)]);
        bool firstOn = scene.TryGetInteract(firstReveal, out var firstIa) && firstIa.HoverTarget > 0.99f;
        bool secondOff = scene.TryGetInteract(secondReveal, out var secondIa) && secondIa.HoverTarget < 0.01f;
        Check("26b. a list ancestor hover reveals only the hovered interactive row's descendant affordance",
            firstOn && secondOff, $"first={firstIa.HoverTarget:0.00} second={secondIa.HoverTarget:0.00}");
    }

    static void ContextMenuChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        static void Right(HeadlessWindow w, Point2 p)
        {
            w.QueueInput(new InputEvent(InputKind.PointerDown, p, 1, 0));   // button 1 = right
            w.QueueInput(new InputEvent(InputKind.PointerUp, p, 1, 0));
        }
        static void Left(HeadlessWindow w, Point2 p)
        {
            w.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            w.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
        }
        static void RunN(AppHost h, int n) { for (int i = 0; i < n; i++) h.RunFrame(); }
        static NodeHandle FindScroll(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.TryGetScroll(n, out _)) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            { var r = FindScroll(s, c); if (!r.IsNull) return r; }
            return NodeHandle.Null;
        }

        // gate.ctx.identity-header — optional entity identity is a non-command strip above the menu rows.
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-header", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe { WithHeader = true };
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 3);
            var title = FindTextNode(host.Scene, strings, host.Scene.Root, "Header title");
            var subtitle = FindTextNode(host.Scene, strings, host.Scene.Root, "Header subtitle");
            var first = FindTextNode(host.Scene, strings, host.Scene.Root, "A1");
            bool ordered = !title.IsNull && !first.IsNull
                && host.Scene.AbsoluteRect(title).Y < host.Scene.AbsoluteRect(first).Y;
            Check("gate.ctx.identity-header an optional artwork/title/subtitle strip renders above the actionable menu rows",
                probe.Service!.AnyOpen && ordered && !subtitle.IsNull,
                $"open={probe.Service!.AnyOpen} title={title.Raw.Index} subtitle={subtitle.Raw.Index} ordered={ordered}");
        }

        // gate.ctx.open-at-pointer — a right press+release on an attached row opens ONE menu whose first row lands ON the
        // right-tap point (OpenAtLocal: owner rect + local − FlyoutMargin ⇒ presenter top-left at the point), light-dismiss.
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-open", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var pt = CenterOf(host.Scene, probe.RowA);
            Right(w, pt); RunN(host, 3);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool opened = probe.Service!.AnyOpen && !mi.IsNull;
            bool atPoint = !mi.IsNull && Near(r.X, pt.X, 30f) && r.Y >= pt.Y - 2f && r.Y <= pt.Y + 30f;
            Check("gate.ctx.open-at-pointer right-click on an attached row opens one menu at the tap point (presenter top-left on the point, light-dismiss)",
                opened && atPoint, $"open={opened} first=({r.X:0.#},{r.Y:0.#}) pt=({pt.X:0.#},{pt.Y:0.#})");
        }

        // gate.ctx.lazy-items — the factory is NOT invoked at render (a re-render leaves the count at 0); it runs exactly
        // once, AT OPEN, so a bound row reads current state at open time (RowScope.Index.Peek()).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-lazy", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame(); host.RunFrame();   // mount + a re-render
            bool notAtRender = probe.BuildsA == 0;
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 3);
            bool builtAtOpen = probe.BuildsA == 1 && probe.Service!.AnyOpen;
            Check("gate.ctx.lazy-items the items factory runs at OPEN time, not render (count 0 across renders, 1 after the right-click)",
                notAtRender && builtAtOpen, $"atRender={probe.BuildsA} (want 0 pre-open) open={probe.Service!.AnyOpen}");
        }

        // gate.ctx.empty-menu-no-open — a null factory result opens nothing (the factory still ran).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-empty", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe { ReturnNull = true };
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 3);
            bool noOpen = !probe.Service!.AnyOpen && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
            Check("gate.ctx.empty-menu-no-open a null/empty model opens nothing (factory ran, no overlay)",
                noOpen && probe.BuildsA == 1, $"anyOpen={probe.Service!.AnyOpen} builds={probe.BuildsA}");
        }

        // gate.ctx.disabled-rows — a model of only disabled/separator rows opens nothing.
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-disabled", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe { AllDisabled = true };
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 3);
            bool noOpen = !probe.Service!.AnyOpen && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
            Check("gate.ctx.disabled-rows an all-disabled model opens nothing",
                noOpen, $"anyOpen={probe.Service!.AnyOpen}");
        }

        // gate.ctx.keyboard-at-node — focus a row, VK_APPS ⇒ the menu anchors to the row RECT (not a point), the first
        // row takes focus, and Esc restores focus to the row (SavedFocus).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-kbd", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var rowRect = host.Scene.AbsoluteRect(probe.RowA);
            Left(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 2);   // focus the row (pointer focus)
            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Apps)); RunN(host, 4);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool nodeAnchored = !mi.IsNull && Near(r.X, rowRect.X, 30f) && r.Y >= rowRect.Bottom - 2f;   // below the ROW, not at a point
            bool firstFocused = !mi.IsNull && host.Input.Focused == mi;
            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape)); RunN(host, 45);
            bool restored = host.Input.Focused == probe.RowA && !probe.Service!.AnyOpen;
            Check("gate.ctx.keyboard-at-node VK_APPS anchors the menu to the row rect + focuses the first item; Esc restores focus to the row",
                nodeAnchored && firstFocused && restored,
                $"nodeAnchored={nodeAnchored} firstFocused={firstFocused}(focus={host.Input.Focused.Raw.Index} first={mi.Raw.Index}) restored={restored}");
        }

        // gate.ctx.dismiss-reopen-one-gesture — THE pitfall: a menu open on row A; ONE right-click on row B closes A AND
        // opens B's menu at the B point (the scrim's OnContextRequested → CloseTop + RedispatchContextAt through the
        // synchronously-unmarked scrim).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-reopen", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 3);
            bool aOpen = probe.Service!.AnyOpen && probe.BuildsA == 1;
            var ptB = CenterOf(host.Scene, probe.RowB);
            Right(w, ptB); RunN(host, 45);   // one gesture on B: dismiss A + reopen at B, then settle A away
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool reopenedAtB = probe.Service!.AnyOpen && probe.BuildsB == 1 && !mi.IsNull
                               && Near(r.X, ptB.X, 30f) && r.Y >= ptB.Y - 2f && r.Y <= ptB.Y + 30f;
            Check("gate.ctx.dismiss-reopen-one-gesture a right-click on B while A's menu is open dismisses A AND opens B's menu at the B point (scrim redispatch)",
                aOpen && reopenedAtB, $"aOpen={aOpen} reopenB={reopenedAtB} buildsB={probe.BuildsB} first=({r.X:0.#},{r.Y:0.#}) ptB=({ptB.X:0.#},{ptB.Y:0.#})");
        }

        // gate.ctx.dismiss-only-on-empty-area — a right-click on inert background while a menu is open dismisses it and
        // opens nothing (the redispatch finds no ContextBit under the point).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-empty-area", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 3);
            bool aOpen = probe.Service!.AnyOpen;
            Right(w, new Point2(430f, 380f)); RunN(host, 45);   // inert corner (no attached row there)
            bool dismissedOnly = !probe.Service!.AnyOpen && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
            Check("gate.ctx.dismiss-only-on-empty-area a right-click on inert background dismisses the open menu and opens nothing",
                aOpen && dismissedOnly, $"aOpen={aOpen} dismissedOnly={dismissedOnly}");
        }

        // gate.ctx.race-open-close-open — a rapid supersede (open A, then reopen via B before A settles) leaves exactly
        // one live windowed popup after settle (no leaked PopupWindowToken).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-race", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowA)); RunN(host, 2);   // rapid: reopen before A's open settles
            Right(w, CenterOf(host.Scene, probe.RowB)); RunN(host, 45);
            // <=1 (not ==1): catches a leaked token (would be ≥2) while tolerating a constrained (non-windowed) fallback (0).
            bool oneLive = probe.Service!.AnyOpen && host.PopupWindows.Count <= 1;
            Check("gate.ctx.race-open-close-open a rapid open→close→open leaves at most one live windowed popup (no leaked token) with a menu still open",
                oneLive, $"anyOpen={probe.Service!.AnyOpen} popupWindows={host.PopupWindows.Count}");
        }

        // gate.ctx.scrim-blocks-wheel — with a menu open the light-dismiss scrim is the topmost hit, so a wheel over the
        // covered list scrolls NOTHING (the ancestor-only wheel walk finds no scrollable); once closed, the same wheel
        // scrolls the list (proving the wheel is real and the scrim was blocking it).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-wheel", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextWheelProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scroller = FindScroll(host.Scene, host.Scene.Root);
            host.Scene.TryGetScroll(scroller, out var before);
            Right(w, CenterOf(host.Scene, probe.Row)); RunN(host, 3);
            bool opened = probe.Service!.AnyOpen;
            var listPt = new Point2(150f, 250f);   // over the list, under the scrim
            w.QueueInput(new InputEvent(InputKind.Wheel, listPt, 0, 0, 240f)); RunN(host, 2);
            host.Scene.TryGetScroll(scroller, out var afterBlocked);
            bool blocked = Near(afterBlocked.OffsetY, before.OffsetY, 0.5f);
            probe.Service!.CloseAll(); RunN(host, 45);
            w.QueueInput(new InputEvent(InputKind.Wheel, listPt, 0, 0, 240f)); RunN(host, 3);
            host.Scene.TryGetScroll(scroller, out var afterFree);
            bool scrolls = afterFree.OffsetY > before.OffsetY + 4f;
            Check("gate.ctx.scrim-blocks-wheel a wheel over the covered list does not scroll while a menu is open; the same wheel scrolls it once closed",
                !scroller.IsNull && opened && blocked && scrolls,
                $"opened={opened} blocked={blocked}(off={afterBlocked.OffsetY:0.#}) scrolls={scrolls}(off={afterFree.OffsetY:0.#})");
        }

        // gate.ctx.touch-hold-opens — a synthetic touch down + a >500ms stationary hold fires the context request
        // (Trigger.Hold) and opens the menu at the contact point (the pressed visual is held through the fire).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-hold", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var pt = CenterOf(host.Scene, probe.RowA);
            uint t = s_touchClockMs;
            w.QueueInput(Touch(InputKind.PointerDown, pt, t, 97));
            host.RunFrame();
            bool pressedDuringHold = (host.Scene.Flags(probe.RowA) & NodeFlags.Pressed) != 0;
            for (int i = 0; i < 38; i++) host.RunFrame();   // > 500ms hold → Hold win fires the context request
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool opened = probe.Service!.AnyOpen && !mi.IsNull && Near(r.X, pt.X, 30f) && r.Y >= pt.Y - 2f && r.Y <= pt.Y + 30f;
            w.QueueInput(Touch(InputKind.PointerUp, pt, t + 620, 97)); host.RunFrame();
            s_touchClockMs = t + 2000;
            Check("gate.ctx.touch-hold-opens a >500ms stationary touch long-press opens the menu at the contact (press held through the fire)",
                pressedDuringHold && opened, $"pressedDuringHold={pressedDuringHold} opened={opened} first=({r.X:0.#},{r.Y:0.#}) pt=({pt.X:0.#},{pt.Y:0.#})");
        }

        // gate.ctx.re-render-anchor — the regression the ContextRequestEventArgs.Node carry fixed: after row A RE-RENDERS
        // (a plain OnRealized capture would have gone stale → menu at the window origin), a right-click still opens the
        // menu AT the tap point because ContextMenu.Attach anchors from args.Node (the live ContextBit owner), not a
        // captured handle. Mirror gate.ctx.open-at-pointer, but bump the probe's Rev between frames to force the re-render.
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-rerender", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            probe.Rev.Value = 1; RunN(host, 2);   // re-render row A (would stale a captured anchor)
            probe.Rev.Value = 2; RunN(host, 2);
            var pt = CenterOf(host.Scene, probe.RowA);
            Right(w, pt); RunN(host, 3);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool opened = probe.Service!.AnyOpen && !mi.IsNull;
            bool atPoint = !mi.IsNull && Near(r.X, pt.X, 30f) && r.Y >= pt.Y - 2f && r.Y <= pt.Y + 30f;
            Check("gate.ctx.re-render-anchor after row A re-renders, a right-click still opens the menu at the tap point (anchors from args.Node, not a stale OnRealized capture)",
                opened && atPoint, $"open={opened} first=({r.X:0.#},{r.Y:0.#}) pt=({pt.X:0.#},{pt.Y:0.#})");
        }

        // gate.ctx.invoke-anchors-source — a LEFT click on row B's "…" (ClickRequestsContext) opens ONE menu anchored
        // at the BUTTON rect (ContextRequestTrigger.Invoke → rect-anchored on args.Source, the Keyboard rule
        // generalized), and the first item is NOT focused (pointer-originated).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-invoke", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var btn = host.Scene.AbsoluteRect(probe.MoreB);
            Left(w, CenterOf(host.Scene, probe.MoreB)); RunN(host, 3);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool opened = probe.Service!.AnyOpen && probe.BuildsB == 1 && !mi.IsNull;
            bool atButton = !mi.IsNull && Near(r.X, btn.X, 30f) && r.Y >= btn.Bottom - 2f && r.Y <= btn.Bottom + 30f;
            bool firstNotFocused = !mi.IsNull && host.Input.Focused != mi;
            Check("gate.ctx.invoke-anchors-source a left click on the \"…\" opens one menu anchored at the BUTTON rect, first item NOT focused",
                opened && atButton && firstNotFocused,
                $"open={opened} first=({r.X:0.#},{r.Y:0.#}) btn=({btn.X:0.#},{btn.Bottom:0.#}) notFocused={firstNotFocused}");
        }

        // gate.ctx.invoke-source-field — the args carry: right-click ⇒ Source == Node (== row B, the ContextBit owner);
        // "…" click ⇒ Trigger=Invoke, Source == the button, Node == the row (the funnel re-entered at the button, the
        // walk stopped at the row).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-invoke-src", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Right(w, CenterOf(host.Scene, probe.RowB)); RunN(host, 3);
            bool rightSourceIsNode = probe.LastTrigger == ContextRequestTrigger.Pointer
                                  && probe.LastNode == probe.RowB && probe.LastSource == probe.RowB;
            probe.Service!.CloseAll(); RunN(host, 45);
            Left(w, CenterOf(host.Scene, probe.MoreB)); RunN(host, 3);
            bool invokeFields = probe.LastTrigger == ContextRequestTrigger.Invoke
                             && probe.LastNode == probe.RowB && probe.LastSource == probe.MoreB;
            Check("gate.ctx.invoke-source-field right-click: Source==Node==row; \"…\" click: Trigger=Invoke, Source=button, Node=row",
                rightSourceIsNode && invokeFields,
                $"right={rightSourceIsNode} invoke={invokeFields} trig={probe.LastTrigger} node={probe.LastNode.Raw.Index} src={probe.LastSource.Raw.Index}");
        }

        // gate.ctx.invoke-keyboard-focuses-first — Space on the FOCUSED "…" dispatches a Keyboard-trigger request
        // (key-activation keeps WinUI TryGetPosition-false semantics), so the menu anchors to the button rect AND
        // focuses its first item — unlike the pointer Invoke above.
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-invoke-kbd", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var btn = host.Scene.AbsoluteRect(probe.MoreB);
            host.Input.SetFocus(probe.MoreB);
            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Space));
            w.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Space));
            RunN(host, 4);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool opened = probe.Service!.AnyOpen && !mi.IsNull && probe.LastTrigger == ContextRequestTrigger.Keyboard;
            bool atButton = !mi.IsNull && Near(r.X, btn.X, 30f) && r.Y >= btn.Bottom - 2f;
            bool firstFocused = !mi.IsNull && host.Input.Focused == mi;
            Check("gate.ctx.invoke-keyboard-focuses-first Space on the focused \"…\" opens a Keyboard-trigger menu at the button rect, first item focused",
                opened && atButton && firstFocused,
                $"open={opened} trig={probe.LastTrigger} atButton={atButton} firstFocused={firstFocused}");
        }

        // gate.ctx.invoke-re-render-anchor — the stale-capture bug the prop kills: bump the probe Rev (row B re-renders,
        // an OnRealized capture would have gone stale) then click the "…" — the menu anchors to the LIVE button rect
        // (RequestContextFrom reads AbsoluteRect(source) at dispatch time; no captured node, no captured rect).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-invoke-rerender", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            probe.Rev.Value = 1; RunN(host, 2);   // re-render row B (would stale a captured anchor)
            probe.Rev.Value = 2; RunN(host, 2);
            var btn = host.Scene.AbsoluteRect(probe.MoreB);
            Left(w, CenterOf(host.Scene, probe.MoreB)); RunN(host, 3);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            var r = mi.IsNull ? default : host.Scene.AbsoluteRect(mi);
            bool opened = probe.Service!.AnyOpen && probe.BuildsB == 1 && !mi.IsNull;
            bool atButton = !mi.IsNull && Near(r.X, btn.X, 30f) && r.Y >= btn.Bottom - 2f && r.Y <= btn.Bottom + 30f;
            Check("gate.ctx.invoke-re-render-anchor after row B re-renders, the \"…\" click still anchors the menu to the LIVE button rect",
                opened && atButton, $"open={opened} first=({r.X:0.#},{r.Y:0.#}) btn=({btn.X:0.#},{btn.Bottom:0.#})");
        }

        // gate.ctx.invoke-disabled — the DISABLED "…" opens nothing: disabled nodes don't hit-test, so the click never
        // reaches the ClickRequestsContext bit (and the fall-through row click is a plain click, not a context request).
        {
            using var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc("ctx-invoke-disabled", new Size2(480, 400), 1f)); w.Show();
            var probe = new ContextMenuProbe();
            using var host = new AppHost(app, w, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            Left(w, CenterOf(host.Scene, probe.MoreBDisabled)); RunN(host, 3);
            bool noOpen = !probe.Service!.AnyOpen && probe.BuildsB == 0
                       && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
            Check("gate.ctx.invoke-disabled a disabled \"…\" opens nothing",
                noOpen, $"anyOpen={probe.Service!.AnyOpen} buildsB={probe.BuildsB}");
        }
    }

    static void RecipeChecks(StringTable strings)
    {
        // A deterministic (theme-independent) recipe with BOTH halves + a stroke ramp.
        var fillRamp = new StateBrush(ColorF.FromRgba(10, 10, 10), ColorF.FromRgba(20, 20, 20), ColorF.FromRgba(30, 30, 30), ColorF.FromRgba(5, 5, 5));
        var strokeRamp = new StateBrush(ColorF.FromRgba(40, 40, 40), ColorF.FromRgba(50, 50, 50), ColorF.FromRgba(60, 60, 60), ColorF.FromRgba(35, 35, 35));
        var recipe = new InteractionRecipe
        {
            Fill = fillRamp, Stroke = strokeRamp, StrokeWidth = 2f,
            HoverScale = 1.04f, PressScale = 0.96f, HoverOpacity = 0.9f,
            BrushMs = 120f, Motion = MotionTokenId.StandardSpring,
        };
        // Pre-set channels the recipe does NOT name (must survive) + a caller While* leg (must be preserved).
        var pre = new BoxEl
        {
            Width = 33f, Padding = Edges4.All(7), Corners = CornerRadius4.All(5f),
            WhileFocus = new MotionTarget { Scale = 1.5f }, OnClick = static () => { },
        };

        // gate.ctl.recipe.expand — the exact field writes + untouched channels.
        var box = pre.Interactive(recipe);
        bool brush = box.Fill.Value == fillRamp.Rest && box.HoverFill.Value == fillRamp.Hover && box.PressedFill.Value == fillRamp.Pressed
                     && box.BrushTransitionMs == 120f && box.IsEnabled;
        bool border = box.BorderColor.Value == strokeRamp.Rest && box.HoverBorderColor == strokeRamp.Hover
                      && box.PressedBorderColor == strokeRamp.Pressed && box.BorderWidth == 2f;
        bool motion = box.WhileHover is { } wh && wh.Scale == 1.04f && wh.Opacity == 0.9f
                      && box.WhilePressed is { } wp && wp.Scale == 0.96f && wp.Opacity == 1f
                      && box.Transition is not null;
        bool untouched = box.Width.Value == 33f && box.WhileFocus is { } wf && wf.Scale == 1.5f && box.OnClick is not null;
        Check("gate.ctl.recipe.expand .Interactive writes fill/border/brush-ms + While* targets; unnamed channels untouched",
            brush && border && motion && untouched, $"brush={brush} border={border} motion={motion} untouched={untouched}");

        // A recipe with NO motion half must not touch While*/Transition (don't stomp channels the recipe doesn't use).
        var noMotion = new InteractionRecipe { Fill = fillRamp };   // HoverScale/PressScale default 1, opacities NaN
        var b2 = pre.Interactive(noMotion);
        Check("gate.ctl.recipe.expand no-motion recipe leaves While*/Transition untouched (caller WhileFocus survives, no WhileHover)",
            b2.WhileHover is null && b2.WhilePressed is null && b2.Transition is null && b2.WhileFocus is { } f2 && f2.Scale == 1.5f,
            $"hover={b2.WhileHover is null} press={b2.WhilePressed is null} transition={b2.Transition is null}");

        // One-transform-owner: a bound Transform suppresses the recipe's While* (the bound matrix is the sole owner).
        var bound = new BoxEl { Transform = Prop.Of(() => Affine2D.Identity), OnClick = static () => { } }.Interactive(recipe);
        Check("gate.ctl.recipe.expand bound Transform suppresses the While* motion half (one transform owner)",
            bound.WhileHover is null && bound.WhilePressed is null && bound.Fill.Value == fillRamp.Rest,
            $"hover={bound.WhileHover is null} press={bound.WhilePressed is null} brushStillApplied={bound.Fill.Value == fillRamp.Rest}");

        // gate.ctl.recipe.presets — the four presets resolve the expected Tok values, and a theme swap re-resolves them.
        var kind0 = Tok.Theme;
        bool subtleNow = Interaction.Subtle.Fill.Hover == Tok.FillSubtleSecondary && Interaction.Subtle.Fill.Rest == Tok.FillSubtleTransparent;
        bool listRowNow = Interaction.ListRow.Fill.Hover == Tok.FillSubtleSecondary;
        bool cardNow = Interaction.Card.Fill.Rest == Tok.FillCardDefault && Interaction.Card.Stroke is { } cs && cs.Rest == Tok.StrokeCardDefault
                       && Interaction.Card.PressScale == 0.985f && Interaction.Card.Motion == MotionTokenId.StandardSpring;
        bool ghostNow = Interaction.AccentGhost.Fill.Hover == Tok.AccentSubtle && Interaction.AccentGhost.Fill.Rest == ColorF.Transparent;
        var subtleHover0 = Interaction.Subtle.Fill.Hover;
        var cardRest0 = Interaction.Card.Fill.Rest;
        // Flip the theme kind: FillSubtleSecondary / FillCardDefault differ light↔dark, so a live re-resolve must change.
        Tok.Use(kind0 == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
        bool reResolved = Interaction.Subtle.Fill.Hover == Tok.FillSubtleSecondary && Interaction.Subtle.Fill.Hover != subtleHover0
                          && Interaction.Card.Fill.Rest == Tok.FillCardDefault && Interaction.Card.Fill.Rest != cardRest0;
        Tok.Use(kind0);   // restore the original theme kind
        Check("gate.ctl.recipe.presets Subtle/ListRow/Card/AccentGhost resolve Tok values; a theme swap re-resolves (theme-live)",
            subtleNow && listRowNow && cardNow && ghostNow && reResolved,
            $"subtle={subtleNow} listRow={listRowNow} card={cardNow} ghost={ghostNow} reResolved={reResolved}");

        // gate.ctl.recipe.control — the standard control-surface preset (G7): the opaque control fill ramp
        // (default→secondary→tertiary→disabled, the same ramp Button's Standard appearance uses) + a 1px control border,
        // fill+border only. Theme-live: the get-only preset re-reads Tok on every access (proven in BOTH theme kinds).
        bool controlNow = Interaction.Control.Fill.Rest == Tok.FillControlDefault
                          && Interaction.Control.Fill.Hover == Tok.FillControlSecondary
                          && Interaction.Control.Fill.Pressed == Tok.FillControlTertiary
                          && Interaction.Control.Fill.Disabled == Tok.FillControlDisabled
                          && Interaction.Control.Stroke is { } ctrlStroke && ctrlStroke.Rest == Tok.StrokeControlDefault
                          && Interaction.Control.StrokeWidth == 1f
                          && Interaction.Control.HoverScale == 1f && Interaction.Control.PressScale == 1f;   // no geometry
        Tok.Use(kind0 == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark);
        bool controlLiveOtherTheme = Interaction.Control.Fill.Rest == Tok.FillControlDefault
                                     && Interaction.Control.Stroke is { } ctrlStroke2 && ctrlStroke2.Rest == Tok.StrokeControlDefault;
        Tok.Use(kind0);   // restore
        Check("gate.ctl.recipe.control standard control-surface preset resolves FillControl ramp + control border (theme-live in both kinds)",
            controlNow && controlLiveOtherTheme, $"control={controlNow} liveOtherTheme={controlLiveOtherTheme}");

        // gate.ctl.recipe.disabled — isEnabled=false applies the Disabled legs, sets IsEnabled=false (the engine's
        // hover/press gate), and suppresses the motion half (no hover/press response).
        var dis = pre.Interactive(recipe, isEnabled: false);
        bool disElem = !dis.IsEnabled && dis.Fill.Value == fillRamp.Disabled && dis.BorderColor.Value == strokeRamp.Disabled
                       && dis.WhileHover is null && dis.WhilePressed is null;
        // Reconcile it and confirm the scene Disabled flag is set (what actually blocks hover/press dispatch).
        using (var app = new HeadlessPlatformApp())
        {
            var window = new HeadlessWindow(new WindowDesc("recipe-dis", new Size2(200, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            NodeHandle n = default;
            var root = new W0fStaticProbe { Build = () => (pre with { OnRealized = h => n = h }).Interactive(recipe, isEnabled: false) };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            bool flagged = !n.IsNull && (host.Scene.Flags(n) & NodeFlags.Disabled) != 0;
            Check("gate.ctl.recipe.disabled disabled legs + IsEnabled=false (scene Disabled flag) + no While* motion",
                disElem && flagged, $"elem={disElem} disabledFlag={flagged}");
        }

        // gate.ctl.recipe.zero-alloc — a scene of N recipe-styled boxes, once mounted, adds NO per-frame paint cost: the
        // recipe bakes into scene columns at reconcile (the cold path), so steady frames are 0-alloc in the hot window
        // (the HotPhaseAllocBytes window spans flush + record + submit + present). Proven the same way as slice gate 9.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("recipe-alloc", new Size2(400, 400), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            const int N = 24;
            InteractionRecipe[] presets = [Interaction.Subtle, Interaction.ListRow, Interaction.Card, Interaction.AccentGhost];
            var root = new W0fStaticProbe
            {
                Build = () =>
                {
                    var kids = new Element[N];
                    for (int i = 0; i < N; i++)
                        kids[i] = new BoxEl { Width = 40f, Height = 20f, Corners = Radii.ControlAll, OnClick = static () => { } }
                            .Interactive(presets[i % presets.Length]);
                    return new BoxEl { Direction = 1, Gap = 2f, Children = kids };
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            for (int i = 0; i < 8; i++) host.RunFrame();   // warm (mount + JIT) → memoized steady state
            var steady = host.RunFrame();
            Check("gate.ctl.recipe.zero-alloc 24 recipe-styled boxes, steady frame: memoized + hot window 0 bytes",
                !steady.Rendered && steady.HotPhaseAllocBytes == 0, $"rendered={steady.Rendered} hot={steady.HotPhaseAllocBytes}B");
        }
    }

    static void ControlBindChecks(StringTable strings)
    {
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        // gate.ctl.bind.toggle — user toggle writes the value signal then fires onChange ONCE; a programmatic write
        // re-skins with NO echo and never re-invokes the owner's render (adjustment #8's decoupling regression pin).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-toggle", new Size2(320, 160), 1f)); window.Show();
            var sig = new Signal<bool>(false);
            int probeRenders = 0, changes = 0; bool lastV = false;
            using var host = new AppHost(app, window, device, fonts, strings,
                new W0fStaticProbe { Build = () => { probeRenders++; return new BoxEl { Padding = Edges4.All(12),
                    Children = [ToggleSwitch.Create(sig, onChange: v => { changes++; lastV = v; })] }; } });
            host.RunFrame();
            int rendersAtMount = probeRenders;
            var ts = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            ClickNode(host, window, ts);
            bool wrote = sig.Value && changes == 1 && lastV;
            int changesBefore = changes;
            sig.Value = false;                      // programmatic write
            host.RunFrame();
            bool noEcho = changes == changesBefore;
            bool decoupled = probeRenders == rendersAtMount;   // the Signal write never re-rendered the owner
            Check("gate.ctl.bind.toggle ToggleSwitch: user toggle writes the signal then fires onChange once; programmatic write re-skins with no echo (owner not re-rendered)",
                wrote && noEcho && decoupled, $"wrote={wrote} changes={changes} noEcho={noEcho} ownerRenders={probeRenders}(mount {rendersAtMount})");
        }

        // gate.ctl.bind.automaterialize — a signal-less ToggleSwitch toggles via its OWN internal signal; an external
        // signal controls another; BOTH ride the one `IsOn ?? own` code path.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-auto", new Size2(320, 220), 1f)); window.Show();
            var extSig = new Signal<bool>(false);
            int autoN = 0, extN = 0;
            using var host = new AppHost(app, window, device, fonts, strings,
                new W0fStaticProbe { Build = () => new BoxEl { Direction = 1, Gap = 8, Padding = Edges4.All(12),
                    Children = [
                        ToggleSwitch.Create(onChange: _ => autoN++),            // signal-less → internal signal
                        ToggleSwitch.Create(extSig, onChange: _ => extN++),     // caller-owned signal
                    ] } });
            host.RunFrame();
            var toggles = Roles(host.Scene, AutomationRole.ToggleSwitch);
            ClickNode(host, window, toggles[0]);
            bool autoToggled = autoN == 1;         // the internal signal drove a toggle
            toggles = Roles(host.Scene, AutomationRole.ToggleSwitch);
            ClickNode(host, window, toggles[1]);
            bool extToggled = extSig.Value && extN == 1;
            Check("gate.ctl.bind.automaterialize signal-less ToggleSwitch toggles via its own internal signal; a caller signal controls another; one code path",
                autoToggled && extToggled, $"auto={autoN} ext={extSig.Value}/{extN}");
        }

        // gate.ctl.bind.check + gate.ctl.bind.tristate — CheckBox 2-state click writes the bool signal; the CheckState
        // overload cycles Unchecked → Checked → Indeterminate → Unchecked through the signal.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-check", new Size2(360, 240), 1f)); window.Show();
            var b = new Signal<bool>(false);
            var tri = new Signal<CheckState>(CheckState.Unchecked);
            int bN = 0, tN = 0;
            using var host = new AppHost(app, window, device, fonts, strings,
                new W0fStaticProbe { Build = () => new BoxEl { Direction = 1, Gap = 8, Padding = Edges4.All(12),
                    Children = [
                        CheckBox.Create("two", b, onChange: _ => bN++),
                        CheckBox.Create("tri", tri, onChange: _ => tN++),
                    ] } });
            host.RunFrame();
            var boxes = Roles(host.Scene, AutomationRole.CheckBox);
            ClickNode(host, window, boxes[0]);
            bool check2 = b.Value && bN == 1;
            boxes = Roles(host.Scene, AutomationRole.CheckBox);
            ClickNode(host, window, boxes[1]); bool c1 = tri.Value == CheckState.Checked;
            boxes = Roles(host.Scene, AutomationRole.CheckBox);
            ClickNode(host, window, boxes[1]); bool c2 = tri.Value == CheckState.Indeterminate;
            boxes = Roles(host.Scene, AutomationRole.CheckBox);
            ClickNode(host, window, boxes[1]); bool c3 = tri.Value == CheckState.Unchecked;
            Check("gate.ctl.bind.check CheckBox 2-state click writes the bool signal (onChange once)",
                check2, $"val={b.Value} changes={bN}");
            Check("gate.ctl.bind.tristate CheckBox CheckState click cycles Unchecked→Checked→Indeterminate→Unchecked via the signal",
                c1 && c2 && c3 && tN == 3, $"c1={c1} c2={c2} c3={c3} changes={tN}");
        }

        // gate.ctl.bind.radio — a RadioButtons click WRITES the selected-index signal (onChange once); arrow roving
        // (Down) moves the selection and updates the SAME signal (selection follows focus).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-radio", new Size2(320, 240), 1f)); window.Show();
            var sel = new Signal<int>(0);   // start at 0 so Tab lands on the roving stop (item 0)
            int rN = 0;
            using var host = new AppHost(app, window, device, fonts, strings,
                new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(12),
                    Children = [RadioButtons.Create(new[] { "A", "B", "C" }, sel, onChange: _ => rN++, maxColumns: 1)] } });
            host.RunFrame();
            // arrow roving: Tab focuses the single roving stop, Down moves selection (selection follows focus) → writes the signal.
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down)); host.RunFrame();
            bool roved = sel.Value == 1 && rN == 1;
            // click selects: clicking item C writes index 2 (mutual exclusion via the one shared signal).
            ClickNode(host, window, Roles(host.Scene, AutomationRole.RadioButton)[2]);
            bool clickWrote = sel.Value == 2 && rN == 2;
            Check("gate.ctl.bind.radio RadioButtons: arrow roving updates the index signal; a click writes the selected index (onChange each)",
                roved && clickWrote, $"afterDown={(roved ? 1 : sel.Value)}@{rN} afterClick={sel.Value}");
        }

        // gate.ctl.bind.segmented-options — the required item content stays first, while selection follows the canonical
        // Signal<int>? + onChange + options contract. AutoSelection initializes quietly; user selection writes then
        // notifies; a programmatic write re-skins without echoing.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-segmented", new Size2(360, 180), 1f)); window.Show();
            var selected = new Signal<int>(-1);
            int changes = 0;
            NodeHandle pill = default;
            var parts = new TemplateParts
            {
                [Segmented.PartSelectionPill] = e => e with { OnRealized = h => pill = h },
            };
            using var host = new AppHost(app, window, device, fonts, strings,
                new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(12),
                    Children = [Segmented.Create(
                    [
                        new SegmentedItem("All"),
                        new SegmentedItem("Hide"),
                        new SegmentedItem("Only"),
                    ],
                    selected,
                    onChange: _ => changes++,
                    options: new Segmented.SegmentedOptions { AutoSelection = true, Parts = parts })] } });
            host.RunFrame();
            host.RunFrame();
            bool initializedQuietly = selected.Value == 0 && changes == 0;
            var items = Roles(host.Scene, AutomationRole.RadioButton);
            var item0 = host.Scene.AbsoluteRect(items[0]);
            var pill0 = host.Scene.AbsoluteRect(pill);
            bool initialPillCentered = Near(pill0.X + pill0.W * 0.5f, item0.X + item0.W * 0.5f, 0.5f);
            ClickNode(host, window, items[2]);
            bool userWrote = selected.Value == 2 && changes == 1;
            host.RunFrame();
            var item2 = host.Scene.AbsoluteRect(items[2]);
            var pill2 = host.Scene.AbsoluteRect(pill);
            bool movedPillCentered = Near(pill2.X + pill2.W * 0.5f, item2.X + item2.W * 0.5f, 0.5f);
            selected.Value = 1;
            host.RunFrame();
            bool programmaticQuiet = selected.Value == 1 && changes == 1;
            Check("gate.ctl.bind.segmented-options Segmented.Create(items, signal, onChange, options): auto-select is quiet; user writes then notifies; programmatic write has no echo; indicator remains centered",
                initializedQuietly && userWrote && programmaticQuiet && initialPillCentered && movedPillCentered,
                $"init={initializedQuietly} selected={selected.Value} changes={changes} centered={initialPillCentered}/{movedPillCentered}");
        }

        // gate.ctl.bind.naming — the closed callback-name set is enforced: NO public control factory (Create/Group)
        // parameter is named onToggle/onSelect/onTextChanged/OnValueChanged (the eliminated Action<TOld,TNew>/idiom
        // spellings). A reflection scan over the whole FluentGpu.Controls surface (comprehensive — catches any control,
        // migrated or not). Param names are present under JIT (the gate run); under AOT trimming they degrade to a
        // vacuous pass, never a false failure.
        {
            string[] banned = { "ontoggle", "onselect", "ontextchanged", "onvaluechanged" };
            var offenders = new List<string>();
            foreach (var t in typeof(ToggleSwitch).Assembly.GetExportedTypes())
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "Create" && m.Name != "Group") continue;
                    foreach (var pi in m.GetParameters())
                        if (Array.IndexOf(banned, (pi.Name ?? "").ToLowerInvariant()) >= 0)
                            offenders.Add($"{t.Name}.{m.Name}({pi.Name})");
                }
            Check("gate.ctl.bind.naming no public control factory parameter named onToggle/onSelect/onTextChanged/OnValueChanged remains",
                offenders.Count == 0, offenders.Count == 0 ? "clean" : string.Join(", ", offenders));
        }

        // gate.ctl.bind.textbox-options — TextBox is built via the TextBoxOptions record (the long tail) + a controlled
        // value signal; a user edit round-trips text THROUGH the signal and fires onChange; the mount-time signal seed
        // does NOT fire onChange (onChange is an edit callback, not a re-push echo).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-tb", new Size2(420, 240), 1f)); window.Show();
            var text = new Signal<string>("");
            int changes = 0; string last = "";
            using var host = new AppHost(app, window, device, fonts, strings,
                new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(12),
                    Children = [TextBox.Create(text, onChange: s => { changes++; last = s; },
                        new TextBox.TextBoxOptions { Placeholder = "ph", Width = 200f, Header = "H" })] } });
            host.RunFrame();
            bool mountQuiet = changes == 0;   // the mount-time seed sync does not fire onChange
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
            ClickNode(host, window, field);
            foreach (char c in "hi") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            bool userWrote = text.Value == "hi" && changes >= 1 && last == "hi";   // user edit → signal round-trip + onChange
            Check("gate.ctl.bind.textbox-options TextBox via TextBoxOptions round-trips text through the signal; onChange fires on user edits (not the mount seed)",
                mountQuiet && userWrote, $"mountQuiet={mountQuiet} text='{text.Peek()}' changes={changes} last='{last}'");
        }
    }

    static void ControlsChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ctl", new Size2(480, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new ControlsProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        NodeHandle Kid(int i) => Child(host.Scene, host.Scene.Root, i);
        void Press(NodeHandle n, float lx, float ly)
        {
            var r = host.Scene.AbsoluteRect(n);
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(r.X + lx, r.Y + ly), 0, 0));
        }

        // slider: press at x=100/200 → 0.5
        Press(Kid(0), 100f, 12f);
        host.RunFrame();
        bool press = Near(probe.SliderVal, 0.5f);
        // drag to x=160 → 0.8 (the drag target survives the in-place re-render)
        var sr = host.Scene.AbsoluteRect(Kid(0));
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(sr.X + 160f, sr.Y + 12f), 0, 0));
        host.RunFrame();
        bool drag = Near(probe.SliderVal, 0.8f);
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(sr.X + 160f, sr.Y + 12f), 0, 0));
        host.RunFrame();
        Check("47. Slider press-sets + drag-scrubs value", press && drag, $"press={probe.SliderVal:0.0} (0.5→0.8)");

        // 47t. The SAME slider, by TOUCH: a touch press-sets and the touch drag scrubs through the shared OnDrag
        // implicit-capture (_dragTarget) — proof that the dispatcher's single-recognizer touch path honors an OnDrag node
        // exactly like the mouse, so the editor's drag-select (W0e.t4) and the slider scrub ride the one mechanism. Press
        // x=60 → 0.3, drag x=140 → 0.7 (distinct from the mouse 0.8 above, so this is the touch path doing the work).
        var st = host.Scene.AbsoluteRect(Kid(0));
        uint tms = s_touchClockMs;
        window.QueueInput(Touch(InputKind.PointerDown, new Point2(st.X + 60f, st.Y + 12f), tms, 1));
        host.RunFrame();
        bool touchPress = Near(probe.SliderVal, 0.3f);
        for (int i = 1; i <= 8; i++)
        {
            window.QueueInput(Touch(InputKind.PointerMove, new Point2(st.X + 60f + i * 10f, st.Y + 12f), tms + (uint)i * 16, 1));
            host.RunFrame();
        }
        bool touchDrag = Near(probe.SliderVal, 0.7f);
        window.QueueInput(Touch(InputKind.PointerUp, new Point2(st.X + 140f, st.Y + 12f), tms + 9 * 16, 1));
        host.RunFrame();
        s_touchClockMs = tms + 1000;
        Check("47t. Slider press-sets + drag-scrubs by TOUCH (the shared OnDrag implicit-capture the editor drag-select also rides)",
            touchPress && touchDrag, $"press={probe.SliderVal:0.0} (0.3→0.7)");

        // toggle: click flips on
        var tr = host.Scene.AbsoluteRect(Kid(1));
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(tr.X + tr.W / 2, tr.Y + tr.H / 2), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(tr.X + tr.W / 2, tr.Y + tr.H / 2), 0, 0));
        host.RunFrame();
        bool toggled = probe.Toggled;

        // icon button: click increments
        var ir = host.Scene.AbsoluteRect(Kid(2));
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ir.X + ir.W / 2, ir.Y + ir.H / 2), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ir.X + ir.W / 2, ir.Y + ir.H / 2), 0, 0));
        host.RunFrame();
        bool iconClicked = probe.IconClicks == 1;

        // scrollbar: drag the thumb to ~bottom → position near 1
        Press(Kid(3), 5f, 190f);
        host.RunFrame();
        bool scrolled = probe.ScrollPos > 0.5f;

        Check("48. Toggle flips, IconButton clicks, ScrollBar drags", toggled && iconClicked && scrolled,
            $"toggled={probe.Toggled} icon={probe.IconClicks} scrollPos={probe.ScrollPos:0.0}");
    }

    static void AutoFitTextChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("autofit", new Size2(320, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new AutoFitProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        float longFit    = host.Scene.AbsoluteRect(probe.LongFit).H;
        float longFixed  = host.Scene.AbsoluteRect(probe.LongFixed).H;
        float shortFit   = host.Scene.AbsoluteRect(probe.ShortFit).H;
        float shortFixed = host.Scene.AbsoluteRect(probe.ShortFixed).H;

        bool longShrank = longFit > 0f && longFit < longFixed - 1f;   // shrank to fit → shorter than the capped 40px run
        bool shortKept  = Near(shortFit, shortFixed, 1f);             // already fits at the authored size → no shrink

        Check("AF1. TextEl auto-fit (MinSize): a long title shrinks to fit MaxLines; a short title is unchanged",
            longShrank && shortKept,
            $"longFit={longFit:0.#} < longFixed={longFixed:0.#} | shortFit={shortFit:0.#} ~= shortFixed={shortFixed:0.#}");
    }

    static void FontFamilyChecks(StringTable strings)
    {
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(
            new BoxEl { Direction = 1, Children = [new TextEl("hi") { FontFamily = "Segoe Fluent Icons" }, new TextEl("plain")] }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        bool hiFam = false, plainEmpty = false;
        foreach (var g in dev.LastGlyphs)
        {
            string t = strings.Resolve(g.Text), fam = strings.Resolve(g.Family);
            if (t == "hi") hiFam = fam == "Segoe Fluent Icons";
            if (t == "plain") plainEmpty = fam.Length == 0;
        }
        var icon = Ui.Icon(Icons.Play, 20f);
        bool iconFactory = icon.FontFamily == Theme.IconFont && icon.Text.Value == Icons.Play && icon.Size == 20f;
        Check("56. per-run font family threads to the glyph cmd; Ui.Icon uses the icon font", hiFam && plainEmpty && iconFactory,
            $"hiFam={hiFam} plainEmpty={plainEmpty} iconFont='{icon.FontFamily}'");
    }

    static void GradientBorderChecks(StringTable strings)
    {
        var brush = Ui.LinearGradient(90f,
            new GradientStop(0.33f, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x18)),
            new GradientStop(1f, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x12)));
        var scene = LayoutTree(strings, new BoxEl
        {
            Width = 120, Height = 40, Corners = CornerRadius4.All(4f), Fill = ColorF.FromRgba(20, 20, 20),
            BorderBrush = brush, BorderWidth = 1f,
        });
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));

        bool oneStroke = dev.LastGradientStrokes.Count == 1;
        var gs = oneStroke ? dev.LastGradientStrokes[0] : default;
        bool band = oneStroke && Near(gs.StrokeWidth, 1f) && gs.StopCount == 2
            && Near(gs.Rect.X, 0.5f) && Near(gs.Rect.W, 119f);          // ring inset by bw/2, width = bounds − bw
        bool solidFill = dev.LastRects.Count == 1;                      // the fill drew once (full bounds), border is the gradient stroke
        Check("57. gradient elevation border emits a DrawGradientStroke band", oneStroke && band && solidFill,
            $"strokes={dev.LastGradientStrokes.Count} w={gs.StrokeWidth:0.0} stops={gs.StopCount} ring=({gs.Rect.X:0.0},{gs.Rect.W:0.0})");

        var fillScene = LayoutTree(strings, new BoxEl
        {
            Width = 120, Height = 40, Corners = CornerRadius4.All(4f),
            Gradient = Ui.LinearGradient(0f, new GradientStop(0f, ColorF.FromRgba(0, 0, 0)), new GradientStop(1f, ColorF.FromRgba(255, 255, 255))),
        });
        dl.Reset();
        SceneRecorder.Record(fillScene, dl);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        bool oneFill = dev.LastGradients.Count == 1 && dev.LastGradients[0].StopCount == 2 && dev.LastRects.Count == 0;
        Check("57b. gradient-only BoxEl emits a DrawGradientRect", oneFill,
            $"gradients={dev.LastGradients.Count} rects={dev.LastRects.Count}");

        var chromeScene = LayoutTree(strings, new BoxEl
        {
            Width = 120,
            Height = 72,
            Corners = Radii.OverlayAll,
            Fill = ColorF.FromRgba(0x20, 0x20, 0x20),
            BorderColor = ColorF.FromRgba(0x80, 0x80, 0x80),
            BorderWidth = 1f,
            Children =
            [
                new BoxEl
                {
                    Width = 120,
                    Height = 36,
                    Fill = ColorF.FromRgba(0x40, 0x40, 0x40),
                },
            ],
        });
        dl.Reset();
        SceneRecorder.Record(chromeScene, dl);
        int firstFill = -1, secondFill = -1, stroke = -1, opIndex = 0, pos = 0;
        var bytes = dl.Bytes;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos));
            pos += sizeof(int);
            if (op == DrawOp.FillRoundRect)
            {
                if (firstFill < 0) firstFill = opIndex;
                else if (secondFill < 0) secondFill = opIndex;
            }
            else if (op == DrawOp.DrawRoundRectStroke && stroke < 0)
            {
                stroke = opIndex;
            }
            pos += DrawPayloadSize(op);
            opIndex++;
        }
        bool chromeOrder = firstFill >= 0 && secondFill > firstFill && stroke > secondFill;
        Check("57c. BoxEl chrome order: parent border records after descendant fills",
            chromeOrder, $"parentFill={firstFill} childFill={secondFill} stroke={stroke}");

        // 57e — WinUI MappingMode=Absolute (ControlElevationBorderBrush EndPoint 0,3): AxisLengthPx squeezes the stop
        // ramp into 3 physical px of the 40px axis (offsets ×3/40); AnchorEnd (the light/accent ScaleY=-1 mirror)
        // measures the band from the BOTTOM, reversing the stop order so offsets stay ascending.
        {
            var sec = ColorF.FromRgba(0x10, 0x10, 0x10);
            var def = ColorF.FromRgba(0xF0, 0xF0, 0xF0);
            var absBand = new GradientSpec(GradientShape.Linear, 90f,
                [new GradientStop(0.33f, sec), new GradientStop(1f, def)]) { AxisLengthPx = 3f };
            var topScene = LayoutTree(strings, new BoxEl
            {
                Width = 120, Height = 40, Corners = CornerRadius4.All(4f), BorderBrush = absBand, BorderWidth = 1f,
            });
            var dl2 = new DrawList();
            SceneRecorder.Record(topScene, dl2);
            var dev2 = new HeadlessGpuDevice();
            dev2.SubmitDrawList(dl2.Bytes, dl2.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
            var g1 = dev2.LastGradientStrokes[0];
            float k = 3f / 40f;
            bool topBand = Near(g1.O0, 0.33f * k) && Near(g1.O1, k) && g1.C0 == sec && g1.C1 == def;

            var bottomScene = LayoutTree(strings, new BoxEl
            {
                Width = 120, Height = 40, Corners = CornerRadius4.All(4f),
                BorderBrush = absBand with { AnchorEnd = true }, BorderWidth = 1f,
            });
            var dl3 = new DrawList();
            SceneRecorder.Record(bottomScene, dl3);
            var dev3 = new HeadlessGpuDevice();
            dev3.SubmitDrawList(dl3.Bytes, dl3.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
            var g2 = dev3.LastGradientStrokes[0];
            bool bottomBand = Near(g2.O0, 1f - k) && Near(g2.O1, 1f - 0.33f * k) && g2.C0 == def && g2.C1 == sec;

            Check("57e. absolute-axis elevation band: 3px ramp at the top; AnchorEnd mirrors it to the bottom (stops reversed)",
                topBand && bottomBand,
                $"top=({g1.O0:0.0000},{g1.O1:0.0000} secFirst={g1.C0 == sec}) bottom=({g2.O0:0.0000},{g2.O1:0.0000} defFirst={g2.C0 == def})");
        }
    }

    static void PolylineStrokeChecks(StringTable strings)
    {
        var scene = LayoutTree(strings, new PolylineStrokeEl
        {
            Width = 24,
            Height = 24,
            P0 = new Point2(2, 12),
            P1 = new Point2(10, 20),
            P2 = new Point2(22, 4),
            PointCount = 3,
            Color = ColorF.FromRgba(255, 255, 255),
            Thickness = 2f,
            TrimEnd = 0.5f,
        });
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool staticStroke = dev.LastPolylines.Count == 1 && Near(dev.LastPolylines[0].TrimEnd, 0.5f, 0.001f)
            && dev.LastPolylines[0].PointCount == 3;

        var anim = new AnimEngine(scene);
        anim.Keyframes(scene.Root, AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))], 100f);
        anim.Tick(0f);
        float t0 = scene.Paint(scene.Root).StrokeTrimEnd;
        anim.Tick(16f);
        float t16 = scene.Paint(scene.Root).StrokeTrimEnd;
        dl.Reset();
        SceneRecorder.Record(scene, dl);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool animatedStroke = Near(t0, 0f, 0.001f) && t16 > 0f && t16 < 0.35f
            && dev.LastPolylines.Count == 1 && Near(dev.LastPolylines[0].TrimEnd, t16, 0.001f);
        Check("57d. PolylineStroke emits DrawPolylineStroke and supports animated trim-end",
            staticStroke && animatedStroke, $"static={staticStroke} t0={t0:0.00} t16={t16:0.00} cmds={dev.LastPolylines.Count}");
    }

    static void RepeatButtonChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("repeat", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new RepeatProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var btn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
        var center = CenterOf(host.Scene, btn);

        // Press and HOLD (no up): the ticker fires once immediately, then repeats while held.
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        host.RunFrame();
        int afterPress = root.Clicks;                     // 1 (fired on arm)
        bool activeHeld = host.HasActiveWork;             // frames keep flowing while held
        for (int i = 0; i < 45; i++) host.RunFrame();     // ~720ms: past the 500ms initial delay + a few intervals
        int heldClicks = root.Clicks;

        // Release: the repeat stops; clicks no longer grow (no busy loop).
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        int atRelease = root.Clicks;
        for (int i = 0; i < 10; i++) host.RunFrame();
        int afterRelease = root.Clicks;

        Check("62. RepeatButton: press fires once, holds repeat, release stops",
            afterPress == 1 && activeHeld && heldClicks >= 2 && afterRelease == atRelease,
            $"press={afterPress} held={heldClicks} release={atRelease}→{afterRelease}");
        Check("62a. RepeatButton: idle after release does no work (no busy loop)", !host.HasActiveWork);
    }

    static void Wave2ControlChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("wave2", new Size2(320, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new ButtonProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var buttons = Roles(host.Scene, AutomationRole.Button);
        var enabledBtn = buttons[0];
        var disabledBtn = buttons[1];
        var restFg = GlyphColor(device, strings, "enabled-btn");   // resting foreground (ButtonForeground = TextPrimary)

        // W2.a the disabled Button swallows the click (the control now sets IsEnabled=false instead of nulling handlers).
        ClickNode(host, window, disabledBtn);
        int afterDisabledClick = root.Clicks;
        ClickNode(host, window, enabledBtn);
        Check("W2.a Button adopts the IsEnabled gate (disabled swallows click; enabled clicks)",
            afterDisabledClick == 0 && root.Clicks == 1, $"disabledClicks={afterDisabledClick} enabled={root.Clicks}");

        // W2.b the disabled Button's label resolves ButtonForegroundDisabled — matched on FULL ARGB (WinUI dims via ALPHA),
        // and proven actually dimmer than the resting foreground (not just the same white RGB).
        var disFg = GlyphColor(device, strings, "disabled-btn");
        var disExpect = Tok.TextDisabled;
        bool disMatchesToken = ColorClose(disFg, disExpect, 0.03f);
        bool disActuallyDimmer = disFg.A < restFg.A - 0.05f;
        Check("W2.b disabled Button label = DisabledForeground (ARGB) and is dimmer than resting",
            disMatchesToken && disActuallyDimmer,
            $"label=({disFg.R:0.00},{disFg.G:0.00},{disFg.B:0.00},A={disFg.A:0.00}) token A={disExpect.A:0.00} restA={restFg.A:0.00}");

        // W2.c pressing the enabled Button ramps its label to ButtonForegroundPressed (TextSecondary) — full ARGB, and
        // the alpha actually changed from resting (the WinUI press dim).
        var c = CenterOf(host.Scene, enabledBtn);
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        for (int i = 0; i < 20; i++) host.RunFrame();
        var pressFg = GlyphColor(device, strings, "enabled-btn");
        var pressExpect = Tok.TextSecondary;
        bool pressMatchesToken = ColorClose(pressFg, pressExpect, 0.06f);
        bool pressChangedFromRest = MathF.Abs(pressFg.A - restFg.A) > 0.02f || !ColorClose(pressFg, restFg, 0.02f);
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        host.RunFrame();
        Check("W2.c pressed Button label ramps to PressedForeground (ARGB, changed from resting)",
            pressMatchesToken && pressChangedFromRest,
            $"label=({pressFg.R:0.00},{pressFg.G:0.00},{pressFg.B:0.00},A={pressFg.A:0.00}) pressTokenA={pressExpect.A:0.00} restA={restFg.A:0.00}");

        // W2.d HyperlinkButton uses the accent TEXT palette (AccentTextPrimary), NOT the accent FILL (AccentDefault),
        // and that foreground tracks a live accent override (OS accent / Tok.SetAccent) by recomputing its shade.
        ColorF LinkForeground(string id)
        {
            using var a = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc(id, new Size2(240, 120), 1f)); w.Show();
            var dev = new HeadlessGpuDevice();
            using var h = new AppHost(a, w, dev, new HeadlessFontSystem(strings), strings, new HyperlinkProbe());
            h.RunFrame();
            return GlyphColor(dev, strings, "link-text");
        }

        var defFg = LinkForeground("hlink");
        bool usesAccentText = ColorClose(defFg, Tok.AccentTextPrimary, 0.02f) && !ColorClose(defFg, Tok.AccentDefault, 0.02f);

        Tok.SetAccent(ColorF.FromRgba(0xE0, 0x40, 0x40));   // developer/OS override (red)
        var ovFg = LinkForeground("hlink2");
        Tok.SetAccent(null);                                 // clear the override (revert to theme default)
        bool tracksOverride = !ColorClose(ovFg, defFg, 0.05f) && ovFg.R > ovFg.B + 0.1f;   // now reddish, changed

        Check("W2.d HyperlinkButton foreground = accent TEXT (not fill) and tracks the accent override",
            usesAccentText && tracksOverride,
            $"def=({defFg.R:0.00},{defFg.G:0.00},{defFg.B:0.00}) override=({ovFg.R:0.00},{ovFg.G:0.00},{ovFg.B:0.00}) accentFill=({Tok.AccentDefault.R:0.00},{Tok.AccentDefault.G:0.00},{Tok.AccentDefault.B:0.00})");
    }

    static void ExpanderSettingsChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("expander", new Size2(360, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new Expander { Header = "Section", Content = new TextEl("expander-body") { Size = 14f }, InitiallyExpanded = false };
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // collapsed (chevron rotation seeded to 0° → identity)
        var chevron0 = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
        float m11Collapsed = host.Scene.Paint(chevron0).LocalTransform.M11;
        bool noContent = Child(host.Scene, Child(host.Scene, host.Scene.Root, 1), 0).IsNull;   // clip mounted, panel not

        // Toggle open. (a) The chevron rotation TWEENS (167ms): track peak sin θ — a tween passes through a mid-angle
        // (sin θ → ~1 near 90°), an instant snap never leaves ~0. (b) The content panel SLIDES out from under the
        // header: the clip wrapper's SizeMode.Reflow Trailing anchor keeps the panel's bottom edge on the reveal edge
        // (ChildShiftY < 0 mid-flight, 0 at rest) — an instant appear would read 0 every frame.
        ClickNode(host, window, Child(host.Scene, host.Scene.Root, 0));
        float peakSin = 0f, minShift = 0f;
        for (int i = 0; i < 16; i++)
        {
            host.RunFrame();
            var ch = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
            peakSin = MathF.Max(peakSin, MathF.Abs(host.Scene.Paint(ch).LocalTransform.M12));
            var clipW = Child(host.Scene, host.Scene.Root, 1);   // the reflow clip wrapper carries the child-shift
            if (!clipW.IsNull) minShift = MathF.Min(minShift, host.Scene.Paint(clipW).ChildShiftY);
        }
        bool rotating = peakSin > 0.5f;
        bool contentSlidIn = minShift < -4f;

        for (int i = 0; i < 16; i++) host.RunFrame();   // settle
        var chevronDone = Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 1);
        float m11Done = host.Scene.Paint(chevronDone).LocalTransform.M11;   // cos 180° ≈ -1
        bool settled = m11Done < -0.9f;
        bool hasContent = !Child(host.Scene, Child(host.Scene, host.Scene.Root, 1), 0).IsNull && HasGlyph(device, strings, "expander-body");

        Check("W1-P3.a Expander animates chevron rotation + content slide (mid-flight)",
            Near(m11Collapsed, 1f, 0.05f) && rotating && settled && contentSlidIn && hasContent,
            $"m11→{m11Done:0.00} peakSinθ={peakSin:0.00} minShift={minShift:0.0} content {!noContent}→{hasContent}");
    }

    // ── SettingsExpander: wide header content, and the ItemsHeader slot ───────────────────────────────────────────────
    // THE REGRESSION THESE PIN. A SettingsExpander built its header card without an Alignment, so wide Content landed in
    // SettingsCard's right-hand Auto grid track (BuildRightRow). Once that track is wider than the card, FlexLayout's
    // ResolveColumns overflow guard drives the header's Star track toward zero and ArrangeGrid places both cells at the
    // SAME x — and a text run whose budget collapses neither wraps nor clips (TextLayoutEngine disables wrapping at
    // maxWidth <= 1 and reports Width 0), so the header painted straight across its own content. Wavee's Settings →
    // General sidebar group shipped that way for one release.
    static void SettingsExpanderWideContentChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("settings-expander-wide", new Size2(900, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        // 600 DIP of un-shrinkable content — wider than the header card can spare beside a header at this window width.
        static Element WideContent(string label) => new BoxEl
        {
            Width = 600f, Height = 40f, Shrink = 0f,
            Children = [new TextEl(label) { Size = 14f }],
        };

        var root = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1, Gap = 8f, Padding = Edges4.All(16f),
                Children =
                [
                    SettingsExpander.Create(new SettingsExpander.Options
                    {
                        Header = "WideHeader",
                        Description = "WideDescription",
                        Content = WideContent("WideSlot"),
                        // The fix: wide content stacks UNDER the header text instead of racing it for the same row.
                        Alignment = SettingsCard.ContentAlignment.Vertical,
                    }),
                    SettingsExpander.Create(new SettingsExpander.Options
                    {
                        Header = "PanelHost",
                        InitiallyExpanded = true,
                        ItemsHeader = new BoxEl { Padding = Edges4.All(8f), Children = [new TextEl("PanelSlot") { Size = 14f }] },
                        Items = [SettingsExpander.Item("ItemRow", null)],
                    }),
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);
        // The body mounts a frame after the reveal is declared, and the 333ms reflow crops it until it settles — an
        // un-settled clip culls the body's glyphs entirely, so let the expansion finish before reading them.
        for (int i = 0; i < 24; i++) host.RunFrame();

        // Glyph runs carry their placement, so "did the header paint over its content?" is answerable directly rather
        // than by walking the card's internal template. Bounds is the run's LOCAL box; Transform.Dx/Dy place it.
        RectF Run(string text)
        {
            foreach (var g in device.LastGlyphs)
                if (strings.Resolve(g.Text) == text)
                    return new RectF(g.Transform.Dx + g.Bounds.X, g.Transform.Dy + g.Bounds.Y, g.Bounds.W, g.Bounds.H);
            return default;
        }

        var hdr = Run("WideHeader");
        var dsc = Run("WideDescription");
        var slot = Run("WideSlot");
        // Stacked, not overlapped: the content starts at or below the description's baseline row.
        bool stacked = hdr.H > 0f && slot.H > 0f && slot.Y >= dsc.Y + dsc.H - 0.5f;
        // …and the header text kept a real width. The defect starved it to a 0-DIP cell while the glyphs still painted.
        bool headerNotStarved = hdr.W > 40f && dsc.W > 40f;
        Check("cp3.g SettingsExpander(Alignment=Vertical): 600-DIP header content STACKS under the header text — neither overlaps nor starves it",
            stacked && headerNotStarved,
            $"hdr=({hdr.X:0},{hdr.Y:0}) {hdr.W:0}x{hdr.H:0} dsc.bottom={dsc.Y + dsc.H:0} slot.y={slot.Y:0}");

        var panel = Run("PanelSlot");
        var itemRow = Run("ItemRow");
        Check("cp3.h SettingsExpander.ItemsHeader renders ABOVE the first Items row inside the revealed body",
            panel.H > 0f && itemRow.H > 0f && panel.Y + panel.H <= itemRow.Y + 0.5f,
            $"panel.bottom={panel.Y + panel.H:0} item.y={itemRow.Y:0}");
    }

    // ── RadioButtons as a preview-card picker ────────────────────────────────────────────────────────────────────────
    // The two additions that let the WinUI RadioButtons container host a strip of preview CARDS (Wavee's row-density /
    // page-layout / palette / sidebar-design pickers, which were four hand-rolled bags of independent tab stops):
    // Style.ShowGlyph = false drops the ring column so the card itself states the selection, and PartGrid/PartColumn
    // expose the items grid so a fixed-width strip wraps instead of overflowing. Both are deliberate WinUI divergences.
    static void CardPickerRadioGroupChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var bare = RadioButton.DefaultStyle with { ShowGlyph = false, MinWidth = 0f, MinHeight = 0f, ContentGap = 0f };

        static void CollectRadios(SceneStore s, NodeHandle n, List<NodeHandle> into)
        {
            if (n.IsNull) return;
            if (s.Interaction(n).Role == AutomationRole.RadioButton) into.Add(n);
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) CollectRadios(s, c, into);
        }

        // cp3.i — the glyph column is not merely hidden, it is NOT BUILT. Zeroing the ring through PartRing could not
        // achieve this: RadioButton.Build re-asserts the ring's Children after the modifier, so the dot would stay
        // mounted inside a collapsed ellipse.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("radio-bare", new Size2(480, 320), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Gap = 16f,
                    Children =
                    [
                        RadioButtons.Create(2, i => new TextEl("bare" + i) { Size = 14f },
                            new Signal<int>(0), maxColumns: 2, style: bare),
                        RadioButtons.Create(2, i => new TextEl("ringed" + i) { Size = 14f },
                            new Signal<int>(0), maxColumns: 2),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var radios = new List<NodeHandle>();
            CollectRadios(host.Scene, host.Scene.Root, radios);
            // First two are the bare strip's items, last two the default-styled strip's.
            bool enough = radios.Count == 4;
            bool bareHasContentOnly = enough && host.Scene.ChildCount(radios[0]) == 1 && host.Scene.ChildCount(radios[1]) == 1;
            bool ringedHasGlyph = enough && host.Scene.ChildCount(radios[2]) == 2 && host.Scene.ChildCount(radios[3]) == 2;
            bool stillARadio = enough && host.Scene.Interaction(radios[0]).Role == AutomationRole.RadioButton;
            Check("cp3.i RadioButton(ShowGlyph=false): the ring/dot column is not built — the item mounts CONTENT ONLY, and stays a radio",
                bareHasContentOnly && ringedHasGlyph && stillARadio,
                $"items={radios.Count} bare kids={(enough ? host.Scene.ChildCount(radios[0]) : -1)} ringed kids={(enough ? host.Scene.ChildCount(radios[2]) : -1)}");
        }

        // cp3.j — PartGrid wrap: four 200-DIP cards in a window that fits two per line reflow to TWO rows, and the
        // container keyboard contract survives the wrap (one tab stop in, Right/Right roves two items forward with
        // selection following focus).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("radio-wrap", new Size2(500, 400), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var sel = new Signal<int>(0);
            var parts = new TemplateParts
            {
                [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = 12f },
                [RadioButtons.PartColumn] = c => c with { Shrink = 0f },
            };
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Padding = Edges4.All(16f),
                    Children =
                    [
                        RadioButtons.Create(4,
                            i => new BoxEl { Width = 200f, Height = 60f, Shrink = 0f, Children = [new TextEl("card" + i) { Size = 12f }] },
                            sel, maxColumns: 4, style: bare, parts: parts),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var radios = new List<NodeHandle>();
            CollectRadios(host.Scene, host.Scene.Root, radios);
            bool four = radios.Count == 4;
            var r0 = four ? host.Scene.AbsoluteRect(radios[0]) : default;
            var r1 = four ? host.Scene.AbsoluteRect(radios[1]) : default;
            var r2 = four ? host.Scene.AbsoluteRect(radios[2]) : default;
            bool wrapped = four && Near(r0.Y, r1.Y, 1f) && r2.Y > r0.Y + r0.H - 1f && r2.X < r1.X;

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame();
            int afterOne = sel.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame();
            int afterTwo = sel.Peek();

            Check("cp3.j RadioButtons(PartGrid wrap): a 4x200 card strip reflows to two rows and keeps the roving arrow contract (selection follows focus)",
                wrapped && afterOne == 1 && afterTwo == 2,
                $"rows y={r0.Y:0}/{r1.Y:0}/{r2.Y:0} sel 0->{afterOne}->{afterTwo}");
        }
    }

    static void InputVocabularyChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // E2.a — Shift+Tab walks focus backward.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10,
                Children =
                [
                    new BoxEl { Key = "A", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "B", Width = 20, Height = 20, OnClick = () => { } },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var a = Child(scene, scene.Root, 0); var b = Child(scene, scene.Root, 1);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
            var f1 = disp.Focused;   // A
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab, Mods: KeyModifiers.Shift) });
            var f2 = disp.Focused;   // wraps back to B
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab, Mods: KeyModifiers.Shift) });
            var f3 = disp.Focused;   // back to A
            Check("E2.a Shift+Tab walks focus backward (wraps)", f1 == a && f2 == b && f3 == a,
                $"f1=A?{f1 == a} f2=B?{f2 == b} f3=A?{f3 == a}");
        }

        // E2.b/c/d — WinUI ButtonBase activation (ButtonBaseKeyProcess.h): Space/Enter key-DOWN arms the pressed
        // visual (held-key repeats ignored — ONE activation per hold); the click fires on key-UP (ClickMode.Release,
        // ButtonBase_Partial.cpp:475-483); Escape or ANY other key while held cancels without firing (:64-70).
        {
            var scene = new SceneStore();
            int clicks = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.MoveFocus(forward: true);
            var node = disp.Focused;

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
            bool enterArms = clicks == 0 && (scene.Flags(node) & NodeFlags.Pressed) != 0;   // pressed, no click yet
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter, IsRepeat: true) });
            bool heldOnce = clicks == 0;                                                    // held Enter never re-fires
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });
            bool enterUp = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) == 0;     // click on the UP edge

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            bool armedNoFire = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool firedOnUp = clicks == 2 && (scene.Flags(node) & NodeFlags.Pressed) == 0;
            Check("E2.b Space/Enter arm pressed on key-down, click on key-up; a held key activates exactly once",
                enterArms && heldOnce && enterUp && armedNoFire && firedOnUp,
                $"enterArm={enterArms} held={heldOnce} enterUp={enterUp} spaceArm={armedNoFire} spaceUp={firedOnUp}");

            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            Check("E2.c Escape cancels a held Space without firing",
                clicks == 2 && (scene.Flags(node) & NodeFlags.Pressed) == 0, $"clicks={clicks}");

            // ANY other key while held also cancels without firing (ButtonBaseKeyProcess.h:64-70) — the press visual
            // clears on the foreign key-down and the eventual Space-up does nothing.
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            bool reArmed = (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.A) });
            bool canceledByKey = (scene.Flags(node) & NodeFlags.Pressed) == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            Check("E2.c2 any other key-down cancels a held Space/Enter press without firing",
                reArmed && canceledByKey && clicks == 2, $"armed={reArmed} canceled={canceledByKey} clicks={clicks}");
        }

        // E2.e — double/triple-click promotion (timestamps + slop) surfaces in OnPointerPressed.ClickCount.
        {
            var scene = new SceneStore();
            var counts = new List<byte>();
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 60, Height = 30, OnPointerPressed = e => counts.Add(e.ClickCount) }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var p = new Point2(10, 10);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1000) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1040) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1100) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1140) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1200) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1240) });
            // A press 600ms later resets to 1; so does a press 5px away.
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 2400) });
            Check("E2.e click count promotes 1→2→3 inside the window and resets after it",
                counts.Count == 4 && counts[0] == 1 && counts[1] == 2 && counts[2] == 3 && counts[3] == 1,
                string.Join(",", counts));
        }

        // E2.n — ONE RELEASE, ONE OWNER (input-a11y.md §6.5). A nested CLICK-only child owns the gesture outright, so an
        // ancestor's OnPointerReleased must not also fire. Regression gate for the chevron class: the release walk used to
        // test PressedBit alone, which made an OnClick-only child look inert and let the release sail past it to the row —
        // two owners for one gesture, so double-clicking a track row's expand chevron toggled the drawer AND played the
        // track (the row read ClickCount 2 as its double-tap-to-invoke). The second half of the check is the regression
        // guard in the other direction: the row's OWN double-click must still arrive.
        {
            var scene = new SceneStore();
            int childClicks = 0;
            var rowReleases = new List<byte>();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1, Width = 200f, Height = 100f,
                OnPointerReleased = e => rowReleases.Add(e.ClickCount),
                Children = [new BoxEl { Width = 40f, Height = 40f, OnClick = () => childClicks++ }],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            void Click(Point2 at, uint ms)
            {
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, at, 0, 0, TimestampMs: ms) });
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, at, 0, 0, TimestampMs: ms + 40) });
            }
            var onChild = new Point2(20f, 20f);
            var onRow = new Point2(120f, 80f);
            Click(onChild, 1000); Click(onChild, 1100);   // double-click the nested affordance
            bool childOwned = childClicks == 2 && rowReleases.Count == 0;
            Click(onRow, 3000); Click(onRow, 3100);       // the row's own double-click is untouched
            bool rowUnaffected = rowReleases.Count == 2 && rowReleases[1] == 2 && childClicks == 2;
            Check("E2.n one release, one owner: a nested click-only child takes the release, the row's own double-click still lands",
                childOwned && rowUnaffected,
                $"childClicks={childClicks} rowReleases=[{string.Join(",", rowReleases)}]");
        }

        // E2.o — the click-count chain is per-OWNER, not per-position. Two presses inside DoubleClickMs *and* inside the
        // 4px slop but resolving to different gesture owners must both read 1: a node-agnostic counter promoted whichever
        // control the second press landed in, so a press pair straddling a row/child boundary read as a double-click on
        // the row. Owner-keyed (not hit-node-keyed) so a plate with inert children stays double-clickable.
        {
            var scene = new SceneStore();
            var childCounts = new List<byte>();
            var rowCounts = new List<byte>();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1, Width = 200f, Height = 100f,
                OnPointerPressed = e => rowCounts.Add(e.ClickCount),
                Children = [new BoxEl { Width = 200f, Height = 40f, OnPointerPressed = e => childCounts.Add(e.ClickCount) }],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            void Press(Point2 at, uint ms)
            {
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, at, 0, 0, TimestampMs: ms) });
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, at, 0, 0, TimestampMs: ms + 40) });
            }
            Press(new Point2(20f, 38f), 1000);   // the child (y < 40)
            Press(new Point2(20f, 42f), 1100);   // the row — 4px away, inside the slop, DIFFERENT owner
            Press(new Point2(20f, 42f), 1200);   // the row again — same owner, so this one promotes
            Check("E2.o click-count chains per gesture owner: crossing into a different owner inside the slop resets to 1",
                childCounts.Count == 1 && childCounts[0] == 1
                && rowCounts.Count == 2 && rowCounts[0] == 1 && rowCounts[1] == 2,
                $"child=[{string.Join(",", childCounts)}] row=[{string.Join(",", rowCounts)}]");
        }

        // E2.f/g — right-click release fires OnContextRequested (left never does); the Menu key (VK_APPS) fires it
        // on the focused node at its centre.
        {
            var scene = new SceneStore();
            int ctx = 0; Point2 ctxAt = default; ContextRequestTrigger ctxTrigger = default;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl
                {
                    Width = 60, Height = 30, OnClick = () => { },
                    OnContextRequested = e => { ctx++; ctxAt = e.Position; ctxTrigger = e.Trigger; },
                }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var p = new Point2(20, 10);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0) });
            bool leftSilent = ctx == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 1, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 1, 0) });
            bool rightFired = ctx == 1 && ctxTrigger == ContextRequestTrigger.Pointer
                && Near(ctxAt.X, 20) && Near(ctxAt.Y, 10);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Apps) });   // focused by the left click
            bool appsFired = ctx == 2 && ctxTrigger == ContextRequestTrigger.Keyboard
                && Near(ctxAt.X, 30) && Near(ctxAt.Y, 15);                                  // node centre
            Check("E2.f right-click release fires OnContextRequested (left stays silent)", leftSilent && rightFired,
                $"left={leftSilent} right={rightFired} at=({ctxAt.X:0.#},{ctxAt.Y:0.#})");
            Check("E2.g VK_APPS requests the context menu at the focused node's centre", appsFired,
                $"ctx={ctx} at=({ctxAt.X:0.#},{ctxAt.Y:0.#})");
        }

        // E2.m — BoxEl.ClickRequestsContext (input-a11y §6.5.1): the declarative "this button opens the ancestor's
        // context menu". (a) the prop reconciles to ClickBit|ClickRequestsContextBit with a NULL click-handler column
        // (+ the focusable implication); (b) a left click on the button fires the NEAREST OnContextRequested as an
        // Invoke request (Source = the button, Node = the row) and fires NO click; (c) toggling the prop off clears
        // bit 16 without stomping neighbor bits — the R1 regression guard for the HandlerMask ushort→uint widening
        // (every no-handler clear-site runs AFTER the bit is set in the same reconcile, so a single surviving
        // ushort-truncated `&=` would stomp bit 16 before (a) ever reads it).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            int ctx = 0, keys = 0;
            Point2 ctxAt = default; ContextRequestTrigger ctxTrigger = default;
            NodeHandle ctxNode = default, ctxSource = default;
            BoxEl Tree(bool crc) => new BoxEl
            {
                Key = "row", Width = 100, Height = 30, Direction = 0,
                OnContextRequested = e => { ctx++; ctxAt = e.Position; ctxTrigger = e.Trigger; ctxNode = e.Node; ctxSource = e.Source; },
                Children =
                [
                    new BoxEl { Key = "spacer", Width = 40, Height = 30 },
                    // OnKeyDown + Cursor ride along as NEIGHBOR bits (KeyBit, CursorBit) for the (c) stomp check.
                    new BoxEl { Key = "more", Width = 20, Height = 20, OnKeyDown = _ => keys++, Cursor = CursorId.Hand, ClickRequestsContext = crc },
                ],
            };
            var t1 = Tree(true);
            recon.ReconcileRoot(t1, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var row = scene.Root;
            var more = Child(scene, row, 1);
            ref readonly var ii1 = ref scene.Interaction(more);
            bool bitsSet = (ii1.HandlerMask & InteractionInfo.ClickBit) != 0
                        && (ii1.HandlerMask & InteractionInfo.ClickRequestsContextBit) != 0;
            bool nullClick = scene.GetClickHandler(more) is null;
            bool focusable = ii1.Focusable;
            Check("E2.m.a ClickRequestsContext reconciles to ClickBit|ClickRequestsContextBit with a null click handler (+ focusable)",
                bitsSet && nullClick && focusable, $"mask={ii1.HandlerMask:x} nullClick={nullClick} focusable={focusable}");

            var disp = new InputDispatcher(scene);
            var br = scene.AbsoluteRect(more);
            var pt = new Point2(br.X + br.W / 2f, br.Y + br.H / 2f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, pt, 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, pt, 0, 0) });
            bool invoked = ctx == 1 && ctxTrigger == ContextRequestTrigger.Invoke
                        && ctxNode == row && ctxSource == more
                        && Near(ctxAt.X, pt.X) && Near(ctxAt.Y, pt.Y);   // the button centre, row-local (row at origin)
            Check("E2.m.b a left click on the button raises the nearest OnContextRequested as Invoke (Source=button, Node=row), no click",
                invoked, $"ctx={ctx} trig={ctxTrigger} node={ctxNode.Raw.Index}/{row.Raw.Index} src={ctxSource.Raw.Index}/{more.Raw.Index} at=({ctxAt.X:0.#},{ctxAt.Y:0.#})");

            var t2 = Tree(false);
            recon.ReconcileRoot(t2, t1);
            ref readonly var ii2 = ref scene.Interaction(more);
            bool bit16Cleared = (ii2.HandlerMask & InteractionInfo.ClickRequestsContextBit) == 0
                             && (ii2.HandlerMask & InteractionInfo.ClickBit) == 0;   // no OnClick either → ClickBit clears too
            bool neighborsIntact = (ii2.HandlerMask & InteractionInfo.KeyBit) != 0
                                && (ii2.HandlerMask & InteractionInfo.CursorBit) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, pt, 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, pt, 0, 0) });
            bool inert = ctx == 1;   // the prop off → the click no longer re-enters the context funnel
            Check("E2.m.c toggling the prop off clears bit 16 without stomping neighbor bits (the uint HandlerMask R1 guard)",
                bit16Cleared && neighborsIntact && inert, $"mask={ii2.HandlerMask:x} inert={inert}");
        }

        // E2.h/i — a Ctrl+K accelerator and an Alt+S access-key chord invoke their owner from anywhere.
        {
            var scene = new SceneStore();
            int accel = 0, access = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 8,
                Children =
                [
                    new BoxEl { Key = "plain", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "accel", Width = 20, Height = 20, OnClick = () => accel++, Accelerator = new KeyAccelerator(Keys.K, KeyModifiers.Ctrl) },
                    new BoxEl { Key = "access", Width = 20, Height = 20, OnClick = () => access++, AccessKey = 'S' },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.MoveFocus(forward: true);   // focus the PLAIN node — accelerator must still find its owner
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.K, Mods: KeyModifiers.Ctrl) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.K) });   // bare K: no accelerator
            Check("E2.h Ctrl+K accelerator invokes its owner from anywhere (bare K does not)", accel == 1, $"accel={accel}");
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.S, Mods: KeyModifiers.Alt) });
            Check("E2.i Alt+S access-key chord invokes the mnemonic owner", access == 1, $"access={access}");
        }

        // E2.j — a pushed focus scope traps Tab inside its subtree until popped (dialog focus trap).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl { Key = "outside", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "dialog", Direction = 0, Gap = 4, Children = [
                        new BoxEl { Key = "d1", Width = 20, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "d2", Width = 20, Height = 20, OnClick = () => { } } ] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var outside = Child(scene, scene.Root, 0);
            var dialog = Child(scene, scene.Root, 1);
            var d1 = Child(scene, dialog, 0); var d2 = Child(scene, dialog, 1);
            disp.PushFocusScope(dialog);
            bool stays = true;
            disp.MoveFocus(forward: true);
            for (int i = 0; i < 4; i++)
            {
                disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
                stays &= disp.Focused == d1 || disp.Focused == d2;
            }
            disp.PopFocusScope();
            bool escapes = false;   // scope released → full-tree cycling reaches the outside node again
            for (int i = 0; i < 3 && !escapes; i++)
            {
                disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
                escapes = disp.Focused == outside;
            }
            Check("E2.j focus scope traps Tab inside the dialog subtree until popped", stays && escapes,
                $"trapped={stays} released={escapes}");
        }

        // E2.k — WindowBlur clears pressed/hover state and raises the host blur hook (light-dismiss trigger).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnClick = () => { } }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            bool blurred = false;
            disp.OnWindowBlur = () => blurred = true;
            var node = Child(scene, scene.Root, 0).IsNull ? scene.Root : scene.Root;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool pressedBefore = (scene.Flags(scene.Root) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.WindowBlur, default, 0, 0) });
            bool clearedAfter = (scene.Flags(scene.Root) & NodeFlags.Pressed) == 0;
            Check("E2.k WindowBlur clears pressed state and raises the blur hook",
                pressedBefore && clearedAfter && blurred, $"before={pressedBefore} after={clearedAfter} hook={blurred}");
        }

        // E2.l — hover resolves the cursor with WinUI semantics: clickability does NOT imply the hand (arrow unless an
        // element declares a cursor — WinUI sets the hand only on HyperlinkButton); an explicit cursor INHERITS down to
        // cursor-less descendants; and an explicit Arrow on a child MASKS an ancestor's I-beam (CursorBit stops the
        // walk — WinUI's forced SetCursor(MouseCursorArrow) on TextBox's delete button, TextBox_Partial.cpp:884).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10, Padding = Edges4.All(0),
                Children =
                [
                    new BoxEl { Key = "plain", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl
                    {
                        Key = "field", Direction = 0, Gap = 10, Cursor = CursorId.IBeam,   // an editing surface
                        Children =
                        [
                            new BoxEl { Key = "text", Width = 20, Height = 20, OnClick = () => { } },                            // inherits I-beam
                            new BoxEl { Key = "affix", Width = 20, Height = 20, OnClick = () => { }, Cursor = CursorId.Arrow }, // masks I-beam
                        ],
                    },
                    new BoxEl { Key = "link", Width = 20, Height = 20, OnClick = () => { }, Cursor = CursorId.Hand },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            CursorId last = CursorId.Arrow;
            disp.OnCursorChanged = c => last = c;
            // Row layout: plain 0–20 | field 30–80 (text 30–50, gap 50–60, affix 60–80) | link 90–110.
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(100, 10), 0, 0) });
            bool hand = last == CursorId.Hand;                  // explicit Hand (the HyperlinkButton case)
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });
            bool plainArrow = last == CursorId.Arrow;           // clickable WITHOUT a declared cursor → arrow, not hand
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(40, 10), 0, 0) });
            bool inherited = last == CursorId.IBeam;            // cursor-less child falls through to the field's I-beam
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 10), 0, 0) });
            bool masked = last == CursorId.Arrow;               // child's explicit Arrow masks the ancestor I-beam
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(55, 10), 0, 0) });
            bool ownSurface = last == CursorId.IBeam;           // the field's OWN gap resolves its I-beam (CursorBit
                                                                // makes a cursor-declared node hover-resolvable, like
                                                                // WinUI's background-gated hit testing)
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(300, 200), 0, 0) });
            bool offArrow = last == CursorId.Arrow;             // off-control: a REAL IBeam→Arrow transition must fire
            Check("E2.l hover resolves the cursor (no clickable hand; explicit inherits; Arrow masks ancestor I-beam; own surface; off→arrow)",
                hand && plainArrow && inherited && masked && ownSurface && offArrow,
                $"hand={hand} plain={plainArrow} inherit={inherited} mask={masked} own={ownSurface} off={offArrow}");
        }
    }

    static void WaveBInputChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // B.1 — pressed visual tracks the pointer while held (ButtonBase_Partial.cpp:629-638): drag-off un-presses,
        // drag-back re-presses, and a release back over the node still clicks.
        {
            var scene = new SceneStore();
            int clicks = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool pressed = (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(200, 100), 0, 0) });
            bool offCleared = (scene.Flags(node) & NodeFlags.Pressed) == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });
            bool backPressed = (scene.Flags(node) & NodeFlags.Pressed) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0) });
            bool clicked = clicks == 1 && (scene.Flags(node) & NodeFlags.Pressed) == 0;
            Check("B.1 pressed tracks the held pointer (off→clear, back→press, release-over→click)",
                pressed && offCleared && backPressed && clicked,
                $"down={pressed} off={offCleared} back={backPressed} click={clicked}");
        }

        // B.2 — repeat pause/resume hooks fire on drag-off/drag-back while held; ticker honors per-node Delay/Interval
        // with a FRESH delay (no immediate re-fire) on resume (RepeatButton_Partial.cpp:530-574).
        {
            var scene = new SceneStore();
            int clicks = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, Repeats = true, RepeatDelayMs = 80f, RepeatIntervalMs = 30f, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ticker = new FluentGpu.Animation.RepeatTicker(scene);
            var disp = new InputDispatcher(scene)
            {
                OnRepeatArmed = ticker.Arm, OnRepeatReleased = ticker.Disarm,
                OnRepeatPaused = ticker.Pause, OnRepeatResumed = ticker.Resume,
            };
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool armFired = clicks == 1;                       // Arm fires once immediately (ClickMode.Press)
            ticker.Tick(100f);                                 // crosses the 80ms custom delay → second fire
            bool delayHonored = clicks == 2;
            ticker.Tick(60f);                                  // two 30ms intervals
            bool intervalHonored = clicks == 4;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(200, 100), 0, 0) });   // off → pause
            ticker.Tick(500f);
            bool pausedNoFire = clicks == 4 && !ticker.HasActive;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0) });     // back → resume
            bool resumedNoImmediate = clicks == 4 && ticker.HasActive;   // fresh delay, NO re-fire on re-entry
            ticker.Tick(79f);
            bool freshDelay = clicks == 4;                     // still inside the fresh 80ms delay
            ticker.Tick(2f);
            bool resumedFires = clicks == 5;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0) });
            bool releaseStops = !ticker.HasActive && clicks == 5;   // release does NOT re-click a repeat node
            Check("B.2 repeat: per-node 80/30 cadence; drag-off pauses; re-entry resumes with a fresh delay",
                armFired && delayHonored && intervalHonored && pausedNoFire && resumedNoImmediate && freshDelay && resumedFires && releaseStops,
                $"arm={armFired} delay={delayHonored} interval={intervalHonored} pause={pausedNoFire} resume={resumedNoImmediate} fresh={freshDelay} fires={resumedFires} stop={releaseStops}");
        }

        // B.3 — pointer focus moves on the PRESS edge (ButtonBase_Partial.cpp:700-709); AllowFocusOnInteraction=false
        // blocks the move entirely while Tab still reaches the node (AppBarButton_themeresources.xaml:136).
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10,
                Children =
                [
                    new BoxEl { Key = "a", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "b", Width = 20, Height = 20, OnClick = () => { }, AllowFocusOnInteraction = false },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var a = Child(scene, scene.Root, 0); var b = Child(scene, scene.Root, 1);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            bool focusOnPress = disp.Focused == a;             // BEFORE any release
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 10), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(40, 10), 0, 0) });
            bool blocked = disp.Focused == a;                  // press on b never moved focus
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) });
            bool tabReaches = disp.Focused == b;               // keyboard still reaches it
            Check("B.3 pointer focus on press; AllowFocusOnInteraction=false blocks pointer focus, Tab still works",
                focusOnPress && blocked && tabReaches, $"press={focusOnPress} blocked={blocked} tab={tabReaches}");
        }

        // B.4 — middle-button release over the press target delivers OnPointerPressed with Button=2 (the WinUI
        // TabViewItem middle-click-close commit, TabViewItem.cpp:418-462); release elsewhere delivers nothing.
        {
            var scene = new SceneStore();
            var seen = new List<byte>();
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnPointerPressed = e => seen.Add(e.Button) }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 2, 0) });
            bool noneOnDown = seen.Count == 0;                 // middle never presses/activates on the down edge
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 2, 0) });
            bool delivered = seen.Count == 1 && seen[0] == 2;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 2, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(200, 100), 2, 0) });
            bool offDropped = seen.Count == 1;                 // release elsewhere → no delivery
            Check("B.4 middle-click: typed args Button=2 on release-over-same; nothing on down or off-release",
                noneOnDown && delivered && offDropped, $"down={noneOnDown} hit={delivered} off={offDropped}");
        }

        // B.4b — LEFT activation walks to the nearest click-owning ancestor (input-a11y.md §6.5), exactly like the
        // middle-release and context-request walks already did. Hit() treats DragBit as hit-anywhere and the DEEPEST hit
        // wins, so a Draggable-ONLY child inside a clickable row/card was a click BLACK HOLE: the release fired
        // GetClickHandler(child) — null — and the row never activated. Four things this pins:
        //   • the walk fires the OWNER exactly once (never the child AND the owner),
        //   • a child with its OWN OnClick still wins (the walk stops at the first owner),
        //   • press and release that resolve to the same owner are ONE click even across different hit nodes
        //     (pressing the row's label, releasing on the row's padding — the WinUI capture-on-the-owner shape),
        //   • a release that resolves to NO owner is still not a click.
        // Press-side is deliberately NOT walked: `_pressed` is the pressed-VISUAL singleton and a dozen release/cancel
        // paths clear it on `_pressed == _down`, so it keeps tracking the raw hit — asserted here so the walk is proven
        // to be what delivers the click, not the hit test.
        {
            var scene = new SceneStore();
            int plateClicks = 0, ownClicks = 0, started = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 240, Height = 120,
                Children =
                [
                    new BoxEl
                    {
                        Key = "plate", Direction = 0, Width = 200, Height = 60, OnClick = () => plateClicks++,
                        Children =
                        [
                            new BoxEl { Key = "label", Width = 80, Height = 30, CanDrag = true, OnDragStarted = _ => started++ },
                            new BoxEl { Key = "own", Width = 40, Height = 30, OnClick = () => ownClicks++ },
                        ],
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var plate = Child(scene, scene.Root, 0);
            var label = Child(scene, plate, 0);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 15), 0, 0) });
            bool hitTheChild = (scene.Flags(label) & NodeFlags.Pressed) != 0;   // the CanDrag child really is the hit
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(40, 15), 0, 0) });
            bool walkedToOwner = plateClicks == 1 && ownClicks == 0;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 15), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(100, 15), 0, 0) });
            bool childWins = ownClicks == 1 && plateClicks == 1;                // stops at the FIRST owner — no double fire

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 15), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(150, 15), 0, 0) });
            bool sameOwnerAcrossNodes = plateClicks == 2 && ownClicks == 1;     // label → plate padding is one click

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 15), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(220, 100), 0, 0) });
            bool offOwnerNoClick = plateClicks == 2;                            // released past the owner → nothing

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 15), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(56, 15), 0, 0) });   // dx 16 > 4 → promote
            bool dragArmedFromChild = started == 1 && disp.Drag.IsActive && disp.Drag.ActiveNode == label;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(56, 15), 0, 0) });
            bool dragSuppressesClick = plateClicks == 2 && ownClicks == 1;      // a finished drag never clicks the owner

            Check("B.4b activation walks to the nearest click-owning ancestor: a Draggable-only child fires the row's OnClick once; its own OnClick still wins; owner-equality spans hit nodes; a drag still arms from the child and suppresses the click",
                hitTheChild && walkedToOwner && childWins && sameOwnerAcrossNodes && offOwnerNoClick
                    && dragArmedFromChild && dragSuppressesClick,
                $"hitChild={hitTheChild} walked={walkedToOwner} childWins={childWins} sameOwner={sameOwnerAcrossNodes} offOwner={offOwnerNoClick} armed={dragArmedFromChild} suppressed={dragSuppressesClick} plate={plateClicks} own={ownClicks} started={started}");
        }

        // B.5 — the element wheel hook sees the wheel BEFORE the viewport and consumes it when Handled
        // (NumberBox.cpp:578-597); an unhandled hook lets the dispatch fall through.
        {
            var scene = new SceneStore();
            float sawDelta = 0f; int calls = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnPointerWheel = e => { calls++; sawDelta = e.Delta; e.Handled = true; } }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            int handled = disp.Dispatch(new[] { new InputEvent(InputKind.Wheel, new Point2(10, 10), 0, 0, ScrollDelta: -48f) });
            Check("B.5 element wheel hook consumes the wheel (Handled) with the raw delta",
                handled == 1 && calls == 1 && sawDelta == -48f, $"handled={handled} calls={calls} delta={sawDelta}");
        }

        // B.6 — ActivateOnEnter=false (CheckBox/RadioButton/ToggleSwitch — KeyPress::Button bAcceptsReturn=false):
        // Enter does NOT activate (it routes to OnKeyDown instead); Space still activates on key-up.
        {
            var scene = new SceneStore();
            int clicks = 0, sawKey = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, ActivateOnEnter = false, OnClick = () => clicks++, OnKeyDown = a => sawKey = a.KeyCode }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.MoveFocus(forward: true);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });
            bool enterRouted = clicks == 0 && sawKey == Keys.Enter;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool spaceClicks = clicks == 1;
            Check("B.6 ActivateOnEnter=false: Enter falls through to key routing; Space still toggles",
                enterRouted && spaceClicks, $"enter={enterRouted} (saw={sawKey}) space={spaceClicks}");
        }

        // B.7 — keyboard repeat: a held Space arms the engine repeat timer ONCE (no OS auto-repeat involvement);
        // Enter on a repeat node yields exactly one click on its down edge (RepeatButton_Partial.cpp:212-217, :29).
        {
            var scene = new SceneStore();
            int clicks = 0, armed = 0, released = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, Repeats = true, OnClick = () => clicks++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene) { OnRepeatArmed = _ => armed++, OnRepeatReleased = _ => released++ };
            disp.MoveFocus(forward: true);
            var node = disp.Focused;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Space, IsRepeat: true) });
            bool armedOnce = armed == 1 && (scene.Flags(node) & NodeFlags.Pressed) != 0;   // OS repeat ignored
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Space) });
            bool releasedOnce = released == 1 && clicks == 0;   // ticker owns the clicks; key-up never re-fires
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter) });
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Enter, IsRepeat: true) });
            disp.Dispatch(new[] { new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter) });
            bool enterOnce = clicks == 1 && armed == 1;         // Enter: ONE direct click, never arms the timer
            Check("B.7 keyboard repeat: Space arms the ticker once; Enter fires exactly one click on a repeat node",
                armedOnce && releasedOnce && enterOnce, $"armed={armedOnce} released={releasedOnce} enter={enterOnce}");
        }

        // B.8 — PointerCancel delivers OnPointerExit to the captured OnDrag target (capture-loss reset — the
        // RatingControl alt-tab mid-sweep case); touch lift clears hover (no resting touch hover).
        {
            var scene = new SceneStore();
            int exits = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(
                new BoxEl { Width = 40, Height = 20, OnDrag = _ => { }, OnPointerExit = () => exits++ }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerCancel, default, 0, 0) });
            bool cancelExit = exits >= 1;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(10, 10), 0, 0, Pointer: PointerKind.Touch) });
            bool hovered = (scene.Flags(scene.Root) & NodeFlags.Hovered) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(10, 10), 0, 0, Pointer: PointerKind.Touch) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(10, 10), 0, 0, Pointer: PointerKind.Touch) });
            bool touchHoverCleared = (scene.Flags(scene.Root) & NodeFlags.Hovered) == 0;
            Check("B.8 cancel delivers exit to the captured drag target; touch lift clears hover",
                cancelExit && hovered && touchHoverCleared, $"exit={cancelExit} hover={hovered} lifted={touchHoverCleared}");
        }

        // B.9 — TextEl's default Color is the BOUND semantic brush (one shared singleton thunk, so text retained inside
        // stateful controls follows a live re-theme instead of freezing a construction-resolved value, and the recycle
        // path can identity-match it). The thunk resolves the LIVE theme's TextFillColorPrimary: dark #FFFFFF stays
        // WinUI-faithful; light is Wavee's warm off-black #1F1E1B (the light-mode warm-ramp retint), NOT WinUI's
        // #E4000000. Guards against both a hardcoded revert AND a per-element (non-singleton) thunk.
        {
            var a = new TextEl("x").Color;
            var b = new TextEl("y").Color;
            bool singleton = a.IsBound && b.IsBound && a.Thunk is not null && ReferenceEquals(a.Thunk, b.Thunk);
            bool dark = a.Thunk!() == Tok.TextPrimary && a.Thunk() == ColorF.FromRgba(0xFF, 0xFF, 0xFF);
            Tok.Use(ThemeKind.Light);
            bool light = a.Thunk() == Tok.TextPrimary && a.Thunk() == ColorF.FromRgba(0x1F, 0x1E, 0x1B);
            Tok.Use(ThemeKind.Dark);
            Check("B.9 TextEl default color = the bound singleton theme brush resolving TextFillColorPrimary (dark #FFFFFF / light #1F1E1B warm off-black)",
                singleton && dark && light, $"singleton={singleton} dark={dark} light={light}");
        }

        // B.9b — theme-derived CONTROL defaults and a bound border remain live after mount. The same EditableText
        // instance keeps its document/focus lifetime while its typed-text brush changes; the shell-card border bind
        // follows the same RethemeAll pass. TitleBar/DropZone/TabStrip defaults use live semantic fallbacks too.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Dark);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("theme-defaults", new Size2(320, 120), 1f));
                window.Show();
                var probe = new LiveThemeDefaultsProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
                host.RunFrame();

                NodeHandle FindBorder(NodeHandle n)
                {
                    if (host.Scene.Paint(n).BorderWidth > 0f) return n;
                    for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
                    {
                        var hit = FindBorder(c);
                        if (!hit.IsNull) return hit;
                    }
                    return NodeHandle.Null;
                }

                var textNode = FindTextNode(host.Scene, strings, host.Scene.Root, "typed");
                var borderNode = FindBorder(host.Scene.Root);
                ColorF darkText = host.Scene.Paint(textNode).TextColor;
                ColorF darkBorder = host.Scene.Paint(borderNode).BorderColor;
                var title = new TitleBar();
                var drop = new DropZone();
                var tabs = new TabStrip();
                var marquee = Marquee.Default;
                ColorF darkTitle = title.IconColor;
                ColorF darkDrop = drop.Accent;
                ColorF darkTab = tabs.SelectedFill.Thunk!();
                ColorF darkMarquee = marquee.Foreground.Thunk!();

                Tok.Use(ThemeKind.Light);
                host.Reconciler.RethemeAll();
                host.RunFrame();

                var textAfter = FindTextNode(host.Scene, strings, host.Scene.Root, "typed");
                var borderAfter = FindBorder(host.Scene.Root);
                bool retained = probe.FieldConstructions == 1 && textAfter == textNode && borderAfter == borderNode;
                bool textLive = darkText != Tok.TextPrimary && host.Scene.Paint(textAfter).TextColor == Tok.TextPrimary;
                bool borderLive = darkBorder != Tok.StrokeCardDefault && host.Scene.Paint(borderAfter).BorderColor == Tok.StrokeCardDefault;
                bool defaultsLive = darkTitle != title.IconColor && title.IconColor == Tok.AccentDefault
                                 && darkDrop != drop.Accent && drop.Accent == Tok.AccentDefault
                                 && darkTab != tabs.SelectedFill.Thunk!() && tabs.SelectedFill.Thunk!() == Tok.FillSolidTertiary
                                 && darkMarquee != marquee.Foreground.Thunk!() && marquee.Foreground.Thunk!() == Tok.TextPrimary;
                Check("B.9b retained control defaults + bound border follow RethemeAll in place (no EditableText remount)",
                    retained && textLive && borderLive && defaultsLive,
                    $"retained={retained} text={textLive} border={borderLive} defaults={defaultsLive} builds={probe.FieldConstructions}");
            }
            finally { Tok.Use(saved); }
        }

        // B.9c — title-bar TabStrip uses the MUX rail grammar: the selected material is caller-supplied verbatim
        // (Wavee passes its raw translucent commanding plate), CardStroke defines the dark silhouette, the bottom rail
        // stops at both four-DIP flares, and the separator left of selection is suppressed.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Dark);
                var selected = new Signal<int>(1);
                ColorF livePlate = ColorF.FromRgba(0x3A, 0x48, 0x57, 0x73);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("tabstrip-rail", new Size2(520, 80), 1f));
                window.Show();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                    new W0fStaticProbe
                    {
                        Build = () => Embed.Comp(() => new TabStrip
                        {
                            Items =
                            [
                                new TabViewItem { Header = "one", IsClosable = false },
                                new TabViewItem { Header = "two", IsClosable = false },
                                new TabViewItem { Header = "three", IsClosable = false },
                            ],
                            SelectedIndex = selected,
                            IsAddTabButtonVisible = false,
                            TabWidth = 100f,
                            MinTabWidth = 100f,
                            MaxTabWidth = 100f,
                            SelectedFill = Prop.Of(() => livePlate),
                        }),
                    });
                host.RunFrame();

                var tabs = Roles(host.Scene, AutomationRole.Tab);
                var shapes = new List<NodeHandle>();
                var horizontalLines = new List<NodeHandle>();
                void Collect(NodeHandle n)
                {
                    if (n.IsNull) return;
                    ref var paint = ref host.Scene.Paint(n);
                    RectF rect = host.Scene.AbsoluteRect(n);
                    if (paint.VisualKind == VisualKind.TabShape) shapes.Add(n);
                    if (paint.VisualKind == VisualKind.Box && rect.H is > 0f and <= 1.01f && rect.W > 1f)
                        horizontalLines.Add(n);
                    for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Collect(c);
                }
                Collect(host.Scene.Root);

                NodeHandle selectedShape = NodeHandle.Null;
                NodeHandle rimShape = NodeHandle.Null;
                foreach (var shape in shapes)
                {
                    ColorF fill = host.Scene.Paint(shape).Fill;
                    if (fill == livePlate) selectedShape = shape;
                    if (fill == Tok.StrokeCardDefault) rimShape = shape;
                }

                bool flareClear = !selectedShape.IsNull;
                RectF selectedRect = selectedShape.IsNull ? default : host.Scene.AbsoluteRect(selectedShape);
                foreach (var line in horizontalLines)
                {
                    RectF r = host.Scene.AbsoluteRect(line);
                    if (r.X < selectedRect.Right && r.Right > selectedRect.X) flareClear = false;
                }

                float SeparatorOpacity(NodeHandle tab)
                {
                    NodeHandle wrapper = host.Scene.Parent(tab);
                    for (var n = host.Scene.FirstChild(wrapper); !n.IsNull; n = host.Scene.NextSibling(n))
                    {
                        for (var leaf = host.Scene.FirstChild(n); !leaf.IsNull; leaf = host.Scene.NextSibling(leaf))
                        {
                            RectF r = host.Scene.AbsoluteRect(leaf);
                            if (r.W is > 0f and <= 1.01f && r.H >= 16f)
                                return host.Scene.Paint(leaf).Opacity;
                        }
                    }
                    return -1f;
                }

                bool separators = tabs.Count == 3
                    && Near(SeparatorOpacity(tabs[0]), 0f)
                    && Near(SeparatorOpacity(tabs[1]), 0f)
                    && Near(SeparatorOpacity(tabs[2]), 1f);
                bool material = !selectedShape.IsNull && host.Scene.Paint(selectedShape).Fill == livePlate
                    && !rimShape.IsNull && host.Scene.Paint(rimShape).Fill == Tok.StrokeCardDefault;
                bool baseline = horizontalLines.Count == 4 && flareClear;

                Check("B.9c TabStrip raw selected material + CardStroke rim + continuous flare-cleared rail + MUX separator suppression",
                    tabs.Count == 3 && material && baseline && separators,
                    $"tabs={tabs.Count} shapes={shapes.Count} lines={horizontalLines.Count} material={material} clear={flareClear} sep={separators}");
            }
            finally { Tok.Use(saved); }
        }

        // B.9d — the TEXT appearance is a DIFFERENT grammar, not a restyle of the Chrome one: a tab is its label, and
        // the whole MUX rail vocabulary (plate fills, the two selected TabShape layers, the separators, the bottom
        // rail) is absent. Selection is carried by one strip-owned sliding underline whose springs target the selected
        // tab's laid-out rect. Chrome mode (B.9c above) must still assert the full rail — the two are siblings.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Dark);
                var selected = new Signal<int>(0);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("tabstrip-text", new Size2(640, 80), 1f));
                window.Show();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                    new W0fStaticProbe
                    {
                        Build = () => Embed.Comp(() => new TabStrip
                        {
                            Appearance = TabStripAppearance.Text,
                            Items =
                            [
                                new TabViewItem { Header = "one", IsClosable = true },
                                new TabViewItem { Header = "two", IsClosable = true },
                                new TabViewItem { Header = "three", IsClosable = true },
                            ],
                            SelectedIndex = selected,
                            IsAddTabButtonVisible = false,
                            // Left at the DEFAULT (Auto): Text mode hover-gates the close button for every tab,
                            // including the selected one — the `|| isSelected` arm the Chrome path keeps is dropped,
                            // and Auto resolves to hover-only rather than always-on.
                            MinTabWidth = 90f,
                            MaxTabWidth = 200f,
                        }),
                    });
                void Settle(int frames = 90) { for (int i = 0; i < frames; i++) host.RunFrame(); }
                Settle();

                int shapes = 0, hairlines = 0, ticks = 0;
                NodeHandle underline = NodeHandle.Null;
                void Collect(NodeHandle n)
                {
                    if (n.IsNull) return;
                    ref var paint = ref host.Scene.Paint(n);
                    RectF b = host.Scene.Bounds(n);
                    if (paint.VisualKind == VisualKind.TabShape) shapes++;
                    if (paint.VisualKind == VisualKind.Box && b.H is > 0f and <= 1.01f && b.W > 1f) hairlines++;
                    if (paint.VisualKind == VisualKind.Box && b.W is > 0f and <= 1.01f && b.H >= 16f) ticks++;
                    if (paint.VisualKind == VisualKind.Box && Near(b.H, 2f, 0.01f) && paint.Fill == Tok.AccentDefault)
                        underline = n;
                    for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Collect(c);
                }
                Collect(host.Scene.Root);

                var tabs = Roles(host.Scene, AutomationRole.Tab);
                // Hover-only close, RESERVED-SLOT contract: a closable text tab always MOUNTS its × (so the content-hug
                // tab's width can never change on hover — the old hover-MOUNTED × reflowed everything after it and slid
                // the underline on every pointer pass), but at rest every × is Opacity 0 and not hit-testable. Hovering
                // a tab reveals ITS × at full opacity while the tab's width stays byte-identical.
                var closes = Roles(host.Scene, AutomationRole.Button);
                bool slotsMounted = closes.Count == 3;
                bool hiddenAtRest = true;
                foreach (var c in closes)
                    hiddenAtRest &= Near(host.Scene.Paint(c).Opacity, 0f, 0.001f)
                        && (host.Scene.Flags(c) & NodeFlags.HitTestVisible) == 0;
                float restW0 = host.Scene.AbsoluteRect(host.Scene.Parent(tabs[0])).W;
                var hoverPt = host.Scene.AbsoluteRect(tabs[0]);
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(hoverPt.X + hoverPt.W / 2f, hoverPt.Y + hoverPt.H / 2f), 0, 0));
                Settle();
                var closesHover = Roles(host.Scene, AutomationRole.Button);
                bool shownOnHover = closesHover.Count == 3 && Near(host.Scene.Paint(closesHover[0]).Opacity, 1f, 0.01f);
                float hoverW0 = host.Scene.AbsoluteRect(host.Scene.Parent(tabs[0])).W;
                bool widthStable = Near(hoverW0, restW0, 0.01f);
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(600f, 70f), 0, 0));
                Settle();
                bool closeContract = slotsMounted && hiddenAtRest && shownOnHover && widthStable;

                // B.9c-text (selection expression) — the Text strip says "not selected" with a THEME TOKEN, not with a
                // plate opacity tier. It used to multiply the whole tab subtree by 0.6 (0.85 on hover) on top of an
                // already-secondary foreground, which (a) compounded to a dimness below any rung the theme defines,
                // (b) dimmed the tab's icon and its close glyph, neither of which expresses selection, and (c) could
                // not be retuned by a high-contrast or custom palette, because 0.6 is not a token. Pin the replacement
                // so it cannot drift back: the label of an UNSELECTED tab rests on TextSecondary and carries a
                // TextPrimary hover ramp (eased by the plate's own HoverT — SceneRecorder.ResolveTextColorCore), the
                // SELECTED tab's label is TextPrimary with NO state ramp (A==0), and every plate sits at full opacity.
                NodeHandle LabelOf(NodeHandle n, string header)
                {
                    if (n.IsNull) return NodeHandle.Null;
                    if (host.Scene.Paint(n).VisualKind == VisualKind.Text && strings.Resolve(host.Scene.Paint(n).Text) == header)
                        return n;
                    for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
                    {
                        var hit = LabelOf(c, header);
                        if (!hit.IsNull) return hit;
                    }
                    return NodeHandle.Null;
                }
                var selLabel = LabelOf(tabs[0], "one");
                var offLabel = LabelOf(tabs[1], "two");
                bool platesFullStrength = true;
                foreach (var t in tabs) platesFullStrength &= Near(host.Scene.Paint(t).Opacity, 1f, 0.001f);
                bool selInk = !selLabel.IsNull
                    && host.Scene.Paint(selLabel).TextColor == Tok.TextPrimary
                    && host.Scene.Paint(selLabel).TextHoverColor.A == 0f;
                bool offInk = !offLabel.IsNull
                    && host.Scene.Paint(offLabel).TextColor == Tok.TextSecondary
                    && host.Scene.Paint(offLabel).TextHoverColor == Tok.TextPrimary;
                Check("B.9c-text TabStrip Text selection is a TEXT-TOKEN ramp (secondary→primary), not a plate opacity tier",
                    selInk && offInk && platesFullStrength,
                    $"selectedInk={selInk} unselectedInk={offInk} platesOpaque={platesFullStrength} " +
                    $"sel={(selLabel.IsNull ? "none" : host.Scene.Paint(selLabel).TextColor.ToString())} " +
                    $"off={(offLabel.IsNull ? "none" : host.Scene.Paint(offLabel).TextColor.ToString())}/" +
                    $"{(offLabel.IsNull ? "none" : host.Scene.Paint(offLabel).TextHoverColor.ToString())}");

                // The underline tracks the SELECTED tab: x = tab.x + the 12-DIP label inset, width = tab.w − both
                // insets, both in LAYOUT (the springs only ease the FLIP delta back to identity, so a resize that
                // clears the animation slab leaves the bar exactly on target). AbsoluteRect folds in the live
                // TranslateX, so reading it mid-flight shows the slide.
                (float X, float W) Target(int index)
                {
                    RectF wrap = host.Scene.AbsoluteRect(host.Scene.Parent(tabs[index]));
                    return (wrap.X + 12f, wrap.W - 24f);
                }
                float UnderlineX() => host.Scene.AbsoluteRect(underline).X;
                float UnderlineW() => host.Scene.Bounds(underline).W;
                // …and it is ANCHORED TO THE TAB PLATE'S BOTTOM EDGE, not to the strip's floor. The row is 48 tall with
                // the 32-DIP plates centred, so the plate bottom is 40 and the 2-DIP bar occupies 38..40. It used to
                // hang off the host's own 48-DIP bottom (46..48) — six DIP under the tab it marks, which reads as a rule
                // beneath the whole bar rather than as that tab's underline.
                float UnderlineBottom() { var r = host.Scene.AbsoluteRect(underline); return r.Y + r.H; }
                float TabBottom(int index)
                {
                    RectF wrap = host.Scene.AbsoluteRect(host.Scene.Parent(tabs[index]));
                    return wrap.Y + wrap.H;
                }

                var t0 = Target(0);
                bool at0 = !underline.IsNull && Near(UnderlineX(), t0.X, 1.5f) && Near(UnderlineW(), t0.W, 1.5f);
                bool onPlateBottom = !underline.IsNull && Near(UnderlineBottom(), TabBottom(0), 0.51f);
                float x0 = underline.IsNull ? float.NaN : UnderlineX();

                selected.Value = 2;                                  // …and it SLIDES to the new selection
                Settle();
                var t2 = Target(2);
                bool at2 = !underline.IsNull && Near(UnderlineX(), t2.X, 1.5f) && Near(UnderlineW(), t2.W, 1.5f);
                float x2 = underline.IsNull ? float.NaN : UnderlineX();

                Check("B.9c-text TabStrip Text appearance: labels only — no TabShape/rail/separator ink — a sliding accent underline on the plate's bottom edge, and reserved-slot hover-only close (no width flicker)",
                    tabs.Count == 3 && shapes == 0 && hairlines == 0 && ticks == 0
                        && !underline.IsNull && at0 && at2 && onPlateBottom && x2 > x0 + 50f && closeContract,
                    $"tabs={tabs.Count} shapes={shapes} hairlines={hairlines} ticks={ticks} underline={!underline.IsNull} " +
                    $"at0={at0}(x={x0:0.#} want {t0.X:0.#}/{t0.W:0.#}) at2={at2}(x={x2:0.#} want {t2.X:0.#}/{t2.W:0.#}) " +
                    $"onPlateBottom={onPlateBottom}(bottom={(underline.IsNull ? float.NaN : UnderlineBottom()):0.##} want {TabBottom(0):0.##}) " +
                    $"closeContract={closeContract}(slots={slotsMounted} rest0={hiddenAtRest} hover1={shownOnHover} widthStable={widthStable} restW={restW0:0.##} hoverW={hoverW0:0.##})");

                // Reduced motion is a VALUE read at the SEED, never a branch in authoring code — and the RAW UseSpring
                // hook carries no ReducedMotionPolicy, so the underline seeds through a motion TOKEN instead. Same
                // retarget, four frames in: normally still mid-flight, under reduced motion already parked.
                var t1 = Target(1);
                bool prevReduced = Motion.ReducedMotion;
                float slideX, snapX;
                try
                {
                    selected.Value = 1; Settle(4); slideX = UnderlineX();      // sliding
                    selected.Value = 2; Settle();                              // park it back at the far tab
                    Motion.ReducedMotion = true;
                    selected.Value = 1; Settle(4); snapX = UnderlineX();       // snapped
                }
                finally { Motion.ReducedMotion = prevReduced; }
                Check("B.9c-text TabStrip Text underline honours reduced-motion-as-a-value (token-seeded ⇒ snap, not slide)",
                    !Near(slideX, t1.X, 3f) && Near(snapX, t1.X, 1.5f),
                    $"target={t1.X:0.#} sliding@4f={slideX:0.#} reduced@4f={snapX:0.#}");

                // A window resize CANCELS every in-flight structural track BY DESIGN (gpu-renderer.md's window-resize
                // snap → AnimEngine.SnapStructuralToLayout/CancelStructuralAll; butter-smooth-resize-v2.md:263 "FLIP
                // capture is skipped when `resized` — resizes snap by design"). The indicator survives that only
                // because its RESTING position is pure layout and the springs carry a decaying FLIP delta: kill the
                // delta at any instant and the bar is already under the selected tab. (An anim-positioned indicator
                // collapsed to the strip origin here and never came back.)
                selected.Value = 0; Settle();
                selected.Value = 2; host.RunFrame(); host.RunFrame();     // mid-slide, delta still large
                float midX = UnderlineX();
                host.Animation.SnapStructuralToLayout(underline);         // exactly what a resize does to this node
                host.RunFrame();
                float snappedX = UnderlineX(), snappedW = UnderlineW();
                // …and a real relayout (window resize) re-derives the rest position from the new tab rects.
                window.ClientSizePx = new Size2(720, 80);
                window.PaintRequested?.Invoke();
                Settle(30);
                var t2r = Target(2);
                Check("B.9c-text TabStrip Text underline survives the by-design structural snap (rest = layout truth, not anim state)",
                    !Near(midX, t2r.X, 3f) && Near(snappedX, t2r.X, 1.5f) && Near(snappedW, t2r.W, 1.5f)
                        && Near(UnderlineX(), t2r.X, 1.5f) && Near(UnderlineW(), t2r.W, 1.5f),
                    $"mid={midX:0.#} snapped={snappedX:0.#}/{snappedW:0.#} afterResize={UnderlineX():0.#}/{UnderlineW():0.#} want={t2r.X:0.#}/{t2r.W:0.#}");
            }
            finally { Tok.Use(saved); }
        }

        // B.9c-text (add button) — the Text strip's HOVER-ONLY "+". Three things are load-bearing and none of them are
        // visible from the Chrome path: (1) the slot is RESERVED at rest, i.e. the button node exists and occupies its
        // full 32 DIP after the LAST tab even while invisible — the strip hugs and a title bar reports that hug as ONE
        // TitleBarHit.Client region, so a mount-on-hover would move the reported rect on every pointer entry; (2) at
        // rest it paints NOTHING (opacity 0), so the reserved slot is not visible space; (3) the reserved slot is not a
        // dead hole — it is hit-testable, so entering it reveals the button and a click still opens a tab. `Never`
        // mounts no button at all, which is what keeps the historical no-"+" strip exactly as it was.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Dark);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("tabstrip-add", new Size2(640, 80), 1f));
                window.Show();
                int added = 0;
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                    new W0fStaticProbe
                    {
                        Build = () => Embed.Comp(() => new TabStrip
                        {
                            Appearance = TabStripAppearance.Text,
                            // Non-closable on purpose: the close buttons would otherwise be MORE AutomationRole.Buttons
                            // in the strip and the "+" could not be identified by role alone. IsClosable defaults TRUE,
                            // and since the reserved-slot close contract the × nodes are mounted even at rest — so the
                            // intent must be explicit.
                            Items = [new TabViewItem { Header = "one", IsClosable = false },
                                     new TabViewItem { Header = "two", IsClosable = false }],
                            IsAddTabButtonVisible = true,
                            AddButtonVisibility = TabStripAddButtonVisibility.OnStripPointerOver,
                            OnAddTabButtonClick = () => { added++; return null; },
                            MinTabWidth = 90f,
                            MaxTabWidth = 200f,
                        }),
                    });
                void Settle(int frames = 90) { for (int i = 0; i < frames; i++) host.RunFrame(); }
                Settle();

                var addButtons = Roles(host.Scene, AutomationRole.Button);
                NodeHandle add = addButtons.Count == 1 ? addButtons[0] : NodeHandle.Null;
                var addTabs = Roles(host.Scene, AutomationRole.Tab);
                float Opacity() => add.IsNull ? float.NaN : host.Scene.Paint(add).Opacity;

                RectF addRect = add.IsNull ? default : host.Scene.AbsoluteRect(add);
                RectF lastTab = addTabs.Count == 2 ? host.Scene.AbsoluteRect(host.Scene.Parent(addTabs[1])) : default;
                // RESERVED: full width, after the last tab — and INVISIBLE.
                bool reserved = !add.IsNull && Near(addRect.W, 32f, 0.51f) && addRect.X >= lastTab.Right - 0.51f;
                float restOpacity = Opacity();
                bool hiddenAtRest = !add.IsNull && Near(restOpacity, 0f, 0.001f);

                // Pointer onto a TAB ⇒ the strip is hovered ⇒ the "+" cross-fades in. (The reveal rides the strip's own
                // per-tab hover signal with a sentinel for the button, NOT a hover handler on the strip root — an
                // interactive ancestor becomes a hover CONTAINER and AnimScheduler.SetHoverDescendants would drive
                // every text tab's HoverOpacity at once.)
                var onTab = host.Scene.AbsoluteRect(addTabs[0]);
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(onTab.X + onTab.W / 2f, onTab.Y + onTab.H / 2f), 0, 0));
                Settle();
                float hoverOpacity = Opacity();
                bool shownOnHover = Near(hoverOpacity, 1f, 0.01f);

                // …and it is a real target while revealed: press+release over the reserved slot opens a tab.
                var onAdd = new Point2(addRect.X + addRect.W / 2f, addRect.Y + addRect.H / 2f);
                window.QueueInput(new InputEvent(InputKind.PointerMove, onAdd, 0, 0));
                Settle(2);
                window.QueueInput(new InputEvent(InputKind.PointerDown, onAdd, 0, 0));
                host.RunFrame();
                window.QueueInput(new InputEvent(InputKind.PointerUp, onAdd, 0, 0));
                Settle();
                bool clickable = added == 1 && Near(Opacity(), 1f, 0.01f);   // still revealed while the pointer is on IT

                // Pointer off the strip ⇒ back to nothing.
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(600f, 70f), 0, 0));
                Settle();
                float leftOpacity = Opacity();
                bool hiddenAgain = Near(leftOpacity, 0f, 0.01f);

                Check("B.9c-text TabStrip Text add button OnStripPointerOver: the '+' slot is reserved after the last tab, paints nothing at rest, fades in on strip hover (and is a live target there), and fades back out on leave",
                    !add.IsNull && reserved && hiddenAtRest && shownOnHover && clickable && hiddenAgain,
                    $"found={!add.IsNull}(buttons={addButtons.Count}) reserved={reserved}(w={addRect.W:0.#} x={addRect.X:0.#} lastTabRight={lastTab.Right:0.#}) " +
                    $"rest={restOpacity:0.###} hover={hoverOpacity:0.###} left={leftOpacity:0.###} added={added}");
            }
            finally { Tok.Use(saved); }
        }

        // …and Never is a true absence, not a zero-width ghost: no add button node is mounted at all, which is the
        // configuration every existing Text-strip host (and the B.9d block above) relies on.
        {
            ThemeKind saved = Tok.Theme;
            try
            {
                Tok.Use(ThemeKind.Dark);
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("tabstrip-noadd", new Size2(640, 80), 1f));
                window.Show();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings,
                    new W0fStaticProbe
                    {
                        Build = () => Embed.Comp(() => new TabStrip
                        {
                            Appearance = TabStripAppearance.Text,
                            Items = [new TabViewItem { Header = "one", IsClosable = false },
                                     new TabViewItem { Header = "two", IsClosable = false }],
                            IsAddTabButtonVisible = true,
                            AddButtonVisibility = TabStripAddButtonVisibility.Never,
                            MinTabWidth = 90f,
                            MaxTabWidth = 200f,
                        }),
                    });
                for (int i = 0; i < 90; i++) host.RunFrame();
                int buttons = Roles(host.Scene, AutomationRole.Button).Count;
                int tabCount = Roles(host.Scene, AutomationRole.Tab).Count;
                Check("B.9c-text TabStrip Text add button Never: no '+' node is mounted (the strip is exactly its tabs)",
                    buttons == 0 && tabCount == 2, $"buttons={buttons} tabs={tabCount}");
            }
            finally { Tok.Use(saved); }
        }

        // B.10 — PersonPicture geometry contract: initials centered in the circle; the badge plate hangs 4px outside
        // the top-right (root UNclipped, left = size+4−plate, top = −4); a negative badge number shows NO badge.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(PersonPicture.Create("JD", 96f, badgeNumber: 5), null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var root = scene.Root;
            bool unclipped = (scene.Flags(root) & NodeFlags.ClipsToBounds) == 0;
            var face = Child(scene, root, 0);
            var text = Child(scene, face, 0);
            var rootR = scene.AbsoluteRect(root);
            var textR = scene.AbsoluteRect(text);
            bool centered = MathF.Abs((textR.X + textR.W / 2f) - (rootR.X + 48f)) <= 1f
                         && MathF.Abs((textR.Y + textR.H / 2f) - (rootR.Y + 48f)) <= 1f;
            var badge = Child(scene, root, 1);
            var badgeR = scene.AbsoluteRect(badge);
            bool badgePos = MathF.Abs(badgeR.X - rootR.X - 52f) <= 0.5f && MathF.Abs(badgeR.Y - rootR.Y + 4f) <= 0.5f
                         && MathF.Abs(badgeR.W - 48f) <= 0.5f;

            var scene2 = new SceneStore();
            new TreeReconciler(scene2, strings).ReconcileRoot(PersonPicture.Create("JD", 96f, badgeNumber: -3, badgeGlyph: ""), null);
            bool negativeNoBadge = Child(scene2, scene2.Root, 1).IsNull;   // number<0 owns the slot → NO badge, glyph ignored
            Check("B.10 PersonPicture: centered initials; badge at (52,−4) 48px on an unclipped root; negative number = no badge",
                unclipped && centered && badgePos && negativeNoBadge,
                $"unclipped={unclipped} centered={centered} badge={badgePos} negNone={negativeNoBadge}");
        }

        // B.11 — the TabView "+" appends through the REAL click path (the WinUI Gallery AddButtonClick handler,
        // TabViewPage.xaml.cs:51-54): a click on the captured add button runs OnAddTabButtonClick and the returned
        // TabViewItem joins the strip (one more AutomationRole.Tab plate). Guards the gallery wiring fix end-to-end —
        // a null handler stays a no-op (the correct WinUI contract), a wired handler appends.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tabadd", new Size2(640, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            int adds = 0;
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Grow = 1f,
                    Children =
                    [
                        TabView.Create(
                            new[] { "Document 1", "Document 2" },
                            onAddTabButtonClick: () => { adds++; return new TabViewItem { Header = "Document " + (adds + 2), Icon = Icons.Document }; }),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s = host.Scene;

            // The add button is the right-most AutomationRole.Button in the strip (tabs + their close buttons sit to
            // its left; the trailing Grow spacer pushes nothing focusable past it).
            NodeHandle AddButton()
            {
                var buttons = Roles(s, AutomationRole.Button);
                var pick = NodeHandle.Null; float maxX = float.NegativeInfinity;
                foreach (var b in buttons)
                {
                    float cx = CenterOf(s, b).X;
                    if (cx > maxX) { maxX = cx; pick = b; }
                }
                return pick;
            }

            int tabsBefore = Roles(s, AutomationRole.Tab).Count;
            var add = AddButton();
            bool foundAdd = !add.IsNull;
            ClickNode(host, window, add);
            int tabsAfterOne = Roles(s, AutomationRole.Tab).Count;
            ClickNode(host, window, AddButton());   // re-find: the strip rebuilt with the new tab
            int tabsAfterTwo = Roles(s, AutomationRole.Tab).Count;
            bool appended = tabsBefore == 2 && tabsAfterOne == 3 && tabsAfterTwo == 4 && adds == 2;
            Check("B.11 TabView '+' appends a tab through the live click path (Gallery AddButtonClick wiring)",
                foundAdd && appended,
                $"found={foundAdd} before={tabsBefore} after1={tabsAfterOne} after2={tabsAfterTwo} adds={adds}");
        }
    }

    static void E5DragDropChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // e5dragdrop.1 — a press that never leaves the 4px per-axis drag box stays a plain click: release fires
        // OnClick, no drag lifecycle event fires, and the node's transform is untouched. +4/+4 sits ON the box edge —
        // WinUI promotes only strictly OUTSIDE it (dx > maxDx || dy > maxDy, ListViewBaseItem_Partial.cpp:1877).
        {
            var scene = new SceneStore();
            int clicks = 0, started = 0, deltas = 0, completed = 0, canceled = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 60, CanDrag = true,
                OnClick = () => clicks++,
                OnDragStarted = _ => started++,
                OnDragDelta = _ => deltas++,
                OnDragCompleted = _ => completed++,
                OnDragCanceled = () => canceled++,
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
            bool armed = disp.Drag.IsArmed && !disp.Drag.IsActive;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(104, 34), 0, 0) });
            bool stillArmed = disp.Drag.IsArmed && !disp.Drag.IsActive && started == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(104, 34), 0, 0) });
            bool clicked = clicks == 1 && started == 0 && deltas == 0 && completed == 0 && canceled == 0;
            bool untouched = scene.Paint(node).LocalTransform.Dx == 0f && !disp.Drag.IsArmed && !disp.Drag.IsActive;
            Check("e5dragdrop.1 press inside the 4px per-axis drag box stays a click (no drag lifecycle)",
                armed && stillArmed && clicked && untouched,
                $"armed={armed} stillArmed={stillArmed} clicks={clicks} started={started}");
        }

        // e5dragdrop.ext — the OS file-drop seam matches the hand-vtable IDropTarget backend: HOVER is DATA-FREE
        // (ExternalDragEnter is given EMPTY paths, so the session payload is an empty FileDropData while hovering — the
        // backend reads no file data during DragEnter/Over), Enter/Over report Copy + flip OnEnter/OnLeave, and the
        // PATH-BEARING ExternalDropFiles fills the payload at drop so OnDrop sees the real FileDropData. Off-target
        // reports None + fires OnLeave.
        {
            var scene = new SceneStore();
            string[]? dropped = null; int enters = 0, leaves = 0, hoverPayloadCount = -1;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
                DropTarget = new DropTargetSpec(
                    new[] { DropKinds.Files },
                    OnEnter: s => { enters++; if (s.Payload is FileDropData d) hoverPayloadCount = d.Count; },
                    OnLeave: _ => leaves++,
                    OnDrop: s => { if (s.Payload is FileDropData d) dropped = d.Paths; }),
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);

            var paths = new[] { @"C:\music\track.flac", @"C:\music\album" };
            var inside = new Point2(100, 100);
            var outside = new Point2(400, 400);   // off the 200×200 target

            var eff1 = disp.ExternalDragEnter(inside, System.Array.Empty<string>(), KeyModifiers.None);   // DATA-FREE hover
            var eff2 = disp.ExternalDragOver(inside, KeyModifiers.None);
            bool dropOk = disp.ExternalDropFiles(inside, paths, KeyModifiers.None);                        // paths arrive at DROP
            bool acceptOk = eff1 == DropEffect.Copy && eff2 == DropEffect.Copy && dropOk
                            && enters == 1 && hoverPayloadCount == 0   // hover saw an EMPTY payload (data-free)
                            && dropped is { Length: 2 } && dropped[0] == paths[0] && dropped[1] == paths[1];

            // off-target: a fresh enter inside, then a move OUTSIDE the target reports None and fires OnLeave; no drop.
            var eff3 = disp.ExternalDragEnter(inside, System.Array.Empty<string>(), KeyModifiers.None);
            var eff4 = disp.ExternalDragOver(outside, KeyModifiers.None);
            disp.ExternalDragLeave();
            bool leaveOk = eff3 == DropEffect.Copy && eff4 == DropEffect.None && leaves == 1;

            Check("e5dragdrop.ext an OS file drop (IDropTarget seam) hovers DATA-FREE (empty payload, Enter/Over=Copy) then ExternalDropFiles delivers the real FileDropData to a DropTarget accepting DropKinds.Files; off-target reports None + fires OnLeave",
                acceptOk && leaveOk,
                $"enter={eff1} over={eff2} drop={dropOk} hoverCount={hoverPayloadCount} paths={(dropped is null ? "null" : string.Join("|", dropped))} off(eff={eff4},leaves={leaves})");
        }

        // e5dragdrop.capability — a generic kind match is only the cheap first gate. An incompatible inner target is
        // transparent, so the nearest compatible ancestor owns the session instead of showing a false drop affordance.
        {
            var scene = new SceneStore();
            int outerEnter = 0, innerEnter = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 200,
                DropTarget = new DropTargetSpec(["resource"], OnEnter: _ => outerEnter++),
                Children =
                [
                    new BoxEl
                    {
                        Width = 100, Height = 100,
                        DropTarget = new DropTargetSpec(["resource"], OnEnter: _ => innerEnter++)
                        {
                            CanAccept = static _ => false,
                        },
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var p = new Point2(50, 50);
            bool began = disp.DragDrop.ExternalBegin("resource", "payload", p, KeyModifiers.None);
            disp.DragDrop.Move(disp.DiagHitTest(p), p, 0f, 0f, KeyModifiers.None);
            bool passedThrough = began && outerEnter == 1 && innerEnter == 0
                                 && disp.DragDrop.OverTarget == scene.Root;
            disp.DragDrop.Cancel();
            Check("e5dragdrop.capability incompatible inner target passes through to compatible ancestor",
                passedThrough, $"began={began} outer={outerEnter} inner={innerEnter}");
        }

        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Width = 200, Height = 100,
                Children =
                [
                    new BoxEl
                    {
                        Width = 100, Height = 100,
                        DropTarget = new DropTargetSpec(["resource"])
                        {
                            VisualPolicy = DropTargetVisualPolicy.Spotlight,
                            CanAccept = static s => string.Equals(s.Payload as string, "ok", StringComparison.Ordinal),
                        },
                    },
                    new BoxEl
                    {
                        Width = 100, Height = 100,
                        DropTarget = new DropTargetSpec(["resource"])
                        {
                            VisualPolicy = DropTargetVisualPolicy.Spotlight,
                            CanAccept = static _ => false,
                        },
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var compatible = scene.FirstChild(scene.Root);
            var incompatible = scene.NextSibling(compatible);
            var disp = new InputDispatcher(scene);
            bool began = disp.DragDrop.ExternalBegin("resource", "ok", new Point2(50, 50), KeyModifiers.None);
            bool filtered = began && scene.DropSpotlightActive
                && scene.IsDropSpotlightRoot(compatible) && !scene.IsDropSpotlightRoot(incompatible);
            disp.DragDrop.Cancel();
            Check("e5dragdrop.spotlight only capability-compatible opt-in targets escape the drag dim and cancel clears it",
                filtered && !scene.DropSpotlightActive,
                $"began={began} filtered={filtered} activeAfterCancel={scene.DropSpotlightActive}");
        }

        // e5dragdrop.parked — a KeepAlive-PARKED page (an INACTIVE TAB) must not publish drop targets, and a drag over a
        // still-attached target must keep working. Reconciler.DeactivateKeepAliveEntry parks a page with SetSubtreeParked
        // + Detach and deliberately RETAINS HitTestVisible; the drop-target registry is only cleared when a node is FREED,
        // so a parked page keeps every target row it registered. Reachability used to be proved by running out of
        // ancestors, which a detached subtree root does IMMEDIATELY (its parent is Null) — so the parked page's targets
        // were advertised as destinations and punched scrim cutouts at their stale last-arranged rects. Reachability is
        // now proved by TERMINATING AT Root, the only node the hit test descends from.
        {
            var scene = new SceneStore();
            int liveEnter = 0, parkedEnter = 0;
            Element Target(string key, Action<DragSession> onEnter) => new BoxEl
            {
                Key = key, Width = 200, Height = 100,
                DropTarget = new DropTargetSpec(["resource"], OnEnter: onEnter)
                {
                    VisualPolicy = DropTargetVisualPolicy.Spotlight,
                },
            };
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Width = 400, Height = 100,
                Children =
                [
                    Target("live", _ => liveEnter++),
                    new BoxEl { Key = "page", Width = 200, Height = 100, Children = [Target("parked", _ => parkedEnter++)] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var liveTarget = scene.FirstChild(scene.Root);
            var page = scene.NextSibling(liveTarget);
            var parkedTarget = scene.FirstChild(page);

            scene.Detach(page);   // the park: still LIVE, still HitTestVisible, still holding its DropTargetSpec rows

            var disp = new InputDispatcher(scene);
            var p = new Point2(100, 50);
            string moveFault = "none";
            bool began = disp.DragDrop.ExternalBegin("resource", "payload", p, KeyModifiers.None);
            try { disp.DragDrop.Move(disp.DiagHitTest(p), p, 0f, 0f, KeyModifiers.None); }
            catch (Exception ex) { moveFault = ex.GetType().Name; }

            bool parkedExcluded = !scene.IsDropSpotlightRoot(parkedTarget) && parkedEnter == 0;
            bool parkedWasRoot = scene.IsDropSpotlightRoot(parkedTarget);
            var over = disp.DragDrop.OverTarget;
            bool reachableWorks = scene.IsDropSpotlightRoot(liveTarget) && over == liveTarget && liveEnter == 1;
            disp.DragDrop.Cancel();

            // …and REACTIVATION restores it. The filter keeps no per-node exclusion state — re-linking the page to Root
            // is the whole repair, which is what makes a tab that is switched back to droppable again.
            scene.AppendChild(scene.Root, page);
            bool reBegan = disp.DragDrop.ExternalBegin("resource", "payload", p, KeyModifiers.None);
            bool reattachedPublishes = reBegan && scene.IsDropSpotlightRoot(parkedTarget);
            disp.DragDrop.Cancel();

            Check("e5dragdrop.parked a KeepAlive-parked (detached) page publishes NO drop targets while a still-attached target keeps taking the drag, Move never faults, and re-attaching the page restores its targets",
                began && moveFault == "none" && parkedExcluded && reachableWorks && reattachedPublishes,
                $"began={began} moveFault={moveFault} parkedRoot={parkedWasRoot} parkedEnter={parkedEnter} liveOk={reachableWorks} liveEnter={liveEnter} over={over.Raw.Index} live={liveTarget.Raw.Index} reattached={reattachedPublishes}");
        }

        // e5dragdrop.style — DragSource.Style overrides the lifted ghost's opacity (the default 0.80 → a custom value).
        // A drag promotes on a Draggable carrying Style{Opacity=0.5}; after promotion the node's painted opacity is 0.5.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 60, CanDrag = true,
                Draggable = new DragSource("chip", () => "p") { Style = new DragVisualStyle { Opacity = 0.5f } },
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(140, 30), 0, 0) });   // cross the box → promote
            float ghostOpacity = scene.Paint(node).Opacity;
            bool styledGhost = disp.Drag.IsActive && System.MathF.Abs(ghostOpacity - 0.5f) < 0.001f;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(140, 30), 0, 0) });

            Check("e5dragdrop.style DragSource.Style.Opacity overrides the lifted ghost opacity (default 0.80 → 0.50)",
                styledGhost, $"active={disp.Drag.IsActive} ghostOpacity={ghostOpacity:0.00}");
        }

        // e5dragdrop.threshold — DragVisualStyle.ThresholdMultiplier widens the MOUSE drag box per source (WinUI
        // LISTVIEWBASEITEM_MOUSE_DRAG_THRESHOLD_MULTIPLIER = 2.0, ListViewBaseItem_Partial.cpp:54, applied :1873-1874).
        // The reported bug: a tab is a Drag.Source, so clicking one while the mouse is still travelling crosses the 4px
        // box, promotes, and has its click SUPPRESSED — the tab silently fails to select. At ×2 a 6px travel is still a
        // click; 10px still drags; and a multiplier-1 source is byte-identical to before (promotes at 5-6px).
        {
            static (int clicks, int starts, bool active) Gesture(StringTable st, HeadlessFontSystem f, float mul, float travel)
            {
                var scene = new SceneStore();
                int clicks = 0, starts = 0;
                new TreeReconciler(scene, st).ReconcileRoot(new BoxEl
                {
                    Width = 200, Height = 60, CanDrag = true,
                    OnClick = () => clicks++,
                    OnDragStarted = _ => starts++,
                    Draggable = new DragSource("tab", () => "p")
                    {
                        Style = new DragVisualStyle { Lift = DragLift.Stationary, ThresholdMultiplier = mul },
                    },
                }, null);
                new FlexLayout(scene, f).Run(scene.Root);
                var disp = new InputDispatcher(scene);
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(100 + travel, 30), 0, 0) });
                bool active = disp.Drag.IsActive;
                disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(100 + travel, 30), 0, 0) });
                return (clicks, starts, active);
            }

            var wide6 = Gesture(strings, fonts, 2f, 6f);     // ×2 box = 8px → 6px is INSIDE it: a click, not a drag
            var wide10 = Gesture(strings, fonts, 2f, 10f);   // ×2 box = 8px → 10px still promotes
            var base5 = Gesture(strings, fonts, 1f, 5f);     // unscaled: the historical 4px box still promotes at 5px
            var base6 = Gesture(strings, fonts, 1f, 6f);
            var zero6 = Gesture(strings, fonts, 0f, 6f);     // a meaningless ≤0 multiplier degrades to the base box

            bool clickKept = wide6.clicks == 1 && wide6.starts == 0 && !wide6.active;
            bool stillDrags = wide10.clicks == 0 && wide10.starts == 1 && wide10.active;
            bool defaultUnchanged = base5.clicks == 0 && base5.starts == 1 && base5.active
                                    && base6.clicks == 0 && base6.starts == 1 && base6.active;
            bool zeroIsBase = zero6.clicks == 0 && zero6.starts == 1;

            Check("e5dragdrop.threshold DragVisualStyle.ThresholdMultiplier scales the per-axis MOUSE drag box per source: at ×2 (WinUI's list-item multiplier) a 6px press-move-release still CLICKS the source instead of being eaten by a drag promotion, while 10px still drags; a multiplier-1 source is unchanged (promotes at 5px and 6px) and a ≤0 multiplier degrades to the base 4px box",
                clickKept && stillDrags && defaultUnchanged && zeroIsBase,
                $"wide6(clicks={wide6.clicks} starts={wide6.starts} active={wide6.active}) wide10(clicks={wide10.clicks} starts={wide10.starts}) base5(clicks={base5.clicks} starts={base5.starts}) base6(clicks={base6.clicks} starts={base6.starts}) zero6(clicks={zero6.clicks} starts={zero6.starts})");
        }

        // e5dragdrop.2/.2b — crossing the drag box on a press that began on a CHILD of the CanDrag row promotes the
        // ROW (TryArm walks up like WinUI's item container): OnDragStarted fires once BEFORE the first OnDragDelta,
        // the transient pressed visuals are cleared, and the row carries the drag visuals — opacity 0.80
        // (ListViewItemDragThemeOpacity — ListViewItem_themeresources.xaml:7), the flyout-class shadow, hit-test off,
        // and a parent-space translate equal to the gesture delta. Release restores everything, fires DragCompleted,
        // SUPPRESSES the click, and hands OnSettle the (drop → resting) rects for the FLIP glide.
        {
            var scene = new SceneStore();
            int rowClicks = 0, childClicks = 0, started = 0, deltas = 0, completed = 0;
            int firstEvent = 0;                       // 1 = started first, 2 = delta first — order proof
            float doneDx = 0f, doneDy = 0f;
            NodeHandle settleNode = default; RectF settleFrom = default, settleTo = default; int settles = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 240, Height = 120,
                Children =
                [
                    new BoxEl
                    {
                        Key = "row", Width = 200, Height = 60, CanDrag = true,
                        OnClick = () => rowClicks++,
                        OnDragStarted = _ => { started++; if (firstEvent == 0) firstEvent = 1; },
                        OnDragDelta = _ => { deltas++; if (firstEvent == 0) firstEvent = 2; },
                        OnDragCompleted = e => { completed++; doneDx = e.TotalDx; doneDy = e.TotalDy; },
                        Children = [new BoxEl { Key = "child", Width = 80, Height = 30, OnClick = () => childClicks++ }],
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Drag.OnSettle = (n, from, to) => { settles++; settleNode = n; settleFrom = from; settleTo = to; };
            var row = Child(scene, scene.Root, 0);
            var child = Child(scene, row, 0);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(40, 15), 0, 0) });
            bool pressedChild = (scene.Flags(child) & NodeFlags.Pressed) != 0 && disp.Drag.IsArmed;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(50, 15), 0, 0) });   // dx 10 > 4 → promote
            bool promoted = started == 1 && deltas == 1 && firstEvent == 1
                && disp.Drag.IsActive && disp.Drag.ActiveNode == row
                && (scene.Flags(child) & NodeFlags.Pressed) == 0;                  // pressed visuals cleared on promotion
            bool visuals = Near(scene.Paint(row).Opacity, 0.80f)                   // ListViewItemDragThemeOpacity
                && scene.TryGetShadow(row, out var sh) && sh == DragController.DragShadow
                && (scene.Flags(row) & NodeFlags.HitTestVisible) == 0              // drop-targets see THROUGH the visual
                && Near(scene.Paint(row).LocalTransform.Dx, 10f) && Near(scene.Paint(row).LocalTransform.Dy, 0f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 40), 0, 0) });
            bool follows = deltas == 2
                && Near(scene.Paint(row).LocalTransform.Dx, 30f) && Near(scene.Paint(row).LocalTransform.Dy, 25f);
            Check("e5dragdrop.2 over-threshold promotes the CanDrag row (child press arms it): Started→Delta, pressed cleared, drag visuals on",
                pressedChild && promoted && visuals && follows,
                $"pressed={pressedChild} promoted={promoted} visuals={visuals} follows={follows}");

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(70, 40), 0, 0) });
            bool suppressed = rowClicks == 0 && childClicks == 0;                  // WinUI: a finished drag never clicks
            bool restored = Near(scene.Paint(row).Opacity, 1f) && !scene.TryGetShadow(row, out _)
                && (scene.Flags(row) & NodeFlags.HitTestVisible) != 0
                && scene.Paint(row).LocalTransform.Dx == 0f && scene.Paint(row).LocalTransform.Dy == 0f
                && !disp.Drag.IsActive && disp.Drag.ActiveNode.IsNull;
            bool settled = completed == 1 && Near(doneDx, 30f) && Near(doneDy, 25f)
                && settles == 1 && settleNode == row
                && Near(settleFrom.X - settleTo.X, 30f) && Near(settleFrom.Y - settleTo.Y, 25f);
            Check("e5dragdrop.2b release after a drag suppresses the click, restores resting visuals, and hands OnSettle the drop→resting rects",
                suppressed && restored && settled,
                $"suppressed={suppressed} restored={restored} completed={completed} settles={settles} dxdy=({doneDx:0.#},{doneDy:0.#})");
        }

        // e5dragdrop.3 — DragEventArgs: Total deltas measured from the arming press, Absolute = the raw pointer,
        // Local ≈ the grab offset on the MOVING box, and the ~50ms-EMA pointer velocity driven by PLATFORM timestamps
        // (alpha = dt/(dt+50): 10px/16ms moves → 625 px/s instantaneous → 151.5 then 266.3 px/s smoothed). A gesture
        // whose events carry TimestampMs == 0 (the headless default) leaves the velocity at 0.
        {
            var scene = new SceneStore();
            float vx = float.NaN, vy = float.NaN, dx = float.NaN, dy = float.NaN;
            Point2 local = default, abs = default;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 100, CanDrag = true,
                OnDragDelta = e => { vx = e.VelocityX; vy = e.VelocityY; dx = e.TotalDx; dy = e.TotalDy; local = e.Local; abs = e.Absolute; },
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_016) });
            bool firstMove = Near(dx, 10f) && Near(vx, 151.5f, 0.5f) && vy == 0f;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_032) });
            bool secondMove = Near(dx, 20f) && Near(dy, 0f) && Near(vx, 266.3f, 0.5f)
                && Near(abs.X, 70f) && Near(abs.Y, 50f)
                && Near(local.X, 50f) && Near(local.Y, 50f);   // grab offset: Local tracks the MOVING box
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(70, 50), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_048) });

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 50), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(80, 50), 0, 0) });
            bool zeroStamp = Near(dx, 30f) && vx == 0f && vy == 0f;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(80, 50), 0, 0) });
            Check("e5dragdrop.3 DragEventArgs coords + ~50ms-EMA velocity from platform timestamps (0-stamps leave velocity 0)",
                firstMove && secondMove && zeroStamp, $"first={firstMove} second={secondMove} zero={zeroStamp} vx={vx:0.#}");
        }

        // e5dragdrop.4 — cancel paths: Escape mid-drag (the most-modal gesture — WinUI drag cancel routes before any
        // other key handling) and window deactivation both abort the drag: resting visuals restore, OnDragCanceled
        // fires (never OnDragCompleted), OnSettle glides the visual home, and the still-down pointer's eventual
        // release does NOT click (a canceled drag never raises a click or a drop).
        {
            var scene = new SceneStore();
            int clicks = 0, canceled = 0, completed = 0, settles = 0;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 60, CanDrag = true,
                OnClick = () => clicks++,
                OnDragCompleted = _ => completed++,
                OnDragCanceled = () => canceled++,
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var node = scene.Root;
            disp.Drag.OnSettle = (_, _, _) => settles++;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(70, 30), 0, 0) });
            bool active1 = disp.Drag.IsActive && Near(scene.Paint(node).LocalTransform.Dx, 20f);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });
            bool escCancel = canceled == 1 && completed == 0 && settles == 1 && !disp.Drag.IsActive
                && scene.Paint(node).LocalTransform.Dx == 0f && Near(scene.Paint(node).Opacity, 1f)
                && !scene.TryGetShadow(node, out _) && (scene.Flags(node) & NodeFlags.HitTestVisible) != 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(70, 30), 0, 0) });
            bool noClick1 = clicks == 0;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(75, 30), 0, 0) });
            bool active2 = disp.Drag.IsActive;
            disp.Dispatch(new[] { new InputEvent(InputKind.WindowBlur, default, 0, 0) });
            bool blurCancel = canceled == 2 && settles == 2 && !disp.Drag.IsActive
                && scene.Paint(node).LocalTransform.Dx == 0f && Near(scene.Paint(node).Opacity, 1f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(75, 30), 0, 0) });
            bool noClick2 = clicks == 0 && completed == 0;
            Check("e5dragdrop.4 Escape / window-blur cancel the drag: visuals restore, DragCanceled fires, release does not click",
                active1 && escCancel && noClick1 && active2 && blurCancel && noClick2,
                $"esc={escCancel} blur={blurCancel} clicks={clicks} canceled={canceled} settles={settles}");
        }

        // e5dragdrop.5 — arena-lite (promotion-time arbitration, DragController.YieldsToPan): the item's reorder axis
        // is its PARENT container's main axis; a dominant-axis gesture PERPENDICULAR to it yields to a scrollable
        // ancestor that actually overflows along the gesture axis (the WinUI manipulation-arena outcome for a tab
        // strip inside a scrolling page) — the candidate silently disarms, no DragStarted. Along-axis gestures and
        // no-overflow scrollables never yield.
        {
            // a) horizontal strip (row ⇒ items drag horizontally) inside a vertically OVERFLOWING scroll viewport.
            var sceneA = new SceneStore();
            int startedA = 0;
            new TreeReconciler(sceneA, strings).ReconcileRoot(new ScrollEl
            {
                Width = 200, Height = 100,
                Content = new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0,
                            Children =
                            [
                                new BoxEl { Key = "a", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedA++ },
                                new BoxEl { Key = "b", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedA++ },
                                new BoxEl { Key = "c", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedA++ },
                            ],
                        },
                        new BoxEl { Key = "filler", Width = 10, Height = 300 },
                    ],
                },
            }, null);
            new FlexLayout(sceneA, fonts).Run(sceneA.Root);
            var dispA = new InputDispatcher(sceneA);
            sceneA.TryGetScroll(sceneA.Root, out var scA);
            bool overflows = scA.ContentH - scA.ViewportH > 0.5f;   // 340 content over a 100 viewport

            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(30, 20), 0, 0) });
            bool armedA = dispA.Drag.IsArmed;
            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(30, 60), 0, 0) });   // dy 40 ⊥ the row axis
            bool yielded = startedA == 0 && !dispA.Drag.IsActive && !dispA.Drag.IsArmed;                 // the pan owns it
            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(30, 60), 0, 0) });

            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(30, 20), 0, 0) });
            dispA.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(80, 20), 0, 0) });   // dx 50 along the row axis
            bool alongDrags = startedA == 1 && dispA.Drag.IsActive;
            dispA.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });

            // b) the same strip in a NON-overflowing viewport: the vertical gesture has no pan to yield to → it drags.
            var sceneB = new SceneStore();
            int startedB = 0;
            new TreeReconciler(sceneB, strings).ReconcileRoot(new ScrollEl
            {
                Width = 200, Height = 100,
                Content = new BoxEl
                {
                    Direction = 0,
                    Children =
                    [
                        new BoxEl { Key = "a", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedB++ },
                        new BoxEl { Key = "b", Width = 60, Height = 40, CanDrag = true, OnDragStarted = _ => startedB++ },
                    ],
                },
            }, null);
            new FlexLayout(sceneB, fonts).Run(sceneB.Root);
            var dispB = new InputDispatcher(sceneB);
            dispB.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(30, 20), 0, 0) });
            dispB.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(30, 60), 0, 0) });
            bool noOverflowDrags = startedB == 1 && dispB.Drag.IsActive;
            dispB.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });

            // c) a vertical (column) list inside the overflowing vertical viewport: the vertical gesture runs ALONG
            //    the item's own reorder axis → the drag wins even over a real pan candidate.
            var sceneC = new SceneStore();
            int startedC = 0;
            new TreeReconciler(sceneC, strings).ReconcileRoot(new ScrollEl
            {
                Width = 200, Height = 100,
                Content = new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        new BoxEl { Key = "a", Width = 160, Height = 60, CanDrag = true, OnDragStarted = _ => startedC++ },
                        new BoxEl { Key = "b", Width = 160, Height = 60, CanDrag = true, OnDragStarted = _ => startedC++ },
                        new BoxEl { Key = "c", Width = 160, Height = 60, CanDrag = true, OnDragStarted = _ => startedC++ },
                    ],
                },
            }, null);
            new FlexLayout(sceneC, fonts).Run(sceneC.Root);
            var dispC = new InputDispatcher(sceneC);
            sceneC.TryGetScroll(sceneC.Root, out var scC);
            bool overflowsC = scC.ContentH - scC.ViewportH > 0.5f;   // 180 content over a 100 viewport
            dispC.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(80, 30), 0, 0) });
            dispC.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(80, 70), 0, 0) });
            bool axisDrags = startedC == 1 && dispC.Drag.IsActive;
            dispC.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Escape) });

            Check("e5dragdrop.5 arena-lite: cross-axis gesture over an overflowing scrollable yields to the pan; along-axis and no-overflow gestures drag",
                overflows && armedA && yielded && alongDrags && noOverflowDrags && overflowsC && axisDrags,
                $"overflow={overflows} yielded={yielded} along={alongDrags} noOverflow={noOverflowDrags} axis={axisDrags}");
        }

        // e5dragdrop.6 — ReorderList midpoint slot math: the dragged item's centre crossing a sibling's midpoint
        // claims its slot (GetDragOverIndex — ListViewBase_Partial_Reorder.cpp:984-1063); the SHOWN target waits on
        // the 200ms live-reorder dwell that re-arms on every pending change (LISTVIEW_LIVEREORDER_TIMER :50, restart
        // :1068-1074); displaced items shift one dragged-extent (+spacing) toward the vacated slot.
        {
            var rl = new ReorderList();
            bool defaults = rl.DwellMs == ReorderList.ListDwellMs && ReorderList.ListDwellMs == 200f && ReorderList.GridDwellMs == 300f;
            rl.Begin(1, 5, itemExtent: 40f, spacing: 8f);     // starts 0,48,96,144,192; dragged centre 68
            bool init = rl.IsActive && rl.DraggedIndex == 1 && rl.PendingIndex == 1 && rl.TargetIndex == 1;
            bool stay = !rl.Update(47f) && rl.PendingIndex == 1;                       // 115 < sibling-2 mid 116
            bool cross = rl.Update(49f) && rl.PendingIndex == 2 && rl.TargetIndex == 1;   // 117 > 116; dwell pending
            bool dwellHeld = !rl.Advance(199f) && rl.TargetIndex == 1;
            bool dwellFire = rl.Advance(1f) && rl.TargetIndex == 2;
            bool hints = Near(rl.OffsetFor(2), -48f) && rl.OffsetFor(0) == 0f && rl.OffsetFor(1) == 0f
                && rl.OffsetFor(3) == 0f && rl.OffsetFor(4) == 0f;
            Span<int> order = stackalloc int[5];
            rl.ProjectOrder(order);
            bool proj = order[0] == 0 && order[1] == 2 && order[2] == 1 && order[3] == 3 && order[4] == 4;
            bool tgtStart = Near(rl.DraggedTargetStart, 96f);

            bool flip = rl.Update(-100f) && rl.PendingIndex == 0 && rl.TargetIndex == 2;   // centre −32 < sibling-0 mid 20
            bool reArm = !rl.Advance(199f) && rl.Advance(1f) && rl.TargetIndex == 0;       // the dwell re-armed in full
            bool upHints = Near(rl.OffsetFor(0), 48f) && rl.OffsetFor(2) == 0f;
            rl.ProjectOrder(order);
            bool upProj = order[0] == 1 && order[1] == 0 && order[2] == 2 && Near(rl.DraggedTargetStart, 0f);
            Check("e5dragdrop.6 ReorderList midpoint slot math + 200ms dwell-committed target + displacement hints + ProjectOrder",
                defaults && init && stay && cross && dwellHeld && dwellFire && hints && proj && tgtStart && flip && reArm && upHints && upProj,
                $"init={init} stay={stay} cross={cross} dwell={dwellHeld}/{dwellFire} hints={hints} proj={proj} flip={flip} reArm={reArm}");
        }

        // e5dragdrop.7 — drop commit: Complete() lands at the LATEST pending slot (the release point never waits for
        // the dwell), resets all hints BEFORE firing OnCommit (from,to in ORIGINAL indices), and ReorderList.Move
        // applies exactly WinUI's RemoveAt(from)+Insert(to) drop (ListViewBase::ReorderItemsTo —
        // ListViewBase_Partial_Reorder.cpp:1536-1537). Cancel drops the hints without committing. Variable extents
        // honor per-item midpoints; DwellMs = 0 commits the shown target on the next Advance.
        {
            var rl = new ReorderList { DwellMs = 0f };
            rl.Begin(0, new[] { 30f, 50f, 20f }, spacing: 4f);    // starts 0,34,88; dragged centre 15
            bool varStay = !rl.Update(40f);                       // 55 < sibling-1 mid 59
            bool varCross = rl.Update(45f) && rl.PendingIndex == 1;   // 60 > 59
            bool zeroDwell = rl.Advance(0f) && rl.TargetIndex == 1 && Near(rl.OffsetFor(1), -34f);
            rl.Cancel();
            bool dropped = !rl.IsActive && rl.OffsetFor(1) == 0f && rl.PendingIndex == -1;

            int commitFrom = -1, commitTo = -1; bool hintsClearedAtCommit = false;
            var rl2 = new ReorderList();
            rl2.OnCommit = (from, to) => { commitFrom = from; commitTo = to; hintsClearedAtCommit = rl2.TargetIndex == -1 && rl2.OffsetFor(1) == 0f; };
            rl2.Begin(0, 4, itemExtent: 40f);                     // starts 0,40,80,120; dragged centre 20
            rl2.Update(85f);                                      // 105 > mid-1 60 and > mid-2 100 → pending 2 (no Advance)
            int dest = rl2.Complete();
            bool commit = dest == 2 && commitFrom == 0 && commitTo == 2 && hintsClearedAtCommit && !rl2.IsActive;

            var list = new List<char> { 'a', 'b', 'c', 'd' };
            ReorderList.Move(list, 0, 2);
            bool moved = list[0] == 'b' && list[1] == 'c' && list[2] == 'a' && list[3] == 'd';
            ReorderList.Move(list, 0, 9);                         // out of range → ignored
            ReorderList.Move(list, 2, 2);                         // no-op
            bool guarded = list[0] == 'b' && list[1] == 'c' && list[2] == 'a' && list[3] == 'd';
            Check("e5dragdrop.7 Complete commits at the latest pending slot (hints reset before OnCommit); Move applies RemoveAt+Insert; Cancel drops",
                varStay && varCross && zeroDwell && dropped && commit && moved && guarded,
                $"varCross={varCross} zeroDwell={zeroDwell} dropped={dropped} dest={dest} commit=({commitFrom}->{commitTo}) moved={moved}");
        }

        // e5dragdrop.7b — the full pipeline: CanDrag rows wired to ReorderList through the drag lifecycle (Begin in
        // OnDragStarted, Update(e.TotalDy) in OnDragDelta, Complete in OnDragCompleted); dragging row 0 past row 1's
        // midpoint and releasing commits the collection move 0→1 via OnCommit + ReorderList.Move.
        {
            var scene = new SceneStore();
            var items = new List<int> { 0, 1, 2 };
            var rl = new ReorderList();
            rl.OnCommit = (from, to) => ReorderList.Move(items, from, to);
            var children = new Element[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                children[i] = new BoxEl
                {
                    Key = "row" + i, Width = 120, Height = 40, CanDrag = true,
                    OnDragStarted = _ => rl.Begin(idx, 3, itemExtent: 40f),
                    OnDragDelta = e => rl.Update(e.TotalDy),
                    OnDragCompleted = _ => rl.Complete(),
                    OnDragCanceled = rl.Cancel,
                };
            }
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl { Direction = 1, Children = children }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(60, 20), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 40), 0, 0) });   // promote; centre 40 < mid-1 60
            bool pendingHome = rl.IsActive && rl.DraggedIndex == 0 && rl.PendingIndex == 0;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 67), 0, 0) });   // centre 67 > mid-1 60 → pending 1
            bool pendingNext = rl.PendingIndex == 1;
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(60, 67), 0, 0) });
            bool committed = !rl.IsActive && items[0] == 1 && items[1] == 0 && items[2] == 2;
            Check("e5dragdrop.7b end-to-end: dragging row 0 past row 1's midpoint commits the reorder through the drag lifecycle",
                pendingHome && pendingNext && committed,
                $"home={pendingHome} next={pendingNext} items=[{string.Join(",", items)}]");
        }

        // e5dragdrop.8 — steady-state drag dispatch is allocation-free: the controller reuses ONE DragEventArgs for
        // the whole gesture and the move path writes only scene columns (no closures, no boxing).
        {
            var scene = new SceneStore();
            float lastDx = 0f;
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 400, Height = 60, CanDrag = true, OnDragDelta = e => lastDx = e.TotalDx,
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var ev = new InputEvent[1];
            ev[0] = new InputEvent(InputKind.PointerDown, new Point2(50, 30), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000);
            disp.Dispatch(ev);
            for (int i = 1; i <= 6; i++)   // promote + warm the move path (shadow row, EMA, transform writes)
            {
                ev[0] = new InputEvent(InputKind.PointerMove, new Point2(50 + i * 10, 30), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000 + (uint)(i * 16));
                disp.Dispatch(ev);
            }
            ev[0] = new InputEvent(InputKind.PointerMove, new Point2(140, 30), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_200);
            long before = GC.GetAllocatedBytesForCurrentThread();
            disp.Dispatch(ev);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check("e5dragdrop.8 steady drag-move dispatch allocates 0 bytes (one reused DragEventArgs per gesture)",
                bytes == 0 && Near(lastDx, 90f), $"{bytes} bytes dx={lastDx:0.#}");
        }

        // e5dragdrop.8b — the whole drag FRAME at pointer rate is 0-alloc on phases 6–13: a drag move never
        // reconciles or relayouts (LocalTransform + dirty flags only), and the record/submit of the lifted visual
        // (0.80 opacity + shadow) reuses pooled storage.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-alloc", new Size2(480, 320), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var root = new DragFrameProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);

            host.RunFrame();   // mount + layout
            var item = Child(host.Scene, host.Scene.Root, 0);
            var c = CenterOf(host.Scene, item);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000));
            host.RunFrame();
            for (int i = 1; i <= 12; i++)   // promote, then warm: shadow slab, draw-list growth, eased press/hover settle
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + i * 4, c.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000 + (uint)(i * 16)));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 60, c.Y), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_300));
            var dragFrame = host.RunFrame();
            bool zero = dragFrame.HotPhaseAllocBytes == 0;
            // E5b spring-lag follow (the adopted Flutter/rbd ghost feel): with real timestamps the lifted visual EASES
            // toward the pointer instead of snapping, so the transform reaches 60 only once the spring settles —
            // assert the SETTLED position, while the 0-alloc gate stays on the immediate pointer-rate drag frame.
            for (int i = 0; i < 30; i++) host.RunFrame();
            bool tracked = Near(root.LastTotalDx, 60f) && Near(host.Scene.Paint(item).LocalTransform.Dx, 60f, 1.5f)
                && Near(host.Scene.Paint(item).Opacity, 0.80f);
            Check("e5dragdrop.8b steady drag frame is 0-alloc on phases 6–13 (transform-only repaint of the lifted visual)",
                zero && tracked, $"{dragFrame.HotPhaseAllocBytes} bytes dx={root.LastTotalDx:0.#} tdx={host.Scene.Paint(item).LocalTransform.Dx:0.#}");
        }

        // e5dragdrop.touch — L2 parity on the touch path (bug E4). A horizontal touch drag along the item axis resolves
        // DragReorder in the §7A arena; the claim must ALSO open the typed session (TryBegin), drive it per move
        // (Enter/Over on the target under the contact) and pair TryDrop with Complete on release — exactly the mouse
        // PointerMove/PointerUp branches. Before this the touch path was L1-only: no drop ever fired and the session
        // (with its global spotlight dim) leaked for the rest of the process.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-touch-l2", new Size2(500, 340), 1f)); window.Show();
            var probe = new TouchDragDropProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var scroller = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(scroller, out var tsc);
            var strip = Child(scene, tsc.ContentNode, 0);
            var source = Child(scene, strip, 0);          // "a" — the typed drag source
            var target = Child(scene, strip, 2);          // "c" — the spotlight drop target
            var from = CenterOf(scene, source);
            var to = CenterOf(scene, target);

            TouchGesture(window, host, from, new Point2(to.X, from.Y), 12, pointerId: 71, msPerStep: 16f);

            bool sessionRan = probe.Enters >= 1 && probe.Overs >= 1;
            bool dropped = probe.Drops == 1 && (probe.DroppedPayload as string) == "payload";
            bool closed = !host.Input.DragDrop.IsActive && !host.Input.Drag.IsActive
                          && host.Input.DragDrop.OverTarget.IsNull && !scene.DropSpotlightActive;
            Check("e5dragdrop.touch an arena-claimed TOUCH drag-reorder drives the full L2 session (TryBegin at the claim, Move per contact move, TryDrop paired with Complete on lift): the target sees Enter/Over/Drop and the session + drop spotlight close on release",
                sessionRan && dropped && closed,
                $"enter={probe.Enters} over={probe.Overs} leave={probe.Leaves} drop={probe.Drops} payload={probe.DroppedPayload ?? "null"} l2Active={host.Input.DragDrop.IsActive} l1Active={host.Input.Drag.IsActive} spotlight={scene.DropSpotlightActive}");
        }

        // e5dragdrop.prune — a virtualized-away / rebuilt source node (bug E10). The dragged node's OnDragCanceled column
        // dies with its slot, so PruneDead used to Reset() SILENTLY: the L2 session (and its spotlight) outlived the
        // gesture forever. The L2 source here is an ANCESTOR that survives the rebuild, so DragDropContext.PruneDead's
        // own dead-source guard cannot close it — only the new OnAbandoned notification can.
        {
            var scene = new SceneStore();
            int abandoned = 0;
            var rec = new TreeReconciler(scene, strings);
            var source = new DragSource("res", static () => "p");
            var sinkSpec = new DropTargetSpec(["res"]) { VisualPolicy = DropTargetVisualPolicy.Spotlight };
            Element Sink() => new BoxEl { Key = "sink", Width = 200, Height = 60, DropTarget = sinkSpec };
            var before = new BoxEl
            {
                Width = 300, Height = 200, Draggable = source,
                Children = [new BoxEl { Key = "row", Width = 200, Height = 60, CanDrag = true }, Sink()],
            };
            var after = new BoxEl { Width = 300, Height = 200, Draggable = source, Children = [Sink()] };
            rec.ReconcileRoot(before, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.Drag.OnAbandoned += () => abandoned++;   // append: the dispatcher's own DragDrop.Cancel wiring stays
            var row = Child(scene, scene.Root, 0);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(140, 30), 0, 0) });   // promote + TryBegin
            bool lifted = disp.Drag.IsActive && disp.Drag.ActiveNode == row
                          && disp.DragDrop.IsActive && scene.DropSpotlightActive;

            rec.ReconcileRoot(after, before);   // in-place diff: the dragged row is freed, the L2 source ancestor survives
            new FlexLayout(scene, fonts).Run(scene.Root);
            bool rowDead = !scene.IsLive(row);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(150, 30), 0, 0) });   // dispatch-start PruneDead

            bool closed = abandoned == 1 && !disp.Drag.IsActive && !disp.DragDrop.IsActive
                          && !scene.DropSpotlightActive && scene.DragGhost.IsNull;
            Check("e5dragdrop.prune a drag whose node is freed by a reconcile reports OnAbandoned (its own OnDragCanceled column is dead), which closes the L2 session and clears the drop spotlight instead of leaking them for the process",
                lifted && rowDead && closed,
                $"lifted={lifted} rowDead={rowDead} abandoned={abandoned} l1={disp.Drag.IsActive} l2={disp.DragDrop.IsActive} spotlight={scene.DropSpotlightActive}");
        }

        // e5dragdrop.reassert — a mid-drag reconcile commit re-applies the dragged row's AUTHORED opacity/hit-test
        // (ApplyBox writes them unconditionally), and Tick cannot repair it: a settled/snap gesture early-outs before
        // ApplyPresented, so the frame RECORDS an un-lifted row (bug E9). The host re-asserts the ghost post-reconcile.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-reassert", new Size2(420, 320), 1f)); window.Show();
            var probe = new DragReconcileClobberProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var row = Child(host.Scene, host.Scene.Root, 0);
            var c = CenterOf(host.Scene, row);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 40f, c.Y + 24f), 0, 0));   // promote (0-stamp ⇒ snap-tracking)
            host.RunFrame();
            bool lifted = host.Input.Drag.IsActive && Near(host.Scene.Paint(row).Opacity, 0.80f)
                && Near(host.Scene.Paint(row).LocalTransform.Dx, 40f) && Near(host.Scene.Paint(row).LocalTransform.Dy, 24f);

            probe.Rev.Value = 1;      // re-render the row mid-drag: NO pointer move accompanies the commit
            host.RunFrame();
            bool held = Near(host.Scene.Paint(row).Opacity, 0.80f)
                && (host.Scene.Flags(row) & NodeFlags.DragGhost) != 0
                && (host.Scene.Flags(row) & NodeFlags.HitTestVisible) == 0
                && host.Scene.DragGhost == row
                && Near(host.Scene.Paint(row).LocalTransform.Dx, 40f) && Near(host.Scene.Paint(row).LocalTransform.Dy, 24f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 40f, c.Y + 24f), 0, 0));
            host.RunFrame();
            bool restored = Near(host.Scene.Paint(row).Opacity, 1f) && !host.Input.Drag.IsActive
                && (host.Scene.Flags(row) & NodeFlags.DragGhost) == 0;
            Check("e5dragdrop.reassert a reconcile commit that re-renders the dragged row mid-drag does not clobber the ghost for a frame — the host re-asserts translate/opacity/hit-test/DragGhost after the reconcile, with no pointer move, and release still restores the resting visuals",
                lifted && held && restored,
                $"lifted={lifted} held={held} restored={restored} op={host.Scene.Paint(row).Opacity:0.00}");
        }

        // e5dragdrop.hidesource — the OTHER half of the re-assert (Wave 4 residual). A Stationary source's dim and a
        // same-list insertion's VIRTUAL REMOVAL both own the press-source row's opacity, and they disagree: the
        // insertion hides the whole dragged block (0 — those rows are in the chip), while ReassertPresented re-writes
        // the source style's 0.4 after every mid-drag reconcile, AFTER the frame's animation compose. The visible bug
        // was one row strobing back to 0.4 while its siblings stayed hidden. SceneStore.DragSourceOpacityOverride is
        // the destination's declaration of ownership; without it the re-assert wins the frame it runs on.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-hidesource", new Size2(420, 320), 1f)); window.Show();
            var probe = new DragStationaryClobberProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var row = Child(scene, scene.Root, 0);
            var c = CenterOf(scene, row);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 40f, c.Y + 24f), 0, 0));
            host.RunFrame();
            // Stationary: dimmed in place, never hoisted into the ghost band.
            bool dimmed = host.Input.Drag.IsActive && Near(scene.Paint(row).Opacity, Drag.SourceDimOpacity)
                          && (scene.Flags(row) & NodeFlags.DragGhost) == 0 && scene.DragGhost.IsNull;

            scene.DragSourceOpacityOverride = 0f;   // what a same-list insertion publishes while it hides its sources
            probe.Rev.Value = 1;                    // reconcile the row mid-drag, with NO pointer move
            host.RunFrame();
            bool hidden = Near(scene.Paint(row).Opacity, 0f) && host.Input.Drag.IsActive;

            scene.DragSourceOpacityOverride = null; // the pointer left the insertion: the source's own dim is back
            probe.Rev.Value = 2;
            host.RunFrame();
            bool redimmed = Near(scene.Paint(row).Opacity, Drag.SourceDimOpacity);

            // A destination whose teardown never ran must not leave the override latched onto the next gesture.
            scene.DragSourceOpacityOverride = 0f;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 40f, c.Y + 24f), 0, 0));
            host.RunFrame();
            bool released = Near(scene.Paint(row).Opacity, 1f) && !host.Input.Drag.IsActive
                            && scene.DragSourceOpacityOverride is null;

            Check("e5dragdrop.hidesource a destination that virtually removes the dragged rows owns the Stationary source's opacity: the post-reconcile re-assert writes SceneStore.DragSourceOpacityOverride instead of the source style's dim, restores the dim when the override clears, and the gesture's end releases the override even if the destination never tore down",
                dimmed && hidden && redimmed && released,
                $"dimmed={dimmed} hidden={hidden} redimmed={redimmed} released={released} op={scene.Paint(row).Opacity:0.00}");
        }

        // e5dragdrop.animconflict — the anim slab and the drag both wrote LocalTransform/Opacity (bug E13): a hover/press
        // MotionTarget on a dragged card fought the lift, and because DragController re-anchors off the node's CURRENT
        // resting origin the stomped translate double-counted into a per-frame runaway. Compose now skips both channels
        // for a DragGhost node — the drag owns them for the gesture's duration, the animation before and after it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-animconflict", new Size2(420, 320), 1f)); window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new DragAnimConflictProbe());
            host.RunFrame();
            var scene = host.Scene;
            var row = Child(scene, scene.Root, 0);
            var c = CenterOf(scene, row);

            // A long linear RAMP on both contested channels: if the slab still owned them the values would keep drifting
            // under a stationary pointer, so "constant across held frames" is the ownership proof (no arithmetic on the
            // composed matrix required).
            host.Animation.Animate(row, AnimChannel.TranslateY, 0f, -120f, 6000f, Easing.Linear);
            host.Animation.Animate(row, AnimChannel.Opacity, 1f, 0.2f, 6000f, Easing.Linear);
            for (int i = 0; i < 4; i++) host.RunFrame();
            float animDyBefore = scene.Paint(row).LocalTransform.Dy;
            for (int i = 0; i < 4; i++) host.RunFrame();
            bool animLive = scene.Paint(row).LocalTransform.Dy < animDyBefore - 0.5f && scene.Paint(row).Opacity < 0.99f;

            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 50f, c.Y), 0, 0));   // promote
            host.RunFrame();
            float heldDx = scene.Paint(row).LocalTransform.Dx, heldDy = scene.Paint(row).LocalTransform.Dy;
            bool dragOwns = host.Input.Drag.IsActive && Near(scene.Paint(row).Opacity, 0.80f) && Near(heldDx, 50f);
            for (int i = 0; i < 8; i++) host.RunFrame();   // pointer held still while the ramp keeps running
            bool stationary = Near(scene.Paint(row).LocalTransform.Dx, heldDx, 0.01f)
                && Near(scene.Paint(row).LocalTransform.Dy, heldDy, 0.01f)
                && Near(scene.Paint(row).Opacity, 0.80f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 50f, c.Y), 0, 0));
            host.RunFrame();
            float afterDy = scene.Paint(row).LocalTransform.Dy;
            for (int i = 0; i < 6; i++) host.RunFrame();
            bool animResumed = !host.Input.Drag.IsActive
                && scene.Paint(row).LocalTransform.Dy < afterDy + 0.01f && scene.Paint(row).Opacity < 0.99f
                && Near(scene.Paint(row).LocalTransform.Dx, 0f);
            Check("e5dragdrop.animconflict a node carrying a live anim-slab transform/opacity ramp hands both channels to the drag on promotion (the ghost holds still under a stationary pointer at 0.80 opacity instead of drifting with the ramp) and the slab takes them back after Complete",
                animLive && dragOwns && stationary && animResumed,
                $"animLive={animLive} dragOwns={dragOwns}(dx={heldDx:0.##} op={scene.Paint(row).Opacity:0.00}) stationary={stationary} resumed={animResumed}");
        }

        DragChipChecks();
    }

    /// <summary>Ancestor-chain containment (the preview layer's container sits under its component host node, not
    /// directly under the root).</summary>
    static bool IsDescendantOf(SceneStore scene, NodeHandle node, NodeHandle ancestor)
    {
        for (var n = scene.Parent(node); !n.IsNull; n = scene.Parent(n))
            if (n == ancestor) return true;
        return false;
    }

    // ── Wave 2: the chip system (DragLift.Stationary + the DragOverlay band + the declarative facade) ─────────────
    static void DragChipChecks()
    {
        var strings = new StringTable();
        var fonts = new HeadlessFontSystem(strings);

        // e5dragdrop.chip.stationary — the Stationary lift touches EXACTLY two channels on the source: its opacity
        // (dimmed "it's in the chip") and its hit-test bit (so drop discovery sees through it). No translate, no
        // shadow, no NodeFlags.DragGhost, no SceneStore.DragGhost — the whole ghost-band machinery stays idle, which
        // is what makes the chip immune to the ghost's clipping/blend/clamp failures. Release restores both.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 300, Height = 200,
                Children =
                [
                    new BoxEl
                    {
                        Key = "row", Width = 200, Height = 60, CanDrag = true,
                        Draggable = Drag.Source("chip", static () => "p"),
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var row = Child(scene, scene.Root, 0);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(160, 70), 0, 0) });   // promote
            var lifted = scene.Paint(row);
            bool stationary = disp.Drag.IsActive && disp.Drag.ActiveLift == DragLift.Stationary
                && lifted.LocalTransform.Dx == 0f && lifted.LocalTransform.Dy == 0f
                && (scene.Flags(row) & NodeFlags.DragGhost) == 0 && scene.DragGhost.IsNull
                && !scene.TryGetShadow(row, out _)
                && Near(lifted.Opacity, Drag.SourceDimOpacity)
                && (scene.Flags(row) & NodeFlags.HitTestVisible) == 0;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(280, 150), 0, 0) });
            bool stayedPut = scene.Paint(row).LocalTransform.Dx == 0f && scene.Paint(row).LocalTransform.Dy == 0f
                && Near(scene.Paint(row).Opacity, Drag.SourceDimOpacity);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(280, 150), 0, 0) });
            bool restored = Near(scene.Paint(row).Opacity, 1f) && !disp.Drag.IsActive
                && (scene.Flags(row) & NodeFlags.HitTestVisible) != 0
                && scene.Paint(row).LocalTransform.Dx == 0f;
            Check("e5dragdrop.chip.stationary a DragLift.Stationary source is dimmed + hit-test-transparent IN PLACE — never translated, never shadowed, never hoisted into the DragGhost band — and release restores both channels",
                stationary && stayedPut && restored,
                $"stationary={stationary} stayedPut={stayedPut} restored={restored} op={scene.Paint(row).Opacity:0.00} ghost={scene.DragGhost.IsNull}");
        }

        // e5dragdrop.chip.compositor — THE contract that makes the chip affordable: with a DragPreviewLayer mounted and
        // a Stationary drag live, a pointer-move frame re-renders ZERO components and allocates ZERO bytes on phases
        // 6–13. The chip follows through a BOUND transform over the engine's drag-position signals; the drag epoch
        // (which does re-render the preview) is edge-triggered, so a move that changes no target/effect/caption is a
        // pure compositor write. Before this, AppHost bumped the epoch EVERY frame while a drag was live.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-chip-compositor", new Size2(480, 320), 1f)); window.Show();
            var probe = new ChipDragProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var column = Child(scene, scene.Root, 0);
            var src = Child(scene, column, 0);
            // The mounted DragPreviewLayer registers its own container as the engine's overlay band root.
            bool registered = !scene.DragOverlay.IsNull && scene.IsLive(scene.DragOverlay)
                              && scene.DragOverlay != column && IsDescendantOf(scene, scene.DragOverlay, scene.Root);

            var c = CenterOf(scene, src);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f, c.Y), 0, 0));   // promote + mount the chip
            host.RunFrame();
            bool chipUp = host.Input.Drag.IsActive && host.Input.DragDrop.IsActive
                          && !scene.FirstChild(scene.DragOverlay).IsNull;

            // Warm: the chip's mount render, its Enter spring, draw-list growth and text shaping all settle here.
            for (int i = 1; i <= 24; i++)
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f + i, c.Y), 0, 0));
                host.RunFrame();
            }
            var wrapper = scene.FirstChild(scene.DragOverlay);
            float beforeDx = scene.Paint(wrapper).LocalTransform.Dx;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 60f, c.Y), 0, 0));
            var frameA = host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 66f, c.Y), 0, 0));
            var frameB = host.RunFrame();
            float afterDx = scene.Paint(wrapper).LocalTransform.Dx;

            bool quiet = frameA.ComponentsRendered == 0 && frameB.ComponentsRendered == 0
                         && frameA.HotPhaseAllocBytes == 0 && frameB.HotPhaseAllocBytes == 0;
            bool followed = afterDx > beforeDx + 1f;      // the chip actually tracked the pointer over those frames
            bool sourceHeld = Near(scene.Paint(src).Opacity, Drag.SourceDimOpacity)
                              && scene.Paint(src).LocalTransform.Dx == 0f;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 66f, c.Y), 0, 0));
            host.RunFrame();
            Check("e5dragdrop.chip.compositor a drag-move frame with a mounted DragPreviewLayer re-renders 0 components and allocates 0 bytes on phases 6-13 — the chip follows via a bound transform over the engine drag-position signals, and the drag epoch only bumps on target/effect/caption edges",
                registered && chipUp && quiet && followed && sourceHeld,
                $"registered={registered} chipUp={chipUp} rendersA={frameA.ComponentsRendered} rendersB={frameB.ComponentsRendered} allocA={frameA.HotPhaseAllocBytes}B allocB={frameB.HotPhaseAllocBytes}B dx {beforeDx:0.#}->{afterDx:0.#} srcHeld={sourceHeld}");
        }

        // e5dragdrop.chip.band — band ORDER in the recorded DrawList: main pass, then the DragGhost band, then the
        // DragOverlay band. The chip therefore paints above every clipped surface AND above a legacy lifted ghost,
        // which is the whole point of hoisting it out of the tree (dnd-kit's DragOverlay).
        {
            var scene = new SceneStore();
            ColorF mainFill = ColorF.FromRgba(0x11, 0x22, 0x33);
            ColorF ghostFill = ColorF.FromRgba(0x44, 0x55, 0x66);
            ColorF chipFill = ColorF.FromRgba(0x77, 0x88, 0x99);
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 400, Height = 300, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Key = "main", Width = 100, Height = 40, Fill = mainFill },
                    new BoxEl { Key = "ghost", Width = 100, Height = 40, Fill = ghostFill },
                    new BoxEl { Key = "chip", Width = 100, Height = 40, Fill = chipFill },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ghostNode = Child(scene, scene.Root, 1);
            var chipNode = Child(scene, scene.Root, 2);
            scene.Flags(ghostNode) |= NodeFlags.DragGhost;
            scene.DragGhost = ghostNode;
            scene.DragOverlay = chipNode;

            var dl = new DrawList();
            SceneRecorder.Record(scene, dl);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
            int iMain = -1, iGhost = -1, iChip = -1;
            for (int i = 0; i < dev.LastRects.Count; i++)
            {
                var col = dev.LastRects[i].Fill;
                if (col == mainFill) iMain = i;
                else if (col == ghostFill) iGhost = i;
                else if (col == chipFill) iChip = i;
            }
            bool ordered = iMain >= 0 && iGhost > iMain && iChip > iGhost;
            bool once = dev.LastRects.Count(r => r.Fill == chipFill) == 1;   // hoisted, not ALSO drawn in the main pass
            Check("e5dragdrop.chip.band the DragOverlay subtree records in its own top band AFTER the main pass and AFTER the DragGhost band (and exactly once — it is skipped in the clipped main pass)",
                ordered && once, $"main={iMain} ghost={iGhost} chip={iChip} chipDraws={dev.LastRects.Count(r => r.Fill == chipFill)}");
        }

        // e5dragdrop.chip.clamp — the chip is clamped to the window (dnd-kit restrictToWindowEdges): dragging into the
        // bottom-right corner must not push it half off-screen (screenshot S4's clipped ghost).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-chip-clamp", new Size2(480, 320), 1f)); window.Show();
            var probe = new ChipDragProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var src = Child(scene, Child(scene, scene.Root, 0), 0);
            var c = CenterOf(scene, src);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f, c.Y), 0, 0));
            host.RunFrame();
            for (int i = 0; i < 4; i++) host.RunFrame();   // let the chip measure (OnBoundsChanged feeds the clamp)
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(478f, 318f), 0, 0));   // into the corner
            host.RunFrame();
            host.RunFrame();

            var wrapper = scene.FirstChild(scene.DragOverlay);
            var chipRect = scene.AbsoluteRect(wrapper);
            var rootRect = scene.AbsoluteRect(scene.Root);
            float unclampedX = 478f + DragPreviewLayer.CursorOffsetX;
            bool inside = chipRect.W > 1f && chipRect.H > 1f
                          && chipRect.X + chipRect.W <= rootRect.X + rootRect.W + 0.5f
                          && chipRect.Y + chipRect.H <= rootRect.Y + rootRect.H + 0.5f;
            bool actuallyClamped = scene.Paint(wrapper).LocalTransform.Dx < unclampedX - 1f;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(478f, 318f), 0, 0));
            host.RunFrame();
            Check("e5dragdrop.chip.clamp the drag chip is clamped to the scene root — dragged into the bottom-right corner its measured box stays fully inside the window instead of clipping at the edge",
                inside && actuallyClamped,
                $"inside={inside} clamped={actuallyClamped} chip=({chipRect.X:0.#},{chipRect.Y:0.#},{chipRect.W:0.#},{chipRect.H:0.#}) root=({rootRect.W:0.#}x{rootRect.H:0.#})");
        }

        // e5dragdrop.chip.survive — a Stationary gesture OUTLIVES its source row. The chip is the visual and the
        // payload was resolved at promotion, so a virtualized-away / rebuilt source must not abort the drag (the E10
        // Ghost-mode abort is exactly wrong here): PruneDead reparents the session onto the scene root (the
        // ExternalBegin pattern) and the drop still commits on the target.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-chip-survive", new Size2(480, 320), 1f)); window.Show();
            var probe = new ChipDragProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var column = Child(scene, scene.Root, 0);
            var src = Child(scene, column, 0);
            var c = CenterOf(scene, src);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f, c.Y), 0, 0));
            host.RunFrame();
            bool began = host.Input.Drag.IsActive && host.Input.DragDrop.IsActive;

            probe.ShowSource.Value = false;   // the source row is freed by the reconcile, mid-gesture
            host.RunFrame();
            bool srcDead = !scene.IsLive(src);
            var sink = Child(scene, Child(scene, scene.Root, 0), 0);   // the sink is the only row left
            var sc = CenterOf(scene, sink);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(sc.X, sc.Y), 0, 0));
            host.RunFrame();
            bool aliveOverTarget = host.Input.Drag.IsActive && host.Input.DragDrop.IsActive
                                   && host.Input.DragDrop.OverTarget == sink
                                   && host.Input.Drag.SourceRecycled
                                   && host.Input.DragDrop.Session.Source == scene.Root;   // reparented, not cancelled
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(sc.X, sc.Y), 0, 0));
            host.RunFrame();
            bool committed = probe.Drops == 1 && (probe.DroppedPayload as string) == ChipDragProbe.PayloadValue
                             && !host.Input.Drag.IsActive && !host.Input.DragDrop.IsActive;
            Check("e5dragdrop.chip.survive a Stationary drag whose SOURCE row is freed mid-gesture stays alive (the chip carries it): PruneDead reparents the session onto the scene root instead of cancelling, and the drop still commits on the target",
                began && srcDead && aliveOverTarget && committed,
                $"began={began} srcDead={srcDead} alive={aliveOverTarget} drops={probe.Drops} payload={probe.DroppedPayload ?? "null"} l1={host.Input.Drag.IsActive} l2={host.Input.DragDrop.IsActive}");
        }

        // e5dragdrop.ghost.layer — GHOST-mode hardening (E2/E3). The lifted subtree composites as ONE opacity GROUP
        // (PushLayer{Opacity} at the ghost alpha, children at full per-primitive alpha) so its own text can no longer
        // double-blend against the row underneath (screenshot S3), and a styled Backplate fills an OPAQUE plate under
        // the whole subtree INSIDE that group — before any child draws.
        {
            var scene = new SceneStore();
            ColorF plate = ColorF.FromRgba(0x2A, 0x2B, 0x2C);
            ColorF childFill = ColorF.FromRgba(0x90, 0xA0, 0xB0);
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 400, Height = 300,
                Children =
                [
                    new BoxEl
                    {
                        Key = "row", Width = 200, Height = 60, CanDrag = true,
                        // Transparent row fill — the substrate the backplate exists for.
                        Draggable = new DragSource("chip", static () => "p")
                        {
                            Style = new DragVisualStyle { Lift = DragLift.Ghost, Opacity = 0.75f, Backplate = plate },
                        },
                        Children = [new BoxEl { Key = "label", Width = 120, Height = 20, Fill = childFill }],
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var row = Child(scene, scene.Root, 0);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(100, 30), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(140, 50), 0, 0) });   // promote
            bool grouped = scene.Paint(row).OpacityGroup && scene.DragGhost == row
                           && scene.DragGhostBackplate is { } bp && bp == plate;

            var dl = new DrawList();
            SceneRecorder.Record(scene, dl);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
            bool layer = false;
            foreach (var l in dev.LastLayers)
                if (l.Kind == (int)LayerKind.Opacity && Near(l.GroupAlpha, 0.75f)) layer = true;
            int iPlate = -1, iChild = -1;
            for (int i = 0; i < dev.LastRects.Count; i++)
            {
                if (dev.LastRects[i].Fill == plate) iPlate = i;
                else if (dev.LastRects[i].Fill == childFill) iChild = i;
            }
            bool plated = iPlate >= 0 && iChild > iPlate;
            bool balanced = dev.LayerBalance == 0;

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(140, 50), 0, 0) });
            bool cleared = !scene.Paint(row).OpacityGroup && scene.DragGhostBackplate is null && scene.DragGhost.IsNull;
            Check("e5dragdrop.ghost.layer a lifted ghost composites as one opacity GROUP at its ghost alpha (no per-primitive double-blend) with the styled Backplate filled opaquely UNDER the whole subtree inside that group; restore clears both",
                grouped && layer && plated && balanced && cleared,
                $"grouped={grouped} layer={layer} plate={iPlate} child={iChild} balance={dev.LayerBalance} cleared={cleared}");
        }

        // e5dragdrop.ghost.clamp — E6: the lifted ghost RECT is clamped to the scene root, so dragging far past the
        // window edge parks it flush against the edge instead of half-disappearing.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 300, Height = 200,
                Children = [new BoxEl { Key = "row", Width = 100, Height = 40, CanDrag = true }],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var row = Child(scene, scene.Root, 0);

            disp.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(50, 20), 0, 0) });
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(60, 30), 0, 0) });    // promote, still inside
            bool freeInside = Near(scene.Paint(row).LocalTransform.Dx, 10f) && Near(scene.Paint(row).LocalTransform.Dy, 10f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(900, 700), 0, 0) });  // way past both edges
            var t = scene.Paint(row).LocalTransform;
            bool clamped = Near(t.Dx, 200f) && Near(t.Dy, 160f);   // 300-100 and 200-40 from the row's (0,0) rest
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(-400, -300), 0, 0) });
            var t2 = scene.Paint(row).LocalTransform;
            bool clampedNeg = Near(t2.Dx, 0f) && Near(t2.Dy, 0f);
            disp.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(-400, -300), 0, 0) });
            Check("e5dragdrop.ghost.clamp the lifted ghost rect is clamped to the scene root on both axes (restrictToWindowEdges) — an unconstrained drag parks it flush against the edge instead of clipping off-window",
                freeInside && clamped && clampedNeg,
                $"free={freeInside} max=({t.Dx:0.#},{t.Dy:0.#}) min=({t2.Dx:0.#},{t2.Dy:0.#})");
        }

        // e5dragdrop.facade — the declarative surface (ruling f levels 1-3): Drag.Source ships the premiere DEFAULTS
        // (Stationary lift + the 0.4 source dim), and Drop.Target<T> unwraps the payload for the app — directly OR out
        // of a sortable list's ReorderPayload — gates acceptance on the TYPED predicate, and publishes the caption on
        // enter (which the engine clears on every target change, so a target never unsets it).
        {
            var src = Drag.Source("k", static () => "p");
            bool defaults = src.Style is { } st && st.Lift == DragLift.Stationary
                            && Near(st.Opacity, 0.4f) && st.Shadow is null && st.Backplate is null;
            bool ghostOptIn = Drag.Source("k", static () => "p", lift: DragLift.Ghost).Style!.Value.Lift == DragLift.Ghost;
            bool hidden = Near(Drag.SourceHidden("k", static () => "p").Style!.Value.Opacity, 0f);

            bool direct = Drop.TryUnwrap<string>("payload", out var d1) && d1 == "payload";
            var owner = new Reorderable("k");
            bool wrapped = Drop.TryUnwrap<string>(new ReorderPayload(owner, 3, "payload"), out var d2) && d2 == "payload";
            bool rejects = !Drop.TryUnwrap<string>(42, out _) && !Drop.TryUnwrap<string>(null, out _);

            var scene = new SceneStore();
            int enters = 0, drops = 0;
            string? dropped = null;
            var spec = Drop.Target<string>("k",
                accepts: static p => p != "no",
                onDrop: (p, _) => { drops++; dropped = p; },
                caption: static p => "Add " + p,
                onEnter: (_, _) => enters++);
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 200, Height = 100,
                Children = [new BoxEl { Key = "t", Width = 200, Height = 100, DropTarget = spec }],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);

            disp.DragDrop.ExternalBegin("k", "ok", new Point2(50, 50), KeyModifiers.None);
            disp.DragDrop.Move(scene.Root, new Point2(50, 50), 0f, 0f, KeyModifiers.None);
            var target = Child(scene, scene.Root, 0);
            disp.DragDrop.Move(target, new Point2(50, 50), 0f, 0f, KeyModifiers.None);
            bool captioned = enters == 1 && disp.DragDrop.Session.Caption == "Add ok";
            disp.DragDrop.TryDrop(new Point2(50, 50), KeyModifiers.None, out _);
            bool committed = drops == 1 && dropped == "ok" && disp.DragDrop.Session.Caption is null;

            // A payload the typed predicate refuses makes the target TRANSPARENT — it never enters, never drops.
            enters = 0; drops = 0;
            disp.DragDrop.ExternalBegin("k", "no", new Point2(50, 50), KeyModifiers.None);
            disp.DragDrop.Move(target, new Point2(50, 50), 0f, 0f, KeyModifiers.None);
            bool refusedTyped = enters == 0 && disp.DragDrop.OverTarget.IsNull;
            // …and so does a payload of the wrong TYPE, even on a matching kind (no silent accept-then-no-op drop).
            disp.DragDrop.Cancel();
            disp.DragDrop.ExternalBegin("k", 42, new Point2(50, 50), KeyModifiers.None);
            disp.DragDrop.Move(target, new Point2(50, 50), 0f, 0f, KeyModifiers.None);
            bool refusedType = enters == 0 && disp.DragDrop.OverTarget.IsNull;
            disp.DragDrop.Cancel();

            Check("e5dragdrop.facade Drag.Source ships the premiere defaults (Stationary lift + 0.4 source dim, Ghost still opt-in) and Drop.Target<T> unwraps direct AND ReorderPayload payloads, gates on the typed predicate, and sets the session caption on enter",
                defaults && ghostOptIn && hidden && direct && wrapped && rejects
                && captioned && committed && refusedTyped && refusedType,
                $"defaults={defaults} ghostOptIn={ghostOptIn} hidden={hidden} unwrap=({direct},{wrapped},{rejects}) caption={captioned} drop={committed} refuseValue={refusedTyped} refuseType={refusedType}");
        }

        // e5dragdrop.refusal — the S5 seam: a target that MATCHED the drag's kind but refused it through CanAccept is
        // deliberately transparent (discovery walks past it to an accepting ancestor), so it never becomes OverTarget
        // and none of its handlers fire. That is correct routing and terrible feedback: the user aims at the surface
        // the feature exists for and NOTHING happens — "cannot drop in this mode".
        //
        // The fix is one published fact, not a new event: while nothing on the chain accepts, the engine reports the
        // NEAREST kind-matched refuser as Session.RefusedTarget and its RefusalCaption as the session Caption. The
        // distinction from empty space is the whole point — DropEffect.None means BOTH "over a refuser" and "over
        // nothing", so a chip keyed on the effect alone would shout "not allowed" at every gap between targets.
        {
            var scene = new SceneStore();
            int refusedEnters = 0, okEnters = 0;
            var refusing = Drop.Target<string>("k",
                accepts: static p => p != "no",
                onEnter: (_, _) => refusedEnters++,
                refusalCaption: static _ => "Clear sorting to reorder");
            var accepting = Drop.Target<string>("k",
                caption: static _ => "Add",
                onEnter: (_, _) => okEnters++);
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 300, Height = 200,
                Children =
                [
                    new BoxEl { Key = "no", Width = 300, Height = 60, DropTarget = refusing },
                    new BoxEl { Key = "yes", Width = 300, Height = 60, DropTarget = accepting },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var refuser = Child(scene, scene.Root, 0);
            var acceptor = Child(scene, scene.Root, 1);
            var session = disp.DragDrop.Session;

            // ONE gesture whose payload the first target refuses and the second accepts — so every transition below is
            // exercised against the same live session, which is where the caption bookkeeping actually has to hold.
            disp.DragDrop.ExternalBegin("k", "no", new Point2(10, 10), KeyModifiers.None);
            // Over the REFUSER: no target entered, no effect — but the refusal and its reason are published.
            disp.DragDrop.Move(refuser, new Point2(10, 30), 0f, 0f, KeyModifiers.None);
            bool cued = session.RefusedTarget == refuser && session.Caption == "Clear sorting to reorder"
                        && session.OverTarget.IsNull && session.Effect == DropEffect.None && refusedEnters == 0;
            // Over NOTHING: silent. This is what keeps the not-allowed glyph meaningful.
            disp.DragDrop.Move(scene.Root, new Point2(10, 190), 0f, 0f, KeyModifiers.None);
            bool silentOverNothing = session.RefusedTarget.IsNull && session.Caption is null;
            // An ACCEPTING target is untouched by any of this: it enters, it captions, and it reports no refusal.
            disp.DragDrop.Move(acceptor, new Point2(10, 90), 0f, 0f, KeyModifiers.None);
            bool acceptUnaffected = okEnters == 1 && session.OverTarget == acceptor
                                    && session.RefusedTarget.IsNull && session.Caption == "Add";
            // Back onto the refuser: the accepted caption is REPLACED by the refusal's, never left stacked behind it.
            disp.DragDrop.Move(refuser, new Point2(10, 30), 0f, 0f, KeyModifiers.None);
            bool swapped = session.OverTarget.IsNull && session.RefusedTarget == refuser
                           && session.Caption == "Clear sorting to reorder";
            disp.DragDrop.Cancel();
            bool clearedAtEnd = session.RefusedTarget.IsNull && session.Caption is null;

            // …and the CHIP reads exactly that fact: the not-allowed glyph appears iff DragState.Refused.
            var spec = new DragChipSpec(Title: "Song", Count: 1);
            bool glyphOnRefusal = HasNotAllowedGlyph(DragChip.Render(spec,
                new DragState(true, "k", default, "no", DropEffect.None, "why", Refused: true)));
            bool noGlyphOverNothing = !HasNotAllowedGlyph(DragChip.Render(spec,
                new DragState(true, "k", default, "no", DropEffect.None)));
            bool noGlyphOverTarget = !HasNotAllowedGlyph(DragChip.Render(spec,
                new DragState(true, "k", default, "ok", DropEffect.Copy, "Add")));

            Check("e5dragdrop.refusal a kind-matched target refused by CanAccept publishes Session.RefusedTarget + its RefusalCaption (while still entering nothing), empty space publishes neither, an accepting target is unaffected, and the chip's not-allowed glyph keys on Refused — not on DropEffect.None, which cannot tell a refusal from a gap",
                cued && silentOverNothing && acceptUnaffected && swapped && clearedAtEnd
                && glyphOnRefusal && noGlyphOverNothing && noGlyphOverTarget,
                $"cued={cued} silent={silentOverNothing} accept={acceptUnaffected} swap={swapped} cleared={clearedAtEnd} glyph=({glyphOnRefusal},{noGlyphOverNothing},{noGlyphOverTarget})");
        }

        // e5dragdrop.chip.resting-caption — THE CHIP ALWAYS SAYS WHAT THE DROP WILL DO, including while travelling.
        // DragDropContext.Move clears the session caption on every target change, so most of a gesture — the part spent
        // between targets — had NO caption at all and the card named only the thing being dragged. The spec's
        // RestingCaption is the floor for exactly that phase; a live target's caption, and a refusal's reason, both win.
        {
            var spec = new DragChipSpec(Title: "Song", Subtitle: "Artist", Count: 1,
                                        RestingCaption: "Drag onto a playlist to add");

            // Travelling: nothing under the pointer ⇒ the resting verb is what the card says.
            bool restingShown = HasChipText(DragChip.Render(spec, new DragState(true, "k", default, "p")),
                                           "Drag onto a playlist to add");
            // Over an ACCEPTING target: the target's caption supersedes it, and the resting verb is gone (not stacked).
            var over = DragChip.Render(spec, new DragState(true, "k", default, "p", DropEffect.Copy, "Add to 90s Love Songs"));
            bool targetWins = HasChipText(over, "Add to 90s Love Songs")
                              && !HasChipText(over, "Drag onto a playlist to add");
            // REFUSED: the reason supersedes it too — a refusal must never be narrated as an invitation.
            var refusedChip = DragChip.Render(spec,
                new DragState(true, "k", default, "p", DropEffect.None, "You can't edit this playlist", Refused: true));
            bool refusalWins = HasChipText(refusedChip, "You can't edit this playlist")
                               && !HasChipText(refusedChip, "Drag onto a playlist to add");
            // A spec that supplies none keeps the previous behaviour exactly: no caption row while travelling.
            bool optIn = !HasChipText(DragChip.Render(new DragChipSpec(Title: "Song"), new DragState(true, "k", default, "p")),
                                      "Drag onto a playlist to add");

            Check("e5dragdrop.chip.resting-caption the chip states the drag's PURPOSE while travelling (DragChipSpec.RestingCaption), and a live target's caption / a refusal's reason both supersede it",
                restingShown && targetWins && refusalWins && optIn,
                $"resting={restingShown} targetWins={targetWins} refusalWins={refusalWins} optIn={optIn}");
        }

        // e5dragdrop.transparent — the OTHER kind of "no" (B2). CanAccept=false is a REFUSAL: the user aimed at this
        // surface and it owes them a reason, which is what the gate above publishes. DropTargetSpec.Transparent is
        // "this gesture is none of my business": a page body while the user reorders INSIDE its own list, a track
        // table on an album page that could never take a playlist edit. Refusing those wears a hard not-allowed glyph
        // over scenery the drag is merely PASSING OVER — an accusation with no direction. A transparent target must
        // therefore publish NEITHER acceptance NOR refusal, and the walk must continue to its ancestors exactly as if
        // it declared no target at all — including to an ancestor that refuses (whose reason IS the truthful one).
        {
            var scene = new SceneStore();
            bool transparentNow = true;
            int innerEnters = 0, outerEnters = 0;
            var innerAccepting = Drop.Target<string>("k",
                caption: static _ => "inner",
                onEnter: (_, _) => innerEnters++,
                transparent: _ => transparentNow);
            // The strongest form: a target that WOULD refuse (and has a reason ready) must stay silent while transparent.
            var innerRefusing = Drop.Target<string>("k",
                accepts: static _ => false,
                refusalCaption: static _ => "inner refuses",
                transparent: _ => transparentNow);
            var outerAccepting = Drop.Target<string>("k",
                caption: static _ => "outer accepts",
                onEnter: (_, _) => outerEnters++);
            var outerRefusing = Drop.Target<string>("k",
                accepts: static _ => false,
                refusalCaption: static _ => "outer refuses");
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 300, Height = 300,
                Children =
                [
                    new BoxEl { Key = "a", Width = 300, Height = 100, DropTarget = outerAccepting,
                        Children = [new BoxEl { Key = "ai", Width = 300, Height = 100, DropTarget = innerAccepting }] },
                    new BoxEl { Key = "r", Width = 300, Height = 100, DropTarget = outerRefusing,
                        Children = [new BoxEl { Key = "ri", Width = 300, Height = 100, DropTarget = innerAccepting }] },
                    new BoxEl { Key = "s", Width = 300, Height = 100, DropTarget = innerRefusing },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var session = disp.DragDrop.Session;
            var accInner = Child(scene, Child(scene, scene.Root, 0), 0);
            var accOuter = Child(scene, scene.Root, 0);
            var refInner = Child(scene, Child(scene, scene.Root, 1), 0);
            var refOuter = Child(scene, scene.Root, 1);
            var lone = Child(scene, scene.Root, 2);

            disp.DragDrop.ExternalBegin("k", "payload", new Point2(10, 10), KeyModifiers.None);
            // 1. Transparent inner over an ACCEPTING outer: the outer takes the drop, the inner never enters.
            disp.DragDrop.Move(accInner, new Point2(10, 50), 0f, 0f, KeyModifiers.None);
            bool passedToAcceptor = session.OverTarget == accOuter && outerEnters == 1 && innerEnters == 0
                                    && session.RefusedTarget.IsNull && session.Caption == "outer accepts";
            // 2. Transparent inner over a REFUSING outer: the refusal published is the OUTER's — the transparent
            //    target neither shadows it nor adds one of its own.
            disp.DragDrop.Move(refInner, new Point2(10, 150), 0f, 0f, KeyModifiers.None);
            bool passedToRefuser = session.OverTarget.IsNull && session.RefusedTarget == refOuter
                                   && session.Caption == "outer refuses" && innerEnters == 0;
            // 3. Transparent with NOTHING above it: silent, exactly like empty space — even though this spec carries a
            //    CanAccept that refuses and a RefusalCaption that would otherwise fire.
            disp.DragDrop.Move(lone, new Point2(10, 250), 0f, 0f, KeyModifiers.None);
            bool silent = session.OverTarget.IsNull && session.RefusedTarget.IsNull && session.Caption is null;
            // 4. Transparent=false is the pre-seam behaviour, unchanged: the inner accepts, the lone one refuses aloud.
            transparentNow = false;
            disp.DragDrop.Move(accInner, new Point2(10, 50), 0f, 0f, KeyModifiers.None);
            bool opaqueAccepts = session.OverTarget == accInner && innerEnters == 1 && session.Caption == "inner";
            disp.DragDrop.Move(lone, new Point2(10, 250), 0f, 0f, KeyModifiers.None);
            bool opaqueRefuses = session.OverTarget.IsNull && session.RefusedTarget == lone
                                 && session.Caption == "inner refuses";
            disp.DragDrop.Cancel();

            Check("e5dragdrop.transparent a target whose DropTargetSpec.Transparent holds for the live session is skipped ENTIRELY — it publishes neither acceptance nor its own RefusalCaption, and discovery continues to an accepting ancestor (which enters and captions) or to a REFUSING ancestor (whose reason is the one published); with no ancestor at all the surface is as silent as empty space, and a Transparent that returns false behaves exactly as before",
                passedToAcceptor && passedToRefuser && silent && opaqueAccepts && opaqueRefuses,
                $"toAcceptor={passedToAcceptor} toRefuser={passedToRefuser} silent={silent} opaqueAccept={opaqueAccepts} opaqueRefuse={opaqueRefuses} enters=({innerEnters},{outerEnters})");
        }

        // e5dragdrop.springload — the Finder/WinUI "hold a drag over a closed container and it opens itself" dwell.
        // Three shapes have to work, and the reason they are ONE gate is that they share one dwell host: an ACCEPTING
        // target (a sidebar folder you can also drop into), a target that REFUSES this payload (a folder that cannot
        // take these tracks — it still has to be openable, or the user is stuck outside the tree the drop lives in),
        // and a SpringLoadOnly waypoint (a tab: it takes no drop at all, and must not wear a not-allowed cue for it).
        // The once-per-Enter rule and the still-pointer keep-alive are the other two halves — a spring that re-fired
        // every frame would thrash the tree, and one the host let the loop idle through would never fire at all.
        {
            var scene = new SceneStore();
            int okFires = 0, refusedFires = 0, wayFires = 0;
            var accepting = Drop.Target<string>("k",
                onDrop: static (_, _) => { },
                springLoadMs: 500f, onSpringLoad: (_, _) => okFires++);
            var refusing = Drop.Target<string>("k",
                accepts: static p => p != "no",
                refusalCaption: static _ => "Clear sorting to reorder",
                springLoadMs: 500f, onSpringLoad: (_, _) => refusedFires++);
            var waypoint = Drop.Target<string>("k",
                springLoadMs: 500f, onSpringLoad: (_, _) => wayFires++, springLoadOnly: true);
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 300, Height = 300,
                Children =
                [
                    new BoxEl { Key = "ok", Width = 300, Height = 60, DropTarget = accepting },
                    new BoxEl { Key = "no", Width = 300, Height = 60, DropTarget = refusing },
                    new BoxEl { Key = "way", Width = 300, Height = 60, DropTarget = waypoint },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var dd = disp.DragDrop;
            var okNode = Child(scene, scene.Root, 0);
            var noNode = Child(scene, scene.Root, 1);
            var wayNode = Child(scene, scene.Root, 2);
            var session = dd.Session;

            dd.ExternalBegin("k", "no", new Point2(10, 10), KeyModifiers.None);
            // 1. ACCEPTING target: dwell accumulates across frames with a MOTIONLESS pointer, and HasActiveWork is what
            //    keeps those frames coming (an OS drag has no L1 gesture whose own keep-alive would cover it).
            dd.Move(okNode, new Point2(10, 30), 0f, 0f, KeyModifiers.None);
            bool armed = dd.HasActiveWork;
            bool tickWhileArmed = dd.Tick(100f);           // still counting down: the host is TOLD to keep going
            for (int i = 0; i < 3; i++) dd.Tick(100f);
            bool notYet = okFires == 0 && dd.HasActiveWork;
            bool tickOnFiring = dd.Tick(100f);             // the frame the 500ms lands on: fires, then disarms
            bool fired = okFires == 1;
            for (int i = 0; i < 5; i++) dd.Tick(100f);
            bool onceOnly = okFires == 1 && !dd.HasActiveWork && !tickOnFiring;

            // 2. Re-arm ONLY after leaving and coming back (a jitter inside the target keeps the old dwell).
            dd.Move(scene.Root, new Point2(10, 290), 0f, 0f, KeyModifiers.None);
            dd.Move(okNode, new Point2(12, 32), 0f, 0f, KeyModifiers.None);
            bool rearmed = dd.HasActiveWork && okFires == 1;
            for (int i = 0; i < 5; i++) dd.Tick(100f);
            bool refired = okFires == 2;

            // 3. A REFUSING target still springs: opening a container is navigation, not a drop. The refusal cue is
            //    untouched by it (the user is told both "not here" and — after the dwell — shown what is inside).
            dd.Move(noNode, new Point2(10, 90), 0f, 0f, KeyModifiers.None);
            bool refusedCue = session.OverTarget.IsNull && session.RefusedTarget == noNode
                              && session.Caption == "Clear sorting to reorder";
            for (int i = 0; i < 5; i++) dd.Tick(100f);
            bool refusedSprang = refusedFires == 1 && session.RefusedTarget == noNode;

            // 4. A SpringLoadOnly waypoint is silent in BOTH directions: it never accepts and never accuses.
            dd.Move(wayNode, new Point2(10, 150f), 0f, 0f, KeyModifiers.None);
            bool waySilent = session.OverTarget.IsNull && session.RefusedTarget.IsNull && session.Caption is null;
            for (int i = 0; i < 5; i++) dd.Tick(100f);
            bool waySprang = wayFires == 1;

            dd.Cancel();
            bool idle = !dd.HasActiveWork && !dd.Tick(1000f) && wayFires == 1;

            Check("e5dragdrop.springload a dwell on the nearest spring-configured target fires OnSpringLoad exactly once per Enter (re-arming only after a leave), keeps HasActiveWork true while counting down so a motionless pointer still gets frames, and works for an accepting target, a CanAccept-refuser (whose refusal cue is unchanged) and a SpringLoadOnly waypoint that neither accepts nor accuses",
                armed && notYet && tickWhileArmed && fired && onceOnly && rearmed && refired
                && refusedCue && refusedSprang && waySilent && waySprang && idle,
                $"armed={armed} notYet={notYet} tick={tickWhileArmed} fired={fired} once={onceOnly} rearm=({rearmed},{refired}) refuse=({refusedCue},{refusedSprang}) way=({waySilent},{waySprang}) idle={idle}");
        }

        // e5dragdrop.block — ReorderList.BlockLength (design ruling e). The whole point of an ADDITIVE block API is that
        // the existing single-item surfaces (sidebar pins, tabs, TreeView rows) keep their exact geometry, so the first
        // half of this gate is a byte-identity proof: the same drag driven through Begin(i, extents) and through
        // Begin(i, 1, extents) must agree bit-for-bit on every published number. The second half is the block algebra
        // itself — displacement by the block's whole span, a projection that reduces to a contiguous remove+insert, and
        // a Move<T> overload that lands the collection exactly where the projection said it would.
        {
            float[] extents = [10f, 24f, 10f, 40f, 10f, 16f];
            bool identical = true;
            foreach (float spacing in new[] { 0f, 6f })
                for (int d = 0; d < extents.Length; d++)
                    foreach (float delta in new[] { -80f, -21f, 0f, 17f, 95f })
                    {
                        var classic = new ReorderList { DwellMs = 0f };
                        var block1 = new ReorderList { DwellMs = 0f };
                        classic.Begin(d, extents, spacing);
                        block1.Begin(d, 1, extents, spacing);
                        classic.Update(delta); classic.Advance(1f);
                        block1.Update(delta); block1.Advance(1f);
                        identical &= block1.BlockLength == 1
                                     && classic.PendingIndex == block1.PendingIndex
                                     && classic.TargetIndex == block1.TargetIndex
                                     && classic.DraggedTargetStart.Equals(block1.DraggedTargetStart);
                        Span<int> a = stackalloc int[extents.Length];
                        Span<int> b = stackalloc int[extents.Length];
                        classic.ProjectOrder(a); block1.ProjectOrder(b);
                        for (int i = 0; i < extents.Length; i++)
                            identical &= a[i] == b[i] && classic.OffsetFor(i).Equals(block1.OffsetFor(i));
                    }

            // A 2-long block at [1,2] of six uniform 10px rows, moved FORWARD to slot 3 → [0,3,4,1,2,5].
            var fwd = new ReorderList { DwellMs = 0f };
            fwd.BeginBlock(1, 2, 6, 10f);
            bool sizes = fwd.BlockLength == 2 && fwd.DraggedIndex == 1;
            fwd.Update(26f);            // block centre 20+26=46 clears row 4's midpoint (45), not row 5's (55)
            fwd.Advance(1f);
            Span<int> order = stackalloc int[6];
            fwd.ProjectOrder(order);
            bool fwdSlot = fwd.TargetIndex == 3;
            bool fwdOrder = order[0] == 0 && order[1] == 3 && order[2] == 4 && order[3] == 1 && order[4] == 2 && order[5] == 5;
            // Displaced rows shift by the block's WHOLE span (2 x 10), the block's own rows take no hint, and the block
            // lands at the start slot 3 owns (30).
            bool fwdOffsets = Near(fwd.OffsetFor(0), 0f) && Near(fwd.OffsetFor(1), 0f) && Near(fwd.OffsetFor(2), 0f)
                              && Near(fwd.OffsetFor(3), -20f) && Near(fwd.OffsetFor(4), -20f) && Near(fwd.OffsetFor(5), 0f)
                              && Near(fwd.DraggedTargetStart, 30f);
            // The forward slot is clamped to Count − BlockLength: a block can never start past the last legal landing.
            fwd.Update(400f); fwd.Advance(1f);
            bool fwdClamp = fwd.TargetIndex == 4 && fwd.MoveTarget(+3) == false;

            // The same block moved BACKWARD (a 3-long run at [2,3,4] of six rows → slot 0).
            var back = new ReorderList { DwellMs = 0f };
            back.BeginBlock(2, 3, 6, 10f);
            back.Update(-35f);
            back.Advance(1f);
            Span<int> border = stackalloc int[6];
            back.ProjectOrder(border);
            bool backOk = back.TargetIndex == 0
                          && border[0] == 2 && border[1] == 3 && border[2] == 4
                          && border[3] == 0 && border[4] == 1 && border[5] == 5
                          && Near(back.OffsetFor(0), 30f) && Near(back.OffsetFor(1), 30f)
                          && Near(back.OffsetFor(5), 0f) && Near(back.DraggedTargetStart, 0f);

            // Move<T>(list, from, blockLength, to) applies EXACTLY the projection above; length 1 is the old overload.
            var list = new List<int> { 0, 1, 2, 3, 4, 5 };
            ReorderList.Move(list, 1, 2, 3);
            bool moved = list is [0, 3, 4, 1, 2, 5];
            var single = new List<int> { 0, 1, 2, 3 };
            var reference = new List<int> { 0, 1, 2, 3 };
            ReorderList.Move(single, 0, 1, 2);
            ReorderList.Move(reference, 0, 2);
            bool oneIsOld = single is [1, 2, 0, 3] && reference is [1, 2, 0, 3];

            Check("e5dragdrop.block ReorderList grows an ADDITIVE contiguous-block mode: BlockLength 1 is bit-identical to the classic single-item path across every published number, while a real block displaces siblings by its whole span, projects to a contiguous remove+insert, clamps its landing slot to Count-BlockLength, and commits through Move<T>(list, from, blockLength, to)",
                identical && sizes && fwdSlot && fwdOrder && fwdOffsets && fwdClamp && backOk && moved && oneIsOld,
                $"identity={identical} sizes={sizes} fwd=({fwdSlot},{fwdOrder},{fwdOffsets},{fwdClamp}) back={backOk} move=({moved},{oneIsOld})");
        }

        // e5dragdrop.armblock — Element.BlocksDragArm. A press inside a draggable row arms the ROW (the WinUI
        // item-container rule, implemented as an upward walk from the press target), which is exactly wrong for a
        // child that is its own affordance: a card's play FAB or its "…" button would become a drag handle, so the
        // first 4px of a press on Play would lift the card instead of playing it. The barrier stops the walk.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 300, Height = 200,
                Children =
                [
                    new BoxEl
                    {
                        Key = "card", Width = 300, Height = 100, CanDrag = true,
                        Draggable = Drag.Source("k", static () => "p"),
                        Children =
                        [
                            new BoxEl { Key = "label", Width = 150, Height = 100 },
                            new BoxEl { Key = "fab", Width = 40, Height = 40, OnClick = static () => { }, BlocksDragArm = true },
                        ],
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var card = Child(scene, scene.Root, 0);
            var label = Child(scene, card, 0);
            var fab = Child(scene, card, 1);

            var ctl = new DragController(scene, static () => { });
            bool armsFromPlainChild = ctl.TryArm(label, new Point2(10, 10), PointerKind.Mouse, KeyModifiers.None, 0)
                                      && ctl.IsArmed;
            ctl.Disarm();
            bool blockedFromBarrier = !ctl.TryArm(fab, new Point2(160, 10), PointerKind.Mouse, KeyModifiers.None, 0)
                                      && !ctl.IsArmed;
            // The barrier blocks only the ANCESTOR search — a node that is ITSELF draggable still arms.
            bool selfStillArms = ctl.TryArm(card, new Point2(10, 10), PointerKind.Mouse, KeyModifiers.None, 0)
                                 && ctl.IsArmed;
            ctl.Disarm();

            Check("e5dragdrop.armblock Element.BlocksDragArm stops TryArm's upward walk at itself — a press on a card's own button never arms the card's drag, while a press on ordinary card content still does and a draggable node with the bit still arms itself",
                armsFromPlainChild && blockedFromBarrier && selfStillArms,
                $"plainChild={armsFromPlainChild} barrier={blockedFromBarrier} self={selfStillArms}");
        }

        DragScrimChecks();
    }

    // ── Wave 5: the drop-spotlight SCRIM band ─────────────────────────────────────────────────────────────
    // The dim is now ONE explicit band — an opacity group at DragVisualTok.ScrimOpacity holding a flat scrim fill with
    // one rounded ERASE per compatible destination — instead of the old ×0.28 root multiply + ÷0.28 per-target divide.
    static void DragScrimChecks()
    {
        var strings = new StringTable();
        var fonts = new HeadlessFontSystem(strings);

        // A scene with ONE compatible spotlight target (rounded) and one incompatible one, with a drag already open.
        static (SceneStore scene, InputDispatcher disp, NodeHandle ok, NodeHandle no) ScrimScene(
            StringTable strings, HeadlessFontSystem fonts, Func<DragSession, bool>? spotlightWhen)
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Width = 300, Height = 200,
                Children =
                [
                    new BoxEl
                    {
                        Key = "ok", Width = 100, Height = 60, Corners = new CornerRadius4(8f, 8f, 8f, 8f),
                        DropTarget = new DropTargetSpec(["resource"])
                        {
                            VisualPolicy = DropTargetVisualPolicy.Spotlight,
                            SpotlightWhen = spotlightWhen,
                        },
                    },
                    new BoxEl
                    {
                        Key = "no", Width = 100, Height = 60,
                        DropTarget = new DropTargetSpec(["resource"])
                        {
                            VisualPolicy = DropTargetVisualPolicy.Spotlight,
                            CanAccept = static _ => false,
                        },
                    },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            disp.DragDrop.ExternalBegin("resource", "payload", new Point2(50, 30), KeyModifiers.None);
            return (scene, disp, Child(scene, scene.Root, 0), Child(scene, scene.Root, 1));
        }

        static HeadlessGpuDevice RecordTo(SceneStore scene)
        {
            var dl = new DrawList();
            SceneRecorder.Record(scene, dl);
            var dev = new HeadlessGpuDevice();
            dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(300, 200), 1f, ColorF.Transparent));
            return dev;
        }

        static int ScrimFillIndex(HeadlessGpuDevice dev)
        {
            for (int i = 0; i < dev.LastRects.Count; i++)
                if (dev.LastRects[i].Fill == DragVisualTok.ScrimColor) return i;
            return -1;
        }

        // e5dragdrop.scrim.cutout — the band exists, is ONE opacity group at the token alpha, and cuts exactly one
        // rounded window: over the COMPATIBLE destination, carrying that node's own corner radii. The refuser gets no
        // window, and no node's own opacity was touched to achieve any of it — the deleted hack multiplied the root
        // by 0.28 and divided every spotlight subtree back out again.
        {
            var (scene, disp, ok, no) = ScrimScene(strings, fonts, spotlightWhen: null);
            var dev = RecordTo(scene);
            int iScrim = ScrimFillIndex(dev);
            bool banded = iScrim >= 0;
            int groups = 0;
            for (int i = 0; i < dev.LastLayers.Count; i++)
                if (dev.LastLayers[i].Kind == (int)LayerKind.Opacity && Near(dev.LastLayers[i].GroupAlpha, DragVisualTok.ScrimOpacity)) groups++;
            RectF okRect = scene.AbsoluteRect(ok), noRect = scene.AbsoluteRect(no);
            bool oneHole = dev.LastErases.Count == 1;
            bool overTarget = oneHole
                && Near(dev.LastErases[0].Transform.Dx, okRect.X) && Near(dev.LastErases[0].Transform.Dy, okRect.Y)
                && Near(dev.LastErases[0].Rect.W, okRect.W) && Near(dev.LastErases[0].Rect.H, okRect.H)
                && Near(dev.LastErases[0].Radii.TopLeft, 8f) && Near(dev.LastErases[0].Radii.BottomRight, 8f);
            bool notOverRefuser = !oneHole || !Near(dev.LastErases[0].Transform.Dx, noRect.X);
            bool opacityUntouched = Near(scene.Paint(scene.Root).Opacity, 1f) && Near(scene.Paint(ok).Opacity, 1f);
            disp.DragDrop.Cancel();
            Check("e5dragdrop.scrim.cutout a live drag with compatible spotlight destinations records ONE scrim band — an opacity group at DragVisualTok.ScrimOpacity holding a flat scrim fill — with exactly one rounded ERASE cut over the compatible destination (its own corner radii), none over the refuser, and no node opacity mutated to do it",
                banded && groups == 1 && oneHole && overTarget && notOverRefuser && opacityUntouched,
                $"banded={banded}@{iScrim} groups={groups} holes={dev.LastErases.Count} overTarget={overTarget} notRefuser={notOverRefuser} opacityUntouched={opacityUntouched}");
        }

        // e5dragdrop.scrim.policy — DropTargetSpec.SpotlightWhen is CONSUMED: a session it refuses leaves that target
        // out of the spotlight set, and with no root left the band is not emitted at all (Wavee same-list reorder, A14).
        {
            var (scene, disp, _, _) = ScrimScene(strings, fonts, spotlightWhen: static _ => false);
            var dev = RecordTo(scene);
            bool noGroup = true;
            for (int i = 0; i < dev.LastLayers.Count; i++)
                if (dev.LastLayers[i].Kind == (int)LayerKind.Opacity && Near(dev.LastLayers[i].GroupAlpha, DragVisualTok.ScrimOpacity)) noGroup = false;
            bool noScrim = !scene.DropSpotlightActive && ScrimFillIndex(dev) < 0 && dev.LastErases.Count == 0 && noGroup;
            disp.DragDrop.Cancel();
            Check("e5dragdrop.scrim.policy a DropTargetSpec.SpotlightWhen that refuses the live session drops that target from the spotlight set — and with no compatible destination left the recorder emits NO scrim band at all (a same-list reorder never dims the app)",
                noScrim, $"active={scene.DropSpotlightActive} scrimFill={ScrimFillIndex(dev)} holes={dev.LastErases.Count} noGroup={noGroup}");
        }

        // e5dragdrop.scrim.reachable — B3. A spotlight target must be a target the pointer can actually REACH. The
        // always-mounted-but-hidden layer is the shape that breaks it: a pane keeps both its layouts mounted and turns
        // the inactive one off with Opacity 0 + HitTestVisible false, but its virtualized rows keep writing live
        // DropTargetSpecs. The hit-test prunes that whole subtree, so those targets can never be entered — yet the
        // spotlight collector filtered only on policy/kind/IsLive/Disabled/CanAccept, so the veil punched cutouts at
        // the HIDDEN layer's geometry: bright empty plates over the visible layer's dividers and gaps, while the real
        // rows stayed dimmed. Reachability is now part of "compatible": one cutout, over the reachable target only.
        {
            var scene = new SceneStore();
            var spot = new DropTargetSpec(["resource"]) { VisualPolicy = DropTargetVisualPolicy.Spotlight };
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Width = 300, Height = 200,
                Children =
                [
                    // The parked layer: not itself a target, but hosting one — the ancestor is what makes it unreachable.
                    new BoxEl
                    {
                        Key = "parked", Width = 150, Height = 200, HitTestVisible = false,
                        Children = [new BoxEl { Key = "unreachable", Width = 100, Height = 60, DropTarget = spot }],
                    },
                    new BoxEl { Key = "reachable", Width = 100, Height = 60, DropTarget = spot },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var unreachable = Child(scene, Child(scene, scene.Root, 0), 0);
            var reachable = Child(scene, scene.Root, 1);
            disp.DragDrop.ExternalBegin("resource", "payload", new Point2(200, 30), KeyModifiers.None);
            int roots = scene.DropSpotlightRootCount;
            bool onlyReachable = scene.DropSpotlightActive && roots == 1
                                 && scene.IsDropSpotlightRoot(reachable) && !scene.IsDropSpotlightRoot(unreachable);
            var dev = RecordTo(scene);
            RectF hit = scene.AbsoluteRect(reachable);
            bool oneHoleOnTheLiveOne = dev.LastErases.Count == 1
                && Near(dev.LastErases[0].Transform.Dx, hit.X) && Near(dev.LastErases[0].Transform.Dy, hit.Y);
            disp.DragDrop.Cancel();
            Check("e5dragdrop.scrim.reachable a Spotlight target whose ANCESTOR chain has HitTestVisible cleared is dropped from the spotlight set — the hit-test prunes that subtree, so it can never become the destination and must not advertise as one; the reachable sibling still gets its single cutout (the parked-sidebar-layer veil punching bright plates over rows that render nothing)",
                onlyReachable && oneHoleOnTheLiveOne,
                $"onlyReachable={onlyReachable} roots={roots} holes={dev.LastErases.Count}");
        }

        // e5dragdrop.scrim.clip — SceneStore.SpotlightScrimClip scopes the band (Wavee: the content region, so the title
        // bar and the docked player bar stay lit) and every cutout is intersected with it too.
        {
            var (scene, disp, ok, _) = ScrimScene(strings, fonts, spotlightWhen: null);
            var clip = new RectF(0f, 20f, 300f, 120f);   // a "chrome" band excluded top and bottom — and the target's top 20px
            scene.SpotlightScrimClip = clip;
            var dev = RecordTo(scene);
            int iScrim = ScrimFillIndex(dev);
            bool scoped = iScrim >= 0
                && Near(dev.LastRects[iScrim].Transform.Dx, clip.X) && Near(dev.LastRects[iScrim].Transform.Dy, clip.Y)
                && Near(dev.LastRects[iScrim].Rect.W, clip.W) && Near(dev.LastRects[iScrim].Rect.H, clip.H);
            RectF okRect = scene.AbsoluteRect(ok);
            RectF expected = okRect.Intersect(clip);
            bool holeScoped = dev.LastErases.Count == 1
                && Near(dev.LastErases[0].Transform.Dy, expected.Y) && Near(dev.LastErases[0].Rect.H, expected.H)
                && expected.H < okRect.H;
            scene.SpotlightScrimClip = null;
            disp.DragDrop.Cancel();
            Check("e5dragdrop.scrim.clip SceneStore.SpotlightScrimClip scopes the scrim band to a content region — the veil rect IS that region and each cutout is intersected with it, so app chrome outside the region is never dimmed",
                scoped && holeScoped,
                $"scoped={scoped} holeScoped={holeScoped} holes={dev.LastErases.Count}");
        }

        // e5dragdrop.scrim.band — band ORDER: the scrim covers the main pass, but the drag ghost and the chip
        // (DragOverlay) paint ABOVE it. Painter order in the recorded stream is the contract (the RHI replays in
        // emission order), and it is what retires the old spotlight-exemption registry.
        {
            var scene = new SceneStore();
            var mainFill = ColorF.FromRgba(0x11, 0x22, 0x33);
            var ghostFill = ColorF.FromRgba(0x44, 0x55, 0x66);
            var chipFill = ColorF.FromRgba(0x77, 0x88, 0x99);
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Width = 400, Height = 300, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Key = "main", Width = 100, Height = 40, Fill = mainFill },
                    new BoxEl
                    {
                        Key = "target", Width = 100, Height = 40,
                        DropTarget = new DropTargetSpec(["resource"]) { VisualPolicy = DropTargetVisualPolicy.Spotlight },
                    },
                    new BoxEl { Key = "ghost", Width = 100, Height = 40, Fill = ghostFill },
                    new BoxEl { Key = "chip", Width = 100, Height = 40, Fill = chipFill },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var ghostNode = Child(scene, scene.Root, 2);
            var chipNode = Child(scene, scene.Root, 3);
            scene.Flags(ghostNode) |= NodeFlags.DragGhost;
            scene.DragGhost = ghostNode;
            scene.DragOverlay = chipNode;
            var disp = new InputDispatcher(scene);
            disp.DragDrop.ExternalBegin("resource", "payload", new Point2(50, 20), KeyModifiers.None);

            var dev = RecordTo(scene);
            int iMain = -1, iScrim = -1, iGhost = -1, iChip = -1;
            for (int i = 0; i < dev.LastRects.Count; i++)
            {
                var col = dev.LastRects[i].Fill;
                if (col == mainFill) iMain = i;
                else if (col == DragVisualTok.ScrimColor) iScrim = i;
                else if (col == ghostFill) iGhost = i;
                else if (col == chipFill) iChip = i;
            }
            bool ordered = iMain >= 0 && iScrim > iMain && iGhost > iScrim && iChip > iGhost;
            disp.DragDrop.Cancel();
            Check("e5dragdrop.scrim.band the scrim band records AFTER the whole main pass and BEFORE the drag-ghost and DragOverlay bands — ordinary content dims while the lifted visual and the drag chip stay lit above the veil",
                ordered, $"main={iMain} scrim={iScrim} ghost={iGhost} chip={iChip}");
        }

        // e5dragdrop.scrim.cancel — the band is drag-scoped: ending the gesture clears the spotlight roots, so the very
        // next record emits neither the veil nor any cutout.
        {
            var (scene, disp, _, _) = ScrimScene(strings, fonts, spotlightWhen: null);
            var during = RecordTo(scene);
            bool lit = ScrimFillIndex(during) >= 0 && during.LastErases.Count == 1;
            disp.DragDrop.Cancel();
            var after = RecordTo(scene);
            bool cleared = !scene.DropSpotlightActive && ScrimFillIndex(after) < 0 && after.LastErases.Count == 0;
            Check("e5dragdrop.scrim.cancel the scrim is drag-scoped — cancelling the session clears the spotlight roots and the next record emits neither the veil nor any cutout",
                lit && cleared, $"lit={lit} cleared={cleared}");
        }

        // e5dragdrop.scrim.alloc — the band must be steady-state allocation-free: a drag-move frame with the scrim live
        // still allocates 0 bytes on phases 6-13 (pure rect math over the scene's own root list, no scratch buffers).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-scrim-alloc", new Size2(480, 320), 1f)); window.Show();
            var probe = new ChipDragProbe { SpotlightSink = true };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var src = Child(scene, Child(scene, scene.Root, 0), 0);
            var c = CenterOf(scene, src);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f, c.Y), 0, 0));
            host.RunFrame();
            bool scrimLive = scene.DropSpotlightActive;
            for (int i = 1; i <= 24; i++)   // warm: chip mount, draw-list growth, shaping
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f + i, c.Y), 0, 0));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 60f, c.Y), 0, 0));
            var frameA = host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 66f, c.Y), 0, 0));
            var frameB = host.RunFrame();
            bool quiet = frameA.HotPhaseAllocBytes == 0 && frameB.HotPhaseAllocBytes == 0;
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 66f, c.Y), 0, 0));
            host.RunFrame();
            Check("e5dragdrop.scrim.alloc a drag-move frame with the spotlight scrim live allocates 0 bytes on phases 6-13 — the band is rect math over the scene's own spotlight-root list, with no per-frame list or scratch buffer",
                scrimLive && quiet,
                $"scrimLive={scrimLive} allocA={frameA.HotPhaseAllocBytes}B allocB={frameB.HotPhaseAllocBytes}B");
        }

        DragScrimVirtualScrollChecks(strings, fonts);
        DragChipPickupFlashChecks(strings, fonts);
    }

    // ── The chip's pickup TILT is a flash, not a pose ─────────────────────────────────────────────────────────
    // A permanent ~4° Rotation on the chip wrapper made the card read as MISRENDERED for the whole gesture (a crooked
    // rectangle full of crooked text). The tilt + the 1.02 pickup scale now flash at lift and ease back to flat inside
    // DragChip.PickupFlashMs, and — because DragPreviewLayer re-runs Preview on every caption / target / effect edge —
    // the flash must be seeded ONCE per gesture and never replay mid-drag.
    static void DragChipPickupFlashChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        // e5dragdrop.chip.pickup-flash
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-chip-tilt", new Size2(480, 320), 1f)); window.Show();
            var probe = new ChipDragProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var scene = host.Scene;
            var src = Child(scene, Child(scene, scene.Root, 0), 0);
            var c = CenterOf(scene, src);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 20f, c.Y), 0, 0));
            host.RunFrame();

            // The chip's own transform owner: DragOverlay → follow wrapper (bound Transform) → the inner box the
            // pickup flash and the drop settle share.
            var overlay = scene.DragOverlay;
            var settle = Child(scene, Child(scene, overlay, 0), 0);
            var atPickup = scene.Paint(settle).LocalTransform;
            // Tilted AND scaled at lift: 4° about the centre with a 1.02 scale ⇒ an off-diagonal well clear of zero.
            bool lifted = !settle.IsNull && MathF.Abs(atPickup.M12) > 0.02f && atPickup.M11 > 1.005f;

            // A caption/target edge re-runs Preview. The chip subtree must NOT remount (stable DragChip.ChipRootKey),
            // and the flash must not restart: the same node, still easing DOWN.
            var sink = Child(scene, Child(scene, scene.Root, 0), 1);
            var sc = CenterOf(scene, sink);
            window.QueueInput(new InputEvent(InputKind.PointerMove, sc, 0, 0));
            host.RunFrame();
            var settleAfterEdge = Child(scene, Child(scene, scene.DragOverlay, 0), 0);
            var afterEdge = scene.Paint(settleAfterEdge).LocalTransform;
            bool sameNode = settleAfterEdge == settle && scene.IsLive(settle);
            bool notReplayed = MathF.Abs(afterEdge.M12) <= MathF.Abs(atPickup.M12) + 1e-4f;

            // …and it SETTLES: flat and unscaled well inside 300ms of the lift (PickupFlashMs is 150).
            for (int i = 0; i < 24; i++) host.RunFrame();   // ~400ms of headless frames
            var settled = scene.Paint(Child(scene, Child(scene, scene.DragOverlay, 0), 0)).LocalTransform;
            bool flat = Near(settled.M11, 1f, 0.002f) && Near(settled.M22, 1f, 0.002f)
                        && Near(settled.M12, 0f, 0.002f) && Near(settled.M21, 0f, 0.002f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, sc, 0, 0));
            host.RunFrame();
            Check("e5dragdrop.chip.pickup-flash the drag chip's ~4° tilt + 1.02 scale is a PICKUP FLASH: tilted and scaled at lift, eased back to a flat, unscaled card within DragChip.PickupFlashMs, seeded once per gesture on a stably-keyed subtree so a caption/target edge neither remounts the chip nor replays the flash",
                lifted && sameNode && notReplayed && flat,
                $"lifted={lifted}(m12={atPickup.M12:0.####} m11={atPickup.M11:0.####}) sameNode={sameNode} notReplayed={notReplayed}(m12={afterEdge.M12:0.####}) flat={flat}(m11={settled.M11:0.####} m12={settled.M12:0.####})");
        }
    }

    // ── The scrim over a RECYCLING virtual list ───────────────────────────────────────────────────────────────
    // The spotlight root set used to be collected ONLY on a DropTargetsVersion edge (a spec written to / removed from
    // the sparse column). The signals-first bound realize path never writes that column again: a slot is built once and
    // recycled by a SIGNAL WRITE, so scrolling a virtualized list re-points every realized node at a DIFFERENT logical
    // item while its DropTargetSpec instance — and therefore the version — stays put. The set went stale in place:
    // cutouts stayed on the slots that WERE compatible and drifted with them as the rows underneath changed. The roots
    // are now re-collected once per frame, after layout and before record, for the whole life of a session.
    static void DragScrimVirtualScrollChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        // Which realized slot does a cutout sit on, and what logical item is bound there? Content-space Y of the hole
        // centre, divided by the uniform row pitch — pure geometry, no scene lookup, so a wrong row cannot hide.
        static bool HolesMatchCompatibleRows(HeadlessGpuDevice dev, RectF viewport, float offset, out string detail)
        {
            int expected = 0;
            float first = offset, lastEnd = offset + viewport.H;
            for (int i = 0; i < SpotlightScrollProbe.N; i++)
            {
                float top = i * SpotlightScrollProbe.RowH, bottom = top + SpotlightScrollProbe.RowH;
                if (bottom <= first + 0.5f || top >= lastEnd - 0.5f) continue;   // fully outside the viewport band
                if (SpotlightScrollProbe.AcceptsIndex(i)) expected++;
            }
            bool ok = dev.LastErases.Count == expected;
            int wrongRow = -1;
            for (int h = 0; h < dev.LastErases.Count; h++)
            {
                var e = dev.LastErases[h];
                float centre = e.Transform.Dy + e.Rect.H * 0.5f - viewport.Y + offset;   // → content space
                int idx = (int)MathF.Floor(centre / SpotlightScrollProbe.RowH);
                if (idx < 0 || idx >= SpotlightScrollProbe.N || !SpotlightScrollProbe.AcceptsIndex(idx))
                {
                    ok = false;
                    if (wrongRow < 0) wrongRow = idx;
                }
            }
            detail = $"holes={dev.LastErases.Count} expected={expected} firstWrongRow={wrongRow}";
            return ok;
        }

        // e5dragdrop.scrim.recycled — the whole defect in one gate: begin a drag over a bound virtual list of
        // spotlight targets (even items compatible), then scroll it BOTH ways — offset-driven with no pointer movement
        // at all (the wheel/fling/edge-autoscroll case) and again with a pointer move — and require the cutouts to
        // land on the CURRENTLY-bound compatible rows every time, with a recycled-away row's cutout gone.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e5-scrim-virtual", new Size2(240, 200), 1f)); window.Show();
            var probe = new SpotlightScrollProbe();
            var dev = new HeadlessGpuDevice();
            using var host = new AppHost(app, window, dev, fonts, strings, probe);
            for (int i = 0; i < 6; i++) host.RunFrame();
            var scene = host.Scene;
            var vpNode = FindScrollable(scene, scene.Root);
            var vp = scene.AbsoluteRect(vpNode);
            var p = new Point2(vp.X + vp.W * 0.5f, vp.Y + 10f);

            bool began = host.Input.DragDrop.ExternalBegin(SpotlightScrollProbe.Kind, "payload", p, KeyModifiers.None);
            host.Input.DragDrop.Move(host.Input.DiagHitTest(p), p, 0f, 0f, KeyModifiers.None);
            host.RunFrame();
            scene.TryGetScroll(vpNode, out var sc);
            bool atRest = HolesMatchCompatibleRows(dev, vp, sc.OffsetY, out string restDetail);

            // (1) OFFSET-driven: the controller writes the offset; no pointer event of any kind reaches the session, so
            // nothing on the Move path can re-collect. ONE row at a time — the contiguous-rotate recycle, where a slot
            // keeps its node and its spec and only its bind signal moves (Reconciler.RebindBoundSlot), and an ODD total
            // so every surviving slot ends up on the OTHER parity.
            for (int s = 0; s < 3; s++)
            {
                probe.Ctl.ScrollBy(SpotlightScrollProbe.RowH);
                for (int i = 0; i < 3; i++) host.RunFrame();
            }
            scene.TryGetScroll(vpNode, out sc);
            bool scrolled = sc.OffsetY > SpotlightScrollProbe.RowH;
            bool afterOffsetScroll = HolesMatchCompatibleRows(dev, vp, sc.OffsetY, out string offsetDetail);

            // (2) POINTER-driven: the same one-row recycles, each followed by a Move at the unchanged pointer position
            // (a drag held still over a list the wheel is scrolling under it).
            for (int s = 0; s < 3; s++)
            {
                probe.Ctl.ScrollBy(SpotlightScrollProbe.RowH);
                for (int i = 0; i < 3; i++) host.RunFrame();
                host.Input.DragDrop.Move(host.Input.DiagHitTest(p), p, 0f, 0f, KeyModifiers.None);
                host.RunFrame();
            }
            scene.TryGetScroll(vpNode, out sc);
            bool afterPointerScroll = HolesMatchCompatibleRows(dev, vp, sc.OffsetY, out string pointerDetail);

            // (3) A row recycled clean out of the realized window leaves no cutout behind: every hole is inside the
            // viewport band, and the count matches the rows actually on screen (asserted above).
            bool allInside = true;
            for (int h = 0; h < dev.LastErases.Count; h++)
            {
                var e = dev.LastErases[h];
                if (e.Transform.Dy < vp.Y - 0.5f || e.Transform.Dy + e.Rect.H > vp.Y + vp.H + 0.5f) allInside = false;
            }

            host.Input.DragDrop.Cancel();
            host.RunFrame();
            Check("e5dragdrop.scrim.recycled the spotlight cutouts track the CURRENTLY-bound rows of a recycling virtual list — an offset-driven scroll with no pointer movement (wheel/fling/edge-autoscroll) re-collects the roots just like a pointer move, so a slot recycled onto an incompatible item goes dark, a slot recycled onto a compatible one lights, and a row recycled out of the window leaves no cutout behind",
                began && scrolled && atRest && afterOffsetScroll && afterPointerScroll && allInside,
                $"began={began} scrolled={scrolled} rest[{restDetail}] offsetScroll[{offsetDetail}] pointerScroll[{pointerDetail}] allInside={allInside}");
        }
    }

    /// <summary>Does this chip subtree carry the not-allowed glyph? Walks the ELEMENT tree (the chip is pure data →
    /// elements), so the check reads the same thing a user would see rather than a flag the renderer might ignore.</summary>
    static bool HasNotAllowedGlyph(Element? e)
    {
        switch (e)
        {
            case null: return false;
            case TextEl t: return t.Text == DragChip.NotAllowedGlyph;
            case BoxEl b:
                foreach (var c in b.Children) if (HasNotAllowedGlyph(c)) return true;
                return false;
            default: return false;
        }
    }

    /// <summary>Does this rendered subtree contain a text leaf with exactly <paramref name="text"/>?</summary>
    static bool HasChipText(Element? e, string text)
    {
        switch (e)
        {
            case null: return false;
            case TextEl t: return t.Text == text;   // Prop<string> defines == against string (see HasNotAllowedGlyph)
            case BoxEl b:
                foreach (var c in b.Children) if (HasChipText(c, text)) return true;
                return false;
            default: return false;
        }
    }

    // -- the framework-owned sortable core (pure geometry - no scene, no host) ---------------------------
    // These replace the former VirtualInsertionPreviewController checks: that class computed a single capped suffix
    // gap with NO removal accounting, which is exactly the A4/A5 bug pair (a gap one block too big, a preview placed
    // in the wrong space). SortableMath now owns the whole family and the invariants are asserted directly.
    static void SortableSurfaceChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // e5dragdrop.reorder.varextent — C3. Reorderable/ReorderList assumed a UNIFORM pitch for the cross-list slot
        // and the insertion line even when the consumer supplied ExtentOf, so a list with one tall row aimed at the
        // wrong boundary. The resting table is now sampled (a foreign hover has no local lift to Begin) and both
        // queries read its prefix sums.
        {
            var rl = new ReorderList();
            Span<float> ext = stackalloc float[] { 40f, 100f, 40f, 40f };   // item 1 is a TALL row
            rl.Sample(ext, spacing: 0f);                                    // starts 0,40,140,180; content end 220
            bool slots = rl.SlotAtOffset(0f) == 0 && rl.SlotAtOffset(19f) == 0 && rl.SlotAtOffset(21f) == 1
                && rl.SlotAtOffset(89f) == 1 && rl.SlotAtOffset(91f) == 2 && rl.SlotAtOffset(1000f) == 4;
            bool boundaries = Near(rl.BoundaryOffset(0), 0f) && Near(rl.BoundaryOffset(1), 40f)
                && Near(rl.BoundaryOffset(2), 140f) && Near(rl.BoundaryOffset(4), 220f);
            // Uniform extents must reduce to the historical formula everywhere (the sidebar/tab-strip surfaces).
            var uni = new ReorderList();
            uni.Sample(5, 40f, spacing: 8f);
            bool reduces = true;
            for (float y = -20f; y < 300f; y += 2.3f)
            {
                int item = (int)MathF.Floor(y / 48f);
                float within = y - item * 48f;
                int expect = Math.Clamp(within > 20f ? item + 1 : item, 0, 5);
                reduces &= uni.SlotAtOffset(y) == expect;
            }
            bool uniformBoundary = Near(uni.BoundaryOffset(2), 2 * 48f - 4f) && Near(uni.BoundaryOffset(5), 5 * 48f - 4f);

            // …and through Reorderable's live cross-list hover path (a foreign session over the list body).
            var scene = new SceneStore();
            var ro = new Reorderable("res")
            {
                ItemCount = 4, ItemExtent = 40f, ExtentOf = i => i == 1 ? 100f : 40f, Spacing = 0f,
                Scene = scene, RequestRender = static () => { },
            };
            new TreeReconciler(scene, strings).ReconcileRoot(
                ro.List(new BoxEl { Width = 200, Height = 220 }), null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var at = new Point2(50f, 91f);        // just past the TALL row's centre (90) — a uniform 40 pitch says 2 @ 80
            bool began = disp.DragDrop.ExternalBegin("res", "p", at, KeyModifiers.None);
            disp.DragDrop.Move(disp.DiagHitTest(at), at, 0f, 0f, KeyModifiers.None);
            int liveSlot = ro.InsertionIndex;
            float liveLine = ro.InsertionLineOffset;
            bool live = ro.InsertionVisible && liveSlot == 2 && Near(liveLine, 140f);
            disp.DragDrop.Cancel();

            Check("e5dragdrop.reorder.varextent Reorderable/ReorderList resolve the cross-list slot AND the insertion-line boundary from the sampled resting extents (a list with one tall row), reducing byte-for-byte to the uniform midpoint formula when the extents are equal",
                slots && boundaries && reduces && uniformBoundary && began && live,
                $"slots={slots} boundaries={boundaries} reduces={reduces} uniformBoundary={uniformBoundary} live=({liveSlot},{liveLine:0.#})");
        }

        // e5dragdrop.reorder.policy — a sortable list is also a DESTINATION and a SOURCE of drags that leave it, which
        // the bare Reorderable could express none of: its drop spec accepted every payload of its kind (so a payload it
        // could do nothing with dropped into a silent no-op), it could not caption or explain a refusal, its items were
        // always GHOST-lifted (a second visual on top of an app's chip), and ANY completion committed the dwell slot —
        // so dragging a row OUT to a foreign target also reordered the list the downward travel had projected.
        {
            var scene = new SceneStore();
            int deposits = 0, depositSlot = -1, commits = 0;
            var ro = new Reorderable("res")
            {
                ItemCount = 4, ItemExtent = 40f, Spacing = 0f, Scene = scene, RequestRender = static () => { },
                DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity },
                CanAcceptForeign = static p => p is string s && s != "no",
                ForeignRefusalCaption = static _ => "Nothing to add",
                ForeignCaption = static (_, slot) => "Add at " + slot,
                RequireDropOnList = true,
                OnReorder = (_, _) => commits++,
                OnCrossCommit = (_, _, _, _, slot) => { deposits++; depositSlot = slot; },
            };
            var item = (BoxEl)ro.Item(0, new BoxEl { Width = 200, Height = 40 }, key: "i0");
            bool styled = item.Draggable is { Style: { } style }
                          && style.Lift == DragLift.Stationary && Near(style.Opacity, Drag.SourceDimOpacity);

            new TreeReconciler(scene, strings).ReconcileRoot(
                ro.List(new BoxEl { Width = 200, Height = 160 }), null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var session = disp.DragDrop.Session;
            var at = new Point2(50f, 50f);        // inside item 1's first half ⇒ slot 1
            var node = disp.DiagHitTest(at);

            // A payload the list can do nothing with: transparent (no OverTarget) but NOT silent — the reason rides
            // the session for the chip to render beside its not-allowed glyph.
            disp.DragDrop.ExternalBegin("res", "no", at, KeyModifiers.None);
            disp.DragDrop.Move(node, at, 0f, 0f, KeyModifiers.None);
            bool refused = session.OverTarget.IsNull && !session.RefusedTarget.IsNull
                           && session.Caption == "Nothing to add" && !ro.InsertionVisible;
            disp.DragDrop.Cancel();

            // An accepted one captions per move and deposits at the slot the line marked.
            disp.DragDrop.ExternalBegin("res", "ok", at, KeyModifiers.None);
            disp.DragDrop.Move(node, at, 0f, 0f, KeyModifiers.None);
            bool captioned = !session.OverTarget.IsNull && session.Caption == "Add at 1" && ro.InsertionIndex == 1;
            disp.DragDrop.TryDrop(at, KeyModifiers.None, out _);
            bool deposited = deposits == 1 && depositSlot == 1;

            // RequireDropOnList: the list's OWN gesture, released somewhere else, commits nothing…
            var args = new DragEventArgs { TotalDy = 120f };   // from item 0 past item 2's centre ⇒ pending slot 2
            item.OnDragStarted?.Invoke(args);
            item.OnDragDelta?.Invoke(args);
            item.OnDragCompleted?.Invoke(args);
            bool noCommitAway = commits == 0;

            // …and the same gesture RELEASED over the list does commit — after being accepted despite the foreign gate
            // refusing everything that is not one of its strings (a list must never refuse its own item).
            item.OnDragStarted?.Invoke(args);
            item.OnDragDelta?.Invoke(args);
            disp.DragDrop.ExternalBegin("res", new ReorderPayload(ro, 0, null), at, KeyModifiers.None);
            disp.DragDrop.Move(node, at, 0f, 0f, KeyModifiers.None);
            bool ownAccepted = !session.OverTarget.IsNull && session.Caption is null && !ro.InsertionVisible;
            disp.DragDrop.TryDrop(at, KeyModifiers.None, out _);
            item.OnDragCompleted?.Invoke(args);
            bool commitOnList = commits == 1 && deposits == 1;   // its own drop is NOT a cross-list deposit

            Check("e5dragdrop.reorder.policy Reorderable takes a foreign-payload gate + captions (refusal included), styles its items' lift, and with RequireDropOnList commits a pointer reorder ONLY when the gesture was released over the list — while its OWN payload is always accepted and never captioned",
                styled && refused && captioned && deposited && noCommitAway && ownAccepted && commitOnList,
                $"styled={styled} refused={refused} caption={captioned} deposit=({deposits},{depositSlot}) away={noCommitAway} own={ownAccepted} commit={commitOnList}");
        }

        // e5dragdrop.settle.cancel — Escape mid-insertion. The most-modal gesture: the L2 session closes with OnLeave
        // on the live target, so the gap, the preview, the hidden source rows and the drop spotlight all tear down —
        // and NO deposit runs. A cancel that left the projection open was the "stale cards stuck at the top" failure.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("insertion-cancel", new Size2(320, 400), 1f));
            window.Show();
            var probe = new InsertionProbe { SameList = true, Sources = [0, 2], DraggedCount = 2 };
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            for (int i = 0; i < 4; i++) host.RunFrame();
            var scene = host.Scene;
            var vp = FindScrollable(scene, scene.Root);
            scene.TryGetScroll(vp, out var sc);
            var rect = scene.AbsoluteRect(vp);
            var down = new Point2(rect.X + 100f, rect.Y + 100f);

            // A REAL L1 lift (Escape only cancels a live item drag), which opens the L2 session on promotion.
            window.QueueInput(new InputEvent(InputKind.PointerDown, down, 0, 0));
            host.RunFrame();
            var moved = new Point2(down.X, down.Y + 30f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, moved, 0, 0));
            for (int i = 0; i < 30; i++) host.RunFrame();
            bool lifted = host.Input.Drag.IsActive && host.Input.DragDrop.IsActive
                && !FindFillNode(scene, scene.Root, InsertionProbe.PreviewFill).IsNull;

            window.QueueInput(new InputEvent(InputKind.Key, moved, 0, Keys.Escape));
            for (int i = 0; i < 60; i++) host.RunFrame();
            Span<float> dy = stackalloc float[8];
            Span<float> op = stackalloc float[8];
            int ord = 0;
            for (var n = scene.FirstChild(sc.ContentNode); !n.IsNull && ord < dy.Length; n = scene.NextSibling(n), ord++)
            {
                dy[ord] = scene.Paint(n).LocalTransform.Dy;
                op[ord] = scene.Paint(n).Opacity;
            }
            bool cleared = FindFillNode(scene, scene.Root, InsertionProbe.PreviewFill).IsNull
                && Near(dy[3], 0f, 0.5f) && Near(dy[4], 0f, 0.5f)
                && op[2] > 0.95f && op[3] > 0.95f && op[4] > 0.95f;
            bool closed = !host.Input.DragDrop.IsActive && !host.Input.Drag.IsActive
                && host.Input.DragDrop.OverTarget.IsNull && !scene.DropSpotlightActive;
            bool noCommit = probe.Deposits == 0 && probe.LandedSlot == -1;

            Check("e5dragdrop.settle.cancel Escape mid-insertion tears the whole projection down — gap, in-gap preview, hidden source rows and drop spotlight all restore, the L1+L2 session closes, and no deposit runs",
                lifted && cleared && closed && noCommit,
                $"lifted={lifted} cleared={cleared} closed={closed} deposits={probe.Deposits} dy=[{dy[2]:0.#},{dy[3]:0.#},{dy[4]:0.#}] op=[{op[2]:0.##},{op[3]:0.##},{op[4]:0.##}]");
        }

        // e5dragdrop.reorder.varextent.samelist — C3's sibling case. The existing varextent gate drives the CROSS-LIST
        // hover path (Core.Sample on entry); this drives the SAME-LIST keyboard lift with LiveProject off, which is the
        // shape every mixed-height list in the app actually uses (the customizer outline's 52/44 cards). Both the shown
        // cue and the committed slot must read the sampled prefix sums, or the line lands a row off from the drop.
        {
            var ro = new Reorderable("samelist")
            {
                ItemCount = 4, ItemExtent = 40f, ExtentOf = i => i == 1 ? 100f : 40f, Spacing = 0f,
                LiveProject = false, ShowInsertionLine = true, RequestRender = static () => { },
            };
            var item = (BoxEl)ro.Item(0, new BoxEl { Width = 200, Height = 40 }, key: "i0");
            item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Space));    // lift item 0 (starts 0,40,140,180)
            bool lifted = ro.IsKeyboardLifted && !ro.InsertionVisible;   // home slot: nothing to show yet
            item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Down));     // pending 1 ⇒ the line sits AFTER the tall row
            bool shown = ro.InsertionVisible && ro.InsertionIndex == 1;
            float line = ro.InsertionLineOffset;                     // 140 from the extents; a uniform 40 pitch says 80
            item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Escape));
            Check("e5dragdrop.reorder.varextent.samelist a same-list (LiveProject-off) lift draws its insertion cue from the SAMPLED extents, so a mixed-height list marks the boundary the commit actually lands on",
                lifted && shown && Near(line, 140f, 0.5f), $"lifted={lifted} shown={shown} line={line:0.#} expected=140");
        }

        // e5dragdrop.reorder.announce — the a11y channel (Primer / React-Aria): a keyboard lift has NO other feedback
        // (displacement and the insertion line are purely visual), so grab/move/drop/cancel must reach the engine's
        // live-region seam. Coalesced at ~100ms, because a held arrow key emits far more slot changes than a reader can
        // speak — the throttle DROPS what it swallows, and the terminal drop/cancel announcement is what states the
        // settled result, so the last thing spoken is never a position the user already left.
        {
            var spoken = new List<string>();
            var assertive = new List<bool>();
            var prior = InputHooks.Current.Default.Announce;
            try
            {
                InputHooks.Current.Default.Announce = (t, a) => { spoken.Add(t); assertive.Add(a); };
                var ro = new Reorderable("say")
                {
                    ItemCount = 4, ItemExtent = 40f, Spacing = 0f, LiveProject = false,
                    RequestRender = static () => { },
                    AnnounceText = a => $"{a.Kind}:{a.Index}->{a.Slot}/{a.Count}",
                    // Deterministic: a wall-clock window would make the coalescing assertion depend on how fast this
                    // gate happens to run (a GC pause between two synthetic key presses must not change the outcome).
                    AnnounceThrottleMs = 60_000f,
                };
                var item = (BoxEl)ro.Item(0, new BoxEl { Width = 200, Height = 40 }, key: "i0");
                Announcer.Reset();
                item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Space));   // Grab — immediate
                item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Down));    // Move — inside the window ⇒ coalesced away
                item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Down));    // Move — same
                item.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Space));   // Drop — states the settled result
                bool coalesced = spoken.Count == 2
                    && spoken[0] == "Grab:0->0/4" && spoken[1] == "Drop:0->2/4"
                    && assertive[0] && assertive[1];

                // …and a move that OPENS the window is spoken in its own right (the throttle coalesces bursts, it does
                // not mute the channel): after a Reset the first throttled message always lands.
                spoken.Clear();
                Announcer.Reset();
                bool moveSpeaks = Announcer.SayThrottled("Move:1->2/4", assertive: true)
                    && spoken.Count == 1 && spoken[0] == "Move:1->2/4";

                // An UNWIRED list is byte-identical to the pre-announcement control.
                spoken.Clear();
                Announcer.Reset();
                var quiet = new Reorderable("quiet") { ItemCount = 4, ItemExtent = 40f, RequestRender = static () => { } };
                var qi = (BoxEl)quiet.Item(0, new BoxEl { Width = 200, Height = 40 }, key: "q0");
                qi.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Space));
                qi.OnKeyDown?.Invoke(new KeyEventArgs(Keys.Space));
                bool silentByDefault = spoken.Count == 0;

                Check("e5dragdrop.reorder.announce Reorderable announces grab/move/drop assertively through the engine live-region seam, coalesces a burst of moves down to the terminal message, and stays byte-identical (silent) with no AnnounceText wired",
                    coalesced && moveSpeaks && silentByDefault,
                    $"coalesced={coalesced} moveSpeaks={moveSpeaks} silent={silentByDefault}");
            }
            finally { InputHooks.Current.Default.Announce = prior; Announcer.Reset(); }
        }
    }

    static void SortableMathChecks()
    {
        // sortable.slot - the NN/g centre-crossing trigger, uniform AND variable extents, in CONTENT space with a
        // MEASURED leading extent (the persistent prefix's real height, never an app estimate).
        {
            const float lead = 80f, e = 40f;
            const int n = 6;
            bool atTop = SortableMath.SlotFromOffset(0f, lead, e, n) == 0
                && SortableMath.SlotFromOffset(lead, lead, e, n) == 0
                && SortableMath.SlotFromOffset(lead + e * 0.5f - 0.01f, lead, e, n) == 0;
            bool crossesCentre = SortableMath.SlotFromOffset(lead + e * 0.5f, lead, e, n) == 1
                && SortableMath.SlotFromOffset(lead + e * 1.5f, lead, e, n) == 2;
            bool clampsEnd = SortableMath.SlotFromOffset(lead + e * 99f, lead, e, n) == n
                && SortableMath.SlotFromOffset(-500f, lead, e, n) == 0;
            // The same offset through the pointer form (viewport-space pointer + live scroll offset).
            bool viaPointer = SortableMath.SlotFromPointer(lead + e * 1.5f - 200f, 200f, lead, e, n) == 2;

            // Variable extents: item 1 is a TALL row (an expanded drawer). starts = 80,120,220,260,300,340,380.
            Span<float> starts = stackalloc float[] { 80f, 120f, 220f, 260f, 300f, 340f, 380f };
            bool varSlot = SortableMath.SlotFromOffset(150f, starts, n) == 1        // 150 < centre 170 => before item 1
                && SortableMath.SlotFromOffset(171f, starts, n) == 2                // past the TALL row's centre
                && SortableMath.SlotFromOffset(79f, starts, n) == 0
                && SortableMath.SlotFromOffset(1000f, starts, n) == n;
            // A uniform starts table must agree with the uniform overload exactly.
            Span<float> uniform = stackalloc float[n + 1];
            for (int i = 0; i <= n; i++) uniform[i] = lead + i * e;
            bool agrees = true;
            for (float y = 60f; y < lead + e * (n + 1); y += 3.7f)
                agrees &= SortableMath.SlotFromOffset(y, uniform, n) == SortableMath.SlotFromOffset(y, lead, e, n);
            // The single-band form (what a measured virtualized list can afford per move) agrees too.
            bool band = SortableMath.SlotFromBand(150f, 1, 120f, 100f, n) == 1
                && SortableMath.SlotFromBand(171f, 1, 120f, 100f, n) == 2;

            Check("sortable.slot SortableMath resolves the insertion slot by the NN/g centre-crossing rule against a MEASURED leading extent, clamps to [0,count] at both ends, and the variable-extent (prefix-sum + single-band) overloads agree with the uniform one everywhere the extents are equal",
                atTop && crossesCentre && clampsEnd && viaPointer && varSlot && agrees && band,
                $"top={atTop} centre={crossesCentre} clamp={clampsEnd} pointer={viaPointer} varied={varSlot} agrees={agrees} band={band}");
        }

        // sortable.gap - design ruling (a). Same-list virtual removal: the gap is EXACTLY N*extent, every row below a
        // hidden source shifts UP one extent, and the content height is invariant (the tail row's dy is 0). The
        // dragged set may be NON-CONTIGUOUS. Rows outside the insertable range never move (C1 prefix + A12 trailing).
        {
            const float e = 40f;
            // Items 2..37 insertable (0,1 lead; 38,39 trail). Sources = display 0 and 2 => items 2 and 4.
            var plan = SortableMath.Plan(firstItem: 2, count: 36, slot: 1, draggedCount: 2, itemExtent: e,
                sameList: true, previewCap: 3);
            Span<int> sources = stackalloc int[] { 2, 4 };
            bool exactGap = plan.GapRows == 2 && Near(plan.GapExtent, 80f) && plan.PreviewRows == 2 && plan.SlotItem == 3;
            bool reflow = Near(plan.DisplacementFor(2, sources), 0f)      // source above the slot: stays (it hides)
                && Near(plan.DisplacementFor(3, sources), 40f)            // gap 80 - one removed row above
                && Near(plan.DisplacementFor(4, sources), 40f)            // the second source, already displaced
                && Near(plan.DisplacementFor(5, sources), 0f)             // both removals now cancel the gap...
                && Near(plan.DisplacementFor(37, sources), 0f);           // ...so the content height never changed
            bool bounded = Near(plan.DisplacementFor(0, sources), 0f) && Near(plan.DisplacementFor(1, sources), 0f)
                && Near(plan.DisplacementFor(38, sources), 0f) && Near(plan.DisplacementFor(39, sources), 0f);
            bool hides = plan.IsHiddenSource(2, sources) && plan.IsHiddenSource(4, sources)
                && !plan.IsHiddenSource(3, sources) && !plan.IsHiddenSource(5, sources);
            // The gap's leading edge accounts for the rows removed ABOVE it (content AND viewport space).
            bool preview = plan.RemovedAboveSlot(sources) == 1
                && Near(plan.PreviewOffset(80f, sources), 80f)
                && Near(plan.PreviewY(80f, 30f, sources), 50f);

            // A larger, non-contiguous block: 5 sources scattered across the range, slot in the middle of them.
            var big = SortableMath.Plan(2, 36, 10, 5, e, sameList: true, previewCap: 3);
            Span<int> spread = stackalloc int[] { 3, 5, 9, 20, 30 };      // items; 3 of them are above slotItem 12
            bool bigGap = Near(big.GapExtent, 200f) && big.PreviewRows == 3 && big.RemovedAboveSlot(spread) == 3;
            bool bigNet = Near(big.DisplacementFor(2, spread), 0f)
                && Near(big.DisplacementFor(12, spread), 200f - 3f * e)
                && Near(big.DisplacementFor(37, spread), 0f);             // sum(removal) == N => net zero at the tail

            // CROSS-list copy: no removal, and the gap is CAPPED so a 500-track copy cannot blow the viewport - with
            // the preview reading the SAME extent (the A4 mismatch is structurally impossible now).
            var copy = SortableMath.Plan(2, 36, 4, 500, e, sameList: false, previewCap: 3);
            bool capped = copy.GapRows == 3 && Near(copy.GapExtent, 120f) && copy.PreviewRows == 3
                && copy.RemovedAboveSlot(sources) == 0
                && Near(copy.DisplacementFor(5, sources), 0f)
                && Near(copy.DisplacementFor(6, sources), 120f)
                && !copy.IsHiddenSource(6, sources);

            Check("sortable.gap virtual removal opens an EXACT N*extent same-list gap over a non-contiguous dragged set with a net-zero content-height delta, hides exactly the source rows, positions the preview by the removals above it, leaves items outside the insertable range untouched, and caps a cross-list copy's gap at the preview cap",
                exactGap && reflow && bounded && hides && preview && bigGap && bigNet && capped,
                $"gap={exactGap} reflow={reflow} bounded={bounded} hides={hides} preview={preview} big=({bigGap},{bigNet}) copy={capped}");
        }

        // sortable.empty - S5 cause 2. An EMPTY (or still-loading) destination resolves to slot 0 with a live gap so
        // the drop APPENDS; the old lane bailed on a zero-extent viewport and discarded the drop silently.
        {
            var empty = SortableMath.Plan(firstItem: 2, count: 0, slot: 0, draggedCount: 2, itemExtent: 40f,
                sameList: false, previewCap: 3);
            bool active = empty.IsActive && empty.Slot == 0 && Near(empty.GapExtent, 80f)
                && Near(empty.PreviewOffset(120f, default), 120f);
            bool noRows = Near(empty.DisplacementFor(2, default), 0f) && Near(empty.DisplacementFor(0, default), 0f);
            bool slotZero = SortableMath.SlotFromOffset(999f, 120f, 40f, 0) == 0
                && SortableMath.SlotFromOffset(999f, default, 0) == 0;
            // A payload with nothing in it, or a list with no usable extent, is inert - no phantom gap.
            bool inert = !SortableMath.Plan(2, 10, 3, 0, 40f, true).IsActive
                && !SortableMath.Plan(2, 10, 3, 2, 0f, true).IsActive
                && !SortableMath.Plan(2, 10, -1, 2, 40f, true).IsActive;
            Check("sortable.empty an empty destination still resolves slot 0 with a live gap at the leading edge (the drop APPENDS instead of being silently discarded), while an empty payload / extent-less list stays inert",
                active && noRows && slotZero && inert,
                $"active={active} noRows={noRows} slotZero={slotZero} inert={inert}");
        }

        // sortable.normalize - the one normalization every removal query assumes: sorted, de-duplicated, in range.
        {
            Span<int> raw = stackalloc int[] { 7, 3, 7, -1, 3, 99, 0 };
            int n = SortableMath.Normalize(raw, 10);
            bool normalized = n == 3 && raw[0] == 0 && raw[1] == 3 && raw[2] == 7;
            var live = raw[..n];
            bool queries = SortableMath.RemovedBefore(0, live) == 0 && SortableMath.RemovedBefore(4, live) == 2
                && SortableMath.RemovedBefore(100, live) == 3
                && SortableMath.IsSource(3, live) && !SortableMath.IsSource(4, live);
            Check("sortable.normalize the dragged-index set sorts, de-duplicates and drops out-of-range members in place, and the removal queries binary-search it",
                normalized && queries, $"n={n} normalized={normalized} queries={queries}");
        }
    }

    static void VirtualDisclosureChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virtual-disclosure", new Size2(260, 220), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new VirtualDisclosureProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();
        bool censusInitiallyIdle = !host.Scene.HasActiveVirtualDisclosures;

        int collapseSettled = 0;
        var range = new ItemDisclosureRange("band", 1, 2);
        probe.Controller.BeginDisclosure(range, ItemDisclosureDirection.Collapse,
            collapseCommit: probe.CommitCollapsed,
            settled: () => collapseSettled++);
        host.RunFrame();
        bool collapseStarted = host.Scene.TryGetScroll(probe.Controller.Viewport, out var opening)
            && float.IsFinite(opening.DisclosureT) && opening.DisclosureFirst == 1 && opening.DisclosureCount == 2;
        bool collapseCensusActive = host.Scene.HasActiveVirtualDisclosures;
        for (int i = 0; i < 4; i++) host.RunFrame();
        host.Scene.TryGetScroll(probe.Controller.Viewport, out var collapseMid);
        bool collapseIntermediate = collapseMid.DisclosureT > 0f && collapseMid.DisclosureT < 1f
            && Occurrences(host.Scene.Root, "A") == 1 && Occurrences(host.Scene.Root, "B") == 1
            && Occurrences(host.Scene.Root, "C") == 1 && Occurrences(host.Scene.Root, "D") == 1
            && Occurrences(host.Scene.Root, "E") == 1;
        for (int i = 0; i < 24; i++) host.RunFrame();
        host.Scene.TryGetScroll(probe.Controller.Viewport, out var collapsed);
        bool collapseFinished = collapseSettled == 1 && probe.Count.Peek() == 3
            && !float.IsFinite(collapsed.DisclosureT)
            && Occurrences(host.Scene.Root, "A") == 1 && Occurrences(host.Scene.Root, "B") == 0
            && Occurrences(host.Scene.Root, "C") == 0 && Occurrences(host.Scene.Root, "D") == 1
            && Occurrences(host.Scene.Root, "E") == 1;
        bool collapseCensusIdle = !host.Scene.HasActiveVirtualDisclosures;

        probe.RestoreExpanded();
        host.RunFrame();
        int expandSettled = 0;
        probe.Controller.BeginDisclosure(range, ItemDisclosureDirection.Expand,
            settled: () => expandSettled++);
        host.RunFrame();
        bool expandStarted = host.Scene.TryGetScroll(probe.Controller.Viewport, out var closing)
            && float.IsFinite(closing.DisclosureT) && closing.DisclosureFirst == 1 && closing.DisclosureCount == 2;
        bool expandCensusActive = host.Scene.HasActiveVirtualDisclosures;
        for (int i = 0; i < 4; i++) host.RunFrame();
        host.Scene.TryGetScroll(probe.Controller.Viewport, out var expandMid);
        bool expandIntermediate = expandMid.DisclosureT > 0f && expandMid.DisclosureT < 1f
            && Occurrences(host.Scene.Root, "A") == 1 && Occurrences(host.Scene.Root, "B") == 1
            && Occurrences(host.Scene.Root, "C") == 1 && Occurrences(host.Scene.Root, "D") == 1
            && Occurrences(host.Scene.Root, "E") == 1;
        for (int i = 0; i < 36; i++) host.RunFrame();
        host.Scene.TryGetScroll(probe.Controller.Viewport, out var expanded);
        bool expandFinished = expandSettled == 1 && probe.Count.Peek() == 5
            && !float.IsFinite(expanded.DisclosureT);
        bool expandCensusIdle = !host.Scene.HasActiveVirtualDisclosures;
        bool lifecycle = probe.Diagnostics.Exists(static d => d.Kind == ItemDisclosureDiagnosticKind.Armed)
            && probe.Diagnostics.Exists(static d => d.Kind == ItemDisclosureDiagnosticKind.Progress)
            && probe.Diagnostics.Exists(static d => d.Kind == ItemDisclosureDiagnosticKind.Cleared)
            && !probe.Diagnostics.Exists(static d => d.Kind == ItemDisclosureDiagnosticKind.FailedToArm);

        Check("virtual-disclosure.1 collapse retains the expanded model until settle, commits once, then clears presentation",
            collapseStarted && collapseIntermediate && collapseFinished,
            $"started={collapseStarted} mid={collapseMid.DisclosureT:0.###} identities={collapseIntermediate} settled={collapseSettled} count={probe.Count.Peek()} t={collapsed.DisclosureT}");
        Check("virtual-disclosure.2 expansion starts from the inserted model and releases its clip after the named motion",
            expandStarted && expandIntermediate && expandFinished,
            $"started={expandStarted} mid={expandMid.DisclosureT:0.###} identities={expandIntermediate} settled={expandSettled} count={probe.Count.Peek()} t={expanded.DisclosureT}");
        Check("virtual-disclosure.3 lifecycle arms before observation and clears without a failed-start recovery",
            lifecycle, $"events={probe.Diagnostics.Count}");
        Check("virtual-disclosure.4 scene census is active only while presentation is armed",
            censusInitiallyIdle && collapseCensusActive && collapseCensusIdle && expandCensusActive && expandCensusIdle,
            $"initial={censusInitiallyIdle} collapse={collapseCensusActive}->{collapseCensusIdle} expand={expandCensusActive}->{expandCensusIdle}");

        int Occurrences(NodeHandle node, string text)
        {
            if (node.IsNull) return 0;
            ref var paint = ref host.Scene.Paint(node);
            int found = paint.VisualKind == VisualKind.Text && strings.Resolve(paint.Text) == text ? 1 : 0;
            for (var child = host.Scene.FirstChild(node); !child.IsNull; child = host.Scene.NextSibling(child))
                found += Occurrences(child, text);
            return found;
        }
    }

    static void VirtualDisclosureFastPathChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1,
            Width = 100f,
            Height = 200f,
            ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 1,
                    Width = 100f,
                    Children =
                    [
                        new BoxEl { Key = "A", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "B", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "C", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "D", Width = 100f, Height = 40f, OnClick = static () => { } },
                        new BoxEl { Key = "E", Width = 100f, Height = 40f, OnClick = static () => { } },
                    ],
                },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);

        var viewport = scene.Root;
        var content = Child(scene, viewport, 0);
        var b = Child(scene, content, 1);
        var c = Child(scene, content, 2);
        var d = Child(scene, content, 3);
        ref ScrollState scroll = ref scene.ScrollRef(viewport);
        scroll.Orientation = 0;
        scroll.ContentNode = content;
        scroll.ItemCount = 5;
        scroll.FirstRealized = 0;

        bool idle = !scene.HasActiveVirtualDisclosures;
        bool armed = scene.BeginVirtualDisclosure(viewport, 1, 2, 40f, 80f, 0.5f)
            && scene.HasActiveVirtualDisclosures;
        bool retargeted = scene.BeginVirtualDisclosure(viewport, 1, 2, 40f, 80f, 0.5f)
            && scene.HasActiveVirtualDisclosures;
        var dispatcher = new InputDispatcher(scene);
        var bodyHit = dispatcher.HitTest(new Point2(10f, 50f));
        var suffixHit = dispatcher.HitTest(new Point2(10f, 90f));
        scene.ClearVirtualDisclosure(viewport);
        var restingHit = dispatcher.HitTest(new Point2(10f, 90f));
        scene.SetVirtualDisclosureProgress(viewport, 0.75f);   // a late animation write after clear must be ignored
        bool cleared = !scene.HasActiveVirtualDisclosures
            && scene.TryGetScroll(viewport, out var clearedState) && !float.IsFinite(clearedState.DisclosureT);

        Check("virtual-disclosure.5 midpoint hit testing clips the body and maps the translated suffix",
            idle && armed && retargeted && bodyHit == b && suffixHit == d && restingHit == c && cleared,
            $"idle={idle} armed={armed} retarget={retargeted} body={bodyHit == b} suffix={suffixHit == d} resting={restingHit == c} cleared={cleared}");

        var census = new SceneStore();
        var root = census.CreateNode(1);
        census.Root = root;
        var viewportA = census.CreateNode(1);
        var contentA = census.CreateNode(1);
        var viewportB = census.CreateNode(1);
        var contentB = census.CreateNode(1);
        census.AppendChild(root, viewportA);
        census.AppendChild(viewportA, contentA);
        census.AppendChild(root, viewportB);
        census.AppendChild(viewportB, contentB);
        ref ScrollState scrollA = ref census.ScrollRef(viewportA);
        scrollA.ContentNode = contentA;
        scrollA.ItemCount = 1;
        ref ScrollState scrollB = ref census.ScrollRef(viewportB);
        scrollB.ContentNode = contentB;
        scrollB.ItemCount = 1;

        bool both = census.BeginVirtualDisclosure(viewportA, 0, 1, 0f, 10f, 0f)
            && census.BeginVirtualDisclosure(viewportB, 0, 1, 0f, 10f, 1f)
            && census.HasActiveVirtualDisclosures;
        census.ClearVirtualDisclosure(viewportA);
        bool oneRemains = census.HasActiveVirtualDisclosures;
        census.ClearVirtualDisclosure(viewportA);
        bool repeatClearSafe = census.HasActiveVirtualDisclosures;
        census.FreeSubtree(viewportB);
        bool freeClearsLast = !census.HasActiveVirtualDisclosures;

        Check("virtual-disclosure.6 concurrent, repeated-clear, and viewport-free census edges stay balanced",
            both && oneRemains && repeatClearSafe && freeClearsLast,
            $"both={both} one={oneRemains} repeat={repeatClearSafe} free={freeClearsLast}");
    }

    sealed class VirtualDisclosureProbe : Component
    {
        public readonly Signal<int> Count = new(5);
        public readonly Signal<int> SourceVersion = new(0);
        public readonly ItemsViewController Controller = new();
        public readonly List<ItemDisclosureDiagnostic> Diagnostics = [];
        private string[] _labels = ["A", "B", "C", "D", "E"];
        private float[] _heights = [28f, 36f, 44f, 32f, 40f];

        public void CommitCollapsed() => Publish(["A", "D", "E"], [28f, 32f, 40f]);
        public void RestoreExpanded() => Publish(["A", "B", "C", "D", "E"], [28f, 36f, 44f, 32f, 40f]);

        private void Publish(string[] labels, float[] heights)
        {
            void Mutate()
            {
                _labels = labels;
                _heights = heights;
                Count.Value = labels.Length;
                SourceVersion.Value = SourceVersion.Peek() + 1;
            }
            if (Context.Runtime is { } runtime) runtime.Batch(Mutate);
            else Mutate();
        }

        public override Element Render() => Embed.Comp(() => new ItemsView
        {
            ItemCount = 8,
            ItemCountSignal = Count,
            BoundMode = true,
            RowTemplate = scope => new BoxEl
            {
                Height = Prop.Of(() => { _ = SourceVersion.Value; return _heights[scope.Index.Value]; }),
                Fill = Tok.FillSubtleSecondary,
                Children =
                [
                    new TextEl("")
                    {
                        Text = Prop.Of(() => { _ = SourceVersion.Value; return _labels[scope.Index.Value]; }),
                        Size = 12f,
                    },
                ],
            },
            Layout = RepeatLayout.VariableList(32f),
            HasExplicitLayout = true,
            SelectionMode = ItemsSelectionMode.None,
            Selector = SelectorVisual.None,
            Controller = Controller,
            Disclosure = new DisclosureOptions
            {
                Version = SourceVersion,
                Diagnostic = Diagnostics.Add,
            },
            Grow = 1f,
        });
    }

    static void FocusRingChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("focusring", new Size2(300, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new FocusRingProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();

        // Pointer focus: click the button — NO ring may appear (keyboard-only focus visuals).
        var btn = Child(host.Scene, host.Scene.Root, 0);
        ClickNode(host, window, btn);
        host.RunFrame();
        bool pointerSilent = device.LastStrokes.Count == 0;

        // Keyboard focus: the click focused button1, so Tab lands on button2 — the ASYMMETRIC margin (−7,0,−7,0):
        // 100×40 ⇒ focus rect (−7,0,114,40); primary centerline inset 1 ⇒ (−6,1,112,38).
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        DrawRoundRectStrokeCmd p2 = default;
        foreach (var s in device.LastStrokes) if (Near(s.StrokeWidth, 2f)) p2 = s;
        bool asym = Near(p2.Rect.X, -6f) && Near(p2.Rect.Y, 1f) && Near(p2.Rect.W, 112f) && Near(p2.Rect.H, 38f);
        Check("E1.b FocusVisualMargin −7,0,−7,0 widens the ring horizontally only (Slider shape)", asym,
            $"prim=({p2.Rect.X:0.#},{p2.Rect.Y:0.#} {p2.Rect.W:0.#}x{p2.Rect.H:0.#})");

        // Second Tab wraps to button1 — the DEFAULT margin −3: focus rect (−3,−3,106,46); primary inset 1; secondary 2.5.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        DrawRoundRectStrokeCmd primary = default, secondary = default;
        foreach (var s in device.LastStrokes)
        {
            if (Near(s.StrokeWidth, 2f)) primary = s;
            else if (Near(s.StrokeWidth, 1f)) secondary = s;
        }
        bool primaryGeom = Near(primary.Rect.X, -2f) && Near(primary.Rect.Y, -2f) && Near(primary.Rect.W, 104f) && Near(primary.Rect.H, 44f);
        bool secondaryGeom = Near(secondary.Rect.X, -0.5f) && Near(secondary.Rect.Y, -0.5f) && Near(secondary.Rect.W, 101f) && Near(secondary.Rect.H, 41f);
        bool colors = ColorClose(primary.Color, Tok.FocusOuter, 0.004f) && ColorClose(secondary.Color, Tok.FocusInner, 0.004f);
        Check("E1.a keyboard focus draws the WinUI dual ring OUTSIDE the bounds (pointer focus stays bare)",
            pointerSilent && primaryGeom && secondaryGeom && colors,
            $"ptr={pointerSilent} prim=({primary.Rect.X:0.#},{primary.Rect.Y:0.#} {primary.Rect.W:0.#}x{primary.Rect.H:0.#}) " +
            $"sec=({secondary.Rect.X:0.#},{secondary.Rect.Y:0.#} {secondary.Rect.W:0.#}x{secondary.Rect.H:0.#}) colors={colors}");

        // Light theme: FocusStrokeColorInner is #B3FFFFFF (white @ 0.70) — the audit's alpha fix.
        var light = Tok.Light.FocusInner;
        Check("E1.c Light FocusStrokeColorInner = #B3FFFFFF (alpha corrected)",
            ColorClose(light, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3), 0.004f),
            $"A={light.A:0.###} R={light.R:0.###}");

        // E1.d — a focused ClipsToBounds control (a TextBox field) must NOT scissor away its own ring: the ring is
        // recorded AFTER the node's clip pops, so its strokes decode at the PARENT clip depth (0 here), full geometry.
        {
            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("focusclip", new Size2(300, 200), 1f));
            window2.Show();
            var device2 = new HeadlessGpuDevice();
            var root2 = new FocusClipProbe();
            using var host2 = new AppHost(app2, window2, device2, fonts, strings, root2);
            host2.RunFrame();
            window2.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host2.RunFrame();
            bool ringFound = false, outsideClip = true, geom = false;
            for (int i = 0; i < device2.LastStrokes.Count; i++)
            {
                var s = device2.LastStrokes[i];
                if (!Near(s.StrokeWidth, 2f)) continue;
                ringFound = true;
                outsideClip &= device2.LastStrokeClipDepths[i] == 0;
                geom = Near(s.Rect.X, -2f) && Near(s.Rect.W, 104f);
            }
            Check("E1.d focus ring escapes the focused node's OWN ClipsToBounds scissor (clipped-TextBox-ring fix)",
                ringFound && outsideClip && geom && device2.LastClips.Count > 0,
                $"found={ringFound} depth0={outsideClip} geom={geom} clips={device2.LastClips.Count}");
        }
    }

    static void FocusNavChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // P6.a — TabIndex orders tab navigation: document order A,B,C but TabIndex 3,1,2 → visits B→C→A.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10,
                Children =
                [
                    new BoxEl { Key = "A", Width = 20, Height = 20, OnClick = () => { }, TabIndex = 3 },
                    new BoxEl { Key = "B", Width = 20, Height = 20, OnClick = () => { }, TabIndex = 1 },
                    new BoxEl { Key = "C", Width = 20, Height = 20, OnClick = () => { }, TabIndex = 2 },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var a = Child(scene, scene.Root, 0); var b = Child(scene, scene.Root, 1); var c = Child(scene, scene.Root, 2);
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) }); var f1 = disp.Focused;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) }); var f2 = disp.Focused;
            disp.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, Keys.Tab) }); var f3 = disp.Focused;
            Check("W1-P6.a TabIndex orders tab navigation (1→2→3, not document order)",
                f1 == b && f2 == c && f3 == a, $"f1=B?{f1 == b} f2=C?{f2 == c} f3=A?{f3 == a}");
        }

        // P6.b — XY arrow navigation across a 2×2 grid: Right→Down→Left→Up walks TL→TR→BR→BL→TL.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1, Gap = 10,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = 10, Children = [
                        new BoxEl { Key = "TL", Width = 30, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "TR", Width = 30, Height = 20, OnClick = () => { } } ] },
                    new BoxEl { Direction = 0, Gap = 10, Children = [
                        new BoxEl { Key = "BL", Width = 30, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "BR", Width = 30, Height = 20, OnClick = () => { } } ] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var r0 = Child(scene, scene.Root, 0); var r1 = Child(scene, scene.Root, 1);
            var tl = Child(scene, r0, 0); var tr = Child(scene, r0, 1);
            var bl = Child(scene, r1, 0); var br = Child(scene, r1, 1);
            disp.SetFocus(tl, visual: true);
            disp.MoveFocusArrow(FocusDirection.Right); var aRight = disp.Focused;
            disp.MoveFocusArrow(FocusDirection.Down); var aDown = disp.Focused;
            disp.MoveFocusArrow(FocusDirection.Left); var aLeft = disp.Focused;
            disp.MoveFocusArrow(FocusDirection.Up); var aUp = disp.Focused;
            Check("W1-P6.b arrow XY nav walks the 2×2 grid (R→D→L→U)",
                aRight == tr && aDown == br && aLeft == bl && aUp == tl,
                $"R=TR?{aRight == tr} D=BR?{aDown == br} L=BL?{aLeft == bl} U=TL?{aUp == tl}");
        }

        // P6.c — scoped roving: NextFocusableIn cycles within a subtree and never escapes to an outside focusable.
        {
            var scene = new SceneStore();
            new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl { Key = "outside", Width = 20, Height = 20, OnClick = () => { } },
                    new BoxEl { Key = "sub", Direction = 0, Gap = 4, Children = [
                        new BoxEl { Key = "s1", Width = 20, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "s2", Width = 20, Height = 20, OnClick = () => { } },
                        new BoxEl { Key = "s3", Width = 20, Height = 20, OnClick = () => { } } ] },
                ],
            }, null);
            new FlexLayout(scene, fonts).Run(scene.Root);
            var disp = new InputDispatcher(scene);
            var outside = Child(scene, scene.Root, 0);
            var sub = Child(scene, scene.Root, 1);
            var s1 = Child(scene, sub, 0); var s3 = Child(scene, sub, 2);
            var next = disp.NextFocusableIn(sub, s1);          // s1 → s2
            var wrap = disp.NextFocusableIn(sub, s3);          // s3 → s1 (cycles, never escapes to 'outside')
            var first = disp.FirstFocusableIn(sub);
            var last = disp.LastFocusableIn(sub);
            Check("W1-P6.c scoped roving cycles within a subtree (never escapes)",
                next == Child(scene, sub, 1) && wrap == s1 && first == s1 && last == s3 && first != outside,
                $"next=s2?{next == Child(scene, sub, 1)} wrap=s1?{wrap == s1} first=s1?{first == s1} last=s3?{last == s3}");
        }
    }

    static void ClipChannelChecks()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new RectF(0f, 0f, 100f, 80f);
        var engine = new AnimEngine(scene);

        bool startInfinite = scene.Paint(node).ClipRect.IsInfinite;   // no clip before any animation

        // Reveal the bottom edge 0 → 80 (a one-edge clip; L/T/R default to the node box → only the bottom clips).
        engine.Animate(node, AnimChannel.ClipB, 0f, 80f, 100f, Easing.Linear);
        engine.Tick(0f);
        engine.Tick(50f);
        var mid = scene.Paint(node).ClipRect;
        bool applied = !mid.IsInfinite && Near(mid.X, 0f) && Near(mid.Y, 0f) && Near(mid.W, 100f) && Near(mid.H, 40f, 2f);

        engine.Tick(60f);   // 110ms total → animation completes and the clip override clears
        bool reset = scene.Paint(node).ClipRect.IsInfinite;

        Check("W1-P4a.a clip-rect channel applies mid-anim (bottom reveal), resets on settle",
            startInfinite && applied && reset,
            $"mid=({mid.X:0},{mid.Y:0},{mid.W:0},{mid.H:0}) start∞={startInfinite} reset∞={reset}");
    }

    static void DisabledChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("disabled", new Size2(320, 240), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new DisabledProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // mount — the gated box starts disabled

        // disabled-no-hit: clicking the disabled box invokes nothing; the always-enabled box still clicks.
        ClickNode(host, window, root.GatedBox);
        int gatedAfterDisabledClick = root.GatedClicks;
        ClickNode(host, window, root.EnabledBox);
        Check("W1-P1.a disabled node does not hit-test (click swallowed); enabled still clicks",
            gatedAfterDisabledClick == 0 && root.EnabledClicks == 1,
            $"gated={gatedAfterDisabledClick} enabled={root.EnabledClicks}");

        // disabled-no-focus: Tab skips the disabled box → focus lands on the only enabled focusable.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        bool focusEnabled = FocusedNode(host.Scene, host.Scene.Root) == root.EnabledBox;
        bool gatedNotFocused = (host.Scene.Flags(root.GatedBox) & NodeFlags.Focused) == 0;
        Check("W1-P1.b disabled node is not a tab stop (focus skips it)", focusEnabled && gatedNotFocused,
            $"focusEnabled={focusEnabled} gatedFocused={!gatedNotFocused}");

        // disabled-no-key-activate: Enter activates the focused ENABLED box (pressed on down, click on key-UP — the
        // WinUI ClickMode.Release contract); the disabled box never key-activates.
        int beforeEnter = root.EnabledClicks;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
        window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter));
        host.RunFrame();
        Check("W1-P1.c Enter activates the focused enabled node; disabled never key-activates",
            root.EnabledClicks == beforeEnter + 1 && root.GatedClicks == 0,
            $"enabled={root.EnabledClicks} gated={root.GatedClicks}");

        // disabled-toggle-reenables: flip IsEnabled via the signal → the box now hit-tests and clicks.
        root.Gate!.Value = true;
        host.RunFrame();
        ClickNode(host, window, root.GatedBox);
        Check("W1-P1.d flipping IsEnabled re-enables hit-test (Mark/Unmark each reconcile)",
            root.GatedClicks == 1, $"gated={root.GatedClicks}");

        // zero-alloc: the gate is a flag bittest — steady idle frames allocate nothing on the paint half.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("W1-P1.e disabled gate adds no steady-state allocation", steady.HotPhaseAllocBytes == 0,
            $"{steady.HotPhaseAllocBytes} bytes");
    }

    static void TextRampChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("textramp", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new TextRampProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // mount
        var box = host.Scene.Root;
        var c = CenterOf(host.Scene, box);
        var outside = new Point2(c.X + 300f, c.Y + 300f);

        var rest = GlyphColor(device, strings, "ramp");
        bool restOk = rest.R > 0.5f && rest.G < 0.2f && rest.B < 0.2f;   // resting = red

        // hover → green (eased through the ancestor box's interaction progress)
        window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var hov = GlyphColor(device, strings, "ramp");
        bool hovOk = hov.G > 0.5f && hov.R < 0.2f;

        // press → blue (press composes over hover)
        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var prs = GlyphColor(device, strings, "ramp");
        bool prsOk = prs.B > 0.5f && prs.G < 0.2f;

        // release + leave → back to red
        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var back = GlyphColor(device, strings, "ramp");
        bool backOk = back.R > 0.5f && back.G < 0.2f;

        Check("W1-P2.a text foreground ramps: hover→green, press→blue, release→red",
            restOk && hovOk && prsOk && backOk,
            $"rest=({rest.R:0.0},{rest.G:0.0},{rest.B:0.0}) hov=({hov.R:0.0},{hov.G:0.0},{hov.B:0.0}) prs=({prs.R:0.0},{prs.G:0.0},{prs.B:0.0}) back=({back.R:0.0},{back.G:0.0},{back.B:0.0})");

        // disabled → white (a step, regardless of pointer position; gated by the ancestor's NodeFlags.Disabled)
        root.Enabled!.Value = false;
        host.RunFrame();
        var dis = GlyphColor(device, strings, "ramp");
        Check("W1-P2.b disabled text uses the DisabledColor step", dis.R > 0.8f && dis.G > 0.8f && dis.B > 0.8f,
            $"dis=({dis.R:0.00},{dis.G:0.00},{dis.B:0.00})");

        // zero-alloc: the resolve walks ancestors with struct reads only — steady idle frames allocate nothing.
        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("W1-P2.c text ramp adds no steady-state allocation", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");
    }

    static void GradientRampChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("gradramp", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new GradientRampProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var c = CenterOf(host.Scene, host.Scene.Root);
        var outside = new Point2(c.X + 300f, c.Y + 300f);

        var rest = FirstGradientC0(device);
        bool restOk = rest.R > 0.5f && rest.G < 0.2f && rest.B < 0.2f;

        window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var hov = FirstGradientC0(device);
        bool hovOk = hov.G > 0.5f && hov.R < 0.2f;

        window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var prs = FirstGradientC0(device);
        bool prsOk = prs.B > 0.5f && prs.G < 0.2f;

        window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0));
        for (int i = 0; i < 24; i++) host.RunFrame();
        var back = FirstGradientC0(device);
        bool backOk = back.R > 0.5f && back.G < 0.2f;

        Check("W1-P4b.a gradient fill ramps: hover→green, press→blue, release→red",
            restOk && hovOk && prsOk && backOk,
            $"rest=({rest.R:0.0},{rest.G:0.0},{rest.B:0.0}) hov=({hov.R:0.0},{hov.G:0.0},{hov.B:0.0}) prs=({prs.R:0.0},{prs.G:0.0},{prs.B:0.0}) back=({back.R:0.0},{back.G:0.0},{back.B:0.0})");

        for (int i = 0; i < 6; i++) host.RunFrame();
        var steady = host.RunFrame();
        Check("W1-P4b.b gradient ramp adds no steady-state allocation", steady.HotPhaseAllocBytes == 0, $"{steady.HotPhaseAllocBytes} bytes");
    }

    static void PipsPagerOutputChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("pipsout", new Size2(320, 160), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new PipsPagerOutputProbe());
        host.RunFrame();

        var pips = Roles(host.Scene, AutomationRole.Pager);
        bool initial = HasGlyph(device, strings, "Page 1 / 5");
        if (pips.Count > 2) ClickNode(host, window, pips[2]);   // index 1
        bool odd = HasGlyph(device, strings, "Page 2 / 5");
        pips = Roles(host.Scene, AutomationRole.Pager);
        if (pips.Count > 3) ClickNode(host, window, pips[3]);   // index 2, the reported blank-output path
        bool even = HasGlyph(device, strings, "Page 3 / 5");

        Check("65b. PipsPager output TextBind survives owner re-render for even selected indices",
            pips.Count >= 6 && initial && odd && even,
            $"pips={pips.Count} initial={initial} odd={odd} even={even}");
    }

    static void BasicInputControlChecks(StringTable strings)
    {
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        // CheckBox — two/three-state cycle.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cb", new Size2(320, 160), 1f)); window.Show();
            var root = new CheckBoxProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var s0 = root.State;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox)); var s1 = root.State;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox)); var s2 = root.State;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox)); var s3 = root.State;
            Check("66. CheckBox cycles Unchecked→Checked→Indeterminate→Unchecked",
                s0 == CheckState.Unchecked && s1 == CheckState.Checked && s2 == CheckState.Indeterminate && s3 == CheckState.Unchecked,
                $"{s0}→{s1}→{s2}→{s3}");
        }

        // 66b — the LIVE checkmark DRAW-ON through AppHost (the EXACT click path the gallery uses). Toggling
        // unchecked→checked must leave the checkmark mid-DRAW on the click frame: its clip box's presented width is
        // sweeping 0→full (the stroke drawing itself, WinUI-style), then settles (reveal resets to NaN). If nothing is
        // revealing on that frame, the draw-on isn't running — the "animation not showing" report.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cbanim", new Size2(320, 160), 1f)); window.Show();
            var root = new CheckBoxProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();   // mount unchecked
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox));   // toggle → checked + 1 frame
            var box = Child(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox), 0);
            float t0 = ActiveStrokeTrimEnd(host.Scene, box);
            clock.Advance(80f);
            host.RunFrame();
            float t1 = ActiveStrokeTrimEnd(host.Scene, box);
            bool drewPolyline = device.LastPolylines.Count > 0 && device.LastPolylines[0].TrimEnd > 0f && device.LastPolylines[0].TrimEnd < 1f;
            bool drawing = Near(t0, 0f, 0.001f) && t1 > 0.01f && t1 < 1f && host.Animation.HasActive && drewPolyline;
            clock.Advance(400f);
            host.RunFrame();
            bool settled = float.IsNaN(ActiveStrokeTrimEnd(host.Scene, box)) && !host.Animation.HasActive;
            Check("66b. LIVE: toggling a CheckBox DRAWS the checkmark in (stroke-trim polyline, AppHost click path)",
                drawing && settled, $"trim {t0:0.00}->{t1:0.00} poly={device.LastPolylines.Count} settled={settled}");
        }

        // RadioButton — mutual exclusion via a shared index.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rb", new Size2(320, 200), 1f)); window.Show();
            var root = new RadioProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var radios = Roles(host.Scene, AutomationRole.RadioButton);
            ClickNode(host, window, radios[1]); int sel1 = root.Selected;
            radios = Roles(host.Scene, AutomationRole.RadioButton);
            ClickNode(host, window, radios[2]); int sel2 = root.Selected;
            Check("67. RadioButton group: selecting one deselects the others",
                radios.Count == 3 && sel1 == 1 && sel2 == 2, $"count={radios.Count} sel {sel1}→{sel2}");
        }

        // ToggleSwitch — flips on/off.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("ts", new Size2(320, 160), 1f)); window.Show();
            var root = new ToggleSwitchProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch)); bool on1 = root.On;
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch)); bool on2 = root.On;
            Check("68. ToggleSwitch toggles on/off", on1 && !on2, $"{on1}→{on2}");
        }

        // RatingControl — click sets, drag sweeps.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rt", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            // WinUI percentage model: rating = ceil(x / actualRatingWidth * Max), actualRatingWidth = Max*16 + (Max-1)*8 = 112.
            var p3 = new Point2(rr.X + 56f, rr.Y + rr.H / 2f);   // x=56 -> 56/112*5=2.5 -> ceil=3 (3rd star)
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p3, 0, 0));
            host.RunFrame();
            float v3 = root.Val!.Peek();
            var p5 = new Point2(rr.X + 110f, rr.Y + rr.H / 2f); // x=110 -> 110/112*5=4.91 -> ceil=5 (sweep to 5th)
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, p5, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p5, 0, 0));
            host.RunFrame();
            float v5 = root.Val!.Peek();
            Check("69. RatingControl: click sets value, drag sweeps", Near(v3, 3f) && Near(v5, 5f), $"click={v3} drag={v5}");

            // 69b. Keyboard (Left/Right/Home/End) + IsClearEnabled clear-on-reclick. The prior click focused the row,
            // so arrow keys bubble to the control's OnKeyDown. Value starts at 5 (from the sweep above).
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));   // 5 -> 4
            host.RunFrame(); float kLeft = root.Val!.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Home));   // -> clear (-1)
            host.RunFrame(); float kHome = root.Val!.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));  // unset + Right -> InitialSetValue (1)
            host.RunFrame(); float kRight = root.Val!.Peek();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.End));    // -> MaxRating (5)
            host.RunFrame(); float kEnd = root.Val!.Peek();
            // Re-click the current value (5) with IsClearEnabled (default true) -> clears to -1.
            window.QueueInput(new InputEvent(InputKind.PointerDown, p5, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p5, 0, 0));
            host.RunFrame(); float reClear = root.Val!.Peek();
            Check("69b. RatingControl: keyboard range + clear-on-reclick",
                Near(kLeft, 4f) && Near(kHome, -1f) && Near(kRight, 1f) && Near(kEnd, 5f) && Near(reClear, -1f),
                $"L={kLeft} Home={kHome} R={kRight} End={kEnd} reclick={reClear}");
        }

        // 69c. RatingControl read-only: pointer + keyboard are inert (fixed rating).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rt-ro", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe { ReadOnly = true, Initial = 3f };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            var pp = new Point2(rr.X + 110f, rr.Y + rr.H / 2f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, pp, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, pp, 0, 0));
            host.RunFrame();
            Check("69c. RatingControl read-only: input is inert", Near(root.Val!.Peek(), 3f), $"val={root.Val!.Peek()}");
        }

        // 69d. RatingControl bare-hover PREVIEW: a pointer MOVE with NO button down fills the stars to the cursor
        // (WinUI OnPointerMovedOverBackgroundStackPanel) -- the foreground clip layer widens to the hovered rating.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rt-hov", new Size2(320, 120), 1f)); window.Show();
            var root = new RatingProbe { Initial = RatingControl.NoValueSet };   // unset -> resting foreground clipped to 0
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            const string filledStar = "";   // RatingControl filled glyph (E735); each FULL star renders exactly one
            int restFilled = CountGlyph(device, strings, filledStar);   // unset -> 0 filled (single-glyph rows, no overlay halo)
            // BARE hover (no PointerDown): x=56 -> ceil(56/112*5)=3 stars filled.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rr.X + 56f, rr.Y + rr.H / 2f), 0, 0));
            host.RunFrame();
            int hovFilled = CountGlyph(device, strings, filledStar);
            bool committedNothing = root.Val!.Peek() <= RatingControl.NoValueSet;   // preview only — not committed
            // Pointer EXIT (move far off the strip): the preview drops and the stars revert to the committed rating (0 filled).
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rr.Right + 240f, rr.Bottom + 240f), 0, 0));
            host.RunFrame();
            int exitFilled = CountGlyph(device, strings, filledStar);
            bool coerce = Near(RatingControl.Coerce(0.5f, 5), 1f) && Near(RatingControl.Coerce(-3f, 5), -1f)
                && Near(RatingControl.Coerce(0f, 5), 1f) && Near(RatingControl.Coerce(3.4f, 5), 3.4f) && Near(RatingControl.Coerce(9f, 5), 5f);
            Check("69d. RatingControl: bare-hover fills 3 (single-glyph, no overlay), reverts on pointer-exit, no commit",
                restFilled == 0 && hovFilled == 3 && committedNothing && exitFilled == 0 && coerce,
                $"rest={restFilled} hov={hovFilled} exit={exitFilled} committed={!committedNothing} coerce={coerce}");
        }

        // ComboBox — closed selection.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cmb", new Size2(420, 320), 1f)); window.Show();
            var root = new ComboProbe(false);
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox));
            var menuItems = Roles(host.Scene, AutomationRole.MenuItem);
            bool opened = menuItems.Count == 3;
            int sel = -2;
            if (opened) { ClickNode(host, window, menuItems[1]); sel = root.Sel!.Peek(); }
            Check("70. ComboBox: opens a list and selects an item", opened && sel == 1, $"items={menuItems.Count} sel={sel}");
        }

        // AutoSuggestBox -- open popup width matches the owning field (WinUI popup/list is field-width, not content-width).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("asb", new Size2(420, 320), 1f)); window.Show();
            var root = new AutoSuggestProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();   // mount + post-commit open effect
            host.RunFrame();   // overlay content
            var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox);
            var rows = Roles(host.Scene, AutomationRole.MenuItem);
            float fieldW = host.Scene.AbsoluteRect(field).W;
            float rowW = rows.Count > 0 ? host.Scene.AbsoluteRect(rows[0]).W : 0f;
            Check("70a. AutoSuggestBox: suggestions popup width matches the field", rows.Count == 5 && rowW >= fieldW - 16f,
                $"rows={rows.Count} fieldW={fieldW:0.#} rowW={rowW:0.#}");
        }

        // ComboBox — editable text entry.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cmbe", new Size2(420, 320), 1f)); window.Show();
            var root = new ComboProbe(true);
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Text));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'l'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'o'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'w'));
            host.RunFrame();
            string txt = root.Txt!.Peek();
            Check("71. ComboBox: editable mode accepts typed text", txt == "low", $"text='{txt}'");
        }

        // Slider (ranged) — value range mapping + step snapping.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("rsl", new Size2(320, 120), 1f)); window.Show();
            var root = new RangeSliderProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var mid = new Point2(tr.X + 100f, tr.Y + tr.H / 2f);   // raw 0.5 of length 200 → 50, snapped to step 10
            window.QueueInput(new InputEvent(InputKind.PointerDown, mid, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, mid, 0, 0));
            host.RunFrame();
            float v = root.Val;
            Check("72a. Slider (ranged options): maps to [min,max] and snaps to step", Near(v, 50f), $"value={v}");
        }

        // ColorPicker — hue / spectrum / alpha drags + a hex channel edit.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp", new Size2(420, 420), 1f)); window.Show();
            var root = new ColorPickerProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            bool gradientsDrawn = device.LastGradients.Count >= 9;   // spectrum(2) + hue(6) + alpha(1)
            var sliders = Roles(host.Scene, AutomationRole.Slider);   // [spectrum, hue, alpha]
            var sr = host.Scene.AbsoluteRect(sliders[0]);
            bool spectrumSquare = Near(sr.W, 256f) && Near(sr.H, 256f);
            void DragTo(NodeHandle n, float fx, float fy)
            {
                var r = host.Scene.AbsoluteRect(n);
                var p = new Point2(r.X + r.W * fx, r.Y + r.H * fy);
                window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
                window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
                host.RunFrame();
            }
            DragTo(sliders[1], 0.5f, 0.5f);  var hueHsv = root.Color!.Peek().ToHsv();
            DragTo(sliders[0], 0.5f, 0.5f);  var sv = root.Color!.Peek().ToHsv();
            DragTo(sliders[2], 0.5f, 0.5f);  float a = root.Color!.Peek().A;

            // Hex channel: clear the field and type a pure green.
            var hex = Roles(host.Scene, AutomationRole.Text)[0];
            ClickNode(host, window, hex);
            for (int i = 0; i < 6; i++) window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back));
            foreach (char ch in "00FF00") window.QueueInput(new InputEvent(InputKind.Char, default, 0, ch));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
            host.RunFrame();
            var ce = root.Color!.Peek();

            bool hueOk = MathF.Abs(hueHsv.H - 180f) < 45f;
            bool svOk = sv.S > 0.3f && sv.S < 0.7f && sv.V > 0.3f && sv.V < 0.7f;
            bool alphaOk = a > 0.3f && a < 0.7f;
            bool hexOk = ce.G > 0.8f && ce.R < 0.2f && ce.B < 0.2f;
            Check("72. ColorPicker: hue/spectrum/alpha drags + hex channel update the color",
                gradientsDrawn && spectrumSquare && hueOk && svOk && alphaOk && hexOk,
                $"gradients={device.LastGradients.Count} spectrum={sr.W:0}x{sr.H:0} H={hueHsv.H:0} S={sv.S:0.00} V={sv.V:0.00} A={a:0.00} hex=({ce.R:0.0},{ce.G:0.0},{ce.B:0.0})");
        }
    }

    static void W1ControlsChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // Recorder geometry: rect/stroke commands carry the NODE-LOCAL rect with the absolute placement on Transform.
        static FillRoundRectCmd FillAt(HeadlessGpuDevice dev, RectF abs)
        {
            foreach (var r in dev.LastRects)
                if (Near(r.Transform.Dx, abs.X, 0.6f) && Near(r.Transform.Dy, abs.Y, 0.6f)
                    && Near(r.Rect.W, abs.W, 0.6f) && Near(r.Rect.H, abs.H, 0.6f))
                    return r;
            return default;
        }
        static DrawRoundRectStrokeCmd StrokeOfWidth(HeadlessGpuDevice dev, float strokeW, float rectW)
        {
            foreach (var s in dev.LastStrokes)
                if (Near(s.StrokeWidth, strokeW, 0.01f) && Near(s.Rect.W, rectW, 0.6f)) return s;
            return default;
        }

        // w1controls.1 — Button: color-only states (NO scale — WinUI Button state storyboards swap brushes only,
        // Button_themeresources.xaml:176-229), content centred (Control defaults HorizontalContentAlignment /
        // VerticalContentAlignment = Center, DependencyProperty.cpp:646-652), resting fill = ButtonBackground =
        // ControlFillColorDefault (Button_themeresources.xaml:30/128), FocusVisualMargin −3 (:167).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1btn", new Size2(360, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1ButtonProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var btn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
            var br = host.Scene.AbsoluteRect(btn);
            var fill = FillAt(device, br).Fill;
            bool restFill = ColorClose(fill, Tok.FillControlDefault, 0.004f);

            var label = FindTextNode(host.Scene, strings, host.Scene.Root, "w1-btn");
            var lr = host.Scene.AbsoluteRect(label);
            bool centred = Near(lr.X + lr.W / 2f, br.X + br.W / 2f, 1f) && Near(lr.Y + lr.H / 2f, br.Y + br.H / 2f, 1f);

            bool noScaleWired = !host.Scene.TryGetInteract(btn, out var ia)
                || (Near(ia.HoverScale, 1f, 0.001f) && Near(ia.PressScale, 1f, 0.001f));
            var c = CenterOf(host.Scene, btn);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            bool pressIdentity = true;
            for (int i = 0; i < 6; i++)
            {
                host.RunFrame();
                var t = host.Scene.Paint(btn).LocalTransform;
                pressIdentity &= Near(t.M11, 1f, 0.001f) && Near(t.M22, 1f, 0.001f) && Near(t.Dx, 0f, 0.001f) && Near(t.Dy, 0f, 0.001f);
            }
            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            host.RunFrame();

            Check("w1controls.1 Button: NO scale on press (color-only states), content centres both axes, resting fill = ControlFillColorDefault (ARGB)",
                restFill && centred && noScaleWired && pressIdentity && root.Clicks == 1,
                $"fillA={fill.A:0.###} centred={centred} noScale={noScaleWired} identity={pressIdentity} clicks={root.Clicks}");

            // The −3 focus rect inset by the 2px primary's 1px centerline → local (−2,−2,W+4,H+4); 1px secondary at −0.5.
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            DrawRoundRectStrokeCmd prim = default, sec = default;
            foreach (var s in device.LastStrokes)
            {
                if (Near(s.StrokeWidth, 2f)) prim = s;
                else if (Near(s.StrokeWidth, 1f)) sec = s;
            }
            bool primGeom = Near(prim.Rect.X, -2f) && Near(prim.Rect.Y, -2f) && Near(prim.Rect.W, br.W + 4f) && Near(prim.Rect.H, br.H + 4f);
            bool secGeom = Near(sec.Rect.X, -0.5f) && Near(sec.Rect.Y, -0.5f) && Near(sec.Rect.W, br.W + 1f) && Near(sec.Rect.H, br.H + 1f);
            Check("w1controls.1b Button keyboard focus ring honours FocusVisualMargin −3 (primary −2,−2,+4,+4; secondary −0.5)",
                primGeom && secGeom,
                $"prim=({prim.Rect.X:0.#},{prim.Rect.Y:0.#} {prim.Rect.W:0.#}x{prim.Rect.H:0.#}) sec=({sec.Rect.X:0.#},{sec.Rect.Y:0.#} {sec.Rect.W:0.#}x{sec.Rect.H:0.#})");
        }

        // w1controls.2 — RepeatButton cadence-exact: Delay = 500ms, Interval = 33ms — the WinUI DP metadata defaults
        // (dxaml\xcp\components\DependencyObject\DependencyProperty.cpp:714-720), sampled on a manual clock.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1rpt", new Size2(320, 200), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new RepeatProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var btn = FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);
            var c = CenterOf(host.Scene, btn);

            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            host.RunFrame();                                  // arm → fires once immediately
            int onPress = root.Clicks;                        // 1
            clock.Advance(499f); host.RunFrame();
            int at499 = root.Clicks;                          // still 1 (inside the 500ms initial delay)
            clock.Advance(1f); host.RunFrame();
            int at500 = root.Clicks;                          // 2 — fired exactly at the 500ms boundary
            clock.Advance(32f); host.RunFrame();
            int at532 = root.Clicks;                          // still 2 (inside the 33ms interval)
            clock.Advance(1f); host.RunFrame();
            int at533 = root.Clicks;                          // 3 — fired exactly at the 33ms boundary
            clock.Advance(66f); host.RunFrame();
            int at599 = root.Clicks;                          // 5 — a slow frame fires once per elapsed interval
            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            host.RunFrame();
            clock.Advance(1000f); host.RunFrame();
            int afterRelease = root.Clicks;

            Check("w1controls.2 RepeatButton cadence-exact: fire on press, again at exactly 500ms, then every 33ms (catch-up on a slow frame); release stops",
                onPress == 1 && at499 == 1 && at500 == 2 && at532 == 2 && at533 == 3 && at599 == 5 && afterRelease == 5,
                $"press={onPress} 499={at499} 500={at500} 532={at532} 533={at533} 599={at599} rel={afterRelease}");
        }

        // w1controls.3 — HyperlinkButton: the ONE WinUI control with a hand cursor (SetCursor(MouseCursorHand) at
        // initialize, HyperLinkButton_Partial.cpp:28-34); Click raises FIRST, then the NavigateUri launches through the
        // IPlatformApp.OpenUri PAL seam (Click → Launcher::TryInvokeLauncher, HyperLinkButton_Partial.cpp:149-177 —
        // headless records into OpenedUris instead of launching).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1link", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1HyperlinkProbe { App = app };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var link = FindRole(host.Scene, host.Scene.Root, AutomationRole.Hyperlink);
            var c = CenterOf(host.Scene, link);
            window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
            host.RunFrame();
            bool hand = window.LastCursor == CursorId.Hand;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(5f, 5f), 0, 0));
            host.RunFrame();
            bool arrowOff = window.LastCursor == CursorId.Arrow;

            ClickNode(host, window, link);
            bool launched = app.OpenedUris.Count == 1 && app.OpenedUris[0] == "https://wavee.app/w1";
            bool clickFirst = root.UrisAtClick == 0;

            Check("w1controls.3 HyperlinkButton: hand cursor on hover (arrow off-control); Click→OpenUri records the NavigateUri in WinUI order (Click first)",
                hand && arrowOff && launched && clickFirst,
                $"hand={hand} arrow={arrowOff} uris=[{string.Join(",", app.OpenedUris)}] urisAtClick={root.UrisAtClick}");
        }

        // w1controls.4 — ToggleButton checked flip: the fill cross-fades over the 83ms ContentPresenter.BackgroundTransition
        // (ToggleButton_themeresources.xaml:199-201) while the FOREGROUND flips discretely (KeyTime-0 storyboards, :202-357).
        // Sampled cadence-exact on a manual clock: T=0 old brush, T=0.5 mid, T=1.0 (83ms) exactly AccentFillColorDefault.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1tb", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1ToggleButtonProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();

            var tb = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleButton);
            var tr = host.Scene.AbsoluteRect(tb);
            var off = FillAt(device, tr).Fill;
            var fgOff = GlyphColor(device, strings, "w1-tb");
            bool offState = ColorClose(off, Tok.FillControlDefault, 0.004f) && ColorClose(fgOff, Tok.TextPrimary, 0.004f);

            root.On!.Value = true;
            host.RunFrame();                                     // commit frame, dt 0 → T=0: fill still the unchecked brush
            var atFlip = FillAt(device, tr).Fill;
            var fgFlip = GlyphColor(device, strings, "w1-tb");
            bool t0 = ColorClose(atFlip, Tok.FillControlDefault, 0.004f) && ColorClose(fgFlip, Tok.TextOnAccentPrimary, 0.004f);

            clock.Advance(41.5f); host.RunFrame();               // T = 0.5: mid cross-fade (neither endpoint)
            var mid = FillAt(device, tr).Fill;
            bool midFade = mid.A > Tok.FillControlDefault.A + 0.15f && mid.A < Tok.AccentDefault.A - 0.15f;

            clock.Advance(41.5f); host.RunFrame();               // T = 1.0 at exactly 83ms: settled, anim row dropped
            var done = FillAt(device, tr).Fill;
            bool settled = ColorClose(done, Tok.AccentDefault, 0.004f) && !host.Scene.HasBrushAnims;

            ClickNode(host, window, tb);                         // the pointer path toggles back
            bool clicked = !root.On!.Peek();

            Check("w1controls.4 ToggleButton checked flip: 83ms BrushTransition on the fill (old at T0 → mid → exact accent at 83ms); foreground steps discretely; click toggles",
                offState && t0 && midFade && settled && clicked,
                $"off={offState} t0={t0} midA={mid.A:0.00} settled={settled} clicked={clicked}");
        }

        // w1controls.5 — ToggleSwitch geometry + brush ladder (ToggleSwitch_themeresources.xaml, "the template"):
        // 40×20 track (:507), 20×20 knob host (:509), knob 12 rest (:510/515 + Normal :231-242) / 14 hover (:268-279) /
        // 17×14 pressed pinned 3px off the near edge (:311-322 + :284-287); tap toggles and the knob travels +20 (:445).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1ts", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new ToggleSwitchProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var control = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            var track = Child(host.Scene, control, 0);
            var knobHost = Child(host.Scene, track, 1);
            var knob = Child(host.Scene, knobHost, 0);
            var trk = host.Scene.AbsoluteRect(track);
            var kr = host.Scene.AbsoluteRect(knob);
            var khr = host.Scene.AbsoluteRect(knobHost);
            bool geom = Near(trk.W, 40f) && Near(trk.H, 20f) && Near(khr.W, 20f) && Near(khr.H, 20f)
                && Near(kr.W, 12f) && Near(kr.H, 12f) && Near(kr.X - khr.X, 4f) && Near(khr.X - trk.X, 0f);

            // Off ARGB: fill = ControlAltFillColorSecondary (template:15/135), stroke = ControlStrongStrokeColorDefault
            // (:19/139), knob = TextFillColorSecondary (:31-33/151-153). Stroke cmds carry the CENTERLINE rect: the 1px
            // border of the 40×20 track records as (0.5,0.5,39,19).
            var trackFill = FillAt(device, trk).Fill;
            var knobFill = FillAt(device, kr).Fill;
            var trackStroke = StrokeOfWidth(device, 1f, 39f);
            bool offColors = ColorClose(trackFill, Tok.FillControlAltSecondary, 0.004f)
                && ColorClose(knobFill, Tok.TextSecondary, 0.004f)
                && ColorClose(trackStroke.Color, Tok.StrokeControlStrongDefault, 0.004f);

            var c = CenterOf(host.Scene, control);
            window.QueueInput(new InputEvent(InputKind.PointerMove, c, 0, 0));
            host.RunFrame();
            var kHover = host.Scene.AbsoluteRect(knob);
            bool hover14 = Near(kHover.W, 14f) && Near(kHover.H, 14f);

            // The knob's size/anchor change rides its FLIP transition and AbsoluteRect includes the presented
            // transform — hold the press a few frames so the 83ms grow/pin settles before sampling the geometry.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            for (int i = 0; i < 12; i++) host.RunFrame();
            var kPress = host.Scene.AbsoluteRect(knob);
            var khPress = host.Scene.AbsoluteRect(knobHost);
            bool press17 = Near(kPress.W, 17f) && Near(kPress.H, 14f) && Near(kPress.X - khPress.X, 3f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            host.RunFrame();
            bool toggled = root.On;
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(310f, 150f), 0, 0));
            for (int i = 0; i < 30; i++) host.RunFrame();   // settle: 167ms travel + 83ms brush fades + hover decay

            var kOn = host.Scene.AbsoluteRect(knob);
            bool traveled = Near(kOn.W, 12f) && Near(kOn.H, 12f) && Near(kOn.X - trk.X, 24f);   // +20 travel, re-centred at 4
            // On ARGB: track = AccentFillColorDefault (template:23/143), knob = TextOnAccentFillColorPrimary (:35-37/155-157);
            // ToggleSwitchOnStrokeThickness = 0 (template:5/125) — the 40-wide stroke disappears.
            var onTrack = FillAt(device, host.Scene.AbsoluteRect(track)).Fill;
            var onKnob = FillAt(device, kOn).Fill;
            bool onColors = ColorClose(onTrack, Tok.AccentDefault, 0.004f) && ColorClose(onKnob, Tok.TextOnAccentPrimary, 0.004f);
            bool strokeGone = StrokeOfWidth(device, 1f, 39f).StrokeWidth == 0f;

            Check("w1controls.5 ToggleSwitch geometry + ARGB: 40×20 track, knob 12 rest / 14 hover / 17×14 pressed (3px pin), tap travels +20; off/on brush ladder exact",
                geom && offColors && hover14 && press17 && toggled && traveled && onColors && strokeGone,
                $"geom={geom} off={offColors} hover={kHover.W:0.#}x{kHover.H:0.#} press={kPress.W:0.#}x{kPress.H:0.#}@+{kPress.X - khPress.X:0.#} on={onColors} strokeGone={strokeGone} knobX=+{kOn.X - trk.X:0.#}");
        }

        // w1controls.6 — ToggleSwitch keyboard: Space activates on KEY-UP (engine focused-clickable contract;
        // HandlesKey = Space/GamepadA, ToggleSwitch_Partial.cpp:1002-1007), the knob travel TWEENS over the 167ms
        // ControlFast reposition (template:418-439 → Motion.ControlFast), and arrows toggle directionally
        // (ToggleSwitchKeyProcess.h:52-71).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1tsk", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new ToggleSwitchProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var control = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            var track = Child(host.Scene, control, 0);
            var knobHost = Child(host.Scene, track, 1);            // the 20×20 positioning host OWNS the travel FLIP
            var knob = Child(host.Scene, knobHost, 0);

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Space));
            host.RunFrame();
            bool armedNotToggled = !root.On && (host.Scene.Flags(control) & NodeFlags.Pressed) != 0;
            window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Space));
            host.RunFrame();                                       // commit frame (dt 0): FLIP seeded at the full inverse
            bool toggledOnUp = root.On;
            float dx0 = host.Scene.Paint(knobHost).LocalTransform.Dx;  // ≈ −20: presented still at the off spot

            clock.Advance(50f); host.RunFrame();                   // mid-travel of the 167ms tween
            float dxMid = host.Scene.Paint(knobHost).LocalTransform.Dx;
            clock.Advance(500f); host.RunFrame(); host.RunFrame();
            float dxEnd = host.Scene.Paint(knobHost).LocalTransform.Dx;
            var trk = host.Scene.AbsoluteRect(track);
            var kOn = host.Scene.AbsoluteRect(knob);
            bool seeded = Near(dx0, -20f, 1.5f);
            bool tweened = dxMid > -19.5f && dxMid < -0.5f;
            bool settledTravel = MathF.Abs(dxEnd) < 0.5f && Near(kOn.X - trk.X, 24f);

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
            host.RunFrame(); bool leftOff = !root.On;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame(); bool rightOn = root.On;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame(); bool rightNoop = root.On;             // already on → no toggle (Key only handled when it toggles)

            Check("w1controls.6 ToggleSwitch keys: Space toggles on KEY-UP (armed pressed until then); knob travel tweens (seed −20 → mid → settle +20); arrows toggle directionally",
                armedNotToggled && toggledOnUp && seeded && tweened && settledTravel && leftOff && rightOn && rightNoop,
                $"armed={armedNotToggled} up={toggledOnUp} dx {dx0:0.#}→{dxMid:0.#}→{dxEnd:0.##} L={leftOff} R={rightOn} Rnoop={rightNoop}");
        }

        // w1controls.7 — ToggleSwitch drag-to-toggle (ToggleSwitch_Partial.cpp): the 4px drag box arms the knob drag
        // (:829-836 over the SM_CXDRAG threshold), the knob FOLLOWS the pointer clamped to the travel (:455-457,
        // :579-589), release toggles iff the knob crossed HALF the travel (MoveCompleted :591-619), and a pointer that
        // leaves mid-press cancels — the captured outside release must NOT toggle (capture-lost cleanup :728-746).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1tsd", new Size2(480, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new ToggleSwitchProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var control = FindRole(host.Scene, host.Scene.Root, AutomationRole.ToggleSwitch);
            var track = Child(host.Scene, control, 0);
            var knob = Child(host.Scene, Child(host.Scene, track, 1), 0);
            var trk = host.Scene.AbsoluteRect(track);
            var c = CenterOf(host.Scene, control);

            // 6px: past the 4px drag box but under half the 20px travel → release does NOT toggle.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 6f, c.Y), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 6f, c.Y), 0, 0)); host.RunFrame();
            bool smallDragStaysOff = !root.On;

            // 12px: crosses half the travel; mid-drag the knob follows the pointer (dragX 12 + the 3px pressed pin).
            // AbsoluteRect includes the presented FLIP transform — hold a few frames so the drag's snap-follow
            // (1ms tween) and the press-grow settle onto the model spot before sampling.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X + 12f, c.Y), 0, 0));
            for (int i = 0; i < 10; i++) host.RunFrame();
            var kDrag = host.Scene.AbsoluteRect(knob);
            bool knobFollows = Near(kDrag.X - trk.X, 15f) && Near(kDrag.W, 17f) && Near(kDrag.H, 14f);
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(c.X + 12f, c.Y), 0, 0)); host.RunFrame();
            bool bigDragTogglesOn = root.On;

            // Drag back toward off, then EXIT the control and release outside: cancelled, stays ON.
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c.X - 12f, c.Y), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(450f, 300f), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(450f, 300f), 0, 0)); host.RunFrame();
            bool releaseOutsideCancels = root.On;

            // The cancel doesn't wedge the gesture: a later plain tap still toggles.
            ClickNode(host, window, control);
            bool tapAfterCancel = !root.On;

            Check("w1controls.7 ToggleSwitch drag: 4px drag box, half-travel rule (6px no / 12px yes), knob follows the pointer, exit + release-outside cancels without toggling",
                smallDragStaysOff && knobFollows && bigDragTogglesOn && releaseOutsideCancels && tapAfterCancel,
                $"small={smallDragStaysOff} follows={knobFollows} big={bigDragTogglesOn} cancel={releaseOutsideCancels} tap={tapAfterCancel}");
        }

        // w1controls.8 — RadioButtons container (controls\dev\RadioButtons): column-major MaxColumns grid
        // (ColumnMajorUniformToLargestGridLayout.cpp:48-163; ColumnSpacing 7 / RowSpacing 8 / header gap 8,
        // RadioButtons_themeresources.xaml:18-20), ONE roving tab stop (RadioButtons.xaml:5-6 + OnGettingFocus :80-97),
        // arrows rove with SELECTION FOLLOWS FOCUS unless Ctrl (:100-107, :135-183), edges swallow (:216-242).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1rb", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1RadioButtonsProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var radios = Roles(host.Scene, AutomationRole.RadioButton);
            var a = host.Scene.AbsoluteRect(radios[0]);
            var b = host.Scene.AbsoluteRect(radios[1]);
            var d = host.Scene.AbsoluteRect(radios[3]);
            bool grid = radios.Count == 5
                && Near(d.Y, a.Y) && Near(d.X, a.X + a.W + 7f)
                && Near(b.Y, a.Y + a.H + 8f) && Near(b.X, a.X);
            var header = FindTextNode(host.Scene, strings, host.Scene.Root, "w1-group");
            var hrr = host.Scene.AbsoluteRect(header);
            bool headerRow = !header.IsNull && Near(a.Y - (hrr.Y + hrr.H), 8f, 1f)
                && ColorClose(GlyphColor(device, strings, "w1-group"), Tok.TextPrimary, 0.004f);   // RadioButtonsHeaderForeground = TextFillColorPrimary (themeresources:4-10)

            int FocusableIdx(out int count)
            {
                count = 0; int idx = -1;
                for (int i = 0; i < radios.Count; i++)
                    if ((host.Scene.Flags(radios[i]) & NodeFlags.Focusable) != 0) { count++; idx = i; }
                return idx;
            }
            bool roving0 = FocusableIdx(out int fc0) == 0 && fc0 == 1;

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            bool tabLands = FocusedNode(host.Scene, host.Scene.Root) == radios[0];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down));
            host.RunFrame();
            bool downSelects = root.Selected == 1 && FocusedNode(host.Scene, host.Scene.Root) == radios[1];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down, Mods: KeyModifiers.Ctrl));
            host.RunFrame();
            bool ctrlMovesOnly = root.Selected == 1 && FocusedNode(host.Scene, host.Scene.Root) == radios[2];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame();
            bool rightColumn = root.Selected == 4 && FocusedNode(host.Scene, host.Scene.Root) == radios[4];   // (col0,row2) → col1 clamped to row1 = E

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down));
            host.RunFrame();
            bool edgeSwallow = root.Selected == 4 && FocusedNode(host.Scene, host.Scene.Root) == radios[4];

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
            host.RunFrame();
            bool leftBack = root.Selected == 1 && FocusableIdx(out int fc1) == 1 && fc1 == 1;   // the tab stop follows the selection

            Check("w1controls.8 RadioButtons: column-major MaxColumns grid (7/8 spacing, header gap 8, TextPrimary ARGB), ONE roving tab stop, arrows rove + selection-follows-focus, Ctrl exempts, edges swallow",
                grid && headerRow && roving0 && tabLands && downSelects && ctrlMovesOnly && rightColumn && edgeSwallow && leftBack,
                $"grid={grid} hdr={headerRow} roving={roving0} tab={tabLands} down={downSelects} ctrl={ctrlMovesOnly} right={rightColumn} edge={edgeSwallow} left={leftBack}");
        }

        // w1controls.9 — Slider keyboard matrix (KeyPress::Slider::KeyDown, SliderKeyProcess.h:28-71 + the PageUp/Down
        // parity rows on Slider::Step, Slider_Partial.cpp:1713-1819): steps snap to the closest step multiple, and the
        // AUTO step sizes derive range/100 and range/10 (WinUI's absolute defaults 1/10 on the 0–100 range,
        // Slider_Partial.h:13-15). Header per Slider_themeresources.xaml:396 + SliderTopHeaderMargin 0,0,0,4 (:161).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1sl", new Size2(320, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1SliderKeysProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);
            var hdr = FindTextNode(host.Scene, strings, host.Scene.Root, "w1-vol");
            var hr2 = host.Scene.AbsoluteRect(hdr);
            bool headered = !hdr.IsNull && Near(tr.Y - (hr2.Y + hr2.H), 4f, 1f)
                && ColorClose(GlyphColor(device, strings, "w1-vol"), Tok.TextPrimary, 0.004f);   // SliderHeaderForeground = TextFillColorPrimary (:28)

            var p = new Point2(tr.X + 100f, tr.Y + tr.H / 2f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
            host.RunFrame();
            bool clicked = Near(root.Val, 100f, 0.01f);

            float Key(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); host.RunFrame(); return root.Val; }
            float right = Key(Keys.Right);     // +SmallChange(auto 2) → 102
            float pgUp = Key(Keys.PageUp);     // +LargeChange(auto 20), snapped to the 20-grid → 120 (not 122)
            float pgDn = Key(Keys.PageDown);   // 100
            float left = Key(Keys.Left);       // 98
            float down = Key(Keys.Down);       // 96 (Down = backward, SliderKeyProcess.h:52-59)
            float up = Key(Keys.Up);           // 98 (Up = forward, :44-51)
            float home = Key(Keys.Home);       // Minimum (:60-65)
            float end = Key(Keys.End);         // Maximum (:66-71)

            Check("w1controls.9 Slider keyboard matrix + AUTO Small/Large (range/100, range/10): ±2 arrows, PageUp 102→120 (closest-multiple snap), Home/End; header 4px above (ARGB)",
                headered && clicked && Near(right, 102f, 0.01f) && Near(pgUp, 120f, 0.01f) && Near(pgDn, 100f, 0.01f)
                && Near(left, 98f, 0.01f) && Near(down, 96f, 0.01f) && Near(up, 98f, 0.01f) && Near(home, 0f, 0.01f) && Near(end, 200f, 0.01f),
                $"hdr={headered} click={clicked} R={right:0.#} PgUp={pgUp:0.#} PgDn={pgDn:0.#} L={left:0.#} D={down:0.#} U={up:0.#} Home={home:0.#} End={end:0.#}");
        }

        // w1controls.10 — Slider visuals: inline tick rects (TickPlacement default Inline, visibility mapping
        // Slider_Partial.cpp:2248-2303; SliderInlineTickBarFill = ControlFillColorInputActive,
        // Slider_themeresources.xaml:32), the thumb value tooltip shows on PRESS and scrubs live
        // (UpdateThumbToolTipVisibility, Slider_Partial.cpp:478-543; default converter :1859-1936), hides on release
        // (PerformPointerUpAction :645-659); FocusVisualMargin −7,0,−7,0 (:184) widens the ring horizontally only.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1slt", new Size2(360, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new W1SliderTipProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var track = FindRole(host.Scene, host.Scene.Root, AutomationRole.Slider);
            var tr = host.Scene.AbsoluteRect(track);

            var tickXs = new List<float>();
            bool tickColor = true;
            foreach (var r in device.LastRects)
                if (Near(r.Rect.W, 1f, 0.01f) && Near(r.Rect.H, 4f, 0.01f))
                {
                    tickXs.Add(r.Transform.Dx);
                    tickColor &= ColorClose(r.Fill, Tok.FillControlInputActive, 0.004f);
                }
            tickXs.Sort();
            bool ticks = tickXs.Count == 5 && tickColor;
            for (int i = 1; i < tickXs.Count; i++) ticks &= Near(tickXs[i] - tickXs[i - 1], 50f, 1f);

            var p100 = new Point2(tr.X + 100f, tr.Y + tr.H / 2f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, p100, 0, 0));
            host.RunFrame(); host.RunFrame();                    // open → the overlay content mounts + places next frame
            bool tipShows = HasGlyph(device, strings, "100") && Near(root.Val, 100f, 0.01f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(tr.X + 150f, tr.Y + tr.H / 2f), 0, 0));
            host.RunFrame();
            bool tipScrubs = HasGlyph(device, strings, "150") && Near(root.Val, 150f, 0.01f);

            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(tr.X + 150f, tr.Y + tr.H / 2f), 0, 0));
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool tipHides = !HasGlyph(device, strings, "150");

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            DrawRoundRectStrokeCmd prim = default;
            foreach (var s in device.LastStrokes) if (Near(s.StrokeWidth, 2f)) prim = s;
            bool ring = Near(prim.Rect.X, -6f) && Near(prim.Rect.Y, 1f) && Near(prim.Rect.W, tr.W + 12f) && Near(prim.Rect.H, tr.H - 2f);

            Check("w1controls.10 Slider visuals: 5 inline tick rects 50px apart (InputActive ARGB); press shows the thumb tooltip '100', drag scrubs to '150', release hides; focus ring −7,0,−7,0",
                ticks && tipShows && tipScrubs && tipHides && ring,
                $"ticks={tickXs.Count} color={tickColor} show={tipShows} scrub={tipScrubs} hide={tipHides} ring=({prim.Rect.X:0.#},{prim.Rect.Y:0.#} {prim.Rect.W:0.#}x{prim.Rect.H:0.#})");
        }

        // w1controls.11 — RatingControl: the per-star focal hover SCALE (the composition expression,
        // RatingControl.cpp:350-371, re-based ×2 into the 16px-native strip → focal star 2×c_mouseOverScale = 1.6,
        // far stars floor at 2×0.5 = 1.0), the pointer-over-UNSET preview brush (RatingControlPointerOverUnselected-
        // Foreground = ControlAltFillColorTertiary), and the drag-off-the-left-side clear (capture keeps the sweep
        // alive off-strip, cpp:799-805/856-863; release commits swept 0 → the cleared sentinel, :888-906).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w1rt", new Size2(320, 120), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var root = new RatingProbe { Initial = RatingControl.NoValueSet };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            var rating = FindRole(host.Scene, host.Scene.Root, AutomationRole.Rating);
            var rr = host.Scene.AbsoluteRect(rating);
            var starRow = Child(host.Scene, rating, 0);
            var cell0 = Child(host.Scene, starRow, 0);
            var cell4 = Child(host.Scene, starRow, 4);

            var hov = new Point2(rr.X + 8f, rr.Y + rr.H / 2f);   // star 1 centre (StarCenter(0) = 8)
            window.QueueInput(new InputEvent(InputKind.PointerMove, hov, 0, 0));
            host.RunFrame(); host.RunFrame();
            float s0 = host.Scene.Paint(cell0).LocalTransform.M11;
            float s4 = host.Scene.Paint(cell4).LocalTransform.M11;
            bool focal = Near(s0, 1.6f, 0.05f) && Near(s4, 1.0f, 0.02f);

            const string filled = "";
            int hovFilled = CountGlyph(device, strings, filled);
            bool hovColor = ColorClose(GlyphColor(device, strings, filled), Tok.FillControlAltTertiary, 0.004f);
            bool uncommitted = root.Val!.Peek() <= RatingControl.NoValueSet;

            var p3 = new Point2(rr.X + 56f, rr.Y + rr.H / 2f);   // ceil(56/112·5) = 3
            window.QueueInput(new InputEvent(InputKind.PointerDown, p3, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p3, 0, 0));
            host.RunFrame();
            float committed = root.Val!.Peek();

            var p1 = new Point2(rr.X + 20f, rr.Y + rr.H / 2f);   // press on star 1...
            window.QueueInput(new InputEvent(InputKind.PointerDown, p1, 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(rr.X - 80f, rr.Y + rr.H / 2f), 0, 0)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(rr.X - 80f, rr.Y + rr.H / 2f), 0, 0)); host.RunFrame();
            float cleared = root.Val!.Peek();                     // ...drag past the LEFT edge and release → cleared
            host.RunFrame();
            float s0After = host.Scene.Paint(cell0).LocalTransform.M11;   // focal back at the −100 sentinel → 1.0

            Check("w1controls.11 RatingControl: focal hover scale 1.6 focal / 1.0 far (mouse 0.8 expression), pointer-over-unset preview ARGB, drag-off-left clears to −1, focal resets on release",
                focal && hovFilled == 1 && hovColor && uncommitted && Near(committed, 3f) && Near(cleared, -1f) && Near(s0After, 1f, 0.02f),
                $"s0={s0:0.00} s4={s4:0.00} filled={hovFilled} color={hovColor} committed={committed} cleared={cleared} reset={s0After:0.00}");
        }
    }

    static void D2PasswordRevealFocusChecks(StringTable strings)
    {
        static NodeHandle TextVisual(SceneStore s, NodeHandle n)
        {
            if (n.IsNull) return NodeHandle.Null;
            if (s.Paint(n).VisualKind == VisualKind.Text) return n;
            for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
            {
                var r = TextVisual(s, c);
                if (!r.IsNull) return r;
            }
            return NodeHandle.Null;
        }

        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d2-pw", new Size2(420, 280), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        NodeHandle other = default;
        var root = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1, Gap = 16, Padding = Edges4.All(12),
                Children =
                [
                    PasswordBox.Create("Password", 280f),
                    // A focusable blur target (OnClick ⇒ auto-focusable). Deliberately NOT AutomationRole.Button so
                    // Roles(Button) below counts exactly the reveal eye.
                    new BoxEl { Width = 120, Height = 32, OnClick = () => { }, OnRealized = h => other = h },
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        var scene = host.Scene;
        var field = FindRole(scene, scene.Root, AutomationRole.Text);
        var tn = TextVisual(scene, field);

        // Focus the EMPTY box and type — the empty→non-empty transition arms + mounts the eye
        // (CPasswordBox::OnContentChanged, PasswordBox.cpp:366–377).
        ClickNode(host, window, field);
        host.RunFrame();
        foreach (char c in "ab") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
        host.RunFrame();
        host.RunFrame();
        var btns = Roles(scene, AutomationRole.Button);
        bool mounted = btns.Count == 1 && HasGlyph(device, strings, Icons.RevealPassword);

        // cp2.a — CLICK the eye: it is not a focus target (RevealButton IsTabStop=False,
        // PasswordBox_themeresources.xaml:193), so the field never blurs, the arm flag survives, and the eye STAYS
        // mounted; the click's release re-masks (the press peeked).
        if (mounted) ClickNode(host, window, btns[0]);
        host.RunFrame();
        bool stillMounted = Roles(scene, AutomationRole.Button).Count == 1 && HasGlyph(device, strings, Icons.RevealPassword);
        bool maskedAfterClick = strings.Resolve(scene.Paint(tn).Text) == "●●";
        bool fieldFocused = (scene.Flags(field) & NodeFlags.Focused) != 0;
        Check("cp2.a — clicking the reveal eye keeps it mounted + masked, field still focused",
            mounted && stillMounted && maskedAfterClick && fieldFocused,
            $"mounted={mounted} still={stillMounted} masked={maskedAfterClick} focus={fieldFocused}");

        // cp2.b — press-and-HOLD peek: pointer-down on the eye renders the raw password mid-press; pointer-up
        // re-masks (RevealPassword on the ToggleButton press, PasswordBox.cpp:260–308).
        var eyes = Roles(scene, AutomationRole.Button);
        bool peeked = false, remasked = false, focusHeld = false;
        if (eyes.Count == 1)
        {
            var bc = CenterOf(scene, eyes[0]);
            window.QueueInput(new InputEvent(InputKind.PointerDown, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_000));
            host.RunFrame();
            host.RunFrame();
            peeked = strings.Resolve(scene.Paint(tn).Text) == "ab";
            window.QueueInput(new InputEvent(InputKind.PointerUp, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 5_100));
            host.RunFrame();
            host.RunFrame();
            remasked = strings.Resolve(scene.Paint(tn).Text) == "●●";
            focusHeld = (scene.Flags(field) & NodeFlags.Focused) != 0;
        }
        Check("cp2.b — press-and-hold on the eye reveals the raw password mid-press; release re-masks (field still focused)",
            peeked && remasked && focusHeld, $"peeked={peeked} remasked={remasked} focus={focusHeld}");

        // cp2.c — blur to ANOTHER control, refocus the populated box: the eye must NOT return (OnGotFocus arm-clear,
        // PasswordBox.cpp:572–581); typing into the populated box keeps it hidden (the arm is the empty→non-empty
        // transition ONLY, PasswordBox.cpp:366–377; cleared while empty, :430–434); emptying + retyping re-arms.
        ClickNode(host, window, other);
        host.RunFrame();
        bool blurUnmounts = Roles(scene, AutomationRole.Button).Count == 0 && (scene.Flags(field) & NodeFlags.Focused) == 0;
        ClickNode(host, window, field);
        host.RunFrame();
        bool noEyeOnRefocus = Roles(scene, AutomationRole.Button).Count == 0;
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'c'));
        host.RunFrame();
        host.RunFrame();
        bool typingPopulatedHidden = Roles(scene, AutomationRole.Button).Count == 0;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, 0f, KeyModifiers.Ctrl));
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back));
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
        host.RunFrame();
        host.RunFrame();
        bool rearmed = Roles(scene, AutomationRole.Button).Count == 1;
        Check("cp2.c — blur→refocus shows no eye (OnGotFocus arm-clear); typing populated stays hidden; empty→retype re-arms",
            blurUnmounts && noEyeOnRefocus && typingPopulatedHidden && rearmed,
            $"blur={blurUnmounts} refocus={noEyeOnRefocus} typing={typingPopulatedHidden} rearmed={rearmed}");
    }

    // cp-blur — TextBox.CommitOnLostFocus. WinUI's TextBox has no blur seam at all, so a rename field built on Enter
    // alone silently DROPS a typed value the moment focus moves — the user's edit is gone with no error and nothing to
    // undo. The opt-in must land exactly one commit per real edit: an Escape that reverts-then-blurs is a cancel and
    // must not publish the reverted value as a choice, and an Enter that already committed must not be re-published by
    // the blur that follows it.
    static void TextBoxBlurCommitChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tb-blur", new Size2(460, 260), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);

        var optIn = new FluentGpu.Signals.Signal<string>("");
        var optOut = new FluentGpu.Signals.Signal<string>("");
        var commits = new List<string>();
        var optOutCommits = new List<string>();
        int cancels = 0;
        NodeHandle other = default;

        var root = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1, Gap = 12, Padding = Edges4.All(12),
                Children =
                [
                    TextBox.Create(optIn, null, new TextBox.TextBoxOptions
                    {
                        Width = 200f, CommitOnLostFocus = true,
                        OnCommit = v => commits.Add(v), OnCancel = () => cancels++,
                    }),
                    // The control group: identical field WITHOUT the opt-in must stay byte-identical (Enter only).
                    TextBox.Create(optOut, null, new TextBox.TextBoxOptions
                    {
                        Width = 200f, OnCommit = v => optOutCommits.Add(v),
                    }),
                    new BoxEl { Width = 120, Height = 32, OnClick = () => { }, OnRealized = h => other = h },
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        var scene = host.Scene;
        var fields = Roles(scene, AutomationRole.Text);

        void TypeFocused(string text)
        {
            foreach (char ch in text) window.QueueInput(new InputEvent(InputKind.Char, default, 0, ch));
            host.RunFrame();
            host.RunFrame();
        }
        void Type(NodeHandle field, string text)
        {
            ClickNode(host, window, field);
            host.RunFrame();
            TypeFocused(text);
        }
        void Blur() { ClickNode(host, window, other); host.RunFrame(); }
        void Key(int code) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, code)); host.RunFrame(); host.RunFrame(); }

        bool wired = fields.Count == 2 && !other.IsNull;
        if (!wired)
        {
            Check("cp-blur TextBox.CommitOnLostFocus probe wired", false, $"fields={fields.Count} other={!other.IsNull}");
            return;
        }

        // 1 — type, then click away: the edit is COMMITTED (this is the whole feature).
        Type(fields[0], "ab");
        Blur();
        bool committedOnBlur = commits.Count == 1 && commits[0] == optIn.Peek();

        // 2 — refocus and blur with NO edit: nothing new. The baseline is what the last commit published, not the
        //     mere fact that the field was focused.
        ClickNode(host, window, fields[0]); host.RunFrame();
        Blur();
        bool untouchedBlurSilent = commits.Count == 1;

        // 3 — Enter commits and KEEPS focus (WinUI); the blur that eventually follows must NOT re-publish it.
        Type(fields[0], "c");
        Key(Keys.Enter);
        int afterEnter = commits.Count;
        Blur();
        bool noDoubleCommitAfterEnter = afterEnter == 2 && commits.Count == 2 && commits[1] == optIn.Peek();

        // 4 — Escape reverts to the FOCUS-TIME snapshot and blurs. Staged (focus → type → Enter → type again → Escape)
        //     so the revert target genuinely DIFFERS from what was last published: without the cancel guard the blur
        //     would then publish the reverted text as if the user had chosen it, turning a cancel into an edit.
        Type(fields[0], "z");
        Key(Keys.Enter);                 // publishes the "z" edit and keeps focus
        string published = commits.Count == 3 ? commits[2] : "";
        TypeFocused("y");                // still focused — a further edit the user is about to abandon
        Key(Keys.Escape);                // revert to the focus-time text (≠ published), OnCancel, blur
        Blur();                          // …and the click that follows must not commit it either
        bool cancelDoesNotCommit = cancels == 1 && commits.Count == 3
                                   && published.Length > 0 && optIn.Peek() != published;

        // 5 — …and the cancel flag does not LATCH: the next real edit still commits on blur.
        Type(fields[0], "q");
        Blur();
        bool cancelDoesNotLatch = commits.Count == 4 && commits[3] == optIn.Peek();

        // 6 — the opt-out field is untouched: blur drops the edit exactly as before (Enter is still its only commit).
        Type(fields[1], "x");
        Blur();
        bool optOutUnchanged = optOutCommits.Count == 0;

        Check("cp-blur TextBox.CommitOnLostFocus commits a typed edit on blur, stays silent for an untouched blur, never double-commits after Enter, never commits Escape's revert (and does not latch), and leaves an opt-out field byte-identical",
            committedOnBlur && untouchedBlurSilent && noDoubleCommitAfterEnter && cancelDoesNotCommit
            && cancelDoesNotLatch && optOutUnchanged,
            $"blur={committedOnBlur} untouched={untouchedBlurSilent} enter={noDoubleCommitAfterEnter} " +
            $"cancel={cancelDoesNotCommit}(n={cancels}) latch={cancelDoesNotLatch} optOut={optOutUnchanged}(n={optOutCommits.Count}) " +
            $"commits=[{string.Join(",", commits)}]");
    }

    static void ControlKitIdiomChecks(StringTable strings)
    {
        // gate.ctl.idiom.no-public-build — reflection scan: no public static Build/BuildBody member on any Controls type.
        {
            var asm = typeof(FluentGpu.Controls.Button).Assembly;
            var offenders = new System.Collections.Generic.List<string>();
            foreach (var t in asm.GetExportedTypes())
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                    if (m.Name == "Build" || m.Name == "BuildBody")
                        offenders.Add(t.Name + "." + m.Name);
            Check("gate.ctl.idiom.no-public-build no public static Build/BuildBody on any Controls type",
                offenders.Count == 0, offenders.Count == 0 ? "clean" : string.Join(", ", offenders));
        }

        // gate.ctl.idiom.factories-exist — NavigationView/TitleBar/OverlayHost/MenuFlyout expose a public static Create,
        // and NavigationView.Create mounts + navigates through the options record.
        {
            static bool HasCreate(Type t)
            {
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (m.Name == "Create") return true;
                return false;
            }
            bool exist = HasCreate(typeof(NavigationView)) && HasCreate(typeof(TitleBar))
                       && HasCreate(typeof(OverlayHost)) && HasCreate(typeof(MenuFlyout));

            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("nav-create", new Size2(1200, 700), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            string selected = "";
            var nav = NavigationView.Create(new NavigationViewOptions
            {
                Initial = "home",
                Items = new[] { new NavItem("home", Icons.Home, "Home"), new NavItem("files", Icons.Folder, "Files") },
                Content = key => new TextEl("page:" + key) { Size = 16f, Color = Tok.TextPrimary },
                OnSelect = k => selected = k,
            });
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe { Build = () => nav });
            host.RunFrame();
            var items = Roles(host.Scene, AutomationRole.NavigationItem);
            bool mounted = items.Count >= 2;
            if (mounted) ClickNode(host, window, items[1]);   // navigate to "files"
            bool navigated = selected == "files";
            Check("gate.ctl.idiom.factories-exist NavigationView/TitleBar/OverlayHost/MenuFlyout expose Create; NavigationView.Create mounts + navigates",
                exist && mounted && navigated, $"exist={exist} mounted={mounted} items={items.Count} selected={selected}");
        }

        // gate.ctl.bind.scrollbar — ScrollBar.Create: a track-click page writes the FloatSignal + fires onChange; a
        // programmatic write does NOT echo onChange and never re-renders the owner (compositor-instant thumb).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-scrollbar", new Size2(320, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var pos = new FloatSignal(0f);
            int changes = 0; float last = -1f; int probeRenders = 0;
            // ScrollBar conformance (rename-only, like ComboBox/ColorPicker): the caller's onChange writes the
            // position signal back (the control reads it compositor-instant via the thumb Transform bind).
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => { probeRenders++; return new BoxEl { Padding = Edges4.All(20f),
                    Children = [ScrollBar.Create(0.25f, pos, p => { changes++; last = p; pos.Value = p; }, length: 240f)] }; },
            });
            host.RunFrame();
            int rendersAtMount = probeRenders;
            var bar = FindRole(host.Scene, host.Scene.Root, AutomationRole.ScrollBar);
            var barRect = host.Scene.AbsoluteRect(bar);
            var pt = new Point2(barRect.X + barRect.W * 0.5f, barRect.Y + barRect.H * 0.7f);   // track strip, below the thumb
            window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0));
            host.RunFrame();
            bool wrote = changes >= 1 && pos.Value > 0f && last == pos.Value;
            int changesBefore = changes;
            pos.Value = 0.5f;                       // programmatic write
            host.RunFrame();
            bool noEcho = changes == changesBefore;
            bool decoupled = probeRenders == rendersAtMount;   // the signal write never re-rendered the owner
            Check("gate.ctl.bind.scrollbar ScrollBar.Create: interaction writes the position signal + fires onChange; programmatic write no echo (owner not re-rendered)",
                wrote && noEcho && decoupled, $"wrote={wrote} changes={changes} noEcho={noEcho} pos={pos.Value:0.00} ownerRenders={probeRenders}(mount {rendersAtMount})");
        }

        // gate.ctl.bind.numberbox-options — NumberBox.Create(value, onChange, NumberBoxOptions): the options record is
        // threaded (an inline spin steps the value), a spin click fires onChange once, a programmatic write no echo.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-numberbox", new Size2(360, 220), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var sig = new Signal<double>(5);
            int changes = 0;
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl { Padding = Edges4.All(12f), Children =
                [
                    NumberBox.Create(value: sig, onChange: _ => changes++, options: new NumberBox.NumberBoxOptions
                    {
                        Minimum = 0, Maximum = 10, SmallChange = 1,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    }),
                ] },
            });
            host.RunFrame();
            bool mountQuiet = changes == 0;                     // the mount-seed must NOT fire onChange
            var buttons = Roles(host.Scene, AutomationRole.Button);
            if (buttons.Count >= 1) ClickNode(host, window, buttons[0]);   // an inline spin (±SmallChange)
            bool stepped = changes == 1 && System.Math.Abs(System.Math.Abs(sig.Value - 5.0) - 1.0) < 0.01;
            int changesBefore = changes;
            sig.Value = 8;                                      // programmatic write
            host.RunFrame();
            bool noEcho = changes == changesBefore;
            Check("gate.ctl.bind.numberbox-options NumberBox.Create(options): a spin step writes the value signal + fires onChange once; mount + programmatic write no echo",
                mountQuiet && stepped && noEcho, $"mountQuiet={mountQuiet} stepped={stepped} val={sig.Value:0.##} changes={changes} noEcho={noEcho} buttons={buttons.Count}");
        }

        // gate.ctl.bind.splitview-pane — SplitView.Create(isPaneOpen: signal, onOpenChanged): light dismiss writes the
        // pane-open signal false + fires onOpenChanged once; a programmatic re-open does NOT echo onOpenChanged.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("bind-splitview", new Size2(600, 400), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var openSig = new Signal<bool>(true);
            int changes = 0;
            var pane = new BoxEl { Width = 200f, Padding = Edges4.All(12f), Children = [new TextEl("Pane") { Size = 14f, Color = Tok.TextPrimary }] };
            var content = new BoxEl { Grow = 1f, Padding = Edges4.All(16f), Children = [new TextEl("Content") { Size = 14f, Color = Tok.TextPrimary }] };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => SplitView.Create(pane, content, paneWidth: 200f, isPaneOpen: openSig, onOpenChanged: _ => changes++),
            });
            host.RunFrame();
            bool openMount = openSig.Value && changes == 0;
            // Light dismiss: click the content side (right of the left pane) → the light-dismiss layer closes the pane.
            var rootRect = host.Scene.AbsoluteRect(host.Scene.Root);
            var pt = new Point2(rootRect.Right - 24f, rootRect.Y + rootRect.H * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, pt, 0, 0));
            host.RunFrame();
            bool dismissed = !openSig.Value && changes == 1;
            int changesBefore = changes;
            openSig.Value = true;                               // programmatic re-open
            host.RunFrame();
            bool noEcho = changes == changesBefore;
            Check("gate.ctl.bind.splitview-pane SplitView.Create: light dismiss writes the isPaneOpen signal + fires onOpenChanged once; programmatic re-open no echo",
                openMount && dismissed && noEcho, $"openMount={openMount} dismissed={dismissed} open={openSig.Value} changes={changes} noEcho={noEcho}");
        }

        // gate.ctl.progress.null-indeterminate — ProgressBar/ProgressRing Create(null) = indeterminate (animating);
        // Create(signal) = determinate that tracks the signal (no sweep/spin anim tracks).
        {
            bool barDet, barInd, ringDet, ringInd;
            // ProgressBar determinate tracks the signal (bound indicator width; no sweep tracks).
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("progress-bar-det", new Size2(320, 120), 1f)); window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                NodeHandle fill = default;
                var pd = new TemplateParts();
                pd[ProgressBar.PartFill] = b => b with { OnRealized = h => fill = h };
                var sig = new FloatSignal(0.5f);
                using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl { Padding = Edges4.All(16f), Children = [ProgressBar.Create(value: sig, width: 200f, parts: pd)] },
                });
                host.RunFrame();
                float w50 = fill.IsNull ? -1f : host.Scene.AbsoluteRect(fill).W;
                bool noTracks = !fill.IsNull && !host.Animation.HasTracks(fill);
                sig.Value = 0.25f;
                host.RunFrame();
                float w25 = fill.IsNull ? -1f : host.Scene.AbsoluteRect(fill).W;
                barDet = noTracks && Near(w50, 100f, 3f) && Near(w25, 50f, 3f);
            }
            // ProgressBar Create(null) = indeterminate (the sweeping indicator animates).
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("progress-bar-ind", new Size2(320, 120), 1f)); window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                NodeHandle fill = default;
                var pi = new TemplateParts();
                pi[ProgressBar.PartFill] = b => b with { OnRealized = h => fill = h };
                using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
                {
                    Build = () => new BoxEl { Padding = Edges4.All(16f), Children = [ProgressBar.Create(null, width: 200f, parts: pi)] },
                });
                host.RunFrame(); host.RunFrame();
                barInd = !fill.IsNull && host.Animation.HasTracks(fill);
            }
            // ProgressRing determinate: no spin/trim anim tracks; re-renders when the value signal changes.
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("progress-ring-det", new Size2(200, 200), 1f)); window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                NodeHandle arc = default;
                var pd = new TemplateParts();
                pd[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
                var sig = new FloatSignal(0.5f);
                using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
                {
                    Build = () => ProgressRing.Create(value: sig, parts: pd),
                });
                host.RunFrame();
                bool noTracks = !arc.IsNull && !host.Animation.HasTracks(arc);
                sig.Value = 0.25f;
                var fs = host.RunFrame();
                ringDet = noTracks && fs.Rendered;   // the determinate ring observes the signal (granular re-render)
            }
            // ProgressRing Create(null) = indeterminate (the arc spins / trim breathes).
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("progress-ring-ind", new Size2(200, 200), 1f)); window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                NodeHandle arc = default;
                var pi = new TemplateParts();
                pi[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
                using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
                {
                    Build = () => ProgressRing.Create(null, parts: pi),
                });
                host.RunFrame(); host.RunFrame();
                ringInd = !arc.IsNull && host.Animation.HasTracks(arc);
            }
            Check("gate.ctl.progress.null-indeterminate ProgressBar/Ring Create(null)=indeterminate (animates); Create(signal)=determinate tracking the value",
                barDet && barInd && ringDet && ringInd,
                $"barDet={barDet} barInd={barInd} ringDet={ringDet} ringInd={ringInd}");
        }
    }

    static void ProgressIndeterminateLifecycleChecks(StringTable strings)
    {
        // ProgressRing: parent re-render updates isActive through context, preserving the component instance.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-ring-lifecycle", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var active = new Signal<bool>(true);
            NodeHandle arc = default;
            var parts = new TemplateParts();
            parts[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 160, Height = 120, Padding = Edges4.All(16),
                    Children = [ProgressRing.Indeterminate(isActive: active.Value, parts: parts)],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var ring = host.Scene.Parent(arc);
            bool activeMount = !arc.IsNull && !ring.IsNull
                && Near(host.Scene.Paint(ring).Opacity, 1f, 0.001f)
                && host.Animation.HasTracks(ring) && host.Animation.HasTracks(arc);

            var ring0 = ring;
            var arc0 = arc;
            active.Value = false;
            host.RunFrame(); host.RunFrame();
            bool stopped = ring == ring0 && arc == arc0
                && Near(host.Scene.Paint(ring).Opacity, 0f, 0.001f)
                && !host.Animation.HasTracks(ring)
                && !host.Animation.HasTracks(arc);

            active.Value = true;
            host.RunFrame();
            bool restarted = ring == ring0 && arc == arc0 && Near(host.Scene.Paint(ring).Opacity, 1f, 0.001f)
                && host.Animation.HasTracks(ring) && host.Animation.HasTracks(arc);

            Check("progress.1 ProgressRing isActive flows through re-pushed props: active spins, inactive stops, reactivation restarts without remount",
                activeMount && stopped && restarted,
                $"active={activeMount} stopped={stopped} restarted={restarted} same={ring == ring0 && arc == arc0}"
                + $" reOpacity={host.Scene.Paint(ring).Opacity:0.###} reRingTracks={host.Animation.HasTracks(ring)} reArcTracks={host.Animation.HasTracks(arc)}");
        }

        // Fresh inactive mount: no hidden compositor work should be seeded under opacity 0.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-ring-inactive", new Size2(240, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            NodeHandle arc = default;
            var parts = new TemplateParts();
            parts[ProgressRing.PartRing] = b => b with { OnRealized = h => arc = h };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => ProgressRing.Indeterminate(isActive: false, parts: parts),
            });
            host.RunFrame(); host.RunFrame();
            var ring = host.Scene.Parent(arc);
            bool idle = !arc.IsNull && !ring.IsNull
                && Near(host.Scene.Paint(ring).Opacity, 0f, 0.001f)
                && !host.Animation.HasTracks(ring)
                && !host.Animation.HasTracks(arc);
            Check("progress.2 inactive ProgressRing mounts idle with opacity 0 and zero animation tracks",
                idle, $"arc={arc} ring={ring} tracks arc={(!arc.IsNull && host.Animation.HasTracks(arc))}");
        }

        // ProgressBar: parent re-render updates state/width through re-pushed props, so the existing effect deps fire.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("progress-bar-lifecycle", new Size2(420, 180), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var state = new Signal<ProgressBarState>(ProgressBarState.Normal);
            var width = new Signal<float>(240f);
            var fills = new List<NodeHandle>(2);
            var parts = new TemplateParts();
            parts[ProgressBar.PartFill] = b => b with
            {
                OnRealized = h => { if (!fills.Contains(h)) fills.Add(h); },
            };
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 360, Height = 80, Padding = Edges4.All(16),
                    Children = [ProgressBar.Indeterminate(width.Value, state.Value, parts)],
                },
            });
            host.RunFrame();
            bool normalTracks = fills.Count == 2 && host.Animation.HasTracks(fills[0]) && host.Animation.HasTracks(fills[1]);
            var barRoot = fills.Count == 2 ? host.Scene.Parent(fills[0]) : NodeHandle.Null;

            state.Value = ProgressBarState.Paused;
            host.RunFrame(); host.RunFrame();
            bool paused = fills.Count == 2
                && Near(host.Scene.Paint(fills[0]).Opacity, 0f, 0.001f)
                && !host.Animation.HasTracks(fills[0])
                && ColorClose(host.Scene.Paint(fills[1]).Fill, Tok.SystemFillCaution, 0.004f);

            state.Value = ProgressBarState.Normal;
            host.RunFrame();
            bool resumed = fills.Count == 2
                && ColorClose(host.Scene.Paint(fills[1]).Fill, Tok.AccentDefault, 0.004f)
                && host.Animation.HasTracks(fills[0]) && host.Animation.HasTracks(fills[1]);

            width.Value = 300f;
            host.RunFrame(); host.RunFrame();
            bool resized = !barRoot.IsNull && Near(host.Scene.AbsoluteRect(barRoot).W, 300f, 0.5f);

            Check("progress.3 ProgressBar indeterminate state and width props update the preserved component",
                normalTracks && paused && resumed && resized,
                $"normal={normalTracks} paused={paused} resumed={resumed} resized={resized} width={(!barRoot.IsNull ? host.Scene.AbsoluteRect(barRoot).W : 0):0.#}");
        }

        // CheckBox: checked mark color/pressability must update through re-pushed props without remounting or replaying draw-on.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("checkbox-mark-props", new Size2(260, 160), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var enabled = new Signal<bool>(true);
            var markChecked = new Signal<CheckState>(CheckState.Checked);   // stable instance across re-renders
            using var host = new AppHost(app, window, device, fonts, strings, new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Width = 220, Height = 80, Padding = Edges4.All(16),
                    Children = [CheckBox.Create("opt", markChecked, isEnabled: enabled.Value)],
                },
            });
            host.RunFrame();
            var cb = FindRole(host.Scene, host.Scene.Root, AutomationRole.CheckBox);
            var mark = FindPolylineStrokeNode(host.Scene, cb);
            for (int i = 0; i < 30; i++) host.RunFrame();
            bool settled = !mark.IsNull && !host.Animation.HasTracks(mark);
            bool initialColor = !mark.IsNull && host.Scene.TryGetPolylineStroke(mark, out var before)
                && ColorClose(before.Color, Tok.TextOnAccentPrimary, 0.004f);

            enabled.Value = false;
            host.RunFrame(); host.RunFrame();
            var markAfter = FindPolylineStrokeNode(host.Scene, cb);
            bool sameNode = markAfter == mark;
            bool disabledColor = !markAfter.IsNull && host.Scene.TryGetPolylineStroke(markAfter, out var after)
                && ColorClose(after.Color, Tok.TextOnAccentDisabled, 0.004f);
            bool noReplay = !markAfter.IsNull && !host.Animation.HasTracks(markAfter);

            Check("progress.4 CheckBox mark props update through re-pushed props without remounting or replaying draw-on",
                settled && initialColor && sameNode && disabledColor && noReplay,
                $"settled={settled} initial={initialColor} same={sameNode} disabled={disabledColor} replay={!noReplay}");
        }
    }

    static void D3ExpanderWrapReflowChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("expander-wrap", new Size2(420, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        const string longText = "Hidden content, revealed when the Expander is expanded, long enough that it wraps across at least two lines at this card width.";
        var root = new W0fStaticProbe
        {
            // The gallery hosts the Expander in ControlExample's 'display': a ROW (Direction=0) with Grow=1 and
            // AlignItems=Start, so the Expander is NOT cross-stretched — it is sized from its own intrinsic measure.
            Build = () => new BoxEl
            {
                Direction = 1,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Grow = 1,
                        AlignItems = FlexAlign.Start,
                        Padding = Edges4.All(16),
                        Children =
                        [
                            Embed.Comp(() => new Expander
                            {
                                Header = "Section",
                                Content = new BoxEl
                                {
                                    Direction = 1,
                                    Gap = 8f,
                                    Children =
                                    [
                                        new TextEl(longText) { Size = 14f, Wrap = TextWrap.Wrap },
                                        new BoxEl { Height = 32f, Children = [new TextEl("An action") { Size = 14f }] },
                                    ],
                                },
                                InitiallyExpanded = true,
                            }),
                        ],
                    },
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);

        NodeHandle Anchor() => Child(host.Scene, Child(host.Scene, host.Scene.Root, 0), 0);   // root col -> display row -> Expander anchor

        (bool ok, float textY, float textH, float textW, float markerY) Probe()
        {
            var card = host.Scene.FirstChild(Anchor());
            var clip = Child(host.Scene, card, 1);
            if (clip.IsNull) return (false, 0f, 0f, 0f, 0f);
            var panel = Child(host.Scene, clip, 0);              // PartContent panel
            if (panel.IsNull) return (false, 0f, 0f, 0f, 0f);
            var vstack = Child(host.Scene, panel, 0);            // the user content column
            if (vstack.IsNull) return (false, 0f, 0f, 0f, 0f);
            var text = Child(host.Scene, vstack, 0);
            var marker = Child(host.Scene, vstack, 1);
            if (text.IsNull || marker.IsNull) return (false, 0f, 0f, 0f, 0f);
            var tr = host.Scene.AbsoluteRect(text);
            return (true, tr.Y, tr.H, tr.W, host.Scene.AbsoluteRect(marker).Y);
        }

        for (int i = 0; i < 5; i++) host.RunFrame();             // settle the initially-expanded mount
        var header = Child(host.Scene, host.Scene.FirstChild(Anchor()), 0);
        var (ok0, textY0, textH0, textW0, markerY0) = Probe();
        float lineH = 14f * 1.4f;                                // headless natural line height = size x 1.4
        bool wrapped0 = ok0 && textH0 > lineH * 1.5f;            // precondition: the body genuinely wraps to >=2 lines
        bool below0 = ok0 && markerY0 >= textY0 + textH0 - 0.5f; // marker sits fully below the body on the initial expand

        ClickNode(host, window, header);                         // collapse
        for (int i = 0; i < 22; i++) host.RunFrame();            // settle the 167ms collapse + the unmount frame
        ClickNode(host, window, header);                         // re-expand
        for (int i = 0; i < 32; i++) host.RunFrame();            // settle the 333ms expand

        var (ok1, textY1, textH1, textW1, markerY1) = Probe();
        bool heightStable = ok1 && Near(textH1, textH0, 1.0f);          // SAME reserved >=2-line height after the cycle
        bool below1 = ok1 && markerY1 >= textY1 + textH1 - 0.5f;        // marker still fully below the body (no overlap)
        bool markerStable = ok1 && Near(markerY1, markerY0, 1.0f);

        Check("cp3.f — Expander: auto-height wrapping body keeps its reserved height across a collapse->re-expand reflow (no overlap)",
            wrapped0 && below0 && heightStable && below1 && markerStable,
            $"wrapped0={wrapped0} textW {textW0:0.0}->{textW1:0.0} textH {textH0:0.0}->{textH1:0.0} (1line~{lineH:0.0}) markerY {markerY0:0.0}->{markerY1:0.0} below {below0}/{below1}");
    }

    static void D3ExpanderChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("expander-d3", new Size2(360, 320), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        // Host the Expander INSIDE a column wrapper with a following sibling: the scene root always fills the window,
        // so sibling Y is the observable proof that reveal height participates in parent layout.
        var root = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new Expander
                    {
                        Header = "Section",
                        Content = new BoxEl { Height = 60f, Children = new Element[] { new TextEl("expander-action") { Size = 14f } } },
                        InitiallyExpanded = false,
                    }),
                    new BoxEl { Height = 24f, Children = new Element[] { new TextEl("after-expander") { Size = 14f } } },
                ],
            },
        };
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();   // mount collapsed
        var anchor = Child(host.Scene, host.Scene.Root, 0);    // scene root IS the Build column → child 0 = component anchor
        var sibling = Child(host.Scene, host.Scene.Root, 1);   // the 24px row below the expander
        var card = host.Scene.FirstChild(anchor);              // component anchor → the card box
        var header = Child(host.Scene, card, 0);
        float headerH = host.Scene.AbsoluteRect(header).H;
        float siblingYCollapsed = host.Scene.AbsoluteRect(sibling).Y;

        // cp3.a — open click: the content mounts, but the click frame still PAINTS the old size (the reflow track's
        // JustSeeded first tick re-establishes 0 before record), so the sibling never jumps; later frames ease it down
        // MONOTONICALLY while the Trailing anchor keeps the panel's bottom edge on the reveal edge.
        ClickNode(host, window, header);
        var clip = Child(host.Scene, card, 1);           // the clip wrapper is ALWAYS mounted (the transition's host)
        var content = clip.IsNull ? NodeHandle.Null : Child(host.Scene, clip, 0);
        float cardHClick = host.Scene.AbsoluteRect(card).H;
        float clipHClick = clip.IsNull ? 0f : host.Scene.AbsoluteRect(clip).H;
        float contentH = content.IsNull ? 0f : host.Scene.AbsoluteRect(content).H;
        float contentExtent = contentH - 1f;             // the −1px border-overlap margin: panel bottom = contentH − 1
        float siblingYClick = host.Scene.AbsoluteRect(sibling).Y;
        bool monotoneOpen = true;
        float prevSibY = siblingYClick, clipHMid = 0f, shiftMid = 0f, siblingYMid = 0f;
        for (int i = 0; i < 30; i++)                     // ≥ 333ms — settle (sampled per frame for monotonicity)
        {
            host.RunFrame();
            float y = host.Scene.AbsoluteRect(sibling).Y;
            if (y < prevSibY - 0.25f) monotoneOpen = false;
            prevSibY = y;
            if (i == 2) { clipHMid = host.Scene.AbsoluteRect(clip).H; shiftMid = host.Scene.Paint(clip).ChildShiftY; siblingYMid = host.Scene.AbsoluteRect(sibling).Y; }
        }
        float clipHOpen = host.Scene.AbsoluteRect(clip).H;
        float siblingYOpen = host.Scene.AbsoluteRect(sibling).Y;
        float shiftDone = host.Scene.Paint(clip).ChildShiftY;
        bool liRestoredOpen = float.IsNaN(host.Scene.Layout(clip).Height);   // settle returned the declared NaN(auto)
        bool noClickJump = !clip.IsNull && !content.IsNull && Near(siblingYClick, siblingYCollapsed, 1.5f) && Near(cardHClick, headerH + clipHClick, 1.5f) && clipHClick < 2f;
        bool layoutRevealed = siblingYMid > siblingYClick + 4f && siblingYMid < siblingYOpen - 4f && clipHMid > 4f && clipHMid < clipHOpen - 4f;
        bool anchoredOpen = Near(shiftMid, clipHMid - contentExtent, 1.5f) && shiftMid < -4f;   // bottom edge rides the reveal edge
        Check("cp3.a — expand: sibling eases down monotonically (no click jump); the panel's bottom edge rides the reveal edge",
            noClickJump && layoutRevealed && monotoneOpen && anchoredOpen && MathF.Abs(shiftDone) < 0.01f
            && Near(clipHOpen, contentExtent, 1.5f) && liRestoredOpen,
            $"siblingY {siblingYCollapsed:0.0}→{siblingYClick:0.0}→{siblingYMid:0.0}→{siblingYOpen:0.0} clipH {clipHClick:0.0}→{clipHMid:0.0}→{clipHOpen:0.0} shift {shiftMid:0.0}→{shiftDone:0.00} liNaN={liRestoredOpen}");

        // cp3.b — close click: the content stays LIVE through the 167ms reflow while the sibling eases upward; only
        // after the reflow settles does the content unmount (the clip itself STAYS mounted at its declared 0 height).
        ClickNode(host, window, header);                 // collapse — the declared Height flips to 0; ExitDynamics leg
        float siblingYCloseClick = host.Scene.AbsoluteRect(sibling).Y;
        for (int i = 0; i < 3; i++) host.RunFrame();     // ~48ms into the 167ms reflow
        var contentEarly = Child(host.Scene, clip, 0);
        bool liveEarly = !contentEarly.IsNull && host.Scene.IsLive(contentEarly);
        float siblingYClosing = host.Scene.AbsoluteRect(sibling).Y;
        float clipHClosing = host.Scene.AbsoluteRect(clip).H;
        float shiftClosing = host.Scene.Paint(clip).ChildShiftY;
        for (int i = 0; i < 20; i++) host.RunFrame();    // settle + the collapse watcher's unmount frame
        bool unmounted = Child(host.Scene, clip, 0).IsNull && !Child(host.Scene, card, 1).IsNull;
        float closedH = host.Scene.AbsoluteRect(card).H;
        float siblingYClosed = host.Scene.AbsoluteRect(sibling).Y;
        bool liRestoredClosed = host.Scene.Layout(clip).Height == 0f;        // settle returned the declared 0
        bool noCloseJump = Near(siblingYCloseClick, siblingYOpen, 1.5f);
        bool layoutCollapsed = siblingYClosing < siblingYCloseClick - 4f && siblingYClosing > siblingYCollapsed + 4f && clipHClosing > 4f && clipHClosing < clipHOpen - 4f;
        bool anchoredClosing = Near(shiftClosing, clipHClosing - contentExtent, 1.5f) && shiftClosing < -8f;
        Check("cp3.b — collapse: content stays LIVE while the sibling eases up (anchored to the reveal edge), unmounts at settle",
            liveEarly && noCloseJump && layoutCollapsed && anchoredClosing && unmounted && Near(closedH, headerH, 1.5f)
            && Near(siblingYClosed, siblingYCollapsed, 1.5f) && liRestoredClosed,
            $"liveEarly={liveEarly} siblingY {siblingYOpen:0.0}→{siblingYCloseClick:0.0}→{siblingYClosing:0.0}→{siblingYClosed:0.0} clipHClosing={clipHClosing:0.0} shift={shiftClosing:0.0} unmounted={unmounted} li0={liRestoredClosed}");

        // cp3.e — TemplateParts: a part modifier restyles (header fill, content padding) but can NEVER break the
        // control's mechanics — the control re-asserts them after the modifier (a hostile OnClick = null is defeated;
        // the toggle still opens the card).
        {
            using var app3 = new HeadlessPlatformApp();
            var window3 = new HeadlessWindow(new WindowDesc("expander-d3p", new Size2(360, 320), 1f));
            window3.Show();
            var device3 = new HeadlessGpuDevice();
            var partFill = ColorF.FromRgba(10, 200, 30);
            var root3 = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        Embed.Comp(() => new Expander
                        {
                            Header = "Parted",
                            Content = new BoxEl { Height = 60f },
                            Parts = new()
                            {
                                [Expander.PartHeader] = b => b with { Fill = partFill, OnClick = null },   // hostile clobber attempt
                                [Expander.PartContent] = c => c with { Padding = Edges4.All(0) },
                            },
                        }),
                    ],
                },
            };
            using var host3 = new AppHost(app3, window3, device3, new HeadlessFontSystem(strings), strings, root3);
            host3.RunFrame();
            var card3 = host3.Scene.FirstChild(Child(host3.Scene, host3.Scene.Root, 0));
            var header3 = Child(host3.Scene, card3, 0);
            var clip3 = Child(host3.Scene, card3, 1);
            bool fillApplied = host3.Scene.Paint(header3).Fill.Equals(partFill);
            ClickNode(host3, window3, header3);                  // would be dead if the modifier's OnClick=null won
            for (int i = 0; i < 30; i++) host3.RunFrame();       // settle the reveal
            var content3 = Child(host3.Scene, clip3, 0);
            bool opened = !content3.IsNull && host3.Scene.AbsoluteRect(clip3).H > 40f;
            // Padding 0 via the part: the panel solves at the user content's height (60), not 60 + 2×16 default padding.
            bool padApplied = !content3.IsNull && Near(host3.Scene.AbsoluteRect(content3).H, 60f, 1.5f);
            Check("cp3.e — TemplateParts: part modifiers restyle (fill, padding) but mechanics are re-asserted (toggle survives OnClick=null)",
                fillApplied && opened && padApplied,
                $"fill={fillApplied} opened={opened} clipH={host3.Scene.AbsoluteRect(clip3).H:0} contentH={(content3.IsNull ? -1f : host3.Scene.AbsoluteRect(content3).H):0}");
        }

        // cp3.c — resting expanded mount (the gallery page mounts initiallyExpanded:true): no motion is seeded (the
        // first frame never FLIP-captures), the child-shift rests at 0, the content paints, and the LAST content
        // child's absolute bottom sits INSIDE the card's absolute bottom — the clipped "An action" gallery bug.
        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("expander-d3b", new Size2(360, 320), 1f));
        window2.Show();
        var device2 = new HeadlessGpuDevice();
        var root2 = new W0fStaticProbe
        {
            Build = () => new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new Expander
                    {
                        Header = "Section",
                        Content = new BoxEl { Height = 60f, Children = new Element[] { new TextEl("expander-action") { Size = 14f } } },
                        InitiallyExpanded = true,
                    }),
                ],
            },
        };
        using var host2 = new AppHost(app2, window2, device2, new HeadlessFontSystem(strings), strings, root2);
        for (int i = 0; i < 4; i++) host2.RunFrame();
        var card2 = host2.Scene.FirstChild(Child(host2.Scene, host2.Scene.Root, 0));   // component anchor → card box
        var clip2 = Child(host2.Scene, card2, 1);
        var content2 = clip2.IsNull ? NodeHandle.Null : Child(host2.Scene, clip2, 0);
        var inner2 = content2.IsNull ? NodeHandle.Null : Child(host2.Scene, content2, 0);   // the user content row
        float tyRest = content2.IsNull ? 1f : host2.Scene.Paint(content2).LocalTransform.Dy;
        float shiftRest2 = clip2.IsNull ? 1f : host2.Scene.Paint(clip2).ChildShiftY;
        var rootR = host2.Scene.AbsoluteRect(card2);
        var innerR = inner2.IsNull ? default : host2.Scene.AbsoluteRect(inner2);
        bool contained = !inner2.IsNull && innerR.Y + innerR.H <= rootR.Y + rootR.H + 0.5f;
        bool noStaleReveal = float.IsNaN(host2.Scene.Paint(card2).PresentedH) && float.IsNaN(host2.Scene.Paint(clip2).PresentedH);
        Check("cp3.c — initiallyExpanded rests with no motion (zero shift) and the content inside the card bottom (no clipping)",
            MathF.Abs(tyRest) < 0.01f && MathF.Abs(shiftRest2) < 0.01f && contained && noStaleReveal && HasGlyph(device2, strings, "expander-action"),
            $"tyRest={tyRest:0.00} shiftRest={shiftRest2:0.00} innerBottom={(innerR.Y + innerR.H):0} cardBottom={(rootR.Y + rootR.H):0} presentedHNaN={noStaleReveal}");
    }

    static void D5EditableComboBoxChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("d5-cmb", new Size2(420, 320), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new ComboProbe(true);
        using var host = new AppHost(app, window, device, fonts, strings, root);
        host.RunFrame();
        var scene = host.Scene;
        void Settle() { for (int i = 0; i < 16; i++) host.RunFrame(); }

        // cp5.a — exactly ONE bordered node in the field subtree; the EditableText part is chromeless at rest.
        var combo = FindRole(scene, scene.Root, AutomationRole.ComboBox);
        var field = FindRole(scene, scene.Root, AutomationRole.Text);
        int bordered = 0;
        void CountBorders(NodeHandle n)
        {
            if (n.IsNull) return;
            if (scene.Paint(n).BorderWidth > 0f) bordered++;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) CountBorders(c);
        }
        CountBorders(combo);
        bool partChromeless = Near(scene.Paint(field).BorderWidth, 0f, 0.01f) && scene.Paint(field).Fill.A == 0f
            && Near(scene.Paint(field).Corners.TopLeft, 0f, 0.01f);
        Check("cp5.a — editable combo: ONE bordered box owns the chrome; the EditableText part paints no border/fill/corners at rest",
            bordered == 1 && Near(scene.Paint(combo).BorderWidth, 1f, 0.01f) && partChromeless,
            $"bordered={bordered} outerBw={scene.Paint(combo).BorderWidth:0.##} partBw={scene.Paint(field).BorderWidth:0.##} partFillA={scene.Paint(field).Fill.A:0.##}");

        // cp5.b — DropDownGlyph 12×12 right-inset 14 (hit-test-invisible); DropDownOverlay 30 wide, margin 4; the
        // part spans the FULL field width (ComboBoxEditableTextPadding right 38 keeps text clear of the column).
        var comboR = scene.AbsoluteRect(combo);
        var fieldR = scene.AbsoluteRect(field);
        var glyphText = FindTextNode(scene, strings, combo, Icons.ChevronDown);
        var glyphBox = glyphText.IsNull ? NodeHandle.Null : scene.Parent(glyphText);
        var glyphR = glyphBox.IsNull ? default(RectF) : scene.AbsoluteRect(glyphBox);
        var overlayBtn = FindRole(scene, combo, AutomationRole.Button);
        var ovR = overlayBtn.IsNull ? default(RectF) : scene.AbsoluteRect(overlayBtn);
        Check("cp5.b — glyph 12×12 right-inset 14; overlay button width 30 margin 4; the part spans the full field width",
            !glyphBox.IsNull && Near(glyphR.W, 12f) && Near(glyphR.H, 12f) && Near(comboR.Right - glyphR.Right, 14f)
            && !overlayBtn.IsNull && Near(ovR.W, 30f) && Near(ovR.H, comboR.H - 8f)
            && Near(comboR.Right - ovR.Right, 4f) && Near(ovR.Y - comboR.Y, 4f)
            && Near(fieldR.W, comboR.W),
            $"glyph={glyphR.W:0.#}x{glyphR.H:0.#}@right-{comboR.Right - glyphR.Right:0.#} overlay={ovR.W:0.#}x{ovR.H:0.#}@right-{comboR.Right - ovR.Right:0.#}/top+{ovR.Y - comboR.Y:0.#} fieldW={fieldR.W:0.#}/{comboR.W:0.#}");

        // cp5.c — focus the part: the 2px accent bottom bar sits on the OUTER box; the part paints input-active fill.
        ClickNode(host, window, field);
        host.RunFrame();
        combo = FindRole(scene, scene.Root, AutomationRole.ComboBox);
        field = FindRole(scene, scene.Root, AutomationRole.Text);
        comboR = scene.AbsoluteRect(combo);
        var bar = NodeHandle.Null;
        for (var c = scene.FirstChild(combo); !c.IsNull; c = scene.NextSibling(c))
            if (Near(scene.AbsoluteRect(c).H, 2f) && ColorClose(scene.Paint(c).Fill, Tok.AccentDefault, 0.004f)) bar = c;
        var barR = bar.IsNull ? default(RectF) : scene.AbsoluteRect(bar);
        bool inputActive = ColorClose(scene.Paint(field).Fill, Tok.FillControlInputActive, 0.004f);
        Check("cp5.c — focused editable combo: 2px accent bottom bar on the OUTER box (TextControlBorderThemeThicknessFocused 1,1,1,2) + input-active part fill",
            (scene.Flags(field) & NodeFlags.Focused) != 0 && !bar.IsNull
            && Near(barR.W, comboR.W) && Near(barR.Bottom, comboR.Bottom) && inputActive,
            $"focused={(scene.Flags(field) & NodeFlags.Focused) != 0} bar={!bar.IsNull} barW={barR.W:0.#}/{comboR.W:0.#} bottomGap={comboR.Bottom - barR.Bottom:0.#} inputActive={inputActive}");

        // cp5.d — clicking the overlay opens the dropdown and the FIELD keeps focus (the overlay is no focus target).
        root.Sel!.Value = 1;   // give the open list a selected row (pill)
        host.RunFrame();
        var overlay2 = FindRole(scene, FindRole(scene, scene.Root, AutomationRole.ComboBox), AutomationRole.Button);
        ClickNode(host, window, overlay2);
        host.RunFrame();
        var rows = Roles(scene, AutomationRole.MenuItem);
        field = FindRole(scene, scene.Root, AutomationRole.Text);
        Check("cp5.d — overlay click opens the dropdown; the text field stays focused",
            rows.Count == 3 && (scene.Flags(field) & NodeFlags.Focused) != 0,
            $"rows={rows.Count} fieldFocused={(scene.Flags(field) & NodeFlags.Focused) != 0}");

        // cp5.e — dropdown anatomy: rows margin 5,2,5,2 + corner 3 + content inset 11; selection pill 3×16 r1.5
        // accent FLUSH left; surface corner 8 corner-joined (popup tops squared, field bottoms squared).
        bool rowsOk = false, pillOk = false, joinOk = false;
        if (rows.Count == 3)
        {
            var r0 = scene.AbsoluteRect(rows[0]);
            var r1 = scene.AbsoluteRect(rows[1]);
            var surf = rows[0];
            while (!surf.IsNull && scene.Paint(surf).BorderWidth < 0.5f) surf = scene.Parent(surf);
            var surfR = surf.IsNull ? default(RectF) : scene.AbsoluteRect(surf);
            var label = FindTextNode(scene, strings, rows[0], "Red");
            float labelInset = label.IsNull ? -1f : scene.AbsoluteRect(label).X - r0.X;
            rowsOk = !surf.IsNull
                && Near(r0.X - surfR.X, 5f, 1f) && Near(surfR.Right - r0.Right, 5f, 1f)   // LayoutRoot Margin 5,_,5,_
                && Near(r1.Y - r0.Bottom, 4f, 0.6f)                                        // 2 + 2 vertical margins
                && Near(scene.Paint(rows[0]).Corners.TopLeft, 3f, 0.01f)                   // ComboBoxItemCornerRadius
                && Near(labelInset, 11f, 1f);                                              // ComboBoxItemThemePadding 11
            NodeHandle pill = NodeHandle.Null;
            void FindPill(NodeHandle n)
            {
                if (n.IsNull) return;
                var rr = scene.AbsoluteRect(n);
                if (Near(rr.W, 3f) && Near(rr.H, 16f)) pill = n;
                for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) FindPill(c);
            }
            FindPill(rows[1]);
            pillOk = !pill.IsNull && ColorClose(scene.Paint(pill).Fill, Tok.AccentDefault, 0.004f)
                && Near(scene.Paint(pill).Corners.TopLeft, 1.5f, 0.01f)
                && Near(scene.AbsoluteRect(pill).X - r1.X, 0f, 0.6f);                      // ITEM pill is FLUSH (:759)
            combo = FindRole(scene, scene.Root, AutomationRole.ComboBox);
            joinOk = !surf.IsNull
                && Near(scene.Paint(surf).Corners.TopLeft, 0f, 0.01f) && Near(scene.Paint(surf).Corners.BottomLeft, Radii.Overlay, 0.01f)
                && Near(scene.Paint(combo).Corners.TopLeft, Radii.Control, 0.01f) && Near(scene.Paint(combo).Corners.BottomLeft, 0f, 0.01f);
        }
        Check("cp5.e — dropdown rows margin 5,2,5,2 corner 3 inset 11 + flush selection pill 3×16 r1.5; surface corner 8 corner-joined to the field",
            rowsOk && pillOk && joinOk, $"rows={rowsOk} pill={pillOk} join={joinOk}");

        // cp5.f — a second overlay-position click closes (toggle); the field keeps focus through the close.
        var overlay3 = FindRole(scene, FindRole(scene, scene.Root, AutomationRole.ComboBox), AutomationRole.Button);
        ClickNode(host, window, overlay3);
        Settle();
        field = FindRole(scene, scene.Root, AutomationRole.Text);
        Check("cp5.f — overlay click while open closes the dropdown; the field stays focused",
            Roles(scene, AutomationRole.MenuItem).Count == 0 && (scene.Flags(field) & NodeFlags.Focused) != 0,
            $"rows={Roles(scene, AutomationRole.MenuItem).Count} fieldFocused={(scene.Flags(field) & NodeFlags.Focused) != 0}");
    }

    static void D67SplitButtonFlyoutChecks(StringTable strings)
    {
        // Menu chrome (D67) splits the FlyoutSurface: acrylic+stroke+shadow live on the stretch PLATE (first child of
        // the surface); the open/close channels live on the plate's PARENT (the transparent ZStack surface).
        static NodeHandle SurfaceOf(SceneStore sc, NodeHandle n)
        {
            for (; !n.IsNull; n = sc.Parent(n))
            {
                if (sc.TryGetAcrylic(n, out _)) return n;
                for (var c = sc.FirstChild(n); !c.IsNull; c = sc.NextSibling(c))
                    if (sc.TryGetAcrylic(c, out _) || (sc.FirstChild(c).IsNull && sc.Paint(c).BorderWidth > 0.5f)) return n;
            }
            return NodeHandle.Null;
        }

        // cp6.b — SplitButton extents: primary h32 (and ≥35 wide), secondary 35×32, divider 1px, root MinHeight 32 +
        // hug-left (HorizontalAlignment=Left → AlignSelf.Start).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp6ext", new Size2(360, 160), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            using var host = new AppHost(app, window, device, fonts, strings, new SplitButtonProbe());
            host.RunFrame();
            var btns = Roles(host.Scene, AutomationRole.Button);
            var primary = btns.Count > 0 ? btns[0] : NodeHandle.Null;
            var drop = btns.Count > 1 ? btns[1] : NodeHandle.Null;
            var outer = primary.IsNull ? NodeHandle.Null : host.Scene.Parent(primary);
            var divider = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 1);
            bool primH = !primary.IsNull && Near(host.Scene.Bounds(primary).H, 32f);
            bool dropExt = !drop.IsNull && Near(host.Scene.Bounds(drop).W, 35f) && Near(host.Scene.Bounds(drop).H, 32f);
            bool divExt = !divider.IsNull && Near(host.Scene.Bounds(divider).W, 1f);
            bool primMin = !primary.IsNull && host.Scene.Bounds(primary).W >= 34.5f;
            bool rootSpec = !outer.IsNull && Near(host.Scene.Layout(outer).MinH, 32f)
                            && host.Scene.Layout(outer).AlignSelf == FlexAlign.Start;   // SplitButton.xaml:8 HorizontalAlignment=Left
            Check("cp6.b — SplitButton extents: primary h32 ≥35w, secondary 35×32, divider 1px, root MinHeight 32 + hug-left",
                primH && dropExt && divExt && primMin && rootSpec,
                $"primary={(primary.IsNull ? 0 : host.Scene.Bounds(primary).W):0}×{(primary.IsNull ? 0 : host.Scene.Bounds(primary).H):0} " +
                $"drop={(drop.IsNull ? 0 : host.Scene.Bounds(drop).W):0}×{(drop.IsNull ? 0 : host.Scene.Bounds(drop).H):0} " +
                $"div={(divider.IsNull ? 0 : host.Scene.Bounds(divider).W):0} rootSpec={rootSpec}");
        }

        // cp6.a — live SplitButton: the menu wrapper lands at (anchor.X, anchor.Bottom+4) and the VISIBLE (post-clip)
        // top edge never rises above that line mid-unfold; menus do not fade at open.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("cp6live", new Size2(480, 400), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, new SplitButtonLongMenuProbe(), frameTime: clock);
            host.PopupWindowsEnabled = false;   // explicitly verify the constrained fallback's local clip reveal
            host.RunFrame();

            var halves = Roles(host.Scene, AutomationRole.Button);
            var primary = halves.Count > 0 ? halves[0] : NodeHandle.Null;
            var secondary = halves.Count > 1 ? halves[1] : NodeHandle.Null;
            var anchorRect = primary.IsNull ? default : host.Scene.AbsoluteRect(host.Scene.Parent(primary));
            if (!secondary.IsNull) ClickNode(host, window, secondary);   // open (mount + place + seed in the layout effect)
            host.RunFrame();                                             // compose the t=0 keyframes
            var surface = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
            var wrapper = surface.IsNull ? NodeHandle.Null : host.Scene.Parent(surface);
            float expX = anchorRect.X, expY = anchorRect.Bottom + 4f;    // FlyoutBase::FlyoutMargin = 4
            float minVisTop = float.MaxValue;
            bool opaqueAllTheWay = !surface.IsNull;
            for (int i = 0; i < 6 && !surface.IsNull; i++)
            {
                var sp = host.Scene.Paint(surface);
                float visTop = host.Scene.AbsoluteRect(surface).Y + (sp.ClipRect.IsInfinite ? 0f : sp.ClipRect.Y);
                minVisTop = MathF.Min(minVisTop, visTop);
                if (sp.Opacity < 0.99f) opaqueAllTheWay = false;
                clock.Advance(16f); host.RunFrame();
            }
            for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }   // > 250ms → settled
            var wr = wrapper.IsNull ? default : host.Scene.AbsoluteRect(wrapper);
            bool placed = !wrapper.IsNull && Near(wr.X, expX, 0.75f) && Near(wr.Y, expY, 0.75f);
            bool neverAbove = !surface.IsNull && minVisTop >= expY - 0.75f;
            var pEnd = surface.IsNull ? default : host.Scene.Paint(surface);
            bool settled = !surface.IsNull && pEnd.ClipRect.IsInfinite && Near(pEnd.LocalTransform.Dy, 0f, 0.1f);
            Check("cp6.a — SplitButton menu: wrapper at (anchor.X, anchor.Bottom+4); visible top never above it mid-open; no open fade",
                placed && neverAbove && opaqueAllTheWay && settled,
                $"wrapper=({wr.X:0.0},{wr.Y:0.0}) exp=({expX:0.0},{expY:0.0}) minVisTop={minVisTop:0.0} opaque={opaqueAllTheWay} settled={settled}");
        }

        // cp6.c + cp7.d/e/f/g — the OverlayHost motion paths against the OverlayProbe's 120×32 anchor at (20,20).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("d67motion", new Size2(480, 400), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var svc = root.Service!;
            void Settle() { for (int i = 0; i < 40; i++) { clock.Advance(16f); host.RunFrame(); } }
            var anchorRect = host.Scene.AbsoluteRect(root.Anchor);

            // cp6.c — the EXACT open call DropDownButton/SplitButton make (BottomLeft + FocusTrap + WINDOWED): placed at
            // (anchor.X, anchor.Bottom+4). For a windowed (OS-backed desktop-acrylic) popup the WinUI model slides the
            // WHOLE composition root (CompositionBackdrop, real backend) + stretches the presenter plate — so the ENGINE
            // must leave the SurfaceNode STATIC here (no content TranslateY, no node-local clip), or it would
            // double-animate against the composition slide. (The plate ScaleY stretch is asserted by cp7.d.) The presenter
            // never fades on open (opacity pinned 1). The composition slide itself isn't observable on the headless device
            // (no CompositionBackdrop); the metrics it receives are asserted by cp6.h.
            {
                var hd = svc.Open(() => root.Anchor,
                    () => MenuFlyout.Create(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                host.RunFrame();   // mount + place + seed
                host.RunFrame();   // compose t=0
                var s = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
                var wrapper = s.IsNull ? NodeHandle.Null : host.Scene.Parent(s);
                var wr = wrapper.IsNull ? default : host.Scene.AbsoluteRect(wrapper);
                var p0 = s.IsNull ? default : host.Scene.Paint(s);
                bool placed = !wrapper.IsNull && Near(wr.X, anchorRect.X, 0.75f) && Near(wr.Y, anchorRect.Bottom + 4f, 0.75f);
                // Windowed: the engine leaves the surface static (Dy≈0, clip infinite) — the composition root carries the slide.
                bool staticSurface = !s.IsNull && Near(p0.LocalTransform.Dy, 0f, 0.5f) && p0.ClipRect.IsInfinite && p0.Opacity > 0.99f;
                hd.Close();
                Settle();
                Check("cp6.c — DropDownButton-path WINDOWED menu: placed at (anchor.X, anchor.Bottom+4); engine leaves the surface static (no clip/translate — the composition root carries the slide), opacity 1",
                    placed && staticSurface,
                    $"wrapper=({wr.X:0.0},{wr.Y:0.0}) exp=({anchorRect.X:0.0},{anchorRect.Bottom + 4f:0.0}) dy={p0.LocalTransform.Dy:0.0} clipInf={p0.ClipRect.IsInfinite} op={p0.Opacity:0.00}");
            }

            // cp6.h — the windowed-popup CHROME METRICS the engine hands across the RHI seam (ConfigurePopupChrome — the
            // no-WinAppSDK stand-in for WinUI's SystemBackdrop placement). A downward (BottomLeft) root menu ⇒ OpensUp=false,
            // ClosedRatio=0.5 (MenuPopupThemeTransition root constant), a non-empty content rect + a corner radius, and the
            // open motion is played exactly once. The composition slide (initialTranslateY = (opensUp?+:−)·contentH·ClosedRatio)
            // runs on the real D3D12 backend; here we assert the RHI receives the correct parameters.
            {
                var hd = svc.Open(() => root.Anchor,
                    () => MenuFlyout.Create(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                host.RunFrame(); host.RunFrame();
                var hs = host.PopupWindows.Count == 1 ? host.PopupWindows[0].Swapchain as HeadlessSwapchain : null;
                var m = hs?.LastPopupChrome;
                bool gotMetrics = m is { } mm && !mm.OpensUp && Near(mm.ClosedRatio, 0.5f, 0.01f)
                    && mm.ContentRectPx.H > 1f && mm.ContentRectPx.W > 1f && mm.CornerRadiusPx > 0f;
                bool played = hs?.PopupOpenPlayed == true;
                hd.Close();
                Settle();
                Check("cp6.h — windowed popup: RHI receives chrome metrics (downward, closedRatio 0.5, content rect, corner radius) + open played once",
                    gotMetrics && played,
                    $"metrics={(m is { } x ? $"up={x.OpensUp} cr={x.ClosedRatio:0.00} w={x.ContentRectPx.W:0.0} h={x.ContentRectPx.H:0.0} corner={x.CornerRadiusPx:0.0}" : "null")} played={played}");
            }

            // cp6.i — CommandBarFlyout suppresses the popup slide: closedRatio=0 must survive the host seam unchanged
            // (zero is a contract value, not "unset"). The window still presents once before it is shown/animated.
            {
                var hd = svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = 180f, Height = 64f, Children = [new TextEl("commandbar-body") { Size = 12f }] },
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(Chrome: PopupChrome.CommandBar) { ConstrainToRootBounds = false });
                host.RunFrame(); host.RunFrame();
                var hs = host.PopupWindows.Count == 1 ? host.PopupWindows[0].Swapchain as HeadlessSwapchain : null;
                bool zero = hs?.LastPopupChrome is { } m && Near(m.ClosedRatio, 0f, 0.001f);
                bool seededBeforeShow = hs is { PresentCount: >= 1, PopupOpenPlayed: true };
                hd.Close();
                Settle();
                Check("cp6.i — CommandBar window chrome keeps closedRatio 0 and opens only after a seeded present",
                    zero && seededBeforeShow,
                    $"zero={zero} present={hs?.PresentCount ?? 0} played={hs?.PopupOpenPlayed}");
            }

            host.PopupWindowsEnabled = false;   // remaining checks exercise the in-window transition implementation

            // cp7.d — menu plate (WinUI MenuFlyoutPresenterBorder ScaleY, LayoutTransition_partial.cpp:497-503):
            // ScaleY (1−ratio)→1 mid-flight about the BOTTOM pivot (AnimationDirection_Top sets CenterY=openedLength —
            // a downward menu scales about its bottom/anchor-far edge), settled at 1 by 250ms; the surface stays opaque
            // and its content TranslateY slides in (Dy<0 mid-flight, MenuPopupThemeTransition).
            {
                svc.Open(() => root.Anchor,
                    () => MenuFlyout.Create(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two"), new MenuFlyoutItem("Three") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft);
                host.RunFrame();
                host.RunFrame();   // t=0
                var s = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
                var plate = s.IsNull ? NodeHandle.Null : host.Scene.FirstChild(s);
                bool plateChrome = !plate.IsNull && host.Scene.TryGetAcrylic(plate, out _) && Near(host.Scene.Paint(plate).BorderWidth, 1f);
                clock.Advance(16f); host.RunFrame();   // mid-flight (E(16/250)=0.3517 → scale ≈ 0.676)
                var sp = s.IsNull ? default : host.Scene.Paint(s);
                float midScale = plate.IsNull ? 0f : host.Scene.Paint(plate).LocalTransform.M22;
                // Surface translates in (Dy<0) mid-flight; plate scale strictly between (1−ratio) and 1; opacity 1.
                bool mid = !plate.IsNull && midScale > 0.51f && midScale < 0.99f && sp.Opacity > 0.99f && sp.LocalTransform.Dy < -0.1f;
                bool pivot = !plate.IsNull && Near(host.Scene.Paint(plate).OriginY, 1f, 0.01f);   // opens DOWN → CenterY=openedLength (BOTTOM pivot)
                for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }            // > 250ms
                bool settledScale = !plate.IsNull && Near(host.Scene.Paint(plate).LocalTransform.M22, 1f, 0.01f);
                svc.CloseTop();
                Settle();
                Check("cp7.d — menu plate ScaleY (1−ratio)→1 over 250ms about the BOTTOM pivot; opacity 1 + content TranslateY sliding (Dy<0) mid-flight",
                    plateChrome && mid && pivot && settledScale,
                    $"plate={plateChrome} mid={midScale:0.000} pivot={pivot}(originY={(plate.IsNull ? -1f : host.Scene.Paint(plate).OriginY):0.00}) settled={settledScale} op={sp.Opacity:0.00} dy={sp.LocalTransform.Dy:0.0}");
            }

            // cp7.e — Dropdown SEAM (SplitOpen/SplitClose around the selected-row centre): both clip edges animate, the
            // band stays centred on the seam, content TranslateY stays 0, no open fade; close collapses toward the
            // seam with the fade only in the last 83ms (begin 84ms).
            {
                NodeHandle body = NodeHandle.Null;
                svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = 240f, Height = 120f, Fill = Tok.FillCardDefault, OnRealized = h => body = h },
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(Chrome: PopupChrome.Dropdown) { SeamOffsetY = 20f });
                host.RunFrame();
                host.RunFrame();   // t=0
                var s = SurfaceOf(host.Scene, body);
                float H = s.IsNull ? 0f : host.Scene.Bounds(s).H;            // body 120 + presenter padding (0,2,0,2)
                float c = H * 0.5f + 20f;
                float half0 = MathF.Max(0.25f * H, 20f);                     // ThemeAnimations.cpp:655-668
                float halfF = H * 0.5f + 20f;                                // cpp:674
                var p0 = s.IsNull ? default : host.Scene.Paint(s);
                bool t0 = !s.IsNull && !p0.ClipRect.IsInfinite
                    && Near(p0.ClipRect.Y, c - half0, 1.5f) && Near(p0.ClipRect.Bottom, c + half0, 1.5f)
                    && p0.ClipRect.Y > 0.5f && p0.ClipRect.Bottom < H - 0.5f          // seam-centred: ClipT>0 AND ClipB<H
                    && Near(p0.LocalTransform.Dy, 0f, 0.1f) && p0.Opacity > 0.99f;    // content translate 0, no fade
                clock.Advance(16f); host.RunFrame();
                var p1 = s.IsNull ? default : host.Scene.Paint(s);
                bool bothMove = !s.IsNull && !p1.ClipRect.IsInfinite
                    && p1.ClipRect.Y < p0.ClipRect.Y - 1f && p1.ClipRect.Y > 0.5f
                    && p1.ClipRect.Bottom > p0.ClipRect.Bottom + 1f
                    && Near(p1.LocalTransform.Dy, 0f, 0.1f) && p1.Opacity > 0.99f;
                for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }
                bool opened = !s.IsNull && host.Scene.Paint(s).ClipRect.IsInfinite;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();                          // t=48 < 84ms begin → still opaque
                float halfClose = MathF.Max(0.075f * H, 20f - 0.35f * H);     // cpp:798-811 (compensation inactive here)
                var c48 = s.IsNull ? default : host.Scene.Paint(s);
                float e48 = 0.7320f;                                          // E(48/167) for cubic(0,0,0,1)
                bool close48 = !s.IsNull && c48.Opacity > 0.99f
                    && Near(c48.ClipRect.Y, (c - halfClose) * e48, 2f)
                    && Near(c48.ClipRect.Bottom, H + (c + halfClose - H) * e48, 2f)
                    && (c48.ClipRect.Y + c48.ClipRect.Bottom) * 0.5f > (H * 0.5f + 5f);   // midpoint moving toward the seam
                clock.Advance(64f); host.RunFrame();                          // t=112 → fade (112−84)/83 = 0.3373
                float op112 = !s.IsNull && host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close112 = Near(op112, 1f - (112f - 84f) / 83f, 0.05f);
                Settle();
                Check("cp7.e — Dropdown seam: SplitOpen band centred on the seam (ClipT>0 ∧ ClipB<H, translate 0, no fade); SplitClose collapses toward it, fade after 84ms",
                    t0 && bothMove && opened && close48 && close112,
                    $"t0={t0} (clipT={p0.ClipRect.Y:0.0}≈{c - half0:0.0} clipB={p0.ClipRect.Bottom:0.0}≈{c + half0:0.0}/{H:0.0}) both={bothMove} " +
                    $"opened={opened} close48={close48} (op={c48.Opacity:0.00} T={c48.ClipRect.Y:0.0} B={c48.ClipRect.Bottom:0.0}) close112={close112} (op={op112:0.000})");
            }

            // cp7.f — plain Flyout (PopupChrome.Popup, TAS_SHOWPOPUP): TranslateY −50→0 over 367ms on
            // cubic-bezier(.1,.9,.2,1); opacity holds at zero for 83ms then fades to one over 83ms linear.
            {
                NodeHandle body = NodeHandle.Null;
                svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = 240f, Height = 88f, Fill = Tok.FillCardDefault, OnRealized = h => body = h },
                    FlyoutPlacement.BottomLeft, new PopupOptions(Chrome: PopupChrome.Popup));
                host.RunFrame();
                host.RunFrame();   // t=0
                var s = SurfaceOf(host.Scene, body);
                var p0 = s.IsNull ? default : host.Scene.Paint(s);
                bool t0 = !s.IsNull && Near(p0.LocalTransform.Dy, -50f, 1f) && p0.Opacity < 0.01f;
                clock.Advance(40f); host.RunFrame();
                var p40 = s.IsNull ? default : host.Scene.Paint(s);
                bool t40 = !s.IsNull && p40.Opacity < 0.01f
                    && p40.LocalTransform.Dy > -49f && p40.LocalTransform.Dy < 0f;
                clock.Advance(80f); host.RunFrame();   // t=120
                var p120 = s.IsNull ? default : host.Scene.Paint(s);
                bool t120 = !s.IsNull && Near(p120.Opacity, (120f - 83f) / 83f, 0.06f)
                    && p120.LocalTransform.Dy > p40.LocalTransform.Dy && p120.LocalTransform.Dy <= 0.5f;
                svc.CloseTop();
                Settle();
                Check("cp7.f — plain Flyout: TranslateY −50→0 over 367ms cubic(.1,.9,.2,1); opacity holds 83ms then fades over 83ms",
                    t0 && t40 && t120,
                    $"t0={t0} (dy={p0.LocalTransform.Dy:0.0} op={p0.Opacity:0.00}) t40={t40} (dy={p40.LocalTransform.Dy:0.0} op={p40.Opacity:0.00}) " +
                    $"t120={t120} (dy={p120.LocalTransform.Dy:0.0} op={p120.Opacity:0.000})");
            }

            // gate.overlay.flyout-first-frame — the OPEN PIPELINE must present a regular Flyout PLACED + ENTER-SEEDED on
            // the very first frame its surface exists in the scene: never a frame of unplaced (wrapper at the full-bleed
            // origin) or unseeded (opacity 1 / no slide offset) content that then jumps. WinUI defers showing the popup
            // until placement completes; here place+seed run in the OverlayHost layout effect (frame phase 6.5) BEFORE
            // SceneRecorder.Record (phase 8), so the first RECORDED frame is already correct. Step ONE frame at a time
            // (clock frozen at t=0) and, on the FIRST frame the surface is live, assert the wrapper sits at the placed
            // rect (anchor.X, anchor.Bottom+4) AND the surface is enter-seeded (opacity 0, TranslateY −50).
            {
                NodeHandle body = NodeHandle.Null;
                svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = 200f, Height = 90f, Fill = Tok.FillCardDefault, OnRealized = h => body = h },
                    FlyoutPlacement.BottomLeft, new PopupOptions(Chrome: PopupChrome.Popup));
                float expX = anchorRect.X;
                float expY = anchorRect.Bottom + FlyoutPositioner.FlyoutMargin;   // BottomLeft: below the anchor + 4px margin
                int firstLiveFrame = -1;
                bool firstPlacedSeeded = false;
                float sawX = -1f, sawY = -1f, sawOp = -1f, sawDy = 0f;
                for (int f = 0; f < 5 && firstLiveFrame < 0; f++)
                {
                    host.RunFrame();   // NO clock.Advance — freeze at t=0 so the ENTER INITIAL is the composed value
                    var s = SurfaceOf(host.Scene, body);
                    if (s.IsNull || !host.Scene.IsLive(s)) continue;
                    firstLiveFrame = f;
                    var sp = host.Scene.Paint(s);
                    var wrapper = host.Scene.Parent(s);
                    var wr = wrapper.IsNull ? default : host.Scene.AbsoluteRect(wrapper);
                    sawX = wr.X; sawY = wr.Y; sawOp = sp.Opacity; sawDy = sp.LocalTransform.Dy;
                    firstPlacedSeeded = !wrapper.IsNull && Near(wr.X, expX, 1f) && Near(wr.Y, expY, 1f)
                        && sp.Opacity < 0.01f && Near(sp.LocalTransform.Dy, -50f, 1f);
                }
                svc.CloseTop();
                Settle();
                Check("gate.overlay.flyout-first-frame — a regular Flyout is placed + enter-seeded (opacity 0, TranslateY −50) on the FIRST presented frame; no unplaced/unseeded flash",
                    firstLiveFrame >= 0 && firstPlacedSeeded,
                    $"firstLiveFrame={firstLiveFrame} wrapper=({sawX:0.0},{sawY:0.0}) exp=({expX:0.0},{expY:0.0}) op={sawOp:0.00} dy={sawDy:0.0}");
            }

            // gate.overlay.flyout-open-curve — the PopupThemeTransition entrance picks its slide AXIS + SIGN from the
            // EFFECTIVE placement major side (FlyoutBase::SetTransitionParameters, FlyoutBase_Partial.cpp:2028-2051) applied
            // to g_entranceThemeOffset = 50 (cpp:68): Left → TranslateX +50, Right → TranslateX −50, Full → NO slide (fade
            // only, FromH=FromV=0), Top/Bottom → TranslateY (covered by cp7.f). All ride cubic-bezier(.1,.9,.2,1) over
            // 367ms, with the delayed 83ms opacity fade. A synthetic 40×40 anchor at (300,180) leaves room for BOTH Left
            // (popup to its left) and Right to place UNFLIPPED, so the effective placement equals the requested one.
            {
                // Spin frames at dt=0 (clock frozen → the anim stays at its enter initial) until the surface mounts, then
                // read its LOCAL transform + opacity. A getter (not a stale by-value handle) is required: OnRealized only
                // fires DURING these frames. Rect-anchored OpenAt can mount a frame later than a node-anchored Open.
                (float dx, float dy, float op) Seed0(Func<NodeHandle> bodyGetter)
                {
                    NodeHandle sn = NodeHandle.Null;
                    for (int i = 0; i < 6 && sn.IsNull; i++) { host.RunFrame(); sn = SurfaceOf(host.Scene, bodyGetter()); }
                    if (sn.IsNull) return (0f, 0f, -1f);
                    var p = host.Scene.Paint(sn);
                    return (p.LocalTransform.Dx, p.LocalTransform.Dy, p.Opacity);
                }

                // Left → effective Left → TranslateX +50, no Y slide, opacity 0 at t=0; decays toward 0 as it fades.
                NodeHandle bodyL = NodeHandle.Null;
                svc.OpenAt(() => new RectF(300f, 180f, 40f, 40f),
                    () => new BoxEl { Width = 120f, Height = 80f, Fill = Tok.FillCardDefault, OnRealized = h2 => bodyL = h2 },
                    FlyoutPlacement.Left, new PopupOptions(Chrome: PopupChrome.Popup));
                var (lx, ly, lop) = Seed0(() => bodyL);
                bool leftOk = Near(lx, 50f, 1f) && Near(ly, 0f, 0.5f) && lop < 0.01f;
                clock.Advance(120f); host.RunFrame();
                var sL = SurfaceOf(host.Scene, bodyL);
                var pL = sL.IsNull ? default : host.Scene.Paint(sL);
                bool leftDecays = !sL.IsNull && pL.LocalTransform.Dx < 49f && pL.LocalTransform.Dx >= 0f && pL.Opacity > 0.05f;
                svc.CloseTop(); Settle();

                // Right → effective Right → TranslateX −50.
                NodeHandle bodyR = NodeHandle.Null;
                svc.OpenAt(() => new RectF(300f, 180f, 40f, 40f),
                    () => new BoxEl { Width = 120f, Height = 80f, Fill = Tok.FillCardDefault, OnRealized = h2 => bodyR = h2 },
                    FlyoutPlacement.Right, new PopupOptions(Chrome: PopupChrome.Popup));
                var (rx, ry, rop) = Seed0(() => bodyR);
                bool rightOk = Near(rx, -50f, 1f) && Near(ry, 0f, 0.5f) && rop < 0.01f;
                svc.CloseTop(); Settle();

                // Full → NO slide (fade only): Dx=Dy=0 at t=0, opacity 0 → rises with no translation on either axis.
                NodeHandle bodyF = NodeHandle.Null;
                svc.OpenAt(() => new RectF(300f, 180f, 40f, 40f),
                    () => new BoxEl { Width = 120f, Height = 80f, Fill = Tok.FillCardDefault, OnRealized = h2 => bodyF = h2 },
                    FlyoutPlacement.Full, new PopupOptions(Chrome: PopupChrome.Popup));
                var (fx, fy, fop) = Seed0(() => bodyF);
                bool full0 = Near(fx, 0f, 0.5f) && Near(fy, 0f, 0.5f) && fop < 0.01f;
                clock.Advance(120f); host.RunFrame();
                var sF = SurfaceOf(host.Scene, bodyF);
                var pF = sF.IsNull ? default : host.Scene.Paint(sF);
                bool fullFadeNoSlide = !sF.IsNull && Near(pF.LocalTransform.Dx, 0f, 0.5f) && Near(pF.LocalTransform.Dy, 0f, 0.5f) && pF.Opacity > 0.05f;
                svc.CloseTop(); Settle();

                Check("gate.overlay.flyout-open-curve — PopupThemeTransition axis: Left→TranslateX +50, Right→TranslateX −50, Full→fade-only; 367ms FluentDecelerate + delayed opacity",
                    leftOk && leftDecays && rightOk && full0 && fullFadeNoSlide,
                    $"left=({lx:0.0},{ly:0.0},op{lop:0.00}) leftDecays={leftDecays} right=({rx:0.0},{ry:0.0},op{rop:0.00}) full0=({fx:0.0},{fy:0.0},op{fop:0.00}) fullFadeNoSlide={fullFadeNoSlide}");
            }

            // cp7.g — menu close mid-open: 83ms linear fade with the clip, content translate AND plate scale frozen at
            // the interrupt offset; the entry finalizes once the fade settles. The downward menu's reveal animates the
            // TOP clip edge (ClipRect.Y) + content TranslateY, so BOTH are held fixed through the fade (along with ClipB).
            {
                svc.Open(() => root.Anchor,
                    () => MenuFlyout.Create(new[] { new MenuFlyoutItem("One"), new MenuFlyoutItem("Two") }, () => svc.CloseTop()),
                    FlyoutPlacement.BottomLeft);
                host.RunFrame();
                host.RunFrame();                       // t=0
                clock.Advance(64f); host.RunFrame();   // mid-open
                var s = SurfaceOf(host.Scene, FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
                var plate = s.IsNull ? NodeHandle.Null : host.Scene.FirstChild(s);
                svc.CloseTop(); host.RunFrame();       // freeze (cancel) the load tracks + seed the 83ms fade
                var f0 = s.IsNull ? default : host.Scene.Paint(s);
                float frozenY = f0.ClipRect.IsInfinite ? -1f : f0.ClipRect.Y;
                float frozenB = f0.ClipRect.IsInfinite ? -1f : f0.ClipRect.Bottom;
                float frozenDy = f0.LocalTransform.Dy;
                float frozenScale = plate.IsNull ? -1f : host.Scene.Paint(plate).LocalTransform.M22;
                clock.Advance(32f); host.RunFrame();   // 32ms into the fade → opacity ≈ 1−32/83 = 0.614
                var f1 = s.IsNull ? default : host.Scene.Paint(s);
                bool frozen = !s.IsNull && frozenB > 1f && frozenY > 0.5f && frozenDy < -0.5f && !f1.ClipRect.IsInfinite
                    && Near(f1.ClipRect.Bottom, frozenB, 0.01f) && Near(f1.ClipRect.Y, frozenY, 0.01f)
                    && Near(f1.LocalTransform.Dy, frozenDy, 0.01f)
                    && !plate.IsNull && frozenScale > 0.5f && frozenScale < 1f
                    && Near(host.Scene.Paint(plate).LocalTransform.M22, frozenScale, 0.001f);
                bool fading = !s.IsNull && Near(f1.Opacity, 1f - 32f / 83f, 0.03f);
                clock.Advance(64f); host.RunFrame();   // 96ms > 83 → fade settled
                for (int i = 0; i < 6; i++) { clock.Advance(16f); host.RunFrame(); }
                bool finalized = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;
                Check("cp7.g — menu close: 83ms linear fade with clip (frozen ClipT/ClipB) + content translate + plate scale frozen; finalized after settle",
                    frozen && fading && finalized,
                    $"frozenY={frozenY:0.0} frozenB={frozenB:0.0} frozenDy={frozenDy:0.0} clipYNow={(f1.ClipRect.IsInfinite ? -1f : f1.ClipRect.Y):0.0} plate={frozenScale:0.000} op32={f1.Opacity:0.000}≈{1f - 32f / 83f:0.000} finalized={finalized}");
            }
        }
    }

    static void MediaPlayerElementChecks(StringTable strings)
    {
        // A headless player driven to steady Playing at position 0 (audio-only unless a video size is supplied).
        static HeadlessScriptedPlayer PlayingPlayer(SizeI? video = null)
        {
            var p = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0, DefaultDuration = TimeSpan.FromSeconds(120) };
            p.OpenAsync(MediaSource.FromSamples(new ScriptedSampleSource(TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(20), video)))
                .GetAwaiter().GetResult();
            p.PlayAsync().GetAwaiter().GetResult();
            return p;
        }

        // gate.media.el.no-frameclock-rerender + gate.media.el.pure-render: scripted position advance may re-anchor the
        // isolated seek-bar leaf, but it must NOT create a permanent native-video pump. The video pump left Render and
        // now runs only for explicit source/geometry requests.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5g-mpe", new Size2(480, 320), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var player = PlayingPlayer();
            var root = new FluentGpu.Controls.Media.MediaPlayerElement { Player = player };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();                                                 // mount
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();       // Opening → Buffering
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();       // Buffering → Playing
            host.Paint(0); host.Paint(0);                                     // settle
            bool playing = player.State.Peek() == PlaybackState.Playing;

            long pump0 = host.VideoSurfaces.PumpInvocationCount;
            int renders = 0; bool anyRendered = false;
            const int N = 8;
            for (int i = 0; i < N; i++)
            {
                player.Pump(TimeSpan.FromMilliseconds(40));                   // advance position, sub-second (≤0.32s)
                var fs = host.Paint(0);
                renders += fs.ComponentsRendered;
                anyRendered |= fs.Rendered;
            }
            long pumpDelta = host.VideoSurfaces.PumpInvocationCount - pump0;
            float posAdvanced = player.PositionSeconds.Peek();

            Check("gate.media.el.no-frameclock-rerender", playing && posAdvanced > 0.1f,
                $"playing={playing} renders={renders} anyRendered={anyRendered} pos={posAdvanced:0.###}");
            Check("gate.media.el.pure-render", pumpDelta == 0,
                $"pumpDelta={pumpDelta} frames={N} renders={renders}");
        }

        // gate.media.el.ownership-transfer: single-writer contract on the registry — only the current owner's pump runs;
        // a non-owner pump is a counted no-op; transfer + transfer-back flip which pump drives, restoring the original.
        {
            var reg = new VideoSurfaceRegistry();
            int token = reg.Acquire();
            var a = new object(); var b = new object();
            int aRuns = 0, bRuns = 0;
            int ra = reg.RegisterPump(token, a, _ => aRuns++);               // first registrant → initial owner
            int rb = reg.RegisterPump(token, b, _ => bRuns++);
            long supp0 = reg.SuppressedNonOwnerPumpCount;

            reg.PumpPending(1f);                                              // a owns the initial requested pump
            bool aDrivesFirst = aRuns == 1 && bRuns == 0;
            bool bSuppressed = reg.SuppressedNonOwnerPumpCount == supp0 + 1;
            reg.PumpPending(1f);                                              // no request => no permanent host-frame pump
            bool noRepeatWithoutRequest = aRuns == 1 && bRuns == 0;

            reg.TransferOwnership(token, b);
            reg.PumpPending(1f);                                              // b owns now; a is a no-op
            bool bDrivesAfter = bRuns == 1 && aRuns == 1;
            bool aSuppressed = reg.SuppressedNonOwnerPumpCount == supp0 + 2;

            reg.TransferOwnership(token, a);
            reg.PumpPending(1f);                                              // transferred back
            bool aRestored = aRuns == 2 && bRuns == 1;

            reg.UnregisterPump(ra); reg.UnregisterPump(rb);
            Check("gate.media.el.ownership-transfer",
                token > 0 && ra > 0 && rb > 0 && aDrivesFirst && bSuppressed && noRepeatWithoutRequest
                    && bDrivesAfter && aSuppressed && aRestored,
                $"aFirst={aDrivesFirst} bSupp={bSuppressed} noRepeat={noRepeatWithoutRequest} bDrives={bDrivesAfter} aSupp={aSuppressed} restored={aRestored}");
        }

        // gate.media.el.pins-anchor-autohide: while a picker (an anchored PinsAnchor overlay inside the player) is open,
        // the idle-hide timeout does NOT collapse the chrome; after it closes, the timeout collapses it.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5g-mpe-pin", new Size2(560, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var player = PlayingPlayer();
            var probe = new MediaPlayerHostProbe { Player = player, HideMs = 200f };
            using var host = new AppHost(app, window, device, fonts, strings, probe);
            host.RunFrame();
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();
            host.Paint(0);
            var svc = probe.Service!;

            int Buttons() => Roles(host.Scene, AutomationRole.Button).Count;
            bool chromeShownAtPlaying = Buttons() > 0;

            // Open a picker anchored to a transport button (a node INSIDE the player subtree) → PinsAnchor default true.
            var anchorBtn = Roles(host.Scene, AutomationRole.Button)[0];
            Func<Element> body = () => new BoxEl { Width = 120, Height = 80, Fill = Tok.FillCardDefault, Children = [Ui.Text("picker")] };
            var pick = svc.Open(() => anchorBtn, body, FlyoutPlacement.TopEdgeAlignedRight);
            host.RunFrame();
            for (int i = 0; i < 30; i++) host.Paint(0);                       // ~480ms ≫ 200ms hide delay
            bool heldWhilePinned = Buttons() > 0;                            // chrome NOT collapsed while the picker is open

            pick.Close();
            for (int i = 0; i < 34; i++) host.Paint(0);                       // close settles + re-armed timer fires (>200ms)
            bool collapsedAfterClose = Buttons() == 0;

            Check("gate.media.el.pins-anchor-autohide", chromeShownAtPlaying && heldWhilePinned && collapsedAfterClose,
                $"shown={chromeShownAtPlaying} heldPinned={heldWhilePinned} collapsedAfterClose={collapsedAfterClose}");
        }

        // gate.media.el.controlled-aspect: an external Signal<VideoAspectMode> drives the fitted video rect — and since
        // the restructure that rect IS the hole node's laid-out rect (one source of truth), so the aspect policy is
        // asserted on the hole itself: pillarboxed under Uniform (narrower than the stage, full height), covering the
        // stage under UniformToFill (the crop overflows and is clipped). The opaque letterbox stage fill is ONE element
        // in every mode (the four bar elements are gone). The control also works standalone (auto-materialized signal).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5g-mpe-aspect", new Size2(520, 340), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var player = PlayingPlayer(new SizeI(100, 100));                  // square video → pillarbox under Uniform in a wide area
            var ext = new Signal<VideoAspectMode>(VideoAspectMode.Uniform);
            var root = new FluentGpu.Controls.Media.MediaPlayerElement { Player = player, AspectMode = ext };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();
            for (int i = 0; i < 5; i++) host.Paint(0);                        // settle layout + areaBounds → letterbox computed

            // Pillarboxed = the hole is narrower than the stage but full height; covered = the hole spans the stage on
            // both axes (a UniformToFill crop deliberately overflows it and is clipped by the stage's ClipToBounds).
            static bool Pillarboxed(AppHost h)
            {
                var n = FindVisual(h.Scene, h.Scene.Root, VisualKind.Video);
                if (n.IsNull) return false;
                RectF hole = h.Scene.AbsoluteRect(n), stage = h.Scene.AbsoluteRect(h.Scene.Parent(n));
                return stage.W > 0f && hole.W > 0f && hole.W < stage.W - 1f && Near(hole.H, stage.H);
            }
            static bool Covered(AppHost h)
            {
                var n = FindVisual(h.Scene, h.Scene.Root, VisualKind.Video);
                if (n.IsNull) return false;
                RectF hole = h.Scene.AbsoluteRect(n), stage = h.Scene.AbsoluteRect(h.Scene.Parent(n));
                return stage.W > 0f && hole.X <= stage.X + 0.01f && hole.Y <= stage.Y + 0.01f
                    && hole.X + hole.W >= stage.X + stage.W - 0.01f && hole.Y + hole.H >= stage.Y + stage.H - 0.01f;
            }

            bool pillarUniform = Pillarboxed(host);
            int fillsUniform = CountFill(host.Scene, host.Scene.Root, Tok.MediaLetterbox);
            ext.Value = VideoAspectMode.UniformToFill;
            for (int i = 0; i < 3; i++) host.Paint(0);
            bool coveredCrop = Covered(host) && !Pillarboxed(host);
            int fillsCrop = CountFill(host.Scene, host.Scene.Root, Tok.MediaLetterbox);
            ext.Value = VideoAspectMode.Uniform;
            for (int i = 0; i < 3; i++) host.Paint(0);
            bool pillarUniform2 = Pillarboxed(host);

            // Standalone (auto-materialized: no AspectMode passed) still renders letterbox under the default Uniform.
            var player2 = PlayingPlayer(new SizeI(100, 100));
            var root2 = new FluentGpu.Controls.Media.MediaPlayerElement { Player = player2 };
            using var host2 = new AppHost(new HeadlessPlatformApp(),
                new HeadlessWindow(new WindowDesc("g5g-mpe-auto", new Size2(520, 340), 1f)),
                new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root2);
            host2.RunFrame();
            player2.Pump(TimeSpan.FromMilliseconds(1)); host2.RunFrame();
            player2.Pump(TimeSpan.FromMilliseconds(1)); host2.RunFrame();
            for (int i = 0; i < 5; i++) host2.Paint(0);
            bool pillarAuto = Pillarboxed(host2);
            int fillsAuto = CountFill(host2.Scene, host2.Scene.Root, Tok.MediaLetterbox);

            Check("gate.media.el.controlled-aspect",
                pillarUniform && coveredCrop && pillarUniform2 && pillarAuto
                    && fillsUniform == 1 && fillsCrop == 1 && fillsAuto == 1,
                $"uniformPillarboxed={pillarUniform} cropCovers={coveredCrop} uniform2={pillarUniform2} "
                + $"autoMaterialized={pillarAuto} stageFills={fillsUniform}/{fillsCrop}/{fillsAuto} (want 1 each)");
        }

        // gate.media.el.tokens: the media element carries ZERO hardcoded FromRgba color literals — every ink/scrim/stage
        // reads a Tok.* media token (source-scan of the control's source, located via the compile-time repo path).
        {
            string? src = ReadRepoFile("src/FluentGpu.Controls/Media/MediaPlayerElement.cs");
            bool found = src is not null;
            int fromRgba = src is null ? -1 : CountOccurrences(src, "FromRgba");
            bool usesTokens = src is not null && src.Contains("Tok.ScrimBottom") && src.Contains("Tok.OnMediaPrimary")
                && src.Contains("Tok.MediaStage") && src.Contains("Tok.MediaLetterbox");
            Check("gate.media.el.tokens", found && fromRgba == 0 && usesTokens,
                $"found={found} FromRgba={fromRgba} usesTokens={usesTokens}");
        }

        // gate.media.el.decorative-inactive-skips-pump / gate.media.el.player-inactive-still-pumps: parked decorative
        // clips (WatchFeed) must SetVisible(false) AND return before PumpVideo; non-decorative player surfaces hide but
        // keep pumping so MF can publish NaturalSize/duration. Source-scan of the PumpNow contract (UseIsActive in a
        // headless host is a heavier setup; the branch shape is the load-bearing invariant).
        {
            string? src = ReadRepoFile("src/FluentGpu.Controls/Media/MediaPlayerElement.cs");
            bool found = src is not null;
            bool decorativeSkip = src is not null
                && src.Contains("if (IsDecorative) return;", StringComparison.Ordinal)
                && src.Contains("SetVisible(false)", StringComparison.Ordinal)
                && src.Contains("PumpVideo", StringComparison.Ordinal);
            // Non-decorative must reach PumpVideo after the inactive hide (the return is gated on IsDecorative only).
            bool playerStillPumps = src is not null
                && src.Contains("Non-decorative player surfaces", StringComparison.Ordinal);
            Check("gate.media.el.decorative-inactive-skips-pump", found && decorativeSkip,
                $"found={found} decorativeSkip={decorativeSkip}");
            Check("gate.media.el.player-inactive-still-pumps", found && playerStillPumps,
                $"found={found} playerStillPumps={playerStillPumps}");
            // Remount / generation-swap frames can briefly report a non-live or 0×0 area — non-decorative must still
            // PumpVideo so MF can publish duration/NaturalSize (video→video successor stuck at Opening otherwise).
            bool zeroAreaStillPumps = src is not null
                && src.Contains("even before the area is laid out", StringComparison.Ordinal)
                && src.Contains("if (!IsDecorative) Player.PumpVideo(b, default, s);", StringComparison.Ordinal);
            Check("gate.media.el.zero-area-still-pumps", found && zeroAreaStillPumps,
                $"found={found} zeroAreaStillPumps={zeroAreaStillPumps}");
        }

        // gate.media.el.video-knockout: ONE SOURCE OF TRUTH for the video rect. A PLAYING video player paints an OPAQUE
        // LetterboxColor stage fill across the WHOLE video area FIRST, then punches EXACTLY ONE hole at the FITTED video
        // rect — and PumpNow places the DComp visual from THAT node's rect, so the erased region and the composited
        // video can never disagree. (The old shape — a full-area hole plus letterbox bars painted after it, presenter
        // placed at an independently recomputed fit — left sub-pixel slivers at fractional device scale where the hole
        // was punched but neither the video visual nor a bar covered the pixel: erased-to-zero ⇒ grey Mica showed.)
        // Asserted: one hole, the live registry slot token, full erase strength, the hole's recorded device rect ==
        // the expected fit for this area+natural size, the opaque fill FIRST in painter order (first child of the stage)
        // covering the WHOLE area, and the NO-PIXEL-GAP invariant hole ⊆ fill (every erased pixel lands on opaque
        // letterbox, never on the backdrop). The transport chrome's glyph runs record AFTER the hole, so their survival
        // is the no-truncation proof (a mis-strided DrawVideo payload would shred every command after it).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5g-mpe-hole", new Size2(520, 340), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var natural = new SizeI(100, 100);                              // square video in a wide area → pillarboxed
            var player = PlayingPlayer(natural);
            var root = new FluentGpu.Controls.Media.MediaPlayerElement { Player = player };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();     // Opening → Buffering
            player.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();     // Buffering → Playing
            for (int i = 0; i < 5; i++) host.Paint(0);                      // settle layout + areaBounds → fit computed

            int holes = device.LastVideos.Count;
            int surfaceId = holes > 0 ? device.LastVideos[0].SurfaceId : -1;
            float ready = holes > 0 ? device.LastVideos[0].VideoReady : -1f;
            // Dst is the node-LOCAL (0,0,w,h) box; the world transform places it. Recombine into the device rect.
            RectF dst = holes > 0 ? device.LastVideos[0].Dst : default;
            Affine2D xf = holes > 0 ? device.LastVideos[0].Transform : default;
            var recordedHole = new RectF(xf.Dx + dst.X, xf.Dy + dst.Y, dst.W, dst.H);

            // Structural painter order: stage → [0] opaque letterbox fill, [1] the hole.
            var holeNode = FindVisual(host.Scene, host.Scene.Root, VisualKind.Video);
            var stage = holeNode.IsNull ? NodeHandle.Null : host.Scene.Parent(holeNode);
            var fillNode = stage.IsNull ? NodeHandle.Null : host.Scene.FirstChild(stage);
            ColorF fillColor = fillNode.IsNull ? default : host.Scene.Paint(fillNode).Fill;
            bool fillIsFirstAndOpaque = !fillNode.IsNull && fillNode.Raw.Index != holeNode.Raw.Index
                && ColorApprox(fillColor, Tok.MediaLetterbox) && fillColor.A == 1f;
            RectF stageRect = stage.IsNull ? default : host.Scene.AbsoluteRect(stage);
            RectF fillRect = fillNode.IsNull ? default : host.Scene.AbsoluteRect(fillNode);
            bool fillCoversArea = stageRect.W > 0f && Near(fillRect.X, stageRect.X) && Near(fillRect.Y, stageRect.Y)
                && Near(fillRect.W, stageRect.W) && Near(fillRect.H, stageRect.H);

            RectF expected = FluentGpu.Controls.Media.MediaPlayerElement.FitVideoRect(
                stageRect, natural, VideoAspectMode.Uniform, 16.0 / 9.0);
            bool fitted = holes == 1 && Near(recordedHole.X, expected.X) && Near(recordedHole.Y, expected.Y)
                && Near(recordedHole.W, expected.W) && Near(recordedHole.H, expected.H)
                // the scene node the pump reads and the rect the recorder erased are the SAME rect
                && !holeNode.IsNull && Near(host.Scene.AbsoluteRect(holeNode).X, recordedHole.X)
                && Near(host.Scene.AbsoluteRect(holeNode).W, recordedHole.W);
            // NO PIXEL GAP: hole ⊆ fill (strictly inside or equal) ⇒ no erased pixel can miss the opaque letterbox.
            bool holeInsideFill = holes == 1 && fillRect.W > 0f
                && recordedHole.X >= fillRect.X - 0.01f && recordedHole.Y >= fillRect.Y - 0.01f
                && recordedHole.X + recordedHole.W <= fillRect.X + fillRect.W + 0.01f
                && recordedHole.Y + recordedHole.H <= fillRect.Y + fillRect.H + 0.01f;
            int letterboxFills = 0;
            foreach (var r in device.LastRects) if (ColorApprox(r.Fill, Tok.MediaLetterbox)) letterboxFills++;
            bool notTruncated = device.LastGlyphs.Count > 0;                // transport chrome records AFTER the hole

            Check("gate.media.el.video-knockout",
                holes == 1 && surfaceId > 0 && ready == 1f && fillIsFirstAndOpaque && fillCoversArea && letterboxFills >= 1
                    && fitted && holeInsideFill && notTruncated && device.ClipBalance == 0,
                $"holes={holes} surfaceId={surfaceId} ready={ready:0.###} fillFirst={fillIsFirstAndOpaque} "
                + $"fillCoversArea={fillCoversArea} letterboxFills={letterboxFills} "
                + $"hole=({recordedHole.X:0.##},{recordedHole.Y:0.##},{recordedHole.W:0.##},{recordedHole.H:0.##}) "
                + $"want=({expected.X:0.##},{expected.Y:0.##},{expected.W:0.##},{expected.H:0.##}) "
                + $"insideFill={holeInsideFill} notTruncated={notTruncated} clipBalance={device.ClipBalance}");
        }

        // gate.media.el.video-knockout-poster: the hole is gated EXACTLY on the existing videoReady branch. Audio-only
        // playback (no video stream) and a video source still held pre-ready (Opening, poster/spinner up) both record
        // ZERO holes — punching one early would knock a transparent rectangle through to the desktop/Mica.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5g-mpe-hole-audio", new Size2(520, 340), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var audio = PlayingPlayer();                                    // audio-only → NaturalSize empty
            var root = new FluentGpu.Controls.Media.MediaPlayerElement { Player = audio };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            audio.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();
            audio.Pump(TimeSpan.FromMilliseconds(1)); host.RunFrame();
            for (int i = 0; i < 3; i++) host.Paint(0);
            int audioHoles = device.LastVideos.Count;
            bool audioPlaying = audio.State.Peek() == PlaybackState.Playing;
            bool audioFrameRecorded = device.LastRects.Count > 0;           // the frame really WAS recorded (0 is meaningful)

            // A VIDEO source held in Opening: never pumped, so the state machine cannot leave Opening.
            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("g5g-mpe-hole-open", new Size2(520, 340), 1f));
            window2.Show();
            var device2 = new HeadlessGpuDevice();
            var pending = new HeadlessScriptedPlayer { OpenTicks = 64, BufferTicks = 64, DefaultDuration = TimeSpan.FromSeconds(120) };
            pending.OpenAsync(MediaSource.FromSamples(new ScriptedSampleSource(
                TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(20), new SizeI(1920, 1080)))).GetAwaiter().GetResult();
            pending.PlayAsync().GetAwaiter().GetResult();
            var root2 = new FluentGpu.Controls.Media.MediaPlayerElement { Player = pending };
            using var host2 = new AppHost(app2, window2, device2, new HeadlessFontSystem(strings), strings, root2);
            host2.RunFrame();
            for (int i = 0; i < 3; i++) host2.Paint(0);
            int openingHoles = device2.LastVideos.Count;
            bool stillOpening = pending.State.Peek() == PlaybackState.Opening;
            bool openingFrameRecorded = device2.LastRects.Count > 0;

            Check("gate.media.el.video-knockout-poster",
                audioHoles == 0 && openingHoles == 0 && audioPlaying && stillOpening
                    && audioFrameRecorded && openingFrameRecorded,
                $"audioOnlyHoles={audioHoles} (playing={audioPlaying}) openingHoles={openingHoles} (state={pending.State.Peek()}) "
                + $"framesRecorded={audioFrameRecorded}/{openingFrameRecorded}");
        }
    }

    /// <summary>First node (pre-order) whose paint records <paramref name="kind"/> — e.g. the single VisualKind.Video
    /// hole-punch node inside a media player's video stage.</summary>
    static NodeHandle FindVisual(SceneStore s, NodeHandle n, VisualKind kind)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).VisualKind == kind) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindVisual(s, c, kind);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    // ── DrawOp.DrawVideo — the video hole punch (gpu-renderer.md §7.3) ───────────────────────────────────────────────
    // The op ERASES the already-painted UI pixels under its rect toward premultiplied zero so the DComp video visual
    // composited z-BELOW the swapchain shows through. These gates pin the three seams the primitive rides: the POD
    // stream (payload stride + byte-exact read-back), the clean-span translate fast path, and the recorder→RHI decode.
    static void VideoHoleChecks(StringTable strings)
    {
        // gate.video.op.roundtrip — a hole recorded BETWEEN two fills round-trips byte-exactly and does NOT desync the
        // walker: DrawPayloadSize(DrawVideo) must equal the writer's payload stride, or every op after the hole shreds.
        {
            var dl = new DrawList();
            var xfA = new Affine2D(1f, 0f, 0f, 1f, 10f, 20f);
            var xfHole = new Affine2D(1f, 0f, 0f, 1f, 12.5f, 34.25f);
            var xfB = new Affine2D(1f, 0f, 0f, 1f, 70f, 80f);
            var holeDst = new RectF(4f, 6f, 320f, 180f);
            var holeRadii = new CornerRadius4(8f, 9f, 10f, 11f);
            dl.FillRoundRect(new RectF(0f, 0f, 40f, 20f), CornerRadius4.All(2f), ColorF.FromRgba(0x11, 0x22, 0x33), xfA, 1f, 1UL);
            dl.DrawVideo(holeDst, holeRadii, 7, 1f, xfHole, 0.75f, 2UL);
            dl.FillRoundRect(new RectF(0f, 0f, 10f, 10f), default, ColorF.FromRgba(0x44, 0x55, 0x66), xfB, 1f, 3UL);

            var bytes = dl.Bytes;
            int pos = 0, ops = 0, videoAt = -1, fillsAfterHole = 0;
            DrawVideoCmd hole = default;
            FillRoundRectCmd tailFill = default;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos));
                pos += sizeof(int);
                if (op == DrawOp.DrawVideo) { videoAt = ops; hole = MemoryMarshal.Read<DrawVideoCmd>(bytes.Slice(pos)); }
                else if (op == DrawOp.FillRoundRect && videoAt >= 0)
                {
                    fillsAfterHole++;
                    tailFill = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos));
                }
                pos += DrawPayloadSize(op);
                ops++;
            }
            // Landing exactly on the end (not merely "no overrun") is the stride proof; the trailing fill decoding to its
            // authored transform proves the walk stayed phase-locked ACROSS the hole.
            bool walked = pos == bytes.Length && ops == 3 && videoAt == 1 && fillsAfterHole == 1
                && tailFill.Transform.Dx == 70f && tailFill.Transform.Dy == 80f;
            bool payload = hole.Dst == holeDst && hole.Radii == holeRadii && hole.SurfaceId == 7
                && hole.VideoReady == 1f && hole.Opacity == 0.75f
                && hole.Transform.Dx == 12.5f && hole.Transform.Dy == 34.25f;
            var st = dl.OpcodeStats;
            bool stats = st.DrawVideo == 1 && st.FillRoundRect == 2 && dl.CommandCount == 3 && dl.SortKeys.Length == 3;

            // Erase strength is a blend COVERAGE — the writer clamps it into 0..1 whatever the caller passes.
            var dlc = new DrawList();
            dlc.DrawVideo(holeDst, default, 3, 2.5f, xfHole, 1f);
            dlc.DrawVideo(holeDst, default, 3, -1f, xfHole, 1f);
            var cHi = MemoryMarshal.Read<DrawVideoCmd>(dlc.Bytes.Slice(sizeof(int)));
            var cLo = MemoryMarshal.Read<DrawVideoCmd>(dlc.Bytes.Slice(sizeof(int) * 2 + Unsafe.SizeOf<DrawVideoCmd>()));
            bool clamped = cHi.VideoReady == 1f && cLo.VideoReady == 0f;

            Check("gate.video.op.roundtrip", walked && payload && stats && clamped,
                $"ops={ops} videoAt={videoAt} fillsAfter={fillsAfterHole} walked={pos}/{bytes.Length} payload={payload} "
                + $"stats={st.DrawVideo}/{st.FillRoundRect} cmds={dl.CommandCount} clamp={clamped}");
        }

        // gate.video.op.translate — the hole PARTICIPATES in clean-span reuse: a copied span containing it translates
        // (Dx/Dy rebased, exactly like FillRoundRect — pure geometry, and the presenter drives the video visual's own
        // rect independently, so a rebased span cannot desync). Control: a span carrying an ACRYLIC layer still refuses
        // and rolls back (the one position-DEPENDENT payload — its recipe blurs whatever the canvas holds under the
        // layer rect). Glyph/clip/non-acrylic-layer spans DO translate now; gate.span.textRowScrollRebase owns that.
        {
            var dl = new DrawList();
            var xf = new Affine2D(1f, 0f, 0f, 1f, 40f, 60f);
            var dst = new RectF(0f, 0f, 200f, 120f);
            var radii = CornerRadius4.All(6f);
            dl.FillRoundRect(dst, radii, ColorF.FromRgba(0x20, 0x20, 0x20), xf, 1f, 1UL);
            dl.DrawVideo(dst, radii, 5, 1f, xf, 1f, 2UL);
            int byteLen = dl.BytePosition, sortLen = dl.SortPosition, cmds = dl.CommandCount;
            var stats = dl.OpcodeStats;

            const float dx = -17.5f, dy = 23.25f;
            dl.SwapAndReset();
            bool copied = dl.CopySpanFromPriorTranslated(0, byteLen, 0, sortLen, cmds, in stats, dx, dy);

            var outBytes = dl.Bytes;
            int p = 0, seen = 0;
            DrawVideoCmd movedHole = default;
            FillRoundRectCmd movedFill = default;
            while (p + sizeof(int) <= outBytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(outBytes.Slice(p));
                p += sizeof(int);
                if (op == DrawOp.DrawVideo) movedHole = MemoryMarshal.Read<DrawVideoCmd>(outBytes.Slice(p));
                else if (op == DrawOp.FillRoundRect) movedFill = MemoryMarshal.Read<FillRoundRectCmd>(outBytes.Slice(p));
                p += DrawPayloadSize(op);
                seen++;
            }
            bool rebased = copied && seen == 2 && p == outBytes.Length
                && movedHole.Transform.Dx == 40f + dx && movedHole.Transform.Dy == 60f + dy
                && movedFill.Transform.Dx == 40f + dx && movedFill.Transform.Dy == 60f + dy
                // everything except the transform survives the memcpy untouched
                && movedHole.Dst == dst && movedHole.Radii == radii && movedHole.SurfaceId == 5 && movedHole.VideoReady == 1f
                && dl.CommandCount == 2 && dl.OpcodeStats.DrawVideo == 1;

            // Control: an ACRYLIC layer's pixels depend on WHERE it sits (it blurs the canvas beneath DeviceRect), so its
            // span must refuse and leave the destination list untouched (no half-written span — the rollback).
            var dg = new DrawList();
            dg.FillRoundRect(dst, radii, ColorF.FromRgba(0x20, 0x20, 0x20), xf, 1f, 1UL);
            dg.PushLayer(dst, radii, ColorF.FromRgba(0x30, 0x30, 0x30), ColorF.FromRgba(0x20, 0x20, 0x20), 0.6f, 30f, 0.02f, 0.8f, 2UL);
            dg.PopLayer(dst, 3UL);
            int gBytes = dg.BytePosition, gSort = dg.SortPosition, gCmds = dg.CommandCount;
            var gStats = dg.OpcodeStats;
            dg.SwapAndReset();
            bool refused = !dg.CopySpanFromPriorTranslated(0, gBytes, 0, gSort, gCmds, in gStats, dx, dy)
                && dg.BytePosition == 0 && dg.CommandCount == 0;

            Check("gate.video.op.translate", copied && rebased && refused,
                $"copied={copied} ops={seen} dx={movedHole.Transform.Dx:0.##} dy={movedHole.Transform.Dy:0.##} "
                + $"(want {40f + dx:0.##}/{60f + dy:0.##}) rebased={rebased} acrylicRefused={refused}");
        }

        // gate.video.op.headless — the full reconcile → record → RHI-decode path: a BoxEl{VideoHole} inside a rounded
        // ClipToBounds container over a translucent page emits ONE DrawVideo, INSIDE the container's clip (that tier-2
        // rounded clip is where a PiP hole's corner rounding actually comes from), and the frame is NOT truncated —
        // the marker rect recorded AFTER the hole survives, and the clip stack still balances.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("video-hole", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new VideoHoleProbe { SurfaceId = 7 };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();

            int holes = device.LastVideos.Count;
            int surfaceId = holes > 0 ? device.LastVideos[0].SurfaceId : -1;
            float ready = holes > 0 ? device.LastVideos[0].VideoReady : -1f;
            int clipDepth = device.LastVideoClipDepths.Count > 0 ? device.LastVideoClipDepths[0] : -1;
            bool page = false, marker = false;
            foreach (var r in device.LastRects)
            {
                if (ColorApprox(r.Fill, VideoHoleProbe.PageFill)) page = true;
                if (ColorApprox(r.Fill, VideoHoleProbe.MarkerFill)) marker = true;
            }
            RectF holeDst = holes > 0 ? device.LastVideos[0].Dst : default;
            bool sized = holes > 0 && Near(holeDst.W, 320f) && Near(holeDst.H, 180f);

            Check("gate.video.op.headless",
                holes == 1 && surfaceId == 7 && ready == 1f && clipDepth >= 1 && page && marker && sized
                    && device.ClipBalance == 0,
                $"holes={holes} surfaceId={surfaceId} ready={ready:0.###} clipDepth={clipDepth} pageBelow={page} "
                + $"markerAfter={marker} dst={holeDst.W:0}x{holeDst.H:0} clipBalance={device.ClipBalance}");
        }
    }

    static void MediaCardEngineChecks(StringTable strings)
    {
        // Pointer-rate radial-center updates stay in the binding/paint lane: no element rebuild and no GradientSpec
        // replacement. The recorder must source the draw command's radial origin from the sparse override.
        var runtime = new ReactiveRuntime();
        var center = new Signal<Point2>(new Point2(0.2f, 0.3f));
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings, runtime);
        recon.ReconcileRoot(new BoxEl
        {
            Width = 160f, Height = 100f,
            Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [new GradientStop(0f, ColorF.FromRgba(255, 255, 255)), new GradientStop(1f, ColorF.Transparent)])
                { RadialCenter = new Point2(0.5f, 0.5f), RadialRadius = new Point2(0.5f, 0.5f) },
            RadialGradientCenter = Prop<Point2>.FromSignal(center),
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        center.Value = new Point2(0.8f, 0.6f);
        runtime.Flush();
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200f, 140f), 1f, ColorF.Transparent));
        bool radialMoved = scene.TryGetRadialGradientCenter(scene.Root, out Point2 stored)
            && Near(stored.X, 0.8f, 0.001f) && Near(stored.Y, 0.6f, 0.001f)
            && dev.LastGradients.Count == 1
            && Near(dev.LastGradients[0].Start.X, 0.8f, 0.001f)
            && Near(dev.LastGradients[0].Start.Y, 0.6f, 0.001f);
        Check("gate.media-card.radial-center signal updates the recorded gradient without rebuilding its spec", radialMoved,
            $"stored=({stored.X:0.00},{stored.Y:0.00}) gradients={dev.LastGradients.Count}");

        // The new move channel routes through an interactive child to its container, leaf first. Touch and an active
        // press/capture do not drive hover-only spotlight work.
        int order = 0, parentCalls = 0;
        Point2 parentPoint = default;
        var routed = LayoutTree(strings, new BoxEl
        {
            Width = 200f, Height = 160f, Padding = Edges4.All(10f),
            OnPointerMoveWithin = p => { order = order * 10 + 2; parentCalls++; parentPoint = p; },
            Children =
            [
                new BoxEl
                {
                    Width = 80f, Height = 60f, OnClick = static () => { },
                    OnPointerMoveWithin = _ => order = order * 10 + 1,
                },
            ],
        });
        var dispatcher = new InputDispatcher(routed);
        var point = new Point2(30f, 25f);
        dispatcher.Dispatch([new InputEvent(InputKind.PointerMove, point, 0, 0, Pointer: PointerKind.Mouse)]);
        bool routedOrder = order == 12 && parentCalls == 1 && Near(parentPoint.X, 30f) && Near(parentPoint.Y, 25f);
        Check("gate.media-card.pointer-move-within routes leaf-to-root through interactive children", routedOrder,
            $"order={order} parentCalls={parentCalls} local=({parentPoint.X:0.0},{parentPoint.Y:0.0})");

        int beforeTouch = parentCalls;
        dispatcher.Dispatch([new InputEvent(InputKind.PointerMove, point, 0, 0, Pointer: PointerKind.Touch, PointerId: 7)]);
        bool touchSuppressed = parentCalls == beforeTouch;
        dispatcher.Dispatch([new InputEvent(InputKind.PointerDown, point, 0, 0, Pointer: PointerKind.Mouse)]);
        int beforeCaptureMove = parentCalls;
        dispatcher.Dispatch([new InputEvent(InputKind.PointerMove, new Point2(35f, 28f), 0, 0, Pointer: PointerKind.Mouse)]);
        bool captureSuppressed = parentCalls == beforeCaptureMove;
        dispatcher.Dispatch([new InputEvent(InputKind.PointerUp, new Point2(35f, 28f), 0, 0, Pointer: PointerKind.Mouse)]);
        Check("gate.media-card.pointer-move-within suppresses touch and active capture", touchSuppressed && captureSuppressed,
            $"touch={touchSuppressed} capture={captureSuppressed}");
    }

    // Chrome-focus trap: SetFocus(AutoSuggestBox.PartRoot) paints a ring but OnChar walks ancestors only, so
    // the inner EditableText never sees the character. FirstFocusableIn(partRoot) lands on the editor.
    static void AutoSuggestProgrammaticFocusChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("asb-pf", new Size2(420, 160), 1f)); window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var text = new Signal<string>("");
        using var host = new AppHost(app, window, device, fonts, strings,
            new W0fStaticProbe { Build = () => AutoSuggestBox.Create([], "Search", 260f, text) });
        host.RunFrame();

        var chrome = FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox);
        host.Input.SetFocus(chrome, visual: true);
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'a'));
        host.RunFrame();
        bool chromeTrap = text.Peek() == "";

        var editor = host.Input.FirstFocusableIn(chrome);
        host.Input.SetFocus(editor, visual: true);
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'a'));
        host.RunFrame();
        bool typed = text.Peek() == "a";
        var focusedRole = host.Scene.Interaction(host.Input.Focused).Role;
        bool editorRole = focusedRole == AutomationRole.Text;
        Check("gate.controls.autosuggest-programmatic-focus focusing AutoSuggestBox chrome swallows chars; FirstFocusableIn lands on the editor",
            chromeTrap && typed && editorRole,
            $"chromeTrap={chromeTrap} text='{text.Peek()}' focusedRole={focusedRole} editorNull={editor.IsNull}");
    }
}

sealed class SemanticZoomControlProbe : Component
{
    public readonly Signal<bool> IsZoomedOut = new(false);
    public readonly SemanticZoomController Controller = new();
    public readonly List<SemanticZoomViewChange> Started = [];
    public readonly List<SemanticZoomViewChange> Completed = [];
    private readonly bool _canChangeViews;

    public SemanticZoomControlProbe(bool canChangeViews = true) => _canChangeViews = canChangeViews;

    public override Element Render() => SemanticZoom.Create(
        new SemanticZoomSlots(
            new SemanticZoomView(new BoxEl
            {
                Grow = 1f,
                Children = [new TextEl("semantic-in")],
            }),
            new SemanticZoomView(new BoxEl
            {
                Grow = 1f,
                Children = [new TextEl("semantic-out")],
            })),
        new SemanticZoomOptions
        {
            IsZoomedOut = IsZoomedOut,
            MapInToOut = static index => index + 10,
            MapOutToIn = static _ => -1,
            ViewChangeStarted = Started.Add,
            ViewChangeCompleted = Completed.Add,
            Controller = Controller,
            CanChangeViews = _canChangeViews,
        });
}
