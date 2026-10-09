using System;
using System.Threading;
using FluentGpu.Media;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M4 device-loss / follow-default state-machine tests (spec §7.9) — <c>{Building, Running, Reinitializing, Retrying, Faulted}</c>
/// driven by a FAKE <see cref="IDeviceWatcher"/> + fake endpoints (no real WASAPI). A default-device change rebuilds ONLY
/// the sink under a live graph: sources, mixer voices, and the derived position SURVIVE, and latency is re-measured. A
/// not-ready endpoint keeps the old sink and retries on the ladder (Wavee #112). Deterministic where possible (drive
/// <see cref="AudioDeviceController.OnDefaultDeviceChanged"/> / <c>TryRunDueRetry</c> directly); the cold-thread tests are
/// bounded + hard-timeout'd via a <see cref="ManualResetEventSlim"/>.
/// </summary>
public sealed class AudioDeviceStateMachineTests
{
    private static readonly MixFormat Fmt = new(48000, 2);

    private sealed class FakeDeviceWatcher : IDeviceWatcher
    {
        private readonly Signal<AudioDeviceState> _state = new(AudioDeviceState.Running);
        public IReadSignal<AudioDeviceState> State => _state;
        public event Action? DefaultDeviceChanged;
        public void Raise() => DefaultDeviceChanged?.Invoke();
        public void SetState(AudioDeviceState s) => _state.Value = s;
    }

    private static PcmAudioSession PlayingSession(out MemoryAudioSource voice, long latencyFrames = 100)
    {
        var session = LoadedSession(out voice, latencyFrames);
        _ = session.PlayAsync();
        session.PumpAudio(256);   // Opening → Buffering
        session.PumpAudio(256);   // Buffering → Ready → Playing
        for (int i = 0; i < 50; i++) session.PumpAudio(256);   // advance the clock so position > 0
        return session;
    }

