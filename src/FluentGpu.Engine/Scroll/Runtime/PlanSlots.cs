using System;
using System.Threading;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Runtime;

/// <summary>Identity of one scroll viewport across the UI/render seam: the viewport's scene node index plus the
/// node's generation (the engine's <c>Handle = {index, gen}</c> convention), so a slot left behind by an unmounted
/// viewport can never be read as the plan of a new viewport that reused the same node index.</summary>
public readonly record struct ScrollViewportId(int Node, uint Gen)
{
    /// <summary>The never-allocated id (<c>Node = -1</c>).</summary>
    public static readonly ScrollViewportId None = new(-1, 0);

    public bool IsNone => Node < 0;
}

/// <summary>
/// The lock-free plan seam between the UI thread and the render thread (scroll-rework design §2/§3 "PlanSlot"): a
/// fixed table of <see cref="Capacity"/> per-viewport <see cref="ScrollPlan"/> slots, each guarded by its own
/// seqlock. The UI thread is the ONLY writer (<see cref="Allocate"/>/<see cref="Release"/>/<see cref="Write"/>/
/// <see cref="Shift"/>); any number of readers (<see cref="ScrollPoser"/> on the render thread, the UI-side
/// <see cref="ScrollHandle"/>/<see cref="Virtualizer"/>) call <see cref="TryRead"/> and get a never-torn copy.
///
/// <para>THREADING CONTRACT. Writer side: every mutation of a slot bumps its sequence to odd, writes the payload,
/// bumps it back to even (release). Reader side: spin while odd, copy the payload, re-read the sequence, retry if it
/// moved (<see cref="ScrollPlan"/> is a ~600-byte POD, so a torn copy is possible and is exactly what the retry
/// discards). A slot is looked up by <see cref="ScrollViewportId"/> with a bounded linear scan over the live
/// high-water mark (≤64 key compares — no dictionary, no allocation, no lock). <see cref="Epoch"/> is a global
/// monotonic write counter (one <c>Volatile.Write</c> per mutation) a render tick can compare against its last
/// seen value to skip re-evaluation; <see cref="OnWritten"/> is the wake hook the host connects to the render
/// thread's wake so a wheel notch reaches a posed pixel without waiting for a UI frame (design §1 #3).</para>
///
/// <para>ZERO-ALLOC: the slot array is allocated once in the constructor; no method allocates afterwards.</para>
/// </summary>
public sealed class PlanSlots
{
    /// <summary>Slot capacity — the compositor overlay row headroom the owner raised from 32 to 64 (design §0).</summary>
    public const int Capacity = 64;

    private struct Slot
    {
        public int Seq;               // seqlock: even = stable, odd = write in flight
        public bool Live;
        public ScrollViewportId Key;
        public ScrollPlan Plan;
        public double FrameShift;     // sum of every Shift delta this binding's plan frame has taken (see FrameShiftOf)
    }

    private readonly Slot[] _slots = new Slot[Capacity];
    private int _highWater;           // slots [0, _highWater) may be live — bounds every scan
    private ulong _epoch;

    /// <summary>Global monotonically increasing write counter — bumped (with release semantics) on every
    /// <see cref="Allocate"/>/<see cref="Release"/>/<see cref="Write"/>/<see cref="Shift"/>. Safe to read from any
    /// thread.</summary>
    public ulong Epoch => Volatile.Read(ref _epoch);

    /// <summary>Wake hook: invoked (on the writer's thread, after the seqlock has been released) after every plan
    /// mutation. The host connects this to the render thread's wake so a replan is posed on the very next
    /// compositor tick. Auto-reset in the sense that it carries no payload/state — the render tick re-reads every
    /// slot it covers regardless of how many writes coalesced into one wake.</summary>
    public Action? OnWritten;

    /// <summary>How many slots every scan (<see cref="TryRead"/>, the writer's lookups) walks — one past the highest live
    /// slot. Diagnostics/gates.</summary>
    internal int ScanBound => Volatile.Read(ref _highWater);

    /// <summary>Number of live slots.</summary>
    public int LiveCount
    {
        get
        {
            int n = 0;
            int hw = Volatile.Read(ref _highWater);
            for (int i = 0; i < hw; i++) if (Volatile.Read(ref _slots[i].Live)) n++;
            return n;
        }
    }

