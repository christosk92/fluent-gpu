using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend;

// ── THE STORE — the queryable spine (the plan's §1) ──────────────────────────────────────────────────────────────────
// Single source of truth. The plan's durable layer is SQLite with indexed columns; this is the in-memory backing behind
// the same IStore seam (a SqliteStore is the one swap-in). Entities are QUERYABLE by field (title/artist) — the offline
// search/sort/filter index — and every mutation bumps a per-uri version that drives change signals (→ the UI bridges).

public enum SyncState { Confirmed, Pending, Failed }
public enum TrackSort { None, Title, Artist, DurationAsc }

public readonly record struct StoreChange(string Uri, bool IsBulk = false, CollectionKind? Kind = null)   // struct → no heap alloc per Bump, no boxing through SimpleSubject<StoreChange>
{
    public static readonly StoreChange Bulk = new("", true);   // one signal for a bulk load; subscribers re-read
}

/// <summary>One rootlist row in the queryable spine: a playlist uri or a start/end-group marker (Kind 0=item, 1=start, 2=end).</summary>
public readonly record struct RootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth);

/// <summary>One library-set member with its server add timestamp (unix ms; 0 = unknown) — the Liked-songs/collections
/// default order (added-date descending) reads this; <see cref="IStore.SavedUris"/> stays the unordered fast path.</summary>
public readonly record struct SavedItem(string Uri, long AddedAtMs);

public interface IStore
{
    // entities (queryable)
    void UpsertTrack(Track t);
    Track? GetTrack(string uri);
    IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200);
    // other entity kinds — the metadata layer projects EVERY entity type here, not just tracks
    void UpsertAlbum(Album a);
    Album? GetAlbum(string uri);
    void UpsertArtist(Artist a);
    Artist? GetArtist(string uri);
    void UpsertPlaylist(Playlist p);
    Playlist? GetPlaylist(string uri);
    void UpsertShow(Show s);
    Show? GetShow(string uri);
    void UpsertEpisode(Episode e);
    Episode? GetEpisode(string uri);
    // video↔audio associations (the music-video data side; persisted + etag-revalidated). NOT an entity kind — it is
    // keyed by the SAME entity uri as the Track, so it lives in its own side table rather than the entity store.
    void UpsertVideoAssociation(VideoAssociation a);
    VideoAssociation? GetVideoAssociation(string uri);
    // library sets (collections) + per-item sync state (+ the server add timestamp; 0 = unknown → preserve existing)
    void SetSaved(string setId, string uri, bool saved, SyncState sync);
    void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs);
    bool IsSaved(string setId, string uri);
    IReadOnlyList<string> SavedUris(string setId);
    IReadOnlyList<SavedItem> SavedItems(string setId);
    // ordered playlist membership + the rootlist (the queryable lists the catalog joins onto the shared entities at read)
    void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev);
    /// <summary>True when a playlist has a known membership baseline, including a valid empty playlist.</summary>
    bool HasMembership(string playlistUri);
    IReadOnlyList<PlaylistMember> Membership(string playlistUri);
    byte[]? PlaylistRevision(string playlistUri);
    void SetRootlist(IReadOnlyList<RootlistEntry> entries);
    /// <summary>Set the rootlist AND its opaque revision. The 1-arg overload preserves the stored revision (header
    /// hydration must not wipe it); this overload sets it (null clears). See §2.6.</summary>
    void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev);
    byte[]? RootlistRevision();
    IReadOnlyList<RootlistEntry> Rootlist();
    // reactivity
    long Version(string uri);
    void Bump(string uri, CollectionKind? kind = null);
    IObservable<StoreChange> Changes { get; }
    /// <summary>Coalesce a burst of writes (e.g. a 10k-entity metadata sync) into ONE change signal, not one per entity.</summary>
    IDisposable BeginBulk();
}

