using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared omnibar query signal, provided once at the shell root (WaveeShell) and read by the SearchPage live, so the
// page tracks the search box AS-YOU-TYPE without threading the signal down through ContentHost / the route.
static class SearchQuery
{
    public static readonly Context<Signal<string>?> Slot = new(null);
}

// The Search page (docs/architecture.md §2 "Search, browse & home") — WaveeMusic's search skeleton: a filter-chip row
// (All / Songs / Artists / Albums / Playlists), an empty "Browse all" category grid, an "All" composite (Top result +
// Songs band + per-type shelves), and a flat unified results list per chip (row + type pill). The query comes from the
// live omnibar signal (SearchQuery.Slot) so typing re-runs the search; the route carries the query for history.
sealed class SearchPage : Component
{
    readonly Signal<int> _chip = new(0);   // 0 All · 1 Songs · 2 Artists · 3 Albums · 4 Playlists
    readonly SelectionModel _songsSel = new();
    IReadOnlyList<Track> _songsTracks = Array.Empty<Track>();
    const int SearchPageSize = 50;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var querySig = UseContext(SearchQuery.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };
        // Debounce the omnibar query 250ms: a fast typist fires ONE search, not one per keystroke. The thunk auto-tracks
        // querySig, so each keystroke re-arms the trailing-edge commit; the UseResource deps below ride the debounced value
        // (empty query → BrowseAll after the same quiet window). Zero re-render until the debounce fires.
        string q = UseDebouncedValue(() => (querySig?.Value ?? "").Trim(), 250f).Value;   // subscribe → re-render + re-search after quiet
        int chip = _chip.Value;                             // subscribe
        UseEffect(() => _songsSel.ClearSelection(), q + ":" + chip);
        var facet = RequestFacetFor(chip);
        var results = UseResource(ct => svc.Library.SearchAsync(q, facet, 0, SearchPageSize, ct), SearchResults.Empty, (q, chip)).Loadable;   // selected tab drives the live facet op

        // Scroll-position restoration keyed by the query: each distinct query has its own remembered scroll (a new query
        // starts at the top; returning to a prior query restores it). One ScrollView node serves every query in place.
        if (q.Length == 0)
            return ScrollView(BrowseAll(querySig)) with { Grow = 1f, ScrollKey = "search:" };

