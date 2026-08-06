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
    // Two axes, never one value: CHROME chroma for accent-filled controls and the quieter WASH tint used as one
    // ingredient in the Editorial Split's semantic copy surface.
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

        // ArtistPage is kept alive and reused for artist -> artist navigation, just like DetailShell. Shell tint + wash
        // Watch subscriptions live in cover-keyed leaves (CoverPaletteLeaves) — never in this Render — so a graded batch
        // does not rebuild the magazine tree. HeaderImage is the dominant visual; fall back to the avatar only when
        // there is no hero image.
        bool artistReady = artist.State.Value == (byte)LoadState.Ready;
        var currentArtist = artist.Value.Value;
        string? paletteUrl = PaletteImageUrl(currentArtist);
        Element tintBinder = CoverPaletteLeaves.ShellTint(
            paletteUrl, artistReady, colorWashesDisabled, apply: true, _tintOwner, shellTint,
            key: "artist-tint:" + routeKey);

        var compactInteractive = UseSignal(false);
        var pageScroll = UseSignal(0f);   // live page scroll offset → published so the in-page virtualized discography grids window against it
        UseEffect(() =>
        {
            compactInteractive.Value = false;
            pageScroll.Value = 0f;
        }, routeKey);
        // One tree: the boundary renders Body with the resource's pending value, derives its loading paint, then fills
        // the same Body with the loaded artist. The page does not author or pass a separate skeleton subtree.
        var scroll = ScrollView(Skel.Region(artist,
            content: a => Body(a, fansList, svc, go, bridge, compactInteractive),
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
                 PlaybackBridge? bridge, Signal<bool> compactInteractive)
    {
        string uri = a.Uri;
        // Cover-extracted page accent in two treatments: solid chrome uses the provider's opposite-contrast branch;
        // the editorial copy field uses the current page branch and stays lift-only.
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
        // NO pre-release band here. Four surfaces already carry the announcement, each in a place the visitor is
        // already looking: the hero eyebrow pill (narrow windows), the hero pinned card's date line (wide), the compact
        // bar once the hero scrolls away, and the Upcoming masthead at the head of the Releases column — where anyone
        // asking "what's new from this artist" looks first. A band of its own would be a FIFTH copy of one fact, and it
        // would push Top tracks, the thing most visitors came for, down by ~90px on every artist with something coming.
        if (popular.Count > 0)
            sections.Add(TopBand(popular, uri, bridge, svc, a.Pinned, a.Image, a.HeaderImage, a.Name, extras?.PreRelease, go, PlayContext, accent) with { Key = "sec:popular" });
        // The "just dropped" banner earns full-band prominence directly above Albums — not a narrow rail card
        // sharing a column with Artist Pick/Upcoming (see ArtistPage.TopTracks.LatestReleaseBanner).
        if (a.LatestRelease is { Name.Length: > 0, Uri.Length: > 0 } latestRelease)
            sections.Add(Section(Loc.Get(Strings.Artist.LatestRelease), LatestReleaseBanner(latestRelease, go, PlayContext, accent))
                with { Key = "sec:latest-release" });
        // Owned discography stays inline as full virtualized facets; dedicated pages remain deep-link compatible only.
        if (albums.Length > 0 || a.AlbumsTotal > 0) sections.Add(Embed.Comp(
            new DiscographySection.Props(albums),
            () => new DiscographySection(DiscographyKind.Albums, Loc.Get(Strings.Artist.Albums), svc, go, PlayContext, accent))
            with { Key = "sec:albums" });
        if (singles.Length > 0 || a.SinglesTotal > 0) sections.Add(Embed.Comp(
            new DiscographySection.Props(singles),
            () => new DiscographySection(DiscographyKind.Singles, Loc.Get(Strings.Artist.SinglesEps), svc, go, PlayContext, accent))
            with { Key = "sec:singles" });
        if (compilations.Length > 0 || a.CompilationsTotal > 0) sections.Add(Embed.Comp(
            new DiscographySection.Props(compilations),
            () => new DiscographySection(DiscographyKind.Compilations, Loc.Get(Strings.Artist.Compilations), svc, go, PlayContext, accent))
            with { Key = "sec:compilations" });
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
                        Banner(a, uri, Play, Shuffle, Radio, compactInteractive.Value),
                        sentinel,
                        new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, HitTestVisible = false },
                        new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [inner] },
                    ],
                },
            ],
        };
    }
}
