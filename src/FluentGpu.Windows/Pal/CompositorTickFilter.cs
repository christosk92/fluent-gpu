using System;

namespace FluentGpu.Pal.Windows;

/// <summary>What one return from the compositor wait means. Produced by <see cref="CompositorTickFilter.Observe"/>.</summary>
internal enum TickVerdict : byte
{
    /// <summary>A real vblank: publish <c>publishQpc</c> (a lattice-snapped stamp) as the tick.</summary>
    Tick,
    /// <summary>The wait failed; the lattice carries the phase instead. The caller sleeps until <c>publishQpc</c> and
    /// publishes it as a tick — a software-paced beat, not a latch.</summary>
    SynthesizedTick,
    /// <summary>The liveness timeout elapsed. Not a tick, never published; proves only that the export is still alive.</summary>
    Timeout,
    /// <summary>A success that cannot be a distinct vblank (it landed closer than half a period to the last accepted
    /// tick). Swallowed: no publish, no lattice movement, no sample.</summary>
    Ignored,
    /// <summary>Reserved for the ONE permanent latch the model allows — a missing dcomp export, which the clock
    /// detects itself (the wait throws, so the filter never sees the return). The filter never returns this; the
    /// member exists so the clock and the filter speak one vocabulary.</summary>
    HardLatch,
}

/// <summary>
/// The pure decision layer of the compositor clock: it turns the raw returns of
/// <c>DCompositionWaitForCompositorClock</c> into a clean, constant-interval tick lattice. No P/Invoke, no
/// <c>Stopwatch</c> read, no allocation, no thread affinity beyond "the waiter thread owns one instance" — every entry
/// takes the caller's timestamp, so the whole policy is unit-testable against a synthetic clock.
///
/// <b>Why this exists.</b> The raw export is not a clean beat. Measured on real hardware it bursts 9-21 sub-millisecond
/// returns around monitor/DPI changes (and occasionally for no visible reason), it can fail once mid-topology-change,
/// and its clock is DWM-GLOBAL: the window can sit on a 50 Hz panel while the compositor beats at 120 Hz. Treating any
/// of those as "no compositor clock" (the old fast-streak latch) cost the whole session its vblank phase; treating the
/// 120 Hz beat as the window's rate produces 2.4x the frames the panel can show, which is 2:3 judder plus wasted GPU.
///
/// <b>The model</b> (the same conclusion Gecko's <c>D3DVsyncSource</c>, Chromium's <c>DelayBasedTimeSource</c>, Zed's
/// <c>vsync.rs</c> and makepad's win32 beat all reached):
/// <list type="bullet">
/// <item>A return closer than half a period to the last accepted tick is a DOUBLE TICK, not a vblank — ignored. This
/// is also what swallows the sub-millisecond bursts, without ever latching the clock off.</item>
/// <item>A failed wait degrades to ONE synthesized tick on the lattice — the caller sleeps to it. Log once, keep going.</item>
/// <item>Accepted stamps SNAP to a synthesized constant lattice (<c>prev + period</c>) while they stay within 2 ms of
/// it; a clean constant interval is smoother than the jittery raw instant. Past that (or after a gap of more than two
/// periods — a parked clock, a sleeping monitor) the lattice RESYNCS onto the observed instant.</item>
/// <item>The period is the MEDIAN of the last nine accepted deltas, never a mean, so one bogus delta cannot drag it.</item>
/// <item>When the window's monitor is slower than the compositor's beat, ticks are DECIMATED onto the window's own
/// period, so the host produces one frame per panel refresh, evenly spaced.</item>
/// </list>
///
/// <b>The two lattices.</b> The TICK lattice always runs at the measured tick period: it stamps and it decides what is
/// a double tick. The WINDOW (slot) lattice runs at the window's period and only exists while decimating; it decides
/// WHICH accepted ticks become published slots and stamps them at exact window-period spacing (see
/// <see cref="IsSlot"/>). In pass-through mode every tick is a slot with its own stamp.
///
/// <b>Deliberate detail: the ignore window never comes from the hint.</b> Half of the MEASURED period is the double-tick
/// threshold; before three samples exist the threshold is a flat <see cref="MinSeparationDivisor"/> floor (2 ms - no
/// display refreshes above 500 Hz). It must NOT fall back to the hint, because the hint is the WINDOW's period, which
/// may legitimately be slower than the compositor beat we are filtering: a 20 ms hint would ignore every 8.33 ms tick
/// and the clock could never measure itself out of the hole. The hint is only ever a lattice-advance fallback.
///
/// <b>Degenerate clocks.</b> A session whose wait always returns instantly (some remote/RDP stacks) can no longer latch
/// the clock off. It is bounded instead: with a measured period in hand every burst return is ignored and the filter
/// publishes nothing; from a cold start the 2 ms floor caps acceptance at 500 Hz. That is the deliberate trade for
/// never losing vblank phase for a whole session over one transient burst.
/// </summary>
internal sealed class CompositorTickFilter
{
    /// <summary>Ring size for the measured tick-period median. Odd, so the median is a single middle sample.</summary>
    private const int PeriodRingSize = 9;
    /// <summary>Minimum samples before publishing a median - a 1- or 2-sample "median" is just the newest value.</summary>
    private const int PeriodRingMinSamples = 3;
    /// <summary>Snap window (fraction of a second): |now - lattice| within 2 ms keeps the constant interval.</summary>
    private const int DriftDivisor = 500;
    /// <summary>Lower bound for a legal period sample (1 ms). GPUI's vsync work saw a 29 us "period"; nothing that fast
    /// is a refresh interval.</summary>
    private const int MinSampleDivisor = 1000;
    /// <summary>Upper bound for a legal period sample (100 ms) - the same bound the wait's liveness timeout imposes.</summary>
    private const int MaxSampleDivisor = 10;
    /// <summary>Hard floor for the double-tick window (2 ms): no shipping display refreshes above 500 Hz, so two
    /// returns closer than this are never distinct vblanks - the rule that holds before any period is measured.</summary>
    private const int MinSeparationDivisor = 500;
    /// <summary>Fallback lattice period when nothing is known: 1/60 s.</summary>
    private const int DefaultPeriodDivisor = 60;
    /// <summary>Decimate when the window period is at least 1.25x the tick period (as 5/4, in integer math). A stated
    /// decision, not a tuned constant: 1.2 (a 100 Hz window on a 120 Hz beat) is close enough to pass through - the
    /// engine would drop one frame in six for nothing - while 1.25 already means every fifth beat is wasted.</summary>
    private const int DecimateNumerator = 5, DecimateDenominator = 4;
    /// <summary>A mode flip needs the same verdict on two consecutive accepted ticks. A legal hole in the delta ring
    /// (the wait timed out, the app was parked) can double ONE median; two agreeing ticks cannot both be that hole.</summary>
    private const int ModeSwitchConfirmations = 2;

