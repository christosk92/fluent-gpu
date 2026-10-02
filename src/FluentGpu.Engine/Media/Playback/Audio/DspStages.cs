using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The concrete IDspStage nodes (spec §7.3). Node ORDER is the contract:
//   [Source] → Gain → EQ → Channel → CrossfadeMixer → [MasterEQ?] → Limiter → [SRC?] → Sink
// All in-place, interleaved f32, alloc-free per block. EQ/Gain/Channel are used PER-VOICE (pre-mix) and/or on the master
// chain; Limiter is the TERMINAL master node. Resample is the elided-normally SRC edge (device-rate == mix-rate).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A smoothed linear-gain stage (spec §7.3). A gain change RAMPS via an <see cref="AudioParam"/> (linear, per-sample,
/// no zipper); a "set" is just a zero-length ramp. Applies to every channel uniformly. Used per-voice (ReplayGain +
/// track gain) and on the master (volume). Alloc-free.
/// </summary>
public sealed class GainStage : IDspStage
{
    private AudioParam _gain;
    private long _processedSamples, _identitySamples;
    /// <summary>Samples traversed by gain arithmetic (owner-thread work census).</summary>
    public long ProcessedSamples => System.Threading.Volatile.Read(ref _processedSamples);
    /// <summary>Samples whose settled unity gain needed no arithmetic.</summary>
    public long IdentitySamples => System.Threading.Volatile.Read(ref _identitySamples);

    /// <summary>Create a gain stage at <paramref name="initialLinear"/> (1 = unity).</summary>
    public GainStage(float initialLinear = 1f) => _gain = AudioParam.At(initialLinear);

    /// <inheritdoc/>
    public int LatencySamples => 0;
    /// <inheritdoc/>
    public bool Bypassed { get; set; }

    /// <summary>The current (RT-side) linear gain.</summary>
    public float CurrentGain => _gain.Current;

    /// <summary>Set the target linear gain, ramped over <paramref name="rampSamples"/> (0 = immediate set).</summary>
    public void SetTargetLinear(float linear, float rampSamples)
        => _gain.RampTo(linear, rampSamples, SmoothKind.Linear);

    /// <summary>Set the gain immediately (no ramp).</summary>
    public void SetLinear(float linear) => _gain.Set(linear);

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int n = frames * ctx.Channels;
        if (Bypassed)
        {
            if (!src.Overlaps(dst, out int offset) || offset != 0) src[..n].CopyTo(dst);
            _gain.Advance(frames);   // keep the param clock advancing even when bypassed
            return frames;
        }

        float start = _gain.Advance(frames);   // returns pre-block value; Current is now the post-block value
        float end = _gain.Current;
        int ch = ctx.Channels;

        if (start == 1f && end == 1f)
        {
            _identitySamples += n;
            if (!src.Overlaps(dst, out int offset) || offset != 0) src[..n].CopyTo(dst);
            return frames;
        }
        _processedSamples += n;

        if (start == end)
        {
            int i = 0;
            // Independent interleaved samples: preserve one multiply per sample, without reassociation/FMA.
            // Exact in-place and disjoint spans are safe; partial overlap retains the original forward scalar
            // semantics (a vector load must not consume samples before preceding scalar writes would affect them).
            if (n >= Vector128<float>.Count && Vector128.IsHardwareAccelerated
                && (!src.Overlaps(dst, out int offset) || offset == 0))
            {
                src = src[..n]; // validate once before the unchecked vector loads/stores
                dst = dst[..n];
                var gain = Vector128.Create(end);
                for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
                {
                    var samples = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(src), (nuint)i);
                    (samples * gain).StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)i);
                }
            }
            for (; i < n; i++) dst[i] = src[i] * end;
            return frames;
        }

        // Per-sample linear ramp across the block (branch-free, zipper-free).
        float perFrame = frames > 0 ? (end - start) / frames : 0f;
        for (int f = 0; f < frames; f++)
        {
            float g = start + perFrame * f;
            int b = f * ch;
            for (int c = 0; c < ch; c++) dst[b + c] = src[b + c] * g;
        }
        return frames;
    }
}

