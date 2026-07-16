using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Spotify;
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

    /// <summary>BULK hydrate many entities (a whole playlist). PARTIAL-CACHE aware: only stale/missing entities hit the
    /// network — of a 10k playlist with 5k already fresh, just the 5k misses fetch, and a fully-cached sync makes zero
    /// requests. The misses go to the source as one batch (which itself chunks by body size + gzips). Alloc-free parse.</summary>
    public async Task SyncAllAsync(IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        var misses = new List<EntityRef>(uris.Count);   // the bulk path is cold-cache (mostly all-miss) → pre-size, no resizes
        foreach (var uri in uris)
        {
            var cached = _res.Peek(Key(uri));
            if (cached.IsReady && !cached.IsStale) continue;   // fresh in cache → skip
            misses.Add(EntityRef.Parse(uri));
        }
        if (misses.Count == 0) return;
        if (_extensionCache is not null)
            await SyncAllConditionalAsync(misses, ct).ConfigureAwait(false);
        else
            await _source.FetchAsync(misses, _store, ct).ConfigureAwait(false);
        foreach (var e in misses) _res.Seed(Key(e.Uri), _store.Version(e.Uri));   // mark fetched → next sync skips them
    }

    async Task SyncAllConditionalAsync(IReadOnlyList<EntityRef> misses, CancellationToken ct)
    {
        var extensionRequests = new List<(string Uri, Xm.ExtensionKind Kind)>(misses.Count);
        var fallback = new List<EntityRef>();
        foreach (var entity in misses)
        {
            var kind = KindFor(entity.Kind);
            if (kind == Xm.ExtensionKind.UnknownExtension) fallback.Add(entity);
            else extensionRequests.Add((entity.Uri, kind));
        }

        if (extensionRequests.Count > 0)
        {
            var cached = await _extensionCache!.GetAsync(extensionRequests, ct).ConfigureAwait(false);
            ProjectCachedExtensions(cached, _store);
        }

        if (fallback.Count > 0)
            await _source.FetchAsync(fallback, _store, ct).ConfigureAwait(false);
    }

    static void ProjectCachedExtensions(
        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> cached, IStore store)
    {
        var arrays = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
        foreach (var ((uri, kind), ext) in cached)
        {
            if (ext.Missing || ext.Payload is null || ext.Payload.IsEmpty) continue;
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

        if (arrays.Count == 0) return;
        var resp = new Xm.BatchedExtensionResponse();
        foreach (var array in arrays.Values) resp.ExtendedMetadata.Add(array);
        ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store);
    }

    MetadataKey Key(string uri) => new(SpotifyHeaders.NormalizeLanguage(_ctx().Locale), uri);

    static Xm.ExtensionKind KindFor(EntityKind kind) => kind switch
    {
        EntityKind.Track => Xm.ExtensionKind.TrackV4,
        EntityKind.Album => Xm.ExtensionKind.AlbumV4,
        EntityKind.Artist => Xm.ExtensionKind.ArtistV4,
        EntityKind.Show => Xm.ExtensionKind.ShowV4,
        EntityKind.Episode => Xm.ExtensionKind.EpisodeV4,
        _ => Xm.ExtensionKind.UnknownExtension,
    };
}
