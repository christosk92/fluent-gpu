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

    /// <summary>True while any open, unresolved arena still has a <see cref="GestureKind.Hold"/> member armed (vote
    /// <see cref="ArenaVote.Pending"/> — its long-press timer is counting down). A stationary held finger emits no input
    /// events, so without this the frame loop would idle and the <c>OnFrameEnd</c> timer tick would never fire; the host
    /// ORs this into a wake reason so frames keep coming until the Hold promotes (§7A.4 — the timer is ticked on the held
    /// frames) or the contact strays/lifts and the member rejects. Zero-cost when no arena is open (the
    /// <see cref="OpenArenaCount"/> early-out — the same pattern <c>ScrollAnimator.HasActive</c> uses); the bit clears the
    /// instant the Hold resolves (<see cref="GestureArenaState.WinnerSlot"/> ≥ 0) or rejects, so the idle mask returns to
    /// None right after the context flyout fires (no lingering keep-alive while the finger merely rests).</summary>
    public bool HasArmedHold()
    {
        if (OpenArenaCount == 0) return false;
        for (int slot = 0; slot < MaxArenas; slot++)
            if (ArenaHasArmedHold(slot)) return true;
        return false;
    }

    /// <summary>True when the SPECIFIC open arena <paramref name="arenaSlot"/> is unresolved and still has a
    /// <see cref="GestureKind.Hold"/> member armed (vote <see cref="ArenaVote.Pending"/>). The dispatcher uses this to keep
    /// a non-hit-testable context-only contact's slot alive (it has no Down/pan to hold the seat) until the long-press
    /// fires or rejects — without it the slot would recycle on the down frame and free the arena before the timer.</summary>
    public bool ArenaHasArmedHold(int arenaSlot)
    {
        if ((uint)arenaSlot >= MaxArenas || !_arenaUsed[arenaSlot] || _arenas[arenaSlot].WinnerSlot >= 0) return false;
        ReadOnlySpan<ArenaMember> members = Members(arenaSlot);
        for (int i = 0; i < members.Length; i++)
            if (members[i].Kind == GestureKind.Hold && members[i].Vote == ArenaVote.Pending) return true;
        return false;
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
    /// Returns the winning member slot (resolved this step) or -1 (stay open). Idempotent once resolved.
    ///
    /// TEAMS (§7A.3): a team presents ONE arena entry — its captain. A non-captain team member never wins independently;
    /// it lends its vote to the captain's EFFECTIVE vote (the strongest of the captain + its teammates: EagerAccept ≻
    /// Accept ≻ Pending ≻ Reject), so a drag-extend teammate crossing slop makes the team eager-win while a clean tap
    /// keeps the captain's Tap vote — the captain (not the innermost drag member) stands for the whole selection control.
    /// On a team win the teammates are NOT internally rejected (the §7A.3 "don't reject internally until the team loses");
    /// the captain then picks the firing recognizer (<see cref="CaptainPick"/>). With no teams every helper short-circuits
    /// to the member's own vote, so the team-free arena resolves bit-identically to the §7A.2 pseudocode.</summary>
    public int ResolveStep(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.WinnerSlot >= 0) return a.WinnerSlot;
        ReadOnlySpan<ArenaMember> members = Members(arenaSlot);
        bool hasTeams = _teamLen[arenaSlot] > 0;

        int eager = FindEffective(arenaSlot, members, ArenaVote.EagerAccept, hasTeams);   // a GLOBAL slot (or -1)
        if (eager >= 0) { Sweep(ref a, members, winner: eager); return a.WinnerSlot; }

        int accept = FirstEffective(arenaSlot, members, ArenaVote.Accept, hasTeams);      // a GLOBAL slot (or -1)
        if (a.Closed && accept >= 0 && NoPendingAheadEff(arenaSlot, members, accept, hasTeams))
        { Sweep(ref a, members, winner: accept); return a.WinnerSlot; }

        // Last-standing counts TEAM ENTRIES, not raw members: a team's non-captain members are not independent survivors
        // (they're represented by the captain), so a lone selection team — captain + drag-extend + double-tap, all
        // Pending — is ONE alive entry and wins by last-standing exactly as a single recognizer would (§7A.5 fast-path).
        int alive = AliveEntries(arenaSlot, members, hasTeams);
        if (alive == 1 && !a.Held)
        {
            int win = LastAliveEntry(arenaSlot, members, hasTeams);
            if (win >= 0) { Sweep(ref a, members, winner: win); return a.WinnerSlot; }
        }

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
        bool hasTeams = _teamLen[arenaSlot] > 0;

        // Highest-priority surviving ENTRY (a team is represented by its captain — a non-captain team member is not an
        // independent candidate, so a clean tap resolves to the team's CAPTAIN, not the innermost drag-extend member).
        int best = -1;
        byte bestPri = byte.MaxValue;
        for (int i = 0; i < members.Length; i++)
        {
            int gs = a.MemberOffset + i;
            if (hasTeams && IsNonCaptainTeamMember(arenaSlot, gs)) continue;   // represented by the captain
            if (EffectiveVote(arenaSlot, gs, hasTeams) == ArenaVote.Reject) continue;
            if (members[i].Priority < bestPri) { bestPri = members[i].Priority; best = gs; }   // Accept/Pending, highest priority
        }
        if (best < 0) return -1;
        Sweep(ref a, members, winner: best);
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
    /// (or stays EagerAccept) and <see cref="OnMemberWon"/> fires. A member sharing the winner's TEAM is RETAINED, not
    /// rejected (§7A.3 — "don't reject internally until the team loses"): the team won as a whole, so the captain's
    /// drag-extend / double-tap teammates keep their state for the captain's pick. Pure in-place mutation + callbacks —
    /// no allocation; with no teams the retain test is one early-out and the sweep is byte-identical to §7A.2.</summary>
    private void Sweep(ref GestureArenaState a, ReadOnlySpan<ArenaMember> members, int winner)
    {
        a.WinnerSlot = winner;
        a.Held = false;
        int arenaSlot = winner / MaxMembersPerArena;
        bool hasTeams = _teamLen[arenaSlot] > 0;
        for (int i = 0; i < members.Length; i++)
        {
            int slot = a.MemberOffset + i;
            if (slot == winner)
            {
                if (_members[slot].Vote != ArenaVote.EagerAccept) _members[slot].Vote = ArenaVote.Accept;
                Recorder?.OnWin(a.PointerId, slot, _members[slot].Kind, _members[slot].Vote);
                OnMemberWon?.Invoke(_members[slot].FsmSlot);
            }
            else if (hasTeams && SameTeam(arenaSlot, winner, slot))
            {
                // The winner's teammate: NOT swept — the team carries the win together (§7A.3). Its vote is left as-is so
                // CaptainPick can read it (a drag-extend teammate's EagerAccept = "the captain fires drag, not tap").
            }
            else if (_members[slot].Vote != ArenaVote.Reject)
            {
                _members[slot].Vote = ArenaVote.Reject;     // synthetic GestureRejected
                Recorder?.OnSweep(a.PointerId, slot, _members[slot].Kind);   // sweep order = enrollment order
                OnMemberRejected?.Invoke(_members[slot].FsmSlot);
            }
        }
    }

    // ── teams (§7A.3 — the captain stands for the team; teammates don't reject internally) ──────────────────────

    /// <summary>True when global member slots <paramref name="x"/> and <paramref name="y"/> belong to the SAME team in
    /// <paramref name="arenaSlot"/> (the §7A.3 internal-non-rejection test). Linear over the arena's ≤<see cref="MaxTeamsPerArena"/>
    /// teams; called only when the arena has teams.</summary>
    private bool SameTeam(int arenaSlot, int x, int y)
    {
        int n = _teamLen[arenaSlot];
        int baseT = arenaSlot * MaxTeamsPerArena;
        for (int t = 0; t < n; t++)
        {
            ref ArenaTeam tm = ref _teams[baseT + t];
            bool xin = x >= tm.MemberOffset && x < tm.MemberOffset + tm.MemberLen;
            bool yin = y >= tm.MemberOffset && y < tm.MemberOffset + tm.MemberLen;
            if (xin && yin) return true;
        }
        return false;
    }

    /// <summary>The team index (0..teamLen) whose member RANGE contains global slot <paramref name="ms"/>, or -1 (not in
    /// any team). §7A.3.</summary>
    private int TeamOfMember(int arenaSlot, int ms)
    {
        int n = _teamLen[arenaSlot];
        int baseT = arenaSlot * MaxTeamsPerArena;
        for (int t = 0; t < n; t++)
        {
            ref ArenaTeam tm = ref _teams[baseT + t];
            if (ms >= tm.MemberOffset && ms < tm.MemberOffset + tm.MemberLen) return t;
        }
        return -1;
    }

    /// <summary>True when global slot <paramref name="ms"/> is in a team but is NOT that team's captain — so it is
    /// REPRESENTED by the captain in resolution and never wins independently (§7A.3).</summary>
    private bool IsNonCaptainTeamMember(int arenaSlot, int ms)
    {
        int t = TeamOfMember(arenaSlot, ms);
        if (t < 0) return false;
        return _teams[arenaSlot * MaxTeamsPerArena + t].CaptainSlot != ms;
    }

    /// <summary>The team's EFFECTIVE vote for resolution (§7A.3): for a captain, the STRONGEST vote among the captain and
    /// its teammates (EagerAccept ≻ Accept ≻ Pending ≻ Reject) — so a drag-extend teammate crossing slop makes the team
    /// eager-win while a clean tap keeps the team on the captain's Accept; for a non-captain team member, <see cref="ArenaVote.Reject"/>
    /// (never an independent candidate); for a member in no team, its own vote. With no teams this is just the member's vote.</summary>
    private ArenaVote EffectiveVote(int arenaSlot, int ms, bool hasTeams)
    {
        if (!hasTeams) return _members[ms].Vote;
        int t = TeamOfMember(arenaSlot, ms);
        if (t < 0) return _members[ms].Vote;                 // not in any team
        ref ArenaTeam tm = ref _teams[arenaSlot * MaxTeamsPerArena + t];
        if (tm.CaptainSlot != ms) return ArenaVote.Reject;   // represented by the captain — not a candidate
        // Captain: aggregate the team's strongest vote.
        ArenaVote best = ArenaVote.Reject;
        for (int s = tm.MemberOffset; s < tm.MemberOffset + tm.MemberLen; s++)
            if (VoteRank(_members[s].Vote) > VoteRank(best)) best = _members[s].Vote;
        return best;
    }

    private static int VoteRank(ArenaVote v) => v switch
    {
        ArenaVote.EagerAccept => 3,
        ArenaVote.Accept => 2,
        ArenaVote.Pending => 1,
        _ => 0,   // Reject
    };

    /// <summary>On a TEAM win, the recognizer the captain fires (§7A.3 "based on tap count + movement"). Movement wins: a
    /// teammate voting <see cref="ArenaVote.EagerAccept"/> (a drag-extend / selection-drag that crossed slop) fires its
    /// kind; otherwise the captain's own kind (the clean tap / double-tap, whose tap-COUNT the editor's ClickCount realizes
    /// downstream — §12A). Returns the firing <see cref="GestureKind"/>; defaults to the captain's kind when not a team
    /// win. The dispatcher reads this to label the win; execution stays on the editor's scalar OnDrag + ClickCount path.</summary>
    public GestureKind CaptainPick(int arenaSlot)
    {
        ref GestureArenaState a = ref _arenas[arenaSlot];
        if (a.WinnerSlot < 0) return GestureKind.Tap;
        int t = TeamOfMember(arenaSlot, a.WinnerSlot);
        if (t < 0) return _members[a.WinnerSlot].Kind;       // a non-team winner fires its own kind
        ref ArenaTeam tm = ref _teams[arenaSlot * MaxTeamsPerArena + t];
        // Movement first: an eager-accepting teammate (drag-extend) is the gesture.
        for (int s = tm.MemberOffset; s < tm.MemberOffset + tm.MemberLen; s++)
            if (_members[s].Vote == ArenaVote.EagerAccept) return _members[s].Kind;
        return _members[tm.CaptainSlot].Kind;                // else the captain's tap/double-tap
    }

    // ── span predicates (the §7A.2 ResolveStep helpers — all allocation-free linear scans) ─────────────────

    private int FindArena(uint pointerId)
    {
        for (int i = 0; i < MaxArenas; i++) if (_arenaUsed[i] && _arenas[i].PointerId == pointerId) return i;
        return -1;
    }

    /// <summary>The arena slot currently open for <paramref name="pointerId"/>, or -1.</summary>
    public int ArenaSlotFor(uint pointerId) => FindArena(pointerId);

    // The §7A.2 ResolveStep helpers, generalized to read the team-EFFECTIVE vote (so a captain stands for its team and a
    // non-captain team member is skipped). All return GLOBAL member slots and skip non-captain team members; with no
    // teams `EffectiveVote` is the member's own vote and nothing is skipped, so they reduce to the original §7A.2 scans.

    /// <summary>First ENTRY (global slot) whose effective vote == <paramref name="v"/> (skipping non-captain team members),
    /// or -1. The eager-win scan.</summary>
    private int FindEffective(int arenaSlot, ReadOnlySpan<ArenaMember> m, ArenaVote v, bool hasTeams)
    {
        for (int i = 0; i < m.Length; i++)
        {
            int gs = _arenas[arenaSlot].MemberOffset + i;
            if (hasTeams && IsNonCaptainTeamMember(arenaSlot, gs)) continue;
            if (EffectiveVote(arenaSlot, gs, hasTeams) == v) return gs;
        }
        return -1;
    }

    /// <summary>Entry with effective vote <paramref name="v"/>, lowest <see cref="ArenaMember.Priority"/> on a tie
    /// (innermost, then doc-order) — the §7A.2 "ties broken by Priority" clause for first-accept. Global slot or -1.</summary>
    private int FirstEffective(int arenaSlot, ReadOnlySpan<ArenaMember> m, ArenaVote v, bool hasTeams)
    {
        int best = -1;
        byte bestPri = byte.MaxValue;
        for (int i = 0; i < m.Length; i++)
        {
            int gs = _arenas[arenaSlot].MemberOffset + i;
            if (hasTeams && IsNonCaptainTeamMember(arenaSlot, gs)) continue;
            if (EffectiveVote(arenaSlot, gs, hasTeams) == v && m[i].Priority < bestPri) { bestPri = m[i].Priority; best = gs; }
        }
        return best;
    }

    /// <summary>True when no ENTRY with a strictly lower priority than the global slot <paramref name="acceptGlobal"/> is
    /// still effectively Pending — the §7A.2 "no recognizer ahead of it is still Pending" gate for first-accept (teams
    /// counted by captain).</summary>
    private bool NoPendingAheadEff(int arenaSlot, ReadOnlySpan<ArenaMember> m, int acceptGlobal, bool hasTeams)
    {
        int off = _arenas[arenaSlot].MemberOffset;
        byte acceptPri = _members[acceptGlobal].Priority;
        for (int i = 0; i < m.Length; i++)
        {
            int gs = off + i;
            if (hasTeams && IsNonCaptainTeamMember(arenaSlot, gs)) continue;
            if (EffectiveVote(arenaSlot, gs, hasTeams) == ArenaVote.Pending && m[i].Priority < acceptPri) return false;
        }
        return true;
    }

    /// <summary>The count of live ENTRIES (not raw members): a captain counts once for its whole team; a non-captain team
    /// member counts zero; a member in no team counts if not Reject. The §7A.2 last-standing population, team-aware.</summary>
    private int AliveEntries(int arenaSlot, ReadOnlySpan<ArenaMember> m, bool hasTeams)
    {
        int off = _arenas[arenaSlot].MemberOffset;
        int n = 0;
        for (int i = 0; i < m.Length; i++)
        {
            int gs = off + i;
            if (hasTeams && IsNonCaptainTeamMember(arenaSlot, gs)) continue;
            if (EffectiveVote(arenaSlot, gs, hasTeams) != ArenaVote.Reject) n++;
        }
        return n;
    }

    /// <summary>The single surviving ENTRY's global slot (the last-standing winner), or -1. Mirrors <see cref="AliveEntries"/>
    /// — a captain represents its team.</summary>
    private int LastAliveEntry(int arenaSlot, ReadOnlySpan<ArenaMember> m, bool hasTeams)
    {
        int off = _arenas[arenaSlot].MemberOffset;
        for (int i = 0; i < m.Length; i++)
        {
            int gs = off + i;
            if (hasTeams && IsNonCaptainTeamMember(arenaSlot, gs)) continue;
            if (EffectiveVote(arenaSlot, gs, hasTeams) != ArenaVote.Reject) return gs;
        }
        return -1;
    }
}
