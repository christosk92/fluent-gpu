using System;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

/// <summary>
/// The render seam under a PUBLICATION GAP — the state the hosted path spends most of its scrolling life in. The UI
/// publishes at its own rate and <see cref="SceneFramePublisher.TryAcquire"/> is last-writer-wins, so the renderer
/// routinely skips publications; a gap is the NORMAL case, not a fault.
///
/// Two things used to treat it as a fault, and both fired hardest exactly when the renderer was already behind: the
/// publisher forced <see cref="RepaintFullReason.TargetInvalidated"/> on EVERY scene adoption (partial repaint was dead
/// in the hosted path), and the scene frame's record OR-ed <see cref="SpanReuseDisabledReason.SceneChanged"/> in plus
/// seeded whole-root damage, so a skipped publication bought a zero-reuse full re-record. Positive feedback: falling
/// behind made the next frame the most expensive one available.
///
/// What makes a gap safe is a contract, not a flag: the host holds the scene's record-dirty bits and its pending-removal
/// ledger open until <see cref="SceneFramePublisher.LastConsumedSeq"/> catches up, so a snapshot published across a gap
/// carries the UNION of every delta since the last CONSUMED publication. The span table's freshness test counts RECORD
/// frames (advanced once per record, on the consumer), so publications that were never recorded never aged it. The two
/// ledgers that are per-PUBLICATION rather than per-scene — the structural-cancel damage rects and the repaint region —
/// ride across the gap inside the publisher. These gates pin all three.
/// </summary>
static class PublicationGapChecks
{
    sealed class NoDecoder : IImageDecoder
    {
        public bool Begin(int id, string source, int targetW, int targetH,
                          ImagePriority priority = ImagePriority.Visible) => false;
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
    }

    const float TW = 480f, TH = 320f;

    static bool CoveredBy(RepaintDamageRegion region, in RectF r)
    {
        foreach (ref readonly var m in region.AsSpan())
            if (m.X <= r.X && m.Y <= r.Y && m.X + m.W >= r.X + r.W && m.Y + m.H >= r.Y + r.H) return true;
        return false;
    }

    static NodeHandle AddBox(SceneStore scene, NodeHandle parent, in RectF bounds, in ColorF fill)
    {
        var n = scene.CreateNode(1);
        scene.AppendChild(parent, n);
        scene.Bounds(n) = bounds;
        ref NodePaint p = ref scene.Paint(n);
        p = NodePaint.Default;
        p.VisualKind = VisualKind.Box;
        p.Fill = fill;
        p.Corners = new CornerRadius4(4f, 4f, 4f, 4f);
        return n;
    }

