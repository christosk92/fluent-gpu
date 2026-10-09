using System;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Signals;

namespace FluentGpu.Scroll.Runtime;

/// <summary>How a programmatic move travels (design §9).</summary>
public enum ScrollMove : byte
{
    /// <summary>Velocity-continuous critically-damped glide (<see cref="PlanAuthor.Glide"/>).</summary>
    Glide = 0,
    /// <summary>Jump: a Hold at the target.</summary>
    Immediate = 1,
    /// <summary>A gentle re-target that yields to the user: the same glide, but ignored while a user-driven plan
    /// (wheel/drag/fling/thumb) is live — the lyrics-follow move.</summary>
    Follow = 2,
}

/// <summary>Per-viewport motion state derived from poser feedback (design §7). <see cref="UserDriven"/> is true
/// for wheel/drag/fling/thumb plans that have not settled.</summary>
public readonly record struct ScrollMotionState(MotionKind Kind, float SpeedDipPerS, bool UserDriven)
{
    public static readonly ScrollMotionState Idle = new(MotionKind.Idle, 0f, false);
    public bool IsMoving => Kind != MotionKind.Idle;
}

/// <summary>Pure latch for <see cref="ScrollHandle.Restore"/>: a restored offset is held until the extent can
/// actually hold it (<c>extent − viewport ≥ target</c>), then applied once.</summary>
public struct RestoreLatch
{
    public bool Pending { get; private set; }
    public double Target { get; private set; }

    public void Arm(double target)
    {
        Target = target;
        Pending = true;
    }

    public void Cancel() => Pending = false;

    /// <summary>Resolves when <c>max(0, extent − viewport) ≥ target</c>; the latch clears and <paramref name="offset"/>
    /// receives the target. A target ≤ 0 resolves immediately.</summary>
    public bool TryResolve(double extent, double viewport, out double offset)
    {
        offset = 0.0;
        if (!Pending) return false;
        double max = extent - viewport;
        if (max < 0.0) max = 0.0;
        if (Target <= max)
        {
            offset = Target < 0.0 ? 0.0 : Target;
            Pending = false;
            return true;
        }
        return false;
    }
}

/// <summary>
/// The app-facing per-viewport scroll surface (design B.7/§9). Owns the viewport's <see cref="PlanSlots"/> entry
/// once <see cref="Bind"/>-ed by the host (a handle is app-constructible and travels down through
/// <c>ScrollEl.Handle</c> / <c>VirtualListEl.Handle</c>; the host binds it when the viewport node mounts and unbinds
/// it on unmount). Every input path (wheel, contact, thumb, keyboard) and every programmatic move authors a fresh plan
/// through <see cref="PlanAuthor"/> with <see cref="ScrollTunables.Current"/> and writes it to the slot — nothing else
/// ever writes a plan. <see cref="Offset"/>/<see cref="Motion"/>/<see cref="AtStart"/>/<see cref="AtEnd"/> are read
/// signals fed from render-thread feedback by <see cref="ApplyFeedback"/>, which the host calls once per UI frame (UI
/// thread only — signals are UI-thread state). Positions are content-space doubles; <see cref="Extent"/>/
/// <see cref="Viewport"/> come from the layout via <see cref="SetExtent"/>, which also resolves a pending
/// <see cref="Restore"/>. A move requested before the handle is bound is latched and applied at bind.
/// </summary>
public sealed class ScrollHandle
{
    private PlanSlots? _slots;
    private Func<double> _nowSec = static () => 0.0;
    private Func<double> _shownFloorSec = s_noFloor;
    private static readonly Func<double> s_noFloor = static () => double.NegativeInfinity;
    private readonly Signal<double> _offset = new(0.0);
    private readonly Signal<ScrollMotionState> _motion = new(ScrollMotionState.Idle);
    private readonly Signal<bool> _atStart = new(true);
    private readonly Signal<bool> _atEnd = new(true);
    private readonly Signal<bool> _canScroll = new(false);
    private readonly Signal<double> _extentSig = new(0.0);
    private readonly Signal<double> _viewportSig = new(0.0);
    private readonly Signal<double> _maxSig = new(0.0);
    private WheelAccelState _accel;
    private RestoreLatch _restore;

