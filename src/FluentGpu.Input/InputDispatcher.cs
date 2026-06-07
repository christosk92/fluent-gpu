using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;

namespace FluentGpu.Input;

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
    private NodeHandle _down;
    private NodeHandle _focused;
    private NodeHandle _hovered;
    private NodeHandle _pressed;
    private NodeHandle _dragTarget;
    private NodeHandle _scrollHovered;

    public InputDispatcher(SceneStore scene) => _scene = scene;

    public NodeHandle Focused => _focused;

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

    public int Dispatch(ReadOnlySpan<InputEvent> events)
    {
        // Drop transient state that pointed at a freed node.
        if (!_focused.IsNull && !_scene.IsLive(_focused)) _focused = NodeHandle.Null;
        if (!_hovered.IsNull && !_scene.IsLive(_hovered)) _hovered = NodeHandle.Null;
        if (!_pressed.IsNull && !_scene.IsLive(_pressed)) _pressed = NodeHandle.Null;
        if (!_dragTarget.IsNull && !_scene.IsLive(_dragTarget)) _dragTarget = NodeHandle.Null;
        if (!_scrollHovered.IsNull && !_scene.IsLive(_scrollHovered)) _scrollHovered = NodeHandle.Null;

        int handled = 0;
        foreach (ref readonly var e in events)
        {
            switch (e.Kind)
            {
                case InputKind.PointerMove:
                    SetState(ref _hovered, HitTest(e.PositionPx), NodeFlags.Hovered);
                    UpdateScrollHover(e.PositionPx);
                    if (!_dragTarget.IsNull && _scene.IsLive(_dragTarget))   // drag updates while held (slider/scrollbar)
                    {
                        _scene.GetDrag(_dragTarget)?.Invoke(LocalPos(_dragTarget, e.PositionPx));
                        handled++;
                    }
                    break;

                case InputKind.PointerDown:
                    _down = HitTest(e.PositionPx);
                    SetState(ref _pressed, _down, NodeFlags.Pressed);
                    if (!_down.IsNull)
                    {
                        var local = LocalPos(_down, e.PositionPx);
                        _scene.GetPointerDown(_down)?.Invoke(local);                 // press-to-set
                        if (_scene.GetDrag(_down) is not null) _dragTarget = _down;  // begin a drag gesture
                        if ((_scene.Interaction(_down).HandlerMask & InteractionInfo.PointerBit) != 0) handled++;
                    }
                    break;

                case InputKind.PointerUp:
                    var up = HitTest(e.PositionPx);
                    SetState(ref _pressed, NodeHandle.Null, NodeFlags.Pressed);   // release
                    if (!up.IsNull && up == _down)
                    {
                        SetFocus(up, visual: false);        // pointer activation focuses but does NOT show the focus ring
                        _scene.GetClickHandler(up)?.Invoke();
                        handled++;
                    }
                    _down = NodeHandle.Null;
                    _dragTarget = NodeHandle.Null;
                    break;

                case InputKind.Key:
                    OnKey(e.KeyCode);
                    break;

                case InputKind.Wheel:
                    if (ScrollAt(e.PositionPx, e.ScrollDelta)) handled++;
                    break;
            }
        }
        return handled;
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
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (content <= viewport + 0.5f) return false;

        const float scrollBarSize = 12f; // WinUI ScrollBarSize from generic.xaml
        var r = _scene.AbsoluteRect(n);
        return horizontal
            ? p.Y >= r.Bottom - scrollBarSize && p.Y < r.Bottom
            : p.X >= r.Right - scrollBarSize && p.X < r.Right;
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
        ref ScrollState sc = ref _scene.ScrollRef(n);
        bool horizontal = sc.Orientation == 1;
        float max = horizontal ? MathF.Max(0f, sc.ContentW - sc.ViewportW) : MathF.Max(0f, sc.ContentH - sc.ViewportH);

        if (SmoothScroll)
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
        float next = Math.Clamp(old + delta, 0f, max);
        if (next == old) return false;
        if (horizontal) sc.OffsetX = next; else sc.OffsetY = next;

        // Layout-free scroll: the -ScrollOffset is the content child's LocalTransform (TransformDirty only).
        var content = sc.ContentNode;
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            cp.LocalTransform = Affine2D.Translation(horizontal ? -next : 0f, horizontal ? 0f : -next);
            _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        // Virtualization: re-realize the window ONLY when the offset crosses an item boundary (else transform-only).
        if (sc.ItemCount > 0)
        {
            int oldFirst, newFirst;
            if (sc.Layout is not null)   // fixed-geometry (stack/grid/custom) — re-realize when the window's first item moves
            {
                float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                float vp = horizontal ? sc.ViewportW : sc.ViewportH;
                sc.Layout.Window(sc.ItemCount, cross, vp, old, sc.Overscan, out oldFirst, out _);
                sc.Layout.Window(sc.ItemCount, cross, vp, next, sc.Overscan, out newFirst, out _);
            }
            else if (_scene.TryGetExtents(n, out var t) && t is not null)   // variable (extent table)
            {
                oldFirst = t.IndexAt(old);
                newFirst = t.IndexAt(next);
            }
            else { oldFirst = newFirst = 0; }
            if (oldFirst != newFirst) { _scene.Mark(n, NodeFlags.VirtualRangeDirty); RequestRerender(); }
        }
        return true;
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
        if (node.IsNull) return;
        if (flag == NodeFlags.Hovered) OnHoverChanged?.Invoke(node, on);
        else if (flag == NodeFlags.Pressed) OnPressChanged?.Invoke(node, on);
    }

    private void OnKey(int key)
    {
        if (key == Keys.Tab) { MoveFocus(forward: true); return; }
        if (_focused.IsNull) return;

        // Modality 2: Enter/Space activates a focused clickable.
        if ((key == Keys.Enter || key == Keys.Space) &&
            (_scene.Interaction(_focused).HandlerMask & InteractionInfo.ClickBit) != 0)
        {
            _scene.GetClickHandler(_focused)?.Invoke();
            return;
        }

        // Otherwise route to the focused node and bubble up ancestors until Handled.
        var args = new KeyEventArgs(key);
        for (var n = _focused; !n.IsNull; n = _scene.Parent(n))
        {
            _scene.GetKeyHandler(n)?.Invoke(args);
            if (args.Handled) return;
        }
    }

    /// <summary>Move focus to the next/previous focusable node in document order (cycles).</summary>
    public void MoveFocus(bool forward)
    {
        _focusables.Clear();
        Collect(_scene.Root);
        if (_focusables.Count == 0) { SetFocus(NodeHandle.Null); return; }

        int idx = _focusables.IndexOf(_focused);
        int n = _focusables.Count;
        int next = idx < 0 ? (forward ? 0 : n - 1) : (forward ? (idx + 1) % n : (idx - 1 + n) % n);
        SetFocus(_focusables[next], visual: true);   // keyboard focus → show the focus ring
    }

    /// <summary>Move focus. <paramref name="visual"/> = show the focus ring (keyboard/Tab); pointer focus passes false.</summary>
    public void SetFocus(NodeHandle node, bool visual = false)
    {
        if (!_focused.IsNull && _scene.IsLive(_focused)) _scene.Flags(_focused) &= ~(NodeFlags.Focused | NodeFlags.FocusVisual);
        _focused = node;
        if (node.IsNull) return;
        _scene.Flags(node) |= NodeFlags.Focused;
        if (visual) _scene.Flags(node) |= NodeFlags.FocusVisual; else _scene.Flags(node) &= ~NodeFlags.FocusVisual;
    }

    private void Collect(NodeHandle node)
    {
        if (node.IsNull) return;
        ref InteractionInfo ii = ref _scene.Interaction(node);
        if (ii.Focusable && (_scene.Flags(node) & NodeFlags.Visible) != 0) _focusables.Add(node);
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) Collect(c);
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
        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);   // composited translation (scroll offset / animation) shifts self + subtree
        float ax = ox + b.X + np.LocalTransform.Dx, ay = oy + b.Y + np.LocalTransform.Dy;
        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = HitAny(c, ax, ay, p);
            if (!r.IsNull) result = r;
        }
        if (result.IsNull && (_scene.Flags(node) & NodeFlags.Visible) != 0 && new RectF(ax, ay, b.W, b.H).Contains(p))
            result = node;
        return result;
    }

    private NodeHandle Hit(NodeHandle node, float ox, float oy, Point2 p)
    {
        ref RectF b = ref _scene.Bounds(node);
        ref NodePaint np = ref _scene.Paint(node);   // composited translation (scroll offset / animation) shifts self + subtree
        float ax = ox + b.X + np.LocalTransform.Dx, ay = oy + b.Y + np.LocalTransform.Dy;

        NodeHandle result = NodeHandle.Null;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var r = Hit(c, ax, ay, p);
            if (!r.IsNull) result = r;
        }

        if (result.IsNull)
        {
            var rect = new RectF(ax, ay, b.W, b.H);
            ref InteractionInfo ii = ref _scene.Interaction(node);
            var flags = _scene.Flags(node);
            if ((flags & NodeFlags.HitTestVisible) != 0 &&
                (ii.HandlerMask & (InteractionInfo.ClickBit | InteractionInfo.PointerBit)) != 0 &&
                rect.Contains(p))
            {
                result = node;
            }
        }
        return result;
    }
}
