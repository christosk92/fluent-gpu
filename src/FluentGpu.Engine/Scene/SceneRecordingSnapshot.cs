using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text;

namespace FluentGpu.Scene;

/// <summary>
/// Recording inputs copied from a committed UI scene. Capture is allowed only while the publisher owns the slot;
/// after publication the renderer owns its lease until release. No UI callbacks, hook cells, virtual layouts,
/// or live SceneStore references cross this boundary. Buffers grow at their high-water mark and are reused.
/// Resource ids and immutable PathData references require separate snapshot/fence lifetime retention by the host.
/// </summary>
public sealed partial class SceneRecordingSnapshot
{
    private NodeHandle[] _handles = [];
    private NodeHandle[] _parent = [], _firstChild = [], _nextSibling = [];
    // Recording consumes text styling, never flex/layout inputs. Nontext rows retain an authored font/span
    // identity too, preserving the existing resource-lifetime contract. Reserve first incremental removals.
    private readonly SnapshotColumn<TextStyle> _textStyle = new(reserveRemovals: true);
    private RectF[] _bounds = [];
    private NodePaint[] _paint = [];
    private InteractionInfo[] _interaction = [];
    private NodeFlags[] _flags = [];
    private byte[] _dirty = [], _dirtySelf = [], _dirtyDescendant = [];
    private readonly SnapshotColumn<TextMeasureCache> _measurement = new(reserveRemovals: true);
    private int _nodeCount;
    private readonly SnapshotColumn<ScrollState> _scroll = new();
    private readonly SnapshotColumn<RectBuffer> _selectionRects = new(), _underlineRects = new();
    private readonly SnapshotColumn<OrphanChildren> _orphanChildren = new();
    private readonly SnapshotColumn<SpanDecoration> _spanDecorations = new();
    // Scroll-rework Wave 0.E (scroll-rework-design.md §B.4): ListRowEl's ≤8-cell payload, captured/parity-checked/
    // freed the EXACT same way _spanDecorations is (a VisualKind-gated capture-time copy, not a NodeFlags.SparsePaint
    // row — ListRowEl has no other sparse paint of its own).
    private readonly SnapshotColumn<RowCellsCapture> _rowCells = new();
    private readonly HashSet<int> _retainedSpanRuns = new(), _seenSpanRuns = new();
    private readonly List<int> _releasedSpanRuns = new();
    private StringTable? _stringOwner; // UI-only retention bookkeeping; recorder never reads this table.
    // perf plan item 2: incremental retention. _stringSeenEpoch/_stringRetained are dense arrays indexed by StringId.Value
    // (ids are dense per Foundation/StringTable.cs); _retainedStrings/_previousRetainedStrings swap like _captured/
    // _previousCaptured above, so the common no-change capture touches neither a HashSet nor the full retained set.
    private uint[] _stringSeenEpoch = [];
    private bool[] _stringRetained = [];
    private uint _stringEpoch;
    private List<StringId> _retainedStrings = new(), _previousRetainedStrings = new();
    private Orphan[] _orphans = [];
    private NodeHandle[] _overlays = [], _spotlights = [];
    private SceneStore.RemovedNodeExtent[] _removals = [];
    private int _removalCount;
    private readonly SnapshotColumn<InteractionAnim> _interact = new();
    private readonly SnapshotColumn<ShadowSpec> _shadow = new();
    private readonly SnapshotColumn<ArcSpec> _arc = new();
    private readonly SnapshotColumn<PolylineStrokeSpec> _polyline = new();
    private readonly SnapshotColumn<PathSpec> _path = new();
    private readonly SnapshotColumn<ClipPathSpec> _clipPath = new();
    private readonly SnapshotColumn<Point2> _radialCenter = new();
    private readonly SnapshotColumn<AcrylicSpec> _acrylic = new();
    private readonly SnapshotColumn<EdgeFadeSpec> _edgeFade = new();
    private readonly SnapshotColumn<ImageVisualEffects> _imageEffects = new();
    private readonly SnapshotColumn<BrushAnim> _brushAnim = new();
    private readonly SnapshotColumn<TextEditState> _textEdit = new();
    private readonly SnapshotColumn<ColorF> _selectionHighlight = new();
    private readonly SnapshotColumn<GlyphWipe> _glyphWipe = new();
    private readonly SnapshotColumn<GradientSpec> _Gradient = new();
    private readonly SnapshotColumn<GradientSpec> _BorderBrush = new();
    private readonly SnapshotColumn<GradientSpec> _HoverGradient = new();
    private readonly SnapshotColumn<GradientSpec> _PressedGradient = new();
    private readonly SnapshotColumn<GradientSpec> _HoverBorderBrush = new();
    private readonly SnapshotColumn<GradientSpec> _PressedBorderBrush = new();

    internal FluentGpu.Render.SceneRecordingContext Recording { get; } = new();
    public NodeHandle Root { get; private set; }
    public int Capacity => _handles.Length;
    public float DeviceScale { get; private set; }
    public NodeHandle DragGhost { get; private set; }
    public ColorF? DragGhostBackplate { get; private set; }
    public NodeHandle DragOverlay { get; private set; }
    public bool DropSpotlightActive { get; private set; }
    public int DropSpotlightRootCount { get; private set; }
    public int OverlayCount { get; private set; }
    public RectF OverlayClip { get; private set; }
    public RectF? SpotlightScrimClip { get; private set; }
    public int OrphanCount { get; private set; }
    public bool HasActiveVirtualDisclosures { get; private set; }
    public bool PendingRemovalOverflow { get; private set; }
    public ReadOnlySpan<SceneStore.RemovedNodeExtent> PendingRemovalExtents => _removals.AsSpan(0, _removalCount);
    public RecordingScrollChrome ScrollChrome { get; } = new();
    /// <summary>This publication's scroll coverage (design §5): one row per viewport the UI thread realized content
    /// for, plus the scroll-effect rows scoped to it. Filled by the host at capture (<c>AppHost.CaptureScrollCoverage</c>),
    /// adopted by the render thread's <see cref="ScrollPoser"/> on a fresh publication.</summary>
    public ScrollCoverageTable ScrollCoverage { get; } = new();

    // Reachability walk state. A slot is live in this snapshot only when the walk reached it this capture; a slot the
    // previous capture reached and this one did not has its handle and topology row cleared so nothing can chain
    // through it. Parked pages and parked virtual items are detached from the root's topology, so their slots cost
    // nothing per publication: capture scales with what the recorder can draw, not with the store's high-water mark.
    private uint[] _capturedEpoch = [], _walkedEpoch = [];
    private uint _captureEpoch;
    private List<int> _captured = new(), _previousCaptured = new();
    private NodeHandle[] _walk = new NodeHandle[64];

    /// <summary>Slots the last capture reached (the recorder's reachable set, ancestors of extra roots included).</summary>
    public int CapturedNodeCount => _captured.Count;

    // ── P8: incremental capture state ──────────────────────────────────────────────────────────────────────────────
    // A capture at publication N copies every REACHABLE node on a full capture, but only the nodes whose captured
    // columns changed since publication L (the baseline this slot last captured at) on an incremental one. The
    // reachability WALK still runs either way: it is 3 array reads per node against the store's topology columns, and
    // it is what makes "a parked page's slots read as dead" and "an unparked page's slots come back live" exact
    // rather than approximately right. What incremental removes is the expensive half - ~17 dense column copies plus
    // ~25 sparse-table probes per node - which is where publication cost actually lives.
    private SceneStore? _lastSource;
    private ulong _lastCaptureSeq;
    private bool _capIncremental;
    private ulong _capBaseline;
    private uint _capPrevEpoch;
    private int _copiedNodeCount;
    private int[] _scrollNodes = [];
    private int _scrollNodeCount;
    private bool _resourceReferencesDirty, _unresolvedSpanReference;