    /// <summary>The unrequested-jump detector's memory for this viewport (<see cref="ScrollJumpRules"/>, folded by the host
    /// after each frame's UI pose — <c>AppHost.WatchScrollJumps</c>). Evidence only.</summary>
    internal ScrollJumpWatch JumpWatch;
    private double _extent, _viewport;
    private bool _horizontal;
    private SnapGrid _snap = SnapGrid.None;
    private double _contactOrigin;       // content offset when the current contact began
    private double _contactClockShift;   // device-clock → plan-clock shift of the live contact: + the begin's pose-floor anchor, − the resync
    private ContactClock _contactClock;   // the live contact's sample clock (ContactClock) — fixed at its begin
    private double _lastShown;
    private bool _shownValid;            // _lastShown holds a real pose (frame step / render feedback) for this binding

    /// <summary>An unbound handle: the app creates it and hands it to a scroller element; the host binds it at mount.</summary>
    public ScrollHandle() { }

    /// <param name="slots">The window's plan table.</param>
    /// <param name="vp">This viewport's id.</param>
    /// <param name="nowSec">The plan clock: absolute seconds in the same domain the render thread's present time uses.</param>
    /// <param name="horizontal">Scroll axis.</param>
    /// <param name="shownFloorSec">The pose floor: the latest present time (plan clock) a frame has already been posed
    /// for — see <see cref="Bind"/>. Null = none (re-plans anchor at their own input time).</param>
    public ScrollHandle(PlanSlots slots, ScrollViewportId vp, Func<double> nowSec, bool horizontal = false, Func<double>? shownFloorSec = null)
        => Bind(slots, vp, nowSec, horizontal, shownFloorSec);

    public ScrollViewportId Vp { get; private set; } = ScrollViewportId.None;
    public bool Horizontal => _horizontal;
    public bool IsBound => _slots is not null && !Vp.IsNone;

    /// <summary>Shown offset (content coordinates), updated from poser feedback each UI frame.</summary>
    public IReadSignal<double> Offset => _offset;

    /// <summary>Motion classification, updated from poser feedback each UI frame.</summary>
    public IReadSignal<ScrollMotionState> Motion => _motion;

    /// <summary>True while the shown offset is at (or before) the content start — the "stuck top" edge flag.</summary>
    public IReadSignal<bool> AtStart => _atStart;

    /// <summary>True while the shown offset is at (or past) the content end.</summary>
    public IReadSignal<bool> AtEnd => _atEnd;

    /// <summary>True when the content overflows the viewport along the scroll axis.</summary>
    public IReadSignal<bool> CanScroll => _canScroll;

    public double Extent => _extent;
    public double Viewport => _viewport;

    /// <summary>The content extent along the scroll axis as a signal (a rail's range binds to it).</summary>
    public IReadSignal<double> ExtentSignal => _extentSig;

    /// <summary>The viewport extent along the scroll axis as a signal.</summary>
    public IReadSignal<double> ViewportSignal => _viewportSig;

    /// <summary><c>max(0, Extent − Viewport)</c> as a signal.</summary>
    public IReadSignal<double> MaxOffsetSignal => _maxSig;

    /// <summary>The plan's maximum offset: <c>max(0, Extent − Viewport)</c>.</summary>
    public double MaxOffset => Math.Max(0.0, _extent - _viewport);

    /// <summary>The current plan (a never-torn copy). An unbound handle reports an idle plan at its last shown offset.</summary>
    public ScrollPlan Plan
        => _slots is not null && _slots.TryRead(Vp, out ScrollPlan p) ? p : ScrollPlan.Idle(Vp.Node, _lastShown, 0.0, MaxOffset, _viewport);

    /// <summary>The plan's displayed position at the handle's clock now (UI-side evaluation — identical arithmetic
    /// to the poser's).</summary>
    public double OffsetNow => Plan.Eval(_nowSec(), out _, out _);

    /// <summary>Evaluates the plan at <paramref name="t"/> (UI-side).</summary>
    public double EvalAt(double t, out double velocity, out bool settled) => Plan.Eval(t, out velocity, out settled);

