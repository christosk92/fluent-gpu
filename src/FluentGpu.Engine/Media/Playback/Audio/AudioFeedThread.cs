using System;
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
/// The M4 flip (spec §7.9, §12) — moves the audio graph render/consume onto a dedicated MMCSS "Pro Audio" RT feed thread,
/// with decode/decrypt on a WORKER pool feeding a lock-free <see cref="PcmRing"/>, and clock polling + <c>Position</c>
/// publish on a NON-RT tick. Three roles around one unchanged <see cref="PcmAudioSession"/>:
/// <list type="bullet">
/// <item><b>RT feed thread</b> (<see cref="FeedOnce"/>): copy+mix ONLY — <see cref="PcmAudioSession.RtRenderOnce"/> pulls
/// the published graph (lock-free consume + <c>RenderInFlightDepth+1</c> quarantine) reading pre-decoded PCM out of the
/// per-voice rings, and presents to the sink. Zero managed alloc/lock/syscall/logging (the <see cref="AudioTripwire"/>
/// per-callback contract). On ring underrun it bumps the xrun counter and writes silence — it NEVER blocks or crashes.</item>
/// <item><b>Worker</b> (<see cref="WorkerPumpOnce"/>): decode/decrypt/fetch AHEAD into the rings — never on the RT thread —
/// AND the SOLE ring disposer (drains both retire queues, applies control-requested seeks).</item>
/// <item><b>Control / clock tick</b> (<see cref="ControlTickOnce"/>): the state machine + effect reconcile + the off-RT
/// <c>IAudioClock</c> poll and <c>Position</c> publish, plus publishing the xrun <see cref="Signal{T}"/> and any latched
/// background fault (off the RT thread, as a <see cref="MediaError"/> — never process-fatal).</item>
/// </list>
/// The ring table is an immutable <see cref="RingEntry"/> array published via <see cref="Volatile"/> and read by the RT +
/// worker as a snapshot; rebuilds (control install / worker retire-removal) serialize on <see cref="_tableLock"/> — the RT
/// thread NEVER takes a lock. The methods are individually drivable (deterministic tests + the race gate);
/// <see cref="Start"/>/<see cref="Stop"/> spin the real threads on-box. The single-thread <see cref="PcmAudioSession.PumpAudio"/>
/// pull path is untouched.
/// </summary>
public sealed class AudioFeedThread : IDisposable
{
    /// <summary>One published ring-table entry: the mixer voice id and its decode↔RT firewall ring.</summary>
    public readonly record struct RingEntry(long VoiceId, RingAudioSource Ring);

    private readonly PcmAudioSession _session;
    private readonly IRtThreadCharacteristics _rt;
    private readonly int _blockFrames;
    private readonly int _ringFrames;
    private readonly int _targetAheadFrames;
    private readonly int _blockPeriodMs;

    // The published ring table (spec §7.9/§12): an immutable snapshot the RT thread + worker Volatile-read; rebuilt under
    // _tableLock by the control thread (install) and the worker (retire-removal) — NEVER touched by the RT thread.
    private RingEntry[] _published = Array.Empty<RingEntry>();
    private readonly object _tableLock = new();

    // Lock-free SPSC retire queue (spec §7.9): the RT thread is the SOLE producer (EnqueueRetire); the worker is the SOLE
    // consumer (drains in WorkerPumpOnce → disposes the ring off the RT thread). Power-of-two capacity; drop if full.
    private readonly long[] _retireQ = new long[16];
    private int _retireHead;   // consumer (worker) cursor
    private int _retireTail;   // producer (RT) cursor

    // control→worker retire SPSC carrying RING REFERENCES (not ids — ids would collide: the old and new primary both use
    // PrimaryVoiceId). The control thread unpublishes the rings, then hands them here; the worker is the sole disposer.
    private readonly RingAudioSource?[] _ctlRetireQ = new RingAudioSource?[16];
    private int _ctlRetireHead, _ctlRetireTail;

    private long _pendingSeekFrame = -1;   // control→worker one-slot seek mailbox (-1 = none); last write wins (coalescing)

    private readonly Signal<int> _xruns = new(0);
    private long _xrunCount;

