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

    [Fact]
    public void TopHitsFromV2_PreservesWrapperOrderAndMatchedFields()
    {
        var hits = SpotifyExportMapper.TopHitsFromV2(Root("""
        { "data": { "searchV2": { "topResultsV2": { "itemsV2": [
          { "item": { "__typename": "TrackResponseWrapper", "data": {
            "__typename": "TrackResponseWrapper",
            "uri": "spotify:track:top",
            "name": "I don't want to hurt you",
            "trackMediaType": "AUDIO",
            "albumOfTrack": { "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/top", "width": 300, "height": 300 }
            ] } },
            "artists": { "items": [
              { "profile": { "name": "Natsu" }, "uri": "spotify:artist:top" }
            ] }
          } } },
          { "matchedFields": [ "LYRICS" ], "item": { "__typename": "TrackResponseWrapper", "data": {
            "uri": "spotify:track:lyrics",
            "name": "BIRDS OF A FEATHER",
            "trackMediaType": "VIDEO",
            "albumOfTrack": { "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/lyrics", "width": 300, "height": 300 }
            ] } },
            "artists": { "items": [
              { "profile": { "name": "Billie Eilish" }, "uri": "spotify:artist:billie" }
            ] }
          } } },
          { "item": { "__typename": "PodcastResponseWrapper", "data": {
            "uri": "spotify:show:pod",
            "name": "Strength and Sthenics Podcast",
            "publisher": { "name": "Denis & Sasa" },
            "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/pod", "width": 300, "height": 300 }
            ] }
          } } },
          { "item": { "__typename": "AudiobookResponseWrapper", "data": {
            "uri": "spotify:audiobook:book",
            "name": "Summary of Goodbye, Things",
            "accessInfo": { "signifier": { "text": "Included in Premium" } },
            "authorsV2": { "items": [ { "name": "Abbey Beathan" } ] },
            "audiobookDuration": { "totalMilliseconds": 3840000 },
            "publishDate": { "isoString": "2020-01-13T00:00:00Z", "precision": "MINUTE" },
            "description": "Author(s): Abbey Beathan\nNarrator(s): Peter Prova\n\nGoodbye, Things summary.",
            "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/book", "width": 300, "height": 300 }
            ] }
          } } },
          { "matchedFields": [ "LYRICS" ], "item": {
            "__typename": "Playlist",
            "uri": "spotify:playlist:direct",
            "name": "Direct Playlist",
            "ownerV2": { "data": { "name": "Spotify" } },
            "images": { "items": [ { "sources": [
              { "url": "https://i.scdn.co/image/pl", "width": 300, "height": 300 }
            ] } ] }
          } }
        ] } } } }
        """));

        Assert.Equal(5, hits.Count);
        Assert.Equal(SearchHitKind.Track, hits[0].Kind);
        Assert.Equal("I don't want to hurt you", hits[0].Name);
        Assert.False(hits[0].MatchedLyrics);
        Assert.Equal(SearchHitKind.Track, hits[1].Kind);
        Assert.True(hits[1].MatchedLyrics);
        Assert.Equal("Music video", hits[1].TypeLabel);
        Assert.Equal(SearchHitKind.Podcast, hits[2].Kind);
        Assert.Equal(SearchHitKind.Audiobook, hits[3].Kind);
        Assert.Equal("Included in Premium", hits[3].AccessLabel);
        Assert.Equal("Jan 13, 2020 • 1 hr 4 min", hits[3].Meta);
        Assert.Contains("Goodbye, Things summary.", hits[3].Detail);
        Assert.Equal(SearchHitKind.Playlist, hits[4].Kind);
        Assert.Equal("Playlist • Spotify", hits[4].Subtitle);
        Assert.True(hits[4].MatchedLyrics);
    }
}
