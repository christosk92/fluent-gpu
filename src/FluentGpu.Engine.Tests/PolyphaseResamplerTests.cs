using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// WP-4a (Wavee playback smoothness, plan §4.6, V-PE1/V-PE2/V-PE21/V-PE22/V-PE23/V-PE36): the windowed-sinc polyphase SRC that replaced the
/// linear resampler. Every assertion compares PCM to an INDEPENDENT oracle — an analytic tone or ramp sampled at the documented time, an FFT of
/// the output, or the same stream pumped a different way — never to the resampler's own tables.
///
/// The documented timing (the contract the decoders' trims and joins rely on): the group delay D = (Taps − 1)/2 = 31.5 input frames is absorbed,
/// so the n-th output frame of a stream is the band-limited input evaluated at <c>τ(n) = (n + s)·from/to − D</c> input frames, where
/// <c>s = round(D·to/from)</c> output frames are discarded at the start (|τ(0)| ≤ half an output frame), and <c>Flush</c> trims the stream to
/// exactly <c>⌈in × to/from⌉</c> frames (within one of the plan's <c>⌊in × to/from⌋</c>; never short of a length computed by rounding or flooring).
/// </summary>
public sealed class PolyphaseResamplerTests
{
    private const double D = (PolyphaseResampler.Taps - 1) / 2.0;

    /// <summary>The documented number of output frames discarded at the start of a stream.</summary>
    private static int PreRoll(int from, int to) => (int)Math.Floor(D * to / from + 0.5);

    /// <summary>The documented length of a flushed stream of <paramref name="inFrames"/> input frames: ⌈in × to/from⌉ (integer arithmetic).</summary>
    private static long StreamLength(long inFrames, int from, int to) => (inFrames * to + from - 1) / from;

    // ── pumps ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Pump <paramref name="input"/> the way the decoder adapters do: refill the hold ONLY when it is empty (chunk sizes cycle through
    /// <paramref name="chunkPattern"/>), call Process into a <paramref name="dstFrames"/>-frame destination, drop <c>Consumed</c> frames from the
    /// front of the hold. Asserts every call made progress (V-PE1: a stall is the bug the history-in-the-resampler design removes).</summary>
    private static float[] Pump(PolyphaseResampler rs, float[] input, int ch, int[] chunkPattern, int dstFrames, bool flush)
    {
        int totalFrames = input.Length / ch;
        var dst = new float[dstFrames * ch];
        var hold = new float[chunkPattern.Max() * ch];
        var output = new List<float>(Math.Max(16, totalFrames * ch * 2));
        int holdFrames = 0, inPos = 0, pat = 0;
        while (true)
        {
            if (holdFrames == 0)
            {
                if (inPos >= totalFrames) break;
                int n = Math.Min(chunkPattern[pat++ % chunkPattern.Length], totalFrames - inPos);
                Array.Copy(input, inPos * ch, hold, 0, n * ch);
                holdFrames = n;
                inPos += n;
            }
            ResampleResult rr = rs.Process(hold.AsSpan(0, holdFrames * ch), holdFrames, dst);
            Assert.True(rr.Produced > 0 || rr.Consumed > 0, "the resampler stalled: a call with input and room produced nothing and consumed nothing");
            for (int i = 0; i < rr.Produced * ch; i++) output.Add(dst[i]);
            int unread = holdFrames - rr.Consumed;
            if (unread > 0 && rr.Consumed > 0) Array.Copy(hold, rr.Consumed * ch, hold, 0, unread * ch);
            holdFrames = unread;
        }
        if (flush) FlushInto(rs, ch, dstFrames, output);
        return output.ToArray();
    }

    /// <summary>EOF: call Flush with a <paramref name="dstFrames"/>-frame destination until it answers 0 (the end).</summary>
    private static void FlushInto(PolyphaseResampler rs, int ch, int dstFrames, List<float> output)
    {
        var dst = new float[Math.Max(1, dstFrames) * ch];
        int n;
        while ((n = rs.Flush(dst)) > 0)
            for (int i = 0; i < n * ch; i++) output.Add(dst[i]);
    }

