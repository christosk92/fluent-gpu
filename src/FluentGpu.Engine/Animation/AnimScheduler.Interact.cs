using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — the InteractionState priority resolver (Framer whileHover/whileTap; design §3.5).
//
//  Generalizes the hardcoded 2-state InteractionAnimator (subsumed + deleted — see AnimScheduler.Hover.cs) to N
//  declarative gesture states. The reconciler stashes a node's WhileHover/WhilePressed/WhileFocus targets ALONGSIDE
//  its authored rest pose (Reconciler.cs's SetInteractTargets call site); AppHost fires ApplyInteractionEdge on the
//  input hover/press/focus edge; the resolver picks the active target by fixed priority (press > focus > hover >
//  rest) and folds it, as a DELTA, over the stashed rest pose via SeedTargetOver — releasing every state animates
//  back to that authored pose, never to identity (MotionTarget's rest-pose-relative contract).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>Which interaction state changed (fed by the input dispatch edges).</summary>
    public enum InteractKind : byte { Hover, Press, Focus }

    private struct InteractTargets
    {
        public MotionTarget? Hover, Press, Focus;
        public MotionTarget Rest;                       // the node's AUTHORED pose (see the rest-relative contract)
        public MotionTokenDef Motion;
        public bool IsHovered, IsPressed, IsFocused;
    }

    private readonly Dictionary<int, InteractTargets> _interactTargets = new();

    /// <summary>Stash (or clear) a node's gesture-state targets at reconcile. All-null clears the row (node opted out).
    /// Preserves the live hover/press/focus flags across a re-render so an in-flight state survives a reconcile.
    /// <paramref name="rest"/> is the node's AUTHORED pose (its static OffsetX/OffsetY/Rotation/ScaleX/Opacity/Blur) —
    /// While* targets are deltas on it (<see cref="MotionTarget"/>'s rest-pose-relative contract), so it must be
    /// re-stashed every reconcile even when the While* legs themselves are unchanged (the authored pose can move).</summary>
    public void SetInteractTargets(int nodeIndex, MotionTarget? hover, MotionTarget? press, MotionTarget? focus,
                                   in MotionTarget rest, in MotionTokenDef motion)
    {
        if (hover is null && press is null && focus is null) { _interactTargets.Remove(nodeIndex); return; }
        InteractTargets t = _interactTargets.TryGetValue(nodeIndex, out var ex) ? ex : default;
        t.Hover = hover; t.Press = press; t.Focus = focus; t.Rest = rest; t.Motion = motion;
        _interactTargets[nodeIndex] = t;
    }

    internal void ClearInteractTargets(int nodeIndex) => _interactTargets.Remove(nodeIndex);

    /// <summary>On an input hover/press/focus edge: update the state, resolve the active target by fixed priority
    /// (press &gt; focus &gt; hover &gt; rest), and spring the gesture channels to it. Releasing the top state animates
    /// to the next writer's target — or, with nothing active, back to the node's AUTHORED rest pose (never identity;
    /// see <see cref="MotionTarget"/>'s rest-pose-relative contract). No-op for a node without stashed targets.</summary>
    public void ApplyInteractionEdge(NodeHandle node, InteractKind kind, bool on)
        => ApplyInteractionEdgeSelf(node, kind, on);

    /// <summary>The worker behind <see cref="ApplyInteractionEdge"/> — split out so the hover cascade
    /// (<c>AnimScheduler.Hover.SetHoverDescendants</c>) can drive a non-boundary descendant's own While* row directly,
    /// without going through a NodeHandle-shaped public re-entry.</summary>
    internal void ApplyInteractionEdgeSelf(NodeHandle node, InteractKind kind, bool on)
    {
        int idx = (int)node.Raw.Index;
        if (!_interactTargets.TryGetValue(idx, out var t)) return;
        switch (kind)
        {
            case InteractKind.Hover: t.IsHovered = on; break;
            case InteractKind.Press: t.IsPressed = on; break;
            case InteractKind.Focus: t.IsFocused = on; break;
        }
        _interactTargets[idx] = t;

        MotionTarget delta =
            t.IsPressed && t.Press is { } p ? p :
            t.IsFocused && t.Focus is { } f ? f :
            t.IsHovered && t.Hover is { } h ? h :
            new MotionTarget();                          // identity DELTA ⇒ animate back to the AUTHORED pose
        SeedTargetOver(node, in t.Rest, in delta, in t.Motion);
    }

    /// <summary>Cancel While* transform tracks and land the node on its authored rest pose immediately. KeepAlive
    /// park/un-park uses this so a cached page does not present a leftover hover fan — and so
    /// <see cref="SnapStructuralToLayout"/> cannot leave Fold covers at identity (FLIP TranslateX shares the channel
    /// with Offset/WhileHover; hover then looks like it "fixes" the stack because it retargets from 0 to rest+delta).
    /// Pointer state is cleared: the parked subtree is not under the cursor.</summary>
    public void SnapAuthoredPose(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        if (!_interactTargets.TryGetValue(idx, out var t)) return;
        t.IsHovered = false; t.IsPressed = false; t.IsFocused = false;
        _interactTargets[idx] = t;

        Cancel(node, AnimChannel.TranslateX);
        Cancel(node, AnimChannel.TranslateY);
        Cancel(node, AnimChannel.ScaleX);
        Cancel(node, AnimChannel.ScaleY);
        Cancel(node, AnimChannel.Rotation);

        if (!_scene.IsLive(node)) return;
        ref NodePaint p = ref _scene.Paint(node);
        p.LocalTransform = ComposeRest(in t.Rest);
        _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    /// <summary>Whether a structural-looking Translate/Scale row is actually the While* gesture channel, not a FLIP
    /// projection. <see cref="SnapStructuralToLayout"/> must not treat those as FLIP leftovers — freeing them and
    /// resetting <c>LocalTransform</c> to identity parks authored Offset at the origin until the next hover.</summary>
    private bool IsGestureOwnedTransform(int nodeIndex, AnimChannel ch)
        => (ch is AnimChannel.TranslateX or AnimChannel.TranslateY or AnimChannel.ScaleX or AnimChannel.ScaleY)
           && _interactTargets.ContainsKey(nodeIndex);

    static Affine2D ComposeRest(in MotionTarget rest)
    {
        var tf = Affine2D.Translation(rest.OffsetX, rest.OffsetY);
        if (rest.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(rest.Rotation * (MathF.PI / 180f)));
        if (rest.Scale != 1f) tf = tf.Multiply(Affine2D.Scale(rest.Scale, rest.Scale));
        return tf;
    }
}
