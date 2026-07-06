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
