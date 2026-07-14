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
    ColorF _accent = Tok.AccentDefault;   // cover-extracted page accent (lifted); set per-render in Body, read by the hero + section-bar helpers
    ActionServices? _acts;                // shelf-card context menus — resolved per-render, read by the shelf builders
    IOverlayService? _menuOverlay;
    public ArtistPage(Signal<Route> route) { _route = route; }

    /// <summary>The lazy card-menu attach for this page's shelves (albums / playlists / artists / video tracks — the
    /// model is inferred from the uri by Menus.Card). Null when the action system / overlay isn't provided (fake shell).</summary>
    MenuAttach? CardMenu(string uri, string name) => _menuOverlay is { } ov ? Menus.CardAttach(_acts, ov, uri, name) : null;

    internal static string? UriOf(Route r) =>
        r.Name.StartsWith("artist:", StringComparison.Ordinal) ? r.Name["artist:".Length..] : null;

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
        Palette? artPalette = artistReady ? artist.Value.Value.Palette : null;
        ColorF? micaTint = colorWashesDisabled || artPalette is null ? null : Tok.Theme == ThemeKind.Light
            ? WaveePalette.Lift(WaveePalette.ToColor(artPalette.Light)) with { A = 0.05f }
            : WaveePalette.TintedDark(artPalette) with { A = 0.14f };

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
        UseEffect(() => SetTint(micaTint), routeKey, micaTint.HasValue, micaTint.GetValueOrDefault(), Tok.Theme, artistReady, colorWashesDisabled);
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
                    Padding = new Edges4(0f, WaveeSpace.M, 0f, 0f),
                    Children = [ Embed.Comp(() => new ArtistShyPill(uri, artist, pinned, svc)) with { Key = "artist-pill:" + routeKey } ],
                },
            ],
        });
    }

    // Lightweight loading skeleton (finding #7): an explicit shimmer that MIRRORS the real ArtistPage layout — a 420px
    // full-bleed hero (dim image placeholder with the headline block anchored bottom-left: verified pill → big name → meta
    // → action buttons, matching Banner()), then the two-column band (LEFT top-tracks list, RIGHT popular-releases column).
    // Cover-like blocks are ImageEls (deriver → dim MediaColor) so they read distinctly under the brighter text bars; sized
    // childless boxes → bars. This avoids building the full 14-section Body just to derive a skeleton; SmoothResize eases the
    // swap to the real Body on load.
    static Element ArtistShimmer()
    {
        static Element Bar(float w, float h, float r = 4f) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(r) };
        static Element GrowBar(float h, float r = 4f) => new BoxEl { Height = h, Grow = 1f, Corners = CornerRadius4.All(r) };
        static Element Cover(float size, float r) => new ImageEl { Width = size, Height = size, Corners = CornerRadius4.All(r) };

        // Hero: a full-width dim image placeholder (ImageEl stretches in the ZStack) with the headline overlaid bottom-left.
        Element heroCopy = new BoxEl
        {
            Direction = 1, Justify = FlexJustify.End, Gap = WaveeSpace.S,
            Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL),
            Children =
            [
                Bar(96f, 26f, 13f),                 // verified pill
                Bar(360f, 64f, 8f),                 // big artist name
                Bar(480f, 16f),                     // monthly-listeners / followers meta line
                new BoxEl
                {
                    Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                    Children = [ Bar(120f, 48f, 24f), Bar(44f, 44f, 22f), Bar(120f, 44f, 22f), Bar(150f, 44f, 22f) ],   // Play / shuffle / Follow / radio
                },
            ],
        };
        Element hero = new BoxEl { Height = 420f, ZStack = true, Children = [ new ImageEl { Height = 420f }, heroCopy ] };

        // LEFT column (wider): "Top tracks & popular releases" header + track rows (index · cover · title · duration).
        static Element TrackRow() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 48f,
            Children = [ Bar(14f, 14f), Cover(40f, 4f), GrowBar(14f), Bar(36f, 12f) ],
        };
        Element left = new BoxEl
        {
            Direction = 1, Grow = 2f, Basis = 0f, Gap = WaveeSpace.M,
            Children = [ Bar(240f, 20f), new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = [ TrackRow(), TrackRow(), TrackRow(), TrackRow(), TrackRow() ] } ],
        };
        // RIGHT column (narrower): "Popular releases" header + album rows (cover · title · year).
        static Element ReleaseRow() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Height = 64f,
            Children = [ Cover(56f, 8f), new BoxEl { Direction = 1, Grow = 1f, Gap = WaveeSpace.S, Children = [ Bar(130f, 14f), Bar(80f, 12f) ] } ],
        };
        Element right = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.M,
            Children = [ Bar(150f, 20f), new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = [ ReleaseRow(), ReleaseRow(), ReleaseRow(), ReleaseRow() ] } ],
        };
        Element band = new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.XL, Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL),
            Children = [ left, right ],
        };

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
        // Cover-extracted page accent, lifted so a near-black colorDark stays legible (matches album/playlist via
        // DetailShell). Null palette ⇒ the neutral default. Set before the tree builds so every accent helper reads it.
        _accent = a.Palette is { } pal ? WaveePalette.Lift(WaveePalette.Accent(pal)) : Tok.AccentDefault;
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

        var sections = new List<Element>(14);
        if (popular.Count > 0) sections.Add(TopBand(popular, uri, bridge, svc, albumsAll, go, PlayContext));
        // Discography facets: a capped grid + "See all N" that navigates to the dedicated facet page (breadcrumb + full grid).
        if (albums.Length > 0) sections.Add(Embed.Comp(() => new DiscographySection(uri, a.Name, DiscographyKind.Albums, Loc.Get(Strings.Artist.Albums), svc, go, PlayContext, _accent)));
        if (singles.Length > 0) sections.Add(Embed.Comp(() => new DiscographySection(uri, a.Name, DiscographyKind.Singles, Loc.Get(Strings.Artist.SinglesEps), svc, go, PlayContext, _accent)));
        if (a.AppearsOn is { Count: > 0 } appears) sections.Add(AppearsOnShelf(appears, go, PlayContext));
        if (extras?.Tour is { } tour) sections.Add(TourBannerCard(tour,
            () => go(ConcertRoutes.ArtistSchedule(uri), a.Name)));
        if (extras?.MusicVideos is { Count: > 0 } videos) sections.Add(MusicVideosShelf(videos, PlayContext));
        if (extras?.Playlists is { Count: > 0 } playlists) sections.Add(PlaylistsShelf(playlists, go, PlayContext));
        if (extras?.Concerts is { Count: > 0 } concerts) sections.Add(ConcertsRow(concerts, go));
        if (extras?.Merch is { Count: > 0 } merch) sections.Add(MerchRow(merch));
        sections.Add(BiographyBand(a, albums.Length, singles.Length, extras, fans.Length, go));
        if (extras?.Gallery is { Count: > 0 } gallery) sections.Add(GalleryStrip(gallery));
        if (extras?.Related is { Count: > 0 } related) sections.Add(RelatedShelf(related, go, PlayContext));
        else if (fans.Length > 0) sections.Add(FansShelf(fans, go, PlayContext));

        var inner = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XL,
            Padding = new Edges4(32f, 40f, 32f, PlayerDock.Reserve + 40f),
            Children = sections.ToArray(),
        };
        // Arm the shy pill as the hero finishes collapsing (≈offset 380, near full collapse) so the compact bar takes over
        // exactly as the hero's presented height reaches zero — no dead beat, no overlap.
        var sentinel = new BoxEl { Height = 0f, ScrollBinds = [ new() { PinTop = 40f, OnFlag = v => pinned.Value = v } ] };
        // The seam rule: every paint near the hero↔content boundary must terminate at ALPHA 0 over the shell's one
        // continuous backdrop (Mica + the ShellTint this page publishes). The photo's bottom EdgeFade reaches exactly
        // 0 (compositor feather), the copy scrim's last stop is 0, and this wash layer holds its translucent tint
        // through the hero then releases to 0 across the first content band — so no pixel row exists where background
        // responsibility changes hands. Never paint an OPAQUE approximation of the page surface here: the real
        // background is a live Mica composite no constant colour can match, so any opaque bridge/flatten necessarily
        // draws a line where it ends.
        float heroWidth = _heroWidth.Value;
        ColorF wash = Tok.Theme == ThemeKind.Light ? _accent : WaveePalette.BackgroundDark(a.Palette ?? WaveePalette.Neutral);
        ColorF washTint = wash with { A = Tok.Theme == ThemeKind.Light ? 0.12f : 0.16f };
        bool colorWashesDisabled = svc.Settings.Get(WaveeSettings.DisableColorWashes);
        Element washLayer = colorWashesDisabled
            ? new BoxEl()
            : new BoxEl
            {
                Height = ArtistHeroLayout.BlendBackdropHeightFor(heroWidth), HitTestVisible = false,
                Gradient = GradientDown(
                    new GradientStop(0f, washTint),
                    new GradientStop(ArtistHeroLayout.BlendBoundaryFor(heroWidth), washTint),
                    new GradientStop(1f, washTint with { A = 0f })),
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
                        inner,
                    ],
                },
            ],
        };
    }
}
