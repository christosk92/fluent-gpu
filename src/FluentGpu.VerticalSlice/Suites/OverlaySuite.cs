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


    sealed class FocusClipProbe : Component
    {
        public override Element Render() => new BoxEl
        {
            Padding = Edges4.All(30),
            Children =
            [
                new BoxEl
                {
                    Width = 100, Height = 40, Fill = ColorF.FromRgba(0x20, 0x20, 0x20),
                    ClipToBounds = true, OnClick = () => { },
                    Children = [new TextEl("clip") { Size = 12f }],
                },
            ],
        };
    }

static class OverlaySuite
{
    public static void Run(StringTable strings)
    {
        TextServicesSeamChecks();
        EditableTextCoreChecks(strings);
        TextConsumerControlChecks(strings);
        PlacementChecks();
        OverlayFocusRestoreChecks(strings);
        TextInputChecks(strings);
        OverlayChecks(strings);
        OverlayAnimationChecks(strings);
        E4PopupWindowingChecks(strings);
        G5fPopupToastChecks(strings);
        FlyoutAcrylicChecks(strings);
        AcrylicBackdropMathChecks();
        ContentDialogChromeChecks(strings);
        TeachingTipPlacementChecks(strings);
        MenuFlyoutStyleChecks(strings);
        SplitButtonStyleChecks(strings);
    }

    static void PlacementChecks()
    {
        var vp = new Size2(480, 400);

        // Anchor near the bottom: a 120-tall free flyout can't fit below → flips ABOVE; free flyouts keep all corners.
        var low = new RectF(20, 380, 100, 24);
        var pa = FlyoutPositioner.Place(in low, new Size2(100, 120), in vp, FlyoutPlacement.BottomLeft);
        bool flipped = pa.OpensUp && pa.Y < low.Y && pa.CornerJoin == CornerJoin.None;

        // Anchor near the top: a free flyout opens BELOW (default) and still keeps all corners rounded.
        var high = new RectF(20, 10, 100, 24);
        var pb = FlyoutPositioner.Place(in high, new Size2(100, 120), in vp, FlyoutPlacement.BottomLeft);
        // WinUI FlyoutBase::FlyoutMargin = 4 (FlyoutBase_Partial.cpp:65) — the flyout sits 4px off the anchor edge.
        bool below = !pb.OpensUp && pb.Y >= high.Y + high.H + 3.5f && pb.CornerJoin == CornerJoin.None;

        // Attached stretch dropdowns (ComboBox/AutoSuggest style) stay flush and join the field edge.
        var attach = FlyoutPositioner.Place(in high, new Size2(100, 120), in vp, FlyoutPlacement.BottomStretch);
        bool attachedJoin = !attach.OpensUp && Near(attach.Y, high.Y + high.H, 0.01f) && attach.CornerJoin == CornerJoin.Top;

        // Popup taller than the viewport (collides both ways): clamp MeasuredH to the larger side (here, below).
        var mid = new RectF(20, 180, 100, 24);
        var pc = FlyoutPositioner.Place(in mid, new Size2(100, 600), in vp, FlyoutPlacement.BottomLeft);
        bool clamped = pc.MeasuredH > 0f && pc.MeasuredH < 600f;

        Check("W1-P5.a placement: flip-up + free/attached corner-join + collision clamp",
            flipped && below && attachedJoin && clamped,
            $"flip={flipped} below={below} attach={attachedJoin} clampH={pc.MeasuredH:0}");
    }

    static void OverlayFocusRestoreChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ovfocus", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new FocusRestoreProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        ClickNode(host, window, root.AnchorNode);   // focus the anchor (pre-open focus)
        bool anchorFocused = (host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0;

        // Open an overlay (captures SavedFocus = anchor), Tab moves focus into it, then close and let it settle.
        root.Handle = root.Service!.Open(() => root.AnchorNode,
            () => new BoxEl { Width = 120, Height = 80, Children = [new BoxEl { Width = 100, Height = 24, OnClick = () => { } }] });
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        host.RunFrame();
        bool movedAway = (host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) == 0;
        void DumpP5b(NodeHandle n, int d)
        {
            var fl = host.Scene.Flags(n);
            Console.Error.WriteLine($"[DBG-P5b] {new string(' ', d * 2)}{n} focusable={(fl & NodeFlags.Focusable) != 0} focused={(fl & NodeFlags.Focused) != 0} role={host.Scene.Interaction(n).Role}");
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) DumpP5b(c, d + 1);
        }
        DumpP5b(host.Scene.Root, 0);

        Console.Error.WriteLine($"[DBG-P5b] beforeClose anchor={root.AnchorNode}");
        root.Handle.Close();
        Console.Error.WriteLine($"[DBG-P5b] afterClose anchorFocused={(host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0}");
        for (int i = 0; i < 30; i++)
        {
            host.RunFrame();   // close fade settles → Finalize → restore
            if (i < 5 || i == 29) Console.Error.WriteLine($"[DBG-P5b] f{i} anchorFocused={(host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0}");
        }
        bool restored = (host.Scene.Flags(root.AnchorNode) & NodeFlags.Focused) != 0;

