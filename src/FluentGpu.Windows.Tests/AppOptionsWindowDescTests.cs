using FluentGpu.Foundation;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Step 0 of the app-authoring plan: <see cref="AppOptions"/> maps onto <see cref="WindowDesc"/> through the one
/// <c>FluentApp.DescribeWindow</c> — the overlay shape (Topmost / ToolWindow / Resizable) included.</summary>
public sealed class AppOptionsWindowDescTests
{
    [Fact]
    public void OverlayShape_MapsOntoWindowDesc()
    {
        var d = FluentApp.DescribeWindow(new AppOptions
        {
            Title = "overlay", Width = 340, Height = 300, MinWidth = 200, MinHeight = 120,
            Mica = false, CustomFrame = true, Topmost = true, ToolWindow = true, Resizable = false,
        });
        Assert.Equal("overlay", d.Title);
        Assert.Equal(new Size2(340, 300), d.SizePx);
        Assert.Equal(new Size2(200, 120), d.MinClientSizeDip);
        Assert.False(d.Composited);
        Assert.True(d.CustomFrame);
        Assert.True(d.Topmost);
        Assert.True(d.ToolWindow);
        Assert.False(d.Resizable);
    }

    [Fact]
    public void Defaults_KeepTheOrdinaryAppWindow()
    {
        var d = FluentApp.DescribeWindow(new AppOptions());
        Assert.False(d.Topmost);
        Assert.False(d.ToolWindow);
        Assert.True(d.Resizable);
        Assert.False(d.CustomFrame);
    }
}
