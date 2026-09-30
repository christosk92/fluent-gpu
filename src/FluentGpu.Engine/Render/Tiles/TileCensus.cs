namespace FluentGpu.Render.Tiles;

/// <summary>
/// ALWAYS-ON retained-tile census for one composite turn (docs/plans/scroll-gpu-retained-tiles-implementation.md §E) —
/// plain counters on <c>RenderFrameCensus.Tiles</c>: how many slices / tiles are live and resident (and their bytes), how
/// many tiles were scheduled / actually rastered / evicted this turn, how many slices degraded to direct raster, the
/// composite items and placements, and every invalidation reason's count — so a surprise re-raster names its cause.
/// <see cref="ExposedMissing"/> (a visible tile that composited nothing) must be 0.
/// </summary>
public readonly record struct TileCensus
{
    public int Turn { get; init; }
    public int Slices { get; init; }
    public int LiveTiles { get; init; }
    public int ResidentTiles { get; init; }
    public long ResidentBytes { get; init; }
    public long BudgetBytes { get; init; }
    public int Scheduled { get; init; }
    public int Rastered { get; init; }
    public int Evicted { get; init; }
    public int DegradedSlices { get; init; }
    public int ExposedMissing { get; init; }
    /// <summary>Scroll segments whose visible content reached past the realized rows this turn (must be 0).</summary>
    public int CoverageClamps { get; init; }
    /// <summary>VALID tiles used this turn and not re-rastered whose pixels were rastered for different content than the
    /// stream now describes (evidence-diagnostics §A.1). Must be 0.</summary>
    public int StaleTiles { get; init; }
    /// <summary>Resident valid tiles whose content want was folded this turn (their segment re-recorded or its grid
    /// moved) — the cost side of content-derived validity (gpu-renderer.md §13.1c); 0 on a composite-only turn.</summary>
    public int ContentChecked { get; init; }
    /// <summary>Tiles the content check invalidated this turn (counted within <see cref="Content"/>): valid after every
    /// damage rect, yet rastered for different content than the stream now wants — what damage alone would have left
    /// stale.</summary>
    public int ContentCaught { get; init; }
    public int Items { get; init; }
    public int NoTexture { get; init; }
    public int Content { get; init; }
    public int PrimCount { get; init; }
    public int ValidRectChanged { get; init; }
    public int ScaleChanged { get; init; }
    public int SliceGeometry { get; init; }
    public int BackgroundOrTheme { get; init; }
    public int EvictedReason { get; init; }
    public int Degraded { get; init; }
    /// <summary>Effect slices cut by the last record pass that SPENT the effect budget (opacity / self-blur / non-
    /// distributable edge fade / sticky-parallax translation) — <c>SliceRecorder.EffectSliceCap</c> bounds it.</summary>
    public int EffectSlices { get; init; }
    /// <summary>INLINE (folded) opacity / self-blur / edge-fade group layers in the composited partition (the effect
    /// budget was spent, or they sat inside another inline layer, where no slice may be cut).</summary>
    public int Folded { get; init; }
    /// <summary>Acrylic surfaces cut as their own slice (a frost exists only as a composite <c>Backdrop</c> item).</summary>
    public int AcrylicSlices { get; init; }
    /// <summary>Acrylic surfaces that could NOT be cut (acrylic cap, a node reached twice, walk headroom, inside an inline
    /// layer) and painted their opaque FallbackColor instead — WinUI's no-backdrop answer. Expected 0.</summary>
    public int AcrylicFallbacks { get; init; }
    /// <summary>Distributable edge fades cut without spending the effect budget (<c>CompositeSliceFlags.DistributeFade</c>).</summary>
    public int FreeFades { get; init; }
    /// <summary>Σ surface bytes of every VISIBLE (order-0) tile requested this turn, resident or not — what this page
    /// needs to show without degrading. Compare with <see cref="BudgetBytes"/>.</summary>
    public long VisibleNeedBytes { get; init; }
    /// <summary>Group surfaces the backend rendered this turn (group-cache misses).</summary>
    public int GroupSurfaces { get; init; }
    /// <summary>Groups the backend re-drew from a retained surface this turn.</summary>
    public int GroupCacheHits { get; init; }
    /// <summary>Bytes held by retained derived surfaces (groups, self-blurs, backdrops) — ≤ BudgetBytes ×
    /// <c>TileBudget.RetainedShare</c>.</summary>
    public long RetainedBytes { get; init; }

    /// <summary>The census of the turn <paramref name="table"/> just closed (read after the submit, before the next
    /// turn opens). <paramref name="record"/> = the slice recorder's last pass (<c>SliceRecorder.LastStats</c>),
    /// <paramref name="cache"/> = the backend's group-cache census (<c>IGpuDevice.LastCompositeCache</c>).</summary>
    public static TileCensus Capture(SliceTable table, int items, int rastered, int exposedMissing, int coverageClamps, long budgetBytes,
        SliceRecordStats record = default, FluentGpu.Rhi.CompositeCacheStats cache = default) => new()
    {
        EffectSlices = record.EffectSlices,
        Folded = record.Folded,
        AcrylicSlices = record.AcrylicSlices,
        AcrylicFallbacks = record.AcrylicFallbacks,
        FreeFades = record.FreeFades,
        VisibleNeedBytes = table.VisibleNeedBytes,
        GroupSurfaces = cache.GroupSurfaces,
        GroupCacheHits = cache.GroupCacheHits,
        RetainedBytes = cache.RetainedBytes,
        Turn = table.Frame,
        Slices = table.LiveSlices,
        LiveTiles = table.LiveTiles,
        ResidentTiles = table.ResidentTiles,
        ResidentBytes = table.ResidentBytes,
        BudgetBytes = budgetBytes,
        Scheduled = table.ScheduledThisFrame,
        Rastered = rastered,
        Evicted = table.EvictedThisFrame,
        DegradedSlices = table.DegradedSlicesThisFrame,
        ExposedMissing = exposedMissing,
        StaleTiles = table.StaleTiles,
        ContentChecked = table.ContentCheckedThisFrame,
        ContentCaught = table.ContentCaughtThisFrame,
        CoverageClamps = coverageClamps,
        Items = items,
        NoTexture = table.InvalidationCount(InvalidationReason.NoTexture),
        Content = table.InvalidationCount(InvalidationReason.Content),
        PrimCount = table.InvalidationCount(InvalidationReason.PrimCount),
        ValidRectChanged = table.InvalidationCount(InvalidationReason.ValidRectChanged),
        ScaleChanged = table.InvalidationCount(InvalidationReason.ScaleChanged),
        SliceGeometry = table.InvalidationCount(InvalidationReason.SliceGeometry),
        BackgroundOrTheme = table.InvalidationCount(InvalidationReason.BackgroundOrTheme),
        EvictedReason = table.InvalidationCount(InvalidationReason.Evicted),
        Degraded = table.InvalidationCount(InvalidationReason.Degraded),
    };
}
