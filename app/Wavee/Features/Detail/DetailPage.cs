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
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared detail page (playlist / album / single / liked). A Component keyed per route in ContentHost, so the
// existing KeepAlive boundary caches it. It loads the matching IMusicLibrary slice through UseResource (which
// cancels on unmount — a fast nav-away aborts in flight), shows a matched skeleton via Skel.Region, then reveals the
// two-column shell. The per-context config is resolved POST-load (an album with ≤2 tracks becomes a "single").
sealed class DetailPage : Component
{
    readonly Signal<Route> _route;   // the (per-pane) navigation route, read reactively so ONE instance serves successive detail pages
    // Deep-link skeleton orientation (0 two-column · 1 vertical side-by-side · 2 vertical stacked) + the measured width,
    // so the loading shimmer matches the geometry the Ready reveal will use (no rail→hero jump). Value-gated.
    readonly Signal<int> _skelKind = new(0);
    float _skelW;
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

        // Preview identity is route-scoped so a card's already-known header data can appear immediately while the full
        // model loads. It is deliberately not used as a shared-element/connected-animation key.
        string previewKey = route.Name;

        // The PARTIAL model the Home card already had (cover/title/artist) — optional: deep links / search have none.
        var preview = UseMemo(() => navPreview?.Take(previewKey), previewKey);
        // Dep-keyed on the route: when navigation swaps the detail route on a REUSED instance, cancel the prior load and
        // refetch for the new id (resetting to the new preview/skeleton). Fires once at mount when nothing is reused.
        // Stable per-instance loadable, re-driven by the route dep key — DetailShell freezes the model at construction,
        // so the loadable INSTANCE must be stable across route swaps (a fresh store-cache instance per route would leave
        // the reused shell pinned to the first item — the master-detail reactivity bug). KeepAlive caches the parked page.
        var model = UseResource(ct => LoadAsync(svc, kind, id, ct), preview ?? DetailModel.Empty, route.Name).Loadable;

