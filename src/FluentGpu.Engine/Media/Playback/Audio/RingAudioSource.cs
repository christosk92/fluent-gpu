using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Media;

/// <summary>
/// The decode↔RT firewall (spec §7.9 + §12 thread-ownership): a lock-free <see cref="PcmRing"/> in front of a decoding
/// <see cref="IAudioSource"/>. The WORKER pool decodes ahead into the ring (<see cref="PumpAhead"/> — decode/decrypt/fetch
/// live HERE, never on the RT thread); the RT feed thread's mixer drains the ring (<see cref="Read"/> — copy ONLY). On
/// underrun (the producer fell behind and the inner source is NOT exhausted) <see cref="Read"/> returns a SHORT read and
/// raises <see cref="Starved"/> — the RT loop writes silence for the shortfall and bumps its xrun counter, never blocking.
/// Position/exhaustion/gapless/loudness are forwarded so the ring is invisible to the mixer above it.
/// <para>Single-producer (worker) / single-consumer (RT). The inner source is touched ONLY by the worker.</para>
/// <para><b>Firewall invariant (spec §7.9/§12):</b> the RT-facing members (<see cref="Read"/>, <see cref="Exhausted"/>,
/// <see cref="ConsumeStarve"/>, <see cref="RtConsumeFlush"/>, <see cref="RtTryJump"/>, <see cref="GrowAhead"/>) read ONLY the managed <see cref="PcmRing"/> — they never
/// touch <see cref="_inner"/>. That is what makes worker-side inner disposal safe for the ≤1 block the RT thread may still
/// hold the ring reference after a retire: a torn inner yields short reads/silence, never a use-after-free. No quarantine
/// needed for rings — the firewall IS the quarantine.</para>
/// </summary>
public sealed class RingAudioSource : IAudioSource, IDisposable
{
    private readonly IAudioSource _inner;
    private readonly IRtThreadCharacteristics? _rt;   // the INSTANCE seam the producer thread registers through (no static — V-PE34)
    private readonly PcmRing _ring;
    private readonly int _channels;
    private int _targetFloats;            // keep the ring at least this full ahead of the RT thread; GrowAhead (RT, once per incident) deepens it
    private readonly float[] _pump;       // worker-owned decode scratch (never touched by the RT thread)
    private int _carryOffset, _carryFloats;   // worker-owned: decoded floats in _pump the ring had no room for yet (see PumpAhead)

    private long _decodedEndFrame = -1;
    /// <summary>Exact source-domain EOF position once the producer reached it; -1 while unknown.</summary>
    public long DecodedEndFrame => Interlocked.Read(ref _decodedEndFrame);
    private long _readFrames;             // frames drained by the RT thread (the mixer-domain cursor)
    private int _starve;                  // xrun flag latch (Interlocked-published by the RT thread) — set on the FIRST short read of an incident only
    private int _starveFrames;            // frames of silence accrued since the last ConsumeStarveFrames (severity, not just "it happened")
    private int _inIncident;              // RT-owned: a starvation incident is open (V-PE30) — cleared by the first full read after it
    private int _flushRequest;            // worker sets after an inner seek; RT consumes by discarding buffered pre-seek PCM
    private volatile bool _producerDone;  // the worker exhausted the inner source
    private readonly object _producerGate = new();
    private readonly CancellationTokenSource _producerCancellation = new();
    private readonly AutoResetEvent _producerWake = new(false);
    private const int IdleProducerWaitMs = 500;   // safety net only: low-water, seek and dispose all signal _producerWake
    /// <summary>Wake this voice's producer on low-water, seek or cancellation.</summary>
    public void WakeProducer()
    {
        try { _producerWake.Set(); } catch (ObjectDisposedException) { }
    }
    /// <summary>The name every dedicated decode-ahead producer thread carries (diagnostics + tests).</summary>
    public const string ProducerThreadName = "FluentGpu.AudioProducer";
    private Thread? _producer;
    private int _disposed;
    internal RingAudioSource? RetirementNext;
    private int _retirementRequested;
    internal bool TryRequestRetirement() => Interlocked.Exchange(ref _retirementRequested, 1) == 0;
    private int _innerDisposed;
    private long _seekFrame = -1;
    private TaskCompletionSource<long>? _seekCompletion;
    private readonly object _seekGate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _producerFault;

