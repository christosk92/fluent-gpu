using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
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
        var preview = UseContext(NavPreviewStore.Slot);    // pre-load: stash the card's known cover/title for the detail page
        var acts = UseContext(ActionServices.Slot);        // card context menus (Menus.Card via CardAttach)
        var menuOverlay = UseContext(Overlay.Service);
        if (svc is null) return new BoxEl { Grow = 1f };

        var home = UseLoadable(Loadable<HomeFeed>.Pending(FakeData.HomeSeed));   // seed renders the loading shape; later refreshes swap Ready->Ready in place
        var post = UsePost();
        // Home groups have substantially different heights (quick grid / hero / compact grid / shelf / editorial).
        // Hoist one measured extent table so the viewport can correct and anchor rows while recycling offscreen groups.
        var homeLayout = UseMemo(static () => new HomeFeedVirtualLayout(), DepKey.Empty);
        // Start the background home-refresh loop ONCE and tie it to this component's lifetime. A UseSignalEffect that
        // reads no signals runs exactly once on mount; its Reactive.OnCleanup fires on unmount (KeepAlive eviction /
        // navigation away). Without this, each cold remount of Home leaked an orphaned 60s PeriodicTimer loop that
        // COMPOUNDED over a long session. Mirrors the LyricsTicker lifecycle pattern (Features/Player/LyricsView.cs).
        // Deliberately NO CollectionsChanged subscription: a like/save emits several collection waves and re-fetching
        // the whole feed per wave churned the page every time. Library-driven updates (e.g. the post-sign-in
        // playlist-header hydration) land on the next 60s tick or a manual re-nav — an accepted trade.
        Context.UseSignalEffect(() =>
        {
            var cts = new CancellationTokenSource();
            StartHomeRefreshLoop(svc, home, post, cts.Token);
            Reactive.OnCleanup(() => { cts.Cancel(); cts.Dispose(); });
        });
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
                case HomeCardKind.Track:
                    _ = svc.Player.PlayTrackAsync(c.Uri);
                    break;
                case HomeCardKind.Album:
                {
                    string key = "album:" + c.Uri;
                    preview?.Set(key, DetailPreview.FromAlbum(new Album(Id(c.Uri), c.Uri, c.Title, c.Image, Array.Empty<ArtistRef>(), 0, 0)));
                    go(key, c.Title);
                    break;
                }
                case HomeCardKind.Artist:
                    go("artist:" + c.Uri, c.Title);
                    break;
                default:
                {
                    string key = "pl:" + c.Uri;
                    preview?.Set(key, DetailPreview.FromPlaylist(new PlaylistSummary(c.Uri, c.Title, c.Subtitle ?? "", 0, c.Image)));
                    go(key, c.Title);
                    break;
                }
            }
        }

        void PlayCard(HomeCard c)
        {
            if (c.Kind == HomeCardKind.Track) _ = svc.Player.PlayTrackAsync(c.Uri);
            else Play(c.Uri);
        }

        Element Tile(HomeCard c, string section = "", int index = -1)
        {
            string? url = HomeImageDiagnostics.NormalizedUrl(c.Image);
            Element tile = MediaCard.QuickPick(c.Image, c.Title, c.Uri, () => NavCard(c), () => PlayCard(c),
                accent: c.Accent is { } a ? WaveePalette.Lift(WaveePalette.ToColor(a)) : null,
                diagnostics: url is { Length: > 0 }
                    ? Embed.Comp(() => new HomeQuickImageProbe(url, c.Uri, c.Title, section, index)).Skeletonized(false)
                    : null,
                menu: Menus.CardAttach(acts, menuOverlay, c.Uri, c.Title));
            return tile is BoxEl box ? box with { Key = "home-quick:" + c.Uri, Animate = MotionRecipes.CardRefit } : tile;
        }

        IEnumerable<Element> QuickTiles(IReadOnlyList<HomeCard> cards, string section, int max)
        {
            int n = Math.Min(cards.Count, max);
            for (int i = 0; i < n; i++) yield return Tile(cards[i], section, i);
        }

        // Conventional discovery modules remain finite, one-row horizontal shelves using the SAME MediaCard.Shelf
        // component as the rest of the app. PagedShelf clamps at the first/last page and never loops.
        Element Shelf(HomeGroup g) => PagedShelf.Create(
            g.Cards.Count,
            cardAt: (i, w) =>
            {
                var c = g.Cards[i];
                return MediaCard.Shelf(c.Image, c.Title, c.Subtitle ?? "", c.Uri,
                    () => NavCard(c), () => PlayCard(c), w,
                    circular: c.Kind == HomeCardKind.Artist, onNavUri: NavUri,
                    menu: Menus.CardAttach(acts, menuOverlay, c.Uri, c.Title));
            },
            measured: true,
            header: g.Title is { Length: > 0 } t ? Surfaces.AccentHeader(t, GroupAccent(g)) : new BoxEl(),
            pager: ShelfPager.Chevrons,
            // gap S, not L: the Shelf card already insets its artwork by Pad(8) each side for the hover plate, so the
            // VISUAL cover-to-cover distance is gap + 16 — L read as ~32px of air between resting cards.
            minCardW: 178f, maxCardW: 232f, gap: WaveeSpace.S, edgeFade: 24f,
            keyOf: i => g.Cards[i].Uri);

        // Baseline recommendations are the deliberate exception: a three-up editorial interruption with full-bleed
        // artwork and overlaid context/title, matching Apple Music's Top Picks module.
        Element FeaturedShelf(HomeGroup g) => PagedShelf.Create(
            g.Cards.Count,
            cardAt: (i, w) =>
            {
                var c = g.Cards[i];
                return MediaCard.EditorialCard(c.Image, c.Eyebrow, c.Title, c.Subtitle ?? "", c.Uri, c.Kind,
                    () => NavCard(c), () => PlayCard(c), w,
                    Menus.CardAttach(acts, menuOverlay, c.Uri, c.Title),
                    // Hover peek: preview tracks from the batched feedBaselineLookup cache (primed when the feed lands).
                    previewsOf: Wavee.SpotifyLive.HomeBaselinePreviews.For,
                    previewsEpoch: Wavee.SpotifyLive.HomeBaselinePreviews.Epoch);
            },
            measured: true,
            header: g.Title is { Length: > 0 } t ? Surfaces.AccentHeader(t, GroupAccent(g)) : new BoxEl(),
            pager: ShelfPager.Chevrons,
            // Width-adaptive editorial row, like a normal shelf but larger: the min width drives the column count
            // (≈3 cols at ~1000px, 4 at ~1300, 5 from ~1600) and maxColumns caps an ultrawide window at 5 — never the
            // old count-pinned 3-up that ballooned each card to a third of the row. The uncapped maxCardW keeps the
            // FITTED columns filling the width (no maxCardW slivers); a break with fewer cards than columns simply
            // shows them at the fitted size.
            // edgeFade must cover the shelf's HaloBleed gutter (PagedShelf): with fade 0 a free-wheel (non-page-aligned)
            // rest showed a RAW sliver of the scrolled-out neighbor in the gutter — the fade is what keeps it soft.
            minCardW: 300f, maxCardW: 9999f, gap: WaveeSpace.L, edgeFade: 24f,
            maxColumns: 5,
            keyOf: i => g.Cards[i].Uri);

        Element CompactSection(HomeGroup g) => Responsive.Of(width =>
        {
            float art = HomeCompactLayout.Art(width);
            float cardH = HomeCompactLayout.CardHeight(width);
            int columns = HomeCompactLayout.Columns(width);
            var cards = new Element[g.Cards.Count];
            for (int i = 0; i < cards.Length; i++)
            {
                var c = g.Cards[i];
                Element compact = MediaCard.Compact(c.Image, c.Title, c.Subtitle ?? "", c.Uri, c.Kind,
                    () => NavCard(c), () => PlayCard(c), art, cardH,
                    Menus.CardAttach(acts, menuOverlay, c.Uri, c.Title));
                cards[i] = compact is BoxEl b
                    ? b with { Key = "home-compact:" + c.Uri, Animate = MotionRecipes.CardRefit }
                    : compact;
            }
            return new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M,
                Children =
                [
                    g.Title is { Length: > 0 } t ? Surfaces.AccentHeader(t, GroupAccent(g)) : new BoxEl(),
                    UniformGrid(columns, HomeCompactLayout.GridGap, cardH + HomeCompactLayout.RowClearance, cards),
                ],
            };
        }, fallback: 900f);

        // The library quick picks are the fixed opening matrix, not another unbounded feed module. The responsive
        // column count A determines an A×A window; only a genuinely shorter source leaves the final row incomplete.
        Element QuickSection(HomeGroup g) => Responsive.Of(width =>
        {
            int columns = HomeQuickLayout.Columns(width);
            int count = HomeQuickLayout.VisibleCount(width, g.Cards.Count);
            return UniformGrid(columns, HomeQuickLayout.Gap, MediaCard.QuickH,
                QuickTiles(g.Cards, g.Title ?? "quick-grid", count).ToArray());
        }, fallback: 1100f);

        Element Group(HomeGroup g) => g.Kind switch
        {
            HomeGroupKind.QuickGrid => QuickSection(g),
            HomeGroupKind.Hero when g.Cards.Count > 0 => Responsive.Of(w => HeroBand(g.Cards[0], GroupAccent(g), PlayCard, NavCard, w), fallback: 560f),
            HomeGroupKind.Featured when g.Cards.Count > 0 => FeaturedShelf(g),
            HomeGroupKind.Compact when g.Cards.Count > 0 => CompactSection(g),
            HomeGroupKind.Shelf when g.Cards.Count > 0 => Shelf(g),
            HomeGroupKind.CollapsedGrid when g.Cards.Count > 0 => Shelf(g),
            _ => new BoxEl(),
        };

        // The Concert Hub destination is the final virtual row. It is mounted only when the measured list realizes the
        // tail of the feed instead of living permanently below every Spotify module.
        Element concerts = ConcertUi.WideEditorialDestination(
            artwork: null,
            eyebrow: Loc.Get(Strings.Concerts.LiveMusic),
            title: Loc.Get(Strings.Concerts.HomeTitle),
            subtitle: Loc.Get(Strings.Concerts.HomeSubtitle),
            actionLabel: Loc.Get(Strings.Concerts.Explore),
            onClick: () => go(Wavee.Features.Concerts.ConcertRoutes.Hub, Loc.Get(Strings.Concerts.Title)))
            with { Key = "home-concerts-editorial" };

        void WarmGroup(HomeGroup g)
        {
            // Preview lookup and image decode follow the realized window. The old eager whole-feed pass enqueued every
            // cover before the first content frame, largely defeating the benefit of recycling the group trees.
            if (g.Kind == HomeGroupKind.Featured)
                Wavee.SpotifyLive.HomeBaselinePreviews.Prime(g.Cards.Select(c => c.Uri));
            int px = g.Kind switch
            {
                HomeGroupKind.QuickGrid => 64,
                HomeGroupKind.Hero => 168,
                HomeGroupKind.Compact => 128,
                HomeGroupKind.Featured => 512,
                _ => 256,
            };
            var cards = g.Cards;
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].Image?.Url is { Length: > 0 } url) PrefetchImage(url, px);
        }

        Element HomeRow(Element child, string contentKey, float top, float bottom) => new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(WaveeSpace.L, top, WaveeSpace.L, bottom),
            // Home is a heterogeneous virtual list: greeting, grids, shelves and the concert destination do not share
            // a recyclable subtree shape. Keep this cheap row shell recyclable, but key its content so a shell reused
            // for another row replaces the old subtree instead of positionally rebinding incompatible element trees.
            Children = [ child with { Key = contentKey } ],
        };

        Element VirtualHome(HomeFeed feed)
        {
            HomeImageDiagnostics.LogFeed(feed);
            int groupCount = feed.Groups.Count;
            int concertIndex = groupCount + 1; // 0 greeting, 1..N groups, N+1 concert destination
            homeLayout.Configure(feed.Groups);

            string KeyAt(int index)
            {
                if (index == 0) return "home-greeting";
                if (index == concertIndex) return "home-concerts";
                var g = feed.Groups[index - 1];
                string first = g.Cards.Count > 0 ? g.Cards[0].Uri : "empty";
                return $"home-group:{g.Kind}:{g.Title}:{g.Cards.Count}:{first}";
            }

            Element RowAt(int index)
            {
                string key = KeyAt(index);
                if (index == 0)
                    return HomeRow(GreetingHero(name), key, WaveeSpace.M, WaveeSpace.XL);
                if (index == concertIndex)
                    return HomeRow(concerts, key, 0f, PlayerDock.Reserve + WaveeSpace.XXL);
                return HomeRow(Group(feed.Groups[index - 1]), key, 0f, WaveeSpace.XL);
            }

            return Virtual.Measured(groupCount + 2, homeLayout, RowAt, KeyAt, overscan: 1) with
            {
                Grow = 1f,
                Shrink = 1f,
                MinHeight = 0f,
                ScrollKey = "home",
                OnVisibleRange = (first, end) =>
                {
                    // The realized range already contains one row of overscan. Exclude the greeting and concert rows.
                    int fromGroup = Math.Max(0, first - 1);
                    int toGroup = Math.Min(groupCount, end - 1);
                    for (int i = fromGroup; i < toGroup; i++) WarmGroup(feed.Groups[i]);
                },
            };
        }

        Element PendingHome() => ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = WaveeSpace.XL,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [ GreetingHero(name), HomeShimmer(), concerts ],
        }) with { Grow = 1f, ScrollKey = "home" };

        Element StateHome(Element state) => ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = WaveeSpace.XL,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = [ GreetingHero(name), state, concerts ],
        }) with { Grow = 1f, ScrollKey = "home" };

        // Swap one viewport for another. There is deliberately no outer ScrollView around VirtualHome: doing that would
        // measure the virtual list at its complete content extent and silently realize every group again.
        return Skel.Region(
            home,
            shimmerSource: PendingHome,
            reveal: SkelReveal.StaggerRows,
            isEmpty: feed => feed.Groups.Count == 0,
            onEmpty: () => StateHome(EmptyState.Default()),
            onFailed: () => StateHome(ErrorState.Build(home.Error)),
            content: VirtualHome);
    }

    static void StartHomeRefreshLoop(Services svc, Loadable<HomeFeed> home, Action<Action> post, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshHomeOnce(svc, home, post, failIfInitial: true).ConfigureAwait(false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    await RefreshHomeOnce(svc, home, post, failIfInitial: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Home unmounted → stop the refresh loop cleanly */ }
        }, ct);
    }

    static async Task RefreshHomeOnce(Services svc, Loadable<HomeFeed> home, Action<Action> post, bool failIfInitial)
    {
        try
        {
            var feed = await svc.Library.GetHomeAsync(default).ConfigureAwait(false);
            post(() => home.SetReady(feed));
        }
        catch (Exception ex)
        {
            if (!failIfInitial) return;
            post(() =>
            {
                if (home.State.Peek() != (byte)LoadState.Ready) home.SetFailed(ex);
            });
        }
    }

    // Lightweight loading skeleton for the SAME visual rhythm as the seeded/real Home feed. It deliberately keeps
    // the trees paint-only (no media components, timers, menus, or image work), while reserving the geometry of the
    // quick grid, conventional shelf, compact two-column module, and editorial break before content arrives.
    static Element HomeShimmer()
    {
        const float pad = WaveeSpace.S;

        static Element Bar(float w, float h, float r = 4f) =>
            new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(r) };

        static Element[] Repeat(int count, Func<Element> make)
        {
            var items = new Element[count];
            for (int i = 0; i < items.Length; i++) items[i] = make();
            return items;
        }

        static Element Header(float titleW, bool pager = true) => new BoxEl
        {
            Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
            Children = pager
                ?
                [
                    new BoxEl { Width = 3f, Height = 42f, Corners = CornerRadius4.All(1.5f) },
                    Bar(titleW, 28f, 6f),
                    new BoxEl { Grow = 1f, SkeletonMode = SkeletonMode.Off },
                    Bar(32f, 32f, 16f),
                    Bar(32f, 32f, 16f),
                ]
                :
                [
                    new BoxEl { Width = 3f, Height = 42f, Corners = CornerRadius4.All(1.5f) },
                    Bar(titleW, 28f, 6f),
                ],
        };

        static Element QuickTile() => new BoxEl
        {
            Height = MediaCard.QuickH, Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Children =
            [
                new BoxEl
                {
                    Width = MediaCard.QuickW, Height = MediaCard.QuickH,
                    Corners = CornerRadius4.All(WaveeRadius.Card), Shrink = 0f,
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 6f, Justify = FlexJustify.Center,
                    Children = [ Bar(132f, 15f, 7.5f), Bar(88f, 11f, 5.5f) ],
                },
            ],
        };

        static Element ShelfCard(float cardW)
        {
            float art = MathF.Max(48f, cardW - 2f * pad);
            return new BoxEl
            {
                Width = cardW, Shrink = 0f, Direction = 1, Gap = pad,
                Padding = new Edges4(pad, 4f, pad, WaveeSpace.M),
                Children =
                [
                    new BoxEl { Width = art, Height = art, Corners = CornerRadius4.All(WaveeRadius.Card) },
                    Bar(MathF.Min(132f, art), 15f, 7.5f),
                    Bar(MathF.Min(92f, art * 0.68f), 11f, 5.5f),
                ],
            };
        }

        static Element ShelfSection(float titleW) => Responsive.Of(width =>
        {
            const float minCardW = 178f;
            const float maxCardW = 232f;
            const float gap = WaveeSpace.L;
            int columns = Math.Clamp((int)MathF.Floor((MathF.Max(width, minCardW) + gap) / (minCardW + gap)), 1, 6);
            float fitted = (MathF.Max(width, minCardW) - gap * (columns - 1)) / columns;
            float cardW = Math.Clamp(fitted, minCardW, maxCardW);
            return new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M,
                Children =
                [
                    Header(titleW),
                    new BoxEl
                    {
                        Direction = 0, Gap = gap, ClipToBounds = true,
                        Children = Repeat(6, () => ShelfCard(cardW)),
                    },
                ],
            };
        }, fallback: 1100f);

        static Element CompactCard(float art, float cardH) => new BoxEl
        {
            Direction = 1, Padding = new Edges4(0f, HomeCompactLayout.RowClearance * 0.5f, 0f, HomeCompactLayout.RowClearance * 0.5f),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Height = cardH, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(WaveeSpace.S, MathF.Max(0f, (cardH - art) * 0.5f), WaveeSpace.S,
                        MathF.Max(0f, (cardH - art) * 0.5f)),
                    Corners = CornerRadius4.All(WaveeRadius.Card),
                    Children =
                    [
                        new BoxEl
                        {
                            Width = art, Height = art, Shrink = 0f,
                            Corners = CornerRadius4.All(WaveeRadius.Card),
                        },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, Gap = 7f, Justify = FlexJustify.Center,
                            Children = [ Bar(148f, 15f, 7.5f), Bar(104f, 11f, 5.5f) ],
                        },
                        Bar(42f, 42f, 21f),
                        Bar(32f, 32f, 16f),
                    ],
                },
            ],
        };

        static Element CompactSection() => Responsive.Of(width =>
        {
            float art = HomeCompactLayout.Art(width);
            float cardH = HomeCompactLayout.CardHeight(width);
            int columns = HomeCompactLayout.Columns(width);
            return new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M,
                Children =
                [
                    Header(224f, pager: false),
                    UniformGrid(columns, HomeCompactLayout.GridGap, cardH + HomeCompactLayout.RowClearance, Repeat(4, () => CompactCard(art, cardH))),
                ],
            };
        }, fallback: 900f);

        static Element EditorialCard(float cardW)
        {
            float artH = MathF.Max(360f, cardW * 1.25f);
            float inset = Math.Clamp(cardW * 0.055f, 14f, 20f);
            return new BoxEl
            {
                Width = cardW, Height = artH, Shrink = 0f, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(14f),
                Children =
                [
                    new BoxEl { Height = artH, Corners = CornerRadius4.All(14f) },
                    new BoxEl
                    {
                        Height = artH, Direction = 1, Justify = FlexJustify.End,
                        Padding = new Edges4(inset, inset, inset, inset), Gap = 8f,
                        Children =
                        [
                            Bar(MathF.Min(116f, cardW * 0.42f), 11f, 5.5f),
                            Bar(MathF.Max(96f, cardW - inset * 2f), 19f, 9.5f),
                            Bar(MathF.Min(184f, cardW * 0.68f), 12f, 6f),
                            new BoxEl
                            {
                                Direction = 0, Gap = 8f,
                                Children = [ Bar(44f, 44f, 22f), Bar(44f, 44f, 22f) ],
                            },
                        ],
                    },
                ],
            };
        }

        static Element EditorialSection() => Responsive.Of(width =>
        {
            const float minCardW = 300f;
            const float gap = WaveeSpace.L;
            int columns = Math.Clamp((int)MathF.Floor((MathF.Max(width, minCardW) + gap) / (minCardW + gap)), 1, 5);
            float cardW = MathF.Max(minCardW, (MathF.Max(width, minCardW) - gap * (columns - 1)) / columns);
            return new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M,
                Children =
                [
                    Header(188f),
                    new BoxEl
                    {
                        Direction = 0, Gap = gap, ClipToBounds = true,
                        Children = Repeat(3, () => EditorialCard(cardW)),
                    },
                ],
            };
        }, fallback: 1100f);

        static Element QuickSection() => Responsive.Of(width =>
        {
            int columns = HomeQuickLayout.Columns(width);
            return UniformGrid(columns, HomeQuickLayout.Gap, MediaCard.QuickH,
                Repeat(columns * columns, QuickTile));
        }, fallback: 1100f);

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XL,
            Children =
            [
                QuickSection(),
                ShelfSection(212f),
                CompactSection(),
                EditorialSection(),
                ShelfSection(176f),
            ],
        };
    }

    static string Id(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }
    // The group's lifted accent (renderer color): the composer-assigned tint (cover-extracted or semantic), or a
    // per-kind fallback for groups with none (the offline/library home). Lift keeps a near-black cover color legible.
    static ColorF GroupAccent(HomeGroup g) => WaveePalette.Lift(WaveePalette.ToColor(
        g.Accent ?? (g.Kind is HomeGroupKind.CollapsedGrid or HomeGroupKind.Compact or HomeGroupKind.Featured
            ? 0xFF3B82F6u : 0xFF60CDFFu)));

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
    static Element HeroBand(HomeCard c, ColorF accent, Action<HomeCard> play, Action<HomeCard> nav, float innerW)
    {
        float textMax = innerW > 1f ? MathF.Max(160f, innerW - 216f) : 560f;
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
            Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            // Spotlight glow: a soft accent radial pooled behind the cover (left-of-center), fading into the neutral
            // card material toward the right — so the art appears to emit its own color (the WinUI hero treatment),
            // rather than the whole band being painted. Opaque card-based stops keep it a material, not a wash.
            Gradient = RadialGradient(
                new Point2(0.16f, 0.5f), new Point2(0.78f, 1.05f),
                new GradientStop(0f, ColorF.Lerp(Tok.FillCardSecondary, accent, 0.30f)),
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
                        Caption(Loc.Get(Strings.Home.FeaturedAlbum)) with { Color = accent, Weight = 700, CharSpacing = 80f, MaxWidth = textMax },
                        WaveeType.PageHero(c.Title) with { MaxWidth = textMax, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                        WaveeType.TrackMeta(c.Subtitle ?? "") with { MaxWidth = textMax, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, Margin = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                            Children = [ Button.Accent(Loc.Get(Strings.Home.Play), () => play(c)) ],
                        },
                    ],
                },
            ],
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────
}

