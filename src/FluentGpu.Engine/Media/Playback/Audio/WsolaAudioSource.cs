using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// Pitch-preserving, allocation-free PCM time scaling. A 40 ms window advances by a 20 ms synthesis hop;
/// a normalized waveform match chooses its analysis position within 10 ms of the content clock.
/// Construct off the render thread; Read, Reset and Rate are render-thread operations. The inner ring keeps
/// decoding off that thread. At unity, PCM passes through unchanged. Source and output clocks stay separate.
/// <para><b>A voice's own rate (#48 E4).</b> The voice plays at the session's <see cref="Rate"/> × the factor of its
/// <see cref="VoiceRatePlan"/>, evaluated on the MIXER timeline (the session tells the voice which mixer frame its next output frame
/// is, every block). A hop never straddles a plan step: it is cut short so the step lands on its exact mixer frame. A factor within
/// <see cref="ResampleBelow"/> of 1 (with the session at 1) is RESAMPLED — a 64-tap windowed sinc read at the fractional content
/// clock, the pitch moving by the same tiny ratio — instead of stretched, so a near-unity beatmatch carries no WSOLA artefacts.
/// Every switch between the three modes is crossfaded over one hop: entering the stretch from the resampler matches against the
/// resampler's own continuation; leaving the resampler blends its continuation into the next mode; leaving the stretch continues
/// from where the stretch's last window continues in the source (the content clock moves by that window's alignment offset, so it
/// names what is heard); unity returns to the byte-exact pass-through once the lookahead has drained. A planned voice's stretch
/// also breaks near-ties of its waveform match toward the content clock (<see cref="NearTieScore"/>), which keeps a beatmatched
/// voice's audio, and that offset, close to its plan. Without a plan nothing of this runs: the voice behaves exactly as before E4.</para>
/// </summary>
public sealed class WsolaAudioSource : IAudioSource, IDisposable
{
    /// <summary>#48 E4: a voice factor closer to 1 than this (|factor − 1| &lt; 0.5 %) is resampled, not stretched (the session rate at 1).</summary>
    public const double ResampleBelow = 0.005;
    /// <summary>#48 E4: a planned voice's stretch takes, among window positions whose normalized match is within this of the best, the
    /// one closest to the content clock (a periodic signal matches equally well a period apart all through the 10 ms search).</summary>
    public const double NearTieScore = 0.01;

    private const byte ModeUnity = 0, ModeResample = 1, ModeStretch = 2;
    private const int Taps = PolyphaseResampler.Taps;
    private const int Phases = PolyphaseResampler.InterpPhases;
    private const double KernelDelay = (Taps - 1) / 2.0;
    private static float[]? s_kernel;               // (Phases + 1) rows × Taps; built once, off the render thread
    private static readonly VoiceRatePlan s_unity = VoiceRatePlan.Constant(1.0);

    private readonly IAudioSource _inner;
    private readonly int _channels, _hop, _search;
    private readonly float[] _input, _tail, _output;
    private readonly double[] _scores;              // E4: the coarse match scores of one search (a planned voice's near-tie break)
    private long _inputStart, _outputFrames;
    private int _inputFrames, _ready, _offset;
    private double _contentFrame, _hopRate;
    private bool _hasTail;
    private long _tailSource;                       // E4: the source frame of _tail[0] after a stretched hop (its window's continuation)
    private double _rate = 1;
    private readonly Mapping[] _mapping = new Mapping[4096];
    private long _mappingCount, _lastMappingOutput;
    private double _lastMappingSource, _lastMappingRate;
    private struct Mapping { public long Version, Output; public double Source, Rate; }

