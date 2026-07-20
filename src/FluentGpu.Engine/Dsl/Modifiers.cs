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
    /// <summary>Layout firewall: mark this box as a <b>layout boundary</b> (<c>IsolateLayout</c> + <c>ClipToBounds</c>) so a
    /// re-render or state change deep inside its subtree re-solves ONLY this subtree (scoped relayout) instead of escaping to
    /// a full-tree layout from the scene root. <b>Contract:</b> use this ONLY where the box's outer size is parent-determined
    /// and can never be content-sized by a descendant (a page/content host that fills its region) — the scoped relayout reuses
    /// the box's current bounds. A boundary <b>implies clipping</b> (a firewalled subtree must not paint outside the bounds the
    /// parent gave it), so this sets <see cref="BoxEl.ClipToBounds"/> too. A window resize still triggers a full layout, so
    /// resize stays correct. See <see cref="BoxEl.IsolateLayout"/> and the relayout-escape diagnostic
    /// (<c>FrameStats.RootRelayoutEscapes</c> + the <c>FG_DIAG</c> "relayout escaped to root" message).</summary>
    public static BoxEl Boundary(this BoxEl b) => b with { IsolateLayout = true, ClipToBounds = true };
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
    /// <summary>WinUI BodyStrong is SemiBold 600 (BaseTextBlockStyle FontWeight, TextBlock_themeresources.xaml:13)
    /// — Strong() means SemiBold, not Bold 700. Use <see cref="FontWeight"/> or <c>Bold = true</c> for 700.</summary>
    public static TextEl Strong(this TextEl t) => t with { Weight = 600 };
    /// <summary>Numeric font weight (WinUI FontWeight; the value IS the DWrite weight, e.g. 350 SemiLight, 600 SemiBold).</summary>
    public static TextEl FontWeight(this TextEl t, ushort weight) => t with { Weight = weight };
    public static TextEl Font(this TextEl t, string family) => t with { FontFamily = family };
    public static TextEl Wrapped(this TextEl t, TextWrap wrap = TextWrap.Wrap) => t with { Wrap = wrap };
    public static TextEl NoWrap(this TextEl t) => t with { Wrap = TextWrap.NoWrap };
    public static TextEl Trim(this TextEl t, TextTrim trim = TextTrim.CharacterEllipsis) => t with { Trim = trim };
    public static TextEl Ellipsis(this TextEl t) => t with { Trim = TextTrim.CharacterEllipsis };
    public static TextEl MaxLines(this TextEl t, int lines) => t with { MaxLines = lines };
}
