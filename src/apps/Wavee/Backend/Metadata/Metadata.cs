using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.Backend.Metadata;

// ── STEP 3 — the native metadata layer ───────────────────────────────────────────────────────────────────────────────
// Metadata is a GENERAL system (every entity type + facet), not a track fetch. The design (see chat analysis): a swappable
// IMetadataSource fetched THROUGH the Resource engine (SWR + dedup + FreshnessPolicy) and PROJECTED into the Store — i.e.
// we reuse the cache we already built (Resource + Store + FreshnessPolicy) instead of a new EntityStore/cache stack.

public enum EntityKind { Unknown, Track, Album, Artist, Playlist, Show, Episode }

/// <summary>Generic addressing for ANY entity. extended-metadata addresses by the FULL uri, so we keep just (uri, kind) —
/// no id substring. Parse is ALLOCATION-FREE (span scan + constant-span switch, no String.Split) because it runs per
/// entity at 10k+ scale, where Split's per-call array + substrings would be real GC pressure.</summary>
public readonly record struct EntityRef(string Uri, EntityKind Kind)
{
    public static EntityRef Parse(string uri)
    {
        var s = uri.AsSpan();
        if (s.StartsWith("spotify:"))
        {
            var rest = s[8..];                                   // after "spotify:"
            int colon = rest.IndexOf(':');
            return new EntityRef(uri, KindOf(colon >= 0 ? rest[..colon] : rest));
        }
        return new EntityRef(uri, EntityKind.Unknown);
    }

    static EntityKind KindOf(ReadOnlySpan<char> type) => type switch   // constant-span patterns — no allocation
    {
        "track" => EntityKind.Track,
        "album" => EntityKind.Album,
        "artist" => EntityKind.Artist,
        "playlist" => EntityKind.Playlist,
        "show" => EntityKind.Show,
        "episode" => EntityKind.Episode,
        _ => EntityKind.Unknown,
    };
}

/// <summary>The metadata fetch seam: BATCH fetch + project N entities into the Store in one shot. The real impl is
/// spclient extended-metadata (a single protobuf request addresses many entities × facets, capped at 500 — so a 10k-track
/// playlist is ~20 requests, never 10k). The public Web API is NOT used (it can't batch and the desktop client avoids it);
/// Pathfinder is the sibling source for presentation/relations. The UI never calls this — it reads the Store while
/// <see cref="MetadataService"/> coordinates freshness, dedup, and batching.</summary>
public interface IMetadataSource
{
    /// <summary>Fetch and project into the store. Returns the uris that actually landed (projection wrote an entity) —
    /// not merely requested. Omitted / failed entities stay out so <see cref="MetadataService"/> seals freshness on
    /// outcome only.</summary>
    /// <param name="clientFeatureId">Optional <c>client-feature-id</c> attribution header for the traffic this batch
    /// generates — the desktop client stamps one per SURFACE. Null (every pre-existing caller) omits the header, so the
    /// wire is byte-identical to the pre-seam behaviour.</param>
    /// <param name="headerTraits">Opt-in: also request the desktop client's HEADER-TRAIT bundle
    /// (<see cref="ExtendedMetadataSource.HeaderTraitKinds"/> — 178/179/220) alongside each entity's catalogue kind.
    /// FALSE for every pre-existing caller, and deliberately so: this is the shared bulk path, and three extra kinds
    /// per entity on a 500-uri discography prefetch or a 300-uri tracklist load is pure inflation. Only the surface
    /// whose <paramref name="clientFeatureId"/> the census actually attributes the bundle to opts in.</param>
    Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct,
                                                 string? clientFeatureId = null, bool headerTraits = false);
}

/// <summary>Packs entities into request chunks by serialized BODY SIZE (not a fixed count), so each POST is pushed as
/// large as the server allows — a 10k-track playlist becomes ~1 request, not 20. Memory-efficient: yields index ranges,
/// never sub-lists. The real extended-metadata source iterates these ranges, serializing + gzipping each chunk's body.</summary>
public static class MetadataChunking
{
    public const int DefaultMaxBodyBytes = 4 * 1024 * 1024;   // tune toward the real spclient POST-body ceiling (confirm live)

    /// <summary>Estimated wire cost of one EntityRequest: entity_uri bytes + proto field tags + one ExtensionQuery.</summary>
    public static int EstimateBytes(in EntityRef e) => e.Uri.Length + 12;

    /// <summary>Yields (start, count) index ranges, each whose estimated body stays under maxBodyBytes (always ≥1 entity).</summary>
    /// <param name="extraBytesPerEntity">Wire cost of any ADDITIONAL <c>ExtensionQuery</c> entries the caller packs under
    /// each uri (the header-trait bundle). Keeps the estimate honest instead of under-counting a request that carries
    /// four kinds per entity rather than one.</param>
    public static IEnumerable<(int Start, int Count)> Ranges(IReadOnlyList<EntityRef> entities,
        int maxBodyBytes = DefaultMaxBodyBytes, int headerBytes = 64, int extraBytesPerEntity = 0)
    {
        int start = 0, size = headerBytes;
        for (int i = 0; i < entities.Count; i++)
        {
            int cost = EstimateBytes(entities[i]) + extraBytesPerEntity;
            if (i > start && size + cost > maxBodyBytes)   // flush the current range (never split below 1 entity)
            {
                yield return (start, i - start);
                start = i;
                size = headerBytes;
            }
            size += cost;
        }
        if (entities.Count > start) yield return (start, entities.Count - start);
    }
}