    /// <summary>True when a dedicated producer owns decoding for this ring.</summary>
    public bool HasDedicatedProducer => Volatile.Read(ref _producer) is not null;
    /// <summary>Decode failure, observed off the render thread.</summary>
    public Exception? ProducerFault => Volatile.Read(ref _producerFault);

    /// <summary>Start an isolated producer. A blocked next voice cannot stall the active source.
    /// <para>A real <see cref="Thread"/> at <see cref="ThreadPriority.AboveNormal"/> (#113): decode-ahead must win scheduling
    /// against Normal app/ThreadPool work the same way <c>FluentGpu.AudioWorker</c> does — the LongRunning Task this replaced
    /// ran at Normal and starved under a busy machine (the reporter compiles while listening). Stays below the RT feed
    /// thread's Highest. Priority alone is not enough on a loaded box: the thread also registers through the INSTANCE
    /// <see cref="IRtThreadCharacteristics.EnterDecode"/> seam (MMCSS "Audio" on Windows) for its whole life, so the scheduler
    /// treats decode-ahead as multimedia work (F1).</para></summary>
    public void StartProducer()
    {
        lock (_producerGate)
        {
            if (_producer is not null || _disposed != 0) return;
            var t = new Thread(Produce)
            {
                IsBackground = true,
                Name = ProducerThreadName,
                Priority = ThreadPriority.AboveNormal,
            };
            _producer = t;
            t.Start();
        }
    }

    /// <summary>Tests only: wait up to <paramref name="timeoutMs"/> for the producer thread to exit (true when it has, or
    /// when none was started). Production never joins — see the no-join contract in <see cref="Dispose"/>.</summary>
    internal bool JoinProducer(int timeoutMs) => Volatile.Read(ref _producer)?.Join(timeoutMs) ?? true;

    private void Produce()
    {
        try
        {
            using var rtToken = _rt?.EnterDecode();   // MMCSS "Audio" for the producer's whole life (F1); reverted when the thread leaves the try
            while (!_producerCancellation.IsCancellationRequested)
            {
                ApplyQueuedSeek();
                PumpAhead();
                if (BufferedFrames >= TargetFrames || ProducerDone) _ready.TrySetResult();
                // Wake any waiter whose minimum is now met (or whose producer finished). A manual-reset event: waiters Reset → check →
                // wait, so a Set landing between their check and their wait is never lost.
                if (IsReady(Volatile.Read(ref ReadyMinimum))) ReadyWake.Set();
                // Ring at its decode-ahead target (or the source finished): nothing to do until the RT low-water edge, a seek or
                // disposal signals _producerWake, so sleep long (the timeout is only a safety net). While below target the inner
                // source stalled (a network read with nothing ready) and has no signal to wait on, so it keeps its 20 ms retry.
                bool idle = ProducerDone || _ring.AvailableFloats >= Volatile.Read(ref _targetFloats);
                _producerWake.WaitOne(idle ? IdleProducerWaitMs : 20);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _producerFault, e);
            _producerDone = true;
            _ready.TrySetException(e);
            try { ReadyWake.Set(); } catch (ObjectDisposedException) { }   // a waiter re-checks and surfaces the fault instead of waiting out its recheck
        }
        finally
        {
            if (Volatile.Read(ref _disposed) != 0) DisposeInner();
            _producerWake.Dispose();
            ReadyWake.Dispose();
        }
    }

