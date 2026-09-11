using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The M4 hiccup-hardening fixes adjacent to the decode-ahead-ring fix (spec §7.9): (1) Buffering is gated on the primary
/// ring's actual fill depth, not a fixed tick count, so the RT loop never free-runs into an empty device buffer on a fresh
/// open; (2) a seek/flush-induced ring-empty is a known, EXPECTED rebuffer and must not be counted as an xrun; (3) a sink
/// write that under-delivers (a torn/invalidated device with no follow-default notification) is reported as NOT rendered
/// so the RT loop's sleep-on-idle branch re-arms instead of spinning, and a sustained run asks the on-box
/// <see cref="AudioDeviceController"/> for a rebuild. No real device — a fake partial-write <see cref="IAudioSink"/> plus
/// the deterministic (individually-drivable) <see cref="AudioFeedThread"/>/<see cref="RingAudioSource"/> API, exactly like
/// <see cref="AudioDeviceStateMachineTests"/>. Deterministic where possible; the two timeout/threshold cases are bounded
/// real-time waits (the same style as <c>AudioDeviceStateMachineTests.WatcherEvent_MarshalsToColdThread_AndRebuilds</c>).
/// </summary>
public sealed class AudioPrefillTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

    /// <summary>A fake <see cref="IAudioSink"/> that accepts at most <see cref="FramesToAccept"/> frames per
    /// <see cref="Write"/> call — the harness for fix 3 (a device that stops truly accepting data without a follow-default
    /// notification).</summary>
    private sealed class PartialWriteSink : IAudioSink
    {
        public MixFormat Format { get; }
        public int FramesToAccept = int.MaxValue;
        public int StartCalls, StopCalls;
        public PartialWriteSink(MixFormat format) => Format = format;
        public int Write(ReadOnlySpan<float> src, int frames) => Math.Max(0, Math.Min(frames, FramesToAccept));
        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
    }

    private static PcmAudioSession NewRtSession(out AudioFeedThread feed, double aheadMs = 50.0, double ringMs = 200.0)
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 256, driveWithOwnThread: false, endpoint);
        session.Configure(AudioGraphSpec.Passthrough);
        feed = new AudioFeedThread(session, Fmt.SampleRate, blockMs: 5.0, aheadMs: aheadMs, ringMs: ringMs);   // ctor attaches to the session
        return session;
    }

    private static void OpenVoiceAndArm(PcmAudioSession session, long seconds = 10)
    {
        var voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * seconds], 2);
        session.SetVoice(voice, TimeSpan.FromSeconds(seconds), Fmt.SampleRate * seconds, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
    }

    [Fact]
    public async Task Seek_ArmsSuppression_UntilFlushDecoderAndPcmAreAcknowledged()
    {
        var session = NewRtSession(out var feed);
        OpenVoiceAndArm(session);
        feed.WorkerPumpOnce();
        session.TickControl(256);
        session.TickControl(256);
        var seek = session.SeekAsync(TimeSpan.FromSeconds(2), SeekMode.Accurate).AsTask();
        Assert.True(session.SuppressXrunAccounting);
        await DriveOperationAsync(session, feed, seek);
        Assert.False(session.SuppressXrunAccounting);
        Assert.Equal(0, feed.XrunCount);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SeekRebuffer_DoesNotIncrementXrunCount()
    {
        var session = NewRtSession(out var feed);
        OpenVoiceAndArm(session);
        feed.WorkerPumpOnce();
        session.TickControl(256);
        session.TickControl(256);
        feed.FeedOnce();
        long before = feed.XrunCount;
        await DriveOperationAsync(session, feed, session.SeekAsync(TimeSpan.FromSeconds(2), SeekMode.Accurate).AsTask());
        Assert.Equal(before, feed.XrunCount);
        await session.DisposeAsync();
    }

    private static async Task DriveOperationAsync(PcmAudioSession session, AudioFeedThread feed, Task operation)
    {
        for (int i = 0; i < 2000 && !operation.IsCompleted; i++)
        {
            feed.WorkerPumpOnce();
            feed.FeedOnce();
            session.TickControl(256);
            await Task.Delay(1);
        }
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void PartialSinkWrite_ReportsAcceptedFramesAndRetainsTheRemainder()
    {
        var clockEndpoint = new HeadlessAudioEndpoint(Fmt);
        var sink = new PartialWriteSink(Fmt);
        var session = new PcmAudioSession(Fmt, sink, clockEndpoint.Clock, maxBlock: 256, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        OpenVoiceAndArm(session);

        session.PumpAudio(256);   // Opening -> Buffering (single-thread pull path: unchanged, one tick)
        session.PumpAudio(256);   // Buffering -> Ready -> Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        // Healthy write: the whole block lands, and RenderBlock reports it fully rendered.
        Assert.Equal(256, session.RenderBlock(256));

        // Device trouble (e.g. GetCurrentPadding failing without a device-change notification): the sink accepts far
        // less than requested. RenderBlock must report NOT rendered (0) rather than the requested frame count, so the
        // caller (AudioFeedThread.RtLoop's `if (rendered <= 0) Thread.Sleep(...)`) throttles instead of spinning.
        sink.FramesToAccept = 0;
        Assert.Equal(0, session.RenderBlock(256));

        sink.FramesToAccept = 100;
        long sourcePosition = session.SampleClock;
        Assert.Equal(100, session.RenderBlock(256));
        Assert.Equal(sourcePosition, session.SampleClock);
        Assert.Equal(100, session.RenderBlock(256));
        Assert.Equal(56, session.RenderBlock(256));
        Assert.Equal(sourcePosition, session.SampleClock);
    }

    [Fact]
    public void ConsecutiveSinkFailures_RequestDeviceRebuild()
    {
        var clockEndpoint = new HeadlessAudioEndpoint(Fmt);
        var sink = new PartialWriteSink(Fmt) { FramesToAccept = 0 };
        var session = new PcmAudioSession(Fmt, sink, clockEndpoint.Clock, maxBlock: 256, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        OpenVoiceAndArm(session);

        using var rebuilt = new ManualResetEventSlim(false);
        using var controller = new AudioDeviceController(session, () =>
        {
            rebuilt.Set();
            return new HeadlessAudioEndpoint(Fmt);
        });
        session.RegisterDisposable(controller);   // fix 3: PcmAudioSession captures the controller off this call
        controller.MarkRunning();
        controller.Start();   // spins the real cold device thread — RequestRebuild only marshals to it

        session.PumpAudio(256);   // Opening -> Buffering
        session.PumpAudio(256);   // Buffering -> Ready -> Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        for (int i = 0; i < 8 && !rebuilt.IsSet; i++) session.RenderBlock(256);   // sustained under-delivery trips the threshold

        Assert.True(rebuilt.Wait(TimeSpan.FromSeconds(5)),
            "a sustained run of under-delivered sink writes never asked the on-box AudioDeviceController for a rebuild");
    }
}
