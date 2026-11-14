using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

/// <summary>
/// Whether a laid-out text node shows all of its content (<see cref="TextOverflowQuery"/>), from two independent readings.
/// <para>The UNCUT reading: <see cref="NaturalSize"/> is the extent the run wants (single line: its unconstrained width;
/// wrapping: the full wrapped block at the box width, no line cap, no trim) and <see cref="BoxSize"/> the laid-out box it
/// got, both in DIP. <see cref="Trimmed"/> is that comparison's one-line answer: the uncut run does not fit its box (a single
/// line wider than its box, or a wrapped block taller than its box). It says what the words WANT, not what the draw did: a
/// run whose uncut block is taller than its box may still be drawn whole (no trim and no line cap: the lines run past the
/// box), and a run whose uncut block fits may still be drawn cut.</para>
/// <para>The DRAWN reading (<see cref="HasDrawn"/>; false when the font system cannot say, e.g. the headless model): one
/// layout of the run exactly as the draw shapes it (its laid style at the auto-fit size, its trim and line cap, at the box
/// width). <see cref="DrawnLines"/> is its line count, <see cref="DrawnCut"/> whether that layout does not show all of the
/// content (an ellipsized or trim-cut line, lines dropped past <c>MaxLines</c>, an overflow suffix; see
/// <see cref="TextDrawn"/>), <see cref="DrawnEllipsis"/> whether it drew an ellipsis glyph. Beside it, from the uncut run laid
/// at the same width: <see cref="NaturalLines"/> (the lines the words want) and <see cref="NaturalOverhang"/> (a wrapped word
/// drawn past the box). Together they tell a cut the words needed from one they did not.</para>
/// </summary>
public readonly record struct TextOverflow(bool IsText, bool HorizontalOverflow, bool VerticalOverflow, Size2 NaturalSize, Size2 BoxSize)
{
    /// <summary>True when the uncut run does not fit its box (<see cref="HorizontalOverflow"/> or
    /// <see cref="VerticalOverflow"/>). What the draw did is <see cref="DrawnCut"/>.</summary>
    public bool Trimmed => HorizontalOverflow || VerticalOverflow;

    /// <summary>The drawn facts below were read from the font system (<see cref="IFontSystem.TryMeasureDrawn"/>). When false
    /// they are unknown, not "not cut".</summary>
    public bool HasDrawn { get; init; }

    /// <summary>Lines the draw lays out (valid when <see cref="HasDrawn"/>).</summary>
    public int DrawnLines { get; init; }

    /// <summary>The draw does not show all of the run's content: a line ellipsized or cut by the trim, lines dropped past
    /// <c>MaxLines</c>, an overflow suffix (valid when <see cref="HasDrawn"/>). Content drawn whole past the box and
    /// clipped by it is not a drawn cut (<see cref="Trimmed"/> sees that).</summary>
    public bool DrawnCut { get; init; }

    /// <summary>The draw shows an ellipsis glyph ("…") on some line (valid when <see cref="HasDrawn"/>).</summary>
    public bool DrawnEllipsis { get; init; }

    /// <summary>Lines the UNCUT run lays out at the box width (no trim, no line cap): how many lines the words want (valid when
    /// <see cref="HasDrawn"/>). Against <c>MaxLines</c> it tells a cut the line cap made from one the words did not need.</summary>
    public int NaturalLines { get; init; }

    /// <summary>The UNCUT wrapping run draws a line past the box (a <c>WrapWholeWords</c> word wider than it; hanging trailing
    /// whitespace excluded; see <see cref="TextDrawn.Overhang"/>), valid when <see cref="HasDrawn"/>. Not folded into
    /// <see cref="HorizontalOverflow"/>: a designed bleed of large wrapped type is drawn that way on purpose.</summary>
    public bool NaturalOverhang { get; init; }

    /// <summary>Not a live text node (or not laid out yet).</summary>
    public static TextOverflow NotText => default;
}

