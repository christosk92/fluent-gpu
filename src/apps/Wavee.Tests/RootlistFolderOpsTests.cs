using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// ── P3: rootlist folder CRUD ─────────────────────────────────────────────────────────────────────────────────────────
// A folder is a balanced marker pair in the flat rootlist item stream, so every folder op is an index ADD/REM. The three
// builders are pinned BYTE-EXACT against the desktop captures (a164 create, b037/b128 rename); the delete shape is
// reference-inferred (no capture exists) and is pinned behaviourally instead: end marker first, children untouched.
public class RootlistFolderOpsTests
{
    static readonly SessionContext Ctx = new("bob", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class RecTransport(Func<string, byte[], int, Resp> respond) : ITransport
    {
        public readonly List<(string Route, string Method, byte[] Body)> Sent = new();
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            var b = body.ToArray();
            Sent.Add((route, method ?? (body.IsEmpty ? "GET" : "POST"), b));
            return Task.FromResult(respond(route, b, Sent.Count));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    static PlaylistMutationSource Source(IStore store, ITransport transport)
    {
        var engine = new MutationEngine(store, new IMutationStrategy[]
        {
            new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()),
        });
        var http = new FakeExchange((_, _) => new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        return new PlaylistMutationSource(engine, transport, http, () => Ctx,
            () => "https://spclient.wg.spotify.com", new UserPlaylistSource(), new RootlistLane(), store);
    }

    static IReadOnlyList<RootlistEntry> Entries(params (string Uri, long Ts)[] rows)
    {
        var uris = new string[rows.Length];
        var stamps = new long[rows.Length];
        for (int i = 0; i < rows.Length; i++) { uris[i] = rows[i].Uri; stamps[i] = rows[i].Ts; }
        return RootlistTreeBuilder.EntriesFromUris(uris, stamps);
    }

    static byte[] RootlistBody(IReadOnlyList<PlaylistOp> ops, byte[] baseRev, string user, long nowMs, long nonce)
        => PlaylistWireMapper.BuildRootlistChanges(baseRev, ops, user, nowMs, nonce);

    // ── 1. create: ONE delta, TWO ADDs, byte-exact against a164 ─────────────────────────────────────────────────────
    [Fact]
    public void Create_TwoAddsOneDelta_GoldenA164()
    {
        var captured = Golden.Bytes("a164-folder-create");
        var changes = Golden.Changes("a164-folder-create");
        var delta = Assert.Single(changes.Deltas);
        // Everything the capture fixes: the group id, the name (space encoded as '+'), the create timestamp, the index,
        // the signing user and the per-session nonce.
        long createdAt = delta.Ops[0].Add.Items[0].Attributes.Timestamp;

        var ops = RootlistOps.BuildCreateFolder(Array.Empty<RootlistEntry>(), "edb339e10aebcf38", "New Folder", insertAt: 0, nowMs: createdAt);
        var rebuilt = RootlistBody(ops, changes.BaseRevision.ToByteArray(), delta.Info.User, delta.Info.Timestamp, nonce: 2);

        Assert.Equal(captured, rebuilt);
        Assert.Equal(2, ops.Count);
        Assert.Equal("spotify:start-group:edb339e10aebcf38:New+Folder", ops[0].Items![0].ItemUri);
        Assert.Equal("spotify:end-group:edb339e10aebcf38", ops[1].Items![0].ItemUri);
        Assert.Equal(0, ops[0].FromIndex);
        Assert.Equal(1, ops[1].FromIndex);
        // and NO public attribute on a folder marker (that is a playlist-row attribute — a042)
        Assert.All(delta.Ops, op => Assert.False(op.Add.Items[0].Attributes.HasPublic));
    }

    // ── 2. rename: REM without items + ADD carrying the ORIGINAL create timestamp ────────────────────────────────────
    [Fact]
    public void Rename_RemNoItems_AddOriginalTs_GoldenB037()
    {
        var captured = Golden.Bytes("b037-folder-rename");
        var changes = Golden.Changes("b037-folder-rename");
        var delta = Assert.Single(changes.Deltas);
        long originalTs = delta.Ops[1].Add.Items[0].Attributes.Timestamp;

        // the folder sits at index 2 of the marker stream, carrying the timestamp it was created with
        var entries = Entries(
            ("spotify:playlist:a", 0),
            ("spotify:playlist:b", 0),
            ("spotify:start-group:edb339e10aebcf38:New+Folder", originalTs),
            ("spotify:end-group:edb339e10aebcf38", originalTs));

        var ops = RootlistOps.BuildRenameFolder(entries, "edb339e10aebcf38", "named folder update", nowMs: 1)!;
        var rebuilt = RootlistBody(ops, changes.BaseRevision.ToByteArray(), delta.Info.User, delta.Info.Timestamp, nonce: 16);

        Assert.Equal(captured, rebuilt);
        Assert.Equal(PlaylistOpKind.Remove, ops[0].Kind);
        Assert.Equal(2, ops[0].FromIndex);
        Assert.Equal(1, ops[0].Length);
        Assert.Null(ops[0].Items);                               // a rename REM names nothing — it is purely positional
        Assert.Equal(originalTs, ops[1].Items![0].AddedAt);      // NOT "now": the create timestamp is resent verbatim
        Assert.NotEqual(originalTs, delta.Info.Timestamp);
    }

    [Fact]
    public void Rename_Outer_GoldenB128()
    {
        var captured = Golden.Bytes("b128-folder-rename-outer");
        var changes = Golden.Changes("b128-folder-rename-outer");
        var delta = Assert.Single(changes.Deltas);
        long originalTs = delta.Ops[1].Add.Items[0].Attributes.Timestamp;

        var entries = Entries(
            ("spotify:start-group:3dd9e795c88ae3e4:root+folder", originalTs),
            ("spotify:playlist:child", 0),
            ("spotify:end-group:3dd9e795c88ae3e4", originalTs));

        var ops = RootlistOps.BuildRenameFolder(entries, "3dd9e795c88ae3e4", "root folder updated name", nowMs: 1)!;
        var rebuilt = RootlistBody(ops, changes.BaseRevision.ToByteArray(), delta.Info.User, delta.Info.Timestamp, nonce: 18);

        Assert.Equal(captured, rebuilt);
        Assert.Equal(0, ops[0].FromIndex);
    }

    // ── 3. delete: end marker first, children stay ──────────────────────────────────────────────────────────────────
    [Fact]
    public void Delete_RemEndThenStart_ChildrenStay()
    {
        var entries = Entries(
            ("spotify:playlist:before", 0),
            ("spotify:start-group:g1:Trips", 100),
            ("spotify:playlist:inside", 0),
            ("spotify:end-group:g1", 100),
            ("spotify:playlist:after", 0));

        var ops = RootlistOps.BuildDeleteFolder(entries, "g1")!;

        Assert.Equal(2, ops.Count);
        Assert.All(ops, op => Assert.Equal(PlaylistOpKind.Remove, op.Kind));
        Assert.Equal(3, ops[0].FromIndex);   // the END marker first — removing the start first would shift this by one
        Assert.Equal(1, ops[0].Length);
        Assert.Equal(1, ops[1].FromIndex);
        Assert.Equal(1, ops[1].Length);
        Assert.All(ops, op => Assert.Null(op.Items));

        var after = RootlistOps.ApplyLocally(entries, ops);
        Assert.Equal(new[] { "spotify:playlist:before", "spotify:playlist:inside", "spotify:playlist:after" },
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(after, e => e.Uri)));
        Assert.All(after, e => Assert.Equal(0, e.Depth));   // the child moved up a level, it was not deleted
    }

    [Fact]
    public void Delete_NestedFolder_TakesTheWholeSubtreeWithIt_AsMarkersOnly()
    {
        var entries = Entries(
            ("spotify:start-group:outer:Outer", 100),
            ("spotify:start-group:inner:Inner", 200),
            ("spotify:playlist:deep", 0),
            ("spotify:end-group:inner", 200),
            ("spotify:end-group:outer", 100));

        var ops = RootlistOps.BuildDeleteFolder(entries, "outer")!;
        var after = RootlistOps.ApplyLocally(entries, ops);

        Assert.Equal(4, ops[0].FromIndex);                   // outer's END
        Assert.Equal(0, ops[1].FromIndex);                   // then outer's START
        Assert.Equal(3, after.Count);                        // the inner folder and its child survive, one level up
        Assert.Equal("spotify:start-group:inner:Inner", after[0].Uri);
        Assert.Equal(0, after[0].Depth);
        Assert.Equal(1, after[1].Depth);
    }

    [Fact]
    public void Builders_ReturnNull_WhenTheFolderIsGone()
    {
        var entries = Entries(("spotify:playlist:a", 0));
        Assert.Null(RootlistOps.BuildRenameFolder(entries, "nope", "x", nowMs: 1));
        Assert.Null(RootlistOps.BuildDeleteFolder(entries, "nope"));
        Assert.Equal(-1, RootlistOps.PlacementIndex(entries, new RootlistPlacement("nope")));
        Assert.Equal(0, RootlistOps.PlacementIndex(entries, default));
    }

    [Fact]
    public void FolderName_EncodesSpacesAsPlus_AndEscapesTheRest()
    {
        Assert.Equal("spotify:start-group:g:New+Folder", RootlistOps.StartGroupUri("g", "New Folder"));
        Assert.Equal("spotify:start-group:g:a%2Fb", RootlistOps.StartGroupUri("g", "a/b"));
        // and the parse side reads it back (the tree builder unescapes; '+' is a literal there, which is desktop's own
        // asymmetry — the name shown for "New Folder" round-trips through EntriesFromUris as "New+Folder").
        Assert.Equal("spotify:end-group:g", RootlistOps.EndGroupUri("g"));
    }

    // ── 4. the source: online-only, lane-serialized, one rebase on 409 ───────────────────────────────────────────────
    [Fact]
    public async Task CreateFolder_PostsOnce_AndAppliesTheTreeLocally()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:a", 0)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        string groupId = await source.CreateFolderAsync("Trips", default, Ct);

        var req = Assert.Single(t.Sent);
        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", req.Route);
        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(req.Body).Deltas);
        Assert.Equal(2, delta.Ops.Count);
        Assert.Equal($"spotify:start-group:{groupId}:Trips", delta.Ops[0].Add.Items[0].Uri);

