using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Stage B — the Connect control-plane orchestrator: capture the dealer connection_id from the pusher hello HEADER and
// announce the device (PUT) once per connection id, re-injecting the returned Cluster. Proto-building is delegated, so this
// is exercised entirely against StubTransport.
public class ConnectServiceTests
{
    static WireEvent Pusher(string connId) =>
        new("hm://pusher/v1/connections/gae2", Array.Empty<byte>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Spotify-Connection-Id"] = connId });

    [Fact]
    public async Task CapturesConnectionId_AnnouncesOnce_AndReinjectsCluster()
    {
        var t = new StubTransport { PublishResponse = new byte[] { 1, 2, 3 } };
        byte[]? cluster = null;
        int built = 0;
        using var svc = new ConnectService(t, "dev-1", mid => { built++; return new byte[] { (byte)mid }; }, b => cluster = b);

        t.PushEvent(Pusher("conn-1"));
        await Task.Delay(50);

        Assert.Equal("conn-1", svc.CurrentConnectionId);
        Assert.Equal(1, t.PublishCount);
        Assert.Equal(1, built);
        Assert.Equal(new byte[] { 1 }, t.LastPublishBody);     // message_id 1 → buildPutState(1)
        Assert.Equal(new byte[] { 1, 2, 3 }, cluster);         // the announce-response Cluster is re-injected
    }

    [Fact]
    public async Task DuplicateConnectionId_AnnouncesOnce_NewIdReAnnounces()
    {
        var t = new StubTransport();
        using var svc = new ConnectService(t, "dev-1", mid => new byte[] { (byte)mid });

        t.PushEvent(Pusher("conn-1"));
        t.PushEvent(Pusher("conn-1"));   // duplicate → no re-announce
        await Task.Delay(50);
        Assert.Equal(1, t.PublishCount);

        t.PushEvent(Pusher("conn-2"));   // a reconnect issues a fresh id → re-announce (message_id increments)
        await Task.Delay(50);
        Assert.Equal(2, t.PublishCount);
        Assert.Equal(new byte[] { 2 }, t.LastPublishBody);
    }

    [Fact]
    public async Task IgnoresPusher_WithoutConnectionIdHeader()
    {
        var t = new StubTransport();
        using var svc = new ConnectService(t, "dev-1", mid => new byte[] { 1 });
        t.PushEvent(new WireEvent("hm://pusher/v1/connections/x", Array.Empty<byte>()));   // no header
        await Task.Delay(50);
        Assert.Equal(0, t.PublishCount);
        Assert.Null(svc.CurrentConnectionId);
    }
}
