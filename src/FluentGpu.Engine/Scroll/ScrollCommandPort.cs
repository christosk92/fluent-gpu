using System;
using System.Diagnostics;
using System.Threading;

namespace FluentGpu.Scroll;

/// <summary>The kernel's ONE intake — a fixed 1024-slot ring, zero-alloc after construction. <see cref="Post"/> is
/// safe to call from a producer thread different from the thread that later calls <see cref="ScrollKernel.Tick"/>
/// (true SPSC via <see cref="Volatile"/> head/tail) — that is what makes the render-thread fling lease (plan §6)
/// possible without a second port. <see cref="ScrollKernel.Reclamp"/>'s structural-only drain (below) additionally
/// compacts the ring in place; that operation is UI-thread-only by contract (§3.3 — <c>Reclamp</c> never runs on the
/// render thread, only <c>Tick</c> does, and only for leased bodies), so it does not need to be lock-free against a
/// concurrent producer beyond the same SPSC guarantee <see cref="Post"/> already gives <see cref="TryDrain"/>.</summary>
public sealed class ScrollCommandPort
{
    public const int Capacity = 1024;
    private const int Mask = Capacity - 1; // Capacity is a power of two — required for the & mask below.

    private readonly ScrollInput[] _ring = new ScrollInput[Capacity];

    // _head is producer-owned (only Post writes it); _tail is consumer-owned (only TryDrain/DrainAll/DrainStructural
    // write it). Both are monotonically increasing counters (not pre-masked), so "used" and "empty vs full" are
    // unambiguous without a sentinel slot. Volatile publishes cross-thread visibility for the SPSC handoff.
    private int _head;
    private int _tail;

    /// <summary>Items currently queued (not yet drained). Safe to read from either thread.</summary>
    public int Pending => Volatile.Read(ref _head) - Volatile.Read(ref _tail);

    /// <summary>Post one command. Overflow policy (ring full, <see cref="Capacity"/> items already queued): for
    /// <see cref="ScrollInputKind.ContactMove"/>/<see cref="ScrollInputKind.FrameDelta"/> the OLDEST queued command
    /// for the SAME node and the SAME kind is overwritten in place with this one (coalesced — a stale mid-gesture
    /// sample is worthless once a fresher one exists); Begin/End/structural commands are NEVER dropped that way.
    /// If no coalescable slot exists (the ring is saturated with structural/Begin/End traffic — should never happen
    /// at 1024 capacity against one frame's input, since <see cref="ScrollKernel.Tick"/> fully drains every frame),
    /// this asserts in DEBUG and drops the INCOMING command rather than corrupt an existing one.</summary>
    public void Post(in ScrollInput input)
    {
        int head = _head;
        int tail = Volatile.Read(ref _tail);
        int used = head - tail;
        if (used >= Capacity)
        {
            if (TryCoalesceOverflow(in input, tail, head)) return;
            Debug.Assert(false, "ScrollCommandPort overflow with no coalescable slot (Begin/End/structural starvation) — dropping the newest command.");
            return;
        }
        _ring[head & Mask] = input;
        Volatile.Write(ref _head, head + 1);
    }

    private bool TryCoalesceOverflow(in ScrollInput input, int tail, int head)
    {
        if (input.Kind != ScrollInputKind.ContactMove && input.Kind != ScrollInputKind.FrameDelta) return false;
        for (int i = tail; i < head; i++)
        {
            ref ScrollInput slot = ref _ring[i & Mask];
            if (slot.Node == input.Node && slot.Kind == input.Kind)
            {
                slot = input;
                return true;
            }
        }
        return false;
    }

    /// <summary>Structural-vs-time-based classification (plan §3.3 point 3 / §2.1's <c>Reclamp</c> doc comment): a
    /// non-Immediate ScrollTo/ScrollBy is a time-based glide (Tick-only); every other kind is structural.</summary>
    internal static bool IsStructural(in ScrollInput item) => item.Kind switch
    {
        ScrollInputKind.Bind or ScrollInputKind.Unbind or ScrollInputKind.Park or ScrollInputKind.SetFrame or
        ScrollInputKind.SetZoom or ScrollInputKind.Chain or ScrollInputKind.Cancel or ScrollInputKind.ThumbSet or
        ScrollInputKind.Restore or ScrollInputKind.AnchorShift => true,
        ScrollInputKind.ScrollTo or ScrollInputKind.ScrollBy => (item.Flags & (byte)ScrollInputFlags.Immediate) != 0,
        _ => false,
    };

    /// <summary>Drain everything, in FIFO (posted) order, into <paramref name="buffer"/> — used by
    /// <see cref="ScrollKernel.Tick"/>, which processes ALL pending kinds. Returns the count written (capped at
    /// <paramref name="buffer"/>'s length, which callers size to <see cref="Capacity"/> so this never truncates in
    /// practice). Consumer-thread-only (matches <see cref="Tick"/>'s single-caller-at-a-time contract).</summary>
    internal int DrainAll(Span<ScrollInput> buffer)
    {
        int tail = _tail;
        int head = Volatile.Read(ref _head);
        int n = head - tail;
        if (n > buffer.Length) n = buffer.Length;
        for (int k = 0; k < n; k++) buffer[k] = _ring[(tail + k) & Mask];
        Volatile.Write(ref _tail, tail + n);
        return n;
    }

    /// <summary>Drain ONLY structural commands (see <see cref="IsStructural"/>) into <paramref name="outStructural"/>,
    /// leaving every non-structural command (a live glide/drag sample) in the ring, in its original relative order,
    /// for the next <see cref="ScrollKernel.Tick"/> to consume — used by <see cref="ScrollKernel.Reclamp"/>. This
    /// compacts the ring in place (an O(pending) scan bounded by <see cref="Capacity"/>, no allocation). UI-thread
    /// contract: must not race a concurrent <see cref="Post"/> from another thread (see the type doc remark).</summary>
    internal int DrainStructural(Span<ScrollInput> outStructural)
    {
        int tail = _tail;
        int head = _head;
        int outCount = 0;
        int w = tail;
        for (int i = tail; i < head; i++)
        {
            ScrollInput item = _ring[i & Mask];
            if (IsStructural(in item))
            {
                if (outCount < outStructural.Length) outStructural[outCount] = item;
                outCount++;
            }
            else
            {
                if (w != i) _ring[w & Mask] = item;
                w++;
            }
        }
        _tail = tail; // unchanged — kept items still start at the same logical position
        Volatile.Write(ref _head, w);
        return outCount;
    }

    /// <summary>Single-item drain (FIFO order) — a lighter-weight alternative to <see cref="DrainAll"/> for callers
    /// that want to process one command at a time without a scratch buffer.</summary>
    internal bool TryDrain(out ScrollInput input)
    {
        int tail = _tail;
        int head = Volatile.Read(ref _head);
        if (tail == head) { input = default; return false; }
        input = _ring[tail & Mask];
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }
}
