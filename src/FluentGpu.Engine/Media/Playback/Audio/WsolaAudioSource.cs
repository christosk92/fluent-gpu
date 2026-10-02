using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// Pitch-preserving, allocation-free PCM time scaling. A 40 ms window advances by a 20 ms synthesis hop;
/// a normalized waveform match chooses its analysis position within 10 ms of the content clock.
/// Construct off the render thread; Read, Reset and Rate are render-thread operations. The inner ring keeps
/// decoding off that thread. At unity, PCM passes through unchanged. Source and output clocks stay separate.
/// </summary>
public sealed class WsolaAudioSource : IAudioSource, IDisposable
{
    private readonly IAudioSource _inner;
    private readonly int _channels, _hop, _search;
    private readonly float[] _input, _tail, _output;
    private long _inputStart, _outputFrames;
    private int _inputFrames, _ready, _offset;
    private double _contentFrame, _hopRate;
    private bool _hasTail;
    private double _rate = 1;
    private readonly Mapping[] _mapping = new Mapping[4096];
    private long _mappingCount, _lastMappingOutput;
    private double _lastMappingSource, _lastMappingRate;
    private struct Mapping { public long Version, Output; public double Source, Rate; }

    /// <summary>Allocate all work buffers for a voice in the device mix format.</summary>
    public WsolaAudioSource(IAudioSource inner, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (sampleRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _inner = inner;
        _channels = channels;
        _hop = Math.Max(1, sampleRate / 50);
        _search = Math.Max(1, sampleRate / 100);
        _input = new float[(_hop * 8 + _search * 2) * channels];
        _tail = new float[_hop * channels];
        _output = new float[_hop * channels];
        Reset(inner.PositionFrames);
    }

    /// <summary>Validate the common podcast playback range without quantizing remote rates.</summary>
    public static double ClampRate(double rate) => double.IsFinite(rate) ? Math.Clamp(rate, 0.5, 3) : 1;
    /// <summary>Desired rate. A change takes effect at the next synthesis hop.</summary>
    public double Rate { get => _rate; set => _rate = ClampRate(value); }
    /// <summary>Original source, owned by this adapter; ring retirement still occurs off the render thread.</summary>
    public IAudioSource Inner => _inner;
    /// <summary>Underlying producer ring, when present.</summary>
    public RingAudioSource? Ring => _inner as RingAudioSource;
    /// <summary>Source position corresponding to output frame zero after the last reset.</summary>
    public long InitialSourceFrame { get; private set; }
    /// <inheritdoc/>
    public long PositionFrames => (long)_contentFrame;
    /// <inheritdoc/>
    public bool Exhausted => _ready == 0 && _inner.Exhausted && _contentFrame >= _inputStart + _inputFrames;
    /// <inheritdoc/>
    public GaplessInfo Gapless => _inner.Gapless;
    /// <inheritdoc/>
    public ReplayGainInfo Loudness => _inner.Loudness;

    /// <summary>Discard lookahead/overlap after an acknowledged seek or discontinuity, while output is held.</summary>
    public void Reset(long sourceFrame)
    {
        InitialSourceFrame = _inputStart = Math.Max(0, sourceFrame);
        _contentFrame = _inputStart;
        _inputFrames = _ready = _offset = 0;
        _outputFrames = 0;
        Volatile.Write(ref _mappingCount, 0);
        _hasTail = false;
    }

    /// <summary>The most OUTPUT frames a ring holding <paramref name="ringTargetFrames"/> input frames can ever let this voice
    /// preflight at the current rate. A stretched voice eats <c>rate</c> input frames per output frame and needs two hops plus
    /// the search margin in hand, so a resume cushion beyond this could never be met by a full ring and the mixer's
    /// <see cref="CrossfadeMixer.PcmReady"/> would wait forever (silence) at, say, 3× once the cushion has grown.</summary>
    internal int ReachableFrames(int ringTargetFrames)
        => _rate == 1 ? ringTargetFrames : Math.Max(_hop, (int)((ringTargetFrames - _hop * 2 - _search) / _rate));

    /// <summary>Conservative non-consuming output preflight. A producer shortfall must stall the mixer, not add silence.</summary>
    public int ReadableFrames(int requested)
    {
        if (Ring is not { } ring || ring.ProducerDone) return requested;
        if (ring.HasPendingFlush) return 0;
        long end = _inputStart + _inputFrames + ring.BufferedFrames;
        double afterReady = _contentFrame + _ready * _hopRate;
        if (_rate == 1)
            return Math.Min(requested, _ready + (int)Math.Max(0, end - Math.Ceiling(afterReady)));
        // Every new hop needs its full window plus the right-hand search margin.
        double room = end - (Math.Floor(afterReady) + _hop * 2 + _search);
        int hops = room < 0 ? 0 : 1 + (int)Math.Floor(room / (_hop * _rate));
        return Math.Min(requested, _ready + hops * _hop);
    }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (channels != _channels) throw new ArgumentException("Channel count differs from the voice format.", nameof(channels));
        int wanted = dst.Length / channels, written = 0;
        if (TryEnterPassThrough())
        {
            int got = _inner.Read(dst, channels);
            if (got > 0)
            {
                RecordMapping(_outputFrames, _contentFrame, 1);
                _outputFrames += got;
                _contentFrame += got;
                _inputStart += got;
            }
            return got;
        }
        while (written < wanted)
        {
            if (_ready == 0 && !ProduceHop()) break;
            int take = Math.Min(wanted - written, _ready);
            _output.AsSpan(_offset * channels, take * channels).CopyTo(dst[(written * channels)..]);
            RecordMapping(_outputFrames, _contentFrame, _hopRate);
            _contentFrame += take * _hopRate;
            if (_inner.Exhausted) _contentFrame = Math.Min(_contentFrame, _inputStart + _inputFrames);
            _outputFrames += take;
            _offset += take;
            _ready -= take;
            written += take;
        }
        return written;
    }

