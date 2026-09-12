using System;
using FluentGpu.Text;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class GlyphAtlasGrowthTests
{
    [Fact]
    public void RejectedWrapDoesNotConsumeRemainingShelfSpace()
    {
        var atlas = new GlyphAtlasStore(16);
        Assert.True(atlas.TryPack(new byte[8 * 10], 8, 10, out _, out _));
        int row = atlas.ShelfRow;
        Assert.False(atlas.CanPack(8, 10));
        Assert.False(atlas.TryPack(new byte[80], 8, 10, out int x, out int y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(row, atlas.ShelfRow);
        Assert.True(atlas.TryPack(new byte[3 * 2], 3, 2, out x, out y));
        Assert.Equal(10, x);
        Assert.Equal(1, y);
        Assert.Equal(12, atlas.OccupiedRowCount);
    }

    [Fact]
    public void InvalidCoverageAndOversizedDimensionsLeaveStateUntouched()
    {
        var atlas = new GlyphAtlasStore(16);
        Assert.False(atlas.TryPack(Array.Empty<byte>(), int.MaxValue, 1, out _, out _));
        Assert.False(atlas.CanPack(1, int.MaxValue));
        Assert.Throws<ArgumentException>(() => atlas.TryPack(new byte[1], 2, 2, out _, out _));
        Assert.False(atlas.IsDirty);
        Assert.Equal(0, atlas.OccupiedRowCount);
        Assert.Equal(0, atlas.NonZeroTexels);
        Assert.True(atlas.TryPack(new byte[4], 2, 2, out int x, out int y));
        Assert.Equal(1, x);
        Assert.Equal(1, y);
    }

    [Fact]
    public void DetachedGrowthPreservesCoordinatesAndEverySubpixelRow()
    {
        var old = new GlyphAtlasStore(16);
        old.Reset();
        byte[] phases = new byte[3 * 8];
        for (int i = 0; i < phases.Length; i++) phases[i] = (byte)(i + 1);
        Assert.True(old.TryPack(phases, 3, 8, out int x, out int y));
        byte[] oldPixels = old.Texels.ToArray();
        var expanded = old.CreateExpanded(32);
        Assert.Equal(old.Epoch + 1, expanded.Epoch);
        Assert.Equal(old.NonZeroTexels, expanded.NonZeroTexels);
        Assert.Equal(old.OccupiedRowCount, expanded.OccupiedRowCount);
        for (int row = 0; row < 16; row++)
        {
            Assert.True(old.Texels.Slice(row * 16, 16).SequenceEqual(expanded.Texels.Slice(row * 32, 16)));
            Assert.True(expanded.Texels.Slice(row * 32 + 16, 16).SequenceEqual(new byte[16]));
        }
        for (int row = 0; row < 8; row++)
            Assert.True(phases.AsSpan(row * 3, 3).SequenceEqual(expanded.Texels.Slice((y + row) * 32 + x, 3)));
        Assert.True(expanded.TryPack(new byte[20], 20, 1, out int nextX, out int nextY));
        Assert.Equal(5, nextX);
        Assert.Equal(y, nextY);
        Assert.True(old.Texels.SequenceEqual(oldPixels));
    }

    [Fact]
    public void NewGenerationUploadIncludesAllPreviouslyCleanCoverageAndAprons()
    {
        var old = new GlyphAtlasStore(16);
        byte[] pixels = new byte[8 * 5];
        Array.Fill(pixels, (byte)173);
        Assert.True(old.TryPack(pixels, 8, 5, out _, out _));
        Assert.True(old.TryTakeUpload(16, out _));
        Assert.False(old.IsDirty);
        old.BeginFrame();
        var expanded = old.CreateExpanded(32);
        Assert.True(expanded.TryTakeUpload(32, out var flush));
        Assert.Equal(0, flush.CopyRowStart);
        Assert.Equal(expanded.OccupiedRowCount, flush.CopyRowCount);
        byte[] staging = new byte[32 * 32];
        expanded.StageInto(in flush, staging);
        byte[] gpu = new byte[32 * 32];
        staging.AsSpan(flush.CopyOffset, flush.CopyBytes).CopyTo(gpu.AsSpan(flush.CopyRowStart * 32));
        Assert.True(expanded.Texels.SequenceEqual(gpu));
        Assert.Equal(0, expanded.ShortfallRows);
        Assert.Equal(0, expanded.BandRebases);
    }

    [Fact]
    public void GrowthAfterUploadRecordingIsRejectedAndOldStateRemainsValid()
    {
        var atlas = new GlyphAtlasStore(16);
        Assert.True(atlas.TryPack(new byte[4], 2, 2, out _, out _));
        Assert.True(atlas.TryTakeUpload(16, out _));
        Assert.Throws<InvalidOperationException>(() => atlas.CreateExpanded(32));
        Assert.Equal(16, atlas.Size);
        Assert.Equal(0, atlas.Epoch);
        Assert.Throws<ArgumentOutOfRangeException>(() => atlas.CreateExpanded(16));
        atlas.BeginFrame();
        Assert.Equal(1, atlas.CreateExpanded(32).Epoch);
    }

    [Fact]
    public void EmptyExpandedGenerationDoesNotInventAnUpload()
    {
        var atlas = new GlyphAtlasStore(16).CreateExpanded(32);
        Assert.Equal(1, atlas.Epoch);
        Assert.Equal(0, atlas.OccupiedRowCount);
        Assert.False(atlas.IsDirty);
        Assert.False(atlas.TryTakeUpload(32, out _));
    }
}
