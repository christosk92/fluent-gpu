using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Scroll.Runtime;

/// <summary>
/// Node-level programmatic scrolling over a live scene (design §9 <c>BringIntoView(node, align, move)</c>): resolves a
/// realized node to its nearest scrolling ancestor and to that viewport's CONTENT coordinates — the node's layout
/// position relative to the viewport's content node plus the realized window's <see cref="ScrollState.WindowOrigin"/>
/// (a virtualized list arranges its rows relative to that origin), scaled by the committed zoom — and hands the span to
/// the viewport's <see cref="ScrollHandle.BringIntoView"/>. For an item that is NOT realized (a virtualized index with no
/// node yet) resolve its content offset from the list's layout model and call the handle directly.
/// </summary>
public static class SceneScrollExtensions
{
    /// <summary>Brings <paramref name="node"/> into view inside its NEAREST scrolling ancestor. <paramref name="align"/>
    /// NaN = minimal move (no-op when already fully visible); otherwise the node's leading edge lands at
    /// <c>align·(viewport − extent)</c> (0 = leading edge, 0.5 = centred, 1 = trailing edge). <paramref name="margin"/> is
    /// the gutter kept between the node and the viewport edge it lands against. Returns false when the node has no live
    /// scrolling ancestor (or the viewport has no handle yet).</summary>
    public static bool BringIntoView(this SceneStore scene, NodeHandle node, float align = float.NaN,
        ScrollMove move = ScrollMove.Glide, double margin = 0.0)
    {
        if (node.IsNull || !scene.IsLive(node)) return false;
        var vp = scene.Parent(node);
        while (!vp.IsNull && !scene.HasScroll(vp)) vp = scene.Parent(vp);
        return !vp.IsNull && scene.BringIntoView(vp, node, align, move, margin);
    }

    /// <summary>Same, against an EXPLICIT viewport — for a composing control that must scroll its own scroller and not
    /// some outer page. <paramref name="node"/> must be inside <paramref name="viewport"/>'s content.</summary>
    public static bool BringIntoView(this SceneStore scene, NodeHandle viewport, NodeHandle node, float align = float.NaN,
        ScrollMove move = ScrollMove.Glide, double margin = 0.0)
    {
        if (viewport.IsNull || node.IsNull || !scene.IsLive(viewport) || !scene.IsLive(node) || !scene.HasScroll(viewport)) return false;
        ref ScrollState sc = ref scene.ScrollRef(viewport);
        var content = sc.ContentNode;
        if (content.IsNull || !scene.IsLive(content)) return false;
        var handle = scene.ScrollHandleFor(viewport);
        if (handle is null) return false;
        bool horizontal = sc.Orientation == 1;
        float zoom = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        RectF nodeRect = scene.AbsoluteLayoutRect(node);
        RectF contentRect = scene.AbsoluteLayoutRect(content);
        double local = horizontal ? nodeRect.X - contentRect.X : nodeRect.Y - contentRect.Y;
        double extent = horizontal ? nodeRect.W : nodeRect.H;
        handle.BringIntoView((sc.WindowOrigin + local) * zoom, extent * zoom, align, move, margin);
        return true;
    }
}