        var rows = store.Rootlist();
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].Kind);                                     // the folder landed at the top
        Assert.Equal("Trips", rows[0].GroupName);
        Assert.True(rows[0].AddedAtMs > 0);
        Assert.Equal(2, rows[1].Kind);
        Assert.Equal("spotify:playlist:a", rows[2].Uri);
        Assert.Equal(Rev24(1), store.RootlistRevision());                  // the reply advanced nothing; the rev stands
    }

    [Fact]
    public async Task CreateFolder_InsideAnotherFolder_LandsAfterItsStartMarker()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:outer:Outer", 5), ("spotify:end-group:outer", 5)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        string groupId = await source.CreateFolderAsync("Inner", new RootlistPlacement("outer"), Ct);

        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(t.Sent).Body).Deltas);
        Assert.Equal(1, delta.Ops[0].Add.FromIndex);
        Assert.Equal(2, delta.Ops[1].Add.FromIndex);
        var rows = store.Rootlist();
        Assert.Equal($"spotify:start-group:{groupId}:Inner", rows[1].Uri);
        Assert.Equal(1, rows[1].Depth);
    }

    [Fact]
    public async Task Rename_409_RebasesOnce()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:g1:Old", 77)), Rev24(1));
        // 1st POST → 409; the bootstrap GET returns the rootlist as the server has it (the folder moved down one row);
        // the 2nd POST must carry the RECOMPUTED index and still the original timestamp.
        var bootstrap = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(2)) };
        bootstrap.Contents = new Pl.ListItems();
        bootstrap.Contents.Items.Add(new Pl.Item { Uri = "spotify:playlist:new" });
        bootstrap.Contents.Items.Add(new Pl.Item
        {
            Uri = "spotify:start-group:g1:Old",
            Attributes = new Pl.ItemAttributes { Timestamp = 77 },
        });
        var t = new RecTransport((route, body, call) => call switch
        {
            1 => new Resp(false, Array.Empty<byte>(), 409),
            2 => new Resp(true, bootstrap.ToByteArray(), 200),        // the bootstrap GET
            _ => new Resp(true, Array.Empty<byte>(), 200),
        });
        var source = Source(store, t);

        await source.RenameFolderAsync("g1", "New", Ct);

        Assert.Equal(3, t.Sent.Count);
        Assert.Equal("GET", t.Sent[1].Method);
        var retry = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[2].Body).Deltas);
        Assert.Equal(1, retry.Ops[0].Rem.FromIndex);                 // recomputed against the bootstrapped stream
        Assert.Equal(1, retry.Ops[1].Add.FromIndex);
        Assert.Equal("spotify:start-group:g1:New", retry.Ops[1].Add.Items[0].Uri);
        Assert.Equal(77L, retry.Ops[1].Add.Items[0].Attributes.Timestamp);
        Assert.Equal("New", store.Rootlist()[1].GroupName);
    }

    [Fact]
    public async Task Rename_MissingTimestamp_BootstrapsFirst()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:g1:Old", 0)), Rev24(1));   // no timestamp captured
        var bootstrap = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(1)) };
        bootstrap.Contents = new Pl.ListItems();
        bootstrap.Contents.Items.Add(new Pl.Item
        {
            Uri = "spotify:start-group:g1:Old",
            Attributes = new Pl.ItemAttributes { Timestamp = 4242 },
        });
        var t = new RecTransport((route, body, call) => call == 1
            ? new Resp(true, bootstrap.ToByteArray(), 200)
            : new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        await source.RenameFolderAsync("g1", "New", Ct);

        Assert.Equal(2, t.Sent.Count);
        Assert.Equal("GET", t.Sent[0].Method);                                     // the bootstrap came FIRST
        Assert.StartsWith("/playlist/v2/user/bob/rootlist?", t.Sent[0].Route, StringComparison.Ordinal);
        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).Deltas);
        Assert.Equal(4242L, delta.Ops[1].Add.Items[0].Attributes.Timestamp);       // the ts the GET carried, not "now"
    }

    [Fact]
    public async Task DeleteFolder_RemovesBothMarkers_AndKeepsChildren()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(
            ("spotify:start-group:g1:Trips", 9),
            ("spotify:playlist:inside", 0),
            ("spotify:end-group:g1", 9)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        await source.DeleteFolderAsync("g1", Ct);

        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(Assert.Single(t.Sent).Body).Deltas);
        Assert.Equal(2, delta.Ops[0].Rem.FromIndex);
        Assert.Equal(0, delta.Ops[1].Rem.FromIndex);
        Assert.Equal("spotify:playlist:inside", Assert.Single(store.Rootlist()).Uri);
    }

    [Fact]
    public async Task FolderOps_AreOnlineOnly()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:start-group:g1:Trips", 9), ("spotify:end-group:g1", 9)), Rev24(1));
        var source = Source(store, new StubTransport());   // the named offline stand-in

        var create = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.CreateFolderAsync("x", default, Ct));
        var rename = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.RenameFolderAsync("g1", "x", Ct));
        var delete = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.DeleteFolderAsync("g1", Ct));

        Assert.Equal(PlaylistMutationFailure.Offline, create.Kind);
        Assert.Equal(PlaylistMutationFailure.Offline, rename.Kind);
        Assert.Equal(PlaylistMutationFailure.Offline, delete.Kind);
    }

    [Fact]
    public async Task RenameFolder_Gone_IsDeleted_NotSilent()
    {
        var store = new InMemoryStore();
        store.SetRootlist(Entries(("spotify:playlist:a", 0)), Rev24(1));
        var t = new RecTransport((_, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var source = Source(store, t);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.RenameFolderAsync("gone", "x", Ct));

        Assert.Equal(PlaylistMutationFailure.Deleted, ex.Kind);
    }

    [Fact]
    public void LocalSource_RefusesFolderOps_ByName()
    {
        var local = new LocalPlaylistMutationSource(new UserPlaylistSource());
        Assert.Throws<NotSupportedException>(() => { _ = local.CreateFolderAsync("x", default); });
        Assert.Throws<NotSupportedException>(() => { _ = local.RenameFolderAsync("g", "x"); });
        Assert.Throws<NotSupportedException>(() => { _ = local.DeleteFolderAsync("g"); });
    }
}
