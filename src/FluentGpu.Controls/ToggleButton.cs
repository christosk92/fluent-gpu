using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A two-state toggle (shuffle/repeat/follow). Accent-filled when on — WinUI ToggleButton's checked state
/// (AccentFillColorDefault bg + TextOnAccent fg + AccentControlElevationBorder); the unchecked state is a standard
/// control fill. Controlled: pass <paramref name="on"/> and a toggle callback.
/// </summary>
public static partial class ToggleButton
{
    public sealed record Style
    {
        public float CornerRadius { get; init; } = Radii.Control;    // 4 (ControlCornerRadius)
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);    // ButtonPadding
        public float MinHeight { get; init; } = 32f;
        public float FontSize { get; init; } = 14f;
        public float BorderWidth { get; init; } = 1f;
        public ColorF OnBackground { get; init; }
        public ColorF OnHover { get; init; }
        public ColorF OnPressed { get; init; }
        public ColorF OnForeground { get; init; }
        public GradientSpec? OnBorder { get; init; }
        public ColorF OffBackground { get; init; }
        public ColorF OffHover { get; init; }
        public ColorF OffPressed { get; init; }
        public ColorF OffForeground { get; init; }
        public GradientSpec? OffBorder { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OnBackground = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary,
        OnForeground = Tok.TextOnAccentPrimary, OnBorder = Tok.AccentControlElevationBorder,
        OffBackground = Tok.FillControlDefault, OffHover = Tok.FillControlSecondary, OffPressed = Tok.FillControlTertiary,
        OffForeground = Tok.TextPrimary, OffBorder = Tok.ControlElevationBorder,
    };

    public static BoxEl Create(string label, bool on, Action onToggle, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Direction = 0, Role = AutomationRole.ToggleButton, Padding = s.Padding, MinHeight = s.MinHeight, AlignItems = FlexAlign.Center,
            Corners = CornerRadius4.All(s.CornerRadius), BorderWidth = s.BorderWidth,
            Fill = on ? s.OnBackground : s.OffBackground,
            HoverFill = on ? s.OnHover : s.OffHover,
            PressedFill = on ? s.OnPressed : s.OffPressed,
            BorderBrush = on ? s.OnBorder : s.OffBorder,
            OnClick = onToggle,
            Children = [new TextEl(label) { Size = s.FontSize, Color = on ? s.OnForeground : s.OffForeground }],
        };
    }
}
