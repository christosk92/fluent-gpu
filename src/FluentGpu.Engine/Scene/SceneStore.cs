using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// The reconciler's only window onto the retained tree — handle-in / handle-out, POD-only.
/// (The slice implements this directly on <see cref="SceneStore"/>.)
/// </summary>
public interface ISceneBackend
{
    NodeHandle CreateNode(ushort elementTypeId);
    void FreeSubtree(NodeHandle node);
    void AppendChild(NodeHandle parent, NodeHandle child);

    ref LayoutInput Layout(NodeHandle node);
    ref NodePaint Paint(NodeHandle node);
    ref InteractionInfo Interaction(NodeHandle node);
    void SetClickHandler(NodeHandle node, Action? handler);
    Action? GetClickHandler(NodeHandle node);

    void Mark(NodeHandle node, NodeFlags flags);

    NodeHandle FirstChild(NodeHandle node);
    NodeHandle NextSibling(NodeHandle node);
    int ChildCount(NodeHandle node);
}

/// <summary>Struct-of-arrays retained RenderNode tree. One spine (gen + free-list) indexes all parallel columns.</summary>
[FluentGpu.CodeGen.EnableColdSlab] // GEN-17 (wired): generates the ColdSlab<T> the cold paint side-tables below use
public sealed class SceneStore : ISceneBackend
{
    // spine
    private uint[] _gen;
    private int[] _nextFree;
    private int _freeHead;
    private int _high = 1;     // index 0 reserved = null

    // Exit-animation orphans: nodes removed from the logical tree but kept LIVE (drawing) until their exit animation
    // settles, then reclaimed. They are detached from topology (so reconcile + layout/input skip them) but indexed by
    // their former VISUAL parent so the recorder can replay them inside that parent's transform/clip/layer context.
    // Bounded by MaxOrphans (overflow instant-frees the oldest).
    private struct OrphanEntry { public NodeHandle Node, VisualParent; public float Px, Py; public long EnqueuedTicks; }
    private readonly List<OrphanEntry> _orphans = new();
    private readonly Dictionary<NodeHandle, List<NodeHandle>> _orphansByParent = new();
    private const int MaxOrphans = 64;

    // Connected-animation overlays: standalone flying shared-element (Hero) nodes that are NOT in the logical tree but
    // draw in an UNCLIPPED top band ABOVE the drag ghost (so a card art flying into a clipped rail escapes every
    // ancestor scissor). Each also carries NodeFlags.ConnectedOverlay (excluded from the main + orphan passes).
    // Set/cleared by FluentGpu.Animation.ConnectedAnimation; bounded so a nav storm cannot unbound the band.
    private readonly List<NodeHandle> _overlays = new();
    private const int MaxOverlays = 8;

    // Clip rect for the connected-animation overlay band (window DIP). RectF.Infinite ⇒ the band escapes every scissor
    // (the historical default); set to a content-region rect by FluentGpu.Animation.ConnectedAnimation so a flying cover
    // is bounded to the page area (never sails over the sidebar / window chrome) while still clearing the inner rail
    // scissor. Reset to Infinite when no fly is in flight. Read once per frame by the SceneRecorder overlay pass.
    public RectF OverlayClip = RectF.Infinite;

    // Effective device-pixel scale (DIP→px), set by AppHost each frame from the window scale (1 in headless / on DPI
    // change re-read). The sole consumer is the scroll content transform's device-pixel rounding (OverscrollPhysics.
    // WriteContentTransform), so a sub-pixel pan advances in whole device pixels while the logical offset stays float.
    public float DeviceScale = 1f;

    // topology (int indices; 0 = none)
    private int[] _parent, _firstChild, _lastChild, _prevSib, _nextSib, _childCount;

    // identity + columns
    private ushort[] _elementTypeId;
    private LayoutInput[] _layout;
    private RectF[] _bounds;          // LOCAL
    private NodePaint[] _paint;
    private DynamicTextKind[] _dynamicText;
    private int _dynamicTextCount;
    private InteractionInfo[] _interaction;
    private NodeFlags[] _flags;
    private byte[] _recordDirty;
    private byte[] _recordDirtySelf;
    private byte[] _recordDirtyDescendant;
    private int[] _recordDirtyWrote;
    private int _recordDirtyWroteCount;
    private Action?[] _click;         // managed edge payload (GC ref at the edge only)
    private Action<RectF>?[] _boundsChanged;   // post-layout arranged-bounds callback
    private RectF[] _boundsDelivered;   // last arranged rect actually delivered to _boundsChanged: the edge baseline for
                                        // OnBoundsChanged. NOT the live Bounds (which Measure pre-writes to the hypothetical
                                        // size each pass), so the callback fires on a real ARRANGED-rect change even for an
                                        // unconstrained node whose arranged size equals its measured size (the marquee bug).
    private Action<KeyEventArgs>?[] _keyHandler;
    private Action<CharEventArgs>?[] _charHandler;   // text (character) input handler
    private Action<Point2>?[] _pointerDown;   // position-aware (local coords) press / drag handlers
    private Action<Point2>?[] _drag;
    private Action<Point2>?[] _hoverMove;     // position-aware bare-hover move (no press) — RatingControl preview, etc.
    private Action?[] _pointerExit;           // fired when the pointer leaves the node (hover lost) — reset hover preview
    private Action<PointerEventArgs>?[] _pointerPressed;   // press w/ click-count + modifiers (double/triple-click, drag-select)
    private Action<PointerEventArgs>?[] _pointerReleased;  // clean release-over-press target (tap commit)
    private Action<WheelEventArgs>?[] _pointerWheel;        // element-level wheel hook (pre-viewport-scroll; NumberBox)
    private Action<ContextRequestEventArgs>?[] _contextRequested;  // right-click / Menu-key / long-press context request (local coords + trigger)
    private Action<bool>?[] _focusChanged;                 // dispatcher focus moved onto (true) / off (false) this node (GotFocus/LostFocus)
    // Drag-reorder lifecycle (E5): fired by Input.DragController once a CanDrag press crosses the drag threshold.
    private Action<DragEventArgs>?[] _dragStarted;         // threshold crossed → the gesture is a drag (WinUI DragStarting)
    private Action<DragEventArgs>?[] _dragDelta;           // every pointer move while the drag is active (coords + velocity)
    private Action<DragEventArgs>?[] _dragCompleted;       // released after an active drag (the click is suppressed)
    private Action?[] _dragCanceled;                       // Escape / capture loss / window blur aborted the drag

    // Sparse side-table for scroll/virtual viewports (O(viewports), not one-per-node). Keyed by node index.
    private readonly ColdSlab<ScrollState> _scroll = new();   // GEN-17 (wired)
    // Per-variable-list extent tables (Fenwick); persist across frames. Keyed by viewport node index.
    private readonly Dictionary<int, ExtentTable> _extents = new();
    // Grid specs for grid-container nodes (O(grids)). Keyed by node index.
    private readonly ColdSlab<GridSpec> _grids = new();   // GEN-17 (wired)
    // Optional rich-paint side-tables (O(decorated nodes), keyed by node index): eased interaction, shadow, gradient, acrylic.
    private readonly ColdSlab<InteractionAnim> _interact = new();   // GEN-17 (wired)
    private readonly ColdSlab<ShadowSpec> _shadows = new();   // GEN-17 (wired): dense slab, not Dictionary
    private readonly ColdSlab<ArcSpec> _arcs = new();
    private readonly ColdSlab<PolylineStrokeSpec> _polylines = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _gradients = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _borderBrushes = new();   // GEN-17 (wired) — gradient border stroke (elevation edge)
    // Stateful gradient variants (P4b): the recorder per-frame interpolates resting→state stops by the eased hover/press
    // progress. Sparse (O(state-gradient nodes)). Stop arrays are mount-allocated + stable — never rebuilt per frame.
    private readonly ColdSlab<GradientSpec> _hoverGradients = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _pressedGradients = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _hoverBorderBrushes = new();   // GEN-17 (wired)
    private readonly ColdSlab<GradientSpec> _pressedBorderBrushes = new();   // GEN-17 (wired)
    private readonly ColdSlab<AcrylicSpec> _acrylics = new();   // GEN-17 (wired)
    // Per-element edge fade (sparse): feather the subtree's alpha (+ optional blur) near chosen edges; read at record
    // time → PushLayer{EdgeFade}. Freed on FreeSubtree.
    private readonly ColdSlab<EdgeFadeSpec> _edgeFades = new();   // GEN-17 (wired)
    // Per-text-node measure cache (pure-function: (text,style,availW) → size); self-invalidating, freed on FreeSubtree.
    private readonly ColdSlab<TextMeasureCache> _measureCache = new();   // GEN-17 (wired) — hot: per-layout text measure
    // Implicit brush transitions (WinUI BrushTransition): sparse, O(transitioning nodes), advanced at phase 7.
    private readonly ColdSlab<BrushAnim> _brushAnims = new();   // GEN-17 (wired)
    private readonly List<int> _brushScratch = new();
    // Text-edit decoration state (sparse, O(editors)): caret/IME/focus PODs + per-node POOLED decoration-rect slots
    // (grow-only RectF[] reused across frames — a selection drag updates at pointer rate with ZERO steady alloc).
    private readonly Dictionary<int, TextEditState> _textEdits = new();
    private readonly Dictionary<int, (RectF[]? Arr, int Count)> _textEditSelRects = new();
    private readonly Dictionary<int, (RectF[]? Arr, int Count)> _textEditUnderlineRects = new();
    // Span-text side-tables (sparse, O(span paragraphs) — rtb-01/rtb-02/api-04):
    // the element's TextSpan array (hyperlink actions; the POD shaping overlay lives in SpanRunTable.Shared keyed by
    // TextStyle.SpanRunId); the dispatcher-owned read-only selection range; the per-control selection highlight color.
    private readonly Dictionary<int, TextSpan[]> _spanText = new();
    private readonly Dictionary<int, (int Start, int End)> _textSelection = new();
    private readonly ColdSlab<ColorF> _selectionHighlight = new();   // GEN-17 (wired)
    private readonly ColdSlab<GlyphWipe> _glyphWipes = new();        // sparse per-node glyph wipe (general text-reveal; lyrics karaoke)
    // E5-L2 drag-drop side-tables (sparse, O(sources)/O(targets), keyed by node index): the reconciler writes them
    // from BoxEl.Draggable / BoxEl.DropTarget; Input.DragDropContext reads them at promotion / per pointer move.
    private readonly Dictionary<int, DragSource> _dragSources = new();
    private readonly Dictionary<int, DropTargetSpec> _dropTargets = new();

