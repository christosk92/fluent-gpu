using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary><see cref="MediaPlayer.CloseAsync"/> (F152): a host that parks between videos keeps ONE player for the process.
/// Close releases the session and clears everything the core still showed, but the facade stays usable and wired — the
/// next open is a plain first open and a mounted element's pump subscription survives.</summary>
public sealed class MediaPlayerCloseTests
{
    /// <summary>A backend whose sessions count their own disposals and publish a recognisable picture of "a video is open".</summary>
    private sealed class CountingBackend : IMediaBackend
    {
        public int Opened;
        public CountingSession? Last;
        public MediaCapabilities Capabilities { get; } = new(SupportsVideo: true, SupportsAudioGraph: false, SupportsDrm: false);

        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
        {
            Opened++;
            Last = new CountingSession();
            return ValueTask.FromResult<IMediaSession>(Last);
        }
    }

    private sealed class CountingSession : IMediaSession
    {
        public int Disposed;

        public void ConnectSignals(MediaSignalSink sink)
        {
            sink.State(PlaybackState.Playing);
            sink.PlayRequested(true);
            sink.Duration(TimeSpan.FromSeconds(90));
            sink.Position(TimeSpan.FromSeconds(12));
            sink.NaturalSize(new SizeI(1280, 720));
        }

        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private static MediaPlayer Build(CountingBackend backend)
        => MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();

    private static void Wait(ValueTask task) => task.AsTask().GetAwaiter().GetResult();

    [Fact]
    public void Close_DisposesTheSession_AndClearsWhatTheCoreStillShowed()
    {
        var backend = new CountingBackend();
        var player = Build(backend);
        Wait(player.OpenAsync(MediaSource.FromFile("a.mp4")));
        CountingSession first = backend.Last!;
        Assert.Same(first, player.Session);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Assert.False(player.NaturalSize.Peek().IsEmpty);

        Wait(player.CloseAsync());

        Assert.Equal(1, first.Disposed);
        Assert.Null(player.Session);
        Assert.Equal(PlaybackState.Idle, player.State.Peek());
        Assert.False(player.IsPlayRequested.Peek());
        Assert.False(player.IsPlaying.Peek());
        Assert.Null(player.Error.Peek());
        Assert.True(player.NaturalSize.Peek().IsEmpty);
        Assert.Equal(TimeSpan.Zero, player.Position.Peek());
        Assert.Equal(TimeSpan.Zero, player.Duration.Peek());
        Assert.True(player.VideoSurface.Peek().IsNone);
        Assert.Empty(player.Tracks.Text);
        Assert.Empty(player.Qualities.Variants);
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Close_LeavesTheFacadeUsable_TheNextOpenIsAPlainFirstOpen()
    {
        var backend = new CountingBackend();
        var player = Build(backend);
        Wait(player.OpenAsync(MediaSource.FromFile("a.mp4")));
        CountingSession first = backend.Last!;
        Wait(player.CloseAsync());

        Wait(player.OpenAsync(MediaSource.FromFile("b.mp4")));

        CountingSession second = backend.Last!;
        Assert.NotSame(first, second);
        Assert.Equal(2, backend.Opened);
        Assert.Same(second, player.Session);
        Assert.Equal(1, first.Disposed);            // the open found no session to dispose a second time
        Assert.Equal(0, second.Disposed);
        Assert.Equal(PlaybackState.Playing, player.State.Peek());
        Assert.False(player.NaturalSize.Peek().IsEmpty);

        Wait(player.DisposeAsync());
        Assert.Equal(1, second.Disposed);
    }

    [Fact]
    public void Close_IsIdempotent_AndANoOpBeforeAnOpenOrAfterDispose()
    {
        var backend = new CountingBackend();
        var player = Build(backend);
        Wait(player.CloseAsync());                  // nothing open yet
        Assert.Null(player.Session);

        Wait(player.OpenAsync(MediaSource.FromFile("a.mp4")));
        CountingSession session = backend.Last!;
        Wait(player.CloseAsync());
        Wait(player.CloseAsync());
        Assert.Equal(1, session.Disposed);

        Wait(player.DisposeAsync());
        Wait(player.CloseAsync());                  // disposed: still a no-op, never a throw
        Assert.Equal(1, session.Disposed);
    }

    [Fact]
    public void Close_KeepsThePumpSubscriptionWired_SoAMountedElementIsToldTheSessionIsGone()
    {
        var backend = new CountingBackend();
        var player = Build(backend);
        int pumps = 0;
        player.PumpRequested += () => pumps++;
        Wait(player.OpenAsync(MediaSource.FromFile("a.mp4")));

        int before = pumps;
        Wait(player.CloseAsync());
        Assert.True(pumps > before);                // the element re-pumps and sees no session

        before = pumps;
        Wait(player.OpenAsync(MediaSource.FromFile("b.mp4")));
        Assert.True(pumps > before);                // …and the same subscription hears the next open

        Wait(player.DisposeAsync());
    }
}
