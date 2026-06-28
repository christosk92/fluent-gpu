using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The cover-extracted page-accent provenance: the three detail mappers (album / artist / playlist) read Spotify's
// extracted colours off the cover node and set Palette (ARGB uints; the VIEW lifts Accent). Verifies both shapes
// (single colorDark hex + the rich extractedColorSet tiers), the dark-tier preference, and that a Spotify fallback /
// a colourless cover yields a null palette (⇒ the page keeps its neutral default — never a wrong colour).
public class PaletteExtractionTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // ── ALBUM (data.albumUnion.coverArt.extractedColors.colorDark) ───────────────────────────────────────
    [Fact]
    public void AlbumFromUnion_ReadsCoverColorDark()
    {
        var a = SpotifyExportMapper.AlbumFromUnion(Root("""
        { "data": { "albumUnion": {
            "uri": "spotify:album:A", "name": "Neon", "type": "ALBUM",
            "coverArt": {
              "sources": [ { "url": "https://cdn/a", "width": 640, "height": 640 } ],
              "extractedColors": { "colorDark": { "hex": "#3B82F6", "isFallback": false } }
            }
        } } }
        """));

        Assert.NotNull(a);
        Assert.NotNull(a!.Palette);
        Assert.Equal(0xFF3B82F6u, a.Palette!.Accent);
        Assert.Equal(0xFF3B82F6u, a.Palette.BackgroundDark);
        Assert.Equal(0xFF3B82F6u, a.Palette.TintedDark);
        Assert.Equal(0xFFFFFFFFu, a.Palette.Light);
    }

    [Fact]
    public void AlbumFromUnion_SkipsFallbackColorDark()
    {
        // isFallback = true means Spotify served its generic grey — drop it so the page keeps its neutral default.
        var a = SpotifyExportMapper.AlbumFromUnion(Root("""
        { "data": { "albumUnion": {
            "uri": "spotify:album:A", "name": "Neon",
            "coverArt": { "extractedColors": { "colorDark": { "hex": "#767676", "isFallback": true } } }
        } } }
        """));

        Assert.NotNull(a);
        Assert.Null(a!.Palette);
    }

    [Fact]
    public void AlbumFromUnion_NoCoverColors_NullPalette()
    {
        var a = SpotifyExportMapper.AlbumFromUnion(Root("""
        { "data": { "albumUnion": {
            "uri": "spotify:album:A", "name": "Neon",
            "coverArt": { "sources": [ { "url": "https://cdn/a", "width": 640, "height": 640 } ] }
        } } }
        """));

        Assert.NotNull(a);
        Assert.Null(a!.Palette);
    }

    // ── ARTIST (visualIdentity.wideFullBleedImage.extractedColorSet, dark-tier preference) ───────────────
    [Fact]
    public void ArtistFromOverview_ReadsColorSet_PrefersHigherContrast()
    {
        var ar = SpotifyExportMapper.ArtistFromOverview(Root("""
        { "data": { "artistUnion": {
            "uri": "spotify:artist:X", "profile": { "name": "Maroon 5" },
            "visualIdentity": { "wideFullBleedImage": { "extractedColorSet": {
              "highContrast":   { "backgroundBase": {"alpha":255,"red":1,"green":2,"blue":3},
                                  "backgroundTintedBase": {"alpha":255,"red":1,"green":2,"blue":3} },
              "higherContrast": { "backgroundBase": {"alpha":255,"red":40,"green":24,"blue":24},
                                  "backgroundTintedBase": {"alpha":255,"red":10,"green":20,"blue":30} }
            } } }
        } } }
        """));

        Assert.NotNull(ar);
        Assert.NotNull(ar!.Palette);
        Assert.Equal(0xFF0A141Eu, ar.Palette!.Accent);          // higher's tintedBase (10,20,30), NOT high's (1,2,3)
        Assert.Equal(0xFF281818u, ar.Palette.BackgroundDark);   // higher's backgroundBase (40,24,24)
    }

    [Fact]
    public void ArtistFromOverview_FallsBackToHighContrast()
    {
        // Only highContrast present ⇒ proves `higherContrast ?? highContrast`.
        var ar = SpotifyExportMapper.ArtistFromOverview(Root("""
        { "data": { "artistUnion": {
            "uri": "spotify:artist:X", "profile": { "name": "Maroon 5" },
            "visualIdentity": { "wideFullBleedImage": { "extractedColorSet": {
              "highContrast": { "backgroundBase": {"alpha":255,"red":4,"green":5,"blue":6},
                                "backgroundTintedBase": {"alpha":255,"red":1,"green":2,"blue":3} }
            } } }
        } } }
        """));

        Assert.NotNull(ar);
        Assert.NotNull(ar!.Palette);
        Assert.Equal(0xFF010203u, ar.Palette!.Accent);
        Assert.Equal(0xFF040506u, ar.Palette.BackgroundDark);
    }

    [Fact]
    public void ArtistFromOverview_NoColorSet_NullPalette()
    {
        var ar = SpotifyExportMapper.ArtistFromOverview(Root("""
        { "data": { "artistUnion": { "uri": "spotify:artist:X", "profile": { "name": "Maroon 5" } } } }
        """));

        Assert.NotNull(ar);
        Assert.Null(ar!.Palette);
    }

    // ── PLAYLIST (library colorDark + detail colorSet — MapPlaylistHeader tries both) ────────────────────
    [Fact]
    public void MapPlaylistHeader_LibraryColorDark()
    {
        var p = SpotifyExportMapper.MapPlaylistHeader(Root("""
        { "uri": "spotify:playlist:P", "name": "Daily Mix 1",
          "images": { "items": [ {
            "extractedColors": { "colorDark": { "hex": "#008585", "isFallback": false } },
            "sources": [ { "url": "https://cdn/p", "width": 300, "height": 300 } ]
          } ] } }
        """), 10);

        Assert.NotNull(p.Palette);
        Assert.Equal(0xFF008585u, p.Palette!.Accent);
    }

    [Fact]
    public void MapPlaylistHeader_DetailColorSet()
    {
        // The detail (playlistV2) node carries the rich extractedColorSet on its square cover; it wins over colorDark.
        var p = SpotifyExportMapper.MapPlaylistHeader(Root("""
        { "uri": "spotify:playlist:P", "name": "Iced Americano",
          "visualIdentity": { "squareCoverImage": { "extractedColorSet": {
            "highContrast":   { "backgroundBase": {"alpha":255,"red":1,"green":2,"blue":3},
                                "backgroundTintedBase": {"alpha":255,"red":1,"green":2,"blue":3} },
            "higherContrast": { "backgroundBase": {"alpha":255,"red":40,"green":24,"blue":24},
                                "backgroundTintedBase": {"alpha":255,"red":10,"green":20,"blue":30} }
          } } } }
        """), 10);

        Assert.NotNull(p.Palette);
        Assert.Equal(0xFF0A141Eu, p.Palette!.Accent);
        Assert.Equal(0xFF281818u, p.Palette.BackgroundDark);
    }

    [Fact]
    public void MapPlaylistHeader_NoColors_NullPalette()
    {
        var p = SpotifyExportMapper.MapPlaylistHeader(Root("""
        { "uri": "spotify:playlist:P", "name": "Plain",
          "images": { "items": [ { "sources": [ { "url": "https://cdn/p", "width": 300, "height": 300 } ] } ] } }
        """), 10);

        Assert.Null(p.Palette);
    }
}
