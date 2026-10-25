using System.Runtime.CompilerServices;

namespace FluentGpu.Signals;

/// <summary>A subscriber of a reactive source: an object <see cref="Computation"/> (<see cref="Row"/> = 0), or a row of a
/// <see cref="RxTable"/> (the engine's own computations, which are table rows, not objects: PERF r3 MODEL, DESIGN 2.5).
/// 16 bytes; compares by identity (same target, same row).</summary>
internal readonly struct Sub : IEquatable<Sub>
{
    /// <summary>The <see cref="Computation"/> (when <see cref="Row"/> is 0) or the <see cref="RxTable"/> that owns the row.</summary>
    internal readonly object Target;
    /// <summary>The table row; 0 means <see cref="Target"/> is an object computation.</summary>
    internal readonly int Row;

    internal Sub(Computation c) { Target = c; Row = 0; }
    internal Sub(RxTable table, int row) { Target = table; Row = row; }

    /// <summary>The object computation, or null for a row.</summary>
    internal Computation? Computation => Row == 0 ? Unsafe.As<Computation>(Target) : null;
    /// <summary>The table, or null for an object computation.</summary>
    internal RxTable? Table => Row == 0 ? null : Unsafe.As<RxTable>(Target);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkDirty(ISignalSource? cause)
    {
        if (Row == 0) Unsafe.As<Computation>(Target).MarkDirty(cause);
        else Unsafe.As<RxTable>(Target).MarkDirty(Row, cause);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkCheck(ISignalSource? cause)
    {
        if (Row == 0) Unsafe.As<Computation>(Target).MarkCheck(cause);
        else Unsafe.As<RxTable>(Target).MarkCheck(Row, cause);
    }

    /// <summary>DIAGNOSTIC: what this subscriber exists on behalf of (a component, usually), or null.</summary>
    internal object? DiagOwner => Row == 0 ? Unsafe.As<Computation>(Target).DiagOwner : Unsafe.As<RxTable>(Target).DiagOwnerOf(Row);

    /// <summary>DIAGNOSTIC: the computation's type name, or the row's kind. Allocates for a row; report time only.</summary>
    internal string DiagTypeName => Row == 0 ? Unsafe.As<Computation>(Target).GetType().Name : Unsafe.As<RxTable>(Target).DiagKindOf(Row);

    public bool Equals(Sub other) => ReferenceEquals(Target, other.Target) && Row == other.Row;
    public override bool Equals(object? obj) => obj is Sub s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Target), Row);
}

/// <summary>An insertion-ordered list of <see cref="Sub"/>s that keeps its first two entries inline (a signal has one or two
/// subscribers in the common case) and spills the rest into an array that grows by doubling and stays with the list. Order and
/// remove-shifts match <see cref="List{T}"/>, which the downward notify loops and the read-order poll rely on. A mutable struct:
/// hold it in a non-readonly field and never copy it.</summary>
internal struct SubList
{
    private Sub _a, _b;
    private Sub[]? _more;   // entries [2, _count)
    private int _count;

    public readonly int Count => _count;

    public readonly Sub this[int i] => i == 0 ? _a : i == 1 ? _b : _more![i - 2];

    public void Add(in Sub item)
    {
        switch (_count)
        {
            case 0: _a = item; break;
            case 1: _b = item; break;
            default:
                int at = _count - 2;
                if (_more is null) _more = new Sub[4];
                else if (at == _more.Length) Array.Resize(ref _more, _more.Length * 2);
                _more[at] = item;
                break;
        }
        _count++;
    }

    public readonly bool Contains(in Sub item) => IndexOf(item) >= 0;

    public readonly int IndexOf(in Sub item)
    {
        if (_count > 0 && _a.Equals(item)) return 0;
        if (_count > 1 && _b.Equals(item)) return 1;
        for (int i = 2; i < _count; i++)
            if (_more![i - 2].Equals(item)) return i;
        return -1;
    }

    public bool Remove(in Sub item)
    {
        int at = IndexOf(item);
        if (at < 0) return false;
        // Shift the tail down one, exactly as List<T>.Remove does.
        for (int i = at; i < _count - 1; i++) Set(i, this[i + 1]);
        _count--;
        Set(_count, default);   // drop the reference the vacated slot held
        return true;
    }

    private void Set(int i, in Sub value)
    {
        if (i == 0) _a = value;
        else if (i == 1) _b = value;
        else _more![i - 2] = value;
    }

    public void Clear()
    {
        _a = default;
        _b = default;
        if (_more is not null) Array.Clear(_more, 0, Math.Max(0, _count - 2));
        _count = 0;
    }
}
