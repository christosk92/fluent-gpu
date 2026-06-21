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
/// flat 60-DIP/notch that under-scrolled tall lists and over-scrolled short ones. A precision touchpad (classified by
/// its hi-res delta signature, not the OS device API) instead uses <see cref="TouchpadDipPerNotch"/> to pixel-follow
/// each packet 1:1, then glides the measured lift velocity. Synthetic/test wheel input that carries a DIP
/// <c>ScrollDelta</c> (no notch) bypasses this scaling entirely (the headless harness path).</para>
///
/// <para><b>Velocity sampler note.</b> The touch fling velocity estimator (a fixed-ring windowed least-squares
/// regression in <c>InputDispatcher.TouchVelocity</c>) uses an engine-internal fixed window identical across profiles,
/// so it is NOT a per-profile knob here.</para>
/// </summary>
public readonly record struct ScrollTuning(
    float WheelPerNotchMinDip,        // WinUI mouse-wheel line-height floor (DIP per notch)
    float WheelPerNotchViewportFrac,  // content-relative wheel distance (fraction of the viewport extent per notch)
    float WheelEaseTauMs,             // wheel/scrollbar TargetChase smoothing time constant (ms)
    float TouchpadDipPerNotch,        // high-resolution touchpad packet scale (rawAmount/120 -> DIP)
    float TouchpadVelocityTauMs,      // response time of the packet-velocity estimator
    float TouchpadDecayPerS,          // touchpad glide per-second velocity survival factor (shares the WinUI fling curve)
    float TouchpadMinInertiaPxPerS,   // glide FLOOR: below this lift velocity the stream settles with no glide
    float TouchpadSettlePxPerS,       // glide stops below this speed
    float FlingDecayPerS,             // touch-fling per-second velocity SURVIVAL factor (k = −ln(decay))
    float FlingSettleVelocityPxPerS,  // below this fling speed the integrator reverts to TargetChase (settles)
    float OverscrollSpringOmega)      // critically-damped overscroll release spring frequency (rad/s)
{
    /// <summary>The shipping default — the felt WinUI-parity profile the real (Win32) app and the engine default use.
    /// The fling/ease/spring values match <see cref="ScrollAnimator"/>'s documented constants exactly, so non-wheel-distance
    /// mouse-wheel behavior keeps the documented target chase; precision touchpad packets pixel-follow then glide on
    /// lift using the values below.</summary>
    public static readonly ScrollTuning WinUiLike = new(
        WheelPerNotchMinDip: 48f,
        WheelPerNotchViewportFrac: 0.15f,
        WheelEaseTauMs: ScrollAnimator.WheelEaseTauMs,
        // Touchpad feel knobs are live-tunable via env vars (no rebuild) so on-device dialing is instant:
        //   FG_TP_SCALE = DIP per full wheel-unit (tracking speed — WinUI touchpad is ~pixel-precise, so keep this small),
        //   FG_TP_DECAY = glide velocity survival/s (smaller = shorter coast),
        //   FG_TP_FLOOR = px/s below which a LIFT does NOT glide (so gentle scrolls just stop where you stop; only a
        //                 deliberate flick coasts — this is what stops "one nudge = end of page").
        TouchpadDipPerNotch: Env("FG_TP_SCALE", 36f),
        TouchpadVelocityTauMs: 12f,
        TouchpadDecayPerS: Env("FG_TP_DECAY", 0.010f),
        TouchpadMinInertiaPxPerS: Env("FG_TP_FLOOR", 120f),
        TouchpadSettlePxPerS: 24f,
        FlingDecayPerS: ScrollAnimator.FlingDecayPerS,
        FlingSettleVelocityPxPerS: ScrollAnimator.FlingMinVelocityPxPerS,
        OverscrollSpringOmega: ScrollAnimator.OverscrollSpringOmega);

    /// <summary>Reads a float env-var override (invariant culture) for live on-device tuning of a touchpad feel knob;
    /// falls back to the shipped default when unset/unparseable. Read once at static init (the profile is immutable).</summary>
    private static float Env(string name, float dflt)
    {
        var s = System.Environment.GetEnvironmentVariable(name);
        return s is not null && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : dflt;
    }

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

    /// <summary>Converts a signed precision-touchpad notch-count to a DIP scroll distance with a SUB-LINEAR acceleration
    /// curve. Gentle scrolls (|notch| ≤ knee) get the full <see cref="TouchpadDipPerNotch"/> (FG_TP_SCALE) so they feel
    /// responsive (not "heavy"); a fast flick — this hardware streams huge notch values, 10+ notch-units in ONE packet —
    /// is COMPRESSED above the knee so a single packet can't teleport the content half a page ("top to bottom"). A flat
    /// scale can't serve both ends; this curve does. Tune overall speed with FG_TP_SCALE; the curve keeps the fast end
    /// bounded.</summary>
    public readonly float TouchpadDip(float notch)
    {
        // SMOOTH saturating response — NO kink. The old piecewise knee (full gain below, compressed above) made the speed
        // feel inconsistent the instant you crossed it. eff = u·soft/(u+soft) is C¹-continuous: ≈ u (linear, pixel-precise)
        // for gentle packets, smoothly approaching `soft` for a fast flick so a huge driver notch can't teleport — but the
        // gain never jumps, so the speed feels consistent across the whole range. FG_TP_SCALE scales the whole curve.
        float u = MathF.Abs(notch);                  // notch-units this packet (rawDelta / 120)
        const float soft = 6f;                       // saturation point (notch-units); fast packets approach soft·scale
        float eff = u * soft / (u + soft);
        return MathF.Sign(notch) * eff * TouchpadDipPerNotch;
    }
}
