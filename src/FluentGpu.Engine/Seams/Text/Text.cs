using FluentGpu.Foundation;

namespace FluentGpu.Text;

/// <summary>The resolved style a text run is measured/shaped under. <see cref="Weight"/> is the NUMERIC font weight
/// (WinUI FontWeight — the int IS the DWRITE_FONT_WEIGHT, 1..999; callers resolve sugar like Bold→700 before
/// constructing; 0 is treated as 400). <see cref="CharSpacing"/> is WinUI TextElement.CharacterSpacing — tracking in
/// 1/1000 em, applied per glyph after shaping. <see cref="LineHeight"/> (DIP; NaN/≤0 = font-natural) resolves per
/// <see cref="Stacking"/> (WinUI LineStackingStrategy), and <see cref="LineBounds"/> (WinUI TextLineBounds) selects
/// the reported line box (Full = ascent..descent, Tight = cap-height..baseline). Record equality over ALL fields is
/// load-bearing: TextStyle is the TextMeasureCache key, so every layout-affecting input must live here.
/// <para><see cref="SpanRunId"/> (0 = plain single-style run) keys a <see cref="SpanRunTable.Shared"/> run whose
/// per-range <see cref="SpanStyle"/> overlays (weight/size/family/color/decorations) apply over this base style — the
/// rtb-01 inline-run model: the WHOLE text still shapes as ONE flow (one wrap pass), exactly like a WinUI paragraph's
/// inline collection. Carried here (not as a separate seam argument) so every existing layout/measure/query path —
/// and every cache keyed on TextStyle — is span-aware without signature churn; a span-content change mints a fresh
/// id, so the measure cache and the shaped-run cache self-invalidate.</para></summary>
public readonly record struct TextStyle(StringId FontFamily, float SizeDip, ushort Weight,
    TextWrap Wrap = TextWrap.NoWrap, TextTrim Trim = TextTrim.None, int MaxLines = 0,
    float CharSpacing = 0f, float LineHeight = float.NaN,
    LineStacking Stacking = LineStacking.MaxHeight, TextLineBounds LineBounds = TextLineBounds.Full,
    int SpanRunId = 0, float MinSizeDip = 0f,   // MinSizeDip > 0 ⇒ auto-fit floor (TextEl.MinSize; shrink-to-fit MaxLines)
    TextAlign Align = TextAlign.Start, FontFeatures Features = FontFeatures.None);   // TextEl.TextAlign / TextEl.FontFeatures

/// <summary>Shared alignment/typography math — ONE definition the DirectWrite engine, the headless font system, the
/// recorder (decoration bars) and the input dispatcher (hyperlink rects) all use, so every consumer places a line at the
/// same offset. <see cref="Pack"/> folds <see cref="TextAlign"/> + <see cref="FontFeatures"/> into the single int the glyph
/// op carries (features in the low 28 bits, align in bits 28..29).</summary>
public static class TextTypography
{
    public const uint FeatureMask = 0x0FFF_FFFFu;

    public static int Pack(TextAlign align, FontFeatures features)
        => (int)(((uint)features & FeatureMask) | ((uint)align & 3u) << 28);
    public static int Pack(in TextStyle style) => Pack(style.Align, style.Features);
    public static TextAlign AlignOf(int packed) => (TextAlign)(((uint)packed >> 28) & 3u);
    public static FontFeatures FeaturesOf(int packed) => (FontFeatures)((uint)packed & FeatureMask);

    /// <summary>The x offset of a Start/Center/End line of visible width <paramref name="lineWidth"/> (trailing hanging
    /// whitespace already excluded) inside a box <paramref name="alignWidth"/> wide. Start, Justify (justified lines fill
    /// the box; its last line is Start), a non-finite box and an overflowing line (CSS: start-aligned) all give 0.</summary>
    public static float LineOffset(TextAlign align, float alignWidth, float lineWidth)
    {
        if (align is TextAlign.Start or TextAlign.Justify || !float.IsFinite(alignWidth) || float.IsNaN(lineWidth)) return 0f;
        float slack = alignWidth - lineWidth;
        if (!(slack > 0f)) return 0f;
        return align == TextAlign.Center ? slack * 0.5f : slack;
    }

