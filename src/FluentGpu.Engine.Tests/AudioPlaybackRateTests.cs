using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

[Collection(SerialTestCollection.Name)]
public sealed class AudioPlaybackRateTests
{
    private const int SampleRate = 48000;

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(1.9)]
    [InlineData(3.0)]
    public void RateChangesDurationWithoutChangingPitchAndDrainsExactly(double rate)
    {
        const int frames = SampleRate * 2;
        using var source = new WsolaAudioSource(new MemoryAudioSource(Tone(frames), 2), SampleRate, 2) { Rate = rate };
        var output = new float[(int)Math.Ceiling(frames / rate) * 2 + 2048];
        int produced = 0;
        while (!source.Exhausted)
        {
            int read = source.Read(output.AsSpan(produced * 2, Math.Min(514, output.Length - produced * 2)), 2);
            Assert.True(read > 0, "The confirmed EOF tail must drain without stalling.");
            produced += read;
        }
        Assert.InRange(produced, (int)Math.Ceiling(frames / rate) - 1, (int)Math.Ceiling(frames / rate) + 1);
        Assert.Equal(frames, source.PositionFrames);
        Assert.Equal(0, source.Read(new float[128], 2));
        int first = 2000, last = produced - 2000, crossings = 0;
        for (int f = first + 1; f < last; f++)
            if (output[(f - 1) * 2] <= 0 && output[f * 2] > 0) crossings++;
        double frequency = crossings * (double)SampleRate / (last - first);
        Assert.InRange(frequency, 435, 445);
        // Antiphase channels stay antiphase: matching must not use a cancelling mono sum.
        for (int f = first; f < last; f++) Assert.Equal(-output[f * 2], output[f * 2 + 1], 5);
    }

    [Fact]
    public void UnityBypassPreservesEverySampleAndReadCursor()
    {
        var input = Tone(3217);
        var inner = new MemoryAudioSource(input, 2);
        using var source = new WsolaAudioSource(inner, SampleRate, 2);
        var output = new float[input.Length];
        int produced = 0;
        while (!source.Exhausted)
        {
            int got = source.Read(output.AsSpan(produced * 2, Math.Min(514, output.Length - produced * 2)), 2);
            produced += got;
            Assert.Equal(produced, inner.PositionFrames);
        }
        Assert.Equal(input, output);
    }

    [Fact]
    public void ClockMapsQueuedOldRateAndNewRateSeparately()
    {
        using var source = new WsolaAudioSource(new MemoryAudioSource(Tone(SampleRate), 2), SampleRate, 2);
        var hop = new float[960 * 2];
        source.Read(hop, 2);
        source.Rate = 1.9;
        source.Read(hop, 2);
        source.Rate = 1;
        source.Read(hop, 2);
        Assert.Equal(480, source.SourceFrameAt(480), 6);
        Assert.Equal(960 + 480 * 1.9, source.SourceFrameAt(1440), 6);
        Assert.Equal(960 + 960 * 1.9 + 480, source.SourceFrameAt(2400), 6);
        Assert.Equal(1.9, WsolaAudioSource.ClampRate(1.9));
    }

    [Fact]
    public void SeekResetDiscardsOldOverlapAndQueuedAudio()
    {
        var samples = new float[SampleRate * 2];
        Array.Fill(samples, 0.25f, 0, 24000);
        Array.Fill(samples, -0.5f, 24000, samples.Length - 24000);
        var inner = new MemoryAudioSource(samples, 2);
        using var source = new WsolaAudioSource(inner, SampleRate, 2) { Rate = 1.9 };
        source.Read(new float[128], 2);
        inner.SeekFrame(15000);
        source.Reset(15000);
        var output = new float[256];
        Assert.Equal(128, source.Read(output, 2));
        Assert.All(output, x => Assert.Equal(-0.5f, x));
        Assert.Equal(15000, source.SourceFrameAt(0));
        Assert.Equal(15000 + 64 * 1.9, source.SourceFrameAt(64), 6);
    }

    [Fact]
    public void RingPreflightStallsWithoutConsumingOrInsertingSilence()
    {
        using var ring = new RingAudioSource(new MemoryAudioSource(Tone(SampleRate), 2), 2, 8192, 4096, 512);
        using var source = new WsolaAudioSource(ring, SampleRate, 2) { Rate = 3 };
        var mixer = new CrossfadeMixer(2, 480);
        mixer.AddVoice(new MixVoice { Src = source, Env = GainEnvelope.Constant, ReplayGainScalar = 1 });
        Assert.Equal(0, mixer.ReadableFrames(480, out var waiting));
        Assert.Same(ring, waiting);
        Assert.Equal(0, ring.PositionFrames);
        ring.PumpAhead();
        Assert.Equal(480, mixer.ReadableFrames(480, out _));
        var output = new float[960];
        var context = new BlockCtx(0, SampleRate, 2, new ParamPlane());
        mixer.Render(output, 480, context);
        Assert.Equal(1440, source.PositionFrames);
        Assert.False(ring.Starved);
        for (int i = 0; i < 20; i++)
        {
            int readable = mixer.ReadableFrames(480, out _);
            if (readable == 0) break;
            context = new BlockCtx(mixer.ConsumeSeq, SampleRate, 2, new ParamPlane());
            mixer.Render(output, readable, context);
        }
        Assert.Equal(0, mixer.ReadableFrames(480, out _));
        long stopped = source.PositionFrames;
        Assert.False(ring.Starved);
        ring.PumpAhead();
        Assert.True(mixer.PcmReady(4800));
        Assert.Equal(stopped, source.PositionFrames);
    }

    [Fact]
    public void RenderIsAllocationFreeAfterWarmup()
    {
        using var source = new WsolaAudioSource(new SignalGeneratorSource(2, SampleRate, 440, 0.5f, SampleRate * 60), SampleRate, 2) { Rate = 1.9 };
        var block = new float[512];
        for (int i = 0; i < 200; i++) source.Read(block, 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) source.Read(block, 2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task SessionPublishesContentTimeAndRetainsRateAcrossSeek()
    {
        var format = new MixFormat(SampleRate, 2);
        using var endpoint = new HeadlessAudioEndpoint(format);
        var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, 480, false);
        var core = new MediaPlayerCore();
        session.SetVoice(new MemoryAudioSource(Tone(SampleRate * 4), 2), TimeSpan.FromSeconds(4), SampleRate * 4, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(core));
        session.SetRate(1.9);
        await session.PlayAsync();
        for (int i = 0; i < 53; i++) session.PumpAudio(480);
        long content = session.ContentPositionFrames;
        Assert.InRange(content, (long)(session.PlayedFrames * 1.9) - 960, (long)(session.PlayedFrames * 1.9) + 1);
        Assert.InRange(core.Position.Peek().TotalSeconds, content / (double)SampleRate - 0.001, content / (double)SampleRate + 0.001);
        Task seek = session.SeekAsync(TimeSpan.FromSeconds(2), SeekMode.Accurate).AsTask();
        for (int i = 0; i < 2000 && !seek.IsCompleted; i++)
        {
            session.PumpAudio(480);
            await Task.Delay(1);
        }
        await seek.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1.9, session.PlaybackRate);
        for (int i = 0; i < 10; i++) session.PumpAudio(480);
        Assert.InRange(session.ContentPositionFrames, SampleRate * 2, SampleRate * 3);
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.9)]
    [InlineData(3.0)]
    public void ShortClipEofIsFiniteAndDrainsAtRequestedRate(double rate)
    {
        using var source = new WsolaAudioSource(new MemoryAudioSource(Tone(127), 2), SampleRate, 2) { Rate = rate };
        var output = new float[1024];
        int count = source.Read(output, 2);
        Assert.InRange(count, (int)Math.Ceiling(127 / rate) - 1, (int)Math.Ceiling(127 / rate) + 1);
        Assert.True(source.Exhausted);
    }

    private static float[] Tone(int frames)
    {
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float value = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / SampleRate);
            samples[i * 2] = value;
            samples[i * 2 + 1] = -value;
        }
        return samples;
    }
}
