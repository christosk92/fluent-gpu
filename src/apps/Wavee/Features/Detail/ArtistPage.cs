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
    // The context band's scroll-spy registry: the page's scroll viewport + one node per pivot-visible section. Written
    // from OnRealized, read by ContextPivot's scroll effect — never an ancestor relationship, so the band can live
    // inside the pinned hero while the sections live in the magazine body.
    readonly SectionAnchors _anchors = new();
    // Cover-extracted page CHROME accent for accent-filled controls; null keeps the semantic default live.
    // WASH accent lives in CoverPaletteLeaves (veil / blend wash) — not a page field.
    ColorF? _paletteAccent;
    ColorF _accent => _paletteAccent ?? Tok.AccentDefault;
    ActionServices? _acts;                // shelf-card context menus — resolved per-render, read by the shelf builders
    IOverlayService? _menuOverlay;
    public ArtistPage(Signal<Route> route) { _route = route; }

    /// <summary>The lazy card-menu attach for this page's shelves (albums / playlists / artists / video tracks — the
    /// model is inferred from the uri by Menus.Card). Null when the action system / overlay isn't provided (fake shell).</summary>
    MenuAttach? CardMenu(string uri, string name, Image? image = null, string? subtitle = null, bool circular = false)
        => _menuOverlay is { } ov ? Menus.CardAttach(_acts, ov, uri, name, image, subtitle, circular) : null;

    /// <summary>The drag source for this page's shelf cards. Like <see cref="CardMenu"/> it reads <c>_acts</c> at
    /// PROMOTION (the payload factory is cold), so a page whose services arrive after the first render still drags.
    /// <paramref name="kind"/> is explicit where the shelf knows it and derived from the uri where it does not.</summary>
    DragSource CardDrag(WaveeResourceKind kind, string uri, string name, Image? cover)
        => Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(kind, uri, name, cover, _acts));

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
        var shellMaterial = UseContext(ShellMaterial.Slot);
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

        // ArtistPage is kept alive and reused for artist -> artist navigation, just like DetailShell. Shell tint + wash
        // Watch subscriptions live in cover-keyed leaves (CoverPaletteLeaves) — never in this Render — so a graded batch
        // does not rebuild the magazine tree. HeaderImage is the dominant visual; fall back to the avatar only when
        // there is no hero image.
        bool artistReady = artist.State.Value == (byte)LoadState.Ready;
        var currentArtist = artist.Value.Value;
        string? paletteUrl = PaletteImageUrl(currentArtist);
        // The shell material tint is published from a leaf: the Watch subscription, the derived tone and the
        // owner-gated set/clear effects all live inside CoverPaletteLeaves.ShellTint, so a graded batch repaints one
        // zero-size node instead of rebuilding the magazine tree. Flat arm only (Wash: null) — the three-layer radial
        // wash belongs to Home.
        Element tintBinder = CoverPaletteLeaves.ShellTint(
            paletteUrl, artistReady, colorWashesDisabled, apply: true, _tintOwner, shellMaterial,
            key: "artist-tint:" + routeKey);

        var compactInteractive = UseSignal(false);
        var pageScroll = UseSignal(0f);   // live page scroll offset → published so the in-page virtualized discography grids window against it
        UseEffect(() =>
        {
            compactInteractive.Value = false;
            pageScroll.Value = 0f;
            // A new artist is a new set of sections in a REUSED slot. Dropping the registrations here means a pivot
            // click during the first frames of the new artist can never land on the previous artist's node.
            _anchors.Reset();
        }, routeKey);
        // One tree: the boundary renders Body with the resource's pending value, derives its loading paint, then fills
        // the same Body with the loaded artist. The page does not author or pass a separate skeleton subtree.
        var scroll = ScrollView(Skel.Region(artist,
            content: a => Body(a, fansList, svc, go, bridge, compactInteractive, pageScroll),
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
                // The band's pivot resolves "which section am I in" against THIS viewport, and scrolls THIS viewport
                // on a click — an explicit handle, so a nested scroller deeper in the magazine can never be the thing
                // that moves.
                OnRealized = h => _anchors.Viewport = h,
                // No colour edge cue: the default cue resolved its surface by ANCESTOR walk, which sails past the opaque
                // content pane (a ZStack sibling) to the untinted ShellGround — a neutral one-rung-darker band painted
                // OVER the pinned compact bar. The shy header itself is the occlusion cue on this page.
                EdgeCues = ScrollEdgeCues.None,
                // Publish the live offset (24px write-throttle floor; LazyGrid windowing is per-row inside the control).
                OnScrollGeometryChanged = (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY),
            };

        // Provide the page scroll to the discography LazyGrids deeper in the body (the SwiftUI LazyVGrid-in-ScrollView wiring).
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, new BoxEl
        {
            Key = "artist-page:" + routeKey, Grow = 1f, Direction = 1,
            Children = [tintBinder, scroll],
        });
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
                 PlaybackBridge? bridge, Signal<bool> compactInteractive, IReadSignal<float> pageScroll)
    {
        string uri = a.Uri;
        // Cover-extracted chrome accent (null ⇒ semantic default). Wash/veil accents are owned by CoverPaletteLeaves.
        string? paletteUrl = PaletteImageUrl(a);
        var chromePal = Surfaces.ChromeSchemeFor(paletteUrl);
        _paletteAccent = chromePal is { } pal ? WaveePalette.ChromeAccent(pal) : null;
        Func<ColorF> accent = () => _accent;
        var extras = a.Extras;
        var popular = a.TopTracks is { Count: > 0 } tt ? tt : FakeData.TopTracksOf(a);
        var albumsAll = a.TopAlbums ?? Array.Empty<Album>();
        var albums = albumsAll.Where(al => al.Kind == AlbumKind.Album).ToArray();
        var singles = albumsAll.Where(al => al.Kind is AlbumKind.Single or AlbumKind.EP).ToArray();
        var compilations = albumsAll.Where(al => al.Kind == AlbumKind.Compilation).ToArray();
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
        // The pivot is BUILT FROM the section list, in the same pass, so the two can never disagree about what the
        // page renders. A section joins the pivot only when it is a DESTINATION — a place a visitor would aim at.
        // The two banners (latest release, tour) are announcements sitting between destinations and are deliberately
        // excluded: naming them in the pivot would advertise a jump to a 90-DIP card.
        var pivot = new List<ContextPivotItem>(14);
        void Sec(string key, string? pivotLabel, Element body)
        {
            if (pivotLabel is null) { sections.Add(body with { Key = "sec:" + key }); return; }
            pivot.Add(new ContextPivotItem(key, pivotLabel));
            sections.Add(ContextBand.Anchor(_anchors, key, body with { Key = "sec:" + key }));
        }
        // NO pre-release band here. Four surfaces already carry the announcement, each in a place the visitor is
        // already looking: the hero eyebrow pill (narrow windows), the hero pinned card's date line (wide), the compact
        // bar once the hero scrolls away, and the Upcoming masthead at the head of the Releases column — where anyone
        // asking "what's new from this artist" looks first. A band of its own would be a FIFTH copy of one fact, and it
        // would push Top tracks, the thing most visitors came for, down by ~90px on every artist with something coming.
        if (popular.Count > 0)
            Sec("popular", Loc.Get(Strings.Artist.TopTracks),
                TopBand(popular, uri, bridge, svc, a.Pinned, a.Image, a.HeaderImage, a.Name, extras?.PreRelease, go, PlayContext, accent));
        // The "just dropped" banner earns full-band prominence directly above Albums — not a narrow rail card
        // sharing a column with Artist Pick/Upcoming (see ArtistPage.TopTracks.LatestReleaseBanner).
        if (a.LatestRelease is { Name.Length: > 0, Uri.Length: > 0 } latestRelease)
            Sec("latest-release", null,
                Section(Loc.Get(Strings.Artist.LatestRelease), LatestReleaseBanner(latestRelease, go, PlayContext, accent)));
        // Owned discography stays inline as full virtualized facets; dedicated pages remain deep-link compatible only.
        if (albums.Length > 0 || a.AlbumsTotal > 0) Sec("albums", Loc.Get(Strings.Artist.Albums), Embed.Comp(
            new DiscographySection.Props(albums),
            () => new DiscographySection(DiscographyKind.Albums, Loc.Get(Strings.Artist.Albums), svc, go, PlayContext, accent)));
        if (singles.Length > 0 || a.SinglesTotal > 0) Sec("singles", Loc.Get(Strings.Artist.SinglesEps), Embed.Comp(
            new DiscographySection.Props(singles),
            () => new DiscographySection(DiscographyKind.Singles, Loc.Get(Strings.Artist.SinglesEps), svc, go, PlayContext, accent)));
        if (compilations.Length > 0 || a.CompilationsTotal > 0) Sec("compilations", Loc.Get(Strings.Artist.Compilations), Embed.Comp(
            new DiscographySection.Props(compilations),
            () => new DiscographySection(DiscographyKind.Compilations, Loc.Get(Strings.Artist.Compilations), svc, go, PlayContext, accent)));
        if (a.AppearsOn is { Count: > 0 } appears) Sec("appears-on", Loc.Get(Strings.Artist.AppearsOn), AppearsOnShelf(appears, go, PlayContext));
        if (extras?.Tour is { } tour) Sec("tour", null, TourBannerCard(tour,
            () => go(ConcertRoutes.ArtistSchedule(uri), a.Name)));
        if (extras?.MusicVideos is { Count: > 0 } videos) Sec("music-videos", Loc.Get(Strings.Artist.MusicVideos), MusicVideosShelf(videos, PlayContext));
        if (extras?.Playlists is { Count: > 0 } playlists) Sec("playlists", Loc.Get(Strings.Artist.PlaylistsDiscovery), PlaylistsShelf(playlists, go, PlayContext));
        if (extras?.Concerts is { Count: > 0 } concerts) Sec("concerts", Loc.Get(Strings.Artist.UpcomingConcerts), ConcertsRow(concerts, go));
        if (extras?.Merch is { Count: > 0 } merch) Sec("merch", Loc.Get(Strings.Artist.Merch), MerchRow(merch));
        Sec("biography", Loc.Get(Strings.Artist.Biography), BiographyBand(a, albums.Length, singles.Length, extras, fans.Length, go));
        if (extras?.Gallery is { Count: > 0 } gallery) Sec("gallery", Loc.Get(Strings.Artist.Gallery), GalleryStrip(gallery));
        if (extras?.Related is { Count: > 0 } related) Sec("related", Loc.Get(Strings.Detail.FansAlsoLike), RelatedShelf(related, go, PlayContext));
        else if (fans.Length > 0) Sec("fans", Loc.Get(Strings.Detail.FansAlsoLike), FansShelf(fans, go, PlayContext));

        var inner = new BoxEl
        {
            // W1a-alias: WaveeSize.SectionGap — the ONE page-section gap (32). Was Spacing.XL (20), which made this page's
            // sections sit closer together than every other page's for no stated reason.
            Direction = 1, Gap = WaveeSize.SectionGap,
            // DetailShell's clamp idiom ("detail:two-column"): Grow toward the wrapper row's free width, capped at 1600,
            // with the Justify=Center wrapper below centring the capped block. MaxWidth+AlignSelf.Center is NOT enough —
            // a non-Stretch child arranges at its MEASURED width, so the fluid Grow/Basis=0 sections would under-fill
            // and a wider-than-window fixed shelf would overflow both gutters.
            // The gutter is ArtistHeroLayout.PageGutter — the SAME inset the hero copy uses, so the two line up.
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = 1600f,
            // Top pad kept tight so Top tracks sits close under the hero Play/Follow row (was 40).
            Padding = new Edges4(ArtistHeroLayout.PageGutterFor(_heroWidth.Value), Spacing.M, ArtistHeroLayout.PageGutterFor(_heroWidth.Value), PlayerDock.Reserve + 40f),
            Children = sections.ToArray(),
        };
        // One edge-only handoff: as the full hero reaches its 56-DIP floor, the compact presentation takes input.
        // It stays inside the already-pinned hero; this sentinel paints nothing and never becomes a second chrome layer.
        var sentinel = new BoxEl
        {
            Height = 0f, HitTestVisible = false,
            ScrollBinds = [new() { PinTop = ArtistHeroLayout.CompactIdentityHeight,
                OnFlag = v => compactInteractive.Value = v }],
        };
        float heroWidth = _heroWidth.Value;
        bool colorWashesDisabled = svc.Settings.Get(WaveeSettings.DisableColorWashes);
        // Cover-keyed leaf: a late grading re-renders only the wash box, not this Body / magazine sections.
        Element washLayer = CoverPaletteLeaves.ArtistBlendWash(
            paletteUrl, heroWidth, colorWashesDisabled, key: "artist-wash:" + uri);
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
                        Banner(a, uri, Play, Shuffle, Radio, compactInteractive.Value, pivot.ToArray(), pageScroll),
                        sentinel,
                        new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                        new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [inner] },
                    ],
                },
            ],
        };
    }
}
