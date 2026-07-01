using System;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Consume-gated reclaim (design/subsystems/threading-render-seam.md §5.2). A resource (an arena slot, a freed
/// GPU-resource handle) released while producing frame <c>p</c> is reclaimable only once the consumer has acquired a
/// strictly-later frame — <c>LastConsumedSeq &gt; p + Quarantine - 1</c> — so no in-flight frame still references it.
/// This is the keystone that turns "slot reuse across the seam" from a use-after-free hazard into a
/// SAFE-by-construction rule, verified single-threaded (Step 1) before the render thread exists.
///
/// Single-thread build: consume is immediate, so <see cref="QuarantinePolicy.Quarantine"/> is logically 0 and items
/// reclaim on the next tick. The ledger is a fixed-capacity ring (sized to the max in-flight count) so
/// <see cref="OnFreed"/> and <see cref="TryReclaim"/> are <b>zero-alloc</b>.
/// </summary>
public sealed class QuarantineLedger
{
    private readonly (int Item, ulong FreedAtSeq)[] _pending;
    private int _head;
    private int _count;

    public QuarantineLedger(int capacity = 256) => _pending = new (int, ulong)[Math.Max(8, capacity)];

    /// <summary>Items awaiting the consume gate.</summary>
    public int PendingCount => _count;

    /// <summary>UI thread: record that <paramref name="item"/> was freed while producing frame <paramref name="currentSeq"/>.</summary>
    public void OnFreed(int item, ulong currentSeq)
    {
        ThreadGuard.AssertUi();
        if (_count == _pending.Length) return;   // ring full: it is sized to the max in-flight count, so this is a sizing bug (guarded elsewhere)
        _pending[(_head + _count) % _pending.Length] = (item, currentSeq);
        _count++;
    }

    /// <summary>UI thread, once per frame after publish: pop the next item the consumer has moved strictly past — call in
    /// a <c>while (TryReclaim(seq, out var item)) Free(item);</c> loop. FIFO by free-time, so the head is always the
    /// oldest; stopping at the first non-eligible item is correct. Zero-alloc (no delegate).</summary>
    public bool TryReclaim(ulong lastConsumedSeq, out int item)
    {
        ThreadGuard.AssertUi();
        if (_count > 0)
        {
            ref readonly var s = ref _pending[_head];
            if (lastConsumedSeq > s.FreedAtSeq + (ulong)(QuarantinePolicy.Quarantine - 1))
            {
                item = s.Item;
                _head = (_head + 1) % _pending.Length;
                _count--;
                return true;
            }
        }
        item = 0;
        return false;
    }
}
