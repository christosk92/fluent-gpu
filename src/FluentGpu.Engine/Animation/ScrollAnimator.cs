using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Smooth scrolling (phase 7): eases each armed viewport's live offset toward its target (the WinUI inertial wheel feel),
/// applies the content's -offset transform, re-realizes the virtual window at guard-band edges, and drives the
/// auto-hiding scrollbar's WinUI "conscious" state machine. Only viewports actively scrolling/transitioning are ticked;
/// zero work / zero alloc once everything settles (<see cref="HasActive"/> == false).
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
public sealed class ScrollAnimator
{
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
    /// tick (v *= FlingDecayPerS^dt_s). NOT WinUI's 0.95: WinUI's <c>c_scrollPresenterDefaultInertiaDecayRate</c>
    /// (ScrollPresenter.cpp:31) is the InteractionTracker's <c>PositionInertiaDecayRate</c> param, integrated inside the
    /// closed Windows.UI.Composition curve — far more frictional than 0.95 applied per SECOND here. 0.95/s is
    /// near-frictionless (k = −ln0.95 ≈ 0.05/s ⇒ coast v0/k ≈ 19.5·v0 ⇒ "coasts for tens of seconds" — the wrong-inertia
    /// bug). The decay constant is <c>k = −ln(FlingDecayPerS)</c> per second; a fling's asymptotic coast is <c>v0/k</c>
    /// and it settles (≤ <see cref="FlingMinVelocityPxPerS"/>) after ≈ <c>ln(v0/v_min)/k</c> s. At 0.03 ⇒ k ≈ 3.5/s ⇒ a
    /// 1600 px/s flick coasts ~460 px and settles in ~1 s — a WinUI-like glide. THIS IS THE FEEL KNOB: tune on-device
    /// against a real WinUI ScrollViewer. The snap-retarget integral below derives k from this value, so it stays
    /// consistent when only this constant changes.</summary>
    public const float FlingDecayPerS = 0.03f;   // per-second velocity survival (k ≈ 3.5/s); tune on-device. WAS 0.95 (near-frictionless — the bug)
    /// <summary>Below this speed the fling has settled: it reverts to TargetChase with zero velocity (a slow drift
    /// reads as stopped, and snapping idle keeps the frame loop quiet). px/s.</summary>
    public const float FlingMinVelocityPxPerS = 40f;
    /// <summary>A snap fling's landing tolerance: once the decay's remaining asymptotic travel is within this many DIP of
    /// the snap value, the integrator writes the exact snap offset and ends (so a snap fling lands ON the snap rather
    /// than v_min/k short). Sub-pixel; deterministic across the integrator dt sweep. px.</summary>
    public const float SnapLandEpsPx = 0.5f;
    /// <summary>Mouse-wheel/scrollbar/keyboard TargetChase smoothing time constant (ms): the eased offset closes ~(1−1/e)
    /// of the gap to the Target each τ (kOff = 1−exp(−dt/τ) per frame). Precision touchpad packets are physically
    /// classified and bypass this target chase for synchronous tracking + a measured kinetic tail.
    /// WAS 40ms: still visibly trailed a fast mouse wheel (~150ms to settle ⇒ "doesn't match my hand"). 18ms settles in
    /// ~3 frames — crisp 1:1-feeling wheel that still spreads a clicky notch over a frame or two (no harsh single-frame jump).</summary>
    public const float WheelEaseTauMs = 18f;
    /// <summary>Mouse-wheel MOMENTUM friction (per-second velocity survival, the wheel-fling analogue of
    /// <see cref="FlingDecayPerS"/>). Heavier than a touch fling so a single notch settles crisply (~0.25s) while a fast
    /// spin still coasts. k = −ln(decay); the seed picks v0 = notchDip·k so one notch coasts ≈ one notch. Env-tunable via
    /// FG_WHEEL_DECAY for on-device dialing without a rebuild.</summary>
    public static readonly float WheelFlingDecayPerS = ParseEnvFloat("FG_WHEEL_DECAY", 0.00002f);
    private static float ParseEnvFloat(string name, float fallback)
        => float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f ? v : fallback;
    /// <summary>A touch fling that REACHES a clamp with at least this residual speed converts it into a rubber-band
    /// overscroll bounce (WinUI/iOS) instead of stopping dead at the edge; below it a slow drift-into-edge just settles.
    /// px/s. TOUCH FlingMode ONLY — a mouse-wheel WheelFlingMode coast hard-clamps at the edge with NO band (the bounce
    /// is gated on FlingMode), matching the Windows mouse-wheel feel.</summary>
    public const float FlingBounceMinPxPerS = 50f;

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

