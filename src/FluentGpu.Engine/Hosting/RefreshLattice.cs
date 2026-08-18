using System.Diagnostics;
using FluentGpu.Pal;

namespace FluentGpu.Hosting;

/// <summary>
/// Pure, static, unit-testable helpers that build <see cref="FrameClock"/> — the ONE target time shared by
/// DirectManipulation's per-frame <c>Update</c>, the scroll kernel's tick, and (Phase 6) the render-thread fling
/// lease (scroll-v3-plan-2026-08-17.md §5.1, §13.2). No allocation, no I/O, no seam dependency — <c>AppHost</c> calls
/// this on the UI thread once per <c>RunFrame</c>; the render thread will call the same statics for the lease (§6.2).
/// The frame time is the compositor tick's own vblank instant when the platform display clock is live; there is no
/// lattice to snap onto because the tick IS the vblank.
/// </summary>
public static class RefreshLattice
{
    /// <summary>
    /// Builds a real-window <see cref="FrameClock"/> for one produced frame (scroll-v3-plan §5.1 / §13.2). With a live
    /// display clock the frame belongs to a compositor tick: <c>FrameQpc</c> is that tick's vblank instant
    /// (<paramref name="tickQpc"/>) — exact, monotone by construction, one per vblank. Without one (software pace) it is
    /// the frame's own start instant. Either way <c>FrameQpc</c> never rewinds past <paramref name="lastFrameQpc"/>, and
    /// <c>PresentQpc = FrameQpc + 2·refresh</c>: a frame produced inside interval N is presented before vblank N+1 and
    /// composited by DWM at N+2 (<c>SetMaximumFrameLatency(1)</c>, D3D12Device.cs). <c>Unpaced</c> = no display clock.
    /// </summary>
    public static FrameClock Build(bool tickAvailable, long tickQpc, long refreshQpc, long nowQpc, long lastFrameQpc, ulong seq)
    {
        // A tick older than two refresh periods is stale (the clock was parked while idle/ambient): the frame is not
        // "for" that vblank — stamp it with now, like a software-paced frame, and let the next tick take over.
        bool onTick = tickAvailable && tickQpc > 0 && refreshQpc > 0 && nowQpc - tickQpc < refreshQpc * 2;
        long frameQpc = onTick ? tickQpc : nowQpc;
        if (frameQpc < lastFrameQpc) frameQpc = lastFrameQpc;   // never rewind
        long presentQpc = frameQpc + 2 * (refreshQpc > 0 ? refreshQpc : Stopwatch.Frequency / 60);
        FrameClockFlags flags = FrameClockFlags.None;
        if (onTick) flags |= FrameClockFlags.LatticeValid;
        if (!tickAvailable) flags |= FrameClockFlags.Unpaced;
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
