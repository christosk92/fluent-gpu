using System;
using System.IO;
using System.Threading;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class AudioBodyDiskCacheTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "wavee-body-cache-" + Guid.NewGuid());

    [Fact]
    public void WriteThenRead_RoundTripsChunk()
    {
        var dir = TempDir();
        var cache = new AudioBodyDiskCache(dir);
        var data = A.Bytes(3, AudioBodyDiskCache.ChunkBytes);
        cache.WriteChunk("fileA", 0, data);
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.True(cache.TryReadChunk("fileA", 0, buf, out int len));
        Assert.Equal(data.Length, len);
        Assert.Equal(data, buf.AsSpan(0, len).ToArray());
        cache.ClearAll();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SparseChunks_MissesGap()
    {
        var dir = TempDir();
        var cache = new AudioBodyDiskCache(dir);
        cache.WriteChunk("f", 0, A.Bytes(1, 100));
        cache.WriteChunk("f", 2, A.Bytes(2, 100));
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.True(cache.TryReadChunk("f", 0, buf, out _));
        Assert.False(cache.TryReadChunk("f", 1, buf, out _));
        Assert.True(cache.TryReadChunk("f", 2, buf, out _));
        cache.ClearAll();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TornWrite_DataWithoutMapBit_IsMiss()
    {
        var dir = TempDir();
        var cache = new AudioBodyDiskCache(dir);
        string enc = Path.Combine(dir, "torn.enc");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(enc, A.Bytes(9, AudioBodyDiskCache.ChunkBytes));
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.False(cache.TryReadChunk("torn", 0, buf, out _));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SetSize_PersistsAcrossInstances()
    {
        var dir = TempDir();
        new AudioBodyDiskCache(dir).SetSize("sz", 1_234_567);
        Assert.Equal(1_234_567, new AudioBodyDiskCache(dir).KnownSize("sz"));
        Directory.Delete(dir, true);
    }
}
