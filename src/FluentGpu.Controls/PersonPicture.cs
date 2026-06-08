using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI PersonPicture: a circular avatar showing initials (or a glyph) on a control-strong fill, ringed by a
/// 1px stroke. Default diameter 48; the initials scale with the picture (40% of the diameter).</summary>
public static class PersonPicture
{
    public static BoxEl Create(string initials, float size = 48f, ColorF? fill = null) => new BoxEl
    {
        Width = size,
        Height = size,
        Corners = Radii.Circle(size),
        Fill = fill ?? Tok.FillControlStrong,
        BorderWidth = 1f,
        BorderColor = Tok.StrokeControlSecondary,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        ClipToBounds = true,
        Children = [new TextEl(initials) { Size = size * 0.4f, Bold = true, Color = Tok.TextPrimary }],
    };
}
