using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The visualizer's FFT: calibration (Parseval, the +3 dB/oct tilt, the [−80, +6] clamp), band layout, determinism and the
/// zero-allocation steady state. Pure math over <see cref="SpectrumAnalyzer"/> — no engine, no audio thread.
/// <para>Two facts shape which frequencies the calibration tests use. (1) A 2048-point FFT at 48 kHz has 23.4 Hz bins, so the
/// lowest ~20 of the 48 log bands are ONE bin wide: a sine there reads its level minus the Hann scalloping loss
/// (−1.76 dB dead-centre on a bin, −3.2 dB half-way between two), not 0 dB. (2) The +3 dB/oct tilt pushes a full-scale
/// sine above ≈ 160 Hz into the +6 dB ceiling. So the absolute calibration is asserted at the centre of a WIDE band (the
/// Hann lobe, ±2 bins, lies wholly inside it) at −30 dBFS, and the single-bin bands get an analytically bounded check.</para>
/// </summary>
public sealed class SpectrumAnalyzerTests
{
    private const int N = SpectrumAnalyzer.DefaultFftSize;

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static float[] Sine(int rate, double hz, double amplitude)
    {
        var x = new float[N];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * hz * i / rate));
        return x;
    }

    private static float[] Analyze(SpectrumAnalyzer an, float[] window)
    {
        var db = new float[an.BandCount];
        an.Analyze(window, db);
        return db;
    }

    private static int ArgMax(float[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
        return best;
    }

    private static double DbToAmplitude(double dbfs) => Math.Pow(10.0, dbfs / 20.0);

    /// <summary>The pink tilt at <paramref name="hz"/>: <c>3·log2(hz/40)</c>.</summary>
    private static double TiltAt(double hz) => SpectrumAnalyzer.TiltDbPerOctave * Math.Log2(hz / SpectrumAnalyzer.MinHz);

    /// <summary>The nominal log-centre of band <paramref name="b"/> (<c>sqrt(edge(b)·edge(b+1))</c>, the frequency the
    /// analyzer evaluates the tilt at). Equal to <see cref="SpectrumAnalyzer.BandCenterHz"/> in the wide bands; BELOW it in
    /// the single-bin bands, whose bin range was pushed up past the nominal edges.</summary>
    private static double NominalCenterHz(SpectrumAnalyzer an, int b) =>
        SpectrumAnalyzer.MinHz * Math.Pow((double)an.MaxHz / SpectrumAnalyzer.MinHz, (b + 0.5) / an.BandCount);

    // ── a sine peaks in its own band ───────────────────────────────────────────────────────────────────────────────

    // The sine is −40 dBFS so that no band reaches the +6 dB ceiling: two clamped bands would tie, and argmax would then
    // depend on enumeration order instead of on the analysis.
    private static void AssertSinePeaksInItsBand(int rate, double hz, double isolationDb)
    {
        var an = new SpectrumAnalyzer(rate);
        float[] db = Analyze(an, Sine(rate, hz, DbToAmplitude(-40.0)));
        int expected = an.BandOf((float)hz);
        Assert.True(expected >= 0, "the test frequency must lie inside a band");
        Assert.Equal(expected, ArgMax(db));
        float peak = db[expected];
        Assert.True(peak < SpectrumAnalyzer.CeilingDb, "the peak must be unclamped");
        for (int b = 0; b < db.Length; b++)
        {
            if (Math.Abs(b - expected) <= 2) continue;
            Assert.True(db[b] <= peak - isolationDb,
                $"band {b} reads {db[b]:F1} dB, only {peak - db[b]:F1} dB below the peak ({peak:F1} dB, band {expected}) at {rate} Hz");
        }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void A_1kHz_sine_peaks_in_its_band(int rate) => AssertSinePeaksInItsBand(rate, 1000.0, 30.0);

    // 46.9 Hz bins make every band below ≈ 1.3 kHz a single bin wide, so the sine leaks into its neighbours more.
    [Fact]
    public void A_1kHz_sine_peaks_in_its_band_at_96k() => AssertSinePeaksInItsBand(96000, 1000.0, 25.0);

    // ── calibration ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void A_minus30dBFS_sine_at_a_wide_bands_centre_reads_minus30_plus_the_tilt(int rate)
    {
        // Band 30 (≈ 1.8 kHz) is 5 to 10 bins wide at these rates: the Hann lobe (±2 bins) sits inside it, so by Parseval
        // the band reads the sine's own level (0 dB for a full-scale sine) before the tilt — and −30 dBFS stays well under
        // the +6 ceiling (−30 + 16.5 ≈ −13.5 dB).
        const int band = 30;
        var an = new SpectrumAnalyzer(rate);
        float fc = an.BandCenterHz(band);
        Assert.Equal(band, an.BandOf(fc));
        float[] db = Analyze(an, Sine(rate, fc, DbToAmplitude(-30.0)));
        double expected = -30.0 + TiltAt(fc);
        Assert.InRange((double)db[band], expected - 1.0, expected + 1.0);
    }

    [Fact]
    public void The_reading_follows_the_amplitude_at_6dB_per_doubling()
    {
        var an = new SpectrumAnalyzer(48000);
        const int band = 30;
        float fc = an.BandCenterHz(band);
        float quiet = Analyze(an, Sine(48000, fc, 0.01))[band];
        float loud = Analyze(an, Sine(48000, fc, 0.02))[band];
        Assert.InRange((double)(loud - quiet), 6.02 - 0.05, 6.02 + 0.05);
    }

    // At 48 kHz bands 0..~20 are one 23.4 Hz bin wide (bin 0 — DC — is in no band). A sine's peak band then holds ONE bin of
    // its Hann lobe: (2/3)·W(δ)², i.e. −1.76 dB (on the bin) … −3.2 dB (half-way), a touch more when the argmax lands on
    // the farther neighbour because the tilt favours it. The tilt itself is bounded by the analyzer's nominal log-centre
    // and by BandCenterHz (the bin range's centre) — the two differ by up to ≈ 2.9 dB in these bands, so the check accepts
    // either. At 80 Hz / 60 Hz the readings are ≈ −32 dB / ≈ −2 dB; the +6 ceiling and the floor are nowhere near.
    [Theory]
    [InlineData(80.0, -30.0)]
    [InlineData(60.0, 0.0)]
    public void A_sine_in_the_one_bin_bands_reads_its_level_minus_the_scalloping_plus_the_tilt(double hz, double levelDb)
    {
        const int rate = 48000;
        var an = new SpectrumAnalyzer(rate);
        float[] db = Analyze(an, Sine(rate, hz, DbToAmplitude(levelDb)));
        int bandOfHz = an.BandOf((float)hz);
        Assert.True(bandOfHz >= 0);
        int peak = ArgMax(db);
        Assert.InRange(peak, bandOfHz - 1, bandOfHz + 1);

        double tiltNominal = TiltAt(NominalCenterHz(an, peak));
        double tiltCentre = TiltAt(an.BandCenterHz(peak));
        double lo = levelDb + Math.Min(tiltNominal, tiltCentre) - 4.0;
        double hi = levelDb + Math.Max(tiltNominal, tiltCentre) - 1.5;
        Assert.InRange((double)db[peak], lo, hi);
        Assert.True(db[peak] < SpectrumAnalyzer.CeilingDb);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Silence_clamps_to_the_floor(int rate)
    {
        var an = new SpectrumAnalyzer(rate);
        float[] db = Analyze(an, new float[N]);
        Assert.Equal(an.BandCount, db.Length);
        Assert.All(db, v => Assert.Equal(SpectrumAnalyzer.FloorDb, v));
    }

    [Fact]
    public void A_full_scale_square_wave_never_leaves_the_clamp_range()
    {
        var an = new SpectrumAnalyzer(48000);
        var x = new float[N];
        for (int i = 0; i < x.Length; i++) x[i] = (i / 24) % 2 == 0 ? 1f : -1f;   // 1 kHz, full scale, every harmonic
        float[] db = Analyze(an, x);
        Assert.All(db, v =>
        {
            Assert.True(v >= SpectrumAnalyzer.FloorDb, $"{v} below the floor");
            Assert.True(v <= SpectrumAnalyzer.CeilingDb, $"{v} above the ceiling");
        });
        Assert.Equal(SpectrumAnalyzer.CeilingDb, db[an.BandOf(1000f)]);   // +16 dB of tilt on a full-scale tone: clamped
    }

    // ── band layout ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Band_edges_are_monotone_and_cover_the_range(int rate)
    {
        var an = new SpectrumAnalyzer(rate);
        float prev = 0f;
        for (int b = 0; b < an.BandCount; b++)
        {
            float c = an.BandCenterHz(b);
            Assert.True(c > prev, $"BandCenterHz({b}) = {c} must exceed {prev}");
            Assert.Equal(b, an.BandOf(c));   // every band is reachable, and its centre lies in it
            prev = c;
        }
        Assert.True(an.BandCenterHz(0) >= SpectrumAnalyzer.MinHz);
        Assert.True(an.BandCenterHz(an.BandCount - 1) <= an.MaxHz);

        int last = -1;
        for (float hz = SpectrumAnalyzer.MinHz; hz < an.MaxHz; hz += 1f)
        {
            int b = an.BandOf(hz);
            if (b < 0) continue;   // a bin in the gap between two bands (or the top edge's rounding)
            Assert.True(b >= last, $"BandOf({hz}) = {b} went backwards from {last}");
            last = b;
        }
        Assert.Equal(an.BandCount - 1, last);   // the sweep reaches the top band
    }

    [Fact]
    public void Frequencies_outside_every_band_report_minus_one()
    {
        var an = new SpectrumAnalyzer(48000);
        Assert.Equal(-1, an.BandOf(0f));                  // DC is in no band
        Assert.Equal(-1, an.BandOf(10f));
        Assert.Equal(-1, an.BandOf(23_000f));             // above MaxHz
    }

    [Fact]
    public void MaxHz_is_the_cap_or_the_nyquist_fraction()
    {
        Assert.Equal(SpectrumAnalyzer.MaxHzCap, new SpectrumAnalyzer(48000).MaxHz);
        Assert.Equal(SpectrumAnalyzer.MaxHzCap, new SpectrumAnalyzer(44100).MaxHz);
        Assert.InRange((double)new SpectrumAnalyzer(32000).MaxHz, 14399.9, 14400.1);   // 0.45 · 32 kHz
    }

    [Fact]
    public void Properties_report_the_constructor_arguments()
    {
        var def = new SpectrumAnalyzer(48000);
        Assert.Equal(2048, def.FftSize);
        Assert.Equal(48, def.BandCount);
        Assert.Equal(48000, def.SampleRate);
        Assert.Equal(SpectrumAnalyzer.DefaultFftSize, def.FftSize);
        Assert.Equal(SpectrumAnalyzer.DefaultBandCount, def.BandCount);

        var custom = new SpectrumAnalyzer(44100, 1024, 32);
        Assert.Equal(1024, custom.FftSize);
        Assert.Equal(32, custom.BandCount);
        float[] db = new float[32];
        custom.Analyze(new float[1024], db);
        Assert.All(db, v => Assert.Equal(SpectrumAnalyzer.FloorDb, v));
    }

    // ── contract ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_rejects_a_non_power_of_two_and_too_many_bands()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(48000, 1000));           // not a power of two
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(48000, 8));              // below the 16 minimum
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(48000, 2048, 513));      // more than fftSize / 4
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(48000, 2048, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumAnalyzer(-44100));
    }

    [Fact]
    public void Analyze_rejects_a_wrong_window_length_or_a_short_output()
    {
        var an = new SpectrumAnalyzer(48000);
        Assert.Throws<ArgumentException>(() => an.Analyze(new float[N - 1], new float[48]));
        Assert.Throws<ArgumentException>(() => an.Analyze(new float[N + 1], new float[48]));
        Assert.Throws<ArgumentException>(() => an.Analyze(new float[N], new float[47]));
        an.Analyze(new float[N], new float[64]);   // a longer output is fine: only BandCount slots are written
    }

    [Fact]
    public void Analysis_is_stateless_between_calls_and_leaves_the_input_alone()
    {
        var an = new SpectrumAnalyzer(48000);
        float[] loud = Sine(48000, 1000.0, 0.5);
        float[] copy = (float[])loud.Clone();
        float[] first = Analyze(an, loud);
        Assert.Equal(copy, loud);   // the input window is never written

        float[] silent = Analyze(an, new float[N]);   // nothing of the loud window survives in the scratch buffers
        Assert.All(silent, v => Assert.Equal(SpectrumAnalyzer.FloorDb, v));

        float[] again = Analyze(an, loud);
        for (int i = 0; i < first.Length; i++)
            Assert.True(Math.Abs(first[i] - again[i]) <= 1e-3f, $"band {i}: {first[i]} vs {again[i]}");
    }

    [Fact]
    public void Analyze_steady_state_allocates_nothing()
    {
        var an = new SpectrumAnalyzer(48000);
        float[] window = Sine(48000, 1000.0, 0.3);
        float[] bands = new float[an.BandCount];
        for (int i = 0; i < 100; i++) an.Analyze(window, bands);   // warm
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) an.Analyze(window, bands);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