    // ── binding (host) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Binds the handle to a viewport slot. A pending programmatic move/restore latched while unbound is
    /// applied once the extent arrives (<see cref="SetExtent"/>). When all <see cref="PlanSlots.Capacity"/> slots are live
    /// the handle stays unbound (<see cref="IsBound"/> false) and a later Bind retries.</summary>
    /// <param name="shownFloorSec">The pose floor (plan clock): the latest present time a frame has ALREADY been posed
    /// for (the render poser runs ahead of the clock by the present lead). A re-plan that starts from the live plan
    /// (a wheel notch, a glide, a key step, a contact begin) is anchored at <c>max(input time, floor)</c>: frames posed
    /// for later presents than the input's own time have already shown the old plan, and a curve anchored before them
    /// would catch up — or, reversing, step back past — frames the user has already seen. Null = none.</param>
    public void Bind(PlanSlots slots, ScrollViewportId vp, Func<double> nowSec, bool horizontal, Func<double>? shownFloorSec = null)
    {
        if (_slots is not null && !Vp.IsNone && (!ReferenceEquals(_slots, slots) || Vp != vp)) _slots.Release(Vp);
        if (Vp != vp) JumpWatch = default;   // a new viewport starts a new baseline
        _shownValid = false;
        _nowSec = nowSec;
        _shownFloorSec = shownFloorSec ?? s_noFloor;
        _horizontal = horizontal;
        // Every slot live (parked KeepAlive pages keep theirs): stay UNBOUND rather than bound to no slot. Moves latch as
        // they do before mount, and the host's ResolveScrollHandle sees !IsBound and retries until a slot frees.
        if (!slots.Allocate(vp, ScrollPlan.Idle(vp.Node, _lastShown, 0.0, MaxOffset, _viewport, ScrollTunables.Current.RubberBandC)))
        {
            _slots = null;
            Vp = ScrollViewportId.None;
            return;
        }
        _slots = slots;
        Vp = vp;
    }

    /// <summary>Unbinds the slot. Call on unmount. The handle keeps its last shown offset so a re-bind (a KeepAlive
    /// un-park, a remount with the same handle) can restore it.</summary>
    public void Unbind()
    {
        if (_slots is not null && !Vp.IsNone)
        {
            _lastShown = OffsetNow;
            _slots.Release(Vp);
        }
        _slots = null;
        Vp = ScrollViewportId.None;
    }

    /// <summary>The last shown offset (survives <see cref="Unbind"/>) — what the host saves under a ScrollKey.</summary>
    public double LastShown => IsBound ? OffsetNow : _lastShown;

    // ── extent / restore ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Layout reports the content extent and viewport. Rebinds the live plan's [Min, Max] (the plan itself
    /// is not re-authored — its arcs keep evaluating; the poser's coverage clamp bounds the shown value) and resolves
    /// a pending <see cref="Restore"/> once the extent can hold it.</summary>
    public void SetExtent(double extent, double viewport)
    {
        if (extent < 0.0) extent = 0.0;
        if (viewport < 0.0) viewport = 0.0;
        bool geometryChanged = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        _extentSig.Value = extent;
        _viewportSig.Value = viewport;
        _maxSig.Value = Math.Max(0.0, extent - viewport);
        _canScroll.Value = extent - viewport > 0.5;
        if (_slots is null) return;
        double max = MaxOffset;
        ScrollPlan prev = Plan;
        // The plan's bounds are content facts: re-assert them whenever they disagree (a frame shift moved Max with a
        // correction the extent has not confirmed, or a nudge through ShiftFrame that grew no content).
        if (!geometryChanged && !_restore.Pending && prev.Min == 0.0 && prev.Max == max && prev.ViewportExtent == viewport) return;
        MotionFeel feel = ScrollTunables.Current;
        ScrollPlan next = prev with { Min = 0.0, Max = max, ViewportExtent = viewport, RubberC = feel.RubberBandC };
        if (_restore.TryResolve(extent, viewport, out double restored))
            next = PlanAuthor.Immediate(in next, _nowSec(), restored);
        else if (_restore.Pending && _restore.Target > max && prev.Kind is MotionKind.Idle or MotionKind.Programmatic)
        {
            // A latched target still past the (grown) extent chases the end: hold at the new max until it can resolve.
            double p = prev.Eval(_nowSec(), out _, out _);
            if (p < max - 0.5) next = PlanAuthor.Immediate(in next, _nowSec(), max);
        }
        else if (prev.Kind == MotionKind.Idle && prev.Count > 0)
        {
            // An idle plan whose hold now sits past the new max (content shrank) re-holds at the clamp.
            double p = prev.Dest;
            if (p > max || p < 0.0) next = PlanAuthor.Immediate(in next, _nowSec(), Math.Clamp(p, 0.0, max));
        }
        _slots.Write(Vp, in next);
    }

