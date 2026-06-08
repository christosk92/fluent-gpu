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
        // Hover/Pressed/Disabled foreground + Disabled fill + per-state border carry the WinUI-correct values; the engine
        // wires box Fill/HoverFill/PressedFill and the static foreground/border directly (no text-color or disabled state
        // machine on a leaf BoxEl/TextEl), so these document parity for theming/overrides and future disabled wiring.
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
        PressedBorderBrush = null,                          // AccentButtonBorderBrushPressed = ControlFillColorTransparent
        DisabledBorderBrush = null,                         // AccentButtonBorderBrushDisabled = ControlFillColorTransparent
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
    public static BoxEl Accent(string label, Action onClick, Style? style = null) => Build(label, onClick, style ?? AccentStyle);

    /// <summary>A neutral (standard) button.</summary>
    public static BoxEl Standard(string label, Action onClick, Style? style = null) => Build(label, onClick, style ?? StandardStyle);

    private static BoxEl Build(string label, Action onClick, Style s) => new()
    {
        Direction = 0,
        Role = AutomationRole.Button,
        Padding = s.Padding,
        MinHeight = s.MinHeight,
        AlignItems = FlexAlign.Center,
        Fill = s.Background,
        HoverFill = s.HoverBackground,
        PressedFill = s.PressedBackground,
        BorderBrush = s.BorderBrush,
        BorderWidth = s.BorderWidth,
        Corners = CornerRadius4.All(s.CornerRadius),
        HoverScale = s.HoverScale,
        PressScale = s.PressScale,
        OnClick = onClick,
        Children = [new TextEl(label) { Size = s.FontSize, Bold = s.Bold, Color = s.Foreground }],
    };
}