    /// <summary>Rows scanned to rebuild image/span reference identities on the last capture. Zero when an
    /// incremental capture preserved traversal order, generations and every copied row's resource identities.
    /// Reachability is still walked, and image readiness/reveal metadata is still refreshed by the caller.</summary>
    public int ResourceReferenceRowsScanned { get; private set; }

    /// <summary>Nodes whose columns this capture actually COPIED - every reachable node on a full capture, only the
    /// changed ones on an incremental one. This (not <see cref="CapturedNodeCount"/>) is the number
    /// <c>FrameStats.CapturedNodes</c> reports: it is the work the publication did, and on a coast frame it is a
    /// handful of nodes rather than the whole tree.</summary>
    public int CopiedNodeCount => _copiedNodeCount;

    /// <summary>Whether the most recent capture took the incremental path (diagnostics/gates).</summary>
    public bool LastCaptureWasIncremental { get; private set; }
    // Incremental captures since the last full one (the DEBUG parity self-check's cadence, Parity.cs).
    private int _incrementalStreak;

    /// <summary>The publication this snapshot's contents describe - <c>SceneStore.PublishSeq + 1</c> at capture time.
    /// The publisher hands this back as the baseline of the NEXT incremental capture into the same slot.</summary>
    public ulong LastCaptureSeq => _lastCaptureSeq;

    // ── referenced-image collection (perf plan item 1) ──────────────────────────────────────────────────────────────
    // The set of ImageCache ids this capture's nodes can actually draw — a captured node's NodePaint.ImageId when its
    // VisualKind is Image (NOT IconLayer, where ImageId is an IconGeometryTable PathId, or Video, where it's a video
    // registry SurfaceId — see Scene/Columns.cs), plus the sparse ImageVisualEffects.DerivedImageId for the same node
    // once its bake is ready. Per-capture epoch-stamped dense array (mirrors _capturedEpoch/_walkedEpoch above) so a
    // dupe id seen from many nodes (the same album-art handle painted by 50 rows) is added once, allocation-free.
    private uint[] _imageIdEpoch = [];
    private uint _imageCaptureEpoch;
    private int[] _referencedImageIds = new int[16];
    private int _referencedImageIdCount;

    /// <summary>The ImageCache ids this capture's nodes reference — every entry is either a <c>VisualKind.Image</c>
    /// node's <c>NodePaint.ImageId</c>, its <c>ImageVisualEffects.DerivedImageId</c> or a live swap's
    /// <c>ImageVisualEffects.SwapOutgoingId</c>. Deduped; order is capture
    /// order, not id order. <see cref="ImageRecordingSnapshot.Capture(ImageCache?, ReadOnlySpan{int})"/> copies exactly
    /// this set instead of every entry the cache has ever seen.</summary>
    public ReadOnlySpan<int> ReferencedImageIds => _referencedImageIds.AsSpan(0, _referencedImageIdCount);

    private void BeginImageCapture()
    {
        _referencedImageIdCount = 0;
        if (++_imageCaptureEpoch == 0)
        {
            Array.Clear(_imageIdEpoch);
            _imageCaptureEpoch = 1;
        }
    }

    private void NoteReferencedImage(int id)
    {
        if (id <= 0) return;
        Grow(ref _imageIdEpoch, id + 1);
        if (_imageIdEpoch[id] == _imageCaptureEpoch) return;
        _imageIdEpoch[id] = _imageCaptureEpoch;
        if (_referencedImageIdCount == _referencedImageIds.Length) Array.Resize(ref _referencedImageIds, _referencedImageIds.Length * 2);
        _referencedImageIds[_referencedImageIdCount++] = id;
    }

    /// <summary>
    /// Copy a committed scene while this snapshot's publisher slot is exclusively owned by the UI. Only nodes the recorder
    /// can reach are copied: the root's tree, exit orphans, connected-animation overlays, the drag visuals, each
    /// <paramref name="extraRoots"/> subtree (popup windows) and every root's ancestor chain, so absolute rects and
    /// reuse-block chains resolve exactly as a full copy would.
    /// </summary>
    public void Capture(SceneStore source, ReadOnlySpan<NodeHandle> extraRoots = default)
        => CaptureCore(source, extraRoots, incremental: false, baseline: 0UL);

    /// <summary>
    /// P8: refresh this snapshot IN PLACE from <paramref name="source"/>, copying only what changed since
    /// <paramref name="lastCapturedSeq"/> - the publication this slot's previous capture described. Returns false, having
    /// changed nothing, when the incremental path is not provably valid; the caller must then call
    /// <see cref="Capture"/>. Validity is deliberately conservative (<see cref="CanCaptureIncremental"/>): a slow full
    /// capture is always correct, a wrong incremental one publishes last frame's geometry and nothing throws.
    /// </summary>
    public bool CaptureIncremental(SceneStore source, ReadOnlySpan<NodeHandle> extraRoots, ulong lastCapturedSeq)
    {
        LastCaptureFullReason = IncrementalRefusal(source, lastCapturedSeq);
        if (LastCaptureFullReason != CaptureFullReason.None) return false;
        CaptureCore(source, extraRoots, incremental: true, baseline: lastCapturedSeq);
        return true;
    }

    /// <summary>Why the last <see cref="CaptureIncremental"/> refused (the first failing clause of
    /// <see cref="CanCaptureIncremental"/>, in its order); <see cref="CaptureFullReason.None"/> when it captured
    /// incrementally. Always on — the render census's answer to "why was this publish a full capture?".</summary>
    public CaptureFullReason LastCaptureFullReason { get; private set; }

    private CaptureFullReason IncrementalRefusal(SceneStore source, ulong lastCapturedSeq)
    {
        if (_lastSource is null || !ReferenceEquals(_lastSource, source) || lastCapturedSeq == 0) return CaptureFullReason.NoBaseline;
        if (_lastCaptureSeq != lastCapturedSeq || source.PublishSeq + 1 <= lastCapturedSeq) return CaptureFullReason.BaselineMismatch;
        if (source.RecordingNodeCount < _nodeCount) return CaptureFullReason.StoreShrank;
        if (source.CaptureLedgerFloor > lastCapturedSeq) return CaptureFullReason.LedgerTruncated;
        if (source.BulkMutationSeq > lastCapturedSeq) return CaptureFullReason.BulkMutation;
        return CaptureFullReason.None;
    }

    /// <summary>Whether an incremental refresh from <paramref name="source"/> against baseline
    /// <paramref name="lastCapturedSeq"/> is provably sound. Every clause is a way the store's capture ledger could
    /// fail to describe the delta:
    /// <list type="bullet">
    /// <item>a different store instance, or none yet - no shared lineage at all;</item>
    /// <item>a baseline that is not the one this snapshot actually holds, or a publication counter that has not
    /// advanced (a host that never calls <c>NotePublished</c> would otherwise stamp every change at the same
    /// sequence and "nothing changed" would be indistinguishable from "everything did");</item>
    /// <item>a store whose node high-water shrank (<c>TrimExcessCapacity</c>) - slots this snapshot still describes
    /// are gone;</item>
    /// <item>a ledger whose floor has risen past the baseline - entries were dropped, so its history is truncated;</item>
    /// <item>a bulk mutation since the baseline (a layout pass, a reconciler commit, a column realloc) - changes
    /// nobody enumerated.</item>
    /// </list>
    /// </summary>
    public bool CanCaptureIncremental(SceneStore source, ulong lastCapturedSeq)
        => IncrementalRefusal(source, lastCapturedSeq) == CaptureFullReason.None;