    /// <summary>The viewport's declared snap grid (flings land on it; wheel/keyboard/programmatic never snap).</summary>
    public void SetSnap(in SnapGrid grid) => _snap = grid;

    /// <summary>Restores a remembered offset (design §9 — replaces the reconciler's <c>ScrollMemory</c>): latched
    /// until the extent can hold it, then applied as an Immediate move. A user or programmatic move before it
    /// resolves cancels it.</summary>
    public void Restore(double offset)
    {
        _restore.Arm(offset);
        if (_slots is null) return;
        if (_restore.TryResolve(_extent, _viewport, out double now))
            _slots.Write(Vp, PlanAuthor.Immediate(Plan, _nowSec(), now));
    }

    /// <summary>True while a <see cref="Restore"/> is waiting for extent.</summary>
    public bool RestorePending => _restore.Pending;

    // ── programmatic moves ──────────────────────────────────────────────────────────────────────────────────────

    public void ScrollTo(double offset, ScrollMove move = ScrollMove.Glide)
    {
        if (_slots is null)
        {
            // Not mounted yet: latch as a restore so the move lands the moment the extent can hold it.
            _restore.Arm(offset);
            _lastShown = Math.Max(0.0, offset);
            return;
        }
        _restore.Cancel();
        double t = _nowSec();
        ScrollPlan prev = Plan;
        if (move == ScrollMove.Follow && IsUserDriven(in prev, t)) return;
        if (move != ScrollMove.Immediate) t = AnchorAt(t);   // a glide continues from what is already shown
        ScrollPlan next = move == ScrollMove.Immediate
            ? PlanAuthor.Immediate(in prev, t, offset)
            : PlanAuthor.Glide(in prev, t, offset, ScrollTunables.Current);
        Publish(in next, t);
        // A destination past the extent known NOW lands at today's max and stays latched as the RAW request: the moment
        // the content grows to hold it (a late measure, a list that is still filling) SetExtent completes the move. Any
        // user input (wheel, contact, thumb, key, stop) drops the latch, so a stale request never resurrects.
        if (offset > MaxOffset + 0.5) _restore.Arm(offset);
    }

    /// <summary>Moves relative to the plan's destination (so repeated ScrollBy calls accumulate) for a glide, or
    /// relative to the displayed position for an immediate jump.</summary>
    public void ScrollBy(double delta, ScrollMove move = ScrollMove.Glide)
    {
        ScrollPlan prev = Plan;
        double from = move == ScrollMove.Immediate ? prev.Eval(_nowSec(), out _, out _) : prev.Dest;
        ScrollTo(from + delta, move);
    }

    /// <summary>Brings a content span [<paramref name="itemTop"/>, <paramref name="itemTop"/> + <paramref name="itemExtent"/>)
    /// into view. <paramref name="align"/> NaN = minimal move (no-op when already fully visible); otherwise the item's
    /// leading edge lands at <c>align·(Viewport − itemExtent)</c> from the viewport's leading edge (0 = top, 0.5 =
    /// centered, 1 = bottom). Geometry-agnostic: the caller resolves a node to content coordinates.</summary>
    public void BringIntoView(double itemTop, double itemExtent, float align = float.NaN, ScrollMove move = ScrollMove.Glide, double margin = 0.0)
    {
        double target;
        if (float.IsNaN(align))
        {
            ScrollPlan prev = Plan;
            double p = move == ScrollMove.Immediate ? prev.Eval(_nowSec(), out _, out _) : prev.Dest;
            if (itemTop - margin < p) target = itemTop - margin;
            else if (itemTop + itemExtent + margin > p + _viewport) target = itemTop + itemExtent + margin - _viewport;
            else return;
        }
        else
        {
            // Aligned: the item's leading edge sits `margin` inside the viewport at align 0, its trailing edge `margin`
            // inside at align 1 — the same gutter the minimal branch keeps.
            target = itemTop - margin - align * (_viewport - itemExtent - 2.0 * margin);
        }
        ScrollTo(target, move);
    }

    // ── input paths (called by the host's input dispatch after ScrollRouter picked this viewport) ───────────────