/// <summary>
/// A per-channel RBJ biquad cascade (spec §7.8). A GAIN-only band tweak recomputes that band's coefficients and
/// CROSS-RAMPS old→new over a short declick window (no zipper); a FREQ/Q change recomputes coefficients OFF-block and
/// cross-ramps the same way. During the ramp the block is filtered through BOTH the old and new cascades and their
/// outputs are crossfaded — the correct declick for a coefficient change. Steady-state runs one cascade, alloc-free.
/// <para><b>Fixed capacity (H-4).</b> The stage is sized ONCE for <see cref="MaxBands"/> bands — coefficient and state storage
/// never reallocate — so a live stage can change topology (band count included, even from/to identity) on the RT thread without
/// an allocation: the session designs coefficients off-thread (<see cref="Design"/>) and the RT adopts them with
/// <see cref="AdoptPending"/>, which restarts the cross-ramp from whatever cascade is currently audible.
/// <see cref="SetBands"/>/<see cref="SetBandGain"/> are the OWNER-thread mutators for a stage nobody else is rendering (graph
/// compile, tests); they are never called on a stage the RT thread is processing.</para>
/// </summary>
public sealed class EqStage : IDspStage
{
    /// <summary>The band capacity every stage is built with. A longer band set is truncated to it.</summary>
    public const int MaxBands = 16;

    private readonly int _channels;
    private readonly int _declickSamples;

    private readonly BiquadBand[] _bands = new BiquadBand[MaxBands];   // the TARGET band set (the pending cascade during a ramp, else the active one)
    private BiquadCoeffs[] _active = new BiquadCoeffs[MaxBands];       // per band
    private BiquadCoeffs[] _pending = new BiquadCoeffs[MaxBands];      // per band (target during a ramp)
    private readonly BiquadState[] _stateActive;                       // [band*channels + ch]
    private readonly BiquadState[] _statePending;
    private int _activeCount, _pendingCount;                           // bands in each cascade; 0 = identity
    private int _sampleRate;
    private int _rampRemaining;   // samples left in the cross-ramp (0 = steady)

    /// <summary>Create an EQ stage for <paramref name="channels"/> channels; <paramref name="declickSamples"/> is the
    /// coefficient cross-ramp length (default ~5 ms at 48k). Allocates the fixed <see cref="MaxBands"/> storage once.</summary>
    public EqStage(int channels, int declickSamples = 256)
    {
        _channels = Math.Max(1, channels);
        _declickSamples = Math.Max(1, declickSamples);
        _stateActive = new BiquadState[MaxBands * _channels];
        _statePending = new BiquadState[MaxBands * _channels];
    }

    /// <inheritdoc/>
    public int LatencySamples => 0;
    /// <inheritdoc/>
    public bool Bypassed { get; set; }
    /// <summary>The band count of the TARGET cascade (the one the stage is converging on; the active one when steady).</summary>
    public int BandCount => _pendingCount;
    /// <summary>The band count of the cascade currently audible (0 = identity). Differs from <see cref="BandCount"/> only mid-ramp.</summary>
    public int ActiveBandCount => _activeCount;
    /// <summary>True while a coefficient cross-ramp is in progress.</summary>
    public bool IsRamping => _rampRemaining > 0;

    /// <summary>The active coefficients for band <paramref name="i"/> (for golden tests).</summary>
    public BiquadCoeffs ActiveCoeffs(int i) => _active[i];

