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

    IEnumerable<ColdSaved> LoadAllSaved();   // unordered library-set membership (collection_items), per active account
    void UpsertEntity(string uri, EntityKind kind, byte[] payload);   // non-blocking (write-behind)
    void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0);   // 0 = preserve stored added_at

    // Video↔audio associations: their own keyed-by-uri table (the file-id map survives restarts). Write-behind like entities.
    IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations();
    void UpsertVideoAssociation(string uri, byte[] payload);   // non-blocking (write-behind)

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

    void Flush();   // block until queued writes are durable
}

/// <summary>Locale-scoped raw extended-metadata cache. Implementations are bound to one launch locale so ETags and
/// payloads can never be read across languages.</summary>
public interface IExtensionCacheStore
{
    string? MetadataLocale { get; }
    IEnumerable<ColdExtension> LoadAllExtensions();
    void UpsertExtension(ColdExtension extension);
}
