using System;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// #48 E7: the per-voice effects behind VoiceAutomationStage's post-envelope part — an echo / delay (one delay line per channel
// with feedback) and a small stereo reverb (an 8-line FDN). Each unit belongs to one VoiceAutomation (built with it, off the
// render thread, its lines sized for the request) and is armed on one voice. The render thread runs it in place on the voice's
// enveloped signal: out = in × dry + wet, the wet fed by in × send (so what the send let in rings on after the send and the
// envelope fall: trails). A unit reports Ringing while its lines hold anything above −80 dBFS; the mixer keeps the voice alive
// for it. Alloc-free and lock-free while running; values that decay below 1e-20 are flushed to 0 (no denormals on silence).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One effect unit: its dry and send lanes (the dry/wet table split by its law), the arm ramp, and the tail detector.</summary>
internal abstract class VoiceEffect
{
    /// <summary>A tail below this (−80 dBFS) is over.</summary>
    public const float TailFloor = 1e-4f;
    /// <summary>Values below this are flushed to 0 in the feedback paths (far above the float subnormal range).</summary>
    protected const float Flush = 1e-20f;

    private readonly ScalarLane _dry, _send;
    private float _mix;                          // 0 = neutral (dry 1, send 0) … 1 = the lanes
    private float _mixTarget;
    private int _quiet;                          // frames since anything above TailFloor was written into the lines
    private readonly int _quietNeeded;           // the longest path through the lines: quiet that long = silent lines

    /// <summary>The stage that armed this unit (a unit's lines belong to one voice).</summary>
    public object? Owner;
    /// <summary>Ramping out (or out): the send is closing and the unit only rings out.</summary>
    public bool Retiring { get; private set; }

    protected VoiceEffect(ScalarLane dry, ScalarLane send, int quietNeeded)
    {
        _dry = dry;
        _send = send;
        _quietNeeded = Math.Max(1, quietNeeded);
        _quiet = _quietNeeded;
    }

    /// <summary>The lines hold something above −80 dBFS.</summary>
    public bool Ringing => _quiet < _quietNeeded;
    /// <summary>Retired, ramped out and silent: the stage can drop it.</summary>
    public bool Finished => Retiring && _mix == 0f && !Ringing;

    /// <summary>Start following the lanes, ramped in from neutral over <see cref="VoiceAutomation.DeclickFrames"/> when
    /// <paramref name="ramp"/>, else at once.</summary>
    public void RampIn(bool ramp)
    {
        Retiring = false;
        _mixTarget = 1f;
        if (!ramp) _mix = 1f;
    }

    /// <summary>Close the send and restore the dry over <see cref="VoiceAutomation.DeclickFrames"/>; the tail rings out.</summary>
    public void RampOut()
    {
        Retiring = true;
        _mixTarget = 0f;
    }

    /// <summary>Drop the tail at once (the voice is parked or replaced).</summary>
    public void Silence()
    {
        if (!Ringing) return;
        ClearLines();
        _quiet = _quietNeeded;
    }

    /// <summary>Run the unit in place over <paramref name="frames"/> frames of <paramref name="buf"/> (interleaved,
    /// <paramref name="stride"/> channels, the first <paramref name="ch"/> processed) starting at mixer frame
    /// <paramref name="startFrame"/>. <paramref name="dry"/> and <paramref name="send"/> are the stage's chunk scratch.</summary>
    public void Process(Span<float> buf, int frames, int stride, int ch, long startFrame, float[] dry, float[] send)
    {
        // Nothing let in and nothing ringing: the dry gain is 1 for every law at a send of 0, so the block passes as is.
        if (!Ringing && (_mix == 0f && _mixTarget == 0f || _send.ZeroOver(startFrame, frames)))
        {
            AdvanceMix(frames);
            return;
        }
        int chunk = dry.Length;
        float step = 1f / VoiceAutomation.DeclickFrames;
        for (int off = 0; off < frames; off += chunk)
        {
            int n = Math.Min(chunk, frames - off);
            long frame = startFrame + off;
            _dry.Fill(dry.AsSpan(0, n), frame);
            _send.Fill(send.AsSpan(0, n), frame);
            if (_mix != 1f || _mixTarget != 1f)
                for (int f = 0; f < n; f++)
                {
                    _mix = _mix < _mixTarget ? MathF.Min(_mixTarget, _mix + step) : MathF.Max(_mixTarget, _mix - step);
                    dry[f] = 1f + _mix * (dry[f] - 1f);
                    send[f] *= _mix;
                }
            int quietRun = Run(buf.Slice(off * stride), n, stride, ch, dry, send);
            _quiet = quietRun >= n ? Math.Min(_quietNeeded, _quiet + n) : quietRun;
        }
    }

