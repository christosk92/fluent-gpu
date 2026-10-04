using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Hooks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <c>MediaPlayer</c> marshals every <c>MediaPlayerCore</c> write (the UI-confined reactive runtime's sole writer) through
/// the UI poster that was live when the player was BUILT, not through whatever the process-static
/// <c>HostDispatch.Current</c> holds at call time. A pop-out child host used to overwrite that static and null it on close,
/// after which <c>OnUiAsync</c> ran the writes inline on the pool thread the backend await resumed on.
/// Serial: <c>HostDispatch.Current</c> is process-static.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class MediaPlayerDispatcherTests
{
    /// <summary>A UI-thread stand-in: posts queue here and only the test thread pumps them.</summary>
    private sealed class UiQueue
    {
        private readonly ConcurrentQueue<Action> _q = new();
        public int Posted;
        public readonly int UiThreadId = Environment.CurrentManagedThreadId;
        public void Post(Action a) { Interlocked.Increment(ref Posted); _q.Enqueue(a); }
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

    private sealed class PoolBackend : IMediaBackend
    {
        public MediaCapabilities Capabilities => new(false, true, false);
        // Completes on a ThreadPool thread, like a native/MF open: the await resumes OFF the UI thread.
        public async ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
        {
            await Task.Run(() => Thread.Sleep(5), ct).ConfigureAwait(false);
            return new Session();
        }
    }

    private sealed class Session : IMediaSession
    {
        public int ConnectThreadId;
        public void ConnectSignals(MediaSignalSink sink) { ConnectThreadId = Environment.CurrentManagedThreadId; sink.State(PlaybackState.Ready); }
        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void OpenAsync_PostsToThePosterCapturedAtConstruction_EvenAfterTheProcessStaticIsNulled()
    {
        FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);   // this thread plays the UI thread
        var prior = HostDispatch.Current;
        var ui = new UiQueue();
        HostDispatch.Current = ui.Post;               // the main host's poster, live when the player is built
        try
        {
            var player = MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, new PoolBackend()).Build();
            HostDispatch.Current = null;              // a pop-out child closed: the old code left the static null for the session
            long offUiBefore = MediaPlayerCore.OffUiCoreWrites;

            var open = player.OpenAsync(MediaSource.FromFile("x.flac")).AsTask();
            ui.PumpUntil(open);
            open.GetAwaiter().GetResult();

            Assert.True(ui.Posted >= 2, $"core writes must hop to the captured poster (posted {ui.Posted})");
            Assert.Equal(PlaybackState.Ready, player.State.Peek());
            Assert.Equal(ui.UiThreadId, ((Session)player.Session!).ConnectThreadId);   // ConnectSignals ran on the "UI" thread
            Assert.Equal(offUiBefore, MediaPlayerCore.OffUiCoreWrites);                 // no core write from a pool thread (Debug probe)
        }
        finally { HostDispatch.Current = prior; }
    }

    [Fact]
    public void AnotherHostsPoster_DoesNotHijackAnExistingPlayer()
    {
        var prior = HostDispatch.Current;
        var mine = new UiQueue();
        var other = new UiQueue();
        HostDispatch.Current = mine.Post;
        try
        {
            var player = MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, new PoolBackend()).Build();
            HostDispatch.Current = other.Post;        // some other host constructed afterwards

            var open = player.OpenAsync(MediaSource.FromFile("x.flac")).AsTask();
            mine.PumpUntil(open);

            Assert.True(mine.Posted >= 2);
            Assert.Equal(0, other.Posted);
        }
        finally { HostDispatch.Current = prior; }
    }

    [Fact]
    public void APlayerBuiltBeforeAnyHost_StillFallsBackToTheLiveStatic()
    {
        var prior = HostDispatch.Current;
        HostDispatch.Current = null;
        try
        {
            var player = MediaPlayer.Build().WithBackend(MediaKind.PcmAudio, new PoolBackend()).Build();
            var late = new UiQueue();
            HostDispatch.Current = late.Post;         // the host came up after the player (a service built at startup)

            var open = player.OpenAsync(MediaSource.FromFile("x.flac")).AsTask();
            late.PumpUntil(open);

            Assert.True(late.Posted >= 2);
        }
        finally { HostDispatch.Current = prior; }
    }
}
