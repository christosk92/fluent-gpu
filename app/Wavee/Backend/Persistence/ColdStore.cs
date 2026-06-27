using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Metadata;

namespace Wavee.Backend.Persistence;

// ── STEP 4 — the durable (cold) tier seam ────────────────────────────────────────────────────────────────────────────
// The persistent source of truth (offline-first). SQLite is the production impl; a memory fake backs unit tests. The
// in-memory tier-1 (CachedStore) bulk-loads from here on startup and dual-writes every mutation back, write-behind.

public readonly record struct ColdEntity(string Uri, EntityKind Kind, byte[] Payload);
public readonly record struct ColdSaved(string SetId, string Uri, SyncState Sync);
/// <summary>One ordered playlist-membership row: the stable per-row <paramref name="ItemId"/> (survives reorder),
/// the referenced entity <paramref name="ItemUri"/>, and the per-membership add facts.</summary>
public readonly record struct ColdPlaylistItem(string ItemId, string ItemUri, string? AddedBy, long AddedAt);
/// <summary>One rootlist row: a playlist uri or a start/end-group marker. <paramref name="Kind"/> 0=item, 1=start-group, 2=end-group.</summary>
public readonly record struct ColdRootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth);

public interface IColdStore : IDisposable
{
    IEnumerable<ColdEntity> LoadAllEntities();
    IEnumerable<ColdSaved> LoadAllSaved();   // unordered library-set membership (collection_items), per active account
    void UpsertEntity(string uri, EntityKind kind, byte[] payload);   // non-blocking (write-behind)
    void UpsertSaved(string setId, string uri, bool saved, SyncState sync);

    // Per-set sync token (the opaque collection delta cursor / playlist-style revision). null = never synced.
    string? GetCollectionRevision(string setId);
    void SetCollectionRevision(string setId, string? revision, long syncedAt);   // non-blocking (write-behind, ordered after its items)

    // Ordered playlist membership + the opaque playlist revision. Replace is synchronous + atomic (bulk delete+insert+rev in one tx).
    IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri);
    void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev);
    byte[]? GetPlaylistRevision(string playlistUri);

    // The rootlist (flat ordered marker stream → tree at read). Replace is synchronous + atomic.
    IReadOnlyList<ColdRootlistEntry> LoadRootlist();
    void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries);

    void Flush();   // block until queued writes are durable
}
