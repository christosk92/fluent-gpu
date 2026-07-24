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

    [Fact]
    public void SideIdentityMorph_IsLateAndHysteretic()
    {
        float enter = DetailVerticalLayout.IdentityMorphEnterOffset(
            DetailHeroOrientation.SideBySide,
            DetailVerticalLayout.SideArtworkSize,
            collapseDistance: 240f);

        Assert.Equal(88f, enter);
        Assert.Equal(64f, DetailVerticalLayout.IdentityMorphExitOffset(enter));
    }

    [Fact]
    public void ImmersiveIdentityMorph_TracksBottomTokenGeometry()
    {
        float enter = DetailVerticalLayout.IdentityMorphEnterOffset(
            DetailHeroOrientation.Immersive,
            artworkSize: 580f,
            collapseDistance: 600f);

        Assert.Equal(526.08f, enter, 2);
        Assert.Equal(502.08f, DetailVerticalLayout.IdentityMorphExitOffset(enter), 2);
    }

    [Theory]
    [InlineData(204f, 88f, 64f, 126.48f, 102f)]
    [InlineData(568f, 520f, 496f, 536f, 504f)]
    public void CompactTools_ArriveAfterIdentityAndLeaveBeforeIt(
        float collapse, float identityEnter, float identityExit, float toolsEnter, float toolsExit)
    {
        Assert.Equal(toolsEnter, DetailVerticalLayout.ToolsEnterOffset(collapse, identityEnter), 2);
        Assert.Equal(toolsExit, DetailVerticalLayout.ToolsExitOffset(collapse, identityExit), 2);
        Assert.True(toolsEnter > identityEnter);
        Assert.True(toolsExit > identityExit);
    }

    [Theory]
    [InlineData(320f, 160f)]
    [InlineData(580f, 266.8f)]
    [InlineData(1400f, 480f)]
    public void CompactPill_IsContentSizedWithViewportCap(float viewport, float expected)
    {
        Assert.Equal(expected, DetailVerticalLayout.CompactPillWidthCap(viewport), 2);
    }

    [Fact]
    public void SideHero_EliminatesPaddingGapBeforeToolbar()
    {
        Assert.Equal(0f, DetailVerticalLayout.SideHeroBottomPad);
        Assert.Equal(0f, DetailVerticalLayout.SideToolbarTopPad);
    }

    [Theory]
    [InlineData(320f, 512)]
    [InlineData(384f, 512)]
    [InlineData(385f, 1024)]
    [InlineData(580f, 1024)]
    public void ImmersiveArtworkDecodePx_UsesStableHighResolutionBuckets(float size, int expected)
        => Assert.Equal(expected, DetailVerticalLayout.ImmersiveArtworkDecodePx(size));
}