        var resultBody = new BoxEl
        {
            Direction = 1, Gap = Spacing.L,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, PlayerDock.Reserve + Spacing.XXL),
            // Songs (chip 1) is a BOUND virtualized list — its slots realize AFTER the skel-reveal walk runs, so the
            // block-level StaggerRows would fade the whole list as one; the per-slot RowRise entrance in SearchSongs
            // owns the stagger there instead (mirrors the detail list's entrance-vs-reveal split).
            Children = [Skel.Region(results, SearchShimmer, r => ResultsFor(r, chip, q, svc, go),
                reveal: chip == 1 ? SkelReveal.None : SkelReveal.StaggerRows,
                onFailed: () => ErrorState.Build(results.Error))],
        };

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            MinHeight = 0f,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f,
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.S),
                    Children = [ChipBar(chip)],
                },
                chip == 1
                    ? ZStack(
                        ScrollView(resultBody) with { Grow = 1f, MinHeight = 0f, ScrollKey = "search:" + q + ":" + chip },
                        Embed.Comp(() => new SelectionCommandBar(_songsSel, i => (uint)i < (uint)_songsTracks.Count ? _songsTracks[i] : null)))
                        with { Grow = 1f, MinHeight = 0f }
                    : ScrollView(resultBody) with { Grow = 1f, MinHeight = 0f, ScrollKey = "search:" + q + ":" + chip },
            ],
        };
    }

    // Lightweight loading skeleton (finding #7): a fixed list of result-row placeholders so the pending edge doesn't build
    // the full results tree just to derive a skeleton. Sized childless boxes → shimmer bars; SmoothResize eases the swap.
    static Element SearchShimmer()
    {
        var rows = new Element[10];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Height = 48f, AlignSelf = FlexAlign.Stretch,
                Corners = CornerRadius4.All(Radii.Control),
                Fill = Tok.FillSubtleSecondary,
            };
        return new BoxEl { Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, Children = rows };
    }

    Element ChipBar(int chip) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center,
        Children = [SelectorBar.Create(ChipLabels(), _chip)],
    };

    static string[] ChipLabels() =>
    [
        Loc.Get(Strings.Search.All), Loc.Get(Strings.Search.Songs), Loc.Get(Strings.Search.Albums),
        Loc.Get(Strings.Search.Playlists), Loc.Get(Strings.Search.Audiobooks),
        Loc.Get(Strings.Search.PodcastsShows), Loc.Get(Strings.Search.Artists),
        Loc.Get(Strings.Search.Episodes), Loc.Get(Strings.Search.Profiles),
    ];

    // Every chip maps to a dedicated captured Pathfinder operation now, so the request facet IS the display facet.
    // Audiobooks/Podcasts used to query All and filter the unified top hits, which capped them at the top-results page
    // size and mixed in unrelated kinds.
    static SearchFacet RequestFacetFor(int chip) => FacetFor(chip);

    static SearchFacet FacetFor(int chip) => chip switch
    {
        1 => SearchFacet.Tracks,
        2 => SearchFacet.Albums,
        3 => SearchFacet.Playlists,
        4 => SearchFacet.Audiobooks,
        5 => SearchFacet.Podcasts,
        6 => SearchFacet.Artists,
        7 => SearchFacet.Episodes,
        8 => SearchFacet.Profiles,
        _ => SearchFacet.All,
    };

    // ── results dispatch (All composite vs a flat per-type list) ──
    Element ResultsFor(SearchResults r, int chip, string q, Services svc, Action<string, string?> go)
    {
        void Play(string uri) => _ = svc.Player.PlayAsync(uri, 0);
        void PlayTrack(string uri) => _ = svc.Player.PlayTrackAsync(uri);
        void PlayKnownTrack(Track track) => _ = svc.Player.PlayTrackAsync(track);

        // Dedicated facets render their OWN result list (not a filtered slice of the All-tab top hits), so they page
        // properly and keep their per-kind metadata: an audiobook's access signifier, an episode's show name.
        if (chip == 4)
            return HitsList(r.Audiobooks, Loc.Get(Strings.Search.NoAudiobookResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (chip == 5)
            return HitsList(ShowHits(r.Shows), Loc.Get(Strings.Search.NoPodcastResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (chip == 7)
            return HitsList(EpisodeHits(r.Episodes), Loc.Get(Strings.Search.NoEpisodeResults), r, go, Play, PlayTrack, PlayKnownTrack);
        if (chip == 8)
            return HitsList(r.Profiles, Loc.Get(Strings.Search.NoProfileResults), r, go, Play, PlayTrack, PlayKnownTrack);

        if (chip != 0 && r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count == 0)
            return Centered(Icons.Search, Loc.Get(Strings.Search.NoResults), Strings.Search.NoResultsSub(q));

        return chip switch
        {
            1 => SongsList(r.Tracks, PlayKnownTrack, go, int.MaxValue),
            2 => FlatList(r.Albums.Select(a => ResultRow(a.Cover, a.Id.GetHashCode(), a.Name, a.Artists.Count > 0 ? a.Artists[0].Name : "", Loc.Get(Strings.Search.TypeAlbum), false, () => go("album:" + a.Uri, a.Name)))),
            3 => FlatList(r.Playlists.Select(p => ResultRow(p.Cover, p.Id.GetHashCode(), p.Name, p.OwnerName, Loc.Get(Strings.Search.TypePlaylist), false, () => go("pl:" + p.Uri, p.Name)))),
            6 => FlatList(r.Artists.Select(a => ResultRow(a.Image, a.Id.GetHashCode(), a.Name, Loc.Get(Strings.Search.TypeArtist), Loc.Get(Strings.Search.TypeArtist), true, () => go("artist:" + a.Uri, a.Name)))),
            _ => AllView(r, go, Play, PlayTrack, PlayKnownTrack),
        };
    }

    Element AllView(SearchResults r, Action<string, string?> go, Action<string> play, Action<string> playTrack, Action<Track> playKnownTrack)
        => Ctx.Provide(SearchAllList.Props, new SearchAllList.Model(r, go, playTrack, play, playKnownTrack),
            Embed.Comp(() => new SearchAllList()));

    Element TopHitList(SearchResults r, Func<SearchTopHit, bool> include, string emptyTitle,
                       Action<string, string?> go, Action<string> play, Action<string> playTrack, Action<Track> playKnownTrack)
        => Ctx.Provide(SearchAllList.Props, new SearchAllList.Model(r, go, playTrack, play, playKnownTrack, include, emptyTitle),
            Embed.Comp(() => new SearchAllList()));

    /// <summary>Render an explicit hit list (a dedicated facet's results) through the SAME row factory the All tab
    /// uses, so a search row looks and behaves identically regardless of which operation produced it.</summary>
    Element HitsList(IReadOnlyList<SearchTopHit>? hits, string emptyTitle, SearchResults r,
                     Action<string, string?> go, Action<string> play, Action<string> playTrack, Action<Track> playKnownTrack)
        => Ctx.Provide(SearchAllList.Props,
            new SearchAllList.Model(r, go, playTrack, play, playKnownTrack, Filter: null, EmptyTitle: emptyTitle,
                                    Hits: hits ?? Array.Empty<SearchTopHit>()),
            Embed.Comp(() => new SearchAllList()));

    // Show/Episode are real domain records (the app has podcast surfaces to route into); the search LIST renders them
    // through the shared hit row, so they are projected here instead of duplicating the row factory.
    static IReadOnlyList<SearchTopHit> ShowHits(IReadOnlyList<Show>? shows)
    {
        if (shows is not { Count: > 0 }) return Array.Empty<SearchTopHit>();
        var hits = new SearchTopHit[shows.Count];
        for (int i = 0; i < shows.Count; i++)
        {
            var sh = shows[i];
            hits[i] = new SearchTopHit(SearchHitKind.Podcast, sh.Uri, sh.Name, sh.Publisher,
                Loc.Get(Strings.Search.TypePodcast), sh.Cover, RoundImage: false, Followable: true,
                MatchedLyrics: false, AccessLabel: null);
        }
        return hits;
    }

    static IReadOnlyList<SearchTopHit> EpisodeHits(IReadOnlyList<Episode>? episodes)
    {
        if (episodes is not { Count: > 0 }) return Array.Empty<SearchTopHit>();
        var hits = new SearchTopHit[episodes.Count];
        for (int i = 0; i < episodes.Count; i++)
        {
            var ep = episodes[i];
            hits[i] = new SearchTopHit(SearchHitKind.Episode, ep.Uri, ep.Title, ep.ShowName,
                Loc.Get(Strings.Search.TypeEpisode), ep.Image, RoundImage: false, Followable: false,
                MatchedLyrics: false, AccessLabel: null, Detail: ep.Description);
        }
        return hits;
    }

    Element SongsList(IReadOnlyList<Track> tracks, Action<Track> playTrack, Action<string, string?> go, int max)
    {
        _songsTracks = tracks;
        return Ctx.Provide(SearchSongs.Props, new SearchSongs.Model(tracks, playTrack, go, max, _songsSel),
            Embed.Comp(() => new SearchSongs()) with { SkeletonProxy = () => SearchSongs.SkeletonShape(tracks, max) });
    }

    // ── flat unified results list (per chip) ──
    static Element FlatList(IEnumerable<Element> rows) => new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows.ToArray() };

    static Element ResultRow(Image? cover, int seed, string title, string subtitle, string type, bool circular, Action open) => new BoxEl
    {
        Direction = 0, Height = 60f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = CornerRadius4.All(6f),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary, OnClick = open,
        Children =
        [
            new BoxEl { Width = 48f, Height = 48f, Shrink = 0f, Corners = CornerRadius4.All(circular ? 24f : 6f), ClipToBounds = true,
                Children = [Surfaces.Artwork(cover, seed & 0x7fffffff, 48f, 48f, circular ? 24f : 6f)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
                Children =
                [
                    new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
            TypePill(type),
        ],
    };

    static Element TypePill(string type) => new BoxEl
    {
        Padding = new Edges4(10f, 3f, 10f, 3f), Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
        Children = [new TextEl(type) { Size = 10f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 40f }],
    };

    // ── browse empty state ───────────────────────────────────────────────────────────────────────────────────────
    // No query → the real Browse directory (every Spotify category, grouped and alphabetised), NOT a hardcoded grid of
    // invented category tiles. Type to search, don't type and you're browsing.
    Element BrowseAll(Signal<string>? querySig)
    {
        var go = UseContext(HistoryStore.NavCtx);
        var model = new Wavee.Features.Browse.BrowseDirectory.Model(
            OnOpenCategory: uri => go(Wavee.Features.Browse.BrowseRoutes.Page(uri), null),
            // Live Events is a BrowseClientFeature, not a page — it routes into the Concerts hub Wavee already has.
            OnOpenFeature: uri => go(string.Equals(uri, "spotify:concerts", StringComparison.Ordinal)
                ? Wavee.Features.Concerts.ConcertRoutes.Hub
                : uri, null));

        return Ctx.Provide(Wavee.Features.Browse.BrowseDirectory.Props, model,
            new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, PlayerDock.Reserve + Spacing.XXL),
                Children = [Embed.Comp(() => new Wavee.Features.Browse.BrowseDirectory())],
            });
    }

    // ── top result ───────────────────────────────────────────────────────────────────────────────────────
    static Element? TopResult(SearchResults r, Action<string, string?> go, Action<string> play)
    {
        if (r.Artists.Count > 0)
        {
            var a = r.Artists[0];
            return TopCard(a.Image, a.Name, Loc.Get(Strings.Search.TypeArtist), a.Id.GetHashCode(), true, () => go("artist:" + a.Uri, a.Name), () => play(a.Uri));
        }
        if (r.Albums.Count > 0)
        {
            var a = r.Albums[0];
            return TopCard(a.Cover, a.Name, Loc.Get(Strings.Search.TypeAlbum), a.Id.GetHashCode(), false, () => go("album:" + a.Uri, a.Name), () => play(a.Uri));
        }
        if (r.Playlists.Count > 0)
        {
            var p = r.Playlists[0];
            return TopCard(p.Cover, p.Name, Loc.Get(Strings.Search.TypePlaylist), p.Id.GetHashCode(), false, () => go("pl:" + p.Uri, p.Name), () => play(p.Uri));
        }
        return null;
    }

    static Element TopCard(Image? img, string name, string type, int seed, bool circular, Action open, Action play) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.L),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
        HoverFill = Tok.FillCardDefault, OnClick = open,
        Children =
        [
            new BoxEl { Width = 92f, Height = 92f, Corners = CornerRadius4.All(circular ? 46f : Radii.Card), ClipToBounds = true, Shadow = Elevation.Card,
                Children = [Surfaces.Artwork(img, seed & 0x7fffffff, 92f, 92f, circular ? 46f : Radii.Card, decodePx: 256)] },
            WaveeType.PageHero(name) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center,
                Children =
                [
                    TypePill(type),
                    new BoxEl { Grow = 1f },
                    new BoxEl { Width = 44f, Height = 44f, Corners = CornerRadius4.All(22f), Fill = Tok.AccentDefault,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shadow = Elevation.Card,
                        HoverScale = 1.06f, PressScale = 0.94f, OnClick = play,
                        Children = [Icon(Icons.Play, 16f, Tok.TextOnAccentPrimary)] },
                ],
            },
        ],
    };

    // ── songs (the All-view right column) — the SAME shared track cell as the detail/library lists, capped to 4 rows. ──
    static Element SongsSection(IReadOnlyList<Track> tracks, Action<Track> playTrack, Action<string, string?> go) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S,
        Children =
        [
            WaveeType.RailHeader(Loc.Get(Strings.Search.Songs)),
            Ctx.Provide(SearchSongs.Props, new SearchSongs.Model(tracks, playTrack, go, 4, new SelectionModel()),
                Embed.Comp(() => new SearchSongs()) with { SkeletonProxy = () => SearchSongs.SkeletonShape(tracks, 4) }),
        ],
    };

    // ── shelves & states ─────────────────────────────────────────────────────────────────────────────────
    static Element Shelf(string title, int count, Func<int, float, Element> cardAt) => new BoxEl
    {
        Direction = 1,
        Children = [PagedShelf.Create(count, cardAt: cardAt, measured: true, header: WaveeType.RailHeader(title))],
    };

    static Element Centered(string glyph, string title, string sub) => new BoxEl
    {
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.XL, Spacing.XXL, Spacing.XL, Spacing.XXL),
        Children =
        [
            Icon(glyph, 40f, Tok.TextTertiary),
            WaveeType.PageHero(title),
            new TextEl(sub) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MaxWidth = 440f },
        ],
    };
}