    private void CaptureCore(SceneStore source, ReadOnlySpan<NodeHandle> extraRoots, bool incremental, ulong baseline)
    {
        int count = source.RecordingNodeCount;
        // Parked/unreachable slots in the UI store do not require dense recording rows. Growth happens only
        // when the existing reachability walk encounters a required index, never while the renderer paints.
        EnsureCapacity(16);
        PrepareCompositorOverlay(Capacity);
        _highestCapturedIndex = 0;
        _nodeCount = count;
        Root = source.Root;
        DeviceScale = source.DeviceScale;
        DragGhost = source.DragGhost;
        DragGhostBackplate = source.DragGhostBackplate;
        DragOverlay = source.DragOverlay;
        DropSpotlightActive = source.DropSpotlightActive;
        DropSpotlightRootCount = source.DropSpotlightRootCount;
        OverlayCount = source.OverlayCount;
        OverlayClip = source.OverlayClip;
        SpotlightScrimClip = source.SpotlightScrimClip;
        OrphanCount = source.OrphanCount;
        HasActiveVirtualDisclosures = source.HasActiveVirtualDisclosures;
        PendingRemovalOverflow = source.PendingRemovalOverflow;
        _removalCount = source.PendingRemovalExtents.Length;
        Grow(ref _removals, _removalCount);
        source.PendingRemovalExtents.CopyTo(_removals);
        _capIncremental = incremental;
        _capBaseline = baseline;
        _copiedNodeCount = 0;
        _scrollNodeCount = 0;
        ResourceReferenceRowsScanned = 0;
        _resourceReferencesDirty = !incremental || _unresolvedSpanReference;
        // A full capture rebuilds every per-node sparse table from empty; an incremental one edits them per touched
        // node (ClearSparseRows + Set), so it must NOT reset them.
        if (!incremental)
        {
            BeginSparseCapture();
            _spanDecorations.BeginCapture();
            _rowCells.BeginCapture();
        }
        // These three are rebuilt wholesale on EVERY capture: they are tiny (orphans are budget-capped, scroll rows
        // are a handful of viewports) and rebuilding is cheaper than tracking deltas through them.
        _orphanChildren.BeginCapture();

        uint previousEpoch = _captureEpoch;
        uint epoch = NextCaptureEpoch();
        // Epoch wrap clears the stamp arrays, so "was this reached last capture" is no longer answerable - treat every
        // node as newly reached, which forces a full row copy for each. Correct, and happens once per 4 billion frames.
        if (epoch == 1 && previousEpoch != 0) previousEpoch = 0;
        _capPrevEpoch = previousEpoch;
        (_captured, _previousCaptured) = (_previousCaptured, _captured);
        _captured.Clear();
        CaptureTree(source, source.Root, epoch);
        for (int i = 0; i < OverlayCount; i++) CaptureTree(source, source.OverlayAt(i), epoch);
        for (int i = 0; i < OrphanCount; i++) CaptureTree(source, source.OrphanAt(i, out _, out _), epoch);
        CaptureTree(source, DragGhost, epoch);
        CaptureTree(source, DragOverlay, epoch);
        for (int i = 0; i < extraRoots.Length; i++) CaptureTree(source, extraRoots[i], epoch);
        if (_captured.Count != _previousCaptured.Count) _resourceReferencesDirty = true;
        // A slot reachable last capture but not this one reads as dead: no handle, no topology to chain through, and
        // (incremental only - a full capture already dropped every sparse row) no leftover sparse payload either.
        foreach (int index in _previousCaptured)
        {
            if (_capturedEpoch[index] == epoch) continue;
            _handles[index] = NodeHandle.Null;
            _parent[index] = _firstChild[index] = _nextSibling[index] = NodeHandle.Null;
            if (incremental) ClearSparseRows(index);
        }

        // Retain identities on pure paint/lyric changes. A changed identity, generation or traversal order falls
        // back to the original captured-order derivation, preserving both de-duplication and ownership semantics.
        if (_resourceReferencesDirty) RebuildResourceReferences();

        // Scrollbar chrome: rebuilt for the captured scrollable set (collected during the walk) — keyed by the
        // SCROLLER's index, a handful of entries, so a full rebuild is the only correct cheap option.
        ScrollChrome.BeginCapture();
        for (int i = 0; i < _scrollNodeCount; i++)
        {
            int index = _scrollNodes[i];
            if (_scroll.Contains(index)) ScrollChrome.Add(index, source.ScrollChrome.Get(index));
        }

        Grow(ref _orphans, OrphanCount);
        for (int i = 0; i < OrphanCount; i++)
        {
            var node = source.OrphanAt(i, out float x, out float y);
            var parent = source.OrphanVisualParentAt(i);
            _orphans[i] = new(node, parent, x, y);
            if (parent.IsNull) continue;
            ref var children = ref _orphanChildren.GetOrAdd((int)parent.Raw.Index, out bool added);
            children.Items ??= new();
            if (added) children.Items.Clear();
            children.Items.Add(node);
        }
        Grow(ref _overlays, OverlayCount);
        for (int i = 0; i < OverlayCount; i++) _overlays[i] = source.OverlayAt(i);
        Grow(ref _spotlights, DropSpotlightRootCount);
        for (int i = 0; i < DropSpotlightRootCount; i++) _spotlights[i] = source.DropSpotlightRootAt(i);
        if (!incremental)
        {
            EndSparseCapture();
            _spanDecorations.EndCapture();
            _rowCells.EndCapture();
        }
        _orphanChildren.EndCapture();

        _lastSource = source;
        _lastCaptureSeq = source.PublishSeq + 1;
        LastCaptureWasIncremental = incremental;
        _incrementalStreak = incremental ? _incrementalStreak + 1 : 0;
        if (incremental) VerifyIncrementalParity(source, extraRoots);
    }

    private readonly record struct ResourceReferences(int Image, int Derived, int Outgoing, int SpanRun);

    private ResourceReferences ResourceReferencesAt(int index)
    {
        int spanRun = _textStyle.TryGet(index, out var style) ? style.SpanRunId : 0;
        ref readonly NodePaint paint = ref _paint[index];
        if (paint.VisualKind != VisualKind.Image) return new(0, 0, 0, spanRun);
        _imageEffects.TryGet(index, out var effects);
        return new(paint.ImageId, effects.DerivedImageId, effects.SwapOutgoingId, spanRun);
    }

    private void RebuildResourceReferences()
    {
        BeginImageCapture();
        _seenSpanRuns.Clear();
        _unresolvedSpanReference = false;
        ResourceReferenceRowsScanned = _captured.Count;
        foreach (int index in _captured)
        {
            var references = ResourceReferencesAt(index);
            NoteReferencedImage(references.Image);
            NoteReferencedImage(references.Derived);
            NoteReferencedImage(references.Outgoing);
            if (references.SpanRun != 0) NoteSpanRunSeen(references.SpanRun);
        }
        ReleaseUnseenSpanRuns();
    }

    private void NoteScrollNode(int index)
    {
        if (_scrollNodeCount == _scrollNodes.Length) Array.Resize(ref _scrollNodes, Math.Max(8, _scrollNodes.Length * 2));
        _scrollNodes[_scrollNodeCount++] = index;
    }

