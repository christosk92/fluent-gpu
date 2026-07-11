using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// The single scroll integrator (phase 7) — scroll-feel-rework-v2 §2. This is the renamed, unified <c>ScrollAnimator</c>:
/// it is the ONE writer of <see cref="ScrollState.OffsetX"/>/<c>OffsetY</c> and <see cref="ScrollState.OverscrollPx"/>
/// (§2.1 single-writer invariant). Per active viewport per frame it consumes the recorded intents (<see cref="ScrollState.Phase"/>
/// + the intent columns, §2.4), advances the state's closed-form physics with the frame <c>dt</c>, and writes offset+band
/// ONCE through the chokepoint (the <see cref="ScrollWrite"/>/<see cref="OverscrollWrite"/> seams, wired to the Input
/// chokepoint <c>ApplyScrollPosition</c>). It also drives the auto-hiding scrollbar's WinUI "conscious" state machine.
/// Only viewports actively scrolling/transitioning are ticked; zero work / zero alloc once everything settles
/// (<see cref="HasActive"/> == false).
///
/// State enum (§2.2): <see cref="Idle"/> / <see cref="TouchpadTracking"/> / <see cref="WheelAnimating"/> /
/// <see cref="Fling"/> / <see cref="Overscroll"/> / <see cref="SnapBack"/>, with the auxiliary
/// <see cref="ScrollState.PhaseFlags"/> bits (OsOwned/Programmatic/Wheel).
///
/// Conscious-scrollbar timing — verified against microsoft-ui-xaml
/// controls\dev\CommonStyles\ScrollBar_themeresources.xaml:
/// <list type="bullet">
/// <item>Expand: pointer dwells over the scrollbar lane for ScrollBarExpandBeginTime = 400ms (:188), then the thumb
/// grows / track+buttons fade in over ScrollBarExpandDuration = 167ms (:173) with KeySpline 0,0,0,1 (:587) —
/// <see cref="Easing.FluentPopOpen"/>.</item>
/// <item>Contract (lane left, pointer still inside the viewport): begins after ScrollBarContractBeginTime = 500ms
/// (:189), plays over ScrollBarContractDuration = 167ms (:176), same spline (:543).</item>
/// <item>Indicator fades over ScrollBarOpacityChangeDuration = 83ms (:174), linear (the storyboards' plain
/// DoubleAnimation).</item>
/// <item>Idle auto-hide after a SCROLL: ScrollBarContractDelay = 2s (:178; ScrollViewer's SeparatorContractDelay is
/// the same 2s — ScrollViewer_themeresources.xaml) then the 83ms fade.</item>
/// <item>ENGINE-DELIBERATE (documented deviation): when the pointer leaves the whole viewport and the bar was revealed
/// by hover alone (no scroll since it appeared), it contracts immediately and retires after
/// ContractBeginTime + ContractDuration (≈667ms) + the 83ms fade — WinUI would hold the expanded bar 500ms and the
/// thin indicator the full 2s; an untouched hover-flash retiring early reads cleaner and keeps the frame loop idle.
/// Scroll-revealed bars keep the WinUI 2s ContractDelay.</item>
/// <item>While the pointer rests inside the viewport the thin indicator stays visible (the WinUI MouseIndicator
/// hold); it never idle-hides under the pointer.</item>
/// </list>
/// </summary>
public sealed class ScrollIntegrator
{
    // ── §2.2 state enum (was the untyped ScrollMode 0/1/2/3). Compile-time byte consts, written to ScrollState.Phase.
    /// <summary>No motion; the node drops from the active set (settle / clamp / cancel).</summary>
    public const byte Idle = 0;
    /// <summary>Contact tracking (touch / PTP / fallback): resampled contact position → offset 1:1; past-edge → Overscroll.</summary>
    public const byte TouchpadTracking = 1;
    /// <summary>Discrete wheel notch / scrollbar track-click / keyboard / <b>Programmatic</b> bring-into-view:
    /// velocity-preserving critically-damped chase to an accumulated, hard-clamped <see cref="ScrollState.PendingTargetX"/>/Y.</summary>
    public const byte WheelAnimating = 2;
    /// <summary>Inertial coast (touch/PTP-fallback lift |v|≥seed): exact-integral <see cref="OverscrollPhysics.CoastStep"/>;
    /// the <see cref="ScrollState.PhaseOsOwned"/> sub-flag applies OS <c>INERTIA</c> deltas verbatim instead.</summary>
    public const byte Fling = 3;
    /// <summary>Contact dragged past a content edge: the finger drives the iOS rubber-band 1:1 (band only; offset pinned).</summary>
    public const byte Overscroll = 4;
    /// <summary>Overscroll/Fling release with a live band: critically-damped spring band→0 (ω = <see cref="OverscrollPhysics.SnapBackOmega"/> = 12.5).</summary>
    public const byte SnapBack = 5;

    // WinUI ScrollBar_themeresources.xaml timing constants (see class remarks for line cites).
    public const float ExpandBeginMs = 400f;     // ScrollBarExpandBeginTime (:188)
    public const float ContractBeginMs = 500f;   // ScrollBarContractBeginTime (:189)
    public const float ExpandContractMs = 167f;  // ScrollBarExpandDuration / ScrollBarContractDuration (:173/:176)
    public const float FadeMs = 83f;             // ScrollBarOpacityChangeDuration (:174)
    public const float IdleHideMs = 2000f;       // ScrollBarContractDelay (:178) — after a scroll, pointer away
    /// <summary>Engine-deliberate hover-flash retire delay (see class remarks): contract-begin + contract.</summary>
    public const float LeaveHideMs = ContractBeginMs + ExpandContractMs;

    /// <summary>Minimum overflow (content − viewport, DIP) before the conscious scrollbar may arm. Below it the content
    /// effectively fits — a sub-px / few-px fractional-layout remainder is not worth a bar — so a hover or a tiny pan
    /// never flickers the indicator in. (The edge-fade cue keeps its own tighter 0.5px gate.)</summary>
    public const float MinBarOverflowPx = 4f;