    private const uint WaitTimeout = 0x00000102, WaitFailed = 0xFFFFFFFF;

    private readonly long _qpcFrequency;
    private readonly long _hintPeriodQpc;      // the window's DisplayInfo period at construction; 0 = unknown
    private readonly long _defaultPeriodQpc;   // 1/60 s
    private readonly long _driftQpc, _minSampleQpc, _maxSampleQpc, _minSeparationQpc;

    private readonly long[] _periodRing = new long[PeriodRingSize];
    private int _periodCount, _periodHead;
    private long _measuredPeriodQpc;

    private long _windowPeriodQpc;             // 0 = unknown

    private bool _hasLattice;
    private long _nextLatticeQpc;
    private bool _hasAccepted;
    private long _lastAcceptedQpc;             // RAW observed instant of the last accepted tick (not its snapped stamp)
    // The double-tick reference: an IDEALIZED lattice slot that advances by exactly one measured period per accepted
    // tick, deliberately never the raw/publish instant of that tick. A tick's own OBSERVATION can land late (the waiter
    // thread got scheduled late, a backstop fired a beat late) without that lateness being a genuine cadence change;
    // if the ignore window were measured from that late raw instant (as it used to be), the NEXT tick — genuinely on
    // the original hardware cadence — could land inside half a period of it and be wrongly swallowed as a duplicate
    // (one late return dropping the next on-time tick). Resets exactly where the display-facing lattice resets (the
    // first tick since Reset, or a real parked-clock gap over two periods) and otherwise never resyncs to raw.
    private long _expectedSlotQpc;
    private long _ignoredCount;                 // always-on: every Ignored verdict, burst or not (see IgnoredCount)

