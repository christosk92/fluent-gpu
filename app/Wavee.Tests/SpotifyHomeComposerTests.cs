using System.Linq;
using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Mixed-cadence home composer: generic sections remain titled shelves, baseline recommendations become bounded
// three-card editorial breaks, and Spotlight remains a Hero.
public class SpotifyHomeComposerTests
{
    const string Home = """
    { "sectionContainer": { "sections": { "items": [
        { "data": { "__typename": "HomeSpotlightSectionData", "title": { "transformedLabel": "Spotlight" } },
          "sectionItems": { "items": [ { "content": { "data": { "__typename": "Album", "uri": "spotify:album:S", "name": "Spot" } } } ] } },

        { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Your top mixes" } },
          "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Album", "uri": "spotify:album:A", "name": "A1" } } },
            { "content": { "data": { "__typename": "Album", "uri": "spotify:album:B", "name": "A2" } } } ] } },

        { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Recommended Stations" } },
          "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Album", "uri": "spotify:album:C", "name": "C1" } } },
            { "content": { "data": { "__typename": "Album", "uri": "spotify:album:D", "name": "C2" } } } ] } },

        { "data": { "__typename": "HomeFeedBaselineSectionData", "title": { "transformedLabel": "For fans of IU" } },
          "sectionItems": { "items": [ { "content": { "data": { "__typename": "Artist", "uri": "spotify:artist:X", "profile": { "name": "IU" } } } } ] } },

        { "data": { "__typename": "HomeFeedBaselineSectionData", "title": { "transformedLabel": "More like GFRIEND" } },
          "sectionItems": { "items": [ { "content": { "data": { "__typename": "Artist", "uri": "spotify:artist:Y", "profile": { "name": "GFRIEND" } } } } ] } }
    ] } } }
    """;

    static HomeContribution Compose() =>
        SpotifyHomeComposer.Compose(JsonDocument.Parse(Home).RootElement, System.Array.Empty<PlaylistSummary>());

    [Fact]
    public void GenericSections_BecomeOwnTitledShelves()
    {
        var shelves = Compose().Groups.Where(g => g.Kind == HomeGroupKind.Shelf).ToList();
        Assert.Equal(2, shelves.Count);
        Assert.Equal("Your top mixes", shelves[0].Title);
        Assert.Equal("Recommended Stations", shelves[1].Title);
        Assert.Equal(2, shelves[0].Cards.Count);
    }

    [Fact]
    public void FeaturedBreak_InterruptsBeforeConventionalShelves()
    {
        var groups = Compose().Groups;
        Assert.Equal(HomeGroupKind.Hero, groups[0].Kind);
        Assert.Equal(HomeGroupKind.Featured, groups[1].Kind);
        Assert.Equal(HomeGroupKind.Shelf, groups[2].Kind);
        Assert.Equal(HomeGroupKind.Shelf, groups[3].Kind);
    }

    [Fact]
    public void BaselineSections_FoldIntoOneFeaturedRow_WithSectionTitlesAsEyebrows()
    {
        var featured = Assert.Single(Compose().Groups, g => g.Kind == HomeGroupKind.Featured);
        Assert.Equal("Made for you", featured.Title);
        Assert.Equal(2, featured.Cards.Count);
        Assert.Equal("For fans of IU", featured.Cards[0].Eyebrow);
        Assert.Equal("More like GFRIEND", featured.Cards[1].Eyebrow);
    }

    [Fact]
    public void Spotlight_BecomesHero()
    {
        var hero = Assert.Single(Compose().Groups, g => g.Kind == HomeGroupKind.Hero);
        Assert.Equal("Spotlight", hero.Title);
        Assert.Equal("spotify:album:S", Assert.Single(hero.Cards).Uri);
    }

    [Fact]
    public void MadeForYouTitle_IsCallerSupplied()
    {
        var groups = SpotifyHomeComposer.Compose(JsonDocument.Parse(Home).RootElement, System.Array.Empty<PlaylistSummary>(), "Voor jou").Groups;
        Assert.Equal("Voor jou", Assert.Single(groups, g => g.Kind == HomeGroupKind.Featured).Title);
    }

    // The desktop home response embeds recently-played inline as HomeRecentlyPlayedSectionData → a `List` of recent
    // entities (content.data.__typename == "List"), so the composer builds the recents shelf without a separate query.
    const string HomeWithRecents = """
    { "sectionContainer": { "sections": { "items": [
        { "data": { "__typename": "HomeRecentlyPlayedSectionData", "title": { "transformedLabel": "Recents" } },
          "sectionItems": { "items": [ { "content": { "data": { "__typename": "List", "items": { "items": [
            { "entity": { "data": {
                "entityTypeTrait": { "type": "ENTITY_TYPE_PLAYLIST" },
                "identityTrait": { "name": "Daily Mix 3", "contributors": { "items": [ { "name": "Spotify", "uri": "spotify:user:spotify" } ] } },
                "uri": "spotify:playlist:P1" } } },
            { "entity": { "data": {
                "entityTypeTrait": { "type": "ENTITY_TYPE_ARTIST" },
                "identityTrait": { "name": "GFRIEND" },
                "uri": "spotify:artist:A1" } } }
          ] } } } } ] } },

        { "data": { "__typename": "HomeGenericSectionData", "title": { "transformedLabel": "Your top mixes" } },
          "sectionItems": { "items": [
            { "content": { "data": { "__typename": "Album", "uri": "spotify:album:A", "name": "A1" } } },
            { "content": { "data": { "__typename": "Album", "uri": "spotify:album:B", "name": "A2" } } } ] } }
    ] } } }
    """;

    [Fact]
    public void RecentlyPlayed_RendersInlineFromHomeResponse_AsLeadingShelf()
    {
        var groups = SpotifyHomeComposer.Compose(JsonDocument.Parse(HomeWithRecents).RootElement,
            System.Array.Empty<PlaylistSummary>(), recentlyPlayedTitle: "Onlangs afgespeeld").Groups;

        var recents = groups[0];
        Assert.Equal(HomeGroupKind.Shelf, recents.Kind);
        Assert.Equal("Onlangs afgespeeld", recents.Title);
        Assert.Equal(2, recents.Cards.Count);
        Assert.Equal("spotify:playlist:P1", recents.Cards[0].Uri);
        Assert.Equal(HomeCardKind.Playlist, recents.Cards[0].Kind);
        Assert.Equal("spotify:artist:A1", recents.Cards[1].Uri);
        Assert.Equal(HomeCardKind.Artist, recents.Cards[1].Kind);
    }
}
