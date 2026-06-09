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
        public Edges4 Padding { get; init; } = new(8, 4, 8, 5);      // ButtonPadding (merged generic.xaml)
        public float MinHeight { get; init; } = 32f;
        public float FontSize { get; init; } = 14f;
        public float BorderWidth { get; init; } = 1f;
        // On (Checked) state
        public ColorF OnBackground { get; init; }
        public ColorF OnHover { get; init; }
        public ColorF OnPressed { get; init; }
        public ColorF OnDisabledBackground { get; init; }  // ToggleButtonBackgroundCheckedDisabled = AccentFillColorDisabled
        public ColorF OnForeground { get; init; }
        public ColorF OnPressedForeground { get; init; }   // ToggleButtonForegroundCheckedPressed = TextOnAccentSecondary
        public ColorF OnDisabledForeground { get; init; }  // ToggleButtonForegroundCheckedDisabled = TextOnAccentDisabled
        public GradientSpec? OnBorder { get; init; }
        public GradientSpec? OnPressedBorder { get; init; }  // ToggleButtonBorderBrushCheckedPressed = transparent
        public GradientSpec? OnDisabledBorder { get; init; } // ToggleButtonBorderBrushCheckedDisabled = transparent
        // Off (Unchecked) state
        public ColorF OffBackground { get; init; }
        public ColorF OffHover { get; init; }
        public ColorF OffPressed { get; init; }
        public ColorF OffDisabledBackground { get; init; }  // ToggleButtonBackgroundDisabled = FillControlDisabled
        public ColorF OffForeground { get; init; }
        public ColorF OffPressedForeground { get; init; }   // ToggleButtonForegroundPressed = TextSecondary
        public ColorF OffDisabledForeground { get; init; }  // ToggleButtonForegroundDisabled = TextDisabled
        public GradientSpec? OffBorder { get; init; }
        public GradientSpec? OffPressedBorder { get; init; }  // ToggleButtonBorderBrushPressed = StrokeControlDefault
        public GradientSpec? OffDisabledBorder { get; init; } // ToggleButtonBorderBrushDisabled = StrokeControlDefault
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OnBackground = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary,
        OnDisabledBackground = Tok.AccentDisabled,
        OnForeground = Tok.TextOnAccentPrimary, OnBorder = Tok.AccentControlElevationBorder,
        OnPressedForeground = Tok.TextOnAccentSecondary, OnDisabledForeground = Tok.TextOnAccentDisabled,
        OnPressedBorder = null, OnDisabledBorder = null,   // ControlFillColorTransparent → null
        OffBackground = Tok.FillControlDefault, OffHover = Tok.FillControlSecondary, OffPressed = Tok.FillControlTertiary,
        OffDisabledBackground = Tok.FillControlDisabled,
        OffForeground = Tok.TextPrimary, OffBorder = Tok.ControlElevationBorder,
        OffPressedForeground = Tok.TextSecondary, OffDisabledForeground = Tok.TextDisabled,
        OffPressedBorder = GradientSpec.Solid(Tok.StrokeControlDefault),
        OffDisabledBorder = GradientSpec.Solid(Tok.StrokeControlDefault),
    };

    public static BoxEl Create(string label, bool on, Action onToggle, Style? style = null, bool isEnabled = true)
        => Build(label, on ? CheckState.Checked : CheckState.Unchecked, _ => onToggle(), style, isEnabled);

    /// <summary>Three-state toggle (adds the mixed "indeterminate" look). Click cycles Unchecked → Checked → Indeterminate.</summary>
    public static BoxEl Create(string label, CheckState state, Action<CheckState> onCycle, Style? style = null, bool isEnabled = true)
    {
        var next = state switch
        {
            CheckState.Unchecked => CheckState.Checked,
            CheckState.Checked => CheckState.Indeterminate,
            _ => CheckState.Unchecked,
        };
        return Build(label, state, _ => onCycle(next), style, isEnabled);
    }

    static BoxEl Build(string label, CheckState state, Action<CheckState> onClick, Style? style, bool isEnabled)
    {
        var s = style ?? DefaultStyle;
        bool on = state == CheckState.Checked;
        // Resting per-state fill / foreground / border (the engine eases hover/press; disabled visuals stay control-chosen).
        // WinUI ToggleButton Indeterminate == Unchecked (neutral control), per ToggleButton_themeresources.xaml — not accent.
        ColorF restFill = on ? s.OnBackground : s.OffBackground;
        ColorF disFill = on ? s.OnDisabledBackground : s.OffDisabledBackground;  // indet uses the standard control disabled fill
        ColorF restFg = on ? s.OnForeground : s.OffForeground;
        ColorF pressFg = on ? s.OnPressedForeground : s.OffPressedForeground;
        ColorF disFg = on ? s.OnDisabledForeground : s.OffDisabledForeground;  // indet → TextDisabled
        GradientSpec? restBorder = on ? s.OnBorder : s.OffBorder;
        GradientSpec? disBorder = on ? s.OnDisabledBorder : s.OffDisabledBorder;
        GradientSpec? pressBorder = on ? s.OnPressedBorder : s.OffPressedBorder;
        return new BoxEl
        {
            Direction = 0, Role = AutomationRole.ToggleButton, Padding = s.Padding, MinHeight = s.MinHeight, AlignItems = FlexAlign.Center,
            Corners = CornerRadius4.All(s.CornerRadius), BorderWidth = s.BorderWidth,
            Fill = isEnabled ? restFill : disFill,
            HoverFill = on ? s.OnHover : s.OffHover,
            PressedFill = on ? s.OnPressed : s.OffPressed,
            BorderBrush = isEnabled ? restBorder : (disBorder ?? restBorder),
            PressedBorderBrush = pressBorder,
            IsEnabled = isEnabled,
            OnClick = () => onClick(state),
            Children = [new TextEl(label)
            {
                Size = s.FontSize, Color = restFg, PressedColor = pressFg, DisabledColor = disFg,
            }],
        };
    }
}
