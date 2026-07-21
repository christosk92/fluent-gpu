using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>An integer, half-open physical-pixel box used by the self-blur work planner.</summary>
public readonly record struct SelfBlurPixelBox(int MinX, int MinY, int MaxX, int MaxY)
{
    public int Width => Math.Max(0, MaxX - MinX);
    public int Height => Math.Max(0, MaxY - MinY);
    public long AreaPx => (long)Width * Height;
    public bool IsEmpty => MaxX <= MinX || MaxY <= MinY;
}

/// <summary>
/// Tight physical-pixel regions for one self blur. <see cref="VisibleOutput"/> is the halo-bearing output that can
/// survive the active clip; <see cref="RequiredSource"/> is the subset of the crisp layer that can contribute to that
/// output; <see cref="Work"/> is their union and therefore the minimum local surface that can hold both.
/// </summary>
public readonly record struct SelfBlurWorkGeometry(
    SelfBlurPixelBox VisibleOutput,
    SelfBlurPixelBox RequiredSource,
    SelfBlurPixelBox Work);

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

    /// <summary>
    /// Compute the tight work regions for a self blur in physical pixels. The layer and active
    /// <see cref="PushLayerCmd.CompositeClip"/> are expressed in the DrawList's DIP/device space and conservatively
    /// floor/ceil-scaled. A default/empty composite clip means unbounded for compatibility with manually-authored draw
    /// lists; the recorder always supplies the inherited active clip.
    ///
    /// The visible output is <c>inflate(layer, kernel halo) intersect activeClip intersect canvas</c>. Only crisp source
    /// pixels within one halo of that output can affect it, so the required source is
    /// <c>layer intersect inflate(visibleOutput, halo) intersect canvas</c>. This distinction is what lets a renderer
    /// process a partially-clipped row without clearing, drawing, or blurring the rest of the row (or the canvas).
    /// </summary>
    public static SelfBlurWorkGeometry ComputeWork(in PushLayerCmd layer, float scale, int canvasW, int canvasH)
    {
        if (!(scale > 0f) || canvasW <= 0 || canvasH <= 0 || layer.DeviceRect.IsEmpty)
            return default;

        var canvas = new SelfBlurPixelBox(0, 0, canvasW, canvasH);
        SelfBlurPixelBox layerPx = ToPixelBox(layer.DeviceRect, scale);
        int halo = TapRadius(layer.BlurSigma);

        SelfBlurPixelBox visible = Intersect(Inflate(layerPx, halo), canvas);
        RectF activeClip = layer.CompositeClip;
        if (!activeClip.IsEmpty && !activeClip.IsInfinite)
            visible = Intersect(visible, ToPixelBox(activeClip, scale));

        if (visible.IsEmpty)
            return default;

        SelfBlurPixelBox requiredSource = Intersect(Intersect(layerPx, Inflate(visible, halo)), canvas);
        SelfBlurPixelBox work = Union(visible, requiredSource);
        return new SelfBlurWorkGeometry(visible, requiredSource, work);
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

    private static SelfBlurPixelBox ToPixelBox(in RectF rect, float scale)
        => new(
            (int)MathF.Floor(rect.X * scale),
            (int)MathF.Floor(rect.Y * scale),
            (int)MathF.Ceiling(rect.Right * scale),
            (int)MathF.Ceiling(rect.Bottom * scale));

    private static SelfBlurPixelBox Inflate(in SelfBlurPixelBox box, int amount)
        => new(box.MinX - amount, box.MinY - amount, box.MaxX + amount, box.MaxY + amount);

    private static SelfBlurPixelBox Intersect(in SelfBlurPixelBox a, in SelfBlurPixelBox b)
    {
        int minX = Math.Max(a.MinX, b.MinX), minY = Math.Max(a.MinY, b.MinY);
        int maxX = Math.Min(a.MaxX, b.MaxX), maxY = Math.Min(a.MaxY, b.MaxY);
        return maxX <= minX || maxY <= minY ? default : new SelfBlurPixelBox(minX, minY, maxX, maxY);
    }

    private static SelfBlurPixelBox Union(in SelfBlurPixelBox a, in SelfBlurPixelBox b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new SelfBlurPixelBox(
            Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY),
            Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
    }
}
