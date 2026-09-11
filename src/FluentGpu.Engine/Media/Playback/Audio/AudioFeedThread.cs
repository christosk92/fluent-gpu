using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The RT-thread characteristics seam (spec §7.9/§13) — registers the calling thread as a real-time audio thread. The
/// Windows leaf implements it with MMCSS "Pro Audio" (<c>AvSetMmThreadCharacteristics</c>); the portable default is a
/// no-op (headless / macOS supply their own). Kept in the portable engine so <see cref="AudioFeedThread"/> stays
/// TerraFX-free. <see cref="Enter"/> returns a token whose disposal reverts the characteristics.
/// </summary>
public interface IRtThreadCharacteristics
{
    /// <summary>Register the CURRENT thread as a Pro-Audio RT thread; dispose the returned token to revert. May return null.</summary>
    IDisposable? Enter();
}

/// <summary>The portable no-op <see cref="IRtThreadCharacteristics"/> (headless / tests): the RT thread runs at normal
/// priority. On-box the Windows leaf supplies the real MMCSS registration.</summary>
public sealed class NullRtThreadCharacteristics : IRtThreadCharacteristics
{
    /// <summary>The shared instance.</summary>
    public static NullRtThreadCharacteristics Instance { get; } = new();
    /// <inheritdoc/>
    public IDisposable? Enter() => null;
}

/// <summary>
/// Separates PCM output, decoder production and clock/control observation. Live rings have isolated producers;
/// the management worker drains render-acknowledged retirement. Deterministic fixtures drive the same ring
/// producers through WorkerPumpOnce instead. The output loop drains commands while paused, renders allocation-free
/// DSP and waits for device/control events outside that DSP scope. Ring tables are immutable published snapshots.
/// </summary>
public sealed class AudioFeedThread : IDisposable
{
    /// <summary>One published ring-table entry: the mixer voice id and its decode↔RT firewall ring.</summary>
    public readonly record struct RingEntry(long VoiceId, RingAudioSource Ring);

    /// <summary>
    /// One underrun incident, recorded on the RT thread and drained off it (spec §7.9). A bare cumulative counter — all
    /// this engine published before — cannot answer the only questions that matter when a user reports a dropout: WHEN,
    /// HOW MUCH audio was lost, and WHY. These fields separate the causes: a starve with a healthy
    /// <paramref name="RingFrames"/> is a device/scheduling problem, a starve with an empty ring is producer starvation,
    /// and a non-zero <paramref name="GcPauseTicksDelta"/> in the same window implicates the GC.
    /// </summary>
    /// <param name="Timestamp"><c>Stopwatch.GetTimestamp()</c> at the miss.</param>
    /// <param name="GapFrames">Frames of silence written — the severity, NOT a callback count.</param>
    /// <param name="RingFrames">The ring's fill at the miss (0 ⇒ fully drained).</param>
    /// <param name="VoiceId">The mixer voice whose ring starved.</param>
    /// <param name="GcPauseTicksDelta">GC pause ticks accrued since the previous drain (see <see cref="DrainXrunEvents"/>).</param>
    public readonly record struct XrunEvent(long Timestamp, int GapFrames, int RingFrames, long VoiceId,
        long GcPauseTicksDelta);

    private readonly PcmAudioSession _session;
    private readonly IRtThreadCharacteristics _rt;
    // Not readonly (spec §7.9 Fix 3): Resize(MixFormat) re-derives all four from the ms-domain sizing below when a device
    // rebuild also changes rate, so the decode-ahead cushion stays correct in TIME (not frames) at the new rate too.
    private int _blockFrames;
    private int _ringFrames;
    private int _targetAheadFrames;
    private int _blockPeriodMs;
    private readonly int _maxBlocksPerWake;
    // The ms-domain sizing this feed was configured with (back-derived from the frame counts at construction time for
    // BOTH ctors — they are just different unit systems for the same four sizes). Resize re-applies these against a new
    // sample rate instead of leaving the frame counts pinned to the rate the feed happened to be built at.
    private double _blockMs, _ringMs, _aheadMs;

