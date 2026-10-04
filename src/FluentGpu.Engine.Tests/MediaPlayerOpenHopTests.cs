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
}
