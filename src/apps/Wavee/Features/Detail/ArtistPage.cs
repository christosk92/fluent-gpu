using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The artist page (docs/architecture.md §2 "Album & artist") — WaveeMusic's full magazine surface.
// This partial owns route-reactive loading and composes the hero with the magazine sections.
sealed partial class ArtistPage : Component
{
    readonly Signal<Route> _route;
    readonly object _tintOwner = new();   // stable ownership across artist -> artist reuse and KeepAlive park/reactivate
    // Two axes, never one value: CHROME chroma (what an accent-filled control paints) and WASH tint (what a translucent
    // backdrop paints). _paletteAccent is the saturation-floored CHROME accent (WaveePalette.ChromeAccent) every filled
    // control on this page reads — hero Play, the Verified pill, the Top-tracks masthead chip, the facet accent bars,
    // the shy pill's Play. _washAccent is the plain brightness-LIFTED accent the light-theme blend backdrop paints, so a
    // chroma change to the CTA can never be mistaken for a wash-alpha regression (and vice versa).
    ColorF? _paletteAccent;                // cover-extracted page CHROME accent; null keeps the semantic default live
    ColorF? _washAccent;                   // cover-extracted WASH accent (lifted only, NOT saturation-floored)
    ColorF _accent => _paletteAccent ?? Tok.AccentDefault;
    ActionServices? _acts;                // shelf-card context menus — resolved per-render, read by the shelf builders
    IOverlayService? _menuOverlay;
    public ArtistPage(Signal<Route> route) { _route = route; }

    /// <summary>The lazy card-menu attach for this page's shelves (albums / playlists / artists / video tracks — the
    /// model is inferred from the uri by Menus.Card). Null when the action system / overlay isn't provided (fake shell).</summary>
    MenuAttach? CardMenu(string uri, string name, Image? image = null, string? subtitle = null, bool circular = false)
        => _menuOverlay is { } ov ? Menus.CardAttach(_acts, ov, uri, name, image, subtitle, circular) : null;

    internal static string? UriOf(Route r) =>
        r.Name.StartsWith("artist:", StringComparison.Ordinal) ? r.Name["artist:".Length..] : null;

    internal static string? PaletteImageUrl(Artist a)
        => a.HeaderImage?.Url is { Length: > 0 } hero ? hero : a.Image?.Url;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        _ = AppearancePrefs.Epoch.Value;
        bool colorWashesDisabled = svc?.Settings.Get(WaveeSettings.DisableColorWashes) ?? false;
        var go = UseContext(HistoryStore.NavCtx);
        var bridge = UseContext(PlaybackBridge.Slot);
        var store = UseContext(LibraryStore.Slot);
        var shellTint = UseContext(ShellTint.Slot);
        _acts = UseContext(ActionServices.Slot);          // shelf-card context menus (Menus.CardAttach)
        _menuOverlay = UseContext(Overlay.Service);
        if (svc is null || store is null) return new BoxEl { Grow = 1f };

        var route = _route.Value;                       // subscribe → reload on artist→artist nav (reused slot)
        string routeKey = route.Name;
        string uri = UriOf(route) ?? "";

        // ContentHost keeps one ArtistPage alive for artist→artist hops. The data is cached per artist, while the
        // scroll/skeleton subtree below is keyed by route so pending/ready branches and child components remount cleanly.
        // One complete read: the V4 artist (identity + discography) then the lazy stats overlay (header stats only). The
        // stats call is standalone-page-scoped (IArtistStatsService) — the Library artist pane never fires it. Offline /
        // no stats provider → EnsureStatsAsync returns null and the V4 artist stands.
        var artist = store.ArtistDetail(uri, async ct =>
        {
            var a = await svc.Library.GetArtistAsync(uri, ct);
            return await svc.ArtistStats.EnsureStatsAsync(uri, ct) ?? a;
        }, PendingArtist(uri));
        store.EnsureArtists();
        var fansList = store.Artists.Value.Value;

