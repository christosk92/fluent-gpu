using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;

namespace Wavee;

// The boundary mapper: framework-neutral Wavee.Core.Palette (uint ARGB) → engine ColorF. This is the ONLY place the
// album-art palette becomes renderer color (keeps Wavee.Core free of FluentGpu types). Used by the now-playing recolor.
public static class WaveePalette
{
    public static ColorF ToColor(uint argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        return ColorF.FromRgba(r, g, b, a);
    }

    /// <summary>Brighten a color so its strongest channel reaches <paramref name="targetMax"/> (0–255), scaling RGB
    /// uniformly to preserve hue — only ever lifts, never darkens. Spotify's extracted <c>colorDark</c> is often
    /// near-black and collapses to nothing as a faint tint/bar; this keeps it legible. (Port of WaveeMusic's BrightenForTint.)</summary>
    public static ColorF Lift(ColorF c, byte targetMax = 210)
    {
        float target = targetMax / 255f;
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        if (max <= 0.001f) { float v = target; return new ColorF(v, v, v, c.A); }   // pure black → neutral grey at the target
        if (max >= target) return c;                                                // already bright enough — don't darken
        float k = target / max;
        return new ColorF(MathF.Min(1f, c.R * k), MathF.Min(1f, c.G * k), MathF.Min(1f, c.B * k), c.A);
    }

    public static ColorF Accent(Palette p) => ToColor(p.Accent);
    public static ColorF BackgroundDark(Palette p) => ToColor(p.BackgroundDark);
    public static ColorF TintedDark(Palette p) => ToColor(p.TintedDark);

    /// <summary>Neutral card fill under <see cref="Surfaces.HeroWash"/> — same as the shell content card on detail pages.</summary>
    public static ColorF HeroBase(Palette? art) => WaveeColors.FileArea;

    /// <summary>Hero-wash accent — same derivation as <c>DetailShell</c> (lifted accent in light, <c>colorDark</c> in dark).</summary>
    public static ColorF HeroWashColor(Palette? art) =>
        Tok.Theme == ThemeKind.Light
            ? (art is { } p ? Lift(Accent(p)) : Tok.AccentDefault)
            : BackgroundDark(art ?? Neutral);

    /// <summary>The legible ink (near-black or white) for text/icons sitting ON a solid <paramref name="accent"/> fill,
    /// chosen by the fill's WCAG luminance — NOT the theme. A cover-extracted accent (after <see cref="Lift"/>) can land
    /// anywhere from a near-white grey to a saturated mid-tone, so the theme-only <c>Tok.TextOnAccentPrimary</c> (white in
    /// light theme) failed contrast on a lifted-bright cover accent. This always picks the higher-contrast of black/white.</summary>
    public static ColorF OnAccent(ColorF accent)
    {
        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        float l = 0.2126f * Lin(accent.R) + 0.7152f * Lin(accent.G) + 0.0722f * Lin(accent.B);
        // Contrast vs white = 1.05/(l+0.05); vs black = (l+0.05)/0.05. Pick whichever reads better on this fill.
        return (l + 0.05f) / 0.05f >= 1.05f / (l + 0.05f) ? ColorF.FromRgba(0x16, 0x16, 0x16) : ColorF.FromRgba(255, 255, 255);
    }

    /// <summary>Neutral fallback when no palette is available (no current track / not yet extracted).</summary>
    public static Palette Neutral { get; } = new(BackgroundDark: 0xFF1C1C1C, TintedDark: 0xFF2A2A2A, Light: 0xFFFFFFFF, Accent: 0xFF2E6CE0);

    /// <summary>The player bar's neutral dark base — the surface the album hue is only faintly lifted from. WinUI's
    /// subtlety came from acrylic over a real blurred desktop; we have neither, so the bar is a flat neutral fill with a
    /// capped hue instead of a saturated tint.</summary>
    public static ColorF BarSurface { get; } = ColorF.FromRgba(0x1A, 0x1B, 0x1E, 0xFF);

    /// <summary>The player bar fill: the neutral <see cref="BarSurface"/> with only ~10% of the track accent blended in,
    /// so the bar reads neutral-dark with a hint of the cover — never the raw saturated album colour.</summary>
    public static ColorF BarTint(Palette p) => ColorF.Lerp(BarSurface, Accent(p), 0.10f);
}
