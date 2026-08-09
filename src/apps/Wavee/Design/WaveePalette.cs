using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.SpotifyLive;

namespace Wavee;

// The boundary mapper: the framework-neutral cover-colour roles (uint ARGB, from CoverColorPlane) → engine ColorF.
// This is the ONLY place a cover colour becomes a renderer colour. A page asks the plane for its cover's Scheme and
// maps the roles it needs here; nothing carries a per-entity palette any more.
public static class WaveePalette
{
    const float HairlineSaturationCeiling = 0.50f;
    const float HairlineContrast = 3.25f;
    const float HairlineHoverContrast = 3.55f;

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

    /// <summary>A quiet identity hairline solved against the actual card surface. Saturation is capped, then value is
    /// binary-solved to a 3.25:1 contrast ratio in the theme-appropriate direction. This is deliberately separate from
    /// <see cref="Lift"/>/<see cref="Vivid"/>: chrome fills want brightness and chroma; a 2-DIP rule does not.</summary>
    public static ColorF Hairline(ColorF seed)
        => Hairline(seed, Tok.Theme,
            ColorContrast.Flatten(Tok.FillCardDefault, WaveeColors.FloatingPane), HairlineContrast);

    /// <summary>The hovered card's slightly stronger identity cue; 3.55:1 remains below the 4.5:1 text threshold.</summary>
    public static ColorF HairlineHover(ColorF seed)
        => Hairline(seed, Tok.Theme,
            ColorContrast.Flatten(Tok.FillCardSecondary, WaveeColors.FloatingPane), HairlineHoverContrast);

    /// <summary>Pure overload used by contrast tests for both themes without mutating global theme state.</summary>
    public static ColorF Hairline(ColorF seed, ThemeKind theme, ColorF cardBackground, float targetContrast = HairlineContrast)
    {
        var (h, s, _) = seed.ToHsv();
        s = MathF.Min(s, HairlineSaturationCeiling);

        // Contrast is monotonic along V for a fixed hue/saturation. Dark cards search from black to white and retain
        // the lighter solution; light cards search the same interval and retain the darker solution.
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 22; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float ratio = ColorContrast.Ratio(ColorF.FromHsv(h, s, mid, seed.A), cardBackground);
            if (theme == ThemeKind.Dark)
            {
                if (ratio < targetContrast) lo = mid; else hi = mid;
            }
            else
            {
                // On a light card, contrast falls as V rises.
                if (ratio < targetContrast) hi = mid; else lo = mid;
            }
        }
        float v = theme == ThemeKind.Dark ? hi : lo;
        return ColorF.FromHsv(h, s, v, seed.A);
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

    // ── THE PAGE TONE ───────────────────────────────────────────────────────────────────────────────────────────
    // The detail pages' ONE art-derived surface: an opaque plane the whole page sits on, replacing the stack of
    // top-anchored alpha washes that used to tint it. Two rules, and they are the whole contract:
    //
    //   1. COMPLEMENTARY, NOT SAMPLED. The tone takes the cover's HUE and nothing else. It is not the cover's dominant
    //      colour re-painted at page scale — that is what produced pages the artwork could not be told apart from.
    //      Saturation and lightness are the page's, not the record's, so two albums by the same artist read as the
    //      same PAGE in two different colours rather than as two different apps.
    //   2. THE CLAMP IS THE POINT. Apple Music's unclamped version of this is its single loudest complaint: a
    //      saturated cover produces a page that is genuinely hard to read, and a dark cover produces one that is
    //      indistinguishable from every other dark cover's. So lightness is FORCED (never sampled) to
    //      <see cref="PageToneDarkL"/> / <see cref="PageToneLightL"/> and saturation is CAPPED at
    //      <see cref="PageToneDarkSMax"/> / <see cref="PageToneLightSMax"/>. The forced lightness is also what makes
    //      the standard Tok ink tokens correct on this plane in both themes — there is no on-media ladder on these
    //      pages, because polarity is guaranteed by construction rather than measured per cover.
    //
    // A cover with no hue worth using (greyscale art, a mosaic of monochrome tiles) gets the NEUTRAL tone instead of
    // an invented tint — the same decision <see cref="ChromeAccent"/> makes for the Play button, for the same reason.

