using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Persistence;

// ── the artist thin split (locked decision 17 + Addendum A3) ──────────────────────────────────────────────────────────
// The HOT tier keeps FULL Artist records — nothing here changes what a page reads from memory. At COLD-PERSIST time the
// record is cut in two:
//
//   • the CORE  → `entity` (kind=Artist): Id/Uri/Name/Image/HeaderImage/Palette/Verified/MonthlyListeners/Followers +
//                 the SWR stamp (FetchedAt). ~1 KB instead of the up-to-370 KB accreted document.
//   • the FACETS→ `artist_overview`: TopAlbums/AppearsOn/PopularReleases/LatestRelease as (uri,kind,name,year,cover_url)
//                 REFS, TopTracks as uris, plus Pinned/Bio/Extras and the per-facet totals + WorldRank ("stats").
//
// The split is loss-tolerant by construction: album refs re-fatten from the STANDALONE album rows (ArtistDiscography.
// Assemble already knows how), and anything genuinely missing simply degrades to the stub the overview stored.
public sealed record ArtistAlbumStub(string Uri, int Kind, string Name, int Year, string? CoverUrl);

/// <summary>The persisted `artist_overview` document — the fat facets stripped off the Artist core.</summary>
public sealed record ArtistOverviewDoc(
    IReadOnlyList<ArtistAlbumStub>? TopAlbums = null,
    IReadOnlyList<ArtistAlbumStub>? AppearsOn = null,
    IReadOnlyList<ArtistAlbumStub>? PopularReleases = null,
    ArtistAlbumStub? LatestRelease = null,
    IReadOnlyList<string>? TopTracks = null,
    PinnedItem? Pinned = null,
    string? Bio = null,
    ArtistExtras? Extras = null,
    int AlbumsTotal = 0, int SinglesTotal = 0, int CompilationsTotal = 0, int WorldRank = 0);

public static class ArtistSplit
{
    /// <summary>AppearsOn is capped AT PROJECTION (design §D.1) — the UI hydrates 20, the page can page beyond, and an
    /// uncapped appears-on set is the single biggest artist-row bloater.</summary>
    public const int AppearsOnCap = 100;

    /// <summary>The persisted core: every fat facet nulled. Never applied to the hot record.</summary>
    public static Artist Core(Artist a) => a with
    {
        TopAlbums = null,
        AppearsOn = null,
        PopularReleases = null,
        LatestRelease = null,
        Pinned = null,
        TopTracks = null,
        Bio = null,
        Extras = null,
        AlbumsTotal = 0,
        SinglesTotal = 0,
        CompilationsTotal = 0,
        WorldRank = 0,
    };

    /// <summary>Project the fat facets of a MERGED hot artist into the overview document.</summary>
    public static ArtistOverviewDoc Project(Artist a) => new(
        TopAlbums: Stubs(a.TopAlbums, int.MaxValue),
        AppearsOn: Stubs(a.AppearsOn, AppearsOnCap),
        PopularReleases: Stubs(a.PopularReleases, int.MaxValue),
        LatestRelease: Stub(a.LatestRelease),
        TopTracks: TrackUris(a.TopTracks),
        Pinned: a.Pinned,
        Bio: a.Bio,
        Extras: a.Extras,
        AlbumsTotal: a.AlbumsTotal,
        SinglesTotal: a.SinglesTotal,
        CompilationsTotal: a.CompilationsTotal,
        WorldRank: a.WorldRank);

    /// <summary>True when the document carries anything at all — an empty projection must never be written (it would
    /// clobber a stored overview when a THIN artist write lands before the record has been re-fattened).</summary>
    public static bool HasContent(ArtistOverviewDoc d) =>
        Has(d.TopAlbums) || Has(d.AppearsOn) || Has(d.PopularReleases) || d.LatestRelease is not null
        || Has(d.TopTracks) || d.Pinned is not null || d.Bio is not null || d.Extras is not null
        || d.AlbumsTotal > 0 || d.SinglesTotal > 0 || d.CompilationsTotal > 0 || d.WorldRank > 0;

    /// <summary>Fold <paramref name="incoming"/> onto the STORED document with exactly <c>StoreEntityMerge.Artist</c>'s
    /// semantics at the facet level: an absent/empty facet keeps the stored one ("a thin write must never clobber a fat
    /// record"), a present one wins, and per-uri a NAME-LESS incoming stub keeps the stored rich card (the
    /// <c>MergeAlbumCards</c> rule — "discography flickers empty"). 0-is-unknown for every stat.</summary>
    public static ArtistOverviewDoc Merge(ArtistOverviewDoc? stored, ArtistOverviewDoc incoming)
    {
        if (stored is null) return incoming;
        return incoming with
        {
            TopAlbums = MergeCards(stored.TopAlbums, incoming.TopAlbums),
            AppearsOn = MergeCards(stored.AppearsOn, incoming.AppearsOn),
            PopularReleases = MergeCards(stored.PopularReleases, incoming.PopularReleases),
            LatestRelease = incoming.LatestRelease ?? stored.LatestRelease,
            TopTracks = Has(incoming.TopTracks) ? incoming.TopTracks : stored.TopTracks,
            Pinned = incoming.Pinned ?? stored.Pinned,
            Bio = incoming.Bio ?? stored.Bio,
            Extras = incoming.Extras ?? stored.Extras,
            AlbumsTotal = incoming.AlbumsTotal > 0 ? incoming.AlbumsTotal : stored.AlbumsTotal,
            SinglesTotal = incoming.SinglesTotal > 0 ? incoming.SinglesTotal : stored.SinglesTotal,
            CompilationsTotal = incoming.CompilationsTotal > 0 ? incoming.CompilationsTotal : stored.CompilationsTotal,
            WorldRank = incoming.WorldRank > 0 ? incoming.WorldRank : stored.WorldRank,
        };
    }