static class StoreEntityMerge
{
    public static Track Track(Track? current, Track incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Title = NonEmpty(incoming.Title, current.Title),
            Artists = Has(incoming.Artists) ? incoming.Artists : current.Artists,
            Album = MergeAlbumRef(current.Album, incoming.Album),
            DurationMs = incoming.DurationMs > 0 ? incoming.DurationMs : current.DurationMs,
            IsExplicit = incoming.IsExplicit || current.IsExplicit,
            Image = incoming.Image ?? current.Image,
            AddedAt = incoming.AddedAt ?? current.AddedAt,
            AddedBy = incoming.AddedBy ?? current.AddedBy,
            HasVideo = incoming.HasVideo || current.HasVideo,
            PlayCount = incoming.PlayCount > 0 ? incoming.PlayCount : current.PlayCount,
            Origin = incoming.Origin != TrackOrigin.Streamed || current.Origin == TrackOrigin.Streamed ? incoming.Origin : current.Origin,
            Availability = incoming.Availability != Availability.Playable ? incoming.Availability : current.Availability,
            Source = incoming.Source ?? current.Source,
            Isrc = incoming.Isrc ?? current.Isrc,   // keep a known ISRC across a later thin upsert (cluster/library write)
        };
    }

    public static Album Album(Album? current, Album incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),
            Cover = incoming.Cover ?? current.Cover,
            Artists = Has(incoming.Artists) ? incoming.Artists : current.Artists,
            Year = incoming.Year > 0 ? incoming.Year : current.Year,
            TrackCount = incoming.TrackCount > 0 ? incoming.TrackCount : current.TrackCount,
            Tracks = Has(incoming.Tracks) ? incoming.Tracks : current.Tracks,
            MoreByArtist = Has(incoming.MoreByArtist) ? incoming.MoreByArtist : current.MoreByArtist,
            Label = incoming.Label ?? current.Label,
            Copyright = incoming.Copyright ?? current.Copyright,
            ReleaseDate = incoming.ReleaseDate ?? current.ReleaseDate,
            ArtistsDetailed = MergeArtists(current.ArtistsDetailed, incoming.ArtistsDetailed),
            OtherVersions = Has(incoming.OtherVersions) ? incoming.OtherVersions : current.OtherVersions,
            CourtesyLine = incoming.CourtesyLine ?? current.CourtesyLine,
            ReleaseDatePrecision = incoming.ReleaseDatePrecision ?? current.ReleaseDatePrecision,
            DiscCount = incoming.Hydration == AlbumHydrationLevel.Full
                ? Math.Max(1, incoming.DiscCount)
                : Math.Max(current.DiscCount, incoming.DiscCount),
            ShareUrl = incoming.ShareUrl ?? current.ShareUrl,
            IsPreRelease = incoming.Hydration == AlbumHydrationLevel.Full ? incoming.IsPreRelease : current.IsPreRelease,
            PreReleaseEnd = incoming.PreReleaseEnd ?? current.PreReleaseEnd,
            Hydration = incoming.Hydration > current.Hydration ? incoming.Hydration : current.Hydration,
        };
    }

    public static Artist Artist(Artist? current, Artist incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),
            Image = incoming.Image ?? current.Image,
            TopAlbums = MergeAlbumCards(current.TopAlbums, incoming.TopAlbums),
            MonthlyListeners = incoming.MonthlyListeners > 0 ? incoming.MonthlyListeners : current.MonthlyListeners,
            Followers = incoming.Followers > 0 ? incoming.Followers : current.Followers,
            Bio = incoming.Bio ?? current.Bio,
            Verified = incoming.Verified || current.Verified,
            WorldRank = incoming.WorldRank > 0 ? incoming.WorldRank : current.WorldRank,
            HeaderImage = incoming.HeaderImage ?? current.HeaderImage,
            TopTracks = Has(incoming.TopTracks) ? incoming.TopTracks : current.TopTracks,
            AppearsOn = MergeAlbumCards(current.AppearsOn, incoming.AppearsOn),
            Pinned = incoming.Pinned ?? current.Pinned,
            Extras = MergeExtras(current.Extras, incoming.Extras),
            Palette = incoming.Palette ?? current.Palette,   // a thin write (no palette) must not drop a full-overview palette
            // Per-facet discography totals: a thin write (0 = unknown) must not drop a full-overview's real total.
            AlbumsTotal = incoming.AlbumsTotal > 0 ? incoming.AlbumsTotal : current.AlbumsTotal,
            SinglesTotal = incoming.SinglesTotal > 0 ? incoming.SinglesTotal : current.SinglesTotal,
            CompilationsTotal = incoming.CompilationsTotal > 0 ? incoming.CompilationsTotal : current.CompilationsTotal,
            LatestRelease = incoming.LatestRelease ?? current.LatestRelease,
            PopularReleases = Has(incoming.PopularReleases) ? incoming.PopularReleases : current.PopularReleases,
            // Keep the newer freshness stamp: a full-overview write carries UtcNow; a thin write carries default → keeps current.
            FetchedAt = incoming.FetchedAt > current.FetchedAt ? incoming.FetchedAt : current.FetchedAt,
        };
    }

    // Discography merge: the incoming list is the authoritative group order + Kind (a fresh ArtistV4 write), so incoming
    // order wins — but a name-less incoming STUB must never downgrade an already-hydrated card (the "discography flickers
    // empty" bug). Per URI, a stub keeps the prior rich card (adopting only the incoming Kind); a hydrated incoming card
    // upgrades. A GraphQL-stats write passes TopAlbums:null → Has=false → keeps current wholesale.
    static IReadOnlyList<Album>? MergeAlbumCards(IReadOnlyList<Album>? current, IReadOnlyList<Album>? incoming)
    {
        if (!Has(incoming)) return current;
        if (!Has(current)) return incoming;
        var prior = new Dictionary<string, Album>(StringComparer.Ordinal);
        for (int i = 0; i < current!.Count; i++) prior[current[i].Uri] = current[i];
        var merged = new List<Album>(incoming!.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var a = incoming[i];
            merged.Add(a.Name.Length == 0 && prior.TryGetValue(a.Uri, out var rich) ? rich with { Kind = a.Kind } : a);
        }
        return merged;
    }

    static IReadOnlyList<Artist>? MergeArtists(IReadOnlyList<Artist>? current, IReadOnlyList<Artist>? incoming)
    {
        if (!Has(incoming)) return current;
        if (!Has(current)) return incoming;
        var existing = new Dictionary<string, Artist>(StringComparer.Ordinal);
        for (int i = 0; i < current!.Count; i++) existing[current[i].Uri] = current[i];
        var merged = new List<Artist>(incoming!.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var artist = incoming[i];
            existing.TryGetValue(artist.Uri, out var prior);
            merged.Add(Artist(prior, artist));
        }
        return merged;
    }

    static ArtistExtras? MergeExtras(ArtistExtras? current, ArtistExtras? incoming)
    {
        if (incoming is null) return current;
        if (current is null) return incoming;
        return new ArtistExtras(
            Concerts: Has(incoming.Concerts) ? incoming.Concerts : current.Concerts,
            Merch: Has(incoming.Merch) ? incoming.Merch : current.Merch,
            Playlists: Has(incoming.Playlists) ? incoming.Playlists : current.Playlists,
            MusicVideos: Has(incoming.MusicVideos) ? incoming.MusicVideos : current.MusicVideos,
            TopCities: Has(incoming.TopCities) ? incoming.TopCities : current.TopCities,
            ExternalLinks: Has(incoming.ExternalLinks) ? incoming.ExternalLinks : current.ExternalLinks,
            Gallery: Has(incoming.Gallery) ? incoming.Gallery : current.Gallery,
            Related: Has(incoming.Related) ? incoming.Related : current.Related,
            Tour: incoming.Tour ?? current.Tour);
    }

    static AlbumRef MergeAlbumRef(AlbumRef current, AlbumRef incoming) => new(
        NonEmpty(incoming.Id, current.Id),
        NonEmpty(incoming.Uri, current.Uri),
        NonEmpty(incoming.Name, current.Name));

    static bool Has<T>(IReadOnlyList<T>? value) => value is { Count: > 0 };
    static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;
}

