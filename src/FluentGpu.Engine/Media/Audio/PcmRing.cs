using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>
/// A lock-free single-producer / single-consumer ring of interleaved <c>f32</c> PCM (spec §7.9). The WORKER pool decodes
/// ahead and writes (<see cref="Write"/>); the RT feed thread drains it (<see cref="Read"/>) — copy ONLY, never a decode.
/// Monotonic <see cref="long"/> head/tail with <see cref="Volatile"/> publish/acquire fences make it wait-free and
/// zero-alloc; a power-of-two capacity turns the modulo into a mask. Capacity is in <b>floats</b> (frames × channels).
/// This is the decode↔mix firewall: the RT thread never blocks and, when the producer falls behind, <see cref="Read"/>
/// returns a SHORT read (silence upstream) instead of stalling — the caller bumps the xrun counter.
/// </summary>
public sealed class PcmRing
{
    private readonly float[] _buf;
    private readonly int _mask;
    private long _head;   // total floats consumed (RT thread owns the write to this)
    private long _tail;   // total floats produced (worker owns the write to this)

    /// <summary>Create a ring holding at least <paramref name="minFloats"/> interleaved floats (rounded up to a power of two).</summary>
    public PcmRing(int minFloats)
    {
        int cap = 1;
        int want = Math.Max(2, minFloats);
        while (cap < want) cap <<= 1;
        _buf = new float[cap];
        _mask = cap - 1;
    }

    /// <summary>The ring capacity in floats.</summary>
    public int CapacityFloats => _buf.Length;

    /// <summary>Floats currently available to read (producer-published, consumer-unread). Safe from either thread.</summary>
    public int AvailableFloats
    {
        get
        {
            long t = Volatile.Read(ref _tail);
            long h = Volatile.Read(ref _head);
            return (int)(t - h);
        }
    }

    /// <summary>Free floats the producer may still write. Safe from either thread.</summary>
    public int FreeFloats => _buf.Length - AvailableFloats;

    /// <summary>PRODUCER (worker): copy up to <paramref name="src"/>.Length floats into the ring; returns the count actually
    /// written (a short write when the ring is nearly full). Wait-free, alloc-free — the single-producer invariant means no
    /// CAS is needed, only a release fence on <see cref="_tail"/> after the copy.</summary>
    public int Write(ReadOnlySpan<float> src)
    {
        long tail = _tail;                       // only this thread writes _tail — a plain read is fine
        long head = Volatile.Read(ref _head);    // acquire the consumer's progress
        int free = _buf.Length - (int)(tail - head);
        int n = Math.Min(src.Length, free);
        if (n <= 0) return 0;

        int start = (int)(tail & _mask);
        int first = Math.Min(n, _buf.Length - start);
        src[..first].CopyTo(_buf.AsSpan(start));
        if (first < n) src[first..n].CopyTo(_buf.AsSpan(0));

        Volatile.Write(ref _tail, tail + n);     // publish the data
        return n;
    }

    /// <summary>CONSUMER (RT feed thread): copy up to <paramref name="dst"/>.Length floats out of the ring; returns the count
    /// actually read (a SHORT read on underrun — the RT thread never blocks). Wait-free, alloc-free, no lock, no syscall.</summary>
    public int Read(Span<float> dst)
    {
        long head = _head;                       // only this thread writes _head
        long tail = Volatile.Read(ref _tail);    // acquire the producer's published data
        int avail = (int)(tail - head);
        int n = Math.Min(dst.Length, avail);
        if (n <= 0) return 0;

        int start = (int)(head & _mask);
        int first = Math.Min(n, _buf.Length - start);
        _buf.AsSpan(start, first).CopyTo(dst);
        if (first < n) _buf.AsSpan(0, n - first).CopyTo(dst[first..]);

        Volatile.Write(ref _head, head + n);     // release the slots back to the producer
        return n;
    }

    /// <summary>Discard all buffered data (control/rebuild path only — never called concurrently with a live producer/consumer).</summary>
    public void Clear()
    {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
}
