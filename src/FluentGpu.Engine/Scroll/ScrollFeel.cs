using System;

namespace FluentGpu.Scroll;

/// <summary>ONE POD feel profile, ONE shipping instance — no env knobs on the scroll path (plan §2.1). Every field's
/// provenance is documented on <see cref="ScrollPhysics"/> or <see cref="ScrollTuning"/>'s original home; this
/// record just collects the values a <see cref="ScrollKernel"/> needs at construction.</summary>
public readonly record struct ScrollFeel(
    float FlingDecayPerS,             // touch-fling per-second velocity SURVIVAL factor (k = −ln(decay))
    float FlingSeedGate,              // |v| ≥ this seeds a Ballistic coast (Android min-fling)
    float FlingMax,                   // fling seed clamp (Android max-fling)
    float FlingSettleVel,             // settle floor for the snap-armed fling, the Driven ζ/ω chase and the bounce seed (a free fling hands off at FlingLandVel instead)
    float WheelHalflifeMs,            // wheel/scrollbar chase half-life (ms): the cold-notch / stiffest cadence plan (S1 sweep)
    float WheelTailHalflifeMs,        // half-life the wheel glide stiffens to once a cadence stream has stopped (S1 tail: 3 3 2 2 2 2 px)
    float WheelSlowHalflifeMs,        // cadence-plan ceiling: hl = clamp(WheelHalflifePerGap·gap, WheelHalflifeMs, this)
    float WheelSeedFraction,          // κ — cold-notch velocity seed as a fraction of the no-hump velocity |R|·y (S1: 0.40 → 12.9 DIP first frame at D=120)
    float WheelCadenceKick,           // live-notch velocity kick: vel ≥ this·|Δ|/gap (Firefox cadence regime — the second click never dips)
    float WheelHalflifePerGap,        // ρ — cadence plan half-life as a fraction of the observed notch gap (0.70 = the S1 Pareto point)
    float WheelGapMinS,               // observed notch gap floor (s) — a burst faster than this plans as if at this gap
    float WheelGapMaxS,               // observed notch gap ceiling (s) — slower clicks are independent (stay at WheelHalflifeMs)
    float WheelGapSlackFrac,          // the stream is over once since > gap·(1+this) + WheelGapSlackS with no notch → tail stiffening
    float WheelGapSlackS,             // absolute slack (s) added to the stream-over test (one late frame of jitter)
    float WheelFloorDipPerS,          // displacement floor toward the target while landing — 160 DIP/s = 2 device px per 120 Hz frame at scale 1.5
    float WheelSnapEpsDip,            // distance-only snap: |R| below this lands exactly on the target (kills the sub-pixel creep tail)
    float ProgrammaticMinHalflifeMs,  // sqrt-ramp floor (a short programmatic glide)
    float ProgrammaticMaxHalflifeMs,  // sqrt-ramp ceiling (a long programmatic glide)
    float ProgrammaticShortDip,       // travel at/below which the ramp is flat at the min
    float ProgrammaticLongDip,        // travel at/above which the ramp is flat at the max
    float SnapBackOmega,              // critically-damped overscroll release spring frequency (rad/s)
    float RubberC,                    // iOS rubber-band slope at zero excess
    float BandAsymptoteFraction,      // band asymptote, as a fraction of the viewport
    float ResampleLatencyMs,          // touch contact resample target: frameT − this (touch/pen only)
    float ImpulseWindowMs,            // IMPULSE estimator trailing window
    float AssumeStoppedMs,            // sample→lift gap beyond which release velocity reads 0
    float RealizeAheadSec,            // virtualization realize-ahead horizon (velocity·this = lookahead distance)
    float WheelNotchMinDip,           // WinUI per-notch floor
    float WheelNotchViewportFrac,     // WinUI per-notch content-relative fraction
    float DragExtrapolateMaxMs,       // render-lease drag extrapolation cap (§6 — unused by WP-A, carried for the pin)
    float FlickProjectWindowS,        // bounded settle window for flick-projection commit arithmetic
    float FlingLandVel,               // below this coast speed a fling hands off to the Driven landing (ScrollPhysics.LandingStep)
    float FlingLandHorizonS)          // the landing travels v·this from the hand-off at the hand-off speed, never faster (the distance a linear decel from v to 0 over 2·this covers)
{
    /// <summary>The shipping feel — the only instance the engine ever constructs a <see cref="ScrollKernel"/> with.</summary>
    public static readonly ScrollFeel Shipping = new(
        FlingDecayPerS: 0.05f,
        FlingSeedGate: 50f,
        FlingMax: 8000f,
        FlingSettleVel: 13f,
        WheelHalflifeMs: 45f,
        WheelTailHalflifeMs: 32f,
        WheelSlowHalflifeMs: 90f,
        WheelSeedFraction: 0.40f,
        WheelCadenceKick: 0.65f,
        WheelHalflifePerGap: 0.70f,
        WheelGapMinS: 0.025f,
        WheelGapMaxS: 0.130f,
        WheelGapSlackFrac: 0.20f,
        WheelGapSlackS: 0.008f,
        WheelFloorDipPerS: 160f,
        WheelSnapEpsDip: 1.0f,
        ProgrammaticMinHalflifeMs: 46f,
        ProgrammaticMaxHalflifeMs: 88f,
        ProgrammaticShortDip: 96f,
        ProgrammaticLongDip: 900f,
        SnapBackOmega: 12.5f,
        RubberC: 0.55f,
        BandAsymptoteFraction: 0.15f,
        ResampleLatencyMs: 12f,
        ImpulseWindowMs: 40f,
        AssumeStoppedMs: 40f,
        RealizeAheadSec: 0.10f,
        WheelNotchMinDip: 48f,
        WheelNotchViewportFrac: 0.10f,
        DragExtrapolateMaxMs: 16f,
        FlickProjectWindowS: 0.250f,
        FlingLandVel: 120f,
        FlingLandHorizonS: 0.075f);

    /// <summary>The DIP a single wheel notch scrolls for a viewport of the given main-axis extent —
    /// <c>max(WheelNotchMinDip, WheelNotchViewportFrac·viewport)</c>.</summary>
    public float PerNotchDip(float viewportExtent) => MathF.Max(WheelNotchMinDip, WheelNotchViewportFrac * viewportExtent);

    /// <summary>The Windows "three lines per notch" rule: a scroller that knows its own line height
    /// (<paramref name="lineDip"/> &gt; 0 — a virtualized list's item extent, an <c>Element.ScrollLineDip</c> hint)
    /// travels <c>3·lineDip</c> per notch, so a 40-DIP row list moves exactly three rows and stays on the row grid.
    /// The platform has already scaled the notch by <c>SystemParams.WheelScrollLines / 3</c>, so the 3 here is the
    /// Windows baseline, not a second application of the user's setting. Without a hint (<c>lineDip == 0</c>) this is
    /// the viewport rule of <see cref="PerNotchDip(float)"/>.</summary>
    public float PerNotchDip(float viewportExtent, float lineDip) => lineDip > 0f ? 3f * lineDip : PerNotchDip(viewportExtent);

    /// <summary>The flick-projection divisor at this profile's fling decay over its settle window (see
    /// <see cref="ScrollPhysics.FlickProjectDivisor"/> / <c>ScrollTuning.FlickProjectDivisor</c>).</summary>
    public float FlickProjectK => ScrollPhysics.FlickProjectDivisor(FlingDecayPerS, FlickProjectWindowS);
}
