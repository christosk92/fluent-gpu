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
        // The engine paints a single static TextEl colour (no per-state foreground hook on a leaf), so Foreground (rest)
        // drives the visible text; the hover/pressed/disabled values carry WinUI parity for theming/overrides.
        public ColorF Foreground { get; init; }
        public ColorF ForegroundHover { get; init; }
        public ColorF ForegroundPressed { get; init; }
        public ColorF ForegroundDisabled { get; init; }
        // Background: HyperlinkButtonBackground{,PointerOver,Pressed,Disabled} = SubtleFillColor{Transparent,Secondary,Tertiary,Disabled}.
        public ColorF BackgroundRest { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF DisabledFill { get; init; }
        public Edges4 Padding { get; init; } = new(0, 6, 0, 7);   // HyperlinkButtonPadding (NOT ButtonPadding)
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Foreground = Tok.AccentDefault, ForegroundHover = Tok.AccentSecondary,
        ForegroundPressed = Tok.AccentTertiary, ForegroundDisabled = Tok.AccentDisabled,
        BackgroundRest = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary, DisabledFill = Tok.FillControlDisabled,   // no SubtleDisabled token → closest
    };

    public static BoxEl Create(string text, Action onClick, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = s.Padding,
            Corners = Radii.ControlAll,
            Role = AutomationRole.Hyperlink,
            Fill = s.BackgroundRest,        // SubtleFillColorTransparent (rest)
            HoverFill = s.HoverFill,
            PressedFill = s.PressedFill,
            // BorderBrush = SubtleFillColorTransparent (all states) → no border (BorderWidth 0)
            OnClick = onClick,
            Children = [new TextEl(text) { Size = s.FontSize, Color = s.Foreground }],
        };
    }
}
