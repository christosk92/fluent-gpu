using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// WinUI-parity rubber-band / overpan physics for touch pan and precision touchpad. Centralizes the resistance curve,
/// DM-inspired scale compression, edge-pivot transform composition, and the InteractionTracker-style release spring so
/// pull-at-edge and glide-into-edge share one model (ScrollInputHelper.cpp 10% cap; DirectManipulationService.cpp
/// scale-overpan constants; ScrollPresenter touchpad via IT elastic bounds — approximated here on the wheel path).
/// </summary>
public static class OverscrollPhysics
{
    // ScrollInputHelper.cpp:309 — ScrollViewer / ScrollPresenter default overpan cap (kept: BandLimit / OverscrollLimitFraction).
    public const float ViewportLimitFraction = 0.1f;

    // Reserved DM touch-overpan pull distance (the translation-only IT/touchpad path does not use it).
    public const float DmMaxOverpanDistancePx = 200f;

    // TODO (§5f, deferred): snap on touchpad coast — when a precision-touchpad coast settles within ~5f DIP of a
    // configured snap point, nudge the final rest ONTO it (the touch-fling snap-retarget already does this for a flick;
    // a touchpad coast currently settles wherever the momentum lands). Deferred until on-device snap-list tuning.

    // ── v2 iOS rubber-band (scroll-feel-rework-v2 §4.4/§4.6). ONE shipping feel — no env knobs on the scroll path.
    /// <summary>iOS rubber-band coefficient — the marginal slope at zero excess. <c>f(x)=x·d·c/(d+c·|x|)</c>.</summary>
    public const float RubberC = 0.55f;
    /// <summary>Asymptote fraction of the viewport the band approaches but NEVER reaches: <c>d = 0.15·viewport</c>.</summary>
    public const float BandAsymptoteFraction = 0.15f;

    // Critically-damped release (WinUI ScrollPresenter elastic settle) — plain consts, one shipping feel.
    /// <summary>Overscroll snap-back frequency (rad/s) — WebKit λ=12.5; τ=80ms, settle ≈320ms (was the snappy 42).</summary>
    public const float SnapBackOmega = 12.5f;
    public const float SpringDampingRatio = 1f;

    /// <summary>Edge-bounce seed coupling γ = WebKit's momentum coefficient <c>a</c> = 0.31 (scroll-feel-v2.1 §A.1). At
    /// ω=12.5 the critically-damped seed <c>v0·t·e^(−ωt)</c> is ALGEBRAICALLY WebKit's momentum bounce
    /// <c>(v0·a)·t·e^(−(s/p)t)</c> because <c>s/p = 20/1.6 = 12.5</c>, so γ=0.31 makes our bounce the WebKit exponential
    /// exactly and reproduces its ~9 px excursion per 1000 px/s. Was the ad-hoc 0.45.</summary>
    public const float MomentumSpringCoupling = 0.31f;

    /// <summary>Bounce depth cap <c>Cpeak</c> — the deepest a velocity-only edge bounce may reach as a fraction of the
    /// asymptote <c>d</c> (scroll-feel-v2.1 §A.1). The exact peak overshoot of <c>v0·t·e^(−ωt)</c> is <c>v0/(ω·e)</c>, so
    /// clamping the seed velocity to <c>Cpeak·d·ω·e</c> bounds the peak at <c>Cpeak·d &lt; d</c> — which keeps §A.4's
    /// re-grab inverse strictly inside its valid domain (the F7 divergence is then unreachable on a bounce). 0.6 is an
    /// honest feel constant: the soft-spring-into-asymptotic-band composition is ours (no shipping system stacks these
    /// two), so we keep the simplest value that guarantees <c>peak &lt; d</c> rather than invent a pedigree.</summary>
    public const float MomentumPeakDepthFraction = 0.6f;

    public static float BandLimit(float viewportExtent)
        => ViewportLimitFraction * MathF.Max(0f, viewportExtent);

    /// <summary>Signed visual band for a past-edge excess (offset space) — the canonical iOS asymptotic rubber-band
    /// (scroll-feel-rework-v2 §4.4): <c>f(x) = x·d·c/(d + c·|x|)</c> with <c>c = <see cref="RubberC"/> = 0.55</c> (the
    /// marginal slope at 0) and <c>d = <see cref="BandAsymptoteFraction"/>·viewport</c> the asymptote the band approaches
    /// but NEVER reaches — bounded, no wall, marginal give &gt; 0 everywhere (replaces the min()-clamped soft-knee that
    /// froze the content at the 10% cap). Applied to past-edge excess only; in-range is 1:1.</summary>
    public static float BandFromExcess(float excess, float viewportExtent)
    {
        if (excess == 0f || viewportExtent <= 0f) return 0f;
        float d = BandAsymptoteFraction * viewportExtent;
        float ax = MathF.Abs(excess);
        float f = (ax * d * RubberC) / (d + RubberC * ax);
        return excess < 0f ? -f : f;
    }

