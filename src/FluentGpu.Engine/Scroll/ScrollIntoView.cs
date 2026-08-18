using System;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Scroll;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ScrollIntoView — the ONE programmatic "scroll this node into view" (WinUI UIElement.StartBringIntoView /
//  BringIntoViewOptions), rebuilt against the Scroll v3 kernel seam.
//
//  Every WRITE goes through ONE post: `scene.ScrollPort!.Post(ScrollInput.ScrollTo(...))`. This type never writes
//  ScrollState.Offset*, never writes the content LocalTransform, and never arms anything — the kernel (Tick/Reclamp)
//  owns motion end to end (single-writer). `animate:false` posts `immediate:true` (a snap this frame, resolved on
//  the kernel's next Reclamp); `animate:true` posts a glide with `halflifeMs:0`, which the kernel derives from the
//  travel distance itself (ScrollFeel.Shipping.ProgrammaticHalflifeS) — the caller no longer latches a half-life.
//
//  NOT modelled here (deliberately): velocity-continuous re-targeting of an already-running programmatic chase with
//  bespoke spring constants. LyricsView's follow-scroll needs that (a re-target mid-chase must keep the carried
//  velocity or the list visibly trails the song) — the kernel itself provides the continuity (retargeting a live
//  Driven chase for the same node keeps its carried velocity, `plan §2.2` "Driven"), and the explicit-glide overload
//  below lets a caller pin the exact ζ/ω/half-life/settle-velocity LyricsView always used instead of the
//  distance-derived default.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Programmatic bring-into-view over the engine's scroll seam. Scrolls along the viewport's own
/// <see cref="ScrollState.Orientation"/> only — a viewport scrolls one axis.</summary>
public static class ScrollIntoView
{
    /// <summary>Bring <paramref name="node"/> into view inside its NEAREST scrolling ancestor.
    /// <paramref name="margin"/> is the gutter kept between the node and the viewport edge it lands against.
    /// <paramref name="alignmentRatio"/> NaN (default) = MINIMAL scroll: do nothing when the node is already visible;
    /// otherwise 0 parks its leading edge at the viewport's leading edge and 1 its trailing edge at the trailing edge
    /// (the WinUI <c>BringIntoViewOptions</c> ratios). <paramref name="animate"/> posts a glide instead of a snap.
    /// Returns true when a scroll was posted.</summary>
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
    /// orientation, clamped to <c>[0, content − viewport]</c> (zoom-scaled). Split out for callers that compute the
    /// destination from a layout MODEL rather than a realized node — a virtualized list scrolling to an index that is
    /// not realized yet has no node to hand <see cref="Bring"/>. Posts <see cref="ScrollInput.ScrollTo"/> through the
    /// scene's <see cref="SceneStore.ScrollPort"/> — the kernel resolves the clamp, the motion and the write.
    /// Returns true when a scroll was posted (the target differs from the live offset by ≥ 0.5 DIP).</summary>
    public static bool ScrollTo(RenderContext ctx, NodeHandle viewport, float target, bool animate)
    {
        var scene = ctx.Scene;
        if (scene is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return false;
        if (!TryClampAgainstLive(scene, viewport, target, out float clamped)) return false;

        scene.ScrollPort!.Post(ScrollInput.ScrollTo((int)viewport.Raw.Index, clamped, immediate: !animate, halflifeMs: 0));
        // WAKE, don't re-render: the kernel resolves the offset/transform/VirtualRangeDirty on its own, so the tree the
        // calling component would re-render is byte-identical — RequestFrame is exactly this escape hatch's contract
        // (falls back to RequestRerender when a caller hasn't wired it).
        (ctx.RequestFrame ?? ctx.RequestRerender)();
        return true;
    }

    /// <summary>The explicit-glide overload: same clamp/near-miss contract as <see cref="ScrollTo(RenderContext,NodeHandle,float,bool)"/>,
    /// but pins the kernel's Driven-chase constants instead of letting it derive a half-life from the travel distance.
    /// This is the seam LyricsView's follow-scroll (velocity-continuous re-target, bespoke ζ/ω/settle-velocity) uses —
    /// posting a <see cref="ScrollInput.ScrollTo"/> for a node that is ALREADY mid-Driven-chase keeps the kernel's
    /// carried velocity (the kernel retargets in place; `plan §2.2` "Driven"), so this is not a snap-then-restart.</summary>
    public static bool ScrollTo(RenderContext ctx, NodeHandle viewport, float target,
                                float halflifeMs, float zeta, float omega, float settleVel)
    {
        var scene = ctx.Scene;
        if (scene is null || viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return false;
        if (!TryClampAgainstLive(scene, viewport, target, out float clamped)) return false;

        scene.ScrollPort!.Post(ScrollInput.ScrollTo((int)viewport.Raw.Index, clamped, immediate: false,
            halflifeMs: halflifeMs, zeta: zeta, omega: omega, settleVel: settleVel));
        // WAKE, don't re-render — see the note on the other overload. This one matters MORE here: a per-line-emphasis
        // caller re-targeting a live chase every frame (LyricsView's follow-scroll) cannot afford a full component
        // re-render on every call — that is precisely the "every line flashes active for a frame" regression the
        // caller's own RequestRerender-free hand-rolled version existed to avoid.
        (ctx.RequestFrame ?? ctx.RequestRerender)();
        return true;
    }

    // Zoom-scaled max, the same clamp contract the kernel's own Reclamp uses — the unscaled `content − viewport` this
    // used to be silently disagreed with every wheel/fling clamp on a zoomed viewport. `false` = already at target
    // (within 0.5 DIP), the minimal-scroll no-op.
    static bool TryClampAgainstLive(SceneStore scene, NodeHandle viewport, float target, out float clamped)
    {
        ref ScrollState sc = ref scene.ScrollRef(viewport);
        bool horizontal = sc.Orientation != 0;
        float viewportExtent = horizontal ? sc.ViewportW : sc.ViewportH;
        float contentExtent = horizontal ? sc.ContentW : sc.ContentH;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;

        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float maxOffset = MathF.Max(0f, contentExtent * z - viewportExtent);
        clamped = Math.Clamp(target, 0f, maxOffset);
        return MathF.Abs(clamped - offset) >= 0.5f;
    }
}
