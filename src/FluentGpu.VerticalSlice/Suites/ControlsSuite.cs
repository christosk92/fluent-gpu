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
        MediaCardEngineChecks(strings);
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
        FocusRingChecks(strings);
        Wave2ControlChecks(strings);
        RepeatButtonChecks(strings);
        BasicInputControlChecks(strings);
        W1ControlsChecks(strings);
        D2PasswordRevealFocusChecks(strings);
        ProgressIndeterminateLifecycleChecks(strings);
        D3ExpanderChecks(strings);
        D3ExpanderWrapReflowChecks(strings);
        D5EditableComboBoxChecks(strings);
        D67SplitButtonFlyoutChecks(strings);
        ExpanderSettingsChecks(strings);
        PipsPagerOutputChecks(strings);
        AutoFitTextChecks(strings);
        FontFamilyChecks(strings);
        GradientBorderChecks(strings);
        PolylineStrokeChecks(strings);
        ContextMenuChecks(strings);
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

            // cp7.f — plain Flyout (PopupChrome.Popup, TAS_SHOWPOPUP): axis-aware entrance on the shared Fluent
            // flyout-family curve — TranslateY −50→0 (below-anchor) over 167ms cubic-bezier(0,0,0,1) with opacity 0→1
            // over the SAME 167ms window, linear, NO invisible hold. [ASSERTION-CHANGE vs the prior 367ms/FluentDecelerate
            // slide + 83ms opacity-hold "PVL dump": the OS TAS_SHOWPOPUP timeline is uxtheme-RUNTIME (not in the mux
            // source — verified: only g_entranceThemeOffset=50 + the per-side axis are pinned), so the entrance is aligned
            // to the verified Menu/PickerFlyout family spline (0,0,0,1) + the flyout-open-curve gate, removing the
            // perceptible ~83ms blank hold + floaty tail that read as a laggy open vs the menu path.]
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
                // opacity fades from the FIRST tick (linear, no hold) ≈ 40/167; TranslateY decaying from −50 (0,0,0,1 is front-loaded).
                bool t40 = !s.IsNull && Near(p40.Opacity, 40f / 167f, 0.06f) && p40.LocalTransform.Dy > -49f && p40.LocalTransform.Dy < 0f;
                clock.Advance(80f); host.RunFrame();   // t=120
                var p120 = s.IsNull ? default : host.Scene.Paint(s);
                bool t120 = !s.IsNull && Near(p120.Opacity, 120f / 167f, 0.06f)
                    && p120.LocalTransform.Dy > p40.LocalTransform.Dy && p120.LocalTransform.Dy <= 0.5f;
                svc.CloseTop();
                Settle();
                Check("cp7.f — plain Flyout: axis-aware TranslateY −50→0 over 167ms cubic(0,0,0,1) + opacity 0→1 over the SAME 167ms linear (no hold)",
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
            // only, FromH=FromV=0), Top/Bottom → TranslateY (covered by cp7.f). All ride cubic-bezier(0,0,0,1) over 167ms
            // with opacity 0→1 over the same window. A synthetic 40×40 anchor at (300,180) leaves room for BOTH Left
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
                clock.Advance(60f); host.RunFrame();
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
                clock.Advance(60f); host.RunFrame();
                var sF = SurfaceOf(host.Scene, bodyF);
                var pF = sF.IsNull ? default : host.Scene.Paint(sF);
                bool fullFadeNoSlide = !sF.IsNull && Near(pF.LocalTransform.Dx, 0f, 0.5f) && Near(pF.LocalTransform.Dy, 0f, 0.5f) && pF.Opacity > 0.05f;
                svc.CloseTop(); Settle();

                Check("gate.overlay.flyout-open-curve — PopupThemeTransition entrance axis: Left→TranslateX +50, Right→TranslateX −50, Full→fade-only (no slide); cubic(0,0,0,1)+opacity over 167ms",
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

        // gate.media.el.no-frameclock-rerender + gate.media.el.pure-render: during scripted position advance the player
        // component does NOT re-render (the FrameClock.Tick subscription is deleted), while the engine-driven pump fires
        // ONCE PER FRAME (not per render) — the video pump left Render for the frame phase.
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

            Check("gate.media.el.no-frameclock-rerender", playing && renders == 0 && !anyRendered && posAdvanced > 0.1f,
                $"playing={playing} renders={renders} anyRendered={anyRendered} pos={posAdvanced:0.###}");
            Check("gate.media.el.pure-render", pumpDelta == N && renders == 0,
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

            reg.PumpAll(1f);                                                  // a owns
            bool aDrivesFirst = aRuns == 1 && bRuns == 0;
            bool bSuppressed = reg.SuppressedNonOwnerPumpCount == supp0 + 1;

            reg.TransferOwnership(token, b);
            reg.PumpAll(1f);                                                  // b owns now; a is a no-op
            bool bDrivesAfter = bRuns == 1 && aRuns == 1;
            bool aSuppressed = reg.SuppressedNonOwnerPumpCount == supp0 + 2;

            reg.TransferOwnership(token, a);
            reg.PumpAll(1f);                                                  // transferred back
            bool aRestored = aRuns == 2 && bRuns == 1;

            reg.UnregisterPump(ra); reg.UnregisterPump(rb);
            Check("gate.media.el.ownership-transfer",
                token > 0 && ra > 0 && rb > 0 && aDrivesFirst && bSuppressed && bDrivesAfter && aSuppressed && aRestored,
                $"aFirst={aDrivesFirst} bSupp={bSuppressed} bDrives={bDrivesAfter} aSupp={aSuppressed} restored={aRestored}");
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

        // gate.media.el.controlled-aspect: an external Signal<VideoAspectMode> drives the fitted video rect (letterbox
        // pillars for Uniform, none for UniformToFill); the control also works standalone (auto-materialized signal).
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

            int barsUniform = CountFill(host.Scene, host.Scene.Root, Tok.MediaLetterbox);
            ext.Value = VideoAspectMode.UniformToFill;
            for (int i = 0; i < 3; i++) host.Paint(0);
            int barsCrop = CountFill(host.Scene, host.Scene.Root, Tok.MediaLetterbox);
            ext.Value = VideoAspectMode.Uniform;
            for (int i = 0; i < 3; i++) host.Paint(0);
            int barsUniform2 = CountFill(host.Scene, host.Scene.Root, Tok.MediaLetterbox);

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
            int barsAuto = CountFill(host2.Scene, host2.Scene.Root, Tok.MediaLetterbox);

            Check("gate.media.el.controlled-aspect", barsUniform >= 2 && barsCrop == 0 && barsUniform2 >= 2 && barsAuto >= 2,
                $"uniform={barsUniform} crop={barsCrop} uniform2={barsUniform2} autoMaterialized={barsAuto}");
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
}