/// <summary>
/// The per-text-node "was it trimmed" query. Derived, not stored: the node's laid-out box (<see cref="SceneStore.Bounds"/>)
/// against the run re-measured through the SAME font system the layout used, without the cut — a single-line run at
/// unbounded width, a wrapping run at the box width (the width the draw shapes it at) with no line cap and no trimming —
/// plus one layout of the run AS DRAWN (trim and line cap kept, at the box width) and one of the uncut run at that width for
/// the drawn facts. The style is the
/// node's layout style (size, weight, family, spacing, line height, case), so the measure is the layout's own, at the
/// auto-fit size when <c>MinSize</c> shrank it. Cold path (three or four layouts per call, re-wraps of one shaping; no caching): meant for inspection
/// and lint, not for every frame. Call on the UI thread between frames.
/// </summary>
public static class TextOverflowQuery
{
    /// <summary>Slack (DIP) on the size COMPARISON only (natural against box) before a difference counts: sub-pixel rounding
    /// between measure and arrange is not a trim. The run itself is laid out at the box width, never at the box plus this.</summary>
    public const float Tolerance = 0.5f;

    /// <summary>The overflow of text node <paramref name="node"/> in <paramref name="scene"/>, measured by
    /// <paramref name="fonts"/> (pass the font system the host was built with): the uncut natural size against the box, and
    /// (when <paramref name="fonts"/> can say) what the draw's own layout cut. <see cref="TextOverflow.NotText"/> for a
    /// dead handle or a non-text node.</summary>
    public static TextOverflow Query(SceneStore scene, IFontSystem fonts, NodeHandle node)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(fonts);
        if (node.IsNull || !scene.IsLive(node)) return TextOverflow.NotText;
        ref NodePaint paint = ref scene.Paint(node);
        if (paint.VisualKind != VisualKind.Text) return TextOverflow.NotText;
        StringId text = paint.Text;
        TextStyle laid = scene.Layout(node).TextStyle;
        RectF bounds = scene.Bounds(node);
        var box = new Size2(bounds.W, bounds.H);

        // The auto-fit size the measure pass chose (TextStyle.MinSizeDip): the run is drawn at it, so measure at it too.
        TextStyle style = laid;
        if (scene.TryGetMeasureCache(node, out TextMeasureCache cache))
        {
            TextMeasureEntry entry = cache.ResolveForWidth(box.Width);
            if (entry.Valid && entry.FitSize > 0f && entry.FitSize < laid.SizeDip) style = style.AtFitSize(entry.FitSize);
        }

        // The uncut run: no trimming, no line cap, and (a span run) no artifacts published at a probe width (restored below).
        TextStyle uncut = style with { Trim = TextTrim.None, MaxLines = 0, MinSizeDip = 0f };
        bool horizontal, vertical;
        Size2 natural;
        // The box width, the width the draw shapes the run at (never widened): the layout engine's own wrap slack
        // (TextLayoutEngine.WrapSlack) is what keeps a run whose box IS its widest line from re-breaking that line on rounding.
        float wrapW = MathF.Max(0f, box.Width);
        if (laid.Wrap == TextWrap.NoWrap)
        {
            natural = fonts.Measure(text, uncut, float.PositiveInfinity).Size;
            horizontal = natural.Width > box.Width + Tolerance;
            vertical = natural.Height > box.Height + Tolerance;
        }
        else
        {
            natural = fonts.Measure(text, uncut, wrapW).Size;
            // Measure clamps Size.Width to the wrap width, so this never fires: a wrapping run's sideways overflow (a
            // WrapWholeWords word wider than the box, which overhangs) is NOT detected by HorizontalOverflow. TextMetrics.MaxLineWidth
            // carries the overhang, but it also counts a line's hanging trailing whitespace, so it cannot tell an overhanging
            // word from a line whose trailing space hangs past the edge (which draws nothing there). The overhang is reported
            // apart, from visible line widths (NaturalOverhang below). What HorizontalOverflow/VerticalOverflow detect for a
            // wrapping run is the wrapped block taller than the box (vertical).
            horizontal = natural.Width > box.Width + Tolerance;
            vertical = natural.Height > box.Height + Tolerance;
        }