    /// <summary>Wait for real PCM or confirmed EOF; cancellation never manufactures readiness.</summary>
    public Task WaitUntilReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);
    /// <summary>Wait for a smaller transport prefill while the producer continues toward normal decode-ahead. Event-driven (no
    /// timer polling): <see cref="ReadyWake"/> is Reset → checked → waited on, with a 20 ms recheck that bounds any lost wake-up
    /// when several waiters share the event. Cancellation or disposal throws; a producer fault is rethrown.</summary>
    public async Task WaitUntilReadyAsync(int minimumFrames, CancellationToken ct)
    {
        Volatile.Write(ref ReadyMinimum, minimumFrames);
        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(RingAudioSource));
            ReadyWake.Reset();                                              // Reset FIRST …
            if (BufferedFrames >= minimumFrames || ProducerDone) break;     // … THEN check …
            if (ProducerFault is { } fault) throw fault;
            await WaitReadyWakeAsync(20, ct).ConfigureAwait(false);         // … THEN wait: a Set after the check is not lost
        }
        ct.ThrowIfCancellationRequested();
        if (ProducerFault is { } failure) throw failure;
    }

    // Await ReadyWake without a timer tick: the pool's registered-wait thread completes the TCS when the handle signals (true) or
    // the timeout elapses (false). The registration is always unregistered; cancellation completes the await as cancelled.
    private async Task<bool> WaitReadyWakeAsync(int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(ReadyWake,
            static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut), tcs, timeoutMs, executeOnlyOnce: true);
        using var cancellation = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }
    private int _belowRefill;             // RT-owned edge latch for the idle producer's refill wake — see CheckRefillEdge
    private int _belowLowWater;           // RT-owned edge latch (spec §7.9) for the worker low-water wake — see CheckLowWaterEdge

    /// <summary>Wrap <paramref name="inner"/> with a ring sized to <paramref name="ringFrames"/> frames, keeping
    /// <paramref name="targetAheadFrames"/> decoded ahead of the RT thread and <paramref name="keepBehindFrames"/> already-played
    /// frames intact behind the read head (an instant backward seek / in-ring jump; 0 ⇒ none). The ring is enlarged to hold
    /// ahead + kept-behind + one pump block. The worker owns a <paramref name="pumpFrames"/> decode-scratch block.
    /// <paramref name="startFrames"/> is the content position the ring's read cursor starts at (V-PE3): the cursor begins at the
    /// larger of it and <c>inner.PositionFrames</c>, so a ring over an already-sought decoder reports its real position rather than 0.
    /// <paramref name="rt"/> is the INSTANCE characteristics seam the producer thread registers through (null ⇒ none).</summary>
    public RingAudioSource(IAudioSource inner, int channels, int ringFrames = 8192, int targetAheadFrames = 4096, int pumpFrames = 1024,
                           int keepBehindFrames = 0, long startFrames = 0, IRtThreadCharacteristics? rt = null)
    {
        _inner = inner;
        _rt = rt;
        _channels = Math.Max(1, channels);
        keepBehindFrames = Math.Max(0, keepBehindFrames);
        _ring = new PcmRing(Math.Max(ringFrames, targetAheadFrames + keepBehindFrames + pumpFrames) * _channels, keepBehindFrames * _channels);
        _targetFloats = Math.Min(_ring.CapacityFloats - _ring.KeepBehindFloats, targetAheadFrames * _channels);
        _pump = new float[Math.Max(1, pumpFrames) * _channels];
        _readFrames = Math.Max(startFrames, inner.PositionFrames);   // V-PE3: the content cursor starts where the (already sought) decoder is
    }

    /// <summary>The inner (decoding) source — worker-only access.</summary>
    public IAudioSource Inner => _inner;

    /// <summary>Frames currently buffered ahead of the RT thread (spec §7.9 sizing/diagnostics gates). Safe from either
    /// thread — a Volatile snapshot of the underlying <see cref="PcmRing"/> fill.</summary>
    public int BufferedFrames => _ring.AvailableFloats / _channels;
    /// <summary>Frames of already-played audio still intact behind the read head (the kept-behind span, exact per
    /// <see cref="PcmRing.BehindFloats"/>): how far back <see cref="RtTryJump"/> can rewind. Safe from either thread (advisory
    /// off the RT thread — the RT re-validates).</summary>
    public int KeptBehindFrames => _ring.BehindFloats / _channels;
    /// <summary>Producer must wait until the output consumer acknowledges this flush.</summary>
    public bool HasPendingFlush => Volatile.Read(ref _flushRequest) != 0;

    /// <summary>The target decode-ahead depth in frames (spec §7.9 sizing/diagnostics gates) — the worker's
    /// <see cref="PumpAhead"/> fills toward this; the low-water wake (<see cref="CheckLowWaterEdge"/>) fires at half of it.
    /// <see cref="GrowAhead"/> raises it after an incident.</summary>
    public int TargetFrames => Volatile.Read(ref _targetFloats) / _channels;

    /// <summary>"The ring holds ≥ <see cref="ReadyMinimum"/> frames (or the producer is done)" — a MANUAL-reset kernel event the
    /// producer Sets after every pump. Waiters follow Reset → check (<see cref="IsReady"/>) → wait so a Set landing between the
    /// check and the wait is never lost (V-PE10). Disposed with the ring.</summary>
    public readonly ManualResetEvent ReadyWake = new(false);
    /// <summary>The frame count <see cref="ReadyWake"/> signals for. A waiter writes it (Volatile) before its loop; the producer
    /// reads it (Volatile) after each pump.</summary>
    public int ReadyMinimum;
    /// <summary>True when the ring holds at least <paramref name="minFrames"/> frames of REAL post-flush PCM, or the producer has
    /// reached EOF / failed (so waiting longer cannot help). A ring with a pending seek flush is never ready: what it holds is
    /// pre-seek audio the RT thread is about to discard.</summary>
    public bool IsReady(int minFrames) => !HasPendingFlush && (BufferedFrames >= minFrames || ProducerDone);

    private int KeepBehindFloats => _ring.KeepBehindFloats;

    // ── worker (producer) side ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>WORKER: decode the inner source into the ring until it holds at least the target-ahead depth (or the ring is
    /// full, or the inner source is exhausted). Decode/decrypt happen here — never on the RT thread. Returns the frames
    /// decoded this call. Idempotent when already full.</summary>
    public int PumpAhead()
    {
        ApplyQueuedSeek();
        if (Volatile.Read(ref _flushRequest) != 0) return 0;   // seek pending: don't write post-seek PCM until RT discards pre-seek PCM
        int decodedFrames = 0;
        while (!_producerDone && _ring.AvailableFloats < Volatile.Read(ref _targetFloats))
        {
            // A previous pass left decoded PCM the ring could not take (the consumer jumped BACK into the kept-behind span between
            // our free-space read and the write, shrinking the room): that audio is already out of the decoder, so it goes in first.
            if (_carryFloats > 0)
            {
                int room = _ring.FreeFloats / _channels * _channels;
                int flushed = room > 0 ? _ring.Write(_pump.AsSpan(_carryOffset, Math.Min(_carryFloats, room))) : 0;
                _carryOffset += flushed;
                _carryFloats -= flushed;
                decodedFrames += flushed / _channels;
                if (_carryFloats > 0) break;                   // still no room — retry on the next pass; never read past an unwritten carry
                continue;
            }

            int free = _ring.FreeFloats;
            if (free < _channels) break;                       // no room for even one frame
            int wantFloats = Math.Min(_pump.Length, free);
            int wantFrames = wantFloats / _channels;
            if (wantFrames <= 0) break;

            int got = _inner.Read(_pump.AsSpan(0, wantFrames * _channels), _channels);
            if (got <= 0)
            {
                // A 0-read is EOF ONLY if the inner source says so (Exhausted) — e.g. a network source with nothing
                // ready RIGHT NOW (a stalled fetch) is NOT exhausted, and must not be latched as done. End this pass
                // without spinning; the caller retries promptly on its own cadence (the worker's low-water wake or its
                // bounded poll — spec §7.9), so a transient stall here just costs one pass, not a falsely-retired voice.
                if (_inner.Exhausted) _producerDone = true;
                break;
            }
            int wrote = _ring.Write(_pump.AsSpan(0, got * _channels));
            decodedFrames += wrote / _channels;
            if (wrote < got * _channels)
            {
                // Ring filled mid-block — keep the remainder (see above) and finish the pass.
                _carryOffset = wrote;
                _carryFloats = got * _channels - wrote;
                break;
            }
        }
        if (_inner.Exhausted && _carryFloats == 0) _producerDone = true;   // never EOF while decoded PCM is still waiting for room
        if (_producerDone) Interlocked.Exchange(ref _decodedEndFrame, _inner.PositionFrames);
        return decodedFrames;
    }

    /// <summary>WORKER: mark the producer failed/finished (a contained decode fault) — the mixer sees EOF and retires the
    /// voice naturally; the ring is then disposed off-RT via the normal retire path (spec §7.9). Worker thread only.</summary>
    public void MarkFailed() => _producerDone = true;

    /// <summary>WORKER: apply a control-requested seek to the inner decoder (the sole-toucher invariant — spec §7.9/§12),
    /// then ask the RT consumer to discard the buffered pre-seek PCM. The worker will not pump again until the flush is
    /// consumed (<see cref="PumpAhead"/> early-returns while it is pending). Worker thread only.</summary>
    public void WorkerApplySeek(long frame)
    {
        if (HasDedicatedProducer) { _ = SeekFrameAsync(frame); return; }
        ApplySeek(frame);
    }

    /// <summary>Seek on the sole producer and acknowledge the actual new decoder position.</summary>
    public Task<long> SeekFrameAsync(long frame)
    {
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_seekGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Task.FromException<long>(new ObjectDisposedException(nameof(RingAudioSource)));
            _seekCompletion?.TrySetCanceled();
            _seekCompletion = completion;
            _seekFrame = frame;
        }
        WakeProducer();
        return completion.Task;
    }

    private void ApplyQueuedSeek()
    {
        TaskCompletionSource<long>? completion;
        long frame;
        lock (_seekGate)
        {
            frame = _seekFrame;
            completion = _seekCompletion;
            _seekFrame = -1;
            _seekCompletion = null;
        }
        if (frame < 0) return;
        try { ApplySeek(frame); completion?.TrySetResult(_inner.PositionFrames); }
        catch (Exception e) { completion?.TrySetException(e); }
    }

    private void ApplySeek(long frame)
    {
        if (_inner is DecoderAudioSource das) das.SeekFrame(frame);
        else if (_inner is TrimmingSource ts) ts.SeekFrame(frame);
        else if (_inner is MemoryAudioSource mas) mas.SeekFrame(frame);
        else throw new NotSupportedException("This audio source does not support seeking.");
        _carryOffset = _carryFloats = 0;   // anything still waiting for ring room is pre-seek PCM
        _producerDone = _inner.Exhausted;
        Interlocked.Exchange(ref _decodedEndFrame, -1);
        Interlocked.Exchange(ref _readFrames, _inner.PositionFrames);
        Interlocked.Exchange(ref _flushRequest, 1);
    }

    // ── RT (consumer) side — copy ONLY (never touches _inner; see the firewall invariant on the type) ────────────────────

    /// <summary>RT: consume a pending seek flush — discard everything buffered (a consumer-side <see cref="PcmRing"/> head
    /// jump; SPSC-legal, the consumer owns the head). Reads only the managed ring — never <see cref="_inner"/>.</summary>
    public void RtConsumeFlush()
    {
        AssertRtFirewall();
        if (Volatile.Read(ref _flushRequest) != 0)
        {
            _ring.DiscardAllConsumerSide();
            // Release producer only after advancing the consumer head. The reverse order discarded freshly sought PCM.
            Volatile.Write(ref _flushRequest, 0);
        }
    }

    /// <summary>RT: move the read cursor by <paramref name="deltaFrames"/> — positive skips forward inside the already-published
    /// PCM, negative rewinds into the kept-behind span (<see cref="KeptBehindFrames"/>). Returns false (nothing moved) when the
    /// target lies outside what the ring still holds. The content cursor (<see cref="PositionFrames"/>) moves with the head; the
    /// producer and its decoder are untouched (they keep decoding ahead). Copy-only: wait-free, alloc-free, no lock.</summary>
    public bool RtTryJump(int deltaFrames)
    {
        AssertRtFirewall();
        int limit = int.MaxValue / _channels;               // a frame count whose float count would overflow int can never be inside the ring
        if (deltaFrames > limit || deltaFrames < -limit) return false;
        bool ok = deltaFrames >= 0
            ? _ring.TrySkipConsumerSide(deltaFrames * _channels)
            : _ring.TryRewindConsumerSide(-deltaFrames * _channels);
        if (ok) Interlocked.Add(ref _readFrames, deltaFrames);
        return ok;
    }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        AssertRtFirewall();
        if (channels != _channels) channels = _channels;
        int got = _ring.Read(dst);
        int frames = got / channels;
        _readFrames += frames;

        // Underrun: the RT thread wanted more than the worker had ready AND there is more audio to come → xrun.
        // Record the SHORTFALL (frames of silence the RT loop is about to write), not just the fact — a bare latch
        // cannot say how much audio was actually lost, which is the whole reason "xruns=17" was undiagnosable.
        // ONE xrun per incident (V-PE30): the latch is set only on the FIRST short read of an incident; every block of the incident
        // still accrues its frames. The incident closes at the first read that is not short (or at EOF).
        if (got < dst.Length && !_producerDone)
        {
            if (_inIncident == 0) { _inIncident = 1; Interlocked.Exchange(ref _starve, 1); }
            Interlocked.Add(ref _starveFrames, (dst.Length - got) / channels);
        }
        else _inIncident = 0;

        return frames;
    }

    /// <inheritdoc/>
    public long PositionFrames => Volatile.Read(ref _readFrames);

    /// <inheritdoc/>
    public bool Exhausted => _producerDone && _ring.AvailableFloats < _channels;

    /// <summary>The worker reached the end of the inner source — DISTINCT from <see cref="Exhausted"/>, which also
    /// requires the ring to have been drained. A prefill gate must use this one: a source shorter than the decode-ahead
    /// target (a short track, or any track whose whole body already fits) can never reach that target and is not yet
    /// <see cref="Exhausted"/> while its PCM sits buffered — waiting for either would stall on a ring that is as full
    /// as it will ever get.</summary>
    public bool ProducerDone => _producerDone;

    /// <inheritdoc/>
    public GaplessInfo Gapless => _inner.Gapless;
    /// <inheritdoc/>
    public ReplayGainInfo Loudness => _inner.Loudness;

    /// <summary>True once an underrun was latched (spec §7.9). Read-and-clear via <see cref="ConsumeStarve"/> on the RT loop.</summary>
    public bool Starved => Volatile.Read(ref _starve) != 0;

    /// <summary>Record an observed output starvation interval (RT, the session's silence path) without consuming source PCM. Shares
    /// the per-incident latch with <see cref="Read"/>: only the first block of an incident raises <see cref="ConsumeStarve"/>, every
    /// block adds its frames — so a run of silence blocks is ONE xrun with a growing severity (V-PE30).</summary>
    internal void RecordStarvedFrames(int frames)
    {
        if (frames <= 0) return;
        if (_inIncident == 0) { _inIncident = 1; Interlocked.Exchange(ref _starve, 1); }
        Interlocked.Add(ref _starveFrames, frames);
    }

    /// <summary>RT loop: atomically read-and-clear the underrun latch (drives the xrun counter).</summary>
    public bool ConsumeStarve() { AssertRtFirewall(); return Interlocked.Exchange(ref _starve, 0) != 0; }

    /// <summary>RT loop: atomically read-and-clear the accrued underrun SHORTFALL in frames (0 ⇒ no starve since the last
    /// call). This is the severity half of <see cref="ConsumeStarve"/>: the xrun counter says how many callbacks starved,
    /// this says how much audio was actually replaced by silence — the number a user-facing dropout report needs.</summary>
    public int ConsumeStarveFrames() { AssertRtFirewall(); return Interlocked.Exchange(ref _starveFrames, 0); }

    /// <summary>RT: edge-triggered low-water check (spec §7.9, worker wake) — true the FIRST time the buffered fill drops
    /// below half the target-ahead depth since it was last at/above that mark, false on every subsequent call while it
    /// stays low. This is what makes <see cref="AudioFeedThread.FeedOnce"/>'s worker-wake fire ONCE per drop rather than
    /// on every block: a real signal, not a poll. Alloc/lock-free — Volatile read/write only (the AudioTripwire contract).</summary>
    public bool CheckLowWaterEdge()
    {
        AssertRtFirewall();
        bool low = _ring.AvailableFloats < Volatile.Read(ref _targetFloats) / 2;
        if (low)
        {
            if (Volatile.Read(ref _belowLowWater) != 0) return false;   // already signalled this drop
            Volatile.Write(ref _belowLowWater, 1);
            return true;
        }
        Volatile.Write(ref _belowLowWater, 0);   // recovered — re-arm for the next drop
        return false;
    }

    /// <summary>RT: edge-triggered refill wake for a DEDICATED producer that sleeps while the ring is at target: true once each time the
    /// buffered fill drops an eighth of the target below it (re-armed when it recovers), so the producer tops the ring up in small
    /// steps and the decode-ahead cushion stays near the target instead of oscillating down to the half-target low-water edge. Volatile
    /// reads/writes only (the AudioTripwire contract).</summary>
    public bool CheckRefillEdge()
    {
        AssertRtFirewall();
        int target = Volatile.Read(ref _targetFloats);
        bool low = _ring.AvailableFloats < target - target / 8;
        if (low)
        {
            if (Volatile.Read(ref _belowRefill) != 0) return false;
            Volatile.Write(ref _belowRefill, 1);
            return true;
        }
        Volatile.Write(ref _belowRefill, 0);
        return false;
    }

    /// <summary>RT: deepen the decode-ahead target to twice its current depth, capped at what the ring can hold in front of the
    /// protected kept-behind span. Called ONCE per starvation incident by <see cref="AudioFeedThread.FeedOnce"/> (on the
    /// incident edge, never for a suppressed seek rebuffer): a producer that lost the race against the machine gets a bigger head
    /// start next time. A single Volatile write — alloc-free, lock-free; the producer and the low-water edge re-read the target.</summary>
    public void GrowAhead() => Volatile.Write(ref _targetFloats, Math.Min(_ring.CapacityFloats - KeepBehindFloats, _targetFloats * 2));

    /// <summary>DEBUG-only anchor for the firewall invariant: the RT-facing members read only the managed ring, never
    /// <see cref="_inner"/> — so the worker may dispose the inner while the RT still holds the ring for ≤1 block. Erased
    /// from the shipping AOT binary (production safety == CI coverage); alloc-free when it holds.</summary>
    [Conditional("DEBUG")]
    private void AssertRtFirewall() => Debug.Assert(_ring is not null, "RingAudioSource RT firewall: RT-facing members must read only the managed ring, never _inner (worker-only).");

    private void DisposeInner()
    {
        if (Interlocked.Exchange(ref _innerDisposed, 1) == 0) (_inner as IDisposable)?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_producerGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _producerCancellation.Cancel();
            lock (_seekGate)
            {
                _seekCompletion?.TrySetCanceled();
                _seekCompletion = null;
                _seekFrame = -1;
            }
            _ready.TrySetCanceled();
            WakeProducer();
            try { (_inner as ICancellableAudioSource)?.CancelPendingRead(); } catch (ObjectDisposedException) { }
            if (_producer is null || !_producer.IsAlive) DisposeInner();
            if (_producer is null) { _producerWake.Dispose(); ReadyWake.Dispose(); }   // a live producer disposes both in its finally
            // A running decoder owns its source until its finally block. Never free it under a blocked read.
        }
    }
}
