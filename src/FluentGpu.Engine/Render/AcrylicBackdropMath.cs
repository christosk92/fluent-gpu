using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Portable blur math shared by the in-app acrylic and the per-node self-blur (gpu-renderer.md §13 — the composite's
/// backdrop and self-blur surfaces, the tile rasterizer's inline blur groups, <see cref="SelfBlurRegion.TapRadius"/>):
/// the downsample schedule and the bilinear Gaussian kernel, so the weights the GPU samples come from one
/// headless-verifiable source while the COM/HLSL stays render-thread-confined in the leaf.
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
}
