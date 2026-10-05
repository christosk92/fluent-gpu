using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="MediaPlayer"/>'s open path is last-wins and dispose-safe: an open that finishes after
/// <see cref="MediaPlayer.DisposeAsync"/>, after <see cref="MediaPlayer.Stop"/> or after a newer open must dispose its
/// session instead of starting, connecting or publishing it (a protected attach detaches whichever session is live on the
/// native engine, so a late orphan freezes the live picture). A gated fake backend holds each open until the test releases
/// it, so the completion order is exactly the one the test names.
/// </summary>
[Collection(SerialTestCollection.Name)]   // HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class MediaPlayerOpenRaceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static MediaPlayer NewPlayer(GatedMediaBackend backend)
        => MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();

    [Fact]
    public async Task DisposeMidOpen_DisposesTheLateSession_NeverConnectsOrStartsIt()
    {
        using var poster = new HostPosterScope(null);
        var backend = new GatedMediaBackend();
        var player = NewPlayer(backend);

        Task open = player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask();
        await backend.Entered("a.mp4");

        await player.DisposeAsync();
        Assert.True(backend.TokenFor("a.mp4").IsCancellationRequested);   // the dispose cancels the open's linked token
        backend.Release("a.mp4");
        await open.WaitAsync(Limit);

        RecordingMediaSession late = backend.SessionFor("a.mp4");
        Assert.Equal(1, late.DisposeCount);
        Assert.Equal(0, late.StartCount);
        Assert.Equal(0, late.ConnectCount);
        Assert.Null(player.Session);
    }

    [Fact]
    public async Task OverlappingOpens_LastWins_TheEarlierSessionCompletingLastIsDisposedUnconnected()
    {
        using var poster = new HostPosterScope(null);
        var backend = new GatedMediaBackend();
        var player = NewPlayer(backend);

        Task openA = player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask();
        await backend.Entered("a.mp4");
        Task openB = player.OpenAsync(MediaSource.FromFile("b.mp4")).AsTask();
        await backend.Entered("b.mp4");
        Assert.True(backend.TokenFor("a.mp4").IsCancellationRequested);    // the newer open cancels the older one
        Assert.False(backend.TokenFor("b.mp4").IsCancellationRequested);

        backend.Release("b.mp4");
        await openB.WaitAsync(Limit);
        backend.Release("a.mp4");   // A finishes opening AFTER B is live
        await openA.WaitAsync(Limit);

        RecordingMediaSession a = backend.SessionFor("a.mp4"), b = backend.SessionFor("b.mp4");
        Assert.Same(b, player.Session);
        Assert.Equal(1, b.StartCount);
        Assert.Equal(1, b.ConnectCount);
        Assert.Equal(0, b.DisposeCount);
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(0, a.StartCount);
        Assert.Equal(0, a.ConnectCount);

        await player.DisposeAsync();
        Assert.Equal(1, b.DisposeCount);
    }

    [Fact]
    public async Task AStaleOpenThatFails_DoesNotPublishFailedOverTheNewerOpen()
    {
        using var poster = new HostPosterScope(null);
        var backend = new GatedMediaBackend();
        var player = NewPlayer(backend);

        Task openA = player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask();
        await backend.Entered("a.mp4");
        Task openB = player.OpenAsync(MediaSource.FromFile("b.mp4")).AsTask();
        await backend.Entered("b.mp4");
        backend.Release("b.mp4");
        await openB.WaitAsync(Limit);

        backend.Fail("a.mp4");
        await openA.WaitAsync(Limit);

        Assert.Same(backend.SessionFor("b.mp4"), player.Session);
        Assert.NotEqual(PlaybackState.Failed, player.State.Peek());
        Assert.Null(player.Error.Peek());
        await player.DisposeAsync();
    }

    [Fact]
    public async Task StopMidOpen_DisposesTheLateSession_AndStaysIdle()
    {
        using var poster = new HostPosterScope(null);
        var backend = new GatedMediaBackend();
        var player = NewPlayer(backend);

        Task open = player.OpenAsync(MediaSource.FromFile("a.mp4")).AsTask();
        await backend.Entered("a.mp4");

        player.Stop();
        backend.Release("a.mp4");
        await open.WaitAsync(Limit);

        RecordingMediaSession late = backend.SessionFor("a.mp4");
        Assert.Equal(1, late.DisposeCount);
        Assert.Equal(0, late.StartCount);
        Assert.Equal(0, late.ConnectCount);
        Assert.Null(player.Session);
        Assert.Equal(PlaybackState.Idle, player.State.Peek());
        await player.DisposeAsync();
    }
}

