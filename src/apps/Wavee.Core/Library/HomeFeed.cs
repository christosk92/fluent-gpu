namespace Wavee.Core;

// The source-agnostic, CONDENSED home model (see docs/architecture.md §2). A real Spotify home feed is dozens of
// sections (the export has 31 — 9 real shelves + 22 single-item "baseline" recommendations); rendering them as a
// vertical stack of horizontal rows is an "endless seam". The composer groups them into a small set of typed groups,
// and the aggregate merges contributions across sources. The UI renders groups by kind with existing components.

/// <summary>How a home group is laid out: a featured Hero band, a compact QuickGrid, a finite horizontally paged
/// Shelf, a compatibility CollapsedGrid, or a Featured editorial break. Home alternates shelves with editorial breaks
/// so the vertical feed has rhythm instead of repeating one module forever.</summary>
public enum HomeGroupKind { Hero, QuickGrid, Shelf, Compact, CollapsedGrid, Featured }

/// <summary>What a home card points at — drives the nav route (pl: / album: / artist: / liked) and the card shape.</summary>
public enum HomeCardKind { Playlist, Album, Artist, Track, Liked }

/// <summary>One home tile: a context URI + display metadata + its kind. Source-neutral (cover may be a remote CDN url).
/// <paramref name="MosaicTiles"/> (when <paramref name="Image"/> is null) carries up to 4 album-cover URLs for a 2×2
/// cover-less-playlist mosaic.</summary>
public sealed record HomeCard(string Uri, string Title, string? Subtitle, Image? Image, HomeCardKind Kind,
    System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null,
    // Optional eyebrow — the section context shown ABOVE the title on a Featured card (e.g. "For fans of IU",
    // "More like GFRIEND"). Carried from the baseline section's title, which the old composer discarded.
    string? Eyebrow = null);

/// <summary>A titled group of home cards laid out per <see cref="HomeGroupKind"/>. The section tint is resolved by the
/// VIEW from the first card's cover (CoverColorPlane), so no colour rides the feed.</summary>
public sealed record HomeGroup(HomeGroupKind Kind, string? Title, IReadOnlyList<HomeCard> Cards);

/// <summary>One preview track of a home recommendation (the hover peek on a Featured editorial card): display name,
/// cover art, and an optional 30s MP3 preview URL. Source-neutral — Spotify fills it from feedBaselineLookup.</summary>
public sealed record HomePreviewTrack(string Uri, string Name, Image? Cover, string? PreviewUrl = null);

/// <summary>A home facet chip (Spotify <c>home.homeChips[]</c>). <paramref name="Id"/> is what goes back into the
/// <c>facet</c> request variable — it is an opaque server token ("music-chip"), never localised or synthesised.
/// <paramref name="Label"/> arrives already localised by the server. <paramref name="SubChips"/> is the second level
/// ("Following"), which is what lets a selected chip fuse into a two-segment pill.</summary>
public sealed record HomeChip(string Id, string Label, IReadOnlyList<HomeChip> SubChips);

/// <summary>What a live (server) home fetch returns to a catalog source: the editorial groups plus the facet chip row.
/// Chips are null when the server sent none — the source then contributes groups only.</summary>
public sealed record LiveHomeResult(IReadOnlyList<HomeGroup> Groups, IReadOnlyList<HomeChip>? Chips)
{
    public static readonly LiveHomeResult Empty = new(System.Array.Empty<HomeGroup>(), null);
}

/// <summary>One source's contribution to the home feed (its groups), with a priority for ordering when merged across
/// sources by the aggregate (lower sorts first). <paramref name="Chips"/> is the source's facet row, if it has one.</summary>
public sealed record HomeContribution(IReadOnlyList<HomeGroup> Groups, int Priority = 0,
    IReadOnlyList<HomeChip>? Chips = null);

/// <summary>The finished, merged home model the UI renders: a greeting, the ordered condensed groups, and the facet
/// chip row. <paramref name="Chips"/> is empty for sources that have no facets (local library, fakes) — the chip row
/// is then simply not rendered.</summary>
public sealed record HomeFeed(string Greeting, IReadOnlyList<HomeGroup> Groups,
    IReadOnlyList<HomeChip>? Chips = null)
{
    public static readonly HomeFeed Empty = new("", System.Array.Empty<HomeGroup>());
}
