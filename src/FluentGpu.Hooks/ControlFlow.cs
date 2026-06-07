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

/// <summary>Fluent reactive control-flow: <c>Flow.Show(() =&gt; open.Value, panel)</c>, <c>Flow.For(() =&gt; xs.Value.Count, i =&gt; Row(xs.Value[i]))</c>.</summary>
public static class Flow
{
    public static ShowEl Show(Func<bool> when, Element then, Element? @else = null) => new(when, then, @else);
    public static ForEl For(Func<int> count, Func<int, Element> itemAt, Func<int, string>? keyOf = null) => new(count, itemAt, keyOf);
}
