using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;

namespace FluentGpu.Input;

/// <summary>Directional focus movement for roving/XY keyboard navigation (arrow keys in lists, grids, menus).</summary>
public enum FocusDirection : byte { Left, Right, Up, Down }

/// <summary>
/// Phase 2 (input dispatch): hit-tests the committed scene and routes pointer + keyboard. Pointer down→up over the
/// same node fires the click handler and focuses it; keyboard routes to the focused node and bubbles up ancestors
/// (Handled stops it); Tab moves focus through focusable nodes; Enter/Space activates a focused clickable (the
/// "one declaration, three modalities" contract). The full engine adds tunnel(Preview), gesture arena, XY-focus.
/// </summary>
public sealed class InputDispatcher
{
    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _focusables = new();
    private readonly List<NodeHandle> _scoped = new();   // reused buffer for scoped (within-a-root) focus collection
    private NodeHandle _down;
    private NodeHandle _focused;
    private NodeHandle _hovered;
    private NodeHandle _pressed;
    private NodeHandle _dragTarget;
    private NodeHandle _scrollHovered;
    private NodeHandle _scrollDragNode;
    private float _scrollDragGrab;
    private NodeHandle _contextDown;   // right-button press target — context menu fires on release over the same chain
    private NodeHandle _spaceArmed;    // focused clickable held via Space — activates on key-UP (WinUI semantics)
    private bool _accessKeyMode;       // Alt tapped → the next letter invokes a matching AccessKey mnemonic
    private bool _altPending;          // Alt is down with no intervening key (candidate for access-key-mode toggle)
    private readonly List<NodeHandle> _focusScopes = new();
    // Double/triple-click tracking (platform timestamps; slop + window per Win32 defaults, capped at 3).
    private uint _lastDownMs;
    private Point2 _lastDownPos;
    private int _lastDownButton = -1;
    private byte _clickCount = 1;
    private CursorId _lastCursor = CursorId.Arrow;

    public const float ClickSlopPx = 4f;
    public const uint DoubleClickMs = 500;

    private const float ScrollbarSize = 12f;
    private const float ScrollbarMinExpandedThumb = 30f;
    private const float ScrollbarMinCollapsedThumb = 32f;
    private const float ScrollbarSmallChange = 48f;

    public InputDispatcher(SceneStore scene)
    {
        _scene = scene;
        Drag = new DragController(scene, () => RequestRerender());
    }

    public NodeHandle Focused => _focused;

    /// <summary>The drag-reorder gesture engine (E5): armed by a press on a <c>CanDrag</c> chain, promoted past the
    /// 4px drag box, owning the pointer until release/Escape. Constructed with the dispatcher so every host gets
    /// item-drag without wiring; the host hooks <see cref="DragController.OnSettle"/> for the FLIP drop-glide.</summary>
    public DragController Drag { get; }

    /// <summary>Set by the host: a virtual list crossing an item boundary on scroll requests the next render.</summary>
    public Action RequestRerender { get; set; } = static () => { };

    /// <summary>Set by the host: notified when a node gains/loses hover or press, so the interaction animator can ease
    /// the brush transition (kept as delegates to keep Input decoupled from the Animation assembly).</summary>
    public Action<NodeHandle, bool>? OnHoverChanged;
    public Action<NodeHandle, bool>? OnPressChanged;

    /// <summary>When true, a wheel sets the scroll TARGET and the ScrollAnimator eases the offset (momentum/inertia +
    /// auto-hiding scrollbars). When false, the offset jumps immediately (the deterministic default for tests).</summary>
    public bool SmoothScroll;
    /// <summary>Set by the host: arm a viewport in the ScrollAnimator after a smooth-scroll wheel (decouples Input from Animation).</summary>
    public Action<NodeHandle>? OnScrollArmed;
    /// <summary>Set by the host: pointer is over a scrollable viewport → reveal its auto-hiding scrollbar.</summary>
    public Action<NodeHandle, bool>? OnScrollHover;
    public Action<NodeHandle>? OnScrollLeave;

