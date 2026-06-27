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
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The artist page (docs/architecture.md §2 "Album & artist") — WaveeMusic's full magazine surface.
// This partial owns route-reactive loading and composes the hero with the magazine sections.
sealed partial class ArtistPage : Component
{
    readonly Signal<Route> _route;
    public ArtistPage(Signal<Route> route) { _route = route; }

    internal static string? UriOf(Route r) =>
        r.Name.StartsWith("artist:", StringComparison.Ordinal) ? r.Name["artist:".Length..] : null;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var bridge = UseContext(PlaybackBridge.Slot);
        var store = UseContext(LibraryStore.Slot);
        if (svc is null || store is null) return new BoxEl { Grow = 1f };

        var route = _route.Value;                       // subscribe → reload on artist→artist nav (reused slot)
        string uri = UriOf(route) ?? "";
        this.UseSoftReveal(dy: 0f, blur: 0f);

        // Keep one stable loadable and re-key it by URI. ContentHost retains one ArtistPage across artist navigation,
        // so replacing the Loadable instance would leave the skeleton region's mounted thunks subscribed to the old artist.
        var artist = UseAsyncResource(ct => svc.Library.GetArtistAsync(uri, ct), PendingArtist(uri), uri);
        store.EnsureArtists();
        var fansList = store.Artists.Value.Value;

        var pinned = UseSignal(false);
        var pageScroll = UseSignal(0f);   // live page scroll offset → published so the in-page virtualized discography grids window against it
        // One tree: the boundary renders Body with the resource's pending value, derives its loading paint, then fills
        // the same Body with the loaded artist. The page does not author or pass a separate skeleton subtree.
        var scroll = ScrollView(Skel.Region(artist,
            shimmerSource: ArtistShimmer,
            content: a => Body(a, fansList, svc, go, bridge, pinned),
            onFailed: () => ErrorState.Build(artist.Error)))
            with
            {
                // Scroll-position restoration keyed by the artist (route). One ScrollView serves successive artists in place,
                // so without a key artist B would inherit A's scroll; with it, B starts at the top and a revisit to A restores it.
                Grow = 1f, ScrollKey = route.Name,
                // Publish the live offset (coarse, ~per-row) so the LazyGrid discography sections window against the page scroll.
                OnScrollGeometryChanged = (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY),
            };

        // Provide the page scroll to the discography LazyGrids deeper in the body (the SwiftUI LazyVGrid-in-ScrollView wiring).
        return Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)pageScroll, new BoxEl
        {
            Grow = 1f, ZStack = true,
            Children =
            [
                scroll,
                new BoxEl   // shy pill overlay
                {
                    Grow = 1f, HitTestPassThrough = true, Direction = 1,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                    Padding = new Edges4(0f, WaveeSpace.M, 0f, 0f),
                    Children = [ Embed.Comp(() => new ArtistShyPill(uri, artist, pinned, svc)) ],
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
        var extras = a.Extras;
        var popular = a.TopTracks is { Count: > 0 } tt ? tt : FakeData.TopTracksOf(a);
        var albumsAll = a.TopAlbums ?? Array.Empty<Album>();
        var albums = albumsAll.Where(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation).ToArray();
        var singles = albumsAll.Where(al => al.Kind is AlbumKind.Single or AlbumKind.EP).ToArray();
        var fans = fansAll.Where(f => f.Uri != uri).Take(12).ToArray();

        void Play() => _ = svc.Player.PlayAsync(uri, 0);
        void Shuffle() { _ = svc.Player.SetShuffleAsync(true); _ = svc.Player.PlayAsync(uri, 0); }
        void PlayContext(string u) => _ = svc.Player.PlayAsync(u, 0);

        var sections = new List<Element>(14);
        if (popular.Count > 0) sections.Add(TopBand(popular, uri, bridge, svc, albumsAll, go, PlayContext));
        // Discography facets: a capped grid + "See all N" that navigates to the dedicated facet page (breadcrumb + full grid).
        if (albums.Length > 0) sections.Add(Embed.Comp(() => new DiscographySection(uri, a.Name, DiscographyKind.Albums, Loc.Get(Strings.Artist.Albums), svc, go, PlayContext)));
        if (singles.Length > 0) sections.Add(Embed.Comp(() => new DiscographySection(uri, a.Name, DiscographyKind.Singles, Loc.Get(Strings.Artist.SinglesEps), svc, go, PlayContext)));
        if (a.AppearsOn is { Count: > 0 } appears) sections.Add(AppearsOnShelf(appears, go, PlayContext));
        if (extras?.Tour is { } tour) sections.Add(TourBannerCard(tour,
            () => { if (extras.Concerts is { Count: > 0 } cs) PlayContext(cs[0].Uri); }));
        if (extras?.MusicVideos is { Count: > 0 } videos) sections.Add(MusicVideosShelf(videos, PlayContext));
        if (extras?.Playlists is { Count: > 0 } playlists) sections.Add(PlaylistsShelf(playlists, go, PlayContext));
        if (extras?.Concerts is { Count: > 0 } concerts) sections.Add(ConcertsRow(concerts));
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
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                Banner(a, uri, Play, Shuffle, Play, go),
                sentinel,
                inner,
            ],
        };
    }
}
