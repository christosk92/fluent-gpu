using System;
using System.Collections.Concurrent;

namespace FluentGpu.Media;

/// <summary>
/// Result of one <see cref="PolyphaseResampler.Process"/> call — both sides of the rate conversion so the caller can
/// retain unread input when <c>dst</c> fills before the source block is exhausted.
/// </summary>
public readonly struct ResampleResult
{
    /// <summary>Output frames written into <c>dst</c>.</summary>
    public int Produced { get; init; }
    /// <summary>Input frames fully consumed from <c>src</c> (caller may drop these; retain <c>src[Consumed..]</c>).</summary>
    public int Consumed { get; init; }

    public ResampleResult(int produced, int consumed) { Produced = produced; Consumed = consumed; }
}

/// <summary>
/// Windowed-sinc polyphase sample-rate converter (spec §7.1: "every source resamples INTO the fixed mix format at the decode
/// edge"). Taps = 64, Kaiser β = 9, cutoff <c>0.5·min(from, to)/from</c>: 0.0 dB at 20 kHz and −99 dB at 24.1 kHz for 44.1→48
/// (V-PE22). Exact rational ratios (<c>l ≤ 1024</c> after reduction: every standard rate pair) step one phase per output with no
/// drift; other ratios interpolate between <see cref="InterpPhases"/>+1 kernel rows. Interleaved <c>f32</c>, alloc-free per block.
/// <para>
/// The (Taps−1)-frame HISTORY lives inside the resampler: <see cref="Process"/> consumes every input frame it can and keeps the
/// tail, so a caller that refills only when its hold is empty is never stalled (V-PE1). When <c>dst</c> fills mid-block the caller
/// MUST retain <c>src[Consumed..]</c> and pass it as the prefix of the next call.
/// </para>
/// <para>
/// Group delay is absorbed: the first D output frames of a stream are discarded and <see cref="Flush"/> emits the trailing half
/// kernel, trimmed so the stream totals <c>⌈in × to/from⌉</c> frames (within one of <c>⌊in × to/from⌋</c>, and never short of a length a
/// caller worked out by rounding or flooring: the gapless trim cuts any extra frame at the exact length), so trims and joins are not
/// shifted (V-PE36) — <see cref="LatencySamples"/> is 0.
/// </para>
/// </summary>
public sealed class PolyphaseResampler
{
    /// <summary>Kernel length in input frames (a Kaiser β = 9 windowed sinc).</summary>
    public const int Taps = 64;
    /// <summary>Kernel rows an inexact ratio interpolates between (the table holds this + 1 rows, so <c>row + 1</c> never wraps).</summary>
    public const int InterpPhases = 256;

    private const int H = Taps - 1;                        // history frames kept between calls
    private const int FlushFrames = (Taps + 1) / 2 + 2;    // zero frames Flush may push through: the trailing half kernel (D = 31.5, rounded up) + 2, so the whole tail is always there to trim
    private const int MaxExactPhases = 1024;               // l after reduction above which the ratio interpolates instead

    // Kernel tables are immutable once built and depend only on the rate pair, so a seek's second decoder (a new resampler on the same
    // pair) shares them instead of paying the Bessel/sinc build again. Bounded: a handful of pairs is every real session.
    private static readonly ConcurrentDictionary<long, float[]> s_tables = new();
    private const int MaxCachedTables = 16;

    private readonly int _from, _to, _channels, _phases, _stepNum, _preRoll;
    private readonly bool _active, _exact;
    private readonly double _step;                         // interpolated: input frames per output frame
    private readonly float[] _taps;                        // [rows × Taps], phase-major; row p = kernel for fractional offset p/_phases
    private readonly float[] _hist;                        // H frames × channels: the frames just before the next block
    private readonly float[] _zeros;                       // FlushFrames × channels of silence, never written
    private long _acc;                                     // exact: next output centre in units of 1/_phases of a VIRTUAL frame (virtual frame 0 = oldest history frame)
    private double _pos;                                   // interpolated: the same, in virtual frames
    private int _skipOut;                                  // group-delay pre-roll still to discard (output frames)
    private long _inTotal, _outTotal;                      // real input frames consumed / output frames produced since Reset
    private long _flushTarget;                             // total output frames the stream is trimmed to; −1 until the first Flush
    private int _flushLeft;                                // zero frames of the EOF flush not yet pushed through