    // The worker's low-water wake (spec §7.9): FeedOnce Sets this the instant any ring's fill crosses below half its
    // target-ahead depth (RingAudioSource.CheckLowWaterEdge is the edge latch that keeps this to ONE Set per drop). The
    // wait side (WorkerLoop) is always BOUNDED (WorkerWaitTimeoutMs) — never infinite — so a missed/coalesced Set, or a
    // headless gate driving WorkerPumpOnce with no live RT thread at all, still makes forward progress.
    private readonly AutoResetEvent _workerWake = new(false);
    private readonly AutoResetEvent _outputWake = new(false);
    /// <summary>Wake paused/output-capacity waits for transport commands.</summary>
    public void WakeOutput() { if (!_disposed) _outputWake.Set(); }
    private const int WorkerWaitTimeoutMs = 20;

    // The published ring table (spec §7.9/§12): an immutable snapshot the RT thread + worker Volatile-read; rebuilt under
    // _tableLock by the control thread (install) and the worker (retire-removal) — NEVER touched by the RT thread.
    private RingEntry[] _published = Array.Empty<RingEntry>();
    private readonly object _tableLock = new();

    // Intrusive retire stack uses the already allocated ring nodes; no RT allocation or queue-full drop.
    private RingAudioSource? _retireStack;

    private long _pendingSeekFrame = -1;   // control→worker one-slot seek mailbox (-1 = none); last write wins (coalescing)

    private readonly Signal<int> _xruns = new(0);
    private long _xrunCount;
    private long _xrunFramesLost;   // total frames of silence written on starve (severity; the counter above is incidents)

    // Per-xrun incident queue (spec §7.9): a PRE-ALLOCATED power-of-two SPSC ring — the RT thread is the sole producer
    // (record-on-starve, never logs/allocates), any non-RT caller is the sole consumer via DrainXrunEvents. A full queue
    // DROPS the newest event rather than block or grow: losing one diagnostic beats stalling the RT thread. 64 slots is
    // ~13 s of continuous per-block starving at a 10 ms block — far past the point the drain would have run.
    private readonly XrunEvent[] _xrunQ = new XrunEvent[64];
    private int _xrunHead;   // consumer cursor
    private int _xrunTail;   // producer (RT) cursor
    private long _lastGcPauseTicks;   // drain-side baseline for GcPauseTicksDelta (non-RT; see DrainXrunEvents)

    private long _workerFaults, _rtFaults, _clockFaults;   // containment counters (diagnostics)
    private Exception? _lastFault;                          // latched; published by ControlTickOnce off the RT thread
    private int _ringsFreed;                                // once-guard: worker final sweep vs Dispose inline cleanup

    private Thread? _rtThread, _workerThread, _clockThread;
    private volatile bool _run;
    private volatile bool _disposed;

    /// <summary>Create a feed over <paramref name="session"/>. <paramref name="blockFrames"/> is the per-callback block
    /// (≈ the device period); <paramref name="rt"/> supplies the MMCSS registration (null ⇒ headless no-op). FRAME-sized
    /// overload retained only so pre-existing call sites keep compiling — prefer the TIME-sized ctor below: a fixed frame
    /// count silently shrinks the decode-ahead cushion at higher device rates (spec §7.9: 4096 frames is 85 ms of cushion
    /// at 48 kHz but only 21 ms at a 192 kHz Realtek default), which is exactly the bug that ctor exists to close.</summary>
    public AudioFeedThread(PcmAudioSession session, int blockFrames = 480, IRtThreadCharacteristics? rt = null,
        int ringFrames = 8192, int targetAheadFrames = 4096)
        : this(session, rt, blockFrames, ringFrames, targetAheadFrames, maxBlocksPerWake: 3)
    {
    }

