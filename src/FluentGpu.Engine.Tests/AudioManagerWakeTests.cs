using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Real management-thread gates; manual pumping alone cannot detect a missing seek wake.</summary>
public sealed class AudioManagerWakeTests
{
    private static readonly MixFormat Format = new(48000, 2);

    private static PcmAudioSession NewSession()
    {
        var endpoint = new HeadlessAudioEndpoint(Format);
        var session = new PcmAudioSession(Format, endpoint.Sink, endpoint.Clock, maxBlock: 512, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        return session;
    }

    private static void Eventually(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000));

    [Fact]
    public async Task IdleLiveManager_HasNoPeriodicPasses_AndStopWakesIt()
    {
        await using var session = NewSession();
        using var feed = new AudioFeedThread(session, blockFrames: 256);
        feed.Start();
        Eventually(() => feed.ManagerPassCount >= 1);
        await Task.Delay(80);
        long passes = feed.ManagerPassCount, wakes = feed.ManagerWakeCount;
        await Task.Delay(160);
        Assert.Equal(passes, feed.ManagerPassCount);
        Assert.Equal(wakes, feed.ManagerWakeCount);
        feed.Stop();
        Assert.True(feed.IsStopped);
        feed.Start();
        Eventually(() => feed.ManagerPassCount > passes);
        feed.Stop();
        Assert.True(feed.IsStopped);
    }

    [Fact]
    public async Task SeekPublishedBeforeStart_AndWhileParked_ReachesActiveDecoder()
    {
        await using var session = NewSession();
        using var feed = new AudioFeedThread(session, blockFrames: 256);
        var primary = new SeekDecoder();
        var incoming = new SeekDecoder();
        feed.Wrap(new DecoderAudioSource(primary));
        var active = feed.WrapAdditional(new DecoderAudioSource(incoming), 99);
        session.SetActiveVoice(99, active, TimeSpan.FromSeconds(30), 1440000);
        feed.RequestSeek(1234);
        feed.Start();
        Eventually(() => incoming.LastSeek == 1234);
        Assert.Equal(-1, primary.LastSeek);
        for (int i = 0; i < 12; i++)
        {
            long target = 2400 + i;
            feed.RequestSeek(target);
            Eventually(() => incoming.LastSeek == target);
        }
        Assert.Equal(13, feed.ManagerSeekCount);
        Assert.Equal(-1, primary.LastSeek);
    }

    [Fact]
    public async Task RetireDuringDetachedDrain_IsNotLost_WhenSignalsCoalesce()
    {
        await using var session = NewSession();
        using var feed = new AudioFeedThread(session, blockFrames: 256);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = new DisposalSource(() => { entered.Set(); release.Wait(3000); });
        var first = new RingAudioSource(blocked, 2);
        feed.EnqueueRetire(first);
        feed.Start();
        try
        {
            Assert.True(entered.Wait(3000));
            var sources = new DisposalSource[32];
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = new DisposalSource();
                var ring = new RingAudioSource(sources[i], 2);
                feed.EnqueueRetire(ring);
                feed.EnqueueRetire(ring); // duplicate acknowledgement must not relink the intrusive node
            }
            // Both the mailbox and stack are published while one drain owns a detached list.
            feed.RequestSeek(100);
            feed.RequestSeek(200);
            release.Set();
            Eventually(() => feed.ManagerRetireCount == 33 && feed.ManagerSeekCount == 1);
            Assert.Equal(1, blocked.DisposeCount);
            Assert.All(sources, source => Assert.Equal(1, source.DisposeCount));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ThrowingDisposer_DoesNotLoseRemainderOfDetachedStack()
    {
        await using var session = NewSession();
        using var feed = new AudioFeedThread(session, blockFrames: 256);
        var healthy = new DisposalSource();
        var throwing = new DisposalSource(() => throw new InvalidOperationException("test disposal fault"));
        feed.EnqueueRetire(new RingAudioSource(healthy, 2));
        feed.EnqueueRetire(new RingAudioSource(throwing, 2));
        feed.WorkerPumpOnce();
        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, healthy.DisposeCount);
        Assert.Equal(2, feed.ManagerRetireCount);
    }

