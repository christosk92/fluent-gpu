using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class RenderLifecycleTests
{
    [Fact]
    public void ImmediateRepeatedParkResumeCannotLoseTheNextParkRequest()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher();
        using var renderer = new RenderThread(publisher, _ => { }, async: true);
        for (int i = 0; i < 1000; i++)
        {
            renderer.Quiesce();
            renderer.Resume();
        }
        publisher.Publish([1], default, default);
        renderer.DrainSync();
        Assert.Equal(1UL, publisher.LastConsumedSeq);
    }

    [Fact]
    public void TargetInvalidationRejectsRetainedAndPendingOldSnapshots()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher();
        publisher.Publish([1], default, default);
        Assert.True(publisher.TryAcquire(out var retained));
        publisher.Publish([2], default, default);
        publisher.InvalidateTarget();
        Assert.False(publisher.IsCurrentTarget(retained));
        Assert.False(publisher.TryAcquire(out _));
        publisher.Publish([3], default, default);
        Assert.True(publisher.TryAcquire(out var replacement));
        Assert.True(publisher.IsCurrentTarget(replacement));
        Assert.Equal(3UL, replacement.PublishSeq);
    }

    // The publisher is last-writer-wins, so a consumer that wakes once after two publications adopts only the newest.
    // Record-dirty bits and the pending-removal ledger therefore describe deltas since the last CONSUMED publication:
    // every entry is stamped with the publication it belongs to, and the host drops only entries whose publication the
    // renderer adopted; the rest union into the next capture (which keeps clean-span reuse valid across a gap).
    [Fact]
    public void RecordDirtyBitsSurviveASkippedPublication()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher();
        var scene = new FluentGpu.Scene.SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var a = scene.CreateNode(2); scene.AppendChild(root, a);
        var b = scene.CreateNode(2); scene.AppendChild(root, b);
        scene.ClearRecordDirty();

        // Publication 1 carries a's mark (stamped 1 = PublishSeq 0 + 1). The consumer is parked, so nothing is adopted.
        scene.Mark(a, FluentGpu.Foundation.NodeFlags.PaintDirty);
        ulong first = publisher.Publish([1], default, default);
        scene.NotePublished(first);
        scene.ClearRecordDirty(publisher.LastConsumedSeq);
        Assert.True(scene.IsRecordDirty(a));

        // Publication 2 carries b's mark AND a's (still set): the union across the skipped publication.
        scene.Mark(b, FluentGpu.Foundation.NodeFlags.PaintDirty);
        ulong second = publisher.Publish([2], default, default);
        scene.NotePublished(second);
        scene.ClearRecordDirty(publisher.LastConsumedSeq);
        Assert.True(scene.IsRecordDirty(a));
        Assert.True(scene.IsRecordDirty(b));
        Assert.True(scene.IsRecordDirty(root));

        // One wake, one adoption: publication 1 is skipped outright; publication 2 (with the union) is adopted.
        Assert.True(publisher.TryAcquire(out var adopted));
        Assert.Equal(second, adopted.PublishSeq);
        Assert.Equal(second, publisher.LastConsumedSeq);

        // A mark made after publication 2 belongs to publication 3 and must survive the clear that drops 1 and 2.
        var c = scene.CreateNode(2); scene.AppendChild(root, c);
        scene.ClearRecordDirty(publisher.LastConsumedSeq);
        Assert.False(scene.IsRecordDirty(a));
        Assert.False(scene.IsRecordDirty(b));
        Assert.True(scene.IsRecordDirty(c));
        Assert.True(scene.IsRecordDirty(root));   // the ancestor chain is re-stamped by the newest descendant mark
    }

    // A frame whose recorded stream hashes to the one already on screen, with an empty repaint set and no clock-driven
    // pixels, must not be submitted or presented again.
    [Fact]
    public void ByteIdenticalRenderFrameIsNotResubmitted()
    {
        const ulong stream = 0xC0FFEE_1234UL;

        Assert.True(AppHost.ShouldSkipRenderSubmit(stream, stream,
            repaintPending: false, clockActive: false, hasPopupWindows: false));

        // Each disqualifier on its own forces the submit.
        Assert.False(AppHost.ShouldSkipRenderSubmit(stream + 1, stream, false, false, false));   // the stream changed
        Assert.False(AppHost.ShouldSkipRenderSubmit(stream, stream, repaintPending: true, clockActive: false, hasPopupWindows: false));
        Assert.False(AppHost.ShouldSkipRenderSubmit(stream, stream, repaintPending: false, clockActive: true, hasPopupWindows: false));
        Assert.False(AppHost.ShouldSkipRenderSubmit(stream, stream, repaintPending: false, clockActive: false, hasPopupWindows: true));

        // No baseline yet (nothing presented on this target) is never a skip.
        Assert.False(AppHost.ShouldSkipRenderSubmit(stream, 0UL, false, false, false));
    }
}
