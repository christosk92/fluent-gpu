using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// The Button control: barebone behavior (clickable, hover/pressed) + a default Fluent style. Overrideable globally
/// (<see cref="AccentStyleOverride"/>/<see cref="StandardStyleOverride"/>), per-instance (pass a <see cref="Style"/>),
/// or ad-hoc (chain modifiers). Defaults are sourced from WinUI 3 (ButtonPadding 11,5,11,6; ControlCornerRadius 4;
/// ControlContentThemeFontSize 14; ControlElevationBorderBrush). <c>partial</c> so apps/framework can add variants.
/// </summary>
public static partial class Button
{
    /// <summary>A button's visual style. Colours resolve from <see cref="Tok"/> in the computed default styles below.
    /// State colours follow WinUI's 4-state matrix (normal/hover/pressed/disabled) for fill, foreground and border.</summary>
    public sealed record Style
    {
        public ColorF Background { get; init; }
        public ColorF Foreground { get; init; }
        /// <summary>The one border knob: a WinUI elevation gradient (the default) or a solid via <c>GradientSpec.Solid(c)</c>.</summary>
        public GradientSpec? BorderBrush { get; init; }
        public ColorF HoverBackground { get; init; }
        public ColorF PressedBackground { get; init; }
        // WinUI ButtonBackgroundDisabled / ButtonForeground{PointerOver,Pressed,Disabled} / ButtonBorderBrush{Pressed,Disabled}.
        // Now fully wired: Hover/Pressed/Disabled foreground → the TextEl interaction ramps (P2), per-state border →
        // Hover/PressedBorderBrush (P4b), Disabled fill/border + IsEnabled gate (P1) drive the disabled logical state.
        public ColorF DisabledBackground { get; init; }
        public ColorF HoverForeground { get; init; }
        public ColorF PressedForeground { get; init; }
        public ColorF DisabledForeground { get; init; }
        public GradientSpec? HoverBorderBrush { get; init; }
        public GradientSpec? PressedBorderBrush { get; init; }
        public GradientSpec? DisabledBorderBrush { get; init; }
        public float BorderWidth { get; init; } = 1f;
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius = 4
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);    // ButtonPadding
        public float FontSize { get; init; } = 14f;                  // ControlContentThemeFontSize
        public float MinHeight { get; init; } = 32f;                 // effective WinUI button height
        public float HoverScale { get; init; } = 1f;
        public float PressScale { get; init; } = 0.985f;
        public bool Bold { get; init; }
    }

    /// <summary>Set to globally replace the accent-button default style.</summary>
    public static Style? AccentStyleOverride;
    /// <summary>Set to globally replace the standard-button default style.</summary>
    public static Style? StandardStyleOverride;

    public static Style AccentStyle => AccentStyleOverride ?? new Style
    {
        Background = Tok.AccentDefault,
        Foreground = Tok.TextOnAccentPrimary,
        BorderBrush = Tok.AccentControlElevationBorder,
        HoverBackground = Tok.AccentSecondary,
        PressedBackground = Tok.AccentTertiary,
        DisabledBackground = Tok.AccentDisabled,            // AccentButtonBackgroundDisabled
        HoverForeground = Tok.TextOnAccentPrimary,
        PressedForeground = Tok.TextOnAccentSecondary,      // AccentButtonForegroundPressed
        DisabledForeground = Tok.TextOnAccentDisabled,      // AccentButtonForegroundDisabled
        HoverBorderBrush = Tok.AccentControlElevationBorder,
        PressedBorderBrush = GradientSpec.Solid(ColorF.Transparent),  // AccentButtonBorderBrushPressed = ControlFillColorTransparent
        DisabledBorderBrush = GradientSpec.Solid(ColorF.Transparent), // AccentButtonBorderBrushDisabled = ControlFillColorTransparent
    };

    public static Style StandardStyle => StandardStyleOverride ?? new Style
    {
        Background = Tok.FillControlDefault,
        Foreground = Tok.TextPrimary,
        BorderBrush = Tok.ControlElevationBorder,
        HoverBackground = Tok.FillControlSecondary,
        PressedBackground = Tok.FillControlTertiary,
        DisabledBackground = Tok.FillControlDisabled,                       // ButtonBackgroundDisabled
        HoverForeground = Tok.TextPrimary,
        PressedForeground = Tok.TextSecondary,                              // ButtonForegroundPressed
        DisabledForeground = Tok.TextDisabled,                             // ButtonForegroundDisabled
        HoverBorderBrush = Tok.ControlElevationBorder,
        PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),  // ButtonBorderBrushPressed
        DisabledBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault), // ButtonBorderBrushDisabled
    };

    /// <summary>An accent (primary) button. Override the look by passing a <see cref="Style"/>.</summary>
    public static BoxEl Accent(string label, Action onClick, Style? style = null, bool isEnabled = true) => Build(label, onClick, style ?? AccentStyle, isEnabled);

    /// <summary>A neutral (standard) button.</summary>
    public static BoxEl Standard(string label, Action onClick, Style? style = null, bool isEnabled = true) => Build(label, onClick, style ?? StandardStyle, isEnabled);

    private static BoxEl Build(string label, Action onClick, Style s, bool enabled) => new()
    {
        Direction = 0,
        Role = AutomationRole.Button,
        Padding = s.Padding,
        MinHeight = s.MinHeight,
        AlignItems = FlexAlign.Center,
        // Disabled is a logical state (no engine ramp): resting fill/border swap to the WinUI disabled tokens. Hover/Pressed
        // never fire while disabled (the engine gate stops hit-test), so HoverFill/PressedFill stay wired but inert.
        Fill = enabled ? s.Background : s.DisabledBackground,
        HoverFill = s.HoverBackground,
        PressedFill = s.PressedBackground,
        BorderBrush = enabled ? s.BorderBrush : (s.DisabledBorderBrush ?? s.BorderBrush),
        HoverBorderBrush = s.HoverBorderBrush,           // P4b state-gradient border
        PressedBorderBrush = s.PressedBorderBrush,
        BorderWidth = s.BorderWidth,
        Corners = CornerRadius4.All(s.CornerRadius),
        HoverScale = enabled ? s.HoverScale : 1f,
        PressScale = enabled ? s.PressScale : 1f,
        IsEnabled = enabled,                              // P1 engine gate (no manual handler-nulling)
        OnClick = onClick,
        Children =
        [
            new TextEl(label)
            {
                Size = s.FontSize, Bold = s.Bold,
                Color = s.Foreground,                    // P2 foreground ramp: rest → hover → pressed; disabled via the gate
                HoverColor = s.HoverForeground,
                PressedColor = s.PressedForeground,
                DisabledColor = s.DisabledForeground,
            },
        ],
    };
}
