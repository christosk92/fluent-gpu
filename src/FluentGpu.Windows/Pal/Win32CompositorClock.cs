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
/// <b>A filtered clock, not a capability probe.</b> Where the export exists it is available for the whole session —
/// full stop. The raw returns are noisy (measured: bursts of 9-21 sub-millisecond returns around monitor/DPI changes,
/// an occasional failed wait mid-topology-change, and a DWM-GLOBAL rate that can be 120 Hz while the window sits on a
/// 50 Hz panel), so <see cref="CompositorTickFilter"/> — not a latch — absorbs them: a return closer than half a period
/// to the last tick is a double tick and is ignored; a failed wait degrades to ONE synthesized tick the waiter sleeps
/// to; accepted stamps snap onto a constant lattice and resync only past 2 ms of drift; and when the window's monitor
/// is slower than the compositor beat, ticks are decimated onto the WINDOW's period so the host produces one frame per
/// panel refresh instead of 2.4 (the 2:3 judder case). The ONLY permanent latch left is a missing export (the wait
/// throws): <see cref="IsAvailable"/> goes false, the thread parks, and the host keeps its wall-clock timeout until
/// <see cref="Reprobe"/>. This is a runtime model by design — the engine does not gate behavior behind environment
/// switches.
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
    /// compositor can hold the thread; a timeout is NOT a tick and never signals the event. It also bounds the timed
    /// park a synthesized tick sleeps for.</summary>
    private const uint WaitTimeoutMs = 100;
    /// <summary>Burst length worth one log line. Shorter runs of ignored returns are ordinary double-tick noise; the
    /// 9-21 return storms this clock was rebuilt for are the ones a user log must be able to show.</summary>
    private const int FastBurstLogMin = 8;
    /// <summary>Past this many ignored returns in one burst the waiter stops re-entering the export at once and parks
    /// ~1 ms between waits. The documented storms (9-21 returns around a monitor / DPI change) never reach it.</summary>
    internal const int SpinGuardBurst = 32;
    /// <summary>A burst this long means the export is not blocking at all (measured on a second instance: ~2,300 returns
    /// every 2 ms, a whole core spent re-waiting). Such a clock is no vblank reference: the waiter switches to
    /// SYNTHESIZED ticks (<see cref="EnterSynthesized"/>) — a high-resolution timer at the window's period — so the host
    /// keeps display-rate pacing without the spin, and probes the export again every <see cref="SynthProbeMs"/>.</summary>
    internal const int NoBlockBurst = 1000;
    /// <summary>The blocked-time check: over each window of this length (ms) with at least <see cref="NoBlockMinCalls"/>
    /// waits, a waiter that spent less than 1/<see cref="NoBlockRatio"/> of the window INSIDE the export is not being
    /// paced by it (a real compositor clock keeps it blocked nearly the whole time, storms included).</summary>
    private const int NoBlockWindowMs = 250;
    private const int NoBlockMinCalls = 60;
    private const int NoBlockRatio = 10;
    /// <summary>While synthesizing, how often (ms) the waiter tries the real export once to see whether it blocks again.</summary>
    private const int SynthProbeMs = 2000;

    // synthesized mode (waiter thread only): the export does not block, so a high-resolution timer beats the lattice
    private bool _synth;
    private long _synthNextQpc, _synthProbeAtQpc;
    private HANDLE _synthTimer;
    private EventWaitHandle? _synthTimerWait;
    private bool _synthTimerUnavailable;
    private readonly long _refreshHintQpc;

    private readonly HANDLE _tickEvent;                 // auto-reset: one signal per compositor tick
    private readonly AutoResetEvent _armGate = new(false);   // parks the waiter thread while disarmed
    private readonly Thread _thread;
    private readonly Func<uint>? _waitForClock; // injected blocking clock primitive for deterministic backend tests
    private readonly Func<long> _timestamp;     // injectable QPC read — the same seam, so tests need no real sleeping
    private readonly long _qpcFrequency;
    private readonly CompositorTickFilter _filter;   // waiter-thread only
    private int _armed;
    private readonly object _renderGate = new();
    private RenderSubscription? _renderSubscription;
    private int _renderArmed;
    private long _tickSeq;                      // bumped once per delivered tick (waiter thread writes, host reads)
    private long _tickQpc;                      // Stopwatch instant of the latest tick
    private long _windowPeriodQpc;              // UI thread writes (Volatile), waiter thread reads
    private long _appliedWindowPeriodQpc;       // waiter-thread only: the last value pushed into the filter
    private long _measuredPeriodQpc;            // published: Volatile-written by the waiter, Volatile-read by the host
    private long _latticePeriodQpc;             // published: the period the published stamps are actually spaced by
    private long _slotDrops;                    // published: accepted ticks dropped because they were not window slots
    private long _ignoredReturns;                // published: CompositorTickFilter.IgnoredCount mirror (always-on, not just bursts ≥ FastBurstLogMin)
    private bool _loggedDecimating;             // waiter-thread only: last mode we logged
    private volatile bool _decimating;          // published mirror of the filter's mode
    private volatile bool _reprobeRequested;    // set by any thread, consumed by the waiter at the top of its loop
    private volatile bool _unavailable;
    private volatile bool _disposed;

    /// <param name="waitForClock">Injected blocking wait (tests); null uses the real dcomp export.</param>
    /// <param name="timestamp">Injected timestamp source (tests); null uses <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="refreshPeriodHintQpc">The window's <see cref="DisplayInfo.RefreshPeriodQpc"/> at construction: both
    /// the filter's lattice fallback before it has measured itself, and the initial window period for decimation
    /// (<see cref="SetWindowPeriodQpc"/> keeps it current afterwards). 0 = unknown.</param>
    /// <param name="qpcFrequency">Ticks per second of the timestamp domain; 0 = <see cref="Stopwatch.Frequency"/>.
    /// Tests pair it with <paramref name="timestamp"/> to get a round, fully synthetic clock.</param>
    internal Win32CompositorClock(Func<uint>? waitForClock = null, Func<long>? timestamp = null,
                                  long refreshPeriodHintQpc = 0, long qpcFrequency = 0)
    {
        _waitForClock = waitForClock;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _qpcFrequency = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency;
        _filter = new CompositorTickFilter(_qpcFrequency, refreshPeriodHintQpc);
        // Seed the window period from the hint and let the loop's "push on change" path apply it: one code path keeps
        // the filter's window period current, whether it came from the constructor or from a later monitor hop.
        _windowPeriodQpc = refreshPeriodHintQpc > 0 ? refreshPeriodHintQpc : 0;
        _refreshHintQpc = refreshPeriodHintQpc > 0 ? refreshPeriodHintQpc : 0;
        _tickEvent = waitForClock is null ? CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null) : HANDLE.NULL;
        _thread = new Thread(Loop) { IsBackground = true, Name = "fgpu-vblank" };
        // Above normal: the tick is a phase signal with a hard deadline (it is worthless one refresh late), and the
        // thread does nothing but sleep between ticks. Not time-critical — this must never outrank the UI loop it serves.
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    /// <summary>The auto-reset event signalled once per compositor tick while armed. <c>HANDLE.NULL</c> if the event
    /// could not be created — callers must treat that like <see cref="IsAvailable"/> being false.</summary>
    internal HANDLE TickEvent => _tickEvent;

    /// <summary>False only when the dcomp export is missing (the wait threw) — the one permanent latch in the model,
    /// cleared by <see cref="Reprobe"/>. A failed wait, an instant-return burst, or any amount of jitter no longer
    /// takes the clock away: the filter absorbs them, so where the export exists this stays true for the session. The
    /// caller leaves the tick out of its handle set only while it is false, and its wall-clock timeout paces the loop.</summary>
    internal bool IsAvailable => !_unavailable && (_tickEvent != HANDLE.NULL || _waitForClock is not null);

    /// <summary>Delivered-tick count (monotone) and the Stopwatch instant of the latest one. Read on the UI thread; the
    /// seq is written AFTER the stamp on the waiter thread, so a reader that observes a new seq also observes its stamp.
    /// While decimating, the stamp is the WINDOW lattice point (evenly spaced by the panel's period), not the raw
    /// compositor instant that triggered it — that even spacing is the point of decimating.</summary>
    internal long TickSeq => Volatile.Read(ref _tickSeq);
    internal long TickQpc => Volatile.Read(ref _tickQpc);

    /// <summary>The MEDIAN (never a mean — a single bogus fast/slow delta must not drag the estimate) of the last nine
    /// consecutive accepted tick deltas, Stopwatch domain: the COMPOSITOR's beat, which on a mixed-refresh desktop is
    /// not necessarily the window's panel rate. 0 until at least three deltas have been observed. Diagnostics and the
    /// decimation decision consume it; the host still paces on ticks themselves
    /// (<see cref="TickEvent"/>/<see cref="TickSeq"/>), never on this derived number.</summary>
    internal long MeasuredRefreshPeriodQpc => Volatile.Read(ref _measuredPeriodQpc);

    /// <summary>The period published stamps are actually spaced by: the window's period while
    /// <see cref="Decimating"/>, else the measured compositor beat. Diagnostics only.</summary>
    internal long LatticePeriodQpc => Volatile.Read(ref _latticePeriodQpc);

    /// <summary>True while the compositor beats faster than the window's monitor and ticks are being decimated onto the
    /// window's period. Diagnostics only.</summary>
    internal bool Decimating => _decimating;

    /// <summary>Accepted compositor ticks that were NOT published because they fell between window slots. Diagnostics
    /// only; monotone for the life of the clock.</summary>
    internal long SlotDrops => Volatile.Read(ref _slotDrops);

    /// <summary>Every <c>Ignored</c> (double-tick) return the filter has swallowed for the life of the clock, not just
    /// the bursts long enough to earn their own <c>[compositor-clock] fast-burst</c> log line (<see cref="FastBurstLogMin"/>
    /// = 8). Diagnostics only; monotone.</summary>
    internal long IgnoredReturns => Volatile.Read(ref _ignoredReturns);

    /// <summary>Tell the clock which monitor period the WINDOW is on (<see cref="DisplayInfo.RefreshPeriodQpc"/>;
    /// 0 = unknown). UI thread, allocation-free, safe on the per-frame path: it only stores the value, and the waiter
    /// picks it up on its next iteration. When the window's period is at least 1.25x the compositor beat, the waiter
    /// starts decimating so the host produces one evenly-spaced frame per panel refresh instead of 2.4 of them.</summary>
    internal void SetWindowPeriodQpc(long qpc) => Volatile.Write(ref _windowPeriodQpc, qpc > 0 ? qpc : 0);

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

    internal IRenderDisplayClock CreateRenderSubscription()
    {
        lock (_renderGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_renderSubscription is not null)
                throw new InvalidOperationException("A window supports one render display-clock subscriber.");
            return _renderSubscription = new RenderSubscription(this);
        }
    }

    private sealed class RenderSubscription(Win32CompositorClock owner) : IRenderDisplayClock
    {
        internal readonly AutoResetEvent Event = new(false);
        private bool _disposed;
        public WaitHandle Tick => Event;
        public bool IsAvailable => !_disposed && !owner._disposed && owner.IsAvailable;
        public long TickSeq => owner.TickSeq;
        public long TickQpc => owner.TickQpc;
        public long MeasuredPeriodQpc => owner.MeasuredRefreshPeriodQpc;
        public long IgnoredReturns => owner.IgnoredReturns;
        public long SlotDrops => owner.SlotDrops;
        public bool Decimating => owner.Decimating;
        public void SetActive(bool active)
        {
            lock (owner._renderGate)
            {
                if (_disposed || owner._disposed) return;
                if (Interlocked.Exchange(ref owner._renderArmed, active ? 1 : 0) == (active ? 1 : 0)) return;
                if (active) owner._armGate.Set();
                else Event.Reset();
            }
        }
        public void Dispose()
        {
            lock (owner._renderGate)
            {
                if (_disposed) return;
                _disposed = true;
                Volatile.Write(ref owner._renderArmed, 0);
                owner._renderSubscription = null;
                Event.Dispose();
            }
        }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            if (_reprobeRequested) ConsumeReprobe();

            if (_unavailable || (Volatile.Read(ref _armed) == 0 && Volatile.Read(ref _renderArmed) == 0))
            {
                // Forget the lattice before parking: the first delta after an idle stretch is a multi-second hole, and
                // a hole is not a period sample, a drift measurement, or a slot.
                _filter.Reset();
                PublishFilterState();
                _blockWindowStart = 0;   // a parked stretch is not wait time
                _armGate.WaitOne();   // 0% CPU while the app is idle, minimized, or not display-paced
                continue;
            }

            long window = Volatile.Read(ref _windowPeriodQpc);
            if (window != _appliedWindowPeriodQpc)
            {
                _appliedWindowPeriodQpc = window;
                _filter.SetWindowPeriodQpc(window);
                LogLattice();
            }

            if (_synth) { if (!SynthesizedTurn()) break; continue; }

            uint r;
            long waitStart = _timestamp();
            try
            {
                // count=0/handles=null: wait on the compositor clock alone. The timeout is liveness only.
                r = _waitForClock is null ? DCompositionWaitForCompositorClock(0, null, WaitTimeoutMs) : _waitForClock();
            }
            catch (EntryPointNotFoundException) { MarkUnavailable("export-missing"); continue; }
            catch (DllNotFoundException) { MarkUnavailable("export-missing"); continue; }
            // A wait that throws anything else is equally unusable here, and letting it escape would kill the waiter
            // thread outright (a background thread's unhandled exception ends the process) — latch instead, reprobably.
            catch (Exception) { MarkUnavailable("wait-threw"); continue; }

            if (_disposed) break;
            long now = _timestamp();
            if (NoteBlocking(waitStart, now)) { EnterSynthesized(now); continue; }
            // Snapshot the two streaks the filter is about to clear — the transition logs below describe what ENDED.
            int burst = _filter.FastBurst;
            long burstSpan = _filter.FastBurstSpanQpc;
            int synthesized = _filter.SynthesizedRun;

            TickVerdict verdict = _filter.Observe(r, now, out long publishQpc);
            if (verdict == TickVerdict.Ignored)
            {
                // a double tick / burst return: not a vblank. A short storm re-waits at once; a long one must not spin.
                int run = _filter.FastBurst;
                if (run >= NoBlockBurst)
                {
                    Diag.Line($"[compositor-clock] fast-burst n={run} spanMs={Ms(_filter.FastBurstSpanQpc):0.0} (no-block)");
                    EnterSynthesized(now);
                    continue;
                }
                if (run >= SpinGuardBurst)
                {
                    _armGate.WaitOne(1);   // park ~1 ms; the gate still cancels it (dispose / reprobe / re-arm)
                    if (_disposed) break;
                }
                continue;
            }
            // A synthesized tick does not end a burst (the storm is still open), so the line waits for the real end.
            if (burst >= FastBurstLogMin && verdict != TickVerdict.SynthesizedTick)
                Diag.Line($"[compositor-clock] fast-burst n={burst} spanMs={Ms(burstSpan):0.0}");
            if (verdict == TickVerdict.Timeout) continue;   // liveness only, never a tick
            if (verdict == TickVerdict.HardLatch) { MarkUnavailable("export-missing"); continue; }

            if (verdict == TickVerdict.SynthesizedTick)
            {
                if (_filter.SynthesizedRun == 1)
                    Diag.Line($"[compositor-clock] synthesized reason=wait-failed period={Ms(_filter.PeriodQpc):0.00}");
                // The lattice says where the vblank is; sleep to it rather than free-running on failed waits. The arm
                // gate doubles as the cancellation for that sleep (dispose / reprobe / a subscriber arming).
                bool signalled = _armGate.WaitOne(MsUntil(publishQpc, now));
                if (_disposed) break;
                if (signalled && _reprobeRequested) continue;   // the lattice is about to be thrown away — publishing it would be a lie
            }
            else if (synthesized > 0)
            {
                Diag.Line($"[compositor-clock] hardware-restored after={synthesized}");
            }

            Publish(publishQpc);
        }
    }

    /// <summary>Stamp + seq + wake, in that order, for one published tick. In decimate mode most accepted ticks are
    /// dropped here: only the tick nearest each window-lattice point is published, and it is stamped with the lattice
    /// point itself so consumers see an evenly spaced panel-rate clock.</summary>
    private void Publish(long publishQpc)
    {
        if (!_filter.IsSlot(publishQpc, out long slotQpc))
        {
            Volatile.Write(ref _slotDrops, _slotDrops + 1);
            Diag.Count("clock", "slotDrops");
            return;
        }
        PublishFilterState();
        if (_filter.Decimating != _loggedDecimating) LogLattice();

        // Publish the tick: stamp first, then seq (release), then the event — a host that wakes on the event or
        // observes the new seq sees the stamp. A tick that lands while the host is mid-frame stays signalled
        // (auto-reset, consumed by the next wait), so no vblank is lost to a wake that arrived a little early.
        Volatile.Write(ref _tickQpc, slotQpc);
        Volatile.Write(ref _tickSeq, _tickSeq + 1);
        if (Volatile.Read(ref _armed) != 0 && _tickEvent != HANDLE.NULL) SetEvent(_tickEvent);
        // Each consumer owns its event: the UI cannot steal a render tick, or stop the shared waiter at idle.
        // Serialize only signal/dispose, never a wait or a frame; no handle can be recycled under Set().
        lock (_renderGate)
            if (_renderArmed != 0) _renderSubscription?.Event.Set();
    }

    /// <summary>Republish the waiter's derived state for UI-thread readers. Plain volatile stores of longs/bools — no
    /// allocation, no lock, and stale-by-one-tick is fine for every consumer (all diagnostics or coarse decisions).</summary>
    private void PublishFilterState()
    {
        Volatile.Write(ref _measuredPeriodQpc, _filter.MeasuredTickPeriodQpc);
        Volatile.Write(ref _latticePeriodQpc, _filter.PeriodQpc);
        Volatile.Write(ref _ignoredReturns, _filter.IgnoredCount);
        _decimating = _filter.Decimating;
    }

    /// <summary>Waiter thread: adopt a requested reprobe. The whole derived model (lattice phase, measured period,
    /// mode, slot phase) is invalid after a display topology change, so it is thrown away here rather than by the
    /// caller — <see cref="Reprobe"/> must not write fields the waiter owns unsynchronized.</summary>
    private void ConsumeReprobe()
    {
        _reprobeRequested = false;
        _synth = false;   // a new display topology: try the real export again
        _filter.Reset();
        _loggedDecimating = false;
        PublishFilterState();
        Diag.Line("[compositor-clock] reprobe");
    }

    /// <summary>The ONE permanent latch: the export does not exist in this process (a downlevel/stripped dcomp), which
    /// no retry can change. Everything else the wait can do — fail once, return instantly, return in a storm — is the
    /// filter's job, not a reason to give up the display's phase for the rest of the session. Logs via
    /// <see cref="Diag.Line"/> (the always-on channel: silent degradation is exactly what a user log must show) once
    /// per latch, never per tick. <see cref="Reprobe"/> clears it.</summary>
    /// <summary>The export does not block: beat the window's period with a high-resolution timer instead (waiter thread).</summary>
    private void EnterSynthesized(long nowQpc)
    {
        _synth = true;
        long period = SynthPeriodQpc();
        _synthNextQpc = nowQpc + period;
        _synthProbeAtQpc = nowQpc + _qpcFrequency * SynthProbeMs / 1000;
        _filter.Reset();
        Volatile.Write(ref _measuredPeriodQpc, 0);
        Volatile.Write(ref _latticePeriodQpc, period);
        _decimating = false;
        Diag.Line($"[compositor-clock] synthesized reason=no-block period={Ms(period):0.00}");
    }

    /// <summary>The synthesized beat: the window's monitor period, else the refresh hint, else 1/60 s.</summary>
    private long SynthPeriodQpc()
    {
        long w = Volatile.Read(ref _windowPeriodQpc);
        return w > 0 ? w : _refreshHintQpc > 0 ? _refreshHintQpc : _qpcFrequency / 60;
    }

    /// <summary>One synthesized turn: now and then probe the real export; else sleep to the next lattice point on the
    /// high-resolution timer (cancellable through the arm gate) and publish it. False = disposed.</summary>
    private bool SynthesizedTurn()
    {
        long now = _timestamp();
        long period = SynthPeriodQpc();
        if (now >= _synthProbeAtQpc)
        {
            _synthProbeAtQpc = now + _qpcFrequency * SynthProbeMs / 1000;
            uint pr;
            try { pr = _waitForClock is null ? DCompositionWaitForCompositorClock(0, null, WaitTimeoutMs) : _waitForClock(); }
            catch (Exception) { pr = WaitFailedResult; }
            long after = _timestamp();
            if (pr == 0 && after - now >= period / 2)
            {
                _synth = false;
                _filter.Reset();
                _blockWindowStart = 0;
                Diag.Line("[compositor-clock] hardware-restored reason=blocks-again");
                return !_disposed;
            }
            now = after;
        }
        SleepUntil(_synthNextQpc, now);
        if (_disposed) return false;
        if (_reprobeRequested || !_synth) return true;
        long tick = _synthNextQpc;
        now = _timestamp();
        _synthNextQpc += period;
        if (_synthNextQpc <= now) _synthNextQpc = now + period;   // a late wake re-phases instead of bursting to catch up
        Volatile.Write(ref _latticePeriodQpc, period);
        PublishTick(tick);
        return true;
    }

    private const uint WaitFailedResult = 0xFFFFFFFF;

    /// <summary>Sleep until <paramref name="targetQpc"/> on a high-resolution waitable timer (the default 15.6 ms timer
    /// resolution would halve a 120 Hz beat), or on the arm gate if no such timer exists. The arm gate cancels either.</summary>
    private void SleepUntil(long targetQpc, long nowQpc)
    {
        long delta = targetQpc - nowQpc;
        if (delta <= 0) return;
        if (_synthTimer == HANDLE.NULL && !_synthTimerUnavailable)
        {
            _synthTimer = CreateWaitableTimerExW(null, null, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */,
                0x0002 | 0x00100000 /* TIMER_MODIFY_STATE | SYNCHRONIZE */);
            if (_synthTimer == HANDLE.NULL) _synthTimerUnavailable = true;
            else _synthTimerWait = new EventWaitHandle(false, EventResetMode.AutoReset)
                { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle((nint)_synthTimer.Value, ownsHandle: false) };
        }
        if (_synthTimerWait is not null)
        {
            LARGE_INTEGER due = default;
            due.QuadPart = -(delta * 10_000_000 / _qpcFrequency);   // relative, 100 ns units
            if (due.QuadPart == 0) due.QuadPart = -1;
            if (SetWaitableTimer(_synthTimer, &due, 0, null, null, BOOL.FALSE))
            {
                WaitHandle.WaitAny([_synthTimerWait, _armGate]);
                return;
            }
        }
        _armGate.WaitOne(MsUntil(targetQpc, nowQpc));
    }

    /// <summary>Publish one tick as is (no slot / decimation filter): stamp, then seq, then the events.</summary>
    private void PublishTick(long qpc)
    {
        Volatile.Write(ref _tickQpc, qpc);
        Volatile.Write(ref _tickSeq, _tickSeq + 1);
        if (Volatile.Read(ref _armed) != 0 && _tickEvent != HANDLE.NULL) SetEvent(_tickEvent);
        lock (_renderGate)
            if (_renderArmed != 0) _renderSubscription?.Event.Set();
    }

    // the blocked-time window (waiter thread only)
    private long _blockWindowStart, _blockedQpc;
    private int _blockCalls;

    /// <summary>Account one wait (<paramref name="start"/> → <paramref name="end"/>) to the blocked-time window; true when a
    /// closed window shows the export is not blocking (see <see cref="NoBlockWindowMs"/>).</summary>
    private bool NoteBlocking(long start, long end)
    {
        if (_blockWindowStart == 0) { _blockWindowStart = start; _blockedQpc = 0; _blockCalls = 0; }
        _blockedQpc += end - start;
        _blockCalls++;
        long elapsed = end - _blockWindowStart;
        if (elapsed < _qpcFrequency * NoBlockWindowMs / 1000) return false;
        bool noBlock = _blockCalls >= NoBlockMinCalls && _blockedQpc * NoBlockRatio < elapsed;
        if (noBlock) Diag.Line($"[compositor-clock] no-block calls={_blockCalls} blockedMs={Ms(_blockedQpc):0.0} windowMs={Ms(elapsed):0.0}");
        _blockWindowStart = 0;
        return noBlock;
    }

    private void MarkUnavailable(string reason)
    {
        _unavailable = true;
        Volatile.Write(ref _armed, 0);
        lock (_renderGate) _renderSubscription?.Event.Set(); // wake render waiter to select its fallback
        Diag.Line($"[compositor-clock] unavailable reason={reason}");
    }

    /// <summary>Called on every display topology change (a monitor added/removed/reconfigured, or the window dragged
    /// onto another display). Two jobs: clear a missing-export latch so the next wait tries the export again, and tell
    /// the waiter to throw away a phase that now belongs to a display the window may no longer be on. Cross-thread
    /// safe by construction — it only sets flags and pokes the gate; the waiter does the resetting itself.</summary>
    internal void Reprobe()
    {
        if (_disposed) return;
        _unavailable = false;
        _reprobeRequested = true;
        _armGate.Set();   // unpark a latched/idle waiter so the reset (and the log line) happen promptly
    }

    private double Ms(long qpc) => qpc * 1000.0 / _qpcFrequency;

    /// <summary>Milliseconds to sleep before publishing a synthesized tick, clamped to [1, <see cref="WaitTimeoutMs"/>]:
    /// never a spin (0) and never longer than one liveness window, so dispose stays bounded.</summary>
    private int MsUntil(long targetQpc, long nowQpc)
    {
        long delta = targetQpc - nowQpc;
        if (delta <= 0) return 1;
        long ms = delta * 1000 / _qpcFrequency;
        if (ms < 1) return 1;
        return ms > WaitTimeoutMs ? (int)WaitTimeoutMs : (int)ms;
    }

    /// <summary>One line whenever the pacing shape changes — the window's period or the decimate/pass mode. Transitions
    /// only: this is the evidence trail for "why is this app producing 50 fps on a 120 Hz compositor".</summary>
    private void LogLattice()
    {
        _loggedDecimating = _filter.Decimating;
        Diag.Line($"[compositor-clock] lattice window={Ms(_filter.WindowPeriodQpc):0.00} tick={Ms(_filter.MeasuredTickPeriodQpc):0.00} mode={(_loggedDecimating ? "decimate" : "pass")}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _armed, 0);
        lock (_renderGate)
        {
            Volatile.Write(ref _renderArmed, 0);
            _renderSubscription?.Event.Set(); // owner teardown must not leave a consumer asleep
        }
        _armGate.Set();               // unpark so the loop can observe _disposed
        // Bounded: an in-flight compositor wait returns within WaitTimeoutMs, and a synthesized-tick park is clamped to
        // the same window. If the join still fails the thread is background, so it cannot hold the process — but the
        // event handle is then deliberately LEAKED rather than closed under a live SetEvent (closing a handle another
        // thread is about to signal risks hitting a recycled one). The arm gate is disposed on the same condition and
        // for the same reason: the parked waiter is inside _armGate.WaitOne(), and disposing it under that wait raises
        // on a background thread, which is a process kill.
        if (!_thread.Join((int)WaitTimeoutMs * 4)) return;
        if (_tickEvent != HANDLE.NULL) CloseHandle(_tickEvent);
        _synthTimerWait?.Dispose();   // does not own the handle
        if (_synthTimer != HANDLE.NULL) CloseHandle(_synthTimer);
        _armGate.Dispose();
    }
}
