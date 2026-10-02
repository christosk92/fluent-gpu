using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>A deterministic buffered device that can DIE: after <see cref="FailAfterWrites"/> accepted writes — or on
/// <see cref="Kill"/> — its writable count reads −1 and every write is refused, exactly an invalidated WASAPI client with no
/// follow-default notification. It is its own played-frames clock, advanced by <see cref="AdvanceHardware"/>, and counts the
/// transitions the session drives so a test can assert "stopped exactly once".</summary>
internal sealed class FailingAudioSink : IBufferedAudioSink, IAudioClockSource
{
    private readonly int _capacity;
    private int _padding;
    private long _played;
    private bool _dead;

    /// <summary>Create the double over <paramref name="format"/>; <paramref name="capacityFrames"/> is the device buffer.</summary>
    public FailingAudioSink(MixFormat format, int capacityFrames = 960)
    {
        Format = format;
        _capacity = capacityFrames;
    }

    /// <summary>The device dies once this many writes have been ACCEPTED (−1 = never, until <see cref="Kill"/>).</summary>
    public int FailAfterWrites = -1;
    /// <summary>Accepted writes so far.</summary>
    public int Writes { get; private set; }
    /// <summary>Stop calls received.</summary>
    public int StopCalls { get; private set; }
    /// <summary>Start calls received.</summary>
    public int StartCalls { get; private set; }
    /// <summary>Whether the device is running.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>The device is lost from this point on.</summary>
    public void Kill() => _dead = true;

    /// <summary>The hardware consumes up to <paramref name="frames"/> queued frames (only while started and alive).</summary>
    public void AdvanceHardware(int frames)
    {
        if (!IsStarted || _dead) return;
        int count = Math.Min(Math.Max(0, frames), _padding);
        _padding -= count;
        _played += count;
    }

    /// <inheritdoc/>
    public MixFormat Format { get; }
    /// <inheritdoc/>
    public int CapacityFrames => _capacity;
    /// <inheritdoc/>
    public int WritableFrames => _dead ? -1 : _capacity - _padding;
    /// <inheritdoc/>
    public int Write(ReadOnlySpan<float> src, int frames)
    {
        if (_dead) return 0;
        int count = Math.Max(0, Math.Min(frames, _capacity - _padding));
        _padding += count;
        if (count > 0 && ++Writes == FailAfterWrites) _dead = true;   // the write that tips it over IS accepted; the next one is refused
        return count;
    }
    /// <inheritdoc/>
    public void Start() { StartCalls++; IsStarted = true; }
    /// <inheritdoc/>
    public void Stop() { StopCalls++; IsStarted = false; }
    /// <inheritdoc/>
    public void Reset() { _padding = 0; }
    /// <inheritdoc/>
    public void WaitForWritable(System.Threading.WaitHandle controlWake, int timeoutMs) => controlWake.WaitOne(timeoutMs);

    /// <inheritdoc/>
    public long WrittenFrames => _played + _padding;
    /// <inheritdoc/>
    public long StreamLatencyFrames => 0;
    /// <inheritdoc/>
    public int MixRate => Format.SampleRate;
    /// <inheritdoc/>
    public bool TryGetPlayed(out long playedFrames, out long qpc) { playedFrames = _played; qpc = 0; return !_dead; }
}

/// <summary>
/// H-3 (playback smoothness, V-PE8): a sink that has gone dead while a transport fade is in flight (phase 1) or draining
/// (phase 2) used to hang the pause forever — the drain test needs <c>WritableFrames ≥ CapacityFrames</c> and a dead sink reads
/// −1, and both phases returned before <c>RenderBlock</c>'s dead-sink report was reachable. The session now drops what the dead
/// device will never accept, reports the loss exactly once, stops, and calls the fade over (phase 3) on the next wake.
/// </summary>
public sealed class DeadSinkTests
{
    private static readonly MixFormat Fmt = new(48000, 2);
    private const int Block = 480;

    private sealed class Rig : IDisposable
    {
        public readonly FailingAudioSink Sink = new(Fmt);
        public readonly PcmAudioSession Session;
        public readonly AudioDeviceController Controller;

