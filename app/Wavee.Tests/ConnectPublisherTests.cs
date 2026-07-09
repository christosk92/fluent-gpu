using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The outbound DeviceStatePublisher: NewConnection announce on the connection-id + local player_state on playback changes,
// with stable session/playback ids + dedup. Proto-building is delegated (here a string encoding for assertions).
public class ConnectPublisherTests
{
    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static ClusterDelta ContextCluster(string contextUri) =>
        new("us", false, default, contextUri, false, true, false, 0, 0, 0, 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    sealed class Harness
    {
        public readonly StubTransport Transport = new();
        public readonly NowPlayingProjection Proj = new("us", () => 0);
        public readonly SimpleSubject<string?> ConnId = new(null);
        public string? CurrentConnId;
        public readonly List<string> Built = new();
        public LocalPlaybackSnapshot? LastSnapshot;
        public readonly DeviceStatePublisher Publisher;

        public Harness()
        {
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, ConnId, () => CurrentConnId,
                (reason, snap, mid, active) =>
                {
                    LastSnapshot = snap;
                    var s = reason + "|" + active + "|" + (snap?.Track.Uri ?? "-") + "|" + (snap?.SessionId ?? "");
                    Built.Add(s);
                    return Encoding.UTF8.GetBytes(s);
                },
                onCluster: null, clock: () => 1000);
        }

        public void Connect(string id) { CurrentConnId = id; ConnId.OnNext(id); }
        // proj + publisher both see the event (the controller fans to both in production)
        public void Play(string trackUri, EvKind kind = EvKind.Started)
        {
            var e = new PlaybackEvent(kind, T(trackUri), 0);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }

        // Emit a state event for the CURRENT track (mirrors the controller's EmitState).
        public void Emit(EvKind kind, long atMs = 0)
        {
            var e = new PlaybackEvent(kind, Proj.CurrentTrack, atMs);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }
        public void SetOptions(bool shuffle, RepeatMode repeat) => Proj.SetLocalOptions(shuffle, repeat);
        public void SetVolume(double v) => Proj.SetLocalVolume(v);
        public void SetQueue(params QueueEntry[] q) => Proj.SetLocalQueue(q);
    }

    [Fact]
    public async Task OnConnectionId_AnnouncesNewConnection()
    {
        var h = new Harness();
        h.Connect("c1");
        await Task.Delay(20);
        Assert.Equal(1, h.Transport.PublishCount);
        Assert.StartsWith("NewConnection|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task BeforeConnectionId_DoesNotPublish()
    {
        var h = new Harness();
        h.Play("spotify:track:a");   // no connection id yet → can't PUT
        await Task.Delay(20);
        Assert.Equal(0, h.Transport.PublishCount);
    }

    [Fact]
    public async Task LocalPlay_PublishesPlayerStateChanged_Active()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a");
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);   // NewConnection + PlayerStateChanged
        Assert.Contains("PlayerStateChanged|True|spotify:track:a", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task DedupsIdenticalPlayerState()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a", EvKind.Started);
        h.Play("spotify:track:a", EvKind.Resumed);   // same salient state → deduped
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);    // NewConnection + one PlayerStateChanged
    }

    [Fact]
    public async Task NewContext_MintsDifferentSessionId()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Proj.OnCluster(ContextCluster("spotify:playlist:A"));
        h.Play("spotify:track:a");
        h.Proj.OnCluster(ContextCluster("spotify:playlist:B"));
        h.Play("spotify:track:b");
        await Task.Delay(20);

        var sessions = h.Built.FindAll(b => b.StartsWith("PlayerStateChanged")).ConvertAll(b => b.Split('|')[3]);
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(sessions[0], sessions[1]);   // different context → different session id
    }

    // ── Phase C: PutState now publishes on EVERY salient local change (not just track boundaries) ─────────────────────
    [Fact]
    public async Task Pause_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a"); h.Emit(EvKind.Paused);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // NewConnection + Started + Paused
    }

    [Fact]
    public async Task Seek_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a"); h.Emit(EvKind.Seeked, 5000);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // position jumped → not deduped
    }

    [Fact]
    public async Task OptionsChange_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a");
        h.SetOptions(true, RepeatMode.Context); h.Emit(EvKind.OptionsChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // shuffle/repeat changed → not deduped
    }

    [Fact]
    public async Task VolumeChange_Publishes_WithVolumeChangedReason()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a");
        h.SetVolume(0.25); h.Emit(EvKind.VolumeChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);
        Assert.StartsWith("VolumeChanged|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task QueueChange_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a");
        h.SetQueue(new QueueEntry(QueueItemId.None, "now", T("spotify:track:a"), QueueBucket.NowPlaying, QueueProvider.Context, false, "u0"),
                   new QueueEntry(QueueItemId.None, "q0", T("spotify:track:q"), QueueBucket.UserQueue, QueueProvider.Queue, false, "uq"));
        h.Emit(EvKind.QueueChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // up-next changed → not deduped
    }

    [Fact]
    public async Task QueueSnapshot_CapsWireNextTracks_LocalHistoryNotPublished()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:now");
        var queue = new List<QueueEntry>();
        for (int i = 0; i < 55; i++)
            queue.Add(new QueueEntry(QueueItemId.None, "h" + i, T("spotify:track:h" + i), QueueBucket.History, QueueProvider.Context, false, "uh" + i));
        queue.Add(new QueueEntry(QueueItemId.None, "now", T("spotify:track:now"), QueueBucket.NowPlaying, QueueProvider.Context, false, "unow"));
        for (int i = 0; i < 55; i++)
            queue.Add(new QueueEntry(QueueItemId.None, "n" + i, T("spotify:track:n" + i), QueueBucket.NextUp, QueueProvider.Context, false, "un" + i));

        h.SetQueue(queue.ToArray());
        h.Emit(EvKind.QueueChanged);
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.Empty(snap.PrevTracks);   // local history stays client-side until server-driven history lands
        Assert.Equal(50, snap.NextTracks.Count);
        Assert.Equal("spotify:track:n0", snap.NextTracks[0].Uri);
        Assert.Equal("spotify:track:n49", snap.NextTracks[49].Uri);
    }

    [Fact]
    public async Task BecameInactive_Publishes_IsActiveFalse()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a"); h.Emit(EvKind.BecameInactive);
        await Task.Delay(20);
        Assert.StartsWith("BecameInactive|False|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task NoOpRepeatOfSameState_StaysDeduped()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a", EvKind.Started);
        h.Emit(EvKind.OptionsChanged);   // options unchanged (default) + same track/pos → identical key
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);   // NewConnection + the one Started; the no-op OptionsChanged collapses
    }
}
