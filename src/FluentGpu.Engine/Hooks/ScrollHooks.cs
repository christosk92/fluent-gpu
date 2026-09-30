using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Hooks;

/// <summary>The context channel every <c>ScrollEl</c>/<c>VirtualListEl</c> provides to its content at mount (re-asserted
/// on every patch): the viewport's bound <see cref="ScrollHandle"/> (the authored one, else the host-minted one). A
/// descendant's <c>UseScroll()</c> resolves the NEAREST scroller through it with no prop drilling. Null default — a tree
/// outside every viewport, or a headless tree with no host, resolves to no scroller.</summary>
public static class ScrollCtx
{
    public static readonly Context<ScrollHandle?> Nearest = new(null);
}
