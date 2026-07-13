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
/// with ONE fixed-sigma separable kernel by choosing the snapshot downsample factor instead of the kernel width:
/// <c>down = round(sigmaPhys / KernelSigma)</c>, so blurring the 1/down-resolution snapshot with the fixed
/// sigma-7.5 kernel yields the requested full-resolution sigma (30 DIP ⇒ /4 at 100% DPI, /6 at 150%, /8 at 200%).
/// </summary>
public static class AcrylicBackdropMath
{
    /// <summary>Gaussian standard deviation of the FIXED separable blur kernel, in downsampled texels.</summary>
    public const float KernelSigma = 7.5f;

    /// <summary>Discrete kernel radius in downsampled texels (≈3σ support; the tail beyond carries &lt;0.4% weight —
    /// the same truncation a D2D GaussianBlur uses for its kernel window).</summary>
    public const int KernelRadius = 22;

    /// <summary>Bilinear-optimized taps per pass: the center texel + 11 linear taps, each folding a texel pair of the
    /// 45-texel discrete kernel into one bilinear fetch (offset weighted between the pair).</summary>
    public const int TapCount = 12;

    private static readonly float[] s_tapOffsets = new float[TapCount];
    private static readonly float[] s_tapWeights = new float[TapCount];

    static AcrylicBackdropMath()
    {
        // Discrete gaussian σ = KernelSigma over [-KernelRadius..KernelRadius], normalized to sum 1.
        Span<double> w = stackalloc double[KernelRadius + 1];
        double sum = 0;
        for (int i = 0; i <= KernelRadius; i++)
        {
            w[i] = Math.Exp(-(double)i * i / (2.0 * KernelSigma * KernelSigma));
            sum += i == 0 ? w[i] : 2.0 * w[i];
        }
        for (int i = 0; i <= KernelRadius; i++) w[i] /= sum;

        // Fold texel pairs (1,2),(3,4),…,(21,22) into single bilinear taps (linear-sampling gaussian):
        // weight = wa+wb, offset = (a·wa + b·wb)/(wa+wb) — the hardware bilinear filter reconstructs both texels.
        s_tapOffsets[0] = 0f;
        s_tapWeights[0] = (float)w[0];
        for (int t = 1; t < TapCount; t++)
        {
            int a = 2 * t - 1, b = 2 * t;
            double wp = w[a] + w[b];
            s_tapOffsets[t] = (float)((a * w[a] + b * w[b]) / wp);
            s_tapWeights[t] = (float)wp;
        }
    }

    /// <summary>Per-pass bilinear tap offsets in source texels (index 0 = center). One-sided; the shader mirrors.</summary>
    public static ReadOnlySpan<float> TapOffsets => s_tapOffsets;

    /// <summary>Per-pass tap weights (index 0 = center weight; indices 1.. are applied at ±offset, so the
    /// total kernel mass is <c>w[0] + 2·Σ w[1..]</c> == 1).</summary>
    public static ReadOnlySpan<float> TapWeights => s_tapWeights;

    /// <summary>
    /// Snapshot downsample divisor for a requested blur sigma (in DIPs — WinUI BlurAmount semantics) at a DPI scale.
    /// Effective full-resolution sigma = factor · <see cref="KernelSigma"/> ≈ blurSigmaDip · scale physical px
    /// (exact at 100/125/150/175/200% DPI for the WinUI sigma 30). Clamped to /8 — beyond that the bilinear
    /// down/up-sample artifacts outweigh kernel fidelity.
    /// </summary>
    public static int DownsampleFactor(float blurSigmaDip, float scale)
    {
        float sigmaPhys = MathF.Max(1f, blurSigmaDip * MathF.Max(0.25f, scale));
        return Math.Clamp((int)MathF.Round(sigmaPhys / KernelSigma), 1, 8);
    }

    /// <summary>
    /// The backdrop region to snapshot beneath a layer rect, in physical px clamped to the canvas: the rect inflated
    /// on every side by the full blur support (<see cref="KernelRadius"/>·down physical px), so every blurred texel
    /// under the rect samples REAL backdrop instead of a clamp-streaked edge (WinUI blurs the whole backdrop;
    /// inflating by the kernel support makes the region blur bit-identical inside the rect).
    /// </summary>
    public static void SnapshotRegion(in RectF deviceRectDip, float scale, int down, int canvasW, int canvasH,
        out int x, out int y, out int w, out int h)
    {
        int pad = KernelRadius * down;
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
    /// <see cref="SnapshotRegion"/>), so a node animating in the ±KernelRadius·down halo but outside rect+8 no longer
    /// forces a per-frame re-blur; a cache MISS still snapshots/blurs the inflated region (edge fidelity unchanged).</summary>
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
    /// kernel-INFLATED snapshot region — a node animating anywhere in the ±KernelRadius·down halo but outside rect+8 no
    /// longer forces a re-blur every frame (misses still snapshot/blur the inflated region, so edge fidelity is intact).</summary>
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