        // ArtistPage is kept alive and reused for artist -> artist navigation, just like DetailShell. Claim the shell
        // tint immediately (null while loading clears any prior page's tint), then republish when the overview palette
        // arrives. Previously ArtistPage only drew its in-card gradient, so a whole-window tint on this route was stale
        // state from whichever detail page happened to be visited before it.
        bool artistReady = artist.State.Value == (byte)LoadState.Ready;
        var currentArtist = artist.Value.Value;
        string? paletteUrl = PaletteImageUrl(currentArtist);
        // Grade the artwork the hero actually presents. HeaderImage is the dominant visual on this page; using the
        // small avatar instead made an incidental portrait colour override the full-bleed hero it visibly belongs to.
        // Fall back to the avatar only when there is no hero image. Watch that one source so the shell tint lands
        // without coupling
        // this page to every batch the discography grid kicks off while scrolling.
        _ = SpotifyLive.CoverColorPlane.Current.Watch(paletteUrl).Value;
        var artPalette = artistReady ? Surfaces.SchemeFor(paletteUrl) : null;
        ColorF? micaTint = colorWashesDisabled || artPalette is not { } artScheme ? null : Tok.Theme == ThemeKind.Light
            ? WaveePalette.Lift(WaveePalette.ToColor(artScheme.TextBase)) with { A = 0.05f }
            : WaveePalette.TintedDark(artScheme) with { A = 0.14f };

        void SetTint(ColorF? color)
        {
            if (shellTint is not null) shellTint.Value = new ShellTintState(color, _tintOwner);
        }
        void ClearTint()
        {
            if (shellTint is not null && ReferenceEquals(shellTint.Peek().Owner, _tintOwner)) shellTint.Value = default;
        }

        // Exact deps are intentional: this is a low-frequency navigation/data effect, and route identity must refresh
        // ownership even when two artists happen to have the same extracted colour.
        UseEffect(() => SetTint(micaTint), DepKey.From(HashCode.Combine(routeKey, micaTint.HasValue, micaTint.GetValueOrDefault(), Tok.Theme, artistReady, colorWashesDisabled)));
        UseActivation(onActivated: () => SetTint(micaTint), onDeactivated: ClearTint);

        var pinned = UseSignal(false);
        var pageScroll = UseSignal(0f);   // live page scroll offset → published so the in-page virtualized discography grids window against it
        UseEffect(() =>
        {
            pinned.Value = false;
            pageScroll.Value = 0f;
        }, routeKey);
        // One tree: the boundary renders Body with the resource's pending value, derives its loading paint, then fills
        // the same Body with the loaded artist. The page does not author or pass a separate skeleton subtree.
        var scroll = ScrollView(Skel.Region(artist,
            shimmerSource: ArtistShimmer,
            content: a => Body(a, fansList, svc, go, bridge, pinned),
            onFailed: () => ErrorState.Build(artist.Error),
            group: routeKey)
            with
            {
                Key = "artist-region:" + routeKey,
            })
            with
            {
                // Scroll-position restoration keyed by the artist (route). One ScrollView serves successive artists in place,
                // so without a key artist B would inherit A's scroll; with it, B starts at the top and a revisit to A restores it.
                Key = "artist-scroll:" + routeKey, Grow = 1f, ScrollKey = routeKey,
                // Publish the live offset (24px write-throttle floor; LazyGrid windowing is per-row inside the control).
                OnScrollGeometryChanged = (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY),
            };

