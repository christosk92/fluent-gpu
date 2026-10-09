using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The frame mailbox while the UI writes its next frame: the render turn that opens its slot in that window must
/// still find the publication that was already waiting (a stale re-present there skipped a frame of every animation).</summary>
public sealed class SceneFramePublisherTests
{
    [Fact]
    public void ThePublishedFrameStaysAcquirableWhileTheNextOneIsBeingWritten()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        ulong first = seam.Publish([1], [1UL], default(FrameInfo));

        int writing = seam.ClaimWriteSlot(first + 1);   // the UI has started capturing its next frame

        Assert.True(seam.HasPendingFrame);
        Assert.True(seam.TryAcquire(out var frame));
        Assert.Equal(first, frame.PublishSeq);
        Assert.NotEqual(writing, frame.ArenaIndex);   // never the slot under the writer
    }

    [Fact]
    public void AFrameAlreadyAdoptedIsNotOfferedAgainWhileTheNextOneIsBeingWritten()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        ulong first = seam.Publish([1], [1UL], default(FrameInfo));
        Assert.True(seam.TryAcquire(out _));

        seam.ClaimWriteSlot(first + 1);

        Assert.False(seam.HasPendingFrame);
        Assert.False(seam.TryAcquire(out _));
    }

    [Fact]
    public void TheNextPublicationSupersedesTheReannouncedOne()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        seam.Publish([1], [1UL], default(FrameInfo));
        ulong second = seam.Publish([2], [2UL], default(FrameInfo));

        Assert.True(seam.TryAcquire(out var frame));
        Assert.Equal(second, frame.PublishSeq);
        Assert.Equal((byte)2, seam.Bytes(in frame)[0]);
    }
}
