using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Reconciler;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  AnimBake — the authoring→engine glue (the rework's declarative bake; design §5.5).
//
//  Maps an Element's declarative motion fields (Enter/Exit/WhileHover/WhilePressed/WhileFocus/Transition/Stagger) onto
//  AnimScheduler seeds. Lives in the Reconciler layer (which already references both Dsl and Animation) so the
//  Dsl→Animation field reference stays one-way (no namespace cycle). The reconciler/host call these at the switch-over:
//  Enter on mount (logical-identity-keyed so a re-skin doesn't replay it), Exit on orphan (deferred via DetachedAnimSlab),
//  and Hover/Pressed/Focus on the interaction-state edge (the priority resolver decides which target is active).
//  Additive + INERT until the reconciler/host wire the calls (build-gated). Stagger is baked here from the child index.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class AnimBake
{
    /// <summary>Seed a mounted node's entrance from its declarative Enter terminal + Transition token. <paramref name="childIndex"/>
    /// bakes the per-child stagger delay (index * Stagger seconds) — no runtime closure, no O(n²) sort.</summary>
    public static void Enter(AnimEngine sched, NodeHandle node, Element el, int childIndex = 0)
    {
        if (el.Enter is { } e)
        {
            MotionTokenDef m = el.Transition ?? MotionTok.StandardEnter;
            float delayMs = el.Stagger > 0f ? childIndex * el.Stagger * 1000f : 0f;
            sched.SeedEnter(node, e, in m, delayMs);
        }
    }

    /// <summary>Seed an orphaned node's exit from its declarative Exit terminal (its structural removal is deferred by the
    /// Presence completion gate until the exit settles).</summary>
    public static void Exit(AnimEngine sched, NodeHandle node, Element el)
    {
        if (el.Exit is { } e)
        {
            MotionTokenDef m = el.Transition ?? MotionTok.StandardExit;
            sched.SeedExit(node, e, in m);
        }
    }

    /// <summary>Apply / release the hover target on the interaction-state edge.</summary>
    public static void Hover(AnimEngine sched, NodeHandle node, Element el, bool on)
    {
        if (el.WhileHover is { } t)
        {
            MotionTokenDef m = el.Transition ?? MotionTok.ControlFaster;
            sched.SeedTarget(node, on ? t : new MotionTarget(), in m);   // rest = ctor defaults (Scale 1, Opacity 1), NOT default()
        }
    }

    /// <summary>Apply / release the pressed target on the interaction-state edge.</summary>
    public static void Pressed(AnimEngine sched, NodeHandle node, Element el, bool on)
    {
        if (el.WhilePressed is { } t)
        {
            MotionTokenDef m = el.Transition ?? MotionTok.ControlFaster;
            sched.SeedTarget(node, on ? t : new MotionTarget(), in m);   // rest = ctor defaults (Scale 1, Opacity 1), NOT default()
        }
    }

    /// <summary>Apply / release the focus target on the interaction-state edge.</summary>
    public static void Focus(AnimEngine sched, NodeHandle node, Element el, bool on)
    {
        if (el.WhileFocus is { } t)
        {
            MotionTokenDef m = el.Transition ?? MotionTok.ControlFaster;
            sched.SeedTarget(node, on ? t : new MotionTarget(), in m);   // rest = ctor defaults (Scale 1, Opacity 1), NOT default()
        }
    }
}
