using System;
using System.Threading;
using FluentGpu.Rhi;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// The UI→render seam point (design/subsystems/threading-render-seam.md §2), <b>Cut A</b> variant: a triple-buffered,
/// both-directions-volatile hand-off carrying a <see cref="RenderFrame"/> header. Each of the 3 slots owns its own
/// render-readable DrawList arena (command bytes + sort keys); <see cref="Publish"/> copies the finished DrawList into
/// the picked slot's arena, so the arena's lifetime IS the slot's — and <see cref="PickFreeSlot"/> guarantees the UI
/// never writes the slot the consumer is reading. That makes the async flip (Step 5) arena-safe BY CONSTRUCTION (§2.3):
/// no torn read, no consume-gating needed for the arena itself.
///
/// <b>Ordering (§2.2).</b> The DrawList memcpy + the header stores in <see cref="Publish"/> are ordinary stores; the
/// single <c>Volatile.Write(ref _publishedIdx)</c> is a RELEASE that publishes them to the paired ACQUIRE
/// <c>Volatile.Read(ref _publishedIdx)</c> in <see cref="TryAcquire"/>. The reverse indices are also volatile so the UI
/// slot-picker + quarantine tick never race the consumer.
///
/// Single-consumer. Step 1 (thread off): the UI both Publishes and TryAcquires inline. Step 4/5 (thread on): the
/// <c>fgpu-render</c> thread TryAcquires; the UI only Publishes. Zero managed allocation at Publish in steady state
/// (the per-slot arenas are pinned + grown only on a new high-water DrawList size).
/// </summary>
public sealed class SceneFramePublisher
{
    private readonly RenderFrame[] _slots = new RenderFrame[3];   // triple-buffered header (§2.3)
    private readonly byte[][] _cmds = new byte[3][];              // per-slot command-byte arena (pinned)
    private readonly ulong[][] _sort = new ulong[3][];            // per-slot sort-key arena (pinned)

    private int _publishedIdx = -1;    // UI writes (release) → consumer reads (acquire)
    private int _consumeIdx = -1;      // consumer writes (release) → UI reads (acquire)
    private ulong _publishSeq;         // UI-private monotonic counter
    private ulong _lastConsumedSeq;    // consumer writes (release) → UI reads (acquire); also drives quarantine (§5)

    public SceneFramePublisher(int cmdCap = 1 << 16, int sortCap = 1 << 12)
    {
        for (int i = 0; i < 3; i++)
        {
            _cmds[i] = GC.AllocateUninitializedArray<byte>(Math.Max(1, cmdCap), pinned: true);
            _sort[i] = GC.AllocateUninitializedArray<ulong>(Math.Max(1, sortCap), pinned: true);
        }
    }

    /// <summary>The publish seq of the latest frame the consumer has acquired (acquire read) — feeds
    /// <see cref="QuarantineLedger.TryReclaim"/>. 0 until the first consume.</summary>
    public ulong LastConsumedSeq => Volatile.Read(ref _lastConsumedSeq);

    /// <summary>The last seq handed to <see cref="Publish"/> (UI-private; no cross-thread read).</summary>
    public ulong PublishSeq => _publishSeq;

    /// <summary>UI thread: copy a finished DrawList into a FREE slot's arena and publish it. Returns the monotonic
    /// publish seq. Zero-alloc in steady state (grows a pinned arena only on a new high-water size).</summary>
    public ulong Publish(ReadOnlySpan<byte> cmds, ReadOnlySpan<ulong> sort, in FrameInfo submit, bool suppressVsync = false)
    {
        ThreadGuard.AssertUi();
        int consumed = Volatile.Read(ref _consumeIdx);              // ACQUIRE — what the consumer is/was reading
        int free = PickFreeSlot(_publishedIdx, consumed);           // never the published-not-consumed nor the consuming slot ⇒ arena-safe
        if (cmds.Length > _cmds[free].Length) _cmds[free] = GC.AllocateUninitializedArray<byte>(NextCap(_cmds[free].Length, cmds.Length), pinned: true);
        if (sort.Length > _sort[free].Length) _sort[free] = GC.AllocateUninitializedArray<ulong>(NextCap(_sort[free].Length, sort.Length), pinned: true);
        cmds.CopyTo(_cmds[free]);
        sort.CopyTo(_sort[free]);
        ulong seq = ++_publishSeq;
        _slots[free] = new RenderFrame { PublishSeq = seq, ArenaIndex = free, ByteLen = cmds.Length, SortLen = sort.Length, Submit = submit, SuppressVsync = suppressVsync };
        Volatile.Write(ref _publishedIdx, free);                    // RELEASE — makes the arena copy + header visible-before
        return seq;
    }

    /// <summary>Consumer: acquire the latest published frame (last-writer-wins ⇒ never stale when a newer exists, the
    /// DropOldest coalesce, §11). Read the bytes via <see cref="Bytes"/>/<see cref="SortKeys"/>. Zero-alloc.</summary>
    public bool TryAcquire(out RenderFrame frame)
    {
        int idx = Volatile.Read(ref _publishedIdx);                 // ACQUIRE — pairs with the Publish release
        if (idx < 0) { frame = default; return false; }
        frame = _slots[idx];                                        // POD header copy
        Volatile.Write(ref _consumeIdx, idx);                       // RELEASE — UI now knows this slot is in use (won't overwrite its arena)
        Volatile.Write(ref _lastConsumedSeq, frame.PublishSeq);     // RELEASE — drives consume-gated quarantine (§5)
        return true;
    }

    /// <summary>Consumer: the command bytes of an acquired frame (over its slot's arena).</summary>
    public ReadOnlySpan<byte> Bytes(in RenderFrame rf) => _cmds[rf.ArenaIndex].AsSpan(0, rf.ByteLen);

    /// <summary>Consumer: the sort keys of an acquired frame (over its slot's arena).</summary>
    public ReadOnlySpan<ulong> SortKeys(in RenderFrame rf) => _sort[rf.ArenaIndex].AsSpan(0, rf.SortLen);

    private static int PickFreeSlot(int published, int consuming)
    {
        for (int i = 0; i < 3; i++)
            if (i != published && i != consuming) return i;
        return 0;   // unreachable with 3 slots and ≤2 occupied
    }

    private static int NextCap(int current, int need)
    {
        int cap = Math.Max(1, current);
        while (cap < need) cap <<= 1;
        return cap;
    }
}
