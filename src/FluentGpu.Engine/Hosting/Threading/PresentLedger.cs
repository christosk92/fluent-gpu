using System.Threading;

namespace FluentGpu.Hosting.Threading;

/// <summary>One FRESH present: the publication it put on the glass, the compositor tick it was decided for and when the
/// present call returned. <paramref name="Target"/> is 0 for the primary window and the detached child's pace-target id
/// (<see cref="ChildPresentPace.Target"/>) for a child row.</summary>
public readonly record struct PresentRecord(ulong PublishSeq, long TickSeq, long TickQpc, long DoneQpc, int Target = 0);

/// <summary>"When did publication N reach the glass?" — a 64-slot ring the render thread writes on every FRESH present
/// (<see cref="RenderThread"/>) and the UI thread reads (the engaged-edge present line, <c>AppHost.Engaged.cs</c>).
/// Versioned slots (odd = being written): torn-free reads, no lock, no allocation. Process-wide like the scroll probe.
/// <para>The PRIMARY window's presents go in the primary ring (<see cref="Record"/>, <see cref="TryFindFirstAtOrAfter"/>).
/// A detached child's presents (they ride <c>extraDrain</c>) go in a SEPARATE ring (<see cref="RecordChild"/>,
/// <see cref="TryFindChildFirstAtOrAfter"/>): a child's publication seq is numbered by its OWN seam, so mixing it into the
/// primary ring would make "the first present at or after seq N" answer for the wrong window.</para></summary>
public static class PresentLedger
{
    public const int Capacity = 64;

    private struct Slot
    {
        public int Version;
        public PresentRecord Value;
    }

    /// <summary>One 64-slot versioned ring (single writer: the render thread).</summary>
    private sealed class Ring
    {
        private readonly Slot[] _slots = new Slot[Capacity];
        private long _count;

        public void Record(in PresentRecord value)
        {
            long n = _count;
            ref Slot slot = ref _slots[(int)(n & (Capacity - 1))];
            Volatile.Write(ref slot.Version, slot.Version + 1);
            Thread.MemoryBarrier();
            slot.Value = value;
            Thread.MemoryBarrier();
            Volatile.Write(ref slot.Version, slot.Version + 1);
            Volatile.Write(ref _count, n + 1);
        }

        /// <summary>The EARLIEST recorded present of <paramref name="target"/> (-1 = any) whose publication is ≥ <paramref name="seq"/>.</summary>
        public bool TryFindFirstAtOrAfter(ulong seq, int target, out PresentRecord record)
        {
            long n = Volatile.Read(ref _count);
            long from = Math.Max(0, n - Capacity);
            bool found = false;
            record = default;
            for (long k = from; k < n; k++)
            {
                ref Slot slot = ref _slots[(int)(k & (Capacity - 1))];
                int v1 = Volatile.Read(ref slot.Version);
                if ((v1 & 1) != 0) continue;
                Thread.MemoryBarrier();
                PresentRecord r = slot.Value;
                Thread.MemoryBarrier();
                if (Volatile.Read(ref slot.Version) != v1) continue;
                if (r.PublishSeq < seq) continue;
                if (target >= 0 && r.Target != target) continue;
                if (!found || r.PublishSeq < record.PublishSeq) { record = r; found = true; }
            }
            return found;
        }

        public void Reset()
        {
            Array.Clear(_slots);
            _count = 0;
        }
    }

    private static readonly Ring s_primary = new();
    private static readonly Ring s_children = new();

    /// <summary>RENDER THREAD: record one fresh present of the primary window.</summary>
    public static void Record(ulong publishSeq, long tickSeq, long tickQpc, long doneQpc)
        => s_primary.Record(new PresentRecord(publishSeq, tickSeq, tickQpc, doneQpc));

    /// <summary>RENDER THREAD: record one fresh present of detached child <paramref name="target"/>
    /// (<see cref="ChildPresentPace.Target"/>, always ≥ 1).</summary>
    public static void RecordChild(int target, ulong publishSeq, long tickSeq, long tickQpc, long doneQpc)
        => s_children.Record(new PresentRecord(publishSeq, tickSeq, tickQpc, doneQpc, target));

    /// <summary>ANY THREAD: the EARLIEST recorded primary-window present whose publication is ≥ <paramref name="seq"/> — the
    /// present that first carried publication <paramref name="seq"/> (DropOldest may have coalesced it into a later one).
    /// False when no such present is in the ring yet.</summary>
    public static bool TryFindFirstAtOrAfter(ulong seq, out PresentRecord record)
        => s_primary.TryFindFirstAtOrAfter(seq, -1, out record);

    /// <summary>ANY THREAD: <see cref="TryFindFirstAtOrAfter"/> for detached child <paramref name="target"/>'s own
    /// publications. False when none of that child's presents is in the (shared, 64-slot) child ring.</summary>
    public static bool TryFindChildFirstAtOrAfter(int target, ulong seq, out PresentRecord record)
        => s_children.TryFindFirstAtOrAfter(seq, target, out record);

    /// <summary>Test seam.</summary>
    internal static void Reset()
    {
        s_primary.Reset();
        s_children.Reset();
    }
}