    /// <summary>Create a resampler from <paramref name="fromRate"/> Hz to <paramref name="toRate"/> Hz for
    /// <paramref name="channels"/> channels.</summary>
    public PolyphaseResampler(int fromRate, int toRate, int channels)
    {
        _from = fromRate; _to = toRate; _channels = Math.Max(1, channels);
        _active = fromRate > 0 && toRate > 0 && fromRate != toRate;
        if (_active)
        {
            int g = Gcd(fromRate, toRate), l = toRate / g, m = fromRate / g;
            _exact = l <= MaxExactPhases;
            _phases = _exact ? l : InterpPhases;
            _stepNum = _exact ? m : 0;
            _step = (double)fromRate / toRate;
            _preRoll = (int)Math.Floor((Taps - 1) / 2.0 * toRate / fromRate + 0.5);                          // the kernel's leading half, in OUTPUT frames (V-PE36)
            _taps = Table(fromRate, toRate, _exact ? _phases : _phases + 1, _phases, 0.5 * Math.Min(fromRate, toRate) / fromRate);
            _hist = new float[H * _channels];
            _zeros = new float[FlushFrames * _channels];
        }
        else { _taps = Array.Empty<float>(); _hist = Array.Empty<float>(); _zeros = Array.Empty<float>(); }
        Reset();
    }

    /// <summary>True when the rates differ (the resampler does real work; otherwise <see cref="Process"/> is a copy).</summary>
    public bool IsActive => _active;

    /// <summary>The added latency in OUTPUT frames: 0 — the group delay is absorbed by discarding the first D output frames and
    /// emitting the trailing D on <see cref="Flush"/> (V-PE36).</summary>
    public int LatencySamples => 0;

    /// <summary>Kernel rows in the table: <c>l</c> for an exact ratio, <see cref="InterpPhases"/> + 1 for an interpolated one, 0 when
    /// inactive.</summary>
    internal int PhaseRows => _taps.Length / Taps;

    /// <summary>True when the ratio reduces to <c>l ≤ 1024</c> and so steps one exact phase per output (no interpolation between rows).</summary>
    internal bool IsExact => _active && _exact;

    /// <summary>The worst-case output-frame capacity needed for <paramref name="inFrames"/> input frames (for sizing dst).</summary>
    public int MaxOutFrames(int inFrames) => _active ? (int)Math.Ceiling(inFrames * (double)_to / _from) + 1 : inFrames;

    /// <summary>
    /// Largest source-frame pull whose <see cref="MaxOutFrames"/> fits in <paramref name="wantOutFrames"/> output slots.
    /// Decoders use this so the common path fully consumes each pull; <see cref="ResampleResult.Consumed"/> still covers short/partial
    /// <c>dst</c> pumps.
    /// </summary>
    public int SrcFramesForOutput(int wantOutFrames) =>
        _active ? Math.Max(1, (int)Math.Floor((wantOutFrames - 1) * (double)_from / _to)) : Math.Max(0, wantOutFrames);

    /// <summary>Reset all continuity state (a seek/discontinuity — the caller declicks and drops any retained input): the history is
    /// silence, the pre-roll is owed again and the EOF flush is re-armed.</summary>
    public void Reset()
    {
        Array.Clear(_hist);
        _acc = (long)H * _phases; _pos = H;                                                                  // the first output centres on virtual frame H = src[0]
        _skipOut = _preRoll;
        _inTotal = _outTotal = 0;
        _flushTarget = -1;
        _flushLeft = FlushFrames;
    }

