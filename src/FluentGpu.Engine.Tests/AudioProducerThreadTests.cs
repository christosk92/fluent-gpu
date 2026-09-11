using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// #113 — the per-ring decode-ahead producer (<see cref="RingAudioSource.StartProducer"/>) is a dedicated
/// <see cref="ThreadPriority.AboveNormal"/> thread, never a Normal-priority ThreadPool/LongRunning task: under a busy machine
/// (the reporter compiles while listening) a Normal producer lost scheduling to app work and playback stalled. These gates
/// observe the thread from INSIDE the inner source's <c>Read</c> — the only place the producer identity is authoritative.
/// </summary>
public sealed class AudioProducerThreadTests
{
    /// <summary>An endless silent source that records the identity of every thread entering <c>Read</c> and, while
    /// <see cref="Release"/> is unset, holds the first reader inside <c>Read</c> so a second producer (if one were ever
    /// spawned) would show up as a second concurrent entry.</summary>
    private sealed class ThreadProbeSource : IAudioSource, IDisposable
    {
        private readonly int _channels;
        private long _pos;
        public readonly object Gate = new();
        public readonly HashSet<int> ThreadIds = new();
        public readonly ManualResetEventSlim Entered = new(false), Release = new(true);
        public int Entries;
        public ThreadPriority Priority;
        public string? ThreadName;
        public bool IsThreadPoolThread;
        public volatile bool Disposed;

        public ThreadProbeSource(int channels) => _channels = Math.Max(1, channels);

        public int Read(Span<float> dst, int channels)
        {
            var current = Thread.CurrentThread;
            lock (Gate)
            {
                Entries++;
                ThreadIds.Add(Environment.CurrentManagedThreadId);
                Priority = current.Priority;
                ThreadName = current.Name;
                IsThreadPoolThread = current.IsThreadPoolThread;
            }
            Entered.Set();
            Release.Wait();
            dst.Clear();
            int frames = dst.Length / _channels;
            _pos += frames;
            return frames;   // endless
        }

        public long PositionFrames => _pos;
        public bool Exhausted => false;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void StartProducer_DecodesOnADedicatedAboveNormalThread_NeverTheThreadPool()
    {
        var probe = new ThreadProbeSource(2);
        var ring = new RingAudioSource(probe, 2, ringFrames: 4096, targetAheadFrames: 2048, pumpFrames: 256);
        Assert.False(ring.HasDedicatedProducer);

        ring.StartProducer();
        Assert.True(ring.HasDedicatedProducer);
        Assert.True(probe.Entered.Wait(TimeSpan.FromSeconds(3)), "the producer never entered the inner Read");

        lock (probe.Gate)
        {
            Assert.Equal(ThreadPriority.AboveNormal, probe.Priority);        // wins against Normal app/ThreadPool work (#113)
            Assert.Equal(RingAudioSource.ProducerThreadName, probe.ThreadName);
            Assert.False(probe.IsThreadPoolThread);                           // a real dedicated thread, not a pool/LongRunning task
        }

        ring.Dispose();                                                      // cancels; the producer's finally disposes the inner
        Assert.True(ring.JoinProducer(3000), "the producer thread did not exit after Dispose");
        Assert.True(probe.Disposed);
    }

    [Fact]
    public void StartProducer_IsIdempotent()
    {
        var probe = new ThreadProbeSource(2);
        probe.Release.Reset();                                               // hold the first reader inside Read
        var ring = new RingAudioSource(probe, 2, ringFrames: 4096, targetAheadFrames: 2048, pumpFrames: 256);

        ring.StartProducer();
        ring.StartProducer();                                                // must NOT spawn a second producer
        Assert.True(probe.Entered.Wait(TimeSpan.FromSeconds(3)), "the producer never entered the inner Read");
        Thread.Sleep(100);                                                   // a second thread would have entered Read by now

        lock (probe.Gate)
        {
            Assert.Equal(1, probe.Entries);
            Assert.Single(probe.ThreadIds);
        }

        probe.Release.Set();
        ring.Dispose();
        Assert.True(ring.JoinProducer(3000), "the producer thread did not exit after Dispose");
    }
}
