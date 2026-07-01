using System.Threading;
using FluentGpu.Rhi;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// The UI→render seam point (design/subsystems/threading-render-seam.md §2), <b>Cut A</b> variant: a triple-buffered,
/// both-directions-volatile hand-off carrying a <see cref="RenderFrame"/> header (the DrawList bytes live in the
/// <see cref="DrawListArenaRing"/>, referenced by <see cref="RenderFrame.ArenaIndex"/>).
///
/// <b>Ordering (§2.2), stated exactly.</b> Every field store in <see cref="Publish"/> is an ordinary store; the single
/// <c>Volatile.Write(ref _publishedIdx)</c> is a RELEASE that publishes all prior stores to any thread performing the
/// paired ACQUIRE <c>Volatile.Read(ref _publishedIdx)</c> in <see cref="TryAcquire"/>. The reverse direction
/// (<c>_consumeIdx</c>, <c>_lastConsumedSeq</c>) is also volatile so the UI slot-picker + quarantine tick never race
/// the consumer. Three slots (§2.3) guarantee the UI can always pick a slot that is neither published-not-yet-consumed
/// nor consuming, without blocking.
///
/// <b>Step 1 (single-thread build).</b> The UI thread calls BOTH <see cref="Publish"/> and <see cref="TryAcquire"/> in
/// the same frame, so the quarantine is logically 0 and no <c>AssertRender</c> is armed on the consume side yet — the
/// render thread takes over <see cref="TryAcquire"/> (and gets the <c>AssertRender</c> + the cap-1 wakeup channel) when
/// the seam spawns it, a later soak-gated step. The type is written so that flip is additive, not a rewrite.
/// </summary>
public sealed class SceneFramePublisher
{
    private readonly RenderFrame[] _slots = new RenderFrame[3];   // triple-buffered (§2.3)

    private int _publishedIdx = -1;    // UI writes (release) → render reads (acquire)
    private int _consumeIdx = -1;      // render writes (release) → UI reads (acquire)
    private ulong _publishSeq;         // UI-private monotonic counter
    private ulong _lastConsumedSeq;    // render writes (release) → UI reads (acquire); also drives quarantine (§5)

    /// <summary>The publish seq of the latest frame the consumer has acquired (acquire read). Feeds
    /// <see cref="QuarantineLedger.Tick"/>. 0 until the first consume.</summary>
    public ulong LastConsumedSeq => Volatile.Read(ref _lastConsumedSeq);

    /// <summary>The last seq handed to <see cref="Publish"/> (UI-private; no cross-thread read).</summary>
    public ulong PublishSeq => _publishSeq;

    /// <summary>UI thread: publish a recorded frame. Returns its monotonic publish seq (the quarantine key for anything
    /// freed while producing it). Zero-alloc.</summary>
    public ulong Publish(int arenaIndex, int byteLen, int sortLen, in FrameInfo submit, bool suppressVsync = false)
    {
        ThreadGuard.AssertUi();
        int consumed = Volatile.Read(ref _consumeIdx);              // ACQUIRE — what the consumer is/was reading
        int free = PickFreeSlot(_publishedIdx, consumed);           // never the published-not-consumed nor the consuming slot
        ulong seq = ++_publishSeq;
        _slots[free] = new RenderFrame { PublishSeq = seq, ArenaIndex = arenaIndex, ByteLen = byteLen, SortLen = sortLen, Submit = submit, SuppressVsync = suppressVsync };
        Volatile.Write(ref _publishedIdx, free);                    // RELEASE — makes the slot stores visible-before
        return seq;
    }

    /// <summary>Consumer: acquire the latest published frame (last-writer-wins ⇒ never a stale frame when a newer exists,
    /// the DropOldest coalesce, §11). Returns false when nothing has been published. Zero-alloc.
    /// Step 1: called on the UI thread (no <c>AssertRender</c> yet); the render thread owns it after the spawn step.</summary>
    public bool TryAcquire(out RenderFrame frame)
    {
        int idx = Volatile.Read(ref _publishedIdx);                 // ACQUIRE — pairs with the Publish release
        if (idx < 0) { frame = default; return false; }
        frame = _slots[idx];                                        // POD header copy
        Volatile.Write(ref _consumeIdx, idx);                       // RELEASE — UI now knows this slot is in use
        Volatile.Write(ref _lastConsumedSeq, frame.PublishSeq);     // RELEASE — drives consume-gated quarantine (§5)
        return true;
    }

    private static int PickFreeSlot(int published, int consuming)
    {
        for (int i = 0; i < 3; i++)
            if (i != published && i != consuming) return i;
        return 0;   // unreachable with 3 slots and ≤2 occupied
    }
}
