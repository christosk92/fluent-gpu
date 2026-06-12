using System.Text;
using FluentGpu.Foundation;

namespace FluentGpu.Input;

// ── The gesture-arena replay recorder (validation.md §12.6 — the L2 arena-determinism gate) ──────────────────
// A lightweight, OPT-IN trace of the arbitration the GestureArena performs: every arena OPEN, each member ENROLL (in
// enrollment order), every VOTE transition, each RESOLUTION winner, and the SWEEP order of the losers. validation.md
// §12.6 demands an EXACT golden over "the arena resolution trace (ordered accept/reject events) + the winner" — this
// is that ordered ledger, reified so the §12.6 gate (Program.cs gate.arena.determinism) can assert two runs of one
// scripted multi-pointer sequence produce a BIT-IDENTICAL trace, and the §12.6/validation.md:1145 integrator sweep
// can assert the same fling target resolves to an identical RESOLUTION trace across dt ∈ {8.33, 16.67, 33.3} ms.
//
// COST MODEL — matches the engine diagnostics discipline (Diag / WakeDiagnostics): the recorder is attached through a
// single nullable field on GestureArena (Recorder), and every record site is a `_recorder?.X(...)` null-guard — the
// SAME shape the arena's OnMemberRejected/OnMemberWon sinks already use, so when no recorder is attached (production
// and every non-determinism check) the arbitration pays one predictable null-check per mutation and allocates nothing.
// Recording itself is zero-alloc after construction: entries land in a fixed slab reused across Reset(); the trace
// string is materialized only when a gate ASKS for it (Signature), never on the hot path. This is a TEST/DEBUG seam —
// the dispatcher never attaches one in the host; the harness wires it onto InputDispatcher.Arena for the gate.

/// <summary>One ordered event in an arena replay trace (validation.md §12.6). A blittable POD in the recorder's fixed
/// slab — the (<see cref="Op"/>, ids) tuple is everything the determinism golden compares (it deliberately omits
/// positions/offsets: §12.6 pins the *arbitration* — winner + accept/reject order — not the per-dt geometry, so a
/// fling that resolves identically at different timesteps yields an identical trace even as offsets diverge).</summary>
internal readonly struct ArenaTraceEntry
{
    public readonly ArenaTraceOp Op;
    public readonly uint PointerId;     // the arena's contact id (groups events per pointer in a multi-pointer script)
    public readonly int MemberSlot;     // the member this event concerns (-1 for arena-level Open/Close events)
    public readonly GestureKind Kind;   // the member's recognizer kind (mirrored so the trace reads without the arena)
    public readonly ArenaVote Vote;     // the new vote for a Vote transition; the winner's vote for Win; Reject for a sweep loser

    public ArenaTraceEntry(ArenaTraceOp op, uint pointerId, int memberSlot, GestureKind kind, ArenaVote vote)
    {
        Op = op; PointerId = pointerId; MemberSlot = memberSlot; Kind = kind; Vote = vote;
    }
}

/// <summary>The kind of an <see cref="ArenaTraceEntry"/> (validation.md §12.6 ordered ledger). The order of these
/// events as they land in the slab IS the determinism signature: Open → Enroll* → (Vote | Win | Sweep)*.</summary>
internal enum ArenaTraceOp : byte
{
    Open,       // an arena opened for a PointerId (§7A.1)
    Enroll,     // a member enrolled (innermost-first; the ledger preserves enrollment order, §7A.1)
    Team,       // a selection team bound over a member run (§7A.3)
    Close,      // the arena latched closed — no new members (§7A.1, first PointerMove past slop)
    Vote,       // a member's vote TRANSITIONED to a new value (only transitions are logged — §7A.2)
    Win,        // a member won the arena (the resolution winner — §7A.2)
    Sweep,      // a loser was swept with the synthetic GestureRejected, in sweep order (§7A.2/§7A.5)
    Free,       // the arena seat was reclaimed (contact ended)
}

