using System;
using System.Diagnostics;
using System.Threading;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The UI loop's display clock: one background thread parked in <c>DCompositionWaitForCompositorClock</c>, republishing
/// each compositor tick as a plain auto-reset event the message-loop wait can include in its handle set.
///
/// <b>Why a clock at all.</b> Moving <c>Present()</c> to the render thread removed the UI thread's only vblank
/// reference — in the sync path the present block itself paced the loop. What replaced it was a wall-clock timer, and a
/// wall-clock cap can bound how OFTEN the loop produces but never WHEN: ~7.9 ms production against an 8.333 ms grid
/// slips about 4% of slots, which is 115 fps on a 120 Hz panel. It also does not scale — the same constant is wrong at
/// 60, 144 and 240 Hz. The compositor clock is the display's own phase, delivered by DWM.
///
/// <b>Why <c>DCompositionWaitForCompositorClock</c> and not <c>IDXGIOutput::WaitForVBlank</c>.</b> It is a flat dcomp
/// export, so it entangles nothing with the render thread's COM confinement (no <c>ComPtr</c> crosses a thread here, no
/// output enumeration, no re-enumeration when the window is dragged to another monitor — DWM tracks that itself), and it
/// takes a timeout, so the waiter stays live and cancellable instead of blocking forever on a stalled display.
///
/// <b>Exactly one waiter.</b> The export forbids concurrent callers, hence one owned thread and no public wait entry
/// point: callers observe ticks through <see cref="TickEvent"/> only.
///
/// <b>Capability probe, not a flag.</b> The export is not usable everywhere (remote sessions in particular). The FIRST
/// failure — <c>WAIT_FAILED</c>, a missing export, OR a sustained run of sub-millisecond successes (the clock returns
/// without waiting, which would free-spin the UI loop) — marks it permanently unavailable and parks the thread for
/// good; the host then keeps its wall-clock timeout. This is a runtime probe by design — the engine does not gate new
/// behavior behind environment switches.
///
/// <b>Power.</b> The thread is armed only for the duration of a display-paced wait and parks on
/// <see cref="_armGate"/> otherwise, so an idle or minimized app pays nothing: no compositor wait runs and no event is
/// signalled. <see cref="Arm"/>/<see cref="Disarm"/> are allocation-free (an interlocked write plus an event signal), so
/// they are safe on the per-frame wait path.
/// </summary>
internal sealed unsafe class Win32CompositorClock : IDisposable
{
    /// <summary>Liveness timeout for one compositor wait (ms). Bounds how long teardown can block and how long a wedged
    /// compositor can hold the thread; a timeout is NOT a tick and never signals the event.</summary>
    private const uint WaitTimeoutMs = 100;
    /// <summary>Consecutive sub-millisecond successes before the clock is ruled out. One coalesced tick after Arm is
    /// legal; a remote DWM that never blocks is not.</summary>
    private const int FastStreakLimit = 16;

    // Not in the TerraFX static-import surface (the same reason Win32Window declares its own pair).
    private const uint WAIT_TIMEOUT = 0x00000102, WAIT_FAILED = 0xFFFFFFFF;

    private readonly HANDLE _tickEvent;                 // auto-reset: one signal per compositor tick
    private readonly AutoResetEvent _armGate = new(false);   // parks the waiter thread while disarmed
    private readonly Thread _thread;
    private int _armed;
    private int _fastStreak;                    // waiter-thread only: consecutive sub-millisecond successes
    private volatile bool _unavailable;
    private volatile bool _disposed;

    internal Win32CompositorClock()
    {
        _tickEvent = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);
        _thread = new Thread(Loop) { IsBackground = true, Name = "fgpu-vblank" };
        // Above normal: the tick is a phase signal with a hard deadline (it is worthless one refresh late), and the
        // thread does nothing but sleep between ticks. Not time-critical — this must never outrank the UI loop it serves.
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    /// <summary>The auto-reset event signalled once per compositor tick while armed. <c>HANDLE.NULL</c> if the event
    /// could not be created — callers must treat that like <see cref="IsAvailable"/> being false.</summary>
    internal HANDLE TickEvent => _tickEvent;

