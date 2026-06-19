using System;
using System.Linq;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Home landing page — a vertically SCROLLING composition whose section TYPES alternate so it never reads as endless
// rails: greeting → responsive quick-pick grid → playlists PagedShelf → featured spotlight → artists PagedShelf. Every
// section is size-reactive (clamped text, responsive grid, width-paged shelves) and uses the async skeleton pattern
// (UseAsyncResource + StatefulRegion → shimmer → reveal).
sealed class HomePage : Component
{
    public override Element Render()
    {
        var svc    = UseContext(Services.Slot);
        var go     = UseContext(HistoryStore.NavCtx);
        var bridge = UseContext(PlaybackBridge.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };

        var library   = UseAsyncResource(async ct => (await svc.Library.GetLibraryAsync(ct)).ToArray(),   Array.Empty<LibraryItem>());
        var playlists = UseAsyncResource(async ct => (await svc.Library.GetPlaylistsAsync(ct)).ToArray(), Array.Empty<PlaylistSummary>());
        var albums    = UseAsyncResource(async ct => (await svc.Library.GetAlbumsAsync(ct)).ToArray(),    Array.Empty<Album>());
        var artists   = UseAsyncResource(async ct => (await svc.Library.GetArtistsAsync(ct)).ToArray(),   Array.Empty<Artist>());

        string? name = bridge?.User.Value?.DisplayName;     // subscribe → greeting refreshes on login

        void Play(string uri) => _ = svc.Player.PlayAsync(uri, 0);
        void NavItem(LibraryItem it)
        {
            if (it.Uri.Contains("collection:tracks", StringComparison.Ordinal)) { go("liked", null); return; }
            switch (it.Kind)
            {
                case LibraryItemKind.Album:  go("album:" + it.Uri, it.Title); break;
                case LibraryItemKind.Artist: go("artist:" + it.Uri, it.Title); break;
                default:                     go("pl:" + it.Uri, it.Title); break;
            }
        }

        // 2 · Quick-pick grid (responsive AutoGrid; clamped tile text → no overflow) ─────────────────────
        Element quickPicks = StatefulRegion.Single(
            library,
            shimmer: () => QuickGrid(Enumerable.Range(0, 4).Select(_ => MediaCard.QuickPickSkeleton())),
            content: items => items.Length == 0
                ? EmptyState.Default()
                : QuickGrid(items.Take(6).Select(it =>
                    MediaCard.QuickPick(it.Image, it.Title, () => NavItem(it), () => Play(it.Uri)))));

        // 3 · "Made for you" — playlists PagedShelf (width-reactive + pager) ───────────────────────────
        Element madeFor = StatefulRegion.Single(
            playlists,
            shimmer: () => ShelfShimmer(false),
            content: arr => PagedShelf.Create(
                arr.Length,
                cardAt: (i, w) => MediaCard.Shelf(arr[i].Cover, arr[i].Name, arr[i].OwnerName,
                                                  () => go("pl:" + arr[i].Uri, arr[i].Name), () => Play(arr[i].Uri), w),
                cardHeight: MediaCard.ShelfHeight,
                header: WaveeType.RailHeader(Strings.Home.MadeFor(string.IsNullOrWhiteSpace(name) ? Loc.Get(Strings.Home.You) : name))));

        // 4 · Featured spotlight (full-width band — the calm break) ─────────────────────────────────────
        Element spotlight = StatefulRegion.Single(
            albums,
            shimmer: SpotlightSkeleton,
            content: arr => arr.Length == 0 ? new BoxEl() : Responsive.Of(w => Spotlight(arr[0], Play, w), fallback: 560f));

        // 4b · "New releases" — albums PagedShelf (every release kind: single / EP / album / compilation) ───────
        Element newReleases = StatefulRegion.Single(
            albums,
            shimmer: () => ShelfShimmer(false),
            content: arr => arr.Length == 0 ? new BoxEl() : PagedShelf.Create(
                arr.Length,
                cardAt: (i, w) => MediaCard.Shelf(arr[i].Cover, arr[i].Name, AlbumSub(arr[i]),
                                                  () => go("album:" + arr[i].Uri, arr[i].Name), () => Play(arr[i].Uri), w),
                cardHeight: MediaCard.ShelfHeight,
                header: WaveeType.RailHeader(Loc.Get(Strings.Home.NewReleases))));

        // 5 · "Popular artists" — circular PagedShelf ───────────────────────────────────────────────────
        Element popularArtists = StatefulRegion.Single(
            artists,
            shimmer: () => ShelfShimmer(true),
            content: arr => PagedShelf.Create(
                arr.Length,
                cardAt: (i, w) => MediaCard.Shelf(arr[i].Image, arr[i].Name, Loc.Get(Strings.Home.Artist),
                                                  () => go("artist:" + arr[i].Uri, arr[i].Name), () => Play(arr[i].Uri), w, circular: true),
                cardHeight: MediaCard.ShelfHeight,
                header: WaveeType.RailHeader(Loc.Get(Strings.Home.PopularArtists))));

        var page = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XL,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [ GreetingHero(name), quickPicks, madeFor, newReleases, spotlight, popularArtists ],
        };