    // ── writer side (UI thread) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Binds <paramref name="vp"/> to a free slot seeded with <paramref name="initial"/> (typically
    /// <see cref="ScrollPlan.Idle"/>). Re-allocating an already-live id just rewrites its plan. Returns false when
    /// all <see cref="Capacity"/> slots are live (the caller degrades to an unposed viewport — never throws on a
    /// hot path).</summary>
    public bool Allocate(ScrollViewportId vp, in ScrollPlan initial)
    {
        int i = IndexOfLocked(vp);
        if (i < 0)
        {
            i = FirstFreeIndex();
            if (i < 0) return false;
        }
        ref Slot s = ref _slots[i];
        bool rebind = s.Live && s.Key == vp;
        BeginWrite(ref s);
        s.Live = true;
        s.Key = vp;
        s.Plan = initial;
        if (!rebind) s.FrameShift = 0.0;
        EndWrite(ref s);
        if (i >= _highWater) Volatile.Write(ref _highWater, i + 1);
        Bump();
        return true;
    }

    /// <summary>Unbinds <paramref name="vp"/>; subsequent <see cref="TryRead"/> calls for it return false. No-op for
    /// an unknown id.</summary>
    public void Release(ScrollViewportId vp)
    {
        int i = IndexOfLocked(vp);
        if (i < 0) return;
        ref Slot s = ref _slots[i];
        BeginWrite(ref s);
        s.Live = false;
        s.Key = ScrollViewportId.None;
        EndWrite(ref s);
        // Every scan (the render tick's TryRead per covered viewport, the writer's lookups) walks [0, _highWater): pull
        // the bound back to one past the highest slot still live, so a page that once bound many scrollers does not
        // leave every later tick scanning its dead slots. Freed slots are reused lowest-first (FirstFreeIndex), which
        // keeps the live set packed at the bottom. Only the bound moves — no live slot is relocated, so a concurrent
        // reader can never miss one (a reader that loaded the old, larger bound just scans a few dead slots).
        if (i == _highWater - 1)
        {
            int hw = i;
            while (hw > 0 && !_slots[hw - 1].Live) hw--;
            Volatile.Write(ref _highWater, hw);
        }
        Bump();
    }

    /// <summary>Publishes a fresh plan for <paramref name="vp"/>. Returns false (nothing written) if the id is not
    /// live.</summary>
    public bool Write(ScrollViewportId vp, in ScrollPlan plan)
    {
        int i = IndexOfLocked(vp);
        if (i < 0) return false;
        ref Slot s = ref _slots[i];
        BeginWrite(ref s);
        s.Plan = plan;
        EndWrite(ref s);
        Bump();
        return true;
    }

    /// <summary>Shifts <paramref name="vp"/>'s live plan's coordinate frame by <paramref name="delta"/>
    /// (<see cref="ScrollPlan.Shifted"/>) — the ONE way a measured-extent correction above the anchor reaches an
    /// in-flight plan, applied in the same call the virtualizer discovers it (design §1 #5). Returns false if the id
    /// is not live. A zero delta is a no-op (no epoch bump, no wake).</summary>
    public bool Shift(ScrollViewportId vp, double delta)
    {
        int i = IndexOfLocked(vp);
        if (i < 0) return false;
        if (delta == 0.0) return true;
        ref Slot s = ref _slots[i];
        ScrollPlan shifted = s.Plan.Shifted(delta);   // single writer: a plain read of our own last write is safe
        double frameShift = s.FrameShift + delta;
        BeginWrite(ref s);
        s.Plan = shifted;
        s.FrameShift = frameShift;
        EndWrite(ref s);
        Bump();
        return true;
    }

    /// <summary>True when <paramref name="vp"/> is currently bound.</summary>
    public bool IsLive(ScrollViewportId vp) => IndexOfLocked(vp) >= 0;

