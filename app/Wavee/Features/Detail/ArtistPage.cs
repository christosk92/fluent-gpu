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
        var artist = UseAsyncResource(ct => svc.Library.GetArtistAsync(uri, ct), EmptyArtist(uri), uri);
        store.EnsureArtists();
        var fansList = store.Artists.Value.Value;

        var pinned = UseSignal(false);
        // ONE skeleton mechanism, and no second tree: pass ONLY the real content. The engine derives the shimmer from
        // content(seed) — i.e. Body rendered against the loadable's empty seed artist (which still lays out hero +
        // top-tracks + bio via FakeData). No shimmer arg. The engine owns Pending/Ready/Failed; a single artist is never empty.
        var scroll = ScrollView(Skel.Region(artist,
            content: a => Body(a, fansList, svc, go, bridge, pinned),
            onFailed: () => ErrorState.Build(artist.Error))) with { Grow = 1f };

        return new BoxEl
        {
            Grow = 1f, ZStack = true,
            Children =
            [
                scroll,
                new BoxEl
                {
                    Grow = 1f, HitTestPassThrough = true, Direction = 1,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                    Padding = new Edges4(0f, WaveeSpace.M, 0f, 0f),
                    Children = [ Embed.Comp(() => new ArtistShyPill(uri, artist, pinned, svc)) ],
                },
            ],
        };
    }

    static Artist EmptyArtist(string uri) => new("", uri, "", null);

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
        if (albums.Length > 0) sections.Add(DiscographyGrid(Loc.Get(Strings.Artist.Albums), albums, go, PlayContext));
        if (singles.Length > 0) sections.Add(DiscographyGrid(Loc.Get(Strings.Artist.SinglesEps), singles, go, PlayContext));
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
        var sentinel = new BoxEl { Height = 0f, ScrollBinds = [ new() { PinTop = 12f, OnFlag = v => pinned.Value = v } ] };
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                Responsive.Of(w => Banner(a, w, uri, Play, Shuffle, Play, go), fallback: 900f),
                sentinel,
                inner,
            ],
        };
    }
}
