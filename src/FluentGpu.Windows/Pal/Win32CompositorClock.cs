using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;
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
/// <b>Ticks are the production clock.</b> Each tick bumps <see cref="TickSeq"/> and stamps <see cref="TickQpc"/> (the
/// vblank instant, Stopwatch domain) BEFORE the event is signalled, so the host can gate production to one frame per
/// tick and stamp the frame with the exact vblank it was produced for. The clock stays armed across frames while the
/// host keeps asking for display pacing (<see cref="Arm"/> is idempotent) — a tick that lands while the host is
/// producing is counted and its event stays signalled, so the next wait returns immediately instead of losing a vblank —
/// and parks only when the host explicitly <see cref="Disarm"/>s (idle / ambient / minimized): no compositor wait runs
/// and no event is signalled while the app is quiet.
/// </summary>
internal sealed unsafe class Win32CompositorClock : IDisposable
{
    /// <summary>Liveness timeout for one compositor wait (ms). Bounds how long teardown can block and how long a wedged
    /// compositor can hold the thread; a timeout is NOT a tick and never signals the event.</summary>
    private const uint WaitTimeoutMs = 100;
    /// <summary>Consecutive sub-millisecond successes before the clock is ruled out. One coalesced tick after Arm is
    /// legal; a remote DWM that never blocks is not.</summary>
    private const int FastStreakLimit = 16;
    /// <summary>Consecutive WAIT_FAILED returns before the clock is ruled out. A single transient failure is exactly
    /// what a display topology change (a monitor added/removed/reconfigured mid-wait) can legitimately produce once —
    /// latching off the whole process on ONE such failure is the bug <see cref="Reprobe"/> and this streak both exist
    /// to fix. A wedged/removed compositor fails on every subsequent wait, so 3 consecutive failures still latches
    /// promptly.</summary>
    private const int WaitFailedStreakLimit = 3;
    /// <summary>Ring size for the empirically-measured tick-period median (<see cref="MeasuredRefreshPeriodQpc"/>).
    /// Odd, so the median is a single middle sample — no averaging of two middles.</summary>
    private const int PeriodRingSize = 9;
    /// <summary>Minimum samples in the ring before publishing a median — a 1- or 2-sample "median" is really just
    /// the newest value, not yet a robust estimate.</summary>
    private const int PeriodRingMinSamples = 3;

    // Not in the TerraFX static-import surface (the same reason Win32Window declares its own pair).
    private const uint WAIT_TIMEOUT = 0x00000102, WAIT_FAILED = 0xFFFFFFFF;

    private readonly HANDLE _tickEvent;                 // auto-reset: one signal per compositor tick
    private readonly AutoResetEvent _armGate = new(false);   // parks the waiter thread while disarmed
    private readonly Thread _thread;
    private int _armed;
    private int _fastStreak;                    // waiter-thread only: consecutive sub-millisecond successes
    private int _waitFailedStreak;               // waiter-thread only: consecutive WAIT_FAILED returns
    private long _tickSeq;                      // bumped once per delivered tick (waiter thread writes, host reads)
    private long _tickQpc;                      // Stopwatch instant of the latest tick
    // ── empirically-measured tick period (diagnostics cross-check only — never the pacing source) ────────────────────
    private readonly long[] _periodRing = new long[PeriodRingSize];   // waiter-thread only
    private int _periodCount, _periodHead;                             // waiter-thread only
    private long _lastTickQpcForPeriod;                                 // waiter-thread only: previous tick's stamp
    private long _measuredPeriodQpc;             // published: Volatile-written by the waiter, Volatile-read by the host
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

    /// <summary>Delivered-tick count (monotone) and the Stopwatch instant of the latest one. Read on the UI thread; the
    /// seq is written AFTER the stamp on the waiter thread, so a reader that observes a new seq also observes its stamp.</summary>
    internal long TickSeq => Volatile.Read(ref _tickSeq);
    internal long TickQpc => Volatile.Read(ref _tickQpc);

    /// <summary>The MEDIAN (never a mean — a single bogus fast/slow delta must not drag the estimate) of the last
    /// <see cref="PeriodRingSize"/> consecutive tick-to-tick deltas, Stopwatch domain. 0 until at least
    /// <see cref="PeriodRingMinSamples"/> deltas have been observed. This is a DIAGNOSTICS cross-check against the
    /// panel's reported rate (<see cref="FluentGpu.Pal.Windows.DisplayInfo"/>) — never the pacing source; the host
    /// paces on ticks themselves (<see cref="TickEvent"/>/<see cref="TickSeq"/>), not on this derived number.</summary>
    internal long MeasuredRefreshPeriodQpc => Volatile.Read(ref _measuredPeriodQpc);

    /// <summary>Start (or keep) delivering ticks. Idempotent and allocation-free; safe to call on the per-frame wait path.
    /// A tick signalled since the last wait is deliberately kept: it is a real vblank the host has not produced for.</summary>
    internal void Arm()
    {
        if (_disposed || _unavailable) return;
        if (Interlocked.Exchange(ref _armed, 1) == 1) return;   // already armed: keep any pending tick signal — it is a real vblank the host has not produced for yet
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
                // A missing export (EntryPointNotFound) or a failed load — same verdict as a failed wait, but NOT
                // streak-gated: unlike a topology-change WAIT_FAILED, a missing export can never succeed on retry, so
                // there is nothing to wait out.
                MarkUnavailable("export-missing", 1);
                continue;
            }