    /// <summary>A wheel notch (<paramref name="notches"/> signed notch units — fractional for a hi-res wheel) at device
    /// time <paramref name="tNotch"/>. The curve is anchored at the pose floor when frames have already been posed past
    /// the stamp (<see cref="PlanAuthor.WheelNotch"/>).</summary>
    public void Wheel(double tNotch, double notches)
    {
        if (_slots is null) return;
        _restore.Cancel();
        ScrollPlan prev = Plan;
        MotionFeel feel = ScrollTunables.Current;
        double floor = _shownFloorSec();
        double t0 = floor > tNotch ? floor : tNotch;
        ScrollPlan next = PlanAuthor.WheelNotch(in prev, tNotch, notches, feel, ref _accel, floor);
        if (!_snap.IsEmpty)
        {
            // Mandatory snap points: the notch's destination is re-targeted onto the grid in the notch's direction (WinUI
            // ScrollView snap points), shaped by the same wheel segment rule from the same anchor.
            double from = prev.Eval(t0, out _, out _);
            double snapped = Math.Clamp(SnapTargets.Target(next.Dest, in _snap, impulse: false, from), next.Min, next.Max);
            if (Math.Abs(snapped - next.Dest) > 1e-6)
                next = next with { S0 = PlanAuthor.WheelSeg(in prev, t0, snapped, in feel) };
        }
        Publish(in next, t0);
    }

    /// <summary>A detented wheel notch at the handle's clock now (a header's <c>Element.WheelTarget</c> route, a
    /// control forwarding notches).</summary>
    public void WheelNow(double notches) => Wheel(_nowSec(), notches);

    /// <summary>Shifts the live plan's coordinate frame by <paramref name="delta"/> (<see cref="PlanSlots.Shift"/>): an
    /// instant rebase that moves WITH every in-flight arc instead of interrupting one — a structural correction above
    /// the first visible row (a reorder/insert/measured drawer), never a motion.</summary>
    public void ShiftFrame(double delta)
    {
        if (_slots is null || delta == 0.0) return;
        bool shifted = _slots.Shift(Vp, delta);
        NoteFrameShift(delta);
        ScrollProbe.Extent(Vp.Node, 0, -1, delta, shifted, ProbeExtentCause.Structural);
    }

    /// <summary>The plan's coordinate frame was shifted by <paramref name="delta"/> through the slots directly (layout's
    /// same-frame measured correction, <see cref="Virtualizer.ApplyMeasured"/>): move the handle's own frame-relative
    /// state — the live contact origin and the last shown offset — with it, so the next contact sample continues from
    /// the corrected coordinates instead of re-posting the unshifted origin (the "fought re-pin" jitter).</summary>
    public void NoteFrameShift(double delta)
    {
        if (delta == 0.0) return;
        _contactOrigin += delta;
        _lastShown += delta;
    }

    /// <summary>A held constant-velocity move (drag-reorder edge auto-scroll): <paramref name="dipPerS"/> along the
    /// axis until the edge; 0 stops in place.</summary>
    public void AutoScroll(double dipPerS)
    {
        if (_slots is null) return;
        _restore.Cancel();
        double t = _nowSec();
        ScrollPlan prev = Plan;
        if (dipPerS == 0.0 && prev.Kind != MotionKind.Programmatic) return;
        t = AnchorAt(t);
        Publish(PlanAuthor.Constant(in prev, t, dipPerS), t);
    }

    /// <summary>A positional (device-clock) contact begins: <paramref name="pos"/> is the content position the contact
    /// maps to (the caller converts pointer DIP to content coordinates) — a touch/pen pan.</summary>
    public void ContactBegin(double t, double pos)
    {
        if (_slots is null) return;
        double now = _nowSec();
        BeginContact(t > now ? now : t, pos, clockShift: 0.0, ContactClock.Device);
    }

    /// <summary>Contact begin from the position the user is looking at; the contact's later <see cref="ContactDelta"/>s
    /// accumulate from it (the touchpad delta-stream shape). <paramref name="clock"/> is the stream's sample clock:
    /// <list type="bullet">
    /// <item><see cref="ContactClock.Device"/>: the first sample is stamped at <c>max(<paramref name="t"/>, pose floor)</c>
    /// (a stamp ahead of the plan clock first comes back to now) — it holds the shown position through every present
    /// already posed — and the contact's later device stamps slide by the same amount, so their order and spacing (the
    /// velocity) are kept.</item>
    /// <item><see cref="ContactClock.Present"/>: the stamps already are plan-clock present times (each sample carries
    /// the present of the first render turn that sees it, <see cref="ContactStamp.ForFrame"/>), so they are taken as
    /// they are — no anchor, no slide.</item>
    /// </list></summary>
    public void ContactBeginHere(double t, ContactClock clock)
    {
        if (_slots is null) return;
        if (clock == ContactClock.Present)
        {
            BeginContact(t, DisplayedAt(t), clockShift: 0.0, ContactClock.Present);
            return;
        }
        double now = _nowSec();
        double tb = AnchorAt(t > now ? now : t);
        BeginContact(tb, DisplayedAt(t), clockShift: tb - t, ContactClock.Device);
    }

