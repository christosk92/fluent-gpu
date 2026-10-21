using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

/// <summary>
/// Whether a laid-out text node shows all of its content (<see cref="TextOverflowQuery"/>).
/// <see cref="Trimmed"/> is the one-line answer: the run was cut by its width (a single line wider than its box: ellipsis,
/// clip, or overflow) or by its line budget / box height (a wrapped run with more lines than <c>MaxLines</c> or than the box
/// holds). <see cref="NaturalSize"/> is the extent the run wants (single line: its unconstrained width; wrapping: the full
/// wrapped block at the box width, no line cap) and <see cref="BoxSize"/> the laid-out box it got, both in DIP.
/// </summary>
public readonly record struct TextOverflow(bool IsText, bool HorizontalOverflow, bool VerticalOverflow, Size2 NaturalSize, Size2 BoxSize)
{
    /// <summary>True when any of the content is not shown in full (<see cref="HorizontalOverflow"/> or
    /// <see cref="VerticalOverflow"/>).</summary>
    public bool Trimmed => HorizontalOverflow || VerticalOverflow;

    /// <summary>Not a live text node (or not laid out yet).</summary>
    public static TextOverflow NotText => default;
}

/// <summary>
/// The per-text-node "was it trimmed" query. Derived, not stored: the node's laid-out box (<see cref="SceneStore.Bounds"/>)
/// against the run re-measured through the SAME font system the layout used, without the cut — a single-line run at
/// unbounded width, a wrapping run at the box width with no line cap and no trimming. The style is the node's layout style
/// (size, weight, family, spacing, line height, case), so the measure is the layout's own, at the auto-fit size when
/// <c>MinSize</c> shrank it. Cold path (one or two shapes per call, no caching): meant for inspection and lint, not for
/// every frame. Call on the UI thread between frames.
/// </summary>
public static class TextOverflowQuery
{
    /// <summary>Slack (DIP) before a difference counts: sub-pixel rounding between measure and arrange is not a trim.</summary>
    public const float Tolerance = 0.5f;

    /// <summary>The overflow of text node <paramref name="node"/> in <paramref name="scene"/>, measured by
    /// <paramref name="fonts"/> (pass the font system the host was built with). <see cref="TextOverflow.NotText"/> for a
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
        if (laid.Wrap == TextWrap.NoWrap)
        {
            natural = fonts.Measure(text, uncut, float.PositiveInfinity).Size;
            horizontal = natural.Width > box.Width + Tolerance;
            vertical = natural.Height > box.Height + Tolerance;
        }
        else
        {
            // The box width plus the slack: a run whose box IS its widest line must not re-break that line on rounding.
            float wrapW = MathF.Max(0f, box.Width) + Tolerance;
            natural = fonts.Measure(text, uncut, wrapW).Size;
            // An unbreakable word wider than the box overflows sideways even when wrapping.
            horizontal = natural.Width > box.Width + Tolerance;
            vertical = natural.Height > box.Height + Tolerance;
        }

        // A span run's Measure republishes its link/decoration rects for the width it was asked about: put back the ones the
        // layout published, at the width the run is drawn at.
        if (laid.SpanRunId != 0)
        {
            float drawnW = cache.Valid ? cache.ResolveForWidth(box.Width).MaxW : box.Width;
            fonts.Measure(text, style, drawnW);
        }
        return new TextOverflow(true, horizontal, vertical, natural, box);
    }

    /// <summary>True when text node <paramref name="node"/> does not show all of its content (see <see cref="Query"/>).
    /// False for a non-text node.</summary>
    public static bool IsTrimmed(SceneStore scene, IFontSystem fonts, NodeHandle node) => Query(scene, fonts, node).Trimmed;
}
