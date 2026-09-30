using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
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
            new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent), suppressVsync: false);
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

    [Fact]
    public void AThirdSlotUsedDuringHandoverCannotPinTheRetentionFloorForever()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var (scene, _) = BuildPage(3);
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();
        Publish(publisher, scene, anim);
        Assert.True(publisher.TryAcquire(out var first));
        Publish(publisher, scene, anim);
        Publish(publisher, scene, anim); // first is retained and second announced: capture needs the third slot
        Assert.True(publisher.TryAcquire(out var third));
        Assert.NotEqual(first.ArenaIndex, third.ArenaIndex);

        for (int i = 0; i < 12; i++)
        {
            ulong seq = Publish(publisher, scene, anim);
            Assert.True(publisher.TryAcquire(out _));
            Assert.True(publisher.OldestSlotCaptureSeq >= seq - 2,
                $"publication {seq} still retains snapshot {publisher.OldestSlotCaptureSeq}");
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

    // A node written on CONSECUTIVE publications while the consumer has not caught up keeps its record-dirty bits set
    // the whole time (each write re-stamps them one publication ahead of consumption, so the retained ledger never
    // clears them). Every write must still reach the snapshot: the ledger used to note a capture change only when the
    // dirty bits CHANGED, so from the second write on the node looked unchanged to the incremental capture and a
    // rotating slot kept serving an earlier value — the lyrics karaoke wipe stepping at the full-capture cadence.
    [Fact]
    public void ConsecutiveWritesToAStillDirtyNode_EachReachTheSnapshot()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var (scene, content) = BuildPage(24);
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();
        for (int i = 0; i < 4; i++) Publish(publisher, scene, anim);   // every slot holds a baseline; incremental from here

        // No consumer adopts anything below, so LastConsumedSeq stays put and the retained ledger keeps the bits set.
        for (int step = 1; step <= 6; step++)
        {
            scene.Paint(content).BlurSigma = step;
            scene.Mark(content, NodeFlags.PaintDirty);
            Publish(publisher, scene, anim);
            Assert.True(publisher.LastCaptureWasIncremental, "the steady writes must stay on the incremental path");
        }

        Assert.True(publisher.TryAcquire(out var frame));
        Assert.Equal(6f, publisher.Scene(frame).Scene.Paint(content).BlurSigma);
    }

    // "When incremental validity is in doubt, fall back" — a bulk mutation must force the next capture of EVERY slot
    // back to a full copy. Since scroll-root-cause-2026-09-23 §5 (Part A) neither FlexLayout.BeginMeasurePass nor
    // Reconciler.WriteColumns declares this any more (both ledger precisely instead — see the tests below); the one
    // caller left is SceneStore's own column-realloc/trim path (SceneStore.Capture.cs's NoteCaptureColumnsResized),
    // which this test still drives directly as the escape hatch's contract, independent of who else might call it.
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

        scene.NoteBulkMutation();   // the escape hatch: "I touched captured columns I cannot enumerate"
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

    // Root cause 4 (scroll-root-cause-2026-09-23.md §5): before the fix, EVERY mount/rebind (Reconciler.WriteColumns)
    // called NoteBulkMutation() unconditionally, so a virtualized list recycling rows on a scroll coast forced a FULL
    // copy of the whole scene every frame (1750-3139 nodes measured in the field) even though only the recycled
    // WINDOW of rows actually changed. WriteColumns now ledgers precisely — `_scene.NoteCaptureChanged((int)node.Raw.
    // Index)` for the one node it just rebound — so this drives that exact primitive across a RECYCLE-sized window
    // (8 of 300 rows, matching a typical realized-window slide) and proves both halves of the fix: the capture stays
    // incremental AND (DEBUG parity self-check) is column-for-column identical to a from-scratch full capture.
    [Fact]
    public void VirtualizedListRecyclingRowsOnAScrollCoastStaysIncrementalAndParityIdentical()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        const int Rows = 300, RecycledFirst = 40, RecycledCount = 8;
        var (scene, _) = BuildPage(Rows);
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();
        for (int i = 0; i < 4; i++) Publish(publisher, scene, anim);   // every slot holds a full baseline

        // Simulate one coast frame's realize-window slide: RecycledCount rows are rebound in place (their content/
        // paint changes; the DOM position — their node index — does not, exactly like ItemsView's recycle contract).
        // No layout pass runs (scrolling is deliberately layout-free, layout.md §6) and nothing else in the tree moves.
        for (int i = 0; i < RecycledCount; i++)
        {
            var row = scene.FirstChild(scene.Root);
            for (int hop = 0; hop < RecycledFirst + i; hop++) row = scene.NextSibling(row);
            scene.Paint(row).Fill = new ColorF(1f, 0f, 0f, 1f);
            scene.NoteCaptureChanged((int)row.Raw.Index);   // exactly what Reconciler.WriteColumns now calls for a rebind
        }
        ulong seq = Publish(publisher, scene, anim);

        Assert.True(publisher.LastCaptureWasIncremental, "a pure recycle rebind must stay on the incremental path");
        Assert.True(publisher.LastCapturedNodeCount <= RecycledCount + 4,
            $"a recycle of {RecycledCount} rows copied {publisher.LastCapturedNodeCount} nodes; expected the recycled window only");
        Assert.True(publisher.TryAcquire(out var frame));
        Assert.Equal(0, publisher.Scene(frame).Scene.IncrementalParityFailures);
        _ = seq;
    }

    // Root cause 4's other half: FlexLayout.BeginMeasurePass used to call NoteBulkMutation() on every layout pass —
    // including a scoped relayout that only shifts a handful of rows (one row's height changing pushes every row
    // below it down). BeginMeasurePass no longer marks anything itself; SetArrangedBounds (the per-node Bounds
    // commit every non-early-outed Arrange lands on) ledgers each moved row precisely. This drives a REAL FlexLayout
    // solve (not a synthetic store write) that moves many rows and proves the capture stays incremental and parity-
    // identical to a from-scratch full capture.
    [Fact]
    public void ALayoutPassThatMovesManyRowsStaysIncrementalAndParityIdentical()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        const int Rows = 200;
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Layout(root).Direction = 1;      // column stack
        scene.Layout(root).Width = 300f;
        scene.Layout(root).Height = 10_000f;

        var rows = new NodeHandle[Rows];
        for (int i = 0; i < Rows; i++)
        {
            var row = scene.CreateNode(2);
            scene.AppendChild(root, row);
            scene.Layout(row).Width = 300f;
            scene.Layout(row).Height = 24f;
            rows[i] = row;
        }
        layout.Run(root);
        var yBefore = new float[Rows];
        for (int i = 0; i < Rows; i++) yBefore[i] = scene.Bounds(rows[i]).Y;

        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();
        for (int i = 0; i < 4; i++) Publish(publisher, scene, anim);   // every slot holds a full baseline

        // Grow row 0 — every row after it re-arranges to a new Y. A real "N nodes move" layout pass, with no
        // reconcile involved (WriteColumns never runs) — isolates BeginMeasurePass's own contribution.
        const float GrowBy = 72f;
        scene.Layout(rows[0]).Height = 24f + GrowBy;
        scene.Mark(rows[0], NodeFlags.LayoutDirty);
        layout.Run(root);

        for (int i = 1; i < Rows; i++)
            Assert.Equal(yBefore[i] + GrowBy, scene.Bounds(rows[i]).Y, 3);   // sanity: they really did move

        Publish(publisher, scene, anim);
        Assert.True(publisher.LastCaptureWasIncremental, "a layout pass that only moved rows must stay incremental");
        Assert.True(publisher.TryAcquire(out var frame));
        Assert.Equal(0, publisher.Scene(frame).Scene.IncrementalParityFailures);
    }

    // Root cause 4's remaining escape hatch: a genuinely unenumerable change (here, SceneStore's own column trim —
    // SceneStore.cs:TrimExcessCapacity, one of the "column reallocation, capacity growth, trims" the plan names)
    // must still force a full capture, exactly like the synthetic NoteBulkMutation() case above — proving the fix
    // narrowed the escape hatch's CALLERS (Part A), not its correctness contract.
    [Fact]
    public void AColumnTrimStillForcesAFullCaptureOnEverySlot()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        const int Total = 600, Keep = 40;
        var rows = new NodeHandle[Total];
        for (int i = 0; i < Total; i++)
        {
            var row = scene.CreateNode(2);
            scene.AppendChild(root, row);
            rows[i] = row;
        }
        var anim = new AnimEngine(scene);
        var publisher = new SceneFramePublisher();
        Publish(publisher, scene, anim);
        Assert.True(publisher.LastCapturedNodeCount > Total, "the baseline must cover the whole (large) tree");
        for (int i = 0; i < 3; i++) Publish(publisher, scene, anim);   // every slot now holds a full baseline

        for (int i = Keep; i < Total; i++) scene.FreeSubtree(rows[i]);
        int reclaimed = scene.TrimExcessCapacity();
        Assert.True(reclaimed > 0, "the mostly-empty tail must actually trim for this test to exercise the escape hatch");

        for (int i = 0; i < 2; i++)
        {
            Publish(publisher, scene, anim);
            Assert.False(publisher.LastCaptureWasIncremental,
                "every slot whose baseline predates the column trim must fall back to a full capture");
        }
        scene.Mark(rows[0], NodeFlags.PaintDirty);
        Publish(publisher, scene, anim);
        Assert.True(publisher.LastCaptureWasIncremental, "incremental resumes once every slot has re-captured past the trim");
    }
}