    [Fact]
    public async Task DedicatedProducerLowWater_DoesNotWakeManager_ManualPumpStillRefills()
    {
        await using var session = NewSession();
        using var feed = new AudioFeedThread(session, blockFrames: 256, ringFrames: 1024, targetAheadFrames: 512);
        var ring = (RingAudioSource)feed.Wrap(new MemoryAudioSource(new float[48000 * 2], 2));
        feed.FeedOnce(); // empty manual ring: manager is its only producer
        Assert.Equal(1, feed.ManagerLowWaterWakeCount);
        feed.WorkerPumpOnce();
        Assert.True(ring.BufferedFrames >= ring.TargetFrames);
        feed.FeedOnce(); // re-arm low-water latch above the watermark
        ring.Read(new float[ring.BufferedFrames * 2], 2);
        ring.StartProducer();
        // Dedicated ownership is established even if decode wins the refill race before this callback.
        feed.FeedOnce();
        Assert.Equal(1, feed.ManagerLowWaterWakeCount);
    }

    [Fact]
    public async Task StopDisposeAndLateSignals_RaceWithoutClosingLiveWaitHandles()
    {
        await using var session = NewSession();
        using var feed = new AudioFeedThread(session, blockFrames: 256);
        feed.Start();
        Eventually(() => feed.ManagerPassCount > 0);
        using var start = new ManualResetEventSlim();
        var signals = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 1000; i++) { feed.RequestSeek(i); feed.WakeOutput(); }
        });
        var stop = Task.Run(() => { start.Wait(); feed.Stop(); });
        var dispose = Task.Run(() => { start.Wait(); feed.Dispose(); });
        start.Set();
        await Task.WhenAll(signals, stop, dispose).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(feed.IsStopped);
        feed.WakeOutput();
        feed.RequestSeek(1001);
    }

    [Fact]
    public async Task ShutdownSweep_WaitsForLastOutputRetirementAcknowledgement()
    {
        await using var session = NewSession();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var source = new DisposalSource();
        var retired = new RingAudioSource(source, 2);
        AudioFeedThread? feed = null;
        var characteristics = new ExitCharacteristics(() =>
        {
            entered.Set();
            release.Wait(3000);
            feed!.EnqueueRetire(retired);
        });
        using (feed = new AudioFeedThread(session, blockFrames: 256, rt: characteristics))
        {
            feed.Start();
            Eventually(() => feed.ManagerPassCount > 0);
            var dispose = Task.Run(feed.Dispose);
            try
            {
                Assert.True(entered.Wait(3000));
                Assert.Equal(0, source.DisposeCount);
            }
            finally { release.Set(); }
            await dispose.WaitAsync(TimeSpan.FromSeconds(8));
            Eventually(() => source.DisposeCount == 1);
            Assert.Equal(1, feed.ManagerRetireCount);
        }
    }

    private sealed class SeekDecoder : IAudioDecoder
    {
        private long _lastSeek = -1;
        public long LastSeek => Interlocked.Read(ref _lastSeek);
        public bool TryOpen(IMediaByteSource source, MixFormat target, out DecodedInfo info) { info = default; return true; }
        public int Read(Span<float> destination) { destination.Clear(); return destination.Length / 2; }
        public long Seek(long frame) { Interlocked.Exchange(ref _lastSeek, frame); return frame; }
        public GaplessInfo Gapless => GaplessInfo.None;
    }

    private sealed class DisposalSource(Action? onDispose = null) : IAudioSource, IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int Read(Span<float> destination, int channels) => 0;
        public long PositionFrames => 0;
        public bool Exhausted => true;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public void Dispose() { Interlocked.Increment(ref _disposeCount); onDispose?.Invoke(); }
    }

    private sealed class ExitCharacteristics(Action onExit) : IRtThreadCharacteristics, IDisposable
    {
        public IDisposable Enter() => this;
        public void Dispose() => onExit();
    }
}