    /// <summary>The whole input in ONE call into a destination sized by <c>MaxOutFrames</c>, then (optionally) the EOF flush.</summary>
    private static float[] OneShot(PolyphaseResampler rs, float[] input, int ch, bool flush)
    {
        int frames = input.Length / ch;
        var dst = new float[(rs.MaxOutFrames(frames) + 8) * ch];
        ResampleResult rr = rs.Process(input, frames, dst);
        Assert.Equal(frames, rr.Consumed);
        var output = new List<float>(rr.Produced * ch + 256);
        for (int i = 0; i < rr.Produced * ch; i++) output.Add(dst[i]);
        if (flush) FlushInto(rs, ch, 4096, output);
        return output.ToArray();
    }

    // ── helpers: signals and oracles ─────────────────────────────────────────────────────────────────────────────────────

    private static float[] Noise(int frames, int ch, int seed)
    {
        var rng = new Random(seed);
        var x = new float[frames * ch];
        for (int i = 0; i < x.Length; i++) x[i] = rng.NextSingle() * 2f - 1f;
        return x;
    }

    private static double ToneL(double t, int rate, double hz) => 0.5 * Math.Sin(2 * Math.PI * hz * t / rate + 0.3);
    private static double ToneR(double t, int rate, double hz) => 0.4 * Math.Sin(2 * Math.PI * hz * t / rate + 1.1);

    private static float[] StereoTones(int frames, int rate, double hzL, double hzR)
    {
        var x = new float[frames * 2];
        for (int j = 0; j < frames; j++)
        {
            x[2 * j] = (float)ToneL(j, rate, hzL);
            x[2 * j + 1] = (float)ToneR(j, rate, hzR);
        }
        return x;
    }