    /// <summary>R-9: true when the byte-exact unity pass-through may serve this read. Fresh after <see cref="Reset"/> that is
    /// trivially so (nothing buffered); after a stretched run it re-opens as soon as the lookahead is DRAINED — every
    /// buffered input frame has been emitted, so the next frame out is exactly the next frame the inner source delivers. The
    /// retained history is dropped and the content cursor pinned to that integral inner position (the sub-sample fraction a
    /// fractional rate left behind is discarded: &lt; 1 frame).</summary>
    private bool TryEnterPassThrough()
    {
        if (_rate != 1 || _ready != 0 || _hasTail) return false;
        if (_inputFrames == 0) return true;
        long next = _inputStart + _inputFrames;
        if ((long)Math.Floor(_contentFrame) != next) return false;
        _inputStart = next;
        _inputFrames = 0;
        _contentFrame = next;
        return true;
    }

    private bool ProduceHop()
    {
        Compact();
        long ideal = (long)Math.Floor(_contentFrame);
        bool unity = _rate == 1;
        // Q-2: the first unity hop after a stretched one still holds the overlap tail of the hop before it. Dropping it left
        // a step at the speed toggle, so the tail is blended out instead. No waveform search: unity carries no lookahead margin.
        bool returnBlend = unity && _hasTail;
        long wantedEnd = ideal + (unity ? _hop : _hop * 2 + _search);
        FillUntil(wantedEnd);
        long end = _inputStart + _inputFrames;
        if (!_inner.Exhausted && end < wantedEnd)
        {
            // Unity needs no lookahead: short reads can be returned immediately.
            if (!unity || end <= ideal) return false;
        }
        if (_contentFrame >= end) return false;
        _hopRate = _rate;
        int count = _inner.Exhausted
            ? Math.Min(_hop, (int)Math.Ceiling((end - _contentFrame) / _rate)) : _hop;
        if (unity) count = (int)Math.Min(count, end - ideal);
        if (count <= 0) return false;
        long chosen = unity || !_hasTail ? ideal : FindMatch(ideal, end);
        // A short unity read (EOF / producer shortfall) must still finish the return blend inside the frames it emits.
        int ramp = returnBlend ? count : _hop;
        for (int f = 0; f < count; f++)
        {
            float blend = (float)f / ramp;
            for (int c = 0; c < _channels; c++)
            {
                float next = Sample(chosen + f, c);
                _output[f * _channels + c] = !_hasTail ? next
                    : _tail[f * _channels + c] * (1 - blend) + next * blend;
            }
        }
        if (!unity)
            for (int f = 0; f < _hop; f++)
                for (int c = 0; c < _channels; c++)
                    _tail[f * _channels + c] = Sample(chosen + _hop + f, c);
        _hasTail = !unity;
        _ready = count;
        _offset = 0;
        return true;
    }