/// <summary>The arena replay recorder (validation.md §12.6). Attached to a <see cref="GestureArena"/> via its
/// <see cref="GestureArena.Recorder"/> field; the arena calls the <c>On*</c> sinks at each arbitration point. Holds a
/// fixed-capacity slab of <see cref="ArenaTraceEntry"/> reused across <see cref="Reset"/> — zero heap after
/// construction. <see cref="Signature"/> renders the ordered ledger to a stable string the determinism gate compares
/// for bit-identity (winner ids, vote order, sweep order). A test/debug seam: nothing in the host attaches one.</summary>
internal sealed class GestureArenaRecorder
{
    /// <summary>Trace-entry cap. A scripted multi-pointer determinism sequence (tap, double-tap, hold, reorder, swipe,
    /// pan+fling) produces a bounded ledger; this is comfortably above its length. Overflow is dropped (the trace stays
    /// deterministic — the same script always fills the same prefix) and flagged via <see cref="Overflowed"/>.</summary>
    public const int Capacity = 4096;

    private readonly ArenaTraceEntry[] _entries = new ArenaTraceEntry[Capacity];
    private int _count;

    /// <summary>True if the script exceeded <see cref="Capacity"/> (the trace is truncated). A determinism gate should
    /// assert this is false so it is comparing complete ledgers.</summary>
    public bool Overflowed { get; private set; }

    public int Count => _count;

    /// <summary>Clear the ledger to reuse the slab for the next run (the §12.6 gate records the SAME script twice and
    /// diffs the two signatures — it Resets between runs). Zero alloc: the backing array is retained.</summary>
    public void Reset() { _count = 0; Overflowed = false; }

    private void Append(in ArenaTraceEntry e)
    {
        if (_count >= Capacity) { Overflowed = true; return; }
        _entries[_count++] = e;
    }

    // ── the arena's record sinks (called from GestureArena at each arbitration point; all zero-alloc) ─────────
    public void OnOpen(uint pointerId) => Append(new ArenaTraceEntry(ArenaTraceOp.Open, pointerId, -1, default, default));
    public void OnEnroll(uint pointerId, int memberSlot, GestureKind kind)
        => Append(new ArenaTraceEntry(ArenaTraceOp.Enroll, pointerId, memberSlot, kind, ArenaVote.Pending));
    public void OnTeam(uint pointerId, int captainSlot)
        => Append(new ArenaTraceEntry(ArenaTraceOp.Team, pointerId, captainSlot, default, default));
    public void OnClose(uint pointerId) => Append(new ArenaTraceEntry(ArenaTraceOp.Close, pointerId, -1, default, default));
    public void OnVote(uint pointerId, int memberSlot, GestureKind kind, ArenaVote vote)
        => Append(new ArenaTraceEntry(ArenaTraceOp.Vote, pointerId, memberSlot, kind, vote));
    public void OnWin(uint pointerId, int memberSlot, GestureKind kind, ArenaVote vote)
        => Append(new ArenaTraceEntry(ArenaTraceOp.Win, pointerId, memberSlot, kind, vote));
    public void OnSweep(uint pointerId, int memberSlot, GestureKind kind)
        => Append(new ArenaTraceEntry(ArenaTraceOp.Sweep, pointerId, memberSlot, kind, ArenaVote.Reject));
    public void OnFree(uint pointerId) => Append(new ArenaTraceEntry(ArenaTraceOp.Free, pointerId, -1, default, default));

