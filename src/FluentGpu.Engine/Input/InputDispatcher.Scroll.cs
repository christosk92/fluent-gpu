using System;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Input;

/// <summary>
/// The dispatcher's scroll front end (scroll rework §4): every scroll input — a wheel notch, a hi-res wheel packet (a
/// fractional notch), a touchpad contact stream, a touch/pen pan, a scrollbar grab, a keyboard step — resolves its viewport through the
/// pure <see cref="ScrollRouter"/> (this class is its <see cref="IScrollerQuery"/> over the live scene) and drives that
/// viewport's <see cref="ScrollHandle"/>, which authors the plan and writes the plan table. Nothing here integrates a
/// position or touches a scene offset: the host's frame step and the render poser read the plan.
/// </summary>
public sealed partial class InputDispatcher : IScrollerQuery
{
    /// <summary>Host wiring: the viewport node's <see cref="ScrollHandle"/> (the host creates one on first use).</summary>
    public Func<NodeHandle, ScrollHandle?>? ScrollHandleFor { get; set; }

    /// <summary>Host wiring: the plan clock's "now" (QPC seconds on a real window, the deterministic frame clock headless).</summary>
    public Func<double>? ScrollNowSec { get; set; }

    /// <summary>Host wiring: a device QPC stamp → plan-clock seconds (0 stamp ⇒ now).</summary>
    public Func<long, double>? ScrollQpcToSec { get; set; }

    /// <summary>Host wiring: a FRAME-CLOCK QPC stamp (a lattice present, <see cref="ContactStamp.ForFrame"/> — what a
    /// composition-timed <see cref="ScrollInputEvent.PresentTimed"/> stream carries) → plan-clock seconds. The frame clock and the plan clock
    /// are the same timeline in every host (QPC on a real window, the deterministic frame clock headless), which a device
    /// stamp is not headless.</summary>
    public Func<long, double>? ScrollFrameQpcToSec { get; set; }

    /// <summary>"A scroll gesture started" (SwipeControl auto-dismiss) — fired when a wheel/contact latches a scroller.</summary>
    public Action? OnScrollGestureStarted { get; set; }

    /// <summary>The scrollbar conscious-fade ticker, host-owned. Hover/reveal notifications forward to it; null = the
    /// bars never reveal.</summary>
    public ScrollBarChrome? Chrome { get; set; }

    private ScrollRouter? _scrollRouter;
    private ScrollRouter Router => _scrollRouter ??= new ScrollRouter(this);

    // Live contacts (touch/pen pans + the touchpad contact stream), keyed by pointer id — a handful at most.
    private struct ScrollContact
    {
        public bool Live;
        public uint PointerId;
        public NodeHandle Vp;
        public bool Horizontal;
        public double OriginOffset;   // content offset when the contact began
        public float AnchorAxis;      // pointer main-axis position at claim (pan)
        public float LastAxis;        // pointer main-axis position at the last sample (pan): deltas map 1:1 onto content
        public bool DeltaStream;      // a delta-carrying stream (touchpad) vs a positional pan
        // Event-clock → plan-clock mapping for a positional pan, latched at the claim: the DOWN's plan-clock time and its
        // raw device stamps. Every later sample is placed at DownSec + (its stamp − the down's stamp), so the contact ring
        // keeps the DEVICE's sample spacing (the release velocity is a property of the finger, not of when frames ran).
        public double DownSec;
        public long DownQpc;
        public uint DownMs;
        // A delta stream (touchpad) opens unrouted at its Begin; the first movement routes it.
        public double BeginSec;
        public Point2 BeginPointer;
        public ContactClock Clock;    // the stream's sample clock (a composition-timed DirectManipulation stream = Present)
    }

    private readonly ScrollContact[] _scrollContacts = new ScrollContact[8];

    /// <summary>True while a latched wheel gesture or a live contact is driving a scroller (gate observability).</summary>
    public bool GestureActive
    {
        get
        {
            if (_scrollRouter is { LatchedScroller: >= 0 }) return true;
            for (int i = 0; i < _scrollContacts.Length; i++) if (_scrollContacts[i].Live) return true;
            return false;
        }
    }

    private double NowSec => ScrollNowSec?.Invoke() ?? 0.0;

