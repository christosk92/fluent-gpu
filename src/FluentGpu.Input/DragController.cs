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

    /// <summary>Arm a drag candidate from a left press: walk up from <paramref name="pressTarget"/> for the nearest
    /// enabled node carrying <see cref="InteractionInfo.DragBit"/> (a press on a child of a draggable row arms the row,
    /// like WinUI's item container). Returns false when nothing in the chain is draggable.</summary>
    public bool TryArm(NodeHandle pressTarget, Point2 abs, PointerKind kind, KeyModifiers mods, uint timestampMs)
    {
        if (_active || !_node.IsNull) return false;
        for (var n = pressTarget; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.DragBit) == 0) continue;
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
    /// pointer after this call (the dispatcher then skips hover/scroll/slider routing).</summary>
    public bool Move(Point2 abs, KeyModifiers mods, uint timestampMs)
    {
        if (_node.IsNull) return false;
        if (!_scene.IsLive(_node)) { Reset(); return false; }
        _mods = mods;
        uint prevMs = _lastMs;
        UpdateVelocity(abs, timestampMs);

        float tx = abs.X - _pressAbs.X, ty = abs.Y - _pressAbs.Y;
        if (!_active)
        {
            // Per-axis drag box (dx > maxDx || dy > maxDy — ListViewBaseItem_Partial.cpp:1877).
            if (MathF.Abs(tx) <= DragThresholdPx && MathF.Abs(ty) <= DragThresholdPx) return false;
            if (YieldsToPan(tx, ty)) { Reset(); return false; }   // cross-axis pan over a scrollable wins the arena
            Promote(abs, tx, ty);
        }

        // Re-anchor: aim the visual at (grab origin + gesture delta) relative to the node's CURRENT resting origin —
        // identical to a plain (tx, ty) translate until a mid-drag commit moves the slot under the pointer.
        _lastTx = tx;
        _lastTy = ty;
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
        if (!_active || !_scene.IsLive(_node)) return false;
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
        _tgtTx = _grabVisualAbs.X + _lastTx - (curAbs.X - _appliedTx);
        _tgtTy = _grabVisualAbs.Y + _lastTy - (curAbs.Y - _appliedTy);
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
        p.LocalTransform = Affine2D.Translation(_appliedTx, _appliedTy).Multiply(_restingTransform);
        p.Opacity = DragOpacity;
        _scene.SetShadow(_node, DragShadow);
        _scene.Flags(_node) &= ~NodeFlags.HitTestVisible;
        _scene.Flags(_node) |= NodeFlags.DragGhost;
        _scene.DragGhost = _node;
        _scene.Mark(_node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    /// <summary>Release after an active drag: restore the resting visuals, fire <c>OnDragCompleted</c> (the app commits
    /// the reorder here), then hand the (dragged → resting) rects to <see cref="OnSettle"/> for the FLIP glide.
    /// Returns true iff a drag was active — the dispatcher suppresses the click. An armed-only candidate just disarms.
    /// <paramref name="suppressSettle"/> (E5-L2): an accepted DROP on a non-reorder target skips the glide — the
    /// payload was deposited there, so the visual snaps home instead of springing back
    /// (<see cref="DropTargetSpec.SettleOnDrop"/> opts a reorder target back into the glide).</summary>
    public bool Complete(Point2 abs, KeyModifiers mods, uint timestampMs, bool suppressSettle = false)
    {
        if (!_active) { Reset(); return false; }
        _mods = mods;
        UpdateVelocity(abs, timestampMs);

        var node = _node;
        bool live = _scene.IsLive(node);
        RectF draggedRect = default, restingRect = default;
        if (live)
        {
            draggedRect = _scene.AbsoluteRect(node);
            RestoreVisuals(node);
            restingRect = _scene.AbsoluteRect(node);
        }
        FillArgs(abs, abs.X - _pressAbs.X, abs.Y - _pressAbs.Y);
        Reset();   // idle BEFORE handlers run, so a handler-triggered press/arm sees a clean controller
        if (live)
        {
            _scene.GetDragCompleted(node)?.Invoke(_args);
            if (!suppressSettle && (draggedRect.X != restingRect.X || draggedRect.Y != restingRect.Y))
                OnSettle?.Invoke(node, draggedRect, restingRect);
        }
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
        Reset();
        if (!wasActive || !_scene.IsLive(node)) return;
        RectF draggedRect = _scene.AbsoluteRect(node);
        RestoreVisuals(node);
        RectF restingRect = _scene.AbsoluteRect(node);
        _scene.GetDragCanceled(node)?.Invoke();
        if (draggedRect.X != restingRect.X || draggedRect.Y != restingRect.Y)
            OnSettle?.Invoke(node, draggedRect, restingRect);
        _requestRerender();
    }

    /// <summary>Drop an armed candidate that never promoted (release inside the drag box ⇒ a plain click).</summary>
    public void Disarm()
    {
        if (!_active) Reset();
    }

    /// <summary>Called at dispatch start: an armed/active node freed by a reconcile is abandoned WITHOUT touching its
    /// (dead) columns — the visual state died with the slot.</summary>
    public void PruneDead()
    {
        if (!_node.IsNull && !_scene.IsLive(_node)) Reset();
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────

    private void Promote(Point2 abs, float tx, float ty)
    {
        _active = true;
        var grab = _scene.AbsoluteRect(_node);   // resting visual origin at gesture start (no drag translate applied yet)
        _grabVisualAbs = new Point2(grab.X, grab.Y);
        _appliedTx = 0f;
        _appliedTy = 0f;
        _tgtTx = _tgtTy = 0f;
        _springVx = _springVy = 0f;
        _sprung = false;
        ref NodePaint p = ref _scene.Paint(_node);
        _restingTransform = p.LocalTransform;
        _restingOpacity = p.Opacity;
        _hadShadow = _scene.TryGetShadow(_node, out _restingShadow);
        _wasHitTestVisible = (_scene.Flags(_node) & NodeFlags.HitTestVisible) != 0;

        p.Opacity = DragOpacity;                              // ListViewItemDragThemeOpacity 0.80
        _scene.SetShadow(_node, DragShadow);                  // lifted visual (ThemeShadow-equivalent depth)
        _scene.Flags(_node) &= ~NodeFlags.HitTestVisible;     // drop-target hit-tests see through the moving visual
        _scene.Flags(_node) |= NodeFlags.DragGhost;           // recorder hoists the subtree into the unclipped top band
        _scene.DragGhost = _node;
        _scene.Mark(_node, NodeFlags.PaintDirty);

        FillArgs(abs, tx, ty);
        _scene.GetDragStarted(_node)?.Invoke(_args);          // WinUI DragStarting — once, before the first delta
    }

    private void RestoreVisuals(NodeHandle node)
    {
        ref NodePaint p = ref _scene.Paint(node);
        p.LocalTransform = _restingTransform;
        p.Opacity = _restingOpacity;
        if (_hadShadow) _scene.SetShadow(node, _restingShadow);
        else _scene.ClearShadow(node);
        if (_wasHitTestVisible) _scene.Flags(node) |= NodeFlags.HitTestVisible;
        _scene.Flags(node) &= ~NodeFlags.DragGhost;           // back into the clipped main pass
        if (_scene.DragGhost == node) _scene.DragGhost = NodeHandle.Null;
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
        if (!_node.IsNull && _scene.DragGhost == _node) _scene.DragGhost = NodeHandle.Null;   // PruneDead path safety
        _node = NodeHandle.Null;
        _active = false;
        _sprung = false;
        _springVx = _springVy = 0f;
    }
}
