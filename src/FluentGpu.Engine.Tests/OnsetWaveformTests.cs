using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The visualizer's beat and wave signals: <see cref="OnsetDetector"/> (flux + adaptive-threshold onsets) and the
/// waveform half of the spectrum publish (<see cref="AudioEffects.CopyWaveform"/>).</summary>
public sealed class OnsetWaveformTests
{
    static float[] Frame(float db)
    {
        var f = new float[SpectrumAnalyzer.DefaultBandCount];
        Array.Fill(f, db);
        return f;
    }

    [Fact]
    public void A_steady_tone_has_no_flux_and_no_onset()
    {
        var d = new OnsetDetector(SpectrumAnalyzer.DefaultBandCount);
        var tone = Frame(-30f);
        for (int i = 0; i < 200; i++)
        {
            Assert.False(d.Step(tone, i * 15.0, out float flux, out _));
            Assert.Equal(0f, flux);
        }
    }

    [Fact]
    public void A_step_up_after_a_quiet_baseline_is_one_onset_and_the_refractory_holds()
    {
        var d = new OnsetDetector(SpectrumAnalyzer.DefaultBandCount);
        var quiet = Frame(-60f);
        double t = 0;
        for (int i = 0; i < 40; i++, t += 15) Assert.False(d.Step(quiet, t, out _, out _));

        Assert.True(d.Step(Frame(-20f), t, out float flux, out float strength));   // a 40 dB rise in every band
        Assert.Equal(40f, flux, 3);
        Assert.InRange(strength, 0.01f, 1f);

        // 15 ms later an even bigger rise is still inside the 100 ms refractory window
        Assert.False(d.Step(Frame(-60f), t + 5, out _, out _));
        Assert.False(d.Step(Frame(0f), t + 15, out _, out _));
    }

    [Fact]
    public void Flux_counts_rises_only_and_warmup_blocks_early_onsets()
    {
        var d = new OnsetDetector(4);
        Assert.False(d.Step([0f, 0f, 0f, 0f], 0, out float f0, out _));      // primes, no flux
        Assert.Equal(0f, f0);
        Assert.False(d.Step([8f, -8f, 4f, -4f], 15, out float f1, out _));   // falls are ignored: (8 + 4) / 4
        Assert.Equal(3f, f1, 4);
        d.Reset();
        Assert.False(d.Step([40f, 40f, 40f, 40f], 30, out float f2, out _)); // after a reset the first frame only primes
        Assert.Equal(0f, f2);
    }

    [Fact]
    public void The_waveform_rides_the_band_publish_under_the_same_lease_and_sequence()
    {
        var effects = new AudioEffects();
        long source = effects.BindVisualizerSource();
        var wave = new float[AudioEffects.WaveformSamples];
        for (int i = 0; i < wave.Length; i++) wave[i] = MathF.Sin(i * 0.05f);
        var bands = Frame(-30f);
        var dst = new float[AudioEffects.WaveformSamples];

        Assert.Equal(0, effects.CopyWaveform(dst, out var none));             // no lease
        Assert.False(none.Live);

        using (var lease = effects.AcquireSpectrum())
        {
            long epoch = effects.SpectrumDemand(source);
            Assert.Equal(0, effects.CopyWaveform(dst, out _));                // leased, nothing published yet
            Assert.True(effects.PublishSpectrum(source, epoch, bands, false, 0.2f, 0, 0f, wave, 48000, flux: 3.5f, onset: true, onsetStrength: 0.6f));
            Assert.Equal(AudioEffects.WaveformSamples, effects.CopyWaveform(dst, out var wi));
            Assert.Equal(wave, dst);
            Assert.Equal(48000, wi.SampleRate);
            effects.CopySpectrum(new float[bands.Length], out var si);
            Assert.Equal(si.Sequence, wi.Sequence);
            Assert.Equal(3.5f, si.Flux);
            Assert.Equal(1, si.OnsetSequence);
            Assert.Equal(0.6f, si.OnsetStrength);

            // a publish without an onset keeps the onset sequence
            Assert.True(effects.PublishSpectrum(source, epoch, bands, false, 0.2f, 0, 0f, wave, 48000, flux: 0.1f));
            effects.CopySpectrum(new float[bands.Length], out var si2);
            Assert.Equal(1, si2.OnsetSequence);
        }
        Assert.Equal(0, effects.CopyWaveform(dst, out _));                    // the lease ended: nothing to read
        Assert.Equal(0, NullAudioEffects.Instance.CopyWaveform(dst, out _));
    }
}
