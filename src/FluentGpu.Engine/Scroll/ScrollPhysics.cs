using System;

namespace FluentGpu.Scroll;

/// <summary>Pure static scroll-feel formulas — every constant carries its provenance. Ported VERBATIM (units
/// converted to seconds throughout, per the WP-A brief — the source files mix ms/s) from:
/// <c>src/FluentGpu.Engine/Animation/OverscrollPhysics.cs</c> (band/spring/coast/edge-seed),
/// <c>src/FluentGpu.Engine/Animation/ScrollIntegrator.cs:496-532,823-900</c> (chase + resample),
/// <c>src/FluentGpu.Engine/Animation/ScrollTuning.cs:83-99</c> (programmatic half-life ramp),
/// <c>src/FluentGpu.Engine/Scene/Columns.cs:526-623</c> (the <c>ScrollSnap</c> static — algorithm only, not the
/// <c>ScrollState</c>-typed API), and <c>src/FluentGpu.Engine/Input/InputDispatcher.cs:289-403</c> (the Android
/// IMPULSE velocity estimator). No reference back to any of those types — this is the "~200 lines of pure functions"
/// plan §1 says survive the rewrite structurally unchanged.</summary>
public static class ScrollPhysics
{
    // ── iOS asymptotic rubber-band (OverscrollPhysics.cs:24-28) ──────────────────────────────────────────────
    /// <summary>Marginal slope at zero excess in <c>f(x)=x·d·c/(d+c|x|)</c>.</summary>
    public const float RubberC = 0.55f;
    /// <summary>Asymptote fraction of the viewport the band approaches but never reaches: <c>d = 0.15·viewport</c>.</summary>
    public const float BandAsymptoteFraction = 0.15f;
    /// <summary>ScrollInputHelper.cpp:309 — the WinUI ScrollViewer / ScrollPresenter default overpan cap as a fraction of the
    /// viewport (kept for the OverscrollBand bind-range anchor cap; the band itself follows the asymptotic curve above).</summary>
    public const float ViewportLimitFraction = 0.1f;
    /// <summary>The nominal band cap in DIP for a viewport extent: <c>ViewportLimitFraction · max(0, viewport)</c>.</summary>
    public static float BandLimit(float viewportExtent) => ViewportLimitFraction * MathF.Max(0f, viewportExtent);

    // ── Critically-damped release spring (OverscrollPhysics.cs:30-47) — WebKit λ=12.5 ────────────────────────
    public const float SnapBackOmega = 12.5f;
    public const float SpringDampingRatio = 1f;
    /// <summary>Edge-bounce seed coupling γ — WebKit's momentum coefficient a=0.31.</summary>
    public const float MomentumSpringCoupling = 0.31f;
    /// <summary>Bounce depth cap Cpeak — the deepest a velocity-only edge bounce may reach, as a fraction of d.</summary>
    public const float MomentumPeakDepthFraction = 0.6f;

    /// <summary>Signed visual band for a past-edge excess — <c>f(x)=x·d·c/(d+c|x|)</c>. Bounded, no wall, marginal
    /// give &gt; 0 everywhere. Applied to past-edge excess only; in-range motion is 1:1.</summary>
    public static float BandFromExcess(float excess, float viewportExtent)
    {
        if (excess == 0f || viewportExtent <= 0f) return 0f;
        float d = BandAsymptoteFraction * viewportExtent;
        float ax = MathF.Abs(excess);
        float f = (ax * d * RubberC) / (d + RubberC * ax);
        return excess < 0f ? -f : f;
    }

    /// <summary>Exact inverse of <see cref="BandFromExcess"/> — <c>x = f·d/(c·(d−|f|))</c>, valid for all
    /// <c>|f| &lt; d</c>. A band displacement that reaches/exceeds the asymptote (a velocity-seeded spring
    /// overshoot) is clamped just inside it before inverting.</summary>
    public static float ExcessFromBand(float band, float viewportExtent)
    {
        if (band == 0f || viewportExtent <= 0f) return 0f;
        float d = BandAsymptoteFraction * viewportExtent;
        float af = MathF.Abs(band);
        if (af >= d) af = 0.98f * d;
        float x = (af * d) / (RubberC * (d - af));
        return band < 0f ? -x : x;
    }