    private int _fastBurst;
    private long _burstStartQpc, _burstEndQpc;
    private int _synthesizedRun;

    private bool _decimating;
    private int _modeDisagreements;
    private bool _hasSlot;
    private long _nextSlotQpc;

    /// <param name="qpcFrequency">Ticks per second of the caller's timestamp domain (<c>Stopwatch.Frequency</c> in
    /// production; a round 1e6 in tests). Non-positive falls back to 10 MHz so no threshold can collapse to zero.</param>
    /// <param name="refreshPeriodHintQpc">The window's <see cref="DisplayInfo.RefreshPeriodQpc"/> at construction, used
    /// ONLY to advance the lattice before the clock has measured itself (0 = unknown, use 1/60 s). Never the
    /// double-tick threshold - see the class remarks.</param>
    public CompositorTickFilter(long qpcFrequency, long refreshPeriodHintQpc)
    {
        _qpcFrequency = qpcFrequency > 0 ? qpcFrequency : 10_000_000;
        _hintPeriodQpc = refreshPeriodHintQpc > 0 ? refreshPeriodHintQpc : 0;
        _defaultPeriodQpc = _qpcFrequency / DefaultPeriodDivisor;
        _driftQpc = _qpcFrequency / DriftDivisor;
        _minSampleQpc = _qpcFrequency / MinSampleDivisor;
        _maxSampleQpc = _qpcFrequency / MaxSampleDivisor;
        _minSeparationQpc = _qpcFrequency / MinSeparationDivisor;
    }

    /// <summary>The lattice period the CONSUMER is paced at: the window's period while decimating, else the tick
    /// period (measured, else the hint, else 1/60 s).</summary>
    public long PeriodQpc => _decimating && _windowPeriodQpc > 0 ? _windowPeriodQpc : TickPeriodQpc;

    /// <summary>The period the TICK lattice advances by - always the compositor's own beat, never the window's, even
    /// while decimating (the beat is what stamping and double-tick detection are about).</summary>
    public long TickPeriodQpc =>
        _measuredPeriodQpc > 0 ? _measuredPeriodQpc : _hintPeriodQpc > 0 ? _hintPeriodQpc : _defaultPeriodQpc;

    /// <summary>Median of the last <see cref="PeriodRingSize"/> accepted tick-to-tick deltas; 0 until
    /// <see cref="PeriodRingMinSamples"/> legal samples exist. Synthesized ticks never feed it (their delta is the
    /// period by construction - a circular sample).</summary>
    public long MeasuredTickPeriodQpc => _measuredPeriodQpc;

    /// <summary>The window's monitor period as last pushed by the UI thread; 0 = unknown.</summary>
    public long WindowPeriodQpc => _windowPeriodQpc;

    /// <summary>True while ticks are being decimated onto the window lattice (the window's monitor is slower than the
    /// compositor beat).</summary>
    public bool Decimating => _decimating;

    /// <summary>Consecutive <see cref="TickVerdict.Ignored"/> returns in the CURRENT burst (0 when no burst is open).
    /// Read it BEFORE <see cref="Observe"/> to log a burst that the next accepted tick or timeout is about to end.</summary>
    public int FastBurst => _fastBurst;

    /// <summary>Span of the current burst (first to last ignored return), 0 when no burst is open. Same read-before-
    /// Observe contract as <see cref="FastBurst"/>.</summary>
    public long FastBurstSpanQpc => _fastBurst == 0 ? 0 : _burstEndQpc - _burstStartQpc;

    /// <summary>Consecutive synthesized ticks in the current run (0 once real hardware ticks resume).</summary>
    public int SynthesizedRun => _synthesizedRun;

    /// <summary>Total <see cref="TickVerdict.Ignored"/> returns for the life of this filter (since construction, NOT
    /// reset by <see cref="Reset"/> — a reprobe/idle-park does not make the session's double-tick noise disappear).
    /// Always-on, unlike <see cref="FastBurst"/>'s logged-burst threshold: this is the counter a diagnostics reader
    /// checks for the single ignored returns that never reach a burst log line.</summary>
    public long IgnoredCount => _ignoredCount;