public sealed class InMemoryStore : IStore
{
    readonly object _gate = new();
    readonly Dictionary<string, Track> _tracks = new();
    readonly Dictionary<string, Album> _albums = new();
    readonly Dictionary<string, Artist> _artists = new();
    readonly Dictionary<string, Playlist> _playlists = new();
    readonly Dictionary<string, Show> _shows = new();
    readonly Dictionary<string, Episode> _episodes = new();
    readonly Dictionary<string, VideoAssociation> _videoAssoc = new();
    readonly Dictionary<string, long> _versions = new();
    readonly Dictionary<(string set, string uri), (SyncState Sync, long AddedAt)> _saved = new();
    readonly Dictionary<string, HashSet<string>> _savedBySet = new();   // set → uris, so SavedUris is O(set), not O(all-saved)
    readonly Dictionary<string, (IReadOnlyList<PlaylistMember> Rows, byte[]? Rev)> _membership = new();
    IReadOnlyList<RootlistEntry> _rootlist = Array.Empty<RootlistEntry>();
    byte[]? _rootlistRev;
    readonly SimpleSubject<StoreChange> _changes = new();

    // ── entity residency (bounded string floor) ──────────────────────────────────────────────────────────────────────
    // Per-entity LRU stamp: a strict-monotonic Seq (deterministic recency ordering for eviction) + a wall-clock Tick
    // (Environment.TickCount64 ms; the upsert-time backstop uses it to never evict an entity touched in the last window).
    // Touched under _gate on every entity getter HIT and every entity upsert. Only the six real entity kinds are stamped.
    readonly Dictionary<string, (long Seq, long Tick)> _lastUse = new();
    long _useClock;

