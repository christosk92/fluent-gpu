using FluentGpu.Media;
using Xunit;

namespace FluentGpu.Tests;

public sealed class SeekPreviewSchedulerTests
{
    [Fact]
    public void BurstKeepsLatestTargetAndDispatchesItWhenDue()
    {
        var scheduler = new SeekPreviewScheduler();
        scheduler.Queue(10_000);
        Assert.True(scheduler.TryTake(1_000, out long first));
        Assert.Equal(10_000, first);
        scheduler.Queue(20_000);
        scheduler.Queue(30_000);
        Assert.False(scheduler.TryTake(1_099, out _));
        Assert.True(scheduler.HasPending);
        Assert.True(scheduler.TryTake(1_100, out long latest));
        Assert.Equal(30_000, latest);
        Assert.False(scheduler.TryTake(1_200, out _));
    }

    [Fact]
    public void ReleaseDiscardsPostedPreviewAndNewGestureCanStartImmediately()
    {
        var scheduler = new SeekPreviewScheduler();
        scheduler.Queue(10);
        Assert.True(scheduler.TryTake(100, out _));
        scheduler.Queue(20);
        scheduler.DiscardPending();
        Assert.False(scheduler.TryTake(200, out _));
        scheduler.Reset();
        scheduler.Queue(30);
        Assert.True(scheduler.TryTake(101, out long target));
        Assert.Equal(30, target);
    }

    [Fact]
    public void FirstDispatchDoesNotDependOnClockOrigin()
    {
        var scheduler = new SeekPreviewScheduler();
        scheduler.Queue(0);
        Assert.True(scheduler.TryTake(0, out long target));
        Assert.Equal(0, target);
    }
}