    /// <summary>Render the ordered ledger to a stable, human-readable signature (validation.md §12.6 "exact golden over
    /// the arena resolution trace"). One line per event: <c>op pointer member kind vote</c>. Member slots are reported
    /// RELATIVE to each arena's first member (its <c>MemberOffset</c>), so two runs that happen to take different cap-10
    /// seats still produce identical text — the determinism property is about WHICH recognizer wins and the accept/reject
    /// ORDER, not the absolute slab index. Materialized only when a gate calls this (never on the hot path).</summary>
    public string Signature()
    {
        var sb = new StringBuilder(_count * 24);
        // Per-pointer member-slot base, so slots print relative to the arena's first enrolled member (seat-independent).
        // A pointer's base is the slot of its FIRST Enroll; absent (arena-level events before any enroll) prints -1.
        for (int i = 0; i < _count; i++)
        {
            ref readonly ArenaTraceEntry e = ref _entries[i];
            int rel = e.MemberSlot < 0 ? -1 : e.MemberSlot - BaseSlotFor(e.PointerId);
            sb.Append(OpName(e.Op)).Append(' ')
              .Append('p').Append(e.PointerId).Append(' ')
              .Append('m').Append(rel).Append(' ')
              .Append(KindName(e.Kind)).Append(' ')
              .Append(VoteName(e.Vote)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The slab index of <paramref name="pointerId"/>'s first enrolled member (its arena's MemberOffset), so
    /// <see cref="Signature"/> can print seat-independent relative slots. -1 if the pointer enrolled nothing.</summary>
    private int BaseSlotFor(uint pointerId)
    {
        for (int i = 0; i < _count; i++)
            if (_entries[i].Op == ArenaTraceOp.Enroll && _entries[i].PointerId == pointerId)
                return _entries[i].MemberSlot;
        return 0;   // no enroll for this pointer ⇒ no member events to offset; the -1 sentinel prints as-is above
    }

    private static string OpName(ArenaTraceOp op) => op switch
    {
        ArenaTraceOp.Open => "OPEN",
        ArenaTraceOp.Enroll => "ENROLL",
        ArenaTraceOp.Team => "TEAM",
        ArenaTraceOp.Close => "CLOSE",
        ArenaTraceOp.Vote => "VOTE",
        ArenaTraceOp.Win => "WIN",
        ArenaTraceOp.Sweep => "SWEEP",
        ArenaTraceOp.Free => "FREE",
        _ => "?",
    };

    private static string KindName(GestureKind k) => k switch
    {
        GestureKind.Tap => "Tap",
        GestureKind.DoubleTap => "DoubleTap",
        GestureKind.RightTap => "RightTap",
        GestureKind.Hold => "Hold",
        GestureKind.Drag => "Drag",
        GestureKind.Pan => "Pan",
        GestureKind.Pinch => "Pinch",
        GestureKind.SelectionDrag => "SelectionDrag",
        GestureKind.DragReorder => "DragReorder",
        _ => "?",
    };

    private static string VoteName(ArenaVote v) => v switch
    {
        ArenaVote.Pending => "Pending",
        ArenaVote.Accept => "Accept",
        ArenaVote.Reject => "Reject",
        ArenaVote.EagerAccept => "EagerAccept",
        _ => "?",
    };

    /// <summary>Just the RESOLUTION sub-trace (validation.md:1145 integrator sweep): the ordered Win + Sweep events,
    /// dropping Open/Enroll/Vote noise. The §12.6 fling-determinism sweep compares THIS across dt ∈ {8.33,16.67,33.3} ms
    /// — the per-dt vote cadence can differ (more or fewer move samples), but the arbitration RESULT (who wins, in what
    /// order the losers are swept) must be identical. Seat-independent relative slots, same as <see cref="Signature"/>.</summary>
    public string ResolutionSignature()
    {
        var sb = new StringBuilder(64);
        for (int i = 0; i < _count; i++)
        {
            ref readonly ArenaTraceEntry e = ref _entries[i];
            if (e.Op != ArenaTraceOp.Win && e.Op != ArenaTraceOp.Sweep) continue;
            int rel = e.MemberSlot < 0 ? -1 : e.MemberSlot - BaseSlotFor(e.PointerId);
            sb.Append(OpName(e.Op)).Append(' ')
              .Append('p').Append(e.PointerId).Append(' ')
              .Append('m').Append(rel).Append(' ')
              .Append(KindName(e.Kind)).Append('\n');
        }
        return sb.ToString();
    }
}
