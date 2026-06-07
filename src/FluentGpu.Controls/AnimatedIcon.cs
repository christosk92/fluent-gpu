using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// Lightweight AnimatedIcon analogue for glyph icons. The wrapper scales from the nearest interactive ancestor's
/// eased hover/press progress, matching WinUI's AnimatedIcon.State PointerOver/Pressed affordance without relayout.
/// </summary>
public static class AnimatedIcon
{
    public static BoxEl Glyph(string glyph, float size = 16f, ColorF? color = null, string? fontFamily = null,
                              float hoverScale = 1.08f, float pressScale = 0.88f) => new()
    {
        Width = size,
        Height = size,
        Direction = 0,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        HoverScale = hoverScale,
        PressScale = pressScale,
        Children =
        [
            new TextEl(glyph)
            {
                Size = size,
                Color = color ?? Tok.TextPrimary,
                FontFamily = fontFamily ?? Theme.IconFont,
            },
        ],
    };
}
