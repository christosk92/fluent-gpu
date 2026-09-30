using System.Diagnostics;
using FluentGpu.Pal;

namespace FluentGpu.Hosting;

/// <summary>
/// Pure, static, unit-testable helpers that build <see cref="FrameClock"/> — the ONE target time shared by
/// DirectManipulation's per-frame <c>Update</c>, the scroll frame step (<c>AppHost.RunScrollFrame</c>), and the
/// render-thread <c>ScrollPoser</c>'s present-time prediction. No allocation, no I/O, no seam dependency — <c>AppHost</c> calls
/// this on the UI thread once per <c>RunFrame</c>.
/// The frame time is the compositor tick's own vblank instant when the platform display clock is live; there is no
/// lattice to snap onto because the tick IS the vblank.
/// </summary>
public static class RefreshLattice
{
    /// <summary>
    /// Builds a real-window <see cref="FrameClock"/> for one produced frame. With a live
    /// display clock the frame belongs to a compositor tick: <c>FrameQpc</c> is that tick's vblank instant
    /// (<paramref name="tickQpc"/>) — exact, monotone by construction, one per vblank. A STALE tick (the clock was
    /// parked while idle/ambient and hasn't refreshed in over two periods) is re-snapped onto the same lattice instead
    /// of stamped with `now` off-grid — Chromium's <c>SnappedToNextTick</c> move: <c>FrameQpc</c> becomes the latest
    /// lattice point at-or-before `now` (<c>tickQpc + floor((now − tickQpc) / refresh)·refresh</c>), so a frame
    /// produced after an idle wake still lands exactly `refresh` apart from its predecessor instead of at an arbitrary
    /// wake-up instant. Only with NO display clock at all (software pace) is <c>FrameQpc</c> the frame's own start
    /// instant. Either way <c>FrameQpc</c> never rewinds past <paramref name="lastFrameQpc"/>, and
    /// <c>PresentQpc = FrameQpc + (1 + <paramref name="maxFrameLatency"/>)·refresh</c>: a frame produced inside interval N
    /// is presented before vblank N+1, and the swapchain may hold <paramref name="maxFrameLatency"/> queued presents
    /// before DWM composites this one (<c>SetMaximumFrameLatency</c>, D3D12Device.cs — the backend's
    /// <c>IGpuDevice.MaxFrameLatency</c>). The default 1 reproduces the historical <c>+2·refresh</c> prediction.
    /// <c>Unpaced</c> = no display clock.
    /// </summary>
    public static FrameClock Build(bool tickAvailable, long tickQpc, long refreshQpc, long nowQpc, long lastFrameQpc, ulong seq,
                                   int maxFrameLatency = 1)
    {
        // A tick older than two refresh periods is stale (the clock was parked while idle/ambient): the frame is not
        // "for" that vblank, so it doesn't get LatticeValid. The two here is a PARKED-CLOCK heuristic (how long a
        // tick may go unrefreshed before it stops describing a vblank), NOT the present-latency term below — deeper
        // queued presents never make a stale tick fresher.
        bool onTick = tickAvailable && tickQpc > 0 && refreshQpc > 0 && nowQpc - tickQpc < refreshQpc * 2;
        long frameQpc;
        if (onTick) frameQpc = tickQpc;
        else if (tickAvailable && tickQpc > 0 && refreshQpc > 0)
            // Stale but a real lattice exists: re-snap onto it (Chromium's SnappedToNextTick) instead of stamping
            // `now` off-grid, so an idle-wake frame still lands evenly spaced from its predecessor. Integer division
            // floors — the latest lattice point at-or-before `now`.
            frameQpc = tickQpc + (nowQpc - tickQpc) / refreshQpc * refreshQpc;
        else frameQpc = nowQpc;   // no clock at all: software pace, stamp the frame's own start instant
        if (frameQpc < lastFrameQpc) frameQpc = lastFrameQpc;   // never rewind
        long refresh = refreshQpc > 0 ? refreshQpc : Stopwatch.Frequency / 60;
        long presentQpc = frameQpc + (1 + Math.Clamp(maxFrameLatency, 1, 3)) * refresh;
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
