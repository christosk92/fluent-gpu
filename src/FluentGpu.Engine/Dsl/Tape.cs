using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluentGpu.Dsl;

// The per-render TAPE (PERF r3 MODEL M4b, DESIGN 2.2). Elements are values; pushing one (BoxEl → Element) copies it into a
// typed column of the current thread's tape and returns a 16-byte handle. The reconciler opens a REGION around every call into
// app element-building code and closes it when the reconcile of that output returns; regions nest and close LIFO, so the tape
// is a stack and sibling renders reuse the same slots. A handle is valid while its slot is below the column's top AND carries
// the stamp of the region that wrote it (every region gets a fresh stamp): a handle that outlived its region fails that test
// (a loud failure in DEBUG, never a silent wrong element). Outside any region (static initializers, event handlers, effects,
// services) a push FREEZES instead: the value goes to the heap (FrozenElement<T>) and the handle is valid forever, exactly as a
// class element was.

/// <summary>The tape's regions (UI-thread state; a thread that never opens a region freezes every element it builds).</summary>
public static class Tape
{
    [ThreadStatic] private static int t_depth;
    [ThreadStatic] private static uint t_stamp;
    [ThreadStatic] private static List<TapeColumn>? t_columns;
    [ThreadStatic] private static int[]? t_marks;      // per open region: the column tops at its start, then its column count
    [ThreadStatic] private static int t_markTop;
    [ThreadStatic] private static uint[]? t_stamps;    // per open region: the stamp of the region it interrupted
    private static int s_nextStamp;

    /// <summary>A region is open on this thread: pushes go to the tape.</summary>
    public static bool IsOpen
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_depth > 0;
    }

    internal static uint Stamp => t_stamp;

    /// <summary>Open a region; returns the token <see cref="End"/> takes.</summary>
    public static int Begin()
    {
        var cols = t_columns ??= new List<TapeColumn>();
        int n = cols.Count;
        var marks = t_marks ??= new int[256];
        if (t_markTop + n + 1 > marks.Length) Array.Resize(ref t_marks, Math.Max(marks.Length * 2, t_markTop + n + 1));
        marks = t_marks!;
        for (int i = 0; i < n; i++) marks[t_markTop + i] = cols[i].Top;
        marks[t_markTop + n] = n;
        t_markTop += n + 1;
        var stamps = t_stamps ??= new uint[64];
        if (t_depth == stamps.Length) Array.Resize(ref t_stamps, stamps.Length * 2);
        t_stamps![t_depth] = t_stamp;
        t_depth++;
        uint s = (uint)Interlocked.Increment(ref s_nextStamp);
        t_stamp = s == 0 ? (uint)Interlocked.Increment(ref s_nextStamp) : s;   // 0 is "no stamp"
        return t_depth;
    }

    /// <summary>Close the innermost region (LIFO: <paramref name="token"/> must be the latest <see cref="Begin"/>'s): every slot it
    /// wrote is cleared (no stale reference keeps an app object alive) and reused by the next region.</summary>
    public static void End(int token)
    {
        if (token != t_depth) throw new InvalidOperationException($"Tape regions must close LIFO (closing {token}, innermost {t_depth}).");
        var cols = t_columns!;
        var marks = t_marks!;
        int n = marks[t_markTop - 1];
        int start = t_markTop - 1 - n;
        for (int i = 0; i < cols.Count; i++) cols[i].TruncateTo(i < n ? marks[start + i] : 0);
        t_markTop = start;
        t_depth--;
        t_stamp = t_stamps![t_depth];
    }

    internal static void Register(TapeColumn c) => (t_columns ??= new List<TapeColumn>()).Add(c);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void StaleRecord(int slot, uint stamp, string column) =>
        throw new InvalidOperationException(
            $"A {column} node record was read through a handle taken before the node was reconciled again (node slot {slot}, stamp {stamp}).");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Stale(int slot, uint stamp, string column) =>
        throw new InvalidOperationException(
            $"A {column} built during a render was used after that render's reconcile returned (slot {slot}, stamp {stamp}). " +
            "An element that must outlive its render has to be frozen (el.Freeze()) or built where it is used (DESIGN 2.2, escapes).");
}

