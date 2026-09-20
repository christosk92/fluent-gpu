using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Scroll;

/// <summary>The scroll v3 kernel — a compact POOL of <see cref="ScrollBody"/> slots driven by ONE
/// <see cref="ScrollCommandPort"/> intake and ONE <see cref="IScrollSink"/> outlet (plan §2). Nothing here
/// references <c>SceneStore</c>/<c>NodeHandle</c>/<c>RenderContext</c> — that is what makes it tickable from a
/// non-UI thread (the render-thread fling lease, §6).
///
/// <para><b>Sparse by value, dense only by lookup.</b> The body storage used to be ONE <c>ScrollBody[]</c> indexed
/// directly by scene node index, grown to the highest node index that ever carried a viewport. <see cref="ScrollBody"/>
/// is 376 B, so on the 32 768-node scene the native ARM64 tour reached that slab alone was 12 320 792 B — plus four
/// more node-indexed side columns (<c>_activeList</c>, <c>_inActive</c>, <c>_touchedNodes</c>, <c>_touchedStamp</c>:
/// 12 746 752 B = 12.2 MiB in total) for a workload that has ~27 live scroll viewports. It is now a bounded SLOT
/// POOL plus a 4-byte node→slot lookup (<see cref="_slotByNode"/>): every other array in this class is sized to the
/// slot pool, i.e. to the number of viewports that have ever been simultaneously bound, never to the scene's node
/// high-water. Same arithmetic at 32 768 nodes and 32 viewports: 143 776 B (0.14 MiB) — a 98.9% cut.
/// <c>gate.kernel.body-sparse</c> asserts the backing is O(viewports) and <c>gate.kernel.slot-reuse</c> asserts an
/// unbound slot comes back instead of growing the pool.</para>
///
/// <para><b>Who may size it, and when.</b> Both the pool and the node column grow by doubling, and ONLY on
/// <see cref="ScrollInputKind.Bind"/> — the publisher-side event of a viewport mounting. Nothing in the integration
/// path (<see cref="Tick"/>'s active-body loop, <see cref="Reclamp"/>'s edge resolution, <see cref="MarkActive"/>,
/// <see cref="MarkTouched"/>, <see cref="EmitTouched"/>) can allocate: <c>_activeList</c>/<c>_touchedSlots</c> are
/// sized to the pool and each slot can enter either list at most once per pass (<c>_inActive</c> / the touched stamp
/// are what make that true), so both stay in bounds by construction — <c>gate.kernel.alloc-zero-tick</c>.</para>
///
/// <para><b>Slot release is deferred to the end of the pass.</b> <see cref="ScrollInputKind.Unbind"/> clears the
/// node's lookup entry immediately (so the body reads as gone at once, exactly as before) but parks the slot on
/// <see cref="_pendingFree"/> instead of returning it to the free list. The slot only becomes reusable after
/// <see cref="EmitTouched"/> has skipped it and <see cref="CompactActiveList"/> has dropped it, so a Bind arriving
/// LATER IN THE SAME PASS can never be handed a slot that this pass's touched list or active list still names. That
/// is what keeps "one sink write per body per pass" true across a mount/unmount in one frame.</para>
///
/// <para>Node indices remain the kernel's external vocabulary: the port, <see cref="IScrollSink.Apply"/>,
/// <see cref="TryGetBody"/>/<see cref="TryLease"/>/<see cref="Return"/>, and <see cref="ScrollBody.ChainParent"/>/
/// <see cref="ScrollBody.LastAbsorbed"/> are all node-keyed. Slots are strictly internal, and
/// <see cref="ScrollBody.Node"/> is the back-reference emission reads.</para>
///
/// <para><b>No body ever reaches the render thread.</b> A body's result is projected into the scene by
/// <c>SceneScrollSink.Apply</c> — <c>ScrollState</c> (a <c>ColdSlab</c> side-table on <c>SceneStore</c>) plus the
/// content child's <c>LocalTransform</c> — and the seam copies THAT (a sparse <c>SnapshotColumn&lt;ScrollState&gt;</c>,
/// itself a dictionary + slot pool). So this pool needs no snapshot mirror, and making it sparse changes nothing on
/// the render side. The reserved <see cref="TryLease"/>/<see cref="Return"/> fling-lease hands out a BY-VALUE copy of
/// one body, never the pool.</para></summary>
public sealed class ScrollKernel
{
    /// <summary>A contact sample farther than this from the frame clock is stamped in a foreign clock domain (a
    /// scripted or replayed stream): the resample then evaluates relative to the newest sample (see Tick).</summary>
    private const double ForeignClockToleranceSec = 0.5;
    /// <summary>Restore is a goal: clamp-and-apply every Reclamp until the extent can hold the saved offset, or
    /// this many retries elapse (~3s at 60 Hz). User/programmatic input cancels sooner.</summary>
    public const byte RestoreMaxRetries = 180;
    private readonly IScrollSink _sink;
    private readonly ScrollFeel _feel;

    // ── Sparse body storage: a slot pool + one 4-byte-per-node lookup ─────────────────────────────────────────
    /// <summary>node index → slot + 1 (0 = this node has no body). The ONE array in this class sized by node index,
    /// at 4 B/node; every other array below is sized to the slot pool.</summary>
    private int[] _slotByNode = [];
    /// <summary>The pool. Packed by slot, NOT by node — <c>_bodies[slot].Node</c> is the back-reference.</summary>
    private ScrollBody[] _bodies;
    /// <summary>Reclaimed slots, LIFO (so a mount/unmount cycle reuses the slot it just released).</summary>
    private int[] _freeSlots;
    private int _freeCount;
    /// <summary>Slots unbound during the CURRENT pass, released into <see cref="_freeSlots"/> at the end of it —
    /// see the type doc's "Slot release is deferred" paragraph.</summary>
    private int[] _pendingFree;
    private int _pendingFreeCount;
    /// <summary>High-water of slots ever handed out (live + free) — the used prefix of the pool, and the bound of
    /// every whole-pool scan (<see cref="ResolveRestores"/>).</summary>
    private int _slotHi;
    private int _boundCount;

    private int[] _activeList;   // slots
    private bool[] _inActive;    // by slot
    private int _activeCount;
    private int _wakeActiveCount;   // ActiveCount minus Parked slots — see WakeActiveCount

    // Per-call (Tick or Reclamp) "touched" tracking, stamp-based so it never needs an O(pool) clear.
    private int[] _touchedSlots;
    private int[] _touchedStamp;   // by slot
    private int _touchedCount;
    private int _stamp;

    private int _restorePendingCount;

    private readonly ScrollInput[] _drainScratch;
    private readonly ScrollInput[] _structuralScratch;
    private readonly double[] _histT;
    private readonly float[] _histX;

    public ScrollCommandPort Port { get; }
    public ScrollFrameSummary Summary { get; private set; }
    public int ActiveCount => _activeCount;

    /// <summary>Wake-purposed subset of <see cref="ActiveCount"/>: active slots minus <see cref="ScrollBody.Parked"/>
    /// ones. A parked body stays in <c>_activeList</c> by design (<see cref="CompactActiveList"/>'s <c>keep</c>
    /// clause: <c>b.Parked</c> alone keeps it resident so it resumes cleanly when unparked) but the per-frame tick
    /// loop skips parked bodies outright (<see cref="Tick"/>'s <c>if (!b.Bound || b.Parked) continue;</c>), so a
    /// parked body never does real work and must not, by itself, justify waking the render loop. Computed once per
    /// <see cref="CompactActiveList"/> pass (already an O(_activeCount) scan) — no extra allocation, no extra scan.
    /// <see cref="ActiveCount"/> itself is left untouched: other callers (diagnostics, capacity gates) still want
    /// the raw resident count including parked bodies.</summary>
    public int WakeActiveCount => _wakeActiveCount;

    public ScrollKernelDiag Diag;

