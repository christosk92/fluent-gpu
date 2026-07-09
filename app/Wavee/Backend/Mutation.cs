using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend;

// ── ENGINE ② — Mutation (durable intents + per-type sync strategy) ───────────────────────────────────────────────────
// Every write is a durable intent: optimistic apply → outbox → reconnect drain → reconcile, with terminal failure →
// rollback + dead-letter. Each resource TYPE supplies a strategy; SetReplay (library saves/follows) is the representative
// one wired here (the plan also has OpRebase for playlists and OnlineOnly for pins/cover-upload). The outbox is in-memory
// here (the durable table is store-backed in §4.1); the coalescing + reconcile shape is the real one.

// Carries either a boolean end-state (SetReplay) OR an ordered op list + base revision (OpRebase). Ops/BaseRev are
// additive/nullable so the boolean save path is unchanged.
public sealed record OutboxOp(long Id, string Type, string EntityKey, string SetId, bool TargetSaved, long LogicalTs, int Attempts,
    IReadOnlyList<PlaylistOp>? Ops = null, byte[]? BaseRev = null);

public interface IMutationStrategy
{
    string Type { get; }
    bool OfflineQueueable { get; }
    void ApplyOptimistic(OutboxOp op, IStore store);
    Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct);
    void Rollback(OutboxOp op, IStore store);
}

/// <summary>Durable backing for the outbox: pending intents persist here so a restart can replay them (offline-first).
/// SQLite implements it; a null engine outbox keeps the in-memory-only behaviour.</summary>
public interface IMutationOutbox
{
    IReadOnlyList<OutboxOp> Load();
    void Save(OutboxOp op);                 // insert-or-replace by Id (also used to persist an attempts bump)
    void Remove(long id);
    void DeadLetter(OutboxOp op, string reason);
}

/// <summary>SetReplay: idempotent end-state writes (saved tracks/albums/artists, follows). Local-intent-wins: replay the
/// desired state; a server no-op when already in that state. Rollback reverts on terminal failure.</summary>
public sealed class SetReplayStrategy : IMutationStrategy
{
    const string VendorType = "application/vnd.collection-v2.spotify.proto";

    // The echo ring (§7.1) records the client_update_id of each ACCEPTED write so LibrarySync can drop our own PubSubUpdate
    // echo. Nullable: tests/scaffold that don't exercise echo suppression pass none; production always wires it.
    readonly CollectionEchoRing? _echoRing;

    public SetReplayStrategy(CollectionEchoRing? echoRing = null) => _echoRing = echoRing;

    public string Type => "set";
    public bool OfflineQueueable => true;

    public void ApplyOptimistic(OutboxOp op, IStore store)
        // added_at = now for a save (the local like time — the server echo refines it); an unsave removes the row.
        => store.SetSaved(op.SetId, op.EntityKey, op.TargetSaved, SyncState.Pending,
                          op.TargetSaved ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0);

    // §2.4 (fixes RC4): the real collection write — POST /collection/v2/write with the vendor media type (the gateway 400s
    // on the generic type at the media-type layer) and an EXPLICIT method (never the RC4 bodyless-GET inference). The body is
    // a single-item WriteRequest carrying the desired end-state (added_at in UNIX SECONDS); on accept the cuid is recorded.
    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var cuid = Guid.NewGuid().ToString("N");
        var body = CollectionWriteMapper.BuildWrite(ctx.Account, op.SetId, op.EntityKey, op.TargetSaved,
                                                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cuid);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = VendorType,
            ["Accept"] = VendorType,
        };
        var r = await t.Request(Channel.Spclient, "/collection/v2/write", body, ct, method: "POST", headers: headers).ConfigureAwait(false);
        if (r.Ok) _echoRing?.Record(cuid);   // register the echo id (§7.1)
        return r.Ok;
    }

    public void Rollback(OutboxOp op, IStore store)
        => store.SetSaved(op.SetId, op.EntityKey, !op.TargetSaved, SyncState.Confirmed);
}

/// <summary>OpRebase: ordered playlist edits (add/remove/reorder). Optimistic-applies the ops to the local membership;
/// replays as a POST of the ListChanges body to /playlist/v2/{path}/changes against the captured base revision. The
/// pre-edit membership snapshot (for rollback on terminal failure) is engine-managed.</summary>
public sealed class OpRebaseStrategy : IMutationStrategy
{
    readonly IStore _store;
    readonly Func<string> _spclientBaseUrl;
    public OpRebaseStrategy(IStore store, Func<string> spclientBaseUrl) => (_store, _spclientBaseUrl) = (store, spclientBaseUrl);

