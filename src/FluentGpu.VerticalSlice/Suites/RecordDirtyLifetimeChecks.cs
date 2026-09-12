using System;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Scene;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class RecordDirtyLifetimeChecks
{
    public static void Run()
    {
        AncestorSelfRetires();
        BitsRetireIndependently();
        RepeatedChildDamageStaysLocal();
        GrowthTrimAndReuse();
    }

    static (SceneStore Scene, NodeHandle Root, NodeHandle Child) Fixture(int capacity = 64)
    {
        var scene = new SceneStore(capacity);
        var root = scene.CreateNode(1); scene.Root = root;
        scene.Bounds(root) = new RectF(0, 0, 400, 300);
        scene.Paint(root).VisualKind = VisualKind.Box;
        scene.Paint(root).Fill = new ColorF(.1f, .1f, .1f, 1f);
        var child = scene.CreateNode(1); scene.AppendChild(root, child);
        scene.Bounds(child) = new RectF(10, 10, 50, 40);
        scene.Paint(child).VisualKind = VisualKind.Box;
        scene.Paint(child).Fill = new ColorF(.8f, .2f, .2f, 1f);
        scene.ClearRecordDirty();
        scene.ClearTransformDirty();
        return (scene, root, child);
    }

    static void AncestorSelfRetires()
    {
        var (scene, root, child) = Fixture();
        scene.Mark(root, NodeFlags.PaintDirty); // navigation changed this whole subtree in publication 1
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.Mark(child, NodeFlags.PaintDirty); // karaoke keeps writing a descendant in publication 2
        scene.ClearRecordDirty(1);
        bool retired = scene.RecordDirtySelfBits(root) == 0
            && scene.RecordDirtyDescendantBits(root) == SceneStore.RecordDirtyContent
            && scene.RecordDirtyBits(root) == SceneStore.RecordDirtyContent
            && scene.RecordDirtySelfBits(child) == SceneStore.RecordDirtyContent;
        bool incremental = snapshot.CaptureIncremental(scene, default, 1);
        Check("gate.damage.ancestor-self-retires a new descendant cannot retain consumed ancestor self-content",
            retired && incremental && snapshot.RecordDirtySelfBits(root) == 0
            && snapshot.RecordDirtyDescendantBits(root) == SceneStore.RecordDirtyContent,
            $"retired={retired} incremental={incremental} rootSelf={snapshot.RecordDirtySelfBits(root)}");
        scene.Mark(root, NodeFlags.PaintDirty); // this really IS a fresh self write: it must survive consuming 1
        scene.ClearRecordDirty(1);
        bool freshHeld = scene.RecordDirtySelfBits(root) == SceneStore.RecordDirtyContent;
        scene.NotePublished(2);
        scene.ClearRecordDirty(2);
        Check("gate.damage.fresh-self-retained a newer self contribution survives until its own publication is consumed",
            freshHeld && !scene.AnyRecordDirty, $"freshHeld={freshHeld} remaining={scene.AnyRecordDirty}");
        snapshot.ReleaseResources();
    }

    static void BitsRetireIndependently()
    {
        var (scene, root, child) = Fixture();
        scene.Mark(child, NodeFlags.PaintDirty);
        scene.NotePublished(1);
        scene.Mark(child, NodeFlags.TransformDirty);
        scene.ClearRecordDirty(1);
        bool contentRetired = scene.RecordDirtySelfBits(child) == SceneStore.RecordDirtyTransform
            && scene.RecordDirtyDescendantBits(root) == SceneStore.RecordDirtyTransform;
        scene.NotePublished(2);
        scene.Mark(child, NodeFlags.PaintDirty);
        scene.ClearRecordDirty(2);
        bool transformRetired = scene.RecordDirtySelfBits(child) == SceneStore.RecordDirtyContent
            && scene.RecordDirtyDescendantBits(root) == SceneStore.RecordDirtyContent;
        Check("gate.damage.content-transform-lifetimes newer transform and content writes retire the other consumed bit independently",
            contentRetired && transformRetired, $"contentRetired={contentRetired} transformRetired={transformRetired}");
    }

    static void RepeatedChildDamageStaysLocal()
    {
        var (scene, root, child) = Fixture();
        var dl = new DrawList();
        var spans = new SpanTable();
        SceneRecorder.Record(scene, dl, spans: spans);
        scene.Mark(root, NodeFlags.PaintDirty);
        scene.NotePublished(1);
        bool local = true;
        float maxCoverage = 0f;
        for (ulong seq = 2; seq <= 130; seq++)
        {
            scene.Paint(child).Opacity = (seq & 1) == 0 ? .8f : 1f;
            scene.Mark(child, NodeFlags.PaintDirty);
            scene.ClearRecordDirty(seq - 1);
            dl.Reset();
            var record = SceneRecorder.Record(scene, dl, spans: spans);
            float coverage = record.RepaintDamage.Coverage(400, 300);
            maxCoverage = Math.Max(maxCoverage, coverage);
            local &= !record.RepaintDamage.IsFull && coverage > 0f && coverage < .1f
                && scene.RecordDirtySelfBits(root) == 0 && scene.RecordDirtyDescendantBits(root) != 0;
            scene.NotePublished(seq);
        }
        Check("gate.damage.continuous-child-localized continuous child paint after navigation never retains the ancestor-sized repaint",
            local, $"128 frames maxCoverage={maxCoverage:P2}");

        // Warmed scene ledgers are reused; measure mark/retirement only, not recorder cold work.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong seq = 131; seq <= 258; seq++)
        {
            scene.Mark(child, NodeFlags.PaintDirty);
            scene.ClearRecordDirty(seq - 1);
            scene.NotePublished(seq);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.damage.dirty-retirement-alloc-zero independent dirty lifetimes allocate nothing on steady marks and consumption",
            allocated == 0, $"allocated={allocated}");
    }

    static void GrowthTrimAndReuse()
    {
        var (scene, root, child) = Fixture();
        var extra = new NodeHandle[1024];
        for (int i = 0; i < extra.Length; i++) extra[i] = scene.CreateNode(1);
        var high = extra[^1];
        scene.ClearRecordDirty();
        scene.Mark(high, NodeFlags.PaintDirty);
        scene.NotePublished(1);
        scene.Mark(high, NodeFlags.TransformDirty);
        scene.ClearRecordDirty(1);
        bool grew = scene.RecordDirtySelfBits(high) == SceneStore.RecordDirtyTransform;
        foreach (var node in extra) scene.FreeSubtree(node);
        scene.ClearRecordDirty();
        int trimmed = scene.TrimExcessCapacity();
        scene.Mark(root, NodeFlags.PaintDirty);
        scene.NotePublished(2);
        scene.Mark(child, NodeFlags.TransformDirty);
        scene.ClearRecordDirty(2);
        bool preserved = scene.RecordDirtySelfBits(root) == 0
            && scene.RecordDirtyDescendantBits(root) == SceneStore.RecordDirtyTransform;
        scene.ClearRecordDirty();
        var reused = scene.CreateNode(1);
        scene.ClearRecordDirty();
        scene.Mark(reused, NodeFlags.PaintDirty);
        scene.NotePublished(3);
        scene.ClearRecordDirty(3);
        Check("gate.damage.dirty-lifetime-capacity stamps survive growth and trimming without leaking into recycled nodes",
            grew && trimmed > 0 && preserved && !scene.AnyRecordDirty,
            $"grew={grew} trimmed={trimmed} preserved={preserved} remaining={scene.AnyRecordDirty}");
    }
}