    /// <summary>WRITER side (UI thread): the cumulative coordinate-frame shift <paramref name="vp"/>'s plan has taken
    /// (the sum of every <see cref="Shift"/> delta since the binding was allocated), 0 when unbound. The coverage the UI
    /// thread publishes records it (<see cref="ScrollCoverageRow.FrameShift"/>) — its content was laid out in that frame —
    /// so a poser holding a coverage OLDER than the plan's latest shift poses in the coverage's own frame
    /// (<see cref="ScrollPoser.Tick"/>).</summary>
    public double FrameShiftOf(ScrollViewportId vp)
    {
        int i = IndexOfLocked(vp);
        return i < 0 ? 0.0 : _slots[i].FrameShift;
    }

    // ── reader side (any thread) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="vp"/>'s current plan without ever observing a torn write. Returns false when
    /// the id is not bound (or was released between the caller's last frame and now). Zero allocation.</summary>
    public bool TryRead(ScrollViewportId vp, out ScrollPlan plan) => TryRead(vp, out plan, out _);

    /// <summary>As <see cref="TryRead(ScrollViewportId, out ScrollPlan)"/>, plus the slot's cumulative frame shift
    /// (<see cref="FrameShiftOf"/>) read under the SAME seqlock — a plan and the frame it is expressed in are never torn
    /// apart.</summary>
    public bool TryRead(ScrollViewportId vp, out ScrollPlan plan, out double frameShift)
    {
        int hw = Volatile.Read(ref _highWater);
        for (int i = 0; i < hw; i++)
        {
            // Key first (a never-torn 12-byte read under the same seqlock), the ~600-byte plan copy only for the match:
            // a scan over N live viewports costs N key reads, not N plan copies.
            if (!TryReadKey(i, out bool liveKey, out ScrollViewportId k) || !liveKey || k != vp) continue;
            if (TryReadSlot(i, out bool live, out ScrollViewportId key, out plan, out frameShift) && live && key == vp)
                return true;
        }
        plan = default;
        frameShift = 0.0;
        return false;
    }

    private bool TryReadKey(int i, out bool live, out ScrollViewportId key)
    {
        ref Slot s = ref _slots[i];
        int s1, s2;
        int spins = 0;
        do
        {
            s1 = Volatile.Read(ref s.Seq);
            while ((s1 & 1) != 0)
            {
                if (++spins > 1_000_000) { live = false; key = ScrollViewportId.None; return false; }
                Thread.SpinWait(1);
                s1 = Volatile.Read(ref s.Seq);
            }
            Thread.MemoryBarrier();
            live = s.Live;
            key = s.Key;
            Thread.MemoryBarrier();
            s2 = Volatile.Read(ref s.Seq);
        } while (s1 != s2);
        return true;
    }

    private bool TryReadSlot(int i, out bool live, out ScrollViewportId key, out ScrollPlan plan, out double frameShift)
    {
        ref Slot s = ref _slots[i];
        int s1, s2;
        int spins = 0;
        do
        {
            s1 = Volatile.Read(ref s.Seq);
            while ((s1 & 1) != 0)
            {
                if (++spins > 1_000_000) { live = false; key = ScrollViewportId.None; plan = default; frameShift = 0.0; return false; }
                Thread.SpinWait(1);
                s1 = Volatile.Read(ref s.Seq);
            }
            Thread.MemoryBarrier();
            live = s.Live;
            key = s.Key;
            plan = s.Plan;
            frameShift = s.FrameShift;
            Thread.MemoryBarrier();
            s2 = Volatile.Read(ref s.Seq);
        } while (s1 != s2);
        return true;
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────────────────────

    private int IndexOfLocked(ScrollViewportId vp)
    {
        if (vp.IsNone) return -1;
        int hw = _highWater;
        for (int i = 0; i < hw; i++)
        {
            ref Slot s = ref _slots[i];
            if (s.Live && s.Key == vp) return i;
        }
        return -1;
    }

    private int FirstFreeIndex()
    {
        for (int i = 0; i < Capacity; i++) if (!_slots[i].Live) return i;
        return -1;
    }

    private static void BeginWrite(ref Slot s)
    {
        Volatile.Write(ref s.Seq, s.Seq + 1);   // odd
        Thread.MemoryBarrier();
    }

    private static void EndWrite(ref Slot s)
    {
        Thread.MemoryBarrier();
        Volatile.Write(ref s.Seq, s.Seq + 1);   // even again
    }

    private void Bump()
    {
        Volatile.Write(ref _epoch, _epoch + 1);
        OnWritten?.Invoke();
    }
}
