namespace FluentGpu.Foundation;

/// <summary>Path-rendering anti-aliasing strategy (gpu-renderer.md §5 step 4). <see cref="Fringe"/> (the default) is
/// the shipped approach — an extruded 0→1 coverage edge, MSAA off, keeping the whole renderer single-sample (no
/// resolve target, no MSAA RT) — <see cref="PathSweep"/>/<see cref="PathStroker"/> in this batch only ever emit
/// fringe geometry. <see cref="Msaa4"/> is DEFINED but has NO backend behind it yet: canon's open question `OQ-1`
/// ("validate fringe vs MSAA4 on real icon-as-paths / Bézier logos via the golden gate before locking MSAA out") is
/// still open, and an MSAA render target/resolve path is a <c>FluentGpu.Windows</c>-side change outside this batch's
/// file ownership. A future backend that honors this flag should call <see cref="GpuProfile.NotePathMsaaFallback"/>
/// when it falls back to <see cref="Fringe"/> rather than silently doing nothing.</summary>
public enum PathAaMode { Fringe = 0, Msaa4 = 1 }

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

    /// <summary>Path-rendering AA strategy (gpu-renderer.md §5 step 4). Default <see cref="PathAaMode.Fringe"/>;
    /// <see cref="PathAaMode.Msaa4"/> is selectable but falls back to <see cref="PathAaMode.Fringe"/> (MSAA is
    /// descoped — canon's open <c>OQ-1</c>). <b>Deviation from canon:</b> gpu-renderer.md §5 prints
    /// <c>RenderConfig.PathAaMode</c>; there is no <c>RenderConfig</c> type anywhere in this repo, and this class
    /// already carries the identical "process-global, backend-set at init, app-read" posture <see cref="Tier"/> uses
    /// (its own doc: "a single GPU per desktop process, so a global is the right shape") — so the setting lives here
    /// instead of inventing a new type for one field.</summary>
    public static PathAaMode PathAaMode { get; set; } = PathAaMode.Fringe;

    /// <summary>Soft byte budget for <see cref="FluentGpu.Render.PathRealizationCache"/>'s retained vertex/index
    /// slab (gpu-renderer.md §5.1 "LRU eviction by slab pressure"). Default 4 MB. Advisory, not a hard cap: the cache
    /// evicts LRU entries outside its quarantine window to try to stay under this, but never fails a realization
    /// (and never evicts a quarantined entry) just to respect it — see <see cref="FluentGpu.Render.PathRealizationCache"/>.</summary>
    public static int PathSlabBudgetBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Count of a <see cref="PathAaMode.Msaa4"/> selection actually falling back to <see cref="PathAaMode.Fringe"/>
    /// (diagnostics — MSAA is descoped, not silently ignored).</summary>
    public static void NotePathMsaaFallback() => Diag.Count("path", "msaaFallback");
}
