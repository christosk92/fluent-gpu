using System;

namespace FluentGpu.Scroll.Motion;

/// <summary>The shape a <see cref="MotionSeg"/> evaluates — closed forms only, no per-tick state. <c>Follow</c> (drag
/// contact) is NOT a segment kind: a live drag has no closed form (it tracks raw pointer samples), so it is carried
/// on <see cref="ScrollPlan"/>'s contact ring instead (<see cref="ScrollPlan.Count"/> == 0 selects the ring).</summary>
public enum SegKind : byte
{
    /// <summary>Stationary: <c>p = P0</c>, <c>v = 0</c>, for all <c>t</c> in the segment's validity range.</summary>
    Hold = 0,

    /// <summary>The wheel curve — a front-loaded cubic ease <c>p = P0 + R(1.5u − 0.5u³)</c>, <c>R = P1−P0</c>,
    /// <c>u = clamp((t−T0)/D, 0, 1)</c>, <c>D = T1−T0</c> (the kick: velocity <c>1.5R/D</c> at <c>u=0</c>, exactly 0 at
    /// <c>u=1</c> — a clean stop, never a crawl), plus a C1 blend-in over the first <see cref="MotionSeg.K"/> seconds
    /// (<c>ε = K/D</c>): <c>+ (V0·D − 1.5R)·u(1 − u/ε)³</c> for <c>u &lt; ε</c>. The blend makes the segment start at
    /// velocity <see cref="MotionSeg.V0"/> (the velocity the previous plan was showing) and rejoin the kick curve with
    /// matching position, velocity and acceleration at <c>u = ε</c>; with <c>K = 0</c> it is the plain cubic.
    /// Monotone and never past <c>P1</c> whenever <c>0 ≤ V0·D ≤ 1.5R</c> (the author's contract,
    /// <see cref="PlanAuthor.WheelSeg"/>).</summary>
    Cubic = 1,

    /// <summary>Exponential velocity decay (a released fling coasting): <c>v(t) = V0·e^(−K·dt)</c>,
    /// <c>p(t) = P0 + V0/K·(1 − e^(−K·dt))</c>. Asymptotic destination is <c>P0 + V0/K</c> — never reached in finite
    /// time, so an edge crossing is precomputed analytically (see <see cref="Motion.PlanAuthor"/>) rather than waited
    /// out.</summary>
    Decay = 2,

    /// <summary>An analytic damped spring released from <c>(P0, V0)</c> toward target <c>P1</c>, natural frequency
    /// <see cref="K"/> (rad/s) and damping ratio <see cref="Zeta"/> — under- (Zeta&lt;1), critically- (Zeta==1) or
    /// over-damped (Zeta&gt;1). Used for the rubber-band release back to an edge.</summary>
    Spring = 3,

    /// <summary>A programmatic move: critically-damped spring to <c>P1</c>, velocity-continuous from <c>V0</c> — the
    /// same closed form as <see cref="Spring"/> with <see cref="Zeta"/> pinned to 1 by the author (kept as a distinct
    /// tag purely for <see cref="MotionKind"/>/diagnostics classification, not a different formula).</summary>
    Glide = 4,
}