    // UseGesture (input-a11y.md §13) declarations: sparse (only nodes that declared a gesture hook have an entry), the
    // _textEdits/_brushAnims side-table pattern — no per-node array, no resize cost. FluentGpu.Hooks WRITES the
    // subscription on mount; FluentGpu.Input READS it when the gesture arena resolves a winner on the node (both
    // reference Scene; neither references the other — this column is the seam). The handler delegates are the only GC
    // edge (a freshly-captured user closure at mount, like every HandlerTable column — foundations: GC at the edge OK).
    private readonly Dictionary<int, GestureSubscription> _gestureSubs = new();

    public NodeHandle Root { get; set; }

    /// <summary>The node currently lifted by an active item-drag (E5 ghost) — set/cleared by
    /// <c>Input.DragController</c> at promotion/restore (the node also carries <see cref="NodeFlags.DragGhost"/>).
    /// The recorder EXCLUDES it from the clipped main pass and re-walks its subtree in an UNCLIPPED top band emitted
    /// last, so the lifted visual escapes every ancestor scissor and paints above overlays. Null = no drag.</summary>
    public NodeHandle DragGhost { get; set; }

    /// <summary>Optional interner for text-id lifetime accounting: when set, freeing a node (or rewriting its dynamic
    /// text) releases its <c>paint.Text</c> / <c>TextStyle.Family</c> refs so streamed virtual-list text is reclaimed
    /// instead of accumulating for the process lifetime. Wired by the reconciler at composition.</summary>
    public StringTable? Strings { get; set; }

    /// <summary>Optional slot-free notification (node INDEX): invoked by <see cref="FreeSubtree"/> as a node's slot is
    /// reclaimed, so subsystems that key per-node state by INDEX (rather than gen-checked handle) — the AnimEngine
    /// layout-transition side-table, the ScrollIntegrator conscious-bar timers — can drop the dormant row symmetrically.
    /// Without it a freed slot's stale spec/state would be inherited by the NEXT node reusing that index. Wired by the
    /// host; null on backends that don't use the index-keyed side-tables.</summary>
    public Action<int>? OnFreeIndex { get; set; }

    public SceneStore(int capacity = 64)
    {
        if (capacity < 4) capacity = 4;
        _gen = new uint[capacity];
        _nextFree = new int[capacity];
        _parent = new int[capacity];
        _firstChild = new int[capacity];
        _lastChild = new int[capacity];
        _prevSib = new int[capacity];
        _nextSib = new int[capacity];
        _childCount = new int[capacity];
        _elementTypeId = new ushort[capacity];
        _layout = new LayoutInput[capacity];
        _bounds = new RectF[capacity];
        _paint = new NodePaint[capacity];
        _dynamicText = new DynamicTextKind[capacity];
        _interaction = new InteractionInfo[capacity];
        _flags = new NodeFlags[capacity];
        _recordDirty = new byte[capacity];
        _recordDirtySelf = new byte[capacity];
        _recordDirtyDescendant = new byte[capacity];
        _recordDirtyWrote = new int[capacity];
        _click = new Action?[capacity];
        _boundsChanged = new Action<RectF>?[capacity];
        _boundsDelivered = new RectF[capacity];
        _keyHandler = new Action<KeyEventArgs>?[capacity];
        _charHandler = new Action<CharEventArgs>?[capacity];
        _pointerDown = new Action<Point2>?[capacity];
        _drag = new Action<Point2>?[capacity];
        _hoverMove = new Action<Point2>?[capacity];
        _pointerExit = new Action?[capacity];
        _pointerPressed = new Action<PointerEventArgs>?[capacity];
        _pointerReleased = new Action<PointerEventArgs>?[capacity];
        _pointerWheel = new Action<WheelEventArgs>?[capacity];
        _contextRequested = new Action<ContextRequestEventArgs>?[capacity];
        _focusChanged = new Action<bool>?[capacity];
        _dragStarted = new Action<DragEventArgs>?[capacity];
        _dragDelta = new Action<DragEventArgs>?[capacity];
        _dragCompleted = new Action<DragEventArgs>?[capacity];
        _dragCanceled = new Action?[capacity];
    }

    public int LiveCount { get; private set; }

    /// <summary>SoA column length (the high-water spine allocation) — O(1) census of the slab size, not the live count.</summary>
    public int Capacity => _gen.Length;
    /// <summary>Live scroll/virtual-viewport rows — O(1) census of the <c>_scroll</c> side-table.</summary>
    public int ScrollStateCount => _scroll.Count;
    /// <summary>In-flight implicit brush transitions — O(1) census of the <c>_brushAnims</c> side-table.</summary>
    public int BrushAnimCount => _brushAnims.Count;

    public bool IsLive(NodeHandle h)
        => h.Raw.Index > 0 && h.Raw.Index < (uint)_high && _gen[h.Raw.Index] == h.Raw.Gen;

    public NodeHandle CreateNode(ushort elementTypeId)
    {
        int idx;
        if (_freeHead != 0) { idx = _freeHead; _freeHead = _nextFree[idx]; }
        else { if (_high >= _gen.Length) Grow(); idx = _high++; }

        if (_gen[idx] == 0) _gen[idx] = 1;
        // reset columns
        _parent[idx] = _firstChild[idx] = _lastChild[idx] = _prevSib[idx] = _nextSib[idx] = _childCount[idx] = 0;
        _elementTypeId[idx] = elementTypeId;
        _layout[idx] = LayoutInput.Default;
        _bounds[idx] = default;
        _paint[idx] = NodePaint.Default;
        ClearDynamicText(idx);
        _interaction[idx] = default;
        _flags[idx] = NodeFlags.Visible | NodeFlags.HitTestVisible | NodeFlags.NewThisFrame;
        _recordDirty[idx] = 0;
        _recordDirtySelf[idx] = 0;
        _recordDirtyDescendant[idx] = 0;
        MarkRecordDirty(idx);
        _click[idx] = null;
        _boundsChanged[idx] = null;
        _boundsDelivered[idx] = default;
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
        _pointerReleased[idx] = null;
        _pointerWheel[idx] = null;
        _contextRequested[idx] = null;
        _focusChanged[idx] = null;
        _dragStarted[idx] = null;
        _dragDelta[idx] = null;
        _dragCompleted[idx] = null;
        _dragCanceled[idx] = null;
        LiveCount++;
        return new NodeHandle(new Handle((uint)idx, _gen[idx]));
    }

    public void FreeSubtree(NodeHandle node)
    {
        if (!IsLive(node)) return;
        // Logically-detached exiting children are absent from the topology walk below. Retire them with a hard-freed
        // visual parent instead of letting them escape into a root-level render band.
        ReclaimOrphanChildren(node);
        int idx = (int)node.Raw.Index;
        // free children first
        int c = _firstChild[idx];
        while (c != 0)
        {
            int next = _nextSib[c];
            FreeSubtree(new NodeHandle(new Handle((uint)c, _gen[c])));
            c = next;
        }
        DetachFromParent(idx);
        if (Strings is { } st)
        {
            st.Release(_paint[idx].Text);
            st.Release(_layout[idx].TextStyle.FontFamily);
        }
        ReleaseSpanRun(_layout[idx].TextStyle.SpanRunId);   // span-run + per-span family lifetime (rtb-01)
        _click[idx] = null;
        _boundsChanged[idx] = null;
        _boundsDelivered[idx] = default;
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
        _pointerReleased[idx] = null;
        _pointerWheel[idx] = null;
        _contextRequested[idx] = null;
        _focusChanged[idx] = null;
        _dragStarted[idx] = null;
        _dragDelta[idx] = null;
        _dragCompleted[idx] = null;
        _dragCanceled[idx] = null;
        ClearDynamicText(idx);
        NodeFlags flags = _flags[idx];
        if ((flags & NodeFlags.Scrollable) != 0)
        {
            _scroll.Remove(idx);
            _extents.Remove(idx);
            _scrollObs.Remove(idx);
        }
        _grids.Remove(idx);
        if (_hitPassThrough.Count != 0) _hitPassThrough.Remove(idx);
        if ((flags & NodeFlags.InteractionAnim) != 0) _interact.Remove(idx);
        if ((flags & NodeFlags.SparsePaint) != 0)
        {
            _shadows.Remove(idx);
            _arcs.Remove(idx);
            _polylines.Remove(idx);
            _gradients.Remove(idx);
            _borderBrushes.Remove(idx);
            _hoverGradients.Remove(idx);
            _pressedGradients.Remove(idx);
            _hoverBorderBrushes.Remove(idx);
            _pressedBorderBrushes.Remove(idx);
            _acrylics.Remove(idx);
            _edgeFades.Remove(idx);
            _brushAnims.Remove(idx);
        }
        if (_paint[idx].VisualKind == VisualKind.Text)
        {
            _measureCache.Remove(idx);
            if (_textEdits.Count != 0) _textEdits.Remove(idx);
            if (_textEditSelRects.Count != 0) _textEditSelRects.Remove(idx);
            if (_textEditUnderlineRects.Count != 0) _textEditUnderlineRects.Remove(idx);
            if (_spanText.Count != 0) _spanText.Remove(idx);
            if (_textSelection.Count != 0) _textSelection.Remove(idx);
            _selectionHighlight.Remove(idx);
            _glyphWipes.Remove(idx);
        }
        if (_dragSources.Count != 0) _dragSources.Remove(idx);
        if (_dropTargets.Count != 0) _dropTargets.Remove(idx);
        if (_gestureSubs.Count != 0) _gestureSubs.Remove(idx);   // drop the node's UseGesture declaration with it (handler closures released)
        if (DragGhost == node) DragGhost = NodeHandle.Null;   // a freed ghost must not linger in the recorder's top band
        if ((flags & NodeFlags.ConnectedOverlay) != 0) RemoveOverlay(node);   // a freed overlay must not linger in the band
        OnFreeIndex?.Invoke(idx);   // symmetric teardown of INDEX-keyed external side-tables (AnimEngine transitions / ScrollIntegrator timers)
        _recordDirty[idx] = 0;
        _recordDirtySelf[idx] = 0;
        _recordDirtyDescendant[idx] = 0;
        _gen[idx]++;
        if (_gen[idx] == 0) _gen[idx] = 1;
        _nextFree[idx] = _freeHead;
        _freeHead = idx;
        LiveCount--;
    }

