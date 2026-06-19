using System;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

// Maps a route key (+ optional display arg) to its (title, glyph). Shared by the tab strip, the content host and the
// sidebar so a destination's label/icon is defined in ONE place. A "pl:<uri>" key is a playlist; its display name rides
// in the route Arg.
static class ShellNav
{
    public static (string Title, string Glyph) Dest(string key, string? arg = null)
    {
        if (key.StartsWith("pl:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Playlist), Icons.MusicNote);
        if (key.StartsWith("album:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Album), Mdl.Album);
        if (key.StartsWith("artist:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Artist), Mdl.Contact);
        return key switch
        {
            "home"     => (Loc.Get(Strings.Nav.Home), Icons.Home),
            "search"   => (Loc.Get(Strings.Nav.Search), Icons.Search),
            "albums"   => (Loc.Get(Strings.Nav.Albums), Mdl.Album),
            "artists"  => (Loc.Get(Strings.Nav.Artists), Mdl.Contact),
            "liked"    => (Loc.Get(Strings.Nav.LikedSongs), Icons.Heart),
            "podcasts" => (Loc.Get(Strings.Nav.Podcasts), Mdl.RadioTower),
            "local"    => (Loc.Get(Strings.Nav.LocalFiles), Icons.Folder),
            "history"  => (Loc.Get(Strings.Nav.History.Title), Icons.Clock),
            _          => (Loc.Get(Strings.Nav.YourLibrary), Icons.MusicNote),
        };
    }

    public static (string Title, string Glyph) Dest(Route r) => Dest(r.Name, r.Arg);
}
