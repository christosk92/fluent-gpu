using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI CheckBox: a 20px box + label. Two-state via the <see cref="bool"/> overload; three-state (adds the mixed
/// "indeterminate" glyph) via the <see cref="CheckState"/> overload, which cycles Unchecked → Checked → Indeterminate →
/// Unchecked on click. Controlled — the caller owns the state.
/// </summary>
public static partial class CheckBox
{
    public sealed record Style
    {
        public float BoxSize { get; init; } = 20f;          // CheckBoxSize
        public float GlyphSize { get; init; } = 12f;        // CheckBoxGlyphSize
        public float FontSize { get; init; } = 14f;         // ControlContentThemeFontSize
        public float MinHeight { get; init; } = 32f;        // CheckBoxHeight

        // Unchecked box — fill is ControlAltFillColor*, stroke is ControlStrongStrokeColor* (the outer ring)
        public ColorF OffFill { get; init; }                // CheckBoxCheckBackgroundFillUnchecked → ControlAltFillColorSecondary
        public ColorF OffHover { get; init; }               // ...UncheckedPointerOver → ControlAltFillColorTertiary
        public ColorF OffPressed { get; init; }             // ...UncheckedPressed → ControlAltFillColorQuaternary
        public ColorF OffBorder { get; init; }              // CheckBoxCheckBackgroundStrokeUnchecked → ControlStrongStrokeColorDefault

        // Checked/Indeterminate box — accent fill + accent stroke (the box keeps its 1px ring; stroke == fill)
        public ColorF OnFill { get; init; }                 // CheckBoxCheckBackgroundFillChecked → AccentDefault
        public ColorF OnHover { get; init; }                // ...CheckedPointerOver → AccentSecondary
        public ColorF OnPressed { get; init; }              // ...CheckedPressed → AccentTertiary
        public ColorF OnBorder { get; init; }               // CheckBoxCheckBackgroundStrokeChecked → AccentDefault

        public ColorF GlyphColor { get; init; }             // CheckBoxCheckGlyphForegroundChecked → TextOnAccentPrimary
        public ColorF Foreground { get; init; }             // CheckBoxForeground → TextPrimary
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlAltSecondary, OffHover = Tok.FillControlAltTertiary, OffPressed = Tok.FillControlAltQuaternary,
        OffBorder = Tok.StrokeControlStrongDefault,
        OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnBorder = Tok.AccentDefault,
        GlyphColor = Tok.TextOnAccentPrimary, Foreground = Tok.TextPrimary,
    };

    public static BoxEl Create(string label, bool isChecked, Action onToggle, Style? style = null)
        => Build(label, isChecked ? CheckState.Checked : CheckState.Unchecked, _ => onToggle(), style);

    public static BoxEl Create(string label, CheckState state, Action<CheckState> onChange, Style? style = null)
    {
        var next = state switch
        {
            CheckState.Unchecked => CheckState.Checked,
            CheckState.Checked => CheckState.Indeterminate,
            _ => CheckState.Unchecked,
        };
        return Build(label, state, _ => onChange(next), style);
    }

    static BoxEl Build(string label, CheckState state, Action<CheckState> onClick, Style? style)
    {
        var s = style ?? DefaultStyle;
        bool on = state == CheckState.Checked;
        bool indet = state == CheckState.Indeterminate;
        bool filled = on || indet;

        Element mark = on
            ? new TextEl(Icons.Accept) { Size = s.GlyphSize, Color = s.GlyphColor, FontFamily = Theme.IconFont }
            : indet
                ? new BoxEl { Width = s.BoxSize * 0.5f, Height = 2f, Corners = CornerRadius4.All(1f), Fill = s.GlyphColor }
                : new BoxEl();

        // WinUI keeps a 1px ring on the box in every state: accent stroke when filled, ControlStrongStroke when off.
        var box = new BoxEl
        {
            Width = s.BoxSize, Height = s.BoxSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderColor = filled ? s.OnBorder : s.OffBorder,
            Fill = filled ? s.OnFill : s.OffFill,
            HoverFill = filled ? s.OnHover : s.OffHover,
            PressedFill = filled ? s.OnPressed : s.OffPressed,
            Children = [mark],
        };

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 10f,
            MinHeight = s.MinHeight,
            Role = AutomationRole.CheckBox,
            OnClick = () => onClick(state),
            Children = [box, new TextEl(label) { Size = s.FontSize, Color = s.Foreground }],
        };
    }
}
