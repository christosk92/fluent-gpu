using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Pure policy pieces of the GPU memory trims: scratch size ladder, free-scratch byte cap and idle windows, the
/// retained-tile wall-clock stale rule.</summary>
public sealed class SurfacePoolPolicyTests
{
    [Theory]
    [InlineData(1, 64)]
    [InlineData(1195, 1216)]
    [InlineData(2048, 2048)]
    [InlineData(2049, 2176)]
    [InlineData(2304, 2304)]      // used to round to 4096
    [InlineData(2305, 2432)]
    [InlineData(4000, 4096)]
    public void DimIsLinearAboveTheCeilingToo(int px, int want) => Assert.Equal(want, LayerTargetBucket.Dim(px));

    [Fact]
    public void DimNeverWastesMoreThanOneStep()
    {
        for (int px = 1; px <= 8192; px++)
        {
            int d = LayerTargetBucket.Dim(px);
            Assert.True(d >= px);
            Assert.True(d - px < (px > LayerTargetBucket.LinearCeiling ? LayerTargetBucket.HighStep : LayerTargetBucket.LinearStep) || d == LayerTargetBucket.MinDim);
        }
    }

    [Fact]
    public void IdleWindowsAndFreeCapDependOnTheAdapterTier()
    {
        Assert.True(LayerTargetTrim.FreeScratchCapBytes(true) < LayerTargetTrim.FreeScratchCapBytes(false));
        Assert.False(LayerTargetTrim.IsIdleFor(1_000 + LayerTargetTrim.IdleMs(true) - 1, 1_000, true));
        Assert.True(LayerTargetTrim.IsIdleFor(1_000 + LayerTargetTrim.IdleMs(true), 1_000, true));
        Assert.False(LayerTargetTrim.IsIdleFor(1_000 + LayerTargetTrim.IdleMs(true), 1_000, false));
    }

    [Fact]
    public void StaleTileRuleNeverTouchesTheLastTurnsSet()
    {
        Assert.False(SliceTable.IsStale(100_000, 0, SliceTable.StaleTileMs, requestedLastTurn: true));
        Assert.False(SliceTable.IsStale(SliceTable.StaleTileMs - 1, 0, SliceTable.StaleTileMs, false));
        Assert.True(SliceTable.IsStale(SliceTable.StaleTileMs, 0, SliceTable.StaleTileMs, false));
    }

    [Fact]
    public void EmptyTableHasNothingToEvictOrTrim()
    {
        var t = new SliceTable(4, 8, 8);
        Assert.Equal(0, t.EvictStale(long.MaxValue));
        Assert.True(t.TrimFreeSlotsNow().IsEmpty);
    }
}
