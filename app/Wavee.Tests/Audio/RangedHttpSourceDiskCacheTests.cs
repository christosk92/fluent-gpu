using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class RangedHttpSourceDiskCacheTests
{
    [Fact]
    public void SecondStream_ServesFromDisk_ZeroAdditionalHttp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-disk-" + Guid.NewGuid());
        var disk = new AudioBodyDiskCache(dir, budgetBytes: 64 << 20);
        int calls = 0;
        const long size = 200_000;
        const string fileId = "deadbeef";
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ =>
            {
                Interlocked.Increment(ref calls);
                var body = A.Bytes((byte)calls, (int)size);
                return (HttpStatusCode.PartialContent, body);
            }
        };
        using var http = new HttpClient(handler);

        using (var src1 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src1.Configure(["http://cdn.test/track"], size);
            src1.EnsureRange(0, AudioBodyDiskCache.ChunkBytes);
        }

        int afterFirst = Volatile.Read(ref calls);
        Assert.True(afterFirst > 0);

        using (var src2 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src2.Configure(["http://cdn.test/track"], size);
            src2.EnsureRange(0, AudioBodyDiskCache.ChunkBytes);
        }

        Assert.Equal(afterFirst, Volatile.Read(ref calls));

        disk.ClearAll();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PartialCoverage_FetchesOnlyGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-gap-" + Guid.NewGuid());
        var disk = new AudioBodyDiskCache(dir, budgetBytes: 64 << 20);
        int calls = 0;
        const long size = AudioBodyDiskCache.ChunkBytes * 3;
        const string fileId = "partial";
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ =>
            {
                Interlocked.Increment(ref calls);
                return (HttpStatusCode.PartialContent, A.Bytes(7, (int)size));
            }
        };
        using var http = new HttpClient(handler);

        using (var src1 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src1.Configure(["http://cdn.test/track"], size);
            src1.EnsureRange(AudioBodyDiskCache.ChunkBytes, AudioBodyDiskCache.ChunkBytes);
        }

        int afterPartial = Volatile.Read(ref calls);
        Assert.True(afterPartial > 0);

        using (var src2 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src2.Configure(["http://cdn.test/track"], size);
            src2.EnsureRange(0, AudioBodyDiskCache.ChunkBytes * 3);
        }

        Assert.True(Volatile.Read(ref calls) > afterPartial);

        disk.ClearAll();
        Directory.Delete(dir, true);
    }
}
