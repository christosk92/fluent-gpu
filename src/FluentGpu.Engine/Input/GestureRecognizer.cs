using FluentGpu.Foundation;

namespace FluentGpu.Input;

// ── Per-pointer FSM + velocity sampler (input-a11y.md §7B) ─────────────────────────────────────────────────
// Standalone units (no dispatcher integration yet). GestureRecognizer runs a per-(PointerId, recognizer) FSM over the
// coalesced pointer stream; each FSM is an ARENA MEMBER (§7A) and produces ArenaVotes, NOT events — recognized gestures
// emit their bubble events only after the arena declares the FSM the winner. The struct shape (PointerFsm + its
// VelocitySampler) is canon-registered alongside §7A at SPEC-INDEX.md:63 and implemented AS WRITTEN. Gesture structs are
// re-typed off the as-built Point2 (the spec's forward-looking Vec2 name; Reactor's Windows.Foundation.Point is gone),
// consistent with the §3 InputEvent narrowing that ships PositionPx as a Point2.
//
// Zero managed alloc on the per-event path: the sampler is a fixed inline ring (no heap), the FSM is an unmanaged struct
// held in a parallel bank alongside the arena's members (reached by ArenaMember.FsmSlot). Thresholds reuse the
// InputDispatcher constants already in this assembly (PanSlopPx = SM_CXDRAG analogue, DoubleClickMs = GetDoubleClickTime
// analogue, FlingMinVelocityPxPerS); the long-press promotion uses HoldMs (~500ms, WinUI InteractionContext is
// closed-source so the live value is approximated and tunable).

/// <summary>The recognizer FSM phase (§7B). Idle → Pressed on down; Pressed → Tapping/Dragging/Manipulating as the
/// stream resolves the kind; a swept loser resets to Idle (the synthetic GestureRejected, §7A.5).</summary>
internal enum GesturePhase : byte { Idle, Pressed, Tapping, Dragging, Manipulating }

/// <summary>Outlier-trimmed velocity sampler: a fixed ring of the most recent (dtMs, dPos) samples over a ~50ms horizon,
/// for the touch fling hand-off (§7B inertia integrator). Adapts the <see cref="DragController.UpdateVelocity"/> EMA into
/// the per-spec RING shape — the window is summed (Σdpos / Σdt), the single largest-magnitude sample is trimmed as an
/// outlier when the ring is full, and a 0 / duplicate platform timestamp (the headless default) contributes nothing, so
/// a 0-stamp gesture measures zero velocity (a vacuous fling — the harness uses monotonic stamps to exercise inertia).
/// Fully inline (a fixed buffer of value tuples) — no heap, copied by value inside the FSM struct.</summary>
internal struct VelocitySampler
{
    /// <summary>Ring depth — enough to span the ~50ms horizon at a 60-120Hz sample rate while trimming one outlier.</summary>
    public const int Capacity = 8;

    /// <summary>The sampling horizon (ms): samples older than this (walking back from the newest) are excluded from the
    /// summed velocity, so a long pause before release does not dilute the flick speed (§7B "last ~50 ms of samples").</summary>
    public const float HorizonMs = 50f;

    // Inline fixed ring (zero heap). Parallel arrays of the per-sample dt and displacement; a single inline-array field
    // would need C# 12 [InlineArray] on a named struct — the parallel fixed-size buffers below are the equivalent with no
    // extra type and identical zero-alloc behavior.
    private SampleRing _dtMs;
    private SampleRing _dx;
    private SampleRing _dy;
    private int _count;        // live samples (saturates at Capacity)
    private int _head;         // next write index (ring)
    private Point2 _lastAbs;
    private uint _lastMs;
    private bool _have;        // a prior absolute sample exists (so the first Sample only seeds the anchor)

    /// <summary>Seed the anchor (the gesture-down position/time); clears the ring. Velocity is 0 until the next sample.</summary>
    public void Reset(Point2 abs, uint timestampMs)
    {
        _count = 0; _head = 0;
        _lastAbs = abs; _lastMs = timestampMs;
        _have = true;
    }