        // Provide the page scroll to the discography LazyGrids deeper in the body (the SwiftUI LazyVGrid-in-ScrollView wiring).
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, new BoxEl
        {
            Key = "artist-page:" + routeKey, Grow = 1f, ZStack = true,
            Children =
            [
                scroll,
                new BoxEl   // shy pill overlay
                {
                    Grow = 1f, HitTestPassThrough = true, Direction = 1,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                    // The named half of ArtistShyPill.Clearance: this margin + the pill's Height is what the sticky
                    // facet headers pin BELOW (DiscographySection's stickyClearance) — keep them in lockstep.
                    Padding = new Edges4(0f, ArtistShyPill.TopMargin, 0f, 0f),
                    Children = [ ArtistShyPill.Create(uri, artist, svc, pinned) ],
                },
            ],
        });
    }

    // Lightweight loading skeleton (finding #7): an explicit shimmer that MIRRORS the real ArtistPage layout — a
    // full-bleed hero on the SAME ArtistHeroLayout.HeroHeightFor rule as Banner() (dim image placeholder with the
    // headline block anchored bottom-left: verified pill → big name → meta → action buttons), then the two-column band
    // (LEFT top-tracks list, RIGHT popular-releases column).
    // Cover-like blocks are ImageEls (deriver → dim MediaColor) so they read distinctly under the brighter text bars; sized
    // childless boxes → bars. This avoids building the full 14-section Body just to derive a skeleton; SmoothResize eases the
    // swap to the real Body on load.
    static Element ArtistShimmer()
    {
        static Element Bar(float w, float h, float r = 4f) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(r) };
        static Element GrowBar(float h, float r = 4f) => new BoxEl { Height = h, Grow = 1f, Corners = CornerRadius4.All(r) };
        static Element Cover(float size, float r) => new ImageEl { Width = size, Height = size, Corners = CornerRadius4.All(r) };

        // Hero: a full-width dim image placeholder (ImageEl stretches in the ZStack) with the headline overlaid bottom-left.
        // The hero HEIGHT is width-dependent (the wide banner grows past ~1312px), so the shimmer self-measures through
        // Responsive rather than freezing a constant — a hardcoded 420 would step-jump on reveal in a wide window. The
        // gutter matches the real hero copy (ArtistHeroLayout.PageGutter horizontally, Spacing.XL vertically).
        Element hero = Responsive.Of(w =>
        {
            float heroH = ArtistHeroLayout.HeroHeightFor(w);
            Element heroCopy = new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, Gap = Spacing.S,
                Padding = new Edges4(ArtistHeroLayout.PageGutter, Spacing.XL, ArtistHeroLayout.PageGutter, Spacing.XL),
                Children =
                [
                    Bar(96f, 26f, 13f),                 // verified pill
                    Bar(360f, 48f, 8f),                 // big artist name (HeroSize's widest step)
                    Bar(480f, 22f),                     // monthly-listeners / followers meta line (18px counts)
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, Spacing.S, 0f, 0f),
                        Children = [ Bar(120f, 48f, 24f), Bar(44f, 44f, 22f), Bar(120f, 44f, 22f), Bar(150f, 44f, 22f) ],   // Play / shuffle / Follow / radio
                    },
                ],
            };
            return new BoxEl { Height = heroH, ZStack = true, Children = [ new ImageEl { Height = heroH }, heroCopy ] };
        }, fallback: ArtistHeroLayout.WideWidth);

        // LEFT: Top tracks chart (rank · art · title · plays). RIGHT: Releases masthead + strip.
        static Element ChartRow() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Height = 48f,
            Children = [ Bar(14f, 10f), Cover(36f, 4f), GrowBar(12f), Bar(28f, 10f) ],
        };
        Element leftCol = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, Grow = 2f, Basis = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Gap = 2f, Children = [ ChartRow(), ChartRow(), ChartRow(), ChartRow(), ChartRow() ] },
                new BoxEl { Direction = 1, Grow = 1f, Gap = 2f, Children = [ ChartRow(), ChartRow(), ChartRow(), ChartRow(), ChartRow() ] },
            ],
        };
        Element mast = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.M),
            Children = [ Cover(112f, 8f), new BoxEl { Direction = 1, Grow = 1f, Gap = Spacing.S, Children = [ Bar(80f, 10f), Bar(140f, 14f), Bar(100f, 12f) ] } ],
        };
        // The Releases column's two shapes — the WIDE chip strip and the STACKED row list (ArtistPage.TopTracks.cs
        // BuildReleaseStrip / BuildReleaseList). A skeleton that always drew chips step-jumped on reveal at narrow
        // widths, because the live column is a row list there.
        static Element Chip() => new BoxEl
        {
            Direction = 1, Grow = 1f, Gap = Spacing.S,
            Children = [ Cover(72f, 8f), Bar(60f, 12f), Bar(40f, 10f) ],
        };
        static Element ListRow() => new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, Padding = new Edges4(10f, 6f, 10f, 6f),
            Children =
            [
                Cover(52f, Radii.Control),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f, Children = [ Bar(180f, 13f), Bar(90f, 11f) ] },
            ],
        };
        // The band MIRRORS the real TopBand's stack rule (row above 760, one stacked column below) including the
        // Releases variant swap, so the reveal is a cross-fade rather than a re-layout. Deliberately NO hysteresis: the
        // latch exists to stop a slow window DRAG from flipping the live column, and a skeleton is on screen for one
        // load — it is never dragged across the breakpoint, and a static builder has no page instance to latch on.
        Element band = Responsive.Of(w =>
        {
            bool wide = w >= 760f;
            Element left = new BoxEl
            {
                Direction = 1, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f, Gap = Spacing.M,
                Children = [ Bar(140f, 20f), leftCol ],
            };
            Element releases = wide
                ? new BoxEl { Direction = 0, Gap = Spacing.S, Children = [ Chip(), Chip(), Chip() ] }
                : new BoxEl { Direction = 1, Gap = 2f, Children = [ ListRow(), ListRow(), ListRow(), ListRow() ] };
            Element right = new BoxEl
            {
                Direction = 1, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f, Gap = Spacing.M,
                Children = [ Bar(100f, 20f), mast, releases ],
            };
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Padding = new Edges4(ArtistHeroLayout.PageGutter, Spacing.XL, ArtistHeroLayout.PageGutter, Spacing.XL),
                Children = [ left, right ],
            };
        }, fallback: 900f);

        return new BoxEl { Direction = 1, Children = [ hero, band ] };
    }

    static Artist PendingArtist(string uri)
    {
        // Async resources need a value while Pending because Body is data-driven (sections are conditional). Reuse a
        // route-stable catalog shape so the one real Body has representative hero/tracks/releases/biography geometry.
        // The engine replaces all text/media paint, so none of this sample content is shown to the user.
        var shape = FakeData.Artist(FakeData.IndexFromUri(uri));
        return shape with { Id = "", Uri = uri, Name = "" };
    }

    Element Body(Artist a, IReadOnlyList<Artist> fansAll, Services svc, Action<string, string?> go,
                 PlaybackBridge? bridge, Signal<bool> pinned)
    {
        string uri = a.Uri;
        // Cover-extracted page accent in TWO treatments, mirroring DetailShell exactly: solid CHROME uses the provider's
        // opposite-contrast branch (soft light treatment on dark heroes; stronger dark treatment on pale pages), then
        // ChromeAccent's lift/saturation floor. The WASH uses the current page branch and stays lift-only, because wash
        // strength is an alpha decision owned by the gradient stops below.
        // Null palette ⇒ the neutral default. Both are set before the tree builds so every accent helper reads them.
        string? paletteUrl = PaletteImageUrl(a);
        var pagePal = Surfaces.SchemeFor(paletteUrl);
        var chromePal = Surfaces.ChromeSchemeFor(paletteUrl);
        _paletteAccent = chromePal is { } pal ? WaveePalette.ChromeAccent(pal) : null;
        _washAccent = pagePal is { } wp ? WaveePalette.Lift(WaveePalette.Accent(wp)) : null;
        Func<ColorF> accent = () => _accent;
        var extras = a.Extras;
        var popular = a.TopTracks is { Count: > 0 } tt ? tt : FakeData.TopTracksOf(a);
        var albumsAll = a.TopAlbums ?? Array.Empty<Album>();
        var albums = albumsAll.Where(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation).ToArray();
        var singles = albumsAll.Where(al => al.Kind is AlbumKind.Single or AlbumKind.EP).ToArray();
        var fans = fansAll.Where(f => f.Uri != uri).Take(12).ToArray();

        void Play() => _ = svc.Player.PlayAsync(uri, 0);
        void Shuffle() { _ = svc.Player.SetShuffleAsync(true); _ = svc.Player.PlayAsync(uri, 0); }
        void PlayContext(string u) => _ = svc.Player.PlayAsync(u, 0);
        // The hero "Artist Radio" pill: seed a real radio off the artist (Apple-Music-style, never interrupting) + toast
        // — NOT a plain replay of the artist context (the previous bug passed Play as the radio callback).
        void Radio() => RadioLaunch.Start(svc.Player, uri, a.Name, go);

        // EVERY section carries a stable Key. This list is built CONDITIONALLY off a loadable: extras/top-albums arrive
        // after the first Ready render, so sections appear mid-stream and shift every later sibling's index. Keyless
        // children pair by raw list index + ElementTypeId, and all but the two DiscographySection entries are BoxEl —
        // one identical type — so an inserted section made each following one pair against its NEIGHBOUR's old subtree:
        // silent cross-wiring of shelves plus a remount cascade of their cards (the LazyNowPlayingOverlay/CoverShimmer
        // mount mass on artist loads). Keyed children pair by key regardless of position, so an insert now costs exactly
        // the inserted section. Keys must stay UNIQUE (a duplicate silently mounts a second node) and must stay stable
        // across renders — note "related" and "fans" are deliberately DISTINCT keys: they are alternatives holding
        // different data, and reusing one key would reuse the shelf subtree across the swap.
        var sections = new List<Element>(15);
        // NO pre-release band here. Four surfaces already carry the announcement, each in a place the visitor is
        // already looking: the hero eyebrow pill (narrow windows), the hero pinned card's date line (wide), the shy
        // pill once the hero scrolls away, and the Upcoming masthead at the head of the Releases column — where anyone
        // asking "what's new from this artist" looks first. A band of its own would be a FIFTH copy of one fact, and it
        // would push Top tracks, the thing most visitors came for, down by ~90px on every artist with something coming.
        if (popular.Count > 0)
            sections.Add(TopBand(popular, uri, bridge, svc, a.LatestRelease, a.PopularReleases, extras?.PreRelease, go, PlayContext, accent) with { Key = "sec:popular" });
        // Discography facets: a capped grid + "See all N" that navigates to the dedicated facet page (breadcrumb + full grid).
        if (albums.Length > 0) sections.Add(Embed.Comp(() => new DiscographySection(uri, a.Name, DiscographyKind.Albums, Loc.Get(Strings.Artist.Albums), svc, go, PlayContext, accent, ArtistShyPill.Clearance)) with { Key = "sec:albums" });
        if (singles.Length > 0) sections.Add(Embed.Comp(() => new DiscographySection(uri, a.Name, DiscographyKind.Singles, Loc.Get(Strings.Artist.SinglesEps), svc, go, PlayContext, accent, ArtistShyPill.Clearance)) with { Key = "sec:singles" });
        if (a.AppearsOn is { Count: > 0 } appears) sections.Add(AppearsOnShelf(appears, go, PlayContext) with { Key = "sec:appears-on" });
        if (extras?.Tour is { } tour) sections.Add(TourBannerCard(tour,
            () => go(ConcertRoutes.ArtistSchedule(uri), a.Name)) with { Key = "sec:tour" });
        if (extras?.MusicVideos is { Count: > 0 } videos) sections.Add(MusicVideosShelf(videos, PlayContext) with { Key = "sec:music-videos" });
        if (extras?.Playlists is { Count: > 0 } playlists) sections.Add(PlaylistsShelf(playlists, go, PlayContext) with { Key = "sec:playlists" });
        if (extras?.Concerts is { Count: > 0 } concerts) sections.Add(ConcertsRow(concerts, go) with { Key = "sec:concerts" });
        if (extras?.Merch is { Count: > 0 } merch) sections.Add(MerchRow(merch) with { Key = "sec:merch" });
        sections.Add(BiographyBand(a, albums.Length, singles.Length, extras, fans.Length, go) with { Key = "sec:biography" });
        if (extras?.Gallery is { Count: > 0 } gallery) sections.Add(GalleryStrip(gallery) with { Key = "sec:gallery" });
        if (extras?.Related is { Count: > 0 } related) sections.Add(RelatedShelf(related, go, PlayContext) with { Key = "sec:related" });
        else if (fans.Length > 0) sections.Add(FansShelf(fans, go, PlayContext) with { Key = "sec:fans" });

        var inner = new BoxEl
        {
            Direction = 1, Gap = Spacing.XL,
            // DetailShell's clamp idiom ("detail:two-column"): Grow toward the wrapper row's free width, capped at 1600,
            // with the Justify=Center wrapper below centring the capped block. MaxWidth+AlignSelf.Center is NOT enough —
            // a non-Stretch child arranges at its MEASURED width, so the fluid Grow/Basis=0 sections would under-fill
            // and a wider-than-window fixed shelf would overflow both gutters.
            // The gutter is ArtistHeroLayout.PageGutter — the SAME inset the hero copy uses, so the two line up.
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = 1600f,
            // Top pad kept tight so Top tracks sits close under the hero Play/Follow row (was 40).
            Padding = new Edges4(ArtistHeroLayout.PageGutter, Spacing.M, ArtistHeroLayout.PageGutter, PlayerDock.Reserve + 40f),
            Children = sections.ToArray(),
        };
        // Arm the shy pill as the hero finishes collapsing (≈offset 380, near full collapse) so the compact bar takes over
        // exactly as the hero's presented height reaches zero — no dead beat, no overlap.
        var sentinel = new BoxEl { Height = 0f, ScrollBinds = [ new() { PinTop = 40f, OnFlag = v => pinned.Value = v } ] };
        // The seam rule: every paint near the hero↔content boundary must terminate at ALPHA 0 over the shell's one
        // continuous backdrop (Mica + the ShellTint this page publishes). The photo's bottom EdgeFade reaches exactly
        // 0 (compositor feather), the copy scrim's last stop is 0, and this wash layer ramps its translucent tint down
        // across the hero and reaches 0 just inside the first content band — so no pixel row exists where background
        // responsibility changes hands. Never paint an OPAQUE approximation of the page surface here: the real
        // background is a live Mica composite no constant colour can match, so any opaque bridge/flatten necessarily
        // draws a line where it ends.
        float heroWidth = _heroWidth.Value;
        bool lightWash = Tok.Theme == ThemeKind.Light;
        // The light branch paints the NON-vivid wash accent (see the field docs): a translucent backdrop must not gain
        // chroma just because the CTA did. Reuses the current-theme `pagePal` grading resolved above.
        ColorF wash = lightWash ? (_washAccent ?? Tok.AccentDefault)
                    : WaveePalette.BackgroundDark(pagePal ?? WaveePalette.Neutral);
        // A RAMP, not a plateau: strongest behind the hero photo, already thin by the hero↔content boundary
        // (BlendBoundaryFor, derived from the SHORT ContentBlendTail), gone by the end of the tail. The old flat-then-drop
        // shape held its full tint for the hero's whole height and then dumped it across 320px of content — which is what
        // read as a tinted plate over the first band. Light stays weaker than dark: the same alpha over a light surface
        // is a visible cast, over a dark one it barely registers.
        bool colorWashesDisabled = svc.Settings.Get(WaveeSettings.DisableColorWashes);
        Element washLayer = colorWashesDisabled
            ? new BoxEl()
            : new BoxEl
            {
                Height = ArtistHeroLayout.BlendBackdropHeightFor(heroWidth), HitTestVisible = false,
                Gradient = GradientDown(
                    new GradientStop(0f, wash with { A = lightWash ? 0.20f : 0.30f }),
                    new GradientStop(ArtistHeroLayout.BlendBoundaryFor(heroWidth), wash with { A = lightWash ? 0.06f : 0.08f }),
                    new GradientStop(1f, wash with { A = 0f })),
            };
        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                washLayer,
                new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        Banner(a, uri, Play, Shuffle, Radio, go),
                        sentinel,
                        new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [inner] },
                    ],
                },
            ],
        };
    }
}