/// <summary>One closed-form motion segment: a portable, immutable, POD arc of a <see cref="ScrollPlan"/>. Valid over
/// the half-open interval <c>[T0, T1)</c> (an open-ended segment has <c>T1 = +∞</c>); <see cref="Eval"/> is a pure
/// function of an ABSOLUTE time — there is no per-tick <c>dt</c> integration anywhere in this type.</summary>
public readonly struct MotionSeg
{
    /// <summary>The segment's shape.</summary>
    public readonly SegKind Kind;

    /// <summary>Validity start (absolute seconds, QPC-derived domain — any monotonic clock works as long as every
    /// timestamp fed to <see cref="Eval"/> shares it).</summary>
    public readonly double T0;

    /// <summary>Validity end, exclusive; <c>+∞</c> for an open-ended segment (Decay/Spring/Glide/Hold typically run
    /// until superseded by a fresh plan, so they carry no natural end time).</summary>
    public readonly double T1;

    /// <summary>Start position (the value at <c>t = T0</c>).</summary>
    public readonly double P0;

    /// <summary>Destination / spring target (Cubic's end value; Decay's target is <c>P0 + V0/K</c>, NOT this field —
    /// Decay does not use <see cref="P1"/>).</summary>
    public readonly double P1;

    /// <summary>Initial velocity at <c>t = T0</c> (Decay/Spring/Glide; a Cubic's carried velocity for its blend-in).</summary>
    public readonly double V0;

    /// <summary>Decay's rate (1/s), Spring/Glide's natural frequency omega (rad/s), or a Cubic's blend-in duration (s).</summary>
    public readonly double K;

    /// <summary>Spring/Glide damping ratio (Glide is always constructed with <c>Zeta = 1</c>, i.e. critically
    /// damped; Spring may be under/critically/over-damped).</summary>
    public readonly double Zeta;

    /// <summary>Constructs a segment. <paramref name="v0"/>/<paramref name="k"/>/<paramref name="zeta"/> default to 0
    /// for kinds that don't use them (Hold, Cubic).</summary>
    public MotionSeg(SegKind kind, double t0, double t1, double p0, double p1, double v0 = 0.0, double k = 0.0, double zeta = 0.0)
    {
        Kind = kind;
        T0 = t0;
        T1 = t1;
        P0 = p0;
        P1 = p1;
        V0 = v0;
        K = k;
        Zeta = zeta;
    }

    /// <summary>Evaluates the segment's position and velocity at absolute time <paramref name="t"/> (closed form,
    /// zero allocation). Callers are expected to have already clamped <paramref name="t"/> into
    /// <c>[T0, T1)</c> (that selection is <see cref="ScrollPlan.Eval"/>'s job) — passing a <paramref name="t"/>
    /// before <see cref="T0"/> is treated as <see cref="T0"/> itself (never extrapolated backward).</summary>
    public double Eval(double t, out double v)
    {
        double dt = t - T0;
        if (dt < 0.0) dt = 0.0;

        switch (Kind)
        {
            case SegKind.Hold:
                v = 0.0;
                return P0;

            case SegKind.Cubic:
                {
                    double duration = T1 - T0;
                    if (!(duration > 0.0))
                    {
                        v = 0.0;
                        return P1;
                    }
                    double u = dt / duration;
                    if (u > 1.0) u = 1.0;
                    double r = P1 - P0;
                    v = r * (1.5 - 1.5 * u * u) / duration;
                    double p = P0 + r * (1.5 * u - 0.5 * u * u * u);
                    if (dt < K)
                    {
                        // C1 blend-in: carried velocity (V0) onto the kick (1.5R/D) over K seconds; zero at dt >= K.
                        double c = V0 * duration - 1.5 * r;
                        double w = 1.0 - dt / K;
                        p += c * u * w * w * w;
                        v += c * w * w * (1.0 - 4.0 * dt / K) / duration;
                    }
                    return p;
                }

            case SegKind.Decay:
                {
                    if (!(K > 0.0))
                    {
                        v = V0;
                        return P0;
                    }
                    double e = Math.Exp(-K * dt);
                    v = V0 * e;
                    return P0 + V0 / K * (1.0 - e);
                }

            case SegKind.Spring:
            case SegKind.Glide:
                return SpringEval(dt, out v);

            default:
                v = 0.0;
                return P0;
        }
    }

    /// <summary>The shared analytic damped-spring closed form used by both <see cref="SegKind.Spring"/> and
    /// <see cref="SegKind.Glide"/> (Glide is simply a Spring authored with <c>Zeta = 1</c>). Solves
    /// <c>y'' + 2·Zeta·K·y' + K²·y = 0</c> for <c>y = p − P1</c> with <c>y(0) = P0 − P1</c>, <c>y'(0) = V0</c>, split
    /// on the sign of <c>1 − Zeta²</c> (under/critically/over-damped); each branch's cosine/hyperbolic-cosine
    /// coefficient reduces algebraically to exactly <see cref="V0"/> at every <paramref name="dt"/> (not just at
    /// <c>dt = 0</c>), which is what keeps the three branches' velocities in the same closed form shape.</summary>
    private double SpringEval(double dt, out double v)
    {
        double omega = K;
        if (!(omega > 0.0))
        {
            v = V0;
            return P0;
        }

        double y0 = P0 - P1;
        double zeta = Zeta;
        double a = zeta * omega;
        double y, dy;

        if (zeta > 1.0 + 1e-9)
        {
            // Overdamped: y = e^-at (y0 cosh(bt) + D sinh(bt)), b = omega*sqrt(zeta^2-1).
            double b = omega * Math.Sqrt(zeta * zeta - 1.0);
            double d = (V0 + a * y0) / b;
            double eA = Math.Exp(-a * dt);
            double ch = Math.Cosh(b * dt);
            double sh = Math.Sinh(b * dt);
            y = eA * (y0 * ch + d * sh);
            dy = eA * (V0 * ch + (y0 * b - a * d) * sh);
        }
        else if (zeta < 1.0 - 1e-9)
        {
            // Underdamped: y = e^-at (y0 cos(wd t) + D sin(wd t)), wd = omega*sqrt(1-zeta^2).
            double wd = omega * Math.Sqrt(1.0 - zeta * zeta);
            double d = (V0 + a * y0) / wd;
            double eA = Math.Exp(-a * dt);
            double cs = Math.Cos(wd * dt);
            double sn = Math.Sin(wd * dt);
            y = eA * (y0 * cs + d * sn);
            dy = eA * (V0 * cs - (a * d + y0 * wd) * sn);
        }
        else
        {
            // Critically damped: y = (y0 + B t) e^-omega t, B = V0 + omega*y0.
            double b = V0 + omega * y0;
            double e = Math.Exp(-omega * dt);
            y = (y0 + b * dt) * e;
            dy = (V0 - omega * b * dt) * e;
        }

        v = dy;
        return P1 + y;
    }
}
