using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI RadioButton: a ring + (when selected) a filled accent dot, plus a label. Mutual exclusion comes from a
/// shared selected index — use <see cref="Group"/> to render a set of options over one selected-index callback, or
/// <see cref="Create"/> for a single button you wire up yourself. Controlled.
/// </summary>
public static partial class RadioButton
{
    public sealed record Style
    {
        public float RingSize { get; init; } = 20f;        // OuterEllipse Width/Height
        public float DotSize { get; init; } = 12f;         // RadioButtonCheckGlyphSize (rest)
        public float FontSize { get; init; } = 14f;        // ControlContentThemeFontSize
        public float MinHeight { get; init; } = 32f;

        // Unchecked ellipse — ControlAltFillColor* fill + ControlStrongStrokeColor* ring
        public ColorF OffFill { get; init; }               // RadioButtonOuterEllipseFill → ControlAltFillColorSecondary
        public ColorF OffHover { get; init; }              // ...FillPointerOver → ControlAltFillColorTertiary
        public ColorF OffPressed { get; init; }            // ...FillPressed → ControlAltFillColorQuaternary
        public ColorF OffBorder { get; init; }             // RadioButtonOuterEllipseStroke → ControlStrongStrokeColorDefault

        // Checked ellipse — accent fill + accent ring (the 1px ring is retained, stroke == fill)
        public ColorF OnRing { get; init; }                // RadioButtonOuterEllipseCheckedFill → AccentDefault
        public ColorF OnHover { get; init; }               // ...CheckedFillPointerOver → AccentSecondary
        public ColorF OnPressed { get; init; }             // ...CheckedFillPressed → AccentTertiary
        public ColorF OnBorder { get; init; }              // RadioButtonOuterEllipseCheckedStroke → AccentDefault

        public ColorF Dot { get; init; }                   // RadioButtonCheckGlyphFill → TextOnAccentPrimary
        public GradientSpec? DotBorder { get; init; }      // RadioButtonCheckGlyphStrokeChecked → AccentControlElevationBorder
        public ColorF Foreground { get; init; }            // RadioButtonForeground → TextPrimary
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlAltSecondary, OffHover = Tok.FillControlAltTertiary, OffPressed = Tok.FillControlAltQuaternary,
        OffBorder = Tok.StrokeControlStrongDefault,
        OnRing = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnBorder = Tok.AccentDefault,
        Dot = Tok.TextOnAccentPrimary, DotBorder = Tok.AccentControlElevationBorder, Foreground = Tok.TextPrimary,
    };

    public static BoxEl Create(string label, bool selected, Action onSelect, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        var ring = new BoxEl
        {
            Width = s.RingSize, Height = s.RingSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(s.RingSize),
            BorderWidth = 1f,
            BorderColor = selected ? s.OnBorder : s.OffBorder,
            Fill = selected ? s.OnRing : s.OffFill,
            HoverFill = selected ? s.OnHover : s.OffHover,
            PressedFill = selected ? s.OnPressed : s.OffPressed,
            Children = selected
                ? [new BoxEl { Width = s.DotSize, Height = s.DotSize, Corners = Radii.Circle(s.DotSize), Fill = s.Dot, BorderBrush = s.DotBorder, BorderWidth = s.DotBorder is null ? 0f : 1f }]
                : [],
        };

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 10f,
            MinHeight = s.MinHeight,
            Role = AutomationRole.RadioButton,
            OnClick = onSelect,
            Children = [ring, new TextEl(label) { Size = s.FontSize, Color = s.Foreground }],
        };
    }

    /// <summary>A mutually-exclusive group: renders one radio per option; clicking option i invokes <paramref name="onSelect"/>(i).</summary>
    public static BoxEl Group(IReadOnlyList<string> options, int selected, Action<int> onSelect, bool horizontal = false, Style? style = null)
    {
        var children = new Element[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            int idx = i;
            children[i] = Create(options[i], i == selected, () => onSelect(idx), style);
        }
        return new BoxEl { Direction = horizontal ? (byte)0 : (byte)1, Gap = horizontal ? 16f : 4f, Children = children };
    }
}
