namespace FluentGpu.Animation;

/// <summary>
/// Deterministic mapping from a Windows DirectManipulation content transform (manual-update mode) to an engine scroll
/// position + overscroll band. Pure scalar — NO COM/TerraFX — so it is unit-testable headlessly (the §Phase-3 plan's
/// "isolate the untestable surface to the COM/CCW plumbing alone"). The Windows <c>Win32DmScrollSource</c> reads the
/// polled 2×3 content transform each frame, converts its scroll-axis translation to an offset-space displacement, and
/// feeds it here; the result is written through the SAME Input chokepoint (<c>SetScrollOffset</c>/<c>WriteOverscroll</c>),
/// so the clamp/translation-only contract is never bypassed and DM stays a SAMPLE source (not the clamp authority).
/// </summary>
public static class DmScrollMath
{
    /// <summary>Split the OS manipulation's absolute scroll-axis position into the in-range offset (clamped to
    /// <c>[0, max]</c>, written through the chokepoint) and the past-edge OVERSCROLL band (signed offset-space excess:
    /// negative past the top/left, positive past the bottom/right; 0 in range). <paramref name="baselineOffset"/> is the
    /// engine offset captured at contact-start; <paramref name="dmDisplacement"/> is the offset-space delta DM has
    /// integrated since then (already sign-corrected by the source — positive = toward the content end). DM's own
    /// translation carries the inertia and rubber-band; we re-clamp so the engine offset never leaves <c>[0, max]</c> and
    /// the band is the visual remainder, identical to the touch-pan overscroll path.</summary>
    public static (float offset, float band) Split(float baselineOffset, float dmDisplacement, float maxOffset)
    {
        float max = MathF.Max(0f, maxOffset);
        float raw = baselineOffset + dmDisplacement;
        float clamped = MathF.Max(0f, MathF.Min(raw, max));
        float band = raw - clamped;   // signed past-edge excess (0 when in range)
        return (clamped, band);
    }

    /// <summary>The offset-space scroll displacement for a DM content transform's scroll-axis translation. DM reports a
    /// 2×3 affine <c>[m11 m12 m21 m22 m31 m32]</c> whose translation is <c>(m31, m32)</c> in CONTENT px: as the content
    /// pans toward its end the translation goes NEGATIVE, so the engine offset (which INCREASES toward the content end)
    /// is the negated translation. <paramref name="horizontal"/> selects m31 vs m32.</summary>
    public static float DisplacementFromTransform(System.ReadOnlySpan<float> transform2x3, bool horizontal)
    {
        if (transform2x3.Length < 6) return 0f;
        float translate = horizontal ? transform2x3[4] : transform2x3[5];
        return -translate;
    }
}
