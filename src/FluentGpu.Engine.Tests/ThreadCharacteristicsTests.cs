using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F1 — the thread-characteristics seam is an INSTANCE (V-PE34, no statics): the same <see cref="IRtThreadCharacteristics"/>
/// object handed to <see cref="AudioFeedThread"/> / <see cref="RingAudioSource"/> registers the RT thread through
/// <see cref="IRtThreadCharacteristics.Enter"/> and every decode-ahead producer plus the clock thread through
/// <see cref="IRtThreadCharacteristics.EnterDecode"/>, and each registration is reverted on the SAME thread when it exits.
/// A recording fake stands in for the Windows MMCSS leaf (which is on-box only), so these run headless and in parallel.
/// </summary>
public sealed class ThreadCharacteristicsTests
{
    private static readonly MixFormat Format = new(48000, 2);

    private readonly record struct Registration(string? ThreadName, int ThreadId);

    /// <summary>Records which thread called <c>Enter</c>/<c>EnterDecode</c> and which thread disposed each returned token.</summary>
    private sealed class RecordingCharacteristics : IRtThreadCharacteristics
    {
        private readonly object _gate = new();
        private readonly List<Registration> _rtEntered = new(), _rtReverted = new(), _decodeEntered = new(), _decodeReverted = new();

        public IReadOnlyList<Registration> RtEntered => Snapshot(_rtEntered);
        public IReadOnlyList<Registration> RtReverted => Snapshot(_rtReverted);
        public IReadOnlyList<Registration> DecodeEntered => Snapshot(_decodeEntered);
        public IReadOnlyList<Registration> DecodeReverted => Snapshot(_decodeReverted);

        public IDisposable? Enter() => Register(_rtEntered, _rtReverted);
        public IDisposable? EnterDecode() => Register(_decodeEntered, _decodeReverted);

        private IDisposable Register(List<Registration> entered, List<Registration> reverted)
        {
            lock (_gate) entered.Add(Current());
            return new Token(this, reverted);
        }

        private List<Registration> Snapshot(List<Registration> list)
        {
            lock (_gate) return new List<Registration>(list);
        }

        private static Registration Current() => new(Thread.CurrentThread.Name, Environment.CurrentManagedThreadId);

