using FluentGpu.Pal;

namespace FluentGpu.Hosting;

/// <summary>
/// Pure, static, unit-testable helpers that build <see cref="FrameClock"/> — the ONE target time shared by
/// DirectManipulation's per-frame <c>Update</c>, the scroll kernel's tick, and (Phase 6) the render-thread fling
/// lease (scroll-v3-plan-2026-08-17.md §5.1). No allocation, no I/O, no seam dependency — <c>AppHost</c> calls this
/// on the UI thread once per <c>RunFrame</c>; the render thread will call the same statics for the lease (§6.2).
///
/// <para>The lattice model: a display's vblanks land at <c>anchor + k·refresh</c> for integer <c>k</c>, where
/// <paramref name="anchorQpc"/> is a real OS-attested vblank stamp (DXGI <c>SyncQPCTime</c> — see
/// <see cref="FluentGpu.Rhi.PresentStats.SyncQpc"/>) and <paramref name="refreshQpc"/> the measured period between
/// them. <see cref="Snap"/> rounds an arbitrary QPC instant onto that lattice; <see cref="NextVBlank"/> finds the
/// next (strictly future) lattice point.</para>
/// </summary>
public static class RefreshLattice
{
    /// <summary>
    /// The nearest lattice point (<c>anchorQpc + k·refreshQpc</c>) to <paramref name="nowQpc"/>, floored at
    /// <paramref name="floorQpc"/> so the result never rewinds frame-to-frame (a backwards step would hand the
    /// kernel a negative <c>DtSec</c> and freeze the motion — ported verbatim from the pre-scroll-v3
    /// <c>AppHost.QuantizedFrameSec</c>).
    ///
    /// <para>NEAREST, not next: a zero-mean correction that removes per-frame sampling jitter without shifting the
    /// resampler's effective latency (rounding forward to the expected present would routinely land the target past
    /// the newest contact sample and trip the no-extrapolation clamp).</para>
    ///
    /// <para>Falls back to the raw <paramref name="nowQpc"/> (floored) whenever the lattice cannot be trusted: no
    /// anchor yet (<paramref name="anchorQpc"/> ≤ 0), a non-positive <paramref name="refreshQpc"/>, a
    /// <paramref name="nowQpc"/> that precedes the anchor, or an anchor so old that accumulated period error would
    /// have drifted the phase (128 refreshes — at 120 Hz, a 0.1% period error is a whole refresh of drift after
    /// ~1.4 s). Better an honest jittery instant than a confidently wrong one.</para>
    /// </summary>
    public static long Snap(long anchorQpc, long refreshQpc, long nowQpc, long floorQpc)
    {
        if (anchorQpc <= 0 || refreshQpc <= 0)
            return nowQpc > floorQpc ? nowQpc : floorQpc;

        long delta = nowQpc - anchorQpc;
        if (delta < 0 || delta > refreshQpc * 128)
            return nowQpc > floorQpc ? nowQpc : floorQpc;

        long k = (delta + refreshQpc / 2) / refreshQpc;   // nearest lattice point (delta >= 0 here, no floor/trunc split)
        long snapped = anchorQpc + k * refreshQpc;
        return snapped < floorQpc ? floorQpc : snapped;   // never rewind
    }

    /// <summary>
    /// The next STRICTLY FUTURE lattice point (<c>anchorQpc + k·refreshQpc</c>) at or after <paramref name="nowQpc"/>
    /// — the predicted vblank a frame produced "now" would first become eligible to show at. Falls back to
    /// <paramref name="nowQpc"/> itself when the lattice cannot be trusted (no anchor / non-positive refresh), same
    /// honesty rule as <see cref="Snap"/>.
    /// </summary>
    public static long NextVBlank(long anchorQpc, long refreshQpc, long nowQpc)
    {
        if (anchorQpc <= 0 || refreshQpc <= 0) return nowQpc;

        long delta = nowQpc - anchorQpc;
        long k = delta <= 0 ? 0 : (delta + refreshQpc - 1) / refreshQpc;   // ceil to the next lattice point >= now
        long next = anchorQpc + k * refreshQpc;
        if (next <= nowQpc) next += refreshQpc;   // guarantee strictly future even when now lands exactly on a point
        return next;
    }

    /// <summary>
    /// Builds a real-window <see cref="FrameClock"/> (scroll-v3-plan §5.1): <c>FrameQpc</c> = <see cref="Snap"/>
    /// against <paramref name="lastFrameQpc"/> (the caller's running monotonicity floor — the caller is responsible
    /// for threading its own previous result back in); <c>PresentQpc</c> = <see cref="NextVBlank"/> PLUS one more
    /// refresh, because the swapchain is created with <c>SetMaximumFrameLatency(1)</c>
    /// (<c>D3D12Device.cs</c>): a frame produced right after the present-ack for the PREVIOUS frame shows at the
    /// vblank AFTER next, not the very next one.
    /// </summary>
    public static FrameClock Build(long anchorQpc, long refreshQpc, long nowQpc, long lastFrameQpc, ulong seq,
        bool latticeValid, bool unpaced)
    {
        long frameQpc = Snap(anchorQpc, refreshQpc, nowQpc, lastFrameQpc);
        long presentQpc = NextVBlank(anchorQpc, refreshQpc, nowQpc) + refreshQpc;

        FrameClockFlags flags = FrameClockFlags.None;
        if (latticeValid) flags |= FrameClockFlags.LatticeValid;
        if (unpaced) flags |= FrameClockFlags.Unpaced;
        return new FrameClock(frameQpc, presentQpc, refreshQpc, nowQpc, seq, flags);
    }

    /// <summary>
    /// Builds a deterministic headless <see cref="FrameClock"/>: no real present/vblank exists, so
    /// <paramref name="frameQpc"/> is the caller's own accumulated <c>FixedFrameTimeSource</c> clock (in QPC ticks)
    /// rather than a QPC read, and <c>PresentQpc</c> is simply one refresh period ahead of it. Deterministic —
    /// gates stay bit-reproducible across runs/machines.
    /// </summary>
    public static FrameClock Headless(long frameQpc, long refreshQpc, ulong seq)
        => new(frameQpc, frameQpc + refreshQpc, refreshQpc, frameQpc, seq, FrameClockFlags.Headless);
}
