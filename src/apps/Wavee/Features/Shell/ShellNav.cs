using System;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Features.Concerts;

namespace Wavee;

// Maps a route key (+ optional display arg) to its (title, glyph). Shared by the tab strip, the content host and the
// sidebar so a destination's label/icon is defined in ONE place. A "pl:<uri>" key is a playlist; its display name rides
// in the route Arg.
static class ShellNav
{
    public static (string Title, string Glyph) Dest(string key, string? arg = null)
    {
        if (key.StartsWith("pl:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Playlist), Icons.MusicNote);
        if (key.StartsWith("album:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Album), Icons.Album);
        // An upcoming release renders the ordinary album page (ContentHost.IsDetail), so it wears the album's label and
        // glyph — without this it would fall through to the "Your Library" default in the tab strip and breadcrumb.
        if (key.StartsWith("prerelease:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Album), Icons.Album);
        if (key.StartsWith("artist:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Artist), Icons.Contact);
        // A podcast/show renders the shared detail surface (ContentHost.IsDetail) exactly like album/artist, so it needs its
        // own label + glyph here — without this it falls through to the "Your Library" default in the tab strip, the history
        // rows, and the sidebar's pinned rows (whose pin id IS the route key). Icons.RadioTower is the podcasts glyph below.
        if (key.StartsWith("show:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Show), Icons.RadioTower);
        if (ConcertRoutes.TryParse(key, out var concertRoute))
            return concertRoute.Kind switch
            {
                ConcertRouteKind.ArtistSchedule => (arg is { Length: > 0 } ? arg + " concerts" : "Artist concerts", Icons.Calendar),
                ConcertRouteKind.Detail => (arg is { Length: > 0 } ? arg : "Concert details", Icons.Calendar),
                _ => ("Concerts", Icons.Calendar),
            };
        return key switch
        {
            "home"     => (Loc.Get(Strings.Nav.Home), Icons.Home),
            "search"   => (arg is { Length: > 0 } ? arg : Loc.Get(Strings.Nav.Search), Icons.Search),
            "albums"   => (Loc.Get(Strings.Nav.Albums), Icons.Album),
            "artists"  => (Loc.Get(Strings.Nav.Artists), Icons.Contact),
            "liked"    => (Loc.Get(Strings.Nav.LikedSongs), Icons.Heart),
            "podcasts" => (Loc.Get(Strings.Nav.Podcasts), Icons.RadioTower),
            "local"    => (Loc.Get(Strings.Nav.LocalFiles), Icons.Folder),
            "history"  => (Loc.Get(Strings.Nav.History.Title), Icons.Clock),
            "settings" => ("Settings", Icons.Settings),
            "api-console" => ("API Console", Icons.Code),
            // The full-page sidebar customizer (§C4.1) — a real destination, so the tab strip / breadcrumb need its label
            // and glyph here like any other page. The key is `SidebarLayoutMenu.CustomizeRoute`, spelled as a LITERAL
            // because this file is source-included by src/apps/Wavee.Tests and that engine-bound file is not.
            "sidebar-customize" => (Loc.Get(Strings.Sidebar.Customizer.Title), Icons.Edit),
            _          => (Loc.Get(Strings.Nav.YourLibrary), Icons.MusicNote),
        };
    }

    public static (string Title, string Glyph) Dest(Route r) => Dest(r.Name, r.Arg);
}
