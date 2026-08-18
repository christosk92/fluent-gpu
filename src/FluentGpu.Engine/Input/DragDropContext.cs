using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Input;

/// <summary>
/// L2 of the drag-drop stack (plan E5; user ruling 2026-06-10: deliberately NOT WinUI's OLE
/// <c>DataPackage</c>/<c>DoDragDrop</c> modal loop — the Flutter Draggable/DragTarget + react-beautiful-dnd context +
/// SwiftUI draggable/dropDestination model instead; the frame loop STAYS LIVE for the whole gesture). Layered on the
/// L1 <see cref="DragController"/> gesture:
///
/// • A <c>BoxEl.Draggable</c> (<see cref="DragSource"/>: a string <c>Kind</c> discriminator + a payload factory) marks
///   a node a typed drag source. When the L1 press promotes past the drag box, the dispatcher calls
///   <see cref="TryBegin"/> — the nearest enabled DragSource up the chain opens THE session and its
///   <see cref="DragSource.PayloadFactory"/> resolves ONCE (cold gesture edge; never per-move).
/// • A <c>BoxEl.DropTarget</c> (<see cref="DropTargetSpec"/>) makes ANY surface a candidate receiver. Per pointer move
///   the dispatcher hands <see cref="Move"/> the hit-test chain under the pointer; the nearest enabled target whose
///   <c>AcceptKinds</c> contains the session's Kind becomes <see cref="OverTarget"/> (a non-accepting target never
///   blocks an accepting ancestor — the Flutter DragTarget pass-through). The dragged subtree can never self-target:
///   L1 already cleared its <c>HitTestVisible</c>, so hit-testing sees THROUGH the moving visual.
/// • Enter/Leave fire on target transitions, Over on every move while inside, Drop on release over an accepting
///   target (<see cref="TryDrop"/> — fired BEFORE the L1 completion so the target reads the live session). The ONE
///   mutable <see cref="DragSession"/> instance is reused for the whole gesture (0 steady-state alloc per move);
///   handlers copy what they keep.
///
/// Spring-loading (<see cref="DropTargetSpec.SpringLoadDelayMs"/>) rides the same <see cref="Tick"/>: the dwell on the
/// nearest kind-matched spring-configured target accumulates per frame and fires <c>OnSpringLoad</c> once per Enter, so
/// a collapsed container opens itself under a held drag. <see cref="HasActiveWork"/> reports the armed window, which is
/// what keeps frames coming while the pointer is perfectly still.
///
/// Edge auto-scroll (the rbd/WinUI "drag near the viewport edge keeps scrolling" behavior) is engine-level here so
/// every drag gets it: while a session is live, the pointer entering the hot zone of the nearest OVERFLOWING
/// scrollable ancestor arms a proportional scroll velocity the host drives via <see cref="Tick"/>. Constants and
/// behavior are WinUI's (microsoft-ui-xaml dxaml\xcp\dxaml\lib\ListViewBase_Partial_Reorder.cpp): a 100px edge band
/// (:39 LISTVIEWBASE_EDGE_SCROLL_EDGE_WIDTH_PX), speed varying LINEARLY from 1500 px/s at the edge to 150 px/s at the
/// band's inner rim (:42-47), a 50ms delay-start with instant velocity updates once running (:40, :1749-1753), the
/// start-edge tried before the end-edge (:1660-1707), and scrolling suppressed when already pinned to that boundary
/// (:1686-1690, :1718-1722).
/// </summary>
public sealed class DragDropContext
{
    /// <summary>Edge hot-zone width — LISTVIEWBASE_EDGE_SCROLL_EDGE_WIDTH_PX = 100 (ListViewBase_Partial_Reorder.cpp:39).</summary>
    public const float EdgeScrollZonePx = 100f;

    /// <summary>Speed at the inner rim of the zone — LISTVIEWBASE_EDGE_SCROLL_MIN_SPEED = 150 px/s (:46).</summary>
    public const float EdgeScrollMinSpeed = 150f;

