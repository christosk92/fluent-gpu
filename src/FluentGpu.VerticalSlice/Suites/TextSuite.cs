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
        BoundSpansChecks(strings);
        GlyphAtlasUploadChecks();
        ColorGlyphBakeChecks();
        SpanEllipsisChecks();
        TextAlignChecks(strings);
    }

    // §4 TextEl.TextAlign / TextEl.FontFeatures (docs/plans/app-authoring-ergonomics-implementation.md §4). Headless advance
    // model at Size 10: 5.5 DIP per char, 14 DIP lines. The seam queries are called exactly as the input dispatcher calls
    // them (wrap width = QueryMaxWidth, align width = the node's laid width = the glyph op's Bounds.W), so a caret/range
    // x here is where the renderer places the glyph: the DirectWrite engine applies the SAME TextTypography.LineOffset
    // to its laid glyphs and cluster table (glyph-x ≡ hit-test is pinned on real DirectWrite in
    // FluentGpu.Windows.Tests/Scout/TextAlignTypographyTests).
    static void TextAlignChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var dispatcher = new InputDispatcher(scene) { Fonts = fonts };
        CursorId cursor = CursorId.Arrow;
        dispatcher.OnCursorChanged = c => cursor = c;
        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 300, Height = 200,
            Children =
            [
                // [0] NoWrap number measured at +Inf (11 DIP) but arranged 100 wide — the core case.
                new TextEl("42") { Size = 10f, Width = 100f, TextAlign = TextAlign.End, FontFeatures = FontFeatures.TabularFigures, Underline = true },
                // [1] same, with trailing spaces — they hang and must not shift the line.
                new TextEl("42  ") { Size = 10f, Width = 100f, TextAlign = TextAlign.End },
                // [2] End-aligned hyperlink paragraph: "Read the docs now" = 93.5 DIP in a 200 box → offset 106.5.
                new SpanTextEl([new TextSpan("Read the "), new TextSpan("docs", OnClick: () => { }), new TextSpan(" now")])
                { Size = 10f, Width = 200f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);
        var endNode = Child(scene, scene.Root, 0);
        var trailNode = Child(scene, scene.Root, 1);
        var linkNode = Child(scene, scene.Root, 2);

        // (a) the style + the draw op carry align/features; the op's box is the align width the render engine uses.
        var endStyle = scene.Layout(endNode).TextStyle;
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(300, 200), 1f, ColorF.Transparent));
        int opTypo = -1; float opBoxW = -1f;
        foreach (var g in dev.LastGlyphs)
            if (strings.Resolve(g.Text) == "42") { opTypo = g.Typography; opBoxW = g.Bounds.W; }
        Check("gate.text.align.op TextEl.TextAlign/FontFeatures reach TextStyle and the glyph op (packed Typography; Bounds.W = the arranged box)",
            endStyle.Align == TextAlign.End && endStyle.Features == FontFeatures.TabularFigures
            && TextTypography.AlignOf(opTypo) == TextAlign.End && TextTypography.FeaturesOf(opTypo) == FontFeatures.TabularFigures
            && Near(opBoxW, 100f, 0.01f),
            $"style={endStyle.Align}/{endStyle.Features} op={TextTypography.AlignOf(opTypo)}/{TextTypography.FeaturesOf(opTypo)} boxW={opBoxW:0.0}");

        // (b) end-aligned NoWrap text in a wider box: glyph x (= LineOffset(End, 100, 11) = 89) ≡ caret ≡ range rect ≡ hit-test.
        string t42 = "42";
        float boxW = scene.Bounds(endNode).W;
        float glyphX = TextTypography.LineOffset(TextAlign.End, boxW, 11f);
        fonts.GetCaret(t42, endStyle, float.PositiveInfinity, 0, out float caret0, out _, out _, out _, boxW);
        fonts.GetCaret(t42, endStyle, float.PositiveInfinity, 2, out float caret2, out _, out _, out _, boxW);
        Span<RectF> rr = stackalloc RectF[4];
        int nr = fonts.GetRangeRects(t42, endStyle, float.PositiveInfinity, 0, 2, rr, boxW);
        int hitLead = fonts.HitTestText(t42, endStyle, float.PositiveInfinity, new Point2(glyphX + 1f, 5f), out _, boxW);
        int hitSecond = fonts.HitTestText(t42, endStyle, float.PositiveInfinity, new Point2(glyphX + 7f, 5f), out _, boxW);
        int hitLeftOfText = fonts.HitTestText(t42, endStyle, float.PositiveInfinity, new Point2(20f, 5f), out _, boxW);
        bool barAligned = false;
        foreach (var r in dev.LastRects)
            if (Near(r.Rect.H, 1f, 0.01f) && Near(r.Rect.X, 89f, 0.01f) && Near(r.Rect.W, 11f, 0.01f)) barAligned = true;
        Check("gate.text.align.end-nowrap end-aligned NoWrap text in a wider box: glyph x, caret, range rect and hit-test agree (x = 89 = 100 − 11); the underline bar follows",
            Near(boxW, 100f, 0.01f) && Near(glyphX, 89f, 0.01f) && Near(caret0, 89f, 0.01f) && Near(caret2, 100f, 0.01f)
            && nr == 1 && Near(rr[0].X, 89f, 0.01f) && Near(rr[0].W, 11f, 0.01f)
            && hitLead == 0 && hitSecond == 1 && hitLeftOfText == 0 && barAligned,
            $"boxW={boxW:0.0} glyphX={glyphX:0.0} caret0={caret0:0.0} caret2={caret2:0.0} rect={(nr > 0 ? rr[0].ToString() : "none")} hits={hitLead}/{hitSecond}/{hitLeftOfText} bar={barAligned}");

        // (c) trailing spaces do not shift end alignment.
        var trailStyle = scene.Layout(trailNode).TextStyle;
        fonts.GetCaret("42  ", trailStyle, float.PositiveInfinity, 0, out float trailCaret0, out _, out _, out _, scene.Bounds(trailNode).W);
        Check("gate.text.align.trailing-space trailing spaces hang: \"42  \" End-aligns its digits exactly where \"42\" does",
            Near(trailCaret0, caret0, 0.01f), $"withSpaces={trailCaret0:0.00} plain={caret0:0.00}");

        // (d) center on a 2-line wrapped run: "aaaa bb" at 30 → "aaaa " (visible 22) | "bb" (11) → offsets 4 and 9.5.
        var center = new TextStyle(default, 10f, 400, TextWrap.Wrap, Align: TextAlign.Center);
        Span<RectF> cr = stackalloc RectF[4];
        int nc = fonts.GetRangeRects("aaaa bb", center, 30f, 0, 7, cr, 30f);
        fonts.GetCaret("aaaa bb", center, 30f, 5, out float cLine1X, out float cLine1Top, out _, out int cLine1, 30f);
        int cHit = fonts.HitTestText("aaaa bb", center, 30f, new Point2(10f, 20f), out _, 30f);   // line 1, inside the first 'b' [9.5, 15)
        Check("gate.text.align.center-wrapped center on a 2-line wrapped run: each line centers on its own visible width",
            nc == 2 && Near(cr[0].X, 4f, 0.01f) && Near(cr[1].X, 9.5f, 0.01f) && Near(cr[1].Y, 14f, 0.01f)
            && cLine1 == 1 && Near(cLine1X, 9.5f, 0.01f) && Near(cLine1Top, 14f, 0.01f) && cHit == 5,
            $"rects={nc}: {(nc > 0 ? cr[0].X : -1):0.0}/{(nc > 1 ? cr[1].X : -1):0.0} caret5=({cLine1X:0.0}, line {cLine1}) hit={cHit}");

        // (e) justify widens inter-word gaps except on the last line: "aa bb cc dd" at 40 → "aa bb " | "cc dd".
        // Line 0 visible 27.5, one interior space → +12.5; 'b' moves 16.5 → 29 and the visible end reaches the box (40).
        var just = new TextStyle(default, 10f, 400, TextWrap.Wrap, Align: TextAlign.Justify);
        const string jt = "aa bb cc dd";
        fonts.GetCaret(jt, just, 40f, 3, out float jB, out _, out _, out _, 40f);
        fonts.GetCaret(jt, just, 40f, 5, out float jEnd0, out _, out _, out _, 40f);
        fonts.GetCaret(jt, just, 40f, 9, out float jD, out _, out _, out int jLine, 40f);
        var startStyle = just with { Align = TextAlign.Start };
        fonts.GetCaret(jt, startStyle, 40f, 9, out float sD, out _, out _, out _, 40f);
        Check("gate.text.align.justify justify widens inter-word gaps on every line but the last",
            Near(jB, 29f, 0.01f) && Near(jEnd0, 40f, 0.01f) && jLine == 1 && Near(jD, 16.5f, 0.01f) && Near(jD, sD, 0.01f),
            $"b={jB:0.0} line0End={jEnd0:0.0} lastLine d={jD:0.0} (start {sD:0.0}, line {jLine})");

        // (f) TextStyle equality (the measure-cache / SameLayoutInput key) separates align and features.
        var baseStyle = new TextStyle(default, 14f, 400);
        var centered = baseStyle with { Align = TextAlign.Center };
        var tabular = baseStyle with { Features = FontFeatures.TabularFigures };
        var slashed = baseStyle with { Features = FontFeatures.SlashedZero };
        var ss01 = baseStyle with { Features = FontFeatures.StylisticSet01 };
        bool keysSplit = baseStyle != centered && baseStyle != tabular && slashed != ss01
            && baseStyle == new TextStyle(default, 14f, 400, Align: TextAlign.Start, Features: FontFeatures.None);
        var entry = new TextMeasureEntry { Valid = true, Text = strings.Intern("x"), Style = baseStyle, MaxW = float.PositiveInfinity };
        bool cacheSplit = entry.Matches(entry.Text, baseStyle, float.PositiveInfinity)
            && !entry.Matches(entry.Text, baseStyle with { Align = TextAlign.End }, float.PositiveInfinity)
            && !entry.Matches(entry.Text, baseStyle with { Features = FontFeatures.TabularFigures }, float.PositiveInfinity);
        bool packRound = true;
        foreach (var a in new[] { TextAlign.Start, TextAlign.Center, TextAlign.End, TextAlign.Justify })
            foreach (var f in new[] { FontFeatures.None, FontFeatures.TabularFigures | FontFeatures.SlashedZero, FontFeatures.StylisticSet20 | FontFeatures.LiningFigures })
            {
                int pk = TextTypography.Pack(a, f);
                if (TextTypography.AlignOf(pk) != a || TextTypography.FeaturesOf(pk) != f) packRound = false;
            }
        Check("gate.text.align.style-key TextStyle inequality on align/features keeps measure-cache keys separate; the op packing round-trips",
            keysSplit && cacheSplit && packRound, $"keys={keysSplit} cache={cacheSplit} pack={packRound}");

        // (g) headless measure is unchanged by align (alignment is positioning only), wrapped and not.
        bool measureSame = true;
        var para = strings.Intern("aa bb cc dd ee");
        foreach (var a in new[] { TextAlign.Center, TextAlign.End, TextAlign.Justify })
            foreach (float mw in new[] { float.PositiveInfinity, 40f, 25f })
            {
                var st = new TextStyle(default, 10f, 400, TextWrap.Wrap);
                var m0 = fonts.Measure(para, st, mw);
                var m1 = fonts.Measure(para, st with { Align = a, Features = FontFeatures.TabularFigures }, mw);
                if (m0.Size != m1.Size || m0.Baseline != m1.Baseline) measureSame = false;
            }
        Check("gate.text.align.measure-unchanged headless measure is identical for every TextAlign (and FontFeatures)", measureSame, $"same={measureSame}");

        // (h) span artifacts follow the line offset: the "docs" link rect is laid at [49.5, 71.5) Start-aligned and the
        // dispatcher shifts it by LineOffset(End, 200, 93.5) = 106.5 → Hand over [156, 178), arrow at the unshifted spot.
        var lr = scene.AbsoluteRect(linkNode);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(lr.X + 160f, lr.Y + 7f), 0, 0) });
        bool handShifted = cursor == CursorId.Hand;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(lr.X + 55f, lr.Y + 7f), 0, 0) });
        bool arrowUnshifted = cursor != CursorId.Hand;
        var linkRun = SpanRunTable.Shared.Resolve(scene.Layout(linkNode).TextStyle.SpanRunId);
        float linkLineW = float.NaN;
        if (linkRun?.Rects is { } lrr)
            foreach (var art in lrr.Rects) if (art.Kind == SpanStyle.LinkBit) linkLineW = art.LineWidth;
        Check("gate.text.align.span-artifacts an End-aligned paragraph's hyperlink rect moves with its line (Hand at the aligned x, not the Start x)",
            handShifted && arrowUnshifted && Near(linkLineW, 93.5f, 0.01f),
            $"handShifted={handShifted} arrowUnshifted={arrowUnshifted} lineW={linkLineW:0.0}");

        TextAlignJustifyArtifactChecks(strings, fonts);
        TextAlignTrimQueryChecks(strings, fonts);
    }

    // (i) Justify span artifacts follow the DRAWN box, not the measure wrap width. On DirectWrite a wrapped paragraph in a
    // non-stretching column measures at the column width but its box shrinks to the widest natural line, and the renderer
    // justifies at that box (Bounds.W). Headless reports wrapped boxes AT maxWidth, so the gate lays the node in a 35 box
    // and then re-publishes the run's artifacts at a 43 wrap width (same breaks: "aa bb " | "cc dd") — exactly the
    // DirectWrite shape (SpanRunRects.MaxWidth 43 ≠ Bounds.W 35). Line 0 visible 27.5, one interior gap: justified at 35
    // the gap widens by 7.5 → the "bb" link/underline sit at [24, 35) (at 43 they would sit at [32, 43); Start at [16.5, 27.5)).
    static void TextAlignJustifyArtifactChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var dispatcher = new InputDispatcher(scene) { Fonts = fonts };
        CursorId cursor = CursorId.Arrow;
        dispatcher.OnCursorChanged = c => cursor = c;
        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 300, Height = 200,
            Children =
            [
                // [0] justified paragraph with an underlined hyperlink on its justified first line.
                new SpanTextEl([new TextSpan("aa "), new TextSpan("bb", Underline: true, OnClick: () => { }), new TextSpan(" cc dd")])
                { Size = 10f, Width = 35f, TextAlign = TextAlign.Justify },
                // [1] whole-node underline, 2 wrapped lines: the justified first line fills the box → bar [0, 35).
                new TextEl("aa bb cc dd") { Size = 10f, Width = 35f, Wrap = TextWrap.Wrap, TextAlign = TextAlign.Justify, Underline = true },
                // [2] whole-node underline, single (last ⇒ unjustified) line: the bar keeps the natural 27.5.
                new TextEl("aa bb") { Size = 10f, Width = 60f, TextAlign = TextAlign.Justify, Underline = true },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);
        var spanNode = Child(scene, scene.Root, 0);
        ref readonly TextStyle spanStyle = ref scene.Layout(spanNode).TextStyle;
        string text = strings.Resolve(scene.Paint(spanNode).Text);
        fonts.Measure(scene.Paint(spanNode).Text, spanStyle, 43f);   // re-publish at the wider measure width
        var run = SpanRunTable.Shared.Resolve(spanStyle.SpanRunId);
        float boxW = scene.Bounds(spanNode).W;
        SpanRect link = default, bar = default;
        bool setup = run?.Rects is { } laid && Near(laid.MaxWidth, 43f, 0.01f) && Near(boxW, 35f, 0.01f) && text == "aa bb cc dd";
        if (run?.Rects is { } arts)
            foreach (var a in arts.Rects)
            {
                if (a.Kind == SpanStyle.LinkBit) link = a;
                if (a.Kind == SpanStyle.UnderlineBit) bar = a;
            }
        // Published Start-laid + the line's gap counts; placed at the drawn box they match the seam's own justified query.
        var placed = TextTypography.PlaceSpanRect(in link, TextAlign.Justify, boxW);
        Span<RectF> q = stackalloc RectF[2];
        int nq = fonts.GetRangeRects(text, spanStyle, boxW, 3, 5, q, boxW);
        bool artsOk = Near(link.Rect.X, 16.5f, 0.01f) && Near(link.LineWidth, 27.5f, 0.01f)
            && link.JustifyGaps == 1 && link.GapsBefore == 1 && link.GapsIn == 0
            && Near(placed.X, 24f, 0.01f) && Near(placed.W, 11f, 0.01f)
            && nq == 1 && Near(q[0].X, placed.X, 0.01f) && Near(q[0].W, placed.W, 0.01f);

        // Hit-test (dispatcher) and paint (recorder) both place at Bounds.W.
        var sr = scene.AbsoluteRect(spanNode);
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(sr.X + 26f, sr.Y + 7f), 0, 0) });
        bool handAtDrawn = cursor == CursorId.Hand;
        dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerMove, new Point2(sr.X + 20f, sr.Y + 7f), 0, 0) });
        bool arrowAtStart = cursor != CursorId.Hand;
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(300, 200), 1f, ColorF.Transparent));
        bool spanBar = false, fullBar = false, naturalBar = false;
        foreach (var r in dev.LastRects)
        {
            if (!Near(r.Rect.H, 1f, 0.01f)) continue;
            if (Near(r.Rect.X, 24f, 0.01f) && Near(r.Rect.W, 11f, 0.01f)) spanBar = true;
            if (Near(r.Rect.X, 0f, 0.01f) && Near(r.Rect.W, 35f, 0.01f)) fullBar = true;
            if (Near(r.Rect.X, 0f, 0.01f) && Near(r.Rect.W, 27.5f, 0.01f)) naturalBar = true;
        }
        Check("gate.text.align.justify-artifacts justified span link/underline rects are placed at the drawn box (Bounds.W 35), not the measure wrap width (43): hit-test, bar and the seam's justified range agree; whole-node bars follow the justified first line",
            setup && artsOk && handAtDrawn && arrowAtStart && spanBar && fullBar && naturalBar && bar.JustifyGaps == 1,
            $"setup={setup} link={link.Rect} lineW={link.LineWidth:0.0} gaps={link.JustifyGaps}/{link.GapsBefore}/{link.GapsIn} placed={placed} query={(nq > 0 ? q[0].ToString() : "none")} hand={handAtDrawn} arrow={arrowAtStart} bars span={spanBar} full={fullBar} natural={naturalBar}");
    }

    // (j) NoWrap + Trim: the renderer ellipsizes at Bounds.W, so the dispatcher's selection hit-test must lay the line at
    // the same width (an untrimmed +Inf query line sits at a different Center/End offset). Untrimmed NoWrap keeps +Inf.
    static void TextAlignTrimQueryChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, strings);
        var probe = new MaxWidthProbeFonts(fonts);
        var dispatcher = new InputDispatcher(scene) { Fonts = probe };
        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 300, Height = 200,
            Children =
            [
                new TextEl("aaaaaaaaaa") { Size = 10f, Width = 30f, Trim = TextTrim.CharacterEllipsis, TextAlign = TextAlign.Center, IsTextSelectionEnabled = true },
                new TextEl("aaaaaaaaaa") { Size = 10f, Width = 30f, TextAlign = TextAlign.Center, IsTextSelectionEnabled = true },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);
        float Press(NodeHandle n)
        {
            var r = scene.AbsoluteRect(n);
            probe.LastHitMaxWidth = float.NaN;
            dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, new Point2(r.X + 15f, r.Y + 7f), 0, 0) });
            dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, new Point2(r.X + 15f, r.Y + 7f), 0, 0) });
            return probe.LastHitMaxWidth;
        }
        var trimmed = Child(scene, scene.Root, 0);
        var untrimmed = Child(scene, scene.Root, 1);
        float trimW = Press(trimmed), plainW = Press(untrimmed);
        Check("gate.text.align.trim-query a NoWrap+Trim selectable text hit-tests at the renderer's ellipsis width (Bounds.W); untrimmed NoWrap stays unbounded",
            Near(trimW, scene.Bounds(trimmed).W, 0.01f) && Near(trimW, 30f, 0.01f) && float.IsPositiveInfinity(plainW),
            $"trimmed maxW={trimW:0.0} (box {scene.Bounds(trimmed).W:0.0}) untrimmed maxW={plainW}");
    }

    /// <summary>Forwards to a real font system and records the wrap width the last hit-test was asked for.</summary>
    private sealed class MaxWidthProbeFonts(IFontSystem inner) : IFontSystem
    {
        public float LastHitMaxWidth = float.NaN;
        public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
            => inner.Measure(text, in style, maxWidth);
        public int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing,
            float alignWidth = float.PositiveInfinity)
        {
            LastHitMaxWidth = maxWidth;
            return inner.HitTestText(text, in style, maxWidth, point, out trailing, alignWidth);
        }
        public void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop,
            out float lineHeight, out int lineIndex, float alignWidth = float.PositiveInfinity)
            => inner.GetCaret(text, in style, maxWidth, charIndex, out x, out lineTop, out lineHeight, out lineIndex, alignWidth);
        public int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects,
            float alignWidth = float.PositiveInfinity)
            => inner.GetRangeRects(text, in style, maxWidth, start, end, rects, alignWidth);
    }

    // Colour emoji (COLR/CPAL) bake decisions — the Windows GlyphRenderer is DirectWrite-bound, so the two rules it
    // applies while baking a colour glyph's per-quad Colors live in the engine-free FluentGpu.Text.ColorGlyphBake and
    // are pinned here. The replay contract they feed: a per-quad colour with A == 0 inherits the run colour.
    static void ColorGlyphBakeChecks()
    {
        var palette = ColorF.FromRgba(0xF4, 0x90, 0x0C);   // a CPAL entry (the flame's orange)
        var span = ColorF.FromRgba(0x20, 0x60, 0xFF);       // the enclosing span's tint
        var inherit = ColorF.Transparent;                    // A == 0 ⇒ "inherit the run colour at replay" (a plain run)

        // T-color-1: a FOREGROUND layer (palette index 0xFFFF) takes the span colour — which is the inherit sentinel
        // for a plain run — and a palette layer keeps its CPAL colour whatever the span says.
        ColorF fgSpan = ColorGlyphBake.LayerColor(foreground: true, palette, span);
        ColorF fgPlain = ColorGlyphBake.LayerColor(foreground: true, palette, inherit);
        ColorF palSpan = ColorGlyphBake.LayerColor(foreground: false, palette, span);
        ColorF palPlain = ColorGlyphBake.LayerColor(foreground: false, palette, inherit);
        Check("T-color-1 a foreground layer inherits the span colour and a palette layer overrides it",
            fgSpan == span && fgPlain == inherit && palSpan == palette && palPlain == palette,
            $"fg/span={fgSpan} fg/plain={fgPlain} pal/span={palSpan} pal/plain={palPlain}");

        // T-color-2: the retain rule — an all-inherit list (a plain run, or an emoji made only of foreground layers)
        // keeps the cached run at Colors = null; a single palette (or span) quad retains the whole list.
        ReadOnlySpan<ColorF> allInherit = [inherit, inherit, inherit];
        ReadOnlySpan<ColorF> onePalette = [inherit, palette, inherit];
        ReadOnlySpan<ColorF> oneSpan = [span, inherit];
        bool keepsNull = !ColorGlyphBake.RetainColors(allInherit) && !ColorGlyphBake.RetainColors(ReadOnlySpan<ColorF>.Empty);
        bool retains = ColorGlyphBake.RetainColors(onePalette) && ColorGlyphBake.RetainColors(oneSpan);
        Check("T-color-2 an all-inherit run retains no Colors array", keepsNull && retains,
            $"allInherit={ColorGlyphBake.RetainColors(allInherit)} empty={ColorGlyphBake.RetainColors(ReadOnlySpan<ColorF>.Empty)} onePalette={ColorGlyphBake.RetainColors(onePalette)} oneSpan={ColorGlyphBake.RetainColors(oneSpan)}");
    }

    // E29: a trimmed SPANNED paragraph ends in the "…" of the span the cut lands in (a 20-px title span + a 12-px
    // subtitle span cut inside the subtitle → a 12-px "…"), and the cut reserves THAT ellipsis's advance. The
    // DirectWrite TextLayoutEngine (TerraFX-bound, so not headless-testable) drives its spanned-line trim through the
    // engine-free LineBreaker.FitEllipsisBySpan and emits the "…" glyph from the returned span's face/size/colour, so the
    // decision is pinned here on synthetic advances (title glyphs 11 DIP, subtitle glyphs 6 DIP; "…" = 18 DIP in the
    // title style, 11 in the subtitle style, 15 in the paragraph base style).
    static void SpanEllipsisChecks()
    {
        const int titleN = 10, subN = 10;
        var advs = new float[titleN + subN];
        var spanOf = new short[titleN + subN];
        for (int i = 0; i < titleN; i++) { advs[i] = 11f; spanOf[i] = 0; }
        for (int i = titleN; i < titleN + subN; i++) { advs[i] = 6f; spanOf[i] = 1; }
        const float baseEll = 15f;
        ReadOnlySpan<float> spanEll = [18f, 11f];

        // Cut inside the subtitle (140 of 170 DIP): 10 title glyphs (110) + 3 subtitle glyphs (128) + the SUBTITLE "…"
        // (11) = 139 ≤ 140. Reserving the base "…" (15) instead would have cut one glyph earlier (the measure/draw
        // disagreement this fixes).
        int keepSub = LineBreaker.FitEllipsisBySpan(advs, spanOf, 140f, baseEll, spanEll, out int ellSub);
        float subW = 0f; for (int k = 0; k < keepSub; k++) subW += advs[k];
        float subLine = subW + LineBreaker.EllipsisAdvanceFor(ellSub, baseEll, spanEll);
        int keepIfBase = LineBreaker.FitEllipsisBySpan(advs, spanOf, 140f, baseEll, ReadOnlySpan<float>.Empty, out _);

        // Cut inside the title (60 DIP): 3 title glyphs (33) + the TITLE "…" (18) = 51; a 4th (44 + 18) would overflow.
        int keepTitle = LineBreaker.FitEllipsisBySpan(advs, spanOf, 60f, baseEll, spanEll, out int ellTitle);

        // A base-style gap glyph (span −1) as the last visible glyph takes the paragraph base "…".
        var gapAdvs = new float[] { 10f, 10f, 10f, 10f };
        var gapSpans = new short[] { 0, -1, 1, 1 };
        int keepGap = LineBreaker.FitEllipsisBySpan(gapAdvs, gapSpans, 36f, baseEll, spanEll, out int ellGap);   // 10+10 + 15 = 35 ≤ 36; 30 + 11 = 41 > 36

        // Single-style identity: with no span table every glyph reserves the base "…" — the verbatim single-style fit
        // (budget = max(0, maxWidth − ell); keep while acc + a ≤ budget; the first glyph always stays).
        var plainSpans = new short[titleN + subN];
        for (int i = 0; i < plainSpans.Length; i++) plainSpans[i] = -1;
        bool plainIdentical = true;
        foreach (float w in new[] { 0f, 0.5f, 14f, 60f, 100f, 140f, 169.9f })
        {
            float budget = MathF.Max(0f, w - baseEll); float acc = 0f; int reference = 0;
            for (int k = 0; k < advs.Length; k++) { float a = advs[k]; if (acc + a > budget && reference > 0) break; acc += a; reference++; }
            int got = LineBreaker.FitEllipsisBySpan(advs, plainSpans, w, baseEll, ReadOnlySpan<float>.Empty, out int plainEll);
            if (got != reference || plainEll != -1) plainIdentical = false;
        }

        Check("gate.text.span-ellipsis-style a trimmed spanned line's \"…\" takes the style (span) of the run the cut lands in and the cut reserves THAT ellipsis's advance; base-style glyphs and single-style text keep the base \"…\" and the unchanged fit",
            keepSub == titleN + 3 && ellSub == 1 && subLine <= 140f && keepIfBase == titleN + 2
            && keepTitle == 3 && ellTitle == 0
            && keepGap == 2 && ellGap == -1
            && plainIdentical,
            $"sub: keep={keepSub} span={ellSub} line={subLine:0.#} (baseEll keep={keepIfBase}); title: keep={keepTitle} span={ellTitle}; gap: keep={keepGap} span={ellGap}; plainIdentical={plainIdentical}");
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
        bool suffixClicked = false;
        var gold = ColorF.FromRgba(0xFF, 0xD7, 0x00);
        var red = ColorF.FromRgba(0xFF, 0x00, 0x00);

        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1, Width = 200, Height = 140,
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
                // [4] overflow-only suffix: body wraps past 2 lines at 55 DIP; “… More” stays atomic + clickable on line 2.
                new SpanTextEl([new TextSpan("one two three four five six seven")])
                {
                    Size = 10f, Width = 55f, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    OverflowSuffix = [new TextSpan("… More", Color: gold, OnClick: () => suffixClicked = true)],
                },
            ],
        }, null);
        new FlexLayout(scene, fonts).Run(scene.Root);

        var mixed = Child(scene, scene.Root, 0);
        var linky = Child(scene, scene.Root, 1);
        var sel = Child(scene, scene.Root, 2);
        var plain = Child(scene, scene.Root, 3);
        var overflow = Child(scene, scene.Root, 4);

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

        // (b2) OverflowSuffix is hidden while the body fits, but when MaxLines clips it is reserved atomically on the
        // final line and remains an ordinary clickable TextSpan through the existing span artifact/dispatcher path.
        var overflowStyle = scene.Layout(overflow).TextStyle;
        var overflowRun = SpanRunTable.Shared.Resolve(overflowStyle.SpanRunId);
        SpanRect suffixRect = default;
        bool suffixVisible = false;
        if (overflowRun?.Rects is { } overflowRects)
            for (int i = 0; i < overflowRects.Rects.Length; i++)
                if (overflowRects.Rects[i].Span == 1 && overflowRects.Rects[i].Kind == SpanStyle.LinkBit)
                { suffixRect = overflowRects.Rects[i]; suffixVisible = true; break; }
        if (suffixVisible)
        {
            var or = scene.AbsoluteRect(overflow);
            var p = new Point2(or.X + suffixRect.Rect.X + suffixRect.Rect.W * 0.5f,
                or.Y + suffixRect.Rect.Y + suffixRect.Rect.H * 0.5f);
            dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerDown, p, 0, 0) });
            dispatcher.Dispatch(new[] { new InputEvent(InputKind.PointerUp, p, 0, 0) });
        }
        var fitMetrics = fonts.Measure(scene.Paint(overflow).Text, overflowStyle, 400f);
        bool suffixHiddenWhenFit = true;
        if (overflowRun?.Rects is { } fitRects)
            for (int i = 0; i < fitRects.Rects.Length; i++)
                if (fitRects.Rects[i].Span == 1 && fitRects.Rects[i].Kind == SpanStyle.LinkBit)
                { suffixHiddenWhenFit = false; break; }
        Check("WC-SPAN.b2 OverflowSuffix is atomic/clickable only on clipped MaxLines and hidden when the body fits",
            suffixVisible && suffixClicked && suffixHiddenWhenFit && Near(fitMetrics.Size.Width, 181.5f, 0.01f),
            $"visible={suffixVisible} clicked={suffixClicked} hiddenWhenFit={suffixHiddenWhenFit} fitW={fitMetrics.Size.Width:0.0}");

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

    // ── P2 "bound spans with index-resolved clicks" (Foundation/SpanText.cs TextSpans/SpanBuffer, SpanTextEl.Spans :
    //    Prop<TextSpans>, SpanTextEl.OnSpanClick, Reconciler.Spans.cs). ──────────────────────────────────────────────
    static void BoundSpansChecks(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);

        // ── gate.spans.bound-rebind-zero-alloc: a 1000-row CreateBound list, each row a SpanTextEl whose Spans is
        //    Prop.Of(() => a per-slot SpanBuffer refilled from the row's live index) — 3 spans, 2 links (Artist,
        //    Album). Far scroll (many slots recycle) then settle: a steady frame allocates 0 hot-phase bytes, the
        //    recycle FLUSH stays within a 256-B/row budget, no template rebuilds happen on the steady frame, and a
        //    click on the SECOND link of a realized row after a recycle reaches OnSpanClick with the CURRENT item
        //    (not whatever item occupied that slot before the recycle).
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("spans-bound-zero-alloc", new Size2(360, 240), 1f));
            window.Show();
            var probe = new BoundSpanRowsProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var vp = ViewportWithItemCount(host.Scene, host.Scene.Root, probe.Count);
            int buildsAtMount = probe.Builds;

            host.TryGetScrollHandle(vp)?.ScrollTo(20000f, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            var reboundFrame = host.RunFrame();   // the frame that actually rebinds the recycled slots
            host.Scene.TryGetScroll(vp, out var sc);
            int reboundRows = sc.LastRealized - sc.FirstRealized;
            for (int k = 0; k < 4; k++) host.RunFrame();
            var steady = host.RunFrame();

            bool noRebuilds = probe.Builds == buildsAtMount;   // recycle = signal rebind, never a fresh template build
            bool zero = steady.HotPhaseAllocBytes == 0;
            bool flushBudgetOk = reboundRows <= 0 || reboundFrame.RebindFlushAllocBytes <= 256L * reboundRows;

            // Click the SECOND link (span index 2 — "Album") of WHICHEVER realized row is actually inside the
            // viewport's visible clip band (overscan realizes rows above/below it too — a click there would miss the
            // clip and prove nothing). Scan every child of the content node rather than assume DOM order == visual
            // order (recycled slots keep their pooled position; only their bound index/content moves).
            host.Scene.TryGetScroll(vp, out sc);
            NodeHandle rowNode = NodeHandle.Null;
            RectF rowRect = default;
            for (var slot = host.Scene.FirstChild(sc.ContentNode); !slot.IsNull; slot = host.Scene.NextSibling(slot))
            {
                var r = host.Scene.AbsoluteRect(slot);
                if (r.Y >= 0f && r.Y + r.H <= 240f) { rowNode = slot; rowRect = r; break; }
            }
            probe.LastClick = null;
            int parsedRowIndex = -1;
            if (!rowNode.IsNull)
            {
                // The row's own bound index (ground truth for "the CURRENT item"), read back from its live text
                // ("row {i}ArtistAlbum") rather than trusted from any position arithmetic.
                string text = strings.Resolve(host.Scene.Paint(rowNode).Text);
                if (text.StartsWith("row ", StringComparison.Ordinal))
                {
                    int j = 4;
                    while (j < text.Length && char.IsDigit(text[j])) j++;
                    int.TryParse(text.AsSpan(4, j - 4), out parsedRowIndex);
                }

                // Resolve the SECOND link's ("Album", span index 2) seam-published rect directly — the row's own
                // Width is stretched by the Stack cross-axis, so a fixed fraction of it is not reliable; the
                // artifact rects are the ground truth the dispatcher itself hit-tests against.
                int runId = host.Scene.Layout(rowNode).TextStyle.SpanRunId;
                if (SpanRunTable.Shared.Resolve(runId)?.Rects is { } rr)
                {
                    for (int i = 0; i < rr.Rects.Length; i++)
                    {
                        if (rr.Rects[i].Span != 2 || rr.Rects[i].Kind != SpanStyle.LinkBit) continue;
                        var linkRect = rr.Rects[i].Rect;
                        var p = new Point2(rowRect.X + linkRect.X + linkRect.W * 0.5f, rowRect.Y + linkRect.Y + linkRect.H * 0.5f);
                        ClickAt(host, window, p);
                        break;
                    }
                }
            }
            bool clickResolvedCurrentItem = parsedRowIndex >= 0 && probe.LastClick is { } lc && lc.RowIndex == parsedRowIndex && lc.SpanIndex == 2;

            Check("gate.spans.bound-rebind-zero-alloc a 1000-row bound span list recycled by a far scroll settles to 0 hot-phase alloc, stays within a 256B/row rebind-flush budget, never rebuilds templates, and a post-recycle link click resolves the CURRENT item by index",
                noRebuilds && zero && flushBudgetOk && clickResolvedCurrentItem,
                $"builds={buildsAtMount}->{probe.Builds} hotAlloc={steady.HotPhaseAllocBytes}B reboundFlush={reboundFrame.RebindFlushAllocBytes}B/{reboundRows}rows " +
                $"click={(probe.LastClick is { } c ? $"(row={c.RowIndex} span={c.SpanIndex})" : "none")} parsedRow={parsedRowIndex}");
        }

        // ── gate.spans.shaping-gate-keeps-run / gate.spans.scene-owns-copy: one node, a SpanBuffer the test owns
        //    directly (not hidden behind the row template) so it can force a rebind with UNCHANGED content (proves
        //    the shaping gate) and then mutate the buffer OUTSIDE any bind fire (proves the scene holds its own copy,
        //    not an alias onto the reused buffer). ────────────────────────────────────────────────────────────────
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("spans-bound-shaping-gate", new Size2(240, 80), 1f));
            window.Show();
            var buf = new SpanBuffer();
            var epoch = new Signal<int>(0);
            var fillWord = "World";
            void Fill()
            {
                buf.Clear();
                buf.Add(new TextSpan("Hello "));
                buf.Add(new TextSpan(fillWord, IsLink: true));
            }
            Fill();
            var probe = new OneSpanProbe(epoch, () => { Fill(); return buf.Current; });
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            var mountFrame = host.RunFrame();   // real shaping happens on the first bind fire
            var node = FindTextNode(host.Scene, strings, host.Scene.Root, "Hello World");
            int runId0 = node.IsNull ? 0 : host.Scene.Layout(node).TextStyle.SpanRunId;

            // (shaping-gate) re-fire with IDENTICAL content (epoch bump only) ⇒ same run id, no re-shape this frame.
            epoch.Value++;
            var sameFrame = host.RunFrame();
            int runId1 = node.IsNull ? 0 : host.Scene.Layout(node).TextStyle.SpanRunId;
            Check("gate.spans.shaping-gate-keeps-run an identical rebind (same span content/style) keeps the SpanRunId and shapes nothing",
                !node.IsNull && runId0 != 0 && runId1 == runId0 && sameFrame.TextShapes == 0,
                $"node={!node.IsNull} run={runId0}->{runId1} textShapes={sameFrame.TextShapes}");

            // (scene-owns-copy) mutate the CALLER'S buffer directly, with no bind fire in between: the scene's own
            // copy (and therefore the drawn text) must be unaffected — SetSpanText copied, it didn't alias.
            bool sceneSpansUnaffectedBeforeRebind = host.Scene.TryGetSpanText(node, out var spansNow)
                && spansNow.Length == 2 && spansNow[1].Text == fillWord;
            buf.Clear();
            buf.Add(new TextSpan("Hello "));
            buf.Add(new TextSpan("MUTATED", IsLink: true));   // caller-side mutation, scene must not see it yet
            bool sceneStillUnaffectedAfterMutation = host.Scene.TryGetSpanText(node, out var spansAfterMutate)
                && spansAfterMutate.Length == 2 && spansAfterMutate[1].Text == fillWord
                && FindTextNode(host.Scene, strings, host.Scene.Root, "Hello MUTATED").IsNull;

            // Now drive a REAL rebind (Fill() overwrites buf back to a truthful refill on the next fire) with genuinely
            // different content ⇒ the run id MUST change (a changed span mints a new run).
            fillWord = "Changed";
            epoch.Value++;
            host.RunFrame();
            var changedNode = FindTextNode(host.Scene, strings, host.Scene.Root, "Hello Changed");
            int runId2 = changedNode.IsNull ? 0 : host.Scene.Layout(changedNode).TextStyle.SpanRunId;
            Check("gate.spans.scene-owns-copy mutating the caller's SpanBuffer after the fire leaves the scene's copy (and drawn text) unchanged; a genuinely changed rebind mints a new run",
                sceneSpansUnaffectedBeforeRebind && sceneStillUnaffectedAfterMutation && !changedNode.IsNull && runId2 != 0 && runId2 != runId0,
                $"beforeMutate={sceneSpansUnaffectedBeforeRebind} afterMutate={sceneStillUnaffectedAfterMutation} changedNode={!changedNode.IsNull} run0={runId0} run2={runId2}");
        }
    }

    // ── Glyph-atlas DIRTY-ROW upload staging (GlyphAtlasStore) ────────────────────────────────────────────────────
    // The backend's atlas upload used to memcpy + CopyTextureRegion the WHOLE atlas on every dirty flush, out of one
    // full-atlas staging bank per frame-in-flight. It now stages only the dirty ROW band. These checks drive the real
    // store (mirror + append-only shelf packer + region tracker) against a MODEL of the D3D12 semantics that make it
    // correct: a flush maps the frame's staging bank and records a copy, and every recorded copy reads that bank at
    // EXECUTION time — i.e. after every CPU write of the frame, at the one Close+ExecuteCommandLists.
    static void GlyphAtlasUploadChecks()
    {
        // 512² (a multiple of the D3D12 placement alignment, like the real 4096) — small enough to compare every texel.
        const int Size = 512;
        const int StagingRows = Size;
        var store = new GlyphAtlasStore(Size);
        var gpu = new byte[Size * Size];                 // the atlas texture: a committed D3D12 resource starts ZEROED
        var arena = new byte[StagingRows * Size];        // this frame's mapped staging bank
        var recorded = new List<(int Row, int Rows, int Offset)>();
        var cells = new List<(int X, int Y, int W, int H, int Seed)>();
        var touched = new bool[Size];                    // rows this FRAME dirtied (the expected copy footprint)
        long copied = 0, staged = 0;
        int flushes = 0, copyCmds = 0, refreshOnly = 0;
        int seed = 0;

        // One glyph = one SubPixelPhases-tall coverage stack, sized off the character so the sequence is deterministic.
        void Append(string text)
        {
            foreach (char ch in text)
            {
                int w = 6 + ch % 7, h = 4 * (9 + ch % 5);
                var bmp = new byte[w * h];
                int s = ++seed;
                for (int i = 0; i < bmp.Length; i++) bmp[i] = Px(s, i);
                if (!store.TryPack(bmp, w, h, out int x, out int y)) { seed--; return; }   // atlas full (its own scenario below)
                cells.Add((x, y, w, h, s));
                for (int r = Math.Max(0, y - 1); r < Math.Min(Size, y + h + 1); r++) touched[r] = true;
            }
        }

        // = D3D12Device.FlushSegment → GlyphRenderer.UploadIfDirty.
        void Flush()
        {
            if (!store.IsDirty) return;
            if (!store.TryTakeUpload(StagingRows, out var f)) return;
            flushes++;
            store.StageInto(in f, arena);
            staged += f.StageBytes;
            if (f.HasCopy) { recorded.Add((f.CopyRowStart, f.CopyRowCount, f.CopyOffset)); copied += f.CopyBytes; copyCmds++; }
            else refreshOnly++;
        }

        // = the ONE Close+ExecuteCommandLists: the recorded copies run in order, reading the bank's FINAL bytes.
        void Submit()
        {
            foreach (var c in recorded) Array.Copy(arena, c.Offset, gpu, c.Row * Size, c.Rows * Size);
            recorded.Clear();
        }

        // ── frame 1: three segment flushes, glyphs appended between them ──
        store.BeginFrame();
        Append("Hello"); Flush();
        Append("Wavee, the"); Flush();
        Append("glyph atlas"); Flush();
        Submit();

        int expectRows = 0; for (int r = 0; r < Size; r++) if (touched[r]) expectRows++;
        Check("gate.atlas.upload.rows-only three flushes copy EXACTLY the dirty rows, once each — not the atlas",
            copied == (long)expectRows * Size && flushes == 3 && copied < (long)Size * Size,
            $"copied={copied}B rows={copied / Size}/{expectRows} flushes={flushes} fullAtlas={(long)Size * Size}B oldCost={(long)flushes * Size * Size}B");
        Check("gate.atlas.upload.staging-bounded the per-flush re-stage stays under ONE atlas across the whole frame",
            staged < (long)Size * Size, $"staged={staged}B vs {(long)flushes * Size * Size}B for the old full-atlas re-copy");
        Check("gate.atlas.upload.pixels every glyph's texels reach the GPU byte-for-byte across the flush boundaries",
            AllCellsIntact(gpu, cells, Size), $"cells={cells.Count}");
        Check("gate.atlas.upload.apron the 1-texel gutter around every cell is uploaded as ZERO (a linear sampler reads half a texel past a cell edge)",
            AllGuttersZero(gpu, cells, Size), $"cells={cells.Count}");
        Check("gate.atlas.upload.mirror-agrees the GPU texture equals the mirror over every row a live cell occupies",
            RowsMatch(gpu, store.Texels, Size, cells), "full-width rows ⇒ no column tracking needed");

        // ── frame 2: a steady frame costs nothing; then appends copy only the newly dirty rows ──
        long copiedAfter1 = copied, stagedAfter1 = staged;
        Array.Clear(touched);
        store.BeginFrame();
        Flush(); Flush();
        Check("gate.atlas.upload.steady-zero a frame that appends no glyph stages and copies zero bytes",
            !store.IsDirty && copied == copiedAfter1 && staged == stagedAfter1 && flushes == 3,
            $"copied={copied} staged={staged} flushes={flushes}");

        Append("more text"); Flush();
        int expectRows2 = 0; for (int r = 0; r < Size; r++) if (touched[r]) expectRows2++;
        Check("gate.atlas.upload.incremental frame 2 copies only ITS dirty rows (the rest of the atlas is never re-sent)",
            copied - copiedAfter1 == (long)expectRows2 * Size && expectRows2 < Size,
            $"copied={copied - copiedAfter1}B rows={expectRows2}/{Size}");

        // A REFRESH-ONLY flush — the subtlest clause: a glyph that lands entirely inside rows an EARLIER flush of this
        // frame already recorded a copy for gets no new copy at all, and still arrives, because that copy reads the
        // staging bank at execution time. 'i' stacks to 4×(9+105%5) = 36 rows, well inside the 54-row band already copied.
        int cellsBefore = cells.Count, copyCmdsBefore = copyCmds;
        Append("i"); Flush();
        Submit();
        Check("gate.atlas.upload.refresh-only a glyph inside already-copied rows records NO new copy and still lands (the copy reads the bank at execute time)",
            cells.Count > cellsBefore && AllCellsIntact(gpu, cells, Size) && AllGuttersZero(gpu, cells, Size),
            $"newCopyCmds={copyCmds - copyCmdsBefore} refreshOnlyFlushes={refreshOnly}");

        // ── zero allocation in the steady path (pack + plan + stage) ──
        var probe = new byte[8 * 40];
        for (int i = 0; i < probe.Length; i++) probe[i] = 7;
        store.BeginFrame();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        store.TryPack(probe, 8, 40, out _, out _);
        bool planned = store.TryTakeUpload(StagingRows, out var zf);
        store.StageInto(in zf, arena);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        Check("gate.atlas.upload.alloc-zero pack + plan + stage allocate nothing in the steady state", planned && alloc == 0, $"alloc={alloc}B");
        Submit();

        // ── staging shorter than the band: the tail stays dirty, the bank is told to grow, and the frame is unfaithful ──
        var small = new GlyphAtlasStore(Size);
        var smallArena = new byte[64 * Size];
        var smallGpu = new byte[Size * Size];
        var tall = new byte[10 * 100];
        for (int i = 0; i < tall.Length; i++) tall[i] = Px(1, i);
        small.BeginFrame();
        small.TryPack(tall, 10, 100, out int tx, out int ty);          // 102 dirty rows into a 64-row bank
        bool tookClamped = small.TryTakeUpload(64, out var cf);
        small.StageInto(in cf, smallArena);
        Array.Copy(smallArena, cf.CopyOffset, smallGpu, cf.CopyRowStart * Size, cf.CopyBytes);
        bool clamped = tookClamped && cf.CopyRowCount == 64 && small.IsDirty && small.ShortfallRows > 0 && small.WantedStagingRows > 64;
        Check("gate.atlas.upload.clamp a band longer than the bank copies what fits, keeps the tail dirty and asks to grow",
            clamped, $"copied={cf.CopyRowCount} dirtyLeft={small.DirtyRowCount} short={small.ShortfallRows} want={small.WantedStagingRows}");
        // Next frame with the grown bank drains the tail — and the whole glyph is then intact.
        int grownRows = small.WantedStagingRows; // backend retains requested capacity before resetting frame-local demand
        var grownArena = new byte[grownRows * Size];
        small.BeginFrame();
        bool drained = small.TryTakeUpload(grownRows, out var df);
        small.StageInto(in df, grownArena);
        Array.Copy(grownArena, df.CopyOffset, smallGpu, df.CopyRowStart * Size, df.CopyBytes);
        var oneCell = new List<(int X, int Y, int W, int H, int Seed)> { (tx, ty, 10, 100, 1) };
        Check("gate.atlas.upload.drain the deferred tail lands on the next frame and the glyph is whole",
            drained && !small.IsDirty && small.ShortfallRows == 0 && AllCellsIntact(smallGpu, oneCell, Size),
            $"drainedRows={df.CopyRowCount} dirtyLeft={small.DirtyRowCount}");

        // ── generational reset: a full atlas rewinds, and the fresh generation's rows are re-uploaded IN FULL, so the
        //    previous generation's ink can never be sampled by a live cell (why the reset needs no full-atlas clear).
        var gen = new GlyphAtlasStore(Size);
        var genGpu = new byte[Size * Size];
        var genArena = new byte[StagingRows * Size];
        var genRecorded = new List<(int Row, int Rows, int Offset)>();
        var big = new byte[120 * 120];
        for (int i = 0; i < big.Length; i++) big[i] = 0xFF;                  // dense ink: the OLD generation
        gen.BeginFrame();
        int packedGen1 = 0;
        while (gen.TryPack(big, 120, 120, out _, out _)) packedGen1++;
        bool full = !gen.TryPack(big, 120, 120, out _, out _);
        while (gen.TryTakeUpload(StagingRows, out var gf))
        {
            gen.StageInto(in gf, genArena);
            if (gf.HasCopy) genRecorded.Add((gf.CopyRowStart, gf.CopyRowCount, gf.CopyOffset));
        }
        foreach (var c in genRecorded) Array.Copy(genArena, c.Offset, genGpu, c.Row * Size, c.Rows * Size);
        genRecorded.Clear();
        long inkedBefore = gen.NonZeroTexels;
        int epochBefore = gen.Epoch;
        gen.Reset();                                                          // = ResetAtlas at the next frame boundary
        gen.BeginFrame();
        var fresh = new byte[9 * 44];
        for (int i = 0; i < fresh.Length; i++) fresh[i] = Px(99, i);
        gen.TryPack(fresh, 9, 44, out int fx, out int fy);
        if (gen.TryTakeUpload(StagingRows, out var ff))
        {
            gen.StageInto(in ff, genArena);
            if (ff.HasCopy) Array.Copy(genArena, ff.CopyOffset, genGpu, ff.CopyRowStart * Size, ff.CopyBytes);
        }
        var freshCell = new List<(int X, int Y, int W, int H, int Seed)> { (fx, fy, 9, 44, 99) };
        Check("gate.atlas.upload.reset-no-clear after a generational reset the fresh generation's rows are re-uploaded in full — no stale ink is reachable, no full-atlas clear needed",
            full && packedGen1 > 0 && gen.Epoch == epochBefore + 1 && gen.NonZeroTexels == 44L * 9
                && AllCellsIntact(genGpu, freshCell, Size) && AllGuttersZero(genGpu, freshCell, Size)
                && RowsMatch(genGpu, gen.Texels, Size, freshCell),
            $"gen1Cells={packedGen1} inkBefore={inkedBefore} epoch={gen.Epoch} rebases={gen.BandRebases}");
        Check("gate.atlas.upload.no-rebase the band mapping never re-bases mid-frame (the append-only clause holds)",
            store.BandRebases == 0 && gen.BandRebases == 0 && small.BandRebases == 0,
            $"store={store.BandRebases} gen={gen.BandRebases} small={small.BandRebases}");
    }

    /// <summary>A deterministic, never-zero coverage value: lets a check tell "this texel arrived" from "this texel is
    /// still whatever was there before" (a zero would alias the gutter).</summary>
    static byte Px(int seed, int i) => (byte)(1 + ((seed * 31 + i * 17) & 0x7E));

    static bool AllCellsIntact(byte[] gpu, List<(int X, int Y, int W, int H, int Seed)> cells, int size)
    {
        foreach (var c in cells)
            for (int r = 0; r < c.H; r++)
                for (int x = 0; x < c.W; x++)
                    if (gpu[(c.Y + r) * size + c.X + x] != Px(c.Seed, r * c.W + x)) return false;
        return true;
    }

    static bool AllGuttersZero(byte[] gpu, List<(int X, int Y, int W, int H, int Seed)> cells, int size)
    {
        foreach (var c in cells)
        {
            for (int x = c.X - 1; x <= c.X + c.W; x++)
                if (gpu[(c.Y - 1) * size + x] != 0 || gpu[(c.Y + c.H) * size + x] != 0) return false;
            for (int r = c.Y - 1; r <= c.Y + c.H; r++)
                if (gpu[r * size + c.X - 1] != 0 || gpu[r * size + c.X + c.W] != 0) return false;
        }
        return true;
    }

    static bool RowsMatch(byte[] gpu, ReadOnlySpan<byte> mirror, int size, List<(int X, int Y, int W, int H, int Seed)> cells)
    {
        foreach (var c in cells)
            for (int r = Math.Max(0, c.Y - 1); r < Math.Min(size, c.Y + c.H + 1); r++)
                if (!mirror.Slice(r * size, size).SequenceEqual(gpu.AsSpan(r * size, size))) return false;
        return true;
    }



    sealed class BoundSpanRowsProbe : Component
    {
        public int Count = 1000;
        public int Builds;
        public (int RowIndex, int SpanIndex)? LastClick;

        // P3: the real FormatCache<int> now exists — a bound row author caches "row {i}" through it instead of paying
        // a fresh string concat PER REBIND (this used to be an ad hoc pre-interned string[1000], built by hand before
        // P3 landed; the cache is the same idea, just the real primitive). Warmed for every row up front, same as the
        // old array literal — this gate measures STEADY-STATE recycle cost, not a cold cache miss.
        static readonly FormatCache<int> s_labels = WarmLabels();
        static FormatCache<int> WarmLabels()
        {
            var c = FormatCache.Create<int>();
            for (int i = 0; i < 1000; i++) c.Get(i, static ii => "row " + ii);
            return c;
        }

        public override Element Render()
        {
            var list = ItemsView.CreateBound(Count, scope =>
            {
                Builds++;
                var buf = new SpanBuffer();
                var idx = scope.Index;
                return new SpanTextEl(Prop.Of(() =>
                {
                    int i = idx.Value;
                    buf.Clear();
                    buf.Add(new TextSpan(s_labels.Get(i, static ii => "row " + ii)));
                    buf.Add(new TextSpan("Artist", IsLink: true));
                    buf.Add(new TextSpan("Album", IsLink: true));
                    return buf.Current;
                }))
                {
                    Size = 12f,
                    OnSpanClick = spanIndex => LastClick = (idx.Peek(), spanIndex),
                };
            }, RepeatLayout.Stack(40f), new ListOptions { Grow = 1f });
            return new BoxEl { Width = 360f, Height = 240f, Children = [list] };
        }
    }

    sealed class OneSpanProbe(IReadSignal<int> epoch, Func<TextSpans> fill) : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 240f, Height = 80f,
            Children =
            [
                new SpanTextEl(Prop.Of(() => { _ = epoch.Value; return fill(); })) { Size = 14f },
            ],
        };
    }
}