    private long _workerFaults, _rtFaults, _clockFaults;   // containment counters (diagnostics)
    private Exception? _lastFault;                          // latched; published by ControlTickOnce off the RT thread
    private int _ringsFreed;                                // once-guard: worker final sweep vs Dispose inline cleanup

    private Thread? _rtThread, _workerThread, _clockThread;
    private volatile bool _run;
    private volatile bool _disposed;

    /// <summary>Create a feed over <paramref name="session"/>. <paramref name="blockFrames"/> is the per-callback block
    /// (≈ the device period); <paramref name="rt"/> supplies the MMCSS registration (null ⇒ headless no-op).</summary>
    public AudioFeedThread(PcmAudioSession session, int blockFrames = 480, IRtThreadCharacteristics? rt = null,
        int ringFrames = 8192, int targetAheadFrames = 4096)
    {
        _session = session;
        _rt = rt ?? NullRtThreadCharacteristics.Instance;
        _blockFrames = Math.Clamp(blockFrames, 1, session.Format.SampleRate);
        _blockPeriodMs = Math.Max(1, (int)Math.Round(_blockFrames * 1000.0 / session.Format.SampleRate));
        _ringFrames = Math.Max(ringFrames, targetAheadFrames + blockFrames);
        _targetAheadFrames = targetAheadFrames;
        session.AttachFeed(this);
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
        var ring = new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2);
        lock (_tableLock)
        {
            var old = Volatile.Read(ref _published);
            for (int i = 0; i < old.Length; i++) CtlEnqueueRetire(old[i].Ring);
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
        var ring = new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2);
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

    /// <summary>Enqueue a retired voice id for the worker to dispose its ring off the RT thread (spec §7.9). Alloc-free,
    /// lock-free ring-buffer push — the RT thread is the sole producer. Drops silently if the queue is full (never blocks).</summary>
    public void EnqueueRetire(long voiceId)
    {
        int tail = _retireTail;
        int next = (tail + 1) & (_retireQ.Length - 1);
        if (next == Volatile.Read(ref _retireHead)) return;   // full → drop (the worker will catch it on the next finish)
        _retireQ[tail] = voiceId;
        Volatile.Write(ref _retireTail, next);
    }

