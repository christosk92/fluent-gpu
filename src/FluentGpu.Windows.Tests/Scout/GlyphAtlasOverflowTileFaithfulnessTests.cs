using System;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// A glyph-atlas overflow while a composite turn is prepared shapes that turn's NEW glyphs blank (PackOrReset) and only
/// arms a deferred reset; no instance is dropped. A tile rastered on that turn must not be reported faithful, or the slice
/// table keeps it valid with the missing characters until its segment bytes change.
/// </summary>
public sealed class GlyphAtlasOverflowTileFaithfulnessTests
{
    // 200×200 masks: the 2048² mirror holds about a hundred before it is full (outside preparation it never grows).
    private static int FillAtlas(GlyphRenderer renderer, byte[] ink)
    {
        int pathId = 1;
        while (!renderer.AtlasResetPending && pathId < 1000)
            renderer.PackIconMask(pathId++, 200, 200, ink, out _, out _, out _, out _);
        return pathId;
    }

    private static byte[] Ink()
    {
        var ink = new byte[200 * 200];
        Array.Fill(ink, (byte)0xFF);
        return ink;
    }

    [Fact]
    public void OverflowBlanksTheGlyphWithoutCountingADrop()
    {
        using var renderer = new GlyphRenderer();
        byte[] ink = Ink();
        int pathId = FillAtlas(renderer, ink);
        Assert.True(renderer.AtlasResetPending);
        renderer.PackIconMask(pathId, 200, 200, ink, out float u0, out float v0, out float u1, out float v1);
        Assert.Equal(u0, u1);   // a zero-size entry: no quad is drawn
        Assert.Equal(v0, v1);
        Assert.Equal(0, renderer.DroppedInstances);
    }

    [Fact]
    public void TileRasteredWhileTheAtlasResetIsPendingIsNotFaithful()
    {
        using var renderer = new GlyphRenderer();
        FillAtlas(renderer, Ink());
        Assert.True(renderer.AtlasResetPending);
        // No instance, glyph-bank or image counter moved across the tile: only the atlas is owed.
        Assert.False(D3D12Device.TileRasterFaithful(0, 0, 0, renderer.DroppedInstances, 0, 0, renderer.AtlasResetPending));
    }

    [Fact]
    public void TheExistingCausesStillDecide()
    {
        Assert.True(D3D12Device.TileRasterFaithful(3, 3, 2, 2, 1, 1, atlasResetPending: false));
        Assert.False(D3D12Device.TileRasterFaithful(3, 4, 2, 2, 1, 1, atlasResetPending: false));   // instance drop
        Assert.False(D3D12Device.TileRasterFaithful(3, 3, 2, 5, 1, 1, atlasResetPending: false));   // glyph-bank drop
        Assert.False(D3D12Device.TileRasterFaithful(3, 3, 2, 2, 1, 2, atlasResetPending: false));   // image in flight
    }
}
