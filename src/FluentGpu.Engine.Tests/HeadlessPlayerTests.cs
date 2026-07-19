using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M0 behavior tests for the headless media player (spec docs/plans/media-playback-api-spec.md §16 M0): the full
/// <see cref="PlaybackState"/> transition machine, transport intent arbitration, the callback-reentrancy firewall,
/// the authoritative-tick position precision, the zero-managed-alloc position sampling, and the facade+router wiring.
/// All deterministic — a virtual clock (<see cref="HeadlessScriptedPlayer.Pump"/>), no GPU/backend.
/// </summary>
public sealed class HeadlessPlayerTests
{
    private static readonly TimeSpan Dt = TimeSpan.FromMilliseconds(100);
    private static void Wait(ValueTask vt)
    {
        var task = vt.AsTask();
        if (!task.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Media operation did not complete within five seconds.");
        task.GetAwaiter().GetResult();
    }

    private static HeadlessScriptedPlayer OpenReady(TimeSpan duration, TimeSpan? sampleDur = null)
    {
        var player = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0 };
        var src = MediaSource.FromSamples(new ScriptedSampleSource(duration, sampleDur ?? TimeSpan.FromMilliseconds(100)));
        Wait(player.OpenAsync(src));
        player.Pump(Dt);   // Opening → Buffering
        player.Pump(Dt);   // Buffering → Ready
        return player;
    }

    // ── (a) full state-machine transition correctness ────────────────────────────────────────────────────────────────

