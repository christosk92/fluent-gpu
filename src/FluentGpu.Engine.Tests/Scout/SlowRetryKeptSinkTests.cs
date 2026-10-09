using System;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Wavee #112 case A under the slow retry: the new default device is Active but refuses Initialize while the kept speakers
/// sink still plays. Every attempt parks the RT feed around a synchronous open, so a 5 s retry past the ladder drained the
/// audible sink's buffer every 5 s for as long as the listener kept playing. Past the ladder the controller now goes
/// Faulted while the kept sink is live, and keeps the slow retry only when the kept sink is dead (nothing audible to protect).
/// </summary>
public sealed class SlowRetryKeptSinkTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

    // The kept endpoint; Lost models a running WASAPI client that MarkLost invalidated (IsReady false).
    private sealed class KeptEndpoint : IAudioEndpoint
    {
        private readonly HeadlessAudioEndpoint _inner = new(Fmt, warmupFrames: 0);
        public bool Lost;
        public IAudioSink Sink => _inner.Sink;
        public IAudioClockSource Clock => _inner.Clock;
        public bool IsReady => !Lost;
        public void Dispose() { }
    }

    private static PcmAudioSession PlayingSession(KeptEndpoint kept)
    {
        var session = new PcmAudioSession(Fmt, kept.Sink, kept.Clock, maxBlock: 256, driveWithOwnThread: false, kept);
        session.Configure(AudioGraphSpec.Passthrough);
        var voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * 10], 2);   // 10 s
        session.SetVoice(voice, TimeSpan.FromSeconds(10), Fmt.SampleRate * 10, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        session.PumpAudio(256);   // Opening → Buffering
        session.PumpAudio(256);   // Buffering → Ready → Playing
        return session;
    }

    // Default-device event + the three ladder retries against an endpoint that never becomes Initialize-able.
    private static AudioDeviceController ExhaustLadder(PcmAudioSession session, Func<int> opened)
    {
        var ctrl = new AudioDeviceController(session, () =>
        {
            opened();
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, ready: false);
        });
        ctrl.MarkRunning();
        ctrl.OnDefaultDeviceChanged();                     // attempt 1 → 250 ms
        for (int i = 0; i < 3; i++)
            Assert.True(ctrl.TryRunDueRetry(long.MaxValue)); // attempts 2-4 → 1 s, 3 s, exhausted
        return ctrl;
    }

    [Fact]
    public void LadderExhausted_WhileTheKeptSinkStillPlays_StopsRetrying()
    {
        var kept = new KeptEndpoint();
        var session = PlayingSession(kept);
        Assert.True(session.WantsOutput);
        int opens = 0;
        using var ctrl = ExhaustLadder(session, () => ++opens);

        Assert.Equal(AudioDeviceState.Faulted, ctrl.State.Peek());
        Assert.False(ctrl.TryRunDueRetry(long.MaxValue));   // no 5 s retry parking the feed under the audible sink
        Assert.Equal(4, opens);
        Assert.Same(kept.Sink, session.Sink);
    }

    [Fact]
    public void LadderExhausted_WithADeadKeptSink_KeepsTheSlowRetry()
    {
        var kept = new KeptEndpoint();
        var session = PlayingSession(kept);
        kept.Lost = true;   // the running client was invalidated: silence until a retry lands
        int opens = 0;
        using var ctrl = ExhaustLadder(session, () => ++opens);

        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));    // the slow retry is scheduled and runs
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.Equal(5, opens);
    }
}