    /// <summary>Drop every per-node sparse row for a slot. The incremental path's equivalent of what a full capture's
    /// <c>BeginCapture</c> does wholesale: a node that LOST a shadow (or was recycled into a different element) must not
    /// keep its predecessor's payload.</summary>
    private void ClearSparseRows(int index)
    {
        _textStyle.Remove(index);
        _measurement.Remove(index);
        _scroll.Remove(index);
        _selectionRects.Remove(index);
        _underlineRects.Remove(index);
        _spanDecorations.Remove(index);
        _rowCells.Remove(index);
        _interact.Remove(index);
        _shadow.Remove(index);
        _arc.Remove(index);
        _polyline.Remove(index);
        _path.Remove(index);
        _clipPath.Remove(index);
        _radialCenter.Remove(index);
        _acrylic.Remove(index);
        _edgeFade.Remove(index);
        _imageEffects.Remove(index);
        _brushAnim.Remove(index);
        _textEdit.Remove(index);
        _selectionHighlight.Remove(index);
        _glyphWipe.Remove(index);
        _Gradient.Remove(index);
        _BorderBrush.Remove(index);
        _HoverGradient.Remove(index);
        _PressedGradient.Remove(index);
        _HoverBorderBrush.Remove(index);
        _PressedBorderBrush.Remove(index);
    }

    private uint NextCaptureEpoch()
    {
        if (++_captureEpoch == 0)
        {
            Array.Clear(_capturedEpoch); Array.Clear(_walkedEpoch);
            _captureEpoch = 1;
        }
        return _captureEpoch;
    }

    /// <summary>Copy <paramref name="root"/>'s ancestor chain (nodes only) and its whole subtree. Iterative: a deep page
    /// must not cost UI stack, and the walk buffer grows once to the widest pending-children frontier.</summary>
    private void CaptureTree(SceneStore source, NodeHandle root, uint epoch)
    {
        if (root.IsNull || !source.IsLive(root)) return;
        for (var ancestor = source.Parent(root); !ancestor.IsNull; ancestor = source.Parent(ancestor))
        {
            EnsureReachableCapacity(ancestor.Raw.Index);
            if (_capturedEpoch[ancestor.Raw.Index] == epoch) break;
            CaptureNode(source, ancestor, epoch);
        }
        int depth = 0;
        _walk[depth++] = root;
        while (depth > 0)
        {
            var node = _walk[--depth];
            uint index = node.Raw.Index;
            EnsureReachableCapacity(index);
            if (_walkedEpoch[index] == epoch) continue;
            _walkedEpoch[index] = epoch;
            if (_capturedEpoch[index] != epoch) CaptureNode(source, node, epoch);
            for (var child = source.FirstChild(node); !child.IsNull; child = source.NextSibling(child))
            {
                if (depth == _walk.Length) Array.Resize(ref _walk, _walk.Length * 2);
                _walk[depth++] = child;
            }
        }
    }

    private void CaptureNode(SceneStore source, NodeHandle node, uint epoch)
    {
        int index = (int)node.Raw.Index;
        _highestCapturedIndex = Math.Max(_highestCapturedIndex, index);
        bool reachedLastCapture = _capPrevEpoch != 0 && _capturedEpoch[index] == _capPrevEpoch;
        _capturedEpoch[index] = epoch;
        // Compare while performing the existing reachability walk: no extra scan/index, and a reordered tree
        // rebuilds the public ReferencedImageIds sequence in the same order a full capture would produce.
        if (!_resourceReferencesDirty && (_captured.Count >= _previousCaptured.Count
            || _previousCaptured[_captured.Count] != index || _handles[index] != node))
            _resourceReferencesDirty = true;
        _captured.Add(index);
        NodeFlags sourceFlags = source.Flags(node);
        if ((sourceFlags & NodeFlags.Scrollable) != 0) NoteScrollNode(index);

        // P8: the incremental skip. A node keeps last capture's rows only when it was reachable last capture too (a
        // page coming back from being parked has EMPTY rows here, whatever its stamps say) AND neither its captured
        // columns nor its slot identity changed since the baseline.
        if (_capIncremental && reachedLastCapture
            && source.CaptureStampAt(index) <= _capBaseline
            && source.CreatedStampAt(index) <= _capBaseline)
            return;

        _copiedNodeCount++;
        var previousReferences = _resourceReferencesDirty ? default : ResourceReferencesAt(index);
        if (_capIncremental) ClearSparseRows(index);   // a full capture already emptied every table
        _handles[index] = node;
        _parent[index] = source.Parent(node);
        _firstChild[index] = source.FirstChild(node);
        _nextSibling[index] = source.NextSibling(node);
        _bounds[index] = source.Bounds(node);
        _paint[index] = source.Paint(node);
        _interaction[index] = source.Interaction(node);
        NodeFlags flags = sourceFlags;
        _flags[index] = flags;
        _dirty[index] = source.RecordDirtyBits(node);
        _dirtySelf[index] = source.RecordDirtySelfBits(node);
        _dirtyDescendant[index] = source.RecordDirtyDescendantBits(node);
        if (source.TryGetMeasureCache(node, out var measurement)) _measurement.Set(index) = measurement;
        TextStyle style = source.Layout(node).TextStyle;
        if (_paint[index].VisualKind == VisualKind.Text || !style.FontFamily.IsEmpty || style.SpanRunId != 0)
        {
            _textStyle.Set(index) = style;
            CaptureSpanDecorationRects(index, style.SpanRunId);
        }
        // The side tables are probed only where the store can hold a row: the flag/kind that gates each table's
        // add path also gates its free path, so a node without the flag has no row to copy.
        if ((flags & NodeFlags.Scrollable) != 0)
        {
            if (source.TryGetScroll(node, out var scroll))
            {
                // Physics/virtual-layout inputs are not recorder inputs; none may retain app objects.
                scroll.Layout = null;
                scroll.Extent = null;
                scroll.SnapPoints = null;
                scroll.ScrollKey = null;
                _scroll.Set(index) = scroll;
            }
            // The chrome row is captured by the post-walk scrollable pass in CaptureCore (keyed by the SCROLLER's index).
        }
        if ((flags & NodeFlags.InteractionAnim) != 0 && source.TryGetInteract(node, out InteractionAnim interact)) _interact.Set(index) = interact;
        if ((flags & NodeFlags.SparsePaint) != 0)
        {
            if (source.TryGetShadow(node, out ShadowSpec shadow)) _shadow.Set(index) = shadow;
            if (source.TryGetArc(node, out ArcSpec arc)) _arc.Set(index) = arc;
            if (source.TryGetPolylineStroke(node, out PolylineStrokeSpec polyline)) _polyline.Set(index) = polyline;
            if (source.TryGetPath(node, out PathSpec path)) _path.Set(index) = path;
            if (source.TryGetClipPath(node, out ClipPathSpec clipPath)) _clipPath.Set(index) = clipPath;
            if (source.TryGetRadialGradientCenter(node, out Point2 radialCenter)) _radialCenter.Set(index) = radialCenter;
            if (source.TryGetAcrylic(node, out AcrylicSpec acrylic)) _acrylic.Set(index) = acrylic;
            if (source.TryGetEdgeFade(node, out EdgeFadeSpec edgeFade)) _edgeFade.Set(index) = edgeFade;
            if (source.TryGetImageEffects(node, out ImageVisualEffects imageEffects)) _imageEffects.Set(index) = imageEffects;
            if (source.TryGetBrushAnim(node, out BrushAnim brushAnim)) _brushAnim.Set(index) = brushAnim;
            if (source.TryGetGradient(node, out var capturedGradient)) CopyGradient(ref _Gradient.Set(index), in capturedGradient);
            if (source.TryGetBorderBrush(node, out var capturedBorderBrush)) CopyGradient(ref _BorderBrush.Set(index), in capturedBorderBrush);
            if (source.TryGetHoverGradient(node, out var capturedHoverGradient)) CopyGradient(ref _HoverGradient.Set(index), in capturedHoverGradient);
            if (source.TryGetPressedGradient(node, out var capturedPressedGradient)) CopyGradient(ref _PressedGradient.Set(index), in capturedPressedGradient);
            if (source.TryGetHoverBorderBrush(node, out var capturedHoverBorderBrush)) CopyGradient(ref _HoverBorderBrush.Set(index), in capturedHoverBorderBrush);
            if (source.TryGetPressedBorderBrush(node, out var capturedPressedBorderBrush)) CopyGradient(ref _PressedBorderBrush.Set(index), in capturedPressedBorderBrush);
        }
        if (_paint[index].VisualKind == VisualKind.Text)
        {
            if (source.TryGetTextEdit(node, out TextEditState textEdit)) _textEdit.Set(index) = textEdit;
            if (source.TryGetSelectionHighlight(node, out ColorF selectionHighlight)) _selectionHighlight.Set(index) = selectionHighlight;
            if (source.TryGetGlyphWipe(node, out GlyphWipe glyphWipe)) _glyphWipe.Set(index) = glyphWipe;
            CopyRects(_selectionRects, index, source.GetTextEditSelectionRects(node));
            CopyRects(_underlineRects, index, source.GetTextEditUnderlineRects(node));
        }
        if (_paint[index].VisualKind == VisualKind.ListRow
            && source.TryGetRowCells(node, out var rowCells, out bool rowPlaceholder, out ColorF rowPlaceholderColor))
        {
            ref var rc = ref _rowCells.Set(index);
            if (rc.Cells is null || rc.Cells.Length < rowCells.Length) rc.Cells = new RowCellRecorded[rowCells.Length];
            rowCells.CopyTo(rc.Cells);
            rc.Count = rowCells.Length;
            rc.Placeholder = rowPlaceholder;
            rc.PlaceholderColor = rowPlaceholderColor;
        }
        if (!_resourceReferencesDirty && previousReferences != ResourceReferencesAt(index))
            _resourceReferencesDirty = true;
    }

