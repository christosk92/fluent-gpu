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
