using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Input;

/// <summary>
/// The drag-reorder gesture engine (E5). A left press on (or inside) a <c>CanDrag</c> node ARMS a candidate; pointer
/// travel outside the drag box PROMOTES it from a click to a drag (the dispatcher then suppresses the click and routes
/// every move here — capture semantics). The promoted drag draws its visual on the dragged node itself: a parent-space
/// <c>LocalTransform</c> translate + 0.8 opacity + a flyout-class shadow
/// (WinUI <c>ListViewItemDragThemeOpacity</c> = 0.80 — microsoft-ui-xaml controls\dev\CommonStyles\ListViewItem_themeresources.xaml:7),
/// and the node stops hit-testing so drop-target queries see THROUGH the moving visual.
///
/// Threshold: the Windows drag box is <c>SM_CXDRAG</c>/<c>SM_CYDRAG</c> (4px default), tested per-axis
/// (<c>dx &gt; maxDx || dy &gt; maxDy</c> — microsoft-ui-xaml dxaml\xcp\dxaml\lib\ListViewBaseItem_Partial.cpp:1864-1878).
/// WinUI's list items double it (LISTVIEWBASEITEM_MOUSE_DRAG_THRESHOLD_MULTIPLIER = 2.0, same file :54); this engine
/// uses the 4px base box per plan E5, matching <see cref="InputDispatcher.ClickSlopPx"/>.
///
/// Axis-aware arena-lite (promotion-time arbitration): the item's reorder axis is its PARENT container's main axis
/// (row → horizontal item-drag). A gesture whose dominant axis is PERPENDICULAR to that, with a scrollable ancestor
/// that actually overflows along the gesture axis, yields to the pan (vertical pan over a scrollable beats a
/// horizontal item-drag) — the WinUI manipulation-arena outcome for a tab strip inside a scrolling page.
///
/// Drop-settle rides the existing FLIP pipeline: <c>Complete</c> restores the resting visuals and reports
/// (draggedRect → restingRect) through <see cref="OnSettle"/>; the host wires that to
/// <c>AnimEngine.AnimateBounds</c>, so the seeded position spring is retargeted velocity-continuously by the same
/// commit's <c>ApplyProjections</c> when the app's <c>OnDragCompleted</c> handler reorders the collection — displaced
/// siblings and the dropped item all animate through the one layout-transition path (reorder hints come free).
///
/// Mid-drag live reorder rides the same pipeline: when the consumer re-projects the children ORDER at the
/// dwell-committed target (<c>ReorderList.ProjectOrder</c>), displaced siblings FLIP to their new slots while
/// <see cref="Move"/> RE-ANCHORS the pointer-held visual — it strips the translate it last applied to find the node's
/// CURRENT resting origin, then aims at (grab origin + gesture delta), so a slot move under the pointer (or an
/// ancestor scrolling) never jumps the visual (the WinUI outcome: the dragged item tracks the pointer while
/// <c>MoveItemsForLiveReorder</c> shifts the rest — ListViewBase_Partial_Reorder.cpp:2254). The drag visuals are
/// re-asserted on every move because a mid-drag commit's patch restores the authored opacity/shadow/hit-test
/// (Reconciler ApplyBox writes them unconditionally).
///
/// GHOST (E5-L2): while a drag is active the lifted node carries <see cref="NodeFlags.DragGhost"/> and is published as
/// <see cref="SceneStore.DragGhost"/> — the recorder excludes it from the clipped main pass and re-walks its subtree
/// in an UNCLIPPED top band emitted last (mirroring the orphan pass), so the visual escapes every ancestor scissor (a
/// row dragged out of a clipped list keeps drawing) and paints above overlays — the Flutter/rbd ghost layer.
///
/// Spring-lag follow (the rbd feel): the PRESENTED translate eases toward the gesture target with a critically-damped
/// spring (<see cref="FollowOmega"/>) instead of pinning rigidly — 0-alloc per move/tick. It engages only when the
/// platform delivers real timestamps (consecutive non-zero <c>TimestampMs</c>); 0-stamp gestures (the headless
/// default) snap presented == target, keeping every position assertion deterministic. <see cref="SpringFollow"/>
/// false disables it outright. The host advances in-between-move easing (and the pointer-pin while an edge
/// auto-scroll moves the resting origin under a still pointer) via <see cref="Tick"/>.
/// </summary>
public sealed class DragController
{
    /// <summary>The drag box half-extent, tested per-axis (Win32 SM_CXDRAG/SM_CYDRAG default = 4;
    /// ListViewBaseItem_Partial.cpp:1871-1877. WinUI list items apply a ×2 multiplier — see class remarks).</summary>
    public const float DragThresholdPx = 4f;

