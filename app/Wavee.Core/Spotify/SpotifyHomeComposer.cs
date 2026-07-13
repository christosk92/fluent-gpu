using System.Linq;
using System.Text.Json;

namespace Wavee.Core;

/// <summary>Projects Spotify's raw home into a mixed editorial cadence: substantial sections remain finite horizontal
/// shelves, Spotlight remains a hero, and baseline recommendations become editorial breaks interleaved between
/// shelves. This avoids both one enormous grid and an unbroken stack of identical carousels.</summary>
public static class SpotifyHomeComposer
{
    const int MaxSections = 12;
    const int CardsPerSection = 24;
    // A real home carries ~20 single-item HomeFeedBaselineSectionData recs. The editorial shelf is width-adaptive
    // (up to 5 columns), so 5 per break fills a wide row exactly; ALL breaks are placed (no cap) — the interleave
    // below spreads them evenly across the full shelf list instead of clustering them at the top.
    const int FeaturedCardsPerBreak = 5;
    const int QuickPicks = 8;
    const int RecentsShown = 12;

    // madeForYouTitle: the localized "Made for you" label supplied by the caller (the app layer has the loc system;
    // Core stays framework-neutral). Defaults to English for the offline/export path. recentlyPlayedTitle labels the
    // recents shelf built from the home response's embedded HomeRecentlyPlayedSectionData list.
    public static HomeContribution Compose(JsonElement homeRoot, IReadOnlyList<PlaylistSummary> library,
        string madeForYouTitle = "Made for you", string moreForYouTitle = "More for you",
        string recentlyPlayedTitle = "Recently played")
    {
        HomeGroup? quick = null, hero = null, recents = null;
        var shelves = new List<HomeGroup>();
        var featured = new List<HomeCard>();

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
                            hero = new HomeGroup(HomeGroupKind.Hero, title, new[] { hc }, GroupAccent(HomeGroupKind.Hero, new[] { hc }));
                        break;
                    case "HomeFeedBaselineSectionData":
                        // Single-item personalized rec → one Featured card; the section title becomes the card eyebrow.
                        if (FirstCard(items) is { } bc)
                            featured.Add(title is { Length: > 0 } ? bc with { Eyebrow = title } : bc);
                        break;
                    case "HomeGenericSectionData":
                        if (shelves.Count < MaxSections)
                        {
                            var cards = Cards(items, CardsPerSection);
                            if (cards.Count >= 2) shelves.Add(new HomeGroup(HomeGroupKind.Shelf, title, cards, GroupAccent(HomeGroupKind.Shelf, cards)));
                        }
                        break;
                    case "HomeRecentlyPlayedSectionData":
                        // The desktop home embeds recents inline: one sectionItem whose content.data is a `List` of
                        // recent entities. Render it directly — no separate recents API call.
                        if (recents is null && FirstContentData(items) is { ValueKind: JsonValueKind.Object } listData)
                        {
                            var rc = SpotifyExportMapper.RecentCardsFromListData(listData, RecentsShown);
                            if (rc.Count > 0)
                                recents = new HomeGroup(HomeGroupKind.Shelf, recentlyPlayedTitle, rc, GroupAccent(HomeGroupKind.QuickGrid, rc));
                        }
                        break;
                    // HomeShortsSectionData: skipped (short-form module we don't render).
                }
            }

        var groups = new List<HomeGroup>();
        if (quick is not null) groups.Add(quick);
        if (recents is not null) groups.Add(recents);   // recently played leads (was a separate query; now inline)
        if (hero is not null) groups.Add(hero);
        int featureAt = 0, featureBreak = 0;
        void AddFeatureBreak()
        {
            if (featureAt >= featured.Count) return;
            int take = Math.Min(FeaturedCardsPerBreak, featured.Count - featureAt);
            var cards = featured.GetRange(featureAt, take);
            groups.Add(new HomeGroup(HomeGroupKind.Featured,
                featureBreak == 0 ? madeForYouTitle : moreForYouTitle,
                cards, GroupAccent(HomeGroupKind.Featured, cards)));
            featureAt += take;
            featureBreak++;
        }

        // Apple-style rhythm: open with personalization, then SPREAD the remaining breaks evenly across the whole
        // shelf list (interval = shelves ÷ remaining breaks) so the editorial cadence covers the entire home instead
        // of front-loading — the old "every 2 shelves, max 3 breaks" clustered them at the top and dropped the rest.
        AddFeatureBreak();
        int breaksLeft = (featured.Count - featureAt + FeaturedCardsPerBreak - 1) / FeaturedCardsPerBreak;
        int interval = breaksLeft > 0 ? Math.Max(1, (int)Math.Ceiling(shelves.Count / (double)(breaksLeft + 1))) : int.MaxValue;
        for (int i = 0; i < shelves.Count; i++)
        {
            groups.Add(shelves[i]);
            if ((i + 1) % interval == 0) AddFeatureBreak();
        }
        while (featureAt < featured.Count) AddFeatureBreak();   // drain any leftover recs at the tail
        return new HomeContribution(groups, Priority: 0);
    }

    // The group's section tint: the first card carrying an extracted cover color, else a semantic per-kind fallback
    // (amber recents / blue made-for-you / the app accent for generic shelves) — ported from WaveeMusic's HomeRegion kinds.
    public static uint GroupAccent(HomeGroupKind kind, IReadOnlyList<HomeCard> cards)
    {
        foreach (var c in cards) if (c.Accent is { } a) return a;
        return kind switch
        {
            HomeGroupKind.QuickGrid => 0xFFF59E0Bu,                              // amber — your recents
            HomeGroupKind.CollapsedGrid or HomeGroupKind.Featured => 0xFF3B82F6u, // blue — made for you
            _ => 0xFF60CDFFu,                                                    // the app accent — generic sections / hero
        };
    }

    static string? Str(JsonElement e, params string[] path)
    {
        var x = SpotifyExportMapper.Dig(e, path);
        return x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    }

    // The content.data of the first section item (the recently-played `List` for HomeRecentlyPlayedSectionData).
    static JsonElement FirstContentData(JsonElement items) =>
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            ? SpotifyExportMapper.Dig(items[0], "content", "data")
            : default;

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
