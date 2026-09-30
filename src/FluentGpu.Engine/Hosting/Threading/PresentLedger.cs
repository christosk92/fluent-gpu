using System.Threading;

namespace FluentGpu.Hosting.Threading;

/// <summary>One FRESH present: the publication it put on the glass, the compositor tick it was decided for and when the
/// present call returned.</summary>
public readonly record struct PresentRecord(ulong PublishSeq, long TickSeq, long TickQpc, long DoneQpc);

/// <summary>"When did publication N reach the glass?" — a 64-slot ring the render thread writes on every FRESH present
/// (<see cref="RenderThread"/>) and the UI thread reads (the engaged-edge present line, <c>AppHost.Engaged.cs</c>).
/// Versioned slots (odd = being written): torn-free reads, no lock, no allocation. Process-wide like the scroll probe:
/// one render thread presents for the primary window (a detached child's presents go through <c>extraDrain</c> and are
/// not recorded here).</summary>
public static class PresentLedger
{
    public const int Capacity = 64;

    private struct Slot
    {
        public int Version;
        public PresentRecord Value;
    }

    private static readonly Slot[] s_ring = new Slot[Capacity];
    private static long s_count;

    /// <summary>RENDER THREAD: record one fresh present.</summary>
    public static void Record(ulong publishSeq, long tickSeq, long tickQpc, long doneQpc)
    {
        long n = s_count;
        ref Slot slot = ref s_ring[(int)(n & (Capacity - 1))];
        Volatile.Write(ref slot.Version, slot.Version + 1);
        Thread.MemoryBarrier();
        slot.Value = new PresentRecord(publishSeq, tickSeq, tickQpc, doneQpc);
        Thread.MemoryBarrier();
        Volatile.Write(ref slot.Version, slot.Version + 1);
        Volatile.Write(ref s_count, n + 1);
    }

    /// <summary>ANY THREAD: the EARLIEST recorded present whose publication is ≥ <paramref name="seq"/> — the present that
    /// first carried publication <paramref name="seq"/> (DropOldest may have coalesced it into a later one). False when
    /// no such present is in the ring yet.</summary>
    public static bool TryFindFirstAtOrAfter(ulong seq, out PresentRecord record)
    {
        long n = Volatile.Read(ref s_count);
        long from = Math.Max(0, n - Capacity);
        bool found = false;
        record = default;
        for (long k = from; k < n; k++)
        {
            ref Slot slot = ref s_ring[(int)(k & (Capacity - 1))];
            int v1 = Volatile.Read(ref slot.Version);
            if ((v1 & 1) != 0) continue;
            Thread.MemoryBarrier();
            PresentRecord r = slot.Value;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) != v1) continue;
            if (r.PublishSeq < seq) continue;
            if (!found || r.PublishSeq < record.PublishSeq) { record = r; found = true; }
        }
        return found;
    }

    /// <summary>Test seam.</summary>
    internal static void Reset()
    {
        Array.Clear(s_ring);
        s_count = 0;
    }
}
