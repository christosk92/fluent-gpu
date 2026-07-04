using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Xunit;
using Col = Wavee.Protocol.Collection;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// A live LibrarySync over a faked HTTP exchange + a StubTransport (dealer pushes + the mutation transport). The loop is a
// background consumer; tests await completion via a command's Done TCS (InitialHydrate/OpenPlaylist) or WaitForIdleAsync.
sealed class SyncHarness : IAsyncDisposable
{
    public readonly InMemoryStore Store = new();
    public readonly StubTransport Dealer = new();          // dealer MESSAGE pushes + the mutation transport
    public readonly MutationEngine Mut;
    public readonly CollectionEchoRing Echo = new();       // §7.1 — shared between the write strategy and the sync loop
    public readonly Dictionary<string, string?> Revs = new();
    public readonly List<string> Hydrated = new();
    public int PlaylistGets, RootlistGets, CollectionPosts;
    public readonly LibrarySync Sync;
    readonly CancellationTokenSource _cts = new();

    public static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);

    public SyncHarness(Func<HttpReq, HttpResp> responder, Func<string, string, bool>? hasPending = null)
    {
        var http = new FakeExchange((req, _) =>
        {
            if (req.Url.Contains("/rootlist")) RootlistGets++;
            else if (req.Url.Contains("/playlist/v2/")) PlaylistGets++;
            else if (req.Url.Contains("/collection/v2/")) CollectionPosts++;
            return responder(req);
        });
        Task Hydrate(IReadOnlyList<string> uris, CancellationToken c) { lock (Hydrated) Hydrated.AddRange(uris); return Task.CompletedTask; }
        var pf = new PlaylistFetcher(http, () => "https://x", Store, Hydrate);
        var cf = new CollectionFetcher(http, () => "https://x", () => "bob", Store,
            s => Revs.TryGetValue(s, out var r) ? r : null, (s, r) => Revs[s] = r, Hydrate, hasPending);
        Mut = new MutationEngine(Store, new IMutationStrategy[] { new SetReplayStrategy(Echo), new OpRebaseStrategy(Store), new RootlistFollowStrategy(Store) });
        Sync = new LibrarySync(Store, pf, cf, Mut, Dealer,
            () => new SessionContext("bob", "US", "premium", "en", Tier.Premium, false), () => "bob", _ => { }, _cts.Token, Echo);
    }

    public async ValueTask DisposeAsync() { await Sync.DisposeAsync(); _cts.Cancel(); _cts.Dispose(); }
}

sealed class ChangeCollector : IObserver<StoreChange>
{
    public readonly List<StoreChange> All = new();
    public void OnNext(StoreChange v) { lock (All) All.Add(v); }
    public void OnCompleted() { }
    public void OnError(Exception e) { }
}

sealed class FailTransport : ITransport
{
    public int Calls;
    public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null)
    { Calls++; return Task.FromResult(new Resp(false, Array.Empty<byte>(), 500)); }
    public IObservable<WireEvent> Events(string topicPrefix) => throw new NotImplementedException();
    public IObservable<WireRequest> Requests(string identPrefix) => throw new NotImplementedException();
    public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
    public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default) => throw new NotImplementedException();
}