    private double SecOf(long qpc) => qpc != 0 && ScrollQpcToSec is { } f ? f(qpc) : NowSec;

    private double SecOf(in ScrollInputEvent e)
        => e.PresentTimed && e.Qpc != 0 && ScrollFrameQpcToSec is { } f ? f(e.Qpc) : SecOf(e.Qpc);

    private ScrollHandle? HandleOf(NodeHandle vp)
        => vp.IsNull || !_scene.IsLive(vp) || !_scene.HasScroll(vp) ? null : ScrollHandleFor?.Invoke(vp);

    // ── IScrollerQuery ──────────────────────────────────────────────────────────────────────────────────────────

    int IScrollerQuery.ParentScroller(int vp)
    {
        var h = _scene.HandleAt(vp);
        if (h.IsNull || !_scene.IsLive(h)) return -1;
        for (var p = _scene.Parent(h); !p.IsNull; p = _scene.Parent(p))
            if ((_scene.Flags(p) & NodeFlags.Scrollable) != 0 && _scene.HasScroll(p)) return (int)p.Raw.Index;
        return -1;
    }

    uint IScrollerQuery.Identity(int vp)
    {
        var h = _scene.HandleAt(vp);
        if (h.IsNull || !_scene.IsLive(h) || !_scene.HasScroll(h) || (_scene.Flags(h) & NodeFlags.Parked) != 0) return 0;
        return h.Raw.Gen;
    }

    bool IScrollerQuery.CanMove(int vp, bool horizontal, int sign, double tNow)
    {
        var h = _scene.HandleAt(vp);
        if (h.IsNull || !_scene.IsLive(h) || !_scene.HasScroll(h)) return false;
        ref ScrollState sc = ref _scene.ScrollRef(h);
        if ((sc.Orientation == 1) != horizontal) return false;
        double max = sc.MaxOffset;
        if (max <= 0.5) return false;
        var handle = ScrollHandleFor?.Invoke(h);
        double p = handle is not null ? handle.EvalAt(tNow, out _, out _) : sc.Offset;
        return sign > 0 ? p < max - 0.5 : p > 0.5;
    }

    // ── the one scroll input entry ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Dispatches one scroll input (from the urgent sink or the ring). Returns true when a scroller took it.</summary>
    public bool DispatchScroll(in ScrollInputEvent e)
    {
        ScrollProbe.Input((ScrollSourceCode)(byte)e.Source, (byte)e.Phase, e.ArrivalQpc != 0 ? e.ArrivalQpc : e.Qpc, e.Dx, e.Dy, -1,
            e.Phase == ScrollGesture.End ? (byte)e.Release : (byte)0);
        double t = SecOf(in e);
        switch (e.Source)
        {
            case ScrollSource.MouseWheel:
            case ScrollSource.MouseWheelHiRes:   // a hi-res wheel packet is a fractional notch — a mouse wheel has no lift
                return DispatchWheelNotch(in e, t);
            case ScrollSource.Touchpad:
            case ScrollSource.Touch:
            case ScrollSource.Pen:
                return DispatchContactStream(in e, t);
            default:
                return false;
        }
    }

    private bool DispatchWheelNotch(in ScrollInputEvent e, double t)
    {
        // Element-level wheel handlers (WinUI PointerWheelChanged) see the wheel BEFORE the viewport: a Handled
        // NumberBox consumes the step instead of scrolling the form (NumberBox.cpp:578-597).
        if (DispatchWheel(in e, t)) return true;
        // App zoom (browser Ctrl+wheel): after element first-refusal, before the viewport. The hook speaks the device
        // convention (>0 = rotated away = zoom in), the event "positive = toward the content end" — WheelClassifier.ZoomNotches
        // is the one conversion. A horizontal-only notch never zooms (it scrolls, Ctrl or not).
        if ((e.Mods & KeyModifiers.Ctrl) != 0 && OnZoomWheel is { } zoomWheel
            && WheelClassifier.ZoomNotches(in e) is var zoom && zoom != 0f && zoomWheel(zoom)) return true;
        // Header → list routing (Element.WheelTarget).
        if (RouteWheelTarget(in e, t)) return true;

        bool horizontal = (e.Dy == 0f && e.Dx != 0f) || (e.Mods & KeyModifiers.Shift) != 0;
        float notches = horizontal ? (e.Dx != 0f ? e.Dx : e.Dy) : e.Dy;
        if (notches == 0f) return false;
        int sign = notches > 0f ? 1 : -1;
        NodeHandle hit = ResolveScrollTarget(e.PointerDip, horizontal);
        bool hadLatch = Router.LatchedScroller >= 0;
        var decision = Router.Decide(hit.IsNull ? -1 : (int)hit.Raw.Index, horizontal, sign, t, e.Source, ScrollGesture.Notch, e.Mods);
        if (decision.IsNone) return false;
        var vp = _scene.HandleAt(decision.Vp);
        var handle = HandleOf(vp);
        if (handle is null) return false;
        if (!hadLatch) OnScrollGestureStarted?.Invoke();
        handle.Wheel(t, notches);
        Chrome?.NotifyMoved(decision.Vp);
        return true;
    }

