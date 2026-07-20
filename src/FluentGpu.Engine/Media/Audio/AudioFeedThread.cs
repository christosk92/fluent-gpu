using System;
using System.Collections.Generic;
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
/// <item><b>Worker</b> (<see cref="WorkerPumpOnce"/>): decode/decrypt/fetch AHEAD into the rings — never on the RT thread.</item>
/// <item><b>Control / clock tick</b> (<see cref="ControlTickOnce"/>): the state machine + effect reconcile + the off-RT
/// <c>IAudioClock</c> poll and <c>Position</c> publish, plus publishing the xrun <see cref="Signal{T}"/>.</item>
/// </list>
/// The methods are individually drivable (deterministic tests + the race gate); <see cref="Start"/>/<see cref="Stop"/>
/// spin the real threads on-box. The single-thread <see cref="PcmAudioSession.PumpAudio"/> pull path is untouched.
/// </summary>
public sealed class AudioFeedThread : IDisposable
{
    private readonly PcmAudioSession _session;
    private readonly IRtThreadCharacteristics _rt;
    private readonly List<RingAudioSource> _rings = new(2);
    private readonly List<long> _ringVoiceIds = new(2);   // kept in lockstep with _rings (primary = 0)
    private readonly int _blockFrames;
    private readonly int _ringFrames;
    private readonly int _targetAheadFrames;

    // Lock-free SPSC retire queue (spec §7.9): the RT thread is the SOLE producer (EnqueueRetire); the worker is the SOLE
    // consumer (drains in WorkerPumpOnce → disposes the ring off the RT thread). Power-of-two capacity; drop if full.
    private readonly long[] _retireQ = new long[16];
    private int _retireHead;   // consumer (worker) cursor
    private int _retireTail;   // producer (RT) cursor

    private readonly Signal<int> _xruns = new(0);
    private long _xrunCount;

    private Thread? _rtThread, _workerThread, _clockThread;
    private volatile bool _run;
    private bool _disposed;

    /// <summary>Create a feed over <paramref name="session"/>. <paramref name="blockFrames"/> is the per-callback block
    /// (≈ the device period); <paramref name="rt"/> supplies the MMCSS registration (null ⇒ headless no-op).</summary>
    public AudioFeedThread(PcmAudioSession session, int blockFrames = 480, IRtThreadCharacteristics? rt = null,
        int ringFrames = 8192, int targetAheadFrames = 4096)
    {
        _session = session;
        _rt = rt ?? NullRtThreadCharacteristics.Instance;
        _blockFrames = Math.Clamp(blockFrames, 1, session.Format.SampleRate);
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
    /// <summary>The registered per-voice decode rings (for tests/diagnostics).</summary>
    public IReadOnlyList<RingAudioSource> Rings => _rings;

    /// <summary>Called by <see cref="PcmAudioSession.SetVoice"/> when a feed is attached: wrap a decoding voice in a
    /// decode↔RT firewall ring and register it so the worker pumps it and the RT loop watches it for underrun. The primary
    /// voice is (re)installed by <c>SetVoice</c>, so the previous ring is retired here.</summary>
    public IAudioSource Wrap(IAudioSource inner)
    {
        for (int i = 0; i < _rings.Count; i++) _rings[i].Dispose();
        _rings.Clear();
        _ringVoiceIds.Clear();
        var ring = new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2);
        _rings.Add(ring);
        _ringVoiceIds.Add(0);   // the primary voice's ring
        return ring;
    }

