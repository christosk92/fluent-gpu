namespace FluentGpu.Foundation;

/// <summary>Coarse GPU performance class, published ONCE at device init (backend-set, app-read) so visual-quality
/// defaults can scale to the hardware WITHOUT a render-hardware seam contract. Integrated/UMA GPUs and the WARP software
/// rasterizer read as <see cref="GpuPowerTier.Weak"/> (a fraction of a discrete GPU's fill rate / memory bandwidth);
/// a GPU with dedicated VRAM reads as <see cref="GpuPowerTier.Strong"/>. <see cref="GpuPowerTier.Unknown"/> = not yet
/// detected (or detection failed) — callers treat it as the balanced middle.</summary>
public enum GpuPowerTier
{
    Unknown = 0,
    Weak = 1,
    Strong = 2,
}

/// <summary>
/// Process-global GPU profile. The active RHI backend sets <see cref="Tier"/> during device init; UI code reads it to
/// pick effect-quality defaults that must scale to the hardware (e.g. the lyrics depth-of-field self-blur, which is a
/// per-line full-resolution Gaussian that is invisible on a discrete GPU but bandwidth-bound on an integrated one).
/// A single GPU per desktop process, so a global is the right shape — exactly like <see cref="Diag"/>.
/// </summary>
public static class GpuProfile
{
    /// <summary>The detected GPU power class (default <see cref="GpuPowerTier.Unknown"/> until the backend sets it).</summary>
    public static GpuPowerTier Tier { get; set; } = GpuPowerTier.Unknown;

    /// <summary>True only when the GPU is KNOWN to be weak (integrated / UMA / WARP). Unknown is NOT weak — callers
    /// that want "guaranteed cheap on the worst hardware" gate on this; the balanced default covers Unknown.</summary>
    public static bool IsWeak => Tier == GpuPowerTier.Weak;
}
