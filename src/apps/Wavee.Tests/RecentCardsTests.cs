using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Recent-cards cover + accent mapping from Pathfinder home/recents entity shapes.
public class RecentCardsTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void RecentCards_EntityWithVisualIdentity_ReturnsCoverAndAccent()
    {
        var cards = SpotifyExportMapper.RecentCards(Root("""
        {
          "data": {
            "lists": [{
              "items": {
                "items": [{
                  "entity": {
                    "_uri": "spotify:album:41b0hsQwhVkMc3NQcvB0NF",
                    "data": {
                      "entityTypeTrait": { "type": "ENTITY_TYPE_ALBUM" },
                      "identityTrait": {
                        "name": "Easy",
                        "type": "Single",
                        "contributors": { "items": [ { "name": "SHAUN", "uri": "spotify:artist:1" } ] }
                      },
                      "uri": "spotify:album:41b0hsQwhVkMc3NQcvB0NF",
                      "visualIdentityTrait": {
                        "squareCoverImage": {
                          "image": {
                            "data": {
                              "sources": [
                                { "url": "https://image-cdn-ak.spotifycdn.com/image/ab67616d000075a0", "maxWidth": 640, "maxHeight": 640 }
                              ]
                            }
                          },
                          "extractedColorSet": {
                            "higherContrast": {
                              "backgroundTintedBase": { "red": 92, "green": 84, "blue": 84, "alpha": 255 }
                            }
                          }
                        }
                      }
                    }
                  }
                }]
              }
            }]
          }
        }
        """));

        Assert.Single(cards);
        Assert.NotNull(cards[0].Image?.Url);
        Assert.Contains("spotifycdn.com", cards[0].Image!.Url);
        Assert.Equal(0xFF5C5454u, cards[0].Accent);
    }

    [Fact]
    public void CardFromEntity_OriginalInstancesOnly_PicksScdnUrl()
    {
        var card = SpotifyExportMapper.CardFromEntity(Root("""
        {
          "__typename": "Album", "uri": "spotify:album:A", "name": "Test",
          "visualIdentityTrait": {
            "squareCoverImage": {
              "originalInstances": [
                { "flatFile": { "cdnUrl": "https://i.scdn.co/image/small" }, "size": "IMAGE_SIZE_SMALL" },
                { "flatFile": { "cdnUrl": "https://i.scdn.co/image/large" }, "size": "IMAGE_SIZE_LARGE" }
              ]
            }
          },
          "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "A" } } ] }
        }
        """));

        Assert.NotNull(card);
        Assert.Equal("https://i.scdn.co/image/large", card!.Image?.Url);
    }

    [Fact]
    public void MapArtist_ReleaseWithVisualIdentityTrait_HasCoverAndPalette()
    {
        var artist = SpotifyExportMapper.MapArtist(Root("""
        {
          "uri": "spotify:artist:x", "profile": { "name": "X" },
          "discography": {
            "albums": {
              "items": [{
                "releases": {
                  "items": [{
                    "uri": "spotify:album:y", "name": "Disc", "type": "ALBUM",
                    "date": { "year": 2024 }, "tracks": { "totalCount": 8 },
                    "visualIdentityTrait": {
                      "squareCoverImage": {
                        "originalInstances": [
                          { "flatFile": { "cdnUrl": "https://i.scdn.co/disc-cover" }, "size": "IMAGE_SIZE_LARGE" }
                        ],
                        "extractedColorSet": {
                          "higherContrast": {
                            "backgroundTintedBase": { "red": 20, "green": 13, "blue": 13, "alpha": 255 }
                          }
                        }
                      }
                    }
                  }]
                }
              }]
            }
          }
        }
        """));

        Assert.Single(artist.TopAlbums);
        var al = artist.TopAlbums[0];
        Assert.Equal("https://i.scdn.co/disc-cover", al.Cover?.Url);
        Assert.NotNull(al.Palette);
        Assert.Equal(0xFF140D0Du, al.Palette!.TintedDark);
    }

    [Fact]
    public void RecentCards_ExtractedColorSetFromRecentsShape()
    {
        // icedamericano.json recents entity: accent from visualIdentityTrait.squareCoverImage.extractedColorSet
        var cards = SpotifyExportMapper.RecentCards(Root("""
        {
          "data": {
            "lists": [{
              "items": {
                "items": [{
                  "entity": {
                    "data": {
                      "entityTypeTrait": { "type": "ENTITY_TYPE_TRACK" },
                      "identityTrait": {
                        "name": "Cold Brew Chapters",
                        "type": "Song",
                        "contributors": { "items": [ { "name": "roti.", "uri": "spotify:artist:1" } ] }
                      },
                      "uri": "spotify:track:7idegBIikag5rTZP4WZihP",
                      "visualIdentityTrait": {
                        "squareCoverImage": {
                          "image": {
                            "data": {
                              "sources": [ { "url": "https://image-cdn.example/cover", "maxWidth": 300, "maxHeight": 300 } ]
                            }
                          },
                          "extractedColorSet": {
                            "higherContrast": {
                              "backgroundTintedBase": { "red": 92, "green": 84, "blue": 84, "alpha": 255 }
                            }
                          }
                        }
                      }
                    }
                  }
                }]
              }
            }]
          }
        }
        """));

        Assert.Single(cards);
        Assert.Equal(0xFF5C5454u, cards[0].Accent);
    }
}
