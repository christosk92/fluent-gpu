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




static class TextSuite
{
    public static void Run(StringTable strings)
    {
        WaveCTextPipelineChecks(strings);
        WaveCSpanTextChecks(strings);
    }

    static void WaveCTextPipelineChecks(StringTable strings)
    {
        // (a)+(b) the numeric weight threads TextEl → TextStyle → the DrawGlyphRun op; Bold sugar resolves to 700,
        // the default to 400, and an explicit Weight beats Bold (the TextEl.ResolvedWeight rule).
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl
        {
            Direction = 1,
            Children =
            [
                new TextEl("semibold") { Weight = 600 },
                new TextEl("boldsugar") { Bold = true },
                new TextEl("normal"),
                new TextEl("semilight") { Weight = 350, Bold = true },
            ],
        }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400, 300), 1f, ColorF.Transparent));
        int w600 = -1, w700 = -1, w400 = -1, w350 = -1;
        foreach (var g in dev.LastGlyphs)
        {
            string t = strings.Resolve(g.Text);
            if (t == "semibold") w600 = g.Weight;
            else if (t == "boldsugar") w700 = g.Weight;
            else if (t == "normal") w400 = g.Weight;
            else if (t == "semilight") w350 = g.Weight;
        }
        Check("WC-TXT.a numeric Weight reaches the DrawGlyphRun op (Weight=600 → op 600)", w600 == 600, $"weight={w600}");
        Check("WC-TXT.b Bold sugar → 700; default → 400; explicit Weight beats Bold", w700 == 700 && w400 == 400 && w350 == 350,
            $"bold={w700} normal={w400} explicit={w350}");

        // (c) LineHeight changes the measured height deterministically: natural 14×1.4 = 19.6;
        // MaxHeight = max(natural, LineHeight) (BaseTextBlockStyle default, TextBlock_themeresources.xaml:16);
        // BlockLineHeight = LineHeight exactly, even below natural.
        var fonts = new HeadlessFontSystem(strings);
        var hello = strings.Intern("Hello");
        float hNat = fonts.Measure(hello, new TextStyle(default, 14f, 400)).Size.Height;
        float hMax30 = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineHeight: 30f)).Size.Height;
        float hMax10 = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineHeight: 10f)).Size.Height;
        float hBlock10 = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineHeight: 10f, Stacking: LineStacking.BlockLineHeight)).Size.Height;
        bool lineHeightOk = Near(hNat, 19.6f, 0.01f) && Near(hMax30, 30f, 0.01f) && Near(hMax10, 19.6f, 0.01f) && Near(hBlock10, 10f, 0.01f);
        // …and through the element pipeline: a TextEl with LineHeight=30 lays out 30 DIP tall.
        var lhScene = LayoutTree(strings, new BoxEl { Direction = 1, Children = [new TextEl("line") { LineHeight = 30f }] });
        float nodeH = lhScene.AbsoluteRect(Child(lhScene, lhScene.Root, 0)).H;
        Check("WC-TXT.c LineHeight resolves per LineStackingStrategy (MaxHeight clamps up, BlockLineHeight exact)",
            lineHeightOk && Near(nodeH, 30f, 0.01f),
            $"nat={hNat:0.0} max30={hMax30:0.0} max10={hMax10:0.0} block10={hBlock10:0.0} nodeH={nodeH:0.0}");

        // (d) CharacterSpacing (1/1000 em) widens the measured advance: 5 chars × (14×0.55 + 14×100/1000) = 45.5 vs 38.5.
        float wPlain = fonts.Measure(hello, new TextStyle(default, 14f, 400)).Size.Width;
        float wSpaced = fonts.Measure(hello, new TextStyle(default, 14f, 400, CharSpacing: 100f)).Size.Width;
        Check("WC-TXT.d CharacterSpacing widens the measured advance (+size×spacing/1000 per char)",
            Near(wPlain, 38.5f, 0.01f) && Near(wSpaced, 45.5f, 0.01f), $"plain={wPlain:0.0} spaced={wSpaced:0.0}");

        // (e) TextLineBounds=Tight trims the line box to cap-height..baseline: 14×0.7 = 9.8 < 19.6 full,
        // and the baseline lands at the box bottom (= the tight height).
        var mTight = fonts.Measure(hello, new TextStyle(default, 14f, 400, LineBounds: TextLineBounds.Tight));
        Check("WC-TXT.e TextLineBounds=Tight reduces the measured height to cap..baseline",
            Near(mTight.Size.Height, 9.8f, 0.01f) && Near(mTight.Baseline, 9.8f, 0.01f),
            $"tightH={mTight.Size.Height:0.0} baseline={mTight.Baseline:0.0} fullH={hNat:0.0}");

        // (f) the ramp carries the WinUI values: sizes 12/14/14/18/20/28/40/68 (TextBlock_themeresources.xaml:3-9),
        // SemiBold 600 on BodyStrong/Subtitle/Title/TitleLarge/Display (:13 inherited; :26, :36-51), line heights
        // 16/20/20/24/28/36/52/92 (the Fluent type-ramp spec); Strong() now means SemiBold 600, not Bold 700.
        var cap = Caption("x"); var body = Body("x"); var bs = BodyStrong("x"); var bl = BodyLarge("x");
        var sub = Subtitle("x"); var ti = Title("x"); var tl = TitleLarge("x"); var di = Display("x");
        bool sizes = cap.Size == 12f && body.Size == 14f && bs.Size == 14f && bl.Size == 18f
                  && sub.Size == 20f && ti.Size == 28f && tl.Size == 40f && di.Size == 68f;
        bool lineHs = cap.LineHeight == 16f && body.LineHeight == 20f && bs.LineHeight == 20f && bl.LineHeight == 24f
                   && sub.LineHeight == 28f && ti.LineHeight == 36f && tl.LineHeight == 52f && di.LineHeight == 92f;
        bool weights = cap.ResolvedWeight == 400 && body.ResolvedWeight == 400 && bs.Weight == 600
                    && sub.Weight == 600 && ti.Weight == 600 && tl.Weight == 600 && di.Weight == 600;
        bool strong = new TextEl("x").Strong().Weight == 600 && new TextEl("x").FontWeight(350).ResolvedWeight == 350;
        Check("WC-TXT.f type ramp carries the WinUI values (sizes, line heights, SemiBold 600; Strong()=600)",
            sizes && lineHs && weights && strong, $"sizes={sizes} lineHs={lineHs} weights={weights} strong={strong}");
    }

    static void WaveCSpanTextChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var clip = new HeadlessClipboard();
        var dispatcher = new InputDispatcher(scene) { Fonts = fonts, Clipboard = clip };
        CursorId cursor = CursorId.Arrow;
        dispatcher.OnCursorChanged = c => cursor = c;
        bool linkClicked = false;
        var gold = ColorF.FromRgba(0xFF, 0xD7, 0x00);
        var red = ColorF.FromRgba(0xFF, 0x00, 0x00);

        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200, Height = 100,
            Children =
            [
                // [0] mixed-weight flow: "aaaa " base 400 (5 × 5.5 = 27.5) + "bbbb" 700 (4 × 6.2 = 24.8) → 52.3 one flow
                new SpanTextEl([new TextSpan("aaaa "), new TextSpan("bbbb", Weight: 700)]) { Size = 10f },
                // [1] hyperlink paragraph: link "docs" covers chars [9,13) → x [49.5, 71.5) on its line
                new SpanTextEl(
                [
                    new TextSpan("Read the "),
                    new TextSpan("docs", Color: gold, Underline: true, OnClick: () => linkClicked = true),
                    new TextSpan(" now"),
                ]) { Size = 10f },
                // [2] selectable paragraph with a per-control highlight override (api-04)
                new SpanTextEl([new TextSpan("Hello world")]) { Size = 10f, IsTextSelectionEnabled = true, SelectionHighlightColor = red },
                // [3] plain TextEl, selection opt-in (WinUI TextBlock.cpp:583), engine-default highlight brush
                new TextEl("Copy me too") { Size = 10f, IsTextSelectionEnabled = true },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);

        var mixed = Child(scene, scene.Root, 0);
        var linky = Child(scene, scene.Root, 1);
        var sel = Child(scene, scene.Root, 2);
        var plain = Child(scene, scene.Root, 3);

        // (a) rtb-01: the paragraph measures as ONE flow with per-span advances (NOT a uniform-weight run), wraps as
        // one unit, and reaches the draw stream as ONE glyph op carrying the span-run id.
        var mixedStyle = scene.Layout(mixed).TextStyle;
        var mUnwrapped = fonts.Measure(scene.Paint(mixed).Text, mixedStyle);
        var mWrapped = fonts.Measure(scene.Paint(mixed).Text, mixedStyle, 30f);   // "aaaa " | "bbbb" → 2 lines × 14
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
        int mixedOps = 0, mixedSpanId = 0;
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == "aaaa bbbb") { mixedOps++; mixedSpanId = g.SpanRunId; }
        Check("WC-SPAN.a mixed-weight spans measure as a SINGLE flow (52.3 = 5×5.5 + 4×6.2; wraps to 2 lines) and emit ONE glyph op",
            Near(mUnwrapped.Size.Width, 52.3f, 0.01f) && Near(mWrapped.Size.Height, 28f, 0.01f)
            && mixedOps == 1 && mixedSpanId != 0,
            $"w={mUnwrapped.Size.Width:0.0} wrapH={mWrapped.Size.Height:0.0} ops={mixedOps} spanId={mixedSpanId}");

        // (b) hyperlink span: Hand cursor over the span's laid rects (RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)),
        // arrow elsewhere on the same node, click fires the span's OnClick; the underline bar draws in the span color.
        var lr = scene.AbsoluteRect(linky);
        var overLink = new Point2(lr.X + 55f, lr.Y + 7f);     // inside "docs" [49.5, 71.5)
        var offLink = new Point2(lr.X + 10f, lr.Y + 7f);      // inside "Read the"
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, overLink, 0, 0) });
        bool handOverLink = cursor == CursorId.Hand;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, offLink, 0, 0) });
        bool arrowOffLink = cursor == CursorId.Arrow;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, overLink, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, overLink, 0, 0) });
        bool underlineInLinkColor = false;
        foreach (var r in dev.LastRects)
            if (ColorClose(r.Fill, gold, 0.01f) && Near(r.Rect.H, 1f, 0.01f) && Near(r.Rect.W, 22f, 0.01f)) underlineInLinkColor = true;
        Check("WC-SPAN.b hyperlink span: Hand over its rects, arrow off them, OnClick fires, underline bar in the span color",
            handOverLink && arrowOffLink && linkClicked && underlineInLinkColor,
            $"hand={handOverLink} arrow={arrowOffLink} clicked={linkClicked} underline={underlineInLinkColor}");

        // (c) rtb-02: drag-select publishes selection rects through the editor slab; Ctrl+C copies through the
        // clipboard seam (TextSelectionManager.cpp:30-41); double-click selects the word under the press.
        var sr = scene.AbsoluteRect(sel);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(sr.X + 1f, sr.Y + 7f), 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(sr.X + 29f, sr.Y + 7f), 0, 0) });   // → "Hello" [0,5)
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(sr.X + 29f, sr.Y + 7f), 0, 0) });
        var dragRects = scene.GetTextEditSelectionRects(sel);
        bool dragRectOk = dragRects.Length == 1 && Near(dragRects[0].W, 27.5f, 0.01f);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, 'C', Mods: KeyModifiers.Ctrl) });
        bool copiedHello = clip.TryGetText(out string copied1) && copied1 == "Hello";
        // double-click on "world" (chars [6,11) → x 33..60.5): press, release, press again inside the slop window
        var onWorld = new Point2(sr.X + 35f, sr.Y + 7f);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, onWorld, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, onWorld, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, onWorld, 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, onWorld, 0, 0) });
        bool wordSel = scene.TryGetTextSelection(sel, out int ws, out int we) && ws == 6 && we == 11;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, 'C', Mods: KeyModifiers.Ctrl) });
        bool copiedWorld = clip.TryGetText(out string copied2) && copied2 == "world";
        Check("WC-SPAN.c drag-select publishes rects + Ctrl+C copies; double-click selects the word",
            dragRectOk && copiedHello && wordSel && copiedWorld,
            $"rects={dragRects.Length} w={(dragRects.Length > 0 ? dragRects[0].W : 0):0.0} copy1='{copied1}' word=({ws},{we}) copy2='{copied2}'");

        // (d) api-04: the per-control SelectionHighlightColor reaches the draw; a control WITHOUT the override keeps
        // the host theme brush (TextControlSelectionHighlightColor ≡ system accent, TextSelectionManager.cpp:52-56).
        var themeBlue = ColorF.FromRgba(0x00, 0x78, 0xD4);
        var te = new TextEditStyle(themeBlue, ColorF.FromRgba(0xFF, 0xFF, 0xFF), ColorF.FromRgba(0xFF, 0xFF, 0xFF));
        SceneRecorder.Record(scene, dl, textEdit: te);   // "world" still selected on the override paragraph
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
        bool overrideReachesDraw = false, themeLeaked = false;
        foreach (var r in dev.LastRects)
        {
            if (ColorClose(r.Fill, red, 0.01f)) overrideReachesDraw = true;
            if (ColorClose(r.Fill, themeBlue, 0.01f)) themeLeaked = true;
        }
        // now select on the plain TextEl (no override) → the theme brush paints
        var pr = scene.AbsoluteRect(plain);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(pr.X + 1f, pr.Y + 7f), 0, 0) });
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(pr.X + 23f, pr.Y + 7f), 0, 0) });  // → "Copy" [0,4)
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(pr.X + 23f, pr.Y + 7f), 0, 0) });
        SceneRecorder.Record(scene, dl, textEdit: te);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200, 100), 1f, ColorF.Transparent));
        bool themeOnPlain = false;
        foreach (var r in dev.LastRects)
            if (ColorClose(r.Fill, themeBlue, 0.01f)) themeOnPlain = true;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.Key, default, 0, 'C', Mods: KeyModifiers.Ctrl) });
        bool copiedFromTextEl = clip.TryGetText(out string copied3) && copied3 == "Copy";
        Check("WC-SPAN.d SelectionHighlightColor override reaches the draw; default keeps the theme brush; TextEl opt-in selects",
            overrideReachesDraw && !themeLeaked && themeOnPlain && copiedFromTextEl,
            $"override={overrideReachesDraw} leak={themeLeaked} theme={themeOnPlain} copy3='{copied3}'");

        // (e) steady-state: with a live selection + span paragraphs, a re-record allocates ZERO managed bytes
        // (artifacts/measure cached at layout; the recorder only reads — phases 6–13 stay clean).
        SceneRecorder.Record(scene, dl, textEdit: te);   // warm growth (DrawList buffers, rect slabs)
        SceneRecorder.Record(scene, dl, textEdit: te);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        SceneRecorder.Record(scene, dl, textEdit: te);
        long recBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
        Check("WC-SPAN.e record with span paragraphs + live selection allocates 0 bytes", recBytes == 0, $"{recBytes} bytes");
    }
}
