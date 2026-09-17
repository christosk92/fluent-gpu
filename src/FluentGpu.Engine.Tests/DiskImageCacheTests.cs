using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="DiskImageCache"/> occupancy and eviction: the directory is seeded once (sum of sizes), a trim evicts
/// oldest-first with 90 % hysteresis and a per-pass cap, and a hit records recency in memory instead of writing the
/// file's last-access time. Every test owns a fresh temp directory. Budgets that could schedule a BACKGROUND trim
/// are avoided; eviction is driven through the explicit <c>TrimPass</c> seam so each assertion is deterministic.
/// </summary>
public sealed class DiskImageCacheTests : IDisposable
{
    private const long Unbounded = 1L << 40;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fluent-gpu-tests", "imgcache-" + Guid.NewGuid().ToString("n"));

    public DiskImageCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A PNG signature + IHDR tag padded to <paramref name="size"/> — enough for <c>LooksLikeImage</c>.</summary>
    private static byte[] Png(int size)
    {
        var b = new byte[size];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        return b;
    }

    /// <summary>Writes <paramref name="count"/> files of <paramref name="size"/> bytes named f00.., f00 carrying the oldest write time.</summary>
    private List<string> Seed(int count, int size, DateTime oldest)
    {
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string name = "f" + i.ToString("00");
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, oldest.AddMinutes(i));
            names.Add(name);
        }
        return names;
    }

    private string[] Remaining() => Directory.EnumerateFiles(_dir).Select(p => Path.GetFileName(p)).Order().ToArray();

    private void AssertRemaining(IEnumerable<string> expected) => Assert.Equal(expected.Order().ToArray(), Remaining());

    [Fact]
    public async Task SeedingSumsTheExistingDirectoryAndWritesAddOnTop()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "b"), new byte[200]);
        File.WriteAllBytes(Path.Combine(_dir, "c"), new byte[300]);

        var cache = new DiskImageCache(_dir, budgetBytes: Unbounded, maxObjects: int.MaxValue);
        await cache.SeedAsync();
        Assert.Equal(600L, cache.ApproxBytes);
        Assert.Equal(3L, cache.ApproxCount);

        await cache.WriteAsync("https://cdn/x.png", Png(1000), TestContext.Current.CancellationToken);
        Assert.Equal(1600L, cache.ApproxBytes);
        Assert.Equal(4L, cache.ApproxCount);
        Assert.True(File.Exists(cache.PathFor("https://cdn/x.png")));
    }

    [Fact]
    public void TrimEvictsOldestFirstAndStopsAtNinetyPercentOfTheBudget()
    {
        // 20 × 1000 B against a 10 000 B budget: the pass evicts 11 (down to 9 000 = 90 %), the 11 oldest.
        var names = Seed(20, 1000, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cache = new DiskImageCache(_dir, budgetBytes: 10_000, maxObjects: 1000, maxEvictionsPerTrim: 1000);

        int evicted = cache.TrimPass();

        Assert.Equal(11, evicted);
        AssertRemaining(names.Skip(11));
        Assert.Equal(9_000L, cache.ApproxBytes);
        Assert.Equal(9L, cache.ApproxCount);
        Assert.Equal(0, cache.TrimPass());   // under budget: nothing more to do
    }

    [Fact]
    public void TrimEvictsByObjectCountToo()
    {
        var names = Seed(20, 10, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cache = new DiskImageCache(_dir, budgetBytes: Unbounded, maxObjects: 10, maxEvictionsPerTrim: 1000);

        int evicted = cache.TrimPass();

        Assert.Equal(11, evicted);   // down to 9 = 90 % of 10
        AssertRemaining(names.Skip(11));
    }

    [Fact]
    public void TrimIsBoundedPerPassAndResumesWhereItStopped()
    {
        var names = Seed(20, 1000, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cache = new DiskImageCache(_dir, budgetBytes: 10_000, maxObjects: 1000, maxEvictionsPerTrim: 4);

        Assert.Equal(4, cache.TrimPass());
        AssertRemaining(names.Skip(4));   // exactly the four oldest went

        Assert.Equal(4, cache.TrimPass());
        Assert.Equal(3, cache.TrimPass());   // 12 000 → 9 000 needs three, not a full cap
        Assert.Equal(0, cache.TrimPass());
        AssertRemaining(names.Skip(11));
    }

    [Fact]
    public async Task HitDoesNotWriteLastAccessTimeAndCountsAsRecencyForTrim()
    {
        var ct = TestContext.Current.CancellationToken;
        const string a = "https://cdn/a.png", b = "https://cdn/b.png";
        var writer = new DiskImageCache(_dir, budgetBytes: Unbounded, maxObjects: int.MaxValue, maxAge: TimeSpan.FromDays(36500));
        await writer.WriteAsync(a, Png(1000), ct);
        await writer.WriteAsync(b, Png(1000), ct);

        string pathA = writer.PathFor(a), pathB = writer.PathFor(b);
        // On disk A is the OLDER write and B the newer. A far-future access stamp survives only if nobody overwrites it
        // (a filesystem's own access-time maintenance never moves a stamp backwards).
        File.SetLastWriteTimeUtc(pathA, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(pathB, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var future = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(pathA, future);

        // A fresh instance starts with an empty touch map: only the hit below makes A recent.
        var cache = new DiskImageCache(_dir, budgetBytes: Unbounded, maxObjects: int.MaxValue, maxAge: TimeSpan.FromDays(36500), maxEvictionsPerTrim: 1000);
        var hit = await cache.TryReadAsync(a, ct);
        Assert.True(hit.Ok);
        Assert.Equal(1000, hit.Length);
        ArrayPool<byte>.Shared.Return(hit.Buffer!);
        await cache.SeedAsync();

        Assert.Equal(future, File.GetLastAccessTimeUtc(pathA));   // the hit did not touch file metadata

        // 2 000 B over a 1 500 B budget → one eviction. A was hit this session; B falls back to its 2020 write time.
        Assert.Equal(1, cache.TrimPass(budgetBytes: 1500, maxObjects: 100));
        Assert.True(File.Exists(pathA));
        Assert.False(File.Exists(pathB));
    }

    [Fact]
    public async Task StaleEntryIsDeletedOnReadAndLeavesTheBookkeeping()
    {
        var ct = TestContext.Current.CancellationToken;
        const string url = "https://cdn/stale.png";
        var cache = new DiskImageCache(_dir, budgetBytes: Unbounded, maxObjects: int.MaxValue, maxAge: TimeSpan.FromDays(1));
        await cache.SeedAsync();   // seed the empty directory first so the write below is counted exactly once
        await cache.WriteAsync(url, Png(500), ct);
        string path = cache.PathFor(url);
        Assert.Equal(1L, cache.ApproxCount);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

        var miss = await cache.TryReadAsync(url, ct);

        Assert.False(miss.Ok);
        Assert.False(File.Exists(path));
        Assert.Equal(0L, cache.ApproxCount);
        Assert.Equal(0L, cache.ApproxBytes);
    }
}
