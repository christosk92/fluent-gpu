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

    // ── the ONE cascade boundary, shared by hover and press ──────────────────────────────────────────────────────────
    //
    // A nested control owns a NEW interaction scope. The dispatcher publishes HoverWithin for every interactive
    // ancestor, so allowing a pane/list ancestor to recurse through an interactive row made that ancestor turn on the
    // reveal affordances of EVERY sibling row (all sidebar ellipses at once). A boundary can itself be the row-owned
    // affordance, though (TrackRow's clickable play surface), so both cascades DRIVE that node and STOP beneath it.
    //
    // Press used to recurse UNCONDITIONALLY, with no boundary at all. Its de-facto reach filter was SetPressCore's
    // interact-row gate (force: false ⇒ a node with no InteractionAnim row is skipped) — and rows exist only for
    // scale/reveal/duration props, so the press-reachable set was already ≈ the hover-reachable set MINUS the boundary
    // rule. That difference is the whole defect: one press on a container that happens to own a handler (a context
    // flyout puts ContextBit in the hit-anywhere mask, so its own background is a hit) drove PressTarget through every
    // nested BUTTON's subtree. Press now stops where hover stops. Note the two filters are deliberately NOT identical:
    // hover additionally asks FollowsContainerHover, because a fill-only control tracks the real pointer; press does
    // not need that question asked twice — the interact-row gate below IS that filter (no row ⇒ no press channel).
    private void SetHoverDescendants(NodeHandle node, bool on)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            bool boundary = IsNestedHoverBoundary(c);
            // Only a REVEAL/scale affordance follows its CONTAINER's hover (a list-row #-cell play glyph fading in on row
            // hover via HoverOpacity, a part that scales). A pure-fill control (♥/like: HoverFill, no HoverOpacity/scale)
            // tracks the ACTUAL pointer, so the container hover must not light it up.
            if (FollowsContainerHover(c)) SetHoverCore(c, on, force: false);
            if (boundary) continue;
            SetHoverDescendants(c, on);
        }
    }

    private bool IsNestedHoverBoundary(NodeHandle node)
    {
        const uint interactive = InteractionInfo.PointerBit | InteractionInfo.ClickBit | InteractionInfo.PressedBit;
        return (_scene.Interaction(node).HandlerMask & interactive) != 0;
    }

    /// <summary>A descendant follows its container's hover only for a reveal (HoverOpacity/PressedOpacity) or a
    /// hover/press scale — not for a fill-only control (which tracks the real pointer).</summary>
    private bool FollowsContainerHover(NodeHandle node)
    {
        ref NodePaint p = ref _scene.Paint(node);
        if (!float.IsNaN(p.HoverOpacity) || !float.IsNaN(p.PressedOpacity)) return true;
        return _scene.TryGetInteract(node, out var ia) && (ia.HoverScale != 1f || ia.PressScale != 1f);
    }

    private void SetPressDescendants(NodeHandle node, bool on)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            // Same shape as hover: drive the child (the interact-row gate inside SetPressCore is the reach filter),
            // then stop at a nested interactive boundary — the boundary node itself still presses if it owns a row
            // (a RadioButton's PressedFill ring, TrackRow's play surface), its SUBTREE does not.
            bool boundary = IsNestedHoverBoundary(c);
            SetPressCore(c, on, force: false);
            if (boundary) continue;
            SetPressDescendants(c, on);
        }
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
