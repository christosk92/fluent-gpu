using FluentGpu.Foundation;

namespace FluentGpu.Animation;

/// <summary>Which scalar of a scroller's <see cref="FluentGpu.Scene.ScrollState"/> feeds a <see cref="ScrollBind"/>.
/// The generic, hookable scroll model: scroll never binds to raw px — it binds to a normalized progress derived from
/// one of these post-physics integrator outputs (design/plans/generic-hookable-scroll-engine-design.md §3).</summary>
public enum ScrollChannel : byte
{
    Offset = 0,          // the settled/animating scroll offset on the scroll axis (Offset along Orientation)
    OverscrollBand = 1,  // signed rubber-band displacement past the clamp (OverscrollPx; <0 = pulled past top/left)
    Velocity = 2,        // signed fling velocity px/s
    SignedPhase = 3,     // per-item viewport-position phase in [-1,+1], identity = 0 (item-center vs viewport-center)
}

/// <summary>Which <see cref="FluentGpu.Scene.NodePaint"/> compositor field a <see cref="ScrollBind"/> writes. Every
/// sink is transform/paint-only (no relayout), composed exactly like the recorder already reads it.</summary>
public enum BindSink : byte
{
    TransY = 0,        // LocalTransform translate Y
    TransX = 1,        // LocalTransform translate X
    ScaleUniform = 2,  // LocalTransform uniform scale about OriginX/Y
    ScaleY = 3,        // LocalTransform scale Y about OriginX/Y
    Opacity = 4,       // NodePaint.Opacity
    Blur = 5,          // NodePaint.BlurSigma
    ClipBottom = 6,    // NodePaint.ClipRect bottom edge
    ClipTop = 7,       // NodePaint.ClipRect top edge
    PresentedH = 8,    // NodePaint.PresentedH (compositor reveal; clips children, no relayout)
    PresentedHTrailing = 9, // PresentedH + ChildShiftY: child content's trailing edge rides the reveal edge
}

/// <summary>How a <see cref="ScrollBind"/> range anchor is authored before it is baked to a scroll-px bound. Literal-px
/// anchors bake at reconcile; geometry anchors (<see cref="OffsetFrac"/>/<see cref="NodeEnterViewport"/>/
/// <see cref="NodeExitViewport"/>) bake at <c>ArrangeViewport</c>, where Content*/Bounds become known.</summary>
public enum ScrollBindAnchor : byte
{
    OffsetPx = 0,            // literal scroll-px
    OffsetFrac = 1,          // fraction of the scroller's max offset (0..1 ⇒ 0..maxOffset)
    OverscrollBand = 2,      // the overscroll band itself is the sample; range bakes to [0, BandLimit]
    NodeEnterViewport = 3,   // the offset at which the target node's top reaches the viewport bottom (enters)
    NodeExitViewport = 4,    // the offset at which the target node's bottom reaches the viewport top (exits)
}

/// <summary>One compiled <c>(scroll-source → target-property)</c> binding edge — the engine-side row of the generic
/// scroll-binding model. Evaluated allocation-free at the offset-write chokepoint (offset/band/velocity/phase ops) or
/// the phase-7 pin pass (<see cref="PinKind"/> ≠ 0). The result is DATA describing a transform written into the target's
/// <see cref="FluentGpu.Scene.NodePaint"/> — never an imperative mutation.
/// <para>This is a managed row (it carries the optional <see cref="OnFlag"/> delegate); the slab grows only at reconcile,
/// never in frame phases 6–13, so the zero-alloc hot-path contract holds (the per-frame eval reads fields by index).</para>
/// </summary>
public struct ScrollBind
{
    public NodeHandle Target;     // the node whose paint field this edge writes (resolved at reconcile)
    public int Scroller;          // node index of the enclosing scroll viewport (-1 = unresolved / no scroller)
    public NodeHandle ScrollerHandle; // the enclosing scroller's handle (gen-checked; the pin pass reads ScrollRef from it)
    public ScrollChannel Source;
    public BindSink Sink;
    public byte PinKind;          // 0 none | 1 top-pin (containing-block clamp) | 2 bottom-pin | 3 sticky clip-top (ClipRect.top rides the viewport line)
    public byte Flags;            // bit field — see the Flag* constants below

    // RANGE — authored anchors (left) baked to scroll-px bounds (right). a==b ⇒ inactive (writes identity / OutLo).
    public ScrollBindAnchor AnchorA, AnchorB;
    public float AnchorAv, AnchorBv;
    public float RangeA, RangeB;

