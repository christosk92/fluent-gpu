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
