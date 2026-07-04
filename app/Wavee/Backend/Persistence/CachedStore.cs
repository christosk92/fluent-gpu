using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend.Persistence;

// ── STEP 4 — durable, offline-first IStore ───────────────────────────────────────────────────────────────────────────
// A FULL in-memory tier-1 (every entity — reads never touch disk) mirrored to a persistent cold tier via DUAL-WRITE:
// each mutation updates memory synchronously and enqueues a write-behind, batched persist (the UI never blocks on disk).
// On startup the whole cold tier bulk-loads into memory, including the persisted library (saved) state — that's offline.
public sealed class CachedStore : IStore, IDisposable
{
    readonly InMemoryStore _hot = new();
    readonly IColdStore _cold;

    // WARM tier: resident playlist membership baselines are bounded by a byte budget AND a count cap. On overflow the
    // least-recently-used baseline is evicted from the resident mirror (it stays in the cold tier and rehydrates on next
    // access). ~40 B/item is the SoA membership estimate. (HOT pinning — open/outbox-pending — is a later refinement;
    // the open list is touched constantly so it stays MRU.)
    const int BytesPerMembershipItem = 40;
    readonly int _maxResidentPlaylists;
    readonly long _maxResidentBytes;
    readonly object _lruGate = new();
    readonly Dictionary<string, (long Tick, long Bytes)> _resident = new();
    long _residentTick;
    long _residentBytes;

    public CachedStore(IColdStore cold, int maxResidentPlaylists = 128, long maxResidentBytes = 24L * 1024 * 1024)
    {
        _cold = cold;
        _maxResidentPlaylists = maxResidentPlaylists;
        _maxResidentBytes = maxResidentBytes;
        foreach (var e in _cold.LoadAllEntities()) Replay(e);                                  // entities → memory
        foreach (var v in _cold.LoadAllVideoAssociations()) ReplayVideo(v);                     // + the video↔audio map
        foreach (var s in _cold.LoadAllSaved()) _hot.SetSaved(s.SetId, s.Uri, true, s.Sync, s.AddedAtMs);   // + library state
    }

    public int ResidentMembershipCount { get { lock (_lruGate) return _resident.Count; } }

    void TouchResident(string playlistUri, int itemCount)
    {
        lock (_lruGate)
        {
            long bytes = (long)itemCount * BytesPerMembershipItem;
            if (_resident.TryGetValue(playlistUri, out var prev)) _residentBytes -= prev.Bytes;
            _resident[playlistUri] = (++_residentTick, bytes);
            _residentBytes += bytes;
            // Evict LRU until under both budgets — but never the just-touched MRU (count > 1 guards that).
            while (_resident.Count > 1 && (_resident.Count > _maxResidentPlaylists || _residentBytes > _maxResidentBytes))
            {
                string? lru = null;
                long min = long.MaxValue;
                foreach (var kv in _resident) if (kv.Value.Tick < min) { min = kv.Value.Tick; lru = kv.Key; }
                if (lru is null) break;
                _residentBytes -= _resident[lru].Bytes;
                _resident.Remove(lru);
                _hot.EvictMembership(lru);
            }
        }
    }