    /// <summary>Re-fatten a cold-loaded core from its overview: album refs become STUB cards (which
    /// <c>ArtistDiscography.Assemble</c> then upgrades from the standalone album rows) and top-track uris resolve through
    /// <paramref name="track"/>. Facets the overview never carried stay null — the page's existing hide-on-null contract.</summary>
    public static Artist Refatten(Artist core, ArtistOverviewDoc d, Func<string, Track?> track) => core with
    {
        TopAlbums = Cards(d.TopAlbums),
        AppearsOn = Cards(d.AppearsOn),
        PopularReleases = Cards(d.PopularReleases),
        LatestRelease = Card(d.LatestRelease),
        TopTracks = Tracks(d.TopTracks, track),
        Pinned = d.Pinned,
        Bio = d.Bio,
        Extras = d.Extras,
        AlbumsTotal = d.AlbumsTotal,
        SinglesTotal = d.SinglesTotal,
        CompilationsTotal = d.CompilationsTotal,
        WorldRank = d.WorldRank,
    };

    /// <summary>The album uris this overview references (the artist→albums `entity_refs` edges — Addendum A3: the offline
    /// search cascade "artist matched → its albums → their tracks" becomes a refs join once artists persist thin).</summary>
    public static List<string> ReferencedAlbums(ArtistOverviewDoc d)
    {
        var uris = new List<string>(32);
        Add(d.TopAlbums);
        Add(d.PopularReleases);
        Add(d.AppearsOn);
        if (d.LatestRelease is { Uri.Length: > 0 } l) uris.Add(l.Uri);
        return uris;

        void Add(IReadOnlyList<ArtistAlbumStub>? list)
        {
            if (list is null) return;
            for (int i = 0; i < list.Count; i++) if (list[i].Uri.Length > 0) uris.Add(list[i].Uri);
        }
    }

    static IReadOnlyList<ArtistAlbumStub>? Stubs(IReadOnlyList<Album>? albums, int cap)
    {
        if (albums is not { Count: > 0 }) return null;
        int n = Math.Min(albums.Count, cap);
        var list = new List<ArtistAlbumStub>(n);
        for (int i = 0; i < n; i++) if (Stub(albums[i]) is { } s) list.Add(s);
        return list.Count > 0 ? list : null;
    }

    static ArtistAlbumStub? Stub(Album? a)
        => a is { Uri.Length: > 0 } ? new ArtistAlbumStub(a.Uri, (int)a.Kind, a.Name, a.Year, NullIfEmpty(a.Cover?.Url)) : null;

    static IReadOnlyList<string>? TrackUris(IReadOnlyList<Track>? tracks)
    {
        if (tracks is not { Count: > 0 }) return null;
        var list = new List<string>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Uri.Length > 0) list.Add(tracks[i].Uri);
        return list.Count > 0 ? list : null;
    }

    static IReadOnlyList<Album>? Cards(IReadOnlyList<ArtistAlbumStub>? stubs)
    {
        if (stubs is not { Count: > 0 }) return null;
        var list = new List<Album>(stubs.Count);
        for (int i = 0; i < stubs.Count; i++) if (Card(stubs[i]) is { } card) list.Add(card);
        return list.Count > 0 ? list : null;
    }

    static Album? Card(ArtistAlbumStub? s) => s is null || s.Uri.Length == 0
        ? null
        : new Album("", s.Uri, s.Name, s.CoverUrl is null ? null : new Image(s.CoverUrl),
            Array.Empty<ArtistRef>(), s.Year, 0, Kind: (AlbumKind)s.Kind);

    static IReadOnlyList<Track>? Tracks(IReadOnlyList<string>? uris, Func<string, Track?> resolve)
    {
        if (uris is not { Count: > 0 }) return null;
        var list = new List<Track>(uris.Count);
        for (int i = 0; i < uris.Count; i++) if (resolve(uris[i]) is { } t) list.Add(t);
        return list.Count > 0 ? list : null;
    }

    static IReadOnlyList<ArtistAlbumStub>? MergeCards(IReadOnlyList<ArtistAlbumStub>? stored, IReadOnlyList<ArtistAlbumStub>? incoming)
    {
        if (!Has(incoming)) return stored;
        if (!Has(stored)) return incoming;
        var prior = new Dictionary<string, ArtistAlbumStub>(StringComparer.Ordinal);
        for (int i = 0; i < stored!.Count; i++) prior[stored[i].Uri] = stored[i];
        var merged = new List<ArtistAlbumStub>(incoming!.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var s = incoming[i];
            merged.Add(s.Name.Length == 0 && prior.TryGetValue(s.Uri, out var rich) ? rich with { Kind = s.Kind } : s);
        }
        return merged;
    }

    static bool Has<T>(IReadOnlyList<T>? v) => v is { Count: > 0 };
    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
