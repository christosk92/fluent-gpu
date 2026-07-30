namespace FluentGpu.Animation;

/// <summary>
/// The centralized scroll "feel" profile — every WinUI-parity scroll constant in one POD, so on-device tuning is a
/// VALUE edit (never a logic edit) and the headless determinism gates can pin the exact numbers they were balanced
/// against. Consumed by <see cref="ScrollIntegrator"/> (the fling/ease/spring knobs) and <c>InputDispatcher</c> (the
/// per-notch wheel distance). Pure managed, no TerraFX/COM — lives in the portable engine.
///
/// <para><b>Per-notch mouse-wheel distance.</b> A mouse/free-spin wheel notch (the <c>WheelNotch</c> field on an
/// <c>InputEvent</c>, a signed fractional notch count = rawAmount/120) scrolls
/// <c>max(<see cref="WheelPerNotchMinDip"/>, <see cref="WheelPerNotchViewportFrac"/>·viewport)·lines/3</c> DIP — the
/// WinUI bounded content-relative mouse-wheel line height (<c>max(48, 10%·vp)·lines/3</c>, where <c>lines</c> is
/// <c>SPI_GETWHEELSCROLLLINES</c>; scroll-feel-rework-v2 §3.2). This keeps tall pages responsive. Synthetic/test wheel
/// input that carries a DIP <c>ScrollDelta</c> (no notch) bypasses this scaling entirely (the headless harness path).
/// (Precision touchpad pan rides the phase-tagged scroll contract (ScrollBegin/Update/End — 1:1), not this notch path.)</para>
///
/// <para><b>Velocity sampler note.</b> The touch fling velocity estimator (a fixed-ring windowed least-squares
/// regression in <c>InputDispatcher.TouchVelocity</c>) uses an engine-internal fixed window identical across profiles,
/// so it is NOT a per-profile knob here.</para>
/// </summary>
public readonly record struct ScrollTuning(
    float WheelPerNotchMinDip,        // WinUI mouse-wheel line-height floor (DIP per notch)
    float WheelPerNotchViewportFrac,  // content-relative wheel distance (fraction of the viewport extent per notch)
    float WheelFlingMaxVelocityPxPerS, // maximum accumulated mouse-wheel momentum
    float WheelEaseTauMs,             // wheel/scrollbar TargetChase smoothing time constant (ms)
    float FlingDecayPerS,             // touch-fling per-second velocity SURVIVAL factor (k = −ln(decay); 0.05 ⇒ WinUI 0.95 feel)
    float FlingSettleVelocityPxPerS,  // below this fling speed the integrator reverts to TargetChase (settles)
    float OverscrollSpringOmega)      // critically-damped overscroll release spring frequency (rad/s)
{
    // ── scroll-feel-rework-v2 §4.6 constants (ONE shipping feel — no env knobs on the scroll path). Physics-owned
    // values (SnapBackOmega / RubberC / BandAsymptoteFraction) alias OverscrollPhysics so there is one source of truth;
    // the resample / wheel-chase / fling-estimator constants are homed here (consumed by the integrator + dispatcher).
    public const float SnapBackOmega = OverscrollPhysics.SnapBackOmega;              // 12.5 rad/s — WebKit λ=12.5
    public const float RubberC = OverscrollPhysics.RubberC;                          // 0.55 — iOS rubber-band slope at 0
    public const float BandAsymptoteFraction = OverscrollPhysics.BandAsymptoteFraction; // 0.15·vp — band asymptote
    public const float WheelChaseHalflifeMs = 40f;      // velocity-preserving crit-damped wheel chase (~130ms settle)
    public const float ResampleLatencyMs = 12f;         // TouchpadTracking: resample to frameT − 12ms — ~1.5 packet
                                                        // periods (DM/touchpad packets arrive ~8.3ms apart, the SAME
                                                        // cadence as a 120Hz frame with drifting phase), so the target
                                                        // ALWAYS lands between two real samples. At 5ms it fell past the
                                                        // newest sample on ~40% of frames: extrapolating there bounced on
                                                        // every deceleration, holding there ground (hold-then-double-step
                                                        // aliasing). Android resamples at vsync−~11.5ms for this reason.
    public const float ResampleMaxPredictionMs = 8f;    // (historical) extrapolation cap — the resampler no longer predicts
    public const float ResampleMinDeltaMs = 2f;         // min usable sample spacing
    public const float VelWindowMs = 40f;               // Fling IMPULSE estimator window (= the trailing window; one gate)
    public const float AssumeStoppedMs = 40f;           // newest sample older than this at lift ⇒ v = 0
    public const float FlingSeedGate = 50f;             // |v| ≥ 50 px/s seeds a coast (Android min-fling)
    public const float FlingMaxVelocityPxPerS = 8000f;  // fling seed clamp (Android max-fling)

    // ── Flick projection (the page/index COMMIT ARITHMETIC — deliberately not physics). A release of speed v (px/s)
    // coasts an extra v / FlickProjectK DIP inside the BOUNDED snap-settle window before resting, so a control that must
    // decide WHICH page a lift commits to projects the resting offset forward by that much and rails/rounds it. Derived
    // from this profile's fling decay over the settle window T: coast = v·(1−decay^T)/k with k = −ln(decay), so the
    // divisor is k/(1−decay^T) — ≈ 5.68 at the shipping decay 0.05/s over 250 ms (i.e. a 1000 px/s lift projects ≈ 176 DIP
    // of extra travel). The BOUNDED window (not the infinite coast) is the right model for a page snap — a
    // slow under-threshold drag springs back, a flick navigates. Nothing here touches the glide that follows: that stays
    // the exact closed form the integrator runs (dt-deterministic).
    //
    // NOTE FlipViewCore.FlickProjectK computes the IDENTICAL value from the identical two inputs
    // (ScrollIntegrator.FlingDecayPerS, Dsl.Motion.ControlNormal). This is the canonical home — fold that private copy
    // onto this constant the next time FlipView is edited (it was left untouched here to keep the change surface small).
    public const float FlickProjectWindowS = 0.250f;   // = Dsl.Motion.ControlNormal / 1000 (Animation must not reference Dsl)
    /// <summary>The flick-projection divisor at the shipping fling decay: <c>projectedExtra = v / FlickProjectK</c>.</summary>
    public static readonly float FlickProjectK = FlickProjectDivisor(ScrollIntegrator.FlingDecayPerS, FlickProjectWindowS);

    /// <summary>The projection divisor for a per-second velocity SURVIVAL factor over a bounded window (seconds). Pure.</summary>
    public static float FlickProjectDivisor(float decayPerS, float windowS)
    {
        float k = -MathF.Log(decayPerS);                 // the per-second decay rate (−ln survival)
        float frac = 1f - MathF.Exp(-k * windowS);       // fraction of the full coast reached within the window
        return frac > 1e-4f ? k / frac : k;              // divisor: projectedExtra = v / divisor = v·frac/k
    }

    // ── Programmatic-glide dynamic (a PhaseProgrammatic WheelAnimating chase). A full-page shelf jump and a 40 DIP
    // settle-correction ride the SAME critically-damped closed form, so its half-life is the only feel lever — and one
    // constant cannot serve both: perceived arrival for a ζ=1 chase is ≈ 4.8·halflife (remaining travel drops under 1% at
    // y·t ≈ 6.64), so the fixed 95 ms bring-into-view value reads as ~455 ms — right for a page, mushy for a nudge.
    // The half-life is therefore chosen ONCE, AT ARM TIME, from the arm distance and latched in
    // ScrollState.ProgrammaticHalflifeMs; the integrator never recomputes it per tick, so the step stays the exact closed
    // form and the glide remains dt-deterministic. Distance → half-life is SQRT-shaped, not linear (perceived duration
    // should grow with √distance — the Fitts-like law every premium pager uses; a linear ramp makes mid-range jumps drag).
    public const float ProgrammaticGlideMinHalflifeMs = 46f;   // ≤ ShortDip travel: arrives in ~220 ms (a crisp correction)
    public const float ProgrammaticGlideMaxHalflifeMs = 88f;   // ≥ LongDip travel: arrives in ~420 ms (a legible page jump)
    public const float ProgrammaticGlideShortDip = 96f;        // at/below this travel the MIN half-life applies flat
    public const float ProgrammaticGlideLongDip = 900f;        // at/above this travel the MAX half-life applies flat

    /// <summary>The programmatic-glide half-life (ms) for a chase that must travel <paramref name="distanceDip"/> DIP —
    /// the sqrt ramp between <see cref="ProgrammaticGlideMinHalflifeMs"/> and <see cref="ProgrammaticGlideMaxHalflifeMs"/>
    /// described above. Pure; latched into <c>ScrollState.ProgrammaticHalflifeMs</c> by the arming caller (NaN/negative
    /// distances fall to the min).</summary>
    public static float ProgrammaticHalflifeForDistance(float distanceDip)
    {
        float d = MathF.Abs(distanceDip);
        if (!(d > ProgrammaticGlideShortDip)) return ProgrammaticGlideMinHalflifeMs;   // NaN-safe (NaN takes this branch)
        if (d >= ProgrammaticGlideLongDip) return ProgrammaticGlideMaxHalflifeMs;
        float t = MathF.Sqrt((d - ProgrammaticGlideShortDip) / (ProgrammaticGlideLongDip - ProgrammaticGlideShortDip));
        return ProgrammaticGlideMinHalflifeMs + t * (ProgrammaticGlideMaxHalflifeMs - ProgrammaticGlideMinHalflifeMs);
    }

    /// <summary>The shipping default — the felt WinUI-parity profile the real (Win32) app and the engine default use.
    /// The fling/ease/spring values match <see cref="ScrollIntegrator"/>'s documented constants exactly, so non-wheel-distance
    /// mouse-wheel behavior keeps the documented target chase.</summary>
    public static readonly ScrollTuning WinUiLike = new(
        WheelPerNotchMinDip: 48f,
        WheelPerNotchViewportFrac: 0.10f,
        WheelFlingMaxVelocityPxPerS: 4500f,
        WheelEaseTauMs: ScrollIntegrator.WheelEaseTauMs,
        FlingDecayPerS: ScrollIntegrator.FlingDecayPerS,
        FlingSettleVelocityPxPerS: ScrollIntegrator.FlingMinVelocityPxPerS,
        OverscrollSpringOmega: OverscrollPhysics.SnapBackOmega);   // v2: 12.5 rad/s (was 42)

    /// <summary>The gate-calibrated profile: identical feel to <see cref="WinUiLike"/> but with a per-notch distance of
    /// exactly 1 DIP (<see cref="WheelPerNotchMinDip"/> = 1, <see cref="WheelPerNotchViewportFrac"/> = 0), so a
    /// notch-count wheel event scrolls its raw value as DIP — preserving any headless gate that wants deterministic,
    /// viewport-independent wheel arithmetic. (The standing headless gates queue a DIP <c>ScrollDelta</c> and bypass
    /// per-notch scaling, so they are already independent of this profile; this exists for notch-based headless tests.)</summary>
    public static readonly ScrollTuning HeadlessGolden = WinUiLike with
    {
        WheelPerNotchMinDip = 1f,
        WheelPerNotchViewportFrac = 0f,
    };

    /// <summary>The DIP a single notch scrolls for a viewport of the given inner extent (DIP) along the scroll axis:
    /// <c>max(WheelPerNotchMinDip, WheelPerNotchViewportFrac·viewport)</c>. A zero/degenerate viewport (pre-Layout
    /// first frame) falls back to the floor.</summary>
    public readonly float PerNotchDip(float viewportExtent) =>
        MathF.Max(WheelPerNotchMinDip, WheelPerNotchViewportFrac * viewportExtent);
}