        public Rig()
        {
            Session = new PcmAudioSession(Fmt, Sink, Sink, Block, driveWithOwnThread: false);
            Session.Configure(AudioGraphSpec.Passthrough);
            Controller = new AudioDeviceController(Session, () => new HeadlessAudioEndpoint(Fmt));
            Session.RegisterDisposable(Controller);   // the session captures the controller off this call; no cold thread: the request just becomes pending
            Controller.MarkRunning();
            Session.SetVoice(new SignalGeneratorSource(2, Fmt.SampleRate, 440, 0.3f, -1), TimeSpan.FromSeconds(60), 60 * 48000L, NormMode.Off, -14f, initialVolume: 1f);
            Session.ConnectSignals(new MediaSignalSink(new MediaPlayerCore()));
            _ = Session.PlayAsync();
        }

        /// <summary>10 ms on the single-thread pull path: the inline pump renders one block, then the device consumes one.</summary>
        public void Step()
        {
            Session.PumpAudio(Block);
            Sink.AdvanceHardware(Block);
        }

        public void Dispose() => _ = Session.DisposeAsync();
    }

    private static void ReachPlaying(Rig rig)
    {
        for (int i = 0; i < 8; i++) rig.Step();
        Assert.Equal(PlaybackState.Playing, rig.Session.CurrentState);
        Assert.True(rig.Sink.IsStarted);
    }

    [Fact]
    public async Task ADeadSink_MidFade_FailsTheFadeOverOnTheNextWake_AndReportsExactlyOnce()
    {
        using var rig = new Rig();
        ReachPlaying(rig);

        Task pause = rig.Session.PauseAsync().AsTask();        // the 20 ms fade-out begins: phase 1
        Assert.Equal(1, rig.Session.TransportPhase);
        Assert.False(pause.IsCompleted);

        rig.Sink.Kill();                                        // the device is invalidated mid-fade
        rig.Session.PumpAudio(Block);                           // ONE wake

        Assert.Equal(3, rig.Session.TransportPhase);            // the fade is over as far as this device is concerned
        Assert.Equal(1, rig.Sink.StopCalls);
        Assert.False(rig.Sink.IsStarted);
        Assert.True(rig.Controller.HasPendingRebuild);
        Assert.Equal(1, rig.Controller.SinkFailureRequests);    // a typed report: the rebuild starts without waiting out the 8-block threshold
        await pause.WaitAsync(TimeSpan.FromSeconds(5));         // the pause is no longer hanging
        Assert.Equal(PlaybackState.Paused, rig.Session.CurrentState);

        for (int i = 0; i < 4; i++) rig.Session.PumpAudio(Block);   // later wakes neither stop again nor re-report
        Assert.Equal(1, rig.Sink.StopCalls);
        Assert.Equal(1, rig.Controller.SinkFailureRequests);
    }

    [Fact]
    public async Task ADeadSink_WhileDraining_StopsOnceAndReachesTheHeldPhase()
    {
        using var rig = new Rig();
        ReachPlaying(rig);

        Task pause = rig.Session.PauseAsync().AsTask();
        // Pump the fade out WITHOUT letting the hardware drain the last block: the session parks in phase 2 (draining).
        for (int i = 0; i < 20 && rig.Session.TransportPhase != 2; i++)
        {
            rig.Session.PumpAudio(Block);
            if (rig.Session.TransportPhase == 2) break;
            rig.Sink.AdvanceHardware(Block);
        }
        Assert.Equal(2, rig.Session.TransportPhase);
        Assert.False(pause.IsCompleted);

        rig.Sink.Kill();
        rig.Session.PumpAudio(Block);
        rig.Session.PumpAudio(Block);                           // "within two wakes"

        Assert.Equal(3, rig.Session.TransportPhase);
        Assert.Equal(1, rig.Sink.StopCalls);
        Assert.Equal(1, rig.Controller.SinkFailureRequests);
        await pause.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ASinkThatDiesWhileTheTransportRuns_IsStillReportedByTheNormalRenderPath_OnceAfterTheThreshold()
    {
        // The device dies while the transport is RUNNING (phase 0): RenderBlock's own dead-sink branch (the 8-block threshold) owns
        // that case; H-3 must not have changed it — the controller is asked once after the threshold, never re-stamped.
        using var rig = new Rig();
        rig.Sink.FailAfterWrites = 6;
        for (int i = 0; i < 30; i++) rig.Step();

        Assert.Equal(0, rig.Sink.StopCalls);                    // a running transport never stops a dead device itself — the rebuild does
        Assert.True(rig.Controller.HasPendingRebuild);
        Assert.Equal(1, rig.Controller.SinkFailureRequests);
    }
}