        // The page scrolls vertically; `page` has no Grow so it sizes to content (taller than the viewport → scrolls).
        // The vertical viewport adopts the finite offered width (FlexLayout overflow-y), so each size-reactive section
        // self-measures the real content width itself (the PagedShelves internally, the Spotlight via Responsive) — no
        // page-level width broker.
        return ScrollView(page) with { Grow = 1f };
    }

    // ── greeting hero ────────────────────────────────────────────────────────────────────────────────
    static Element GreetingHero(string? name)
    {
        int h = DateTime.Now.Hour;
        string part = h < 5 ? Loc.Get(Strings.Home.GoodEvening)
                    : h < 12 ? Loc.Get(Strings.Home.GoodMorning)
                    : h < 18 ? Loc.Get(Strings.Home.GoodAfternoon)
                    : Loc.Get(Strings.Home.GoodEvening);
        string greet = string.IsNullOrWhiteSpace(name) ? part : Strings.Home.Greeting(part, name);
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XS, Padding = new Edges4(0f, WaveeSpace.S, 0f, 0f),
            Children = [ WaveeType.PageHero(greet), WaveeType.TrackMeta(Loc.Get(Strings.Home.OnRotation)) ],
        };
    }

    // ── featured spotlight (text clamped via MaxWidth so a long album name can't push the row wide) ─────
    static Element Spotlight(Album a, Action<string> play, float innerW)
    {
        // Clamp the text to the REAL remaining width (cover 168 + gaps + padding ≈ 216) so a long album name can't
        // push the band past the viewport at narrow widths. Falls back wide before the first measure.
        float TextMax = innerW > 1f ? MathF.Max(160f, innerW - 216f) : 560f;
        string sub = Strings.Home.SpotlightSub(a.Artists.Count > 0 ? a.Artists[0].Name : "", a.Year);
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
            Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Gradient = LinearGradient(110f,
                new GradientStop(0f, Tok.AccentDefault with { A = 0.22f }),
                new GradientStop(1f, Tok.FillCardSecondary)),
            Children =
            [
                new BoxEl
                {
                    Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Card, ClipToBounds = true,
                    Children = [ Image(a.Cover?.Url ?? "", 168f, 168f, WaveeRadius.Card, placeholder: Tok.FillCardDefault) ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.S,
                    Children =
                    [
                        Caption(Loc.Get(Strings.Home.FeaturedAlbum)) with { Color = Tok.AccentDefault, Weight = 700, CharSpacing = 80f, MaxWidth = TextMax },
                        WaveeType.PageHero(a.Name) with { MaxWidth = TextMax, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                        WaveeType.TrackMeta(sub) with { MaxWidth = TextMax, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, Margin = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                            Children = [ Button.Accent(Loc.Get(Strings.Home.Play), () => play(a.Uri)) ],
                        },
                    ],
                },
            ],
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────
    static string AlbumSub(Album a) => Strings.Home.AlbumSub(KindLabel(a.Kind), a.Year);
    static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Home.Single),
        AlbumKind.EP => Loc.Get(Strings.Home.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Home.Compilation),
        _ => Loc.Get(Strings.Home.Album),
    };

    static Element QuickGrid(IEnumerable<Element> tiles) =>
        AutoGrid(320f, WaveeSpace.M, MediaCard.QuickH, tiles.ToArray());

    // A rail-shaped shimmer: a title bar + a clipped strip of shelf-card skeletons.
    static Element ShelfShimmer(bool circular) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M,
        Children =
        [
            SkelBar(180f, 22f),
            new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.M, ClipToBounds = true,
                Children = Enumerable.Range(0, 6).Select(_ => MediaCard.ShelfSkeleton(168f, circular)).ToArray(),
            },
        ],
    };

    static Element SpotlightSkeleton() => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new BoxEl { Width = 168f, Height = 168f, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.S,
                Children = [ SkelBar(120f, 11f), SkelBar(280f, 28f), SkelBar(160f, 13f), SkelBar(96f, 32f) ],
            },
        ],
    };

    static Element SkelBar(float w, float h) =>
        new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault };
}
