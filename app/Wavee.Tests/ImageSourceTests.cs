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
}