    // A track loaded but never played: the session does not want sound, so an exhausted ladder goes Faulted (a playing
    // session keeps a 5 s slow retry instead, see PlayingSession_PastTheLadder_KeepsASlowRetry).
    private static PcmAudioSession LoadedSession(out MemoryAudioSource voice, long latencyFrames = 100)
    {
        var ep = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, latencyFrames: latencyFrames);
        var session = new PcmAudioSession(Fmt, ep.Sink, ep.Clock, maxBlock: 256, driveWithOwnThread: false, ep);
        session.Configure(AudioGraphSpec.Passthrough);
        voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * 10], 2);   // 10 s
        session.SetVoice(voice, TimeSpan.FromSeconds(10), Fmt.SampleRate * 10, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        return session;
    }

    [Fact]
    public void Controller_BuildingToRunning()
    {
        var session = PlayingSession(out _);
        var watcher = new FakeDeviceWatcher();
        using var ctrl = new AudioDeviceController(session, () => new HeadlessAudioEndpoint(Fmt, warmupFrames: 0), watcher);
        Assert.Equal(AudioDeviceState.Building, ctrl.State.Peek());
        ctrl.MarkRunning();
        Assert.Equal(AudioDeviceState.Running, ctrl.State.Peek());
    }

    [Fact]
    public void DefaultDeviceChange_Reinitializing_Then_Running_RebuildsOnlySink_SourcesSurvive()
    {
        var session = PlayingSession(out var voice, latencyFrames: 100);
        var watcher = new FakeDeviceWatcher();

        int voicesBefore = session.Mixer.VoiceCount;
        var srcBefore = session.Mixer.VoicesSpan[0].Src;
        long posBefore = session.PositionTracker.PlayedFramesCompensated;
        Assert.True(posBefore > 0);

        bool sawReinitializing = false;
        AudioDeviceController? c = null;
        c = new AudioDeviceController(session, () =>
        {
            // The rebuild opens the new endpoint WHILE the state is Reinitializing (proves the transition happened).
            if (c!.State.Peek() == AudioDeviceState.Reinitializing) sawReinitializing = true;
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, latencyFrames: 250);   // a DIFFERENT device latency
        }, watcher);
        var ctrl = c;
        ctrl.MarkRunning();

        ctrl.OnDefaultDeviceChanged();   // the deterministic cold-thread body

        Assert.True(sawReinitializing, "did not pass through Reinitializing");
        Assert.Equal(AudioDeviceState.Running, ctrl.State.Peek());

        // ONLY the sink was rebuilt — the sources/voices survive (same instance, same count).
        Assert.Equal(voicesBefore, session.Mixer.VoiceCount);
        Assert.Same(srcBefore, session.Mixer.VoicesSpan[0].Src);

        // Position continues across the rebuild, and the NEW device's latency is re-measured.
        for (int i = 0; i < 20; i++) session.PumpAudio(256);
        Assert.Equal(250, session.PositionTracker.StreamLatencyFrames);
        long posAfter = session.PositionTracker.PlayedFramesCompensated;
        Assert.True(posAfter > posBefore, $"position did not survive/continue: before={posBefore} after={posAfter}");

        ctrl.Dispose();
    }

    /// <summary>Device-reopen gapless bug (spec §7.9 Fix 2, root cause A4): a rebuild that adopts an endpoint clocking at a
    /// DIFFERENT rate must both raise <see cref="PcmAudioSession.DeviceFormatChanged"/> (driving the host's soft reload)
    /// AND already report the new rate off <see cref="PcmAudioSession.Format"/> the instant it's raised — a racing
    /// `PrepareContext.For(session.Format)` (e.g. a queue preroll) must never see the stale rate.</summary>
    [Fact]
    public void DefaultDeviceChange_DifferentRate_RaisesDeviceFormatChanged_AndUpdatesFormat()
    {
        var session = PlayingSession(out _);
        var watcher = new FakeDeviceWatcher();
        Assert.Equal(48000, session.Format.SampleRate);

        MixFormat? raised = null;
        session.DeviceFormatChanged += fmt => raised = fmt;

        using var ctrl = new AudioDeviceController(session,
            () => new HeadlessAudioEndpoint(new MixFormat(44100, 2), warmupFrames: 0), watcher);
        ctrl.MarkRunning();

        ctrl.OnDefaultDeviceChanged();   // the deterministic cold-thread body

        Assert.Equal(AudioDeviceState.Running, ctrl.State.Peek());

        // Format is stamped SYNCHRONOUSLY inside RebuildSink, before the event fires (Fix 2) — no wait needed for it.
        Assert.Equal(44100, session.Format.SampleRate);

        // DeviceFormatChanged itself is raised fire-and-forget on the thread pool — bounded wait for it to land.
        Assert.True(SpinWaitFor(() => raised is not null, TimeSpan.FromSeconds(5)), "DeviceFormatChanged was not raised");
        Assert.Equal(44100, raised!.Value.SampleRate);

        ctrl.Dispose();
    }

    /// <summary>Device-reopen gapless bug (spec §7.9 Fix 4, root cause A4): the OS raises the default-device-changed
    /// notification before the new endpoint's mix format is settled, so one physical device switch fires TWO
    /// notifications a close moments apart (observed: 48000 then 44100 within ~800 ms). Two notifications inside the
    /// 250 ms debounce window must fold into exactly ONE rebuild, not two.</summary>
    [Fact]
    public void Debounce_TwoNotificationsWithin250ms_RebuildOnce()
    {
        var session = PlayingSession(out _);
        var watcher = new FakeDeviceWatcher();
        int rebuilds = 0;
        using var firstRebuild = new ManualResetEventSlim(false);

        using var ctrl = new AudioDeviceController(session, () =>
        {
            if (Interlocked.Increment(ref rebuilds) == 1) firstRebuild.Set();
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0);
        }, watcher);
        ctrl.MarkRunning();
        ctrl.Start();

        watcher.Raise();       // notification #1
        Thread.Sleep(50);      // well inside the 250 ms debounce window
        watcher.Raise();       // notification #2 — re-arms the window instead of queuing a second rebuild

        Assert.True(firstRebuild.Wait(TimeSpan.FromSeconds(5)), "the debounced rebuild never landed");
        // Bounded settle past the debounce window: a second (wrongly un-coalesced) rebuild would show up here.
        Thread.Sleep(600);
        Assert.Equal(1, Volatile.Read(ref rebuilds));
    }

    /// <summary>Wavee #112: a rebuild with no endpoint (a throwing factory) no longer faults on the spot — it keeps the old
    /// sink and enters the 250 ms / 1 s / 3 s retry ladder; only an exhausted ladder is <c>Faulted</c>.</summary>
    [Fact]
    public void FatalRebuild_NoEndpoint_EntersTheRetryLadder()
    {
        var session = PlayingSession(out _);
        var watcher = new FakeDeviceWatcher();
        using var ctrl = new AudioDeviceController(session, () => throw new InvalidOperationException("all devices gone"), watcher);
        ctrl.MarkRunning();
        ctrl.OnDefaultDeviceChanged();
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
    }

    /// <summary>Wavee #112: <c>Faulted</c> is no longer terminal — the next default-device event re-enters the machine
    /// (all devices gone → a device comes back must resume without a track change).</summary>
    [Fact]
    public void Fault_IsRecoverable_NextDeviceEventRebuilds()
    {
        var session = PlayingSession(out _);
        using var ctrl = new AudioDeviceController(session, () => new HeadlessAudioEndpoint(Fmt, warmupFrames: 0));
        ctrl.MarkRunning();
        ctrl.Fault();
        Assert.Equal(AudioDeviceState.Faulted, ctrl.State.Peek());
        ctrl.OnDefaultDeviceChanged();   // the deterministic equivalent of the watcher event landing on the cold thread
        Assert.Equal(AudioDeviceState.Running, ctrl.State.Peek());
    }

    /// <summary>Wavee #112 case A: a plug-in raises the default-device event while the new endpoint is still not
    /// <c>Initialize</c>-able. The controller must NOT adopt the dead endpoint (the 0.2.8 root cause — the valid speakers
    /// sink was disposed for a device that never opened): the OLD sink stays, the position keeps advancing, and a retry is
    /// scheduled.</summary>
    [Fact]
    public void NotReadyEndpoint_KeepsOldSink_EntersRetrying()
    {
        var session = PlayingSession(out _);
        var oldSink = session.Sink;
        long posBefore = session.PositionTracker.PlayedFramesCompensated;
        int opens = 0;
        using var ctrl = new AudioDeviceController(session, () =>
        {
            opens++;
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, ready: false);   // open failed: inert endpoint
        });
        ctrl.MarkRunning();

        ctrl.OnDefaultDeviceChanged();

        Assert.Equal(1, opens);
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.Same(oldSink, session.Sink);
        for (int i = 0; i < 20; i++) session.PumpAudio(256);
        Assert.True(session.PositionTracker.PlayedFramesCompensated > posBefore, "the kept sink stopped advancing the position");
        Assert.Equal(PlaybackState.Playing, session.CurrentState);
    }

    /// <summary>The ladder: 250 ms → 1 s → 3 s while the endpoint stays not-ready, then <c>Faulted</c> for a session that does
    /// not want sound (a playing one keeps a 5 s slow retry) — and a device event re-arms the whole thing (ladder reset to
    /// 250 ms) instead of staying dead.</summary>
    [Fact]
    public void Retrying_LadderExhausted_Faulted_ThenDeviceEventRearms()
    {
        var session = LoadedSession(out _);
        Assert.False(session.WantsOutput);
        bool ready = false;
        int opens = 0;
        using var ctrl = new AudioDeviceController(session, () =>
        {
            opens++;
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, ready: ready);
        });
        ctrl.MarkRunning();

        ctrl.OnDefaultDeviceChanged();                                    // attempt 1 → Retrying (250 ms)
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.False(ctrl.TryRunDueRetry(Environment.TickCount64 - 1));   // not due yet
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                  // attempt 2 → Retrying (1 s)
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                  // attempt 3 → Retrying (3 s)
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));                  // attempt 4 → ladder exhausted
        Assert.Equal(AudioDeviceState.Faulted, ctrl.State.Peek());
        Assert.False(ctrl.TryRunDueRetry(long.MaxValue));                 // nothing scheduled any more
        Assert.Equal(4, opens);

        // A sink-failure report cannot re-arm a Faulted controller (a dead sink would otherwise drive an attempt every 250 ms)...
        ctrl.ReportSinkFailure();
        Assert.False(ctrl.HasPendingRebuild);

        // ...but a device event does, with the ladder back at the first rung.
        ctrl.RequestRebuild();
        Assert.True(ctrl.HasPendingRebuild);
        ctrl.OnDefaultDeviceChanged();                                    // still not ready → Retrying again, not Faulted
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());

        ready = true;
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));
        Assert.Equal(AudioDeviceState.Running, ctrl.State.Peek());
        Assert.Equal(6, opens);
    }

    /// <summary>Past the ladder, a session that wants sound keeps a slow retry instead of going <c>Faulted</c>: a Bluetooth
    /// endpoint can refuse Initialize longer than the ladder and then recover with no default-device event.</summary>
    [Fact]
    public void PlayingSession_PastTheLadder_KeepsASlowRetry()
    {
        var session = PlayingSession(out _);
        bool ready = false;
        using var ctrl = new AudioDeviceController(session, () => new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, ready: ready));
        ctrl.MarkRunning();

        ctrl.OnDefaultDeviceChanged();                                    // attempt 1
        for (int i = 0; i < 3; i++) Assert.True(ctrl.TryRunDueRetry(long.MaxValue));   // attempts 2-4: the ladder runs out
        Assert.Equal(AudioDeviceState.Retrying, ctrl.State.Peek());       // not Faulted: the listener wants sound

        ready = true;                                                     // the endpoint comes back, no device event
        Assert.True(ctrl.TryRunDueRetry(long.MaxValue));
        Assert.Equal(AudioDeviceState.Running, ctrl.State.Peek());
    }

    /// <summary>A sink that accepts nothing — the invalidated-device shape (Wavee #112 case B) — and stamps when the
    /// report threshold is crossed.</summary>
    private sealed class DeadSink : IAudioSink
    {
        public MixFormat Format { get; }
        public int Writes;
        public long EighthFailureAtMs = -1;
        public DeadSink(MixFormat format) => Format = format;
        public int Write(ReadOnlySpan<float> src, int frames)
        {
            if (++Writes == 8) EighthFailureAtMs = Environment.TickCount64;
            return 0;
        }
        public void Start() { }
        public void Stop() { }
    }

    /// <summary>THE livelock regression (Wavee #112, shipped 0.2.8): a dead sink reports a failure every ~80 ms; each report
    /// used to re-stamp the 250 ms trailing debounce, so the cold thread never ran the rebuild — silence until the next
    /// track. Now the first report starts the request and the rest cannot postpone it: the rebuild lands within 500 ms of
    /// the report that crossed the threshold.</summary>
    [Fact]
    public void SinkFailureStorm_RebuildHappensWithin500ms()
    {
        var clockEndpoint = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0);
        var sink = new DeadSink(Fmt);
        var session = new PcmAudioSession(Fmt, sink, clockEndpoint.Clock, maxBlock: 256, driveWithOwnThread: false);
        session.Configure(AudioGraphSpec.Passthrough);
        var voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * 10], 2);
        session.SetVoice(voice, TimeSpan.FromSeconds(10), Fmt.SampleRate * 10, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();

        using var rebuilt = new ManualResetEventSlim(false);
        long rebuiltAtMs = 0;
        using var ctrl = new AudioDeviceController(session, () =>
        {
            rebuiltAtMs = Environment.TickCount64;
            rebuilt.Set();
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0);
        });
        session.RegisterDisposable(ctrl);   // the session captures the controller off this call (fix 3)
        ctrl.MarkRunning();
        ctrl.Start();                       // the real cold device thread

        session.PumpAudio(256);   // Opening → Buffering
        session.PumpAudio(256);   // Buffering → Ready → Playing
        Assert.Equal(PlaybackState.Playing, session.CurrentState);

        // The storm: a dead-sink report every ~10 ms for up to 3 s (0.2.8 never rebuilt under this; it hits the 3 s cap).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!rebuilt.IsSet && sw.ElapsedMilliseconds < 3000)
        {
            session.RenderBlock(256);
            Thread.Sleep(10);
        }

        Assert.True(rebuilt.IsSet, "the sink-failure storm livelocked the debounce: no rebuild in 3 s");
        Assert.True(sink.EighthFailureAtMs > 0);
        long sinceReport = rebuiltAtMs - sink.EighthFailureAtMs;
        Assert.True(sinceReport < 500, $"rebuild landed {sinceReport} ms after the threshold report (debounce is 250 ms)");
        Assert.True(SpinWaitFor(() => ctrl.State.Peek() == AudioDeviceState.Running, TimeSpan.FromSeconds(5)));
    }

    /// <summary>The 0.2.9 mode where the cold loop parked the RT feed for a rebuild, the rebuild failed, and nothing ever
    /// called <c>Start()</c> again. The feed must be restarted whenever it was parked — success or not — so the old sink keeps
    /// playing and the RT loop can keep reporting.</summary>
    [Fact]
    public void FeedIsRestartedAfterAFailedRebuild()
    {
        var endpoint = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0);
        var session = new PcmAudioSession(Fmt, endpoint.Sink, endpoint.Clock, maxBlock: 256, driveWithOwnThread: false, endpoint);
        session.Configure(AudioGraphSpec.Passthrough);
        using var feed = new AudioFeedThread(session, Fmt.SampleRate, blockMs: 5.0, aheadMs: 50.0, ringMs: 200.0);   // ctor attaches
        var watcher = new FakeDeviceWatcher();
        int opens = 0;
        using var ctrl = new AudioDeviceController(session, () =>
        {
            Interlocked.Increment(ref opens);
            return new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, ready: false);   // every open fails
        }, watcher, feed);
        session.RegisterDisposable(ctrl);
        ctrl.MarkRunning();
        ctrl.Start();
        feed.Start();
        Assert.False(feed.IsStopped);

        watcher.Raise();   // → cold thread: park the feed, attempt, fail → Retrying

        Assert.True(SpinWaitFor(() => Volatile.Read(ref opens) >= 1 && ctrl.State.Peek() == AudioDeviceState.Retrying, TimeSpan.FromSeconds(5)),
            "the failed rebuild did not enter Retrying");
        Assert.True(SpinWaitFor(() => !feed.IsStopped, TimeSpan.FromSeconds(5)),
            "the RT feed stayed parked after a failed rebuild");

        ctrl.Dispose();
        feed.Stop();
    }

    [Fact]
    public void WatcherEvent_MarshalsToColdThread_AndRebuilds()
    {
        var session = PlayingSession(out _);
        var watcher = new FakeDeviceWatcher();
        using var rebuilt = new ManualResetEventSlim(false);
        int rebuilds = 0;

        using var ctrl = new AudioDeviceController(session, () =>
        {
            Interlocked.Increment(ref rebuilds);
            var ep = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, latencyFrames: 200);
            rebuilt.Set();
            return ep;
        }, watcher);
        ctrl.MarkRunning();
        ctrl.Start();          // spins the cold device thread

        watcher.Raise();       // fires DefaultDeviceChanged → RequestRebuild → cold thread → OnDefaultDeviceChanged

        Assert.True(rebuilt.Wait(TimeSpan.FromSeconds(5)), "the cold device thread did not service the rebuild");
        // Give the cold thread a moment to publish Running (bounded, event-gated — no busy sleep on a hot path).
        Assert.True(SpinWaitFor(() => ctrl.State.Peek() == AudioDeviceState.Running, TimeSpan.FromSeconds(5)));
        Assert.True(rebuilds >= 1);
    }

    private static bool SpinWaitFor(Func<bool> cond, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout) { if (cond()) return true; Thread.Yield(); }
        return cond();
    }
}
