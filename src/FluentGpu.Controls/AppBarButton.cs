using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarButton: a vertically-stacked command button with a 16px icon glyph over a 12px label.
/// Used inside a CommandBar. Hover/press states use the subtle fill tokens; the whole control is one click target.</summary>
public static class AppBarButton
{
    public static BoxEl Create(string glyph, string label, Action onClick) => new BoxEl
    {
        Direction = 1,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Gap = 4,
        MinWidth = 48,
        MinHeight = 48,
        Padding = new Edges4(4, 6, 4, 6),
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        OnClick = onClick,
        Role = AutomationRole.Button,
        Children =
        [
            new TextEl(glyph) { Size = 16, Color = Tok.TextPrimary, FontFamily = Theme.IconFont },
            new TextEl(label) { Size = 12, Color = Tok.TextPrimary },
        ],
    };
}