    /// <summary>Push one absolute sample. A 0 / duplicate / backwards / >1s-gap timestamp contributes no ring entry (it
    /// only re-anchors), so degenerate stamps measure zero velocity. Otherwise the (dtMs, dPos) since the last sample is
    /// pushed into the ring (overwriting the oldest when full).</summary>
    public void Sample(Point2 abs, uint timestampMs)
    {
        if (!_have) { Reset(abs, timestampMs); return; }
        uint dt = timestampMs - _lastMs;
        if (timestampMs != 0 && _lastMs != 0 && dt > 0 && dt < 1000)
        {
            _dtMs[_head] = dt;
            _dx[_head] = abs.X - _lastAbs.X;
            _dy[_head] = abs.Y - _lastAbs.Y;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
        if (timestampMs != 0) _lastMs = timestampMs;
        _lastAbs = abs;
    }

    /// <summary>The summed, outlier-trimmed velocity over the ~50ms horizon (px/s). Walks the ring newest→oldest, sums
    /// Σdpos / Σdt within <see cref="HorizonMs"/>, and (when the ring is full) drops the single sample whose speed is the
    /// largest outlier from the running mean. 0 when fewer than 2 samples or the window is degenerate.</summary>
    public readonly Point2 Velocity()
    {
        if (_count == 0) return Point2.Zero;

        // Newest→oldest within the horizon; identify the largest-magnitude per-sample speed to trim as the outlier.
        float sumDt = 0f, sumDx = 0f, sumDy = 0f;
        float trimDt = 0f, trimDx = 0f, trimDy = 0f;
        float worstSpeedSq = -1f;
        int used = 0;
        for (int k = 0; k < _count; k++)
        {
            int idx = (_head - 1 - k + Capacity * 2) % Capacity;
            float dt = _dtMs[idx];
            if (dt <= 0f) continue;
            sumDt += dt; sumDx += _dx[idx]; sumDy += _dy[idx];
            used++;
            if (sumDt >= HorizonMs) break;   // horizon reached (inclusive of the sample that crosses it)

            float instSq = (_dx[idx] * _dx[idx] + _dy[idx] * _dy[idx]) / (dt * dt);
            if (instSq > worstSpeedSq) { worstSpeedSq = instSq; trimDt = dt; trimDx = _dx[idx]; trimDy = _dy[idx]; }
        }
        // Trim the outlier only when it leaves a non-degenerate window (≥2 contributing samples and dt remaining).
        if (used >= 3 && _count >= Capacity && sumDt - trimDt > 0f)
        { sumDt -= trimDt; sumDx -= trimDx; sumDy -= trimDy; }

        if (sumDt <= 0f) return Point2.Zero;
        return new Point2(sumDx * 1000f / sumDt, sumDy * 1000f / sumDt);
    }

    /// <summary>A <see cref="VelocitySampler.Capacity"/>-wide inline fixed buffer of floats (the ring backing). The
    /// C# 12 <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/> keeps the whole <see cref="VelocitySampler"/>
    /// a blittable value with zero heap (no array allocation), indexable like an array.</summary>
    [System.Runtime.CompilerServices.InlineArray(Capacity)]
    private struct SampleRing
    {
        private float _e0;
    }
}

/// <summary>The per-(PointerId, recognizer) FSM (§7B). One per arena member, in the parallel FSM bank reached by
/// <see cref="ArenaMember.FsmSlot"/>. It consumes the coalesced pointer stream and produces an <see cref="ArenaVote"/>
/// (NOT events): <see cref="Start"/> is buffered so a deferred winner reports the DOWN position (§7A.5);
/// <see cref="TapCount"/> accumulates double/triple taps; <see cref="Velocity"/> feeds the fling. The per-kind transition
/// logic is <see cref="OnDown"/>/<see cref="OnMove"/>/<see cref="OnUp"/>/<see cref="OnFrameTick"/> (the long-press timer
/// tick). All blittable — zero heap; held by value in the bank.</summary>
internal struct PointerFsm
{
    /// <summary>The drag/pan/selection slop (px) that promotes a movement recognizer to <see cref="ArenaVote.EagerAccept"/>
    /// — the SM_CXDRAG analogue; reuses <see cref="InputDispatcher.PanSlopPx"/> so the FSM and the Phase-1 single
    /// recognizer agree on the threshold.</summary>
    public const float SlopPx = InputDispatcher.PanSlopPx;

    /// <summary>The double/triple-tap accumulation window (ms) — the GetDoubleClickTime analogue; reuses
    /// <see cref="InputDispatcher.DoubleClickMs"/>.</summary>
    public const uint DoubleClickMs = InputDispatcher.DoubleClickMs;

