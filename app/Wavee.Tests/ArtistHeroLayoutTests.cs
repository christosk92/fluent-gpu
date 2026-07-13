using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class ArtistHeroLayoutTests
{
    [Fact]
    public void HeroHeightFor_WideWidth_KeepsCurrentBannerHeight()
    {
        Assert.Equal(420f, ArtistHeroLayout.HeroHeightFor(900f));
        Assert.Equal(420f, ArtistHeroLayout.HeroHeightFor(1200f));
    }

    [Fact]
    public void HeroHeightFor_NarrowWidths_GrowsProgressivelyTaller()
    {
        Assert.Equal(640f, ArtistHeroLayout.HeroHeightFor(420f));
        Assert.Equal(585f, ArtistHeroLayout.HeroHeightFor(540f));
        Assert.Equal(530f, ArtistHeroLayout.HeroHeightFor(660f));
    }

    [Fact]
    public void HeroHeightFor_MonotonicallyShrinksAsWidthWidens()
    {
        float narrow = ArtistHeroLayout.HeroHeightFor(420f);
        float medium = ArtistHeroLayout.HeroHeightFor(660f);
        float wide = ArtistHeroLayout.HeroHeightFor(900f);

        Assert.True(narrow > medium);
        Assert.True(medium > wide);
    }

    [Theory]
    [InlineData(420f)]
    [InlineData(660f)]
    [InlineData(900f)]
    [InlineData(1200f)]
    public void BlendBackdrop_ExtendsPastHeroAndReleasesInContent(float width)
    {
        float hero = ArtistHeroLayout.HeroHeightFor(width);
        float backdrop = ArtistHeroLayout.BlendBackdropHeightFor(width);

        Assert.Equal(ArtistHeroLayout.ContentBlendTail, backdrop - hero);
        Assert.Equal(hero / backdrop, ArtistHeroLayout.BlendBoundaryFor(width), 5);
    }
}
