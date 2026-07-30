using Xunit;

namespace Wavee.Tests;

public class ShellResponsiveLayoutTests
{
    [Fact]
    public void ShellNarrow_Uses720To760Hysteresis()
    {
        Assert.False(ShellResponsiveLayout.NarrowFor(721f, current: false, initialized: false));
        Assert.True(ShellResponsiveLayout.NarrowFor(720f, current: false, initialized: true));
        Assert.True(ShellResponsiveLayout.NarrowFor(759f, current: true, initialized: true));
        Assert.False(ShellResponsiveLayout.NarrowFor(760f, current: true, initialized: true));
    }

    [Fact]
    public void ToolbarNarrow_Uses520To560Hysteresis()
    {
        Assert.False(ShellResponsiveLayout.ToolbarNarrowFor(521f, current: false, initialized: false));
        Assert.True(ShellResponsiveLayout.ToolbarNarrowFor(520f, current: false, initialized: true));
        Assert.True(ShellResponsiveLayout.ToolbarNarrowFor(559f, current: true, initialized: true));
        Assert.False(ShellResponsiveLayout.ToolbarNarrowFor(560f, current: true, initialized: true));
    }

    [Theory]
    [InlineData(360f, 300f, 300f)]
    [InlineData(360f, 460f, 328f)]
    [InlineData(720f, 200f, 240f)]
    public void DrawerWidth_ClampsPreferredWidthToViewport(float viewport, float preferred, float expected)
        => Assert.Equal(expected, ShellResponsiveLayout.DrawerWidth(viewport, preferred));

    [Fact]
    public void ClosedDrawer_RestsTransparentAndFullyOffCanvas()
    {
        Assert.Equal(0f, ShellResponsiveLayout.DrawerRestingOpacity(open: false));
        Assert.Equal(-300f, ShellResponsiveLayout.DrawerRestingTranslateX(open: false, width: 300f));
        Assert.Equal(1f, ShellResponsiveLayout.DrawerRestingOpacity(open: true));
        Assert.Equal(0f, ShellResponsiveLayout.DrawerRestingTranslateX(open: true, width: 300f));
    }

    [Theory]
    [InlineData(1200f, 240f)]
    [InlineData(1500f, 280f)]
    [InlineData(1900f, 320f)]
    public void NavPaneDefault_LaddersOpenPaneLengthByWindowClass(float viewport, float expected)
    {
        Assert.Equal(expected, ShellResponsiveLayout.NominalNavPaneDefaultFor(viewport));
        Assert.Equal(expected, ShellResponsiveLayout.InitialNavPaneDefaultForViewport(viewport));
        Assert.Equal(expected, ShellResponsiveLayout.NavPaneDefaultFor(viewport, current: 300f, initialized: false));
    }

    [Fact]
    public void NavPaneDefault_WidensAt1400_ThenHolds280Until1376()
    {
        float w = ShellResponsiveLayout.NavPaneDefaultFor(1399f, 240f, initialized: true);
        Assert.Equal(240f, w);

        w = ShellResponsiveLayout.NavPaneDefaultFor(1400f, w, initialized: true);
        Assert.Equal(280f, w);   // widening applies immediately at the threshold

        w = ShellResponsiveLayout.NavPaneDefaultFor(1390f, w, initialized: true);
        Assert.Equal(280f, w);   // a dip back inside the 24-DIP band holds the wider tier

        w = ShellResponsiveLayout.NavPaneDefaultFor(1376f, w, initialized: true);
        Assert.Equal(280f, w);   // 1400 - 24: the last width that still holds

        w = ShellResponsiveLayout.NavPaneDefaultFor(1375f, w, initialized: true);
        Assert.Equal(240f, w);   // past the band → shrink
    }

    [Fact]
    public void NavPaneDefault_WideTierUsesTheSameBand()
    {
        float w = ShellResponsiveLayout.NavPaneDefaultFor(1800f, 280f, initialized: true);
        Assert.Equal(320f, w);

        w = ShellResponsiveLayout.NavPaneDefaultFor(1776f, w, initialized: true);
        Assert.Equal(320f, w);

        w = ShellResponsiveLayout.NavPaneDefaultFor(1775f, w, initialized: true);
        Assert.Equal(280f, w);

        w = ShellResponsiveLayout.NavPaneDefaultFor(1900f, 240f, initialized: true);
        Assert.Equal(320f, w);   // a multi-tier widen jumps straight to the nominal tier
    }

    [Fact]
    public void NavPaneDefault_UnseededRunIgnoresCurrent_ZeroViewportKeepsIt()
    {
        // The shell constructor has no viewport yet: it seeds the narrow tier and the first effect run commits the real one.
        Assert.Equal(240f, ShellResponsiveLayout.InitialNavPaneDefaultForViewport(0f));
        Assert.Equal(300f, ShellResponsiveLayout.NavPaneDefaultFor(0f, current: 300f, initialized: false));
        Assert.Equal(300f, ShellResponsiveLayout.NavPaneDefaultFor(0f, current: 300f, initialized: true));
        // First run with a real width has no band to honour, so `current` does not hold it back in either direction.
        Assert.Equal(320f, ShellResponsiveLayout.NavPaneDefaultFor(1900f, current: 240f, initialized: false));
        Assert.Equal(240f, ShellResponsiveLayout.NavPaneDefaultFor(1200f, current: 460f, initialized: false));
    }

    [Fact]
    public void NavPaneDefault_PersistedLegacyWidthFollowsTheLadder()
    {
        // 300 was the old fixed default. Until the user drags the seam it is not a preference, so it resolves to its tier.
        Assert.Equal(240f, ShellResponsiveLayout.NavPaneDefaultFor(1280f, current: 300f, initialized: true));
        Assert.Equal(280f, ShellResponsiveLayout.NavPaneDefaultFor(1500f, current: 300f, initialized: true));
    }

    [Fact]
    public void NavPaneClampBounds_ContainEveryDefaultTier()
    {
        Assert.Equal(240f, ShellResponsiveLayout.NavPaneMinW);
        Assert.Equal(460f, ShellResponsiveLayout.NavPaneMaxW);
        Assert.InRange(ShellResponsiveLayout.NavPaneNarrowW, ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW);
        Assert.InRange(ShellResponsiveLayout.NavPaneMidW, ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW);
        Assert.InRange(ShellResponsiveLayout.NavPaneWideW, ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW);
        // The narrow-drawer floor and the pane floor are the same 240 seam.
        Assert.Equal(ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.DrawerMinW);
    }

    [Fact]
    public void ClosedDrawer_RestingTranslationTracksWidthGrowth()
    {
        float initialWidth = ShellResponsiveLayout.DrawerWidth(viewportWidth: 360f, preferredWidth: 460f);
        float grownWidth = ShellResponsiveLayout.DrawerWidth(viewportWidth: 700f, preferredWidth: 460f);

        Assert.Equal(328f, initialWidth);
        Assert.Equal(-328f, ShellResponsiveLayout.DrawerRestingTranslateX(open: false, initialWidth));
        Assert.Equal(460f, grownWidth);
        Assert.Equal(-460f, ShellResponsiveLayout.DrawerRestingTranslateX(open: false, grownWidth));
    }
}
