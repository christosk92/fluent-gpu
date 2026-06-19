using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared detail page (playlist / album / single / liked). A Component keyed per route in ContentHost, so the
// existing KeepAlive boundary caches it. It loads the matching IMusicLibrary slice through UseAsyncResource (which
// cancels on unmount — a fast nav-away aborts in flight), shows a matched skeleton via StatefulRegion, then reveals the
// two-column shell. The per-context config is resolved POST-load (an album with ≤2 tracks becomes a "single").
sealed class DetailPage : Component
{
    readonly DetailKind _kind;
    readonly string? _id;
    public DetailPage(DetailKind kind, string? id) { _kind = kind; _id = id; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };

        var model = UseAsyncResource(ct => LoadAsync(svc, ct), DetailModel.Empty);

        return StatefulRegion.Single(
            model,
            shimmer: () => DetailSkeleton.Build(SkeletonConfig()),
            content: m => Embed.Comp(() => new DetailShell(m, ResolveConfig(m))));
    }

    // Album cfg is release-kind-dependent (single = one-track layout, compilation = various-artists rows); playlist/liked fixed.
    DetailConfig ResolveConfig(DetailModel m) => _kind switch
    {
        DetailKind.Playlist => DetailConfig.Playlist,
        DetailKind.Liked => DetailConfig.Liked,
        _ => m.ReleaseKind switch
        {
            AlbumKind.Single => DetailConfig.Single,
            AlbumKind.Compilation => DetailConfig.Compilation,
            _ => DetailConfig.Album,   // Album + EP share the album layout
        },
    };

    // A coarse config just for sizing the loading skeleton (the single-vs-album split doesn't matter pre-load).
    DetailConfig SkeletonConfig() => _kind switch
    {
        DetailKind.Playlist => DetailConfig.Playlist,
        DetailKind.Liked => DetailConfig.Liked,
        _ => DetailConfig.Album,
    };

    async Task<DetailModel> LoadAsync(Services svc, CancellationToken ct) => _kind switch
    {
        DetailKind.Playlist => MapPlaylist(await svc.Library.GetPlaylistAsync(_id ?? "", ct)),
        DetailKind.Liked => MapLiked(await svc.Library.GetLikedSongsAsync(ct)),
        _ => await MapAlbumAsync(svc, await svc.Library.GetAlbumAsync(_id ?? "", ct), ct),
    };

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
            BadgeType: null, Year: null, OwnerName: p.OwnerName, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: p.Description, MetaLine: meta,
            Tracks: tracks, AboutArtist: null, Palette: null,
            HasDateAdded: hasDate, HasAddedBy: contributors.Count >= 2, HasVideo: hasVideo);
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

    static async Task<DetailModel> MapAlbumAsync(Services svc, Album a, CancellationToken ct)
    {
        var tracks = a.Tracks ?? Array.Empty<Track>();
        // Best-effort trailing fetch (About-artist / Fans-also-like / Featured-on) — fired concurrently; a failure must
        // not fail the page. (No related-artists / featured-on endpoints in the model, so we reuse the catalog lists.)
        Artist? about = null;
        IReadOnlyList<Artist> fans = Array.Empty<Artist>();
        IReadOnlyList<PlaylistSummary> featured = Array.Empty<PlaylistSummary>();
        try
        {
            var aboutT = a.Artists.Count > 0 ? svc.Library.GetArtistAsync(a.Artists[0].Id, ct) : null;
            var fansT = svc.Library.GetArtistsAsync(ct);
            var featuredT = svc.Library.GetPlaylistsAsync(ct);
            if (aboutT is not null) about = await aboutT;
            fans = await fansT;
            featured = await featuredT;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* trailing degrades gracefully */ }

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
            Tracks: tracks, AboutArtist: about, Palette: null,
            HasVideo: tracks.Any(t => t.HasVideo), ReleaseKind: a.Kind, Fans: fans, FeaturedOn: featured);
    }
}

// The loading skeleton, matched to the real layout (rail block + N row bars) so the reveal doesn't jump.
static class DetailSkeleton
{
    public static Element Build(DetailConfig cfg)
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
                new BoxEl { Width = cover, Height = cover, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault },
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