    /// <summary>Advance a friction coast one step: decay velocity by <c>decayPerS^dtSec</c> and add the EXACT
    /// closed-form position integral of <c>v·decay^τ</c> over <c>[0, dtSec]</c> — <c>Δpos = v·(1−decay^dt)/k</c>
    /// where <c>k = −ln(decay)</c>. Frame-rate-independent (the per-step integrals telescope to the same geometric
    /// sum at any dt) — this is what makes <c>gate.kernel.dt-invariance</c> possible. Mutates
    /// <paramref name="velPxPerS"/> in place; returns the displacement to apply this step.</summary>
    public static float CoastStep(ref float velPxPerS, float dtSec, float decayPerS)
    {
        if (decayPerS <= 0f || decayPerS >= 1f || dtSec <= 0f) return 0f;
        float f = MathF.Pow(decayPerS, dtSec);
        float k = -MathF.Log(decayPerS);
        float dpos = velPxPerS * (1f - f) / k;
        velPxPerS *= f;
        return dpos;
    }

    /// <summary>A SNAP fling terminates on DISTANCE-TO-TARGET, not on velocity (ScrollIntegrator.cs:421-422, deleted) —
    /// the exponential coast never fully finishes (its natural rest is only reached at t=∞), so ending purely on the
    /// velocity floor (<see cref="ScrollFeel.FlingSettleVel"/>) leaves a permanent residual gap of roughly
    /// <c>v/k</c> off the snap grid. <see cref="ScrollBody.Advance"/> lands a snap-armed Ballistic EXACTLY on
    /// <c>ScrollBody.Target</c> once within this epsilon (or once the predicted step crosses it, or once velocity
    /// settles anyway) instead of wherever the natural decay happened to stop.</summary>
    public const float SnapLandEpsPx = 0.5f;

    /// <summary>Seed the edge-bounce spring when inertia hits a clamp — VELOCITY-ONLY, position untouched. Never
    /// SHRINKS an existing <paramref name="bandVelPxPerS"/> (a lift-at-a-held-stretch tick with v≈0 must not erase a
    /// live seed).</summary>
    public static void SeedFromEdgeMomentum(ref float bandVelPxPerS, float velocityPxPerS, float viewportExtent)
    {
        if (viewportExtent <= 0f) return;
        float d = BandAsymptoteFraction * viewportExtent;
        float vCap = MomentumPeakDepthFraction * d * SnapBackOmega * MathF.E;
        float sv = Math.Clamp(velocityPxPerS * MomentumSpringCoupling, -vCap, vCap);
        if (MathF.Abs(sv) > MathF.Abs(bandVelPxPerS)) bandVelPxPerS = sv;
    }

    /// <summary>Advance a damped spring from <paramref name="posPx"/> toward <paramref name="targetPx"/> (default 0
    /// = release spring-back). Returns true when settled AT the target (snapped exactly, not left crawling
    /// sub-pixel). The ζ=1 branch is the exact closed-form solution (no Euler drift); other damping ratios use a
    /// bounded ≤16ms semi-implicit sub-step fallback.</summary>
    public static bool StepSpring(ref float posPx, ref float velPxPerS, float dtSec, float omegaRadPerS = 0f, float targetPx = 0f)
    {
        if (posPx == targetPx && velPxPerS == 0f) return true;
        bool justSeededAtTarget = posPx == targetPx && velPxPerS != 0f;
        float w = omegaRadPerS > 0f ? omegaRadPerS : SnapBackOmega;
        float z = SpringDampingRatio;
        if (dtSec > 0f && MathF.Abs(z - 1f) <= 0.0001f)
        {
            float t = dtSec;
            float x = posPx - targetPx;
            float c = velPxPerS + w * x;
            float e = MathF.Exp(-w * t);
            posPx = targetPx + (x + c * t) * e;
            velPxPerS = (velPxPerS - w * c * t) * e;
        }
        else
        {
            float remaining = dtSec;
            while (remaining > 0f)
            {
                float h = MathF.Min(remaining, 0.016f);
                remaining -= h;
                velPxPerS += (w * w * (targetPx - posPx) - 2f * z * w * velPxPerS) * h;
                posPx += velPxPerS * h;
            }
        }
        if (!justSeededAtTarget && MathF.Abs(posPx - targetPx) <= 0.5f && MathF.Abs(velPxPerS) <= 8f)
        {
            posPx = targetPx;
            velPxPerS = 0f;
            return true;
        }
        return false;
    }

