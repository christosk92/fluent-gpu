using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Portable geometry for the per-node SELF-blur (the Expressive Motion Kit's <c>LayerKind.Blur</c>) run by the D3D12
/// <c>OpacityLayerCompositor</c> — the physical-px box a self-blur writes and whether it is clamped by the canvas edge.
/// Extracted here (TerraFX-free) so the region math is the ONE source of truth for both the leaf and the headless
/// VerticalSlice (mirroring how <see cref="AcrylicBackdropMath"/> serves the acrylic leaf); the leaf's
/// <c>RegionBox</c>/<c>RegionIsClamped</c> delegate to these.
///
/// The box is the layer's device rect (DIP × frame scale) inflated by the kernel's tap halo — the ACTUAL blur support
/// of the downsample-then-separable-Gaussian schedule (<see cref="AcrylicBackdropMath"/>): for a self-blur σ the leaf
/// downsamples by <c>down = DownsampleFactor(σ)</c> and blurs at <c>texelSigma = σ/down ≤ 4</c>, so the physical-px
/// support is <c>KernelRadiusTexels(texelSigma) · down</c> (≈ 3σ, never the old hardcoded 32 px cap which TRUNCATED the
/// Gaussian at large σ). At σ ≤ 4 (down = 1, the exact full-res path) this is <c>ceil(3σ) ≤ 12</c> px — identical to the
/// old un-capped value. Then clamped to the canvas — so it matches the blur pass's scissor exactly and, therefore, the
/// size of the cross-frame blur pin the compositor copies out of it. The pin cache keys off a POSITION-INDEPENDENT
/// content hash (<see cref="BlurPinKey"/>), so a stationary edge-clamped node produces a byte-identical hash AND a
/// byte-identical clamped region box two frames running ⇒ the compositor's size-exact <c>FindPin</c> HITS it (no
/// re-blur). A ≥1-device-px move that shifts the floor/ceil clamp by a pixel changes the box size ⇒ a size MISS
/// (re-blur the exact region), never a stretched/squished pin — the same size-exact safety the on-canvas pins rely on.
/// </summary>
public static class SelfBlurRegion
{
    /// <summary>The kernel's tap radius (blur support) in physical px = <c>KernelRadiusTexels(σ/down) · down</c> where
    /// <c>down = DownsampleFactor(σ)</c> — the EXACT support of the downsample-then-separable-Gaussian schedule
    /// (<see cref="AcrylicBackdropMath"/>), NOT a hardcoded 32 px cap. At σ ≤ 4 (down = 1) this is <c>ceil(3σ) ≤ 12</c>
    /// (the un-capped full-res value); at large σ it is the true ≈ 3σ reach (e.g. σ26 ⇒ down 8, texelσ 3.25, radius 10
    /// texels ⇒ 80 px — where the old cap truncated at 32 px, blurring only ~1.2σ). The self-blur σ is already physical
    /// px (it does not scale-multiply), so <c>DownsampleFactor(σ, 1)</c> reads σ as sigmaPhys directly.</summary>
    public static int TapRadius(float blurSigma)
    {
        int down = AcrylicBackdropMath.DownsampleFactor(blurSigma, 1f);
        float texelSigma = AcrylicBackdropMath.EffectiveTexelSigma(blurSigma, 1f, down);
        return AcrylicBackdropMath.KernelRadiusTexels(texelSigma) * down;
    }

    /// <summary>The physical-px box a self-blur actually writes: <c>DeviceRect × scale</c> inflated by
    /// <see cref="TapRadius"/> on every side, clamped to <c>[0,canvasW] × [0,canvasH]</c>.</summary>
    public static void RegionBox(in PushLayerCmd L, float scale, int canvasW, int canvasH,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        int r = TapRadius(L.BlurSigma);
        minX = Math.Max(0, (int)MathF.Floor(L.DeviceRect.X * scale) - r);
        minY = Math.Max(0, (int)MathF.Floor(L.DeviceRect.Y * scale) - r);
        maxX = Math.Min(canvasW, (int)MathF.Ceiling((L.DeviceRect.X + L.DeviceRect.W) * scale) + r);
        maxY = Math.Min(canvasH, (int)MathF.Ceiling((L.DeviceRect.Y + L.DeviceRect.H) * scale) + r);
    }

    /// <summary>True iff the halo-inflated region is clamped by a CANVAS edge — i.e. the UNCLAMPED box would poke
    /// outside <c>[0,canvasW] × [0,canvasH]</c>. A clamped region captures only a PARTIAL strip, so its pin is smaller
    /// than the full on-canvas strip; the compositor's size-exact <c>FindPin</c> keeps the two distinct (a clamped pin
    /// serves only while clamped the same way, the full pin serves only on-canvas), which is why a clamped region can
    /// safely HIT its own size-matched pin but a mint mid-motion (a per-frame-changing clamp size) is churny.</summary>
    public static bool IsClamped(in PushLayerCmd L, float scale, int canvasW, int canvasH)
    {
        int r = TapRadius(L.BlurSigma);
        return (int)MathF.Floor(L.DeviceRect.X * scale) - r < 0
            || (int)MathF.Floor(L.DeviceRect.Y * scale) - r < 0
            || (int)MathF.Ceiling((L.DeviceRect.X + L.DeviceRect.W) * scale) + r > canvasW
            || (int)MathF.Ceiling((L.DeviceRect.Y + L.DeviceRect.H) * scale) + r > canvasH;
    }
}