            if (_disposed) break;
            if (r == WAIT_TIMEOUT) { _fastStreak = 0; _waitFailedStreak = 0; continue; }   // no tick: liveness only, never signal
            if (r == WAIT_FAILED)
            {
                // A SINGLE WAIT_FAILED is exactly what a display topology change (a monitor added/removed/
                // reconfigured mid-wait) can legitimately produce once — latch only after a genuine streak, so that
                // transient case survives without ever going unavailable. Not reset on any branch below: only real
                // progress (a timeout or a delivered tick) proves the export is still live.
                if (++_waitFailedStreak >= WaitFailedStreakLimit) MarkUnavailable("wait-failed", _waitFailedStreak);
                continue;
            }
            _waitFailedStreak = 0;
            // Remote sessions (RDP / Shadow) can succeed WITHOUT waiting: the export returns WAIT_OBJECT_0 in
            // microseconds, which would republish ticks at CPU speed and free-spin the UI loop. A sustained run of
            // sub-millisecond successes is the same verdict as WAIT_FAILED — the clock is not a phase reference.
            long dt = Stopwatch.GetTimestamp() - t0;
            if (dt < Stopwatch.Frequency / 1000)
            {
                if (++_fastStreak >= FastStreakLimit) { MarkUnavailable("fast-streak", _fastStreak); continue; }
            }
            else _fastStreak = 0;
            // Publish the tick: stamp first, then seq (release), then the event — a host that wakes on the event or
            // observes the new seq sees the stamp. A tick that lands while the host is mid-frame stays signalled
            // (auto-reset, consumed by the next wait), so no vblank is lost to a wake that arrived a little early.
            long tickQpc = Stopwatch.GetTimestamp();
            RecordPeriodSample(tickQpc);
            Volatile.Write(ref _tickQpc, tickQpc);
            Volatile.Write(ref _tickSeq, _tickSeq + 1);
            if (Volatile.Read(ref _armed) != 0 && _tickEvent != HANDLE.NULL) SetEvent(_tickEvent);
        }
    }

    /// <summary>Waiter-thread-only: fold one new tick timestamp into the empirically-measured period ring and
    /// republish the median (see <see cref="MeasuredRefreshPeriodQpc"/>). WAIT_TIMEOUT returns never reach here (they
    /// are not ticks), so the delta sequence has legal holes across a busy stretch — exactly why a median, not a mean,
    /// is published.</summary>
    private void RecordPeriodSample(long tickQpc)
    {
        long previous = _lastTickQpcForPeriod;
        _lastTickQpcForPeriod = tickQpc;
        if (previous == 0) return;   // first tick since Arm/Reprobe: no delta yet
        long delta = tickQpc - previous;
        if (delta <= 0) return;   // clock went backwards or coalesced to nothing — not a legal sample

        if (_periodCount < PeriodRingSize) _periodRing[_periodCount++] = delta;
        else { _periodRing[_periodHead] = delta; _periodHead = (_periodHead + 1) % PeriodRingSize; }
        if (_periodCount < PeriodRingMinSamples) return;

        // Small N (<= 9): a stackalloc + insertion sort, same shape as the wheel gap-ring median in Win32Platform.cs.
        Span<long> sorted = stackalloc long[_periodCount];
        for (int i = 0; i < _periodCount; i++) sorted[i] = _periodRing[i];
        for (int i = 1; i < sorted.Length; i++)
        {
            long v = sorted[i];
            int j = i - 1;
            while (j >= 0 && sorted[j] > v) { sorted[j + 1] = sorted[j]; j--; }
            sorted[j + 1] = v;
        }
        Volatile.Write(ref _measuredPeriodQpc, sorted[sorted.Length / 2]);
    }

    /// <summary>Permanent, one-way UNTIL <see cref="Reprobe"/>: the clock is off for the rest of the process (or
    /// until reprobed) and the thread parks. Never internally retried — a retry loop against an unsupported export is
    /// a spin, and the fallback (a wall-clock wait) is correct, just less well phased. Logs the verdict via
    /// <see cref="Diag.Line"/> (the always-on channel — this is exactly the kind of silent-degradation event a user
    /// log must be able to show) once per latch, never per tick.</summary>
    private void MarkUnavailable(string reason, int streak)
    {
        _unavailable = true;
        Volatile.Write(ref _armed, 0);
        Diag.Line($"[compositor-clock] unavailable reason={reason} streak={streak}");
    }

    /// <summary>Give the compositor clock one more chance after a display topology change (a monitor
    /// added/removed/reconfigured, or a window dragged onto a new display) — precisely the moment a compositor wait
    /// can legitimately fail once (see <see cref="WaitFailedStreakLimit"/>'s doc). A pure flag clear: the waiter
    /// thread is never torn down on <see cref="MarkUnavailable(string,int)"/> (<see cref="Loop"/> keeps running,
    /// parked in <see cref="_armGate"/> once <c>_armed</c> is cleared), so the next <see cref="Arm"/> resumes probing
    /// with a fresh streak. A no-op if the clock was never marked unavailable (the common case — most reprobes are
    /// speculative, called on every display change whether or not the clock had actually latched off).</summary>
    internal void Reprobe()
    {
        if (!_unavailable) return;
        _unavailable = false;
        // Cross-thread writes into fields the waiter thread otherwise owns unsynchronized: safe because the waiter is
        // guaranteed parked in _armGate.WaitOne() by the time _unavailable observably went true (MarkUnavailable
        // clears _armed synchronously before the loop can next inspect it), so there is no concurrent writer here —
        // Volatile.Write only to guarantee this reset is visible to the waiter once Arm() unparks it.
        Volatile.Write(ref _fastStreak, 0);
        Volatile.Write(ref _waitFailedStreak, 0);
        Diag.Line("[compositor-clock] reprobe");
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
