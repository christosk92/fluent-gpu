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

    // topology (int indices; 0 = none)
    private int[] _parent, _firstChild, _lastChild, _prevSib, _nextSib, _childCount;

    // identity + columns
    private ushort[] _elementTypeId;
    private LayoutInput[] _layout;
    private RectF[] _bounds;          // LOCAL
    private NodePaint[] _paint;
    private InteractionInfo[] _interaction;
    private NodeFlags[] _flags;
    private Action?[] _click;         // managed edge payload (GC ref at the edge only)
    private Action<KeyEventArgs>?[] _keyHandler;
    private Action<Point2>?[] _pointerDown;   // position-aware (local coords) press / drag handlers
    private Action<Point2>?[] _drag;

    // Sparse side-table for scroll/virtual viewports (O(viewports), not one-per-node). Keyed by node index.
    private readonly Dictionary<int, ScrollState> _scroll = new();
    // Per-variable-list extent tables (Fenwick); persist across frames. Keyed by viewport node index.
    private readonly Dictionary<int, ExtentTable> _extents = new();
    // Grid specs for grid-container nodes (O(grids)). Keyed by node index.
    private readonly Dictionary<int, GridSpec> _grids = new();
    // Optional rich-paint side-tables (O(decorated nodes), keyed by node index): eased interaction, shadow, gradient, acrylic.
    private readonly Dictionary<int, InteractionAnim> _interact = new();
    private readonly Dictionary<int, ShadowSpec> _shadows = new();
    private readonly Dictionary<int, GradientSpec> _gradients = new();
    private readonly Dictionary<int, GradientSpec> _borderBrushes = new();   // gradient border stroke (elevation edge)
    private readonly Dictionary<int, AcrylicSpec> _acrylics = new();

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
        _interaction = new InteractionInfo[capacity];
        _flags = new NodeFlags[capacity];
        _click = new Action?[capacity];
        _keyHandler = new Action<KeyEventArgs>?[capacity];
        _pointerDown = new Action<Point2>?[capacity];
        _drag = new Action<Point2>?[capacity];
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
        _interaction[idx] = default;
        _flags[idx] = NodeFlags.Visible | NodeFlags.HitTestVisible | NodeFlags.NewThisFrame;
        _click[idx] = null;
        _keyHandler[idx] = null;
        _pointerDown[idx] = null;
        _drag[idx] = null;
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
        _pointerDown[idx] = null;
        _drag[idx] = null;
        _scroll.Remove(idx);
        _extents.Remove(idx);
        _grids.Remove(idx);
        _interact.Remove(idx);
        _shadows.Remove(idx);
        _gradients.Remove(idx);
        _borderBrushes.Remove(idx);
        _acrylics.Remove(idx);
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
    public void SetPointerDown(NodeHandle h, Action<Point2>? handler) => _pointerDown[h.Raw.Index] = handler;
    public Action<Point2>? GetPointerDown(NodeHandle h) => _pointerDown[h.Raw.Index];
    public void SetDrag(NodeHandle h, Action<Point2>? handler) => _drag[h.Raw.Index] = handler;
    public Action<Point2>? GetDrag(NodeHandle h) => _drag[h.Raw.Index];

    public void Mark(NodeHandle h, NodeFlags flags) => _flags[h.Raw.Index] |= flags;
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

    public void SetGradient(NodeHandle h, in GradientSpec g) => _gradients[(int)h.Raw.Index] = g;
    public bool TryGetGradient(NodeHandle h, out GradientSpec g) => _gradients.TryGetValue((int)h.Raw.Index, out g);
    public void ClearGradient(NodeHandle h) => _gradients.Remove((int)h.Raw.Index);

    public void SetBorderBrush(NodeHandle h, in GradientSpec g) => _borderBrushes[(int)h.Raw.Index] = g;
    public bool TryGetBorderBrush(NodeHandle h, out GradientSpec g) => _borderBrushes.TryGetValue((int)h.Raw.Index, out g);
    public void ClearBorderBrush(NodeHandle h) => _borderBrushes.Remove((int)h.Raw.Index);

    public void SetAcrylic(NodeHandle h, in AcrylicSpec a) => _acrylics[(int)h.Raw.Index] = a;
    public bool TryGetAcrylic(NodeHandle h, out AcrylicSpec a) => _acrylics.TryGetValue((int)h.Raw.Index, out a);
    public void ClearAcrylic(NodeHandle h) => _acrylics.Remove((int)h.Raw.Index);

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
        Array.Resize(ref _paint, n); Array.Resize(ref _interaction, n); Array.Resize(ref _flags, n);
        Array.Resize(ref _click, n); Array.Resize(ref _keyHandler, n);
        Array.Resize(ref _pointerDown, n); Array.Resize(ref _drag, n);
    }

    // ISceneBackend explicit ref returns already satisfied above.
    ref LayoutInput ISceneBackend.Layout(NodeHandle node) => ref _layout[node.Raw.Index];
    ref NodePaint ISceneBackend.Paint(NodeHandle node) => ref _paint[node.Raw.Index];
    ref InteractionInfo ISceneBackend.Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
}
