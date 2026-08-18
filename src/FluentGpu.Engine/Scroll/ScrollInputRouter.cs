using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;

namespace FluentGpu.Scroll;

/// <summary>
/// Scroll v3 WP-C (docs/plans/scroll-v3-plan-2026-08-17.md §3.4): the device-blind translation layer between
/// <see cref="InputDispatcher"/> and the portable <see cref="ScrollKernel"/>. Owns NOTHING the kernel owns — no
/// physics, no offset, no phase/activity state beyond the couple of scalars needed to LATCH a gesture and accumulate
/// one frame's worth of delta before posting it. Every real decision (clamp, band, chase, fling seed, chaining
/// integration) happens inside <see cref="ScrollKernel.Tick"/>; this class only decides WHICH node a raw input event
/// is FOR and translates it into one <see cref="ScrollInput"/> command per event (or, for frame-aligned producers,
/// one per frame — see <see cref="EndFrame"/>).
///
/// <para><b>Hit-test bridge (plan ambiguity, resolved here):</b> the pinned ctor is <c>(SceneStore, ScrollKernel)</c>
/// only — no <see cref="InputDispatcher"/> reference — yet <see cref="Wheel"/>/<see cref="Phase"/>/<see cref="CancelAt"/>
/// must resolve a window-space point to a scrollable node, which requires <c>InputDispatcher</c>'s private geometric
/// hit-test (transform/clip-stack walk; <c>HitTestAny</c> + <c>ScrollableUnderForAxis</c>/<c>ContainingScrollerForAxis</c>).
/// Reproducing that walk here would duplicate ~80 lines of transform/clip machinery this class has no business owning.
/// The resolution: two settable delegate properties (<see cref="ResolveAxisTarget"/>, <see cref="ResolveAnyTarget"/>),
/// wired once by <c>InputDispatcher.Scroll</c>'s property setter after both objects exist (AppHost's construction
/// order in plan §3.3.5: kernel → router → <c>_dispatcher.Scroll = router</c>). Not part of the pinned public surface;
/// the router is inert (resolves nothing, i.e. <see cref="Wheel"/>/<see cref="Phase"/>/<see cref="CancelAt"/> no-op)
/// until wired — same shape as the dispatcher's existing <c>OnScrollArmed</c>-style delegate seams.</para>
///
/// <para><b>Axis-position sign convention (plan ambiguity, resolved here):</b> <see cref="ScrollKernel"/>'s Drag
/// integration is <c>raw = anchor + (resample(hist, t) − x0)</c> (plan §2.2) — i.e. INCREASING axis position increases
/// the raw offset. The legacy per-node touch-pan writer (<c>RecordTouchPanSample</c>, the scratchpad snapshot at
/// :2048-2056) computed <c>PendingRawOffset = anchor − panDelta</c> where <c>panDelta = pointerPos − anchorPos</c> —
/// i.e. dragging content DOWN (pointer position increases) DECREASES the offset (WinUI/iOS: the content follows the
/// finger, so scrolling toward the content's end requires the finger moving toward its START). To reproduce that sign
/// under the kernel's `anchor + (x − x0)` formula without the kernel knowing about drag direction, <see cref="AxisPosDip"/>
/// feeds the NEGATED pointer coordinate: with x0 = −beginPos and x = −curPos, `anchor + (x − x0) = anchor + (beginPos −
/// curPos) = anchor − panDelta` — byte-identical to the legacy formula. Reused for the direct-touch contact path
/// (<see cref="PanClaimed"/>/<see cref="PanSample"/>/<see cref="PanEnd"/>) only; the frame-delta producer path
/// (<see cref="Phase"/>, DM RUNNING / hi-res fallback) uses <see cref="InputEvent.ScrollDelta"/>/<see cref="InputEvent.ScrollDeltaX"/>
/// directly — those are already stamped "positive = scroll toward the content end" at the PRODUCER (Pal.cs XML doc),
/// so no negation is needed there.</para>
///
/// <para><b>Wheel bubbling (documented simplification, plan §12 risk accepted):</b> the legacy multi-candidate
/// ancestor-climb-with-per-candidate-edge-test (<c>ScrollAxis</c>, scratchpad :3725-3772) climbed PAST every same-axis
/// scroller pinned in the wheel's direction to find an outer one with room. The kernel's <c>WheelNotch</c> command is
/// fire-and-forget (no synchronous "did it move" the way the old <c>ScrollBy</c> returned), so the router can only
/// resolve ONE candidate (<see cref="ResolveAxisTarget"/>, which already climbs past viewports with NO overflow at
/// all) and test that ONE candidate's current <c>OffsetX/Y</c> against its extent for a direction-specific at-edge
/// refusal. A same-axis viewport with SOME overflow but pinned in exactly this wheel direction is accepted (not
/// bubbled past) — Phase 3 (plan §5.3) rewires the wheel classifier and can reintroduce full climbing if needed.</para>
/// </summary>
public sealed class ScrollInputRouter
{
    private readonly SceneStore _scene;
    private readonly ScrollKernel _kernel;
    private readonly ScrollCommandPort _port;

