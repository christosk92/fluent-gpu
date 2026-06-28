using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// The single dealer firehose router: decode hm://playlist / hm://collection pushes and apply the two-step policy —
// parent-rev match => apply ops in place (zero network); otherwise / not-resident => mark dirty only (anti-herd).
public class DealerRouterTests
{
    static PlaylistMember M(string id) => new(id, "spotify:track:" + id, null, 0);

    static Pl.PlaylistModificationInfo Mod(string uri, byte parent, byte? newRev, params Pl.Op[] ops)
    {
        var info = new Pl.PlaylistModificationInfo { Uri = ByteString.CopyFromUtf8(uri), ParentRevision = ByteString.CopyFrom(parent) };
        if (newRev is { } nr) info.NewRevision = ByteString.CopyFrom(nr);
        info.Ops.AddRange(ops);
        return info;
    }

    static Pl.Op Rem(int from, int len) => new() { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = from, Length = len } };

    [Fact]
    public void PlaylistPush_ParentRevMatch_AppliesOpsInPlace_AndAdvancesRevision()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        var transport = new StubTransport();
        var stale = new List<string>();
        using var router = new DealerRouter(transport, store, u => stale.Add(u), _ => { });

        transport.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", 1, 2, Rem(0, 1)).ToByteArray()));

        var m = store.Membership("spotify:playlist:p");
        Assert.Equal("spotify:track:b", Assert.Single(m).ItemUri);            // op applied in place
        Assert.Equal(new byte[] { 2 }, store.PlaylistRevision("spotify:playlist:p"));  // revision advanced
        Assert.Empty(stale);                                                 // NOT marked stale — zero network
    }

    [Fact]
    public void PlaylistPush_ParentRevMismatch_MarksStaleOnly()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        var transport = new StubTransport();
        var stale = new List<string>();
        using var router = new DealerRouter(transport, store, u => stale.Add(u), _ => { });

        transport.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", 9, 10, Rem(0, 1)).ToByteArray()));

        Assert.Equal(2, store.Membership("spotify:playlist:p").Count);       // unchanged
        Assert.Contains("spotify:playlist:p", stale);                        // dirty → lazy revalidate on next open
    }

    [Fact]
    public void PlaylistPush_NotResident_MarksStaleOnly_NeverFetches()
    {
        var store = new InMemoryStore();   // no resident membership for this playlist
        var transport = new StubTransport();
        var stale = new List<string>();
        using var router = new DealerRouter(transport, store, u => stale.Add(u), _ => { });

        transport.PushEvent(new WireEvent("hm://playlist/v2/playlist/cold", Mod("spotify:playlist:cold", 1, 2, Rem(0, 1)).ToByteArray()));

        Assert.Contains("spotify:playlist:cold", stale);                     // COLD push → mark dirty only (the anti-herd)
    }

    [Fact]
    public void CollectionPush_MarksTheSetStale()
    {
        var store = new InMemoryStore();
        var transport = new StubTransport();
        var staleSets = new List<string>();
        using var router = new DealerRouter(transport, store, _ => { }, s => staleSets.Add(s));

        transport.PushEvent(new WireEvent("hm://collection/collection/bob/json", System.Array.Empty<byte>()));

        Assert.NotEmpty(staleSets);   // a collection push invalidates freshness (lazy /delta on next read)
    }
}
