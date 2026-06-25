using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — the InteractionState priority resolver (Framer whileHover/whileTap; design §3.5).
//
//  Generalizes the hardcoded 2-state InteractionAnimator to N declarative gesture states. The reconciler stashes a
//  node's WhileHover/WhilePressed/WhileFocus targets; AppHost fires ApplyInteractionEdge on the input hover/press/
//  focus edge; the resolver picks the active target by fixed priority (press > focus > hover > rest) and springs the
//  gesture channels to it via SeedTarget (releasing a state animates back to the next writer / rest). Additive —
//  coexists with InteractionAnimator (a node uses While* OR the old HoverScale/HoverFill) until that class is deleted.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>Which interaction state changed (fed by the input dispatch edges).</summary>
    public enum InteractKind : byte { Hover, Press, Focus }

    private struct InteractTargets
    {
        public MotionTarget? Hover, Press, Focus;
        public MotionTokenDef Motion;
        public bool IsHovered, IsPressed, IsFocused;
    }

    private readonly Dictionary<int, InteractTargets> _interactTargets = new();

    /// <summary>Stash (or clear) a node's gesture-state targets at reconcile. All-null clears the row (node opted out).
    /// Preserves the live hover/press/focus flags across a re-render so an in-flight state survives a reconcile.</summary>
    public void SetInteractTargets(int nodeIndex, MotionTarget? hover, MotionTarget? press, MotionTarget? focus, in MotionTokenDef motion)
    {
        if (hover is null && press is null && focus is null) { _interactTargets.Remove(nodeIndex); return; }
        InteractTargets t = _interactTargets.TryGetValue(nodeIndex, out var ex) ? ex : default;
        t.Hover = hover; t.Press = press; t.Focus = focus; t.Motion = motion;
        _interactTargets[nodeIndex] = t;
    }

    internal void ClearInteractTargets(int nodeIndex) => _interactTargets.Remove(nodeIndex);

    /// <summary>On an input hover/press/focus edge: update the state, resolve the active target by fixed priority
    /// (press &gt; focus &gt; hover &gt; rest), and spring the gesture channels to it. Releasing the top state animates
    /// to the next writer's target (or rest = identity). No-op for a node without stashed targets.</summary>
    public void ApplyInteractionEdge(NodeHandle node, InteractKind kind, bool on)
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

        MotionTarget target =
            t.IsPressed && t.Press is { } p ? p :
            t.IsFocused && t.Focus is { } f ? f :
            t.IsHovered && t.Hover is { } h ? h :
            new MotionTarget();   // rest = ctor defaults (Scale 1, Opacity 1)
        SeedTarget(node, in target, in t.Motion);
    }
}
