namespace FluentGpu.Rhi;

/// <summary>What one interval of the pass-granular GPU timeline was spent on. Intervals are bounded by timestamps
/// taken ONLY at pass boundaries (render-target switches, uploads, the clear) — never between two draws into the same
/// target, which on a tiler would itself break the pass being measured.</summary>
public enum GpuPassKind : byte
{
    /// <summary>Glyph-atlas band + image texture copies at the top of the submit.</summary>
    Uploads,
    /// <summary>One baked (static) blur derivative job.</summary>
    BakedBlur,
    /// <summary>A direct-route (secondary swapchain) back-buffer transition + clear.</summary>
    Clear,
    /// <summary>Draw-list playback into one render target between two pass boundaries.</summary>
    Scene,
    /// <summary>The glyph atlas's dirty band upload (DIRECT, same frame — a new glyph is visible the frame it appears).</summary>
    GlyphBand,
    /// <summary>Retained tiles rastered this frame (one CLEAR→PRESERVE render pass each).</summary>
    TileRaster,
    /// <summary>The composite's offscreen work: degraded direct rasters, group surfaces, self-blur, acrylic backdrops.</summary>
    Offscreen,
    /// <summary>The composite pass into the back buffer (CLEAR→STORE).</summary>
    Composite,
}

/// <summary>One interval of the pass-granular GPU timeline: what it was, the size of the target it rendered into
/// (physical px), and its on-GPU duration.</summary>
public readonly record struct GpuPassTiming(GpuPassKind Kind, int TargetWidthPx, int TargetHeightPx, float Ms);

/// <summary>The frame a <see cref="GpuPassTiming"/> sequence belongs to. <paramref name="Sequence"/> is target-local and
/// monotonic; <paramref name="WholeMs"/> spans frame start → end; <paramref name="PassesDropped"/> counts intervals past
/// <see cref="GpuPassTimeline.MaxPasses"/> that were folded into the last recorded one;
/// <paramref name="BackBufferTransitions"/> counts the back buffer's resource-state transitions in that frame.</summary>
public readonly record struct GpuPassFrameSummary(ulong Sequence, float WholeMs, int PassCount, int PassesDropped, int BackBufferTransitions);

/// <summary>Bounds of the pass-granular GPU timeline.</summary>
public static class GpuPassTimeline
{
    /// <summary>Most intervals one frame records; the tail past this folds into the last recorded interval.</summary>
    public const int MaxPasses = 128;
}

/// <summary>Measurement knockouts (probe arguments / the Diagnostics page — runtime-settable, never an environment
/// variable): each removes one cost class from the device's submit so its share of GPU time can be measured by
/// difference. They trade correctness for measurement by definition and are never set by production code.</summary>
[Flags]
public enum GpuKnockouts : byte
{
    None = 0,
    /// <summary>Edge fades draw their content flat (no analytic composite feather, no inline fade group in a tile).</summary>
    EdgeFadesOff = 1,
    /// <summary>No texture uploads are copied (image uploads and the glyph-atlas band stay queued).</summary>
    FreezeUploads = 2,
    /// <summary>Nothing is retained: every segment of the primary composite is DEGRADED to direct raster (the route a slice
    /// takes when its tiles do not fit the budget) and every tile stays invalid.</summary>
    ForceFullDirect = 4,
    /// <summary>The submit transitions + clears the back buffer and presents without replaying the draw list.</summary>
    ClearOnly = 8,
    /// <summary>Every edge fade over child slices composites as a GROUP (one offscreen surface, then the feather) instead of
    /// being distributed as an analytic per-item feather (gpu-renderer.md §13.1e). A probe-only IDENTITY / measurement
    /// control like <see cref="ForceFullDirect"/>: the distributed route differs from it only by the group surface's 8-bit
    /// quantization (≤ 1/255, <c>fade-distribute-identity</c>). Read by the slice recorder's placement, not the device.</summary>
    GroupFades = 16,
    /// <summary>Every <c>.StickyClip</c> records its clip INLINE in its slice's stream (the paint route: re-recorded when
    /// the band line moves) instead of as a composite-time clip on its slice marker (gpu-renderer.md §13.1e). A probe-only
    /// IDENTITY / measurement control like <see cref="GroupFades"/>: the two routes are pixel-identical
    /// (<c>stickyclip-identity</c>). Read by the scene recorder, not the device.</summary>
    StickyClipInPaint = 32,
    /// <summary>The primary target composites and presents WHOLE frames (CLEAR load, full Present) instead of the partial
    /// route (PRESERVE + repaint rects + Present1 dirty rects). A probe-only IDENTITY control: the two routes are
    /// pixel-identical (<c>partial-present-identity</c>).</summary>
    FullPresent = 64,
    /// <summary>Every item composites, including one hidden under a later opaque item (<c>CompositeItem.Opaque</c>) that
    /// the occlusion pass would leave out. A probe-only IDENTITY control: the two routes are pixel-identical
    /// (<c>occlusion-identity</c>).</summary>
    NoOcclusion = 128,
}

