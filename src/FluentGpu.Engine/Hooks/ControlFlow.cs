using System;
using System.Buffers;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>
/// Reactive conditional (the Solid <c>&lt;Show&gt;</c>). <see cref="When"/> reads signals; the reconciler runs it inside a
/// boundary effect and mounts <see cref="Then"/> / <see cref="Else"/> as the condition flips — structural change only,
/// no parent re-render required to toggle. A parent re-render, when it happens, replaces the stored element and
/// reschedules the boundary effect, so the latest <see cref="Then"/> / <see cref="Else"/> children and <see cref="When"/>
/// thunk are reconciled in place (children built from live render state stay fresh — no freeze at first mount).
/// </summary>
public sealed record ShowEl(Func<bool> When, Element Then, Element? Else = null) : Element
{
    public override ushort ElementTypeId => 7;
}

/// <summary>
/// The base of the reactive keyed list (the Solid <c>&lt;For&gt;</c>). All <c>For</c> shapes are ElementTypeId 10; the
/// reconciler runs <see cref="Fill"/> inside a boundary effect and re-realizes the children through the keyed diff when the
/// source changes — rows are created/moved/removed (state preserved by key) with no parent re-render. A parent re-render
/// replaces the stored element and reschedules the boundary effect (see <c>UpdateFor</c>), so fresh closures take hold
/// instead of freezing at first mount (the <c>Show</c>-parity fix). Build one with <see cref="Flow.For{T}(Func{IReadOnlyList{T}}, Func{T, string}, Func{T, int, Element})"/>.
/// </summary>
public abstract record ForElBase : Element
{
    public sealed override ushort ElementTypeId => 10;

    /// <summary>
    /// Read the items source ONCE (tracked — this is the single read that subscribes the boundary effect), fill
    /// <paramref name="buf"/><c>[0..n)</c> with keyed row elements, and return <c>n</c>. The buffer is pooled and owned by
    /// the reconciler (<c>MountFor</c>); implementations MUST NOT retain it past the call. Use <see cref="Grow"/> to size it
    /// (an <see cref="ArrayPool{Element}"/> return+rent). Runs only on a structural change / reschedule, never on a quiet frame.
    /// </summary>
    internal abstract int Fill(ref Element[] buf);

    /// <summary>Ensure <paramref name="buf"/> holds at least <paramref name="needed"/> slots, renting from the shared
    /// <see cref="ArrayPool{Element}"/> (returning the old buffer first). Starts from <see cref="Array.Empty{Element}"/> so
    /// a run rents exactly one buffer, which the reconciler stores and returns to the pool on the NEXT run / on unmount.</summary>
    private protected static void Grow(ref Element[] buf, int needed)
    {
        if (buf.Length >= needed) return;
        var old = buf;
        buf = ArrayPool<Element>.Shared.Rent(needed);
        if (old.Length > 0) ArrayPool<Element>.Shared.Return(old);
    }

#if DEBUG
    /// <summary>DEBUG duplicate-key tripwire: keyed rows sharing a key break the keyed diff (rows collapse to one
    /// identity). Throws naming the offending key. Runs only inside <see cref="Fill"/> (structural change), so it never
    /// touches a quiet frame's alloc budget.</summary>
    private protected static void CheckDuplicateKeys(Element[] buf, int n)
    {
        if (n < 2) return;
        var seen = new HashSet<string>(n);
        for (int i = 0; i < n; i++)
            if (buf[i].Key is string k && !seen.Add(k))
                throw new InvalidOperationException(
                    $"Flow.For: duplicate key '{k}' (row {i}). Row keys must be unique — a duplicate breaks the keyed " +
                    "diff (two rows share one identity). Use a stable per-item id, not the array index.");
    }
#endif
}

/// <summary>
/// The typed, mandatory-key reactive list. <see cref="Items"/> is read ONCE per boundary run (the snapshot that subscribes
/// the effect); rows are keyed by <see cref="KeyOf"/> (REQUIRED — a duplicate key trips the DEBUG tripwire) and built by
/// <see cref="Row"/>. Build with <see cref="Flow.For{T}(Func{IReadOnlyList{T}}, Func{T, string}, Func{T, int, Element})"/>.
/// </summary>
public sealed record ForEl<T>(Func<IReadOnlyList<T>> Items, Func<T, string> KeyOf, Func<T, int, Element> Row) : ForElBase
{
    internal override int Fill(ref Element[] buf)
    {
        var items = Items();                 // ONE tracked read of the source (kills the old N+1-subscribe path)
        int n = items.Count;
        Grow(ref buf, n);
        for (int i = 0; i < n; i++)
        {
            var item = items[i];
            buf[i] = Row(item, i) with { Key = KeyOf(item) };
        }
#if DEBUG
        CheckDuplicateKeys(buf, n);
#endif
        return n;
    }
}

