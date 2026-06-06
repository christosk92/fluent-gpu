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

    // Text
    public static TextEl Foreground(this TextEl t, ColorF c) => t with { Color = c };
    public static TextEl FontSize(this TextEl t, float size) => t with { Size = size };
    public static TextEl Strong(this TextEl t) => t with { Bold = true };
}
