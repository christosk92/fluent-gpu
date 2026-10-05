using FluentGpu.Dsl;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>An explicit-extent image decodes at the PHYSICAL pixels it paints (DIP × device scale, never smaller); a fluid
/// image keeps the caller's DecodePx hint (already physical).</summary>
public sealed class ImageDecodeTargetTests
{
    [Theory]
    [InlineData(1f, 100, 100)]
    [InlineData(1.5f, 150, 150)]
    [InlineData(2f, 200, 200)]
    [InlineData(1.25f, 125, 125)]
    public void ExplicitExtentScalesToPhysicalPixels(float scale, int w, int h)
    {
        var im = new ImageEl { Source = "x", Width = 100f, Height = 100f, DecodePx = 64f };
        Assert.Equal((w, h), FluentGpu.Reconciler.TreeReconciler.ImageDecodeTarget(in im, scale));
    }

    [Fact]
    public void FractionalPhysicalExtentRoundsUpNeverDown()
    {
        var im = new ImageEl { Source = "x", Width = 33f, Height = 33f };
        Assert.Equal((50, 50), FluentGpu.Reconciler.TreeReconciler.ImageDecodeTarget(in im, 1.5f));   // 49.5 -> 50
    }

    [Fact]
    public void FluidImageKeepsTheHintAndDerivesHeightFromAspect()
    {
        var im = new ImageEl { Source = "x", DecodePx = 300f, AspectRatio = 1.5f };
        Assert.Equal((300, 200), FluentGpu.Reconciler.TreeReconciler.ImageDecodeTarget(in im, 2f));
    }

    [Fact]
    public void BadScaleFallsBackToOne()
    {
        var im = new ImageEl { Source = "x", Width = 40f, Height = 40f };
        Assert.Equal((40, 40), FluentGpu.Reconciler.TreeReconciler.ImageDecodeTarget(in im, 0f));
        Assert.Equal((40, 40), FluentGpu.Reconciler.TreeReconciler.ImageDecodeTarget(in im, float.NaN));
    }
}

public sealed class ImageRetargetTests
{
    [Fact]
    public void ExplicitExtentFollowsTheNewScale()
    {
        Assert.True(FluentGpu.Reconciler.TreeReconciler.TryRetargetDecode(100f, 100f, float.NaN, 0, 2f, out int w, out int h));
        Assert.Equal((200, 200), (w, h));
        Assert.True(FluentGpu.Reconciler.TreeReconciler.TryRetargetDecode(100f, 100f, float.NaN, 0, 1f, out w, out h));
        Assert.Equal((100, 100), (w, h));   // and back down: no 4x bytes kept
    }

    [Fact]
    public void HeightFromAspectElseTheOldHeight()
    {
        Assert.True(FluentGpu.Reconciler.TreeReconciler.TryRetargetDecode(100f, float.NaN, 2f, 77, 1.5f, out _, out int h));
        Assert.Equal(75, h);
        Assert.True(FluentGpu.Reconciler.TreeReconciler.TryRetargetDecode(100f, float.NaN, float.NaN, 77, 1.5f, out _, out h));
        Assert.Equal(77, h);
    }

    [Fact]
    public void AFluidImageIsNeverRetargeted()
        => Assert.False(FluentGpu.Reconciler.TreeReconciler.TryRetargetDecode(float.NaN, float.NaN, 1f, 0, 2f, out _, out _));
}
