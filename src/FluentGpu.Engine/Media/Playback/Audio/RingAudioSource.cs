using System;
using System.Diagnostics;
using System.Threading;

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

    private long _readFrames;             // frames drained by the RT thread (the mixer-domain cursor)
    private int _starve;                  // xrun flag latch (Interlocked-published by the RT thread)
    private int _flushRequest;            // worker sets after an inner seek; RT consumes by discarding buffered pre-seek PCM
    private volatile bool _producerDone;  // the worker exhausted the inner source

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

    // ── worker (producer) side ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>WORKER: decode the inner source into the ring until it holds at least the target-ahead depth (or the ring is
    /// full, or the inner source is exhausted). Decode/decrypt happen here — never on the RT thread. Returns the frames
    /// decoded this call. Idempotent when already full.</summary>
    public int PumpAhead()
    {
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
                if (_inner.Exhausted) _producerDone = true;
                break;
            }
            int wrote = _ring.Write(_pump.AsSpan(0, got * _channels));
            decodedFrames += wrote / _channels;
            if (wrote < got * _channels) break;                // ring filled mid-block — done for now
        }
        if (_inner.Exhausted) _producerDone = true;
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
        if (_inner is DecoderAudioSource das) das.SeekFrame(frame);
        else if (_inner is MemoryAudioSource mas) mas.SeekFrame(frame);
        _producerDone = _inner.Exhausted;
        Interlocked.Exchange(ref _flushRequest, 1);
    }

    // ── RT (consumer) side — copy ONLY (never touches _inner; see the firewall invariant on the type) ────────────────────

    /// <summary>RT: consume a pending seek flush — discard everything buffered (a consumer-side <see cref="PcmRing"/> head
    /// jump; SPSC-legal, the consumer owns the head). Reads only the managed ring — never <see cref="_inner"/>.</summary>
    public void RtConsumeFlush()
    {
        AssertRtFirewall();
        if (Interlocked.Exchange(ref _flushRequest, 0) == 1) _ring.DiscardAllConsumerSide();
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
        if (got < dst.Length && !_producerDone)
            Interlocked.Exchange(ref _starve, 1);

        return frames;
    }

    /// <inheritdoc/>
    public long PositionFrames => Volatile.Read(ref _readFrames);

    /// <inheritdoc/>
    public bool Exhausted => _producerDone && _ring.AvailableFloats < _channels;

    /// <inheritdoc/>
    public GaplessInfo Gapless => _inner.Gapless;
    /// <inheritdoc/>
    public ReplayGainInfo Loudness => _inner.Loudness;

    /// <summary>True once an underrun was latched (spec §7.9). Read-and-clear via <see cref="ConsumeStarve"/> on the RT loop.</summary>
    public bool Starved => Volatile.Read(ref _starve) != 0;

    /// <summary>RT loop: atomically read-and-clear the underrun latch (drives the xrun counter).</summary>
    public bool ConsumeStarve() { AssertRtFirewall(); return Interlocked.Exchange(ref _starve, 0) != 0; }

    /// <summary>DEBUG-only anchor for the firewall invariant: the RT-facing members read only the managed ring, never
    /// <see cref="_inner"/> — so the worker may dispose the inner while the RT still holds the ring for ≤1 block. Erased
    /// from the shipping AOT binary (production safety == CI coverage); alloc-free when it holds.</summary>
    [Conditional("DEBUG")]
    private void AssertRtFirewall() => Debug.Assert(_ring is not null, "RingAudioSource RT firewall: RT-facing members must read only the managed ring, never _inner (worker-only).");

    /// <inheritdoc/>
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
