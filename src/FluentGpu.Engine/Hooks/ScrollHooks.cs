using FluentGpu.Scroll;

namespace FluentGpu.Hooks;

/// <summary>The Ctx channel a <c>ScrollEl</c>/<c>VirtualListEl</c> mount publishes ITS <see cref="ScrollController"/>
/// onto (scroll-v3-plan §7.2), so any descendant resolves the nearest ancestor viewport's controller via
/// <see cref="Hooks.UseScroll"/> without prop-drilling. Null default — a tree with no enclosing viewport (or a
/// headless/no-host tree) resolves to no controller rather than throwing.</summary>
public static class ScrollControllerChannel
{
    public static readonly Context<ScrollController?> Current = new(null);
}

/// <summary>Free-standing scroll hooks (scroll-v3-plan §7.2's pinned <c>Hooks.UseScroll()</c> — an extension method on
/// <see cref="Component"/>, matching this codebase's other node-free hook groups: <c>GestureHooks.UseGesture</c>,
/// <c>MotionHooks.UseEntrance</c>).</summary>
public static partial class Hooks
{
    /// <summary>The nearest ancestor <c>ScrollEl</c>/<c>VirtualListEl</c> viewport's <see cref="ScrollController"/>,
    /// or null when this component is not inside a scrolling viewport (or that viewport's controller has not been
    /// attached yet). Subscribes this component's render-effect to the provider's signal, same as any other
    /// <c>UseContext</c> read — a controller's identity is stable for the life of its viewport, so this practically
    /// never re-renders.</summary>
    public static ScrollController? UseScroll(this Component c) => c.Context.UseContext(ScrollControllerChannel.Current);
}
