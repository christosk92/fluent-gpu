using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

/// <summary>
/// The Expressive Motion Kit — a library of named, tuned transition recipes adopted from transitions.dev, expressed on
/// FluentGpu's existing animation engine (the spring/eased tracks, the geometry channels, the new <c>AnimChannel.BlurSigma</c>
/// self-blur, and the <see cref="Expressive"/> vocabulary + <see cref="Easing.SmoothOut"/>/<see cref="Easing.Overshoot"/>
/// curves). This is an OPT-IN app-author palette: framework controls keep their WinUI/Fluent-2 curves for 1:1 parity —
/// reach for these for app surfaces and emphasis moments, not to restyle the control kit.
///
/// Two flavours:
/// <list type="bullet">
/// <item><b>Imperative</b> (the primary): extension methods on <see cref="AnimEngine"/> that seed tracks on a CAPTURED
/// node — call them from an event handler or a <c>UseLayoutEffect</c> with a node ref (the gallery idiom:
/// <c>Context.Anim.PopIn(dot)</c>). Multi-node recipes (per-digit, per-line, neighbour falloff) take a span of nodes.</item>
/// <item><b>Declarative</b>: a few <see cref="LayoutTransition"/> presets for <c>BoxEl.Animate</c>, and a couple of
/// own-node <see cref="Component"/> hooks for the common mount cases.</item>
/// </list>
/// Every recipe honours <see cref="Motion.ReducedMotion"/> (it no-ops, leaving the element at its resting/end state).
/// All ride phases 6–13 with zero per-frame managed allocation; the blur recipes go through the pooled self-blur layer.
/// </summary>
public static class MotionRecipes
{
    // ── Imperative seeders (AnimEngine extensions) ──────────────────────────────────────────────────────────────────