    /// <summary>Velocity-preserving critically-damped (ζ=1) chase to <paramref name="target"/> — the wheel/settle
    /// chase. <c>y = 2·ln2/halflifeS</c>; exact per-tick closed form (dt-deterministic).</summary>
    public static void ChaseStep(ref float off, ref float vel, float target, float halflifeMs, float dtSec)
    {
        if (halflifeMs <= 0f) halflifeMs = 1f;
        float y = 1.3862944f / (halflifeMs * 0.001f);
        float j0 = off - target;
        float j1 = vel + j0 * y;
        float e = MathF.Exp(-y * dtSec);
        off = e * (j0 + j1 * dtSec) + target;
        vel = e * (vel - j1 * y * dtSec);
    }

    /// <summary>Plan the <see cref="ChaseStep"/> a wheel notch drives — the S1 wheel plan (validated by the D=83/D=120
    /// simulation sweep behind <c>fix-everything-make-a-nested-willow.md §S1</c>). <see cref="ChaseStep"/> stays the
    /// integrator; this only decides <paramref name="target"/>, the seed/kick <paramref name="vel"/>, and the plan's
    /// <paramref name="halflifeMs"/>, and books the cadence state (<paramref name="sinceS"/> = seconds since the last
    /// notch, <paramref name="gapS"/> = the observed cadence, 0 = no cadence plan armed).
    /// <list type="bullet">
    /// <item>Cold notch (<paramref name="live"/> false — no wheel glide in flight): <c>vel = κ·R·yBase</c> with
    /// κ = <see cref="ScrollFeel.WheelSeedFraction"/>, so the first frame is already ~40 % of a no-hump chase instead of
    /// the <c>t·e^{−yt}</c> ease-in a zero-velocity ζ=1 chase produces (the old <c>Velocity = 0</c> reset). A same-direction
    /// <paramref name="carryVel"/> (a Ballistic fling, a Programmatic glide) is carried instead, capped at
    /// <c>0.95·|R|·yBase</c> — the exact no-overshoot bound of the ζ=1 solution (<c>v ≤ |R|·y</c> keeps
    /// <c>j1 = v − |R|·y ≤ 0</c>, so the position never crosses the target).</item>
    /// <item>Live notch, gap ≤ <see cref="ScrollFeel.WheelGapMaxS"/> (a stream — Chromium's velocity-continuous
    /// retarget): the target accumulates, <c>hl = clamp(ρ·gap, WheelHalflifeMs, WheelSlowHalflifeMs)</c> so a steady
    /// cadence gives a steady velocity (Firefox's cadence regime), a kick <c>vel ≥ min(WheelCadenceKick·|Δ|/gap,
    /// 0.95·|R|·y)</c> so the second click never dips to 2 px, then <c>y ≥ min(|vel|/|R|, yBase)</c> so the plan is never
    /// softer than the no-hump match (velocity never rises-then-falls inside one gap).</item>
    /// <item>Live notch, gap &gt; WheelGapMaxS: independent clicks — stay at <see cref="ScrollFeel.WheelHalflifeMs"/>,
    /// kick up to the cold seed, no cadence plan.</item>
    /// <item>Reversal (<c>Δ·vel &lt; 0</c> or <c>Δ·R &lt; 0</c>): rebase <c>target = off + Δ</c> (drop the unconsumed
    /// lag), keep the velocity — the ζ=1 chase brakes through zero in one frame and moves back on the second.</item>
    /// </list>
    /// Zero allocation, no branches on anything but POD; NaN-safe (the <c>|R| &lt; 1e-3</c> guard is what keeps a notch
    /// clamped onto the current offset from producing 0/0).</summary>
    public static void WheelPlanNotch(ref float target, ref float vel, ref float halflifeMs, ref float sinceS, ref float gapS,
        float off, float delta, float maxOff, bool live, float carryVel, in ScrollFeel f)
    {
        float yBase = 1.3862944f / (f.WheelHalflifeMs * 0.001f);
        if (!live)
        {
            target = Math.Clamp(off + delta, 0f, maxOff);
            float r = target - off;
            float seed = MathF.Abs(f.WheelSeedFraction * r * yBase);
            float carry = carryVel * r > 0f ? MathF.Min(MathF.Abs(carryVel), 0.95f * MathF.Abs(r) * yBase) : 0f;
            vel = r == 0f ? 0f : MathF.CopySign(MathF.Max(seed, carry), r);
            halflifeMs = f.WheelHalflifeMs; sinceS = 0f; gapS = 0f;
            return;
        }
        float g = sinceS; sinceS = 0f;
        float rOld = target - off;
        if (delta * vel < 0f || delta * rOld < 0f)            // reversal: drop the unconsumed lag, brake through zero
        { target = Math.Clamp(off + delta, 0f, maxOff); halflifeMs = f.WheelHalflifeMs; gapS = 0f; return; }
        target = Math.Clamp(target + delta, 0f, maxOff);
        float R = target - off, aR = MathF.Abs(R);
        if (aR < 1e-3f) { halflifeMs = f.WheelHalflifeMs; gapS = 0f; return; }   // 0/0 → NaN guard, mandatory
        if (g > f.WheelGapMaxS)                                // slow cadence: independent clicks, stay stiff
        {
            halflifeMs = f.WheelHalflifeMs; gapS = 0f;
            float want = f.WheelSeedFraction * aR * yBase;
            if (MathF.Abs(vel) < want) vel = MathF.CopySign(want, R);
            return;
        }
        g = MathF.Max(g, f.WheelGapMinS); gapS = g;
        float hl = Math.Clamp(f.WheelHalflifePerGap * g * 1000f, f.WheelHalflifeMs, f.WheelSlowHalflifeMs);
        float y = 1.3862944f / (hl * 0.001f);
        float kick = MathF.Min(f.WheelCadenceKick * MathF.Abs(delta) / g, 0.95f * aR * y);
        if (MathF.Abs(vel) < kick) vel = MathF.CopySign(kick, R);
        y = MathF.Max(y, MathF.Min(MathF.Abs(vel) / aR, yBase));   // never softer than the no-hump match
        halflifeMs = 1386.2944f / y;
    }

