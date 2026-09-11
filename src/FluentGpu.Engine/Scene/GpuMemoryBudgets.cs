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

    /// <summary>Image-cache steady-state cap. Weak drops to 24 MB (from 64) to shrink both the at-rest residency and
    /// the post-device-recovery re-realize burst on Adreno-class parts that page hard over their small LOCAL budget
    /// (adreno-hang-fixes.md M5).</summary>
    public const long ImageCacheDefault = 64L * 1024 * 1024;
    public const long ImageCacheWeak = 24L * 1024 * 1024;

    /// <summary>Derived/blur (blur-hash preview) soft cap. Weak halves it so previews retire faster instead of
    /// padding the small LOCAL segment.</summary>
    public const long DerivedDefault = 16L * 1024 * 1024;
    public const long DerivedWeak = 8L * 1024 * 1024;

    /// <summary>The three caps for a tier. Pure: no globals, no environment, no clock — so a gate can drive both
    /// tiers headlessly, which is the whole point (see the class remarks).</summary>
    public static (long PixelPool, long ImageCache, long Derived) For(bool weak)
        => weak
            ? (PixelPoolWeak, ImageCacheWeak, DerivedWeak)
            : (PixelPoolDefault, ImageCacheDefault, DerivedDefault);
}