/// <summary>Always-on, device-side per-submit counters for one swapchain target (plain fields — never compiled out).
/// Published once per successful submit; <paramref name="Sequence"/> is target-local and monotonic.</summary>
/// <param name="Sequence">Target-local submit sequence the counters describe.</param>
/// <param name="Route">The repaint route the submit actually took.</param>
/// <param name="FullReason">Why the submit's repaint region was forced full (None when it described rects).</param>
/// <param name="CoveragePct">Repaint damage coverage of the target, percent.</param>
/// <param name="FeatherItems">Composite items drawn with an analytic edge feather.</param>
/// <param name="OffscreenPx">Pixel area of the offscreen surfaces leased this submit (degraded segments, groups,
/// self-blurs, acrylic backdrops, inline layers inside tiles).</param>
/// <param name="OffscreenSurfaces">Offscreen surfaces leased this submit.</param>
/// <param name="PassBreaks">Render-target switches + mid-frame copies — each one ends a render pass on a tiler.</param>
/// <param name="Draws">Draw calls recorded.</param>
/// <param name="GlyphInstances">Glyph instances drawn.</param>
/// <param name="UploadBytes">Texture bytes copied at the top of the submit (image uploads + glyph-atlas band).</param>
/// <param name="ImageUploads">Image texture uploads copied.</param>
/// <param name="BakedBlurJobs">Baked-blur derivative jobs executed.</param>
/// <param name="BackBufferTransitions">Back-buffer resource-state transitions.</param>
/// <param name="GroupSurfaces">Group surfaces RENDERED this submit (a group-cache miss).</param>
/// <param name="GroupCacheHits">Groups re-drawn from a retained surface this submit.</param>
/// <param name="RetainedBytes">Bytes held by retained derived surfaces (groups, self-blurs, acrylic backdrops).</param>
/// <param name="ScratchRefused">Scratch-surface leases REFUSED this submit (every scratch slot was leased): an inline folded
/// group inside a tile, a group, a blur or a degraded chunk that asked for one drew nothing. Expected 0
/// (docs/plans/evidence-diagnostics-implementation.md §A.3).</param>
/// <param name="FeatherPx">Quad pixels composited through the analytic feather's per-pixel evaluation — only the strips
/// outside each feather's unit interior (FeatherQuadSplit); the interior composites feather-free.</param>
public readonly record struct GpuFrameCounters(
    ulong Sequence, RepaintRoute Route, RepaintFullReason FullReason, float CoveragePct,
    int FeatherItems, long OffscreenPx, int OffscreenSurfaces, int PassBreaks, int Draws, int GlyphInstances,
    long UploadBytes, int ImageUploads, int BakedBlurJobs, int BackBufferTransitions,
    int GroupSurfaces = 0, int GroupCacheHits = 0, long RetainedBytes = 0, long FeatherPx = 0, int ScratchRefused = 0);

/// <summary>Where the composite's OFFSCREEN work went in one submit (always-on plain counters on the D3D12 device; the
/// scroll bench's per-kind split): surfaces leased and their pixel area per kind of work.</summary>
public readonly record struct OffscreenSplit(
    int GroupSurfaces, long GroupPx, int GroupCacheHits,
    int LeafBlurSurfaces, long LeafBlurPx, int LeafBlurHits,
    int BackdropSurfaces, long BackdropPx, int BackdropHits,
    int DirectSurfaces, long DirectPx,
    int InlineSurfaces, long InlinePx);
