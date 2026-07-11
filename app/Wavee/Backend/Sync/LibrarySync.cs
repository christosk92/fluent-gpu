using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Channels = System.Threading.Channels;   // alias: 'Channel' alone collides with Wavee.Backend.Channel (transport enum)
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Backend.Sync;

// ── The single library-sync writer loop (§0 tenet 1, §2.2) ───────────────────────────────────────────────────────────
// One serialized consumer owns every network-sourced library-state write (rootlist, memberships, collection sets, revision
// bumps) + the mutation-outbox drain. The DealerRouter/Seam/on-open path/boot only ENQUEUE typed commands; nothing else
// races a store write for the same entity. Optimistic user writes stay inline (UI-frame latency) — their replay/reconcile
// runs here via DrainWrites. Placement in Backend/ keeps it unit-testable against StubTransport.PushEvent + crafted protos.
//
// Phase-1/2/3 scope (docs/library-sync-implementation-plan.md §12): PlaylistRevalidate = full FetchPlaylistAsync (the /diff
// upgrade is Phase 5 — one call-site swap); CollectionPush carries the WIRE set + raw payload — a parseable PubSubUpdate is
// direct-applied on the loop (echo-dropped via the ring, else the items folded through the pending shield, zero round-trip),
// and only an unparseable/empty/zero-item payload falls back to the 250ms-settled delta fetch (wire → logical fan-out).
// ReconnectResync (§6.2): the ordered convergence pass on a drop→Online transition — drain first (local intent wins),
// then rootlist, then token-gated per-set deltas, then /diff for the open + dirty RESIDENT playlists only (anti-herd
// preserved: cold-dirty playlists stay lazy). Rate-limited to one pass per 30s so a flapping network can't storm.

public enum SyncKind : byte
{
    InitialHydrate, RootlistPush, PlaylistPush, CollectionPush, OpenPlaylist, PlaylistRevalidate, DrainWrites, ReconnectResync,
}

/// <summary>A queued command for the sync loop. A readonly record struct through the unbounded channel (no boxing).
/// <see cref="Done"/> completes when the command's handler finishes (OpenPlaylist awaits it; tests use it as a barrier).</summary>
public readonly record struct SyncCommand(
    SyncKind Kind,
    string Uri = "",                                  // playlist uri / set id
    byte[]? ParentRev = null,
    byte[]? NewRev = null,
    IReadOnlyList<PlaylistOp>? Ops = null,
    byte[]? Payload = null,                           // raw collection-push payload (§2.3 — passed through, unused in Phase 1)
    TaskCompletionSource? Done = null);

public sealed class LibrarySync : IAsyncDisposable
{
    static readonly string[] Sets = { "liked", "albums", "artists", "shows", "episodes" };   // same list as SpotifyLibrarySync
    const int SettleMs = 250;                                                                 // dealer-burst settle (§2.2)
    static readonly TimeSpan OpenRevalidateWindow = TimeSpan.FromMinutes(5);                  // on-open SWR window (§2.2)
    static readonly TimeSpan SetRetryDelay = TimeSpan.FromSeconds(30);                        // per-set hydrate retry (§8.2)

    readonly IStore _store;
    readonly PlaylistFetcher _playlists;
    readonly CollectionFetcher _collections;
    readonly MutationEngine _mutations;
    readonly ITransport _mutationTransport;
    readonly CollectionEchoRing? _echoRing;   // §7.1 — drop our own accepted-write echoes before any store work
    readonly Func<SessionContext> _ctx;
    readonly Func<string> _username;
    readonly WaveeLogger _log;
    readonly CancellationToken _ct;
    readonly Channels.Channel<SyncCommand> _queue = Channels.Channel.CreateUnbounded<SyncCommand>(new Channels.UnboundedChannelOptions { SingleReader = true });
    readonly Task _consumer;

    readonly object _gate = new();
    readonly HashSet<string> _dirtyPlaylists = new(StringComparer.Ordinal);            // pushed-while-cold → revalidate on open
    readonly Dictionary<string, DateTime> _lastRevalidatedAt = new(StringComparer.Ordinal);
    readonly Dictionary<string, TaskCompletionSource> _openInFlight = new(StringComparer.Ordinal);  // per-uri open dedup
    readonly HashSet<string> _pendingSets = new(StringComparer.Ordinal);              // collection-push settle coalescing
    readonly HashSet<string> _loggedUnknownSets = new(StringComparer.Ordinal);        // unknown wire sets logged at most once
    string? _openUri;                                                                 // the on-screen playlist (SetOpenContext)
    int _consecutiveDrainFailures;
    bool _drainReenqueueScheduled;
    DateTime _lastResyncAt = DateTime.MinValue;                                       // §6.2 rate limit (one pass per window)

