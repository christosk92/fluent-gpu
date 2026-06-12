using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

// The §7A/§7B gesture types are canon-internal (the doc spells every struct `internal`); the vertical-slice harness
// drives them directly as standalone units (gate.arena.* / the FSM unit checks), so it needs internals access. This is
// the same test-bridge the slice uses for other internal seams — no production surface is widened.
[assembly: InternalsVisibleTo("FluentGpu.VerticalSlice")]

namespace FluentGpu.Input;

// ── The gesture arena (input-a11y.md §7A — the coordinator above PointerFsm) ──────────────────────────────
// Standalone unit (no dispatcher integration yet). The arena is the Flutter GestureArenaManager model — "first to
// accept, or last to not reject, wins" — reimplemented over the per-pointer FSM votes (§7B). One arena per active
// PointerId, opened on PointerDown, closed on resolution or the PointerUp-sweep. Members (recognizers wanting the
// pointer) are enrolled along the already-computed reverse-z route, INNERMOST-first, so the deepest hit node's
// recognizers get the earliest claim (Flutter child-before-parent; WinUI inner-control-wins). Resolution can defer
// across pointer-move frames; the common single-recognizer case still resolves synchronously (last-standing of one).
//
// Storage is slab-backed with ZERO per-frame heap (§7A.4): members/states/teams live in fixed arrays sized to the
// cap-10 contact model (the InputDispatcher PointerSlot[] precedent), the per-arena member list is an (offset,len)
// span, votes are in-place mutations, and a synthetic GestureRejected is a vote write + an FSM Reset — never an
// allocation. The struct shapes (ArenaVote / GestureKind / ArenaMember / GestureArenaState / ArenaTeam) are
// canon-registered at SPEC-INDEX.md:63 and implemented AS WRITTEN.

/// <summary>An arena member's running verdict (§7A.1). <see cref="EagerAccept"/> = "I win NOW, sweep the rest" — a
/// movement-slop cross or a second-contact pinch; it resolves the arena mid-stream without waiting for PointerUp.</summary>
internal enum ArenaVote : byte { Pending, Accept, Reject, EagerAccept }

/// <summary>The recognizer a <see cref="PointerFsm"/> implements — the arena-member identity (§7A.1). A byte enum (not
/// flags): one member is exactly one kind; a control wanting several presents several members (a selection
/// <see cref="ArenaTeam"/> binds tap/double-tap/triple-tap/drag-extend under one captain, §7A.3).</summary>
internal enum GestureKind : byte
{
    Tap,
    DoubleTap,
    RightTap,
    Hold,
    Drag,
    Pan,
    Pinch,
    SelectionDrag,
    DragReorder,
}

/// <summary>One recognizer enrolled in an arena (§7A.1). Slab-backed; the arena holds an (offset,len) span of these.
/// <see cref="Vote"/> is mutated in place as the <see cref="PointerFsm"/> at <see cref="FsmSlot"/> advances;
/// <see cref="Priority"/> breaks an eager/accept tie (innermost enrollment first, document order next).</summary>
internal struct ArenaMember
{
    public NodeHandle Node;     // the route node that owns this recognizer
    public GestureKind Kind;    // Tap | DoubleTap | RightTap | Hold | Drag | Pan | Pinch | SelectionDrag | DragReorder
    public ArenaVote Vote;      // updated as PointerFsm advances (in-place mutation — no per-frame heap)
    public int FsmSlot;         // index of the PointerFsm driving this member (its slot in the FSM backing store)
    public byte Priority;       // tie-break when two members resolve in one frame (innermost wins; doc-order next)
}

/// <summary>One arena, keyed by <see cref="PointerId"/> (§7A.1). Slab-backed; <see cref="MemberOffset"/>/<see
/// cref="MemberLen"/> are a span into the member backing store. <see cref="WinnerSlot"/> is -1 until resolved;
/// <see cref="Closed"/> latches after the first PointerMove past slop (no new members thereafter); <see cref="Held"/>
/// keeps the arena open across the double-tap inter-tap window.</summary>
internal struct GestureArenaState
{
    public uint PointerId;
    public int MemberOffset, MemberLen;   // span into the ArenaMember backing store
    public int WinnerSlot;                // -1 until resolved (a member offset, or -1)
    public bool Closed;                   // no new members after the first PointerMove past slop
    public bool Held;                     // a member requested hold (double-tap second-down wait)
    public long OpenedUs;
}

