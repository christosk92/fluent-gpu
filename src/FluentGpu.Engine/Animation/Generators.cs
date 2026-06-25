using System;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION ENGINE — the generator evaluation LAW (LANDED; AnimEngine.Tick samples it every frame).
//
//  One pure function samples all four generator kinds at ABSOLUTE elapsed time `tMs` — no accumulator on the value,
//  no dt in the math. That is the determinism fix: the analytical closed-form spring (3 regimes) replaces the
//  sub-stepped semi-implicit Euler (whose sub-step count made the trajectory dt-path-dependent), so a replay at
//  dt ∈ {8.33, 16.67, 33.3} ms yields a bit-identical Position/Velocity trace by construction.
//
//  All parameter resolution (the (response,ζ)→(ω,ζ) conversion, the per-regime coefficient solve) happens ONCE in
//  the Bake* helpers at seed/reconcile, writing {Omega,Zeta,A,B}; Eval never solves, parses, iterates, or allocates.
//  `Sample` is returned BY VALUE (no shared-mutable {value,done} aliasing).
//  Design: docs/plans/animation-engine-rework-design.md §6.5–§6.7. Spring math generalizes OverscrollPhysics.StepSpring.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The pure generator law. Stateless; every method is a closed form of <c>(coefficients, t)</c>.</summary>
public static class Generators
{
    /// <summary>Default per-channel rest thresholds. Transforms/scale/opacity settle at sub-pixel
    /// (<see cref="RestDelta"/> ≈ 1e-3) so they never hang at the 0.5px scroll threshold; the scheduler passes a
    /// coarser delta (≈0.5px) for presented-size channels. (Plan §6.6 / OverscrollPhysics:169.)</summary>
    public const float RestDelta = 1e-3f;
    public const float RestSpeed = 1e-2f;

    // Over-damped guard: clamp the exponent magnitude so a very stiff spring at large t cannot underflow/NaN.
    private const float ExpClamp = 300f;

    /// <summary>Sample a generator at absolute elapsed <paramref name="tMs"/>. <paramref name="from"/> is the row's
    /// seed-time start (used by Eased/Inertia/Keyframes; IGNORED by Spring, whose start folds into the baked A/B).
    /// <paramref name="to"/> is the rest target.</summary>
    public static Sample Eval(in Generator g, GenKind kind, float from, float to, float tMs)
    {
        switch (kind)
        {
            case GenKind.Spring:
                return EvalSpring(in g, to, tMs, RestDelta, RestSpeed, out _);

            case GenKind.Inertia:
            {
                // Closed-form friction coast (subsumes OverscrollPhysics.CoastStep): pos(t) = start + V0·(1−e^−kt)/k.
                float t = tMs * 1e-3f;
                float k = g.DecayK <= 1e-6f ? 1e-6f : g.DecayK;
                float decay = MathF.Exp(-k * t);
                float pos = from + g.V0 * (1f - decay) / k;
                bool done = MathF.Abs(g.V0 * decay) <= 8f;   // settle below ~8 px/s
                if (!float.IsNaN(g.Boundary) &&
                    ((g.V0 >= 0f && pos >= g.Boundary) || (g.V0 < 0f && pos <= g.Boundary)))
                {
                    pos = g.Boundary;   // crossed the clamp — a Phase-5 boundary handoff springs back; coast stops here
                    done = true;
                }
                return new Sample(pos, done);
            }

            case GenKind.Keyframes:
                // Multi-keyframe sampling needs the shared keyframe arena (a later phase). Two-point fallback for now.
                goto default;

            default: // Eased — two-point From→To over DurationMs with a named/bezier curve.
            {
                float dur = g.DurationMs <= 0f ? 1f : g.DurationMs;
                float u = tMs / dur;
                u = u < 0f ? 0f : (u > 1f ? 1f : u);
                float e = Easings.Ease((Easing)(byte)g.EaseId, u);
                return new Sample(from + (to - from) * e, u >= 1f);
            }
        }
    }

