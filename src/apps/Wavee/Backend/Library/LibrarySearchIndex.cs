using System;
using System.Collections.Generic;
using Wavee.Backend.Persistence;
using Wavee.Core;

namespace Wavee.Backend.Library;

// ── Offline library full-text search ─────────────────────────────────────────────────────────────────────────────────
// A cache-only, HIERARCHICAL search over the user's library — NO network, NO on-demand hydration (per-keystroke fetches
// would starve the UI). It produces a drill-down tree the library page fans across its master-detail columns:
//   Artists scope → artist ▸ matching albums ▸ matching tracks. An artist is INCLUDED when its name matches OR it owns a
//     matching album/track (so e.g. Jukjae surfaces because his album matched, even though "jukjae" wasn't typed).
//   Albums scope  → matching album ▸ matching tracks.
// Inclusion cascades: if the artist name matched, ALL its albums show (browse the artist); if an album name (or its
// artist) matched, ALL its tracks show; otherwise only the entities whose own name/title matched. Matched names/titles
// carry a highlight span; entities present only because a child matched carry MatchLen == 0 (no highlight).
//
// THE CORPUS (Addendum A4 / locked decision 7). The hot tier is a bounded cache over cold now, so walking residency is
// no longer a correct corpus. Two sources are UNIONED per level:
//   • the COLD candidates — thin `entity` rows (uri/title/subtitle/image_url/album_uri, no payload) joined out of
//     collection_items + entity_refs by SqliteColdStore.LoadLibraryCandidates. This is what makes a saved entity that is
//     on disk but NOT resident findable, and it is what supplies album TRACKS at all (a persisted album blob carries no
//     tracklist — PersistAlbum strips it).
//   • the RESIDENT records (store.GetArtist / store.GetAlbum, both of which cold-fall-back). They take PRECEDENCE: a hot
//     record is at worst as fresh as its cold row and may be fresher (the cold write-behind lane is asynchronous), and
//     it carries the fields the thin columns cannot (Year, AlbumKind, an ordered tracklist).
// MATCHING IS ALWAYS C#-SIDE `IndexOf(q, OrdinalIgnoreCase)` — never SQL. The UI needs the match OFFSET, not a boolean,
// and SQLite `NOCASE` folds ASCII only, so any `LIKE` pre-pass would be a strict SUBSET (it would drop Ω/ω, İ, ı …).
// There is therefore no pre-filter at all: SQL bounds the SCOPE, C# decides the MATCH.
//
// Runs off the UI thread (StoreLibrarySource wraps it in Task.Run); Store reads are lock-safe and the cold reads ride the
// dedicated read-only SQLite connection.
public static class LibrarySearchIndex
{
    const int ArtistCap = 200;   // matched-artist ceiling (bounded by the followed set anyway)

    public static LibrarySearchResults Run(IStore store, LibrarySearchScope scope, string query)
        => Run(store, scope, query, store as ILibraryCandidateStore);

