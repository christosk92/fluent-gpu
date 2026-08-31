namespace FluentGpu.Foundation;

/// <summary>
/// The browser-style application-zoom ladder (Chromium's step set). Zoom is deliberately DISCRETE, not continuous:
/// the glyph-atlas raster keys and baked path-geometry keys quantize the effective scale at ×100 (two decimal
/// places), so a free-form zoom slider would alias distinct zoom values onto one raster bucket — or worse, churn
/// the atlas on every pixel of a drag. A fixed ladder of well-spaced steps (the same 50%…250% ramp Chromium ships
/// for Ctrl+±) keeps every step a distinct, cache-friendly raster key and matches the muscle memory users already
/// have. The effective window scale is <c>OS DPI scale × zoom</c> (<see cref="FluentGpu.Pal.IPlatformWindow.Scale"/>);
/// everything downstream (layout DIP viewport, glyph raster, damage, popups, IME, input DIP conversion) consumes
/// that one product. All members are allocation-free.
/// </summary>
public static class ZoomLadder
{
    /// <summary>The Ctrl+± step ladder, ascending (Chromium's zoom factors). <see cref="In"/>/<see cref="Out"/> walk
    /// it; <see cref="Snap"/> quantizes onto it. 1f (100%) sits at index 5.</summary>
    public static readonly float[] Steps =
        [0.5f, 0.67f, 0.75f, 0.8f, 0.9f, 1f, 1.1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f];

    /// <summary>Hard lower bound for any zoom value (beyond the ladder — a persisted/programmatic floor).</summary>
    public const float Min = 0.25f;

    /// <summary>Hard upper bound for any zoom value (beyond the ladder — a persisted/programmatic ceiling).</summary>
    public const float Max = 5f;

    /// <summary>The neutral zoom (100% — no app zoom; effective scale = the OS DPI scale alone).</summary>
    public const float Default = 1f;

    // Step comparisons tolerate float noise from persisted values (0.6699999f must still count as the 0.67 step).
    private const float Epsilon = 0.001f;

    /// <summary>Sanitize an arbitrary zoom value: a finite, positive input clamps to [<see cref="Min"/>, <see cref="Max"/>];
    /// anything else (NaN, ±∞, zero, negative — a corrupt persisted value) falls back to <see cref="Default"/>.</summary>
    public static float Clamp(float z)
    {
        if (!float.IsFinite(z) || z <= 0f) return Default;
        return z < Min ? Min : z > Max ? Max : z;
    }

    /// <summary>Zoom IN one step: the first ladder step strictly greater than <paramref name="z"/> (with epsilon, so a
    /// value sitting ON a step advances past it). Saturates at the top step.</summary>
    public static float In(float z)
    {
        z = Clamp(z);
        for (int i = 0; i < Steps.Length; i++)
            if (Steps[i] > z + Epsilon) return Steps[i];
        return Steps[^1];
    }

    /// <summary>Zoom OUT one step: the last ladder step strictly smaller than <paramref name="z"/> (with epsilon, so a
    /// value sitting ON a step retreats past it). Saturates at the bottom step.</summary>
    public static float Out(float z)
    {
        z = Clamp(z);
        for (int i = Steps.Length - 1; i >= 0; i--)
            if (Steps[i] < z - Epsilon) return Steps[i];
        return Steps[0];
    }

    /// <summary>The zoom as a whole percent for UI display (e.g. 0.67f → 67).</summary>
    public static int Percent(float z) => (int)MathF.Round(Clamp(z) * 100f);

    /// <summary>Quantize onto the nearest ladder step — persisted-value hygiene: a stored zoom that drifted off the
    /// ladder (an old ladder, float noise, hand-edited settings) re-enters it here so <see cref="In"/>/<see cref="Out"/>
    /// step cleanly instead of landing between rungs.</summary>
    public static float Snap(float z)
    {
        z = Clamp(z);
        float best = Steps[0];
        float bestDist = MathF.Abs(z - best);
        for (int i = 1; i < Steps.Length; i++)
        {
            float d = MathF.Abs(z - Steps[i]);
            if (d < bestDist) { bestDist = d; best = Steps[i]; }
        }
        return best;
    }
}
