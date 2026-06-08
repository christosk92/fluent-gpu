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
        public float BoxSize { get; init; } = 20f;
        public float FontSize { get; init; } = 14f;
        public float MinHeight { get; init; } = 32f;
        public ColorF OffFill { get; init; }
        public ColorF OffHover { get; init; }
        public ColorF OffBorder { get; init; }
        public ColorF OnFill { get; init; }
        public ColorF OnHover { get; init; }
        public ColorF GlyphColor { get; init; }
        public ColorF Foreground { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlDefault, OffHover = Tok.FillControlSecondary, OffBorder = Tok.StrokeControlSecondary,
        OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, GlyphColor = Tok.TextOnAccentPrimary, Foreground = Tok.TextPrimary,
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
            ? new TextEl(Icons.Accept) { Size = s.BoxSize * 0.62f, Color = s.GlyphColor, FontFamily = Theme.IconFont }
            : indet
                ? new BoxEl { Width = s.BoxSize * 0.5f, Height = 2f, Corners = CornerRadius4.All(1f), Fill = s.GlyphColor }
                : new BoxEl();

        var box = new BoxEl
        {
            Width = s.BoxSize, Height = s.BoxSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            BorderWidth = filled ? 0f : 1f,
            BorderColor = filled ? ColorF.Transparent : s.OffBorder,
            Fill = filled ? s.OnFill : s.OffFill,
            HoverFill = filled ? s.OnHover : s.OffHover,
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
