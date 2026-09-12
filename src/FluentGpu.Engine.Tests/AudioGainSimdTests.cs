using System;
using System.Runtime.InteropServices;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class AudioGainSimdTests
{
    private static readonly ParamPlane Plane = new();
    private static BlockCtx Context(int channels) => new(0, 48000, channels, Plane);

    [Fact]
    public void SettledGain_MatchesScalarBytes_AcrossTailsChannelsAndAliasing()
    {
        int[] frameCounts = [0, 1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 31, 32, 127, 255, 256, 257, 480, 512];
        int[] channels = [1, 2, 3, 6, 8];
        float[] gains = [0f, -0f, 0.25f, 0.7f, 1.5f, -0.75f,
            BitConverter.Int32BitsToSingle(0x3f7fffff), BitConverter.Int32BitsToSingle(0x3f800001)];
        foreach (int frames in frameCounts)
        foreach (int ch in channels)
        foreach (float gain in gains)
        foreach (bool inPlace in new[] { false, true })
        {
            int n = frames * ch;
            var input = Samples(n + 2);
            var actual = inPlace ? input : Samples(n + 2);
            var expected = (float[])actual.Clone();
            Scalar(input.AsSpan(1, n), expected.AsSpan(1, n), gain);
            var stage = new GainStage(gain);
            Assert.Equal(frames, stage.Process(input.AsSpan(1, n), actual.AsSpan(1, n), frames, Context(ch)));
            AssertBytes(expected, actual); // includes untouched prefix/suffix sentinels
            Assert.Equal((long)n, stage.ProcessedSamples);
            Assert.Equal(0, stage.IdentitySamples);
        }
    }

    [Fact]
    public void PartialOverlap_RetainsOriginalForwardScalarSemantics()
    {
        foreach (int shift in new[] { -3, -1, 1, 3 })
        foreach (int frames in new[] { 1, 3, 4, 5, 17, 256 })
        {
            int n = frames * 2;
            int sourceStart = 4, destinationStart = sourceStart + shift;
            var actual = Samples(n + 10);
            var expected = (float[])actual.Clone();
            Scalar(expected.AsSpan(sourceStart, n), expected.AsSpan(destinationStart, n), 0.7f);
            var stage = new GainStage(0.7f);
            stage.Process(actual.AsSpan(sourceStart, n), actual.AsSpan(destinationStart, n), frames, Context(2));
            AssertBytes(expected, actual);
        }
    }

    [Fact]
    public void Ramp_ThenSettledBlock_RetainsScalarFrameClockAndChannelReplication()
    {
        foreach (int ch in new[] { 1, 2, 6 })
        {
            var stage = new GainStage(0.25f);
            var parameter = AudioParam.At(0.25f);
            stage.SetTargetLinear(0.75f, 19);
            parameter.RampTo(0.75f, 19, SmoothKind.Linear);
            foreach (int frames in new[] { 3, 7, 17, 5 })
            {
                var input = Samples(frames * ch);
                var expected = new float[input.Length];
                var actual = new float[input.Length];
                float start = parameter.Advance(frames), end = parameter.Current;
                if (start == end) Scalar(input, expected, end);
                else
                {
                    float perFrame = (end - start) / frames;
                    for (int f = 0; f < frames; f++)
                    {
                        float gain = start + perFrame * f;
                        for (int c = 0; c < ch; c++) expected[f * ch + c] = input[f * ch + c] * gain;
                    }
                }
                stage.Process(input, actual, frames, Context(ch));
                AssertBytes(expected, actual);
                Assert.Equal(parameter.Current, stage.CurrentGain);
            }
        }
    }

    [Fact]
    public void UnityIdentityAndBypass_RemainBytePreserving()
    {
        var input = Samples(1024);
        var output = new float[input.Length];
        var stage = new GainStage();
        stage.Process(input, output, 512, Context(2));
        AssertBytes(input, output);
        Assert.Equal(1024, stage.IdentitySamples);
        Assert.Equal(0, stage.ProcessedSamples);
        stage.SetTargetLinear(0.25f, 256);
        stage.Bypassed = true;
        stage.Process(input, output, 512, Context(2));
        AssertBytes(input, output);
        Assert.Equal(0.25f, stage.CurrentGain);
        Assert.Equal(0, stage.ProcessedSamples);
    }

    [Fact]
    public void VectorPath_RejectsUndersizedSpans_BeforeUnsafeAccess()
    {
        var stage = new GainStage(0.7f);
        var shortSource = Record.Exception(() => stage.Process(new float[7], new float[8], 8, Context(1)));
        var shortDestination = Record.Exception(() => stage.Process(new float[8], new float[7], 8, Context(1)));
        // Vector slicing checks and the portable scalar bounds check expose different managed exception types.
        Assert.True(shortSource is ArgumentException or IndexOutOfRangeException);
        Assert.True(shortDestination is ArgumentException or IndexOutOfRangeException);
    }

    [Fact]
    public void SettledGain_SteadyBlocks_AllocateNothing()
    {
        var input = Samples(1024);
        var output = new float[input.Length];
        var stage = new GainStage(0.7f);
        var context = Context(2);
        for (int i = 0; i < 1000; i++) stage.Process(input, output, 512, context);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) stage.Process(input, output, 512, context);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static void Scalar(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        for (int i = 0; i < source.Length; i++) destination[i] = source[i] * gain;
    }

    private static void AssertBytes(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)));

    private static float[] Samples(int count)
    {
        // All finite: include signed zero, smallest/largest subnormal, smallest normal and ordinary PCM magnitudes.
        int[] bits = [0, unchecked((int)0x80000000), 1, unchecked((int)0x80000001), 0x007fffff,
            unchecked((int)0x807fffff), 0x00800000, unchecked((int)0x80800000), 0x3e123456,
            unchecked((int)0xbe654321), 0x3f000000, unchecked((int)0xbf400000), 0x3f800000];
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = BitConverter.Int32BitsToSingle(bits[i % bits.Length]);
        return values;
    }
}