    private void BeginContact(double t, double pos, double clockShift, ContactClock clock)
    {
        _restore.Cancel();
        _contactOrigin = pos;
        _contactClockShift = clockShift;
        _contactClock = clock;
        ScrollPlan next = PlanAuthor.FollowBegin(Plan, t, pos, clock, ScrollTunables.Current);
        Publish(in next, t);
    }

    /// <summary>A contact sample at time <paramref name="t"/> (on the plan clock's scale; a device stamp or a present
    /// time per the contact's <see cref="ContactClock"/>).</summary>
    public void ContactSample(double t, double pos)
    {
        if (_slots is null) return;
        ScrollPlan prev = Plan;
        if (prev.Kind != MotionKind.Drag)
        {
            // The live contact's plan was replaced (a programmatic move mid-gesture): it re-begins here on its own clock.
            if (_contactClock == ContactClock.Present) BeginContact(t, pos, clockShift: 0.0, ContactClock.Present);
            else ContactBegin(t, pos);
            return;
        }
        t = PlaceContactTime(ref prev, t);
        ScrollPlan next = PlanAuthor.FollowSample(in prev, t, pos, ScrollTunables.Current);
        Publish(in next, t);
    }

    /// <summary>Places a contact timestamp on the plan clock, per the contact's clock. A PRESENT-clock stamp is already a
    /// plan-clock present time: it only never goes back past the newest sample (a status edge the producer could only
    /// stamp "now" arrives behind a sample stamped for a later present — the lift must not rewind what was shown). A
    /// DEVICE stamp is resynced (<see cref="ResyncContactClock"/>).</summary>
    private double PlaceContactTime(ref ScrollPlan drag, double t)
    {
        if (drag.Clock == ContactClock.Present)
        {
            double newest = drag.Ring.LastT;
            return t < newest ? newest : t;
        }
        return ResyncContactClock(ref drag, t);
    }

    /// <summary>Places a DEVICE contact timestamp on the plan clock. A sample can never have happened after it was
    /// dispatched: a device clock that ran ahead of the plan clock (a coalesced burst, a clock-domain mismatch) would put
    /// the newest sample in the future, where the ring would interpolate BEHIND the finger. The excess slides the
    /// contact's whole time base back (the ring's history with it, <see cref="ContactRing.TimeShifted"/>), so the newest
    /// sample lands at now and every interval between samples — the release velocity — stays the device's own.</summary>
    private double ResyncContactClock(ref ScrollPlan drag, double t)
    {
        t += _contactClockShift;
        double now = _nowSec();
        if (t <= now) return t;
        double excess = t - now;
        _contactClockShift -= excess;
        drag = drag with { Ring = drag.Ring.TimeShifted(-excess) };
        return now;
    }

    /// <summary>A contact delta (DIP, positive toward the content end) accumulated onto the contact's origin.</summary>
    public void ContactDelta(double t, double delta)
    {
        if (_slots is null) return;
        _contactOrigin += delta;
        ContactSample(t, _contactOrigin);
    }