    public string Type => "oprebase";
    public bool OfflineQueueable => true;

    public void ApplyOptimistic(OutboxOp op, IStore store)
    {
        var ops = op.Ops ?? Array.Empty<PlaylistOp>();
        var list = new List<PlaylistMember>(store.Membership(op.EntityKey));
        try { PlaylistDiffApplier.Apply(list, ops); }
        catch (ArgumentOutOfRangeException) { return; }
        store.SetMembership(op.EntityKey, list, store.PlaylistRevision(op.EntityKey));
        ApplyHeaderPatch(store, op.EntityKey, ops);
    }

    internal static void ApplyHeaderPatch(IStore store, string uri, IReadOnlyList<PlaylistOp> ops)
    {
        PlaylistListAttributePatch? patch = null;
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Kind == PlaylistOpKind.UpdateList && ops[i].ListPatch is { } p) { patch = p; break; }
        if (patch is null) return;
        var header = store.GetPlaylist(uri);
        if (header is null) return;
        string? name = patch.ClearName ? "" : patch.Name ?? header.Name;
        string? desc = patch.ClearDescription ? null : patch.Description ?? header.Description;
        Image? cover = patch.ClearPicture ? null
            : patch.PictureBytes is { Length: > 0 } pic
                ? new Image("https://i.scdn.co/image/" + Convert.ToHexStringLower(pic))
                : header.Cover;
        bool collab = patch.Collaborative ?? header.Capabilities.IsCollaborative;
        var caps = header.Capabilities with { IsCollaborative = collab };
        store.UpsertPlaylist(header with { Name = name ?? header.Name, Description = desc, Cover = cover, Capabilities = caps });
    }

    // §2.7 — the /changes POST now (1) carries the first-party header set + an EXPLICIT POST method (a bare POST 200-OKs
    // against a passive read handler → a silent no-op; that latent RC-class bug in playlist edits is fixed here), and (2)
    // REBASES per attempt against the freshest stored revision (mirroring the reference), then CAPTURES the 200 response as
    // the fresh membership + revision (the response IS the fresh list) so echo suppression (§7.3) sees a matching revision.
    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var path = op.EntityKey.StartsWith("spotify:", StringComparison.Ordinal) ? op.EntityKey.Substring(8).Replace(':', '/') : op.EntityKey;
        var baseRev = _store.PlaylistRevision(op.EntityKey) ?? op.BaseRev;   // rebase per attempt: freshest cached rev wins
        var body = PlaylistWireMapper.BuildChanges(baseRev, op.Ops ?? Array.Empty<PlaylistOp>(), ctx.Account, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var headers = SpotifyHeaders.PlaylistV2Mutation(_spclientBaseUrl());
        var r = await t.Request(Channel.Spclient, $"/playlist/v2/{path}/changes", body, ct, method: "POST", headers: headers).ConfigureAwait(false);
        if (r.Ok) { CaptureChangesResponse(op.EntityKey, r.Body); return true; }
        return false;   // a 409 (revision conflict) surfaces as !Ok → retry rebased against the fresher cached revision next drain
    }

    // Fold the /changes SelectedListContent response back into the store: full contents → replace membership + revision;
    // rev-only (no contents) → advance just the revision, keeping current rows. Zstd-guarded (§2.7).
    void CaptureChangesResponse(string uri, byte[] body)
    {
        var bytes = SpotifyZstd.MaybeDecompressZstd(body);
        if (bytes.Length == 0) return;
        Pl.SelectedListContent slc;
        try { slc = Pl.SelectedListContent.Parser.ParseFrom(bytes); }
        catch { return; }
        var rev = PlaylistWireMapper.ResultingRevision(slc);
        if (slc.Contents is { } contents && contents.Items.Count > 0)
        {
            var (members, _) = PlaylistWireMapper.ParseContents(slc);
            _store.SetMembership(uri, members, rev ?? _store.PlaylistRevision(uri));
        }
        else if (rev is not null)
            _store.SetMembership(uri, _store.Membership(uri), rev);   // rev-only: keep current rows, advance the revision
    }

    public void Rollback(OutboxOp op, IStore store) { /* membership restore is engine-managed via the pre-edit snapshot */ }
}

/// <summary>RootlistFollow (§2.5, fixes RC3): following/unfollowing a playlist is a rootlist ADD/REM, not a collection
/// write. Optimistic-flips the "playlists" saved pill + edits the rootlist entry list inline; replays as a POST of the
/// rootlist ListChanges body to /playlist/v2/user/{username}/rootlist/changes against the stored rootlist revision
/// (bootstrapped once via a GET if absent). The 200 response IS the fresh rootlist → captured; a 409 refetches the base
/// so the next drain rebases.</summary>
public sealed class RootlistFollowStrategy : IMutationStrategy
{
    readonly IStore _store;
    public RootlistFollowStrategy(IStore store) => _store = store;