    // CONTROL: hand an already-unpublished ring to the worker for off-RT disposal (by reference — ids collide). Sole
    // producer = control; sole consumer = worker. Drops (leaks until the worker's final sweep) rather than block if full.
    private void CtlEnqueueRetire(RingAudioSource ring)
    {
        int tail = _ctlRetireTail;
        int next = (tail + 1) & (_ctlRetireQ.Length - 1);
        if (next == Volatile.Read(ref _ctlRetireHead)) return;
        _ctlRetireQ[tail] = ring;
        Volatile.Write(ref _ctlRetireTail, next);
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
        for (int i = 0; i < rings.Length; i++)
            if (rings[i].Ring.ConsumeStarve()) starved = true;
        if (starved) Interlocked.Increment(ref _xrunCount);

        return rendered;
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
            try { rings[i].Ring.PumpAhead(); }
            catch (Exception e) { rings[i].Ring.MarkFailed(); RecordFault(ref _workerFaults, e); }   // decode fault → EOF → natural retire
        }
        DrainRetireQueue();      // RT-natural retires (by id): dispose + republish the table minus the entry
        DrainCtlRetireQueue();   // control retires (by ref, already unpublished): just dispose
    }

    private void ApplyPendingSeek()
    {
        long frame = Interlocked.Exchange(ref _pendingSeekFrame, -1);
        if (frame < 0) return;
        var rings = Volatile.Read(ref _published);
        long primary = _session.PrimaryVoiceIdValue;
        for (int i = 0; i < rings.Length; i++)
            if (rings[i].VoiceId == primary) { rings[i].Ring.WorkerApplySeek(frame); break; }
    }

    /// <summary>Drain the RT→worker retire queue: for each retired voice id, dispose its ring and republish the ring table
    /// WITHOUT that entry (an immutable rebuild under <see cref="_tableLock"/> — never a shared-list <c>RemoveAt</c> the RT
    /// snapshot could tear on). Worker thread ONLY — the sole consumer/disposer, so dispose is safe.</summary>
    private void DrainRetireQueue()
    {
        int head = _retireHead;
        while (head != Volatile.Read(ref _retireTail))
        {
            long voiceId = _retireQ[head];
            head = (head + 1) & (_retireQ.Length - 1);
            RemovePublishedEntry(voiceId);
        }
        Volatile.Write(ref _retireHead, head);
    }

    // WORKER: unpublish the entry with voiceId (immutable rebuild under the table lock) then dispose its ring off-RT.
    private void RemovePublishedEntry(long voiceId)
    {
        RingAudioSource? ring = null;
        lock (_tableLock)
        {
            var cur = Volatile.Read(ref _published);
            int idx = -1;
            for (int i = 0; i < cur.Length; i++) if (cur[i].VoiceId == voiceId) { idx = i; break; }
            if (idx < 0) return;
            ring = cur[idx].Ring;
            var next = new RingEntry[cur.Length - 1];
            for (int i = 0, j = 0; i < cur.Length; i++) if (i != idx) next[j++] = cur[i];
            Volatile.Write(ref _published, next);
        }
        try { ring?.Dispose(); } catch { /* teardown never throws */ }
    }

    // WORKER: dispose the control-retired rings (already unpublished — just free the inner decoder off-RT).
    private void DrainCtlRetireQueue()
    {
        int head = _ctlRetireHead;
        while (head != Volatile.Read(ref _ctlRetireTail))
        {
            var ring = _ctlRetireQ[head];
            _ctlRetireQ[head] = null;
            head = (head + 1) & (_ctlRetireQ.Length - 1);
            try { ring?.Dispose(); } catch { /* teardown never throws */ }
        }
        Volatile.Write(ref _ctlRetireHead, head);
    }

    // ── control / clock tick — off the RT thread ─────────────────────────────────────────────────────────────────────

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
        _run = true;

        _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "FluentGpu.AudioWorker" };
        _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "FluentGpu.AudioClock" };
        _rtThread = new Thread(RtLoop) { IsBackground = true, Name = "FluentGpu.AudioRT", Priority = ThreadPriority.Highest };
        _workerThread.Start();
        _clockThread.Start();
        _rtThread.Start();
    }

    /// <summary>Stop the live threads (idempotent). Joins are best-effort (500 ms) and NEVER followed by a ring dispose —
    /// a thread field is nulled ONLY on a successful join, so <see cref="Dispose"/> can tell whether a live worker will run
    /// the final ring sweep. Teardown never throws.</summary>
    public void Stop()
    {
        _run = false;
        bool rtJoined = true, workerJoined = true, clockJoined = true;
        try { rtJoined = _rtThread?.Join(500) ?? true; } catch { }
        try { workerJoined = _workerThread?.Join(500) ?? true; } catch { }
        try { clockJoined = _clockThread?.Join(500) ?? true; } catch { }
        if (rtJoined) _rtThread = null;
        if (workerJoined) _workerThread = null;
        if (clockJoined) _clockThread = null;
    }

    private void RtLoop()
    {
        using var _ = _rt.Enter();   // MMCSS Pro-Audio for the lifetime of the RT thread
        while (_run)
        {
            int rendered = 0;
            try { rendered = FeedOnce(); }
            catch (Exception e) { RecordFault(ref _rtFaults, e); }

            // A live blocking endpoint (WASAPI) is the clock: its buffer-full wait INSIDE FeedOnce paces this thread to the
            // device and avoids a busy spin. Adding a wall-clock period sleep on TOP of that double-paces it — the timer
            // granularity overshoots every period, so the loop settles below real time and the device buffer never fills
            // (chronic near-empty → stutter + slow). Sleep ONLY when nothing was rendered (paused / inert / headless /
            // device-loss sinks return instantly) so the MMCSS thread doesn't spin a core; on the live path the sink paces.
            if (rendered <= 0) Thread.Sleep(_blockPeriodMs);
        }
    }

    private void WorkerLoop()
    {
        while (_run)
        {
            try { WorkerPumpOnce(); }
            catch (Exception e) { RecordFault(ref _workerFaults, e); }
            // PumpAhead fills every ring to a multi-period target in one pass. Polling once per half-period is enough to
            // replenish it while avoiding the full-ring busy spin measured on FluentGpu.AudioWorker.
            Thread.Sleep(Math.Max(1, _blockPeriodMs / 2));
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
        DrainCtlRetireQueue();
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
    }
}
