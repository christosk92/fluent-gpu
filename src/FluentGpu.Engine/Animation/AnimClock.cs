namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — the wake-authority data model (LIVE: AnimEngine carries a Cadence per row and the host
//  waits on AnimEngine.NextDueMs; the AnimIsAmbient/AmbientAnimationFps inference it replaced is deleted).
//
//  NOTE ON THE NAME: the rework plan (§6.3) calls this `FrameClock`, but `FluentGpu.Hooks.FrameClock` already
//  exists (the hooks ambient-signal key behind `UseContext(FrameClock.Tick)`). To avoid the namespace collision
//  this type is named `AnimClock`. Wherever the plan says "FrameClock", the implementation means `AnimClock`.
//
//  `AnimClock` makes determinism a property of the clock, not of every animator: NowMs advances by a CLAMPED
//  delta (1..40ms), never raw wall-time, and a post-idle/throttle resume uses the default 1/60 quantum. Because
//  every Generator samples absolute time, a generator's trajectory is bit-identical under the dt∈{8.33,16.67,33.3}
//  replay gate by construction (inject wallNowMs = lastNow + dtFixture, wasIdle=false ⇒ delta == dtFixture exactly).
//
//  `Cadence` is the per-source classification whose `min(next-due)` scan REPLACES the entire ComputeWakeReasons()
//  16-bool OR + the ambient/grace/HUD branch tree: a lone 30Hz shimmer ⇒ ~33ms; add a live spring ⇒ present-now;
//  a paused playhead is `Driven` ⇒ skipped (+∞), event-woken by its signal write ⇒ ZERO frames, no exemption list.
//  The engine stores it per row (AnimEngine's `_cadencePeriodMs` side array, seeded by `Keyframes(..., cadence:)`)
//  and answers `AnimEngine.NextDueMs(nowMs)`; the host waits for that instead of inferring an ambient frame class.
//  Design: docs/plans/animation-engine-rework-design.md §3.4/§6.2–§6.3.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The one per-frame clock (the plan's "FrameClock"). The scheduler owns a mutable instance, calls
/// <see cref="Advance"/> once at frame start, then passes it by <c>in</c> to every tick. <see cref="NowMs"/> is the
/// absolute time every <see cref="Generator"/> samples; per-row <c>ElapsedMs</c> accumulates <see cref="DeltaMs"/>.</summary>
public struct AnimClock
{
    public const float MinDeltaMs = 1f;
    public const float MaxDeltaMs = 40f;            // a 200ms GC stall advances NowMs by 40ms, not 200ms
    public const float DefaultDeltaMs = 1000f / 60f; // the post-idle/throttle resume quantum (useDefaultElapsed)

    public double NowMs;      // running sum of clamped quanta — the deterministic absolute clock
    public float DeltaMs;     // this frame's clamped delta (what each row's ElapsedMs adds)
    public uint FrameId;
    private double _lastWallMs;

    /// <summary>Advance the clock. <paramref name="wasIdleOrThrottled"/> forces the default quantum (a resume from a
    /// blocking wait must not lurch by the full elapsed wall time). The headless replay injects
    /// <paramref name="wallNowMs"/> = lastNow + dtFixture with <paramref name="wasIdleOrThrottled"/> = false, so the
    /// clamped delta equals the fixture exactly (useManualTiming 1:1).</summary>
    public void Advance(double wallNowMs, bool wasIdleOrThrottled)
    {
        float delta;
        if (wasIdleOrThrottled || FrameId == 0)
        {
            delta = DefaultDeltaMs;
        }
        else
        {
            double raw = wallNowMs - _lastWallMs;
            delta = raw < MinDeltaMs ? MinDeltaMs : (raw > MaxDeltaMs ? MaxDeltaMs : (float)raw);
        }
        _lastWallMs = wallNowMs;
        DeltaMs = delta;
        NowMs += delta;
        FrameId++;
    }
}

/// <summary>How often a registered animation source needs a frame. The scheduler's wake IS <c>min(next-due)</c> over
/// the live, non-quiesced rows (<see cref="FluentGpu.Animation.AnimEngine.NextDueMs(double)"/>) — each kind answers
/// "does this source need a frame right now?" as DATA. This REPLACED the heuristics
/// (<c>AmbientAnimationFps</c>/<c>AnimIsAmbient()</c>/scroll-grace/<c>LatencySensitiveWake</c>) that approximated it:
/// cadence is now carried per row, so nothing has to guess whether the frame class is "ambient".</summary>
public enum CadenceKind : byte
{
    DisplayRate,   // present every frame while alive (a live spring/eased transform)
    Hz,            // a fixed sub-refresh rate (caret blink 2Hz, shimmer 30Hz, dynamic-text HUD 10Hz)
    Driven,        // progress comes from a signal — event-woken by the signal write, NEVER timer-due
    OneShot,       // settled / fire-once — never timer-due
    Paused,        // KeepAlive-parked — excluded from the wake entirely
}

/// <summary>A source's cadence + the parameter its kind needs. Tiny POD; the scheduler stores one per active source.</summary>
public struct Cadence
{
    public CadenceKind Kind;
    public float Hz;          // CadenceKind.Hz: frames per second
    public int DrivenSlot;    // CadenceKind.Driven: the SignalSource index (event-woken via WakeFrame)
    /// <summary>Opt a LOOPING row out of the weak-GPU-tier cadence cap (<see cref="TierCadenceCap"/>): the row keeps the
    /// cadence it asked for even on a <see cref="FluentGpu.Foundation.GpuProfile.IsWeak"/> device. Off by default - the
    /// cap is the policy, an exemption is the explicit exception (motion that IS the content, never ambient decoration).
    /// Set through <see cref="WithoutTierCap"/>.</summary>
    public bool TierUncapped;

    public static Cadence Display => new() { Kind = CadenceKind.DisplayRate };
    public static Cadence At(float hz) => new() { Kind = CadenceKind.Hz, Hz = hz };
    public static Cadence DrivenBy(int signalSlot) => new() { Kind = CadenceKind.Driven, DrivenSlot = signalSlot };
    public static Cadence Once => new() { Kind = CadenceKind.OneShot };
    public static Cadence Parked => new() { Kind = CadenceKind.Paused };

    /// <summary>This cadence, exempt from the weak-tier loop cap (<see cref="TierCadenceCap"/>) - for a perpetual row whose
    /// rate is part of what it shows. Meaningful on a <c>loop: true</c> row only; a one-shot is never capped.</summary>
    public readonly Cadence WithoutTierCap()
    {
        Cadence c = this;
        c.TierUncapped = true;
        return c;
    }

    /// <summary>The perpetual-loop default: <see cref="CadenceKind.Hz"/> with <see cref="Hz"/> = 0, which the engine
    /// resolves to <see cref="FluentGpu.Animation.AnimEngine.DefaultLoopHz"/> at wake/advance time — LATE, not at the
    /// seed. So the app's power policy can retune every idle shimmer live (battery ⇒ 15Hz, AC ⇒ 30Hz) without
    /// re-seeding a single row. <c>AnimEngine.Keyframes(..., loop: true)</c> with no explicit cadence means this.</summary>
    public static Cadence Default => new() { Kind = CadenceKind.Hz, Hz = 0f };

    /// <summary>True for <see cref="Default"/> — a rate the ENGINE resolves (<see cref="PeriodMs"/> cannot: it has no
    /// fixed period of its own and reports +∞, so resolve through the engine before reading it).</summary>
    public readonly bool IsDefault => Kind == CadenceKind.Hz && Hz <= 0f;

    /// <summary>Milliseconds between frames this source needs. <c>0</c> = present every frame (DisplayRate);
    /// <c>+∞</c> = never timer-due (Driven is event-woken; OneShot/Paused never wake — and so does
    /// <see cref="Default"/>, whose rate only the engine knows: check <see cref="IsDefault"/> first). The scheduler's
    /// <c>NextDueMs</c> scan takes the soonest <c>due − now</c> over live sources, skipping the +∞ ones.</summary>
    public readonly float PeriodMs => Kind switch
    {
        CadenceKind.DisplayRate => 0f,
        CadenceKind.Hz => Hz <= 0f ? float.PositiveInfinity : 1000f / Hz,
        _ => float.PositiveInfinity,
    };
}

/// <summary>
/// The weak-GPU-tier ceiling on PERPETUAL motion, applied once where a cadence resolves instead of at each call site
/// (F245). On an integrated / UMA GPU (the Adreno) every perpetual source - a marquee, a lyrics wipe, an equalizer, a
/// cover-wall drift - that runs at the panel rate keeps the whole frame loop hot and every decoded art texture uploading
/// on the very next frame; one <c>GpuProfile.IsWeak</c> branch inside a single recipe reached almost none of them. The
/// cap is data, not a gate: <see cref="FluentGpu.Animation.AnimEngine"/> clamps every looping row's period through
/// <see cref="LoopPeriodMs"/>, and the host spaces frame-clock pollers through <see cref="TryPollerWaitMs"/>. It only ever
/// LENGTHENS a period, so a stronger or unknown tier and a row that opted out (<see cref="Cadence.WithoutTierCap"/>) are
/// untouched, and a slower cadence (the 24 Hz battery loop, a 10 Hz HUD) keeps its own rate. Pure and allocation-free so a
/// test can pin the arithmetic without a weak device (<c>GpuProfile.IsWeak</c> is always false headlessly).
/// </summary>
internal static class TierCadenceCap
{
    /// <summary>Highest rate a perpetual loop row may run at on a weak tier (Hz).</summary>
    public const float WeakLoopMaxHz = 30f;
    /// <summary>Highest rate a frame-clock poller (a lyrics stepper, an equalizer, a playhead ticker) may be served at on a
    /// weak tier (Hz) - an UPPER bound the panel's refresh lattice can only round down from.</summary>
    public const float WeakPollerMaxHz = 60f;
    /// <summary><see cref="WeakLoopMaxHz"/> as the whole-ms period the scheduler resolves cadences to (1000/30 rounded).</summary>
    public const int WeakLoopMinPeriodMs = 33;
    /// <summary><see cref="WeakPollerMaxHz"/> as a period (ms).</summary>
    public const double WeakPollerMinPeriodMs = 1000.0 / WeakPollerMaxHz;

    /// <summary>The effective period (ms; <c>0</c> = display rate) of a LOOPING row whose own period is
    /// <paramref name="periodMs"/>: on a weak tier never shorter than <see cref="WeakLoopMinPeriodMs"/>, unless the row
    /// <paramref name="optedOut"/> (<see cref="Cadence.TierUncapped"/>). Display-rate (0) rows are capped too - that is the
    /// loop the cap exists for.</summary>
    public static int LoopPeriodMs(int periodMs, bool weak, bool optedOut)
        => weak && !optedOut && periodMs < WeakLoopMinPeriodMs ? WeakLoopMinPeriodMs : periodMs;

    /// <summary>The wait (ms, at least 1) that holds a frame-clock poller to <see cref="WeakPollerMaxHz"/> on a weak tier,
    /// phased on the last present by <see cref="FluentGpu.Hosting.CadencePacing"/>; false (no cap to apply) on any other tier
    /// or when the panel is already at or below that rate (a 60 Hz panel's display tick IS the cap).
    /// <paramref name="dueMs"/> is the soonest row/caret due time (+infinity for none): the poller is due NOW, so a LATER row
    /// or caret never delays it, while an EARLIER one shortens the wait so that row is not held back.
    /// The period is a WHOLE number of refreshes rounded UP, so the result is never faster than the cap: 120 Hz serves
    /// every 2nd refresh (60 Hz), 144 Hz every 3rd (48 Hz).</summary>
    public static bool TryPollerWaitMs(bool weak, double refreshMs, double sincePresentMs, double dueMs, out int waitMs)
    {
        waitMs = 0;
        if (!weak || !(refreshMs > 0.0) || refreshMs >= WeakPollerMinPeriodMs - 1.0) return false;
        double wantMs = WeakPollerMinPeriodMs;
        if (dueMs > 0.0 && dueMs < wantMs) wantMs = dueMs;
        // Whole refreshes, rounded up (the small tolerance keeps a measured 8.30 ms period on the 2-refresh lattice).
        double refreshes = Math.Ceiling(wantMs / refreshMs - 0.02);
        if (refreshes < 1.0) refreshes = 1.0;
        waitMs = FluentGpu.Hosting.CadencePacing.QuantizedWaitMs(refreshes * refreshMs, refreshMs, sincePresentMs);
        return true;
    }
}