    // ── #48 E4: the voice's own rate (render-thread state; the control thread reads the published values) ──
    private VoiceRatePlan? _plan;
    private double _factor = 1;                     // the factor of the newest synthesized frame
    private byte _hopMode;                          // the mode of the newest hop (pass-through counts as unity)
    private long _nextChange = long.MaxValue;       // mixer frame of the plan's next change after the newest hop; 0 = not known yet
    private long _factorChanges, _lastChangeMixer = long.MinValue;
    private long _mixerOffset;                      // mixer frame − output frame, as of the last block start (BeginBlock)
    private bool _synced;                           // _mixerOffset is known (false after a reset until the next block)
    private long _anchorVersion, _anchorOffset;     // seqlock for the control thread: _mixerOffset, written only when it changes
    private int _anchorValid;

    /// <summary>Allocate all work buffers for a voice in the device mix format.</summary>
    public WsolaAudioSource(IAudioSource inner, int sampleRate, int channels) : this(inner, sampleRate, channels, null) { }

    /// <summary>#48 E4: a voice that carries its own rate <paramref name="plan"/> from its first frame (B starts stretched).</summary>
    public WsolaAudioSource(IAudioSource inner, int sampleRate, int channels, VoiceRatePlan? plan)
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
        _scores = new double[2 * _search / 4 + 2];
        if (plan is not null) { EnsureResampleKernel(); _plan = plan.IsConstant && plan.Initial == 1.0 ? null : plan; }
        Reset(inner.PositionFrames);
    }

    /// <summary>Validate the common podcast playback range without quantizing remote rates.</summary>
    public static double ClampRate(double rate) => double.IsFinite(rate) ? Math.Clamp(rate, 0.5, 3) : 1;
    /// <summary>The SESSION's desired rate (the podcast speed). The voice's own factor (<see cref="VoiceRatePlan"/>) multiplies it.
    /// A change takes effect at the next synthesis hop.</summary>
    public double Rate { get => _rate; set => _rate = ClampRate(value); }
    /// <summary>#48 E4: session rate × the voice's own factor of the newest synthesized frame.</summary>
    public double EffectiveRate => ClampRate(_rate * Volatile.Read(ref _factor));
    /// <summary>#48 E4: true when the voice has its own rate plan (it then reports its rate from its output → source map).</summary>
    public bool HasRatePlan => Volatile.Read(ref _plan) is not null;
    /// <summary>#48 E4: the voice's rate plan (null = factor 1).</summary>
    public VoiceRatePlan? RatePlan => Volatile.Read(ref _plan);
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

    /// <summary>#48 E4: build the near-unity resampling kernel (a Kaiser-windowed sinc, <see cref="PolyphaseResampler.Taps"/> taps,
    /// <see cref="PolyphaseResampler.InterpPhases"/> interpolated phases, cut-off at Nyquist so an integral position reproduces its
    /// sample exactly). Once per process, off the render thread: every path that arms a plan calls it first.</summary>
    public static void EnsureResampleKernel()
    {
        if (Volatile.Read(ref s_kernel) is not null) return;
        float[] built = PolyphaseResampler.BuildTaps(Phases + 1, Phases, 0.5);
        Interlocked.CompareExchange(ref s_kernel, built, null);
    }

    /// <summary>Discard lookahead/overlap after an acknowledged seek or discontinuity, while output is held.</summary>
    public void Reset(long sourceFrame)
    {
        InitialSourceFrame = _inputStart = Math.Max(0, sourceFrame);
        _contentFrame = _inputStart;
        _inputFrames = _ready = _offset = 0;
        _outputFrames = 0;
        Volatile.Write(ref _mappingCount, 0);
        _hasTail = false;
        // E4: the new position starts afresh (a seek carries its own blend); the plan's mixer timeline is unchanged and the next
        // block re-anchors the output frames to it.
        _hopMode = ModeUnity;
        _synced = false;
        _nextChange = _plan is null ? long.MaxValue : 0;
        PublishAnchor(0, valid: false);
    }

    /// <summary>RENDER thread, once per block before the voice is read (#48 E4): the session's rate, and the mixer frame the voice's
    /// next output frame is heard at, so its plan's steps land on mixer frames. The mixer → output offset is published for the
    /// control thread's position maps only when it changes (the voice's start, a hold, a seek), so a steady block costs a compare.
    /// <see cref="PcmAudioSession"/> calls it for every voice; a caller driving the stage itself calls it the same way (setting
    /// <see cref="Rate"/> alone keeps the pre-E4 behaviour of a voice without a plan).</summary>
    public void BeginBlock(double sessionRate, long mixerFrame)
    {
        _rate = ClampRate(sessionRate);
        long offset = mixerFrame - _outputFrames;
        if (_synced && offset == _mixerOffset) return;
        _mixerOffset = offset;
        _synced = true;
        PublishAnchor(offset, valid: true);
    }

    /// <summary>RENDER thread (#48 E4): adopt a new plan (null = back to factor 1, through the same switch blends). A step already due
    /// applies at the next hop.</summary>
    internal void ArmRatePlan(VoiceRatePlan? plan)
    {
        VoiceRatePlan? next = plan is null || plan.IsConstant && plan.Initial == 1.0
            ? (_plan is null && _factor == 1 && _hopMode != ModeResample ? null : s_unity)
            : plan;
        Volatile.Write(ref _plan, next);
        _nextChange = next is null ? long.MaxValue : 0;
    }

    /// <summary>The most OUTPUT frames a ring holding <paramref name="ringTargetFrames"/> input frames can ever let this voice
    /// preflight at the current rate. A stretched voice eats <c>rate</c> input frames per output frame and needs two hops plus
    /// the search margin in hand, so a resume cushion beyond this could never be met by a full ring and the mixer's
    /// <see cref="CrossfadeMixer.PcmReady"/> would wait forever (silence) at, say, 3× once the cushion has grown.</summary>
    internal int ReachableFrames(int ringTargetFrames)
        => UnityAhead ? ringTargetFrames : Math.Max(_hop, (int)((ringTargetFrames - _hop * 2 - _search) / ReadAheadRate));

    /// <summary>True when the next frames pass through at unity with no lookahead: the session at 1 and, with a plan, a factor of 1
    /// that the plan can no longer change (and no resampler hop to blend out of). Without a plan this is <c>Rate == 1</c>.</summary>
    internal bool UnityAhead
        => _plan is null ? _rate == 1 : _rate == 1 && _factor == 1 && _nextChange == long.MaxValue && _hopMode != ModeResample;

    /// <summary>The rate the read-ahead is sized by: the session rate, × the largest factor the plan may still play.</summary>
    private double ReadAheadRate => _plan is not { } plan ? _rate : ClampRate(_rate * Math.Max(_factor, plan.MaxFactorValue));

    /// <summary>Conservative non-consuming output preflight. A producer shortfall must stall the mixer, not add silence.</summary>
    public int ReadableFrames(int requested)
    {
        if (Ring is not { } ring || ring.ProducerDone) return requested;
        if (ring.HasPendingFlush) return 0;
        long end = _inputStart + _inputFrames + ring.BufferedFrames;
        double afterReady = _contentFrame + _ready * _hopRate;
        if (UnityAhead)
            return Math.Min(requested, _ready + (int)Math.Max(0, end - Math.Ceiling(afterReady)));
        // Every new hop needs its full window plus the right-hand search margin.
        double room = end - (Math.Floor(afterReady) + _hop * 2 + _search);
        int hops = room < 0 ? 0 : 1 + (int)Math.Floor(room / (_hop * ReadAheadRate));
        return Math.Min(requested, _ready + hops * _hop);
    }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (channels != _channels) throw new ArgumentException("Channel count differs from the voice format.", nameof(channels));
        int wanted = dst.Length / channels, written = 0;
        if (TryEnterPassThrough(out int cap))
        {
            int got = cap >= wanted ? _inner.Read(dst, channels) : _inner.Read(dst[..(cap * channels)], channels);
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
    /// fractional rate left behind is discarded: &lt; 1 frame). E4: with a plan, the read is capped at the plan's next step
    /// (<paramref name="cap"/> frames), and a resampled hop is first blended out by <see cref="ProduceHop"/>.</summary>
    private bool TryEnterPassThrough(out int cap)
    {
        cap = int.MaxValue;
        if (_ready != 0 || _hasTail) return false;
        double factor = 1;
        long mixer = long.MinValue;
        if (_plan is not null)
        {
            if (_hopMode == ModeResample) return false;
            factor = PlanFactorAt(_outputFrames, out cap, out mixer);
            if (ClampRate(_rate * factor) != 1) return false;
        }
        else if (_rate != 1) return false;
        if (_inputFrames != 0)
        {
            long next = _inputStart + _inputFrames;
            if ((long)Math.Floor(_contentFrame) != next) return false;
            _inputStart = next;
            _inputFrames = 0;
            _contentFrame = next;
        }
        if (_plan is not null) { PublishFactor(factor, mixer); _hopMode = ModeUnity; }
        return true;
    }

    private bool ProduceHop()
    {
        double factor = 1, rate = _rate;
        int cap = int.MaxValue;
        long hopMixer = long.MinValue;
        byte mode;
        if (_plan is null) mode = rate == 1 ? ModeUnity : ModeStretch;
        else
        {
            factor = PlanFactorAt(_outputFrames, out cap, out hopMixer);
            rate = ClampRate(_rate * factor);
            mode = rate == 1 ? ModeUnity : UseResampler(factor) ? ModeResample : ModeStretch;
            // E4: a planned voice leaving the stretch continues from where the stretch's last window continues in the source (the
            // tail), not from the content clock the window was aligned around (up to the 10 ms search away): the blend then joins two
            // copies of the same audio instead of two copies up to 10 ms apart (a comb dip on tonal audio). The content clock moves by
            // that alignment offset, so from here on it names exactly what is heard (resampled or passed through, both exact).
            if (_hasTail && _hopMode == ModeStretch && mode != ModeStretch) _contentFrame = _tailSource;
        }
        Compact();
        long ideal = (long)Math.Floor(_contentFrame);
        bool unity = mode == ModeUnity;
        bool leavingResample = _hopMode == ModeResample && mode != ModeResample;
        // Q-2: the first unity hop after a stretched one still holds the overlap tail of the hop before it. Dropping it left
        // a step at the speed toggle, so the tail is blended out instead. No waveform search: unity carries no lookahead margin.
        // E4: leaving the resampler blends its own continuation out the same way.
        bool returnBlend = unity && (_hasTail || leavingResample);
        long wantedEnd = ideal + (unity ? _hop + (leavingResample ? Taps : 0) : _hop * 2 + _search);
        FillUntil(wantedEnd);
        long end = _inputStart + _inputFrames;
        if (!_inner.Exhausted && end < wantedEnd)
        {
            // Unity needs no lookahead: short reads can be returned immediately.
            if (!unity || end <= ideal) return false;
        }
        if (_contentFrame >= end) return false;
        if (leavingResample)
        {
            // What the resampler would have played next: the stretch matches against it, unity blends it out.
            FillResampleTail(_contentFrame, _hopRate);
            _hasTail = true;
        }
        _hopRate = rate;
        int count = _inner.Exhausted
            ? Math.Min(_hop, (int)Math.Ceiling((end - _contentFrame) / rate)) : _hop;
        bool capped = cap < count;   // E4: a plan step falls inside this hop: the hop ends on it
        if (capped) count = cap;
        if (unity) count = (int)Math.Min(count, end - ideal);
        if (count <= 0) return false;
        if (mode == ModeResample) RenderResampleHop(count, rate);
        else
        {
            long chosen = unity || !_hasTail ? ideal : FindMatch(ideal, end);
            // A short unity read (EOF / producer shortfall) must still finish the return blend inside the frames it emits; so must a
            // hop cut short at a plan step, whose next window then starts right after the frames it emitted.
            int ramp = returnBlend || capped ? count : _hop;
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
            {
                int from = capped ? count : _hop;
                for (int f = 0; f < _hop; f++)
                    for (int c = 0; c < _channels; c++)
                        _tail[f * _channels + c] = Sample(chosen + from + f, c);
                _tailSource = chosen + from;
            }
            _hasTail = !unity;
        }
        if (_plan is not null) PublishFactor(factor, hopMixer);
        _hopMode = mode;
        _ready = count;
        _offset = 0;
        return true;
    }

    /// <summary>E4: the plan's factor for output frame <paramref name="outputFrame"/>, the frames until its next step
    /// (<paramref name="cap"/>) and that frame's mixer frame. Before the first block's anchor the plan's initial factor applies.</summary>
    private double PlanFactorAt(long outputFrame, out int cap, out long mixer)
    {
        var plan = _plan!;
        cap = int.MaxValue;
        if (!_synced)
        {
            mixer = long.MinValue;
            _nextChange = 0;
            return plan.Initial;
        }
        mixer = outputFrame + _mixerOffset;
        long next = plan.NextChangeAfter(mixer);
        _nextChange = next;
        if (next != long.MaxValue) cap = (int)Math.Min(int.MaxValue, next - mixer);
        return plan.FactorAt(mixer);
    }

    private bool UseResampler(double factor)
        => _rate == 1 && factor != 1 && Math.Abs(factor - 1) < ResampleBelow && Volatile.Read(ref s_kernel) is not null;

    private void PublishFactor(double factor, long mixer)
    {
        if (factor == _factor) return;
        Volatile.Write(ref _factor, factor);
        Volatile.Write(ref _lastChangeMixer, mixer);
        Volatile.Write(ref _factorChanges, _factorChanges + 1);
    }

    /// <summary>E4: one resampled hop — the windowed sinc read at the content clock, advancing <paramref name="rate"/> source frames
    /// per output frame. A tail left by a stretched (or blended) hop is crossfaded out over the hop.</summary>
    private void RenderResampleHop(int count, double rate)
    {
        int ch = _channels;
        double start = _contentFrame;
        bool blend = _hasTail;
        for (int f = 0; f < count; f++)
        {
            var frame = _output.AsSpan(f * ch, ch);
            InterpolateFrame(start + f * rate, frame);
            if (!blend) continue;
            float b = (float)f / count;
            for (int c = 0; c < ch; c++) frame[c] = _tail[f * ch + c] * (1 - b) + frame[c] * b;
        }
        _hasTail = false;
    }

    /// <summary>E4: the next hop of what the resampler would play from <paramref name="from"/> at <paramref name="rate"/>, into the tail.</summary>
    private void FillResampleTail(double from, double rate)
    {
        for (int f = 0; f < _hop; f++) InterpolateFrame(from + f * rate, _tail.AsSpan(f * _channels, _channels));
    }

    /// <summary>E4: the input at fractional source frame <paramref name="pos"/>: <c>Σ h_p[k]·x[i − k]</c> with
    /// <c>i + p/Phases = pos + (Taps − 1)/2</c> (the house resampler's causal kernel convention), lerped between two phase rows.
    /// Frames outside the buffered input read as silence.</summary>
    private void InterpolateFrame(double pos, Span<float> dst)
    {
        float[] k = s_kernel!;
        double u = pos + KernelDelay;
        long i = (long)Math.Floor(u);
        double fp = (u - i) * Phases;
        int row = (int)fp;
        if (row > Phases - 1) row = Phases - 1;
        float lerp = (float)(fp - row);
        int r0 = row * Taps, r1 = r0 + Taps;
        long newest = i - _inputStart;
        int ch = _channels;
        if (newest >= Taps - 1 && newest < _inputFrames)
        {
            for (int c = 0; c < ch; c++)
            {
                float a0 = 0f, a1 = 0f;
                int p = (int)newest * ch + c;
                for (int t = 0; t < Taps; t++, p -= ch)
                {
                    float s = _input[p];
                    a0 += k[r0 + t] * s;
                    a1 += k[r1 + t] * s;
                }
                dst[c] = a0 + (a1 - a0) * lerp;
            }
            return;
        }
        for (int c = 0; c < ch; c++)
        {
            float a0 = 0f, a1 = 0f;
            for (int t = 0; t < Taps; t++)
            {
                float s = Sample(i - t, c);
                a0 += k[r0 + t] * s;
                a1 += k[r1 + t] * s;
            }
            dst[c] = a0 + (a1 - a0) * lerp;
        }
    }

    private long FindMatch(long ideal, long end)
    {
        long lo = Math.Max(_inputStart, ideal - _search);
        long hi = Math.Min(ideal + _search, Math.Max(lo, end - _hop * 2));
        long best = Math.Clamp(ideal, lo, hi);
        double score = Correlation(best);
        // Coarse-to-fine bounds CPU work at high sample rates while retaining sample-accurate phase alignment.
        const int stride = 4;
        bool near = _plan is not null;
        long clamped = best;
        double clampedScore = score;
        int n = 0;
        for (long p = lo; p <= hi; p += stride)
        {
            double s = Correlation(p);
            if (near && n < _scores.Length) _scores[n++] = s;
            if (s > score + 1e-12) { score = s; best = p; }
        }
        if (near)
        {
            // E4: the near-tie closest to the content clock (the clock itself first), then the fine search around it.
            double floor = score - NearTieScore;
            long dist = clampedScore >= floor ? Math.Abs(clamped - ideal) : Math.Abs(best - ideal);
            if (clampedScore >= floor) { best = clamped; score = clampedScore; }
            for (int i = 0; i < n; i++)
            {
                long p = lo + (long)i * stride;
                if (_scores[i] >= floor && Math.Abs(p - ideal) < dist) { dist = Math.Abs(p - ideal); best = p; score = _scores[i]; }
            }
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

    /// <summary>#48 E4: the rate (source frames per output frame) the voice played output frame <paramref name="outputFrame"/> at, from
    /// the same render-published map as <see cref="SourceFrameAt"/>; the newest rate before anything was emitted.</summary>
    public double RateAt(double outputFrame)
    {
        long count = Volatile.Read(ref _mappingCount);
        if (count == 0) return EffectiveRate;
        double oldest = EffectiveRate;
        for (long sequence = count - 1, limit = Math.Max(0, count - _mapping.Length); sequence >= limit; sequence--)
        {
            ref var slot = ref _mapping[sequence & (_mapping.Length - 1)];
            long version = Volatile.Read(ref slot.Version);
            if (version != sequence * 2 + 2) continue;
            long output = slot.Output;
            double rate = slot.Rate;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) != version) continue;
            oldest = rate;
            if (outputFrame >= output) return rate;
        }
        return oldest;
    }

    /// <summary>#48 E4: the output frame at which the voice played source frame <paramref name="sourceFrame"/> (the inverse of
    /// <see cref="SourceFrameAt"/> over the render-published map); 0 before it.</summary>
    public double OutputFrameOf(double sourceFrame)
    {
        long count = Volatile.Read(ref _mappingCount);
        if (count == 0) return 0;
        for (long sequence = count - 1, limit = Math.Max(0, count - _mapping.Length); sequence >= limit; sequence--)
        {
            ref var slot = ref _mapping[sequence & (_mapping.Length - 1)];
            long version = Volatile.Read(ref slot.Version);
            if (version != sequence * 2 + 2) continue;
            long output = slot.Output;
            double source = slot.Source, rate = slot.Rate;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) != version) continue;
            if (sourceFrame >= source) return output + (sourceFrame - source) / Math.Max(1e-9, rate);
        }
        return 0;
    }

    private void PublishAnchor(long offset, bool valid)
    {
        long version = _anchorVersion + 1;
        Interlocked.Exchange(ref _anchorVersion, version);   // odd: being written (a full fence keeps the payload after it, ARM64 too)
        _anchorOffset = offset;
        _anchorValid = valid ? 1 : 0;
        Volatile.Write(ref _anchorVersion, version + 1);
    }

    private bool TryReadAnchor(out long offset)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            long version = Volatile.Read(ref _anchorVersion);
            if ((version & 1) != 0) continue;
            offset = _anchorOffset;
            int valid = _anchorValid;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref _anchorVersion) != version) continue;
            return valid != 0;
        }
        offset = 0;
        return false;
    }

    private double PlanSourceBetween(long from, long to, double sessionRate)
        => Volatile.Read(ref _plan) is { } plan ? plan.SourceFramesBetween(from, to, sessionRate) : Math.Max(0, to - from) * ClampRate(sessionRate);

    private double PlanMixerFor(double sourceFrames, long from, double sessionRate)
        => Volatile.Read(ref _plan) is { } plan ? plan.MixerFramesFor(sourceFrames, from, sessionRate) : Math.Max(0, sourceFrames) / ClampRate(sessionRate);

    /// <summary>#48 E4 (control thread): the source frame this voice plays at mixer frame <paramref name="mixerFrame"/> — the output →
    /// source map for frames it has rendered, its plan beyond them, its start (<paramref name="voiceStartFrame"/>,
    /// <see cref="InitialSourceFrame"/>) before its first block.</summary>
    internal double SourceFrameAtMixer(long mixerFrame, long voiceStartFrame, double sessionRate)
    {
        if (!TryReadAnchor(out long offset))
            return InitialSourceFrame + PlanSourceBetween(voiceStartFrame, mixerFrame, sessionRate);
        long head = Volatile.Read(ref _outputFrames);
        double output = mixerFrame - offset;
        if (output <= head) return SourceFrameAt(output);
        return SourceFrameAt(head) + PlanSourceBetween(head + offset, mixerFrame, sessionRate);
    }

    /// <summary>#48 E4 (control thread): the mixer frame at which this voice reaches <paramref name="sourceFrame"/> (the inverse of
    /// <see cref="SourceFrameAtMixer"/>).</summary>
    internal double MixerFrameAtSource(double sourceFrame, long voiceStartFrame, double sessionRate)
    {
        if (!TryReadAnchor(out long offset))
            return voiceStartFrame + PlanMixerFor(sourceFrame - InitialSourceFrame, voiceStartFrame, sessionRate);
        long head = Volatile.Read(ref _outputFrames);
        double headSource = SourceFrameAt(head);
        if (sourceFrame <= headSource) return OutputFrameOf(sourceFrame) + offset;
        return head + offset + PlanMixerFor(sourceFrame - headSource, head + offset, sessionRate);
    }

    /// <summary>#48 E4 (control thread): output frames this voice needs to advance <paramref name="sourceFrames"/> source frames from
    /// its newest rendered frame, along its plan.</summary>
    internal double OutputFramesFor(double sourceFrames, double sessionRate)
    {
        if (!TryReadAnchor(out long offset)) return Math.Max(0, sourceFrames) / ClampRate(sessionRate * Volatile.Read(ref _factor));
        return PlanMixerFor(sourceFrames, Volatile.Read(ref _outputFrames) + offset, sessionRate);
    }

    /// <summary>#48 E4 (any thread): what the renderer last applied of the voice's plan.</summary>
    public VoiceRateReading ReadRate()
    {
        double factor = Volatile.Read(ref _factor);
        return new VoiceRateReading(factor, ClampRate(_rate * factor), Volatile.Read(ref _factorChanges),
            Volatile.Read(ref _lastChangeMixer), _hopMode == ModeResample);
    }

    /// <inheritdoc/>
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
