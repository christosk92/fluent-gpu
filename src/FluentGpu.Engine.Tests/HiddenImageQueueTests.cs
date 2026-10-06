using System.Collections.Generic;
using System.Linq;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The Shallow image release over the real UI-to-render evict queue: what <see cref="ImageCache.ReleaseUnpinnedGpu"/>
/// evicts is exactly what the render thread's off-frame drain receives, nothing pinned or held is in it, and a second
/// release enqueues nothing.</summary>
public sealed class HiddenImageQueueTests
{
    private const int Px = 64;

    [Fact]
    public void TheReleasedUnpinnedSetIsExactlyWhatTheRenderThreadDrainsAsEvictions()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var held = new HashSet<int>();
        var cache = new ImageCache(new FakeImageDecoder(), budgetBytes: 1L << 30);
        cache.SetHeldImageSource(set => set.UnionWith(held));
        var queue = new ImageUploadQueue();
        cache.SetEvictSink(queue.EnqueueEvict);

        var pinned = cache.Request("https://covers.example/pinned", Px, Px);
        cache.Pin(pinned);
        var rowCell = cache.Request("https://covers.example/row", Px, Px);
        held.Add(rowCell.Id);
        var a = cache.Request("https://covers.example/a", Px, Px);
        var b = cache.Request("https://covers.example/b", Px, Px);
        cache.Pump();
        Assert.Equal(4, cache.ReadyCount);

        Assert.Equal(2, cache.ReleaseUnpinnedGpu());

        var drained = new List<int>();
        while (queue.TryDequeueJob(out var job))
        {
            Assert.True(job.Evict);
            drained.Add(job.Id);
        }
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), drained.OrderBy(x => x));

        Assert.Equal(0, cache.ReleaseUnpinnedGpu());
        Assert.False(queue.TryDequeueJob(out _));
    }
}