    /// <summary>One wheel-glide time step — <see cref="ChaseStep"/> under the plan <see cref="WheelPlanNotch"/> armed,
    /// plus the three landing rules that remove the sub-pixel creep tail without touching the content transform
    /// (<c>§S4</c>): returns true when the body has landed exactly on <paramref name="target"/>.
    /// <list type="bullet">
    /// <item>Tail stiffening: once <paramref name="sinceS"/> exceeds <c>gap·(1+WheelGapSlackFrac) + WheelGapSlackS</c>
    /// with no new notch, the stream is over and the half-life drops to <see cref="ScrollFeel.WheelTailHalflifeMs"/>.
    /// The step is split at the exact switch instant (two <see cref="ChaseStep"/>s), so the trajectory stays
    /// dt-exact — a 60 Hz and a 120 Hz lattice agree to well under a DIP.</item>
    /// <item>Displacement floor: while heading for the target before AND after the step (never through it), the
    /// step is at least <c>WheelFloorDipPerS·dt</c> — 160 DIP/s is exactly 2 device px per 120 Hz frame at scale 1.5, so
    /// the tail reads 3 3 2 2 2 2 (+≤1.5 px landing) instead of 1 1 1 0 1 0 1: zero frames that change the offset
    /// without moving a whole device pixel, which is what forced a crisp re-snap of every glyph run each frame.</item>
    /// <item>Distance-only snap: <c>|R| &lt; WheelSnapEpsDip</c> lands exactly on the target and zeroes the
    /// velocity — the settle predicate is distance, never a velocity floor the exponential creeps under.</item>
    /// </list>
    /// The step never travels past the target. Zero allocation; the caller (<c>ScrollBody.Advance</c>) still applies the
    /// hard-stop clamp at the extents.</summary>
    public static bool WheelStep(ref float off, ref float vel, ref float halflifeMs, ref float sinceS, ref float gapS,
        float target, float dtSec, in ScrollFeel f)
    {
        float r0 = target - off;
        if (MathF.Abs(r0) < f.WheelSnapEpsDip) { off = target; vel = 0f; gapS = 0f; return true; }
        float since0 = sinceS; sinceS += dtSec;
        float tSwitch = -1f;
        if (gapS > 0f && halflifeMs > f.WheelTailHalflifeMs)
        {
            float tExpect = gapS * (1f + f.WheelGapSlackFrac) + f.WheelGapSlackS;
            if (sinceS > tExpect) tSwitch = MathF.Max(0f, tExpect - since0);
        }
        float o = off, v = vel, v0 = vel, rest = dtSec;
        if (tSwitch > 0f) { ChaseStep(ref o, ref v, target, halflifeMs, tSwitch); rest -= tSwitch; }
        if (tSwitch >= 0f) { halflifeMs = f.WheelTailHalflifeMs; gapS = 0f; }
        ChaseStep(ref o, ref v, target, halflifeMs, rest);
        float d = o - off;
        if (v0 * r0 >= 0f && v * r0 >= 0f)                     // heading for the target before AND after: floor applies
        {
            float floor = f.WheelFloorDipPerS * dtSec;
            if (MathF.Abs(d) < floor) { d = MathF.CopySign(floor, r0); v = MathF.CopySign(f.WheelFloorDipPerS, r0); }
        }
        if (MathF.Abs(d) > MathF.Abs(r0)) d = r0;               // never past the target
        off += d; vel = v;
        if (MathF.Abs(target - off) < f.WheelSnapEpsDip) { off = target; vel = 0f; gapS = 0f; return true; }
        return false;
    }

