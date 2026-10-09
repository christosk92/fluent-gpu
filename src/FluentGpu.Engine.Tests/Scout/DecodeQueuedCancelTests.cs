using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary><see cref="DecodeScheduler.Cancel"/> on a still-queued request removed it without paying back Begin's
/// <c>_queued</c> increment, so every row recycled before a worker claimed its cover leaked one slot until the
/// backpressure gate refused every Overscan/Prefetch decode for the rest of the session.</summary>
public sealed class DecodeQueuedCancelTests
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
    public void CancelBeforeClaim_FreesTheBackpressureSlot()
    {
        // NO workers: nothing claims, so every cancel below is a cancel-before-claim (a fling's recycled overscan rows).
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1, QueueCapacity = 4 }, startWorkers: false);
        for (int id = 1; id <= 4; id++) Assert.True(scheduler.Begin(id, "cover" + id, 64, 64, ImagePriority.Overscan));
        Assert.Equal(4, scheduler.QueueDepth);
        for (int id = 1; id <= 4; id++) scheduler.Cancel(id);

        Assert.Equal(0, scheduler.RequestCount);
        Assert.Equal(0, scheduler.QueueDepth);
        // The capacity is free again: an off-screen request is accepted, not dropped.
        Assert.True(scheduler.Begin(5, "cover5", 64, 64, ImagePriority.Overscan));
        Assert.True(scheduler.Begin(6, "cover6", 64, 64, ImagePriority.Prefetch));

        // The stale lane entries of 1..4 are skipped without driving the count negative; 5 and 6 still claim.
        Assert.True(scheduler.TryClaimForTest(out int first));
        Assert.Equal(5, first);
        Assert.True(scheduler.TryClaimForTest(out int second));
        Assert.Equal(6, second);
        Assert.False(scheduler.TryClaimForTest(out _));
        Assert.Equal(0, scheduler.QueueDepth);
    }

    [Fact]
    public void CancelAfterClaim_DoesNotDecrementTwice()
    {
        using var scheduler = new DecodeScheduler(new Codec(), new Fetcher(),
            new DecodeOptions { MaxConcurrency = 1 }, startWorkers: false);
        Assert.True(scheduler.Begin(1, "claimed", 64, 64, ImagePriority.Overscan));
        Assert.True(scheduler.Begin(2, "queued", 64, 64, ImagePriority.Overscan));
        Assert.True(scheduler.TryClaimForTest(out int claimed));
        Assert.Equal(1, claimed);
        scheduler.Cancel(1);   // claimed: tombstones, the claim already paid its slot back
        Assert.Equal(1, scheduler.QueueDepth);
        scheduler.Cancel(2);   // queued: pays its own slot back
        Assert.Equal(0, scheduler.QueueDepth);
        Assert.False(scheduler.TryClaimForTest(out _));
        Assert.Equal(0, scheduler.QueueDepth);
    }
}
