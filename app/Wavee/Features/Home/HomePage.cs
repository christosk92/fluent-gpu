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
        if (svc is null) return new BoxEl { Grow = 1f };

        // Re-fetch the home when the library changes (e.g. the live-session playlist-header hydration lands) so playlist
        // names/covers replace the initial URIs without a manual nav.
        var home = UseLoadable(Loadable<HomeFeed>.Pending(FakeData.HomeSeed));   // seed renders the loading shape; later refreshes swap Ready->Ready in place
        var post = UsePost();
        // Start the background home-refresh loop + the library-change subscription ONCE, and tie BOTH to this component's
        // lifetime. A UseSignalEffect that reads no signals runs exactly once on mount; its Reactive.OnCleanup fires on
        // unmount (KeepAlive eviction / navigation away). Without this, each cold remount of Home leaked an orphaned 60s
        // PeriodicTimer loop + a never-unsubscribed observer that COMPOUNDED over a long session (memory + background
        // fetches). Mirrors the LyricsTicker lifecycle pattern (Features/Player/LyricsView.cs).
        Context.UseSignalEffect(() =>
        {
            var cts = new CancellationTokenSource();
            StartHomeRefreshLoop(svc, home, post, cts.Token);
            IDisposable? sub = svc.Library is Wavee.Core.ICollectionEvents ev
                ? ev.CollectionsChanged.Subscribe(Wavee.Backend.Observers.From<Wavee.Core.CollectionKind>(__ => { _ = RefreshHomeOnce(svc, home, post, failIfInitial: false); }))
                : null;
            Reactive.OnCleanup(() => { cts.Cancel(); cts.Dispose(); sub?.Dispose(); });
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
                    : null);
            return tile is BoxEl box ? box with { Key = "home-quick:" + c.Uri, Animate = MotionRecipes.CardRefit } : tile;
        }

        IEnumerable<Element> QuickTiles(IReadOnlyList<HomeCard> cards, string section, int max)
        {
            int n = Math.Min(cards.Count, max);
            for (int i = 0; i < n; i++) yield return Tile(cards[i], section, i);
        }

        Element Shelf(HomeGroup g) => PagedShelf.Create(
            g.Cards.Count,
            cardAt: (i, w) =>
            {
                var c = g.Cards[i];
                // Home descriptions are Spotify HTML fragments. Shelf is the width-aware card path that preserves the
                // 3-line RichText parser, link navigation, circular artist fallback, and explicit cover geometry. The
                // width-agnostic GridCard shortcut regressed all four (literal <a href>, one line, blank artists, and a
                // poorly constrained hover overlay).
                return MediaCard.Shelf(c.Image, c.Title, c.Subtitle ?? "", c.Uri,
                    () => NavCard(c), () => PlayCard(c), w,
                    circular: c.Kind == HomeCardKind.Artist, onNavUri: NavUri);
            },
            measured: true,   // engine measures each card → row is exactly as tall as the tallest card (no reserved height)
            header: g.Title is { } t ? Surfaces.AccentHeader(t, GroupAccent(g)) : new BoxEl());

        Element Group(HomeGroup g) => g.Kind switch
        {
            HomeGroupKind.QuickGrid => QuickGrid(QuickTiles(g.Cards, g.Title ?? "quick-grid", 8)),
            HomeGroupKind.Hero when g.Cards.Count > 0 => Responsive.Of(w => HeroBand(g.Cards[0], GroupAccent(g), PlayCard, NavCard, w), fallback: 560f),
            HomeGroupKind.Shelf when g.Cards.Count > 0 => Shelf(g),   // accent-bar header inside the shelf
            // The "Made for you" region: an accent-bar header + the cards, behind a faintly tinted accent band.
            HomeGroupKind.CollapsedGrid => Surfaces.SectionBand(new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M,
                Children = [ g.Title is { } t ? Surfaces.AccentHeader(t, GroupAccent(g)) : new BoxEl(), QuickGrid(QuickTiles(g.Cards, g.Title ?? "", int.MaxValue)) ],
            }, GroupAccent(g)),
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
                HomeImageDiagnostics.LogFeed(feed);
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

    // Lightweight loading skeleton: match the real home shelves (accent header + square media cards + text bars) instead
    // of generic row rectangles. Sized childless boxes become shimmer bars through the skeleton deriver.
    static Element HomeShimmer()
    {
        const float cardW = 250f;
        const float pad = WaveeSpace.S;
        const float cover = cardW - 2f * pad;

        static Element Bar(float w, float h, float r = 4f) =>
            new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(r) };

        Element Header(float titleW) => new BoxEl
        {
            Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl { Width = 3f, Height = 42f, Corners = CornerRadius4.All(1.5f) },
                Bar(titleW, 28f, 6f),
            ],
        };

        Element ShelfCard() => new BoxEl
        {
            Width = cardW,
            Direction = 1,
            Gap = pad,
            Shrink = 0f,
            Padding = new Edges4(pad, pad, pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Children =
            [
                Bar(cover, cover, WaveeRadius.Card),
                Bar(cover * 0.72f, 18f),
                Bar(cover * 0.86f, 13f),
                Bar(cover * 0.58f, 13f),
            ],
        };

        Element Shelf(float titleW) => new BoxEl
        {
            Direction = 1,
            Gap = WaveeSpace.M,
            ClipToBounds = true,
            Children =
            [
                Header(titleW),
                new BoxEl
                {
                    Direction = 0,
                    Gap = WaveeSpace.M,
                    ClipToBounds = true,
                    Children = [ShelfCard(), ShelfCard(), ShelfCard(), ShelfCard(), ShelfCard(), ShelfCard(), ShelfCard()],
                },
            ],
        };

        return new BoxEl
        {
            Direction = 1,
            Gap = WaveeSpace.XL,
            Children = [Shelf(260f), Shelf(320f), Shelf(240f)],
        };
    }

    static string Id(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }
    // The group's lifted accent (renderer color): the composer-assigned tint (cover-extracted or semantic), or a
    // per-kind fallback for groups with none (the offline/library home). Lift keeps a near-black cover color legible.
    static ColorF GroupAccent(HomeGroup g) => WaveePalette.Lift(WaveePalette.ToColor(
        g.Accent ?? (g.Kind == HomeGroupKind.CollapsedGrid ? 0xFF3B82F6u : 0xFF60CDFFu)));

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
    static Element QuickGrid(IEnumerable<Element> tiles) =>
        AutoGrid(320f, WaveeSpace.M, MediaCard.QuickH, tiles.ToArray());
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

            int total = Math.Min(group.Cards.Count, 8);
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
