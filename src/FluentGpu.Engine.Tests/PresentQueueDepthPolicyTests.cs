using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The present-queue depth follows measured GPU margin (see <see cref="PresentQueueDepthPolicy"/> for the
/// latency/throughput trade-off), and the present-time prediction follows the depth actually in force.</summary>
public sealed class PresentQueueDepthPolicyTests
{
    private const double Refresh = 8.333;   // 120 Hz

    private static PresentQueueDepthPolicy Feed(PresentQueueDepthPolicy p, double gpuMs, int samples, ref ulong seq)
    {
        for (int i = 0; i < samples; i++) p.Observe(gpuMs, ++seq, Refresh);
        return p;
    }

    [Fact]
    public void AGpuWithMarginKeepsTheLowestLatencyDepth()
    {
        ulong seq = 0;
        var p = Feed(default, 4.0, 200, ref seq);
        Assert.Equal(1, p.Depth);
    }

    [Fact]
    public void AGpuNearTheRefreshBuysOneFrameOfQueue_AndGivesItBackWhenMarginReturns()
    {
        ulong seq = 0;
        var p = Feed(default, 7.5, 60, ref seq);              // 0.9 of the refresh, sustained
        Assert.Equal(2, p.Depth);
        p = Feed(p, 6.0, 60, ref seq);                        // 0.72: inside the hysteresis band — stays deep
        Assert.Equal(2, p.Depth);
        p = Feed(p, 4.0, 60, ref seq);                        // 0.48: margin is back
        Assert.Equal(1, p.Depth);
    }

    [Fact]
    public void ASingleSpikeNeverBuysLatency_AndARepeatedSampleIsOneObservation()
    {
        ulong seq = 0;
        var p = Feed(default, 4.0, 30, ref seq);
        for (int i = 0; i < 3; i++) p.Observe(20.0, ++seq, Refresh);   // an upload burst
        p = Feed(p, 4.0, 30, ref seq);
        Assert.Equal(1, p.Depth);
        ulong same = ++seq;
        for (int i = 0; i < 100; i++) p.Observe(9.0, same, Refresh);   // the same retired frame read 100 times
        Assert.Equal(1, p.Depth);
    }

    [Fact]
    public void ThePresentPredictionFollowsTheDepthInForce()
    {
        const long refreshQpc = 8_333, frameQpc = 1_000_000;
        var shallow = RefreshLattice.Build(true, frameQpc, refreshQpc, frameQpc + 100, 0, 1, maxFrameLatency: 1);
        var deep = RefreshLattice.Build(true, frameQpc, refreshQpc, frameQpc + 100, 0, 1, maxFrameLatency: 2);
        Assert.Equal(refreshQpc, deep.PresentQpc - shallow.PresentQpc);   // one refresh later per queued frame
    }
}