    /// <summary>Create a feed sized in TIME, not frames (spec §7.9). The WASAPI device buffer is ~100 ms: if the RT thread
    /// stalls for <c>T</c> ms (e.g. a Gen2 GC), the device drains silently for the whole stall — that is what the device
    /// buffer is FOR — but on resume the catch-up burst must not then drain more of the decode-ahead ring than the device
    /// buffer just hid. <paramref name="aheadMs"/> defaults to 500 ms (well over the ~100 ms device buffer, unlike the old
    /// ~85 ms frame-sized default) and every size here converts against <paramref name="sampleRate"/> so the cushion holds
    /// at any device rate (44.1/48/96/192 kHz) instead of collapsing at high rates the way a fixed frame count does.
    /// <paramref name="maxBlocksPerWake"/> bounds the RT catch-up burst (<see cref="RenderBurst"/>) so a long stall drains
    /// the ring gradually across wakes — leaving time for the worker's low-water wake to refill it — instead of instantly.</summary>
    public AudioFeedThread(PcmAudioSession session, int sampleRate, IRtThreadCharacteristics? rt = null,
        double blockMs = 10.0, double aheadMs = 500.0, double ringMs = 1000.0, int maxBlocksPerWake = 3)
        : this(session, rt,
              blockFrames: FramesFromMs(blockMs, sampleRate),
              ringFrames: FramesFromMs(ringMs, sampleRate),
              targetAheadFrames: FramesFromMs(aheadMs, sampleRate),
              maxBlocksPerWake: maxBlocksPerWake)
    {
    }

    // Shared init (both public ctors funnel here): both are just different unit systems for the same four sizes.
    private AudioFeedThread(PcmAudioSession session, IRtThreadCharacteristics? rt, int blockFrames, int ringFrames,
        int targetAheadFrames, int maxBlocksPerWake)
    {
        _session = session;
        _rt = rt ?? NullRtThreadCharacteristics.Instance;
        int rate = session.Format.SampleRate;
        _blockFrames = Math.Clamp(blockFrames, 1, rate);
        _blockPeriodMs = Math.Max(1, (int)Math.Round(_blockFrames * 1000.0 / rate));
        _ringFrames = Math.Max(ringFrames, targetAheadFrames + blockFrames);
        _targetAheadFrames = targetAheadFrames;
        _maxBlocksPerWake = Math.Max(1, maxBlocksPerWake);
        // Back-derive the ms-domain sizing from the frame counts at the CONSTRUCTION rate (spec §7.9 Fix 3) — the
        // frame-sized ctor's counts are, by construction, sized against `session.Format.SampleRate` right here (see the
        // Clamp above), so this recovers the same time-domain intent the ms-sized ctor expresses directly.
        _blockMs = _blockFrames * 1000.0 / rate;
        _ringMs = _ringFrames * 1000.0 / rate;
        _aheadMs = _targetAheadFrames * 1000.0 / rate;
        session.AttachFeed(this);
    }

    // ms → frames against a caller-supplied sample rate (not session.Format.SampleRate) so the composition root's probed
    // device rate is the single source of truth, matching whatever it passes as sampleRate.
    private static int FramesFromMs(double ms, int sampleRate) => Math.Max(1, (int)Math.Round(ms * sampleRate / 1000.0));

    /// <summary>Re-derive the ms→frames sizing against <paramref name="newFormat"/>'s rate (spec §7.9 Fix 3): a device
    /// rebuild that also changes sample rate (e.g. 48000 → 44100) would otherwise leave the block/ring/decode-ahead
    /// sizing pinned to the OLD rate — the exact ms-vs-frames collapse the time-sized ctor exists to prevent, just
    /// re-introduced on a LATER rebuild instead of at construction. Call this from the rate-change site (the cold device
    /// thread, around the same park/swap/resume the RT feed already gets) — it plain-writes four fields with no
    /// Volatile/Interlocked, so it is NOT RT-safe while the feed's threads are live; callers must have the feed
    /// stopped first (the on-box cold loop already parks it around every rebuild).</summary>
    public void Resize(MixFormat newFormat)
    {
        int rate = Math.Max(1, newFormat.SampleRate);
        _blockFrames = Math.Clamp(FramesFromMs(_blockMs, rate), 1, rate);
        _blockPeriodMs = Math.Max(1, (int)Math.Round(_blockFrames * 1000.0 / rate));
        _targetAheadFrames = FramesFromMs(_aheadMs, rate);
        _ringFrames = Math.Max(FramesFromMs(_ringMs, rate), _targetAheadFrames + _blockFrames);
    }