    /// <summary>Speed AT the edge — LISTVIEWBASE_EDGE_SCROLL_MAX_SPEED = 1500 px/s (:47).</summary>
    public const float EdgeScrollMaxSpeed = 1500f;

    /// <summary>Delay before an armed edge scroll starts moving — LISTVIEWBASE_EDGE_SCROLL_START_DELAY_MSEC = 50
    /// (:40). Velocity CHANGES while running apply instantly (:1749-1753).</summary>
    public const float EdgeScrollStartDelayMs = 50f;

    private readonly SceneStore _scene;
    private readonly Action _requestRerender;
    private readonly DragSession _session = new();   // THE live session — reused for every gesture (0 alloc per move)

    private bool _active;
    private NodeHandle _over;            // current accepting target (Null = over nothing that accepts)
    private DropTargetSpec? _overSpec;   // its spec (cached so Leave/Over/Drop never re-query a dead column)
    private NodeHandle _refused;         // nearest kind-matched CanAccept-refuser while nothing accepts (the cue seam)
    private DropTargetSpec? _refusedSpec;
    private NodeHandle _spring;          // nearest kind-matched target that configures spring-loading (accepting or not)
    private DropTargetSpec? _springSpec;
    private float _springDwellMs;        // hover dwell on THAT node (reset on change, never on a move inside it)
    private bool _springFired;           // once per Enter — re-arms only when _spring changes
    private int _spotlightTargetVersion = -1;
    private DragLift _lift;              // L1's lift mode for this gesture (see PruneDead's dead-source rule)
    private DropEffect _defaultEffect = DropEffect.Move;   // the engine's advisory effect over an accepting target;
                                                           // Move for in-app drags, Copy for an OS file drop (Explorer convention)

    // Edge auto-scroll (armed by Move, driven by the host's Tick).
    private NodeHandle _scrollViewport;
    private float _edgeVelocity;         // px/s along the viewport's scroll orientation (negative = toward 0)
    private float _edgeDelayLeftMs;
    private bool _edgeScrolling;         // past the 50ms delay-start
    // Scroll v3 (docs/plans/scroll-v3-plan-2026-08-17.md §3.2): the LAST velocity actually posted to the kernel (and
    // which viewport it was posted to) — AutoScroll is a time-true continuous command (the kernel integrates
    // off += v·dt itself), so it is posted only on a CHANGE (new value or new viewport), not every Tick like the old
    // per-frame delta poke. Lets Tick/UpdateEdgeScroll/End all detect "does the router need a fresh post" cheaply.
    private NodeHandle _postedVp;
    private float _postedVelocity;

    /// <summary>Wired by the dispatcher (Scroll v3): a time-true edge-scroll VELOCITY (DIP/s; 0 = stop) — replaces the
    /// legacy per-frame delta-write <c>ScrollBy</c> (<c>Func&lt;NodeHandle,float,bool&gt;</c>, immediate clamped write +
    /// "did it move" feedback). The kernel now owns the clamp/at-the-edge stop internally (plan §2.2 Autoscroll:
    /// <c>off += v·dt</c>), so this is fire-and-forget — <see cref="Tick"/> posts it only when the desired velocity or
    /// target viewport changes (see <see cref="_postedVp"/>/<see cref="_postedVelocity"/>).</summary>
    internal Action<NodeHandle, float>? AutoScroll;

    public DragDropContext(SceneStore scene, Action requestRerender)
    {
        _scene = scene;
        _requestRerender = requestRerender;
        _over = NodeHandle.Null;
        _refused = NodeHandle.Null;
        _scrollViewport = NodeHandle.Null;
    }

    /// <summary>A typed session is live (an L1 drag promoted on a chain carrying a <see cref="DragSource"/>).</summary>
    public bool IsActive => _active;

    /// <summary>THE live session object (meaningful only while <see cref="IsActive"/>). One instance, reused.</summary>
    public DragSession Session => _session;

