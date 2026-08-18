using System;

namespace FluentGpu.Scroll;

/// <summary>The scroll v3 kernel — a POD slab of <see cref="ScrollBody"/> (indexed by scene node index) driven by
/// ONE <see cref="ScrollCommandPort"/> intake and ONE <see cref="IScrollSink"/> outlet (plan §2). Nothing here
/// references <c>SceneStore</c>/<c>NodeHandle</c>/<c>RenderContext</c> — that is what makes it tickable from a
/// non-UI thread (the render-thread fling lease, §6). Body slab growth happens ONLY on <see cref="ScrollInputKind.Bind"/>
/// (never inside <see cref="Tick"/>/<see cref="Reclamp"/>) so both stay zero-alloc after warm-up.</summary>
public sealed class ScrollKernel
{
    /// <summary>A contact sample farther than this from the frame clock is stamped in a foreign clock domain (a
    /// scripted or replayed stream): the resample then evaluates relative to the newest sample (see Tick).</summary>
    private const double ForeignClockToleranceSec = 0.5;
    private readonly IScrollSink _sink;
    private readonly ScrollFeel _feel;

    private ScrollBody[] _bodies;
    private int[] _activeList;
    private bool[] _inActive;
    private int _activeCount;

    // Per-call (Tick or Reclamp) "touched" tracking, stamp-based so it never needs an O(capacity) clear.
    private int[] _touchedNodes;
    private int[] _touchedStamp;
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
    public ScrollKernelDiag Diag;

