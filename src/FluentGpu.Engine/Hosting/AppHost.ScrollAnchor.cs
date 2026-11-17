using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Hosting;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Scroll anchoring (ScrollEl.Anchoring, in the spirit of CSS overflow-anchor): what the user is looking at stays where it is
//  when layout above it changes size. Per opted-in vertical scroller the host keeps one ANCHOR: the first node in reading order
//  that starts inside the viewport (descending through the first node that straddles its top, never into a nested scroller),
//  and that node's drawn top in content space, with up to three of its ancestors as fallbacks (a tier crossing that re-forms the
//  anchor's subtree keeps the box around it). Once per frame after the frame's layout, the anchor's new top is compared with the
//  remembered one and the scroll's frame is shifted by the difference (ScrollHandle.ShiftFrame: a structural rebase that moves
//  with any plan, never a motion), then a fresh anchor is chosen.
//
//  Runs once a frame, after every layout pass of the frame (the commit's, the reflow re-solve and ticks, the scoped relayouts, the
//  flow pass) and before the UI pose: on the layout the frame presents. Not earlier: the commit's own layout has width reflows
//  at their TARGETS until the seed pass re-solves them at their start, so an anchor read there and again here alternates. The
//  shift cannot disturb a FLIP: projections compare parent-relative rects. The anchor's top is where it is DRAWN in the content:
//  its layout place plus the live translate rows on its chain and the flow shifts (a tile a re-flow FLIP holds at its old place
//  has not moved for the user, so it is not compensated; a glide that carries it is followed). The destination probe's target
//  solve never feeds it.
//
//  No adjustment while the scroll's plan is moving (a wheel, a drag, a fling, a programmatic ScrollTo / BringIntoView), when
//  the offset changed since the last pass for any other reason (the anchor is chosen again), or at offset 0. Virtual lists
//  (ItemCount > 0) anchor their own measured rows (Virtualizer.ApplyMeasured) and are left alone. Zero allocation per frame:
//  one struct row per opted-in scroller.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AppHost
{
    /// <summary>One scroller's anchor: the anchor and up to three of its ancestors below the content (a fallback chain: a tier crossing
    /// that re-forms the subtree the anchor lives in keeps the box around it), each with its drawn top in content space when chosen.</summary>
    private struct ScrollAnchorRow
    {
        public NodeHandle N0, N1, N2, N3;   // the anchor, then its ancestors (Null past the content)
        public float T0, T1, T2, T3;
        public double Offset;               // the scroll offset when chosen: a different one next pass means somebody else scrolled

        public readonly NodeHandle Node(int i) => i switch { 0 => N0, 1 => N1, 2 => N2, _ => N3 };
        public readonly float Top(int i) => i switch { 0 => T0, 1 => T1, 2 => T2, _ => T3 };
        public void Set(int i, NodeHandle n, float t)
        {
            switch (i) { case 0: N0 = n; T0 = t; break; case 1: N1 = n; T1 = t; break; case 2: N2 = n; T2 = t; break; default: N3 = n; T3 = t; break; }
        }
    }

    private readonly Dictionary<int, ScrollAnchorRow> _scrollAnchors = new();

    /// <summary>Hold every opted-in scroller's anchor in place over this frame's layout, then choose the anchors afresh. True when
    /// an offset moved.</summary>
    private bool AnchorScrolls()
    {
        if (_scrollAnchors.Count == 0 && !AnyAnchoringScroller()) return false;
        bool shifted = false;
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            if (!AnchoringScroller(idx, out NodeHandle node, out ScrollState sc)) { _scrollAnchors.Remove(idx); continue; }
            if (!_scrollAnchors.TryGetValue(idx, out var a) || a.N0.IsNull) continue;
            if (sc.Offset <= 0.0 || sc.Offset != a.Offset) continue;                               // at the top, or scrolled by someone
            var handle = ResolveScrollHandle(idx);
            handle.EvalAt(_lastScrollPresentSec, out _, out bool settled);
            if (!settled || sc.Motion.UserDriven) continue;                                      // a scroll in motion owns the offset
            // The deepest member of the chain still in the content (a re-formed subtree keeps the box around it).
            float delta = float.NaN;
            for (int k = 0; k < 4; k++)
            {
                NodeHandle m = a.Node(k);
                if (m.IsNull) break;
                if (_scene.IsLive(m) && TopInContent(m, sc.ContentNode, out float top)) { delta = top - a.Top(k); break; }
            }
            if (float.IsNaN(delta)) continue;                                                    // the whole chain left the content
            if (MathF.Abs(delta) < 0.5f) continue;
            handle.ShiftFrame(delta);
            shifted = true;
        }
        if (shifted) SyncScrollPlansMidFrame();   // the shifted offsets show (and pose the content) on this frame
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            if (!AnchoringScroller(idx, out NodeHandle node, out ScrollState sc)) continue;
            var row = new ScrollAnchorRow { Offset = sc.Offset };
            float viewTop = (float)sc.Offset, viewBottom = viewTop + sc.ViewportH;
            NodeHandle pick = PickAnchor(sc.ContentNode, viewTop, viewBottom, out _);
            int k = 0;
            for (var m = pick; !m.IsNull && m != sc.ContentNode && k < 4; m = _scene.Parent(m))
                if (TopInContent(m, sc.ContentNode, out float t)) row.Set(k++, m, t);
            _scrollAnchors[idx] = row;
        }
        return shifted;
    }

    private bool AnyAnchoringScroller()
    {
        for (int i = 0; i < _scrollNodes.Count; i++)
            if (AnchoringScroller(_scrollNodes[i], out _, out _)) return true;
        return false;
    }

    /// <summary>A live, unparked, plain (not virtual) vertical scroller that opted in, with its content laid out.</summary>
    private bool AnchoringScroller(int idx, out NodeHandle node, out ScrollState sc)
    {
        sc = default;
        node = _scene.HandleAt(idx);
        if (node.IsNull || !_scene.IsLive(node) || (_scene.Flags(node) & NodeFlags.Parked) != 0) return false;
        if (!_scene.TryGetScroll(node, out sc)) return false;
        return sc.Anchoring && sc.Orientation == 0 && sc.ItemCount == 0 && sc.ViewportH > 0f
               && !sc.ContentNode.IsNull && _scene.IsLive(sc.ContentNode);
    }

    /// <summary>The layout top of <paramref name="n"/> in <paramref name="content"/>'s space (the scroll is a paint shift, never in
    /// these). False when <paramref name="n"/> is no longer under it.</summary>
    private bool TopInContent(NodeHandle n, NodeHandle content, out float top)
    {
        top = 0f;
        for (var c = n; !c.IsNull; )
        {
            if (c == content) return true;
            top += _scene.Bounds(c).Y + PresentedShiftY(c);
            c = _scene.Parent(c);
            if (!c.IsNull) top += _scene.Paint(c).ChildShiftY;
        }
        return false;
    }

    /// <summary>How far <paramref name="n"/>'s live translate row holds it from its layout place: a FLIP seeded this frame holds a moved node
    /// where it was drawn (and that is where the user sees it), a glide carries it from there. The row's UI value: exact on its seed frame,
    /// the last pose fed back while the render thread owns it.</summary>
    private float PresentedShiftY(NodeHandle n) => _anim.TryGetTrackValue(n, FluentGpu.Animation.AnimChannel.TranslateY, out float v) ? v : 0f;

    /// <summary>The anchor for the window [<paramref name="viewTop"/>, <paramref name="viewBottom"/>) of the content: the first child in
    /// reading order that reaches into the window and has content of its own (a childless box only when no other does); while that child straddles the window's top, the first of ITS children that does,
    /// and so on, stopping at a node that starts inside the window, a leaf, or a nested scroller. Null when nothing is in view.</summary>
    private NodeHandle PickAnchor(NodeHandle content, float viewTop, float viewBottom, out float pickTop)
    {
        pickTop = 0f;
        NodeHandle pick = NodeHandle.Null, cur = content;
        float curTop = 0f;
        for (int depth = 0; depth < 32; depth++)
        {
            NodeHandle next = NodeHandle.Null;
            float nextTop = 0f;
            bool leafPick = false;
            for (var c = _scene.FirstChild(cur); !c.IsNull; c = _scene.NextSibling(c))
            {
                if ((_scene.Flags(c) & NodeFlags.Visible) == 0) continue;
                ref RectF b = ref _scene.Bounds(c);
                if (b.H <= 0f) continue;
                float t = curTop + b.Y + PresentedShiftY(c) + _scene.Paint(cur).ChildShiftY;
                if (t + b.H <= viewTop || t >= viewBottom) continue;
                // A childless box is air or a plate (a page gutter spans the whole content and never moves with it): a box with content
                // of its own is the anchor; a leaf only when nothing else in this row reaches the window.
                bool container = !_scene.FirstChild(c).IsNull;
                if (next.IsNull || (container && leafPick)) { next = c; nextTop = t; leafPick = !container; }
                if (container) break;
            }
            if (next.IsNull) break;
            pick = next; pickTop = nextTop;
            if (nextTop >= viewTop || (_scene.Flags(next) & NodeFlags.Scrollable) != 0 || _scene.FirstChild(next).IsNull) break;
            cur = next; curTop = nextTop;
        }
        return pick;
    }
}
