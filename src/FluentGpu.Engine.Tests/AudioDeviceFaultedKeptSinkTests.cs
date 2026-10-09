using System;
using FluentGpu.Media;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A playing session whose kept sink dies AFTER the ladder ran out (Wavee #112). The ladder exhausts while the kept sink is
/// still live, so the controller goes <c>Faulted</c> with the session still wanting sound. When that sink dies later (its
/// USB DAC is unplugged while the refusing default stays), the render path's report must start one attempt rather than be
/// dropped: otherwise the session plays in silence until a pause/play or restart.
/// </summary>
public sealed class AudioDeviceFaultedKeptSinkTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

    // A kept endpoint whose liveness the test can flip: the session reads it through IAudioEndpoint.IsReady (OutputLive).
    private sealed class LossyEndpoint : IAudioEndpoint
    {
        private readonly HeadlessAudioEndpoint _inner = new(Fmt, warmupFrames: 0, latencyFrames: 100);
        public bool Lost;
        public IAudioSink Sink => _inner.Sink;
        public IAudioClockSource Clock => _inner.Clock;
        public bool IsReady => !Lost;
        public void Dispose() { }
    }

    private static PcmAudioSession PlayingSession(LossyEndpoint ep, out MemoryAudioSource voice)
    {
        var session = new PcmAudioSession(Fmt, ep.Sink, ep.Clock, maxBlock: 256, driveWithOwnThread: false, ep);
        session.Configure(AudioGraphSpec.Passthrough);
        voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * 10], 2);   // 10 s
        session.SetVoice(voice, TimeSpan.FromSeconds(10), Fmt.SampleRate * 10, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        session.PumpAudio(256);   // Opening → Buffering
        session.PumpAudio(256);   // Buffering → Ready → Playing
        for (int i = 0; i < 50; i++) session.PumpAudio(256);
        return session;
    }

    [Fact]
    public void PlayingSession_LadderExhaustedOnLiveKeptSink_GoesFaulted_ThenKeptSinkDies_SinkFailureStartsARequest()
    {
        var kept = new LossyEndpoint();
        var session = PlayingSession(kept, out _);
        Assert.True(session.WantsOutput);
        Assert.True(session.OutputLive);

        using var ctrl = new AudioDeviceController(session, () => new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, ready: false));
        ctrl.MarkRunning();

        ctrl.OnDefaultDeviceChanged();                                    // attempt 1 → Retrying (250 ms)
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                  // attempt 2 → Retrying (1 s)
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                  // attempt 3 → Retrying (3 s)
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                  // attempt 4 → ladder exhausted
        Assert.Equal(AudioDeviceState.Faulted, ctrl.State.Peek());        // the kept sink is still live: no slow retry
        Assert.False(ctrl.TryRunDueRetry(long.MaxValue));

        // While the kept sink still plays, a dead-write report from it is still ignored (no attempt every report).
        ctrl.ReportSinkFailure();
        Assert.False(ctrl.HasPendingRebuild);

        // The kept sink dies later: the render path reports it, and the Faulted controller re-enters the machine.
        kept.Lost = true;
        ctrl.ReportSinkFailure();
        Assert.True(ctrl.HasPendingRebuild);
        Assert.Equal(1, ctrl.SinkFailureRequests);

        // A second report while that request is pending does not start another one.
        ctrl.ReportSinkFailure();
        Assert.Equal(1, ctrl.SinkFailureRequests);

        // The attempt that request runs finds the default still refusing and the ladder spent: with sound wanted and the
        // kept sink dead it lands on the slow retry (Retrying), never back in Faulted where the next report would be dropped.
        ctrl.OnDefaultDeviceChanged();
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.False(ctrl.TryRunDueRetry(Environment.TickCount64));          // not due before its 5 s
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                     // and it keeps trying once due
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
    }
}