    /// <summary>The testable seam: pass <paramref name="candidates"/> = null to search the RESIDENT graph only (the
    /// pre-SQL path, still the whole story for a fake/offline backend or a plain <c>InMemoryStore</c>).</summary>
    public static LibrarySearchResults Run(IStore store, LibrarySearchScope scope, string query, ILibraryCandidateStore? candidates)
    {
        var q = query.Trim();
        if (q.Length == 0) return LibrarySearchResults.Empty;
        var cold = Load(candidates, scope);
        var corpus = new ColdIndex(cold);

        if (scope == LibrarySearchScope.Artists)
        {
            var artists = new List<Ranked<LibraryArtistGroup>>();
            foreach (var artistUri in Union(store.SavedUris("artists"), cold.Roots))
            {
                var artist = store.GetArtist(artistUri);
                var row = corpus.Root(artistUri);
                var name = artist?.Name is { Length: > 0 } n ? n : row?.Title ?? "";
                if (artist is null && name.Length == 0) continue;   // neither resident nor on disk → nothing to match
                var aSpan = Match(name, q);

                var albums = new List<Ranked<LibraryAlbumGroup>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // The resident/overview cards first (they carry Year + AlbumKind), then any album the cold refs closure
                // knows about that the overview didn't list — the two together are the artist's offline discography.
                foreach (var card in artist?.TopAlbums ?? Array.Empty<Album>())
                    AddAlbum(card.Uri, card);
                foreach (var albumUri in corpus.AlbumsOf(artistUri))
                    AddAlbum(albumUri, null);

                if (aSpan is null && albums.Count == 0) continue;   // no match anywhere under this artist
                var sortedAlbums = Sort(albums);
                // Why the artist surfaced when its OWN name didn't match: attribute the child that pulled it in (a
                // name-matched album, else a title-matched track). One of these always exists here (we only keep a
                // name-unmatched artist when albums.Count > 0), so the "why" caption is certain — never a guess.
                var reason = aSpan is not null ? MatchReason.None : ArtistReason(sortedAlbums);
                artists.Add(new Ranked<LibraryArtistGroup>(
                    new LibraryArtistGroup(artistUri, name, artist?.Image ?? ImageOf(row?.ImageUrl),
                        aSpan?.Start ?? -1, aSpan?.Len ?? 0, sortedAlbums, reason),
                    aSpan is not null ? 0 : 1, 0, name));

                void AddAlbum(string uri, Album? card)
                {
                    if (uri.Length == 0 || !seen.Add(uri)) return;
                    var album = store.GetAlbum(uri) ?? card ?? corpus.AlbumStub(uri);
                    if (album is null) return;
                    if (BuildAlbum(album, corpus.TracksOf(album), aSpan is not null, q) is { } g)
                        albums.Add(new Ranked<LibraryAlbumGroup>(g, g.MatchLen > 0 ? 0 : 1, -album.Year));
                }
            }
            return new LibrarySearchResults(SortArtists(artists), Array.Empty<LibraryAlbumGroup>());
        }
        else // Albums scope
        {
            var albums = new List<Ranked<LibraryAlbumGroup>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var albumUri in Union(store.SavedUris("albums"), cold.Albums))
            {
                if (!seen.Add(albumUri)) continue;
                var album = store.GetAlbum(albumUri) ?? corpus.AlbumStub(albumUri);
                if (album is null) continue;
                if (BuildAlbum(album, corpus.TracksOf(album), false, q) is { } g)
                    albums.Add(new Ranked<LibraryAlbumGroup>(g, g.MatchLen > 0 ? 0 : 1, -album.Year));
            }
            return new LibrarySearchResults(Array.Empty<LibraryArtistGroup>(), Sort(albums));
        }
    }

    // A cold read must never break search: an I/O failure degrades to the resident-graph walk, which is exactly the
    // pre-SQL behavior.
    static ColdCandidates Load(ILibraryCandidateStore? candidates, LibrarySearchScope scope)
    {
        if (candidates is null) return ColdCandidates.Empty;
        try
        {
            return candidates.LoadLibraryCandidates(
                scope == LibrarySearchScope.Artists ? ColdCandidateScope.SavedArtists : ColdCandidateScope.SavedAlbums)
                ?? ColdCandidates.Empty;
        }
        catch (Exception) { return ColdCandidates.Empty; }
    }

    // The saved set (the identity tier — always resident, and the ORDER the pre-SQL walk used) first, then any cold row
    // the saved set didn't name. De-duplicated, order-stable: ranking is what decides the output order, but a stable
    // input keeps equal-ranked ties deterministic.
    static List<string> Union(IReadOnlyList<string> savedUris, IReadOnlyList<ColdThinRow> rows)
    {
        var list = new List<string>(savedUris.Count + rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < savedUris.Count; i++) if (savedUris[i].Length > 0 && seen.Add(savedUris[i])) list.Add(savedUris[i]);
        for (int i = 0; i < rows.Count; i++) if (rows[i].Uri.Length > 0 && seen.Add(rows[i].Uri)) list.Add(rows[i].Uri);
        return list;
    }

    // ── the cold corpus, indexed ─────────────────────────────────────────────────────────────────────────────────────
    sealed class ColdIndex
    {
        readonly Dictionary<string, ColdThinRow> _roots = new(StringComparer.Ordinal);
        readonly Dictionary<string, ColdThinRow> _albums = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<ColdThinRow>> _tracksByAlbum = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> _albumsByArtist = new(StringComparer.Ordinal);

        public ColdIndex(ColdCandidates c)
        {
            for (int i = 0; i < c.Roots.Count; i++) _roots[c.Roots[i].Uri] = c.Roots[i];
            for (int i = 0; i < c.Albums.Count; i++) _albums[c.Albums[i].Uri] = c.Albums[i];
            for (int i = 0; i < c.Tracks.Count; i++)
            {
                var t = c.Tracks[i];
                if (t.AlbumUri is not { Length: > 0 } album) continue;
                if (!_tracksByAlbum.TryGetValue(album, out var list)) _tracksByAlbum[album] = list = new List<ColdThinRow>(16);
                list.Add(t);
            }
            for (int i = 0; i < c.Edges.Count; i++)
            {
                var e = c.Edges[i];
                if (!_albums.ContainsKey(e.ChildUri)) continue;   // the edge table also carries track→artist edges
                if (!_albumsByArtist.TryGetValue(e.ParentUri, out var list)) _albumsByArtist[e.ParentUri] = list = new List<string>(16);
                list.Add(e.ChildUri);
            }
        }

        public ColdThinRow? Root(string uri) => _roots.TryGetValue(uri, out var r) ? r : null;
        public IReadOnlyList<string> AlbumsOf(string artistUri)
            => _albumsByArtist.TryGetValue(artistUri, out var list) ? list : Array.Empty<string>();

        /// <summary>A minimal Album for a row that exists on disk but nowhere in memory and in no overview: name + cover
        /// only. Year/Kind are unknowable from the thin columns, so it ranks as year 0 / AlbumKind.Album.</summary>
        public Album? AlbumStub(string uri)
            => _albums.TryGetValue(uri, out var r)
                ? new Album("", uri, r.Title ?? "", ImageOf(r.ImageUrl), Array.Empty<ArtistRef>(), 0, 0)
                : null;

        /// <summary>The album's tracklist for search: the RESIDENT list first (ordered, and its index is the playback
        /// index the row click uses), then every cold track row the resident list didn't carry. A cold-only album has no
        /// persisted track ORDER — its blob never stored one — so those rows keep the SQL's `uri` order and are indexed
        /// after the resident ones.</summary>
        public List<TrackCand> TracksOf(Album album)
        {
            var tl = album.Tracks;
            var cold = _tracksByAlbum.TryGetValue(album.Uri, out var rows) ? rows : null;
            var list = new List<TrackCand>((tl?.Count ?? 0) + (cold?.Count ?? 0));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (tl is not null)
                for (int i = 0; i < tl.Count; i++)
                {
                    var t = tl[i];
                    if (t.Title.Length == 0 || !seen.Add(t.Uri)) continue;
                    list.Add(new TrackCand(t.Uri, t.Title, t.Image ?? album.Cover, i));
                }
            if (cold is not null)
            {
                int next = tl?.Count ?? 0;
                for (int i = 0; i < cold.Count; i++)
                {
                    var r = cold[i];
                    if (r.Title is not { Length: > 0 } title || r.Uri.Length == 0 || !seen.Add(r.Uri)) continue;
                    list.Add(new TrackCand(r.Uri, title, ImageOf(r.ImageUrl) ?? album.Cover, next++));
                }
            }
            return list;
        }
    }

    /// <summary>One searchable track: the thin fields plus its playback index inside the album.</summary>
    readonly record struct TrackCand(string Uri, string Title, Image? Cover, int Index);

    static Image? ImageOf(string? url) => string.IsNullOrEmpty(url) ? null : new Image(url);

    // Build an album group if it should be included: the album name matched, or its artist matched (parentMatched), or
    // it has ≥1 matching track. Tracks shown = ALL when the album/artist matched, else only the title-matching ones.
    static LibraryAlbumGroup? BuildAlbum(Album album, List<TrackCand> candidates, bool parentMatched, string q)
    {
        var alSpan = Match(album.Name, q);
        bool albumMatched = alSpan is not null || parentMatched;

        var tracks = new List<LibraryTrackHit>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var t = candidates[i];
            var tSpan = Match(t.Title, q);
            if (albumMatched)
                tracks.Add(new LibraryTrackHit(t.Uri, t.Title, t.Cover, t.Index, tSpan?.Start ?? -1, tSpan?.Len ?? 0));
            else if (tSpan is not null)
                tracks.Add(new LibraryTrackHit(t.Uri, t.Title, t.Cover, t.Index, tSpan.Value.Start, tSpan.Value.Len));
        }

        if (!albumMatched && tracks.Count == 0) return null;
        // The album's own "why": its name matched → None (the highlight explains it). Else, when it was NOT pulled in by
        // a matching parent artist (i.e. it stands as a top-level album result), it is here through a title-matched
        // track → attribute that track. Under a matched artist (parentMatched) it is browse context → no reason.
        var reason = MatchReason.None;
        if (alSpan is null && !parentMatched)
            foreach (var th in tracks)
                if (th.MatchLen > 0) { reason = new MatchReason(LibraryMatchKind.Track, th.Title); break; }
        return new LibraryAlbumGroup(album.Uri, album.Name, album.Cover, album.Year, album.Kind, alSpan?.Start ?? -1, alSpan?.Len ?? 0, tracks, reason);
    }

    // The reason a name-unmatched artist surfaced: prefer a child album whose NAME matched (the most specific, useful
    // attribution), else the first child track whose TITLE matched. Returns None only if neither exists (defensive — the
    // caller guarantees at least one child matched), so a hit is never captioned with a fabricated reason.
    static MatchReason ArtistReason(IReadOnlyList<LibraryAlbumGroup> albums)
    {
        foreach (var a in albums)
            if (a.MatchLen > 0) return new MatchReason(LibraryMatchKind.Album, a.Name);
        foreach (var a in albums)
            foreach (var t in a.Tracks)
                if (t.MatchLen > 0) return new MatchReason(LibraryMatchKind.Track, t.Title);
        return MatchReason.None;
    }

    readonly record struct MatchSpan(int Start, int Len);

    static MatchSpan? Match(string text, string q)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : new MatchSpan(i, q.Length);
    }

    // Rank tuple: primary (name-match beats child-only), secondary (e.g. year desc), then a name tiebreak.
    readonly record struct Ranked<T>(T Value, int Primary, int Secondary, string Name = "");

    static IReadOnlyList<LibraryAlbumGroup> Sort(List<Ranked<LibraryAlbumGroup>> list)
    {
        list.Sort(Compare);
        var arr = new LibraryAlbumGroup[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = list[i].Value;
        return arr;
    }

    static IReadOnlyList<LibraryArtistGroup> SortArtists(List<Ranked<LibraryArtistGroup>> list)
    {
        list.Sort(Compare);
        int n = Math.Min(list.Count, ArtistCap);
        var arr = new LibraryArtistGroup[n];
        for (int i = 0; i < n; i++) arr[i] = list[i].Value;
        return arr;
    }

    static int Compare<T>(Ranked<T> a, Ranked<T> b)
    {
        if (a.Primary != b.Primary) return a.Primary.CompareTo(b.Primary);
        if (a.Secondary != b.Secondary) return a.Secondary.CompareTo(b.Secondary);
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
