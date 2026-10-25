using System.Runtime.CompilerServices;

namespace FluentGpu.Hooks;

/// <summary>One hook cell's slot in the shared <see cref="HookTable"/> (PERF r3 MODEL M2, DESIGN 2.4): the call-site key, the
/// render that last resolved it (how a hook in a loop finds its ordinal), what its unmount does, and its payload. The payload is
/// the hook's own object (the <c>Signal</c>/<c>Memo</c>/<c>Ref</c>/effect the hook hands out, or the cell object of a rarer
/// hook); <see cref="Aux"/> holds a second object (UseState's cached setter).</summary>
internal struct HookSlot
{
    public ulong FileHash;
    public int Line, Ordinal;
    public int Epoch;
    public HookSlotKind Kind;
    public object? Obj;
    public object? Aux;
}

/// <summary>What a slot's unmount runs (<see cref="RenderContext.RunAllCleanups"/>), in registration order.</summary>
internal enum HookSlotKind : byte
{
    /// <summary>Nothing (a signal, a ref, a context read signal, a plain cell).</summary>
    Plain,
    /// <summary><see cref="Obj"/> is a <see cref="FluentGpu.Signals.Computation"/> the component owns: dispose it.</summary>
    Computation,
    /// <summary><see cref="Obj"/> is an <see cref="EffectCell"/>: run its cleanup.</summary>
    DepsEffect,
    /// <summary><see cref="Obj"/> is an <see cref="IDisposableCell"/>: dispose it.</summary>
    DisposableCell,
}

/// <summary>
/// Every mounted component's hook cells in ONE slab (PERF r3 MODEL M2, DESIGN 2.4), instead of a <c>List</c> of cell objects
/// plus a <c>Dictionary</c> keyed by call site per component. A component owns a contiguous run of slots; runs come in size
/// classes (4, 8, 16, ... slots), are recycled through one free list per class, and move to the next class (order kept) when
/// the component outgrows them. So a mounted page adds slots to a long-lived array, not objects to the heap, and an unmount
/// returns its run for the next mount. UI-thread confined, like the hooks themselves.
/// </summary>
internal static class HookTable
{
    internal static HookSlot[] Slots = new HookSlot[1024];
    private static int s_top;
    private static readonly Stack<int>[] s_free = [new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new()];

    /// <summary>The capacity of size class <paramref name="cls"/>: 4 &lt;&lt; cls slots.</summary>
    internal static int Capacity(int cls) => 4 << cls;

    /// <summary>Take a run of size class <paramref name="cls"/>; returns its first slot. Its slots are cleared.</summary>
    internal static int Rent(int cls)
    {
        if (cls < s_free.Length && s_free[cls].TryPop(out int start)) return start;
        int cap = Capacity(cls);
        if (s_top + cap > Slots.Length)
        {
            int n = Slots.Length;
            while (n < s_top + cap) n *= 2;
            Array.Resize(ref Slots, n);
        }
        start = s_top;
        s_top += cap;
        return start;
    }

    /// <summary>Return a run (cleared here, so no slot keeps an app object alive).</summary>
    internal static void Return(int start, int cls, int used)
    {
        Slots.AsSpan(start, used).Clear();
        if (cls < s_free.Length) s_free[cls].Push(start);
        // a run above the largest class is not recycled: it stays in the slab, cleared (a component with > 8192 hooks)
    }

    /// <summary>Move a full run of class <paramref name="cls"/> to the next class, keeping the order; returns the new start.</summary>
    internal static int Grow(int start, int cls, int used)
    {
        int next = Rent(cls + 1);
        Slots.AsSpan(start, used).CopyTo(Slots.AsSpan(next, used));
        Return(start, cls, used);
        return next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref HookSlot At(int i) => ref Slots[i];
}
