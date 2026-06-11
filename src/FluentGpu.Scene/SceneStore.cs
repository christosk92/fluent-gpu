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
public sealed class SceneStore : ISceneBackend
{
    // spine
    private uint[] _gen;
    private int[] _nextFree;
    private int _freeHead;
    private int _high = 1;     // index 0 reserved = null

    // Exit-animation orphans: nodes removed from the logical tree but kept LIVE (drawing) until their exit animation
    // settles, then reclaimed. Detached from their parent (so reconcile + layout skip them) and drawn by a separate
    // recorder pass at their FROZEN parent-world origin. Bounded by MaxOrphans (overflow instant-frees the oldest).
    private struct OrphanEntry { public NodeHandle Node; public float Px, Py; }
    private readonly List<OrphanEntry> _orphans = new();
    private const int MaxOrphans = 64;

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
    private Action?[] _click;         // managed edge payload (GC ref at the edge only)
    private Action<KeyEventArgs>?[] _keyHandler;
    private Action<CharEventArgs>?[] _charHandler;   // text (character) input handler
    private Action<Point2>?[] _pointerDown;   // position-aware (local coords) press / drag handlers
    private Action<Point2>?[] _drag;
    private Action<Point2>?[] _hoverMove;     // position-aware bare-hover move (no press) — RatingControl preview, etc.
    private Action?[] _pointerExit;           // fired when the pointer leaves the node (hover lost) — reset hover preview
    private Action<PointerEventArgs>?[] _pointerPressed;   // press w/ click-count + modifiers (double/triple-click, drag-select)
    private Action<WheelEventArgs>?[] _pointerWheel;        // element-level wheel hook (pre-viewport-scroll; NumberBox)
    private Action<Point2>?[] _contextRequested;           // right-click / Menu-key context request (local coords)
    private Action<bool>?[] _focusChanged;                 // dispatcher focus moved onto (true) / off (false) this node (GotFocus/LostFocus)
    // Drag-reorder lifecycle (E5): fired by Input.DragController once a CanDrag press crosses the drag threshold.
    private Action<DragEventArgs>?[] _dragStarted;         // threshold crossed → the gesture is a drag (WinUI DragStarting)
    private Action<DragEventArgs>?[] _dragDelta;           // every pointer move while the drag is active (coords + velocity)
    private Action<DragEventArgs>?[] _dragCompleted;       // released after an active drag (the click is suppressed)
    private Action?[] _dragCanceled;                       // Escape / capture loss / window blur aborted the drag