    public string Type => "rootlist";
    public bool OfflineQueueable => true;

    // (1) flip the pill (Pending — the Saved union folds it this frame, §2.8) and (2) edit the rootlist entry inline so the
    // sidebar reflects it immediately (follow → insert at position 0; unfollow → drop the matching row). Rev-preserving.
    public void ApplyOptimistic(OutboxOp op, IStore store)
    {
        var uri = op.EntityKey;
        bool follow = op.TargetSaved;
        store.SetSaved("playlists", uri, follow, SyncState.Pending,
                       follow ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0);
        var next = follow ? InsertFollow(store.Rootlist(), uri) : RemoveFollow(store.Rootlist(), uri);
        if (next is not null) store.SetRootlist(next);   // 1-arg overload preserves the stored revision (§2.6)
    }

    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var uri = op.EntityKey;
        bool follow = op.TargetSaved;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // (1) base revision — one-time bootstrap of the rootlist if we don't have it.
        var rev = _store.RootlistRevision();
        if (rev is null) rev = await RootlistOps.BootstrapRootlistAsync(_store, t, ctx, ct).ConfigureAwait(false);

        // (2) the op: follow → ADD at index 0 with item attributes (timestamp ms + public); unfollow → keyed REM
        // (items_as_key: remove by uri, order-independent). NEVER index-based and never skipped-on-local-absence — the
        // optimistic edit already removed the local row before this replay runs, so local absence proves nothing about
        // the server; the server treats removing an absent uri as a no-op success.
        var plop = follow
            ? new PlaylistOp(PlaylistOpKind.Add, FromIndex: 0, Items: new[] { new PlaylistMember("", uri, null, nowMs) })
            : new PlaylistOp(PlaylistOpKind.Remove, Items: new[] { new PlaylistMember("", uri, null, 0) }, ItemsAsKey: true);

        // (3–5) POST rootlist changes — shared with visibility/delete (409 → rebase, return false for retry).
        if (await RootlistOps.TryPostRootlistOpsAsync(_store, t, ctx, new[] { plop }, uri, ct).ConfigureAwait(false))
            return true;
        return false;
    }

    // undo the optimistic entry edit + flip the pill back (a subsequent authoritative refetch reconciles ordering).
    public void Rollback(OutboxOp op, IStore store)
    {
        var uri = op.EntityKey;
        bool follow = op.TargetSaved;
        store.SetSaved("playlists", uri, !follow, SyncState.Confirmed);
        var next = follow ? RemoveFollow(store.Rootlist(), uri) : InsertFollow(store.Rootlist(), uri);
        if (next is not null) store.SetRootlist(next);
    }

    // insert a followed playlist at position 0 (skip if already present); returns null on no-op.
    static IReadOnlyList<RootlistEntry>? InsertFollow(IReadOnlyList<RootlistEntry> cur, string uri)
    {
        for (int i = 0; i < cur.Count; i++) if (cur[i].Kind == 0 && cur[i].Uri == uri) return null;   // already followed
        var list = new List<RootlistEntry>(cur.Count + 1) { new RootlistEntry(0, 0, uri, null, 0) };
        for (int i = 0; i < cur.Count; i++) list.Add(cur[i]);
        return Renumber(list);
    }

    // remove the first matching kind-0 row; returns null when absent (no-op).
    static IReadOnlyList<RootlistEntry>? RemoveFollow(IReadOnlyList<RootlistEntry> cur, string uri)
    {
        int found = -1;
        for (int i = 0; i < cur.Count; i++) if (cur[i].Kind == 0 && cur[i].Uri == uri) { found = i; break; }
        if (found < 0) return null;
        var list = new List<RootlistEntry>(cur.Count - 1);
        for (int i = 0; i < cur.Count; i++) if (i != found) list.Add(cur[i]);
        return Renumber(list);
    }

    static IReadOnlyList<RootlistEntry> Renumber(List<RootlistEntry> list)
    {
        for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Position = i };
        return list;
    }
}

public sealed record EditSnapshot(IReadOnlyList<PlaylistMember> Membership, Playlist? Header);

public sealed class MutationEngine
{
    const int MaxAttempts = 10;

