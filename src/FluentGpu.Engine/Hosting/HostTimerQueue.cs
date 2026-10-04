using System;
using FluentGpu.Hooks;

namespace FluentGpu.Hosting;

/// <summary>
/// Ambient handle to the host's frame-clock <see cref="HostTimerQueue"/>. The timing hooks
/// (<c>UseDebouncedValue</c>/<c>UseThrottledValue</c>/<c>UseTimeout</c>/<c>UseInterval</c>) resolve it once at mount via
/// this channel (the <c>HostDispatch.Post</c> ambient pattern). Null when no host published one (a bare headless test
/// with no <c>AppHost</c>) — the hooks then degrade to inert no-ops. Published by <see cref="AppHost"/>.
/// </summary>
public static class HostTimers
{
    public static readonly Context<HostTimerQueue?> Current = new(null);
}

/// <summary>
/// An engine-owned min-heap of one-shot timers keyed on the host frame clock — the substrate for the UI timing hooks.
/// A timer is a <c>(dueMs, generation, callback)</c> triple; the callback is a mount-allocated, stable delegate so a
/// FIRE allocates nothing. Draining happens at the top of the frame, before the reactive flush, so a fired timer's
/// signal writes coalesce into the same re-render (see <see cref="AppHost.Paint"/>).
/// <para>
/// The clock is injected: a real window uses the wall clock (so idle quiesce is accurate across a blocked
/// <c>WaitForWork</c> — the animation frame delta is clamped and would drift), and the headless harness uses the host's
/// deterministic accumulated frame delta so the VerticalSlice gates fire predictably by pumping frames.
/// </para>
/// <para>
/// Re-arming is a LAZY re-insert guarded by a per-timer generation: the owner bumps its generation and schedules a fresh
/// entry; the stale heap entry is left in place and skipped when it pops (its generation no longer matches). So a debounce
/// re-arm / an interval tick costs one push and zero removals, and a cancel / unmount is a single generation bump.
/// </para>
/// <para>
/// NOT the media clock: playback position is device-clock-derived and never rides this queue (WS-Media non-goal).
/// </para>
/// UI-thread only: schedule and drain run on the frame thread. A cross-thread caller marshals through
/// <c>HostDispatch.Post</c> first (which wakes the loop), so a schedule is always seen by the very next
/// <c>RecommendedWaitMs</c> — no lost wake.
/// </summary>
public sealed class HostTimerQueue
{
    private struct Entry
    {
        public double DueMs;
        public long Seq;            // monotonic tiebreaker → a total order among equal due times (deterministic)
        public long Gen;
        public Action<long> Callback;
        public Type? Owner;         // the component type that armed it (wake census: timersSeen); null = unattributable
    }

    private Entry[] _heap;
    private int _count;
    private long _seq;
    private readonly Func<double> _nowMs;

    public HostTimerQueue(Func<double> nowMs, int capacity = 16)
    {
        _nowMs = nowMs;
        _heap = new Entry[Math.Max(1, capacity)];   // pre-sized; grows only at schedule time (cold)
    }

    /// <summary>The current time on this queue's clock (ms). Wall clock for a real window, frame-delta accumulator headless.</summary>
    public double NowMs => _nowMs();

    /// <summary>Live timer count. Zero ⇒ the frame drain is skipped entirely (no per-frame cost when nothing is armed).</summary>
    public int Count => _count;

    /// <summary>Whether the earliest timer is due at <paramref name="now"/> — a SINGLE comparison (one on an empty heap).</summary>
    public bool HasDue(double now) => _count > 0 && _heap[0].DueMs <= now;

    /// <summary>Whether the earliest timer is due at the current clock. Drives the <c>WakeReasons.Timer</c> bit.</summary>
    public bool HasDueNow() => HasDue(_nowMs());

