using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluentGpu.Foundation;

/// <summary>
/// Stable, generation-versioned slab over <typeparamref name="T"/>. Index 0 is reserved as the null slot,
/// so <c>default(Handle)</c> is null. Generation is bumped on Free (ABA defense). O(1) alloc/free via an
/// intrusive int free-list. Pointer-bearing T uses a side <c>_next</c> array (we always do, here).
/// </summary>
public sealed class SlabAllocator<T> where T : unmanaged
{
    private T[] _items;
    private uint[] _gens;
    private int[] _next;     // free-list links; 0 = end
    private int _freeHead;   // 0 = empty
    private int _high = 1;   // next never-used index (0 reserved)

    public SlabAllocator(int capacity = 16)
    {
        if (capacity < 2) capacity = 2;
        _items = new T[capacity];
        _gens = new uint[capacity];
        _next = new int[capacity];
    }

    public int Capacity => _items.Length;
    public int LiveCount { get; private set; }

    public Handle Alloc()
    {
        int idx;
        if (_freeHead != 0) { idx = _freeHead; _freeHead = _next[idx]; }
        else { if (_high >= _items.Length) Grow(); idx = _high++; }

        if (_gens[idx] == 0) _gens[idx] = 1;   // fresh slot starts live at gen 1
        _items[idx] = default;
        LiveCount++;
        return new Handle((uint)idx, _gens[idx]);
    }

    public void Free(Handle h)
    {
        Debug.Assert(IsLive(h), "Free of a stale or null handle");
        int idx = (int)h.Index;
        _gens[idx]++;                 // invalidate every outstanding handle to this slot
        if (_gens[idx] == 0) _gens[idx] = 1; // skip 0 (the null gen) on wrap
        _next[idx] = _freeHead;
        _freeHead = idx;
        LiveCount--;
    }

    public bool IsLive(Handle h)
        => h.Index > 0 && h.Index < (uint)_high && _gens[h.Index] == h.Gen;

    /// <summary>Mutable view of the slot. Must NOT be held across an <see cref="Alloc"/> that can grow.</summary>
    public ref T Get(Handle h)
    {
        Debug.Assert(IsLive(h), "Get of a stale or null handle");
        return ref _items[h.Index];
    }

    private void Grow()
    {
        int n = _items.Length * 2;
        Array.Resize(ref _items, n);
        Array.Resize(ref _gens, n);
        Array.Resize(ref _next, n);
    }
}

/// <summary>Per-frame bump allocator. O(1) <see cref="Reset"/>. Spans are invalidated by Reset (and by a grow).</summary>
public sealed class ArenaAllocator
{
    private byte[] _buf;
    private int _off;

    public ArenaAllocator(int capacity = 64 * 1024) => _buf = GC.AllocateUninitializedArray<byte>(capacity, pinned: false);

    public Span<T> AllocSpan<T>(int count) where T : unmanaged
    {
        if (count <= 0) return Span<T>.Empty;
        int bytes = count * Unsafe.SizeOf<T>();
        int start = (_off + 15) & ~15;      // 16-byte align
        if (start + bytes > _buf.Length) Grow(start + bytes);
        _off = start + bytes;
        return MemoryMarshal.Cast<byte, T>(_buf.AsSpan(start, bytes));
    }

    public void Reset() => _off = 0;

    private void Grow(int need)
    {
        int n = _buf.Length * 2;
        while (n < need) n *= 2;
        var nb = GC.AllocateUninitializedArray<byte>(n, pinned: false);
        Array.Copy(_buf, nb, _off);
        _buf = nb;
    }
}

/// <summary>Edge-only recycle pool for managed objects (Component / RenderContext). Cap-bounded.</summary>
public sealed class ObjectPool<T> where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly int _cap;
    private readonly Stack<T> _stack = new();

    public ObjectPool(Func<T> factory, Action<T>? reset = null, int cap = 32)
    {
        _factory = factory;
        _reset = reset;
        _cap = cap;
    }

    public T Rent() => _stack.Count > 0 ? _stack.Pop() : _factory();

    public void Return(T item)
    {
        _reset?.Invoke(item);
        if (_stack.Count < _cap) _stack.Push(item);
    }
}
