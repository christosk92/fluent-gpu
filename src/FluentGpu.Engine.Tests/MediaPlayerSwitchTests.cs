using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A real <see cref="MediaPlayer"/> through an in-place Switch (a second <c>OpenAsync</c> on the same player, which is
/// what Wavee's plan does for every key change). The element's poster/hole gate and Wavee's first-frame heuristics read
/// the core's per-source signals, so the Opening hop must hand the new source a core that remembers nothing of the
/// previous one: a stale <see cref="IMediaPlayer.VideoSurface"/> keeps the hole punched and the poster down over the
/// held outgoing frame, and a stale <see cref="IMediaPlayer.NaturalSize"/> reads as "a frame was seen".
/// </summary>
public sealed class MediaPlayerSwitchTests
{
    private static void Wait(ValueTask vt)
    {
        var task = vt.AsTask();
        if (!task.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Media operation did not complete within five seconds.");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Switch_ReturnsSurfaceSizeDurationAndPositionToNone_SoThePosterShowsAgain()
    {
        var backend = new SwitchBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();

        Wait(player.OpenAsync(MediaSource.FromFile("a.mp4")));
        MediaSignalSink first = backend.Sink!;   // one sink for the player's whole life: every session writes through it
        first.NaturalSize(new SizeI(1280, 720));
        first.Duration(TimeSpan.FromSeconds(30));
        first.Position(TimeSpan.FromSeconds(12));
        first.VideoSurface(new VideoSurfaceId(1));
        first.State(PlaybackState.Playing);
        Assert.False(player.VideoSurface.Peek().IsNone);   // the previous source was presenting

        // What the core holds at the instant the backend is asked to open the NEXT source (after the Opening hop).
        PlaybackState stateAtOpen = PlaybackState.Idle;
        VideoSurfaceId surfaceAtOpen = new(99);
        SizeI naturalAtOpen = new(1, 1);
        TimeSpan durationAtOpen = TimeSpan.MaxValue, positionAtOpen = TimeSpan.MaxValue;
        backend.OnOpen = () =>
        {
            stateAtOpen = player.State.Peek();
            surfaceAtOpen = player.VideoSurface.Peek();
            naturalAtOpen = player.NaturalSize.Peek();
            durationAtOpen = player.Duration.Peek();
            positionAtOpen = player.Position.Peek();
        };

        Wait(player.OpenAsync(MediaSource.FromFile("b.mp4")));

        Assert.Equal(PlaybackState.Opening, stateAtOpen);
        Assert.True(surfaceAtOpen.IsNone);                 // the poster is back until THIS source presents
        Assert.True(naturalAtOpen.IsEmpty);                // no size left over to read as a first frame
        Assert.Equal(TimeSpan.Zero, durationAtOpen);
        Assert.Equal(TimeSpan.Zero, positionAtOpen);

        // The new session republishes from its own metadata and the surface comes back with its first frame.
        Assert.True(player.VideoSurface.Peek().IsNone);
        first.NaturalSize(new SizeI(640, 360));
        first.VideoSurface(new VideoSurfaceId(1));
        Assert.Equal(new SizeI(640, 360), player.NaturalSize.Peek());
        Assert.False(player.VideoSurface.Peek().IsNone);
        Wait(player.DisposeAsync());
    }

    [Fact]
    public void Switch_WithAStartPosition_KeepsTheRequestedPositionAfterTheReset()
    {
        var backend = new SwitchBackend();
        var player = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, backend).Build();
        Wait(player.OpenAsync(MediaSource.FromFile("a.mp4")));
        backend.Sink!.Position(TimeSpan.FromSeconds(12));

        TimeSpan positionAtOpen = TimeSpan.MaxValue;
        backend.OnOpen = () => positionAtOpen = player.Position.Peek();
        Wait(player.OpenAsync(MediaSource.FromFile("b.mp4"), new MediaOpenOptions { StartPosition = TimeSpan.FromSeconds(7) }));

        Assert.Equal(TimeSpan.FromSeconds(7), positionAtOpen);   // reset to zero first, THEN the caller's start position
        Wait(player.DisposeAsync());
    }

    private sealed class SwitchBackend : IMediaBackend
    {
        /// <summary>The sink of the most recently connected session.</summary>
        public MediaSignalSink? Sink;
        /// <summary>Runs when the facade asks this backend to open a source.</summary>
        public Action? OnOpen;
        public MediaCapabilities Capabilities => new(true, false, false);
        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
        {
            OnOpen?.Invoke();
            return new(new SwitchSession(this));
        }
    }

    private sealed class SwitchSession : IMediaSession
    {
        private readonly SwitchBackend _owner;
        public SwitchSession(SwitchBackend owner) => _owner = owner;
        public void ConnectSignals(MediaSignalSink sink) => _owner.Sink = sink;
        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
