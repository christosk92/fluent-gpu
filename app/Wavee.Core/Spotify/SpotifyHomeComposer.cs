using System.Linq;
using System.Text.Json;

namespace Wavee.Core;

/// <summary>Condenses a raw Spotify home feed (the export has 31 sections — 9 real shelves + 22 single-item
/// "baseline" recommendations) into a small, finite set of typed groups so the UI isn't an endless vertical stack of
/// horizontal rows (docs/architecture.md §2). Rules: a QuickGrid of the user's playlists at the top; the Spotlight as a
/// Hero; the substantial multi-item shelves (capped) as carousels; and ALL the single-item baseline sections folded
/// into ONE "Made for you" grid.</summary>
internal static class SpotifyHomeComposer
{
    const int MaxShelves = 6;
    const int CardsPerShelf = 12;
    const int QuickPicks = 8;

    public static HomeContribution Compose(JsonElement homeRoot, IReadOnlyList<PlaylistSummary> library)
    {
        HomeGroup? quick = null, hero = null;
        var shelves = new List<HomeGroup>();
        var collapsed = new List<HomeCard>();

        if (library.Count > 0)
        {
            var picks = library.Take(QuickPicks)
                .Select(p => new HomeCard(p.Uri, p.Name, p.OwnerName, p.Cover, HomeCardKind.Playlist)).ToList();
            quick = new HomeGroup(HomeGroupKind.QuickGrid, null, picks);
        }

        var sections = SpotifyExportMapper.Dig(homeRoot, "sectionContainer", "sections", "items");
        if (sections.ValueKind == JsonValueKind.Array)
            foreach (var sec in sections.EnumerateArray())
            {
                var d = SpotifyExportMapper.Dig(sec, "data");
                var tn = Str(d, "__typename");
                var title = Str(d, "title", "transformedLabel") ?? Str(d, "title", "text");
                var items = SpotifyExportMapper.Dig(sec, "sectionItems", "items");
                switch (tn)
                {
                    case "HomeSpotlightSectionData":
                        if (hero is null && FirstCard(items) is { } hc)
                            hero = new HomeGroup(HomeGroupKind.Hero, title, new[] { hc });
                        break;
                    case "HomeFeedBaselineSectionData":
                        if (FirstCard(items) is { } bc) collapsed.Add(bc);
                        break;
                    case "HomeGenericSectionData":
                        if (shelves.Count < MaxShelves)
                        {
                            var cards = Cards(items, CardsPerShelf);
                            if (cards.Count >= 2) shelves.Add(new HomeGroup(HomeGroupKind.Shelf, title, cards));
                        }
                        break;
                    // HomeShortsSectionData / HomeRecentlyPlayedSectionData: skipped (not worth a card / uncertain shape).
                }
            }

        var groups = new List<HomeGroup>();
        if (quick is not null) groups.Add(quick);
        if (hero is not null) groups.Add(hero);
        groups.AddRange(shelves);
        if (collapsed.Count > 0) groups.Add(new HomeGroup(HomeGroupKind.CollapsedGrid, "Made for you", collapsed));
        return new HomeContribution(groups, Priority: 0);
    }

    static string? Str(JsonElement e, params string[] path)
    {
        var x = SpotifyExportMapper.Dig(e, path);
        return x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    }

    static HomeCard? FirstCard(JsonElement items) =>
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            ? SpotifyExportMapper.CardFromEntity(SpotifyExportMapper.Dig(items[0], "content", "data"))
            : null;

    static List<HomeCard> Cards(JsonElement items, int max)
    {
        var list = new List<HomeCard>();
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                if (list.Count >= max) break;
                if (SpotifyExportMapper.CardFromEntity(SpotifyExportMapper.Dig(it, "content", "data")) is { } c) list.Add(c);
            }
        return list;
    }
}