    /// <summary>Place a seam-published span artifact (laid Start-aligned, unjustified) inside a box
    /// <paramref name="boxWidth"/> wide — the drawn Bounds.W the renderer aligns the glyphs at. Center/End shift by
    /// <see cref="LineOffset"/>; Justify widens each of the line's <see cref="SpanRect.JustifyGaps"/> gaps by
    /// (boxWidth − LineWidth) / JustifyGaps (the engines' JustifyLine rule: slack ≤ 0 or a non-finite box ⇒ no change),
    /// moving the rect by the gaps before it and growing it by the gaps inside it. Pure math, 0-alloc.</summary>
    public static RectF PlaceSpanRect(in SpanRect art, TextAlign align, float boxWidth)
    {
        RectF r = art.Rect;
        if (align == TextAlign.Justify)
        {
            if (art.JustifyGaps <= 0 || !float.IsFinite(boxWidth) || float.IsNaN(art.LineWidth)) return r;
            float slack = boxWidth - art.LineWidth;
            if (!(slack > 0f)) return r;
            float per = slack / art.JustifyGaps;
            return new RectF(r.X + per * art.GapsBefore, r.Y, r.W + per * art.GapsIn, r.H);
        }
        float dx = LineOffset(align, boxWidth, art.LineWidth);
        return dx != 0f ? new RectF(r.X + dx, r.Y, r.W, r.H) : r;
    }
}

/// <summary>Result of measuring a run: the box the shaped glyphs occupy + the baseline offset from the top, plus the
/// face's decoration metrics — all vertical values measured DOWN from the line top, the same frame as
/// <see cref="Baseline"/>. <see cref="UnderlineY"/>/<see cref="UnderlineThickness"/> place the underline bar and
/// <see cref="StrikeY"/> the strikethrough bar (it reuses the underline thickness). These feed text decorations;
/// backends without face metrics (the experimental GDI path) leave them 0.
/// <see cref="FirstLineWidth"/> is the first line's advance WITHOUT trailing hanging whitespace (NaN = not reported) —
/// the width an aligned single-run decoration bar spans and is offset by (<see cref="TextTypography.LineOffset"/>).
/// Under <see cref="TextAlign.Justify"/> it is +Inf when the first line is justified (it fills whatever box it is drawn
/// in — the measure does not know that box), else that line's natural visible width.</summary>
public readonly record struct TextMetrics(Size2 Size, float Baseline,
    float UnderlineY = 0f, float UnderlineThickness = 0f, float StrikeY = 0f, float FirstLineWidth = float.NaN);

/// <summary>Process-default <see cref="IFontSystem"/> for engine layers that have no host seam to receive an instance
/// through — concretely the input dispatcher's read-only text-selection/hyperlink gestures (rtb-02), which need the
/// editor queries but are constructed by hosts (AppHost) that predate them. Set by each backend's constructor;
/// last-constructed wins (the established <c>InputHooks.Current</c> channel-default convention). Consumers prefer an
/// explicitly-wired instance and fall back here.</summary>
public static class TextSeam
{
    public static IFontSystem? Default;
}