    private bool DispatchContactStream(in ScrollInputEvent e, double t)
    {
        int slot = FindScrollContact(e.PointerId);
        if (e.Phase == ScrollGesture.End)
        {
            if (slot < 0) return false;
            if (_scrollContacts[slot].Vp.IsNull) { _scrollContacts[slot] = default; return true; }   // never moved: nothing to end
            EndScrollContact(slot, t, cancel: false, liftDetectedLate: e.LiftDetectedLate, release: e.Release);
            return true;
        }

        if (slot < 0)
        {
            // A Begin (or a sample with no live contact) opens the contact UNROUTED: the axis and the direction — which
            // decide the scroller (a horizontal shelf vs the page, an inner list at its edge vs its parent) — are only
            // known from the first movement, and a DirectManipulation Begin carries none.
            slot = AllocScrollContact(e.PointerId);
            if (slot < 0) return false;
            ref ScrollContact fresh = ref _scrollContacts[slot];
            fresh.DeltaStream = true;
            fresh.BeginSec = t;
            fresh.BeginPointer = e.PointerDip;
            fresh.Clock = e.PresentTimed ? ContactClock.Present : ContactClock.Device;
        }

        ref ScrollContact live = ref _scrollContacts[slot];
        float dx = e.Dx, dy = e.Dy;

        if (live.Vp.IsNull)
        {
            if (dx == 0f && dy == 0f) return true;   // no movement yet: stay unrouted
            // Axis lock from the first movement: its dominant axis.
            bool horizontal = MathF.Abs(dx) > MathF.Abs(dy);
            float first = horizontal ? (dx != 0f ? dx : dy) : dy;
            if (first == 0f) return true;
            NodeHandle hit = ResolveScrollTarget(live.BeginPointer, horizontal);
            var decision = Router.Decide(hit.IsNull ? -1 : (int)hit.Raw.Index, horizontal, first > 0f ? 1 : -1, t, e.Source,
                ScrollGesture.Begin, e.Mods);
            if (decision.IsNone) return false;
            var vp = _scene.HandleAt(decision.Vp);
            var handle = HandleOf(vp);
            if (handle is null) return false;
            live.Vp = vp;
            live.Horizontal = decision.Horizontal;
            handle.ContactBeginHere(live.BeginSec, live.Clock);
            live.OriginOffset = handle.OffsetNow;
            OnScrollGestureStarted?.Invoke();
        }

        // No movement on the locked axis is not a position sample: a contact stream reports MOVEMENT, and a report that
        // moved nothing (a cross-axis wobble) carries no new position. Whether the fingers have stopped is a time rule
        // at the lift (PlanAuthor.StoppedAfterS — no position change for a couple of report periods), never a sample
        // stamped with an unchanged position, which would read as a velocity collapse the finger never made.
        float delta = live.Horizontal ? dx : dy;
        if (delta == 0f) return true;
        var h = HandleOf(live.Vp);
        if (h is null) { live = default; return false; }
        // Mid-stream chaining: a contact whose scroller is pinned at the edge it started at hands the delta up. The
        // scroller left behind settles where it is (a hand-off is not a release — it must not fling).
        int dir = delta > 0f ? 1 : -1;
        var re = Router.Decide((int)live.Vp.Raw.Index, live.Horizontal, dir, t, e.Source, ScrollGesture.Sample, e.Mods);
        if (!re.IsNone && re.Vp != (int)live.Vp.Raw.Index)
        {
            var other = HandleOf(_scene.HandleAt(re.Vp));
            if (other is not null)
            {
                h.ContactCancel(t);
                live.Vp = _scene.HandleAt(re.Vp);
                h = other;
                h.ContactBeginHere(t, live.Clock);
            }
        }
        h.ContactDelta(t, delta);
        Chrome?.NotifyMoved((int)live.Vp.Raw.Index);
        return true;
    }

