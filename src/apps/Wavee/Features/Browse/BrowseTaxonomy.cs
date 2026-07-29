using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using Wavee.Core;

namespace Wavee.Features.Browse;

/// <summary>Which band of the Browse directory a category sits in.</summary>
public enum BrowseGroup
{
    Top,
    ForYou,
    Genres,
    MoodActivity,
    Charts,
    More,
}

/// <summary>The Browse directory's grouping.
///
/// The wire has NO grouping: <c>browseAll</c> returns one flat section of ~70 categories. The Top / For you / Genres /
/// Mood &amp; activity / Charts / More bands are a product decision, so they live here as a curated map.
///
/// Two rules make this safe to maintain:
///  • Keyed by page URI, NEVER by title. Titles arrive already localised by the server ("Dutch music" on an nl
///    account), so a title-keyed map would silently fall apart per market.
///  • Anything unmapped lands in <see cref="BrowseGroup.More"/>. A category Spotify adds tomorrow shows up in the
///    directory immediately — it just is not curated into a band yet. Nothing disappears.
///
/// Group LABELS are localised (<c>Strings.Browse.*</c>); group MEMBERSHIP is not.</summary>
public static class BrowseTaxonomy
{
    // Uri -> group. Uris are stable Spotify page ids captured from browseAll (browe.saz).
    static readonly FrozenDictionary<string, BrowseGroup> Map = new Dictionary<string, BrowseGroup>(StringComparer.Ordinal)
    {
        // ── Top: the four entry points Spotify itself puts first ────────────────────────────────────────────────────
        ["spotify:page:0JQ5DAqbMKFSi39LMRT0Cy"] = BrowseGroup.Top,          // Music
        ["spotify:page:0JQ5DArNBzkmxXHCqFLx2J"] = BrowseGroup.Top,          // Podcasts
        ["spotify:page:0JQ5DAqbMKFETqK4t8f1n3"] = BrowseGroup.Top,          // Audiobooks
        ["spotify:concerts"] = BrowseGroup.Top,                              // Live Events (a client feature, not a page)

        // ── For you ─────────────────────────────────────────────────────────────────────────────────────────────────
        ["spotify:page:0JQ5DAtOnAEpjOgUKwXyxj"] = BrowseGroup.ForYou,       // Discover
        ["spotify:page:0JQ5DAqbMKFPw634sFwguI"] = BrowseGroup.ForYou,       // EQUAL
        ["spotify:page:0JQ5DAqbMKFImHYGo3eTSg"] = BrowseGroup.ForYou,       // Fresh Finds
        ["spotify:page:0JQ5DAqbMKFGnsSfvg90Wo"] = BrowseGroup.ForYou,       // GLOW
        ["spotify:page:0JQ5DAt0tbjZptfcdMSKl3"] = BrowseGroup.ForYou,       // Made For You
        ["spotify:page:0JQ5DAqbMKFz6FAsUtgAab"] = BrowseGroup.ForYou,       // New Releases
        ["spotify:page:0JQ5DAqbMKFOOxftoKZxod"] = BrowseGroup.ForYou,       // RADAR
        ["spotify:page:0JQ5DAqbMKFDBgllo2cUIN"] = BrowseGroup.ForYou,       // Spotify Singles
        ["spotify:page:0JQ5DAqbMKFRKBHIxJ5hMm"] = BrowseGroup.ForYou,       // Tastemakers
        ["spotify:page:0JQ5DAqbMKFQIL0AXnG5AK"] = BrowseGroup.ForYou,       // Trending

        // ── Genres ──────────────────────────────────────────────────────────────────────────────────────────────────
        ["spotify:page:0JQ5DAqbMKFNQ0fGp4byGU"] = BrowseGroup.Genres,       // Afro
        ["spotify:page:0JQ5DAqbMKFFtlLYUHv8bT"] = BrowseGroup.Genres,       // Alternative
        ["spotify:page:0JQ5DAqbMKFLjmiZRss79w"] = BrowseGroup.Genres,       // Ambient
        ["spotify:page:0JQ5DAqbMKFQ1UFISXj59F"] = BrowseGroup.Genres,       // Arab
        ["spotify:page:0JQ5DAqbMKFQiK2EHwyjcU"] = BrowseGroup.Genres,       // Blues
        ["spotify:page:0JQ5DAqbMKFObNLOHydSW8"] = BrowseGroup.Genres,       // Caribbean
        ["spotify:page:0JQ5DAqbMKFPrEiAOxgac3"] = BrowseGroup.Genres,       // Classical
        ["spotify:page:0JQ5DAqbMKFKLfwjuJMoNC"] = BrowseGroup.Genres,       // Country
        ["spotify:page:0JQ5DAqbMKFHOzuVTgTizF"] = BrowseGroup.Genres,       // Dance/Electronic
        ["spotify:page:0JQ5DAqbMKFCLroFGPFVr5"] = BrowseGroup.Genres,       // Dutch music
        ["spotify:page:0JQ5DAqbMKFy78wprEpAjl"] = BrowseGroup.Genres,       // Folk & Acoustic
        ["spotify:page:0JQ5DAqbMKFFsW9N8maB6z"] = BrowseGroup.Genres,       // Funk & Disco
        ["spotify:page:0JQ5DAqbMKFQ00XGBls6ym"] = BrowseGroup.Genres,       // Hip-Hop
        ["spotify:page:0JQ5DAqbMKFCWjUTdzaG0e"] = BrowseGroup.Genres,       // Indie
        ["spotify:page:0JQ5DAqbMKFAJ5xb0fwo9m"] = BrowseGroup.Genres,       // Jazz
        ["spotify:page:0JQ5DAqbMKFGvOw3O4nLAf"] = BrowseGroup.Genres,       // K-pop
        ["spotify:page:0JQ5DAqbMKFxXaXKP7zcDp"] = BrowseGroup.Genres,       // Latin
        ["spotify:page:0JQ5DAqbMKFDkd668ypn6O"] = BrowseGroup.Genres,       // Metal
        ["spotify:page:0JQ5DAqbMKFEC4WFtoNRpw"] = BrowseGroup.Genres,       // Pop
        ["spotify:page:0JQ5DAqbMKFAjfauKLOZiv"] = BrowseGroup.Genres,       // Punk
        ["spotify:page:0JQ5DAqbMKFEZPnFQSFB1T"] = BrowseGroup.Genres,       // R&B
        ["spotify:page:0JQ5DAqbMKFJKoGyUMo2hE"] = BrowseGroup.Genres,       // Reggae
        ["spotify:page:0JQ5DAqbMKFDXXwE9BDJAr"] = BrowseGroup.Genres,       // Rock
        ["spotify:page:0JQ5DAqbMKFIpEuaCnimBj"] = BrowseGroup.Genres,       // Soul
        ["spotify:page:0JQ5DAqbMKFSCjnQr8QZ3O"] = BrowseGroup.Genres,       // Songwriters

        // ── Mood & activity ─────────────────────────────────────────────────────────────────────────────────────────
        ["spotify:page:0JQ5DAqbMKFx0uLQR2okcc"] = BrowseGroup.MoodActivity, // At Home
        ["spotify:page:0JQ5DAqbMKFFzDl7qN9Apr"] = BrowseGroup.MoodActivity, // Chill
        ["spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0"] = BrowseGroup.MoodActivity, // Cooking & Dining
        ["spotify:page:0JQ5DAqbMKFJ6dHNHTv6Mx"] = BrowseGroup.MoodActivity, // Fitness
        ["spotify:page:0JQ5DAqbMKFCbimwdOYlsl"] = BrowseGroup.MoodActivity, // Focus
        ["spotify:page:0JQ5DAqbMKFIRybaNTYXXy"] = BrowseGroup.MoodActivity, // In the car
        ["spotify:page:0JQ5DAqbMKFAUsdyVjCQuL"] = BrowseGroup.MoodActivity, // Love
        ["spotify:page:0JQ5DAqbMKFzHmL4tf05da"] = BrowseGroup.MoodActivity, // Mood
        ["spotify:page:0JQ5DAqbMKFI3pNLtYMD9S"] = BrowseGroup.MoodActivity, // Nature & Noise
        ["spotify:page:0JQ5DAqbMKFA6SOHvT3gck"] = BrowseGroup.MoodActivity, // Party
        ["spotify:page:0JQ5DAqbMKFCuoRTxhYWow"] = BrowseGroup.MoodActivity, // Sleep
        ["spotify:page:0JQ5DAqbMKFAQy4HL4XU2D"] = BrowseGroup.MoodActivity, // Travel
        ["spotify:page:0JQ5DAqbMKFLb2EqgLtpjC"] = BrowseGroup.MoodActivity, // Wellness
        ["spotify:page:0JQ5DAqbMKFAXlCG6QvYQ4"] = BrowseGroup.MoodActivity, // Workout Music

        // ── Charts ──────────────────────────────────────────────────────────────────────────────────────────────────
        ["spotify:page:0JQ5DAudkNjCgYMM0TZXDw"] = BrowseGroup.Charts,       // Charts
        ["spotify:page:0JQ5DAB3zgCauRwnvdEQjJ"] = BrowseGroup.Charts,       // Podcast Charts
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The band a category belongs to. Unmapped → <see cref="BrowseGroup.More"/>, so a new Spotify category
    /// still appears in the directory rather than vanishing.</summary>
    public static BrowseGroup GroupOf(BrowseCategory c)
        => Map.TryGetValue(c.Uri, out var g) ? g : BrowseGroup.More;

    /// <summary>Group the flat category list into display bands, in fixed band order, alphabetised WITHIN each band —
    /// the ordering the design relies on so the eye can bisect a long list.
    ///
    /// Sorting uses the CURRENT CULTURE, because these titles are localised server-side and an ordinal sort would put
    /// accented names in the wrong place for the very markets that have them. Empty bands are omitted entirely.</summary>
    public static IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> Grouped(
        IReadOnlyList<BrowseCategory> categories)
    {
        if (categories.Count == 0)
            return Array.Empty<(BrowseGroup, IReadOnlyList<BrowseCategory>)>();

        var buckets = new Dictionary<BrowseGroup, List<BrowseCategory>>(6);
        foreach (var c in categories)
        {
            var g = GroupOf(c);
            if (!buckets.TryGetValue(g, out var list)) buckets[g] = list = new List<BrowseCategory>();
            list.Add(c);
        }

        var order = new[] { BrowseGroup.Top, BrowseGroup.ForYou, BrowseGroup.Genres,
                            BrowseGroup.MoodActivity, BrowseGroup.Charts, BrowseGroup.More };
        var result = new List<(BrowseGroup, IReadOnlyList<BrowseCategory>)>(order.Length);
        foreach (var g in order)
        {
            if (!buckets.TryGetValue(g, out var list) || list.Count == 0) continue;
            // Top keeps the SERVER's order — Music / Podcasts / Audiobooks / Live Events is a deliberate ranking,
            // and alphabetising it would read as arbitrary.
            if (g != BrowseGroup.Top)
                list.Sort(static (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            result.Add((g, list));
        }
        return result;
    }
}
