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
        public ColorF Foreground { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Foreground = Tok.AccentDefault, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
    };

    public static BoxEl Create(string text, Action onClick, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            Padding = new Edges4(8, 3, 8, 3),
            Corners = Radii.ControlAll,
            Role = AutomationRole.Hyperlink,
            HoverFill = s.HoverFill,
            PressedFill = s.PressedFill,
            OnClick = onClick,
            Children = [new TextEl(text) { Size = s.FontSize, Color = s.Foreground }],
        };
    }
}
