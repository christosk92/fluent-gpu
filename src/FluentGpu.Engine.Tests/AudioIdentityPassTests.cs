using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class AudioIdentityPassTests
{
    private static readonly ParamPlane Plane = new();
    private static BlockCtx Ctx(int channels) => new(0, 48000, channels, Plane);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void UnityGainAndNeutralChannelPreservePcmWithoutArithmetic(int channels)
    {
        var gain = new GainStage();
        var channel = new ChannelStage();
        float[] input = new float[channels * 8];
        for (int i = 0; i < input.Length; i++) input[i] = (i - 9) * .013f;
        input[0] = -0f;
        var output = new float[input.Length];
        gain.Process(input, output, 8, Ctx(channels));
        channel.Process(output, output, 8, Ctx(channels));
        Assert.Equal(System.Runtime.InteropServices.MemoryMarshal.AsBytes(input.AsSpan()).ToArray(),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.AsSpan()).ToArray());
        Assert.Equal(0, gain.ProcessedSamples);
        Assert.Equal(0, channel.ProcessedSamples);
        Assert.Equal(input.Length, gain.IdentitySamples);
        Assert.Equal(input.Length, channel.IdentitySamples);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IdentityCopiesShiftedOverlappingBuffers(bool channel, bool forward)
    {
        IDspStage stage = channel ? new ChannelStage() : new GainStage();
        float[] values = [1, 2, 3, 4, 5, 6];
        int source = forward ? 0 : 2, destination = forward ? 2 : 0;
        float[] expected = values.AsSpan(source, 4).ToArray();
        stage.Process(values.AsSpan(source, 4), values.AsSpan(destination, 4), 2, Ctx(2));
        Assert.Equal(expected, values.AsSpan(destination, 4).ToArray());
    }

    [Fact]
    public void GainReachingUnityStillProcessesTheRampBlock()
    {
        var stage = new GainStage(.5f);
        stage.SetTargetLinear(1f, 4);
        float[] samples = [1, 1, 1, 1];
        stage.Process(samples, samples, 4, Ctx(1));
        Assert.Equal(new[] { .5f, .625f, .75f, .875f }, samples);
        Assert.Equal(4, stage.ProcessedSamples);
        stage.Process(samples, samples, 4, Ctx(1));
        Assert.Equal(4, stage.IdentitySamples);
        stage.SetLinear(.999f); // near-unity is not identity
        stage.Process(samples, samples, 4, Ctx(1));
        Assert.Equal(8, stage.ProcessedSamples);
    }

    [Fact]
    public void MonoAndBalanceAreNotSkipped()
    {
        var stage = new ChannelStage(mono: true);
        float[] samples = [1, -1, .25f, .75f];
        stage.Process(samples, samples, 2, Ctx(2));
        Assert.Equal(new[] { 0f, 0f, .5f, .5f }, samples);
        stage.SetMono(false);
        stage.SetTargetBalance(1f, 0);
        stage.Process(samples, samples, 2, Ctx(2));
        Assert.Equal(8, stage.ProcessedSamples);
        Assert.Equal(0, stage.IdentitySamples);
        stage.SetTargetBalance(0f, 2);
        stage.Process(samples, samples, 2, Ctx(2)); // keep pre/post balance guard
        Assert.Equal(12, stage.ProcessedSamples);
        stage.Process(samples, samples, 2, Ctx(2));
        Assert.Equal(4, stage.IdentitySamples);
    }

    [Fact]
    public void TransportMatchesScalarReferenceAcrossRetargets()
    {
        var ramp = new TransportRamp(1);
        var reference = ramp;
        float[] actual = new float[32], expected = new float[32];
        for (int block = 0; block < 10; block++)
        {
            long start = block * 16;
            if (block is 1 or 5)
            {
                float target = block == 1 ? 0 : 1;
                ramp.Retarget(target, start, 25);
                reference.Retarget(target, start, 25);
            }
            Array.Fill(actual, .25f); Array.Fill(expected, .25f);
            ramp.Apply(actual, 16, 2, start);
            for (int frame = 0; frame < 16; frame++)
                for (int ch = 0; ch < 2; ch++) expected[frame * 2 + ch] *= reference.At(start + frame);
            Assert.Equal(expected, actual);
        }
        Assert.Equal(320, ramp.ProcessedSamples + ramp.IdentitySamples);
        Assert.True(ramp.IdentitySamples > 0);
        Assert.True(ramp.ProcessedSamples > 0);
    }

    [Fact]
    public void SteadyIdentityAllocatesNothing()
    {
        var gain = new GainStage(); var channel = new ChannelStage(); var ramp = new TransportRamp(1);
        float[] samples = new float[960]; var ctx = Ctx(2);
        for (int i = 0; i < 16; i++)
        {
            gain.Process(samples, samples, 480, ctx); channel.Process(samples, samples, 480, ctx);
            ramp.Apply(samples, 480, 2, i * 480);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            gain.Process(samples, samples, 480, ctx); channel.Process(samples, samples, 480, ctx);
            ramp.Apply(samples, 480, 2, i * 480);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, gain.ProcessedSamples + channel.ProcessedSamples + ramp.ProcessedSamples);
    }
}