    /// <summary>Touch/trackpad-fling friction: the per-second exponential SURVIVAL factor applied to the velocity each
    /// tick (v *= FlingDecayPerS^dt_s). Calibrated to WinUI ScrollPresenter's InteractionTracker inertia: its shipping
    /// decay setting corresponds to about 5% velocity survival per SECOND. The fling advances the offset by the EXACT
    /// closed-form integral <see cref="OverscrollPhysics.CoastStep"/> (Δpos = v·(1−decay^dt)/k,
    /// k = −ln(FlingDecayPerS) ≈ 3.0/s),
    /// never a <c>v·dt</c> Riemann step — so a flick coasts the same distance (v0/k) at 60 Hz and 120 Hz. THIS IS THE
    /// FEEL KNOB: tune on-device. The snap-retarget integral below derives k from this value, so it stays consistent
    /// when only this constant changes.</summary>
    public const float FlingDecayPerS = 0.05f;   // per-second velocity survival (k ≈ 3.0/s) — WinUI-like inertia
    /// <summary>Below this speed the fling has settled (px/s): it ends with zero velocity.</summary>
    public const float FlingMinVelocityPxPerS = 13f;
    /// <summary>A snap fling's landing tolerance (px): once this tick's advance would reach/pass the snap value, the
    /// integrator writes the exact snap offset and ends (so a snap fling lands ON the snap rather than v_min/k short).</summary>
    public const float SnapLandEpsPx = 0.5f;
    /// <summary>Legacy WinUiLike wheel exp-ease time constant (ms). SUPERSEDED on the scroll path by the §4.2
    /// velocity-preserving critically-damped chase (<see cref="ScrollTuning.WheelChaseHalflifeMs"/>); retained only so
    /// <see cref="ScrollTuning.WinUiLike"/> keeps its documented value and FlipView/SwipeControl flick-projection
    /// derivations stay stable. The integrator no longer reads it.</summary>
    public const float WheelEaseTauMs = 18f;
    /// <summary>Programmatic bring-into-view spring half-life (ms). A WheelAnimating chase carrying the
    /// <see cref="ScrollState.PhaseProgrammatic"/> flag uses this (slower than a wheel notch): selection-driven viewport
    /// movement should be legible and coordinated, not feel like a wheel notch. Velocity-continuous, critically-damped
    /// (no overshoot), closed-form in dt ⇒ frame-rate-independent + dt-deterministic.</summary>
    public const float ProgrammaticSpringHalflifeMs = 95f;
    /// <summary>Below this chase speed AND within 0.5 DIP of the target, a WheelAnimating chase settles onto the exact
    /// target and ends. px/s.</summary>
    public const float WheelSettleVelPxPerS = 16f;
    /// <summary>A touch fling that REACHES a clamp with at least this residual speed (px/s) converts it into a
    /// rubber-band <see cref="SnapBack"/> bounce (WinUI/iOS) instead of stopping dead; below it a slow drift-into-edge
    /// just settles. TOUCH-descended states only — a WheelAnimating notch hard-clamps at the edge with NO band (§2.2
    /// extent asymmetry).</summary>
    public const float FlingBounceMinPxPerS = FlingMinVelocityPxPerS;

    /// <summary>Compile-time seed clamp for a coast velocity (px/s) — Android max-fling (scroll-feel-rework-v2 §4.3/§4.6
    /// FlingMaxVelocityPxPerS). The seed writer (AppHost.SeedScrollFling) clamps to ±this.</summary>
    public const float FlingMaxVelocityPxPerS = ScrollTuning.FlingMaxVelocityPxPerS;

    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _active = new();
    private readonly HashSet<int> _member = new();
    private readonly HashSet<int> _parkedActive = new();   // armed viewports in a KeepAlive-parked subtree: not ticked, excluded from HasActive
    private readonly Dictionary<int, Conscious> _state = new();   // per-viewport conscious-bar timers (cold; survives drops)

    /// <summary>Per-viewport conscious-state timers + eased FadeT/ExpandT tracks (kept out of ScrollState so the
    /// scene columns stay engine-owned PODs; entries are reclaimed when the bar fully hides or the node dies).</summary>
    private struct Conscious
    {
        public float LaneDwellMs;      // continuous lane hover (toward ExpandBeginMs)
        public float LaneOffDwellMs;   // since lane-leave while still over the viewport (toward ContractBeginMs)
        public float AwayMs;           // since the pointer left the viewport (toward LeaveHideMs for hover-flash bars)
        public bool ScrolledSinceReveal;   // a real scroll happened while visible → WinUI 2s idle hide applies

        // Eased tracks: value animates From → Target over the given duration; ClockMs counts up.
        public float ExpandFrom, ExpandTarget, ExpandClockMs;
        public float FadeFrom, FadeTarget, FadeClockMs;
    }

    // ── §4.1/§2.3 gesture resampler — a SINGLETON POD (only one gesture is latched at a time), fixed inline scalars.
    // Holds the two newest contact samples retained across frames + the last applied position. It is fed by the
    // dispatcher recorder via AppendContactSample / BeginTracking (the P3 wiring — the dispatcher becomes a pure
    // recorder). TouchpadTracking (below) consumes it: resample to frameT − ResampleLatencyMs, emit ONE displacement.
    private struct GestureResampler
    {
        public int Node;            // latched node index (-1 = none)
        public bool Horizontal;
        public double T0, T1;       // the two newest sample times (seconds, QPC-derived)
        public float X0, X1;        // the two newest sample positions (cumulative axis, DIP)
        public int Count;           // samples seen since latch (0/1/2+)
        public float Anchor;        // offset captured at latch
    }
    private GestureResampler _rs = new() { Node = -1 };
    /// <summary>Frame QPC time (seconds) for the current tick — set by the host before <see cref="Tick"/> so the
    /// resampler can target <c>frameT − ResampleLatencyMs</c> (§4.1). 0 = headless / not wired (resampling is vacuous;
    /// TouchpadTracking then falls back to the latest sample position).</summary>
    public double FrameQpcSec { get; set; }

    // Feel knobs seeded from the active ScrollTuning. The public consts above remain the WinUiLike DEFAULTS (and the
    // values FlipView/SwipeControl derive their flick projection from); Tick reads these instance fields so on-device
    // tuning is a value edit, not a logic edit. HeadlessGolden == WinUiLike for all, so determinism gates are unperturbed.
    private readonly float _flingDecayPerS;
    private readonly float _flingSettleVelPxPerS;
    private readonly float _overscrollSpringOmega;

