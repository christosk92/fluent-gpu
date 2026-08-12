using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

// STEP 3 — the consume API over the Resource engine. Single reads go through Resource (SWR + dedup + FreshnessPolicy);
// BULK hydration (a 10k-track playlist) parses once into one array (alloc-free per-uri parse) and hands the WHOLE batch
// to the source, which packs it into as-few-as-possible body-size-bounded, gzip-compressed requests. The UI reads the
// Store; this only coordinates freshness, dedup, and batching.
public sealed class MetadataService
{
    readonly record struct MetadataKey(string Locale, string Uri);
    readonly IMetadataSource _source;
    readonly IStore _store;
    readonly Resource<MetadataKey, long> _res;
    readonly ExtensionEtagCache? _extensionCache;
    readonly Func<SessionContext> _ctx;

    // Chokepoint closure (S2): after a SyncAll lands (or skips-as-fresh) track uris, scan resident rows for blank
    // AlbumRefs / thin tracks and fire-and-forget a depth-bounded second wave. Same bounds the deleted
    // LikedAlbumNameBackfill shipped — once-per-session, 300/batch, ≤900/pass, 512-row yield.
    const int ClosureBatchSize = 300;
    const int ClosureMaxPerPass = 900;
    readonly object _closureGate = new();
    readonly HashSet<string> _closureAttempted = new(StringComparer.Ordinal);

    public MetadataService(IMetadataSource source, IStore store, Func<SessionContext> ctx, TimeSpan? ttl = null,
        ExtensionEtagCache? extensionCache = null)
    {
        _source = source;
        _store = store;
        _ctx = ctx;
        _extensionCache = extensionCache;
        _res = new Resource<MetadataKey, long>(
            async (key, _) => { await source.FetchAsync(new[] { EntityRef.Parse(key.Uri) }, store, CancellationToken.None).ConfigureAwait(false); return store.Version(key.Uri); },
            new FreshnessPolicy.Etag(ttl ?? TimeSpan.FromHours(1)),   // catalog facts: TTL (+ conditional refresh later)
            ctx);
    }

    /// <summary>On-demand single-entity read (SWR + in-flight dedup). Returns load state; data is read from the Store.</summary>
    public Loaded<long> Use(string uri) => _res.Use(Key(uri));
    public Task EnsureAsync(string uri) => _res.Revalidate(Key(uri));
    public int FetchCount => _res.FetchCount;

    /// <summary>Mark a catalog uri dirty so the next <see cref="SyncAllAsync"/> / <see cref="Use"/> re-fetches it.
    /// Video recovery and dealer routes call this when a sealed miss must not win against a known-better outcome.</summary>
    public void MarkStale(string uri) => _res.MarkStale(Key(uri));

    /// <summary>BULK hydrate many entities (a whole playlist). PARTIAL-CACHE aware: only stale/missing entities hit the
    /// network — of a 10k playlist with 5k already fresh, just the 5k misses fetch, and a fully-cached sync makes zero
    /// requests. The misses go to the source as one batch (which itself chunks by body size + gzips). Alloc-free parse.
    /// Freshness seals ONLY uris whose projection landed — omitted / Missing payloads stay unsealed so the next hydrate
    /// retries them (outcome seeding, not batch-membership seeding).
    /// When <paramref name="closeRefs"/> is true (default), a fire-and-forget closure scans the requested TRACK rows for
    /// blank AlbumRefs / thin tracks and re-enters them once (depth-bounded — the second wave does not rescan).</summary>
    public Task SyncAllAsync(IReadOnlyList<string> uris, CancellationToken ct = default)
        => SyncAllAsync(uris, ct, closeRefs: true);