/// <summary>
/// Variable-height Home stack with kind-aware first estimates. The engine still measures every realized row and feeds
/// the exact extent back through <see cref="IMeasuredVirtualLayout.SetMeasured"/>; these estimates only make the cold
/// window/content extent credible before those measurements exist. The state is hoisted by HomePage and retained across
/// refreshes, so steady scrolling remains the normal Fenwick-table path.
/// </summary>
static class HomeCompactLayout
{
    public const float GridGap = WaveeSpace.S;
    public const float RowClearance = 4f;

    public static float Art(float width) => width < 620f ? 72f : 88f;
    public static float CardHeight(float width) => width < 620f ? 88f : 104f;
    public static int Columns(float width) => width >= 960f ? 3 : width >= 620f ? 2 : 1;
}

static class HomeQuickLayout
{
    public const float MinColumnWidth = 320f;
    public const float Gap = WaveeSpace.M;
    public const int MaxColumns = 3;

    public static int Columns(float width)
        => Math.Clamp((int)MathF.Floor((MathF.Max(1f, width) + Gap) / (MinColumnWidth + Gap)), 1, MaxColumns);

    public static int VisibleCount(float width, int available)
    {
        int columns = Columns(width);
        return Math.Min(Math.Max(0, available), columns * columns);
    }
}