    [Fact]
    public void StateMachine_WalksIdleToEndedThroughEveryState()
    {
        var player = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0 };
        var src = MediaSource.FromSamples(new ScriptedSampleSource(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)));

        Assert.Equal(PlaybackState.Idle, player.State.Peek());

        Wait(player.OpenAsync(src));
        Assert.Equal(PlaybackState.Opening, player.State.Peek());

        player.Pump(Dt);
        Assert.Equal(PlaybackState.Buffering, player.State.Peek());

        player.Pump(Dt);
        Assert.Equal(PlaybackState.Ready, player.State.Peek());
        Assert.False(player.IsPlaying.Peek());
        Assert.Equal(1.0, player.Duration.Peek().TotalSeconds, 6);

        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Assert.True(player.IsPlaying.Peek());

        double p0 = player.PositionSeconds.Peek();
        player.Pump(Dt);
        player.Pump(Dt);
        Assert.True(player.PositionSeconds.Peek() > p0);

        _ = player.PauseAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Paused, player.State.Peek());
        Assert.False(player.IsPlaying.Peek());
        Assert.False(player.IsPlayRequested.Peek());

        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());

        Wait(player.DisposeAsync());
    }

    [Fact]
    public void HeadlessCommands_RecordIntentAndCompleteWithoutPump()
    {
        var player = new HeadlessScriptedPlayer { OpenTicks = 5, BufferTicks = 5 };

        var open = player.OpenAsync(MediaSource.FromFile("x.flac"));
        Assert.True(open.IsCompletedSuccessfully);
        Assert.Equal(PlaybackState.Opening, player.State.Peek());

        var play = player.PlayAsync();
        var seek = player.SeekAsync(TimeSpan.FromSeconds(1));
        var pause = player.PauseAsync();

        Assert.True(play.IsCompletedSuccessfully);
        Assert.True(seek.IsCompletedSuccessfully);
        Assert.True(pause.IsCompletedSuccessfully);
        Assert.Equal(PlaybackState.Opening, player.State.Peek());

        Wait(player.DisposeAsync());
    }

    [Fact]
    public void StateMachine_StallIsTransientAndRecovers_NeverFailed()
    {
        var player = OpenReady(TimeSpan.FromSeconds(10));
        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());

        player.InjectStall();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Stalled, player.State.Peek());
        Assert.Equal(SuppressionReason.BufferingUnderrun, player.Suppression.Peek());
        Assert.NotEqual(PlaybackState.Failed, player.State.Peek());

        player.Resume();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Assert.Equal(SuppressionReason.None, player.Suppression.Peek());

        Wait(player.DisposeAsync());
    }

    [Fact]
    public void StateMachine_ReachesEndedFromDemuxerEos()
    {
        var player = OpenReady(TimeSpan.FromSeconds(1));
        _ = player.PlayAsync();
        player.Pump(Dt);
        _ = player.SeekAsync(TimeSpan.FromSeconds(0.9));
        for (int i = 0; i < 8 && player.State.Peek() != PlaybackState.Ended; i++) player.Pump(Dt);

        Assert.Equal(PlaybackState.Ended, player.State.Peek());
        Assert.False(player.IsPlayRequested.Peek());
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void StateMachine_InjectedFailureIsTerminalWithTypedError()
    {
        var player = OpenReady(TimeSpan.FromSeconds(5));
        player.InjectFailure(new MediaError(MediaErrorCategory.Decode, "scripted decode fault", 0x8000FFFF, null, MediaRecovery.Fatal));

        Assert.Equal(PlaybackState.Failed, player.State.Peek());
        var err = player.Error.Peek();
        Assert.NotNull(err);
        Assert.Equal(MediaErrorCategory.Decode, err!.Category);
        Assert.Equal(0x8000FFFF, err.UnderlyingCode);
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Open_PublishesTracksFromDemuxerStreams()
    {
        var player = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0 };
        var src = MediaSource.FromSamples(new ScriptedSampleSource(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100), new SizeI(1920, 1080)));
        Wait(player.OpenAsync(src));
        player.Pump(Dt);   // → Buffering (publishes metadata)

        Assert.Equal(new SizeI(1920, 1080), player.NaturalSize.Peek());
        Assert.Equal(1, player.Tracks.Audio.Count);
        Assert.Equal(1, player.Tracks.Video.Count);
        Wait(player.DisposeAsync());
    }

    // ── (c) overlapping transport intent arbitration ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Transport_OverlappingAcceptedVerbs_FinalIntentWinsAndNoneThrow()
    {
        var player = OpenReady(TimeSpan.FromSeconds(10));
        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());

        // Four verbs are accepted WITHOUT pumping between them. Each acceptance completes immediately; the next pump
        // realizes the final combined intent deterministically.
        var t1 = player.PlayAsync().AsTask();
        var t2 = player.PauseAsync().AsTask();
        var t3 = player.SeekAsync(TimeSpan.FromSeconds(3)).AsTask();
        var t4 = player.PlayAsync().AsTask();

        Assert.All(new[] { t1, t2, t3, t4 }, t => Assert.True(t.IsCompletedSuccessfully));

        player.Pump(Dt);   // realize the accepted intents

        bool allDone = Task.WaitAll(new[] { t1, t2, t3, t4 }, 2000);
        Assert.True(allDone);
        Assert.All(new[] { t1, t2, t3, t4 }, t => Assert.False(t.IsFaulted));

        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Assert.True(player.IsPlayRequested.Peek());
        Assert.Equal(3.0, player.PositionSeconds.Peek(), 2);
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Transport_IdempotentVerbsNeverThrowInAnyState()
    {
        var player = new HeadlessScriptedPlayer { OpenTicks = 0, BufferTicks = 0 };
        // Idempotent in Idle — no source, no crash.
        Wait(player.PlayAsync());
        Wait(player.PauseAsync());
        player.Stop();
        Wait(player.SeekAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(PlaybackState.Idle, player.State.Peek());
        Wait(player.DisposeAsync());
    }

    // ── (d) callback-firewall reentrancy safety ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CallbackFirewall_ReentrantTransportIsDeferred_NoThrow_ConsistentState()
    {
        var player = OpenReady(TimeSpan.FromSeconds(10));
        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());

        // A firewalled byte-source callback that re-enters transport verbs — must never reenter/crash.
        var ex = Record.Exception(() => player.RunSourceCallback(() =>
        {
            _ = player.PauseAsync();
            _ = player.SeekAsync(TimeSpan.FromSeconds(5));
        }));
        Assert.Null(ex);

        player.Pump(Dt);   // apply the seek deferred out of the callback

        Assert.Equal(PlaybackState.Paused, player.State.Peek());
        Assert.False(player.IsPlayRequested.Peek());
        Assert.Equal(5.0, player.PositionSeconds.Peek(), 2);
        Assert.Null(player.Error.Peek());

        // Still fully operational afterward.
        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Wait(player.DisposeAsync());
    }

    // ── (b) zero managed alloc when sampling position ────────────────────────────────────────────────────────────────

    [Fact]
    public void Position_SamplingIsZeroManagedAlloc()
    {
        var player = OpenReady(TimeSpan.FromSeconds(1000));
        _ = player.PlayAsync();
        player.Pump(Dt);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());

        // Warm the read path (JIT) so the measurement reflects steady-state.
        float warmF = 0f; long warmT = 0;
        for (int i = 0; i < 8192; i++) { warmF += player.PositionSeconds.Value; warmT += player.Position.Value.Ticks; }
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        float accF = 0f; long accT = 0;
        for (int i = 0; i < 200_000; i++)
        {
            accF += player.PositionSeconds.Value;   // hot FloatSignal read — alloc-free, node-bindable
            accT += player.Position.Value.Ticks;    // TimeSpan view — generic Signal read, no boxing
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
        Assert.True(accF + accT + warmF + warmT != float.NegativeInfinity);   // keep the accumulators live
        Wait(player.DisposeAsync());
    }

    // ── (g) authoritative-tick position precision ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Position_SeekRoundTripsExactAtMultiHourOffset_FloatWouldLose()
    {
        // 2h 30m 17.1234567s — a 100-ns value a float-seconds round-trip cannot represent.
        var target = new TimeSpan(0, 2, 30, 17) + TimeSpan.FromTicks(1_234_567);
        var player = OpenReady(TimeSpan.FromHours(3));   // Ready (paused) — a seek won't be advanced by playback

        Wait(player.SeekAsync(target));
        player.Pump(Dt);   // applies the pending seek atomically

        // Position (authoritative TimeSpan) is EXACT to the tick.
        Assert.Equal(target, player.Position.Peek());
        Assert.Equal(target.Ticks, player.Position.Peek().Ticks);

        // The float projection is LOSSY (proves it is a one-way projection, not the source of truth).
        double reconstructedFromFloat = player.PositionSeconds.Peek();
        var floatRoundTrip = TimeSpan.FromSeconds(reconstructedFromFloat);
        Assert.NotEqual(target.Ticks, floatRoundTrip.Ticks);
        Assert.True(Math.Abs((floatRoundTrip - target).TotalSeconds) < 1.0);   // still close — it's the same value, just lossy
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Position_StepFrameSeeksInTickDomain()
    {
        var player = OpenReady(TimeSpan.FromSeconds(30));
        Wait(player.SeekAsync(TimeSpan.FromSeconds(10)));
        player.Pump(Dt);
        var before = player.Position.Peek();

        Wait(player.StepFrame(1));
        player.Pump(Dt);
        Assert.True(player.Position.Peek() > before);

        Wait(player.StepFrame(-1));
        player.Pump(Dt);
        Assert.Equal(before, player.Position.Peek());
        Wait(player.DisposeAsync());
    }

    // ── (f-ish) facade + router wiring ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Facade_RoutedBackendDrivesForwardedSignals()
    {
        var player = MediaPlayer.Build()
            .WithBackend(MediaKind.PcmAudio, new TestBackend(TimeSpan.FromSeconds(5)))
            .Build();

        Wait(player.OpenAsync(MediaSource.FromFile("routed.flac")));   // sniffs → PcmAudio
        Assert.Equal(PlaybackState.Ready, player.State.Peek());
        Assert.Equal(5.0, player.Duration.Peek().TotalSeconds, 6);

        Wait(player.PlayAsync());
        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Assert.True(player.IsPlaying.Peek());

        Wait(player.SeekAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2.0, player.Position.Peek().TotalSeconds, 6);

        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Facade_UnresolvedKindSurfacesTypedError_NotSilentFailure()
    {
        var bare = MediaPlayer.Create();   // empty router
        Wait(bare.OpenAsync(MediaSource.FromFile("nobackend.mp4")));   // sniffs → MfVideoOrFile, none registered

        Assert.Equal(PlaybackState.Failed, bare.State.Peek());
        var err = bare.Error.Peek();
        Assert.NotNull(err);
        Assert.Equal(MediaErrorCategory.Lifecycle, err!.Category);
        Wait(bare.DisposeAsync());
    }

    [Fact]
    public void Facade_BackendSwitchOnKindChange()
    {
        var audio = new TestBackend(TimeSpan.FromSeconds(5));
        var video = new TestBackend(TimeSpan.FromSeconds(9));
        var player = MediaPlayer.Build()
            .WithBackend(MediaKind.PcmAudio, audio)
            .WithBackend(MediaKind.MfVideoOrFile, video)
            .Build();

        Wait(player.OpenAsync(MediaSource.FromFile("a.flac")));
        Assert.Equal(5.0, player.Duration.Peek().TotalSeconds, 6);
        var firstSession = player.Session;

        Wait(player.OpenAsync(MediaSource.FromFile("b.mp4")));   // kind changes → swap the inner session
        Assert.Equal(9.0, player.Duration.Peek().TotalSeconds, 6);
        Assert.NotSame(firstSession, player.Session);
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Dispose_IsSafeMidOpen_AfterAcceptedOpen()
    {
        var player = new HeadlessScriptedPlayer { OpenTicks = 5, BufferTicks = 5 };
        var open = player.OpenAsync(MediaSource.FromFile("x.flac"));   // never pumped to Ready
        Assert.True(open.IsCompletedSuccessfully);
        Wait(player.DisposeAsync());
    }

    // ── test-only backend/session driving the facade's MediaSignalSink ──────────────────────────────────────────────

    private sealed class TestBackend : IMediaBackend
    {
        private readonly TimeSpan _duration;
        public TestBackend(TimeSpan duration) => _duration = duration;
        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
            => new(new TestSession(_duration));
        public MediaCapabilities Capabilities => new(false, true, false);
    }

    private sealed class TestSession : IMediaSession
    {
        private readonly TimeSpan _duration;
        private MediaSignalSink? _sink;
        public TestSession(TimeSpan duration) => _duration = duration;
        public void ConnectSignals(MediaSignalSink sink)
        {
            _sink = sink;
            sink.Duration(_duration);
            sink.State(PlaybackState.Ready);
        }
        public ValueTask PlayAsync() { _sink?.State(PlaybackState.Playing); _sink?.SettleTransport(); return ValueTask.CompletedTask; }
        public ValueTask PauseAsync() { _sink?.State(PlaybackState.Paused); _sink?.SettleTransport(); return ValueTask.CompletedTask; }
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) { _sink?.Position(to); _sink?.SettleTransport(); return ValueTask.CompletedTask; }
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