    public bool IsLive(NodeHandle node) => node.Raw.Index > 0 && node.Raw.Index < (uint)_nodeCount && node.Raw.Index < (uint)Capacity
        && _handles[node.Raw.Index] == node;
    // Recorder skip/reuse-block chains may name a parked high-index handle. An unallocated dead tail has the
    // same zero topology as a cleared dense row. Strict live payload refs below still require a validated handle.
    public NodeHandle Parent(NodeHandle node) => node.Raw.Index < (uint)Capacity ? _parent[node.Raw.Index] : default;
    public NodeHandle FirstChild(NodeHandle node) => node.Raw.Index < (uint)Capacity ? _firstChild[node.Raw.Index] : default;
    public NodeHandle NextSibling(NodeHandle node) => node.Raw.Index < (uint)Capacity ? _nextSibling[node.Raw.Index] : default;
    public ref readonly RectF Bounds(NodeHandle node) => ref _bounds[node.Raw.Index];
    /// <summary>Value-copied style for a live text or authored font/span-bearing node. No flex/layout inputs
    /// cross this surface; nontext resource rows preserve their authored string/span lifetime.</summary>
    public bool TryGetTextStyle(NodeHandle node, out TextStyle style)
    {
        if (IsLive(node)) return _textStyle.TryGet((int)node.Raw.Index, out style);
        style = default;
        return false;
    }
    /// <summary>The recorder requires a captured style after dispatching a live text node. A missing row is
    /// a capture-contract failure, never a request to silently draw with a default font.</summary>
    public TextStyle RecordingTextStyle(NodeHandle node)
        => TryGetTextStyle(node, out var style) ? style
            : throw new InvalidOperationException("Recording text requires a live node with a captured text style.");
    /// <summary>Captured text/resource-bearing rows, independent of the scene's addressable node high-water.</summary>
    public int TextStyleRowCount => _textStyle.RowCount;
    /// <summary>Reserved value-array payload bytes only; index/free capacities and object overhead are separate.</summary>
    public long TextStyleValueCapacityBytes
        => (long)_textStyle.ValueCapacity * System.Runtime.CompilerServices.Unsafe.SizeOf<TextStyle>();
    internal (int Values, int Index, int Free) TextStyleCapacity
        => (_textStyle.ValueCapacity, _textStyle.IndexCapacity, _textStyle.FreeCapacity);
    /// <summary>The node's paint as the recorder must see it: the render thread's overlay row when this tick posed
    /// this node, otherwise the authored column. See <c>SceneRecordingSnapshot.Animation.cs</c> for the row pool.</summary>
    public ref readonly NodePaint Paint(NodeHandle node)
    {
        int slot = _overlayRow[node.Raw.Index] - 1;
        return ref (slot >= 0 && (_overlayRows[slot].Have & HavePaint) != 0
            ? ref _overlayRows[slot].Paint
            : ref _paint[node.Raw.Index]);
    }
    public ref readonly InteractionInfo Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
    // §13.1: the SELF epoch, not the trail. SceneRecorder reads TransformDirty here to emit a repaint band over the
    // node's SubtreeBounds; the trail marks every ancestor up to the root, whose SubtreeBounds is the window, so
    // reading the trail here is precisely what made one animated leaf repaint everything. The trail readers below are
    // unchanged — span reuse must still be denied along it.
    public NodeFlags Flags(NodeHandle node) => node.Raw.Index < (uint)Capacity
        ? _flags[node.Raw.Index] | (_overlaySelfEpoch[node.Raw.Index] == _overlayEpoch ? NodeFlags.TransformDirty | NodeFlags.PaintDirty : 0)
        : default;
    public byte RecordDirtyBits(NodeHandle node) => (byte)(_dirty[node.Raw.Index]
        | (_overlayDirtyEpoch[node.Raw.Index] == _overlayEpoch ? SceneStore.RecordDirtyContent : 0));
    // Also the SELF epoch, and for the same reason as Flags above: this is the recorder's OTHER route to a repaint
    // band (`contentDirtyNode`). It used to read "does this node have a paint row this epoch", which is true for a
    // HELD pose too — the row is still written, it just carries the same number — so a cadence-held marquee kept
    // damaging its band on every tick even after Flags stopped reporting it moved. The self epoch is stamped only by
    // a pose that actually changed, and only when a row was really acquired, so an overflowed node (whose pose is
    // discarded and which presents its authored paint) still reports 0 — gate.compositor-row-overflow.
    public byte RecordDirtySelfBits(NodeHandle node) => (byte)(_dirtySelf[node.Raw.Index]
        | (_overlaySelfEpoch[node.Raw.Index] == _overlayEpoch ? SceneStore.RecordDirtyContent : 0));
    public byte RecordDirtyDescendantBits(NodeHandle node) => (byte)(_dirtyDescendant[node.Raw.Index]
        | (_overlayDirtyEpoch[node.Raw.Index] == _overlayEpoch ? SceneStore.RecordDirtyContent : 0));

