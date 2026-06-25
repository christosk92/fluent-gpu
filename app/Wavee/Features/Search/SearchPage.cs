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

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var querySig = UseContext(SearchQuery.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };
        this.UseSoftReveal(dy: 0f, blur: 0f);

        string q = (querySig?.Value ?? "").Trim();          // subscribe → re-render + re-search as the user types
        var results = UseAsyncResource(ct => svc.Library.SearchAsync(q, ct), FakeData.SearchSeed, q);   // seed renders the loading shape; Skel.Region derives the shimmer from it
        int chip = _chip.Value;                             // subscribe

        // Scroll-position restoration keyed by the query: each distinct query has its own remembered scroll (a new query
        // starts at the top; returning to a prior query restores it). One ScrollView node serves every query in place.
        if (q.Length == 0)
            return ScrollView(BrowseAll(querySig)) with { Grow = 1f, ScrollKey = "search:" };

        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.L,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [ChipBar(chip), Skel.Region(results, SearchShimmer, r => ResultsFor(r, chip, q, svc, go), onFailed: () => ErrorState.Build(results.Error))],
        }) with { Grow = 1f, ScrollKey = "search:" + q };
    }

    // Lightweight loading skeleton (finding #7): a fixed list of result-row placeholders so the pending edge doesn't build
    // the full results tree just to derive a skeleton. Sized childless boxes → shimmer bars; SmoothResize eases the swap.
    static Element SearchShimmer()
    {
        var rows = new Element[10];
        for (int i = 0; i < rows.Length; i++) rows[i] = new BoxEl { Height = 48f, Corners = CornerRadius4.All(WaveeRadius.Control) };
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = rows };
    }

    Element ChipBar(int chip) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center,
        Children = [SelectorBar.Create(ChipLabels(), chip, i => _chip.Value = i)],
    };

    static string[] ChipLabels() =>
    [
        Loc.Get(Strings.Search.All), Loc.Get(Strings.Search.Songs), Loc.Get(Strings.Search.Artists),
        Loc.Get(Strings.Search.Albums), Loc.Get(Strings.Search.Playlists),
    ];

    // ── results dispatch (All composite vs a flat per-type list) ──
    Element ResultsFor(SearchResults r, int chip, string q, Services svc, Action<string, string?> go)
    {
        if (r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count == 0)
            return Centered(Icons.Search, Loc.Get(Strings.Search.NoResults), Strings.Search.NoResultsSub(q));

        void Play(string uri) => _ = svc.Player.PlayAsync(uri, 0);
        void PlayTrack(string uri) => _ = svc.Player.PlayTrackAsync(uri);

        return chip switch
        {
            1 => Embed.Comp(() => new SearchSongs(r.Tracks, PlayTrack, go))
                 with { SkeletonProxy = () => SearchSongs.SkeletonShape(r.Tracks, int.MaxValue) },   // SHARED track cell (hover transport + now-playing + heart)
            2 => FlatList(r.Artists.Select(a => ResultRow(a.Image, a.Id.GetHashCode(), a.Name, Loc.Get(Strings.Search.TypeArtist), Loc.Get(Strings.Search.TypeArtist), true, () => go("artist:" + a.Uri, a.Name)))),
            3 => FlatList(r.Albums.Select(a => ResultRow(a.Cover, a.Id.GetHashCode(), a.Name, a.Artists.Count > 0 ? a.Artists[0].Name : "", Loc.Get(Strings.Search.TypeAlbum), false, () => go("album:" + a.Uri, a.Name)))),
            4 => FlatList(r.Playlists.Select(p => ResultRow(p.Cover, p.Id.GetHashCode(), p.Name, p.OwnerName, Loc.Get(Strings.Search.TypePlaylist), false, () => go("pl:" + p.Uri, p.Name)))),
            _ => AllView(r, go, Play, PlayTrack),
        };
    }

    Element AllView(SearchResults r, Action<string, string?> go, Action<string> play, Action<string> playTrack)
    {
        var sections = new List<Element>(5);

        Element? top = TopResult(r, go, play);
        Element? songs = r.Tracks.Count > 0 ? SongsSection(r.Tracks, playTrack, go) : null;
        if (top is not null || songs is not null)
            sections.Add(Responsive.Of(w =>
            {
                bool wide = w >= 720f;
                var cols = new List<Element>(2);
                if (top is not null) cols.Add(new BoxEl { Direction = 1, Shrink = 0f, Width = wide ? 360f : float.NaN, Grow = wide ? 0f : 1f, Children = [top] });
                if (songs is not null) cols.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Children = [songs] });
                return new BoxEl { Direction = (byte)(wide ? 0 : 1), Gap = WaveeSpace.XL, Children = cols.ToArray() };
            }, fallback: 900f));

        if (r.Artists.Count > 0)
            sections.Add(Shelf(Loc.Get(Strings.Search.Artists), r.Artists.Count, (i, w) =>
                MediaCard.Shelf(r.Artists[i].Image, r.Artists[i].Name, Loc.Get(Strings.Search.TypeArtist), r.Artists[i].Uri,
                    () => go("artist:" + r.Artists[i].Uri, r.Artists[i].Name), () => play(r.Artists[i].Uri), w, circular: true)));
        if (r.Albums.Count > 0)
            sections.Add(Shelf(Loc.Get(Strings.Search.Albums), r.Albums.Count, (i, w) =>
                MediaCard.Shelf(r.Albums[i].Cover, r.Albums[i].Name, r.Albums[i].Year > 0 ? r.Albums[i].Year.ToString() : Loc.Get(Strings.Search.TypeAlbum),
                    r.Albums[i].Uri, () => go("album:" + r.Albums[i].Uri, r.Albums[i].Name), () => play(r.Albums[i].Uri), w)));
        if (r.Playlists.Count > 0)
            sections.Add(Shelf(Loc.Get(Strings.Search.Playlists), r.Playlists.Count, (i, w) =>
                MediaCard.Shelf(r.Playlists[i].Cover, r.Playlists[i].Name, r.Playlists[i].OwnerName, r.Playlists[i].Uri,
                    () => go("pl:" + r.Playlists[i].Uri, r.Playlists[i].Name), () => play(r.Playlists[i].Uri), w)));

        return new BoxEl { Direction = 1, Gap = WaveeSpace.XL, Children = sections.ToArray() };
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
    static Element SongsSection(IReadOnlyList<Track> tracks, Action<string> playTrack, Action<string, string?> go) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S,
        Children = [WaveeType.RailHeader(Loc.Get(Strings.Search.Songs)), Embed.Comp(() => new SearchSongs(tracks, playTrack, go, 4)) with { SkeletonProxy = () => SearchSongs.SkeletonShape(tracks, 4) }],
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
    readonly IReadOnlyList<Track> _tracks;
    readonly Action<string> _playTrack;
    readonly Action<string, string?> _go;
    readonly int _max;
    public SearchSongs(IReadOnlyList<Track> tracks, Action<string> playTrack, Action<string, string?> go, int max = int.MaxValue)
    { _tracks = tracks; _playTrack = playTrack; _go = go; _max = max; }

    static readonly ColumnSet Cols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: true);
    static readonly TrackSize[] Columns =
        [TrackSize.Px(36f), TrackSize.Px(40f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(), TrackSize.Px(64f)];

    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var cur = bridge?.CurrentTrack.Value;            // subscribe → now-playing equalizer
        bool playing = bridge?.IsPlaying.Value ?? false;
        bool buffering = bridge?.IsBuffering.Value ?? false;
        int n = Math.Min(_max, _tracks.Count);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var t = _tracks[i];
            bool isNow = cur is not null && cur.Id == t.Id;
            var st = new TrackRow.State(isNow, isNow && playing, isNow && buffering, IsTop: false,
                                        Saved: t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false));   // subscribe → heart re-skins on toggle
            rows[i] = TrackRow.Row(t, i, st, Cols, Columns, 56f, showTrackArtist: true, _go,
                onPlay: () => PlayRow(bridge, t),
                onLike: t.Uri.Length > 0 ? () => lib?.ToggleSaved(t.Uri) : null);
        }
        return new BoxEl { Direction = 1, Children = rows };
    }

    void PlayRow(PlaybackBridge? bridge, Track t)
    {
        if (bridge is not null && bridge.CurrentTrack.Peek()?.Id == t.Id)
        {
            bool p = bridge.IsPlaying.Peek();
            bridge.IsPlaying.Value = !p;
            if (p) _ = bridge.Player.PauseAsync(); else _ = bridge.Player.ResumeAsync();
            return;
        }
        _playTrack(t.Uri);
    }

    // The skeleton shape the deriver walks (SkeletonProxy at the Embed.Comp site): a few real TrackRow rows with no-op
    // handlers so the search-songs list shimmers as rows instead of one bar.
    public static Element SkeletonShape(IReadOnlyList<Track> tracks, int max)
    {
        int n = Math.Min(Math.Min(max, tracks.Count), 6);
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
            rows[i] = TrackRow.Row(tracks[i], i, new TrackRow.State(false, false, false, IsTop: false, Saved: false),
                                   Cols, Columns, 56f, showTrackArtist: true, static (_, _) => { },
                                   onPlay: static () => { }, onLike: null);
        return new BoxEl { Direction = 1, Children = rows };
    }
}
