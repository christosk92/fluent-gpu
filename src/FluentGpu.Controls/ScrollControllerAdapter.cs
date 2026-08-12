using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>Pairs a plain vertical <see cref="ScrollEl"/> with an <see cref="IScrollController"/>.</summary>
public static class ScrollControllerAdapter
{
    public static Element Attach(ScrollEl scrollView, IScrollController controller)
    {
        ArgumentNullException.ThrowIfNull(scrollView);
        ArgumentNullException.ThrowIfNull(controller);
        if (scrollView.Horizontal)
            throw new ArgumentException("A vertical scroll controller requires a vertical ScrollEl.", nameof(scrollView));
        return Embed.Comp(new Props(scrollView, controller), static () => new ScrollControllerAdapterCore());
    }

    internal sealed record Props(ScrollEl ScrollView, IScrollController Controller);
}

internal sealed class ScrollControllerAdapterCore : Component
{
    public override Element Render()
    {
        var props = UseProps<ScrollControllerAdapter.Props>();
        var viewport = UseRef(NodeHandle.Null);
        var controller = props.Controller;
        var observer = props.ScrollView.OnScrollGeometryChanged;
        var mux = UseMemo(
            () => new ScrollGeometryObserverMux(controller, observer),
            DepKey.Combine(DepKey.FromRef(controller),
                observer is { } o ? DepKey.FromRef(o.Project, o.Action) : DepKey.Empty));

        void ScrollTo(ScrollToRequest request)
        {
            var node = viewport.Value;
            if (node.IsNull || Context.Scene is not { } scene || !scene.IsLive(node) || !scene.HasScroll(node)) return;
            ScrollIntoView.ScrollTo(Context, node, request.Offset, request.Animate);
        }

        void ScrollBy(ScrollByRequest request)
        {
            var node = viewport.Value;
            if (node.IsNull || Context.Scene is not { } scene || !scene.IsLive(node)
                || !scene.TryGetScroll(node, out var state)) return;
            ScrollIntoView.ScrollTo(Context, node, state.OffsetY + request.Delta, request.Animate);
        }

        UseEffect(() =>
        {
            controller.ScrollToRequested += ScrollTo;
            controller.ScrollByRequested += ScrollBy;
            return () =>
            {
                controller.ScrollToRequested -= ScrollTo;
                controller.ScrollByRequested -= ScrollBy;
                controller.SetIsScrollable(false);
                viewport.Value = NodeHandle.Null;
            };
        }, DepKey.FromRef(controller));

        Action<NodeHandle> capture = h => viewport.Value = h;
        return props.ScrollView with
        {
            OnScrollGeometryChanged = (mux.Project, mux.OnGeometryChanged),
            OnRealized = TemplateParts.Chain(capture, props.ScrollView.OnRealized),
        };
    }
}
