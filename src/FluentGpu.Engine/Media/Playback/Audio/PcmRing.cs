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
/// <para><b>Keep-behind (D8, seek design B).</b> A ring built with <c>keepBehindFloats &gt; 0</c> never lets the producer
/// overwrite the span of already-consumed audio right behind the consumer's head, so the RT thread can rewind into it
/// (<see cref="TryRewindConsumerSide"/>) or jump forward inside the published data (<see cref="TrySkipConsumerSide"/>) for an
/// instant, decoder-free seek. How much of that span is genuinely intact is tracked exactly (<see cref="BehindFloats"/>): it is
/// never overstated after a flush, a clear, or a consumer head that has not yet been that far.</para>
/// </summary>
public sealed class PcmRing
{
    private readonly float[] _buf;
    private readonly int _mask;
    private readonly int _keepBehind;   // floats the producer leaves intact behind the consumer's head
    private long _head;                 // total floats consumed (RT thread owns the write to this)
    private long _tail;                 // total floats produced (worker owns the write to this)
    private long _maxHead;              // consumer-owned: the furthest head ever reached since the last flush/clear
    private long _floor;                // consumer-owned: nothing below this is intact (set to the tail on a flush, 0 on Clear)

    /// <summary>Create a ring holding at least <paramref name="minFloats"/> interleaved floats (rounded up to a power of two),
    /// protecting <paramref name="keepBehindFloats"/> floats behind the consumer head (clamped to half the capacity so the
    /// producer always keeps room to write).</summary>
    public PcmRing(int minFloats, int keepBehindFloats = 0)
    {
        int cap = 1;
        int want = Math.Max(2, minFloats);
        while (cap < want) cap <<= 1;
        _buf = new float[cap];
        _mask = cap - 1;
        _keepBehind = Math.Clamp(keepBehindFloats, 0, _buf.Length / 2);
    }

    /// <summary>The ring capacity in floats.</summary>
    public int CapacityFloats => _buf.Length;

    /// <summary>The floats the producer leaves intact behind the consumer head (the effective value: the constructor argument
    /// clamped to half the capacity).</summary>
    public int KeepBehindFloats => _keepBehind;

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

    /// <summary>Free floats the producer may still write: capacity − unread − the protected span behind the head (negative
    /// transiently after a rewind grew the unread span; <see cref="Write"/> then accepts nothing). Safe from either thread.</summary>
    public int FreeFloats => _buf.Length - AvailableFloats - _keepBehind;

    /// <summary>Floats behind the head that are still intact: the producer may have overwritten anything older than
    /// <c>keepBehind</c> behind the FURTHEST head ever reached, and nothing before the last flush/clear exists at all. Never
    /// negative. Exact for the consumer thread; from any other thread an advisory snapshot (the RT re-validates in
    /// <see cref="TryRewindConsumerSide"/>).</summary>
    public int BehindFloats
    {
        get
        {
            long h = Volatile.Read(ref _head);              // acquire first: the consumer stores _maxHead/_floor BEFORE it publishes the head
            long oldest = Math.Max(Volatile.Read(ref _floor), Volatile.Read(ref _maxHead) - _keepBehind);
            return (int)Math.Max(0L, h - oldest);
        }
    }

    /// <summary>PRODUCER (worker): copy up to <paramref name="src"/>.Length floats into the ring; returns the count actually
    /// written (a short write when the ring is nearly full). Wait-free, alloc-free — the single-producer invariant means no
    /// CAS is needed, only a release fence on <see cref="_tail"/> after the copy. The kept-behind span is never overwritten.</summary>
    public int Write(ReadOnlySpan<float> src)
    {
        long tail = _tail;                       // only this thread writes _tail — a plain read is fine
        long head = Volatile.Read(ref _head);    // acquire the consumer's progress
        int free = _buf.Length - (int)(tail - head) - _keepBehind;   // the behind span is never overwritten
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

        long next = head + n;
        if (next > _maxHead) _maxHead = next;    // BEFORE the head is published (see BehindFloats)
        Volatile.Write(ref _head, next);         // release the slots back to the producer
        return n;
    }

    /// <summary>Discard all buffered data (control/rebuild path only — never called concurrently with a live producer/consumer).</summary>
    public void Clear()
    {
        _maxHead = 0;
        _floor = 0;
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }

    /// <summary>CONSUMER (RT): discard everything currently buffered — a head jump to the published tail. Legal only from
    /// the consumer thread (it owns <see cref="_head"/>); wait-free, SPSC-safe. Used by the seek flush (spec §7.9). The audio
    /// behind the old head is pre-seek content, so nothing behind the new head counts as kept (<see cref="BehindFloats"/> = 0).</summary>
    public void DiscardAllConsumerSide()
    {
        long t = Volatile.Read(ref _tail);
        _maxHead = t;                            // BEFORE the head is published (see BehindFloats)
        _floor = t;
        Volatile.Write(ref _head, t);
    }

    /// <summary>CONSUMER (RT): jump the head FORWARD by <paramref name="floats"/> within the already-published data. Returns
    /// false (nothing moved) when <paramref name="floats"/> is negative or reaches past the tail. Wait-free, alloc-free, SPSC-legal
    /// (the consumer owns the head).</summary>
    public bool TrySkipConsumerSide(int floats)
    {
        long head = _head;
        if (floats < 0 || floats > (int)(Volatile.Read(ref _tail) - head)) return false;
        long next = head + floats;
        if (next > _maxHead) _maxHead = next;    // BEFORE the head is published (see BehindFloats)
        Volatile.Write(ref _head, next);
        return true;
    }

    /// <summary>CONSUMER (RT): move the head BACK by <paramref name="floats"/> into the intact kept-behind span. Returns false
    /// (nothing moved) when <paramref name="floats"/> is negative or exceeds <see cref="BehindFloats"/>. The producer, which
    /// sees the head through an acquire read, simply stops writing until the consumer has caught up again. Wait-free, alloc-free.</summary>
    public bool TryRewindConsumerSide(int floats)
    {
        long head = _head;
        if (floats < 0 || floats > BehindFloats) return false;
        Volatile.Write(ref _head, head - floats);
        return true;
    }
}
