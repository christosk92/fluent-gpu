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
}

/// <summary>Fluent helper: <c>Embed.Comp(() =&gt; new MyComponent())</c> embeds a stateful child component.</summary>
public static class Embed
{
    public static ComponentEl Comp<T>(Func<T> factory) where T : Component
        => new(() => factory(), typeof(T));
}
