using System;
using System.IO;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The persistent cover-color cache: a resolved palette (or a known-colorless result) survives restarts and is good for
// ~half a year, so fetchExtractedColors runs once per image ever. Null/transient results are deliberately NOT cached:
// the API cannot distinguish colorless art from transport/auth/schema failure. Verifies round-trip persistence,
// positive TTL expiry, and that different pre-sized URLs for the same cover share one key.
public class ExtractedColorCacheTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "wavee-color-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void RoundTrips_PaletteThroughDisk()
    {
        var path = TempFile();
        try
        {
            var pal = new Palette(0xFF112233u, 0xFF445566u, 0xFFFFFFFFu, 0xFF778899u);
            var a = new ExtractedColorCache(path);
            a.Set("img1", pal);
            a.Flush();

            var b = new ExtractedColorCache(path);   // fresh instance reads the persisted file
            Assert.True(b.TryGet("img1", out var got));
            Assert.NotNull(got);
            Assert.Equal(pal, got);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NullResult_RemainsAMiss_SoTheNextReadRetries()
    {
        var path = TempFile();
        try
        {
            var c = new ExtractedColorCache(path);
            c.Set("colorless", null);
            Assert.False(c.TryGet("colorless", out var got));
            Assert.Null(got);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExpiresHitAfter180Days_AndNeverPersistsMisses()
    {
        var path = TempFile();
        try
        {
            long now = 1_000_000_000;
            var pal = new Palette(1, 2, 3, 4);
            var write = new ExtractedColorCache(path, nowUnix: () => now);
            write.Set("img", pal);
            write.Set("none", null);
            write.Flush();

            // 179 days later: palette still fresh; null was never persisted.
            long later = now + (long)TimeSpan.FromDays(179).TotalSeconds;
            var read = new ExtractedColorCache(path, nowUnix: () => later);
            Assert.True(read.TryGet("img", out var p) && p is not null);
            Assert.False(read.TryGet("none", out _));   // negative TTL (14d) elapsed → miss → caller refetches

            // 181 days later: palette expired too.
            long muchLater = now + (long)TimeSpan.FromDays(181).TotalSeconds;
            var read2 = new ExtractedColorCache(path, nowUnix: () => muchLater);
            Assert.False(read2.TryGet("img", out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://i.scdn.co/image/ab67706c0000abcd", "ab67706c0000abcd")]
    [InlineData("https://i.scdn.co/image/AB67706C0000ABCD?size=640", "ab67706c0000abcd")]
    [InlineData("https://mosaic.scdn.co/640/deadbeef", "deadbeef")]
    public void KeyForUrl_SharesOneEntryAcrossPreSizedUrls(string url, string expectedKey)
        => Assert.Equal(expectedKey, ExtractedColorCache.KeyForUrl(url));
}
