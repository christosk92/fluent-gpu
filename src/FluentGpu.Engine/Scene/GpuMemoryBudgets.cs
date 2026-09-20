namespace FluentGpu.Scene;

/// <summary>The three image-pipeline byte budgets that are captured ONCE at construction, as a pure function of the
/// GPU tier.
/// <para><b>Why this is a function and not three <c>GpuProfile.IsWeak</c> reads at their use sites.</b> It was three
/// reads, and all three were wrong on every UMA machine: <c>GpuProfile.Tier</c> is published during D3D12 device
/// init, the device is brought up lazily by the first <c>CreateSwapchain</c>, and that call is made by the AppHost
/// constructor — i.e. AFTER the host has already built its pixel pool and image cache. <c>Unknown</c> is not weak by
/// contract, so each budget silently took the discrete branch and sized itself twice as large as intended, for the
/// lifetime of the process. The per-frame/per-tick readers of <c>IsWeak</c> were never affected (they re-read after
/// publication); only these three captured it too early.</para>
/// <para>The fix is twofold: the host now forces the adapter up before reading anything (<c>EnsureDeviceCreated</c>),
/// and the DECISION lives here, taking <paramref name="weak"/> as an argument rather than reading the global. That is
/// the same shape <c>LayerTargetTrim.Classify</c> uses and for the same reason — <c>GpuProfile.IsWeak</c> is always
/// false headlessly, so a gate that reads the global can only ever exercise one of the two tiers, which is precisely
/// why this class of bug survived. Both tiers are gated as <c>gate.budgets.*</c>.</para></summary>
public static class GpuMemoryBudgets
{
    /// <summary>CPU pixel-pool retained cap. Weak halves the shipped 32 MB default: on UMA the pool competes with the
    /// textures it feeds for the same physical memory.</summary>
    public const long PixelPoolDefault = 32L * 1024 * 1024;
    public const long PixelPoolWeak = 16L * 1024 * 1024;

    /// <summary>Image-cache steady-state cap. Weak stays well below the discrete default to shrink both the at-rest
    /// residency and the post-device-recovery re-realize burst on Adreno-class parts that page hard over their small
    /// LOCAL budget (adreno-hang-fixes.md M5).
    /// <para>Raised from 24 MB when the cache started charging COMMITTED bytes instead of decoded pixels
    /// (<see cref="ImageCache.CommittedBytesFor"/>). The old 24 was an honest number against a dishonest measure: at a
    /// ~3.5× average over-commit it described roughly 84 MB of real GPU memory. Holding 24 against the true figure
    /// would have cut the resident set to under a third of what shipped and shrunk the prefetch ring with it, so the
    /// cap moves to 40 — still a large net reduction (~45 MB), with a BIGGER usable ring than before because every
    /// byte of it is now a byte the GPU actually holds.</para></summary>
    public const long ImageCacheDefault = 64L * 1024 * 1024;
    public const long ImageCacheWeak = 40L * 1024 * 1024;

    /// <summary>Derived/blur (blur-hash preview) soft cap. Weak halves it so previews retire faster instead of
    /// padding the small LOCAL segment.</summary>
    public const long DerivedDefault = 16L * 1024 * 1024;
    public const long DerivedWeak = 8L * 1024 * 1024;

    /// <summary>The three caps for a tier. Pure: no globals, no environment, no clock — so a gate can drive both
    /// tiers headlessly, which is the whole point (see the class remarks).
    /// <para><paramref name="localBudgetBytes"/> is the adapter's DXGI LOCAL segment budget (0 = unknown, e.g. the
    /// sample has not been taken yet, or the device is headless) — on the weak tier it derives
    /// <see cref="ImageCache"/> instead of using the flat <see cref="ImageCacheWeak"/> constant, because a 128 MB
    /// Adreno-class part and a 512 MB-class UMA iGPU are both "weak" but do not have the same LOCAL segment to share
    /// between the swapchain, the pixel pool and the image cache. The discrete tier is unaffected — it never reads
    /// the parameter.</para></summary>
    public static (long PixelPool, long ImageCache, long Derived) For(bool weak, long localBudgetBytes = 0)
        => weak
            ? (PixelPoolWeak, WeakImageCacheFor(localBudgetBytes), DerivedWeak)
            : (PixelPoolDefault, ImageCacheDefault, DerivedDefault);

    /// <summary>5/16 of the LOCAL segment, clamped to [32, 64] MB. 0 (unknown LOCAL) keeps the shipped 40 MB flat
    /// default. A 128 MB Adreno part lands at exactly 40 MB — the number this shipped with — so this is a
    /// derivation, not a re-tune; a 256 MB-class part is allowed up to the discrete cache's own 64 MB ceiling, and
    /// nothing below the 32 MB floor the eviction/prefetch ring needs to stay useful.</summary>
    private static long WeakImageCacheFor(long localBudgetBytes)
        => localBudgetBytes <= 0 ? ImageCacheWeak : Math.Clamp(localBudgetBytes * 5 / 16, 32L << 20, 64L << 20);
}
