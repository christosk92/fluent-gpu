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
    DisclosureExpand, DisclosureCollapse, DisclosureChevron,
    // Media transport chrome — deliberately ASYMMETRIC (reveal must feel instant, conceal must not blink out).
    MediaChromeReveal, MediaChromeConceal,
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

    /// <summary>This token's duration with REDUCED MOTION already resolved as a VALUE (design §5.10) — 0 when the
    /// policy says snap for <paramref name="channel"/>, the authored duration otherwise. Call sites that seed a channel
    /// by hand (<c>AnimEngine.SeedEased</c>, which takes a raw duration and therefore cannot consult the token) use this
    /// instead of branching on <c>Motion.ReducedMotion</c> — a branch in authoring code is a hook-order hazard AND
    /// re-litigates the policy per site. Mirrors <c>AnimScheduler.ReducedSnap</c> exactly: <see cref="ReducedMotionPolicy.Exempt"/>
    /// always runs, <see cref="ReducedMotionPolicy.KeepFade"/> keeps an opacity cross-fade, everything else snaps.</summary>
    public float EffectiveDurationMs(AnimChannel channel)
        => Mode == IntegrationMode.Spring ? 0f
         : FluentGpu.Dsl.Motion.ReducedMotion
           && Reduced != ReducedMotionPolicy.Exempt
           && !(Reduced == ReducedMotionPolicy.KeepFade && channel == AnimChannel.Opacity)
           ? 0f : DurationMs;

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
/// priority machine (higher-priority state wins; releasing it animates to the next writer's value).
/// <para><b>Rest-pose-RELATIVE contract:</b> every field here is a DELTA on the node's AUTHORED rest pose, never an
/// absolute value — <see cref="OffsetX"/>/<see cref="OffsetY"/>/<see cref="Rotation"/>/<see cref="Blur"/> ADD to the
/// rest pose's offset/rotation/blur; <see cref="Scale"/>/<see cref="Opacity"/> MULTIPLY it. Releasing every state
/// (no Hover/Press/Focus active) animates back to the authored rest pose, not to identity — see
/// <c>AnimEngine.SeedTargetOver</c>, the fold that carries the rest pose through. Defaults are the identity DELTA
/// (Scale 1, Opacity 1, no offset/rotation/blur), so <c>new() { Scale = 1.04f }</c> lifts a node 4% off its own rest
/// scale on hover and rests back at its own rest scale (not literal 1) on release.</para>
/// <para><b>Gotcha:</b> <c>default(MotionTarget)</c> bypasses this struct's parameterless ctor and yields
/// <c>Scale = 0, Opacity = 0</c> (a node that scales to nothing and vanishes) — every "identity delta" use MUST be
/// <c>new MotionTarget()</c>, never <c>default</c>.</para></summary>
public readonly record struct MotionTarget
{
    public float Scale { get; init; }
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    /// <summary>Degrees — a DELTA on the node's authored rest <see cref="FluentGpu.Dsl.Element"/><c>.Rotation</c>.</summary>
    public float Rotation { get; init; }
    public float Opacity { get; init; }
    public float Blur { get; init; }
    public MotionTarget() { Scale = 1f; Opacity = 1f; }
}

/// <summary>The default motion-token table + convenience accessors (mirrors theming's <c>Tok.*</c> surface).</summary>
public static class MotionTok
{
    // ── Media transport chrome: the auto-hide timing table ───────────────────────────────────────────────────────────
    // Every value here is a SHIPPED-PLAYER value, not a taste call. The previous 1800 ms sat below every player that
    // ships: WinUI MediaTransportControls' ControlPanelDisplayTimeoutInSecs is 3 s, Chromium's media controls 2.5 s,
    // video.js / Plyr 2 s, Android Media3 5 s. A dwell shorter than the shortest shipped one reads as the chrome
    // "running away" from a user who is still deciding.

    /// <summary>Idle dwell before an actively-playing media surface hides its transport chrome — MOUSE input.
    /// WinUI <c>ControlPanelDisplayTimeoutInSecs</c> parity (3 s).</summary>
    public const float MediaChromeIdleDelayMs = 3000f;
    /// <summary>Idle dwell after a TOUCH reveal. Longer than the mouse dwell because a touch user has no hover to keep
    /// the chrome alive and must re-tap to get it back — the same split Media3/YouTube make.</summary>
    public const float MediaChromeIdleDelayTouchMs = 4000f;
    /// <summary>Idle dwell after the chrome was revealed by FOCUS entering it (keyboard/AT reveal). Longer for the same
    /// reason as touch: there is no pointer motion to keep re-arming it while the user reads the controls.</summary>
    public const float MediaChromeIdleDelayAfterFocusMs = 4000f;
    /// <summary>The cursor hides this long AFTER the chrome does, so it does not blink out in the middle of the chrome's
    /// fade (two simultaneous disappearances read as a glitch, not as one idle transition).</summary>
    public const float MediaChromeCursorExtraDelayMs = 400f;
    /// <summary>Squared-distance gate (DIP) a pointer move must cross to count as ACTIVITY while the chrome is HIDDEN.
    /// Zero while the chrome is VISIBLE: once it is up, every move should re-arm the dwell. (Identical coordinates are
    /// de-duplicated and dropped BEFORE this test — the video.js phantom-mousemove fix.)</summary>
    public const float MediaChromeMoveThresholdDip = 3f;
    /// <summary>Chrome conceal duration (ms) — see <see cref="MotionTokenId.MediaChromeConceal"/>.</summary>
    public const float MediaChromeFadeOutMs = 200f;
    /// <summary>Chrome reveal duration (ms) — HALF the conceal, deliberately: a reveal answers a user action and must
    /// feel instant, a conceal happens unattended and must not flicker. See <see cref="MotionTokenId.MediaChromeReveal"/>.</summary>
    public const float MediaChromeFadeInMs = 100f;

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
        MotionTokenId.DisclosureExpand => MotionTokenDef.Eased(333f, Easing.FluentPopOpen),
        MotionTokenId.DisclosureCollapse => MotionTokenDef.Eased(167f, Easing.FluentDisclosureCollapse),
        MotionTokenId.DisclosureChevron => MotionTokenDef.Eased(167f, Easing.FluentDisclosureChevron),
        // Media chrome — SnapEnd (not KeepFade): under reduced motion the transport must appear/disappear instantly.
        // "Chrome that fades" IS the motion here; there is no orientation cue in it worth keeping.
        MotionTokenId.MediaChromeReveal => MotionTokenDef.Eased(MediaChromeFadeInMs, Easing.FluentDecelerate),
        MotionTokenId.MediaChromeConceal => MotionTokenDef.Eased(MediaChromeFadeOutMs, Easing.FluentAccelerate),
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
    public static MotionTokenDef DisclosureExpand => Get(MotionTokenId.DisclosureExpand);
    public static MotionTokenDef DisclosureCollapse => Get(MotionTokenId.DisclosureCollapse);
    public static MotionTokenDef DisclosureChevron => Get(MotionTokenId.DisclosureChevron);
    public static MotionTokenDef MediaChromeReveal => Get(MotionTokenId.MediaChromeReveal);
    public static MotionTokenDef MediaChromeConceal => Get(MotionTokenId.MediaChromeConceal);
}