    /// <summary>The contact lifted at <paramref name="t"/>. <paramref name="detectedAt"/> (plan clock) is when the lift
    /// was DETECTED when that is later than the lift itself — the touchpad wheel fallback only knows after its packet
    /// silence and stamps the End at the last packet: the fling is then authored from the contact ring at the lift time
    /// and evaluated consistently from the detection instant, starting at the position SHOWN then (no forward jump) —
    /// see <see cref="PlanAuthor.FollowEnd"/>. Omitted (NaN) for a source that delivers its End at the lift: authored at
    /// <paramref name="t"/>, deterministic in the event script whatever the frame rate.
    /// <para><paramref name="release"/> is the producer's verdict (<see cref="ContactRelease"/>). A PRESENT-clock contact
    /// that carries one (DirectManipulation) is released from its NEWEST REAL SAMPLE: DM stops producing motion at the
    /// physical lift and raises the status edge the End is stamped at one to five frames later, so the release is
    /// authored with <c>tLift</c> = the ring's newest sample and <c>tNow</c> = the End stamp — the velocity the fingers
    /// had, decayed over that latency, starting at the held newest sample. Device-clock streams and a verdict-less End
    /// keep <c>tLift = t</c> and the time rule.</para></summary>
    public void ContactEnd(double t, double detectedAt = double.NaN, ContactRelease release = ContactRelease.Unknown)
    {
        if (_slots is null) return;
        ScrollPlan prev = Plan;
        if (prev.Kind != MotionKind.Drag) return;
        t = PlaceContactTime(ref prev, t);
        double tNow = double.IsNaN(detectedAt) ? t : Math.Max(t, detectedAt);
        double tLift = prev.Clock == ContactClock.Present && release != ContactRelease.Unknown && prev.Ring.Count > 0
            ? prev.Ring.LastT   // <= t: PlaceContactTime never places a Present stamp before the newest sample
            : t;
        double from = prev.Eval(tNow, out _, out _);
        ScrollPlan next = PlanAuthor.FollowEnd(in prev, tLift, tNow, ScrollTunables.Current, release);
        t = tNow;
        if (!_snap.IsEmpty)
        {
            next = SnapTargets.ResolveFling(in next, in _snap, from, ScrollTunables.Current);
            if (next.Kind == MotionKind.Idle)
            {
                // A lift below the fling threshold still lands on the grid (a page shelf never rests between pages).
                double target = Math.Clamp(SnapTargets.Target(from, in _snap, impulse: false, from), next.Min, next.Max);
                if (Math.Abs(target - from) > 0.5) next = PlanAuthor.Glide(in next, t, target, ScrollTunables.Current);
            }
        }
        Publish(in next, t);
    }

    /// <summary>Drops a live contact without a fling (a cancelled pan, a swipe that won the contact, a hand-off to
    /// another scroller): settles in place, or springs back to the edge from an overpan.</summary>
    public void ContactCancel(double t)
    {
        if (_slots is null) return;
        _restore.Cancel();
        ScrollPlan prev = Plan;
        if (prev.Kind != MotionKind.Drag) return;
        t = PlaceContactTime(ref prev, t);
        Publish(PlanAuthor.FollowCancel(in prev, t, ScrollTunables.Current), t);
    }

    /// <summary>Scrollbar thumb: hold at the mapped position.</summary>
    public void ThumbTo(double t, double pos)
    {
        if (_slots is null) return;
        _restore.Cancel();
        t = AnchorAt(t);
        ScrollPlan next = PlanAuthor.Thumb(Plan, t, pos);
        Publish(in next, t);
    }

    /// <summary>Keyboard move (line/page/home/end) as a glide.</summary>
    public void Key(double t, KeyMove move)
    {
        if (_slots is null) return;
        _restore.Cancel();
        t = AnchorAt(t);
        ScrollPlan next = PlanAuthor.Key(Plan, t, move, ScrollTunables.Current, _viewport);
        Publish(in next, t);
    }

    /// <summary>Stops any live motion at the displayed position (a pointer-down over a coasting list).</summary>
    public void Stop(double t)
    {
        if (_slots is null) return;
        _restore.Cancel();
        ScrollPlan prev = Plan;
        if (prev.Kind == MotionKind.Idle) return;
        double p = Math.Clamp(DisplayedAt(t), prev.Min, prev.Max);
        Publish(PlanAuthor.Immediate(in prev, t, p), t);
    }

    /// <summary>The offset the user is looking at: the latest pose (the frame step's present-time evaluation, or the
    /// render poser's feedback), else — before any pose — the plan at <paramref name="t"/>. Grabbing a moving list
    /// (a finger or a click landing on a coast/glide, a touchpad contact beginning) holds HERE: frames already posed
    /// for later present times showed the content further along, so evaluating the plan at the input's own earlier
    /// time would step the content back against its motion.</summary>
    private double DisplayedAt(double t) => _shownValid ? _lastShown : Plan.Eval(t, out _, out _);

