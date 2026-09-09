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
        Assert.Equal(AudioDeviceState.Faulted, controller.State.Peek());
        controller.OnDefaultDeviceChanged();
        Assert.Equal(AudioDeviceState.Running, controller.State.Peek());
        Assert.Equal(2, attempts);
        controller.Dispose();
        controller.RequestRebuild();
        Assert.Equal(2, attempts);
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
    public void Starvation_FreezesContentTimeline_ThenRebasesHardwareWithoutSkippingPcm()
    {
        var format = new MixFormat(48000, 2);
        using var endpoint = new BufferedAudioEndpoint(format, 32, 512);
        var session = new PcmAudioSession(format, endpoint, endpoint, 16, false);
        using var feed = new AudioFeedThread(session, blockFrames: 16, ringFrames: 256, targetAheadFrames: 96);
        session.SetVoice(new MemoryAudioSource(new float[96000], 2), TimeSpan.FromSeconds(1), 48000, NormMode.Off, -14, 1);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        feed.WorkerPumpOnce();
        session.TickControl(16);
        session.TickControl(16);
        for (int i = 0; i < 12; i++)
        {
            feed.FeedOnce();
            endpoint.AdvanceHardware(16);
            session.TickControl(16);
        }
        long stoppedAt = session.SampleClock;
        long readAt = feed.RingsSnapshot[0].Ring.PositionFrames;
        Assert.Equal(96, stoppedAt);
        Assert.False(endpoint.IsStarted);
        Assert.Equal(PlaybackState.Stalled, session.CurrentState);
        endpoint.AdvanceHardware(96000);
        for (int i = 0; i < 12; i++) feed.FeedOnce();
        Assert.Equal(stoppedAt, session.SampleClock);
        Assert.Equal(readAt, feed.RingsSnapshot[0].Ring.PositionFrames);
        feed.WorkerPumpOnce();
        feed.FeedOnce();
        endpoint.AdvanceHardware(16);
        session.TickControl(16);
        Assert.Equal(stoppedAt + 16, session.SampleClock);
        Assert.Equal(readAt + 16, feed.RingsSnapshot[0].Ring.PositionFrames);
        Assert.Equal(stoppedAt + 16, session.PlayedFrames);
        Assert.Equal(1, endpoint.ResetCount);
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
        _ = session.DisposeAsync();
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
        using var endpoint = new BufferedAudioEndpoint(format, 8, 32) { MaximumWriteFrames = 3 };
        var pcm = Enumerable.Range(0, 32).Select(i => i * .001f).ToArray();
        var voice = new MemoryAudioSource(pcm, 2);
        var session = new PcmAudioSession(format, endpoint, endpoint, 8, false);
        session.SetVoice(voice, TimeSpan.FromSeconds(1), 16, NormMode.Off, -14, 1);
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
        Assert.Equal(pcm.Take(16), endpoint.Captured.ToArray());
        Assert.Equal(8, session.SubmittedFrames);
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