    /// <summary>Underdamped (ζ&lt;1) chase to <paramref name="target"/> — the per-viewport programmatic override
    /// (e.g. <c>LyricsView</c>'s bespoke ζ/ω follow-glide). Exact per-tick closed form, velocity-continuous across
    /// retargets.</summary>
    public static void ChaseStepUnderdamped(ref float off, ref float vel, float target, float zeta, float omega, float dtSec)
    {
        float z = zeta, w0 = omega;
        float wd = w0 * MathF.Sqrt(MathF.Max(1e-6f, 1f - z * z));
        float j0 = off - target;
        float v0 = vel;
        float e = MathF.Exp(-z * w0 * dtSec);
        float cosD = MathF.Cos(wd * dtSec), sinD = MathF.Sin(wd * dtSec);
        float a = (v0 + z * w0 * j0) / wd;
        float x = e * (j0 * cosD + a * sinD);
        vel = -z * w0 * x + e * wd * (a * cosD - j0 * sinD);
        off = target + x;
    }

    /// <summary>Sqrt-shaped programmatic-glide half-life (ms) for a chase that must travel
    /// <paramref name="distanceDip"/> DIP — the fixed 95ms constant reads right for a page jump and mushy for a
    /// nudge, so distance picks the half-life once, at arm time (ScrollTuning.cs:88-99). NaN-safe (falls to the min).</summary>
    public static float ProgrammaticHalflifeS(float distanceDip, float minHalflifeMs, float maxHalflifeMs, float shortDip, float longDip)
    {
        float d = MathF.Abs(distanceDip);
        if (!(d > shortDip)) return minHalflifeMs;
        if (d >= longDip) return maxHalflifeMs;
        float t = MathF.Sqrt((d - shortDip) / (longDip - shortDip));
        return minHalflifeMs + t * (maxHalflifeMs - minHalflifeMs);
    }