    // Value-copied at capture, read-only on the renderer: never aliases the UI's mutable layout cache.
    internal TextMeasureEntry ResolveMeasureForWidth(NodeHandle node, float width)
        => IsLive(node) && _measurement.TryGet((int)node.Raw.Index, out var measurement)
            ? measurement.ResolveForWidth(width) : default;
    internal int MeasurementRowCount => _measurement.RowCount;
    internal (int Values, int Index, int Free) MeasurementCapacity
        => (_measurement.ValueCapacity, _measurement.IndexCapacity, _measurement.FreeCapacity);
    public bool HasScroll(NodeHandle node) => _scroll.Contains((int)node.Raw.Index);
    public bool TryGetScroll(NodeHandle node, out ScrollState value) => _scroll.TryGet((int)node.Raw.Index, out value);
    public ref readonly ScrollState ScrollRef(NodeHandle node) => ref _scroll.At((int)node.Raw.Index);
    public NodeHandle DropSpotlightRootAt(int index) => _spotlights[index];
    public NodeHandle OverlayAt(int index) => _overlays[index];
    public NodeHandle OrphanAt(int index, out float x, out float y)
    {
        var orphan = _orphans[index]; x = orphan.X; y = orphan.Y; return orphan.Node;
    }
    internal NodeHandle OrphanVisualParentAt(int index) => _orphans[index].Parent;
    internal IReadOnlyList<NodeHandle>? OrphanChildrenOf(NodeHandle parent)
        => _orphanChildren.TryGet((int)parent.Raw.Index, out var children) ? children.Items : null;
    public ReadOnlySpan<RectF> GetTextEditSelectionRects(NodeHandle node)
        => _selectionRects.TryGet((int)node.Raw.Index, out var rects) ? rects.Items.AsSpan(0, rects.Count) : default;
    public ReadOnlySpan<RectF> GetTextEditUnderlineRects(NodeHandle node)
        => _underlineRects.TryGet((int)node.Raw.Index, out var rects) ? rects.Items.AsSpan(0, rects.Count) : default;
    public bool TryGetSpanDecorations(NodeHandle node, out SpanStyle[] styles, out SpanRect[] rects)
    {
        if (_spanDecorations.TryGet((int)node.Raw.Index, out var decoration))
        {
            styles = decoration.Styles; rects = decoration.Rects; return true;
        }
        styles = []; rects = []; return false;
    }

    /// <summary>The recorder's read of a captured <c>ListRowEl</c> row (scroll-rework Wave 0.E) — the render-thread
    /// twin of <c>SceneStore.TryGetRowCells</c>, resolved against THIS snapshot's captured column instead of the
    /// live UI-thread SceneStore (the render thread never touches SceneStore directly).</summary>
    public bool TryGetRowCells(NodeHandle node, out ReadOnlySpan<RowCellRecorded> cells, out bool placeholder, out ColorF placeholderColor)
    {
        if (_rowCells.TryGet((int)node.Raw.Index, out var capture) && capture.Cells is not null)
        {
            cells = capture.Cells.AsSpan(0, capture.Count);
            placeholder = capture.Placeholder;
            placeholderColor = capture.PlaceholderColor;
            return true;
        }
        cells = default;
        placeholder = false;
        placeholderColor = default;
        return false;
    }

    /// <summary>
    /// UI-thread retirement after the snapshot lease AND every referencing GPU submit have retired. Repeated capture
    /// retains unchanged ids without release/reacquire churn, so idle scenes cannot grow the span quarantine queue.
    /// </summary>
    public void ReleaseResources()
    {
        foreach (int id in _retainedSpanRuns) SpanRunTable.Shared.Release(id);
        _retainedSpanRuns.Clear();
        _seenSpanRuns.Clear();
        if (_stringOwner is { } strings)
            foreach (var id in _retainedStrings) strings.Unpin(id);
        _retainedStrings.Clear();
        _previousRetainedStrings.Clear();
        Array.Clear(_stringSeenEpoch);
        Array.Clear(_stringRetained);
        _stringOwner = null;
        // P8: the retired snapshot no longer describes any publication, so no later capture may treat it as a baseline.
        _lastSource = null;
        _lastCaptureSeq = 0;
    }

    /// <summary>
    /// UI-only retention for strings embedded in this scene and any host-authored draw text. Call on an exclusively
    /// owned slot before publication; release occurs only on safe recapture or ReleaseResources after GPU retirement.
    /// <para>perf plan item 2: incremental. Every captured id is stamped into <see cref="_stringSeenEpoch"/> (a cheap
    /// dense-array write, no HashSet) and pinned only the first time it transitions from unretained → retained
    /// (<see cref="_stringRetained"/>); the release pass then walks only <see cref="_previousRetainedStrings"/> — what
    /// was retained BEFORE this call — instead of a fresh diff over the whole current retained set. The common
    /// no-change capture (same text/fonts every frame) does O(nodes) epoch stamps and zero Pin/Unpin calls.</para>
    /// </summary>
    public void RetainStrings(StringTable strings, ReadOnlySpan<StringId> additional = default)
    {
        if (!ReferenceEquals(_stringOwner, strings))
        {
            if (_stringOwner is { } previous)
                foreach (var id in _retainedStrings) previous.Unpin(id);
            _retainedStrings.Clear();
            _previousRetainedStrings.Clear();
            Array.Clear(_stringSeenEpoch);
            Array.Clear(_stringRetained);
            _stringOwner = strings;
        }

        uint epoch = NextStringEpoch();
        (_retainedStrings, _previousRetainedStrings) = (_previousRetainedStrings, _retainedStrings);
        _retainedStrings.Clear();   // rebuilt below by MarkStringSeen; _previousRetainedStrings still holds the old set

        foreach (int index in _captured)
        {
            MarkStringSeen(_paint[index].Text, epoch);
            if (_textStyle.TryGet(index, out var style)) MarkStringSeen(style.FontFamily, epoch);
        }
        foreach (int id in _retainedSpanRuns)
        {
            if (SpanRunTable.Shared.Resolve(id) is not { } run) continue;
            foreach (var style in run.Spans) MarkStringSeen(style.FontFamily, epoch);
        }
        foreach (var id in additional) MarkStringSeen(id, epoch);

        // Release: ids retained before this call that were not re-marked — O(previously retained), never O(total
        // ever seen). A re-marked id was already folded into the new _retainedStrings by MarkStringSeen above.
        foreach (var id in _previousRetainedStrings)
        {
            int v = id.Value;
            if (v < _stringSeenEpoch.Length && _stringSeenEpoch[v] == epoch) continue;
            if (v < _stringRetained.Length) _stringRetained[v] = false;
            StringRetentionOps++;
            strings.Unpin(id);
        }
    }

    private uint NextStringEpoch()
    {
        if (++_stringEpoch == 0)
        {
            Array.Clear(_stringSeenEpoch);
            _stringEpoch = 1;
        }
        return _stringEpoch;
    }

    /// <summary>Currently retained string ids — diagnostics/gates only (perf plan item 2: proves a repeated capture of
    /// an unchanged scene neither grows nor churns the retained set).</summary>
    internal int RetainedStringCount => _retainedStrings.Count;

    /// <summary>Pin + Unpin calls this snapshot has made on its string table since the last
    /// <see cref="ResetStringRetentionOps"/> - diagnostics/gates only. An unchanged frame must add ZERO
    /// (<c>gate.capture.string-refcount-no-churn</c>): retention is derived from the captured SET, so an incremental
    /// capture that copied almost nothing still resolves the same retained ids without touching the table.</summary>
    internal int StringRetentionOps { get; private set; }
    internal void ResetStringRetentionOps() => StringRetentionOps = 0;

    private void MarkStringSeen(StringId id, uint epoch)
    {
        if (id.IsEmpty) return;
        int v = id.Value;
        Grow(ref _stringSeenEpoch, v + 1);
        Grow(ref _stringRetained, v + 1);
        if (_stringSeenEpoch[v] == epoch) return;   // already processed this call (repeat id across nodes/spans)
        _stringSeenEpoch[v] = epoch;
        if (!_stringRetained[v])
        {
            _stringRetained[v] = true;
            StringRetentionOps++;
            _stringOwner!.Pin(id);
        }
        _retainedStrings.Add(id);
    }

