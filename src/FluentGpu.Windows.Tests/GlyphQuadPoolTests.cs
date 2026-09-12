using System;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

public sealed class GlyphQuadPoolTests
{
    [Fact]
    public void FreeReserveIsByteBoundedAcrossAllBuckets()
    {
        using var renderer = new GlyphRenderer();
        for (int power = 0; power < 14; power++)
            for (int count = 0; count < 16; count++)
                renderer.ReturnQuads(new ShapedGlyph[1 << power]);
        Assert.InRange(renderer.QuadPoolBytes, 1, 1024 * 1024);
        Assert.InRange(renderer.QuadPoolRetained, 1, 14 * 8);
        long retained = renderer.QuadPoolBytes;
        renderer.ReturnQuads(new ShapedGlyph[16384]); // out-of-pool pathological run
        Assert.Equal(retained, renderer.QuadPoolBytes);
    }

    [Fact]
    public void RentTransfersOwnershipOutOfFreeByteCensus()
    {
        using var renderer = new GlyphRenderer();
        var array = new ShapedGlyph[128];
        renderer.ReturnQuads(array);
        long retained = renderer.QuadPoolBytes;
        Assert.True(retained > 0);
        Assert.Same(array, renderer.RentQuads(100));
        Assert.Equal(0, renderer.QuadPoolBytes);
        Assert.Equal(0, renderer.QuadPoolRetained);
        renderer.ReturnQuads(array);
        Assert.Equal(retained, renderer.QuadPoolBytes);
        Assert.Empty(renderer.RentQuads(0));
        Assert.Equal(retained, renderer.QuadPoolBytes);
        renderer.ReturnQuads(Array.Empty<ShapedGlyph>());
        Assert.Equal(retained, renderer.QuadPoolBytes);
    }
}