    // ── touch/pen pan (the dispatcher's arena-claimed pan drives a contact positionally) ────────────────────────

    /// <summary>The arena claimed a pan on <paramref name="vp"/>: open the contact at the DOWN event's position/time.</summary>
    private double _panClaimDownSec;   // the pan candidate's DOWN on the plan clock (InputDispatcher.TouchDown records it)

    private void PanClaimed(NodeHandle vp, bool horizontal, in InputEvent down, double tDown = double.NaN)
    {
        var handle = HandleOf(vp);
        if (handle is null) return;
        int slot = FindScrollContact(down.PointerId);
        if (slot < 0) slot = AllocScrollContact(down.PointerId);
        if (slot < 0) return;
        double t = double.IsNaN(tDown) ? SecOf(down.QpcTicks) : tDown;
        ref ScrollContact c = ref _scrollContacts[slot];
        c.Live = true; c.Vp = vp; c.Horizontal = horizontal; c.DeltaStream = false;
        c.DownSec = t; c.DownQpc = down.QpcTicks; c.DownMs = down.TimestampMs;
        c.AnchorAxis = horizontal ? down.PositionPx.X : down.PositionPx.Y;
        c.LastAxis = c.AnchorAxis;
        c.OriginOffset = handle.EvalAt(t, out _, out _);
        handle.ContactBegin(t, c.OriginOffset);
        Router.Decide((int)vp.Raw.Index, horizontal, 1, t, ScrollSource.Touch, ScrollGesture.Begin, KeyModifiers.None);
        OnScrollGestureStarted?.Invoke();
    }

    /// <summary>A pointer move on a claimed pan: the content follows the finger 1:1 (position = origin − travel).</summary>
    private void PanSample(in InputEvent e)
    {
        int slot = FindScrollContact(e.PointerId);
        if (slot < 0) return;
        ref ScrollContact c = ref _scrollContacts[slot];
        var handle = HandleOf(c.Vp);
        if (handle is null) { c = default; return; }
        double t = ContactSec(in c, in e);
        float axis = c.Horizontal ? e.PositionPx.X : e.PositionPx.Y;
        handle.ContactDelta(t, -(axis - c.LastAxis));   // 1:1: the finger moving DOWN scrolls the content UP (offset decreases)
        c.LastAxis = axis;
        Chrome?.NotifyMoved((int)c.Vp.Raw.Index);
    }

    /// <summary>The contact lifted (fling) or was cancelled (settle in place) by <paramref name="e"/>: the end sample
    /// sits on the same device-stamp timeline as the contact's moves.</summary>
    private void PanEnd(in InputEvent e, bool cancel)
    {
        int slot = FindScrollContact(e.PointerId);
        if (slot < 0) return;
        EndScrollContact(slot, ContactSec(in _scrollContacts[slot], in e), cancel);
    }

    /// <summary>The contact was cancelled without an input event of its own (arena reset, capture loss): settle in
    /// place at the plan clock's now.</summary>
    private void PanCancel(uint pointerId)
    {
        int slot = FindScrollContact(pointerId);
        if (slot < 0) return;
        EndScrollContact(slot, NowSec, cancel: true);
    }

    /// <summary>A positional pan sample's plan-clock time: the claim's plan-clock DOWN time plus the device-clock interval
    /// since the down (QPC when both stamps carry it, else the millisecond stamp — wrap-safe unsigned difference).</summary>
    private static double ContactSec(in ScrollContact c, in InputEvent e)
    {
        if (e.QpcTicks != 0 && c.DownQpc != 0)
            return c.DownSec + (e.QpcTicks - c.DownQpc) / (double)System.Diagnostics.Stopwatch.Frequency;
        return c.DownSec + unchecked((int)(e.TimestampMs - c.DownMs)) * 1e-3;
    }