    /// <summary>The accepting target currently under the pointer (<see cref="NodeHandle.Null"/> when none / idle).</summary>
    public NodeHandle OverTarget => _active ? _over : NodeHandle.Null;

    /// <summary>True while an edge auto-scroll is armed/running OR a spring-load is counting down — the host keeps
    /// frames coming and calls <see cref="Tick"/>. The spring term is what makes a STILL pointer keep ticking: the
    /// dwell is measured in frames, and an in-app gesture's own <c>DragActive</c> keep-alive does not cover an
    /// external (OS) drag, which emits no L1 gesture at all.</summary>
    public bool HasActiveWork => _active && (_edgeVelocity != 0f || SpringArmed);

    /// <summary>A spring-load is configured on the current dwell host and has not fired yet for this Enter.</summary>
    private bool SpringArmed
        => !_spring.IsNull && !_springFired
           && _springSpec is { OnSpringLoad: not null } s && s.SpringLoadDelayMs > 0f;

    /// <summary>Called by the dispatcher at L1 promotion: walk up from the promoted node for the nearest ENABLED
    /// <see cref="DragSource"/>, resolve its payload once, and open the session. Returns false when the chain carries
    /// no source — the gesture stays a plain L1 reorder drag and drop targets never see it.
    /// <paramref name="lift"/> is the L1 controller's <see cref="DragController.ActiveLift"/> — THE seam between the two
    /// layers: a <see cref="DragLift.Stationary"/> session survives its source node being virtualized away (see
    /// <see cref="PruneDead"/>), because the drag visual is an independent chip rather than the source row.</summary>
    public bool TryBegin(NodeHandle promoted, Point2 abs, KeyModifiers mods, PointerKind kind,
                         DragLift lift = DragLift.Ghost)
    {
        if (_active) return false;
        for (var n = promoted; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if (!_scene.TryGetDragSource(n, out var src) || src is null) continue;
            _session.Payload = src.PayloadFactory();   // resolved ONCE at promotion (cold edge), never per move
            _session.Kind = src.Kind;
            _session.Position = abs;
            _session.VelocityX = 0f;
            _session.VelocityY = 0f;
            _session.Source = n;
            _session.OverTarget = NodeHandle.Null;
            _session.RefusedTarget = NodeHandle.Null;
            _session.Effect = DropEffect.None;
            _session.Caption = null;
            _session.Mods = mods;
            _session.Pointer = kind;
            _defaultEffect = DropEffect.Move;   // in-app reorder/transfer
            _lift = lift;
            _active = true;
            _over = NodeHandle.Null;
            _overSpec = null;
            _refused = NodeHandle.Null;
            _refusedSpec = null;
            ClearSpring();
            RefreshSpotlight(force: true);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Open a session for an OS-originated (OLE) drag that did NOT promote from an in-tree <see cref="DragSource"/>:
    /// the host's <c>IDropTarget</c> resolves the payload (e.g. the dropped paths) and calls this on DragEnter, then
    /// drives <see cref="Move"/>/<see cref="TryDrop"/>/<see cref="Cancel"/> exactly like an in-app gesture. The session
    /// <c>Source</c> is the live scene ROOT (never <see cref="NodeHandle.Null"/>) so <see cref="PruneDead"/> — which
    /// <see cref="Cancel"/>s a session whose source died — keeps the external session alive across reconciles. The
    /// advisory effect over an accepting target is <paramref name="defaultEffect"/> (Copy for a file drop, the Explorer
    /// convention); targets may still refine it in OnEnter/OnOver. Returns false if a session is already live.
    /// </summary>
    public bool ExternalBegin(string kind, object? payload, Point2 abs, KeyModifiers mods,
                              DropEffect defaultEffect = DropEffect.Copy)
    {
        if (_active) return false;
        if (_scene.Root.IsNull) return false;   // a never-rendered tree has no surface to drop onto
        _session.Payload = payload;
        _session.Kind = kind;
        _session.Position = abs;
        _session.VelocityX = 0f;
        _session.VelocityY = 0f;
        _session.Source = _scene.Root;          // NOT Null — PruneDead Cancels when !IsLive(Source)
        _session.OverTarget = NodeHandle.Null;
        _session.RefusedTarget = NodeHandle.Null;
        _session.Effect = DropEffect.None;
        _session.Caption = null;
        _session.Mods = mods;
        _session.Pointer = PointerKind.Mouse;   // an OLE drag presents as a mouse cursor
        _defaultEffect = defaultEffect;
        _lift = DragLift.Ghost;                 // no in-tree source to lift; Source is already the (live) scene root
        _active = true;
        _over = NodeHandle.Null;
        _overSpec = null;
        _refused = NodeHandle.Null;
        _refusedSpec = null;
        ClearSpring();
        RefreshSpotlight(force: true);
        return true;
    }

    /// <summary>Per pointer move while the session is live: update the session coords/velocity, resolve the nearest
    /// accepting target on <paramref name="hit"/>'s parent chain, fire Enter/Leave on transitions and Over while
    /// inside, then re-evaluate the edge auto-scroll. 0-alloc.</summary>
    public void Move(NodeHandle hit, Point2 abs, float velocityX, float velocityY, KeyModifiers mods)
    {
        if (!_active) return;
        _session.Position = abs;
        _session.VelocityX = velocityX;
        _session.VelocityY = velocityY;
        _session.Mods = mods;
        RefreshSpotlight(force: false);

        var next = FindTarget(hit, out var refuser, out var refuserSpec, out var springHost, out var springSpec);
        if (next != _over)
        {
            if (!_over.IsNull && _scene.IsLive(_over))
            {
                _session.OverTarget = _over;             // Leave still reports the target being left
                _overSpec?.OnLeave?.Invoke(_session);
            }
            _over = next;
            _overSpec = !next.IsNull && _scene.TryGetDropTarget(next, out var spec) ? spec : null;
            _scene.SetDropSpotlightOver(next);
            _session.OverTarget = next;
            _session.Effect = next.IsNull ? DropEffect.None : _defaultEffect;   // targets may refine in OnEnter/OnOver
            _session.Caption = null;   // the caption belongs to the target being ENTERED — a target never has to unset it
            if (!next.IsNull) _overSpec?.OnEnter?.Invoke(_session);
            _requestRerender();
        }
        else
        {
            _session.OverTarget = _over;
        }
        if (!_over.IsNull && _scene.IsLive(_over)) _overSpec?.OnOver?.Invoke(_session);
        // The refusal cue is resolved LAST so an accepting target's own Caption always wins: a refuser is published
        // only when nothing on the chain accepted, in which case the caption slot is free by construction.
        UpdateRefusal(_over.IsNull ? refuser : NodeHandle.Null, _over.IsNull ? refuserSpec : null);
        UpdateSpring(springHost, springSpec);

        UpdateEdgeScroll(hit, abs);
    }

    /// <summary>Release: when the pointer is over an accepting target, fire <c>OnDrop(session)</c> and close the
    /// session — called by the dispatcher BEFORE the L1 <see cref="DragController.Complete"/> so the target reads the
    /// live session. Returns true on a drop; <paramref name="settleGlide"/> = the target's
    /// <see cref="DropTargetSpec.SettleOnDrop"/> (true ⇒ keep the L1 drop-settle glide — reorder targets; false ⇒ the
    /// visual snaps home, the "deposited" feel of a foreign-surface drop).</summary>
    public bool TryDrop(Point2 abs, KeyModifiers mods, out bool settleGlide)
    {
        settleGlide = false;
        if (!_active) return false;
        _session.Position = abs;
        _session.Mods = mods;
        var target = _over;
        var spec = _overSpec;
        bool dropped = !target.IsNull && _scene.IsLive(target) && spec is not null;
        if (dropped)
        {
            _session.OverTarget = target;
            if (_session.Effect == DropEffect.None) _session.Effect = _defaultEffect;
            spec!.OnDrop?.Invoke(_session);
            settleGlide = spec.SettleOnDrop;
        }
        End();
        return dropped;
    }

    /// <summary>Abort the session (Escape / capture loss / window blur / release over nothing accepting follows
    /// <see cref="TryDrop"/> returning false): fires <c>OnLeave</c> on a live current target, then resets. The L1
    /// spring-back (the dispatcher's subsequent <see cref="DragController.Cancel"/>/<c>Complete</c>) glides the
    /// visual home. No-op when idle.</summary>
    public void Cancel()
    {
        if (!_active) return;
        if (!_over.IsNull && _scene.IsLive(_over))
        {
            _session.OverTarget = _over;
            _overSpec?.OnLeave?.Invoke(_session);
        }
        End();
    }

    /// <summary>Called at dispatch start: a session whose SOURCE was freed by a reconcile ends (Leave fires on a live
    /// target); a freed TARGET/viewport is dropped silently (its columns are dead).</summary>
    public void PruneDead()
    {
        if (!_active) return;
        RefreshSpotlight(force: false);
        if (!_scene.IsLive(_session.Source))
        {
            // A STATIONARY-lift session outlives its source row (virtualized away / list rebuilt): the payload was
            // resolved at promotion and the drag visual is the DragPreviewLayer chip, so the gesture must still be able
            // to reach a drop. Reparent onto the live scene root — exactly what ExternalBegin does for an OS drag
            // (:141), which is the same "no in-tree source" shape. Ghost lift keeps aborting: its visual is the corpse.
            if (_lift == DragLift.Stationary && !_scene.Root.IsNull) _session.Source = _scene.Root;
            else { Cancel(); return; }
        }
        if (!_over.IsNull && !_scene.IsLive(_over))
        {
            _over = NodeHandle.Null;
            _overSpec = null;
            _session.OverTarget = NodeHandle.Null;
            _scene.SetDropSpotlightOver(NodeHandle.Null);
        }
        if (!_refused.IsNull && !_scene.IsLive(_refused))
        {
            _refused = NodeHandle.Null;
            _refusedSpec = null;
            _session.RefusedTarget = NodeHandle.Null;
            _session.Caption = null;
        }
        if (!_spring.IsNull && !_scene.IsLive(_spring)) ClearSpring();
        if (!_scrollViewport.IsNull && !_scene.IsLive(_scrollViewport))
        {
            // The node died — SceneStore's removal path already posted Unbind to the kernel (WP-B), so there is
            // nothing further to tell it; just forget the local bookkeeping (StopPostedAutoScroll would no-op here
            // anyway, since it also gates on IsLive, but a dead handle should never linger in these fields).
            _scrollViewport = NodeHandle.Null;
            _edgeVelocity = 0f;
            _edgeScrolling = false;
            _postedVp = NodeHandle.Null; _postedVelocity = 0f;
        }
    }

    /// <summary>Phase-7 host tick, driving the two time-based behaviours of a live session. (1) SPRING-LOAD: accumulate
    /// the dwell on the current host and fire it once. (2) EDGE AUTO-SCROLL: hold the 50ms delay-start, then post the
    /// desired velocity through <see cref="AutoScroll"/> (Scroll v3: the kernel integrates + clamps continuously — no
    /// more per-frame delta write / "did it move" feedback; the at-the-edge stop is the kernel's, not this class's).
    /// Returns true while EITHER still has work so the host keeps frames coming — the two are deliberately independent,
    /// since a spring-load usually counts down over a perfectly stationary pointer. 0-alloc.</summary>
    public bool Tick(float dtMs)
    {
        if (!_active) return false;
        bool springArmed = TickSpring(dtMs);
        if (_edgeVelocity == 0f)
        {
            StopPostedAutoScroll();
            return springArmed;
        }
        if (_scrollViewport.IsNull || !_scene.IsLive(_scrollViewport) || AutoScroll is null)
        {
            _edgeVelocity = 0f;
            _edgeScrolling = false;
            StopPostedAutoScroll();
            return springArmed;
        }
        if (!_edgeScrolling)
        {
            _edgeDelayLeftMs -= dtMs;
            if (_edgeDelayLeftMs > 0f) return true;   // delay-start pending (LISTVIEWBASE_EDGE_SCROLL_START_DELAY_MSEC)
            _edgeScrolling = true;
        }
        if (_postedVp != _scrollViewport || _postedVelocity != _edgeVelocity)
        {
            if (_postedVp != _scrollViewport) StopPostedAutoScroll();   // the target changed — zero the OLD viewport first
            AutoScroll(_scrollViewport, _edgeVelocity);                 // instant update once running (cpp:1749-1753)
            _postedVp = _scrollViewport; _postedVelocity = _edgeVelocity;
        }
        // The pointer did not move, but the target's content geometry did. Re-run the current destination's projection
        // so insertion slots/previews track edge autoscroll continuously instead of lagging until the next mouse event.
        if (!_over.IsNull && _scene.IsLive(_over)) _overSpec?.OnOver?.Invoke(_session);
        _requestRerender();
        return true;
    }

    /// <summary>Zero the last-posted velocity if one is live, and forget which viewport it targeted (idempotent).</summary>
    private void StopPostedAutoScroll()
    {
        if (_postedVelocity == 0f) return;
        if (!_postedVp.IsNull && _scene.IsLive(_postedVp)) AutoScroll?.Invoke(_postedVp, 0f);
        _postedVp = NodeHandle.Null; _postedVelocity = 0f;
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nearest enabled drop target on the chain whose AcceptKinds contains the session's Kind. Non-accepting
    /// targets do NOT block accepting ancestors (Flutter DragTarget pass-through).
    /// <para><paramref name="refuser"/> reports the NEAREST target that matched the kind but was turned away by its
    /// <see cref="DropTargetSpec.CanAccept"/> — the surface the user aimed at and that owes them a reason. It is
    /// meaningful only when the return value is Null (an accepting ancestor makes the drop succeed anyway).</para></summary>
    private NodeHandle FindTarget(NodeHandle hit, out NodeHandle refuser, out DropTargetSpec? refuserSpec,
                                 out NodeHandle springHost, out DropTargetSpec? springSpec)
    {
        refuser = NodeHandle.Null;
        refuserSpec = null;
        springHost = NodeHandle.Null;
        springSpec = null;
        if (!_scene.HasDropTargets) return NodeHandle.Null;
        for (var n = hit; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if (!_scene.TryGetDropTarget(n, out var spec) || spec is null) continue;
            if (!spec.Accepts(_session.Kind)) continue;
            // The spring host is the NEAREST kind-matched target that configures one, decided BEFORE acceptance: a
            // spring-load opens a container, it does not receive a payload, so a folder that refuses this drop (and a
            // pure SpringLoadOnly waypoint) must still be able to open itself under the pointer.
            if (springHost.IsNull && spec.OnSpringLoad is not null && spec.SpringLoadDelayMs > 0f)
            {
                springHost = n;
                springSpec = spec;
            }
            // Per-session transparency (DropTargetSpec.Transparent): "this gesture is none of my business". Tested
            // BEFORE acceptance AND before the refusal candidate is recorded, so the walk passes through as though the
            // node declared no target at all — the page body under a same-list reorder stops accusing the user with a
            // not-allowed glyph while the drag merely crosses it on the way to the list (B2).
            if (spec.Transparent is { } transparent && transparent(_session)) continue;
            if (spec.SpringLoadOnly) continue;   // a waypoint: never a destination, never a refusal
            if (spec.CanAccept is { } canAccept && !canAccept(_session))
            {
                if (refuser.IsNull) { refuser = n; refuserSpec = spec; }   // nearest refuser wins the cue
                continue;
            }
            return n;
        }
        return NodeHandle.Null;
    }

    /// <summary>Publish (or clear) the refusal cue. A CHANGE of refuser re-renders — the chip's not-allowed glyph and
    /// caption are content, not a bound transform. The caption is re-read on every move while refused so a reason that
    /// depends on the pointer (a slot, a section) stays truthful; steady-state that is one delegate call.</summary>
    private void UpdateRefusal(NodeHandle node, DropTargetSpec? spec)
    {
        if (node != _refused)
        {
            _refused = node;
            _refusedSpec = spec;
            _session.RefusedTarget = node;
            // Left the refuser: its reason leaves with it — but ONLY when nothing accepted. On the refuser→acceptor
            // transition the acceptor's OnEnter/OnOver has already published its own caption a few lines above (Move
            // resolves acceptance first), and clearing here would blank it for one frame before the next move re-set it.
            if (node.IsNull && _over.IsNull) _session.Caption = null;
            _requestRerender();
        }
        if (_refused.IsNull) return;
        _session.Caption = _refusedSpec?.RefusalCaption?.Invoke(_session);
    }

    /// <summary>Re-home the spring-load dwell. A CHANGE of host restarts the clock and re-arms the one-shot; staying on
    /// the same node keeps counting (a pointer that jitters inside a folder is still dwelling on it). 0-alloc; no
    /// re-render — nothing is published until the spring actually fires.</summary>
    private void UpdateSpring(NodeHandle node, DropTargetSpec? spec)
    {
        if (node != _spring)
        {
            _spring = node;
            _springDwellMs = 0f;
            _springFired = false;
        }
        _springSpec = node.IsNull ? null : spec;
    }

    private void ClearSpring()
    {
        _spring = NodeHandle.Null;
        _springSpec = null;
        _springDwellMs = 0f;
        _springFired = false;
    }

    /// <summary>Accumulate the hover dwell and fire the spring-load once. Returns true while still ARMED (the host must
    /// keep frames coming for a motionless pointer); false once it has fired or nothing is armed.</summary>
    private bool TickSpring(float dtMs)
    {
        if (!SpringArmed) return false;
        if (!_scene.IsLive(_spring)) { ClearSpring(); return false; }
        _springDwellMs += dtMs;
        if (_springDwellMs < _springSpec!.SpringLoadDelayMs) return true;
        _springFired = true;
        _springSpec.OnSpringLoad!(_session);
        _requestRerender();
        return false;
    }

    private void UpdateEdgeScroll(NodeHandle hit, Point2 abs)
    {
        // Nearest scrollable ancestor under the pointer that actually overflows along its scroll orientation.
        NodeHandle vp = NodeHandle.Null;
        ScrollState sc = default;
        for (var n = hit; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0) continue;
            if (!_scene.TryGetScroll(n, out sc)) continue;
            bool h = sc.Orientation == 1;
            float overflow = h ? sc.ContentW - sc.ViewportW : sc.ContentH - sc.ViewportH;
            if (overflow <= 0.5f) continue;
            vp = n;
            break;
        }

        float velocity = 0f;
        if (!vp.IsNull)
        {
            var r = _scene.AbsoluteRect(vp);
            bool horizontal = sc.Orientation == 1;
            float pos = horizontal ? abs.X - r.X : abs.Y - r.Y;
            float extent = horizontal ? r.W : r.H;
            float offset = horizontal ? sc.OffsetX : sc.OffsetY;
            float max = horizontal ? MathF.Max(0f, sc.ContentW - sc.ViewportW) : MathF.Max(0f, sc.ContentH - sc.ViewportH);

            // Start-edge first; end-edge only when the start-edge is stationary (cpp:1660-1707). Suppress against
            // the boundary (cpp:1686-1690, :1718-1722).
            float toward0 = EdgeSpeed(pos);
            float towardEnd = EdgeSpeed(extent - pos);
            if (toward0 > 0f) { if (offset > 0.5f) velocity = -toward0; }
            else if (towardEnd > 0f && offset < max - 0.5f) velocity = towardEnd;
        }

        if (velocity == 0f)
        {
            _edgeVelocity = 0f;
            _edgeScrolling = false;
        }
        else
        {
            if (_edgeVelocity == 0f && !_edgeScrolling) _edgeDelayLeftMs = EdgeScrollStartDelayMs;   // delay-start (:1749-1753)
            _edgeVelocity = velocity;   // instant update once armed/running
        }
        _scrollViewport = vp;
    }

    /// <summary>WinUI's linear edge-speed gradient: MAX at distance 0, MIN at the zone's inner rim, 0 outside
    /// (ComputeEdgeScrollVelocityFromEdgeDistance — ListViewBase_Partial_Reorder.cpp:1731-1747).</summary>
    private static float EdgeSpeed(float distanceFromEdge)
    {
        if (distanceFromEdge > EdgeScrollZonePx) return 0f;
        if (distanceFromEdge < 0f) distanceFromEdge = 0f;
        return EdgeScrollMaxSpeed - (distanceFromEdge / EdgeScrollZonePx) * (EdgeScrollMaxSpeed - EdgeScrollMinSpeed);
    }

    /// <summary>Phase-7.8 host hook, called once per frame while a session is live — AFTER reconcile/layout/realize and
    /// the scroll-offset writes, BEFORE record. Re-collects the spotlight roots unconditionally.
    /// <para>The version gate below is NOT sufficient on its own. <c>DropTargetsVersion</c> only moves when the sparse
    /// spec column is WRITTEN, and the signals-first bound realize path never writes it again: a row is built once per
    /// slot and recycled by a bind-signal write (<c>Reconciler.RebindBoundSlot</c>), so a scrolling virtualized list
    /// re-points every realized node at a different logical item while its <see cref="DropTargetSpec"/> instance — and
    /// therefore the version — stays exactly where it was. The set then went stale IN PLACE: the cutouts stayed on the
    /// slots that WERE compatible and travelled with them as the rows underneath changed, which is the "wrong rows lit,
    /// highlights drift while the sidebar scrolls" defect. A <see cref="DropTargetSpec.CanAccept"/> that reads a signal
    /// has the same problem with no virtualization at all.</para>
    /// <para>The cost is one pass over the realized drop targets per frame, only while a drag is live, and it lands at a
    /// fixed point in the frame rather than during record (which the recorder's contract still forbids). 0-alloc: the
    /// dictionary walk uses a struct enumerator and the root list is cleared, not reallocated.</para></summary>
    public void SyncSpotlightBeforeRecord()
    {
        if (!_active) return;
        CollectSpotlight();   // no _requestRerender: this frame is already being recorded
    }

    private void RefreshSpotlight(bool force)
    {
        int version = _scene.DropTargetsVersion;
        if (!force && version == _spotlightTargetVersion) return;
        CollectSpotlight();
        _requestRerender();
    }

    private void CollectSpotlight()
    {
        _spotlightTargetVersion = _scene.DropTargetsVersion;
        _scene.RefreshDropSpotlight(_session);
        if (!_over.IsNull && _scene.IsLive(_over))
            _overSpec = _scene.TryGetDropTarget(_over, out var current) ? current : null;
        _scene.SetDropSpotlightOver(_over);
    }

    private void End()
    {
        StopPostedAutoScroll();   // a live session ending mid-edge-scroll must not leave the viewport coasting
        _scene.ClearDropSpotlight();
        _requestRerender();
        _spotlightTargetVersion = -1;
        _active = false;
        _over = NodeHandle.Null;
        _overSpec = null;
        _refused = NodeHandle.Null;
        _refusedSpec = null;
        ClearSpring();
        _defaultEffect = DropEffect.Move;
        _scrollViewport = NodeHandle.Null;
        _edgeVelocity = 0f;
        _edgeScrolling = false;
        _session.Payload = null;            // release the payload's GC edge with the gesture
        _session.Kind = "";
        _session.Source = NodeHandle.Null;
        _session.OverTarget = NodeHandle.Null;
        _session.RefusedTarget = NodeHandle.Null;
        _session.Effect = DropEffect.None;
        _session.Caption = null;
        _lift = DragLift.Ghost;
    }
}