        private sealed class Token(RecordingCharacteristics owner, List<Registration> reverted) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (owner._gate) reverted.Add(Current());
            }
        }
    }

    /// <summary>An implementation written before <c>EnterDecode</c> existed: only <c>Enter</c>.</summary>
    private sealed class LegacyCharacteristics : IRtThreadCharacteristics
    {
        public IDisposable? Enter() => null;
    }

    private static void Eventually(Func<bool> condition, string what) => Assert.True(SpinWait.SpinUntil(condition, 5000), what);

    private static float[] Silence(int seconds) => new float[Format.SampleRate * 2 * seconds];

    [Fact]
    public void Producer_RegistersThroughTheInstanceSeam_AndRevertsOnTheSameThreadWhenItExits()
    {
        var rt = new RecordingCharacteristics();
        var ring = new RingAudioSource(new MemoryAudioSource(Silence(10), 2), 2, ringFrames: 4096, targetAheadFrames: 2048,
            pumpFrames: 256, rt: rt);
        Assert.Empty(rt.DecodeEntered);                        // building a ring registers nothing

        ring.StartProducer();
        Eventually(() => rt.DecodeEntered.Count == 1, "the producer never registered through EnterDecode");
        var entered = rt.DecodeEntered[0];
        Assert.Equal(RingAudioSource.ProducerThreadName, entered.ThreadName);
        Assert.Empty(rt.DecodeReverted);                       // still registered while it runs
        Assert.Empty(rt.RtEntered);                            // a producer never claims the RT (Pro Audio) class

        ring.Dispose();
        Assert.True(ring.JoinProducer(3000), "the producer thread did not exit after Dispose");
        var reverted = Assert.Single(rt.DecodeReverted);
        Assert.Equal(entered.ThreadId, reverted.ThreadId);     // reverted on the thread that registered
        Assert.Single(rt.DecodeEntered);                       // exactly one registration for the whole life of the thread
    }

    [Fact]
    public void EachRingProducerRegistersItsOwnThread_OncePerThread()
    {
        var rt = new RecordingCharacteristics();
        var first = new RingAudioSource(new MemoryAudioSource(Silence(10), 2), 2, 4096, 2048, 256, rt: rt);
        var second = new RingAudioSource(new MemoryAudioSource(Silence(10), 2), 2, 4096, 2048, 256, rt: rt);

        first.StartProducer();
        first.StartProducer();                                 // idempotent: must not register a second time
        second.StartProducer();
        Eventually(() => rt.DecodeEntered.Count == 2, "both producers should have registered");
        Thread.Sleep(50);                                      // a stray third registration would have landed by now
        var entered = rt.DecodeEntered;
        Assert.Equal(2, entered.Count);
        Assert.NotEqual(entered[0].ThreadId, entered[1].ThreadId);

        first.Dispose();
        second.Dispose();
        Assert.True(first.JoinProducer(3000));
        Assert.True(second.JoinProducer(3000));
        Assert.Equal(2, rt.DecodeReverted.Count);
    }

    [Fact]
    public void RingWithoutASeam_StillRunsItsProducer()
    {
        var ring = new RingAudioSource(new MemoryAudioSource(Silence(10), 2), 2, 4096, 2048, 256);   // rt: null — headless / tests
        ring.StartProducer();
        Eventually(() => ring.BufferedFrames >= ring.TargetFrames, "a ring with no characteristics seam never filled");
        ring.Dispose();
        Assert.True(ring.JoinProducer(3000));
    }

    [Fact]
    public async Task FeedThread_RegistersRtThroughEnter_AndTheClockThroughEnterDecode_AndRevertsBoth()
    {
        var endpoint = new HeadlessAudioEndpoint(Format);
        await using var session = new PcmAudioSession(Format, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        var rt = new RecordingCharacteristics();
        using var feed = new AudioFeedThread(session, blockFrames: 256, rt: rt);

        feed.Start();
        Eventually(() => rt.RtEntered.Count == 1 && rt.DecodeEntered.Count == 1, "the RT and clock threads never registered");
        Assert.Equal("FluentGpu.AudioRT", rt.RtEntered[0].ThreadName);
        Assert.Equal("FluentGpu.AudioClock", rt.DecodeEntered[0].ThreadName);
        Assert.Empty(rt.RtReverted);
        Assert.Empty(rt.DecodeReverted);

        feed.Stop();
        Assert.True(feed.IsStopped);
        Assert.Equal(rt.RtEntered[0].ThreadId, Assert.Single(rt.RtReverted).ThreadId);
        Assert.Equal(rt.DecodeEntered[0].ThreadId, Assert.Single(rt.DecodeReverted).ThreadId);
        Assert.Single(rt.RtEntered);                           // one registration per thread, never re-entered
        Assert.Single(rt.DecodeEntered);                       // the worker thread is not an EnterDecode thread
    }

    [Fact]
    public async Task Wrap_And_WrapAdditional_HandTheFeedsInstanceToEveryRing()
    {
        var endpoint = new HeadlessAudioEndpoint(Format);
        await using var session = new PcmAudioSession(Format, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        var rt = new RecordingCharacteristics();
        using var feed = new AudioFeedThread(session, blockFrames: 256, rt: rt);

        var primary = (RingAudioSource)feed.Wrap(new MemoryAudioSource(Silence(10), 2));
        var additional = (RingAudioSource)feed.WrapAdditional(new MemoryAudioSource(Silence(10), 2), voiceId: 99);
        primary.StartProducer();
        additional.StartProducer();
        Eventually(() => rt.DecodeEntered.Count == 2, "rings built by the feed never registered through the feed's seam");
        var entered = rt.DecodeEntered;
        Assert.All(entered, e => Assert.Equal(RingAudioSource.ProducerThreadName, e.ThreadName));
        Assert.NotEqual(entered[0].ThreadId, entered[1].ThreadId);

        feed.Dispose();                                        // the final sweep disposes both rings
        Assert.True(primary.JoinProducer(3000));
        Assert.True(additional.JoinProducer(3000));
        Assert.Equal(2, rt.DecodeReverted.Count);
    }

    [Fact]
    public void NullCharacteristics_EnterDecode_IsNullThroughTheInterface()
    {
        IRtThreadCharacteristics seam = NullRtThreadCharacteristics.Instance;
        Assert.Null(seam.EnterDecode());
        Assert.Null(seam.Enter());
    }

    [Fact]
    public void LegacyImplementationWithOnlyEnter_GetsTheNullDefaultForEnterDecode()
    {
        IRtThreadCharacteristics seam = new LegacyCharacteristics();
        Assert.Null(seam.EnterDecode());
    }
}