    /// <summary>Copy one node's laid span-decoration rects. Retention (the seen-set + AddRef) is NOT done here:
    /// an incremental capture does not visit every node, so the live-run set is derived from the whole captured set by
    /// <see cref="NoteSpanRunSeen"/> in <c>CaptureCore</c>'s post-walk pass.</summary>
    private void CaptureSpanDecorationRects(int nodeIndex, int id)
    {
        if (id == 0 || SpanRunTable.Shared.Resolve(id) is not { } run) return;
        if (run.Rects is not { } laid) return;
        ref var value = ref _spanDecorations.Set(nodeIndex);
        if (value.Styles is null || value.Styles.Length != run.Spans.Length) value.Styles = new SpanStyle[run.Spans.Length];
        if (value.Rects is null || value.Rects.Length != laid.Rects.Length) value.Rects = new SpanRect[laid.Rects.Length];
        run.Spans.CopyTo(value.Styles, 0);
        laid.Rects.CopyTo(value.Rects, 0);
    }

    /// <summary>Mark a span run as still referenced by this snapshot, taking a table reference the first time.</summary>
    private void NoteSpanRunSeen(int id)
    {
        // Unknown ids cannot be retained. Keep checking on later captures rather than sealing a currently
        // unresolvable run as permanently absent; valid retained runs cannot disappear while this slot owns them.
        if (SpanRunTable.Shared.Resolve(id) is null) { _unresolvedSpanReference = true; return; }
        _seenSpanRuns.Add(id);
        if (_retainedSpanRuns.Add(id)) SpanRunTable.Shared.AddRef(id);
    }

    private void ReleaseUnseenSpanRuns()
    {
        _releasedSpanRuns.Clear();
        foreach (int id in _retainedSpanRuns)
            if (!_seenSpanRuns.Contains(id)) _releasedSpanRuns.Add(id);
        foreach (int id in _releasedSpanRuns)
        {
            _retainedSpanRuns.Remove(id);
            SpanRunTable.Shared.Release(id);
        }
    }
    public bool TryGetInteract(NodeHandle node, out InteractionAnim value)
    {
        if (node.Raw.Index >= (uint)Capacity) { value = default; return false; }
        int slot = _overlayRow[node.Raw.Index] - 1;
        if (slot >= 0 && (_overlayRows[slot].Have & HaveInteraction) != 0)
        {
            value = _overlayRows[slot].Interaction;
            return true;
        }
        return _interact.TryGet((int)node.Raw.Index, out value);
    }
    public bool TryGetShadow(NodeHandle node, out ShadowSpec value) => _shadow.TryGet((int)node.Raw.Index, out value);
    public bool TryGetArc(NodeHandle node, out ArcSpec value) => _arc.TryGet((int)node.Raw.Index, out value);
    public bool TryGetPolylineStroke(NodeHandle node, out PolylineStrokeSpec value) => _polyline.TryGet((int)node.Raw.Index, out value);
    public bool TryGetPath(NodeHandle node, out PathSpec value) => _path.TryGet((int)node.Raw.Index, out value);
    public bool TryGetClipPath(NodeHandle node, out ClipPathSpec value) => _clipPath.TryGet((int)node.Raw.Index, out value);
    public bool TryGetRadialGradientCenter(NodeHandle node, out Point2 value) => _radialCenter.TryGet((int)node.Raw.Index, out value);
    public bool TryGetAcrylic(NodeHandle node, out AcrylicSpec value) => _acrylic.TryGet((int)node.Raw.Index, out value);
    public bool TryGetEdgeFade(NodeHandle node, out EdgeFadeSpec value) => _edgeFade.TryGet((int)node.Raw.Index, out value);
    public bool TryGetImageEffects(NodeHandle node, out ImageVisualEffects value) => _imageEffects.TryGet((int)node.Raw.Index, out value);
    public bool TryGetBrushAnim(NodeHandle node, out BrushAnim value)
    {
        if (node.Raw.Index >= (uint)Capacity) { value = default; return false; }
        int slot = _overlayRow[node.Raw.Index] - 1;
        if (slot >= 0 && (_overlayRows[slot].Have & HaveBrush) != 0)
        {
            value = _overlayRows[slot].Brush;
            return true;
        }
        return _brushAnim.TryGet((int)node.Raw.Index, out value);
    }
    public bool TryGetTextEdit(NodeHandle node, out TextEditState value) => _textEdit.TryGet((int)node.Raw.Index, out value);
    public bool TryGetSelectionHighlight(NodeHandle node, out ColorF value) => _selectionHighlight.TryGet((int)node.Raw.Index, out value);
    public bool TryGetGlyphWipe(NodeHandle node, out GlyphWipe value) => _glyphWipe.TryGet((int)node.Raw.Index, out value);
    public bool TryGetGradient(NodeHandle node, out GradientSpec value) => _Gradient.TryGet((int)node.Raw.Index, out value);
    public bool TryGetBorderBrush(NodeHandle node, out GradientSpec value) => _BorderBrush.TryGet((int)node.Raw.Index, out value);
    public bool TryGetHoverGradient(NodeHandle node, out GradientSpec value) => _HoverGradient.TryGet((int)node.Raw.Index, out value);
    public bool TryGetPressedGradient(NodeHandle node, out GradientSpec value) => _PressedGradient.TryGet((int)node.Raw.Index, out value);
    public bool TryGetHoverBorderBrush(NodeHandle node, out GradientSpec value) => _HoverBorderBrush.TryGet((int)node.Raw.Index, out value);
    public bool TryGetPressedBorderBrush(NodeHandle node, out GradientSpec value) => _PressedBorderBrush.TryGet((int)node.Raw.Index, out value);

    public RectF AbsoluteRect(NodeHandle node)
    {
        float x = 0, y = 0;
        for (var current = node; !current.IsNull; current = Parent(current))
        {
            x += Bounds(current).X + Paint(current).LocalTransform.Dx;
            y += Bounds(current).Y + Paint(current).LocalTransform.Dy;
            var parent = Parent(current);
            if (!parent.IsNull) { x += Paint(parent).ChildShiftX; y += Paint(parent).ChildShiftY; }
        }
        return new(x, y, Bounds(node).W, Bounds(node).H);
    }

    public bool TryGetVirtualItemBand(NodeHandle content, out int persistentPrefixCount, out float topInset, out float topFadeBand)
    {
        persistentPrefixCount = 0; topInset = float.NaN; topFadeBand = 0;
        if (content.IsNull || !IsLive(content)) return false;
        var viewport = Parent(content);
        if (viewport.IsNull || !IsLive(viewport) || !TryGetScroll(viewport, out var scroll)
            || scroll.ContentNode != content || scroll.Orientation != 0 || !float.IsFinite(scroll.ItemClipTopInset)) return false;
        persistentPrefixCount = Math.Clamp(scroll.PersistentPrefixCount, 0, scroll.ItemCount);
        topInset = MathF.Max(0, scroll.ItemClipTopInset);
        topFadeBand = MathF.Max(0, scroll.ItemClipTopFadeBand);
        return true;
    }

    public bool TryGetVirtualDisclosure(NodeHandle content, out int firstIndex, out int count,
        out float top, out float extent, out float progress, out int persistentPrefixCount, out int firstRealized)
    {
        firstIndex = -1; count = 0; top = extent = progress = 0; persistentPrefixCount = firstRealized = 0;
        if (!HasActiveVirtualDisclosures || content.IsNull || !IsLive(content)) return false;
        var viewport = Parent(content);
        if (viewport.IsNull || !IsLive(viewport) || !TryGetScroll(viewport, out var scroll)
            || scroll.ContentNode != content || scroll.Orientation != 0 || !float.IsFinite(scroll.DisclosureT)
            || scroll.DisclosureFirst < 0 || scroll.DisclosureCount <= 0 || scroll.DisclosureExtent <= 0) return false;
        firstIndex = scroll.DisclosureFirst; count = scroll.DisclosureCount;
        top = scroll.DisclosureTop; extent = scroll.DisclosureExtent; progress = Math.Clamp(scroll.DisclosureT, 0, 1);
        persistentPrefixCount = Math.Clamp(scroll.PersistentPrefixCount, 0, scroll.ItemCount);
        firstRealized = Math.Max(persistentPrefixCount, scroll.FirstRealized);
        return true;
    }

