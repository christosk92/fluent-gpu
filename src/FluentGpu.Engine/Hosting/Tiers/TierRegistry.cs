using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Hosting.Tiers;

/// <summary>
/// Ambient handle to the host's <see cref="TierRegistry"/> (the <see cref="HostTimers"/> pattern). <c>UseTier</c> resolves it
/// once at mount; null (a bare context with no <see cref="AppHost"/>) makes the hook write its signal directly.
/// </summary>
public static class TierHost
{
    public static readonly Context<TierRegistry?> Registry = new(null);
}

/// <summary>
/// One <c>UseTier</c> cell's host-visible state: the COMMITTED tier signal consumers read, and the PENDING tier the hook
/// found after mount but has not written yet (<see cref="TierRegistry.CommitPending"/> writes it).
/// </summary>
public sealed class TierSlot
{
    internal int Pending;
    internal bool HasPending;
    internal bool Queued;
    internal bool Disposed;

    /// <summary>The committed tier. Changes only when the host commits a crossing (or on the first mount).</summary>
    public Signal<int> Committed { get; } = new(0);

    /// <summary>The tier the source currently resolves to: the pending value when a crossing is waiting for its commit,
    /// else the committed one. The hysteresis state the next resolve starts from.</summary>
    public int Logical => HasPending ? Pending : Committed.Peek();

    /// <summary>True while a crossing is stored and not yet committed.</summary>
    public bool IsPending => HasPending;
}

/// <summary>
/// The host-owned table of pending tier crossings (plan §3.3 rule 3). A tier crossing found AFTER mount is not written to
/// its signal where it is found: every in-frame global flush (<c>FlushRebindsToQuiescence</c>, the flow pass, the realize
/// loops) would reconcile the write with no FLIP capture and the crossing would snap. It is stored here instead, a frame is
/// requested at once, and <see cref="AppHost"/>'s <c>CommitPendingTiers</c> writes every pending tier on the first frame
/// that is NOT resized, after the scroll frame step and before the reconcile decision, so the write makes the runtime
/// pending and FLIP capture runs with the post-resize rects as "before".
/// <para>UI-thread only; <see cref="Store"/>, <see cref="CommitPending"/> and the counters allocate nothing in steady state
/// (the slot list grows once, at the first crossing of each slot).</para>
/// </summary>
public sealed class TierRegistry
{
    private readonly Action _requestFrame;
    private readonly List<TierSlot> _queue = new();
    private int _pendingCount;

    public TierRegistry(Action requestFrame) => _requestFrame = requestFrame;

    /// <summary>True while at least one slot holds an uncommitted crossing. The host counts it as work (the wake term and
    /// the modal-loop keep-alive idle decision).</summary>
    public bool HasPending => _pendingCount > 0;

    /// <summary>The number of slots holding an uncommitted crossing.</summary>
    public int PendingCount => _pendingCount;

    /// <summary>Total commits written since construction (a test/census counter).</summary>
    public long CommittedWrites { get; private set; }

    /// <summary>Create a slot bound to this registry.</summary>
    public TierSlot Create() => new();

    /// <summary>Store <paramref name="tier"/> as the slot's pending crossing and request a frame immediately.</summary>
    public void Store(TierSlot slot, int tier)
    {
        if (slot.Disposed) return;
        slot.Pending = tier;
        if (!slot.HasPending) { slot.HasPending = true; _pendingCount++; }
        if (!slot.Queued) { slot.Queued = true; _queue.Add(slot); }
        _requestFrame();
    }

    /// <summary>Drop the slot's pending crossing (the source crossed back before the commit).</summary>
    public void Cancel(TierSlot slot)
    {
        if (!slot.HasPending) return;
        slot.HasPending = false;
        _pendingCount--;
    }

    /// <summary>Unmount: drop the pending crossing and make the slot inert.</summary>
    public void Release(TierSlot slot)
    {
        Cancel(slot);
        slot.Disposed = true;
    }

    /// <summary>Write every pending tier into its signal (equality-gated, so a tier that crossed back writes nothing) and
    /// clear the table. Returns the number of signals that changed. The caller owns the "not resized" condition.</summary>
    public int CommitPending()
    {
        if (_queue.Count == 0) return 0;
        int changed = 0;
        for (int i = 0; i < _queue.Count; i++)
        {
            TierSlot s = _queue[i];
            s.Queued = false;
            if (!s.HasPending) continue;
            s.HasPending = false;
            _pendingCount--;
            if (s.Disposed) continue;
            if (s.Committed.SetIfChanged(s.Pending)) changed++;
        }
        _queue.Clear();
        CommittedWrites += changed;
        return changed;
    }
}