    // EXPRESSION — input-range → output-range affine lerp (OutLo at t=0, OutHi at t=1), optional shaping ease.
    public float OutLo, OutHi;
    public Easing Ease;

    public float Inset;           // sticky inset / collapse pivot (px)
    public float LastWritten;     // change-gate: skip the NodePaint write + Mark when the output is unchanged
    public int Next;              // intrusive list: next bind on the SAME scroller (-1 = tail) — the eval chain
    public int NodeNext;          // intrusive list: next bind on the SAME node (-1 = tail) — the teardown chain

    // PREDICATE channel hook — fires a managed callback only when the watched boolean flips (edge-only, UI-thread).
    public byte FlagBit;          // which ScrollState.ScrollFlags bit OnFlag observes (non-pin binds)
    public Action<bool>? OnFlag;  // the CSS :stuck-style observable; for a pin bind it observes THIS node's pinned state
    public bool OnFlagLast;
    public bool OnFlagHasLast;

    public const byte FlagClampOut = 1;          // clamp t to [0,1] (default authoring; cleared for extrapolating parallax)
    public const byte FlagStretchClosedForm = 2; // the iOS/Spotify (h+pull)/h band-cancel matrix (overscroll hero)
    public const byte FlagPaintAbove = 4;        // mirror NodeFlags.StickyPinned at apply so the recorder paints it above siblings
    public const byte FlagEaseInOut = 8;         // SampleMode.EaseInOut shaping of t before the lerp
    public const byte FlagGeometryAnchor = 16;   // RangeA/B depend on layout geometry → (re)bake at ArrangeViewport

    public readonly bool Has(byte bit) => (Flags & bit) != 0;
}

/// <summary>The reconciler-owned dense slab of <see cref="ScrollBind"/> rows + the two index maps that thread the
/// intrusive chains: <c>_headByVp</c> (scroller node-index → head of its eval chain via <see cref="ScrollBind.Next"/>)
/// and <c>_headByNode</c> (target node-index → head of its teardown chain via <see cref="ScrollBind.NodeNext"/>).
/// Slots are recycled through a free-list — lifetimes owned by the reconciler, not GC. Growth (the only allocation)
/// happens at reconcile, never in the frame hot path.</summary>
public sealed class ScrollBindTable
{
    private ScrollBind[] _binds = System.Array.Empty<ScrollBind>();
    private int _count;
    private readonly System.Collections.Generic.Stack<int> _free = new();
    private readonly System.Collections.Generic.Dictionary<int, int> _headByVp = new();   // scroller idx → head slot
    private readonly System.Collections.Generic.Dictionary<int, int> _headByNode = new();  // node idx → head slot

    /// <summary>Live bind rows (census; excludes recycled free-list slots).</summary>
    public int Count => _count - _free.Count;

    /// <summary>Head of a scroller's eval chain (walk <see cref="ScrollBind.Next"/>); -1 = none.</summary>
    public int Head(int scrollerIndex) => _headByVp.TryGetValue(scrollerIndex, out int h) ? h : -1;

    /// <summary>True when any scroller has at least one bind (skip the whole pass otherwise).</summary>
    public bool HasAny => _headByVp.Count != 0;

    /// <summary>True when this node currently owns a row writing <paramref name="sink"/>.</summary>
    public bool NodeOwnsSink(int nodeIndex, BindSink sink)
    {
        if (!_headByNode.TryGetValue(nodeIndex, out int s)) return false;
        for (; s >= 0; s = _binds[s].NodeNext)
            if (_binds[s].Sink == sink) return true;
        return false;
    }

    /// <summary>The scroller node-indices that currently own binds — the phase-7 pin/flag pass iterates these
    /// (struct enumerator, allocation-free).</summary>
    public System.Collections.Generic.Dictionary<int, int>.KeyCollection ScrollerIndices => _headByVp.Keys;

    public ref ScrollBind At(int slot) => ref _binds[slot];

    private int Alloc()
    {
        int s;
        if (_free.Count > 0) s = _free.Pop();
        else
        {
            if (_count == _binds.Length)
                System.Array.Resize(ref _binds, _binds.Length == 0 ? 8 : _binds.Length * 2);
            s = _count++;
        }
        _binds[s] = default;
        _binds[s].Next = -1;
        _binds[s].NodeNext = -1;
        _binds[s].Scroller = -1;
        return s;
    }

