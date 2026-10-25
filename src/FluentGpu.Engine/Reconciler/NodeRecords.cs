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
    private string?[] _key = new string?[256];  // the stored element's Key (the keyed child diff reads the old side here)
    private int[] _slot = new int[256];         // the node's slot in its kind's column (a slab: a column holds only its kind)
    private readonly RecordColumn?[] _cols = new RecordColumn?[32];

    internal bool Has(int idx) => (uint)idx < (uint)_kind.Length && _kind[idx] != 0;

    /// <summary>The Key of the element the node was last reconciled against (null: unkeyed, or no record).</summary>
    internal string? KeyOf(int idx) => (uint)idx < (uint)_key.Length ? _key[idx] : null;

    /// <summary>The node's record (a handle valid until the node's next <see cref="Store"/>), or none.</summary>
    internal Element Get(int idx)
    {
        if ((uint)idx >= (uint)_kind.Length) return default;
        ushort k = _kind[idx];
        if (k == 0) return default;
        var col = _cols[k];
        if (col is null) return new Element((HeapElement)_heap[idx]!);
        int s = _slot[idx];
        return new Element(col, k, s, col.Stamps[s]);
    }

    /// <summary>The node's record, or null.</summary>
    internal Element? GetOpt(int idx) => Has(idx) ? Get(idx) : null;

    /// <summary>The record came from <paramref name="el"/> itself (the same frozen element, the same heap element).</summary>
    internal bool IsSource(int idx, Element el)
    {
        if (!Has(idx) || _kind[idx] != el.ElementTypeId) return false;
        if (el.Obj is FrozenElement f) return _src[idx] == f.Serial;

        if (el.Obj is ContextProviderEl cp) return _src[idx] == cp.Serial;
        if (el.Obj is HeapElement h) return ReferenceEquals(_heap[idx], h);
        return false;
    }

    internal uint SourceSerial(int idx) => (uint)idx < (uint)_src.Length ? _src[idx] : 0;

    internal void Store(int idx, Element el)
    {
        if (idx >= _kind.Length)
        {
            int n = Math.Max(_kind.Length * 2, idx + 1);
            Array.Resize(ref _kind, n); Array.Resize(ref _src, n); Array.Resize(ref _heap, n); Array.Resize(ref _key, n);
            Array.Resize(ref _slot, n);
        }
        ushort k = el.ElementTypeId;
        ushort old = _kind[idx];
        if (old != 0 && old != k) { ClearSlot(idx, old); old = 0; }
        _kind[idx] = (byte)k;
        bool fresh = old == 0;   // the node has no slot of this kind yet
        _src[idx] = el.Obj is FrozenElement f ? f.Serial : el.Obj is ContextProviderEl cps ? cps.Serial : 0;
        _key[idx] = el.Key;
        switch (k)
        {
            // A tape element's record outlives its region: what it keeps is frozen (its cold groups, a Show's branches, a
            // SkeletonOverride); the element-tree members are dropped first (DropTree), so nothing under them is copied.
            case BoxEl.TypeId: { ref var v = ref StoreAs<BoxEl>(idx, k, el, fresh); v.DropTree(); if (el.Obj is null) Freeze(ref v); break; }
            case TextEl.TypeId: { ref var v = ref StoreAs<TextEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case ComponentEl.TypeId: { ref var v = ref StoreAs<ComponentEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case ScrollEl.TypeId: { ref var v = ref StoreAs<ScrollEl>(idx, k, el, fresh); v.DropTree(); if (el.Obj is null) Freeze(ref v); break; }
            case ShowEl.TypeId: { ref var v = ref StoreAs<ShowEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case ImageEl.TypeId: { ref var v = ref StoreAs<ImageEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case GridEl.TypeId: { ref var v = ref StoreAs<GridEl>(idx, k, el, fresh); v.DropTree(); if (el.Obj is null) Freeze(ref v); break; }
            case PolylineStrokeEl.TypeId: { ref var v = ref StoreAs<PolylineStrokeEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case SpanTextEl.TypeId: { ref var v = ref StoreAs<SpanTextEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case IconLayerEl.TypeId: { ref var v = ref StoreAs<IconLayerEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case PathEl.TypeId: { ref var v = ref StoreAs<PathEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case ListRowEl.TypeId: { ref var v = ref StoreAs<ListRowEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case SeriesEl.TypeId: { ref var v = ref StoreAs<SeriesEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case SpriteFieldEl.TypeId: { ref var v = ref StoreAs<SpriteFieldEl>(idx, k, el, fresh); if (el.Obj is null) Freeze(ref v); break; }
            case ContextProviderEl.TypeId:   // a copy without its subtree
            {
                var cp = ((ContextProviderEl)el.Obj!) with { Child = default };
                _heap[idx] = cp.SkeletonOverride is { } so ? cp with { SkeletonOverride = so.Freeze() } : cp;
                break;
            }
            default: _heap[idx] = ((HeapElement)el.Obj!).FreezeMembers(); break;   // a heap kind (boundaries): the object
        }
    }

    private static void Freeze<T>(ref T v) where T : struct, IElementKind<T> => T.FreezeMembers(ref v);

    /// <summary>Copy the element into the kind's column; returns the stored value (the caller clears its element-valued
    /// members: a record never points at an element tree).</summary>
    private ref T StoreAs<T>(int idx, ushort k, Element el, bool fresh) where T : struct
    {
        var col = (RecordColumn<T>)(_cols[k] ??= new RecordColumn<T>());
        int s = fresh ? (_slot[idx] = col.Alloc()) : _slot[idx];
        uint stamp = col.NextStamp();
        col.Items[s] = el.Ref<T>();
        col.Stamps[s] = stamp;
        return ref col.Items[s];
    }

    /// <summary>Store <paramref name="el"/> as the node's record and return its PREVIOUS record (a copy on the kind's old stack,
    /// valid until <see cref="PopOld"/>; a heap kind's previous object). The node's kind does not change across an Update.</summary>
    internal Element StoreKeepOld(int idx, Element el)
    {
        ushort k = _kind[idx];
        System.Diagnostics.Debug.Assert(k == el.ElementTypeId, "an Update never changes the node's element kind");
        Element old = _cols[k] is { } col ? col.PushOld(_slot[idx], k) : new Element((HeapElement)_heap[idx]!);
        Store(idx, el);
        return old;
    }

    internal void PopOld(ushort kind) => _cols[kind]?.PopOld();

    /// <summary>The node left the tree: drop its record (no reference outlives the node).</summary>
    internal void Clear(int idx)
    {
        if ((uint)idx >= (uint)_kind.Length) return;
        ushort k = _kind[idx];
        if (k != 0) ClearSlot(idx, k);
        _kind[idx] = 0;
        _src[idx] = 0;
        _key[idx] = null;
    }

    private void ClearSlot(int idx, ushort k)
    {
        if (_cols[k] is { } c) c.Free(_slot[idx]);
        else _heap[idx] = null;
    }
}
