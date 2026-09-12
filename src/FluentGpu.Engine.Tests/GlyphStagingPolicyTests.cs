using System;
using FluentGpu.Text;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class GlyphStagingPolicyTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(600)]
    public void ColdReserveSurvivesUntilWholeIdleWindow(int threshold)
    {
        Assert.Equal(1024, GlyphStagingPolicy.AfterIdle(1024, threshold - 1, threshold));
        Assert.Equal(256, GlyphStagingPolicy.AfterIdle(1024, threshold, threshold));
        Assert.Equal(9 * 1024 * 1024, 3 * 4096 * (1024 - 256));
        Assert.Equal(256, GlyphStagingPolicy.AfterIdle(256, threshold, threshold));
    }

    [Fact]
    public void ScrollBurstsDoNotRepeatedlyShrinkAnActiveReserve()
    {
        int rows = GlyphStagingPolicy.ForDemand(256, 700);
        for (int burst = 0; burst < 100; burst++)
        {
            for (int clean = 0; clean < 120; clean++)
                rows = GlyphStagingPolicy.AfterIdle(rows, clean, 120);
            rows = GlyphStagingPolicy.ForDemand(rows, 700);
            Assert.Equal(1024, rows);
        }
        Assert.Equal(256, GlyphStagingPolicy.AfterIdle(rows, 120, 120));
        Assert.Equal(2048, GlyphStagingPolicy.ForDemand(256, 10000));
    }

    [Fact]
    public void HistoricColdUploadDoesNotRegrowSmallWarmUploads()
    {
        var atlas = new GlyphAtlasStore(2048);
        var cold = new byte[2046 * 600];
        Array.Fill(cold, (byte)73);
        atlas.BeginFrame();
        Assert.True(atlas.TryPack(cold, 2046, 600, out _, out _));
        Assert.True(atlas.TryTakeUpload(1024, out _));
        Assert.True(atlas.WantedStagingRows > 256);
        var small = new byte[16 * 16];
        Array.Fill(small, (byte)121);
        for (int frame = 0; frame < 100; frame++)
        {
            atlas.BeginFrame();
            Assert.Equal(0, atlas.WantedStagingRows);
            Assert.True(atlas.TryPack(small, 16, 16, out _, out _));
            Assert.True(atlas.TryTakeUpload(256, out _));
            Assert.Equal(256, GlyphStagingPolicy.ForDemand(256, atlas.WantedStagingRows));
            Assert.Equal(0, atlas.ShortfallRows);
        }
    }

    [Fact]
    public void WarmOverflowRetainsAndDrainsEveryPixelAfterGrowth()
    {
        var atlas = new GlyphAtlasStore(1024);
        var pixels = new byte[1022 * 700];
        Array.Fill(pixels, (byte)197);
        var gpu = new byte[1024 * 1024];
        int rows = GlyphStagingPolicy.WarmRows;
        atlas.BeginFrame();
        Assert.True(atlas.TryPack(pixels, 1022, 700, out _, out _));
        for (int frame = 0; frame < 3 && atlas.IsDirty; frame++)
        {
            Assert.True(atlas.TryTakeUpload(rows, out var flush));
            var staging = new byte[rows * atlas.Size];
            atlas.StageInto(in flush, staging);
            staging.AsSpan(flush.CopyOffset, flush.CopyRowCount * atlas.Size)
                .CopyTo(gpu.AsSpan(flush.CopyRowStart * atlas.Size));
            rows = GlyphStagingPolicy.ForDemand(rows, atlas.WantedStagingRows);
            if (frame == 0) Assert.True(atlas.ShortfallRows > 0);
            atlas.BeginFrame();
        }
        Assert.False(atlas.IsDirty);
        Assert.True(atlas.Texels.SequenceEqual(gpu));
        Assert.Equal(0, atlas.BandRebases);
    }
}