sealed class HomeFeedVirtualLayout : IMeasuredVirtualLayout
{
    readonly ExtentTable _extents = new(0, 1f);
    HomeGroupKind[] _kinds = Array.Empty<HomeGroupKind>();
    int[] _cardCounts = Array.Empty<int>();
    bool[] _titled = Array.Empty<bool>();
    int _groupCount;
    int _shapeVersion;
    int _seededVersion = -1;
    float _seededCross = float.NaN;

    public void Configure(IReadOnlyList<HomeGroup> groups)
    {
        int count = groups.Count;
        bool changed = count != _groupCount;
        if (_kinds.Length < count)
        {
            Array.Resize(ref _kinds, count);
            Array.Resize(ref _cardCounts, count);
            Array.Resize(ref _titled, count);
        }

        for (int i = 0; i < count; i++)
        {
            var g = groups[i];
            bool titled = g.Title is { Length: > 0 };
            if (!changed && (_kinds[i] != g.Kind || _cardCounts[i] != g.Cards.Count || _titled[i] != titled))
                changed = true;
            _kinds[i] = g.Kind;
            _cardCounts[i] = g.Cards.Count;
            _titled[i] = titled;
        }

        _groupCount = count;
        if (changed) _shapeVersion++;
    }

    void Ensure(int itemCount, float crossSize)
    {
        // Measure can ask for an estimate before arrange publishes a finite cross size. Reuse the last real width when
        // available so a 0-width prepass cannot reset a corrected table every frame.
        float cross = crossSize > 1f ? crossSize : !float.IsNaN(_seededCross) ? _seededCross : 1100f;
        if (_extents.Count == itemCount && _seededVersion == _shapeVersion
            && !float.IsNaN(_seededCross) && MathF.Abs(_seededCross - cross) <= 0.5f)
            return;

        // Trace the reseed trigger (code 110): f0=incoming cross, f1=previously seeded cross, i1=itemCount vs i2=seeded
        // count — a reseed mid-scroll wipes every measured correction and flaps the anchor re-pin.
        if (FluentGpu.Foundation.ScrollTrace.On)
            FluentGpu.Foundation.ScrollTrace.Note(110, cross, itemCount, (_extents.Count << 8) | (_seededVersion == _shapeVersion ? 1 : 0), _seededCross);

        _extents.Reset(itemCount, 360f);
        for (int i = 0; i < itemCount; i++) _extents.SetExtent(i, Estimate(i, cross));
        _seededCross = cross;
        _seededVersion = _shapeVersion;
    }