    /// <summary>The earliest due time (ms) if any timer is armed — the loop's wait is shortened to reach it.</summary>
    public bool TryPeekEarliest(out double dueMs)
    {
        if (_count > 0) { dueMs = _heap[0].DueMs; return true; }
        dueMs = 0.0;
        return false;
    }

    /// <summary>Arm a timer to fire <paramref name="callback"/> at <paramref name="dueMs"/> (this queue's clock), tagged
    /// with <paramref name="gen"/>. The callback is invoked with the entry's generation so the owner can no-op a stale
    /// fire (cancel/re-arm/unmount bumped the generation). Grows the backing array only when full (cold).
    /// <paramref name="owner"/> names who armed it for the <c>[wake]</c> census's <c>timersSeen=</c> (the hooks pass their
    /// component's type); omitted, the callback target's type stands in, so a timer is never anonymous in the census.</summary>
    public void Schedule(double dueMs, long gen, Action<long> callback, Type? owner = null)
    {
        if (callback is null) return;
        if (_count == _heap.Length) Array.Resize(ref _heap, _heap.Length * 2);
        int i = _count++;
        _heap[i] = new Entry { DueMs = dueMs, Seq = _seq++, Gen = gen, Callback = callback, Owner = owner ?? callback.Target?.GetType() };
        SiftUp(i);
    }

    // ── Fire census (the [wake] line's timersSeen=) ──────────────────────────────────────────────────────────────────
    // The wake census can only say "a timer was due" (WakeReasons.Timer); this names WHO, in the shape of pollersSeen: every
    // owner type whose timer fired this census window, with its fire count. A fixed table filled in first-seen order (no
    // allocation, no LINQ, a handful of reference compares per fire) and cleared by the census after each report. A fire is
    // counted when the entry pops, whether or not its generation guard then makes the callback a no-op.
    private const int MaxTimerOwners = 12;
    private readonly Type?[] _fireOwners = new Type?[MaxTimerOwners];
    private readonly long[] _fireCounts = new long[MaxTimerOwners];
    private int _fireOwnerCount;
    private long _fireUnowned, _fireOverflow;

    private void NoteFire(Type? owner)
    {
        if (owner is null) { _fireUnowned++; return; }
        for (int i = 0; i < _fireOwnerCount; i++)
            if (ReferenceEquals(_fireOwners[i], owner)) { _fireCounts[i]++; return; }
        if (_fireOwnerCount < MaxTimerOwners)
        {
            _fireOwners[_fireOwnerCount] = owner;
            _fireCounts[_fireOwnerCount] = 1;
            _fireOwnerCount++;
        }
        else _fireOverflow++;
    }

    /// <summary>Fires counted since the last <see cref="ResetFireCensus"/> (all owners, the unattributed and the overflow).</summary>
    public long FiresInWindow
    {
        get
        {
            long total = _fireUnowned + _fireOverflow;
            for (int i = 0; i < _fireOwnerCount; i++) total += _fireCounts[i];
            return total;
        }
    }

