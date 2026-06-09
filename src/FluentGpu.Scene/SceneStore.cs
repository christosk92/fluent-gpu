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
    private Action<Point2>?[] _contextRequested;           // right-click / Menu-key context request (local coords)

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

    public NodeHandle Root { get; set; }

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
        _contextRequested = new Action<Point2>?[capacity];
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
        _contextRequested[idx] = null;
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
        _click[idx] = null;
        _keyHandler[idx] = null;
        _charHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _hoverMove[idx] = null;
        _pointerExit[idx] = null;
        _pointerPressed[idx] = null;
        _contextRequested[idx] = null;
        ClearDynamicText(idx);
        _scroll.Remove(idx);
        _extents.Remove(idx);
        _grids.Remove(idx);
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

    public void SetClickHandler(NodeHandle h, Action? handler) => _click[h.Raw.Index] = handler;
    public Action? GetClickHandler(NodeHandle h) => _click[h.Raw.Index];
    public void SetKeyHandler(NodeHandle h, Action<KeyEventArgs>? handler) => _keyHandler[h.Raw.Index] = handler;
    public Action<KeyEventArgs>? GetKeyHandler(NodeHandle h) => _keyHandler[h.Raw.Index];
    public void SetCharHandler(NodeHandle h, Action<CharEventArgs>? handler) => _charHandler[h.Raw.Index] = handler;
    public Action<CharEventArgs>? GetCharHandler(NodeHandle h) => _charHandler[h.Raw.Index];
    public void SetPointerDown(NodeHandle h, Action<Point2>? handler) => _pointerDown[h.Raw.Index] = handler;
    public Action<Point2>? GetPointerDown(NodeHandle h) => _pointerDown[h.Raw.Index];
    public void SetDrag(NodeHandle h, Action<Point2>? handler) => _drag[h.Raw.Index] = handler;
    public Action<Point2>? GetDrag(NodeHandle h) => _drag[h.Raw.Index];
    public void SetHoverMove(NodeHandle h, Action<Point2>? handler) => _hoverMove[h.Raw.Index] = handler;
    public Action<Point2>? GetHoverMove(NodeHandle h) => _hoverMove[h.Raw.Index];
    public void SetPointerExit(NodeHandle h, Action? handler) => _pointerExit[h.Raw.Index] = handler;
    public Action? GetPointerExit(NodeHandle h) => _pointerExit[h.Raw.Index];
    public void SetPointerPressed(NodeHandle h, Action<PointerEventArgs>? handler) => _pointerPressed[h.Raw.Index] = handler;
    public Action<PointerEventArgs>? GetPointerPressed(NodeHandle h) => _pointerPressed[h.Raw.Index];
    public void SetContextRequested(NodeHandle h, Action<Point2>? handler) => _contextRequested[h.Raw.Index] = handler;
    public Action<Point2>? GetContextRequested(NodeHandle h) => _contextRequested[h.Raw.Index];

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
            _paint[i].Text = resolve(kind);
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
        Array.Resize(ref _pointerPressed, n); Array.Resize(ref _contextRequested, n);
    }

    // ISceneBackend explicit ref returns already satisfied above.
    ref LayoutInput ISceneBackend.Layout(NodeHandle node) => ref _layout[node.Raw.Index];
    ref NodePaint ISceneBackend.Paint(NodeHandle node) => ref _paint[node.Raw.Index];
    ref InteractionInfo ISceneBackend.Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
}
