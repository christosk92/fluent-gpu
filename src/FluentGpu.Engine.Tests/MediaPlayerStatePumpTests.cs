using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F132: the media control plane leaves the element. <see cref="MediaPlayer"/> runs the session's STATE pump itself, one
/// coalesced UI post per burst of pump requests, so state, position, duration, natural size and errors advance with no
/// <c>MediaPlayerElement</c> mounted; the element's <see cref="MediaPlayer.PumpVideo"/> is the geometry half only, and a
/// session that has not split its pump falls back to <see cref="IVideoSurfaceSession.PumpVideo"/> through the interface's
/// default members. Serial: <see cref="HostDispatch.Current"/> is process-static.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class MediaPlayerStatePumpTests
{
    /// <summary>A UI-thread stand-in: posts queue here and only the test thread runs them.</summary>
    private sealed class UiQueue
    {
        private readonly ConcurrentQueue<Action> _q = new();
        public int Posted;
        public void Post(Action a) { Interlocked.Increment(ref Posted); _q.Enqueue(a); }
        public void Drain() { while (_q.TryDequeue(out var a)) a(); }
        public void PumpUntil(Task done, int timeoutMs = 10_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done.IsCompleted)
            {
                if (_q.TryDequeue(out var a)) a();
                else Thread.Sleep(1);
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, "OpenAsync never completed: a post was stranded or never made");
            }
        }
    }

    private sealed class SessionBackend(IMediaSession session) : IMediaBackend
    {
        public MediaCapabilities Capabilities { get; } = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: false);
        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
            => ValueTask.FromResult(session);
    }

    /// <summary>A video session with a split pump: it counts each half and publishes a duration from its state half.</summary>
    private sealed class PumpSession : IMediaSession, IVideoSurfaceSession, IVideoPumpSource
    {
        public int StatePumps, GeometryPumps, FullPumps;
        // A presentation epoch (FORMATCHANGE / RESOURCELOST) the way MfMediaSession models it: the state half adopts the engine's
        // epoch, and the geometry half skips a turn while the engine is on one the state half has not adopted yet.
        public int Epoch, AdoptedEpoch, BoundEpoch, StaleGeometry;
        public TimeSpan Duration;
        public Action? OnState;
        private MediaSignalSink? _sink;

        public event Action? PumpRequested;
        public void RaisePump() => PumpRequested?.Invoke();

        public void ConnectSignals(MediaSignalSink sink) { _sink = sink; sink.State(PlaybackState.Opening); }
        public void PumpVideo(VideoBinding binding, RectF videoRect, float scale) => FullPumps++;
        public void PumpState()
        {
            StatePumps++;
            AdoptedEpoch = Epoch;
            OnState?.Invoke();
            if (Duration > TimeSpan.Zero) _sink?.Duration(Duration);
        }
        public void PumpGeometry(VideoBinding binding, RectF videoRect, float scale)
        {
            GeometryPumps++;
            if (Epoch != AdoptedEpoch) { StaleGeometry++; return; }
            BoundEpoch = Epoch;
        }

        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A session that only knows the old one-method seam: every pump must reach it through <see cref="PumpVideo"/>.</summary>
    private sealed class LegacySession : IVideoSurfaceSession
    {
        public int Calls;
        public VideoBinding LastBinding;
        public RectF LastRect;
        public float LastScale;
        public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
        {
            Calls++;
            LastBinding = binding;
            LastRect = videoRect;
            LastScale = scale;
        }
    }

    private static (MediaPlayer Player, PumpSession Session, UiQueue Ui, Action Restore) Open()
    {
        FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);   // this thread plays the UI thread
        var prior = HostDispatch.Current;
        var ui = new UiQueue();
        HostDispatch.Current = ui.Post;
        var session = new PumpSession();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, new SessionBackend(session)).Build();
        var open = player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask();
        ui.PumpUntil(open);
        open.GetAwaiter().GetResult();
        ui.Drain();   // the state pump the connect asked for
        return (player, session, ui, () => HostDispatch.Current = prior);
    }

    [Fact]
    public void ASessionRaise_RunsThePlayersOwnStatePump_WithNoElementMounted()
    {
        var (player, session, ui, restore) = Open();
        try
        {
            int baseline = session.StatePumps;
            session.Duration = TimeSpan.FromSeconds(90);

            session.RaisePump();                                    // the engine thread's event
            Assert.Equal(baseline, session.StatePumps);             // nothing ran on the raising thread: the pump is the UI thread's
            ui.Drain();

            Assert.Equal(baseline + 1, session.StatePumps);
            Assert.Equal(TimeSpan.FromSeconds(90), player.Duration.Peek());   // published with no element and no binding
            Assert.Equal(0, session.GeometryPumps);                 // no element ever asked for the surface half
            Assert.Equal(0, session.FullPumps);
        }
        finally { restore(); }
    }

    [Fact]
    public void ABurstOfRaises_CoalescesIntoOneStatePump()
    {
        var (_, session, ui, restore) = Open();
        try
        {
            int baseline = session.StatePumps;
            int posted = ui.Posted;

            for (int i = 0; i < 25; i++) session.RaisePump();
            Assert.Equal(posted + 1, ui.Posted);                    // one queued turn, not 25
            ui.Drain();
            Assert.Equal(baseline + 1, session.StatePumps);

            session.RaisePump();                                    // a raise after the turn ran queues the next one
            ui.Drain();
            Assert.Equal(baseline + 2, session.StatePumps);
        }
        finally { restore(); }
    }

    [Fact]
    public void TheStatePump_IsQueuedBeforeTheMountedElementIsTold()
    {
        var (player, session, ui, restore) = Open();
        try
        {
            var order = new List<string>();
            session.OnState = () => order.Add("state");
            // What MediaPlayerElement.QueuePumpRequest does: post its own registry request when the player says so.
            player.PumpRequested += () => ui.Post(() => order.Add("element"));

            session.RaisePump();
            ui.Drain();

            Assert.Equal(new[] { "state", "element" }, order);       // the element binds AFTER the state it was told about
        }
        finally { restore(); }
    }

    [Fact]
    public void TheElementsPumpVideo_IsTheGeometryHalfOnly()
    {
        var (player, session, _, restore) = Open();
        try
        {
            int state = session.StatePumps;

            player.PumpVideo(default, new RectF(0, 0, 320, 180), 1f);

            Assert.Equal(1, session.GeometryPumps);
            Assert.Equal(state, session.StatePumps);                 // PumpVideo publishes nothing the player already publishes itself
            Assert.Equal(0, session.FullPumps);
        }
        finally { restore(); }
    }

    [Fact]
    public void TheHeartbeat_CountsPumps_AndIsZeroWithoutASession()
    {
        var (player, session, ui, restore) = Open();
        try
        {
            Assert.True(player.StatePumpCount >= 1);
            Assert.True(player.StatePumpAgeMs >= 0);
            int count = player.StatePumpCount;

            session.RaisePump();
            ui.Drain();
            Assert.Equal(count + 1, player.StatePumpCount);

            player.Stop();                                           // the session is released: no session, no age
            ui.Drain();
            Assert.Equal(0, player.StatePumpAgeMs);
        }
        finally { restore(); }
    }

    [Fact]
    public void ADisposedPlayer_RunsNoMoreStatePumps()
    {
        var (player, session, ui, restore) = Open();
        try
        {
            int baseline = session.StatePumps;

            session.RaisePump();                                    // a turn is queued ...
            player.DisposeAsync().AsTask().GetAwaiter().GetResult();   // ... and the player goes away before it runs
            ui.Drain();

            Assert.Equal(baseline, session.StatePumps);
        }
        finally { restore(); }
    }

    [Fact]
    public void AnElementOnAnotherHost_DrainingBeforeThePlayersStatePost_StillRebindsWithNoFurtherRaise()
    {
        var (player, session, ui, restore) = Open();
        try
        {
            // The pop-out's element posts through ITS host's queue; the player's state pump posts to the queue it was built on.
            var elementUi = new UiQueue();
            player.PumpRequested += () => elementUi.Post(() => player.PumpVideo(default, new RectF(0, 0, 320, 180), 1f));

            session.Epoch++;                                        // the engine's FORMATCHANGE / RESOURCELOST
            session.RaisePump();                                    // ONE raise, and nothing raises again
            elementUi.Drain();                                      // the element's host drains FIRST, before the state turn

            Assert.Equal(0, session.StaleGeometry);                 // the geometry half never saw an epoch the state half had not adopted
            Assert.Equal(session.Epoch, session.BoundEpoch);        // the rebind landed in that one turn
            ui.Drain();                                             // the late state turn is harmless
            Assert.Equal(0, session.StaleGeometry);
            Assert.Equal(session.Epoch, session.BoundEpoch);
        }
        finally { restore(); }
    }

    [Fact]
    public void WithNoVideoSession_TransportVerbsAndStopPostNoStateTurn()
    {
        var (player, _, ui, restore) = Open();
        try
        {
            player.Stop();                                          // releases the session; its own reset needs no pump
            ui.Drain();
            int posted = ui.Posted;

            _ = player.PlayAsync();
            _ = player.PauseAsync();
            _ = player.SeekAsync(TimeSpan.FromSeconds(3));
            player.SetRate(1.5);
            player.Stop();

            Assert.Equal(posted, ui.Posted);                        // nothing to pump, so no host-frame wake per verb
        }
        finally { restore(); }
    }

    [Fact]
    public void ASessionThatHasNotSplitItsPump_FallsBackToPumpVideoThroughTheDefaultMembers()
    {
        var legacy = new LegacySession();
        IVideoSurfaceSession seam = legacy;

        seam.PumpState();
        Assert.Equal(1, legacy.Calls);
        Assert.False(legacy.LastBinding.IsValid);                    // the state half is the old inert-binding pump
        Assert.Equal(default(RectF), legacy.LastRect);

        var rect = new RectF(10, 20, 640, 360);
        seam.PumpGeometry(default, rect, 2f);
        Assert.Equal(2, legacy.Calls);
        Assert.Equal(rect, legacy.LastRect);
        Assert.Equal(2f, legacy.LastScale);
    }
}
