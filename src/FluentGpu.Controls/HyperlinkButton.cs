using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>A WinUI HyperlinkButton: accent-colored link text with a subtle hover/press surface. Raises a click (in-app
/// navigation); an external URL is the caller's concern.</summary>
public static partial class HyperlinkButton
{
    public sealed record Style
    {
        public float FontSize { get; init; } = 14f;
        // WinUI HyperlinkButtonForeground{,PointerOver,Pressed,Disabled} = AccentTextFillColor{Primary,Secondary,Tertiary,Disabled}.
        // Foreground (rest) drives the visible text; the hover/pressed values are the eased TextEl ramps and
        // ForegroundDisabled is used when the interactive box is IsEnabled=false.
        public ColorF Foreground { get; init; }
        public ColorF ForegroundHover { get; init; }
        public ColorF ForegroundPressed { get; init; }
        public ColorF ForegroundDisabled { get; init; }
        // Background: HyperlinkButtonBackground{,PointerOver,Pressed,Disabled} = SubtleFillColor{Transparent,Secondary,Tertiary,Disabled}.
        public ColorF BackgroundRest { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF DisabledFill { get; init; }
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);   // no HyperlinkButtonPadding resource → WinUI uses ButtonPadding
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        // WinUI HyperlinkButtonForeground = AccentTextFillColor{Primary,Secondary,Tertiary,Disabled} — the accent TEXT
        // palette (solid, readability-adjusted), NOT the accent FILL. In dark, Primary==Secondary (only the fill changes
        // on hover); Tertiary (pressed) is one step deeper. Disabled = AccentTextFillColorDisabled (== Tok.TextDisabled).
        Foreground = Tok.AccentTextPrimary, ForegroundHover = Tok.AccentTextSecondary,
        ForegroundPressed = Tok.AccentTextTertiary, ForegroundDisabled = Tok.TextDisabled,
        BackgroundRest = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary, DisabledFill = Tok.FillSubtleTransparent,   // SubtleFillColorDisabled == transparent
    };

    public static BoxEl Create(string text, Action onClick, Style? style = null, bool isEnabled = true)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = s.Padding,
            Corners = Radii.ControlAll,
            Role = AutomationRole.Hyperlink,
            Fill = isEnabled ? s.BackgroundRest : s.DisabledFill,   // SubtleFillColorTransparent (rest) / disabled
            HoverFill = s.HoverFill,
            PressedFill = s.PressedFill,
            // BorderBrush = SubtleFillColorTransparent (all states) → no border (BorderWidth 0)
            IsEnabled = isEnabled,
            OnClick = onClick,
            Children = [new TextEl(text)
            {
                Size = s.FontSize, Color = s.Foreground,
                HoverColor = s.ForegroundHover, PressedColor = s.ForegroundPressed,
                DisabledColor = s.ForegroundDisabled,
            }],
        };
    }
}