    readonly IStore _store;
    readonly Dictionary<string, IMutationStrategy> _strategies;
    readonly IMutationOutbox? _durable;
    readonly object _gate = new();
    // "set" rows coalesce (one per (set, entity), latest end-state wins); "oprebase" rows append (keyed by unique id —
    // the server permits duplicate playlist items, so edits must NOT dedupe).
    readonly Dictionary<string, OutboxOp> _outbox = new();
    readonly Dictionary<long, EditSnapshot> _editSnapshots = new();   // pre-edit membership + header for OpRebase rollback
    // §8.3 — per-op replay backoff (in-memory only: after a restart, attempts reload from SQLite and the clock resets —
    // a restart is a natural retry moment). Drain skips ops whose next-attempt time hasn't come; cleared on success/dead-letter.
    readonly Dictionary<long, DateTime> _nextAttemptAt = new();
    readonly Func<DateTime> _now;
    long _seq;

    // "set" (collection saves) and "rootlist" (playlist follows) COALESCE per (set, entity) — latest end-state wins, so a
    // follow/unfollow toggle never stacks; "oprebase" (playlist edits) append (keyed by unique id — duplicate items are
    // legal). The "rootlist" shape is exactly what HasPending checks (rootlist|{setId}|{entityKey}).
    static string KeyOf(OutboxOp op) => op.Type == "set" ? $"set|{op.SetId}|{op.EntityKey}"
        : op.Type == "rootlist" ? $"rootlist|{op.SetId}|{op.EntityKey}"
        : $"{op.Type}|{op.Id}";

    public List<OutboxOp> DeadLetter { get; } = new();

    public MutationEngine(IStore store, IEnumerable<IMutationStrategy> strategies, IMutationOutbox? durable = null, Func<DateTime>? now = null)
    {
        _store = store;
        _strategies = strategies.ToDictionary(s => s.Type);
        _durable = durable;
        _now = now ?? (() => DateTime.UtcNow);
        if (_durable is not null)
            foreach (var op in _durable.Load())   // restore pending intents from disk (the optimistic store state already persisted)
                if (_strategies.ContainsKey(op.Type)) { _outbox[KeyOf(op)] = op; if (op.Id > _seq) _seq = op.Id; }
    }

    public int Pending { get { lock (_gate) return _outbox.Count; } }

    /// <summary>The pending-op shield (§7.2): true when a local intent is in flight for this (setId, entityKey). Checks
    /// BOTH the "set" key shape (collection saves) and the "rootlist" key shape (playlist follows — `rootlist|playlists|
    /// {uri}`). The rootlist strategy lands in a later phase; the key check is built now so inbound Confirmed writes can
    /// be skipped for a shielded key while its own drain reconciles it. Unused by production code yet.</summary>
    public bool HasPending(string setId, string entityKey)
    {
        lock (_gate) return _outbox.ContainsKey($"set|{setId}|{entityKey}") || _outbox.ContainsKey($"rootlist|{setId}|{entityKey}");
    }