    /// <summary>Append <c>timersSeen=N:Owner×fires,…</c> (or <c>timersSeen=0</c>): every timer owner that fired in the current
    /// window. <c>?</c> is a timer nothing could attribute, <c>+</c> the fires of owners past the table's capacity. Report
    /// cadence only, UI thread.</summary>
    internal void AppendTimersSeen(System.Text.StringBuilder sb)
    {
        int distinct = _fireOwnerCount + (_fireUnowned > 0 ? 1 : 0) + (_fireOverflow > 0 ? 1 : 0);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" | timersSeen={distinct}");
        if (distinct == 0) return;
        sb.Append(':');
        bool first = true;
        for (int i = 0; i < _fireOwnerCount; i++)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(_fireOwners[i]!.Name).Append(System.Globalization.CultureInfo.InvariantCulture, $"×{_fireCounts[i]}");
        }
        if (_fireUnowned > 0)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"?×{_fireUnowned}");
        }
        if (_fireOverflow > 0)
        {
            if (!first) sb.Append(',');
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"+×{_fireOverflow}");
        }
    }

    /// <summary>Open a fresh census window (the owner slots are reused, the counts zeroed).</summary>
    internal void ResetFireCensus()
    {
        Array.Clear(_fireOwners);
        Array.Clear(_fireCounts);
        _fireOwnerCount = 0;
        _fireUnowned = 0;
        _fireOverflow = 0;
    }

    /// <summary>Eagerly drop every heap entry tagged with <paramref name="gen"/>. Lazy cancel (generation bump alone)
    /// still works for re-arm; this is for pause/disable/unmount paths that would otherwise leave a phantom earliest-due
    /// shaping <c>RecommendedWaitMs</c> / <c>sole: timer</c> wakes toward a dead deadline.</summary>
    public void Cancel(long gen)
    {
        if (_count == 0) return;
        int w = 0;
        for (int r = 0; r < _count; r++)
        {
            if (_heap[r].Gen == gen) { _heap[r] = default; continue; }
            if (w != r) _heap[w] = _heap[r];
            w++;
        }
        if (w == _count) return;
        for (int i = w; i < _count; i++) _heap[i] = default;
        _count = w;
        for (int i = (_count >> 1) - 1; i >= 0; i--) SiftDown(i);
    }

    /// <summary>Remove all queued entries owned by a timer callback. Generation invalidation prevents stale entries
    /// from firing, but without eager removal they still wake the host at their old deadlines.</summary>
    public void Cancel(Action<long> callback)
    {
        if (_count == 0 || callback is null) return;
        int w = 0;
        for (int r = 0; r < _count; r++)
        {
            if (ReferenceEquals(_heap[r].Callback, callback)) { _heap[r] = default; continue; }
            if (w != r) _heap[w] = _heap[r];
            w++;
        }
        if (w == _count) return;
        for (int i = w; i < _count; i++) _heap[i] = default;
        _count = w;
        for (int i = (_count >> 1) - 1; i >= 0; i--) SiftDown(i);
    }

    /// <summary>Fire every timer due at the current clock, earliest first. Zero-alloc: callbacks are mount-allocated and
    /// generation-guarded; a callback may re-<see cref="Schedule"/> itself (an interval tick / a debounce re-arm) — those
    /// fresh entries are deferred to the NEXT drain (they carry a sequence past this drain's snapshot), so a sub-frame
    /// period can never spin the drain. An empty heap returns after one comparison.</summary>
    public void Drain()
    {
        if (_count == 0) return;
        double now = _nowMs();
        long limit = _seq;   // entries (re)scheduled during this drain get Seq >= limit → not fired this pass
        while (_count > 0 && _heap[0].DueMs <= now && _heap[0].Seq < limit)
        {
            Entry e = _heap[0];
            int last = --_count;
            if (last > 0)
            {
                _heap[0] = _heap[last];
                _heap[last] = default;   // release the delegate reference held by the popped slot
                SiftDown(0);
            }
            else _heap[0] = default;
            NoteFire(e.Owner);
            e.Callback(e.Gen);           // may re-Schedule (interval/debounce) — guarded above
        }
    }

    private void SiftUp(int i)
    {
        Entry e = _heap[i];
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (!Less(e, _heap[p])) break;
            _heap[i] = _heap[p];
            i = p;
        }
        _heap[i] = e;
    }

    private void SiftDown(int i)
    {
        Entry e = _heap[i];
        int half = _count >> 1;
        while (i < half)
        {
            int c = (i << 1) + 1;
            int r = c + 1;
            if (r < _count && Less(_heap[r], _heap[c])) c = r;
            if (!Less(_heap[c], e)) break;
            _heap[i] = _heap[c];
            i = c;
        }
        _heap[i] = e;
    }

    private static bool Less(in Entry a, in Entry b)
        => a.DueMs < b.DueMs || (a.DueMs == b.DueMs && a.Seq < b.Seq);
}
