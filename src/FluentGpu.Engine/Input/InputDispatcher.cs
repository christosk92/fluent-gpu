using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Input;

/// <summary>Directional focus movement for roving/XY keyboard navigation (arrow keys in lists, grids, menus).</summary>
public enum FocusDirection : byte { Left, Right, Up, Down }

/// <summary>
/// Phase 2 (input dispatch): hit-tests the committed scene and routes pointer + keyboard. Pointer down→up over the
/// same node fires the click handler and focuses the nearest focusable self-or-ancestor (a WinUI IsTabStop=False part
/// never takes pointer focus; nothing focusable in the chain → focus is CLEARED iff the press hit INERT background — a node
/// with no press handlers, AnyInteractiveMask == 0, and (touch) no pan candidate — else unchanged: a FluentGpu §8
/// divergence); keyboard routes to the focused node and bubbles up ancestors
/// (Handled stops it); Tab moves focus through focusable nodes; Enter/Space activates a focused clickable (the
/// "one declaration, three modalities" contract). The full engine adds tunnel(Preview), gesture arena, XY-focus.
/// </summary>
public sealed class InputDispatcher
{
    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _focusables = new();
    private readonly List<NodeHandle> _scoped = new();   // reused buffer for scoped (within-a-root) focus collection
    // ── per-pointer interaction (the contact-keyed scalars; cap 10 concurrent contacts) ───────────────
    // Multi-touch capture: each contact (PointerId — mouse/pen/headless default = 0; touch = the OS id) owns its OWN
    // down/drag/scrollbar-drag/context/middle press targets, plus its single-recognizer touch pan + velocity sampler.
    // The fields below are the WORKING SET for the contact whose event is being processed: SlotIn loads the contact's
    // saved slot into them at the top of each pointer event, SlotOut stores it back at the end. For a lone mouse (id 0)
    // that is a round-trip of one slot, so every existing mouse path stays bit-identical (WinUI's per-PointerId
    // PointerInputProcessor capture, generalized to N contacts). Hover stays a mouse/pen-only single field (touch never
    // latches a resting hover); pressed is a single field driven by mouse/pen AND touch (Phase 2 — a contact owns it
    // while _pressed == _down), released on up/cancel/pan-claim back to Normal, never PointerOver (touch has no cursor).
    private NodeHandle _down;
    private NodeHandle _focused;
    private NodeHandle _hovered;
    private NodeHandle _pressed;
    private NodeHandle _dragTarget;
    private NodeHandle _scrollHovered;
    private NodeHandle _scrollDragNode;
    private float _scrollDragGrab;
    private NodeHandle _contextDown;   // right-button press target — context menu fires on release over the same chain
    private NodeHandle _middleDown;    // middle-button press target — delivered on release over the same node (TabView close)
    // Single-recognizer touch pan/tap (Phase 1, no arena): the contact's press anchor, the scroll target it drives once
    // the gesture claims a pan, and the velocity sampler seeded for the fling hand-off. Inactive (null target) for a
    // below-slop tap, which falls through to the existing click path.
    private NodeHandle _panTarget;     // the Scrollable this contact's touch-down landed over (the pan candidate/claim)
    private bool _panClaimed;          // the candidate crossed slop → the pan owns the contact (the press was cancelled)
    private Point2 _panAnchorPx;       // window-space press point — pan delta measures from here
    private float _panAnchorOffset;    // the scroll offset captured at the press (drives SetScrollOffset(anchor − delta))
    private bool _panAxisX;            // the scroll axis of _panTarget (Orientation == 1) — pan tracks this axis only
    private NodeHandle _chainOuter;    // nested-scroll (§10) LIFT-time hand-off latch (ChainFlingTarget): the outer same-axis
                                       // scroller a pan-at-edge threw at lift. DRAG-time chaining (the old _chainOuterAnchor
                                       // absolute-map) is not yet reproduced in the per-node integrator path — see openIssues.
    private ImpulseVelocity _panVel;   // per-contact IMPULSE (work-energy) velocity estimator for the touch fling hand-off
    private bool _touchSuppressTap;    // contact landed during inertia: only arrest/re-grab the viewport, never click
    private bool _pendingTouchPress;   // scrollable item visual waits 100ms so ordinary pans never flash Pressed
    private float _pendingTouchPressMs;

    // This frame's pre-coalesce velocity samples (InputEventRing side ring, design §2): stashed at Dispatch, fed into
    // the pan estimator when the matching contact's move/up processes (its slot is loaded then). Feeding is idempotent
    // (the estimator rejects non-monotonic stamps), so no consumption bookkeeping. Fixed slab, zero alloc.
    private readonly PointerVelSample[] _velSamples = new PointerVelSample[64];
    private int _velSampleCount;

    /// <summary>Feed the stashed pre-coalesce samples for <paramref name="pointerId"/> into <see cref="_panVel"/> —
    /// restores sub-frame velocity fidelity that per-frame move coalescing would otherwise discard.</summary>
    private void FeedPanVelocity(uint pointerId)
    {
        for (int i = 0; i < _velSampleCount; i++)
        {
            ref readonly PointerVelSample s = ref _velSamples[i];
            if (s.PointerId == pointerId) _panVel.Sample(new Point2(s.X, s.Y), s.TimestampMs, s.QpcTicks);
        }
    }

    // ── phase-driven scroll gesture (docs/plans/scroll-feel-rework-design.md §6): ONE latched gesture consuming the
    // ScrollBegin/Update/End + MomentumBegin/Update/End contract from any producer (DirectManipulation PTP, the Win32
    // wheel-fallback classifier, macOS NSEvent later, headless scripts). Contact AND OS-momentum deltas apply 1:1
    // through ApplyTouchPan (position-driven band — the same model as touch, killing the old velocity-enveloped band,
    // H10) with NO low-pass and NO gain curves (H1/H2). The fallback device seeds ONE SelfFling from the IMPULSE
    // estimator at ScrollEnd — an OS momentum tail, having scrolled 1:1 as contact, leaves the trailing velocity ≈ 0,
    // so no double inertia in either world (§5). DManip contacts never self-fling (their momentum arrives as
    // Momentum* events, applied verbatim). Gesture phase state lives HERE (like the touch pan); a contact/momentum frame
    // leaves ScrollState.Phase at Idle + does a synchronous write, which pulses ScrollMoved → UserScrollActive, so the
    // recorder's DoF-defer stays true through a PTP coast (design §8). (P3 rewires this to record TouchpadTracking intent.)
    private NodeHandle _sgTarget;       // the latched viewport (the WHOLE gesture incl. momentum routes here — checklist #1)
    private bool _sgHoriz;              // the latched axis
    private bool _sgLatched;            // axis+target resolved (after the ~PanSlopPx accumulation window — no first-delta mislatch)
    private bool _sgMomentum;           // inside MomentumBegin..End (OS-owned inertia; applied verbatim, no estimator feed)
    // scroll-feel-v2.1 §A.3: once the phase-7 tick bounces an OS-momentum tail off a content edge (Phase→SnapBack), the
    // remaining tail samples of THIS stream are ignored at the dispatcher — matching WebKit's m_ignoreMomentumScrolls /
    // Chromium's kStateMomentumAnimated. Latched on the first post-conversion MomentumUpdate, cleared at ScrollBegin/End.
    private bool _sgTailConverted;
    private byte _sgDevice;             // ScrollDeviceClass of the live gesture (picks the momentum-ownership row)
    private float _sgAnchorOffset;      // the target's offset (incl. band excess) when the gesture latched — desired = anchor + accum
    private float _sgAccumX, _sgAccumY; // accumulated gesture deltas (the synthetic position the estimator samples)
    private ImpulseVelocity _sgVel;     // fallback release-velocity estimator (unused on the DManip row)
    // scroll-feel-rework-v2 §2.1/§8-gate-1 single-writer guard: TRUE only for the span of the phase-7-tick write seams
    // WriteScrollOffset / WriteOverscroll (the sole legitimate paths to the offset chokepoint ApplyScrollPosition). The
    // chokepoint asserts this is TRUE, so ANY ApplyScrollPosition reached off a non-tick stack (a touch-pan / scrollbar /
    // pinch / auto-scroll / phase-recorder synchronous write — the R1 two-owners defect) trips it, not merely writes made
    // inside OnScrollPhase (the old under-scoped `_inPhaseContract`). [Conditional("DEBUG")] — erased from Release/Ship.
    private bool _inPhaseTick;
    // scroll-feel-rework-v2 §8 single-writer trace: which code path is driving the current SetScrollOffset. The phase-7
    // ScrollIntegrator seam (WriteScrollOffset) tags Integrator around its call; a bare SetScrollOffset (none remain off the
    // seam after §6) would stay Direct. SetScrollOffset records one OffsetWrite with this id per real move, so the
    // single-writer AUDIT gate sees Integrator only.
    private FluentGpu.Foundation.ScrollWriter _scrollWriter = FluentGpu.Foundation.ScrollWriter.Direct;
    // §7A drag-reorder-over-touch: the contact enrolled a DragReorder member (a CanDrag chain under the press) whose
    // axis-locked vote competes with Pan in the arena. The reorder's item axis (the source row's parent-container main
    // axis); when the arena resolves DragReorder, _touchReorder latches and the contact DRIVES Input.DragController
    // (arena-governed: YieldsToPan bypassed) until up/cancel, exactly as _panClaimed latches the pan.
    private NodeHandle _reorderTarget; // the CanDrag node a DragReorder member is enrolled on (null = none for this contact)
    private bool _touchReorder;        // the arena resolved DragReorder → this contact drives DragController (capture)
    private bool _reorderAxisX;        // the item's reorder axis (parent container Direction==0 row ⇒ horizontal item-drag)
    // §7A cross-axis content-pan (SwipeControl row swipe / FlipView page drag — a DragYieldsToPan OnDrag node): unlike the
    // Slider/EditableText eager OnDrag capture, this drag does NOT own the contact on press — it enrolls an axis-locked Drag
    // member (its own main axis) that COMPETES with the enclosing scroller's Pan, so a along-axis swipe wins and a cross-axis
    // drag yields (the list scrolls). On its arena win the contact captures it into _dragTarget and drives OnDrag, exactly as
    // the eager path would have (the declarative DragController.YieldsToPan; the two-arbitration-models risk stays gone).
    private NodeHandle _swipeDrag;     // a DragYieldsToPan OnDrag node hit on this contact (null = none — the eager OnDrag path)
    private bool _swipeAxisX;          // the swipe's own axis (the node's Direction==0 row ⇒ horizontal swipe) — its slop axis
    private NodeHandle _gesturePanNode;// a UseGesture(Pan) member's node (null = none) — voted RAW (any-axis) in the arena
    // §7B/Phase-4 pinch-zoom (the working scalars; PinchViewport/PinchMember ride the slot): the nearest Zoomable viewport
    // this contact's down landed over, and the Pinch arena member enrolled on it (innermost). A SECOND contact whose own
    // PinchViewport matches a still-down contact's opens the pinch SESSION (the singleton _pinch* fields below) and feeds
    // PointerFsm.OnSecondContact → the Pinch member EagerAccept-wins and sweeps Pan/Tap (the existing cancel routing).
    private NodeHandle _pinchViewport; // the Zoomable viewport this contact landed over (null = none — no pinch candidate)
    private int _pinchMember = -1;     // this contact's Pinch arena member slot (-1 = none)
    private NodeHandle _keyArmed;      // focused clickable held via Space/Enter — pressed while held, activates on key-UP
    private int _keyArmedKey;          // which key armed it (Space or Enter) — any OTHER key-down cancels without firing
    private bool _accessKeyMode;       // Alt tapped → the next letter invokes a matching AccessKey mnemonic
    private bool _altPending;          // Alt is down with no intervening key (candidate for access-key-mode toggle)
    private readonly List<NodeHandle> _focusScopes = new();
    // Double/triple-click tracking (platform timestamps; slop + window per Win32 defaults, capped at 3).
    private uint _lastDownMs;
    private Point2 _lastDownPos;
    private int _lastDownButton = -1;
    private byte _clickCount = 1;
    private KeyModifiers _pressMods;
    private PointerKind _pressKind;
    private byte _pressClickCount = 1;
    private CursorId _lastCursor = CursorId.Arrow;
    // Read-only text selection (rtb-02 — WinUI TextSelectionManager, default-on for RichTextBlock,
    // RichTextBlock.cpp:1730): the SelectableTextBit node owning the current selection + the drag anchor index.
    private NodeHandle _selText;
    private int _selAnchor;
    private bool _selDragging;
    // The device class of the most recent pointer event (mouse/pen/touch), updated on every PointerDown/Move/Up. The SIP
    // (touch-keyboard) trigger policy reads it through the InputHooks seam: an EditableText shows the on-screen keyboard
    // on focus-gain ONLY when the focus-moving input was a touch contact (WinUI InputPaneHandler.cpp keys the SIP off the
    // focus-causing pointer's device type). Mouse/pen focus never raises the panel. Defaults to Mouse (the safe identity).
    private PointerKind _lastPointerKind = PointerKind.Mouse;

    // Stuck-hover fix (input-a11y.md §5.4/§15): the last mouse/pen window position + whether it is known. Written on
    // every mouse/pen Down/Move/Up (below, next to _lastPointerKind), invalidated on WindowBlur. Drives the synthesized
    // stationary hover re-resolve (RefreshHoverAfterScroll) after a phase-7 scroll write moves content under a still cursor.
    private Point2 _lastPointerPx;
    private bool _lastPointerValid;

    // ── pinch-zoom session (Phase-4; a singleton across the TWO pinching contacts — the DragController precedent) ──────
    // Opened when a second touch contact lands over the same Zoomable viewport a still-down contact is over; both contacts'
    // moves drive ZoomFor (the magnification about the gesture midpoint), and the FIRST up/cancel commits the scale and
    // ends the session (the surviving finger re-arms as a pan naturally in TouchMove). All scalar (zero alloc).
    private NodeHandle _pinchSessionViewport;   // the viewport being pinch-zoomed (null = no active pinch)
    private uint _pinchIdA, _pinchIdB;          // the two contributing PointerIds (the session ends on either lifting)
    private float _pinchStartDist;              // |A−B| in window px at the second-contact down (the scale denominator)
    private float _pinchStartZoom;              // ScrollState.ZoomFactor captured when the pinch opened (the scale base)
    private float _pinchOriginAxis;             // window-space position of the content node's local origin on the scroll
                                                // axis (offset-independent) — midLocal = midpointAxis − this
    private Point2 _pinchPosA, _pinchPosB;      // the latest window positions of A and B (each contact's move updates its own)
    private float _pinchAppliedOff, _pinchAppliedZoom;   // the (offset, zoom) base UpdatePinch's focal math chains from —
                                                // tracked in the dispatcher because the offset intent applies at phase 7
                                                // (reading sc.Offset mid-frame would lag the just-committed ZoomFactor and
                                                // corrupt the focal point when BOTH fingers move in one coalesced frame).

    public const float ClickSlopPx = 4f;
    public const uint DoubleClickMs = 500;

    /// <summary>Touch pan claim distance, tested per-axis (the Windows drag box <c>SM_CXDRAG</c>/<c>SM_CYDRAG</c> default
    /// = 4, the same threshold <see cref="DragController.DragThresholdPx"/> uses; a portable const because the OS metric
    /// is unavailable off-Windows). Below it a touch down→up is a tap; crossing it on the scroll axis claims the pan and
    /// cancels the press candidate (WinUI Pressed→Canceled, never Released — PointerInputProcessor.cpp:397/423).</summary>
    public const float TouchSlopPx = 8f;
    public const float PanSlopPx = TouchSlopPx;

    // NOTE (scroll-feel-rework-v2 §4.3/§4.6): the touch/PTP-fallback self-fling SEED gate is the canonical
    // ScrollTuning.FlingSeedGate = 50 px/s (Android min-fling), NOT a local dispatcher const. The old
    // InputDispatcher.FlingMinVelocityPxPerS (=50, which merely COINCIDED with FlingSeedGate) is deleted: it read as the
    // sibling of ScrollIntegrator.FlingMinVelocityPxPerS (=13, the SETTLE velocity), so a reader could not tell which
    // threshold the seed gate meant. All three seed sites now reference ScrollTuning.FlingSeedGate directly.

    // (The old touchpad-feel tuning block — s_tpSmoothTau 14ms low-pass, s_tpSettleQuietMs 120ms quiet-latch,
    // s_tpVelTauMs/s_tpVelDecayTauMs/s_tpBandEaseTauMs/s_tpBandFullVelPxPerS/s_tpBandHeadroom/ExcessSnapDip velocity-
    // enveloped band — is DELETED with the second integrator it tuned: contact is 1:1 and the band is position-driven
    // now. See docs/plans/scroll-feel-rework-design.md §12.)

    /// <summary>Rubber-band overscroll cap as a fraction of the viewport extent: WinUI lets a ScrollViewer over/underpan
    /// up to 10% of its viewport (ScrollInputHelper.cpp:309-311). Delegates to <see cref="OverscrollPhysics"/>.</summary>
    public const float OverscrollLimitFraction = OverscrollPhysics.ViewportLimitFraction;

    private const float ScrollbarSize = 12f;
    private const float ScrollbarMinExpandedThumb = 30f;
    private const float ScrollbarMinCollapsedThumb = 32f;
    private const float ScrollbarSmallChange = 48f;

    /// <summary>Concurrent-contact cap (the per-PointerId capture map): mouse/pen + up to this many simultaneous touch
    /// points. An 11th concurrent contact is ignored deterministically — no growth, zero allocation after construction
    /// (orchestrator ruling: 10 contacts, 11th dropped). Idle slots are reclaimed when a contact ends.</summary>
    private const int MaxContacts = 10;

    /// <summary>Per-contact capture + touch-gesture state, indexed by an id→slot linear probe. A slot is LIVE between
    /// the contact's first event and its up/cancel; once every target/anchor is clear and the velocity is idle it is
    /// recycled (so a fresh contact reusing the OS id, and the steady mouse, never run the table out).</summary>
    private struct PointerSlot
    {
        public uint Id;
        public bool Used;
        public NodeHandle Down, DragTarget, ScrollDragNode, ContextDown, MiddleDown;
        public byte PressClickCount;
        public KeyModifiers PressMods;
        public PointerKind PressKind;
        public float ScrollDragGrab;
        public NodeHandle PanTarget;
        public bool PanClaimed;
        public Point2 PanAnchorPx;
        public float PanAnchorOffset;
        public bool PanAxisX;
        public ImpulseVelocity PanVel;
        public bool TouchSuppressTap;
        public bool PendingTouchPress;
        public float PendingTouchPressMs;
        public NodeHandle ReorderTarget;   // §7A DragReorder member's CanDrag node (null = no reorder candidate)
        public bool TouchReorder;          // the arena resolved DragReorder → this contact drives DragController
        public bool ReorderAxisX;          // the item's reorder axis (parent row ⇒ horizontal item-drag)
        public NodeHandle SwipeDrag;       // §7A cross-axis content-pan Drag member's DragYieldsToPan node (null = none)
        public bool SwipeAxisX;            // the swipe's own axis (node Direction==0 row ⇒ horizontal swipe)
        public NodeHandle GesturePanNode;  // §13 UseGesture(Pan) member's node (raw any-axis vote)
        public NodeHandle PinchViewport;   // the nearest Zoomable viewport this contact landed over (null = none) — a
                                           // SECOND contact landing over the SAME viewport opens a pinch (§7B Pinch member)
        public int PinchMember;            // this contact's enrolled Pinch arena member slot (-1 = none) — fed OnSecondContact
        public int ArenaSlot;   // the §7A arena this contact opened (-1 = none: a mouse/pen contact, or a route with no gestures)
        public bool HoldFired;  // §7A the long-press Hold won + fired its context flyout: the eventual up suppresses the tap-click
    }

    /// <summary>IMPULSE (work-energy) pointer-velocity estimator (px/s) — Android's scroll-axis strategy
    /// (VelocityTracker IMPULSE; design doc §2). Over the trailing <see cref="HorizonMs"/> window it forms the
    /// successive segment velocities v_i and accumulates the work <c>W = ½·v₀·|v₀| + Σ(v_i − v_{i−1})·|v_i|</c>;
    /// the release velocity is <c>sign(W)·√(2|W|)</c>. Chosen over the prior 50ms least-squares regression because
    /// the work integral is robust to the last-sample noise that corrupts slope fits on the scroll axis (the exact
    /// "same flick, different result" class the no-up-fold hack fought), it needs only 2 samples (fixes the
    /// short-flick under-read), and it naturally reads ≈0 at the end of a decaying stream — the property the
    /// fallback path uses as its OS-momentum-tail detector (§5). Prefers per-packet QPC stamps
    /// (<see cref="InputEvent.QpcTicks"/> / <see cref="SystemParams.QpcFrequency"/>, sub-ms) and falls back to
    /// millisecond message time; 0-stamp samples (the headless default) never advance it, so a 0-stamp gesture
    /// measures zero velocity — the vacuous-fling guard and gate determinism hold. A sample gap of
    /// <see cref="AssumeStoppedMs"/> before the lift reads 0 (the finger stopped before lifting — Android
    /// ASSUME_POINTER_STOPPED_TIME). Per-contact, fixed inline storage, zero alloc, fixed oldest→newest iteration
    /// (deterministic across the animation-dt sweep — the input events are identical).</summary>
    private struct ImpulseVelocity
    {
        private const int Cap = 8;                 // fixed ring capacity (covers the horizon at any realistic cadence)
        // scroll-feel-rework-v2 §4.3: ONE 40ms IMPULSE window, ONE gate (|v| ≥ FlingSeedGate seeds). The v1 dual gate
        // (work-energy + a trailing-32ms displacement velocity agreeing in sign) is DELETED — the 40ms window IS the
        // trailing window: a completed OS/driver tail decays to v≈0 (below the seed gate ⇒ no double inertia), an abrupt
        // lift keeps true hand speed, a mid-stream reversal yields opposite-sign v ⇒ no stale coast. The remaining
        // double-inertia guard is AssumeStoppedMs alone (newest sample older than 40ms at lift ⇒ v=0 — a completed tail
        // then silence). One window, one gate.
        private const float HorizonMs = ScrollTuning.VelWindowMs;          // 40ms — the single IMPULSE window (= the trailing window)
        private const float AssumeStoppedMs = ScrollTuning.AssumeStoppedMs; // 40ms — newest-sample→lift gap beyond this ⇒ release velocity 0

        private struct VSample { public float X, Y; public double TSec; }
        [System.Runtime.CompilerServices.InlineArray(8)]
        private struct Ring { private VSample _e0; }

        private Ring _ring;
        private int _count;      // valid samples in the ring (0..Cap)
        private int _head;       // next write index (ring)
        private double _lastT;   // newest accepted sample time (s)
        private bool _hasLast;
        private float _vx, _vy;
        public float Vx => _vx;
        public float Vy => _vy;

        /// <summary>Seconds on the sample clock: QPC when the platform provides it, else the ms message time.
        /// 0/0 = the vacuous headless stamp → NaN sentinel (callers skip it).</summary>
        private static double ToSec(uint ms, long qpc)
        {
            long f = SystemParams.QpcFrequency;
            if (qpc != 0 && f > 0) return qpc / (double)f;
            return ms == 0 ? double.NaN : ms / 1000.0;
        }

        public void Reset(Point2 abs, uint timestampMs, long qpcTicks = 0)
        {
            _count = 0; _head = 0; _hasLast = false;
            _vx = 0f; _vy = 0f;
            Push(abs, timestampMs, qpcTicks);
        }

        public void Sample(Point2 abs, uint timestampMs, long qpcTicks = 0)
        {
            if (!Push(abs, timestampMs, qpcTicks)) return;   // vacuous/non-monotonic stamp — never advances (guard)
            Compute(_lastT);                                  // Vx/Vy stay live (PointerVelocity, under-sampled path)
        }

        /// <summary>The release velocity to seed a fling, read at touch-UP: re-window the EXISTING move samples against
        /// the lift timestamp WITHOUT pushing the up position in (the up event repeats the last move's position at a
        /// later stamp — folding it in corrupted the estimate on lift-timing jitter; see the design doc §2). The lift
        /// stamp still expires stale samples (a finger that paused &gt; <see cref="AssumeStoppedMs"/> before lifting
        /// reads 0). 0/duplicate stamp (the headless non-inertial default): keep the last computed velocity.</summary>
        public void ComputeReleaseVelocity(uint upMs, long upQpcTicks = 0)
        {
            double t = ToSec(upMs, upQpcTicks);
            if (double.IsNaN(t) || (_hasLast && t <= _lastT)) return;
            Compute(t);
        }

        private bool Push(Point2 abs, uint ms, long qpc)
        {
            double t = ToSec(ms, qpc);
            if (double.IsNaN(t)) return false;
            if (_hasLast && t <= _lastT) return false;
            _ring[_head] = new VSample { X = abs.X, Y = abs.Y, TSec = t };
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
            _lastT = t; _hasLast = true;
            return true;
        }

        private void Compute(double tLift)
        {
            _vx = 0f; _vy = 0f;
            if (_count < 2) return;
            if (_hasLast && (tLift - _lastT) * 1000.0 > AssumeStoppedMs) return;   // stopped before lifting

            int oldest = (_head - _count + Cap) % Cap;
            double wx = 0.0, wy = 0.0, vpx = 0.0, vpy = 0.0;
            bool first = true, hasPrev = false;
            VSample prev = default;
            for (int k = 0; k < _count; k++)
            {
                VSample s = _ring[(oldest + k) % Cap];
                if ((tLift - s.TSec) * 1000.0 > HorizonMs)
                {
                    prev = s; hasPrev = true;   // the newest pre-window sample baselines the first in-window segment
                    continue;
                }
                if (!hasPrev) { prev = s; hasPrev = true; continue; }
                double dt = s.TSec - prev.TSec;
                if (dt <= 0.0) { prev = s; continue; }
                double vx = (s.X - prev.X) / dt, vy = (s.Y - prev.Y) / dt;
                if (first)
                {
                    wx += 0.5 * vx * Math.Abs(vx);   // ½·v₀·|v₀| — the from-rest first term (½mv²)
                    wy += 0.5 * vy * Math.Abs(vy);
                    first = false;
                }
                else
                {
                    wx += (vx - vpx) * Math.Abs(vx);
                    wy += (vy - vpy) * Math.Abs(vy);
                }
                vpx = vx; vpy = vy; prev = s;
            }
            if (first) return;                        // no in-window segment
            _vx = (float)(Math.Sign(wx) * Math.Sqrt(2.0 * Math.Abs(wx)));
            _vy = (float)(Math.Sign(wy) * Math.Sqrt(2.0 * Math.Abs(wy)));
        }
    }

    private readonly PointerSlot[] _slots = new PointerSlot[MaxContacts];
    private uint _activeSlotId;        // the contact whose event is being processed (its slot is loaded into the scalars)
    private bool _activeSlotValid;     // false when the contact was ignored (table full) — SlotOut then discards it

    // ── the gesture arena (§7A) + the per-(PointerId,recognizer) FSM bank (§7B) ──────────────────────────────
    // The touch path opens one arena per contact on TouchDown and enrolls members innermost-first along the hit route
    // (§7A.1): a Scrollable ancestor → Pan, an OnDrag/_dragTarget node → Drag, a CanDrag chain → DragReorder, a clickable
    // → Tap (+ DoubleTap on a double-click consumer, bound under a SELECTION TEAM §7A.3 for editable text), a hold-handler
    // → Hold, and a UseGesture(§13) node → its declared Tap/Hold/Pan. The arena owns WHICH recognizer wins; the proven
    // Phase-1/2 scalar machinery (SetScrollOffset pan, the click path, the OnDrag capture, the thumb-drag) executes the
    // winner — so the single-recognizer common case stays observably identical (§7A.5: one member → synchronous accept →
    // same-frame capture/click/drag, the explicit fast-path below). Two consumer surfaces close on the arena's verdict:
    // a DragReorder win drives Input.DragController arena-governed (its YieldsToPan heuristic SUBSUMED — the arena's
    // axis-locked Pan-vs-DragReorder vote is the single arbiter, replacing the two-models risk), and a winning
    // UseGesture member routes its event to the handler (RouteGestureWin via OnMemberWon). The FSM bank is index-parallel
    // to the arena's member backing store (reached by ArenaMember.FsmSlot); a swept loser is reset to Idle through
    // OnMemberRejected (the synthetic GestureRejected). All fixed storage — zero per-frame heap.
    private readonly GestureArena _arena = new();
    private readonly PointerFsm[] _fsms = new PointerFsm[GestureArena.MaxArenas * GestureArena.MaxMembersPerArena];
    private int _activeArenaSlot = -1;   // the arena slot of the contact being processed (loaded by SlotIn, stored by SlotOut)
    private bool _holdFired;             // §7A the contact's Hold won + fired its context flyout (the up suppresses the tap-click; press visual still held until up)
    private long _arenaClockUs;          // monotonic µs clock for the §7A timer resolutions (Hold long-press): synced
                                         // forward to the latest event stamp in Dispatch, advanced by dt in TickGestureArenas

    public InputDispatcher(SceneStore scene)
    {
        _scene = scene;
        Drag = new DragController(scene, () => RequestRerender());
        DragDrop = new DragDropContext(scene, () => RequestRerender()) { ScrollBy = AutoScrollBy };
        // The arena drives the FSM bank: a swept loser resets to Idle (emits nothing, §7A.5); the winner is granted hard
        // capture and lets its FSM emit. The DRAG-REORDER / pan / click EXECUTION stays on the scalar path (so the
        // single-recognizer common case is observably identical), but the win sink now ROUTES a UseGesture declaration
        // (§13): when the winning member's node declared a matching Tap/Hold/Pan handler, it fires with the gesture
        // payload. Instance lambdas captured ONCE in the ctor (no per-event closure): the reject sink only needs the
        // FsmSlot; the win sink reads the member's Node/Kind (via the arena) + the per-event gesture context fields.
        _arena.OnMemberRejected = slot => { if ((uint)slot < (uint)_fsms.Length) _fsms[slot].Reset(); };
        _arena.OnMemberWon = RouteGestureWin;
    }

    // Per-event gesture-routing context (set right before a Resolve* so the OnMemberWon sink reports the right point):
    // the window-space position the win is reported at, the device kind, and the end-velocity (filled on the Pan-end up).
    // Zero alloc — plain fields, no closure. The reused GestureEventArgs is filled per invocation (handlers copy keeps).
    private Point2 _gestureWinPos;
    private PointerKind _gestureWinPointer = PointerKind.Touch;
    private Point2 _gestureWinVel;
    private readonly GestureEventArgs _gestureArgs = new();

    /// <summary>The §7A win sink: execute the winning recognizer's effect (§7A.2). Two surfaces close here. (1) A
    /// <see cref="GestureKind.Hold"/> win on a <see cref="InteractionInfo.ContextBit"/> node FIRES the context request at
    /// the contact point — the touch long-press → context flyout (WinUI: a touch <c>Holding</c> shows the context flyout
    /// at the contact with the press visual held; touch has no right button, so the Hold is the only path). This runs
    /// REGARDLESS of <c>HasGestureSubs</c> (a context-only node declares no <c>UseGesture</c>). (2) If the winning node
    /// declared a matching <c>UseGesture</c> handler (§13), fire it: Tap/Hold report the gesture point (the up / down
    /// position), a Pan win reports the claim sample (velocity 0; the Pan-END velocity is delivered by
    /// <see cref="FireGesturePanEnd"/> on touch-up). Reserved kinds (DoubleTap/RightTap/Drag/Pinch) have no §13 handler
    /// slot. Zero alloc (the §13 leg is gated on <c>HasGestureSubs</c>; the reused args struct is filled per invocation).</summary>
    private void RouteGestureWin(int fsmSlot)
    {
        if ((uint)fsmSlot >= (uint)_fsms.Length) return;
        ref ArenaMember m = ref _arena.MemberAt(fsmSlot);
        // (1) Touch long-press context flyout: a Hold winner over a context-request chain fires the SAME action a
        // right-click fires (DispatchContextRequest walks for the nearest enabled ContextBit handler from the Hold's
        // node). The position is the buffered DOWN point (_gestureWinPos — the contact where the finger went down and
        // held, set by TickGestureArenas before this resolves). The press visual is still held: the contact owns the
        // _pressed singleton (the Hold never crossed slop, so the press was never cancelled), and it releases on the
        // eventual up — exactly the WinUI "show the flyout while the press is held" sequence.
        if (m.Kind == GestureKind.Hold && !m.Node.IsNull && _scene.IsLive(m.Node)
            && (_scene.Interaction(m.Node).HandlerMask & InteractionInfo.ContextBit) != 0)
            DispatchContextRequest(m.Node, _gestureWinPos, ContextRequestTrigger.Hold);
        if (!_scene.HasGestureSubs) return;   // no UseGesture anywhere in the scene → skip the §13 leg (the common case)
        GestureType gt = m.Kind switch
        {
            GestureKind.Tap => GestureType.Tap,
            GestureKind.Hold => GestureType.Hold,
            GestureKind.Pan => GestureType.Pan,
            _ => (GestureType)255,   // reserved/unrouted kind
        };
        if ((byte)gt == 255) return;
        var handler = _scene.GetGestureHandler(m.Node, gt);
        if (handler is null) return;
        _gestureArgs.Kind = gt;
        _gestureArgs.Position = _gestureWinPos;
        _gestureArgs.Velocity = gt == GestureType.Pan ? _gestureWinVel : Point2.Zero;
        _gestureArgs.Pointer = _gestureWinPointer;
        handler.Invoke(_gestureArgs);
    }

    /// <summary>Deliver a final <see cref="GestureType.Pan"/> event with the sampled end-velocity on touch-up, when this
    /// contact's arena resolved a UseGesture(Pan) member on <see cref="_gesturePanNode"/> (§13). The velocity is the
    /// per-contact sampler's flick speed (px/s) — the same one the scroller fling uses. Zero alloc (reused args). A
    /// gesture-pan that never crossed slop (no win) fires nothing.</summary>
    private void FireGesturePanEnd(in InputEvent e)
    {
        if (_gesturePanNode.IsNull || _activeArenaSlot < 0 || !_arena.IsArenaOpen(_activeArenaSlot)) return;
        int winner = _arena.ArenaAt(_activeArenaSlot).WinnerSlot;
        if (winner < 0) return;
        ref ArenaMember w = ref _arena.MemberAt(winner);
        if (w.Kind != GestureKind.Pan || w.Node != _gesturePanNode) return;
        var handler = _scene.GetGestureHandler(_gesturePanNode, GestureType.Pan);
        if (handler is null) return;
        FeedPanVelocity(e.PointerId);
        _panVel.ComputeReleaseVelocity(e.TimestampMs, e.QpcTicks);   // release velocity re-windowed at lift (no up-point fold) — see ComputeReleaseVelocity
        _gestureArgs.Kind = GestureType.Pan;
        _gestureArgs.Position = e.PositionPx;
        _gestureArgs.Velocity = new Point2(_panVel.Vx, _panVel.Vy);
        _gestureArgs.Pointer = e.Pointer;
        handler.Invoke(_gestureArgs);
    }

    public NodeHandle Focused => _focused;

    /// <summary>The device class of the most recent focus-causing pointer (mouse/pen/touch). The host exposes it through
    /// the <c>InputHooks.LastPointerWasTouch</c> seam so an editor's focus-gain handler shows the touch keyboard (SIP)
    /// only on a TOUCH focus (WinUI InputPaneHandler.cpp keys the panel off the focus pointer's type). Defaults to Mouse.</summary>
    public PointerKind LastPointerKind => _lastPointerKind;

