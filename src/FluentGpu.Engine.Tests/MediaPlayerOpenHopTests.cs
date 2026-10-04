using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The open path no longer walks 3-4 serial UI-thread round trips with the protected attach buried in the third. With a
/// counting poster standing in for the host's UI queue (it runs each post inline and records it), one open of a source
/// without sidecar subtitles and with a start position posts exactly two hops: the Opening reset (fire-and-forget, so
/// <c>backend.OpenAsync</c> starts at once) and the single post that connects the session, and the session is STARTED on
/// the opening thread between them, before the connecting hop.
/// </summary>
[Collection(SerialTestCollection.Name)]   // HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class MediaPlayerOpenHopTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OpenWithAStartPositionAndNoSubtitles_PostsTwoHops_AndStartsTheSessionBeforeTheConnectingOne()
    {
        var events = new List<string>();
        void Record(string e) { lock (events) events.Add(e); }
        using var poster = new HostPosterScope(action => { Record("post"); action(); });
        var backend = new GatedMediaBackend { Log = Record };
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
        backend.Release("a.mp4");

        await player.OpenAsync(MediaSource.FromFile("a.mp4"), new MediaOpenOptions { StartPosition = TimeSpan.FromSeconds(7) })
            .AsTask().WaitAsync(Limit);

        string[] seen;
        lock (events) seen = events.ToArray();
        Assert.Equal(new[] { "post", "start", "post", "connect" }, seen);
        Assert.Equal(TimeSpan.FromSeconds(7), player.Position.Peek());   // reset, THEN the start position
        Assert.Equal(PlaybackState.Opening, player.State.Peek());
        await player.DisposeAsync();
    }

    [Fact]
    public async Task OpenWithoutAStartPosition_PostsTwoHops_TheSubtitleResetCostsNone()
    {
        int posts = 0;
        using var poster = new HostPosterScope(action => { Interlocked.Increment(ref posts); action(); });
        var backend = new GatedMediaBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
        backend.Release("a.mp4");

        await player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask().WaitAsync(Limit);

        Assert.Equal(2, Volatile.Read(ref posts));
        await player.DisposeAsync();
    }

    /// <summary>A UI-thread stand-in that runs nothing until the test drains it (the host's queue between two frames).</summary>
    private sealed class DeferredUi
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _q = new();
        public int Pending => _q.Count;
        public void Post(Action a) => _q.Enqueue(a);
        public void Drain() { while (_q.TryDequeue(out var a)) a(); }
    }

    [Fact]
    public async Task OpenAsync_DoesNotWaitForAUiFrame_TheSessionIsClaimedAndReachableBeforeItsSignalsConnect()
    {
        var ui = new DeferredUi();
        using var poster = new HostPosterScope(ui.Post);
        var backend = new GatedMediaBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
        backend.Release("a.mp4");

        // The UI thread never drains while the open runs: an awaited post would make this time out.
        await player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask().WaitAsync(Limit);

        RecordingMediaSession session = backend.SessionFor("a.mp4");
        Assert.Equal(2, ui.Pending);                         // the Opening reset and the connecting post, both still queued
        Assert.Equal(1, session.StartCount);                 // started on the opening thread
        Assert.Equal(0, session.ConnectCount);               // signals not connected yet
        Assert.Same(session, player.Session);                // but the session is the player's: the caller's verbs reach it
        await player.PlayAsync();
        Assert.Equal(1, session.PlayCount);

        ui.Drain();
        Assert.Equal(1, session.ConnectCount);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task AStopBetweenTheOpenReturningAndTheConnectingPost_NeverConnectsTheReleasedSession()
    {
        var ui = new DeferredUi();
        using var poster = new HostPosterScope(ui.Post);
        var backend = new GatedMediaBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
        backend.Release("a.mp4");
        await player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask().WaitAsync(Limit);

        player.Stop();   // supersedes the open and releases the claimed session before its connecting post runs
        ui.Drain();

        RecordingMediaSession session = backend.SessionFor("a.mp4");
        Assert.Equal(0, session.ConnectCount);
        Assert.Null(player.Session);
        await player.DisposeAsync();
    }

    /// <summary>A real UI thread stand-in: one thread runs every post in order, so the test can await an open whose
    /// completion depends on a UI post (unlike <see cref="DeferredUi"/>, which only runs when the test drains it).</summary>
    private sealed class PumpedUi : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _q = new();
        private readonly Thread _thread;

        public PumpedUi()
        {
            _thread = new Thread(() => { foreach (var a in _q.GetConsumingEnumerable()) a(); }) { IsBackground = true, Name = "test-ui" };
            _thread.Start();
        }

        public void Post(Action a) => _q.Add(a);
        public void Dispose() { _q.CompleteAdding(); _thread.Join(Limit); }
    }

    [Fact]
    public async Task AnOpenAfterAFailedOne_DoesNotReturnWhileTheStaleErrorIsStillOnTheCore()
    {
        // The Opening post is what clears the previous source's Error; Wavee reads Error right after the await, on a pool
        // thread. A stale error there would fault a good open (an in-place retry after a fault could never succeed).
        using var ui = new PumpedUi();
        using var poster = new HostPosterScope(ui.Post);
        var backend = new GatedMediaBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
        backend.Fail("bad.mp4");
        await player.OpenAsync(MediaSource.FromFile("bad.mp4")).AsTask().WaitAsync(Limit);
        Assert.NotNull(player.Error.Peek());
        Assert.Equal(PlaybackState.Failed, player.State.Peek());

        backend.Release("good.mp4");
        await player.OpenAsync(MediaSource.FromFile("good.mp4")).AsTask().WaitAsync(Limit);

        Assert.Null(player.Error.Peek());                              // already cleared when OpenAsync completed
        Assert.NotEqual(PlaybackState.Failed, player.State.Peek());
        await player.DisposeAsync();
    }
}
