using System;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ScrollIntoView — the ONE programmatic "scroll this node into view" (WinUI UIElement.StartBringIntoView /
//  BringIntoViewOptions).
//
//  Before this, every caller hand-rolled the same delicate dance: walk to the scrolling ancestor, take
//  viewport-relative bounds, clamp against ContentExtent − Viewport, then EITHER write Offset+Target and apply the
//  -offset content LocalTransform (snap) OR write PendingTarget + arm the phase-7 ScrollIntegrator (animate). Each
//  copy diverged slightly — some marked VirtualRangeDirty, some only PaintDirty; some cleared the fling state, some
//  did not. This is that idiom, once.
//
//  NOT modelled here (deliberately): velocity-continuous re-targeting of an already-running programmatic chase with
//  bespoke spring constants. LyricsView's follow-scroll needs it (a re-target mid-chase must keep the carried
//  velocity or the list visibly trails the song) and keeps its own implementation.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Programmatic bring-into-view over the engine's scroll seam. Scrolls along the viewport's own
/// <see cref="ScrollState.Orientation"/> only — a viewport scrolls one axis.</summary>
public static class ScrollIntoView
{
    /// <summary>Bring <paramref name="node"/> into view inside its NEAREST scrolling ancestor.
    /// <paramref name="margin"/> is the gutter kept between the node and the viewport edge it lands against.
    /// <paramref name="alignmentRatio"/> NaN (default) = MINIMAL scroll: do nothing when the node is already visible;
    /// otherwise 0 parks its leading edge at the viewport's leading edge and 1 its trailing edge at the trailing edge
    /// (the WinUI <c>BringIntoViewOptions</c> ratios). <paramref name="animate"/> arms the phase-7 integrator for a
    /// smooth chase (WinUI <c>AnimationDesired</c>) instead of writing the offset now.
    /// Returns true when a scroll was applied or armed.</summary>
    public static bool Bring(RenderContext ctx, NodeHandle node, float margin = 0f,
                             float alignmentRatio = float.NaN, bool animate = false)
    {
        var scene = ctx.Scene;
        if (scene is null || node.IsNull || !scene.IsLive(node)) return false;
        var vp = scene.Parent(node);
        while (!vp.IsNull && !scene.HasScroll(vp)) vp = scene.Parent(vp);
        return !vp.IsNull && BringInto(ctx, vp, node, margin, alignmentRatio, animate);
    }

    /// <summary>Same, against an EXPLICIT viewport — for a composing control that already captured its own
    /// <c>ScrollEl</c> handle (via <c>ScrollEl.OnRealized</c>) and must not scroll some outer page instead.</summary>
    public static bool BringInto(RenderContext ctx, NodeHandle viewport, NodeHandle node, float margin = 0f,
                                 float alignmentRatio = float.NaN, bool animate = false)
    {
        var scene = ctx.Scene;
        if (scene is null || viewport.IsNull || node.IsNull) return false;
        if (!scene.IsLive(viewport) || !scene.IsLive(node) || !scene.HasScroll(viewport)) return false;

        ref ScrollState sc = ref scene.ScrollRef(viewport);
        bool horizontal = sc.Orientation != 0;
        float viewportExtent = horizontal ? sc.ViewportW : sc.ViewportH;
        if (viewportExtent <= 1f) return false;                 // geometry not published yet — the caller should re-post

        // VIEWPORT-RELATIVE bounds, not absolute: reliable inside nested scrollers and under a sticky/morph ancestor,
        // because the live -offset transform is already folded into both rects.
        RectF nodeAbs = scene.AbsoluteRect(node);
        RectF vpAbs = scene.AbsoluteRect(viewport);
        float lead = horizontal ? nodeAbs.X - vpAbs.X : nodeAbs.Y - vpAbs.Y;
        float extent = horizontal ? nodeAbs.W : nodeAbs.H;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;

        float target;
        if (float.IsNaN(alignmentRatio))
        {
            float trail = lead + extent;
            if (lead < margin) target = offset + lead - margin;
            else if (trail > viewportExtent - margin) target = offset + trail - viewportExtent + margin;
            else return false;                                  // already fully visible — minimal scroll does nothing
        }
        else
        {
            // 0 ⇒ leading edge at the leading gutter, 1 ⇒ trailing edge at the trailing gutter. An item taller than the
            // viewport has no slack, so every ratio degenerates to "leading edge at the gutter", which is what a reader
            // wants (start at the top of the thing, not the middle of it).
            float ratio = Math.Clamp(alignmentRatio, 0f, 1f);
            float slack = MathF.Max(0f, viewportExtent - extent - margin * 2f);
            target = offset + lead - margin - ratio * slack;
        }

        return ScrollTo(ctx, viewport, target, animate);
    }

    /// <summary>The WRITE half on its own: move <paramref name="viewport"/> to a CONTENT-SPACE offset along its own
    /// orientation, clamped to <c>[0, content − viewport]</c>. Split out for callers that compute the destination from a
    /// layout MODEL rather than a realized node — a virtualized list scrolling to an index that is not realized yet has
    /// no node to hand <see cref="Bring"/>. Returns true when the offset moved (or a chase was armed).</summary>
    public static bool ScrollTo(RenderContext ctx, NodeHandle viewport, float target, bool animate)
    {
        var scene = ctx.Scene;
        if (scene is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return false;

        ref ScrollState sc = ref scene.ScrollRef(viewport);
        bool horizontal = sc.Orientation != 0;
        float viewportExtent = horizontal ? sc.ViewportW : sc.ViewportH;
        float contentExtent = horizontal ? sc.ContentW : sc.ContentH;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;

        target = Math.Clamp(target, 0f, MathF.Max(0f, contentExtent - viewportExtent));
        if (MathF.Abs(target - offset) < 0.5f) return false;

        if (animate)
        {
            // Hand off to the phase-7 ScrollIntegrator: it chases Offset → PendingTarget with the programmatic
            // crit-damped spring, re-realizing the virtual window and fading the bar as it goes.
            //
            // VELOCITY-PRESERVING RETARGET. Re-arming a chase that is ALREADY a programmatic glide keeps the carried
            // spring velocity, so the closed form bends toward the new target instead of restarting from rest — the
            // visible hitch when a pager re-arms mid-glide (a second chevron click, a same-page re-arm after a partial
            // pan). Any other inbound phase starts at rest: carrying a FRICTION-coast (fling) or finger-tracking velocity
            // into a spring chase would slingshot past the target.
            bool retarget = sc.Phase == ScrollIntegrator.WheelAnimating
                         && (sc.PhaseFlags & ScrollState.PhaseProgrammatic) != 0
                         && (sc.PhaseFlags & ScrollState.PhaseImmediate) == 0;
            if (!retarget) sc.FlingVelocity = 0f;
            // Latch the half-life from the travel REMAINING at arm time (a retarget re-solves it for the new distance).
            // Per-chase, never per-tick, so the integrator's step stays the exact closed form (dt-deterministic).
            sc.ProgrammaticHalflifeMs = ScrollTuning.ProgrammaticHalflifeForDistance(target - offset);
            if (horizontal) sc.PendingTargetX = target; else sc.PendingTargetY = target;
            sc.Phase = ScrollIntegrator.WheelAnimating;
            sc.PhaseFlags = ScrollState.PhaseProgrammatic;
            sc.FlingRetargeted = false;
            sc.FlingSnapTarget = float.NaN;
            ctx.ArmScroll?.Invoke(viewport);
            ctx.RequestRerender();
            return true;
        }

        // Snap: the dispatcher's SetScrollOffset idiom — write Offset==Target, ARREST any in-flight chase or fling, and
        // apply the -offset content transform NOW so the node is on screen this frame (an edit-enter must land before
        // the user types). Leaving a chase armed here would let it drag the offset straight back off the target.
        if (horizontal) { sc.OffsetX = target; sc.TargetX = target; sc.PendingTargetX = float.NaN; }
        else { sc.OffsetY = target; sc.TargetY = target; sc.PendingTargetY = float.NaN; }
        sc.Phase = ScrollIntegrator.Idle;
        sc.PhaseFlags = 0;
        sc.FlingVelocity = 0f;
        sc.ProgrammaticHalflifeMs = 0f;
        sc.FlingRetargeted = false;
        sc.FlingSnapTarget = float.NaN;

        var content = sc.ContentNode;
        if (!content.IsNull && scene.IsLive(content))
        {
            scene.Paint(content).LocalTransform =
                Affine2D.Translation(horizontal ? -target : 0f, horizontal ? 0f : -target);
            scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
        scene.Mark(viewport, NodeFlags.PaintDirty | NodeFlags.VirtualRangeDirty);
        ctx.RequestRerender();
        return true;
    }
}
