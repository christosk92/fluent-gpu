namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — the wake-authority data model (additive; Phase 2 wires it into AppHost's wait math).
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
//  `Cadence` is the per-source classification whose `min(next-due)` scan replaces the entire ComputeWakeReasons()
//  16-bool OR + the ambient/grace/HUD branch tree: a lone 30Hz shimmer ⇒ ~33ms; add a live spring ⇒ present-now;
//  a paused playhead is `Driven` ⇒ skipped (+∞), event-woken by its signal write ⇒ ZERO frames, no exemption list.
//  Design: docs/plans/animation-engine-rework-design.md §6.2–§6.3.
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

/// <summary>How often a registered animation source needs a frame. The scheduler's wake is
/// <c>min(next-due)</c> over the live, non-quiesced sources — each kind answers "does this source need a frame
/// right now?" as DATA, replacing the heuristics (<c>AmbientAnimationFps</c>/<c>AnimIsAmbient()</c>/scroll-grace/
/// <c>LatencySensitiveWake</c>) that approximated it.</summary>
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

    public static Cadence Display => new() { Kind = CadenceKind.DisplayRate };
    public static Cadence At(float hz) => new() { Kind = CadenceKind.Hz, Hz = hz };
    public static Cadence DrivenBy(int signalSlot) => new() { Kind = CadenceKind.Driven, DrivenSlot = signalSlot };
    public static Cadence Once => new() { Kind = CadenceKind.OneShot };
    public static Cadence Parked => new() { Kind = CadenceKind.Paused };

    /// <summary>Milliseconds between frames this source needs. <c>0</c> = present every frame (DisplayRate);
    /// <c>+∞</c> = never timer-due (Driven is event-woken; OneShot/Paused never wake). The scheduler's
    /// <c>NextDueMs</c> scan takes the soonest <c>due − now</c> over live sources, skipping the +∞ ones.</summary>
    public readonly float PeriodMs => Kind switch
    {
        CadenceKind.DisplayRate => 0f,
        CadenceKind.Hz => Hz <= 0f ? float.PositiveInfinity : 1000f / Hz,
        _ => float.PositiveInfinity,
    };
}