    public ScrollInputRouter(SceneStore scene, ScrollKernel kernel)
    {
        _scene = scene;
        _kernel = kernel;
        _port = kernel.Port;
    }

    /// <summary>Wiring-only hit-test bridge (see class remarks): resolve the nearest same-axis scrollable ancestor
    /// under a window-space point, wrapping <c>InputDispatcher.ResolveScrollTarget</c> (which itself wraps the kept
    /// <c>ScrollableUnderForAxis</c>/<c>ContainingScrollerForAxis</c> helpers). Set once by <c>InputDispatcher.Scroll</c>.</summary>
    public Func<Point2, bool, NodeHandle>? ResolveAxisTarget { get; set; }

    /// <summary>Wiring-only hit-test bridge: the nearest scrollable ancestor under a point, ANY axis — wraps
    /// <c>InputDispatcher.ScrollableUnder</c> (kept, public, used for hover/cancel today). Set once by <c>InputDispatcher.Scroll</c>.</summary>
    public Func<Point2, NodeHandle>? ResolveAnyTarget { get; set; }

    /// <summary>Wiring-only bridge (§A′ wheel fallback, class remarks / plan §12): run the SAME element-level
    /// <c>WheelBit</c> bubbling <c>InputDispatcher.DispatchWheel</c> uses for a real <see cref="InputKind.Wheel"/>,
    /// at a phase-gesture packet's point with that packet's delta — true iff an element consumed it. Set once by
    /// <c>InputDispatcher.Scroll</c>.</summary>
    public Func<Point2, float, float, bool>? DispatchElementWheel { get; set; }

    /// <summary>Fired once when a user scroll gesture STARTS on any viewport (touch pan claimed, phase producer latched,
    /// or a wheel notch accepted) — the "scroll started elsewhere" signal SwipeControl's auto-dismiss listens to
    /// (<c>Context.ScrollStartedObserved</c>). Wired by AppHost; null = nobody listening.</summary>
    public Action? OnGestureStarted { get; set; }

    // ── direct-touch / pen contacts (WM_POINTER): the dispatcher's touch arena already decided THAT this contact is a
    // pan and WHICH viewport+axis it drives (PanClaimed carries both); PanSample/PanEnd carry only the InputEvent (its
    // PointerId), so a fixed per-contact slab (mirrors InputDispatcher.PointerSlot's cap) remembers node+axis across
    // calls. Zero alloc after construction; a table-full 11th contact is dropped deterministically (matches the
    // dispatcher's own MaxContacts=10 policy — the arena never claims a pan on a contact it couldn't seat anyway).
    private const int MaxContacts = 10;
    private struct ContactSlot { public uint PointerId; public bool Used; public NodeHandle Node; public bool Horizontal; }
    private readonly ContactSlot[] _contacts = new ContactSlot[MaxContacts];

