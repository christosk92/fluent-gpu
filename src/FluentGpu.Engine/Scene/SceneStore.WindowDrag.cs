using FluentGpu.Foundation;

namespace FluentGpu.Scene;

// Declarative window drag regions (BoxEl.WindowDrag, docs/plans/app-authoring-ergonomics-implementation.md §1): a sparse
// side table keyed by node index, the _dropTargets pattern. The reconciler writes it from BoxEl.WindowDrag; the host's
// phase-7.8 collector (AppHost.WindowDrag.cs) reads it once per frame and turns every reachable entry into a non-client
// region. Freed with the node (FreeSubtreeCore → ReleaseWindowDrag).
public sealed partial class SceneStore
{
    private readonly Dictionary<int, WindowDragRole> _windowDrag = new();
    private int _windowDragVersion;

    /// <summary>True while any node declares a window-drag role (the host collector's one-compare gate).</summary>
    public bool HasWindowDrag => _windowDrag.Count != 0;

    /// <summary>Generation of the window-drag registry — bumped when a role is added, changed or removed. A hint only:
    /// geometry moves (scroll, resize, transforms) never bump it, so the collector re-derives rects every frame it runs.</summary>
    public int WindowDragVersion => _windowDragVersion;

    /// <summary>Set (or clear, <see cref="WindowDragRole.None"/>) the node's window-drag role — the reconciler writes it
    /// from <c>BoxEl.WindowDrag</c> on every column write. Writing the role a node already holds is free.</summary>
    public void SetWindowDrag(NodeHandle h, WindowDragRole role)
    {
        int idx = (int)h.Raw.Index;
        if (role == WindowDragRole.None)
        {
            if (_windowDrag.Count != 0 && _windowDrag.Remove(idx)) _windowDragVersion++;
            return;
        }
        if (_windowDrag.TryGetValue(idx, out var prior) && prior == role) return;
        _windowDrag[idx] = role;
        _windowDragVersion++;
    }

    /// <summary>The node's declared window-drag role (<see cref="WindowDragRole.None"/> when it declares none).</summary>
    public WindowDragRole WindowDragOf(NodeHandle h)
        => _windowDrag.Count != 0 && _windowDrag.TryGetValue((int)h.Raw.Index, out var r) ? r : WindowDragRole.None;

    // FreeSubtreeCore hook: a freed node's role must not survive into whichever node recycles the index.
    private void ReleaseWindowDrag(int idx)
    {
        if (_windowDrag.Count != 0 && _windowDrag.Remove(idx)) _windowDragVersion++;
    }

    /// <summary>Append to <paramref name="into"/> (not cleared) one entry per declared node the hit-test can reach
    /// (<see cref="IsHitReachable"/>: live, linked to <see cref="Root"/>, Visible + HitTestVisible on the whole chain —
    /// so KeepAlive-parked pages, exit orphans and collapsed subtrees drop out) whose <see cref="VisibleWindowRect"/> is
    /// non-empty. Registry order. Then, for every collected <see cref="WindowDragRole.Drag"/> entry, one
    /// <see cref="WindowDragRole.NoDrag"/> entry per interactive node PAINTED ABOVE the band that is not part of it
    /// (<see cref="CollectDragOccluders"/>: an in-window menu / flyout / dialog / light-dismiss scrim / toast in the
    /// OverlayHost layers above the app content) at its rect inside the band — the press would land on that node in the
    /// window's own hit-test, so the OS move loop must not eat it. Allocation-free once <paramref name="into"/> has grown.</summary>
    public void CollectWindowDragRects(List<WindowDragRect> into)
    {
        if (_windowDrag.Count == 0) return;
        int start = into.Count;
        foreach (var pair in _windowDrag)
        {
            int idx = pair.Key;
            var node = new NodeHandle(new Handle((uint)idx, _gen[idx]));
            if (!IsHitReachable(node)) continue;
            RectF r = VisibleWindowRect(node);
            if (r.IsEmpty || r.IsInfinite) continue;   // realized-but-not-laid-out / scrolled out / clipped away
            into.Add(new WindowDragRect(node, pair.Value, r));
        }
        int end = into.Count;
        for (int i = start; i < end; i++)
        {
            WindowDragRect e = into[i];
            if (e.Role == WindowDragRole.Drag) CollectDragOccluders(e.Node, e.RectDip, into);
        }
    }

    // The interaction bits that make a node a pointer-hit target in its own right — InputDispatcher.Hit's hitAnywhere set
    // plus the rect-gated SpanLinks/RowCells shapes (counted as their whole box here: a conservative Client).
    private const uint OccluderHitMask = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit
        | InteractionInfo.DragBit | InteractionInfo.CursorBit | InteractionInfo.SelectableTextBit | InteractionInfo.GestureBit
        | InteractionInfo.ContextBit | InteractionInfo.SpanLinksBit | InteractionInfo.RowCellsBit;

