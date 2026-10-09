using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary><see cref="DecodeScheduler.Prioritize"/> raced a worker's claim: it read the queued request, the worker
/// claimed it, and the indexer write put it back with a promoted lane copy — so a second worker claimed and decoded the
/// same image again and ImageCache counted its bytes twice.</summary>
public sealed class DecodePrioritizeRaceTests
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
    public void PrioritizeRacingAClaim_DoesNotResurrectTheClaimedRequest()
    {
        // NO workers: this test IS the worker, and it claims from INSIDE Prioritize's read/write window
        // (PrioritizeBarrier) — the interleaving a scroll's prefetch promotion hits against a live worker.
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1 }, startWorkers: false);
        Assert.True(scheduler.Begin(7, "cover", 64, 64, ImagePriority.Prefetch));

        bool firstClaimed = false;
        int firstId = 0;
        scheduler.PrioritizeBarrier = () =>
        {
            scheduler.PrioritizeBarrier = null;   // once
            firstClaimed = scheduler.TryClaimForTest(out firstId);
        };
        scheduler.Prioritize(7, ImagePriority.Visible);

        Assert.True(firstClaimed);
        Assert.Equal(7, firstId);
        // The claimed request stays claimed: no resurrected descriptor and no promoted lane copy a second worker could
        // claim (one image decoded twice, two ok completions in ImageCache).
        Assert.Equal(0, scheduler.RequestCount);
        Assert.False(scheduler.TryClaimForTest(out _));
        Assert.Equal(0, scheduler.QueueDepth);   // one decrement per Begin, never driven negative
    }

    [Fact]
    public void Prioritize_StillPromotesAQueuedRequest()
    {
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1 }, startWorkers: false);
        Assert.True(scheduler.Begin(3, "prefetch", 64, 64, ImagePriority.Prefetch));
        Assert.True(scheduler.Begin(4, "promoted", 64, 64, ImagePriority.Prefetch));
        scheduler.Prioritize(4, ImagePriority.Visible);

        Assert.True(scheduler.TryClaimForTest(out int first));
        Assert.Equal(4, first);                           // the promoted Visible copy is claimed ahead of the older prefetch
        Assert.True(scheduler.TryClaimForTest(out int second));
        Assert.Equal(3, second);
        Assert.False(scheduler.TryClaimForTest(out _));   // 4's stale Prefetch copy is skipped (claim dedup)
        Assert.Equal(0, scheduler.QueueDepth);
    }
}