    private void AdvanceMix(int frames)
    {
        float d = frames / (float)VoiceAutomation.DeclickFrames;
        _mix = _mix < _mixTarget ? MathF.Min(_mixTarget, _mix + d) : MathF.Max(_mixTarget, _mix - d);
    }

    /// <summary>Process one chunk; returns how many frames at the chunk's END wrote nothing above <see cref="TailFloor"/> (n when
    /// none did).</summary>
    protected abstract int Run(Span<float> buf, int n, int stride, int ch, float[] dry, float[] send);

    /// <summary>Zero every line and filter state.</summary>
    protected abstract void ClearLines();

    /// <summary>A one-pole low-pass coefficient for <paramref name="hz"/> (0 = none).</summary>
    protected static float OnePole(float hz, int rate)
        => hz <= 0f ? 0f : (float)(1.0 - Math.Exp(-2.0 * Math.PI * Math.Min(hz, rate * 0.45f) / rate));

    /// <summary>Split a dry/wet table (0..1) into its dry and send lanes by <paramref name="law"/>.</summary>
    public static (ScalarLane Dry, ScalarLane Send) Split(ReadOnlySpan<float> wet, DryWetLaw law, long start, int frames)
    {
        if (wet.IsEmpty) wet = [0f];
        var dry = new float[wet.Length];
        var send = new float[wet.Length];
        for (int i = 0; i < wet.Length; i++)
        {
            float w = float.IsNaN(wet[i]) ? 0f : Math.Clamp(wet[i], 0f, 1f);
            (dry[i], send[i]) = law switch
            {
                DryWetLaw.Linear => (1f - w, w),
                DryWetLaw.Send => (1f, w),
                DryWetLaw.DjKnob => (MathF.Min(1f, 2f * (1f - w)), MathF.Min(1f, 2f * w)),
                _ => (w == 1f ? 0f : MathF.Cos(w * MathF.PI * 0.5f), w == 1f ? 1f : MathF.Sin(w * MathF.PI * 0.5f)),
            };
        }
        return (new ScalarLane(dry, start, frames), new ScalarLane(send, start, frames));
    }
}

/// <summary>An echo or a delay: one line per channel, a feedback gain and an optional damping low-pass in the feedback path.
/// The repeat at k × delay has the level send × feedback^(k−1).</summary>
internal sealed class DelayEffect : VoiceEffect
{
    private readonly float[] _line;              // _frames × _channels, interleaved
    private readonly float[] _lp;                // the feedback low-pass's state per channel
    private readonly int _frames, _channels;
    private readonly float _feedback, _damp;
    private int _pos;

    public DelayEffect(ScalarLane dry, ScalarLane send, int channels, int delayFrames, float feedback, float damp)
        : base(dry, send, delayFrames)
    {
        _channels = channels;
        _frames = delayFrames;
        _feedback = feedback;
        _damp = damp;
        _line = new float[delayFrames * channels];
        _lp = new float[channels];
    }

    /// <summary>The delay in frames.</summary>
    public int DelayFrames => _frames;
    /// <summary>The bytes this unit holds.</summary>
    public long Bytes => (long)_line.Length * sizeof(float);

    /// <summary>The unit for <paramref name="spec"/>, or null when its wet is 0 throughout (nothing to allocate).</summary>
    public static DelayEffect? Build(DelayLaneSpec spec, int rate, int channels, long start, int frames)
    {
        var (dry, send) = Split(spec.Wet.Span, spec.Law, start, frames);
        if (send.IsConstant(0f)) return null;
        int delay = Math.Clamp(spec.DelayFrames, 1, DelayLaneSpec.MaxDelaySeconds * rate);
        float fb = float.IsNaN(spec.Feedback) ? 0f : Math.Clamp(spec.Feedback, 0f, DelayLaneSpec.MaxFeedback);
        return new DelayEffect(dry, send, channels, delay, fb, OnePole(spec.HighCutHz, rate));
    }

    protected override int Run(Span<float> buf, int n, int stride, int ch, float[] dry, float[] send)
    {
        int all = ch;
        ch = Math.Min(ch, _channels);
        var line = _line;
        int quiet = 0, pos = _pos, chs = _channels;
        float fb = _feedback, damp = _damp;
        for (int f = 0; f < n; f++)
        {
            int b = f * stride, l = pos * chs;
            float g = dry[f], s = send[f], peak = 0f;
            for (int c = 0; c < ch; c++)
            {
                float x = buf[b + c];
                float d = line[l + c];
                float back = d;
                if (damp > 0f)
                {
                    float lp = _lp[c] + damp * (d - _lp[c]);
                    if (MathF.Abs(lp) < Flush) lp = 0f;
                    _lp[c] = lp;
                    back = lp;
                }
                float v = x * s + fb * back;
                if (MathF.Abs(v) < Flush) v = 0f;
                line[l + c] = v;
                buf[b + c] = x * g + d;
                float a = MathF.Abs(v);
                if (a > peak) peak = a;
            }
            for (int c = ch; c < all; c++) buf[b + c] *= g;   // channels past the line's take the dry only
            quiet = peak > TailFloor ? 0 : quiet + 1;
            if (++pos == _frames) pos = 0;
        }
        _pos = pos;
        return quiet;
    }