    /// <summary>Long-press promotion threshold (µs): a Hold recognizer still down and within slop this long promotes its
    /// vote to <see cref="ArenaVote.EagerAccept"/> (§7B — the timer fires in OnFrameEnd). ~500ms; WinUI's live value is
    /// in the closed-source InteractionContext, so this is approximated and tunable.</summary>
    public const long HoldUs = 500_000;

    public GesturePhase Phase;     // Idle, Pressed, Tapping, Dragging, Manipulating
    public GestureKind Kind;       // which recognizer this FSM implements (arena-member identity)
    public Point2 Start, Last;     // Start buffered so deferred resolution reports the DOWN position (§7A.5)
    public long DownTimeUs, LastMoveUs;
    public int TapCount;           // double/triple-tap accumulation
    public VelocitySampler Velocity;   // ring of recent (dt, dPos) for the fling
    public ArenaVote Vote;         // the FSM's current vote into its arena (§7A)

    /// <summary>Initialize this FSM as a fresh member for <paramref name="kind"/> (called at enrollment). Idle/Pending.</summary>
    public void Init(GestureKind kind)
    {
        Phase = GesturePhase.Idle;
        Kind = kind;
        Start = default; Last = default;
        DownTimeUs = 0; LastMoveUs = 0;
        TapCount = 0;
        Velocity = default;
        Vote = ArenaVote.Pending;
    }

    /// <summary>Reset to Idle on a synthetic GestureRejected (a swept loser, §7A.5): the FSM emits nothing and rearms its
    /// vote to Pending for any future gesture. Keeps <see cref="Kind"/> (the member identity is stable).</summary>
    public void Reset()
    {
        Phase = GesturePhase.Idle;
        Start = default; Last = default;
        DownTimeUs = 0; LastMoveUs = 0;
        TapCount = 0;
        Velocity = default;
        Vote = ArenaVote.Pending;
    }

    /// <summary>PointerDown (§7B). Buffers the DOWN position, seeds the velocity ring, and casts the kind's opening vote:
    /// most kinds stay <see cref="ArenaVote.Pending"/> (waiting on up or slop); <see cref="GestureKind.Pinch"/> stays
    /// Pending until a SECOND contact arrives (<see cref="OnSecondContact"/>); a DoubleTap accumulates its tap count
    /// across the inter-tap window (the arena's Held flag keeps it open). Returns the resulting vote.</summary>
    public ArenaVote OnDown(Point2 abs, long timeUs)
    {
        // Double/triple-tap accumulation: a second down inside the window (since the prior down) bumps the count.
        bool withinWindow = DownTimeUs != 0 && (timeUs - DownTimeUs) <= (long)DoubleClickMs * 1000
                            && Near(abs, Start, SlopPx);
        if ((Kind == GestureKind.DoubleTap || Kind == GestureKind.SelectionDrag) && withinWindow) TapCount++;
        else TapCount = 1;

        Phase = GesturePhase.Pressed;
        Start = abs; Last = abs;
        DownTimeUs = timeUs; LastMoveUs = timeUs;
        Velocity.Reset(abs, ToMs(timeUs));
        Vote = ArenaVote.Pending;
        return Vote;
    }

    /// <summary>PointerMove (§7B). Samples velocity; a movement recognizer (Drag/Pan/SelectionDrag/DragReorder) that
    /// crosses <see cref="SlopPx"/> promotes to <see cref="ArenaVote.EagerAccept"/> and enters Dragging — buffering the
    /// DOWN position so the deferred *Started reports it; a Tap/DoubleTap/RightTap/Hold that moves past slop REJECTS
    /// itself (it is no longer that gesture). Returns the resulting vote.</summary>
    public ArenaVote OnMove(Point2 abs, long timeUs)
    {
        Velocity.Sample(abs, ToMs(timeUs));
        Last = abs; LastMoveUs = timeUs;

        bool past = !Near(abs, Start, SlopPx);
        switch (Kind)
        {
            case GestureKind.Drag:
            case GestureKind.Pan:
            case GestureKind.SelectionDrag:
            case GestureKind.DragReorder:
                if (past) { Phase = GesturePhase.Dragging; Vote = ArenaVote.EagerAccept; }
                break;
            case GestureKind.Tap:
            case GestureKind.DoubleTap:
            case GestureKind.RightTap:
            case GestureKind.Hold:
                if (past) { Phase = GesturePhase.Idle; Vote = ArenaVote.Reject; TapCount = 0; }   // moved → not this gesture (dead)
                break;
            case GestureKind.Pinch:
                break;   // pinch is driven by a second contact, not movement (OnSecondContact)
        }
        return Vote;
    }