    // Sparse side-table for scroll/virtual viewports (O(viewports), not one-per-node). Keyed by node index.
    private readonly Dictionary<int, ScrollState> _scroll = new();
    // Per-variable-list extent tables (Fenwick); persist across frames. Keyed by viewport node index.
    private readonly Dictionary<int, ExtentTable> _extents = new();
    // Grid specs for grid-container nodes (O(grids)). Keyed by node index.
    private readonly Dictionary<int, GridSpec> _grids = new();
    // Optional rich-paint side-tables (O(decorated nodes), keyed by node index): eased interaction, shadow, gradient, acrylic.
    private readonly Dictionary<int, InteractionAnim> _interact = new();
    private readonly Dictionary<int, ShadowSpec> _shadows = new();
    private readonly Dictionary<int, ArcSpec> _arcs = new();
    private readonly Dictionary<int, PolylineStrokeSpec> _polylines = new();
    private readonly Dictionary<int, GradientSpec> _gradients = new();
    private readonly Dictionary<int, GradientSpec> _borderBrushes = new();   // gradient border stroke (elevation edge)
    // Stateful gradient variants (P4b): the recorder per-frame interpolates resting→state stops by the eased hover/press
    // progress. Sparse (O(state-gradient nodes)). Stop arrays are mount-allocated + stable — never rebuilt per frame.
    private readonly Dictionary<int, GradientSpec> _hoverGradients = new();
    private readonly Dictionary<int, GradientSpec> _pressedGradients = new();
    private readonly Dictionary<int, GradientSpec> _hoverBorderBrushes = new();
    private readonly Dictionary<int, GradientSpec> _pressedBorderBrushes = new();
    private readonly Dictionary<int, AcrylicSpec> _acrylics = new();
    // Per-text-node measure cache (pure-function: (text,style,availW) → size); self-invalidating, freed on FreeSubtree.
    private readonly Dictionary<int, TextMeasureCache> _measureCache = new();
    // Implicit brush transitions (WinUI BrushTransition): sparse, O(transitioning nodes), advanced at phase 7.
    private readonly Dictionary<int, BrushAnim> _brushAnims = new();
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
    private readonly Dictionary<int, ColorF> _selectionHighlight = new();
    // E5-L2 drag-drop side-tables (sparse, O(sources)/O(targets), keyed by node index): the reconciler writes them
    // from BoxEl.Draggable / BoxEl.DropTarget; Input.DragDropContext reads them at promotion / per pointer move.
    private readonly Dictionary<int, DragSource> _dragSources = new();
    private readonly Dictionary<int, DropTargetSpec> _dropTargets = new();

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
        _click = new Action?[capacity];
        _keyHandler = new Action<KeyEventArgs>?[capacity];
        _charHandler = new Action<CharEventArgs>?[capacity];
        _pointerDown = new Action<Point2>?[capacity];
        _drag = new Action<Point2>?[capacity];
        _hoverMove = new Action<Point2>?[capacity];
        _pointerExit = new Action?[capacity];
        _pointerPressed = new Action<PointerEventArgs>?[capacity];
        _pointerWheel = new Action<WheelEventArgs>?[capacity];
        _contextRequested = new Action<Point2>?[capacity];
        _focusChanged = new Action<bool>?[capacity];
        _dragStarted = new Action<DragEventArgs>?[capacity];
        _dragDelta = new Action<DragEventArgs>?[capacity];
        _dragCompleted = new Action<DragEventArgs>?[capacity];
        _dragCanceled = new Action?[capacity];
    }

    public int LiveCount { get; private set; }

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
        _click[idx] = null;
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
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
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
        _pointerWheel[idx] = null;
        _contextRequested[idx] = null;
        _focusChanged[idx] = null;
        _dragStarted[idx] = null;
        _dragDelta[idx] = null;
        _dragCompleted[idx] = null;
        _dragCanceled[idx] = null;
        ClearDynamicText(idx);
        _scroll.Remove(idx);
        _extents.Remove(idx);
        _grids.Remove(idx);
        _hitPassThrough.Remove(idx);
        _interact.Remove(idx);
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
        _measureCache.Remove(idx);
        _brushAnims.Remove(idx);
        _textEdits.Remove(idx);
        _textEditSelRects.Remove(idx);
        _textEditUnderlineRects.Remove(idx);
        _spanText.Remove(idx);
        _textSelection.Remove(idx);
        _selectionHighlight.Remove(idx);
        _dragSources.Remove(idx);
        _dropTargets.Remove(idx);
        if (DragGhost == node) DragGhost = NodeHandle.Null;   // a freed ghost must not linger in the recorder's top band
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
        if (_prevSib[c] != 0) _nextSib[_prevSib[c]] = _nextSib[c]; else _firstChild[p] = _nextSib[c];
        if (_nextSib[c] != 0) _prevSib[_nextSib[c]] = _prevSib[c]; else _lastChild[p] = _prevSib[c];
        _childCount[p]--;
        _parent[c] = _prevSib[c] = _nextSib[c] = 0;
    }

    // ── exit-animation orphans ────────────────────────────────────────────────────────────────────
    /// <summary>Remove a node from the logical tree but keep it LIVE and drawing: detach from its parent (so reconcile +
    /// layout no longer see it), freeze its parent-world origin, flag it Exiting. The recorder draws it via the orphan
    /// pass at that frozen origin while its opacity/transform animate out; <see cref="ReclaimOrphan"/> frees it on settle.</summary>
    public void Orphan(NodeHandle node)
    {
        if (!IsLive(node)) return;
        if (_orphans.Count >= MaxOrphans) { var o = _orphans[0]; _orphans.RemoveAt(0); FreeSubtree(o.Node); }  // budget: instant-free oldest
        RectF abs = AbsoluteRect(node);
        int idx = (int)node.Raw.Index;
        float px = abs.X - _bounds[idx].X, py = abs.Y - _bounds[idx].Y;   // frozen parent-world origin
        DetachFromParent(idx);
        _flags[idx] |= NodeFlags.Exiting;
        _orphans.Add(new OrphanEntry { Node = node, Px = px, Py = py });
    }

    /// <summary>Free a settled exit orphan (the deferred <see cref="FreeSubtree"/> — gen bump → handle dead).</summary>
    public void ReclaimOrphan(NodeHandle node)
    {
        for (int i = _orphans.Count - 1; i >= 0; i--)
            if (_orphans[i].Node == node) { _orphans.RemoveAt(i); break; }
        FreeSubtree(node);
    }

    public bool IsOrphan(NodeHandle node)
    {
        for (int i = 0; i < _orphans.Count; i++) if (_orphans[i].Node == node) return true;
        return false;
    }

    /// <summary>Count of exit orphans currently animating out (the host keeps painting while &gt; 0).</summary>
    public int OrphanCount => _orphans.Count;

    /// <summary>The i-th orphan node + its frozen parent-world origin (for the recorder's orphan draw pass).</summary>
    public NodeHandle OrphanAt(int i, out float px, out float py)
    {
        var e = _orphans[i]; px = e.Px; py = e.Py; return e.Node;
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
    public void SetPointerWheel(NodeHandle h, Action<WheelEventArgs>? handler) => _pointerWheel[LiveIndex(h)] = handler;
    public Action<WheelEventArgs>? GetPointerWheel(NodeHandle h) => _pointerWheel[LiveIndexOrZero(h)];
    public void SetContextRequested(NodeHandle h, Action<Point2>? handler) => _contextRequested[LiveIndex(h)] = handler;
    public Action<Point2>? GetContextRequested(NodeHandle h) => _contextRequested[LiveIndexOrZero(h)];
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
    public void SetBrushAnim(NodeHandle h, in BrushAnim ba) => _brushAnims[(int)h.Raw.Index] = ba;
    public bool TryGetBrushAnim(NodeHandle h, out BrushAnim ba) => _brushAnims.TryGetValue((int)h.Raw.Index, out ba);

    /// <summary>Advance every active brush transition by <paramref name="dtMs"/>, mark the nodes PaintDirty, and drop
    /// settled rows. Called by the host at phase 7 (next to the interaction animator). 0-alloc (reused scratch).</summary>
    public void AdvanceBrushAnims(float dtMs)
    {
        if (_brushAnims.Count == 0) return;
        _brushScratch.Clear();
        foreach (var kv in _brushAnims) _brushScratch.Add(kv.Key);
        for (int i = 0; i < _brushScratch.Count; i++)
        {
            int idx = _brushScratch[i];
            if (_gen[idx] == 0) { _brushAnims.Remove(idx); continue; }
            var ba = _brushAnims[idx];
            ba.T = ba.DurationMs <= 0f ? 1f : MathF.Min(1f, ba.T + dtMs / ba.DurationMs);
            _flags[idx] |= NodeFlags.PaintDirty;
            if (ba.T >= 1f) _brushAnims.Remove(idx);
            else _brushAnims[idx] = ba;
        }
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
        => _textSelection[(int)node.Raw.Index] = (start, end);

    public bool TryGetTextSelection(NodeHandle h, out int start, out int end)
    {
        if (_textSelection.TryGetValue((int)h.Raw.Index, out var r)) { start = r.Start; end = r.End; return true; }
        start = end = 0;
        return false;
    }

    public void ClearTextSelection(NodeHandle h) => _textSelection.Remove((int)h.Raw.Index);

    /// <summary>Per-node selection-highlight override (api-04, WinUI TextBlock.SelectionHighlightColor —
    /// TextBlock.cpp:266/330). A==0 clears back to the host theme brush (TextEditStyle.SelectionFill — the system
    /// accent, TextSelectionManager.cpp:52-56).</summary>
    public void SetSelectionHighlight(NodeHandle node, ColorF color)
    {
        int idx = (int)node.Raw.Index;
        if (color.A <= 0f) _selectionHighlight.Remove(idx);
        else _selectionHighlight[idx] = color;
    }

    public bool TryGetSelectionHighlight(NodeHandle h, out ColorF color)
        => _selectionHighlight.TryGetValue((int)h.Raw.Index, out color);

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

    public void SetDynamicText(NodeHandle h, DynamicTextKind kind)
    {
        int idx = (int)h.Raw.Index;
        var old = _dynamicText[idx];
        if (old == kind) return;
        if (old == DynamicTextKind.None && kind != DynamicTextKind.None) _dynamicTextCount++;
        else if (old != DynamicTextKind.None && kind == DynamicTextKind.None) _dynamicTextCount--;
        _dynamicText[idx] = kind;
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

    public void Mark(NodeHandle h, NodeFlags flags)
    {
        int idx = (int)h.Raw.Index;
        if ((flags & NodeFlags.LayoutDirty) != 0 && (_flags[idx] & NodeFlags.LayoutDirty) == 0) _layoutDirty.Add(h);
        _flags[idx] |= flags;
    }
    public void Unmark(NodeHandle h, NodeFlags flags) => _flags[h.Raw.Index] &= ~flags;

    // ── scroll/virtual side-table (sparse; only viewport nodes have an entry) ──
    /// <summary>Get-or-create the scroll row for a viewport node; marks it <see cref="NodeFlags.Scrollable"/>.</summary>
    public ref ScrollState ScrollRef(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        ref ScrollState s = ref CollectionsMarshal.GetValueRefOrAddDefault(_scroll, idx, out bool existed);
        if (!existed) { s = ScrollState.Default; _flags[idx] |= NodeFlags.Scrollable; }
        return ref s;
    }
    public bool HasScroll(NodeHandle h) => _scroll.ContainsKey((int)h.Raw.Index);
    /// <summary>Read the scroll row by value (default if the node is not a viewport).</summary>
    public bool TryGetScroll(NodeHandle h, out ScrollState s) => _scroll.TryGetValue((int)h.Raw.Index, out s);

    // ── hit-test pass-through (WinUI FlyoutBase.OverlayInputPassThroughElement) ──────────────────
    // A light-dismiss scrim registers ONE target subtree whose rendered bounds it yields to: pointer input there
    // bypasses the scrim and reaches the content beneath (the MenuBar hover-switches titles with a menu open,
    // FlyoutBase_Partial.cpp:3922-3938). Sparse — O(open scrims), cleared on free.
    private readonly Dictionary<int, NodeHandle> _hitPassThrough = new();

    public void SetHitTestPassThrough(NodeHandle node, NodeHandle target)
    {
        if (!IsLive(node)) return;
        if (target.IsNull) _hitPassThrough.Remove((int)node.Raw.Index);
        else _hitPassThrough[(int)node.Raw.Index] = target;
    }

    public bool TryGetHitTestPassThrough(NodeHandle node, out NodeHandle target)
        => _hitPassThrough.TryGetValue((int)node.Raw.Index, out target);

    // ── sticky registry (CSS position:sticky, top edge) ─────────────────────────────────────────
    // Node index → (handle, top inset, pin-state observer). The reconciler Set/Clears it from BoxEl.StickyTop each
    // reconcile (slot reuse self-cleans like the transition side-table); the host's phase-7 sticky pass iterates it
    // and writes the pin offset as the node's LocalTransform — so HIT-TESTING follows the PINNED position
    // (AbsoluteRect sums transforms) — and fires OnPinned on engage/release transitions (the CSS :stuck observable).
    private readonly Dictionary<int, (NodeHandle Node, float Inset, Action<bool>? OnPinned)> _sticky = new();
    /// <summary>All sticky-declared nodes, keyed by slot index — consumed by the host's per-frame sticky pass.</summary>
    public Dictionary<int, (NodeHandle Node, float Inset, Action<bool>? OnPinned)> StickyNodes => _sticky;
    public void SetSticky(NodeHandle h, float inset, Action<bool>? onPinned = null) => _sticky[(int)h.Raw.Index] = (h, inset, onPinned);
    public void ClearSticky(NodeHandle h)
    {
        if (!_sticky.Remove((int)h.Raw.Index)) return;
        if (!IsLive(h)) return;
        // Un-declaring while pinned: release the pin transform and the paint-order boost.
        ref NodePaint p = ref Paint(h);
        if (p.LocalTransform.Dy != 0f || p.LocalTransform.Dx != 0f)
        {
            p.LocalTransform = Affine2D.Identity;
            Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
        Unmark(h, NodeFlags.StickyPinned);
    }

    /// <summary>Get-or-create the variable-height extent table for a viewport, (re)building it on item-count change.</summary>
    public ExtentTable ExtentTableFor(NodeHandle h, int itemCount, float estimate)
    {
        int idx = (int)h.Raw.Index;
        if (!_extents.TryGetValue(idx, out var t)) { t = new ExtentTable(itemCount, estimate); _extents[idx] = t; }
        else if (t.Count != itemCount) t.Reset(itemCount, estimate);
        return t;
    }
    public bool TryGetExtents(NodeHandle h, out ExtentTable? t) => _extents.TryGetValue((int)h.Raw.Index, out t);

    public void SetGrid(NodeHandle h, in GridSpec spec) => _grids[(int)h.Raw.Index] = spec;
    public bool HasGrid(NodeHandle h) => _grids.ContainsKey((int)h.Raw.Index);
    public bool TryGetGrid(NodeHandle h, out GridSpec spec) => _grids.TryGetValue((int)h.Raw.Index, out spec);

    // ── rich-paint side-tables ──
    /// <summary>Get-or-create the eased-interaction row for a node (hover/press progress).</summary>
    public ref InteractionAnim InteractRef(NodeHandle h)
    {
        ref InteractionAnim s = ref CollectionsMarshal.GetValueRefOrAddDefault(_interact, (int)h.Raw.Index, out bool existed);
        if (!existed) s = InteractionAnim.Default;
        return ref s;
    }
    public bool TryGetInteract(NodeHandle h, out InteractionAnim s) => _interact.TryGetValue((int)h.Raw.Index, out s);

    public void SetShadow(NodeHandle h, in ShadowSpec s) => _shadows[(int)h.Raw.Index] = s;
    public bool TryGetShadow(NodeHandle h, out ShadowSpec s) => _shadows.TryGetValue((int)h.Raw.Index, out s);
    public void ClearShadow(NodeHandle h) => _shadows.Remove((int)h.Raw.Index);

    public void SetArc(NodeHandle h, in ArcSpec a) => _arcs[(int)h.Raw.Index] = a;
    public bool TryGetArc(NodeHandle h, out ArcSpec a) => _arcs.TryGetValue((int)h.Raw.Index, out a);
    public void ClearArc(NodeHandle h) => _arcs.Remove((int)h.Raw.Index);

    public void SetPolylineStroke(NodeHandle h, in PolylineStrokeSpec p) => _polylines[(int)h.Raw.Index] = p;
    public bool TryGetPolylineStroke(NodeHandle h, out PolylineStrokeSpec p) => _polylines.TryGetValue((int)h.Raw.Index, out p);
    public void ClearPolylineStroke(NodeHandle h) => _polylines.Remove((int)h.Raw.Index);

    public void SetGradient(NodeHandle h, in GradientSpec g) => _gradients[(int)h.Raw.Index] = g;
    public bool TryGetGradient(NodeHandle h, out GradientSpec g) => _gradients.TryGetValue((int)h.Raw.Index, out g);
    public void ClearGradient(NodeHandle h) => _gradients.Remove((int)h.Raw.Index);

    public void SetBorderBrush(NodeHandle h, in GradientSpec g) => _borderBrushes[(int)h.Raw.Index] = g;
    public bool TryGetBorderBrush(NodeHandle h, out GradientSpec g) => _borderBrushes.TryGetValue((int)h.Raw.Index, out g);
    public void ClearBorderBrush(NodeHandle h) => _borderBrushes.Remove((int)h.Raw.Index);

    public void SetHoverGradient(NodeHandle h, in GradientSpec g) => _hoverGradients[(int)h.Raw.Index] = g;
    public bool TryGetHoverGradient(NodeHandle h, out GradientSpec g) => _hoverGradients.TryGetValue((int)h.Raw.Index, out g);
    public void ClearHoverGradient(NodeHandle h) => _hoverGradients.Remove((int)h.Raw.Index);

    public void SetPressedGradient(NodeHandle h, in GradientSpec g) => _pressedGradients[(int)h.Raw.Index] = g;
    public bool TryGetPressedGradient(NodeHandle h, out GradientSpec g) => _pressedGradients.TryGetValue((int)h.Raw.Index, out g);
    public void ClearPressedGradient(NodeHandle h) => _pressedGradients.Remove((int)h.Raw.Index);

    public void SetHoverBorderBrush(NodeHandle h, in GradientSpec g) => _hoverBorderBrushes[(int)h.Raw.Index] = g;
    public bool TryGetHoverBorderBrush(NodeHandle h, out GradientSpec g) => _hoverBorderBrushes.TryGetValue((int)h.Raw.Index, out g);
    public void ClearHoverBorderBrush(NodeHandle h) => _hoverBorderBrushes.Remove((int)h.Raw.Index);

    public void SetPressedBorderBrush(NodeHandle h, in GradientSpec g) => _pressedBorderBrushes[(int)h.Raw.Index] = g;
    public bool TryGetPressedBorderBrush(NodeHandle h, out GradientSpec g) => _pressedBorderBrushes.TryGetValue((int)h.Raw.Index, out g);
    public void ClearPressedBorderBrush(NodeHandle h) => _pressedBorderBrushes.Remove((int)h.Raw.Index);

    public void SetAcrylic(NodeHandle h, in AcrylicSpec a) => _acrylics[(int)h.Raw.Index] = a;
    public bool TryGetAcrylic(NodeHandle h, out AcrylicSpec a) => _acrylics.TryGetValue((int)h.Raw.Index, out a);
    public void ClearAcrylic(NodeHandle h) => _acrylics.Remove((int)h.Raw.Index);

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

    /// <summary>Get-or-create the per-node text measure cache row (layout.md §2.3).</summary>
    public ref TextMeasureCache MeasureCacheRef(NodeHandle h)
        => ref CollectionsMarshal.GetValueRefOrAddDefault(_measureCache, (int)h.Raw.Index, out _);

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
        }
        return new RectF(x, y, _bounds[h.Raw.Index].W, _bounds[h.Raw.Index].H);
    }

    private NodeHandle Wrap(int idx) => idx == 0 ? NodeHandle.Null : new NodeHandle(new Handle((uint)idx, _gen[idx]));

    private void Grow()
    {
        int n = _gen.Length * 2;
        Array.Resize(ref _gen, n); Array.Resize(ref _nextFree, n);
        Array.Resize(ref _parent, n); Array.Resize(ref _firstChild, n); Array.Resize(ref _lastChild, n);
        Array.Resize(ref _prevSib, n); Array.Resize(ref _nextSib, n); Array.Resize(ref _childCount, n);
        Array.Resize(ref _elementTypeId, n); Array.Resize(ref _layout, n); Array.Resize(ref _bounds, n);
        Array.Resize(ref _paint, n); Array.Resize(ref _dynamicText, n); Array.Resize(ref _interaction, n); Array.Resize(ref _flags, n);
        Array.Resize(ref _click, n); Array.Resize(ref _keyHandler, n); Array.Resize(ref _charHandler, n);
        Array.Resize(ref _pointerDown, n); Array.Resize(ref _drag, n); Array.Resize(ref _hoverMove, n); Array.Resize(ref _pointerExit, n);
        Array.Resize(ref _pointerPressed, n); Array.Resize(ref _pointerWheel, n); Array.Resize(ref _contextRequested, n);
        Array.Resize(ref _focusChanged, n);
        Array.Resize(ref _dragStarted, n); Array.Resize(ref _dragDelta, n);
        Array.Resize(ref _dragCompleted, n); Array.Resize(ref _dragCanceled, n);
    }

    // ISceneBackend explicit ref returns already satisfied above.
    ref LayoutInput ISceneBackend.Layout(NodeHandle node) => ref _layout[node.Raw.Index];
    ref NodePaint ISceneBackend.Paint(NodeHandle node) => ref _paint[node.Raw.Index];
    ref InteractionInfo ISceneBackend.Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
}
