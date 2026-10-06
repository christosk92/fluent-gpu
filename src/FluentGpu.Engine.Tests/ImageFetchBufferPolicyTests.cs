using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="DefaultImageFetcher"/> body-buffer policy (scroll-feel 2026-09-16 W3-E1): a response that declares its
/// length is read into ONE rental from the fetcher's dedicated pool (no doubling chain, no LOH churn on the workers); a
/// chunked body still grows by doubling and hands every outgrown buffer back; the scheduler's post-decode
/// <see cref="IImageFetcher.ReturnBuffer"/> lands in the same pool and the next fetch of that size reuses the array.
/// The pool is observed through a counting wrapper injected via the internal constructor — no HTTP, no network.
/// </summary>
public sealed class ImageFetchBufferPolicyTests
{
    /// <summary>Counts rents/returns and delegates to a real dedicated pool so LIFO reuse is observable by reference.</summary>
    private sealed class CountingPool : ArrayPool<byte>
    {
        private readonly ArrayPool<byte> _inner = Create(DefaultImageFetcher.PoolMaxArrayLength, DefaultImageFetcher.PoolMaxArraysPerBucket);
        public int Rents, Returns;
        public int LastRentRequest;
        public override byte[] Rent(int minimumLength) { Rents++; LastRentRequest = minimumLength; return _inner.Rent(minimumLength); }
        public override void Return(byte[] array, bool clearArray = false) { Returns++; _inner.Return(array, clearArray); }
    }

    /// <summary>A stream that throws after <paramref name="failAfter"/> bytes — a dropped connection mid-body.</summary>
    private sealed class FailingStream : Stream
    {
        private readonly int _failAfter;
        private int _pos;
        public FailingStream(int failAfter) => _failAfter = failAfter;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _failAfter) throw new IOException("connection reset");
            int n = Math.Min(count, _failAfter - _pos);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    private static (DefaultImageFetcher fetcher, CountingPool pool) Make()
    {
        var pool = new CountingPool();
        // An explicit HttpClient keeps the fetcher from building a SocketsHttpHandler it never uses here.
        var fetcher = new DefaultImageFetcher(new System.Net.Http.HttpClient(), diskCache: null, acceptHeader: null, pool: pool);
        return (fetcher, pool);
    }

    /// <summary>Doublings the chunked path takes for <paramref name="total"/> bytes: it grows whenever the buffer is
    /// exactly full BEFORE the EOF read, so a body equal to a capacity still costs one step.</summary>
    private static int ExpectedChunkedRents(int total)
    {
        int rents = 1;
        for (int cap = DefaultImageFetcher.ChunkedInitialCapacity; cap <= total; cap *= 2) rents++;
        return rents;
    }

    [Theory]
    [InlineData(150_000L, 150_000 + DefaultImageFetcher.ContentLengthSlack)]
    [InlineData(65_536L, 65_536 + DefaultImageFetcher.ContentLengthSlack)]
    [InlineData(0L, 4096)]
    [InlineData(1L, 4096)]
    [InlineData(1L << 40, DefaultImageFetcher.MaxBufferBytes)]
    public void InitialCapacity_SizesFromContentLengthPlusSlack(long contentLength, int expected)
        => Assert.Equal(expected, DefaultImageFetcher.InitialCapacity(contentLength));

    [Fact]
    public void InitialCapacity_NoHint_StartsAtChunkedCapacity()
        => Assert.Equal(DefaultImageFetcher.ChunkedInitialCapacity, DefaultImageFetcher.InitialCapacity(null));