    float Estimate(int index, float cross)
    {
        float available = MathF.Max(1f, cross - 2f * WaveeSpace.L);
        if (index == 0)
            return 84f + WaveeSpace.M + WaveeSpace.XL; // greeting copy + row top/bottom rhythm
        if (index == _groupCount + 1)
            return Wavee.Features.Concerts.ConcertLayout.WideEditorial(available).Height
                + PlayerDock.Reserve + WaveeSpace.XXL;

        int gi = index - 1;
        if ((uint)gi >= (uint)_groupCount) return 360f;
        int count = _cardCounts[gi];
        float header = _titled[gi] ? 42f + WaveeSpace.M : 0f;

        return _kinds[gi] switch
        {
            HomeGroupKind.QuickGrid => QuickExtent(available, count),
            HomeGroupKind.Hero => 168f + 2f * WaveeSpace.L + WaveeSpace.XL,
            HomeGroupKind.Compact => header + CompactExtent(available, count) + WaveeSpace.XL,
            HomeGroupKind.Featured => header + FeaturedExtent(available) + WaveeSpace.XL,
            HomeGroupKind.Shelf or HomeGroupKind.CollapsedGrid => header + ShelfExtent(available) + WaveeSpace.XL,
            _ => WaveeSpace.XL,
        };
    }

