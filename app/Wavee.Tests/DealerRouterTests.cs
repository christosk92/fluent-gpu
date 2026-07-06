using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// The dealer router now DECODES + ENQUEUES onto the LibrarySync loop (the single writer); the in-place apply / mark-dirty /
// refetch policy lives in the loop. Drive real pushes through a real router + real loop (StubTransport.PushEvent) and assert
// the store outcome. Barrier: LibrarySync.WaitForIdleAsync (a FIFO no-op behind the enqueued push).
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

    // ── playlist pushes (§2.2 three-way gate) ──
    [Fact]
    public async Task PlaylistPush_ParentRevMatch_AppliesOpsInPlace_AndAdvancesRevision()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", 1, 2, Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        var m = h.Store.Membership("spotify:playlist:p");
        Assert.Equal("spotify:track:b", Assert.Single(m).ItemUri);
        Assert.Equal(new byte[] { 2 }, h.Store.PlaylistRevision("spotify:playlist:p"));
        Assert.Equal(1, h.Sync.PushApplied);
        Assert.Equal(0, h.PlaylistGets);   // zero network
    }

    [Fact]
    public async Task PlaylistPush_ParentRevMismatch_MarksDirty_NoFetch()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", 9, 10, Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, h.Store.Membership("spotify:playlist:p").Count);   // unchanged
        Assert.Equal(1, h.Sync.PushMarkedDirty);
        Assert.Equal(0, h.PlaylistGets);                                   // anti-herd: no fetch
    }

    [Fact]
    public async Task PlaylistPush_Echo_StoredEqualsNewRev_NoOp()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 5 });
        using var router = new DealerRouter(h.Dealer, h.Sync);

        // new_revision == stored (5) → an echo of our own write → dropped before any store work.
        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", 4, 5, Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, h.Store.Membership("spotify:playlist:p").Count);
        Assert.Equal(1, h.Sync.EchoDropped);
        Assert.Equal(0, h.Sync.PushApplied);
    }

    [Fact]
    public async Task PlaylistPush_TornApply_FallsToDirty()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        using var router = new DealerRouter(h.Dealer, h.Sync);

        // parent matches but REM [0,+5] doesn't fit → torn apply → gate 3 (not open) → mark dirty, no fetch.
        h.Dealer.PushEvent(new WireEvent("hm://playlist/v2/playlist/p", Mod("spotify:playlist:p", 1, 2, Rem(0, 5)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(2, h.Store.Membership("spotify:playlist:p").Count);   // unchanged
        Assert.Equal(1, h.Sync.PushMarkedDirty);
        Assert.Equal(0, h.PlaylistGets);
    }

    // ── rootlist pushes (§2.2 / §2.8) ──
    static Pl.RootlistModificationInfo RootMod(byte parent, byte newRev, params Pl.Op[] ops)
    {
        var info = new Pl.RootlistModificationInfo { ParentRevision = ByteString.CopyFrom(parent), NewRevision = ByteString.CopyFrom(newRev) };
        info.Ops.AddRange(ops);
        return info;
    }

    [Fact]
    public async Task RootlistPush_RevMatch_AppliesInPlace_AndFoldsSavedSet_ShieldsPending()
    {
        await using var h = new SyncHarness(_ => SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetRootlist(new[]
        {
            new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0),
            new RootlistEntry(1, 0, "spotify:playlist:p2", null, 0),
        }, new byte[] { 1 });
        h.Store.SetSaved("playlists", "spotify:playlist:p1", true, SyncState.Confirmed);   // will be swept by the fold
        h.Mut.Save("playlists", "spotify:playlist:shield", true);                          // pending → shielded from removal
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent("hm://playlist/user/bob/rootlist", RootMod(1, 2, Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal("spotify:playlist:p2", Assert.Single(h.Store.Rootlist()).Uri);         // p1 removed in place
        Assert.Equal(new byte[] { 2 }, h.Store.RootlistRevision());
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p2"));                    // fold ADDED the survivor
        Assert.False(h.Store.IsSaved("playlists", "spotify:playlist:p1"));                   // fold REMOVED the departed
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:shield"));                // pending → survived the fold
        Assert.Equal(0, h.RootlistGets);
    }

    [Fact]
    public async Task RootlistPush_RevMismatch_FullFetches()
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(7) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:playlist:fresh" });
        slc.Contents = contents;

        await using var h = new SyncHarness(req => req.Url.Contains("/rootlist") ? SyncHarness.Ok(slc.ToByteArray()) : SyncHarness.Ok(Array.Empty<byte>()));
        h.Store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:old", null, 0) }, new byte[] { 1 });
        using var router = new DealerRouter(h.Dealer, h.Sync);

        h.Dealer.PushEvent(new WireEvent("hm://playlist/user/bob/rootlist", RootMod(9, 10, Rem(0, 1)).ToByteArray()));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.RootlistGets);                                                     // mismatch → full GET
        Assert.Equal("spotify:playlist:fresh", Assert.Single(h.Store.Rootlist()).Uri);       // replaced by the fetch
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:fresh"));                  // fold ran after the fetch
        Assert.Equal(new byte[] { 7 }, h.Store.RootlistRevision());
    }
}
