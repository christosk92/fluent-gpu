using FluentGpu.Foundation;

namespace FluentGpu.Scene;

// F169 (follow-rect): a node that must sit exactly where ANOTHER node is on screen THIS frame (the docked video overlay
// over its hollow reservation). A signal sampled from OnBoundsChanged cannot do that: the callback fires inside Arrange
// and only on a LOCAL arranged-rect edge, so the follower lags the target by a frame, and a paint-only move of the
// target (a rail slide, a page transition) never fires it at all. This is the engine primitive instead: the host runs
// SizeFollowRects / PlaceFollowRects after layout and the animation tick and before the video geometry scan and record.
// "The target's painted position is final" holds for UI-owned motion. A render-thread-owned compositor row (the default
// Async host) never advances the UI-side transform between feedbacks, so the host also publishes CollectFollowAnchors to
// the animation engine, which keeps the translate/scale rows on the followed chains UI-owned while a follower follows.
public sealed partial class SceneStore
{
    private readonly struct FollowEntry
    {
        public readonly NodeHandle Node;
        public readonly Func<NodeHandle> Target;
        public FollowEntry(NodeHandle node, Func<NodeHandle> target) { Node = node; Target = target; }
    }

    private readonly List<FollowEntry> _followers = new();

    /// <summary>True while any node declares a follow target (the host's one-compare gate for the post-layout pass).</summary>
    public bool HasFollowers => _followers.Count != 0;

    /// <summary>Install, replace or (null) remove the follow target of <paramref name="node"/>. The thunk is read each
    /// pass and answers the node to follow right now, or <see cref="NodeHandle.Null"/> for "not following": the node then
    /// keeps whatever its own Width/Height/Transform say. It runs on the UI thread outside any reactive scope (read signals
    /// with <c>Peek</c>) and must not allocate. The target must not be inside the follower's own subtree.</summary>
    public void SetFollowRect(NodeHandle node, Func<NodeHandle>? target)
    {
        if (!IsLive(node)) return;
        for (int i = 0; i < _followers.Count; i++)
        {
            if (_followers[i].Node != node) continue;
            if (target is null) _followers.RemoveAt(i);
            else if (!ReferenceEquals(_followers[i].Target, target)) _followers[i] = new FollowEntry(node, target);
            return;
        }
        if (target is not null) _followers.Add(new FollowEntry(node, target));
    }

    /// <summary>Whether <paramref name="node"/> is following a live target right now. A node's own Width/Height/Transform
    /// bindings stand down while this is true (the pass rewrites all three every frame), and take over again the moment the
    /// thunk answers Null. False without a lookup when nothing follows.</summary>
    public bool IsFollowing(NodeHandle node)
    {
        if (_followers.Count == 0) return false;
        for (int i = 0; i < _followers.Count; i++)
            if (_followers[i].Node == node) return TryResolveFollowTarget(_followers[i], out _);
        return false;
    }

    private bool TryResolveFollowTarget(in FollowEntry entry, out NodeHandle target)
    {
        target = default;
        if (!IsLive(entry.Node)) return false;
        NodeHandle t = entry.Target();
        if (t.IsNull || t == entry.Node || !IsLive(t)) return false;
        for (NodeHandle p = Parent(t); !p.IsNull; p = Parent(p))
            if (p == entry.Node) return false;   // a target inside the follower would chase its own tail
        target = t;
        return true;
    }

    /// <summary>Add to <paramref name="into"/> every node whose painted pose <see cref="PlaceFollowRects"/> reads for a
    /// follower that is following right now: the follower and its ancestors, the target and its ancestors (a common
    /// ancestor's pose cancels in the math, but a render-owned one would still move both ends by different frames' worth of
    /// feedback, so the whole chains are held). Hand the set to <c>AnimEngine.SetFollowAnchors</c>. The caller clears it.</summary>
    public void CollectFollowAnchors(HashSet<NodeHandle> into)
    {
        for (int i = 0; i < _followers.Count; i++)
        {
            FollowEntry entry = _followers[i];
            if (!TryResolveFollowTarget(entry, out NodeHandle target)) continue;
            // Every Add walks to the root, so a chain that hits a node already present is already complete above it.
            for (NodeHandle n = entry.Node; !n.IsNull; n = Parent(n)) if (!into.Add(n)) break;
            for (NodeHandle n = target; !n.IsNull; n = Parent(n)) if (!into.Add(n)) break;
        }
    }

    /// <summary>Phase 1: write each follower's LAYOUT size from its target's laid-out size and mark it layout-dirty when
    /// it moved. Returns true when anything was marked, so the host re-solves the dirty scopes before
    /// <see cref="PlaceFollowRects"/> reads the follower's origin. Also prunes followers whose node was freed.</summary>
    public bool SizeFollowRects()
    {
        bool sized = false;
        for (int i = _followers.Count - 1; i >= 0; i--)
        {
            FollowEntry entry = _followers[i];
            if (!IsLive(entry.Node)) { _followers.RemoveAt(i); continue; }
            if (!TryResolveFollowTarget(entry, out NodeHandle target)) continue;
            ref readonly RectF tb = ref Bounds(target);
            if (float.IsNaN(tb.W) || float.IsNaN(tb.H)) continue;
            ref LayoutInput li = ref Layout(entry.Node);
            if (li.Width.Equals(tb.W) && li.Height.Equals(tb.H)) continue;
            li.Width = tb.W;
            li.Height = tb.H;
            Mark(entry.Node, NodeFlags.LayoutDirty);
            sized = true;
        }
        return sized;
    }

    /// <summary>Phase 2: write each follower's paint translation so its box lands on its target's PAINTED origin
    /// (<see cref="AbsoluteRect"/>: ancestors' scroll, composited translation and ChildShift included, which is how a rail
    /// that slides on a paint-only animation moves it; the animation engine keeps those rows UI-ticked while the follower
    /// follows, see <see cref="CollectFollowAnchors"/>). The translation is the target's window origin minus the follower's
    /// own un-transformed origin, so it holds wherever the follower is laid out. Value-gated at 0.001 DIP so float noise
    /// never re-marks a settled frame.</summary>
    public void PlaceFollowRects()
    {
        for (int i = 0; i < _followers.Count; i++)
        {
            FollowEntry entry = _followers[i];
            if (!TryResolveFollowTarget(entry, out NodeHandle target)) continue;
            RectF want = AbsoluteRect(target);
            RectF self = AbsoluteRect(entry.Node);
            ref NodePaint paint = ref Paint(entry.Node);
            float dx = want.X - (self.X - paint.LocalTransform.Dx);
            float dy = want.Y - (self.Y - paint.LocalTransform.Dy);
            Affine2D cur = paint.LocalTransform;
            if (cur.M11 == 1f && cur.M12 == 0f && cur.M21 == 0f && cur.M22 == 1f
                && MathF.Abs(cur.Dx - dx) < 0.001f && MathF.Abs(cur.Dy - dy) < 0.001f) continue;
            paint.LocalTransform = Affine2D.Translation(dx, dy);
            Mark(entry.Node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }
}