    static float QuickExtent(float width, int count)
    {
        if (count <= 0) return WaveeSpace.XL;
        int columns = HomeQuickLayout.Columns(width);
        int visible = HomeQuickLayout.VisibleCount(width, count);
        int rows = (visible + columns - 1) / columns;
        return rows * MediaCard.QuickH + Math.Max(0, rows - 1) * HomeQuickLayout.Gap + WaveeSpace.XL;
    }

    static float CompactExtent(float width, int count)
    {
        float rowHeight = HomeCompactLayout.CardHeight(width) + HomeCompactLayout.RowClearance;
        int columns = HomeCompactLayout.Columns(width);
        int rows = Math.Max(1, (count + columns - 1) / columns);
        return rows * rowHeight + Math.Max(0, rows - 1) * HomeCompactLayout.GridGap;
    }

    static float ShelfExtent(float width)
    {
        var (_, cardW) = FillRowVirtualLayout.Fit(width, 178f, 232f, WaveeSpace.L);
        // Square art plus the fixed one-line title/two-line metadata stack, card padding, and shelf shadow clearance.
        return cardW + 76f;
    }

    static float FeaturedExtent(float width)
    {
        var (_, cardW) = FillRowVirtualLayout.Fit(width, 300f, 9999f, WaveeSpace.L, maxColumns: 5);
        return MathF.Max(360f, cardW * 1.25f) + 12f; // portrait art/card + shelf shadow clearance
    }

