using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Portable math for the in-app acrylic effect runner (design/subsystems/backdrop-effects-animation.md §2.3 two-pass
/// PushLayer{Acrylic} schedule + §3 IEffectRunner seam, gpu-renderer.md §7 LayerPool). The GPU leaf
/// (Rhi.D3D12 <c>AcrylicCompositor</c>) consumes these values so the region / size-bucket / kernel logic is
/// headless-verifiable in the VerticalSlice while the COM/HLSL stays render-thread-confined in the leaf.
///
/// WinUI ground truth: microsoft-ui-xaml AcrylicBrush.h:64 <c>sc_blurRadius = 30.0f</c>, applied as Composition
/// <c>GaussianBlurEffect.BlurAmount</c> (= the gaussian STANDARD DEVIATION, in DIPs) over the backdrop resolved onto
/// the opaque FallbackColor (AcrylicBrush.cpp:500-528). The runner reproduces sigma = BlurSigma·dpiScale physical px
/// with the Flutter-Impeller / Skia <c>downsample-then-separable-Gaussian</c> schedule: choose the snapshot downsample
/// factor so the intermediate's EFFECTIVE texel sigma is ≤ <see cref="MaxEffectiveTexelSigma"/> (4) — the
/// production-validated quality threshold — snapped UP to a power of two:
/// <c>down = pow2up(ceil(sigmaPhys / 4))</c> clamped [1,16] (Skia: <c>scale = 4/sigma</c> pow2-snapped, floored 1/16 —
/// the same curve from the other direction). Blurring the 1/down-resolution snapshot with a kernel rebuilt for
/// <c>texelSigma = sigmaPhys / down</c> (≤ 4, exact — never clamped when smaller) yields the requested full-resolution
/// sigma. Because texelSigma is now VARIABLE the kernel is no longer baked static: the leaf recomputes the bilinear
/// taps per (sigma,down) bucket on the CPU (<see cref="BuildKernel"/>) and uploads them as blur-pass constants, so the
/// weights the GPU samples still come from this one headless-checked source (no HLSL drift).
/// </summary>
public static class AcrylicBackdropMath
{
    /// <summary>Cap on the intermediate's effective texel sigma (Flutter-Impeller / Skia quality threshold): the
    /// downsample factor is chosen so <c>sigmaPhys/down ≤ 4</c>. Above this the separable Gaussian on the downsampled
    /// snapshot stops being visually distinguishable from a full-resolution blur, so extra intermediate resolution is
    /// pure bandwidth waste — the whole point of the downsample curve.</summary>
    public const float MaxEffectiveTexelSigma = 4f;

    /// <summary>Max snapshot downsample divisor (Skia's 1/16 scale floor): beyond /16 the bilinear down/up-sample
    /// artifacts outweigh any bandwidth saving.</summary>
    public const int MaxDownsample = 16;

    /// <summary>Max discrete kernel radius in downsampled texels: <c>ceil(3 · MaxEffectiveTexelSigma) = 12</c> (≈3σ
    /// support; the tail beyond carries &lt;0.4% weight). Bounds the per-pass tap count and the CPU tap buffers.</summary>
    public const int MaxKernelRadius = 12;

    /// <summary>Max bilinear taps per pass: center + <c>ceil(MaxKernelRadius/2) = 6</c> folded pairs = 7 (down from the
    /// old fixed 12 — the ≤4 texel sigma needs a narrower kernel, so each pass is also cheaper).</summary>
    public const int MaxTapCount = 1 + (MaxKernelRadius + 1) / 2;

    /// <summary>The physical blur sigma the schedule reproduces for a WinUI BlurAmount (DIP) at a DPI scale — clamped
    /// to ≥1 px and scale to ≥0.25 (the same guards <see cref="DownsampleFactor"/> uses).</summary>
    public static float PhysicalSigma(float blurSigmaDip, float scale) => MathF.Max(1f, blurSigmaDip * MathF.Max(0.25f, scale));

    /// <summary>
    /// Snapshot downsample divisor for a requested blur sigma (in DIPs — WinUI BlurAmount semantics) at a DPI scale:
    /// the smallest power of two ≥ <c>ceil(sigmaPhys / <see cref="MaxEffectiveTexelSigma"/>)</c>, clamped [1,16]
    /// (Flutter-Impeller / Skia). At sigmaPhys ≤ 4 this is 1 (no downsample) so small blurs stay full-resolution and
    /// exact.
    /// </summary>
    public static int DownsampleFactor(float blurSigmaDip, float scale)
    {
        float sigmaPhys = PhysicalSigma(blurSigmaDip, scale);
        int d = (int)MathF.Ceiling(sigmaPhys / MaxEffectiveTexelSigma);
        // smallest power of two ≥ d (d ≥ 1)
        int p = 1;
        while (p < d) p <<= 1;
        return Math.Clamp(p, 1, MaxDownsample);
    }

    /// <summary>The intermediate's EFFECTIVE texel sigma for a chosen downsample factor: <c>sigmaPhys / down</c>, EXACT
    /// (≤ 4 by construction, and NOT clamped up to 4 when smaller — a σ=8 phys blur at down=2 stays texelSigma 4, a
    /// σ=6 stays 3). This is the sigma the kernel is built for.</summary>
    public static float EffectiveTexelSigma(float blurSigmaDip, float scale, int down)
        => PhysicalSigma(blurSigmaDip, scale) / MathF.Max(1, down);

    /// <summary>Discrete kernel radius in downsampled texels for a texel sigma: <c>ceil(3·texelSigma)</c> (≈3σ support),
    /// clamped [1, <see cref="MaxKernelRadius"/>]. The snapshot pad is this · down (the exact kernel support), and the
    /// per-pass tap count is <c>1 + ceil(radius/2)</c>.</summary>
    public static int KernelRadiusTexels(float texelSigma)
        => Math.Clamp((int)MathF.Ceiling(3f * MathF.Max(1e-3f, texelSigma)), 1, MaxKernelRadius);

    /// <summary>Build the per-pass bilinear-optimized gaussian taps for a given texel sigma into caller buffers (each ≥
    /// <see cref="MaxTapCount"/>), returning the tap count. Index 0 is the center; indices 1.. are applied at ±offset,
    /// so the total mass is <c>w[0] + 2·Σ w[1..]</c> == 1. Same fold as the old fixed kernel — texel pairs
    /// (1,2),(3,4),… collapse into one bilinear fetch at the weight-interpolated offset — but the radius is now variable
    /// (≤ <see cref="MaxKernelRadius"/>), so a narrower ≤4-sigma kernel emits fewer taps. Zero heap allocation
    /// (stackalloc only) → safe on the render-thread record path.</summary>
    public static int BuildKernel(float texelSigma, Span<float> offsets, Span<float> weights)
    {
        texelSigma = MathF.Max(1e-3f, texelSigma);
        int radius = KernelRadiusTexels(texelSigma);
        // Discrete gaussian σ = texelSigma over [-radius..radius], normalized to sum 1.
        Span<double> w = stackalloc double[MaxKernelRadius + 1];
        double sum = 0;
        for (int i = 0; i <= radius; i++)
        {
            w[i] = Math.Exp(-(double)i * i / (2.0 * texelSigma * texelSigma));
            sum += i == 0 ? w[i] : 2.0 * w[i];
        }
        for (int i = 0; i <= radius; i++) w[i] /= sum;

        // Fold texel pairs (1,2),(3,4),… into single bilinear taps; a trailing ODD texel (b > radius) folds alone.
        offsets[0] = 0f;
        weights[0] = (float)w[0];
        int t = 1;
        for (int a = 1; a <= radius; a += 2, t++)
        {
            int b = a + 1;
            double wa = w[a], wb = b <= radius ? w[b] : 0.0;
            double wp = wa + wb;
            offsets[t] = (float)((a * wa + b * wb) / wp);
            weights[t] = (float)wp;
        }
        return t;   // 1 + ceil(radius/2)
    }

    /// <summary>
    /// The backdrop region to snapshot beneath a layer rect, in physical px clamped to the canvas: the rect inflated
    /// on every side by the FULL blur support (<paramref name="kernelRadiusTexels"/>·down physical px ≈ 3·sigmaPhys),
    /// so every blurred texel under the rect samples REAL backdrop instead of a clamp-streaked edge (WinUI blurs the
    /// whole backdrop; inflating by the kernel support makes the region blur bit-identical inside the rect). The pad is
    /// derived from the actual kernel the leaf built (<see cref="KernelRadiusTexels"/>), not a fixed constant.
    /// </summary>
    public static void SnapshotRegion(in RectF deviceRectDip, float scale, int down, int kernelRadiusTexels, int canvasW, int canvasH,
        out int x, out int y, out int w, out int h)
    {
        int pad = kernelRadiusTexels * down;
        int x0 = (int)MathF.Floor(deviceRectDip.X * scale) - pad;
        int y0 = (int)MathF.Floor(deviceRectDip.Y * scale) - pad;
        int x1 = (int)MathF.Ceiling((deviceRectDip.X + deviceRectDip.W) * scale) + pad;
        int y1 = (int)MathF.Ceiling((deviceRectDip.Y + deviceRectDip.H) * scale) + pad;
        x0 = Math.Clamp(x0, 0, Math.Max(1, canvasW)); y0 = Math.Clamp(y0, 0, Math.Max(1, canvasH));
        x1 = Math.Clamp(x1, x0, Math.Max(1, canvasW)); y1 = Math.Clamp(y1, y0, Math.Max(1, canvasH));
        x = x0; y = y0; w = Math.Max(1, x1 - x0); h = Math.Max(1, y1 - y0);
    }

    /// <summary>Slack (physical px) added on every side of the un-inflated layer rect for the CACHE-REUSE damage test
    /// (design §2.3 / E8) — a small tolerance so a change hugging the rect edge still invalidates, without pulling in the
    /// whole ±KernelRadius·down blur halo.</summary>
    public const int DamageTestMarginPx = 8;

    /// <summary>The TIGHT damage-test region for a layer (design §2.3 / E8): the layer rect in physical px WITHOUT the
    /// kernel-support inflation, expanded by <see cref="DamageTestMarginPx"/> on every side and clamped to the canvas.
    /// <see cref="BackdropReusable"/> tests this frame's damage against THIS region (not the inflated
    /// <see cref="SnapshotRegion"/>), so a node animating in the ±(kernelRadiusTexels·down ≈ 3·sigmaPhys) halo but
    /// outside rect+8 no longer forces a per-frame re-blur; a cache MISS still snapshots/blurs the inflated region
    /// (edge fidelity unchanged).</summary>
    public static void SnapshotRegionTight(in RectF deviceRectDip, float scale, int canvasW, int canvasH,
        out int x, out int y, out int w, out int h)
    {
        int m = DamageTestMarginPx;
        int x0 = (int)MathF.Floor(deviceRectDip.X * scale) - m;
        int y0 = (int)MathF.Floor(deviceRectDip.Y * scale) - m;
        int x1 = (int)MathF.Ceiling((deviceRectDip.X + deviceRectDip.W) * scale) + m;
        int y1 = (int)MathF.Ceiling((deviceRectDip.Y + deviceRectDip.H) * scale) + m;
        x0 = Math.Clamp(x0, 0, Math.Max(1, canvasW)); y0 = Math.Clamp(y0, 0, Math.Max(1, canvasH));
        x1 = Math.Clamp(x1, x0, Math.Max(1, canvasW)); y1 = Math.Clamp(y1, y0, Math.Max(1, canvasH));
        x = x0; y = y0; w = Math.Max(1, x1 - x0); h = Math.Max(1, y1 - y0);
    }

    /// <summary>
    /// LayerPool size bucket (gpu-renderer.md §7.1: "pooled RT textures keyed by quantized power-of-two-ish size
    /// buckets"): next power of two ≥ px, floor 64 — few distinct buckets ⇒ high cross-frame/cross-layer reuse, so
    /// steady state acquires from the free list and never creates a texture.
    /// </summary>
    public static int BucketDim(int px)
    {
        int b = 64;
        while (b < px) b <<= 1;
        return b;
    }

    /// <summary>The determinants of a layer's blurred backdrop: the inputs to the snapshot+blur passes. Two frames whose
    /// stamps are equal produce a bit-identical blurred snapshot, so the compositor can REUSE a retained RT instead of
    /// re-running passes A/B/C (design/subsystems/backdrop-effects-animation.md §2.3 "snapshot taken once on the
    /// overlay's PushLayer"). The rect + scale are QUANTIZED into the KEY before compare (see <see cref="Stamp"/>): the
    /// values come from the same layout/DPI/token every frame, but a presence-spring settle's sub-pixel jitter or a 1-ULP
    /// fractional-DPI wobble would otherwise trip a bit-exact float compare and re-blur permanently every frame. Snapping
    /// the rect to the integer device grid (the biased round of <c>BlurPinKey.RoundGrid</c>) and bucketing the scale to
    /// 1/1024 collapses that wobble, while any real ≥1-device-px move / resize / sigma / source / clip change still trips
    /// the compare. The COMPOSITE stays exact — it positions from <c>L.DeviceRect</c>, not the stamp.</summary>
    public readonly record struct BackdropStamp(
        float RectX, float RectY, float RectW, float RectH, float Sigma, float Scale, int CanvasW, int CanvasH,
        ulong SourceId, int ClipLeft, int ClipTop, int ClipRight, int ClipBottom);

    // Deterministic biased grid-round, IDENTICAL to BlurPinKey.RoundGrid (the 1-ULP straddle rationale applies verbatim):
    // a layout value at exactly N.5 straddles the integer boundary by ~1 ULP across ease frames under half-to-even
    // MathF.Round, flipping the stamp every other frame ⇒ a sustained per-frame re-blur. The +1/512 bias rounds a hair
    // above .5 so a wobble below 1/512 never crosses.
    private static float RoundGrid(float x) => MathF.Floor(x + 0.5f + (1f / 512f));

    /// <summary>Build the stamp for a layer rect (logical DIP) at the current scale + canvas size. The rect is snapped to
    /// the integer DEVICE grid (left/top/right/bottom independently) and the scale bucketed to 1/1024 in the CACHE KEY
    /// ONLY — the composite still positions from the exact <c>L.DeviceRect</c> — so sub-pixel jitter can never force a
    /// permanent per-frame re-blur (see <see cref="BackdropStamp"/>). Store cached stamps post-quantize so compares stay
    /// homogeneous.</summary>
    public static BackdropStamp Stamp(in RectF deviceRectDip, float sigma, float scale, int canvasW, int canvasH,
        ulong sourceId = 0, int clipLeft = 0, int clipTop = 0, int clipRight = 0, int clipBottom = 0)
    {
        float qx0 = RoundGrid(deviceRectDip.X * scale);
        float qy0 = RoundGrid(deviceRectDip.Y * scale);
        float qx1 = RoundGrid((deviceRectDip.X + deviceRectDip.W) * scale);
        float qy1 = RoundGrid((deviceRectDip.Y + deviceRectDip.H) * scale);
        float qScale = MathF.Round(scale * 1024f) / 1024f;
        return new(qx0, qy0, qx1 - qx0, qy1 - qy0, sigma, qScale, canvasW, canvasH,
            sourceId, clipLeft, clipTop, clipRight, clipBottom);
    }

    /// <summary>Can a retained blurred backdrop be reused this frame? True IFF the layer's determinants are unchanged
    /// (<paramref name="cached"/> == <paramref name="now"/>) AND nothing behind it changed — i.e. this frame's damage
    /// region (physical px; the union of changed nodes' device bounds, see SceneRecorder) does NOT overlap the layer's
    /// <see cref="SnapshotRegionTight"/> damage-test region. An empty damage rect (W/H ≤ 0 ⇒ nothing changed) reuses
    /// unconditionally. This is the region-aware "behind-region in the damage set" gate of §2.3, sampled headlessly by
    /// the VerticalSlice. The test region is the TIGHT (un-inflated) rect+<see cref="DamageTestMarginPx"/>, NOT the
    /// kernel-INFLATED snapshot region — a node animating anywhere in the ±(kernelRadiusTexels·down) halo but outside
    /// rect+8 no longer forces a re-blur every frame (misses still snapshot/blur the inflated region, so edge fidelity
    /// is intact).</summary>
    public static bool BackdropReusable(in BackdropStamp cached, in BackdropStamp now, in RectF damageTestRegionPhys, in RectF damagePhys)
    {
        if (!cached.Equals(now)) return false;                       // geometry / sigma / scale / canvas changed → re-blur
        if (damagePhys.W <= 0f || damagePhys.H <= 0f) return true;   // nothing changed behind the overlay → reuse
        return !damageTestRegionPhys.Overlaps(damagePhys);          // re-blur only when the change touches THIS region
    }

    /// <summary>Union the frame's damage ENTRIES that are OUTSIDE a layer's own subtree (own-subtree damage carve-out,
    /// design §2.3 / E9): damage emitted between an acrylic layer's PushLayer and PopLayer (the contiguous entry range
    /// [<paramref name="ownStart"/>, <paramref name="ownEnd"/>)) is drawn ON TOP OF that layer's snapshot — it was
    /// captured BEFORE the layer's PushLayer — so it can NEVER affect the snapshot and is always ignorable for that
    /// layer's reuse test. Genuine ≥1-device-px moves / resizes of the layer itself are caught by the quantized stamp
    /// (<see cref="Stamp"/>). On <paramref name="overflow"/> (more entries than the recorder's fixed pool) fall back to
    /// the whole-frame union — no carve-out, the current safe behavior.</summary>
    public static RectF ExternalDamageUnion(ReadOnlySpan<RectF> entries, int count, int ownStart, int ownEnd, bool overflow, in RectF fullUnion)
    {
        if (overflow) return fullUnion;
        RectF acc = default; bool has = false;
        int n = Math.Min(count, entries.Length);
        for (int i = 0; i < n; i++)
        {
            if (i >= ownStart && i < ownEnd) continue;              // own subtree — cannot damage this layer's snapshot
            RectF r = entries[i];
            if (r.W <= 0f || r.H <= 0f) continue;
            if (!has) { acc = r; has = true; continue; }
            float x0 = MathF.Min(acc.X, r.X), y0 = MathF.Min(acc.Y, r.Y);
            float x1 = MathF.Max(acc.X + acc.W, r.X + r.W), y1 = MathF.Max(acc.Y + acc.H, r.Y + r.H);
            acc = new RectF(x0, y0, x1 - x0, y1 - y0);
        }
        return has ? acc : default;
    }

    /// <summary>Own-subtree-aware reuse predicate (E9), portable + headless-gated: reuse IFF the stamp is unchanged AND
    /// no damage entry OUTSIDE the layer's own subtree overlaps its tight damage-test region. The pipeline precomputes
    /// <see cref="ExternalDamageUnion"/> at record time and passes it to <see cref="BackdropReusable"/> as the damage
    /// rect; this entry point folds the two so the exclusion logic has one source of truth for the gate.</summary>
    public static bool OwnSubtreeReusable(in BackdropStamp cached, in BackdropStamp now, in RectF damageTestRegionPhys,
        ReadOnlySpan<RectF> entries, int count, int ownStart, int ownEnd, bool overflow, in RectF fullUnion)
    {
        RectF external = ExternalDamageUnion(entries, count, ownStart, ownEnd, overflow, in fullUnion);
        return BackdropReusable(in cached, in now, in damageTestRegionPhys, in external);
    }
}