    /// <summary>The per-callback block size (frames).</summary>
    public int BlockFrames => _blockFrames;
    /// <summary>The total underruns observed since start (spec §7.9). Bumped on the RT thread, read anywhere.</summary>
    public long XrunCount => Interlocked.Read(ref _xrunCount);
    /// <summary>The xrun count as a bindable signal, published from the NON-RT control tick.</summary>
    public IReadSignal<int> Xruns => _xruns;
    /// <summary>The number of registered per-voice decode rings (for tests/diagnostics). Volatile snapshot.</summary>
    public int RingCount => Volatile.Read(ref _published).Length;
    /// <summary>The published ring table snapshot (for tests/diagnostics) — immutable; never mutate the returned array.</summary>
    public RingEntry[] RingsSnapshot => Volatile.Read(ref _published);

    /// <summary>Called by <see cref="PcmAudioSession.SetVoice"/> when a feed is attached: wrap a decoding voice in a
    /// decode↔RT firewall ring and PUBLISH a single-entry ring table tagged with the session's primary voice id (so the RT
    /// natural-end retire resolves it — spec §7.9). Any previous rings are handed to the worker for off-RT disposal by
    /// REFERENCE (ids would collide with the new primary). Control thread only.</summary>
    public IAudioSource Wrap(IAudioSource inner)
    {
        var ring = inner as RingAudioSource ?? new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2);
        if (_run) ring.StartProducer();
        lock (_tableLock)
        {
            Volatile.Write(ref _published, new[] { new RingEntry(_session.PrimaryVoiceIdValue, ring) });
        }
        return ring;
    }

    /// <summary>Wrap an ADDITIONAL crossfade voice (spec §8) in its own decode↔RT firewall ring and PUBLISH it into the ring
    /// table (a copy-grow under <see cref="_tableLock"/>) WITHOUT retiring the primary — the worker pumps it and the RT loop
    /// watches it for underrun, exactly like the primary. The ring is tagged with <paramref name="voiceId"/> (the mixer
    /// voice id) so the worker can dispose it off-RT when the RT thread retires that voice (via <see cref="EnqueueRetire"/>).
    /// CONTROL thread only (called from <see cref="PcmAudioSession.AddCrossfadeVoice"/>).</summary>
    public IAudioSource WrapAdditional(IAudioSource inner, long voiceId)
    {
        var ring = inner as RingAudioSource ?? new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2);
        if (_run) ring.StartProducer();
        lock (_tableLock)
        {
            var cur = Volatile.Read(ref _published);
            var next = new RingEntry[cur.Length + 1];
            Array.Copy(cur, next, cur.Length);
            next[cur.Length] = new RingEntry(voiceId, ring);
            Volatile.Write(ref _published, next);
        }
        return ring;
    }

    /// <summary>Output-thread retirement by voice identity. Existing ring objects form an intrusive queue,
    /// so structural retirement cannot be lost to queue capacity and never allocates on output.</summary>
    public void EnqueueRetire(long voiceId)
    {
        foreach (var entry in Volatile.Read(ref _published))
            if (entry.VoiceId == voiceId) { EnqueueRetire(entry.Ring); return; }
    }

    /// <summary>Retire the exact ring acknowledged by the mixer, independent of reused voice identities.</summary>
    public void EnqueueRetire(RingAudioSource ring)
    {
        if (!ring.TryRequestRetirement()) return;
        RingAudioSource? previous;
        do
        {
            previous = Volatile.Read(ref _retireStack);
            ring.RetirementNext = previous;
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _retireStack, ring, previous), previous));
        if (!_disposed) _workerWake.Set();
    }

    /// <summary>CONTROL: request a primary-voice seek; the WORKER applies it between pumps (the worker is the sole toucher
    /// of the inner decoder — spec §7.9/§12). Last write wins (seek coalescing).</summary>
    public void RequestSeek(long frame) => Volatile.Write(ref _pendingSeekFrame, frame);

    // ── RT feed thread — copy+mix ONLY ───────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE RT callback (spec §7.9): consume any pending seek flush per ring, render+present one block through the
    /// published graph (lock-free consume) and, if a voice ring underran, bump the xrun counter + write silence for the
    /// shortfall. Reads the ring table as a Volatile snapshot — no lock. Alloc/lock/syscall-free (the
    /// <see cref="AudioTripwire"/> around <see cref="PcmAudioSession.RenderBlock"/> enforces it). Returns frames presented.</summary>
    public int FeedOnce()
    {
        var rings = Volatile.Read(ref _published);
        for (int i = 0; i < rings.Length; i++) rings[i].Ring.RtConsumeFlush();   // seek: discard pre-seek PCM (consumer-side)

        int rendered = _session.RtRenderOnce(_blockFrames);

        // Underrun detection is a cheap read-and-clear of each ring's latch — no alloc, no lock (Interlocked only).
        bool starved = false;
        bool lowWater = false;
        bool suppress = _session.SuppressXrunAccounting;
        for (int i = 0; i < rings.Length; i++)
        {
            if (rings[i].Ring.ConsumeStarve()) starved = true;
            // Severity + incident record. ConsumeStarveFrames must be read-and-cleared EVERY block whether or not we are
            // recording, otherwise a suppressed seek rebuffer would leak its shortfall into the next real incident.
            int gapFrames = rings[i].Ring.ConsumeStarveFrames();
            if (gapFrames > 0 && !suppress)
            {
                Interlocked.Add(ref _xrunFramesLost, gapFrames);
                RecordXrun(gapFrames, rings[i].Ring.BufferedFrames, rings[i].VoiceId);
            }
            // Low-water worker wake (spec §7.9): edge-triggered (Volatile read only) so this fires the worker's event
            // ONCE per drop below half target-ahead, not on every block while it stays low — the AudioTripwire alloc/
            // lock/syscall-free contract's one carved-out exception is exactly this: a Volatile read + Set on a
            // pre-allocated event, nothing else.
            if (rings[i].Ring.CheckLowWaterEdge())
            {
                rings[i].Ring.WakeProducer();
                lowWater = true;
            }
        }
        // Fix 2 hook (spec, PcmAudioSession.SuppressXrunAccounting remarks): a control-requested seek/flush intentionally
        // empties the ring, so the RT loop's very next reads finding it empty are a PLANNED rebuffer, not a real underrun —
        // gate the xrun increment on the session's suppression flag (a single volatile bool read; safe on the RT thread).
        if (starved && !_session.SuppressXrunAccounting) Interlocked.Increment(ref _xrunCount);
        if (lowWater) _workerWake.Set();

        return rendered;
    }

    /// <summary>ONE RT wake (spec §7.9): render up to <see cref="_maxBlocksPerWake"/> blocks via <see cref="FeedOnce"/>,
    /// then stop even if the sink would still accept more. This is the catch-up BURST CAP: after a stall the device
    /// buffer can accept many blocks back-to-back without <see cref="FeedOnce"/> ever blocking (a live WASAPI <c>Write</c>
    /// returns instantly while the buffer's padding is low), so an uncapped wake would drain the whole decode-ahead ring
    /// in under a millisecond — turning a stall the DEVICE BUFFER should have hidden into audible silence. Stops early
    /// (before the cap) the moment a block renders nothing (paused / inert / device-loss — the sink is not the pacing
    /// clock right now). Individually drivable (deterministic tests); <see cref="RtLoop"/> calls it once per wake and
    /// yields the timeslice afterward — it never sleeps here (see the no-double-pacing reasoning on <see cref="RtLoop"/>).
    /// Returns the number of blocks actually rendered (<c>&lt; MaxBlocksPerWake</c> ⇒ the RT thread went idle this wake).</summary>
    public int RenderBurst()
    {
        int blocks = 0;
        for (; blocks < _maxBlocksPerWake; blocks++)
        {
            int rendered;
            try { rendered = FeedOnce(); }
            catch (Exception e) { RecordFault(ref _rtFaults, e); rendered = 0; }
            if (rendered <= 0) break;
        }
        return blocks;
    }

    /// <summary>Observed starvation duration expressed in mix frames. Content is retained while playback waits;
    /// this measures the audible output interruption, not source samples skipped from the track.</summary>
    public long XrunFramesLost => Interlocked.Read(ref _xrunFramesLost);

    // RT: push one incident into the pre-allocated SPSC queue. No allocation, no lock, no logging — a struct store plus
    // a Volatile cursor write. Stopwatch.GetTimestamp is a QPC read (the AudioTripwire itself uses it, so it is in
    // contract). Full queue ⇒ DROP: the RT thread never blocks or grows a buffer to keep a diagnostic.
    private void RecordXrun(int gapFrames, int ringFrames, long voiceId)
    {
        int tail = _xrunTail;
        int next = (tail + 1) & (_xrunQ.Length - 1);
        if (next == Volatile.Read(ref _xrunHead)) return;
        _xrunQ[tail] = new XrunEvent(Stopwatch.GetTimestamp(), gapFrames, ringFrames, voiceId, 0);
        Volatile.Write(ref _xrunTail, next);
    }

    /// <summary>Drain recorded underrun incidents into <paramref name="dst"/>, returning how many were written (never more
    /// than <c>dst.Length</c>; call again while it returns a full span). NON-RT ONLY — the sole consumer, typically the
    /// host's control tick or the app's UI-rate timer.
    /// <para><see cref="XrunEvent.GcPauseTicksDelta"/> is stamped HERE, not on the RT thread: it is the GC pause time
    /// accrued across the whole interval since the previous drain, attributed to every event in that interval. That is
    /// deliberately an interval attribution, not a per-event measurement — a non-zero value means "the GC was pausing
    /// threads in the window these misses occurred in", which is the correlation a dropout report needs; it does not
    /// claim this specific miss was inside a pause.</para></summary>
    public int DrainXrunEvents(Span<XrunEvent> dst)
    {
        if (dst.IsEmpty) return 0;
        long pauseNow = GC.GetTotalPauseDuration().Ticks;
        long pauseDelta = pauseNow - _lastGcPauseTicks;
        _lastGcPauseTicks = pauseNow;

        int n = 0, head = _xrunHead;
        while (n < dst.Length && head != Volatile.Read(ref _xrunTail))
        {
            var e = _xrunQ[head];
            dst[n++] = e with { GcPauseTicksDelta = pauseDelta };
            head = (head + 1) & (_xrunQ.Length - 1);
        }
        Volatile.Write(ref _xrunHead, head);
        return n;
    }

    // ── worker — decode AHEAD + the SOLE ring disposer ───────────────────────────────────────────────────────────────

    /// <summary>ONE worker pass: apply any pending control-requested seek, decode/decrypt every voice ring up to its
    /// target-ahead depth (per-ring fault containment: a decode fault marks the ring failed → natural EOF retire), then
    /// drain both retire queues (the worker is the sole ring disposer). Off the RT thread.</summary>
    public void WorkerPumpOnce()
    {
        ApplyPendingSeek();
        var rings = Volatile.Read(ref _published);
        for (int i = 0; i < rings.Length; i++)
        {
            try { if (!rings[i].Ring.HasDedicatedProducer) rings[i].Ring.PumpAhead(); }
            catch (Exception e) { rings[i].Ring.MarkFailed(); RecordFault(ref _workerFaults, e); }   // decode fault → EOF → natural retire
        }
        DrainRetireQueue();      // RT-natural retires (by id): dispose + republish the table minus the entry
    }

    private void ApplyPendingSeek()
    {
        long frame = Interlocked.Exchange(ref _pendingSeekFrame, -1);
        if (frame < 0) return;
        var rings = Volatile.Read(ref _published);
        // Seek the ACTIVE voice's ring — after a committed crossfade/gapless hand-off that is the promoted incoming voice
        // (SetActiveVoice re-pointed it), not the retired primary; a fresh SetVoice resets it to the primary id.
        long active = _session.ActiveVoiceIdValue;
        for (int i = 0; i < rings.Length; i++)
            if (rings[i].VoiceId == active) { rings[i].Ring.WorkerApplySeek(frame); break; }
    }

    /// <summary>Drain the RT→worker retire queue: for each retired voice id, dispose its ring and republish the ring table
    /// WITHOUT that entry (an immutable rebuild under <see cref="_tableLock"/> — never a shared-list <c>RemoveAt</c> the RT
    /// snapshot could tear on). Worker thread ONLY — the sole consumer/disposer, so dispose is safe.</summary>
    private void DrainRetireQueue()
    {
        var ring = Interlocked.Exchange(ref _retireStack, null);
        while (ring is not null)
        {
            var next = ring.RetirementNext;
            ring.RetirementNext = null;
            lock (_tableLock)
            {
                var current = Volatile.Read(ref _published);
                int index = -1;
                for (int i = 0; i < current.Length; i++)
                    if (ReferenceEquals(current[i].Ring, ring)) { index = i; break; }
                if (index >= 0)
                {
                    var updated = new RingEntry[current.Length - 1];
                    for (int i = 0, j = 0; i < current.Length; i++) if (i != index) updated[j++] = current[i];
                    Volatile.Write(ref _published, updated);
                }
            }
            ring.Dispose();
            ring = next;
        }
    }

    /// <summary>ONE control tick (spec §7.6/§7.9): advance the state machine, reconcile effects, sample the
    /// <c>IAudioClock</c> and publish <c>Position</c> (all off the RT thread), publish the xrun signal, and surface any
    /// latched background fault as a <see cref="MediaError"/> (off the RT thread — never process-fatal).</summary>
    public void ControlTickOnce()
    {
        _session.TickControl(_blockFrames);
        int x = (int)Interlocked.Read(ref _xrunCount);
        if (_xruns.Peek() != x) _xruns.Value = x;

        var f = Interlocked.Exchange(ref _lastFault, null);
        if (f is not null) _session.ReportBackgroundFault(f);
    }

    private void RecordFault(ref long counter, Exception e)
    {
        Interlocked.Increment(ref counter);
        Interlocked.CompareExchange(ref _lastFault, e, null);   // first fault wins; ControlTickOnce publishes it off-RT
    }

    // ── on-box live drive (three real threads) ───────────────────────────────────────────────────────────────────────

    /// <summary>Spin the RT feed thread (MMCSS Pro-Audio), the decode worker, and the clock tick. ON-BOX only — the
    /// automated gate drives <see cref="FeedOnce"/>/<see cref="WorkerPumpOnce"/>/<see cref="ControlTickOnce"/> deterministically.</summary>
    public void Start()
    {
        if (_run || _disposed) return;
        if (!IsStopped) throw new InvalidOperationException("A previous audio worker has not acknowledged retirement.");
        _run = true;
        foreach (var entry in Volatile.Read(ref _published)) entry.Ring.StartProducer();

        // AboveNormal (below the RT thread's Highest — spec §7.9): the worker must win scheduling against ordinary app
        // work (UI/GC/other Normal threads) so its low-water refill actually lands promptly after a stall-triggered
        // catch-up burst, without contending with the RT thread itself.
        _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "FluentGpu.AudioWorker", Priority = ThreadPriority.AboveNormal };
        _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "FluentGpu.AudioClock" };
        _rtThread = new Thread(RtLoop) { IsBackground = true, Name = "FluentGpu.AudioRT", Priority = ThreadPriority.Highest };
        _workerThread.Start();
        _clockThread.Start();
        _rtThread.Start();
    }

    /// <summary>All output, management and clock threads acknowledged stopping.</summary>
    public bool IsStopped => _rtThread is null && _workerThread is null && _clockThread is null;

    /// <summary>Request stop and join each thread with a two-second bound. Failed joins retain ownership.</summary>
    public void Stop()
    {
        if (IsStopped) return;
        _run = false;
        _outputWake.Set();
        _workerWake.Set();
        bool rtJoined = true, workerJoined = true, clockJoined = true;
        try { rtJoined = _rtThread?.Join(2000) ?? true; } catch { }
        try { workerJoined = _workerThread?.Join(2000) ?? true; } catch { }
        try { clockJoined = _clockThread?.Join(2000) ?? true; } catch { }
        if (rtJoined) _rtThread = null;
        if (workerJoined) _workerThread = null;
        if (clockJoined) _clockThread = null;
    }

    private void RtLoop()
    {
        using var _ = _rt.Enter();   // MMCSS Pro-Audio for the lifetime of the RT thread
        while (_run)
        {
            int blocks = RenderBurst();   // up to _maxBlocksPerWake blocks — the catch-up burst cap (spec §7.9)

            // Device waits are outside pure PCM rendering and can be interrupted by controls.
            _session.WaitForOutput(_outputWake, blocks == _maxBlocksPerWake ? 1 : _blockPeriodMs);
        }
    }

    private void WorkerLoop()
    {
        while (_run)
        {
            try { WorkerPumpOnce(); }
            catch (Exception e) { RecordFault(ref _workerFaults, e); }
            // Low-water wake (spec §7.9): FeedOnce Sets _workerWake the instant any ring's fill drops below half its
            // target-ahead depth, so refill happens promptly after a stall-triggered catch-up burst instead of waiting
            // out a fixed poll — replaces the old `Thread.Sleep(_blockPeriodMs / 2)` poll, whose rounded-to-int period
            // (2.5 ms rounds to 2 at 192 kHz) drifted from real time over a long run. The wait is BOUNDED — never
            // infinite — so a missed/coalesced Set still makes forward progress (e.g. more than one ring going low in
            // the same block only needs one Set, or the ring recovers between the Set and the wait).
            _workerWake.WaitOne(WorkerWaitTimeoutMs);
        }
        if (_disposed) FinalCleanup();   // sole safe disposer: frees rings even when Dispose's join timed out
    }

    private void ClockLoop()
    {
        while (_run)
        {
            try { ControlTickOnce(); }
            catch (Exception e) { RecordFault(ref _clockFaults, e); }
            // Position/state publication is a control-rate concern, not an audio-rate spin loop.
            Thread.Sleep(15);
        }
    }

    // The final ring sweep — the SOLE safe disposer runs it on the worker's exit (or inline from Dispose only when no
    // worker was ever started). Guarded by a once-flag so the two paths can't double-dispose.
    private void FinalCleanup()
    {
        if (Interlocked.Exchange(ref _ringsFreed, 1) != 0) return;
        DrainRetireQueue();
        var rings = Volatile.Read(ref _published);
        for (int i = 0; i < rings.Length; i++) { try { rings[i].Ring.Dispose(); } catch { /* teardown never throws */ } }
        Volatile.Write(ref _published, Array.Empty<RingEntry>());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        bool hadThreads = _rtThread is not null || _workerThread is not null || _clockThread is not null;
        Stop();   // joins are best-effort; NEVER followed by an unconditional ring dispose
        // Clean inline ONLY when there is no live worker left to run the sweep (headless/manual-drive path, or a clean join).
        if (!hadThreads || _workerThread is null) FinalCleanup();
        // Only dispose the wake event once the worker loop has actually exited (Stop nulls _workerThread ONLY on a
        // successful join) — a still-live worker calling WaitOne on a disposed handle would throw unhandled on its own
        // thread. If the join timed out, the handle is left for process teardown, same trade-off as the ring sweep above.
        if (_workerThread is null) try { _workerWake.Dispose(); } catch { /* teardown never throws */ }
    }
}
