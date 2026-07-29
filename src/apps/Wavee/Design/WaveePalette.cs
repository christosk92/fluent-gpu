using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.SpotifyLive;

namespace Wavee;

// The boundary mapper: the framework-neutral cover-colour roles (uint ARGB, from CoverColorPlane) → engine ColorF.
// This is the ONLY place a cover colour becomes a renderer colour. A page asks the plane for its cover's Scheme and
// maps the roles it needs here; nothing carries a per-entity palette any more.
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

    // The four roles the app's chrome asks for, over the plane's five-role scheme. textBrightAccent IS the accent
    // Spotify grades for text/controls on that cover; backgroundBase is the dominant tone; backgroundTintedBase is the
    // slightly-lifted band tone the page washes use.
    public static ColorF Accent(in CoverColorPlane.Scheme s) => ToColor(s.TextBrightAccent);
    public static ColorF BackgroundDark(in CoverColorPlane.Scheme s) => ToColor(s.BackgroundBase);
    public static ColorF TintedDark(in CoverColorPlane.Scheme s) => ToColor(s.BackgroundTintedBase);

    /// <summary>Neutral card fill under <see cref="Surfaces.HeroWash"/> — same as the shell content card on detail pages.</summary>
    public static ColorF HeroBase(CoverColorPlane.Scheme? art) => WaveeColors.FileArea;

    /// <summary>Hero-wash accent — same derivation as <c>DetailShell</c> (lifted accent in light, the dominant tone in dark).</summary>
    public static ColorF HeroWashColor(CoverColorPlane.Scheme? art) =>
        Tok.Theme == ThemeKind.Light
            ? (art is { } p ? Lift(Accent(p)) : Tok.AccentDefault)
            : BackgroundDark(art ?? Neutral);

    /// <summary>Neutral fallback when the plane has no grading yet (no current track / not fetched).</summary>
    public static CoverColorPlane.Scheme Neutral { get; } =
        new(BackgroundBase: 0xFF1C1C1C, BackgroundTintedBase: 0xFF2A2A2A, TextBase: 0xFFFFFFFF,
            TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFF2E6CE0);

    /// <summary>The player bar's neutral dark base — the surface the album hue is only faintly lifted from. WinUI's
    /// subtlety came from acrylic over a real blurred desktop; we have neither, so the bar is a flat neutral fill with a
    /// capped hue instead of a saturated tint.</summary>
    public static ColorF BarSurface { get; } = ColorF.FromRgba(0x1A, 0x1B, 0x1E, 0xFF);

    /// <summary>The player bar fill: the neutral <see cref="BarSurface"/> with only ~10% of the track accent blended in,
    /// so the bar reads neutral-dark with a hint of the cover — never the raw saturated album colour.</summary>
    public static ColorF BarTint(in CoverColorPlane.Scheme s) => ColorF.Lerp(BarSurface, Accent(s), 0.10f);
}
