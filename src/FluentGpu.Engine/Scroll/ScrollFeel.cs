using System;

namespace FluentGpu.Scroll;

/// <summary>ONE POD feel profile, ONE shipping instance — no env knobs on the scroll path (plan §2.1). Every field's
/// provenance is documented on <see cref="ScrollPhysics"/> or <see cref="ScrollTuning"/>'s original home; this
/// record just collects the values a <see cref="ScrollKernel"/> needs at construction.</summary>
public readonly record struct ScrollFeel(
    float FlingDecayPerS,             // touch-fling per-second velocity SURVIVAL factor (k = −ln(decay))
    float FlingSeedGate,              // |v| ≥ this seeds a Ballistic coast (Android min-fling)
    float FlingMax,                   // fling seed clamp (Android max-fling)
    float FlingSettleVel,             // below this fling speed the body settles (Ballistic → Idle/Bounce)
    float WheelHalflifeMs,            // wheel/scrollbar chase half-life (ms) when no per-command override is given
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
    float FlickProjectWindowS)        // bounded settle window for flick-projection commit arithmetic
{
    /// <summary>The shipping feel — the only instance the engine ever constructs a <see cref="ScrollKernel"/> with.</summary>
    public static readonly ScrollFeel Shipping = new(
        FlingDecayPerS: 0.05f,
        FlingSeedGate: 50f,
        FlingMax: 8000f,
        FlingSettleVel: 13f,
        WheelHalflifeMs: 40f,
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
        FlickProjectWindowS: 0.250f);

    /// <summary>The DIP a single wheel notch scrolls for a viewport of the given main-axis extent —
    /// <c>max(WheelNotchMinDip, WheelNotchViewportFrac·viewport)</c>.</summary>
    public float PerNotchDip(float viewportExtent) => MathF.Max(WheelNotchMinDip, WheelNotchViewportFrac * viewportExtent);

    /// <summary>The flick-projection divisor at this profile's fling decay over its settle window (see
    /// <see cref="ScrollPhysics.FlickProjectDivisor"/> / <c>ScrollTuning.FlickProjectDivisor</c>).</summary>
    public float FlickProjectK => ScrollPhysics.FlickProjectDivisor(FlingDecayPerS, FlickProjectWindowS);
}