    // ── phase-tagged producer (DM RUNNING / hi-res wheel fallback): ONE latched gesture (single-gesture producer,
    // like the legacy _sg* singleton) — a fresh ScrollBegin always ends whatever was latched. Pre-latch accumulates the
    // WHOLE gesture's raw delta to pick + resolve an axis at the 8-DIP slop window; post-latch accumulates only THIS
    // FRAME's delta (flushed once per Dispatch batch by EndFrame — plan §3.4 "sum the frame's deltas → ONE FrameDelta").
    private const float LatchSlopDip = 8f;
    private NodeHandle _phaseNode;
    private bool _phaseHoriz;
    private bool _phaseLatched;
    private bool _phaseOpen;                     // a ScrollBegin opened a gesture that no End/Cancel/takeover has closed yet
    private bool _phaseDirty;                    // this frame produced a delta EndFrame must flush
    private float _phaseTotalX, _phaseTotalY;     // whole-gesture accumulation, pre-latch axis pick
    private float _phaseFrameX, _phaseFrameY;     // this-frame accumulation, post-latch (flushed by EndFrame)
    private double _phaseLastT;

    // §A′ (class remarks / plan §12 "Wave-6 Fix C"): the slop-crossing packet found NO scroller but an ELEMENT under
    // it consumed the wheel — that ownership decision is locked for the WHOLE gesture (never re-latch a viewport even
    // once a later packet travels over one), unlike a genuine miss (§A), which keeps retrying every packet.
    private bool _phaseElementOwnsWheel;

    private const float KeyGlideDip = 48f;

    // ── seconds on the sample clock (mirrors InputDispatcher.ImpulseVelocity.ToSec / ContactSampleSec): QPC when the
    // platform provides it (sub-ms), else the ms message time, else 0 (a truly vacuous — headless-default — stamp).
    private static double SampleSec(uint ms, long qpc)
    {
        long f = SystemParams.QpcFrequency;
        if (qpc != 0 && f > 0) return qpc / (double)f;
        return ms != 0 ? ms / 1000.0 : 0.0;
    }

    /// <summary>The kernel's raw-offset-space axis position for a window-DIP point (see class remarks on the sign
    /// convention): negated so the kernel's `anchor + (x − x0)` reproduces the legacy `anchor − panDelta`.</summary>
    private static float AxisPosDip(Point2 p, bool horizontal) => horizontal ? -p.X : -p.Y;

