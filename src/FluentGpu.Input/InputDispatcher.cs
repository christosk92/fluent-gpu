using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;

namespace FluentGpu.Input;

/// <summary>
/// Phase 2 (input dispatch): hit-tests the committed previous-frame scene and routes pointer down/up to click
/// handlers. The full engine adds the gesture arena, tunnel/bubble, focus, and edge-autoscroll; the slice does
/// the down→up-over-same-node click round-trip.
/// </summary>
public sealed class InputDispatcher
{
    private readonly SceneStore _scene;
    private NodeHandle _down;

    public InputDispatcher(SceneStore scene) => _scene = scene;

    public int Dispatch(ReadOnlySpan<InputEvent> events)
    {
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
                        _scene.GetClickHandler(up)?.Invoke();
                        handled++;
                    }
                    _down = NodeHandle.Null;
                    break;
            }
        }
        return handled;
    }

    public NodeHandle HitTest(Point2 p)
        => _scene.Root.IsNull ? NodeHandle.Null : Hit(_scene.Root, 0f, 0f, p);

    private NodeHandle Hit(NodeHandle node, float ox, float oy, Point2 p)
    {
        ref RectF b = ref _scene.Bounds(node);
        float ax = ox + b.X, ay = oy + b.Y;

        NodeHandle result = NodeHandle.Null;
        // children paint last-on-top; later sibling wins ties (slice layout is non-overlapping)
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