    protected override void ClearLines()
    {
        Array.Clear(_line);
        Array.Clear(_lp);
    }
}

/// <summary>
/// A small stereo reverb: a 12 dB/oct high-pass (the low cut; the network has a mode at DC) and two Schroeder all-passes per channel
/// (gain 0.6) condition the send, which feeds an 8-line feedback delay
/// network (left into lines 0-3, right into 4-7, alternating signs) mixed by a Householder matrix (I − 2/N·11ᵀ: lossless, O(N)).
/// Each line's gain g = 10^(−3·L / (RT60·rate)) sets the decay; a one-pole low-pass per line damps the highs. The two outputs tap
/// all eight lines with orthogonal sign patterns (decorrelated left and right), scaled so a steady pink-noise input (a music-like
/// spectrum) comes out at about its own level: the network's power gain at a frequency is about 1 / (1 − (g·|damping|)²) times
/// the low cut's, averaged over a pink spectrum when the unit is built (without damping or low cut that is √((1 − g²) / 8)).
/// </summary>
internal sealed class ReverbEffect : VoiceEffect
{
    private const int N = 8;
    private const float Diffusion = 0.6f;
    // Line and all-pass lengths at 48 kHz (primes; scaled with the rate).
    private static readonly int[] s_lines = [1433, 1601, 1867, 2053, 2251, 2399, 2617, 2797];
    private static readonly int[] s_diffusers = [211, 337, 229, 353];    // left 1, left 2, right 1, right 2
    private static readonly float[] s_outL = [1, -1, 1, -1, 1, -1, 1, -1];
    private static readonly float[] s_outR = [1, 1, -1, -1, 1, 1, -1, -1];
    private static readonly float[] s_in = [1, -1, 1, -1, 1, -1, 1, -1];

    private readonly float[][] _lines = new float[N][];
    private readonly int[] _pos = new int[N];
    private readonly float[] _gain = new float[N];
    private readonly float[] _lp = new float[N];
    private readonly float[] _read = new float[N];
    private readonly float[][] _ap = new float[4][];
    private readonly int[] _apPos = new int[4];
    private readonly float _damp, _outGain;
    private readonly BiquadCoeffs _lowCut;
    private BiquadState _lowCutL, _lowCutR;
    private readonly long _bytes;

    private ReverbEffect(ScalarLane dry, ScalarLane send, int[] lines, int[] diffusers, float decaySeconds, int rate, float damp, float lowCutHz)
        : base(dry, send, Max(lines) + diffusers[0] + diffusers[1] + diffusers[2] + diffusers[3])
    {
        _lowCut = BiquadCoeffs.Design(new BiquadBand(BiquadType.HighPass, lowCutHz, 0.70710678f, 0f), rate);
        double meanG2 = 0;
        long bytes = 0;
        for (int i = 0; i < N; i++)
        {
            _lines[i] = new float[lines[i]];
            _gain[i] = (float)Math.Pow(10.0, -3.0 * lines[i] / (decaySeconds * rate));
            meanG2 += _gain[i] * (double)_gain[i] / N;
            bytes += lines[i] * sizeof(float);
        }
        for (int i = 0; i < 4; i++) { _ap[i] = new float[diffusers[i]]; bytes += diffusers[i] * sizeof(float); }
        _damp = damp;
        _outGain = (float)OutputGain(meanG2, damp, lowCutHz, rate);
        _bytes = bytes;
    }

    /// <summary>The output scale that brings a steady pink-noise send out at its own level: the input reaches the lines at 8 × its
    /// power per channel pair; a frequency where one pass keeps (g·|D(f)|)² of the power (D = the damping low-pass) builds up to
    /// 1 / (1 − (g·|D(f)|)²) of it, behind the low cut's |HP(f)|². Averaged over a pink spectrum (equal power per octave, 20 Hz to
    /// 0.45 × rate). Control thread, once per unit.</summary>
    private static double OutputGain(double g2, float damp, float lowCutHz, int rate)
    {
        const int points = 96;
        double lo = Math.Log(20.0), hi = Math.Log(0.45 * rate), gain = 0;
        for (int i = 0; i < points; i++)
        {
            double hz = Math.Exp(lo + (hi - lo) * (i + 0.5) / points);
            double w = 2 * Math.PI * hz / rate;
            double d2 = damp > 0f ? damp * (double)damp / (1 + (1 - damp) * (1.0 - damp) - 2 * (1 - damp) * Math.Cos(w)) : 1.0;
            double r = lowCutHz / hz, hp2 = 1.0 / (1.0 + r * r * r * r);   // the 2nd-order Butterworth high-pass, |HP|²
            gain += hp2 / (1.0 - g2 * d2) / points;
        }
        return Math.Sqrt(1.0 / (N * gain));
    }