    /// <summary>CONTROL, allocating: design the coefficients for <paramref name="bands"/> (at most <see cref="MaxBands"/>) at
    /// <paramref name="sampleRate"/>. The result rides a mixer command to the RT thread's <see cref="AdoptPending"/>.</summary>
    public static BiquadCoeffs[] Design(ReadOnlySpan<BiquadBand> bands, int sampleRate)
    {
        var coeffs = new BiquadCoeffs[Math.Min(bands.Length, MaxBands)];
        for (int i = 0; i < coeffs.Length; i++) coeffs[i] = BiquadCoeffs.Design(bands[i], sampleRate);
        return coeffs;
    }

    /// <summary>RT, alloc-free: adopt a designed band set at a block boundary. A cross-ramp already in flight is COMMITTED first
    /// (its pending cascade becomes the active one — the new ramp must start from a single, well-defined cascade); the new
    /// cascade's state is then seeded from the active cascade for every band that keeps its position and type (so a gain-only
    /// edit stays phase-aligned) and zeroed elsewhere, and the 256-sample cross-ramp restarts. From identity (0 active bands) this
    /// ramps the EQ IN; to an empty set it ramps it OUT. <paramref name="coeffs"/> must hold one entry per band.</summary>
    public void AdoptPending(ReadOnlySpan<BiquadBand> bands, ReadOnlySpan<BiquadCoeffs> coeffs)
    {
        if (_rampRemaining > 0) CommitPending();
        int n = Math.Min(Math.Min(bands.Length, coeffs.Length), MaxBands);
        int ch = _channels;
        for (int b = 0; b < n; b++)
        {
            bool carry = b < _activeCount && _bands[b].Type == bands[b].Type;
            for (int c = 0; c < ch; c++)
                _statePending[b * ch + c] = carry ? _stateActive[b * ch + c] : default;
            _bands[b] = bands[b];
            _pending[b] = coeffs[b];
        }
        _pendingCount = n;
        _rampRemaining = _declickSamples;
    }

    /// <summary>OWNER thread, immediate: install a designed band set on a stage that is NOT being rendered yet (a voice chain built
    /// from the live EQ before it reaches the mixer). No ramp; state is reset.</summary>
    public void Seed(ReadOnlySpan<BiquadBand> bands, ReadOnlySpan<BiquadCoeffs> coeffs)
    {
        int n = Math.Min(Math.Min(bands.Length, coeffs.Length), MaxBands);
        for (int b = 0; b < n; b++) { _bands[b] = bands[b]; _active[b] = coeffs[b]; }
        _activeCount = _pendingCount = n;
        Array.Clear(_stateActive);
        Array.Clear(_statePending);
        _rampRemaining = 0;
    }

    /// <summary>OWNER thread: replace the full band set (a freq/Q/topology change; spec §7.8). A different band count installs
    /// immediately (fresh topology — no ramp source); the same count recomputes pending coefficients and cross-ramps
    /// active→pending. NOT for a stage the RT thread is processing — use <see cref="Design"/> + <see cref="AdoptPending"/>.</summary>
    public void SetBands(ReadOnlySpan<BiquadBand> bands, int sampleRate)
    {
        _sampleRate = sampleRate;
        int n = Math.Min(bands.Length, MaxBands);
        if (n != _activeCount)
        {
            for (int i = 0; i < n; i++) { _bands[i] = bands[i]; _active[i] = BiquadCoeffs.Design(bands[i], sampleRate); }
            _activeCount = _pendingCount = n;
            Array.Clear(_stateActive);
            Array.Clear(_statePending);
            _rampRemaining = 0;   // fresh topology — no ramp source
            return;
        }

        // Same count: recompute pending coefficients and cross-ramp active→pending.
        for (int i = 0; i < n; i++) { _bands[i] = bands[i]; _pending[i] = BiquadCoeffs.Design(bands[i], sampleRate); }
        _pendingCount = n;
        StartRamp();
    }

    /// <summary>OWNER thread: change a single band's gain (spec §7.8: a gain-only tweak) — recomputes that band's coefficients and
    /// cross-ramps (no zipper). Other bands keep their coefficients.</summary>
    public void SetBandGain(int index, float gainDb)
    {
        if ((uint)index >= (uint)_pendingCount) return;
        _bands[index] = _bands[index] with { GainDb = gainDb };
        for (int i = 0; i < _pendingCount; i++) _pending[i] = BiquadCoeffs.Design(_bands[i], _sampleRate);
        StartRamp();
    }