    /// <summary>The §6.2 resync rate-limit window (default 30s). Public only so tests can collapse it; production never sets it.</summary>
    public TimeSpan ResyncWindow = TimeSpan.FromSeconds(30);

    // Counters (§11) — test + probe visibility. Interlocked-bumped.
    public int PushApplied, PushMarkedDirty, PushDirectApplied, EchoDropped, RevalidateRuns, RootlistApplied, RootlistFullFetch, HydrateRuns, SetFetches;
    public int DiffApplied, DiffUpToDate, DiffFellBack;   // §2.6 revalidation outcomes (Applied / 304-or-up-to-date / full-fetch fallback)
    public int ReconnectResyncs, ReconnectResyncsRateLimited;                         // §6.2

    public LibrarySync(IStore store, PlaylistFetcher playlists, CollectionFetcher collections, MutationEngine mutations,
        ITransport mutationTransport, Func<SessionContext> ctx, Func<string> username, WaveeLogger log, CancellationToken ct,
        CollectionEchoRing? echoRing = null)
    {
        _store = store;
        _playlists = playlists;
        _collections = collections;
        _mutations = mutations;
        _mutationTransport = mutationTransport;
        _echoRing = echoRing;
        _ctx = ctx;
        _username = username;
        _log = log;
        _ct = ct;
        _consumer = Task.Run(ConsumeAsync);
    }

    // ── public surface ──────────────────────────────────────────────────────────────────────────────────────────────
    // CollectionPush routing (§2.2): a payload that will DIRECT-APPLY or ECHO-DROP (a parseable PubSubUpdate with items, or
    // one whose client_update_id is in the echo ring) bypasses the settle entirely — it is O(items), no network, so it runs
    // immediately on the loop. Everything else (unparseable/empty/zero-item) arms the 250ms settle OUT of the consumer: a
    // settling set does NOT stall the loop, and a second push for the same wire set folds while the first is still settling.
    public void Enqueue(in SyncCommand cmd)
    {
        if (cmd.Kind == SyncKind.CollectionPush)
        {
            if (ShouldDirectApply(cmd.Payload)) _queue.Writer.TryWrite(cmd);   // immediate — no settle, applied on the loop
            else ScheduleCollectionSettle(cmd);                               // fetch path — settle + wire→logical fan-out
            return;
        }
        _queue.Writer.TryWrite(cmd);
    }

