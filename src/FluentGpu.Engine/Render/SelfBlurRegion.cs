using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>An integer, half-open physical-pixel box used by <see cref="SelfBlurRegion.ComputeRecordGeometry"/>.</summary>
internal readonly record struct SelfBlurPixelBox(int MinX, int MinY, int MaxX, int MaxY)
{
    public bool IsEmpty => MaxX <= MinX || MaxY <= MinY;
}

/// <summary>Recorder-side logical-space geometry for one self blur. <see cref="OutputBounds"/> is the full
/// halo-bearing output (unclipped, so span/subtree culling remains translation-safe); <see cref="VisibleOutput"/> is
/// the part that survives the active composite clip; <see cref="RequiredSource"/> is the crisp layer strip that must
/// be recorded inside the blur group to produce that visible output.</summary>
public readonly record struct SelfBlurRecordGeometry(
    RectF OutputBounds,
    RectF VisibleOutput,
    RectF RequiredSource);

/// <summary>
/// Portable geometry for the per-node SELF-blur (the Expressive Motion Kit's <c>LayerKind.Blur</c>): the kernel's
/// physical-px tap support (<see cref="TapRadius"/>) — the halo a self-blur paints past its sharp rect, which the tile
/// rasterizer, the composite's retained self-blur surface and the recorder's span bounds all size by — and the
/// recorder's visible-output / required-source geometry (<see cref="ComputeRecordGeometry"/>). TerraFX-free, so the
/// headless gates and the D3D12 leaf read one source of truth (mirroring <see cref="AcrylicBackdropMath"/>).
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

    /// <summary>The reach of the WHOLE retained-blur pipeline in physical px: how far from an output pixel a source pixel
    /// can still change it. <see cref="TapRadius"/> is the Gaussian's support; the box-downsample chain and the bilinear
    /// upsample add their own footprint — a pixel x reads texels [⌊u⌋ − R, ⌊u⌋ + 1 + R] with u = (x + ½)/down − ½,
    /// i.e. source px within <c>R·down + 1.5·down + ½</c> of it — so this is <c>TapRadius + 2·down + 1</c>. Source
    /// farther than this from every pixel the blur DRAWS cannot change a drawn pixel: the composite cuts a retained blur's
    /// source to its composite clip grown by this (<see cref="Tiles.GroupCacheKey.BlurRegions"/>).</summary>
    public static int SupportRadius(float blurSigma)
        => TapRadius(blurSigma) + 2 * AcrylicBackdropMath.DownsampleFactor(blurSigma, 1f) + 1;

    /// <summary>
    /// Compute the recorder's DIP-space visibility/source geometry from the same physical-pixel tap support used by
    /// <see cref="TapRadius"/>. Pixel boxes convert back OUTWARD by a tiny fraction of one device pixel so a
    /// DIP→px floor/ceil round-trip can never lose the first or last contributing tap at fractional DPI.
    /// </summary>
    public static SelfBlurRecordGeometry ComputeRecordGeometry(
        in RectF layerRect, in RectF compositeClip, float blurSigma, float scale)
    {
        if (!(scale > 0f) || layerRect.IsEmpty || !(blurSigma > 0f)) return default;

        SelfBlurPixelBox layerPx = ToPixelBox(layerRect, scale);
        int halo = TapRadius(blurSigma);
        SelfBlurPixelBox outputPx = Inflate(layerPx, halo);
        RectF output = FromPixelBoxOutward(outputPx, scale);

        // SceneRecorder passes an actual active clip: empty means fully clipped; Infinite is the root/unbounded case.
        if (compositeClip.IsEmpty) return new SelfBlurRecordGeometry(output, default, default);

        SelfBlurPixelBox visiblePx = compositeClip.IsInfinite
            ? outputPx
            : Intersect(outputPx, ToPixelBox(compositeClip, scale));
        if (visiblePx.IsEmpty) return new SelfBlurRecordGeometry(output, default, default);

        SelfBlurPixelBox sourcePx = Intersect(layerPx, Inflate(visiblePx, halo));
        return new SelfBlurRecordGeometry(
            output,
            FromPixelBoxOutward(visiblePx, scale),
            FromPixelBoxOutward(sourcePx, scale));
    }

    private static SelfBlurPixelBox ToPixelBox(in RectF rect, float scale)
        => new(
            (int)MathF.Floor(rect.X * scale),
            (int)MathF.Floor(rect.Y * scale),
            (int)MathF.Ceiling(rect.Right * scale),
            (int)MathF.Ceiling(rect.Bottom * scale));

    private static RectF FromPixelBoxOutward(in SelfBlurPixelBox box, float scale)
    {
        if (box.IsEmpty || !(scale > 0f)) return default;
        float epsilon = 1f / (scale * 1024f);
        float x = box.MinX / scale - epsilon;
        float y = box.MinY / scale - epsilon;
        float right = box.MaxX / scale + epsilon;
        float bottom = box.MaxY / scale + epsilon;
        return new RectF(x, y, right - x, bottom - y);
    }

    private static SelfBlurPixelBox Inflate(in SelfBlurPixelBox box, int amount)
        => new(box.MinX - amount, box.MinY - amount, box.MaxX + amount, box.MaxY + amount);

    private static SelfBlurPixelBox Intersect(in SelfBlurPixelBox a, in SelfBlurPixelBox b)
    {
        int minX = Math.Max(a.MinX, b.MinX), minY = Math.Max(a.MinY, b.MinY);
        int maxX = Math.Min(a.MaxX, b.MaxX), maxY = Math.Min(a.MaxY, b.MaxY);
        return maxX <= minX || maxY <= minY ? default : new SelfBlurPixelBox(minX, minY, maxX, maxY);
    }
}
