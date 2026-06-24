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
    // DirectManipulationService.cpp — scale falls from 1 → s_scaleOverpanValue over s_maxOverpanDistance px of pull.
    public const float DmMaxOverpanDistancePx = 200f;
    public const float DmScaleAtMaxOverpan = 0.91f;

    // ScrollInputHelper.cpp:309 — ScrollViewer / ScrollPresenter default overpan cap.
    public const float ViewportLimitFraction = 0.1f;

    // TODO (§5f, deferred): snap on touchpad coast — when a precision-touchpad coast settles within ~5f DIP of a
    // configured snap point, nudge the final rest ONTO it (the touch-fling snap-retarget already does this for a flick;
    // a touchpad coast currently settles wherever the momentum lands). Deferred until on-device snap-list tuning.

    // InteractionTracker-style release — snappy bounce (WinUI ScrollPresenter elastic settle).
    public static readonly float SpringOmegaRadPerS = Env("FG_OS_OMEGA", 42f);
    public static readonly float SpringDampingRatio = Env("FG_OS_ZETA", 1f);

    /// <summary>Glide-into-edge: fraction of residual speed feeding the spring (higher = livelier edge bounce).</summary>
    public static readonly float MomentumSpringCoupling = Env("FG_OS_MOMENTUM", 0.45f);

    public static float BandLimit(float viewportExtent)
        => ViewportLimitFraction * MathF.Max(0f, viewportExtent);

    /// <summary>Signed visual band for a past-edge excess (offset space). Ratio damping with a SOFT knee at
    /// <c>soft = 2·limit</c> (gentler initial give — the band tracks the finger more closely for the first pixels of
    /// touch overpan), still hard-capped at the 10% viewport <see cref="BandLimit"/>. The softer denominator only
    /// changes the APPROACH; the cap is unchanged (DM / IT elastic family). Saturates to <c>limit</c> at <c>excess ≥ soft</c>.</summary>
    public static float BandFromExcess(float excess, float viewportExtent)
    {
        if (excess == 0f || viewportExtent <= 0f) return 0f;
        float limit = BandLimit(viewportExtent);
        float soft = limit * 2f;                          // gentler initial give (the soft knee)
        float raw = MathF.Abs(excess);
        float d = MathF.Min(limit, soft * raw / (raw + soft));
        return excess < 0f ? -d : d;
    }

    /// <summary>EXACT inverse of <see cref="BandFromExcess"/> for re-seeding raw position mid-spring (|band| &lt; limit).
    /// Uses the same <c>soft = 2·limit</c> knee so a re-seed round-trips (a headless gate pins this within 0.5px).</summary>
    public static float ExcessFromBand(float band, float viewportExtent)
    {
        if (band == 0f || viewportExtent <= 0f) return 0f;
        float limit = BandLimit(viewportExtent);
        float soft = limit * 2f;                          // must match BandFromExcess for the inverse to hold
        float abs = MathF.Abs(band);
        if (abs >= limit) return band < 0f ? -limit : limit;
        float excess = abs * soft / (soft - abs);
        return band < 0f ? -excess : excess;
    }

    /// <summary>DM scale compression during overpan: 1 at rest → <see cref="DmScaleAtMaxOverpan"/> at the band cap.
    /// Reserved for the Windows DM touch path; the precision-touchpad / InteractionTracker path is translation-only.</summary>
    public static float ScaleForBand(float bandAbs, float viewportExtent)
    {
        if (bandAbs <= 0f || viewportExtent <= 0f) return 1f;
        float limit = BandLimit(viewportExtent);
        if (limit <= 0f) return 1f;
        float t = MathF.Min(bandAbs / limit, 1f);
        return 1f - (1f - DmScaleAtMaxOverpan) * t;
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
        float k = -MathF.Log(decayPerS);            // > 0
        float dpos = velPxPerS * (1f - f) / k;      // exact ∫₀^dt v·decay^τ dτ
        velPxPerS *= f;
        return dpos;
    }

    /// <summary>Progressive precision-touchpad packet curve, applied once to the frame-coalesced DIP delta. Small motion
    /// (≤16 DIP/frame) stays exactly 1:1 for precision. Between 16 and 140 DIP the exponent slightly suppresses the
    /// mid/late tail while preserving the hard-flick endpoint, so acceleration and deceleration read as a curve instead
    /// of a linear gain. Above 140 DIP, surplus is compressed toward a 160 DIP safety ceiling. This shapes only OS
    /// packets; it creates no autonomous coast and therefore cannot double the driver's momentum tail.</summary>
    public static float ShapeTouchpadPacketDelta(float deltaDip)
    {
        const float precision = 16f;
        const float hardFlick = 140f;
        const float maxFrame = 160f;
        const float exponent = 1.25f;

        float a = MathF.Abs(deltaDip);
        if (a <= precision) return deltaDip;

        float shaped;
        if (a < hardFlick)
        {
            float x = (a - precision) / (hardFlick - precision);
            shaped = precision + (hardFlick - precision) * MathF.Pow(x, exponent);
        }
        else
        {
            shaped = MathF.Min(maxFrame, hardFlick + (a - hardFlick) * 0.20f);
        }
        return MathF.CopySign(shaped, deltaDip);
    }

    /// <summary>Seed band + spring velocity when inertia hits a clamp (touch fling or touchpad glide). One code path for
    /// both — excess travel from v/k is mapped through the same resistance curve; spring gets damped coupling, not raw v.</summary>
    public static void SeedFromEdgeMomentum(ref float bandPx, ref float bandVelPxPerS, float velocityPxPerS, float viewportExtent, float decayPerS)
    {
        if (velocityPxPerS == 0f || viewportExtent <= 0f) { bandPx = 0f; bandVelPxPerS = 0f; return; }
        float k = decayPerS > 0f && decayPerS < 1f ? -MathF.Log(decayPerS) : 1f;
        float excess = velocityPxPerS / k;
        bandPx = BandFromExcess(excess, viewportExtent);
        bandVelPxPerS = velocityPxPerS * MomentumSpringCoupling;
    }

    /// <summary>Advance a damped spring from <paramref name="posPx"/> toward <paramref name="targetPx"/> (default 0 = the
    /// release spring-back). Returns true when settled at the target. The shipping critically-damped path uses the exact
    /// closed-form solution, avoiding the large first-frame displacement and long numerical tail of semi-implicit Euler.
    /// Non-default damping ratios retain the bounded ≤16ms semi-implicit fallback. The continuous
    /// touchpad overscroll passes the live demanded band as the target each frame; velocity carries forward across
    /// re-targets, so a new pull redirects the SAME spring instead of restarting it (no double-bounce).</summary>
    public static bool StepSpring(ref float posPx, ref float velPxPerS, float dtMs, float omegaRadPerS = 0f, float targetPx = 0f)
    {
        if (posPx == targetPx && velPxPerS == 0f) return true;
        float w = omegaRadPerS > 0f ? omegaRadPerS : SpringOmegaRadPerS;
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
        if (MathF.Abs(posPx - targetPx) <= 0.5f && MathF.Abs(velPxPerS) <= 8f) { posPx = targetPx; velPxPerS = 0f; return true; }
        return false;
    }

    /// <summary>Compose content <c>LocalTransform</c>: -(offset+band) translation. The precision-touchpad and
    /// InteractionTracker-style path is <b>translation-only</b> elastic overscroll (no scale — scale-about-edge is
    /// DM touch-only and pivots in viewport scroll-space, not content bounds, which read as "wrong relative point"
    /// when scrolling). Pinch zoom still uses origin conjugation when <paramref name="zoomFactor"/> ≠ 1.</summary>
    public static void WriteContentTransform(
        ref NodePaint cp, in RectF contentBounds,
        bool horizontal, float offset, float band,
        float zoomFactor)
    {
        float z = zoomFactor > 0f ? zoomFactor : 1f;
        float offX = horizontal ? offset + band : 0f;
        float offY = horizontal ? 0f : offset + band;

        if (z == 1f)
        {
            cp.LocalTransform = Affine2D.Translation(-offX, -offY);
            return;
        }

        float w = contentBounds.W, h = contentBounds.H;
        float ox = w * cp.OriginX, oy = h * cp.OriginY;
        var map = new Affine2D(z, 0f, 0f, z, -offX, -offY);
        cp.LocalTransform = Affine2D.Translation(-ox, -oy).Multiply(map).Multiply(Affine2D.Translation(ox, oy));
    }

    /// <summary>iOS/Spotify stretchy header: find the flagged media node on the vertical scroll content's leading-child
    /// chain and scale it uniformly from normalized origin <c>(0.5, 0)</c> (top-center) to cover a top overscroll band.
    /// The content translation moves the authored header down by <c>pull</c>; the media transform cancels that translation
    /// and grows downward from its top-center without scaling the overlaid text.
    /// Resets to identity when not overscrolling. No-op for horizontal scrollers, bottom overscroll, or pages without a
    /// flagged media node.</summary>
    public static void ApplyStretchHeader(SceneStore scene, NodeHandle content, bool horizontal, float band)
    {
        if (horizontal || content.IsNull || !scene.IsLive(content)) return;
        // Walk the leading-child chain to the flagged media (skips SkelRegion / Body / Responsive / hero wrappers).
        // Bounded depth keeps the per-scroll-frame lookup constant and allocation-free.
        NodeHandle header = default;
        var n = scene.FirstChild(content);
        for (int depth = 0; depth < 12 && !n.IsNull && scene.IsLive(n); depth++)
        {
            if ((scene.Flags(n) & NodeFlags.ScrollStretchHeader) != 0) { header = n; break; }
            n = scene.FirstChild(n);
        }
        if (header.IsNull) return;

        ref NodePaint hp = ref scene.Paint(header);
        float pull = band < 0f ? -band : 0f;                 // top overscroll only (band<0)
        var bounds = scene.Bounds(header);
        float h = bounds.H;
        Affine2D target;
        if (h <= 1f)
            target = Affine2D.Identity;
        else if (pull > 0.5f)
        {
            // The recorder already conjugates LocalTransform around NodePaint.OriginX/Y. The flagged media authors
            // (0.5, 0), so this matrix contains ONLY scale + the band-cancel translation — embedding pivot offsets here
            // would apply the origin twice and make the image appear to scale from an edge.
            float s = (h + pull) / h;
            target = new Affine2D(s, 0f, 0f, s, 0f, -pull);
        }
        else
            target = Affine2D.Identity;
        if (hp.LocalTransform != target)
        {
            hp.LocalTransform = target;
            scene.Mark(header, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    /// <summary>Clamp band sign at clamped edges (prevents a 1-frame wrong-way flash during spring / relayout).</summary>
    public static float GuardBandSign(float band, float offset, float maxOffset)
    {
        if (offset <= 0.5f && band > 0f) return 0f;
        if (offset >= maxOffset - 0.5f && band < 0f) return 0f;
        return band;
    }

    private static float Env(string name, float dflt)
    {
        var s = Environment.GetEnvironmentVariable(name);
        return s is not null && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : dflt;
    }
}