    private void StartRamp()
    {
        // Seed the pending state from the active state so the two cascades stay phase-aligned entering the ramp.
        Array.Copy(_stateActive, _statePending, _stateActive.Length);
        _rampRemaining = _declickSamples;
    }

    // The pending cascade becomes the sole active one (its coefficients and state are adopted).
    private void CommitPending()
    {
        (_active, _pending) = (_pending, _active);
        Array.Copy(_statePending, _stateActive, _stateActive.Length);
        _activeCount = _pendingCount;
        _rampRemaining = 0;
    }

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int n = frames * ctx.Channels;
        if (Bypassed || (_activeCount == 0 && _rampRemaining == 0))
        {
            if (!src.Overlaps(dst)) src[..n].CopyTo(dst);
            return frames;
        }

        int ch = Math.Min(ctx.Channels, _channels);

        for (int f = 0; f < frames; f++)
        {
            int b = f * ctx.Channels;
            bool ramping = _rampRemaining > 0;
            float t = ramping ? 1f - (_rampRemaining - 1) / (float)_declickSamples : 1f;   // 0→1 across the window

            for (int c = 0; c < ch; c++)
            {
                float x = src[b + c];
                float yActive = x;
                for (int band = 0; band < _activeCount; band++)
                    yActive = _stateActive[band * _channels + c].Process(yActive, in _active[band]);

                if (ramping)
                {
                    float yPending = x;
                    for (int band = 0; band < _pendingCount; band++)
                        yPending = _statePending[band * _channels + c].Process(yPending, in _pending[band]);
                    dst[b + c] = yActive * (1f - t) + yPending * t;
                }
                else
                {
                    dst[b + c] = yActive;
                }
            }

            // Pass through any channels beyond the EQ's channel count unchanged.
            for (int c = ch; c < ctx.Channels; c++) dst[b + c] = src[b + c];

            if (_rampRemaining > 0 && --_rampRemaining == 0) CommitPending();   // ramp complete: pending becomes the sole active cascade
        }
        return frames;
    }
}

/// <summary>
/// A channel stage — L/R balance + optional mono downmix (spec §7.3). Balance is a smoothed <see cref="AudioParam"/>
/// (constant-power pan). Stereo-only meaningfully; other layouts pass through. Alloc-free.
/// </summary>
public sealed class ChannelStage : IDspStage
{
    private AudioParam _balance;
    private bool _mono;
    private long _processedSamples, _identitySamples;
    /// <summary>Samples traversed by channel processing (owner-thread work census).</summary>
    public long ProcessedSamples => System.Threading.Volatile.Read(ref _processedSamples);
    /// <summary>Samples passed through with settled neutral balance and no downmix.</summary>
    public long IdentitySamples => System.Threading.Volatile.Read(ref _identitySamples);

    /// <summary>Create a channel stage.</summary>
    public ChannelStage(float balance = 0f, bool mono = false) { _balance = AudioParam.At(balance); _mono = mono; }

    /// <inheritdoc/>
    public int LatencySamples => 0;
    /// <inheritdoc/>
    public bool Bypassed { get; set; }

    /// <summary>Set the target balance (-1 = full left, +1 = full right), ramped.</summary>
    public void SetTargetBalance(float balance, float rampSamples)
        => _balance.RampTo(Math.Clamp(balance, -1f, 1f), rampSamples, SmoothKind.Linear);

    /// <summary>Enable/disable a mono downmix.</summary>
    public void SetMono(bool mono) => _mono = mono;

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int ch = ctx.Channels;
        int n = frames * ch;
        float start = _balance.Advance(frames);
        float bal = _balance.Current;