/// <summary>A team of sibling recognizers on one logical control that present a single arena entry and don't reject
/// internally until the team as a whole loses (§7A.3 — Flutter GestureArenaTeam; the canonical use is selection). The
/// <see cref="CaptainSlot"/> member votes for the team; on a team win the captain decides which internal recognizer
/// fires from tap-count + movement.</summary>
internal struct ArenaTeam
{
    public int CaptainSlot;               // the ArenaMember slot that votes for the team
    public int MemberOffset, MemberLen;   // internal team members (tap/dbltap/tripletap/drag-extend)
}

/// <summary>The gesture-arena coordinator (§7A): one arena per active PointerId over the cap-10 contact model, with
/// slab-backed member/state/team storage and the exact §7A.2 resolution rule (eager-win / first-accept-when-closed /
/// last-standing / pointer-up sweep / hold-release for double-tap). Allocates ZERO per-frame heap after construction:
/// arenas/members/teams are fixed-array seats, (offset,len) spans, in-place vote mutations, and synthetic
/// GestureRejected = a vote write + the FSM driver's <c>Reset</c> callback. The FSM bank is held alongside so a
/// swept loser resets to Idle; this stage wires the FSM reset through a delegate so <see cref="GestureArena"/> stays a
/// pure coordinator (dispatcher integration is a later stage).</summary>
internal sealed class GestureArena
{
    /// <summary>Concurrent-arena cap = the cap-10 contact model (InputDispatcher.MaxContacts): mouse/pen + up to this
    /// many simultaneous touch points each own one arena. An 11th concurrent contact opens no arena (deterministic, no
    /// growth) — the same hard policy the capture slab enforces.</summary>
    public const int MaxArenas = 10;

    /// <summary>Max recognizers enrolled per arena. The reverse-z route can advertise several gestures, but one pointer
    /// over one control stack contributes a bounded set (tap/double/right/hold + pan/drag/selection/pinch). A fixed
    /// stride keeps each arena's member span at a DETERMINISTIC offset (<c>slot * MaxMembersPerArena</c>) — zero-growth,
    /// span-friendly, the PointerSlot[] precedent. Enrollment past this cap is dropped (innermost already enrolled).</summary>
    public const int MaxMembersPerArena = 12;

    /// <summary>Max teams per arena (§7A.3). One selection team is the common case; a couple suffices.</summary>
    public const int MaxTeamsPerArena = 4;

    // Fixed backing stores (zero per-frame heap; sized once at construction). The member store is strided by arena slot
    // so member-span offsets are deterministic and contiguous (a real (offset,len) span); teams parallel it. States are
    // one-per-seat. FSM state is owned by the recognizer bank (GestureRecognizer.cs) and reached through FsmSlot; the
    // arena only needs to RESET a swept loser, which it does through the injected sink (kept a coordinator).
    private readonly GestureArenaState[] _arenas = new GestureArenaState[MaxArenas];
    private readonly bool[] _arenaUsed = new bool[MaxArenas];
    private readonly ArenaMember[] _members = new ArenaMember[MaxArenas * MaxMembersPerArena];
    private readonly ArenaTeam[] _teams = new ArenaTeam[MaxArenas * MaxTeamsPerArena];
    private readonly int[] _teamLen = new int[MaxArenas];   // live team count per arena slot

    /// <summary>Loser-reset sink: a swept member's <see cref="ArenaMember.FsmSlot"/> is handed back so the owner resets
    /// that FSM to Idle (the synthetic <c>GestureRejected</c>, §7A.5). Defaults to a no-op so the arena drives standalone
    /// in unit tests; the dispatcher wires it to the recognizer bank later. NOT a per-event allocation — set once.</summary>
    public Action<int>? OnMemberRejected;