    /// <summary>transitions.dev "number pop-in" / element pop-in: the element re-enters from a direction with a blurred
    /// slide. Translate(<paramref name="distance"/>·dir) + Opacity 0→1 + Blur→0, the <see cref="Easing.Pop"/> curve.
    /// Default direction is straight up (dirY=1). Seed it per-character (see <see cref="PopInStaggered"/>) for the
    /// classic counter/price effect.</summary>
    public static void PopIn(this AnimEngine anim, NodeHandle node, float dirX = 0f, float dirY = 1f,
        float distance = Expressive.DistBase, float blur = Expressive.BlurSmall, float durationMs = Expressive.VerySlow, float delayMs = 0f)
    {
        if (Motion.ReducedMotion) return;
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, durationMs, Easing.SmoothOut, delayMs: delayMs);
        if (dirX != 0f) anim.Animate(node, AnimChannel.TranslateX, dirX * distance, 0f, durationMs, Easing.Pop, delayMs: delayMs);
        if (dirY != 0f) anim.Animate(node, AnimChannel.TranslateY, dirY * distance, 0f, durationMs, Easing.Pop, delayMs: delayMs);
        if (blur > 0f) anim.Animate(node, AnimChannel.BlurSigma, blur, 0f, durationMs, Easing.SmoothOut, delayMs: delayMs);
    }

    /// <summary>Staggered <see cref="PopIn"/> across a row of nodes (the transitions.dev number pop-in / texts reveal):
    /// node i rides in <paramref name="staggerMs"/>·i behind the first. Stagger is an engine primitive (the track's
    /// delay), not a per-frame timer.</summary>
    public static void PopInStaggered(this AnimEngine anim, ReadOnlySpan<NodeHandle> nodes, float dirX = 0f, float dirY = 1f,
        float distance = Expressive.DistBase, float blur = Expressive.BlurSmall, float durationMs = Expressive.VerySlow, float staggerMs = 70f)
    {
        for (int i = 0; i < nodes.Length; i++)
            if (!nodes[i].IsNull) anim.PopIn(nodes[i], dirX, dirY, distance, blur, durationMs, delayMs: i * staggerMs);
    }

    /// <summary>transitions.dev "texts reveal" / soft panel reveal: a blurred rise — TranslateY <paramref name="dy"/>→0 +
    /// Opacity 0→1 + Blur→0 over <see cref="Easing.SmoothOut"/>. The blur makes a short travel read as a full reveal.</summary>
    public static void SoftReveal(this AnimEngine anim, NodeHandle node, float dy = Expressive.DistMedium,
        float blur = Expressive.BlurMedium, float durationMs = Expressive.VerySlow, float delayMs = 0f)
    {
        if (Motion.ReducedMotion) return;
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, durationMs, Easing.SmoothOut, delayMs: delayMs);
        anim.Animate(node, AnimChannel.TranslateY, dy, 0f, durationMs, Easing.SmoothOut, delayMs: delayMs);
        if (blur > 0f) anim.Animate(node, AnimChannel.BlurSigma, blur, 0f, durationMs, Easing.SmoothOut, delayMs: delayMs);
    }

    /// <summary>Staggered <see cref="SoftReveal"/> across stacked lines (the transitions.dev "texts reveal" — stagger 40ms).</summary>
    public static void SoftRevealStaggered(this AnimEngine anim, ReadOnlySpan<NodeHandle> nodes, float dy = Expressive.DistMedium,
        float blur = Expressive.BlurMedium, float durationMs = Expressive.VerySlow, float staggerMs = Expressive.Stagger)
    {
        for (int i = 0; i < nodes.Length; i++)
            if (!nodes[i].IsNull) anim.SoftReveal(nodes[i], dy, blur, durationMs, delayMs: i * staggerMs);
    }

    /// <summary>transitions.dev "error state shake": a percussive left/right shake with overshoot — a multi-segment
    /// TranslateX keyframe path with the <see cref="Easing.SmoothOut"/> curve per leg (stops at 0/28.57/57.14/78.57/100%,
    /// the A,A,B,B leg pattern = 80+80+60+60ms). Trigger from a validation-failure handler; replays each call.</summary>
    public static void Shake(this AnimEngine anim, NodeHandle node, float distance = Expressive.DistSmall,
        float overshoot = Expressive.DistMicro, float durationMs = 280f)
    {
        if (Motion.ReducedMotion) return;
        anim.Keyframes(node, AnimChannel.TranslateX,
        [
            new Keyframe(0f, 0f),
            new Keyframe(0.2857f, distance, Easing.SmoothOut),
            new Keyframe(0.5714f, -distance, Easing.SmoothOut),
            new Keyframe(0.7857f, overshoot, Easing.SmoothOut),
            new Keyframe(1f, 0f, Easing.SmoothOut),
        ], durationMs);
    }

    /// <summary>transitions.dev "icon swap" (the incoming half): the new icon grows from a small scale with a blurred
    /// cross-fade — Scale 0.25→1 + Opacity 0→1 + Blur→0, ease-in-out. Pair with an exit on the outgoing icon, or just
    /// swap the glyph and play this on the replacement.</summary>
    public static void IconSwapIn(this AnimEngine anim, NodeHandle node, float startScale = 0.25f, float durationMs = Expressive.Fast)
    {
        if (Motion.ReducedMotion) return;
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, durationMs, Easing.EaseInOut);
        anim.Animate(node, AnimChannel.ScaleX, startScale, 1f, durationMs, Easing.EaseInOut);
        anim.Animate(node, AnimChannel.ScaleY, startScale, 1f, durationMs, Easing.EaseInOut);
        anim.Animate(node, AnimChannel.BlurSigma, Expressive.BlurSmall, 0f, durationMs, Easing.EaseInOut);
    }

    /// <summary>transitions.dev "notification badge": the dot slides onto the trigger and pops with an overshoot spring,
    /// fading + un-blurring as it arrives. The pop uses a low-damping spring (the <see cref="Easing.Overshoot"/> feel as
    /// physics) so a re-trigger mid-flight stays velocity-continuous.</summary>
    public static void BadgePop(this AnimEngine anim, NodeHandle node, float offsetX = -Expressive.DistBase, float offsetY = Expressive.DistMedium)
    {
        if (Motion.ReducedMotion) return;
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, Expressive.Slow, Easing.SmoothOut);
        anim.Animate(node, AnimChannel.TranslateX, offsetX, 0f, 260f, Easing.SmoothOut);
        anim.Animate(node, AnimChannel.TranslateY, offsetY, 0f, 260f, Easing.SmoothOut);
        var pop = SpringParams.FromResponse(0.30f, 0.55f);   // low damping → overshoot pop
        anim.Spring(node, AnimChannel.ScaleX, 1f, pop, initial: 0.3f);
        anim.Spring(node, AnimChannel.ScaleY, 1f, pop, initial: 0.3f);
        anim.Animate(node, AnimChannel.BlurSigma, Expressive.BlurSmall, 0f, Expressive.Slow, Easing.SmoothOut);
    }

    /// <summary>transitions.dev "success check" (the container celebration): fade in + un-rotate from 80° + a Y-bob that
    /// settles with overshoot + un-blur from a large radius. Pair with a stroke-draw on the checkmark path itself — seed
    /// <c>AnimChannel.StrokeTrimEnd</c> 0→1 on a <c>PolylineStrokeEl</c> child (the engine's existing draw-on channel,
    /// the same mechanism as the CheckBox glyph) for the line that draws itself on.</summary>
    public static void SuccessCheck(this AnimEngine anim, NodeHandle node, float durationMs = Expressive.VerySlow)
    {
        if (Motion.ReducedMotion) return;
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, durationMs, Easing.SmoothOut);
        anim.Animate(node, AnimChannel.Rotation, 80f, 0f, durationMs, Easing.SmoothOut);
        anim.Keyframes(node, AnimChannel.TranslateY,
            [new Keyframe(0f, Expressive.DistLarge + 10f), new Keyframe(1f, 0f, Easing.Overshoot)], durationMs);
        anim.Animate(node, AnimChannel.BlurSigma, Expressive.BlurLarge, 0f, durationMs, Easing.SmoothOut);
    }

    /// <summary>transitions.dev "avatar group hover": lift the hovered item and its neighbours with an exponential
    /// distance falloff (<c>lift · falloff^|i−active|</c>), each on a spring. On <paramref name="hovered"/>=false the
    /// row springs back with a LOW-damping spring (the bouncy <see cref="Easing.OvershootStrong"/> return). Useful for
    /// avatar stacks, chip rows, tag pills, emoji reactions.</summary>
    public static void NeighborLift(this AnimEngine anim, ReadOnlySpan<NodeHandle> nodes, int activeIndex,
        bool hovered, float lift = -4f, float falloff = 0.45f)
    {
        // Bouncy on release, clean on enter (transitions.dev sets the timing-function inline before the write).
        var spring = SpringParams.FromResponse(0.32f, hovered ? 0.85f : 0.5f);
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].IsNull) continue;
            float shift = hovered ? lift * MathF.Pow(falloff, MathF.Abs(i - activeIndex)) : 0f;
            anim.Spring(nodes[i], AnimChannel.TranslateY, Motion.ReducedMotion ? 0f : shift, spring);
        }
    }

    /// <summary>transitions.dev "skeleton loader" pulse: a gentle looping opacity breathe (1 → <paramref name="min"/> →
    /// 1) on a skeleton placeholder. Place it on the bars/avatar; cross-fade to the real content with a blurred
    /// <see cref="SoftReveal"/> (opacity + Blur) when data arrives — the two layers share the slot, so the swap is
    /// layout-free. Loops until cancelled (<see cref="AnimEngine.Cancel"/>).</summary>
    public static void SkeletonPulse(this AnimEngine anim, NodeHandle node, float min = 0.5f, float durationMs = 1000f)
    {
        if (Motion.ReducedMotion) return;
        anim.Keyframes(node, AnimChannel.Opacity,
            [new Keyframe(0f, 1f), new Keyframe(0.5f, min, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)],
            durationMs, loop: true);
    }

    /// <summary>transitions.dev "menu dropdown" / "modal" / "tooltip" open (the incoming half): scale from
    /// <paramref name="fromScale"/>→1 + Opacity 0→1, GROWING FROM the trigger anchor (set the node's
    /// <c>TransformOriginX/Y</c> so it unfolds from the right edge of the screen-space anchor — e.g. originY=0 for a
    /// dropdown that opens downward). Uses the expressive <see cref="Easing.SmoothOut"/> for an app surface; framework
    /// flyouts keep their Fluent curve.</summary>
    public static void ScaleOpen(this AnimEngine anim, NodeHandle node, float fromScale = Expressive.ScaleMedium, float durationMs = Expressive.Fast)
    {
        if (Motion.ReducedMotion) return;
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, durationMs, Easing.SmoothOut);
        anim.Animate(node, AnimChannel.ScaleX, fromScale, 1f, durationMs, Easing.SmoothOut);
        anim.Animate(node, AnimChannel.ScaleY, fromScale, 1f, durationMs, Easing.SmoothOut);
    }

    // ── Declarative LayoutTransition presets (BoxEl.Animate) ────────────────────────────────────────────────────────

    /// <summary>transitions.dev "page side-by-side": slide between two pages (list ↔ detail, step 1 ↔ step 2) with a
    /// blurred cross-fade. Inserted page enters from +X, removed page exits to −X; opacity cross-fades. (Blur on the
    /// page nodes themselves via <see cref="SoftReveal"/> if you want the cross-blur too.)</summary>
    public static LayoutTransition PageSlide => PageSlideForward;

    // NB: no page-root Blur on Enter/Exit. A BlurSigma on a page root makes the WHOLE page a blur group — a
    // canvas-sized offscreen RT + a 2-pass Gaussian every frame of the transition (measured ~13ms vs ~7ms GPU submit
    // when this was last removed). The Dx slide + opacity cross-fade carry the motion; per-element swaps (TextSwap /
    // IconSwap) keep their tiny blur.
    public static LayoutTransition PageSlideForward => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: Expressive.DistBase, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: -Expressive.DistBase, Opacity: 0f, Active: true));

    public static LayoutTransition PageSlideBack => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: -Expressive.DistBase, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: Expressive.DistBase, Opacity: 0f, Active: true));

    public static LayoutTransition PageFade => new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>transitions.dev text-state swap: old text rises and blurs; replacement enters from below.</summary>
    public static LayoutTransition TextSwap => new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(150f, Easing.EaseInOut),
        Enter: new EnterExit(Dy: 4f, Opacity: 0f, Active: true, Blur: 2f),
        Exit: new EnterExit(Dy: -4f, Opacity: 0f, Active: true, Blur: 2f));

    public static LayoutTransition IconSwap => new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.EaseInOut),
        Enter: new EnterExit(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall),
        Exit: new EnterExit(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall));

    /// <summary>transitions.dev "card resize": smoothly tween a container's size change through real layout (neighbours
    /// reflow) on the expressive <see cref="Easing.SmoothOut"/> curve, 300ms.</summary>
    public static LayoutTransition CardResize => new(
        TransitionChannels.Size, TransitionDynamics.Tween(300f, Easing.SmoothOut), Size: SizeMode.Reflow);

    public static LayoutTransition CardResizeHeight => new(
        TransitionChannels.Size, TransitionDynamics.Tween(300f, Easing.SmoothOut), Size: SizeMode.Reflow,
        Axes: SizeAxes.Height);

    public static LayoutTransition CardResizeWidth => new(
        TransitionChannels.Size, TransitionDynamics.Tween(300f, Easing.SmoothOut), Size: SizeMode.Reflow,
        Axes: SizeAxes.Width);

    /// <summary>Responsive grid/card refit. The parent owns final bounds; children keep identity and project into them.
    /// Size is COMPOSITOR-ONLY (<see cref="SizeMode.ScaleCorrect"/>): the cell GPU-scales from its old extent to 1 over
    /// the brief flight — it does NOT re-solve its subtree per tick, so content stretches slightly in-flight rather than
    /// re-wrapping (the deliberate trade for no per-cell Measure+Arrange every frame during a window/panel resize).</summary>
    public static LayoutTransition CardRefit => new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(300f, Easing.SmoothOut), Size: SizeMode.ScaleCorrect);

    /// <summary>transitions.dev "panel reveal": slide a panel into a region (translate + opacity) with the slower open /
    /// quicker close asymmetry (open 400ms, close 350ms). Add Blur on the panel node for the cross-blur.</summary>
    public static LayoutTransition PanelReveal => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Slow, Easing.SmoothOut),
        Enter: new EnterExit(Dy: Expressive.DistLarge, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: Expressive.DistLarge, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Expressive.Medium, Easing.SmoothOut));

    // ── Own-node Component hooks (mount-seeded sugar) ───────────────────────────────────────────────────────────────

    /// <summary>Own-node <see cref="SoftReveal"/>: seed the blurred rise on THIS component's rendered root at mount (and
    /// re-seed when <paramref name="key"/> changes). The component-sugar counterpart to <see cref="MotionHooks.UseEntrance"/>
    /// but with the expressive curve + blur.</summary>
    public static void UseSoftReveal(this Component c, DepKey key = default, float dy = Expressive.DistMedium, float blur = Expressive.BlurMedium)
    {
        // Reduced-motion is read as a VALUE — never early-return. Motion.ReducedMotion is a mutable global (a drag-resize
        // grip flips it to suppress springs), so an early-return would change THIS hook's slot count mid-life and shift
        // every later hook in the calling component → an EffectCell↔ResourceCell cast crash. When reduced, the
        // transitions are seeded already at their end state (instant, no visible motion) so the hook order stays invariant.
        bool reduce = Motion.ReducedMotion;
        DepKey dep = key;   // default = seed once at mount
        c.Context.UseTransition(AnimChannel.Opacity, reduce ? 1f : 0f, 1f, Expressive.VerySlow, Easing.SmoothOut, dep);
        c.Context.UseTransition(AnimChannel.TranslateY, reduce ? 0f : dy, 0f, Expressive.VerySlow, Easing.SmoothOut, dep);
        if (blur > 0f) c.Context.UseTransition(AnimChannel.BlurSigma, reduce ? 0f : blur, 0f, Expressive.VerySlow, Easing.SmoothOut, dep);
    }
}