    /// <summary>The bytes this unit holds.</summary>
    public long Bytes => _bytes;

    private static int Max(int[] a)
    {
        int m = 0;
        foreach (int x in a) m = Math.Max(m, x);
        return m;
    }

    /// <summary>The unit for <paramref name="spec"/>, or null when its wet is 0 throughout (nothing to allocate).</summary>
    public static ReverbEffect? Build(ReverbLaneSpec spec, int rate, long start, int frames)
    {
        var (dry, send) = Split(spec.Wet.Span, spec.Law, start, frames);
        if (send.IsConstant(0f)) return null;
        float decay = float.IsNaN(spec.DecaySeconds) ? 1f : Math.Clamp(spec.DecaySeconds, 0.1f, ReverbLaneSpec.MaxDecaySeconds);
        double scale = rate / 48_000.0;
        var lines = new int[N];
        for (int i = 0; i < N; i++) lines[i] = Math.Max(1, (int)Math.Round(s_lines[i] * scale));
        var diff = new int[4];
        for (int i = 0; i < 4; i++) diff[i] = Math.Max(1, (int)Math.Round(s_diffusers[i] * scale));
        float lowCut = float.IsNaN(spec.LowCutHz) ? ReverbLaneSpec.DefaultLowCutHz : Math.Clamp(spec.LowCutHz, ReverbLaneSpec.MinLowCutHz, rate * 0.25f);
        return new ReverbEffect(dry, send, lines, diff, decay, rate, OnePole(spec.HighCutHz, rate), lowCut);
    }

    private float AllPass(int k, float x)
    {
        var a = _ap[k];
        int p = _apPos[k];
        float d = a[p];
        float v = x + Diffusion * d;
        if (MathF.Abs(v) < Flush) v = 0f;
        a[p] = v;
        _apPos[k] = p + 1 == a.Length ? 0 : p + 1;
        return d - Diffusion * v;
    }

    protected override int Run(Span<float> buf, int n, int stride, int ch, float[] dry, float[] send)
    {
        int quiet = 0;
        bool stereo = ch >= 2;
        float damp = _damp, k = _outGain;
        var read = _read;
        for (int f = 0; f < n; f++)
        {
            int b = f * stride;
            float xl = buf[b], xr = stereo ? buf[b + 1] : xl;
            float s = send[f];
            float inL = AllPass(1, AllPass(0, _lowCutL.Process(xl * s, in _lowCut)));
            float inR = AllPass(3, AllPass(2, _lowCutR.Process(xr * s, in _lowCut)));
            float sum = 0f, outL = 0f, outR = 0f;
            for (int i = 0; i < N; i++)
            {
                float d = _lines[i][_pos[i]];
                outL += s_outL[i] * d;
                outR += s_outR[i] * d;
                float back = d;
                if (damp > 0f)
                {
                    float lp = _lp[i] + damp * (d - _lp[i]);
                    if (MathF.Abs(lp) < Flush) lp = 0f;
                    _lp[i] = lp;
                    back = lp;
                }
                back *= _gain[i];
                read[i] = back;
                sum += back;
            }
            sum *= 2f / N;
            float peak = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));
            for (int i = 0; i < N; i++)
            {
                float v = read[i] - sum + s_in[i] * (i < N / 2 ? inL : inR);
                if (MathF.Abs(v) < Flush) v = 0f;
                var line = _lines[i];
                int p = _pos[i];
                line[p] = v;
                _pos[i] = p + 1 == line.Length ? 0 : p + 1;
                float a = MathF.Abs(v);
                if (a > peak) peak = a;
            }
            float g = dry[f];
            buf[b] = xl * g + outL * k;
            if (stereo) buf[b + 1] = xr * g + outR * k;
            for (int c = 2; c < ch; c++) buf[b + c] *= g;   // channels past the stereo pair take the dry only
            quiet = peak > TailFloor ? 0 : quiet + 1;
        }
        return quiet;
    }

    protected override void ClearLines()
    {
        foreach (var l in _lines) Array.Clear(l);
        foreach (var a in _ap) Array.Clear(a);
        Array.Clear(_lp);
        _lowCutL.Reset();
        _lowCutR.Reset();
    }
}