    /// <summary>Append one bind owned by <paramref name="nodeIndex"/>, linked into <paramref name="scroller"/>'s
    /// eval chain. Returns the slot so the caller can finish filling fields via <see cref="At"/>.</summary>
    public int Add(int nodeIndex, NodeHandle scroller, in ScrollBind seed)
    {
        int s = Alloc();
        _binds[s] = seed;
        // A fresh row must always take its FIRST write: a re-bake (re-theme / re-render) may just have re-applied the
        // element's literal paint (Opacity=1 etc.) over a bind-written value, and a default LastWritten of 0 would
        // change-gate the corrective write away whenever the bound value IS 0 — the collapsed-hero photo popping back
        // at full opacity on a focus-regain re-theme. NaN compares unequal to every v, so the first eval writes.
        _binds[s].LastWritten = float.NaN;
        int scrollerIndex = scroller.IsNull ? -1 : (int)scroller.Raw.Index;
        _binds[s].Scroller = scrollerIndex;
        _binds[s].ScrollerHandle = scroller;
        // prepend to the node's teardown chain
        _binds[s].NodeNext = _headByNode.TryGetValue(nodeIndex, out int nh) ? nh : -1;
        _headByNode[nodeIndex] = s;
        // prepend to the scroller's eval chain (only when a scroller is known)
        if (scrollerIndex >= 0)
        {
            _binds[s].Next = _headByVp.TryGetValue(scrollerIndex, out int vh) ? vh : -1;
            _headByVp[scrollerIndex] = s;
        }
        else _binds[s].Next = -1;
        return s;
    }

    /// <summary>Free every bind owned by <paramref name="nodeIndex"/> (re-reconcile / unmount). Unlinks each from both
    /// its scroller eval chain and the node teardown chain, then recycles the slot. No-op if the node has no binds.</summary>
    public void ClearNode(int nodeIndex)
    {
        if (!_headByNode.TryGetValue(nodeIndex, out int s)) return;
        _headByNode.Remove(nodeIndex);
        while (s >= 0)
        {
            int nextNode = _binds[s].NodeNext;
            UnlinkFromScroller(s, _binds[s].Scroller);
            _binds[s] = default;
            _free.Push(s);
            s = nextNode;
        }
    }

    private void UnlinkFromScroller(int slot, int scrollerIndex)
    {
        if (scrollerIndex < 0) return;
        if (!_headByVp.TryGetValue(scrollerIndex, out int head)) return;
        if (head == slot)
        {
            int next = _binds[slot].Next;
            if (next < 0) _headByVp.Remove(scrollerIndex);
            else _headByVp[scrollerIndex] = next;
            return;
        }
        for (int p = head; p >= 0; p = _binds[p].Next)
        {
            if (_binds[p].Next == slot) { _binds[p].Next = _binds[slot].Next; return; }
        }
    }

    /// <summary>True when <paramref name="nodeIndex"/> owns at least one bind (used by the reconciler to skip a re-bake
    /// of an empty `ScrollBind[]` on a node that never had any).</summary>
    public bool NodeHasBinds(int nodeIndex) => _headByNode.ContainsKey(nodeIndex);
}

/// <summary>SwiftUI <c>ScrollGeometry</c>-style POD passed to the change-only observer escape hatch
/// (<c>ScrollEl.OnScrollGeometryChanged</c>). Struct-compared via the app's <c>long</c> projection so the action fires
/// only when a coarse projected key changes — never per-px, never per-frame.</summary>
public readonly struct ScrollGeometry
{
    public readonly float OffsetX, OffsetY, ViewportW, ViewportH, ContentW, ContentH, Band, Velocity;
    public readonly byte Flags;

    public ScrollGeometry(float ox, float oy, float vw, float vh, float cw, float ch, float band, float vel, byte flags)
    {
        OffsetX = ox; OffsetY = oy; ViewportW = vw; ViewportH = vh; ContentW = cw; ContentH = ch;
        Band = band; Velocity = vel; Flags = flags;
    }
}

/// <summary>The escape-hatch observer row (one per scroller that opted in). The reconciler Set/Clears it from
/// <c>ScrollEl.OnScrollGeometryChanged</c>, exactly like the old sticky registry. The host evaluates the projection
/// after the integrator settles and fires <see cref="Action"/> only when the <c>long</c> key changes.</summary>
public struct ScrollObserverRow
{
    public long LastKey;
    public bool HasLast;
    public NodeHandle Node;      // the scroller this observer watches (set at reconcile; gen-checked at eval)
    public Func<ScrollGeometry, long>? Project;
    public Action<ScrollGeometry>? Action;
}
