using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The home section-accent provenance: hex parsing + the cover-extracted accent the composer turns into a section tint.
public class HomeAccentTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("#3B82F6", 0xFF3B82F6u)]
    [InlineData("3b82f6", 0xFF3B82F6u)]
    [InlineData("#000000", 0xFF000000u)]
    public void HexToArgb_ParsesHex(string hex, uint expected) => Assert.Equal(expected, SpotifyExportMapper.HexToArgb(hex));

    [Theory]
    [InlineData("")]
    [InlineData("#xyzxyz")]
    [InlineData("#12345")]    // wrong length
    [InlineData("not a color")]
    public void HexToArgb_RejectsMalformed(string hex) => Assert.Null(SpotifyExportMapper.HexToArgb(hex));

    [Fact]
    public void CardFromEntity_ExtractsColorSetAccent()
    {
        var card = SpotifyExportMapper.CardFromEntity(Root("""
        { "__typename": "Album", "uri": "spotify:album:A", "name": "Neon",
          "visualIdentity": { "squareCoverImage": {
            "extractedColorSet": { "higherContrast": {
              "backgroundTintedBase": { "red": 59, "green": 130, "blue": 246, "alpha": 255 } } } } },
          "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "Aurora" } } ] } }
        """));

        Assert.NotNull(card);
        Assert.Equal(0xFF3B82F6u, card!.Accent);
    }

    [Fact]
    public void CardFromEntity_ExtractsCoverAccent()
    {
        var card = SpotifyExportMapper.CardFromEntity(Root("""
        { "__typename": "Album", "uri": "spotify:album:A", "name": "Neon",
          "coverArt": { "sources": [ { "url": "https://cdn/a", "width": 300, "height": 300 } ],
                        "extractedColors": { "colorDark": { "hex": "#3B82F6", "isFallback": false } } },
          "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "Aurora" } } ] } }
        """));

        Assert.NotNull(card);
        Assert.Equal(0xFF3B82F6u, card!.Accent);
    }

    [Fact]
    public void CardFromEntity_SkipsSpotifyFallbackAccent()
    {
        // isFallback = true means Spotify served its generic grey — drop it so the composer uses its semantic kind tint.
        var card = SpotifyExportMapper.CardFromEntity(Root("""
        { "__typename": "Album", "uri": "spotify:album:A", "name": "Neon",
          "coverArt": { "extractedColors": { "colorDark": { "hex": "#767676", "isFallback": true } } } }
        """));

        Assert.NotNull(card);
        Assert.Null(card!.Accent);
    }
}