    /// <summary>Wrap an ADDITIONAL crossfade voice (spec §8) in its own decode↔RT firewall ring and register it WITHOUT
    /// clearing the primary — the worker pumps it and the RT loop watches it for underrun, exactly like the primary. The
    /// ring is tagged with <paramref name="voiceId"/> (the mixer voice id) so the worker can dispose it off-RT when the RT
    /// thread retires that voice (via <see cref="EnqueueRetire"/>). CONTROL thread only (called from
    /// <see cref="PcmAudioSession.AddCrossfadeVoice"/>).</summary>
    public IAudioSource WrapAdditional(IAudioSource inner, long voiceId)
    {
        var ring = new RingAudioSource(inner, _session.Format.Channels, _ringFrames, _targetAheadFrames, _blockFrames * 2);
        _rings.Add(ring);
        _ringVoiceIds.Add(voiceId);
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

    // ── RT feed thread — copy+mix ONLY ───────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE RT callback (spec §7.9): render+present one block through the published graph (lock-free consume) and,
    /// if a voice ring underran, bump the xrun counter + write silence for the shortfall. Alloc/lock/syscall-free (the
    /// <see cref="AudioTripwire"/> around <see cref="PcmAudioSession.RenderBlock"/> enforces it). Returns frames presented.</summary>
    public int FeedOnce()
    {
        int rendered = _session.RtRenderOnce(_blockFrames);

        // Underrun detection is a cheap read-and-clear of each ring's latch — no alloc, no lock (Interlocked only).
        bool starved = false;
        var rings = _rings;
        for (int i = 0; i < rings.Count; i++)
            if (rings[i].ConsumeStarve()) starved = true;
        if (starved) Interlocked.Increment(ref _xrunCount);

        return rendered;
    }

    // ── worker — decode AHEAD ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE worker pass: decode/decrypt every voice ring up to its target-ahead depth. Off the RT thread.</summary>
    public void WorkerPumpOnce()
    {
        var rings = _rings;
        for (int i = 0; i < rings.Count; i++) rings[i].PumpAhead();
        DrainRetireQueue();
    }

    /// <summary>Drain the RT→worker retire queue: dispose each retired voice's ring and remove it from <see cref="_rings"/>
    /// / <see cref="_ringVoiceIds"/> (kept in lockstep). Worker thread ONLY — the sole consumer, so dispose is safe.</summary>
    private void DrainRetireQueue()
    {
        int head = _retireHead;
        while (head != Volatile.Read(ref _retireTail))
        {
            long voiceId = _retireQ[head];
            head = (head + 1) & (_retireQ.Length - 1);
            for (int i = 0; i < _ringVoiceIds.Count; i++)
            {
                if (_ringVoiceIds[i] != voiceId) continue;
                _rings[i].Dispose();
                _rings.RemoveAt(i);
                _ringVoiceIds.RemoveAt(i);
                break;
            }
        }
        Volatile.Write(ref _retireHead, head);
    }

    // ── control / clock tick — off the RT thread ─────────────────────────────────────────────────────────────────────

    /// <summary>ONE control tick (spec §7.6/§7.9): advance the state machine, reconcile effects, sample the
    /// <c>IAudioClock</c> and publish <c>Position</c> (all off the RT thread), then publish the xrun signal.</summary>
    public void ControlTickOnce()
    {
        _session.TickControl(_blockFrames);
        int x = (int)Interlocked.Read(ref _xrunCount);
        if (_xruns.Peek() != x) _xruns.Value = x;
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

    /// <summary>Stop the live threads (idempotent). Teardown never throws.</summary>
    public void Stop()
    {
        _run = false;
        try { _rtThread?.Join(500); } catch { }
        try { _workerThread?.Join(500); } catch { }
        try { _clockThread?.Join(500); } catch { }
        _rtThread = _workerThread = _clockThread = null;
    }

    private void RtLoop()
    {
        using var _ = _rt.Enter();   // MMCSS Pro-Audio for the lifetime of the RT thread
        while (_run)
        {
            FeedOnce();
            Thread.Yield();          // sink-write backpressure paces us; no wall-clock sleep on the RT path
        }
    }

    private void WorkerLoop()
    {
        while (_run) { WorkerPumpOnce(); Thread.Yield(); }
    }

    private void ClockLoop()
    {
        while (_run) { ControlTickOnce(); Thread.Yield(); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        for (int i = 0; i < _rings.Count; i++) _rings[i].Dispose();
        _rings.Clear();
    }
}
