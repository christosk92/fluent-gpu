using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;

namespace FluentGpu.Hosting;

/// <summary>
/// ALWAYS-ON per-frame render census — plain counters, never <c>Diag.Set</c> (which compiles out of Release). One value
/// per painted frame on <see cref="FrameStats.RenderCensus"/>: the UI side of the frame (repaint damage, span reuse,
/// capture) plus the device's latest retired per-submit counters (<see cref="Device"/>, the render thread's
/// <see cref="GpuFrameCounters"/> for the main swapchain). A consumer (the Scroll Lab, a probe) reads it straight off
/// <see cref="AppHost.FrameCompleted"/> without enabling anything.
/// </summary>
public readonly record struct RenderFrameCensus
{
    /// <summary>Repaint damage coverage the UI computed for this frame (0..1; 1 when a full repaint was forced).</summary>
    public float RepaintCoverage { get; init; }
    /// <summary>Disjoint rects in this frame's repaint set.</summary>
    public int RepaintRects { get; init; }
    /// <summary>The UI side's named full-repaint reason (None unless the region was forced full).</summary>
    public RepaintFullReason RepaintFullReason { get; init; }
    /// <summary>Edge-fade groups the recorder emitted.</summary>
    public int EdgeFadeGroups { get; init; }
    /// <summary>Spans reused verbatim and re-recorded.</summary>
    public int SpansReused { get; init; }
    /// <summary>Bytes the record pass wrote into slice arenas (0 on a composite-only turn).</summary>
    public long SliceBytesRecorded { get; init; }
    /// <inheritdoc cref="SpansReused"/>
    public int SpansReRecorded { get; init; }
    /// <summary>Why spans were re-recorded instead of reused (per-reason counts).</summary>
    public SpanReuseMissStats SpanMisses { get; init; }
    /// <summary>The publication's capture took the incremental path.</summary>
    public bool CaptureIncremental { get; init; }
    /// <summary>Why the capture was full (<see cref="CaptureFullReason.None"/> when incremental or not captured).</summary>
    public CaptureFullReason CaptureFullReason { get; init; }
    /// <summary>Nodes the capture copied (every reachable node on a full capture, only the changed ones on an
    /// incremental one).</summary>
    public int CapturedNodes { get; init; }
    /// <summary>UI-side record time and capture time (ms).</summary>
    public float RecordMs { get; init; }
    /// <inheritdoc cref="RecordMs"/>
    public float CaptureMs { get; init; }
    /// <summary>The device's latest retired per-submit counters for the main swapchain (route actually taken, full
    /// reason, feathered items, offscreen surfaces + px, pass breaks, draws, glyph instances, upload bytes, image uploads,
    /// baked-blur jobs, back-buffer transitions). <c>Device.Sequence == 0</c> ⇒ no sample yet / unsupported backend.</summary>
    public GpuFrameCounters Device { get; init; }
    /// <summary>The retained-tile census of the latest composite turn (slices, live / resident tiles + bytes, scheduled /
    /// rastered / evicted tiles, degraded slices, exposed-missing tiles, per-reason invalidations).</summary>
    public FluentGpu.Render.Tiles.TileCensus Tiles { get; init; }
}

/// <summary>
/// UI-readable snapshot of the render thread's pacing state (cumulative counters — a reader differences two samples)
/// plus the display clock's filter state and the host's GPU governor. What the always-on <c>[render.pace]</c> line
/// prints once a second, as data, so a probe or the Scroll Lab never has to parse a log line.
/// </summary>
public readonly record struct RenderPaceSnapshot
{
    /// <summary>Fresh-publication presents / motion re-presents (cumulative).</summary>
    public long FreshPresents { get; init; }
    /// <inheritdoc cref="FreshPresents"/>
    public long MotionPresents { get; init; }
    /// <summary>Paced turns with work pending that presented nothing because the tick was already spent (cumulative).</summary>
    public long SkippedTicks { get; init; }
    /// <summary>Fresh publications that reached the glass one tick behind a motion re-present that had already taken the
    /// tick they landed in (cumulative).</summary>
    public long RaceHits { get; init; }
    /// <summary>Clock-paced turns whose present slot was still busy past the grace — the previous present missed its
    /// vblank and owned this one — and that presented nothing so the next tick presented on time (cumulative;
    /// RenderThread.CatchUpSkips, the SlotCatchUp policy). Each is also one <see cref="MissedMotionTicks"/>: the vblank the
    /// late frame already owned. 0 while no frame is late, and while frames do not fit the early phase (they wait).</summary>
    public long CatchUpSkips { get; init; }
    /// <summary>Compositor ticks the render thread woke for while motion was due but presented nothing on (cumulative) —
    /// the direct measure of the pacing cliff; 0 on a healthy run.</summary>
    public long MissedMotionTicks { get; init; }
    /// <summary>The display clock's delivered-tick sequence (0 without a clock).</summary>
    public long TickSeq { get; init; }
    /// <summary>Present-slot waits: count, summed ms, max ms (cumulative; max is since start).</summary>
    public long SlotWaitCount { get; init; }
    /// <inheritdoc cref="SlotWaitCount"/>
    public double SlotWaitSumMs { get; init; }
    /// <inheritdoc cref="SlotWaitCount"/>
    public double SlotWaitMaxMs { get; init; }
    /// <summary>The compositor clock's measured beat (ms; 0 until measured / no clock).</summary>
    public double ClockPeriodMs { get; init; }
    /// <summary>Double-tick returns the clock filter swallowed / accepted ticks dropped between window slots (cumulative).</summary>
    public long ClockIgnoredReturns { get; init; }
    /// <inheritdoc cref="ClockIgnoredReturns"/>
    public long ClockSlotDrops { get; init; }
    /// <summary>The clock is decimating onto a slower window's period.</summary>
    public bool ClockDecimating { get; init; }
    /// <summary>The present-queue depth currently in force (DXGI maximum frame latency).</summary>
    public int PresentQueueDepth { get; init; }
    /// <summary>The adaptive GPU governor's smoothed GPU execution (ms), whether it is engaged, and the host's last wait kind.</summary>
    public double GovernorEmaMs { get; init; }
    /// <inheritdoc cref="GovernorEmaMs"/>
    public bool GovernorEngaged { get; init; }
    /// <inheritdoc cref="GovernorEmaMs"/>
    public HostWaitKind LastWaitKind { get; init; }
    /// <summary>Latest retired whole-frame GPU execution for the main swapchain (ms; 0 when unsupported).</summary>
    public double GpuExecutionMs { get; init; }
}

/// <summary>The host's half of the render thread's always-on <c>[render.pace]</c> line, sampled once a second on the
/// render thread (cross-thread reads of plain fields). Also read once per clock-paced present for its
/// <c>GpuExecutionMs</c> alone — the GPU share of the frame cost the render thread's slot catch-up policy
/// (<c>SlotCatchUp</c>) smooths; a readonly record struct returned by value, so that per-present read never allocates.
/// The other fields stay diagnostics only, never a pacing input.</summary>
public readonly record struct RenderPaceHostState(
    double GovernorEmaMs, bool GovernorEngaged, HostWaitKind LastWaitKind, double GpuExecutionMs, int PresentQueueDepth,
    long SlotLivenessTimeouts = 0);
