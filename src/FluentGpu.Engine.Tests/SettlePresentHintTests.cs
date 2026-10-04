using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F101: the resize-settle hint rides the publication it was set for (like <c>SuppressVsync</c>) instead of being a flag the UI
/// thread pokes into render-owned swapchain state, and the swapchain's settle sync is a separate step the host runs AFTER the
/// frame's video placement commit. The real DwmFlush ordering is a Windows-only behaviour; what is pinned here is the transport
/// (which frame carries the hint) and the seam contract (arm on one call, consume on the other, default backend no-op).
/// </summary>
public sealed class SettlePresentHintTests
{
    [Fact]
    public void SettleHint_RidesTheFrameItWasPublishedWith()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher();

        publisher.Publish([1], default, default, suppressVsync: true, settlePresent: true);
        Assert.True(publisher.TryAcquire(out var settle));
        Assert.True(settle.SettlePresent);
        Assert.True(settle.SuppressVsync);

        publisher.Publish([2], default, default);
        Assert.True(publisher.TryAcquire(out var ordinary));
        Assert.False(ordinary.SettlePresent);
    }

    [Fact]
    public void SettleHint_OfASupersededPublication_DoesNotLeakOntoTheNewerFrame()
    {
        // The old cross-thread flag was consumed by whichever present came next. A publication the renderer never adopted must
        // take its hint with it: the frame that IS presented decides for itself.
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher();

        publisher.Publish([1], default, default, suppressVsync: true, settlePresent: true);
        publisher.Publish([2], default, default);
        Assert.True(publisher.TryAcquire(out var adopted));
        Assert.Equal(2UL, adopted.PublishSeq);
        Assert.False(adopted.SettlePresent);
    }

    [Fact]
    public void HeadlessSwapchain_ArmsOnHint_AndCompleteRecordsTheBlockingChoice()
    {
        var sc = new HeadlessSwapchain(new Size2(64, 64));
        ISwapchain seam = sc;

        seam.HintSettlePresent();
        Assert.Equal(1, sc.HintSettlePresentCount);
        Assert.Equal(0, sc.CompleteSettlePresentCount);

        seam.CompleteSettlePresent(blockUntilComposed: false);
        Assert.Equal(1, sc.CompleteSettlePresentCount);
        Assert.False(sc.LastSettleBlocked);

        seam.CompleteSettlePresent(blockUntilComposed: true);
        Assert.True(sc.LastSettleBlocked);
    }
}
