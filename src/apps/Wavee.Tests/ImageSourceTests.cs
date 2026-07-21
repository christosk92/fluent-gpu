using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class ImageSourceTests
{
    [Fact]
    public void Image_NormalizesSpotifyImageToken_OnConstruction()
    {
        var image = new Image(" spotify:image:ab67616d00001e02870c1c64b1d77eb4456e4283 ", 300, 300);

        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02870c1c64b1d77eb4456e4283", image.Url);
        Assert.Equal(ImageSourceQuality.Usable, ImageSource.Quality(image));
    }

    [Fact]
    public void Image_NormalizesSpotifyImageToken_OnWithUpdate()
    {
        var image = new Image("https://i.scdn.co/image/old") with { Url = "spotify:image:new" };

        Assert.Equal("https://i.scdn.co/image/new", image.Url);
    }

    [Fact]
    public void Image_NormalizesMosaicTiles()
    {
        var image = new Image("", MosaicTiles: new List<string>
        {
            "spotify:image:first",
            " https://i.scdn.co/image/second ",
        });

        Assert.NotNull(image.MosaicTiles);
        Assert.Equal("https://i.scdn.co/image/first", image.MosaicTiles![0]);
        Assert.Equal("https://i.scdn.co/image/second", image.MosaicTiles[1]);
        Assert.Equal(ImageSourceQuality.Usable, ImageSource.Quality(image));
    }

    [Fact]
    public void ImageSource_Quality_ClassifiesRawSources()
    {
        Assert.Equal(ImageSourceQuality.None, ImageSource.Quality(""));
        Assert.Equal(ImageSourceQuality.None, ImageSource.Quality("   "));
        Assert.Equal(ImageSourceQuality.Unresolved, ImageSource.Quality("spotify:image:abc"));
        Assert.Equal(ImageSourceQuality.Usable, ImageSource.Quality("https://i.scdn.co/image/abc"));
    }

    [Fact]
    public void SameSource_MatchesNormalizedUrls_CaseInsensitive()
    {
        var a = new Image("https://i.scdn.co/image/AbC");
        var b = new Image("spotify:image:abc");
        Assert.True(ImageSource.SameSource(a, b));
        Assert.False(ImageSource.SameSource(a, new Image("https://i.scdn.co/image/other")));
    }

    [Fact]
    public void PreferVisible_KeepsAlreadyShownCover_WhenLoadedUrlDiffers()
    {
        // Nav preview (card CDN size) vs detail payload (largest CDN size) — both usable, different hashes.
        var visible = new Image("https://i.scdn.co/image/cardsize", 300, 300);
        var incoming = new Image("https://i.scdn.co/image/fullsize", 640, 640);

        Image? chosen = ImageSource.PreferVisible(incoming, visible);

        Assert.Same(visible, chosen);
    }

    [Fact]
    public void PreferVisible_UsesIncoming_WhenVisibleMissingOrUnusable()
    {
        var incoming = new Image("https://i.scdn.co/image/fullsize", 640, 640);
        Assert.Same(incoming, ImageSource.PreferVisible(incoming, null));
        // Empty Image is unusable (Url normalizes spotify:image: tokens to CDN urls at construction).
        Assert.Same(incoming, ImageSource.PreferVisible(incoming, new Image("")));
    }

    [Fact]
    public void PreferVisible_KeepsVisible_WhenIncomingMissing()
    {
        var visible = new Image("https://i.scdn.co/image/cardsize");
        Assert.Same(visible, ImageSource.PreferVisible(null, visible));
        Assert.Same(visible, ImageSource.PreferVisible(new Image(""), visible));
    }

    [Fact]
    public void PreferVisible_SameSource_MergesBlurHashFromIncoming()
    {
        var visible = new Image("https://i.scdn.co/image/same", 300, 300);
        var incoming = new Image("https://i.scdn.co/image/same", 640, 640, BlurHash: "LGF5]+Yk^6#M@-5c,1J5@[or[Q6.");

        Image? chosen = ImageSource.PreferVisible(incoming, visible);

        Assert.NotNull(chosen);
        Assert.Equal(visible.Url, chosen!.Url);
        Assert.Equal(incoming.BlurHash, chosen.BlurHash);
        Assert.Equal(300, chosen.Width); // keep the already-decoded size metadata
    }
}