    /// <summary>Push the window's monitor period (0 = unknown). Re-anchors the slot lattice and cancels a half-made
    /// mode decision, because both are expressed in the period that just changed.</summary>
    public void SetWindowPeriodQpc(long qpc)
    {
        if (qpc < 0) qpc = 0;
        if (qpc == _windowPeriodQpc) return;
        _windowPeriodQpc = qpc;
        _modeDisagreements = 0;
        _hasSlot = false;
        if (qpc == 0) _decimating = false;   // no window period, no decimation target
    }

    /// <summary>Forget everything derived from observations - lattice, ring, streaks, slot state, mode - while KEEPING
    /// the window period and the construction hint. Called on reprobe (a display topology change invalidates the
    /// phase) and before the waiter parks (so the first delta after an idle stretch is not a multi-second hole).</summary>
    public void Reset()
    {
        _periodCount = 0;
        _periodHead = 0;
        _measuredPeriodQpc = 0;
        _hasLattice = false;
        _nextLatticeQpc = 0;
        _hasAccepted = false;
        _lastAcceptedQpc = 0;
        _expectedSlotQpc = 0;
        _fastBurst = 0;
        _burstStartQpc = 0;
        _burstEndQpc = 0;
        _synthesizedRun = 0;
        _decimating = false;
        _modeDisagreements = 0;
        _hasSlot = false;
        _nextSlotQpc = 0;
    }

