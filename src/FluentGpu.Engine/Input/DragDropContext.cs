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
    private int _spotlightTargetVersion = -1;
    private DragLift _lift;              // L1's lift mode for this gesture (see PruneDead's dead-source rule)
    private DropEffect _defaultEffect = DropEffect.Move;   // the engine's advisory effect over an accepting target;
                                                           // Move for in-app drags, Copy for an OS file drop (Explorer convention)

    // Edge auto-scroll (armed by Move, driven by the host's Tick).
    private NodeHandle _scrollViewport;
    private float _edgeVelocity;         // px/s along the viewport's scroll orientation (negative = toward 0)
    private float _edgeDelayLeftMs;
    private bool _edgeScrolling;         // past the 50ms delay-start

    /// <summary>Wired by the dispatcher: immediate clamped scroll-offset write (the SetScrollOffset idiom — applies
    /// the content transform + virtual re-realize + scrollbar reveal). Returns false when clamped to a no-op (at the
    /// boundary), which stops the edge scroll exactly like WinUI's at-the-edge suppression.</summary>
    internal Func<NodeHandle, float, bool>? ScrollBy;

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

    /// <summary>True while an edge auto-scroll is armed or running — the host keeps frames coming and calls <see cref="Tick"/>.</summary>
    public bool HasActiveWork => _active && _edgeVelocity != 0f;

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

        var next = FindTarget(hit, out var refuser, out var refuserSpec);
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
        if (!_scrollViewport.IsNull && !_scene.IsLive(_scrollViewport))
        {
            _scrollViewport = NodeHandle.Null;
            _edgeVelocity = 0f;
            _edgeScrolling = false;
        }
    }

    /// <summary>Phase-7 host tick: drive the armed edge auto-scroll — hold the 50ms delay-start, then scroll the
    /// viewport by velocity·dt through <see cref="ScrollBy"/> (clamped writes; hitting the boundary stops it, the
    /// WinUI at-the-edge suppression). Returns true while armed/scrolling so the host keeps frames coming. 0-alloc.</summary>
    public bool Tick(float dtMs)
    {
        if (!_active || _edgeVelocity == 0f) return false;
        if (_scrollViewport.IsNull || !_scene.IsLive(_scrollViewport) || ScrollBy is null)
        {
            _edgeVelocity = 0f;
            _edgeScrolling = false;
            return false;
        }
        if (!_edgeScrolling)
        {
            _edgeDelayLeftMs -= dtMs;
            if (_edgeDelayLeftMs > 0f) return true;   // delay-start pending (LISTVIEWBASE_EDGE_SCROLL_START_DELAY_MSEC)
            _edgeScrolling = true;
        }
        float delta = _edgeVelocity * (dtMs / 1000f);
        if (delta != 0f && !ScrollBy(_scrollViewport, delta))
        {
            _edgeVelocity = 0f;       // pinned to the boundary → stop (cpp:1686-1690 at-the-edge suppression)
            _edgeScrolling = false;
            return false;
        }
        // The pointer did not move, but the target's content geometry did. Re-run the current destination's projection
        // so insertion slots/previews track edge autoscroll continuously instead of lagging until the next mouse event.
        if (!_over.IsNull && _scene.IsLive(_over)) _overSpec?.OnOver?.Invoke(_session);
        _requestRerender();
        return true;
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nearest enabled drop target on the chain whose AcceptKinds contains the session's Kind. Non-accepting
    /// targets do NOT block accepting ancestors (Flutter DragTarget pass-through).
    /// <para><paramref name="refuser"/> reports the NEAREST target that matched the kind but was turned away by its
    /// <see cref="DropTargetSpec.CanAccept"/> — the surface the user aimed at and that owes them a reason. It is
    /// meaningful only when the return value is Null (an accepting ancestor makes the drop succeed anyway).</para></summary>
    private NodeHandle FindTarget(NodeHandle hit, out NodeHandle refuser, out DropTargetSpec? refuserSpec)
    {
        refuser = NodeHandle.Null;
        refuserSpec = null;
        if (!_scene.HasDropTargets) return NodeHandle.Null;
        for (var n = hit; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if (!_scene.TryGetDropTarget(n, out var spec) || spec is null) continue;
            if (!spec.Accepts(_session.Kind)) continue;
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
            if (node.IsNull) _session.Caption = null;   // left the refuser: its reason leaves with it
            _requestRerender();
        }
        if (_refused.IsNull) return;
        _session.Caption = _refusedSpec?.RefusalCaption?.Invoke(_session);
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

    private void RefreshSpotlight(bool force)
    {
        int version = _scene.DropTargetsVersion;
        if (!force && version == _spotlightTargetVersion) return;
        _spotlightTargetVersion = version;
        _scene.RefreshDropSpotlight(_session);
        if (!_over.IsNull && _scene.IsLive(_over))
            _overSpec = _scene.TryGetDropTarget(_over, out var current) ? current : null;
        _scene.SetDropSpotlightOver(_over);
        _requestRerender();
    }

    private void End()
    {
        _scene.ClearDropSpotlight();
        _requestRerender();
        _spotlightTargetVersion = -1;
        _active = false;
        _over = NodeHandle.Null;
        _overSpec = null;
        _refused = NodeHandle.Null;
        _refusedSpec = null;
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