/// <summary>
/// Text seam. DirectWrite implements it on Windows; <c>Text.Headless</c> provides a deterministic stub for tests.
/// The shaper resolves BiDi/script/fallback and returns FINAL device-space glyph positions in visual order; the
/// renderer treats glyph runs as opaque positioned quads. Beyond measurement, the seam answers the editor queries —
/// hit-test, caret geometry, selection rects — from the SAME layout pipeline (identical wrapping, identical advances),
/// so hit-testing matches rendering exactly. For all members <c>maxWidth</c> is the wrap width
/// (<see cref="float.PositiveInfinity"/> = no wrap), interpreted exactly as <see cref="Measure"/> interprets it.
/// <para><c>alignWidth</c> (the editor queries) is the width of the box the run is DRAWN in — the box
/// <see cref="TextStyle.Align"/> positions each line inside. It is separate from <c>maxWidth</c> because a NoWrap run is
/// measured and queried at an unbounded wrap width yet arranged into a wider box (Grow / Width / stretch): the box, not
/// the wrap width, decides where an End-aligned line sits. Render, hit-test, caret and range rects must all pass the
/// SAME value (the node's laid width); +Inf aligns to the widest line. Irrelevant (any value) for Start-aligned text.
/// <see cref="Measure"/> takes none: alignment never changes the measured box.</para>
/// </summary>
public interface IFontSystem
{
    /// <summary>P0 always-on counter: total real glyph-shape calls since process start (DirectWrite: the existing
    /// internal shape counter, now exposed; headless: counts each <see cref="Measure"/> call as one shape, its
    /// deterministic stand-in for "did real shaping work happen"). Never resets — callers read the delta across a
    /// frame boundary (<c>FrameStats.TextShapes</c>). Default 0 for a backend that has not wired a counter.</summary>
    long ShapeCount => 0;

    /// <summary>Measure a string under a style (intrinsic content size). Feeds the layout engine. When
    /// <paramref name="maxWidth"/> is finite and the style wraps, the result is the word-wrapped multi-line box.</summary>
    TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity);

    /// <summary><see cref="Measure(StringId, in TextStyle, float)"/> with control over its one side effect: with
    /// <paramref name="publishArtifacts"/> false the measure is PURE — it does not republish the run's span artifacts
    /// (link / decoration rects on the <c>SpanRun</c>) at <paramref name="maxWidth"/>. Used by hypothetical measures that
    /// must not disturb the laid-out frame (the <c>SizeToContent</c> natural probe, the auto-fit size search). The default
    /// forwards to the publishing overload, so a backend without span artifacts needs no change.</summary>
    TextMetrics Measure(StringId text, in TextStyle style, float maxWidth, bool publishArtifacts)
        => Measure(text, in style, maxWidth);

    /// <summary>Point → UTF-16 index. Returns the SNAPPED insertion index — the leading or trailing edge of the hit
    /// glyph cluster — and <paramref name="trailing"/> reports which half of the cluster was hit. Points outside the
    /// laid-out bounds clamp to the nearest valid index (above → first line, below → last line, left → line start,
    /// right → line end), so drag-selection keeps tracking past the box. Editor-hosting backends must implement this;
    /// the default throws (the experimental GDI path cannot host editable text).</summary>
    int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing,
        float alignWidth = float.PositiveInfinity)
        => throw new NotSupportedException($"{GetType().Name} does not implement text hit-testing.");

    /// <summary>Caret geometry for a UTF-16 index: x + the line's top/height (single line: top = 0, height = the line
    /// height) + line index. Returns the LEADING position of <paramref name="charIndex"/>; at a soft-wrap boundary the
    /// leading position is the START of the continuation line — trailing affinity (pinning the caret to the END of the
    /// wrapped line) is the CALLER's job. <paramref name="charIndex"/> == text length → the trailing edge of the last
    /// line. An index inside a multi-char cluster snaps to the cluster's edges. Editor-hosting backends must implement
    /// this; the default throws.</summary>
    void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex,
        float alignWidth = float.PositiveInfinity)
        => throw new NotSupportedException($"{GetType().Name} does not implement caret metrics.");

    /// <summary>One rect per line fragment covered by [<paramref name="start"/>, <paramref name="end"/>), x-from/x-to
    /// via cluster edges. Returns the count written (≤ <paramref name="rects"/>.Length; excess fragments are dropped —
    /// callers size generously). Zero-width ranges produce nothing. Editor-hosting backends must implement this; the
    /// default throws.</summary>
    int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects,
        float alignWidth = float.PositiveInfinity)
        => throw new NotSupportedException($"{GetType().Name} does not implement selection geometry.");
}
