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
        this.UseSoftReveal(dy: 0f, blur: 0f);

        string q = (querySig?.Value ?? "").Trim();          // subscribe → re-render + re-search as the user types
        int chip = _chip.Value;                             // subscribe
        UseEffect(() => _songsSel.ClearSelection(), q + ":" + chip);
        var facet = RequestFacetFor(chip);
        var results = UseAsyncResource(ct => svc.Library.SearchAsync(q, facet, 0, SearchPageSize, ct), SearchResults.Empty, q, chip);   // selected tab drives the live facet op

        // Scroll-position restoration keyed by the query: each distinct query has its own remembered scroll (a new query
        // starts at the top; returning to a prior query restores it). One ScrollView node serves every query in place.
        if (q.Length == 0)
            return ScrollView(BrowseAll(querySig)) with { Grow = 1f, ScrollKey = "search:" };

        var resultBody = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.S, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [Skel.Region(results, SearchShimmer, r => ResultsFor(r, chip, q, svc, go),
                reveal: SkelReveal.StaggerRows,
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
                    Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.S),
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
                Corners = CornerRadius4.All(WaveeRadius.Control),
                Fill = Tok.FillSubtleSecondary,
            };
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, AlignSelf = FlexAlign.Stretch, Children = rows };
    }

    Element ChipBar(int chip) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center,
        Children = [SelectorBar.Create(ChipLabels(), chip, i => _chip.Value = i)],
    };

    static string[] ChipLabels() =>
    [
        Loc.Get(Strings.Search.All), Loc.Get(Strings.Search.Songs), Loc.Get(Strings.Search.Albums),
        Loc.Get(Strings.Search.Playlists), "Audiobooks", "Podcasts & Shows", Loc.Get(Strings.Search.Artists),
    ];

    static SearchFacet RequestFacetFor(int chip) => chip switch
    {
        // We do not have dedicated captured Pathfinder ops for these yet. Query All and filter the unified top hits.
        4 or 5 => SearchFacet.All,
        _ => FacetFor(chip),
    };

    static SearchFacet FacetFor(int chip) => chip switch
    {
        1 => SearchFacet.Tracks,
        2 => SearchFacet.Albums,
        3 => SearchFacet.Playlists,
        4 => SearchFacet.Audiobooks,
        5 => SearchFacet.Podcasts,
        6 => SearchFacet.Artists,
        _ => SearchFacet.All,
    };

    // ── results dispatch (All composite vs a flat per-type list) ──
    Element ResultsFor(SearchResults r, int chip, string q, Services svc, Action<string, string?> go)
    {
        void Play(string uri) => _ = svc.Player.PlayAsync(uri, 0);
        void PlayTrack(string uri) => _ = svc.Player.PlayTrackAsync(uri);
        void PlayKnownTrack(Track track) => _ = svc.Player.PlayTrackAsync(track);

        if (chip == 4) return TopHitList(r, h => h.Kind == SearchHitKind.Audiobook, "No audiobook results", go, Play, PlayTrack, PlayKnownTrack);
        if (chip == 5) return TopHitList(r, h => h.Kind is SearchHitKind.Podcast or SearchHitKind.Episode, "No podcast results", go, Play, PlayTrack, PlayKnownTrack);

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

    Element SongsList(IReadOnlyList<Track> tracks, Action<Track> playTrack, Action<string, string?> go, int max)
    {
        _songsTracks = tracks;
        return Ctx.Provide(SearchSongs.Props, new SearchSongs.Model(tracks, playTrack, go, max, _songsSel),
            Embed.Comp(() => new SearchSongs()) with { SkeletonProxy = () => SearchSongs.SkeletonShape(tracks, max) });
    }

    // ── flat unified results list (per chip) ──
    static Element FlatList(IEnumerable<Element> rows) => new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() };

    static Element ResultRow(Image? cover, int seed, string title, string subtitle, string type, bool circular, Action open) => new BoxEl
    {
        Direction = 0, Height = 60f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Corners = CornerRadius4.All(6f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = open,
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

    // ── browse-all empty state (a grid of category tiles; tapping searches that category) ──
    static readonly string[] Categories =
        ["Pop", "Hip-Hop", "Rock", "Dance / Electronic", "Chill", "Focus", "Workout", "Indie", "Jazz", "R&B", "Classical", "Sleep"];
    static readonly (byte R, byte G, byte B)[] CatRgb =
        [(0xE1, 0x3A, 0x5A), (0x2E, 0x6C, 0xE0), (0x6A, 0x2D, 0x6A), (0x1E, 0x5F, 0x4F), (0xB5, 0x53, 0x2A), (0x24, 0x50, 0x6B), (0x7A, 0x5A, 0x2E), (0x2A, 0x6F, 0x5A)];
    static ColorF CatColor(int i) { var (r, g, b) = CatRgb[i % CatRgb.Length]; return ColorF.FromRgba(r, g, b); }

    static Element BrowseAll(Signal<string>? querySig)
    {
        var cards = new Element[Categories.Length];
        for (int i = 0; i < Categories.Length; i++)
        {
            int idx = i;
            cards[i] = CategoryCard(Categories[i], i, () => { if (querySig is not null) querySig.Value = Categories[idx]; });
        }
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [WaveeType.PageHero(Loc.Get(Strings.Search.BrowseAll)), AutoGrid(200f, WaveeSpace.M, 104f, cards)],
        };
    }

    static Element CategoryCard(string name, int i, Action open) => new BoxEl
    {
        Height = 104f, Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
        Gradient = LinearGradient(135f, new GradientStop(0f, CatColor(i)), new GradientStop(1f, CatColor(i) with { A = 0.7f })),
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M),
        HoverScale = 1.02f, PressScale = 0.99f, OnClick = open,
        Children = [new TextEl(name) { Size = 18f, Weight = 800, Color = ColorF.FromRgba(255, 255, 255), MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis }],
    };

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
        Direction = 1, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
        HoverFill = Tok.FillCardDefault, OnClick = open,
        Children =
        [
            new BoxEl { Width = 92f, Height = 92f, Corners = CornerRadius4.All(circular ? 46f : WaveeRadius.Card), ClipToBounds = true, Shadow = Elevation.Card,
                Children = [Surfaces.Artwork(img, seed & 0x7fffffff, 92f, 92f, circular ? 46f : WaveeRadius.Card, decodePx: 256)] },
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
        Direction = 1, Gap = WaveeSpace.S,
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
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XXL, WaveeSpace.XL, WaveeSpace.XXL),
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
        [TrackSize.Px(36f), TrackSize.Px(40f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(), TrackSize.Px(64f)];
    const float RowContentH = 56f;
    const float RowExtent = 60f;

    public override Element Render()
    {
        var model = UseContext(Props);
        if (model is null) return new BoxEl();
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var tracks = model.Tracks;
        int n = Math.Min(model.Max, tracks.Count);
        if (n <= 0) return new BoxEl();
        Func<bool> showChecks = () =>
        {
            _ = model.Selection.Version.Value;
            return model.Selection.SelectedCount > 0;
        };
        return ItemsView.CreateBound(
            n,
            scope => SelectorVisualsBound.AccentPill(scope, Embed.Comp(() => new SearchSongRow(model, scope, bridge, lib)), showChecks),
            RepeatLayout.Stack(RowExtent),
            selectionMode: ItemsSelectionMode.Extended,
            selection: model.Selection,
            isItemInvokedEnabled: true,
            itemInvoked: i =>
            {
                if ((uint)i >= (uint)n) return;
                var t = tracks[i];
                TrackRow.Invoke(bridge, t, () => model.PlayTrack(t));
            },
            itemText: i => (uint)i < (uint)n ? tracks[i].Title : "",
            grow: 0f);
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
            int i = _scope.Index.Value;
            int n = Math.Min(_model.Max, _model.Tracks.Count);
            if ((uint)i >= (uint)n) return new BoxEl();
            var t = _model.Tracks[i];
            var st = TrackRow.StateOf(_bridge, _lib, t);
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
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri) : null);
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
                                   onPlay: static () => { }, onLike: null);
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
        string? EmptyTitle = null);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var model = UseContext(Props);
        if (model is null) return new BoxEl();
        var lib = UseContext(LibraryBridge.Slot);
        return model.Filter is { } filter
            ? BuildFiltered(model.Results, lib, model, filter, model.EmptyTitle ?? Loc.Get(Strings.Search.NoResults))
            : Build(model.Results, lib, model);
    }

    internal static Element Build(SearchResults r, LibraryBridge? lib, Model model)
    {

        // Spotify's unified "All" tab: render topResultsV2.itemsV2 IN ORDER — the FIRST item is the Top Result (the `large`
        // hero skin), the rest a flat list of mixed types. EVERY row is the SAME MediaCard.Row factory — the shared
        // now-playing/play affordance (NowPlayingOverlay; the home of a future context menu) — differing only by skin + the
        // search extras (eyebrow label, type chip, save/follow trailing).
        var hits = r.TopHits;
        if (hits is { Count: > 0 })
        {
            var rows = new List<Element>(hits.Count);
            rows.Add(HitRow(hits[0], lib, model, large: true));
            for (int i = 1; i < hits.Count; i++) rows.Add(HitRow(hits[i], lib, model, large: false));
            return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = rows.ToArray() };
        }

        var fallback = FallbackRows(r, lib, model);
        if (fallback.Count > 0)
            return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = fallback.ToArray() };

        // No unified top-results and no facet rows.
        return EmptyState.Build(Loc.Get(Strings.Search.NoResults), glyph: Icons.Search);
    }

    internal static Element BuildFiltered(SearchResults r, LibraryBridge? lib, Model model, Func<SearchTopHit, bool> include, string emptyTitle)
    {
        var hits = r.TopHits?.Where(include).ToArray() ?? Array.Empty<SearchTopHit>();
        if (hits.Length == 0) return EmptyState.Build(emptyTitle, glyph: Icons.Search);

        var rows = new Element[hits.Length];
        for (int i = 0; i < hits.Length; i++)
            rows[i] = HitRow(hits[i], lib, model, large: false);
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = rows };
    }

    // ── every row is MediaCard.Row (the shared factory); these supply the per-kind data + actions only ───────────────────
    static Element HitRow(SearchTopHit h, LibraryBridge? lib, Model model, bool large)
    {
        bool isTrack = h.Kind == SearchHitKind.Track;
        Element? trailing =
            h.Followable ? FollowButton(lib?.IsSaved(h.Uri) ?? false, () => lib?.ToggleSaved(h.Uri))
            : isTrack ? SaveButton(lib?.IsSaved(h.Uri) ?? false, () => { if (h.Uri.Length > 0) lib?.ToggleSaved(h.Uri); })
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
            onSubtitleNav: key => model.Go(key, null));   // artist/album names in the subtitle are individually clickable
    }

    static List<Element> FallbackRows(SearchResults r, LibraryBridge? lib, Model model)
    {
        var rows = new List<Element>(Math.Min(r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count, 18));

        bool topIsArtist = r.Artists.Count > 0;
        bool topIsAlbum = !topIsArtist && r.Albums.Count > 0;
        bool topIsPlaylist = !topIsArtist && !topIsAlbum && r.Playlists.Count > 0;

        if (topIsArtist) rows.Add(ArtistRow(r.Artists[0], lib, model, large: true));
        else if (topIsAlbum) rows.Add(AlbumRow(r.Albums[0], model, large: true));
        else if (topIsPlaylist) rows.Add(PlaylistRow(r.Playlists[0], model, large: true));

        int artistIndex = topIsArtist ? 1 : 0;
        int trackCount = Math.Min(r.Tracks.Count, 8);
        for (int i = 0; i < trackCount; i++)
        {
            rows.Add(TrackRowFb(r.Tracks[i], lib, model));
            if ((i == 2 || i == 5) && artistIndex < r.Artists.Count)
                rows.Add(ArtistRow(r.Artists[artistIndex++], lib, model, large: false));
        }

        while (artistIndex < r.Artists.Count && rows.Count < 14)
            rows.Add(ArtistRow(r.Artists[artistIndex++], lib, model, large: false));

        int albumStart = topIsAlbum ? 1 : 0;
        for (int i = albumStart; i < r.Albums.Count && i < albumStart + 4; i++)
            rows.Add(AlbumRow(r.Albums[i], model, large: false));

        int playlistStart = topIsPlaylist ? 1 : 0;
        for (int i = playlistStart; i < r.Playlists.Count && i < playlistStart + 4; i++)
            rows.Add(PlaylistRow(r.Playlists[i], model, large: false));

        return rows;
    }

    static Element TrackRowFb(Track t, LibraryBridge? lib, Model model) => MediaCard.Row(
        t.Image, t.Title, (t.HasVideo ? "Music video" : "Song") + " • " + Names(t.Artists), t.Uri, false,
        () => model.PlayKnownTrack(t), () => model.PlayKnownTrack(t), typeChip: "Song",
        trailing: SaveButton(t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false), () => { if (t.Uri.Length > 0) lib?.ToggleSaved(t.Uri); }));

    static Element ArtistRow(Artist a, LibraryBridge? lib, Model model, bool large) => MediaCard.Row(
        a.Image, a.Name, Loc.Get(Strings.Search.TypeArtist), a.Uri, true,
        () => model.Go("artist:" + a.Uri, a.Name), () => model.PlayContext(a.Uri),
        typeChip: large ? null : Loc.Get(Strings.Search.TypeArtist),
        trailing: FollowButton(lib?.IsSaved(a.Uri) ?? false, () => lib?.ToggleSaved(a.Uri)), large: large);

    static Element AlbumRow(Album a, Model model, bool large) => MediaCard.Row(
        a.Cover, a.Name, Loc.Get(Strings.Search.TypeAlbum) + (a.Artists.Count > 0 ? " • " + a.Artists[0].Name : ""), a.Uri, false,
        () => model.Go("album:" + a.Uri, a.Name), () => model.PlayContext(a.Uri),
        typeChip: large ? null : Loc.Get(Strings.Search.TypeAlbum), large: large);

    static Element PlaylistRow(Playlist p, Model model, bool large) => MediaCard.Row(
        p.Cover, p.Name, Loc.Get(Strings.Search.TypePlaylist), p.Uri, false,
        () => model.Go("pl:" + p.Uri, p.Name), () => model.PlayContext(p.Uri),
        typeChip: large ? null : Loc.Get(Strings.Search.TypePlaylist), large: large);

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