    /// <summary>Save / unsave (idempotent). Optimistic: the store reflects it as Pending immediately; the outbox replays on drain.</summary>
    public void Save(string setId, string uri, bool saved)
    {
        if (!_strategies.TryGetValue("set", out var s)) return;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "set", uri, setId, saved, id, 0);
        OutboxOp? replaced = null;
        lock (_gate) { if (_outbox.TryGetValue(KeyOf(op), out var ex)) replaced = ex; _outbox[KeyOf(op)] = op; }   // coalesce
        if (_durable is not null) { if (replaced is not null) _durable.Remove(replaced.Id); _durable.Save(op); }
        s.ApplyOptimistic(op, _store);
    }

    /// <summary>Follow / unfollow a playlist (§2.5) — a rootlist ADD/REM, not a collection write. Sibling of <see cref="Save"/>:
    /// optimistic (the "playlists" pill flips + the rootlist entry edits immediately), coalesced per uri (latest end-state
    /// wins — follow/unfollow toggles must not stack), and durably persisted so it replays on the next login/reconnect.</summary>
    public void Follow(string playlistUri, bool follow)
    {
        if (!_strategies.TryGetValue("rootlist", out var s)) return;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "rootlist", playlistUri, "playlists", follow, id, 0);
        OutboxOp? replaced = null;
        lock (_gate) { if (_outbox.TryGetValue(KeyOf(op), out var ex)) replaced = ex; _outbox[KeyOf(op)] = op; }   // coalesce per uri
        if (_durable is not null) { if (replaced is not null) _durable.Remove(replaced.Id); _durable.Save(op); }
        s.ApplyOptimistic(op, _store);
    }

    /// <summary>Edit a playlist's ordered membership (add/remove/reorder). Each edit is a DISTINCT outbox row — appended,
    /// never coalesced. Optimistic: the membership reflects it immediately; a pre-edit snapshot is captured so a terminal
    /// replay failure rolls the membership back.</summary>
    public void Edit(string playlistUri, IReadOnlyList<PlaylistOp> ops, byte[]? baseRev = null)
    {
        if (!_strategies.TryGetValue("oprebase", out var s)) return;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "oprebase", playlistUri, playlistUri, false, id, 0, ops, baseRev);
        lock (_gate) { _outbox[KeyOf(op)] = op; _editSnapshots[id] = new EditSnapshot(_store.Membership(playlistUri), _store.GetPlaylist(playlistUri)); }
        _durable?.Save(op);
        s.ApplyOptimistic(op, _store);
    }

    /// <summary>Reconnect drain: replay each op; on success reconcile (Confirmed); on terminal failure rollback + dead-letter.</summary>
    public async Task Drain(ITransport t, SessionContext ctx, CancellationToken ct = default)
    {
        List<OutboxOp> ops;
        lock (_gate) ops = _outbox.Values.OrderBy(o => o.Id).ToList();   // monotonic id = the drain tiebreak

        foreach (var op in ops)
        {
            var s = _strategies[op.Type];
            var key = KeyOf(op);

            // §8.3 backoff: skip an op whose scheduled next-attempt time hasn't arrived — it stays Pending; the loop's
            // post-drain reschedule (§6.3.4) guarantees it's re-visited. A rage-click can't burn all 10 attempts in a burst.
            lock (_gate) { if (_nextAttemptAt.TryGetValue(op.Id, out var due) && _now() < due) continue; }

            bool ok;
            try { ok = await s.Replay(op, t, ctx, ct).ConfigureAwait(false); }
            catch { ok = false; }

            // Reconcile by IDENTITY: a Save that coalesced in during the (awaited) replay replaced this row with a newer
            // intent. We must not remove/clobber that newer op — else the user's latest action is silently lost.
            if (ok)
            {
                bool stillCurrent;
                lock (_gate)
                {
                    stillCurrent = _outbox.TryGetValue(key, out var cur) && cur.Id == op.Id;
                    if (stillCurrent) { _outbox.Remove(key); _editSnapshots.Remove(op.Id); }
                    _nextAttemptAt.Remove(op.Id);   // cleared on success
                }
                if (stillCurrent)
                {
                    _durable?.Remove(op.Id);
                    // "set" (collection saves) + "rootlist" (playlist follows) reconcile the saved pill to Confirmed;
                    // "oprebase" leaves the already-applied membership (the dealer echo / next /diff confirms).
                    if (op.Type == "set" || op.Type == "rootlist") _store.SetSaved(op.SetId, op.EntityKey, op.TargetSaved, SyncState.Confirmed);
                }
                // else: a newer Save superseded this op mid-replay → leave it Pending for the next drain.
            }
            else
            {
                var bumped = op with { Attempts = op.Attempts + 1 };
                bool deadLetter = false, bumpedDurable = false;
                IReadOnlyList<PlaylistMember>? snapshot = null;
                Playlist? headerSnapshot = null;
                lock (_gate)
                {
                    if (_outbox.TryGetValue(key, out var cur) && cur.Id == op.Id)   // only touch the row if it's still ours
                    {
                        if (bumped.Attempts >= MaxAttempts)
                        {
                            _outbox.Remove(key); DeadLetter.Add(op); deadLetter = true;
                            _nextAttemptAt.Remove(op.Id);
                            if (_editSnapshots.Remove(op.Id, out var snap)) { snapshot = snap.Membership; headerSnapshot = snap.Header; }
                        }
                        else
                        {
                            _outbox[key] = bumped; bumpedDurable = true;
                            // Exponential backoff on the next attempt: min(60s, 1s · 2^attempts).
                            _nextAttemptAt[op.Id] = _now() + TimeSpan.FromSeconds(Math.Min(60d, Math.Pow(2, op.Attempts)));
                        }
                    }
                    // else: a newer Save superseded this op → drop this stale attempt; the newer op drains next.
                }
                if (bumpedDurable) _durable?.Save(bumped);   // persist the attempts bump
                if (deadLetter)   // revert the optimistic write OUTSIDE the lock (cardinal rule)
                {
                    _durable?.Remove(op.Id);
                    _durable?.DeadLetter(op, "max replay attempts exceeded");
                    if (op.Type == "oprebase")
                    {
                        if (snapshot is not null) _store.SetMembership(op.EntityKey, snapshot, op.BaseRev);
                        if (headerSnapshot is not null) _store.UpsertPlaylist(headerSnapshot);
                    }
                    else s.Rollback(op, _store);
                }
            }
        }
    }
}