    /// <summary>True once any entity has been evicted this session. CachedStore gates its cold-fallback reads on this so a
    /// fresh session (hot == cold, nothing evicted) keeps the old zero-disk read semantics; only after the first eviction
    /// can a hot miss be an evicted-but-recoverable entity worth a cold read.</summary>
    public bool HasEvictedEntities { get; private set; }

    // Rough per-entity retained-bytes model for census attribution + the bytes-freed return values (order-of-magnitude,
    // never a hard budget): a Track averages ~0.6 KB of UTF-16 strings + refs; an Album ~0.9 KB; an ACCRETED Artist ~4 KB
    // (bio + discography cards + extras merge in over a session); a Playlist header ~0.4 KB; Show/Episode ~0.5 KB; and each
    // _versions row ~48 B (interned uri key + a long). Deliberately coarse — the census is for attribution, not accounting.
    const int TrackBytes = 640, AlbumBytes = 900, ArtistBytes = 4096, PlaylistBytes = 420, ShowBytes = 512, EpisodeBytes = 512, VersionBytes = 48;
    static readonly int[] s_kindBytes = { TrackBytes, AlbumBytes, ArtistBytes, PlaylistBytes, ShowBytes, EpisodeBytes };

    // Upsert-time backstop: when a week-long session's entity count climbs past BackstopHigh, immediately shed (LRU-only,
    // pin-set-free) down to BackstopLow — but never evicting anything touched within BackstopProtectMs (the live working
    // set). This is the safety net for when the 30 s governor poll never fires eviction (the entity arena is priority 3,
    // shed only under CRITICAL OS pressure). Suppressed during a bulk sync (see MaybeBackstopEvict / EndBulk).
    const int BackstopHigh = 12_000, BackstopLow = 8_000, BackstopCheckStride = 256;
    // The live-working-set guard: the backstop never evicts an entity touched within this window. An instance field (not a
    // const) purely so a test can zero it to exercise the shed path deterministically (production is always 60 s).
    internal long BackstopProtectMs = 60_000;
    // Throttle the (O(n log n)) shed to at most once per BackstopCheckStride upserts past the high-water, so a session that
    // hovers just above 12k with an all-recent (protected) working set doesn't pay a gather+sort on every single upsert.
    int _backstopCheckAt;

    void TouchUse(string uri) => _lastUse[uri] = (++_useClock, Environment.TickCount64);   // caller holds _gate

    int TotalEntitiesLocked() => _tracks.Count + _albums.Count + _artists.Count + _playlists.Count + _shows.Count + _episodes.Count;   // caller holds _gate

    /// <summary>Live entity-dictionary counts (census attribution) — the six queryable kinds plus the version side-table.</summary>
    public (int Tracks, int Albums, int Artists, int Playlists, int Shows, int Episodes, int Versions) EntityCounts
    {
        get { lock (_gate) return (_tracks.Count, _albums.Count, _artists.Count, _playlists.Count, _shows.Count, _episodes.Count, _versions.Count); }
    }

