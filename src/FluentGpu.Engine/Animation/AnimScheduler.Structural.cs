using System;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — structural enter/exit + FLIP/reveal seeding (ported from AnimEngine onto the slab).
//
//  Additive to AnimScheduler; the reconciler/host call these (SeedEnter on mount, SeedExit on orphan, AnimateBounds
//  per moved BoundsAnimated node) — same public shapes as AnimEngine so the switch-over is near-drop-in. Dynamics
//  default to SPRING (the rework's default + AnimEngine's), which carries velocity through interruption; the tween
//  path uses the dynamics duration + the named default curve.
//
//  WIRED: SizeMode.Relayout + SizeMode.Reflow (the host RunReflowLayout/Incremental worklists) + the Trailing child-shift
//  (recorder ChildShiftY). Residual: the 0-alloc Eased generator stores only a NAMED EaseId, so a custom cubic-bezier on
//  that path falls back via EasingSpec.NamedOr — a non-issue in practice (the fades that use SeedEased pass named curves;
//  the declarative Keyframes/Animate path preserves full EasingSpec beziers). A sampled linear()-LUT would close it.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    private const Easing TweenDefault = Easing.FluentDecelerate;

    // ── declarative-token seeding (the authoring bake target; Reconciler/AnimBake calls these) ──────────
    /// <summary>Enter terminal → identity, driven by a <see cref="MotionTokenDef"/> (the declarative Element.Enter +
    /// Element.Transition path; no LayoutTransition needed).</summary>
    public void SeedEnter(NodeHandle node, in EnterExit e, in MotionTokenDef m, float delayMs = 0f)
    {
        if (e.Opacity != 1f) SeedChannel(node, AnimChannel.Opacity, 1f, in m, e.Opacity, delayMs);
        if (e.Dx != 0f) SeedChannel(node, AnimChannel.TranslateX, 0f, in m, e.Dx, delayMs);
        if (e.Dy != 0f) SeedChannel(node, AnimChannel.TranslateY, 0f, in m, e.Dy, delayMs);
        if (e.Sx != 1f) SeedChannel(node, AnimChannel.ScaleX, 1f, in m, e.Sx, delayMs);
        if (e.Sy != 1f) SeedChannel(node, AnimChannel.ScaleY, 1f, in m, e.Sy, delayMs);
        if (e.Blur != 0f) SeedChannel(node, AnimChannel.BlurSigma, 0f, in m, e.Blur, delayMs);
    }

    /// <summary>Current state → exit terminal, driven by a <see cref="MotionTokenDef"/> (the declarative Element.Exit path).</summary>
    public void SeedExit(NodeHandle node, in EnterExit e, in MotionTokenDef m, float delayMs = 0f)
    {
        SeedChannel(node, AnimChannel.Opacity, e.Opacity, in m, null, delayMs);   // always (the exit-settle signal)
        if (e.Dx != 0f) SeedChannel(node, AnimChannel.TranslateX, e.Dx, in m, null, delayMs);
        if (e.Dy != 0f) SeedChannel(node, AnimChannel.TranslateY, e.Dy, in m, null, delayMs);
        if (e.Sx != 1f) SeedChannel(node, AnimChannel.ScaleX, e.Sx, in m, null, delayMs);
        if (e.Sy != 1f) SeedChannel(node, AnimChannel.ScaleY, e.Sy, in m, null, delayMs);
        if (e.Blur != 0f) SeedChannel(node, AnimChannel.BlurSigma, e.Blur, in m, null, delayMs);
    }

    /// <summary>Spring/ease the gesture channels toward a <see cref="MotionTarget"/> (WhileHover/WhilePressed/WhileFocus
    /// when the state engages) or back to rest (pass <c>default</c>). The InteractionState resolver picks which target
    /// is active; this applies it.</summary>
    public void SeedTarget(NodeHandle node, in MotionTarget t, in MotionTokenDef m)
    {
        SeedChannel(node, AnimChannel.ScaleX, t.Scale, in m, null, 0f);
        SeedChannel(node, AnimChannel.ScaleY, t.Scale, in m, null, 0f);
        SeedChannel(node, AnimChannel.Opacity, t.Opacity, in m, null, 0f);
        SeedChannel(node, AnimChannel.TranslateX, t.OffsetX, in m, null, 0f);
        SeedChannel(node, AnimChannel.TranslateY, t.OffsetY, in m, null, 0f);
        SeedChannel(node, AnimChannel.BlurSigma, t.Blur, in m, null, 0f);
    }

    private void SeedChannel(NodeHandle node, AnimChannel ch, float to, in MotionTokenDef m, float? initial, float delayMs)
    {
        if (ReducedSnap(ch, m.Reduced)) { SnapTo(node, ch, to); return; }
        if (m.Mode == IntegrationMode.Spring)
            Spring(node, ch, to, m.Spring, initial, delayMs: delayMs);
        else
            Animate(node, ch, initial ?? CurrentValue(node, ch), to, m.DurationMs, m.Easing, delayMs: delayMs);
    }

    // Reduced-motion as a VALUE (read at the seed, never an early-return in authoring code — Motion.ReducedMotion is a
    // mutable global a resize-grip flips, so a conditional early-return is a hook-order hazard; the engine reads it as
    // data). A non-Exempt token SNAPS transforms to their end-state; KeepFade still animates Opacity (a cross-fade aids
    // orientation, it is not "motion"). The LayoutTransition path (which carries no policy) defaults to KeepFade.
    private static bool ReducedSnap(AnimChannel ch, ReducedMotionPolicy policy)
        => FluentGpu.Dsl.Motion.ReducedMotion
           && policy != ReducedMotionPolicy.Exempt
           && !(policy == ReducedMotionPolicy.KeepFade && ch == AnimChannel.Opacity);

    // Place a channel at its end-state immediately (a settled 1ms eased track → Compose writes `to` from the first frame).
    private void SnapTo(NodeHandle node, AnimChannel ch, float to) => SeedEased(node, ch, to, to, 1f, Easing.Linear);

    /// <summary>An inserted node animates FROM the enter terminal (offset/scale/opacity/blur) TO identity.</summary>
    public void SeedEnter(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.Dynamics);
        if (e.Opacity != 1f) SeedTerminal(node, AnimChannel.Opacity, 1f, dyn, initial: e.Opacity, delayMs: spec.DelayMs);
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, 0f, dyn, initial: e.Dx, delayMs: spec.DelayMs);
        if (e.Dy != 0f) SeedTerminal(node, AnimChannel.TranslateY, 0f, dyn, initial: e.Dy, delayMs: spec.DelayMs);
        if (e.Sx != 1f) SeedTerminal(node, AnimChannel.ScaleX, 1f, dyn, initial: e.Sx, delayMs: spec.DelayMs);
        if (e.Sy != 1f) SeedTerminal(node, AnimChannel.ScaleY, 1f, dyn, initial: e.Sy, delayMs: spec.DelayMs);
        if (e.Blur != 0f) SeedTerminal(node, AnimChannel.BlurSigma, 0f, dyn, initial: e.Blur, delayMs: spec.DelayMs);
    }

    /// <summary>A removed (now-Exiting) node animates FROM its current state TO the exit terminal; the host reclaims it
    /// when its rows settle. (Under the full rework this routes through the DetachedAnimSlab — Phase 5.)</summary>
    public void SeedExit(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.ExitDynamics ?? spec.Dynamics);
        SeedTerminal(node, AnimChannel.Opacity, e.Opacity, dyn, delayMs: spec.DelayMs);   // always (the exit-settle signal)
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, e.Dx, dyn, delayMs: spec.DelayMs);
        if (e.Dy != 0f) SeedTerminal(node, AnimChannel.TranslateY, e.Dy, dyn, delayMs: spec.DelayMs);
        if (e.Sx != 1f) SeedTerminal(node, AnimChannel.ScaleX, e.Sx, dyn, delayMs: spec.DelayMs);
        if (e.Sy != 1f) SeedTerminal(node, AnimChannel.ScaleY, e.Sy, dyn, delayMs: spec.DelayMs);
        if (e.Blur != 0f) SeedTerminal(node, AnimChannel.BlurSigma, e.Blur, dyn, delayMs: spec.DelayMs);
    }

    private void SeedTerminal(NodeHandle node, AnimChannel ch, float to, in TransitionDynamics dyn, float? initial = null, float delayMs = 0f)
    {
        if (ReducedSnap(ch, ReducedMotionPolicy.KeepFade)) { SnapTo(node, ch, to); return; }   // LayoutTransition carries no policy → KeepFade default
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial, delayMs: delayMs);
        else
            Animate(node, ch, initial ?? CurrentValue(node, ch), to, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
    }

    /// <summary>FLIP a moved node from its captured rect to its new laid-out rect, seeding/retargeting the requested
    /// channels. Position is velocity-continuous; Size uses Reveal (presented extent) or ScaleCorrect (GPU scale→1).
    /// Relayout re-solves the subtree each tick (live re-wrap); Reflow runs through the host's boundary worklist.</summary>
    public void AnimateBounds(NodeHandle node, in RectF fromAbs, in RectF toAbs, in LayoutTransition spec)
    {
        bool contracts = ((spec.Axes & SizeAxes.Width) != 0 && toAbs.W < fromAbs.W - 0.5f)
                      || ((spec.Axes & SizeAxes.Height) != 0 && toAbs.H < fromAbs.H - 0.5f);
        TransitionDynamics dyn = Normalize(contracts && spec.ExitDynamics is { } exit ? exit : spec.Dynamics);
        if (s_motionDiag)
            Console.Error.WriteLine($"[motion-diag] AnimateBounds node={node.Raw.Index} channels={spec.Channels} size={spec.Size} contracts={contracts} dyn={dyn.Kind}/{dyn.DurationMs:0}ms from=({fromAbs.X:0.0},{fromAbs.Y:0.0},{fromAbs.W:0.0},{fromAbs.H:0.0}) to=({toAbs.X:0.0},{toAbs.Y:0.0},{toAbs.W:0.0},{toAbs.H:0.0})");
        if ((spec.Channels & TransitionChannels.Position) != 0)
        {
            ReframePosition(node, AnimChannel.TranslateX, fromAbs.X - toAbs.X, dyn, spec.DelayMs);
            ReframePosition(node, AnimChannel.TranslateY, fromAbs.Y - toAbs.Y, dyn, spec.DelayMs);
        }
        if ((spec.Channels & TransitionChannels.Size) != 0)
        {
            bool width = (spec.Axes & SizeAxes.Width) != 0;
            bool height = (spec.Axes & SizeAxes.Height) != 0;
            SizeMode mode = spec.Size == SizeMode.Auto ? SizeMode.Reveal : spec.Size;
            switch (mode)
            {
                case SizeMode.Reveal:
                    if (width) RevealSize(node, AnimChannel.SizeW, fromAbs.W, toAbs.W, dyn, spec.DelayMs);
                    if (height) RevealSize(node, AnimChannel.SizeH, fromAbs.H, toAbs.H, dyn, spec.DelayMs);
                    break;
                case SizeMode.Relayout:   // re-solve the subtree at the interpolated size each tick (live re-wrap)
                    if (width)
                    {
                        RevealSize(node, AnimChannel.SizeW, fromAbs.W, toAbs.W, dyn, spec.DelayMs);
                        MarkRestoreLayout(node, AnimChannel.SizeW, _scene.Layout(node).Width);
                    }
                    if (height)
                    {
                        RevealSize(node, AnimChannel.SizeH, fromAbs.H, toAbs.H, dyn, spec.DelayMs);
                        MarkRestoreLayout(node, AnimChannel.SizeH, _scene.Layout(node).Height);
                    }
                    _scene.Mark(node, NodeFlags.Relayouting);
                    break;
                case SizeMode.ScaleCorrect:
                    if (width && toAbs.W > 0.5f) ScaleReveal(node, AnimChannel.ScaleX, fromAbs.W / toAbs.W, dyn, spec.DelayMs);
                    if (height && toAbs.H > 0.5f) ScaleReveal(node, AnimChannel.ScaleY, fromAbs.H / toAbs.H, dyn, spec.DelayMs);
                    break;
                case SizeMode.Reflow:
                    if (width) ReflowSize(node, AnimChannel.LayoutW, fromAbs.W, toAbs.W, spec);
                    if (height) ReflowSize(node, AnimChannel.LayoutH, fromAbs.H, toAbs.H, spec);
                    break;
            }
        }
    }

    // Position FLIP. Spring: shift a running spring's frame by the layout delta (keep velocity — analytical rebase),
    // or start a fresh spring at +delta springing to 0. Tween: restart from current+delta → 0.
    private void ReframePosition(NodeHandle node, AnimChannel ch, float delta, in TransitionDynamics dyn, float delayMs = 0f)
    {
        if (s_motionDiag) Console.Error.WriteLine($"[motion-diag]   Reframe node={node.Raw.Index} ch={ch} delta={delta:0.0} found={Find(node, ch) >= 0}");
        if (MathF.Abs(delta) < 0.01f && Find(node, ch) < 0) return;
        if (dyn.Kind == DynamicsKind.Spring)
        {
            var sp = SpringParams.FromResponse(dyn.Response, dyn.DampingRatio);
            int ex = Find(node, ch);
            if (ex >= 0 && _slab.At(ex).Kind == GenKind.Spring)
            {
                ref AnimValue r = ref _slab.At(ex);
                r.Position += delta;                       // coordinate frame shifted by the move
                r.To = 0f;
                r.Gen = Generators.BakeSpring(in sp, x0: r.Position, v0: r.Velocity);   // keep velocity (handoff)
                r.ElapsedMs = 0f; r.Flags &= ~AnimFlags.Done;
            }
            else Spring(node, ch, 0f, sp, initial: delta, delayMs: delayMs);
        }
        else
        {
            float cur = CurrentValue(node, ch);
            Animate(node, ch, cur + delta, 0f, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
        }
    }

    // Presented-extent reveal: spring/tween the recorder's drawn size old→new (works for grow and shrink).
    private void RevealSize(NodeHandle node, AnimChannel ch, float fromSize, float toSize, in TransitionDynamics dyn, float delayMs = 0f)
    {
        if (s_motionDiag) Console.Error.WriteLine($"[motion-diag]   Reveal node={node.Raw.Index} ch={ch} from={fromSize:0.0} to={toSize:0.0} found={Find(node, ch) >= 0}");
        if (MathF.Abs(fromSize - toSize) < 0.5f && Find(node, ch) < 0) return;
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, toSize, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: fromSize, delayMs: delayMs);
        else
            Animate(node, ch, fromSize, toSize, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
    }

    // ScaleCorrect: spring a scale channel old/new → 1 (recorder composites about centre; opted-in children counter-scale).
    private void ScaleReveal(NodeHandle node, AnimChannel ch, float fromRatio, in TransitionDynamics dyn, float delayMs = 0f)
    {
        if (MathF.Abs(fromRatio - 1f) < 0.001f && Find(node, ch) < 0) return;
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, 1f, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: fromRatio, delayMs: delayMs);
        else
            Animate(node, ch, fromRatio, 1f, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
    }

    /// <summary>Fill default dynamics (matches AnimEngine.Normalize): a spring with no response → the standard
    /// 0.30s/0.85ζ; a zero damping ratio → 0.85 (an undamped spring never settles); a default tween → 200ms.</summary>
    private static TransitionDynamics Normalize(in TransitionDynamics d)
    {
        return d.Kind == DynamicsKind.Spring
            ? (d.Response > 0f ? (d.DampingRatio > 0f ? d : d with { DampingRatio = 0.85f }) : TransitionDynamics.Default)
            : d with
            {
                DurationMs = d.DurationMs > 0f ? d.DurationMs : 200f,
                Easing = d.Easing.IsDefault ? TweenDefault : d.Easing,
            };
    }
}