    private void EndScrollContact(int slot, double t, bool cancel, bool liftDetectedLate = false,
        ContactRelease release = ContactRelease.Unknown)
    {
        ref ScrollContact c = ref _scrollContacts[slot];
        var handle = HandleOf(c.Vp);
        if (handle is not null)
        {
            // A late-detected lift (ScrollInputEvent.LiftDetectedLate — the touchpad wheel fallback's packet silence) is
            // authored at NOW from what is shown now (ScrollHandle.ContactEnd); a prompt one at its own event time —
            // deterministic in the event script, whatever the frame rate. The producer's release verdict
            // (ScrollInputEvent.Release — DirectManipulation's INERTIA/READY edge) rides along to the author.
            if (cancel) handle.ContactCancel(t);
            else if (liftDetectedLate) handle.ContactEnd(t, NowSec, release);
            else handle.ContactEnd(t, release: release);
            Router.Decide((int)c.Vp.Raw.Index, c.Horizontal, 1, t, ScrollSource.Touch, ScrollGesture.End, KeyModifiers.None);
        }
        c = default;
    }

    private int FindScrollContact(uint pointerId)
    {
        for (int i = 0; i < _scrollContacts.Length; i++)
            if (_scrollContacts[i].Live && _scrollContacts[i].PointerId == pointerId) return i;
        return -1;
    }

    private int AllocScrollContact(uint pointerId)
    {
        for (int i = 0; i < _scrollContacts.Length; i++)
            if (!_scrollContacts[i].Live) { _scrollContacts[i] = default; _scrollContacts[i].PointerId = pointerId; _scrollContacts[i].Live = true; return i; }
        return -1;
    }

    // ── programmatic paths the dispatcher itself drives ─────────────────────────────────────────────────────────

    /// <summary>A pointer-down over a coasting viewport takes authoritative control: stop its motion in place.</summary>
    private void ScrollStop(NodeHandle vp) => HandleOf(vp)?.Stop(NowSec);

    /// <summary>Stop the scroller under <paramref name="p"/> (a press lands over a coasting list).</summary>
    private void ScrollStopAt(Point2 p)
    {
        var vp = ScrollableUnder(p);
        if (!vp.IsNull) ScrollStop(vp);
    }

    /// <summary>Scrollbar thumb drag: hold at the mapped offset.</summary>
    private void ThumbSet(NodeHandle vp, double offset) => HandleOf(vp)?.ThumbTo(NowSec, offset);

    /// <summary>Scrollbar track click (page) / arrow button (line): a glide.</summary>
    private void ScrollBy(NodeHandle vp, double delta, ScrollMove move = ScrollMove.Glide) => HandleOf(vp)?.ScrollBy(delta, move);

    /// <summary>Pinch-zoom commit: the viewport's zoom factor plus the focal-preserving offset (the content point under
    /// the gesture midpoint stays put).</summary>
    private void SetZoom(NodeHandle vp, float zoom, float focalLocal)
    {
        var handle = HandleOf(vp);
        if (handle is null) return;
        ref ScrollState sc = ref _scene.ScrollRef(vp);
        float oldZ = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        if (zoom == oldZ) return;
        double p = handle.OffsetNow;
        double next = (p + focalLocal) * (zoom / oldZ) - focalLocal;
        sc.ZoomFactor = zoom;
        handle.SetExtent(sc.ContentMain * zoom, sc.ViewportMain);
        handle.ScrollTo(next, ScrollMove.Immediate);
        _scene.Mark(vp, NodeFlags.PaintDirty);
    }

    /// <summary>Drag-and-drop edge auto-scroll: a held VELOCITY (<paramref name="dipPerS"/> along the axis; 0 = stop in
    /// place), authored as the handle's closed-form constant-velocity plan that runs on to the edge by itself.
    /// <see cref="DragDropContext"/> posts only when the velocity or the viewport changes, so a still pointer in the edge
    /// zone posts once: a one-frame step here would move the list once and then stop.</summary>
    private void AutoScroll(NodeHandle vp, float dipPerS) => HandleOf(vp)?.AutoScroll(dipPerS);

