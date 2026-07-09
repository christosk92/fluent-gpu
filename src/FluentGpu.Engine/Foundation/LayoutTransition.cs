namespace FluentGpu.Foundation;

/// <summary>
/// The general layout-transition spec — the entire authoring surface for "animate this node's layout changes".
/// A plain POD (no closures) so it is hot-path safe, and it lives in Foundation (below the AnimEngine, which reads it
/// and converts the dynamics to its internal spring/tween). It attaches to a node via <c>BoxEl.Animate</c> (or
/// <c>RenderContext.UseLayoutAnimation</c>); the host diffs the node's presented rect vs its new laid-out rect each
/// commit and drives the presented geometry toward target through these channels/dynamics — no relayout, no per-frame
/// re-render, and zero control-type knowledge.
/// </summary>
[Flags]
public enum TransitionChannels : byte
{
    None = 0,
    Position = 1,                 // translate the node from its old presented origin to the new layout origin (FLIP)
    Size = 2,                     // animate a size change per SizeMode (clip-reveal / scale-correct / relayout)
    Opacity = 4,                  // cross-fade on appearance/disappearance or paint change
    Bounds = Position | Size,
}

/// <summary>How a SIZE change is animated. <see cref="Auto"/> picks per node: pure-chrome leaf → <see cref="ScaleCorrect"/>;
/// a text/wrapping subtree under budget → <see cref="Relayout"/>; everything else → <see cref="Reveal"/>.</summary>
public enum SizeMode : byte
{
    Reveal,        // lay out at final size immediately; ease a clip window + translate. Crisp, compositor-only (the default).
    ScaleCorrect,  // GPU scale toward 1 with child counter-scale (Framer-Motion projection). Chrome only — distorts text/borders.
    Relayout,      // re-solve the subtree at the interpolated size each tick so text re-wraps live. Correct, costs scoped layout.
    Reflow,        // the size change runs through REAL layout each tick: the interpolated size participates in PARENT
                   // layout (boundary-scoped re-solve), so neighbours/siblings reflow smoothly. The deliberate
                   // smoother-than-WinUI mode for open/close surfaces (Expander, panes, info rows).
    Auto,
}

/// <summary>Which edge of a <see cref="SizeMode.Reflow"/> node its CONTENT is anchored to while the size animates.
/// <see cref="Leading"/> = content stays put, the far edge sweeps (a wipe). <see cref="Trailing"/> = the content's
/// end edge rides the animated edge (the WinUI Expander "slide out from under the header": at reveal 0 the content
/// sits fully behind the leading edge; its trailing rounded corners stay visible mid-motion). Applied by the recorder
/// as a child-group offset — compositor-composed, no per-child knowledge.</summary>
public enum SizeAnchor : byte { Leading, Trailing }

/// <summary>Axes affected by a size transition. Useful when one dimension is author-owned while the other is
/// continuously parent-owned (for example a measured shelf whose height settles while its width tracks the window).</summary>
[Flags]
public enum SizeAxes : byte { None = 0, Width = 1, Height = 2, Both = Width | Height }

/// <summary>Spring (velocity-carrying, interruptible — the default) or an eased fixed-duration tween.</summary>
public enum DynamicsKind : byte { Spring, Tween }

/// <summary>
/// The dynamics of a transition. Springs are the default (interruptible, jump-free retarget): <c>Response</c> ≈ the
/// settle time in seconds, <c>DampingRatio</c> 1 = critical (no overshoot), &lt;1 = bouncy. A tween is opt-in:
/// fixed <c>DurationMs</c> + <c>Easing</c> (an <see cref="EasingSpec"/>, so authored WinUI KeySplines are expressible;
/// a default spec is normalized by the engine to <see cref="Foundation.Easing.FluentDecelerate"/>). Stored as scalars
/// (not the AnimEngine's <c>SpringParams</c>) so the spec can live in Foundation; the engine converts at seed time.
/// A <c>default</c> value (all zero) is normalized by the engine to the spring defaults.
/// </summary>
public readonly record struct TransitionDynamics(
    DynamicsKind Kind = DynamicsKind.Spring,
    float Response = 0.30f,
    float DampingRatio = 0.85f,
    float DurationMs = 0f,
    EasingSpec Easing = default)
{
    public static TransitionDynamics Spring(float response = 0.30f, float dampingRatio = 0.85f)
        => new(DynamicsKind.Spring, response, dampingRatio);
    public static TransitionDynamics Tween(float durationMs, Easing easing = Foundation.Easing.FluentDecelerate)
        => new(DynamicsKind.Tween, 0f, 0f, durationMs, easing);
    public static TransitionDynamics Tween(float durationMs, EasingSpec easing)
        => new(DynamicsKind.Tween, 0f, 0f, durationMs, easing);
    // NOTE: must spell the arguments out — on a record struct the parameterless `new()` ZERO-initializes (primary-ctor
    // parameter defaults do NOT apply), which made Default a 0-response/0-damping spring: stiffness ~4e7 with zero
    // damping, and the integrator DIVERGES to ±Infinity in two frames (the Repeater.cs ItemCollectionTransition
    // comment documents the same landmine).
    public static TransitionDynamics Default => new(DynamicsKind.Spring, 0.30f, 0.85f);
}

