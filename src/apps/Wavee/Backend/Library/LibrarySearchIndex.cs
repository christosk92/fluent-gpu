using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Library;

// ── Offline library full-text search ─────────────────────────────────────────────────────────────────────────────────
// A cache-only, HIERARCHICAL scan over the resident Store — NO network, NO on-demand hydration (per-keystroke fetches
// would starve the UI). It produces a drill-down tree the library page fans across its master-detail columns:
//   Artists scope → artist ▸ matching albums ▸ matching tracks. An artist is INCLUDED when its name matches OR it owns a
//     matching album/track (so e.g. Jukjae surfaces because his album matched, even though "jukjae" wasn't typed).
//   Albums scope  → matching album ▸ matching tracks.
// Inclusion cascades: if the artist name matched, ALL its albums show (browse the artist); if an album name (or its
// artist) matched, ALL its tracks show; otherwise only the entities whose own name/title matched. Matched names/titles
// carry a highlight span; entities present only because a child matched carry MatchLen == 0 (no highlight).
// Track coverage is best-effort over resident tracklists (DiscographyPrefetcher fills these at sign-in). Runs off the UI
// thread (StoreLibrarySource wraps it in Task.Run); Store reads are lock-safe.
public static class LibrarySearchIndex
{
    const int ArtistCap = 200;   // matched-artist ceiling (bounded by the followed set anyway)

    public static LibrarySearchResults Run(IStore store, LibrarySearchScope scope, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return LibrarySearchResults.Empty;

        if (scope == LibrarySearchScope.Artists)
        {
            var artists = new List<Ranked<LibraryArtistGroup>>();
            foreach (var artistUri in store.SavedUris("artists"))
            {
                var artist = store.GetArtist(artistUri);
                if (artist is null) continue;
                var aSpan = Match(artist.Name, q);

                var albums = new List<Ranked<LibraryAlbumGroup>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var card in artist.TopAlbums ?? Array.Empty<Album>())
                {
                    if (!seen.Add(card.Uri)) continue;
                    var album = store.GetAlbum(card.Uri) ?? card;
                    if (BuildAlbum(album, aSpan is not null, q) is { } g)
                        albums.Add(new Ranked<LibraryAlbumGroup>(g, g.MatchLen > 0 ? 0 : 1, -album.Year));
                }

                if (aSpan is null && albums.Count == 0) continue;   // no match anywhere under this artist
                var sortedAlbums = Sort(albums);
                // Why the artist surfaced when its OWN name didn't match: attribute the child that pulled it in (a
                // name-matched album, else a title-matched track). One of these always exists here (we only keep a
                // name-unmatched artist when albums.Count > 0), so the "why" caption is certain — never a guess.
                var reason = aSpan is not null ? MatchReason.None : ArtistReason(sortedAlbums);
                artists.Add(new Ranked<LibraryArtistGroup>(
                    new LibraryArtistGroup(artist.Uri, artist.Name, artist.Image, aSpan?.Start ?? -1, aSpan?.Len ?? 0, sortedAlbums, reason),
                    aSpan is not null ? 0 : 1, 0, artist.Name));
            }
            return new LibrarySearchResults(SortArtists(artists), Array.Empty<LibraryAlbumGroup>());
        }
        else // Albums scope
        {
            var albums = new List<Ranked<LibraryAlbumGroup>>();
            foreach (var albumUri in store.SavedUris("albums"))
            {
                var album = store.GetAlbum(albumUri);
                if (album is null) continue;
                if (BuildAlbum(album, false, q) is { } g)
                    albums.Add(new Ranked<LibraryAlbumGroup>(g, g.MatchLen > 0 ? 0 : 1, -album.Year));
            }
            return new LibrarySearchResults(Array.Empty<LibraryArtistGroup>(), Sort(albums));
        }
    }

    // Build an album group if it should be included: the album name matched, or its artist matched (parentMatched), or
    // it has ≥1 matching track. Tracks shown = ALL when the album/artist matched, else only the title-matching ones.
    static LibraryAlbumGroup? BuildAlbum(Album album, bool parentMatched, string q)
    {
        var alSpan = Match(album.Name, q);
        bool albumMatched = alSpan is not null || parentMatched;

        var tracks = new List<LibraryTrackHit>();
        var tl = album.Tracks;
        if (tl is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tl.Count; i++)
            {
                var t = tl[i];
                if (t.Title.Length == 0 || !seen.Add(t.Uri)) continue;
                var tSpan = Match(t.Title, q);
                if (albumMatched)
                    tracks.Add(new LibraryTrackHit(t.Uri, t.Title, t.Image ?? album.Cover, i, tSpan?.Start ?? -1, tSpan?.Len ?? 0));
                else if (tSpan is not null)
                    tracks.Add(new LibraryTrackHit(t.Uri, t.Title, t.Image ?? album.Cover, i, tSpan.Value.Start, tSpan.Value.Len));
            }
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