    /// <summary>The flick-projection divisor at a given fling decay over a bounded settle window —
    /// <c>projectedExtra = v / divisor</c> (ScrollTuning.cs:64-73).</summary>
    public static float FlickProjectDivisor(float decayPerS, float windowS)
    {
        float k = -MathF.Log(decayPerS);
        float frac = 1f - MathF.Exp(-k * windowS);
        return frac > 1e-4f ? k / frac : k;
    }

    // ── Snap (Scene/Columns.cs:526-623 ScrollSnap — algorithm only, decoupled from ScrollState) ────────────────

    /// <summary>Snap <paramref name="natural"/> to the nearest applicable snap value. <paramref name="impulse"/> =
    /// the move is a fling (apply the ignored-start rule: a flick must travel at least one snap step);
    /// <paramref name="fromOffset"/> is the gesture-start offset. Identity when neither snap kind is configured.</summary>
    public static float SnapTarget(float natural, float snapInterval, float snapStart, float snapEnd, float[]? snapPoints, bool impulse, float fromOffset)
    {
        if (snapInterval <= 0f && (snapPoints is null || snapPoints.Length == 0)) return natural;

        float best = natural;
        float bestDist = float.PositiveInfinity;
        if (snapInterval > 0f)
        {
            float cand = SnapRepeated(natural, snapInterval, snapStart, snapEnd);
            float d = MathF.Abs(cand - natural);
            if (d < bestDist) { best = cand; bestDist = d; }
        }
        if (snapPoints is { Length: > 0 } pts)
        {
            float cand = SnapIrregular(natural, pts);
            float d = MathF.Abs(cand - natural);
            if (d < bestDist) { best = cand; bestDist = d; }
        }
        if (!impulse) return best;

        float startSnap = SnapTarget(fromOffset, snapInterval, snapStart, snapEnd, snapPoints, impulse: false, fromOffset);
        if (MathF.Abs(best - startSnap) < 0.5f)
        {
            float dir = natural - fromOffset;
            if (MathF.Abs(dir) > 0.0001f)
                best = NextSnap(startSnap, dir > 0f, snapInterval, snapStart, snapEnd, snapPoints);
        }
        return best;
    }

    private static float SnapRepeated(float value, float interval, float start, float end)
    {
        float first = start;
        float prev = MathF.Floor((value - first) / interval) * interval + first;
        float next = prev + interval;
        float snapped = (value - prev) <= (next - value) ? prev : next;
        if (end > start) snapped = Math.Clamp(snapped, start, end);
        else if (snapped < start) snapped = start;
        return snapped;
    }

    private static float SnapIrregular(float value, float[] pts)
    {
        float best = pts[0];
        float bestDist = MathF.Abs(pts[0] - value);
        for (int i = 1; i < pts.Length; i++)
        {
            float d = MathF.Abs(pts[i] - value);
            if (d < bestDist) { best = pts[i]; bestDist = d; }
        }
        return best;
    }

