using System;
using FluentGpu.Controls;

namespace Wavee;

// Maps a route key (+ optional display arg) to its (title, glyph). Shared by the tab strip, the content host and the
// sidebar so a destination's label/icon is defined in ONE place. A "pl:<uri>" key is a playlist; its display name rides
// in the route Arg.
static class ShellNav
{
    public static (string Title, string Glyph) Dest(string key, string? arg = null)
    {
        if (key.StartsWith("pl:", StringComparison.Ordinal)) return (arg ?? "Playlist", Icons.MusicNote);
        return key switch
        {
            "home"     => ("Home", Icons.Home),
            "search"   => ("Search", Icons.Search),
            "albums"   => ("Albums", Mdl.Album),
            "artists"  => ("Artists", Mdl.Contact),
            "liked"    => ("Liked Songs", Icons.Heart),
            "podcasts" => ("Podcasts", Mdl.RadioTower),
            "local"    => ("Local files", Icons.Folder),
            _          => ("Your Library", Icons.MusicNote),
        };
    }

    public static (string Title, string Glyph) Dest(Route r) => Dest(r.Name, r.Arg);
}
