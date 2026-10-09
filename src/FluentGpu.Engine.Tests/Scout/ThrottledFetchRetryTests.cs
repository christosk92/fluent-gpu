using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary><see cref="DefaultImageFetcher"/> mapped HTTP 429 Too Many Requests and 408 Request Timeout to the permanent
/// <see cref="ImageFailureKind.HttpError"/>, so a cover the CDN throttled during a fast scroll was never retried by the
/// scheduler and ImageCache kept it terminal for the session. Both now map to transient kinds and retry with backoff.</summary>
public sealed class ThrottledFetchRetryTests
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

    /// <summary>Answers the first request with <c>first</c>, every later one with 200 and a small body.</summary>
    private sealed class ScriptedHandler(HttpStatusCode first) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = Interlocked.Increment(ref _calls) == 1 ? first : HttpStatusCode.OK;
            var resp = new HttpResponseMessage(status) { RequestMessage = request };
            if (status == HttpStatusCode.OK) resp.Content = new ByteArrayContent(new byte[64]);
            return Task.FromResult(resp);
        }
    }

    private static (bool Ok, ImageFailureKind Failure, int Attempts, int Calls) Load(HttpStatusCode first)
    {
        var handler = new ScriptedHandler(first);
        using var http = new HttpClient(handler);
        using var fetcher = new DefaultImageFetcher(http);
        using var scheduler = new DecodeScheduler(new Codec(), fetcher, new DecodeOptions
        {
            MaxConcurrency = 1, MaxAttempts = 3,
            BackoffBase = TimeSpan.FromMilliseconds(1), BackoffMax = TimeSpan.FromMilliseconds(1),
        });
        Assert.True(scheduler.Begin(1, "https://img.test/cover.jpg", 8, 8));

        bool done = false, ok = false;
        var failure = ImageFailureKind.None;
        int attempts = 0;
        var sw = Stopwatch.StartNew();
        while (!done && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            scheduler.Pump((_, o, _, _, f, a) => { done = true; ok = o; failure = f; attempts = a; }, static (_, _, _, _) => { });
            if (!done) Thread.Sleep(1);
        }
        Assert.True(done);
        return (ok, failure, attempts, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void ThrottledOrTimedOutResponse_IsRetried(HttpStatusCode first)
    {
        var r = Load(first);
        Assert.True(r.Ok);
        Assert.Equal(ImageFailureKind.None, r.Failure);
        Assert.Equal(2, r.Attempts);   // one throttled attempt, one successful retry
        Assert.Equal(2, r.Calls);
    }

    [Fact]
    public void ForbiddenResponse_StaysPermanent()
    {
        var r = Load(HttpStatusCode.Forbidden);
        Assert.False(r.Ok);
        Assert.Equal(ImageFailureKind.HttpError, r.Failure);
        Assert.Equal(1, r.Attempts);   // a real 4xx is never retried
        Assert.Equal(1, r.Calls);
    }
}