    /// <summary>Set by the host: a RepeatButton was pressed (held) / released — drives the RepeatTicker auto-repeat.</summary>
    public Action<NodeHandle>? OnRepeatArmed;
    public Action<NodeHandle>? OnRepeatReleased;

    /// <summary>Set by the host: a global key preview run before focus routing (returns true = consumed). Lets a tree-level
    /// concern (an open overlay/flyout) intercept Escape regardless of where focus is, without stealing focus.</summary>
    public Func<int, bool>? OnKeyPreview;

    /// <summary>Raised when the window loses activation: pressed/hover/drag state has been cleared; the host closes
    /// light-dismiss overlays here (WinUI window-deactivation dismiss).</summary>
    public Action? OnWindowBlur;

    /// <summary>Raised when the resolved hover cursor changes — the host wires this to <c>IPlatformWindow.SetCursor</c>.</summary>
    public Action<CursorId>? OnCursorChanged;

    // ── focus scopes (modal focus trap: ContentDialog / flyout) ───────────────────────────────────
    /// <summary>Push a focus scope: Tab/Shift+Tab and arrow focus stay within <paramref name="root"/>'s subtree until popped.</summary>
    public void PushFocusScope(NodeHandle root) => _focusScopes.Add(root);
    public void PopFocusScope() { if (_focusScopes.Count > 0) _focusScopes.RemoveAt(_focusScopes.Count - 1); }
    private NodeHandle ScopeRoot
    {
        get
        {
            for (int i = _focusScopes.Count - 1; i >= 0; i--)
                if (_scene.IsLive(_focusScopes[i])) return _focusScopes[i];
            return _scene.Root;
        }
    }