        if (start == 0f && bal == 0f && !_mono)
        {
            _identitySamples += n;
            if (!src.Overlaps(dst, out int offset) || offset != 0) src[..n].CopyTo(dst);
            return frames;
        }
        _processedSamples += n;

        if (ch != 2)
        {
            if (!src.Overlaps(dst)) src[..n].CopyTo(dst);
            if (_mono && ch > 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    int b = f * ch;
                    float sum = 0f;
                    for (int c = 0; c < ch; c++) sum += src[b + c];
                    float m = sum / ch;
                    for (int c = 0; c < ch; c++) dst[b + c] = m;
                }
            }
            return frames;
        }

        // Constant-power pan: left/right gains from balance.
        float lg = bal <= 0f ? 1f : MathF.Cos(bal * (MathF.PI / 2f));
        float rg = bal >= 0f ? 1f : MathF.Cos(-bal * (MathF.PI / 2f));
        for (int f = 0; f < frames; f++)
        {
            int b = f * 2;
            float l = src[b];
            float r = src[b + 1];
            if (_mono) { float m = (l + r) * 0.5f; l = m; r = m; }
            dst[b] = l * lg;
            dst[b + 1] = r * rg;
        }
        return frames;
    }
}

/// <summary>
/// The TERMINAL lookahead brickwall limiter (spec §7.3/§7.7; plan §4.7): always present, ceiling ~-1.5 dBTP, after any gain/EQ
/// boost — a boosted signal can never exceed the ceiling. The audio is DELAYED by <see cref="LatencySamples"/> frames
/// (<c>LimiterSpec.LookaheadMs</c>, default 2 ms) while a detector reads it that far ahead, so the gain reaches the level a
/// peak needs by a LINEAR ramp over the lookahead — before the peak is heard — instead of snapping (no distortion on a
/// transient). Channel-linked (one gain across the frame preserves the image); the release is a smoothed exponential that
/// snaps to unity.
/// <para><b>Detector.</b> One frame behind the input, so a frame's score is final once its successor has arrived: its linked
/// sample peak, and the half-sample point between it and its predecessor via the 4-point interpolator
/// <c>(−x[n−2] + 9·x[n−1] + 9·x[n] − x[n+1]) / 16</c> on the SIGNED per-channel samples (an estimate of the inter-sample peak;
/// exact at low frequency, −1.1 dB at fs/4, blind near Nyquist — the 1.5 dB of headroom under the ceiling is the margin). The
/// score of the last <see cref="LatencySamples"/> frames is kept in a monotonic deque (window max, O(1) amortised).</para>
/// <para><b>Guarantee.</b> Frame o is scored at call o+1 and leaves the delay line at call o+L; every one of those L gain updates
/// sees it in the window, and the attack drops the gain by (1−need)/(L−1) per call (one call of slack), so it reaches
/// <c>ceiling / score(o)</c> in time: <c>|out| ≤ ceiling</c> for every output sample (to float rounding).</para>
/// <para>Alloc-free; <see cref="Process"/> is safe in place (<c>src</c> and <c>dst</c> the same span).</para>
/// </summary>
public sealed class LimiterStage : IDspStage
{
    private readonly int _channels, _lookahead;     // _lookahead = the delay in frames (= LatencySamples when not bypassed)
    private readonly float[] _delay;                // _lookahead × channels, circular: read-then-write the slot ⇒ an exact _lookahead-frame delay
    private readonly float[] _hist;                 // 3 × channels: the last three INPUT frames (signed), newest first
    private readonly long[] _dqFrame;               // monotonic deque of (frame, score), scores strictly decreasing head → tail
    private readonly float[] _dqPeak;
    private readonly double _releaseCoeff, _attackStep;
    private float _ceiling;                         // linear
    private double _gain = 1d;                      // double: a float gain stalls ~7e-5 short of unity (the release step rounds away) and would never snap
    private int _write, _dqHead, _dqCount;
    private long _frameNo;                          // number of the frame arriving now
    private bool _dirty;                            // processed since the last Reset (so a bypass can drop the stale delay content once)

