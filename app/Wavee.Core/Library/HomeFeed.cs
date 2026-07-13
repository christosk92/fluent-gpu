namespace Wavee.Core;

// The source-agnostic, CONDENSED home model (see docs/architecture.md §2). A real Spotify home feed is dozens of
// sections (the export has 31 — 9 real shelves + 22 single-item "baseline" recommendations); rendering them as a
// vertical stack of horizontal rows is an "endless seam". The composer groups them into a small set of typed groups,
// and the aggregate merges contributions across sources. The UI renders groups by kind with existing components.

/// <summary>How a home group is laid out: a featured Hero band, a compact QuickGrid, a finite horizontally paged
/// Shelf, a compatibility CollapsedGrid, or a Featured editorial break. Home alternates shelves with editorial breaks
/// so the vertical feed has rhythm instead of repeating one module forever.</summary>
public enum HomeGroupKind { Hero, QuickGrid, Shelf, CollapsedGrid, Featured }

/// <summary>What a home card points at — drives the nav route (pl: / album: / artist: / liked) and the card shape.</summary>
public enum HomeCardKind { Playlist, Album, Artist, Track, Liked }

/// <summary>One home tile: a context URI + display metadata + its kind. Source-neutral (cover may be a remote CDN url).
/// <paramref name="MosaicTiles"/> (when <paramref name="Image"/> is null) carries up to 4 album-cover URLs for a 2×2
/// cover-less-playlist mosaic.</summary>
public sealed record HomeCard(string Uri, string Title, string? Subtitle, Image? Image, HomeCardKind Kind,
    System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null,
    // The cover's extracted dominant color (ARGB; null = none). Drives the section accent bar / tinted band. uint keeps
    // Core framework-neutral (mapped to the renderer's ColorF at the UI boundary, like Palette).
    uint? Accent = null,
    // Optional eyebrow — the section context shown ABOVE the title on a Featured card (e.g. "For fans of IU",
    // "More like GFRIEND"). Carried from the baseline section's title, which the old composer discarded.
    string? Eyebrow = null);

/// <summary>A titled group of home cards laid out per <see cref="HomeGroupKind"/>. <paramref name="Accent"/> (ARGB; null
/// = none) is the group's section tint — the first card's extracted color, else a semantic per-kind fallback.</summary>
public sealed record HomeGroup(HomeGroupKind Kind, string? Title, IReadOnlyList<HomeCard> Cards, uint? Accent = null);

/// <summary>One preview track of a home recommendation (the hover peek on a Featured editorial card): display name,
/// cover art, and an optional 30s MP3 preview URL. Source-neutral — Spotify fills it from feedBaselineLookup.</summary>
public sealed record HomePreviewTrack(string Uri, string Name, Image? Cover, string? PreviewUrl = null);

/// <summary>One source's contribution to the home feed (its groups), with a priority for ordering when merged across
/// sources by the aggregate (lower sorts first).</summary>
public sealed record HomeContribution(IReadOnlyList<HomeGroup> Groups, int Priority = 0);

/// <summary>The finished, merged home model the UI renders: a greeting + the ordered, condensed groups.</summary>
public sealed record HomeFeed(string Greeting, IReadOnlyList<HomeGroup> Groups)
{
    public static readonly HomeFeed Empty = new("", System.Array.Empty<HomeGroup>());
}
