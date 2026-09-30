using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The always-on image latency census (<see cref="ImageLatencyCensus"/>): its histogram arithmetic, and the
/// cache events that feed it — a miss, a hit, a landing's reveal kind and a canceled pending decode.</summary>
public sealed class ImageLatencyCensusTests
{
    [Fact]
    public void Histogram_quantiles_are_the_upper_edge_of_the_bucket_that_holds_them()
    {
        var h = new LatencyHistogram();
        Assert.True(float.IsNaN(h.Quantile(0.5f)));
        foreach (float ms in new[] { 5f, 10f, 20f, 40f, 90f, 300f, 1200f, 2200f, 2300f, 9000f }) h.Add(ms);
        Assert.Equal(10, h.Count);
        Assert.Equal(100f, h.Quantile(0.5f));          // the 5th sample (90 ms) sits in (66, 100]
        Assert.Equal(4000f, h.Quantile(0.9f));         // the 9th (2300 ms) sits in (2000, 4000]
        Assert.True(float.IsPositiveInfinity(h.Quantile(1f)));   // 9000 ms: the open bucket
        Assert.Equal(9000f, h.MaxMs);
        Assert.Equal("n=10 p50<100 p90<4000 max=9000", h.Format());
        h.Reset();
        Assert.Equal("n=0", h.Format());
    }

    [Fact]
    public void A_miss_a_hit_and_the_landing_are_counted_and_the_first_decode_gets_the_full_fade()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        var art = cache.Request("art", 64, 64);
        Assert.Equal(1, cache.Latency.Misses);
        cache.Pump();                                  // lands: the first texture → Fetch + a FULL reveal
        Assert.Equal(1, cache.Latency.Fetch.Count);
        Assert.Equal(1, cache.Latency.RevealFull);
        cache.Request("art", 64, 64);                  // already cached → a hit, no new decode, no new reveal
        Assert.Equal(1, cache.Latency.Hits);
        Assert.Equal(1, cache.Latency.Misses);
        Assert.Equal(1, cache.Latency.RevealFull + cache.Latency.RevealShort + cache.Latency.RevealNone);
        Assert.Equal(ImageState.Ready, cache.StateOf(art));
        Assert.StartsWith("srcWait(n=0) srcImmediate=0 fetch(n=1 ", cache.Latency.FormatLine());
    }

    [Fact]
    public void A_pending_decode_given_up_by_its_node_counts_as_canceled_and_a_settled_one_does_not()
    {
        var cache = new ImageCache(new FakeImageDecoder());
        var pending = cache.Request("row-cover", 64, 64);
        cache.Cancel(pending);                         // a de-realized row's unpin → cancel while Pending
        Assert.Equal(1, cache.Latency.Canceled);
        var ready = cache.Request("other-cover", 64, 64);
        cache.Pump();
        cache.Cancel(ready);                           // nothing in flight: not a cancel
        Assert.Equal(1, cache.Latency.Canceled);
        cache.Latency.Reset();
        Assert.True(cache.Latency.IsEmpty);
    }
}
