using System;
using System.Reflection;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E-1 / E-2 (Wavee playback smoothness, plan §4.7 "double biquad", V-PE31): the RBJ biquad runs in DOUBLE — coefficients
/// <see cref="BiquadCoeffs"/>(double B0, B1, B2, A1, A2) and state <see cref="BiquadState"/> (double x1/x2/y1/y2) — and flushes
/// a decaying tail to exact zero BEFORE it is stored as state.
///
/// WHY. A 31 Hz band at 48 kHz has its poles at r ≈ 0.999. A float state quantises the pole radius's recursion and amplifies the
/// round-off of every sum by ~1/(1 − r) (the "−120 dB noise floor" of the plan); float coefficients cannot even hold the pole
/// radius (1 − r ≈ 1e-3 needs more than the 24 bits a float has left of 1.0). .NET sets no FTZ/DAZ, so a recursion that decays
/// into the subnormal range on digital silence is slow on the RT thread — and flushing only the RETURNED sample would leave the
/// stored y1 subnormal, so the next block's recursion would stay in that range.
///
/// Everything here is deterministic and device-free: synthetic noise from a fixed seed, an INDEPENDENT double reference
/// implementation inside the test (so a float state or a float-quantised design shows up as an error against it), and no clock.
/// </summary>
public sealed class BiquadDoubleStateTests
{
    private const int Rate = 48000;

    /// <summary>The flush threshold <c>BiquadState.Process</c> applies (V-PE31): |y| below it is stored and returned as 0.</summary>
    private const double FlushBelow = 1e-25;

    // ── an INDEPENDENT RBJ peaking design in double (the plan's formula, not a call into BiquadCoeffs.Design) ──────────────

    private static BiquadCoeffs ReferencePeaking(double freqHz, double q, double gainDb, int sampleRate)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * freqHz / sampleRate;
        double cw = Math.Cos(w0), alpha = Math.Sin(w0) / (2.0 * q);
        double b0 = 1 + alpha * a, b1 = -2 * cw, b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a, a1 = -2 * cw, a2 = 1 - alpha / a;
        return new BiquadCoeffs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    // ── E-1: double coefficients ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(31f, 1f, 12f)]
    [InlineData(31f, 1f, -12f)]
    [InlineData(62f, 4f, 12f)]
    public void Peaking_coefficients_hold_a_low_frequency_pole_radius_to_double_precision(float freq, float q, float gainDb)
    {
        BiquadCoeffs got = BiquadCoeffs.Design(new BiquadBand(BiquadType.Peaking, freq, q, gainDb), Rate);
        BiquadCoeffs want = ReferencePeaking(freq, q, gainDb, Rate);

        // 1e-12 is five orders tighter than a float can hold (~6e-8 near 1.0): a float-quantised Design fails every line.
        Assert.Equal(want.B0, got.B0, 12);
        Assert.Equal(want.B1, got.B1, 12);
        Assert.Equal(want.B2, got.B2, 12);
        Assert.Equal(want.A1, got.A1, 12);
        Assert.Equal(want.A2, got.A2, 12);
        // The pole radius itself (sqrt of the product of the poles = sqrt(a2)) is the quantity a float would have rounded.
        Assert.Equal(Math.Sqrt(want.A2), Math.Sqrt(got.A2), 12);
        Assert.True(Math.Sqrt(got.A2) > 0.995, "a 31/62 Hz band at 48 kHz really is within 0.5 % of the unit circle (the premise of E-1)");
    }

    // ── E-1: double state — the noise floor ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1f)]   // the equalizer's default Q (EqBand.Q = 1)
    [InlineData(4f)]   // a narrower band: poles closer to the circle, round-off amplified ~2×
    public void A_31_Hz_boost_on_minus_60_dBFS_noise_stays_within_minus_120_dB_of_a_double_reference(float q)
    {
        var band = new BiquadBand(BiquadType.Peaking, 31f, q, 12f);
        BiquadCoeffs c = BiquadCoeffs.Design(band, Rate);
        BiquadCoeffs r = ReferencePeaking(31.0, q, 12.0, Rate);

        var state = new BiquadState();
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;              // the reference's own double Direct-Form-I state
        double errEnergy = 0, refEnergy = 0;

        var rng = new Random(20261002);
        double amp = Math.Pow(10.0, -60.0 / 20.0) * Math.Sqrt(3.0);   // uniform ±amp has RMS amp/√3 = −60 dBFS
        const long total = 60L * Rate;                                // 60 s, 2.88 M samples
        for (long n = 0; n < total; n++)
        {
            float x = (float)((rng.NextDouble() * 2.0 - 1.0) * amp);
            float got = state.Process(x, in c);

            double y = r.B0 * x + r.B1 * x1 + r.B2 * x2 - r.A1 * y1 - r.A2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;

            double e = got - y;
            errEnergy += e * e;
            refEnergy += y * y;
        }

        double relativeDb = 10.0 * Math.Log10(errEnergy / refEnergy);
        // The error of a double-state implementation is the final (float) cast — ≈ −150 dB. A float state amplifies its round-off by
        // the pole radius's 1/(1 − r) and lands within a few dB of this threshold or above it.
        Assert.True(relativeDb <= -120.0, $"noise floor {relativeDb:F1} dB relative to the double reference (limit −120 dB)");
    }

