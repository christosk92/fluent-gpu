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

    public InputDispatcher(SceneStore scene) => _scene = scene;

    public NodeHandle Focused => _focused;

    public int Dispatch(ReadOnlySpan<InputEvent> events)
    {
        if (!_focused.IsNull && !_scene.IsLive(_focused)) _focused = NodeHandle.Null;   // focus survived a free? drop it

        int handled = 0;
        foreach (ref readonly var e in events)
        {
            switch (e.Kind)
            {
                case InputKind.PointerDown:
                    _down = HitTest(e.PositionPx);
                    break;

                case InputKind.PointerUp:
                    var up = HitTest(e.PositionPx);
                    if (!up.IsNull && up == _down)
                    {
                        SetFocus(up);                       // pointer activation focuses the target
                        _scene.GetClickHandler(up)?.Invoke();
                        handled++;
                    }
                    _down = NodeHandle.Null;
                    break;

                case InputKind.Key:
                    OnKey(e.KeyCode);
                    break;
            }
        }
        return handled;
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
        SetFocus(_focusables[next]);
    }

    public void SetFocus(NodeHandle node)
    {
        if (node == _focused) return;
        if (!_focused.IsNull && _scene.IsLive(_focused)) _scene.Flags(_focused) &= ~NodeFlags.Focused;
        _focused = node;
        if (!node.IsNull) _scene.Flags(node) |= NodeFlags.Focused;
    }

    private void Collect(NodeHandle node)
    {
        if (node.IsNull) return;
        ref InteractionInfo ii = ref _scene.Interaction(node);
        if (ii.Focusable && (_scene.Flags(node) & NodeFlags.Visible) != 0) _focusables.Add(node);
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) Collect(c);
    }

    public NodeHandle HitTest(Point2 p)
        => _scene.Root.IsNull ? NodeHandle.Null : Hit(_scene.Root, 0f, 0f, p);

    private NodeHandle Hit(NodeHandle node, float ox, float oy, Point2 p)
    {
        ref RectF b = ref _scene.Bounds(node);
        float ax = ox + b.X, ay = oy + b.Y;

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
                (ii.HandlerMask & InteractionInfo.ClickBit) != 0 &&
                rect.Contains(p))
            {
                result = node;
            }
        }
        return result;
    }
}
