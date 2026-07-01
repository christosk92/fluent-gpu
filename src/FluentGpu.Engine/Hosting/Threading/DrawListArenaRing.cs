using System;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// A ring of ≥3 render-readable DrawList arenas (design/subsystems/threading-render-seam.md §6), Cut A variant.
///
/// Cut A: the UI thread records the finished DrawList, copies it into the <see cref="FrontIndex"/> arena
/// (<see cref="WriteFront"/>), and publishes that index; the render thread submits from the published arena; the roles
/// then <see cref="Rotate"/> render-locally. Why ≥3: at submit the just-published arena's bytes feed instance buffers
/// the GPU is still reading (in flight until its fence completes), the previous arena is retained, and a third is free
/// for the next record — so a free arena always exists without blocking on the GPU fence. (In the Step-1 single-thread
/// build consume is immediate, so even one arena would suffice; ≥3 is the shape the render thread will need.)
///
/// Buffers are pinned + grown geometrically (never shrunk) so a steady frame copies into a pre-sized arena with
/// <b>zero managed allocation</b>. Growth happens only on a new high-water DrawList size (rare, warmup).
/// </summary>
public sealed class DrawListArenaRing
{
    public const int MinArenas = 3;

    private readonly byte[][] _cmds;    // per-arena command bytes (pinned)
    private readonly ulong[][] _sort;   // per-arena sort keys (pinned)
    private int _front;

    public DrawListArenaRing(int arenas = MinArenas, int cmdCap = 1 << 16, int sortCap = 1 << 12)
    {
        int n = Math.Max(MinArenas, arenas);
        _cmds = new byte[n][];
        _sort = new ulong[n][];
        for (int i = 0; i < n; i++)
        {
            _cmds[i] = GC.AllocateUninitializedArray<byte>(Math.Max(1, cmdCap), pinned: true);
            _sort[i] = GC.AllocateUninitializedArray<ulong>(Math.Max(1, sortCap), pinned: true);
        }
    }

    public int ArenaCount => _cmds.Length;

    /// <summary>The arena the next <see cref="WriteFront"/> targets (record destination this frame).</summary>
    public int FrontIndex => _front;

    /// <summary>Render-readable view of a recorded arena's command bytes.</summary>
    public ReadOnlySpan<byte> Bytes(int arena, int len) => _cmds[arena].AsSpan(0, len);

    /// <summary>Render-readable view of a recorded arena's sort keys.</summary>
    public ReadOnlySpan<ulong> SortKeys(int arena, int len) => _sort[arena].AsSpan(0, len);

    /// <summary>UI thread: copy a finished DrawList into the FRONT arena (growing the pinned buffers only on a new
    /// high-water size), returning the arena index to publish. Zero-alloc in steady state.</summary>
    public int WriteFront(ReadOnlySpan<byte> cmds, ReadOnlySpan<ulong> sort)
    {
        ThreadGuard.AssertUi();
        int i = _front;
        if (cmds.Length > _cmds[i].Length)
            _cmds[i] = GC.AllocateUninitializedArray<byte>(NextCap(_cmds[i].Length, cmds.Length), pinned: true);
        if (sort.Length > _sort[i].Length)
            _sort[i] = GC.AllocateUninitializedArray<ulong>(NextCap(_sort[i].Length, sort.Length), pinned: true);
        cmds.CopyTo(_cmds[i]);
        sort.CopyTo(_sort[i]);
        return i;
    }

    /// <summary>Advance the front so the next record targets a different arena than the one just published — with ≥3
    /// arenas a free arena always exists even with one published + one in-flight. Render-thread-local when the seam
    /// spawns; the UI thread calls it in the single-thread build.</summary>
    public void Rotate() => _front = (_front + 1) % _cmds.Length;

    private static int NextCap(int current, int need)
    {
        int cap = Math.Max(1, current);
        while (cap < need) cap <<= 1;
        return cap;
    }
}