// Search track rows — the All-view "Songs" preview (capped) and the dedicated "Songs" tab (full). A Component so the rows
// re-skin live on play/like: it renders the SAME shared TrackRow cell as the detail + library lists (number↔play/pause on
// hover, the now-playing equalizer, the per-row heart, art + artist subline, duration), just eager (no virtualization) and
// single-click-to-play, since search lists are short. Columns: [#↔play, ♥, art, title+artist, duration].
sealed class SearchSongs : Component
{
    internal sealed record Model(IReadOnlyList<Track> Tracks, Action<Track> PlayTrack, Action<string, string?> Go, int Max, SelectionModel Selection);
    internal static readonly Context<Model?> Props = new(null);

    static readonly ColumnSet Cols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: true);
    static readonly TrackSize[] Columns =
        [TrackSize.Px(36f), TrackSize.Px(40f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(), TrackSize.Px(52f), TrackSize.Px(40f)];   // trailing 40px = the "…" overflow lane
    const float RowContentH = 56f;
    const float RowExtent = 60f;
    readonly SwipeGroup _swipeGroup = new();

    // transitions.dev texts-reveal for the bound Songs list: per-slot mount Enter (rise + fade + blur) with a baked
    // per-index delay. Only the first viewport staggers; slots realized later by scrolling mount unanimated (the
    // detail list's accepted behavior for virtualized entrances).
    static readonly LayoutTransition RowRise = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Slow, Easing.SmoothOut),
        Enter: new EnterExit(Dy: 8f, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall));
    const int StaggerRowCap = 12;

    public override Element Render()
    {
        var model = UseContext(Props);
        if (model is null) return new BoxEl();
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);      // row context menus (selection-aware TrackContextMenu)
        var menuOverlay = UseContext(Overlay.Service);
        var tracks = model.Tracks;
        int n = Math.Min(model.Max, tracks.Count);
        if (n <= 0) return new BoxEl();
        Func<bool> showChecks = () =>
        {
            // 2+ only: a plain single click must not flip the list into checkbox mode.
            _ = model.Selection.Version.Value;
            return model.Selection.SelectedCount > 1;
        };
        return ItemsView.CreateBound(
            n,
            // transitions.dev texts-reveal for the Songs tab: every committed query remounts this whole subtree (the
            // Skel branch replace), so each realized slot's Enter plays ONCE with a per-index stagger delay; scroll
            // recycling re-binds slots without remounting → no replay mid-scroll. A WRAPPER carries the Enter (not
            // `AccentPill with { Animate }` — its root has a bound Opacity that would fight an Enter opacity track).
            scope =>
            {
                int slot0 = scope.Index.Peek();   // the slot's initial item index at realize
                var wrapper = new BoxEl
                {
                    Direction = 1, Corners = CornerRadius4.All(6f), ClipToBounds = true,
                    Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Animate = slot0 < StaggerRowCap && !Motion.ReducedMotion
                        ? RowRise with { DelayMs = slot0 * Expressive.Stagger }
                        : (LayoutTransition?)null,
                    Children = [SelectorVisualsBound.AccentPill(scope, Embed.Comp(() => new SearchSongRow(model, scope, bridge, lib)), showChecks)],
                };
                // Right-click / long-press: the selection-aware track menu (Explorer semantics — inside a multi-
                // selection acts on all of it; outside collapses to the clicked row). No playlist host here.
                var row = wrapper;
                if (acts is { } a)
                    row = row.WithContextMenu(menuOverlay, () => TrackContextMenu.Build(
                        a, model.Selection, i => (uint)i < (uint)Math.Min(model.Max, model.Tracks.Count) ? model.Tracks[i] : null,
                        scope.Index.Peek(), static () => null));
                Element result = row;
                if (acts is { } swipeActs)
                    result = RowSwipe.WrapBound(result, () =>
                    {
                        int i = scope.Index.Peek();
                        int count = Math.Min(model.Max, model.Tracks.Count);
                        return (uint)i < (uint)count
                            ? new ActionContext(ActionTarget.ForTracks(new[] { model.Tracks[i] }), swipeActs)
                            : null;
                    }, _swipeGroup, TrackActions.ToggleLike, TrackActions.AddToQueue, scope.Index);
                return result;
            },
            RepeatLayout.Stack(RowExtent),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.Extended,
                Selection = model.Selection,
                IsItemInvokedEnabled = true,
                OnInvoked = i =>
                {
                    if ((uint)i >= (uint)n) return;
                    var t = tracks[i];
                    TrackRow.Invoke(bridge, t, () => model.PlayTrack(t));
                },
                ItemText = i => (uint)i < (uint)n ? tracks[i].Title : "",
                Grow = 0f,
                Scroll = new ScrollOptions { OnScrollGeometryChanged = (g => _swipeGroup.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipeGroup.Close()) },
            });
    }

    sealed class SearchSongRow : Component
    {
        readonly Model _model;
        readonly RowScope _scope;
        readonly PlaybackBridge? _bridge;
        readonly LibraryBridge? _lib;
        public SearchSongRow(Model model, RowScope scope, PlaybackBridge? bridge, LibraryBridge? lib)
        { _model = model; _scope = scope; _bridge = bridge; _lib = lib; }

        public override Element Render()
        {
            var likePrev = UseRef(((string?)null, false));   // hook BEFORE the early return (stable order) — like-edge memory
            int i = _scope.Index.Value;
            int n = Math.Min(_model.Max, _model.Tracks.Count);
            if ((uint)i >= (uint)n) return new BoxEl();
            var t = _model.Tracks[i];
            var st = TrackRow.StateOf(_bridge, _lib, t);
            bool likePop = TrackRow.LikeEdge(likePrev, t.Uri, st.Saved);   // pop only on the SAME-uri unsaved→saved edge
            Element title = new TextEl(t.Title)
            {
                Size = 14f,
                Weight = 600,
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Wrap = TextWrap.NoWrap,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            };
            return TrackRow.Grid(t, i, st, Cols, Columns, RowContentH, title, showTrackArtist: true, _model.Go,
                onPlay: () => TrackRow.Invoke(_bridge, t, () => _model.PlayTrack(t)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null,
                likePop: likePop,
                // Trailing "…" opens the row's context menu (the .WithContextMenu wrapper at the slot) via ClickRequestsContext.
                actionsCell: TrackRow.MoreButton(true));
        }
    }

    // The skeleton shape the deriver walks (SkeletonProxy at the Embed.Comp site): a few real TrackRow rows with no-op
    // handlers so the search-songs list shimmers as rows instead of one bar.
    public static Element SkeletonShape(IReadOnlyList<Track> tracks, int max)
    {
        int n = Math.Min(Math.Min(max, tracks.Count), 6);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
            rows[i] = TrackRow.Row(tracks[i], i, new TrackRow.State(false, false, false, IsTop: false, Saved: false),
                                   Cols, Columns, RowContentH, showTrackArtist: true, static (_, _) => { },
                                   onPlay: static () => { }, onLike: null,
                                   actionsCell: TrackRow.MoreButton(false));   // reserve the "…" lane so the shimmer matches the live rows
        return new BoxEl { Direction = 1, Children = rows };
    }
}

