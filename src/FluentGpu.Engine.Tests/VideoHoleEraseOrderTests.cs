using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Where the composite plan puts a video hole's <c>EraseVideoHole</c> item relative to the segment that punched it
/// (gpu-renderer.md §7.3 "Emit order"): a hole punched on the tile erases BEFORE its segment — so the chrome recorded after
/// the hole in the same segment (transport, captions, a mini-player strip) survives — and a hole punched inside an inline
/// group layer erases AFTER it, because the in-tile punch cleared only that layer's scratch. The nesting tracker is the one
/// the per-slot scan runs over each arena.</summary>
public sealed class VideoHoleEraseOrderTests
{
    [Fact]
    public void AHolePunchedOnTheTileErasesBeforeItsSegment()
    {
        var layers = default(InlineLayerNesting);
        Assert.False(layers.InGroup);
        Assert.Equal(VideoEraseOrder.BeforeSegment, VideoHoleErase.Order(layers.InGroup));
    }

    [Theory]
    [InlineData((int)LayerKind.Opacity)]
    [InlineData((int)LayerKind.Blur)]
    [InlineData((int)LayerKind.EdgeFade)]
    public void AHolePunchedInsideAnInlineGroupLayerErasesAfterItsSegment(int kind)
    {
        var layers = default(InlineLayerNesting);
        layers.Push(kind);
        Assert.True(layers.InGroup);
        Assert.Equal(VideoEraseOrder.AfterSegment, VideoHoleErase.Order(layers.InGroup));
        layers.Pop();
        Assert.False(layers.InGroup);
        Assert.Equal(0, layers.Depth);
        Assert.Equal(VideoEraseOrder.BeforeSegment, VideoHoleErase.Order(layers.InGroup));
    }

    [Fact]
    public void AnAcrylicLayerDrawsOnTheTileSoItIsNotAGroup()
    {
        var layers = default(InlineLayerNesting);
        layers.Push((int)LayerKind.Acrylic);
        Assert.False(layers.InGroup);
        Assert.Equal(1, layers.Depth);
        layers.Push((int)LayerKind.Opacity);        // a group inside the acrylic slice's own layer
        Assert.True(layers.InGroup);
        layers.Push((int)LayerKind.Acrylic);
        Assert.True(layers.InGroup);                // still inside the group
        layers.Pop();
        layers.Pop();                               // the group closes
        Assert.False(layers.InGroup);
        Assert.Equal(1, layers.Depth);
        layers.Pop();
        Assert.Equal(0, layers.Depth);
        Assert.Equal(0, layers.Groups);
    }

    [Fact]
    public void NestedGroupsCloseInOrder()
    {
        var layers = default(InlineLayerNesting);
        layers.Push((int)LayerKind.Opacity);
        layers.Push((int)LayerKind.EdgeFade);
        Assert.Equal(2, layers.Groups);
        layers.Pop();
        Assert.True(layers.InGroup);
        layers.Pop();
        Assert.False(layers.InGroup);
    }

    [Fact]
    public void PastTheTrackedDepthEveryLayerCountsAsAGroupAndStaysBalanced()
    {
        var layers = default(InlineLayerNesting);
        for (int i = 0; i < 64; i++) layers.Push((int)LayerKind.Acrylic);
        Assert.False(layers.InGroup);
        layers.Push((int)LayerKind.Acrylic);        // depth 64: untracked — conservatively a group
        Assert.True(layers.InGroup);
        layers.Pop();
        Assert.False(layers.InGroup);
        for (int i = 0; i < 64; i++) layers.Pop();
        Assert.Equal(0, layers.Depth);
        Assert.Equal(0, layers.Groups);
    }

    [Fact]
    public void AnUnbalancedPopIsInert()
    {
        var layers = default(InlineLayerNesting);
        layers.Pop();
        Assert.Equal(0, layers.Depth);
        Assert.Equal(0, layers.Groups);
        layers.Push((int)LayerKind.Opacity);
        Assert.True(layers.InGroup);
    }
}
