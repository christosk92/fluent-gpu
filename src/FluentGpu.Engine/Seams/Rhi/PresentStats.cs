namespace FluentGpu.Rhi;

/// <summary>
/// OS-attested present/compositor statistics — the only vblank-granular cadence truth the process can obtain about
/// itself. Sampled by the backend at its present chokepoint (DXGI <c>GetFrameStatistics</c>) and, once a second, from
/// the desktop compositor (<c>DwmGetCompositionTimingInfo</c>). A pure POD snapshot: the host copies it, never holds a
/// reference, and nothing here allocates.
///
/// WHY this exists rather than an in-app timer: a QPC stamp taken after <c>Present()</c> returns is
/// SUBMIT-confirmed — it says the call came back, not that a scanout began. The difference is exactly the quantity a
/// "high FPS, bad feel" investigation is about, so cadence metrics are computed against <see cref="RefreshPeriodQpc"/>
/// and cross-checked against <see cref="PresentRefreshCount"/>.
///
/// HONEST LIMITS, which every consumer must carry:
/// <list type="bullet">
/// <item>This is vblank granularity, NOT photons. Panel scanout position, panel response and backlight are invisible.
///   A pixel at row Y on a top-down panel lights (Y/height) x refreshPeriod after the vblank.</item>
/// <item><c>GetFrameStatistics</c> is meaningful for flip-model / fullscreen swapchains only, and its
///   <c>PresentCount</c> is documented as NOT the number of <c>Present()</c> calls — a correlation must tolerate holes.</item>
/// <item>The DWM frame counters are main-monitor-global (the per-window form was removed in Windows 8.1), and are
///   valid only as DELTAS from a second call. <see cref="Valid"/> stays false until then.</item>
/// <item>Under VRR the fixed-period model collapses entirely; <see cref="RefreshPeriodQpc"/> then describes the
///   current period only, and fixed-period metrics must be reported as not-measured rather than computed.</item>
/// </list>
/// </summary>
public readonly record struct PresentStats
{
    /// <summary>False when the backend has no present statistics at all (headless), when DXGI reported
    /// <c>DXGI_ERROR_FRAME_STATISTICS_DISJOINT</c> (the first call, and after every mode change), or before the second
    /// DWM sample has established a delta. Consumers MUST treat false as not-measured, never as zeroes.</summary>
    public bool Valid { get; init; }

    /// <summary>DXGI <c>PresentCount</c> at the last present. Not a count of <c>Present()</c> calls.</summary>
    public uint PresentCount { get; init; }
    /// <summary>DXGI <c>PresentRefreshCount</c>: the vblank ordinal at which the last present was displayed. Deltas of
    /// this against the expected ordinal are the vblank-ATTESTED missed-slot count.</summary>
    public uint PresentRefreshCount { get; init; }
    /// <summary>DXGI <c>SyncRefreshCount</c>: the vblank ordinal of the most recent vblank paired with
    /// <see cref="SyncQpc"/>.</summary>
    public uint SyncRefreshCount { get; init; }
    /// <summary>DXGI <c>SyncQPCTime</c>: the QPC of that vblank — the same clock domain as
    /// <c>Stopwatch.GetTimestamp()</c>, so it joins in-app stamps with no conversion.</summary>
    public long SyncQpc { get; init; }

    /// <summary>DWM <c>qpcRefreshPeriod</c>: the MEASURED refresh period in QPC ticks. Every fixed-period cadence
    /// metric is denominated in this, never in a nominal <c>GetDeviceCaps(VREFRESH)</c> Hz (which returns 0/1 on some
    /// drivers and is only re-sampled on a client-size change).</summary>
    public long RefreshPeriodQpc { get; init; }
    /// <summary>DWM <c>qpcVBlank</c>: the QPC of the last vblank the compositor observed. Preferred over
    /// <c>qpcCompose</c> — its measured jitter is roughly a third.</summary>
    public long VBlankQpc { get; init; }

    /// <summary>DWM <c>cFramesDropped</c> delta since the previous sample: frames the compositor had to drop because
    /// they arrived late. "We were late."</summary>
    public uint DwmFramesDroppedDelta { get; init; }
    /// <summary>DWM <c>cFramesMissed</c> delta: composition cycles that found no new frame at all. "We starved it."</summary>
    public uint DwmFramesMissedDelta { get; init; }
    /// <summary>DWM <c>cFramesLate</c> delta: the compositor's own lateness — a confound, not our fault.</summary>
    public uint DwmFramesLateDelta { get; init; }

    /// <summary>Wall time (ms) the last present spent BLOCKED on the frame-latency waitable. The swapchain is created
    /// waitable with a maximum frame latency of 1, so a sustained non-trivial wait here is compositor backpressure —
    /// distinct from GPU work, which is the fence wait.</summary>
    public double LatencyWaitMs { get; init; }
}
