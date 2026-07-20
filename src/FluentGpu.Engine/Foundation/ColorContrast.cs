namespace FluentGpu.Foundation;

/// <summary>WCAG 2.x relative luminance and contrast helpers for palette build-time verification.</summary>
public static class ColorContrast
{
    public static float RelativeLuminance(in ColorF c)
    {
        static float Lin(float ch) => ch <= 0.04045f ? ch / 12.92f : MathF.Pow((ch + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    }

    /// <summary>Contrast ratio (L1+0.05)/(L2+0.05) with L1 ≥ L2.</summary>
    public static float Ratio(in ColorF fg, in ColorF bg)
    {
        float a = RelativeLuminance(fg), b = RelativeLuminance(bg);
        if (a < b) (a, b) = (b, a);
        return (a + 0.05f) / (b + 0.05f);
    }

    public static bool MeetsAaText(in ColorF fg, in ColorF bg, float min = 4.5f) => Ratio(fg, bg) >= min;

    /// <summary>The near-black ink (#161616) the on-accent picker uses as the dark candidate (WinUI TextOnAccent's dark
    /// stop; matches the accent-legibility picker Wavee proved on cover-extracted accents).</summary>
    public static readonly ColorF NearBlackInk = ColorF.FromRgba(0x16, 0x16, 0x16);
    private static readonly ColorF WhiteInk = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

    /// <summary>Pick whichever of <paramref name="darkInk"/> / <paramref name="lightInk"/> reads with the higher WCAG
    /// contrast on <paramref name="bg"/> — the legible foreground for text/icons sitting ON a solid fill, chosen by the
    /// fill's luminance, NOT the theme. (A cover-extracted / custom accent can land anywhere from a near-white grey to a
    /// saturated mid-tone, where a theme-fixed on-accent color fails contrast.) Pure; no allocation.</summary>
    public static ColorF PickContrast(in ColorF bg, in ColorF darkInk, in ColorF lightInk)
        => Ratio(darkInk, bg) >= Ratio(lightInk, bg) ? darkInk : lightInk;

    /// <summary>Pick the legible ink for a solid <paramref name="bg"/> fill from the default pair — near-black
    /// (<see cref="NearBlackInk"/>) vs white.</summary>
    public static ColorF PickContrast(in ColorF bg) => PickContrast(bg, NearBlackInk, WhiteInk);

    /// <summary>Relative luminance delta as a fraction of the lighter surface (for adjacent-layer checks).</summary>
    public static float LuminanceDelta(in ColorF a, in ColorF b)
    {
        float la = RelativeLuminance(a), lb = RelativeLuminance(b);
        float hi = MathF.Max(la, lb), lo = MathF.Min(la, lb);
        return hi <= 0f ? 0f : (hi - lo) / hi;
    }

    /// <summary>Alpha-composite <paramref name="top"/> (straight alpha) over an opaque <paramref name="under"/>.
    /// What a translucent shell surface actually reads as once DWM puts Mica beneath it — used to solve palette
    /// anchors and to make the contrast/ladder gates evaluate composited reality instead of raw token values.</summary>
    public static ColorF Flatten(in ColorF top, in ColorF under)
    {
        float a = top.A;
        return new ColorF(
            top.R * a + under.R * (1f - a),
            top.G * a + under.G * (1f - a),
            top.B * a + under.B * (1f - a),
            1f);
    }
}

/// <summary>Reference DWM Mica backdrop tones for palette solving and build-time gates. Mica is wallpaper-tinted, so
/// these are calibration anchors, not truths: <c>*Default</c> is the typical BaseAlt tone the shell anchors are solved
/// against; <c>*Bright</c>/<c>*Dim</c> bound the assumed ±0x14/channel wallpaper swing the gates check worst-case.</summary>
public static class MicaRef
{
    public static readonly ColorF LightDefault = ColorF.FromRgba(0xED, 0xED, 0xED);
    public static readonly ColorF LightBright  = ColorF.FromRgba(0xF9, 0xF9, 0xF9);
    public static readonly ColorF LightDim     = ColorF.FromRgba(0xD9, 0xD9, 0xD9);
    public static readonly ColorF DarkDefault  = ColorF.FromRgba(0x20, 0x20, 0x20);
    public static readonly ColorF DarkBright   = ColorF.FromRgba(0x2E, 0x2E, 0x2E);
    public static readonly ColorF DarkDim      = ColorF.FromRgba(0x17, 0x17, 0x17);
}
