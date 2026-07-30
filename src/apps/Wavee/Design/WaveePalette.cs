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

    /// <summary>Below this HSV saturation a colour has no hue worth amplifying — grading a genuinely greyscale cover
    /// would just produce a random tint. Callers fall back to the semantic accent instead.</summary>
    public const float NeutralS = 0.08f;

    /// <summary>Saturation FLOOR for a colour used as a CHROME FILL (the Play capsule, the Verified pill, the accent
    /// bars). <see cref="Lift"/> only raises brightness, so a cover graded to a washed pastel stayed washed and the CTA
    /// read as a grey plate. This pushes S up to <paramref name="minS"/> at constant HUE and keeps V at or above
    /// <paramref name="targetMax"/> — the same 210 ceiling Lift targets, so after Lift the V clamp is a no-op and only
    /// S moves. Near-neutrals (S ≤ <see cref="NeutralS"/>) are returned UNCHANGED; see <see cref="ChromeAccent"/> for
    /// the greyscale-art fallback.
    ///
    /// Raising S at constant V LOWERS relative luminance, so an accent used as TEXT on a light card gets slightly
    /// darker (better), and <see cref="ColorContrast.PickContrast"/> keeps picking the legible ink on fills.</summary>
    public static ColorF Vivid(ColorF c, float minS = 0.55f, byte targetMax = 210)
    {
        var (h, s, v) = c.ToHsv();
        if (s <= NeutralS) return c;
        return ColorF.FromHsv(h, MathF.Max(s, minS), MathF.Max(v, targetMax / 255f), c.A);
    }

    /// <summary>THE page's chrome accent — the one derivation every accent-filled control on a media surface uses: the
    /// cover's most-saturated graded role (<see cref="Accent"/>), brightness-lifted then saturation-floored.
    /// Greyscale/near-monochrome art has no hue to amplify, so it falls back to <see cref="Tok.AccentDefault"/> rather
    /// than shipping a grey Play button.
    ///
    /// Deliberately NOT used for the hero WASHES: those keep the plain <see cref="Lift"/>ed accent, so wash strength
    /// (alpha) and chrome chroma (saturation) stay independent axes.</summary>
    public static ColorF ChromeAccent(in CoverColorPlane.Scheme s)
    {
        var lifted = Lift(Accent(s));
        var (_, sat, _) = lifted.ToHsv();
        return sat <= NeutralS ? Tok.AccentDefault : Vivid(lifted);
    }

    // The roles the app's chrome asks for, over the plane's five-role scheme. backgroundBase is the dominant tone;
    // backgroundTintedBase is the slightly-lifted band tone the page washes use.

    /// <summary>The cover's HUE — the most saturated of the graded roles, in preference order
    /// <c>backgroundTintedBase → backgroundBase → textSubdued → textBrightAccent</c> (ties keep the earlier one).
    ///
    /// Why not simply <c>textBrightAccent</c>, which its name promises: in the real <c>getDynamicColorsByUris</c>
    /// payloads that role is the CONTRAST-GRADED INK, not a hue — it is pure <c>#FFFFFF</c> in every <c>highContrast</c>
    /// dark half and pure <c>#000000</c> in every light half (verified over 9,316 cached gradings: 100%). Reading it as
    /// "the accent" therefore made <see cref="ChromeAccent"/>'s <see cref="NeutralS"/> guard fire on EVERY cover, so
    /// every Play CTA in the app rendered <see cref="Tok.AccentDefault"/> blue. The cover's actual chroma lives in the
    /// BACKGROUND roles (dark <c>backgroundTintedBase</c>: median HSV S≈0.73) and, in the light half, in
    /// <c>textSubdued</c> (median S≈0.45).
    ///
    /// <c>textBrightAccent</c> stays in the list — LAST — so a future feed that does grade it chromatically still wins
    /// wherever it is the most saturated role, without this needing to change. Genuinely greyscale art leaves every role
    /// below <see cref="NeutralS"/> and still falls through <see cref="ChromeAccent"/>'s guard to the semantic accent,
    /// so a black-and-white cover keeps the system blue instead of inventing a random tint.</summary>
    public static ColorF Accent(in CoverColorPlane.Scheme s)
    {
        // A Span collection expression: stack-allocated, so this stays allocation-free on the render path.
        Span<uint> roles = [s.BackgroundTintedBase, s.BackgroundBase, s.TextSubdued, s.TextBrightAccent];
        ColorF best = default;
        float bestS = -1f;
        for (int i = 0; i < roles.Length; i++)
        {
            var c = ToColor(roles[i]);
            var (_, sat, _) = c.ToHsv();
            if (sat > bestS) { bestS = sat; best = c; }   // strict > ⇒ a tie keeps the earlier (higher-preference) role
        }
        return best;
    }

    public static ColorF BackgroundDark(in CoverColorPlane.Scheme s) => ToColor(s.BackgroundBase);
    public static ColorF TintedDark(in CoverColorPlane.Scheme s) => ToColor(s.BackgroundTintedBase);

    /// <summary>Neutral card fill under <see cref="Surfaces.HeroWash"/> — same as the shell content card on detail pages.</summary>
    public static ColorF HeroBase(CoverColorPlane.Scheme? art) => WaveeColors.FileArea;

    /// <summary>Hero-wash accent — same derivation as <c>DetailShell</c> (lifted accent in light, the dominant tone in dark).</summary>
    public static ColorF HeroWashColor(CoverColorPlane.Scheme? art) =>
        Tok.Theme == ThemeKind.Light
            ? (art is { } p ? Lift(Accent(p)) : Tok.AccentDefault)
            : BackgroundDark(art ?? Neutral);

    /// <summary>Neutral fallback when the plane has no grading yet (no current track / not fetched). Every role is
    /// greyscale ON PURPOSE — including <c>textBrightAccent</c>, which the real wire always grades to pure white in the
    /// dark half (see <see cref="Accent"/>). A fabricated blue here made the fallback scheme the one "cover" in the app
    /// with a hue, which is precisely the wrong shape to test chrome against.</summary>
    public static CoverColorPlane.Scheme Neutral { get; } =
        new(BackgroundBase: 0xFF1C1C1C, BackgroundTintedBase: 0xFF2A2A2A, TextBase: 0xFFFFFFFF,
            TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);

    /// <summary>The player bar's neutral dark base — the surface the album hue is only faintly lifted from. WinUI's
    /// subtlety came from acrylic over a real blurred desktop; we have neither, so the bar is a flat neutral fill with a
    /// capped hue instead of a saturated tint.</summary>
    public static ColorF BarSurface { get; } = ColorF.FromRgba(0x1A, 0x1B, 0x1E, 0xFF);
}