    public ScrollKernel(IScrollSink sink, in ScrollFeel feel, int initialCapacity = 64)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _feel = feel;
        int cap = Math.Max(4, initialCapacity);
        _bodies = new ScrollBody[cap];
        _activeList = new int[cap];
        _inActive = new bool[cap];
        _touchedNodes = new int[cap];
        _touchedStamp = new int[cap];
        Port = new ScrollCommandPort();
        _drainScratch = new ScrollInput[ScrollCommandPort.Capacity];
        _structuralScratch = new ScrollInput[ScrollCommandPort.Capacity];
        _histT = new double[5];
        _histX = new float[5];
    }

    // ── Public reads ──────────────────────────────────────────────────────────────────────────────────────────

    public bool TryGetBody(int node, out ScrollBody snapshot)
    {
        if (node >= 0 && node < _bodies.Length && _bodies[node].Bound) { snapshot = _bodies[node]; return true; }
        snapshot = default;
        return false;
    }

    /// <summary>§6 render-thread fling-lease hand-off (reserved — Phase 6/WP-L wires the actual seam). Hands out a
    /// by-value snapshot for a body currently Ballistic/Driven; bumps <see cref="ScrollBody.LeaseSeq"/> so a stale
    /// <see cref="Return"/> can be detected and ignored.</summary>
    public bool TryLease(int node, out ScrollBody body, out uint seq)
    {
        if (node >= 0 && node < _bodies.Length && _bodies[node].Bound &&
            (_bodies[node].Activity == ScrollActivity.Ballistic || _bodies[node].Activity == ScrollActivity.Driven))
        {
            ref var b = ref _bodies[node];
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
        if (node < 0 || node >= _bodies.Length || !_bodies[node].Bound) return;
        if (_bodies[node].LeaseSeq != seq) return;
        _bodies[node] = body;
        _bodies[node].LeaseSeq = seq;
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
            int node = _activeList[k];
            ref ScrollBody b = ref _bodies[node];
            if (!b.Bound || b.Parked) continue;

            if (b.Activity == ScrollActivity.Drag)
            {
                float rawBefore = b.DragRaw;
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
                        if (delta != 0f) ApplyDragDelta(node, node, delta);
                    }
                }
                // Live drag speed (signed, main axis) from this tick's raw advance — the result column the realize-ahead
                // skew and text-motion softness read; it is NOT the fling seed (the impulse estimator owns that).
                b = ref _bodies[node];
                float dtV = clock.DtSec > 0f ? Math.Min(clock.DtSec, 0.034f) : clock.RefreshSec;
                b.Velocity = dtV > 0f ? (b.DragRaw - rawBefore) / dtV : 0f;
                MarkTouched(node);
                continue;
            }

            ScrollBody.Advance(ref b, in clock, in _feel);
            MarkTouched(node);
        }

        EmitTouched(ScrollWriteSource.Tick);
        CompactActiveList();
        UpdateSummary();
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
            int node = _activeList[k];
            ref ScrollBody b = ref _bodies[node];
            if (b.Bound && b.EdgeHitPending) ResolveEdge(node);
        }

        EmitTouched(ScrollWriteSource.Reclamp);
        CompactActiveList();
        UpdateSummary();
    }

    // ── Command dispatch (shared by Tick's full drain and Reclamp's structural-only drain) ──────────────────────

    private void ProcessCommand(in ScrollInput cmd)
    {
        switch (cmd.Kind)
        {
            case ScrollInputKind.Bind: BindNode(cmd.Node); break;
            case ScrollInputKind.Unbind: UnbindNode(cmd.Node); break;
            case ScrollInputKind.Park:
                if (TryGetBoundRef(cmd.Node, out int pidx))
                {
                    _bodies[pidx].Parked = (cmd.Flags & (byte)ScrollInputFlags.Immediate) != 0;
                    MarkTouched(pidx);
                }
                break;
            case ScrollInputKind.SetFrame: ApplySetFrame(in cmd); break;
            case ScrollInputKind.SetZoom: ApplySetZoom(in cmd); break;
            case ScrollInputKind.Chain:
                if (TryGetBoundRef(cmd.Node, out int cidx)) _bodies[cidx].ChainParent = cmd.I;
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
        }
    }

    private void BindNode(int node)
    {
        if (node < 0) return;
        EnsureCapacity(node);
        ref ScrollBody b = ref _bodies[node];
        if (b.Bound) return; // idempotent
        b = default;
        b.Node = node;
        b.Bound = true;
        b.ChainParent = -1;
        b.LastAbsorbed = -1;
        b.Zoom = 1f;
    }

    private void UnbindNode(int node)
    {
        if (node < 0 || node >= _bodies.Length) return;
        _bodies[node].Bound = false;
        if (node < _inActive.Length) _inActive[node] = false; // lazily dropped from _activeList by CompactActiveList
    }

    private void EnsureCapacity(int node)
    {
        if (node < _bodies.Length) return;
        int cap = _bodies.Length;
        while (cap <= node) cap *= 2;
        Array.Resize(ref _bodies, cap);
        Array.Resize(ref _activeList, cap);
        Array.Resize(ref _inActive, cap);
        Array.Resize(ref _touchedNodes, cap);
        Array.Resize(ref _touchedStamp, cap);
    }

    private bool TryGetBoundRef(int node, out int idx)
    {
        idx = node;
        return node >= 0 && node < _bodies.Length && _bodies[node].Bound;
    }

    // ── Structural handlers ───────────────────────────────────────────────────────────────────────────────────

    private void ApplySetFrame(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
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
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
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
        b.TargetRaw = b.Target;   // zoom rewrites content space — the old raw request no longer means anything
        MarkTouched(idx);
    }

    private void ApplyCancel(int node)
    {
        if (!TryGetBoundRef(node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        b.Activity = ScrollActivity.Idle;
        b.Velocity = 0f;
        b.BandVelMain = 0f;
        b.BandX = 0f; b.BandY = 0f;
        b.Flags = ScrollActivityFlags.None;
        b.TargetRaw = b.Target;   // the request is dead — never let it resurrect when the content next grows
        b.EdgeHitPending = false;
        b.Awake = false;
        MarkTouched(idx);
    }

    private void ApplyThumbSet(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float target = Math.Clamp(cmd.A, 0f, maxOff);
        SetOffsetMain(ref b, target);
        b.Velocity = 0f;
        b.Activity = ScrollActivity.Idle;
        b.Flags = ScrollActivityFlags.None;
        b.TargetRaw = b.Target;   // dragging the thumb wins over any pending programmatic request
        MarkTouched(idx);
    }

    private void ApplyRestore(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        bool wasPending = b.RestorePending;
        b.RestoreX = cmd.A; b.RestoreY = cmd.B;
        b.RestorePending = true;
        if (!wasPending) _restorePendingCount++;
        if (TryApplyRestore(ref b)) _restorePendingCount--;
        MarkTouched(idx);
    }

    private void ResolveRestores()
    {
        for (int node = 0; node < _bodies.Length && _restorePendingCount > 0; node++)
        {
            ref ScrollBody b = ref _bodies[node];
            if (!b.Bound || !b.RestorePending) continue;
            if (TryApplyRestore(ref b))
            {
                _restorePendingCount--;
                MarkTouched(node);
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
        b.RestorePending = false;
        return true;
    }

    private void ApplyAnchorShift(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
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
        ClampToFrame(ref b);
        MarkTouched(idx);
    }

    private static void ClampToFrame(ref ScrollBody b)
    {
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float pos = b.PositionMain;
        float clamped = Math.Clamp(pos, 0f, maxOff);
        if (clamped != pos) SetOffsetMain(ref b, clamped);
    }

    // ── Drag (ContactBegin/Move/End, FrameDelta) ─────────────────────────────────────────────────────────────

    private void ApplyContactBegin(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
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
        PushHistory(ref b, cmd.T, cmd.A);
        b.Impulse.Reset(cmd.A, cmd.T);
        b.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing | ScrollActivityFlags.Chained);
        b.TargetRaw = b.Target;   // the finger took over — a pending programmatic request must not resurrect on growth
        b.BandVelMain = 0f;
        b.LastAbsorbed = -1;
        b.EdgeHitPending = false;
        MarkActive(idx);
        MarkTouched(idx);
    }

    private void ApplyContactMove(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        if (b.Activity != ScrollActivity.Drag || b.DragMode != 1) { ApplyContactBegin(in cmd); b = ref _bodies[idx]; }
        PushHistory(ref b, cmd.T, cmd.A);
        b.Impulse.Sample(cmd.A, cmd.T);
        // Position is recomputed once per Tick from the full history (see Tick's active-body loop) — no resample here.
    }

    private void ApplyContactEnd(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        if (b.Activity != ScrollActivity.Drag) return; // a stray End with no live drag — nothing to seed

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
                b = ref _bodies[idx];
            }
        }

        b.Impulse.ComputeReleaseVelocity(cmd.T);
        float v = b.Impulse.Velocity;
        b.LastReleaseVelocity = v;

        int seedNode = _bodies[idx].LastAbsorbed >= 0 ? _bodies[idx].LastAbsorbed : idx;
        ref ScrollBody seed = ref _bodies[seedNode];
        seed.LastReleaseVelocity = v;
        float band = seed.BandMain;

        if (band == 0f && MathF.Abs(v) >= _feel.FlingSeedGate)
        {
            seed.Activity = ScrollActivity.Ballistic;
            seed.Velocity = Math.Clamp(v, -_feel.FlingMax, _feel.FlingMax);
            seed.Awake = false;
            seed.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Chained);
            SnapRetargetOnEntry(ref seed);
            MarkActive(seedNode);
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
            MarkActive(seedNode);
        }
        else
        {
            seed.Activity = ScrollActivity.Idle;
            seed.Velocity = 0f;
        }

        if (idx != seedNode)
        {
            ref ScrollBody finger = ref _bodies[idx];
            finger.Activity = ScrollActivity.Idle;
            finger.Velocity = 0f;
            MarkTouched(idx);
        }
        _bodies[idx].LastAbsorbed = -1;
        MarkTouched(seedNode);
    }

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
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        bool starting = b.Activity != ScrollActivity.Drag || b.DragMode != 2;
        if (starting)
        {
            b.Activity = ScrollActivity.Drag;
            b.DragMode = 2;
            b.DragOrigin = b.PositionMain;
            b.DragAnchor = b.PositionMain;
            b.DragRaw = b.PositionMain + ScrollPhysics.ExcessFromBand(b.BandMain, b.Frame.ViewportMain);   // re-grab keeps a live stretch continuous
            b.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing | ScrollActivityFlags.Chained);
            b.BandVelMain = 0f;
            b.LastAbsorbed = -1;
            b.EdgeHitPending = false;
            MarkActive(idx);
        }
        ApplyDragDelta(idx, idx, cmd.A);
        b = ref _bodies[idx];
        // (T, Σdelta) — DragRaw IS the running delta-sum accumulator. Reset (not Sample) on the FIRST frame of a new
        // drag: it must seed the estimator with the POST-delta position at cmd.T (matching what every later Sample
        // records), not the pre-delta baseline — seeding pre-delta would double the very first computed segment
        // velocity against the second sample.
        if (starting) b.Impulse.Reset(b.DragRaw, cmd.T);
        else b.Impulse.Sample(b.DragRaw, cmd.T);
        MarkTouched(idx);
    }

    /// <summary>Apply a main-axis drag delta at <paramref name="node"/>, chaining any leftover excess to
    /// <see cref="ScrollBody.ChainParent"/> in the SAME tick (plan §2.2). <paramref name="gestureNode"/> is the
    /// node the whole gesture addresses (where ContactBegin/first FrameDelta landed) — <see cref="ScrollBody.LastAbsorbed"/>
    /// is tracked THERE so a lift knows which body (self or a chained ancestor) to seed the fling on.</summary>
    private void ApplyDragDelta(int gestureNode, int node, float delta)
    {
        if (node < 0 || node >= _bodies.Length || !_bodies[node].Bound) return;
        MarkActive(node);
        ref ScrollBody body = ref _bodies[node];
        float zoom = body.Zoom > 0f ? body.Zoom : 1f;
        float maxOff = MathF.Max(0f, body.Frame.ExtentMain * zoom - body.Frame.ViewportMain);
        float raw = body.DragRaw + delta;
        float clamped = Math.Clamp(raw, 0f, maxOff);
        float cur = body.PositionMain;
        SetOffsetMain(ref body, clamped);
        body.DragRaw = raw;
        float excess = raw - clamped;
        bool handedOff = false;

        if (excess != 0f && body.ChainParent >= 0 && CanChainAbsorb(body.ChainParent, excess))
        {
            SetBandMain(ref body, 0f);
            // The parent now OWNS the surplus: the child's raw rests at its clamp so the next packet hands off only its
            // own increment (never the cumulative overshoot again), and a reversal moves the child back immediately
            // (CSS overscroll-behavior:auto — the inner scrolls whenever it can).
            body.DragRaw = clamped;
            int parentNode = body.ChainParent;
            ApplyDragDelta(gestureNode, parentNode, excess);
            _bodies[parentNode].Flags |= ScrollActivityFlags.Chained;
            handedOff = true;
        }
        else
        {
            float band = ScrollPhysics.BandFromExcess(excess, body.Frame.ViewportMain);
            SetBandMain(ref body, band);
        }

        body = ref _bodies[node];
        body.Activity = ScrollActivity.Drag;
        // Only the TERMINAL absorber in a hand-off chain claims LastAbsorbed this call — when this body handed its
        // excess up to a parent (the recursive ApplyDragDelta above already ran and set LastAbsorbed on whichever
        // node absorbed it), this body's own partial consumption (its clamped-move-before-handoff) must not
        // overwrite that with itself, or a lift always seeds on the outermost child instead of the true absorber.
        if (!handedOff && (clamped != cur || excess != 0f))
        {
            if (gestureNode >= 0 && gestureNode < _bodies.Length) _bodies[gestureNode].LastAbsorbed = node;
        }
        MarkTouched(node);
    }

    private bool CanChainAbsorb(int parentNode, float excessSign)
    {
        if (parentNode < 0 || parentNode >= _bodies.Length || !_bodies[parentNode].Bound) return false;
        ref ScrollBody parent = ref _bodies[parentNode];
        if (parent.Parked) return false;
        float zoom = parent.Zoom > 0f ? parent.Zoom : 1f;
        float maxOff = MathF.Max(0f, parent.Frame.ExtentMain * zoom - parent.Frame.ViewportMain);
        float cur = parent.PositionMain;
        if (excessSign > 0f) return cur < maxOff - 0.001f;
        if (excessSign < 0f) return cur > 0.001f;
        return false;
    }

    // ── Wheel / programmatic / velocity / driven ─────────────────────────────────────────────────────────────

    private void ApplyWheelNotch(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        ref ScrollBody b = ref _bodies[idx];
        bool sameFlavourLive = b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Wheel) != 0;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float baseTarget = sameFlavourLive ? b.Target : b.PositionMain;
        b.Target = Math.Clamp(baseTarget + cmd.A, 0f, maxOff);
        b.TargetRaw = b.Target;   // a wheel notch supersedes any pending programmatic request
        if (!sameFlavourLive) { b.Velocity = 0f; b.Awake = false; }
        b.Activity = ScrollActivity.Driven;
        b.Flags = (b.Flags & ~(ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing)) | ScrollActivityFlags.Wheel;
        b.DrivenHalflifeMs = _feel.WheelHalflifeMs;
        b.DrivenZeta = 0f; b.DrivenOmega = 0f; b.DrivenSettleVel = 0f;
        MarkActive(idx);
        MarkTouched(idx);
    }

    private void ApplyScrollTo(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
        SetDrivenTarget(idx, cmd.A, in cmd);
    }

    private void ApplyScrollBy(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
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
        target = Math.Clamp(target, 0f, maxOff);

        if (immediate)
        {
            SetOffsetMain(ref b, target);
            b.Target = target;
            b.TargetRaw = target;   // a snap has no chase to regrow — never leave the pair disagreeing
            b.Velocity = 0f;
            b.Activity = ScrollActivity.Idle;
            b.Flags &= ~(ScrollActivityFlags.Programmatic | ScrollActivityFlags.Wheel | ScrollActivityFlags.Autoscroll);
            MarkTouched(idx);
            return;
        }

        bool sameFlavourLive = b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Programmatic) != 0;
        b.Target = target;
        if (!sameFlavourLive) { b.Velocity = 0f; b.Awake = false; }
        b.Activity = ScrollActivity.Driven;
        b.Flags = (b.Flags & ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing)) | ScrollActivityFlags.Programmatic;
        float halflife = cmd.B > 0f ? cmd.B
            : ScrollPhysics.ProgrammaticHalflifeS(MathF.Abs(target - b.PositionMain), _feel.ProgrammaticMinHalflifeMs, _feel.ProgrammaticMaxHalflifeMs, _feel.ProgrammaticShortDip, _feel.ProgrammaticLongDip);
        b.DrivenHalflifeMs = halflife;
        b.DrivenZeta = cmd.C;
        b.DrivenOmega = cmd.D;
        b.DrivenSettleVel = cmd.E;
        MarkActive(idx);
        MarkTouched(idx);
    }

    private void ApplySetVelocity(in ScrollInput cmd)
    {
        if (!TryGetBoundRef(cmd.Node, out int idx)) return;
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
        MarkActive(idx);
        MarkTouched(idx);
    }

    // ── Edge resolution (Reclamp) ─────────────────────────────────────────────────────────────────────────────

    private void ResolveEdge(int node)
    {
        ref ScrollBody b = ref _bodies[node];
        if (!b.EdgeHitPending) return;
        float zoom = b.Zoom > 0f ? b.Zoom : 1f;
        float maxOff = MathF.Max(0f, b.Frame.ExtentMain * zoom - b.Frame.ViewportMain);
        float pos = b.PositionMain;
        bool stillAtEdge = pos <= 0.0001f || pos >= maxOff - 0.0001f;
        b.EdgeHitPending = false;

        if (!stillAtEdge)
        {
            MarkTouched(node); // fresh geometry gave it room — Ballistic simply continues next Tick
            return;
        }

        float v = b.Velocity;
        float excessSign = pos <= 0.0001f ? -1f : 1f;
        if (b.ChainParent >= 0 && CanChainAbsorb(b.ChainParent, excessSign))
        {
            int parentNode = b.ChainParent;
            b.Activity = ScrollActivity.Idle;
            b.Velocity = 0f;
            b.Awake = false;
            ref ScrollBody parent = ref _bodies[parentNode];
            parent.Activity = ScrollActivity.Ballistic;
            parent.Velocity = v;
            // No per-viewport snap retarget for a chain hand-off (the child's edge, not the parent's, is what fired)
            // — but a stale True from an EARLIER fling on this same parent body must not leak in and chase a
            // long-settled Target (the exact class of bug ScrollPhysics.SnapLandEpsPx's doc covers for the seed path).
            parent.SnapArmed = false;
            parent.Awake = false;
            parent.Flags &= ~(ScrollActivityFlags.Wheel | ScrollActivityFlags.Programmatic | ScrollActivityFlags.Autoscroll | ScrollActivityFlags.Bouncing);
            MarkActive(parentNode);
            MarkTouched(parentNode);
        }
        else if (MathF.Abs(v) >= _feel.FlingSettleVel)
        {
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
        MarkTouched(node);
    }

    // ── Contact history (fixed 5-slot, chronological T0/X0=oldest .. T4/X4=newest within ContactCount) ─────────

    private static void PushHistory(ref ScrollBody b, double t, float x)
    {
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

    private void MarkActive(int node)
    {
        if (node < 0 || node >= _inActive.Length || _inActive[node]) return;
        _inActive[node] = true;
        if (_activeCount >= _activeList.Length) Array.Resize(ref _activeList, _activeList.Length * 2);
        _activeList[_activeCount++] = node;
    }

    private void MarkTouched(int node)
    {
        if (node < 0 || node >= _touchedStamp.Length || _touchedStamp[node] == _stamp) return;
        _touchedStamp[node] = _stamp;
        if (_touchedCount >= _touchedNodes.Length) Array.Resize(ref _touchedNodes, _touchedNodes.Length * 2);
        _touchedNodes[_touchedCount++] = node;
    }

    private void EmitTouched(ScrollWriteSource writer)
    {
        for (int i = 0; i < _touchedCount; i++)
        {
            int node = _touchedNodes[i];
            ref ScrollBody b = ref _bodies[node];
            if (!b.Bound) continue;
            ScrollWrite write = BuildWrite(in b, writer);
            _sink.Apply(node, in write);
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
        for (int r = 0; r < _activeCount; r++)
        {
            int node = _activeList[r];
            ref ScrollBody b = ref _bodies[node];
            bool keep = b.Bound && (!b.IsSettled || b.Parked || b.RestorePending || b.EdgeHitPending);
            if (keep) _activeList[w++] = node;
            else _inActive[node] = false;
        }
        _activeCount = w;
    }

    private void UpdateSummary()
    {
        bool anyMoved = _touchedCount > 0;
        bool anyUserActive = false, anyDragOrBallistic = false;
        float maxSpeed = 0f;
        for (int i = 0; i < _activeCount; i++)
        {
            ref ScrollBody b = ref _bodies[_activeList[i]];
            if (!b.Bound) continue;
            bool userActive = b.Activity == ScrollActivity.Drag || b.Activity == ScrollActivity.Ballistic
                || (b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Programmatic) == 0);
            if (userActive) anyUserActive = true;
            if (b.Activity == ScrollActivity.Drag || b.Activity == ScrollActivity.Ballistic) anyDragOrBallistic = true;
            float speed = MathF.Abs(b.VelocityMain);
            if (speed > maxSpeed) maxSpeed = speed;
        }
        Summary = new ScrollFrameSummary(anyMoved, anyUserActive, anyDragOrBallistic, _activeCount, maxSpeed);
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