    /// <summary>Create a limiter at <paramref name="ceilingDbTp"/> dBTP with a <paramref name="releaseMs"/> release,
    /// <paramref name="lookaheadMs"/> of lookahead (at least one frame) at <paramref name="mixRate"/>, for <paramref name="channels"/> channels.</summary>
    public LimiterStage(float ceilingDbTp = -1.5f, float releaseMs = 50f, int mixRate = 48000, float lookaheadMs = 2f, int channels = 2)
    {
        _channels = Math.Max(1, channels);
        _ceiling = DbToLinear(ceilingDbTp);
        _lookahead = Math.Max(1, (int)Math.Clamp(Math.Round(lookaheadMs * (double)mixRate / 1000.0), 0.0, Math.Max(1, mixRate)));
        _delay = new float[_lookahead * _channels];
        _hist = new float[3 * _channels];
        _dqFrame = new long[_lookahead + 1];
        _dqPeak = new float[_lookahead + 1];
        _releaseCoeff = Math.Exp(-1.0 / Math.Max(1.0, releaseMs * 0.001 * mixRate));
        _attackStep = 1.0 / Math.Max(1, _lookahead - 1);
    }

    /// <summary>The lookahead delay in frames — what <c>CompiledAudioGraph.TotalLatencySamples</c> sums into the position compensation.
    /// 0 while <see cref="Bypassed"/> (a bypassed limiter is a plain pass-through).</summary>
    public int LatencySamples => Bypassed ? 0 : _lookahead;
    /// <inheritdoc/>
    public bool Bypassed { get; set; }

    /// <summary>The linear ceiling.</summary>
    public float Ceiling => _ceiling;

    /// <summary>The gain applied to the frame leaving the delay line now (1 = no gain reduction).</summary>
    public float CurrentGain => (float)_gain;

    /// <summary>Update the ceiling (dBTP).</summary>
    public void SetCeilingDbTp(float dbTp) => _ceiling = DbToLinear(dbTp);

    /// <summary>Drop the delay line, the detector history and the gain state (silence in, unity gain). RT thread, or a stage nobody is rendering.</summary>
    public void Reset()
    {
        Array.Clear(_delay);
        Array.Clear(_hist);
        Array.Clear(_dqFrame);
        Array.Clear(_dqPeak);
        _write = _dqHead = _dqCount = 0;
        _frameNo = 0;
        _gain = 1d;
        _dirty = false;
    }

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int ch = _channels;
        System.Diagnostics.Debug.Assert(ctx.Channels == ch, "LimiterStage was built for a different channel count than the block");
        if (Bypassed)
        {
            if (_dirty) Reset();   // a bypassed limiter holds no audio: un-bypassing must not replay the stale delay content
            if (!src.Overlaps(dst)) src[..(frames * ch)].CopyTo(dst);
            return frames;
        }
        _dirty = true;

        int look = _lookahead, cap = _dqFrame.Length, write = _write, head = _dqHead, count = _dqCount;
        long frameNo = _frameNo;
        double gain = _gain, release = _releaseCoeff, attack = _attackStep;
        float ceiling = _ceiling;