    /// <param name="initialCapacity">Initial size of the VIEWPORT SLOT POOL — not a node capacity (the node lookup
    /// grows on demand from the first Bind). 16 covers a Wavee page's ~27 live viewports after one doubling.</param>
    public ScrollKernel(IScrollSink sink, in ScrollFeel feel, int initialCapacity = 16)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _feel = feel;
        int cap = Math.Max(4, initialCapacity);
        _bodies = new ScrollBody[cap];
        _freeSlots = new int[cap];
        _pendingFree = new int[cap];
        _activeList = new int[cap];
        _inActive = new bool[cap];
        _touchedSlots = new int[cap];
        _touchedStamp = new int[cap];
        Port = new ScrollCommandPort();
        _drainScratch = new ScrollInput[ScrollCommandPort.Capacity];
        _structuralScratch = new ScrollInput[ScrollCommandPort.Capacity];
        _histT = new double[5];
        _histX = new float[5];
    }

    // ── Storage census (diagnostics + gates; always on, no switch) ────────────────────────────────────────────

    /// <summary>Bodies currently bound — the live viewport count.</summary>
    internal int BoundCount => _boundCount;

    /// <summary>Slots the pool can hold. O(viewports ever simultaneously bound), never O(scene capacity) —
    /// <c>gate.kernel.body-sparse</c>.</summary>
    internal int BodySlotCapacity => _bodies.Length;

    /// <summary>The used prefix of the pool: slots ever handed out minus slots reclaimed. Equals
    /// <see cref="BoundCount"/> plus the slots unbound in the current pass but not yet released.</summary>
    internal int BodySlotsInUse => _slotHi - _freeCount;

    /// <summary>Length of the node→slot lookup — the highest bound node index, rounded up by doubling.</summary>
    internal int NodeColumnLength => _slotByNode.Length;

    /// <summary>Bytes this kernel's body storage holds: the slot pool, the pool-sized side arrays, and the one
    /// 4-byte-per-node lookup. The gate compares it against <see cref="DenseBodyStorageBytes"/>.</summary>
    internal long BodyStorageBytes
        => (long)_bodies.Length * Unsafe.SizeOf<ScrollBody>()
           + (long)_slotByNode.Length * sizeof(int)
           + (long)_freeSlots.Length * sizeof(int)
           + (long)_pendingFree.Length * sizeof(int)
           + (long)_activeList.Length * sizeof(int)
           + _inActive.Length
           + (long)_touchedSlots.Length * sizeof(int)
           + (long)_touchedStamp.Length * sizeof(int);

    /// <summary>Bytes one pooled body costs (diagnostics/gates).</summary>
    internal static int BodyBytes => Unsafe.SizeOf<ScrollBody>();

    /// <summary>Bytes the DELETED node-indexed storage would cost for <paramref name="nodeCapacity"/> slots: the
    /// body slab plus the four node-indexed side columns it dragged along.</summary>
    internal static long DenseBodyStorageBytes(int nodeCapacity)
        => (long)nodeCapacity * Unsafe.SizeOf<ScrollBody>()   // _bodies, indexed by node
           + (long)nodeCapacity * sizeof(int)                  // _activeList
           + nodeCapacity                                      // _inActive
           + (long)nodeCapacity * sizeof(int)                  // _touchedNodes
           + (long)nodeCapacity * sizeof(int);                 // _touchedStamp

    // ── Public reads ──────────────────────────────────────────────────────────────────────────────────────────

    public bool TryGetBody(int node, out ScrollBody snapshot)
    {
        if (TryGetSlot(node, out int slot)) { snapshot = _bodies[slot]; return true; }
        snapshot = default;
        return false;
    }

    /// <summary>§6 render-thread fling-lease hand-off (reserved — Phase 6/WP-L wires the actual seam). Hands out a
    /// by-value snapshot for a body currently Ballistic/Driven; bumps <see cref="ScrollBody.LeaseSeq"/> so a stale
    /// <see cref="Return"/> can be detected and ignored.</summary>
    public bool TryLease(int node, out ScrollBody body, out uint seq)
    {
        if (TryGetSlot(node, out int slot) &&
            (_bodies[slot].Activity == ScrollActivity.Ballistic || _bodies[slot].Activity == ScrollActivity.Driven))
        {
            ref var b = ref _bodies[slot];
            b.LeaseSeq++;
            body = b;
            seq = b.LeaseSeq;
            return true;
        }
        body = default;
        seq = 0;
        return false;
    }

    /// <summary>Hand a leased body back (reserved — Phase 6). A stale <paramref name="seq"/> (superseded by a newer
    /// lease or a UI-side revoke) is ignored.</summary>
    public void Return(int node, in ScrollBody body, uint seq)
    {
        if (!TryGetSlot(node, out int slot)) return;
        if (_bodies[slot].LeaseSeq != seq) return;
        _bodies[slot] = body;
        _bodies[slot].LeaseSeq = seq;
        // The pool's own bookkeeping is not the lessee's to overwrite: a leased copy carries whatever Node/Bound the
        // body had when it was handed out, and a Return must not be able to re-point this slot at another node.
        _bodies[slot].Node = node;
        _bodies[slot].Bound = true;
    }

    // ── Tick / Reclamp ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drains ALL pending inputs in posted order, integrates one time step, and calls
    /// <see cref="IScrollSink.Apply"/> exactly once per body that moved this call.</summary>
    public void Tick(in ScrollClock clock)
    {
        _stamp++;
        _touchedCount = 0;

        int n = Port.DrainAll(_drainScratch);
        for (int i = 0; i < n; i++) ProcessCommand(in _drainScratch[i]);

        for (int k = 0; k < _activeCount; k++)
        {
            int slot = _activeList[k];
            ref ScrollBody b = ref _bodies[slot];
            if (!b.Bound || b.Parked) continue;

            if (b.Activity == ScrollActivity.Drag)
            {
                float rawBefore = b.DragRaw;
                bool released = false;
                if (b.DragMode == 1)
                {
                    int count = CopyHistory(in b, _histT, _histX);
                    if (count > 0)
                    {
                        // Resample target = frame time − latency, in the SAMPLE clock. Real producers stamp samples on the
                        // frame clock's own domain (QPC), so FrameSec is used directly; a producer stamping in a foreign
                        // clock (a scripted/replayed stream) is detected by proximity and evaluated relative to its own
                        // newest sample instead — same latency, deterministic, never a domain-mismatch clamp.
                        double newestT = _histT[count - 1];
                        double frameRef = Math.Abs(clock.FrameSec - newestT) <= ForeignClockToleranceSec ? clock.FrameSec : newestT;
                        double tStar = frameRef - _feel.ResampleLatencyMs / 1000.0;
                        float resampled = ScrollPhysics.ResampleContact(_histT.AsSpan(0, count), _histX.AsSpan(0, count), count, tStar);
                        float delta = resampled - b.LastResampleX;
                        b.LastResampleX = resampled;
                        if (delta != 0f) ApplyDragDelta(slot, slot, delta);
                    }
                }
                else if (b.DragMode == 2)
                {
                    released = PaceStream(slot, in clock);
                }
                b = ref _bodies[slot];
                if (!released)
                {
                    // Live drag speed (signed, main axis) from this tick's raw advance — the result column the realize-ahead
                    // skew and text-motion softness read; it is NOT the fling seed (the impulse estimator owns that).
                    float dtV = clock.DtSec > 0f ? Math.Min(clock.DtSec, 0.034f) : clock.RefreshSec;
                    b.Velocity = dtV > 0f ? (b.DragRaw - rawBefore) / dtV : 0f;
                    MarkTouched(slot);
                    continue;
                }
                if (b.Activity != ScrollActivity.Ballistic) { MarkTouched(slot); continue; }
                // The inferred fling coasts on this same tick: no zero frame between the last paced frame and the first
                // coast frame.
            }

            float posBefore = b.PositionMain;
            ScrollBody.Advance(ref b, in clock, in _feel);
            b.LastAdvanceStamp = _stamp;
            // An inferred release keeps the paced resampler's baseline on the displayed position, so a late packet
            // resumes the stream without applying its travel twice.
            if (b.InferredRelease) b.LastResampleX += b.PositionMain - posBefore;
            MarkTouched(slot);
        }

        EmitTouched(ScrollWriteSource.Tick);
        CompactActiveList();
        FlushPendingFree();
        UpdateSummary(in clock, timed: true);
        UpdateDiag();
    }

    /// <summary>Drains STRUCTURAL inputs only (Bind/Unbind/Park/SetFrame/SetZoom/Chain/Cancel/ThumbSet/Restore/
    /// AnchorShift/ScrollTo|Immediate/ScrollBy|Immediate — <see cref="ScrollCommandPort.IsStructural"/>), re-clamps,
    /// resolves any pending <see cref="ScrollBody.EdgeHitPending"/>, and calls <see cref="IScrollSink.Apply"/> once
    /// per touched body. No time advance.</summary>
    public void Reclamp()
    {
        _stamp++;
        _touchedCount = 0;

        int n = Port.DrainStructural(_structuralScratch);
        for (int i = 0; i < n; i++) ProcessCommand(in _structuralScratch[i]);

        if (_restorePendingCount > 0) ResolveRestores();

        for (int k = 0; k < _activeCount; k++)
        {
            int slot = _activeList[k];
            ref ScrollBody b = ref _bodies[slot];
            if (b.Bound && b.EdgeHitPending) ResolveEdge(slot);
        }

        EmitTouched(ScrollWriteSource.Reclamp);
        CompactActiveList();
        FlushPendingFree();
        UpdateSummary(default, timed: false);
    }

    // ── Command dispatch (shared by Tick's full drain and Reclamp's structural-only drain) ──────────────────────

    private void ProcessCommand(in ScrollInput cmd)
    {
        switch (cmd.Kind)
        {
            case ScrollInputKind.Bind: BindNode(cmd.Node); break;
            case ScrollInputKind.Unbind: UnbindNode(cmd.Node); break;
            case ScrollInputKind.Park:
                if (TryGetSlot(cmd.Node, out int pidx))
                {
                    ref ScrollBody pb = ref _bodies[pidx];
                    pb.Parked = (cmd.Flags & (byte)ScrollInputFlags.Immediate) != 0;
                    // A parked body is never ticked, so whatever motion it carried would otherwise stay "live" for
                    // as long as it is parked (a page parked mid-fling kept AnyLiveMotion true for 41 s). Settle it
                    // on the way in; unparking finds an idle body.
                    if (pb.Parked) SettleParked(ref pb);
                    MarkTouched(pidx);
                }
                break;
            case ScrollInputKind.SetFrame: ApplySetFrame(in cmd); break;
            case ScrollInputKind.SetZoom: ApplySetZoom(in cmd); break;
            case ScrollInputKind.Chain:
                // cmd.I is the ancestor's NODE index — ChainParent stays node-keyed (the router speaks nodes).
                if (TryGetSlot(cmd.Node, out int cidx)) _bodies[cidx].ChainParent = cmd.I;
                break;
            case ScrollInputKind.Cancel: ApplyCancel(cmd.Node); break;
            case ScrollInputKind.ContactBegin: ApplyContactBegin(in cmd); break;
            case ScrollInputKind.ContactMove: ApplyContactMove(in cmd); break;
            case ScrollInputKind.ContactEnd: ApplyContactEnd(in cmd); break;
            case ScrollInputKind.FrameDelta: ApplyFrameDelta(in cmd); break;
            case ScrollInputKind.WheelNotch: ApplyWheelNotch(in cmd); break;
            case ScrollInputKind.ScrollTo: ApplyScrollTo(in cmd); break;
            case ScrollInputKind.ScrollBy: ApplyScrollBy(in cmd); break;
            case ScrollInputKind.SetVelocity: ApplySetVelocity(in cmd); break;
            case ScrollInputKind.ThumbSet: ApplyThumbSet(in cmd); break;
            case ScrollInputKind.Restore: ApplyRestore(in cmd); break;
            case ScrollInputKind.AnchorShift: ApplyAnchorShift(in cmd); break;
            case ScrollInputKind.ImpulseSample: ApplyImpulseSample(in cmd); break;
        }
    }

    private void BindNode(int node)
    {
        if (node < 0) return;
        EnsureNodeColumn(node);
        if (_slotByNode[node] != 0) return; // idempotent
        int slot = AllocSlot();
        _slotByNode[node] = slot + 1;
        ref ScrollBody b = ref _bodies[slot];
        b = default;
        b.Node = node;
        b.Bound = true;
        b.ChainParent = -1;
        b.LastAbsorbed = -1;
        b.Zoom = 1f;
        // A REUSED slot must not inherit the previous tenant's list membership. (_inActive is already false whenever
        // a slot reaches the free list — FlushPendingFree clears it — but a fresh slot is cleared here too so the
        // invariant holds at one place; the touched stamp is reset because 0 is never a live stamp.)
        _inActive[slot] = false;
        _touchedStamp[slot] = 0;
        _boundCount++;
    }

    private void UnbindNode(int node)
    {
        if (!TryGetSlot(node, out int slot)) return;
        _slotByNode[node] = 0;
        ref ScrollBody b = ref _bodies[slot];
        b.Bound = false;
        // An unbound body can never satisfy its restore, so stop counting it: otherwise _restorePendingCount stays
        // positive forever and every later Reclamp re-scans the pool for a body that no longer exists.
        if (b.RestorePending) { b.RestorePending = false; b.RestoreRetries = 0; _restorePendingCount--; }
        _boundCount--;
        // Deferred release (see the type doc): the slot is still named by this pass's active/touched lists. It is
        // NOT dropped from _activeList here — CompactActiveList does that off !Bound, which also clears _inActive.
        _pendingFree[_pendingFreeCount++] = slot;
    }

    private int AllocSlot()
    {
        int slot = _freeCount > 0 ? _freeSlots[--_freeCount] : _slotHi++;
        EnsureSlotCapacity(slot);
        return slot;
    }

    /// <summary>Return this pass's unbound slots to the free list. Runs AFTER <see cref="EmitTouched"/> (which
    /// skipped them) and <see cref="CompactActiveList"/> (which dropped them and cleared <c>_inActive</c>), so a
    /// slot handed out on the next pass cannot collide with a list entry from this one.</summary>
    private void FlushPendingFree()
    {
        for (int i = 0; i < _pendingFreeCount; i++)
        {
            int slot = _pendingFree[i];
            _inActive[slot] = false;
            _freeSlots[_freeCount++] = slot;
        }
        _pendingFreeCount = 0;
    }

    /// <summary>Grow the node→slot lookup to cover <paramref name="node"/>. Bind-only (publisher side).</summary>
    private void EnsureNodeColumn(int node)
    {
        if (node < _slotByNode.Length) return;
        int n = _slotByNode.Length == 0 ? 64 : _slotByNode.Length;
        while (n <= node) n *= 2;
        Array.Resize(ref _slotByNode, n);
    }

    /// <summary>Grow the slot pool AND every pool-sized side array together, so <see cref="MarkActive"/>/
    /// <see cref="MarkTouched"/> are in-bounds by construction and never have to grow anything themselves.
    /// Bind-only (publisher side) — nothing in the integration path calls this.</summary>
    private void EnsureSlotCapacity(int slot)
    {
        if (slot < _bodies.Length) return;
        int cap = _bodies.Length;
        while (cap <= slot) cap *= 2;
        Array.Resize(ref _bodies, cap);
        Array.Resize(ref _freeSlots, cap);
        Array.Resize(ref _pendingFree, cap);
        Array.Resize(ref _activeList, cap);
        Array.Resize(ref _inActive, cap);
        Array.Resize(ref _touchedSlots, cap);
        Array.Resize(ref _touchedStamp, cap);
    }

    /// <summary>node → pool slot. False ⇔ the node has no bound body (the old <c>_bodies[node].Bound</c> test).</summary>
    private bool TryGetSlot(int node, out int slot)
    {
        if ((uint)node < (uint)_slotByNode.Length)
        {
            int s = _slotByNode[node];
            if (s != 0) { slot = s - 1; return true; }
        }
        slot = -1;
        return false;
    }

    // ── Structural handlers ───────────────────────────────────────────────────────────────────────────────────

    private void ApplySetFrame(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        var spec = ScrollInput.UnpackFrame(in cmd);
        b.Frame = spec;
        if (spec.Zoom > 0f && b.Zoom <= 0f) b.Zoom = spec.Zoom;
        if (b.Zoom <= 0f) b.Zoom = 1f;
        ClampToFrame(ref b);
        // A programmatic request re-derives its target against the NEW extent. Content that grows AFTER the post (a
        // SizeMode.Reflow drawer animating 0→full, a virtualized list measuring late) would otherwise leave the
        // destination truncated forever, since Target was clamped at post time and nothing re-clamps it upward.
        // TWO shapes qualify:
        //   • a LIVE chase (Driven+Programmatic) — re-target in place; the chase is velocity-continuous by kernel
        //     contract, so re-deriving + MarkActive is the whole regrow;
        //   • a request the OLD extent already TRUNCATED (TargetRaw ≠ Target) whose chase has therefore already
        //     hard-stopped at the edge and settled to Idle — the common case, because a target beyond the extent
        //     reaches the clamp on the very first tick. It re-arms as the same Driven chase (the DrivenHalflifeMs/
        //     Zeta/Omega/SettleVel latched at post time survive the settle) and continues from where it stopped.
        // Idle is the only settled state that qualifies: a Drag/Ballistic body has moved on, and every takeover path
        // (Cancel/ContactBegin/ThumbSet/wheel notch) relatches TargetRaw to Target so a dead request cannot resurrect.
        bool liveChase = b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Programmatic) != 0;
        // The settled case additionally requires the body to still be PARKED at its truncated destination: that is
        // the signature of "stopped because of the clamp". If anything moved it since (a Restore, an AnchorShift,
        // any path that is not one of the relatching takeovers), the request is stale and must die rather than
        // glide the viewport somewhere the user has long left behind.
        bool truncatedPending = b.Activity == ScrollActivity.Idle && b.TargetRaw != b.Target
                                && MathF.Abs(b.PositionMain - b.Target) < 0.5f;
        if (liveChase || truncatedPending)
        {
            float zoomNow = b.Zoom > 0f ? b.Zoom : 1f;
            float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoomNow - b.Frame.ViewportMain);
            float retarget = Math.Clamp(b.TargetRaw, 0f, maxOff);
            if (liveChase || retarget != b.Target)
            {
                b.Target = retarget;
                if (!liveChase)
                {
                    b.Activity = ScrollActivity.Driven;
                    b.Flags = (b.Flags & ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing)) | ScrollActivityFlags.Programmatic;
                    ScrollBody.ClearWheelPlan(ref b);
                    b.Awake = false;
                }
                MarkActive(idx);
            }
        }
        if (b.RestorePending && TryApplyRestore(ref b)) _restorePendingCount--;
        MarkTouched(idx);
    }

    private void ApplySetZoom(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        float oldZoom = b.Zoom > 0f ? b.Zoom : 1f;
        float newZoom = cmd.A > 0f ? cmd.A : 1f;
        float focal = cmd.B;
        float pos = b.PositionMain;
        float newPos = (pos + focal) * (newZoom / oldZoom) - focal;
        b.Zoom = newZoom;
        SetOffsetMain(ref b, newPos);
        ClampToFrame(ref b);
        b.Activity = ScrollActivity.Idle;
        b.Velocity = 0f;
        b.Flags = ScrollActivityFlags.None;
        ScrollBody.ClearWheelPlan(ref b);
        b.TargetRaw = b.Target;   // zoom rewrites content space — the old raw request no longer means anything
        MarkTouched(idx);
    }

    private void ApplyCancel(int node)
    {
        if (!TryGetSlot(node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        b.Activity = ScrollActivity.Idle;
        b.Velocity = 0f;
        b.BandVelMain = 0f;
        b.BandX = 0f; b.BandY = 0f;
        b.Flags = ScrollActivityFlags.None;
        ScrollBody.ClearWheelPlan(ref b);
        b.TargetRaw = b.Target;   // the request is dead — never let it resurrect when the content next grows
        b.EdgeHitPending = false;
        b.EdgeOvershoot = 0f;
        b.InferredRelease = false;
        b.Awake = false;
        CancelRestore(ref b);
        MarkTouched(idx);
    }

    /// <summary>Park-time settle: a parked slot is skipped by <see cref="Tick"/>, so any motion it carries would never
    /// finish. Idle, no velocity, no band, no wheel/programmatic plan, no pending edge or drag state. A pending Restore
    /// is kept (it resolves on geometry, not on time).</summary>
    private static void SettleParked(ref ScrollBody b)
    {
        b.Activity = ScrollActivity.Idle;
        b.Velocity = 0f;
        b.BandVelMain = 0f;
        b.BandX = 0f; b.BandY = 0f;
        b.Flags = ScrollActivityFlags.None;
        ScrollBody.ClearWheelPlan(ref b);
        b.TargetRaw = b.Target;
        b.EdgeHitPending = false;
        b.EdgeOvershoot = 0f;
        b.SnapArmed = false;
        b.InferredRelease = false;
        b.DragMode = 0;
        b.ContactCount = 0;
        b.LastAbsorbed = -1;
        b.Awake = false;
    }

    private void ApplyThumbSet(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float target = Math.Clamp(cmd.A, 0f, maxOff);
        SetOffsetMain(ref b, target);
        b.Velocity = 0f;
        b.Activity = ScrollActivity.Idle;
        b.Flags = ScrollActivityFlags.None;
        ScrollBody.ClearWheelPlan(ref b);
        b.TargetRaw = b.Target;   // dragging the thumb wins over any pending programmatic request
        CancelRestore(ref b);
        MarkTouched(idx);
    }

    private void ApplyRestore(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        bool wasPending = b.RestorePending;
        b.RestoreX = cmd.A; b.RestoreY = cmd.B;
        b.RestorePending = true;
        b.RestoreRetries = 0;
        if (!wasPending) _restorePendingCount++;
        if (TryApplyRestore(ref b)) _restorePendingCount--;
        MarkTouched(idx);
    }

    /// <summary>Retry every latched restore. Scans the pool's used prefix (O(viewports ever bound), not O(scene
    /// capacity) as the node-indexed slab forced) — so the iteration order is SLOT order (bind order) rather than
    /// node order. Each restore resolves against its own body only, and the sink write is per node, so nothing here
    /// depends on the order; it is deterministic for a given command stream because slot allocation is.</summary>
    private void ResolveRestores()
    {
        for (int slot = 0; slot < _slotHi && _restorePendingCount > 0; slot++)
        {
            ref ScrollBody b = ref _bodies[slot];
            if (!b.Bound || !b.RestorePending) continue;
            if (TryApplyRestore(ref b))
            {
                _restorePendingCount--;
                MarkTouched(slot);
            }
        }
    }

    private static bool TryApplyRestore(ref ScrollBody b)
    {
        if (!b.RestorePending) return false;
        if (b.Frame.ViewportMain <= 0f) return false; // geometry not known yet — latch, retried each Reclamp
        float value = b.Horizontal ? b.RestoreX : b.RestoreY;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        SetOffsetMain(ref b, Math.Clamp(value, 0f, maxOff));
        ScrollBody.ClearWheelPlan(ref b);   // the offset was moved under any live wheel plan — its cadence is meaningless now
        // Goal, not event: land immediately at the best-effort clamp, but stay latched while the saved offset is
        // still past the current extent (content is still growing). Resolve when the extent can hold it, or when
        // the retry deadline fires so a permanently-short page cannot latch forever.
        if (maxOff >= value - 0.5f || b.RestoreRetries >= RestoreMaxRetries)
        {
            b.RestorePending = false;
            b.RestoreRetries = 0;
            return true;
        }
        if (b.RestoreRetries < byte.MaxValue) b.RestoreRetries++;
        return false;
    }

    private void CancelRestore(ref ScrollBody b)
    {
        if (!b.RestorePending) return;
        b.RestorePending = false;
        b.RestoreRetries = 0;
        _restorePendingCount--;
    }

    private void ApplyAnchorShift(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        float delta = cmd.A;
        SetOffsetMain(ref b, b.PositionMain + delta);
        b.DragAnchor += delta;
        b.DragRaw += delta;
        b.Target += delta;
        b.TargetRaw += delta;     // the whole content moved — the raw request travels with it (lockstep)
        if (b.RestorePending)
        {
            if (b.Horizontal) b.RestoreX += delta; else b.RestoreY += delta;
        }
        NoteStructural(ref b, delta);
        ClampToFrame(ref b);
        MarkTouched(idx);
    }

    private void ClampToFrame(ref ScrollBody b)
    {
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float pos = b.PositionMain;
        float clamped = Math.Clamp(pos, 0f, maxOff);
        if (clamped != pos)
        {
            SetOffsetMain(ref b, clamped);
            NoteStructural(ref b, clamped - pos);
        }
    }

    /// <summary>A position rebase that is not motion (an AnchorShift, a clamp correction after SetFrame): move the
    /// summary baseline with it so <see cref="UpdateSummary"/> does not report it as this tick's shift, and book it
    /// for <see cref="ScrollFrameSummary.MaxAbsStructuralDip"/>.</summary>
    private void NoteStructural(ref ScrollBody b, float delta)
    {
        b.SummaryMain += delta;
        float abs = MathF.Abs(delta);
        if (abs > _maxAbsStructuralDip) _maxAbsStructuralDip = abs;
    }

    // ── Drag (ContactBegin/Move/End, FrameDelta) ─────────────────────────────────────────────────────────────

    private void ApplyContactBegin(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        b.Activity = ScrollActivity.Drag;
        b.DragMode = 1;
        b.DragOrigin = cmd.A;
        b.DragAnchor = b.PositionMain;
        // Re-grab of a live stretch (mid-bounce or a held band): fold the band back into the raw origin through the
        // exact inverse so the stretch continues seamlessly under the finger instead of snapping to zero.
        b.DragRaw = b.PositionMain + ScrollPhysics.ExcessFromBand(b.BandMain, b.Frame.ViewportMain);
        b.LastResampleX = cmd.A;
        b.ContactCount = 0;
        b.InferredRelease = false;
        PushHistory(ref b, cmd.T, cmd.A);
        b.Impulse.Reset(cmd.A, cmd.T);
        b.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing | ScrollActivityFlags.Chained);
        ScrollBody.ClearWheelPlan(ref b);
        b.TargetRaw = b.Target;   // the finger took over — a pending programmatic request must not resurrect on growth
        b.BandVelMain = 0f;
        b.LastAbsorbed = -1;
        b.EdgeHitPending = false;
        b.EdgeOvershoot = 0f;
        b.NoOverscroll = false;   // a direct contact (touch/pen) always rubber-bands
        CancelRestore(ref b);
        MarkActive(idx);
        MarkTouched(idx);
    }

    private void ApplyContactMove(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        if (b.Activity != ScrollActivity.Drag || b.DragMode != 1) { ApplyContactBegin(in cmd); b = ref _bodies[idx]; }
        PushHistory(ref b, cmd.T, cmd.A);
        b.Impulse.Sample(cmd.A, cmd.T);
        // Position is recomputed once per Tick from the full history (see Tick's active-body loop) — no resample here.
    }

    private void ApplyContactEnd(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        if (b.Activity != ScrollActivity.Drag)
        {
            // The producer's End for a precise stream the kernel already released by inference (PaceStream): the
            // coast is live and seeded from the same samples this End would have used — nothing to do but drop the
            // resume latch so the next gesture starts fresh.
            if (b.InferredRelease) { b.InferredRelease = false; b.LastAbsorbed = -1; }
            return; // a stray End with no live drag — nothing to seed
        }

        // Only feed the raw finger sample into the contact-history/impulse tracker for a RESAMPLED (touch/pen)
        // drag — cmd.A is meaningless for a FrameDelta drag (the router passes 0/irrelevant there) and would
        // otherwise inject a bogus sample that corrupts the release-velocity estimate.
        if (b.DragMode == 1)
        {
            PushHistory(ref b, cmd.T, cmd.A);
            b.Impulse.Sample(cmd.A, cmd.T);

            // Under-sampled flick (Begin+End only, ≤2 samples): ResampleContact's 1-2-sample branches apply the
            // final sample verbatim — no separate "click" special-case needed.
            int count = CopyHistory(in b, _histT, _histX);
            if (count > 0)
            {
                float resampled = ScrollPhysics.ResampleContact(_histT.AsSpan(0, count), _histX.AsSpan(0, count), count, cmd.T);
                float delta = resampled - b.LastResampleX;
                b.LastResampleX = resampled;
                if (delta != 0f) ApplyDragDelta(idx, idx, delta);
            }
        }

        ReleaseDrag(idx, cmd.T, out _);
    }

    /// <summary>Release the drag at pool slot <paramref name="idx"/> at <paramref name="tRelease"/>: compute the
    /// release velocity, seed the fling on the body that absorbed the last delta (self or a chained ancestor), spring
    /// a live band home, or stop. Shared by the producer's <see cref="ScrollInputKind.ContactEnd"/> and by
    /// <see cref="PaceStream"/>'s inferred release. Returns the seed slot; <paramref name="seeded"/> is true when
    /// that body left as Ballistic.</summary>
    private int ReleaseDrag(int idx, double tRelease, out bool seeded)
    {
        ref ScrollBody b = ref _bodies[idx];
        // A precise stream long enough to measure releases on its raw packet totals; anything shorter (a flick of
        // one or two packets) and every touch/pen drag keep the impulse estimator.
        float v;
        if (b.DragMode == 2 && TryRawStreamVelocity(in b, tRelease, out float vRaw)) v = vRaw;
        else
        {
            b.Impulse.ComputeReleaseVelocity(tRelease);
            v = b.Impulse.Velocity;
        }
        b.LastReleaseVelocity = v;

        // A paced precise stream (DragMode 2) shows its packets one latency late. Landing without a fling, it owes
        // the residual it has not displayed yet: apply it so the resting offset is exactly the packets' total. A
        // fling starts from the displayed position instead — the coast covers the residual, and applying it here
        // would show one frame at 2–3× the stream's step at release.
        if (b.DragMode == 2 && MathF.Abs(v) < _feel.FlingSeedGate && b.ContactCount > 0)
        {
            float newest = NewestX(in b);
            float residual = newest - b.LastResampleX;
            b.LastResampleX = newest;
            if (residual != 0f) ApplyDragDelta(idx, idx, residual);
        }

        // LastAbsorbed is a NODE index (the chain hand-off speaks nodes). Resolve it back to a slot; a chained
        // ancestor that unbound between the hand-off and the lift falls back to seeding this body.
        int absorbed = _bodies[idx].LastAbsorbed;
        int seedSlot = absorbed >= 0 && TryGetSlot(absorbed, out int aslot) ? aslot : idx;
        ref ScrollBody seed = ref _bodies[seedSlot];
        seed.LastReleaseVelocity = v;
        seed.NoOverscroll = _bodies[idx].NoOverscroll;   // the fling inherits the gesture's producer (ResolveEdge reads it)
        float band = seed.BandMain;
        seeded = band == 0f && MathF.Abs(v) >= _feel.FlingSeedGate;

        if (ScrollTrace.CompiledIn && ScrollTrace.Enabled)
        {
            // bug-B/A3 measurement: the computed release v + the FlingSeedGate verdict, in the SAME row — settles
            // "did the estimator return 0" definitively (§8.5 of the bug-B handoff) instead of inferring it from an
            // absent OffsetWrite(Activity=Ballistic) run. aux/qpc: see ApplyImpulseSample's note — 0 here too.
            int flags = (seed.Horizontal ? 1 : 0) | (seeded ? 2 : 0) | (idx != seedSlot ? 4 : 0);
            float vx = seed.Horizontal ? v : 0f, vy = seed.Horizontal ? 0f : v;
            ScrollTrace.Release(flags, vx, vy, v, band, 0L);
        }

        if (seeded)
        {
            seed.Activity = ScrollActivity.Ballistic;
            seed.Velocity = Math.Clamp(v, -_feel.FlingMax, _feel.FlingMax);
            seed.Awake = false;
            seed.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Chained);
            ScrollBody.ClearWheelPlan(ref seed);
            SnapRetargetOnEntry(ref seed);
            MarkActive(seedSlot);
        }
        else if (MathF.Abs(band) > 0.0001f)
        {
            // A live stretch springs home; the release velocity seeds the bounce ONLY when it is still pushing INTO the
            // edge (sign match) and is above the settle floor — a slow lift with no band must stop dead, never wobble.
            float bandv = seed.BandVelMain;
            if (MathF.Abs(v) >= _feel.FlingSettleVel && MathF.Sign(v) == MathF.Sign(band))
                ScrollPhysics.SeedFromEdgeMomentum(ref bandv, v, seed.Frame.ViewportMain);
            seed.BandVelMain = bandv;
            seed.Activity = ScrollActivity.Idle;
            seed.Flags |= ScrollActivityFlags.Bouncing;
            seed.Velocity = 0f;
            seed.Awake = false;
            MarkActive(seedSlot);
        }
        else
        {
            seed.Activity = ScrollActivity.Idle;
            seed.Velocity = 0f;
        }

        if (idx != seedSlot)
        {
            ref ScrollBody finger = ref _bodies[idx];
            finger.Activity = ScrollActivity.Idle;
            finger.Velocity = 0f;
            MarkTouched(idx);
        }
        _bodies[idx].LastAbsorbed = -1;
        MarkTouched(seedSlot);
        return seedSlot;
    }

    /// <summary>One paced step of a precise-stream drag (<see cref="ScrollBody.DragMode"/> 2 — FrameDelta packets from
    /// DirectManipulation / the hi-res wheel fallback). The packets' cumulative positions are resampled at
    /// <c>frame − PacedLatencyS(mean packet interval)</c> (<see cref="ScrollPhysics.ResamplePaced"/>), so packets that
    /// arrive off the frame phase (a 60 Hz stream drawn at 120 Hz) yield one even step per frame instead of a zero
    /// frame followed by a double. When the resample instant runs more than <see cref="ScrollFeel.DragExtrapolateMaxMs"/>
    /// past the newest sample, the stream has stopped: the release is inferred at the newest sample's time
    /// (<see cref="ReleaseDrag"/>, the same estimate the producer's late End would give) so the coast starts on this
    /// tick rather than after the 1–6 zero frames the End takes to arrive. Returns true when the drag was released.</summary>
    private bool PaceStream(int slot, in ScrollClock clock)
    {
        ref ScrollBody b = ref _bodies[slot];
        int count = CopyHistory(in b, _histT, _histX);
        if (count == 0) return false;
        double newestT = _histT[count - 1];
        double frameRef = Math.Abs(clock.FrameSec - newestT) <= ForeignClockToleranceSec ? clock.FrameSec : newestT;
        double interval = count >= 2 ? (newestT - _histT[0]) / (count - 1) : 0.0;
        double tStar = frameRef - ScrollPhysics.PacedLatencyS(interval);
        double extrapolateMax = _feel.DragExtrapolateMaxMs / 1000.0;
        if (tStar > newestT + extrapolateMax)
        {
            int seedSlot = ReleaseDrag(slot, newestT, out bool seeded);
            // Resume latch only when the fling rides this body: a chained ancestor's coast has no stream to resume.
            if (seeded && seedSlot == slot) _bodies[slot].InferredRelease = true;
            return true;
        }
        float resampled = ScrollPhysics.ResamplePaced(_histT.AsSpan(0, count), _histX.AsSpan(0, count), count, tStar, extrapolateMax);
        float delta = resampled - b.LastResampleX;
        // The resampler redistributes the packets' travel across frames; it must not invent any. Two bounds, both
        // from the raw packets themselves: a frame moves at most twice the largest packet in the retained history
        // (a burst delivered inside one frame is spread over the next few instead of landing as one jump), and at
        // most the undisplayed backlog plus one mean packet (extrapolation past the newest sample reaches one packet
        // beyond what was received, never further). LastResampleX advances by what was applied, so a capped frame's
        // remainder stays in the backlog and the paced total still equals the raw total.
        float newest = _histX[count - 1];
        float maxPacket = 0f, meanPacket = 0f;
        if (count >= 2)
        {
            for (int i = 1; i < count; i++)
            {
                float p = MathF.Abs(_histX[i] - _histX[i - 1]);
                if (p > maxPacket) maxPacket = p;
                meanPacket += p;
            }
            meanPacket /= count - 1;
        }
        else maxPacket = meanPacket = MathF.Abs(newest);   // a single-sample history starts at that packet's own travel
        float cap = MathF.Min(2f * maxPacket, MathF.Abs(newest - b.LastResampleX) + meanPacket);
        if (MathF.Abs(delta) > cap) delta = MathF.CopySign(cap, delta);
        b.LastResampleX += delta;
        if (delta != 0f) ApplyDragDelta(slot, slot, delta);
        return false;
    }

    private const double RawReleaseWindowS = 0.024;    // the raw packet span a release velocity is read over
    private const double RawReleaseStoppedS = 0.040;   // newest packet → release gap beyond which the stream had stopped

    /// <summary>A precise stream's release velocity from its RAW packet totals: the slope of the cumulative positions
    /// over the newest samples spanning at least <see cref="RawReleaseWindowS"/>. False when the retained history is
    /// shorter than that (a two-packet flick keeps the impulse estimator). The resampled positions are never used —
    /// they are a display schedule, not the finger — and a stream silent for more than
    /// <see cref="RawReleaseStoppedS"/> before the release reads 0.</summary>
    private bool TryRawStreamVelocity(in ScrollBody b, double tRelease, out float v)
    {
        v = 0f;
        int count = CopyHistory(in b, _histT, _histX);
        if (count < 2) return false;
        double tNew = _histT[count - 1];
        int from = -1;
        for (int i = count - 2; i >= 0; i--)
        {
            if (tNew - _histT[i] >= RawReleaseWindowS) { from = i; break; }
        }
        if (from < 0) return false;
        if (tRelease - tNew > RawReleaseStoppedS) return true;   // stopped before the lift: v = 0
        v = (float)((_histX[count - 1] - _histX[from]) / (tNew - _histT[from]));
        return true;
    }

    private static float NewestX(in ScrollBody b) => b.ContactCount switch { 0 => 0f, 1 => b.X0, 2 => b.X1, 3 => b.X2, 4 => b.X3, _ => b.X4 };
    private const double HistoryMinStepS = 0.000001;
    private static double NewestT(in ScrollBody b) => b.ContactCount switch { 0 => 0.0, 1 => b.T0, 2 => b.T1, 3 => b.T2, 4 => b.T3, _ => b.T4 };

    /// <summary>Fling-entry snap retarget (once): pick the snap value the natural decay would settle nearest, then
    /// re-solve velocity so the SAME exponential curve lands EXACTLY there (ScrollIntegrator.cs:393-410).</summary>
    private void SnapRetargetOnEntry(ref ScrollBody seed)
    {
        // Always cleared first: this runs exactly once per fling seed (ApplyContactEnd), so a fling that this time
        // has no snap grid configured must not inherit ScrollBody.SnapArmed left set by an EARLIER fling over a
        // (since-unmounted/reconfigured) snap viewport — Advance's Ballistic branch would otherwise chase a stale
        // Target from that prior fling instead of coasting naturally.
        seed.SnapArmed = false;
        var f = seed.Frame;
        if (f.SnapInterval <= 0f && (f.SnapPoints is null || f.SnapPoints.Length == 0)) return;
        float k = -MathF.Log(_feel.FlingDecayPerS);
        if (k <= 0f) return;
        float zoom = seed.Zoom > 0f ? seed.Zoom : 1f;
        float maxOff = MathF.Max(0f, f.ExtentMain * zoom - f.ViewportMain);
        float natural = Math.Clamp(seed.PositionMain + seed.Velocity / k, 0f, maxOff);
        float snapTarget = ScrollPhysics.SnapTarget(natural, f.SnapInterval, f.SnapStart, f.SnapEnd, f.SnapPoints, impulse: true, seed.DragAnchor);
        snapTarget = Math.Clamp(snapTarget, 0f, maxOff);
        seed.Velocity = (snapTarget - seed.PositionMain) * k;
        seed.Target = snapTarget;
        seed.TargetRaw = snapTarget;
        seed.SnapArmed = true;
    }

    private void ApplyFrameDelta(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        // A packet reaching a body whose stream PaceStream released by inference resumes that stream (the coast was
        // the stream's own extrapolation); any other non-DragMode-2 state is a fresh grab, which stops a fling.
        bool resume = b.InferredRelease && b.Activity == ScrollActivity.Ballistic;
        bool starting = !resume && (b.Activity != ScrollActivity.Drag || b.DragMode != 2);
        if (starting)
        {
            b.Activity = ScrollActivity.Drag;
            b.DragMode = 2;
            b.DragOrigin = b.PositionMain;
            b.DragAnchor = b.PositionMain;
            b.DragRaw = b.PositionMain + ScrollPhysics.ExcessFromBand(b.BandMain, b.Frame.ViewportMain);   // re-grab keeps a live stretch continuous
            b.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing | ScrollActivityFlags.Chained);
            ScrollBody.ClearWheelPlan(ref b);
            b.BandVelMain = 0f;
            b.LastAbsorbed = -1;
            b.EdgeHitPending = false;
            b.EdgeOvershoot = 0f;
            b.SnapArmed = false;
            b.InferredRelease = false;
            b.ContactCount = 0;
            b.LastResampleX = 0f;
            // Latched for the whole gesture: a mouse-wheel producer clamps at the extents (no band, no edge bounce).
            b.NoOverscroll = (cmd.Flags & (byte)ScrollInputFlags.NoOverscroll) != 0;
            MarkActive(idx);
        }
        else if (resume)
        {
            // The coast is already on screen: re-base the accumulator to the displayed position and restart the
            // sample history from this packet. Keeping the pre-release samples would make the paced resample span
            // the silent gap (its latency clamps to 34 ms) against a LastResampleX that has the coast folded in, so
            // the first resumed frame could apply about minus the coast travel. With the history restarted the
            // latch frame applies this packet's own travel, exactly as a fresh stream does.
            b.Activity = ScrollActivity.Drag;
            b.InferredRelease = false;
            b.Velocity = 0f;
            b.DragAnchor = b.PositionMain;
            b.DragRaw = b.PositionMain + ScrollPhysics.ExcessFromBand(b.BandMain, b.Frame.ViewportMain);
            b.ContactCount = 0;
            b.LastResampleX = 0f;
            b.EdgeHitPending = false;
            b.EdgeOvershoot = 0f;
            b.SnapArmed = false;
        }
        // The packet is a sample of the stream's cumulative position, applied by Tick's PaceStream resample (not 1:1
        // here) so per-frame steps follow the frame clock rather than packet arrival. The release-velocity estimator
        // is fed per RAW packet by ImpulseSample (ScrollInputRouter.AccumulatePhaseDelta), not here.
        PushHistory(ref b, cmd.T, NewestX(in b) + cmd.A);
    }

    /// <summary>bug-B/A3: feed ONE raw (pre-frame-coalesce) sample into a body's release-velocity estimator, without
    /// touching its offset/position (that stays <see cref="ApplyFrameDelta"/>'s job — no <c>MarkActive</c>/
    /// <c>MarkTouched</c>/sink write here, this never moves anything visible). Posted by
    /// <c>ScrollInputRouter.AccumulatePhaseDelta</c> once per Phase()-consumed packet (direct feed) and by
    /// <c>ScrollInputRouter.FeedImpulsePreSamples</c> for the pre-coalesce velocity-ring deposits of a frame that
    /// folded 2+ raw packets before Phase() ever saw them (Seams/Pal/Pal.cs "scroll-v3-plan §5.4"). A body with no
    /// bound slot (node died mid-gesture) is a silent no-op, matching every other node-keyed command handler.</summary>
    private void ApplyImpulseSample(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        bool reset = (cmd.Flags & (byte)ScrollInputFlags.ImpulseReset) != 0;
        if (reset) b.Impulse.Reset(cmd.A, cmd.T);
        else b.Impulse.Sample(cmd.A, cmd.T);
        if (ScrollTrace.CompiledIn && ScrollTrace.Enabled)
        {
            // aux/qpc: this layer only has cmd.T in SECONDS (the router already converted it) — no raw QPC tick
            // count survives to here, so the trace row's aux column is 0 ("no stamp"), not a wrong-domain value.
            int src = reset ? 2 : cmd.I;   // ScrollTrace.VelSample's own "2=reset" takes precedence over the caller's tag
            float px = b.Horizontal ? cmd.A : 0f, py = b.Horizontal ? 0f : cmd.A;
            float vx = b.Horizontal ? b.Impulse.Velocity : 0f, vy = b.Horizontal ? 0f : b.Impulse.Velocity;
            ScrollTrace.VelSample(src, px, py, vx, vy, 0L);
        }
    }

    /// <summary>Apply a main-axis drag delta at pool slot <paramref name="slot"/>, chaining any leftover excess to
    /// <see cref="ScrollBody.ChainParent"/> in the SAME tick (plan §2.2). <paramref name="gestureSlot"/> is the
    /// body the whole gesture addresses (where ContactBegin/first FrameDelta landed) — <see cref="ScrollBody.LastAbsorbed"/>
    /// is tracked THERE (as a NODE index) so a lift knows which body (self or a chained ancestor) to seed the fling on.</summary>
    private void ApplyDragDelta(int gestureSlot, int slot, float delta)
    {
        if (slot < 0 || !_bodies[slot].Bound) return;
        MarkActive(slot);
        ref ScrollBody body = ref _bodies[slot];
        float zoom = body.Zoom > 0f ? body.Zoom : 1f;
        float maxOff = MathF.Max(0f, body.Frame.ExtentMain * zoom - body.Frame.ViewportMain);
        float raw = body.DragRaw + delta;
        float clamped = Math.Clamp(raw, 0f, maxOff);
        float cur = body.PositionMain;
        SetOffsetMain(ref body, clamped);
        body.DragRaw = raw;
        float excess = raw - clamped;
        bool handedOff = false;

        if (excess != 0f && body.ChainParent >= 0 && TryGetSlot(body.ChainParent, out int parentSlot) && CanChainAbsorb(parentSlot, excess))
        {
            SetBandMain(ref body, 0f);
            // The parent now OWNS the surplus: the child's raw rests at its clamp so the next packet hands off only its
            // own increment (never the cumulative overshoot again), and a reversal moves the child back immediately
            // (CSS overscroll-behavior:auto — the inner scrolls whenever it can).
            body.DragRaw = clamped;
            ApplyDragDelta(gestureSlot, parentSlot, excess);
            _bodies[parentSlot].Flags |= ScrollActivityFlags.Chained;
            handedOff = true;
        }
        else if (gestureSlot >= 0 && gestureSlot < _bodies.Length && _bodies[gestureSlot].NoOverscroll)
        {
            // A mouse-wheel producer: clamp, no rubber band. The raw rests at the clamp (exactly like the chain hand-off
            // above) so a reversal scrolls back immediately instead of first unwinding an invisible overshoot.
            SetBandMain(ref body, 0f);
            body.DragRaw = clamped;
        }
        else
        {
            float band = ScrollPhysics.BandFromExcess(excess, body.Frame.ViewportMain);
            SetBandMain(ref body, band);
        }

        body = ref _bodies[slot];
        body.Activity = ScrollActivity.Drag;
        // Only the TERMINAL absorber in a hand-off chain claims LastAbsorbed this call — when this body handed its
        // excess up to a parent (the recursive ApplyDragDelta above already ran and set LastAbsorbed on whichever
        // node absorbed it), this body's own partial consumption (its clamped-move-before-handoff) must not
        // overwrite that with itself, or a lift always seeds on the outermost child instead of the true absorber.
        if (!handedOff && (clamped != cur || excess != 0f))
        {
            if (gestureSlot >= 0 && gestureSlot < _bodies.Length) _bodies[gestureSlot].LastAbsorbed = body.Node;
        }
        MarkTouched(slot);
    }

    private bool CanChainAbsorb(int parentSlot, float excessSign)
    {
        if (parentSlot < 0 || !_bodies[parentSlot].Bound) return false;
        ref ScrollBody parent = ref _bodies[parentSlot];
        if (parent.Parked) return false;
        float zoom = parent.Zoom > 0f ? parent.Zoom : 1f;
        float maxOff = MathF.Max(0f, parent.Frame.ExtentMain * zoom - parent.Frame.ViewportMain);
        float cur = parent.PositionMain;
        if (excessSign > 0f) return cur < maxOff - 0.001f;
        if (excessSign < 0f) return cur > 0.001f;
        return false;
    }

    // ── Wheel / programmatic / velocity / driven ─────────────────────────────────────────────────────────────

    /// <summary>A wheel notch arms (or re-plans) the Driven|Wheel glide through <see cref="ScrollPhysics.WheelPlanNotch"/>:
    /// a live wheel glide accumulates the target and re-plans its half-life/velocity from the observed cadence
    /// (velocity-continuous — nothing here ever resets <see cref="ScrollBody.Velocity"/>, that reset was the ease-in
    /// discontinuity the S1 plan removes); a cold notch seeds the chase at κ·R·y, or carries a same-direction
    /// Ballistic/Driven velocity (capped at the no-overshoot bound). A Drag body's velocity is stale by construction
    /// (command-driven, not integrated) and is never carried. The notch supersedes any pending programmatic request
    /// (<c>TargetRaw</c> relatched, restore cancelled) and clears the per-command ζ/ω/settle overrides.</summary>
    private int _wheelNotchesSinceTick;   // cadence marker for ScrollFrameSummary.WheelNotches; zeroed by UpdateSummary
    private float _maxAbsStructuralDip;   // ScrollFrameSummary.MaxAbsStructuralDip accumulator (AnchorShift / clamp correction); zeroed by UpdateSummary

    private void ApplyWheelNotch(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        _wheelNotchesSinceTick++;
        ref ScrollBody b = ref _bodies[idx];
        bool sameFlavourLive = b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Wheel) != 0;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float carry = (b.Activity == ScrollActivity.Ballistic || b.Activity == ScrollActivity.Driven) ? b.Velocity : 0f;
        ScrollPhysics.WheelPlanNotch(ref b.Target, ref b.Velocity, ref b.DrivenHalflifeMs, ref b.WheelSinceS, ref b.WheelGapS,
            b.PositionMain, cmd.A, maxOff, sameFlavourLive, carry, in _feel);
        b.TargetRaw = b.Target;   // a wheel notch supersedes any pending programmatic request
        CancelRestore(ref b);
        if (!sameFlavourLive) b.Awake = false;
        b.Activity = ScrollActivity.Driven;
        b.Flags = (b.Flags & ~(ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing)) | ScrollActivityFlags.Wheel;
        b.DrivenZeta = 0f; b.DrivenOmega = 0f; b.DrivenSettleVel = 0f;
        b.LandFloorDipPerS = 0f;   // a notch on a landing fling is a wheel glide, not a landing
        MarkActive(idx);
        MarkTouched(idx);
    }

    private void ApplyScrollTo(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        SetDrivenTarget(idx, cmd.A, in cmd);
    }

    private void ApplyScrollBy(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        SetDrivenTarget(idx, _bodies[idx].PositionMain + cmd.A, in cmd);
    }

    private void SetDrivenTarget(int idx, float target, in ScrollInput cmd)
    {
        ref ScrollBody b = ref _bodies[idx];
        bool immediate = (cmd.Flags & (byte)ScrollInputFlags.Immediate) != 0;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        // Latch the RAW request BEFORE the clamp: the extent known right now may be a fraction of what the content
        // will be a few frames from here (a Reflow drawer mid-animation, a list that measures late), and without this
        // the truncated Target would be the permanent destination — ApplySetFrame re-derives from TargetRaw instead.
        b.TargetRaw = target;
        CancelRestore(ref b);
        target = Math.Clamp(target, 0f, maxOff);

        if (immediate)
        {
            SetOffsetMain(ref b, target);
            b.Target = target;
            b.TargetRaw = target;   // a snap has no chase to regrow — never leave the pair disagreeing
            b.Velocity = 0f;
            b.Activity = ScrollActivity.Idle;
            b.Flags &= ~(ScrollActivityFlags.Programmatic | ScrollActivityFlags.Wheel | ScrollActivityFlags.Autoscroll);
            ScrollBody.ClearWheelPlan(ref b);
            MarkTouched(idx);
            return;
        }

        bool sameFlavourLive = b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Programmatic) != 0;
        b.Target = target;
        if (!sameFlavourLive) { b.Velocity = 0f; b.Awake = false; }
        b.Activity = ScrollActivity.Driven;
        b.Flags = (b.Flags & ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing)) | ScrollActivityFlags.Programmatic;
        ScrollBody.ClearWheelPlan(ref b);
        float halflife = cmd.B > 0f ? cmd.B
            : ScrollPhysics.ProgrammaticHalflifeS(MathF.Abs(target - b.PositionMain), _feel.ProgrammaticMinHalflifeMs, _feel.ProgrammaticMaxHalflifeMs, _feel.ProgrammaticShortDip, _feel.ProgrammaticLongDip);
        b.DrivenHalflifeMs = halflife;
        b.LandFloorDipPerS = 0f;   // a ScrollTo/ScrollBy on a landing fling is a programmatic glide, not a landing
        b.DrivenZeta = cmd.C;
        b.DrivenOmega = cmd.D;
        b.DrivenSettleVel = cmd.E;
        MarkActive(idx);
        MarkTouched(idx);
    }

    private void ApplySetVelocity(in ScrollInput cmd)
    {
        if (!TryGetSlot(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        float v = cmd.A;
        if (v == 0f)
        {
            if ((b.Flags & ScrollActivityFlags.Autoscroll) != 0)
            {
                b.Activity = ScrollActivity.Idle;
                b.Velocity = 0f;
                b.Flags &= ~ScrollActivityFlags.Autoscroll;
                MarkTouched(idx);
            }
            return;
        }
        b.Velocity = v;
        b.Activity = ScrollActivity.Driven;
        b.Flags = (b.Flags & ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Bouncing)) | ScrollActivityFlags.Autoscroll;
        ScrollBody.ClearWheelPlan(ref b);
        MarkActive(idx);
        MarkTouched(idx);
    }

    // ── Edge resolution (Reclamp) ─────────────────────────────────────────────────────────────────────────────

    private void ResolveEdge(int slot)
    {
        ref ScrollBody b = ref _bodies[slot];
        if (!b.EdgeHitPending) return;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float pos = b.PositionMain;
        bool stillAtEdge = pos <= 0.0001f || pos >= maxOff - 0.0001f;
        float overshoot = b.EdgeOvershoot;
        b.EdgeHitPending = false;
        b.EdgeOvershoot = 0f;

        if (!stillAtEdge)
        {
            // Fresh geometry gave it room. The pinned step already decayed the velocity for its whole dt, so re-apply
            // the travel the clamp withheld (bounded by the new room) — Reclamp runs after layout and before record,
            // so it lands on screen this same frame instead of leaving a dead frame in the coast.
            float room = overshoot >= 0f ? maxOff - pos : pos;
            float apply = MathF.CopySign(MathF.Min(MathF.Abs(overshoot), room), overshoot);
            if (apply != 0f)
            {
                pos += apply;
                SetOffsetMain(ref b, pos);
                stillAtEdge = pos <= 0.0001f || pos >= maxOff - 0.0001f;
            }
            if (!stillAtEdge)
            {
                MarkTouched(slot); // Ballistic simply continues next Tick
                return;
            }
            // The room was smaller than the withheld travel: it is at the NEW edge now — resolve below as usual.
        }

        float v = b.Velocity;
        float excessSign = pos <= 0.0001f ? -1f : 1f;
        if (b.ChainParent >= 0 && TryGetSlot(b.ChainParent, out int parentSlot) && CanChainAbsorb(parentSlot, excessSign))
        {
            b.Activity = ScrollActivity.Idle;
            b.Velocity = 0f;
            b.Awake = false;
            ref ScrollBody parent = ref _bodies[parentSlot];
            parent.Activity = ScrollActivity.Ballistic;
            parent.Velocity = v;
            // No per-viewport snap retarget for a chain hand-off (the child's edge, not the parent's, is what fired)
            // — but a stale True from an EARLIER fling on this same parent body must not leak in and chase a
            // long-settled Target (the exact class of bug ScrollPhysics.SnapLandEpsPx's doc covers for the seed path).
            parent.SnapArmed = false;
            parent.Awake = false;
            parent.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing);
            ScrollBody.ClearWheelPlan(ref parent);
            MarkActive(parentSlot);
            MarkTouched(parentSlot);
        }
        else if (MathF.Abs(v) >= _feel.FlingSettleVel && !b.NoOverscroll)
        {
            // (A mouse-wheel fling — NoOverscroll — falls through to the dead stop below: a wheel never bounces.)
            float bandv = b.BandVelMain;
            ScrollPhysics.SeedFromEdgeMomentum(ref bandv, v, b.Frame.ViewportMain);
            b.BandVelMain = bandv;
            b.Activity = ScrollActivity.Idle;
            b.Flags |= ScrollActivityFlags.Bouncing;
            b.Velocity = 0f;
            b.Awake = false;
        }
        else
        {
            b.Activity = ScrollActivity.Idle;
            b.Velocity = 0f;
            b.Awake = false;
        }
        MarkTouched(slot);
    }

    // ── Contact history (fixed 5-slot, chronological T0/X0=oldest .. T4/X4=newest within ContactCount) ─────────

    private static void PushHistory(ref ScrollBody b, double t, float x)
    {
        // Stamps must be strictly increasing: a frame-pumped packet stamped with the frame instant can arrive after
        // an idle-pumped one stamped `now`, and a history that runs backwards blew up the resamplers (a Math.Clamp
        // with min > max threw out of Tick on 2026-09-17). Fold such a packet into the next microsecond.
        if (b.ContactCount > 0)
        {
            double newest = NewestT(in b);
            if (t <= newest) t = newest + HistoryMinStepS;
        }
        if (b.ContactCount < 5)
        {
            SetSlot(ref b, b.ContactCount, t, x);
            b.ContactCount++;
        }
        else
        {
            b.T0 = b.T1; b.X0 = b.X1;
            b.T1 = b.T2; b.X1 = b.X2;
            b.T2 = b.T3; b.X2 = b.X3;
            b.T3 = b.T4; b.X3 = b.X4;
            b.T4 = t; b.X4 = x;
        }
    }

    private static void SetSlot(ref ScrollBody b, int i, double t, float x)
    {
        switch (i)
        {
            case 0: b.T0 = t; b.X0 = x; break;
            case 1: b.T1 = t; b.X1 = x; break;
            case 2: b.T2 = t; b.X2 = x; break;
            case 3: b.T3 = t; b.X3 = x; break;
            default: b.T4 = t; b.X4 = x; break;
        }
    }

    private static int CopyHistory(in ScrollBody b, double[] t, float[] x)
    {
        int n = b.ContactCount;
        if (n > 0) { t[0] = b.T0; x[0] = b.X0; }
        if (n > 1) { t[1] = b.T1; x[1] = b.X1; }
        if (n > 2) { t[2] = b.T2; x[2] = b.X2; }
        if (n > 3) { t[3] = b.T3; x[3] = b.X3; }
        if (n > 4) { t[4] = b.T4; x[4] = b.X4; }
        return n;
    }

    // ── Field helpers, active/touched bookkeeping, emission ──────────────────────────────────────────────────

    private static void SetOffsetMain(ref ScrollBody b, float v) { if (b.Horizontal) b.OffsetX = v; else b.OffsetY = v; }
    private static void SetBandMain(ref ScrollBody b, float v) { if (b.Horizontal) b.BandX = v; else b.BandY = v; }

    /// <summary>Enrol a pool slot in the active list. Allocation-free by construction: <c>_activeList</c> is sized to
    /// the pool (<see cref="EnsureSlotCapacity"/>) and <c>_inActive</c> admits each slot at most once, so
    /// <c>_activeCount</c> can never reach <c>_activeList.Length</c> — no growth on this per-frame path.</summary>
    private void MarkActive(int slot)
    {
        if (slot < 0 || _inActive[slot]) return;
        _inActive[slot] = true;
        _activeList[_activeCount++] = slot;
        // Enrolment precedes the enrolling command's write (ApplyDragDelta, the wheel plan, a programmatic glide), so
        // this is the position the first summary of the new gesture measures its displacement from.
        _bodies[slot].SummaryMain = _bodies[slot].PositionMain;
    }

    /// <summary>Stamp a pool slot as written this pass. Same bound as <see cref="MarkActive"/>: the stamp admits each
    /// slot once, and <c>_touchedSlots</c> is pool-sized — allocation-free on this per-frame path.</summary>
    private void MarkTouched(int slot)
    {
        if (slot < 0 || _touchedStamp[slot] == _stamp) return;
        _touchedStamp[slot] = _stamp;
        _touchedSlots[_touchedCount++] = slot;
    }

    private void EmitTouched(ScrollWriteSource writer)
    {
        for (int i = 0; i < _touchedCount; i++)
        {
            int slot = _touchedSlots[i];
            ref ScrollBody b = ref _bodies[slot];
            if (!b.Bound) continue;
            ScrollWrite write = BuildWrite(in b, writer);
            _sink.Apply(b.Node, in write);
        }
    }

    private static ScrollWrite BuildWrite(in ScrollBody b, ScrollWriteSource writer)
    {
        ScrollWriteMask mask = b.Horizontal
            ? ScrollWriteMask.OffsetX | ScrollWriteMask.BandX
            : ScrollWriteMask.OffsetY | ScrollWriteMask.BandY;
        if (MathF.Abs(b.Zoom - 1f) > 0.0001f) mask |= ScrollWriteMask.Zoom;
        float visualSpeed = MathF.Abs(b.VelocityMain);
        return new ScrollWrite(b.OffsetX, b.OffsetY, b.BandX, b.BandY, b.Zoom, b.VelocityMain, visualSpeed,
            b.Activity, b.Flags, mask, b.LastReleaseVelocity, writer);
    }

    private void CompactActiveList()
    {
        int w = 0;
        int wake = 0;
        for (int r = 0; r < _activeCount; r++)
        {
            int slot = _activeList[r];
            ref ScrollBody b = ref _bodies[slot];
            bool keep = b.Bound && (!b.IsSettled || b.Parked || b.RestorePending || b.EdgeHitPending);
            if (keep)
            {
                _activeList[w++] = slot;
                if (!b.Parked) wake++;
            }
            else _inActive[slot] = false;
        }
        _activeCount = w;
        _wakeActiveCount = wake;
    }

    /// <param name="clock">The tick's clock; only read when <paramref name="timed"/> (a <see cref="Reclamp"/> has no
    /// time step, so it never judges a contact held).</param>
    private void UpdateSummary(in ScrollClock clock, bool timed)
    {
        bool anyMoved = _touchedCount > 0;
        bool anyUserActive = false, anyLiveMotion = false, anyContactHeld = false;
        float maxSpeed = 0f, maxAbsDelta = 0f;
        int edgePins = 0;
        var zeroReason = ScrollZeroReason.None;
        for (int i = 0; i < _activeCount; i++)
        {
            ref ScrollBody b = ref _bodies[_activeList[i]];
            // A parked body is resident but never ticked; it must not report motion the host would budget a frame for.
            if (!b.Bound) continue;
            if (b.Parked)
            {
                if (timed && b.Activity == ScrollActivity.Ballistic && zeroReason == ScrollZeroReason.None) zeroReason = ScrollZeroReason.Parked;
                continue;
            }
            b.TickDeltaMain = b.PositionMain - b.SummaryMain;
            b.SummaryMain = b.PositionMain;
            float absDelta = MathF.Abs(b.TickDeltaMain);
            if (absDelta > maxAbsDelta) maxAbsDelta = absDelta;
            if (b.EdgeHitPending) edgePins++;
            // A body that should have coasted this tick and did not: name why, for the trace.
            bool shouldCoast = (b.Activity == ScrollActivity.Ballistic && MathF.Abs(b.Velocity) >= _feel.FlingLandVel)
                || (b.Activity == ScrollActivity.Driven && b.LandFloorDipPerS > 0f);
            if (timed && shouldCoast && absDelta < 0.05f && zeroReason == ScrollZeroReason.None)
            {
                zeroReason = clock.DtSec <= 0f ? ScrollZeroReason.DtZero
                    : b.LastAdvanceStamp != _stamp ? ScrollZeroReason.NotAdvanced
                    : b.EdgeHitPending ? ScrollZeroReason.Pinned
                    : ScrollZeroReason.Other;
            }
            if (timed && b.TickDeltaMain == 0f && b.Activity == ScrollActivity.Drag && IsContactHeld(in b, in clock)) anyContactHeld = true;
            bool userActive = b.Activity == ScrollActivity.Drag || b.Activity == ScrollActivity.Ballistic
                || (b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Programmatic) == 0);
            if (userActive) anyUserActive = true;
            // Continuous motion the host budgets a frame for: a drag, a fling, or a wheel/programmatic glide (an
            // Autoscroll drive is excluded — it is a constant-velocity edge drive, not a bounded glide).
            bool liveMotion = b.Activity == ScrollActivity.Drag || b.Activity == ScrollActivity.Ballistic
                || (b.Activity == ScrollActivity.Driven && (b.Flags & (ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic)) != 0);
            if (liveMotion) anyLiveMotion = true;
            float speed = MathF.Abs(b.VelocityMain);
            if (speed > maxSpeed) maxSpeed = speed;
        }
        // A body that landed THIS tick has already left the active list (CompactActiveList runs first), and an
        // immediate write (ScrollBy immediate, a restore) touches a body that was never active: both are read off the
        // touched set, so the landing frame's shift and a one-frame jump are part of the trace, not zeros. A slot in
        // both sets was already settled above (TickDeltaMain is now 0 against the refreshed SummaryMain — harmless).
        for (int i = 0; i < _touchedCount; i++)
        {
            ref ScrollBody b = ref _bodies[_touchedSlots[i]];
            if (_inActive[_touchedSlots[i]]) continue;
            b.TickDeltaMain = b.PositionMain - b.SummaryMain;
            b.SummaryMain = b.PositionMain;
            float absDelta = MathF.Abs(b.TickDeltaMain);
            if (absDelta > maxAbsDelta) maxAbsDelta = absDelta;
        }
        Summary = new ScrollFrameSummary(anyMoved, anyUserActive, anyLiveMotion, _activeCount, maxSpeed, maxAbsDelta, _wheelNotchesSinceTick,
            edgePins, _maxAbsStructuralDip, anyContactHeld, zeroReason);
        _wheelNotchesSinceTick = 0;
        _maxAbsStructuralDip = 0f;
    }

    /// <summary>A Drag body's newest contact sample is older than its resample latency plus one frame: touch/pen
    /// (DragMode 1) at <c>ResampleLatencyMs</c>, a precise stream (DragMode 2) at <c>PacedLatencyS(mean interval)</c>.
    /// Same frame-reference rule as the resample itself (a foreign sample clock is judged against its own newest
    /// sample). A DragMode 2 stream is released by inference once it is silent past <c>DragExtrapolateMaxMs</c>, so
    /// for it this window is at most that wide.</summary>
    private bool IsContactHeld(in ScrollBody b, in ScrollClock clock)
    {
        if (b.ContactCount == 0 || b.DragMode == 0) return false;
        double newestT = NewestT(in b);
        double frameRef = Math.Abs(clock.FrameSec - newestT) <= ForeignClockToleranceSec ? clock.FrameSec : newestT;
        double frame = clock.RefreshSec > 0f ? clock.RefreshSec : clock.DtSec;
        double latency;
        if (b.DragMode == 1) latency = _feel.ResampleLatencyMs / 1000.0;
        else
        {
            double oldestT = b.T0;
            double interval = b.ContactCount >= 2 ? (newestT - oldestT) / (b.ContactCount - 1) : 0.0;
            latency = ScrollPhysics.PacedLatencyS(interval);
        }
        return frameRef - newestT > latency + frame;
    }

    private void UpdateDiag()
    {
        byte word = 0;
        double lastContact = 0.0;
        bool anySampled = false;
        for (int i = 0; i < _activeCount; i++)
        {
            ref ScrollBody b = ref _bodies[_activeList[i]];
            if (!b.Bound) continue;
            byte w = b.Activity switch
            {
                ScrollActivity.Drag => (byte)1,
                ScrollActivity.Ballistic => (byte)2,
                ScrollActivity.Driven => (byte)3,
                _ => (byte)((b.Flags & ScrollActivityFlags.Bouncing) != 0 ? 3 : 0),
            };
            if (w > word) word = w;
            if (b.ContactCount > 0)
            {
                double newest = b.ContactCount switch { 1 => b.T0, 2 => b.T1, 3 => b.T2, 4 => b.T3, _ => b.T4 };
                if (newest > lastContact) lastContact = newest;
                anySampled = true;
            }
        }
        Diag.GestureWord = word;
        Diag.LastContactSampleSec = lastContact;
        Diag.TrackingLagSampled = anySampled;
        // TrackingLagDip / TrackingVelocityDipPerMs: left at 0 for Phase 1 — they compare DEMANDED vs DISPLAYED
        // position, which requires the UI-side sink's actual applied value (WP-F wires this once ScrollTrace's
        // API changes land; the kernel has nothing to compare against on its own).
    }
}
