using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared detail page (playlist / album / single / liked). A Component keyed per route in ContentHost, so the
// existing KeepAlive boundary caches it. It loads the matching IMusicLibrary slice through UseAsyncResource (which
// cancels on unmount — a fast nav-away aborts in flight), shows a matched skeleton via Skel.Region, then reveals the
// two-column shell. The per-context config is resolved POST-load (an album with ≤2 tracks becomes a "single").
sealed class DetailPage : Component
{
    readonly Signal<Route> _route;   // the (per-pane) navigation route, read reactively so ONE instance serves successive detail pages
    public DetailPage(Signal<Route> route) { _route = route; }

    // Route → (kind, id): album:/pl: carry the uri after the prefix; "liked" is the saved-tracks collection.
    internal static (DetailKind Kind, string? Id) ParseDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["album:".Length..])
        : r.Name.StartsWith("pl:", StringComparison.Ordinal) ? (DetailKind.Playlist, r.Name["pl:".Length..])
        : r.Name == "local" ? (DetailKind.Playlist, "wavee:local:all")   // the Local Files collection (LocalSource owns it)
        : r.Name.StartsWith("show:", StringComparison.Ordinal) ? (DetailKind.Show, r.Name["show:".Length..])
        : (DetailKind.Liked, null);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };
        var navPreview = UseContext(NavPreviewStore.Slot);

        var route = _route.Value;                          // subscribe → re-render when navigation swaps the detail route in place
        var (kind, id) = ParseDetail(route);

        // Connected-animation (Hero) key for the cover == the route key the Home card navigated with. Threaded into BOTH
        // the skeleton's reserved cover slot AND the real cover, so the flying art lands whichever is laid out first.
        string? morphKey = MorphKeys.For(kind, id);

        // The PARTIAL model the Home card already had (cover/title/artist) — optional: deep links / search have none.
        var preview = UseMemo(() => morphKey is null ? null : navPreview?.Take(morphKey), morphKey ?? "");
        // Dep-keyed on the route: when navigation swaps the detail route on a REUSED instance, cancel the prior load and
        // refetch for the new id (resetting to the new preview/skeleton). Fires once at mount when nothing is reused.
        // Stable per-instance loadable, re-driven by the route dep key — DetailShell freezes the model at construction,
        // so the loadable INSTANCE must be stable across route swaps (a fresh store-cache instance per route would leave
        // the reused shell pinned to the first item — the master-detail reactivity bug). KeepAlive caches the parked page.
        var model = UseAsyncResource(ct => LoadAsync(svc, kind, id, ct), preview ?? DetailModel.Empty, route.Name);

        // Pre-loaded: render the shell straight away from the preview (header live), tracks stream in via Skel.Region.
        // Thread the preview's cover as the fallback so a loaded null cover never drops the flown-in art to a placeholder.
        if (preview is not null)
            return Embed.Comp(() => new DetailShell(_route, model, preview.Cover));

        // No data at click (deep link): the full-page skeleton until the model lands, then the loadable-driven shell.
        // The content is wrapped in a plain Grow=1 BoxEl (NOT a bare component): the SkelRegion boundary mirrors its active
        // child's layout participation, and a plain BoxEl's Grow is written synchronously (WriteColumns) — a bare component's
        // Grow is mirrored from ITS output only after its async render effect runs, so the boundary would mirror a stale Grow=0
        // and the single-column page (a virtualized list whose only intrinsic height is its chrome) would collapse to 0 rows.
        return Skel.Region(
            model,
            shimmerSource: () => DetailSkeleton.Build(SkeletonConfig(kind), morphKey),
            onFailed: () => ErrorState.Build(model.Error),
            // Pass the SHARED loadable (Ready when content runs), not a fresh Loadable.Ready(m): the shell is REUSED
            // across detail routes, so it must read the one re-driven loadable — a per-render wrapper would leave the
            // reused shell pinned to the first album's value.
            content: _ => new BoxEl { Grow = 1f, Direction = 0, Children = [ Embed.Comp(() => new DetailShell(_route, model)) ] },
            smoothResize: false);
    }

    // Album cfg is release-kind-dependent (single = one-track layout, compilation = various-artists rows); playlist/liked fixed.
    internal static DetailConfig ResolveConfig(DetailKind kind, DetailModel m) => kind switch
    {
        DetailKind.Playlist => DetailConfig.Playlist,
        DetailKind.Liked => DetailConfig.Liked,
        DetailKind.Show => DetailConfig.Show,
        _ => m.ReleaseKind switch
        {
            AlbumKind.Single => DetailConfig.Single,
            AlbumKind.Compilation => DetailConfig.Compilation,
            _ => DetailConfig.Album,   // Album + EP share the album layout
        },
    };

    // A coarse config just for sizing the loading skeleton (the single-vs-album split doesn't matter pre-load).
    static DetailConfig SkeletonConfig(DetailKind kind) => kind switch
    {
        DetailKind.Playlist => DetailConfig.Playlist,
        DetailKind.Liked => DetailConfig.Liked,
        DetailKind.Show => DetailConfig.Show,
        _ => DetailConfig.Album,
    };

    internal static async Task<DetailModel> LoadAsync(Services svc, DetailKind kind, string? id, CancellationToken ct) => kind switch
    {
        DetailKind.Playlist => MapPlaylist(await svc.Library.GetPlaylistAsync(id ?? "", ct)),
        DetailKind.Liked => MapLiked(await svc.Library.GetLikedSongsAsync(ct)),
        DetailKind.Show => MapShow(await svc.Library.GetShowAsync(id ?? "", ct)),
        _ => MapAlbum(await svc.Library.GetAlbumAsync(id ?? "", ct)),
    };

    // A podcast show folds onto the shared detail surface: rail = cover + PODCAST pill + publisher/episode-count meta +
    // description + Play/Follow; the right column renders Episodes (DetailConfig.Show.Content == Episodes → EpisodeList).
    static DetailModel MapShow(Show? s)
    {
        if (s is null) return DetailModel.Empty;
        var eps = s.Episodes ?? Array.Empty<Episode>();
        string meta = s.Publisher + " · " + Strings.Podcast.EpisodeCount(eps.Count);
        return new DetailModel(
            Title: s.Name, Cover: s.Cover, ContextUri: s.Uri,
            BadgeType: Loc.Get(Strings.Podcast.Show), Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: s.Description, MetaLine: meta,
            Tracks: Array.Empty<Track>(), AboutArtist: null, Palette: null,
            Episodes: eps, Publisher: s.Publisher);
    }

    static DetailModel MapPlaylist(Playlist p)
    {
        var tracks = p.Tracks ?? Array.Empty<Track>();
        string meta = Strings.Detail.MetaLine(Strings.Detail.SongCount(p.TrackCount), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)));
        // Data-drive the optional columns: show Date-added if any track has one, and Added-by only when the playlist is
        // collaborative (≥2 distinct contributors) — matching the reference app's "hide unless it carries signal" rule.
        bool hasDate = false, hasVideo = false;
        var contributors = new HashSet<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].AddedAt is not null) hasDate = true;
            if (tracks[i].HasVideo) hasVideo = true;
            if (tracks[i].AddedBy is { } by) contributors.Add(by);
        }
        return new DetailModel(
            Title: p.Name, Cover: p.Cover, ContextUri: p.Uri,
            BadgeType: null, Year: null, OwnerName: p.OwnerName, OwnerImage: p.Owner?.Avatar,
            Artists: Array.Empty<ArtistRef>(), Description: p.Description, MetaLine: meta,
            Tracks: tracks, AboutArtist: null, Palette: p.Palette,
            HasDateAdded: hasDate, HasAddedBy: contributors.Count >= 2, HasVideo: hasVideo,
            Capabilities: p.Capabilities);
    }

    static DetailModel MapLiked(IReadOnlyList<Track> tracks)
    {
        string meta = Strings.Detail.MetaLine(Strings.Detail.SongCount(tracks.Count), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)));
        return new DetailModel(
            Title: Loc.Get(Strings.Detail.LikedSongs), Cover: null, ContextUri: "spotify:collection:tracks",
            BadgeType: null, Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null, Palette: null, HasVideo: tracks.Any(t => t.HasVideo));
    }

    // The album model: hero + tracklist + the "More by" shelf the getAlbum payload carries. The below-the-fold
    // enrichment (About-the-artist / Fans-also-like / Featured-on / Merch / Similar) is deliberately NOT awaited here —
    // AlbumTrailing loads each section independently so the hero and track list render immediately and no slow or failed
    // enrichment can block (or sink) them.
    static DetailModel MapAlbum(Album a)
    {
        var tracks = a.Tracks ?? Array.Empty<Track>();
        string badge = a.Kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        };
        string meta = Strings.Detail.MetaLineYear(
            Strings.Detail.SongCount(a.TrackCount), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)), a.Year);
        return new DetailModel(
            Title: a.Name, Cover: a.Cover, ContextUri: a.Uri,
            BadgeType: badge, Year: a.Year.ToString(), OwnerName: null, OwnerImage: null,
            Artists: a.Artists, Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null, Palette: a.Palette,
            HasVideo: tracks.Any(t => t.HasVideo), ReleaseKind: a.Kind, MoreByArtist: a.MoreByArtist,
            Label: a.Label, Copyright: a.Copyright, ReleaseDate: FormatReleaseDate(a.ReleaseDate, a.ReleaseDatePrecision), AlbumArtists: a.ArtistsDetailed,
            OtherVersions: a.OtherVersions, CourtesyLine: a.CourtesyLine, ReleaseDatePrecision: a.ReleaseDatePrecision,
            DiscCount: a.DiscCount, ShareUrl: a.ShareUrl, IsPreRelease: a.IsPreRelease, PreReleaseEnd: a.PreReleaseEnd);
    }

    // ISO date + Spotify precision: YEAR → "2014"; MONTH → "November 2014"; DAY → "November 4, 2014".
    static string? FormatReleaseDate(string? iso, string? precision)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!System.DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ) return iso;
        return (precision ?? "").ToUpperInvariant() switch
        {
            "YEAR" => d.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            "MONTH" => d.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            _ => d.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

// The loading skeleton, matched to the real layout (rail block + N row bars) so the reveal doesn't jump.
static class DetailSkeleton
{
    public static Element Build(DetailConfig cfg, string? morphKey = null)
    {
        var rows = new Element[8];
        for (int i = 0; i < rows.Length; i++) rows[i] = RowBar();
        var tracks = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S, Grow = 1f,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.L),
            Children = rows,
        };

        if (!cfg.TwoColumn)
            return new BoxEl { Direction = 1, Grow = 1f, Children = [tracks] };

        float cover = DetailRail.CoverEdge(cfg.RailWidth);
        var rail = new BoxEl
        {
            Direction = 1, Gap = 14f, Shrink = 0f, Width = cfg.RailWidth,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.XXL, WaveeSpace.S, WaveeSpace.XXL),
            Children =
            [
                // The reserved cover slot doubles as the connected-animation dest while the album loads — the flying card
                // art lands here immediately (no wait for the fetch); the real cover cross-fades in underneath when ready.
                new BoxEl { Width = cover, Height = cover, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault, MorphId = morphKey },
                Bar(cover * 0.5f, 12f), Bar(cover * 0.85f, 30f), Bar(cover * 0.6f, 13f),
                new BoxEl { Height = WaveeSpace.S },
                Bar(120f, 40f),
            ],
        };

        return new BoxEl { Direction = 0, Grow = 1f, Children = [rail, tracks] };
    }

    static Element RowBar() => new BoxEl
    {
        Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Children = [Bar(20f, 14f), new BoxEl { Grow = 1f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, Bar(40f, 12f)],
    };

    static Element Bar(float w, float h) =>
        new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault };
}
