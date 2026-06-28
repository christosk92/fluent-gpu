using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.Backend.Metadata;

// STEP 3 — the consume API over the Resource engine. Single reads go through Resource (SWR + dedup + FreshnessPolicy);
// BULK hydration (a 10k-track playlist) parses once into one array (alloc-free per-uri parse) and hands the WHOLE batch
// to the source, which packs it into as-few-as-possible body-size-bounded, gzip-compressed requests. The UI reads the
// Store; this only coordinates freshness, dedup, and batching.
public sealed class MetadataService
{
    readonly IMetadataSource _source;
    readonly IStore _store;
    readonly Resource<string, long> _res;

    public MetadataService(IMetadataSource source, IStore store, Func<SessionContext> ctx, TimeSpan? ttl = null)
    {
        _source = source;
        _store = store;
        _res = new Resource<string, long>(
            async (uri, _) => { await source.FetchAsync(new[] { EntityRef.Parse(uri) }, store, CancellationToken.None).ConfigureAwait(false); return store.Version(uri); },
            new FreshnessPolicy.Etag(ttl ?? TimeSpan.FromHours(1)),   // catalog facts: TTL (+ conditional refresh later)
            ctx);
    }

    /// <summary>On-demand single-entity read (SWR + in-flight dedup). Returns load state; data is read from the Store.</summary>
    public Loaded<long> Use(string uri) => _res.Use(uri);
    public Task EnsureAsync(string uri) => _res.Revalidate(uri);
    public int FetchCount => _res.FetchCount;

    /// <summary>BULK hydrate many entities (a whole playlist). PARTIAL-CACHE aware: only stale/missing entities hit the
    /// network — of a 10k playlist with 5k already fresh, just the 5k misses fetch, and a fully-cached sync makes zero
    /// requests. The misses go to the source as one batch (which itself chunks by body size + gzips). Alloc-free parse.</summary>
    public async Task SyncAllAsync(IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        var misses = new List<EntityRef>(uris.Count);   // the bulk path is cold-cache (mostly all-miss) → pre-size, no resizes
        foreach (var uri in uris)
        {
            var cached = _res.Peek(uri);
            if (cached.IsReady && !cached.IsStale) continue;   // fresh in cache → skip
            misses.Add(EntityRef.Parse(uri));
        }
        if (misses.Count == 0) return;
        await _source.FetchAsync(misses, _store, ct).ConfigureAwait(false);
        foreach (var e in misses) _res.Seed(e.Uri, _store.Version(e.Uri));   // mark fetched → next sync skips them
    }
}
