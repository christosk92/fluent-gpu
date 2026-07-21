using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class DetailVerticalLayoutTests
{
    [Theory]
    [InlineData(579f)]
    [InlineData(539f)]
    [InlineData(440f)]   // boundary inclusive: 440 is NOT below the 440 stack threshold
    public void OrientationFor_WideEnough_SideBySide(float w)
        => Assert.Equal(DetailHeroOrientation.SideBySide, DetailVerticalLayout.OrientationFor(w));

    [Theory]
    [InlineData(439f)]
    [InlineData(340f)]
    public void OrientationFor_Narrow_Stacked(float w)
        => Assert.Equal(DetailHeroOrientation.Stacked, DetailVerticalLayout.OrientationFor(w));

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
    [InlineData(440f, 160f)]   // 440·0.36 = 158.4 → clamp up to 160 min
    [InlineData(500f, 180f)]   // 500·0.36 = 180
    [InlineData(800f, 256f)]   // wide hero → taller cover carries the full info/description column
    public void ArtworkFor_SideBySide_Clamps(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, DetailHeroOrientation.SideBySide));

    [Theory]
    [InlineData(439f, 240f)]   // 439-48 = 391 → clamp down to 240 max
    [InlineData(260f, 212f)]   // 260-48 = 212 → in range
    [InlineData(200f, 180f)]   // 200-48 = 152 → clamp up to 180 min
    public void ArtworkFor_Stacked_Clamps(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, DetailHeroOrientation.Stacked));

    [Fact]
    public void DescriptionMaxLines_SideBySide3_Stacked4()
    {
        Assert.Equal(3, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.SideBySide));
        Assert.Equal(4, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.Stacked));
    }
}
