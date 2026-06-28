using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  MotionTok — the named-motion vocabulary (the rework's one token registry; design §3.9/§5.10).
//
//  Unifies the three colliding namespaces (`Dsl.Motion` Fast=150 / `Dsl.Expressive` Fast=250 / `Animation.MotionSprings`)
//  into one table of named recipes, each carrying its dynamics AND its reduced-motion policy — so motion is coherent,
//  themeable, and reduced-motion-expressible centrally instead of hand-typed per call site. A control configures
//  `Transition = MotionTok.ControlFaster` (the ~83ms WinUI BrushTransition) rather than a bespoke BrushTransitionMs.
//
//  This is the DEFAULT (non-themed) table. The per-theme `FrozenDictionary<MotionTokenId, MotionTokenDef>` that rides
//  theming's Tok.* machinery (a theme variant can ship all-SnapEnd/KeepFade tokens = reduced-motion-as-a-theme) is the
//  theming integration step (TODO, build loop). Reuses the existing IntegrationMode (Eased|Spring) + SpringParams.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What reduced-motion does to a token's motion (read by Tick/seed, never by authoring code). KeepFade keeps
/// opacity cross-fades (they aid orientation, are not "motion"); SnapEnd jumps transforms to the end; Exempt always runs
/// (essential motion, e.g. a loading spinner).</summary>
public enum ReducedMotionPolicy : byte { SnapEnd, KeepFade, Exempt }

/// <summary>The named motion tokens. Source-gen'd id family in the full design (reusing theming's Tok.* generator);
/// here a hand-authored enum for the default table.</summary>
public enum MotionTokenId : ushort
{
    // Control interaction (the WinUI durations — ControlFaster is the 83ms BrushTransition / hover-press cross-fade)
    ControlFaster, ControlFast, ControlNormal,
    // Structural enter/exit
    StandardEnter, StandardExit, EmphasizedEnter, EmphasizedExit,
    // Springs
    StandardSpring, ExpressiveSpring,
    // Feature motions
    ConnectedFly, ContentResize, ItemPlacement, ScrollFade,
}

/// <summary>A resolved motion recipe: dynamics (eased OR spring) + the reduced-motion policy. 24B-ish POD.</summary>
public readonly struct MotionTokenDef : System.IEquatable<MotionTokenDef>
{
    public readonly IntegrationMode Mode;
    public readonly Easing Easing;          // Mode == Eased
    public readonly float DurationMs;       // Mode == Eased
    public readonly SpringParams Spring;    // Mode == Spring
    public readonly ReducedMotionPolicy Reduced;

    private MotionTokenDef(IntegrationMode mode, Easing easing, float durationMs, in SpringParams spring, ReducedMotionPolicy reduced)
    {
        Mode = mode; Easing = easing; DurationMs = durationMs; Spring = spring; Reduced = reduced;
    }

    public static MotionTokenDef Eased(float durationMs, Easing easing, ReducedMotionPolicy reduced = ReducedMotionPolicy.SnapEnd)
        => new(IntegrationMode.Eased, easing, durationMs, default, reduced);

    public static MotionTokenDef SpringOf(in SpringParams spring, ReducedMotionPolicy reduced = ReducedMotionPolicy.SnapEnd)
        => new(IntegrationMode.Spring, Easing.Linear, 0f, spring, reduced);

    /// <summary>Convert to a Foundation <see cref="TransitionDynamics"/> (so a declarative Element field can synthesize a
    /// LayoutTransition routed through the existing seed lifecycle). Recovers (response, dampingRatio) from the baked
    /// SpringParams (the inverse of <c>SpringParams.FromResponse</c>).</summary>
    public TransitionDynamics ToDynamics()
    {
        if (Mode == IntegrationMode.Spring)
        {
            float m = Spring.Mass <= 0f ? 1f : Spring.Mass;
            float w = System.MathF.Sqrt(System.MathF.Max(Spring.Stiffness / m, 1e-6f));
            float response = (2f * System.MathF.PI) / w;
            float damping = w <= 1e-6f ? 1f : Spring.Damping / (2f * m * w);
            return TransitionDynamics.Spring(response, damping);
        }
        return TransitionDynamics.Tween(DurationMs, Easing);
    }

    // IEquatable so the base Element.Transition (Prop-adjacent: a `MotionTokenDef?` on EVERY node) diffs through the
    // no-box GenericEqualityComparer/NullableEqualityComparer path, not ObjectEqualityComparer's boxing + reflection
    // ValueType.Equals (which would also recurse-box the nested SpringParams). Field-wise, matching ValueType.Equals.
    public bool Equals(MotionTokenDef other)
        => Mode == other.Mode && Easing == other.Easing && DurationMs == other.DurationMs
        && Spring.Equals(other.Spring) && Reduced == other.Reduced;
    public override bool Equals(object? obj) => obj is MotionTokenDef o && Equals(o);
    public override int GetHashCode() => System.HashCode.Combine((int)Mode, (int)Easing, DurationMs, Spring, (int)Reduced);
}

/// <summary>A gesture-state target set (Framer <c>whileHover</c>/<c>whileTap</c>): the channel values a node animates
/// TO while a state (hover/press/focus) is active, and back FROM on release — resolved by the InteractionState
/// priority machine (higher-priority state wins; releasing it animates to the next writer's value). Defaults are
/// identity (Scale 1, Opacity 1, no offset/blur), so <c>new() { Scale = 1.04f }</c> lifts on hover and rests at 1.</summary>
public readonly record struct MotionTarget
{
    public float Scale { get; init; }
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float Opacity { get; init; }
    public float Blur { get; init; }
    public MotionTarget() { Scale = 1f; Opacity = 1f; }
}

/// <summary>The default motion-token table + convenience accessors (mirrors theming's <c>Tok.*</c> surface).</summary>
public static class MotionTok
{
    public static MotionTokenDef Get(MotionTokenId id) => id switch
    {
        // Control interaction — fades keep their cross-fade under reduced motion.
        MotionTokenId.ControlFaster => MotionTokenDef.Eased(83f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
        MotionTokenId.ControlFast => MotionTokenDef.Eased(150f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
        MotionTokenId.ControlNormal => MotionTokenDef.Eased(250f, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
        // Structural enter/exit — Fluent decelerate in, accelerate out.
        MotionTokenId.StandardEnter => MotionTokenDef.Eased(300f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
        MotionTokenId.StandardExit => MotionTokenDef.Eased(200f, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade),
        MotionTokenId.EmphasizedEnter => MotionTokenDef.Eased(500f, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
        MotionTokenId.EmphasizedExit => MotionTokenDef.Eased(350f, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade),
        // Springs — Standard is critically-ish damped; Expressive is bouncier.
        MotionTokenId.StandardSpring => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.35f, 0.85f)),
        MotionTokenId.ExpressiveSpring => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.50f, 0.60f)),
        // Feature motions — ConnectedFly is critically damped (the user prefers a smooth, no-overshoot hero fly).
        MotionTokenId.ConnectedFly => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.45f, 1.0f)),
        MotionTokenId.ContentResize => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.40f, 0.90f)),
        MotionTokenId.ItemPlacement => MotionTokenDef.SpringOf(SpringParams.FromResponse(0.40f, 0.85f)),
        MotionTokenId.ScrollFade => MotionTokenDef.Eased(150f, Easing.Linear, ReducedMotionPolicy.KeepFade),
        _ => MotionTokenDef.SpringOf(SpringParams.Default),
    };

    public static MotionTokenDef ControlFaster => Get(MotionTokenId.ControlFaster);
    public static MotionTokenDef ControlFast => Get(MotionTokenId.ControlFast);
    public static MotionTokenDef ControlNormal => Get(MotionTokenId.ControlNormal);
    public static MotionTokenDef StandardEnter => Get(MotionTokenId.StandardEnter);
    public static MotionTokenDef StandardExit => Get(MotionTokenId.StandardExit);
    public static MotionTokenDef EmphasizedEnter => Get(MotionTokenId.EmphasizedEnter);
    public static MotionTokenDef EmphasizedExit => Get(MotionTokenId.EmphasizedExit);
    public static MotionTokenDef StandardSpring => Get(MotionTokenId.StandardSpring);
    public static MotionTokenDef ExpressiveSpring => Get(MotionTokenId.ExpressiveSpring);
    public static MotionTokenDef ConnectedFly => Get(MotionTokenId.ConnectedFly);
    public static MotionTokenDef ContentResize => Get(MotionTokenId.ContentResize);
    public static MotionTokenDef ItemPlacement => Get(MotionTokenId.ItemPlacement);
    public static MotionTokenDef ScrollFade => Get(MotionTokenId.ScrollFade);
}