    /// <summary>The analytical closed-form spring (3 regimes), sampled at absolute <paramref name="tMs"/>. Also yields
    /// the exact <paramref name="velocity"/> derivative — the single read the retarget handoff needs (no "sample at t
    /// and t−10ms" trick). Coefficients <c>A</c>/<c>B</c> were baked at seed by <see cref="BakeSpring(float,float,float,float)"/>
    /// from <c>(x0 = Position − To, v0 = Velocity)</c>.</summary>
    public static Sample EvalSpring(in Generator g, float to, float tMs, float restDelta, float restSpeed, out float velocity)
    {
        float t = tMs * 1e-3f;
        float w = g.Omega, zeta = g.Zeta, a = g.A, b = g.B;
        float disp, vel;

        if (zeta < 1f - 1e-4f)
        {
            // Under-damped: To + e^(−ζωt)(A·cos ωd·t + B·sin ωd·t).
            float wd = w * MathF.Sqrt(MathF.Max(1f - zeta * zeta, 1e-9f));
            float env = MathF.Exp(-zeta * w * t);
            float c = MathF.Cos(wd * t), s = MathF.Sin(wd * t);
            disp = env * (a * c + b * s);
            vel = env * ((-zeta * w * a + b * wd) * c + (-zeta * w * b - a * wd) * s);
        }
        else if (zeta <= 1f + 1e-4f)
        {
            // Critically-damped: To + (A + B·t)·e^(−ωt).
            float env = MathF.Exp(-w * t);
            disp = (a + b * t) * env;
            vel = env * (b - w * (a + b * t));
        }
        else
        {
            // Over-damped: To + A·e^(r1·t) + B·e^(r2·t), r negative (decaying) so the clamp guards against NaN.
            float sdisc = MathF.Sqrt(MathF.Max(zeta * zeta - 1f, 0f));
            float r1 = -w * (zeta - sdisc);
            float r2 = -w * (zeta + sdisc);
            float e1 = MathF.Exp(MathF.Max(r1 * t, -ExpClamp));
            float e2 = MathF.Exp(MathF.Max(r2 * t, -ExpClamp));
            disp = a * e1 + b * e2;
            vel = a * r1 * e1 + b * r2 * e2;
        }

        float value = to + disp;
        bool done = MathF.Abs(disp) <= restDelta && MathF.Abs(vel) <= restSpeed;
        if (done) { value = to; vel = 0f; }   // snap exactly to rest, zero velocity (gates assert final == 0)
        velocity = vel;
        return new Sample(value, done);
    }

    /// <summary>Bake the spring regime coefficients from the natural frequency <paramref name="omega"/> (rad/s),
    /// damping ratio <paramref name="zeta"/>, and the initial conditions <c>x0 = Position − To</c>, <c>v0 = Velocity</c>.
    /// Called at seed AND on every retarget (rebase: read current value/velocity, reset ElapsedMs=0, re-bake).</summary>
    public static Generator BakeSpring(float omega, float zeta, float x0, float v0)
    {
        Generator g = default;
        g.Omega = omega;
        g.Zeta = zeta;
        if (zeta < 1f - 1e-4f)
        {
            float wd = omega * MathF.Sqrt(MathF.Max(1f - zeta * zeta, 1e-9f));
            g.A = x0;
            g.B = (v0 + zeta * omega * x0) / (wd <= 1e-9f ? 1e-9f : wd);
        }
        else if (zeta <= 1f + 1e-4f)
        {
            g.A = x0;
            g.B = v0 + omega * x0;
        }
        else
        {
            float sdisc = MathF.Sqrt(MathF.Max(zeta * zeta - 1f, 0f));
            float r1 = -omega * (zeta - sdisc);
            float r2 = -omega * (zeta + sdisc);
            float aCoef = (v0 - x0 * r2) / (r1 - r2);
            g.A = aCoef;
            g.B = x0 - aCoef;
        }
        return g;
    }

    /// <summary>Bake from the friendlier <see cref="SpringParams"/> (Stiffness/Damping/Mass) + initial conditions:
    /// ω = √(k/m), ζ = c / (2√(k·m)).</summary>
    public static Generator BakeSpring(in SpringParams sp, float x0, float v0)
    {
        float m = sp.Mass <= 0f ? 1f : sp.Mass;
        float w = MathF.Sqrt(MathF.Max(sp.Stiffness / m, 0f));
        float zeta = w <= 1e-6f ? 1f : sp.Damping / (2f * m * w);
        return BakeSpring(w, zeta, x0, v0);
    }

    /// <summary>Bake a two-point eased generator (From→To over <paramref name="durationMs"/> with a named curve).</summary>
    public static Generator BakeEased(float from, float durationMs, Easing ease)
    {
        Generator g = default;
        g.FromV = from;
        g.DurationMs = durationMs;
        g.EaseId = (ushort)ease;
        g.KeyOffset = 0;
        g.KeyCount = 0;
        return g;
    }

    /// <summary>Bake a friction-coast inertia generator. <paramref name="decayK"/> = −ln(decayPerSecond) &gt; 0;
    /// <paramref name="boundary"/> = NaN for an unbounded coast.</summary>
    public static Generator BakeInertia(float v0, float decayK, float boundary)
    {
        Generator g = default;
        g.V0 = v0;
        g.DecayK = decayK;
        g.Boundary = boundary;
        return g;
    }
}