    private long FindMatch(long ideal, long end)
    {
        long lo = Math.Max(_inputStart, ideal - _search);
        long hi = Math.Min(ideal + _search, Math.Max(lo, end - _hop * 2));
        long best = Math.Clamp(ideal, lo, hi);
        double score = Correlation(best);
        // Coarse-to-fine bounds CPU work at high sample rates while retaining sample-accurate phase alignment.
        const int stride = 4;
        for (long p = lo; p <= hi; p += stride)
        {
            double s = Correlation(p);
            if (s > score + 1e-12) { score = s; best = p; }
        }
        long fineLo = Math.Max(lo, best - stride), fineHi = Math.Min(hi, best + stride);
        for (long p = fineLo; p <= fineHi; p++)
        {
            double s = Correlation(p);
            if (s > score + 1e-12) { score = s; best = p; }
        }
        return best;
    }

    private double Correlation(long start)
    {
        double dot = 0, aa = 0, bb = 0;
        // Correlate both channels; using a mono sum could cancel antiphase stereo entirely.
        for (int f = 0; f < _hop; f += 4)
            for (int c = 0; c < _channels; c++)
            {
                double a = _tail[f * _channels + c], b = Sample(start + f, c);
                dot += a * b; aa += a * a; bb += b * b;
            }
        return aa * bb <= 1e-20 ? 0 : dot / Math.Sqrt(aa * bb);
    }

    private float Sample(long frame, int channel)
    {
        long index = frame - _inputStart;
        return index >= 0 && index < _inputFrames ? _input[(int)index * _channels + channel] : 0;
    }

    private void Compact()
    {
        long keepFrom = Math.Max(InitialSourceFrame, (long)_contentFrame - _search - _hop);
        int drop = (int)Math.Clamp(keepFrom - _inputStart, 0, _inputFrames);
        if (drop == 0) return;
        _input.AsSpan(drop * _channels, (_inputFrames - drop) * _channels).CopyTo(_input);
        _inputStart += drop;
        _inputFrames -= drop;
    }

    private void FillUntil(long end)
    {
        while (_inputStart + _inputFrames < end && !_inner.Exhausted)
        {
            int room = _input.Length / _channels - _inputFrames;
            int take = (int)Math.Min(room, end - _inputStart - _inputFrames);
            if (take <= 0) break;
            int got = _inner.Read(_input.AsSpan(_inputFrames * _channels, take * _channels), _channels);
            if (got <= 0) break;
            _inputFrames += got;
        }
    }

    private void RecordMapping(long output, double source, double rate)
    {
        long sequence = _mappingCount;
        if (sequence > 0 && rate == _lastMappingRate &&
            Math.Abs(source - (_lastMappingSource + (output - _lastMappingOutput) * rate)) < 0.00001) return;
        _lastMappingOutput = output;
        _lastMappingSource = source;
        _lastMappingRate = rate;
        ref var slot = ref _mapping[sequence & (_mapping.Length - 1)];
        Interlocked.Exchange(ref slot.Version, sequence * 2 + 1);
        slot.Output = output; slot.Source = source; slot.Rate = rate;
        Volatile.Write(ref slot.Version, sequence * 2 + 2);
        Volatile.Write(ref _mappingCount, sequence + 1);
    }

    /// <summary>Map audible output frames to source frames, including rate changes still queued in the endpoint.
    /// The control thread reads render-published bounded history; no wall-clock multiplication or locks.</summary>
    public double SourceFrameAt(double outputFrame)
    {
        long count = Volatile.Read(ref _mappingCount);
        if (count == 0 || outputFrame <= 0) return InitialSourceFrame;
        double oldest = InitialSourceFrame;
        for (long sequence = count - 1, limit = Math.Max(0, count - _mapping.Length); sequence >= limit; sequence--)
        {
            ref var slot = ref _mapping[sequence & (_mapping.Length - 1)];
            long version = Volatile.Read(ref slot.Version);
            if (version != sequence * 2 + 2) continue;
            long output = slot.Output;
            double source = slot.Source, rate = slot.Rate;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) != version) continue;
            oldest = source;
            if (outputFrame >= output)
                return Math.Min(_contentFrame, source + (outputFrame - output) * rate);
        }
        return oldest;
    }

    /// <inheritdoc/>
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
