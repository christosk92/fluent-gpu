namespace Wavee.Core;

// The source-agnostic, CONDENSED home model (see docs/architecture.md §2). A real Spotify home feed is dozens of
// sections (the export has 31 — 9 real shelves + 22 single-item "baseline" recommendations); rendering them as a
// vertical stack of horizontal rows is an "endless seam". The composer groups them into a small set of typed groups,
// and the aggregate merges contributions across sources. The UI renders groups by kind with existing components.

/// <summary>How a home group is laid out: a featured Hero band, a compact 2-col QuickGrid (recents/jump-back-in), a
/// horizontally-paged Shelf (a real shelf), or a CollapsedGrid (many one-item recommendations folded into one grid).</summary>
public enum HomeGroupKind { Hero, QuickGrid, Shelf, CollapsedGrid }

/// <summary>What a home card points at — drives the nav route (pl: / album: / artist: / liked) and the card shape.</summary>
public enum HomeCardKind { Playlist, Album, Artist, Liked }

/// <summary>One home tile: a context URI + display metadata + its kind. Source-neutral (cover may be a remote CDN url).</summary>
public sealed record HomeCard(string Uri, string Title, string? Subtitle, Image? Image, HomeCardKind Kind);

/// <summary>A titled group of home cards laid out per <see cref="HomeGroupKind"/>.</summary>
public sealed record HomeGroup(HomeGroupKind Kind, string? Title, IReadOnlyList<HomeCard> Cards);

/// <summary>One source's contribution to the home feed (its groups), with a priority for ordering when merged across
/// sources by the aggregate (lower sorts first).</summary>
public sealed record HomeContribution(IReadOnlyList<HomeGroup> Groups, int Priority = 0);

/// <summary>The finished, merged home model the UI renders: a greeting + the ordered, condensed groups.</summary>
public sealed record HomeFeed(string Greeting, IReadOnlyList<HomeGroup> Groups)
{
    public static readonly HomeFeed Empty = new("", System.Array.Empty<HomeGroup>());
}
