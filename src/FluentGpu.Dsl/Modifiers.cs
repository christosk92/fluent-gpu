using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Fluent per-element style overrides (React/Compose/WinUI-style): <c>Button("x", onClick).Background(c).Rounded(8)</c>.
/// Each returns a record <c>with</c>-copy, so any default style is overrideable inline without forking the control.
/// </summary>
public static class Modifiers
{
    // Box surface
    public static BoxEl Background(this BoxEl b, ColorF c) => b with { Fill = c };
    public static BoxEl HoverColor(this BoxEl b, ColorF c) => b with { HoverFill = c };
    public static BoxEl PressedColor(this BoxEl b, ColorF c) => b with { PressedFill = c };
    public static BoxEl Border(this BoxEl b, ColorF c, float width = 1f) => b with { BorderColor = c, BorderWidth = width };
    public static BoxEl Rounded(this BoxEl b, float radius) => b with { Corners = CornerRadius4.All(radius) };
    public static BoxEl Pad(this BoxEl b, float all) => b with { Padding = Edges4.All(all) };
    public static BoxEl Pad(this BoxEl b, float left, float top, float right, float bottom) => b with { Padding = new Edges4(left, top, right, bottom) };

    // Rich paint
    public static BoxEl Shadow(this BoxEl b, ShadowSpec s) => b with { Shadow = s };
    public static BoxEl Elevate(this BoxEl b, ShadowSpec preset) => b with { Shadow = preset };
    public static BoxEl Gradient(this BoxEl b, GradientSpec g) => b with { Gradient = g };
    public static BoxEl BorderBrush(this BoxEl b, GradientSpec g, float width = 1f) => b with { BorderBrush = g, BorderWidth = width };
    public static BoxEl Acrylic(this BoxEl b, AcrylicSpec a) => b with { Acrylic = a };

    // Composited transform + opacity (animate without relayout)
    public static BoxEl Offset(this BoxEl b, float dx, float dy) => b with { OffsetX = dx, OffsetY = dy };
    public static BoxEl Scale(this BoxEl b, float s) => b with { ScaleX = s, ScaleY = s };
    public static BoxEl Scale(this BoxEl b, float sx, float sy) => b with { ScaleX = sx, ScaleY = sy };
    public static BoxEl Rotate(this BoxEl b, float degrees) => b with { Rotation = degrees };
    public static BoxEl Alpha(this BoxEl b, float opacity) => b with { Opacity = opacity };

    // Text
    public static TextEl Foreground(this TextEl t, ColorF c) => t with { Color = c };
    public static TextEl FontSize(this TextEl t, float size) => t with { Size = size };
    public static TextEl Strong(this TextEl t) => t with { Bold = true };
    public static TextEl Font(this TextEl t, string family) => t with { FontFamily = family };
    public static TextEl Wrapped(this TextEl t, TextWrap wrap = TextWrap.Wrap) => t with { Wrap = wrap };
    public static TextEl NoWrap(this TextEl t) => t with { Wrap = TextWrap.NoWrap };
    public static TextEl Trim(this TextEl t, TextTrim trim = TextTrim.CharacterEllipsis) => t with { Trim = trim };
    public static TextEl Ellipsis(this TextEl t) => t with { Trim = TextTrim.CharacterEllipsis };
    public static TextEl MaxLines(this TextEl t, int lines) => t with { MaxLines = lines };
}
