using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The audio clock thread and the ring producer block (no polling) while idle. These gates pin the wake paths that replaced the
/// 15 ms / 20 ms polls: an idle producer still refills on the RT refill edge and on a seek, disposal still joins it promptly,
/// the clock thread never goes idle while a session is opening/buffering/playing, and Stop does not wait out an idle clock.
/// </summary>
public sealed class AudioIdleWakeTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

    private static RingAudioSource NewRing(out MemoryAudioSource source)
    {
        source = new MemoryAudioSource(new float[48000 * 2 * 20], 2);   // 20 s of silence
        return new RingAudioSource(source, 2, ringFrames: 16384, targetAheadFrames: 8192, pumpFrames: 512);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; Thread.Sleep(2); }
        return condition();
    }

    [Fact]
    public void IdleProducer_AtTarget_RefillsOnTheRtRefillEdge_WellInsideTheSafetyTimeout()
    {
        var ring = NewRing(out _);
        ring.StartProducer();
        try
        {
            Assert.True(WaitUntil(() => ring.BufferedFrames >= ring.TargetFrames, 3000), "the ring never reached its target");
            Thread.Sleep(100);   // let the producer park in its long idle wait
            // The RT consumes a little more than an eighth of the target: the feed then checks the edge and wakes the producer.
            var block = new float[2 * 2048];
            ring.Read(block, 2);
            Assert.True(ring.CheckRefillEdge());
            ring.WakeProducer();
            Assert.True(WaitUntil(() => ring.BufferedFrames >= ring.TargetFrames, 400), "an idle producer did not refill on the refill edge");
        }
        finally { ring.Dispose(); Assert.True(ring.JoinProducer(3000)); }
    }

    [Fact]
    public void RefillEdge_FiresOncePerDrop_AndRearmsOnRecovery()
    {
        var ring = NewRing(out _);
        ring.PumpAhead();   // fills to target on this thread, no producer
        Assert.False(ring.CheckRefillEdge());
        ring.Read(new float[2 * 2048], 2);
        Assert.True(ring.CheckRefillEdge());
        Assert.False(ring.CheckRefillEdge());   // latched while it stays low
        ring.PumpAhead();
        Assert.False(ring.CheckRefillEdge());   // recovered: re-armed
        ring.Read(new float[2 * 2048], 2);
        Assert.True(ring.CheckRefillEdge());
        ring.Dispose();
    }

    [Fact]
    public async Task IdleProducer_WithAFullRing_AppliesASeekPromptly()
    {
        var ring = NewRing(out _);
        ring.StartProducer();
        try
        {
            Assert.True(WaitUntil(() => ring.BufferedFrames >= ring.TargetFrames, 3000));
            Thread.Sleep(100);
            var sw = Stopwatch.StartNew();
            long landed = await ring.SeekFrameAsync(48000 * 5).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(sw.ElapsedMilliseconds < 400, $"a seek into an idle producer took {sw.ElapsedMilliseconds} ms");
            Assert.True(landed >= 48000 * 5);
        }
        finally { ring.Dispose(); Assert.True(ring.JoinProducer(3000)); }
    }

    [Fact]
    public void Dispose_JoinsAnIdleProducer_Promptly()
    {
        var ring = NewRing(out _);
        ring.StartProducer();
        Assert.True(WaitUntil(() => ring.BufferedFrames >= ring.TargetFrames, 3000));
        Thread.Sleep(100);
        var sw = Stopwatch.StartNew();
        ring.Dispose();
        Assert.True(ring.JoinProducer(1000));
        Assert.True(sw.ElapsedMilliseconds < 100, $"disposing an idle producer took {sw.ElapsedMilliseconds} ms");
    }

    private static PcmAudioSession NewSession(out AudioFeedThread feed)
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        feed = new AudioFeedThread(session, blockFrames: 256);
        session.Configure(AudioGraphSpec.Passthrough);
        long frames = 30L * Fmt.SampleRate;
        session.SetVoice(new SignalGeneratorSource(2, Fmt.SampleRate, 220, 0.5f, frames), TimeSpan.FromSeconds(30), frames, NormMode.Off, -14f, initialVolume: 1f);
        return session;
    }

    [Fact]
    public void ControlIdle_IsFalse_WhileOpeningBufferingOrPlaying()
    {
        var session = NewSession(out var feed);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        Assert.False(session.ControlIdle);            // Opening
        feed.WorkerPumpOnce();
        feed.ControlTickOnce();                        // Opening -> Buffering
        Assert.Equal(PlaybackState.Buffering, session.CurrentState);
        Assert.False(session.ControlIdle);
        _ = session.PlayAsync();
        feed.WorkerPumpOnce();
        feed.ControlTickOnce();
        feed.ControlTickOnce();
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        Assert.False(session.ControlIdle);
        feed.Dispose();
    }

    [Fact]
    public void Stop_DoesNotWaitOutAnIdleClockThread()
    {
        var session = NewSession(out var feed);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        feed.Start();
        // Never played: the session settles in Ready and the clock thread goes idle on its wake event.
        Assert.True(WaitUntil(() => session.ControlIdle, 5000), "the session never became control-idle");
        Thread.Sleep(100);
        var sw = Stopwatch.StartNew();
        feed.Stop();
        Assert.True(feed.IsStopped);
        Assert.True(sw.ElapsedMilliseconds < 100, $"Stop took {sw.ElapsedMilliseconds} ms with an idle clock thread");
        feed.Dispose();
    }

    [Fact]
    public void IdleClockThread_WakesForAPlayCommand_WithoutWaitingOutItsSafetyTimeout()
    {
        var session = NewSession(out var feed);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        feed.Start();
        Assert.True(WaitUntil(() => session.ControlIdle, 5000));
        Thread.Sleep(100);
        var sw = Stopwatch.StartNew();
        _ = session.PlayAsync();
        Assert.True(WaitUntil(() => session.CurrentState == PlaybackState.Playing, 2000), "an idle clock thread did not start playback");
        Assert.True(sw.ElapsedMilliseconds < 200, $"play took {sw.ElapsedMilliseconds} ms to reach Playing from an idle clock");
        feed.Dispose();
    }
}