    [Fact]
    public void The_delay_line_is_held_in_double()
    {
        // The state is private by design (a POD the RT path copies); its PRECISION is the whole of E-1, so it is pinned by type.
        foreach (string name in new[] { "_x1", "_x2", "_y1", "_y2" })
        {
            FieldInfo? f = typeof(BiquadState).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            Assert.Equal(typeof(double), f!.FieldType);
        }
    }

    // ── E-2: the decaying tail is flushed BEFORE it is stored ────────────────────────────────────────────────────────────

    [Fact]
    public void A_decaying_tail_is_flushed_to_exact_zero_and_never_goes_subnormal()
    {
        // A 31 Hz low-pass (poles at r ≈ 0.997): one impulse, then digital silence. The tail decays through 1e-25 after ~20 k
        // samples; WITHOUT the flush the float output would pass through the subnormal range (≈ 1e-38 … 1e-45) a few thousand
        // samples later, and the double recursion would sit in ITS subnormal range (≈ 1e-308) for ~14 k samples after that.
        BiquadCoeffs c = BiquadCoeffs.Design(new BiquadBand(BiquadType.LowPass, 31f, 0.7071f, 0f), Rate);
        var state = new BiquadState();

        const int silence = 10 * Rate;                         // 10 s ≫ the flush point
        const int settledBy = 60_000;                          // well past ~20 k: every later output must be exactly 0
        Assert.True(state.Process(1f, in c) != 0f);            // the impulse does come out

        int nonZero = 0;
        float smallestNonZero = float.MaxValue;
        for (int n = 1; n < silence; n++)
        {
            float y = state.Process(0f, in c);
            if (float.IsSubnormal(y)) Assert.Fail($"sample {n} is subnormal: the tail was not flushed");
            if (y != 0f) { nonZero++; smallestNonZero = Math.Min(smallestNonZero, Math.Abs(y)); }
            if (n >= settledBy && y != 0f) Assert.Fail($"sample {n} is {y}: the tail must be exactly zero by now");
        }

        Assert.True(nonZero > 1_000, "the tail rings for thousands of samples before it is flushed");
        // Every non-zero output is at or above the flush threshold (a float of 1e-25 rounds to within a ULP of it).
        Assert.True(smallestNonZero >= (float)(FlushBelow * 0.99), $"a non-zero output of {smallestNonZero} survived below the flush threshold");
    }

    [Fact]
    public void The_flush_zeroes_the_stored_feedback_state_not_only_the_returned_sample()
    {
        BiquadCoeffs c = BiquadCoeffs.Design(new BiquadBand(BiquadType.LowPass, 31f, 0.7071f, 0f), Rate);
        var state = new BiquadState();
        state.Process(1f, in c);
        for (int n = 0; n < 60_000; n++) state.Process(0f, in c);       // far past the flush point

        object boxed = state;
        double y1 = (double)typeof(BiquadState).GetField("_y1", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxed)!;
        double y2 = (double)typeof(BiquadState).GetField("_y2", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxed)!;
        // V-PE31: flushing only the return value would leave _y1/_y2 subnormal-but-nonzero, and the NEXT block's recursion would
        // keep running in the subnormal range. Flushed before the store, the state is exactly zero.
        Assert.Equal(0.0, y1);
        Assert.Equal(0.0, y2);

        // …and a flushed filter is indistinguishable from a fresh one: the same impulse produces the same response, bit for bit.
        var fresh = new BiquadState();
        for (int n = 0; n < 256; n++)
        {
            float x = n == 0 ? 0.5f : 0f;
            Assert.Equal(fresh.Process(x, in c), state.Process(x, in c));
        }
    }

    [Fact]
    public void An_audible_signal_is_never_flushed()
    {
        // The flush is for the sub-1e-25 tail only: a −120 dBFS tone (1e-6) passes through a unity-gain filter untouched.
        BiquadCoeffs c = BiquadCoeffs.Design(new BiquadBand(BiquadType.Peaking, 1000f, 1f, 0f), Rate);   // identity
        var state = new BiquadState();
        for (int n = 0; n < 4_800; n++)
        {
            float x = 1e-6f * MathF.Sin(2f * MathF.PI * 440f * n / Rate);
            float y = state.Process(x, in c);
            Assert.Equal(x, y, 9);
        }
    }
}