    private static float NextSnap(float from, bool forward, float snapInterval, float snapStart, float snapEnd, float[]? snapPoints)
    {
        float best = from;
        float bestGap = float.PositiveInfinity;
        if (snapInterval > 0f)
        {
            float cand = forward ? from + snapInterval : from - snapInterval;
            if (snapEnd > snapStart) cand = Math.Clamp(cand, snapStart, snapEnd);
            float gap = MathF.Abs(cand - from);
            if (gap > 0.5f && gap < bestGap) { best = cand; bestGap = gap; }
        }
        if (snapPoints is { Length: > 0 } pts)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                float p = pts[i];
                bool side = forward ? p > from + 0.5f : p < from - 0.5f;
                if (!side) continue;
                float gap = MathF.Abs(p - from);
                if (gap < bestGap) { best = p; bestGap = gap; }
            }
        }
        return best;
    }

    // ── Contact resampling (ScrollIntegrator.cs:823-900) ─────────────────────────────────────────────────────
    private const double ResampleMinDeltaS = 0.002; // 2ms — degenerate spacing guard

    /// <summary>Resample a contact-position history to <paramref name="tStar"/> seconds. <paramref name="t"/>/
    /// <paramref name="x"/> are chronological (index 0 = oldest, <paramref name="count"/>-1 = newest), up to 5
    /// retained samples. With ≥3 samples, fits a LEAST-SQUARES line through up to the newest 5 (excluding any older
    /// than 50ms — a different motion regime) — EXACT for on-line (constant-velocity) samples, averages out only
    /// off-line time-quantization noise. With exactly 2, linear-interpolates/back-projects (never extrapolates past
    /// the newest sample — holds there instead, since mis-prediction there was the visible snap-back). With 1 (or a
    /// vacuous <c>tStar &lt;= 0</c>), returns the latest position verbatim.</summary>
    public static float ResampleContact(ReadOnlySpan<double> t, ReadOnlySpan<float> x, int count, double tStar)
    {
        if (count <= 0) return 0f;
        if (count == 1 || tStar <= 0.0) return x[count - 1];

        double t1 = t[count - 1], t0 = t[count - 2];
        float x1 = x[count - 1], x0 = x[count - 2];
        double span = t1 - t0;
        if (span < ResampleMinDeltaS) return x1;

        if (count >= 3)
        {
            int n = Math.Min(count, 5);
            double staleBefore = t1 - 0.050;
            double ts = 0.0; float xs = 0f; int used = 0;
            for (int k = 0; k < n; k++)
            {
                int idx = count - 1 - k;
                double tk = t[idx];
                if (tk < staleBefore) break;
                ts += tk; xs += x[idx]; used++;
            }
            if (used >= 3)
            {
                double tm = ts / used; float xm = xs / used;
                double num = 0.0, denom = 0.0, oldestUsed = t1;
                for (int k = 0; k < used; k++)
                {
                    int idx = count - 1 - k;
                    double tk = t[idx]; float xk = x[idx];
                    double d = tk - tm;
                    num += d * (xk - xm); denom += d * d;
                    oldestUsed = tk;
                }
                if (denom > 1e-9)
                {
                    double slope = num / denom;
                    double tEval = Math.Clamp(tStar, oldestUsed, t1);
                    return (float)(xm + slope * (tEval - tm));
                }
            }
        }

        if (tStar >= t0 && tStar <= t1)
        {
            double f = (tStar - t0) / span;
            return (float)(x0 + (x1 - x0) * f);
        }
        if (tStar < t0)
        {
            if (count >= 3)
            {
                double tPrev = t[count - 3]; float xPrev = x[count - 3];
                double spanP = t0 - tPrev;
                if (spanP >= ResampleMinDeltaS)
                {
                    double f = (tStar - tPrev) / spanP;
                    if (f < -1.0) f = -1.0;
                    return (float)(xPrev + (x0 - xPrev) * f);
                }
            }
            double fb = (tStar - t0) / span;
            if (fb < -1.0) fb = -1.0;
            return (float)(x0 + (x1 - x0) * fb);
        }
        return x1; // no extrapolation past the newest sample
    }

    // ── Android IMPULSE release-velocity estimator (InputDispatcher.cs:289-403) ─────────────────────────────────

    /// <summary>Android IMPULSE velocity estimator, ported scalar (one axis — the kernel keeps one estimator per
    /// body, main axis only; cross-axis fling is not a shipping feel). <c>W = ½v₀|v₀| + Σ(vᵢ−vᵢ₋₁)|vᵢ|</c>,
    /// <c>release = sign(W)·√(2|W|)</c>. Fixed 8-slot inline ring, zero alloc, oldest→newest iteration (deterministic
    /// across a dt sweep — the input events are identical regardless of tick rate).</summary>
    public struct ImpulseEstimator
    {
        private const int Cap = 8;
        private const float HorizonMs = 40f;         // the single IMPULSE window (= the trailing window)
        private const float AssumeStoppedMs = 40f;    // newest-sample→lift gap beyond this ⇒ release velocity 0
        private const float MaxVelocityPxPerS = 8000f; // Android max-fling clamp

        private struct Pt { public float X; public double T; }
        [System.Runtime.CompilerServices.InlineArray(Cap)]
        private struct Ring { private Pt _e0; }

        private Ring _ring;
        private int _count, _head;
        private double _lastT;
        private bool _hasLast;
        private float _v;

        public readonly float Velocity => _v;

        public void Reset(float x, double tSec)
        {
            _count = 0; _head = 0; _hasLast = false; _v = 0f;
            Push(x, tSec);
        }

        /// <summary>Deposit a live sample; <see cref="Velocity"/> stays live (matches the source's
        /// windowed-estimate-while-dragging behavior).</summary>
        public void Sample(float x, double tSec)
        {
            if (!Push(x, tSec)) return;
            Compute(_lastT);
        }

        /// <summary>The release velocity to seed a fling, evaluated at lift WITHOUT pushing the lift position in (an
        /// Up/End event repeats the last Move's position at a later stamp — folding it in corrupts the estimate on
        /// lift-timing jitter). A non-advancing/duplicate stamp keeps the last computed velocity (the headless
        /// vacuous-0-stamp default).</summary>
        public void ComputeReleaseVelocity(double liftTSec)
        {
            if (double.IsNaN(liftTSec) || (_hasLast && liftTSec <= _lastT)) return;
            Compute(liftTSec);
        }

        private bool Push(float x, double t)
        {
            if (double.IsNaN(t)) return false;
            if (_hasLast && t <= _lastT) return false;
            _ring[_head] = new Pt { X = x, T = t };
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
            _lastT = t; _hasLast = true;
            return true;
        }

        private void Compute(double tLift)
        {
            _v = 0f;
            if (_count < 2) return;
            if (_hasLast && (tLift - _lastT) * 1000.0 > AssumeStoppedMs + 0.001) return; // stopped before lifting

            int oldest = (_head - _count + Cap) % Cap;
            double w = 0.0, vPrev = 0.0;
            bool first = true, hasPrev = false;
            Pt prev = default;
            for (int k = 0; k < _count; k++)
            {
                Pt s = _ring[(oldest + k) % Cap];
                if ((tLift - s.T) * 1000.0 > HorizonMs + 0.001) { prev = s; hasPrev = true; continue; }
                if (!hasPrev) { prev = s; hasPrev = true; continue; }
                double dt = s.T - prev.T;
                if (dt <= 0.0) { prev = s; continue; }
                double v = (s.X - prev.X) / dt;
                if (first) { w += 0.5 * v * Math.Abs(v); first = false; }
                else { w += (v - vPrev) * Math.Abs(v); }
                vPrev = v; prev = s;
            }
            if (first) return;
            float release = (float)(Math.Sign(w) * Math.Sqrt(2.0 * Math.Abs(w)));
            _v = Math.Clamp(release, -MaxVelocityPxPerS, MaxVelocityPxPerS);
        }
    }
}