    /// <summary><see cref="ScrollState.ScrollMode"/> value for an active touch fling (vs 0 = TargetChase).</summary>
    private const byte FlingMode = 1;
    /// <summary>Mouse-wheel momentum fling (browser-like coast): seeded by InputDispatcher's smooth-wheel path, decayed by
    /// <see cref="WheelFlingDecayPerS"/>, and — unlike a touch fling — hard-clamps at the edge with NO rubber-band/snap.</summary>
    private const byte WheelFlingMode = 3;

    // Feel knobs seeded from the active ScrollTuning. The public consts above remain the WinUiLike DEFAULTS (and the
    // values FlipView/SwipeControl derive their flick projection from); Tick reads these instance fields so on-device
    // tuning is a value edit, not a logic edit. HeadlessGolden == WinUiLike for all, so determinism gates are unperturbed.
    private readonly float _wheelEaseTauMs;
    private readonly float _flingDecayPerS;
    private readonly float _flingSettleVelPxPerS;

    public ScrollAnimator(SceneStore scene, ScrollTuning? tuning = null)
    {
        _scene = scene;
        var t = tuning ?? ScrollTuning.WinUiLike;
        _wheelEaseTauMs = t.WheelEaseTauMs;
        _flingDecayPerS = t.FlingDecayPerS;
        _flingSettleVelPxPerS = t.FlingSettleVelocityPxPerS;
    }
    public Action RequestRerender { get; set; } = static () => { };

    /// <summary>Set by the host: write an ABSOLUTE scroll offset through the Input chokepoint (<c>SetScrollOffset</c> —
    /// clamp + content <c>-offset</c> transform + virtual re-realize), returning whether the viewport actually moved.
    /// A Fling tick routes its integrated offset through here, NEVER raw <c>LocalTransform</c> / <c>LayoutDirty</c>; a
    /// false return (or an unchanged offset) means a clamp boundary and ends the fling. Cross-assembly seam wired in the
    /// host, the <c>AutoScrollBy</c>/<c>OnScrollArmed</c> precedent. Null = no fling write wired (a seeded fling no-ops).</summary>
    public Func<NodeHandle, float, bool>? ScrollWrite { get; set; }

    /// <summary>Set by the host: write the rubber-band overscroll DISPLACEMENT (purely visual; the offset is untouched) and
    /// re-apply the content transform through the Input chokepoint (<c>WriteOverscroll</c>). The phase-7 spring-back routes
    /// the band toward 0 through here, NEVER the offset — the clamp contract is never relaxed. Null = no band write wired.</summary>
    public Action<NodeHandle, float>? OverscrollWrite { get; set; }

    /// <summary>Critically-damped spring frequency for the overscroll release bounce-back (rad/s) — delegates to
    /// <see cref="OverscrollPhysics.SpringOmegaRadPerS"/>.</summary>
    public const float OverscrollSpringOmega = 42f;

