using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// ONE polymorphic, NativeAOT-true icon value (no interfaces, no boxing, no reflection) carried by menu/command rows.
/// It is either a PUA <see cref="Glyph"/> string (the existing icon-font world — <see cref="Font"/> absorbs the old
/// per-row glyph-font override) or a layered-vector <see cref="ThemedName"/> (a <c>ThemedIconRegistry</c> key). A plain
/// <c>string</c> converts implicitly, so every existing glyph call-site is unchanged; <see cref="Themed"/> attaches a
/// fallback glyph that renders until the harvest covers the themed name.
/// </summary>
public readonly record struct IconRef
{
    /// <summary>PUA glyph string (icon-font code point). The fallback when <see cref="ThemedName"/> is unset/unregistered.</summary>
    public string? Glyph { get; init; }
    /// <summary>Icon-font family override for <see cref="Glyph"/> ("path#family"). Null ⇒ <c>Theme.IconFont</c>. Absorbs
    /// the old <c>MenuFlyoutItem.GlyphFont</c> concept so an app can ship glyphs the stock icon font doesn't carry.</summary>
    public string? Font { get; init; }
    /// <summary>Layered-vector icon key (<c>ThemedIconRegistry</c>). Wins over <see cref="Glyph"/> when set AND
    /// registered; otherwise the row falls back to <see cref="Glyph"/>.</summary>
    public string? ThemedName { get; init; }

    /// <summary>A bare glyph string is an icon (nullable so the many <c>MenuFlyoutItem(label, null, …)</c> call-sites
    /// keep converting cleanly to an empty icon — no CS8625 noise).</summary>
    public static implicit operator IconRef(string? glyph) => new() { Glyph = glyph };

    /// <summary>A layered-vector icon by registry name, with an optional glyph rendered until the themed harvest covers it.</summary>
    public static IconRef Themed(string name, string? fallbackGlyph = null) => new() { ThemedName = name, Glyph = fallbackGlyph };

    /// <summary>Nothing to render (no glyph, no themed name).</summary>
    public bool IsNone => Glyph is null && ThemedName is null;

    /// <summary>True when this ref will render pixels (a non-empty glyph or a themed name) — the icon-column reservation test.</summary>
    internal bool HasContent => Glyph is { Length: > 0 } || ThemedName is { Length: > 0 };
}

/// <summary>
/// The ONE internal render path for an <see cref="IconRef"/> — every menu/command icon slot routes through it, so the
/// glyph-vs-themed decision (and the disabled/on-accent handling) lives in a single place. A registered
/// <see cref="IconRef.ThemedName"/> becomes the layered <c>ThemedIcon</c> stack (theme-live tints, no re-raster); else a
/// glyph <see cref="TextEl"/> in <c>Font ?? Theme.IconFont</c>; an empty ref renders an empty leaf (keeps the column).
/// </summary>
internal static class IconView
{
    public static Element Render(in IconRef icon, float size, ColorF glyphColor,
        ColorF? pressedColor = null, ColorF? disabledColor = null, System.Func<bool>? enabled = null,
        IconColorType themedColor = IconColorType.Normal, bool onAccent = false)
    {
        if (icon.ThemedName is { Length: > 0 } tn && ThemedIconRegistry.Has(tn))
            return ThemedIcon.Create(tn, size, color: themedColor, enabled: enabled, onAccent: onAccent);
        if (icon.Glyph is { Length: > 0 } g)
        {
            var t = new TextEl(g) { Size = size, Color = glyphColor, FontFamily = icon.Font ?? Theme.IconFont };
            if (pressedColor is { } pc) t = t with { PressedColor = pc };
            if (disabledColor is { } dc) t = t with { DisabledColor = dc };
            return t;
        }
        return new BoxEl();
    }
}
