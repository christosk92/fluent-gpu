using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class SceneSnapshotChecks
{
    public static void Run()
    {
        Isolation();
        Reachability();
        SpanLifetime();
        StringLifetime();
        SteadyAllocation();
        ReferencedImageCapture();
        StringRetentionSteady();
        ScrollBindCaptureGated();
        IncrementalCoast();
        IncrementalGapUnion();
        IncrementalAddRemoveParity();
        IncrementalStringRefcountNoChurn();
        IncrementalCapacityTrimFallsBack();
    }

    // ══ P8: incremental scene capture for coast frames ═══════════════════════════════════════════════════════════
    // The invariant, in every gate below: an incremental refresh must be column-for-column indistinguishable from a
    // from-scratch capture of the same store at the same instant (SceneRecordingSnapshot.EqualsForParity). What it may
    // buy is work, never accuracy - so each gate asserts parity FIRST and the cost saving second.

    /// <summary>One publication into <paramref name="snap"/>, wired exactly like AppHost's: capture incrementally when
    /// the store can prove the delta, else in full; compare against a from-scratch capture taken at that same instant;
    /// then advance the publication clock and drop the ledgers the way the host does once every slot has consumed.</summary>
    static bool Publish(SceneStore scene, SceneRecordingSnapshot snap, ref ulong baseline,
                        out bool incremental, out int copied, out string mismatch)
    {
        incremental = snap.CaptureIncremental(scene, default, baseline);
        if (!incremental) snap.Capture(scene);
        copied = snap.CopiedNodeCount;

        var scratch = new SceneRecordingSnapshot();
        scratch.Capture(scene);
        bool parity = snap.EqualsForParity(scratch, out mismatch);
        scratch.ReleaseResources();

        baseline = snap.LastCaptureSeq;
        scene.NotePublished(baseline);
        scene.ClearTransformDirty();
        scene.ClearPendingRemovals(baseline);
        scene.ClearRecordDirty(baseline);
        scene.ClearCaptureLedger(baseline);   // single-slot simulation: this snapshot IS the oldest slot
        return parity;
    }

    /// <summary>One kernel scroll write, wired exactly like <c>SceneScrollSink.Apply</c>: the offset through the
    /// token-gated <c>ApplyMotion</c>, the content node's transform, and the TransformDirty/PaintDirty marks. This is
    /// the whole of what a coast frame does to the store.</summary>
    static void ScrollTo(SceneStore scene, NodeHandle viewport, NodeHandle content, float offsetY, uint frame)
    {
        ref var sc = ref scene.ScrollRefByIndex((int)viewport.Raw.Index);
        sc.ApplyMotion(FluentGpu.Scroll.SceneScrollSink.ScrollWriteToken.Mint(frame),
            new FluentGpu.Scroll.ScrollWrite(0f, offsetY, 0f, 0f, 1f, 0f, 0f,
                FluentGpu.Scroll.ScrollActivity.Ballistic, default, FluentGpu.Scroll.ScrollWriteMask.OffsetY, 0f,
                FluentGpu.Scroll.ScrollWriteSource.Tick));
        scene.Paint(content).LocalTransform = FluentGpu.Foundation.Affine2D.Translation(0f, -offsetY);
        scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    /// <summary>A page: a scroll viewport over a content node with 200 rows, plus some sibling chrome.</summary>
    static SceneStore BuildScrollPage(out NodeHandle viewport, out NodeHandle content, out NodeHandle[] rows)
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var chrome = scene.CreateNode(2);
        scene.AppendChild(root, chrome);
        scene.Paint(chrome).VisualKind = VisualKind.Box;
        viewport = scene.CreateNode(3);
        scene.AppendChild(root, viewport);
        content = scene.CreateNode(4);
        scene.AppendChild(viewport, content);
        ref var sc = ref scene.ScrollRef(viewport);
        sc.ContentNode = content;
        sc.ItemCount = 200;
        sc.ViewportH = 600;
        sc.ContentH = 200 * 40;
        rows = new NodeHandle[200];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = scene.CreateNode(5);
            scene.AppendChild(content, rows[i]);
            scene.Paint(rows[i]).VisualKind = VisualKind.Box;
            scene.Bounds(rows[i]) = new(0, i * 40, 300, 40);
        }
        return scene;
    }

    // ── gate.capture.incremental-coast ────────────────────────────────────────────────────────────────────────────
    // A scroll-only frame: the kernel writes the viewport's offset and the content node's shift transform and marks it
    // TransformDirty. Nothing else in the store moved - no reconcile, no layout - so the capture must copy the touched
    // chain and nothing else, while still matching a full capture exactly.
    static void IncrementalCoast()
    {
        var scene = BuildScrollPage(out var viewport, out var content, out _);
        var snap = new SceneRecordingSnapshot();
        ulong baseline = 0;
        bool parity = true;
        int fullCopied = 0;
        for (int i = 0; i < 3; i++)
        {
            parity &= Publish(scene, snap, ref baseline, out _, out int c, out _);
            if (i == 0) fullCopied = c;   // the FIRST publication is the full one this gate measures against
        }

        ScrollTo(scene, viewport, content, 120f, 4);   // the coast frame itself
        parity &= Publish(scene, snap, ref baseline, out bool incremental, out int copied, out string mismatch);

        Check("gate.capture.incremental-coast a scroll-only frame (no reconcile, no layout) publishes incrementally: <= 8 nodes COPIED where a full capture copies the whole reachable tree, with column-level parity against a from-scratch capture",
            parity && incremental && copied <= 8 && fullCopied > 200 && snap.IncrementalParityFailures == 0,
            $"incremental={incremental} copiedOnCoast={copied} copiedOnFull={fullCopied} parity={parity} " +
            $"debugSelfHeals={snap.IncrementalParityFailures} (verifier compiledIn={SceneRecordingSnapshot.ParityVerifyCompiledIn}) {mismatch}");
        snap.ReleaseResources();
    }

    // ── gate.capture.gap-union ────────────────────────────────────────────────────────────────────────────────────
    // A publisher slot is not written every frame (three slots, one publication each). While a slot sits out, the store
    // keeps changing - and the store's ledgers are retained to the OLDEST slot's baseline precisely so that slot's next
    // incremental capture picks up the UNION of everything that changed across the gap.
    static void IncrementalGapUnion()
    {
        var scene = BuildScrollPage(out var viewport, out var content, out var rows);
        var snap = new SceneRecordingSnapshot();
        ulong baseline = 0;
        bool parity = true;
        for (int i = 0; i < 3; i++) parity &= Publish(scene, snap, ref baseline, out _, out _, out _);
        ulong staleBaseline = baseline;

        // Two publications this slot SKIPS: the store advances and the ledgers are trimmed only to the stale baseline,
        // exactly as AppHost trims to min(consumed, oldest slot).
        for (int gap = 0; gap < 2; gap++)
        {
            scene.Paint(rows[10 + gap]).Opacity = 0.5f - gap * 0.1f;
            scene.Mark(rows[10 + gap], NodeFlags.PaintDirty);
            scene.Bounds(rows[20 + gap]) = new(0, 999 + gap, 300, 40);
            scene.Mark(rows[20 + gap], NodeFlags.PaintDirty);
            scene.NotePublished(scene.PublishSeq + 1);
            scene.ClearTransformDirty();
            scene.ClearPendingRemovals(staleBaseline);
            scene.ClearRecordDirty(staleBaseline);
            scene.ClearCaptureLedger(staleBaseline);
        }
        // One more change, then the slot finally captures again - against its OWN, now two-publications-old baseline.
        ScrollTo(scene, viewport, content, 80f, 6);
        parity &= Publish(scene, snap, ref baseline, out bool incremental, out int copied, out string mismatch);

        Check("gate.capture.gap-union a slot that skipped two publications still refreshes incrementally against its OWN older baseline and reaches full parity - the ledgers carry the UNION of everything that changed across the gap",
            parity && incremental && copied >= 4,
            $"incremental={incremental} copied={copied} parity={parity} {mismatch}");
        snap.ReleaseResources();
    }

    // ── gate.capture.add-remove-parity ────────────────────────────────────────────────────────────────────────────
    // Structural churn is the hardest case for a differential copy: an appended child rewrites its new previous
    // sibling's NextSibling, a freed subtree turns whole slots dead, and a recycled index must never inherit its
    // predecessor's sparse payload. None of that goes through a bulk mutation - it is all precise ledger entries.
    static void IncrementalAddRemoveParity()
    {
        var scene = BuildScrollPage(out _, out var content, out var rows);
        var snap = new SceneRecordingSnapshot();
        ulong baseline = 0;
        bool parity = true;
        for (int i = 0; i < 3; i++) parity &= Publish(scene, snap, ref baseline, out _, out _, out _);

        int incrementalPasses = 0;
        string mismatch = "";
        var added = new System.Collections.Generic.List<NodeHandle>();
        for (int pass = 0; pass < 6; pass++)
        {
            if ((pass & 1) == 0)
            {
                var fresh = scene.CreateNode(6);
                scene.AppendChild(content, fresh);
                scene.Paint(fresh).VisualKind = VisualKind.Box;
                scene.SetGradient(fresh, GradientSpec.Vertical(new(1, 0, 0, 1), new(0, 1, 0, 1)));
                added.Add(fresh);
            }
            else
            {
                scene.FreeSubtree(rows[pass]);                  // a real removal (ledger + dead slots)
                if (added.Count > 0)
                {
                    scene.FreeSubtree(added[0]);                // free a slot the next CreateNode will RECYCLE
                    added.RemoveAt(0);
                }
            }
            bool ok = Publish(scene, snap, ref baseline, out bool incremental, out _, out string m);
            parity &= ok;
            if (!ok && mismatch.Length == 0) mismatch = m;
            if (incremental) incrementalPasses++;
        }

        Check("gate.capture.add-remove-parity adds and removes (including a RECYCLED slot inheriting a freed index) interleaved with incremental captures keep column-level parity with a from-scratch capture every single pass",
            parity && incrementalPasses == 6 && snap.IncrementalParityFailures == 0,
            $"parity={parity} incrementalPasses={incrementalPasses}/6 debugSelfHeals={snap.IncrementalParityFailures} {mismatch}");
        snap.ReleaseResources();
    }

    // ── gate.capture.string-refcount-no-churn ─────────────────────────────────────────────────────────────────────
    // String retention is derived from the CAPTURED SET (which an incremental capture still resolves exactly), not from
    // the nodes it copied. An unchanged frame must therefore retain the same ids and touch neither Pin nor Unpin.
    static void IncrementalStringRefcountNoChurn()
    {
        var strings = new StringTable();
        var scene = BuildScrollPage(out var viewport, out var content, out var rows);
        var text = strings.Intern("a retained row label");
        var family = strings.Intern("a retained font family");
        strings.AddRef(text); strings.AddRef(family);
        for (int i = 0; i < 8; i++)
        {
            scene.Paint(rows[i]).VisualKind = VisualKind.Text;
            scene.Paint(rows[i]).Text = text;
            scene.Layout(rows[i]).TextStyle = new(family, 12, 400);
        }
        var snap = new SceneRecordingSnapshot();
        ulong baseline = 0;
        bool parity = true;
        for (int i = 0; i < 3; i++)
        {
            parity &= Publish(scene, snap, ref baseline, out _, out _, out _);
            snap.RetainStrings(strings);
        }
        int warmRetained = snap.RetainedStringCount;
        snap.ResetStringRetentionOps();

        // Four incremental coast frames: scroll only, nothing textual changes.
        int incrementalFrames = 0;
        for (int i = 0; i < 4; i++)
        {
            ScrollTo(scene, viewport, content, 40f * (i + 1), (uint)(10 + i));
            parity &= Publish(scene, snap, ref baseline, out bool incremental, out _, out _);
            if (incremental) incrementalFrames++;
            snap.RetainStrings(strings);
        }

        Check("gate.capture.string-refcount-no-churn four incremental coast captures retain a STABLE string set with ZERO Pin/Unpin churn (retention follows the captured set, not the copied nodes)",
            parity && incrementalFrames == 4 && snap.RetainedStringCount == warmRetained && snap.StringRetentionOps == 0,
            $"incrementalFrames={incrementalFrames}/4 retained {warmRetained}->{snap.RetainedStringCount} pinUnpinOps={snap.StringRetentionOps} parity={parity}");
        snap.ReleaseResources();
        strings.Release(text); strings.Release(family);
    }

    // ── gate.capture.capacity-trim-falls-back ─────────────────────────────────────────────────────────────────────
    // SceneStore.TrimExcessCapacity cuts the all-free column tail and lowers the node high-water. Slots this snapshot
    // still describes vanish, and every column array is reallocated - a delta the ledger cannot express. The
    // incremental path must DETECT that and refuse, and the full capture it falls back to must still be right.
    static void IncrementalCapacityTrimFallsBack()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var keep = scene.CreateNode(2);
        scene.AppendChild(root, keep);
        var bulk = new NodeHandle[3000];
        for (int i = 0; i < bulk.Length; i++)
        {
            bulk[i] = scene.CreateNode(3);
            scene.AppendChild(root, bulk[i]);
        }
        var snap = new SceneRecordingSnapshot();
        ulong baseline = 0;
        bool parity = true;
        for (int i = 0; i < 3; i++) parity &= Publish(scene, snap, ref baseline, out _, out _, out _);
        int capacityBefore = scene.Capacity;

        for (int i = 0; i < bulk.Length; i++) scene.FreeSubtree(bulk[i]);
        parity &= Publish(scene, snap, ref baseline, out _, out _, out _);   // the removals themselves
        int trimmed = scene.TrimExcessCapacity();

        bool canIncremental = snap.CanCaptureIncremental(scene, baseline);
        parity &= Publish(scene, snap, ref baseline, out bool incremental, out _, out string mismatch);

        Check("gate.capture.capacity-trim-falls-back a column trim (TrimExcessCapacity shrinks the node high-water and reallocates every column) makes the incremental path report itself invalid, and the full capture it falls back to is still exact",
            trimmed > 0 && !canIncremental && !incremental && parity,
            $"trimmedSlots={trimmed} capacity {capacityBefore}->{scene.Capacity} canIncremental={canIncremental} tookIncremental={incremental} parity={parity} {mismatch}");
        snap.ReleaseResources();
    }

    static void Isolation()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        var child = scene.CreateNode(2);
        scene.Root = root;
        scene.AppendChild(root, child);
        scene.Bounds(root) = new(10, 20, 200, 100);
        scene.Bounds(child) = new(3, 4, 20, 10);
        scene.Paint(child).VisualKind = VisualKind.Text;
        scene.Paint(child).Opacity = .75f;
        var stops = new[] { new GradientStop(0, new ColorF(1, 0, 0, 1)), new GradientStop(1, new ColorF(0, 0, 1, 1)) };
        scene.SetGradient(child, new(GradientShape.Linear, 90, stops));
        scene.SetTextEditRects(child, [new(1, 2, 3, 4)], [new(5, 6, 7, 8)]);
        scene.MeasureCacheRef(child).E0.FitSize = 12;
        scene.MeasureCacheRef(child).E0.Valid = true;
        ref var scroll = ref scene.ScrollRef(root);
        scroll.ContentNode = child;
        scroll.SnapPoints = [1, 2];
        scroll.ScrollKey = "source-only";
        scroll.ItemCount = 10;
        scroll.PersistentPrefixCount = 1;
        scroll.ItemClipTopInset = 8;
        scroll.ItemClipTopFadeBand = 4;
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.Bounds(child) = new(99, 99, 1, 1);
        scene.Paint(child).Opacity = .1f;
        stops[0] = new GradientStop(0, new ColorF(0, 1, 0, 1));
        scene.SetTextEditRects(child, [new(9, 9, 9, 9)], []);
        scene.MeasureCacheRef(child).E0.FitSize = 30;
        var copiedMeasure = snapshot.ResolveMeasureForWidth(child, 0);
        copiedMeasure.FitSize = 15;
        bool detached = snapshot.Bounds(child) == new RectF(3, 4, 20, 10)
            && snapshot.AbsoluteRect(child) == new RectF(13, 24, 20, 10)
            && snapshot.Paint(child).Opacity == .75f
            && snapshot.TryGetGradient(child, out var gradient) && gradient.Stops[0].Color.R == 1
            && snapshot.GetTextEditSelectionRects(child)[0] == new RectF(1, 2, 3, 4)
            && snapshot.GetTextEditUnderlineRects(child)[0] == new RectF(5, 6, 7, 8)
            && scene.MeasureCacheRef(child).E0.FitSize == 30
            && snapshot.ResolveMeasureForWidth(child, 0).FitSize == 12 && copiedMeasure.FitSize == 15
            && snapshot.ScrollRef(root).SnapPoints is null && snapshot.ScrollRef(root).ScrollKey is null
            && snapshot.TryGetVirtualItemBand(child, out int prefix, out float inset, out float fade)
            && prefix == 1 && inset == 8 && fade == 4;
        Check("gate.scene-snapshot-isolation", detached,
            "Topology, paint, gradient stops, text rectangles and measurement state do not alias UI mutation.");

        scene.FreeSubtree(child);
        var second = new SceneRecordingSnapshot();
        second.Capture(scene);
        Check("gate.scene-snapshot-free", snapshot.IsLive(child) && !second.IsLive(child)
            && snapshot.FirstChild(root) == child && second.FirstChild(root).IsNull
            && second.PendingRemovalExtents.Length > 0,
            "Freeing the source does not invalidate a retained snapshot; the next capture includes removal damage.");
        var replacement = scene.CreateNode(2);
        scene.AppendChild(root, replacement);
        second.Capture(scene);
        Check("gate.scene-snapshot-generation", replacement.Raw.Index == child.Raw.Index
            && replacement.Raw.Gen != child.Raw.Gen && second.IsLive(replacement) && !second.IsLive(child)
            && !second.TryGetGradient(replacement, out _) && second.GetTextEditSelectionRects(replacement).IsEmpty,
            "A reused slot cannot inherit stale sparse visual payloads or its predecessor's identity.");
        snapshot.ReleaseResources(); second.ReleaseResources();
    }

    static void Reachability()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var live = new NodeHandle[8];
        for (int i = 0; i < live.Length; i++) { live[i] = scene.CreateNode(2); scene.AppendChild(root, live[i]); }
        // A kept-alive page: attached, populated, then detached from the root exactly as the reconciler parks it.
        var parked = scene.CreateNode(3);
        scene.AppendChild(root, parked);
        var parkedRows = new NodeHandle[20_000];
        for (int i = 0; i < parkedRows.Length; i++)
        {
            parkedRows[i] = scene.CreateNode(2);
            scene.AppendChild(parked, parkedRows[i]);
            scene.Paint(parkedRows[i]).VisualKind = VisualKind.Text;
        }
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        bool attached = snapshot.CapturedNodeCount == 1 + live.Length + 1 + parkedRows.Length
            && snapshot.IsLive(parkedRows[^1]) && snapshot.Parent(parkedRows[0]) == parked;
        scene.Detach(parked);
        snapshot.Capture(scene);
        Check("gate.scene-snapshot-parked", attached && snapshot.CapturedNodeCount == 1 + live.Length
            && snapshot.IsLive(live[^1]) && !snapshot.IsLive(parked) && !snapshot.IsLive(parkedRows[0])
            && snapshot.Parent(parkedRows[0]).IsNull && snapshot.FirstChild(parked).IsNull,
            "Capture copies the reachable tree only; a parked page's slots read as dead and chain nowhere.");
        long detachedTicks = long.MaxValue, attachedTicks = long.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            snapshot.Capture(scene);
            detachedTicks = Math.Min(detachedTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
        scene.AppendChild(root, parked);
        for (int i = 0; i < 20; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            snapshot.Capture(scene);
            attachedTicks = Math.Min(attachedTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
        Check("gate.scene-snapshot-parked-cost", detachedTicks * 10 < attachedTicks && snapshot.IsLive(parkedRows[^1]),
            $"Publication cost follows the reachable tree, not the slot high-water mark (parked {detachedTicks} vs unparked {attachedTicks} ticks).");

        // Off-root roots: an exit orphan keeps its subtree, a connected overlay is standalone, a popup subtree root
        // under a detached host still resolves its absolute rect through the captured ancestor chain.
        scene.Detach(parked);
        var host = scene.CreateNode(4);
        scene.AppendChild(root, host);
        scene.Bounds(host) = new(10, 10, 100, 100);
        var exiting = scene.CreateNode(2);
        scene.AppendChild(host, exiting);
        var exitingChild = scene.CreateNode(2);
        scene.AppendChild(exiting, exitingChild);
        scene.Orphan(exiting);
        var overlay = scene.CreateNode(2);
        scene.AddOverlay(overlay);
        var popupHost = scene.CreateNode(2);
        var popupRoot = scene.CreateNode(2);
        scene.AppendChild(popupHost, popupRoot);
        scene.Bounds(popupHost) = new(40, 0, 10, 10);
        scene.Bounds(popupRoot) = new(1, 2, 3, 4);
        snapshot.Capture(scene, [popupRoot]);
        Check("gate.scene-snapshot-roots", snapshot.IsLive(exiting) && snapshot.IsLive(exitingChild)
            && snapshot.OrphanChildrenOf(host) is { Count: 1 } orphans && orphans[0] == exiting
            && snapshot.IsLive(overlay) && snapshot.IsLive(popupRoot) && snapshot.IsLive(popupHost)
            && snapshot.AbsoluteRect(popupRoot) == new RectF(41, 2, 3, 4)
            && snapshot.CapturedNodeCount == 1 + live.Length + 1 + 2 + 1 + 2,
            "Orphans, overlays, extra roots and their ancestors are captured; nothing else is.");
        snapshot.ReleaseResources();
    }

    static void SpanLifetime()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(2);
        scene.Root = root;
        scene.Paint(root).VisualKind = VisualKind.Text;
        SpanStyle[] styles = [new(0, 3, 400, 12, default, new(1, 0, 0, 1), SpanStyle.UnderlineBit)];
        int id = SpanRunTable.Shared.Create(styles);
        SpanRunTable.Shared.AddRef(id);
        var run = SpanRunTable.Shared.Resolve(id)!;
        run.PublishRects(new(100, [new(new(0, 10, 30, 1), 0, SpanStyle.UnderlineBit)]));
        scene.Layout(root).TextStyle = new(default, 12, 400, SpanRunId: id);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        run.PublishRects(new(200, [new(new(0, 20, 60, 2), 0, SpanStyle.UnderlineBit)]));
        scene.FreeSubtree(root); scene.Root = NodeHandle.Null;
        for (int i = 0; i < 300; i++)
        {
            int churn = SpanRunTable.Shared.Create([]);
            SpanRunTable.Shared.AddRef(churn); SpanRunTable.Shared.Release(churn);
        }
        Check("gate.scene-snapshot-span-retain", SpanRunTable.Shared.Resolve(id) is not null
            && snapshot.TryGetSpanDecorations(root, out var copiedStyles, out var copiedRects)
            && copiedStyles[0].Color.R == 1 && copiedRects[0].Rect == new RectF(0, 10, 30, 1),
            "A retained frame survives span-table creation-distance churn and later layout publication.");
        snapshot.ReleaseResources();
        for (int i = 0; i < 300; i++)
        {
            int churn = SpanRunTable.Shared.Create([]);
            SpanRunTable.Shared.AddRef(churn); SpanRunTable.Shared.Release(churn);
        }
        Check("gate.scene-snapshot-span-release", SpanRunTable.Shared.Resolve(id) is null,
            "UI retirement releases snapshot span ownership after the frame is no longer referenced.");
    }

    static void SteadyAllocation()
    {
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(2);
        scene.Paint(scene.Root).VisualKind = VisualKind.Text;
        scene.SetGradient(scene.Root, GradientSpec.Vertical(new(1, 0, 0, 1), new(0, 1, 0, 1)));
        scene.SetTextEditRects(scene.Root, [new(1, 2, 3, 4)], []);
        var snapshot = new SceneRecordingSnapshot();
        for (int i = 0; i < 32; i++) snapshot.Capture(scene);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) snapshot.Capture(scene);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.scene-snapshot-alloc", allocated == 0, $"Repeated capture allocated {allocated} bytes after warmup.");
        snapshot.ReleaseResources();
    }

    static void StringLifetime()
    {
        var strings = new StringTable();
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(2);
        var text = strings.Intern("retained old-frame text");
        var family = strings.Intern("retained old-frame font");
        var chrome = strings.Intern("retained scrollbar glyph");
        var permanent = strings.Intern("permanent theme family");
        var adopted = strings.Intern("owned after first frame pin");
        strings.AddRef(text); strings.AddRef(family); strings.AddRef(chrome);
        scene.Paint(scene.Root).Text = text;
        scene.Layout(scene.Root).TextStyle = new(family, 12, 400);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        snapshot.RetainStrings(strings, [chrome, permanent, adopted]);
        strings.AddRef(adopted); strings.Release(adopted);
        var unrelated = strings.Intern("unrelated retired text");
        strings.AddRef(unrelated); strings.Release(unrelated);
        strings.Release(text); strings.Release(family); strings.Release(chrome);
        for (int i = 0; i < 100; i++) strings.Tick();
        Check("gate.scene-snapshot-string-retain", strings.Resolve(text) == "retained old-frame text"
            && strings.Resolve(family) == "retained old-frame font" && strings.Resolve(chrome) == "retained scrollbar glyph"
            && strings.Resolve(adopted) == "owned after first frame pin" && strings.Resolve(unrelated).Length == 0,
            "UI creation-distance reclamation cannot erase text, font, or host glyph ids owned by a retained frame.");
        snapshot.ReleaseResources();
        for (int i = 0; i < 20; i++) strings.Tick();
        Check("gate.scene-snapshot-string-release", strings.Resolve(text).Length == 0
            && strings.Resolve(family).Length == 0 && strings.Resolve(chrome).Length == 0 && strings.Resolve(adopted).Length == 0,
            "Snapshot string references release only on UI reclamation, after which ordinary quarantine applies.");
        Check("gate.scene-snapshot-string-permanent", strings.Resolve(permanent) == "permanent theme family"
            && strings.Intern("permanent theme family") == permanent,
            "Temporary frame pins never convert a permanent theme/font id into a reclaimable authored reference.");
    }

    // perf plan item 1: image capture narrows to the ids the recorder can actually draw (a VisualKind.Image node's
    // ImageId), not every entry the cache has ever seen — proven by (a) the referenced set + narrowed snapshot both
    // containing exactly the one drawn id, and (b) narrowed-capture cost staying flat whether the cache also holds
    // 5,000 unreferenced entries or none at all.
    static void ReferencedImageCapture()
    {
        var busyCache = new ImageCache(new FakeImageDecoder());
        for (int i = 0; i < 5000; i++) busyCache.Request($"unreferenced-{i}", 32, 32);
        busyCache.Pump();
        var referenced = busyCache.Request("referenced", 32, 32);
        busyCache.Pump();

        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Paint(root).VisualKind = VisualKind.Image;
        scene.Paint(root).ImageId = referenced.Id;
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        bool onlyReferenced = snapshot.ReferencedImageIds.Length == 1 && snapshot.ReferencedImageIds[0] == referenced.Id;

        var images = new ImageRecordingSnapshot();
        images.Capture(busyCache, snapshot.ReferencedImageIds);
        bool narrowedCount = images.Count == 1 && images.StateOf(referenced) == ImageState.Ready;

        long busyTicks = long.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            images.Capture(busyCache, snapshot.ReferencedImageIds);
            busyTicks = Math.Min(busyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }

        var quietCache = new ImageCache(new FakeImageDecoder());
        var solo = quietCache.Request("referenced", 32, 32);
        quietCache.Pump();
        Span<int> soloId = [solo.Id];
        long quietTicks = long.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            images.Capture(quietCache, soloId);
            quietTicks = Math.Min(quietTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
        bool flatCost = busyTicks <= Math.Max(quietTicks * 4, 1);
        Check("gate.scene-snapshot-image-referenced", onlyReferenced && narrowedCount && flatCost,
            $"5,000 cached-but-unreferenced images cost {busyTicks} ticks vs {quietTicks} for a cache holding only the referenced one; " +
            "the snapshot must hold exactly the referenced set either way.");
        snapshot.ReleaseResources();
    }

    // perf plan item 2: repeated RetainStrings on an unchanged scene is allocation-free and retains a STABLE set (no
    // Pin/Unpin churn) — the incremental epoch-stamped design replacing the old full HashSet rebuild-and-diff.
    static void StringRetentionSteady()
    {
        var strings = new StringTable();
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(2);
        var text = strings.Intern("steady retained text");
        var family = strings.Intern("steady retained font");
        strings.AddRef(text); strings.AddRef(family);
        scene.Paint(scene.Root).Text = text;
        scene.Layout(scene.Root).TextStyle = new(family, 12, 400);
        var snapshot = new SceneRecordingSnapshot();
        for (int i = 0; i < 8; i++) { snapshot.Capture(scene); snapshot.RetainStrings(strings); }
        int warmCount = snapshot.RetainedStringCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) { snapshot.Capture(scene); snapshot.RetainStrings(strings); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.scene-snapshot-string-retain-steady", allocated == 0 && snapshot.RetainedStringCount == warmCount,
            $"Repeated RetainStrings on an unchanged scene allocated {allocated} bytes (retained count {warmCount} -> {snapshot.RetainedStringCount}).");
        snapshot.ReleaseResources();
        strings.Release(text); strings.Release(family);
    }

    // perf plan item 3: ScrollBinds.CaptureChain is gated on NodeFlags.Scrollable — a chain head can only ever be
    // keyed by a scroller's node index (ScrollBind.cs's Add() keys _headByVp by the enclosing viewport), so probing a
    // non-scrollable node was always a guaranteed miss. Proven by an invocation counter, not a timing comparison.
    static void ScrollBindCaptureGated()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.ScrollRef(root);   // marks root NodeFlags.Scrollable
        for (int i = 0; i < 500; i++)
        {
            var child = scene.CreateNode(2);
            scene.AppendChild(root, child);
            scene.Paint(child).VisualKind = VisualKind.Box;
        }
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        Check("gate.scene-snapshot-scrollbind-gated", snapshot.ScrollBinds.CaptureChainCalls == 1,
            $"CaptureChain ran {snapshot.ScrollBinds.CaptureChainCalls} times for 1 scrollable node + 500 non-scrollable children; expected exactly 1.");
        snapshot.ReleaseResources();
    }
}