    // Fold + settle the collection burst off the consumer thread. First push for a set arms the settle (and its payload —
    // §2.3 — is the one Phase 3 parses); subsequent pushes within the window are dropped (already pending). IsSetSyncing is
    // true from this add until the follow-up handler's fetch completes and removes the set.
    void ScheduleCollectionSettle(in SyncCommand cmd)
    {
        var set = cmd.Uri;
        if (set.Length == 0) { cmd.Done?.TrySetResult(); return; }
        lock (_gate) { if (!_pendingSets.Add(set)) { cmd.Done?.TrySetResult(); return; } }   // folded into the in-flight settle
        var payload = cmd.Payload;
        var done = cmd.Done;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SettleMs, _ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { lock (_gate) _pendingSets.Remove(set); done?.TrySetResult(); return; }
            // Write the settled follow-up directly to the channel (bypassing Enqueue's interception); the handler fetches
            // immediately (no further delay) and clears _pendingSets in its finally. If the writer is closed (dispose),
            // undo the pending mark and release any barrier.
            if (!_queue.Writer.TryWrite(new SyncCommand(SyncKind.CollectionPush, set, Payload: payload, Done: done)))
            { lock (_gate) _pendingSets.Remove(set); done?.TrySetResult(); }
        });
    }

    /// <summary>The DetailPage mount effect sets the on-screen playlist so a push for it revalidates eagerly (§2.2 gate 3).</summary>
    public void SetOpenContext(string? uri) { lock (_gate) _openUri = uri; }

    /// <summary>Optional UI progress hook: is a full set fetch currently settling/running.</summary>
    public bool IsSetSyncing(string setId) { lock (_gate) return _pendingSets.Contains(setId); }

    /// <summary>On-open path (EnsureFetchedAsync): enqueue + await completion, DEDUPED per uri (a second open while one is
    /// in-flight awaits the same task). Empty membership → full fetch; else dirty/stale-gated revalidate.</summary>
    public Task OpenPlaylistAsync(string uri, CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_openInFlight.TryGetValue(uri, out var existing))
                return ct.CanBeCanceled ? existing.Task.WaitAsync(ct) : existing.Task;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _openInFlight[uri] = tcs;
        }
        Enqueue(new SyncCommand(SyncKind.OpenPlaylist, uri, Done: tcs));
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>Enqueue a mutation-outbox drain on the single-writer loop and await that command's completion. User-facing
    /// playlist actions use this barrier so they never report a queued write as a confirmed server mutation.</summary>
    public Task DrainWritesAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new SyncCommand(SyncKind.DrainWrites, Done: tcs)))
            return Task.FromException(new InvalidOperationException("The library sync loop is not available."));
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>Test/probe barrier: a no-op that completes only after all previously-queued commands are processed
    /// (the channel is FIFO single-reader). A PlaylistRevalidate with an empty uri is the idle sentinel.</summary>
    public Task WaitForIdleAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new SyncCommand(SyncKind.PlaylistRevalidate, "", Done: tcs));
        return tcs.Task;
    }

    // ── the consumer loop ───────────────────────────────────────────────────────────────────────────────────────────
    async Task ConsumeAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_ct).ConfigureAwait(false))
                while (reader.TryRead(out var cmd))
                {
                    try { await Dispatch(cmd).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { cmd.Done?.TrySetResult(); return; }
                    catch (Exception ex) { _log.Info("sync: " + cmd.Kind + " failed: " + ex.Message); }
                    finally { cmd.Done?.TrySetResult(); }
                }
        }
        catch (OperationCanceledException) { /* cancelled (logout) — fall through to complete stragglers */ }
        catch (Exception ex) { _log.Info("sync: loop crashed: " + ex.Message); }
        finally
        {
            while (reader.TryRead(out var leftover)) leftover.Done?.TrySetResult();
            lock (_gate) { foreach (var t in _openInFlight.Values) t.TrySetResult(); _openInFlight.Clear(); }
        }
    }

    Task Dispatch(SyncCommand cmd) => cmd.Kind switch
    {
        SyncKind.InitialHydrate => InitialHydrateAsync(),
        SyncKind.RootlistPush => RootlistPushAsync(cmd.ParentRev, cmd.NewRev, cmd.Ops),
        SyncKind.PlaylistPush => PlaylistPushAsync(cmd.Uri, cmd.ParentRev, cmd.NewRev, cmd.Ops),
        SyncKind.CollectionPush => CollectionPushAsync(cmd.Uri, cmd.Payload),
        SyncKind.OpenPlaylist => OpenPlaylistHandlerAsync(cmd.Uri),
        SyncKind.PlaylistRevalidate => PlaylistRevalidateAsync(cmd.Uri),
        SyncKind.DrainWrites => DrainWritesAsync(),
        SyncKind.ReconnectResync => ReconnectResyncAsync(),
        _ => Task.CompletedTask,
    };

    // ── handlers ────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task InitialHydrateAsync()
    {
        // (1) drain the outbox first — local intent wins (§6.3).
        await DrainWritesAsync().ConfigureAwait(false);

        // (2) rootlist (full fetch — the /diff upgrade is a later phase) + the "playlists" saved-set fold, one bulk.
        int rootCount = 0;
        try
        {
            using (_store.BeginBulk())
            {
                await _playlists.FetchRootlistAsync(RootlistUri(), _ct).ConfigureAwait(false);
                FoldRootlistIntoSavedSet();
            }
            rootCount = _store.Rootlist().Count(e => e.Kind == 0);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("sync: rootlist hydrate failed: " + ex.Message); }

        // (3) the 5 sets sequentially, per-set failures isolated (log + record); retry the failed ones once after 30s.
        var counts = new List<string>(Sets.Length);
        var failed = new List<string>();
        foreach (var set in Sets)
        {
            _ct.ThrowIfCancellationRequested();
            try { await FetchSetAsync(set).ConfigureAwait(false); counts.Add(set + "=" + _store.SavedUris(set).Count); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failed.Add(set); _log.Info("sync: set '" + set + "' hydrate failed: " + ex.Message); }
        }
        if (failed.Count > 0) ScheduleSetRetry(failed);

        // (4) summary.
        _log.Info($"sync: initial hydrate — {rootCount} rootlist playlists; " + string.Join(", ", counts)
            + (failed.Count > 0 ? " (failed: " + string.Join(",", failed) + ", retry in 30s)" : ""));
    }

    async Task RootlistPushAsync(byte[]? parentRev, byte[]? newRev, IReadOnlyList<PlaylistOp>? ops)
    {
        var stored = _store.RootlistRevision();
        if (stored is not null && parentRev is not null && ops is not null && BytesEqual(stored, parentRev))
        {
            var members = new List<PlaylistMember>();
            foreach (var e in _store.Rootlist()) members.Add(new PlaylistMember("", e.Uri, null, 0));
            bool torn = false;
            try { PlaylistDiffApplier.Apply(members, ops); }
            catch (ArgumentOutOfRangeException) { torn = true; }   // torn apply → full fetch
            if (!torn)
            {
                using (_store.BeginBulk())
                {
                    _store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(members.Select(m => m.ItemUri)), newRev);
                    FoldRootlistIntoSavedSet();
                }
                Interlocked.Increment(ref RootlistApplied);
                return;
            }
        }

        // full fetch fallback (rootlists are small; a full GET always converges).
        using (_store.BeginBulk())
        {
            await _playlists.FetchRootlistAsync(RootlistUri(), _ct).ConfigureAwait(false);
            FoldRootlistIntoSavedSet();
        }
        Interlocked.Increment(ref RootlistFullFetch);
    }

    async Task PlaylistPushAsync(string uri, byte[]? parentRev, byte[]? newRev, IReadOnlyList<PlaylistOp>? ops)
    {
        if (uri.Length == 0) return;
        var stored = _store.PlaylistRevision(uri);

        // gate 1 — echo of our own write (we advanced the revision from the /changes response): stored == newRev → drop.
        if (stored is not null && newRev is not null && BytesEqual(stored, newRev)) { Interlocked.Increment(ref EchoDropped); return; }

        var membership = _store.Membership(uri);

        // gate 2 — resident + parent-rev match → apply ops in place (zero network), hydrate ONLY the added uris.
        if (membership.Count > 0 && stored is not null && parentRev is not null && ops is not null && BytesEqual(stored, parentRev))
        {
            var list = new List<PlaylistMember>(membership);
            var before = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < membership.Count; i++) before.Add(membership[i].ItemUri);
            bool torn = false;
            try { PlaylistDiffApplier.Apply(list, ops); }
            catch (ArgumentOutOfRangeException) { torn = true; }
            if (!torn)
            {
                _store.SetMembership(uri, list, newRev ?? stored);
                var added = new List<string>();
                for (int i = 0; i < list.Count; i++) { var u = list[i].ItemUri; if (!before.Contains(u)) added.Add(u); }
                if (added.Count > 0)
                {
                    try { await _playlists.HydrateUrisAsync(added, _ct).ConfigureAwait(false); Interlocked.Increment(ref HydrateRuns); _store.Bump(uri); }
                    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _log.Info("sync: hydrate added uris failed: " + ex.Message); }
                }
                if (ContainsUpdateList(ops))
                {
                    try { await _playlists.FetchPlaylistHeaderAsync(uri, _ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _log.Info("sync: playlist header refresh failed: " + ex.Message); }
                }
                ClearDirty(uri);
                Interlocked.Increment(ref PushApplied);
                return;
            }
        }

        // gate 3 — open playlist revalidates eagerly; everything else marks dirty (anti-herd).
        if (IsOpen(uri)) await PlaylistRevalidateAsync(uri).ConfigureAwait(false);
        else { MarkDirty(uri); Interlocked.Increment(ref PushMarkedDirty); }
    }

    // CollectionPush handler. `wireSet` is the WIRE set as it comes off the dealer topic ("collection"/"artist"/…). A
    // parseable PubSubUpdate is handled with zero round-trip: an echo (cuid in the ring) is dropped, else the items are
    // folded straight into the store through the pending shield (§2.2 E). Only an unparseable/empty/zero-item payload falls
    // back to the delta fetch — translating the wire set to its logical set(s) and delta-fetching each (§2.2). A direct-apply
    // command bypassed the settle so it was never in _pendingSets; a fetch command was, and its finally clears it.
    async Task CollectionPushAsync(string wireSet, byte[]? payload)
    {
        // Only the SETTLE follow-up owns the _pendingSets mark (a direct-apply command bypassed the settle and never added
        // it — clearing it here would prematurely free a concurrent settle window). Enqueue routed non-direct payloads here.
        bool fromSettle = !ShouldDirectApply(payload);
        try
        {
            if (wireSet.Length == 0) return;

            if (TryParsePush(payload, out var upd))
            {
                var cuid = upd.ClientUpdateId;
                if (cuid.Length > 0 && (_echoRing?.Contains(cuid) ?? false)) { Interlocked.Increment(ref EchoDropped); return; }
                if (upd.Items.Count > 0) { await DirectApplyPushAsync(wireSet, upd).ConfigureAwait(false); return; }
                // parsed but zero items → unknown change shape → fall through to the delta fetch.
            }

            var logical = CollectionSets.LogicalSetsForWireSet(wireSet);
            if (logical.Count == 0) { LogUnknownWireSetOnce(wireSet); return; }
            foreach (var set in logical) await FetchSetAsync(set).ConfigureAwait(false);
        }
        finally { if (fromSettle) lock (_gate) _pendingSets.Remove(wireSet); }
    }

    // §2.2 E — apply the pushed items directly (zero collection round-trip). Each item is attributed to a LOGICAL set via
    // its URI prefix within the wire set, shielded (§7.2) items are skipped, and the rest fold under ONE bulk. Added spotify
    // uris are hydrated (metadata) as on the playlist-push path. The sync token is deliberately NOT advanced — the next
    // delta re-delivers these items idempotently (the Phase-0 no-op elision makes that silent).
    async Task DirectApplyPushAsync(string wireSet, Col.PubSubUpdate upd)
    {
        var added = new List<string>();
        var firstAddedBySet = new Dictionary<string, string>(StringComparer.Ordinal);
        using (_store.BeginBulk())
        {
            foreach (var it in upd.Items)
            {
                var logical = CollectionSets.LogicalSetForItem(wireSet, it.Uri);
                if (logical is null) continue;                          // not attributable to a known logical set
                if (_mutations.HasPending(logical, it.Uri)) continue;   // §7.2 — a local intent shields this (set, uri)
                _store.SetSaved(logical, it.Uri, !it.IsRemoved, SyncState.Confirmed);
                if (!it.IsRemoved && it.Uri.StartsWith("spotify:", StringComparison.Ordinal))
                {
                    added.Add(it.Uri);
                    if (!firstAddedBySet.ContainsKey(logical)) firstAddedBySet[logical] = it.Uri;
                }
            }
        }
        if (added.Count > 0)
        {
            try
            {
                await _playlists.HydrateUrisAsync(added, _ct).ConfigureAwait(false);
                Interlocked.Increment(ref HydrateRuns);
                foreach (var kv in firstAddedBySet)
                    if (KindForLogicalSet(kv.Key) is { } kind) _store.Bump(kv.Value, kind);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: direct-apply hydrate failed: " + ex.Message); }
        }
        Interlocked.Increment(ref PushDirectApplied);
    }

    static bool ContainsUpdateList(IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Kind == PlaylistOpKind.UpdateList) return true;
        return false;
    }

    static CollectionKind? KindForLogicalSet(string setId) => setId switch
    {
        "albums" => CollectionKind.Albums,
        "artists" => CollectionKind.Artists,
        "shows" or "episodes" => CollectionKind.Shows,
        "playlists" => CollectionKind.Playlists,
        "liked" => CollectionKind.Liked,
        _ => null,
    };

    // A payload direct-applies (bypassing the settle) iff it parses to a PubSubUpdate that carries items OR is an echo of one
    // of our accepted writes (a cuid in the ring). Parsing is pure + off-loop-safe; the handler re-parses to do the work.
    bool ShouldDirectApply(byte[]? payload)
    {
        if (!TryParsePush(payload, out var upd)) return false;
        if (upd.Items.Count > 0) return true;
        return upd.ClientUpdateId.Length > 0 && (_echoRing?.Contains(upd.ClientUpdateId) ?? false);
    }

    static bool TryParsePush(byte[]? payload, out Col.PubSubUpdate update)
    {
        update = null!;
        if (payload is null || payload.Length == 0) return false;
        try { update = Col.PubSubUpdate.Parser.ParseFrom(payload); return true; }
        catch { return false; }
    }

    void LogUnknownWireSetOnce(string wireSet)
    {
        bool first; lock (_gate) first = _loggedUnknownSets.Add(wireSet);
        if (first) _log.Info("sync: ignoring collection push for unknown wire set '" + wireSet + "'");
    }

    async Task OpenPlaylistHandlerAsync(string uri)
    {
        try
        {
            if (uri.Length == 0) return;
            if (_store.Membership(uri).Count == 0)
            {
                await _playlists.FetchPlaylistAsync(uri, _ct).ConfigureAwait(false);   // first open — the skeleton path
                MarkRevalidated(uri); ClearDirty(uri);
                return;
            }
            // Heal headers stripped or capability-stale after a partial LIST_METADATA_V2 upsert (membership stayed resident).
            var header = _store.GetPlaylist(uri);
            if (header is not null && (header.Capabilities == default
                || (header.Capabilities.CanEditMetadata && !header.Capabilities.CanAdministratePermissions)))
            {
                try { await _playlists.FetchPlaylistHeaderAsync(uri, _ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                catch { }
            }
            bool dirty = IsDirty(uri);
            bool stale = !TryGetLastRevalidated(uri, out var last) || (DateTime.UtcNow - last) > OpenRevalidateWindow;
            if (dirty || stale) await PlaylistRevalidateAsync(uri).ConfigureAwait(false);
        }
        finally { lock (_gate) _openInFlight.Remove(uri); }
    }

    // Revision-gated /diff (§2.6, fixes RC5): an unchanged playlist costs one up-to-date round-trip (usually a 304); a
    // changed one applies only the server's ops; every degenerate case (no baseline, stale rev/509, torn apply, bad body)
    // falls back to a full fetch inside the fetcher — all outcomes converge and mark the playlist fresh.
    async Task PlaylistRevalidateAsync(string uri)
    {
        if (uri.Length == 0) return;   // the WaitForIdleAsync idle barrier
        var outcome = await _playlists.FetchPlaylistDiffAsync(uri, _ct).ConfigureAwait(false);
        switch (outcome)
        {
            case DiffOutcome.Applied: Interlocked.Increment(ref DiffApplied); break;
            case DiffOutcome.UpToDate: Interlocked.Increment(ref DiffUpToDate); break;
            default: Interlocked.Increment(ref DiffFellBack); break;
        }
        MarkRevalidated(uri); ClearDirty(uri);
        Interlocked.Increment(ref RevalidateRuns);
    }

    async Task DrainWritesAsync()
    {
        lock (_gate) _drainReenqueueScheduled = false;   // this run consumes any scheduled re-enqueue
        await _mutations.Drain(_mutationTransport, _ctx(), _ct).ConfigureAwait(false);
        if (_mutations.Pending > 0)
        {
            int fails;
            lock (_gate) fails = _consecutiveDrainFailures++;
            ScheduleDrainReenqueue(TimeSpan.FromSeconds(Math.Min(60d, Math.Pow(2, fails))));   // §8.3 backoff
        }
        else lock (_gate) _consecutiveDrainFailures = 0;   // a drain that empties the outbox resets the backoff
    }

    // §6.2 — the ordered convergence pass after a drop→Online transition. Everything is revision/token-gated, so an
    // eventless reconnect costs a handful of near-free probes. Order matters: drain FIRST (local intent wins — a delta
    // running first could visually revert a not-yet-sent like), then rootlist, then per-set deltas, then /diff for the
    // open playlist + the dirty RESIDENT playlists only (cold-dirty stays lazy — the anti-herd contract). Rate-limited:
    // pushes queued during the gap were dropped by the dead socket, so this pass is the only recovery; a flapping network
    // coalesces to one pass per window.
    async Task ReconnectResyncAsync()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastResyncAt < ResyncWindow) { ReconnectResyncsRateLimited++; return; }
            _lastResyncAt = now;
        }

        await DrainWritesAsync().ConfigureAwait(false);                                    // (1) local intent first

        try                                                                                // (2) rootlist + fold
        {
            using (_store.BeginBulk())
            {
                await _playlists.FetchRootlistAsync(RootlistUri(), _ct).ConfigureAwait(false);
                FoldRootlistIntoSavedSet();
            }
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("sync: reconnect rootlist failed: " + ex.Message); }

        foreach (var set in Sets)                                                          // (3) token-gated deltas
        {
            _ct.ThrowIfCancellationRequested();
            try { await FetchSetAsync(set).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: reconnect set '" + set + "' failed: " + ex.Message); }
        }

        List<string> targets;                                                              // (4) open + dirty RESIDENT
        lock (_gate)
        {
            targets = new List<string>(_dirtyPlaylists.Count + 1);
            if (_openUri is { Length: > 0 } open) targets.Add(open);
            foreach (var d in _dirtyPlaylists) if (!targets.Contains(d)) targets.Add(d);
        }
        foreach (var uri in targets)
        {
            _ct.ThrowIfCancellationRequested();
            if (_store.Membership(uri).Count == 0) continue;   // cold stays lazy (revalidates on open)
            try { await PlaylistRevalidateAsync(uri).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Info("sync: reconnect playlist '" + uri + "' failed: " + ex.Message); }
        }

        Interlocked.Increment(ref ReconnectResyncs);
        _log.Info("sync: reconnect resync complete (" + targets.Count + " playlist revalidations)");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task FetchSetAsync(string set)
    {
        await _collections.FetchSetAsync(set, _ct).ConfigureAwait(false);
        Interlocked.Increment(ref SetFetches);
    }

    void FoldRootlistIntoSavedSet()   // §2.8 — must run inside the caller's BeginBulk
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _store.Rootlist())
            if (e.Kind == 0 && e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) next.Add(e.Uri);

        foreach (var uri in next)
            if (!_store.IsSaved("playlists", uri) && !_mutations.HasPending("playlists", uri))
                _store.SetSaved("playlists", uri, true, SyncState.Confirmed);

        var current = _store.SavedUris("playlists");
        for (int i = 0; i < current.Count; i++)
        {
            var uri = current[i];
            if (!next.Contains(uri) && !_mutations.HasPending("playlists", uri))   // Pending-shielded rows survive the fold
                _store.SetSaved("playlists", uri, false, SyncState.Confirmed);
        }
    }

    void ScheduleSetRetry(List<string> logicalSets)
    {
        // Retry keys on the WIRE set (CollectionPush's contract): map the failed logical sets back to their wire sets (deduped
        // — liked+albums collapse to "collection"), so a re-push re-fetches every logical set the wire set carries. Idempotent
        // + token-gated ⇒ re-fetching a superset of the failed set is cheap.
        var wireSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in logicalSets) wireSets.Add(CollectionSets.WireSet(s));
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SetRetryDelay, _ct).ConfigureAwait(false); } catch { return; }
            foreach (var w in wireSets) Enqueue(new SyncCommand(SyncKind.CollectionPush, w));   // one-shot retry (settle + fetch)
        });
    }

    void ScheduleDrainReenqueue(TimeSpan delay)
    {
        lock (_gate) { if (_drainReenqueueScheduled) return; _drainReenqueueScheduled = true; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, _ct).ConfigureAwait(false); } catch { return; }
            Enqueue(new SyncCommand(SyncKind.DrainWrites));
        });
    }

    string RootlistUri() => "spotify:user:" + _username() + ":rootlist";

    bool IsOpen(string uri) { lock (_gate) return _openUri == uri; }
    void MarkDirty(string uri) { lock (_gate) _dirtyPlaylists.Add(uri); }
    void ClearDirty(string uri) { lock (_gate) _dirtyPlaylists.Remove(uri); }
    bool IsDirty(string uri) { lock (_gate) return _dirtyPlaylists.Contains(uri); }
    void MarkRevalidated(string uri) { lock (_gate) _lastRevalidatedAt[uri] = DateTime.UtcNow; }
    bool TryGetLastRevalidated(string uri, out DateTime t) { lock (_gate) return _lastRevalidatedAt.TryGetValue(uri, out t); }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _consumer.ConfigureAwait(false); } catch { /* cancelled / already stopped */ }
    }
}