/// <summary>Presented-space terminal for an inserted/removed node: where it animates FROM on enter / TO on exit
/// (offset + scale + opacity + self-blur σ), relative to its laid-out rect. <c>Active</c>=false ⇒ the node simply
/// snaps in/out. <c>Blur</c> &gt; 0 drives <c>AnimChannel.BlurSigma</c> (enter: Blur→0; exit: current→Blur) — the
/// skeleton cross-blur on the EXITING orphan layer.</summary>
public readonly record struct EnterExit(
    float Dx = 0f, float Dy = 0f, float Sx = 1f, float Sy = 1f, float Opacity = 1f, bool Active = false, float Blur = 0f);

/// <summary>The whole authoring surface (interned POD). Channels × dynamics × size-mode × enter/exit compose
/// orthogonally — translate / scale / rotate / opacity / clip-reveal all fall out, with no per-control special cases.</summary>
public readonly record struct LayoutTransition(
    TransitionChannels Channels,
    TransitionDynamics Dynamics = default,
    SizeMode Size = SizeMode.Auto,
    EnterExit Enter = default,
    EnterExit Exit = default,
    ushort CustomCurveId = 0,
    // Optional separate dynamics for the EXIT (disappear/collapse) leg — for controls whose WinUI open/close timings are
    // asymmetric (e.g. Expander expand 333ms / collapse 167ms). Null ⇒ exit reuses <see cref="Dynamics"/>.
    TransitionDynamics? ExitDynamics = null,
    // Optional start delay for layout-transition channels + enter/exit terminals. This keeps stagger as an engine
    // primitive (a field on the transition spec), not a control-local timer or per-frame callback.
    float DelayMs = 0f,
    // Content anchoring while a Reflow size animation runs (see SizeAnchor). Leading = wipe; Trailing = the content's
    // end edge rides the animated edge (the Expander slide-from-under-the-header). Ignored by the other size modes.
    SizeAnchor Anchor = SizeAnchor.Leading,
    SizeAxes Axes = SizeAxes.Both,
    // A projected container owns the visual response to this geometry commit. Bounds-animated descendants still receive
    // their FINAL layout, but their structural tracks are snapped instead of starting a second wave of Relayout/Reflow
    // work inside the already-projecting surface. This is scoped to the changed container subtree (unlike the global
    // interactive-resize suppression gate), so sibling panels and unrelated motion keep running.
    bool SuppressDescendantTransitions = false)
{
    /// <summary>Translate-only reflow (the default for reordered / moved items). Springs.</summary>
    public static LayoutTransition Slide => new(TransitionChannels.Position, TransitionDynamics.Default);

    /// <summary>Cross-fade only (no geometry).</summary>
    public static LayoutTransition Fade => new(TransitionChannels.Opacity, TransitionDynamics.Default);

    /// <summary>Position + size, the size animated via <paramref name="size"/> (Reveal = translate + clip, no distortion).</summary>
    public static LayoutTransition BoundsT(SizeMode size) => new(TransitionChannels.Bounds, TransitionDynamics.Default, size);

    /// <summary>The catch-all: position + size (Auto) + opacity. "Animate my layout, figure out the rest."</summary>
    public static LayoutTransition AutoAll => new(TransitionChannels.Bounds | TransitionChannels.Opacity, TransitionDynamics.Default);
}
