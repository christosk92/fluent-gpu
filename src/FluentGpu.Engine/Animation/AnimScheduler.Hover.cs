using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — hover/press progress, now engine-driven (the former InteractionAnimator, subsumed + deleted).
//
//  SetHover/SetPress + the reveal-on-hover descendant cascade move here from the deleted InteractionAnimator class;
//  the per-frame Tick is GONE. HoverT/PressT are eased by HoverFade/PressFade tracks in the unified AnimValue slab
//  (PASS1 writes the InteractionAnim side-table via WriteSideTable; the recorder's hover/press composite is unchanged,
//  so the cp-series + w1controls hover/press gates are unaffected). One ticker fewer; one engine.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>Pointer entered/left a node (the dispatcher's HoverWithin edge). A container reads as hovered while the
    /// pointer is anywhere in its subtree, so its reveal/scale descendants stay driven as the pointer crosses onto a
    /// child — the effective guard makes a stray leaf-hover-off of a still-within container a no-op.</summary>
    public void SetHover(NodeHandle node, bool on)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        bool effective = on || (_scene.Flags(node) & (NodeFlags.Hovered | NodeFlags.HoverWithin)) != 0;
        SetHoverCore(node, effective, force: true);
        SetHoverDescendants(node, effective);
    }

    public void SetPress(NodeHandle node, bool on)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        SetPressCore(node, on, force: true);
        SetPressDescendants(node, on);
    }

    // ── the ONE cascade rule, shared by hover and press ──────────────────────────────────────────────────────────────
    //
    // CASCADE THE REVEAL, NEVER THE CONTROL'S OWN STATE. Two legs with genuinely different semantics:
    //
    //   REVEAL (HoverOpacity / PressedOpacity) — follows its container ACROSS a boundary. A hover-revealed child is the
    //   CONTAINER's affordance appearing (a row's #-cell play glyph, a card's "…" corner, a track row's heart): the whole
    //   point is that the pointer is NOT on it yet, so it cannot possibly drive itself.
    //
    //   SCALE (HoverScale / PressScale) — follows its container ONLY when the child is not itself interactive. An
    //   interactive node is its OWN interaction scope: the dispatcher already gives it Hovered / HoverWithin / Pressed
    //   when the pointer is genuinely on it, so it needs no inheritance. This is what canon has always said —
    //   backdrop-effects-animation.md §7 and controls.md scale a node "by the eased hover/press of its nearest
    //   interactive ancestor", and a nested button IS its own nearest interactive ancestor.
    //
    // Both cascades used to compute the boundary and then drive the child ANYWAY, stopping only BENEATH it. So every
    // nested control carrying a scale grew when any interactive ancestor was hovered and dipped when it was pressed:
    // hovering a home card's copy area grew its Play / Shuffle / ♥ / "…" cluster, and press-and-holding the card rendered
    // all four pressed. (Registered as a named residual when the press cascade gained hover's boundary; this is the
    // follow-up.) A container needs no such inheritance to light a REVEAL, which is the case the old ordering was
    // protecting — hence the split rather than a blanket "boundaries inherit nothing".
    //
    // A fill-only control (♥/like: HoverFill with no HoverOpacity/scale) is in NEITHER leg and never was: it owns no
    // InteractionAnim row, so it tracks the actual pointer. Press now applies the SAME predicate as hover rather than its
    // looser interact-row gate, so the two can no longer disagree about who is being driven.
    private void SetHoverDescendants(NodeHandle node, bool on)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            bool boundary = IsNestedHoverBoundary(c);
            if (FollowsContainer(c, boundary)) SetHoverCore(c, on, force: false);
            if (boundary) continue;
            SetHoverDescendants(c, on);
        }
    }

    private void SetPressDescendants(NodeHandle node, bool on)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            bool boundary = IsNestedHoverBoundary(c);
            if (FollowsContainer(c, boundary)) SetPressCore(c, on, force: false);
            if (boundary) continue;
            SetPressDescendants(c, on);
        }
    }

    /// <summary>Does this node own its own interaction scope? Click / pointer / pressed handlers make it a control in its
    /// own right rather than a part of its container.</summary>
    private bool IsNestedHoverBoundary(NodeHandle node)
    {
        const uint interactive = InteractionInfo.PointerBit | InteractionInfo.ClickBit | InteractionInfo.PressedBit;
        return (_scene.Interaction(node).HandlerMask & interactive) != 0;
    }

    /// <summary>Whether a descendant's hover/press progress is driven by its CONTAINER. A reveal always is; a scale is
    /// only when the node is not its own interaction scope (see the cascade-rule comment above).</summary>
    private bool FollowsContainer(NodeHandle node, bool ownsScope)
    {
        ref NodePaint p = ref _scene.Paint(node);
        if (!float.IsNaN(p.HoverOpacity) || !float.IsNaN(p.PressedOpacity)) return true;   // reveal — crosses boundaries
        if (ownsScope) return false;                                                       // its own control state
        return _scene.TryGetInteract(node, out var ia) && (ia.HoverScale != 1f || ia.PressScale != 1f);
    }

    /// <summary>Seed a NEWLY MOUNTED node's hover from the container scope it mounted into — the reconciler's lazy
    /// hover-affordance path (a media-card play FAB mounts after its card already took the pointer-enter edge). Applies
    /// the SAME rule as the cascade, which the old path bypassed by going through <see cref="SetHover"/>'s
    /// <c>force: true</c> arm: a reveal is seeded, a nested interactive control is not, so a button that mounts (or
    /// re-keys) inside a hovered card no longer lights up with no pointer edge at all.
    /// <para>Returns false when nothing was seeded, so a caller without an <see cref="AnimEngine"/> equivalent can tell
    /// the node was deliberately left at rest.</para></summary>
    public bool TrySeedHoverFromContainer(NodeHandle node)
    {
        if (node.IsNull || !_scene.IsLive(node)) return false;
        bool boundary = IsNestedHoverBoundary(node);
        if (!FollowsContainer(node, boundary)) return false;
        SetHoverCore(node, true, force: true);
        if (!boundary) SetHoverDescendants(node, true);
        return true;
    }

    private void SetHoverCore(NodeHandle node, bool on, bool force)
    {
        if (!force && !_scene.TryGetInteract(node, out _)) return;
        ref InteractionAnim ia = ref _scene.InteractRef(node);
        ia.HoverTarget = on ? 1f : 0f;   // record the target in the column — visual-state consumers + gates read HoverTarget
        SeedInteractFade(node, AnimChannel.HoverFade, ia.HoverT, ia.HoverTarget, ia.HoverDurationMs, ia.HoverEasing);
    }

    private void SetPressCore(NodeHandle node, bool on, bool force)
    {
        if (!force && !_scene.TryGetInteract(node, out _)) return;
        ref InteractionAnim ia = ref _scene.InteractRef(node);
        ia.PressTarget = on ? 1f : 0f;
        SeedInteractFade(node, AnimChannel.PressFade, ia.PressT, ia.PressTarget, ia.PressDurationMs, ia.PressEasing);
    }

    /// <summary>Seed (or retarget) the hover/press fade as an eased track HoverT→target over the node's authored
    /// duration/easing, written to the InteractionAnim side-table each tick. No first-frame hold — matches the old
    /// InteractionAnimator.Step (which advanced immediately), so the recorder's per-frame composite is identical.</summary>
    private void SeedInteractFade(NodeHandle node, AnimChannel ch, float from, float to, float durMs, EasingSpec easing)
        => SeedEased(node, ch, from, to, durMs, easing.NamedOr(Easing.FluentPopOpen));
}
