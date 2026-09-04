using System;
using System.Threading;
using FluentGpu.Media;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// M4 device-loss / follow-default state-machine tests (spec §7.9) — <c>{Building, Running, Reinitializing, Faulted}</c>
/// driven by a FAKE <see cref="IDeviceWatcher"/> + fake endpoints (no real WASAPI). A default-device change rebuilds ONLY
/// the sink under a live graph: sources, mixer voices, and the derived position SURVIVE, and latency is re-measured. All
/// deterministic (drive <see cref="AudioDeviceController.OnDefaultDeviceChanged"/> directly); the one watcher-event test is
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
        var ep = new HeadlessAudioEndpoint(Fmt, warmupFrames: 0, latencyFrames: latencyFrames);
        var session = new PcmAudioSession(Fmt, ep.Sink, ep.Clock, maxBlock: 256, driveWithOwnThread: false, ep);
        session.Configure(AudioGraphSpec.Passthrough);
        voice = new MemoryAudioSource(new float[Fmt.SampleRate * 2 * 10], 2);   // 10 s
        session.SetVoice(voice, TimeSpan.FromSeconds(10), Fmt.SampleRate * 10, NormMode.Off, -14f, initialVolume: 1f);
        session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
        _ = session.PlayAsync();
        session.PumpAudio(256);   // Opening → Buffering
        session.PumpAudio(256);   // Buffering → Ready → Playing
        for (int i = 0; i < 50; i++) session.PumpAudio(256);   // advance the clock so position > 0
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

    [Fact]
    public void FatalRebuild_NoEndpoint_TransitionsToFaulted()
    {
        var session = PlayingSession(out _);
        var watcher = new FakeDeviceWatcher();
        using var ctrl = new AudioDeviceController(session, () => throw new InvalidOperationException("all devices gone"), watcher);
        ctrl.MarkRunning();
        ctrl.OnDefaultDeviceChanged();
        Assert.Equal(AudioDeviceState.Faulted, ctrl.State.Peek());
    }

    [Fact]
    public void Fault_IsTerminal()
    {
        var session = PlayingSession(out _);
        using var ctrl = new AudioDeviceController(session, () => new HeadlessAudioEndpoint(Fmt));
        ctrl.MarkRunning();
        ctrl.Fault();
        Assert.Equal(AudioDeviceState.Faulted, ctrl.State.Peek());
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
