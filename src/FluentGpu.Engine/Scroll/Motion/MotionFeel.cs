using System.Collections.Generic;

namespace FluentGpu.Scroll.Motion;

/// <summary>ONE POD feel profile for the Wave-0 motion model — every named constant a <see cref="PlanAuthor"/> call
/// needs, so a profile switch (diagnostics-driven, never an env var — see the scroll-rework design's diagnostics
/// section) is a single struct swap. Values are baked into an authored <see cref="ScrollPlan"/> at author time
/// (<see cref="ScrollPlan.RubberC"/>), never read back out of the
/// plan mid-flight.</summary>
public readonly record struct MotionFeel(
    double WheelNotchDip,             // fixed DIP a single detented wheel notch moves the destination, pre-scaled by wheel-lines
    double WheelDurationS,            // re-plan duration (s) of a wheel notch's Cubic (shorter only when carried velocity outruns the kick)
    double AccelRefGapS,              // fast-spin branch: a notch's travel is scaled by AccelRefGapS / gapEma (the spin EMA)
    double AccelMax,                  // spin-acceleration ceiling: a notch's travel scaled by at most this under a fast spin
    double AccelResetGapS,            // a same-direction gap at/above this (s) ends the spin: next notch starts again at 1x
    double AccelEmaWeight,            // weight of the newest gap in the spin-rate EMA
    double FlingDecayPerS,            // touch/touchpad fling velocity decay RATE (1/s): v(t) = V0*e^(-this*t)
    double FlingMinVelocity,          // release speed below which a lifted contact settles in place instead of a fling
    double FlingImpulseWindowS,       // trailing horizon (s) of the contact ring's least-squares velocity (the release)
    double VelocityMinSpanS,          // least time span (s) that horizon must cover, else the contact reads 0 (coincident packet pairs)
    double RubberBandC,               // iOS-style rubber-band slope at zero excess: c*excess*vp/(vp + c*excess)
    double SpringOmega,               // natural frequency (rad/s) of the rubber-band release-to-edge Spring
    double SpringZeta,                // damping ratio of the rubber-band release-to-edge Spring (1 = critically damped)
    double GlideOmega,                // natural frequency (rad/s) of a programmatic Glide (always critically damped)
    double LookaheadS,                // virtualization look-ahead: overscanPx = clamp(|v|*this, OverscanMinPx, OverscanMaxPx)
    double OverscanMinPx,             // floor of the velocity-scaled overscan window (DIP)
    double OverscanMaxPx,             // ceiling of the velocity-scaled overscan window (DIP)
    double KeyLineDip,                // arrow-key line-step distance (DIP)
    double PageFraction,              // page-key step as a fraction of the viewport extent
    double WheelLatchSilenceS,        // notch silence (s) after which a wheel latch on a scroller releases
    double SettleEpsilonDip,          // position epsilon (DIP) below which a plan reads as visually settled
    double SettleVelocity,            // velocity epsilon below which a plan reads as visually settled
    double WheelRiseS,                // C1 blend-in (s) at the start of a wheel Cubic: from the carried velocity onto the front-loaded kick
    double AccelUnityGapS,            // spin EMA (s) at/above which a notch travels the full WheelNotchDip; below it gapEma/this (constant speed)
    double TouchpadWheelDip,          // DIP per 120 raw units of a touchpad's wheel-packet fallback stream (not the mouse notch)
    double TouchpadReleaseCapDipPerS) // ceiling of a composition-timed (DirectManipulation) touchpad release velocity; 0 = none
{
}

/// <summary>The shipping <see cref="MotionFeel"/> presets — data, not code paths; runtime-switchable from the
/// diagnostics page (no env vars, no `#if`).</summary>
public static class FeelProfiles
{
    /// <summary>The default wheel feel (2026-09-25, tuned from the owner's own notch traces — not a clone of any WinUI
    /// control): a single notch moves 64 DIP on a front-loaded 0.15 s cubic (50 % in 52 ms, 90 % in 109 ms), every
    /// re-plan continues the shown velocity (<see cref="MotionFeel.WheelRiseS"/>, C1), and the spin curve is re-derived so a fast
    /// spin travels what the 32 DIP model did (per-burst totals within ±6 % on the owner's traces): the travel per notch
    /// is <c>WheelNotchDip · clamp(max(AccelRefGapS/ema, min(1, ema/AccelUnityGapS)), ·, AccelMax)</c> — full at a slow
    /// roll, a constant 1280 DIP/s between 21 and 50 ms, rising again for a fast spin (<see cref="PlanAuthor.AccelFor"/>).
    /// The provenance and the simulation live in the `winui-wheel-model-measured` note.</summary>
    public static readonly MotionFeel Standard = new(
        WheelNotchDip: 64.0,
        WheelDurationS: 0.150,
        AccelRefGapS: 0.009,
        AccelMax: 2.0,
        AccelResetGapS: 0.120,
        AccelEmaWeight: 0.35,
        FlingDecayPerS: 2.3,
        FlingMinVelocity: 20.0,
        FlingImpulseWindowS: 0.040,
        VelocityMinSpanS: 0.004,
        RubberBandC: 0.55,
        SpringOmega: 12.5,
        SpringZeta: 1.0,
        GlideOmega: 18.0,
        LookaheadS: 0.025,
        OverscanMinPx: 200.0,
        OverscanMaxPx: 2400.0,
        KeyLineDip: 56.0,
        PageFraction: 0.875,
        WheelLatchSilenceS: 0.30,
        SettleEpsilonDip: 0.05,
        SettleVelocity: 8.0,    // a coast below 8 DIP/s (0.13 DIP/frame) reads as at rest; its asymptote is < 4 DIP away
        WheelRiseS: 0.012,      // 1.5 frames at 120 Hz: the kick is reached before the second frame
        AccelUnityGapS: 0.050,
        TouchpadWheelDip: 32.0,    // a touchpad's 120 raw units — the finger's 1:1 distance, unrelated to the mouse notch
        // WinUI 3 ScrollView's measured touchpad release plateau (owner capture 2026-09-25_16-32-17-tp-flicks.csv, 150 %):
        // 12 of 14 flicks released at 8.1-10.9k DIP/s whatever the finger speed, the highest at 10 865 DIP/s. DM's pan
        // gain makes the engine's ring read 50-100k DIP/s on the same flicks, so a DM release is capped at WinUI's.
        TouchpadReleaseCapDipPerS: 10865.0);

    /// <summary>The owner's "momentum" tuning: one notch reads as one track row and each notch
    /// glides for longer.</summary>
    public static readonly MotionFeel Glide = Standard with
    {
        WheelNotchDip = 56.0,
        WheelDurationS = 0.40,
    };

    /// <summary>All shipping profiles, named — the diagnostics page's picker source and the JSON round-trip's
    /// enumeration order.</summary>
    public static readonly IReadOnlyList<(string Name, MotionFeel Feel)> All = new (string, MotionFeel)[]
    {
        ("Standard", Standard),
        ("Glide", Glide),
    };
}
