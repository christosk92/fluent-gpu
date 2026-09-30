using System.Threading;

namespace FluentGpu.Scroll.Diag;

/// <summary>One sticky / sticky-clip ENGAGED crossing as the RENDER poser saw it: the render tick whose posed offset first
/// put the row on the other side of its engage threshold.</summary>
public readonly record struct EngagedCrossing(int Node, bool Engaged, long TickSeq, long TickQpc, double PresentSec,
    double Offset, long RecordedQpc);

/// <summary>The render side of the engaged-edge evidence (F(ii) of the 2026-09-25 RCA): the render poser records here
/// the tick at which a Sticky/StickyClip row's engaged state flipped at its posed offset; the UI thread, when it flips
/// the authored <c>engaged:</c> signal (<c>AppHost.Scroll.FillScrollCoverage</c>), looks up the matching crossing and
/// logs how many ticks/ms the UI edge — and the re-render it drives — trailed the pose.
/// <para>Single producer (THE render-thread poser, <c>ScrollPoser(recordsProbePoses: true)</c>, the same one that owns
/// the probe's render ring), any-thread reader. A fixed 32-slot ring of versioned slots (odd version = being written),
/// so a read is torn-free and nothing allocates. The per-node "last engaged" memory is a fixed 32-entry table, linear
/// scanned — sticky rows are a handful per page.</para></summary>
public static class EngagedCrossings
{
    public const int Capacity = 32;

    private struct Slot
    {
        public int Version;
        public EngagedCrossing Value;
    }

    private static readonly Slot[] s_ring = new Slot[Capacity];
    private static long s_count;

    // Render-thread-private: the node→last-engaged memory and the tick the current render turn presents for.
    private static readonly int[] s_nodes = new int[Capacity];
    private static readonly bool[] s_state = new bool[Capacity];
    private static int s_nodeCount;

    /// <summary>The compositor tick the CURRENT render turn presents for (render thread writes it before the turn's pose;
    /// the poser, on the same thread, stamps crossings with it). 0 without a display clock.</summary>
    public static long CurrentTickSeq { get; set; }
    /// <inheritdoc cref="CurrentTickSeq"/>
    public static long CurrentTickQpc { get; set; }

    /// <summary>Monotonic count of crossings recorded.</summary>
    public static long Count => Volatile.Read(ref s_count);

    /// <summary>RENDER THREAD (the recording poser): note <paramref name="node"/>'s engaged state at this tick's posed
    /// <paramref name="offset"/>; records a crossing when it differs from the last one noted for the node. The first
    /// observation of a node records nothing (there is no edge yet).</summary>
    public static void Observe(int node, bool engaged, double presentSec, double offset)
    {
        int i = 0;
        for (; i < s_nodeCount; i++) if (s_nodes[i] == node) break;
        if (i == s_nodeCount)
        {
            if (s_nodeCount == Capacity) i = node & (Capacity - 1);   // table full: reuse a slot (at worst one missed edge)
            else s_nodeCount++;
            s_nodes[i] = node;
            s_state[i] = engaged;
            return;
        }
        if (s_state[i] == engaged) return;
        s_state[i] = engaged;
        long n = s_count;
        ref Slot slot = ref s_ring[(int)(n & (Capacity - 1))];
        Volatile.Write(ref slot.Version, slot.Version + 1);
        Thread.MemoryBarrier();
        slot.Value = new EngagedCrossing(node, engaged, CurrentTickSeq, CurrentTickQpc, presentSec, offset,
            System.Diagnostics.Stopwatch.GetTimestamp());
        Thread.MemoryBarrier();
        Volatile.Write(ref slot.Version, slot.Version + 1);
        Volatile.Write(ref s_count, n + 1);
    }

    /// <summary>ANY THREAD: the most recent crossing of <paramref name="node"/> to <paramref name="engaged"/>. False when
    /// the ring holds none (never crossed, or overwritten).</summary>
    public static bool TryFindLatest(int node, bool engaged, out EngagedCrossing crossing)
    {
        long n = Volatile.Read(ref s_count);
        long from = Math.Max(0, n - Capacity);
        for (long k = n - 1; k >= from; k--)
        {
            ref Slot slot = ref s_ring[(int)(k & (Capacity - 1))];
            int v1 = Volatile.Read(ref slot.Version);
            if ((v1 & 1) != 0) continue;
            Thread.MemoryBarrier();
            EngagedCrossing c = slot.Value;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) != v1) continue;
            if (c.Node == node && c.Engaged == engaged) { crossing = c; return true; }
        }
        crossing = default;
        return false;
    }

    /// <summary>Test seam: forget everything.</summary>
    internal static void Reset()
    {
        Array.Clear(s_ring);
        s_count = 0;
        s_nodeCount = 0;
        CurrentTickSeq = CurrentTickQpc = 0;
    }
}