    /// <summary>Keyboard scroll: arrows/PageUp/PageDown/Home/End glide the nearest scrollable of the focused node.</summary>
    private bool ScrollKey(int key, NodeHandle vp)
    {
        var handle = HandleOf(vp);
        if (handle is null) return false;
        ref ScrollState sc = ref _scene.ScrollRef(vp);
        if (!ScrollRouter.TryMapKey(key, sc.Orientation == 1, out KeyMove move)) return false;
        if (sc.MaxOffset <= 0.5) return false;
        handle.Key(NowSec, move);
        Chrome?.NotifyMoved((int)vp.Raw.Index);
        return true;
    }

    /// <summary>Drops the wheel latch (focus loss / window deactivate).</summary>
    public void ResetScrollLatch() => _scrollRouter?.ResetLatch();

    // Hi-res (free-spin) wheel packets are fractional notches; element handlers that act once per detent read whole
    // detents (WheelEventArgs.Steps) from this carryover, kept in raw WHEEL_DELTA units like the PAL's detented one.
    private int _wheelStepAccumX, _wheelStepAccumY;
    private double _wheelStepLastSec = double.NegativeInfinity;

    private void WheelSteps(in ScrollInputEvent e, double t, out int steps, out int stepsX)
    {
        bool hiRes = e.Source == ScrollSource.MouseWheelHiRes;
        if (!hiRes || t - _wheelStepLastSec > WheelClassifier.GestureGapMs / 1000.0) { _wheelStepAccumX = 0; _wheelStepAccumY = 0; }
        _wheelStepLastSec = t;
        if (!hiRes)
        {
            steps = e.Dy > 0f ? 1 : e.Dy < 0f ? -1 : 0;   // a detented event is one step whatever the wheel-lines scale
            stepsX = e.Dx > 0f ? 1 : e.Dx < 0f ? -1 : 0;
            return;
        }
        steps = WheelStep(ref _wheelStepAccumY, e.Dy);
        stepsX = WheelStep(ref _wheelStepAccumX, e.Dx);
    }

    private static int WheelStep(ref int accum, float notches)
    {
        int raw = (int)MathF.Round(notches * WheelClassifier.DeltaPerNotch);
        if ((raw > 0 && accum < 0) || (raw < 0 && accum > 0)) accum = 0;   // a reversal restarts the detent
        return WheelClassifier.Carryover(ref accum, raw);
    }

    /// <summary>Element-level wheel routing (WinUI PointerWheelChanged bubbling): every enabled WheelBit handler up the
    /// chain sees the event until one sets Handled, which also stops the enclosing viewport from scrolling. Deltas are
    /// reported in DIP (notches × the feel's notch distance), positive toward the content end; Steps are the whole
    /// detents the event completes (a hi-res wheel's fractional packets accumulate to them).</summary>
    private bool DispatchWheel(in ScrollInputEvent e, double t)
    {
        WheelSteps(in e, t, out int steps, out int stepsX);
        WheelEventArgs? args = null;
        float dip = (float)ScrollTunables.Current.WheelNotchDip;
        for (var n = HitTestAny(e.PointerDip); !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.WheelBit) == 0) continue;
            args ??= new WheelEventArgs { Delta = e.Dy * dip, DeltaX = e.Dx * dip, Steps = steps, StepsX = stepsX, Mods = e.Mods };
            args.Local = LocalPos(n, e.PointerDip);
            _scene.GetPointerWheel(n)?.Invoke(args);
            if (args.Handled) return true;
        }
        return false;
    }

    /// <summary><c>Element.WheelTarget</c> routing: walk the hit chain leaf→root for the nearest node naming a wheel
    /// target, stopping at the first scrollable ancestor: a notch over a list's OWN rows belongs to the router's
    /// same-axis / at-edge resolution, never to a header target further up.</summary>
    private bool RouteWheelTarget(in ScrollInputEvent e, double t)
    {
        if (_scene.WheelTargetCount == 0) return false;
        float notches = e.Dy != 0f ? e.Dy : e.Dx;
        if (notches == 0f) return false;
        for (var n = HitTestAny(e.PointerDip); !n.IsNull; n = _scene.Parent(n))
        {
            var flags = _scene.Flags(n);
            if ((flags & NodeFlags.Scrollable) != 0) return false;
            if ((flags & NodeFlags.Disabled) != 0) continue;
            if (!_scene.TryGetWheelTarget(n, out var target)) continue;
            target.Wheel(t, notches);
            return true;
        }
        return false;
    }
}