    /// <summary>Classify one return from the compositor wait. Allocation-free and branch-cheap: this runs on every
    /// return, including the sub-millisecond storms.</summary>
    /// <param name="waitResult">The raw wait return (<c>0x102</c> = timeout, <c>0xFFFFFFFF</c> = failed, anything else
    /// = success).</param>
    /// <param name="nowQpc">The caller's timestamp taken immediately after the wait returned.</param>
    /// <param name="publishQpc">The instant to publish as the tick - lattice-snapped for <see cref="TickVerdict.Tick"/>,
    /// the next lattice point (in the future - sleep to it) for <see cref="TickVerdict.SynthesizedTick"/>, 0 otherwise.</param>
    public TickVerdict Observe(uint waitResult, long nowQpc, out long publishQpc)
    {
        publishQpc = 0;

        if (waitResult == WaitTimeout)
        {
            // Liveness only. It proves the export is alive, so it ends a burst; it is NOT a vblank, so the lattice,
            // the ring and the synthesized run are all left exactly as they are.
            EndBurst();
            return TickVerdict.Timeout;
        }

        if (waitResult == WaitFailed)
        {
            // One failed wait is what a monitor added/removed/reconfigured mid-wait produces. The phase we already
            // measured is still the best estimate of where the next vblank is, so beat the lattice ourselves and let
            // the caller sleep to it. Never a latch, however long the run lasts.
            long period = TickPeriodQpc;
            // With a lattice the next point is the estimate (never in the past: a failure that arrived late is
            // published now); with no lattice at all there is no phase to sleep to, so the beat starts NOW and the
            // following synthesized ticks pace themselves one period apart from it.
            long synthesized = _hasLattice ? Math.Max(_nextLatticeQpc, nowQpc)
                             : _hasAccepted ? Math.Max(_lastAcceptedQpc + period, nowQpc)
                             : nowQpc;
            _nextLatticeQpc = synthesized + period;
            _hasLattice = true;
            // The synthesized point IS the assumed vblank: recording it keeps the next real return one period away
            // instead of looking like a multi-period gap that would force a pointless resync.
            _lastAcceptedQpc = synthesized;
            _expectedSlotQpc = synthesized;   // a synthesized tick IS its own ideal slot — no raw jitter to guard against
            _hasAccepted = true;
            _synthesizedRun++;
            publishQpc = synthesized;
            return TickVerdict.SynthesizedTick;
        }

        // ── success ───────────────────────────────────────────────────────────────────────────────────────────────
        // Measured against the IDEALIZED slot (_expectedSlotQpc), never the raw instant of the last accepted return
        // (item D). A late OBSERVATION of a real tick must not make the genuinely next, on-time tick look like its
        // duplicate — see _expectedSlotQpc's remarks.
        if (_hasAccepted && nowQpc - _expectedSlotQpc < DoubleTickWindowQpc)
        {
            if (_fastBurst == 0) _burstStartQpc = nowQpc;
            _burstEndQpc = nowQpc;
            _fastBurst++;
            _ignoredCount++;
            return TickVerdict.Ignored;
        }

        long tickPeriod = TickPeriodQpc;
        long publish;
        bool isFirstTick = !_hasLattice;
        bool isResyncGap = !isFirstTick && _hasAccepted && nowQpc - _lastAcceptedQpc > 2 * tickPeriod;
        if (isFirstTick)
        {
            publish = nowQpc;                                   // first tick since Reset: the lattice starts here
        }
        else if (isResyncGap)
        {
            publish = nowQpc;                                   // parked clock / sleeping monitor: the phase is gone
        }
        else
        {
            long drift = nowQpc - _nextLatticeQpc;
            if (drift < 0) drift = -drift;
            // Both arms of the design's threshold pair snap - |drift| <= 500 us is the exact hit and |drift| <= 2 ms
            // is still "close enough that a constant interval reads smoother" - so one comparison expresses both; the
            // wider of the two is the one that decides.
            publish = drift <= _driftQpc ? _nextLatticeQpc : nowQpc;
        }

        // Advance the idealized slot along the IDEAL lattice, REGARDLESS of how this tick's own publish/raw instant came
        // out (unsnapped/late included) — only a first tick or a genuine parked-clock gap resets it to raw.
        //  • Before the beat is measured, tickPeriod is only the window HINT (20 ms on a 50 Hz panel against an 8.3 ms
        //    compositor beat): an ideal slot advanced by it would race AHEAD of reality and swallow every real beat as a
        //    "duplicate", so the beat would never be measured and the slower-panel decimation would never engage. There
        //    the slot advances one hint period, clamped to the raw instant.
        //  • Once measured, the slot advances by the WHOLE number of periods that elapsed: a return 2P after the last
        //    accepted one is the tick after a MISSED one (the waiter was busy), and its ideal slot is +2P. Clamping it to
        //    +1P (as this used to) left the reference one period behind the real tick, so a spurious second return 0.1 ms
        //    after it looked a full period late and was published as a second tick. The quarter-period bias rounds a
        //    return that is merely LATE (observed up to 3/4 of a period after its slot) down to its own slot, so a late
        //    observation still never makes the next on-time tick look like its duplicate.
        long measured = _measuredPeriodQpc;
        if (isFirstTick || isResyncGap) _expectedSlotQpc = nowQpc;
        else if (measured <= 0) _expectedSlotQpc = Math.Min(_expectedSlotQpc + tickPeriod, nowQpc);
        else
        {
            long periods = (nowQpc - _expectedSlotQpc + measured / 4) / measured;
            if (periods < 1) periods = 1;
            _expectedSlotQpc += periods * measured;
        }

        if (_hasAccepted) RecordPeriodSample(nowQpc - _lastAcceptedQpc);
        _lastAcceptedQpc = nowQpc;
        _hasAccepted = true;
        UpdateDecimation();
        _nextLatticeQpc = publish + TickPeriodQpc;               // re-read: the sample above may have moved the median
        _hasLattice = true;
        EndBurst();
        _synthesizedRun = 0;
        publishQpc = publish;
        return TickVerdict.Tick;
    }

