using Xunit;

namespace Wavee.Tests;

public class PlayerBarResponsiveLayoutTests
{
    [Theory]
    [InlineData(300f, PlayerBarTier.Minimal)]
    [InlineData(439f, PlayerBarTier.Minimal)]
    [InlineData(440f, PlayerBarTier.Compact)]
    [InlineData(759f, PlayerBarTier.Compact)]
    [InlineData(760f, PlayerBarTier.Medium)]
    [InlineData(899f, PlayerBarTier.Medium)]
    [InlineData(900f, PlayerBarTier.Comfortable)]
    [InlineData(1099f, PlayerBarTier.Comfortable)]
    [InlineData(1100f, PlayerBarTier.Wide)]
    [InlineData(1239f, PlayerBarTier.Wide)]
    [InlineData(1240f, PlayerBarTier.Full)]
    public void NominalTier_UsesTheDocumentedBoundaries(float width, PlayerBarTier expected)
        => Assert.Equal(expected, PlayerBarResponsiveLayout.Nominal(width));

    [Theory]
    [InlineData(PlayerBarResponsiveLayout.CompactW, PlayerBarTier.Minimal, PlayerBarTier.Compact)]
    [InlineData(PlayerBarResponsiveLayout.MediumW, PlayerBarTier.Compact, PlayerBarTier.Medium)]
    [InlineData(PlayerBarResponsiveLayout.ComfortableW, PlayerBarTier.Medium, PlayerBarTier.Comfortable)]
    [InlineData(PlayerBarResponsiveLayout.WideW, PlayerBarTier.Comfortable, PlayerBarTier.Wide)]
    [InlineData(PlayerBarResponsiveLayout.FullW, PlayerBarTier.Wide, PlayerBarTier.Full)]
    public void Tier_WidensImmediatelyAndNarrowsAfterThe24DipBand(
        float threshold,
        PlayerBarTier lower,
        PlayerBarTier upper)
    {
        Assert.Equal(upper, PlayerBarResponsiveLayout.Resolve(threshold, lower, initialized: true));
        Assert.Equal(upper, PlayerBarResponsiveLayout.Resolve(
            threshold - PlayerBarResponsiveLayout.NarrowHysteresis,
            upper,
            initialized: true));
        Assert.Equal(lower, PlayerBarResponsiveLayout.Resolve(
            threshold - PlayerBarResponsiveLayout.NarrowHysteresis - 1f,
            upper,
            initialized: true));
    }

    [Theory]
    [InlineData(PlayerBarTier.Minimal)]
    [InlineData(PlayerBarTier.Compact)]
    [InlineData(PlayerBarTier.Medium)]
    [InlineData(PlayerBarTier.Comfortable)]
    [InlineData(PlayerBarTier.Wide)]
    [InlineData(PlayerBarTier.Full)]
    public void EveryTier_PreservesIdentityHeartAndDeviceRoute(PlayerBarTier tier)
    {
        var layout = PlayerBarLayout.ForTier(tier);

        Assert.True(layout.ShowSubtitle);
        Assert.True(layout.ShowLikeSlot);
        Assert.True(layout.ShowDevices);
    }

    [Fact]
    public void Compact_KeepsRemainingTimeButMovesSecondaryCommandsToOverflow()
    {
        var layout = PlayerBarLayout.ForTier(PlayerBarTier.Compact);

        Assert.True(layout.ShowTimesRemaining);
        Assert.False(layout.ShowTimesElapsed);
        Assert.False(layout.ShowPrevNext);
        Assert.False(layout.ShowVolumeButton);
        Assert.False(layout.ShowLyrics);
        Assert.False(layout.ShowShuffleRepeat);
    }

    [Fact]
    public void Minimal_RetainsOnlyTheEssentialTransportSurface()
    {
        var layout = PlayerBarLayout.ForTier(PlayerBarTier.Minimal);

        Assert.False(layout.ShowTimesElapsed);
        Assert.False(layout.ShowTimesRemaining);
        Assert.False(layout.ShowPrevNext);
        Assert.False(layout.ShowVolumeButton);
        Assert.False(layout.ShowLyrics);
        Assert.False(layout.ShowShuffleRepeat);
        Assert.False(layout.ShowQueue);
        Assert.False(layout.ShowExpand);
    }

    [Fact]
    public void Full_ExposesEveryDesktopPlayerCommand()
    {
        var layout = PlayerBarLayout.ForTier(PlayerBarTier.Full);

        Assert.True(layout.ShowExpand);
        Assert.True(layout.ShowQueue);
        Assert.True(layout.ShowVolumeSlider);
        Assert.True(layout.ShowShuffleRepeat);
        Assert.True(layout.ShowVolumeButton);
        Assert.True(layout.ShowLyrics);
        Assert.True(layout.ShowRemoteDeviceLine);
        Assert.True(layout.ShowTimesElapsed);
        Assert.True(layout.ShowTimesRemaining);
        Assert.True(layout.ShowPrevNext);
    }
}