    public ScrollIntegrator(SceneStore scene, ScrollTuning? tuning = null)
    {
        _scene = scene;
        var t = tuning ?? ScrollTuning.WinUiLike;
        _flingDecayPerS = t.FlingDecayPerS;
        _flingSettleVelPxPerS = t.FlingSettleVelocityPxPerS;
        _overscrollSpringOmega = t.OverscrollSpringOmega;
    }
    public Action RequestRerender { get; set; } = static () => { };

    /// <summary>Set by the host: write an ABSOLUTE scroll offset through the Input chokepoint (<c>SetScrollOffset</c> —
    /// clamp + content <c>-offset</c> transform + virtual re-realize), returning whether the viewport actually moved.
    /// Every Fling/WheelAnimating tick routes its integrated offset through here (the §2.1 single chokepoint), NEVER raw
    /// <c>LocalTransform</c> / <c>LayoutDirty</c>; a false return (or an unchanged offset) means a clamp boundary and
    /// ends the motion. Null = no offset write wired (a seeded motion no-ops).</summary>
    public Func<NodeHandle, float, bool>? ScrollWrite { get; set; }

    /// <summary>Set by the host: write the rubber-band overscroll DISPLACEMENT (purely visual; the offset is untouched) and
    /// re-apply the content transform through the Input chokepoint (<c>WriteOverscroll</c>). The phase-7 spring-back routes
    /// the band toward 0 through here, NEVER the offset — the clamp contract is never relaxed. Null = no band write wired.</summary>
    public Action<NodeHandle, float>? OverscrollWrite { get; set; }

    /// <summary>Critically-damped spring frequency for the overscroll release bounce-back (rad/s) — delegates to
    /// <see cref="OverscrollPhysics.SnapBackOmega"/> (the live value flows through <see cref="ScrollTuning"/>).</summary>
    public const float OverscrollSpringOmega = OverscrollPhysics.SnapBackOmega;

    // HasActive EXCLUDES parked viewports (a backgrounded tab mid-fling/transition must not keep the loop awake).
    public bool HasActive => _active.Count - _parkedActive.Count > 0;
    /// <summary>Viewports currently scrolling/transitioning (the armed set) — O(1) census.</summary>
    public int ActiveCount => _active.Count;
    /// <summary>True only for the frame just ticked when at least one viewport is in real USER scroll motion. The recorder
    /// uses this as a global self-blur defer gate: while the user is scrolling one surface, stationary sibling effects
    /// (notably the lyrics DoF rail) should not keep the GPU over budget. Programmatic bring-into-view stays excluded via
    /// <see cref="ScrollState.UserScrollActive"/>.</summary>
    public bool AnyUserScrollActiveThisFrame { get; private set; }

    /// <summary>True only for the frame just ticked when at least one viewport's scroll OFFSET actually advanced this
    /// frame — the precise "content moved under the cursor" signal for the §5.4 stationary-hover re-resolve. Unlike
    /// <see cref="AnyUserScrollActiveThisFrame"/> it is driven by a REAL offset write (an integrator tick chase OR a
    /// synchronous dispatch write, via the ScrollMoved pulse), so it EXCLUDES clamped/settled/band-only frames and
    /// INCLUDES the non-smooth (SmoothScroll=false) synchronous wheel path and programmatic bring-into-view.</summary>
    public bool AnyOffsetWroteThisFrame { get; private set; }

    /// <summary>Quiesce / resume an armed viewport on a KeepAlive park edge: a parked viewport is not ticked (its
    /// mid-fling / target-chase scroll freezes) and is excluded from <see cref="HasActive"/>, so a backgrounded tab
    /// can't keep the frame loop awake; it resumes on un-park. No-op for a viewport that isn't currently armed.</summary>
    public void SetNodeParked(NodeHandle n, bool parked)
    {
        int idx = (int)n.Raw.Index;
        if (!_member.Contains(idx)) { _parkedActive.Remove(idx); return; }
        if (parked) _parkedActive.Add(idx); else _parkedActive.Remove(idx);
    }
    /// <summary>Viewports retaining a conscious-scrollbar timer row (survives drops until fully hidden) — O(1) census.</summary>
    public int ConsciousStateCount => _state.Count;