    /// <summary>
    /// Decide whether a published tick is a WINDOW SLOT and, if so, what to stamp it with. Call EXACTLY ONCE per
    /// published tick, in order: it advances the window lattice.
    ///
    /// Pass-through mode: every tick is a slot stamped with its own instant. Decimate mode: the tick nearest each
    /// window-lattice point becomes the slot and is stamped with the LATTICE point, not its own instant - that exact,
    /// evenly-spaced stamp is what removes the 2:3 judder of showing a 120 Hz cadence on a 50 Hz panel. A tick more
    /// than a full window period past the lattice point (the clock was parked, the monitor slept) resyncs the lattice
    /// onto itself and is a slot.
    /// </summary>
    public bool IsSlot(long tickQpc, out long slotQpc)
    {
        long window = _windowPeriodQpc;
        if (!_decimating || window <= 0)
        {
            slotQpc = tickQpc;
            return true;
        }
        if (!_hasSlot)
        {
            _hasSlot = true;
            _nextSlotQpc = tickQpc + window;
            slotQpc = tickQpc;
            return true;
        }
        // Nearest-neighbour: the first tick at or after (slot - half a beat) owns the slot.
        long half = (_measuredPeriodQpc > 0 ? _measuredPeriodQpc : window) / 2;
        if (tickQpc + half < _nextSlotQpc)
        {
            slotQpc = 0;
            return false;
        }
        if (tickQpc - _nextSlotQpc > window)
        {
            slotQpc = tickQpc;
            _nextSlotQpc = tickQpc + window;
            return true;
        }
        slotQpc = _nextSlotQpc;
        _nextSlotQpc += window;
        return true;
    }

    /// <summary>The double-tick threshold: half the MEASURED beat once it exists, never below the 500 Hz floor, and
    /// never derived from the hint (see the class remarks).</summary>
    private long DoubleTickWindowQpc
    {
        get
        {
            long half = _measuredPeriodQpc > 0 ? _measuredPeriodQpc / 2 : 0;
            return half > _minSeparationQpc ? half : _minSeparationQpc;
        }
    }

    private void EndBurst()
    {
        _fastBurst = 0;
        _burstStartQpc = 0;
        _burstEndQpc = 0;
    }

    /// <summary>Fold one accepted delta into the median ring. Deltas outside [1 ms, 100 ms] are not refresh intervals
    /// (a 29 us "period" from a spinning wait below, a multi-second hole above) and are dropped as samples - the tick
    /// itself is still a tick.</summary>
    private void RecordPeriodSample(long delta)
    {
        if (delta < _minSampleQpc || delta > _maxSampleQpc) return;
        // A gap of k whole periods (k >= 2) is k-1 MISSED ticks — the waiter was busy under load — not evidence that the
        // display's period changed. Folding it into the median is what made the published period drift to 2P during a
        // sustained load (present-time prediction and pacing then ran at half the real rate). A genuine refresh-rate
        // change arrives with a display topology change, whose reprobe Resets the filter and re-measures from scratch.
        long measured = _measuredPeriodQpc;
        if (measured > 0 && delta >= measured + measured / 2) return;

        if (_periodCount < PeriodRingSize) _periodRing[_periodCount++] = delta;
        else { _periodRing[_periodHead] = delta; _periodHead = (_periodHead + 1) % PeriodRingSize; }
        if (_periodCount < PeriodRingMinSamples) return;

        // Small N (<= 9): stackalloc + insertion sort. No allocation, no comparer, no Array.Sort generic instantiation.
        Span<long> sorted = stackalloc long[_periodCount];
        for (int i = 0; i < _periodCount; i++) sorted[i] = _periodRing[i];
        for (int i = 1; i < sorted.Length; i++)
        {
            long v = sorted[i];
            int j = i - 1;
            while (j >= 0 && sorted[j] > v) { sorted[j + 1] = sorted[j]; j--; }
            sorted[j + 1] = v;
        }
        _measuredPeriodQpc = sorted[sorted.Length / 2];
    }

    /// <summary>Re-evaluate decimation after an accepted tick. Hysteresis by confirmation count, not by a ratio band:
    /// one doubled median (a legal hole in the ring) must never flip the whole pacing mode.</summary>
    private void UpdateDecimation()
    {
        long window = _windowPeriodQpc, tick = _measuredPeriodQpc;
        bool want = window > 0 && tick > 0 && window * DecimateDenominator >= tick * DecimateNumerator;
        if (want == _decimating)
        {
            _modeDisagreements = 0;
            return;
        }
        if (++_modeDisagreements < ModeSwitchConfirmations) return;
        _modeDisagreements = 0;
        _decimating = want;
        _hasSlot = false;   // the next published tick anchors the window lattice
    }
}