    private static void AssertIdentical(float[] expected, float[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: {actual.Length} floats, expected {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) Assert.Fail($"{what}: first difference at float {i}: {actual[i]:R} vs {expected[i]:R}");
    }

    private static void AssertClose(float[] expected, float[] actual, double tolerance, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: {actual.Length} floats, expected {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
            if (Math.Abs(expected[i] - actual[i]) > tolerance) Assert.Fail($"{what}: float {i} is {actual[i]:R}, expected {expected[i]:R} ± {tolerance}");
    }

    /// <summary>In-place radix-2 FFT (the oracle: nothing here calls the engine's own analyzer).</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        var cos = new double[n / 2];
        var sin = new double[n / 2];
        for (int k = 0; k < n / 2; k++) { double ang = -2 * Math.PI * k / n; cos[k] = Math.Cos(ang); sin[k] = Math.Sin(ang); }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = n / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < half; k++)
                {
                    double wr = cos[k * step], wi = sin[k * step];
                    int lo = i + k, hi = lo + half;
                    double tr = re[hi] * wr - im[hi] * wi, ti = re[hi] * wi + im[hi] * wr;
                    re[hi] = re[lo] - tr; im[hi] = im[lo] - ti;
                    re[lo] += tr; im[lo] += ti;
                }
        }
    }

    /// <summary>A coherent (bin-centred) tone through a periodic Hann window leaks into its own bin and the two beside it ONLY, so every other
    /// bin is the resampler's: returns the tone's amplitude and the largest other bin relative to the tone, in dB.</summary>
    private static (double Amplitude, double WorstSpurDb) Analyse(float[] y, int start, int n, int toneBin)
    {
        var re = new double[n];
        var im = new double[n];
        double windowSum = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
            re[i] = y[start + i] * w;
            windowSum += w;
        }
        Fft(re, im);
        double peak = Math.Sqrt(re[toneBin] * re[toneBin] + im[toneBin] * im[toneBin]), worst = 0;
        for (int k = 0; k <= n / 2; k++)
        {
            if (Math.Abs(k - toneBin) <= 3) continue;
            worst = Math.Max(worst, Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
        }
        return (2 * peak / windowSum, 20 * Math.Log10(Math.Max(worst, 1e-30) / peak));
    }

    private const int FftSize = 16384, FftSkip = 256;

    /// <summary>A mono tone at the output's FFT bin nearest <paramref name="hz"/>, resampled <paramref name="from"/>→<paramref name="to"/> in one call
    /// (no flush: the analysed window sits in the steady state, clear of both the start-up and the tail).</summary>
    private static (double Amplitude, double WorstSpurDb) ToneSpectrum(int from, int to, double hz)
    {
        int bin = (int)Math.Round(hz * FftSize / to);
        double f = (double)bin * to / FftSize;
        int inFrames = (int)((FftSkip + FftSize + 512) * (double)from / to) + 64;
        var x = new float[inFrames];
        for (int j = 0; j < inFrames; j++) x[j] = (float)(0.5 * Math.Sin(2 * Math.PI * f * j / from));
        float[] y = OneShot(new PolyphaseResampler(from, to, 1), x, 1, flush: false);
        Assert.True(y.Length >= FftSkip + FftSize, $"only {y.Length} output frames");
        return Analyse(y, FftSkip, FftSize, bin);
    }

    // ── response: passband, images ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    [InlineData(5000.0)]
    [InlineData(10000.0)]
    [InlineData(15000.0)]
    [InlineData(18000.0)]
    [InlineData(20000.0)]
    public void The_passband_of_44100_to_48000_is_flat_to_20_kHz(double hz)
    {
        (double amplitude, _) = ToneSpectrum(44100, 48000, hz);
        double gainDb = 20 * Math.Log10(amplitude / 0.5);
        Assert.InRange(gainDb, -0.1, 0.1);
    }

    [Fact]
    public void A_20_kHz_tone_through_44100_to_48000_leaves_every_image_below_minus_90_dB()
    {
        // The input's 20 kHz tone has its image at 44.1 − 20 = 24.1 kHz, which folds to 23.9 kHz in the 48 kHz output; every other image folds
        // elsewhere. The stopband starts at 24.06 kHz (Kaiser β 9: −90 dB), so NOTHING but the tone may stand above −90 dB.
        (_, double worstSpurDb) = ToneSpectrum(44100, 48000, 20000.0);
        Assert.True(worstSpurDb <= -90.0, $"worst spur {worstSpurDb:0.0} dB relative to the tone");
    }

    [Fact]
    public void The_inexact_ratio_44056_to_48000_interpolates_between_257_rows_and_keeps_images_below_minus_75_dB()
    {
        // gcd(44056, 48000) = 8, so l = 6000 > 1024: no exact phase table, the (InterpPhases + 1)-row path.
        var rs = new PolyphaseResampler(44056, 48000, 2);
        Assert.False(rs.IsExact);
        Assert.Equal(PolyphaseResampler.InterpPhases + 1, rs.PhaseRows);
        (_, double worstSpurDb) = ToneSpectrum(44056, 48000, 20000.0);
        Assert.True(worstSpurDb <= -75.0, $"worst spur {worstSpurDb:0.0} dB relative to the tone");
    }

    [Fact]
    public void Standard_ratios_step_exact_phase_tables()
    {
        Assert.True(new PolyphaseResampler(44100, 48000, 2).IsExact);
        Assert.Equal(160, new PolyphaseResampler(44100, 48000, 2).PhaseRows);
        Assert.Equal(147, new PolyphaseResampler(48000, 44100, 2).PhaseRows);
        Assert.Equal(147, new PolyphaseResampler(192000, 44100, 2).PhaseRows);
        Assert.Equal(1, new PolyphaseResampler(96000, 48000, 2).PhaseRows);
    }

    // ── timing: the mirrored-kernel regression (V-PE2) ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(44056, 48000)]
    [InlineData(22050, 48000)]
    [InlineData(96000, 48000)]
    [InlineData(48000, 96000)]
    public void Output_frame_n_is_the_input_tone_sampled_at_the_documented_time(int from, int to)
    {
        const int InFrames = 6000;
        int lowRate = Math.Min(from, to);
        double hzL = 0.10 * lowRate, hzR = 0.15 * lowRate;                       // well inside every passband
        float[] x = StereoTones(InFrames, from, hzL, hzR);
        var rs = new PolyphaseResampler(from, to, 2);
        float[] y = Pump(rs, x, 2, [1000, 777], 1000, flush: false);

        int s = PreRoll(from, to);
        double r = (double)from / to;
        Assert.InRange(s * r - D, -0.5 * r - 1e-9, 0.5 * r + 1e-9);               // the stream is not shifted by more than half an output frame

        int count = y.Length / 2, checkedFrames = 0;
        double worst = 0;
        for (int n = 0; n < count; n++)
        {
            double tau = (n + s) * r - D;
            if (tau < 40) continue;                                               // the zero history is still inside the kernel
            worst = Math.Max(worst, Math.Abs(y[2 * n] - ToneL(tau, from, hzL)));
            worst = Math.Max(worst, Math.Abs(y[2 * n + 1] - ToneR(tau, from, hzR)));
            checkedFrames++;
        }
        Assert.True(checkedFrames > count / 2, $"only {checkedFrames} of {count} frames were checked");
        // A mirrored kernel (the V-PE2 bug: argument k − D − frac) is up to a whole input frame early or late: ≥ 0.1 here. The windowed sinc is
        // within ~3e-5 of the tone (Kaiser β 9) plus float rounding.
        Assert.True(worst <= 2e-4, $"worst |y − tone(τ)| = {worst:0.0e+0} ({from}→{to})");
    }

    [Fact]
    public void A_linear_ramp_resamples_to_the_correctly_timed_ramp()
    {
        const int From = 44100, To = 48000, InFrames = 2000;
        var x = new float[InFrames * 2];
        for (int j = 0; j < InFrames; j++) { x[2 * j] = j / 1024f; x[2 * j + 1] = (InFrames - j) / 1024f; }   // up on the left, down on the right
        var rs = new PolyphaseResampler(From, To, 2);
        float[] y = Pump(rs, x, 2, [881], 960, flush: false);

        int s = PreRoll(From, To);
        double r = (double)From / To, worst = 0;
        int count = y.Length / 2, checkedFrames = 0;
        for (int n = 0; n < count; n++)
        {
            double tau = (n + s) * r - D;
            if (tau < 40) continue;
            worst = Math.Max(worst, Math.Abs(y[2 * n] - tau / 1024.0));
            worst = Math.Max(worst, Math.Abs(y[2 * n + 1] - (InFrames - tau) / 1024.0));
            checkedFrames++;
        }
        Assert.True(checkedFrames > 1500, $"only {checkedFrames} frames were checked");
        // The mirrored kernel is 2·frac input frames off (up to 2/1024 ≈ 2e-3 here); the right kernel is within float rounding of the ramp.
        Assert.True(worst <= 5e-5, $"worst |y − ramp(τ)| = {worst:0.0e+0}");
    }

    // ── stalls and block independence (V-PE1) ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(44056, 48000)]
    [InlineData(96000, 48000)]
    [InlineData(8000, 48000)]
    public void Every_input_frame_is_consumed_whenever_dst_has_room(int from, int to)
    {
        var rs = new PolyphaseResampler(from, to, 2);
        var rng = new Random(5);
        for (int call = 0; call < 300; call++)
        {
            int n = 1 + rng.Next(3000);
            float[] src = Noise(n, 2, call);
            var dst = new float[(rs.MaxOutFrames(n) + 1) * 2];
            ResampleResult rr = rs.Process(src, n, dst);
            Assert.Equal(n, rr.Consumed);                                         // the input ran out: all of it is taken, a refill-only-when-empty caller moves on
            Assert.True(rr.Produced <= rs.MaxOutFrames(n), $"{rr.Produced} frames from {n} exceeds MaxOutFrames {rs.MaxOutFrames(n)}");
        }
    }

    [Fact]
    public void The_production_pump_is_never_held_back()
    {
        // The decoders pull SrcFramesForOutput(want) frames into a `want`-frame destination: it never fills, so every call consumes its whole pull.
        var rs = new PolyphaseResampler(44100, 48000, 2);
        int pull = rs.SrcFramesForOutput(960);
        var dst = new float[960 * 2];
        for (int call = 0; call < 200; call++)
        {
            ResampleResult rr = rs.Process(Noise(pull, 2, call), pull, dst);
            Assert.Equal(pull, rr.Consumed);
            if (call > 0) Assert.True(rr.Produced > 0, "a steady-state pull produced nothing");
        }
    }

    public static IEnumerable<object[]> ExactPairs()
    {
        yield return new object[] { 44100, 48000 };
        yield return new object[] { 48000, 44100 };
        yield return new object[] { 96000, 48000 };
        yield return new object[] { 22050, 48000 };
        yield return new object[] { 8000, 48000 };
        yield return new object[] { 48000, 96000 };
    }

    [Theory]
    [MemberData(nameof(ExactPairs))]
    public void Output_is_bit_identical_at_every_pump_size(int from, int to)
    {
        float[] x = Noise(12000, 2, 21);
        float[] expected = OneShot(new PolyphaseResampler(from, to, 2), x, 2, flush: true);
        // (chunk sizes that cycle, destination frames): one frame at a time, the production 884/960, a destination that fills many times
        // per chunk (7, 1), uneven chunks — every one of them must be the same stream.
        var pumps = new (int[] Chunks, int DstFrames)[]
        {
            (new[] { 1 }, 480), (new[] { 480 }, 480), (new[] { 4096 }, 4096), (new[] { 4096 }, 7), (new[] { 4096 }, 1),
            (new[] { 333, 1000, 1 }, 1000), (new[] { 884 }, 960),
        };
        foreach ((int[] chunks, int dstFrames) in pumps)
        {
            float[] got = Pump(new PolyphaseResampler(from, to, 2), x, 2, chunks, dstFrames, flush: true);
            AssertIdentical(expected, got, $"{from}→{to}, chunks [{string.Join(',', chunks)}], dst {dstFrames}");
        }
    }

    [Fact]
    public void The_interpolated_ratio_matches_at_every_pump_size_to_float_rounding()
    {
        // The interpolated path accumulates its position in a double that is rebased per call, so two pump sizes round the 13th decimal of a
        // position differently: the same stream to ~1e-9, not bit for bit.
        float[] x = Noise(12000, 2, 22);
        float[] expected = OneShot(new PolyphaseResampler(44056, 48000, 2), x, 2, flush: true);
        foreach ((int chunk, int dstFrames) in new[] { (1, 480), (480, 480), (4096, 4096), (4096, 7) })
        {
            float[] got = Pump(new PolyphaseResampler(44056, 48000, 2), x, 2, new[] { chunk }, dstFrames, flush: true);
            AssertClose(expected, got, 5e-5, $"44056→48000, chunk {chunk}, dst {dstFrames}");   // the interpolated path rebases a double position per call: float rounding, ≈ −86 dBFS
        }
    }

    [Fact]
    public void A_silent_pad_pushed_through_between_two_signals_is_one_continuous_stream()
    {
        // S-7: the Ogg adapter hands the span a hole cost through the resampler as silence, in its own calls, between two decoded packets.
        float[] a = Noise(3000, 2, 1), b = Noise(3000, 2, 2);
        var all = new float[(3000 + 1500 + 3000) * 2];
        Array.Copy(a, 0, all, 0, a.Length);
        Array.Copy(b, 0, all, (3000 + 1500) * 2, b.Length);                      // the 1500 frames between them stay silent
        float[] expected = OneShot(new PolyphaseResampler(44100, 48000, 2), all, 2, flush: true);
        float[] got = Pump(new PolyphaseResampler(44100, 48000, 2), all, 2, [3000, 1024, 476, 3000], 960, flush: true);
        AssertIdentical(expected, got, "signal / silence / signal");
        Assert.Equal(StreamLength(all.Length / 2, 44100, 48000), got.Length / 2);   // the track keeps its length
    }

    [Fact]
    public void Each_channel_is_resampled_independently_of_its_neighbours()
    {
        const int Ch = 6, Frames = 5000;
        float[] x = Noise(Frames, Ch, 3);
        float[] multi = Pump(new PolyphaseResampler(44100, 48000, Ch), x, Ch, [1000], 1000, flush: true);
        for (int c = 0; c < Ch; c++)
        {
            var mono = new float[Frames];
            for (int j = 0; j < Frames; j++) mono[j] = x[j * Ch + c];
            float[] ym = Pump(new PolyphaseResampler(44100, 48000, 1), mono, 1, [1000], 1000, flush: true);
            Assert.Equal(ym.Length, multi.Length / Ch);
            for (int n = 0; n < ym.Length; n++)
                if (ym[n] != multi[n * Ch + c]) Assert.Fail($"channel {c}, frame {n}: {multi[n * Ch + c]:R} vs mono {ym[n]:R}");
        }
    }

    // ── drift ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_exact_ratio_does_not_drift_over_ten_minutes()
    {
        // 44.1 → 48 reduces to 147:160. The same pair at 1/20 of the rates (2205 → 2400) walks the SAME 160-phase integer machinery, but ten
        // minutes of it is 1.3 M frames instead of 26 M: the drift is a property of the ratio arithmetic, not of the absolute rate.
        const int From = 2205, To = 2400, Seconds = 600, Chunk = 4096;
        const long InTotal = (long)From * Seconds;                                  // 1,323,000 input frames
        const double Hz = 0.02 * From;                                             // 44.1 Hz at this scale: a slow tone, so a timing error shows as amplitude
        var rs = new PolyphaseResampler(From, To, 1);
        Assert.True(rs.IsExact);
        Assert.Equal(160, rs.PhaseRows);

        int s = PreRoll(From, To);
        double r = (double)From / To, worst = 0;
        long produced = 0;
        var src = new float[Chunk];
        var dst = new float[rs.MaxOutFrames(Chunk) + 8];
        for (long j0 = 0; j0 < InTotal; j0 += Chunk)
        {
            int n = (int)Math.Min(Chunk, InTotal - j0);
            for (int i = 0; i < n; i++) src[i] = (float)ToneL(j0 + i, From, Hz);
            ResampleResult rr = rs.Process(src, n, dst);
            Assert.Equal(n, rr.Consumed);
            for (int i = 0; i < rr.Produced; i++, produced++)
            {
                if (produced < 64) continue;
                double tau = (produced + s) * r - D;
                worst = Math.Max(worst, Math.Abs(dst[i] - ToneL(tau, From, Hz)));
            }
        }
        Assert.True(worst <= 2e-4, $"after 10 minutes the output is off the tone by {worst:0.0e+0}");

        var tail = new List<float>();
        FlushInto(rs, 1, 256, tail);
        long total = produced + tail.Count;
        Assert.Equal(1_440_000L, StreamLength(InTotal, From, To));                  // 147:160 is exact: 1,323,000 × 160/147
        Assert.Equal(1_440_000L, total);                                            // not one frame gained or lost in ten minutes
    }

    // ── flush: the trailing half kernel (V-PE21, V-PE36) ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100, 48000, true)]
    [InlineData(48000, 44100, true)]
    [InlineData(8000, 48000, true)]
    [InlineData(96000, 48000, true)]
    [InlineData(22050, 48000, true)]
    [InlineData(44056, 48000, false)]
    public void Flush_is_the_zero_extended_stream_trimmed_to_floor_in_times_ratio(int from, int to, bool exact)
    {
        foreach (int n in new[] { 0, 1, 2, 5, 31, 32, 33, 63, 64, 65, 500, 1000, 1001, 4097 })
        {
            float[] x = Noise(n, 2, n + 11);
            float[] got = Pump(new PolyphaseResampler(from, to, 2), x, 2, [4096], 4096, flush: true);
            long want = StreamLength(n, from, to);
            Assert.True(want == got.Length / 2, $"{from}→{to}, {n} frames in: {got.Length / 2} out, expected exactly {want}");

            // The oracle: the same stream with enough real zeros appended, cut to the same length.
            var padded = new float[(n + 256) * 2];
            Array.Copy(x, padded, n * 2);
            float[] extended = OneShot(new PolyphaseResampler(from, to, 2), padded, 2, flush: false);
            Assert.True(extended.Length / 2 >= want, $"{n} frames: the zero-extended stream has only {extended.Length / 2} frames");
            float[] expected = extended.AsSpan(0, (int)want * 2).ToArray();
            if (exact) AssertIdentical(expected, got, $"{from}→{to}, {n} frames");
            else AssertClose(expected, got, 1e-5, $"{from}→{to}, {n} frames");
        }
    }

    [Fact]
    public void Flush_hands_out_the_same_tail_in_any_destination_size()
    {
        float[] x = Noise(1234, 2, 8);
        float[] expected = Pump(new PolyphaseResampler(44100, 48000, 2), x, 2, [4096], 4096, flush: true);
        foreach (int dstFrames in new[] { 1, 3, 7, 33, 480 })
        {
            float[] got = Pump(new PolyphaseResampler(44100, 48000, 2), x, 2, [4096], dstFrames, flush: true);
            AssertIdentical(expected, got, $"Flush into {dstFrames}-frame destinations");
        }
        Assert.Equal(StreamLength(1234, 44100, 48000), expected.Length / 2);
    }

    [Fact]
    public void Flush_runs_dry_and_Reset_restarts_the_stream_and_rearms_it()
    {
        float[] x = Noise(3000, 2, 6);
        var rs = new PolyphaseResampler(44100, 48000, 2);
        float[] first = Pump(rs, x, 2, [881], 960, flush: true);
        var spare = new float[64];
        Assert.Equal(0, rs.Flush(spare));                                         // the tail is spent: 0 is the end, and it stays 0

        rs.Reset();
        float[] second = Pump(rs, x, 2, [881], 960, flush: true);
        AssertIdentical(first, second, "the stream after Reset");                 // pre-roll owed again, history silent, flush re-armed
    }

    // ── bookkeeping ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(96000, 48000)]
    [InlineData(22050, 48000)]
    public void SrcFramesForOutput_keeps_the_output_inside_the_destination(int from, int to)
    {
        foreach (int want in new[] { 16, 100, 480, 960, 4096 })
        {
            var rs = new PolyphaseResampler(from, to, 2);
            int n = rs.SrcFramesForOutput(want);
            Assert.True(rs.MaxOutFrames(n) <= want, $"SrcFramesForOutput({want}) = {n}, MaxOutFrames = {rs.MaxOutFrames(n)}");
            var dst = new float[want * 2];
            ResampleResult rr = rs.Process(Noise(n, 2, want), n, dst);
            Assert.Equal(n, rr.Consumed);
            Assert.True(rr.Produced <= want);
        }
    }

    [Fact]
    public void Equal_rates_are_a_pass_through_and_degenerate_rates_do_not_throw()
    {
        var rs = new PolyphaseResampler(48000, 48000, 2);
        Assert.False(rs.IsActive);
        Assert.Equal(0, rs.LatencySamples);
        Assert.Equal(0, rs.PhaseRows);
        float[] x = Noise(64, 2, 9);
        var dst = new float[64 * 2];
        ResampleResult rr = rs.Process(x, 64, dst);
        Assert.Equal(64, rr.Produced);
        Assert.Equal(64, rr.Consumed);
        AssertIdentical(x, dst, "pass-through");
        Assert.Equal(0, rs.Flush(dst));
        Assert.Equal(7, rs.SrcFramesForOutput(7));
        Assert.Equal(7, rs.MaxOutFrames(7));

        Assert.False(new PolyphaseResampler(0, 48000, 2).IsActive);
        Assert.False(new PolyphaseResampler(44100, 0, 2).IsActive);
    }

    [Fact]
    public void An_active_resampler_reports_no_latency_because_the_group_delay_is_absorbed()
    {
        var rs = new PolyphaseResampler(44100, 48000, 2);
        Assert.True(rs.IsActive);
        Assert.Equal(0, rs.LatencySamples);
    }

    [Fact]
    public void Process_allocates_nothing_in_steady_state()
    {
        var rs = new PolyphaseResampler(44100, 48000, 2);
        int pull = rs.SrcFramesForOutput(960);
        float[] src = Noise(pull, 2, 4);
        var dst = new float[960 * 2];
        for (int i = 0; i < 200; i++) rs.Process(src, pull, dst);                // warm: JIT, the pre-roll, the first history rebuild
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int produced = 0;
        for (int i = 0; i < 2000; i++) produced += rs.Process(src, pull, dst).Produced;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(produced > 0);
    }
}