    /// <summary>The last mouse/pen pointer position (window DIP), or null while there is none to trust (the cursor left
    /// the client area / the window blurred — the same validity the stationary-hover re-resolve uses). Host-exposed via
    /// <c>InputHooks.GetPointerPosition</c>; the ToolTip safe-zone poll reads it (the WinUI IsToolTipInSafeZone global
    /// point, ToolTipService_Partial.cpp:1060-1098) so the bubble can stay hit-test-INVISIBLE like a real tooltip.</summary>
    public Point2? PointerPosition => _lastPointerValid ? _lastPointerPx : null;

    /// <summary>
    /// SIP reflow (input-a11y.md §10): scroll the focused editor's caret above the occluded region the touch keyboard
    /// reported (<see cref="Pal.IPlatformTextInput.OccludedRectChanged"/>). Walks from <see cref="Focused"/> to its
    /// nearest VERTICAL scrollable ancestor and, if the focused node's bottom edge sits below <paramref name="occludedTopDip"/>
    /// (window DIP — the pane top), increases that viewport's offset just enough to lift the field above it (+ a small
    /// margin), written through the <see cref="WriteScrollOffset"/> clamp chokepoint (so it inherits the clamp + virtual
    /// re-realize and can never push past the content). The WinUI EnsureFocusedElementInView the InputPaneHandler runs
    /// (InputPaneHandler.cpp → ScrollContentPresenter bring-into-view). A non-positive/empty rect (the pane hid) is a
    /// no-op. Returns true when it moved a viewport. 0-alloc (scalar walk + one ref write).
    /// </summary>
    public bool EnsureFocusedAboveOcclusion(float occludedTopDip)
    {
        if (occludedTopDip <= 0f || _focused.IsNull || !_scene.IsLive(_focused)) return false;

        // Nearest VERTICAL scrollable ancestor of the focused field (the panel reflow is a vertical bring-into-view; a
        // horizontal-only scroller cannot lift the caret clear of a bottom-docked keyboard).
        NodeHandle vp = NodeHandle.Null;
        for (var n = _scene.Parent(_focused); !n.IsNull; n = _scene.Parent(n))
            if ((_scene.Flags(n) & NodeFlags.Scrollable) != 0 && _scene.HasScroll(n) && _scene.ScrollRef(n).Orientation == 0)
            { vp = n; break; }
        if (vp.IsNull) return false;

        const float Margin = 8f;   // keep a sliver of breathing room between the field's bottom and the pane top
        RectF fieldAbs = _scene.AbsoluteRect(_focused);
        float overlap = (fieldAbs.Y + fieldAbs.H + Margin) - occludedTopDip;
        if (overlap <= 0f) return false;   // the field already clears the pane — nothing to scroll

        ref ScrollState sc = ref _scene.ScrollRef(vp);
        return WriteScrollOffset(vp, sc.OffsetY + overlap);   // clamp + virtual re-realize; bottom edge rides up by `overlap`
    }

    /// <summary>The gesture-arena coordinator driving the touch path (§7A). Internal — exposed for the validation.md
    /// §12.6 arena-determinism gate, which attaches a <see cref="GestureArenaRecorder"/> to <see cref="GestureArena.Recorder"/>
    /// before a scripted sequence and reads its <see cref="GestureArena.CaptureIsTentative"/> mid-gesture. No production
    /// surface is widened (the type and its members are <c>internal</c>); the host never touches this.</summary>
    internal GestureArena Arena => _arena;

    /// <summary>True while a touch contact's gesture arena has a long-press <c>Hold</c> member still armed (counting down
    /// to the ~500ms context flyout). The host ORs this into <c>WakeReasons.GestureHold</c> so a STATIONARY held finger
    /// — which emits no further input — still keeps frames coming, letting <see cref="TickGestureArenas"/> advance the
    /// long-press timer to the fire (§7A.4). Zero-cost when no arena is open; clears the instant the Hold resolves or the
    /// contact strays/lifts, so the idle mask returns to None right after the flyout fires.</summary>
    public bool HasArmedHold => _arena.HasArmedHold();

