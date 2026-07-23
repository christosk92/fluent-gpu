using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class DetailVerticalLayoutTests
{
    [Theory]
    [InlineData(580f)]
    [InlineData(600f)]
    [InlineData(900f)]
    public void OrientationFor_WideEnough_SideBySide(float w)
        => Assert.Equal(DetailHeroOrientation.SideBySide, DetailVerticalLayout.OrientationFor(w));

    [Theory]
    [InlineData(579f)]
    [InlineData(540f)]
    [InlineData(340f)]
    public void OrientationFor_Narrow_Immersive(float w)
        => Assert.Equal(DetailHeroOrientation.Immersive, DetailVerticalLayout.OrientationFor(w));

    [Fact]
    public void OrientationFor_Unmeasured_UsesFallbackSideBySide()
        => Assert.Equal(DetailHeroOrientation.SideBySide, DetailVerticalLayout.OrientationFor(0f));

    [Fact]
    public void PageLayoutConstants_MirrorPersistedSettingValues()
    {
        Assert.Equal(0, DetailVerticalLayout.PageAuto);
        Assert.Equal(1, DetailVerticalLayout.PageHero);
    }

    [Theory]
    [InlineData(580f, 200f)]
    [InlineData(800f, 200f)]
    [InlineData(1400f, 200f)]
    public void ArtworkFor_SideBySide_IsFixed200(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, DetailHeroOrientation.SideBySide));

    [Theory]
    [InlineData(579f, 579f)]
    [InlineData(420f, 420f)]
    [InlineData(300f, 300f)]
    public void ArtworkFor_Immersive_IsFullWidthSquare(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, DetailHeroOrientation.Immersive));

    [Fact]
    public void DescriptionMaxLines_SideBySide3_Immersive4()
    {
        Assert.Equal(3, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.SideBySide));
        Assert.Equal(4, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.Immersive));
    }

    [Fact]
    public void OrientationFor_UsesResizeHysteresis()
    {
        Assert.Equal(DetailHeroOrientation.SideBySide,
            DetailVerticalLayout.OrientationFor(579f, DetailHeroOrientation.SideBySide, initialized: true));
        Assert.Equal(DetailHeroOrientation.Immersive,
            DetailVerticalLayout.OrientationFor(560f, DetailHeroOrientation.SideBySide, initialized: true));
        Assert.Equal(DetailHeroOrientation.Immersive,
            DetailVerticalLayout.OrientationFor(599f, DetailHeroOrientation.Immersive, initialized: true));
        Assert.Equal(DetailHeroOrientation.SideBySide,
            DetailVerticalLayout.OrientationFor(600f, DetailHeroOrientation.Immersive, initialized: true));
    }

    [Fact]
    public void StickyGeometry_UsesCompactIdentityPlusChromeInset()
    {
        Assert.Equal(56f, DetailVerticalLayout.CompactIdentityHeight);
        Assert.Equal(36f, DetailVerticalLayout.CompactArtworkSize);
        Assert.Equal(93f, DetailVerticalLayout.StickyClipInset);
    }

    [Theory]
    [InlineData(260f, 204f)]
    [InlineData(56f, 1f)]
    [InlineData(20f, 1f)]
    public void CollapseDistance_EndsAtCompactIdentity(float expanded, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.CollapseDistance(expanded));
}
