namespace Wavee.Core;

// The sidebar's information-architecture data — the "Your Library" counts and the (folder-capable) playlist tree.
// Framework-neutral, driven by FakeData; the UI binds these instead of hard-coded literal arrays.

/// <summary>A lightweight playlist row for the sidebar (no track payload — the detail page loads that on demand).
/// <paramref name="MosaicTiles"/> (when <paramref name="Cover"/> is null) carries up to 4 distinct album-cover URLs to
/// compose a 2×2 mosaic, the way Spotify renders a cover-less playlist; recomputed from the live tracklist.</summary>
public sealed record PlaylistSummary(string Uri, string Name, string OwnerName, int TrackCount, Image? Cover,
    System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null);

/// <summary>A node in the sidebar playlist tree: either a single playlist or a folder of playlists (WaveeMusic's
/// hierarchical Playlists section — flat leaves + collapsible folders).</summary>
public abstract record PlaylistNode;
public sealed record PlaylistLeaf(PlaylistSummary Playlist) : PlaylistNode;
public sealed record PlaylistFolder(string Id, string Name, IReadOnlyList<PlaylistSummary> Items) : PlaylistNode;

/// <summary>The "Your Library" badge counts (Albums / Artists / Liked Songs / Podcasts).</summary>
public sealed record LibraryStats(int Albums, int Artists, int LikedSongs, int Podcasts);
