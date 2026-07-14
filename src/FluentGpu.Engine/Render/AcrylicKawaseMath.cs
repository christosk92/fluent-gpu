using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Portable math for the LIVE-BACKDROP acrylic blur run as a <b>dual-Kawase</b> downsample/upsample chain
/// (ARM SIGGRAPH 2015 dual filter, Bjørge; shipped by KWin / picom / Plasma Better Blur). This replaces the separable
/// Gaussian of passes B/C in <c>Rhi.D3D12 AcrylicCompositor</c> (design/subsystems/backdrop-effects-animation.md §2.3).
/// The GPU leaf consumes these values so the σ→chain mapping, the snapshot pad, and the RT-pyramid level dims are all
/// headless-verifiable in the VerticalSlice while the COM/HLSL stays render-thread-confined in the leaf. The self-blur
/// (own-content) path in <c>OpacityLayerCompositor</c> keeps the separable pipeline (<see cref="AcrylicBackdropMath"/>);
/// only the LIVE-BACKDROP path changes.
///
/// <para><b>The chain.</b> Pass A snapshots the backdrop region at FULL resolution (down = 1; see the pad note below);
/// the chain then runs <c>iterations</c> downsample passes (each halving resolution — the pyramid ½, ¼, ⅛, 1/16 of the
/// region) followed by <c>iterations</c> upsample passes back to full snapshot resolution. Each downsample is a 5-tap
/// bilinear filter (center ½ + four half-texel diagonal corners ⅛ each, ÷8); each upsample is an 8-tap tent (÷12). The
/// blur RADIUS grows with the iteration count in coarse 2× steps; the per-pass <c>offset</c> (in source texels)
/// interpolates SMOOTHLY between those integer steps (KWin Phabricator D9848 model), so σ is continuously controllable.</para>
///
/// <para><b>σ→(iterations, offset) mapping — the adopted equivalence.</b> A dual-Kawase chain's Gaussian-equivalent
/// standard deviation, referred to full-resolution pixels, grows as the sampling reach at the coarsest pyramid level,
/// i.e. <c>σ_eff ≈ offset · 2^iterations · K</c>. K = <see cref="SigmaPerLevel"/> is a single calibration constant fixed
/// by an A/B against the WinUI σ=30 acrylic look (the Wave-A separable reference). Because iterations are discrete, each
/// integer i owns a σ band <c>[2^i·K·1, 2^i·K·2]</c> (offset ∈ [1,2]); the bands are contiguous (the top of band i is
/// the bottom of band i+1) so every σ maps to exactly one (i, offset) with offset in the sane [1,2] range. With K = 1.25
/// this places the whole WinUI-relevant σ14…30 band in 3–4 iterations:
/// <c>σ14→(3, 1.4)  σ20→(3, 2.0)  σ30→(4, 1.5)</c>, each round-tripping to its input σ exactly via
/// <see cref="EffectiveSigma"/>. Exactness of K is not claimed — the visual A/B is the authority (§7 of the task); the
/// formula guarantees monotonic, smooth, in-range control.</para>
///
/// <para><b>Snapshot decision (down = 1).</b> Pass A snapshots at full region resolution and lets the chain do ALL the
/// halving. Rationale: (a) it keeps the pad/support math and the K calibration in clean full-resolution units (the chain
/// levels ARE ½, ¼, ⅛, 1/16 of the region); (b) the finest ½-region level preserves the crisp near-edge gradient at the
/// acrylic rect boundary that a pre-halved (down = 2) snapshot would soften. The trade-off is a larger pinned-cache RT
/// (the retained blurred snapshot is region-sized, not region/8 as the old aggressive downsample curve produced); this
/// is bandwidth the retained-backdrop cache pays once, then reuses across frames at rest.</para>
/// </summary>
public static class AcrylicKawaseMath
{
    /// <summary>Max downsample iterations = the RT pyramid depth (½, ¼, ⅛, 1/16 of the region). Bounds the chain's pass
    /// count (2·iterations blur passes) and the pyramid RT lease count (iterations + 1 levels held concurrently).</summary>
    public const int MaxIterations = 4;

    /// <summary>Pass-A snapshot downsample divisor the chain runs from. 1 = full-region snapshot (the chain does all the
    /// halving; crispest near-edge gradient, region-sized pinned cache RT). THE ONE SANCTIONED PIVOT: setting this to 2
    /// snapshots at ½ resolution (pyramid ¼…1/32) and cuts pass-A + pyramid bandwidth ~4× at the cost of a slightly
    /// softer rect edge — if flipped, K (<see cref="SigmaPerLevel"/>) must be re-calibrated (effective reach doubles per
    /// level) and the acrylic visual A/B re-shot. Not an env flag by design: the trade is a code decision, made once.</summary>
    public const int SnapshotDown = 1;