    private void EnsureCapacity(int count)
    {
        Grow(ref _handles, count); Grow(ref _parent, count); Grow(ref _firstChild, count); Grow(ref _nextSibling, count);
        Grow(ref _bounds, count); Grow(ref _paint, count); Grow(ref _interaction, count);
        Grow(ref _flags, count); Grow(ref _dirty, count); Grow(ref _dirtySelf, count); Grow(ref _dirtyDescendant, count);
        Grow(ref _capturedEpoch, count); Grow(ref _walkedEpoch, count);
    }

    internal static void Grow<T>(ref T[] values, int count)
    {
        if (values.Length < count) Array.Resize(ref values, Math.Max(count, Math.Max(16, values.Length * 2)));
    }

    private static void CopyRects(SnapshotColumn<RectBuffer> target, int index, ReadOnlySpan<RectF> source)
    {
        if (source.IsEmpty) return;
        ref var buffer = ref target.Add(index);
        buffer.Items ??= [];
        Grow(ref buffer.Items, source.Length);
        source.CopyTo(buffer.Items);
        buffer.Count = source.Length;
    }

    private static void CopyGradient(ref GradientSpec target, in GradientSpec source)
    {
        int count = Math.Min(source.Stops?.Length ?? 0, GradientSpec.MaxStops);
        var stops = target.Stops;
        if (stops is null || stops.Length != count) stops = new GradientStop[count];
        source.Stops.AsSpan(0, count).CopyTo(stops);
        target = source with { Stops = stops };
    }

    private void BeginSparseCapture()
    {
        _textStyle.BeginCapture();
        _measurement.BeginCapture();
        _scroll.BeginCapture(); _selectionRects.BeginCapture(); _underlineRects.BeginCapture();
        _interact.BeginCapture();
        _shadow.BeginCapture();
        _arc.BeginCapture();
        _polyline.BeginCapture();
        _path.BeginCapture();
        _clipPath.BeginCapture();
        _radialCenter.BeginCapture();
        _acrylic.BeginCapture();
        _edgeFade.BeginCapture();
        _imageEffects.BeginCapture();
        _brushAnim.BeginCapture();
        _textEdit.BeginCapture();
        _selectionHighlight.BeginCapture();
        _glyphWipe.BeginCapture();
        _Gradient.BeginCapture();
        _BorderBrush.BeginCapture();
        _HoverGradient.BeginCapture();
        _PressedGradient.BeginCapture();
        _HoverBorderBrush.BeginCapture();
        _PressedBorderBrush.BeginCapture();
    }

    private void EndSparseCapture()
    {
        _textStyle.EndCapture();
        _measurement.EndCapture();
        _scroll.EndCapture(); _selectionRects.EndCapture(); _underlineRects.EndCapture();
        _interact.EndCapture();
        _shadow.EndCapture();
        _arc.EndCapture();
        _polyline.EndCapture();
        _path.EndCapture();
        _clipPath.EndCapture();
        _radialCenter.EndCapture();
        _acrylic.EndCapture();
        _edgeFade.EndCapture();
        _imageEffects.EndCapture();
        _brushAnim.EndCapture();
        _textEdit.EndCapture();
        _selectionHighlight.EndCapture();
        _glyphWipe.EndCapture();
        _Gradient.EndCapture();
        _BorderBrush.EndCapture();
        _HoverGradient.EndCapture();
        _PressedGradient.EndCapture();
        _HoverBorderBrush.EndCapture();
        _PressedBorderBrush.EndCapture();
    }

    private readonly record struct Orphan(NodeHandle Node, NodeHandle Parent, float X, float Y);
    private struct RectBuffer { public RectF[] Items; public int Count; }
    private struct OrphanChildren { public List<NodeHandle>? Items; }
    private struct SpanDecoration { public SpanStyle[] Styles; public SpanRect[] Rects; }
    private struct RowCellsCapture { public RowCellRecorded[]? Cells; public int Count; public bool Placeholder; public ColorF PlaceholderColor; }
}

/// <summary>Reusable dense visual rows indexed by sparse scene slots. Mutation is confined to exclusive capture.</summary>
internal sealed class SnapshotColumn<T>
{
    // A previously absent visual can become live during interaction (e.g. a drag lift adds a shadow).
    // Reserve the small sparse page when the snapshot itself is created, not on that first hot capture.
    private readonly Dictionary<int, int> _indices = new(16);
    private T[] _values = new T[16];
    // P8: slots released by Remove (an incremental capture editing one node's rows). _count stays the high-water so
    // EndCapture's tail clear keeps working on the full-capture path.
    private int[] _free = [];
    private int _count, _oldCount, _freeCount;
    // Text style/measurement rows are removed/reinserted on incremental captures. Reserve their first free-list page
    // with the value/index page, so the first new text row in a warmed non-text scene needs no capture allocation.
    public SnapshotColumn(bool reserveRemovals = false)
    {
        if (reserveRemovals) _free = new int[16];
    }
    internal int RowCount => _indices.Count;
    internal int ValueCapacity => _values.Length;
    internal int IndexCapacity => _indices.EnsureCapacity(0);
    internal int FreeCapacity => _free.Length;
    public void BeginCapture() { _oldCount = _count; _count = 0; _freeCount = 0; _indices.Clear(); }
    public void EndCapture() { if (_oldCount > _count) Array.Clear(_values, _count, _oldCount - _count); }
    public ref T Add(int index)
    {
        int slot;
        if (_freeCount > 0) slot = _free[--_freeCount];
        else { SceneRecordingSnapshot.Grow(ref _values, _count + 1); slot = _count++; }
        _indices.Add(index, slot);
        return ref _values[slot];
    }
    /// <summary>P8: get-or-add. Identical to <see cref="Add"/> on a full capture (the table was just emptied); on an
    /// incremental one it overwrites the node's existing row in place instead of throwing on the duplicate key.</summary>
    public ref T Set(int index)
    {
        if (_indices.TryGetValue(index, out int existing)) return ref _values[existing];
        return ref Add(index);
    }
    /// <summary>P8: drop one node's row, returning its slot to the free list. The value is deliberately NOT cleared -
    /// every reusable buffer it holds (a RectBuffer's array, an OrphanChildren list) is re-initialized by the next
    /// writer, and dropping them would re-allocate on the next capture.</summary>
    public void Remove(int index)
    {
        if (!_indices.Remove(index, out int slot)) return;
        if (_freeCount == _free.Length) Array.Resize(ref _free, Math.Max(8, _free.Length * 2));
        _free[_freeCount++] = slot;
    }
    public ref T GetOrAdd(int index, out bool added)
    {
        if (_indices.TryGetValue(index, out int slot)) { added = false; return ref _values[slot]; }
        added = true; return ref Add(index);
    }
    public bool Contains(int index) => _indices.ContainsKey(index);
    public ref T At(int index) => ref _values[_indices[index]];
    public bool TryGet(int index, out T value)
    {
        if (_indices.TryGetValue(index, out int slot)) { value = _values[slot]; return true; }
        value = default!; return false;
    }
}

/// <summary>Scrollbar presentation only; no mutable UI chrome table crosses publication.</summary>
public sealed class RecordingScrollChrome
{
    private readonly Dictionary<int, ScrollBarChromeRow> _rows = new();
    internal void BeginCapture() => _rows.Clear();
    internal void Add(int index, ScrollBarChromeRow row) => _rows.Add(index, row);
    public ScrollBarChromeRow Get(int index) => _rows.TryGetValue(index, out var value) ? value : default;
}