    /// <summary>EXACT inverse of <see cref="BandFromExcess"/> for re-seeding raw position on a mid-spring re-grab —
    /// <c>x = f·d / (c·(d − |f|))</c>, valid for ALL <c>|f| &lt; d</c>. No saturation early-out: the v2 map never
    /// saturates (band &lt; d always), so the inverse is exact everywhere in the band range (a headless gate round-trips
    /// this within 0.5px including at/above the OLD saturation point).</summary>
    public static float ExcessFromBand(float band, float viewportExtent)
    {
        if (band == 0f || viewportExtent <= 0f) return 0f;
        float d = BandAsymptoteFraction * viewportExtent;
        float af = MathF.Abs(band);
        // Safety clamp: BandFromExcess never emits |f| ≥ d, but a band DISPLACEMENT can also come from the spring
        // (velocity-seeded edge bounce overshoot) and may touch/exceed the asymptote — the true inverse diverges
        // there (d − af ≤ 0 ⇒ sign-flipped/∞ excess folded into a re-grab anchor). Cap just inside the asymptote.
        if (af >= d) af = 0.98f * d;
        float x = (af * d) / (RubberC * (d - af));   // |f| < d after the clamp
        return band < 0f ? -x : x;
    }

    /// <summary>Advance a friction coast one frame: decay the velocity by <paramref name="decayPerS"/>^dt and add the
    /// EXACT closed-form position integral of <c>v·decay^τ</c> over <c>[0, dt]</c> — <c>Δpos = v·(1 − decay^dt) / k</c>
    /// where <c>k = −ln(decay)</c>. Using the closed form (not a <c>v·dt</c> Riemann step) makes the total coast distance
    /// FRAME-RATE-INDEPENDENT (the per-step integrals telescope to the same geometric sum at any dt). Mutates
    /// <paramref name="velPxPerS"/> in place and returns the offset displacement to apply this frame. The shared owner of
    /// a reusable exact exponential-decay integrator; headless-gated for distance + dt-invariance.</summary>
    public static float CoastStep(ref float velPxPerS, float dtMs, float decayPerS)
    {
        if (decayPerS <= 0f || decayPerS >= 1f || dtMs <= 0f) return 0f;
        float dtS = dtMs * 0.001f;
        float f = MathF.Pow(decayPerS, dtS);
        float k = -MathF.Log(decayPerS); // > 0
        float dpos = velPxPerS * (1f - f) / k; // exact ∫₀^dt v·decay^τ dτ
        velPxPerS *= f;
        return dpos;
    }

    // (ShapeTouchpadPacketDelta — the second, frame-coalescing-sensitive gain curve of the deleted touchpad
    // integrator — is GONE: contact deltas apply 1:1 by contract (docs/plans/scroll-feel-rework-design.md §12; H2).)

    /// <summary>Seed the edge-bounce spring when inertia hits a clamp (touch fling or an OS-momentum tail at the edge) —
    /// VELOCITY-ONLY, position untouched (scroll-feel-v2.1 §A.1). This is the universal shipping pattern (iOS seeds a ζ=1
    /// spring at the current stretch with the edge-crossing velocity; Chromium seeds <c>initial_stretch=StretchAmount</c>
    /// + <c>initial_velocity</c>; Flutter/Android seed velocity only) — the old <c>v/k</c> position seed reached ~96% of the
    /// asymptote on any hard flick (F6's one-frame band teleport) and is deleted. <paramref name="bandPx"/> is LEFT at the
    /// passed-in current stretch; the spring then evolves it as <c>v0·t·e^(−ωt)</c> whose peak <c>v0/(ω·e)</c> is bounded to
    /// <c>Cpeak·d</c> by clamping the seed to <c>γ·v</c> capped at <c>Cpeak·d·ω·e</c>. Never SHRINKS an existing
    /// <paramref name="bandVelPxPerS"/> — a lift-at-a-held-stretch tick with v≈0 must not erase a live seed (F5). The
    /// caller keeps its own <c>sign(v)==sign(excess)</c> / <c>|v|≥settle</c> gate. The band POSITION is deliberately
    /// not a parameter: velocity-only means this function can never move the stretch.</summary>
    public static void SeedFromEdgeMomentum(ref float bandVelPxPerS, float velocityPxPerS, float viewportExtent)
    {
        if (viewportExtent <= 0f) return;   // leave the seed untouched (nothing to seed against)
        float d = BandAsymptoteFraction * viewportExtent;
        float vCap = MomentumPeakDepthFraction * d * SnapBackOmega * MathF.E;   // exact peak bound: peak = seedVel/(ω·e) ≤ Cpeak·d
        float sv = Math.Clamp(velocityPxPerS * MomentumSpringCoupling, -vCap, vCap);
        if (MathF.Abs(sv) > MathF.Abs(bandVelPxPerS)) bandVelPxPerS = sv;   // never shrink
    }