    /// <summary>A coarse estimate of the retained bytes held by the resident entities (the per-kind byte model above ×
    /// counts). Cheap — no deep walk. Attribution only.</summary>
    public long EstimatedEntityBytes { get { lock (_gate) return EstimateBytesLocked(); } }

    long EstimateBytesLocked() =>
        (long)_tracks.Count * TrackBytes + (long)_albums.Count * AlbumBytes + (long)_artists.Count * ArtistBytes
        + (long)_playlists.Count * PlaylistBytes + (long)_shows.Count * ShowBytes + (long)_episodes.Count * EpisodeBytes
        + (long)_versions.Count * VersionBytes;

    /// <summary>Pin up to <paramref name="perSet"/> members of each saved (library) set — the collection-page heads the
    /// entity evictor must not drop. Order is the set's hash order (arbitrary but bounded); it only needs to keep SOME of
    /// each collection resident so its page paints without a cold round-trip.</summary>
    public void CollectSavedHeads(ISet<string> pins, int perSet)
    {
        if (perSet <= 0) return;
        lock (_gate)
            foreach (var kv in _savedBySet)
            {
                int n = 0;
                foreach (var uri in kv.Value) { if (n++ >= perSet) break; pins.Add(uri); }
            }
    }

    /// <summary>Evict oldest-first (by LRU Seq) unpinned entities until the resident entity count drops to
    /// <paramref name="maxResident"/>, returning the estimated bytes freed (the byte model above). A no-op returning 0 when
    /// already at/under the target. Removes the entity from its kind dictionary, its <c>_versions</c> row, and its LRU
    /// stamp; the cold tier still holds it (offline-first), so CachedStore's cold-fallback rehydrates it on next access.</summary>
    public long EvictEntities(ISet<string> pinned, int maxResident) => EvictCore(pinned, maxResident, protectTickFloor: 0);

