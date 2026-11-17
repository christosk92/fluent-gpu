using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Hosting;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Scroll anchoring (ScrollEl.Anchoring, in the spirit of CSS overflow-anchor): what the user is looking at stays where it is
//  in the window when layout above it changes size. Per opted-in vertical scroller the host keeps one ANCHOR: the first node in
//  reading order that starts inside the viewport (descending through the node that straddles its top, never into a nested
//  scroller), with the chain of boxes from the content down to it and each one's drawn top in content space. Once per frame
//  after the frame's layout, the anchor's new place is compared with the remembered one and the scroll's frame is shifted by the
//  difference (ScrollHandle.ShiftFrame: a structural rebase that moves with any plan, never a motion), then a fresh anchor is chosen.
//
//  What "the anchor's place" is:
//   • DRAWN, not laid out: its layout place plus the live translate rows on its chain and the flow shifts. A tile a FLIP holds
//     at its old place has not moved for the user, so it is not compensated; a glide that carries it is followed.
//   • In the WINDOW: a viewport whose top moved (a header above it changing height at the same tier edge) carries the content
//     with it, so that move is taken back too.
//   • By its bottom edge when it starts above the window (only its lower part shows; a list that adds a row above the one in
//     view grows at its top), else by its top.
//   • When the anchor is gone (a tier crossing re-forms its subtree), by the deepest box of its chain still in place.
//
//  A shift moves every node by the same amount, and a FLIP seeded this frame holds its node where it was drawn in CONTENT space,
//  which the shift would then carry away by the part of the anchor's move that is layout no hold took up (rows above the anchor
//  that grew): each fresh hold beside the anchor's chain takes that part back (AnimEngine.ShiftHold), so it starts where the user
//  saw it. A glide's own step is no such part: a hold easing home beside the anchor glides alike.
//
//  Runs once a frame, after every layout pass of the frame (the commit's, the reflow re-solve and ticks, the scoped relayouts, the
//  flow pass) and before the UI pose: on the layout the frame presents. Not earlier: the commit's own layout has width reflows
//  at their TARGETS until the seed pass re-solves them at their start, so an anchor read there and again here alternates. The
//  destination probe's target solve never feeds it.
//
//  No adjustment while the scroll's plan is moving (a wheel, a drag, a fling, a programmatic ScrollTo / BringIntoView), when
//  the offset changed since the last pass for any other reason (the anchor is chosen again), or at offset 0. Virtual lists
//  (ItemCount > 0) anchor their own measured rows (Virtualizer.ApplyMeasured) and are left alone. Zero allocation per frame:
//  one anchor record per opted-in scroller, made once.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AppHost
{
    /// <summary>How deep the anchor search descends. A Home tile is 30-odd wrappers deep before its rows (Zune's releases list: 32), so the
    /// bound is a guard against a cycle, not a budget: the walk stops at the first node that starts in the window.</summary>
    private const int MaxAnchorDepth = 256;

    /// <summary>One scroller's anchor: the chain from the content's child down to the anchor, each box's drawn top in content space when
    /// chosen, and the edge the anchor is held by.</summary>
    private sealed class ScrollAnchor
    {
        public readonly NodeHandle[] Chain = new NodeHandle[MaxAnchorDepth];
        public readonly float[] Tops = new float[MaxAnchorDepth];
        public readonly float[] RelY = new float[MaxAnchorDepth];     // each box's layout top in its parent (with the parent's flow shift)
        public readonly float[] Unheld = new float[MaxAnchorDepth];   // scratch: the layout moves below each level no FLIP hold took up
        public float AnchorH;      // the anchor's drawn height when chosen
        public int Count;          // 0: no anchor
        public bool ByBottom;      // the anchor (Chain[Count - 1]) is held by its bottom edge
        public float Edge;         // the anchor's held edge in content space
        public double Offset;      // the scroll offset when chosen: a different one next pass means somebody else scrolled
        public float ViewportY;    // the scroller's window-space top when chosen
    }

    private readonly Dictionary<int, ScrollAnchor> _scrollAnchors = new();

    /// <summary>The nodes this frame's projection pass gave a vertical FLIP hold (ApplyProjections): the holds a shift must take back.
    /// Emptied by <see cref="AnchorScrolls"/> every frame.</summary>
    private readonly HashSet<NodeHandle> _freshHolds = new();

    /// <summary>Hold every opted-in scroller's anchor in place over this frame's layout, then choose the anchors afresh. True when
    /// an offset moved.</summary>
    private bool AnchorScrolls()
    {
        if (_scrollAnchors.Count == 0 && !AnyAnchoringScroller()) { _freshHolds.Clear(); return false; }
        bool shifted = false;
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            if (!AnchoringScroller(idx, out NodeHandle node, out ScrollState sc)) { _scrollAnchors.Remove(idx); continue; }
            if (!_scrollAnchors.TryGetValue(idx, out var a) || a.Count == 0) continue;
            if (sc.Offset <= 0.0 || sc.Offset != a.Offset) continue;                               // at the top, or scrolled by someone
            var handle = ResolveScrollHandle(idx);
            handle.EvalAt(_lastScrollPresentSec, out _, out bool settled);
            if (!settled || sc.Motion.UserDriven) continue;                                      // a scroll in motion owns the offset
            NodeHandle content = sc.ContentNode;
            // The deepest box of the chain still where it was in the tree (a re-formed subtree keeps the boxes around it).
            int k = 0;
            while (k < a.Count && _scene.IsLive(a.Chain[k]) && _scene.Parent(a.Chain[k]) == (k == 0 ? content : a.Chain[k - 1])) k++;
            if (k == 0) continue;                                                                // the whole chain left the content
            k--;
            if (!TopInContent(a.Chain[k], content, out float top)) continue;
            bool anchorItself = k == a.Count - 1;
            float moved = anchorItself
                ? (a.ByBottom ? top + DrawnH(a.Chain[k]) : top) - a.Edge
                : top - a.Tops[k];
            float viewportMoved = _scene.AbsoluteRect(node).Y - a.ViewportY;
            float delta = moved + viewportMoved;
            if (MathF.Abs(delta) < 0.01f) continue;   // every fraction: the anchor is chosen afresh each frame, so a skipped one is lost and the content drifts
            handle.ShiftFrame(delta);
            shifted = true;
            // The fresh FLIP holds beside the chain. A node a FLIP holds this frame stays where it was drawn in content space, so the shift
            // would carry it away by the part of the anchor's move that is layout no hold took up (rows above the anchor that grew): below
            // each level of the chain, the sum of those unheld moves. A glide's own step on the chain (a hold easing home) is no such part:
            // the held node beside it glides alike.
            if (_freshHolds.Count == 0) continue;
            float unheld = anchorItself && a.ByBottom ? DrawnH(a.Chain[k]) - a.AnchorH : 0f;
            for (int c = k; c >= 0; c--)
            {
                NodeHandle m = a.Chain[c];
                if (!_freshHolds.Contains(m)) unheld += RelYOf(m) - a.RelY[c];
                a.Unheld[c] = unheld;
            }
            for (int level = -1; level < k; level++)
            {
                float hold = a.Unheld[level + 1];
                if (MathF.Abs(hold) < 0.01f) continue;
                NodeHandle box = level < 0 ? content : a.Chain[level], onChain = a.Chain[level + 1];
                for (var x = _scene.FirstChild(box); !x.IsNull; x = _scene.NextSibling(x))
                    if (x != onChain && _freshHolds.Contains(x)) _anim.ShiftHold(x, AnimChannel.TranslateY, hold);
            }
        }
        _freshHolds.Clear();
        if (shifted) SyncScrollPlansMidFrame();   // the shifted offsets show (and pose the content) on this frame
        for (int i = 0; i < _scrollNodes.Count; i++)
        {
            int idx = _scrollNodes[i];
            if (!AnchoringScroller(idx, out NodeHandle node, out ScrollState sc)) continue;
            if (!_scrollAnchors.TryGetValue(idx, out var a)) _scrollAnchors[idx] = a = new ScrollAnchor();
            a.Offset = sc.Offset;
            a.ViewportY = _scene.AbsoluteRect(node).Y;
            float viewTop = (float)sc.Offset, viewBottom = viewTop + sc.ViewportH;
            a.Count = PickAnchor(sc.ContentNode, viewTop, viewBottom, a);
            if (a.Count == 0) continue;
            NodeHandle anchor = a.Chain[a.Count - 1];
            float t = a.Tops[a.Count - 1], bottom = t + DrawnH(anchor);
            a.ByBottom = t < viewTop && bottom <= viewBottom;
            a.AnchorH = bottom - t;
            a.Edge = a.ByBottom ? bottom : t;
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

    /// <summary>The drawn top of <paramref name="n"/> in <paramref name="content"/>'s space (layout place, live translate rows, flow
    /// shifts; the scroll itself is a paint shift on the content, never in these). False when <paramref name="n"/> is no longer under it.</summary>
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

    /// <summary>The layout top of <paramref name="n"/> in its parent, with the parent's flow shift (no translate rows).</summary>
    private float RelYOf(NodeHandle n)
    {
        NodeHandle p = _scene.Parent(n);
        return _scene.Bounds(n).Y + (p.IsNull ? 0f : _scene.Paint(p).ChildShiftY);
    }

    /// <summary>The height <paramref name="n"/> is drawn at: a size reveal's presented height while one runs, else its layout height.</summary>
    private float DrawnH(NodeHandle n)
    {
        float ph = _scene.Paint(n).PresentedH;
        return float.IsNaN(ph) ? _scene.Bounds(n).H : ph;
    }

    /// <summary>How far <paramref name="n"/>'s live translate row holds it from its layout place: a FLIP seeded this frame holds a moved node
    /// where it was drawn (and that is where the user sees it), a glide carries it from there. The row's UI value: exact on its seed frame,
    /// the last pose fed back while the render thread owns it.</summary>
    private float PresentedShiftY(NodeHandle n) => _anim.TryGetTrackValue(n, AnimChannel.TranslateY, out float v) ? v : 0f;

    /// <summary>The anchor for the window [<paramref name="viewTop"/>, <paramref name="viewBottom"/>) of the content, written to
    /// <paramref name="a"/>'s chain (the content's child first) with each box's drawn top; returns the chain's length (0: nothing in view).
    /// At each level, of the children that reach into the window: the first with content of its own, else a leaf that starts in the
    /// window, else a straddling leaf; while the choice straddles the window's top the walk descends into it, stopping at a node that
    /// starts inside the window, a leaf, or a nested scroller.</summary>
    private int PickAnchor(NodeHandle content, float viewTop, float viewBottom, ScrollAnchor a)
    {
        int count = 0;
        NodeHandle cur = content;
        float curTop = 0f;
        for (int depth = 0; depth < MaxAnchorDepth; depth++)
        {
            NodeHandle next = NodeHandle.Null;
            float nextTop = 0f;
            int nextRank = -1;
            float shift = _scene.Paint(cur).ChildShiftY;
            for (var c = _scene.FirstChild(cur); !c.IsNull; c = _scene.NextSibling(c))
            {
                if ((_scene.Flags(c) & NodeFlags.Visible) == 0) continue;
                ref RectF b = ref _scene.Bounds(c);
                if (b.H <= 0f) continue;
                float t = curTop + b.Y + PresentedShiftY(c) + shift;
                if (t + b.H <= viewTop || t >= viewBottom) continue;
                // A childless box that straddles the window's top is air or a plate (a page gutter, a card's backdrop): what is drawn over
                // it can move while it stays. So a box with content of its own wins; then a leaf that starts in the window (a line of text);
                // a straddling leaf only when nothing else in this row reaches the window.
                bool container = !_scene.FirstChild(c).IsNull;
                int rank = container ? 2 : t >= viewTop ? 1 : 0;
                if (next.IsNull || rank > nextRank) { next = c; nextTop = t; nextRank = rank; }
                if (container) break;
            }
            if (next.IsNull) break;
            a.Chain[count] = next;
            a.Tops[count] = nextTop;
            a.RelY[count] = RelYOf(next);
            count++;
            if (nextTop >= viewTop || (_scene.Flags(next) & NodeFlags.Scrollable) != 0 || _scene.FirstChild(next).IsNull) break;
            cur = next; curTop = nextTop;
        }
        return count;
    }
}
