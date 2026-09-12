using System;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

public sealed class SmallImageHeapPolicyTests
{
    [Fact]
    public void InitialActivationMustActuallyCompleteBeforeCpuLease()
    {
        var page = new SmallImagePageState(2);
        Assert.False(page.TryAcquire(out _, out _));
        page.SubmitActivation(7);
        page.Complete(6);
        Assert.False(page.TryAcquire(out _, out _));
        page.Complete(ulong.MaxValue); // documented device-removal sentinel
        Assert.False(page.TryAcquire(out _, out _));
        page.Complete(7);
        Assert.True(page.TryAcquire(out _, out _));
    }

    [Fact]
    public void PublishedLeaseCannotReturnBeforeItsActualLastUseFence()
    {
        var page = new SmallImagePageState(1);
        page.SubmitActivation(2); page.Complete(2);
        Assert.True(page.TryAcquire(out int slot, out int generation));
        Assert.False(page.Release(slot, generation, 10, 9));
        Assert.False(page.TryAcquire(out _, out _));
        Assert.False(page.Release(slot, generation, 10, ulong.MaxValue));
        Assert.True(page.Release(slot, generation, 10, 10));
        Assert.True(page.TryAcquire(out int again, out int nextGeneration));
        Assert.Equal(slot, again);
        Assert.NotEqual(generation, nextGeneration);
        Assert.False(page.Release(slot, generation, 10, 10));
        Assert.Equal(1, page.LiveCount);
    }

    [Fact]
    public void DuplicateAndInvalidReturnsDoNotDuplicateFreeCapacity()
    {
        var page = new SmallImagePageState(1);
        page.SubmitActivation(2); page.Complete(2);
        Assert.True(page.TryAcquire(out int slot, out int generation));
        Assert.False(page.Release(-1, generation, 0, 0));
        Assert.False(page.Release(1, generation, 0, 0));
        Assert.True(page.Release(slot, generation, 0, 0)); // unpublished failed acquisition
        Assert.False(page.Release(slot, generation, 0, 0));
        Assert.True(page.TryAcquire(out _, out _));
        Assert.False(page.TryAcquire(out _, out _));
    }

    [Fact]
    public void QueriedStrideNotPixelBytesControlsPageCapacity()
    {
        Assert.True(SmallImageHeapPool.TryLayout(20480, 4096, 65536, 131072, out int first));
        Assert.Equal(6, first);
        Assert.True(SmallImageHeapPool.TryLayout(20480, 4096, 65536, 1048576, out int growth));
        Assert.Equal(51, growth);
        Assert.False(SmallImageHeapPool.TryLayout(ulong.MaxValue, 65536, 65536, 131072, out _));
        Assert.False(SmallImageHeapPool.TryLayout(65536, 4096, 65536, 131072, out _)); // no byte saving
        Assert.False(SmallImageHeapPool.TryLayout(20480, 65536, 65536, 131072, out _));
        Assert.False(SmallImageHeapPool.TryLayout(20000, 4096, 65536, 131072, out _));
        Assert.False(SmallImageHeapPool.TryLayout(20480, 4096, 65536, 4096, out _)); // heap is not 4KiB aligned
    }

    [Fact]
    public void ReserveLimitIncludesPagesWaitingForActivationOrLeaseRetirement()
    {
        ulong reserved = 0;
        Assert.True(SmallImageHeapPool.CanReserve(reserved, SmallImageHeapPool.FirstPageBytes));
        reserved += SmallImageHeapPool.FirstPageBytes;
        while (SmallImageHeapPool.CanReserve(reserved, SmallImageHeapPool.GrowthPageBytes))
            reserved += SmallImageHeapPool.GrowthPageBytes;
        Assert.InRange(reserved, 15ul * 1024 * 1024, SmallImageHeapPool.MaxHeapBytes);
        Assert.False(SmallImageHeapPool.CanReserve(reserved, SmallImageHeapPool.GrowthPageBytes));
        Assert.False(SmallImageHeapPool.CanReserve(ulong.MaxValue, SmallImageHeapPool.FirstPageBytes));
        Assert.False(SmallImageHeapPool.CanReserve(0, 4096));
    }

    [Fact]
    public void WarmAcquireAndReturnDoNotAllocate()
    {
        var page = new SmallImagePageState(4);
        page.SubmitActivation(1); page.Complete(1);
        page.TryAcquire(out int index, out int generation); page.Release(index, generation, 1, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            page.TryAcquire(out index, out generation);
            page.Release(index, generation, 1, 1);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(0, page.LiveCount);
    }
}