    /// <summary>False once a compositor wait has failed (or the export is missing): permanently, for this process. The
    /// caller then leaves the tick out of its handle set and its wall-clock timeout paces the loop as before.</summary>
    internal bool IsAvailable => !_unavailable && _tickEvent != HANDLE.NULL;

    /// <summary>Start delivering ticks. Idempotent and allocation-free; safe to call on the per-frame wait path. Clears
    /// any tick left over from the previous armed window so a stale signal cannot short-circuit this wait.</summary>
    internal void Arm()
    {
        if (_disposed || _unavailable) return;
        if (Interlocked.Exchange(ref _armed, 1) == 1) return;
        if (_tickEvent != HANDLE.NULL) ResetEvent(_tickEvent);
        _armGate.Set();   // unpark the waiter
    }

    /// <summary>Stop delivering ticks (the waiter parks after its current wait returns). Idempotent, allocation-free.</summary>
    internal void Disarm() => Interlocked.Exchange(ref _armed, 0);

    private void Loop()
    {
        while (!_disposed)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                _fastStreak = 0;
                _armGate.WaitOne();   // 0% CPU while the app is idle, minimized, or not display-paced
                continue;
            }

            uint r;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                // count=0/handles=null: wait on the compositor clock alone. The timeout is liveness only.
                r = DCompositionWaitForCompositorClock(0, null, WaitTimeoutMs);
            }
            catch
            {
                // A missing export (EntryPointNotFound) or a failed load — same verdict as a failed wait.
                MarkUnavailable();
                continue;
            }

            if (_disposed) break;
            if (r == WAIT_TIMEOUT) { _fastStreak = 0; continue; }   // no tick: liveness only, never signal
            if (r == WAIT_FAILED) { MarkUnavailable(); continue; }
            // Remote sessions (RDP / Shadow) can succeed WITHOUT waiting: the export returns WAIT_OBJECT_0 in
            // microseconds, which would republish ticks at CPU speed and free-spin the UI loop. A sustained run of
            // sub-millisecond successes is the same verdict as WAIT_FAILED — the clock is not a phase reference.
            long dt = Stopwatch.GetTimestamp() - t0;
            if (dt < Stopwatch.Frequency / 1000)
            {
                if (++_fastStreak >= FastStreakLimit) { MarkUnavailable(); continue; }
            }
            else _fastStreak = 0;
            // Republish the tick. Disarm may have raced us here; the arm-side ResetEvent discards the stale signal.
            if (Volatile.Read(ref _armed) != 0 && _tickEvent != HANDLE.NULL) SetEvent(_tickEvent);
        }
    }

    /// <summary>Permanent, one-way: the clock is off for the rest of the process and the thread parks. Never retried —
    /// a retry loop against an unsupported export is a spin, and the fallback (a wall-clock wait) is correct, just less
    /// well phased.</summary>
    private void MarkUnavailable()
    {
        _unavailable = true;
        Volatile.Write(ref _armed, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _armed, 0);
        _armGate.Set();               // unpark so the loop can observe _disposed
        // Bounded: an in-flight compositor wait returns within WaitTimeoutMs. If the join still fails the thread is
        // background, so it cannot hold the process — but the event handle is then deliberately LEAKED rather than
        // closed under a live SetEvent (closing a handle another thread is about to signal risks hitting a recycled one).
        // The arm gate is disposed on the same condition and for the same reason: the parked waiter is inside
        // _armGate.WaitOne(), and disposing it under that wait raises on a background thread, which is a process kill.
        if (!_thread.Join((int)WaitTimeoutMs * 4)) return;
        if (_tickEvent != HANDLE.NULL) CloseHandle(_tickEvent);
        _armGate.Dispose();
    }
}