    /// <summary>Append a <see cref="WindowDragRole.NoDrag"/> entry for every interactive node painted above
    /// <paramref name="drag"/> (a LATER sibling of the drag node or of any of its ancestors — paint order, which is also the
    /// dispatcher's last-child-wins hit order) whose rect overlaps <paramref name="band"/>, clipped to the band. Mirrors
    /// the interaction-gated hit walk: Visible + HitTestVisible prune the subtree, a Disabled or self-pass-through
    /// (<c>HitTestPassThrough</c>) node yields to its children, the first interactive node of a branch is emitted whole and
    /// its subtree is not walked. A subtree with its own window-drag role is skipped (its entry already says what it is).
    /// A branch whose box misses the band is pruned — a child that overflows an UNCLIPPED parent onto the band is missed
    /// (layout keeps children inside their parent; popups translate their own wrapper). Stops once one occluder covers the
    /// whole band (the light-dismiss scrim of an open menu). Allocation-free; visits only the overlapping branches.</summary>
    private void CollectDragOccluders(NodeHandle drag, RectF band, List<WindowDragRect> into)
    {
        for (NodeHandle c = drag, p = Parent(drag); !p.IsNull; c = p, p = Parent(p))
            for (NodeHandle s = NextSibling(c); !s.IsNull; s = NextSibling(s))
                if (CollectOccludersIn(s, band, band, into)) return;
    }

    // True once an emitted occluder covers the whole band (nothing beneath can still be a caption pixel).
    private bool CollectOccludersIn(NodeHandle n, RectF band, RectF clip, List<WindowDragRect> into)
    {
        int i = (int)n.Raw.Index;
        NodeFlags f = _flags[i];
        if ((f & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible)) return false;
        if (_windowDrag.ContainsKey(i)) return false;
        ref NodePaint p = ref _paint[i];
        float w = float.IsNaN(p.PresentedW) ? _bounds[i].W : p.PresentedW;
        float h = float.IsNaN(p.PresentedH) ? _bounds[i].H : p.PresentedH;
        RectF box = AbsoluteTransformedRect(n, w, h);
        RectF hit = box.Intersect(clip);
        if (hit.IsEmpty) return false;
        if (!p.ClipRect.IsInfinite)
        {
            clip = clip.Intersect(WorldAffine(n).TransformBounds(p.ClipRect));
            hit = hit.Intersect(clip);
            if (hit.IsEmpty) return false;
        }
        bool selfYields = (f & NodeFlags.Disabled) != 0
            || (_hitPassThrough.TryGet(i, out NodeHandle pass) && pass == n);
        if (!selfYields && (_interaction[i].HandlerMask & OccluderHitMask) != 0)
        {
            into.Add(new WindowDragRect(n, WindowDragRole.NoDrag, hit));
            return hit.X <= band.X && hit.Y <= band.Y && hit.Right >= band.Right && hit.Bottom >= band.Bottom;
        }
        if ((f & NodeFlags.ClipsToBounds) != 0) clip = hit;
        for (NodeHandle c = FirstChild(n); !c.IsNull; c = NextSibling(c))
            if (CollectOccludersIn(c, band, clip, into)) return true;
        return false;
    }

    /// <summary>The window-DIP rect <paramref name="node"/> can actually show: its painted box
    /// (<see cref="AbsoluteTransformedRect(NodeHandle)"/> — translation, scroll pose and ChildShift folded in;
    /// scale/rotate collapse to the bounding box) intersected with every ancestor's <see cref="NodeFlags.ClipsToBounds"/>
    /// extent (the presented size while a reveal animates) and every authored <see cref="NodePaint.ClipRect"/> on the
    /// chain, the node's own included. Empty when nothing is visible or the node is dead. The same answer as
    /// <c>ConnectedAnimation.EffectiveVisibleRect</c>. Allocation-free; O(depth²) (one rect walk per clipping
    /// ancestor) — run for a handful of declared nodes per frame, never per scene node.</summary>
    public RectF VisibleWindowRect(NodeHandle node)
    {
        if (node.IsNull || !IsLive(node)) return default;
        RectF visible = AbsoluteTransformedRect(node);
        if (visible.IsEmpty) return default;
        for (NodeHandle n = node; !n.IsNull; n = Parent(n))
        {
            int i = (int)n.Raw.Index;
            ref NodePaint p = ref _paint[i];
            if ((_flags[i] & NodeFlags.ClipsToBounds) != 0)
            {
                float w = float.IsNaN(p.PresentedW) ? _bounds[i].W : p.PresentedW;
                float h = float.IsNaN(p.PresentedH) ? _bounds[i].H : p.PresentedH;
                visible = visible.Intersect(AbsoluteTransformedRect(n, w, h));
            }
            if (!p.ClipRect.IsInfinite)
                visible = visible.Intersect(WorldAffine(n).TransformBounds(p.ClipRect));
            if (visible.IsEmpty) return default;
        }
        return visible;
    }
}