/// <summary>Installs a process-static UI poster (<see cref="FluentGpu.Hooks.HostDispatch.Current"/>) for a test and
/// restores the previous one; null runs every post inline on the calling thread, as a headless player does.</summary>
internal sealed class HostPosterScope : IDisposable
{
    private readonly Action<Action>? _previous;

    public HostPosterScope(Action<Action>? poster)
    {
        _previous = FluentGpu.Hooks.HostDispatch.Current;
        FluentGpu.Hooks.HostDispatch.Current = poster;
    }

    public void Dispose() => FluentGpu.Hooks.HostDispatch.Current = _previous;
}

/// <summary>A fake <see cref="IMediaBackend"/> whose <c>OpenAsync</c> parks (per source file name) until the test calls
/// <see cref="Release"/> or <see cref="Fail"/>, and ignores its cancellation token the way a slow native open does.</summary>
internal sealed class GatedMediaBackend : IMediaBackend
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource> _gates = new();
    private readonly Dictionary<string, TaskCompletionSource> _entered = new();
    private readonly Dictionary<string, RecordingMediaSession> _sessions = new();
    private readonly Dictionary<string, CancellationToken> _tokens = new();

    /// <summary>Receives "start" / "connect" from every session this backend opened (ordering assertions).</summary>
    public Action<string>? Log { get; set; }
    public MediaCapabilities Capabilities => new(true, false, false);

    private TaskCompletionSource GateFor(Dictionary<string, TaskCompletionSource> map, string name)
    {
        lock (_lock)
        {
            if (!map.TryGetValue(name, out TaskCompletionSource? gate))
                map[name] = gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return gate;
        }
    }

    /// <summary>Let the open of <paramref name="name"/> return its session.</summary>
    public void Release(string name) => GateFor(_gates, name).TrySetResult();
    /// <summary>Let the open of <paramref name="name"/> throw.</summary>
    public void Fail(string name) => GateFor(_gates, name).TrySetException(new InvalidOperationException("open failed: " + name));
    /// <summary>Completes once the open of <paramref name="name"/> is parked inside the backend.</summary>
    public Task Entered(string name) => GateFor(_entered, name).Task.WaitAsync(Limit);
    public RecordingMediaSession SessionFor(string name) { lock (_lock) return _sessions[name]; }
    public CancellationToken TokenFor(string name) { lock (_lock) return _tokens[name]; }

    public async ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        string name = ((FileSource)source).Path;
        var session = new RecordingMediaSession(name, Log);
        lock (_lock) { _sessions[name] = session; _tokens[name] = ct; }
        GateFor(_entered, name).TrySetResult();
        await GateFor(_gates, name).Task.ConfigureAwait(false);
        return session;
    }
}

/// <summary>A fake <see cref="IMediaSession"/> that counts every lifecycle call; <see cref="HoldDispose"/> makes its
/// <c>DisposeAsync</c> wait for <see cref="ReleaseDispose"/> (a slow native teardown).</summary>
internal sealed class RecordingMediaSession : IMediaSession
{
    private readonly Action<string>? _log;
    private readonly TaskCompletionSource _disposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _holdDispose;
    private int _start, _connect, _pause, _dispose, _play;

    public RecordingMediaSession(string name, Action<string>? log) { Name = name; _log = log; }

    public string Name { get; }
    public int StartCount => Volatile.Read(ref _start);
    public int ConnectCount => Volatile.Read(ref _connect);
    public int PauseCount => Volatile.Read(ref _pause);
    public int PlayCount => Volatile.Read(ref _play);
    public int DisposeCount => Volatile.Read(ref _dispose);
    /// <summary>Completes when <c>DisposeAsync</c> was entered.</summary>
    public Task DisposeStarted => _disposeStarted.Task;
    public void HoldDispose() => Volatile.Write(ref _holdDispose, true);
    public void ReleaseDispose() => _disposeGate.TrySetResult();

    public void Start() { Interlocked.Increment(ref _start); _log?.Invoke("start"); }
    public void ConnectSignals(MediaSignalSink sink) { Interlocked.Increment(ref _connect); _log?.Invoke("connect"); }
    public ValueTask PlayAsync() { Interlocked.Increment(ref _play); return ValueTask.CompletedTask; }
    public ValueTask PauseAsync() { Interlocked.Increment(ref _pause); return ValueTask.CompletedTask; }
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
    public void SetRate(double rate) { }
    public void SetVolume(double volume) { }
    public void SetMuted(bool muted) { }
    public VideoDelivery Video => VideoDelivery.None;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _dispose);
        _disposeStarted.TrySetResult();
        if (Volatile.Read(ref _holdDispose)) await _disposeGate.Task.ConfigureAwait(false);
    }
}