    /// <summary>True while a scrollable touch press is waiting out the 100ms WinUI pressed-visual delay.</summary>
    public bool HasPendingTouchPress
    {
        get
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Used && _slots[i].PendingTouchPress) return true;
            return false;
        }
    }

    /// <summary>The drag-reorder gesture engine (E5-L1): armed by a press on a <c>CanDrag</c> chain, promoted past the
    /// 4px drag box, owning the pointer until release/Escape. Constructed with the dispatcher so every host gets
    /// item-drag without wiring; the host hooks <see cref="DragController.OnSettle"/> for the FLIP drop-glide.</summary>
    public DragController Drag { get; }

    /// <summary>The typed drag-drop context (E5-L2, the Flutter Draggable/DragTarget + rbd model): a promoted L1 drag
    /// whose chain carries a <c>BoxEl.Draggable</c> opens THE <see cref="DragSession"/> here; per move the nearest
    /// accepting <c>BoxEl.DropTarget</c> under the pointer gets Enter/Over/Leave, release over it gets OnDrop, and
    /// the engine edge auto-scroll arms when the pointer drags near an overflowing viewport's edge (host-ticked).</summary>
    public DragDropContext DragDrop { get; }

    // ── External (OS) drop entry points ─────────────────────────────────────────────────────────────────────────────
    // The host's file-drop handler (the Windows backend's WM_DROPFILES case) calls these on the UI thread via the normal
    // message pump — wired in through InputHooks.ExternalDrag* (the inbound twin of OpenUri). They open / drive / commit
    // an external DragSession on the SAME DragDropContext as in-app drags, so a BoxEl.DropTarget that accepts
    // DropKinds.Files receives the drop identically. Coordinates are window-DIP (the host converts the drop point from
    // client px). Returned DropEffect → the OS drag cursor (None = no-drop). The seam keeps the richer Over/Leave shape
    // for backends that can supply hover feedback (a future OLE IDropTarget, or macOS); WM_DROPFILES uses Enter+Drop only.

    /// <summary>OLE DragEnter: open the external session (payload = the dragged <paramref name="paths"/>), hit-test, and
    /// report the effect the OS should show.</summary>
    public DropEffect ExternalDragEnter(Point2 windowDip, string[] paths, KeyModifiers mods)
    {
        DragDrop.ExternalBegin(DropKinds.Files, new FileDropData(paths), windowDip, mods);
        DragDrop.Move(HitTestAny(windowDip), windowDip, 0f, 0f, mods);
        return CurrentExternalEffect();
    }

    /// <summary>OLE DragOver: re-hit-test under the new point and report the live effect.</summary>
    public DropEffect ExternalDragOver(Point2 windowDip, KeyModifiers mods)
    {
        if (!DragDrop.IsActive) return DropEffect.None;
        DragDrop.Move(HitTestAny(windowDip), windowDip, 0f, 0f, mods);
        return CurrentExternalEffect();
    }

    /// <summary>OLE DragLeave / cancel: end the external session (fires OnLeave on a live target).</summary>
    public void ExternalDragLeave() => DragDrop.Cancel();

    /// <summary>Clear input state that points into a retained subtree before it is detached from the live scene chain.</summary>
    public void DeactivateSubtree(NodeHandle root)
    {
        if (root.IsNull || !_scene.IsLive(root)) return;

        bool cancelPointer = IsSelfOrAncestorOf(root, _hovered) || IsSelfOrAncestorOf(root, _pressed)
                             || IsSelfOrAncestorOf(root, _down) || IsSelfOrAncestorOf(root, _dragTarget)
                             || IsSelfOrAncestorOf(root, _scrollHovered) || IsSelfOrAncestorOf(root, _scrollDragNode)
                             || IsSelfOrAncestorOf(root, _panTarget) || IsSelfOrAncestorOf(root, _reorderTarget)
                             || IsSelfOrAncestorOf(root, _swipeDrag) || IsSelfOrAncestorOf(root, _gesturePanNode)
                             || IsSelfOrAncestorOf(root, _pinchViewport) || IsSelfOrAncestorOf(root, _pinchSessionViewport);
        for (int i = 0; !cancelPointer && i < _slots.Length; i++)
        {
            if (!_slots[i].Used) continue;
            ref PointerSlot s = ref _slots[i];
            cancelPointer = IsSelfOrAncestorOf(root, s.Down) || IsSelfOrAncestorOf(root, s.DragTarget)
                            || IsSelfOrAncestorOf(root, s.ScrollDragNode) || IsSelfOrAncestorOf(root, s.ContextDown)
                            || IsSelfOrAncestorOf(root, s.MiddleDown) || IsSelfOrAncestorOf(root, s.PanTarget)
                            || IsSelfOrAncestorOf(root, s.ReorderTarget) || IsSelfOrAncestorOf(root, s.SwipeDrag)
                            || IsSelfOrAncestorOf(root, s.GesturePanNode) || IsSelfOrAncestorOf(root, s.PinchViewport);
        }
        if (cancelPointer) CancelPointer();
        if (IsSelfOrAncestorOf(root, _keyArmed)) CancelKeyArm(fire: false);
        if (IsSelfOrAncestorOf(root, _focused)) SetFocus(NodeHandle.Null);
        if (IsSelfOrAncestorOf(root, _selText)) { _selText = NodeHandle.Null; _selDragging = false; }
    }

    /// <summary>OLE Drop WITH the dragged paths (the hover-capable backend reads the file list once, at drop, and passes
    /// it here — hover stayed data-free). Fills the session payload deferred from the data-free DragEnter, then commits
    /// (<c>OnDrop</c> sees the real <see cref="FileDropData"/>). Opens a session first if somehow none is live.</summary>
    public bool ExternalDropFiles(Point2 windowDip, string[] paths, KeyModifiers mods)
    {
        var data = new FileDropData(paths);
        if (!DragDrop.IsActive)
            DragDrop.ExternalBegin(DropKinds.Files, data, windowDip, mods);   // robustness: drop with no prior enter
        else
            DragDrop.Session.Payload = data;                                  // fill the payload the hover left empty
        DragDrop.Move(HitTestAny(windowDip), windowDip, 0f, 0f, mods);
        return DragDrop.TryDrop(windowDip, mods, out _);
    }

    /// <summary>OLE Drop: final hit-test then commit (<c>OnDrop</c> fires on an accepting target). Returns whether a
    /// target accepted the drop.</summary>
    public bool ExternalDrop(Point2 windowDip, KeyModifiers mods)
    {
        if (!DragDrop.IsActive) return false;
        DragDrop.Move(HitTestAny(windowDip), windowDip, 0f, 0f, mods);
        return DragDrop.TryDrop(windowDip, mods, out _);
    }

    private DropEffect CurrentExternalEffect()
        => DragDrop.IsActive && !DragDrop.OverTarget.IsNull ? DragDrop.Session.Effect : DropEffect.None;

    /// <summary>Edge auto-scroll for <see cref="DragDropContext"/> (scroll-feel-rework-v2 §2.1/§6): RECORD an exact
    /// absolute-offset intent (PendingTarget + PhaseImmediate) and arm the node — the phase-7 integrator writes the
    /// clamped offset (content transform + virtual re-realize + scrollbar reveal) verbatim the same frame. The dispatcher
    /// never moves the offset synchronously. Returns false at the boundary (the clamp can't advance — auto-scroll stops).</summary>
    private bool AutoScrollBy(NodeHandle n, float delta)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return false;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horiz = sc.Orientation == 1;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float max = horiz ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW) : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        float old = horiz ? sc.OffsetX : sc.OffsetY;
        float target = Math.Clamp(old + delta, 0f, max);
        if (target == old) return false;   // at the boundary → no move
        if (horiz) sc.PendingTargetX = target; else sc.PendingTargetY = target;
        sc.Phase = ScrollIntegrator.WheelAnimating;
        sc.PhaseFlags = ScrollState.PhaseImmediate;
        sc.FlingVelocity = 0f; sc.FlingRetargeted = false; sc.FlingSnapTarget = float.NaN;
        sc.IdleMs = 0f;
        OnScrollArmed?.Invoke(n);
        return true;
    }

    /// <summary>Absolute scroll-offset write seam for the <c>ScrollIntegrator</c> Fling integrator (wired in the host to
    /// <c>ScrollIntegrator.ScrollWrite</c>, the <see cref="AutoScrollBy"/> precedent that hands the private
    /// <c>SetScrollOffset</c> chokepoint to another assembly without making it public). Returns whether the viewport
    /// moved — a false (a clamp boundary, or no change) ends the fling. <c>SetScrollOffset</c> itself stays private.</summary>
    public bool WriteScrollOffset(NodeHandle n, float offset)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return false;
        // §2.1/§8 single-writer: this IS the phase-7-tick write seam — raise _inPhaseTick (so the chokepoint's guard
        // passes) and tag the OffsetWrite as ScrollWriter.Integrator, both restored in finally (re-entrancy-safe).
        var prev = _scrollWriter;
        bool prevTick = _inPhaseTick;
        _scrollWriter = FluentGpu.Foundation.ScrollWriter.Integrator;
        _inPhaseTick = true;
        try { return SetScrollOffset(n, offset); }
        finally { _scrollWriter = prev; _inPhaseTick = prevTick; }
    }

    /// <summary>Overscroll-band write seam for the <see cref="ScrollIntegrator"/> spring-back (wired in the host to
    /// <c>ScrollIntegrator.OverscrollWrite</c>, the <see cref="WriteScrollOffset"/> precedent). Sets the rubber-band
    /// displacement and RE-APPLIES the content transform (the band is composed there with the unchanged -offset, so the
    /// content displaces even though the offset is untouched — the clamp contract holds). The offset is NOT touched (the
    /// band is purely visual); only the ContentNode's LocalTransform changes (TransformDirty). 0-alloc.</summary>
    public void WriteOverscroll(NodeHandle n, float bandPx)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.OverscrollPx = bandPx;
        bool horizontal = sc.Orientation == 1;
        float at = horizontal ? sc.OffsetX : sc.OffsetY;   // offset unchanged; the band rides the SAME content translation
        // §2.1/§8 single-writer: this IS a phase-7-tick write seam — raise _inPhaseTick so the chokepoint's guard passes.
        bool prevTick = _inPhaseTick;
        _inPhaseTick = true;
        try { ApplyScrollPosition(n, ref sc, horizontal, at); }
        finally { _inPhaseTick = prevTick; }
    }

    /// <summary>The active contact's sampled flick velocity (px/s, window space; the ~50ms-EMA the touch fling uses). The
    /// working scalars are loaded for the contact whose event is being dispatched, so a control reading this from its
    /// <c>OnClick</c> commit edge (the release of a DragYieldsToPan swipe — SwipeControl/FlipView) gets THAT gesture's
    /// real release speed for the WinUI snap (100px open / 31px/s close; FlipView flick-navigate). Zero between gestures
    /// and for a mouse/0-stamp stream (the vacuous-fling guard). Wired to the InputHooks seam in AppHost (the
    /// <c>GetNodeRect</c>/<c>OnFlingStarted</c> delegate-seam precedent — no per-call allocation).</summary>
    public Point2 PointerVelocity => new(_panVel.Vx, _panVel.Vy);

    /// <summary>Set by the host: a virtual list crossing an item boundary on scroll requests the next render.</summary>
    public Action RequestRerender { get; set; } = static () => { };

    /// <summary>Set by the host: notified when a node gains/loses hover or press, so the interaction animator can ease
    /// the brush transition (kept as delegates to keep Input decoupled from the Animation assembly).</summary>
    public Action<NodeHandle, bool>? OnHoverChanged;
    public Action<NodeHandle, bool>? OnPressChanged;

    /// <summary>The active scroll feel profile. The host sets it from the app's <see cref="ScrollTuning"/>; defaults to
    /// <see cref="ScrollTuning.WinUiLike"/>. The per-notch wheel distance and accumulated-velocity ceiling are read here;
    /// a DIP-only ScrollDelta (the headless harness) bypasses notch scaling but still observes the momentum ceiling.</summary>
    public ScrollTuning Tuning { get; set; } = ScrollTuning.WinUiLike;

    /// <summary>When true, a wheel sets the scroll TARGET and the ScrollIntegrator eases the offset (momentum/inertia +
    /// auto-hiding scrollbars). When false, the offset jumps immediately (the deterministic default for tests).</summary>
    public bool SmoothScroll;
    /// <summary>Set by the host: arm a viewport in the ScrollIntegrator after a smooth-scroll wheel (decouples Input from Animation).</summary>
    public Action<NodeHandle>? OnScrollArmed;

    /// <summary>Set by the host: pointer is over a scrollable viewport → reveal its auto-hiding scrollbar.</summary>
    public Action<NodeHandle, bool>? OnScrollHover;
    public Action<NodeHandle>? OnScrollLeave;

    /// <summary>Set by the host: a touch pan released with a flick speed (px/s) above <see cref="ScrollTuning.FlingSeedGate"/>
    /// hands its sampled velocity off here — the Fling stage seeds the friction-decay inertia on the viewport's
    /// ScrollIntegrator from it (the <c>OnScrollArmed</c>/<c>AutoScrollBy</c> delegate-seam precedent;
    /// <c>SetScrollOffset</c> stays private to Input). The velocity sign matches a positive scroll delta (offset
    /// increases). Null = no fling stage wired yet — a released pan simply settles where it ended.</summary>
    public Action<NodeHandle, float /*velocityPxPerS*/>? OnFlingStarted;

    // ── scroll-feel-rework-v2 §2.1/§2.3: the dispatcher is a pure intent recorder for the phase-driven scroll gesture.
    // These seams record contact intent onto the integrator resampler; phase 7 (ScrollIntegrator.Tick) resamples them to
    // frame time and is the SOLE writer of ScrollState.OffsetX/Y + OverscrollPx. Wired in AppHost to
    // ScrollIntegrator.BeginTracking / AppendContactSample / EndTracking (the OnScrollArmed delegate-seam precedent —
    // Input stays decoupled from Animation; no per-call allocation). Null = unwired (a bare dispatcher records nothing).
    /// <summary>Begin contact tracking on a viewport (latch): (node, horizontalAxis, anchorOffset). Sets
    /// <see cref="ScrollIntegrator.TouchpadTracking"/>, latches the resampler anchor, and arms the node.</summary>
    public Action<NodeHandle, bool, float>? OnScrollTrackBegin;
    /// <summary>Append one contact sample to the integrator resampler: (nodeIndex, qpcSeconds, cumulativeAxisPositionDip).</summary>
    public Action<int, double, float>? OnScrollTrackSample;
    /// <summary>End contact tracking (clear the resampler latch).</summary>
    public Action<NodeHandle>? OnScrollTrackEnd;
    /// <summary>Set by the host (→ <c>ScrollIntegrator.CancelFling</c>): every <c>PointerDown</c> (mouse/touch/pen) over a
    /// scrolling viewport and every scrollbar grab zeros the viewport's coast/chase (a click over a coasting viewport must
    /// not drift under the pointer — scroll-feel-rework-v2 §2.2, fixes R6's dead <c>CancelFling</c>); a live rubber-band is
    /// handed to the phase-7 SnapBack spring so a re-grab over a stretched edge is not erased. Idempotent; null = unwired.</summary>
    public Action<NodeHandle>? OnCancelFling;
    public Action<Point2>? OnPointerDownObserved;
    public Action? OnScrollStartedObserved;

    /// <summary>Set by the host: a RepeatButton was pressed (held) / released — drives the RepeatTicker auto-repeat.</summary>
    public Action<NodeHandle>? OnRepeatArmed;
    public Action<NodeHandle>? OnRepeatReleased;
    /// <summary>Set by the host: the held pointer left / re-entered the armed repeat node — the ticker pauses off-node
    /// and resumes with a FRESH initial delay on re-entry, never an immediate re-fire (RepeatButton_Partial.cpp:530-574).
    /// Both are idempotent per move.</summary>
    public Action<NodeHandle>? OnRepeatPaused;
    public Action<NodeHandle>? OnRepeatResumed;

    /// <summary>Set by the host: a global key preview run before focus routing (returns true = consumed). Lets a tree-level
    /// concern (an open overlay/flyout) intercept Escape regardless of where focus is, without stealing focus.</summary>
    public Func<int, bool>? OnKeyPreview;

    /// <summary>Raised when the window loses activation: pressed/hover/drag state has been cleared; the host closes
    /// light-dismiss overlays here (WinUI window-deactivation dismiss).</summary>
    public Action? OnWindowBlur;

    /// <summary>Raised on window activation/placement changes (WindowFocus, WindowBlur, WindowStateChanged): the host
    /// bumps the titlebar-chrome epoch signal here so a custom TitleBar re-renders (dimming / max↔restore glyph).</summary>
    public Action? OnWindowActivationChanged;

    /// <summary>Raised when the resolved hover cursor changes — the host wires this to <c>IPlatformWindow.SetCursor</c>.</summary>
    public Action<CursorId>? OnCursorChanged;

    /// <summary>Text seam for the read-only selection/hyperlink gestures (point↔index hit-testing + selection rects —
    /// the SAME editor queries EditableText drives). Null falls back to <see cref="TextSeam.Default"/> (set by the
    /// font-system constructors), so hosts that predate this seam still get selection without wiring.</summary>
    public IFontSystem? Fonts { get; set; }

    /// <summary>Clipboard seam for Ctrl+C over a read-only selection (WinUI TextSelectionManager::CopySelectionToClipboard,
    /// TextSelectionManager.cpp:30-41). Set by the host; null = copy is a no-op.</summary>
    public IClipboard? Clipboard { get; set; }

    // ── focus scopes (modal focus trap: ContentDialog / flyout) ───────────────────────────────────
    /// <summary>Push a focus scope: Tab/Shift+Tab and arrow focus stay within <paramref name="root"/>'s subtree until popped.</summary>
    public void PushFocusScope(NodeHandle root) => _focusScopes.Add(root);
    public void PopFocusScope() { if (_focusScopes.Count > 0) _focusScopes.RemoveAt(_focusScopes.Count - 1); }
    /// <summary>Remove the focus scope for <paramref name="root"/> wherever it sits in the stack (overlays can close
    /// out of stack order - popping blindly could drop another live trap).</summary>
    public void RemoveFocusScope(NodeHandle root)
    {
        for (int i = _focusScopes.Count - 1; i >= 0; i--)
            if (_focusScopes[i] == root) { _focusScopes.RemoveAt(i); return; }
    }
    private NodeHandle ScopeRoot
    {
        get
        {
            for (int i = _focusScopes.Count - 1; i >= 0; i--)
                if (_scene.IsLive(_focusScopes[i])) return _focusScopes[i];
            return _scene.Root;
        }
    }

    public int Dispatch(ReadOnlySpan<InputEvent> events) => Dispatch(events, ReadOnlySpan<PointerVelSample>.Empty);

    public int Dispatch(ReadOnlySpan<InputEvent> events, ReadOnlySpan<PointerVelSample> velSamples)
    {
        // Stash this frame's pre-coalesce velocity samples (fed per-contact as its events process — see FeedPanVelocity).
        _velSampleCount = Math.Min(velSamples.Length, _velSamples.Length);
        for (int i = 0; i < _velSampleCount; i++) _velSamples[i] = velSamples[i];

        // Drop transient state that pointed at a freed node.
        if (!_focused.IsNull && !_scene.IsLive(_focused)) _focused = NodeHandle.Null;
        if (!_hovered.IsNull && !_scene.IsLive(_hovered)) _hovered = NodeHandle.Null;
        if (!_pressed.IsNull && !_scene.IsLive(_pressed)) _pressed = NodeHandle.Null;
        if (!_keyArmed.IsNull && !_scene.IsLive(_keyArmed)) { _keyArmed = NodeHandle.Null; _keyArmedKey = 0; }
        if (!_scrollHovered.IsNull && !_scene.IsLive(_scrollHovered)) _scrollHovered = NodeHandle.Null;
        if (!_selText.IsNull && !_scene.IsLive(_selText)) { _selText = NodeHandle.Null; _selDragging = false; }
        if (!_pinchSessionViewport.IsNull && !_scene.IsLive(_pinchSessionViewport)) EndPinchSession();   // a reconciled-away zoom viewport ends the pinch
        PruneDeadSlots();      // every contact's per-pointer down/drag/scroll-drag/pan target dropped if its node died
        Drag.PruneDead();      // an armed/active drag node freed by a reconcile is abandoned (its columns are dead)
        DragDrop.PruneDead();  // a session whose source/target/viewport died: end / drop the dead reference

        int handled = 0;
        foreach (ref readonly var e in events)
        {
            // Per-contact capture: load this PointerId's saved targets/anchor into the working scalars for the duration
            // of the event, then store them back (SlotOut). PointerCancel for one id clears only that contact (the
            // whole-reset stays the WindowBlur path). Non-pointer events (key/char/window) leave the scalars untouched.
            bool pointerEvent = e.Kind is InputKind.PointerMove or InputKind.PointerDown or InputKind.PointerUp or InputKind.PointerCancel;
            // Sync the arena timer clock forward to the latest touch stamp (the long-press tick runs on this clock; a
            // non-zero stamp keeps it monotonic — a 0 stamp is the headless vacuous-time sentinel and never rewinds it).
            if (pointerEvent && e.Pointer == PointerKind.Touch && e.TimestampMs != 0)
            {
                long us = ToUs(e.TimestampMs);
                if (us > _arenaClockUs) _arenaClockUs = us;
            }
            // Track the focus-causing pointer's device class BEFORE the switch routes the down/up (which moves focus and
            // fires OnFocusChanged → the EditableText SIP policy reads LastPointerWasTouch). PointerCancel is capture LOSS,
            // not a focus-moving input, so it never rewrites the kind (a touch-up's cancel must not look like a mouse).
            if (e.Kind is InputKind.PointerDown or InputKind.PointerMove or InputKind.PointerUp) _lastPointerKind = e.Pointer;
            // §5.4: remember the last mouse/pen position so a phase-7 scroll can re-resolve hover at this point when the
            // cursor is stationary. Mouse/pen only — a touch contact never latches a resting hover (the kind gate below).
            if (e.Pointer != PointerKind.Touch && e.Kind is InputKind.PointerDown or InputKind.PointerMove or InputKind.PointerUp)
            { _lastPointerPx = e.PositionPx; _lastPointerValid = true; }
            if (pointerEvent)
            {
                SlotIn(e.PointerId);
                if (!_activeSlotValid) continue;   // the 11th concurrent contact has no seat → ignored, no side-effects
            }

            switch (e.Kind)
            {
                case InputKind.PointerMove:
                    if (e.Pointer == PointerKind.Touch) { if (TouchMove(in e)) handled++; break; }
                    if (Drag.IsActive)   // an active item-drag owns the pointer (capture): no hover/scroll/slider routing
                    {
                        Drag.Move(e.PositionPx, e.Mods, e.TimestampMs);
                        // L2: target Enter/Over/Leave transitions + edge auto-scroll on the chain under the pointer
                        // (the lifted subtree is hit-test-transparent, so the chain sees THROUGH the moving visual).
                        // Gated on a live session — a plain CanDrag reorder never pays the extra hit-test walk.
                        if (DragDrop.IsActive)
                            DragDrop.Move(HitTestAny(e.PositionPx), e.PositionPx, Drag.VelocityX, Drag.VelocityY, e.Mods);
                        handled++;
                        break;
                    }
                    if (Drag.IsArmed && Drag.Move(e.PositionPx, e.Mods, e.TimestampMs))
                    {
                        // Promoted on this move: the gesture is a drag now — kill the click candidate, the transient
                        // press/hover visuals and any pending auto-repeat (WinUI: crossing the drag box cancels them).
                        if (!_down.IsNull && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                            OnRepeatReleased?.Invoke(_down);
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
                        _dragTarget = NodeHandle.Null;
                        // L2: a chain carrying a BoxEl.Draggable opens THE session (payload resolved once at this
                        // promotion edge) and immediately evaluates the target under the pointer.
                        if (DragDrop.TryBegin(Drag.ActiveNode, e.PositionPx, e.Mods, e.Pointer))
                            DragDrop.Move(HitTestAny(e.PositionPx), e.PositionPx, Drag.VelocityX, Drag.VelocityY, e.Mods);
                        handled++;
                        break;
                    }
                    SetState(ref _hovered, HitTest(e.PositionPx), NodeFlags.Hovered);
                    // Hyperlink spans re-resolve the cursor on EVERY move over the same text node — the span boundary
                    // crossings happen inside one node, which the on-hover-change walk alone can't see (WinUI flips to
                    // the hand per pointer-move over an inline Hyperlink, RichTextBlock.cpp:2995 / TextBlock.cpp:3488).
                    if (!_hovered.IsNull && _scene.IsLive(_hovered)
                        && (_scene.Interaction(_hovered).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
                        UpdateSpanCursor(_hovered, e.PositionPx);
                    // While a press is held WITHOUT a capture-drag (no OnDrag target / scrollbar drag), the pressed
                    // visual tracks whether the pointer is still over the pressed node — drag-off un-presses,
                    // drag-back re-presses (ButtonBase_Partial.cpp:629-638 IsPressed = IsValidPointerPosition). A
                    // continuous OnDrag gesture (slider scrub) keeps its pressed state — WinUI's captured thumb does.
                    if (!_down.IsNull && _dragTarget.IsNull && _scrollDragNode.IsNull && _scene.IsLive(_down))
                    {
                        var dl = PointToLocal(_down, e.PositionPx);   // scale-aware (a button inside a Viewbox)
                        ref RectF db = ref _scene.Bounds(_down);
                        bool overDown = dl.X >= 0f && dl.X < db.W && dl.Y >= 0f && dl.Y < db.H;
                        SetState(ref _pressed, overDown ? _down : NodeHandle.Null, NodeFlags.Pressed);
                        // Auto-repeat pauses off-node and resumes with a FRESH delay on re-entry — no immediate
                        // re-fire (RepeatButton_Partial.cpp:530-548, :565-574). Idempotent per move.
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                        {
                            if (overDown) OnRepeatResumed?.Invoke(_down);
                            else OnRepeatPaused?.Invoke(_down);
                        }
                    }
                    // Read-only selection drag (rtb-02): the press anchored on a selectable text node — every move
                    // extends the selection at pointer rate (the seam clamps out-of-bounds points, so the drag keeps
                    // tracking past the box exactly like the editor's drag-select).
                    if (_selDragging && !_selText.IsNull && _down == _selText && _scene.IsLive(_selText))
                    {
                        ExtendTextSelection(PointToLocal(_selText, e.PositionPx));
                        handled++;
                        break;
                    }
                    UpdateScrollHover(e.PositionPx);
                    if (DragScrollbar(e.PositionPx))
                    {
                        handled++;
                        break;
                    }
                    if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget))   // drag updates while held (slider/scrollbar)
                    {
                        // UNCLAMPED pointer-local (PointToLocal, not the box-clamped LocalPos): an OnDrag handler either
                        // reconstructs the true cursor (the sidebar resize grip does local.X + AbsoluteRect — clamping to
                        // the 16px grip box gave a dead-band + lag on every direction reversal) or clamps its OWN output
                        // (Slider value, caret index, selection extent). Clamping here only ever broke the first kind.
                        _scene.GetDrag(_dragTarget)?.Invoke(PointToLocal(_dragTarget, e.PositionPx));
                        handled++;
                    }
                    else if (!_hovered.IsNull && _scene.GetHoverMove(_hovered) is { } hm)   // bare-hover preview (RatingControl)
                    {
                        if (Diag.Enabled)
                        {
                            ref RectF hb = ref _scene.Bounds(_hovered);
                            Diag.Event("hover", $"deliver node={_hovered.Raw.Index} box={hb.W:0}x{hb.H:0} abs=({e.PositionPx.X:0.#},{e.PositionPx.Y:0.#})");
                        }
                        hm(LocalPos(_hovered, e.PositionPx));
                        handled++;
                    }
                    break;

                case InputKind.PointerDown:
                    OnPointerDownObserved?.Invoke(e.PositionPx);
                    if (e.Pointer == PointerKind.Touch) { if (TouchDown(in e)) handled++; break; }
                    if (e.Button == 1)   // right button: context-menu tracking only — never presses/activates
                    {
                        // Interaction-gated walk (Hit, not HitTestAny): a handler-less full-bleed layer above the
                        // target — the overlay's anchored-popup positioning wrapper — must not swallow the context
                        // chain the way it never swallows clicks. ContextBit is in Hit()'s self-hit mask.
                        _contextDown = HitTest(e.PositionPx);
                        break;
                    }
                    if (e.Button == 2)   // middle button: tracked for release-over-same delivery (TabView middle-click
                    {                    // close commits on RELEASE over the tab, TabViewItem.cpp:418-462) — no press
                        _middleDown = HitTestAny(e.PositionPx);   // visual, no focus move, no click
                        break;
                    }
                    if (e.Button != 0) break;

                    // Any mouse/pen PRESS over a coasting/animating viewport zeros its motion first (scroll-feel-rework-v2
                    // §2.2, fixes R6's dead CancelFling): a click must not drift under the pointer, and a scrollbar grab
                    // must not fight a live chase. A live band is handed to the phase-7 SnapBack spring (not erased).
                    OnCancelFling?.Invoke(ScrollableUnder(e.PositionPx));

                    if (TryScrollbarPointerDown(e.PositionPx))
                    {
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        _down = NodeHandle.Null;
                        handled++;
                        break;
                    }

                    TrackClickCount(in e);
                    _pressClickCount = _clickCount;
                    _pressMods = e.Mods;
                    _pressKind = e.Pointer;
                    _down = HitTest(e.PositionPx);
                    SetState(ref _pressed, _down, NodeFlags.Pressed);
                    // A true blank-area press has no interaction-gated hit at all. It is still an explicit click-away:
                    // clear keyboard focus even though there is no `_down` node to enter the normal focus branch below.
                    if (_down.IsNull && !_focused.IsNull && _scene.IsLive(_focused)) SetFocus(NodeHandle.Null);
                    // A press anywhere OUTSIDE the selection's node dismisses it (WinUI: pointer-down resets the
                    // text selection unless it lands back in the selectable control).
                    if (!_selText.IsNull && _selText != _down) ClearTextSelection();
                    if (!_down.IsNull)
                    {
                        // Pointer focus moves on the PRESS edge (WinUI Focus(FocusState_Pointer) + CapturePointer in
                        // OnPointerPressed, ButtonBase_Partial.cpp:700-709), never on release. Nearest focusable
                        // self-or-ancestor, without the focus ring; an AllowFocusOnInteraction=False node blocks the
                        // move entirely (focus stays put — AppBarButton_themeresources.xaml:136). IsTabStop=False
                        // parts (PasswordBox reveal / TextBox delete) still fall through to the field root.
                        var focusTarget = NearestFocusable(_down);
                        if (!focusTarget.IsNull &&
                            (_scene.Interaction(focusTarget).HandlerMask & InteractionInfo.NoPointerFocusBit) == 0)
                            SetFocus(focusTarget, visual: false);
                        // DIVERGENCE (input-a11y §8): a mouse press on INERT BACKGROUND — nothing focusable self-or-ancestor
                        // (focusTarget == Null) AND the hit node advertises no press handlers (AnyInteractiveMask == 0) —
                        // CLEARS focus. SetFocus(Null) drops _focused, clears Focused|FocusVisual (the white ring repaints
                        // away) and fires the field's OnFocusChanged(false): edit commit + validate-on-blur + caret hide +
                        // SIP dismiss (EditableText.cs:511-528). An interactive-but-non-focusable hit (light-dismiss/modal
                        // scrim, OnDrag/OnPointer node, CanDrag handle, selectable label, hyperlink, gesture/wheel node) is
                        // NOT background and KEEPS focus; a scrollbar press already returned above (:628). WinUI leaves focus
                        // put on a background click — we deliberately diverge (click-away-to-blur).
                        else if (focusTarget.IsNull && !_focused.IsNull && _scene.IsLive(_focused) &&
                                 (_scene.Interaction(_down).HandlerMask & InteractionInfo.NoPointerFocusBit) == 0)
                            SetFocus(NodeHandle.Null);

                        var local = LocalPos(_down, e.PositionPx);
                        _scene.GetPointerDown(_down)?.Invoke(local);                 // press-to-set
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.PressedBit) != 0)
                            _scene.GetPointerPressed(_down)?.Invoke(new PointerEventArgs
                            {
                                Local = local, ClickCount = _clickCount, Mods = e.Mods, Button = 0, Kind = e.Pointer,
                            });
                        if (_scene.GetDrag(_down) is not null) _dragTarget = _down;  // begin a drag gesture
                        else
                            // Arm a drag-reorder candidate on the nearest CanDrag ancestor (a press on a child of a
                            // draggable row arms the row). A continuous OnDrag press (slider) keeps its semantics.
                            Drag.TryArm(_down, e.PositionPx, e.Pointer, e.Mods, e.TimestampMs);
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                            OnRepeatArmed?.Invoke(_down);   // RepeatButton: fire click now, then repeat while held
                        // Read-only selection press (rtb-02): anchor / word-select / select-all per click count
                        // (single = caret anchor for the drag; double = word; triple = all — the RichEdit/WinUI shape).
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.SelectableTextBit) != 0)
                            BeginTextSelection(_down, PointToLocal(_down, e.PositionPx), _clickCount);
                        if ((_scene.Interaction(_down).HandlerMask & (InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.RepeatBit | InteractionInfo.DragBit | InteractionInfo.SelectableTextBit | InteractionInfo.SpanLinksBit)) != 0) handled++;
                    }
                    break;

                case InputKind.PointerUp:
                    if (e.Pointer == PointerKind.Touch) { if (TouchUp(in e)) handled++; break; }
                    if (e.Button == 1)   // right button release → context request on the nearest handler in the chain
                    {
                        var ctxHit = HitTest(e.PositionPx);   // interaction-gated, matches the press tracking above
                        if (!ctxHit.IsNull && ctxHit == _contextDown && DispatchContextRequest(ctxHit, e.PositionPx, ContextRequestTrigger.Pointer)) handled++;
                        _contextDown = NodeHandle.Null;
                        break;
                    }
                    if (e.Button == 2)   // middle release over the same node → typed pointer args with Button=2 on the
                    {                    // nearest OnPointerPressed in the chain (WinUI TabViewItem middle-click close
                        var midHit = HitTestAny(e.PositionPx);   // commits on PointerReleased, TabViewItem.cpp:418-462)
                        if (!midHit.IsNull && midHit == _middleDown && DispatchMiddleRelease(midHit, in e)) handled++;
                        _middleDown = NodeHandle.Null;
                        break;
                    }
                    if (e.Button != 0) break;

                    if (Drag.IsActive)
                    {
                        // Release after an active item-drag: L2 drop FIRST (OnDrop reads the live session while the
                        // visuals are still lifted), then the L1 completion; the click is SUPPRESSED (WinUI — a
                        // finished drag never raises the item's click/Tapped). A drop on a non-reorder target
                        // suppresses the spring-back glide (the payload was deposited there); a reorder target
                        // (DropTargetSpec.SettleOnDrop) keeps the FLIP drop-glide into the new slot.
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        bool dropped = DragDrop.TryDrop(e.PositionPx, e.Mods, out bool settleGlide);
                        Drag.Complete(e.PositionPx, e.Mods, e.TimestampMs, suppressSettle: dropped && !settleGlide);
                        _down = NodeHandle.Null;
                        _dragTarget = NodeHandle.Null;
                        handled++;
                        break;
                    }
                    Drag.Disarm();   // armed but never promoted → a plain click; fall through to normal release

                    if (!_scrollDragNode.IsNull)
                    {
                        _scrollDragNode = NodeHandle.Null;
                        handled++;
                        break;
                    }

                    var up = HitTest(e.PositionPx);
                    bool wasRepeat = !_down.IsNull && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0;
                    if (wasRepeat) OnRepeatReleased?.Invoke(_down);   // stop the auto-repeat
                    SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);   // release
                    _selDragging = false;   // the selection (if any) stays; the drag gesture ends with the press
                    if (!up.IsNull && up == _down)
                    {
                        DispatchPointerReleased(up, e.PositionPx);
                        // Click on release-over-same (ClickMode.Release). Pointer FOCUS already moved on the press
                        // edge (WinUI ButtonBase_Partial.cpp:700-709) — the release only fires the click.
                        if (!wasRepeat) InvokeActivation(up, ContextRequestTrigger.Invoke);   // repeat nodes already fired via the ticker
                        // Hyperlink span click: release over the span's laid rect fires ITS action (WinUI inline
                        // Hyperlink commits on the release over the pressed hyperlink, RichTextBlock.cpp:2996-3001).
                        if ((_scene.Interaction(up).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
                        {
                            int si = HitLinkSpan(up, PointToLocal(up, e.PositionPx));
                            if (si >= 0 && _scene.TryGetSpanText(up, out var linkSpans) && (uint)si < (uint)linkSpans.Length)
                                linkSpans[si].OnClick?.Invoke();
                        }
                        handled++;
                    }
                    else if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget) &&
                             (_scene.Flags(_dragTarget) & NodeFlags.Disabled) == 0)
                    {
                        // Implicit pointer capture for continuous OnDrag gestures (WinUI CapturePointer): a press that
                        // began an OnDrag gesture (slider scrub, RatingControl sweep, ToggleSwitch knob drag) delivers
                        // its RELEASE to that node even when the pointer ends outside it — the node's click handler is
                        // its release/commit edge (RatingControl.cpp:875-906 capture → commit-on-release incl. the
                        // drag-off-left clear; Slider_Partial.cpp:478-543/580-623 CapturePointer → PerformPointerUpAction).
                        // PointerCancel still skips this (capture loss is not a commit), matching WinUI's cancel path.
                        // (Focus moved on the press edge, like every pointer gesture.)
                        InvokeActivation(_dragTarget, ContextRequestTrigger.Invoke);   // one commit path (a drag node is never a ContextBit invoker in practice)
                        handled++;
                    }
                    // Touch never reaches here (it routes through TouchUp, which clears its transient hover); this is the
                    // mouse/pen release, which keeps hovering the node under the cursor.
                    _down = NodeHandle.Null;
                    _dragTarget = NodeHandle.Null;
                    break;

                case InputKind.Key:
                    OnKey(in e);
                    break;

                case InputKind.KeyUp:
                    OnKeyUp(in e);
                    break;

                case InputKind.Char:
                    if (OnChar(e.KeyCode)) handled++;
                    break;

                case InputKind.Wheel:
                    // A detented mouse / free-spin wheel ends any live phase-driven scroll gesture BEFORE any handler or
                    // viewport consumes the wheel (one integrator owns the offset at a time — the cross-device takeover
                    // contract). Producers emit phase kinds (ScrollBegin/Update/End) for touchpad panning now; a stray
                    // Touchpad-tagged Wheel event (stale producer) degrades to the eased DIP path below, never a 2nd integrator.
                    CancelGesture();
                    // Element-level wheel handlers (WinUI PointerWheelChanged) see the wheel BEFORE the viewport:
                    // a Handled NumberBox consumes the step instead of scrolling the form (NumberBox.cpp:578-597).
                    if (DispatchWheel(in e)) { handled++; break; }
                    {
                        // Mouse / free-spin wheel: a device NOTCH amount → the discrete max(48 DIP, 10%·viewport) eased
                        // notch; a synthetic DIP-only ScrollDelta (the headless harness) is used directly. (Touchpad pans
                        // arrive as ScrollBegin/Update/End phase events and never reach here.)
                        bool useNotch = e.WheelNotch != 0f || e.WheelNotchX != 0f;
                        float wAxisY = useNotch ? e.WheelNotch : e.ScrollDelta;
                        float wAxisX = useNotch ? e.WheelNotchX : e.ScrollDeltaX;
                        if (ScrollAt(e.PositionPx, wAxisY, wAxisX, useNotch, e.TimestampMs)) handled++;
                    }
                    break;

                // ── the phase-tagged scroll contract (design §1/§6): the ONE consumer for every producer ──
                case InputKind.ScrollBegin:
                case InputKind.ScrollUpdate:
                case InputKind.ScrollEnd:
                case InputKind.MomentumBegin:
                case InputKind.MomentumUpdate:
                case InputKind.MomentumEnd:
                    OnScrollPhase(in e);
                    handled++;
                    break;

                case InputKind.PointerCancel:
                    CancelPointerContact(in e);
                    EndScrollGesture(springBand: true);   // cancel == end-with-zero-velocity (checklist #16)
                    break;

                case InputKind.WindowBlur:
                    CancelPointer();
                    EndScrollGesture(springBand: true);   // a live gesture dies with activation; its band springs home
                    CancelKeyArm(fire: false);
                    SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
                    // Scroll hover is a separate retained target from normal element hover. Leaving it latched across
                    // deactivation strands ScrollState.PointerOver/_scrollHovered on opposite sides of the lifecycle;
                    // after the bar auto-hides, reactivation can then fail to produce a fresh reveal edge. Explicitly
                    // leave and clear it so the first post-focus pointer move always re-arms the viewport consciously.
                    if (!_scrollHovered.IsNull && _scene.IsLive(_scrollHovered)) OnScrollLeave?.Invoke(_scrollHovered);
                    _scrollHovered = NodeHandle.Null;
                    _lastPointerValid = false;   // §5.4: the cursor left the client area — no stationary point to re-resolve hover at
                    _accessKeyMode = false; _altPending = false;
                    OnWindowBlur?.Invoke();
                    OnWindowActivationChanged?.Invoke();   // custom titlebar dims (TextTertiary / disabled glyphs)
                    break;

                case InputKind.WindowFocus:
                    OnWindowActivationChanged?.Invoke();   // custom titlebar un-dims
                    break;

                case InputKind.WindowStateChanged:
                    OnWindowActivationChanged?.Invoke();   // custom titlebar re-glyphs max↔restore
                    break;
            }

            if (pointerEvent) SlotOut();   // store this contact's working scalars back into its slot (and recycle if idle)
        }
        return handled;
    }

    /// <summary>Clear ALL in-flight pointer interaction across every contact (window deactivated — the WindowBlur path,
    /// no single PointerId). Each live slot's capture side-effects fire (RepeatReleased / OnPointerExit / hover clear)
    /// then its slot is recycled.</summary>
    private void CancelPointer()
    {
        DragDrop.Cancel();   // L2 first: OnLeave fires on a live target while the session still exists
        Drag.Cancel();   // an in-flight item-drag aborts on capture loss (restores visuals, fires OnDragCanceled)
        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Used) continue;
            SlotIn(_slots[i].Id);
            CancelWorkingContact(fireDragCancelEngines: false);   // Drag/DragDrop already cancelled once above
            SlotOut();
        }
        _selDragging = false;   // capture lost mid-drag-select: keep the selection, end the gesture
    }

    /// <summary>Per-id capture loss (WM_POINTERCAPTURECHANGED → one contact): clear ONLY this contact's slot/state,
    /// leaving every other live contact alone. A claimed touch pan ends with NO fling (capture loss is not a flick).
    /// Runs inside the contact's SlotIn/SlotOut window, so it operates on the loaded working scalars.</summary>
    private void CancelPointerContact(in InputEvent e)
    {
        // A captured item-drag/L2 session is single-pointer today (mouse/pen) — cancel it on this contact's loss.
        if (Drag.IsActive || DragDrop.IsActive) { DragDrop.Cancel(); Drag.Cancel(); }
        if (e.Pointer == PointerKind.Touch) ClearTouchHover();   // a touch contact never leaves a latched hover behind
        // A contact lost mid-thumb-drag drops the bar's PointerOverScrollbar/PointerOver reveal so it fades (touch has no
        // resting hover to keep it up). Captured BEFORE CancelWorkingContact nulls _scrollDragNode.
        if (!_scrollDragNode.IsNull) OnScrollLeave?.Invoke(_scrollDragNode);
        // Clear the pressed glyph IFF it belongs to this contact (the global singleton tracks the live mouse/pen OR touch
        // press, which while held is always either this contact's _down or null). CancelWorkingContact's doc defers the
        // pressed clear to the caller: the whole-window CancelPointer clears it for all contacts, this per-id path only
        // for its own — a held contact that loses its implicit capture (WM_POINTERCAPTURECHANGED) must not leave the
        // pressed visual latched until the next PointerDown.
        if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        CancelWorkingContact(fireDragCancelEngines: false);
    }

    /// <summary>Abort the working contact (the SlotIn-loaded scalars): fire RepeatReleased / the captured OnDrag node's
    /// OnPointerExit, drop a claimed touch pan (no fling), and clear every capture target. The pressed visual (mouse/pen
    /// only) is cleared by the caller. <paramref name="fireDragCancelEngines"/> = also cancel the Drag/DragDrop engines
    /// here (false when the caller already did).</summary>
    private void CancelWorkingContact(bool fireDragCancelEngines)
    {
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        if (fireDragCancelEngines) { DragDrop.Cancel(); Drag.Cancel(); }
        if (!_down.IsNull && _scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
            OnRepeatReleased?.Invoke(_down);
        // The captured OnDrag gesture owner learns its gesture died (WinUI PointerCaptureLost): controls reset the
        // scrub/hover preview in OnPointerExit — a RatingControl alt-tabbed mid-sweep must not keep its downRef.
        if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget)) _scene.GetPointerExit(_dragTarget)?.Invoke();
        _down = NodeHandle.Null;
        _dragTarget = NodeHandle.Null;
        _scrollDragNode = NodeHandle.Null;
        _contextDown = NodeHandle.Null;
        _middleDown = NodeHandle.Null;
        // A claimed pan dies with the contact — no fling on capture loss. If it was holding the rubber band past the
        // clamp, release the band to the phase-7 spring-back so it doesn't stick displaced (the band still belongs to the
        // visual, not the offset, so it must return to 0).
        if (!_panTarget.IsNull && _scene.IsLive(_panTarget) && _scene.HasScroll(_panTarget))
        {
            OnScrollTrackEnd?.Invoke(_panTarget);   // §2.1: clear any resampler latch on capture loss (no stale replay)
            ref ScrollState psc = ref _scene.ScrollRef(_panTarget);
            psc.PendingRawOffset = float.NaN;       // stop the per-node touch-pan intent
            psc.PhaseFlags &= unchecked((byte)~ScrollState.PhaseTouchPan);
            if (psc.OverscrollPx != 0f)
            {
                psc.Overscrolling = false; psc.OverscrollVel = 0f;
                if (psc.Phase == ScrollIntegrator.TouchpadTracking || psc.Phase == ScrollIntegrator.Overscroll)
                    psc.Phase = ScrollIntegrator.SnapBack;   // band → spring-back (cancel == end-with-zero-velocity)
                OnScrollArmed?.Invoke(_panTarget);
            }
            else if (psc.Phase == ScrollIntegrator.TouchpadTracking) { psc.Phase = ScrollIntegrator.Idle; OnScrollArmed?.Invoke(_panTarget); }
        }
        _panTarget = NodeHandle.Null;
        _panClaimed = false;
        _reorderTarget = NodeHandle.Null;   // a claimed drag-reorder dies with the contact (Drag.Cancel above restored the visuals)
        _touchReorder = false;
        _swipeDrag = NodeHandle.Null;       // a cross-axis swipe dies with the contact (no OnClick commit on capture loss)
        _swipeAxisX = false;
        _gesturePanNode = NodeHandle.Null;
        // A contact lost mid-pinch ends the pinch session (the committed ZoomFactor stays — a partial pinch keeps its
        // magnification, exactly like a lifted finger commits the scale). Cleared whether or not this contact was a member.
        if (!_pinchSessionViewport.IsNull && (_activeSlotId == _pinchIdA || _activeSlotId == _pinchIdB)) EndPinchSession();
        _pinchViewport = NodeHandle.Null; _pinchMember = -1;
        // The contact's arena force-closes (§7A.5 PointerCaptureLost: the provisional winner wins, the rest reject — pure
        // cleanup here, no further execution) and frees its seat: the scalar cancel above already aborted the gesture.
        if (_activeArenaSlot >= 0) { _arena.ForceClose(_activeArenaSlot); _arena.CloseAndFree(_activeArenaSlot); _activeArenaSlot = -1; }
    }

    // ── per-contact capture slab (cap MaxContacts; the 11th concurrent contact is ignored deterministically) ────────

    /// <summary>Load <paramref name="id"/>'s saved capture/pan state into the working scalars (find-or-allocate its
    /// slot). When the slab is full of distinct LIVE contacts the new id gets no slot — the working scalars are zeroed
    /// and <see cref="_activeSlotValid"/> is false, so the event runs harmlessly and <see cref="SlotOut"/> discards it.</summary>
    private void SlotIn(uint id)
    {
        int slot = FindOrAllocSlot(id);
        _activeSlotId = id;
        _activeSlotValid = slot >= 0;
        if (slot < 0) { ClearWorkingScalars(); return; }
        ref PointerSlot s = ref _slots[slot];
        _down = s.Down; _dragTarget = s.DragTarget; _scrollDragNode = s.ScrollDragNode;
        _pressClickCount = s.PressClickCount; _pressMods = s.PressMods; _pressKind = s.PressKind;
        _scrollDragGrab = s.ScrollDragGrab; _contextDown = s.ContextDown; _middleDown = s.MiddleDown;
        _panTarget = s.PanTarget; _panClaimed = s.PanClaimed; _panAnchorPx = s.PanAnchorPx;
        _panAnchorOffset = s.PanAnchorOffset; _panAxisX = s.PanAxisX; _panVel = s.PanVel;
        _touchSuppressTap = s.TouchSuppressTap;
        _pendingTouchPress = s.PendingTouchPress; _pendingTouchPressMs = s.PendingTouchPressMs;
        _reorderTarget = s.ReorderTarget; _touchReorder = s.TouchReorder; _reorderAxisX = s.ReorderAxisX;
        _swipeDrag = s.SwipeDrag; _swipeAxisX = s.SwipeAxisX;
        _gesturePanNode = s.GesturePanNode;
        _pinchViewport = s.PinchViewport; _pinchMember = s.PinchMember;
        _activeArenaSlot = s.ArenaSlot;
        _holdFired = s.HoldFired;
    }

    /// <summary>Store the working scalars back into the active contact's slot, then RECYCLE the slot if the contact is
    /// fully idle (no capture target, no pan) so a finished contact frees its seat for the next id (the steady mouse
    /// idles its slot between gestures, keeping the 10-seat table from filling).</summary>
    private void SlotOut()
    {
        if (!_activeSlotValid) return;
        int slot = FindSlot(_activeSlotId);
        if (slot < 0) return;
        ref PointerSlot s = ref _slots[slot];
        s.Down = _down; s.DragTarget = _dragTarget; s.ScrollDragNode = _scrollDragNode;
        s.PressClickCount = _pressClickCount; s.PressMods = _pressMods; s.PressKind = _pressKind;
        s.ScrollDragGrab = _scrollDragGrab; s.ContextDown = _contextDown; s.MiddleDown = _middleDown;
        s.PanTarget = _panTarget; s.PanClaimed = _panClaimed; s.PanAnchorPx = _panAnchorPx;
        s.PanAnchorOffset = _panAnchorOffset; s.PanAxisX = _panAxisX; s.PanVel = _panVel;
        s.TouchSuppressTap = _touchSuppressTap;
        s.PendingTouchPress = _pendingTouchPress; s.PendingTouchPressMs = _pendingTouchPressMs;
        s.ReorderTarget = _reorderTarget; s.TouchReorder = _touchReorder; s.ReorderAxisX = _reorderAxisX;
        s.SwipeDrag = _swipeDrag; s.SwipeAxisX = _swipeAxisX;
        s.GesturePanNode = _gesturePanNode;
        s.PinchViewport = _pinchViewport; s.PinchMember = _pinchMember;
        s.ArenaSlot = _activeArenaSlot;
        s.HoldFired = _holdFired;
        // A context-ONLY contact (a long-press over a node that is hit-test-transparent to clicks) has no Down/pan/etc. to
        // hold its seat — but its arena's Hold is still counting down. Keep the slot LIVE while that Hold is armed (else it
        // recycles on the down frame and frees the arena before the ~500ms timer); the up/cancel or the Hold's resolution
        // clears the seat normally. Zero-cost once the Hold resolves/rejects (the per-arena scan early-outs).
        bool armedHold = _activeArenaSlot >= 0 && _arena.ArenaHasArmedHold(_activeArenaSlot);
        if (!armedHold && s.Down.IsNull && s.DragTarget.IsNull && s.ScrollDragNode.IsNull && s.ContextDown.IsNull
            && s.MiddleDown.IsNull && s.PanTarget.IsNull && s.ReorderTarget.IsNull && s.SwipeDrag.IsNull
            && s.PinchViewport.IsNull)
        {
            // Idle contact: free its arena seat too (the contact ended — a resolved/unused arena is reclaimed so the
            // cap-10 table never fills) before reclaiming the slot. CloseAndFree is idempotent on an already-free slot.
            if (_activeArenaSlot >= 0) { _arena.CloseAndFree(_activeArenaSlot); _activeArenaSlot = -1; s.ArenaSlot = -1; }
            s.Used = false;   // idle contact: reclaim the seat
        }
    }

    private void ClearWorkingScalars()
    {
        _down = NodeHandle.Null; _dragTarget = NodeHandle.Null; _scrollDragNode = NodeHandle.Null;
        _pressClickCount = 1; _pressMods = KeyModifiers.None; _pressKind = PointerKind.Mouse;
        _scrollDragGrab = 0f; _contextDown = NodeHandle.Null; _middleDown = NodeHandle.Null;
        _panTarget = NodeHandle.Null; _panClaimed = false; _panAnchorPx = default; _panAnchorOffset = 0f;
        _panAxisX = false; _panVel = default;
        _touchSuppressTap = false;
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        _sgTarget = NodeHandle.Null; _sgLatched = false; _sgMomentum = false; _sgTailConverted = false; _sgAccumX = 0f; _sgAccumY = 0f;
        _reorderTarget = NodeHandle.Null; _touchReorder = false; _reorderAxisX = false;
        _swipeDrag = NodeHandle.Null; _swipeAxisX = false;
        _gesturePanNode = NodeHandle.Null;
        _pinchViewport = NodeHandle.Null; _pinchMember = -1;
        _activeArenaSlot = -1;
        _holdFired = false;
    }

    private int FindSlot(uint id)
    {
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].Used && _slots[i].Id == id) return i;
        return -1;
    }

    private int FindOrAllocSlot(uint id)
    {
        int free = -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Used) { if (_slots[i].Id == id) return i; }
            else if (free < 0) free = i;
        }
        if (free < 0) return -1;   // table full of distinct live contacts: ignore this id deterministically
        _slots[free] = default;
        _slots[free].Used = true;
        _slots[free].Id = id;
        _slots[free].ArenaSlot = -1;   // no arena until TouchDown opens one (default(int)==0 is a VALID arena slot, so -1 explicitly)
        _slots[free].PinchMember = -1; // no Pinch member until enrolled (default(int)==0 is a VALID member slot, so -1 explicitly)
        return free;
    }

    /// <summary>Drop any contact's capture target whose node was freed by a reconcile (the per-slot analogue of the
    /// single-pointer dead-node pruning at the top of <see cref="Dispatch"/>).</summary>
    private void PruneDeadSlots()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Used) continue;
            ref PointerSlot s = ref _slots[i];
            if (!s.Down.IsNull && !_scene.IsLive(s.Down)) s.Down = NodeHandle.Null;
            if (!s.DragTarget.IsNull && !_scene.IsLive(s.DragTarget)) s.DragTarget = NodeHandle.Null;
            if (!s.ScrollDragNode.IsNull && !_scene.IsLive(s.ScrollDragNode)) s.ScrollDragNode = NodeHandle.Null;
            if (!s.ContextDown.IsNull && !_scene.IsLive(s.ContextDown)) s.ContextDown = NodeHandle.Null;
            if (!s.MiddleDown.IsNull && !_scene.IsLive(s.MiddleDown)) s.MiddleDown = NodeHandle.Null;
            if (!s.PanTarget.IsNull && !_scene.IsLive(s.PanTarget)) { s.PanTarget = NodeHandle.Null; s.PanClaimed = false; }
            if (!s.ReorderTarget.IsNull && !_scene.IsLive(s.ReorderTarget)) { s.ReorderTarget = NodeHandle.Null; s.TouchReorder = false; }
            if (!s.SwipeDrag.IsNull && !_scene.IsLive(s.SwipeDrag)) s.SwipeDrag = NodeHandle.Null;
            if (!s.GesturePanNode.IsNull && !_scene.IsLive(s.GesturePanNode)) s.GesturePanNode = NodeHandle.Null;
            if (!s.PinchViewport.IsNull && !_scene.IsLive(s.PinchViewport)) { s.PinchViewport = NodeHandle.Null; s.PinchMember = -1; }
            // Keep a context-ONLY contact's slot alive while its Hold is armed (it holds no Down/pan to anchor the seat) —
            // PruneDeadSlots runs every frame (top of Dispatch, even with no events), so without this a hit-test-transparent
            // long-press would lose its arena on the first idle frame, before the ~500ms timer fires (the wake bug).
            bool armedHold = s.ArenaSlot >= 0 && _arena.ArenaHasArmedHold(s.ArenaSlot);
            if (!armedHold && s.Down.IsNull && s.DragTarget.IsNull && s.ScrollDragNode.IsNull && s.ContextDown.IsNull
                && s.MiddleDown.IsNull && s.PanTarget.IsNull && s.ReorderTarget.IsNull && s.SwipeDrag.IsNull
                && s.PinchViewport.IsNull)
            {
                if (s.ArenaSlot >= 0) { _arena.CloseAndFree(s.ArenaSlot); s.ArenaSlot = -1; }   // the contact's targets all died → free its arena
                s.Used = false;
            }
        }
    }

    /// <summary>Clear the down (click) candidate of every contact AND the working scalar — used by the keyboard
    /// Escape-cancels-drag path, which has no PointerId. The item-drag is single-pointer, so this only ever clears the
    /// one captured contact's candidate; idle slots are recycled.</summary>
    private void ClearDownEverywhere()
    {
        _down = NodeHandle.Null;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Used) continue;
            ref PointerSlot s = ref _slots[i];
            s.Down = NodeHandle.Null;
            if (s.DragTarget.IsNull && s.ScrollDragNode.IsNull && s.ContextDown.IsNull
                && s.MiddleDown.IsNull && s.PanTarget.IsNull)
            {
                if (s.ArenaSlot >= 0) { _arena.CloseAndFree(s.ArenaSlot); s.ArenaSlot = -1; }
                s.Used = false;
            }
        }
    }

    // ── single-recognizer touch pan/tap (Phase 1, no arena) ──────────────────────────────────────────
    // One recognizer per contact: a touch-down records a press candidate AND, when it lands over a Scrollable, a pan
    // candidate. Below the pan slop a down→up over the same node is a TAP — the existing click path (focus-on-press +
    // click-on-release). Crossing the slop on the scroll axis CLAIMS the pan: the press candidate is cancelled (WinUI
    // Pressed→Canceled, never Released — PointerInputProcessor.cpp:397/423) and each subsequent move drives the
    // viewport through SetScrollOffset (clamp + virtual re-realize). Touch never latches hover/pressed visuals.

    /// <summary>Touch down: a press landing on the conscious overlay scrollbar's lane/thumb drives the per-PointerId
    /// <c>_scrollDragNode</c> thumb-drag (or a lane page-step) and reveals/expands the bar for the contact — tested
    /// BEFORE the pan-claim, exactly as the mouse PointerDown prioritizes the scrollbar (so a touch on the bar drags the
    /// thumb instead of panning content). Otherwise hit-test the press candidate (the tap target), drive the pressed
    /// visual like a mouse press (<see cref="OnPressChanged"/> → the InteractionAnimator's PressT — WinUI shows Pressed on
    /// touch down; released on up/cancel/pan-claim, returning to Normal, NOT PointerOver, since touch has no cursor), and
    /// if the contact sits under a Scrollable record the pan anchor + seed the velocity sampler (no claim until the slop
    /// is crossed). Fires the same press-edge handlers a click does — pointer focus moves on press,
    /// OnPointerDown/OnPointerPressed fire — but arms no item-drag (single recognizer).</summary>
    private bool TouchDown(in InputEvent e)
    {
        // Scrollbar lane/thumb FIRST (mirror the mouse PointerDown order, lines feeding TryScrollbarPointerDown): the
        // scrollbar owns the contact — no content pan, no pressed visual, no press/click handlers; the per-PointerId
        // _scrollDragNode (and the bar's PointerOverScrollbar reveal) is set by TryScrollbarPointerDown and rides the slot.
        if (TryScrollbarPointerDown(e.PositionPx))
        {
            _down = NodeHandle.Null;
            // A touch lane PAGE-STEP (TryScrollbarPointerDown returned true but grabbed no thumb ⇒ _scrollDragNode null)
            // can't hold the lane reveal a resting mouse cursor does — drop the PointerOver/PointerOverScrollbar it set so
            // the bar fades on the idle timer instead of latching forever (no touch move ever clears it). A thumb grab
            // keeps the reveal via _scrollDragNode and releases it in TouchUp.
            if (_scrollDragNode.IsNull)
            {
                var lane = ScrollableUnder(e.PositionPx);
                if (!lane.IsNull) OnScrollLeave?.Invoke(lane);
            }
            return true;
        }

        bool handled = false;
        _down = HitTest(e.PositionPx);
        _panAnchorPx = e.PositionPx;   // shared radial tap/hold anchor, even when there is no scroll candidate
        NodeHandle stoppingScrollable = ScrollableUnder(e.PositionPx);
        if (!stoppingScrollable.IsNull && _scene.HasScroll(stoppingScrollable))
        {
            ref ScrollState stopping = ref _scene.ScrollRef(stoppingScrollable);
            _touchSuppressTap = stopping.Phase == ScrollIntegrator.Fling
                                || stopping.Phase == ScrollIntegrator.WheelAnimating;
            if (_touchSuppressTap) _down = NodeHandle.Null;
        }
        // Click-count synthesis is pointer-kind-AGNOSTIC (the shared _lastDown* tracker): a touch double-tap inside the
        // slop window + DoubleClickMs promotes _clickCount to 2 (then 3), so an editor/selectable text leaf word-selects
        // on a double-tap exactly like a mouse double-click (the press args below carry the live count). Tracked here on
        // the touch down edge, mirroring the mouse PointerDown — without it every tap delivered ClickCount=1.
        if (!_touchSuppressTap) TrackClickCount(in e);
        if (!_touchSuppressTap) { _pressClickCount = _clickCount; _pressMods = e.Mods; _pressKind = e.Pointer; }
        // A press anywhere outside the selection's node dismisses a live (mouse/pen) selection, same as a mouse press.
        if (!_touchSuppressTap && !_selText.IsNull && _selText != _down) ClearTextSelection();
        // Pressed visual on touch down, exactly like a mouse press (WinUI: touch shows the Pressed state). Released on
        // PointerUp / PointerCancel / pan-claim — the contact owns the shared _pressed singleton while _pressed == _down.
        _pendingTouchPress = !_touchSuppressTap && !_down.IsNull && !stoppingScrollable.IsNull;
        _pendingTouchPressMs = 0f;
        if (!_pendingTouchPress) SetState(ref _pressed, _down, NodeFlags.Pressed);

        // OnDrag implicit-capture (WinUI CapturePointer in OnPointerPressed) ON the touch path, identical to the mouse
        // PointerDown: a press landing on an OnDrag node (Slider track scrub, EditableText drag-select) makes THAT node
        // the capture target the move drives — and OWNS the contact, so the content-pan candidate below is skipped. This
        // is the single-recognizer arbitration the spec calls for: the editor's/slider's drag wins on a touch that
        // starts on it; a touch that starts on non-drag chrome inside the same scroller still becomes a pan candidate.
        // (No item-drag arm: that is the §7A arena's Phase-3 work — single recognizer here.)
        // A DragYieldsToPan OnDrag node (SwipeControl/FlipView) is the EXCEPTION: it must NOT eager-capture, so the Pan
        // candidate below is still set and the two compete axis-locked in the arena (§7A). Record it as the swipe candidate
        // + its own axis (the node's main Direction); the arena's Drag-vs-Pan vote captures it into _dragTarget on a win.
        if (!_touchSuppressTap && !_down.IsNull && _scene.GetDrag(_down) is not null)
        {
            if ((_scene.Flags(_down) & NodeFlags.DragYieldsToPan) != 0)
            {
                _swipeDrag = _down;
                _swipeAxisX = _scene.Layout(_down).Direction == 0;   // 0 = row box ⇒ horizontal swipe
                _panAnchorPx = e.PositionPx;
                _panVel.Reset(e.PositionPx, e.TimestampMs, e.QpcTicks);   // seed velocity for the snap (works without a scroller too)
                handled = true;
            }
            else
            {
                _dragTarget = _down;   // Slider/EditableText: eager capture keeps its immediate pressed feedback
                if (_pendingTouchPress)
                {
                    _pendingTouchPress = false;
                    SetState(ref _pressed, _down, NodeFlags.Pressed);
                }
            }
        }
        else if (!_touchSuppressTap)
        {
            // §7A.1 ROUTE-WALK enrollment: the hit node has NO drag handler of its own — an INTERACTIVE row
            // (OnClick/OnPointerPressed) nested INSIDE a SwipeControl/FlipView content-pan wrapper. GetDrag/OnDrag do
            // not bubble, so without this walk the wrapper's axis-locked Drag member never arms and the swipe is dead
            // (the hit-node check above only catches a wrapper pressed DIRECTLY, e.g. the gallery). Walk ancestors from
            // the hit node (or the deepest visual, when nothing interactive was hit) for the nearest enabled
            // DragYieldsToPan drag node and record it EXACTLY as the hit-node branch does — everything downstream
            // (EnrollTouchArena hasSwipe, the StepTouchArena axis vote, ClaimTouchSwipe) is unchanged. Touch-path only.
            NodeHandle swipeAncestor = NearestSwipeDrag(_down.IsNull ? HitTestAny(e.PositionPx) : _down);
            if (!swipeAncestor.IsNull)
            {
                _swipeDrag = swipeAncestor;
                _swipeAxisX = _scene.Layout(swipeAncestor).Direction == 0;   // 0 = row box ⇒ horizontal swipe
                _panAnchorPx = e.PositionPx;
                _panVel.Reset(e.PositionPx, e.TimestampMs, e.QpcTicks);
                // ANCHOR delivery: GetPointerDown fires only on the hit node (:1520, no bubbling), so a walk-found
                // wrapper (_swipeDrag != _down) never received its PanDown. Deliver it here with the wrapper's OWN
                // model-local point so its pan anchor (panStart/panBase) is measured in the SAME node space as the
                // OnDrag samples ClaimTouchSwipe feeds it — the second-swipe-on-an-open-row delta stays correct. The
                // hit node's own PointerDown still fires below (:1520); no double-fire (walk ⇒ _swipeDrag != _down).
                _scene.GetPointerDown(_swipeDrag)?.Invoke(LocalPos(_swipeDrag, e.PositionPx));
                handled = true;
            }
        }

        NodeHandle scrollable = NodeHandle.Null;
        if (_dragTarget.IsNull)
        {
            scrollable = stoppingScrollable;
            if (!scrollable.IsNull)
            {
                ref ScrollState sc = ref _scene.ScrollRef(scrollable);
                // A finger touching an in-flight viewport takes authoritative control immediately (scroll-feel-rework-v2
                // §2.2: any PointerDown zeroes the viewport's motion first — the same CancelFling every mouse/pen press
                // and scrollbar grab uses). Cancel the coast/chase before capturing the pan anchor, otherwise phase 7 can
                // move the content underneath the stationary finger between down and the first move. A live overscroll
                // band is handed to the phase-7 SnapBack spring (not erased); the pan re-grabs it via ApplyTouchPan.
                OnCancelFling?.Invoke(scrollable);
                _panTarget = scrollable;
                _panClaimed = false;
                _chainOuter = NodeHandle.Null;   // fresh pan → no nested-scroll hand-off yet
                _panAxisX = sc.Orientation == 1;
                _panAnchorOffset = _panAxisX ? sc.OffsetX : sc.OffsetY;
                _panAnchorPx = e.PositionPx;
                if (_swipeDrag.IsNull) _panVel.Reset(e.PositionPx, e.TimestampMs, e.QpcTicks);   // (the swipe path already seeded it above)
                handled = true;   // a pan candidate owns this contact's potential scroll even before the claim
            }
        }

        // Pinch-zoom candidacy (Phase-4): record the nearest Zoomable viewport under this contact so a SECOND contact
        // landing over the SAME viewport can open a pinch (the §7B Pinch member EagerAccept-wins, sweeping this contact's
        // Pan/Tap). A zoomable viewport is also Scrollable, so the pan candidate above still arms — until the second
        // contact's pinch sweeps it. Skipped when an eager OnDrag (Slider/EditableText) already captured the contact.
        _pinchViewport = _dragTarget.IsNull ? ZoomableUnder(e.PositionPx) : NodeHandle.Null;

        if (!_down.IsNull)
        {
            // Pointer focus moves on the press edge (WinUI Focus(FocusState_Pointer)), exactly like a mouse press —
            // a NoPointerFocusBit node blocks the move, IsTabStop=False parts fall through to the focusable root.
            var focusTarget = NearestFocusable(_down);
            if (!focusTarget.IsNull &&
                (_scene.Interaction(focusTarget).HandlerMask & InteractionInfo.NoPointerFocusBit) == 0)
                SetFocus(focusTarget, visual: false);
            // DIVERGENCE (input-a11y §8): inert-background press clears focus (mouse + touch/pen parity). Full rationale at
            // the mouse PointerDown site. TOUCH-ONLY refinement: a press that armed a content-pan candidate (scrollable !=
            // null, :1102) is a SCROLL-gesture start, not a background tap — it KEEPS focus (the touch analogue of a
            // scrollbar drag, requirement 2f). On mouse there is no content-pan, so an empty-content click stays a click-away.
            else if (focusTarget.IsNull && scrollable.IsNull && !_focused.IsNull && _scene.IsLive(_focused) &&
                     (_scene.Interaction(_down).HandlerMask & InteractionInfo.NoPointerFocusBit) == 0)
                SetFocus(NodeHandle.Null);

            var local = LocalPos(_down, e.PositionPx);
            _scene.GetPointerDown(_down)?.Invoke(local);                 // press-to-set
            if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.PressedBit) != 0)
                _scene.GetPointerPressed(_down)?.Invoke(new PointerEventArgs
                {
                    Local = local, ClickCount = _clickCount, Mods = e.Mods, Button = 0, Kind = e.Pointer,
                });
            if ((_scene.Interaction(_down).HandlerMask & (InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.ClickBit | InteractionInfo.DragBit)) != 0) handled = true;
        }
        else if (!_touchSuppressTap && scrollable.IsNull && !_focused.IsNull && _scene.IsLive(_focused))
        {
            // Touch on inert page chrome is the same click-away blur as mouse. A pan candidate deliberately preserves
            // focus, and an inertia-stop contact is swallowed without changing it.
            SetFocus(NodeHandle.Null);
        }

        // Open this contact's gesture arena (§7A.1) and enroll members innermost-first along the hit route, MIRRORING the
        // scalar facts just computed (the OnDrag/_dragTarget node → Drag, the Scrollable → Pan, the clickable _down → Tap,
        // the CanDrag chain → DragReorder, a hold/context chain → Hold). The arena owns WHICH recognizer wins; the proven
        // scalar machinery above/below still EXECUTES it (so the common case is observably identical — §7A.5 fast-path). A
        // route with no gesture-advertising node opens no arena (nothing to arbitrate) and the contact keeps pre-arena flow.
        EnrollTouchArena(in e, scrollable);
        // Phase-4: if this is the SECOND contact over a Zoomable viewport a still-down contact is already over, open the
        // pinch — feed the first contact's Pinch FSM OnSecondContact (EagerAccept), sweep both contacts' Pan/Tap, and start
        // tracking the magnification. The press visuals/clicks are cancelled the capture-loss way (Pressed→Canceled).
        if (TryOpenPinch(in e)) handled = true;
        return handled;
    }

    /// <summary>Detect a second touch contact landing over the SAME Zoomable viewport a still-down contact is over and open
    /// the pinch session (§7B Pinch → EagerAccept). Returns true when a pinch was opened. Scans the fixed slab (≤10) for the
    /// partner; zero alloc. The arena sweep + the explicit scalar cancel of BOTH contacts' pans run here so neither finger
    /// drives a content scroll while pinching (the Pan members were swept; the scalar pan state is the executing path).</summary>
    private bool TryOpenPinch(in InputEvent e)
    {
        if (_pinchViewport.IsNull) return false;                 // this contact isn't over a zoomable viewport
        if (!_pinchSessionViewport.IsNull) return false;         // a pinch is already active (only two fingers drive it)
        // Find a DIFFERENT live contact whose own down landed over the same viewport (its slot carries PinchViewport).
        int partner = -1;
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].Used && _slots[i].Id != e.PointerId && _slots[i].PinchViewport == _pinchViewport && _slots[i].PinchMember >= 0)
            { partner = i; break; }
        if (partner < 0) return false;

        ref PointerSlot first = ref _slots[partner];
        // Feed the FIRST contact's Pinch FSM the second contact (its EagerAccept vote), then resolve the first contact's
        // arena so the Pinch member wins and sweeps that contact's Pan/Tap (the existing synthetic-GestureRejected routing).
        int pm = first.PinchMember;
        if ((uint)pm < (uint)_fsms.Length)
        {
            ArenaVote v = _fsms[pm].OnSecondContact(e.PositionPx, ToUs(e.TimestampMs));
            _arena.SetVote(pm, v);
            if (first.ArenaSlot >= 0 && _arena.IsArenaOpen(first.ArenaSlot)) _arena.ResolveStep(first.ArenaSlot);
        }
        // Cancel the FIRST contact's scalar pan (it lives in the partner slot — not the working set): a swept Pan must not
        // keep driving the content scroll, and its eventual up must fire no fling. The press was already released on its
        // own down→claim path or stays as a tap candidate; null Down so its up taps nothing mid-pinch.
        first.PanTarget = NodeHandle.Null; first.PanClaimed = false; first.Down = NodeHandle.Null;
        // Cancel THIS (second) contact's pan candidate too (working scalars): the pinch owns it.
        if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
        _panTarget = NodeHandle.Null; _panClaimed = false; _down = NodeHandle.Null;

        BeginPinchSession(_pinchViewport, first.Id, e.PointerId, first.PanAnchorPx, e.PositionPx);
        return true;
    }

    /// <summary>Open the pinch session (Phase-4): capture the start separation/zoom and the content-origin axis anchor used
    /// to keep the gesture midpoint's content point fixed as the scale changes. <paramref name="posA"/>/<paramref name="posB"/>
    /// are the two contacts' window positions at the open. Zero alloc — scalar field writes.</summary>
    private void BeginPinchSession(NodeHandle viewport, uint idA, uint idB, Point2 posA, Point2 posB)
    {
        if (viewport.IsNull || !_scene.IsLive(viewport) || !_scene.HasScroll(viewport)) return;
        ref ScrollState sc = ref _scene.ScrollRef(viewport);
        _pinchSessionViewport = viewport;
        _pinchIdA = idA; _pinchIdB = idB;
        _pinchPosA = posA; _pinchPosB = posB;
        _pinchStartDist = Dist(posA, posB);
        _pinchStartZoom = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        // The content node's local origin in window space on the scroll axis, INDEPENDENT of the current scale/offset
        // (AbsoluteRect includes the LocalTransform translation, so subtract it back out). midLocal = midpointAxis − this.
        bool horizontal = sc.Orientation == 1;
        _pinchAppliedZoom = _pinchStartZoom;                         // seed the focal chain base (offset + zoom, consistent)
        _pinchAppliedOff = horizontal ? sc.OffsetX : sc.OffsetY;
        _pinchOriginAxis = 0f;
        var content = sc.ContentNode;
        if (!content.IsNull && _scene.IsLive(content))
        {
            RectF cabs = _scene.AbsoluteRect(content);
            ref NodePaint cp = ref _scene.Paint(content);
            _pinchOriginAxis = horizontal ? cabs.X - cp.LocalTransform.Dx : cabs.Y - cp.LocalTransform.Dy;
        }
        OnScrollTrackEnd?.Invoke(viewport);   // §2.1: a swept pan's resampler latch must not replay under the pinch's intents
        UpdatePinch();   // record the initial (start-zoom) focal offset intent so the tick commits the current factor
    }

    /// <summary>Recompute the magnification from the two contacts' current separation and apply it about the gesture
    /// midpoint (Phase-4): <c>scale = clamp(startZoom · curDist/startDist, MinZoom, MaxZoom)</c>; the offset is solved so the
    /// content point under the midpoint stays put on the scroll axis (the WinUI focal-point pinch), then written through
    /// <see cref="SetScrollOffset"/> (clamp + the zoom-aware content transform + virtual re-realize — TransformDirty only,
    /// never LayoutDirty). Called on every contributing move and at session open. Zero alloc.</summary>
    private void UpdatePinch()
    {
        if (_pinchSessionViewport.IsNull || !_scene.IsLive(_pinchSessionViewport) || !_scene.HasScroll(_pinchSessionViewport)) return;
        if (_pinchStartDist < 1f) return;   // degenerate start span (both fingers coincident) — no scale until they spread
        ref ScrollState sc = ref _scene.ScrollRef(_pinchSessionViewport);
        bool horizontal = sc.Orientation == 1;
        float curDist = Dist(_pinchPosA, _pinchPosB);
        float minZ = sc.MinZoom > 0f ? sc.MinZoom : 0.1f;
        float maxZ = sc.MaxZoom > 0f ? sc.MaxZoom : 10f;
        float z = Math.Clamp(_pinchStartZoom * (curDist / _pinchStartDist), minZ, maxZ);

        float midAxis = horizontal ? (_pinchPosA.X + _pinchPosB.X) * 0.5f : (_pinchPosA.Y + _pinchPosB.Y) * 0.5f;
        float midLocal = midAxis - _pinchOriginAxis;            // the midpoint in the viewport's content coordinate frame
        // Chain the focal math from the dispatcher-tracked (offset, zoom) base — NOT sc.Offset, which lags the just-committed
        // ZoomFactor (the offset intent applies at phase 7). Using a consistent pair keeps the content point under the
        // midpoint fixed even when BOTH fingers move in one coalesced frame (two UpdatePinch calls chaining correctly).
        float oldOff = _pinchAppliedOff;
        float oldZ = _pinchAppliedZoom > 0f ? _pinchAppliedZoom : 1f;
        // Keep the content point currently under the midpoint fixed: c = (midLocal + oldOff)/oldZ; newOff = z·c − midLocal.
        float c = (midLocal + oldOff) / oldZ;
        float newOff = z * c - midLocal;
        float maxOff = horizontal ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW) : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        newOff = Math.Clamp(newOff, 0f, maxOff);   // clamp to the SAME extent the tick will clamp to, so the base stays exact

        sc.ZoomFactor = z;                  // commit the factor BEFORE the offset intent so the integrator clamps on z·content
        _pinchAppliedZoom = z; _pinchAppliedOff = newOff;   // advance the focal base (matches what the tick will apply)
        // §2.1/§6 single-writer: RECORD the focal offset as an exact intent (PendingTarget + PhaseImmediate) and arm the
        // node; the phase-7 integrator writes it verbatim (clamp + zoom-aware content transform + virtual re-realize, no
        // relayout) the same frame. ZoomFactor is not offset/band, so it commits here directly. The dispatcher never moves
        // the offset synchronously.
        if (horizontal) sc.PendingTargetX = newOff; else sc.PendingTargetY = newOff;
        sc.Phase = ScrollIntegrator.WheelAnimating;
        sc.PhaseFlags = ScrollState.PhaseImmediate;
        sc.FlingVelocity = 0f; sc.FlingRetargeted = false; sc.FlingSnapTarget = float.NaN;
        sc.IdleMs = 0f;
        OnScrollArmed?.Invoke(_pinchSessionViewport);
    }

    /// <summary>End the pinch session (Phase-4): the FIRST contact lifted/cancelled. The committed <see cref="ScrollState.ZoomFactor"/>
    /// stays (a finished pinch keeps its magnification); the surviving finger re-arms as a pan naturally in
    /// <see cref="TouchMove"/> (its slot's PanTarget was nulled at pinch open, but a fresh down/move on it re-anchors — see
    /// the openIssues note). Clears the session scalars only.</summary>
    private void EndPinchSession()
    {
        _pinchSessionViewport = NodeHandle.Null;
        _pinchIdA = 0; _pinchIdB = 0;
        _pinchStartDist = 0f; _pinchStartZoom = 1f; _pinchOriginAxis = 0f;
        _pinchPosA = default; _pinchPosB = default;
    }

    private static float Dist(Point2 a, Point2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Pinch→pan continuation (Phase-4; WinUI continues the manipulation with the remaining finger): re-anchor the
    /// SURVIVING contact's slot (it is not the active event's contact — write its slot directly) as an ALREADY-CLAIMED pan
    /// on <paramref name="viewport"/> from <paramref name="survivorPos"/> + the committed offset, so its next move scrolls
    /// the zoomed content immediately (no slop re-cross). Resets that contact's velocity sampler so its eventual flick reads
    /// a fresh post-pinch speed. No-op if the survivor already lifted (its slot was reclaimed). Zero alloc.</summary>
    private void ContinuePinchAsPan(NodeHandle viewport, uint survivorId, Point2 survivorPos, uint timestampMs, long qpcTicks = 0)
    {
        if (viewport.IsNull || !_scene.IsLive(viewport) || !_scene.HasScroll(viewport)) return;
        int si = FindSlot(survivorId);
        if (si < 0) return;   // the surviving finger already lifted in the same drain → nothing to continue
        ref PointerSlot s = ref _slots[si];
        ref ScrollState sc = ref _scene.ScrollRef(viewport);
        bool horizontal = sc.Orientation == 1;
        s.PanTarget = viewport;
        s.PanClaimed = true;                       // already past slop (the pinch owned it) → its next move pans straight away
        s.PanAxisX = horizontal;
        s.PanAnchorPx = survivorPos;
        s.PanAnchorOffset = horizontal ? sc.OffsetX : sc.OffsetY;
        s.PanVel.Reset(survivorPos, timestampMs, qpcTicks);
        s.PinchViewport = NodeHandle.Null;         // the pinch is over for the survivor too (it's a pan now)
        s.PinchMember = -1;
        // §2.1: start the survivor as a per-node touch-pan intent (Phase = TouchpadTracking, PendingRawOffset = committed
        // offset) so its next move records PendingRawOffset the tick applies — a pure recorder, never a synchronous writer.
        sc.FlingVelocity = 0f; sc.FlingRetargeted = false; sc.FlingSnapTarget = float.NaN;
        sc.PendingTargetX = float.NaN; sc.PendingTargetY = float.NaN;
        sc.Overscrolling = false; sc.OverscrollVel = 0f;
        sc.PendingRawOffset = s.PanAnchorOffset;
        sc.PhaseFlags = ScrollState.PhaseTouchPan;
        sc.Phase = ScrollIntegrator.TouchpadTracking;
        OnScrollArmed?.Invoke(viewport);
    }

    /// <summary>Update this contact's tracked position in the active pinch session (its move feeds the magnification). True
    /// when the contact is one of the two pinching fingers (so its move is FULLY consumed by the pinch — no content pan).</summary>
    private bool PinchMoveContact(in InputEvent e)
    {
        if (_pinchSessionViewport.IsNull) return false;
        if (e.PointerId == _pinchIdA) _pinchPosA = e.PositionPx;
        else if (e.PointerId == _pinchIdB) _pinchPosB = e.PositionPx;
        else return false;
        UpdatePinch();
        return true;
    }

    /// <summary>Touch move: drive a captured OnDrag gesture (slider scrub / editor drag-select), a claimed scrollbar
    /// thumb-drag, a claimed content pan, or test the pan candidate against the slop and claim it on the first axis
    /// crossing. The velocity sampler runs on every move so the release has a flick speed to hand off. Hover is transient
    /// (never latched): a pre-claim move tracks the node under the finger, a drag/scrollbar-drag/pan-claim suppresses it,
    /// and the up/cancel clears it — so a touch gesture never leaves a stuck hover behind (the WinUI no-resting-touch-hover).</summary>
    private bool TouchMove(in InputEvent e)
    {
        // Phase-4 pinch: a move by either of the two pinching contacts updates that finger's position and re-applies the
        // magnification about the gesture midpoint (no content pan, no hover). Tested FIRST — a pinching finger drives only
        // the zoom until it lifts.
        if (PinchMoveContact(in e)) return true;

        // Tap and Hold use total finger travel, not the enclosing scroller's projected axis. A diagonal/cross-axis
        // movement can therefore cancel item activation and long-press without incorrectly claiming the scroller.
        if (!_down.IsNull && Dist(e.PositionPx, _panAnchorPx) > TouchSlopPx)
            CancelTouchTapCandidate();

        // A captured OnDrag node (the press landed on a Slider track / EditableText — _dragTarget set in TouchDown, the
        // mouse PointerMove's _dragTarget drive) owns the contact: each move scrubs/extends-selects it, exactly like the
        // mouse, with NO content pan and NO touch hover. The pan candidate is never armed alongside it (TouchDown gates
        // the pan on _dragTarget.IsNull), so this branch is the single recognizer the spec wants: the editor's/slider's
        // drag wins on a touch that started on it.
        if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget))
        {
            // A claimed cross-axis swipe (DragYieldsToPan, captured into _dragTarget by ClaimTouchSwipe) keeps sampling
            // velocity so its release has a real flick speed to snap on (SwipeControl 31px/s close / FlipView flick). The
            // eager Slider/EditableText drag samples nothing extra (the sampler is unused on that path).
            if (_dragTarget == _swipeDrag) { FeedPanVelocity(e.PointerId); _panVel.Sample(e.PositionPx, e.TimestampMs, e.QpcTicks); }
            _scene.GetDrag(_dragTarget)?.Invoke(PointToLocal(_dragTarget, e.PositionPx));   // UNCLAMPED — see the mouse PointerMove path
            return true;
        }

        // A scrollbar-drag contact (touch grabbed the thumb on down) drives DragScrollbar via its per-PointerId
        // _scrollDragNode — track-fraction → SetScrollOffset, exactly like the mouse drag — and never sets a touch hover.
        if (!_scrollDragNode.IsNull) { DragScrollbar(e.PositionPx); return true; }

        // A claimed touch drag-reorder (the arena resolved DragReorder for this contact, §7A) owns the move: drive
        // DragController arena-governed (its YieldsToPan bypassed). The reorder visual / live-reorder runs exactly as the
        // mouse path; no content pan, no touch hover. The list does NOT scroll (Pan was swept).
        if (_touchReorder)
        {
            if (_scene.IsLive(_reorderTarget) && Drag.IsActive) Drag.Move(e.PositionPx, e.Mods, e.TimestampMs, arenaGoverned: true);
            return true;
        }

        if (_panClaimed)   // a driven pan suppresses hover visuals — finger-down scrolling never hovers
        {
            FeedPanVelocity(e.PointerId);
            _panVel.Sample(e.PositionPx, e.TimestampMs, e.QpcTicks);
            RecordTouchPanSample(in e);   // §2.1: RECORD the contact sample; phase 7 resamples + writes offset+band ONCE
            return true;
        }

        // Test the movement candidates against the slop FIRST so the claim move never sets a transient hover it must
        // immediately clear (no enter+exit churn on the claiming move). The CLAIM is routed through the arena: the Pan
        // member (scroll-axis-locked) and the DragReorder member (item-axis-locked) both vote on this move (§7B), then
        // ResolveStep decides (§7A.2) — whichever crosses its OWN axis slop first eager-wins. The Pan claim fires on the
        // SAME PanSlopPx on the SAME axis as the old raw `axisTravel >= PanSlopPx` test (observably identical, §7A.5); a
        // DragReorder win instead drives the reorder (StepTouchArena → ClaimTouchReorder); a cross-axis swipe win drives
        // OnDrag (StepTouchArena → ClaimTouchSwipe, capturing it into _dragTarget). A contact has a movement candidate when
        // it set a pan target OR a reorder target OR a swipe-drag target OR a UseGesture(Pan) member.
        if (!_panTarget.IsNull || !_reorderTarget.IsNull || !_swipeDrag.IsNull || !_gesturePanNode.IsNull || _activeArenaSlot >= 0)
        {
            FeedPanVelocity(e.PointerId);
            _panVel.Sample(e.PositionPx, e.TimestampMs, e.QpcTicks);
            if (_activeArenaSlot >= 0)
            {
                if (StepTouchArena(in e)) return true;   // arena claimed pan OR reorder (and already drove it / pinned the pan)
                if (_panClaimed)   // a just-claimed pan: record the first content sample (StepTouchArena gates the claim, not the drive)
                {
                    RecordTouchPanSample(in e);
                    return true;
                }
            }
            else if (!_panTarget.IsNull)
            {
                // No arena for this contact (the cap-10 arena table was full at enrollment): fall back to the pre-arena
                // raw axis-slop pan claim so the pan still works deterministically (the capture slab let this contact in,
                // so it must remain usable). DragReorder is arena-only on touch (it never existed pre-arena), so a full
                // table simply skips the reorder — a bounded-overflow narrowing, not a regression to a prior behavior.
                float axisTravel = _panAxisX ? MathF.Abs(e.PositionPx.X - _panAnchorPx.X) : MathF.Abs(e.PositionPx.Y - _panAnchorPx.Y);
                if (axisTravel >= PanSlopPx && !_panClaimed) ClaimTouchPan();
                if (_panClaimed)
                {
                    RecordTouchPanSample(in e);
                    return true;
                }
            }
        }

        SetState(ref _hovered, HitTest(e.PositionPx), NodeFlags.Hovered);   // transient touch hover (cleared on up/cancel)
        return false;
    }

    /// <summary>Cancel only the provisional item tap/press. The pan candidate remains armed so later scroll-axis
    /// travel can still claim and drive the viewport. This is the touch equivalent of capture-lost cancellation.</summary>
    private void CancelTouchTapCandidate()
    {
        if (_down.IsNull) return;
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        if (_scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
            OnRepeatReleased?.Invoke(_down);
        _down = NodeHandle.Null;
        _selDragging = false;
    }

    /// <summary>Claim the pan for this contact: cancel the press candidate the same way capture-loss does (so a node
    /// that saw the down sees one consistent cancel, never a click), suppress its pressed visual, and end any in-flight
    /// (mouse/pen) text-selection drag. After this the contact is a pure scroll driver until its up/cancel.</summary>
    private void ClaimTouchPan()
    {
        OnScrollStartedObserved?.Invoke();
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        _panClaimed = true;
        SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);   // suppress hover visuals while panning (fires the
                                                                      // hover-leave/PointerExit on the hovered node, once)
        if (!_down.IsNull)
        {
            // Cancel the press candidate the same way capture-loss does (CancelWorkingContact): the pressed visual is
            // released (the contact owns the _pressed singleton while _pressed == _down — clear it through SetState so the
            // NodeFlags.Pressed flag drops AND OnPressChanged(...,false) eases PressT back) and a repeat is stopped, so the
            // node sees a cancel — never a Released/click (the WinUI Pressed→Canceled contract). A captured OnDrag node
            // would also get OnPointerExit, but touch arms none in Phase 1, and the hover-clear above already delivered
            // the exit to the node the finger was over.
            if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            if (_scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                OnRepeatReleased?.Invoke(_down);
            _down = NodeHandle.Null;   // the eventual touch-up must NOT fire a click
        }
        _selDragging = false;
        BeginTouchPanTracking();   // §2.1: latch the integrator resampler — the pan is now a pure intent recorder
    }

    /// <summary>Begin a freshly-claimed direct-touch (WM_POINTER) pan as a PER-NODE intent (scroll-feel-rework-v2 §2.1/§6):
    /// the dispatcher becomes a pure RECORDER — it never moves the offset synchronously. Unlike the single-gesture PTP path
    /// (the singleton resampler), each touch contact drives its OWN scroller's <see cref="ScrollState.PendingRawOffset"/>, so
    /// two fingers panning two sibling scrollers stay independent. Zeros any in-flight momentum (contact zeroes momentum,
    /// §2.2) and, on a re-grab over a still-easing band, folds the live band back into the anchor as excess so
    /// <c>rawOffset = anchor − panDelta</c> reproduces the stretch seamlessly. Sets <see cref="ScrollIntegrator.TouchpadTracking"/>
    /// + <see cref="ScrollState.PhaseTouchPan"/> and arms the node; phase 7 splits PendingRawOffset into offset + band ONCE.</summary>
    private void BeginTouchPanTracking()
    {
        if (_panTarget.IsNull || !_scene.IsLive(_panTarget) || !_scene.HasScroll(_panTarget)) return;
        ref ScrollState sc = ref _scene.ScrollRef(_panTarget);
        sc.FlingVelocity = 0f; sc.FlingRetargeted = false; sc.FlingSnapTarget = float.NaN;
        sc.PendingTargetX = float.NaN; sc.PendingTargetY = float.NaN;
        // The raw anchor is the clamped offset at press (NOT folded with a live band — that seamless-regrab fold is the
        // single-gesture PTP path's, AccumulateContactDelta; ExcessFromBand blows up near the asymptote and, re-applied per
        // direct-touch gesture, runs the anchor away). A live SnapBack band (handed off by the down's CancelFling) simply
        // springs to 0 as the finger re-drags from the clamped offset — matching the pre-rework direct-touch behavior.
        sc.PendingRawOffset = _panAnchorOffset;     // panDelta = 0 at claim; the claiming move's sample sets it below
        sc.PhaseFlags = ScrollState.PhaseTouchPan;
        sc.Phase = ScrollIntegrator.TouchpadTracking;
        OnScrollArmed?.Invoke(_panTarget);
    }

    /// <summary>Record one direct-touch pan sample as the per-node intent (scroll-feel-rework-v2 §2.1/§4.1): the UNCLAMPED
    /// desired offset <c>anchor − panDelta</c> on the scroll axis (finger +axis ⇒ offset decreases). NEVER moves the offset —
    /// phase 7 reads <see cref="ScrollState.PendingRawOffset"/> and writes offset+band once. Keeps the node armed.</summary>
    private void RecordTouchPanSample(in InputEvent e)
    {
        if (_panTarget.IsNull || !_scene.IsLive(_panTarget) || !_scene.HasScroll(_panTarget)) return;
        float panDelta = _panAxisX ? (e.PositionPx.X - _panAnchorPx.X) : (e.PositionPx.Y - _panAnchorPx.Y);
        ref ScrollState sc = ref _scene.ScrollRef(_panTarget);
        sc.PendingRawOffset = _panAnchorOffset - panDelta;
        OnScrollArmed?.Invoke(_panTarget);
    }

    /// <summary>Complete an UN-CLAIMED pan candidate on touch-up when the RELEASE traveled past the scroll-axis slop —
    /// the under-sampled-flick path. A quick flick can land every mid-gesture sample the OS delivered within slop, so no
    /// <see cref="TouchMove"/> ever crossed the slop and <see cref="ClaimTouchPan"/> never ran, yet the finger lifted far
    /// from the press anchor: that is a scroll the OS under-sampled, NOT a tap. Apply the residual offset (where the
    /// claimed drag would have landed the content) and hand off the fling exactly like the claimed-pan release branch
    /// (spring back if the finger was holding the band past the clamp, else seed the friction fling when the flick speed
    /// clears <see cref="ScrollTuning.FlingSeedGate"/>). Returns true when it acted (the caller then SUPPRESSES the spurious
    /// tap-click); false when the contact stayed within the scroll-axis slop (a genuine tap) or has no live scroll target.
    /// Tested per the SCROLL axis (the same axis + threshold the live claim uses), so a tap that wobbles only on the
    /// cross axis still taps — and this adds no tap-cancellation beyond the 4px the move-driven claim already enforces.</summary>
    private bool CompleteUnderSampledPan(in InputEvent e)
    {
        if (_panTarget.IsNull || !_scene.IsLive(_panTarget) || !_scene.HasScroll(_panTarget)) return false;
        float axisTravel = _panAxisX ? MathF.Abs(e.PositionPx.X - _panAnchorPx.X) : MathF.Abs(e.PositionPx.Y - _panAnchorPx.Y);
        if (axisTravel < PanSlopPx) return false;   // within the scroll-axis slop → a genuine tap, not an under-sampled flick

        // KEEPS the up-Sample (unlike the well-sampled release sites that use ComputeReleaseVelocity): this is the UNDER-sampled
        // path — the OS delivered only a few in-slop moves, so the LIFT position is the one sample carrying the real flick
        // displacement, not a near-zero repeat. Folding it in is load-bearing here (re-windowing without it would read ~0 and drop the flick).
        FeedPanVelocity(e.PointerId);
        _panVel.Sample(e.PositionPx, e.TimestampMs, e.QpcTicks);
        float delta = _panAxisX ? (e.PositionPx.X - _panAnchorPx.X) : (e.PositionPx.Y - _panAnchorPx.Y);
        // §2.1: a one-shot exact landing through the integrator write seam (the sole path to the offset chokepoint). The
        // under-sampled path never claimed, so no resampler samples were deposited during the drag — land the residual
        // where the drag would have ended, hard-clamped (no rubber-band on this rare OS-under-sampled edge), then coast.
        WriteScrollOffset(_panTarget, _panAnchorOffset - delta);

        ref ScrollState psc = ref _scene.ScrollRef(_panTarget);
        float v = _panAxisX ? _panVel.Vx : _panVel.Vy;
        if (psc.OverscrollPx != 0f)                            // released holding the band past the clamp → spring back, no fling
        {
            if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"UP  under-sampled overpan-release band={psc.OverscrollPx:0.0} -> spring back");
            psc.Overscrolling = false; psc.OverscrollVel = 0f;
            OnScrollArmed?.Invoke(_panTarget);
        }
        else if (MathF.Abs(v) >= ScrollTuning.FlingSeedGate)   // a fast flick the moves under-sampled → seed the coast (|v| ≥ FlingSeedGate)
        {
            if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"UP  under-sampled flick complete v={-v:0} travel={axisTravel:0}");
            OnFlingStarted?.Invoke(ChainFlingTarget(), -v);
        }
        return true;
    }

    // ── gesture-arena wiring for the touch path (§7A.1/§7A.4/§7A.5) ───────────────────────────────────────────
    // Enrollment mirrors the scalar facts TouchDown computed: the OnDrag/_dragTarget node → Drag (innermost, on the hit
    // node), the CanDrag chain → DragReorder (executed by DragController; enrolled so the arena sees the candidacy), the
    // Scrollable ancestor → Pan (outermost), the clickable _down → Tap (+ DoubleTap only where the node consumes double-
    // clicks, e.g. selectable text), a context/hold chain → Hold. The arena owns WHICH wins; the scalar machinery still
    // executes the winner, so the single-recognizer common case is byte-identical (the explicit fast-path: an arena with
    // one member is last-standing immediately). FSMs live in _fsms[member.FsmSlot]; each is Init'd to its kind and fed
    // OnDown here so a deferred resolution reports the DOWN position (§7A.5). Zero per-frame heap (fixed slab + the
    // ctor-captured sinks). The §7A.5 tentative-capture shift only manifests with ≥2 genuinely-competing recognizers;
    // the cases the scalar path already arbitrates (OnDrag-vs-pan, tap-vs-pan) resolve identically under the arena.

    /// <summary>µs clock for the FSMs from the event's ms platform stamp (the FSM keeps µs for the long-press; its
    /// VelocitySampler reprojects to ms). A 0 stamp stays 0 (the vacuous-fling / no-prior-down sentinel, §7B).</summary>
    private static long ToUs(uint timestampMs) => (long)timestampMs * 1000;

    /// <summary>Bind member slot <paramref name="ms"/>'s FSM to <paramref name="kind"/> and feed the DOWN (Init + OnDown +
    /// the opening vote into the arena). Idempotent storage — the slot is reused from the cap-10 slab.</summary>
    private void ArmMemberFsm(int ms, GestureKind kind, Point2 abs, long timeUs)
    {
        if ((uint)ms >= (uint)_fsms.Length) return;
        ref PointerFsm f = ref _fsms[ms];
        f.Init(kind);
        f.OnDown(abs, timeUs);
        _arena.SetVote(ms, f.Vote);
    }

    /// <summary>Open the contact's arena (§7A.1) and enroll members innermost-first along the hit route, mirroring the
    /// scalar facts. A route with no gesture-advertising node opens NO arena (<see cref="_activeArenaSlot"/> stays -1) and
    /// the contact keeps the pre-arena scalar flow. Called at the end of <see cref="TouchDown"/>.</summary>
    private void EnrollTouchArena(in InputEvent e, NodeHandle scrollable)
    {
        // Nothing advertises a gesture ⇒ no arena (the scrollbar-drag path already returned before this; a bare hit with
        // no Drag/Pan/clickable is just a press that taps-or-nothing — pre-arena behavior, no arbitration needed).
        bool hasDrag = !_dragTarget.IsNull;
        bool hasSwipe = !_swipeDrag.IsNull;   // a DragYieldsToPan OnDrag node (cross-axis content pan) competing with Pan
        bool hasPan = !scrollable.IsNull;
        bool hasPinch = !_pinchViewport.IsNull;   // a Zoomable viewport under this contact (Phase-4): a second contact opens the pinch
        bool clickable = !_down.IsNull && _scene.IsLive(_down)
                         && (_scene.Interaction(_down).HandlerMask
                             & (InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit)) != 0;
        NodeHandle dragReorder = (hasDrag || hasSwipe) ? NodeHandle.Null : NearestCanDrag(_down);   // DragController arms only off non-OnDrag chains
        // The touch long-press (Hold → context flyout) walks from the deepest VISUAL under the contact, not just the
        // interactively-hit _down: a context-request handler (ContextBit) is NOT in the Hit interaction mask (a box with
        // only OnContextRequested is hit-test-transparent to clicks), so a context-ONLY node would otherwise enroll no
        // Hold. This mirrors the mouse right-click path, which also resolves its context target via HitTestAny (the
        // any-node hit). When _down is set (a clickable+context row) the chain from it is identical.
        NodeHandle holdHit = _touchSuppressTap ? NodeHandle.Null : (_down.IsNull ? HitTestAny(e.PositionPx) : _down);
        NodeHandle holdChain = NearestContextOrHold(holdHit);
        // UseGesture (§13): the nearest self-or-ancestor that declared a gesture hook. Only probed when the scene has ANY
        // gesture subscription (HasGestureSubs) — the common case pays nothing. Its declared Tap/Hold/Pan kinds enroll as
        // arena members below; the arena WINNER routes to the handler (RouteGestureWin via OnMemberWon).
        NodeHandle gestureNode = _scene.HasGestureSubs ? NearestGesture(_down) : NodeHandle.Null;
        // A fresh down ⇒ a fresh arena: free any stale arena this contact's slot still carries (a prior gesture that
        // resolved but whose seat lingered), so OpenArena never re-enrolls members onto a half-used arena.
        if (_activeArenaSlot >= 0) { _arena.CloseAndFree(_activeArenaSlot); _activeArenaSlot = -1; }
        _reorderTarget = NodeHandle.Null; _touchReorder = false;   // fresh down: no resolved reorder yet (set below if enrolled)
        _gesturePanNode = NodeHandle.Null;                          // fresh down: no UseGesture(Pan) member yet
        _pinchMember = -1;                                          // fresh down: no Pinch member yet (set below if enrolled)
        _holdFired = false;                                         // fresh down: the long-press hasn't fired its context yet
        if (!hasDrag && !hasSwipe && !hasPan && !hasPinch && !clickable && dragReorder.IsNull && holdChain.IsNull && gestureNode.IsNull) return;

        long timeUs = ToUs(e.TimestampMs);
        int slot = _arena.OpenArena(e.PointerId, timeUs);
        if (slot < 0) { _activeArenaSlot = -1; return; }   // cap-10 arenas full: no arena (matches the capture-slab policy)
        _activeArenaSlot = slot;

        // Innermost-first (§7A.1): the hit node's own recognizers (Drag/Tap/DoubleTap) get the earliest claim (lowest
        // Priority → they win ties); the CanDrag/scrollable ancestors enroll after; Pan (outermost viewport) last.
        int m;
        int eagerDragSlot = -1;                                   // the eager OnDrag (Slider/EditableText) member, for the §7A.5 fast-path
        int selDragSlot = -1, selTapSlot = -1, selDblSlot = -1;   // the selection team's members (§7A.3), captured below
        bool selectable = clickable && (_scene.Interaction(_down).HandlerMask & InteractionInfo.SelectableTextBit) != 0;
        if (hasDrag)
        {
            m = _arena.Enroll(slot, _dragTarget, GestureKind.Drag);
            if (m >= 0) { ArmMemberFsm(m, GestureKind.Drag, e.PositionPx, timeUs); eagerDragSlot = m; if (selectable && _dragTarget == _down) selDragSlot = m; }
        }
        else if (hasSwipe)
        {
            // The cross-axis content-pan Drag member (SwipeControl/FlipView), innermost so it claims before Pan on an
            // along-axis tie. Axis-locked in StepTouchArena (projected onto _swipeAxisX) so it eager-wins ONLY along its
            // own axis and yields to the (scroll-axis-locked) Pan when the gesture is cross-axis. Found later by Kind==Drag.
            m = _arena.Enroll(slot, _swipeDrag, GestureKind.Drag);
            if (m >= 0) ArmMemberFsm(m, GestureKind.Drag, e.PositionPx, timeUs);
        }
        if (clickable)
        {
            m = _arena.Enroll(slot, _down, GestureKind.Tap);
            if (m >= 0) { ArmMemberFsm(m, GestureKind.Tap, e.PositionPx, timeUs); if (selectable) selTapSlot = m; }
            // DoubleTap ONLY where the node consumes double-clicks (selectable/editable text word-select); a plain button
            // gets Tap only (no inter-tap Held window to keep its arena open across taps).
            if (selectable)
            {
                m = _arena.Enroll(slot, _down, GestureKind.DoubleTap);
                if (m >= 0) { ArmMemberFsm(m, GestureKind.DoubleTap, e.PositionPx, timeUs); selDblSlot = m; }
            }
        }
        if (!dragReorder.IsNull)
        {
            // The DragReorder member competes with Pan via an AXIS-LOCKED vote (§7A): its FSM is driven on every move
            // with the position projected onto the item's reorder axis (the source row's parent-container main axis —
            // a row container ⇒ horizontal item-drag), so it eager-wins only on travel ALONG that axis. A cross-axis
            // drag leaves it Pending and the (scroll-axis-locked) Pan eager-wins instead. This replaces the
            // DragController.YieldsToPan(dx,dy) heuristic with the arena's deterministic resolution (the
            // two-arbitration-models risk is gone); the WINNER drives DragController, arena-governed.
            m = _arena.Enroll(slot, dragReorder, GestureKind.DragReorder);
            if (m >= 0)
            {
                ArmMemberFsm(m, GestureKind.DragReorder, e.PositionPx, timeUs);
                _reorderTarget = dragReorder;
                var rp = _scene.Parent(dragReorder);
                _reorderAxisX = !rp.IsNull && _scene.Layout(rp).Direction == 0;   // 0 = row container ⇒ horizontal item-drag
            }
        }
        if (!holdChain.IsNull)
        {
            m = _arena.Enroll(slot, holdChain, GestureKind.Hold);
            if (m >= 0) ArmMemberFsm(m, GestureKind.Hold, e.PositionPx, timeUs);
        }
        if (hasPan)
        {
            m = _arena.Enroll(slot, scrollable, GestureKind.Pan);
            if (m >= 0) ArmMemberFsm(m, GestureKind.Pan, e.PositionPx, timeUs);
        }
        // Pinch (Phase-4): a viewport-level member on the nearest Zoomable viewport (outermost; it stays Pending — a Pinch
        // FSM votes ONLY on OnSecondContact, never on move). When a SECOND contact lands over the SAME viewport, the first
        // contact's Pinch member is fed OnSecondContact → EagerAccept and sweeps this contact's Pan/Tap (the §7B trigger).
        // The member slot is saved on the contact (_pinchMember, riding the slot) so the second-contact handler can reach it.
        if (hasPinch)
        {
            m = _arena.Enroll(slot, _pinchViewport, GestureKind.Pinch);
            if (m >= 0) { ArmMemberFsm(m, GestureKind.Pinch, e.PositionPx, timeUs); _pinchMember = m; }
        }
        // UseGesture (§13) members on the declaring node. A Tap/Hold member is enrolled only when not ALREADY covered by
        // the clickable-Tap / holdChain enrollment on the SAME node (the routing keys on the winner's node, so one member
        // suffices — RouteGestureWin fires the handler regardless of which path enrolled it). A UseGesture(Pan) member is
        // enrolled on the gesture node and voted RAW (any-axis) in StepTouchArena (the gesture pan is not scroll-axis-locked).
        if (!gestureNode.IsNull)
        {
            if (_scene.WantsGesture(gestureNode, GestureType.Tap) && !(clickable && _down == gestureNode))
            {
                m = _arena.Enroll(slot, gestureNode, GestureKind.Tap);
                if (m >= 0) ArmMemberFsm(m, GestureKind.Tap, e.PositionPx, timeUs);
            }
            if (_scene.WantsGesture(gestureNode, GestureType.Hold) && gestureNode != holdChain)
            {
                m = _arena.Enroll(slot, gestureNode, GestureKind.Hold);
                if (m >= 0) ArmMemberFsm(m, GestureKind.Hold, e.PositionPx, timeUs);
            }
            if (_scene.WantsGesture(gestureNode, GestureType.Pan))
            {
                m = _arena.Enroll(slot, gestureNode, GestureKind.Pan);
                if (m >= 0)
                {
                    ArmMemberFsm(m, GestureKind.Pan, e.PositionPx, timeUs);
                    _gesturePanNode = gestureNode;   // marks the raw-voted (any-axis) gesture pan for StepTouchArena
                }
            }
        }

        // The §7A.3 selection TEAM: the editor/selectable-text node's tap (caret-place), double-tap (word-select) and
        // drag-extend recognizers present ONE arena entry under a captain so they don't reject each other before the
        // slop decides which it is (Flutter GestureArenaTeam; the canonical selection use). The captain is the Tap
        // member; on a team win it decides which internal recognizer fires by tap-count + movement — REALIZED by the
        // editor's existing ClickCount (single→caret, double→word) + OnDrag (drag→extend) mechanics, which the
        // OnDrag implicit-capture already executes. This FORMALIZES the Phase-2 flat enrollment (gate.touch2.caret-and-
        // select stays observably identical): the members were already independent (a loser only rejects via a winner's
        // sweep, never each other pre-slop), so grouping them under a captain changes the structure, not the behavior.
        if (selTapSlot >= 0)
        {
            int teamStart = selDragSlot >= 0 ? selDragSlot : selTapSlot;   // the Drag (drag-extend) member is innermost when present
            int teamEnd = selDblSlot >= 0 ? selDblSlot : selTapSlot;       // members enroll contiguously: drag → tap → dbltap
            _arena.EnrollTeam(slot, captainSlot: selTapSlot, memberOffset: teamStart, memberLen: teamEnd - teamStart + 1);
        }

        // §7A.5 single-recognizer FAST-PATH: an eager OnDrag capture (Slider scrub / EditableText drag — _dragTarget set
        // in TouchDown, NOT a DragYieldsToPan swipe) is the canonical "one recognizer → immediate accept → synchronous
        // capture" case. The scalar path ALREADY took hard capture into _dragTarget (TouchMove drives OnDrag the same
        // frame, bypassing StepTouchArena's vote loop and never feeding this Drag member a move); resolve the arena to
        // MATCH so capture is hard — NOT tentative (CaptureIsTentative is false the instant the press lands). The eager
        // Drag EagerAccept-wins and sweeps any incidental co-members (a Slider's own Tap/Pressed Tap member; a selectable
        // editor's tap/double-tap team): those are EXECUTED by the editor's/slider's scalar OnDrag + ClickCount mechanics
        // regardless of the arena winner, so the sweep changes the arena bookkeeping, not the observable caret/scrub
        // behavior (gate.touch2.caret-and-select / 47t stay identical). This fast-path is suppressed for a DragYieldsToPan
        // swipe (hasSwipe) and for a Pan/DragReorder contact (no _dragTarget) — those genuinely compete and stay tentative
        // until the slop resolves them (the §7A.5 semantics shift, which only manifests with ≥2 competing recognizers).
        if (eagerDragSlot >= 0 && !hasSwipe)
        {
            _arena.SetVote(eagerDragSlot, ArenaVote.EagerAccept);
            _arena.ResolveStep(slot);   // the eager OnDrag wins immediately → hard capture, no tentative window (§7A.5 fast-path)
        }
    }

    /// <summary>Nearest self-or-ancestor of <paramref name="from"/> that declared ANY <c>UseGesture</c> handler (§13), or
    /// null. Only called when <c>SceneStore.HasGestureSubs</c> is set, so the common gesture-free scene never walks here.</summary>
    private NodeHandle NearestGesture(NodeHandle from)
    {
        for (var n = from; !n.IsNull; n = _scene.Parent(n))
            if (_scene.IsLive(n) && (_scene.Flags(n) & NodeFlags.Disabled) == 0
                && (_scene.WantsGesture(n, GestureType.Tap) || _scene.WantsGesture(n, GestureType.Hold) || _scene.WantsGesture(n, GestureType.Pan)))
                return n;
        return NodeHandle.Null;
    }

    /// <summary>Feed this move to the contact's arena (§7A.2/§7A.4) and resolve a step. Returns true when the move is
    /// FULLY handled in here — i.e. the arena resolved DragReorder and <see cref="ClaimTouchReorder"/> already drove the
    /// controller. A pan claim sets <see cref="_panClaimed"/> (the caller drives the content scroll, gated on the claim);
    /// false also when neither crossed slop yet.
    ///
    /// Both movement recognizers are AXIS-LOCKED and voted here: the Pan member crosses the SCROLL-axis slop, the
    /// DragReorder member crosses the ITEM-axis slop (each FSM driven with the position PROJECTED onto its axis so its
    /// generic Near-slop reduces to the single-axis test). Whichever crosses its own axis first eager-wins and sweeps
    /// the rest (§7A.2 rule 1) — this IS the drag-reorder-vs-pan arbitration, deterministic and via votes, REPLACING
    /// <c>DragController.YieldsToPan</c> on the touch path: a reorder drag along the item axis beats the scroller's pan;
    /// a cross-axis drag leaves DragReorder Pending and Pan eager-wins. The Pan claim stays gated on the Pan FSM actually
    /// crossing slop (not on a lone-Pan last-standing resolution), so the Pan-only observable is identical to Phase 1.
    /// Tap/DoubleTap members are not move-voted: an eager-win sweeps them (the synthetic GestureRejected == the scalar
    /// press-cancel), so a tap dies at the claim, not on any-axis slop.</summary>
    private bool StepTouchArena(in InputEvent e)
    {
        int slot = _activeArenaSlot;
        if (slot < 0 || !_arena.IsArenaOpen(slot)) return false;
        long timeUs = ToUs(e.TimestampMs);

        ReadOnlySpan<ArenaMember> members = _arena.Members(slot);
        int memberOffset = _arena.ArenaAt(slot).MemberOffset;
        int panMember = -1, reorderMember = -1, swipeMember = -1;
        ArenaVote panVote = ArenaVote.Pending, reorderVote = ArenaVote.Pending, swipeVote = ArenaVote.Pending;
        for (int i = 0; i < members.Length; i++)
        {
            int ms = memberOffset + i;
            switch (members[i].Kind)
            {
                case GestureKind.Tap:
                case GestureKind.DoubleTap:
                case GestureKind.RightTap:
                case GestureKind.Hold:
                    // These recognizers reject on radial/total movement. Feeding every move is essential: the
                    // scroller Pan below is deliberately axis-projected and must not keep a diagonal tap/hold alive.
                    _arena.SetVote(ms, _fsms[ms].OnMove(e.PositionPx, timeUs));
                    break;
                case GestureKind.Drag when members[i].Node == _swipeDrag:
                    // The cross-axis content-pan Drag member (SwipeControl/FlipView): projected onto its OWN axis
                    // (_swipeAxisX) so it eager-wins only on along-axis travel and yields to the (scroll-axis-locked) Pan on
                    // a cross-axis drag — the same axis-locked rule the DragReorder member uses, driving OnDrag not the
                    // reorder controller. (The eager Slider/EditableText Drag never reaches here — _swipeDrag is null then.)
                    swipeMember = ms;
                    swipeVote = _fsms[ms].OnMove(
                        _swipeAxisX ? new Point2(e.PositionPx.X, _panAnchorPx.Y) : new Point2(_panAnchorPx.X, e.PositionPx.Y),
                        timeUs);
                    _arena.SetVote(ms, swipeVote);
                    break;
                case GestureKind.Pan:
                    // A UseGesture(Pan) member (its node == _gesturePanNode) is voted RAW (any-axis Near-slop — a gesture
                    // pan is not scroll-axis-locked); the scroller Pan member is projected onto the scroll axis (the
                    // single-axis WinUI pan). The gesture-pan does NOT claim a continuous content drive (no _panTarget) —
                    // its eager-win just routes the §13 handler (RouteGestureWin in ResolveStep below).
                    if (members[i].Node == _gesturePanNode)
                    {
                        _fsms[ms].OnMove(e.PositionPx, timeUs);
                        _arena.SetVote(ms, _fsms[ms].Vote);
                    }
                    else
                    {
                        panMember = ms;
                        panVote = _fsms[ms].OnMove(
                            _panAxisX ? new Point2(e.PositionPx.X, _panAnchorPx.Y) : new Point2(_panAnchorPx.X, e.PositionPx.Y),
                            timeUs);
                        _arena.SetVote(ms, panVote);
                    }
                    break;
                case GestureKind.DragReorder:
                    reorderMember = ms;
                    // Project onto the ITEM axis (the source row's parent main axis) so the reorder eager-wins only on
                    // along-axis travel — the YieldsToPan axis rule, now a vote. _reorderAxisX true ⇒ item drags along X.
                    reorderVote = _fsms[ms].OnMove(
                        _reorderAxisX ? new Point2(e.PositionPx.X, _panAnchorPx.Y) : new Point2(_panAnchorPx.X, e.PositionPx.Y),
                        timeUs);
                    _arena.SetVote(ms, reorderVote);
                    break;
            }
        }

        // Resolve: the first axis-slop eager-win sweeps the rest. Innermost priority (DragReorder enrolls before Pan, so
        // a lower Priority) wins a same-move tie — but a clean cross-axis drag only crosses ONE axis, so ties are rare.
        // A UseGesture(Pan) member that wins reports THIS sample's position (the §13 routing fires inside ResolveStep).
        _gestureWinPos = e.PositionPx; _gestureWinPointer = e.Pointer; _gestureWinVel = Point2.Zero;
        _arena.ResolveStep(slot);
        int winner = _arena.ArenaAt(slot).WinnerSlot;

        // Execute the winner. The claim is gated on the winning FSM having EAGER-ACCEPTED this move (crossed its slop),
        // not merely on resolution (a lone member is last-standing immediately, but content/reorder must not start until
        // slop — exactly Phase 1's gating). DragReorder drives DragController (arena-governed: YieldsToPan bypassed).
        if (!_touchReorder && reorderMember >= 0 && winner == reorderMember && reorderVote == ArenaVote.EagerAccept)
            ClaimTouchReorder(e);
        else if (_dragTarget.IsNull && swipeMember >= 0 && winner == swipeMember && swipeVote == ArenaVote.EagerAccept)
            ClaimTouchSwipe(e);
        else if (!_panClaimed && panMember >= 0 && winner == panMember && panVote == ArenaVote.EagerAccept)
            ClaimTouchPan();
        // A swipe claim is fully driven in here (the first OnDrag move ran); the subsequent moves go through the
        // _dragTarget branch in TouchMove, so return true to consume this move. A reorder is likewise fully driven.
        return _touchReorder || (!_dragTarget.IsNull && _dragTarget == _swipeDrag);
    }

    /// <summary>The arena resolved the cross-axis content-pan Drag for this contact (§7A — SwipeControl/FlipView): the
    /// along-axis swipe beat the scroller's Pan. Capture it into <see cref="_dragTarget"/> (so the rest of the gesture
    /// drives <c>OnDrag</c> through the existing capture branch, exactly as the eager OnDrag path would), cancel the press
    /// candidate the capture-loss way (Pressed→Canceled, never a click), and drive THIS move's <c>OnDrag</c> immediately so
    /// the content follows from the first claimed sample. The Pan member was swept by the eager-win, so the list will not
    /// scroll. Velocity keeps sampling on the captured moves (TouchMove's _dragTarget branch) for the release snap.</summary>
    private void ClaimTouchSwipe(in InputEvent e)
    {
        if (_swipeDrag.IsNull || !_scene.IsLive(_swipeDrag)) return;
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        // Cancel the press the same way ClaimTouchPan does (CancelWorkingContact shape): release the pressed visual + any
        // auto-repeat and null _down so the eventual up fires NO click on the down chain (the swipe's own OnClick release
        // edge still fires via the _dragTarget branch in TouchUp).
        SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
        if (!_down.IsNull)
        {
            if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            if (_scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                OnRepeatReleased?.Invoke(_down);
            _down = NodeHandle.Null;
        }
        _selDragging = false;
        _panTarget = NodeHandle.Null; _panClaimed = false;   // the swipe won — the scroller pan is off for this contact
        _dragTarget = _swipeDrag;                            // capture: subsequent moves drive OnDrag via the _dragTarget branch
        _scene.GetDrag(_dragTarget)?.Invoke(LocalPos(_dragTarget, e.PositionPx));   // drive the first claimed sample
    }

    /// <summary>The arena resolved DragReorder for this contact (§7A): latch the reorder, cancel the press candidate the
    /// way capture-loss does (the Pressed→Canceled contract, never a click), and ARM + drive <see cref="DragController"/>
    /// arena-governed (its <c>YieldsToPan</c> bypassed — the arena already arbitrated). The drag-reorder is a singleton
    /// engine, so a second concurrent contact landing on a CanDrag row simply doesn't claim (Drag already active); its
    /// arena still resolves but the execution no-ops — deterministic, no second drag.</summary>
    private void ClaimTouchReorder(in InputEvent e)
    {
        if (_reorderTarget.IsNull || !_scene.IsLive(_reorderTarget)) return;
        if (Drag.IsActive || Drag.IsArmed) return;   // the singleton reorder engine is busy with another contact
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        // Cancel the press the same way ClaimTouchPan does (CancelWorkingContact shape): release the pressed visual and
        // any auto-repeat, and null _down so the eventual up fires NO click (the lifted row never clicks — WinUI).
        SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
        if (!_down.IsNull)
        {
            if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            if (_scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0)
                OnRepeatReleased?.Invoke(_down);
            _down = NodeHandle.Null;
        }
        _selDragging = false;
        _touchReorder = true;
        // Arm from the press anchor (the gesture's down position, so TotalDx/Dy measure from there) and immediately drive
        // the current move so the controller promotes this frame (arena-governed ⇒ no YieldsToPan re-arbitration).
        Drag.TryArm(_reorderTarget, _panAnchorPx, e.Pointer, e.Mods, e.TimestampMs);
        Drag.Move(e.PositionPx, e.Mods, e.TimestampMs, arenaGoverned: true);
    }

    /// <summary>Pointer-up sweep (§7A.2 rule 4) for the contact's arena: the clean-tap resolution. Feeds OnUp to the Tap/
    /// DoubleTap members so a within-slop release votes Accept, then <c>ResolveUp</c> picks the highest-priority survivor.
    /// The scalar <see cref="TouchUp"/> still fires the actual click on the winner; this keeps the arena state correct
    /// (and frees the seat). A claimed-pan or captured-OnDrag contact never reaches here (those branches return earlier).</summary>
    private void UpSweepTouchArena(in InputEvent e)
    {
        int slot = _activeArenaSlot;
        if (slot < 0 || !_arena.IsArenaOpen(slot)) return;
        long timeUs = ToUs(e.TimestampMs);
        ReadOnlySpan<ArenaMember> members = _arena.Members(slot);
        for (int i = 0; i < members.Length; i++)
        {
            GestureKind k = members[i].Kind;
            if (k is GestureKind.Tap or GestureKind.RightTap or GestureKind.DoubleTap)
            {
                int ms = _arena.ArenaAt(slot).MemberOffset + i;
                ArenaVote v = _fsms[ms].OnUp(e.PositionPx, timeUs);
                _arena.SetVote(ms, v);
            }
        }
        // A UseGesture(Tap) member that wins the up-sweep reports the UP position (the §13 routing fires in ResolveUp).
        _gestureWinPos = e.PositionPx; _gestureWinPointer = e.Pointer; _gestureWinVel = Point2.Zero;
        _arena.ResolveUp(slot);
    }

    /// <summary>Nearest <c>CanDrag</c> (drag-reorder source, <see cref="InteractionInfo.DragBit"/>) self-or-ancestor of
    /// <paramref name="from"/>, or null. Mirrors <see cref="DragController.TryArm"/>'s chain walk so the DragReorder
    /// member is enrolled exactly where the controller would arm.</summary>
    private NodeHandle NearestCanDrag(NodeHandle from)
    {
        for (var n = from; !n.IsNull; n = _scene.Parent(n))
            if (_scene.IsLive(n) && (_scene.Interaction(n).HandlerMask & InteractionInfo.DragBit) != 0
                && (_scene.Flags(n) & NodeFlags.Disabled) == 0)
                return n;
        return NodeHandle.Null;
    }

    /// <summary>Nearest enabled DragYieldsToPan content-pan drag node (SwipeControl/FlipView) self-or-ancestor of
    /// <paramref name="from"/>, or null — the §7A.1 route walk that enrolls a swipe/flip wrapper AROUND an interactive
    /// row (the hit node carries no drag handler of its own, so the wrapper's axis-locked Drag member would never arm).
    /// Mirrors <see cref="NearestCanDrag"/> (enabled + live), additionally gated on the DragYieldsToPan flag AND a real
    /// OnDrag handler so a plain box never matches. Touch-path only.</summary>
    private NodeHandle NearestSwipeDrag(NodeHandle from)
    {
        for (var n = from; !n.IsNull; n = _scene.Parent(n))
            if (_scene.IsLive(n) && (_scene.Flags(n) & NodeFlags.Disabled) == 0
                && (_scene.Flags(n) & NodeFlags.DragYieldsToPan) != 0
                && _scene.GetDrag(n) is not null)
                return n;
        return NodeHandle.Null;
    }

    /// <summary>Nearest enabled context-request (<see cref="InteractionInfo.ContextBit"/>) self-or-ancestor of
    /// <paramref name="from"/>, or null — the chain a touch long-press (Hold → context flyout) would target. Touch has no
    /// right button, so the Hold member is the only path to a context request on the touch surface.</summary>
    private NodeHandle NearestContextOrHold(NodeHandle from)
    {
        for (var n = from; !n.IsNull; n = _scene.Parent(n))
            if (_scene.IsLive(n) && (_scene.Interaction(n).HandlerMask & InteractionInfo.ContextBit) != 0
                && (_scene.Flags(n) & NodeFlags.Disabled) == 0)
                return n;
        return NodeHandle.Null;
    }

    /// <summary>The §7A "OnFrameEnd" timer tick (§7A.4): advance the arena clock by the frame's <paramref name="dtMs"/>
    /// and promote any Hold member whose long-press window elapsed (its FSM votes <see cref="ArenaVote.EagerAccept"/>),
    /// then resolve a step so the Hold can win. Wired from the host's phase-7 tick block (alongside the Repeat/Drag ticks),
    /// the existing per-frame end hook the plan points at. On a Hold win the win sink (<see cref="RouteGestureWin"/>) FIRES
    /// the context flyout (a ContextBit node) / the UseGesture(Hold) handler, and this flags the contact so its eventual up
    /// suppresses the tap-click (the press visual stays held until then). A motionless held finger emits no events, so the
    /// host keeps frames coming via <see cref="HasArmedHold"/> (<c>WakeReasons.GestureHold</c>) until this fires. Zero
    /// per-frame heap: it scans the fixed slab only while arenas are open. No-op when nothing is open.</summary>
    public void TickGestureArenas(float dtMs)
    {
        if (dtMs > 0f)
        {
            for (int si = 0; si < _slots.Length; si++)
            {
                ref PointerSlot s = ref _slots[si];
                if (!s.Used || !s.PendingTouchPress) continue;
                if (s.Down.IsNull || !_scene.IsLive(s.Down) || s.PanClaimed)
                {
                    s.PendingTouchPress = false; s.PendingTouchPressMs = 0f;
                    continue;
                }
                s.PendingTouchPressMs += dtMs;
                if (s.PendingTouchPressMs < 100f) continue;
                s.PendingTouchPress = false; s.PendingTouchPressMs = 0f;
                SetState(ref _pressed, s.Down, NodeFlags.Pressed);
            }
        }
        if (_arena.OpenArenaCount == 0) return;
        if (dtMs > 0f) _arenaClockUs += (long)(dtMs * 1000f);   // advance even with no events this frame (idle-held finger)
        for (int slot = 0; slot < GestureArena.MaxArenas; slot++)
        {
            if (!_arena.IsArenaOpen(slot) || _arena.ArenaAt(slot).WinnerSlot >= 0) continue;
            ReadOnlySpan<ArenaMember> members = _arena.Members(slot);
            bool promoted = false;
            Point2 holdStart = default;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i].Kind != GestureKind.Hold) continue;
                int ms = _arena.ArenaAt(slot).MemberOffset + i;
                ArenaVote v = _fsms[ms].OnFrameTick(_arenaClockUs);
                if (v != _arena.VoteOf(ms)) { _arena.SetVote(ms, v); promoted = true; holdStart = _fsms[ms].Start; }
            }
            if (promoted)
            {
                // The Hold win reports the DOWN position (no current move in a timer tick — the FSM buffered it). On a win,
                // OnMemberWon (RouteGestureWin) fires the context flyout / UseGesture(Hold) handler at that point.
                _gestureWinPos = holdStart; _gestureWinPointer = PointerKind.Touch; _gestureWinVel = Point2.Zero;
                int winner = _arena.ResolveStep(slot);
                // If a Hold actually won, flag the owning contact's slot so its eventual PointerUp SUPPRESSES the tap-click
                // (WinUI: a long-press that showed the context flyout does not also fire the element's click on release).
                // The press visual stays held until the up (the contact still owns _pressed). TickGestureArenas runs OUTSIDE
                // the SlotIn/SlotOut window, so write the slot directly (find it by ArenaSlot == this arena).
                if (winner >= 0 && _arena.MemberAt(winner).Kind == GestureKind.Hold)
                    for (int si = 0; si < _slots.Length; si++)
                        if (_slots[si].Used && _slots[si].ArenaSlot == slot) { _slots[si].HoldFired = true; break; }
            }
        }
    }

    /// <summary>Touch up: a captured OnDrag gesture (slider scrub / editor drag-select) delivers its release to that node
    /// — its click handler is the commit edge (WinUI capture-to-release, even when the finger ends outside the node); a
    /// claimed scrollbar thumb-drag releases its per-PointerId <c>_scrollDragNode</c> and lets the bar fade (touch has no
    /// resting hover, so the contact-duration reveal ends here); a claimed pan hands its sampled velocity to the fling
    /// stage (gated by <see cref="ScrollTuning.FlingSeedGate"/>); otherwise a below-slop down→up over the same node is a tap
    /// (the existing click + hyperlink-span path) and releases the pressed visual. Either way the contact's touch-set
    /// hover is cleared (no resting touch hover).</summary>
    private bool TouchUp(in InputEvent e)
    {
        _pendingTouchPress = false; _pendingTouchPressMs = 0f;
        // Phase-4 pinch end: a pinching contact lifted. Commit the scale (ZoomFactor stays) and CONTINUE the gesture with
        // the surviving finger as a pan (WinUI continues the manipulation with the remaining contact) — re-anchor its slot
        // as an already-claimed pan from its last position + the committed offset, so its next move scrolls the (now
        // zoomed) content with no slop re-cross. This contact's up fires no tap/click/fling (it was the pinch).
        if (!_pinchSessionViewport.IsNull && (e.PointerId == _pinchIdA || e.PointerId == _pinchIdB))
        {
            NodeHandle vp = _pinchSessionViewport;
            uint survivorId = e.PointerId == _pinchIdA ? _pinchIdB : _pinchIdA;
            Point2 survivorPos = e.PointerId == _pinchIdA ? _pinchPosB : _pinchPosA;
            EndPinchSession();
            ContinuePinchAsPan(vp, survivorId, survivorPos, e.TimestampMs, e.QpcTicks);
            // Clear this lifting contact's own state (it was the pinch; no tap/fling). Its slot recycles in SlotOut.
            if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            _down = NodeHandle.Null; _panTarget = NodeHandle.Null; _panClaimed = false;
            _pinchViewport = NodeHandle.Null; _pinchMember = -1;
            ClearTouchHover();
            return true;
        }

        // §7A long-press: the Hold already won + fired its context flyout while the finger was held (TickGestureArenas).
        // The up releases the held press visual but fires NO tap-click (WinUI: a long-press that showed the context menu
        // does not also click the element on release) and no fling. Nulling _down frees the arena seat in SlotOut.
        if (_holdFired)
        {
            if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            _down = NodeHandle.Null; _panTarget = NodeHandle.Null; _panClaimed = false;
            _holdFired = false;
            ClearTouchHover();
            return true;
        }

        // Resolve the contact's arena on the up-sweep (§7A.2 rule 4): a within-slop Tap/RightTap/DoubleTap votes Accept and
        // the highest-priority survivor wins — the clean-tap resolution. This is arena bookkeeping (the win/reject sinks
        // are a no-op / an FSM reset this stage); the scalar branches below still fire the actual click / fling / commit,
        // so the observable is unchanged. A claimed-pan or captured-OnDrag contact resolved earlier (or resolves here to
        // its Pan/Drag survivor) — harmless either way; SlotOut frees the seat once the scalar targets all clear.
        UpSweepTouchArena(in e);
        FireGesturePanEnd(in e);   // §13: a resolved UseGesture(Pan) gets its end-velocity on up (before the seat frees)
        bool handled = false;
        if (_touchReorder)
        {
            // The contact was driving a drag-reorder (the arena resolved DragReorder, §7A): complete it exactly like the
            // mouse PointerUp Drag.IsActive branch — Complete fires OnDragCompleted (the app commits the reorder), suppresses
            // the click (the lifted row never clicks — already enforced by the nulled _down at claim), and hands OnSettle the
            // drop→resting rects for the FLIP glide. arena-governed needs no flag here (the gesture is over).
            if (Drag.IsActive) Drag.Complete(e.PositionPx, e.Mods, e.TimestampMs);
            else Drag.Disarm();   // armed-but-never-promoted safety (shouldn't reach: the claim promotes on the same move)
            _reorderTarget = NodeHandle.Null;
            _touchReorder = false;
            handled = true;
        }
        else if (!_dragTarget.IsNull)
        {
            // The press captured an OnDrag gesture (Slider scrub / EditableText drag-select) — deliver the release to it,
            // exactly like the mouse PointerUp _dragTarget branch: its click handler is its commit edge (Slider Ranged
            // CloseTip; EditableText has none, so this is a harmless null call), and it fires even if the finger lifted
            // off the node (implicit capture). A PointerCancel still skips this (capture loss is not a commit — the
            // CancelWorkingContact path clears _dragTarget without firing). No content pan was ever armed alongside it.
            // A claimed cross-axis swipe samples its release velocity FIRST so its OnClick commit edge (SwipeControl
            // ReleaseOrTap / FlipView CommitPan) can read the real flick speed off PointerVelocity for the WinUI snap
            // (100px open / 31px/s close; FlipView flick-navigate). Re-window at lift — don't fold the up point (ComputeReleaseVelocity).
            if (_dragTarget == _swipeDrag) { FeedPanVelocity(e.PointerId); _panVel.ComputeReleaseVelocity(e.TimestampMs, e.QpcTicks); }
            if (_scene.IsLive(_dragTarget) && (_scene.Flags(_dragTarget) & NodeFlags.Disabled) == 0)
                InvokeActivation(_dragTarget, ContextRequestTrigger.Invoke);   // one commit path (a drag node is never a ContextBit invoker in practice)
            _dragTarget = NodeHandle.Null;
            _panTarget = NodeHandle.Null;
            _swipeDrag = NodeHandle.Null;
            handled = true;
        }
        else if (!_scrollDragNode.IsNull)
        {
            // Touch lifted off the thumb: release the drag and drop the bar's PointerOverScrollbar/PointerOver reveal so
            // it fades on the idle timer (no mouse cursor rests over it to keep it up — the contact-duration reveal ends).
            var dragged = _scrollDragNode;
            _scrollDragNode = NodeHandle.Null;
            OnScrollLeave?.Invoke(dragged);
            handled = true;
        }
        else if (_panClaimed)
        {
            // Read the release velocity from the move-stream regression RE-WINDOWED at the lift stamp — NOT by folding the up
            // point in (that injected a near-zero-velocity sample at the newest time and corrupted the seed by up to 100% on OS
            // lift-timing jitter — the "small fast flick scrolls only a bit, inconsistently" report). See ComputeReleaseVelocity.
            FeedPanVelocity(e.PointerId);
            _panVel.ComputeReleaseVelocity(e.TimestampMs, e.QpcTicks);
            float v = _panAxisX ? _panVel.Vx : _panVel.Vy;
            // Released while the rubber band is displaced (the finger was holding past the clamp): spring the band back to
            // 0 in phase 7 (the ScrollIntegrator's critically-damped StepSpring) and start NO fling — an overpan release is
            // not a flick, it's a bounce-back. Clearing Overscrolling hands the band to the spring; arming keeps frames
            // coming until it settles. Otherwise (released in-range) seed the friction-decay fling as before; the viewport
            // scrolls -delta as the finger moves +axis, so the fling (in offset space) carries -velocity.
            if (!_panTarget.IsNull && _scene.IsLive(_panTarget) && _scene.HasScroll(_panTarget))
            {
                ref ScrollState psc = ref _scene.ScrollRef(_panTarget);
                OnScrollTrackEnd?.Invoke(_panTarget);     // §2.1: clear any resampler latch (no-op for a per-node touch pan)
                psc.PendingRawOffset = float.NaN;         // stop the per-node touch-pan intent (the tick must not re-apply it)
                psc.PhaseFlags &= unchecked((byte)~ScrollState.PhaseTouchPan);
                if (psc.OverscrollPx != 0f)
                {
                    if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"UP  overpan-release band={psc.OverscrollPx:0.0} -> spring back");
                    psc.Overscrolling = false;            // release the band to the phase-7 spring-back
                    psc.OverscrollVel = 0f;
                    if (psc.Phase == ScrollIntegrator.TouchpadTracking || psc.Phase == ScrollIntegrator.Overscroll)
                        psc.Phase = ScrollIntegrator.SnapBack;
                    OnScrollArmed?.Invoke(_panTarget);    // arm the ScrollIntegrator (WakeReasons.ScrollAnim ticks the spring)
                }
                else if (MathF.Abs(v) >= ScrollTuning.FlingSeedGate)
                {
                    if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"UP  fling seed v={-v:0} (panVel={v:0})");
                    OnFlingStarted?.Invoke(ChainFlingTarget(), -v);   // → SeedScrollFling: Phase = Fling
                }
                else   // came to rest in-range: end the tracking state so the node settles to Idle
                {
                    if (psc.Phase == ScrollIntegrator.TouchpadTracking) { psc.Phase = ScrollIntegrator.Idle; OnScrollArmed?.Invoke(_panTarget); }
                    if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"UP  no fling (|v|={MathF.Abs(v):0} < {ScrollTuning.FlingSeedGate:0})");
                }
            }
            _panTarget = NodeHandle.Null;
            _panClaimed = false;
            handled = true;
        }
        else if (!_down.IsNull)
        {
            var up = HitTest(e.PositionPx);
            // A pan candidate that NEVER claimed (no mid-gesture MOVE crossed the scroll-axis slop) yet whose RELEASE is
            // past that slop is a quick flick the OS UNDER-SAMPLED — not a tap. Firing the click on `up == _down` here is
            // the "touch scroll selects a track / navigates a card / shows the play button" bug: the scalar path was
            // tapping on hit-equality alone, ignoring the slop the arena Tap FSM already enforces on its OnUp. Complete it
            // as the scroll the finger intended (residual offset + fling) and fire NO click. A plain clickable with no
            // scroller (_panTarget null) keeps WinUI's release-over-element click unconditionally (no slop cancellation).
            if (!_panTarget.IsNull && CompleteUnderSampledPan(in e))
                handled = true;
            else if (up == _down)
            {
                DispatchPointerReleased(up, e.PositionPx);
                InvokeActivation(up, ContextRequestTrigger.Invoke);   // tap = release-over-same click (or context-invoke on a ClickRequestsContext node)
                if ((_scene.Interaction(up).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
                {
                    int si = HitLinkSpan(up, PointToLocal(up, e.PositionPx));
                    if (si >= 0 && _scene.TryGetSpanText(up, out var linkSpans) && (uint)si < (uint)linkSpans.Length)
                        linkSpans[si].OnClick?.Invoke();
                }
                handled = true;
            }
            _panTarget = NodeHandle.Null;   // the pan candidate ends with the tap / completed late-pan
        }
        // No click candidate (the press hit non-clickable scroller content): still complete an under-sampled flick so a
        // quick swipe over empty list area scrolls instead of doing nothing.
        else if (!_panTarget.IsNull && CompleteUnderSampledPan(in e)) { handled = true; _panTarget = NodeHandle.Null; }
        else _panTarget = NodeHandle.Null;

        // Release the pressed visual the down set (the contact owns the _pressed singleton while _pressed == _down). On a
        // tap this fires AFTER the click, so the handler runs while still Pressed, then the node settles back to Normal —
        // NOT PointerOver (touch has no cursor; the hover-clear below removes any transient touch hover too).
        if (_pressed == _down) SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        _down = NodeHandle.Null;
        _swipeDrag = NodeHandle.Null;   // an un-claimed swipe candidate ends with the contact (a below-slop tap took the tap branch)
        _pinchViewport = NodeHandle.Null; _pinchMember = -1;   // a pinch candidate that never became a pinch ends with the contact
        _touchSuppressTap = false;
        _selDragging = false;
        ClearTouchHover();   // lifting the finger never leaves a latched hover (touch has no resting hover)
        return handled;
    }

    /// <summary>Clear the single hover field if it is non-null and live — a touch contact must never latch hover, so its
    /// up/cancel fires OnHoverChanged(node,false) symmetric with a mouse leave (the hover field is mouse/pen-owned, so
    /// this is a no-op unless a stray path set it).</summary>
    private void ClearTouchHover() => SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);

    private void DispatchPointerReleased(NodeHandle node, Point2 positionPx)
    {
        _scene.GetPointerReleased(node)?.Invoke(new PointerEventArgs
        {
            Local = LocalPos(node, positionPx), ClickCount = _pressClickCount, Mods = _pressMods,
            Button = 0, Kind = _pressKind,
        });
    }

    /// <summary>Promote consecutive same-button presses inside the slop window into double/triple clicks (capped at 3).</summary>
    private void TrackClickCount(in InputEvent e)
    {
        bool chained = _lastDownButton == e.Button
                       && e.TimestampMs - _lastDownMs <= DoubleClickMs
                       && MathF.Abs(e.PositionPx.X - _lastDownPos.X) <= ClickSlopPx
                       && MathF.Abs(e.PositionPx.Y - _lastDownPos.Y) <= ClickSlopPx;
        _clickCount = chained ? (byte)Math.Min(_clickCount + 1, 3) : (byte)1;
        _lastDownMs = e.TimestampMs;
        _lastDownPos = e.PositionPx;
        _lastDownButton = e.Button;
    }

    // Reused context-request args (0 steady-state alloc; filled per invocation — handlers copy what they keep).
    private readonly ContextRequestEventArgs _ctxArgs = new();

    /// <summary>Walk up from <paramref name="node"/> for the first enabled ContextBit handler and invoke it (local coords
    /// + the raising <paramref name="trigger"/> so a keyboard/invoke request anchors to the element rect, pointer/hold at the
    /// point). <paramref name="source"/> is the node the request ORIGINATED at — a <c>ClickRequestsContext</c> button for
    /// an <see cref="ContextRequestTrigger.Invoke"/>, defaulted to <paramref name="node"/> (Source == Node) for the
    /// right-click / keyboard / hold paths whose call sites pass nothing.</summary>
    private bool DispatchContextRequest(NodeHandle node, Point2 abs, ContextRequestTrigger trigger, NodeHandle source = default)
    {
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.ContextBit) == 0) continue;
            var h = _scene.GetContextRequested(n);
            if (h is not null)
            {
                _ctxArgs.Position = LocalPos(n, abs);
                _ctxArgs.Trigger = trigger;
                _ctxArgs.Node = n;
                _ctxArgs.Source = source.IsNull ? n : source;   // Pointer/Keyboard/Hold → Source == Node; Invoke → the button
                h(_ctxArgs);
            }
            return true;
        }
        return false;
    }

    /// <summary>Raise a context request that ORIGINATED at <paramref name="source"/> (input-a11y §6.5.1): a
    /// <c>ClickRequestsContext</c> activation re-enters the funnel here, the walk finds the nearest ancestor
    /// <c>OnContextRequested</c>, and the args carry <see cref="ContextRequestEventArgs.Source"/> = the source so a
    /// rect-anchored open (keyboard/invoke) anchors against the button, not the row. No pointer point — like the Menu
    /// key we anchor to the source node's centre. Zero-alloc (reuses <c>_ctxArgs</c>).</summary>
    private bool RequestContextFrom(NodeHandle source, ContextRequestTrigger trigger)
    {
        if (source.IsNull || !_scene.IsLive(source)) return false;
        var r = _scene.AbsoluteRect(source);
        var centre = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        return DispatchContextRequest(source, centre, trigger, source);
    }

    /// <summary>Commit an ACTIVATION (left-click / touch-tap / Space-Enter) on <paramref name="node"/>: a
    /// <c>ClickRequestsContext</c> node (bit 16) re-enters the context-request funnel via <see cref="RequestContextFrom"/>
    /// with <paramref name="ctxTrigger"/> (<see cref="ContextRequestTrigger.Invoke"/> for pointer/touch, or
    /// <see cref="ContextRequestTrigger.Keyboard"/> for a Space/Enter activation so the first item focuses); every other
    /// clickable fires its click handler as before. The single commit chokepoint for all activation sites.</summary>
    private void InvokeActivation(NodeHandle node, ContextRequestTrigger ctxTrigger)
    {
        if (!node.IsNull && (_scene.Interaction(node).HandlerMask & InteractionInfo.ClickRequestsContextBit) != 0)
            RequestContextFrom(node, ctxTrigger);
        else
            _scene.GetClickHandler(node)?.Invoke();
    }

    /// <summary>Redispatch a context request at a window point — the OverlayHost scrim's dismiss-and-reopen seam
    /// (<c>InputHooks.RedispatchContextAt</c>): hit-test through the (synchronously-unmarked) scrim and fire on the
    /// nearest enabled ContextBit handler under the point, as a <see cref="ContextRequestTrigger.Pointer"/> request.</summary>
    public void RequestContextAt(Point2 p)
    {
        var n = HitTest(p);   // interaction-gated: same walk the right-click press/release path uses
        if (!n.IsNull) DispatchContextRequest(n, p, ContextRequestTrigger.Pointer);
    }

    /// <summary>Middle-button release over the press target: typed pointer args (Button=2) on the nearest enabled
    /// <c>OnPointerPressed</c> in the chain — the WinUI commit-on-release middle-click (TabViewItem.cpp:418-462).</summary>
    private bool DispatchMiddleRelease(NodeHandle node, in InputEvent e)
    {
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.PressedBit) == 0) continue;
            _scene.GetPointerPressed(n)?.Invoke(new PointerEventArgs
            {
                Local = LocalPos(n, e.PositionPx), ClickCount = 1, Mods = e.Mods, Button = 2, Kind = e.Pointer,
            });
            return true;
        }
        return false;
    }

    /// <summary>Element-level wheel routing (WinUI PointerWheelChanged bubbling): every enabled WheelBit handler up the
    /// chain sees the event until one sets Handled — which also stops the enclosing viewport from scrolling.</summary>
    private bool DispatchWheel(in InputEvent e)
    {
        WheelEventArgs? args = null;
        for (var n = HitTestAny(e.PositionPx); !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.WheelBit) == 0) continue;
            args ??= new WheelEventArgs { Delta = e.ScrollDelta, DeltaX = e.ScrollDeltaX, Mods = e.Mods };
            args.Local = LocalPos(n, e.PositionPx);
            _scene.GetPointerWheel(n)?.Invoke(args);
            if (args.Handled) return true;
        }
        return false;
    }

    // ── scrolling (layout-free: write the content's -ScrollOffset transform; never relayout) ──

    /// <summary>The nearest scrollable viewport under the pointer (for revealing its scrollbar on hover and for resolving
    /// the wheel/pan target).</summary>
    public NodeHandle ScrollableUnder(Point2 p)
    {
        for (var n = HitTestAny(p); !n.IsNull; n = _scene.Parent(n))
            if ((_scene.Flags(n) & NodeFlags.Scrollable) != 0) return n;
        return NodeHandle.Null;
    }

    /// <summary>The viewport a pan on a KNOWN axis should drive (window-space point). Mirrors the same-axis climbing of
    /// <see cref="ScrollAxis"/>: the nearest scrollable ancestor whose orientation matches <paramref name="wantHorizontal"/>
    /// AND still has room to move, climbing PAST a cross-axis inner scroller (a horizontal shelf must not eat the vertical
    /// page's pan) and past a same-axis scroller already at full extent. Strictly same-axis — no opposite-axis fallback, so
    /// the rare standalone-opposite-axis carousel is left to the notch fallback path. Null ⇒ caller lets the pan fall
    /// through. Used by the phase-contract consumer (<see cref="OnScrollPhase"/>) to resolve a gesture's target.</summary>
    public NodeHandle ScrollableUnderForAxis(Point2 p, bool wantHorizontal)
    {
        for (var n = HitTestAny(p); !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0 || !_scene.HasScroll(n)) continue;
            ref ScrollState sc = ref _scene.ScrollRef(n);
            if ((sc.Orientation == 1) != wantHorizontal) continue;   // cross-axis → chain past (don't let a shelf eat the pan)
            float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
            float over = wantHorizontal ? sc.ContentW * z - sc.ViewportW : sc.ContentH * z - sc.ViewportH;
            if (over > 0.5f) return n;   // same-axis scroller with room → bind it; else keep climbing to an outer one
        }
        return NodeHandle.Null;
    }

    /// <summary>True while a phase-driven scroll gesture (touchpad/fallback) owns a viewport. Test/diagnostic
    /// observability for the scroll-feel gates.</summary>
    public bool GestureActive => _sgLatched && !_sgTarget.IsNull;

    /// <summary>Transfer scroll ownership from a live phase-driven gesture to a physical mouse/free-spin wheel or the
    /// keyboard (scroll-feel-rework-v2 §2.2: "wheel/keyboard during a contact or fling → CancelGesture() then seed
    /// WheelAnimating"): end the gesture synchronously, clear the resampler latch + every intent it may have recorded, and
    /// snap away any held rubber-band before the wheel handler/viewport runs. One integrator owns the offset at a time; the
    /// wheel starts hard-clamped and clean. Renamed from <c>CancelScrollGestureForWheel</c>; free of the deleted <c>_tp*</c>
    /// state. Zero allocation on the normal and takeover paths.</summary>
    private void CancelGesture()
    {
        NodeHandle target = _sgTarget;
        if (target.IsNull) { _sgLatched = false; _sgMomentum = false; _sgTailConverted = false; return; }
        OnScrollTrackEnd?.Invoke(target);   // clear the resampler latch — no stale contact samples replay under the wheel

        if (_scene.IsLive(target) && _scene.HasScroll(target))
        {
            ref ScrollState sc = ref _scene.ScrollRef(target);
            bool horizontal = sc.Orientation == 1;
            float offset = horizontal ? sc.OffsetX : sc.OffsetY;
            float oldBand = sc.OverscrollPx;

            // Cancel every integrator state that could have been started while the gesture owned this viewport, then
            // establish Offset == Target as the mouse wheel's clean starting point. (One integrator owns the offset at a
            // time — the wheel starts hard-clamped and clean; ScrollBy re-seeds WheelAnimating right after.)
            sc.Phase = ScrollIntegrator.Idle;
            sc.PhaseFlags = 0;
            sc.FlingVelocity = 0f;
            sc.FlingRetargeted = false;
            sc.FlingSnapTarget = float.NaN;
            sc.PendingTargetX = float.NaN;
            sc.PendingTargetY = float.NaN;
            if (horizontal) sc.TargetX = offset; else sc.TargetY = offset;

            // Device crossover is an explicit ownership boundary — snap the visual-only band away now instead of letting
            // its spring compete with the incoming wheel.
            if (FluentGpu.Foundation.ScrollTrace.On) FluentGpu.Foundation.ScrollTrace.WheelCancel(offset, oldBand);
            sc.OverscrollPx = 0f;
            sc.OverscrollVel = 0f;
            sc.Overscrolling = false;
            sc.OverscrollReleaseOmega = 0f;
            if (oldBand != 0f)
            {
                sc.ScrollMoved = true;
                WriteOverscroll(target, 0f);   // re-apply the content transform with band 0 through the tick seam (§2.1)
            }
            sc.IdleMs = 0f;
            OnScrollArmed?.Invoke(target);

            if (FluentGpu.Foundation.ScrollLog.On)
                FluentGpu.Foundation.ScrollLog.Line($"SG->WHEEL handoff off={offset:0.0} band={oldBand:0.0}->0");
        }

        _sgTarget = NodeHandle.Null;
        _sgLatched = false;
        _sgMomentum = false;
        _sgTailConverted = false;
        _sgAccumX = 0f; _sgAccumY = 0f;
    }

    /// <summary>The phase-contract consumer (scroll-feel-rework-v2 §2.1/§6): routes ScrollBegin/Update/End +
    /// MomentumBegin/Update/End into the one latched gesture as INTENT — it records <see cref="ScrollIntegrator.TouchpadTracking"/>
    /// + resampler samples and NEVER moves the offset (the §2.1 single-writer guard fires if it does). Phase 7 resamples +
    /// writes offset+band once. The fallback device seeds one self-fling from the single-window IMPULSE release velocity at
    /// ScrollEnd (≈0 after a completed tail then silence — no double inertia, §4.3).</summary>
    private void OnScrollPhase(in InputEvent e)
    {
        if (FluentGpu.Foundation.ScrollTrace.On)
            FluentGpu.Foundation.ScrollTrace.Phase((byte)e.Kind,
                e.DeviceClassRaw | (_sgLatched ? 1 << 8 : 0) | (_sgMomentum ? 1 << 9 : 0),
                e.ScrollPhaseSeq, e.ScrollDelta, e.ScrollDeltaX, _sgAccumX, _sgAccumY, e.QpcTicks);
        // §2.1 single-writer: the phase-contract path is a pure recorder — nothing here may call ApplyScrollPosition. The
        // chokepoint's positive-in-tick guard (_inPhaseTick, false here) catches any regression automatically; no scoped
        // flag is needed anymore.
        switch (e.Kind)
        {
            case InputKind.ScrollBegin:
                EndScrollGesture(springBand: true, traceReason: 2);   // a producer restart ends any prior gesture cleanly
                _sgDevice = e.DeviceClassRaw;
                _sgAccumX = 0f; _sgAccumY = 0f;
                _sgVel.Reset(default, e.TimestampMs, e.QpcTicks);
                if (FluentGpu.Foundation.ScrollTrace.On)
                    FluentGpu.Foundation.ScrollTrace.VelSample(2, 0f, 0f, 0f, 0f, e.QpcTicks);
                AccumulateContactDelta(in e);
                break;

            case InputKind.ScrollUpdate:
                if (_sgMomentum) { ApplyMomentumDelta(in e); break; }   // producer glitch tolerance: late Update inside momentum
                if (_sgLatched) FeedGestureVelocitySide(e.PointerId);   // pre-coalesce packet stamps (cumulative deltas)
                AccumulateContactDelta(in e);
                if (_sgLatched)
                {
                    _sgVel.Sample(new Point2(_sgAccumX, _sgAccumY), e.TimestampMs, e.QpcTicks);
                    if (FluentGpu.Foundation.ScrollTrace.On)
                        FluentGpu.Foundation.ScrollTrace.VelSample(1, _sgAccumX, _sgAccumY, _sgVel.Vx, _sgVel.Vy, e.QpcTicks);
                }
                break;

            case InputKind.ScrollEnd:
                // Hard lift. Fallback devices seed ONE self-fling from the IMPULSE release velocity over the single 40ms
                // window (the producer stamps ScrollEnd with the LAST PACKET's time, so AssumeStopped evaluates against the
                // true lift — a completed tail then silence reads v≈0, scroll-feel-rework-v2 §4.3). A DManip contact never
                // self-flings: its momentum, when the OS grants one, arrives as Momentum* events. ONE window, ONE gate
                // (|v| ≥ FlingSeedGate) — the v1 trailing-32ms dual-sign gate is deleted with the ImpulseVelocity retune.
                if (_sgLatched && _sgDevice == (byte)ScrollDeviceClass.WheelHiResFallback
                    && _scene.IsLive(_sgTarget) && _scene.HasScroll(_sgTarget))
                {
                    _sgVel.ComputeReleaseVelocity(e.TimestampMs, e.QpcTicks);
                    float v = _sgHoriz ? _sgVel.Vx : _sgVel.Vy;    // synthetic-position slope: + = offset increasing
                    ref ScrollState sc = ref _scene.ScrollRef(_sgTarget);
                    bool seedFling = sc.OverscrollPx == 0f && MathF.Abs(v) >= ScrollTuning.FlingSeedGate;   // one gate: |v| ≥ FlingSeedGate (§4.3)
                    // Nested-scroll hand-off parity with the touch path (ChainFlingTarget): a pan whose past-edge
                    // excess chained to an outer scroller throws the OUTER at lift — the latched inner is parked at
                    // its edge, and flinging it would convert the residual velocity into a spurious edge bounce.
                    NodeHandle flingTarget = (!_chainOuter.IsNull && _scene.IsLive(_chainOuter) && _scene.HasScroll(_chainOuter))
                        ? _chainOuter : _sgTarget;
                    if (FluentGpu.Foundation.ScrollTrace.On)
                        FluentGpu.Foundation.ScrollTrace.Release(
                            (_sgHoriz ? 1 : 0) | (seedFling ? 2 : 0) | (flingTarget != _sgTarget ? 4 : 0),
                            _sgVel.Vx, _sgVel.Vy, v, sc.OverscrollPx, e.QpcTicks, 0f);
                    if (seedFling)
                    {
                        if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"SG  lift fling seed v={v:0} dev={_sgDevice}");
                        OnFlingStarted?.Invoke(flingTarget, v);   // → SeedScrollFling: Phase = Fling (integrator coasts at phase 7)
                    }
                }
                EndScrollGesture(springBand: true, traceReason: 0);
                break;

            case InputKind.MomentumBegin:
                _sgMomentum = true;   // the target/axis stay latched; deltas keep applying 1:1 (OS-owned inertia)
                // Tag the latched target OS-owned so the integrator can end the tail at an edge: an OS INERTIA decay
                // runs up to ~2s, and riding it 1:1 into the rubber band holds the stretch frozen (no fingers!) until
                // MomentumEnd. The tick converts the tail's velocity into an immediate SnapBack bounce instead (§2.2).
                if (_sgLatched && _scene.IsLive(_sgTarget) && _scene.HasScroll(_sgTarget))
                    _scene.ScrollRef(_sgTarget).PhaseFlags |= ScrollState.PhaseOsOwned;
                break;

            case InputKind.MomentumUpdate:
                ApplyMomentumDelta(in e);
                break;

            case InputKind.MomentumEnd:
                EndScrollGesture(springBand: true, traceReason: 1);
                break;
        }
    }

    /// <summary>[Conditional("DEBUG")] single-writer guard (scroll-feel-rework-v2 §2.1/§8 gate 1): the offset chokepoint
    /// <see cref="ApplyScrollPosition"/> asserts it is reached ONLY from the phase-7-tick write seams
    /// (<see cref="WriteScrollOffset"/>/<see cref="WriteOverscroll"/>, which raise <see cref="_inPhaseTick"/>). This pins R1
    /// structurally: ANY synchronous phase-2 write — a touch-pan / scrollbar-drag / pinch / edge-auto-scroll / phase-recorder
    /// caller reaching the chokepoint off a non-tick stack — trips it (the demoted recorders now RECORD intents; the sole
    /// writer is the integrator tick). Stronger than the old OnScrollPhase-only scope. Erased from Release/Ship.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertInPhaseTick()
        => System.Diagnostics.Debug.Assert(_inPhaseTick,
            "scroll offset written outside the phase-7 integrator tick — every offset/band write must route through WriteScrollOffset/WriteOverscroll (v2 §2.1 single-writer)");

    /// <summary>Accumulate a contact delta; latch axis+target once the accumulated travel crosses the
    /// <see cref="PanSlopPx"/> window (first-delta latching mislatches diagonal starts), then RECORD it (scroll-feel-
    /// rework-v2 §2.1/§6): the dispatcher is a pure intent recorder — it never moves the offset synchronously. On latch it
    /// records <see cref="ScrollIntegrator.TouchpadTracking"/> (via <see cref="OnScrollTrackBegin"/>, folding any live band
    /// into the anchor so the resampler reproduces it) and thereafter appends each cumulative-axis contact sample to the
    /// integrator resampler (<see cref="OnScrollTrackSample"/>); phase 7 resamples to frame time and writes offset+band
    /// ONCE (§4.1). The old <c>ApplyTouchPan</c> synchronous apply is GONE.</summary>
    private void AccumulateContactDelta(in InputEvent e)
    {
        _sgAccumX += e.ScrollDeltaX;
        _sgAccumY += e.ScrollDelta;
        if (!_sgLatched)
        {
            if (MathF.Abs(_sgAccumX) + MathF.Abs(_sgAccumY) < PanSlopPx) return;
            bool horiz = MathF.Abs(_sgAccumX) > MathF.Abs(_sgAccumY);
            NodeHandle vp = ScrollableUnderForAxis(e.PositionPx, horiz);
            if (vp.IsNull)
            {
                vp = ScrollableUnderForAxis(e.PositionPx, !horiz);   // dominant axis has no scroller → cross-axis fallback (standalone carousel)
                if (vp.IsNull) return;
                horiz = !horiz;
            }
            _sgTarget = vp; _sgHoriz = horiz; _sgLatched = true;

            ref ScrollState s0 = ref _scene.ScrollRef(vp);
            // A new contact ZEROES any in-flight momentum on the latched target (§2.2: contact zeroes momentum, never adds).
            s0.PhaseFlags = 0; s0.FlingVelocity = 0f;
            s0.FlingRetargeted = false; s0.FlingSnapTarget = float.NaN;
            s0.PendingTargetX = float.NaN; s0.PendingTargetY = float.NaN;
            _sgAnchorOffset = horiz ? s0.OffsetX : s0.OffsetY;
            // A gesture landing over a still-easing band continues the stretch seamlessly (no one-frame erase): fold the
            // live band back into the anchor as excess so the resampler's rawOffset = anchor + Σδ reproduces it (§2.4).
            float viewport = horiz ? s0.ViewportW : s0.ViewportH;
            _sgAnchorOffset += OverscrollPhysics.ExcessFromBand(s0.OverscrollPx, viewport);
            s0.Overscrolling = s0.OverscrollPx != 0f;   // the gesture owns the band again (no spring while held)
            s0.OverscrollVel = 0f;
            // Record TouchpadTracking intent + latch the resampler anchor (this sets Phase = TouchpadTracking + arms).
            OnScrollTrackBegin?.Invoke(vp, horiz, _sgAnchorOffset);
            _sgVel.Reset(new Point2(_sgAccumX, _sgAccumY), e.TimestampMs, e.QpcTicks);
            if (FluentGpu.Foundation.ScrollTrace.On)
            {
                FluentGpu.Foundation.ScrollTrace.Latch((int)vp.Raw.Index, _sgDevice | (horiz ? 1 << 8 : 0),
                    _sgAnchorOffset, _sgAccumX, _sgAccumY);
                FluentGpu.Foundation.ScrollTrace.VelSample(2, _sgAccumX, _sgAccumY, 0f, 0f, e.QpcTicks);
            }
            if (FluentGpu.Foundation.ScrollLog.On)
                FluentGpu.Foundation.ScrollLog.Line($"SG  latch {(horiz ? "x" : "y")} dev={_sgDevice} anchor={_sgAnchorOffset:0.0}");
        }
        RecordGestureSample(in e);
    }

    /// <summary>An OS-momentum delta (DManip <c>INERTIA</c>): recorded verbatim through the SAME resampler as contact —
    /// the OS computed the inertia, the engine applies it 1:1 at phase 7 (scroll-feel-rework-v2 §4.3 "OS INERTIA deltas
    /// apply verbatim through the resampled 1:1 path"). Never feeds the release estimator; requires a latched gesture
    /// (momentum with no prior contact is dropped). Keeps <see cref="ScrollIntegrator.TouchpadTracking"/> so
    /// <c>UserScrollActive</c> stays true through the OS coast (the DoF gate).</summary>
    private void ApplyMomentumDelta(in InputEvent e)
    {
        if (!_sgLatched) return;
        // §A.3 ignore-the-rest-of-the-tail: the phase-7 tick converts an OS-momentum tail to a SnapBack bounce the frame
        // it reaches the edge (Phase→SnapBack, no fingers down). Every later MomentumUpdate of this stream is dropped —
        // depositing it into the resampler goes nowhere (SnapBack ignores contact samples) and re-arming is wasted work.
        // Interior momentum never sets Phase=SnapBack, so this only latches on a genuine edge conversion.
        if (_sgTailConverted) return;
        if (_scene.IsLive(_sgTarget) && _scene.HasScroll(_sgTarget)
            && _scene.ScrollRef(_sgTarget).Phase == ScrollIntegrator.SnapBack)
        {
            _sgTailConverted = true;   // edge→SnapBack happened last tick — swallow the remaining tail (m_ignoreMomentumScrolls)
            return;
        }
        _sgAccumX += e.ScrollDeltaX;
        _sgAccumY += e.ScrollDelta;
        RecordGestureSample(in e);
    }

    /// <summary>Feed this frame's pre-coalesce scroll-phase samples into the gesture estimator. Scroll-phase side
    /// samples carry CUMULATIVE deltas (the ring coalescer pushes the running sum before each overwrite), so the
    /// synthetic position is the pre-event accumulator base + the sample. Idempotent (monotonic-stamp guard).</summary>
    private void FeedGestureVelocitySide(uint pointerId)
    {
        float baseX = _sgAccumX, baseY = _sgAccumY;
        for (int i = 0; i < _velSampleCount; i++)
        {
            ref readonly PointerVelSample s = ref _velSamples[i];
            if (s.PointerId != pointerId) continue;
            _sgVel.Sample(new Point2(baseX + s.X, baseY + s.Y), s.TimestampMs, s.QpcTicks);
            if (FluentGpu.Foundation.ScrollTrace.On)
                FluentGpu.Foundation.ScrollTrace.VelSample(0, baseX + s.X, baseY + s.Y, _sgVel.Vx, _sgVel.Vy, s.QpcTicks);
        }
    }

    /// <summary>Record one contact sample into the integrator resampler (scroll-feel-rework-v2 §2.3/§4.1): the cumulative
    /// axis position (offset space) at this packet's timestamp. NEVER moves the offset — phase 7 resamples the deposited
    /// samples to <c>frameT − 5ms</c> and writes offset+band once. Keeps the node armed so the tick runs.</summary>
    private void RecordGestureSample(in InputEvent e)
    {
        if (!_sgLatched) return;
        if (!_scene.IsLive(_sgTarget) || !_scene.HasScroll(_sgTarget))
        {
            OnScrollTrackEnd?.Invoke(_sgTarget);
            _sgTarget = NodeHandle.Null; _sgLatched = false; return;
        }
        float axisAccum = _sgHoriz ? _sgAccumX : _sgAccumY;   // cumulative offset-space position (rawOffset = anchor + this)
        OnScrollTrackSample?.Invoke((int)_sgTarget.Raw.Index, ContactSampleSec(e.TimestampMs, e.QpcTicks), axisAccum);
        OnScrollArmed?.Invoke(_sgTarget);   // stay armed (BeginTracking armed it; re-arm each packet is idempotent)
    }

    /// <summary>Seconds on the sample clock feeding the integrator resampler: QPC when the platform provides it (sub-ms),
    /// else the ms message time (headless → monotonic frame-driven stamps), else 0 (a truly vacuous stamp). Mirrors the
    /// <see cref="ImpulseVelocity"/> clock so the resampler and the release estimator agree.</summary>
    private static double ContactSampleSec(uint ms, long qpc)
    {
        long f = SystemParams.QpcFrequency;
        if (qpc != 0 && f > 0) return qpc / (double)f;
        return ms != 0 ? ms / 1000.0 : 0.0;
    }

    /// <summary>End the latched gesture: optionally hand a held band to the phase-7 spring-back (lift/momentum-end/
    /// cancel), then unlatch. Cancel semantics == end-with-zero-velocity (checklist #16). <paramref name="traceReason"/>
    /// is diagnostic-only (ScrollTrace gestureEnd i0: 0=ScrollEnd 1=MomentumEnd 2=restart-on-Begin 3=wheel 4=cancel).</summary>
    private void EndScrollGesture(bool springBand, int traceReason = 4)
    {
        if (FluentGpu.Foundation.ScrollTrace.On && _sgLatched)
        {
            float traceBand = (_scene.IsLive(_sgTarget) && _scene.HasScroll(_sgTarget)) ? _scene.ScrollRef(_sgTarget).OverscrollPx : 0f;
            FluentGpu.Foundation.ScrollTrace.GestureEnd(traceReason, _sgMomentum ? 1 : 0, traceBand);
        }
        if (_sgLatched && _scene.IsLive(_sgTarget) && _scene.HasScroll(_sgTarget))
        {
            ref ScrollState sc = ref _scene.ScrollRef(_sgTarget);
            OnScrollTrackEnd?.Invoke(_sgTarget);   // clear the resampler latch (no more contact samples consumed)
            // The OS-owned tag belongs to THIS gesture only — a residue would make a later engine fling on this node
            // skip its own coast (the integrator's Fling branch is OsOwned-gated).
            sc.PhaseFlags &= unchecked((byte)~ScrollState.PhaseOsOwned);
            if (springBand && sc.OverscrollPx != 0f
                && (sc.Phase == ScrollIntegrator.TouchpadTracking || sc.Phase == ScrollIntegrator.Overscroll))
            {
                // Only a band still HELD by the gesture is released here. A SnapBack already in flight (the integrator
                // bounced an OS-momentum tail off the edge before MomentumEnd arrived) keeps its spring state — zeroing
                // OverscrollVel mid-bounce would visibly restart the spring.
                if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"SG  end band={sc.OverscrollPx:0.0} -> spring back");
                sc.Overscrolling = false;   // release the band to the phase-7 SnapBack spring-back
                sc.OverscrollVel = 0f;
                sc.Phase = ScrollIntegrator.SnapBack;
                OnScrollArmed?.Invoke(_sgTarget);
            }
            // No held band and no seeded Fling (ScrollEnd seeds Fling BEFORE calling here): the tracking gesture just
            // settles. Leave a freshly-seeded Fling/WheelAnimating untouched — only demote a still-live TouchpadTracking.
            else if (sc.Phase == ScrollIntegrator.TouchpadTracking || sc.Phase == ScrollIntegrator.Overscroll)
                sc.Phase = ScrollIntegrator.Idle;
        }
        // A chained outer belongs to THIS gesture (the touch path consumes it via ChainFlingTarget; the phase path's
        // ScrollEnd reads it before calling here) — never leak the latch into the next gesture.
        if (_sgLatched) _chainOuter = NodeHandle.Null;
        _sgTarget = NodeHandle.Null;
        _sgLatched = false;
        _sgMomentum = false;
        _sgTailConverted = false;   // §A.3 tail-ignore latch is per-gesture
        _sgAccumX = 0f; _sgAccumY = 0f;
    }

    // (PanTouchpad — the packet accumulator of the deleted second integrator — is REPLACED by the phase-contract
    // consumer above: producers emit ScrollBegin/Update/End (+ Momentum*) and OnScrollPhase RECORDS them as
    // TouchpadTracking intent + resampler samples — the dispatcher never moves the offset (scroll-feel-rework-v2 §2.1).)

    // (TickTouchpad — the 14ms low-pass + velocity-enveloped-band frame advance of the deleted second integrator — is
    // GONE: contact/momentum deltas are RECORDED at dispatch (intent columns + resampler samples); the phase-7
    // ScrollIntegrator.Tick resamples to frame time and writes offset+band ONCE — the single writer, §4.1/§4.4.)

    /// <summary>Nearest self-or-ancestor viewport under <paramref name="p"/> whose <see cref="ScrollState.Zoomable"/> opt-in
    /// is set (Phase-4 pinch-zoom), or null. A zoomable viewport is always Scrollable; this walks PAST a non-zoomable inner
    /// scroller to an enclosing zoomable one, so a pinch targets the declared zoom viewport.</summary>
    private NodeHandle ZoomableUnder(Point2 p)
    {
        for (var n = HitTestAny(p); !n.IsNull; n = _scene.Parent(n))
            if ((_scene.Flags(n) & NodeFlags.Scrollable) != 0 && _scene.HasScroll(n) && _scene.ScrollRef(n).Zoomable) return n;
        return NodeHandle.Null;
    }

    private void UpdateScrollHover(Point2 p)
    {
        if (OnScrollHover is null && OnScrollLeave is null) return;

        var next = ScrollableUnder(p);
        if (next != _scrollHovered)
        {
            if (!_scrollHovered.IsNull && _scene.IsLive(_scrollHovered))
                OnScrollLeave?.Invoke(_scrollHovered);
            _scrollHovered = next;
        }

        if (!next.IsNull)
            OnScrollHover?.Invoke(next, PointerInScrollbarLane(next, p));
    }

    private bool PointerInScrollbarLane(NodeHandle n, Point2 p)
    {
        if (!TryGetScrollbarMetrics(n, out var m)) return false;
        var local = new Point2(p.X - m.Bounds.X, p.Y - m.Bounds.Y);
        return InScrollbarLane(local, in m);
    }

    /// <summary>Stuck-hover fix (input-a11y.md §5.4/§15): a phase-7 scroll offset write moved content under a possibly
    /// STATIONARY mouse/pen cursor, so synthesize the hover re-resolve a real <see cref="InputKind.PointerMove"/> would
    /// have done — HitTest at the last known pointer position and drive the SAME _hovered / scroll-hover / cursor path.
    /// This is the sanctioned per-frame exception to §14's one-hit-test-per-event budget (it is offset-changed-gated,
    /// not per-event). Mouse/pen only — a touch pan (_panClaimed) and an item-drag capture (Drag.IsActive) deliberately
    /// suppress hover, and an unknown/off-window position (invalidated on WindowBlur) is skipped. Called by AppHost AFTER
    /// the phase-7 scroll tick + the virtual re-realize catch-up, so the hit-test sees the finalized realized/transformed
    /// content. Zero managed allocation — scalar walks through the existing hover chokepoints + cached delegate fields.</summary>
    internal void RefreshHoverAfterScroll()
    {
        if (!_lastPointerValid || _lastPointerKind == PointerKind.Touch || _panClaimed || Drag.IsActive) return;
        NodeHandle before = _hovered;
        NodeHandle next = HitTest(_lastPointerPx);
        // Recycled virtual-list slot: a boundary-crossing scroll rebinds the slot HANDLE still under the cursor to a new
        // item, and the reconciler's rebind Unmark (Reconciler.cs) cleared this node's Hovered SCENE flag while _hovered
        // still points AT it. SetState below early-outs on slot==next and would leave the row painted un-hovered until the
        // next real PointerMove (the stuck-hover symptom, virtual-list variant) — so re-assert the bit here BEFORE the
        // early-out. The recorder reads NodeFlags.Hovered directly (SceneRecorder.cs) and re-records after this refresh; a
        // no-op when the flag survived (normal in-window scroll). Only the exact _hovered handle needs it — a changed hit
        // goes through SetState's full enter transition, which sets the flag itself.
        if (next == _hovered && !next.IsNull && _scene.IsLive(next))
            _scene.Flags(next) |= NodeFlags.Hovered;
        // The hover resolve + enter/leave + HoverWithin diff + cursor publish all ride through this single SetState (it
        // early-outs when the node under the point is unchanged — no redundant OnHoverChanged / cursor churn, §3-gate).
        SetState(ref _hovered, next, NodeFlags.Hovered);
        // A same-text-node re-resolve over a Hyperlink still needs the per-span cursor refresh — the span boundary moved
        // under the cursor even though the text node didn't change (mirrors the PointerMove path at ~855-857).
        if (!_hovered.IsNull && _scene.IsLive(_hovered)
            && (_scene.Interaction(_hovered).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
            UpdateSpanCursor(_hovered, _lastPointerPx);
        UpdateScrollHover(_lastPointerPx);   // scrollbar-reveal target follows the content (early-outs with no subscribers)
        // Bare-hover preview (OnHoverMove) ONLY when the hovered node actually CHANGED this refresh — never re-fire it for
        // an unchanged target (SetState's early-out already covers the flag/enter/leave).
        if (before != _hovered && !_hovered.IsNull && _scene.GetHoverMove(_hovered) is { } hm)
            hm(LocalPos(_hovered, _lastPointerPx));
    }

    /// <summary>Layout-move companion to <see cref="RefreshHoverAfterScroll"/> (input-a11y.md §5.4/§15/§1056: hover re-resolves
    /// "when content moves under a stationary pointer, NOT just layout commits"). A reconcile/relayout can TRANSLATE a node out
    /// from under a STATIONARY mouse/pen cursor with no <see cref="InputKind.PointerMove"/> to re-resolve it — the sidebar
    /// collapse snapping its 240→56 rail while carrying the hover-only resize grip is the canonical instance (the grip keeps
    /// <see cref="NodeFlags.Hovered"/>, so its fade-in seam hairline stays lit until the next real move). Unlike the scroll
    /// path — which is offset-write-gated (perfectly correlated with a scroll-hover update) — a relayout is NOT correlated
    /// with the hovered node moving, so this fires the full re-resolve ONLY when re-hit-testing at the stationary pointer
    /// yields a DIFFERENT node than the current hover. That early-out is load-bearing: a bare per-relayout re-resolve would
    /// re-drive the unconditional scroll-hover reveal + hover anim edges on EVERY reconcile (perturbing frame-exact controls
    /// — the ToolTip show/dismiss clock, the ToggleSwitch knob hover-grow), whereas a genuine stuck-hover ALWAYS changes the
    /// hit. Mouse/pen only — a touch pan (_panClaimed) and an item-drag capture (Drag.IsActive) deliberately suppress hover,
    /// and an unknown/off-window position is skipped. Zero managed allocation: one hit-test through the existing chokepoints.</summary>
    internal void RefreshHoverAfterLayoutMove()
    {
        if (!_lastPointerValid || _lastPointerKind == PointerKind.Touch || _panClaimed || Drag.IsActive) return;
        // Un-stick ONLY: this path exists to CLEAR/relocate a hover the layout stranded, never to SYNTHESIZE one. When
        // nothing is hovered — including a scrub/press that deliberately nulled hover — a moved node sliding under the
        // still cursor must NOT fabricate a hover-enter here (that spuriously re-drove the ToggleSwitch knob hover-grow
        // every reconcile). A genuine enter still rides the next real PointerMove; the offset-write-correlated scroll path
        // is the one that may seed from null. Nothing hovered ⇒ nothing to un-stick.
        if (_hovered.IsNull || !_scene.IsLive(_hovered)) return;
        // Fire ONLY when the HOVERED node itself translated out from under the still cursor — i.e. the cursor is no longer
        // within its (post-transform) absolute bounds. This is the precise "content moved under a stationary pointer"
        // edge: a NEW node merely APPEARING over the cursor (a ToolTip bubble / flyout opening at the pointer) leaves the
        // hovered node's rect still containing the cursor, so its dwell hover is NOT churned; only a genuine translate-away
        // (the sidebar-collapse rail carrying its hover-only grip to x=56 while the cursor stays put) fails containment.
        // Cheap: one AbsoluteRect walk + an AABB test — the hit-test only runs on the rare frame the hovered node moved.
        RectF r = _scene.AbsoluteRect(_hovered);
        if (_lastPointerPx.X >= r.X && _lastPointerPx.X < r.X + r.W &&
            _lastPointerPx.Y >= r.Y && _lastPointerPx.Y < r.Y + r.H) return;
        // The hovered node moved off the cursor: drive the SAME resolve + enter/leave + HoverWithin diff + cursor publish
        // the scroll refresh does, through the one SetState chokepoint (the cursor is genuinely over `next` now).
        NodeHandle before = _hovered;
        NodeHandle next = HitTest(_lastPointerPx);
        SetState(ref _hovered, next, NodeFlags.Hovered);
        if (!_hovered.IsNull && _scene.IsLive(_hovered)
            && (_scene.Interaction(_hovered).HandlerMask & InteractionInfo.SpanLinksBit) != 0)
            UpdateSpanCursor(_hovered, _lastPointerPx);
        UpdateScrollHover(_lastPointerPx);   // scrollbar-reveal target follows the moved content (early-outs with no subscribers)
        // Bare-hover preview (OnHoverMove) only when the hovered node actually CHANGED this refresh.
        if (before != _hovered && !_hovered.IsNull && _scene.GetHoverMove(_hovered) is { } hm)
            hm(LocalPos(_hovered, _lastPointerPx));
    }

    /// <summary>Diagnostic only: drive the wheel-routing path (hit-test → nearest vertical scroller) directly, bypassing the
    /// input ring — lets a harness isolate routing from the OS-pump/injection path. Returns true iff a scroller consumed it.</summary>
    public bool DiagScrollAt(Point2 p, float deltaY) => ScrollAt(p, deltaY, 0f);
    /// <summary>Diagnostic only: the topmost hit-test node at a point (the same walk wheel/click routing starts from).</summary>
    public NodeHandle DiagHitTest(Point2 p) => HitTestAny(p);

    private bool ScrollAt(Point2 p, float deltaY, float deltaX, bool isNotch = false, uint timestampMs = 0)
    {
        var node = HitTestAny(p);
        bool any = false;
        // The two wheel axes route INDEPENDENTLY to a scroller of their OWN orientation, so a horizontal swipe never
        // scrolls vertically and a vertical wheel never scrolls horizontally.
        // • Vertical wheel → nearest VERTICAL scroller, climbing PAST horizontal-only viewports (a horizontal code box /
        //   shelf nested in a vertical page must not eat the page's wheel — WinUI semantics); a horizontal scroller is a
        //   FALLBACK so a STANDALONE horizontal carousel still wheel-scrolls.
        // • Horizontal wheel/two-finger swipe → nearest HORIZONTAL scroller ONLY (no vertical fallback — a horizontal
        //   swipe must never scroll the page vertically, the symptom this fix removes).
        if (deltaY != 0f) any |= ScrollAxis(node, deltaY, wantHorizontal: false, oppositeFallback: true, isNotch, timestampMs);
        if (deltaX != 0f) any |= ScrollAxis(node, deltaX, wantHorizontal: true, oppositeFallback: false, isNotch, timestampMs);
        if (any) OnScrollStartedObserved?.Invoke();
        return any;
    }

    /// <summary>Diagnostic: FG_OFFSET_JUMP=1 logs (to stderr) any single offset write that jumps the viewport > 60px — used
    /// to localise the 1-frame top-edge "another viewport" flash to its writer (input/glide/programmatic). Fires only on a
    /// jump, so it's near-free and doesn't perturb the frame loop the way the per-event FG_SCROLL_LOG does.</summary>
    private static readonly bool s_offsetJumpLog = System.Environment.GetEnvironmentVariable("FG_OFFSET_JUMP") == "1";

    private bool ScrollAxis(NodeHandle node, float delta, bool wantHorizontal, bool oppositeFallback, bool isNotch = false,
                            uint timestampMs = 0)
    {
        NodeHandle fallback = NodeHandle.Null;
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0) continue;
            bool horiz = _scene.ScrollRef(n).Orientation == 1;
            if (horiz != wantHorizontal)
            {
                if (oppositeFallback && fallback.IsNull) fallback = n;   // opposite-axis scroller remembered as a fallback
                continue;
            }
            if (TryScrollNode(n, delta, isNotch, timestampMs)) return true;   // a same-axis scroller consumed it (else climb on)
        }
        if (oppositeFallback && !fallback.IsNull && TryScrollNode(fallback, delta, isNotch, timestampMs)) return true;
        return false;
    }

    private bool TryScrollNode(NodeHandle n, float delta, bool isNotch = false, uint timestampMs = 0)
    {
        return ScrollBy(n, delta, SmoothScroll, isNotch, timestampMs);
    }

    private bool ScrollBy(NodeHandle n, float delta, bool smooth, bool isNotch = false, uint timestampMs = 0)
    {
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        // A device-NOTCH wheel amount → DIP: max(48, 10%·viewport) per notch (content-relative line height,
        // ScrollTuning.PerNotchDip). A DIP ScrollDelta (synthetic/test/programmatic, isNotch=false) is used directly.
        if (isNotch) delta *= Tuning.PerNotchDip(horizontal ? sc.ViewportW : sc.ViewportH);
        // Clamp against the SCALED content extent (Content*Zoom − Viewport), identical to SetScrollOffset/ApplyTouchPan: the
        // smooth (wheel + scrollbar-track-click) Target write below clamps directly here and never reaches SetScrollOffset,
        // so it must use the same scaled max or a zoomed-in viewport can't wheel to the far edge / a zoomed-out one drives
        // the eased Offset past the content. ZoomFactor is 1 on every non-zoom viewport (ScrollState.Default), so this is
        // byte-identical to the old `Content − Viewport` there.
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float max = horizontal ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW) : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);

        // Discrete mouse/free-spin wheel path (scroll-feel-rework-v2 §4.2): a notch does NOT fling. It advances an
        // accumulated, hard-clamped PendingTarget which the integrator chases with a velocity-preserving critically-damped
        // spring (WheelAnimating) — velocity carries across a re-target, so rapid notches accumulate smoothly with no
        // restart jerk, and the hard clamp gives a free hard-stop at the extents (never a rubber-band). Supersedes v1's
        // WheelFlingMode (v·dt Riemann coast) and its WheelFlingDecayPerS / velocity-cap knobs.
        if (smooth)
        {
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            bool atEdge = (delta < 0f && off <= 0.5f) || (delta > 0f && off >= max - 0.5f);
            if (max <= 0f || atEdge)
            {
                if (FluentGpu.Foundation.ScrollTrace.On)
                    FluentGpu.Foundation.ScrollTrace.WheelSeed((int)n.Raw.Index, 2, delta, 0f, off);
                return false;   // nothing to scroll / pinned at this edge → bubble to an outer scroller
            }
            // Continue an in-flight wheel chase (carry velocity across the re-target); else start fresh from the offset.
            bool sameWheel = sc.Phase == ScrollIntegrator.WheelAnimating && (sc.PhaseFlags & ScrollState.PhaseProgrammatic) == 0;
            float curPending = horizontal ? sc.PendingTargetX : sc.PendingTargetY;
            float basePending = (sameWheel && !float.IsNaN(curPending)) ? curPending : off;
            float newPending = Math.Clamp(basePending + delta, 0f, max);   // §4.2: hard-stops at the extents
            if (!sameWheel) sc.FlingVelocity = 0f;   // fresh chase from rest; a same-direction continuation carries velocity
            if (horizontal) sc.PendingTargetX = newPending; else sc.PendingTargetY = newPending;
            sc.Phase = ScrollIntegrator.WheelAnimating;
            sc.PhaseFlags = ScrollState.PhaseWheel;   // a mouse notch: hard-stops at extents, never bands
            sc.FlingRetargeted = false;
            sc.FlingSnapTarget = float.NaN;
            sc.IdleMs = 0f;
            OnScrollArmed?.Invoke(n);
            if (FluentGpu.Foundation.ScrollTrace.On)
                FluentGpu.Foundation.ScrollTrace.WheelSeed((int)n.Raw.Index, sameWheel ? 1 : 0, delta, newPending, off);
            if (FluentGpu.Foundation.ScrollLog.On)
                FluentGpu.Foundation.ScrollLog.Line($"  scrollBy WHEEL-CHASE {(horizontal ? "x" : "y")} delta={delta:0.0} pending={newPending:0} off={off:0}");
            return true;
        }

        float old = horizontal ? sc.OffsetX : sc.OffsetY;
        // Non-smooth (SmoothScroll=false, the deterministic test default) jump: route through the integrator's write seam
        // so the offset chokepoint runs under the phase-7-tick guard (§2.1/§8) — the sole path to ApplyScrollPosition.
        bool movedDirect = WriteScrollOffset(n, old + delta);
        if (FluentGpu.Foundation.ScrollLog.On)
            FluentGpu.Foundation.ScrollLog.Line($"  scrollBy DIRECT {(horizontal ? "x" : "y")} delta={delta:0.0} off={old:0}->{(horizontal ? sc.OffsetX : sc.OffsetY):0}");
        return movedDirect;
    }

    private bool SetScrollOffset(NodeHandle n, float offset)
    {
        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.RestorePending = false;   // an explicit scroll (user pan/wheel/thumb-drag, fling, programmatic) cancels a pending restore
        bool horizontal = sc.Orientation == 1;
        // The offset clamps against the SCALED content extent (Content*Zoom − Viewport, WinUI ScrollPresenter), so a
        // zoomed-in viewport can pan across the full magnified content. ZoomFactor is 1 for every non-zoom viewport, so
        // this is identical to the old `Content − Viewport` on the unchanged path (and the clamp contract is never
        // relaxed — wheel/keyboard/programmatic offsets stay hard-clamped to this scaled max).
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float max = horizontal ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW) : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        float old = horizontal ? sc.OffsetX : sc.OffsetY;
        float next = Math.Clamp(offset, 0f, max);
        if (s_offsetJumpLog && MathF.Abs(next - old) > 60f)
            System.Console.Error.WriteLine($"[OFFSET-JUMP] {old:0}->{next:0} req={offset:0} max={max:0} phase={sc.Phase} content={(horizontal ? sc.ContentW : sc.ContentH):0} vp={(horizontal ? sc.ViewportW : sc.ViewportH):0}");
        float target = horizontal ? sc.TargetX : sc.TargetY;
        if (next == old && target == next) return false;
        if (horizontal) { sc.OffsetX = next; sc.TargetX = next; }
        else { sc.OffsetY = next; sc.TargetY = next; }
        sc.IdleMs = 0f;
        // A SYNCHRONOUS offset move (touch content-pan, scrollbar thumb-drag, drag-edge auto-scroll, non-smooth wheel) sets
        // Offset == Target, so the ScrollIntegrator can't infer motion from |Target − Offset|. Pulse ScrollMoved so the next
        // Tick reveals the thin indicator for this frame (the WinUI TouchIndicator that shows THROUGHOUT a manipulation, not
        // only the post-lift fling) — FadeT only, never PointerOver/ExpandT, so the bar idle-hides once the move stops.
        if (next != old) sc.ScrollMoved = true;
        ApplyScrollPosition(n, ref sc, horizontal, next);
        // §8: record exactly one offset write per real move, carrying the §2.2 Phase + the writer id (the phase-7 tick
        // sets Integrator via WriteScrollOffset; other synchronous callers stay Direct). Feeds the single-writer audit.
        FluentGpu.Foundation.ScrollTrace.OffsetWrite((int)n.Raw.Index, sc.Phase, _scrollWriter, next);
        OnScrollArmed?.Invoke(n);
        return true;
    }

    // (ApplyTouchPan + OuterScroller — the synchronous direct-touch offset+band writer and its nested-scroll ancestor
    // walk — are DELETED (scroll-feel-rework-v2 §2.1/§6): touch pan is now a pure intent recorder (BeginTouchPanTracking +
    // RecordTouchPanSample deposit resampler samples; the phase-7 ScrollIntegrator.TouchpadTracking branch resamples them
    // and writes offset+band ONCE — the single writer). DRAG-TIME nested-scroll chaining (routing past-edge excess to an
    // outer scroller mid-drag) is not yet reproduced in the integrator, matching the PTP path; the LIFT-time chain hand-off
    // survives via ChainFlingTarget/_chainOuter. See openIssues.)

    /// <summary>Nested-scroll fling hand-off (§10): if the pan was chaining to an outer scroller at lift, the flick throws
    /// the OUTER (the inner is parked at its edge), Compose-style. Consumes (clears) the chain latch.</summary>
    private NodeHandle ChainFlingTarget()
    {
        var t = (!_chainOuter.IsNull && _scene.IsLive(_chainOuter)) ? _chainOuter : _panTarget;
        _chainOuter = NodeHandle.Null;
        return t;
    }

    // Overscroll resistance + spring: OverscrollPhysics (WinUI DM + IT parity).
    private void ApplyScrollPosition(NodeHandle n, ref ScrollState sc, bool horizontal, float next)
    {
        AssertInPhaseTick();   // §2.1 single-writer: reachable ONLY from the phase-7-tick write seams (DEBUG-only)
        // Layout-free scroll/zoom: the content child's LocalTransform carries BOTH the -ScrollOffset translation and (when
        // the viewport is zoomed or overpanning) scale — TransformDirty | PaintDirty only, never LayoutDirty.
        var content = sc.ContentNode;
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            float maxOff = horizontal ? MathF.Max(0f, sc.ContentW * (sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f) - sc.ViewportW)
                                      : MathF.Max(0f, sc.ContentH * (sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f) - sc.ViewportH);
            float band = OverscrollPhysics.GuardBandSign(sc.OverscrollPx, next, maxOff);
            // During an active pull the guard may zero a wrong-sign band at the clamp; during spring-back only the
            // transform is guarded — mutating OverscrollPx here would fight phase-7 StepSpring.
            if (sc.Overscrolling && band != sc.OverscrollPx) sc.OverscrollPx = band;
            OverscrollPhysics.WriteContentTransform(ref cp, in _scene.Bounds(content), horizontal, next, band,
                sc.ZoomFactor, _scene.DeviceScale);
            _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            ScrollBindEval.ApplyContinuous(_scene, n, ref sc);   // generic scroll-driven bindings (stretch/parallax/fade/…)
        }

        // Virtualization: keep transform-only scroll while the visible band remains inside the realized guard band. When
        // zoomed, the on-screen viewport band maps back to a SMALLER content-space band ([off/z, (off+vp)/z]) — the item
        // extents are in unscaled content units — so the realize window tracks the magnified content correctly.
        if (sc.ItemCount > 0)
        {
            int visibleFirst, visibleLast;
            float zv = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
            float vp = (horizontal ? sc.ViewportW : sc.ViewportH) / zv;
            float contentNext = next / zv;
            if (sc.Layout is not null)   // fixed-geometry (stack/grid/custom)
            {
                float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                sc.Layout.Window(sc.ItemCount, cross, vp, contentNext, 0, out visibleFirst, out visibleLast);
            }
            else if (_scene.TryGetExtents(n, out var t) && t is not null)   // variable (extent table)
            {
                visibleFirst = t.IndexAt(contentNext);
                visibleLast = Math.Min(sc.ItemCount, t.IndexAt(contentNext + vp) + 1);
            }
            else { visibleFirst = visibleLast = 0; }
            if (VirtualWindowing.NeedsRealize(in sc, visibleFirst, visibleLast)) { _scene.Mark(n, NodeFlags.VirtualRangeDirty); RequestRerender(); }
        }
    }

    private bool TryScrollbarPointerDown(Point2 p)
    {
        var n = ScrollableUnder(p);
        if (n.IsNull || !TryGetScrollbarMetrics(n, out var m)) return false;

        var local = new Point2(p.X - m.Bounds.X, p.Y - m.Bounds.Y);
        if (!InScrollbarLane(local, in m)) return false;

        // Scrollbar grab over a coasting/animating viewport zeros its motion first (scroll-feel-rework-v2 §2.2): the thumb
        // drag / track-click must own the offset, not fight a live fling/chase. (Also reached from the touch path's
        // TouchDown, so a touch scrollbar grab cancels the coast too.)
        OnCancelFling?.Invoke(n);

        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.PointerOver = true;
        sc.PointerOverScrollbar = true;
        sc.IdleMs = 0f;
        if (sc.FadeT < 0.2f) sc.FadeT = 0.2f;
        OnScrollArmed?.Invoke(n);

        float axis = AxisPos(local, in m);
        if (axis >= m.ThumbStart && axis <= m.ThumbStart + m.ThumbLen)
        {
            _scrollDragNode = n;
            _scrollDragGrab = Math.Clamp(axis - m.ThumbStart, 0f, m.ThumbLen);
            return true;
        }

        float delta;
        if (m.Button > 1f && axis < m.Button) delta = -ScrollbarSmallChange;
        else if (m.Button > 1f && axis >= m.Axis - m.Button) delta = ScrollbarSmallChange;
        else
        {
            float page = MathF.Max(ScrollbarSmallChange, m.Viewport * 0.875f);
            delta = axis < m.ThumbStart ? -page : page;
        }
        ScrollBy(n, delta, SmoothScroll);
        return true;
    }

    private bool DragScrollbar(Point2 p)
    {
        if (_scrollDragNode.IsNull) return false;
        if (!TryGetScrollbarMetrics(_scrollDragNode, out var m))
        {
            _scrollDragNode = NodeHandle.Null;
            return false;
        }

        var local = new Point2(p.X - m.Bounds.X, p.Y - m.Bounds.Y);
        float axis = AxisPos(local, in m);
        float thumbStart = Math.Clamp(axis - _scrollDragGrab, m.TrackStart, m.TrackStart + m.Travel);
        float fraction = Math.Clamp((thumbStart - m.TrackStart) / MathF.Max(1f, m.Travel), 0f, 1f);

        // §2.1/§6 single-writer: the thumb drag RECORDS an exact absolute-offset intent (PendingTarget + PhaseImmediate)
        // and arms the node; the phase-7 integrator writes the offset (verbatim, no spring — 1:1 thumb tracking). The
        // dispatcher never moves the offset synchronously.
        ref ScrollState sc = ref _scene.ScrollRef(_scrollDragNode);
        bool horiz = sc.Orientation == 1;
        float targetOff = fraction * m.Max;
        if (horiz) sc.PendingTargetX = targetOff; else sc.PendingTargetY = targetOff;
        sc.Phase = ScrollIntegrator.WheelAnimating;
        sc.PhaseFlags = ScrollState.PhaseImmediate;
        sc.FlingVelocity = 0f; sc.FlingRetargeted = false; sc.FlingSnapTarget = float.NaN;
        sc.PointerOver = true;
        sc.PointerOverScrollbar = true;
        sc.IdleMs = 0f;
        OnScrollArmed?.Invoke(_scrollDragNode);
        return true;
    }

    private bool TryGetScrollbarMetrics(NodeHandle n, out ScrollbarMetrics m)
    {
        m = default;
        if (n.IsNull || !_scene.HasScroll(n)) return false;

        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        float max = MathF.Max(0f, content - viewport);
        if (max <= 0.5f) return false;

        var bounds = _scene.AbsoluteRect(n);
        float axis = horizontal ? bounds.W : bounds.H;
        float cross = horizontal ? bounds.H : bounds.W;
        if (axis <= 1f || cross <= 1f) return false;

        float expand = Math.Clamp(sc.ExpandT, 0f, 1f);
        float button = ScrollbarSize * expand;
        float trackStart = button;
        float trackLen = MathF.Max(1f, axis - 2f * button);
        float fraction = Math.Clamp(viewport / content, 0.08f, 1f);
        float minThumb = ScrollbarMinCollapsedThumb + (ScrollbarMinExpandedThumb - ScrollbarMinCollapsedThumb) * expand;
        float thumbLen = MathF.Min(trackLen, MathF.Max(minThumb, fraction * trackLen));
        float travel = MathF.Max(1f, trackLen - thumbLen);
        float off = horizontal ? sc.OffsetX : sc.OffsetY;
        float thumbStart = trackStart + Math.Clamp(off / MathF.Max(max, 1f), 0f, 1f) * travel;

        m = new ScrollbarMetrics
        {
            Bounds = bounds,
            Horizontal = horizontal,
            Axis = axis,
            Cross = cross,
            Viewport = viewport,
            Max = max,
            Button = button,
            TrackStart = trackStart,
            ThumbStart = thumbStart,
            ThumbLen = thumbLen,
            Travel = travel,
        };
        return true;
    }

    private static bool InScrollbarLane(Point2 local, in ScrollbarMetrics m)
    {
        float cross = m.Horizontal ? local.Y : local.X;
        float laneStart = m.Cross - ScrollbarSize;
        return cross >= laneStart && cross < m.Cross;
    }

    private static float AxisPos(Point2 local, in ScrollbarMetrics m) => m.Horizontal ? local.X : local.Y;

    private struct ScrollbarMetrics
    {
        public RectF Bounds;
        public bool Horizontal;
        public float Axis, Cross, Viewport, Max;
        public float Button, TrackStart, ThumbStart, ThumbLen, Travel;
    }

    /// <summary>Move a single-node interaction flag (hover/pressed) from the old node to <paramref name="next"/>.</summary>
    private void SetState(ref NodeHandle slot, NodeHandle next, NodeFlags flag)
    {
        if (slot == next) return;
        NodeHandle prev = slot;
        if (!prev.IsNull && _scene.IsLive(prev)) _scene.Flags(prev) &= ~flag;
        slot = next;
        if (!next.IsNull) _scene.Flags(next) |= flag;
        if (flag == NodeFlags.Hovered) UpdateHoverWithin(prev, next);
        Notify(flag, prev, on: false);
        Notify(flag, next, on: true);
    }

    // Hover-within: keep the HoverWithin flag on the interactive STRICT-ancestors of the hovered leaf, so a container
    // (a list row, a card) reads as hovered while the pointer is anywhere inside it — over its interactive children
    // (links, buttons) included. Diff prev's ancestor chain vs next's: clear the ancestors that left the chain, set the
    // ones that entered; both loops break at the first SHARED ancestor, so a move WITHIN a row dirties only the row.
    // Per-node OnHoverMove/OnPointerExit handlers are delivered on the SUBTREE edges here (WinUI PointerEntered/Exited
    // are subtree-scoped) — a wrapper whose interactive CHILD wins the hit (ToolTip.Wrap around a button) still gets
    // its enter/exit; without this the deepest-leaf-only delivery starved every such wrapper.
    private void UpdateHoverWithin(NodeHandle prev, NodeHandle next)
    {
        const int interactive = InteractionInfo.PointerBit | InteractionInfo.ClickBit | InteractionInfo.PressedBit;
        for (var n = prev.IsNull ? NodeHandle.Null : _scene.Parent(prev); !n.IsNull && _scene.IsLive(n); n = _scene.Parent(n))
        {
            if (n != next && IsSelfOrAncestorOf(n, next)) break;   // still a strict-ancestor of the new leaf → stays set
            if ((_scene.Flags(n) & NodeFlags.HoverWithin) != 0)
            {
                _scene.Flags(n) &= ~NodeFlags.HoverWithin; _scene.Mark(n, NodeFlags.PaintDirty);
                // The container left the hover scope (pointer exited its subtree) → let its reveal-on-hover descendants
                // decay (it is no longer Hovered nor HoverWithin, so SetHover resolves to off).
                OnHoverChanged?.Invoke(n, false);
                // Subtree-exit → the node's own exit handler. n == next means the pointer moved from a child UP ONTO
                // this node itself — still inside, no exit (its HoverWithin→Hovered flag handoff is paint-only).
                if (n != next) _scene.GetPointerExit(n)?.Invoke();
            }
        }
        for (var n = next.IsNull ? NodeHandle.Null : _scene.Parent(next); !n.IsNull && _scene.IsLive(n); n = _scene.Parent(n))
        {
            if (n != prev && IsSelfOrAncestorOf(n, prev)) break;   // already set from prev's chain → stop
            if ((_scene.Interaction(n).HandlerMask & interactive) != 0 && (_scene.Flags(n) & NodeFlags.HoverWithin) == 0)
            {
                _scene.Flags(n) |= NodeFlags.HoverWithin; _scene.Mark(n, NodeFlags.PaintDirty);
                // The pointer entered this container's subtree (possibly straight onto an interactive child) → keep its
                // reveal-on-hover descendants driven, so the affordance does not require the leaf to be the row itself.
                OnHoverChanged?.Invoke(n, true);
                // Subtree-enter → the node's own bare-hover handler (the ToolTip.Wrap trigger). n == prev means the
                // pointer moved from this node DOWN onto a child — it was already hovered, no re-enter.
                if (n != prev) _scene.GetHoverMove(n)?.Invoke(LocalPos(n, _lastPointerPx));
            }
        }
    }

    private void Notify(NodeFlags flag, NodeHandle node, bool on)
    {
        if (flag == NodeFlags.Hovered && on) UpdateCursor(node);   // resolve even for null (back to arrow)
        if (node.IsNull) return;
        if (flag == NodeFlags.Hovered)
        {
            OnHoverChanged?.Invoke(node, on);
            // Pointer left the leaf → its exit handler, UNLESS the new leaf is inside this node's own subtree (moving
            // from a wrapper's padding onto its interactive child is NOT an exit — WinUI PointerExited is subtree-
            // scoped; firing here would instantly cancel a pending tooltip on its own target).
            if (!on && _scene.IsLive(node) && !IsSelfOrAncestorOf(node, _hovered)) _scene.GetPointerExit(node)?.Invoke();
        }
        else if (flag == NodeFlags.Pressed) OnPressChanged?.Invoke(node, on);
    }

    /// <summary>Resolve the cursor for the hover chain — the nearest enabled node with an EXPLICIT <c>Cursor</c>
    /// (CursorBit) wins and stops the walk, so a child's Arrow masks an ancestor I-beam/hand (WinUI's forced
    /// SetCursor(MouseCursorArrow) on TextBox's delete button, TextBox_Partial.cpp:884). No explicit cursor anywhere
    /// in the chain ⇒ system arrow — clickability does NOT imply the hand (WinUI: only HyperlinkButton shows it).</summary>
    private void UpdateCursor(NodeHandle hover) => PublishCursor(ResolveCursorWalk(hover));

    private CursorId ResolveCursorWalk(NodeHandle hover)
    {
        for (var n = hover; !n.IsNull; n = _scene.Parent(n))
        {
            if (!_scene.IsLive(n)) break;
            ref InteractionInfo ii = ref _scene.Interaction(n);
            if ((ii.HandlerMask & InteractionInfo.CursorBit) != 0 && (_scene.Flags(n) & NodeFlags.Disabled) == 0) return ii.Cursor;
        }
        return CursorId.Arrow;
    }

    private void PublishCursor(CursorId resolved)
    {
        if (resolved == _lastCursor) return;
        _lastCursor = resolved;
        OnCursorChanged?.Invoke(resolved);
    }

    // ── read-only text selection + hyperlink spans (rtb-01/rtb-02) ───────────────────────────────
    // The dispatcher OWNS the gestures for SelectableTextBit / SpanLinksBit text leaves: it queries the text seam
    // (the SAME editor queries EditableText drives — point↔index, range rects) and publishes the visuals through the
    // EXISTING TextEditState/SetTextEditRects machinery, so the recorder paints read-only selection with the editor's
    // brushes unchanged. WinUI equivalent: TextSelectionManager on CRichTextBlock/CTextBlock (RichTextBlock.cpp:1730).

    private IFontSystem? ResolveFonts() => Fonts ?? TextSeam.Default;

    private string SelectableTextOf(NodeHandle node) => _scene.Strings?.Resolve(_scene.Paint(node).Text) ?? "";

    /// <summary>The seam wrap width for a text leaf's queries — the node's laid width when the style wraps, else
    /// unbounded (the EditableText.QueryMaxWidth convention; matches the recorder's DrawGlyphRun Bounds.W).</summary>
    private float QueryMaxWidth(NodeHandle node)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        return li.TextStyle.Wrap != TextWrap.NoWrap ? MathF.Max(1f, _scene.Bounds(node).W) : float.PositiveInfinity;
    }

    /// <summary>Press on a selectable text leaf: single click anchors the drag (no selection yet — unless it lands on
    /// a hyperlink span, which keeps the press a clean link click); double-click selects the word under the press;
    /// triple selects all (the RichEdit/WinUI TextSelectionManager click ladder).</summary>
    private void BeginTextSelection(NodeHandle node, Point2 local, int clickCount)
    {
        if (ResolveFonts() is not { } fonts) return;
        if (!_selText.IsNull && _selText != node) ClearTextSelection();
        string text = SelectableTextOf(node);
        if (text.Length == 0) return;
        if (clickCount == 1 && HitLinkSpan(node, local) >= 0) return;   // a link press never starts a selection
        int idx = fonts.HitTestText(text, _scene.Layout(node).TextStyle, QueryMaxWidth(node), local, out _);
        _selText = node;
        switch (clickCount)
        {
            case 1:
                _selAnchor = idx;
                _selDragging = true;
                ApplyTextSelection(node, idx, idx);   // empty until the drag extends it
                break;
            case 2:
            {
                int ws = idx, we = idx;
                while (ws > 0 && !char.IsWhiteSpace(text[ws - 1])) ws--;
                while (we < text.Length && !char.IsWhiteSpace(text[we])) we++;
                _selAnchor = ws;
                _selDragging = true;   // WinUI keeps extending by drag after a double-click
                ApplyTextSelection(node, ws, we);
                break;
            }
            default:
                _selAnchor = 0;
                _selDragging = false;
                ApplyTextSelection(node, 0, text.Length);
                break;
        }
    }

    private void ExtendTextSelection(Point2 local)
    {
        if (ResolveFonts() is not { } fonts) return;
        var node = _selText;
        string text = SelectableTextOf(node);
        if (text.Length == 0) return;
        int idx = fonts.HitTestText(text, _scene.Layout(node).TextStyle, QueryMaxWidth(node), local, out _);
        ApplyTextSelection(node, Math.Min(_selAnchor, idx), Math.Max(_selAnchor, idx));
    }

    /// <summary>Commit a selection range: store it (Ctrl+C reads it back), flag the node's text-edit row
    /// SelectionActive, and publish the seam's range rects through the pooled slab the recorder already draws
    /// (selection highlight under the run + on-accent recolor — SceneRecorder's editor path, reused verbatim).</summary>
    private void ApplyTextSelection(NodeHandle node, int start, int end)
    {
        _scene.SetTextSelection(node, start, end);
        ref TextEditState tes = ref _scene.TextEditRef(node);
        if (end > start) tes.Flags |= TextEditState.SelectionActive;
        else tes.Flags &= unchecked((byte)~TextEditState.SelectionActive);

        Span<RectF> rects = stackalloc RectF[32];
        int n = 0;
        if (end > start && ResolveFonts() is { } fonts)
        {
            string text = SelectableTextOf(node);
            n = fonts.GetRangeRects(text, _scene.Layout(node).TextStyle, QueryMaxWidth(node), start, end, rects);
        }
        _scene.SetTextEditRects(node, rects[..n], default);
        _scene.Mark(node, NodeFlags.PaintDirty);
        RequestRerender();
    }

    private void ClearTextSelection()
    {
        var node = _selText;
        _selText = NodeHandle.Null;
        _selDragging = false;
        if (node.IsNull || !_scene.IsLive(node)) return;
        _scene.ClearTextSelection(node);
        ref TextEditState tes = ref _scene.TextEditRef(node);
        tes.Flags &= unchecked((byte)~TextEditState.SelectionActive);
        _scene.SetTextEditRects(node, default, default);
        _scene.Mark(node, NodeFlags.PaintDirty);
        RequestRerender();
    }

    /// <summary>The hyperlink span index under a node-local point, or −1. Hit-tests the seam-published span LINK
    /// rects (SpanRunRects on the node's span run — laid at measure, no font-seam touch here), then verifies the
    /// span actually carries an action.</summary>
    private int HitLinkSpan(NodeHandle node, Point2 local)
    {
        int runId = _scene.Layout(node).TextStyle.SpanRunId;
        if (runId == 0 || SpanRunTable.Shared.Resolve(runId) is not { } run || run.Rects is not { } rects) return -1;
        if (!_scene.TryGetSpanText(node, out var spans)) return -1;
        var arts = rects.Rects;
        for (int i = 0; i < arts.Length; i++)
        {
            if (arts[i].Kind != SpanStyle.LinkBit) continue;
            var rr = arts[i].Rect;
            if (local.X >= rr.X && local.X < rr.X + rr.W && local.Y >= rr.Y && local.Y < rr.Y + rr.H)
            {
                int si = arts[i].Span;
                if ((uint)si < (uint)spans.Length && spans[si].OnClick is not null) return si;
            }
        }
        return -1;
    }

    /// <summary>Per-move cursor over a span-text node: Hand over a hyperlink span's laid rect (WinUI
    /// RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)), else whatever the normal explicit-cursor walk resolves
    /// (I-beam when selectable, arrow otherwise).</summary>
    private void UpdateSpanCursor(NodeHandle node, Point2 abs)
    {
        var local = PointToLocal(node, abs);
        PublishCursor(HitLinkSpan(node, local) >= 0 ? CursorId.Hand : ResolveCursorWalk(node));
    }

    private void OnKey(in InputEvent e)
    {
        int key = e.KeyCode;

        // Gamepad translation (WinUI XYFocus): DPad/left-stick → directional focus, A → activate, B → cancel/Escape.
        switch (key)
        {
            case Keys.GamepadDPadLeft or Keys.GamepadLeftThumbLeft: MoveFocusArrow(FocusDirection.Left); return;
            case Keys.GamepadDPadRight or Keys.GamepadLeftThumbRight: MoveFocusArrow(FocusDirection.Right); return;
            case Keys.GamepadDPadUp or Keys.GamepadLeftThumbUp: MoveFocusArrow(FocusDirection.Up); return;
            case Keys.GamepadDPadDown or Keys.GamepadLeftThumbDown: MoveFocusArrow(FocusDirection.Down); return;
            case Keys.GamepadA: key = Keys.Enter; break;
            case Keys.GamepadB: key = Keys.Escape; break;
        }

        // An active item-drag is the most-modal gesture: Escape cancels it before any other routing (WinUI drag
        // cancel). The pointer is still down — kill the click candidate so the eventual release does NOT click. A
        // keyboard event carries no PointerId, but the item-drag is single-pointer, so clear the down candidate in
        // EVERY slot (the captured contact's slot included — its later release then falls through without a click).
        if (key == Keys.Escape && Drag.IsActive)
        {
            DragDrop.Cancel();   // L2 first: OnLeave fires on a live target, no drop
            Drag.Cancel();
            SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            ClearDownEverywhere();
            return;
        }

        // Alt access-key bookkeeping: a bare Alt tap (down with nothing in between, then up) toggles access-key mode;
        // a letter while Alt is held invokes the mnemonic directly (the WM_SYSKEYDOWN chord path).
        if (key == Keys.Alt) { _altPending = !e.IsRepeat; return; }
        _altPending = false;

        if ((e.Mods & KeyModifiers.Alt) != 0 && Keys.IsAccessKeyCandidate(key))
        {
            if (InvokeAccessKey((char)key)) return;
        }
        if (_accessKeyMode)
        {
            _accessKeyMode = false;
            if (Keys.IsAccessKeyCandidate(key) && InvokeAccessKey((char)key)) return;
            if (key == Keys.Escape) return;   // Escape only exits access-key mode
        }

        if (OnKeyPreview is not null && OnKeyPreview(key)) return;   // an open overlay can swallow Escape here

        // ANY other key while a Space/Enter press is held cancels it without firing, and the new key then routes
        // normally (ButtonBaseKeyProcess.h:64-70). Escape is handled below as the explicit, CONSUMED cancel.
        if (!_keyArmed.IsNull && key != _keyArmedKey && key != Keys.Escape) CancelKeyArm(fire: false);

        if (key == Keys.Tab)
        {
            MoveFocus(forward: (e.Mods & KeyModifiers.Shift) == 0);
            return;
        }

        // Escape cancels a held Space/Enter-activation without firing (WinUI button semantics).
        if (key == Keys.Escape && !_keyArmed.IsNull) { CancelKeyArm(fire: false); return; }

        // Context-menu key (VK_APPS) / Shift+F10 → context request on the focused node (keyboard passes its centre).
        if ((key == Keys.Apps || (key == Keys.F10 && (e.Mods & KeyModifiers.Shift) != 0)) && !_focused.IsNull)
        {
            var r = _scene.AbsoluteRect(_focused);
            if (DispatchContextRequest(_focused, new Point2(r.X + r.W / 2f, r.Y + r.H / 2f), ContextRequestTrigger.Keyboard)) return;
        }

        if (!_focused.IsNull)
        {
            bool clickable = (_scene.Flags(_focused) & NodeFlags.Disabled) == 0 &&
                             (_scene.Interaction(_focused).HandlerMask & InteractionInfo.ClickBit) != 0;

            // Modality 2 — keyboard activation with WinUI ButtonBase semantics (ButtonBaseKeyProcess.h): the FIRST
            // Space/Enter key-down arms the press (pressed visual); held-key auto-repeats are ignored, so a held key
            // yields exactly ONE activation; the click fires on key-UP (ClickMode.Release reentrancy rule,
            // ButtonBase_Partial.cpp:475-483). A RepeatButton is ClickMode.Press (RepeatButton_Partial.cpp:29): its
            // click fires on the DOWN edge — Space additionally arms the engine repeat timer (never the OS key-repeat
            // rate), Enter never repeats (RepeatButton_Partial.cpp:212-217). Space-only controls (CheckBox/
            // RadioButton/ToggleSwitch — NoEnterActivateBit) let Enter fall through to normal key routing.
            if ((key == Keys.Space || key == Keys.Enter) && clickable &&
                (key == Keys.Space || (_scene.Interaction(_focused).HandlerMask & InteractionInfo.NoEnterActivateBit) == 0))
            {
                if (e.IsRepeat || !_keyArmed.IsNull) return;   // held key / second activation key: one press only
                _keyArmed = _focused;
                _keyArmedKey = key;
                _scene.Flags(_focused) |= NodeFlags.Pressed;
                OnPressChanged?.Invoke(_focused, true);
                if ((_scene.Interaction(_focused).HandlerMask & InteractionInfo.RepeatBit) != 0)
                {
                    if (key == Keys.Space) OnRepeatArmed?.Invoke(_focused);   // fires once now + repeats while held
                    else InvokeActivation(_focused, ContextRequestTrigger.Keyboard);   // Enter: exactly one click (or keyboard context-invoke), down edge
                }
                return;
            }

            // Route to the focused node and bubble up ancestors until Handled (disabled nodes don't receive keys).
            var args = new KeyEventArgs(key, e.Mods, e.IsRepeat);
            for (var n = _focused; !n.IsNull; n = _scene.Parent(n))
            {
                if ((_scene.Flags(n) & NodeFlags.Disabled) == 0) _scene.GetKeyHandler(n)?.Invoke(args);
                if (args.Handled) return;
            }
        }

        // Unhandled Escape is the app-wide "leave keyboard focus" gesture. Controls and overlays get first refusal
        // above; only a genuinely unhandled Escape clears the focus ring/caret and runs routed LostFocus cleanup.
        if (key == Keys.Escape && !_focused.IsNull) { SetFocus(NodeHandle.Null); return; }

        // Ctrl+C over a read-only selection (rtb-02): copy the focused selectable node's selected text through the
        // clipboard seam (WinUI TextSelectionManager::CopySelectionToClipboard — TextSelectionManager.cpp:30-41).
        // After focused routing: an editor's own Ctrl+C (EditableText) already consumed the chord above.
        if (key == 'C' && (e.Mods & KeyModifiers.Ctrl) != 0 && !_focused.IsNull && _scene.IsLive(_focused)
            && (_scene.Interaction(_focused).HandlerMask & InteractionInfo.SelectableTextBit) != 0
            && _scene.TryGetTextSelection(_focused, out int selS, out int selE) && selE > selS)
        {
            string selDoc = SelectableTextOf(_focused);
            if (selE <= selDoc.Length) Clipboard?.SetText(selDoc.Substring(selS, selE - selS));
            return;
        }

        // Keyboard accelerators (WinUI ProcessKeyboardAccelerators order: after focused routing leaves it unhandled).
        if ((e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0 || (key >= Keys.F1 && key <= Keys.F12))
        {
            var owner = _scene.FindAccelerator(key, e.Mods);
            if (!owner.IsNull) InvokeActivation(owner, ContextRequestTrigger.Keyboard);   // one commit path (an accelerator owner is never a ContextBit invoker in practice)
        }
    }

    private void OnKeyUp(in InputEvent e)
    {
        int key = e.KeyCode;
        // The gamepad A/B translation must mirror OnKey's, or a GamepadA-armed press would never release.
        if (key == Keys.GamepadA) key = Keys.Enter;
        else if (key == Keys.GamepadB) key = Keys.Escape;

        if (key == Keys.Alt)
        {
            if (_altPending) _accessKeyMode = !_accessKeyMode;   // bare Alt tap toggles access-key mode
            _altPending = false;
            return;
        }
        if ((key == Keys.Space || key == Keys.Enter) && !_keyArmed.IsNull && key == _keyArmedKey)
        {
            // Released over the armed node → activate (ClickMode.Release). Repeat nodes already fired on the down
            // edge (Enter) or through the ticker (Space) — their release only clears the press, never re-fires.
            bool repeats = _scene.IsLive(_keyArmed) &&
                           (_scene.Interaction(_keyArmed).HandlerMask & InteractionInfo.RepeatBit) != 0;
            CancelKeyArm(fire: !repeats);
        }
    }

    /// <summary>Release a held Space/Enter-activation: clear the pressed visual, stop a keyboard-armed repeat ticker;
    /// <paramref name="fire"/> = invoke the click (key-up over the armed node).</summary>
    private void CancelKeyArm(bool fire)
    {
        var node = _keyArmed;
        int key = _keyArmedKey;
        _keyArmed = NodeHandle.Null;
        _keyArmedKey = 0;
        if (node.IsNull || !_scene.IsLive(node)) return;
        if (key == Keys.Space && (_scene.Interaction(node).HandlerMask & InteractionInfo.RepeatBit) != 0)
            OnRepeatReleased?.Invoke(node);
        _scene.Flags(node) &= ~NodeFlags.Pressed;
        OnPressChanged?.Invoke(node, false);
        if (fire && node == _focused && (_scene.Flags(node) & NodeFlags.Disabled) == 0)
            InvokeActivation(node, ContextRequestTrigger.Keyboard);   // Space/Enter key-up activation: a ClickRequestsContext node opens keyboard-anchored (first item focused)
    }

    private bool InvokeAccessKey(char key)
    {
        var owner = _scene.FindAccessKey(key);
        if (owner.IsNull) return false;
        _accessKeyMode = false;
        InvokeActivation(owner, ContextRequestTrigger.Keyboard);   // one commit path (an access-key owner is never a ContextBit invoker in practice)
        return true;
    }

    /// <summary>Route a text (character) codepoint to the focused node, bubbling up ancestors until Handled.</summary>
    private bool OnChar(int codepoint)
    {
        if (_focused.IsNull) return false;
        var args = new CharEventArgs(codepoint);   // cold path: only allocates while the user is typing
        for (var n = _focused; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) == 0 &&
                (_scene.Interaction(n).HandlerMask & InteractionInfo.CharBit) != 0)
            {
                _scene.GetCharHandler(n)?.Invoke(args);
                if (args.Handled) return true;
            }
        }
        return false;
    }

    /// <summary>Move focus to the next/previous focusable node in tab order (TabIndex, then document order; cycles).
    /// Constrained to the active focus scope when one is pushed (dialog/flyout focus trap).</summary>
    public void MoveFocus(bool forward)
    {
        _focusables.Clear();
        Collect(ScopeRoot, _focusables);
        StableSortByTabIndex(_focusables);
        if (_focusables.Count == 0) { SetFocus(NodeHandle.Null); return; }

        int idx = _focusables.IndexOf(_focused);
        int n = _focusables.Count;
        int next = idx < 0 ? (forward ? 0 : n - 1) : (forward ? (idx + 1) % n : (idx - 1 + n) % n);
        SetFocus(_focusables[next], visual: true);   // keyboard focus → show the focus ring
    }

    /// <summary>Directional (arrow/XY) focus movement: from the focused node, pick the nearest focusable in
    /// <paramref name="dir"/> (primary-axis distance dominates; cross-axis breaks ties). For roving lists/grids.</summary>
    public void MoveFocusArrow(FocusDirection dir)
    {
        if (_focused.IsNull) { MoveFocus(forward: true); return; }
        _focusables.Clear();
        Collect(ScopeRoot, _focusables);
        if (_focusables.Count == 0) return;

        var cur = _scene.AbsoluteRect(_focused);
        float cx = cur.X + cur.W * 0.5f, cy = cur.Y + cur.H * 0.5f;
        NodeHandle best = NodeHandle.Null;
        float bestScore = float.MaxValue;
        bool horizontal = dir is FocusDirection.Left or FocusDirection.Right;
        foreach (var n in _focusables)
        {
            if (n == _focused) continue;
            var r = _scene.AbsoluteRect(n);
            float dx = (r.X + r.W * 0.5f) - cx, dy = (r.Y + r.H * 0.5f) - cy;
            bool inDir = dir switch
            {
                FocusDirection.Left => dx < -1f,
                FocusDirection.Right => dx > 1f,
                FocusDirection.Up => dy < -1f,
                FocusDirection.Down => dy > 1f,
                _ => false,
            };
            if (!inDir) continue;
            float primary = horizontal ? MathF.Abs(dx) : MathF.Abs(dy);
            float cross = horizontal ? MathF.Abs(dy) : MathF.Abs(dx);
            float score = primary + cross * 2f;   // bias toward staying on the same row/column
            if (score < bestScore) { bestScore = score; best = n; }
        }
        if (!best.IsNull) SetFocus(best, visual: true);
    }

    /// <summary>First focusable within <paramref name="root"/>'s subtree (tab order) — for focus-trap entry / menus.</summary>
    public NodeHandle FirstFocusableIn(NodeHandle root)
    {
        _scoped.Clear();
        Collect(root, _scoped);
        StableSortByTabIndex(_scoped);
        return _scoped.Count > 0 ? _scoped[0] : NodeHandle.Null;
    }

    /// <summary>Last focusable within <paramref name="root"/>'s subtree (tab order) — for Shift-Tab focus-trap wrap.</summary>
    public NodeHandle LastFocusableIn(NodeHandle root)
    {
        _scoped.Clear();
        Collect(root, _scoped);
        StableSortByTabIndex(_scoped);
        return _scoped.Count > 0 ? _scoped[^1] : NodeHandle.Null;
    }

    /// <summary>Next/previous focusable within <paramref name="root"/>, cycling — roving-tabindex within a list/menu/overlay.</summary>
    public NodeHandle NextFocusableIn(NodeHandle root, NodeHandle current, bool forward = true)
    {
        _scoped.Clear();
        Collect(root, _scoped);
        StableSortByTabIndex(_scoped);
        if (_scoped.Count == 0) return NodeHandle.Null;
        int idx = _scoped.IndexOf(current);
        int n = _scoped.Count;
        int next = idx < 0 ? (forward ? 0 : n - 1) : (forward ? (idx + 1) % n : (idx - 1 + n) % n);
        return _scoped[next];
    }

    // Stable insertion sort by effective TabIndex (explicit positive indices first ascending; default 0/unset keep
    // document order at the end). Stable + in-place + alloc-free; the focusable count is small (cold Tab/arrow path).
    private void StableSortByTabIndex(List<NodeHandle> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            var node = list[i];
            int key = TabKey(node);
            int j = i - 1;
            while (j >= 0 && TabKey(list[j]) > key) { list[j + 1] = list[j]; j--; }
            list[j + 1] = node;
        }
    }

    private int TabKey(NodeHandle n) { int t = _scene.Interaction(n).TabIndex; return t > 0 ? t : int.MaxValue; }

    /// <summary>Move focus. <paramref name="visual"/> = show the focus ring (keyboard/Tab); pointer focus passes false.
    /// Focus-visual transitions mark the affected nodes PaintDirty and request a frame — the ring is paint state.
    /// When focus actually MOVES, the old node's <c>OnFocusChanged</c> handler fires with false and the new node's with
    /// true (WinUI LostFocus → GotFocus order), AFTER all flag updates — so a GotFocus handler can read
    /// <see cref="NodeFlags.FocusVisual"/> to distinguish keyboard focus (select-all) from pointer focus.</summary>
    public void SetFocus(NodeHandle node, bool visual = false)
    {
        if (!node.IsNull && (_scene.Flags(node) & NodeFlags.Disabled) != 0) return;   // can't focus a disabled node — keep current focus
        if (!_keyArmed.IsNull && node != _keyArmed) CancelKeyArm(fire: false);  // focus moved while Space/Enter held → no activation
        var prev = _focused;
        bool repaint = false;
        if (!_focused.IsNull && _scene.IsLive(_focused))
        {
            if ((_scene.Flags(_focused) & NodeFlags.FocusVisual) != 0)
            {
                _scene.Mark(_focused, NodeFlags.PaintDirty);   // the old ring must disappear
                repaint = true;
            }
            _scene.Flags(_focused) &= ~(NodeFlags.Focused | NodeFlags.FocusVisual);
        }
        _focused = node;
        if (!node.IsNull)
        {
            _scene.Flags(node) |= NodeFlags.Focused;
            if (visual)
            {
                _scene.Flags(node) |= NodeFlags.FocusVisual;
                _scene.Mark(node, NodeFlags.PaintDirty);
                repaint = true;
            }
            else _scene.Flags(node) &= ~NodeFlags.FocusVisual;
        }
        if (prev != node)
        {
            // WinUI GotFocus/LostFocus are ROUTED (bubbling) events: an ancestor with an OnFocusChanged handler hears
            // focus ENTERING/LEAVING its SUBTREE, fired only on boundary crossings. The focused node itself keeps the
            // exact pre-existing self semantics. ToolTipService's keyboard-focus trigger hangs off this
            // (microsoft-ui-xaml ToolTipService_Partial.cpp:1635 OnOwnerGotFocus on the OWNER element).
            if (!prev.IsNull && _scene.IsLive(prev))
            {
                if ((_scene.Interaction(prev).HandlerMask & InteractionInfo.FocusBit) != 0)
                    _scene.GetFocusChanged(prev)?.Invoke(false);
                for (var n = _scene.Parent(prev); !n.IsNull; n = _scene.Parent(n))
                    if ((_scene.Interaction(n).HandlerMask & InteractionInfo.FocusBit) != 0 && !IsSelfOrAncestorOf(n, node))
                        _scene.GetFocusChanged(n)?.Invoke(false);
            }
            if (!node.IsNull && _scene.IsLive(node) && node == _focused)   // a LostFocus handler may have re-moved focus
            {
                if ((_scene.Interaction(node).HandlerMask & InteractionInfo.FocusBit) != 0)
                    _scene.GetFocusChanged(node)?.Invoke(true);
                for (var n = _scene.Parent(node); !n.IsNull && node == _focused; n = _scene.Parent(n))
                    if ((_scene.Interaction(n).HandlerMask & InteractionInfo.FocusBit) != 0 && !IsSelfOrAncestorOf(n, prev))
                        _scene.GetFocusChanged(n)?.Invoke(true);
            }
        }
        if (repaint) RequestRerender();
    }

    /// <summary>Resolve a pointer-activation focus target: the nearest self-or-ancestor carrying
    /// <see cref="NodeFlags.Focusable"/> (the Reconciler keeps that flag mirrored from <c>InteractionInfo.Focusable</c> =
    /// <c>TabStop ?? (Focusable || OnClick != null)</c>). WinUI <c>Control.IsTabStop=False</c> parts cannot receive
    /// focus at all — pointer included — so a click on them lands focus on the focusable control root above (the
    /// PasswordBox RevealButton / TextBox DeleteButton keep the FIELD focused; PasswordBox_themeresources.xaml:193 +
    /// TextBox_themeresources.xaml:339, both IsTabStop=False). Null = nothing focusable in the chain — the caller must
    /// leave focus unchanged. Disabled nodes are skipped (they cannot take focus); the chain is already visible
    /// (hit-testing requires Visible on every ancestor).</summary>
    private NodeHandle NearestFocusable(NodeHandle node)
    {
        for (var n = node; !n.IsNull && _scene.IsLive(n); n = _scene.Parent(n))
            if ((_scene.Flags(n) & (NodeFlags.Focusable | NodeFlags.Disabled)) == NodeFlags.Focusable) return n;
        return NodeHandle.Null;
    }

    /// <summary>True if <paramref name="root"/> is <paramref name="node"/> or one of its ancestors — i.e. focus stayed
    /// inside <paramref name="root"/>'s subtree, so no enter/leave boundary was crossed for it.</summary>
    private bool IsSelfOrAncestorOf(NodeHandle root, NodeHandle node)
    {
        if (node.IsNull || !_scene.IsLive(node)) return false;
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
            if (n == root) return true;
        return false;
    }

    private void Collect(NodeHandle node, List<NodeHandle> into)
    {
        if (node.IsNull) return;
        ref InteractionInfo ii = ref _scene.Interaction(node);
        if (ii.Focusable && (_scene.Flags(node) & (NodeFlags.Visible | NodeFlags.Disabled)) == NodeFlags.Visible) into.Add(node);
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) Collect(c, into);
    }

    /// <summary>Event position (window space) → the node's MODEL-LOCAL coords, clamped to its box (slider/scrollbar
    /// drag). Inverse-maps through every ancestor transform (scale-aware: a slider inside a Viewbox scrubs true).</summary>
    private Point2 LocalPos(NodeHandle node, Point2 abs)
    {
        var l = PointToLocal(node, abs);
        return new Point2(Math.Clamp(l.X, 0f, _scene.Bounds(node).W), Math.Clamp(l.Y, 0f, _scene.Bounds(node).H));
    }

    private readonly List<NodeHandle> _chain = new();   // reused root→node buffer for PointToLocal (0 steady alloc)

    /// <summary>Window-space point → <paramref name="node"/>'s model-local space, undoing every ancestor's frame the
    /// way the recorder composes it (translate(bounds) ∘ origin-conjugated LocalTransform, counter-scale about the
    /// centre) — the inverse of SceneRecorder.Walk's world transform.</summary>
    private Point2 PointToLocal(NodeHandle node, Point2 abs)
    {
        _chain.Clear();
        for (var n = node; !n.IsNull; n = _scene.Parent(n)) _chain.Add(n);
        var q = abs;
        float sx = 1f, sy = 1f;
        for (int i = _chain.Count - 1; i >= 0; i--)
        {
            if (!StepIntoNode(_chain[i], ref q, ref sx, ref sy)) return q;   // degenerate scale: best-effort
            if (i > 0)
            {
                ref NodePaint p = ref _scene.Paint(_chain[i]);
                q = new Point2(q.X - p.ChildShiftX, q.Y - p.ChildShiftY);
            }
        }
        return q;
    }

    /// <summary>Undo ONE node's frame: parent-content point → this node's model-local point, mirroring the recorder's
    /// composition (translate(b) ∘ [T(o)∘L∘T(−o)] then the counter-scale block). Updates the running net scale the
    /// children inherit. Interaction hover/press grow is deliberately NOT mirrored — the hit target keeps its model
    /// box while a thumb visually grows (matches the previous engine behavior and WinUI's layout-driven hit testing).</summary>
    private bool StepIntoNode(NodeHandle node, ref Point2 q, ref float netSx, ref float netSy)
    {
        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);
        float lx = q.X - b.X, ly = q.Y - b.Y;
        if (!np.LocalTransform.IsIdentity)
        {
            float ox = b.W * np.OriginX, oy = b.H * np.OriginY;
            // forward: l' = L(l − o) + o  ⇒  l = L⁻¹(l' − o) + o
            if (!np.LocalTransform.TryInverseTransform(new Point2(lx - ox, ly - oy), out var inv))
            {
                q = new Point2(float.MinValue, float.MinValue);   // zero-scale renders nothing: miss everything
                return false;
            }
            lx = inv.X + ox; ly = inv.Y + oy;
        }
        // Counter-scaled node (drag-lift label …): the recorder un-scales it about its centre by the PARENT net scale
        // (SceneRecorder counter block) — the inverse re-applies that scale to the point.
        if ((_scene.Flags(node) & NodeFlags.CounterScaled) != 0 && (netSx != 1f || netSy != 1f))
        {
            float cx = b.W * 0.5f, cy = b.H * 0.5f;
            lx = (lx - cx) * netSx + cx;
            ly = (ly - cy) * netSy + cy;
            netSx = 1f; netSy = 1f;
        }
        netSx *= np.LocalTransform.M11;
        netSy *= np.LocalTransform.M22;
        q = new Point2(lx, ly);
        return true;
    }

    private Point2 _hitAbs;   // the window-space point of the in-flight hit walk (pass-through rect checks)

    public NodeHandle HitTest(Point2 p)
    {
        if (_scene.Root.IsNull) return NodeHandle.Null;
        _hitAbs = p;
        return Hit(_scene.Root, p, 1f, 1f);
    }

    /// <summary>Deepest visible node containing the point, regardless of click handler (used to find a scroll target).</summary>
    private NodeHandle HitTestAny(Point2 p)
    {
        if (_scene.Root.IsNull) return NodeHandle.Null;
        _hitAbs = p;
        return HitAny(_scene.Root, p, 1f, 1f);
    }

    /// <summary>WinUI <c>OverlayInputPassThroughElement</c>: a light-dismiss scrim yields the hit when the pointer is
    /// over its registered pass-through subtree — input falls through to the content beneath (the MenuBar keeps
    /// hover-switching titles with a menu open, FlyoutBase_Partial.cpp:3922-3938).</summary>
    private bool YieldsToPassThrough(NodeHandle node)
    {
        if (!_scene.TryGetHitTestPassThrough(node, out var pass) || pass.IsNull || !_scene.IsLive(pass)) return false;
        var pr = _scene.AbsoluteRect(pass);
        return _hitAbs.X >= pr.X && _hitAbs.X < pr.X + pr.W && _hitAbs.Y >= pr.Y && _hitAbs.Y < pr.Y + pr.H;
    }

    // Both walks descend the POINT through each node's inverse transform (scale-aware — WinUI hit-tests the rendered
    // geometry, so a button inside a 2× Viewbox is clickable across its whole rendered extent), mirroring the
    // recorder's world composition exactly. q is the point in the node's PARENT-content space.

    private NodeHandle HitAny(NodeHandle node, Point2 q, float netSx, float netSy)
    {
        var flags = _scene.Flags(node);
        if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible))
            return NodeHandle.Null;

        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);
        var local = q;
        if (!StepIntoNode(node, ref local, ref netSx, ref netSy)) return NodeHandle.Null;
        float hitW = float.IsNaN(np.PresentedW) ? b.W : np.PresentedW;
        float hitH = float.IsNaN(np.PresentedH) ? b.H : np.PresentedH;
        bool inside = local.X >= 0f && local.X < hitW && local.Y >= 0f && local.Y < hitH;
        if ((flags & NodeFlags.ClipsToBounds) != 0 && !inside) return NodeHandle.Null;

        var childLocal = new Point2(local.X - np.ChildShiftX, local.Y - np.ChildShiftY);
        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = HitAny(c, childLocal, netSx, netSy);
            if (!r.IsNull) result = r;
        }
        if (result.IsNull && inside && !YieldsToPassThrough(node))
            result = node;
        return result;
    }

    private NodeHandle Hit(NodeHandle node, Point2 q, float netSx, float netSy)
    {
        var flags = _scene.Flags(node);
        if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible))
            return NodeHandle.Null;

        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);
        var local = q;
        if (!StepIntoNode(node, ref local, ref netSx, ref netSy)) return NodeHandle.Null;
        float hitW = float.IsNaN(np.PresentedW) ? b.W : np.PresentedW;
        float hitH = float.IsNaN(np.PresentedH) ? b.H : np.PresentedH;
        bool inside = local.X >= 0f && local.X < hitW && local.Y >= 0f && local.Y < hitH;
        if ((flags & NodeFlags.ClipsToBounds) != 0 && !inside) return NodeHandle.Null;

        var childLocal = new Point2(local.X - np.ChildShiftX, local.Y - np.ChildShiftY);
        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = Hit(c, childLocal, netSx, netSy);
            if (!r.IsNull) result = r;
        }

        if (result.IsNull)
        {
            ref InteractionInfo ii = ref _scene.Interaction(node);
            // CursorBit makes a node hover-resolvable in its own right (WinUI: SetCursor applies on direct hover of
            // any hit-testable element — XAML hit-testing is background-gated, not handler-gated), so an editing
            // surface's own padding/gaps still show its I-beam. Harmless for clicks: no handler ⇒ nothing fires.
            // SelectableText text leaves hit-test across their whole box (drag-select anchoring).
            // ContextBit: an OnContextRequested handler makes a node hit-testable in its own right (WinUI: an element
            // with a ContextFlyout is a hit-test target), so a context-only row/card takes right-clicks without also
            // needing a click/pointer handler. Left-clicks on it walk up past it exactly like any handler-less hit.
            const int hitAnywhere = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit
                | InteractionInfo.DragBit | InteractionInfo.CursorBit | InteractionInfo.SelectableTextBit | InteractionInfo.GestureBit
                | InteractionInfo.ContextBit;
            if ((flags & NodeFlags.Disabled) == 0 && inside && !YieldsToPassThrough(node))   // disabled nodes don't hit-test
            {
                if ((ii.HandlerMask & hitAnywhere) != 0)
                    result = node;
                // A SpanLinks-ONLY leaf (an inline hyperlink with no other interaction) is hit ONLY over a link RECT —
                // the gaps around the link text fall through to the container beneath (the list row), so clicking the
                // empty space next to an artist/album link selects the ROW (WinUI inline-Hyperlink hit shape).
                else if ((ii.HandlerMask & InteractionInfo.SpanLinksBit) != 0 && HitLinkSpan(node, local) >= 0)
                    result = node;
            }
        }
        return result;
    }
}