    /// <summary>Win sink (symmetric to <see cref="OnMemberRejected"/>): the winning member's FsmSlot, so the owner can
    /// grant hard capture and let that FSM emit its bubble events (§7A.2). No-op by default for standalone tests.</summary>
    public Action<int>? OnMemberWon;

    /// <summary>Opt-in replay recorder (validation.md §12.6 — the L2 arena-determinism gate). When attached, the arena
    /// logs every open / enroll / vote-transition / resolution-winner / sweep-loser to an ordered ledger the
    /// determinism gate compares for bit-identity. <c>null</c> in production and every non-determinism check — each
    /// record site is a <c>_recorder?.X()</c> null-guard (the same shape as the win/reject sinks), so the arbitration
    /// pays one predictable null-check per mutation and allocates nothing when off. A test/debug seam only.</summary>
    public GestureArenaRecorder? Recorder;

    public int OpenArenaCount { get; private set; }

    /// <summary>True when ANY open arena is still unresolved (validation.md §12.6 "capture is provisional until
    /// resolution"). Until an arena resolves a winner, the §7A.5 capture granted to its members is TENTATIVE — the
    /// pointer is still being offered to every member for voting. The single-recognizer fast-path resolves
    /// (last-standing of one) the same frame, so capture is NOT tentative for it — exactly the §7A.5 observably-identical
    /// common case the gate.arena.fastpath-sync guard pins.</summary>
    public bool CaptureIsTentative
    {
        get
        {
            for (int i = 0; i < MaxArenas; i++)
                if (_arenaUsed[i] && _arenas[i].WinnerSlot < 0) return true;
            return false;
        }
    }

    // ── opening / enrolling ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open an arena for <paramref name="pointerId"/> (§7A.1, on PointerDown). Returns the arena slot, or -1
    /// when all <see cref="MaxArenas"/> seats are taken by distinct live contacts (the 11th contact opens none —
    /// deterministic, zero-growth). The seat's member span starts empty; <see cref="Enroll"/> appends innermost-first.</summary>
    public int OpenArena(uint pointerId, long openedUs = 0)
    {
        int slot = FindArena(pointerId);
        if (slot >= 0) return slot;        // already open (idempotent on a re-entrant down)
        int free = -1;
        for (int i = 0; i < MaxArenas; i++) if (!_arenaUsed[i]) { free = i; break; }
        if (free < 0) return -1;           // table full of distinct live contacts: open no arena
        _arenaUsed[free] = true;
        _teamLen[free] = 0;
        ref GestureArenaState a = ref _arenas[free];
        a = default;
        a.PointerId = pointerId;
        a.MemberOffset = free * MaxMembersPerArena;
        a.MemberLen = 0;
        a.WinnerSlot = -1;
        a.OpenedUs = openedUs;
        OpenArenaCount++;
        Recorder?.OnOpen(pointerId);
        return free;
    }

