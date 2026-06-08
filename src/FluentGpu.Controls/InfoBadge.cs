using FluentGpu.Foundation;
using FluentGpu.Dsl;
namespace FluentGpu.Controls;
public static class InfoBadge {
  public static BoxEl Dot(ColorF? color = null) =>
    new BoxEl { Width = 10f, Height = 10f, Corners = Radii.Circle(10f), Fill = color ?? Tok.AccentDefault };
  public static BoxEl Count(int value, ColorF? color = null) =>
    new BoxEl {
      Direction = 0, MinWidth = 16f, Height = 16f, Corners = Radii.Circle(16f), Fill = color ?? Tok.AccentDefault,
      AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = new Edges4(5, 0, 5, 0),
      Children = [ new TextEl(value.ToString()) { Size = 11f, Bold = true, Color = Tok.TextOnAccentPrimary } ],
    };
  public static BoxEl Icon(string glyph, ColorF? color = null) =>
    new BoxEl {
      Direction = 0, MinWidth = 16f, Height = 16f, Corners = Radii.Circle(16f), Fill = color ?? Tok.AccentDefault,
      AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = new Edges4(5, 0, 5, 0),
      Children = [ new TextEl(glyph) { Size = 10f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary } ],
    };
}
