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
    /// <summary>A button's visual style. Colours resolve from <see cref="Tok"/> in the computed default styles below.</summary>
    public sealed record Style
    {
        public ColorF Background { get; init; }
        public ColorF Foreground { get; init; }
        /// <summary>The one border knob: a WinUI elevation gradient (the default) or a solid via <c>GradientSpec.Solid(c)</c>.</summary>
        public GradientSpec? BorderBrush { get; init; }
        public ColorF HoverBackground { get; init; }
        public ColorF PressedBackground { get; init; }
        public float BorderWidth { get; init; } = 1f;
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius = 4
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);    // ButtonPadding
        public float FontSize { get; init; } = 14f;                  // ControlContentThemeFontSize
        public float MinHeight { get; init; } = 32f;                 // effective WinUI button height
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
    };

    public static Style StandardStyle => StandardStyleOverride ?? new Style
    {
        Background = Tok.FillControlDefault,
        Foreground = Tok.TextPrimary,
        BorderBrush = Tok.ControlElevationBorder,
        HoverBackground = Tok.FillControlSecondary,
        PressedBackground = Tok.FillControlTertiary,
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
        OnClick = onClick,
        Children = [new TextEl(label) { Size = s.FontSize, Bold = s.Bold, Color = s.Foreground }],
    };
}