/// <summary>One typed column of the tape.</summary>
internal abstract class TapeColumn
{
    internal int Top;
    internal abstract void TruncateTo(int top);
}

/// <summary>The tape column of <typeparamref name="T"/> (an element kind, a cold group, or <see cref="Element"/> for lists).</summary>
internal sealed class TapeColumn<T> : TapeColumn
{
    [ThreadStatic] private static TapeColumn<T>? t_current;
    internal T[] Items = new T[64];
    internal uint[] Stamps = new uint[64];

    internal static TapeColumn<T> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current ?? Create();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TapeColumn<T> Create()
    {
        var c = new TapeColumn<T>();
        t_current = c;
        Tape.Register(c);
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Push(in T value)
    {
        int slot = Top;
        if (slot == Items.Length) Grow();
        Items[slot] = value;
        Stamps[slot] = Tape.Stamp;
        Top = slot + 1;
        return slot;
    }

    private void Grow()
    {
        Array.Resize(ref Items, Items.Length * 2);
        Array.Resize(ref Stamps, Stamps.Length * 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T At(int slot, uint stamp)
    {
        if ((uint)slot >= (uint)Top || Stamps[slot] != stamp) Tape.Stale(slot, stamp, typeof(T).Name);
        return ref Items[slot];
    }

    /// <summary>A contiguous range written by one region (an <see cref="ElementList"/>).</summary>
    internal ReadOnlySpan<T> Range(int start, int count, uint stamp)
    {
        if (count == 0) return default;
        if ((uint)(start + count) > (uint)Top || Stamps[start] != stamp || Stamps[start + count - 1] != stamp)
            Tape.Stale(start, stamp, typeof(T).Name);
        return new ReadOnlySpan<T>(Items, start, count);
    }

    internal override void TruncateTo(int top)
    {
        if (top >= Top) return;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Items, top, Top - top);
        Array.Clear(Stamps, top, Top - top);
        Top = top;
    }
}

/// <summary>A frozen element: the heap copy of the value (its cold groups and element-valued
/// members frozen too). Valid forever, like a class element.</summary>
internal abstract class FrozenElement
{
    private static int s_serial;
    /// <summary>This object's identity as a number (never 0): a node's record keeps the serial of the frozen element it was
    /// reconciled from, so the reconciler's identity skip (the former ReferenceEquals(newEl, oldEl)) needs no reference.</summary>
    internal readonly uint Serial = NextSerial();
    internal abstract ushort Kind { get; }
    private static uint NextSerial() { uint s = (uint)Interlocked.Increment(ref s_serial); return s == 0 ? (uint)Interlocked.Increment(ref s_serial) : s; }
}

internal sealed class FrozenElement<T> : FrozenElement
{
    internal T Value;
    private readonly ushort _kind;
    internal FrozenElement(in T value, ushort kind) { Value = value; _kind = kind; }
    internal override ushort Kind => _kind;
}

/// <summary>A column of node RECORDS of one value kind (PERF r3 MODEL M4b, DESIGN 2.3), owned by a reconciler and indexed by node
/// slot. A record handle (<c>Element{Obj = column, slot = node index, stamp}</c>) reads the value the node was last reconciled
/// against; every store gives the slot a new stamp, so a handle taken before a store fails its check instead of silently reading
/// the newer value.</summary>
internal abstract class RecordColumn
{
    internal uint[] Stamps = new uint[256];
    private uint _next;
    internal uint NextStamp() { uint s = ++_next; return s == 0 ? ++_next : s; }
    internal abstract void Clear(int slot);
    /// <summary>Copy the record at <paramref name="slot"/> onto this column's OLD stack and return a handle to the copy (valid until
    /// <see cref="PopOld"/>): the value an Update diffs against after the node's record already holds the new element.</summary>
    internal abstract Element PushOld(int slot, ushort kind);
    internal abstract void PopOld();
}

internal sealed class RecordColumn<T> : RecordColumn
{
    internal T[] Items = new T[256];

    internal void Ensure(int slot)
    {
        if (slot < Items.Length) return;
        int n = Math.Max(Items.Length * 2, slot + 1);
        Array.Resize(ref Items, n);
        Array.Resize(ref Stamps, n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T At(int slot, uint stamp)
    {
        if ((uint)slot >= (uint)Items.Length || Stamps[slot] != stamp) Tape.StaleRecord(slot, stamp, typeof(T).Name);
        return ref Items[slot];
    }

    internal override void Clear(int slot)
    {
        if (slot >= Items.Length) return;
        Items[slot] = default!;
        Stamps[slot] = 0;
    }

    private RecordColumn<T>? _old;
    private int _oldTop;

    internal override Element PushOld(int slot, ushort kind)
    {
        var s = _old ??= new RecordColumn<T>();
        int at = _oldTop++;
        s.Ensure(at);
        s.Items[at] = Items[slot];
        uint stamp = s.NextStamp();
        s.Stamps[at] = stamp;
        return new Element(s, kind, at, stamp);
    }

    internal override void PopOld()
    {
        int at = --_oldTop;
        _old!.Items[at] = default!;
        _old.Stamps[at] = 0;
    }
}

/// <summary>A rarely-set group of an element's channels (a "cold group"), held by reference from the element value.</summary>
internal interface IColdGroup<TSelf> : IEquatable<TSelf> where TSelf : struct, IColdGroup<TSelf>
{
    static abstract TSelf CreateDefault();
    /// <summary>Freeze the element-valued members of a group being frozen (most groups have none).</summary>
    static virtual void FreezeMembers(ref TSelf value) { }
}

internal static class ColdDefault<T> where T : struct, IColdGroup<T>
{
    internal static readonly T Value = T.CreateDefault();
}

internal sealed class ColdBox<T> where T : struct, IColdGroup<T>
{
    internal T Value;
    internal ColdBox(in T value) => Value = value;
}

/// <summary>A reference from an element value to one of its cold groups: none (the defaults), a tape slot, or a heap box (built
/// outside a region, or frozen). Setting a cold channel ALWAYS copies the group to a new slot: a struct cannot know whether a copy
/// of it shares the slot, so `x with { ColdChannel = v }` must never write in place.</summary>
internal readonly struct ColdRef<T> where T : struct, IColdGroup<T>
{
    private readonly ColdBox<T>? _box;
    private readonly int _slot;      // tape slot + 1; 0 = none
    private readonly uint _stamp;

    private ColdRef(ColdBox<T>? box, int slot, uint stamp) { _box = box; _slot = slot; _stamp = stamp; }

    internal ref readonly T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_box is { } b) return ref b.Value;
            if (_slot != 0) return ref TapeColumn<T>.Current.At(_slot - 1, _stamp);
            return ref ColdDefault<T>.Value;
        }
    }

    internal bool IsDefault => _box is null && _slot == 0;

    internal static ColdRef<T> Of(in T value)
        => Tape.IsOpen ? new(null, TapeColumn<T>.Current.Push(in value) + 1, Tape.Stamp) : new(Box(in value), 0, 0);

    private static ColdBox<T> Box(in T value)
    {
        var v = value;
        T.FreezeMembers(ref v);
        return new ColdBox<T>(in v);
    }

    /// <summary>This reference with its group on the heap (Freeze).</summary>
    internal ColdRef<T> Frozen() => _box is not null || _slot == 0 ? this : new(Box(in Value), 0, 0);

    /// <summary>Same group (the reference, not the values): the reconciler's quick no-change test before comparing values.</summary>
    internal bool SameAs(in ColdRef<T> o) => ReferenceEquals(_box, o._box) && _slot == o._slot && _stamp == o._stamp;

    /// <summary>Same values (the element's value equality; the record compared its cold group by value).</summary>
    internal bool ValueEquals(in ColdRef<T> o)
    {
        if (SameAs(in o)) return true;
        T a = Value;
        return a.Equals(o.Value);
    }
}
