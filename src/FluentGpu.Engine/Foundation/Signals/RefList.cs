using System.Collections.Generic;

namespace FluentGpu.Signals;

/// <summary>An insertion-ordered list of references that keeps its first two entries inline. A signal has one or two
/// subscribers and a computation reads one to three sources, so the common case allocates neither a <see cref="List{T}"/>
/// nor its backing array (together ~90 bytes per signal / computation); a third entry spills to a list. Order and
/// remove-shifts match <see cref="List{T}"/>, which the downward notify loops and the read-order poll rely on. A mutable
/// struct: hold it in a non-readonly field and never copy it.</summary>
internal struct RefList<T> where T : class
{
    private T? _a, _b;
    private List<T>? _more;
    private int _count;

    public readonly int Count => _count;

    public readonly T this[int i] => i == 0 ? _a! : i == 1 ? _b! : _more![i - 2];

    public void Add(T item)
    {
        switch (_count)
        {
            case 0: _a = item; break;
            case 1: _b = item; break;
            default: (_more ??= new()).Add(item); break;
        }
        _count++;
    }

    public readonly bool Contains(T item) => IndexOf(item) >= 0;

    public readonly int IndexOf(T item)
    {
        if (_count > 0 && ReferenceEquals(_a, item)) return 0;
        if (_count > 1 && ReferenceEquals(_b, item)) return 1;
        if (_more is not null)
            for (int i = 0; i < _more.Count; i++)
                if (ReferenceEquals(_more[i], item)) return i + 2;
        return -1;
    }

    public bool Remove(T item)
    {
        int at = IndexOf(item);
        if (at < 0) return false;
        // Shift the tail down one, exactly as List<T>.Remove does.
        if (at == 0)
        {
            _a = _b;
            if (_count > 2) { _b = _more![0]; _more.RemoveAt(0); }
            else _b = null;
        }
        else if (at == 1)
        {
            if (_count > 2) { _b = _more![0]; _more.RemoveAt(0); }
            else _b = null;
        }
        else _more!.RemoveAt(at - 2);
        _count--;
        return true;
    }

    public void Clear()
    {
        _a = null;
        _b = null;
        _more?.Clear();
        _count = 0;
    }
}
