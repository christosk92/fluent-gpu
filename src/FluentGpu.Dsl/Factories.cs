using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>Fluent C# markup. <c>using static FluentGpu.Dsl.Ui;</c> to author trees as expressions.</summary>
public static partial class Ui
{
    public static BoxEl VStack(float gap, params Element[] children)
        => new() { Direction = 1, Gap = gap, Children = children };

    public static BoxEl HStack(float gap, params Element[] children)
        => new() { Direction = 0, Gap = gap, Children = children };

    /// <summary>A padded panel (container with inner padding) — gives content WinUI-like breathing room.</summary>
    public static BoxEl Panel(Edges4 padding, float gap, params Element[] children)
        => new() { Direction = 1, Gap = gap, Padding = padding, Children = children };

    /// <summary>A z-stack: children overlay at the origin, painted in order (last on top) — overlays, scrims, flyouts.</summary>
    public static BoxEl ZStack(params Element[] children)
        => new() { ZStack = true, Children = children };

    public static TextEl Heading(string text)
        => new(text) { Size = 28f, Bold = true, Color = Theme.WindowText };

    public static TextEl Text(string text)
        => new(text) { Size = 14f, Color = Theme.WindowText };

    /// <summary>A font icon (WinUI FontIcon): a glyph rendered from an icon font. Use <see cref="Icons"/> for the glyph,
    /// or pass any codepoint string (e.g. "") + a <paramref name="family"/> (system name or "path.ttf#Family").</summary>
    public static TextEl Icon(string glyph, float size = 16f, ColorF? color = null, string? family = null)
        => new(glyph) { FontFamily = family ?? Theme.IconFont, Size = size, Color = color ?? Theme.WindowText };

    /// <summary>A vertically-scrolling, clipping viewport over <paramref name="content"/> (typically a tall VStack).</summary>
    public static ScrollEl ScrollView(Element content, bool horizontal = false)
        => new() { Content = content, Horizontal = horizontal, Grow = 1f };

    /// <summary>An async, cached, residency-pinned image (album art). Shows a placeholder until decoded.</summary>
    public static ImageEl Image(string source, float width, float height, float corners = 0f)
        => new() { Source = source, Width = width, Height = height, Corners = CornerRadius4.All(corners) };

    /// <summary>A CSS-grid of <paramref name="children"/> auto-flowed into <paramref name="columns"/> tracks.</summary>
    public static GridEl Grid(TrackSize[] columns, float colGap, float rowGap, float rowHeight, params Element[] children)
        => new() { Columns = columns, ColGap = colGap, RowGap = rowGap, RowHeight = rowHeight, Children = children };

    /// <summary>A uniform N-column grid of equal star tracks (the common album/artist card-grid shape).</summary>
    public static GridEl UniformGrid(int columns, float gap, float rowHeight, params Element[] children)
    {
        var tracks = new TrackSize[columns];
        for (int i = 0; i < columns; i++) tracks[i] = TrackSize.Star();
        return new() { Columns = tracks, ColGap = gap, RowGap = gap, RowHeight = rowHeight, Children = children };
    }

    // ── Fluent surfaces (semantic tokens) ────────────────────────────────────────────────────────

    /// <summary>A Fluent card surface: CardBackground fill + CardStroke 1px border + OverlayCornerRadius + a resting
    /// elevation shadow + inner padding. The workhorse container for the gallery's example cards and sample tiles.</summary>
    public static BoxEl Card(params Element[] children) => new()
    {
        Direction = 1, Gap = Spacing.StackM, Padding = Spacing.CardPad,
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Corners = Radii.OverlayAll, Children = children,   // flat, WinUI-style (no drop shadow on cards)
    };

    /// <summary>A layer surface (flyout / expander body): LayerFill + SurfaceStroke + OverlayCornerRadius + flyout shadow.</summary>
    public static BoxEl Layer(Edges4 pad, params Element[] children) => new()
    {
        Direction = 1, Padding = pad, Fill = Tok.FillLayerDefault,
        BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        Shadow = Elevation.Flyout, Children = children,
    };

    /// <summary>A 1px hairline divider (DividerStroke). Horizontal by default; vertical stretches to its parent's cross axis.</summary>
    public static BoxEl Divider(bool vertical = false) => vertical
        ? new() { Width = 1f, Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.Stretch }
        : new() { Height = 1f, Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.Stretch };

    /// <summary>A section header: a BodyStrong title (+ optional Caption subtitle) with the standard top rhythm.</summary>
    public static BoxEl SectionHeader(string title, string? caption = null)
    {
        Element[] kids = caption is null
            ? [BodyStrong(title)]
            : [BodyStrong(title), Caption(caption).Tertiary()];
        return new() { Direction = 1, Gap = Spacing.StackS, Margin = new Edges4(0, Spacing.StackM, 0, 0), Children = kids };
    }

    /// <summary>A SelectorBar pill: rounded-16, accent-filled when selected, subtle hover otherwise.</summary>
    public static BoxEl Pill(string label, bool selected, Action onClick) => new()
    {
        Direction = 0, Padding = new Edges4(14, 6, 14, 6), MinHeight = 32, AlignItems = FlexAlign.Center,
        Corners = Radii.PillAll, OnClick = onClick,
        Fill = selected ? Tok.AccentDefault : Tok.FillSubtleTransparent,
        HoverFill = selected ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
        PressedFill = selected ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
        Children = [new TextEl(label) { Size = 14f, Color = selected ? Tok.TextOnAccentPrimary : Tok.TextPrimary }],
    };

    /// <summary>A linear gradient fill spec (angle 0 = left→right, 90 = top→bottom). Apply with <c>.Gradient(...)</c>.</summary>
    public static GradientSpec LinearGradient(float angleDeg, params GradientStop[] stops)
        => new(GradientShape.Linear, angleDeg, stops);

    /// <summary>A radial gradient fill spec (center→edge). Apply with <c>.Gradient(...)</c>.</summary>
    public static GradientSpec RadialGradient(params GradientStop[] stops)
        => new(GradientShape.Radial, 0f, stops);

    // Controls (Button, …) live in their own classes under Controls/ — e.g. Button.Accent(...) / Button.Standard(...).
    // VirtualList(...) lives in FluentGpu.Reconciler (it composes the keyed reconciler + scroll) — see Virtual.cs.
}