    public float ContentExtent(int itemCount, float crossSize)
    {
        Ensure(itemCount, crossSize);
        return (float)_extents.Total;
    }

    public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
        out int first, out int last)
    {
        Ensure(itemCount, crossSize);
        first = Math.Max(0, _extents.IndexAt(scrollOffset) - overscan);
        last = Math.Min(itemCount, _extents.IndexAt(scrollOffset + viewportExtent) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int index, float crossSize)
    {
        Ensure(_groupCount + 2, crossSize);
        return new RectF(0f, _extents.OffsetOf(index), crossSize, _extents.ExtentAt(index));
    }

    public void SetMeasured(int index, float mainExtent, float crossSize)
    {
        Ensure(_groupCount + 2, crossSize);
        _extents.SetExtent(index, mainExtent);
    }

    public float OffsetOf(int index, float crossSize)
    {
        Ensure(_groupCount + 2, crossSize);
        return _extents.OffsetOf(index);
    }

    public int IndexAt(float offset, float crossSize)
    {
        Ensure(_groupCount + 2, crossSize);
        return _extents.IndexAt(offset);
    }
}

sealed class HomeQuickImageProbe : Component
{
    readonly string _url;
    readonly string _uri;
    readonly string _title;
    readonly string _section;
    readonly int _index;