    public static void Run()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);

        var scene = new SceneStore();
        scene.DeviceScale = 1f;
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new RectF(0f, 0f, TW, TH);
        { ref NodePaint p = ref scene.Paint(root); p = NodePaint.Default; p.VisualKind = VisualKind.Box; p.Fill = new ColorF(0.05f, 0.05f, 0.06f, 1f); }
        // Enough nested subtrees that a clean frame has real spans to reuse — a gate that reuses nothing passes vacuously.
        for (int i = 0; i < 8; i++)
        {
            var row = AddBox(scene, root, new RectF(8f, 8f + i * 34f, TW - 16f, 28f), new ColorF(0.30f, 0.32f, 0.36f, 1f));
            AddBox(scene, row, new RectF(6f, 4f, 20f, 20f), new ColorF(0.6f, 0.6f, 0.7f, 1f));
            AddBox(scene, row, new RectF(34f, 4f, 120f, 20f), new ColorF(0.2f, 0.4f, 0.6f, 1f));
        }

        var publisher = new SceneFramePublisher(1, 1);
        var images = new ImageCache(new NoDecoder());
        var strings = new StringTable();
        var animation = new AnimEngine(scene);
        var detached = new DetachedAnimSlab();
        var popups = Array.Empty<PopupWindowSlot>();
        var commands = new DrawList();
        var spans = new SpanTable();

        void Publish(in RectF repaintRect, in RectF structuralRect)
        {
            var region = default(RepaintDamageRegion);
            region.Add(in repaintRect);
            var submit = new FrameInfo(new Size2(TW, TH), 1f, default, default, 0f, 0, false, region);
            Span<RectF> structural = stackalloc RectF[1];
            structural[0] = structuralRect;
            publisher.PublishScene(scene, images, strings, default, default, default, structural, detached, popups,
                animation, in submit, suppressVsync: false, interactivePresent: false);
        }

        // Frame 1: the baseline the renderer actually consumed. It primes the span table and, exactly as the host does
        // after a consumed publication, clears the scene's record-dirty bits and its removal ledger.
        var seed = new RectF(0f, 0f, TW, TH);
        Publish(in seed, in seed);
        bool seeded = publisher.TryAcquire(out var baseFrame) && baseFrame.HasScene;
        var baseStats = seeded ? publisher.Scene(baseFrame).Record(commands, spans, publicationGap: false) : default;
        scene.ClearRecordDirty();
        scene.ClearPendingRemovals();

        // Frames 2, 3, 4: published with the consumer parked. Nothing in the scene changes; each publication names its
        // own repaint band and its own structural-cancel band.
        var r2 = new RectF(4f, 4f, 12f, 12f);
        var r3 = new RectF(220f, 140f, 12f, 12f);
        var r4 = new RectF(440f, 290f, 12f, 12f);
        var s2 = new RectF(30f, 30f, 10f, 10f);
        var s3 = new RectF(150f, 200f, 10f, 10f);
        var s4 = new RectF(300f, 60f, 10f, 10f);
        Publish(in r2, in s2);
        Publish(in r3, in s3);
        Publish(in r4, in s4);

        bool adopted = publisher.TryAcquire(out var gapFrame);
        var carried = adopted ? gapFrame.Submit.RepaintDamage : default;
        Check("gate.repaint.publication-gap-carries-repaint TWO skipped scene publications hand their repaint bands to the frame the renderer DOES adopt, and that frame stays PARTIAL — the publisher used to ForceFull(TargetInvalidated) on every scene adoption, which killed partial repaint in the hosted path outright",
            adopted && gapFrame.PublishSeq == 4 && !carried.IsFull
            && CoveredBy(carried, in r2) && CoveredBy(carried, in r3) && CoveredBy(carried, in r4)
            && gapFrame.Submit.CarriedFromSeq == 2,
            $"seq={gapFrame.PublishSeq} full={carried.IsFull}/{carried.FullReason} rects={carried.Count} carriedFrom={gapFrame.Submit.CarriedFromSeq}");

        // Recording the adopted frame: the gap must not disable span reuse, and the structural-cancel rects of the two
        // SKIPPED publications must still reach the recorder (their own slots were recycled — the publisher carries them).
        var gapStats = adopted ? publisher.Scene(gapFrame).Record(commands, spans, publicationGap: true) : default;
        Check("gate.repaint.publication-gap-keeps-span-reuse a gap is NOT a reuse killer: the adopted snapshot carries the UNION of the record-dirty bits since the last CONSUMED publication and the span table ages by RECORD frames, so reuse stays valid and SpanReuseDisabledReason.SceneChanged is never raised for a skipped publication",
            adopted && (gapStats.SpanReuseDisabledReasons & SpanReuseDisabledReason.SceneChanged) == 0
            && gapStats.SpansReused > 0 && baseStats.NodesVisited > 0,
            $"disabled={gapStats.SpanReuseDisabledReasons} reused={gapStats.SpansReused} reRecorded={gapStats.SpansReRecorded} baseVisited={baseStats.NodesVisited}");

        Check("gate.repaint.publication-gap-carries-structural-damage structural-cancel damage is a PER-PUBLICATION ledger and a skipped publication's slot is recycled — so the rects it named ride forward and land in the recorded repaint set, instead of being replaced wholesale by whole-root gap damage",
            adopted && !gapStats.RepaintDamage.IsFull
            && CoveredBy(gapStats.RepaintDamage, in s2) && CoveredBy(gapStats.RepaintDamage, in s3)
            && CoveredBy(gapStats.RepaintDamage, in s4),
            $"full={gapStats.RepaintDamage.IsFull}/{gapStats.RepaintDamage.FullReason} rects={gapStats.RepaintDamage.Count}");

        // The flag is a DIAGNOSTIC, not an input: re-recording the same frame with it cleared must reach the same reuse
        // verdict. If it ever influences recording again, these two disagree.
        var again = adopted ? publisher.Scene(gapFrame).Record(commands, spans, publicationGap: false) : default;
        Check("gate.repaint.publication-gap-flag-is-inert the publicationGap argument only records a diagnostic on the frame — recording the same snapshot with it set and with it cleared produces the same reuse verdict and the same disable-reason set",
            adopted && again.SpanReuseDisabledReasons == gapStats.SpanReuseDisabledReasons
            && again.SpansReused == gapStats.SpansReused && !publisher.Scene(gapFrame).PublicationGap,
            $"gap={gapStats.SpansReused}/{gapStats.SpanReuseDisabledReasons} clear={again.SpansReused}/{again.SpanReuseDisabledReasons}");

        // Steady state: the damage carry is the new per-publish work, and it must reuse its buffer rather than mint one
        // array per skipped frame.
        // Warm past the carry cap (32) for every publisher slot: each slot's damage buffer grows once to the cap.
        for (int i = 0; i < 128; i++) Publish(in r2, in s2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) Publish(in r2, in s2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.repaint.publication-gap-alloc-zero carrying damage across skipped publications is steady-state allocation-free — the carry buffer is grown once and reused, never a per-publication array",
            allocated == 0, $"{allocated} bytes over 64 skipped publications");

        publisher.ReleaseSceneResources();
    }
}
