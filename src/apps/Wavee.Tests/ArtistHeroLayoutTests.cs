using FluentGpu.Dsl;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class ArtistHeroLayoutTests
{
    [Theory]
    [InlineData(420f, ArtistHeroTier.Narrow, ArtistHeroTier.Narrow)]
    [InlineData(504f, ArtistHeroTier.Narrow, ArtistHeroTier.Compact)]
    [InlineData(700f, ArtistHeroTier.Compact, ArtistHeroTier.Compact)]
    [InlineData(784f, ArtistHeroTier.Compact, ArtistHeroTier.Medium)]
    [InlineData(1000f, ArtistHeroTier.Wide, ArtistHeroTier.Medium)]
    [InlineData(1016f, ArtistHeroTier.Wide, ArtistHeroTier.Wide)]
    [InlineData(1064f, ArtistHeroTier.Medium, ArtistHeroTier.Wide)]
    public void TierFor_UsesTheDetailLayoutHysteresis(float width, ArtistHeroTier previous, ArtistHeroTier expected)
        => Assert.Equal(expected, ArtistHeroLayout.TierFor(width, previous));

    [Fact]
    public void WideAndMedium_UseHorizontalVeils()
    {
        var wide = ArtistHeroLayout.For(1440f, ArtistHeroTier.Wide);
        var medium = ArtistHeroLayout.For(900f, ArtistHeroTier.Medium);

        Assert.Equal(ArtistHeroVeilAxis.Horizontal, wide.VeilAxis);
        Assert.Equal(ArtistHeroVeilAxis.Horizontal, medium.VeilAxis);
        Assert.False(wide.Stacked);
        Assert.False(medium.Stacked);
        Assert.Equal(ArtistHeroLayout.WideHeight, wide.MinHeight);
        Assert.Equal(ArtistHeroLayout.MediumHeight, medium.MinHeight);
        Assert.True(wide.CopyMaxWidth > medium.CopyMaxWidth);
    }

    [Fact]
    public void CompactAndNarrow_UseBottomCopyWithVerticalVeils()
    {
        var compact = ArtistHeroLayout.For(720f, ArtistHeroTier.Compact);
        var narrow = ArtistHeroLayout.For(420f, ArtistHeroTier.Narrow);

        Assert.Equal(ArtistHeroVeilAxis.Vertical, compact.VeilAxis);
        Assert.Equal(ArtistHeroVeilAxis.Vertical, narrow.VeilAxis);
        Assert.True(compact.Stacked);
        Assert.True(narrow.Stacked);
    }

    [Fact]
    public void PageGutter_UsesOnlySemanticSpacingTokens()
    {
        Assert.Equal(Spacing.PageNarrow, ArtistHeroLayout.PageGutterFor(420f));
        Assert.Equal(Spacing.L, ArtistHeroLayout.PageGutterFor(700f));
        Assert.Equal(Spacing.XXXL, ArtistHeroLayout.PageGutterFor(900f));
        Assert.Equal(Spacing.PageWide, ArtistHeroLayout.PageGutterFor(1200f));
    }

    [Fact]
    public void FadeBand_AlwaysCoversTheParallaxLag()
    {
        foreach (float height in new[] { ArtistHeroLayout.MediumHeight, ArtistHeroLayout.WideHeight,
                                        ArtistHeroLayout.NarrowHeight, ArtistHeroLayout.CompactHeight })
            Assert.True(ArtistHeroLayout.PhotoFadeBandFor(height)
                        > height * ArtistHeroLayout.PhotoParallaxFraction);
    }

    [Fact]
    public void CollapseDistance_LeavesTheSharedCompactIdentityFloor()
    {
        foreach (float height in new[] { ArtistHeroLayout.MediumHeight, ArtistHeroLayout.WideHeight,
                                        ArtistHeroLayout.NarrowHeight, ArtistHeroLayout.CompactHeight })
        {
            float distance = ArtistHeroLayout.CollapseDistance(height);
            Assert.Equal(ArtistHeroLayout.CompactIdentityHeight, height - distance);
            Assert.Equal(DetailVerticalLayout.CompactIdentityHeight, ArtistHeroLayout.CompactIdentityHeight);
        }
    }

    [Theory]
    [InlineData(ArtistHeroTier.Wide, true)]
    [InlineData(ArtistHeroTier.Medium, true)]
    [InlineData(ArtistHeroTier.Compact, false)]
    [InlineData(ArtistHeroTier.Narrow, false)]
    public void CompactBarPolicy_DropsFollowUnderPressure(ArtistHeroTier tier, bool follow)
    {
        var policy = ArtistHeroLayout.CompactBarPolicyFor(tier);
        Assert.Equal(follow, policy.ShowFollow);
    }
}
