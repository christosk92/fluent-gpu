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
    /// residency and the post-device-recovery re-realize burst on a weak part that pages hard once it is over its LOCAL
    /// budget (adreno-hang-fixes.md M5). 40 MB is the fallback used when the LOCAL budget is unknown (see
    /// <see cref="For"/>); a known LOCAL budget derives the cap instead.
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
    /// <see cref="ImageCache"/> instead of using the flat <see cref="ImageCacheWeak"/> constant, because the derivation
    /// separates weak parts by their DXGI LOCAL budget: a part whose LOCAL budget really is small lands at 32-40 MB,
    /// while a UMA part, whose LOCAL budget is the ~15 GB shared-pool residency budget, lands at the 64 MB clamp (see
    /// <c>WeakImageCacheFor</c>). The discrete tier is unaffected — it never reads the parameter.</para></summary>
    public static (long PixelPool, long ImageCache, long Derived) For(bool weak, long localBudgetBytes = 0)
        => weak
            ? (PixelPoolWeak, WeakImageCacheFor(localBudgetBytes), DerivedWeak)
            : (PixelPoolDefault, ImageCacheDefault, DerivedDefault);

    /// <summary>5/16 of the LOCAL segment, clamped to [32, 64] MB. 0 (unknown LOCAL) keeps the shipped 40 MB flat
    /// default. The derivation only distinguishes a discrete-class part that really has a small LOCAL segment: a
    /// 128 MB LOCAL budget lands at exactly 40 MB (the number this shipped with) and a 256 MB-class one is allowed up
    /// to the discrete cache's own 64 MB ceiling, never below the 32 MB floor the eviction/prefetch ring needs.
    /// <para><b>On UMA the clamp lands at 64 MB, by design (F251).</b> A UMA adapter (the Adreno X1 and every iGPU) has
    /// only a small "dedicated" carve-out (128 MB, as the <c>[d3d12.adapter] vramMB</c> log line prints it), but DXGI
    /// reports the SHARED pool as the LOCAL segment, so <c>QueryVideoMemoryInfo(LOCAL).Budget</c> is the OS residency
    /// budget the driver actually enforces: about 15 GB on a 16 GB machine. A 64 MB image cache inside that is harmless,
    /// so the flat 40 MB of the 128 MB premise does not apply. That LOCAL budget stays authoritative and is NOT replaced
    /// by <c>DedicatedVideoMemory</c>: sizing against 128 MB would make <see cref="FluentGpu.Hosting.VramShedPolicy"/>
    /// shed on every frame (the swapchain alone is 66 MB), the churn that policy exists to prevent. The shed arms
    /// when that OS budget SHRINKS under real system memory pressure.</para></summary>
    private static long WeakImageCacheFor(long localBudgetBytes)
        => localBudgetBytes <= 0 ? ImageCacheWeak : Math.Clamp(localBudgetBytes * 5 / 16, 32L << 20, 64L << 20);
}
