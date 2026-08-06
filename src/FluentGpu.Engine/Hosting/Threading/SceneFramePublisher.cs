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

    // Publish-gap accumulation (gpu-renderer.md §13.1). TryAcquire is last-writer-wins (DropOldest), so under sustained
    // load the consumer never sees some published frames — and their repaint damage would be lost, leaving stale pixels
    // exactly when partial repaint matters most. Carry the un-consumed frames' region forward into the next publish:
    // _pendingRepaint holds the region of the most recent publish, valid until the consumer acknowledges that seq.
    // Over-inclusion is the safe direction (a rect repainted twice costs fill; one missed leaves a ghost).
    private RepaintDamageRegion _pendingRepaint;
    private ulong _pendingRepaintSeq;

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
    public ulong Publish(ReadOnlySpan<byte> cmds, ReadOnlySpan<ulong> sort, in FrameInfo submit,
                         bool suppressVsync = false, bool interactivePresent = false)
    {
        ThreadGuard.AssertUi();
        int consumed = Volatile.Read(ref _consumeIdx);              // ACQUIRE — what the consumer is/was reading
        int free = PickFreeSlot(_publishedIdx, consumed);           // never the published-not-consumed nor the consuming slot ⇒ arena-safe
        if (cmds.Length > _cmds[free].Length) _cmds[free] = GC.AllocateUninitializedArray<byte>(NextCap(_cmds[free].Length, cmds.Length), pinned: true);
        if (sort.Length > _sort[free].Length) _sort[free] = GC.AllocateUninitializedArray<ulong>(NextCap(_sort[free].Length, sort.Length), pinned: true);
        cmds.CopyTo(_cmds[free]);
        sort.CopyTo(_sort[free]);
        ulong seq = ++_publishSeq;
        // Fold forward the damage of every frame published since the consumer's last acknowledgement (DropOldest can drop
        // them entirely), then remember THIS frame's accumulated region as the new pending set. Once the consumer has
        // acknowledged _pendingRepaintSeq (or anything newer) the carry is discharged. Racing a concurrent TryAcquire can
        // only make us re-carry an already-consumed region — the safe direction.
        var region = submit.RepaintDamage;
        if (_pendingRepaintSeq != 0 && Volatile.Read(ref _lastConsumedSeq) < _pendingRepaintSeq)
            region.Union(in _pendingRepaint);
        _pendingRepaint = region;
        _pendingRepaintSeq = seq;
        _slots[free] = new RenderFrame
        {
            PublishSeq = seq,
            ArenaIndex = free,
            ByteLen = cmds.Length,
            SortLen = sort.Length,
            // The seq is stamped INSIDE Publish (the counter lives here) so a consumer can detect skipped logical frames
            // from the FrameInfo alone, without reaching back into the header.
            Submit = submit with { RepaintDamage = region, PublishSequence = seq },
            SuppressVsync = suppressVsync,
            InteractivePresent = interactivePresent,
        };
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
        // DropOldest-with-dedup: if the latest published frame is the one we already consumed, there is nothing new — a
        // bare wake (no intervening Publish) must NOT re-submit/re-present the last frame. This makes the consumer
        // idempotent across wakes, which the detached-window routing relies on (a child's wake carries no parent Publish,
        // so the parent seam's TryAcquire here no-ops instead of re-presenting the parent's last frame).
        if (frame.PublishSeq == Volatile.Read(ref _lastConsumedSeq)) { frame = default; return false; }
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