    /// <summary>Below this HSV saturation the dominant graded role has no hue to build a page tone from.</summary>
    public const float PageToneChromaFloor = 0.12f;

    /// <summary>The forced HSL lightness of the dark tone, and the cap on its HSL saturation.</summary>
    public const float PageToneDarkL = 0.15f, PageToneDarkSMax = 0.30f;

    /// <summary>The forced HSL lightness of the light tone, and the cap on its HSL saturation.</summary>
    public const float PageToneLightL = 0.89f, PageToneLightSMax = 0.42f;

    /// <summary>The hue-less answer: a near-black in dark, a WARM off-white in light (a neutral grey page reads as
    /// "unfinished" beside every tinted one, and warm is the direction the app's light palette already leans).</summary>
    public static ColorF PageToneNeutralDark { get; } = ColorF.FromRgba(0x15, 0x15, 0x15);
    public static ColorF PageToneNeutralLight { get; } = ColorF.FromRgba(0xF6, 0xF4, 0xF1);

    /// <summary>THE detail page's ground tone for a cover's grading, or null when there is no grading to build one
    /// from (the caller then paints nothing and the page keeps its neutral surface). See the contract above.</summary>
    public static ColorF? PageTone(CoverColorPlane.Scheme? scheme, ThemeKind theme)
    {
        if (scheme is not { } s) return null;
        var dominant = Accent(s);                        // the cover's HUE role (see Accent's doc for why not textBrightAccent)
        var (_, hsvSat, _) = dominant.ToHsv();
        if (hsvSat < PageToneChromaFloor)
            return theme == ThemeKind.Dark ? PageToneNeutralDark : PageToneNeutralLight;
        var (h, sat, _) = ToHsl(dominant);
        return theme == ThemeKind.Dark
            ? FromHsl(h, MathF.Min(sat, PageToneDarkSMax), PageToneDarkL)
            : FromHsl(h, MathF.Min(sat, PageToneLightSMax), PageToneLightL);
    }

    // HSL, not HSV: the page tone's contract is stated in LIGHTNESS ("dark: L ≈ 15 %"), and HSV's V is not lightness —
    // a fully saturated hue at V=0.15 and a grey at V=0.15 have very different perceived brightness. The engine's
    // ColorF publishes HSV only, so the two conversions live here, where the one caller that needs them is.
    internal static (float H, float S, float L) ToHsl(in ColorF c)
    {
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        float min = MathF.Min(c.R, MathF.Min(c.G, c.B));
        float l = (max + min) * 0.5f;
        float d = max - min;
        if (d <= 1e-6f) return (0f, 0f, l);
        float s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        float h = max == c.R ? (c.G - c.B) / d + (c.G < c.B ? 6f : 0f)
            : max == c.G ? (c.B - c.R) / d + 2f
            : (c.R - c.G) / d + 4f;
        return (h * 60f, s, l);
    }

    internal static ColorF FromHsl(float hDeg, float s, float l, float a = 1f)
    {
        s = Math.Clamp(s, 0f, 1f);
        l = Math.Clamp(l, 0f, 1f);
        if (s <= 0f) return new ColorF(l, l, l, a);
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float h = hDeg / 360f;
        h -= MathF.Floor(h);
        return new ColorF(Hue(p, q, h + 1f / 3f), Hue(p, q, h), Hue(p, q, h - 1f / 3f), a);

        static float Hue(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
    }

    /// <summary>Neutral card fill under <see cref="Surfaces.HeroWash"/> — same as the shell content card on detail pages
    /// (now the OPAQUE content surface, so the hero base no longer depends on what shows through it).</summary>
    public static ColorF HeroBase(CoverColorPlane.Scheme? art) => WaveeColors.ContentSurface;

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