    // Shared eviction core. protectTickFloor > 0 skips any entity whose last-touch Tick is >= the floor (the backstop's
    // "never evict the live working set" guard); pinned (nullable) skips reachability-pinned entities (the governor arena).
    long EvictCore(ISet<string>? pinned, int target, long protectTickFloor)
    {
        lock (_gate)
        {
            int total = TotalEntitiesLocked();
            if (total <= target) return 0;
            var cands = new List<(string Uri, int Kind, long Seq)>(total);
            GatherCandidatesLocked(cands);
            cands.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));   // oldest (smallest Seq) first
            long freed = 0;
            for (int i = 0; i < cands.Count && total > target; i++)
            {
                var (uri, kind, _) = cands[i];
                if (pinned is not null && pinned.Contains(uri)) continue;
                if (protectTickFloor > 0 && _lastUse.TryGetValue(uri, out var lu) && lu.Tick >= protectTickFloor) continue;
                if (RemoveEntityLocked(uri, kind)) { freed += s_kindBytes[kind]; total--; }
            }
            if (freed > 0) HasEvictedEntities = true;
            return freed;
        }
    }

    void GatherCandidatesLocked(List<(string Uri, int Kind, long Seq)> cands)
    {
        foreach (var k in _tracks.Keys) cands.Add((k, 0, SeqOf(k)));
        foreach (var k in _albums.Keys) cands.Add((k, 1, SeqOf(k)));
        foreach (var k in _artists.Keys) cands.Add((k, 2, SeqOf(k)));
        foreach (var k in _playlists.Keys) cands.Add((k, 3, SeqOf(k)));
        foreach (var k in _shows.Keys) cands.Add((k, 4, SeqOf(k)));
        foreach (var k in _episodes.Keys) cands.Add((k, 5, SeqOf(k)));
        long SeqOf(string uri) => _lastUse.TryGetValue(uri, out var v) ? v.Seq : 0;   // never-stamped ⇒ oldest
    }

    bool RemoveEntityLocked(string uri, int kind)
    {
        bool removed = kind switch
        {
            0 => _tracks.Remove(uri), 1 => _albums.Remove(uri), 2 => _artists.Remove(uri),
            3 => _playlists.Remove(uri), 4 => _shows.Remove(uri), 5 => _episodes.Remove(uri), _ => false,
        };
        if (removed) { _lastUse.Remove(uri); _versions.Remove(uri); }
        return removed;
    }

    // Called after every entity upsert (and once at the close of a bulk scope). Cheap on the common path (a count read
    // under the lock); only when past BackstopHigh AND outside a bulk does it run the actual LRU shed. Suppressed during a
    // bulk sync so a 10k-entity load doesn't trigger an O(n log n) shed per upsert (and doesn't evict the just-loaded set).
    void MaybeBackstopEvict()
    {
        lock (_gate)
        {
            if (_bulkDepth != 0) return;   // a bulk sync closes with one backstop pass (EndBulk), never per-upsert
            int total = TotalEntitiesLocked();
            if (total <= BackstopHigh) { _backstopCheckAt = 0; return; }   // back under the line → re-arm the throttle
            if (total < _backstopCheckAt) return;                          // already checked at this level; wait for growth
            _backstopCheckAt = total + BackstopCheckStride;                // next attempt only after another stride of upserts
        }
        long floor = BackstopProtectMs > 0 ? Environment.TickCount64 - BackstopProtectMs : 0;   // 0 ⇒ guard disabled
        EvictCore(pinned: null, target: BackstopLow, protectTickFloor: floor);
    }

    public IObservable<StoreChange> Changes => _changes;

    public void UpsertTrack(Track t)
    {
        lock (_gate)
        {
            _tracks.TryGetValue(t.Uri, out var current);
            _tracks[t.Uri] = StoreEntityMerge.Track(current, t);
            TouchUse(t.Uri);
        }
        Bump(t.Uri);
        MaybeBackstopEvict();
    }

    public Track? GetTrack(string uri)
    {
        lock (_gate) { if (_tracks.TryGetValue(uri, out var t)) { TouchUse(uri); return t; } return null; }
    }

    public IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200)
    {
        if (limit <= 0) return Array.Empty<Track>();   // guard: a non-positive limit is an empty result, not an exception
        bool hasText = !string.IsNullOrEmpty(text);

        // Fast path (the default): no sort → stream the table, filter inline, stop at `limit`. No whole-table copy, no sort —
        // a default limit=200 query over 100k tracks touches ~200 rows instead of materializing+sorting 100k.
        if (sort == TrackSort.None)
        {
            var picked = new List<Track>(Math.Min(limit, 256));
            lock (_gate)
                foreach (var t in _tracks.Values)
                {
                    if (hasText && !MatchesText(t, text!)) continue;
                    picked.Add(t);
                    if (picked.Count >= limit) break;
                }
            return picked;
        }

        // Sorted path: gather matches under the lock, sort OUTSIDE it. (The indexed SqliteStore swap does this in SQL.)
        List<Track> rows;
        lock (_gate)
        {
            rows = new List<Track>(_tracks.Count);
            foreach (var t in _tracks.Values)
                if (!hasText || MatchesText(t, text!)) rows.Add(t);
        }
        rows = sort switch
        {
            TrackSort.Title => rows.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            TrackSort.Artist => rows.OrderBy(t => t.Artists.Count > 0 ? t.Artists[0].Name : "", StringComparer.OrdinalIgnoreCase).ToList(),
            TrackSort.DurationAsc => rows.OrderBy(t => t.DurationMs).ToList(),
            _ => rows,
        };
        return rows.Count > limit ? rows.GetRange(0, limit) : rows;
    }

    static bool MatchesText(Track t, string text)
    {
        if (t.Title.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        var artists = t.Artists;
        for (int i = 0; i < artists.Count; i++)   // manual loop — no .Any() closure per row
            if (artists[i].Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void UpsertAlbum(Album a)
    {
        lock (_gate)
        {
            _albums.TryGetValue(a.Uri, out var current);
            _albums[a.Uri] = StoreEntityMerge.Album(current, a);
            TouchUse(a.Uri);
        }
        Bump(a.Uri);
        MaybeBackstopEvict();
    }
    public Album? GetAlbum(string uri) { lock (_gate) { if (_albums.TryGetValue(uri, out var a)) { TouchUse(uri); return a; } return null; } }
    public void UpsertArtist(Artist a)
    {
        lock (_gate)
        {
            _artists.TryGetValue(a.Uri, out var current);
            _artists[a.Uri] = StoreEntityMerge.Artist(current, a);
            TouchUse(a.Uri);
        }
        Bump(a.Uri);
        MaybeBackstopEvict();
    }
    public Artist? GetArtist(string uri) { lock (_gate) { if (_artists.TryGetValue(uri, out var a)) { TouchUse(uri); return a; } return null; } }
    public void UpsertPlaylist(Playlist p) { lock (_gate) { _playlists[p.Uri] = p; TouchUse(p.Uri); } Bump(p.Uri); MaybeBackstopEvict(); }
    public Playlist? GetPlaylist(string uri) { lock (_gate) { if (_playlists.TryGetValue(uri, out var p)) { TouchUse(uri); return p; } return null; } }
    public void UpsertShow(Show s) { lock (_gate) { _shows[s.Uri] = s; TouchUse(s.Uri); } Bump(s.Uri); MaybeBackstopEvict(); }
    public Show? GetShow(string uri) { lock (_gate) { if (_shows.TryGetValue(uri, out var s)) { TouchUse(uri); return s; } return null; } }
    public void UpsertEpisode(Episode e) { lock (_gate) { _episodes[e.Uri] = e; TouchUse(e.Uri); } Bump(e.Uri); MaybeBackstopEvict(); }
    public Episode? GetEpisode(string uri) { lock (_gate) { if (_episodes.TryGetValue(uri, out var e)) { TouchUse(uri); return e; } return null; } }

    // A full replace (each fetch yields the complete association; a 304 keeps the prior record with a bumped FetchedAt,
    // handled by the caller). No Bump — this is side-table data; the rendered has-video signal is Track.HasVideo.
    public void UpsertVideoAssociation(VideoAssociation a) { lock (_gate) _videoAssoc[a.Uri] = a; }
    public VideoAssociation? GetVideoAssociation(string uri) { lock (_gate) return _videoAssoc.TryGetValue(uri, out var a) ? a : null; }

    public void SetSaved(string setId, string uri, bool saved, SyncState sync) => SetSavedCore(setId, uri, saved, sync, 0);
    public void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs) => SetSavedCore(setId, uri, saved, sync, addedAtMs);

    /// <summary>The SetSaved core with no-op elision (§7.4): returns whether the write actually changed the store. A save
    /// that repeats the SAME (set,uri,SyncState) — or an unsave of an already-absent (set,uri) — writes nothing and does
    /// NOT Bump/emit, turning every idempotent echo/delta-overlap into literal silence. A same-key write with a DIFFERENT
    /// SyncState (Pending→Confirmed) still writes + bumps. <paramref name="addedAtMs"/> 0 preserves the existing add
    /// timestamp; a non-zero refinement of an otherwise-identical row updates the timestamp silently (metadata, not a
    /// state change). CachedStore calls this so it can skip the cold dual-write on a pure no-op too. The change decision
    /// is made under _gate; the Bump (emit) fires outside it (the cardinal rule).</summary>
    internal bool SetSavedCore(string setId, string uri, bool saved, SyncState sync, long addedAtMs)
    {
        bool changed;
        lock (_gate)
        {
            bool present = _saved.TryGetValue((setId, uri), out var cur);
            if (saved)
            {
                changed = !present || cur.Sync != sync;   // new, or a state transition (e.g. Pending→Confirmed)
                long at = addedAtMs != 0 ? addedAtMs : (present ? cur.AddedAt : 0);
                if (changed || (present && at != cur.AddedAt))
                {
                    _saved[(setId, uri)] = (sync, at);
                    if (!_savedBySet.TryGetValue(setId, out var set)) _savedBySet[setId] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(uri);
                }
            }
            else
            {
                changed = present;                    // no-op when already absent
                if (changed)
                {
                    _saved.Remove((setId, uri));
                    if (_savedBySet.TryGetValue(setId, out var set)) set.Remove(uri);
                }
            }
        }
        if (changed) Bump(uri, KindForSet(setId));
        return changed;
    }

    public bool IsSaved(string setId, string uri)
    {
        lock (_gate) return _saved.ContainsKey((setId, uri));
    }

    public IReadOnlyList<string> SavedUris(string setId)
    {
        lock (_gate) return _savedBySet.TryGetValue(setId, out var set) ? new List<string>(set) : new List<string>();
    }

    public IReadOnlyList<SavedItem> SavedItems(string setId)
    {
        lock (_gate)
        {
            if (!_savedBySet.TryGetValue(setId, out var set)) return Array.Empty<SavedItem>();
            var list = new List<SavedItem>(set.Count);
            foreach (var uri in set)
                list.Add(new SavedItem(uri, _saved.TryGetValue((setId, uri), out var v) ? v.AddedAt : 0));
            return list;
        }
    }

    public void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev)
    {
        lock (_gate) _membership[playlistUri] = (rows, baseRev);
        Bump(playlistUri);
    }

    public IReadOnlyList<PlaylistMember> Membership(string playlistUri)
    {
        lock (_gate) return _membership.TryGetValue(playlistUri, out var m) ? m.Rows : Array.Empty<PlaylistMember>();
    }

    public bool HasMembership(string playlistUri)
    {
        lock (_gate) return _membership.ContainsKey(playlistUri);
    }

    /// <summary>Drop a resident membership baseline (the WARM-tier evictor calls this); the cold tier keeps it, so the
    /// next access rehydrates it.</summary>
    public void EvictMembership(string playlistUri) { lock (_gate) _membership.Remove(playlistUri); }
    public int ResidentMembershipCount { get { lock (_gate) return _membership.Count; } }

    public byte[]? PlaylistRevision(string playlistUri)
    {
        lock (_gate) return _membership.TryGetValue(playlistUri, out var m) ? m.Rev : null;
    }

    // 1-arg: PRESERVE the stored revision (header hydration re-writes the rootlist rows without touching the rev).
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries)
    {
        lock (_gate) _rootlist = entries;
        Bump("rootlist");
    }

    // 2-arg: set the rootlist AND its revision (null clears).
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev)
    {
        lock (_gate) { _rootlist = entries; _rootlistRev = rev; }
        Bump("rootlist");
    }

    public byte[]? RootlistRevision() { lock (_gate) return _rootlistRev; }

    public IReadOnlyList<RootlistEntry> Rootlist()
    {
        lock (_gate) return _rootlist;
    }

    public long Version(string uri)
    {
        lock (_gate) return _versions.TryGetValue(uri, out var v) ? v : 0;
    }

    public void Bump(string uri, CollectionKind? kind = null)
    {
        bool suppressed;
        lock (_gate) { _versions[uri] = _versions.TryGetValue(uri, out var v) ? v + 1 : 1; suppressed = _bulkDepth > 0; }
        if (!suppressed) _changes.OnNext(new StoreChange(uri, Kind: kind));   // during a bulk the per-uri signals are coalesced
    }

    static CollectionKind? KindForSet(string setId) => setId switch
    {
        "albums" => CollectionKind.Albums,
        "artists" => CollectionKind.Artists,
        "shows" or "episodes" => CollectionKind.Shows,
        "playlists" => CollectionKind.Playlists,
        "liked" => CollectionKind.Liked,
        _ => null,
    };

    int _bulkDepth;

    /// <summary>Opens a bulk scope: per-URI change signals are suppressed until the outermost scope closes, then ONE
    /// StoreChange.Bulk fires (subscribers full-recompute). NOTE — suppression is store-wide: a concurrent unrelated write
    /// (e.g. a user save) during a bulk sync is also folded into that single Bulk signal rather than emitting its own
    /// per-URI change. Correct (the Bulk recompute covers it), just coarser; acceptable since bulk syncs are short.</summary>
    public IDisposable BeginBulk()
    {
        lock (_gate) _bulkDepth++;
        return new BulkScope(this);
    }

    void EndBulk()
    {
        bool fire;
        lock (_gate) fire = --_bulkDepth == 0;
        if (fire) { _changes.OnNext(StoreChange.Bulk); MaybeBackstopEvict(); }   // one signal for the whole bulk, then the suppressed backstop
    }

    sealed class BulkScope(InMemoryStore store) : IDisposable
    {
        bool _done;
        public void Dispose() { if (_done) return; _done = true; store.EndBulk(); }
    }
}