    public int Dispatch(ReadOnlySpan<InputEvent> events)
    {
        // Drop transient state that pointed at a freed node.
        if (!_focused.IsNull && !_scene.IsLive(_focused)) _focused = NodeHandle.Null;
        if (!_hovered.IsNull && !_scene.IsLive(_hovered)) _hovered = NodeHandle.Null;
        if (!_pressed.IsNull && !_scene.IsLive(_pressed)) _pressed = NodeHandle.Null;
        if (!_dragTarget.IsNull && !_scene.IsLive(_dragTarget)) _dragTarget = NodeHandle.Null;
        if (!_scrollHovered.IsNull && !_scene.IsLive(_scrollHovered)) _scrollHovered = NodeHandle.Null;
        if (!_scrollDragNode.IsNull && !_scene.IsLive(_scrollDragNode)) _scrollDragNode = NodeHandle.Null;
        Drag.PruneDead();   // an armed/active drag node freed by a reconcile is abandoned (its columns are dead)

        int handled = 0;
        foreach (ref readonly var e in events)
        {
            switch (e.Kind)
            {
                case InputKind.PointerMove:
                    if (Drag.IsActive)   // an active item-drag owns the pointer (capture): no hover/scroll/slider routing
                    {
                        Drag.Move(e.PositionPx, e.Mods, e.TimestampMs);
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
                        handled++;
                        break;
                    }
                    SetState(ref _hovered, HitTest(e.PositionPx), NodeFlags.Hovered);
                    UpdateScrollHover(e.PositionPx);
                    if (DragScrollbar(e.PositionPx))
                    {
                        handled++;
                        break;
                    }
                    if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget))   // drag updates while held (slider/scrollbar)
                    {
                        _scene.GetDrag(_dragTarget)?.Invoke(LocalPos(_dragTarget, e.PositionPx));
                        handled++;
                    }
                    else if (!_hovered.IsNull && _scene.GetHoverMove(_hovered) is { } hm)   // bare-hover preview (RatingControl)
                    {
                        hm(LocalPos(_hovered, e.PositionPx));
                        handled++;
                    }
                    break;

                case InputKind.PointerDown:
                    if (e.Button == 1)   // right button: context-menu tracking only — never presses/activates
                    {
                        _contextDown = HitTestAny(e.PositionPx);
                        break;
                    }
                    if (e.Button != 0) break;   // middle button: no default interaction

                    if (TryScrollbarPointerDown(e.PositionPx))
                    {
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        _down = NodeHandle.Null;
                        handled++;
                        break;
                    }

                    TrackClickCount(in e);
                    _down = HitTest(e.PositionPx);
                    SetState(ref _pressed, _down, NodeFlags.Pressed);
                    if (!_down.IsNull)
                    {
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
                        if ((_scene.Interaction(_down).HandlerMask & (InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.RepeatBit | InteractionInfo.DragBit)) != 0) handled++;
                    }
                    break;

                case InputKind.PointerUp:
                    if (e.Button == 1)   // right button release → context request on the nearest handler in the chain
                    {
                        var ctxHit = HitTestAny(e.PositionPx);
                        if (!ctxHit.IsNull && ctxHit == _contextDown && DispatchContextRequest(ctxHit, e.PositionPx)) handled++;
                        _contextDown = NodeHandle.Null;
                        break;
                    }
                    if (e.Button != 0) break;

                    if (Drag.IsActive)
                    {
                        // Release after an active item-drag: complete the lifecycle; the click is SUPPRESSED (WinUI —
                        // a finished drag never raises the item's click/Tapped).
                        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
                        Drag.Complete(e.PositionPx, e.Mods, e.TimestampMs);
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
                    if (!up.IsNull && up == _down)
                    {
                        SetFocus(up, visual: false);        // pointer activation focuses but does NOT show the focus ring
                        if (!wasRepeat) _scene.GetClickHandler(up)?.Invoke();   // repeat nodes already fired via the ticker
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
                        SetFocus(_dragTarget, visual: false);
                        _scene.GetClickHandler(_dragTarget)?.Invoke();
                        handled++;
                    }
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
                    if (ScrollAt(e.PositionPx, e.ScrollDelta)) handled++;
                    break;

                case InputKind.PointerCancel:
                    CancelPointer();
                    break;

                case InputKind.WindowBlur:
                    CancelPointer();
                    CancelSpaceArm(fire: false);
                    SetState(ref _hovered, NodeHandle.Null, NodeFlags.Hovered);
                    _accessKeyMode = false; _altPending = false;
                    OnWindowBlur?.Invoke();
                    break;
            }
        }
        return handled;
    }

    /// <summary>Clear all in-flight pointer interaction (capture lost / window deactivated).</summary>
    private void CancelPointer()
    {
        Drag.Cancel();   // an in-flight item-drag aborts on capture loss (restores visuals, fires OnDragCanceled)
        SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
        if (!_down.IsNull && (_scene.IsLive(_down) && (_scene.Interaction(_down).HandlerMask & InteractionInfo.RepeatBit) != 0))
            OnRepeatReleased?.Invoke(_down);
        _down = NodeHandle.Null;
        _dragTarget = NodeHandle.Null;
        _scrollDragNode = NodeHandle.Null;
        _contextDown = NodeHandle.Null;
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

    /// <summary>Walk up from <paramref name="node"/> for the first enabled ContextBit handler and invoke it (local coords).</summary>
    private bool DispatchContextRequest(NodeHandle node, Point2 abs)
    {
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Disabled) != 0) continue;
            if ((_scene.Interaction(n).HandlerMask & InteractionInfo.ContextBit) == 0) continue;
            _scene.GetContextRequested(n)?.Invoke(LocalPos(n, abs));
            return true;
        }
        return false;
    }

    // ── scrolling (layout-free: write the content's -ScrollOffset transform; never relayout) ──

    /// <summary>Scroll the nearest scrollable ancestor under the pointer; bubbles to an outer scroller at the edge.</summary>
    /// <summary>The nearest scrollable viewport under the pointer (for revealing its scrollbar on hover).</summary>
    private NodeHandle ScrollableUnder(Point2 p)
    {
        for (var n = HitTestAny(p); !n.IsNull; n = _scene.Parent(n))
            if ((_scene.Flags(n) & NodeFlags.Scrollable) != 0) return n;
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

    private bool ScrollAt(Point2 p, float delta)
    {
        var node = HitTestAny(p);
        for (var n = node; !n.IsNull; n = _scene.Parent(n))
        {
            if ((_scene.Flags(n) & NodeFlags.Scrollable) == 0) continue;
            if (TryScrollNode(n, delta)) return true;   // consumed; else (at the edge) climb to an outer scroller
        }
        return false;
    }

    private bool TryScrollNode(NodeHandle n, float delta)
    {
        return ScrollBy(n, delta, SmoothScroll);
    }

    private bool ScrollBy(NodeHandle n, float delta, bool smooth)
    {
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float max = horizontal ? MathF.Max(0f, sc.ContentW - sc.ViewportW) : MathF.Max(0f, sc.ContentH - sc.ViewportH);

        if (smooth)
        {
            // Set the target; the ScrollAnimator eases the live offset toward it (+ virtualization re-realize + fade).
            float curTarget = horizontal ? sc.TargetX : sc.TargetY;
            float nextTarget = Math.Clamp(curTarget + delta, 0f, max);
            if (nextTarget == curTarget) return false;   // at the edge → bubble to an outer scroller
            if (horizontal) sc.TargetX = nextTarget; else sc.TargetY = nextTarget;
            sc.IdleMs = 0f;
            OnScrollArmed?.Invoke(n);
            return true;
        }

        float old = horizontal ? sc.OffsetX : sc.OffsetY;
        return SetScrollOffset(n, old + delta);
    }

    private bool SetScrollOffset(NodeHandle n, float offset)
    {
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float max = horizontal ? MathF.Max(0f, sc.ContentW - sc.ViewportW) : MathF.Max(0f, sc.ContentH - sc.ViewportH);
        float old = horizontal ? sc.OffsetX : sc.OffsetY;
        float next = Math.Clamp(offset, 0f, max);
        float target = horizontal ? sc.TargetX : sc.TargetY;
        if (next == old && target == next) return false;
        if (horizontal) { sc.OffsetX = next; sc.TargetX = next; }
        else { sc.OffsetY = next; sc.TargetY = next; }
        sc.IdleMs = 0f;
        ApplyScrollPosition(n, ref sc, horizontal, next);
        OnScrollArmed?.Invoke(n);
        return true;
    }

    private void ApplyScrollPosition(NodeHandle n, ref ScrollState sc, bool horizontal, float next)
    {
        // Layout-free scroll: the -ScrollOffset is the content child's LocalTransform (TransformDirty only).
        var content = sc.ContentNode;
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            cp.LocalTransform = Affine2D.Translation(horizontal ? -next : 0f, horizontal ? 0f : -next);
            _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        // Virtualization: keep transform-only scroll while the visible band remains inside the realized guard band.
        if (sc.ItemCount > 0)
        {
            int visibleFirst, visibleLast;
            float vp = horizontal ? sc.ViewportW : sc.ViewportH;
            if (sc.Layout is not null)   // fixed-geometry (stack/grid/custom)
            {
                float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                sc.Layout.Window(sc.ItemCount, cross, vp, next, 0, out visibleFirst, out visibleLast);
            }
            else if (_scene.TryGetExtents(n, out var t) && t is not null)   // variable (extent table)
            {
                visibleFirst = t.IndexAt(next);
                visibleLast = Math.Min(sc.ItemCount, t.IndexAt(next + vp) + 1);
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
        SetScrollOffset(_scrollDragNode, fraction * m.Max);

        ref ScrollState sc = ref _scene.ScrollRef(_scrollDragNode);
        sc.PointerOver = true;
        sc.PointerOverScrollbar = true;
        sc.IdleMs = 0f;
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
        Notify(flag, prev, on: false);
        Notify(flag, next, on: true);
    }

    private void Notify(NodeFlags flag, NodeHandle node, bool on)
    {
        if (flag == NodeFlags.Hovered && on) UpdateCursor(node);   // resolve even for null (back to arrow)
        if (node.IsNull) return;
        if (flag == NodeFlags.Hovered)
        {
            OnHoverChanged?.Invoke(node, on);
            if (!on && _scene.IsLive(node)) _scene.GetPointerExit(node)?.Invoke();   // pointer left → reset hover preview
        }
        else if (flag == NodeFlags.Pressed) OnPressChanged?.Invoke(node, on);
    }

    /// <summary>Resolve the cursor for the hover chain (nearest explicit <c>Cursor</c> / clickable hand) and notify on change.</summary>
    private void UpdateCursor(NodeHandle hover)
    {
        CursorId resolved = CursorId.Arrow;
        for (var n = hover; !n.IsNull; n = _scene.Parent(n))
        {
            if (!_scene.IsLive(n)) break;
            ref InteractionInfo ii = ref _scene.Interaction(n);
            if (ii.Cursor != CursorId.Arrow && (_scene.Flags(n) & NodeFlags.Disabled) == 0) { resolved = ii.Cursor; break; }
        }
        if (resolved == _lastCursor) return;
        _lastCursor = resolved;
        OnCursorChanged?.Invoke(resolved);
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
        // cancel). The pointer is still down — kill the click candidate so the eventual release does NOT click.
        if (key == Keys.Escape && Drag.IsActive)
        {
            Drag.Cancel();
            SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);
            _down = NodeHandle.Null;
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

        if (key == Keys.Tab)
        {
            CancelSpaceArm(fire: false);
            MoveFocus(forward: (e.Mods & KeyModifiers.Shift) == 0);
            return;
        }

        // Escape cancels a held Space-activation without firing (WinUI button semantics).
        if (key == Keys.Escape && !_spaceArmed.IsNull) { CancelSpaceArm(fire: false); return; }

        // Context-menu key (VK_APPS) / Shift+F10 → context request on the focused node (keyboard passes its centre).
        if ((key == Keys.Apps || (key == Keys.F10 && (e.Mods & KeyModifiers.Shift) != 0)) && !_focused.IsNull)
        {
            var r = _scene.AbsoluteRect(_focused);
            if (DispatchContextRequest(_focused, new Point2(r.X + r.W / 2f, r.Y + r.H / 2f))) return;
        }

        if (!_focused.IsNull)
        {
            bool clickable = (_scene.Flags(_focused) & NodeFlags.Disabled) == 0 &&
                             (_scene.Interaction(_focused).HandlerMask & InteractionInfo.ClickBit) != 0;

            // Modality 2 (WinUI semantics): Enter activates on key-DOWN; Space shows pressed while held and activates on
            // key-UP (a RepeatButton instead fires on every Space repeat, mirroring its pointer press-and-hold).
            if (key == Keys.Enter && clickable)
            {
                _scene.GetClickHandler(_focused)?.Invoke();
                return;
            }
            if (key == Keys.Space && clickable)
            {
                if ((_scene.Interaction(_focused).HandlerMask & InteractionInfo.RepeatBit) != 0)
                {
                    _scene.GetClickHandler(_focused)?.Invoke();   // RepeatButton: every keydown (incl. auto-repeat) fires
                    return;
                }
                if (!e.IsRepeat && _spaceArmed.IsNull)
                {
                    _spaceArmed = _focused;
                    _scene.Flags(_focused) |= NodeFlags.Pressed;
                    OnPressChanged?.Invoke(_focused, true);
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

        // Keyboard accelerators (WinUI ProcessKeyboardAccelerators order: after focused routing leaves it unhandled).
        if ((e.Mods & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0 || (key >= Keys.F1 && key <= Keys.F12))
        {
            var owner = _scene.FindAccelerator(key, e.Mods);
            if (!owner.IsNull) _scene.GetClickHandler(owner)?.Invoke();
        }
    }

    private void OnKeyUp(in InputEvent e)
    {
        if (e.KeyCode == Keys.Alt)
        {
            if (_altPending) _accessKeyMode = !_accessKeyMode;   // bare Alt tap toggles access-key mode
            _altPending = false;
            return;
        }
        if (e.KeyCode == Keys.Space && !_spaceArmed.IsNull)
            CancelSpaceArm(fire: true);   // Space released over the armed node → activate (WinUI key-up semantics)
    }

    /// <summary>Release a held Space-activation: clear the pressed visual; <paramref name="fire"/> = invoke the click.</summary>
    private void CancelSpaceArm(bool fire)
    {
        var node = _spaceArmed;
        _spaceArmed = NodeHandle.Null;
        if (node.IsNull || !_scene.IsLive(node)) return;
        _scene.Flags(node) &= ~NodeFlags.Pressed;
        OnPressChanged?.Invoke(node, false);
        if (fire && node == _focused && (_scene.Flags(node) & NodeFlags.Disabled) == 0)
            _scene.GetClickHandler(node)?.Invoke();
    }

    private bool InvokeAccessKey(char key)
    {
        var owner = _scene.FindAccessKey(key);
        if (owner.IsNull) return false;
        _accessKeyMode = false;
        _scene.GetClickHandler(owner)?.Invoke();
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
        if (!_spaceArmed.IsNull && node != _spaceArmed) CancelSpaceArm(fire: false);  // focus moved while Space held → no activation
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

    /// <summary>Event position (window space) → the node's LOCAL coords, clamped to its box (for slider/scrollbar drag).</summary>
    private Point2 LocalPos(NodeHandle node, Point2 abs)
    {
        var r = _scene.AbsoluteRect(node);
        return new Point2(Math.Clamp(abs.X - r.X, 0f, r.W), Math.Clamp(abs.Y - r.Y, 0f, r.H));
    }

    public NodeHandle HitTest(Point2 p)
        => _scene.Root.IsNull ? NodeHandle.Null : Hit(_scene.Root, 0f, 0f, p);

    /// <summary>Deepest visible node containing the point, regardless of click handler (used to find a scroll target).</summary>
    private NodeHandle HitTestAny(Point2 p)
        => _scene.Root.IsNull ? NodeHandle.Null : HitAny(_scene.Root, 0f, 0f, p);

    private NodeHandle HitAny(NodeHandle node, float ox, float oy, Point2 p)
    {
        var flags = _scene.Flags(node);
        if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible))
            return NodeHandle.Null;

        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);   // composited translation (scroll offset / animation) shifts self + subtree
        float ax = ox + b.X + np.LocalTransform.Dx, ay = oy + b.Y + np.LocalTransform.Dy;
        var rect = new RectF(ax, ay, b.W, b.H);
        if ((flags & NodeFlags.ClipsToBounds) != 0 && !rect.Contains(p)) return NodeHandle.Null;

        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = HitAny(c, ax, ay, p);
            if (!r.IsNull) result = r;
        }
        if (result.IsNull && rect.Contains(p))
            result = node;
        return result;
    }

    private NodeHandle Hit(NodeHandle node, float ox, float oy, Point2 p)
    {
        var flags = _scene.Flags(node);
        if ((flags & (NodeFlags.Visible | NodeFlags.HitTestVisible)) != (NodeFlags.Visible | NodeFlags.HitTestVisible))
            return NodeHandle.Null;

        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);   // composited translation (scroll offset / animation) shifts self + subtree
        float ax = ox + b.X + np.LocalTransform.Dx, ay = oy + b.Y + np.LocalTransform.Dy;
        var rect = new RectF(ax, ay, b.W, b.H);
        if ((flags & NodeFlags.ClipsToBounds) != 0 && !rect.Contains(p)) return NodeHandle.Null;

        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = Hit(c, ax, ay, p);
            if (!r.IsNull) result = r;
        }

        if (result.IsNull)
        {
            ref InteractionInfo ii = ref _scene.Interaction(node);
            if ((flags & NodeFlags.Disabled) == 0 &&   // disabled nodes don't hit-test (gates click/pointer/drag/repeat)
                (ii.HandlerMask & (InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit | InteractionInfo.DragBit)) != 0 &&
                rect.Contains(p))
            {
                result = node;
            }
        }
        return result;
    }
}
