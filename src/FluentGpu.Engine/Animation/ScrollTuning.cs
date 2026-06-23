namespace FluentGpu.Animation;

/// <summary>
/// The centralized scroll "feel" profile — every WinUI-parity scroll constant in one POD, so on-device tuning is a
/// VALUE edit (never a logic edit) and the headless determinism gates can pin the exact numbers they were balanced
/// against. Consumed by <see cref="ScrollAnimator"/> (the fling/ease/spring knobs) and <c>InputDispatcher</c> (the
/// per-notch wheel distance). Pure managed, no TerraFX/COM — lives in the portable engine.
///
/// <para><b>Per-notch mouse-wheel distance.</b> A mouse/free-spin wheel notch (the <c>WheelNotch</c> field on an
/// <c>InputEvent</c>, a signed fractional notch count = rawAmount/120) scrolls
/// <c>max(<see cref="WheelPerNotchMinDip"/>, <see cref="WheelPerNotchViewportFrac"/>·viewport)</c> DIP — the WinUI
/// content-relative mouse-wheel line height (ScrollViewer_Partial.h: <c>max(48, 15%·viewport)</c>), replacing the old
/// flat 60-DIP/notch that under-scrolled tall lists and over-scrolled short ones. Synthetic/test wheel input that
/// carries a DIP <c>ScrollDelta</c> (no notch) bypasses this scaling entirely (the headless harness path). (Precision
/// touchpad pan is driven by the OS DirectManipulation source, not this wheel path.)</para>
///
/// <para><b>Velocity sampler note.</b> The touch fling velocity estimator (a fixed-ring windowed least-squares
/// regression in <c>InputDispatcher.TouchVelocity</c>) uses an engine-internal fixed window identical across profiles,
/// so it is NOT a per-profile knob here.</para>
/// </summary>
public readonly record struct ScrollTuning(
    float WheelPerNotchMinDip,        // WinUI mouse-wheel line-height floor (DIP per notch)
    float WheelPerNotchViewportFrac,  // content-relative wheel distance (fraction of the viewport extent per notch)
    float WheelEaseTauMs,             // wheel/scrollbar TargetChase smoothing time constant (ms)
    float FlingDecayPerS,             // touch-fling per-second velocity SURVIVAL factor (k = −ln(decay))
    float FlingSettleVelocityPxPerS,  // below this fling speed the integrator reverts to TargetChase (settles)
    float OverscrollSpringOmega)      // critically-damped overscroll release spring frequency (rad/s)
{
    /// <summary>The shipping default — the felt WinUI-parity profile the real (Win32) app and the engine default use.
    /// The fling/ease/spring values match <see cref="ScrollAnimator"/>'s documented constants exactly, so non-wheel-distance
    /// mouse-wheel behavior keeps the documented target chase.</summary>
    public static readonly ScrollTuning WinUiLike = new(
        WheelPerNotchMinDip: 48f,
        WheelPerNotchViewportFrac: 0.15f,
        WheelEaseTauMs: ScrollAnimator.WheelEaseTauMs,
        FlingDecayPerS: ScrollAnimator.FlingDecayPerS,
        FlingSettleVelocityPxPerS: ScrollAnimator.FlingMinVelocityPxPerS,
        OverscrollSpringOmega: OverscrollPhysics.SpringOmegaRadPerS);

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
