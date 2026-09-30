using System.Threading;

namespace FluentGpu.Render.Evidence;

/// <summary>
/// A fixed-capacity single-producer ring of unmanaged records (docs/plans/evidence-diagnostics-implementation.md §A): the
/// storage every evidence ledger shares. The producer (one thread — the recorder pair's owner) writes a slot and then
/// publishes the monotonic write count with a release store; a reader on any thread drains by count with
/// <see cref="Read"/> (the <c>ScrollProbe.ReadRender</c> shape): it copies the records written at or after its cursor and
/// drops any record the producer may have overwritten while it copied, so a drained row is never torn.
/// <para>Sized once at construction; <see cref="Add"/> and <see cref="Read"/> allocate nothing.</para>
/// </summary>
public class DiagRing<T> where T : unmanaged
{
    private readonly T[] _ring;
    private readonly int _mask;
    private long _count;

    /// <param name="capacity">Records held; a power of two.</param>
    public DiagRing(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "a power of two");
        _ring = new T[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Records the ring holds before the oldest is overwritten.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Records ever written (monotonic). A reader that wants only records from "now on" starts its cursor here.</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>PRODUCER ONLY. Append one record; returns its write index (the value <see cref="Count"/> had before).</summary>
    public long Add(in T record)
    {
        long n = _count;   // single producer: a plain read of its own counter
        _ring[(int)(n & _mask)] = record;
        Volatile.Write(ref _count, n + 1);
        return n;
    }

    /// <summary>PRODUCER ONLY. The slot written at <paramref name="index"/> while it is still retained (a late field the
    /// producer fills after <see cref="Add"/> — e.g. a walk's byte count known only when the walk closes). False once the
    /// slot has been overwritten.</summary>
    internal bool TryAt(long index, out int slot)
    {
        long n = _count;
        slot = (int)(index & _mask);
        return index >= 0 && index < n && n - index <= _ring.Length;
    }

    /// <summary>PRODUCER ONLY. The retained slot <see cref="TryAt"/> returned.</summary>
    internal ref T Slot(int slot) => ref _ring[slot];

    /// <summary>Incremental drain: copies up to <c>dst.Length</c> records written at or after <paramref name="from"/> into
    /// <paramref name="dst"/>, returns how many; <paramref name="next"/> is the cursor to pass next time. A reader that fell
    /// more than <see cref="Capacity"/> behind resumes at the oldest retained record; a record the producer may have been
    /// overwriting during the copy is dropped rather than returned torn. Any thread; zero allocation.</summary>
    public int Read(long from, Span<T> dst, out long next)
    {
        long end = Volatile.Read(ref _count);
        long start = from;
        if (start < end - _ring.Length) start = end - _ring.Length;
        if (start < 0) start = 0;
        if (start > end) start = end;
        int n = (int)Math.Min(end - start, (long)dst.Length);
        for (int i = 0; i < n; i++) dst[i] = _ring[(int)((start + i) & _mask)];

        long end2 = Volatile.Read(ref _count);
        long firstSafe = end2 - _ring.Length + 1;
        if (start < firstSafe && n > 0)
        {
            int drop = (int)Math.Min(firstSafe - start, (long)n);
            if (drop < n) dst.Slice(drop, n - drop).CopyTo(dst);
            n -= drop;
            start += drop;
        }
        next = start + n;
        return n;
    }

    /// <summary>The most recent <c>dst.Length</c> records (or fewer), oldest first — the export tail.</summary>
    public int ReadTail(Span<T> dst) => Read(Count - dst.Length, dst, out _);
}