    /// <summary>Advance a damped spring from <paramref name="posPx"/> toward <paramref name="targetPx"/> (default 0 = the
    /// release spring-back). Returns true when settled at the target. The shipping critically-damped path uses the exact
    /// closed-form solution, avoiding the large first-frame displacement and long numerical tail of semi-implicit Euler.
    /// Non-default damping ratios retain the bounded ≤16ms semi-implicit fallback. The continuous
    /// touchpad overscroll passes the live demanded band as the target each frame; velocity carries forward across
    /// re-targets, so a new pull redirects the SAME spring instead of restarting it (no double-bounce).</summary>
    public static bool StepSpring(ref float posPx, ref float velPxPerS, float dtMs, float omegaRadPerS = 0f,
        float targetPx = 0f)
    {
        if (posPx == targetPx && velPxPerS == 0f) return true;
        // A velocity-only edge handoff starts exactly at the target. Even a small live inertia seed must be allowed to
        // produce its first elastic displacement; applying the generic subpixel settle test in this same tick would
        // erase speeds whose coupled spring velocity falls below 8 px/s before they ever become visible.
        bool justSeededAtTarget = posPx == targetPx && velPxPerS != 0f;
        float w = omegaRadPerS > 0f ? omegaRadPerS : SnapBackOmega;
        float z = SpringDampingRatio;
        if (dtMs > 0f && MathF.Abs(z - 1f) <= 0.0001f)
        {
            float t = dtMs * 0.001f;
            float x = posPx - targetPx;
            float c = velPxPerS + w * x;
            float e = MathF.Exp(-w * t);
            posPx = targetPx + (x + c * t) * e;
            velPxPerS = (velPxPerS - w * c * t) * e;
        }
        else
        {
            float remaining = dtMs;
            while (remaining > 0f)
            {
                float stepMs = MathF.Min(remaining, 16f);
                float h = stepMs * 0.001f;
                remaining -= stepMs;
                velPxPerS += (w * w * (targetPx - posPx) - 2f * z * w * velPxPerS) * h;
                posPx += velPxPerS * h;
            }
        }

        // Snap home once the bounce is visually done (≤0.5 DIP, ≤8 px/s) instead of crawling sub-pixel for ~100ms more —
        // that invisible tail read as a slow/lingering settle. (Always lands exactly on 0, so gates that assert final==0 hold.)
        if (!justSeededAtTarget && MathF.Abs(posPx - targetPx) <= 0.5f && MathF.Abs(velPxPerS) <= 8f)
        {
            posPx = targetPx;
            velPxPerS = 0f;
            return true;
        }

        return false;
    }

    /// <summary>Compose content <c>LocalTransform</c>: -(offset+band) translation. The precision-touchpad and
    /// InteractionTracker-style path is <b>translation-only</b> elastic overscroll (no scale — scale-about-edge is
    /// DM touch-only and pivots in viewport scroll-space, not content bounds, which read as "wrong relative point"
    /// when scrolling). Pinch zoom still uses origin conjugation when <paramref name="zoomFactor"/> ≠ 1.
    /// <para>The scroll-axis translation is snapped to a whole DEVICE pixel (scroll-feel-rework-v2 §4.6/§8):
    /// <c>tx = round((offset+band)·scale)/scale</c> with <paramref name="scale"/> the effective DPI scale — a slow
    /// sub-pixel pan advances in whole-device-px steps (a ScrollBind sticky pin sharing the origin never seams), while
    /// the LOGICAL offset/band remain continuous float (the caller's state is untouched). Headless scale = 1.</para></summary>
    public static void WriteContentTransform(
        ref NodePaint cp, in RectF contentBounds,
        bool horizontal, float offset, float band,
        float zoomFactor, float scale)
    {
        float z = (!float.IsFinite(zoomFactor) || zoomFactor <= 0f) ? 1f : zoomFactor;
        float s = (!float.IsFinite(scale) || scale <= 0f) ? 1f : scale;
        float t = MathF.Round((offset + band) * s) / s;   // device-pixel snap; logical offset/band stay float
        float offX = horizontal ? t : 0f;
        float offY = horizontal ? 0f : t;

        z = Math.Clamp(z, 1e-3f, 64f); // pick max to match product needs

        const float epsilon = 1e-4f;
        if (MathF.Abs(z - 1f) <= epsilon)
        {
            cp.LocalTransform = Affine2D.Translation(-offX, -offY);
            return;
        }
        
        float w = contentBounds.W, h = contentBounds.H;
        float ox = w * cp.OriginX, oy = h * cp.OriginY;
        var map = new Affine2D(z, 0f, 0f, z, -offX, -offY);
        cp.LocalTransform = Affine2D.Translation(-ox, -oy).Multiply(map).Multiply(Affine2D.Translation(ox, oy));
    }

    // The stretchy-header transform was moved out of physics into the generic scroll-binding evaluator
    // (FluentGpu.Animation.ScrollBindEval.ApplyContinuous → the FlagStretchClosedForm op): it was a binding
    // masquerading as physics. The (h+pull)/h scale + band-cancel matrix is unchanged; the leading-child walk is
    // gone (the bind targets the hero node by handle).

    /// <summary>Clamp band sign at clamped edges (prevents a 1-frame wrong-way flash during spring / relayout).</summary>
    public static float GuardBandSign(float band, float offset, float maxOffset)
    {
        if (offset <= 0.5f && band > 0f) return 0f;
        if (offset >= maxOffset - 0.5f && band < 0f) return 0f;
        return band;
    }
}
