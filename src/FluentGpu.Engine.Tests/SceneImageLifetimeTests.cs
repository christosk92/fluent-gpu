using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SceneImageLifetimeTests
{
    private static ImageRecordingSnapshot Ready(int id)
    {
        var snapshot = new ImageRecordingSnapshot();
        snapshot.Add(id, ImageState.Ready, 32, 32, float.NaN, 0, 0);
        return snapshot;
    }

    [Fact]
    public void RetainedSceneProtectsTextureAcrossArbitrarilyManyRenderTurns()
    {
        var queue = new ImageUploadQueue();
        object host = new();
        queue.SetSceneReader(host, Ready(7));
        queue.EnqueueEvict(7);
        for (int turn = 0; turn < 1000; turn++) Assert.False(queue.TryDequeueJob(out _));
        queue.SetSceneReader(host, new ImageRecordingSnapshot());
        Assert.True(queue.TryDequeueJob(out var eviction));
        Assert.True(eviction.Evict);
        Assert.Equal(7, eviction.Id);
        Assert.False(queue.TryDequeueJob(out _));
    }

    [Fact]
    public void SharedDeviceWaitsForEverySceneReader()
    {
        var queue = new ImageUploadQueue();
        object parent = new(), child = new();
        queue.SetSceneReader(parent, Ready(9));
        queue.SetSceneReader(child, Ready(9));
        queue.EnqueueEvict(9);
        Assert.False(queue.TryDequeueJob(out _));
        queue.RemoveSceneReader(parent);
        Assert.False(queue.TryDequeueJob(out _));
        queue.RemoveSceneReader(child);
        Assert.True(queue.TryDequeueJob(out var eviction));
        Assert.True(eviction.Evict);
        Assert.Equal(9, eviction.Id);
    }

    [Fact]
    public void ReadoptionBeforeDeferredDrainKeepsResourceAlive()
    {
        var queue = new ImageUploadQueue();
        object host = new();
        var image = Ready(11);
        queue.SetSceneReader(host, image);
        queue.EnqueueEvict(11);
        Assert.False(queue.TryDequeueJob(out _));
        queue.RemoveSceneReader(host);
        queue.SetSceneReader(host, image);
        Assert.False(queue.TryDequeueJob(out _));
        queue.RemoveSceneReader(host);
        Assert.True(queue.TryDequeueJob(out var eviction));
        Assert.Equal(11, eviction.Id);
    }

    [Fact]
    public void NewUploadSupersedesDeferredOldEviction()
    {
        var queue = new ImageUploadQueue();
        object host = new();
        queue.SetSceneReader(host, Ready(13));
        queue.EnqueueEvict(13);
        Assert.False(queue.TryDequeueJob(out _));
        byte[] pixels = [1, 2, 3, 4];
        queue.EnqueueUpload(13, pixels, 1, 1, pixels.Length);
        Assert.True(queue.TryDequeueJob(out var upload));
        Assert.False(upload.Evict);
        Assert.Same(pixels, upload.Buffer);
        queue.RemoveSceneReader(host);
        Assert.False(queue.TryDequeueJob(out _));
    }
}
