using Xunit;

namespace Wavee.Tests;

public class SetupLayoutTests
{
    [Theory]
    [InlineData(0f, 896f)]
    [InlineData(1200f, 896f)]
    [InlineData(900f, 868f)]
    [InlineData(250f, 300f)]
    public void PlateWidth_ClampsToTheReferencePlate(float viewport, float expected)
        => Assert.Equal(expected, SetupLayout.PlateWidth(viewport));

    [Theory]
    [InlineData(896f, 0)]
    [InlineData(700f, 0)]
    [InlineData(699f, 1)]
    [InlineData(520f, 1)]
    [InlineData(519f, 2)]
    [InlineData(360f, 2)]
    [InlineData(359f, 3)]
    public void NominalTier_UsesTheOrderedPressureLadder(float width, int expected)
        => Assert.Equal(expected, (int)SetupLayout.NominalTierFor(width));

    [Fact]
    public void TierFor_NarrowsImmediately_AndWidensPastTheRecoveryBand()
    {
        var tier = SetupLayoutTier.Wide;
        tier = SetupLayout.TierFor(699f, tier);
        Assert.Equal(SetupLayoutTier.Compact, tier);
        Assert.Equal(SetupLayoutTier.Compact, SetupLayout.TierFor(723f, tier));
        Assert.Equal(SetupLayoutTier.Wide, SetupLayout.TierFor(724f, tier));

        tier = SetupLayout.TierFor(519f, SetupLayoutTier.Compact);
        Assert.Equal(SetupLayoutTier.Narrow, tier);
        Assert.Equal(SetupLayoutTier.Narrow, SetupLayout.TierFor(543f, tier));
        Assert.Equal(SetupLayoutTier.Compact, SetupLayout.TierFor(544f, tier));
    }

    [Fact]
    public void TierCapabilities_DropOnlyTheRequiredStructure()
    {
        Assert.True(SetupLayout.ShowsHero(SetupLayoutTier.Wide));
        Assert.False(SetupLayout.ShowsHero(SetupLayoutTier.Compact));
        Assert.False(SetupLayout.StacksSignIn(SetupLayoutTier.Compact));
        Assert.True(SetupLayout.StacksSignIn(SetupLayoutTier.Narrow));
        Assert.True(SetupLayout.StacksFooter(SetupLayoutTier.Narrow));
        Assert.True(SetupLayout.StacksFooterActions(SetupLayoutTier.UltraNarrow));
        Assert.Equal(80f, SetupLayout.FooterHeightFor(SetupLayoutTier.Wide));
        Assert.Equal(108f, SetupLayout.FooterHeightFor(SetupLayoutTier.Narrow));
        Assert.Equal(144f, SetupLayout.FooterHeightFor(SetupLayoutTier.UltraNarrow));
    }
}