    public void AppendChild(NodeHandle parent, NodeHandle child)
    {
        Debug.Assert(IsLive(parent) && IsLive(child));
        int p = (int)parent.Raw.Index, c = (int)child.Raw.Index;
        _parent[c] = p;
        _prevSib[c] = _lastChild[p];
        _nextSib[c] = 0;
        if (_lastChild[p] != 0) _nextSib[_lastChild[p]] = c;
        else _firstChild[p] = c;
        _lastChild[p] = c;
        _childCount[p]++;
        MarkRecordDirty(c);
    }

    /// <summary>Unlink a child from its parent without freeing it (used by keyed reconcile to reorder).</summary>
    public void Detach(NodeHandle child)
    {
        if (IsLive(child)) DetachFromParent((int)child.Raw.Index);
    }

    private void DetachFromParent(int c)
    {
        int p = _parent[c];
        if (p == 0) return;
        MarkRecordDirty(c);
        if (_prevSib[c] != 0) _nextSib[_prevSib[c]] = _nextSib[c]; else _firstChild[p] = _nextSib[c];
        if (_nextSib[c] != 0) _prevSib[_nextSib[c]] = _prevSib[c]; else _lastChild[p] = _prevSib[c];
        _childCount[p]--;
        _parent[c] = _prevSib[c] = _nextSib[c] = 0;
    }

    // ── exit-animation orphans ────────────────────────────────────────────────────────────────────
    /// <summary>Remove a node from the logical tree but keep it LIVE and drawing: detach from its parent (so reconcile +
    /// layout no longer see it), retain its former visual parent, and flag it Exiting. The recorder replays it inside that
    /// parent's active transform/clip/layer/popup context; <see cref="ReclaimOrphan"/> frees it on settle. The frozen origin
    /// remains only as a defensive fallback for an already-rootless orphan.</summary>
    public void Orphan(NodeHandle node)
    {
        if (!IsLive(node)) return;
        if (_orphans.Count >= MaxOrphans) DropOrphanAt(0);   // budget: instant-free oldest
        RectF abs = AbsoluteRect(node);
        int idx = (int)node.Raw.Index;
        NodeHandle visualParent = Parent(node);
        float px = abs.X - _bounds[idx].X, py = abs.Y - _bounds[idx].Y;   // frozen parent-world origin
        DetachFromParent(idx);
        _flags[idx] |= NodeFlags.Exiting;
        _orphans.Add(new OrphanEntry { Node = node, VisualParent = visualParent, Px = px, Py = py, EnqueuedTicks = Stopwatch.GetTimestamp() });
        if (!visualParent.IsNull)
        {
            if (!_orphansByParent.TryGetValue(visualParent, out var children))
                _orphansByParent.Add(visualParent, children = new List<NodeHandle>(2));
            children.Add(node);
        }
    }

    /// <summary>Free a settled exit orphan (the deferred <see cref="FreeSubtree"/> — gen bump → handle dead).</summary>
    public void ReclaimOrphan(NodeHandle node)
    {
        for (int i = _orphans.Count - 1; i >= 0; i--)
            if (_orphans[i].Node == node)
            {
                var entry = _orphans[i];
                UnindexOrphan(in entry);
                _orphans.RemoveAt(i);
                break;
            }
        FreeSubtree(node);
    }

    /// <summary>Exiting children formerly owned by <paramref name="visualParent"/>, in removal order. Internal recorder
    /// seam: the list is stable during a record pass because reconcile/reclaim run outside phase 8.</summary>
    internal List<NodeHandle>? OrphanChildrenOf(NodeHandle visualParent)
        => _orphansByParent.TryGetValue(visualParent, out var children) ? children : null;

    private void UnindexOrphan(in OrphanEntry entry)
    {
        if (entry.VisualParent.IsNull || !_orphansByParent.TryGetValue(entry.VisualParent, out var children)) return;
        children.Remove(entry.Node);
        if (children.Count == 0) _orphansByParent.Remove(entry.VisualParent);
    }

    private void DropOrphanAt(int index)
    {
        var entry = _orphans[index];
        UnindexOrphan(in entry);
        _orphans.RemoveAt(index);
        FreeSubtree(entry.Node);
    }

    private void ReclaimOrphanChildren(NodeHandle visualParent)
    {
        if (!_orphansByParent.Remove(visualParent, out var children)) return;
        // Remove the global lifecycle rows before freeing. FreeSubtree(child) recursively handles exits whose visual
        // parent is itself this exiting child.
        for (int i = children.Count - 1; i >= 0; i--)
        {
            NodeHandle child = children[i];
            for (int j = _orphans.Count - 1; j >= 0; j--)
                if (_orphans[j].Node == child) { _orphans.RemoveAt(j); break; }
            FreeSubtree(child);
        }
    }

    public bool IsOrphan(NodeHandle node)
    {
        for (int i = 0; i < _orphans.Count; i++) if (_orphans[i].Node == node) return true;
        return false;
    }

    /// <summary>Count of exit orphans currently animating out (the host keeps painting while &gt; 0).</summary>
    public int OrphanCount => _orphans.Count;

    /// <summary>The i-th orphan node + its frozen parent-world origin (used only by the rootless fallback draw pass).</summary>
    public NodeHandle OrphanAt(int i, out float px, out float py)
    {
        var e = _orphans[i]; px = e.Px; py = e.Py; return e.Node;
    }

    /// <summary>The former visual parent that owns the orphan's render context. Null is the defensive rootless fallback.</summary>
    internal NodeHandle OrphanVisualParentAt(int i) => _orphans[i].VisualParent;

    /// <summary><see cref="Stopwatch.GetTimestamp"/> when the i-th orphan was enqueued — the host's settle-timeout
    /// backstop force-reclaims an orphan whose exit track wedged (a never-settling animation) so it can't pin the wake
    /// loop forever.</summary>
    public long OrphanEnqueuedTicks(int i) => _orphans[i].EnqueuedTicks;

    // ── connected-animation overlays (flying shared-element heroes) ───────────────────────────────
    /// <summary>Register a node as a connected-animation overlay: it draws in an UNCLIPPED top band ABOVE the drag
    /// ghost (escaping every ancestor scissor) and is excluded from the main + orphan passes. The node's
    /// <see cref="NodePaint.LocalTransform"/> carries the animated fly position/scale. Bounded by <c>MaxOverlays</c>
    /// (overflow instant-frees the oldest). Cleared by <see cref="RemoveOverlay"/> or when the node is freed.</summary>
    public void AddOverlay(NodeHandle node)
    {
        if (!IsLive(node) || _overlays.Contains(node)) return;
        if (_overlays.Count >= MaxOverlays) { var old = _overlays[0]; _overlays.RemoveAt(0); FreeSubtree(old); }
        _flags[node.Raw.Index] |= NodeFlags.ConnectedOverlay;
        _overlays.Add(node);
    }