    public HomeQuickImageProbe(string url, string uri, string title, string section, int index)
    {
        _url = url;
        _uri = uri;
        _title = title;
        _section = section;
        _index = index;
    }

    public override Element Render()
    {
        var binding = UseImage(_url, (int)MediaCard.QuickW, (int)MediaCard.QuickH);
        HomeImageDiagnostics.LogState(_uri, _title, _section, _index, _url, binding);
        return new BoxEl { Width = 0f, Height = 0f };
    }
}

static class HomeImageDiagnostics
{
    static readonly object Gate = new();
    static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    public static string? NormalizedUrl(Image? image)
    {
        if (image?.MosaicTiles is { Count: > 0 } tiles)
            return tiles.Count >= 4 ? null : ImageSource.Normalize(tiles[0]);
        return image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;
    }

    public static void LogFeed(HomeFeed feed)
    {
        for (int gi = 0; gi < feed.Groups.Count; gi++)
        {
            var group = feed.Groups[gi];
            if (group.Kind != HomeGroupKind.QuickGrid) continue;

            int total = Math.Min(group.Cards.Count, HomeQuickLayout.MaxColumns * HomeQuickLayout.MaxColumns);
            int url = 0, mosaic = 0, missing = 0, emptyUrl = 0;
            for (int i = 0; i < total; i++)
            {
                var card = group.Cards[i];
                if (card.Image is null)
                {
                    if (card.Kind != HomeCardKind.Liked) { missing++; LogMissing(group, gi, card, i, "image-null"); }
                    continue;
                }
                if (card.Image.MosaicTiles is { Count: >= 4 }) { mosaic++; continue; }
                if (card.Image.Url is not { Length: > 0 }) { emptyUrl++; LogMissing(group, gi, card, i, "url-empty"); continue; }
                url++;
            }

            LogOnce("summary|" + gi + "|" + total + "|" + url + "|" + mosaic + "|" + missing + "|" + emptyUrl,
                () => WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "home.image.quickgrid.summary",
                    "Home quick-grid image inventory",
                    fields:
                    [
                        WaveeLogField.Of("groupIndex", gi),
                        WaveeLogField.Of("title", group.Title ?? ""),
                        WaveeLogField.Of("cards", total),
                        WaveeLogField.Of("url", url),
                        WaveeLogField.Of("mosaic", mosaic),
                        WaveeLogField.Of("missing", missing),
                        WaveeLogField.Of("emptyUrl", emptyUrl),
                    ]));
        }
    }

    public static void LogState(string uri, string title, string section, int index, string url, ImageBinding binding)
    {
        string key = "state|" + uri + "|" + index + "|" + binding.State + "|" + binding.Failure + "|" + binding.Attempts;
        LogOnce(key, () =>
        {
            var level = binding.State == ImageState.Failed && binding.Failure != ImageFailureKind.Canceled
                ? WaveeLogLevel.Warning
                : WaveeLogLevel.Debug;
            WaveeLog.Instance.Event(level, "ui", "home.image.quickgrid.state",
                "Home quick-grid image cache state",
                fields:
                [
                    WaveeLogField.Of("uri", uri),
                    WaveeLogField.Of("title", title),
                    WaveeLogField.Of("section", section),
                    WaveeLogField.Of("index", index),
                    WaveeLogField.Of("state", binding.State.ToString()),
                    WaveeLogField.Of("failure", binding.Failure.ToString()),
                    WaveeLogField.Of("attempts", binding.Attempts),
                    WaveeLogField.Of("host", WaveeLogRedaction.UrlHost(url)),
                    WaveeLogField.Of("url", ShortUrl(url)),
                ]);
        });
    }

    static void LogMissing(HomeGroup group, int groupIndex, HomeCard card, int index, string reason)
    {
        LogOnce("missing|" + groupIndex + "|" + index + "|" + card.Uri + "|" + reason,
            () => WaveeLog.Instance.Event(WaveeLogLevel.Warning, "ui", "home.image.quickgrid.missing",
                "Home quick-grid card has no renderable image URL",
                fields:
                [
                    WaveeLogField.Of("reason", reason),
                    WaveeLogField.Of("groupIndex", groupIndex),
                    WaveeLogField.Of("section", group.Title ?? ""),
                    WaveeLogField.Of("index", index),
                    WaveeLogField.Of("kind", card.Kind.ToString()),
                    WaveeLogField.Of("uri", card.Uri),
                    WaveeLogField.Of("title", card.Title),
                    WaveeLogField.Of("mosaicTiles", card.Image?.MosaicTiles?.Count ?? 0),
                ]));
    }

    static void LogOnce(string key, Action log)
    {
        lock (Gate)
        {
            if (!Seen.Add(key)) return;
        }
        log();
    }

    static string ShortUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return url.Length <= 96 ? url : url[..96];
        var tail = u.AbsolutePath;
        int slash = tail.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < tail.Length) tail = tail[(slash + 1)..];
        return u.Host + "/" + tail;
    }
}