    public void Arm(NodeHandle n)
    {
        if (n.IsNull || !_scene.IsLive(n)) return;
        int idx = (int)n.Raw.Index;

        // Membership used to be index-only while the active list retained generation-checked handles. If a scroll node
        // was freed and its slot reused before the next animation tick, the stale index rejected Arm for the new
        // viewport: its wheel target changed, but no continuation frame was requested, so scrolling appeared permanently
        // dead after an async branch/page replacement. Prune that stale generation synchronously at the input edge.
        if (_member.Contains(idx))
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var active = _active[i];
                if ((int)active.Raw.Index != idx) continue;
                if (active == n) return;                  // already armed for this exact generation
                _active.RemoveAt(i);                      // stale generation occupying the reused slot
            }
            _member.Remove(idx);
            _parkedActive.Remove(idx);
        }

        if (_member.Add(idx))
        {
            _active.Add(n);
            // A hover/focus re-entry may be the only OS input message after an idle window. Request the continuation
            // frame explicitly so FadeT can advance past its first sample even when no further pointer message arrives.
            RequestRerender();
        }
    }

    /// <summary>Cancel any coast/animation on <paramref name="n"/> (scroll-feel-rework-v2 §2.2): zero motion,
    /// <see cref="Fling"/>/<see cref="WheelAnimating"/> → <see cref="Idle"/>, and hand a live rubber-band off to the
    /// <see cref="SnapBack"/> spring so a re-grab / click over a stretched edge does not erase it. Every PointerDown
    /// (mouse/touch/pen) and every scrollbar grab calls this so a click over a coasting viewport does not drift under the
    /// pointer (fixes R6's dead CancelFling). Idempotent; leaves the node armed so the bar/band still settle. (The real
    /// dispatcher call sites are wired in P3.)</summary>
    public void CancelFling(NodeHandle n)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        if (sc.Phase == Fling || sc.Phase == WheelAnimating)
        {
            sc.Phase = Idle;
            sc.PhaseFlags = 0;
            sc.FlingVelocity = 0f;
            sc.FlingRetargeted = false;
            sc.FlingSnapTarget = float.NaN;
            sc.PendingTargetX = float.NaN;
            sc.PendingTargetY = float.NaN;
        }
        // A live band (finger holding a stretch) hands off to the critically-damped spring — the stretch continues
        // seamlessly rather than being erased.
        if (sc.Overscrolling && sc.OverscrollPx != 0f)
        {
            sc.Overscrolling = false;
            sc.OverscrollVel = 0f;
            sc.Phase = SnapBack;
        }
    }

    /// <summary>Begin a contact-tracking gesture on <paramref name="n"/> (the dispatcher recorder, P3): resets the
    /// singleton resampler and latches the anchor offset. Sets <see cref="ScrollState.Phase"/> to
    /// <see cref="TouchpadTracking"/> and arms the node so the next <see cref="Tick"/> resamples + applies (§2.3).</summary>
    public void BeginTracking(NodeHandle n, bool horizontal, float anchorOffset)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        _rs = new GestureResampler { Node = (int)n.Raw.Index, Horizontal = horizontal, Anchor = anchorOffset };
        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.Phase = TouchpadTracking;
        Arm(n);
    }

    /// <summary>Deposit one per-packet contact sample into the singleton resampler (§3.4): cumulative-within-gesture
    /// axis position (DIP) at <paramref name="qpcSec"/>. Non-monotonic/duplicate stamps are skipped. Fed by the
    /// dispatcher recorder (P3 wiring); the resampler retains the two newest samples across frames.</summary>
    public void AppendContactSample(int nodeIndex, double qpcSec, float axisPos)
    {
        if (_rs.Node != nodeIndex) return;
        if (_rs.Count > 0 && !(qpcSec > _rs.T1)) return;   // non-monotonic / duplicate ⇒ skip
        _rs.T0 = _rs.T1; _rs.X0 = _rs.X1;
        _rs.T1 = qpcSec; _rs.X1 = axisPos;
        if (_rs.Count < 2) _rs.Count++;
    }

    /// <summary>End contact tracking on <paramref name="n"/> (the dispatcher recorder, P3). Clears the latched resampler.</summary>
    public void EndTracking(NodeHandle n)
    {
        if (!n.IsNull && _rs.Node == (int)n.Raw.Index) _rs.Node = -1;
    }

    /// <summary>Reveal a viewport's scrollbar (pointer is over the scrollable area) and reset its idle timer so it
    /// stays up while hovered; lane hover arms the WinUI 400ms-delayed expand.</summary>
    public void Hover(NodeHandle n, bool overScrollbar)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        if (sc.ContentH <= sc.ViewportH + 0.5f && sc.ContentW <= sc.ViewportW + 0.5f) return;  // nothing to scroll
        sc.IdleMs = 0f;
        sc.PointerOver = true;
        sc.PointerOverScrollbar = overScrollbar;
        Arm(n);
    }

    public void Leave(NodeHandle n)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.PointerOver = false;
        sc.PointerOverScrollbar = false;
        Arm(n);
    }

    public void Tick(float dtMs)
    {
        AnyUserScrollActiveThisFrame = false;
        AnyOffsetWroteThisFrame = false;
        // §5 pacing: StopwatchFrameTimeSource deliberately emits one zero-delta frame after a cadence Resync. A zero
        // simulation step cannot move; treating it as a clamp killed newly-seeded motion (the wheel dead-zone). No
        // simulation time elapsed, so preserve every armed state unchanged and advance on the next positive tick. This
        // Resync bail now covers ALL states (the old TickTouchpad fatally lacked it). dt is clamped upstream to 34ms.
        if (_active.Count == 0 || !(dtMs > 0f)) return;
        float dtS = dtMs * 0.001f;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            NodeHandle n = _active[i];
            if (!_scene.IsLive(n)) { Drop(i, n, forget: true); continue; }
            if (_parkedActive.Contains((int)n.Raw.Index)) continue;   // parked (backgrounded) viewport: freeze; resumes on un-park
            int idx = (int)n.Raw.Index;
            ref ScrollState sc = ref _scene.ScrollRef(n);
            bool horizontal = sc.Orientation == 1;
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            byte phase = sc.Phase;
            bool beganProgrammatic = (sc.PhaseFlags & ScrollState.PhaseProgrammatic) != 0;

            bool moved = false;
            bool flinging = false;

            // ── Fling (§4.3): exact-integral coast via OverscrollPhysics.CoastStep (NEVER off += v·dt). ──
            if (phase == Fling && (sc.PhaseFlags & ScrollState.PhaseOsOwned) == 0)
            {
                // Snap retarget (once, on fling entry): pick the snap value the natural decay would settle nearest, then
                // re-solve the velocity so the SAME exponential curve lands EXACTLY there. natural rest = off + v0/k with
                // k = −ln(decay); v0' = (snapTarget − off0)·k. (Touch-fling only; a wheel never enters Fling.)
                if (!sc.FlingRetargeted && sc.HasSnap)
                {
                    sc.FlingRetargeted = true;
                    float k = -MathF.Log(_flingDecayPerS);
                    float natural = off + sc.FlingVelocity / k;
                    float zr = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
                    float maxOffS = horizontal ? MathF.Max(0f, sc.ContentW * zr - sc.ViewportW) : MathF.Max(0f, sc.ContentH * zr - sc.ViewportH);
                    natural = Math.Clamp(natural, 0f, maxOffS);
                    float snapTarget = ScrollSnap.Snap(in sc, natural, impulse: true, sc.FlingFromOffset);
                    snapTarget = Math.Clamp(snapTarget, 0f, maxOffS);
                    sc.FlingVelocity = (snapTarget - off) * k;
                    sc.FlingSnapTarget = snapTarget;
                    if (FluentGpu.Foundation.ScrollTrace.On)
                        FluentGpu.Foundation.ScrollTrace.AnimEvent(idx, 2, natural, snapTarget, sc.FlingVelocity);
                }

                float v = sc.FlingVelocity;
                float dpos = OverscrollPhysics.CoastStep(ref v, dtMs, _flingDecayPerS);   // v now decayed; dpos = exact ∫ v·decay^τ

                // A SNAP fling terminates on DISTANCE-TO-TARGET, not on velocity, so it lands ON the snap (no drift).
                bool snapLanding = false;
                if (sc.FlingRetargeted && sc.HasSnap && !float.IsNaN(sc.FlingSnapTarget))
                {
                    float target = sc.FlingSnapTarget;
                    float nextOff = off + dpos;
                    bool reached = MathF.Abs(target - off) <= SnapLandEpsPx
                                || (sc.FlingVelocity >= 0f ? nextOff >= target - SnapLandEpsPx : nextOff <= target + SnapLandEpsPx)
                                || MathF.Abs(v) < _flingSettleVelPxPerS;
                    if (reached)
                    {
                        moved = ScrollWrite?.Invoke(n, target) ?? false;   // land exactly on the snap (clamp-safe)
                        sc.Phase = Idle;
                        sc.FlingVelocity = 0f;
                        sc.FlingSnapTarget = float.NaN;
                        off = horizontal ? sc.OffsetX : sc.OffsetY;
                        snapLanding = true;
                    }
                }

                if (!snapLanding)
                {
                    float requested = off + dpos;
                    float zr = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
                    float maxOffS = horizontal ? MathF.Max(0f, sc.ContentW * zr - sc.ViewportW) : MathF.Max(0f, sc.ContentH * zr - sc.ViewportH);
                    bool hitClamp = requested < 0f || requested > maxOffS;
                    moved = ScrollWrite?.Invoke(n, requested) ?? false;      // through the chokepoint: clamp + transform + re-realize
                    off = horizontal ? sc.OffsetX : sc.OffsetY;              // re-read the clamped position
                    if (hitClamp || !moved || MathF.Abs(v) < _flingSettleVelPxPerS)     // a clamp boundary or a settle ends the fling
                    {
                        if (FluentGpu.Foundation.ScrollTrace.On)
                            FluentGpu.Foundation.ScrollTrace.AnimEvent(idx, 0, off, v, (hitClamp ? 1f : 0f) + (moved ? 2f : 0f));
                        // §4.5: a touch fling that REACHES a clamp with residual speed hands the momentum to the SnapBack
                        // rubber-band (WinUI/iOS) instead of stopping dead (§2.2 extent asymmetry: contact-descended only).
                        if ((hitClamp || !moved) && MathF.Abs(v) >= FlingBounceMinPxPerS && !sc.Overscrolling)
                        {
                            float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
                            float band = sc.OverscrollPx, bandVel = sc.OverscrollVel;
                            sc.OverscrollReleaseOmega = 0f;   // touch fling uses the profile's direct-touch spring
                            // §A.1 velocity-only seed: the bounce starts at the CURRENT stretch (band, 0 here) and swings
                            // in via v0·t·e^(−ωt), peak capped to Cpeak·d — no position seed (F6 teleport is gone on the
                            // fallback-fling path too, unified through the rewritten SeedFromEdgeMomentum).
                            OverscrollPhysics.SeedFromEdgeMomentum(ref bandVel, v, viewport);
                            sc.OverscrollVel = bandVel;
                            sc.Phase = SnapBack;
                            if (FluentGpu.Foundation.ScrollTrace.On)
                                FluentGpu.Foundation.ScrollTrace.AnimEvent(idx, 1, band, bandVel, v);
                        }
                        else sc.Phase = Idle;
                        sc.FlingVelocity = 0f;
                    }
                    else
                    {
                        sc.FlingVelocity = v;
                        flinging = true;
                    }
                }
            }
            // ── WheelAnimating (§4.2): velocity-preserving critically-damped chase to the accumulated PendingTarget. ──
            else if (phase == WheelAnimating)
            {
                float pending = horizontal ? sc.PendingTargetX : sc.PendingTargetY;
                if (float.IsNaN(pending))
                {
                    sc.Phase = Idle; sc.FlingVelocity = 0f; sc.PhaseFlags = 0;
                }
                else if ((sc.PhaseFlags & ScrollState.PhaseImmediate) != 0)
                {
                    // §2.1 exact absolute-offset intent (scrollbar thumb-drag / pinch focal / edge auto-scroll): the recorder
                    // wrote PendingTarget; apply it verbatim THIS tick (no spring) so a synchronous manipulation still lands
                    // the same frame it was recorded — the sole offset writer stays the integrator, with no manipulation lag.
                    moved = ScrollWrite?.Invoke(n, pending) ?? false;
                    off = horizontal ? sc.OffsetX : sc.OffsetY;
                    sc.Phase = Idle; sc.FlingVelocity = 0f; sc.PhaseFlags = 0;
                    if (horizontal) sc.PendingTargetX = float.NaN; else sc.PendingTargetY = float.NaN;
                }
                else
                {
                    bool programmatic = (sc.PhaseFlags & ScrollState.PhaseProgrammatic) != 0;
                    float newOff, vel;
                    if (programmatic && sc.ProgrammaticZeta > 0f && sc.ProgrammaticZeta < 0.999f && sc.ProgrammaticOmega > 0f)
                    {
                        // UNDERDAMPED closed form (ζ<1) — the per-viewport override (ScrollState.ProgrammaticZeta/Omega):
                        // exact per-tick step (dt-deterministic like the ζ=1 branch), velocity-continuous across retargets.
                        float z = sc.ProgrammaticZeta, w0 = sc.ProgrammaticOmega;
                        float wd = w0 * MathF.Sqrt(1f - z * z);
                        float j0 = off - pending;
                        float v0 = sc.FlingVelocity;
                        float e = MathF.Exp(-z * w0 * dtS);
                        float cosD = MathF.Cos(wd * dtS), sinD = MathF.Sin(wd * dtS);
                        float a = (v0 + z * w0 * j0) / wd;
                        float x = e * (j0 * cosD + a * sinD);
                        vel = -z * w0 * x + e * wd * (a * cosD - j0 * sinD);
                        newOff = pending + x;
                        sc.FlingVelocity = vel;
                    }
                    else
                    {
                        float halflifeMs = programmatic ? ProgrammaticSpringHalflifeMs : ScrollTuning.WheelChaseHalflifeMs;
                        float y = 1.3862944f / (halflifeMs * 0.001f);   // 2·ln2 / halflife(s) — critically-damped (ζ=1)
                        vel = sc.FlingVelocity;
                        float j0 = off - pending;
                        float j1 = vel + j0 * y;
                        float e = MathF.Exp(-y * dtS);
                        newOff = e * (j0 + j1 * dtS) + pending;
                        vel = e * (vel - j1 * y * dtS);
                        sc.FlingVelocity = vel;
                    }

                    if (MathF.Abs(newOff - pending) < 0.5f && MathF.Abs(vel) < WheelSettleVelPxPerS)
                    {
                        moved = ScrollWrite?.Invoke(n, pending) ?? false;   // land exactly on the accumulated target
                        sc.Phase = Idle; sc.FlingVelocity = 0f; sc.PhaseFlags = 0;
                        if (horizontal) sc.PendingTargetX = float.NaN; else sc.PendingTargetY = float.NaN;
                        off = horizontal ? sc.OffsetX : sc.OffsetY;
                    }
                    else
                    {
                        moved = ScrollWrite?.Invoke(n, newOff) ?? false;
                        off = horizontal ? sc.OffsetX : sc.OffsetY;
                        // Hard-stop at the extent (§2.2): a clamped write that no longer advances ends the chase (no band).
                        if (!moved && MathF.Abs(vel) < WheelSettleVelPxPerS)
                        {
                            sc.Phase = Idle; sc.FlingVelocity = 0f; sc.PhaseFlags = 0;
                            if (horizontal) sc.PendingTargetX = float.NaN; else sc.PendingTargetY = float.NaN;
                        }
                    }
                }
            }
            // ── TouchpadTracking / Overscroll (§4.1/§4.4): resample the latched contact position to frameT − 5ms, emit ONE
            // displacement. The dispatcher is a pure recorder (P3) — it deposits contact samples via AppendContactSample and
            // this is the SOLE offset/band writer for the gesture. Overscroll is TouchpadTracking past a content edge: the
            // finger keeps driving the band 1:1, so this branch stays live across the TouchpadTracking↔Overscroll flip
            // (else a past-edge sample would freeze the band).
            // A per-node direct-touch (WM_POINTER) pan reads its OWN PendingRawOffset (concurrent multi-touch stays
            // independent); a PTP/touch-contract gesture reads the singleton resampler at frameT−5ms. Both split the same
            // raw offset into a clamped offset + rubber-band below (the SOLE offset/band writer for the gesture).
            else if ((phase == TouchpadTracking || phase == Overscroll)
                     && (((sc.PhaseFlags & ScrollState.PhaseTouchPan) != 0 && !float.IsNaN(sc.PendingRawOffset))
                         || ((sc.PhaseFlags & ScrollState.PhaseTouchPan) == 0 && _rs.Node == idx && _rs.Count > 0)))
            {
                bool touchPan = (sc.PhaseFlags & ScrollState.PhaseTouchPan) != 0;
                float xStar = touchPan ? 0f : ResampleContact(FrameQpcSec);
                float zr = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
                float maxOffS = horizontal ? MathF.Max(0f, sc.ContentW * zr - sc.ViewportW) : MathF.Max(0f, sc.ContentH * zr - sc.ViewportH);
                float rawOffset = touchPan ? sc.PendingRawOffset : _rs.Anchor + xStar;
                float clamped = Math.Clamp(rawOffset, 0f, maxOffS);
                moved = ScrollWrite?.Invoke(n, clamped) ?? false;
                off = horizontal ? sc.OffsetX : sc.OffsetY;
                float excess = rawOffset < 0f ? rawOffset : rawOffset > maxOffS ? rawOffset - maxOffS : 0f;
                if (excess != 0f && !touchPan && (sc.PhaseFlags & ScrollState.PhaseOsOwned) != 0)
                {
                    // OS-owned momentum (DManip INERTIA) reached the edge. The OS tail decays for up to ~2s and no
                    // finger is down — riding it 1:1 into the band holds the stretch frozen until MomentumEnd
                    // (observed on-device). Convert the tail's instantaneous velocity into the SnapBack bounce NOW
                    // (parity with the engine fling hitting the clamp, §2.2) and leave TouchpadTracking/Overscroll so
                    // the remaining momentum samples of this stream are ignored.
                    float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
                    // §A.2 edge-crossing tail velocity — the SAMPLE-TIMESTAMP slope (X1−X0)/(T1−T0) (WebKit
                    // eventDelta/eventDt), NEVER this-frame displacement (xStar−XAppliedLast)/dt. A lift-at-stretch tick
                    // has no new sample, so the per-frame form reads ≈0 and a 0 seed erased the band (F5's one-frame 70px
                    // snap). Stale-tail zeroing: no fresh sample within AssumeStoppedMs ⇒ the tail stopped (Android 40ms;
                    // WebKit uses 100ms — one unified gate with the fling estimator). Same ±8000 clamp.
                    float vOs = 0f;
                    if (_rs.Count >= 2)
                    {
                        double span = _rs.T1 - _rs.T0;
                        if (span > 0.0)
                        {
                            vOs = (float)((_rs.X1 - _rs.X0) / span);
                            if (FrameQpcSec > 0.0 && (FrameQpcSec - _rs.T1) > ScrollTuning.AssumeStoppedMs / 1000.0) vOs = 0f;
                            vOs = Math.Clamp(vOs, -ScrollTuning.FlingMaxVelocityPxPerS, ScrollTuning.FlingMaxVelocityPxPerS);
                        }
                    }
                    float band = sc.OverscrollPx, bandVel = sc.OverscrollVel;
                    sc.OverscrollReleaseOmega = 0f;   // profile spring (same as the fling→edge hand-off)
                    // §A.1 velocity-only seed: seed ONLY from a tail genuinely moving INTO the edge (sign + settle gate),
                    // never shrinking a live stretch — the bounce starts at the CURRENT stretch and swings in via
                    // v0·t·e^(−ωt) (peak capped to Cpeak·d), so no position seed can teleport the band (F6). One code path
                    // with the fallback-fling hand-off — the inlined vCap/coupling arithmetic is gone.
                    if (MathF.Abs(vOs) >= _flingSettleVelPxPerS && MathF.Sign(vOs) == MathF.Sign(excess))
                        OverscrollPhysics.SeedFromEdgeMomentum(ref bandVel, vOs, viewport);
                    sc.OverscrollVel = bandVel;
                    sc.Overscrolling = false;         // the spring owns the band — nothing is holding it
                    sc.Phase = SnapBack;
                    OverscrollWrite?.Invoke(n, band);
                    moved = true;
                    if (FluentGpu.Foundation.ScrollTrace.On)
                        FluentGpu.Foundation.ScrollTrace.AnimEvent(idx, 1, band, bandVel, vOs);
                }
                else if (excess != 0f)
                {
                    float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
                    sc.OverscrollPx = OverscrollPhysics.BandFromExcess(excess, viewport);
                    sc.Overscrolling = sc.OverscrollPx != 0f;
                    sc.OverscrollVel = 0f;
                    OverscrollWrite?.Invoke(n, sc.OverscrollPx);
                    sc.Phase = sc.Overscrolling ? Overscroll : TouchpadTracking;
                    moved = true;
                }
                else if (sc.OverscrollPx != 0f || sc.Overscrolling)
                {
                    // Finger returned in-range: retire the band and resume plain TouchpadTracking (else it stays Overscroll
                    // with a stale band).
                    sc.OverscrollPx = 0f;
                    sc.Overscrolling = false;
                    sc.OverscrollVel = 0f;
                    OverscrollWrite?.Invoke(n, 0f);
                    sc.Phase = TouchpadTracking;
                    moved = true;
                }
                else if (phase == Overscroll) sc.Phase = TouchpadTracking;
            }

            // ── rubber-band overscroll spring (SnapBack §4.5) ─────────────────────────────────────────────
            // Touch = the finger drives OverscrollPx 1:1 (Overscrolling) then a critically-damped spring pulls it to 0 on
            // release. All writes are TransformDirty-only through OverscrollWrite; the offset is never touched.
            bool bandActive = false;
            if ((sc.OverscrollPx != 0f || sc.OverscrollVel != 0f) && !sc.Overscrolling)
            {
                float p = sc.OverscrollPx, vsp = sc.OverscrollVel;
                float omega = sc.OverscrollReleaseOmega > 0f ? sc.OverscrollReleaseOmega : _overscrollSpringOmega;
                bool settled = OverscrollPhysics.StepSpring(ref p, ref vsp, dtMs, omega);
                sc.OverscrollVel = vsp;
                OverscrollWrite?.Invoke(n, p);
                if (settled)
                {
                    sc.OverscrollReleaseOmega = 0f;
                    if (sc.Phase == SnapBack) sc.Phase = Idle;
                    if (FluentGpu.Foundation.ScrollTrace.On) FluentGpu.Foundation.ScrollTrace.AnimEvent(idx, 3, p, vsp, omega);
                }
                bandActive = !settled;
            }
            else if (sc.Overscrolling && sc.OverscrollPx != 0f)
            {
                bandActive = true;   // a touch finger is holding the band displaced — stay armed (the indicator shows through it)
            }

            float tgt = horizontal ? sc.TargetX : sc.TargetY;

            // DIAGNOSTIC (FG_SCROLL_TRACE): per-frame physics state of this scroll node WHEN anything is moving.
            if (FluentGpu.Foundation.ScrollTrace.On)
            {
                if (moved || flinging || bandActive || sc.OverscrollPx != 0f || MathF.Abs(tgt - off) > 0.5f)
                    FluentGpu.Foundation.ScrollTrace.AnimTick(idx, sc.Phase, off, tgt,
                        sc.FlingVelocity, sc.OverscrollPx, sc.OverscrollVel, dtMs);
            }

            // ── conscious scrollbar state machine (WinUI timings; see class remarks) ──────────────────────
            int key = idx;
            _state.TryGetValue(key, out Conscious cs);
            // A SYNCHRONOUS offset write this frame (touch content-pan / thumb-drag / edge auto-scroll — Offset == Target, so
            // |tgt-off| can't see it) pulses ScrollMoved; consume it (one-frame) and count it as motion so the thin indicator
            // reveals THROUGH a pan / wheel chase, then idle-hides on the pulse stopping.
            bool syncMoved = sc.ScrollMoved;
            sc.ScrollMoved = false;
            bool movingNow = flinging || syncMoved || bandActive || MathF.Abs(tgt - off) > 0.5f;
            // Persist the record-phase self-blur (DoF) defer gate: TRUE only while this is a USER scroll in motion this
            // frame (TouchpadTracking / Fling incl. OsOwned / WheelAnimating / SnapBack), NOT a Programmatic bring-into-view.
            sc.UserScrollActive = movingNow && !beganProgrammatic && (sc.PhaseFlags & ScrollState.PhaseProgrammatic) == 0;
            if (sc.UserScrollActive) AnyUserScrollActiveThisFrame = true;
            if (moved || syncMoved) AnyOffsetWroteThisFrame = true;   // §5.4: a real offset advance (tick chase OR synchronous ScrollMoved pulse) — the stationary-hover re-resolve gate
            bool over = sc.PointerOver;
            bool lane = sc.PointerOverScrollbar;

            // Below the overflow floor the content effectively fits (a fractional-layout remainder) — never arm the bar,
            // so a hover or a tiny pan can't flicker it in. Real overflow (≥ MinBarOverflowPx) behaves as before.
            float overflow = (sc.Orientation == 1 ? sc.ContentW - sc.ViewportW : sc.ContentH - sc.ViewportH);
            bool scrollable = overflow > MinBarOverflowPx;

            if (scrollable && (moved || movingNow)) cs.ScrolledSinceReveal = true;

            // Expand/contract dwell timers (ScrollBarExpandBeginTime 400ms / ScrollBarContractBeginTime 500ms).
            if (lane)
            {
                cs.LaneDwellMs = MathF.Min(ExpandBeginMs, cs.LaneDwellMs + dtMs);
                cs.LaneOffDwellMs = 0f;
                if (cs.LaneDwellMs >= ExpandBeginMs && cs.ExpandTarget != 1f) StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, sc.ExpandT, 1f);
            }
            else
            {
                cs.LaneDwellMs = 0f;
                if (cs.ExpandTarget != 0f || sc.ExpandT > 0f)
                {
                    if (over)
                    {
                        cs.LaneOffDwellMs += dtMs;
                        if (cs.LaneOffDwellMs >= ContractBeginMs && cs.ExpandTarget != 0f)
                            StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, sc.ExpandT, 0f);
                    }
                    else if (cs.ExpandTarget != 0f)
                    {
                        // Viewport-leave: contract immediately (engine-deliberate; class remarks).
                        StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, sc.ExpandT, 0f);
                    }
                }
            }

            // Visibility: visible while moving / lane / over (the MouseIndicator hold); hide after the away/idle delay.
            sc.IdleMs = (movingNow || over) ? 0f : sc.IdleMs + dtMs;
            cs.AwayMs = over ? 0f : cs.AwayMs + dtMs;
            bool show = scrollable && (movingNow || over || lane);
            bool hideDue = !show &&
                (cs.ScrolledSinceReveal ? sc.IdleMs >= IdleHideMs       // WinUI ScrollBarContractDelay 2s
                                        : cs.AwayMs >= LeaveHideMs);    // hover-flash retire (≈667ms; class remarks)
            float fadeWant = show ? 1f : hideDue ? 0f : sc.FadeT > 0f ? 1f : 0f;
            if (fadeWant != cs.FadeTarget) StartTrack(ref cs.FadeFrom, ref cs.FadeTarget, ref cs.FadeClockMs, sc.FadeT, fadeWant);

            // Advance the eased tracks: expand = 167ms KeySpline(0,0,0,1) → FluentPopOpen; fade = 83ms linear.
            float oldExpand = sc.ExpandT, oldFade = sc.FadeT;
            sc.ExpandT = Advance(ref cs.ExpandFrom, cs.ExpandTarget, ref cs.ExpandClockMs, ExpandContractMs, dtMs, Easing.FluentPopOpen, sc.ExpandT);
            sc.FadeT = Advance(ref cs.FadeFrom, cs.FadeTarget, ref cs.FadeClockMs, FadeMs, dtMs, Easing.Linear, sc.FadeT);
            if (sc.ExpandT != oldExpand || sc.FadeT != oldFade) _scene.Mark(n, NodeFlags.PaintDirty);

            bool expandSettled = sc.ExpandT == cs.ExpandTarget;
            bool fadeSettled = sc.FadeT == cs.FadeTarget;
            bool fullyHidden = fadeSettled && sc.FadeT == 0f && expandSettled && sc.ExpandT == 0f;
            if (fullyHidden) cs = default;   // reset all timers — the next reveal starts a fresh conscious cycle
            _state[key] = cs;

            // A pending dwell that will still change state keeps the node armed (timers need ticks to elapse).
            bool dwellPending =
                (lane && cs.LaneDwellMs < ExpandBeginMs && cs.ExpandTarget != 1f) ||
                (!lane && over && sc.ExpandT > 0f && cs.ExpandTarget != 0f) ||
                (!show && sc.FadeT > 0f && !hideDue);

            if (!movingNow && expandSettled && fadeSettled && !dwellPending)
            {
                Drop(i, n, forget: fullyHidden);
            }   // fully settled: statically visible under hover, or hidden after the delays
        }
    }

    /// <summary>Resample the latched contact position to <c>frameT − ResampleLatencyMs</c> (scroll-feel-rework-v2 §4.1):
    /// interpolate between the two newest samples when the target is bracketed (preferred), else extrapolate capped at
    /// <see cref="ScrollTuning.ResampleMaxPredictionMs"/> AND 50% of the last inter-event delta. A single sample (or a
    /// vacuous headless 0-clock) returns the latest position (no synthesis). Pure; 0-alloc.</summary>
    private float ResampleContact(double frameQpcSec)
    {
        if (_rs.Count < 2 || frameQpcSec <= 0.0) return _rs.X1;   // one sample / headless: use the latest position
        double t0 = _rs.T0, t1 = _rs.T1;
        float x0 = _rs.X0, x1 = _rs.X1;
        double span = t1 - t0;
        if (span < ScrollTuning.ResampleMinDeltaMs / 1000.0) return x1;   // degenerate spacing guard
        double targetT = frameQpcSec - ScrollTuning.ResampleLatencyMs / 1000.0;
        if (targetT <= t1)
        {
            double f = (targetT - t0) / span;                              // interpolate (preferred)
            return (float)(x0 + (x1 - x0) * f);
        }
        double predCap = Math.Min(ScrollTuning.ResampleMaxPredictionMs / 1000.0, 0.5 * span);
        double pred = Math.Min(targetT - t1, predCap);                     // extrapolate, capped (§4.1)
        return (float)(x1 + (x1 - x0) / span * pred);
    }

    /// <summary>Retarget an eased track from the live value (mid-flight retargets stay continuous).</summary>
    private static void StartTrack(ref float from, ref float target, ref float clockMs, float current, float to)
    {
        from = current;
        target = to;
        clockMs = 0f;
    }

    private static float Advance(ref float from, float target, ref float clockMs, float durationMs, float dtMs, Easing easing, float current)
    {
        if (current == target) return current;
        clockMs += dtMs;
        float t = Math.Clamp(clockMs / MathF.Max(1f, durationMs), 0f, 1f);
        if (t >= 1f) return target;
        return from + (target - from) * Easings.Ease(easing, t);
    }

    private void Drop(int i, NodeHandle n, bool forget)
    {
        _member.Remove((int)n.Raw.Index);
        _parkedActive.Remove((int)n.Raw.Index);
        _active.RemoveAt(i);
        if (forget) _state.Remove((int)n.Raw.Index);
    }

    /// <summary>Symmetric teardown when a scene slot is FREED (wired to <see cref="SceneStore.OnFreeIndex"/>): drop the
    /// index-keyed conscious-bar timer row so a freed viewport leaves no dormant state the NEXT viewport reusing that
    /// slot would inherit. Remove the slot from every membership structure synchronously: <c>_member</c> is keyed only
    /// by index, so waiting for the generation-checked active handle to self-prune can suppress arming the replacement
    /// viewport in the same frame. 0-alloc; a no-op when the slot had no row.</summary>
    public void ClearForIndex(int index)
    {
        _state.Remove(index);
        _member.Remove(index);
        _parkedActive.Remove(index);
        for (int i = _active.Count - 1; i >= 0; i--)
            if ((int)_active[i].Raw.Index == index) _active.RemoveAt(i);
    }
}
