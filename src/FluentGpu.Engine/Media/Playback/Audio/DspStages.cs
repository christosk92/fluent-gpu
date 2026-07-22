using System;

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
            if (!src.Overlaps(dst)) src[..n].CopyTo(dst);
            _gain.Advance(frames);   // keep the param clock advancing even when bypassed
            return frames;
        }

        float start = _gain.Advance(frames);   // returns pre-block value; Current is now the post-block value
        float end = _gain.Current;
        int ch = ctx.Channels;

        if (start == end)
        {
            for (int i = 0; i < n; i++) dst[i] = src[i] * end;
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
/// </summary>
public sealed class EqStage : IDspStage
{
    private readonly int _channels;
    private readonly int _declickSamples;

    private BiquadBand[] _bands = Array.Empty<BiquadBand>();
    private BiquadCoeffs[] _active = Array.Empty<BiquadCoeffs>();   // per band
    private BiquadCoeffs[] _pending = Array.Empty<BiquadCoeffs>();  // per band (target during a ramp)
    private BiquadState[] _stateActive = Array.Empty<BiquadState>();   // [band*channels + ch]
    private BiquadState[] _statePending = Array.Empty<BiquadState>();
    private int _sampleRate;
    private int _rampRemaining;   // samples left in the cross-ramp (0 = steady)

    /// <summary>Create an EQ stage for <paramref name="channels"/> channels; <paramref name="declickSamples"/> is the
    /// coefficient cross-ramp length (default ~5 ms at 48k).</summary>
    public EqStage(int channels, int declickSamples = 256)
    {
        _channels = Math.Max(1, channels);
        _declickSamples = Math.Max(1, declickSamples);
    }

    /// <inheritdoc/>
    public int LatencySamples => 0;
    /// <inheritdoc/>
    public bool Bypassed { get; set; }
    /// <summary>The band count.</summary>
    public int BandCount => _bands.Length;
    /// <summary>True while a coefficient cross-ramp is in progress.</summary>
    public bool IsRamping => _rampRemaining > 0;

    /// <summary>The active coefficients for band <paramref name="i"/> (for golden tests).</summary>
    public BiquadCoeffs ActiveCoeffs(int i) => _active[i];

    /// <summary>Replace the full band set (a freq/Q/topology change; spec §7.8). Recomputes coefficients OFF-block and
    /// starts a cross-ramp. Band count changes reallocate state (control-thread only — never on the RT path).</summary>
    public void SetBands(ReadOnlySpan<BiquadBand> bands, int sampleRate)
    {
        _sampleRate = sampleRate;
        if (bands.Length != _bands.Length)
        {
            _bands = new BiquadBand[bands.Length];
            _active = new BiquadCoeffs[bands.Length];
            _pending = new BiquadCoeffs[bands.Length];
            _stateActive = new BiquadState[bands.Length * _channels];
            _statePending = new BiquadState[bands.Length * _channels];
            bands.CopyTo(_bands);
            for (int i = 0; i < bands.Length; i++) _active[i] = BiquadCoeffs.Design(bands[i], sampleRate);
            _rampRemaining = 0;   // fresh topology — no ramp source
            return;
        }

        // Same count: recompute pending coefficients and cross-ramp active→pending.
        bands.CopyTo(_bands);
        for (int i = 0; i < bands.Length; i++) _pending[i] = BiquadCoeffs.Design(bands[i], sampleRate);
        StartRamp();
    }

    /// <summary>Change a single band's gain (spec §7.8: a gain-only tweak) — recomputes that band's coefficients and
    /// cross-ramps (no zipper). Other bands keep their coefficients.</summary>
    public void SetBandGain(int index, float gainDb)
    {
        if ((uint)index >= (uint)_bands.Length) return;
        _bands[index] = _bands[index] with { GainDb = gainDb };
        for (int i = 0; i < _bands.Length; i++) _pending[i] = BiquadCoeffs.Design(_bands[i], _sampleRate);
        StartRamp();
    }

    private void StartRamp()
    {
        // Seed the pending state from the active state so the two cascades stay phase-aligned entering the ramp.
        Array.Copy(_stateActive, _statePending, _stateActive.Length);
        _rampRemaining = _declickSamples;
    }

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int n = frames * ctx.Channels;
        if (Bypassed || _bands.Length == 0)
        {
            if (!src.Overlaps(dst)) src[..n].CopyTo(dst);
            return frames;
        }

        int ch = Math.Min(ctx.Channels, _channels);
        int bands = _bands.Length;

        for (int f = 0; f < frames; f++)
        {
            int b = f * ctx.Channels;
            bool ramping = _rampRemaining > 0;
            float t = ramping ? 1f - (_rampRemaining - 1) / (float)_declickSamples : 1f;   // 0→1 across the window

            for (int c = 0; c < ch; c++)
            {
                float x = src[b + c];
                float yActive = x;
                for (int band = 0; band < bands; band++)
                    yActive = _stateActive[band * _channels + c].Process(yActive, in _active[band]);

                if (ramping)
                {
                    float yPending = x;
                    for (int band = 0; band < bands; band++)
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

            if (_rampRemaining > 0 && --_rampRemaining == 0)
            {
                // Ramp complete: pending becomes the sole active cascade (adopt its coefficients + state).
                (_active, _pending) = (_pending, _active);
                Array.Copy(_statePending, _stateActive, _stateActive.Length);
            }
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
        _balance.Advance(frames);
        float bal = _balance.Current;

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
/// The TERMINAL brickwall limiter (spec §7.3/§7.7): always present, ceiling ~-1.5 dBTP, after any gain/EQ boost — a
/// boosted signal can never exceed the ceiling. Instant-attack (the output is hard-guaranteed under the ceiling every
/// sample) with a smoothed release so it recovers cleanly; channel-linked (one gain across the frame preserves the image).
/// M2 uses SAMPLE-peak detection (inter-sample true-peak oversampling is a later refinement — see report). Alloc-free.
/// </summary>
public sealed class LimiterStage : IDspStage
{
    private float _ceiling;      // linear
    private float _gain = 1f;    // current gain-reduction state
    private float _releaseCoeff;
    private int _mixRate;

    /// <summary>Create a limiter at <paramref name="ceilingDbTp"/> dBTP with a <paramref name="releaseMs"/> release.</summary>
    public LimiterStage(float ceilingDbTp = -1.5f, float releaseMs = 50f, int mixRate = 48000)
    {
        _ceiling = DbToLinear(ceilingDbTp);
        _mixRate = mixRate;
        SetRelease(releaseMs);
    }

    /// <inheritdoc/>
    public int LatencySamples => 0;   // instant-attack, no lookahead delay in M2
    /// <inheritdoc/>
    public bool Bypassed { get; set; }

    /// <summary>The linear ceiling.</summary>
    public float Ceiling => _ceiling;

    /// <summary>Update the ceiling (dBTP).</summary>
    public void SetCeilingDbTp(float dbTp) => _ceiling = DbToLinear(dbTp);

    private void SetRelease(float releaseMs)
    {
        float samples = MathF.Max(1f, releaseMs * 0.001f * _mixRate);
        _releaseCoeff = MathF.Exp(-1f / samples);
    }

    /// <inheritdoc/>
    public int Process(ReadOnlySpan<float> src, Span<float> dst, int frames, in BlockCtx ctx)
    {
        int ch = ctx.Channels;
        int n = frames * ch;
        if (Bypassed)
        {
            if (!src.Overlaps(dst)) src[..n].CopyTo(dst);
            return frames;
        }

        for (int f = 0; f < frames; f++)
        {
            int b = f * ch;
            // Channel-linked peak for this frame.
            float peak = 0f;
            for (int c = 0; c < ch; c++) { float a = MathF.Abs(src[b + c]); if (a > peak) peak = a; }

            // Instant attack: never let the output exceed the ceiling.
            float targetGain = peak > _ceiling ? _ceiling / peak : 1f;
            if (targetGain < _gain) _gain = targetGain;                       // attack: instant
            else _gain = targetGain + (_gain - targetGain) * _releaseCoeff;   // release: smoothed

            for (int c = 0; c < ch; c++) dst[b + c] = src[b + c] * _gain;
        }
        return frames;
    }

    /// <summary>dB → linear amplitude.</summary>
    public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}

/// <summary>
/// A linear-interpolation sample-rate converter (spec §7.3 SRC edge). Present ONLY when device-rate != mix-rate (normally
/// elided, since every source resamples INTO the fixed mix format at the decode edge). Frame-count-CHANGING, so it is a
/// terminal edge stage (not an in-place master-chain node): <see cref="Convert"/> reads input frames and writes a
/// (different) number of output frames. Also used at the decode edge by <see cref="LinearResampler"/>.
/// </summary>
public sealed class ResampleStage
{
    private readonly LinearResampler _resampler;

    /// <summary>Create an SRC from <paramref name="fromRate"/> to <paramref name="toRate"/> for <paramref name="channels"/> channels.</summary>
    public ResampleStage(int fromRate, int toRate, int channels) => _resampler = new LinearResampler(fromRate, toRate, channels);

    /// <summary>True when the rates differ (the stage is not a no-op).</summary>
    public bool IsActive => _resampler.IsActive;

    /// <summary>Convert <paramref name="inFrames"/> input frames → output frames written to <paramref name="dst"/>.
    /// Returns produced-output and consumed-input counts — the caller retains <c>src[Consumed..]</c> on a short dst.</summary>
    public ResampleResult Convert(ReadOnlySpan<float> src, int inFrames, Span<float> dst) =>
        _resampler.Process(src, inFrames, dst);
}
