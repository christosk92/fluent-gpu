using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Playlists;

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

/// <summary>SetReplay: idempotent end-state writes (saved tracks/albums/artists, follows). Local-intent-wins: replay the
/// desired state; a server no-op when already in that state. Rollback reverts on terminal failure.</summary>
public sealed class SetReplayStrategy : IMutationStrategy
{
    public string Type => "set";
    public bool OfflineQueueable => true;

    public void ApplyOptimistic(OutboxOp op, IStore store)
        => store.SetSaved(op.SetId, op.EntityKey, op.TargetSaved, SyncState.Pending);

    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var verb = op.TargetSaved ? "add" : "remove";
        var r = await t.Request(Channel.Spclient, $"/collection/{op.SetId}/{verb}/{op.EntityKey}", default, ct).ConfigureAwait(false);
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
    public string Type => "oprebase";
    public bool OfflineQueueable => true;

    public void ApplyOptimistic(OutboxOp op, IStore store)
    {
        var list = new List<PlaylistMember>(store.Membership(op.EntityKey));
        try { PlaylistDiffApplier.Apply(list, op.Ops ?? Array.Empty<PlaylistOp>()); }
        catch (ArgumentOutOfRangeException) { return; }   // can't apply against the local baseline → leave it; the server reconciles
        store.SetMembership(op.EntityKey, list, store.PlaylistRevision(op.EntityKey));
    }

    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var path = op.EntityKey.StartsWith("spotify:", StringComparison.Ordinal) ? op.EntityKey.Substring(8).Replace(':', '/') : op.EntityKey;
        var body = PlaylistWireMapper.BuildChanges(op.BaseRev, op.Ops ?? Array.Empty<PlaylistOp>());
        var r = await t.Request(Channel.Spclient, $"/playlist/v2/{path}/changes", body, ct).ConfigureAwait(false);
        return r.Ok;   // a 409 (revision conflict) surfaces as !Ok → retry/rebase on the next drain
    }

    public void Rollback(OutboxOp op, IStore store) { /* membership restore is engine-managed via the pre-edit snapshot */ }
}

public sealed class MutationEngine
{
    const int MaxAttempts = 10;

    readonly IStore _store;
    readonly Dictionary<string, IMutationStrategy> _strategies;
    readonly object _gate = new();
    // "set" rows coalesce (one per (set, entity), latest end-state wins); "oprebase" rows append (keyed by unique id —
    // the server permits duplicate playlist items, so edits must NOT dedupe).
    readonly Dictionary<string, OutboxOp> _outbox = new();
    readonly Dictionary<long, IReadOnlyList<PlaylistMember>> _editSnapshots = new();   // pre-edit membership, for OpRebase rollback
    long _seq;

    static string KeyOf(OutboxOp op) => op.Type == "set" ? $"set|{op.SetId}|{op.EntityKey}" : $"{op.Type}|{op.Id}";

    public List<OutboxOp> DeadLetter { get; } = new();

    public MutationEngine(IStore store, IEnumerable<IMutationStrategy> strategies)
    {
        _store = store;
        _strategies = strategies.ToDictionary(s => s.Type);
    }

    public int Pending { get { lock (_gate) return _outbox.Count; } }

    /// <summary>Save / unsave (idempotent). Optimistic: the store reflects it as Pending immediately; the outbox replays on drain.</summary>
    public void Save(string setId, string uri, bool saved)
    {
        if (!_strategies.TryGetValue("set", out var s)) return;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "set", uri, setId, saved, id, 0);
        lock (_gate) _outbox[KeyOf(op)] = op;   // coalesce to the latest end-state (per set, so two sets don't collide)
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
        lock (_gate) { _outbox[KeyOf(op)] = op; _editSnapshots[id] = _store.Membership(playlistUri); }   // snapshot BEFORE apply
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
                }
                // "set" reconciles to Confirmed; "oprebase" leaves the already-applied membership (the dealer echo / next /diff confirms).
                if (stillCurrent && op.Type == "set") _store.SetSaved(op.SetId, op.EntityKey, op.TargetSaved, SyncState.Confirmed);
                // else: a newer Save superseded this op mid-replay → leave it Pending for the next drain.
            }
            else
            {
                var bumped = op with { Attempts = op.Attempts + 1 };
                bool deadLetter = false;
                IReadOnlyList<PlaylistMember>? snapshot = null;
                lock (_gate)
                {
                    if (_outbox.TryGetValue(key, out var cur) && cur.Id == op.Id)   // only touch the row if it's still ours
                    {
                        if (bumped.Attempts >= MaxAttempts)
                        {
                            _outbox.Remove(key); DeadLetter.Add(op); deadLetter = true;
                            if (_editSnapshots.Remove(op.Id, out var snap)) snapshot = snap;
                        }
                        else _outbox[key] = bumped;
                    }
                    // else: a newer Save superseded this op → drop this stale attempt; the newer op drains next.
                }
                if (deadLetter)   // revert the optimistic write OUTSIDE the lock (cardinal rule)
                {
                    if (op.Type == "oprebase") { if (snapshot is not null) _store.SetMembership(op.EntityKey, snapshot, op.BaseRev); }
                    else s.Rollback(op, _store);
                }
            }
        }
    }
}
