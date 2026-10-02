using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class PlaybackQualityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledManualPromotion_PreservesRingAndAnyFramesConsumedDuringOutgoingFade(bool crossesBoundary)
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 960, 48000);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false);
        using var feed = new AudioFeedThread(session, blockFrames: 480, ringFrames: 48000, targetAheadFrames: 24000);
        var outgoing = new TrackingPcmSource(.5f, 96000);
        var incoming = new TrackingPcmSource(.25f, 96000);
        var incomingRing = new RingAudioSource(incoming, 2, 48000, 24000, 960);
        incomingRing.PumpAhead();
        session.SetVoice(outgoing, TimeSpan.FromSeconds(2), 96000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        await session.PlayAsync();
        void Step()
        {
            feed.WorkerPumpOnce();
            session.TickControl(480);
            feed.FeedOnce();
            endpoint.AdvanceHardware(480);
        }
        for (int i = 0; i < 8; i++) Step();
        long start = session.SampleClock + (crossesBoundary ? 480 : 48000);
        var gate = new AudioTransitionGate(start);
        session.AddCrossfadeVoice(incomingRing, GainEnvelope.Fade(FadeKind.In, start, 960, CrossCurve.EqualPower).WithTransition(gate),
            start, 1, null, 7);
        session.SetVoiceEnvelope(session.PrimaryVoiceIdValue,
            GainEnvelope.Fade(FadeKind.Out, start, 960, CrossCurve.EqualPower).WithTransition(gate));
        Assert.False(gate.IsCommitted);
        Task fade = session.FadeOutAsync(TimeSpan.FromMilliseconds(50)).AsTask();
        await DriveOperation(fade, Step);
        long incomingPosition = incomingRing.PositionFrames;
        Assert.Equal(crossesBoundary, gate.IsCommitted);
        Assert.Equal(crossesBoundary, incomingPosition > 0);
        Task<long> promotion = session.PromoteScheduledVoiceAsync(7, incomingRing, TimeSpan.FromSeconds(2), 96000).AsTask();
        await DriveOperation(promotion, Step);
        Assert.Equal(incomingPosition, await promotion);
        Assert.Equal(incomingPosition, incomingRing.PositionFrames);
        Assert.Equal(0, session.SampleClock);
        Assert.Single(feed.RingsSnapshot);
        Assert.Same(incomingRing, feed.RingsSnapshot[0].Ring);
        Assert.True(session.MixerRef.HasVoice(session.PrimaryVoiceIdValue));
        Assert.False(session.MixerRef.HasVoice(7));
        gate.TryCancel(); // Cancellation after adoption cannot retire the retained ring under its new constant envelope.
        feed.WorkerPumpOnce();
        Assert.True(outgoing.Disposed);
        Assert.False(incoming.Disposed);
        session.FadeIn(TimeSpan.FromMilliseconds(50));
        await session.PlayAsync();
        Step();
        Assert.True(incomingRing.PositionFrames > incomingPosition);
        Assert.Equal(0, feed.XrunCount);
        Assert.Equal(1, endpoint.ResetCount);
        await session.DisposeAsync();
        Assert.True(incoming.Disposed);
    }

    private static async Task DriveOperation(Task operation, Action step)
    {
        for (int i = 0; i < 500 && !operation.IsCompleted; i++) { step(); await Task.Delay(1); }
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class TrackingPcmSource(float value, int frames) : IAudioSource, IDisposable
    {
        private readonly MemoryAudioSource _inner = new(Enumerable.Repeat(value, frames * 2).ToArray(), 2);
        public bool Disposed { get; private set; }
        public long PositionFrames => _inner.PositionFrames;
        public bool Exhausted => _inner.Exhausted;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public int Read(Span<float> destination, int channels) => _inner.Read(destination, channels);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task PreparedReplacement_WaitsForDeviceCapacityPlusTwoBlocks()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 4800);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false);
        var source = new AvailablePcmSource { AvailableFrames = 4800 };
        var ring = new RingAudioSource(source, 2, 48000, 24000, 480);
        ring.PumpAhead();
        var prepared = new AudioPreparedItem(ring, default, default, 48000, TimeSpan.FromSeconds(1), 48000,
            readinessFrames: 4800);
        Assert.True(prepared.IsReady);
        Task replacement = session.ReplacePreparedAsync(prepared).AsTask();
        Assert.False(replacement.IsCompleted);
        Assert.Equal(0, session.MixerRef.VoiceCount);
        source.AvailableFrames = 5760;
        ring.PumpAhead();
        await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5760, ring.BufferedFrames);
        Assert.Equal(1, session.MixerRef.VoiceCount);
        Assert.False(endpoint.IsStarted);
        await session.DisposeAsync();
    }

    private sealed class AvailablePcmSource : IAudioSource
    {
        public int AvailableFrames;
        public long PositionFrames { get; private set; }
        public bool Exhausted => false;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public int Read(Span<float> destination, int channels)
        {
            int frames = (int)Math.Min(destination.Length / channels, Math.Max(0, AvailableFrames - PositionFrames));
            destination[..(frames * channels)].Fill(.1f);
            PositionFrames += frames;
            return frames;
        }
    }

    [Fact]
    public void ConfirmedEmptyEof_EndsWithoutStartingEndpointOrReportingStarvation()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 960);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false);
        using var feed = new AudioFeedThread(session, sampleRate: 48000);
        session.SetVoice(new MemoryAudioSource(Array.Empty<float>(), 2), TimeSpan.Zero, 0, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        feed.WorkerPumpOnce();
        session.TickControl(480);
        session.TickControl(480);
        feed.FeedOnce();
        Assert.Equal(PlaybackState.Ended, session.CurrentState);
        Assert.False(endpoint.IsStarted);
        Assert.Equal(0, session.SubmittedFrames);
        Assert.Equal(0, feed.XrunCount);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void DeviceRecovery_RetriesAfterNoEndpointAndIgnoresDisposedRequests()
    {
        var format = new MixFormat(48000, 2);
        using var initial = new HeadlessAudioEndpoint(format);
        var session = new PcmAudioSession(format, initial.Sink, initial.Clock, 480, false);
        int attempts = 0;
        using var controller = new AudioDeviceController(session, () =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Endpoint temporarily absent.");
            return new HeadlessAudioEndpoint(format);
        });
        controller.MarkRunning();
        controller.OnDefaultDeviceChanged();
        // A throwing factory schedules the first ladder retry (250 ms) instead of faulting (Wavee #112).
        Assert.Equal(AudioDeviceState.Retrying, controller.State.Peek());
        Assert.True(controller.TryRunDueRetry(long.MaxValue));
        Assert.Equal(AudioDeviceState.Running, controller.State.Peek());
        Assert.Equal(2, attempts);
        controller.Dispose();
        controller.RequestRebuild();
        Assert.False(controller.TryRunDueRetry(long.MaxValue));
        Assert.Equal(2, attempts);
        _ = session.DisposeAsync();
    }

    /// <summary>A sink whose writes succeed but whose <c>Start()</c> reports the device lost — the WASAPI shape after a jack
    /// switch invalidates a client that was opened fine.</summary>
    private sealed class StartLostSink : IAudioSink
    {
        public MixFormat Format { get; }
        public int StartCalls;
        public StartLostSink(MixFormat format) => Format = format;
        public int Write(ReadOnlySpan<float> src, int frames) => frames;
        public void Start() { StartCalls++; throw new AudioDeviceLostException(unchecked((int)0x88890004)); }
        public void Stop() { }
    }

    /// <summary>Wavee #112: <c>Start()</c> throwing on the RT thread used to surface as a Decode/Retryable
    /// <see cref="MediaError"/> (a user-visible "playback error" toast). It is a SINK failure: no error, the session stays
    /// Playing, and the controller is asked for a rebuild.</summary>
    [Fact]
    public void StartFailure_IsASinkFailure_NotAMediaError()
    {
        var format = new MixFormat(48000, 2);
        using var clock = new HeadlessAudioEndpoint(format);
        var sink = new StartLostSink(format);
        var session = new PcmAudioSession(format, sink, clock.Clock, 480, false);
        session.Configure(AudioGraphSpec.Passthrough);
        using var controller = new AudioDeviceController(session, () => new HeadlessAudioEndpoint(format));
        session.RegisterDisposable(controller);   // no cold thread: the request just becomes visible as pending
        controller.MarkRunning();
        var core = new MediaPlayerCore();
        session.SetVoice(new MemoryAudioSource(new float[96000], 2), TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(core));
        _ = session.PlayAsync();

        session.PumpAudio(480);   // Opening → Buffering
        session.PumpAudio(480);   // Buffering → Ready → Playing; the first submit reaches Start() → device lost
        for (int i = 0; i < 4; i++) session.PumpAudio(480);

        Assert.True(sink.StartCalls >= 1);
        Assert.Null(core.Error.Peek());
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        Assert.True(controller.HasPendingRebuild, "a device-lost Start() did not ask the controller for a rebuild");
        Assert.Equal(1, controller.SinkFailureRequests);
        _ = session.DisposeAsync();
    }

    /// <summary>Wavee #112 case B: the running endpoint is invalidated with NO default-device notification. The rendered
    /// block must be retained (no content skipped — the mixer's consume clock holds), the controller asked exactly ONCE
    /// (further dead blocks never re-stamp the request), and a later rebuild resumes consumption.</summary>
    [Fact]
    public void InvalidatedEndpoint_RetainsPendingBlock_AndRequestsRebuildOnce()
    {
        var format = new MixFormat(48000, 2);
        var endpoint = new BufferedAudioEndpoint(format, 960, 48000);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false, endpoint);
        session.Configure(AudioGraphSpec.Passthrough);
        using var controller = new AudioDeviceController(session, () => new BufferedAudioEndpoint(format, 960, 48000));
        session.RegisterDisposable(controller);
        controller.MarkRunning();
        session.SetVoice(new MemoryAudioSource(new float[96000 * 4], 2), TimeSpan.FromSeconds(4), 192000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        for (int i = 0; i < 8; i++) { session.PumpAudio(480); endpoint.AdvanceHardware(480); }
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        long consumedBefore = session.SampleClock;
        Assert.True(consumedBefore > 0);

        endpoint.Invalidate();   // the jack switch: WritableFrames -1, Write 0, no watcher event

        for (int i = 0; i < 40; i++) session.RenderBlock(480);   // 5× the 8-block threshold worth of dead writes
        Assert.Equal(consumedBefore, session.SampleClock);       // nothing consumed into a dead sink
        Assert.True(controller.HasPendingRebuild);
        Assert.Equal(1, controller.SinkFailureRequests);         // reported once, never re-stamped
        Assert.Equal(AudioDeviceState.Running, controller.State.Peek());

        controller.OnDefaultDeviceChanged();                     // the cold thread would run this once the request is due
        Assert.Equal(AudioDeviceState.Running, controller.State.Peek());
        Assert.NotSame(endpoint, session.Sink);
        var fresh = (BufferedAudioEndpoint)session.Sink;
        for (int i = 0; i < 4; i++) { session.PumpAudio(480); fresh.AdvanceHardware(480); }
        Assert.True(session.SampleClock > consumedBefore, "consumption did not resume on the rebuilt sink");
        _ = session.DisposeAsync();
    }

    [Fact]
    public async Task RapidPauseResume_CompletesSupersededPauseWithoutStoppingPlayback()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 960, 48000);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false);
        session.SetVoice(new MemoryAudioSource(Enumerable.Repeat(.5f, 96000).ToArray(), 2),
            TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        await session.PlayAsync();
        for (int i = 0; i < 8; i++) { session.PumpAudio(480); endpoint.AdvanceHardware(480); }
        Task pause = session.PauseAsync().AsTask();
        Assert.False(pause.IsCompleted);
        await session.PlayAsync();
        await pause.WaitAsync(TimeSpan.FromSeconds(2));
        session.PumpAudio(480);
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        Assert.True(endpoint.IsStarted);
        await session.DisposeAsync();
    }

    [Fact]
    public void RejectedPrimaryCommand_DoesNotPublishOrTakeOwnership()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new HeadlessAudioEndpoint(format);
        var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, 480, false);
        using var feed = new AudioFeedThread(session, sampleRate: 48000);
        var source = new MemoryAudioSource(new float[48000], 2);
        session.SetVoice(source, TimeSpan.FromSeconds(.5), 24000, NormMode.Off, -14, 1);
        while (session.SetVoiceEnvelope(1, GainEnvelope.Constant)) { }
        var rejected = new RetirementSource();
        var previous = feed.RingsSnapshot[0].Ring;
        Assert.False(session.TrySetVoice(rejected, TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14, 1));
        Assert.Same(previous, feed.RingsSnapshot[0].Ring);
        Assert.False(rejected.Disposed);
        rejected.Dispose();
        _ = session.DisposeAsync();
    }

    [Fact]
    public async Task RingRetirement_CancelsReadButDisposesOnlyAfterProducerExits()
    {
        var source = new RetirementSource();
        var ring = new RingAudioSource(source, 2, 48000, 24000, 960);
        ring.StartProducer();
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(3)));
        Task<long> pendingSeek = ring.SeekFrameAsync(100);
        ring.Dispose();
        Assert.True(pendingSeek.IsCanceled);
        Assert.True(source.Cancelled);
        Assert.False(source.Disposed);
        source.Release.Set();
        for (int i = 0; i < 1000 && !source.Disposed; i++) await Task.Delay(1);
        Assert.True(source.Disposed);
    }

    private sealed class RetirementSource : IAudioSource, ICancellableAudioSource, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public volatile bool Disposed, Cancelled;
        public long PositionFrames => 0;
        public bool Exhausted => Release.IsSet;
        public GaplessInfo Gapless => GaplessInfo.None;
        public ReplayGainInfo Loudness => default;
        public int Read(Span<float> destination, int channels)
        {
            Entered.Set();
            Release.Wait();
            return 0;
        }
        public void CancelPendingRead() => Cancelled = true;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TransportRamp_RetargetsContinuouslyAndReachesBothEndpoints()
    {
        var ramp = new TransportRamp(1);
        ramp.Retarget(0, 10, 21);
        Assert.Equal(1f, ramp.At(10));
        Assert.Equal(0f, ramp.At(30));
        float halfway = ramp.At(20);
        ramp.Retarget(1, 20, 11);
        Assert.Equal(halfway, ramp.At(20));
        Assert.Equal(1f, ramp.At(30));
    }

    [Fact]
    public void PartialWrites_RetainEverySampleWithoutAdvancingDecoderTwice()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 8, 512) { MaximumWriteFrames = 3 };
        var session = new PcmAudioSession(format, endpoint, endpoint, 8, false);
        // The default graph ends in the lookahead limiter (2 ms = 96 frames at 48 kHz), which DELAYS everything it carries: the first
        // `latency` frames the device receives are the delay line's initial silence, and the payload leaves the graph `latency` frames
        // late. The voice therefore carries the payload plus `latency` frames of zeros after it, and the comparison below is shifted.
        int latency = session.Graph.Live.TotalLatencySamples;
        Assert.True(latency > 0);
        const int payloadFrames = 16;
        int totalFrames = latency + payloadFrames;
        var pcm = new float[totalFrames * 2];
        for (int i = 0; i < payloadFrames * 2; i++) pcm[i] = i * .001f;
        var voice = new MemoryAudioSource(pcm, 2);
        session.SetVoice(voice, TimeSpan.FromSeconds(1), totalFrames, NormMode.Off, -14, 1);
        Assert.Equal(3, session.RenderBlock(8));
        Assert.Equal(8, voice.PositionFrames);
        Assert.Equal(3, session.RenderBlock(8));
        Assert.Equal(8, voice.PositionFrames);
        Assert.Equal(2, session.RenderBlock(8));
        Assert.Equal(8, voice.PositionFrames);
        Assert.Equal(0, session.RenderBlock(8));
        Assert.Equal(8, session.DevicePaddingFrames);
        endpoint.Start();
        endpoint.AdvanceHardware(8);
        Assert.Equal(0, endpoint.PaddingFrames);
        // Diagnostics retain the last published clock sample; they do not synchronously query a live device.
        Assert.Equal(8, session.DevicePaddingFrames);
        Assert.Equal(8, session.SubmittedFrames);

        // Keep going the same way — every 8-frame block still reaches the device in 3-frame pieces — until the payload has come out the far
        // side of the limiter. The decoder only ever advances once per block: each frame is read exactly once.
        for (int i = 0; i < 1000 && endpoint.Captured.Length < totalFrames * 2; i++)
        {
            session.RenderBlock(8);
            endpoint.AdvanceHardware(8);
        }
        Assert.Equal(totalFrames * 2, endpoint.Captured.Length);
        Assert.Equal(totalFrames, voice.PositionFrames);
        Assert.Equal(totalFrames, session.SubmittedFrames);
        var captured = endpoint.Captured;
        Assert.All(captured[..(latency * 2)].ToArray(), v => Assert.Equal(0f, v));   // the limiter's initial delay line
        Assert.Equal(pcm.Take(payloadFrames * 2), captured.Slice(latency * 2, payloadFrames * 2).ToArray());
        _ = session.DisposeAsync();
    }

    [Fact]
    public void EmptyStartupRing_RemainsBufferingWithoutManufacturedUnderruns()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 4800);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false);
        using var feed = new AudioFeedThread(session, sampleRate: 48000);
        session.SetVoice(new MemoryAudioSource(new float[96000], 2), TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        for (int i = 0; i < 20; i++) { session.TickControl(480); feed.FeedOnce(); }
        Assert.Equal(PlaybackState.Buffering, session.CurrentState);
        Assert.Equal(0, feed.XrunCount);
        Assert.False(endpoint.IsStarted);
        feed.WorkerPumpOnce();
        Assert.Equal(PlaybackState.Playing, session.TickControl(480));
        feed.FeedOnce();
        Assert.True(endpoint.IsStarted);
        _ = session.DisposeAsync();
    }

    [Fact]
    public async Task Pause_DrainsSmoothedTailStopsDeviceAndFreezesClock()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 960, 48000);
        var session = new PcmAudioSession(format, endpoint, endpoint, 480, false);
        session.SetVoice(new MemoryAudioSource(Enumerable.Repeat(.5f, 96000).ToArray(), 2),
            TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        for (int i = 0; i < 8; i++) { session.PumpAudio(480); endpoint.AdvanceHardware(480); }
        Task pause = session.PauseAsync().AsTask();
        Assert.False(pause.IsCompleted);
        for (int i = 0; i < 8; i++) { session.PumpAudio(480); endpoint.AdvanceHardware(480); }
        await pause.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PlaybackState.Paused, session.CurrentState);
        Assert.False(endpoint.IsStarted);
        Assert.Equal(0, endpoint.PaddingFrames);
        Assert.Equal(0f, endpoint.Captured[^1]);
        endpoint.TryGetPlayed(out long paused, out _);
        endpoint.AdvanceHardware(48000);
        endpoint.TryGetPlayed(out long later, out _);
        Assert.Equal(paused, later);
        _ = session.PlayAsync();
        session.PumpAudio(480);
        session.PumpAudio(480);
        Assert.True(endpoint.IsStarted);
        Assert.Equal(0, endpoint.ResetCount);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void PreparedRing_IsAdoptedWithoutLosingItsPreroll()
    {
        var format = new MixFormat(48000, 2);
        var source = new MemoryAudioSource(new float[48000], 2);
        var ring = new RingAudioSource(source, 2, 48000, 24000, 960);
        ring.PumpAhead();
        var prepared = new AudioPreparedItem(ring, source.Gapless, source.Loudness, 24000, TimeSpan.FromSeconds(.5), 48000);
        Assert.True(prepared.IsReady);
        using var endpoint = new HeadlessAudioEndpoint(format);
        var session = new PcmAudioSession(format, endpoint.Sink, endpoint.Clock, 480, false);
        using var feed = new AudioFeedThread(session, sampleRate: 48000);
        session.AddCrossfadeVoice(prepared.AudioVoice!, GainEnvelope.Constant, 0, 1, null, 7);
        Assert.Same(ring, feed.RingsSnapshot[0].Ring);
        Assert.Equal(24000, feed.RingsSnapshot[0].Ring.BufferedFrames);
        _ = session.DisposeAsync();
    }

    [Fact]
    public void TransitionCancellation_IsAtomicAndLeavesOutgoingGainUnchanged()
    {
        var gate = new AudioTransitionGate(100);
        var outgoing = GainEnvelope.Fade(FadeKind.Out, 100, 10, CrossCurve.EqualPower).WithTransition(gate);
        var incoming = GainEnvelope.Fade(FadeKind.In, 100, 10, CrossCurve.EqualPower).WithTransition(gate);
        Assert.True(gate.TryCancel());
        Assert.Equal(1f, outgoing.GainAt(105));
        Assert.Equal(0f, incoming.GainAt(105));
        Assert.False(gate.IsCommitted);
        var committed = new AudioTransitionGate(100);
        GainEnvelope.Constant.WithTransition(committed).GainAt(100);
        Assert.True(committed.IsCommitted);
        Assert.False(committed.TryCancel());
    }

    [Fact]
    public void ClockProjection_CannotExtrapolatePastSubmittedAudioOrWhileStopped()
    {
        var endpoint = new BufferedAudioEndpoint(new MixFormat(48000, 2), 4800);
        endpoint.Write(new float[9600], 4800);
        endpoint.Start();
        endpoint.AdvanceHardware(2400);
        var position = new AudioClockPosition { SubmittedFrameLimit = 4800 };
        position.Sample(endpoint);
        Assert.Equal(TimeSpan.FromMilliseconds(100), position.Project(TimeSpan.TicksPerSecond));
        position.IsAdvancing = false;
        Assert.Equal(TimeSpan.FromMilliseconds(50), position.Project(TimeSpan.TicksPerSecond));
    }
}
