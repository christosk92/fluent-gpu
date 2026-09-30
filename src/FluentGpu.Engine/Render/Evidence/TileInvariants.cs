using System.Threading;

namespace FluentGpu.Render.Evidence;

/// <summary>One stale tile as the invariant saw it: the turn, the slice row and the node it is cut at, the tile, the
/// content hash the current stream wants, the hash its pixels were rastered for, the turn that rastered them, and the
/// slice row's <c>Sub</c> (segment·8 + <c>SliceRole</c>).</summary>
public readonly record struct StaleTileSample(int Frame, int SliceId, int NodeIndex, uint Gen, short Tx, short Ty,
    ulong Want, ulong Have, int RasterFrame, int Sub = 0);

/// <summary>
/// The PROCESS-WIDE tally of the stale-tile invariant (docs/plans/evidence-diagnostics-implementation.md §A.1): every
/// composite turn of every host that ended with a stale tile (a valid tile whose pixels the current stream no longer
/// describes) counts here, always on, with the most recent offender kept for the log and the gates. Plain counters —
/// the headless suite's permanent <c>gate.tiles.stale-zero</c> sweep compares them around every suite, and a live
/// Wavee shows them on Diagnostics ▸ Tiles. Written by the composite turn's owner (render thread, or the UI thread
/// inline); read from anywhere.
/// </summary>
public static class TileInvariants
{
    private static long s_staleTurns, s_staleTiles;
    private static StaleTileSample s_last;
    private static readonly object s_lock = new();

    /// <summary>Composite turns that ended with at least one stale tile, process lifetime.</summary>
    public static long StaleTurns => Volatile.Read(ref s_staleTurns);

    /// <summary>Stale tiles summed over those turns.</summary>
    public static long StaleTiles => Volatile.Read(ref s_staleTiles);

    /// <summary>The most recent stale tile (default before the first).</summary>
    public static StaleTileSample LastStale { get { lock (s_lock) return s_last; } }

    /// <summary>A turn ended with <paramref name="tiles"/> &gt; 0 stale tiles, the first of which is <paramref name="first"/>.</summary>
    public static void NoteStaleTurn(int tiles, in StaleTileSample first)
    {
        if (tiles <= 0) return;
        Interlocked.Increment(ref s_staleTurns);
        Interlocked.Add(ref s_staleTiles, tiles);
        lock (s_lock) s_last = first;
    }
}