    /// <summary>Drop a node from the overlay band (does NOT free it — the caller owns its lifetime).</summary>
    public void RemoveOverlay(NodeHandle node)
    {
        for (int i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i] == node) { _overlays.RemoveAt(i); break; }
        if (IsLive(node)) _flags[node.Raw.Index] &= ~NodeFlags.ConnectedOverlay;
    }

    /// <summary>Count of connected-animation overlays currently flying (the host keeps painting while &gt; 0).</summary>
    public int OverlayCount => _overlays.Count;

    /// <summary>The i-th connected-animation overlay node (for the recorder's top-band draw pass).</summary>
    public NodeHandle OverlayAt(int i) => _overlays[i];

    public bool IsOverlay(NodeHandle node)
    {
        for (int i = 0; i < _overlays.Count; i++) if (_overlays[i] == node) return true;
        return false;
    }

    // ── column accessors (re-fetch after any CreateNode that may grow) ─────────────
    public ref LayoutInput Layout(NodeHandle h) => ref _layout[h.Raw.Index];
    public ref RectF Bounds(NodeHandle h) => ref _bounds[h.Raw.Index];
    public ref NodePaint Paint(NodeHandle h) => ref _paint[h.Raw.Index];
    public ref InteractionInfo Interaction(NodeHandle h) => ref _interaction[h.Raw.Index];
    public ref NodeFlags Flags(NodeHandle h) => ref _flags[h.Raw.Index];
    public ushort ElementTypeId(NodeHandle h) => _elementTypeId[h.Raw.Index];

    private int LiveIndex(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        if (raw > 0 && raw < (uint)_high)
        {
            int idx = (int)raw;
            if (_gen[idx] == h.Raw.Gen) return idx;
        }
        throw new InvalidOperationException($"Node handle {h} is not live in this SceneStore.");
    }

    private int LiveIndexOrZero(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        if (raw > 0 && raw < (uint)_high)
        {
            int idx = (int)raw;
            if (_gen[idx] == h.Raw.Gen) return idx;
        }
        return 0;
    }

    public void SetClickHandler(NodeHandle h, Action? handler) => _click[LiveIndex(h)] = handler;
    public Action? GetClickHandler(NodeHandle h) => _click[LiveIndexOrZero(h)];
    public void SetBoundsChangedHandler(NodeHandle h, Action<RectF>? handler)
    {
        int idx = LiveIndex(h);
        // First install of a handler on a node that had none ⇒ arm a one-shot initial delivery: an unconstrained node
        // whose final arranged rect equals its silently-Measured rect would otherwise never see its first value (the
        // edge-triggered SetArrangedBounds only fires on a delta). Re-installing on steady re-renders (handler already
        // present) does NOT re-arm, so the callback fires once at mount then only on real bounds changes.
        if (handler is not null && _boundsChanged[idx] is null) _flags[idx] |= NodeFlags.BoundsChangedPending;
        else if (handler is null) _flags[idx] &= ~NodeFlags.BoundsChangedPending;
        _boundsChanged[idx] = handler;
    }
    public Action<RectF>? GetBoundsChangedHandler(NodeHandle h) => _boundsChanged[LiveIndexOrZero(h)];
    /// <summary>The last arranged rect delivered to this node's OnBoundsChanged (the edge baseline). FlexLayout fires the
    /// handler when the freshly-arranged rect differs from this — NOT from the live <see cref="Bounds"/>, which Measure
    /// pre-writes to the hypothetical size each pass (so an unconstrained node would otherwise never re-notify).</summary>
    public ref RectF BoundsDeliveredRef(NodeHandle h) => ref _boundsDelivered[LiveIndex(h)];
    public void SetKeyHandler(NodeHandle h, Action<KeyEventArgs>? handler) => _keyHandler[LiveIndex(h)] = handler;
    public Action<KeyEventArgs>? GetKeyHandler(NodeHandle h) => _keyHandler[LiveIndexOrZero(h)];
    public void SetCharHandler(NodeHandle h, Action<CharEventArgs>? handler) => _charHandler[LiveIndex(h)] = handler;
    public Action<CharEventArgs>? GetCharHandler(NodeHandle h) => _charHandler[LiveIndexOrZero(h)];
    public void SetPointerDown(NodeHandle h, Action<Point2>? handler) => _pointerDown[LiveIndex(h)] = handler;
    public Action<Point2>? GetPointerDown(NodeHandle h) => _pointerDown[LiveIndexOrZero(h)];
    public void SetDrag(NodeHandle h, Action<Point2>? handler) => _drag[LiveIndex(h)] = handler;
    public Action<Point2>? GetDrag(NodeHandle h) => _drag[LiveIndexOrZero(h)];
    public void SetHoverMove(NodeHandle h, Action<Point2>? handler) => _hoverMove[LiveIndex(h)] = handler;
    public Action<Point2>? GetHoverMove(NodeHandle h) => _hoverMove[LiveIndexOrZero(h)];
    public void SetPointerExit(NodeHandle h, Action? handler) => _pointerExit[LiveIndex(h)] = handler;
    public Action? GetPointerExit(NodeHandle h) => _pointerExit[LiveIndexOrZero(h)];
    public void SetPointerPressed(NodeHandle h, Action<PointerEventArgs>? handler) => _pointerPressed[LiveIndex(h)] = handler;
    public Action<PointerEventArgs>? GetPointerPressed(NodeHandle h) => _pointerPressed[LiveIndexOrZero(h)];
    public void SetPointerReleased(NodeHandle h, Action<PointerEventArgs>? handler) => _pointerReleased[LiveIndex(h)] = handler;
    public Action<PointerEventArgs>? GetPointerReleased(NodeHandle h) => _pointerReleased[LiveIndexOrZero(h)];
    public void SetPointerWheel(NodeHandle h, Action<WheelEventArgs>? handler) => _pointerWheel[LiveIndex(h)] = handler;
    public Action<WheelEventArgs>? GetPointerWheel(NodeHandle h) => _pointerWheel[LiveIndexOrZero(h)];
    public void SetContextRequested(NodeHandle h, Action<ContextRequestEventArgs>? handler) => _contextRequested[LiveIndex(h)] = handler;
    public Action<ContextRequestEventArgs>? GetContextRequested(NodeHandle h) => _contextRequested[LiveIndexOrZero(h)];
    public void SetFocusChanged(NodeHandle h, Action<bool>? handler) => _focusChanged[LiveIndex(h)] = handler;
    public Action<bool>? GetFocusChanged(NodeHandle h) => _focusChanged[LiveIndexOrZero(h)];
    // Drag-reorder lifecycle columns (E5) — set by the reconciler from BoxEl.CanDrag, read by Input.DragController.
    public void SetDragStarted(NodeHandle h, Action<DragEventArgs>? handler) => _dragStarted[LiveIndex(h)] = handler;
    public Action<DragEventArgs>? GetDragStarted(NodeHandle h) => _dragStarted[LiveIndexOrZero(h)];
    public void SetDragDelta(NodeHandle h, Action<DragEventArgs>? handler) => _dragDelta[LiveIndex(h)] = handler;
    public Action<DragEventArgs>? GetDragDelta(NodeHandle h) => _dragDelta[LiveIndexOrZero(h)];
    public void SetDragCompleted(NodeHandle h, Action<DragEventArgs>? handler) => _dragCompleted[LiveIndex(h)] = handler;
    public Action<DragEventArgs>? GetDragCompleted(NodeHandle h) => _dragCompleted[LiveIndexOrZero(h)];
    public void SetDragCanceled(NodeHandle h, Action? handler) => _dragCanceled[LiveIndex(h)] = handler;
    public Action? GetDragCanceled(NodeHandle h) => _dragCanceled[LiveIndexOrZero(h)];

    // ── implicit brush transitions (WinUI BrushTransition; phase-7 advanced) ──────────────────────
    public bool HasBrushAnims => _brushAnims.Count > 0;
    public void SetBrushAnim(NodeHandle h, in BrushAnim ba)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _brushAnims.GetOrAdd(idx) = ba;
        MarkRecordDirty(idx);
    }
    public bool TryGetBrushAnim(NodeHandle h, out BrushAnim ba) => _brushAnims.TryGet((int)h.Raw.Index, out ba);

    /// <summary>Force a full re-record: mark every occupied node <see cref="NodeFlags.PaintDirty"/>. Device-lost recovery
    /// (threading-render-seam.md §9) — after <c>RecoverDevice</c> the backend's textures + glyph atlas are freshly
    /// recreated and empty, so the next frame must regenerate the WHOLE DrawList to repopulate them via on-demand glyph
    /// re-rasterization + image re-upload. Paired with the host's <c>_needFullLayout</c>. Cold path (once per loss).</summary>
    public void MarkAllPaintDirty()
    {
        for (int i = 1; i < _high; i++)
            if (_gen[i] != 0)
            {
                _flags[i] |= NodeFlags.PaintDirty;
                MarkRecordDirty(i);
            }
    }

    /// <summary>Set the brush cross-fade progress, driven by the unified engine's <c>AnimChannel.BrushFade</c> track
    /// (the separate per-frame AdvanceBrushAnims ticker is deleted). Marks PaintDirty; drops the row at T≥1 so the
    /// recorder snaps to the live color. The engine's BrushFade track keeps the loop awake while a fade runs.</summary>
    public void SetBrushAnimT(int idx, float t)
    {
        if (!_brushAnims.TryGet(idx, out var ba)) return;
        if (_gen[idx] == 0) { _brushAnims.Remove(idx); return; }
        ba.T = t < 0f ? 0f : (t > 1f ? 1f : t);
        _flags[idx] |= NodeFlags.PaintDirty;
        MarkRecordDirty(idx);
        if (ba.T >= 1f) _brushAnims.Remove(idx);
        else _brushAnims.GetOrAdd(idx) = ba;
    }

    // ── text-edit decoration side-table (sparse; only editor TEXT nodes have an entry) ───────────────
    /// <summary>Get-or-create the text-edit row for an editor's text node (caret/IME/focus PODs).</summary>
    public ref TextEditState TextEditRef(NodeHandle h)
        => ref CollectionsMarshal.GetValueRefOrAddDefault(_textEdits, (int)h.Raw.Index, out _);

    public bool HasTextEdit(NodeHandle h) => _textEdits.ContainsKey((int)h.Raw.Index);

    /// <summary>Read the text-edit row by value (false + default if the node is not an editor).</summary>
    public bool TryGetTextEdit(NodeHandle h, out TextEditState s) => _textEdits.TryGetValue((int)h.Raw.Index, out s);

    /// <summary>Drop the node's text-edit row AND its pooled decoration-rect slots (editor unmounted / no longer editable).</summary>
    public void ClearTextEdit(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        _textEdits.Remove(idx);
        _textEditSelRects.Remove(idx);
        _textEditUnderlineRects.Remove(idx);
        MarkRecordDirty(idx);
    }

    /// <summary>Any editor currently focused with a blink-visible caret (cheap host gate; O(editors), usually 0–1).</summary>
    public bool AnyTextEditCaretVisible
    {
        get
        {
            const byte on = TextEditState.CaretVisible | TextEditState.Focused;
            foreach (var kv in _textEdits)
                if ((kv.Value.Flags & on) == on) return true;
            return false;
        }
    }

    /// <summary>
    /// Publish this frame's selection-highlight + IME-clause-underline rects for an editor's text node (TEXT-NODE-LOCAL
    /// coords, computed by the control at edit/drag time). Backing arrays are per-node pooled and grow-only — reused
    /// across frames so a selection drag at pointer rate is 0-alloc once grown. Empty spans clear (count → 0, array kept).
    /// </summary>
    public void SetTextEditRects(NodeHandle node, ReadOnlySpan<RectF> selection, ReadOnlySpan<RectF> compUnderlines)
    {
        int idx = (int)node.Raw.Index;
        StoreRects(_textEditSelRects, idx, selection);
        StoreRects(_textEditUnderlineRects, idx, compUnderlines);
        MarkRecordDirty(idx);
    }

    /// <summary>The node's published selection-highlight rects (empty span when no selection).</summary>
    public ReadOnlySpan<RectF> GetTextEditSelectionRects(NodeHandle h)
        => _textEditSelRects.TryGetValue((int)h.Raw.Index, out var s) && s.Arr is not null
            ? s.Arr.AsSpan(0, s.Count) : default;

    /// <summary>The node's published IME composition-underline rects (empty span when no composition).</summary>
    public ReadOnlySpan<RectF> GetTextEditUnderlineRects(NodeHandle h)
        => _textEditUnderlineRects.TryGetValue((int)h.Raw.Index, out var s) && s.Arr is not null
            ? s.Arr.AsSpan(0, s.Count) : default;

    // ── span-text side-tables (rtb-01 inline runs / rtb-02 read-only selection / api-04 highlight color) ────────────

    /// <summary>Attach a span paragraph's element spans (hyperlink OnClick lookup; written by the reconciler from
    /// <c>SpanTextEl.Spans</c> — the POD shaping overlay rides <c>TextStyle.SpanRunId</c> instead). Null clears.</summary>
    public void SetSpanText(NodeHandle node, TextSpan[]? spans)
    {
        int idx = (int)node.Raw.Index;
        if (spans is null) _spanText.Remove(idx);
        else _spanText[idx] = spans;
    }

    public bool TryGetSpanText(NodeHandle h, out TextSpan[] spans)
        => _spanText.TryGetValue((int)h.Raw.Index, out spans!);

    /// <summary>Attach (or clear, when null) a node's <see cref="GlyphWipe"/> — the sparse carrier for a glyph-run wipe
    /// (the lyrics karaoke). Read by the recorder's Text case → emits <c>DrawGlyphRunGradient</c>. Only wiped nodes pay.</summary>
    public void SetGlyphWipe(NodeHandle h, GlyphWipe? w)
    {
        int idx = (int)h.Raw.Index;
        if (w is null) _glyphWipes.Remove(idx);
        else _glyphWipes.GetOrAdd(idx) = w.Value;
        MarkRecordDirty(idx);
    }
    public bool TryGetGlyphWipe(NodeHandle h, out GlyphWipe w) => _glyphWipes.TryGet((int)h.Raw.Index, out w);

    /// <summary>Swap a text node's span-run id with ownership accounting (the scene row owns one table ref plus one
    /// StringTable ref per span family — mirroring the <c>paint.Text</c> discipline). Reconciler rewrite path; the
    /// free path releases via <see cref="ReleaseSpanRun"/>.</summary>
    public void ReleaseSpanRun(int id)
    {
        if (id == 0) return;
        if (Strings is { } st && SpanRunTable.Shared.Resolve(id) is { } run)
            for (int i = 0; i < run.Spans.Length; i++) st.Release(run.Spans[i].FontFamily);
        SpanRunTable.Shared.Release(id);
    }

    /// <summary>The dispatcher-owned read-only selection range on a selectable text node (UTF-16 [start, end) of the
    /// node's paint text). Mirrors what the published selection rects show; consumers (Ctrl+C copy) read it back.</summary>
    public void SetTextSelection(NodeHandle node, int start, int end)
    {
        int idx = (int)node.Raw.Index;
        _textSelection[idx] = (start, end);
        MarkRecordDirty(idx);
    }

    public bool TryGetTextSelection(NodeHandle h, out int start, out int end)
    {
        if (_textSelection.TryGetValue((int)h.Raw.Index, out var r)) { start = r.Start; end = r.End; return true; }
        start = end = 0;
        return false;
    }

    public void ClearTextSelection(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        _textSelection.Remove(idx);
        MarkRecordDirty(idx);
    }

    /// <summary>Per-node selection-highlight override (api-04, WinUI TextBlock.SelectionHighlightColor —
    /// TextBlock.cpp:266/330). A==0 clears back to the host theme brush (TextEditStyle.SelectionFill — the system
    /// accent, TextSelectionManager.cpp:52-56).</summary>
    public void SetSelectionHighlight(NodeHandle node, ColorF color)
    {
        int idx = (int)node.Raw.Index;
        if (color.A <= 0f) _selectionHighlight.Remove(idx);
        else _selectionHighlight.GetOrAdd(idx) = color;
        MarkRecordDirty(idx);
    }

    public bool TryGetSelectionHighlight(NodeHandle h, out ColorF color)
        => _selectionHighlight.TryGet((int)h.Raw.Index, out color);

    private static void StoreRects(Dictionary<int, (RectF[]? Arr, int Count)> table, int idx, ReadOnlySpan<RectF> rects)
    {
        if (rects.IsEmpty)
        {
            // Clear without dropping the pooled array; never create an entry just to say "empty".
            ref var existing = ref CollectionsMarshal.GetValueRefOrNullRef(table, idx);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref existing)) existing.Count = 0;
            return;
        }
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(table, idx, out _);
        RectF[]? arr = slot.Arr;
        if (arr is null || arr.Length < rects.Length)
        {
            int cap = arr is { Length: > 0 } ? arr.Length : 4;
            while (cap < rects.Length) cap *= 2;
            arr = new RectF[cap];   // grow-only; steady-state (selection drags) reuses with zero alloc
            slot.Arr = arr;
        }
        rects.CopyTo(arr);
        slot.Count = rects.Length;
    }

    /// <summary>First live, enabled, visible node whose keyboard-accelerator chord matches — cold keydown path, O(high).</summary>
    public NodeHandle FindAccelerator(int key, KeyModifiers mods)
    {
        for (int i = 1; i < _high; i++)
        {
            if (_gen[i] == 0 || _interaction[i].AccelKey != key || _interaction[i].AccelMods != mods) continue;
            var h = new NodeHandle(new Handle((uint)i, _gen[i]));
            if (!IsLive(h)) continue;
            if ((_flags[i] & (NodeFlags.Visible | NodeFlags.Disabled)) != NodeFlags.Visible) continue;
            return h;
        }
        return NodeHandle.Null;
    }

    /// <summary>First live, enabled, visible node whose access-key mnemonic matches (Alt+letter) — cold path, O(high).</summary>
    public NodeHandle FindAccessKey(char key)
    {
        for (int i = 1; i < _high; i++)
        {
            if (_gen[i] == 0 || _interaction[i].AccessKey != key) continue;
            var h = new NodeHandle(new Handle((uint)i, _gen[i]));
            if (!IsLive(h)) continue;
            if ((_flags[i] & (NodeFlags.Visible | NodeFlags.Disabled)) != NodeFlags.Visible) continue;
            return h;
        }
        return NodeHandle.Null;
    }

    public bool HasDynamicText => _dynamicTextCount > 0;

    /// <summary>Bumped on every dynamic-text registration CHANGE (mount, unmount, kind swap) — a freshly-mounted node
    /// has no resolved id yet, so the host must force one <see cref="UpdateDynamicText"/> pass even when no displayed
    /// value moved this frame (the intern-on-change fast path would otherwise skip it).</summary>
    public int DynamicTextEpoch { get; private set; }

    public void SetDynamicText(NodeHandle h, DynamicTextKind kind)
    {
        int idx = (int)h.Raw.Index;
        var old = _dynamicText[idx];
        if (old == kind) return;
        if (old == DynamicTextKind.None && kind != DynamicTextKind.None) _dynamicTextCount++;
        else if (old != DynamicTextKind.None && kind == DynamicTextKind.None) _dynamicTextCount--;
        _dynamicText[idx] = kind;
        DynamicTextEpoch++;
        MarkRecordDirty(idx);
    }

    public void UpdateDynamicText(Func<DynamicTextKind, StringId> resolve)
    {
        if (_dynamicTextCount == 0) return;
        for (int i = 1; i < _high; i++)
        {
            var kind = _dynamicText[i];
            if (kind == DynamicTextKind.None) continue;
            var next = resolve(kind);
            if (next == _paint[i].Text) continue;
            if (Strings is { } st) { st.AddRef(next); st.Release(_paint[i].Text); }   // per-frame ids (FPS/ms) reclaim instead of accreting
            _paint[i].Text = next;
            MarkRecordDirty(i);
        }
    }

    private void ClearDynamicText(int idx)
    {
        if (_dynamicText[idx] == DynamicTextKind.None) return;
        _dynamicText[idx] = DynamicTextKind.None;
        _dynamicTextCount--;
    }

    // Arena-backed dirty worklist (layout.md §4.4): the nodes marked LayoutDirty this frame, so scoped relayout is
    // O(dirty) — the host walks each up to its layout boundary and re-solves just that subtree.
    // Record-dirty is the recorder clean-span invalidation bit. It up-propagates because a parent span covers the
    // parent's whole emitted command range, including descendants.
    public const byte RecordDirtyTransform = 1;
    public const byte RecordDirtyContent = 2;

    public bool AnyRecordDirty => _recordDirtyWroteCount > 0;

    public bool IsRecordDirty(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen && _recordDirty[raw] != 0;
    }

    public byte RecordDirtyBits(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen ? _recordDirty[raw] : (byte)0;
    }

    public byte RecordDirtySelfBits(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen ? _recordDirtySelf[raw] : (byte)0;
    }

    public byte RecordDirtyDescendantBits(NodeHandle h)
    {
        uint raw = h.Raw.Index;
        return raw > 0 && raw < (uint)_high && _gen[raw] == h.Raw.Gen ? _recordDirtyDescendant[raw] : (byte)0;
    }

    public bool IsRecordContentDirty(NodeHandle h)
        => (RecordDirtyBits(h) & RecordDirtyContent) != 0;

    public void ClearRecordDirty()
    {
        for (int i = 0; i < _recordDirtyWroteCount; i++)
        {
            int idx = _recordDirtyWrote[i];
            if ((uint)idx < (uint)_recordDirty.Length)
            {
                _recordDirty[idx] = 0;
                _recordDirtySelf[idx] = 0;
                _recordDirtyDescendant[idx] = 0;
            }
            _recordDirtyWrote[i] = 0;
        }
        _recordDirtyWroteCount = 0;
    }

    private void MarkRecordDirty(int idx) => MarkRecordDirty(idx, RecordDirtyContent);

    private void MarkRecordDirty(int idx, byte bits)
    {
        if ((uint)idx >= (uint)_high || _gen[idx] == 0 || bits == 0) return;
        for (int n = idx; n != 0; n = _parent[n])
        {
            byte oldAggregate = _recordDirty[n];
            byte oldSelf = _recordDirtySelf[n];
            byte oldDescendant = _recordDirtyDescendant[n];
            byte nextAggregate = (byte)(oldAggregate | bits);
            byte nextSelf = n == idx ? (byte)(oldSelf | bits) : oldSelf;
            byte nextDescendant = n == idx ? oldDescendant : (byte)(oldDescendant | bits);
            if (nextAggregate == oldAggregate && nextSelf == oldSelf && nextDescendant == oldDescendant)
                continue;

            _recordDirty[n] = nextAggregate;
            _recordDirtySelf[n] = nextSelf;
            _recordDirtyDescendant[n] = nextDescendant;
            if (oldAggregate == 0 && oldSelf == 0 && oldDescendant == 0)
            {
                if (_recordDirtyWroteCount == _recordDirtyWrote.Length)
                {
                    int next = Math.Max(_recordDirtyWrote.Length * 2, _gen.Length);
                    Array.Resize(ref _recordDirtyWrote, next);
                }
                _recordDirtyWrote[_recordDirtyWroteCount++] = n;
            }
        }
    }

    private readonly List<NodeHandle> _layoutDirty = new();
    /// <summary>Set once any node is marked <see cref="NodeFlags.LayoutDirty"/> this frame (cheap host gate for scoped relayout).</summary>
    public bool AnyLayoutDirty => _layoutDirty.Count > 0;
    /// <summary>The nodes marked LayoutDirty this frame (the scoped-relayout worklist).</summary>
    public IReadOnlyList<NodeHandle> LayoutDirtyNodes => _layoutDirty;
    /// <summary>Cleared by the host after it runs (scoped) layout — clears the worklist and the per-node LayoutDirty bits.</summary>
    public void ClearLayoutDirty()
    {
        for (int i = 0; i < _layoutDirty.Count; i++) { var h = _layoutDirty[i]; if (IsLive(h)) _flags[h.Raw.Index] &= ~NodeFlags.LayoutDirty; }
        _layoutDirty.Clear();
    }

    // Frame-scoped transform-motion worklist (mirrors _layoutDirty): the nodes whose transform was written THIS frame
    // (scroll/fling/drag/FLIP/sticky — every motion writer marks TransformDirty). The recorder reads the bit to gate
    // glyph baseline snapping (moving text rides sub-pixel with its plate); the host clears the bits right after record,
    // so a node at rest re-snaps on the very next recorded frame.
    private readonly List<NodeHandle> _transformWrote = new();
    /// <summary>True when any node's transform was written this frame (host gate for the one-frame settle repaint).</summary>
    public bool AnyTransformWrote => _transformWrote.Count > 0;
    /// <summary>Cleared by the host right after record — clears the per-node TransformDirty bits marked this frame.</summary>
    public void ClearTransformDirty()
    {
        for (int i = 0; i < _transformWrote.Count; i++) { var h = _transformWrote[i]; if (IsLive(h)) _flags[h.Raw.Index] &= ~NodeFlags.TransformDirty; }
        _transformWrote.Clear();
    }

    // Persistent registry of nodes carrying BoundsAnimated. Entries are appended on the 0→1 transition and compacted
    // by the host's FLIP capture pass, so the empty/common case avoids a recursive full-tree search.
    private readonly List<NodeHandle> _boundsAnimated = new();
    internal List<NodeHandle> BoundsAnimatedNodes => _boundsAnimated;

    // Scene-owned VirtualRangeDirty worklist (E6): mirrors _layoutDirty — appended on the 0→1 edge so the reconciler's
    // ReRealizeVirtuals iterates ONLY the dirty viewports instead of scanning the whole _virtuals dictionary every frame.
    // NOT cleared per-frame like _layoutDirty: the reconciler swap-removes an entry once its window fully realizes (the
    // flag clears); a budget-deferred / mid-warm entry stays queued (still flagged) until it catches up. Duplicate-free by
    // construction — the flag stays set while queued, so no fresh 0→1 edge re-adds it.
    private readonly List<NodeHandle> _virtualRangeDirty = new();
    /// <summary>The queued VirtualRangeDirty viewports (the reconciler's realize worklist). Mutable: the reconciler
    /// swap-removes consumed entries in place (same-assembly, mirrors how the host consumes _layoutDirty).</summary>
    internal List<NodeHandle> VirtualRangeDirtyNodes => _virtualRangeDirty;

    public void Mark(NodeHandle h, NodeFlags flags)
    {
        int idx = (int)h.Raw.Index;
        NodeFlags old = _flags[idx];
        if ((flags & NodeFlags.LayoutDirty) != 0 && (old & NodeFlags.LayoutDirty) == 0) _layoutDirty.Add(h);
        if ((flags & NodeFlags.TransformDirty) != 0 && (old & NodeFlags.TransformDirty) == 0) _transformWrote.Add(h);
        if ((flags & NodeFlags.BoundsAnimated) != 0 && (old & NodeFlags.BoundsAnimated) == 0) _boundsAnimated.Add(h);
        if ((flags & NodeFlags.VirtualRangeDirty) != 0 && (old & NodeFlags.VirtualRangeDirty) == 0) _virtualRangeDirty.Add(h);
        byte recordBits = 0;
        if ((flags & NodeFlags.TransformDirty) != 0) recordBits |= RecordDirtyTransform;
        if ((flags & (NodeFlags.LayoutDirty | NodeFlags.PaintDirty)) != 0) recordBits |= RecordDirtyContent;
        if (recordBits != 0) MarkRecordDirty(idx, recordBits);
        _flags[idx] = old | flags;
    }
    public void Unmark(NodeHandle h, NodeFlags flags) => _flags[h.Raw.Index] &= ~flags;

    // ── scroll/virtual side-table (sparse; only viewport nodes have an entry) ──
    /// <summary>Get-or-create the scroll row for a viewport node; marks it <see cref="NodeFlags.Scrollable"/>.</summary>
    public ref ScrollState ScrollRef(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        ref ScrollState s = ref _scroll.GetOrAdd(idx, out bool existed);
        if (!existed) { s = ScrollState.Default; _flags[idx] |= NodeFlags.Scrollable; MarkRecordDirty(idx); }
        return ref s;
    }
    public bool HasScroll(NodeHandle h) => _scroll.Contains((int)h.Raw.Index);
    /// <summary>Read the scroll row by value (default if the node is not a viewport).</summary>
    public bool TryGetScroll(NodeHandle h, out ScrollState s) => _scroll.TryGet((int)h.Raw.Index, out s);

    // ── hit-test pass-through (WinUI FlyoutBase.OverlayInputPassThroughElement) ──────────────────
    // A light-dismiss scrim registers ONE target subtree whose rendered bounds it yields to: pointer input there
    // bypasses the scrim and reaches the content beneath (the MenuBar hover-switches titles with a menu open,
    // FlyoutBase_Partial.cpp:3922-3938). Sparse — O(open scrims), cleared on free.
    private readonly ColdSlab<NodeHandle> _hitPassThrough = new();   // GEN-17 (wired)

    public void SetHitTestPassThrough(NodeHandle node, NodeHandle target)
    {
        if (!IsLive(node)) return;
        if (target.IsNull) _hitPassThrough.Remove((int)node.Raw.Index);
        else _hitPassThrough.GetOrAdd((int)node.Raw.Index) = target;
    }

    public bool TryGetHitTestPassThrough(NodeHandle node, out NodeHandle target)
        => _hitPassThrough.TryGet((int)node.Raw.Index, out target);

    // The CSS position:sticky registry was removed — sticky is now a generic ScrollBind pin op
    // (FluentGpu.Animation.ScrollBindTable + ScrollBindEval.ApplyPinAndFlagPass / NodeFlags.StickyPinned).

    // ── generic scroll-binding slab (design/plans/generic-hookable-scroll-engine-design.md) ──────────────
    // The reconciler-owned dense slab of ScrollBind rows. The host evaluates them at the offset-write chokepoint
    // (offset/band/velocity/phase-sourced ops) and the phase-7 pin pass (PinKind ops). Sticky + overscroll-stretch are
    // two configured rows here — not bespoke passes / private side-tables.
    private readonly FluentGpu.Animation.ScrollBindTable _scrollBinds = new();
    public FluentGpu.Animation.ScrollBindTable ScrollBinds => _scrollBinds;
    /// <summary>O(1) census of live scroll-binding rows (subsumes the old StickyCount).</summary>
    public int ScrollBindCount => _scrollBinds.Count;

    // ── scroll-geometry observer registry (the change-only escape hatch; ScrollEl.OnScrollGeometryChanged) ──
    // Node index → projection+action. The reconciler Set/Clears it; the host evaluates the projection after the
    // integrator settles and fires the action only when the projected long key changes (SwiftUI onScrollGeometryChange).
    private readonly Dictionary<int, FluentGpu.Animation.ScrollObserverRow> _scrollObs = new();
    public Dictionary<int, FluentGpu.Animation.ScrollObserverRow> ScrollObservers => _scrollObs;
    public int ScrollObserverCount => _scrollObs.Count;
    public void SetScrollObserver(NodeHandle h, Func<FluentGpu.Animation.ScrollGeometry, long>? project, Action<FluentGpu.Animation.ScrollGeometry>? action)
    {
        int idx = (int)h.Raw.Index;
        if (project is null || action is null) { _scrollObs.Remove(idx); return; }
        ref var row = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_scrollObs, idx, out _);
        row.Node = h; row.Project = project; row.Action = action;
    }
    public void ClearScrollObserver(NodeHandle h) => _scrollObs.Remove((int)h.Raw.Index);

    /// <summary>Get-or-create the variable-height extent table for a viewport, (re)building it on item-count change.</summary>
    public ExtentTable ExtentTableFor(NodeHandle h, int itemCount, float estimate)
    {
        int idx = (int)h.Raw.Index;
        if (!_extents.TryGetValue(idx, out var t)) { t = new ExtentTable(itemCount, estimate); _extents[idx] = t; }
        else if (t.Count != itemCount) t.Reset(itemCount, estimate);
        return t;
    }
    public bool TryGetExtents(NodeHandle h, out ExtentTable? t) => _extents.TryGetValue((int)h.Raw.Index, out t);

    public void SetGrid(NodeHandle h, in GridSpec spec) => _grids.GetOrAdd((int)h.Raw.Index) = spec;
    public bool HasGrid(NodeHandle h) => _grids.Contains((int)h.Raw.Index);
    public bool TryGetGrid(NodeHandle h, out GridSpec spec) => _grids.TryGet((int)h.Raw.Index, out spec);

    // ── rich-paint side-tables ──
    /// <summary>Get-or-create the eased-interaction row for a node (hover/press progress).</summary>
    public ref InteractionAnim InteractRef(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.InteractionAnim;
        ref InteractionAnim s = ref _interact.GetOrAdd(idx, out bool existed);
        if (!existed) { s = InteractionAnim.Default; MarkRecordDirty(idx); }
        return ref s;
    }
    public bool TryGetInteract(NodeHandle h, out InteractionAnim s) => _interact.TryGet((int)h.Raw.Index, out s);

    /// <summary>Write the eased hover (or press) progress for a node — driven by the engine's HoverFade/PressFade track
    /// (the deleted InteractionAnimator.Tick's job). The row exists (the fade was seeded through InteractRef); marks
    /// PaintDirty so the recorder re-composites the hover/press cross-fade + scale.</summary>
    public void SetInteractT(NodeHandle node, bool press, float t)
    {
        if (!IsLive(node)) return;
        ref InteractionAnim ia = ref InteractRef(node);
        if (press) ia.PressT = t; else ia.HoverT = t;
        Mark(node, NodeFlags.PaintDirty);
    }

    public void SetShadow(NodeHandle h, in ShadowSpec s)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _shadows.GetOrAdd(idx) = s;
        MarkRecordDirty(idx);
    }
    public bool TryGetShadow(NodeHandle h, out ShadowSpec s) => _shadows.TryGet((int)h.Raw.Index, out s);
    public void ClearShadow(NodeHandle h) { int idx = (int)h.Raw.Index; _shadows.Remove(idx); MarkRecordDirty(idx); }

    public void SetArc(NodeHandle h, in ArcSpec a)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _arcs.GetOrAdd(idx) = a;
        MarkRecordDirty(idx);
    }
    public bool TryGetArc(NodeHandle h, out ArcSpec a) => _arcs.TryGet((int)h.Raw.Index, out a);
    public void ClearArc(NodeHandle h) { int idx = (int)h.Raw.Index; _arcs.Remove(idx); MarkRecordDirty(idx); }

    public void SetPolylineStroke(NodeHandle h, in PolylineStrokeSpec p)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _polylines.GetOrAdd(idx) = p;
        MarkRecordDirty(idx);
    }
    public bool TryGetPolylineStroke(NodeHandle h, out PolylineStrokeSpec p) => _polylines.TryGet((int)h.Raw.Index, out p);
    public void ClearPolylineStroke(NodeHandle h) { int idx = (int)h.Raw.Index; _polylines.Remove(idx); MarkRecordDirty(idx); }

    public void SetGradient(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _gradients.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetGradient(NodeHandle h, out GradientSpec g) => _gradients.TryGet((int)h.Raw.Index, out g);
    public void ClearGradient(NodeHandle h) { int idx = (int)h.Raw.Index; _gradients.Remove(idx); MarkRecordDirty(idx); }

    public void SetBorderBrush(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _borderBrushes.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetBorderBrush(NodeHandle h, out GradientSpec g) => _borderBrushes.TryGet((int)h.Raw.Index, out g);
    public void ClearBorderBrush(NodeHandle h) { int idx = (int)h.Raw.Index; _borderBrushes.Remove(idx); MarkRecordDirty(idx); }

    public void SetHoverGradient(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _hoverGradients.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetHoverGradient(NodeHandle h, out GradientSpec g) => _hoverGradients.TryGet((int)h.Raw.Index, out g);
    public void ClearHoverGradient(NodeHandle h) { int idx = (int)h.Raw.Index; _hoverGradients.Remove(idx); MarkRecordDirty(idx); }

    public void SetPressedGradient(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _pressedGradients.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetPressedGradient(NodeHandle h, out GradientSpec g) => _pressedGradients.TryGet((int)h.Raw.Index, out g);
    public void ClearPressedGradient(NodeHandle h) { int idx = (int)h.Raw.Index; _pressedGradients.Remove(idx); MarkRecordDirty(idx); }

    public void SetHoverBorderBrush(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _hoverBorderBrushes.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetHoverBorderBrush(NodeHandle h, out GradientSpec g) => _hoverBorderBrushes.TryGet((int)h.Raw.Index, out g);
    public void ClearHoverBorderBrush(NodeHandle h) { int idx = (int)h.Raw.Index; _hoverBorderBrushes.Remove(idx); MarkRecordDirty(idx); }

    public void SetPressedBorderBrush(NodeHandle h, in GradientSpec g)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _pressedBorderBrushes.GetOrAdd(idx) = g;
        MarkRecordDirty(idx);
    }
    public bool TryGetPressedBorderBrush(NodeHandle h, out GradientSpec g) => _pressedBorderBrushes.TryGet((int)h.Raw.Index, out g);
    public void ClearPressedBorderBrush(NodeHandle h) { int idx = (int)h.Raw.Index; _pressedBorderBrushes.Remove(idx); MarkRecordDirty(idx); }

    public void SetAcrylic(NodeHandle h, in AcrylicSpec a)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _acrylics.GetOrAdd(idx) = a;
        MarkRecordDirty(idx);
    }
    public bool TryGetAcrylic(NodeHandle h, out AcrylicSpec a) => _acrylics.TryGet((int)h.Raw.Index, out a);
    public void ClearAcrylic(NodeHandle h) { int idx = (int)h.Raw.Index; _acrylics.Remove(idx); MarkRecordDirty(idx); }

    public void SetEdgeFade(NodeHandle h, in EdgeFadeSpec e)
    {
        int idx = (int)h.Raw.Index;
        _flags[idx] |= NodeFlags.SparsePaint;
        _edgeFades.GetOrAdd(idx) = e;
        MarkRecordDirty(idx);
    }
    public bool TryGetEdgeFade(NodeHandle h, out EdgeFadeSpec e) => _edgeFades.TryGet((int)h.Raw.Index, out e);
    public void ClearEdgeFade(NodeHandle h) { int idx = (int)h.Raw.Index; _edgeFades.Remove(idx); MarkRecordDirty(idx); }

    // ── E5-L2 drag-drop columns (BoxEl.Draggable / BoxEl.DropTarget → Input.DragDropContext) ──────
    /// <summary>Set (or clear, null) the node's typed drag-source spec — the reconciler writes it from
    /// <c>BoxEl.Draggable</c>; the L2 context resolves the nearest one up the chain at L1 promotion.</summary>
    public void SetDragSource(NodeHandle h, DragSource? s)
    {
        int idx = (int)h.Raw.Index;
        if (s is null) _dragSources.Remove(idx);
        else _dragSources[idx] = s;
    }

    public bool TryGetDragSource(NodeHandle h, out DragSource? s)
    {
        bool found = _dragSources.TryGetValue((int)h.Raw.Index, out var v);
        s = v;
        return found;
    }

    /// <summary>Set (or clear, null) the node's drop-target spec — the reconciler writes it from
    /// <c>BoxEl.DropTarget</c>; the L2 context walks the hit chain per move for the nearest ACCEPTING one.</summary>
    public void SetDropTarget(NodeHandle h, DropTargetSpec? t)
    {
        int idx = (int)h.Raw.Index;
        if (t is null) _dropTargets.Remove(idx);
        else _dropTargets[idx] = t;
    }

    public bool TryGetDropTarget(NodeHandle h, out DropTargetSpec? t)
    {
        bool found = _dropTargets.TryGetValue((int)h.Raw.Index, out var v);
        t = v;
        return found;
    }

    /// <summary>Cheap per-move gate: any drop target in the scene at all (skips the chain walk for plain reorders).</summary>
    public bool HasDropTargets => _dropTargets.Count > 0;

    // ── UseGesture subscriptions (input-a11y.md §13; the Hooks⇄Input seam) ──────────────────────────────────────
    /// <summary>Cheap census: any node in the scene declared a <c>UseGesture</c> hook (lets the dispatcher skip the
    /// gesture-routing probe entirely when no component subscribes — the common case).</summary>
    public bool HasGestureSubs => _gestureSubs.Count > 0;

    /// <summary>Install / merge one <c>UseGesture</c> handler on a node (input-a11y.md §13). Idempotent per (node, kind):
    /// the latest handler for a kind replaces the prior one (a re-mount re-asserts the same closure); other kinds on the
    /// node are preserved (a component may declare Tap AND Pan). Called by <c>FluentGpu.Hooks.UseGesture</c> on mount.</summary>
    public void SetGestureHandler(NodeHandle h, GestureType kind, Action<GestureEventArgs>? handler)
    {
        int idx = LiveIndex(h);
        ref GestureSubscription s = ref CollectionsMarshal.GetValueRefOrAddDefault(_gestureSubs, idx, out _);
        s.Set(kind, handler);
        // Maintain GestureBit so the node hit-tests (a UseGesture-only node is otherwise non-interactive): set while any
        // handler is installed, cleared when the last one goes (Input opens a gesture arena only over a hit node).
        if (s.HasAny) _interaction[idx].HandlerMask |= InteractionInfo.GestureBit;
        else { _interaction[idx].HandlerMask &= ~(uint)InteractionInfo.GestureBit; _gestureSubs.Remove(idx); }
    }

    /// <summary>True iff the node declared a <c>UseGesture</c> for <paramref name="kind"/> (the dispatcher enrolls a
    /// matching arena member only for declared kinds). Stale/dead handles read false.</summary>
    public bool WantsGesture(NodeHandle h, GestureType kind)
        => _gestureSubs.TryGetValue(LiveIndexOrZero(h), out var s) && s.Handler(kind) is not null;

    /// <summary>The handler for (node, kind), or null. The dispatcher invokes it when the gesture arena resolves the
    /// node's matching member as the winner (§7A.2). Stale/dead handles read null.</summary>
    public Action<GestureEventArgs>? GetGestureHandler(NodeHandle h, GestureType kind)
        => _gestureSubs.TryGetValue(LiveIndexOrZero(h), out var s) ? s.Handler(kind) : null;

    /// <summary>Get-or-create the per-node text measure cache row (layout.md §2.3).</summary>
    public ref TextMeasureCache MeasureCacheRef(NodeHandle h)
        => ref _measureCache.GetOrAdd((int)h.Raw.Index);

    public NodeHandle FirstChild(NodeHandle h) => Wrap(_firstChild[h.Raw.Index]);
    public NodeHandle NextSibling(NodeHandle h) => Wrap(_nextSib[h.Raw.Index]);
    public NodeHandle Parent(NodeHandle h) => Wrap(_parent[h.Raw.Index]);
    public NodeHandle LastChild(NodeHandle h) => Wrap(_lastChild[h.Raw.Index]);
    public int ChildCount(NodeHandle h) => _childCount[h.Raw.Index];

    /// <summary>Absolute (window-space) rect = local W/H at the summed origin up the parent chain. (Slice uses translation-only transforms.)</summary>
    public RectF AbsoluteRect(NodeHandle h)
    {
        float x = 0f, y = 0f;
        for (var n = h; !n.IsNull; n = Parent(n))
        {
            x += _bounds[n.Raw.Index].X + _paint[n.Raw.Index].LocalTransform.Dx;   // include scroll / composited translation
            y += _bounds[n.Raw.Index].Y + _paint[n.Raw.Index].LocalTransform.Dy;
            var parent = Parent(n);
            if (!parent.IsNull)
            {
                x += _paint[parent.Raw.Index].ChildShiftX;
                y += _paint[parent.Raw.Index].ChildShiftY;
            }
        }
        return new RectF(x, y, _bounds[h.Raw.Index].W, _bounds[h.Raw.Index].H);
    }

    private NodeHandle Wrap(int idx) => idx == 0 ? NodeHandle.Null : new NodeHandle(new Handle((uint)idx, _gen[idx]));

    private void Grow() => ResizeColumns(_gen.Length * 2);

    /// <summary>Resize every parallel SoA column to <paramref name="n"/> slots (grow OR shrink). The single place the
    /// column list lives — <see cref="Grow"/> (×2) and <see cref="TrimExcessCapacity"/> (tail-trim) both route here so
    /// the set can never drift between them.</summary>
    private void ResizeColumns(int n)
    {
        Array.Resize(ref _gen, n); Array.Resize(ref _nextFree, n);
        Array.Resize(ref _parent, n); Array.Resize(ref _firstChild, n); Array.Resize(ref _lastChild, n);
        Array.Resize(ref _prevSib, n); Array.Resize(ref _nextSib, n); Array.Resize(ref _childCount, n);
        Array.Resize(ref _elementTypeId, n); Array.Resize(ref _layout, n); Array.Resize(ref _bounds, n);
        Array.Resize(ref _paint, n); Array.Resize(ref _dynamicText, n); Array.Resize(ref _interaction, n); Array.Resize(ref _flags, n);
        Array.Resize(ref _recordDirty, n); Array.Resize(ref _recordDirtySelf, n); Array.Resize(ref _recordDirtyDescendant, n);
        Array.Resize(ref _recordDirtyWrote, n);
        if (_recordDirtyWroteCount > n) _recordDirtyWroteCount = n;
        Array.Resize(ref _click, n); Array.Resize(ref _boundsChanged, n); Array.Resize(ref _boundsDelivered, n); Array.Resize(ref _keyHandler, n); Array.Resize(ref _charHandler, n);
        Array.Resize(ref _pointerDown, n); Array.Resize(ref _drag, n); Array.Resize(ref _hoverMove, n); Array.Resize(ref _pointerExit, n);
        Array.Resize(ref _pointerPressed, n); Array.Resize(ref _pointerReleased, n); Array.Resize(ref _pointerWheel, n); Array.Resize(ref _contextRequested, n);
        Array.Resize(ref _focusChanged, n);
        Array.Resize(ref _dragStarted, n); Array.Resize(ref _dragDelta, n);
        Array.Resize(ref _dragCompleted, n); Array.Resize(ref _dragCanceled, n);
    }

    /// <summary>Conservative slab tail-trim (mem-02): the SoA columns only ever GROW (Gen0 churn at the reconcile edge
    /// ratchets <see cref="Capacity"/> to the session high-water and never gives it back). Index-stability is sacred —
    /// live handles MUST keep their indices — so the ONLY legal shrink is cutting the all-free TAIL above the highest
    /// LIVE index. Find that index H; when the slab is mostly empty tail (capacity &gt; 2·(H+1) and past a floor), shrink
    /// every column to the next pow2 ≥ H+1 and drop the freelist entries that fall in the trimmed tail. Returns the slot
    /// count reclaimed (0 = no-op — nothing trimmable, or below the floor). The host calls it on a slow idle cadence;
    /// allocation at trim time (the transient free-set) is acceptable since it runs only when fully idle.</summary>
    public int TrimExcessCapacity()
    {
        int cap = _gen.Length;
        const int FloorCap = 256;   // never shrink below this — keeps a sane reusable working set, matches the guard
        if (cap <= FloorCap) return 0;

        // Free indices below _high are exactly the freelist members; build the set (transient — idle-time only).
        var free = new HashSet<int>();
        for (int f = _freeHead; f != 0; f = _nextFree[f]) free.Add(f);

        // Highest LIVE index: scan down from the high-water, skipping freed slots. (Every index in [1,_high) was
        // allocated at least once, so below-high ⇒ live XOR free; H=0 ⇒ no live nodes at all.)
        int h = 0;
        for (int i = _high - 1; i >= 1; i--)
            if (!free.Contains(i)) { h = i; break; }

        int target = h + 1;                                   // keep slots [0, h] live-addressable
        // Mostly-empty-tail gate: only worth a realloc when the slab is more than double the live span and past the floor.
        if (cap <= 2 * target || cap <= FloorCap) return 0;

        int newCap = 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(1, target - 1)));
        if (newCap < FloorCap) newCap = FloorCap;
        if (newCap >= cap) return 0;                          // pow2 rounding swallowed the slack — nothing to give back

        ResizeColumns(newCap);
        _high = target;                                       // the tail above H is gone; fresh capacity [target,newCap) is reachable via _high++

        // Rebuild the freelist keeping only entries that survive the trim (index < the new _high). Built from the
        // pre-captured `free` set (NOT by walking the just-resized _nextFree — entries ≥ newCap are now out of bounds).
        // Freed slots in [target, newCap) become plain fresh capacity (reachable via _high++); slots ≥ newCap are gone.
        int newHead = 0;
        foreach (int f in free)
            if (f < target) { _nextFree[f] = newHead; newHead = f; }
        _freeHead = newHead;

        return cap - newCap;
    }

    // ISceneBackend explicit ref returns already satisfied above.
    ref LayoutInput ISceneBackend.Layout(NodeHandle node) => ref _layout[node.Raw.Index];
    ref NodePaint ISceneBackend.Paint(NodeHandle node) => ref _paint[node.Raw.Index];
    ref InteractionInfo ISceneBackend.Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
}
