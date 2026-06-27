using System;
using System.Linq;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Home landing page — a vertically SCROLLING composition driven by the source-agnostic, CONDENSED HomeFeed (the
// catalog seam groups a real Spotify home — dozens of sections — into a small, finite set of typed groups so it never
// reads as an endless stack of rails; see docs/architecture.md §2). Each group renders by KIND with the existing
// reusable cards: QuickGrid / Hero spotlight / horizontally-paged Shelf / a CollapsedGrid that folds the many
// single-item recommendations into one grid. Async skeleton pattern throughout (UseAsyncResource + Skel.Region).
sealed class HomePage : Component
{
    public override Element Render()
    {
        var svc    = UseContext(Services.Slot);
        var go     = UseContext(HistoryStore.NavCtx);
        var bridge = UseContext(PlaybackBridge.Slot);
        var morph  = UseContext(SharedTransition.Begin);   // connected-animation: snapshot the clicked cover before nav
        var preview = UseContext(NavPreviewStore.Slot);    // pre-load: stash the card's known cover/title for the detail page
        if (svc is null) return new BoxEl { Grow = 1f };

        // Re-fetch the home when the library changes (e.g. the live-session playlist-header hydration lands) so playlist
        // names/covers replace the initial URIs without a manual nav.
        var libVer = Context.UseSignal(0);
        Context.UseEffect(() =>
        {
            if (svc.Library is Wavee.Core.ICollectionEvents ev)
                ev.CollectionsChanged.Subscribe(Wavee.Backend.Observers.From<Wavee.Core.CollectionKind>(_ => libVer.Value++));
        });
        var home = UseAsyncResource(ct => svc.Library.GetHomeAsync(ct), FakeData.HomeSeed, libVer.Value);   // seed renders the loading shape; Skel.Region derives the shimmer from it
        string? name = bridge?.User.Value?.DisplayName;     // subscribe → greeting refreshes on login

        void Play(string uri) => _ = svc.Player.PlayAsync(uri, 0);
        void NavUri(string u) { if (RichText.RouteForUri(u) is { } k) go(k, null); }   // a link inside a card description → navigate to it
        void NavCard(HomeCard c)
        {
            switch (c.Kind)
            {
                case HomeCardKind.Liked:
                    go("liked", null);
                    break;
                case HomeCardKind.Album:
                {
                    string key = "album:" + c.Uri;
                    preview?.Set(key, DetailPreview.FromAlbum(new Album(Id(c.Uri), c.Uri, c.Title, c.Image, Array.Empty<ArtistRef>(), 0, 0)));
                    morph?.Invoke(key); go(key, c.Title);
                    break;
                }
                case HomeCardKind.Artist:
                    go("artist:" + c.Uri, c.Title);
                    break;
                default:
                {
                    string key = "pl:" + c.Uri;
                    preview?.Set(key, DetailPreview.FromPlaylist(new PlaylistSummary(c.Uri, c.Title, c.Subtitle ?? "", 0, c.Image)));
                    morph?.Invoke(key); go(key, c.Title);
                    break;
                }
            }
        }

        Element Tile(HomeCard c) => MediaCard.QuickPick(c.Image, c.Title, c.Uri, () => NavCard(c), () => Play(c.Uri));

        Element Shelf(HomeGroup g) => PagedShelf.Create(
            g.Cards.Count,
            cardAt: (i, w) =>
            {
                var c = g.Cards[i];
                return MediaCard.Shelf(c.Image, c.Title, c.Subtitle ?? "", c.Uri, () => NavCard(c), () => Play(c.Uri), w,
                    circular: c.Kind == HomeCardKind.Artist, morphKey: MorphKeyFor(c), onNavUri: NavUri);
            },
            measured: true,   // engine measures each card → row is exactly as tall as the tallest card (no reserved height)
            header: g.Title is { } t ? WaveeType.RailHeader(t) : new BoxEl());

        Element Group(HomeGroup g) => g.Kind switch
        {
            HomeGroupKind.QuickGrid => QuickGrid(g.Cards.Take(8).Select(Tile)),
            HomeGroupKind.Hero when g.Cards.Count > 0 => Responsive.Of(w => HeroBand(g.Cards[0], Play, NavCard, w), fallback: 560f),
            HomeGroupKind.Shelf when g.Cards.Count > 0 => Shelf(g),
            HomeGroupKind.CollapsedGrid => new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M,
                Children = [ g.Title is { } t ? WaveeType.RailHeader(t) : new BoxEl(), QuickGrid(g.Cards.Select(Tile)) ],
            },
            _ => new BoxEl(),
        };

        Element groups = Skel.Region(
            home,
            shimmerSource: HomeShimmer,
            isEmpty: feed => feed.Groups.Count == 0,
            onEmpty: () => EmptyState.Default(),
            onFailed: () => ErrorState.Build(home.Error),
            content: feed =>
            {
                // Warm below-the-fold cover art at the kind-matched decode size the moment the feed lands, so a first
                // scroll reveals resident textures instead of decoding+uploading on the UI thread mid-scroll (the
                // first-pass jank). Prefetch priority → background workers, so the visible cards still decode first;
                // idempotent (a re-hit cache entry is a dictionary lookup), so a rare region rebuild costs nothing.
                foreach (var g in feed.Groups)
                {
                    int px = g.Kind == HomeGroupKind.Hero ? 168 : g.Kind == HomeGroupKind.Shelf ? 256 : 64;
                    var cards = g.Cards;
                    for (int i = 0; i < cards.Count; i++)
                        if (cards[i].Image?.Url is { Length: > 0 } url) PrefetchImage(url, px);
                }
                return new BoxEl { Direction = 1, Gap = WaveeSpace.XL, Children = feed.Groups.Select(Group).ToArray() };
            });

        var page = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XL,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [ GreetingHero(name), groups ],
        };
        // Scroll-position restoration: home is one route, so the key is constant — a revisit after the page was evicted
        // from KeepAlive seeds the saved offset before layout (within KeepAlive the parked node already preserves it).
        return ScrollView(page) with { Grow = 1f, ScrollKey = "home" };
    }

    // Lightweight loading skeleton (finding #7): a few shelf placeholders (title bar + a row of card bars) so the pending
    // edge doesn't build the full home feed just to derive a skeleton. Sized childless boxes → shimmer bars; SmoothResize
    // eases the swap to the real groups on load.
    static Element HomeShimmer()
    {
        static Element TitleBar() => new BoxEl { Width = 180f, Height = 22f, Corners = CornerRadius4.All(WaveeRadius.Control) };
        static Element Card() => new BoxEl { Width = 180f, Height = 56f, Corners = CornerRadius4.All(WaveeRadius.Card) };
        static Element Shelf() => new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M,
            Children = [TitleBar(), new BoxEl { Direction = 0, Gap = WaveeSpace.M, Children = [Card(), Card(), Card(), Card(), Card()] }],
        };
        return new BoxEl { Direction = 1, Gap = WaveeSpace.XL, Children = [Shelf(), Shelf(), Shelf()] };
    }

    static string Id(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }
    static string? MorphKeyFor(HomeCard c) => c.Kind switch
    {
        HomeCardKind.Album => "album:" + c.Uri,
        HomeCardKind.Playlist => "pl:" + c.Uri,
        _ => null,
    };

    // ── greeting hero ────────────────────────────────────────────────────────────────────────────────
    static Element GreetingHero(string? name)
    {
        int h = DateTime.Now.Hour;
        string part = h < 5 ? Loc.Get(Strings.Home.GoodEvening)
                    : h < 12 ? Loc.Get(Strings.Home.GoodMorning)
                    : h < 18 ? Loc.Get(Strings.Home.GoodAfternoon)
                    : Loc.Get(Strings.Home.GoodEvening);
        // Omit a Spotify user-id handle (a long, space-less hash) — show the bare greeting until a real display name lands.
        bool looksLikeHandle = name is { Length: >= 20 } && !name.Contains(' ');
        string greet = (string.IsNullOrWhiteSpace(name) || looksLikeHandle) ? part : Strings.Home.Greeting(part, name);
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XS, Padding = new Edges4(0f, WaveeSpace.S, 0f, 0f),
            Children = [ WaveeType.PageHero(greet), WaveeType.TrackMeta(Loc.Get(Strings.Home.OnRotation)) ],
        };
    }

    // ── featured spotlight (a Hero card — full-width band, the calm break) ──────────────────────────────
    static Element HeroBand(HomeCard c, Action<string> play, Action<HomeCard> nav, float innerW)
    {
        float textMax = innerW > 1f ? MathF.Max(160f, innerW - 216f) : 560f;
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
                    OnClick = () => nav(c), Cursor = CursorId.Hand,
                    Children = [ Image(c.Image?.Url ?? "", 168f, 168f, WaveeRadius.Card, placeholder: Tok.FillCardDefault) ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.S,
                    Children =
                    [
                        Caption(Loc.Get(Strings.Home.FeaturedAlbum)) with { Color = Tok.AccentDefault, Weight = 700, CharSpacing = 80f, MaxWidth = textMax },
                        WaveeType.PageHero(c.Title) with { MaxWidth = textMax, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                        WaveeType.TrackMeta(c.Subtitle ?? "") with { MaxWidth = textMax, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, Margin = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                            Children = [ Button.Accent(Loc.Get(Strings.Home.Play), () => play(c.Uri)) ],
                        },
                    ],
                },
            ],
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────
    static Element QuickGrid(IEnumerable<Element> tiles) =>
        AutoGrid(320f, WaveeSpace.M, MediaCard.QuickH, tiles.ToArray());
}
