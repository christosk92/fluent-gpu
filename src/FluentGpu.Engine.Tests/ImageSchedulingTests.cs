using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class ImageSchedulingTests
{
    private sealed class Codec : IImageCodec
    {
        public bool DecodeConstrained(ReadOnlySpan<byte> encoded, int targetW, int targetH,
            Span<byte> destinationBgra8, out int decodedW, out int decodedH)
        {
            decodedW = targetW;
            decodedH = targetH;
            destinationBgra8[..(targetW * targetH * 4)].Fill(0xff);
            return true;
        }
    }

    private sealed class Fetcher : IImageFetcher
    {
        public Task<FetchResult> FetchAsync(string source, CancellationToken ct)
            => Task.FromResult(FetchResult.Pooled(ArrayPool<byte>.Shared.Rent(16), 16));
    }

    [Fact]
    public async Task DecodeCompletion_WakesHost_AndByteBudgetSpreadsUploads()
    {
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 3 });
        int wakes = 0;
        scheduler.SetCompletionWake(() => Interlocked.Increment(ref wakes));
        for (int id = 1; id <= 3; id++) Assert.True(scheduler.Begin(id, $"image-{id}", 512, 512));

        await WaitForAsync(() => scheduler.QueueDepth == 0 && scheduler.RequestCount == 0
            && scheduler.Inflight == 0 && scheduler.HasReadyCompletions);
        Assert.True(Volatile.Read(ref wakes) >= 3);

        int applied = 0;
        scheduler.Pump((_, ok, _, _, _, _) => { if (ok) applied++; }, static (_, _, _, _) => { });
        Assert.Equal(2, applied);                 // 2 x 1 MiB reaches the normal 2 MiB frame budget
        Assert.True(scheduler.HasReadyCompletions);

        scheduler.Pump((_, ok, _, _, _, _) => { if (ok) applied++; }, static (_, _, _, _) => { });
        Assert.Equal(3, applied);
        Assert.False(scheduler.HasReadyCompletions);
    }

    [Fact]
    public async Task ScrollBudget_StillAdmitsOneOversizedCompletion()
    {
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 2 }) { ScrollThrottled = true };
        Assert.True(scheduler.Begin(1, "one", 512, 512));
        Assert.True(scheduler.Begin(2, "two", 512, 512));
        await WaitForAsync(() => scheduler.QueueDepth == 0 && scheduler.RequestCount == 0
            && scheduler.Inflight == 0 && scheduler.HasReadyCompletions);

        int applied = 0;
        scheduler.Pump((_, ok, _, _, _, _) => { if (ok) applied++; }, static (_, _, _, _) => { });
        Assert.Equal(1, applied);                 // each item is >512 KiB, but the head always makes progress
        Assert.True(scheduler.HasReadyCompletions);
    }

    [Fact]
    public void BakedBlurQueue_ThrottlesAndAdaptsFutureJobs()
    {
        var queue = new BakedBlurQueue();
        for (int id = 1; id <= 7; id++) queue.Enqueue(new BakedBlurQueue.Job(id, 99, 256, 128, 13f, 1));

        Assert.True(queue.TryDequeueRunnableJob(out var first));
        Assert.Equal((64, 32), (first.OutputW, first.OutputH)); // backlog >=6 forces Minimal
        Assert.False(queue.TryDequeueRunnableJob(out _));      // no second job inside the 33 ms window

        var feedback = new BakedBlurQueue();
        Assert.Equal(BakedBlurQueue.Quality.Economy, feedback.AdaptiveQuality);
        for (int i = 0; i < 8; i++) feedback.ReportGpuTime(0.5);
        Assert.Equal(BakedBlurQueue.Quality.High, feedback.AdaptiveQuality);
        feedback.ReportGpuTime(2.1);
        Assert.Equal(BakedBlurQueue.Quality.Economy, feedback.AdaptiveQuality);
    }

    [Fact]
    public void BakedBlurQueue_DeduplicatesGenerations_AndPreservesHighUpgradeIntent()
    {
        var queue = new BakedBlurQueue();
        var stale = new BakedBlurQueue.Job(7, 99, 256, 128, 13f, 1);
        queue.Enqueue(stale);
        queue.Enqueue(stale);                         // duplicate producer notification
        queue.Invalidate(7, 2);
        queue.Enqueue(stale);                         // older than the invalidation: ignored
        queue.Enqueue(stale with { Generation = 2 });

        Assert.True(queue.TryDequeueJob(out var current));
        Assert.Equal(2, current.Generation);
        Assert.False(queue.TryDequeueJob(out _));

        queue.Enqueue(current with { IsUpgrade = true, Quality = BakedBlurQueue.Quality.Minimal });
        Assert.True(queue.TryDequeueJob(out var upgrade));
        Assert.True(upgrade.IsUpgrade);
        Assert.Equal(BakedBlurQueue.Quality.High, upgrade.Quality);
        Assert.Equal((256, 128, 13f), (upgrade.OutputW, upgrade.OutputH, upgrade.SigmaTexels));
    }

    [Fact]
    public void BakedBlur_ProvisionalVisibleResult_UpgradesInPlaceWithoutDoubleAccounting()
    {
        var queue = new BakedBlurQueue();
        var cache = new ImageCache(new FakeImageDecoder());
        cache.SetBakedBlurQueue(queue);
        var source = cache.Request("source", 512, 256);
        cache.Pump();

        var spec = new BakedBlurSpec(26f, 0.5f);
        var derived = cache.RequestBakedBlur(source, 512, 256, in spec);
        cache.Pin(derived);
        Assert.True(queue.TryDequeueJob(out var initial));
        queue.Post(new BakedBlurQueue.Result(initial.Id, initial.Generation, true, 128, 64,
            BakedBlurQueue.Quality.Economy));
        cache.Pump();

        Assert.Equal(ImageState.Ready, cache.StateOf(derived));
        Assert.Equal((128, 64), cache.SizeOf(derived));
        Assert.Equal(128 * 64 * 4, cache.DerivedUsedBytes);
        Assert.True(queue.TryDequeueJob(out var upgrade));
        Assert.True(upgrade.IsUpgrade);
        Assert.Equal(BakedBlurQueue.Quality.High, upgrade.Quality);
        Assert.Equal((256, 128), (upgrade.OutputW, upgrade.OutputH));

        queue.Post(new BakedBlurQueue.Result(upgrade.Id, upgrade.Generation, true,
            upgrade.OutputW, upgrade.OutputH, upgrade.Quality, IsUpgrade: true));
        cache.Pump();

        Assert.Equal(ImageState.Ready, cache.StateOf(derived));
        Assert.Equal((256, 128), cache.SizeOf(derived));
        Assert.Equal(256 * 128 * 4, cache.DerivedUsedBytes);
        Assert.Equal(256 * 128 * 4 + 512 * 256 * 4, cache.UsedBytes);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(2, timeout.Token);
    }
}