    /// <summary>Dragged-visual opacity — WinUI <c>ListViewItemDragThemeOpacity</c> = 0.80
    /// (ListViewItem_themeresources.xaml:7, identical in all ThemeDictionaries incl. _perf2026).</summary>
    public const float DragOpacity = 0.80f;

    /// <summary>Flyout-class soft shadow under the drag visual (the engine's analytic equivalent of the 32px-depth
    /// ThemeShadow WinUI gives lifted drag visuals; values match <c>Dsl.Elevation.Flyout</c> — Input cannot reference Dsl).</summary>
    public static readonly ShadowSpec DragShadow =
        new(Blur: 32f, OffsetY: 8f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x46));

    /// <summary>Critically-damped spring rate (rad/s) of the ghost's pointer-follow lag — ~150ms visual settle, the
    /// react-beautiful-dnd "the ghost breathes behind the pointer" feel. Engine value (the WinUI OLE drag visual is an
    /// OS layer with no published spring; the adopted model is Flutter/rbd per the E5 user ruling).</summary>
    public const float FollowOmega = 38f;

    private readonly SceneStore _scene;
    private readonly Action _requestRerender;
    private readonly DragEventArgs _args = new();   // reused for the whole gesture — 0 steady-state alloc per move

    private NodeHandle _node;          // armed candidate / active drag node
    private bool _active;              // promoted past the drag box
    private Point2 _pressAbs;          // press point (window space) — Total deltas measure from here
    private Point2 _lastAbs;
    private uint _lastMs;
    private float _vx, _vy;            // smoothed pointer velocity (px/s)
    private KeyModifiers _mods;
    private PointerKind _kind;

    // Resting visual state captured at promotion, restored on settle/cancel.
    private Affine2D _restingTransform;
    private float _restingOpacity;
    private bool _hadShadow;
    private ShadowSpec _restingShadow;
    private bool _wasHitTestVisible;
    private bool _wasOpacityGroup;

    // Ghost styling captured at promotion (the dragged node's DragSource.Style, or the engine default). Opacity/shadow/
    // scale are re-asserted every move (ApplyPresented) because a mid-drag reconcile commit restores authored values.
    private DragVisualStyle _dragStyle = DragVisualStyle.Default;
    private float _dragW, _dragH;   // the ghost's box size at promotion — the pivot for a center scale (Style.Scale)

    // Pointer-follow anchor (see class remarks): the node's resting visual origin captured at promotion, plus the drag
    // translate currently written into LocalTransform — stripping it from AbsoluteRect recovers the CURRENT resting
    // origin even after a mid-drag order projection moved the slot or an ancestor scrolled.
    private Point2 _grabVisualAbs;
    private float _appliedTx, _appliedTy;   // the PRESENTED translate currently written into LocalTransform

    // Spring-lag follow (see class remarks): target translate (gesture-exact) vs presented translate (spring-eased).
    private float _lastTx, _lastTy;         // accumulated gesture deltas of the latest move (Tick re-aims from them)
    private float _tgtTx, _tgtTy;           // target translate the presented value eases toward
    private float _springVx, _springVy;     // spring velocity (px/s)
    private bool _sprung;                   // a spring step ran this gesture (a stray 0-stamp event must not teleport)

    /// <summary>Enable the critically-damped pointer-follow lag (default true). It only engages on gestures whose
    /// events carry real platform timestamps — 0-stamp (headless) gestures always track exactly.</summary>
    public bool SpringFollow { get; set; } = true;

    public DragController(SceneStore scene, Action requestRerender)
    {
        _scene = scene;
        _requestRerender = requestRerender;
        _node = NodeHandle.Null;
    }

    /// <summary>A candidate is armed (press seen, threshold not yet crossed).</summary>
    public bool IsArmed => !_node.IsNull && !_active;

    /// <summary>A drag is in flight (threshold crossed; the pointer is owned until release / Escape / cancel).</summary>
    public bool IsActive => _active;

    /// <summary>The node whose drag is in flight (<see cref="NodeHandle.Null"/> when idle/armed). The host's FLIP pass
    /// must SKIP this node — the pointer owns its presented transform until the drag ends.</summary>
    public NodeHandle ActiveNode => _active ? _node : NodeHandle.Null;

    /// <summary>The lift mode of the drag in flight (<see cref="DragLift.Ghost"/> when idle). THE seam L2 consults:
    /// the dispatcher passes it to <see cref="DragDropContext.TryBegin"/> so a Stationary session survives its source
    /// node being virtualized away (the chip, not the row, is the visual). Ghost while armed-but-not-promoted.</summary>
    public DragLift ActiveLift => _active ? _dragStyle.Lift : DragLift.Ghost;

    /// <summary>True while a <see cref="DragLift.Stationary"/> gesture is in flight whose SOURCE node has been freed by
    /// a reconcile: the gesture is deliberately still live (the chip carries it), so node-visual work and the node's own
    /// drag handlers are skipped for the rest of the gesture. Always false in Ghost mode (there the death aborts).</summary>
    public bool SourceRecycled => _active && _dragStyle.Lift == DragLift.Stationary && !_scene.IsLive(_node);

    /// <summary>The smoothed pointer velocity (px/s, ~50ms EMA) — fed into the L2 <see cref="DragDropContext"/> session.</summary>
    public float VelocityX => _vx;
    public float VelocityY => _vy;

    /// <summary>True while the presented (spring-lagged) translate is still easing toward the gesture target — the
    /// host keeps frames coming and calls <see cref="Tick"/>. Always false for snap-tracking gestures.</summary>
    public bool HasActiveWork => _active
        && (MathF.Abs(_tgtTx - _appliedTx) > 0.05f || MathF.Abs(_tgtTy - _appliedTy) > 0.05f
            || MathF.Abs(_springVx) > 1f || MathF.Abs(_springVy) > 1f);

    /// <summary>Set by the host: the drop-settle seam. Fired after <see cref="Complete"/>/<see cref="Cancel"/> restored
    /// the resting visuals, with the dragged presented rect and the resting rect — wire to
    /// <c>AnimEngine.AnimateBounds(node, fromAbs, toAbs, spec)</c> so the visual glides from the drop point into its
    /// slot (and is velocity-continuously retargeted by the reorder commit's FLIP pass). Null ⇒ the visual snaps home.</summary>
    public Action<NodeHandle, RectF, RectF>? OnSettle;

    /// <summary>Set by the host: fired when an ACTIVE drag's node was FREED by a reconcile (virtualized away, list
    /// rebuilt). The node's own <c>OnDragCanceled</c> column is dead and cannot be invoked, so this is the only abort
    /// notification for that path — the dispatcher wires it to <c>DragDropContext.Cancel()</c> so the L2 session and its
    /// drop spotlight can never outlive the gesture. Never fired for an armed-only candidate (no gesture began), and
    /// never for a <see cref="DragLift.Stationary"/> gesture (there the source's death is TOLERATED — see
    /// <see cref="SourceRecycled"/>).</summary>
    public Action? OnAbandoned;

    /// <summary>Set by the host: fired when a <see cref="DragLift.Stationary"/> gesture ENDS, with the settle window the
    /// preview chip should animate through — <see cref="DragSettlePhase.ToTarget"/> + the release point for an accepted
    /// drop, <see cref="DragSettlePhase.Home"/> + the source's resting rect for a refusal/cancel, and
    /// <see cref="DragSettlePhase.None"/> when there is nowhere to glide (a recycled source that nobody accepted).
    /// Ghost mode uses <see cref="OnSettle"/>'s FLIP instead and never fires this.</summary>
    public Action<DragSettlePhase, RectF>? OnStationarySettle;

    /// <summary>Arm a drag candidate from a left press: walk up from <paramref name="pressTarget"/> for the nearest
    /// enabled node carrying <see cref="InteractionInfo.DragBit"/> (a press on a child of a draggable row arms the row,
    /// like WinUI's item container). Returns false when nothing in the chain is draggable.
    /// <para>A node carrying <see cref="InteractionInfo.BlocksDragArmBit"/> (<c>Element.BlocksDragArm</c>) STOPS the
    /// walk at itself: a card's play FAB or "…" button is its own affordance, not a handle for dragging the card. It
    /// blocks only the ANCESTOR search — a barrier that is itself draggable still arms (its own DragBit is tested
    /// first), which is what lets a draggable row host a non-dragging child of its own.</para></summary>
    public bool TryArm(NodeHandle pressTarget, Point2 abs, PointerKind kind, KeyModifiers mods, uint timestampMs)
    {
        if (_active || !_node.IsNull) return false;
        for (var n = pressTarget; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            uint mask = _scene.Interaction(n).HandlerMask;
            if ((mask & InteractionInfo.DragBit) == 0)
            {
                if ((mask & InteractionInfo.BlocksDragArmBit) != 0) return false;   // barrier: no ancestor drag arms
                continue;
            }
            _node = n;
            _pressAbs = abs;
            _lastAbs = abs;
            _lastMs = timestampMs;
            _vx = _vy = 0f;
            _kind = kind;
            _mods = mods;
            return true;
        }
        return false;
    }

    /// <summary>Pointer move while armed or active. Armed: crossing the drag box either PROMOTES (fires
    /// <c>OnDragStarted</c> then the first <c>OnDragDelta</c>) or YIELDS to a cross-axis pan (arena-lite) and disarms.
    /// Active: applies the parent-space translate and fires <c>OnDragDelta</c>. Returns true iff the gesture owns the
    /// pointer after this call (the dispatcher then skips hover/scroll/slider routing).
    ///
    /// <paramref name="arenaGoverned"/> (the §7A touch path): when the gesture-arena already arbitrated this contact's
    /// DragReorder-vs-Pan via the axis-locked recognizer votes (input-a11y.md §7A.2), the per-axis <see cref="YieldsToPan"/>
    /// heuristic is BYPASSED — the arena's deterministic resolution is the single source of truth (the two-arbitration-models
    /// risk the plan flags is closed). The mouse path leaves it false, keeping the heuristic until the mouse arena lands.</summary>
    public bool Move(Point2 abs, KeyModifiers mods, uint timestampMs, bool arenaGoverned = false)
    {
        if (_node.IsNull) return false;
        if (!_scene.IsLive(_node))
        {
            // Stationary lift: the drag visual is the DragPreviewLayer chip, so a virtualized-away / rebuilt source row
            // must NOT kill the gesture (the E10 abort is a GHOST-mode concern — there the visual died with the slot).
            // Keep tracking the pointer; every node-visual write and the node's own handlers are simply skipped.
            if (!SourceRecycled) { Reset(); return false; }
            _mods = mods;
            UpdateVelocity(abs, timestampMs);
            _lastTx = abs.X - _pressAbs.X;
            _lastTy = abs.Y - _pressAbs.Y;
            _requestRerender();
            return true;
        }
        _mods = mods;
        uint prevMs = _lastMs;
        UpdateVelocity(abs, timestampMs);

        float tx = abs.X - _pressAbs.X, ty = abs.Y - _pressAbs.Y;
        if (!_active)
        {
            // Per-axis drag box (dx > maxDx || dy > maxDy — ListViewBaseItem_Partial.cpp:1877).
            if (MathF.Abs(tx) <= DragThresholdPx && MathF.Abs(ty) <= DragThresholdPx) return false;
            // Arena-governed touch (§7A): the DragReorder member already WON its arena over the Pan member on the
            // axis-locked vote, so a yield here would double-arbitrate (and contradict the arena). The mouse path keeps
            // the per-axis heuristic until its own arena lands.
            if (!arenaGoverned && YieldsToPan(tx, ty)) { Reset(); return false; }   // cross-axis pan over a scrollable wins
            Promote(abs, tx, ty);
        }

        // Re-anchor: aim the visual at (grab origin + gesture delta) relative to the node's CURRENT resting origin —
        // identical to a plain (tx, ty) translate until a mid-drag commit moves the slot under the pointer.
        _lastTx = tx;
        _lastTy = ty;
        if (_dragStyle.Lift == DragLift.Stationary)
        {
            // Stationary: the source never moves — no translate, no spring, no re-anchor. Only the dim + hit-test
            // opt-out are (re-)asserted, because a mid-drag reconcile restores the authored values.
            ApplyPresented();
            FillArgs(abs, tx, ty);
            _scene.GetDragDelta(_node)?.Invoke(_args);
            _requestRerender();
            return true;
        }
        RetargetFromRest();

        // Presented translate: spring toward the target when the gesture carries real platform timestamps
        // (the rbd lag); snap exactly otherwise (0-stamp headless gestures stay deterministic). Once a spring step
        // ran, an isolated invalid stamp leaves the presented value in place (Tick continues it) — never a teleport.
        uint dt = timestampMs != 0 && prevMs != 0 && timestampMs > prevMs ? timestampMs - prevMs : 0;
        if (SpringFollow && dt > 0 && dt < 1000) { StepSpring(dt); _sprung = true; }
        else if (!_sprung) SnapPresented();
        ApplyPresented();

        FillArgs(abs, tx, ty);
        _scene.GetDragDelta(_node)?.Invoke(_args);
        _requestRerender();
        return true;
    }

    /// <summary>Phase-7 host tick: re-aim at the node's CURRENT resting origin (an edge auto-scroll moves it under a
    /// still pointer — the ghost must stay pinned to the grab point) and advance the spring-lag easing between pointer
    /// moves. Returns true while the presented translate moved (the host requests the next frame). 0-alloc.</summary>
    public bool Tick(float dtMs)
    {
        if (!_active) return false;
        // Stationary lift owns no transform, so there is nothing to ease or re-pin — and a recycled source has no node
        // to touch at all. Both are steady-state no-ops (the chip follows the pointer through its own bound transform).
        if (_dragStyle.Lift == DragLift.Stationary) return false;
        if (!_scene.IsLive(_node)) return false;
        RetargetFromRest();
        float dx = _tgtTx - _appliedTx, dy = _tgtTy - _appliedTy;
        bool settled = MathF.Abs(dx) <= 0.05f && MathF.Abs(dy) <= 0.05f
            && MathF.Abs(_springVx) <= 1f && MathF.Abs(_springVy) <= 1f;
        if (settled) return false;
        // A gesture that never sprang (0-stamp headless) keeps snap-tracking here too — the ghost stays EXACTLY
        // pinned to the grab point while an edge auto-scroll slides the resting origin (deterministic for checks).
        if (SpringFollow && _sprung && dtMs > 0f) StepSpring((uint)MathF.Min(dtMs, 250f));
        else SnapPresented();
        ApplyPresented();
        _requestRerender();
        return true;
    }

    /// <summary>Aim the target translate at (grab origin + latest gesture delta) relative to the node's CURRENT
    /// resting origin — stripping the PRESENTED translate from AbsoluteRect recovers that origin even after a
    /// mid-drag order projection or an ancestor scroll moved the slot.</summary>
    private void RetargetFromRest()
    {
        var curAbs = _scene.AbsoluteRect(_node);
        float restX = curAbs.X - _appliedTx, restY = curAbs.Y - _appliedTy;   // the node's CURRENT resting origin
        _tgtTx = _grabVisualAbs.X + _lastTx - restX;
        _tgtTy = _grabVisualAbs.Y + _lastTy - restY;

        // E6 window clamp (the dnd-kit `restrictToWindowEdges` modifier): the ghost RECT — resting origin + target
        // translate at the promotion-time size — must stay inside the root's device rect, or a row dragged past the
        // window edge half-disappears (screenshot S4). Skipped when the dragged node IS the root (nothing encloses it)
        // or when the ghost cannot fit on that axis (clamping would pin it and fight the pointer).
        if (_node == _scene.Root || _dragW <= 0.5f || _dragH <= 0.5f) return;
        var rootRect = _scene.AbsoluteRect(_scene.Root);
        if (rootRect.W <= 0.5f || rootRect.H <= 0.5f) return;
        float minTx = rootRect.X - restX, maxTx = rootRect.X + rootRect.W - _dragW - restX;
        if (maxTx >= minTx) _tgtTx = _tgtTx < minTx ? minTx : (_tgtTx > maxTx ? maxTx : _tgtTx);
        float minTy = rootRect.Y - restY, maxTy = rootRect.Y + rootRect.H - _dragH - restY;
        if (maxTy >= minTy) _tgtTy = _tgtTy < minTy ? minTy : (_tgtTy > maxTy ? maxTy : _tgtTy);
    }

    /// <summary>Critically-damped spring step (semi-implicit Euler, ≤16ms substeps for stability at ω·dt &lt; 2).</summary>
    private void StepSpring(uint dtMs)
    {
        float remaining = dtMs;
        float px = _appliedTx, py = _appliedTy;
        while (remaining > 0f)
        {
            float h = MathF.Min(remaining, 16f) / 1000f;
            remaining -= 16f;
            _springVx += (FollowOmega * FollowOmega * (_tgtTx - px) - 2f * FollowOmega * _springVx) * h;
            _springVy += (FollowOmega * FollowOmega * (_tgtTy - py) - 2f * FollowOmega * _springVy) * h;
            px += _springVx * h;
            py += _springVy * h;
        }
        if (MathF.Abs(_tgtTx - px) <= 0.05f && MathF.Abs(_tgtTy - py) <= 0.05f
            && MathF.Abs(_springVx) <= 1f && MathF.Abs(_springVy) <= 1f)
        {
            px = _tgtTx; py = _tgtTy;
            _springVx = _springVy = 0f;
        }
        _appliedTx = px;
        _appliedTy = py;
    }

    private void SnapPresented()
    {
        _appliedTx = _tgtTx;
        _appliedTy = _tgtTy;
        _springVx = _springVy = 0f;
    }

    /// <summary>Write the presented translate (parent space) and re-assert the drag visuals: a mid-drag commit that
    /// re-rendered this item restored the authored opacity/shadow/hit-test/ghost flag (Reconciler ApplyBox patches
    /// them unconditionally). Idempotent rewrites.</summary>
    private void ApplyPresented()
    {
        ref NodePaint p = ref _scene.Paint(_node);
        if (_dragStyle.Lift == DragLift.Stationary)
        {
            // STATIONARY: dim + hit-test opt-out ONLY. No translate, no shadow, no NodeFlags.DragGhost and no
            // SceneStore.DragGhost — the row keeps its slot and its clip, and the recorder's ghost band stays idle
            // (the chip draws in the DragOverlay band instead). PaintDirty alone: nothing moved.
            // A same-list insertion that VIRTUALLY REMOVED this row owns its opacity for the gesture (it hides the whole
            // dragged block, this node included); re-asserting the style dim over it would strobe the press-source row
            // back to 0.4 on every reconcile frame while its siblings stay hidden.
            p.Opacity = _scene.DragSourceOpacityOverride ?? _dragStyle.Opacity;
            _scene.Flags(_node) &= ~NodeFlags.HitTestVisible;   // drop-target hit-tests see THROUGH the dimmed source
            _scene.Mark(_node, NodeFlags.PaintDirty);
            return;
        }
        p.LocalTransform = PresentedTransform();
        p.Opacity = _dragStyle.Opacity;
        // Composite the lifted subtree as ONE group (E2): per-primitive alpha let the ghost's own text blend against the
        // row beneath it twice (the S3 "both texts legible" garbage). WinUI Composition LayerVisual semantics — the
        // recorder's existing isOpacityGroup path emits PushLayer{Opacity}/PopLayer around exactly this subtree.
        p.OpacityGroup = true;
        _scene.SetShadow(_node, _dragStyle.Shadow ?? DragShadow);
        _scene.Flags(_node) &= ~NodeFlags.HitTestVisible;
        _scene.Flags(_node) |= NodeFlags.DragGhost;
        _scene.DragGhost = _node;
        _scene.DragGhostBackplate = _dragStyle.Backplate;   // E3: the opaque plate the recorder fills under the subtree
        _scene.Mark(_node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    /// <summary>The presented ghost transform: the spring-eased drag translate over the resting transform, optionally
    /// composed with a uniform scale ABOUT THE GHOST CENTER (<see cref="DragVisualStyle.Scale"/>). Scale = 1 (the
    /// default) is a no-op, so an unstyled drag is byte-identical to before.</summary>
    private Affine2D PresentedTransform()
    {
        Affine2D baseT = _restingTransform;
        float s = _dragStyle.Scale > 0f ? _dragStyle.Scale : 1f;
        if (s != 1f && (_dragW > 0.5f || _dragH > 0.5f))
        {
            float cx = _dragW * 0.5f, cy = _dragH * 0.5f;
            Affine2D centerScale = Affine2D.Translation(cx, cy).Multiply(Affine2D.Scale(s, s)).Multiply(Affine2D.Translation(-cx, -cy));
            baseT = _restingTransform.Multiply(centerScale);
        }
        return Affine2D.Translation(_appliedTx, _appliedTy).Multiply(baseT);
    }

    /// <summary>Release after an active drag: restore the resting visuals, fire <c>OnDragCompleted</c> (the app commits
    /// the reorder here), then hand the (dragged → resting) rects to <see cref="OnSettle"/> for the FLIP glide.
    /// Returns true iff a drag was active — the dispatcher suppresses the click. An armed-only candidate just disarms.
    /// <paramref name="suppressSettle"/> (E5-L2): an accepted DROP on a non-reorder target skips the glide — the
    /// payload was deposited there, so the visual snaps home instead of springing back
    /// (<see cref="DropTargetSpec.SettleOnDrop"/> opts a reorder target back into the glide).
    /// <paramref name="dropped"/> reports whether an L2 target accepted the payload — it only selects the STATIONARY
    /// settle phase (ToTarget vs Home) published through <see cref="OnStationarySettle"/>; Ghost mode ignores it.</summary>
    public bool Complete(Point2 abs, KeyModifiers mods, uint timestampMs, bool suppressSettle = false, bool dropped = false)
    {
        if (!_active) { Reset(); return false; }
        _mods = mods;
        UpdateVelocity(abs, timestampMs);

        var node = _node;
        bool live = _scene.IsLive(node);
        bool stationary = _dragStyle.Lift == DragLift.Stationary;
        RectF draggedRect = default, restingRect = default;
        if (live)
        {
            draggedRect = _scene.AbsoluteRect(node);
            RestoreVisuals(node);
            restingRect = _scene.AbsoluteRect(node);
        }
        // Stationary settle: an accepted drop glides the chip into the release point; a refusal glides it back to the
        // (still live) source row. A recycled source nobody accepted has nowhere to go — the chip just fades.
        var settlePhase = stationary
            ? (dropped ? DragSettlePhase.ToTarget : (live ? DragSettlePhase.Home : DragSettlePhase.None))
            : DragSettlePhase.None;
        RectF settleTarget = settlePhase == DragSettlePhase.ToTarget ? new RectF(abs.X, abs.Y, 0f, 0f) : restingRect;
        FillArgs(abs, abs.X - _pressAbs.X, abs.Y - _pressAbs.Y);
        Reset();   // idle BEFORE handlers run, so a handler-triggered press/arm sees a clean controller
        if (live)
        {
            _scene.GetDragCompleted(node)?.Invoke(_args);
            if (!stationary && !suppressSettle && (draggedRect.X != restingRect.X || draggedRect.Y != restingRect.Y))
                OnSettle?.Invoke(node, draggedRect, restingRect);
        }
        if (stationary) OnStationarySettle?.Invoke(settlePhase, settleTarget);
        _requestRerender();
        return true;
    }

    /// <summary>Abort the gesture (Escape / pointer-capture loss / window blur): restore the resting visuals, fire
    /// <c>OnDragCanceled</c>, and glide the visual home via <see cref="OnSettle"/>. A no-op when idle; an armed-only
    /// candidate silently disarms (WinUI: a canceled drag never raises a click or a drop).</summary>
    public void Cancel()
    {
        if (_node.IsNull) return;
        var node = _node;
        bool wasActive = _active;
        bool stationary = wasActive && _dragStyle.Lift == DragLift.Stationary;
        bool live = _scene.IsLive(node);
        Reset();
        if (!wasActive) return;
        if (!live)
        {
            // A recycled Stationary source still owes the chip its teardown (there is nothing to glide home TO).
            if (stationary) { OnStationarySettle?.Invoke(DragSettlePhase.None, default); _requestRerender(); }
            return;
        }
        RectF draggedRect = _scene.AbsoluteRect(node);
        RestoreVisuals(node);
        RectF restingRect = _scene.AbsoluteRect(node);
        _scene.GetDragCanceled(node)?.Invoke();
        if (stationary) OnStationarySettle?.Invoke(DragSettlePhase.Home, restingRect);
        else if (draggedRect.X != restingRect.X || draggedRect.Y != restingRect.Y)
            OnSettle?.Invoke(node, draggedRect, restingRect);
        _requestRerender();
    }

    /// <summary>Drop an armed candidate that never promoted (release inside the drag box ⇒ a plain click).</summary>
    public void Disarm()
    {
        if (!_active) Reset();
    }

    /// <summary>Called at dispatch start: an armed/active node freed by a reconcile is abandoned WITHOUT touching its
    /// (dead) columns — the visual state died with the slot. An ACTIVE gesture also reports the abort through
    /// <see cref="OnAbandoned"/> (the node's own <c>OnDragCanceled</c> column is dead), so the L2 session closes.</summary>
    public void PruneDead()
    {
        if (_node.IsNull || _scene.IsLive(_node)) return;
        // Stationary lift TOLERATES its source dying: the chip is the visual and the payload was resolved at promotion,
        // so the gesture (and its L2 session, which DragDropContext.PruneDead reparents onto the scene root) runs to a
        // real drop. Only the GHOST lift — whose visual literally WAS the freed node — must abort.
        if (SourceRecycled) return;
        bool wasActive = _active;
        Reset();
        if (wasActive) OnAbandoned?.Invoke();
    }

    /// <summary>Re-assert the presented ghost after a mid-drag reconcile commit restored the dragged node's AUTHORED
    /// opacity / shadow / hit-test (Reconciler ApplyBox writes them unconditionally). <see cref="Tick"/> alone cannot
    /// cover this: a settled (or snap-tracking) gesture early-outs before <c>ApplyPresented</c>, so the clobbered
    /// visuals would survive into the frame's record. Idempotent, 0-alloc; a no-op unless a live drag is active.</summary>
    public void ReassertPresented()
    {
        if (!_active || !_scene.IsLive(_node)) return;
        // Stationary re-asserts the dim + hit-test opt-out only; it owns no transform, so there is nothing to re-aim.
        if (_dragStyle.Lift != DragLift.Stationary) RetargetFromRest();
        ApplyPresented();
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────

    private void Promote(Point2 abs, float tx, float ty)
    {
        _active = true;
        var grab = _scene.AbsoluteRect(_node);   // resting visual origin at gesture start (no drag translate applied yet)
        _grabVisualAbs = new Point2(grab.X, grab.Y);
        _dragW = grab.W; _dragH = grab.H;        // ghost box size — the pivot for a center scale
        _appliedTx = 0f;
        _appliedTy = 0f;
        _tgtTx = _tgtTy = 0f;
        _springVx = _springVy = 0f;
        _sprung = false;
        // Capture the dragged node's ghost style (DragSource.Style), or the engine default. A plain CanDrag reorder
        // (no DragSource) and any source that leaves Style null both get the default 0.80 opacity + flyout shadow.
        _dragStyle = _scene.TryGetDragSource(_node, out var src) && src?.Style is { } st ? st : DragVisualStyle.Default;
        ref NodePaint p = ref _scene.Paint(_node);
        _restingTransform = p.LocalTransform;
        _restingOpacity = p.Opacity;
        _hadShadow = _scene.TryGetShadow(_node, out _restingShadow);
        _wasHitTestVisible = (_scene.Flags(_node) & NodeFlags.HitTestVisible) != 0;
        _wasOpacityGroup = p.OpacityGroup;

        // Both lift modes route through ApplyPresented so promotion and every re-assert write the SAME state (there is
        // exactly one place that knows what a lifted node looks like).
        ApplyPresented();

        FillArgs(abs, tx, ty);
        _scene.GetDragStarted(_node)?.Invoke(_args);          // WinUI DragStarting — once, before the first delta
    }

    private void RestoreVisuals(NodeHandle node)
    {
        ref NodePaint p = ref _scene.Paint(node);
        p.Opacity = _restingOpacity;
        if (_wasHitTestVisible) _scene.Flags(node) |= NodeFlags.HitTestVisible;
        // The gesture is over, so no destination owns this node's dim any more (the safety net for a destination whose
        // own teardown never ran — a virtualized-away insertion, a torn-down page).
        _scene.DragSourceOpacityOverride = null;
        if (_dragStyle.Lift == DragLift.Stationary)
        {
            // Stationary touched nothing but opacity + hit-test — restoring more (transform/shadow/ghost flag) would
            // clobber whatever the node legitimately owns (an in-flight FLIP, an authored elevation).
            _scene.Mark(node, NodeFlags.PaintDirty);
            return;
        }
        p.LocalTransform = _restingTransform;
        p.OpacityGroup = _wasOpacityGroup;
        if (_hadShadow) _scene.SetShadow(node, _restingShadow);
        else _scene.ClearShadow(node);
        _scene.Flags(node) &= ~NodeFlags.DragGhost;           // back into the clipped main pass
        if (_scene.DragGhost == node) { _scene.DragGhost = NodeHandle.Null; _scene.DragGhostBackplate = null; }
        _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    /// <summary>Arena-lite: a dominant-axis gesture PERPENDICULAR to the item's reorder axis (its parent container's
    /// main axis) yields when a scrollable ancestor actually overflows along the gesture axis — the pan owns it.</summary>
    private bool YieldsToPan(float dx, float dy)
    {
        bool vertical = MathF.Abs(dy) >= MathF.Abs(dx);
        var parent = _scene.Parent(_node);
        bool itemDragsHorizontally = !parent.IsNull && _scene.Layout(parent).Direction == 0;   // 0 = row container
        if (vertical != itemDragsHorizontally) return false;   // gesture runs along the item's own axis → the drag wins

        for (var n = parent; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0) continue;
            if (!_scene.TryGetScroll(n, out var sc)) continue;
            bool scrollsVertically = sc.Orientation == 0;
            if (scrollsVertically != vertical) continue;
            float overflow = vertical ? sc.ContentH - sc.ViewportH : sc.ContentW - sc.ViewportW;
            if (overflow > 0.5f) return true;   // real overflow along the gesture axis → the pan beats the item-drag
        }
        return false;
    }

    /// <summary>Exponential moving average of the pointer velocity (px/s), ~50ms horizon. Platform timestamps drive it;
    /// 0/duplicate stamps (headless default) leave the velocity unchanged.</summary>
    private void UpdateVelocity(Point2 abs, uint timestampMs)
    {
        uint dt = timestampMs - _lastMs;
        if (timestampMs != 0 && _lastMs != 0 && dt > 0 && dt < 1000)
        {
            float instX = (abs.X - _lastAbs.X) * 1000f / dt;
            float instY = (abs.Y - _lastAbs.Y) * 1000f / dt;
            float alpha = dt / (dt + 50f);
            _vx += (instX - _vx) * alpha;
            _vy += (instY - _vy) * alpha;
        }
        if (timestampMs != 0) _lastMs = timestampMs;
        _lastAbs = abs;
    }

    private void FillArgs(Point2 abs, float tx, float ty)
    {
        _args.Absolute = abs;
        _args.TotalDx = tx;
        _args.TotalDy = ty;
        _args.VelocityX = _vx;
        _args.VelocityY = _vy;
        _args.Mods = _mods;
        _args.Kind = _kind;
        if (_scene.IsLive(_node))
        {
            // LOGICAL moving origin = current resting origin + the gesture-target translate. The spring-lagged
            // PRESENTED visual may trail it; Local must stay EXACTLY the grab offset regardless (the
            // e5dragdrop.3 contract). Identical to AbsoluteRect when presented == target (snap gestures).
            var r = _scene.AbsoluteRect(_node);
            _args.Local = new Point2(abs.X - (r.X - _appliedTx + _tgtTx), abs.Y - (r.Y - _appliedTy + _tgtTy));
        }
    }

    private void Reset()
    {
        if (!_node.IsNull && _scene.DragGhost == _node) { _scene.DragGhost = NodeHandle.Null; _scene.DragGhostBackplate = null; }   // PruneDead path safety
        _node = NodeHandle.Null;
        _active = false;
        _sprung = false;
        _springVx = _springVy = 0f;
        // _dragStyle is deliberately NOT reset here: Cancel() resets BEFORE it restores the node's visuals, and the
        // restore has to know which lift mode it is undoing. Promote re-resolves it for every gesture.
    }
}
