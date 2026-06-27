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
        public readonly DeviceStatePublisher Publisher;

        public Harness()
        {
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, ConnId, () => CurrentConnId,
                (reason, snap, mid, active) =>
                {
                    var s = reason + "|" + active + "|" + (snap?.TrackUri ?? "-") + "|" + (snap?.SessionId ?? "");
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
}