    /// <summary>Enroll one recognizer into an open arena (§7A.1, innermost-first along the reverse-z route). Returns the
    /// global member slot (== <see cref="ArenaMember.FsmSlot"/> the owner should bind its FSM to), or -1 if the arena is
    /// closed/full. <see cref="ArenaMember.Priority"/> is the enrollment ordinal (innermost first; doc order next), so a
    /// lower priority wins an eager/accept tie. A new member after the arena CLOSES is rejected (no late entries).</summary>
    public int Enroll(int arenaSlot, NodeHandle node, GestureKind kind)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.Closed || a.MemberLen >= MaxMembersPerArena) return -1;
        int slot = a.MemberOffset + a.MemberLen;
        ref ArenaMember m = ref _members[slot];
        m.Node = node;
        m.Kind = kind;
        m.Vote = ArenaVote.Pending;
        m.FsmSlot = slot;                  // the member IS its FSM seat in the parallel bank (deterministic, zero-growth)
        m.Priority = (byte)a.MemberLen;    // innermost-first enrollment order; doc-order tiebreak follows route order
        a.MemberLen++;
        Recorder?.OnEnroll(a.PointerId, slot, kind);
        return slot;
    }

    /// <summary>Bind a team over a contiguous run of already-enrolled members (§7A.3). The captain votes for the team;
    /// internal members don't reject each other until the team loses. Returns the team index within the arena, or -1.</summary>
    public int EnrollTeam(int arenaSlot, int captainSlot, int memberOffset, int memberLen)
    {
        if (_teamLen[arenaSlot] >= MaxTeamsPerArena) return -1;
        int ti = arenaSlot * MaxTeamsPerArena + _teamLen[arenaSlot];
        ref ArenaTeam t = ref _teams[ti];
        t.CaptainSlot = captainSlot;
        t.MemberOffset = memberOffset;
        t.MemberLen = memberLen;
        _teamLen[arenaSlot]++;
        Recorder?.OnTeam(_arenas[arenaSlot].PointerId, captainSlot);
        return _teamLen[arenaSlot] - 1;
    }

    /// <summary>Latch the arena closed (§7A.1): no new members after the first PointerMove past slop. First-accept can
    /// only fire once closed (rule 2). Idempotent — the close is recorded once (the first latch).</summary>
    public void CloseArena(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.Closed) return;
        a.Closed = true;
        Recorder?.OnClose(a.PointerId);
    }

    /// <summary>Mark/clear the hold flag (§7A.2 rule 5): a DoubleTap member sets it after the first up to keep the arena
    /// open across the inter-tap window; last-standing is suppressed while held.</summary>
    public void SetHeld(int arenaSlot, bool held) => _arenas[arenaSlot].Held = held;

    // ── vote mutation (driven by the FSM bank) ─────────────────────────────────────────────────────────────

    /// <summary>Cast/overwrite a member's vote in place (the FSM's verdict into its arena, §7A.2). No allocation. Only a
    /// genuine TRANSITION is recorded (the move path re-asserts the same vote every sample; the §12.6 ledger logs the
    /// vote CHANGES, not the cadence — so a fling at a finer timestep with more samples but the same transitions traces
    /// identically). The owning arena is the member's strided seat (<c>memberSlot / MaxMembersPerArena</c>).</summary>
    public void SetVote(int memberSlot, ArenaVote vote)
    {
        ref ArenaMember m = ref _members[memberSlot];
        if (Recorder is not null && m.Vote != vote)
            Recorder.OnVote(_arenas[memberSlot / MaxMembersPerArena].PointerId, memberSlot, m.Kind, vote);
        m.Vote = vote;
    }

    public ArenaVote VoteOf(int memberSlot) => _members[memberSlot].Vote;
    public ref ArenaMember MemberAt(int memberSlot) => ref _members[memberSlot];
    public ref GestureArenaState ArenaAt(int arenaSlot) => ref _arenas[arenaSlot];
    public bool IsArenaOpen(int arenaSlot) => arenaSlot >= 0 && arenaSlot < MaxArenas && _arenaUsed[arenaSlot];

    /// <summary>The members of one arena as a read-only (offset,len) span over the backing store (§7A.4).</summary>
    public ReadOnlySpan<ArenaMember> Members(int arenaSlot)
    {
        ref readonly GestureArenaState a = ref _arenas[arenaSlot];
        return new ReadOnlySpan<ArenaMember>(_members, a.MemberOffset, a.MemberLen);
    }

    // ── resolution (§7A.2 — exactly the Flutter ruleset over the FSM votes) ─────────────────────────────────

    /// <summary>Resolve one arena step EXACTLY per §7A.2 over the current votes (§7A.2 / the doc's <c>ResolveStep</c>):
    /// <list type="number">
    /// <item><b>Eager-win:</b> the first member voting <see cref="ArenaVote.EagerAccept"/> wins immediately and sweeps
    /// the rest (a Drag crossing slop, a Pinch's second contact) — resolves mid-stream, no PointerUp wait.</item>
    /// <item><b>First-accept:</b> with no eager-win, the first <see cref="ArenaVote.Accept"/> wins ONLY once the arena is
    /// <see cref="GestureArenaState.Closed"/> and no member ahead of it is still Pending; ties by <see cref="ArenaMember.Priority"/>.</item>
    /// <item><b>Last-standing:</b> if all-but-one rejected, the survivor wins (even still Pending) unless <see cref="GestureArenaState.Held"/>.</item>
    /// </list>
    /// Rule 4 (pointer-up sweep) and rule 5 (hold release) are <see cref="ResolveUp"/> / <see cref="ResolveHoldRelease"/>.
    /// Returns the winning member slot (resolved this step) or -1 (stay open). Idempotent once resolved.</summary>
    public int ResolveStep(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.WinnerSlot >= 0) return a.WinnerSlot;
        ReadOnlySpan<ArenaMember> members = Members(arenaSlot);

        int eager = FindFirst(members, ArenaVote.EagerAccept);
        if (eager >= 0) { Sweep(ref a, members, winner: a.MemberOffset + eager); return a.WinnerSlot; }

        int accept = FirstVote(members, ArenaVote.Accept);
        if (a.Closed && accept >= 0 && NoPendingAhead(members, accept))
        { Sweep(ref a, members, winner: a.MemberOffset + accept); return a.WinnerSlot; }

        int alive = members.Length - CountVote(members, ArenaVote.Reject);
        if (alive == 1 && !a.Held)
        { Sweep(ref a, members, winner: a.MemberOffset + LastAlive(members)); return a.WinnerSlot; }

        return -1;   // stay open; wait for the next PointerMove vote, the up-sweep, or the hold timer
    }

    /// <summary>Pointer-up sweep (§7A.2 rule 4): on PointerUp with no winner, force-sweep — the highest-priority member
    /// still Accept/Pending wins, everyone else is rejected. This is where a clean tap (no slop crossed) resolves to the
    /// Tap recognizer. A held arena is NOT swept here (rule 5 keeps it open across the inter-tap window). Returns the
    /// winner slot or -1 (held / no viable member).</summary>
    public int ResolveUp(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.WinnerSlot >= 0) return a.WinnerSlot;
        if (a.Held) return -1;             // the hold window keeps it open; release decides (rule 5)
        ReadOnlySpan<ArenaMember> members = Members(arenaSlot);

        int best = -1;
        byte bestPri = byte.MaxValue;
        for (int i = 0; i < members.Length; i++)
        {
            ArenaVote v = members[i].Vote;
            if (v == ArenaVote.Reject) continue;
            if (members[i].Priority < bestPri) { bestPri = members[i].Priority; best = i; }   // Accept/Pending, highest priority
        }
        if (best < 0) return -1;
        Sweep(ref a, members, winner: a.MemberOffset + best);
        return a.WinnerSlot;
    }

    /// <summary>Hold release for double-tap (§7A.2 rule 5): the inter-tap window expired without a qualifying second
    /// down+up, so the hold is released and the single-Tap member wins RETROACTIVELY (its Tapped fires deferred). Clears
    /// <see cref="GestureArenaState.Held"/> and resolves by the same priority sweep as the up-sweep. Returns the winner
    /// slot or -1. (A second tap that lands in time instead promotes the DoubleTap member to Accept/EagerAccept, which
    /// <see cref="ResolveStep"/>/<see cref="ResolveUp"/> then resolve — this entry is the TIMEOUT branch only.)</summary>
    public int ResolveHoldRelease(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        a.Held = false;
        return ResolveUp(arenaSlot);
    }

    /// <summary>PointerCaptureLost force-close (§7A.5, OS WM_POINTERCAPTURECHANGED): the current provisional winner (if
    /// any) wins by default, all others are rejected; if none has won, the highest-priority non-rejected member wins.
    /// Always closes the arena. Returns the winner slot or -1 (no viable member — pure cleanup).</summary>
    public int ForceClose(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.WinnerSlot >= 0) { Sweep(ref a, Members(arenaSlot), winner: a.WinnerSlot); return a.WinnerSlot; }
        a.Held = false;
        return ResolveUp(arenaSlot);
    }

    /// <summary>Free the arena seat (its members/teams reclaimed) once the contact ends and resolution is consumed. The
    /// strided member backing is reused by the next contact that takes this seat — zero growth.</summary>
    public void CloseAndFree(int arenaSlot)
    {
        if (!_arenaUsed[arenaSlot]) return;
        Recorder?.OnFree(_arenas[arenaSlot].PointerId);
        _arenaUsed[arenaSlot] = false;
        _teamLen[arenaSlot] = 0;
        _arenas[arenaSlot] = default;
        OpenArenaCount--;
    }

    // ── the sweep (winner + synthetic GestureRejected for the losers) ──────────────────────────────────────

    /// <summary>Grant the win to <paramref name="winner"/> (a global member slot) and sweep every other member with a
    /// synthetic <c>GestureRejected</c> (§7A.2 / §7A.5): each loser's vote becomes <see cref="ArenaVote.Reject"/> and its
    /// FSM is reset to Idle via <see cref="OnMemberRejected"/>; the winner's vote becomes <see cref="ArenaVote.Accept"/>
    /// (or stays EagerAccept) and <see cref="OnMemberWon"/> fires. Pure in-place mutation + callbacks — no allocation.</summary>
    private void Sweep(ref GestureArenaState a, ReadOnlySpan<ArenaMember> members, int winner)
    {
        a.WinnerSlot = winner;
        a.Held = false;
        for (int i = 0; i < members.Length; i++)
        {
            int slot = a.MemberOffset + i;
            if (slot == winner)
            {
                if (_members[slot].Vote != ArenaVote.EagerAccept) _members[slot].Vote = ArenaVote.Accept;
                Recorder?.OnWin(a.PointerId, slot, _members[slot].Kind, _members[slot].Vote);
                OnMemberWon?.Invoke(_members[slot].FsmSlot);
            }
            else if (_members[slot].Vote != ArenaVote.Reject)
            {
                _members[slot].Vote = ArenaVote.Reject;     // synthetic GestureRejected
                Recorder?.OnSweep(a.PointerId, slot, _members[slot].Kind);   // sweep order = enrollment order
                OnMemberRejected?.Invoke(_members[slot].FsmSlot);
            }
        }
    }

    // ── span predicates (the §7A.2 ResolveStep helpers — all allocation-free linear scans) ─────────────────

    private int FindArena(uint pointerId)
    {
        for (int i = 0; i < MaxArenas; i++) if (_arenaUsed[i] && _arenas[i].PointerId == pointerId) return i;
        return -1;
    }

    /// <summary>The arena slot currently open for <paramref name="pointerId"/>, or -1.</summary>
    public int ArenaSlotFor(uint pointerId) => FindArena(pointerId);

    private static int FindFirst(ReadOnlySpan<ArenaMember> m, ArenaVote v)
    {
        for (int i = 0; i < m.Length; i++) if (m[i].Vote == v) return i;
        return -1;
    }

    /// <summary>First member with <paramref name="v"/>, lowest <see cref="ArenaMember.Priority"/> on a tie (innermost,
    /// then doc-order) — the §7A.2 "ties broken by Priority" clause for the first-accept winner.</summary>
    private static int FirstVote(ReadOnlySpan<ArenaMember> m, ArenaVote v)
    {
        int best = -1;
        byte bestPri = byte.MaxValue;
        for (int i = 0; i < m.Length; i++)
            if (m[i].Vote == v && m[i].Priority < bestPri) { bestPri = m[i].Priority; best = i; }
        return best;
    }

    private static int CountVote(ReadOnlySpan<ArenaMember> m, ArenaVote v)
    {
        int n = 0;
        for (int i = 0; i < m.Length; i++) if (m[i].Vote == v) n++;
        return n;
    }

    /// <summary>True when no member with a strictly lower priority than <paramref name="acceptIdx"/> is still Pending —
    /// the §7A.2 "no recognizer ahead of it is still Pending" gate for first-accept.</summary>
    private static bool NoPendingAhead(ReadOnlySpan<ArenaMember> m, int acceptIdx)
    {
        byte acceptPri = m[acceptIdx].Priority;
        for (int i = 0; i < m.Length; i++)
            if (m[i].Vote == ArenaVote.Pending && m[i].Priority < acceptPri) return false;
        return true;
    }

    private static int LastAlive(ReadOnlySpan<ArenaMember> m)
    {
        for (int i = 0; i < m.Length; i++) if (m[i].Vote != ArenaVote.Reject) return i;
        return 0;
    }
}