// The "All" tab body — the modern Spotify layout: a FULL-WIDTH Top Result card, then a unified "best results" list that
// interleaves songs and artists (each with a type chip + a per-row action: save a song ♥ / follow an artist). A Component
// so the row actions re-skin live on save/follow.
sealed class SearchAllList : Component
{
    internal sealed record Model(
        SearchResults Results,
        Action<string, string?> Go,
        Action<string> PlayTrack,
        Action<string> PlayContext,
        Action<Track> PlayKnownTrack,
        Func<SearchTopHit, bool>? Filter = null,
        string? EmptyTitle = null,
        // An EXPLICIT hit list (a dedicated facet's own results). Takes precedence over Filter, which only ever slices
        // the All-tab top hits.
        IReadOnlyList<SearchTopHit>? Hits = null);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var model = UseContext(Props);
        if (model is null) return new BoxEl();
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);      // row context menus (Menus.Card / Menus.TrackAttach)
        var menuOverlay = UseContext(Overlay.Service);
        if (model.Hits is { } explicitHits)
            return BuildHits(explicitHits, lib, model, model.EmptyTitle ?? Loc.Get(Strings.Search.NoResults), acts, menuOverlay);
        return model.Filter is { } filter
            ? BuildFiltered(model.Results, lib, model, filter, model.EmptyTitle ?? Loc.Get(Strings.Search.NoResults), acts, menuOverlay)
            : Build(model.Results, lib, model, acts, menuOverlay);
    }

    // A uri-only card menu (top hits carry uri + name, no domain object); null when the action system isn't provided.
    static MenuAttach? CardMenu(ActionServices? acts, IOverlayService? overlay, string uri, string name,
        Image? image = null, string? subtitle = null, bool circular = false)
        => acts is null || overlay is null
            ? null
            : Menus.CardAttach(acts, overlay, uri, name, image, subtitle, circular);

    // A full-track menu (fallback rows DO carry the Track — album/artist rows included).
    static MenuAttach? TrackMenu(ActionServices? acts, IOverlayService? overlay, Track t)
        => acts is null || overlay is null ? null : Menus.TrackAttach(acts, overlay, t);

    internal static Element Build(SearchResults r, LibraryBridge? lib, Model model,
                                  ActionServices? acts = null, IOverlayService? menuOverlay = null)
    {

        // Spotify's unified "All" tab: render topResultsV2.itemsV2 IN ORDER — the FIRST item is the Top Result (the `large`
        // hero skin), the rest a flat list of mixed types. EVERY row is the SAME MediaCard.Row factory — the shared
        // now-playing/play affordance (NowPlayingOverlay; the home of a future context menu) — differing only by skin + the
        // search extras (eyebrow label, type chip, save/follow trailing).
        var hits = r.TopHits;
        if (hits is { Count: > 0 })
        {
            var rows = new List<Element>(hits.Count);
            rows.Add(HitRow(hits[0], lib, model, large: true, acts, menuOverlay));
            for (int i = 1; i < hits.Count; i++) rows.Add(HitRow(hits[i], lib, model, large: false, acts, menuOverlay));
            return new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows.ToArray() };
        }

        var fallback = FallbackRows(r, lib, model, acts, menuOverlay);
        if (fallback.Count > 0)
            return new BoxEl { Direction = 1, Gap = Spacing.S, Children = fallback.ToArray() };

        // No unified top-results and no facet rows.
        return EmptyState.Build(Loc.Get(Strings.Search.NoResults), glyph: Icons.Search);
    }

    internal static Element BuildFiltered(SearchResults r, LibraryBridge? lib, Model model, Func<SearchTopHit, bool> include, string emptyTitle,
                                          ActionServices? acts = null, IOverlayService? menuOverlay = null)
        => BuildHits(r.TopHits?.Where(include).ToArray() ?? Array.Empty<SearchTopHit>(), lib, model, emptyTitle, acts, menuOverlay);

    /// <summary>Render a hit list through the shared row factory — one code path for the All tab's filtered slice and
    /// for a dedicated facet's own results, so a podcast row is identical wherever it came from.</summary>
    internal static Element BuildHits(IReadOnlyList<SearchTopHit> hits, LibraryBridge? lib, Model model, string emptyTitle,
                                      ActionServices? acts = null, IOverlayService? menuOverlay = null)
    {
        if (hits.Count == 0) return EmptyState.Build(emptyTitle, glyph: Icons.Search);
        var rows = new Element[hits.Count];
        for (int i = 0; i < hits.Count; i++)
            rows[i] = HitRow(hits[i], lib, model, large: false, acts, menuOverlay);
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows };
    }

    // ── every row is MediaCard.Row (the shared factory); these supply the per-kind data + actions only ───────────────────
    static Element HitRow(SearchTopHit h, LibraryBridge? lib, Model model, bool large,
                          ActionServices? acts = null, IOverlayService? menuOverlay = null)
    {
        bool isTrack = h.Kind == SearchHitKind.Track;
        Element? trailing =
            h.Followable ? FollowButton(lib?.IsSaved(h.Uri) ?? false, () => lib?.ToggleSaved(h.Uri, h.Name))
            : isTrack ? SaveButton(lib?.IsSaved(h.Uri) ?? false, () => { if (h.Uri.Length > 0) lib?.ToggleSaved(h.Uri, h.Name); })
            : null;
        Action play = isTrack ? () => model.PlayTrack(h.Uri) : () => model.PlayContext(h.Uri);
        Action open = isTrack ? play : OpenFor(model, h.Kind, h.Uri, h.Name);
        string? eyebrow = large ? null : (h.MatchedLyrics ? "Lyrics match" : h.AccessLabel);
        bool isPremiumEyebrow = !h.MatchedLyrics && h.AccessLabel is { Length: > 0 };
        return MediaCard.Row(h.Image, h.Name, h.Subtitle, h.Uri, h.RoundImage, open, play,
            eyebrow: eyebrow,
            eyebrowColor: isPremiumEyebrow ? WaveeColors.PremiumText : Tok.AccentTextPrimary,
            typeChip: large ? null : h.TypeLabel, detail: large ? null : h.Detail, trailing: trailing, large: large,
            meta: large ? null : h.Meta, detailBelowArt: h.Kind == SearchHitKind.Audiobook,
            onSubtitleNav: key => model.Go(key, null),   // artist/album names in the subtitle are individually clickable
            menu: CardMenu(acts, menuOverlay, h.Uri, h.Name, h.Image, h.Subtitle, h.RoundImage));
    }

    static List<Element> FallbackRows(SearchResults r, LibraryBridge? lib, Model model,
                                      ActionServices? acts = null, IOverlayService? menuOverlay = null)
    {
        var rows = new List<Element>(Math.Min(r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count, 18));

        bool topIsArtist = r.Artists.Count > 0;
        bool topIsAlbum = !topIsArtist && r.Albums.Count > 0;
        bool topIsPlaylist = !topIsArtist && !topIsAlbum && r.Playlists.Count > 0;

        if (topIsArtist) rows.Add(ArtistRow(r.Artists[0], lib, model, large: true, acts, menuOverlay));
        else if (topIsAlbum) rows.Add(AlbumRow(r.Albums[0], model, large: true, acts, menuOverlay));
        else if (topIsPlaylist) rows.Add(PlaylistRow(r.Playlists[0], model, large: true, acts, menuOverlay));

        int artistIndex = topIsArtist ? 1 : 0;
        int trackCount = Math.Min(r.Tracks.Count, 8);
        for (int i = 0; i < trackCount; i++)
        {
            rows.Add(TrackRowFb(r.Tracks[i], lib, model, acts, menuOverlay));
            if ((i == 2 || i == 5) && artistIndex < r.Artists.Count)
                rows.Add(ArtistRow(r.Artists[artistIndex++], lib, model, large: false, acts, menuOverlay));
        }

        while (artistIndex < r.Artists.Count && rows.Count < 14)
            rows.Add(ArtistRow(r.Artists[artistIndex++], lib, model, large: false, acts, menuOverlay));

        int albumStart = topIsAlbum ? 1 : 0;
        for (int i = albumStart; i < r.Albums.Count && i < albumStart + 4; i++)
            rows.Add(AlbumRow(r.Albums[i], model, large: false, acts, menuOverlay));

        int playlistStart = topIsPlaylist ? 1 : 0;
        for (int i = playlistStart; i < r.Playlists.Count && i < playlistStart + 4; i++)
            rows.Add(PlaylistRow(r.Playlists[i], model, large: false, acts, menuOverlay));

        return rows;
    }

    static Element TrackRowFb(Track t, LibraryBridge? lib, Model model,
                              ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        t.Image, t.Title, (VideoPresence.HasVideo(t) ? "Music video" : "Song") + " • " + Names(t.Artists), t.Uri, false,
        () => model.PlayKnownTrack(t), () => model.PlayKnownTrack(t), typeChip: "Song",
        trailing: SaveButton(t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false), () => { if (t.Uri.Length > 0) lib?.ToggleSaved(t.Uri, t.Title); }),
        menu: TrackMenu(acts, menuOverlay, t));

    static Element ArtistRow(Artist a, LibraryBridge? lib, Model model, bool large,
                             ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        a.Image, a.Name, Loc.Get(Strings.Search.TypeArtist), a.Uri, true,
        () => model.Go("artist:" + a.Uri, a.Name), () => model.PlayContext(a.Uri),
        typeChip: large ? null : Loc.Get(Strings.Search.TypeArtist),
        trailing: FollowButton(lib?.IsSaved(a.Uri) ?? false, () => lib?.ToggleSaved(a.Uri, a.Name)), large: large,
        menu: CardMenu(acts, menuOverlay, a.Uri, a.Name, a.Image, Loc.Get(Strings.Search.TypeArtist), circular: true));

    static Element AlbumRow(Album a, Model model, bool large,
                            ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        a.Cover, a.Name, Loc.Get(Strings.Search.TypeAlbum) + (a.Artists.Count > 0 ? " • " + a.Artists[0].Name : ""), a.Uri, false,
        () => model.Go("album:" + a.Uri, a.Name), () => model.PlayContext(a.Uri),
        typeChip: large ? null : Loc.Get(Strings.Search.TypeAlbum), large: large,
        menu: CardMenu(acts, menuOverlay, a.Uri, a.Name, a.Cover,
            a.Artists.Count > 0 ? a.Artists[0].Name : Loc.Get(Strings.Search.TypeAlbum)));

    static Element PlaylistRow(Playlist p, Model model, bool large,
                               ActionServices? acts = null, IOverlayService? menuOverlay = null) => MediaCard.Row(
        p.Cover, p.Name, Loc.Get(Strings.Search.TypePlaylist), p.Uri, false,
        () => model.Go("pl:" + p.Uri, p.Name), () => model.PlayContext(p.Uri),
        typeChip: large ? null : Loc.Get(Strings.Search.TypePlaylist), large: large,
        menu: CardMenu(acts, menuOverlay, p.Uri, p.Name, p.Cover, Loc.Get(Strings.Search.TypePlaylist)));

    static Action OpenFor(Model model, SearchHitKind kind, string uri, string name) => kind switch
    {
        SearchHitKind.Artist => () => model.Go("artist:" + uri, name),
        SearchHitKind.Album => () => model.Go("album:" + uri, name),
        SearchHitKind.Playlist => () => model.Go("pl:" + uri, name),
        SearchHitKind.Audiobook or SearchHitKind.Podcast => () => model.Go("show:" + uri, name),
        _ => static () => { },
    };

    static Element SaveButton(bool saved, Action toggle) => new BoxEl
    {
        Width = 32f, Height = 32f, Shrink = 0f, Corners = CornerRadius4.All(16f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverFill = Tok.FillSubtleSecondary, HoverScale = 1.1f, OnClick = toggle,
        Children = [Icon(saved ? Icons.Accept : Icons.Add, 16f, saved ? Tok.AccentDefault : Tok.TextSecondary)],
    };

    static Element FollowButton(bool following, Action toggle) => new BoxEl
    {
        Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(16f, 6f, 16f, 6f), Corners = CornerRadius4.All(16f),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HoverFill = Tok.FillSubtleSecondary, HoverScale = 1.04f, OnClick = toggle,
        Children = [new TextEl(following ? "Following" : "Follow") { Size = 12f, Weight = 700, Color = Tok.TextPrimary }],
    };

    static string Names(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count && i < 3; i++) { if (i > 0) sb.Append(", "); sb.Append(artists[i].Name); }
        return sb.ToString();
    }
}
