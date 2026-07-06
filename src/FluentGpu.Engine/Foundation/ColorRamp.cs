namespace FluentGpu.Foundation;

/// <summary>HSL-based neutral ramp for seed-derived theme palettes. Build-time only — not on the hot path.</summary>
public static class ColorRamp
{
    /// <summary>A tinted neutral at sRGB lightness <paramref name="lightness01"/> (0..1), hue degrees, and chroma (0..0.12).</summary>
    public static ColorF Neutral(float lightness01, float hueDeg, float chroma, byte alpha = 255)
    {
        float l = Math.Clamp(lightness01, 0f, 1f);
        float s = Math.Clamp(chroma, 0f, 0.12f) * (1f - MathF.Abs(2f * l - 1f)) * 4f;   // soften at extremes
        return FromHsl(hueDeg, s, l, alpha);
    }

    /// <summary>A tinted neutral like <see cref="Neutral"/> but WITHOUT the extreme-lightness chroma softening — the
    /// saturation goes straight into HSL. Use where the tint must survive at high lightness (preset-tinted chrome
    /// over Mica): <see cref="Neutral"/>'s softening crushes 0.03 chroma to ~2/255 at L≈0.9, which is sub-perceptual.</summary>
    public static ColorF Tinted(float lightness01, float hueDeg, float sat, byte alpha = 255)
        => FromHsl(hueDeg, Math.Clamp(sat, 0f, 1f), Math.Clamp(lightness01, 0f, 1f), alpha);

    /// <summary>Darken <paramref name="c"/> by mixing toward black by <paramref name="amount"/> (0..1).</summary>
    public static ColorF Darken(in ColorF c, float amount)
        => ColorF.Lerp(c, ColorF.FromRgba(0, 0, 0, (byte)(c.A * 255f)), amount);

    /// <summary>Lighten <paramref name="c"/> by mixing toward white by <paramref name="amount"/> (0..1).</summary>
    public static ColorF Lighten(in ColorF c, float amount)
        => ColorF.Lerp(c, ColorF.FromRgba(255, 255, 255, (byte)(c.A * 255f)), amount);

    public static ColorF FromHsl(float hDeg, float s, float l, byte alpha = 255)
    {
        float h = (hDeg % 360f + 360f) % 360f / 60f;
        float c = (1f - MathF.Abs(2f * l - 1f)) * s;
        float x = c * (1f - MathF.Abs(h % 2f - 1f));
        float m = l - c * 0.5f;
        float r, g, b;
        int hi = (int)h;
        (r, g, b) = hi switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new ColorF(r + m, g + m, b + m, alpha / 255f);
    }

    /// <summary>Approximate hue (degrees) of an sRGB color.</summary>
    public static float HueDegrees(in ColorF c)
    {
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B)), min = MathF.Min(c.R, MathF.Min(c.G, c.B));
        float d = max - min;
        if (d <= 0.0001f) return 0f;
        float h = max == c.R ? (c.G - c.B) / d + (c.G < c.B ? 6f : 0f)
            : max == c.G ? (c.B - c.R) / d + 2f
            : (c.R - c.G) / d + 4f;
        return h * 60f;
    }
}