        // §4.1 — open-playlist LIVE in-place refresh (kills the skeleton flash). Subscribe the REAL store; when a push lands
        // for THIS playlist (or a Bulk), debounce the burst 150ms, re-run the SAME load off-thread, and SetReady the SAME
        // loadable in place — NEVER SetPending (that would re-seed to Empty = the shimmer). The UseResource dep stays
        // route.Name, untouched. Offline / fake backend (RealStore null) → a no-op. The subscription reads the LIVE route
        // (so one mount-lifetime subscription serves successive playlists), and eager-push context tracks the open uri.
        var post = Context.UsePost();
        var realStore = svc.RealStore;
        var realSync = svc.RealSync;
        UseEffect(() => realSync?.SetOpenContext(kind == DetailKind.Playlist ? id : null), route.Name);
        Context.UseSignalEffect(() =>
        {
            if (realStore is null) return;
            var gate = new object();
            System.Threading.CancellationTokenSource? debounce = null;
            var sub = realStore.Changes.Subscribe(Wavee.Backend.Observers.From<Wavee.Backend.StoreChange>(c =>
            {
                var (k, pid) = ParseDetail(_route.Peek());
                // Live kinds: an open PLAYLIST refreshes on its own uri (membership/diff writes bump it); the LIKED page
                // refreshes on any Liked-kind change (an unlike bumps the track uri with Kind=Liked — the list must drop
                // the row) — both also on a Bulk (hydrate/delta bursts coalesce into one).
                bool relevant = k switch
                {
                    DetailKind.Playlist when pid is not null => c.IsBulk || c.Uri == pid,
                    DetailKind.Liked => c.IsBulk || c.Kind == Wavee.Core.CollectionKind.Liked,
                    _ => false,
                };
                if (!relevant) return;
                System.Threading.CancellationTokenSource cts;
                lock (gate) { debounce?.Cancel(); debounce?.Dispose(); debounce = cts = new System.Threading.CancellationTokenSource(); }
                var token = cts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Short settle: long enough to fold a diff-apply + hydration burst into one re-map, short enough
                        // that a SELF-action (unlike the row you're looking at) reads as immediate, not laggy.
                        await Task.Delay(50, token).ConfigureAwait(false);
                        var fresh = await LoadAsync(svc, k, pid, token).ConfigureAwait(false);
                        if (!token.IsCancellationRequested) post(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            // Nav-away race: the debounced load may land after the user routed to a DIFFERENT detail page,
                            // which now reuses this same loadable cell. Re-resolve the LIVE route and drop the write unless
                            // it still points at THIS page — otherwise the old model flashes into the new page.
                            var (k2, pid2) = ParseDetail(_route.Peek());
                            if (k2 != k || pid2 != pid) return;
                            model.SetReady(fresh);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch { /* a failed background refresh keeps the current content — never surfaces */ }
                });
            }));
            Reactive.OnCleanup(() =>
            {
                sub.Dispose();
                lock (gate) { debounce?.Cancel(); debounce?.Dispose(); debounce = null; }
                realSync?.SetOpenContext(null);
            });
        });

        // Pre-loaded: render the shell straight away from the preview (header live), tracks stream in via Skel.Region.
        // Thread the preview's cover as the fallback so a loaded null cover never drops the flown-in art to a placeholder.
        if (preview is not null)
            return Embed.Comp(() => new DetailShell(_route, model, preview.Cover, svc.Settings));

        // No data at click (deep link): the full-page skeleton until the model lands, then the loadable-driven shell.
        // The content is wrapped in a plain Grow=1 BoxEl (NOT a bare component): the SkelRegion boundary mirrors its active
        // child's layout participation, and a plain BoxEl's Grow is written synchronously (WriteColumns) — a bare component's
        // Grow is mirrored from ITS output only after its async render effect runs, so the boundary would mirror a stale Grow=0
        // and detail pages whose virtualized list has little intrinsic height would collapse to 0 rows.
        // Measure the page width so the shimmer matches the orientation the Ready reveal will use (vertical hero vs the
        // two-column rail). First frame is two-column; the first bounds pass corrects the kind, re-rendering the shimmer
        // once, so the reveal lands on matching geometry (kills the rail→hero jump). Value-gated on the KIND only.
        int skelKind = _skelKind.Value;   // subscribe → re-render the shimmer on a kind change
        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            OnBoundsChanged = r =>
            {
                if (r.W <= 0f) return;
                _skelW = r.W;
                var cfg = SkeletonConfig(kind);
                int k = 0;
                // The vertical hero skeleton stands in for a TWO-COLUMN TRACK page (album/playlist/liked) whenever the page
                // will use the hero SYSTEM at reveal: either the window is narrow enough to enter it, or the page-layout
                // setting is Stacked (always-hero, every width). A podcast (Episodes) deep link keeps the two-column
                // skeleton regardless of the setting. Orientation is width-only (stacked below 440, side-by-side otherwise).
                bool forceHero = svc.Settings?.Get(WaveeSettings.DetailPageLayout) == DetailVerticalLayout.PageHero;
                if (cfg.TwoColumn && cfg.Content == DetailContent.Tracks
                    && (r.W < DetailLayoutBreakpoints.VerticalEnterW || forceHero))
                    k = DetailVerticalLayout.OrientationFor(r.W) == DetailHeroOrientation.Stacked ? 2 : 1;
                if (_skelKind.Peek() != k) _skelKind.Value = k;
            },
            Children =
            [
                Skel.Region(
                    model,
                    shimmerSource: () => skelKind == 0
                        ? DetailSkeleton.Build(SkeletonConfig(kind))
                        : DetailSkeleton.BuildVertical(SkeletonConfig(kind), stacked: skelKind == 2, _skelW),
                    onFailed: () => ErrorState.Build(model.Error),
                    // Pass the SHARED loadable (Ready when content runs), not a fresh Loadable.Ready(m): the shell is REUSED
                    // across detail routes, so it must read the one re-driven loadable — a per-render wrapper would leave the
                    // reused shell pinned to the first album's value.
                    content: _ => new BoxEl { Grow = 1f, Direction = 0, Children = [ Embed.Comp(() => new DetailShell(_route, model, settings: svc.Settings)) ] },
                    smoothResize: false),
            ],
        };
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
        DetailKind.Playlist => MapPlaylist(await LoadPlaylistAsync(svc, id ?? "", ct)),
        DetailKind.Liked => MapLiked(await svc.Library.GetLikedSongsAsync(ct)),
        DetailKind.Show => MapShow(await svc.Library.GetShowAsync(id ?? "", ct)),
        _ => MapAlbum(await svc.Library.GetAlbumAsync(id ?? "", ct)),
    };

    static async Task<Playlist?> LoadPlaylistAsync(Services svc, string uri, CancellationToken ct)
    {
        var p = await svc.Library.GetPlaylistAsync(uri, ct).ConfigureAwait(false);
        if (p is null || !p.Capabilities.IsOwner || svc.RealPlaylistMutations is null) return p;
        try
        {
            var perm = await svc.RealPlaylistMutations.GetBasePermissionAsync(p.Uri, ct).ConfigureAwait(false);
            if (perm is { } bp)
                return p with { IsPublic = bp.IsPublic, BasePermissionRevision = bp.Revision };
        }
        catch { /* offline / transient — keep default visibility */ }
        return p;
    }

    internal static async Task<DetailModel?> ReloadPlaylistDetailAsync(Services svc, string uri, CancellationToken ct = default)
    {
        var p = await LoadPlaylistAsync(svc, uri, ct).ConfigureAwait(false);
        return p is null ? null : MapPlaylist(p);
    }

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
            Capabilities: p.Capabilities,
            Collaborators: p.Collaborators,
            UserProfilesById: UserProfileMap(p),
            IsPublic: p.IsPublic,
            BasePermissionRevision: p.BasePermissionRevision,
            ShareUrl: SpotifyPlaylistWebUrl(p.Uri));
    }

    static IReadOnlyDictionary<string, Owner>? UserProfileMap(Playlist p)
    {
        var map = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        Add(p.Owner);
        if (p.Collaborators is { Count: > 0 } collaborators)
            for (int i = 0; i < collaborators.Count; i++) Add(collaborators[i]);
        return map.Count == 0 ? null : map;

        void Add(Owner? owner)
        {
            if (owner is null) return;
            if (owner.Id.Length > 0) map[owner.Id] = owner;
            var canonical = UserProfileIds.Normalize(owner.Id);
            if (canonical is not null)
            {
                map[canonical] = owner;
                map[UserProfileIds.BareId(canonical)] = owner;
            }
        }
    }

    static DetailModel MapLiked(IReadOnlyList<Track> tracks)
    {
        string meta = Strings.Detail.MetaLine(Strings.Detail.SongCount(tracks.Count), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)));
        return new DetailModel(
            Title: Loc.Get(Strings.Detail.LikedSongs), Cover: null, ContextUri: "spotify:collection:tracks",
            BadgeType: null, Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null, Palette: null,
            HasDateAdded: tracks.Any(t => t.AddedAt is not null),   // liked rows carry the collection add time → Date-added column + sort
            HasVideo: tracks.Any(t => t.HasVideo));
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

    // Delegates to the ONE consolidated converter (Actions/SpotifyLink.cs); keeps this surface's bare-id fallback
    // (a caller passing a raw playlist id — no spotify: prefix — still gets a playlist url).
    internal static string SpotifyPlaylistWebUrl(string uri)
        => SpotifyLink.WebUrl(uri) ?? $"https://open.spotify.com/playlist/{uri}";
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
                // The reserved cover slot doubles as the connected-animation dest while the album loads — the flying card
                // art lands here immediately (no wait for the fetch); the real cover cross-fades in underneath when ready.
                new BoxEl { Width = cover, Height = cover, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault },
                Bar(cover * 0.5f, 12f), Bar(cover * 0.85f, 30f), Bar(cover * 0.6f, 13f),
                new BoxEl { Height = WaveeSpace.S },
                Bar(120f, 40f),
            ],
        };

        return new BoxEl { Direction = 0, Grow = 1f, Children = [rail, tracks] };
    }

    // The vertical (Apple Music) deep-link skeleton: a 24-pad hero block matching DetailVerticalHero's composition —
    // an ArtworkFor(...)-sized art box (centered when stacked), title/meta bars, two 48-DIP pill bars — over the same
    // 8 track RowBar()s. Reuses Bar(...) so the shimmer shape can't drift from the real bars.
    public static Element BuildVertical(DetailConfig cfg, bool stacked, float w)
    {
        var o = stacked ? DetailHeroOrientation.Stacked : DetailHeroOrientation.SideBySide;
        float art = DetailVerticalLayout.ArtworkFor(w, o);
        float pad = DetailVerticalLayout.HeroPad;
        float inner = MathF.Max(160f, (w > 0f ? w : DetailVerticalLayout.FallbackW) - 2f * pad);
        float infoW = stacked ? inner : MathF.Max(120f, inner - art - DetailVerticalLayout.HeroGap);

        Element artBox = new BoxEl
        {
            Width = art, Height = art, Shrink = 0f, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault,
            AlignSelf = stacked ? FlexAlign.Center : FlexAlign.Start,
        };
        Element PillBar() => new BoxEl
        {
            Grow = 1f, Basis = 0f, MaxWidth = 200f, Height = 48f,
            Corners = CornerRadius4.All(24f), Fill = Tok.FillCardDefault,
        };
        Element[] infoKids =
        [
            Bar(infoW * 0.4f, 14f),   // badges / owner
            Bar(infoW * 0.82f, 30f),  // title
            Bar(infoW * 0.5f, 12f),   // meta
            new BoxEl { Direction = 0, Gap = WaveeSpace.M, Children = [PillBar(), PillBar()] },
        ];
        Element info = stacked
            ? new BoxEl { Direction = 1, Width = infoW, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center, Children = infoKids }
            : new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WaveeSpace.M, AlignItems = FlexAlign.Stretch, Children = infoKids };
        Element hero = stacked
            ? new BoxEl { Direction = 1, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center, Children = [artBox, info] }
            : new BoxEl { Direction = 0, Gap = DetailVerticalLayout.HeroGap, AlignItems = FlexAlign.Start, Children = [artBox, info] };

        var rows = new Element[8];
        for (int i = 0; i < rows.Length; i++) rows[i] = RowBar();
        var tracks = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S, Grow = 1f,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.L),
            Children = rows,
        };

        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children =
            [
                new BoxEl { Direction = 1, Padding = Edges4.All(pad), Children = [hero] },
                tracks,
            ],
        };
    }

    static Element RowBar() => new BoxEl
    {
        Direction = 0, Height = 48f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Children = [Bar(20f, 14f), new BoxEl { Grow = 1f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, Bar(40f, 12f)],
    };

    static Element Bar(float w, float h) =>
        new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault };
}
