using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluentGpu.Dsl;

/// <summary>An element's children (PERF r3 MODEL M4b): a range of handles on the tape (a collection expression `[a, b, c]` builds
/// on the stack and copies 16 bytes per child), or a heap array (built outside a region, an existing <c>Element[]</c>, or frozen).
/// <c>new Element[] {..}</c>, <c>list.ToArray()</c> and <c>List&lt;Element&gt;</c> convert implicitly.</summary>
[CollectionBuilder(typeof(ElementList), nameof(Create))]
public readonly struct ElementList : IReadOnlyList<Element>, IEquatable<ElementList>
{
    private readonly Element[]? _heap;
    private readonly int _start, _count;
    private readonly uint _stamp;

    private ElementList(Element[] heap) { _heap = heap; _start = 0; _count = heap.Length; _stamp = 0; }
    private ElementList(int start, int count, uint stamp) { _heap = null; _start = start; _count = count; _stamp = stamp; }

    public static ElementList Empty => default;

    public static ElementList Create(ReadOnlySpan<Element> items)
    {
        if (items.IsEmpty) return default;
        if (!Tape.IsOpen) return new ElementList(items.ToArray());
        var col = TapeColumn<Element>.Current;
        int start = col.Top;
        for (int i = 0; i < items.Length; i++) col.Push(in items[i]);
        return new ElementList(start, items.Length, Tape.Stamp);
    }

    public static implicit operator ElementList(Element[]? items) => items is null || items.Length == 0 ? default : new ElementList(items);
    public static implicit operator ElementList(List<Element>? items) => items is null ? default : Create(CollectionsMarshal.AsSpan(items));

    public int Count => _count;
    public int Length => _count;
    public bool IsEmpty => _count == 0;

    public Element this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new IndexOutOfRangeException();
            return _heap is { } h ? h[index] : TapeColumn<Element>.Current.At(_start + index, _stamp);
        }
    }

    public ReadOnlySpan<Element> AsSpan() => _heap is { } h ? h : _count == 0 ? default : TapeColumn<Element>.Current.Range(_start, _count, _stamp);

    public Element[] ToArray() => AsSpan().ToArray();

    /// <summary>This list with every child frozen, on the heap (DESIGN 2.2, escapes).</summary>
    public ElementList Freeze()
    {
        if (_count == 0) return default;
        var src = AsSpan();
        Element[]? copy = null;
        for (int i = 0; i < src.Length; i++)
        {
            var f = src[i].Freeze();
            if (copy is null && (_heap is null || !f.SameAs(src[i]))) { copy = new Element[src.Length]; src[..i].CopyTo(copy); }
            if (copy is not null) copy[i] = f;
        }
        return copy is null ? this : new ElementList(copy);
    }

    /// <summary>The same list (identity: the record compared its Element[] by reference).</summary>
    public bool Equals(ElementList o) => ReferenceEquals(_heap, o._heap) && _start == o._start && _count == o._count && _stamp == o._stamp;
    public override bool Equals(object? obj) => obj is ElementList o && Equals(o);
    public override int GetHashCode() => _heap is { } h ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(h) : HashCode.Combine(_start, _count, _stamp);

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<Element> IEnumerable<Element>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Element>
    {
        private readonly ElementList _list;
        private int _i;
        internal Enumerator(ElementList list) { _list = list; _i = -1; }
        public bool MoveNext() => ++_i < _list._count;
        public Element Current => _list[_i];
        object IEnumerator.Current => Current;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}
