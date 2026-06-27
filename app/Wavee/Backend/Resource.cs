using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

// ── ENGINE ① — Resource (reactive cached revalidating reads) ─────────────────────────────────────────────────────────
// The single abstraction behind entity hydration, library reads, playlists, Home, Pathfinder views, and online search.
// A "config" (a surface) = a fetch + a freshness policy. The engine owns stale-while-revalidate + in-flight dedup. (The
// wire→project split is folded here for the scaffold; the real fetch composes Transport.Request + a projection-to-store.)

public enum LoadState { Loading, Ready, Error }

public readonly record struct Loaded<T>(LoadState State, T? Value, bool IsStale, string? Error)
{
    public bool IsLoading => State == LoadState.Loading;
    public bool IsReady => State == LoadState.Ready;
    public static Loaded<T> Loading => new(LoadState.Loading, default, false, null);
    public static Loaded<T> Ready(T value, bool stale = false) => new(LoadState.Ready, value, stale, null);
    public static Loaded<T> Err(string error) => new(LoadState.Error, default, false, error);
}

// Freshness is a STRATEGY, not bespoke code — one engine dispatches on it.
public abstract record FreshnessPolicy
{
    public sealed record Etag(TimeSpan Ttl) : FreshnessPolicy;                       // extended-metadata
    public sealed record RevisionDelta : FreshnessPolicy;                            // library sets
    public sealed record SnapshotRevision(bool ParentRevGate = true) : FreshnessPolicy; // playlists
    public sealed record PollWhole(TimeSpan Ttl, bool SuspendInPlayback) : FreshnessPolicy; // home/browse
    public sealed record Immutable : FreshnessPolicy;                                // gids, audio bytes
}

public sealed class Resource<TKey, TValue> where TKey : notnull
{
    readonly Func<TKey, SessionContext, Task<TValue>> _fetch;
    readonly FreshnessPolicy _fresh;
    readonly Func<SessionContext> _ctx;
    readonly object _gate = new();
    readonly Dictionary<TKey, Entry> _cache = new();
    int _fetchCount;

    public int FetchCount => _fetchCount;   // dedup / coalesce assertion hook

    sealed class Entry
    {
        public TValue? Val;
        public bool HasVal;
        public DateTime FetchedAt;
        public Task? InFlight;
    }

    public Resource(Func<TKey, SessionContext, Task<TValue>> fetch, FreshnessPolicy fresh, Func<SessionContext> ctx)
    {
        _fetch = fetch;
        _fresh = fresh;
        _ctx = ctx;
    }

    public Loaded<TValue> Use(TKey key)
    {
        // Snapshot the entry UNDER the lock — Val/HasVal/FetchedAt are written under _gate, so reading them outside it
        // can tear on a weak memory model (Apple Silicon is a target).
        bool hasVal, stale;
        TValue? val;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) { e = new Entry(); _cache[key] = e; }
            hasVal = e.HasVal;
            val = e.Val;
            stale = hasVal && IsStale(e);
        }
        if (hasVal)
        {
            if (stale) _ = Revalidate(key);      // SWR: serve stale, refresh in the background
            return Loaded<TValue>.Ready(val!, stale);
        }
        _ = Revalidate(key);
        return Loaded<TValue>.Loading;
    }

    public Task Revalidate(TKey key)
    {
        Entry e;
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out e!)) { e = new Entry(); _cache[key] = e; }
            if (e.InFlight != null) return e.InFlight;   // in-flight dedup: coalesce concurrent revalidations
            // Mark in-flight with a TCS BEFORE starting the fetch, so (a) the fetch runs OUTSIDE the lock (no user
            // delegate under our lock) and (b) the slot is keyed to THIS attempt — a synchronously-completing fetch can't
            // leave a stale non-null slot that permanently blocks future revalidations.
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            e.InFlight = tcs.Task;
        }
        _ = RunFetch(key, e, tcs);
        return tcs.Task;
    }

    /// <summary>Non-triggering snapshot of an entry (unlike Use, never starts a fetch) — for batch cache partitioning:
    /// Ready+!IsStale ⇒ fresh (skip), otherwise stale/missing (fetch).</summary>
    public Loaded<TValue> Peek(TKey key)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e) || !e.HasVal) return Loaded<TValue>.Loading;
            return Loaded<TValue>.Ready(e.Val!, IsStale(e));
        }
    }

    /// <summary>Record a value as freshly-fetched, bypassing the per-key fetch — for a batch path that fetched many at once
    /// (so subsequent <see cref="Peek"/>s see them as fresh and skip the network).</summary>
    public void Seed(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var e)) { e = new Entry(); _cache[key] = e; }
            e.Val = value;
            e.HasVal = true;
            e.FetchedAt = DateTime.UtcNow;
        }
    }

    async Task RunFetch(TKey key, Entry e, TaskCompletionSource tcs)
    {
        try
        {
            Interlocked.Increment(ref _fetchCount);
            var v = await _fetch(key, _ctx()).ConfigureAwait(false);
            lock (_gate) { e.Val = v; e.HasVal = true; e.FetchedAt = DateTime.UtcNow; }
        }
        catch
        {
            // Fetch failed — leave the entry as-is (a later Use retries). Swallowed so this fire-and-forget revalidation
            // can't fault unobserved (TaskScheduler.UnobservedTaskException). A richer impl would set a Loaded.Error.
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(e.InFlight, tcs.Task)) e.InFlight = null; }   // clear by identity
            tcs.SetResult();
        }
    }

    bool IsStale(Entry e) => _fresh switch
    {
        FreshnessPolicy.Etag et => DateTime.UtcNow - e.FetchedAt > et.Ttl,
        FreshnessPolicy.PollWhole pw => DateTime.UtcNow - e.FetchedAt > pw.Ttl,
        FreshnessPolicy.Immutable => false,
        _ => true,   // RevisionDelta / SnapshotRevision revalidate on a dealer push (LiveTopic) — eager otherwise
    };
}
