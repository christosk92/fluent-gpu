using System;
using System.Linq;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio.Host.Dsp;
using Xunit;

namespace Wavee.Tests.Audio;

public class AudioDspTests
{
    [Fact]
    public void Equalizer_Flat_IsIdentity()
    {
        var samples = new float[512];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(i * 0.037f) * 0.4f;
        var expected = samples.ToArray();

        var eq = new EqualizerProcessor();
        eq.Configure(new EqualizerSettings { Enabled = true, GainsDb = new float[10] });
        eq.Process(samples, sampleRate: 48_000, channels: 2);

        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(expected[i], samples[i], precision: 7);
    }

    [Fact]
    public void Equalizer_ExtremeGains_DoNotProduceInvalidSamples()
    {
        var samples = new float[4096];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(i * 0.071f) * 0.5f;

        var eq = new EqualizerProcessor();
        eq.Configure(new EqualizerSettings
        {
            Enabled = true,
            PreampDb = 12f,
            GainsDb = [-12f, 12f, -12f, 12f, -12f, 12f, -12f, 12f, -12f, 12f],
        });
        eq.Process(samples, sampleRate: 48_000, channels: 2);

        Assert.All(samples, sample => Assert.True(float.IsFinite(sample), "EQ output must stay finite"));
    }

    [Fact]
    public void Crossfade_EqualPower_MixesEndpointsAndMidpoint()
    {
        var outgoing = Enumerable.Repeat(1f, 8).ToArray();
        var incoming = Enumerable.Repeat(2f, 8).ToArray();
        var dst = new float[8];

        CrossfadeMixer.MixEqualPower(outgoing, incoming, dst, 0f);
        Assert.All(dst, sample => Assert.Equal(1f, sample, precision: 7));

        CrossfadeMixer.MixEqualPower(outgoing, incoming, dst, 1f);
        Assert.All(dst, sample => Assert.Equal(2f, sample, precision: 7));

        Array.Clear(incoming);
        CrossfadeMixer.MixEqualPower(outgoing, incoming, dst, 0.5f);
        Assert.All(dst, sample => Assert.Equal(MathF.Sqrt(0.5f), sample, precision: 6));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.1f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1.0f)]
    public void Crossfade_EqualPower_GainsPreserveUnitPower(float progress)
    {
        var (outgoing, incoming) = CrossfadeMixer.EqualPowerGains(progress);
        Assert.Equal(1f, outgoing * outgoing + incoming * incoming, precision: 6);
    }

    [Fact]
    public void Crossfade_PerSample_EndpointGains_AndExactFadeLength()
    {
        const int channels = 2;
        const long fadeFrames = 4;
        // 5 frames (10 interleaved samples): frames 0..3 fade, frame 4 (== fadeFrames) is fully incoming.
        var outgoing = Enumerable.Repeat(1f, 5 * channels).ToArray();
        var incoming = Enumerable.Repeat(2f, 5 * channels).ToArray();
        var dst = new float[5 * channels];

        CrossfadeMixer.MixEqualPower(outgoing, incoming, dst, startFrame: 0, fadeFrames, channels);

        // Frame 0 (p=0): pure outgoing on both channels.
        Assert.Equal(1f, dst[0], precision: 6);
        Assert.Equal(1f, dst[1], precision: 6);
        // Frame 4 (p=1, exactly fadeFrames): pure incoming — the fade completes at exactly fadeFrames.
        Assert.Equal(2f, dst[8], precision: 6);
        Assert.Equal(2f, dst[9], precision: 6);
        // Both channels of a frame share identical gains (frame-quantized progress).
        for (int f = 0; f < 5; f++)
            Assert.Equal(dst[f * channels], dst[f * channels + 1], precision: 7);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    [InlineData(4L)]
    public void Crossfade_PerSample_PreservesUnitPower(long startFrame)
    {
        const int channels = 2;
        const long fadeFrames = 4;
        // A single frame of unit-amplitude sources; outgoing^2 + incoming^2 must equal 1 (equal-power).
        var outgoing = new[] { 1f, 1f };
        var incoming = new[] { 1f, 1f };
        var dst = new float[channels];

        CrossfadeMixer.MixEqualPower(outgoing, incoming, dst, startFrame, fadeFrames, channels);

        long frame = Math.Min(startFrame, fadeFrames);
        float p = (float)(frame / (double)fadeFrames);
        float expectedOut = MathF.Cos(p * MathF.PI * 0.5f);
        float expectedIn = MathF.Sin(p * MathF.PI * 0.5f);
        Assert.Equal(expectedOut + expectedIn, dst[0], precision: 6);
        Assert.Equal(1f, expectedOut * expectedOut + expectedIn * expectedIn, precision: 6);
    }

    [Fact]
    public void Limiter_ClampsAndClearsInvalidSamples()
    {
        Span<float> samples = stackalloc float[]
        {
            -2f, -0.5f, 0f, 0.5f, 2f,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        };

        new Limiter().Process(samples);

        Assert.Equal(-0.999f, samples[0], precision: 7);
        Assert.Equal(-0.5f, samples[1], precision: 7);
        Assert.Equal(0f, samples[2], precision: 7);
        Assert.Equal(0.5f, samples[3], precision: 7);
        Assert.Equal(0.999f, samples[4], precision: 7);
        Assert.Equal(0f, samples[5], precision: 7);
        Assert.Equal(0f, samples[6], precision: 7);
        Assert.Equal(0f, samples[7], precision: 7);
    }
}
