using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Metadata;

namespace Wavee.Backend.Persistence;

// ── STEP 4 — the durable (cold) tier seam ────────────────────────────────────────────────────────────────────────────
// The persistent source of truth (offline-first). SQLite is the production impl; a memory fake backs unit tests. The
// in-memory tier-1 (CachedStore) bulk-loads from here on startup and dual-writes every mutation back, write-behind.

public readonly record struct ColdEntity(string Uri, EntityKind Kind, byte[] Payload);
public readonly record struct ColdExtension(
    string EntityUri,
    int ExtensionKind,
    byte[]? Payload,
    string? Etag,
    long OfflineTtlSeconds,
    bool Missing,
    long ExpiresAtUnixSeconds,
    long UpdatedAtUnixSeconds);
/// <summary>One persisted video↔audio association blob (JSON of <c>VideoAssociation</c>), keyed by entity uri in its
/// OWN table (it shares the track uri, so it can't live in the entity store).</summary>
public readonly record struct ColdVideoAssoc(string Uri, byte[] Payload);
public readonly record struct ColdSaved(string SetId, string Uri, SyncState Sync, long AddedAtMs = 0);
/// <summary>One ordered playlist-membership row: the stable per-row <paramref name="ItemId"/> (survives reorder),
/// the referenced entity <paramref name="ItemUri"/>, and the per-membership add facts.</summary>
public readonly record struct ColdPlaylistItem(string ItemId, string ItemUri, string? AddedBy, long AddedAt);
/// <summary>One rootlist row: a playlist uri or a start/end-group marker. <paramref name="Kind"/> 0=item, 1=start-group, 2=end-group.</summary>
public readonly record struct ColdRootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth);

/// <summary>One recently-opened detail surface (schema v5, `recent_surfaces`). A pin REASON: the newest 50 opened
/// surfaces are exempt from the cache-tier TTL/budget so a restart repaints them offline.</summary>
public readonly record struct ColdRecentSurface(string Uri, int Kind, long LastOpenedUnixSeconds);

/// <summary>The fat artist facets split out of the Artist record (schema v5, `artist_overview`). <paramref name="Payload"/>
/// is the DECODED raw JSON (the store owns the fmt framing). Its own TTL: disposable, re-derived by an ArtistV4 SWR pass.</summary>
public readonly record struct ColdArtistOverview(string Uri, string Locale, byte[] Payload, long FetchedAtUnixSeconds);

public interface IColdStore : IDisposable
{
    IEnumerable<ColdEntity> LoadAllEntities();

    /// <summary>Load ONE persisted entity by uri, or null if the cold tier has no row for it. The rehydration seam for
    /// CachedStore's cold-fallback reads (a hot miss after an entity eviction). The default is a linear scan of
    /// <see cref="LoadAllEntities"/> (fine for the in-memory test fake); SQLite overrides it with an indexed single-row
    /// lookup. Sees only committed rows — an entity still queued in the write-behind lane may be missed, which is safe
    /// here: eviction only reaches entities resident long enough for the lane to have drained.</summary>
    ColdEntity? GetEntity(string uri)
    {
        foreach (var e in LoadAllEntities()) if (e.Uri == uri) return e;
        return null;
    }

    /// <summary>Load a BATCH of persisted entities by uri (the Wave-B warm set: saved sets ∪ recent surfaces ∪ rootlist
    /// headers). The default walks <see cref="GetEntity"/> per uri (fine for the in-memory test fake); SQLite overrides it
    /// with chunked <c>IN (…)</c> reads on the read connection. Missing uris are simply absent from the result.</summary>
    IEnumerable<ColdEntity> LoadEntities(IReadOnlyCollection<string> uris)
    {
        var list = new List<ColdEntity>(uris.Count);
        foreach (var uri in uris) if (GetEntity(uri) is { } e) list.Add(e);
        return list;
    }

