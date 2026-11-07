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

    /// <summary>The reconciler opens regions (on by default since PERF r3 MODEL M4b phase P2; <c>--fg no-tape</c> is the A/B arm). Off: no
    /// region is ever opened and every element is frozen at push, the class-world lifetime.</summary>
    public static bool RegionsEnabled = true;

    /// <summary><c>--fg tape-probe=PATH</c>: regions never give their slots back; a closed region's slots are marked dead and a read of
    /// one is logged to PATH (first occurrence of each stack in full) and SERVED, so one run lists every escape (an element read
    /// after its render's reconcile returned) instead of failing at the first. Memory grows with every element built: a short tour
    /// only.</summary>
    public static string? ProbePath;
    private static StreamWriter? s_probeOut;
    private static readonly Dictionary<string, int> s_probeSeen = new();
    private static long s_probeReads;

    /// <summary>A region is open on this thread: pushes go to the tape.</summary>
    public static bool IsOpen
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_depth > 0;
    }

    internal static uint Stamp => t_stamp;

    /// <summary>Run <paramref name="build"/> with no region open: every element it builds is frozen (heap, valid forever). For a value
    /// that outlives any render and may be built inside one: a static field's initializer runs on the first read, which can be in
    /// a render (<see cref="Ui.Static{T}(Func{T})"/>).</summary>
    public static T Outside<T>(Func<T> build)
    {
        int saved = t_depth;
        t_depth = 0;
        try { return build(); }
        finally { t_depth = saved; }
    }

    /// <summary>Open a region; returns the token <see cref="End"/> takes.</summary>
    /// <summary><see cref="Begin()"/> naming the code that builds in the region (probe mode keeps it per stamp for the escape log).</summary>
    public static int Begin(string label)
    {
        int t = Begin();
        if (ProbePath is not null) s_probeLabels[t_stamp] = label;
        else if (FluentGpu.Foundation.Diag.CompiledIn) { int r = (int)(t_stamp & (LabelRing - 1)); s_ringStamp[r] = t_stamp; s_ringLabel[r] = label; }
        return t;
    }

    // DEBUG / FLUENTGPU_DIAG: the label of each recent region by stamp, so a stale read names the code that built the element.
    private const int LabelRing = 4096;
    private static readonly uint[] s_ringStamp = new uint[LabelRing];
    private static readonly string?[] s_ringLabel = new string?[LabelRing];

    private static string BuiltIn(uint stamp)
    {
        stamp &= 0x7FFF_FFFFu;
        if (s_probeLabels.TryGetValue(stamp, out var l)) return l;
        int r = (int)(stamp & (LabelRing - 1));
        return s_ringStamp[r] == stamp ? s_ringLabel[r] ?? "?" : "?";
    }

    private static long s_staleReads;
    /// <summary>Reads of an element after the render that built it ended (every mode; the inspector gate fails on any).</summary>
    public static long StaleReads => Interlocked.Read(ref s_staleReads);
    /// <summary>The first stale read's message (null while there was none).</summary>
    public static string? FirstStale { get; private set; }

    private static readonly Dictionary<uint, string> s_probeLabels = new();

    /// <summary>Probe mode: what the reconciler is running for app code right now (a skeleton region's content thunk), named in
    /// the escape log.</summary>
    public static string? ProbeNote;

    /// <summary>Probe mode: the app method behind a delegate (through one level of closure: Skel.Region's own lambdas wrap the
    /// app's), for <see cref="ProbeNote"/>.</summary>
    public static string DescribeDelegate(Delegate? d)
    {
        if (d is null) return "null";
        var target = d.Target;
        if (target is not null)
            foreach (var f in target.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (f.GetValue(target) is Delegate inner && inner.Method.DeclaringType?.Namespace?.StartsWith("FluentGpu.Hooks", StringComparison.Ordinal) != true)
                    return inner.Method.DeclaringType?.FullName + "." + inner.Method.Name;
        return d.Method.DeclaringType?.FullName + "." + d.Method.Name;
    }

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
        if (ProbePath is null) for (int i = 0; i < cols.Count; i++) cols[i].TruncateTo(i < n ? marks[start + i] : 0);
        else for (int i = 0; i < cols.Count; i++) cols[i].MarkDead(i < n ? marks[start + i] : 0);
        t_markTop = start;
        t_depth--;
        t_stamp = t_stamps![t_depth];
    }

    internal static void Register(TapeColumn c) => (t_columns ??= new List<TapeColumn>()).Add(c);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void StaleRecord(int slot, uint stamp, string column)
    {
        string msg = $"A {column} node record was read through a handle taken before the node was reconciled again (node slot {slot}, stamp {stamp}).";
        Interlocked.Increment(ref s_staleReads);
        FirstStale ??= msg;
        throw new StaleElementException(msg);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Stale(int slot, uint stamp, string column)
    {
        Interlocked.Increment(ref s_staleReads);
        if (ProbePath is not null) { FirstStale ??= $"{column} built in [{BuiltIn(stamp)}]"; ProbeStale(slot, stamp, column); return; }
        // Never silent, never recycled bytes: the read fails here in every build (Release reaches the app's crash path; the
        // reconciler's fault sinks rethrow this type), and the inspector's gate fails on the count.
        string msg = $"A {column} built during a render of [{BuiltIn(stamp)}] was read after that render's reconcile returned " +
            $"(slot {slot}, stamp {stamp}, read in [{(t_depth > 0 ? BuiltIn(t_stamp) : "no region")}]). An element that outlives its render " +
            "must be frozen (el.Freeze()), built where it is used, or be a static built with Ui.Static (DESIGN 2.2, escapes).";
        FirstStale ??= msg;
        throw new StaleElementException(msg);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeStale(int slot, uint stamp, string column)
    {
        s_probeReads++;
        // The signature: the element kind, the code that built it (the region's label), and the read path with the tape/record
        // plumbing dropped and the reconciler's recursion collapsed, so one escape is one line whatever the tree depth.
        var st = new System.Diagnostics.StackTrace(2, false);
        var sb = new System.Text.StringBuilder();
        string last = "";
        int kept = 0;
        for (int f = 0; f < st.FrameCount && kept < 16; f++)
        {
            var m = st.GetFrame(f)?.GetMethod();
            string ty = m?.DeclaringType?.Name ?? "?";
            if (ty.StartsWith("TapeColumn", StringComparison.Ordinal) || ty is "Element" or "ElementList" or "NodeRecords" or "ElementCompat"
                || ty.StartsWith("ColdRef", StringComparison.Ordinal) || ty.EndsWith("Diff", StringComparison.Ordinal)) continue;
            string name = ty + "." + m?.Name;
            if (ty == "TreeReconciler" && m?.Name is "Update" or "UpdateCore" or "ReconcileChildren" or "ReconcileChildrenCore"
                or "Mount" or "MountCore" or "ReconcileSingleChild" or "ReconcileWindow" or "ReconcileWindowCore") name = "TreeReconciler.(diff)";
            if (name == last) continue;
            sb.Append(name).Append(" < ");
            last = name; kept++;
        }
        string built = s_probeLabels.TryGetValue(stamp & 0x7FFF_FFFFu, out var lbl) ? lbl : "?";
        string reading = t_depth > 0 && s_probeLabels.TryGetValue(t_stamp, out var rl) ? rl : "(no region)";
        string sig = column + " built in [" + built + "] read in [" + reading + "]" + (ProbeNote is { } note ? " via [" + note + "]" : "") + " | " + sb;
        if (s_probeOut is null)
        {
            string path = ProbePath!.Replace("{pid}", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            s_probeOut = new StreamWriter(path, append: true) { AutoFlush = true };
            s_probeOut.WriteLine($"[tape-probe] pid {Environment.ProcessId}");
        }
        // what the dead slot holds (its key, its text), per occurrence: names the escaping element in the app's own words
        string what = "";
        if (t_columns is { } cols)
            foreach (var c in cols)
                if (c.ItemTypeName == column) { what = " " + c.DescribeSlot(slot); break; }
        if (s_probeSeen.TryGetValue(sig, out int id)) { s_probeSeen[sig] = id; s_probeOut.WriteLine($"#{id}{what}"); return; }
        id = s_probeSeen.Count + 1;
        s_probeSeen[sig] = id;
        s_probeOut.WriteLine($"=={id} {sig}");
        s_probeOut.WriteLine($"#{id}{what}");
    }
}

/// <summary>One typed column of the tape.</summary>
internal abstract class TapeColumn
{
    internal int Top;
    internal abstract string ItemTypeName { get; }
    /// <summary>Probe mode: the key and text of the value in <paramref name="slot"/> (reflection; never on a hot path).</summary>
    internal abstract string DescribeSlot(int slot);
    internal abstract void TruncateTo(int top);
    /// <summary>Probe mode: the slots from <paramref name="top"/> up are dead (their stamps fail every check) but keep their values.</summary>
    internal abstract void MarkDead(int top);
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

    internal override string ItemTypeName => typeof(T).Name;

    internal override string DescribeSlot(int slot)
    {
        if ((uint)slot >= (uint)Items.Length) return "(slot out of range)";
        object? v = Items[slot];
        if (v is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var name in new[] { "Key", "Text", "Spans" })
        {
            var pi = v.GetType().GetProperty(name);
            if (pi is null) continue;
            object? x;
            try { x = pi.GetValue(v); } catch { continue; }
            if (x is null) continue;
            string str = x.ToString() ?? "";
            if (str.Length > 60) str = str[..60];
            sb.Append(name).Append('=').Append(str).Append(' ');
        }
        return sb.ToString().TrimEnd();
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

    internal override void MarkDead(int top)
    {
        for (int i = top; i < Top; i++) Stamps[i] |= 0x8000_0000u;
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
    internal static uint NextSerial() { uint s = (uint)Interlocked.Increment(ref s_serial); return s == 0 ? (uint)Interlocked.Increment(ref s_serial) : s; }
}

/// <summary>An object's identity serial inside a record: excluded from the record's value equality (always equal, hash 0).</summary>
internal readonly struct SerialTag : IEquatable<SerialTag>
{
    internal readonly uint Value;
    internal SerialTag(uint value) => Value = value;
    public bool Equals(SerialTag other) => true;
    public override bool Equals(object? obj) => obj is SerialTag;
    public override int GetHashCode() => 0;
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
    internal uint[] Stamps = new uint[16];
    private uint _next;
    private int[] _free = new int[16];
    private int _freeTop, _count;
    internal uint NextStamp() { uint s = ++_next; return s == 0 ? ++_next : s; }
    internal abstract void Clear(int slot);
    internal abstract void Ensure(int slot);

    /// <summary>A slab slot for a node's record of this kind (the column holds only the nodes of its kind, not every node index).</summary>
    internal int Alloc()
    {
        if (_freeTop != 0) return _free[--_freeTop];
        int s = _count++;
        Ensure(s);
        return s;
    }

    internal void Free(int slot)
    {
        Clear(slot);
        if (_freeTop == _free.Length) Array.Resize(ref _free, _free.Length * 2);
        _free[_freeTop++] = slot;
    }
    /// <summary>Copy the record at <paramref name="slot"/> onto this column's OLD stack and return a handle to the copy (valid until
    /// <see cref="PopOld"/>): the value an Update diffs against after the node's record already holds the new element.</summary>
    internal abstract Element PushOld(int slot, ushort kind);
    internal abstract void PopOld();
}

internal sealed class RecordColumn<T> : RecordColumn
{
    internal T[] Items = new T[16];

    internal override void Ensure(int slot)
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

/// <summary>An element (or a node record) read after the render that built it ended (PERF r3 MODEL M4b): a bug in the code that
/// let it escape. Never swallowed by the reconciler's or the host's fault sinks.</summary>
public sealed class StaleElementException : InvalidOperationException
{
    public StaleElementException(string message) : base(message) { }
}