    [Fact]
    public async Task ContentLength_RentsExactlyOnce()
    {
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            byte[] body = Pattern(150_000);   // a typical 640px JPEG cover: past the 85 KB LOH line, under a doubling boundary
            var r = await fetcher.ReadAllPooled(new MemoryStream(body), body.Length, TestContext.Current.CancellationToken);

            Assert.True(r.Ok);
            Assert.Equal(1, pool.Rents);
            Assert.Equal(0, pool.Returns);
            Assert.Equal(body.Length + DefaultImageFetcher.ContentLengthSlack, pool.LastRentRequest);
            Assert.Equal(body.Length, r.Length);
            Assert.True(r.Buffer!.Length >= body.Length + DefaultImageFetcher.ContentLengthSlack);
            Assert.True(r.Span.SequenceEqual(body));
        }
    }

    [Fact]
    public async Task ContentLength_ExactBucketSize_StillRentsOnce()
    {
        // 65 536 bytes is exactly a pool bucket: without the slack the loop would be "full" before the EOF read and
        // double once — the case the slack exists for.
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            byte[] body = Pattern(65_536);
            var r = await fetcher.ReadAllPooled(new MemoryStream(body), body.Length, TestContext.Current.CancellationToken);

            Assert.Equal(1, pool.Rents);
            Assert.Equal(0, pool.Returns);
            Assert.Equal(body.Length, r.Length);
            Assert.True(r.Span.SequenceEqual(body));
        }
    }

    [Fact]
    public async Task Chunked_DoublesAndReturnsEveryOutgrownBuffer()
    {
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            byte[] body = Pattern(150_000);
            var r = await fetcher.ReadAllPooled(new MemoryStream(body), hint: null, TestContext.Current.CancellationToken);

            int expectedRents = ExpectedChunkedRents(body.Length);   // 64 KB → 128 KB → 256 KB
            Assert.True(expectedRents >= 2, "the fixture must exercise at least one doubling");
            Assert.Equal(expectedRents, pool.Rents);
            Assert.Equal(expectedRents - 1, pool.Returns);           // every outgrown buffer went back; the live one is out
            Assert.Equal(body.Length, r.Length);
            Assert.True(r.Span.SequenceEqual(body));
        }
    }

    [Fact]
    public async Task Chunked_SmallBody_RentsOnce()
    {
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            byte[] body = Pattern(20_000);
            var r = await fetcher.ReadAllPooled(new MemoryStream(body), hint: null, TestContext.Current.CancellationToken);

            Assert.Equal(1, pool.Rents);
            Assert.Equal(0, pool.Returns);
            Assert.True(r.Span.SequenceEqual(body));
        }
    }

    [Fact]
    public async Task ReturnBuffer_LandsInFetcherPool_AndNextFetchReusesIt()
    {
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            byte[] body = Pattern(150_000);
            var first = await fetcher.ReadAllPooled(new MemoryStream(body), body.Length, TestContext.Current.CancellationToken);

            ((IImageFetcher)fetcher).ReturnBuffer(first.Buffer!);   // what DecodeScheduler does after the decoder read it
            Assert.Equal(1, pool.Returns);

            var second = await fetcher.ReadAllPooled(new MemoryStream(body), body.Length, TestContext.Current.CancellationToken);
            Assert.Same(first.Buffer, second.Buffer);               // the dedicated pool handed the same array back: zero net allocation
            Assert.Equal(2, pool.Rents);
            Assert.True(second.Span.SequenceEqual(body));
        }
    }

    [Fact]
    public async Task MidStreamFailure_ReturnsTheRental_AndPropagates()
    {
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            await Assert.ThrowsAsync<IOException>(
                () => fetcher.ReadAllPooled(new FailingStream(failAfter: 100_000), hint: null, TestContext.Current.CancellationToken));

            Assert.True(pool.Rents >= 2);              // grew past 64 KB before the fault
            Assert.Equal(pool.Rents, pool.Returns);    // nothing leaked, including the buffer live at the throw
        }
    }

    [Fact]
    public void ReturnBuffer_AcceptsASharedPoolRental_TheDiskCacheHitShape()
    {
        // DiskImageCache serves hits from ArrayPool<byte>.Shared (same 16·2ⁿ bucket sizes); the scheduler returns them
        // through the fetcher like any other fetch buffer, so they must land in the dedicated pool, not throw.
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            byte[] fromShared = ArrayPool<byte>.Shared.Rent(70_000);
            fetcher.ReturnBuffer(fromShared);
            Assert.Equal(1, pool.Returns);
        }
    }

    [Fact]
    public void ReturnBuffer_DropsAForeignArray_NeverThrows()
    {
        var (fetcher, pool) = Make();
        using (fetcher)
        {
            fetcher.ReturnBuffer(new byte[100]);   // not a bucket size — an ArrayPool.Return here would throw on the worker
            fetcher.ReturnBuffer(Array.Empty<byte>());
            Assert.Equal(0, pool.Returns);
        }
    }

    private sealed class SharedRentingFetcher : IImageFetcher
    {
        public Task<FetchResult> FetchAsync(string source, CancellationToken ct)
            => Task.FromResult(FetchResult.Pooled(ArrayPool<byte>.Shared.Rent(16), 16));
    }

    [Fact]
    public async Task DefaultReturnBuffer_RoutesToSharedPool_ForFetchersThatKeepIt()
    {
        // Every fake fetcher (tests, VerticalSlice) rents from Shared and implements only FetchAsync; the interface's
        // default member is what keeps the scheduler's single ReturnBuffer call correct for them.
        IImageFetcher f = new SharedRentingFetcher();
        var r = await f.FetchAsync("x", TestContext.Current.CancellationToken);
        f.ReturnBuffer(r.Buffer!);   // Shared.Return of a Shared rental: no throw is the contract
    }
}

public sealed class ImageFetchPoolTrimPolicyTests
{
    [Fact]
    public void TrimsOnlyAnUsedPoolAfterTheIdleWindow()
    {
        const long idle = DefaultImageFetcher.PoolIdleTrimMs;
        Assert.False(DefaultImageFetcher.ShouldTrimPool(1_000 + idle - 1, 1_000, dirty: true));
        Assert.True(DefaultImageFetcher.ShouldTrimPool(1_000 + idle, 1_000, dirty: true));
        Assert.False(DefaultImageFetcher.ShouldTrimPool(1_000 + 10 * idle, 1_000, dirty: false));
    }
}
