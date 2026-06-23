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

    // InteractionTracker-style release — snappy bounce (WinUI ScrollPresenter elastic settle).
    public static readonly float SpringOmegaRadPerS = Env("FG_OS_OMEGA", 42f);
    public static readonly float SpringDampingRatio = Env("FG_OS_ZETA", 1f);

    /// <summary>Glide-into-edge: fraction of residual speed feeding the spring (higher = livelier edge bounce).</summary>
    public static readonly float MomentumSpringCoupling = Env("FG_OS_MOMENTUM", 0.45f);

    public static float BandLimit(float viewportExtent)
        => ViewportLimitFraction * MathF.Max(0f, viewportExtent);

    /// <summary>Signed visual band for a past-edge excess (offset space). Ratio damping capped at 10% viewport — the
    /// WinUI overpan cap. Near 1:1 for the first pixels, stiffens toward the limit (DM / IT elastic family).</summary>
    public static float BandFromExcess(float excess, float viewportExtent)
    {
        if (excess == 0f || viewportExtent <= 0f) return 0f;
        float limit = BandLimit(viewportExtent);
        float raw = MathF.Abs(excess);
        float d = limit * raw / (raw + limit);
        return excess < 0f ? -d : d;
    }

    /// <summary>Inverse of <see cref="BandFromExcess"/> for re-seeding raw position mid-spring (|band| &lt; limit).</summary>
    public static float ExcessFromBand(float band, float viewportExtent)
    {
        if (band == 0f || viewportExtent <= 0f) return 0f;
        float limit = BandLimit(viewportExtent);
        float abs = MathF.Abs(band);
        if (abs >= limit) return band < 0f ? -limit : limit;
        float excess = abs * limit / (limit - abs);
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
    /// release spring-back). Returns true when settled at the target. Semi-implicit Euler, ≤16ms substeps. The continuous
    /// touchpad overscroll passes the live demanded band as the target each frame; velocity carries forward across
    /// re-targets, so a new pull redirects the SAME spring instead of restarting it (no double-bounce).</summary>
    public static bool StepSpring(ref float posPx, ref float velPxPerS, float dtMs, float omegaRadPerS = 0f, float targetPx = 0f)
    {
        if (posPx == targetPx && velPxPerS == 0f) return true;
        float w = omegaRadPerS > 0f ? omegaRadPerS : SpringOmegaRadPerS;
        float z = SpringDampingRatio;
        float remaining = dtMs;
        while (remaining > 0f)
        {
            float h = MathF.Min(remaining, 16f) / 1000f;
            remaining -= 16f;
            velPxPerS += (w * w * (targetPx - posPx) - 2f * z * w * velPxPerS) * h;
            posPx += velPxPerS * h;
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