    /// <param name="clientFeatureId">Optional <c>client-feature-id</c> attribution for the traffic this call generates
    /// (the desktop client stamps one per surface — e.g. <c>mdata_esperanto</c> for recents viewport hydration). Null =
    /// header omitted = the pre-existing behaviour for every other caller. It rides through to the transport rather than
    /// being fixed at construction because ONE MetadataService serves every surface.</param>
    /// <param name="headerTraits">Opt-in: also request the header-trait bundle
    /// (<see cref="ExtendedMetadataSource.HeaderTraitKinds"/>) alongside each entity's catalogue kind, matching the
    /// desktop client's per-<c>client-feature-id</c> kind set.
    ///
    /// DEFAULT OFF, AND THE DEFAULT IS THE POINT. This method is the app's ONE extended-metadata chokepoint: the
    /// discography prefetcher hands it 500 uris at a time and the tracklist loaders 300, and three extra kinds per
    /// entity there would inflate every request in the app for payloads nobody reads. Only the surface the census
    /// actually attributes the bundle to (the recents viewport hydrator) passes true.
    ///
    /// FRESHNESS IS UNAFFECTED. Seeding below keys on <c>landed</c>, which <see cref="ExtendedMetadataSource"/> only
    /// fills for kinds it can PROJECT — a trait kind never lands, never seals, and never un-seals a uri. And the
    /// per-uri freshness gate at the top of this method runs BEFORE any kind is chosen, so a uri that sealed on its
    /// catalogue kind is skipped whole: its trait kinds are not re-requested either. There is no per-kind churn loop.</param>
    public async Task SyncAllAsync(IReadOnlyList<string> uris, CancellationToken ct, bool closeRefs,
                                   string? clientFeatureId = null, bool headerTraits = false)
    {
        var misses = new List<EntityRef>(uris.Count);   // the bulk path is cold-cache (mostly all-miss) → pre-size, no resizes
        foreach (var uri in uris)
        {
            var cached = _res.Peek(Key(uri));
            if (cached.IsReady && !cached.IsStale) continue;   // fresh in cache → skip
            misses.Add(EntityRef.Parse(uri));
        }
        if (misses.Count > 0)
        {
            IReadOnlyCollection<string> landed;
            if (_extensionCache is not null)
                landed = await SyncAllConditionalAsync(misses, ct, clientFeatureId, headerTraits).ConfigureAwait(false);
            else
                landed = await _source.FetchAsync(misses, _store, ct, clientFeatureId, headerTraits).ConfigureAwait(false);
            foreach (var uri in landed) _res.Seed(Key(uri), _store.Version(uri));
        }
        // Closure scans the ORIGINAL request (fresh-skips included) — a row cached thin by an earlier session still heals.
        if (closeRefs && uris.Count > 0) ScheduleClosure(uris, ct);
    }

    void ScheduleClosure(IReadOnlyList<string> requested, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        // Snapshot the list — callers reuse buffers; the closure runs off-thread.
        var snapshot = requested is string[] arr ? (IReadOnlyList<string>)arr : new List<string>(requested);
        _ = Task.Run(async () =>
        {
            try { await RunClosureAsync(snapshot, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* best-effort — thin refs are cosmetic gaps until the next surface touch */ }
        }, ct);
    }

    async Task RunClosureAsync(IReadOnlyList<string> requested, CancellationToken ct)
    {
        List<string>? need = null;
        for (int i = 0; i < requested.Count; i++)
        {
            if ((i & 511) == 511) await Task.Delay(1, ct).ConfigureAwait(false);
            var uri = requested[i];
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) continue;
            if (_store.GetTrack(uri) is not { } track) continue;

            if (StoreEntityGaps.RefNeedsName(track.Album))
            {
                bool first;
                lock (_closureGate) first = _closureAttempted.Add(track.Album.Uri);
                if (first) (need ??= new List<string>(ClosureBatchSize)).Add(track.Album.Uri);
            }
            if (StoreEntityGaps.TrackNeedsData(track))
            {
                bool first;
                lock (_closureGate) first = _closureAttempted.Add(uri);
                if (first) (need ??= new List<string>(ClosureBatchSize)).Add(uri);
            }
            if (need is { Count: >= ClosureMaxPerPass }) break;
        }
        if (need is null) return;

        for (int i = 0; i < need.Count; i += ClosureBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = need.GetRange(i, Math.Min(ClosureBatchSize, need.Count - i));
            // closeRefs:false — depth bound; do not rescan album/track second-wave for further refs.
            try { await SyncAllAsync(batch, ct, closeRefs: false).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* counted nowhere — next surface touch retries via unsealed freshness */ }
        }
    }