    /// <summary>PointerUp (§7B). A within-slop Tap/RightTap votes <see cref="ArenaVote.Accept"/> (a clean tap resolves on
    /// the up-sweep); a DoubleTap that has reached its tap count Accepts, else stays Pending so the arena's Held window
    /// can wait for the next tap; a Dragging manipulation Accepts (the gesture completes). Returns the resulting vote.</summary>
    public ArenaVote OnUp(Point2 abs, long timeUs)
    {
        Velocity.Sample(abs, ToMs(timeUs));
        Last = abs;
        bool withinSlop = Near(abs, Start, SlopPx);
        switch (Kind)
        {
            case GestureKind.Tap:
            case GestureKind.RightTap:
                if (withinSlop) { Phase = GesturePhase.Tapping; Vote = ArenaVote.Accept; }
                else Vote = ArenaVote.Reject;
                break;
            case GestureKind.DoubleTap:
                if (withinSlop && TapCount >= 2) { Phase = GesturePhase.Tapping; Vote = ArenaVote.Accept; }
                else if (withinSlop) { Phase = GesturePhase.Pressed; Vote = ArenaVote.Pending; }   // await the 2nd tap (Held)
                else Vote = ArenaVote.Reject;
                break;
            case GestureKind.Drag:
            case GestureKind.Pan:
            case GestureKind.SelectionDrag:
            case GestureKind.DragReorder:
                if (Phase == GesturePhase.Dragging) Vote = ArenaVote.Accept;   // completed manipulation
                else Vote = ArenaVote.Reject;                                  // never crossed slop → not a drag
                break;
            case GestureKind.Hold:
                Vote = ArenaVote.Reject;   // up before the long-press timer fired → not a hold
                break;
            case GestureKind.Pinch:
                Vote = ArenaVote.Reject;   // a lone contact lifting is not a pinch
                break;
        }
        return Vote;
    }

    /// <summary>A second contact arrived (§7B — Pinch's EagerAccept trigger; full pinch wiring is Phase 4). A Pinch
    /// recognizer promotes to <see cref="ArenaVote.EagerAccept"/> and enters Manipulating. Other kinds ignore it. The
    /// signature takes the second contact's position so Phase 4 can seed the pinch focal point/initial span.</summary>
    public ArenaVote OnSecondContact(Point2 secondAbs, long timeUs)
    {
        if (Kind == GestureKind.Pinch)
        {
            Phase = GesturePhase.Manipulating;
            LastMoveUs = timeUs;
            Vote = ArenaVote.EagerAccept;
        }
        return Vote;
    }

    /// <summary>The OnFrameEnd-style timer tick (§7B): a Hold recognizer still Pressed and within slop for ≥
    /// <see cref="HoldUs"/> promotes its vote to <see cref="ArenaVote.EagerAccept"/> (the long-press fires). Returns the
    /// resulting vote (unchanged for non-Hold or before the threshold). <paramref name="nowUs"/> is the frame time.</summary>
    public ArenaVote OnFrameTick(long nowUs)
    {
        if (Kind == GestureKind.Hold && Phase == GesturePhase.Pressed
            && Vote == ArenaVote.Pending && DownTimeUs != 0 && (nowUs - DownTimeUs) >= HoldUs)
        {
            Phase = GesturePhase.Manipulating;
            Vote = ArenaVote.EagerAccept;
        }
        return Vote;
    }

    private static bool Near(Point2 a, Point2 b, float slop)
        => MathF.Abs(a.X - b.X) <= slop && MathF.Abs(a.Y - b.Y) <= slop;

    /// <summary>Microseconds → the millisecond clock the <see cref="VelocitySampler"/> ring uses (the platform stamp on
    /// the InputEvent is ms; the FSM keeps µs for the long-press resolution, so the sampler gets the ms projection).</summary>
    private static uint ToMs(long timeUs) => (uint)(timeUs / 1000);
}
