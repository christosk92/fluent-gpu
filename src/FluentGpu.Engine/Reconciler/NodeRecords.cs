using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Reconciler;

/// <summary>The node RECORDS (PERF r3 MODEL M4b, DESIGN 2.3): per node, the element it was last mounted or reconciled against,
/// held as a value in a typed column of its kind (a heap kind keeps its object). The reconciler reads a node's old element
/// here instead of retaining the element tree; <see cref="Get"/> returns a record handle, so every <c>Element old</c> consumer
/// reads it unchanged. The identity skip (the former <c>ReferenceEquals(newEl, oldEl)</c>) compares the
/// <see cref="FrozenElement.Serial"/> the record came from, not a reference, so a record keeps nothing alive but its own
/// members.</summary>
internal sealed class NodeRecords
{
    private byte[] _kind = new byte[256];       // the stored element's type id (0 = no record)
    private uint[] _src = new uint[256];        // FrozenElement.Serial of the stored element (0 = not a frozen element)
    private object?[] _heap = new object?[256]; // a heap kind's element (HeapElement)
    private readonly RecordColumn?[] _cols = new RecordColumn?[32];

    internal bool Has(int idx) => (uint)idx < (uint)_kind.Length && _kind[idx] != 0;

    /// <summary>The node's record (a handle valid until the node's next <see cref="Store"/>), or none.</summary>
    internal Element Get(int idx)
    {
        if ((uint)idx >= (uint)_kind.Length) return default;
        ushort k = _kind[idx];
        if (k == 0) return default;
        var col = _cols[k];
        if (col is null) return new Element((HeapElement)_heap[idx]!);
        return new Element(col, k, idx, col.Stamps[idx]);
    }

    /// <summary>The node's record, or null.</summary>
    internal Element? GetOpt(int idx) => Has(idx) ? Get(idx) : null;

    /// <summary>The record came from <paramref name="el"/> itself (the same frozen element, the same heap element).</summary>
    internal bool IsSource(int idx, Element el)
    {
        if (!Has(idx) || _kind[idx] != el.ElementTypeId) return false;
        if (el.Obj is FrozenElement f) return _src[idx] == f.Serial;
        if (el.Obj is HeapElement h) return ReferenceEquals(_heap[idx], h);
        return false;
    }

    internal uint SourceSerial(int idx) => (uint)idx < (uint)_src.Length ? _src[idx] : 0;

    internal void Store(int idx, Element el)
    {
        if (idx >= _kind.Length)
        {
            int n = Math.Max(_kind.Length * 2, idx + 1);
            Array.Resize(ref _kind, n); Array.Resize(ref _src, n); Array.Resize(ref _heap, n);
        }
        ushort k = el.ElementTypeId;
        ushort old = _kind[idx];
        if (old != 0 && old != k) ClearSlot(idx, old);
        _kind[idx] = (byte)k;
        _src[idx] = el.Obj is FrozenElement f ? f.Serial : 0;
        switch (k)
        {
            case BoxEl.TypeId: StoreAs<BoxEl>(idx, k, el); break;
            case TextEl.TypeId: StoreAs<TextEl>(idx, k, el); break;
            case ComponentEl.TypeId: StoreAs<ComponentEl>(idx, k, el); break;
            case ScrollEl.TypeId: StoreAs<ScrollEl>(idx, k, el); break;
            case ShowEl.TypeId: StoreAs<ShowEl>(idx, k, el); break;
            case ImageEl.TypeId: StoreAs<ImageEl>(idx, k, el); break;
            case GridEl.TypeId: StoreAs<GridEl>(idx, k, el); break;
            case PolylineStrokeEl.TypeId: StoreAs<PolylineStrokeEl>(idx, k, el); break;
            case SpanTextEl.TypeId: StoreAs<SpanTextEl>(idx, k, el); break;
            case IconLayerEl.TypeId: StoreAs<IconLayerEl>(idx, k, el); break;
            case PathEl.TypeId: StoreAs<PathEl>(idx, k, el); break;
            case ListRowEl.TypeId: StoreAs<ListRowEl>(idx, k, el); break;
            case SeriesEl.TypeId: StoreAs<SeriesEl>(idx, k, el); break;
            case SpriteFieldEl.TypeId: StoreAs<SpriteFieldEl>(idx, k, el); break;
            default: _heap[idx] = el.Obj; break;   // a heap kind (boundaries): the object
        }
    }

    private void StoreAs<T>(int idx, ushort k, Element el) where T : struct
    {
        var col = (RecordColumn<T>)(_cols[k] ??= new RecordColumn<T>());
        col.Ensure(idx);
        uint stamp = col.NextStamp();
        col.Items[idx] = el.Ref<T>();
        col.Stamps[idx] = stamp;
    }

    /// <summary>The node left the tree: drop its record (no reference outlives the node).</summary>
    internal void Clear(int idx)
    {
        if ((uint)idx >= (uint)_kind.Length) return;
        ushort k = _kind[idx];
        if (k != 0) ClearSlot(idx, k);
        _kind[idx] = 0;
        _src[idx] = 0;
    }

    private void ClearSlot(int idx, ushort k)
    {
        if (_cols[k] is { } c) c.Clear(idx);
        else _heap[idx] = null;
    }
}