    IEnumerable<ColdSaved> LoadAllSaved();   // unordered library-set membership (collection_items), per active account
    void UpsertEntity(string uri, EntityKind kind, byte[] payload);   // non-blocking (write-behind)
    void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0);   // 0 = preserve stored added_at

    // Video↔audio associations: their own keyed-by-uri table (the file-id map survives restarts). Write-behind like entities.
    IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations();
    void UpsertVideoAssociation(string uri, byte[] payload);   // non-blocking (write-behind)

    // User-attached local video overrides (`video_override`, schema v4): typed columns rather than a payload blob, because
    // the roster UI queries them by field. Deliberately NOT folded into video_assoc — Spotify's 6-hourly revalidation
    // full-replaces those rows and would erase the user's curation. The defaults keep pre-v4 / in-memory fakes compiling
    // (an override-free cold tier is simply an empty roster).
    IEnumerable<VideoOverride> LoadAllVideoOverrides() => Array.Empty<VideoOverride>();
    void UpsertVideoOverride(VideoOverride o) { }        // non-blocking (write-behind)
    void DeleteVideoOverride(string uri) { }             // non-blocking (write-behind)

    // Per-set sync token (the opaque collection delta cursor / playlist-style revision). null = never synced.
    string? GetCollectionRevision(string setId);
    void SetCollectionRevision(string setId, string? revision, long syncedAt);   // non-blocking (write-behind, ordered after its items)

    // The opaque rootlist revision (the rootlist's playlist-style base revision), stored in meta(key='rootlist_rev').
    // null = never synced / cleared. Synchronous + atomic (a coarse op, like the rootlist replace itself).
    byte[]? GetRootlistRevision();
    void SetRootlistRevision(byte[]? rev);

    // Ordered playlist membership + the opaque playlist revision. Replace is synchronous + atomic (bulk delete+insert+rev in one tx).
    IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri);
    void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev);
    byte[]? GetPlaylistRevision(string playlistUri);

    // The rootlist (flat ordered marker stream → tree at read). Replace is synchronous + atomic.
    IReadOnlyList<ColdRootlistEntry> LoadRootlist();
    void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries);

    // ── schema-v5 cache-tier surfaces (defaults keep the in-memory fakes compiling) ──────────────────────────────────
    /// <summary>The recently-opened detail surfaces — a PIN REASON the write gate mirrors in memory.</summary>
    IReadOnlyList<ColdRecentSurface> LoadRecentSurfaces() => Array.Empty<ColdRecentSurface>();
    /// <summary>Record a detail-page open (LRU-capped at 50 rows by the implementation).</summary>
    void UpsertRecentSurface(string uri, int kind, long nowUnixSeconds) { }
    /// <summary>The fat artist facets split out of the Artist record at persist time (decoded JSON), or null.</summary>
    ColdArtistOverview? GetArtistOverview(string uri) => null;
    /// <summary>Persist the artist overview facets. <paramref name="locale"/> "" ⇒ the store's own launch locale.</summary>
    void UpsertArtistOverview(string uri, string locale, byte[] payloadJson, long nowUnixSeconds) { }
    /// <summary>Replace the pin-closure edges out of <paramref name="parentUri"/> (album→artists, artist→albums).</summary>
    void ReplaceEntityRefs(string parentUri, IEnumerable<string> children) { }

    void Flush();   // block until queued writes are durable
}

/// <summary>Locale-scoped raw extended-metadata cache. Implementations are bound to one launch locale so ETags and
/// payloads can never be read across languages.</summary>
public interface IExtensionCacheStore
{
    string? MetadataLocale { get; }

    /// <summary>Newest-first extension rows, capped at <paramref name="limit"/>. The seed loop only keeps its own
    /// <c>maxEntries</c> (2048) rows, so reading the whole table was pure I/O waste — the cap belongs in the SQL.
    /// <c>limit &lt;= 0</c> = no cap.</summary>
    IEnumerable<ColdExtension> LoadAllExtensions(int limit = 2048);
    void UpsertExtension(ColdExtension extension);
}
