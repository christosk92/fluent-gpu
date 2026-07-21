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

    /// <summary>Pending-tree internal: mount this component normally, then derive each output it renders. This is used
    /// by self-measuring controls such as Responsive, whose skeleton cannot be frozen at a guessed proxy width.</summary>
    public bool DeriveRenderedOutput { get; init; }

    internal SkeletonStyle? DerivedSkeletonStyle { get; init; }

    /// <summary>The RE-PUSHED live props for this embedded component (the parent→child data channel). A reused
    /// <c>ComponentEl</c> never re-runs its factory, so a plain field/ctor-arg freezes at mount; a value carried HERE is
    /// instead delivered LIVE at the reconciler's reuse seam into the child's <c>PropsSig</c> (equality-gated — the same
    /// physics as a context-provider signal) or, for a <see cref="IPropsHost"/> component, via
    /// <see cref="IPropsHost.ApplyProps"/>. The child reads it with <c>UseProps&lt;TProps&gt;()</c>. Set only through
    /// <see cref="Embed.Comp{T,TProps}(TProps, Func{T})"/>; <c>null</c> for the propless
    /// <see cref="Embed.Comp{T}(Func{T})"/> overload. Should be an immutable record so value equality coalesces
    /// unchanged re-pushes. See <c>docs/guide/reactivity.md</c> (parent→child props).</summary>
    public object? Props { get; init; }
}

/// <summary>Fluent helper: <c>Embed.Comp(() =&gt; new MyComponent())</c> embeds a stateful child component;
/// <c>Embed.Comp(props, () =&gt; new MyComponent())</c> also re-pushes live <paramref name="props"/> on every parent
/// re-render (read them in the child with <c>UseProps&lt;TProps&gt;()</c>).</summary>
public static class Embed
{
    /// <summary>Embed a stateful child component with no re-pushed props (the child owns its data via its own
    /// state/context/signals).</summary>
    public static ComponentEl Comp<T>(Func<T> factory) where T : Component
        => new(() => factory(), typeof(T));

    /// <summary>Embed a stateful child component and RE-PUSH <paramref name="props"/> to it on every parent re-render.
    /// The factory runs once (at mount); thereafter a reused instance receives the latest <paramref name="props"/> LIVE
    /// through its <c>PropsSig</c> / <see cref="IPropsHost"/> (equality-gated) — the child reads them with
    /// <c>UseProps&lt;TProps&gt;()</c>. <typeparamref name="TProps"/> should be an immutable record so a fresh-but-equal
    /// re-push is coalesced (no child re-render).</summary>
    public static ComponentEl Comp<T, TProps>(TProps props, Func<T> factory)
        where T : Component where TProps : class
        => new(() => factory(), typeof(T)) { Props = props };
}

/// <summary>Implemented by a component that receives re-pushed props through a SINGLE typed sink instead of the generic
/// <c>PropsSig</c> object slot — the seam the <c>[Props]</c> source generator (a later phase) targets so authors declare
/// plain typed props and get per-field signal-backed reactivity for free. When a component implements this, the
/// reconciler's reuse seam calls <see cref="ApplyProps"/> (wrapped in <c>Runtime.Batch</c> — the multi-field write path)
/// in preference to writing <c>PropsSig</c>, and <c>MountComponent</c> seeds it via <see cref="ApplyProps"/> too. The
/// implementation is responsible for its own per-field diffing (only changed fields notify).</summary>
public interface IPropsHost
{
    /// <summary>Receive the latest props (the boxed <see cref="ComponentEl.Props"/> object). Called once at mount (seed)
    /// and again at each parent re-render that re-pushes props; the implementation unpacks and diffs field-by-field.</summary>
    void ApplyProps(object props);
}