    /// <summary>
    /// Row p holds <c>h[k] = sinc_c(k − D + p/denom) · kaiser(k − D + p/denom)</c>, D = (Taps−1)/2 — the CAUSAL kernel for an output at
    /// fractional offset p/denom past input frame i, applied to <c>x[i − k]</c> (V-PE2: argument <c>k − D + frac</c>, not
    /// <c>k − D − frac</c>; the mirrored kernel resamples a ramp with the wrong sub-frame timing). Unity DC gain per row.
    /// </summary>
    private static float[] BuildTaps(int rows, int denom, double cutoff)
    {
        const double beta = 9.0;
        double d = (Taps - 1) / 2.0, i0b = BesselI0(beta);
        var t = new float[rows * Taps];
        Span<double> row = stackalloc double[Taps];
        for (int p = 0; p < rows; p++)
        {
            double frac = (double)p / denom, sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double x = k - d + frac;
                double sinc = Math.Abs(x) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                double w = x / (d + 0.5);                                                                    // Kaiser argument in (−1, 1) over the kernel support
                double kaiser = Math.Abs(w) < 1 ? BesselI0(beta * Math.Sqrt(1 - w * w)) / i0b : 0;
                double v = sinc * kaiser; row[k] = v; sum += v;
            }
            for (int k = 0; k < Taps; k++) t[p * Taps + k] = (float)(row[k] / sum);
        }
        return t;
    }

    private static float[] Table(int fromRate, int toRate, int rows, int denom, double cutoff)
    {
        long key = ((long)fromRate << 32) | (uint)toRate;
        if (s_tables.TryGetValue(key, out float[]? cached)) return cached;
        float[] built = BuildTaps(rows, denom, cutoff);
        if (s_tables.Count < MaxCachedTables) s_tables.TryAdd(key, built);
        return built;
    }

    private static double BesselI0(double x)
    {
        double s = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 60; k++) { term *= q / ((double)k * k); s += term; if (term < 1e-13 * s) break; }
        return s;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>Virtual input frame v ∈ [0, H + inFrames): v &lt; H is history, else <c>src[v − H]</c>.</summary>
    private float At(ReadOnlySpan<float> src, int v, int c) => v < H ? _hist[v * _channels + c] : src[(v - H) * _channels + c];

    /// <summary>
    /// Resample <paramref name="inFrames"/> frames of <paramref name="src"/> into <paramref name="dst"/>. Consumes everything it can: when
    /// the input runs out EVERY frame is consumed and the last H frames of (history ++ src) become the history; when <c>dst</c> fills,
    /// the frames no future output can touch are consumed and the history is rebuilt the same way. Callers retain <c>src[Consumed..]</c>
    /// as the next prefix. Progress is guaranteed (a call with input and room for one frame consumes input or produces output), so a
    /// caller that refills only when its hold is empty never stalls (V-PE1).
    /// </summary>
    public ResampleResult Process(ReadOnlySpan<float> src, int inFrames, Span<float> dst)
    {
        int ch = _channels;
        if (!_active)
        {
            // Defense in depth (spec §7.1): clamp untrusted counts to the buffers rather than throw — a short copy is always safe.
            int n = Math.Min(inFrames * ch, Math.Min(src.Length, dst.Length));
            n -= n % ch;
            if (n <= 0) return default;
            src[..n].CopyTo(dst);
            return new ResampleResult(n / ch, n / ch);
        }
        ResampleResult r = Run(src, inFrames, dst);
        _inTotal += r.Consumed;
        return r;
    }

    /// <summary>
    /// EOF: push zero frames through so the trailing half kernel is emitted (V-PE21/V-PE36), trimmed so the stream totals exactly
    /// <c>⌈frames in × to/from⌉</c> output frames. Returns the frames produced. If <paramref name="dst"/> is too small for the whole
    /// tail (about D·to/from + 1 frames) the rest stays owed: call again with a fresh span until it returns 0, which is the end.
    /// Re-armed by <see cref="Reset"/>.
    /// </summary>
    public int Flush(Span<float> dst)
    {
        if (!_active || _flushLeft <= 0) return 0;
        if (_flushTarget < 0) _flushTarget = (_inTotal * _to + _from - 1) / _from;                          // integer ceil: no float rounding at an exact multiple
        long owed = _flushTarget - _outTotal;
        if (owed <= 0) { _flushLeft = 0; return 0; }
        int room = (int)Math.Min(owed, dst.Length / _channels);
        if (room <= 0) return 0;
        ResampleResult r = Run(_zeros.AsSpan(0, _flushLeft * _channels), _flushLeft, dst[..(room * _channels)]);
        _flushLeft -= r.Consumed;
        return r.Produced;
    }

    /// <summary>The convolution + consume + history rebuild shared by <see cref="Process"/> (real input) and <see cref="Flush"/>
    /// (zeros): the former counts the frames it consumed into the stream length, the latter does not.</summary>
    private ResampleResult Run(ReadOnlySpan<float> src, int inFrames, Span<float> dst)
    {
        int ch = _channels;
        if (inFrames < 0) inFrames = 0;
        if (inFrames * ch > src.Length) inFrames = src.Length / ch;

        int maxOut = dst.Length / ch, outFrames = 0, last = H + inFrames - 1;                                // the newest virtual frame available
        while (outFrames < maxOut)
        {
            int center, row; float lerp = 0f;
            if (_exact)
            {
                center = (int)(_acc / _phases);
                row = (int)(_acc - (long)center * _phases);
            }
            else
            {
                center = (int)_pos;                                                                          // _pos ≥ H > 0: truncation is floor
                double fp = (_pos - center) * InterpPhases;
                row = (int)fp;
                if (row > InterpPhases - 1) row = InterpPhases - 1;                                          // row + 1 ≤ InterpPhases: the table has InterpPhases + 1 rows, no wrap (V-PE22)
                lerp = (float)(fp - row);
            }
            if (center > last) break;                                                                        // need the next block

            if (_skipOut > 0) _skipOut--;                                                                    // group-delay pre-roll: advance without computing
            else
            {
                ReadOnlySpan<float> r0 = _taps.AsSpan(row * Taps, Taps);
                ReadOnlySpan<float> r1 = _exact ? r0 : _taps.AsSpan((row + 1) * Taps, Taps);
                int ob = outFrames * ch;
                for (int c = 0; c < ch; c++)
                {
                    float a0 = 0f, a1 = 0f;
                    if (center - H >= H)
                    {
                        // Every tap is in src (virtual v ↔ src[v − H]); the newest is src[center − H]. Same k-ascending order as the
                        // straddling path below, so the sums are bit-identical wherever a block boundary falls.
                        int p = (center - H) * ch + c;
                        for (int k = 0; k < Taps; k++, p -= ch)
                        {
                            float s = src[p];
                            a0 += r0[k] * s;
                            if (!_exact) a1 += r1[k] * s;
                        }
                    }
                    else
                    {
                        for (int k = 0; k < Taps; k++)
                        {
                            float s = At(src, center - k, c);                                                // center − k ≥ center − H ≥ 0 by construction
                            a0 += r0[k] * s;
                            if (!_exact) a1 += r1[k] * s;
                        }
                    }
                    dst[ob + c] = _exact ? a0 : a0 + (a1 - a0) * lerp;
                }
                outFrames++;
            }
            if (_exact) _acc += _stepNum; else _pos += _step;
        }

        // Consume. When the input ran out, every src frame is taken (the last H frames of history ++ src become the history). When dst
        // filled, a src frame is dead once no future output can touch it: the next output reads virtual [next − H, next], and
        // src[j] is virtual H + j, so j < next − 2H is dead (V-PE1).
        int next = _exact ? (int)(_acc / _phases) : (int)_pos;
        int consumed = next > last ? inFrames : Math.Clamp(next - 2 * H, 0, inFrames);

        // New history = virtual frames [consumed, consumed + H) of this call's (history ++ src). The part that is still old history moves
        // down inside _hist (CopyTo is overlap-safe); the rest comes from src.
        int keepOld = Math.Max(0, H - consumed);
        if (keepOld > 0 && consumed > 0) _hist.AsSpan(consumed * ch, keepOld * ch).CopyTo(_hist);
        int fromSrc = H - keepOld;
        if (fromSrc > 0) src.Slice(Math.Max(consumed - H, 0) * ch, fromSrc * ch).CopyTo(_hist.AsSpan(keepOld * ch));
        if (_exact) _acc -= (long)consumed * _phases; else _pos -= consumed;
        _outTotal += outFrames;
        return new ResampleResult(outFrames, consumed);
    }
}
