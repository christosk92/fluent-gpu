using FluentGpu.Foundation;
using FluentGpu.Dsl;
namespace FluentGpu.Controls;
public static class InfoBadge {
  // WinUI InfoBadge: MinHeight=MinWidth=4, MaxHeight=16. Background=AccentFillColorDefault,
  // Foreground=TextOnAccentFillColorPrimary. The root grid carries no padding; the inner Value text /
  // FontIcon supply their own margins (ValueInfoBadgeTextMargin / IconInfoBadgeFontIconMargin = 4,0,4,2),
  // which is what gives Count/Icon their pill width while the height stays clamped to 16.
  public static BoxEl Dot(ColorF? color = null) =>
    new BoxEl { Width = 4f, Height = 4f, Corners = Radii.Circle(4f), Fill = color ?? Tok.AccentDefault };
  public static BoxEl Count(int value, ColorF? color = null) =>
    new BoxEl {
      Direction = 0, MinWidth = 16f, Height = 16f, MaxHeight = 16f, Corners = Radii.Circle(16f), Fill = color ?? Tok.AccentDefault,
      AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
      Children = [ new TextEl(value.ToString()) { Size = 11f, Color = Tok.TextOnAccentPrimary, Margin = new Edges4(4, 0, 4, 2) } ],
    };
  public static BoxEl Icon(string glyph, ColorF? color = null) =>
    new BoxEl {
      Direction = 0, MinWidth = 16f, Height = 16f, MaxHeight = 16f, Corners = Radii.Circle(16f), Fill = color ?? Tok.AccentDefault,
      AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
      Children = [ new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary, Margin = new Edges4(4, 0, 4, 2) } ],
    };
}
