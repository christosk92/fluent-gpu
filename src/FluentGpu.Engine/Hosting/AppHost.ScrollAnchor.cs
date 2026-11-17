using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Hosting;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Scroll anchoring (ScrollEl.Anchoring, in the spirit of CSS overflow-anchor): what the user is looking at stays where it is
//  in the window when layout above it changes size. Per opted-in vertical scroller the host keeps one ANCHOR: the first node in
//  reading order that starts inside the viewport (descending through the node that straddles its top, never into a nested
//  scroller; a straddling box with nothing inside it in view is passed over), with the chain of boxes from the content down to it and each one's drawn top in content space. Once per frame
//  after the frame's layout, the anchor's new place is compared with the remembered one and the scroll's frame is shifted by the
//  difference (ScrollHandle.ShiftFrame: a structural rebase that moves with any plan, never a motion), then a fresh anchor is chosen.
//
//  What "the anchor's place" is:
//   • DRAWN, not laid out: its layout place plus the live translate rows on its chain and the flow shifts. A tile a FLIP holds
//     at its old place has not moved for the user, so it is not compensated; a glide that carries it is followed.
//   • In the WINDOW: a viewport whose top moved (a header above it changing height at the same tier edge) carries the content
//     with it, so that move is taken back too.
//   • By its top, as CSS does. (Not by the bottom of a box straddling the window's top: in a bento board the tile beside it does
//     not follow its bottom, and a clip reveal moves that bottom a frame after the layout did.)
//   • When the anchor is gone (a tier crossing re-forms its subtree), by the deepest box of its chain still in place.
//   • Never inside a box drawn through a live vertical scale (a size FLIP holding a re-formed card at its old size): its children are drawn
//     through the scale, so their layout tops jump where the user sees them ease. That box is held by its own top. (A Zune capped card going
//     from four columns to five at a tier commit moved the words under its cover 49 up in layout, and the page was shifted 49 at once.)
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
    private const int MaxAnchorDepth = 256;   // recursion depth too: 256 frames of a small method

    /// <summary>One scroller's anchor: the chain from the content's child down to the anchor, each box's drawn top in content space when
    /// chosen, and the edge the anchor is held by.</summary>
    private sealed class ScrollAnchor
    {
        public readonly NodeHandle[] Chain = new NodeHandle[MaxAnchorDepth];
        public readonly float[] Tops = new float[MaxAnchorDepth];
        public readonly float[] RelY = new float[MaxAnchorDepth];     // each box's layout top in its parent (with the parent's flow shift)
        public readonly float[] Widths = new float[MaxAnchorDepth];   // each box's layout width
        public readonly float[] Unheld = new float[MaxAnchorDepth];   // scratch: the layout moves below each level no FLIP hold took up
        public int Count;          // 0: no anchor
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
            // The deepest box of the chain still where it was in the tree (a re-formed subtree keeps the boxes around it) and still shown: a box
            // hidden this frame is out of the layout, its rect stale (a capped grid's last card hidden for the frame a window steps across a tier
            // edge: its words' stale top read 412 below where they were, and the page was shifted by it).
            int k = 0;
            while (k < a.Count && _scene.IsLive(a.Chain[k]) && (_scene.Flags(a.Chain[k]) & NodeFlags.Visible) != 0
                   && _scene.Parent(a.Chain[k]) == (k == 0 ? content : a.Chain[k - 1])) k++;
            if (k == 0) continue;                                                                // the whole chain left the content
            k--;
            // A box drawn through a live vertical scale (a size FLIP easing a re-formed card) holds by its own top: what is inside it is drawn
            // through the scale, which the layout places under it do not show.
            for (int q = 0; q < k; q++)
                if (ScalingY(a.Chain[q])) { k = q; break; }
            // So does a box that re-formed against its parent (its width moved more than its parent's did: a grid cell that a column count change
            // widened at once): what is inside it was laid out afresh at the new width, so the places in it say nothing of where the page went.
            // (A WinUI capped card going from eight columns to six at a tier commit pushed its title 75 down under a taller cover, and the page
            // was shifted 75 at once to hold the title.) A box that only follows its parent's width (a reflow easing the page's margins) does not.
            for (int q = 1; q < k; q++)
            {
                float dw = MathF.Abs(_scene.Bounds(a.Chain[q]).W - a.Widths[q]), dp = MathF.Abs(_scene.Bounds(a.Chain[q - 1]).W - a.Widths[q - 1]);
                if (dw > dp + 0.5f) { k = q; break; }
            }
            if (!TopInContent(a.Chain[k], content, out float top)) continue;
            float moved = top - a.Tops[k];
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
            float unheld = 0f;
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
            a.Count = PickAnchor(sc.ContentNode, 0f, 0, viewTop, viewBottom, a);
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

    /// <summary>How far <paramref name="n"/>'s live translate row holds it from its layout place: a FLIP seeded this frame holds a moved node
    /// where it was drawn (and that is where the user sees it), a glide carries it from there. The row's UI value: exact on its seed frame,
    /// the last pose fed back while the render thread owns it.</summary>
    private float PresentedShiftY(NodeHandle n) => _anim.TryGetTrackValue(n, AnimChannel.TranslateY, out float v) ? v : 0f;

    /// <summary>Whether <paramref name="n"/> is drawn through a live vertical scale (a size FLIP holding a re-formed box at its old size while
    /// it eases to its new one): the boxes inside it are drawn through that scale, so their layout tops say nothing of where the user sees
    /// them, and the anchor neither descends into it nor follows a box inside it.</summary>
    private bool ScalingY(NodeHandle n) => _anim.TryGetTrackValue(n, AnimChannel.ScaleY, out float s) && MathF.Abs(s - 1f) > 1e-4f;

    /// <summary>The anchor for the window [<paramref name="viewTop"/>, <paramref name="viewBottom"/>) under <paramref name="cur"/> (its top
    /// <paramref name="curTop"/> in content space), written to <paramref name="a"/>'s chain from <paramref name="depth"/> on with each box's
    /// drawn top; returns the chain's length (<paramref name="depth"/>: nothing found). In reading order, the first child that starts in the
    /// window is the anchor. A child that straddles the window's top is descended into, and passed over when nothing inside it reaches the
    /// window (its visible part is padding or a backdrop: what follows it is what shows). A straddling leaf or nested scroller is the anchor
    /// only when nothing after it starts in the window; so is one drawn through a live vertical scale (<see cref="ScalingY"/>), and so is a
    /// straddling box whose only part in the window is such a leaf: a later sibling that starts in the window is what the user looks at. (A
    /// Zune Home section whose last line of card titles poked 10 into the window's top held the page while it grew at a tier commit, and the
    /// spotlight under it, which the user was looking at, glided 450 down and out of the window.)</summary>
    private int PickAnchor(NodeHandle cur, float curTop, int depth, float viewTop, float viewBottom, ScrollAnchor a)
    {
        _pickStarts = false;
        if (depth >= MaxAnchorDepth) return depth;
        float shift = _scene.Paint(cur).ChildShiftY;
        NodeHandle fallback = NodeHandle.Null;
        float fallbackTop = 0f;
        int fallbackFound = 0;   // > 0: the fallback is a straddling box whose chain below it is written (to this length)
        for (var c = _scene.FirstChild(cur); !c.IsNull; c = _scene.NextSibling(c))
        {
            if ((_scene.Flags(c) & NodeFlags.Visible) == 0) continue;
            ref RectF b = ref _scene.Bounds(c);
            if (b.H <= 0f) continue;
            float t = curTop + b.Y + PresentedShiftY(c) + shift;
            if (t + b.H <= viewTop || t >= viewBottom) continue;
            if (t >= viewTop)   // starts in the window
            {
                a.Chain[depth] = c;
                a.Tops[depth] = t;
                a.RelY[depth] = RelYOf(c);
                a.Widths[depth] = b.W;
                _pickStarts = true;
                return depth + 1;
            }
            bool leaf = _scene.FirstChild(c).IsNull || (_scene.Flags(c) & NodeFlags.Scrollable) != 0 || ScalingY(c);
            if (!leaf)
            {
                // After a straddling box already kept as the fallback, a later straddling box is not descended (its chain would write over the
                // fallback's): only a sibling that starts in the window takes the anchor from it.
                if (fallbackFound > 0) continue;
                a.Chain[depth] = c;
                a.Tops[depth] = t;
                a.RelY[depth] = RelYOf(c);
                a.Widths[depth] = b.W;
                int found = PickAnchor(c, t, depth + 1, viewTop, viewBottom, a);
                if (found > depth + 1)
                {
                    if (_pickStarts) return found;
                    fallback = c; fallbackTop = t; fallbackFound = found;   // only a straddling leaf inside it shows
                }
                continue;   // nothing inside it starts in the window
            }
            if (fallback.IsNull) { fallback = c; fallbackTop = t; }
        }
        _pickStarts = false;
        if (fallback.IsNull) return depth;
        a.Chain[depth] = fallback;
        a.Tops[depth] = fallbackTop;
        a.RelY[depth] = RelYOf(fallback);
        a.Widths[depth] = _scene.Bounds(fallback).W;
        return fallbackFound > 0 ? fallbackFound : depth + 1;
    }

    /// <summary>Whether the anchor the last <see cref="PickAnchor"/> found starts in the window (false: a straddling fallback).</summary>
    private bool _pickStarts;
}
