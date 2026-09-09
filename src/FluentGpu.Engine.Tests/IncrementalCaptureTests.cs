using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Operation ultra-fast GPU engine, P8 — the publisher half of incremental scene capture. The snapshot's own
/// correctness is proven by the <c>gate.capture.*</c> VerticalSlice gates (column parity against a from-scratch
/// capture); what these tests pin is the SEAM plumbing around it: each of the three slots carries its own capture
/// baseline, the incremental path is chosen only when the store can prove the delta, and
/// <see cref="SceneFramePublisher.OldestSlotCaptureSeq"/> — the sequence the store's ledgers must be retained through —
/// really is the minimum across all three.
/// </summary>
public sealed class IncrementalCaptureTests
{
    private static ulong Publish(SceneFramePublisher publisher, SceneStore scene, AnimEngine anim)
    {
        var options = default(SceneRecordOptions);
        ulong seq = publisher.PublishScene(scene, new ImageCache(new FakeImageDecoder()), new StringTable(), in options,
            default, default, default, new DetachedAnimSlab(), new List<PopupWindowSlot>(), anim,
            new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent), suppressVsync: false, interactivePresent: false);
        scene.NotePublished(seq);
        scene.ClearTransformDirty();
        ulong retain = System.Math.Min(publisher.LastConsumedSeq, publisher.OldestSlotCaptureSeq);
        scene.ClearPendingRemovals(retain);
        scene.ClearRecordDirty(retain);
        scene.ClearCaptureLedger(publisher.OldestSlotCaptureSeq);
        return seq;
    }

    private static (SceneStore scene, NodeHandle content) BuildPage(int rows)
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var content = scene.CreateNode(2);
        scene.AppendChild(root, content);
        for (int i = 0; i < rows; i++)
        {
            var row = scene.CreateNode(3);
            scene.AppendChild(content, row);
            scene.Bounds(row) = new(0, i * 24, 200, 24);
        }
        return (scene, content);
    }

    // The ledger-retention floor. Slots rotate, so the oldest live snapshot trails the newest by one publication; a
    // slot that has NEVER captured must not count as "describes publication 0", or the floor would pin every ledger
    // open forever and record-dirty bits would never clear.
    [Fact]
    public void OldestSlotCaptureSeqIsTheOldestLiveSnapshotNotAnUnusedSlot()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var (scene, content) = BuildPage(24);
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();

        Assert.Equal(0UL, publisher.OldestSlotCaptureSeq);   // nothing published: retain everything
        Assert.Equal(1UL, Publish(publisher, scene, anim));
        Assert.Equal(1UL, publisher.OldestSlotCaptureSeq);   // one live snapshot, and it is publication 1
        Assert.Equal(2UL, Publish(publisher, scene, anim));
        Assert.Equal(1UL, publisher.OldestSlotCaptureSeq);   // two live snapshots; the older is still 1

        for (int i = 0; i < 4; i++)
        {
            scene.Mark(content, NodeFlags.PaintDirty);
            ulong seq = Publish(publisher, scene, anim);
            Assert.Equal(seq - 1, publisher.OldestSlotCaptureSeq);   // steady state: exactly one publication behind
        }
    }

    // The whole point: a publication that changed nothing but one node's paint copies that node's chain, not the tree.
    [Fact]
    public void ACoastPublicationCopiesOnlyTheChangedChain()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var (scene, content) = BuildPage(300);
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();

        Publish(publisher, scene, anim);
        int fullCopied = publisher.LastCapturedNodeCount;
        Assert.False(publisher.LastCaptureWasIncremental);
        Assert.True(fullCopied > 300, $"a first capture must copy the whole reachable tree (copied {fullCopied})");

        // Cycle every slot through one full capture, then coast.
        for (int i = 0; i < 3; i++) Publish(publisher, scene, anim);

        scene.Paint(content).LocalTransform = Affine2D.Translation(0f, -40f);
        scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        Publish(publisher, scene, anim);

        Assert.True(publisher.LastCaptureWasIncremental, "a coast publication must take the incremental path");
        Assert.True(publisher.LastCapturedNodeCount <= 8,
            $"a coast publication copied {publisher.LastCapturedNodeCount} nodes; expected the changed chain only");
    }

    // "When incremental validity is in doubt, fall back" — a bulk mutation (what a reconciler commit or a layout pass
    // declares) must force the next capture of EVERY slot back to a full copy.
    [Fact]
    public void ABulkMutationForcesAFullCaptureOnEverySlot()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var (scene, content) = BuildPage(64);
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();
        for (int i = 0; i < 4; i++) Publish(publisher, scene, anim);

        scene.Mark(content, NodeFlags.PaintDirty);
        Publish(publisher, scene, anim);
        Assert.True(publisher.LastCaptureWasIncremental);

        scene.NoteBulkMutation();   // what FlexLayout.BeginMeasurePass / Reconciler.WriteColumns declare
        for (int i = 0; i < 2; i++)
        {
            Publish(publisher, scene, anim);
            Assert.False(publisher.LastCaptureWasIncremental,
                "every slot whose baseline predates the bulk mutation must fall back to a full capture");
        }
        // Once each rotating slot has re-captured past it, incremental is available again.
        scene.Mark(content, NodeFlags.PaintDirty);
        Publish(publisher, scene, anim);
        Assert.True(publisher.LastCaptureWasIncremental);
    }
}