    // HasActive EXCLUDES parked viewports (a backgrounded tab mid-fling/transition must not keep the loop awake).
    public bool HasActive => _active.Count - _parkedActive.Count > 0;
    /// <summary>Viewports currently scrolling/transitioning (the armed set) — O(1) census.</summary>
    public int ActiveCount => _active.Count;

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
        if (!n.IsNull && _scene.IsLive(n) && _member.Add((int)n.Raw.Index)) _active.Add(n);
    }

    /// <summary>Cancel an active touch fling on <paramref name="n"/> (revert to TargetChase, zero the velocity) — the
    /// explicit programmatic stop. The WIRED same-axis-wheel cancel is the <see cref="Tick"/> auto-detect: a smooth
    /// wheel sets Target away from Offset, which the next fling tick sees and yields to TargetChase; this method covers
    /// callers that want to abort a fling outright. Idempotent; leaves the node armed so the bar still settles.</summary>
    public void CancelFling(NodeHandle n)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.ScrollMode = 0;
        sc.FlingVelocity = 0f;
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
        if (_active.Count == 0) return;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            NodeHandle n = _active[i];
            if (!_scene.IsLive(n)) { Drop(i, n, forget: true); continue; }
            if (_parkedActive.Contains((int)n.Raw.Index)) continue;   // parked (backgrounded) viewport: freeze; resumes on un-park
            ref ScrollState sc = ref _scene.ScrollRef(n);
            bool horizontal = sc.Orientation == 1;
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            float tgt = horizontal ? sc.TargetX : sc.TargetY;

            // ── Fling: friction-decay inertia (touch-up seeds FlingVelocity + ScrollMode=Fling). A same-axis wheel sets
            // Target away from Offset (SetScrollOffset keeps Target==Offset every fling tick) → yield to TargetChase. ──
            bool moved;
            bool flinging = false;
            if ((sc.ScrollMode == FlingMode || sc.ScrollMode == WheelFlingMode) && MathF.Abs(tgt - off) < 0.5f)
            {
                // Snap retarget (once, on fling entry): pick the snap value the natural decay would settle nearest, then
                // re-solve the velocity so the SAME exponential curve lands EXACTLY there. With v *= decay^dt per tick the
                // total remaining travel from off0 is the decay integral v0·∫₀^∞ decay^t dt = v0/k where k = −ln(decay)
                // (the closed form FlipView.ProjectDivisor uses over a bounded window). So natural rest = off0 + v0/k;
                // choosing snapTarget and solving v0' = (snapTarget − off0)·k makes the unchanged decay asymptote to
                // snapTarget. WinUI adjusts the inertia destination at fling START the same way (it sets the snap as the
                // InteractionTracker resting value before inertia runs — ScrollPresenter.cpp:2243 ignored-value +
                // SnapPoint.cpp resting-point expression). Impulse mode (a flick) ignores the start snap so the fling
                // always advances at least one snap (ScrollSnap.Snap).
                if (sc.ScrollMode == FlingMode && !sc.FlingRetargeted && sc.HasSnap)   // snap-retarget is touch-fling only; a wheel fling never snaps
                {
                    sc.FlingRetargeted = true;
                    float k = -MathF.Log(_flingDecayPerS);                 // per-second decay rate (≈0.0513/s for 0.95)
                    float natural = off + sc.FlingVelocity / k;           // unsnapped asymptotic rest
                    // Clamp the natural rest to the reachable offset range BEFORE snapping — a flick can't coast past the
                    // content, so a snap beyond the clamp is meaningless (and would re-solve to an absurd velocity that
                    // overshoots into the clamp and never settles). WinUI clamps the inertia end the same way
                    // (ComputeEndOfInertiaPosition, ScrollPresenter.cpp:1366-1367: end = clamp(end, minPos, maxPos)).
                    float zr = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
                    float maxOff = horizontal ? MathF.Max(0f, sc.ContentW * zr - sc.ViewportW) : MathF.Max(0f, sc.ContentH * zr - sc.ViewportH);
                    natural = Math.Clamp(natural, 0f, maxOff);
                    float snapTarget = ScrollSnap.Snap(in sc, natural, impulse: true, sc.FlingFromOffset);
                    snapTarget = Math.Clamp(snapTarget, 0f, maxOff);     // a snap step (impulse) must not push past the clamp either
                    sc.FlingVelocity = (snapTarget - off) * k;           // same curve, lands on the snap value
                    sc.FlingSnapTarget = snapTarget;                     // the EXACT value the landing writes (drift-free)
                }

                float dtS = dtMs * 0.001f;
                float flingDecay = sc.ScrollMode == WheelFlingMode ? WheelFlingDecayPerS : _flingDecayPerS;
                float v = sc.FlingVelocity * MathF.Pow(flingDecay, dtS);
                // A SNAP fling terminates on DISTANCE-TO-TARGET, not on velocity: the re-solved velocity asymptotes to the
                // snap value, but the 0.95/s decay is near-frictionless, so waiting for |v| < v_min would coast for tens of
                // seconds. Instead, once this tick's advance would reach (or pass) the stored snap target, write the EXACT
                // FlingSnapTarget and end — so the fling lands ON the snap with no integration drift. A free (non-snap)
                // fling keeps the velocity-cutoff/clamp settle below. The next-position estimate (off + v·dt) tells us when
                // the advance has arrived at the target this frame.
                bool snapLanding = false;
                if (sc.FlingRetargeted && sc.HasSnap && !float.IsNaN(sc.FlingSnapTarget))
                {
                    float target = sc.FlingSnapTarget;
                    float nextOff = off + v * dtS;
                    bool reached = MathF.Abs(target - off) <= SnapLandEpsPx                       // already there
                                || (v >= 0f ? nextOff >= target - SnapLandEpsPx : nextOff <= target + SnapLandEpsPx)  // this tick reaches/passes it
                                || MathF.Abs(v) < _flingSettleVelPxPerS;                          // a tiny re-solved velocity (snap was already near) → settle now
                    if (reached)
                    {
                        moved = ScrollWrite?.Invoke(n, target) ?? false;   // land exactly on the snap (clamp-safe through SetScrollOffset)
                        sc.ScrollMode = 0;
                        sc.FlingVelocity = 0f;
                        sc.FlingSnapTarget = float.NaN;
                        off = horizontal ? sc.OffsetX : sc.OffsetY;
                        tgt = horizontal ? sc.TargetX : sc.TargetY;
                        snapLanding = true;
                    }
                    else moved = false;   // (assigned in the non-landing branch below)
                }
                else moved = false;

                if (!snapLanding)
                {
                    float requested = off + v * dtS;
                    float zr = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
                    float maxOff = horizontal ? MathF.Max(0f, sc.ContentW * zr - sc.ViewportW) : MathF.Max(0f, sc.ContentH * zr - sc.ViewportH);
                    bool hitClamp = requested < 0f || requested > maxOff;
                    moved = ScrollWrite?.Invoke(n, requested) ?? false;        // through SetScrollOffset: clamp + transform + re-realize
                    off = horizontal ? sc.OffsetX : sc.OffsetY;              // re-read the clamped position (Target == Offset)
                    tgt = horizontal ? sc.TargetX : sc.TargetY;
                    // A touch fling that REACHES the clamp (either this integrated step requested past the clamp, or
                    // SetScrollOffset returned !moved because the offset was already pinned at 0/max) converts its
                    // residual velocity into a rubber-band overscroll bounce (WinUI/iOS) instead of stopping dead at
                    // the edge — the trace showed a finger flick hitting off=0 and stopping flat, which read as "no rubber
                    // band." A settle (|v| below the cutoff, short of the edge) just ends. Wheel never enters Fling mode,
                    // so this is touch-only; the bounce rides the same OverscrollSpringOmega spring as a touch-pan overpan.
                    if (hitClamp || !moved || MathF.Abs(v) < _flingSettleVelPxPerS)     // a clamp boundary or a settle ends the fling
                    {
                        if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"  fling END off={off:0} moved={moved} hitClamp={hitClamp} v={v:0} (from {sc.FlingFromOffset:0}, coast={off - sc.FlingFromOffset:0})");
                        if (sc.ScrollMode == FlingMode && (hitClamp || !moved) && MathF.Abs(v) >= FlingBounceMinPxPerS && !sc.Overscrolling)
                        {
                            float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
                            float band = sc.OverscrollPx, bandVel = sc.OverscrollVel;
                            OverscrollPhysics.SeedFromEdgeMomentum(ref band, ref bandVel, v, viewport, _flingDecayPerS);
                            sc.OverscrollPx = band;
                            sc.OverscrollVel = bandVel;
                        }
                        sc.ScrollMode = 0;
                        sc.FlingVelocity = 0f;
                    }
                    else
                    {
                        sc.FlingVelocity = v;
                        flinging = true;
                    }
                }
            }
            else
            {
                if (sc.ScrollMode == FlingMode || sc.ScrollMode == WheelFlingMode) { sc.ScrollMode = 0; sc.FlingVelocity = 0f; }   // retargeted away → TargetChase
                float kOff = 1f - MathF.Exp(-dtMs / _wheelEaseTauMs);   // unified wheel/scrollbar offset smoothing (the feel knob)
                float oldOff = off;
                off += (tgt - off) * kOff;
                if (MathF.Abs(tgt - off) < 0.5f) off = tgt;
                moved = off != oldOff;
                if (horizontal) sc.OffsetX = off; else sc.OffsetY = off;

                if (moved)
                {
                    var content = sc.ContentNode;
                    if (!content.IsNull && _scene.IsLive(content))
                    {
                        ref NodePaint cp = ref _scene.Paint(content);
                        cp.LocalTransform = Affine2D.Translation(horizontal ? -off : 0f, horizontal ? 0f : -off);
                        _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                    }
                    if (sc.ItemCount > 0)   // virtualization: re-realize only when visible range approaches realized-window edge
                    {
                        int visibleFirst, visibleLast;
                        float vp = horizontal ? sc.ViewportW : sc.ViewportH;
                        if (sc.Layout is not null)
                        {
                            float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                            sc.Layout.Window(sc.ItemCount, cross, vp, off, 0, out visibleFirst, out visibleLast);
                        }
                        else if (_scene.TryGetExtents(n, out var t) && t is not null) { visibleFirst = t.IndexAt(off); visibleLast = Math.Min(sc.ItemCount, t.IndexAt(off + vp) + 1); }
                        else { visibleFirst = visibleLast = 0; }
                        if (VirtualWindowing.NeedsRealize(in sc, visibleFirst, visibleLast)) { _scene.Mark(n, NodeFlags.VirtualRangeDirty); RequestRerender(); }
                    }
                }
            }

            // ── rubber-band overscroll spring ─────────────────────────────────────────────────────────────
            // Touch = the finger drives OverscrollPx 1:1 (Overscrolling) then a spring pulls it to 0 on pointer-up. All
            // writes are TransformDirty-only through OverscrollWrite. (Precision-touchpad overpan is owned by the OS
            // DirectManipulation source — the OverscrollDmOwned branch below — never an engine spring.)
            bool bandActive = false;
            if (sc.OverscrollDmOwned)
            {
                // DirectManipulation owns this band (its native OS overscroll). OBSERVE ONLY — never spring it: DM writes
                // the band every frame and settles it to 0 itself, so a second engine spring just fights it and the edges
                // jitter (the diagnostics caught 266 engine-spring writes racing 641 DM writes). Stay armed while it's non-0.
                bandActive = sc.OverscrollPx != 0f;
            }
            else if ((sc.OverscrollPx != 0f || sc.OverscrollVel != 0f) && !sc.Overscrolling)
            {
                float p = sc.OverscrollPx, vsp = sc.OverscrollVel;   // TOUCH release spring-back to 0 (unchanged)
                bool settled = OverscrollPhysics.StepSpring(ref p, ref vsp, dtMs, OverscrollPhysics.SpringOmegaRadPerS);
                sc.OverscrollVel = vsp;
                OverscrollWrite?.Invoke(n, p);
                if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"  spring band={p:0.00} v={vsp:0}");
                bandActive = !settled;
            }
            else if (sc.Overscrolling && sc.OverscrollPx != 0f)
            {
                bandActive = true;   // a touch finger is holding the band displaced — stay armed (the indicator shows through it)
            }

            // DIAGNOSTIC (FG_SCROLL_LOG / FG_DM_PROBE): per-frame state of this scroll node WHEN anything is moving. The
            // timestamped intervals expose stutter/gaps (raw values hide it); an "anim" line that MOVES offset/band during a
            // DirectManipulation gesture (which logs its own "DM" lines) exposes the engine fighting DM — DM's log stays
            // clean while the rendered frames alternate writers ("looks fine, feels like dogshit").
            if (FluentGpu.Foundation.ScrollLog.On)
            {
                float offNow = horizontal ? sc.OffsetX : sc.OffsetY;
                float tgtNow = horizontal ? sc.TargetX : sc.TargetY;
                if (moved || flinging || bandActive || sc.OverscrollPx != 0f || MathF.Abs(tgtNow - offNow) > 0.5f)
                    FluentGpu.Foundation.ScrollLog.Line($"anim    off={offNow:0.0} band={sc.OverscrollPx:0.0} tgt={tgtNow:0.0} mode={sc.ScrollMode} moved={(moved ? 1 : 0)} fling={(flinging ? 1 : 0)} bandAct={(bandActive ? 1 : 0)}");
            }

            // ── conscious scrollbar state machine (WinUI timings; see class remarks) ──────────────────────
            int key = (int)n.Raw.Index;
            _state.TryGetValue(key, out Conscious cs);
            // A SYNCHRONOUS offset write this frame (touch content-pan / thumb-drag / edge auto-scroll — Offset == Target, so
            // |tgt-off| can't see it) pulses ScrollMoved; consume it (one-frame) and count it as motion so the thin indicator
            // reveals THROUGH a touch pan, then idle-hides on the pulse stopping. PointerOver/ExpandT stay untouched (a content
            // pan is not a lane dwell — the bar must not expand to the full gutter, and there is no hover to clear on lift).
            bool syncMoved = sc.ScrollMoved;
            sc.ScrollMoved = false;
            bool movingNow = flinging || syncMoved || bandActive || MathF.Abs(tgt - off) > 0.5f;
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
    /// slot would inherit. The armed-set lists (<c>_active</c>/<c>_member</c>) hold gen-checked handles and self-prune at
    /// the next Tick's IsLive guard, so only the index-keyed <c>_state</c> needs clearing here. 0-alloc; a no-op when the
    /// slot had no row.</summary>
    public void ClearForIndex(int index) => _state.Remove(index);
}