public class LibrarySyncTests
{
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);

    // Route the shared exchange by URL: rootlist GET, playlist GET, collection POST (set-appropriate items by wire set).
    static HttpResp HydrateResponder(HttpReq req)
    {
        if (req.Url.Contains("/rootlist"))
        {
            var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(9) };
            var c = new Pl.ListItems { Pos = 0, Truncated = false };
            c.Items.Add(new Pl.Item { Uri = "spotify:playlist:p1" });
            c.Items.Add(new Pl.Item { Uri = "spotify:playlist:p2" });
            slc.Contents = c;
            return Ok(slc.ToByteArray());
        }
        if (req.Url.Contains("/collection/v2/paging"))
        {
            var set = Col.PageRequest.Parser.ParseFrom(req.Body).Set;
            var p = new Col.PageResponse { SyncToken = "tok-" + set, NextPageToken = "" };
            switch (set)
            {
                case "collection": p.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t1", AddedAt = 1 }); p.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a1", AddedAt = 2 }); break;
                case "artist": p.Items.Add(new Col.CollectionItem { Uri = "spotify:artist:ar1", AddedAt = 1 }); break;
                case "show": p.Items.Add(new Col.CollectionItem { Uri = "spotify:show:s1", AddedAt = 1 }); break;
                case "listenlater": p.Items.Add(new Col.CollectionItem { Uri = "spotify:episode:e1", AddedAt = 1 }); break;
            }
            return Ok(p.ToByteArray());
        }
        return Ok(Array.Empty<byte>());
    }

    [Fact]
    public async Task InitialHydrate_PopulatesRootlistSetsTokensAndFold_CoalescedIntoBulkSignals()
    {
        await using var h = new SyncHarness(HydrateResponder);
        var col = new ChangeCollector();
        using var sub = h.Store.Changes.Subscribe(col);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: done));
        await done.Task;

        // rootlist + set members landed
        Assert.Equal(2, h.Store.Rootlist().Count);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
        Assert.True(h.Store.IsSaved("artists", "spotify:artist:ar1"));
        Assert.True(h.Store.IsSaved("shows", "spotify:show:s1"));
        Assert.True(h.Store.IsSaved("episodes", "spotify:episode:e1"));
        // tokens advanced (per set)
        Assert.Equal("tok-collection", h.Revs["liked"]);
        Assert.Equal("tok-collection", h.Revs["albums"]);
        Assert.Equal("tok-artist", h.Revs["artists"]);
        Assert.Equal("tok-show", h.Revs["shows"]);
        Assert.Equal("tok-listenlater", h.Revs["episodes"]);
        // the "playlists" saved-set fold
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p1"));
        Assert.True(h.Store.IsSaved("playlists", "spotify:playlist:p2"));
        // one Bulk-coalesced signal per burst — no per-uri change leaked (rootlist+fold = 1, then 5 sets = 5).
        List<StoreChange> snap; lock (col.All) snap = new List<StoreChange>(col.All);
        Assert.All(snap, c => Assert.True(c.IsBulk));
        Assert.Equal(6, snap.Count);
    }

    [Fact]
    public async Task CollectionPush_TwoRapidPushesForSameWireSet_FoldToOneSettledFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // Two pushes for the same WIRE set inside the settle window: the second must fold into the first (dropped), NOT re-arm.
        // Done rides the first push and completes when the single settled fetch finishes — a deterministic barrier, not a sleep.
        // The "collection" wire set carries BOTH liked and albums, so the settled fetch fans out to two logical delta-fetches.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Done: done));
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection"));   // within the window → folded
        Assert.True(h.Sync.IsSetSyncing("collection"));                           // syncing from the first push

        await done.Task;

        Assert.Equal(2, h.Sync.SetFetches);              // one settled push → both logical sets (liked + albums) fetched once
        Assert.Equal(2, h.CollectionPosts);              // two HTTP hits — the second push did not re-arm the settle
        Assert.False(h.Sync.IsSetSyncing("collection")); // cleared once the fetch completed
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));
    }

    [Fact]
    public async Task CollectionPushSettle_DoesNotBlockAFollowingPlaylistPush()
    {
        await using var h = new SyncHarness(HydrateResponder);
        var uri = "spotify:playlist:pr";
        var rev0 = new byte[] { 1 };
        var rev1 = new byte[] { 2 };
        h.Store.SetMembership(uri, new[] { new Wavee.Backend.Playlists.PlaylistMember("id1", "spotify:track:a", null, 0) }, rev0);
        var ops = new[]
        {
            new Wavee.Backend.Playlists.PlaylistOp(Wavee.Backend.Playlists.PlaylistOpKind.Add, AddLast: true,
                Items: new[] { new Wavee.Backend.Playlists.PlaylistMember("id2", "spotify:track:b", null, 0) }),
        };

        // A collection push arms its 250ms settle OFF the consumer; a PlaylistPush enqueued immediately after must apply
        // right away — it is not queued behind the settle. WaitForIdleAsync drains the consumer (playlist push + the idle
        // sentinel) in microseconds, well under the settle, so this is deterministic (no real-time sleep).
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection"));
        h.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, uri, ParentRev: rev0, NewRev: rev1, Ops: ops));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PushApplied);                  // the playlist push applied in place — not blocked
        Assert.Equal(2, h.Store.Membership(uri).Count);       // membership grew (track b added)
        Assert.Equal(0, h.CollectionPosts);                   // the collection settle is still pending — it never stalled the loop
        Assert.True(h.Sync.IsSetSyncing("collection"));       // wire set is still settling off-thread
    }

    [Fact]
    public async Task OpenPlaylistAsync_ConcurrentOpens_DedupToOneFetch()
    {
        int gets = 0;
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(3) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:x" });
        slc.Contents = contents;
        await using var h = new SyncHarness(req =>
        {
            if (req.Url.Contains("/playlist/v2/")) gets++;
            return Ok(slc.ToByteArray());
        });

        var t1 = h.Sync.OpenPlaylistAsync("spotify:playlist:p", CancellationToken.None);
        var t2 = h.Sync.OpenPlaylistAsync("spotify:playlist:p", CancellationToken.None);
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, gets);                                              // one fetch, both awaiters
        Assert.Single(h.Store.Membership("spotify:playlist:p"));
    }

    [Fact]
    public async Task MarkAndSweep_FullPaging_RemovesAbsent_KeepsShielded()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:gone", true, SyncState.Confirmed);          // absent from the snapshot → swept
        store.SetSaved("albums", "spotify:album:pending", true, SyncState.Pending);         // absent + shielded → survives

        var page = new Col.PageResponse { SyncToken = "t2", NextPageToken = "" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, _) => Ok(page.ToByteArray()));
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask,
            (s, u) => u == "spotify:album:pending");

        await fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken);

        Assert.True(store.IsSaved("albums", "spotify:album:a"));            // snapshot member
        Assert.False(store.IsSaved("albums", "spotify:album:gone"));        // swept
        Assert.True(store.IsSaved("albums", "spotify:album:pending"));      // shielded survives
        Assert.Equal("t2", revs["albums"]);
    }

    [Fact]
    public async Task MarkAndSweep_MidPagingThrow_LeavesPartial_NoSweep_TokenNotAdvanced()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:gone", true, SyncState.Confirmed);

        var page1 = new Col.PageResponse { SyncToken = "t1", NextPageToken = "p2" };         // a second page follows
        page1.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 1 });
        var revs = new Dictionary<string, string?>();
        var http = new FakeExchange((req, n) => n == 1 ? Ok(page1.ToByteArray()) : new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        var fetcher = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, (u, c) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.FetchSetAsync("albums", TestContext.Current.CancellationToken));

        Assert.True(store.IsSaved("albums", "spotify:album:a"));            // partial page applied
        Assert.True(store.IsSaved("albums", "spotify:album:gone"));         // NOT swept (partial loop)
        Assert.False(revs.ContainsKey("albums"));                          // token NOT advanced → next attempt re-pages fully
    }

    [Fact]
    public async Task DrainBackoff_SkipsNotDueOps_ThenAttempts_CapsAtTenDeadLetters()
    {
        var store = new InMemoryStore();
        var clock = DateTime.UtcNow;
        var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy() }, null, () => clock);
        eng.Save("liked", "spotify:track:a", true);
        var t = new FailTransport();
        var ctx = SessionContext.LoggedOut;

        await eng.Drain(t, ctx);                       // attempt 1 fails → nextAttemptAt = now + 1s
        Assert.Equal(1, t.Calls);
        Assert.Equal(1, eng.Pending);

        await eng.Drain(t, ctx);                       // not due → skipped (no new replay)
        Assert.Equal(1, t.Calls);
        Assert.Equal(1, eng.Pending);

        clock = clock.AddSeconds(1.5);                 // advance past the backoff
        await eng.Drain(t, ctx);                       // attempt 2
        Assert.Equal(2, t.Calls);

        for (int i = 0; i < 15 && eng.Pending > 0; i++) { clock = clock.AddSeconds(120); await eng.Drain(t, ctx); }
        Assert.Equal(0, eng.Pending);                  // 10 attempts exhausted → dead-lettered
        Assert.Single(eng.DeadLetter);
    }

    [Fact]
    public async Task SwitchableTransport_SetInner_RoutesRequestToNewInner()
    {
        var a = new StubTransport();
        var b = new StubTransport();
        var sw = new SwitchableTransport(a);

        await sw.Request(Channel.Spclient, "/x", default);
        Assert.Equal(1, a.RequestCount);
        Assert.Equal(0, b.RequestCount);

        sw.SetInner(b);
        await sw.Request(Channel.Spclient, "/y", default);
        Assert.Equal(1, a.RequestCount);               // old inner untouched
        Assert.Equal(1, b.RequestCount);
        Assert.Equal("/y", b.LastRequestRoute);
    }

    // ── §2.2 E — PubSubUpdate direct-apply + echo suppression + wire→logical translation ──
    static WireEvent ColPush(string wireSet, Col.PubSubUpdate upd) =>
        new("hm://collection/" + wireSet + "/bob", upd.ToByteArray());

    [Fact]
    public async Task CollectionPush_EchoOfOurAcceptedWrite_IsDropped_StoreUntouched()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // A like that goes out and is accepted records its client_update_id in the shared echo ring.
        h.Mut.Save("liked", "spotify:track:z", true);
        await h.Mut.Drain(h.Dealer, new SessionContext("bob", "US", "premium", "en", Tier.Premium, false),
            TestContext.Current.CancellationToken);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:z"));                        // optimistic → Confirmed on ack
        var cuid = Col.WriteRequest.Parser.ParseFrom(h.Dealer.LastRequestBody).ClientUpdateId;
        Assert.NotEmpty(cuid);

        // The dealer echoes our own write back (same cuid) as a removal — it MUST be dropped before any store work.
        var echo = new Col.PubSubUpdate { Set = "collection", ClientUpdateId = cuid };
        echo.Items.Add(new Col.CollectionItem { Uri = "spotify:track:z", IsRemoved = true, AddedAt = 1 });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: echo.ToByteArray(), Done: done));
        await done.Task;

        Assert.Equal(1, h.Sync.EchoDropped);
        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:z"));   // the echoed removal was dropped → still saved
        Assert.Equal(0, h.CollectionPosts);                         // zero fetch
    }

    [Fact]
    public async Task CollectionPush_ForeignUpdateWithItems_AppliesDirectly_ShieldsPending_NoFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);
        // A pending local intent shields (liked, t:pending) — a foreign push trying to REMOVE it must be skipped.
        h.Mut.Save("liked", "spotify:track:pending", true);

        var upd = new Col.PubSubUpdate { Set = "collection" };   // foreign: no client_update_id
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:track:t9", IsRemoved = false, AddedAt = 5 });
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a9", IsRemoved = false, AddedAt = 6 });
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:track:pending", IsRemoved = true, AddedAt = 7 });

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Payload: upd.ToByteArray(), Done: done));
        await done.Task;

        Assert.Equal(1, h.Sync.PushDirectApplied);
        Assert.Contains("spotify:track:t9", h.Store.SavedUris("liked"));    // track → liked
        Assert.Contains("spotify:album:a9", h.Store.SavedUris("albums"));   // album → albums
        Assert.True(h.Store.IsSaved("liked", "spotify:track:pending"));     // shielded removal skipped → survives
        Assert.Equal(0, h.CollectionPosts);                                 // zero round-trip
        // HydrateUrisAsync (the spec's hydrate path) covers added track/episode uris; albums ride the next delta/on-open fetch.
        List<string> hyd; lock (h.Hydrated) hyd = new List<string>(h.Hydrated);
        Assert.Contains("spotify:track:t9", hyd);
        Assert.DoesNotContain("spotify:track:pending", hyd);               // shielded item never touched
    }

    [Fact]
    public async Task CollectionPush_UnparseablePayload_FallsBackToSettledDeltaFetch()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // Garbage payload → not a PubSubUpdate → settle + delta fetch (one wire set → its logical fetches).
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "artist", Payload: new byte[] { 0xFF, 0xFF, 0xFF }, Done: done));
        await done.Task;

        Assert.Equal(0, h.Sync.PushDirectApplied);
        Assert.Equal(1, h.Sync.SetFetches);                                 // "artist" → ["artists"], one fetch after the window
        Assert.Equal(1, h.CollectionPosts);
        Assert.True(h.Store.IsSaved("artists", "spotify:artist:ar1"));
    }

    [Fact]
    public async Task CollectionPush_WireSetTranslation_CollectionFetchesBoth_UnknownFetchesNothing()
    {
        await using var h = new SyncHarness(HydrateResponder);

        // "collection" (no payload) → delta fetch fans out to BOTH liked and albums (two routes hit).
        var done1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "collection", Done: done1));
        await done1.Task;
        Assert.Equal(2, h.Sync.SetFetches);
        Assert.Equal(2, h.CollectionPosts);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:t1"));
        Assert.True(h.Store.IsSaved("albums", "spotify:album:a1"));

        // "ylpin" (an unknown wire set) → ignored, zero fetch.
        var done2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sync.Enqueue(new SyncCommand(SyncKind.CollectionPush, "ylpin", Done: done2));
        await done2.Task;
        Assert.Equal(2, h.Sync.SetFetches);      // unchanged — no fetch for the unknown set
        Assert.Equal(2, h.CollectionPosts);
    }

    [Fact]
    public async Task CollectionPush_ForeignUpdate_ThroughRealDealerRouter_AppliesDirectly()
    {
        await using var h = new SyncHarness(HydrateResponder);
        using var router = new Wavee.Backend.Realtime.DealerRouter(h.Dealer, h.Sync);

        // Full path: a dealer collection MESSAGE carrying a PubSubUpdate → router extracts the wire set from the topic →
        // LibrarySync direct-applies. Verifies the topic-derived wire set ("collection") maps items to the right logical sets.
        var upd = new Col.PubSubUpdate { Set = "collection" };
        upd.Items.Add(new Col.CollectionItem { Uri = "spotify:track:router", IsRemoved = false, AddedAt = 1 });
        h.Dealer.PushEvent(ColPush("collection", upd));
        await h.Sync.WaitForIdleAsync();

        Assert.Equal(1, h.Sync.PushDirectApplied);
        Assert.True(h.Store.IsSaved("liked", "spotify:track:router"));
        Assert.Equal(0, h.CollectionPosts);
    }

    [Fact]
    public void RootlistRevision_RoundTrips_ThroughSqliteMeta()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var rev = new byte[] { 1, 2, 3, 0xAB };
            using (var s = new Wavee.Backend.Persistence.SqliteColdStore(path))
            {
                Assert.Null(s.GetRootlistRevision());  // unset → null
                s.SetRootlistRevision(rev);
            }
            using (var s2 = new Wavee.Backend.Persistence.SqliteColdStore(path))
            {
                Assert.Equal(rev, s2.GetRootlistRevision());   // durable across instances
                s2.SetRootlistRevision(null);                  // null clears
                Assert.Null(s2.GetRootlistRevision());
            }
        }
        finally { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) { try { System.IO.File.Delete(f); } catch { } } }
    }
}
