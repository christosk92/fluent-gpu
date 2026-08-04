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
    public async Task ScrollBudget_AdmitsExactlyOneOversizedCompletionPerFrame()
    {
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 2 }) { ScrollThrottled = true };
        Assert.True(scheduler.Begin(1, "one", 512, 512));
        Assert.True(scheduler.Begin(2, "two", 512, 512));
        await WaitForAsync(() => scheduler.QueueDepth == 0 && scheduler.RequestCount == 0
            && scheduler.Inflight == 0 && scheduler.HasReadyCompletions);

        int applied = 0;
        scheduler.Pump((_, ok, _, _, _, _) => { if (ok) applied++; }, static (_, _, _, _) => { });
        Assert.Equal(1, applied);                 // each item is 1 MiB (> the 512 KiB scroll budget): the head still lands
        Assert.True(scheduler.HasReadyCompletions);

        // …and the NEXT frame takes the next one. A cover that cannot land until the gesture ends is a BlurHash smear
        // for the whole scroll; one 1 MiB upload per frame is the paced alternative.
        scheduler.Pump((_, ok, _, _, _, _) => { if (ok) applied++; }, static (_, _, _, _) => { });
        Assert.Equal(2, applied);
        Assert.False(scheduler.HasReadyCompletions);
    }

    [Fact]
    public async Task ScrollPump_PreservesCompletionOrderAcrossSizeLanes()
    {
        // ONE worker ⇒ the two decodes complete in request order, so the 16 KiB completion is strictly OLDER than the
        // 1 MiB one. The size lanes exist to classify, never to reorder: a scroll-throttled pump must still drain them
        // oldest-first.
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1 }) { ScrollThrottled = true };
        Assert.True(scheduler.Begin(1, "small", 64, 64));      // 16 KiB → the small lane
        await WaitForAsync(() => scheduler.HasReadyCompletions);
        Assert.True(scheduler.Begin(2, "large", 512, 512));    // 1 MiB → the large lane
        await WaitForAsync(() => scheduler.QueueDepth == 0 && scheduler.RequestCount == 0 && scheduler.Inflight == 0);

        int first = 0, second = 0;
        scheduler.Pump((id, ok, _, _, _, _) => { if (ok && first == 0) first = id; }, static (_, _, _, _) => { });
        scheduler.Pump((id, ok, _, _, _, _) => { if (ok && second == 0) second = id; }, static (_, _, _, _) => { });
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.False(scheduler.HasReadyCompletions);
    }

    [Fact]
    public void CancelRacingTheClaim_IsObservable_NotLostInTheRegistrationGap()
    {
        // NO workers: this test IS the worker, so the claim/cancel interleaving is exact — no sleeps, no timing.
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1 }, startWorkers: false);
        Assert.True(scheduler.Begin(7, "cover", 64, 64));
        Assert.True(scheduler.TryClaimForTest(out int claimed));
        Assert.Equal(7, claimed);

        // The id has left _reqs. In the OLD shape it was not yet in _activeIds either (WorkerLoop registered it only
        // after TryClaim returned), so a Cancel arriving here found it in NEITHER map and was dropped silently — the
        // decode then published pixels for an already-recycled row. TryClaim now registers BEFORE claiming, so the
        // cancel must be observable: either as a queued Canceled control completion or as a live tombstone.
        scheduler.Cancel(7);
        Assert.True(scheduler.CanceledPending > 0 || scheduler.HasReadyCompletions,
            "a Cancel inside the claim window must not vanish");
        Assert.Equal(1, scheduler.CanceledPending);
    }

    [Fact]
    public async Task CancelFromInsideTheClaimWindow_SuppressesTheDecode()
    {
        // One worker; the cancel is fired FROM the worker thread inside TryClaim's claim window (the ClaimBarrier
        // hook), which is the exact interleaving a sleep-based race test can never reach reliably.
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1 });
        scheduler.ClaimBarrier = () => { scheduler.ClaimBarrier = null; scheduler.Cancel(11); };   // once
        Assert.True(scheduler.Begin(11, "cover", 64, 64));

        await WaitForAsync(() => scheduler.QueueDepth == 0 && scheduler.RequestCount == 0
            && scheduler.Inflight == 0 && scheduler.HasReadyCompletions);

        int canceled = 0, pixels = 0;
        scheduler.Pump((_, ok, _, _, failure, _) => { if (!ok && failure == ImageFailureKind.Canceled) canceled++; },
            (_, _, _, _) => pixels++);
        Assert.Equal(1, canceled);
        Assert.Equal(0, pixels);                      // nothing published for the recycled row
        Assert.Equal(0, scheduler.CanceledPending);   // the tombstone is reclaimed by Pump — bounded, per the contract
    }

    [Fact]
    public void ScrollReveal_IsHalfLength_ExceptForACacheAdjacentLanding()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        float dur = ImageTransition.Default.DurationMs;

        // At rest: the full authored fade.
        var rest = cache.Request("rest", 64, 64);
        cache.Tick(500f);
        cache.Pump();
        Assert.Equal(0f, cache.CrossFadeOf(rest));
        cache.Tick(dur * 0.5f);
        Assert.True(cache.CrossFadeOf(rest) < 1f);
        cache.Tick(dur * 0.5f);
        Assert.Equal(1f, cache.CrossFadeOf(rest));

        // Mid-scroll, landing long after its request: a HALF-length fade — not the old instant pop.
        cache.SuppressReveals = true;
        var slow = cache.Request("slow", 64, 64);
        cache.Tick(500f);
        cache.Pump();
        Assert.Equal(0f, cache.CrossFadeOf(slow));
        cache.Tick(dur * 0.5f);
        Assert.Equal(1f, cache.CrossFadeOf(slow));

        // Mid-scroll, cache-adjacent (landed within 100ms of the request): still instant — fading a hit reads as lag.
        var fast = cache.Request("fast", 64, 64);
        cache.Tick(16f);
        cache.Pump();
        Assert.Equal(1f, cache.CrossFadeOf(fast));
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