    /// <summary>Nearest same-axis ancestor VIEWPORT of <paramref name="from"/> (NOT including itself) — the drag-time
    /// chaining parent (plan §2.2: "ChainParent set by the router at ContactBegin/latch"). Pure scene walk; no hit-test
    /// bridge needed (ancestor chain, not a point).</summary>
    private NodeHandle NearestSameAxisAncestorViewport(NodeHandle from, bool horizontal)
    {
        for (var n = _scene.Parent(from); !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0 || !_scene.HasScroll(n)) continue;
            if ((_scene.ScrollRef(n).Orientation == 1) == horizontal) return n;
        }
        return NodeHandle.Null;
    }

    // ── touch/pen direct contacts ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dispatcher's touch arena resolved this contact's pan claim onto <paramref name="vp"/> (scroll-axis
    /// <paramref name="horizontal"/>) — begin a kernel contact and chain it to the nearest same-axis ancestor.</summary>
    public void PanClaimed(NodeHandle vp, bool horizontal, in InputEvent down)
    {
        if (vp.IsNull || !_scene.IsLive(vp)) return;
        int slot = AllocContact(down.PointerId);
        if (slot < 0) return;   // 11th concurrent contact — dropped deterministically (matches MaxContacts)
        _contacts[slot].Node = vp; _contacts[slot].Horizontal = horizontal;
        int node = (int)vp.Raw.Index;
        double t = SampleSec(down.TimestampMs, down.QpcTicks);
        _port.Post(ScrollInput.ContactBegin(node, t, AxisPosDip(down.PositionPx, horizontal)));
        NodeHandle parent = NearestSameAxisAncestorViewport(vp, horizontal);
        if (!parent.IsNull) _port.Post(ScrollInput.Chain(node, (int)parent.Raw.Index));
        OnGestureStarted?.Invoke();
    }

    /// <summary>One more sample of an already-claimed touch/pen pan (per <see cref="InputEvent.PointerId"/>).</summary>
    public void PanSample(in InputEvent e)
    {
        int slot = FindContact(e.PointerId);
        if (slot < 0) return;
        ref ContactSlot c = ref _contacts[slot];
        if (!_scene.IsLive(c.Node)) { FreeContact(slot); return; }
        double t = SampleSec(e.TimestampMs, e.QpcTicks);
        _port.Post(ScrollInput.ContactMove((int)c.Node.Raw.Index, t, AxisPosDip(e.PositionPx, c.Horizontal)));
    }

    /// <summary>End (lift) or cancel (capture loss) an already-claimed touch/pen pan.</summary>
    public void PanEnd(in InputEvent e, bool cancel)
    {
        int slot = FindContact(e.PointerId);
        if (slot < 0) return;
        ref ContactSlot c = ref _contacts[slot];
        if (_scene.IsLive(c.Node))
        {
            int node = (int)c.Node.Raw.Index;
            if (cancel) _port.Post(ScrollInput.Cancel(node));
            else _port.Post(ScrollInput.ContactEnd(node, SampleSec(e.TimestampMs, e.QpcTicks), AxisPosDip(e.PositionPx, c.Horizontal)));
        }
        FreeContact(slot);
    }

    private int FindContact(uint id)
    {
        for (int i = 0; i < _contacts.Length; i++)
            if (_contacts[i].Used && _contacts[i].PointerId == id) return i;
        return -1;
    }

    private int AllocContact(uint id)
    {
        int existing = FindContact(id);
        if (existing >= 0) return existing;
        for (int i = 0; i < _contacts.Length; i++)
            if (!_contacts[i].Used) { _contacts[i] = new ContactSlot { PointerId = id, Used = true }; return i; }
        return -1;
    }

    private void FreeContact(int slot) => _contacts[slot] = default;

    // ── phase-tagged producer (DM RUNNING / hi-res wheel fallback) ─────────────────────────────────────────────────

    /// <summary>ScrollBegin/Delta/End — the entire phase-tagged producer contract. PTP/precision-touchpad inertia is
    /// engine-owned (the kernel seeds its own Ballistic fling from the trailing frame-delta history on
    /// <see cref="InputKind.ScrollEnd"/>); there is no OS-momentum kind to special-case.</summary>
    public void Phase(in InputEvent e)
    {
        switch (e.Kind)
        {
            case InputKind.ScrollBegin:
                EndPhaseGesture(cancel: true);   // a producer restart ends any prior gesture cleanly (legacy OnScrollPhase)
                _phaseTotalX = 0f; _phaseTotalY = 0f;
                _phaseOpen = true;
                // A Begin may carry the first displacement (a hi-res fallback's slop packet) — count it.
                if (e.ScrollDelta != 0f || e.ScrollDeltaX != 0f) AccumulatePhaseDelta(in e);
                break;

            case InputKind.ScrollDelta:
                // Producer contract: deltas exist only INSIDE an open Begin…End gesture. A stray delta after a wheel
                // takeover / lift (a terminal DM callback already in the queue) must never revive a gesture.
                if (_phaseOpen) AccumulatePhaseDelta(in e);
                break;

            case InputKind.ScrollEnd:
                EndPhaseGesture(cancel: false);
                break;
        }
    }

    /// <summary>Flush this frame's accumulated phase-producer delta as ONE <see cref="ScrollInputKind.FrameDelta"/>.
    /// The host calls this once per <c>InputDispatcher.Dispatch</c> batch, AFTER every event in the batch has been
    /// routed through <see cref="Phase"/> — plan §3.4: "sum the frame's deltas → ONE FrameDelta(node, tSec, Σdelta) per
    /// frame per gesture (accumulate across the drained span, flush at end of Phase batch)".</summary>
    public void EndFrame() => FlushPhaseDelta();

    private void AccumulatePhaseDelta(in InputEvent e)
    {
        if (_phaseElementOwnsWheel)
        {
            // §A′ locked: every remaining packet of this gesture is redirected to the owning element, never latched
            // (the ownership decision survives even a later packet landing squarely over a real viewport).
            DispatchElementWheel?.Invoke(e.PositionPx, e.ScrollDelta, e.ScrollDeltaX);
            return;
        }
        _phaseTotalX += e.ScrollDeltaX;
        _phaseTotalY += e.ScrollDelta;
        if (!_phaseLatched)
        {
            if (MathF.Abs(_phaseTotalX) + MathF.Abs(_phaseTotalY) < LatchSlopDip) return;
            bool horiz = MathF.Abs(_phaseTotalX) > MathF.Abs(_phaseTotalY);
            NodeHandle vp = ResolveAxisTarget?.Invoke(e.PositionPx, horiz) ?? NodeHandle.Null;
            if (vp.IsNull)
            {
                // §A vs §A′ (plan §12 "Wave-6 Fix C"): a slop-crossing packet with no scroller under it is either a
                // genuine miss (§A — nothing there; keep probing every later packet via _phaseTotalX/Y, unchanged) or
                // an element that OWNS the wheel (§A′ — dispatch THIS packet to it and lock the fallback for good).
                if (DispatchElementWheel?.Invoke(e.PositionPx, e.ScrollDelta, e.ScrollDeltaX) == true)
                    _phaseElementOwnsWheel = true;
                return;
            }
            _phaseNode = vp; _phaseHoriz = horiz; _phaseLatched = true;
            NodeHandle parent = NearestSameAxisAncestorViewport(vp, horiz);
            if (!parent.IsNull) _port.Post(ScrollInput.Chain((int)vp.Raw.Index, (int)parent.Raw.Index));
            OnGestureStarted?.Invoke();
            // No dead zone: the pre-latch travel (everything accumulated up to and excluding THIS packet — the packet
            // itself is added below) is applied on the latch frame, so the content starts exactly where the fingers
            // did rather than 8 DIP behind (Chromium/DM apply the slop distance too).
            _phaseFrameX = _phaseTotalX - e.ScrollDeltaX; _phaseFrameY = _phaseTotalY - e.ScrollDelta;
        }
        if (!_scene.IsLive(_phaseNode)) { _phaseLatched = false; return; }
        _phaseFrameX += e.ScrollDeltaX; _phaseFrameY += e.ScrollDelta;
        _phaseLastT = SampleSec(e.TimestampMs, e.QpcTicks);
        _phaseDirty = true;
    }

    private void FlushPhaseDelta()
    {
        if (!_phaseDirty) return;
        _phaseDirty = false;
        float delta = _phaseHoriz ? _phaseFrameX : _phaseFrameY;
        _phaseFrameX = 0f; _phaseFrameY = 0f;
        if (delta == 0f || !_phaseLatched || !_scene.IsLive(_phaseNode)) return;
        _port.Post(ScrollInput.FrameDelta((int)_phaseNode.Raw.Index, _phaseLastT, delta));
    }

    private void EndPhaseGesture(bool cancel)
    {
        FlushPhaseDelta();
        if (_phaseLatched && _scene.IsLive(_phaseNode))
        {
            int node = (int)_phaseNode.Raw.Index;
            // A=0: no position resample happens on the frame-delta path (§2.2 "Drag (FrameDelta)... no resample, no
            // LSQ"), so ContactEnd's axis-position argument is unused for this producer — the kernel seeds Ballistic
            // from the trailing frame deltas already recorded, not from this call's A.
            _port.Post(cancel ? ScrollInput.Cancel(node) : ScrollInput.ContactEnd(node, _phaseLastT, 0f));
        }
        _phaseLatched = false; _phaseOpen = false; _phaseNode = NodeHandle.Null;
        _phaseTotalX = 0f; _phaseTotalY = 0f; _phaseFrameX = 0f; _phaseFrameY = 0f; _phaseDirty = false;
        _phaseElementOwnsWheel = false;
    }

    /// <summary>True when this frame's dispatch accumulated producer delta that <see cref="EndFrame"/> has not yet
    /// flushed to the kernel — the host treats it as pending scroll work (a wake/paint reason), so a frame is never
    /// declined while a delta is waiting (declining would merge several packets' deltas into one late FrameDelta).</summary>
    public bool HasPendingFrameDelta => _phaseDirty;

    /// <summary>True while a latched touch contact or phase-producer gesture is live. Test/diagnostic observability
    /// (mirrors the legacy <c>InputDispatcher.GestureActive</c>).</summary>
    public bool GestureActive
    {
        get
        {
            if (_phaseLatched) return true;
            for (int i = 0; i < _contacts.Length; i++) if (_contacts[i].Used) return true;
            return false;
        }
    }

    // ── wheel ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Element-level <c>WheelBit</c> handlers and the physical-wheel device-crossover cancel already ran in
    /// the dispatcher (<c>DispatchWheel</c> stays there — plan §3.4). This resolves the viewport(s) for the two wheel
    /// axes independently (legacy <c>ScrollAt</c> semantics) and posts a <see cref="ScrollInputKind.WheelNotch"/> per
    /// axis that has room to move. Returns true iff at least one axis moved a viewport.</summary>
    public bool Wheel(in InputEvent e)
    {
        // Device crossover (legacy CancelGesture / scratchpad :3084): a physical wheel takes over from any live
        // phase-driven gesture — one owner of the offset at a time.
        if (_phaseLatched || _phaseOpen)
        {
            FlushPhaseDelta();
            if (_phaseLatched && _scene.IsLive(_phaseNode)) _port.Post(ScrollInput.Cancel((int)_phaseNode.Raw.Index));
            _phaseLatched = false; _phaseOpen = false; _phaseNode = NodeHandle.Null;
            _phaseTotalX = 0f; _phaseTotalY = 0f; _phaseFrameX = 0f; _phaseFrameY = 0f; _phaseDirty = false;
            _phaseElementOwnsWheel = false;
        }
        bool any = false;
        if (e.WheelNotch != 0f || e.ScrollDelta != 0f) any |= WheelAxis(in e, horizontal: false);
        if (e.WheelNotchX != 0f || e.ScrollDeltaX != 0f) any |= WheelAxis(in e, horizontal: true);
        return any;
    }

    private bool WheelAxis(in InputEvent e, bool horizontal)
    {
        float notch = horizontal ? e.WheelNotchX : e.WheelNotch;
        float rawDip = horizontal ? e.ScrollDeltaX : e.ScrollDelta;
        bool useNotch = notch != 0f;   // a synthetic DIP-only event (useNotch false) bypasses notch scaling entirely
        NodeHandle vp = ResolveAxisTarget?.Invoke(e.PositionPx, horizontal) ?? NodeHandle.Null;
        if (vp.IsNull || !_scene.IsLive(vp) || !_scene.HasScroll(vp)) return false;
        ref ScrollState sc = ref _scene.ScrollRef(vp);
        // Cross-axis fallback (a vertical wheel over a horizontal-only shelf scrolls the shelf): the resolver may hand
        // back a viewport whose scroll axis differs from the wheel axis — from here on, geometry follows the VIEWPORT.
        horizontal = sc.Orientation == 1;
        float viewportExtent = horizontal ? sc.ViewportW : sc.ViewportH;
        float dip = useNotch ? ScrollFeel.Shipping.PerNotchDip(viewportExtent) * notch : rawDip;
        if (dip == 0f) return false;
        // Direction-specific at-edge refusal (see class remarks on the accepted simplification vs the legacy
        // multi-candidate climb).
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float max = horizontal ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW) : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        float off = horizontal ? sc.OffsetX : sc.OffsetY;
        bool atEdge = (dip < 0f && off <= 0.5f) || (dip > 0f && off >= max - 0.5f);
        if (max <= 0f || atEdge) return false;
        // A device notch glides (Driven|Wheel, crit-damped chase — plan §2.2). A synthetic DIP-only event (no notch) is
        // the scripted/headless contract documented on InputEvent: "scrolls that DIP directly" — an immediate,
        // frame-synchronous displacement (real mice always carry a notch, so feel is unaffected).
        if (useNotch) _port.Post(ScrollInput.WheelNotch((int)vp.Raw.Index, SampleSec(e.TimestampMs, e.QpcTicks), dip));
        else _port.Post(ScrollInput.ScrollBy((int)vp.Raw.Index, dip, true, 0f, 0f, 0f, 0f));
        OnGestureStarted?.Invoke();
        return true;
    }

    // ── cancel / thumb / zoom / autoscroll / keyboard ──────────────────────────────────────────────────────────────

    /// <summary>PointerDown over a viewport whose kernel body is mid-<see cref="ScrollActivity.Ballistic"/> or
    /// <see cref="ScrollActivity.Driven"/> zeroes its motion — a click/grab must not drift under the pointer (legacy
    /// <c>OnCancelFling</c>, scratchpad :3111-3126 "any PointerDown zeroes the viewport's motion first").</summary>
    public void CancelAt(Point2 p)
    {
        NodeHandle vp = ResolveAnyTarget?.Invoke(p) ?? NodeHandle.Null;
        if (vp.IsNull || !_scene.IsLive(vp)) return;
        if (!_kernel.TryGetBody((int)vp.Raw.Index, out var body)) return;
        if (body.Activity is ScrollActivity.Ballistic or ScrollActivity.Driven)
            _port.Post(ScrollInput.Cancel((int)vp.Raw.Index));
    }

    /// <summary>Cancel an already-resolved viewport's motion unconditionally (scrollbar grab / touch-down-over-scroller
    /// — the caller already resolved the node, so no hit-test bridge or activity gate is needed).</summary>
    public void Cancel(NodeHandle vp)
    {
        if (!vp.IsNull) _port.Post(ScrollInput.Cancel((int)vp.Raw.Index));
    }

    /// <summary>Scrollbar thumb drag: absolute offset, immediate, no spring (1:1 thumb tracking — plan §3.2).</summary>
    public void ThumbSet(NodeHandle vp, float offset)
    {
        if (!vp.IsNull) _port.Post(ScrollInput.ThumbSet((int)vp.Raw.Index, offset));
    }

    /// <summary>Pinch-zoom focal update (plan §3.2 Pinch row).</summary>
    public void SetZoom(NodeHandle vp, float zoom, float focalOffset)
    {
        if (!vp.IsNull) _port.Post(ScrollInput.SetZoom((int)vp.Raw.Index, zoom, focalOffset));
    }

    /// <summary>Drag-drop edge / tree edge auto-scroll: a time-true continuous velocity (replaces the legacy
    /// per-frame delta poke, <c>AutoScrollBy</c>) — 0 stops it. The kernel integrates <c>off += v·dt</c> itself
    /// (plan §2.2 Driven/Autoscroll), so the caller posts on velocity CHANGE, not every frame.</summary>
    public void AutoScroll(NodeHandle vp, float dipPerS)
    {
        if (!vp.IsNull) _port.Post(ScrollInput.SetVelocity((int)vp.Raw.Index, dipPerS));
    }

    /// <summary>Arrow/PageUp/PageDown/Home/End on the focused viewport (plan §4): 48 DIP glide per arrow,
    /// viewport−48 DIP per page, extents for Home/End — all non-immediate (<c>ScrollTo</c>/<c>ScrollBy</c> glide).
    /// <paramref name="focusedViewport"/> is the nearest scrollable self-or-ancestor of the KEYBOARD-focused node,
    /// resolved by the caller (a pure ancestor walk — no hit-test bridge needed). Returns false (unhandled) for any
    /// other key or when the target isn't scrollable, so the dispatcher's normal key routing/bubbling is unaffected.</summary>
    public bool Key(in InputEvent e, NodeHandle focusedViewport)
    {
        if (focusedViewport.IsNull || !_scene.IsLive(focusedViewport) || !_scene.HasScroll(focusedViewport)) return false;
        ref ScrollState sc = ref _scene.ScrollRef(focusedViewport);
        bool horizontal = sc.Orientation == 1;
        int node = (int)focusedViewport.Raw.Index;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float viewportExtent = horizontal ? sc.ViewportW : sc.ViewportH;
        float max = horizontal ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW) : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        float delta;
        switch (e.KeyCode)
        {
            case Keys.Left when horizontal: delta = -KeyGlideDip; break;
            case Keys.Right when horizontal: delta = KeyGlideDip; break;
            case Keys.Up when !horizontal: delta = -KeyGlideDip; break;
            case Keys.Down when !horizontal: delta = KeyGlideDip; break;
            case Keys.PageUp: delta = -MathF.Max(KeyGlideDip, viewportExtent - KeyGlideDip); break;
            case Keys.PageDown: delta = MathF.Max(KeyGlideDip, viewportExtent - KeyGlideDip); break;
            case Keys.Home: _port.Post(ScrollInput.ScrollTo(node, 0f, false, 0f, 0f, 0f, 0f)); return true;
            case Keys.End: _port.Post(ScrollInput.ScrollTo(node, max, false, 0f, 0f, 0f, 0f)); return true;
            default: return false;
        }
        _port.Post(ScrollInput.ScrollBy(node, delta, false, 0f, 0f, 0f, 0f));
        return true;
    }
}
