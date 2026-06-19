using FluentGpu.Dsl;

namespace FluentGpu.Hooks;

/// <summary>
/// Reactive conditional (the Solid <c>&lt;Show&gt;</c>). <see cref="When"/> reads signals; the reconciler runs it inside a
/// boundary effect and mounts <see cref="Then"/> / <see cref="Else"/> as the condition flips — structural change only,
/// no parent re-render.
/// </summary>
public sealed record ShowEl(Func<bool> When, Element Then, Element? Else = null) : Element
{
    public override ushort ElementTypeId => 7;
}

/// <summary>
/// Reactive keyed list (the Solid <c>&lt;For&gt;</c>). <see cref="Count"/> / <see cref="ItemAt"/> read signals; the
/// reconciler runs them inside a boundary effect and re-realizes the children through the existing keyed diff when the
/// list changes — rows are created/moved/removed (and their state preserved by key), with no parent re-render.
/// </summary>
public sealed record ForEl(Func<int> Count, Func<int, Element> ItemAt, Func<int, string>? KeyOf = null) : Element
{
    public override ushort ElementTypeId => 10;
}

/// <summary>
/// Retained-page cache policy for <see cref="Flow.KeepAlive{TKey}"/>. Inactive entries stay mounted but detached from
/// the live scene tree; resource-heavy residency (currently image pins) is released unless
/// <see cref="ReleaseInactiveResources"/> is disabled.
/// </summary>
public sealed record KeepAliveOptions(int MaxEntries = 5, bool ReleaseInactiveResources = true, Func<object, bool>? ShouldCache = null)
{
    public static KeepAliveOptions Default { get; } = new();
}

/// <summary>
/// Reactive single-child keep-alive boundary. <see cref="Active"/> reads signals; the reconciler keys the active token,
/// parks inactive keyed subtrees, and evicts least-recently-used inactive entries beyond <see cref="Options"/>.
/// </summary>
public sealed record KeepAliveEl(Func<object> Active, Func<object, string> KeyOf, Func<object, Element> View, KeepAliveOptions Options) : Element
{
    public override ushort ElementTypeId => 14;
}

/// <summary>Fluent reactive control-flow: <c>Flow.Show(() =&gt; open.Value, panel)</c>, <c>Flow.For(() =&gt; xs.Value.Count, i =&gt; Row(xs.Value[i]))</c>.</summary>
public static class Flow
{
    public static ShowEl Show(Func<bool> when, Element then, Element? @else = null) => new(when, then, @else);
    public static ForEl For(Func<int> count, Func<int, Element> itemAt, Func<int, string>? keyOf = null) => new(count, itemAt, keyOf);
    public static KeepAliveEl KeepAlive<TKey>(Func<TKey> active, Func<TKey, string> keyOf, Func<TKey, Element> view, KeepAliveOptions? options = null)
        where TKey : notnull
        => new(() => active()!, o => keyOf((TKey)o), o => view((TKey)o), options ?? KeepAliveOptions.Default);
}
