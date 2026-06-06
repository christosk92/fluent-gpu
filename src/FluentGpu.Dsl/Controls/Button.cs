using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// The Button control: barebone behavior (clickable + focusable, hover/pressed states) + a default Fluent style on top.
/// Styles are overrideable three ways: globally (set <see cref="AccentStyleOverride"/>/<see cref="StandardStyleOverride"/>),
/// per-instance (pass a <see cref="ButtonStyle"/>), or ad-hoc (chain modifiers on the result). Defaults derive from the
/// <see cref="Theme"/> design tokens. <c>partial</c> so the framework and apps can add variants in separate files.
/// </summary>
public static partial class Button
{
    /// <summary>Set to globally replace the accent-button default style.</summary>
    public static ButtonStyle? AccentStyleOverride;
    /// <summary>Set to globally replace the standard-button default style.</summary>
    public static ButtonStyle? StandardStyleOverride;

    public static ButtonStyle AccentStyle => AccentStyleOverride ?? new ButtonStyle
    {
        Background = Theme.Accent,
        Foreground = Theme.AccentText,
        Border = Theme.AccentBorder,
        HoverBackground = Theme.Accent with { A = 0.9f },
        PressedBackground = Theme.Accent with { A = 0.8f },
    };

    public static ButtonStyle StandardStyle => StandardStyleOverride ?? new ButtonStyle
    {
        Background = Theme.ControlFill,
        Foreground = Theme.ControlText,
        Border = Theme.ControlBorder,
        HoverBackground = Theme.ControlFillHover,
        PressedBackground = Theme.ControlFillPressed,
    };

    /// <summary>An accent (primary) button. Override the look by passing a <see cref="ButtonStyle"/>.</summary>
    public static BoxEl Accent(string label, Action onClick, ButtonStyle? style = null) => Build(label, onClick, style ?? AccentStyle);

    /// <summary>A neutral (standard) button.</summary>
    public static BoxEl Standard(string label, Action onClick, ButtonStyle? style = null) => Build(label, onClick, style ?? StandardStyle);

    private static BoxEl Build(string label, Action onClick, ButtonStyle s) => new()
    {
        Direction = 0,
        Padding = s.Padding,
        MinHeight = s.MinHeight,
        AlignItems = FlexAlign.Center,
        Fill = s.Background,
        HoverFill = s.HoverBackground,
        PressedFill = s.PressedBackground,
        BorderColor = s.Border,
        BorderWidth = s.BorderWidth,
        Corners = CornerRadius4.All(s.CornerRadius),
        OnClick = onClick,
        Children = [new TextEl(label) { Size = s.FontSize, Bold = s.Bold, Color = s.Foreground }],
    };
}
