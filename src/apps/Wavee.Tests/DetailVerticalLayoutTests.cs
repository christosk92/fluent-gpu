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
    public void OrientationFor_Narrow_Immersive(float w)
        => Assert.Equal(DetailHeroOrientation.Immersive, DetailVerticalLayout.OrientationFor(w));

    [Theory]
    [InlineData(419f)]
    [InlineData(340f)]
    [InlineData(280f)]
    public void OrientationFor_UltraNarrow_Compact(float w)
        => Assert.Equal(DetailHeroOrientation.Compact, DetailVerticalLayout.OrientationFor(w));

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
    public void ArtworkFor_Immersive_IsFullWidthSquare(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, DetailHeroOrientation.Immersive));

    [Theory]
    [InlineData(419f, 96f)]
    [InlineData(340f, 96f)]
    [InlineData(339f, 64f)]
    [InlineData(280f, 64f)]
    public void ArtworkFor_Compact_UsesThumbnailNotFullWidth(float w, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.ArtworkFor(w, DetailHeroOrientation.Compact));

    [Fact]
    public void DescriptionMaxLines_SideBySide3_Immersive4()
    {
        Assert.Equal(3, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.SideBySide));
        Assert.Equal(4, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.Immersive));
        Assert.Equal(0, DetailVerticalLayout.DescriptionMaxLines(DetailHeroOrientation.Compact));
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
    public void CompactOrientation_Uses400To440ResizeHysteresis()
    {
        Assert.Equal(DetailHeroOrientation.Immersive,
            DetailVerticalLayout.OrientationFor(419f, DetailHeroOrientation.Immersive, initialized: true));
        Assert.Equal(DetailHeroOrientation.Compact,
            DetailVerticalLayout.OrientationFor(400f, DetailHeroOrientation.Immersive, initialized: true));
        Assert.Equal(DetailHeroOrientation.Compact,
            DetailVerticalLayout.OrientationFor(439f, DetailHeroOrientation.Compact, initialized: true));
        Assert.Equal(DetailHeroOrientation.Immersive,
            DetailVerticalLayout.OrientationFor(440f, DetailHeroOrientation.Compact, initialized: true));
    }

    [Fact]
    public void StickyGeometry_UsesCompactIdentityPlusChromeInset()
    {
        Assert.Equal(56f, DetailVerticalLayout.CompactIdentityHeight);
        Assert.Equal(36f, DetailVerticalLayout.CompactArtworkSize);
        Assert.Equal(37f, DetailVerticalLayout.ChromeExtent());
        Assert.Equal(85f, DetailVerticalLayout.ChromeExtent(contentFilterExtent: 48f));
        Assert.Equal(93f, DetailVerticalLayout.StickyClipInset());
        Assert.Equal(141f, DetailVerticalLayout.StickyClipInset(contentFilterExtent: 48f));
    }

    [Fact]
    public void VerticalViewport_MapsEveryLiveTrackToExpandableSlot()
    {
        const int visibleTracks = 4;
        Assert.Equal(DetailVerticalItemRole.Hero, DetailVerticalLayout.ItemRole(0, visibleTracks));
        Assert.Equal(DetailVerticalItemRole.Chrome, DetailVerticalLayout.ItemRole(1, visibleTracks));
        for (int i = 2; i < 2 + visibleTracks; i++)
            Assert.Equal(DetailVerticalItemRole.ExpandableTrack,
                DetailVerticalLayout.ItemRole(i, visibleTracks));
        Assert.Equal(DetailVerticalItemRole.Empty,
            DetailVerticalLayout.ItemRole(2 + visibleTracks, visibleTracks));
    }

    [Theory]
    [InlineData(260f, 204f)]
    [InlineData(56f, 1f)]
    [InlineData(20f, 1f)]
    public void CollapseDistance_EndsAtCompactIdentity(float expanded, float expected)
        => Assert.Equal(expected, DetailVerticalLayout.CollapseDistance(expanded));

    [Theory]
    [InlineData(204f, 108f, 160f)]
    [InlineData(568f, 472f, 524f)]
    [InlineData(40f, 0f, 0f)]
    public void ScrollHandoff_UsesLateOverlappingWindows(float collapse, float expandedStart, float compactStart)
    {
        Assert.Equal(expandedStart, DetailVerticalLayout.ExpandedFadeStart(collapse));
        Assert.Equal(compactStart, DetailVerticalLayout.CompactRevealStart(collapse));
        // Compact identity starts before the expanded presentation reaches zero, so there is no dead visual interval.
        Assert.True(compactStart < collapse);
        Assert.True(expandedStart <= compactStart);
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