    void Replay(in ColdEntity e)
    {
        try
        {
            switch (e.Kind)
            {
                case EntityKind.Track: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Track); if (v != null) _hot.UpsertTrack(v); break; }
                case EntityKind.Album: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Album); if (v != null) _hot.UpsertAlbum(v); break; }
                case EntityKind.Artist: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Artist); if (v != null) _hot.UpsertArtist(v); break; }
                case EntityKind.Playlist: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Playlist); if (v != null) _hot.UpsertPlaylist(v); break; }
                case EntityKind.Show: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Show); if (v != null) _hot.UpsertShow(v); break; }
                case EntityKind.Episode: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Episode); if (v != null) _hot.UpsertEpisode(v); break; }
            }
        }
        catch (JsonException) { /* skip a corrupt row — it's re-fetchable */ }
    }

    void ReplayVideo(in ColdVideoAssoc v)
    {
        try { var a = JsonSerializer.Deserialize(v.Payload, EntityJson.Default.VideoAssociation); if (a != null) _hot.UpsertVideoAssociation(a); }
        catch (JsonException) { /* skip a corrupt row — it's re-fetchable */ }
    }

    // reads → the full in-memory mirror (no disk)
    public Track? GetTrack(string uri) => _hot.GetTrack(uri);
    public IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200) => _hot.QueryTracks(text, sort, limit);
    public Album? GetAlbum(string uri) => _hot.GetAlbum(uri);
    public Artist? GetArtist(string uri) => _hot.GetArtist(uri);
    public Playlist? GetPlaylist(string uri) => _hot.GetPlaylist(uri);
    public Show? GetShow(string uri) => _hot.GetShow(uri);
    public Episode? GetEpisode(string uri) => _hot.GetEpisode(uri);
    public VideoAssociation? GetVideoAssociation(string uri) => _hot.GetVideoAssociation(uri);
    public bool IsSaved(string setId, string uri) => _hot.IsSaved(setId, uri);
    public IReadOnlyList<string> SavedUris(string setId) => _hot.SavedUris(setId);

    // Membership + rootlist: dual-write (synchronous cold replace), and lazy-load from cold into the resident mirror on a
    // miss (the COLD → resident promotion). Large playlists aren't bulk-loaded at startup — they hydrate on first access.
    public void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev)
    {
        _hot.SetMembership(playlistUri, rows, baseRev);
        _cold.ReplaceMembership(playlistUri, ToCold(rows), baseRev);
        TouchResident(playlistUri, rows.Count);
    }
    public IReadOnlyList<PlaylistMember> Membership(string playlistUri)
    {
        var m = _hot.Membership(playlistUri);
        if (m.Count > 0) { TouchResident(playlistUri, m.Count); return m; }   // resident hit → bump recency
        var cold = _cold.LoadMembership(playlistUri);
        if (cold.Count == 0) return m;
        var rows = FromCold(cold);
        _hot.SetMembership(playlistUri, rows, _cold.GetPlaylistRevision(playlistUri));   // promote into the resident mirror
        TouchResident(playlistUri, rows.Count);
        return rows;
    }
    public byte[]? PlaylistRevision(string playlistUri) => _hot.PlaylistRevision(playlistUri) ?? _cold.GetPlaylistRevision(playlistUri);
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries)
    {
        _hot.SetRootlist(entries);   // preserve the stored revision (header hydration path)
        _cold.ReplaceRootlist(ToColdRoot(entries));
    }
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev)
    {
        _hot.SetRootlist(entries, rev);
        _cold.ReplaceRootlist(ToColdRoot(entries));
        _cold.SetRootlistRevision(rev);   // dual-write the rev to meta
    }
    public byte[]? RootlistRevision() => _hot.RootlistRevision() ?? _cold.GetRootlistRevision();
    public IReadOnlyList<RootlistEntry> Rootlist()
    {
        var r = _hot.Rootlist();
        if (r.Count > 0) return r;
        var mapped = FromColdRoot(_cold.LoadRootlist());
        if (mapped.Count > 0) _hot.SetRootlist(mapped);
        return mapped;
    }

    static IReadOnlyList<ColdPlaylistItem> ToCold(IReadOnlyList<PlaylistMember> rows)
    {
        var list = new List<ColdPlaylistItem>(rows.Count);
        for (int i = 0; i < rows.Count; i++) { var r = rows[i]; list.Add(new ColdPlaylistItem(r.ItemId, r.ItemUri, r.AddedBy, r.AddedAt)); }
        return list;
    }
    static IReadOnlyList<PlaylistMember> FromCold(IReadOnlyList<ColdPlaylistItem> rows)
    {
        var list = new List<PlaylistMember>(rows.Count);
        for (int i = 0; i < rows.Count; i++) { var r = rows[i]; list.Add(new PlaylistMember(r.ItemId, r.ItemUri, r.AddedBy, r.AddedAt)); }
        return list;
    }
    static IReadOnlyList<ColdRootlistEntry> ToColdRoot(IReadOnlyList<RootlistEntry> e)
    {
        var list = new List<ColdRootlistEntry>(e.Count);
        for (int i = 0; i < e.Count; i++) { var r = e[i]; list.Add(new ColdRootlistEntry(r.Position, r.Kind, r.Uri, r.GroupName, r.Depth)); }
        return list;
    }
    static IReadOnlyList<RootlistEntry> FromColdRoot(IReadOnlyList<ColdRootlistEntry> e)
    {
        var list = new List<RootlistEntry>(e.Count);
        for (int i = 0; i < e.Count; i++) { var r = e[i]; list.Add(new RootlistEntry(r.Position, r.Kind, r.Uri, r.GroupName, r.Depth)); }
        return list;
    }

    public long Version(string uri) => _hot.Version(uri);
    public IObservable<StoreChange> Changes => _hot.Changes;
    public void Bump(string uri, CollectionKind? kind = null) => _hot.Bump(uri, kind);
    public IDisposable BeginBulk() => _hot.BeginBulk();   // the cold tier is already write-behind batched

    // writes → DUAL: memory (synchronous) + cold (write-behind, batched)
    public void UpsertTrack(Track t)
    {
        _hot.UpsertTrack(t);
        var merged = _hot.GetTrack(t.Uri) ?? t;
        _cold.UpsertEntity(t.Uri, EntityKind.Track, JsonSerializer.SerializeToUtf8Bytes(merged, EntityJson.Default.Track));
    }
    // Persist the entity HEADER thin: a container's hydrated tracklist is a read-model (joined from membership × shared
    // entities at read), never baked into the entity blob — that would re-serialize a multi-MB LOH blob per edit and
    // duplicate every Track N times. The in-memory tier keeps whatever the caller passed; only the cold blob is thinned.
    public void UpsertAlbum(Album a)
    {
        _hot.UpsertAlbum(a);
        var merged = _hot.GetAlbum(a.Uri) ?? a;
        var thin = merged.Tracks is null ? merged : merged with { Tracks = null };
        _cold.UpsertEntity(a.Uri, EntityKind.Album, JsonSerializer.SerializeToUtf8Bytes(thin, EntityJson.Default.Album));
    }
    public void UpsertArtist(Artist a)
    {
        _hot.UpsertArtist(a);
        var merged = _hot.GetArtist(a.Uri) ?? a;
        _cold.UpsertEntity(a.Uri, EntityKind.Artist, JsonSerializer.SerializeToUtf8Bytes(merged, EntityJson.Default.Artist));
    }
    public void UpsertPlaylist(Playlist p) { _hot.UpsertPlaylist(p); var thin = p.Tracks is null ? p : p with { Tracks = null }; _cold.UpsertEntity(p.Uri, EntityKind.Playlist, JsonSerializer.SerializeToUtf8Bytes(thin, EntityJson.Default.Playlist)); }
    public void UpsertShow(Show s) { _hot.UpsertShow(s); _cold.UpsertEntity(s.Uri, EntityKind.Show, JsonSerializer.SerializeToUtf8Bytes(s, EntityJson.Default.Show)); }
    public void UpsertVideoAssociation(VideoAssociation a) { _hot.UpsertVideoAssociation(a); _cold.UpsertVideoAssociation(a.Uri, JsonSerializer.SerializeToUtf8Bytes(a, EntityJson.Default.VideoAssociation)); }
    public void UpsertEpisode(Episode e) { _hot.UpsertEpisode(e); _cold.UpsertEntity(e.Uri, EntityKind.Episode, JsonSerializer.SerializeToUtf8Bytes(e, EntityJson.Default.Episode)); }
    // Ask the hot tier whether the write actually changed state (§7.4 no-op elision) and skip the cold dual-write when it
    // didn't — so an idempotent echo/delta-overlap costs neither a change signal nor a SQLite round-trip. added_at rides
    // both tiers (0 = preserve-existing, resolved per tier); a pure timestamp refinement still reaches the cold tier.
    public void SetSaved(string setId, string uri, bool saved, SyncState sync) => SetSaved(setId, uri, saved, sync, 0);
    public void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs)
    {
        bool changed = _hot.SetSavedCore(setId, uri, saved, sync, addedAtMs);
        if (changed || (saved && addedAtMs != 0)) _cold.UpsertSaved(setId, uri, saved, sync, addedAtMs);
    }
    public IReadOnlyList<SavedItem> SavedItems(string setId) => _hot.SavedItems(setId);

    public void Flush() => _cold.Flush();
    public void Dispose() => _cold.Dispose();
}

// AOT-clean source-gen serialization for the persisted entities (the generator pulls in the nested refs automatically).
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Track))]
[JsonSerializable(typeof(Album))]
[JsonSerializable(typeof(Artist))]
[JsonSerializable(typeof(Playlist))]
[JsonSerializable(typeof(Show))]
[JsonSerializable(typeof(Episode))]
[JsonSerializable(typeof(VideoAssociation))]
internal partial class EntityJson : JsonSerializerContext { }