        // The drawn run: the laid style (trim and line cap kept) at the auto-fit size, at the box width: the layout the draw
        // makes. A pure measure, so a span run's artifacts are not touched by it.
        bool hasDrawn = fonts.TryMeasureDrawn(text, style with { MinSizeDip = 0f }, wrapW, out TextDrawn drawn);
        // …and the uncut run at the same width (a re-wrap of the same shaping): the lines the words want, and an overhang.
        TextDrawn whole = default;
        hasDrawn = hasDrawn && fonts.TryMeasureDrawn(text, uncut, wrapW, out whole);

        // A span run's Measure republishes its link/decoration rects for the width it was asked about: put back the ones the
        // layout published, at the width the run is drawn at.
        if (laid.SpanRunId != 0)
        {
            float drawnW = cache.Valid ? cache.ResolveForWidth(box.Width).MaxW : box.Width;
            fonts.Measure(text, style, drawnW);
        }
        return new TextOverflow(true, horizontal, vertical, natural, box)
        {
            HasDrawn = hasDrawn,
            DrawnLines = hasDrawn ? drawn.Lines : 0,
            DrawnCut = hasDrawn && drawn.Cut,
            DrawnEllipsis = hasDrawn && drawn.Ellipsis,
            NaturalLines = hasDrawn ? whole.Lines : 0,
            NaturalOverhang = hasDrawn && whole.Overhang,
        };
    }

    /// <summary>True when the uncut run of text node <paramref name="node"/> does not fit its box (see <see cref="Query"/>,
    /// <see cref="TextOverflow.Trimmed"/>). False for a non-text node.</summary>
    public static bool IsTrimmed(SceneStore scene, IFontSystem fonts, NodeHandle node) => Query(scene, fonts, node).Trimmed;

    /// <summary>The glyph INK of text node <paramref name="node"/> as it is drawn (<see cref="IFontSystem.TryMeasureInk"/>): the run at
    /// its laid style (the auto-fit size when <c>MinSize</c> shrank it, its trim and line cap) laid out at its box width, in DIP
    /// relative to the node's box. False for a dead handle, a non-text node, a backend without glyph metrics or a run with no ink.
    /// Cold path (one layout per call): inspection and lint. Call on the UI thread between frames.</summary>
    public static bool TryInk(SceneStore scene, IFontSystem fonts, NodeHandle node, out RectF ink)
    {
        ink = default;
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(fonts);
        if (node.IsNull || !scene.IsLive(node)) return false;
        ref NodePaint paint = ref scene.Paint(node);
        if (paint.VisualKind != VisualKind.Text) return false;
        TextStyle style = scene.Layout(node).TextStyle;
        RectF bounds = scene.Bounds(node);
        if (scene.TryGetMeasureCache(node, out TextMeasureCache cache))
        {
            TextMeasureEntry entry = cache.ResolveForWidth(bounds.W);
            if (entry.Valid && entry.FitSize > 0f && entry.FitSize < style.SizeDip) style = style.AtFitSize(entry.FitSize);
        }
        // A pure measure at the box width the draw shapes at: a span run's artifacts stay the ones the layout published.
        bool found = fonts.TryMeasureInk(paint.Text, style with { MinSizeDip = 0f }, MathF.Max(0f, bounds.W), out ink);
        if (style.SpanRunId != 0)
        {
            float drawnW = cache.Valid ? cache.ResolveForWidth(bounds.W).MaxW : bounds.W;
            fonts.Measure(paint.Text, style, drawnW);
        }
        return found;
    }
}