/// <summary>
/// The index-driven <c>For</c> shape — INTERNAL only (the public index overload was deleted in favour of <see cref="ForEl{T}"/>).
/// Kept for engine/internal compositions that genuinely have no backing collection (a count + an index→element builder).
/// A key is still REQUIRED (<see cref="KeyOf"/>) — there is no positional fallback.
/// </summary>
internal sealed record IndexForEl(Func<int> Count, Func<int, Element> ItemAt, Func<int, string> KeyOf) : ForElBase
{
    internal override int Fill(ref Element[] buf)
    {
        int n = Count();                     // ONE tracked read of the count
        Grow(ref buf, n);
        for (int i = 0; i < n; i++)
            buf[i] = ItemAt(i) with { Key = KeyOf(i) };
#if DEBUG
        CheckDuplicateKeys(buf, n);
#endif
        return n;
    }
}

/// <summary>
/// Retained-page cache policy for <see cref="Flow.KeepAlive{TKey}"/>. Inactive entries stay mounted but detached from
/// the live scene tree; resource-heavy residency (currently image pins) is released unless
/// <see cref="ReleaseInactiveResources"/> is disabled.
/// </summary>
public sealed record KeepAliveOptions(
    int MaxEntries = 5,
    bool ReleaseInactiveResources = true,
    Func<object, bool>? ShouldCache = null,
    Func<object, object, LayoutTransition?>? TransitionFor = null)
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

/// <summary>Fluent reactive control-flow: <c>Flow.Show(() =&gt; open.Value, panel)</c>,
/// <c>Flow.For(() =&gt; tracks.Value, t =&gt; t.Id, (t, i) =&gt; Row(t))</c> (typed, keyed by a stable per-item id).</summary>
public static class Flow
{
    public static ShowEl Show(Func<bool> when, Element then, Element? @else = null) => new(when, then, @else);

    /// <summary>A typed, keyed reactive list. <paramref name="items"/> is snapshotted ONCE per structural change;
    /// <paramref name="key"/> is REQUIRED and must be a stable, unique per-item id (never the index — a re-order would
    /// then lose row state); <paramref name="row"/> builds each row (index provided for display).</summary>
    public static ForEl<T> For<T>(Func<IReadOnlyList<T>> items, Func<T, string> key, Func<T, int, Element> row)
        => new(items, key, row);

    /// <inheritdoc cref="For{T}(Func{IReadOnlyList{T}}, Func{T, string}, Func{T, int, Element})"/>
    public static ForEl<T> For<T>(Func<IReadOnlyList<T>> items, Func<T, string> key, Func<T, Element> row)
        => new(items, key, (item, _) => row(item));

    /// <summary>Signal-direct form of <see cref="For{T}(Func{IReadOnlyList{T}}, Func{T, string}, Func{T, int, Element})"/>
    /// — pass the collection signal itself (covariance binds a <c>Signal&lt;List&lt;T&gt;&gt;</c>). Read once per run.</summary>
    public static ForEl<T> For<T>(IReadSignal<IReadOnlyList<T>> items, Func<T, string> key, Func<T, int, Element> row)
        => new(() => items.Value, key, row);

    /// <inheritdoc cref="For{T}(IReadSignal{IReadOnlyList{T}}, Func{T, string}, Func{T, int, Element})"/>
    public static ForEl<T> For<T>(IReadSignal<IReadOnlyList<T>> items, Func<T, string> key, Func<T, Element> row)
        => new(() => items.Value, key, (item, _) => row(item));

    public static KeepAliveEl KeepAlive<TKey>(Func<TKey> active, Func<TKey, string> keyOf, Func<TKey, Element> view, KeepAliveOptions? options = null)
        where TKey : notnull
        => new(() => active()!, o => keyOf((TKey)o), o => view((TKey)o), options ?? KeepAliveOptions.Default);
}