        Check("W1-P5.b overlay restores focus to the pre-open node on close",
            anchorFocused && movedAway && restored, $"anchor0={anchorFocused} movedAway={movedAway} restored={restored}");
    }

    static void TextServicesSeamChecks()
    {
        using var app = new HeadlessPlatformApp();
        var clip = app.Clipboard;
        uint seq0 = clip.SequenceNumber;
        clip.SetText("héllo ✂");
        bool roundTrip = clip.TryGetText(out var read) && read == "héllo ✂" && clip.SequenceNumber == seq0 + 1;
        ((HeadlessClipboard)clip).Clear();
        bool cleared = !clip.TryGetText(out _) && clip.SequenceNumber == seq0 + 2;
        Check("W0a.1 clipboard seam: unicode round-trip + epoch bumps + clear", roundTrip && cleared,
            $"read='{read}' seq={clip.SequenceNumber - seq0}");

        var window = new HeadlessWindow(new WindowDesc("ime", new Size2(100, 100), 1f));
        var ti = (HeadlessTextInput)window.TextInput;
        var sink = new RecordingSink();
        ti.SetSink(sink);
        ti.BeginComposition();                       // not editable yet → must no-op
        bool gated = sink.Log.Count == 0;
        ti.SetEditable(true);
        ti.BeginComposition();
        ti.UpdateComposition("にほ", 2, new ImeClause(0, 2, ImeClauseKind.Input));
        ti.UpdateComposition("日本", 2, new ImeClause(0, 2, ImeClauseKind.TargetConverted));
        ti.Commit("日本");
        bool flow = sink.Log.Count == 5 && sink.Log[0] == "start" && sink.Log[1] == "upd:にほ@2:Input"
                    && sink.Log[2] == "upd:日本@2:TargetConverted" && sink.Log[3] == "commit:日本" && sink.Log[4] == "end";
        sink.Log.Clear();
        ti.BeginComposition();
        ti.UpdateComposition("か", 1, new ImeClause(0, 1, ImeClauseKind.Input));
        ti.SetEditable(false);                       // focus leaves the editor mid-composition → cancel (empty update + end)
        bool cancelled = sink.Log.Count == 4 && sink.Log[2] == "upd:@0:" && sink.Log[3] == "end";
        Check("W0a.2 IME seam: editable gate, composition lifecycle, cancel-on-blur", gated && flow && cancelled,
            $"gated={gated} flow={flow} cancel={cancelled} log=[{string.Join("|", sink.Log)}]");
    }

    private sealed class RecordingSink : FluentGpu.Pal.ITextInputSink
    {
        public readonly List<string> Log = new();
        public void OnCompositionStart() => Log.Add("start");
        public void OnCompositionUpdate(ReadOnlySpan<char> text, int caret, ReadOnlySpan<FluentGpu.Pal.ImeClause> clauses)
            => Log.Add($"upd:{text.ToString()}@{caret}:{(clauses.Length > 0 ? clauses[0].Kind.ToString() : "")}");
        public void OnCompositionCommit(ReadOnlySpan<char> text) => Log.Add($"commit:{text.ToString()}");
        public void OnCompositionEnd() => Log.Add("end");
    }

    static void EditableTextCoreChecks(StringTable strings)
    {
        const float Adv = 14f * 0.55f;     // 7.7 dip per UTF-16 unit
        const float LineH = 14f * 1.4f;    // 19.6 dip per line

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

        // ── "hello world": caret click, keyboard selection, double/triple click, drag rects (+0-alloc), clipboard ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "hello world" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);

            void Press(float x, float y, uint t, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t));
                window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(x, y), 0, 0, 0f, mods, PointerKind.Mouse, false, t + 10));
                host.RunFrame();
            }
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }

            // W0e.1 — caret click placement round-trips the advance model; pointer focus arms the blinker.
            Press(ta.X + 3f * Adv + 1f, ta.Y + ta.H / 2f, 1_000);
            scene.TryGetTextEdit(tn, out var tes1);
            const byte focusedBits = TextEditState.Focused | TextEditState.CaretVisible;
            Check("W0e.1 caret click placement round-trips the headless advance model; focus arms the blinker",
                root.Edit!.Core.Active == 3 && Near(tes1.CaretX, 3f * Adv)
                && (tes1.Flags & focusedBits) == focusedBits && (scene.Flags(field) & NodeFlags.Focused) != 0,
                $"caret={root.Edit!.Core.Active} caretX={tes1.CaretX:0.##} flags={tes1.Flags}");

            // W0e.2 — Shift+arrow / Ctrl+Shift+arrow word / Ctrl+A selection states (+SelectionChanged fires).
            Key(Keys.Right, KeyModifiers.Shift);
            Key(Keys.Right, KeyModifiers.Shift);
            bool s1 = root.Edit!.SelectionStart == 3 && root.Edit!.SelectionLength == 2 && root.Edit!.SelectedText == "lo";
            Key(Keys.Right, KeyModifiers.Shift | KeyModifiers.Ctrl);
            bool s2 = root.Edit!.SelectionStart == 3 && root.Edit!.SelectionLength == 3 && root.Edit!.SelectedText == "lo ";
            Key(Keys.A, KeyModifiers.Ctrl);
            bool s3 = root.Edit!.SelectionStart == 0 && root.Edit!.SelectionLength == 11;
            Check("W0e.2 Shift+arrow extends, Ctrl+Shift+arrow extends by word, Ctrl+A selects all (+SelectionChanged)",
                s1 && s2 && s3 && root.SelLog.Count >= 3, $"s1={s1} s2={s2} s3={s3} selEvents={root.SelLog.Count}");

            // W0e.3 — double-click selects the word at the hit; triple-click selects all (ClickCount synthesis).
            float wx = ta.X + 7f * Adv + 1f;   // inside "world"
            float wy = ta.Y + ta.H / 2f;
            Press(wx, wy, 5_000);
            Press(wx, wy, 5_100);              // chained within slop+window → ClickCount 2
            bool dbl = root.Edit!.SelectedText == "world" && root.Edit!.SelectionStart == 6;
            Press(wx, wy, 5_200);              // → ClickCount 3
            bool trp = root.Edit!.SelectionStart == 0 && root.Edit!.SelectionLength == 11;
            Check("W0e.3 double-click selects the word at the hit; triple-click selects all", dbl && trp, $"dbl={dbl} trp={trp}");

            // W0e.4 — drag-select publishes rects into the scene slab (count + X/W math) while the button is held.
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ta.X + 1f * Adv + 1f, wy), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 9_000));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(ta.X + 7f * Adv + 1f, wy), 0, 0));
            host.RunFrame();
            var selRects = scene.GetTextEditSelectionRects(tn);
            scene.TryGetTextEdit(tn, out var tes4);
            bool dragSel = selRects.Length == 1 && Near(selRects[0].X, 1f * Adv) && Near(selRects[0].W, 6f * Adv)
                && root.Edit!.SelectionStart == 1 && root.Edit!.SelectionLength == 6
                && (tes4.Flags & TextEditState.SelectionActive) != 0;
            Check("W0e.4 drag-select updates the selection rects in the scene slab (count + first-rect X/W math)",
                dragSel, $"rects={selRects.Length} x={(selRects.Length > 0 ? selRects[0].X : -1f):0.##} w={(selRects.Length > 0 ? selRects[0].W : -1f):0.##}");

            for (int i = 0; i < 5; i++)        // warm the pooled slab, then a steady pointer-rate drag frame
            {
                window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(ta.X + (i % 2 == 0 ? 6f : 7f) * Adv + 1f, wy), 0, 0));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(ta.X + 8f * Adv + 1f, wy), 0, 0));
            var dragSteady = host.RunFrame();
            Check("W0e.4b drag-select frame is 0-alloc on phases 6–13 (pooled slab, no re-render)",
                dragSteady.HotPhaseAllocBytes == 0, $"{dragSteady.HotPhaseAllocBytes} bytes");
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ta.X + 8f * Adv + 1f, wy), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 9_500));
            host.RunFrame();

            // W0e.5 — clipboard round-trip via the HeadlessClipboard seam.
            var clip = (HeadlessClipboard)app.Clipboard;
            root.Edit!.Select(6, 5);           // "world"
            Key(Keys.C, KeyModifiers.Ctrl);
            bool copied = clip.TryGetText(out var c1) && c1 == "world";
            root.Edit!.Select(0, 6);           // "hello "
            Key(Keys.X, KeyModifiers.Ctrl);
            bool cutOk = clip.TryGetText(out var c2) && c2 == "hello " && root.Text!.Peek() == "world";
            Key(Keys.End);
            Key(Keys.V, KeyModifiers.Ctrl);
            bool pasted = root.Text!.Peek() == "worldhello ";
            Check("W0e.5 clipboard: Ctrl+C copies, Ctrl+X removes, Ctrl+V inserts at the caret",
                copied && cutOk && pasted, $"copy='{c1}' cut='{c2}' text='{root.Text!.Peek()}'");
        }

        // ── W0e.t — TOUCH caret/drag-select/double-tap on the SAME HandlePressed/HandleDrag the mouse drives ──
        // The dispatcher's touch path (TouchDown/Move/Up) feeds a real touch press into the editor: focus-on-press,
        // caret at the hit offset (ClickCount=1), drag-select via the OnDrag implicit-capture, word-select on a
        // double-tap (the pointer-kind-agnostic click counter). Probe + advance model are W0e's; only the gesture differs.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-touch", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "hello world" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            float wy = ta.Y + ta.H / 2f;

            // W0e.t1 — a touch tap focuses the field and places the caret at the tapped offset (same HitTestText as mouse).
            uint t1 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(ta.X + 3f * Adv + 1f, wy), t1, 1));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(ta.X + 3f * Adv + 1f, wy), t1 + 16, 1));
            host.RunFrame();
            s_touchClockMs = t1 + 1000;
            scene.TryGetTextEdit(tn, out var tesT1);
            const byte focusedBitsT = TextEditState.Focused | TextEditState.CaretVisible;
            bool tapCaret = root.Edit!.Core.Active == 3 && Near(tesT1.CaretX, 3f * Adv)
                && (scene.Flags(field) & NodeFlags.Focused) != 0 && (tesT1.Flags & focusedBitsT) == focusedBitsT;
            Check("W0e.t1 a touch tap focuses the editor and lands the caret at the tapped text offset (same HitTestText as the mouse)",
                tapCaret, $"caret={root.Edit!.Core.Active} caretX={tesT1.CaretX:0.##} focused={(scene.Flags(field) & NodeFlags.Focused) != 0} flags={tesT1.Flags}");

            // W0e.t2 — a touch drag from inside the text extends the selection (the OnDrag implicit-capture: TouchDown set
            // _dragTarget=field, each move drives HandleDrag). A vertical component on the drag proves the editor's drag
            // wins over any pan — a single-line editor isn't scrollable, but this is the same _dragTarget mechanism the
            // in-scroller arbitration (W0e.t4) relies on.
            uint t2 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(ta.X + 1f * Adv + 1f, wy), t2, 2));
            host.RunFrame();
            for (int i = 1; i <= 6; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(ta.X + (1f + i) * Adv + 1f, wy + i * 4f), t2 + (uint)i * 16, 2));
                host.RunFrame();
            }
            var selT2 = scene.GetTextEditSelectionRects(tn);
            bool dragSel = root.Edit!.SelectionStart == 1 && root.Edit!.SelectionLength == 6
                && selT2.Length == 1 && Near(selT2[0].X, 1f * Adv) && Near(selT2[0].W, 6f * Adv);
            // A steady touch-drag-select move is 0-alloc on phases 6–13: the per-id slot rebind + the OnDrag drive
            // (GetDrag → HandleDrag → SyncVisual's pooled rect slab) stays clean (the touch path's drive is byte-identical
            // to the mouse _dragTarget branch W0e.4b proves clean). Warm the slab over a few moves, then measure one.
            uint warmMs = t2 + 7 * 16;
            for (int i = 0; i < 4; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(ta.X + (i % 2 == 0 ? 6f : 7f) * Adv + 1f, wy + 24f), warmMs + (uint)i * 16, 2));
                host.RunFrame();
            }
            window.QueueInput(Touch(InputKind.PointerMove, new Point2(ta.X + 8f * Adv + 1f, wy + 24f), warmMs + 4 * 16, 2));
            var dragSteadyT = host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(ta.X + 8f * Adv + 1f, wy + 24f), warmMs + 5 * 16, 2));
            host.RunFrame();
            s_touchClockMs = warmMs + 6 * 16 + 1000;
            Check("W0e.t2 a touch drag from inside the text extends the selection by character (the editor's OnDrag capture, not a tap)",
                dragSel, $"start={root.Edit!.SelectionStart} len={root.Edit!.SelectionLength} rects={selT2.Length}");
            Check("W0e.t2b a steady touch drag-select frame is 0-alloc on phases 6–13 (per-id slot rebind + OnDrag drive + pooled slab)",
                dragSteadyT.HotPhaseAllocBytes == 0, $"{dragSteadyT.HotPhaseAllocBytes} bytes");

            // W0e.t3 — a touch DOUBLE-TAP selects the word at the hit, exactly like a mouse double-click (the click
            // counter is pointer-kind-agnostic — TouchDown now tracks it and forwards ClickCount=2 to OnPointerPressed).
            float wxd = ta.X + 7f * Adv + 1f;   // inside "world" (chars 6..10)
            uint t3 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(wxd, wy), t3, 3));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(wxd, wy), t3 + 16, 3));
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(wxd, wy), t3 + 120, 3));   // 2nd tap inside slop+DoubleClickMs → ClickCount 2
            host.RunFrame();
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(wxd, wy), t3 + 136, 3));
            host.RunFrame();
            s_touchClockMs = t3 + 1000;
            bool dblTap = root.Edit!.SelectedText == "world" && root.Edit!.SelectionStart == 6;
            Check("W0e.t3 a touch double-tap selects the word at the hit (pointer-kind-agnostic click count → ClickCount=2)",
                dblTap, $"sel='{root.Edit!.SelectedText}' start={root.Edit!.SelectionStart}");
        }

        // ── W0e.t4 — the single-recognizer ARBITRATION: an EditableText INSIDE a vertical ScrollView ──
        // A touch drag that STARTS on the text must extend the editor's selection and NOT pan the scroller (the OnDrag
        // implicit-capture owns the contact; the pan candidate is never armed). A touch drag that starts on the non-text
        // filler below still pans. Same shared _dragTarget mechanism the Slider drag rides (proved elsewhere by the
        // Slider working by touch); documented Phase-3 nuance: tap-vs-drag ambiguity within slop is the selection team's.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-touch-scroll", new Size2(360, 320), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new TouchEditInScrollerProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            var scroller = FindScrollable(scene, scene.Root);
            float wy = ta.Y + ta.H / 2f;

            // DRAG ON THE TEXT (rightward to extend the selection, with a big downward component on the scroll axis): the
            // editor's drag wins, the scroller does NOT move.
            uint t = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(ta.X + 1f * Adv + 1f, wy), t, 4));
            host.RunFrame();
            for (int i = 1; i <= 6; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(ta.X + (1f + i) * Adv + 1f, wy + i * 12f), t + (uint)i * 16, 4));
                host.RunFrame();
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(ta.X + 7f * Adv + 1f, wy + 72f), t + 7 * 16, 4));
            host.RunFrame();
            s_touchClockMs = t + 1000;
            scene.TryGetScroll(scroller, out var afterTextDrag);
            bool textDragSelected = root.Edit!.SelectionStart == 1 && root.Edit!.SelectionLength == 6;
            bool textDragNoPan = Near(afterTextDrag.OffsetY, 0f);

            // DRAG ON THE FILLER (below the editor): a touch on non-text chrome still pans the scroller.
            float fillerY = ta.Y + ta.H + 60f;   // on the tall filler box, clear of the editor
            float fillerX = ta.X + ta.W / 2f;
            uint t2 = s_touchClockMs;
            window.QueueInput(Touch(InputKind.PointerDown, new Point2(fillerX, fillerY), t2, 5));
            host.RunFrame();
            for (int i = 1; i <= 8; i++)
            {
                window.QueueInput(Touch(InputKind.PointerMove, new Point2(fillerX, fillerY - i * 12f), t2 + (uint)i * 16, 5));
                host.RunFrame();
            }
            window.QueueInput(Touch(InputKind.PointerUp, new Point2(fillerX, fillerY - 96f), t2 + 9 * 16, 5));
            host.RunFrame();
            s_touchClockMs = t2 + 1000;
            scene.TryGetScroll(scroller, out var afterFillerDrag);
            bool fillerPanned = afterFillerDrag.OffsetY > 40f;

            Check("W0e.t4 a touch drag starting ON an EditableText inside a scroller extends the selection and never pans; a drag on the non-text filler pans (single-recognizer arbitration: OnDrag capture wins over content-pan)",
                textDragSelected && textDragNoPan && fillerPanned,
                $"textSel(start={root.Edit!.SelectionStart},len={root.Edit!.SelectionLength}) panOnText={afterTextDrag.OffsetY:0.#} panOnFiller={afterFillerDrag.OffsetY:0.#}");
        }

        // ── W0e.6 — PasswordBox mask: '●' display, copy/cut blocked, paste allowed ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-mask", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "secret", MaskOn = true };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            ClickNode(host, window, field);
            Key(Keys.A, KeyModifiers.Ctrl);
            var clip = (HeadlessClipboard)app.Clipboard;
            clip.Clear();
            Key(Keys.C, KeyModifiers.Ctrl);
            bool copyBlocked = !clip.TryGetText(out _);
            Key(Keys.X, KeyModifiers.Ctrl);
            bool cutBlocked = root.Text!.Peek() == "secret" && !clip.TryGetText(out _);
            clip.SetText("pw");
            Key(Keys.V, KeyModifiers.Ctrl);    // paste replaces the (kept) select-all
            host.RunFrame();
            string disp = strings.Resolve(scene.Paint(tn).Text);
            Check("W0e.6 Mask blocks copy/cut, allows paste; display is '\\u25CF' per grapheme (model text real)",
                copyBlocked && cutBlocked && root.Text!.Peek() == "pw" && disp == "●●",
                $"copyBlocked={copyBlocked} cutBlocked={cutBlocked} text='{root.Text!.Peek()}' disp='{disp}'");
        }

        // ── W0e.7 — undo coalescing: a typing burst is ONE step; paste is its own step; Ctrl+Z/Ctrl+Y replay ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-undo", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            void Type(string s)
            {
                foreach (char c in s) window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
                host.RunFrame();
            }
            ClickNode(host, window, field);
            Type("abc");
            bool oneStep = root.Edit!.Core.UndoDepth == 1 && root.Text!.Peek() == "abc";
            Key(Keys.Z, KeyModifiers.Ctrl);
            bool undone = root.Text!.Peek() == "";
            Key(Keys.Y, KeyModifiers.Ctrl);
            bool redone = root.Text!.Peek() == "abc";
            ((HeadlessClipboard)app.Clipboard).SetText("XY");
            Key(Keys.End);
            Key(Keys.V, KeyModifiers.Ctrl);
            bool pasteStep = root.Text!.Peek() == "abcXY" && root.Edit!.Core.UndoDepth == 2;
            Type("z");
            bool thirdStep = root.Edit!.Core.UndoDepth == 3;
            Key(Keys.Z, KeyModifiers.Ctrl);
            Key(Keys.Z, KeyModifiers.Ctrl);
            bool back = root.Text!.Peek() == "abc";
            Check("W0e.7 undo coalescing: 'abc' = one step back to empty; paste = its own step; Ctrl+Z/Ctrl+Y replay",
                oneStep && undone && redone && pasteStep && thirdStep && back,
                $"one={oneStep} undo={undone} redo={redone} paste={pasteStep} depth3={thirdStep} back='{root.Text!.Peek()}'");
        }

        // ── W0e.8 — MaxLength clamp + BeforeTextChanging rejection ──
        bool maxClamp;
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-max", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { MaxLen = 5 };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Text));
            foreach (char c in "abcdefgh") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            maxClamp = root.Text!.Peek() == "abcde";
        }
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-before", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Before = s => s.Length <= 3 };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.Text));
            foreach (char c in "abcde") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            Check("W0e.8 MaxLength clamps inserts at the limit; BeforeTextChanging(false) rejects the proposed edit",
                maxClamp && root.Text!.Peek() == "abc", $"maxLen→'{(maxClamp ? "abcde" : "?")}' gated='{root.Text!.Peek()}'");
        }

        // ── W0e.9 — IsReadOnly: caret + selection + copy work, typing gated, IME stays disabled ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-ro", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "locked", ReadOnly = true };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ta.X + 2f * Adv + 1f, ta.Y + ta.H / 2f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ta.X + 2f * Adv + 1f, ta.Y + ta.H / 2f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_010));
            host.RunFrame();
            bool caretOk = root.Edit!.Core.Active == 2;
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            bool typingGated = root.Text!.Peek() == "locked";
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            Key(Keys.A, KeyModifiers.Ctrl);
            Key(Keys.C, KeyModifiers.Ctrl);
            bool copyOk = ((HeadlessClipboard)app.Clipboard).TryGetText(out var t) && t == "locked";
            bool imeOff = !((HeadlessTextInput)window.TextInput).Editable;
            Check("W0e.9 IsReadOnly gates typing but allows caret placement, selection and copy (IME disabled)",
                caretOk && typingGated && copyOk && imeOff, $"caret={root.Edit!.Core.Active} text='{root.Text!.Peek()}' copy='{t}' imeOff={imeOff}");
        }

        // ── W0e.10 — Enter commits + KEEPS focus; Escape reverts to the focus-time snapshot + cancels + blurs ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-esc", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "seed" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            void Type(string s)
            {
                foreach (char c in s) window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
                host.RunFrame();
            }
            ClickNode(host, window, field);    // center is beyond the text → caret at end
            Type("XY");
            Key(Keys.Enter);
            bool committed = root.Committed == "seedXY" && (scene.Flags(field) & NodeFlags.Focused) != 0;
            Type("Z");
            Key(Keys.Escape);
            scene.TryGetTextEdit(tn, out var tesEsc);
            bool reverted = root.Text!.Peek() == "seed" && root.CancelCount == 1
                && (scene.Flags(field) & NodeFlags.Focused) == 0 && (tesEsc.Flags & TextEditState.Focused) == 0;
            Check("W0e.10 Enter commits and KEEPS focus (WinUI); Escape reverts to the focus-time snapshot, cancels, blurs",
                committed && reverted, $"committed='{root.Committed}' text='{root.Text!.Peek()}' cancels={root.CancelCount}");
        }

        // ── W0e.11/12 — IME composition lifecycle + caret blink ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-ime", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            var ti = (HeadlessTextInput)window.TextInput;
            bool editable = ti.Editable;

            ti.BeginComposition();
            ti.UpdateComposition("にほ", 2, new ImeClause(0, 2, ImeClauseKind.Input));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var ic);
            var ul = scene.GetTextEditUnderlineRects(tn);
            // The honest contract under test: the provisional composition lives in the DOC (display shows it) while the
            // SIGNAL stays unchanged until commit.
            bool provisional = strings.Resolve(scene.Paint(tn).Text) == "にほ" && root.Text!.Peek().Length == 0
                && ic.CompStart == 0 && ic.CompLen == 2 && ul.Length == 1 && Near(ul[0].H, 1f);

            ti.UpdateComposition("日本", 2, new ImeClause(0, 2, ImeClauseKind.TargetConverted));
            host.RunFrame();
            var ul2 = scene.GetTextEditUnderlineRects(tn);
            bool targetThick = ul2.Length == 1 && Near(ul2[0].H, 2f);

            ti.Commit("日本");
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var ic2);
            bool committed = root.Text!.Peek() == "日本" && strings.Resolve(scene.Paint(tn).Text) == "日本"
                && ic2.CompLen == 0 && scene.GetTextEditUnderlineRects(tn).Length == 0 && root.Edit!.Core.UndoDepth == 1;

            ti.BeginComposition();
            ti.UpdateComposition("か", 1, new ImeClause(0, 1, ImeClauseKind.Input));
            host.RunFrame();
            bool midCancel = strings.Resolve(scene.Paint(tn).Text) == "日本か" && root.Text!.Peek() == "日本";
            ti.Cancel();
            host.RunFrame();
            bool cancelled = strings.Resolve(scene.Paint(tn).Text) == "日本" && root.Text!.Peek() == "日本";
            bool caretRect = Near(ti.LastCaretRectPx.X, scene.AbsoluteRect(tn).X + 2f * Adv);
            Check("W0e.11 IME: provisional in the DOC only (signal commits on commit), clause underline kinds, cancel restores, caret-rect placed",
                editable && provisional && targetThick && committed && midCancel && cancelled && caretRect,
                $"editable={editable} prov={provisional} thick={targetThick} commit={committed} mid={midCancel} cancel={cancelled} rectX={ti.LastCaretRectPx.X:0.#}");

            // W0e.12 — caret blink toggles CaretVisible over frames (500ms half-period @16ms fixed dt) + resets on edit.
            bool wentOff = false;
            int frames = 0;
            for (; frames < 90 && !wentOff; frames++)
            {
                host.RunFrame();
                scene.TryGetTextEdit(tn, out var bf);
                wentOff = (bf.Flags & TextEditState.CaretVisible) == 0;
            }
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'x'));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var br);
            bool resetOn = (br.Flags & TextEditState.CaretVisible) != 0;
            Check("W0e.12 caret blink toggles CaretVisible over frames and snaps visible on edit",
                wentOff && resetOn, $"offAfter={frames}f resetOn={resetOn}");
            for (int i = 0; i < 6; i++) host.RunFrame();
            var blinkSteady = host.RunFrame();
            Check("W0e.12b focused steady (caret blink) frame allocates 0 on phases 6–13",
                blinkSteady.HotPhaseAllocBytes == 0, $"{blinkSteady.HotPhaseAllocBytes} bytes");
        }

        // ── W0e.13 — Tab (keyboard) focus selects all; the DeleteButton clears + keeps focus + hides when empty ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-del", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { Initial = "abc", ShowDelete = true };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            host.RunFrame();
            host.RunFrame();   // the focus re-render mounts the delete button
            bool selAll = root.Edit!.SelectionStart == 0 && root.Edit!.SelectionLength == 3;
            bool visual = (scene.Flags(field) & NodeFlags.FocusVisual) != 0;
            var btns = Roles(scene, AutomationRole.Button);
            bool shown = btns.Count == 1;
            if (shown) ClickNode(host, window, btns[0]);
            host.RunFrame();
            bool cleared = root.Text!.Peek() == "" && (scene.Flags(field) & NodeFlags.Focused) != 0;
            bool gone = Roles(scene, AutomationRole.Button).Count == 0;
            Check("W0e.13 Tab keyboard-focus selects all; DeleteButton (E894) clears, keeps focus, hides when empty",
                selAll && visual && shown && cleared && gone,
                $"selAll={selAll} visual={visual} shown={shown} cleared={cleared} gone={gone}");
        }

        // ── W0e.14 — caret-follow: ScrollX clamps the caret into the viewport; the wrapper transform applies -ScrollX ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-scroll", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0eProbe { W = 80f };   // lane viewport = 80 − (10+6) padding = 64
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            for (int i = 0; i < 20; i++) window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'm'));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var tf);
            // caret = 20×7.7 = 154; follow wants 154−(64−8) = 98, clamped to maxScroll = (154+2)−64 = 92.
            bool follow = Near(tf.CaretX, 154f) && Near(tf.ScrollX, 92f);
            host.RunFrame();   // the TransformBind flush applies -ScrollX to the wrapper
            var scroller = scene.Parent(tn);
            float appliedDx = scene.Paint(scroller).LocalTransform.Dx;
            bool applied = Near(appliedDx, -92f);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Home));
            host.RunFrame();
            scene.TryGetTextEdit(tn, out var th);
            bool home = Near(th.ScrollX, 0f);
            Check("W0e.14 caret-follow clamps ScrollX (caret stays inside the viewport) and the wrapper transform applies -ScrollX",
                follow && applied && home, $"caretX={tf.CaretX:0.#} scrollX={tf.ScrollX:0.#} dx={appliedDx:0.#} home={th.ScrollX:0.#}");
            for (int i = 0; i < 6; i++) host.RunFrame();
            var idle = host.RunFrame();
            Check("W0e.14b typing-burst-then-idle steady frame allocates 0 on phases 6–13",
                idle.HotPhaseAllocBytes == 0, $"{idle.HotPhaseAllocBytes} bytes");
        }

        // ── W0e.15 — multi-line: Up/Down honor the StickyX goal column over wrapped lines; Enter inserts '\r' ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0e-multi", new Size2(420, 240), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            // wrap width 144 → "aaaa bbbb cccc " | "dddd eeee" (greedy word wrap of the headless model)
            var root = new W0eProbe { Multi = true, W = 160f, H = 64f, Initial = "aaaa bbbb cccc dddd eeee" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            var ta = scene.AbsoluteRect(tn);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(ta.X + 2f * Adv + 1f, ta.Y + LineH * 0.5f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_000));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(ta.X + 2f * Adv + 1f, ta.Y + LineH * 0.5f), 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 1_010));
            host.RunFrame();
            bool caretLine1 = root.Edit!.Core.Active == 2;
            Key(Keys.Down);
            bool down1 = root.Edit!.Core.Active == 17;     // line 2 starts at 15; sticky column 2 → 15+2
            Key(Keys.Down);
            bool down2 = root.Edit!.Core.Active == 17;     // clamped at the last line
            Key(Keys.Up);
            bool up1 = root.Edit!.Core.Active == 2;        // StickyX goal column held across the round-trip
            Key(Keys.Down, KeyModifiers.Shift);
            bool extended = root.Edit!.SelectionStart == 2 && root.Edit!.SelectionLength == 15;
            Key(Keys.Right);                                // collapse to the selection end (17)
            Key(Keys.Enter);                                // multi-line Enter inserts a hard '\r' break
            string after = root.Text!.Peek();
            bool newline = after.Length == 25 && after[17] == '\r';
            Check("W0e.15 multi-line: Up/Down/StickyX over wrapped lines (clamped at edges); Shift+Down extends; Enter inserts '\\r'",
                caretLine1 && down1 && down2 && up1 && extended && newline,
                $"caret={caretLine1} d1={down1} d2={down2} up={up1} ext={extended} nl={newline}");
        }
    }

    static void TextConsumerControlChecks(StringTable strings)
    {
        const float Adv = 14f * 0.55f;     // headless advance model @ FontSize 14 (see EditableTextCoreChecks)

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

        // ── W0f.1 — PasswordBox Peek: press-and-hold reveals, release re-masks; reveal button = F78D @ width 30 ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pw", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            bool masked0 = strings.Resolve(scene.Paint(tn).Text) == "●●●●●●";
            bool noBtn0 = Roles(scene, AutomationRole.Button).Count == 0;   // ButtonCollapsed while unfocused

            ClickNode(host, window, field);
            host.RunFrame();
            // Focusing a POPULATED box does NOT show the eye: CPasswordBox::OnGotFocus clears m_fCanShowRevealButton
            // (PasswordBox.cpp:572–581) and CanInvokeRevealButton requires it (PasswordBox.cpp:618–626).
            bool noBtnOnFocus = Roles(scene, AutomationRole.Button).Count == 0;

            // Empty the box, then type a fresh password — the empty→non-empty content change arms the button
            // (OnContentChanged, PasswordBox.cpp:366–377: "only allow password reveal button if transitioning from
            // empty to non-empty state").
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, 0f, KeyModifiers.Ctrl));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back, 0f, KeyModifiers.None));
            host.RunFrame();
            foreach (char c in "secret") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            var btns = Roles(scene, AutomationRole.Button);
            bool shown = btns.Count == 1;
            bool w30 = shown && Near(scene.AbsoluteRect(btns[0]).W, 30f);
            // RevealButton glyph F78D @ TextControlButtonForeground = TextFillColorSecondary #C5FFFFFF (dark)
            // (PasswordBox_themeresources.xaml:100 + TextBox_themeresources.xaml:45).
            bool glyph = shown && HasGlyph(device, strings, "")
                && ColorClose(GlyphColor(device, strings, ""), Tok.TextSecondary, 0.004f);

            // Press-and-HOLD (no release yet) → the password shows in clear text.
            var bc = CenterOf(scene, btns[0]);
            window.QueueInput(new InputEvent(InputKind.PointerDown, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 2_000));
            host.RunFrame();
            host.RunFrame();
            bool peeked = strings.Resolve(scene.Paint(tn).Text) == "secret";

            // Release → re-masks and the FIELD keeps focus (WinUI keeps the field focused across reveal interactions).
            window.QueueInput(new InputEvent(InputKind.PointerUp, bc, 0, 0, 0f, KeyModifiers.None, PointerKind.Mouse, false, 2_100));
            host.RunFrame();
            host.RunFrame();
            bool remasked = strings.Resolve(scene.Paint(tn).Text) == "●●●●●●";
            bool focusKept = (scene.Flags(field) & NodeFlags.Focused) != 0;
            Check("W0f.1 PasswordBox Peek: no eye on focusing a populated box; typing fresh content shows it (F78D, width 30, TextSecondary); press-and-hold reveals, release re-masks + keeps focus",
                masked0 && noBtn0 && noBtnOnFocus && shown && w30 && glyph && peeked && remasked && focusKept,
                $"masked0={masked0} noBtn0={noBtn0} noBtnOnFocus={noBtnOnFocus} shown={shown} w30={w30} glyph={glyph} peeked={peeked} remasked={remasked} focus={focusKept}");
        }

        // ── W0f.1b — PasswordBox reveal arming (the REAL WinUI ButtonStates rule): the eye appears while TYPING a
        // new password from empty (CPasswordBox::OnContentChanged arms m_fCanShowRevealButton only on the
        // empty→non-empty transition, PasswordBox.cpp:366–377; CanInvokeRevealButton = canShow ∧ hasSpace ∧ focused,
        // PasswordBox.cpp:618–626), and does NOT appear from merely focusing a populated box
        // (CPasswordBox::OnGotFocus clears m_fCanShowRevealButton, PasswordBox.cpp:572–581). ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pwt", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe { Initial = "" };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);

            ClickNode(host, window, field);
            host.RunFrame();
            bool noBtnEmptyFocused = Roles(scene, AutomationRole.Button).Count == 0;   // focused but empty → collapsed

            // Type into the focused empty box — the empty→non-empty content change arms the reveal button
            // (PasswordBox.cpp:366–377) and it must actually RENDER (F78D glyph, the user-visible eye).
            foreach (char c in "abc") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            int countAfterType = Roles(scene, AutomationRole.Button).Count;
            bool glyphAfterType = HasGlyph(device, strings, "");   // the reveal eye (PasswordBox_themeresources.xaml:100)
            bool shownAfterType = countAfterType == 1 && glyphAfterType;

            // Clear to empty → the flag drops (OnContentChanged: IsEmpty ⇒ m_fCanShowRevealButton = FALSE,
            // PasswordBox.cpp:430–434) and the button unmounts.
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            Key(Keys.A, KeyModifiers.Ctrl);
            Key(Keys.Back);
            host.RunFrame();
            bool hiddenOnEmpty = Roles(scene, AutomationRole.Button).Count == 0;

            // Typing again from empty re-arms (the empty→non-empty transition, PasswordBox.cpp:366–377).
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'z'));
            host.RunFrame();
            host.RunFrame();
            bool rearmed = Roles(scene, AutomationRole.Button).Count == 1;
            Check("W0f.1b PasswordBox reveal arming: typing from empty shows the eye; focused-empty does not; emptying hides + retype re-arms",
                noBtnEmptyFocused && shownAfterType && hiddenOnEmpty && rearmed,
                $"noBtnEmptyFocused={noBtnEmptyFocused} shownAfterType={shownAfterType} (count={countAfterType} glyph={glyphAfterType}) hiddenOnEmpty={hiddenOnEmpty} rearmed={rearmed}");
        }

        // ── W0f.2 — PasswordRevealMode matrix: Hidden = no button + masked; Visible = no button + clear; copy blocked ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pwh", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe { Mode = PasswordRevealMode.Hidden };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            host.RunFrame();
            bool hiddenOk = strings.Resolve(scene.Paint(tn).Text) == "●●●●●●"
                && Roles(scene, AutomationRole.Button).Count == 0;

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-pwv", new Size2(420, 240), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fPasswordProbe { Mode = PasswordRevealMode.Visible };
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            var scene2 = host2.Scene;
            var field2 = FindRole(scene2, scene2.Root, AutomationRole.Text);
            var tn2 = TextVisual(scene2, field2);
            bool visibleOk = strings.Resolve(scene2.Paint(tn2).Text) == "secret";
            ClickNode(host2, window2, field2);
            host2.RunFrame();
            bool noBtnVisible = Roles(scene2, AutomationRole.Button).Count == 0;
            // Copy stays BLOCKED even while revealed (WinUI never allows copying out of a PasswordBox).
            void Key2(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window2.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host2.RunFrame();
            }
            Key2(Keys.A, KeyModifiers.Ctrl);
            Key2(Keys.C, KeyModifiers.Ctrl);
            bool copyBlocked = !((HeadlessClipboard)app2.Clipboard).TryGetText(out _);
            Check("W0f.2 PasswordRevealMode: Hidden = masked + no reveal button; Visible = clear text + no button, copy still blocked",
                hiddenOk && visibleOk && noBtnVisible && copyBlocked,
                $"hidden={hiddenOk} visible={visibleOk} noBtn={noBtnVisible} copyBlocked={copyBlocked}");
        }

        // ── W0f.3 — PasswordChar customization (WinUI PasswordBox.PasswordChar, default '●') ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-pwc", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fPasswordProbe { Char = '*' };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var tn = TextVisual(scene, FindRole(scene, scene.Root, AutomationRole.Text));
            Check("W0f.3 PasswordBox PasswordChar: a custom mask char renders per grapheme",
                strings.Resolve(scene.Paint(tn).Text) == "******", $"disp='{strings.Resolve(scene.Paint(tn).Text)}'");
        }

        // ── W0f.4 — NumberBox keyboard stepping: Up/Down=SmallChange, PageUp/PageDown=LargeChange, clamp + Text ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-nb", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fNumberProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            bool textSeed = root.Txt!.Peek() == "5";   // mount Value→Text sync (UpdateTextToValue)
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            Key(Keys.Up);
            bool up = root.Val!.Peek() == 6;                       // +SmallChange (NumberBox.cpp:538–541)
            Key(Keys.PageUp);
            bool pgUp = root.Val!.Peek() == 10;                    // +LargeChange 6+5=11 → clamp 10 (cpp:548–551)
            Key(Keys.Up);
            bool clamped = root.Val!.Peek() == 10;                 // at Maximum: stays (Coerce clamps)
            Key(Keys.PageDown);
            bool pgDn = root.Val!.Peek() == 5;                     // −LargeChange (cpp:553–556)
            Key(Keys.Down);
            bool dn = root.Val!.Peek() == 4 && root.Txt!.Peek() == "4";   // −SmallChange + Text reformat
            Check("W0f.4 NumberBox keys: Up/Down step SmallChange, PageUp/PageDown step LargeChange, clamped at bounds; Text carries the formatted value",
                textSeed && up && pgUp && clamped && pgDn && dn,
                $"seed={textSeed} up={up} pgUp={pgUp} clamp={clamped} pgDn={pgDn} dn={dn} txt='{root.Txt!.Peek()}'");
        }

        // ── W0f.5 — NumberBox: NaN does not step (cpp StepValue isnan guard); programmatic Text validates + clamps ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-nbn", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fNumberProbe { Initial = double.NaN };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Up));
            host.RunFrame();
            bool nanNoStep = double.IsNaN(root.Val!.Peek()) && root.Txt!.Peek() == "";   // cpp:604–629

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-nbt", new Size2(420, 240), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fNumberProbe();
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            root2.Txt!.Value = "7";    // external programmatic Text write — OnTextPropertyChanged → ValidateInput (cpp:325–340)
            host2.RunFrame();
            bool progSet = root2.Val!.Peek() == 7;
            root2.Txt!.Value = "25";   // out-of-range → InvalidInputOverwritten clamps to Maximum, text reformats
            host2.RunFrame();
            bool progClamp = root2.Val!.Peek() == 10 && root2.Txt!.Peek() == "10";
            Check("W0f.5 NumberBox: NaN value does not step; a programmatic Text write validates immediately (clamping out-of-range)",
                nanNoStep && progSet && progClamp,
                $"nanNoStep={nanNoStep} progSet={progSet} progClamp={progClamp} v={root2.Val!.Peek()} t='{root2.Txt!.Peek()}'");
        }

        // ── W0f.6 — NumberBox spin visuals: inline E70E/E70D @ TextSecondary in 32-wide cells; Compact EC8F indicator,
        //            popup opens on focus with 36×36 spin buttons and closes on blur ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-nbi", new Size2(420, 240), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fNumberProbe { Mode = NumberBoxSpinButtonPlacementMode.Inline };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            // Glyphs E70E/E70D (NumberBox.xaml:174–175) at TextControlButtonForeground = TextFillColorSecondary
            // #C5FFFFFF dark (TextBox_themeresources.xaml:45) — full-ARGB.
            bool glyphs = HasGlyph(device, strings, "") && HasGlyph(device, strings, "")
                && ColorClose(GlyphColor(device, strings, ""), Tok.TextSecondary, 0.004f)
                && ColorClose(GlyphColor(device, strings, ""), Tok.TextSecondary, 0.004f);
            var spins = Roles(scene, AutomationRole.Button);
            bool cells = spins.Count == 2
                && Near(scene.AbsoluteRect(spins[0]).W, 32f) && Near(scene.AbsoluteRect(spins[1]).W, 32f);   // MinWidth 32 (NumberBox.xaml:185)
            ClickNode(host, window, spins[0]);   // up spin → +SmallChange
            bool stepped = root.Val!.Peek() == 6;

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-nbc", new Size2(420, 320), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fNumberProbe { Mode = NumberBoxSpinButtonPlacementMode.Compact };
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            var scene2 = host2.Scene;
            bool indicator = HasGlyph(device2, strings, "");          // PopupIndicator (NumberBox.xaml:365)
            bool noPopup0 = Roles(scene2, AutomationRole.Button).Count == 0; // the indicator is NOT a button
            ClickNode(host2, window2, FindRole(scene2, scene2.Root, AutomationRole.Text));   // focus → popup opens
            host2.RunFrame();
            var popupBtns = Roles(scene2, AutomationRole.Button);
            bool popupOpen = popupBtns.Count == 2
                && Near(scene2.AbsoluteRect(popupBtns[0]).W, 36f) && Near(scene2.AbsoluteRect(popupBtns[0]).H, 36f);   // 36×36 (NumberBox.xaml:197–198)
            window2.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape));      // Escape blurs → popup closes
            for (int i = 0; i < 10; i++) host2.RunFrame();   // ride out the 83ms overlay close fade
            bool popupClosed = Roles(scene2, AutomationRole.Button).Count == 0;
            Check("W0f.6 NumberBox spin visuals: inline 32-wide E70E/E70D cells @ TextSecondary step on click; Compact = non-interactive EC8F indicator, popup (36×36 spins) opens on focus / closes on blur",
                glyphs && cells && stepped && indicator && noPopup0 && popupOpen && popupClosed,
                $"glyphs={glyphs} cells={cells} stepped={stepped} ind={indicator} no0={noPopup0} open={popupOpen} closed={popupClosed}");
        }

        // ── W0f.7 — AutoSuggestBox TextChanged-reason matrix + arrow preview + SuggestionChosen-per-selection ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-asb", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fAsbProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            foreach (char c in "ca") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();   // overlay content realizes
            bool typed = root.Changes.Count > 0 && root.Changes[^1] == ("ca", TextChangeReason.UserInput)
                && Roles(scene, AutomationRole.MenuItem).Count == 3;

            Key(Keys.Down);    // selection → item 0: preview text + SuggestionChosen (reason SuggestionChosen)
            host.RunFrame();   // the deferred TextChanged effect
            bool preview = root.Query!.Peek() == "Cascadia Code"
                && root.Chosen.Count == 1 && root.Chosen[0] == "Cascadia Code"
                && root.Changes[^1] == ("Cascadia Code", TextChangeReason.SuggestionChosen);

            Key(Keys.Down); Key(Keys.Down);   // → Calendar → Calculator
            Key(Keys.Down);                   // past the end → restore the typed text (reason ProgrammaticChange)
            host.RunFrame();
            bool restored = root.Query!.Peek() == "ca" && root.Chosen.Count == 3
                && root.Changes[^1] == ("ca", TextChangeReason.ProgrammaticChange);

            Key(Keys.Down);                   // back onto item 0
            int chosenBeforeEnter = root.Chosen.Count;
            Key(Keys.Enter);                  // submit: QueryText = field text; NO extra SuggestionChosen (cpp:1149–1160)
            bool submitted = root.Submitted.Count == 1 && root.Submitted[0] == "Cascadia Code"
                && root.Chosen.Count == chosenBeforeEnter;
            Check("W0f.7 AutoSuggestBox: TextChanged reasons (UserInput → SuggestionChosen preview → ProgrammaticChange restore); SuggestionChosen per selection move, NOT on Enter",
                typed && preview && restored && submitted,
                $"typed={typed} preview={preview} restored={restored} submit={submitted} chosen={root.Chosen.Count} changes={root.Changes.Count}");
        }

        // ── W0f.8 — AutoSuggestBox UpdateTextOnSelect=false: arrows cycle + SuggestionChosen, field text untouched ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-asbu", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fAsbProbe { UpdateTextOnSelect = false };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            foreach (char c in "cal") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down));
            host.RunFrame();
            bool ok = root.Query!.Peek() == "cal" && root.Chosen.Count == 1 && root.Chosen[0] == "Calendar";
            Check("W0f.8 AutoSuggestBox UpdateTextOnSelect=false: arrow keeps the typed text but still raises SuggestionChosen (cpp:2367 gates only the text write)",
                ok, $"query='{root.Query!.Peek()}' chosen={string.Join("|", root.Chosen)}");
        }

        // ── W0f.9 — AutoSuggestBox row click: SuggestionChosen BEFORE QuerySubmitted; item content inset 12 ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-asbc", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fAsbProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            ClickNode(host, window, FindRole(scene, scene.Root, AutomationRole.Text));
            foreach (char c in "cal") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            host.RunFrame();
            var rows = Roles(scene, AutomationRole.MenuItem);
            // Content inset: DefaultListViewItemStyle Padding 16,0,12,0 from the ITEM edge; plate inset 4 → 12 inside.
            var rowText = TextVisual(scene, rows[1]);
            float inset = scene.AbsoluteRect(rowText).X - scene.AbsoluteRect(rows[1]).X;
            bool insetOk = Near(inset, 12f);
            ClickNode(host, window, rows[1]);   // "Calculator"
            int ci = root.Order.IndexOf("C:Calculator");
            int qi = root.Order.IndexOf("Q:Calculator");
            bool ordered = ci >= 0 && qi > ci && root.Query!.Peek() == "Calculator";
            Check("W0f.9 AutoSuggestBox click: SelectionChanged → SuggestionChosen → QuerySubmitted sequential; row content inset 12 (ListViewItem 16,0,12,0 minus the 4px plate)",
                insetOk && ordered, $"inset={inset:0.#} order=[{string.Join(",", root.Order)}]");
        }

        // ── W0f.10 — editable ComboBox: prefix auto-match autocompletes with the suffix selected, commit on Enter ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-cmb", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fComboProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            var tn = TextVisual(scene, field);
            ClickNode(host, window, field);
            foreach (char c in "gr") window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host.RunFrame();
            // ProcessSearch prefix hit → auto-complete "Green" with the completed suffix "een" selected
            // (ComboBox_Partial.cpp:4108–4119 + UpdateEditableTextBox :1543–1551); SelectedIndex NOT committed while
            // typing (SelectionChangedTrigger default = Committed).
            var selRects = scene.GetTextEditSelectionRects(tn);
            bool completed = root.Txt!.Peek() == "Green" && root.Sel!.Peek() == -1
                && selRects.Length == 1 && Near(selRects[0].X, 2f * Adv) && Near(selRects[0].W, 3f * Adv);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
            host.RunFrame();
            bool committed = root.Sel!.Peek() == 1 && root.Submitted.Count == 0;   // a search hit commits WITHOUT TextSubmitted
            Check("W0f.10 editable ComboBox: typing 'gr' auto-completes to 'Green' with 'een' selected (no commit); Enter commits the match, no TextSubmitted",
                completed && committed,
                $"txt='{root.Txt!.Peek()}' sel={root.Sel!.Peek()} rects={selRects.Length} committed={committed}");
        }

        // ── W0f.11 — editable ComboBox TextSubmitted: unhandled → custom value (sel −1); handled → selection kept ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-cmbt", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fComboProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            void Key(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host.RunFrame();
            }
            void Type(string s)
            {
                foreach (char c in s) window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
                host.RunFrame();
            }
            ClickNode(host, window, field);
            Type("gr"); Key(Keys.Enter);                       // commit "Green" (sel 1)
            Key(Keys.A, KeyModifiers.Ctrl); Type("xyz"); Key(Keys.Enter);
            bool custom = root.Submitted.Count == 1 && root.Submitted[0] == "xyz"
                && root.Sel!.Peek() == -1 && root.Txt!.Peek() == "xyz";   // unhandled: custom value, no selection (cpp:2540–2543)

            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("w0f-cmbh", new Size2(420, 320), 1f)); window2.Show();
            var device2 = new HeadlessGpuDevice();
            var fonts2 = new HeadlessFontSystem(strings);
            var root2 = new W0fComboProbe { HandleSubmit = true };
            using var host2 = new AppHost(app2, window2, device2, fonts2, strings, root2);
            host2.RunFrame();
            var scene2 = host2.Scene;
            var field2 = FindRole(scene2, scene2.Root, AutomationRole.Text);
            void Key2(int key, KeyModifiers mods = KeyModifiers.None)
            {
                window2.QueueInput(new InputEvent(InputKind.Key, default, 0, key, 0f, mods));
                host2.RunFrame();
            }
            ClickNode(host2, window2, field2);
            foreach (char c in "gr") window2.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host2.RunFrame();
            Key2(Keys.Enter);                                  // sel = 1
            Key2(Keys.A, KeyModifiers.Ctrl);
            foreach (char c in "xyz") window2.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
            host2.RunFrame();
            Key2(Keys.Enter);
            bool handled = root2.Submitted.Count == 1 && root2.Sel!.Peek() == 1 && root2.Txt!.Peek() == "xyz";
            Check("W0f.11 editable ComboBox TextSubmitted: unhandled custom value → SelectedIndex −1 + text kept; handled (return true) → selection untouched",
                custom && handled,
                $"custom={custom} (sel={root.Sel!.Peek()} txt='{root.Txt!.Peek()}') handled={handled} (sel2={root2.Sel!.Peek()})");
        }

        // ── W0f.12 — editable ComboBox: arrows preview without committing; Escape restores the pre-edit text ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-cmbe", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fComboProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var scene = host.Scene;
            var field = FindRole(scene, scene.Root, AutomationRole.Text);
            void Key(int key)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
                host.RunFrame();
            }
            ClickNode(host, window, field);   // focus snapshot: "" / sel −1
            Key(Keys.Down);
            bool prev1 = root.Txt!.Peek() == "Red" && root.Sel!.Peek() == -1;     // preview select-all, NO commit (cpp:2939–2949)
            Key(Keys.Down);
            bool prev2 = root.Txt!.Peek() == "Green" && root.Sel!.Peek() == -1;
            Key(Keys.Escape);
            host.RunFrame();
            bool reverted = root.Txt!.Peek() == "" && root.Sel!.Peek() == -1
                && (scene.Flags(field) & NodeFlags.Focused) == 0;                  // Escape reverts + blurs, no commit
            Check("W0f.12 editable ComboBox: Up/Down preview into the field without committing; Escape restores the pre-edit text + selection",
                prev1 && prev2 && reverted,
                $"prev1={prev1} prev2={prev2} reverted={reverted} txt='{root.Txt!.Peek()}' sel={root.Sel!.Peek()}");
        }

        // ── W0f.13 — header/description chrome: TextControlHeaderForeground (+disabled) and the Description row ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("w0f-hdr", new Size2(420, 320), 1f)); window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new W0fStaticProbe
            {
                Build = () => new BoxEl
                {
                    Direction = 1, Gap = 12,
                    Children =
                    [
                        TextBox.Create(options: new TextBox.TextBoxOptions { Placeholder = "ph", Width = 280f, Header = "Email", Description = "Helper" }),
                        PasswordBox.Create("Password", 280f, "Pw", isEnabled: false,
                                           password: new Signal<string>("secret")),
                    ],
                },
            };
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            // Header = TextControlHeaderForeground (BaseHigh #FFFFFFFF dark, generic.xaml:886+207); Description =
            // SystemControlDescriptionTextForegroundBrush (BaseMedium #99FFFFFF dark, generic.xaml:327+209); disabled
            // header = TextControlHeaderForegroundDisabled (BaseMediumLow #66FFFFFF dark, generic.xaml:887+211);
            // disabled field TEXT = TemporaryTextFillColorDisabled #5DFEFEFE (TextBox_themeresources.xaml:22+34).
            bool header = ColorClose(GlyphColor(device, strings, "Email"), Tok.TextControlHeaderForeground, 0.004f);
            bool desc = ColorClose(GlyphColor(device, strings, "Helper"), Tok.TextControlDescriptionForeground, 0.004f);
            bool disHeader = ColorClose(GlyphColor(device, strings, "Pw"), Tok.TextControlHeaderForegroundDisabled, 0.004f);
            bool disText = ColorClose(GlyphColor(device, strings, "●●●●●●"), Tok.TextControlForegroundDisabled, 0.004f);
            Check("W0f.13 header/description chrome: header BaseHigh, description BaseMedium, disabled header BaseMediumLow, disabled field text #5DFEFEFE (full ARGB)",
                header && desc && disHeader && disText,
                $"header={header} desc={desc} disHeader={disHeader} disText={disText}");
        }
    }

    static void TextInputChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("edit", new Size2(320, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new EditTextProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var field = FindRole(host.Scene, host.Scene.Root, AutomationRole.Text);
        bool found = !field.IsNull;
        var center = CenterOf(host.Scene, field);

        // Focus the field, then type "hi5" via WM_CHAR-style events.
        window.QueueInput(new InputEvent(InputKind.PointerDown, center, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, center, 0, 0));
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'h'));
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'i'));
        window.QueueInput(new InputEvent(InputKind.Char, default, 0, '5'));
        host.RunFrame();
        string typed = root.Text!.Peek();

        // Backspace removes the last char.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Back));
        host.RunFrame();
        string afterBack = root.Text!.Peek();

        // Sanitize clamps to 8 chars: type a long run.
        for (int i = 0; i < 12; i++) window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'x'));
        host.RunFrame();
        string clamped = root.Text!.Peek();

        Check("63. text input: focused field accepts WM_CHAR, backspace, sanitize",
            found && typed == "hi5" && afterBack == "hi" && clamped.Length == 8,
            $"typed='{typed}' back='{afterBack}' clampedLen={clamped.Length}");
    }

    static void G5fPopupToastChecks(StringTable strings)
    {
        // gate.popup.controlled — isOpen drives open/close; light-dismiss writes the signal back + onOpenChanged(false)
        // once; a programmatic close does NOT echo onOpenChanged.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5f-popup", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var probe = new PopupCtlProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, probe, frameTime: clock);
            void Settle() { for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); } }
            host.RunFrame();
            var svc = probe.Service!;

            probe.Open.Value = true; Settle();
            bool opened = svc.AnyOpen;

            int changesBeforeProg = probe.OpenChanges;
            probe.Open.Value = false; Settle();
            bool progClosed = !svc.AnyOpen;
            bool progNoEcho = probe.OpenChanges == changesBeforeProg;   // programmatic close does not fire onOpenChanged

            probe.Open.Value = true; Settle();
            bool reopened = svc.AnyOpen;
            int changesBeforeDismiss = probe.OpenChanges;
            // Light-dismiss: press a blank point on the full-bleed scrim (outside the popup + anchor).
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(420, 320), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(420, 320), 0, 0));
            Settle();
            bool dismissClosed = !svc.AnyOpen;
            bool signalWrittenBack = !probe.Open.Peek();
            bool echoedOnce = probe.OpenChanges == changesBeforeDismiss + 1 && !probe.LastChanged;

            Check("gate.popup.controlled", opened && progClosed && progNoEcho && reopened && dismissClosed && signalWrittenBack && echoedOnce,
                $"open={opened} progClosed={progClosed} progNoEcho={progNoEcho} reopen={reopened} dismissClosed={dismissClosed} writeback={signalWrittenBack} echoOnce={echoedOnce}");
        }

        // gate.popup.anchor — the primitive rides the FlyoutPositioner: node-anchored (live-follow eligible), placed
        // below a top anchor, and FLIPS above when it would overflow below a bottom anchor.
        {
            bool belowOk = false, nodeAnchored = false;
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g5f-anchor-top", new Size2(480, 360), 1f));
                window.Show();
                var probe = new PopupCtlProbe { AnchorLow = false };
                var clock = new ManualFrameTimeSource();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
                host.RunFrame();
                probe.Open.Value = true;
                for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); }
                var impl = (OverlayServiceImpl)probe.Service!;
                if (impl.Entries.Count > 0)
                {
                    var e = impl.Entries[0];
                    nodeAnchored = e.AnchorRect is null;   // node-anchored ⇒ AfterAnimations live-follows it
                    var aRect = host.Scene.AbsoluteRect(probe.Anchor);
                    var sRect = host.Scene.AbsoluteRect(e.SurfaceNode);
                    belowOk = !e.OpensUp && sRect.Y >= aRect.Y + aRect.H - 1f;
                }
            }
            bool flipOk = false;
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g5f-anchor-bottom", new Size2(480, 360), 1f));
                window.Show();
                var probe = new PopupCtlProbe { AnchorLow = true };
                var clock = new ManualFrameTimeSource();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
                host.RunFrame();
                probe.Open.Value = true;
                for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); }
                var impl = (OverlayServiceImpl)probe.Service!;
                if (impl.Entries.Count > 0) flipOk = impl.Entries[0].OpensUp;   // flipped ABOVE the bottom anchor
            }
            Check("gate.popup.anchor", nodeAnchored && belowOk && flipOk,
                $"nodeAnchored={nodeAnchored} placedBelow={belowOk} flipUp={flipOk}");
        }

        // gate.flyout.attach-chains-onclick — an anchor with its OWN OnClick keeps BOTH behaviours (its handler + open).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5f-flyout", new Size2(480, 360), 1f));
            window.Show();
            var probe = new FlyoutAttachProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
            host.RunFrame();
            var svc = probe.Service!;
            var c = CenterOf(host.Scene, probe.Anchor);
            window.QueueInput(new InputEvent(InputKind.PointerDown, c, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, c, 0, 0));
            for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); }
            bool ownRan = probe.AnchorClicks == 1;
            bool opened = svc.AnyOpen;
            Check("gate.flyout.attach-chains-onclick", ownRan && opened,
                $"ownClick={probe.AnchorClicks} opened={opened}");
        }

        // gate.overlay.pins-anchor — an open PinsAnchor overlay reports its anchor scope pinned; close unpins; a
        // PinsAnchor=false overlay never pins. PinEpoch bumps on open/close so auto-hide consumers can subscribe.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("g5f-pin", new Size2(480, 360), 1f));
            window.Show();
            var probe = new OverlayProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
            void Settle() { for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); } }
            host.RunFrame();
            var svc = probe.Service!;
            var scope = host.Scene.Parent(probe.Anchor);   // the anchor's auto-hide SCOPE (an ancestor)
            Func<Element> body = () => new BoxEl { Width = 100, Height = 60, Fill = Tok.FillCardDefault, Children = [Ui.Text("pinned")] };

            int epoch0 = svc.PinEpoch.Peek();
            var h = svc.Open(() => probe.Anchor, body, FlyoutPlacement.BottomLeft);   // default PinsAnchor = true
            host.RunFrame();
            bool pinnedAnchor = svc.IsAnchorPinned(probe.Anchor);
            bool pinnedScope = svc.IsAnchorPinned(scope);
            bool epochBumpedOpen = svc.PinEpoch.Peek() > epoch0;

            int epoch1 = svc.PinEpoch.Peek();
            h.Close(); Settle();
            bool unpinnedAfterClose = !svc.IsAnchorPinned(probe.Anchor);
            bool epochBumpedClose = svc.PinEpoch.Peek() > epoch1;

            var h2 = svc.Open(() => probe.Anchor, body, FlyoutPlacement.BottomLeft, new PopupOptions { PinsAnchor = false });
            host.RunFrame();
            bool notPinnedWhenOptedOut = !svc.IsAnchorPinned(probe.Anchor);
            h2.Close(); Settle();

            Check("gate.overlay.pins-anchor",
                pinnedAnchor && pinnedScope && epochBumpedOpen && unpinnedAfterClose && epochBumpedClose && notPinnedWhenOptedOut,
                $"anchor={pinnedAnchor} scope={pinnedScope} epochOpen={epochBumpedOpen} unpin={unpinnedAfterClose} epochClose={epochBumpedClose} optOut={notPinnedWhenOptedOut}");
        }

        // gate.menu.safe-triangle — a pointer path THROUGH the hover-intent triangle (launch point → submenu near
        // edge) stays inside (submenu kept open); a path OUTSIDE it is not.
        {
            // Launch point left of the submenu; the submenu near edge is the segment (100,0)→(100,100).
            var apex = new Point2(0f, 50f);
            var top = new Point2(100f, 0f);
            var bot = new Point2(100f, 100f);
            bool through = MenuSafeTriangle.Contains(apex, top, bot, new Point2(20f, 50f))
                        && MenuSafeTriangle.Contains(apex, top, bot, new Point2(55f, 48f))
                        && MenuSafeTriangle.Contains(apex, top, bot, new Point2(90f, 45f));
            bool outside = !MenuSafeTriangle.Contains(apex, top, bot, new Point2(55f, 95f))    // veered down toward a sibling
                        && !MenuSafeTriangle.Contains(apex, top, bot, new Point2(50f, -20f));  // veered up, off-aim
            Check("gate.menu.safe-triangle", through && outside, $"through={through} outside={outside}");
        }

        G5fToastChecks(strings);
    }

    static void OverlayChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("overlay", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new OverlayProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var svc = root.Service!;
        Func<Element> menu = () => new BoxEl
        {
            Width = 140, Direction = 1,
            Children =
            [
                new BoxEl
                {
                    Height = 32, Role = AutomationRole.MenuItem, OnClick = () => { root.Selected = 7; svc.CloseTop(); },
                    Children = [Ui.Text("Item A")],
                },
            ],
        };

        // Let the close fade animation (83ms) settle, then the OverlayCloseDriver removes the popup.
        void Settle() { for (int i = 0; i < 16; i++) host.RunFrame(); }

        // Open → the popup (a MenuItem) appears in the scene.
        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();
        bool opened = !FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Escape closes it (via the dispatcher key-preview hook).
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape));
        host.RunFrame();
        Settle();
        bool escClosed = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Re-open → light dismiss: a click away from the popup hits the scrim and closes it.
        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(460, 350), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(460, 350), 0, 0));
        host.RunFrame();
        Settle();
        bool lightDismissed = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Re-open → click the item: invokes its command and closes.
        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();
        var item = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
        var ic = CenterOf(host.Scene, item);
        window.QueueInput(new InputEvent(InputKind.PointerDown, ic, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, ic, 0, 0));
        host.RunFrame();
        Settle();
        bool invoked = root.Selected == 7 && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

        // Caller-owned overlays (ToolTip) must not create an input-blocking scrim. The click reaches the page beneath
        // while the bubble stays open; a light-dismiss flyout still uses the blocking path above.
        int bg0 = root.BackgroundClicks;
        var passive = svc.Open(() => root.Anchor,
            () => new BoxEl { Width = 64, Height = 28, Fill = Tok.FillCardDefault, Children = [Ui.Text("tip")] },
            FlyoutPlacement.BottomLeft,
            new PopupOptions(DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        host.RunFrame();
        var blank = new Point2(190, 110);
        window.QueueInput(new InputEvent(InputKind.PointerDown, blank, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, blank, 0, 0));
        host.RunFrame();
        bool passiveDoesNotBlock = passive.IsOpen && root.BackgroundClicks == bg0 + 1;
        passive.Close(); Settle();

        // Global focus exit: unhandled Escape clears keyboard focus, and a non-focusable page click clears it too.
        var ac = CenterOf(host.Scene, root.Anchor);
        window.QueueInput(new InputEvent(InputKind.PointerDown, ac, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, ac, 0, 0));
        host.RunFrame();
        bool focused = host.Input.Focused == root.Anchor;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape)); host.RunFrame();
        bool escapeClears = host.Input.Focused.IsNull;
        window.QueueInput(new InputEvent(InputKind.PointerDown, ac, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, ac, 0, 0)); host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.PointerDown, blank, 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, blank, 0, 0)); host.RunFrame();
        bool clickAwayClears = host.Input.Focused.IsNull;

        Check("64. overlay: anchored flyout opens, Escape + light-dismiss close, item invokes",
            opened && escClosed && lightDismissed && invoked && passiveDoesNotBlock && focused && escapeClears && clickAwayClears,
            $"open={opened} esc={escClosed} dismiss={lightDismissed} invoke={invoked} passiveInput={passiveDoesNotBlock} focus={focused} escBlur={escapeClears} clickBlur={clickAwayClears}");
    }

    static void OverlayAnimationChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ovanim", new Size2(480, 400), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new OverlayProbe();
        var clock = new ManualFrameTimeSource();
        using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
        host.RunFrame();

        var svc = root.Service!;
        Func<Element> menu = () => MenuFlyout.Create(new[]
        {
            new MenuFlyoutItem("One"), new MenuFlyoutItem("Two"), new MenuFlyoutItem("Three"),
            new MenuFlyoutItem("Four"), new MenuFlyoutItem("Five"),
        }, () => svc.CloseTop());

        NodeHandle SurfaceOf(NodeHandle n)
        {
            for (; !n.IsNull; n = host.Scene.Parent(n))
            {
                if (host.Scene.TryGetAcrylic(n, out _)) return n;
                // D67 menu chrome: the acrylic lives on the stretch PLATE (first child); the channels live on its parent.
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c))
                    if (host.Scene.TryGetAcrylic(c, out _)) return n;
            }
            return NodeHandle.Null;
        }
        NodeHandle SmokeScrim()
        {
            NodeHandle best = NodeHandle.Null;
            float bestArea = 0f;
            void Walk(NodeHandle n)
            {
                if (n.IsNull) return;
                ref var p = ref host.Scene.Paint(n);
                var r = host.Scene.AbsoluteRect(n);
                float area = r.W * r.H;
                if (p.Fill.A > 0.1f && r.W >= 400f && r.H >= 320f && area > bestArea)
                {
                    best = n;
                    bestArea = area;
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
            }
            Walk(host.Scene.Root);
            return best;
        }

        (bool Fading, bool Settled, bool NoPopBack, float MaxSurface, float MaxScrim) CloseChrome(PopupChrome chrome)
        {
            NodeHandle body = NodeHandle.Null;
            Func<Element> content = () => new BoxEl
            {
                Width = chrome == PopupChrome.TeachingTip ? 320f : 260f,
                Height = chrome == PopupChrome.TeachingTip ? 96f : 88f,
                Fill = Tok.FillCardDefault,
                OnRealized = h => body = h,
                Children = [new TextEl("overlay body") { Color = Tok.TextPrimary }],
            };
            var options = new PopupOptions(
                FocusTrap: chrome == PopupChrome.Modal,
                DismissBehavior: chrome == PopupChrome.Modal ? DismissBehavior.Modal : DismissBehavior.LightDismiss,
                Chrome: chrome);
            svc.Open(() => root.Anchor, content, FlyoutPlacement.BottomLeft, options);
            host.RunFrame();
            for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }

            NodeHandle surface = NodeHandle.Null;
            if (!body.IsNull)
            {
                if (chrome == PopupChrome.Flyout)
                {
                    surface = SurfaceOf(body);
                }
                else
                {
                    for (var n = host.Scene.Parent(body); !n.IsNull; n = host.Scene.Parent(n))
                    {
                        if ((host.Scene.Flags(n) & NodeFlags.ClipsToBounds) != 0) { surface = n; break; }
                    }
                }
            }
            NodeHandle scrim = chrome == PopupChrome.Modal ? SmokeScrim() : NodeHandle.Null;

            svc.CloseTop();
            host.RunFrame();
            clock.Advance(16f);
            host.RunFrame();
            float closeOp = !surface.IsNull && host.Scene.IsLive(surface) ? host.Scene.Paint(surface).Opacity : 0f;
            float closeScrim = !scrim.IsNull && host.Scene.IsLive(scrim) ? host.Scene.Paint(scrim).Opacity : 0f;
            // The TeachingTip close is the muxc CONTRACT animation: scale 1 → 20/Width over 200ms
            // cubic-bezier(0.7,0,1,0.5) with NO opacity keyframes (TeachingTip.cpp:1695-1712 contractAnimation;
            // TeachingTip.h:235 + :306-307) — the host-owned close is a live scale track, not a fade (the ease-in
            // curve has barely moved at a 16ms sample, so assert the seeded track; 64k samples the kinematics).
            bool fading = chrome == PopupChrome.TeachingTip
                ? !surface.IsNull && host.Scene.IsLive(surface) && host.Animation.HasTracks(surface)
                : !surface.IsNull && host.Scene.IsLive(surface) && closeOp < 0.99f
                    && (chrome != PopupChrome.Modal || (!scrim.IsNull && host.Scene.IsLive(scrim) && closeScrim < 0.99f));

            for (int i = 0; i < 16; i++) { clock.Advance(16f); host.RunFrame(); }
            bool settled = surface.IsNull || !host.Scene.IsLive(surface) || !host.Animation.HasTracks(surface);
            if (chrome == PopupChrome.Modal && !scrim.IsNull && host.Scene.IsLive(scrim))
                settled &= !host.Animation.HasTracks(scrim);

            bool noPopBack = true;
            float maxSurface = 0f;
            float maxScrim = 0f;
            for (int i = 0; i < 6; i++)
            {
                clock.Advance(16f);
                host.RunFrame();
                if (!surface.IsNull && host.Scene.IsLive(surface))
                {
                    float liveOp = host.Scene.Paint(surface).Opacity;
                    maxSurface = MathF.Max(maxSurface, liveOp);
                    if (liveOp > 0.01f) noPopBack = false;
                }
                if (!scrim.IsNull && host.Scene.IsLive(scrim))
                {
                    float liveScrim = host.Scene.Paint(scrim).Opacity;
                    maxScrim = MathF.Max(maxScrim, liveScrim);
                    if (liveScrim > 0.01f) noPopBack = false;
                }
            }
            return (fading, settled, noPopBack, maxSurface, maxScrim);
        }

        svc.Open(() => root.Anchor, menu, FlyoutPlacement.BottomLeft);
        host.RunFrame();   // mount + seed the authored clip-rect reveal + fade (seed runs in a layout effect)
        clock.Advance(16f);
        host.RunFrame();   // tick the animation once → the first ClipRect lands
        var surface = SurfaceOf(FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem));
        float fullH = surface.IsNull ? 0f : host.Scene.Bounds(surface).H;
        RectF cr1 = surface.IsNull ? RectF.Infinite : host.Scene.Paint(surface).ClipRect;   // mid-reveal: finite, < full height
        bool revealing = !surface.IsNull && !cr1.IsInfinite && cr1.H > 1f && cr1.H < fullH - 2f;

        for (int i = 0; i < 24; i++) { clock.Advance(16f); host.RunFrame(); }             // open settles
        RectF cr2 = surface.IsNull ? RectF.Infinite : host.Scene.Paint(surface).ClipRect;
        bool revealed = cr2.IsInfinite;

        // Close → the surface fades (opacity animates down) while staying on top (not removed instantly).
        svc.CloseTop();
        host.RunFrame();
        clock.Advance(16f);
        host.RunFrame();
        float op = host.Scene.IsLive(surface) ? host.Scene.Paint(surface).Opacity : 0f;
        bool fading = host.Scene.IsLive(surface) && op < 0.99f;

        Check("64d. flyout: open clip-reveals (authored ClipRect) then close fades", revealing && revealed && fading,
            $"clipH {cr1.H:0}/{fullH:0}→{(cr2.IsInfinite ? "∞" : cr2.H.ToString("0"))} closeOpacity={op:0.00}");
        // Regression for overlay close "pop back": after the close track settles, later frames must not record the
        // surface visible while structural overlay removal catches up.
        for (int i = 0; i < 10; i++) { clock.Advance(16f); host.RunFrame(); }
        bool settled = surface.IsNull || !host.Scene.IsLive(surface) || !host.Animation.HasTracks(surface);
        bool noPopBack = true;
        float maxAfterSettle = 0f;
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(16f);
            host.RunFrame();
            if (!surface.IsNull && host.Scene.IsLive(surface))
            {
                float liveOp = host.Scene.Paint(surface).Opacity;
                maxAfterSettle = MathF.Max(maxAfterSettle, liveOp);
                if (liveOp > 0.01f) noPopBack = false;
            }
        }
        Check("64e. overlay close settles without a one-frame pop-back", settled && noPopBack,
            $"settled={settled} maxOpacityAfterSettle={maxAfterSettle:0.00} live={(!surface.IsNull && host.Scene.IsLive(surface))}");

        var flyoutClose = CloseChrome(PopupChrome.Flyout);
        var modalClose = CloseChrome(PopupChrome.Modal);
        var teachingClose = CloseChrome(PopupChrome.TeachingTip);
        bool allChrome = flyoutClose.Fading && flyoutClose.Settled && flyoutClose.NoPopBack
            && modalClose.Fading && modalClose.Settled && modalClose.NoPopBack
            && teachingClose.Fading && teachingClose.Settled && teachingClose.NoPopBack;
        Check("64f. overlay close lifecycle is host-owned for flyout, modal scrim+card, and TeachingTip",
            allChrome,
            $"flyout=({flyoutClose.Fading},{flyoutClose.Settled},max={flyoutClose.MaxSurface:0.00}) " +
            $"modal=({modalClose.Fading},{modalClose.Settled},card={modalClose.MaxSurface:0.00},scrim={modalClose.MaxScrim:0.00}) " +
            $"tip=({teachingClose.Fading},{teachingClose.Settled},max={teachingClose.MaxSurface:0.00})");

        // ── 64g–64l. Per-kind WinUI motion: KEYFRAME SAMPLES at known t (ManualFrameTimeSource). ──
        // cubic-bezier(0,0,0,1) (the WinUI "control fast out / slow in" curve used by MenuPopup/Split open) has the
        // closed form y(x) = 3·x^(2/3) − 2x (P1.x = P2.x = 0 ⇒ x(s) = s³): E(48/250)=0.6144, E(128/250)=0.8960,
        // E(48/167)=0.7320 — the expected sample values asserted below.
        {
            (NodeHandle Surface, OverlayHandle Handle) OpenKind(PopupChrome chrome, float w = 240f, float h = 88f)
            {
                NodeHandle body = NodeHandle.Null;
                var hd = svc.Open(() => root.Anchor,
                    () => new BoxEl { Width = w, Height = h, Fill = Tok.FillCardDefault, OnRealized = n => body = n },
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(Chrome: chrome));
                host.RunFrame();   // mount + place + seed (layout effect)
                host.RunFrame();   // compose the t=0 keyframes
                NodeHandle s = SurfaceOf(body);
                if (s.IsNull)      // bare chromes (Raw/TeachingTip) have no acrylic — find the clipping surface box
                    for (var n = host.Scene.Parent(body); !n.IsNull; n = host.Scene.Parent(n))
                        if ((host.Scene.Flags(n) & NodeFlags.ClipsToBounds) != 0) { s = n; break; }
                return (s, hd);
            }
            void SettleAll() { for (int i = 0; i < 40; i++) { clock.Advance(16f); host.RunFrame(); } }

            // 64g — menus (MenuPopupThemeTransition load, LayoutTransition_partial.cpp:465-473): a DOWNWARD menu
            // (AnimationDirection_Top) slides its content TranslateY from −openedLength·ClosedRatio(0.5) → 0 while a
            // counter-translated clip animates — node-local, the NEAR/TOP clip edge (ClipT = ClipRect.Y) slides
            // slide → 0 with the BOTTOM edge resting at H — over s_OpenDuration=250ms cubic-bezier(0,0,0,1), with NO
            // presenter opacity at load. Unload = 83ms linear fade.
            {
                var (s, _) = OpenKind(PopupChrome.Flyout);
                float menuH = host.Scene.Bounds(s).H, slide = menuH * 0.5f;
                var p0 = host.Scene.Paint(s);
                // t=0: content translated up by slide; the top clip edge sits at slide (near edge glued), bottom = H.
                bool t0 = !p0.ClipRect.IsInfinite && Near(p0.ClipRect.Y, slide, 1f) && Near(p0.ClipRect.Bottom, menuH, 1f)
                    && Near(p0.LocalTransform.Dy, -slide, 0.5f) && p0.Opacity > 0.99f;
                clock.Advance(128f); host.RunFrame();
                var p1 = host.Scene.Paint(s);
                // t=128: ClipT = slide·(1−E) and TranslateY = −slide·(1−E), E(128/250)=0.8960 for cb(0,0,0,1).
                bool t128 = !p1.ClipRect.IsInfinite && Near(p1.ClipRect.Y, slide * (1f - 0.8960f), 1f)
                    && Near(p1.LocalTransform.Dy, -slide * (1f - 0.8960f), 1f) && p1.Opacity > 0.99f;
                clock.Advance(160f); host.RunFrame();   // t=288 > 250 → settled
                var p2 = host.Scene.Paint(s);
                bool tEnd = p2.ClipRect.IsInfinite && Near(p2.LocalTransform.Dy, 0f, 0.1f);
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();
                float op48 = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close48 = Near(op48, 1f - 48f / 83f, 0.02f);   // 83ms LINEAR fade → 0.4217 at t=48
                SettleAll();
                Check("64g. menu motion: content TranslateY −slide→0 + top clip ClipT slide→0 over 250ms cb(0,0,0,1), no open fade; close = 83ms linear fade",
                    t0 && t128 && tEnd && close48,
                    $"t0={t0} (clipT={p0.ClipRect.Y:0.0}/{slide:0.0} clipB={p0.ClipRect.Bottom:0.0} dy={p0.LocalTransform.Dy:0.0}/{-slide:0.0}) t128={t128} " +
                    $"(clipT={p1.ClipRect.Y:0.0}≈{slide * (1f - 0.8960f):0.0} dy={p1.LocalTransform.Dy:0.0}) end={tEnd} close48={close48} (op={op48:0.000}≈{1f - 48f / 83f:0.000})");
            }

            // 64h — ComboBox dropdown (SplitOpen/SplitCloseThemeAnimation, generic.xaml:9047/9056). Open: the clip
            // grows from closedRatio 0.50 over 250ms cubic-bezier(0,0,0,1) with NO content translate (the template
            // sets no ContentTranslationOffset, ThemeAnimations.cpp:692-711) and NO fade (opacity pinned 1,
            // cpp:684). Close: clip collapses to closedRatio 0.15 over s_CloseDuration=167ms (cpp:741 + 826-828),
            // opacity fades only during the LAST 83ms (s_OpacityChangeBeginTime = 167−83 = 84,
            // SplitCloseThemeAnimation_Partial.h:16-18).
            {
                var (s, _) = OpenKind(PopupChrome.Dropdown);
                float ddH = host.Scene.Bounds(s).H;
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.ClipRect.H, ddH * 0.5f, 1f) && Near(p0.LocalTransform.Dy, 0f, 0.1f) && p0.Opacity > 0.99f;
                clock.Advance(128f); host.RunFrame();
                var p1 = host.Scene.Paint(s);
                bool t128 = Near(p1.ClipRect.H, ddH * (0.5f + 0.5f * 0.8960f), 1f)
                    && Near(p1.LocalTransform.Dy, 0f, 0.1f) && p1.Opacity > 0.99f;
                clock.Advance(160f); host.RunFrame();
                bool tEnd = host.Scene.Paint(s).ClipRect.IsInfinite;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();
                var c48 = host.Scene.Paint(s);
                // t=48 < 84ms begin time → STILL FULLY OPAQUE while the clip is already collapsing: E(48/167)=0.7320
                // → clipH = H·(1 − 0.85·0.7320) = 0.3778·H.
                bool close48 = c48.Opacity > 0.99f && Near(c48.ClipRect.H, ddH * (1f - 0.85f * 0.7320f), 1.5f);
                clock.Advance(64f); host.RunFrame();   // t=112 → fade progress (112−84)/83 = 0.3373
                float op112 = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close112 = Near(op112, 1f - (112f - 84f) / 83f, 0.03f);
                SettleAll();
                Check("64h. ComboBox dropdown samples: SplitOpen clip 0.5H→H 250ms no translate/fade; SplitClose 167ms clip→0.15H + fade only after 84ms",
                    t0 && t128 && tEnd && close48 && close112,
                    $"t0={t0} (clipH={p0.ClipRect.H:0.0}/{ddH * 0.5f:0.0}) t128={t128} (clipH={p1.ClipRect.H:0.0}≈{ddH * 0.948f:0.0}) " +
                    $"end={tEnd} close48={close48} (op={c48.Opacity:0.00} clipH={c48.ClipRect.H:0.0}≈{ddH * 0.3778f:0.0}) close112={close112} (op={op112:0.000})");
            }

            // 64i — Flyout/FlyoutPresenter (PopupThemeTransition → TAS_SHOWPOPUP/TAS_HIDEPOPUP; FlyoutBase attaches it,
            // FlyoutBase_Partial.cpp:1968-1975). [ASSERTION-CHANGE: the OS TAS_SHOWPOPUP timeline is uxtheme-RUNTIME (not
            // in the mux source — only g_entranceThemeOffset=50 + the per-side axis are pinned), so the entrance is aligned
            // to the verified Fluent flyout-family curve: TRANSLATE ±50→0 (axis+sign from the effective placement) over
            // 167ms cubic-bezier(0,0,0,1) with OPACITY 0→1 over the SAME 167ms LINEAR (no 83ms hold); hide = OPACITY →0
            // over 83ms linear, no slide (TAS_HIDEPOPUP offset 0→0).]
            {
                var (s, _) = OpenKind(PopupChrome.Popup);
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.LocalTransform.Dy, -50f, 1f) && p0.Opacity < 0.01f;
                clock.Advance(48f); host.RunFrame();
                var p1 = host.Scene.Paint(s);
                bool t48 = Near(p1.Opacity, 48f / 167f, 0.06f)     // opacity fades from the FIRST tick (no hold)
                    && p1.LocalTransform.Dy > -30f && p1.LocalTransform.Dy < -3f;   // 0,0,0,1 front-loaded slide underway
                clock.Advance(80f); host.RunFrame();                // t=128 → opacity 128/167 = 0.766
                var p2 = host.Scene.Paint(s);
                bool t128 = Near(p2.Opacity, 128f / 167f, 0.06f)
                    && p2.LocalTransform.Dy > -8f && p2.LocalTransform.Dy <= 0f;
                clock.Advance(320f); host.RunFrame();               // t=448 > 167 → settled
                var p3 = host.Scene.Paint(s);
                bool tEnd = Near(p3.LocalTransform.Dy, 0f, 0.5f) && p3.Opacity > 0.99f;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(48f); host.RunFrame();
                float op48 = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool close48 = Near(op48, 1f - 48f / 83f, 0.02f);   // TAS_HIDEPOPUP: 83ms linear fade
                SettleAll();
                Check("64i. Flyout samples: PopupThemeTransition slide −50→0 over 167ms cubic(0,0,0,1) + opacity 0→1 over the SAME 167ms linear (no hold); close 83ms fade",
                    t0 && t48 && t128 && tEnd && close48,
                    $"t0={t0} (dy={p0.LocalTransform.Dy:0.0} op={p0.Opacity:0.00}) t48={t48} (dy={p1.LocalTransform.Dy:0.0} op={p1.Opacity:0.00}) " +
                    $"t128={t128} (op={p2.Opacity:0.000}≈{128f / 167f:0.000} dy={p2.LocalTransform.Dy:0.0}) end={tEnd} close48={close48} (op={op48:0.000})");
            }

            // 64j — ToolTip (FadeIn/FadeOutThemeAnimation, the template's Opened/Closed states,
            // ToolTip_themeresources.xaml:56-70): TAS_FADEIN/TAS_FADEOUT = OPACITY over 167ms LINEAR both ways
            // (OS PVL storyboard 4/5 dump; WinTheme.cpp:165-182).
            {
                var (s, _) = OpenKind(PopupChrome.Raw);
                clock.Advance(80f); host.RunFrame();
                float opIn = host.Scene.Paint(s).Opacity;
                bool in80 = Near(opIn, 80f / 167f, 0.02f);
                SettleAll();
                bool inEnd = host.Scene.Paint(s).Opacity > 0.99f;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(80f); host.RunFrame();
                float opOut = host.Scene.IsLive(s) ? host.Scene.Paint(s).Opacity : -1f;
                bool out80 = Near(opOut, 1f - 80f / 167f, 0.02f);
                SettleAll();
                Check("64j. ToolTip samples: FadeIn/FadeOut 167ms linear (TAS_FADEIN/TAS_FADEOUT)",
                    in80 && inEnd && out80,
                    $"in80={in80} (op={opIn:0.000}≈{80f / 167f:0.000}) inEnd={inEnd} out80={out80} (op={opOut:0.000}≈{1f - 80f / 167f:0.000})");
            }

            // 64k — TeachingTip (muxc expand/contract): expand scale Min(0.01, 20/W)→1 over 300ms
            // cubic-bezier(0.1,0.9,0.2,1) (TeachingTip.cpp:1660-1664; TeachingTip.h:234+304-305); contract 1→20/W
            // over 200ms cubic-bezier(0.7,0,1,0.5) (cpp:1695-1712; h:235+306-307). NEITHER storyboard has opacity
            // keyframes — the tip is fully opaque while scaling.
            {
                var (s, _) = OpenKind(PopupChrome.TeachingTip, w: 320f, h: 96f);
                var p0 = host.Scene.Paint(s);
                bool t0 = Near(p0.LocalTransform.M11, 0.01f, 0.005f) && p0.Opacity > 0.99f;
                clock.Advance(160f); host.RunFrame();
                bool mid = host.Scene.Paint(s).Opacity > 0.99f;   // no fade at any point of the expand
                SettleAll();
                var pEnd = host.Scene.Paint(s);
                bool tEnd = Near(pEnd.LocalTransform.M11, 1f, 0.01f) && pEnd.Opacity > 0.99f;
                svc.CloseTop(); host.RunFrame();
                clock.Advance(208f); host.RunFrame();             // ≥200ms → contract target reached
                var pC = host.Scene.Paint(s);
                bool contracted = !host.Scene.IsLive(s) || Near(pC.LocalTransform.M11, 20f / 320f, 0.01f) || pC.Opacity < 0.01f;
                SettleAll();
                Check("64k. TeachingTip samples: expand scale 0.01→1 (300ms), contract →20/W (200ms), NO opacity keyframes",
                    t0 && mid && tEnd && contracted,
                    $"t0={t0} (sx={p0.LocalTransform.M11:0.000}) mid={mid} end={tEnd} (sx={pEnd.LocalTransform.M11:0.00}) contracted={contracted}");
            }

            // 64l — AutoSuggestBox suggestions (a bare WinUI Popup, no TransitionCollection in the template and
            // AutoSuggestBox_Partial.cpp attaches none): NO open/close animation — instant in, instant out.
            {
                var (s, _) = OpenKind(PopupChrome.Static);
                var p0 = host.Scene.Paint(s);
                bool instantIn = p0.Opacity > 0.99f && p0.ClipRect.IsInfinite
                    && Near(p0.LocalTransform.Dy, 0f, 0.1f) && !host.Animation.HasTracks(s);
                svc.CloseTop(); host.RunFrame();
                bool instantOut = !host.Scene.IsLive(s) || host.Scene.Paint(s).Opacity < 0.01f;
                SettleAll();
                Check("64l. AutoSuggest popup: bare Popup — no open/close animation (instant show/hide)",
                    instantIn && instantOut, $"in={instantIn} out={instantOut}");
            }
        }
    }

    static void E4PopupWindowingChecks(StringTable strings)
    {
        // e4popup.1 — WindowBlur (WM_ACTIVATE WA_INACTIVE) closes every LIGHT-DISMISS overlay; Modal (ContentDialog)
        // and None (ToolTip) stay. WinUI: DismissalTriggerFlags::WindowDeactivated is part of the default
        // light-dismiss trigger set (Popup_Partial.h:38); ContentDialog's modal popup excludes it; ToolTipService
        // owns its tooltip's close itself.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4blur", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;

            static Func<Element> Body(string label) => () => new BoxEl
            {
                Width = 120, Height = 40,
                Children = [new TextEl(label) { Size = 12f }],
            };
            var light = svc.Open(() => root.Anchor, Body("ld-body"), FlyoutPlacement.BottomLeft);
            var modal = svc.Open(() => root.Anchor, Body("modal-body"), FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
            var none = svc.Open(() => root.Anchor, Body("none-body"), FlyoutPlacement.BottomLeft,
                new PopupOptions(DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
            host.RunFrame();
            bool allOpen = !FindTextNode(host.Scene, strings, host.Scene.Root, "ld-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "modal-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "none-body").IsNull;

            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0));
            host.RunFrame();
            bool lightClosedNow = !light.IsOpen && modal.IsOpen && none.IsOpen;
            for (int i = 0; i < 20; i++) host.RunFrame();   // the 83ms close fade settles → entry finalized
            bool after = FindTextNode(host.Scene, strings, host.Scene.Root, "ld-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "modal-body").IsNull
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "none-body").IsNull;
            Check("e4popup.1 WindowBlur closes light-dismiss overlays only (Modal + None stay)",
                allOpen && lightClosedNow && after,
                $"allOpen={allOpen} closedNow={lightClosedNow} after={after}");
        }

        // e4popup.2 — work-area clamp math (pure FlyoutPositioner). Windowed popups collide against the MONITOR work
        // area passed as the container (FlyoutBase_Partial.cpp:3382-3392) and skip the final min-edge clamp
        // (cpp:2648-2653 — applied to non-windowed popups only), so they may extend above/left of the window.
        {
            // (a) min-edge clamp: a flyout fitting EXACTLY above lands at container-top − FlyoutMargin(4) after the
            // margin shift (cpp:2622-2646); non-windowed clamps back to the container origin, windowed keeps −4.
            var anchorA = new RectF(100, 60, 80, 20);
            var view = new RectF(0, 0, 480, 360);
            var pcA = FlyoutPositioner.Place(anchorA, new Size2(100, 60), view, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: false);
            var pwA = FlyoutPositioner.Place(anchorA, new Size2(100, 60), view, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: true);
            bool minEdge = Near(pcA.X, 100) && Near(pcA.Y, 0) && pcA.OpensUp
                        && Near(pwA.X, 100) && Near(pwA.Y, -4) && pwA.OpensUp;

            // (b) monitor container vs viewport: an anchor near the window top fits ABOVE against the work area
            // (negative window-DIP coords) but must FLIP BELOW when constrained to the viewport.
            var anchorB = new RectF(10, 10, 50, 20);
            var workB = new RectF(-500, -300, 1000, 400);
            var pwB = FlyoutPositioner.Place(anchorB, new Size2(100, 50), workB, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: true);
            var pcB = FlyoutPositioner.Place(anchorB, new Size2(100, 50), view, FlyoutPlacement.TopEdgeAlignedLeft, isWindowed: false);
            bool monitor = pwB.OpensUp && Near(pwB.X, 10) && Near(pwB.Y, 10 - 50 - 4)
                        && !pcB.OpensUp && Near(pcB.Y, 30 + 4) && pcB.Placement == FlyoutPlacement.BottomEdgeAlignedLeft;

            // (c) secondary-axis clamp INTO the work area: a left-justified popup wider than the space right of the
            // anchor slides left to stay on the monitor (TestAndCenterAlignWithinLimits clamp, cpp:415-483).
            var anchorC = new RectF(940, 50, 50, 20);
            var workC = new RectF(0, 0, 1000, 400);
            var pwC = FlyoutPositioner.Place(anchorC, new Size2(200, 60), workC, FlyoutPlacement.BottomEdgeAlignedLeft, isWindowed: true);
            bool secondary = Near(pwC.X, 800) && Near(pwC.Y, 74) && !pwC.OpensUp;

            Check("e4popup.2 work-area clamp math: windowed skips the min-edge clamp, collides vs the monitor rect, clamps the secondary axis",
                minEdge && monitor && secondary,
                $"minEdge={minEdge} monitor={monitor} secondary={secondary} (pwA.Y={pwA.Y:0.#} pwB.Y={pwB.Y:0.#} pwC.X={pwC.X:0.#})");
        }

        // e4popup.3 — the windowed-popup headless pipeline: PopupOptions.ConstrainToRootBounds=false leases a PAL
        // popup window + its own swapchain, the subtree records into ITS OWN DrawList re-origined to the popup's
        // (0,0) (SceneRecorder.RecordSubtree) while the MAIN record skips it (skipRoots); default (true) keeps
        // today's in-window path, and PopupWindowsEnabled=false falls back silently (CPopup::
        // DoesPlatformSupportWindowedPopup == false, FlyoutBase_Partial.cpp:3188).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4win", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;
            var anchorRect = host.Scene.AbsoluteRect(root.Anchor);

            var popupBody = new PopupExitProbeBody();
            var hWin = svc.Open(() => root.Anchor,
                () => Embed.Comp(() => popupBody),
                FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            host.RunFrame();

            bool leased = host.PopupWindows.Count == 1 && app.PopupWindows.Count == 1;
            PopupWindowSlot? slot = host.PopupWindows.Count > 0 ? host.PopupWindows[0] : null;
            HeadlessPopupWindow? pal = app.PopupWindows.Count > 0 ? app.PopupWindows[0] : null;
            bool shown = pal is { IsShown: true } && pal.ShowCount >= 1;
            // BottomLeft = below the anchor + FlyoutMargin 4; window scale 1 + client origin (0,0) ⇒ px == DIP. BoundsDip
            // stays the logical menu rect; the popup WINDOW (WindowBoundsDip == pal px) is inflated by the WinUI medium-popup
            // shadow insets (L10 T2 R10 B18 DIP) so the composition drop shadow has margin to render into.
            bool placed = slot is not null && pal is not null
                && Near(slot.BoundsDip.X, anchorRect.X) && Near(slot.BoundsDip.Y, anchorRect.Bottom + FlyoutPositioner.FlyoutMargin)
                && slot.BoundsDip.W >= 180f && slot.BoundsDip.H >= 80f
                && Near(slot.WindowBoundsDip.X, slot.BoundsDip.X - 10f) && Near(slot.WindowBoundsDip.Y, slot.BoundsDip.Y - 2f)
                && Near(slot.WindowBoundsDip.W, slot.BoundsDip.W + 20f) && Near(slot.WindowBoundsDip.H, slot.BoundsDip.H + 20f)
                && Near(pal.BoundsPx.X, slot.WindowBoundsDip.X) && Near(pal.BoundsPx.Y, slot.WindowBoundsDip.Y)
                && Near(pal.BoundsPx.W, slot.WindowBoundsDip.W) && Near(pal.BoundsPx.H, slot.WindowBoundsDip.H);
            bool osBackdrop = slot is { Material: PopupWindowMaterial.TransientAcrylic }
                && pal is { Material: PopupWindowMaterial.TransientAcrylic, Dark: true };

            for (int i = 0; i < 20; i++) host.RunFrame();   // the 250ms open clip-reveal settles → full content records

            var scratch = new HeadlessGpuDevice();          // decode the popup's own DrawList
            if (slot is not null)
                scratch.SubmitDrawList(slot.DrawList.Bytes, slot.DrawList.SortKeys,
                    new FrameInfo(new Size2(slot.BoundsDip.W, slot.BoundsDip.H), 1f, default));
            bool routed = HasGlyph(scratch, strings, "popup-exit-old");
            bool noEngineAcrylic = scratch.LastLayers.Count == 0;   // OS-backed menu acrylic: no in-engine PushLayer blur.
            bool reorigined = false;
            foreach (var g in scratch.LastGlyphs)
                if (strings.Resolve(g.Text) == "popup-exit-old")
                    reorigined = slot is not null && g.Bounds.X >= -1f && g.Bounds.Y >= -1f
                        && g.Bounds.Right <= slot.BoundsDip.W + 1f && g.Bounds.Bottom <= slot.BoundsDip.H + 1f;
            bool mainSkips = !HasGlyph(device, strings, "popup-exit-old") && HasGlyph(device, strings, "anchor")
                && !FindTextNode(host.Scene, strings, host.Scene.Root, "popup-exit-old").IsNull;   // scene keeps it (hit-test)
            bool presented = slot?.Swapchain is HeadlessSwapchain sc && sc.PresentCount >= 1;

            // Swap a keyed row while it is hosted in the popup window. The old row is now an exit orphan: it must remain
            // in this popup DrawList (behind the incoming row) and must not leak into the main-window DrawList.
            popupBody.Swap.Value = true;
            host.RunFrame();
            if (slot is not null)
                scratch.SubmitDrawList(slot.DrawList.Bytes, slot.DrawList.SortKeys,
                    new FrameInfo(new Size2(slot.BoundsDip.W, slot.BoundsDip.H), 1f, default));
            bool popupExitRouted = HasGlyph(scratch, strings, "popup-exit-old")
                && HasGlyph(scratch, strings, "popup-exit-new")
                && !HasGlyph(device, strings, "popup-exit-old");

            // Default (ConstrainToRootBounds = true): in-window popup — no window lease, content in the MAIN DrawList.
            var hIn = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 120, Height = 40, Children = [new TextEl("inwin-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft);
            host.RunFrame();
            bool defaultInWindow = app.PopupWindows.Count == 1 && HasGlyph(device, strings, "inwin-body");
            hIn.Close();
            for (int i = 0; i < 20; i++) host.RunFrame();

            // Close: a CLOSING entry keeps its popup window while it fades; the lease releases with the entry.
            hWin.Close();
            host.RunFrame();   // 16ms into the 83ms fade
            bool keptWhileFading = host.PopupWindows.Count == 1 && pal is { IsShown: true };
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool released = host.PopupWindows.Count == 0 && pal is { Disposed: true, IsShown: false };

            // PopupWindowsEnabled = false (the DoesPlatformSupportWindowedPopup gate): silent constrained fallback.
            host.PopupWindowsEnabled = false;
            var hFb = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 120, Height = 40, Children = [new TextEl("fb-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool fallback = app.PopupWindows.Count == 1 && HasGlyph(device, strings, "fb-body");
            hFb.Close();
            for (int i = 0; i < 20; i++) host.RunFrame();

            Check("e4popup.3 windowed popup: PAL lease + own DrawList/swapchain, main record skips the subtree; default + disabled stay in-window",
                leased && shown && placed && osBackdrop && routed && noEngineAcrylic && reorigined && mainSkips && presented
                && popupExitRouted && defaultInWindow && keptWhileFading && released && fallback,
                $"leased={leased} shown={shown} placed={placed} os={osBackdrop} routed={routed} noAcrylic={noEngineAcrylic} reorig={reorigined} skip={mainSkips} exitRoute={popupExitRouted} present={presented} def={defaultInWindow} kept={keptWhileFading} rel={released} fb={fallback}");
        }

        // e4popup.4 — the GetWorkArea seam through the host: the work-area query lands at the anchor's centre in
        // physical virtual-screen px (client origin + scale, IPlatformWindow.ClientOriginPx), the windowed popup
        // flips ABOVE because the MONITOR (not the viewport) has no room below, may take negative window-DIP Y
        // (out-of-bounds), and the PAL window bounds round-trip DIP→px. A constrained popup at the same anchor
        // stays BELOW (the viewport has room) — the monitor flip is windowed-only behavior.
        {
            using var app = new HeadlessPlatformApp();
            Point2 queried = default;
            app.WorkAreaResolver = p => { queried = p; return new RectF(0f, 0f, 2000f, 800f); };
            var window = new HeadlessWindow(new WindowDesc("e4wa", new Size2(960, 720), 2f)) { ClientOriginPx = new Point2(1000f, 600f) };
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;
            var anchorRect = host.Scene.AbsoluteRect(root.Anchor);   // (20,20,120,32) DIP

            var hWin = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 180, Height = 80, Children = [new TextEl("wa-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            host.RunFrame();

            bool leased = host.PopupWindows.Count == 1 && app.PopupWindows.Count == 1;
            PopupWindowSlot? slot = host.PopupWindows.Count > 0 ? host.PopupWindows[0] : null;
            HeadlessPopupWindow? pal = app.PopupWindows.Count > 0 ? app.PopupWindows[0] : null;
            bool query = Near(queried.X, 1000f + (anchorRect.X + anchorRect.W * 0.5f) * 2f)
                      && Near(queried.Y, 600f + (anchorRect.Y + anchorRect.H * 0.5f) * 2f);
            float hDip = slot?.BoundsDip.H ?? 0f;
            // Work area px (0,0,2000,800) → window DIP (−500,−300,1000,400): monitor bottom = 100 DIP < the popup's
            // below-anchor bottom ⇒ flips ABOVE the anchor, past the window top (negative DIP Y).
            bool flipped = slot is not null
                && Near(slot.BoundsDip.Y, anchorRect.Y - hDip - FlyoutPositioner.FlyoutMargin) && slot.BoundsDip.Y < 0f;
            // The popup WINDOW px is the inflated WindowBoundsDip (menu rect + shadow insets) × scale + client origin.
            bool px = slot is not null && pal is not null
                && Near(pal.BoundsPx.X, 1000f + slot.WindowBoundsDip.X * 2f) && Near(pal.BoundsPx.Y, 600f + slot.WindowBoundsDip.Y * 2f)
                && Near(pal.BoundsPx.W, slot.WindowBoundsDip.W * 2f) && Near(pal.BoundsPx.H, slot.WindowBoundsDip.H * 2f);
            bool inWork = pal is not null && pal.BoundsPx.Y >= 0f && pal.BoundsPx.Y + pal.BoundsPx.H <= 800f;

            var hIn = svc.Open(() => root.Anchor,
                () => new BoxEl { Width = 180, Height = 80, Children = [new TextEl("wa-in-body") { Size = 12f }] },
                FlyoutPlacement.BottomLeft);
            for (int i = 0; i < 20; i++) host.RunFrame();
            var inBody = FindTextNode(host.Scene, strings, host.Scene.Root, "wa-in-body");
            bool constrainedBelow = !inBody.IsNull && host.Scene.AbsoluteRect(inBody).Y > anchorRect.Bottom;

            Check("e4popup.4 GetWorkArea seam: px query at the anchor centre, monitor flip above the window (negative DIP Y), DIP→px bounds round-trip",
                leased && query && flipped && px && inWork && constrainedBelow,
                $"leased={leased} query={query}@({queried.X:0.#},{queried.Y:0.#}) flip={flipped}(dipY={slot?.BoundsDip.Y ?? 0f:0.#}) px={px} inWork={inWork} below={constrainedBelow}");
        }

        // e4popup.5 — the 8 edge-aligned FlyoutPlacementMode variants (FlyoutBase_Partial.cpp:84-110 major side +
        // :78-113 edge justification; TryPlacement cpp:415-483) with room on every side: exact rects including the
        // FlyoutMargin 4 shift away from the anchor (cpp:65 + 2622-2646).
        {
            var anchor = new RectF(200, 150, 80, 40);
            var c = new RectF(0, 0, 600, 500);
            var size = new Size2(100, 60);
            bool At(FlyoutPlacement p, float x, float y, bool up)
            {
                var r = FlyoutPositioner.Place(anchor, size, c, p, isWindowed: false);
                return Near(r.X, x) && Near(r.Y, y) && r.OpensUp == up && r.Placement == p;
            }
            bool tal = At(FlyoutPlacement.TopEdgeAlignedLeft, 200, 86, true);       // y = 150−60−4
            bool tar = At(FlyoutPlacement.TopEdgeAlignedRight, 180, 86, true);      // x = 280−100
            bool bal = At(FlyoutPlacement.BottomEdgeAlignedLeft, 200, 194, false);  // y = 190+4
            bool bar = At(FlyoutPlacement.BottomEdgeAlignedRight, 180, 194, false);
            bool lat = At(FlyoutPlacement.LeftEdgeAlignedTop, 96, 150, false);      // x = 200−100−4
            bool lab = At(FlyoutPlacement.LeftEdgeAlignedBottom, 96, 130, false);   // y = 190−60
            bool rat = At(FlyoutPlacement.RightEdgeAlignedTop, 284, 150, false);    // x = 280+4
            bool rab = At(FlyoutPlacement.RightEdgeAlignedBottom, 284, 130, false);
            Check("e4popup.5 the 8 edge-aligned placements produce the exact WinUI rects (major side + edge justify + FlyoutMargin 4)",
                tal && tar && bal && bar && lat && lab && rat && rab,
                $"TAL={tal} TAR={tar} BAL={bal} BAR={bar} LAT={lat} LAB={lab} RAT={rat} RAB={rab}");
        }

        // e4popup.6 — PerformPlacementWithFallback (FlyoutBase_Partial.cpp:488-537; per-side order cpp:2559-2593):
        // a side that can't fit walks its fallback order and REPORTS the effective placement; an edge justification
        // survives a vertical↔vertical (or horizontal↔horizontal) flip and centers on a perpendicular fallback.
        {
            var c = new RectF(0, 0, 600, 500);
            var size = new Size2(100, 60);

            var low = new RectF(50, 440, 80, 20);     // no room below → Bottom flips to Top, Left justify kept
            var pb = FlyoutPositioner.Place(low, size, c, FlyoutPlacement.BottomEdgeAlignedLeft, isWindowed: false);
            bool flipUp = pb.OpensUp && Near(pb.X, 50) && Near(pb.Y, 376) && pb.Placement == FlyoutPlacement.TopEdgeAlignedLeft;

            var right = new RectF(520, 100, 60, 30);  // no room right → Right flips to Left (order R,L,T,B), Top justify kept
            var pr = FlyoutPositioner.Place(right, size, c, FlyoutPlacement.RightEdgeAlignedTop, isWindowed: false);
            bool flipLeft = !pr.OpensUp && Near(pr.X, 416) && Near(pr.Y, 100) && pr.Placement == FlyoutPlacement.LeftEdgeAlignedTop;

            var wide = new RectF(10, 200, 280, 30);   // Left AND Right blocked → 3rd choice Top (order L,R,T,B), centered
            var narrow = new RectF(0, 0, 300, 500);
            var pt = FlyoutPositioner.Place(wide, size, narrow, FlyoutPlacement.Left, isWindowed: false);
            bool thirdChoice = pt.OpensUp && Near(pt.X, 100) && Near(pt.Y, 136) && pt.Placement == FlyoutPlacement.Top;

            Check("e4popup.6 placement fallback: Bottom→Top and Right→Left flips keep the justification; a blocked Left walks to Top centered",
                flipUp && flipLeft && thirdChoice,
                $"flipUp={flipUp} flipLeft={flipLeft} third={thirdChoice} (pb=({pb.X:0.#},{pb.Y:0.#}) pr=({pr.X:0.#},{pr.Y:0.#}) pt=({pt.X:0.#},{pt.Y:0.#}))");
        }

        // e4popup.7 — ToolTipService timing (ToolTipService_Partial.cpp:1771-1780): mouse initial show delay =
        // SPI_GETMOUSEHOVERTIME(400) × 2 = 800ms; auto-dismiss after SPI_GETMESSAGEDURATION = 5s
        // (DEFAULT_SHOW_DURATION_SECONDS, ToolTipService_Partial.h:19; the close timer is armed on automatic open,
        // cpp:429-459); a re-hover within BETWEEN_SHOW_DELAY_MS 200ms of the close (cpp:659, .h:17) uses the RESHOW
        // delay = 400ms — ×1, because the shipping static_cast<INT64>(1.5) truncates (cpp:1775).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4tip", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new E4ToolTipProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();

            bool TipOpen() => !FindTextNode(host.Scene, strings, host.Scene.Root, "tip-body").IsNull;
            void Hover(float x, float y) { window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(x, y), 0, 0)); host.RunFrame(); }
            void Step(float ms) { clock.Advance(ms); host.RunFrame(); }
            // 0-dt frames: a just-seeded AnimEngine track consumes no dt on its FIRST tick (AnimEngine.cs JustSeeded —
            // idle time pending before the seed must not run a track early), and a finished track frees at the END of
            // the tick that crossed it with the clock firing one passive pass later. Polling at dt=0 drains that
            // frame-granularity latency WITHOUT advancing the clock — the ms thresholds below stay exact.
            void Poll() { for (int i = 0; i < 4; i++) host.RunFrame(); }

            Hover(50f, 50f);                  // pointer enters the target → the 800ms show-delay clock arms
            Poll();                           // the countdown track seeds + clears JustSeeded at 0ms
            Step(700f);
            Poll();
            bool notAt700 = !TipOpen();       // 700ms < 800 — must still be closed
            Step(150f);                       // 850ms total ≥ 800 → the delay elapses
            Poll();
            bool openAt800 = TipOpen();

            Poll();                           // the 5s auto-dismiss clock seeds with the open bubble
            Step(4800f);                      // dwell: 4.8s of the 5s auto-dismiss window
            Poll();
            bool stillOpenAt4800 = TipOpen();
            Step(300f);                       // ≥ 5000 → auto-dismiss fires
            Poll();
            for (int i = 0; i < 4; i++) Step(80f);   // the FadeOut (167ms) settles → bubble removed
            Poll();
            bool autoClosed = !TipOpen();

            Hover(10f, 10f);                  // auto-dismiss latches until a real owner leave
            Hover(52f, 50f);                  // re-enter < 200ms (wall) after the close → RESHOW delay (400ms)
            Poll();
            Step(300f);
            Poll();
            bool reshowNotAt300 = !TipOpen(); // 300ms < 400 — must still be closed
            Step(150f);                       // 450ms ≥ 400 → fires (an un-truncated 1.5× = 600ms would still be closed)
            Poll();
            bool reshowAt400 = TipOpen();

            Check("e4popup.7 ToolTip timing: 800ms initial show, 5s auto-dismiss, 400ms reshow inside the 200ms window",
                notAt700 && openAt800 && stillOpenAt4800 && autoClosed && reshowNotAt300 && reshowAt400,
                $"!700={notAt700} 800={openAt800} dwell={stillOpenAt4800} auto={autoClosed} !300={reshowNotAt300} 400={reshowAt400}");
        }

        // e4popup.7b — an OPEN tooltip never blocks input: the bubble is hit-test-invisible (ToolTip.cs) AND the
        // overlay host's full-bleed positioning wrappers + the Raw chrome surface yield self (HitTestPassThrough),
        // so with the bubble open over a scrollable page (a) a wheel AT the bubble scrolls the page beneath it and
        // (b) a wheel anywhere ELSE in the window still resolves the page scroller. (The regression: the entry's
        // full-bleed wrapper was HitTestAny's window-wide deepest hit — no scroller in its ancestor chain — so
        // wheel input died EVERYWHERE while any popup/tooltip was open.)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4tipwheel", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new E4ToolTipWheelProbe();
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, device, fonts, strings, root, frameTime: clock);
            host.RunFrame();
            var s = host.Scene;
            NodeHandle FindScrollable(NodeHandle n)
            {
                if (n.IsNull) return NodeHandle.Null;
                if (s.HasScroll(n)) return n;
                for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
                {
                    var r = FindScrollable(c);
                    if (!r.IsNull) return r;
                }
                return NodeHandle.Null;
            }
            var vp = FindScrollable(s.Root);

            void Hover(float x, float y) { window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(x, y), 0, 0)); host.RunFrame(); }
            void Step(float ms) { clock.Advance(ms); host.RunFrame(); }
            void Poll() { for (int i = 0; i < 4; i++) host.RunFrame(); }
            float ScrollProgress() { ref var st = ref s.ScrollRef(vp); return MathF.Max(st.TargetY, st.OffsetY); }

            Hover(60f, 116f);                 // over the wrapped target (content y 100..132)
            Poll(); Step(850f); Poll();       // the 800ms show delay elapses → bubble open
            var bubbleText = FindTextNode(s, strings, s.Root, "tip-wheel");
            bool open = !bubbleText.IsNull;
            var br = open ? s.AbsoluteRect(bubbleText) : default;

            // (a) wheel AT the open bubble: the page beneath must scroll (bubble + overlay wrappers all yield).
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(br.X + 2f, br.Y + 2f), 0, 0, 60f));
            for (int i = 0; i < 6; i++) Step(16f);
            float afterBubbleWheel = ScrollProgress();

            // (b) wheel far from the bubble while the entry is still open: must also reach the page scroller.
            window.QueueInput(new InputEvent(InputKind.Wheel, new Point2(420f, 320f), 0, 0, 60f));
            for (int i = 0; i < 6; i++) Step(16f);
            float afterFarWheel = ScrollProgress();

            Check("e4popup.7b an open tooltip never blocks input — wheel scrolls the page at the bubble AND anywhere else",
                open && afterBubbleWheel > 1f && afterFarWheel > afterBubbleWheel + 1f,
                $"open={open} afterBubble={afterBubbleWheel:0.#} afterFar={afterFarWheel:0.#}");
        }

        // e4popup.8 — focus restores to the pre-open node when the close STARTS, not when the fade finishes: WinUI
        // restores the popup's SavedFocusState synchronously in Hide()/CPopup::Close (Popup_Partial.h:63-64;
        // FlyoutBase returns focus to the invoker on Hide, not after the close animation) — the popup is still on
        // screen fading while the invoker already has focus back.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4focus", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;

            ClickNode(host, window, root.Anchor);   // pre-open focus = the anchor
            bool anchorFocused0 = (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;

            NodeHandle body = default;
            var h = svc.Open(() => root.Anchor, () => new BoxEl
            {
                Width = 140, Height = 60, Padding = Edges4.All(10),
                Children = [new BoxEl { Width = 100, Height = 24, Role = AutomationRole.Button, OnClick = () => { }, OnRealized = n => body = n }],
            }, FlyoutPlacement.BottomLeft);
            for (int i = 0; i < 18; i++) host.RunFrame();   // the open reveal settles
            ClickNode(host, window, body);                  // focus moves INTO the popup
            bool movedIn = (host.Scene.Flags(body) & NodeFlags.Focused) != 0;

            h.Close();   // the restore happens HERE — synchronously at close start
            bool restoredAtCloseStart = (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;
            bool stillFadingNow = host.Scene.IsLive(body) && !h.IsOpen;
            host.RunFrame();   // 16ms into the 83ms fade — the popup is still alive on screen
            bool fadingAfterFrame = host.Scene.IsLive(body);
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool removed = !host.Scene.IsLive(body) && (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;

            Check("e4popup.8 focus restores at close START (popup still fading) and survives the removal",
                anchorFocused0 && movedIn && restoredAtCloseStart && stillFadingNow && fadingAfterFrame && removed,
                $"pre={anchorFocused0} in={movedIn} atStart={restoredAtCloseStart} fading={stillFadingNow}/{fadingAfterFrame} removed={removed}");
        }

        // e4popup.9 — nested cascade close (WinUI MenuFlyoutSubItem close order): closing the parent closes its
        // children FIRST; each level restores its own saved focus, so the chain unwinds child → parent content →
        // original invoker; both windowed-popup leases release with their entries.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("e4cascade", new Size2(480, 360), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var fonts = new HeadlessFontSystem(strings);
            var root = new OverlayProbe();
            using var host = new AppHost(app, window, device, fonts, strings, root);
            host.RunFrame();
            var svc = root.Service!;
            ClickNode(host, window, root.Anchor);   // pre-open focus = the anchor

            NodeHandle pItem = default, cItem = default;
            OverlayHandle? hChild = null;
            var hParent = svc.Open(() => root.Anchor, () => new BoxEl
            {
                Width = 160, Height = 80, Padding = Edges4.All(8),
                Children =
                [
                    new BoxEl
                    {
                        Width = 120, Height = 24, Role = AutomationRole.MenuItem, OnRealized = n => pItem = n,
                        OnClick = () => hChild = svc.Open(() => pItem, () => new BoxEl
                        {
                            Width = 120, Height = 50,
                            Children = [new BoxEl { Width = 100, Height = 24, Role = AutomationRole.MenuItem, OnClick = () => { }, OnRealized = n2 => cItem = n2 }],
                        }, FlyoutPlacement.RightEdgeAlignedTop, new PopupOptions { ConstrainToRootBounds = false }),
                    },
                ],
            }, FlyoutPlacement.BottomLeft, new PopupOptions { ConstrainToRootBounds = false });
            for (int i = 0; i < 18; i++) host.RunFrame();   // parent open settles

            ClickNode(host, window, pItem);                  // focuses the item + opens the windowed child
            for (int i = 0; i < 18; i++) host.RunFrame();    // child open settles
            bool bothLeased = app.PopupWindows.Count == 2 && host.PopupWindows.Count == 2;

            ClickNode(host, window, cItem);                  // focus moves into the CHILD popup
            bool inChild = (host.Scene.Flags(cItem) & NodeFlags.Focused) != 0;

            hParent.Close();
            bool cascade = !hParent.IsOpen && hChild is { IsOpen: false };
            // Child closed first: its restore put focus on pItem (inside the parent wrapper), so the parent's restore
            // then chained to the original anchor — a parent-first order would strand focus on pItem.
            bool chained = (host.Scene.Flags(root.Anchor) & NodeFlags.Focused) != 0;
            for (int i = 0; i < 20; i++) host.RunFrame();
            bool released = host.PopupWindows.Count == 0
                && app.PopupWindows.Count == 2 && app.PopupWindows[0].Disposed && app.PopupWindows[1].Disposed;
            bool gone = !host.Scene.IsLive(pItem) && !host.Scene.IsLive(cItem);

            Check("e4popup.9 cascade close: parent close closes children first, focus chains back to the invoker, windowed leases release",
                bothLeased && inChild && cascade && chained && released && gone,
                $"leased={bothLeased} inChild={inChild} cascade={cascade} chained={chained} released={released} gone={gone}");
        }
    }

    static void FlyoutAcrylicChecks(StringTable strings)
    {
        try
        {
            foreach (var (kind, label) in new[] { (ThemeKind.Dark, "dark"), (ThemeKind.Light, "light") })
            {
                Tok.Use(kind);
                ColorF tint = kind == ThemeKind.Dark ? ColorF.FromRgba(0x2C, 0x2C, 0x2C) : ColorF.FromRgba(0xFC, 0xFC, 0xFC);
                ColorF fall = kind == ThemeKind.Dark ? ColorF.FromRgba(0x2C, 0x2C, 0x2C) : ColorF.FromRgba(0xF9, 0xF9, 0xF9);
                float tintOp = kind == ThemeKind.Dark ? 0.15f : 0.0f;
                float lumOp = kind == ThemeKind.Dark ? 0.96f : 0.85f;

                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("acry-" + label, new Size2(480, 400), 1f)); window.Show();
                var device = new HeadlessGpuDevice();
                var fonts = new HeadlessFontSystem(strings);
                var root = new OverlayProbe();
                using var host = new AppHost(app, window, device, fonts, strings, root);
                host.RunFrame();
                var svc = root.Service!;

                bool SurfaceLayerOk(PopupChrome chrome)
                {
                    var hd = svc.Open(() => root.Anchor,
                        () => new BoxEl { Width = 220f, Height = 80f, Fill = Tok.FillCardDefault },
                        FlyoutPlacement.BottomLeft, new PopupOptions(Chrome: chrome));
                    host.RunFrame();
                    host.RunFrame();
                    bool found = false;
                    foreach (var l in device.LastLayers)
                        if (ColorClose(l.Tint, tint, 0.004f) && ColorClose(l.Fallback, fall, 0.004f)
                            && Near(l.TintOpacity, tintOp, 0.005f) && Near(l.LuminosityOpacity, lumOp, 0.005f)
                            && Near(l.BlurSigma, 30f, 0.5f) && l.NoiseOpacity > 0f)
                            found = true;
                    hd.Close();
                    for (int i = 0; i < 16; i++) host.RunFrame();
                    return found;
                }

                bool menuOk = SurfaceLayerOk(PopupChrome.Flyout);      // MenuFlyoutPresenter (system backdrop recipe)
                bool comboOk = SurfaceLayerOk(PopupChrome.Dropdown);   // ComboBox PopupBorder
                bool suggestOk = SurfaceLayerOk(PopupChrome.Static);   // AutoSuggestBox SuggestionsContainer
                bool flyoutOk = SurfaceLayerOk(PopupChrome.Popup);     // FlyoutPresenter
                // ToolTip + the Slider value tip bind Tok.AcrylicFlyout directly (ToolTip.cs BubbleContent /
                // Slider.cs TipBubble) — assert the token equals the WinUI recipe so those surfaces are covered too.
                var spec = Tok.AcrylicFlyout;
                bool tokenOk = ColorClose(spec.Tint, tint, 0.004f) && ColorClose(spec.Fallback, fall, 0.004f)
                    && Near(spec.TintOpacity, tintOp, 0.005f) && Near(spec.LuminosityOpacity, lumOp, 0.005f);
                Check($"64m. {label} flyout acrylic: PushLayer tint/fallback/tint-opacity/luminosity match the WinUI default acrylic on every transient surface",
                    menuOk && comboOk && suggestOk && flyoutOk && tokenOk,
                    $"menu={menuOk} combo={comboOk} suggest={suggestOk} flyout={flyoutOk} token={tokenOk}");
            }
        }
        finally { Tok.Use(ThemeKind.Dark); }
    }

    static void AcrylicBackdropMathChecks()
    {
        // Kernel: BuildKernel(texelSigma) is a normalized symmetric bilinear-tap gaussian for the VARIABLE ≤4 texel
        // sigma. Verify at the σ=30-DIP@100% bucket (down 8 ⇒ texel σ 3.75) and a small σ (narrower ⇒ fewer taps).
        Span<float> koff = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        Span<float> kwgt = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        float texelSig = AcrylicBackdropMath.EffectiveTexelSigma(30f, 1f, AcrylicBackdropMath.DownsampleFactor(30f, 1f)); // 3.75
        int nt = AcrylicBackdropMath.BuildKernel(texelSig, koff, kwgt);
        double mass = kwgt[0];                                        // total kernel mass: center + 2·(mirrored taps)
        double variance = 0;
        for (int i = 1; i < nt; i++) { mass += 2.0 * kwgt[i]; variance += 2.0 * kwgt[i] * koff[i] * koff[i]; }
        double sigma = Math.Sqrt(variance);                          // effective std-dev of the folded bilinear kernel
        bool monotone = true;
        for (int i = 2; i < nt; i++) if (koff[i] <= koff[i - 1]) monotone = false;
        int ntSmall = AcrylicBackdropMath.BuildKernel(1.5f, koff, kwgt);   // small σ ⇒ radius ~5 ⇒ ~4 taps (< the 3.75 bucket)
        bool kernelOk = nt >= 2 && nt <= AcrylicBackdropMath.MaxTapCount
            && koff[0] == 0f && kwgt[0] > 0f && monotone
            && koff[nt - 1] <= AcrylicBackdropMath.MaxKernelRadius
            && Math.Abs(mass - 1.0) < 1e-4 && Math.Abs(sigma - texelSig) < 0.4
            && ntSmall >= 2 && ntSmall < nt;                          // a smaller texel σ emits a narrower (fewer-tap) kernel
        Check("64n. acrylic blur kernel: normalized symmetric linear-tap gaussian rebuilt for the variable <=4 texel sigma",
            kernelOk,
            $"taps={nt} mass={mass:0.00000} sigma={sigma:0.000} (texelSigma {texelSig:0.000}) smallTaps={ntSmall}");

        // Downsample curve (Flutter-Impeller / Skia): down = smallest pow2 >= ceil(sigmaPhys/4), clamped [1,16], so the
        // effective texel sigma stays ≤ 4; at sigmaPhys ≤ 4 down stays 1 (no downsample, exact).
        bool downOk = AcrylicBackdropMath.DownsampleFactor(30f, 1f) == 8       // sigmaPhys 30  → ceil(7.5)=8  → pow2 8
            && AcrylicBackdropMath.DownsampleFactor(30f, 1.5f) == 16           // sigmaPhys 45  → ceil(11.25)=12 → pow2 16
            && AcrylicBackdropMath.DownsampleFactor(30f, 2f) == 16             // sigmaPhys 60  → ceil(15)=15  → pow2 16
            && AcrylicBackdropMath.DownsampleFactor(15f, 1f) == 4              // sigmaPhys 15  → ceil(3.75)=4 → pow2 4
            && AcrylicBackdropMath.DownsampleFactor(120f, 2f) == 16            // sigmaPhys 240 → ceil(60)=60  → pow2 64 → clamp 16
            && AcrylicBackdropMath.DownsampleFactor(4f, 1f) == 1               // sigmaPhys 4   → down 1 (no downsample at σ≤4)
            && AcrylicBackdropMath.DownsampleFactor(8f, 1f) == 2;             // sigmaPhys 8   → ceil(2)=2 → pow2 2 (texel σ 4)
        // Effective texel sigma is ≤ 4 always, and EXACT (never clamped up) when smaller than 4.
        bool texelOk = AcrylicBackdropMath.EffectiveTexelSigma(30f, 1f, AcrylicBackdropMath.DownsampleFactor(30f, 1f)) <= 4f + 1e-4f
            && Math.Abs(AcrylicBackdropMath.EffectiveTexelSigma(3f, 1f, AcrylicBackdropMath.DownsampleFactor(3f, 1f)) - 3f) < 1e-4  // down 1 → texel σ 3 (exact)
            && Math.Abs(AcrylicBackdropMath.EffectiveTexelSigma(8f, 1f, AcrylicBackdropMath.DownsampleFactor(8f, 1f)) - 4f) < 1e-4; // down 2 → texel σ 4
        Check("64n2. acrylic downsample factor: pow2-snapped down keeps effective texel sigma <=4 (Skia/Impeller), down 1 at σ<=4",
            downOk && texelOk,
            $"down@1x={AcrylicBackdropMath.DownsampleFactor(30f, 1f)} @1.5x={AcrylicBackdropMath.DownsampleFactor(30f, 1.5f)} @2x={AcrylicBackdropMath.DownsampleFactor(30f, 2f)} texelOk={texelOk}");

        // Snapshot region: the layer rect inflated by the FULL blur support (kernelRadiusTexels·down phys px ≈ 3·sigmaPhys)
        // on every side (so blurred texels under the rect see real backdrop — bit-identical to blurring the whole backdrop
        // inside the rect), clamped to the canvas at window edges. Pad is derived from the actual kernel, not a constant.
        int down1 = AcrylicBackdropMath.DownsampleFactor(30f, 1f);                          // 8
        int rTex1 = AcrylicBackdropMath.KernelRadiusTexels(AcrylicBackdropMath.EffectiveTexelSigma(30f, 1f, down1)); // 12
        int pad = rTex1 * down1;                                                            // 96 px @ 100% (σ=30)
        AcrylicBackdropMath.SnapshotRegion(new RectF(200f, 160f, 200f, 120f), 1f, down1, rTex1, 1920, 1080, out int x, out int y, out int w, out int h);
        bool interiorOk = x == 200 - pad && y == 160 - pad && w == 200 + 2 * pad && h == 120 + 2 * pad;
        AcrylicBackdropMath.SnapshotRegion(new RectF(2f, 2f, 60f, 40f), 1f, down1, rTex1, 480, 400, out int cx, out int cy, out int cw, out int ch);
        bool clampOk = cx == 0 && cy == 0 && cw == 2 + 60 + pad && ch == 2 + 40 + pad;   // left/top clamped at the canvas edge
        int down2 = AcrylicBackdropMath.DownsampleFactor(30f, 2f);                          // 16
        int rTex2 = AcrylicBackdropMath.KernelRadiusTexels(AcrylicBackdropMath.EffectiveTexelSigma(30f, 2f, down2)); // 12
        int pad2 = rTex2 * down2;                                                           // 192 px @ 200% (σ=30)
        AcrylicBackdropMath.SnapshotRegion(new RectF(200f, 160f, 100f, 60f), 2f, down2, rTex2, 4000, 4000, out int sx, out int sy, out int sw, out int sh);
        bool scaleOk = sx == 400 - pad2 && sy == 320 - pad2 && sw == 200 + 2 * pad2 && sh == 120 + 2 * pad2;   // DIP→phys at 200% DPI
        Check("64n3. acrylic snapshot region: rect inflated by the actual kernel support and clamped to the canvas (phys px, DPI-aware)",
            interiorOk && clampOk && scaleOk, $"interior={interiorOk} clamp={clampOk} scale={scaleOk} pad={pad}");

        // LayerPool size buckets (gpu-renderer.md §7.1 quantized pow2 buckets, floor 64): monotone, covering, few
        // distinct sizes ⇒ a steady-state frame re-acquires the same bucket and never creates a texture.
        bool bucketOk = AcrylicBackdropMath.BucketDim(1) == 64 && AcrylicBackdropMath.BucketDim(64) == 64
            && AcrylicBackdropMath.BucketDim(65) == 128 && AcrylicBackdropMath.BucketDim(240) == 256
            && AcrylicBackdropMath.BucketDim(960) == 1024;
        Check("64n4. acrylic LayerPool buckets: next-pow2 (floor 64) so pooled RTs reuse across layers and frames",
            bucketOk,
            $"b(1)={AcrylicBackdropMath.BucketDim(1)} b(65)={AcrylicBackdropMath.BucketDim(65)} b(960)={AcrylicBackdropMath.BucketDim(960)}");

        // 64n5 — retained-backdrop cache decision (AcrylicBackdropMath.BackdropReusable): the §2.3 region-aware reuse
        // gate (headless half of the AcrylicCompositor pinned-RT cache). A stationary layer reuses its blurred snapshot
        // when nothing behind it moved; a geometry change OR a damage rect touching its snapshot region forces a re-blur.
        var stampA = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080);
        var stampSame = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080);
        var stampMoved = AcrylicBackdropMath.Stamp(new RectF(100f, 90f, 300f, 200f), 30f, 1f, 1920, 1080);   // rect moved 10 DIP
        var stampSource = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080,
            sourceId: 42, clipLeft: 90, clipTop: 70, clipRight: 410, clipBottom: 290);
        var stampOtherSource = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080,
            sourceId: 43, clipLeft: 90, clipTop: 70, clipRight: 410, clipBottom: 290);
        var stampOtherClip = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080,
            sourceId: 42, clipLeft: 100, clipTop: 70, clipRight: 410, clipBottom: 290);
        int down5 = AcrylicBackdropMath.DownsampleFactor(30f, 1f);
        int rTex5 = AcrylicBackdropMath.KernelRadiusTexels(AcrylicBackdropMath.EffectiveTexelSigma(30f, 1f, down5));
        AcrylicBackdropMath.SnapshotRegion(new RectF(100f, 80f, 300f, 200f), 1f, down5, rTex5, 1920, 1080, out int qx, out int qy, out int qw, out int qh);
        var region = new RectF(qx, qy, qw, qh);
        bool reuseNone = AcrylicBackdropMath.BackdropReusable(stampA, stampSame, region, default);                                 // nothing moved → reuse
        bool reuseFar  = AcrylicBackdropMath.BackdropReusable(stampA, stampSame, region, new RectF(1500f, 900f, 100f, 80f));       // damage elsewhere (e.g. bottom player bar) → reuse
        bool reblurHit = !AcrylicBackdropMath.BackdropReusable(stampA, stampSame, region, new RectF(150f, 120f, 40f, 40f));        // damage inside the snapshot region → re-blur
        bool reblurGeo = !AcrylicBackdropMath.BackdropReusable(stampA, stampMoved, region, default);                               // geometry changed → re-blur
        bool reblurSource = !AcrylicBackdropMath.BackdropReusable(stampSource, stampOtherSource, region, default);
        bool reblurClip = !AcrylicBackdropMath.BackdropReusable(stampSource, stampOtherClip, region, default);
        Check("64n5. acrylic retained-backdrop cache invalidates on geometry, damage, source-target, or clip changes",
            reuseNone && reuseFar && reblurHit && reblurGeo && reblurSource && reblurClip,
            $"reuse(none)={reuseNone} reuse(far)={reuseFar} hit={reblurHit} geo={reblurGeo} source={reblurSource} clip={reblurClip}");

        // gate.acrylic.stampSubPixelJitterHits (E7): the stamp rect + scale are QUANTIZED into the cache key, so a
        // presence-spring settle's sub-pixel rect wobble (<0.5 device px) + a 1-ULP fractional-DPI scale wobble land in
        // the SAME grid cell/scale bucket ⇒ the stamps compare equal ⇒ the cached blur is reused (no permanent per-frame
        // re-blur). Composite position stays exact (it reads L.DeviceRect, not the stamp).
        var jA = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080);
        var jB = AcrylicBackdropMath.Stamp(new RectF(100.3f, 80.2f, 300.1f, 199.9f), 30f, 1f + 1e-6f, 1920, 1080);
        var jC = AcrylicBackdropMath.Stamp(new RectF(99.7f, 79.8f, 300.3f, 200.4f), 30f, 1f - 1e-6f, 1920, 1080);
        bool jitterHits = jA.Equals(jB) && jA.Equals(jC)
            && AcrylicBackdropMath.BackdropReusable(jA, jB, new RectF(0, 0, 100, 100), default);
        Check("gate.acrylic.stampSubPixelJitterHits: sub-0.5px rect wobble + 1-ULP scale wobble quantize to the same stamp (reusable)",
            jitterHits, $"AB={jA.Equals(jB)} AC={jA.Equals(jC)}");

        // gate.acrylic.stampWholePixelMisses (E7): a ≥1 device-px move crosses a grid cell ⇒ the stamps differ ⇒ re-blur.
        // Verified at 100% DPI (1 DIP = 1 device px) AND at 200% DPI (0.6 DIP = 1.2 device px) — a fractional-DPI-safe move.
        var wA = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 1f, 1920, 1080);
        var wB = AcrylicBackdropMath.Stamp(new RectF(101.6f, 80f, 300f, 200f), 30f, 1f, 1920, 1080);   // +1.6 device px
        var wC0 = AcrylicBackdropMath.Stamp(new RectF(100f, 80f, 300f, 200f), 30f, 2f, 3840, 2160);
        var wC1 = AcrylicBackdropMath.Stamp(new RectF(100.6f, 80f, 300f, 200f), 30f, 2f, 3840, 2160); // +1.2 device px @2x
        bool wholeMisses = !wA.Equals(wB) && !wC0.Equals(wC1);
        Check("gate.acrylic.stampWholePixelMisses: a >=1 device-px move changes the quantized stamp (re-blur) at 100% and 200% DPI",
            wholeMisses, $"move1x={!wA.Equals(wB)} move2x={!wC0.Equals(wC1)}");

        // gate.acrylic.tightDamageRegion (E8): reuse tests damage against the TIGHT rect+8, not the kernel-inflated
        // snapshot region. A damage rect inside the ±KernelRadius·down halo but outside rect+8 ⇒ reusable; one overlapping
        // the rect ⇒ re-blur. Layer rect (200,160,200,120): tight = (192,152,216,136); inflated pad @/4 = 88 ⇒ 112..488.
        var tStamp = AcrylicBackdropMath.Stamp(new RectF(200f, 160f, 200f, 120f), 30f, 1f, 1920, 1080);
        AcrylicBackdropMath.SnapshotRegionTight(new RectF(200f, 160f, 200f, 120f), 1f, 1920, 1080, out int ttx, out int tty, out int ttw, out int tth);
        var tightR = new RectF(ttx, tty, ttw, tth);
        bool haloReuse = AcrylicBackdropMath.BackdropReusable(tStamp, tStamp, tightR, new RectF(130f, 160f, 20f, 20f)); // in halo (112..), left of tight 192 → reuse
        bool rectReblur = !AcrylicBackdropMath.BackdropReusable(tStamp, tStamp, tightR, new RectF(210f, 170f, 20f, 20f)); // inside rect → re-blur
        bool tightBox = ttx == 192 && tty == 152 && ttw == 216 && tth == 136;
        Check("gate.acrylic.tightDamageRegion: damage in the blur halo but outside rect+8 reuses; damage on the rect re-blurs",
            haloReuse && rectReblur && tightBox, $"halo={haloReuse} rect={rectReblur} tight=({ttx},{tty},{ttw},{tth})");

        // gate.acrylic.ownSubtreeDamageCarvedOut (E9): damage entries emitted by the layer's OWN subtree (the contiguous
        // range [push,pop)) draw ON TOP of its snapshot ⇒ can never invalidate it ⇒ excluded from the reuse test; an entry
        // OUTSIDE the range that overlaps the tight region still forces a re-blur; DamageOverflow ⇒ the whole-frame union
        // is used (no carve-out — the safe fallback).
        ReadOnlySpan<RectF> ownOnly = new[] { new RectF(1500f, 900f, 50f, 50f), new RectF(210f, 170f, 20f, 20f), new RectF(230f, 190f, 20f, 20f) };
        bool ownCarved = AcrylicBackdropMath.OwnSubtreeReusable(tStamp, tStamp, tightR, ownOnly, 3, 1, 3, false, default); // idx1,2 own (inside tight) carved; idx0 far → reuse
        ReadOnlySpan<RectF> withExternal = new[] { new RectF(210f, 170f, 20f, 20f), new RectF(300f, 200f, 10f, 10f) };
        bool externalBlocks = !AcrylicBackdropMath.OwnSubtreeReusable(tStamp, tStamp, tightR, withExternal, 2, 1, 2, false, default); // idx0 external inside tight → re-blur
        bool overflowUnion = !AcrylicBackdropMath.OwnSubtreeReusable(tStamp, tStamp, tightR, ownOnly, 3, 0, 3, true, new RectF(210f, 170f, 20f, 20f)); // overflow → union inside tight → re-blur
        Check("gate.acrylic.ownSubtreeDamageCarvedOut: own-subtree entries excluded; an external entry on the tight region re-blurs; overflow falls back to the union",
            ownCarved && externalBlocks && overflowUnion, $"carved={ownCarved} external={externalBlocks} overflow={overflowUnion}");

        KawaseChainChecks();
    }

    static void ContentDialogChromeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("dialogchrome", new Size2(760, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new ContentDialogProbe());

        for (int i = 0; i < 24; i++) host.RunFrame();   // open + settle the modal scale/fade

        var title = FindTextNode(host.Scene, strings, host.Scene.Root, "Save your work?");
        NodeHandle outer = title;
        while (!outer.IsNull)
        {
            ref var p = ref host.Scene.Paint(outer);
            if (Near(p.BorderWidth, 1f, 0.01f) && ColorClose(p.BorderColor, Tok.StrokeSurfaceDefault, 0.02f))
                break;
            outer = host.Scene.Parent(outer);
        }

        var top = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 0);
        var sep = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 1);
        var command = outer.IsNull ? NodeHandle.Null : Child(host.Scene, outer, 2);

        bool realOuterBorder = !outer.IsNull
            && ColorClose(host.Scene.Paint(outer).Fill, Tok.FillSolidBase, 0.02f)
            && ColorClose(host.Scene.Paint(outer).BorderColor, Tok.StrokeSurfaceDefault, 0.02f)
            && Near(host.Scene.Paint(outer).BorderWidth, 1f, 0.01f)
            && Near(host.Scene.Paint(outer).Corners.TopLeft, Radii.Overlay, 0.01f)
            && (host.Scene.Flags(outer) & NodeFlags.ClipsToBounds) != 0;
        bool topOverlay = !top.IsNull && ColorClose(host.Scene.Paint(top).Fill, Tok.FillLayerAlt, 0.02f)
            && Near(host.Scene.Layout(top).Padding.Left, 24f, 0.01f)
            && Near(host.Scene.Paint(top).Corners.TopLeft, 0f, 0.01f)
            && Near(host.Scene.Paint(top).Corners.BottomLeft, 0f, 0.01f);
        bool separator = !sep.IsNull && Near(host.Scene.Bounds(sep).H, 1f, 0.01f)
            && ColorClose(host.Scene.Paint(sep).Fill, Tok.StrokeCardDefault, 0.02f);
        bool commandRow = !command.IsNull && ColorClose(host.Scene.Paint(command).Fill, Tok.FillSolidBase, 0.02f)
            && Near(host.Scene.Layout(command).Padding.Left, 24f, 0.01f)
            && Near(host.Scene.Layout(command).Gap, 8f, 0.01f)
            && Near(host.Scene.Paint(command).Corners.TopLeft, 0f, 0.01f)
            && Near(host.Scene.Paint(command).Corners.BottomLeft, 0f, 0.01f);

        Check("64g. ContentDialog chrome: real outer border; square internal content/command layers",
            realOuterBorder && topOverlay && separator && commandRow,
            $"outer={realOuterBorder} top={topOverlay} sep={separator} cmd={commandRow}");
    }

    static void TeachingTipPlacementChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tipplace", new Size2(760, 520), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new TeachingTipProbe());

        for (int i = 0; i < 16; i++) host.RunFrame();   // mount, place, publish placement signal, settle the beak component

        var triggerText = FindTextNode(host.Scene, strings, host.Scene.Root, "Show teaching tip");
        NodeHandle trigger = triggerText;
        while (!trigger.IsNull && host.Scene.Interaction(trigger).Role != AutomationRole.Button)
            trigger = host.Scene.Parent(trigger);
        var beakStroke = FindPolylineStrokeNode(host.Scene, host.Scene.Root);
        var ar = trigger.IsNull ? default : host.Scene.AbsoluteRect(trigger);
        var br = beakStroke.IsNull ? default : host.Scene.AbsoluteRect(beakStroke);
        float anchorCx = ar.X + ar.W * 0.5f;
        float beakCx = br.X + br.W * 0.5f;
        bool aligned = !trigger.IsNull && !beakStroke.IsNull && Near(beakCx, anchorCx, 1.5f);

        // TEMP-DIAG
        for (var n = beakStroke; !n.IsNull; n = host.Scene.Parent(n))
        {
            var bb = host.Scene.Bounds(n);
            var pp = host.Scene.Paint(n);
            Console.Error.WriteLine($"[64h] node={n.Raw.Index} bounds=({bb.X:0.0},{bb.Y:0.0} {bb.W:0.0}x{bb.H:0.0}) tdx={pp.LocalTransform.Dx:0.0} tdy={pp.LocalTransform.Dy:0.0}");
        }
        Console.Error.WriteLine($"[64h] anchor=({ar.X:0.0},{ar.Y:0.0} {ar.W:0.0}x{ar.H:0.0})");

        Check("64h. TeachingTip tail aligns to resolved target center after popup placement",
            aligned, $"anchorCx={anchorCx:0.0} beakCx={beakCx:0.0} dx={beakCx - anchorCx:0.0}");
    }

    static void MenuFlyoutStyleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("menustyle", new Size2(480, 360), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var root = new OverlayProbe();
        using var host = new AppHost(app, window, device, fonts, strings, root);

        host.RunFrame();
        var items = new[]
        {
            new MenuFlyoutItem("Small", Icons.Tag),
            new MenuFlyoutItem("Medium", Icons.Tag),
            new MenuFlyoutItem("Large", Icons.Tag),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem("Disabled", Icons.Cancel, false),
        };
        root.Service!.Open(() => root.Anchor, () => MenuFlyout.Create(items, () => root.Service!.CloseTop()), FlyoutPlacement.BottomLeft);
        host.RunFrame();

        var rows = Roles(host.Scene, AutomationRole.MenuItem);
        bool rowMetrics = rows.Count == 4 && Near(host.Scene.Bounds(rows[0]).H, 36f);
        // Walk up from a row to the acrylic FlyoutSurface (the presenter card; structure: surface > clip > content > rows).
        NodeHandle surface = rows.Count > 0 ? rows[0] : NodeHandle.Null;
        while (!surface.IsNull && !host.Scene.TryGetAcrylic(surface, out _) && !ChildHasAcrylic(host.Scene, surface)) surface = host.Scene.Parent(surface);
        bool acrylic = !surface.IsNull;
        float surfaceW = surface.IsNull ? 0f : host.Scene.AbsoluteRect(surface).W;
        bool minWidth = surfaceW >= 96f;   // FlyoutThemeMinWidth (generic.xaml)
        bool hasLayer = device.LastLayers.Count > 0 && device.LayerBalance == 0;
        bool disabled = rows.Count == 4 && (host.Scene.Interaction(rows[3]).HandlerMask & InteractionInfo.ClickBit) == 0;

        Check("64b. MenuFlyout: WinUI-like presenter chrome, row metrics, disabled command",
            rowMetrics && minWidth && acrylic && hasLayer && disabled,
            $"rows={rows.Count} h={(rows.Count > 0 ? host.Scene.Bounds(rows[0]).H : 0):0} w={surfaceW:0} acrylic={acrylic} layers={device.LastLayers.Count} disabled={disabled}");
    }

    static void SplitButtonStyleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("splitstyle", new Size2(360, 160), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        using var host = new AppHost(app, window, device, fonts, strings, new SplitButtonProbe());
        host.RunFrame();

        var buttons = Roles(host.Scene, AutomationRole.Button);
        var primary = buttons.Count > 0 ? buttons[0] : NodeHandle.Null;
        var drop = buttons.Count > 1 ? buttons[1] : NodeHandle.Null;
        var outer = primary.IsNull ? NodeHandle.Null : host.Scene.Parent(primary);
        bool twoHalves = buttons.Count == 2;
        bool joinedChrome = !outer.IsNull && host.Scene.Paint(outer).Fill.A > 0f && Near(host.Scene.Paint(outer).BorderWidth, 1f)
                            && (host.Scene.Flags(outer) & NodeFlags.ClipsToBounds) != 0;
        bool halfMetrics = !primary.IsNull && !drop.IsNull
                           // WinUI SplitButtonSecondaryButtonSize = 35 (width); control height = 32.
                           && Near(host.Scene.Bounds(primary).H, 32f) && Near(host.Scene.Bounds(drop).W, 35f) && Near(host.Scene.Bounds(drop).H, 32f);
        bool transparentHalves = !primary.IsNull && !drop.IsNull
                                 && host.Scene.Paint(primary).Fill.A == 0f && host.Scene.Paint(drop).Fill.A == 0f;

        Check("64c. SplitButton: joined outer chrome with independently interactive halves",
            twoHalves && joinedChrome && halfMetrics && transparentHalves,
            $"buttons={buttons.Count} chrome={joinedChrome} primaryH={(primary.IsNull ? 0 : host.Scene.Bounds(primary).H):0} drop={(drop.IsNull ? 0 : host.Scene.Bounds(drop).W):0}x{(drop.IsNull ? 0 : host.Scene.Bounds(drop).H):0}");

        using var app2 = new HeadlessPlatformApp();
        var window2 = new HeadlessWindow(new WindowDesc("splitlong", new Size2(360, 220), 1f));
        window2.Show();
        var device2 = new HeadlessGpuDevice();
        var fonts2 = new HeadlessFontSystem(strings);
        using var host2 = new AppHost(app2, window2, device2, fonts2, strings, new SplitButtonLongMenuProbe());
        host2.RunFrame();
        var splitButtons = Roles(host2.Scene, AutomationRole.Button);
        var secondary = splitButtons.Count > 1 ? splitButtons[1] : NodeHandle.Null;
        if (!secondary.IsNull) ClickNode(host2, window2, secondary);
        host2.RunFrame();
        var specialText = FindTextNode(host2.Scene, strings, host2.Scene.Root, "Paste special");
        NodeHandle surface = specialText;
        while (!surface.IsNull && !host2.Scene.TryGetAcrylic(surface, out _) && !ChildHasAcrylic(host2.Scene, surface)) surface = host2.Scene.Parent(surface);
        var sr = surface.IsNull ? default : host2.Scene.AbsoluteRect(surface);
        var tr = specialText.IsNull ? default : host2.Scene.AbsoluteRect(specialText);
        bool longLabelFits = !surface.IsNull && !specialText.IsNull && tr.Right <= sr.Right - 10f && sr.W > 150f;
        Check("64c2. SplitButton menu: long item text fits inside the flyout surface",
            longLabelFits, $"surfaceW={sr.W:0} textRight={tr.Right:0} surfaceRight={sr.Right:0}");
    }
    static void G5fToastChecks(StringTable strings)
    {
        int savedMax = Toast.MaxVisible;
        Toast.MaxVisible = 3;
        try
        {
            // gate.toast.queue — MaxVisible=3 stacking + FIFO overflow: 4 shown ⇒ 3 rendered + only 3 armed (the 4th
            // waits); closing the front releases the queued one into the visible window (its countdown starts).
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g5f-toast-q", new Size2(640, 480), 1f));
                window.Show();
                var probe = new OverlayProbe();
                var clock = new ManualFrameTimeSource();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
                void Settle() { for (int i = 0; i < 8; i++) { clock.Advance(16f); host.RunFrame(); } }
                host.RunFrame();
                var h1 = Toast.Show("one",   new ToastOptions { Title = "1" });
                var h2 = Toast.Show("two",   new ToastOptions { Title = "2" });
                var h3 = Toast.Show("three", new ToastOptions { Title = "3" });
                var h4 = Toast.Show("four",  new ToastOptions { Title = "4" });
                Settle();
                var ctl = Toast.Default!;
                bool queuedAll = ctl.Items.Count == 4;
                bool visible3 = Roles(host.Scene, AutomationRole.InfoBar).Count == 3;
                bool armed3 = ctl.Items[0].Armed && ctl.Items[1].Armed && ctl.Items[2].Armed;
                bool fourthWaits = !ctl.Items[3].Armed;
                h1.Close(); Settle();
                bool released = ctl.Items.Count == 3 && ctl.Items[2].Armed;   // former 4th now visible + counting
                Check("gate.toast.queue", queuedAll && visible3 && armed3 && fourthWaits && released,
                    $"queued4={queuedAll} rendered3={visible3} armed3={armed3} fourthWaits={fourthWaits} released={released}");
                Toast.CloseAll();
            }

            // gate.toast.autodismiss — a toast closes itself after DurationMs via the HostTimerQueue. Driven by
            // host.Paint(0) (16ms/step, unconditional advance — the gate.timer.* convention; RunFrame is wake-gated and
            // an armed-but-not-due timer alone doesn't wake it).
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g5f-toast-ad", new Size2(640, 480), 1f));
                window.Show();
                var probe = new OverlayProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
                host.Paint(0);
                var h = Toast.Show("bye", new ToastOptions { DurationMs = 200f });
                var ctl = Toast.Default!;
                bool armed = ctl.Items.Count == 1 && ctl.Items[0].Armed;
                for (int i = 0; i < 5; i++) host.Paint(0);   // ~80ms — armed, not due
                bool stillOpenMid = ctl.Items.Count == 1 && h.IsOpen;
                for (int i = 0; i < 20; i++) host.Paint(0);  // well past 200ms ⇒ fires
                bool closed = ctl.Items.Count == 0 && !h.IsOpen;
                Check("gate.toast.autodismiss", armed && stillOpenMid && closed,
                    $"armed={armed} mid={stillOpenMid} closed={closed}");
                Toast.CloseAll();
            }

            // gate.toast.hover-pause — hovering the strip FREEZES the remaining time; advancing past the original due
            // does not fire; resume re-arms for the banked remainder, which then fires.
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("g5f-toast-hp", new Size2(640, 480), 1f));
                window.Show();
                var probe = new OverlayProbe();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
                host.Paint(0);
                var h = Toast.Show("hover", new ToastOptions { DurationMs = 200f });
                var ctl = Toast.Default!;
                for (int i = 0; i < 5; i++) host.Paint(0);   // ~80ms elapsed, armed, not due
                bool armedBefore = ctl.Items[0].Armed;
                ctl.SetPaused(true);   // strip hover: banks the ~120ms remaining, cancels the pending fire
                bool pausedCancels = !ctl.Items[0].Armed;
                for (int i = 0; i < 25; i++) host.Paint(0);  // ~400ms — way past the original due, but paused ⇒ no fire
                bool frozen = ctl.Items.Count == 1 && h.IsOpen;
                ctl.SetPaused(false);  // resume → re-arm for the banked remainder
                bool rearmed = ctl.Items[0].Armed;
                for (int i = 0; i < 3; i++) host.Paint(0);   // ~48ms — banked remainder not yet elapsed
                bool stillOpen = ctl.Items.Count == 1;
                for (int i = 0; i < 15; i++) host.Paint(0);  // remainder elapsed ⇒ fires
                bool fired = ctl.Items.Count == 0 && !h.IsOpen;
                Check("gate.toast.hover-pause", armedBefore && pausedCancels && frozen && rearmed && stillOpen && fired,
                    $"armed={armedBefore} pausedCancel={pausedCancels} frozen={frozen} rearmed={rearmed} stillOpen={stillOpen} fired={fired}");
                Toast.CloseAll();
            }

            // gate.toast.severity — the toast severity visuals ARE InfoBar's (the shared SeverityVisuals helper), so they
            // cannot drift.
            {
                bool allMatch = true;
                foreach (var sev in new[] { InfoBarSeverity.Informational, InfoBarSeverity.Success, InfoBarSeverity.Warning, InfoBarSeverity.Error })
                {
                    var v = SeverityVisuals.For(sev);
                    var s = InfoBar.InfoBarTemplateSettings.For(sev);
                    allMatch &= v.Glyph == s.Glyph
                             && v.IconBackground.Equals(s.IconBackground)
                             && v.IconForeground.Equals(s.IconForeground)
                             && v.Background.Equals(s.Background);
                }
                Check("gate.toast.severity", allMatch, $"sharedIdentity={allMatch}");
            }

            // gate.toast.edge-inset (G7 item 9) — EdgeInset reserves extra DIP on the docked edge so the strip clears a
            // player/chrome bar (Wavee's above-the-player-bar placement). On a Bottom* dock, a larger inset lifts the
            // toast UP by ~that many DIP (the strip's bottom padding = 24 + EdgeInset). Measured off the rendered node.
            {
                float savedInset = Toast.EdgeInset;
                var savedPlacement = Toast.Placement;
                Toast.Placement = ToastPlacement.BottomRight;   // a bottom dock
                float MeasureToastTop(float inset)
                {
                    Toast.EdgeInset = inset;
                    using var app = new HeadlessPlatformApp();
                    var window = new HeadlessWindow(new WindowDesc("g5f-toast-inset", new Size2(640, 480), 1f));
                    window.Show();
                    var probe = new OverlayProbe();
                    var clock = new ManualFrameTimeSource();
                    using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
                    host.RunFrame();
                    Toast.Show("inset", new ToastOptions { Title = "x" });
                    for (int i = 0; i < 8; i++) { clock.Advance(16f); host.RunFrame(); }
                    var toasts = Roles(host.Scene, AutomationRole.InfoBar);
                    float y = toasts.Count > 0 ? host.Scene.AbsoluteRect(toasts[0]).Y : float.NaN;
                    Toast.CloseAll();
                    return y;
                }
                float top0 = MeasureToastTop(0f);
                float top120 = MeasureToastTop(120f);
                Toast.EdgeInset = savedInset; Toast.Placement = savedPlacement;
                float lift = top0 - top120;
                bool shiftedUp = !float.IsNaN(top0) && !float.IsNaN(top120) && lift > 100f && lift < 140f;   // ~120 up
                Check("gate.toast.edge-inset: a Bottom* dock lifts the toast strip up by ~EdgeInset DIP",
                    shiftedUp, $"top@0={top0:0.#} top@120={top120:0.#} lift={lift:0.#}");
            }
        }
        finally { Toast.MaxVisible = savedMax; Toast.Default = null; }
    }

    static void KawaseChainChecks()
    {
        // gate.kawase.sigmaMapping: σ14/20/30 (WinUI acrylic band) map to the expected (iterations, offset) with the
        // offset in the sane [1,2] range and 3–4 iterations, and the chain's effective sigma round-trips to the input σ
        // within tolerance (in-band ⇒ exact). Calibrated K = SigmaPerLevel = 1.25 (validated by the visual A/B).
        AcrylicKawaseMath.SelectChain(14f, 1f, out int i14, out float o14);
        AcrylicKawaseMath.SelectChain(20f, 1f, out int i20, out float o20);
        AcrylicKawaseMath.SelectChain(30f, 1f, out int i30, out float o30);
        bool mapOk =
            i14 == 3 && MathF.Abs(o14 - 1.4f) < 1e-4f &&
            i20 == 3 && MathF.Abs(o20 - 2.0f) < 1e-4f &&
            i30 == 4 && MathF.Abs(o30 - 1.5f) < 1e-4f;
        bool roundTrip =
            MathF.Abs(AcrylicKawaseMath.EffectiveSigma(i14, o14) - 14f) < 0.5f &&
            MathF.Abs(AcrylicKawaseMath.EffectiveSigma(i20, o20) - 20f) < 0.5f &&
            MathF.Abs(AcrylicKawaseMath.EffectiveSigma(i30, o30) - 30f) < 0.5f;
        // every mapped chain stays within the pyramid depth and the sane offset band
        bool bounded = true;
        for (float s = 1f; s <= 40f; s += 0.37f)
        {
            AcrylicKawaseMath.SelectChain(s, 1f, out int it, out float of);
            if (it < 1 || it > AcrylicKawaseMath.MaxIterations || of < AcrylicKawaseMath.OffsetMin - 1e-4f || of > AcrylicKawaseMath.OffsetMax + 1e-4f) bounded = false;
        }
        Check("gate.kawase.sigmaMapping: σ14/20/30 → (3,1.4)/(3,2.0)/(4,1.5), offset∈[1,2], iters∈[3,4], effective σ round-trips",
            mapOk && roundTrip && bounded,
            $"σ14=({i14},{o14:0.00}) σ20=({i20},{o20:0.00}) σ30=({i30},{o30:0.00}) rt={roundTrip} bounded={bounded}");

        // gate.kawase.padCoversSupport: at each iteration count the snapshot pad (PadPx at the max offset) covers the
        // chain's blur support, AND SnapshotRegion inflated by that pad actually contains the ±support halo on every side
        // (mirrors 64n3: unclamped rect grows by 2·pad). down = 1 ⇒ the pad passes straight through as kernelRadius·1.
        bool padOk = true, regionOk = true;
        for (int it = 1; it <= AcrylicKawaseMath.MaxIterations; it++)
        {
            float support = AcrylicKawaseMath.SupportPx(it, AcrylicKawaseMath.OffsetMax);
            int pad = AcrylicKawaseMath.PadPx(it, AcrylicKawaseMath.OffsetMax);
            if (pad < support) padOk = false;
            AcrylicBackdropMath.SnapshotRegion(new RectF(400f, 300f, 200f, 120f), 1f, 1, pad, 4000, 4000,
                out int rx, out int ry, out int rw, out int rh);
            // unclamped (region well inside a 4000² canvas): width = rect + 2·pad, origin = rect − pad
            if (rx != 400 - pad || ry != 300 - pad || rw != 200 + 2 * pad || rh != 120 + 2 * pad) regionOk = false;
        }
        Check("gate.kawase.padCoversSupport: snapshot pad ≥ chain support at every iteration count and inflates the region by ±pad",
            padOk && regionOk, $"padOk={padOk} regionOk={regionOk} pad@4={AcrylicKawaseMath.PadPx(4, 2f)}");
    }

}