        for (int f = 0; f < frames; f++)
        {
            int b = f * ch;

            // 1. Score the frame BEFORE this one (n = k−1), now that its successor (x) is here: its linked sample peak and the
            //    inter-sample estimate between n−1 and n, per channel and signed — an alternating-sign pair must not read as a peak.
            float peak = 0f;
            for (int c = 0; c < ch; c++)
            {
                float x = src[b + c];
                float h1 = _hist[c], h2 = _hist[ch + c], h3 = _hist[2 * ch + c];      // x[n], x[n−1], x[n−2]
                float mid = (9f * (h1 + h2) - h3 - x) * (1f / 16f);
                peak = MathF.Max(peak, MathF.Max(MathF.Abs(h1), MathF.Abs(mid)));
                _hist[2 * ch + c] = h2; _hist[ch + c] = h1; _hist[c] = x;
            }

            // 2. The window is the last `look` scored frames [k−look, k−1]: expire the old head, drop dominated tail entries, push.
            while (count > 0 && _dqFrame[head] < frameNo - look) { if (++head == cap) head = 0; count--; }
            while (count > 0)
            {
                int tail = head + count - 1; if (tail >= cap) tail -= cap;
                if (_dqPeak[tail] > peak) break;
                count--;
            }
            int ins = head + count; if (ins >= cap) ins -= cap;
            _dqFrame[ins] = frameNo - 1; _dqPeak[ins] = peak; count++;
            float windowMax = _dqPeak[head];

            // 3. The gain the whole window needs: a linear attack (one call of slack on the lookahead), a smoothed release that snaps.
            double need = windowMax > ceiling ? ceiling / windowMax : 1d;
            if (need < gain) gain = Math.Max(need, gain - attack * (1d - need));
            else { gain = need + (gain - need) * release; if (need >= 1d && gain > 1d - 1e-6) gain = 1d; }

            // 4. Emit the frame that entered `look` frames ago and store this one. src is read BEFORE dst is written: in place is legal.
            float g = (float)gain;
            int slot = write * ch;
            for (int c = 0; c < ch; c++)
            {
                float x = src[b + c];
                dst[b + c] = _delay[slot + c] * g;
                _delay[slot + c] = x;
            }
            if (++write == look) write = 0;
            frameNo++;
        }

        _write = write; _dqHead = head; _dqCount = count; _frameNo = frameNo; _gain = gain;
        return frames;
    }

    /// <summary>dB → linear amplitude.</summary>
    public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}

/// <summary>
/// A windowed-sinc polyphase sample-rate converter (spec §7.3 SRC edge). Present ONLY when device-rate != mix-rate (normally
/// elided, since every source resamples INTO the fixed mix format at the decode edge). Frame-count-CHANGING, so it is a
/// terminal edge stage (not an in-place master-chain node): <see cref="Convert"/> reads input frames and writes a
/// (different) number of output frames. A thin wrapper over <see cref="PolyphaseResampler"/>, the same converter the decode
/// edge uses; the resampler keeps its own history, so the caller retains <c>src[Consumed..]</c> and, at end of stream, calls
/// <see cref="Flush"/> once for the trailing half kernel.
/// </summary>
public sealed class ResampleStage
{
    private readonly PolyphaseResampler _resampler;

    /// <summary>Create an SRC from <paramref name="fromRate"/> to <paramref name="toRate"/> for <paramref name="channels"/> channels.</summary>
    public ResampleStage(int fromRate, int toRate, int channels) => _resampler = new PolyphaseResampler(fromRate, toRate, channels);

    /// <summary>True when the rates differ (the stage is not a no-op).</summary>
    public bool IsActive => _resampler.IsActive;

    /// <summary>The added latency in samples: 0 — the resampler absorbs its group delay (discards the leading half kernel, flushes the trailing one).</summary>
    public int LatencySamples => _resampler.LatencySamples;

    /// <summary>Convert <paramref name="inFrames"/> input frames → output frames written to <paramref name="dst"/>.
    /// Returns produced-output and consumed-input counts — the caller retains <c>src[Consumed..]</c> on a short dst.</summary>
    public ResampleResult Convert(ReadOnlySpan<float> src, int inFrames, Span<float> dst) =>
        _resampler.Process(src, inFrames, dst);

    /// <summary>End of stream: emit the trailing half kernel into <paramref name="dst"/>; returns the frames produced.</summary>
    public int Flush(Span<float> dst) => _resampler.Flush(dst);

    /// <summary>Forget the history (a seek / a new stream).</summary>
    public void Reset() => _resampler.Reset();
}
