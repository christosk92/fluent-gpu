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
/// <see cref="ConsumeStarve"/>, <see cref="RtConsumeFlush"/>) read ONLY the managed <see cref="PcmRing"/> — they never
/// touch <see cref="_inner"/>. That is what makes worker-side inner disposal safe for the ≤1 block the RT thread may still
/// hold the ring reference after a retire: a torn inner yields short reads/silence, never a use-after-free. No quarantine
/// needed for rings — the firewall IS the quarantine.</para>
/// </summary>
public sealed class RingAudioSource : IAudioSource, IDisposable
{
    private readonly IAudioSource _inner;
    private readonly PcmRing _ring;
    private readonly int _channels;
    private readonly int _targetFloats;   // keep the ring at least this full ahead of the RT thread
    private readonly float[] _pump;       // worker-owned decode scratch (never touched by the RT thread)

    private long _decodedEndFrame = -1;
    /// <summary>Exact source-domain EOF position once the producer reached it; -1 while unknown.</summary>
    public long DecodedEndFrame => Interlocked.Read(ref _decodedEndFrame);
    private long _readFrames;             // frames drained by the RT thread (the mixer-domain cursor)
    private int _starve;                  // xrun flag latch (Interlocked-published by the RT thread)
    private int _starveFrames;            // frames of silence accrued since the last ConsumeStarveFrames (severity, not just "it happened")
    private int _flushRequest;            // worker sets after an inner seek; RT consumes by discarding buffered pre-seek PCM
    private volatile bool _producerDone;  // the worker exhausted the inner source
    private readonly object _producerGate = new();
    private readonly CancellationTokenSource _producerCancellation = new();
    private readonly AutoResetEvent _producerWake = new(false);
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
    /// thread's Highest.</para></summary>
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
            while (!_producerCancellation.IsCancellationRequested)
            {
                ApplyQueuedSeek();
                PumpAhead();
                if (BufferedFrames >= TargetFrames || ProducerDone) _ready.TrySetResult();
                _producerWake.WaitOne(20);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _producerFault, e);
            _producerDone = true;
            _ready.TrySetException(e);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) != 0) DisposeInner();
            _producerWake.Dispose();
        }
    }

    /// <summary>Wait for real PCM or confirmed EOF; cancellation never manufactures readiness.</summary>
    public Task WaitUntilReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);
    /// <summary>Wait for a smaller transport prefill while the producer continues toward normal decode-ahead.</summary>
    public async Task WaitUntilReadyAsync(int minimumFrames, CancellationToken ct)
    {
        while (BufferedFrames < minimumFrames && !ProducerDone)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(RingAudioSource));
            if (ProducerFault is { } fault) throw fault;
            await Task.Delay(2, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (ProducerFault is { } failure) throw failure;
    }
    private int _belowLowWater;           // RT-owned edge latch (spec §7.9) for the worker low-water wake — see CheckLowWaterEdge

    /// <summary>Wrap <paramref name="inner"/> with a ring sized to <paramref name="ringFrames"/> frames, keeping
    /// <paramref name="targetAheadFrames"/> decoded ahead of the RT thread. The worker owns a <paramref name="pumpFrames"/>
    /// decode-scratch block.</summary>
    public RingAudioSource(IAudioSource inner, int channels, int ringFrames = 8192, int targetAheadFrames = 4096, int pumpFrames = 1024)
    {
        _inner = inner;
        _channels = Math.Max(1, channels);
        _ring = new PcmRing(Math.Max(ringFrames, targetAheadFrames + pumpFrames) * _channels);
        _targetFloats = Math.Min(_ring.CapacityFloats, targetAheadFrames * _channels);
        _pump = new float[Math.Max(1, pumpFrames) * _channels];
    }

    /// <summary>The inner (decoding) source — worker-only access.</summary>
    public IAudioSource Inner => _inner;

    /// <summary>Frames currently buffered ahead of the RT thread (spec §7.9 sizing/diagnostics gates). Safe from either
    /// thread — a Volatile snapshot of the underlying <see cref="PcmRing"/> fill.</summary>
    public int BufferedFrames => _ring.AvailableFloats / _channels;
    /// <summary>Producer must wait until the output consumer acknowledges this flush.</summary>
    public bool HasPendingFlush => Volatile.Read(ref _flushRequest) != 0;

    /// <summary>The target decode-ahead depth in frames (spec §7.9 sizing/diagnostics gates) — the worker's
    /// <see cref="PumpAhead"/> fills toward this; the low-water wake (<see cref="CheckLowWaterEdge"/>) fires at half of it.</summary>
    public int TargetFrames => _targetFloats / _channels;

    // ── worker (producer) side ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>WORKER: decode the inner source into the ring until it holds at least the target-ahead depth (or the ring is
    /// full, or the inner source is exhausted). Decode/decrypt happen here — never on the RT thread. Returns the frames
    /// decoded this call. Idempotent when already full.</summary>
    public int PumpAhead()
    {
        ApplyQueuedSeek();
        if (Volatile.Read(ref _flushRequest) != 0) return 0;   // seek pending: don't write post-seek PCM until RT discards pre-seek PCM
        int decodedFrames = 0;
        while (!_producerDone && _ring.AvailableFloats < _targetFloats)
        {
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
            if (wrote < got * _channels) break;                // ring filled mid-block — done for now
        }
        if (_inner.Exhausted) _producerDone = true;
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
        if (got < dst.Length && !_producerDone)
        {
            Interlocked.Exchange(ref _starve, 1);
            Interlocked.Add(ref _starveFrames, (dst.Length - got) / channels);
        }

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

    /// <summary>Record an observed output starvation interval without consuming source PCM.</summary>
    internal void RecordStarvedFrames(int frames)
    {
        if (frames <= 0) return;
        Interlocked.Exchange(ref _starve, 1);
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
        bool low = _ring.AvailableFloats < _targetFloats / 2;
        if (low)
        {
            if (Volatile.Read(ref _belowLowWater) != 0) return false;   // already signalled this drop
            Volatile.Write(ref _belowLowWater, 1);
            return true;
        }
        Volatile.Write(ref _belowLowWater, 0);   // recovered — re-arm for the next drop
        return false;
    }

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
            if (_producer is null) _producerWake.Dispose();
            // A running decoder owns its source until its finally block. Never free it under a blocked read.
        }
    }
}
