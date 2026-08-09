using FluentGpu.Dsl;

namespace Wavee;

// Wavee's ONE motion vocabulary. Hover/press motion everywhere is Wavee's identity — a media app should feel alive
// under the pointer where WinUI is deliberately still — but it is a SYSTEM, not 20 hand-picked numbers. This file owns
// both halves of that system:
//
//   · the three interaction SCALE tiers (below) — every HoverScale/PressScale in the app resolves to one of them;
//   · the Fluent duration ladder — every HoverDurationMs/PressDurationMs/BrushTransitionMs snaps to one of its rungs.
//
// The actual animation is still the engine's: MotionRecipes.*/MotionHooks.* for entrances, MotionTok.* for the
// declarative Transition/While* surface, and the engine's own HoverFade/PressFade slab channels for the scale cues.
//
// ── WHY THE TIERS READ Motion.ReducedMotion AND THE ENGINE DOES NOT ─────────────────────────────────────────────
// Investigated before building this (AnimScheduler.Hover.cs → SeedInteractFade → AnimScheduler.SeedEased): the
// hover/press scale path is the ONLY animated channel in the engine that carries no ReducedMotionPolicy. Structural
// motion goes through SeedMotion/KeyframesMotion, which consult ReducedSnap() and place the channel at its end value;
// the declarative While*/Transition surface carries a MotionTokenDef whose .Reduced policy is honoured the same way.
// But InteractionAnim.HoverT/PressT are seeded by SeedEased(), which has no policy parameter, and SceneRecorder.cs
// (~:772) composites `1 + (HoverScale-1)*HoverT` unconditionally. So a reduced-motion user WOULD still get every
// scale cue, fully eased. The engine is read-only for this wave, and the values are authored app-side anyway, so the
// correct minimal fix is app-side and lives HERE: the tier accessors return 1f under reduced motion, which makes the
// recorder's `MathF.Abs(isc - 1f) > 0.0008f` test fail and skips the transform entirely. This is reduced-motion-
// as-a-VALUE (the canon rule) — a value read during Render, never a hook-order-breaking branch.
// NOT double-suppression: nothing else nulls these. If the engine ever grows a policy on the interaction channels,
// delete the `Motion.ReducedMotion ?` reads here and keep the tiers.
public static class WaveeMotion
{
    // ── Interaction scale tiers ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Chips, list/track rows, small toggles, settings rows, inline pickers — a surface the pointer crosses
    /// often. Barely-there, so a scrolling list doesn't shimmer.</summary>
    public static readonly ScaleTier ScaleSubtle = new(1.02f, 0.98f);

    /// <summary>Buttons, CTAs, pills, secondary circles — a deliberate, discrete target. This is the WaveeCta media
    /// pill's tier (it authored 1.04/0.97 first; the press deepened to the ladder's 0.96).</summary>
    public static readonly ScaleTier ScaleStandard = new(1.04f, 0.96f);

    /// <summary>Media FABs, transport + primary play, on-artwork circles, the "…" corner — a round affordance that
    /// floats over media and is the page's loudest interactive object. Absorbs the old 1.06/1.07/1.10/1.16 and
    /// 0.86/0.90/0.92/0.94 family.</summary>
    public static readonly ScaleTier ScaleEmphatic = new(1.07f, 0.92f);

    // ── Fluent duration ladder (ms) — Common_themeresources_any.xaml ───────────────────────────────────────────
    // Interaction durations only. Structural enter/exit choreography (TransitionDynamics.Tween on a page/pane/flyout)
    // stays authored per surface: those are asymmetric by design (enter decelerates long, exit accelerates short) and
    // are not on this ladder.
    /// <summary>WinUI ControlFasterAnimationDuration — brush cross-fades and press acknowledgement.</summary>
    public const float Faster = 83f;
    /// <summary>WinUI ControlFastAnimationDuration — hover reveals, small state changes.</summary>
    public const float Fast = 167f;
    /// <summary>WinUI ControlNormalAnimationDuration — the workhorse: recolor, icon swap, material cross-fade.</summary>
    public const float Standard = 250f;

    /// <summary>List entrance stagger per row. Not yet wired — Wave 5 (list/shelf entrance choreography) consumes it;
    /// kept so the value is decided in one place when that lands.</summary>
    public const float StaggerMs = 40f;
}

/// <summary>One interaction scale tier: the authored hover/press targets plus the reduced-motion-safe accessors every
/// call site must use. Construct only through the <see cref="WaveeMotion"/> tiers — there is no fourth tier.</summary>
public readonly struct ScaleTier
{
    /// <summary>The authored target, BEFORE the reduced-motion read. For tests/diagnostics; call sites use
    /// <see cref="Hover"/>.</summary>
    public readonly float HoverTarget;
    /// <summary>The authored target, BEFORE the reduced-motion read. For tests/diagnostics; call sites use
    /// <see cref="Press"/>.</summary>
    public readonly float PressTarget;

    internal ScaleTier(float hoverTarget, float pressTarget)
    {
        HoverTarget = hoverTarget;
        PressTarget = pressTarget;
    }

    /// <summary>Assign to <c>BoxEl.HoverScale</c>. Collapses to 1f under reduced motion.</summary>
    public float Hover => Motion.ReducedMotion ? 1f : HoverTarget;

    /// <summary>Assign to <c>BoxEl.PressScale</c>. Collapses to 1f under reduced motion.</summary>
    public float Press => Motion.ReducedMotion ? 1f : PressTarget;

    /// <summary>Hover scale for an affordance that can be dead (a disabled transport button, an unavailable filter):
    /// a surface that cannot be clicked must not answer the pointer.</summary>
    public float HoverIf(bool enabled) => enabled ? Hover : 1f;

    /// <summary>Press scale for an affordance that can be dead. See <see cref="HoverIf"/>.</summary>
    public float PressIf(bool enabled) => enabled ? Press : 1f;
}
