using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A bound row template (EP6): the typed counterpart of <c>Func&lt;BoundItemScope&lt;T&gt;, Element&gt;</c> that also receives a
/// <see cref="RowStyleContext"/>. It runs ONCE per slot (and once more per slot whenever the list's
/// <see cref="BoundListProps{TRow}.TemplateStamp"/> changes), builds the COMPLETE slot root, and expresses everything that varies
/// by item as a bind that reads the scope. It may not embed components, slots, providers or boundaries
/// (<c>Embed.Comp</c>, <c>Slots.View</c>, <c>ctx.View</c>, <c>Flow.*</c>): the analyzer FGTH003 reports it, because such a row
/// could not be recycled by a signal write.
/// </summary>
public delegate Element BoundRowTemplate<TRow>(BoundItemScope<TRow> row, in RowStyleContext ctx);

/// <summary>
/// What a <see cref="BoundRowTemplate{TRow}"/> may read beyond its row: the active theme's application role table
/// (<see cref="App{T}"/>, <c>Tok.Definition.AppRoles</c>) and the list-level style metrics the application passed to
/// <c>ItemsView.CreateBound</c> (<see cref="Metrics"/>, an opaque object the application casts with
/// <see cref="MetricsAs{T}"/>). A ref struct: it lives for one template call, so a bind that needs a value reads it into a local
/// first. A theme change that alters either must also bump the list's template stamp (the rows are built once).
/// </summary>
public readonly ref struct RowStyleContext
{
    public RowStyleContext(object? metrics) => Metrics = metrics;

    /// <summary>The metrics object the application supplied for this list (null when none).</summary>
    public object? Metrics { get; }

    /// <summary>The metrics object as <typeparamref name="T"/>. Throws when none was supplied or it is another type.</summary>
    public T MetricsAs<T>() where T : class
    {
        if (Metrics is T typed) return typed;
        throw new InvalidOperationException(Metrics is null
            ? $"RowStyleContext.MetricsAs<{typeof(T).Name}>: the list was created without style metrics (pass metrics: to ItemsView.CreateBound)."
            : $"RowStyleContext.MetricsAs<{typeof(T).Name}>: the list's metrics object is a {Metrics.GetType().Name}.");
    }

    /// <summary>The application role table of the active theme definition (<c>Tok.Definition.AppRoles</c>) as
    /// <typeparamref name="T"/>. Throws when the definition carries none or it is another type.</summary>
    public T App<T>() where T : class
    {
        object? roles = Tok.Definition.AppRoles;
        if (roles is T typed) return typed;
        throw new InvalidOperationException(roles is null
            ? $"RowStyleContext.App<{typeof(T).Name}>: the active ThemeDefinition has no AppRoles."
            : $"RowStyleContext.App<{typeof(T).Name}>: the active ThemeDefinition's AppRoles is a {roles.GetType().Name}.");
    }
}

/// <summary>The non-generic face of <see cref="BoundListProps{TRow}"/> that <see cref="ItemsView"/> reads.</summary>
public interface IBoundListProps
{
    /// <summary>The list's template generation: a different value rebuilds every slot (see <c>VirtualListEl.TemplateStamp</c>).</summary>
    int TemplateStamp { get; }

    /// <summary>Build one slot root from its <see cref="RowScope"/> (called once per slot).</summary>
    Element Build(RowScope scope);
}

/// <summary>
/// The live props of a bound list (<c>ItemsView.CreateBound</c> embeds the list with <c>Embed.Comp(props, factory)</c>). The row
/// <paramref name="Template"/> and <paramref name="Metrics"/> are read when a slot is built; the
/// <paramref name="TemplateStamp"/> is the explicit "the template changed" edge: when it differs from the one the live slots were
/// built under, the ItemsView rebuilds EVERY slot (realized, prefix, the Spare pool, the content-type pools and the keep-alive
/// bucket) with the same index signals and resets the measured extents, in one reconcile (under the host's <c>SnapScope</c>
/// when a transaction has one open). An unchanged stamp rebuilds nothing, whatever the delegate identity does.
/// <para>Equality (the re-push gate) compares the stamp, the source and the comparer; the template and metrics ride the
/// props as latest-write values (a fresh lambda per parent render re-renders nothing), the delegate contract of
/// <c>component-props-contract.md</c>: what a row renders is a function of its item, the stamp and ambient theme state, never of
/// a captured value that changes without a stamp bump.</para>
/// </summary>
public sealed record BoundListProps<TRow>(
    BoundRowTemplate<TRow> Template,
    int TemplateStamp,
    BoundItemsSource<TRow> Source,
    IEqualityComparer<TRow>? ItemComparer = null,
    object? Metrics = null) : IBoundListProps
{
    public Element Build(RowScope scope)
    {
        var ctx = new RowStyleContext(Metrics);
        var row = new BoundItemScope<TRow>(scope, Source.BindItem(scope.Index, scope.Runtime!, comparer: ItemComparer));
        return Template(row, in ctx);
    }

    public bool Equals(BoundListProps<TRow>? other)
        => other is not null
        && TemplateStamp == other.TemplateStamp
        && ReferenceEquals(Source, other.Source)
        && Equals(ItemComparer, other.ItemComparer);

    public override int GetHashCode() => HashCode.Combine(TemplateStamp, Source);
}