    async Task<IReadOnlyCollection<string>> SyncAllConditionalAsync(IReadOnlyList<EntityRef> misses, CancellationToken ct,
                                                                    string? clientFeatureId, bool headerTraits)
    {
        int perEntity = headerTraits ? 1 + ExtendedMetadataSource.HeaderTraitKinds.Length : 1;
        var extensionRequests = new List<(string Uri, Xm.ExtensionKind Kind)>(misses.Count * perEntity);
        var fallback = new List<EntityRef>();
        foreach (var entity in misses)
        {
            var kind = KindFor(entity.Kind);
            if (kind == Xm.ExtensionKind.UnknownExtension) { fallback.Add(entity); continue; }
            extensionRequests.Add((entity.Uri, kind));
            // Mirrors ExtendedMetadataSource.GzipRequest's bundle on the CONDITIONAL arm. GzipExtensionRequest groups by
            // uri (its byUri map), so these land as extra ExtensionQuery entries under the SAME EntityRequest as the
            // catalogue kind — one POST, four kinds per uri, not a second round trip.
            //
            // ETAG BOOKKEEPING IS PER-(uri, kind) AND STAYS CORRECT. Each trait gets its own ExtensionEtagCache row,
            // its own ETag and its own TTL; a 404 on 178 folds to a Missing row (24h) rather than re-asking, and
            // Fold() already refuses to adopt an ETag onto a Missing row (the 304-forever trap). Nothing here can
            // un-seal the catalogue kind, because ProjectCachedExtensions only reports uris a PROJECTION wrote.
            //
            // ONE HONEST CONSEQUENCE, stated rather than papered over: because the cache suppresses fresh keys, a
            // re-hydrate of a uri whose catalogue row expired (MetadataService TTL, 1h) but whose trait rows have not
            // (CachedExtension.DefaultTtl, 6h) sends the catalogue kind ALONE. The wire shape therefore matches the
            // real client on a cold window and is a strict subset of it on a warm one. That is the ETag cache doing
            // its job; re-asking for a payload we already hold to look more like the client would be waste.
            if (headerTraits)
                foreach (var trait in ExtendedMetadataSource.HeaderTraitKinds)
                    extensionRequests.Add((entity.Uri, trait));
        }

        var landed = new HashSet<string>(StringComparer.Ordinal);
        if (extensionRequests.Count > 0)
        {
            var cached = await _extensionCache!.GetAsync(extensionRequests, ct, clientFeatureId).ConfigureAwait(false);
            foreach (var uri in ProjectCachedExtensions(cached, _store))
                landed.Add(uri);
        }

        if (fallback.Count > 0)
        {
            // An UnknownExtension uri (e.g. `spotify:user:<id>:collection`) is dropped by GzipRequest before a query is
            // written, so the flag rides along for consistency and is a no-op there.
            foreach (var uri in await _source.FetchAsync(fallback, _store, ct, clientFeatureId, headerTraits).ConfigureAwait(false))
                landed.Add(uri);
        }
        return landed;
    }

    static HashSet<string> ProjectCachedExtensions(
        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> cached, IStore store)
    {
        var arrays = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
        foreach (var ((uri, kind), ext) in cached)
        {
            if (ext.Missing || ext.Payload is null || ext.Payload.IsEmpty) continue;
            // Header-trait payloads are cached (so the next window's request stays conditional) but NOT re-serialized
            // into the synthetic response below: ProjectParsed would only `default: continue` past them, and 178/220
            // have no schema to decode anyway. 179 DOES (visual_identity_trait.proto) — reading it here as a cover
            // fallback would mean a per-kind write into six different entity records under StoreEntityMerge's
            // "absence means CLEAR" rule (see ProjectPlaylist), which is a bigger change than wire fidelity needs.
            if (Array.IndexOf(ExtendedMetadataSource.HeaderTraitKinds, kind) >= 0) continue;
            if (!arrays.TryGetValue(kind, out var array))
            {
                array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
                arrays[kind] = array;
            }
            array.ExtensionData.Add(new Xm.EntityExtensionData
            {
                EntityUri = uri,
                ExtensionData = new Any { Value = ext.Payload },
            });
        }

        if (arrays.Count == 0) return [];
        var resp = new Xm.BatchedExtensionResponse();
        foreach (var array in arrays.Values) resp.ExtendedMetadata.Add(array);
        return ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);
    }

    MetadataKey Key(string uri) => new(SpotifyHeaders.NormalizeLanguage(_ctx().Locale), uri);

    static Xm.ExtensionKind KindFor(EntityKind kind) => kind switch
    {
        EntityKind.Track => Xm.ExtensionKind.TrackV4,
        EntityKind.Album => Xm.ExtensionKind.AlbumV4,
        EntityKind.Artist => Xm.ExtensionKind.ArtistV4,
        EntityKind.Show => Xm.ExtensionKind.ShowV4,
        EntityKind.Episode => Xm.ExtensionKind.EpisodeV4,
        // A playlist header rides LIST_METADATA_V2 (205); it has no V4. Must stay identical to
        // ExtendedMetadataSource.KindFor — this one picks the CONDITIONAL (etag-cached) request list, that one picks the
        // plain batch, and a divergence would silently send playlists down the uncached arm.
        EntityKind.Playlist => Xm.ExtensionKind.ListMetadataV2,
        _ => Xm.ExtensionKind.UnknownExtension,
    };
}
