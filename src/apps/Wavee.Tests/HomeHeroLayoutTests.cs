using Xunit;

namespace Wavee.Tests;

public class HomeHeroLayoutTests
{
    [Theory]
    // 344/304/296, one DIP under the pre-convergence 345/305/297: the hero's meta line moved from a bespoke 13/19 onto
    // Body 14/20 and its margin from 18 onto the 4-grid's 16, so the block is 36 rather than 37. Every other block is
    // byte-identical — the tag row in particular stayed at 32 because its padding grew exactly as its margin shrank.
    [InlineData(699.9f, (byte)0, 296f, true)]
    [InlineData(700f, (byte)1, 304f, false)]
    [InlineData(979.9f, (byte)1, 304f, false)]
    [InlineData(980f, (byte)2, 344f, false)]
    public void TierGeometry_IsExact(float width, byte tier, float height, bool stacked)
    {
        var metrics = HomeHeroLayout.For(width);
        Assert.Equal((HomeHeroTier)tier, metrics.Tier);
        Assert.Equal(height, metrics.Height);
        Assert.Equal(height, metrics.ArtworkSize);
        Assert.Equal(stacked, metrics.Stacked);
        Assert.Equal(48f, metrics.CopyPaddingX);
        Assert.Equal(44f, metrics.CopyPaddingY);
        Assert.Equal(height, HomeHeroLayout.HeightFor(width));
    }

    [Fact]
    public void FlattenedSurface_PreservesThePreviousRendererEstimatorArithmetic()
    {
        Assert.Equal(344f, HomeHeroLayout.ContentHeight(HomeHeroTier.Wide));
        Assert.Equal(304f, HomeHeroLayout.ContentHeight(HomeHeroTier.Medium));
        Assert.Equal(296f, HomeHeroLayout.ContentHeight(HomeHeroTier.Narrow));
        Assert.Equal(96f, HomeHeroLayout.ArtworkFade);
    }

    /// <summary>The tier heights are not a magic table: each is the sum of the SAME blocks the renderer stacks, and
    /// each block is a ramp line height plus a Spacing rung. If someone re-hand-picks a size in HomeCards.HeroBand and
    /// forgets this file, the renderer and the virtual estimator disagree and the feed re-pins its scroll anchor
    /// mid-scroll — so the arithmetic is pinned here explicitly rather than only as a total.</summary>
    [Theory]
    // HomeHeroTier is internal to the source-included app file, so the theory takes the ordinal (xunit theories must
    // be public and CS0051 forbids an internal parameter type).
    [InlineData((int)HomeHeroTier.Wide, 2f * 60f)]
    [InlineData((int)HomeHeroTier.Medium, 2f * 40f)]
    [InlineData((int)HomeHeroTier.Narrow, 2f * 36f)]
    public void ContentHeight_IsTheSumOfOnRampBlocks(int tierOrdinal, float titleBlock)
    {
        var tier = (HomeHeroTier)tierOrdinal;
        const float copyPaddingY = 44f;      // Spacing.L + Spacing.XXL + Spacing.XS
        const float eyebrowBlock = 16f + 8f; // Caption 12/16 + an 8 margin
        const float titleMargin = 12f;
        const float tagsBlock = 20f + 12f;   // Caption 12/16 + 2x2 padding, + a 12 margin
        const float metaBlock = 20f + 16f;   // Body 14/20 + a 16 margin
        const float actionsBlock = 32f;      // the hero button row

        Assert.Equal(
            2f * copyPaddingY + eyebrowBlock + titleBlock + titleMargin + tagsBlock + metaBlock + actionsBlock,
            HomeHeroLayout.ContentHeight(tier));
    }
}