    /// <summary>Per-pass offset floor / ceiling (source texels). The offset interpolates the blur radius smoothly between
    /// the discrete 2× iteration steps (KWin D9848); [1,2] is the numerically well-behaved band the WinUI σ14…30 maps into
    /// (offset &lt; 1 collapses toward a plain box-downsample; &gt; 2 starts to alias the 5-tap corners).</summary>
    public const float OffsetMin = 1f, OffsetMax = 2f;

    /// <summary>Calibration constant K in <c>σ_eff = offset · 2^iterations · K</c> (see the class remarks). Fixed by the
    /// visual A/B against the WinUI σ=30 acrylic; K = 1.25 lands σ14…30 in 3–4 iterations with offset ∈ [1,2].</summary>
    public const float SigmaPerLevel = 1.25f;

    /// <summary>Choose the dual-Kawase chain (iteration count + per-pass offset) that reproduces a requested WinUI blur
    /// sigma (DIPs) at a DPI scale. Picks the smallest iteration count whose σ band (offset ≤ <see cref="OffsetMax"/>)
    /// covers <c>sigmaPhys = BlurSigma·scale</c>, then solves the offset for the residual — clamped to [<see cref="OffsetMin"/>,
    /// <see cref="OffsetMax"/>]. In-band sigmas round-trip exactly through <see cref="EffectiveSigma"/>.</summary>
    public static void SelectChain(float blurSigmaDip, float scale, out int iterations, out float offset)
    {
        float sigmaPhys = AcrylicBackdropMath.PhysicalSigma(blurSigmaDip, scale);
        int i = 1;
        while (i < MaxIterations && sigmaPhys > (1 << i) * SigmaPerLevel * OffsetMax) i++;
        iterations = i;
        offset = Math.Clamp(sigmaPhys / ((1 << i) * SigmaPerLevel), OffsetMin, OffsetMax);
    }

    /// <summary>The Gaussian-equivalent full-resolution sigma a chain of <paramref name="iterations"/> downsample passes
    /// at per-pass <paramref name="offset"/> produces: <c>offset · 2^iterations · K</c> (K = <see cref="SigmaPerLevel"/>).
    /// The inverse of <see cref="SelectChain"/> for in-band sigmas (round-trip identity).</summary>
    public static float EffectiveSigma(int iterations, float offset) => offset * (1 << iterations) * SigmaPerLevel;

    /// <summary>The chain's blur SUPPORT (max sample reach from a center output texel) in full-resolution px, a
    /// conservative bound over the down + up passes: <c>2.5 · offset · 2^iterations</c> (≥ the derived geometric reach
    /// <c>2.5·offset·(2^iterations − 1)</c> summed across both phases). The snapshot pad must cover this so every blurred
    /// texel under the rect samples REAL backdrop, never a clamp-streaked edge.</summary>
    public static float SupportPx(int iterations, float offset) => 2.5f * offset * (1 << iterations);

    /// <summary>The snapshot pad (physical px) added on every side of the layer rect in pass A: the chain
    /// <see cref="SupportPx"/> for the chosen (iterations, offset), rounded up, plus one coarsest-level texel
    /// (<c>2^iterations</c>) of alignment slack. Feed this as the <c>kernelRadiusTexels</c> argument of
    /// <see cref="AcrylicBackdropMath.SnapshotRegion"/> with <c>down = 1</c> (pad = radius·down = pad).</summary>
    public static int PadPx(int iterations, float offset)
        => (int)MathF.Ceiling(SupportPx(iterations, offset)) + (1 << iterations);

    /// <summary>The pyramid level dimension for a full snapshot dimension: <paramref name="level"/> successive
    /// <c>ceil(d/2)</c> halvings (floored at 1 so a small region never collapses to a 0-texel level). Level 0 = the full
    /// snapshot; levels 1..iterations are the ½, ¼, ⅛, 1/16 pyramid the chain blurs and folds back up.</summary>
    public static int LevelDim(int fullDim, int level)
    {
        int d = Math.Max(1, fullDim);
        for (int k = 0; k < level; k++) d = Math.Max(1, (d + 1) / 2);
        return d;
    }
}
