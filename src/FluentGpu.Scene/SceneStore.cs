using System.Diagnostics;
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

    public void Mark(NodeHandle h, NodeFlags flags) => _flags[h.Raw.Index] |= flags;

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
            x += _bounds[n.Raw.Index].X;
            y += _bounds[n.Raw.Index].Y;
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
        Array.Resize(ref _click, n);
    }

    // ISceneBackend explicit ref returns already satisfied above.
    ref LayoutInput ISceneBackend.Layout(NodeHandle node) => ref _layout[node.Raw.Index];
    ref NodePaint ISceneBackend.Paint(NodeHandle node) => ref _paint[node.Raw.Index];
    ref InteractionInfo ISceneBackend.Interaction(NodeHandle node) => ref _interaction[node.Raw.Index];
}