    /// <summary>The anchor time of a re-plan that continues the live plan from input time <paramref name="t"/>:
    /// <c>max(t, pose floor)</c> — never earlier than a present the user has already been shown (see <see cref="Bind"/>).</summary>
    private double AnchorAt(double t)
    {
        double floor = _shownFloorSec();
        return floor > t ? floor : t;
    }

    /// <summary>Host per-frame settle rule: an open-ended arc (fling decay, glide) that has come within the feel's
    /// settle epsilon of its destination is replaced by an Idle hold there, so the poser's <c>HasActive</c> and the
    /// motion signal fall quiet. Returns true when a hold was written.</summary>
    public bool SettleIfDue(double t)
    {
        if (_slots is null) return false;
        ScrollPlan prev = Plan;
        if (prev.Kind is MotionKind.Idle or MotionKind.Drag) return false;
        double p = prev.Eval(t, out double v, out bool settled);
        MotionFeel feel = ScrollTunables.Current;
        bool due = settled;
        bool openCoast = prev.Count == 1 && prev.S0.Kind == SegKind.Decay;
        if (!due && openCoast) due = Math.Abs(v) < feel.SettleVelocity;
        else if (!due && prev.Count > 0)
        {
            double dest = Math.Clamp(prev.Dest, prev.Min, prev.Max);
            due = Math.Abs(v) < feel.SettleVelocity && Math.Abs(p - dest) < feel.SettleEpsilonDip;
        }
        if (!due) return false;
        // A Cubic/Glide/Spring rests at its destination (it is within the epsilon anyway); an open Decay rests where it
        // IS — the asymptote may still be a few DIP away at the settle velocity and a jump there would read as a pop.
        double rest = Math.Clamp(openCoast ? p : (prev.Count > 0 ? prev.Dest : p), prev.Min, prev.Max);
        var hold = new MotionSeg(SegKind.Hold, double.NegativeInfinity, double.PositiveInfinity, rest, rest);
        var idle = new ScrollPlan(hold, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.None, MotionKind.Idle, default);
        _slots.Write(Vp, in idle);
        return true;
    }

    // ── feedback ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Host calls once per UI frame with the poser's last feedback for this viewport (UI thread).
    /// Updates <see cref="Offset"/> and <see cref="Motion"/>; equal values coalesce (no notify).</summary>
    public void ApplyFeedback(in ScrollPoseFeedback f) => ApplyShown(f.Shown, f.Velocity, f.Settled ? MotionKind.Idle : f.Kind, f.Settled);

    /// <summary>The UI-side equivalent of <see cref="ApplyFeedback"/> from the plan itself (the host's frame step
    /// when the poser has not yet posed this viewport).</summary>
    public void ApplyShown(double shown, double velocity, MotionKind kind, bool settled)
    {
        _lastShown = shown;
        _shownValid = true;
        _offset.Value = shown;
        MotionKind k = settled ? MotionKind.Idle : kind;
        float speed = settled ? 0f : (float)Math.Abs(velocity);
        bool user = !settled && IsUserKind(kind);
        _motion.Value = new ScrollMotionState(k, speed, user);
        _atStart.Value = shown <= 0.5;
        _atEnd.Value = shown >= MaxOffset - 0.5;
    }

    /// <summary>Convenience: pulls this viewport's feedback from <paramref name="poser"/> and applies it.</summary>
    public void PullFeedback(ScrollPoser poser)
    {
        if (poser.TryGetFeedback(Vp, out ScrollPoseFeedback f)) ApplyFeedback(in f);
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────────────────────

    private void Publish(in ScrollPlan plan, double t)
    {
        _slots!.Write(Vp, in plan);
        if (ScrollProbe.Level == ProbeLevel.Trace)
        {
            double start = plan.Eval(t, out _, out _);
            double durationS = plan.Count > 0 ? plan.S0.T1 - plan.S0.T0 : 0.0;
            ScrollProbe.Plan(Vp.Node, plan.Seq, (byte)plan.Kind, (long)(t * System.Diagnostics.Stopwatch.Frequency), start, plan.Dest, durationS);
        }
    }

    private static bool IsUserKind(MotionKind k) => k is MotionKind.Wheel or MotionKind.Drag or MotionKind.Fling or MotionKind.Thumb;

    private static bool IsUserDriven(in ScrollPlan plan, double t)
    {
        if (!IsUserKind(plan.Kind)) return false;
        plan.Eval(t, out _, out bool settled);
        return !settled;
    }
}
