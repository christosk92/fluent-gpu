using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class SearchSuggestionMapperTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SuggestionsFromV2_SplitsAutocompleteQueriesAndRichHits()
    {
        var suggestions = SpotifyExportMapper.SuggestionsFromV2(Root("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "item": { "__typename": "SearchAutoCompleteEntity",
            "data": { "text": "david guetta", "uri": "spotify:search:david+guetta" } } },
          { "item": { "__typename": "TrackResponseWrapper", "data": {
            "__typename": "Track",
            "uri": "spotify:track:0TDLuuLlV54CkRRUOahJb4",
            "name": "Titanium (feat. Sia)",
            "contentRating": { "label": "NONE" },
            "albumOfTrack": { "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/cover", "width": 300, "height": 300 }
            ] } },
            "artists": { "items": [
              { "profile": { "name": "David Guetta" }, "uri": "spotify:artist:1Cs0zKBU1kc0i8ypK3B9ai" },
              { "profile": { "name": "Sia" }, "uri": "spotify:artist:5WUlDfRSoLAfcVSX1WnrxN" }
            ] }
          } } },
          { "item": { "__typename": "ArtistResponseWrapper", "data": {
            "__typename": "Artist",
            "uri": "spotify:artist:1Cs0zKBU1kc0i8ypK3B9ai",
            "profile": { "name": "David Guetta" },
            "visuals": { "avatarImage": { "sources": [
              { "url": "https://i.scdn.co/image/avatar", "width": 640, "height": 640 }
            ] } }
          } } }
        ] } } } }
        """));

        Assert.Equal("david guetta", Assert.Single(suggestions.Queries));
        Assert.Equal(2, suggestions.Items.Count);
        Assert.Equal(SearchSuggestionKind.Track, suggestions.Items[0].Kind);
        Assert.Equal("Titanium (feat. Sia)", suggestions.Items[0].Title);
        Assert.Contains("David Guetta", suggestions.Items[0].Subtitle);
        Assert.Equal("https://i.scdn.co/image/cover", suggestions.Items[0].Image!.Url);
        Assert.Equal(SearchSuggestionKind.Artist, suggestions.Items[1].Kind);
        Assert.Equal("David Guetta", suggestions.Items[1].Title);
        Assert.Equal("https://i.scdn.co/image/avatar", suggestions.Items[1].Image!.Url);
    }
}
