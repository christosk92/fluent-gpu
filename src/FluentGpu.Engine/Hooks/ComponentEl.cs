using FluentGpu.Dsl;

namespace FluentGpu.Hooks;

/// <summary>
/// An element that embeds a child <see cref="Component"/> (with its own hooks/state) into a parent's render output.
/// The reconciler instantiates the component once (via <see cref="Factory"/>), reuses it across renders of the same
/// <see cref="ComponentType"/>, and reconciles its rendered output as this node's content. This is what lets the
/// framework compose many stateful components rather than one root.
/// </summary>
public sealed record ComponentEl(Func<Component> Factory, Type ComponentType) : Element
{
    public override ushort ElementTypeId => 3;

    /// <summary>A representative real-content builder the <c>SkeletonDeriver</c> DERIVES in place of the single default
    /// bar it would otherwise emit for this opaque component (it cannot run the component's Render at derive time). Set
    /// by size-reactive primitives that hold a pure build function (Responsive / PagedShelf), so the section shimmers as
    /// its real shape. Null ⇒ the component still maps to one default bar (a true dynamic boundary).</summary>
    public Func<Element>? SkeletonProxy { get; init; }
}

/// <summary>Fluent helper: <c>Embed.Comp(() =&gt; new MyComponent())</c> embeds a stateful child component.</summary>
public static class Embed
{
    public static ComponentEl Comp<T>(Func<T> factory) where T : Component
        => new(() => factory(), typeof(T));
}
