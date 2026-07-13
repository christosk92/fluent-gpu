using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION ENGINE — the value substrate (LANDED; wired into the frame loop via AnimEngine.Tick).
//
//  This file lands the ONE value primitive the engine is built on: a POD `AnimValue` slab keyed (node, channel),
//  carrying {value, velocity, target, generator}, plus the pure-POD `Generator` law (sampled at ABSOLUTE time) and
//  the index-based `SignalSource` table that replaces the `DrivenClockTable` `List<Func<float>>` closure leak.
//
//  It is a faithful generalization of the `ScrollBind`/`ScrollBindTable` slab idiom (ScrollBind.cs) from
//  "scroll-offset source" to "any signal source". Lifetimes are reconciler-owned via a free-list (NOT GC); the
//  array grows ONLY at reconcile, never in frame phases 6–13.
//
//  The rework has LANDED: the old `class Track`/`AnimEngine.cs` model was replaced in place — `AnimEngine.Tick`
//  (now the AnimScheduler.* partials) advances this slab via the analytical spring `Generator.Eval` every frame.
//  Design: docs/plans/animation-engine-rework-design.md §3–4. Canon owner: design/subsystems/backdrop-effects-animation.md §5.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The generator law a row integrates by. Selects how <see cref="Generator"/>'s union is read and which
/// closed-form <c>Eval</c> samples it. (Replaces the <see cref="IntegrationMode"/> Eased|Spring split, adding the
/// inertia coast that subsumes <c>OverscrollPhysics.CoastStep</c> and an explicit multi-keyframe form.)</summary>
public enum GenKind : byte { Eased, Spring, Inertia, Keyframes }

/// <summary>Per-row flags (replaces the scattered bools on <c>AnimEngine.Track</c>). 2 bytes so the 64B row holds.</summary>
[System.Flags]
public enum AnimFlags : ushort
{
    None          = 0,
    JustSeeded    = 1 << 0,   // skip the first-frame integrate (seeded this frame)
    Done          = 1 << 1,   // settled — eligible for retire at phase 13
    Loop          = 1 << 2,
    Parked        = 1 << 3,   // node is in a KeepAlive-parked subtree — neither advanced nor counted active
    RestoreLayout = 1 << 4,   // SizeMode.Relayout: restore the declared LayoutInput at settle
    TrailingAnchor= 1 << 5,
    Driven        = 1 << 6,   // ElapsedMs is sourced from a SignalSource (DrivenSrc), not wall-time
    RmExempt      = 1 << 7,   // exempt from the reduced-motion snap (essential motion, e.g. a spinner)
    Additive      = 1 << 8,   // CompositeOp != Replace
    Accumulate    = 1 << 9,   // distinguishes Add vs Accumulate when Additive is set
    DisplayRate   = 1 << 10,  // a TRANSIENT loop (e.g. an indeterminate progress bar) that opts OUT of the ambient
                              // frame-rate cap and runs at the panel refresh — it's short-lived, not a perpetual idle loop
}

/// <summary>The 16-byte tagged-union generator law. The owning <see cref="AnimValue.Kind"/> selects the reading.
/// All coefficients are BAKED at reconcile/seed (the Newton duration-solve, the (response,ζ)→(ω,ζ) conversion, the
/// origin/v0 fold into the spring's regime coefficients) so the per-frame <c>Eval</c> never parses, iterates, or
/// allocates. Blittable, no GC refs ⇒ the overlapping <c>[FieldOffset]</c> union is legal CLR layout.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct Generator
{
    // ── SPRING — analytical closed form, coefficients pre-baked from (response, ζ); origin & v0 fold into A/B at seed.
    [FieldOffset(0)]  public float Omega;       // natural frequency ω = sqrt(k/m)
    [FieldOffset(4)]  public float Zeta;        // damping ratio ζ (1 = critical, <1 = under-damped/bouncy)
    [FieldOffset(8)]  public float A;           // pre-baked regime coefficient #1
    [FieldOffset(12)] public float B;           // pre-baked regime coefficient #2

    // ── EASED — two-point (From→To over DurationMs) or a multi-keyframe span into the shared keyframe arena.
    [FieldOffset(0)]  public float FromV;       // start value (two-point)
    [FieldOffset(4)]  public float DurationMs;
    [FieldOffset(8)]  public ushort EaseId;     // Foundation.Easings / linear()-LUT id; 0 = linear
    [FieldOffset(10)] public ushort KeyOffset;  // keyframe-arena span start; 0 ⇒ two-point From→To
    [FieldOffset(12)] public ushort KeyCount;   // 0/2 ⇒ two-point

    // ── INERTIA — friction coast (subsumes OverscrollPhysics.CoastStep), optional spring-handoff at a boundary.
    [FieldOffset(0)]  public float V0;          // launch velocity px/s
    [FieldOffset(4)]  public float DecayK;      // k = -ln(decayPerSecond) > 0
    [FieldOffset(8)]  public float Boundary;    // optional clamp target (NaN = unbounded)
}

/// <summary>The result of sampling a <see cref="Generator"/> — returned BY VALUE so there is no shared-mutable-state
/// aliasing (Motion's reused <c>{value,done}</c> object footgun, avoided).</summary>
public readonly struct Sample
{
    public readonly float Value;
    public readonly bool Done;
    public Sample(float value, bool done) { Value = value; Done = done; }
}

/// <summary>The one value slot — a POD row keyed <c>(Node, Channel)</c>. Replaces the 33-field heap <c>class Track</c>.
/// 64 bytes (lands the <c>backdrop-effects-animation.md §5</c> <c>AnimTrack</c>-in-a-slab spec). The two <c>int</c>
/// chains replace the <c>List</c>/<c>Dictionary</c> container: <see cref="NextOnNode"/> threads every row on the same
/// node (per-node fold + teardown, mirroring <c>ScrollBind.NodeNext</c>); <see cref="NextActive"/> threads the
/// scheduler's dense advance walk (wired in Phase 2).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnimValue
{
    public NodeHandle Node;            //  8 — {u32 index, u32 gen}; gen-checked each tick (IsLive)
    public AnimChannel Channel;        //  1 — the axis this row drives (the live 17-entry vocabulary; +Color in Phase 4)
    public byte OwnerTag;              //  1 — Fork-1 owner partition {None,Anim,Scroll,Interaction,Connected}
    public byte Priority;              //  1 — Fork-1 Framer 7-slot gesture arbitration (compose-pass input)
    public GenKind Kind;               //  1 — generator discriminant
    public float Position;             //  4 — the channel's live scalar (the old Track.Pos/.Value, collapsed)
    public float Velocity;             //  4 — units/s — analytical, for the retarget handoff
    public float To;                   //  4 — rest target / last-keyframe value
    public float ElapsedMs;            //  4 — absolute elapsed since (re)seed — the Generator's t
    public float DelayRemainingMs;     //  4 — begin delay
    public Generator Gen;              // 16 — the tagged-union law (§4.2)
    public float RestoreTo;            //  4 — declared LayoutInput restored at settle (Reflow)
    public AnimFlags Flags;            //  2
    public ushort DrivenSrc;           //  2 — index into the SignalSource table; 0xFFFF = wall-clock
    public int NextOnNode;             //  4 — next row on the SAME node (teardown + per-node fold) ; -1 = tail
    public int NextActive;             //  4 — reserved for a future row-dense walk; the LANDED active walk is the slab's
                                       //      NODE-level doubly-linked chain (AnimValueSlab.FirstActiveNode/NextActiveNode)

    public const ushort WallClock = 0xFFFF;

    public readonly bool Has(AnimFlags f) => (Flags & f) != 0;
}

/// <summary>What an <see cref="AnimValue"/> in <see cref="AnimFlags.Driven"/> mode reads its progress from — by INDEX,
/// never a captured <c>Func&lt;float&gt;</c> (kills the <see cref="DrivenClockTable"/> closure leak). <see cref="RefIndex"/>
/// is a scroller node-index / media-clock id / signal slot, resolved once at reconcile.</summary>
public enum SignalSourceKind : byte { ScrollOffset, ScrollBand, MediaClockMs, SignalFloat, NodeChannel }

/// <summary>One index-based driving source (Phase 2 reads these in the scheduler).</summary>
public struct SignalSource
{
    public SignalSourceKind Kind;
    public int RefIndex;
    public SignalSource(SignalSourceKind kind, int refIndex) { Kind = kind; RefIndex = refIndex; }
}

/// <summary>The reconciler-owned dense slab of <see cref="AnimValue"/> rows + the <c>(node → head)</c> index that
/// threads each node's <see cref="AnimValue.NextOnNode"/> chain. Mirrors <see cref="ScrollBindTable"/> (ScrollBind.cs:94):
/// slots are recycled through a free-list (lifetimes owned by the reconciler, NOT GC); the backing array grows ONLY
/// here (at reconcile), never in the frame hot path. The canon-mandated <c>NodeHandle→TrackHead</c> map
/// (<c>backdrop-effects-animation.md:454</c>) IS <see cref="_headByNode"/> + a per-node channel filter.</summary>
public sealed class AnimValueSlab
{
    // Pre-sized so hover/press/brush fades (which now share the slab and settle in bursts) never grow these in a measured
    // frame phase: _free is pushed in phase-7 FreeSlot as fades settle; _headByNode/_rows grow as new tracks are added.
    private AnimValue[] _rows = new AnimValue[64];
    private int _count;                                              // high-water of allocated slots
    private readonly Stack<int> _free = new(64);                     // recycled slots (free-list, not GC)
    private readonly Dictionary<int, int> _headByNode = new(64);     // node INDEX → head of its NextOnNode chain

    // ── ACTIVE-NODE chain (perf plan W6/E12): an intrusive doubly-linked list of the node indices that currently own
    // rows, threaded through two parallel int[] keyed by NODE INDEX. The per-tick PASS1/PASS2 walks and the census
    // scans iterate THIS chain — O(active nodes) — instead of enumerating _headByNode.Keys, whose entry-array walk
    // costs O(high-water concurrent animated nodes) even when few are active (Dictionary.Remove leaves tombstoned
    // entries the enumerator still visits). The Dictionary REMAINS the node→head lookup; the chain is membership only.
    // Arrays are sized by the highest node index seen and grow ONLY in Add (a reconcile/input-edge seed — the same
    // sites where _rows itself grows), never in frame phases 6–13. Entries are only read while a node is linked, so
    // resize garbage needs no initialization. Enumeration order is link order (most-recently-linked first) — the tick
    // passes are per-node independent, so order carries no semantics (Dictionary order was already arbitrary).
    private int[] _nextActiveNode = new int[64];
    private int[] _prevActiveNode = new int[64];
    private int _firstActiveNode = -1;

    // Slab-mutation version (perf plan W6/E12): bumped on every Add/Free/ClearNode (and by BumpVersion at the engine's
    // flag-retarget seeds, which rewrite Loop/DisplayRate through At() refs without a slab call). The engine memoizes
    // its LoopTrackCount/DisplayRateActive census against this — ComputeWakeReasons runs several times per frame and
    // was re-scanning every row each call.
    private int _version;

    /// <summary>Monotonic slab-mutation version — see the field remarks. Never reset.</summary>
    public int Version => _version;
    /// <summary>Bump <see cref="Version"/> for a census-visible row mutation done through an <see cref="At"/> ref
    /// (the engine's seed/retarget paths rewrite AnimFlags.Loop/DisplayRate in place, which no slab call sees).</summary>
    public void BumpVersion() => _version++;

    /// <summary>Head of the active-node chain (a node index that currently owns rows); -1 = slab empty.
    /// Iterate: <c>for (int n = FirstActiveNode; n >= 0; n = NextActiveNode(n))</c>. Do not Add/Free while iterating.</summary>
    public int FirstActiveNode => _firstActiveNode;
    /// <summary>Next node index on the active chain after <paramref name="nodeIndex"/> (which must be linked); -1 = tail.</summary>
    public int NextActiveNode(int nodeIndex) => _nextActiveNode[nodeIndex];

    private void EnsureActiveCapacity(int nodeIndex)
    {
        if (nodeIndex < _nextActiveNode.Length) return;
        int cap = _nextActiveNode.Length == 0 ? 64 : _nextActiveNode.Length;
        while (cap <= nodeIndex) cap *= 2;
        System.Array.Resize(ref _nextActiveNode, cap);
        System.Array.Resize(ref _prevActiveNode, cap);
    }

    private void LinkActive(int nodeIndex)   // node gained its FIRST row
    {
        EnsureActiveCapacity(nodeIndex);
        _prevActiveNode[nodeIndex] = -1;
        _nextActiveNode[nodeIndex] = _firstActiveNode;
        if (_firstActiveNode >= 0) _prevActiveNode[_firstActiveNode] = nodeIndex;
        _firstActiveNode = nodeIndex;
    }

    private void UnlinkActive(int nodeIndex)   // node's LAST row freed — O(1) via the prev link
    {
        int prev = _prevActiveNode[nodeIndex], next = _nextActiveNode[nodeIndex];
        if (prev >= 0) _nextActiveNode[prev] = next; else _firstActiveNode = next;
        if (next >= 0) _prevActiveNode[next] = prev;
    }

    /// <summary>Live row count (census; excludes recycled free-list slots).</summary>
    public int Count => _count - _free.Count;

    /// <summary>True while any row is live — the O(1) replacement for <c>AnimEngine.HasActive</c>'s list scan.
    /// (Phase 2 refines this to <c>active − parked</c> over the scheduler's cadence-classed active-set.)</summary>
    public bool HasActive => (_count - _free.Count) > 0;

    /// <summary>ref into the slab — no copy. Caller must hold the slot only across a single synchronous use.</summary>
    public ref AnimValue At(int slot) => ref _rows[slot];

    /// <summary>Head slot of <paramref name="nodeIndex"/>'s chain (walk <see cref="AnimValue.NextOnNode"/>); -1 = none.</summary>
    public int HeadOnNode(int nodeIndex) => _headByNode.TryGetValue(nodeIndex, out int h) ? h : -1;

    /// <summary>The node indices that currently own rows (diagnostic/verification view — e.g. the
    /// anim.activeChainMatchesDictionary gate cross-checks the active chain against it). The scheduler tick and the
    /// census scans iterate <see cref="FirstActiveNode"/>/<see cref="NextActiveNode"/> instead: a Dictionary key
    /// enumeration walks the entries array to its high-water even when few nodes are active.</summary>
    public Dictionary<int, int>.KeyCollection NodeIndices => _headByNode.Keys;

    /// <summary>True when <paramref name="nodeIndex"/> owns at least one row.</summary>
    public bool NodeHasRows(int nodeIndex) => _headByNode.ContainsKey(nodeIndex);

    private int Alloc()
    {
        int s;
        if (_free.Count > 0) s = _free.Pop();
        else
        {
            if (_count == _rows.Length)
                System.Array.Resize(ref _rows, _rows.Length == 0 ? 16 : _rows.Length * 2);
            s = _count++;
        }
        _rows[s] = default;
        _rows[s].NextOnNode = -1;
        _rows[s].NextActive = -1;
        _rows[s].DrivenSrc = AnimValue.WallClock;
        return s;
    }

    /// <summary>Add one row owned by <paramref name="nodeIndex"/>, prepended to that node's chain. Returns the slot so
    /// the caller can finish filling fields via <see cref="At"/>. The row's <see cref="AnimValue.Node"/> /
    /// <see cref="AnimValue.Channel"/> come from <paramref name="seed"/>.</summary>
    public int Add(int nodeIndex, in AnimValue seed)
    {
        int s = Alloc();
        bool hadRows = _headByNode.TryGetValue(nodeIndex, out int nh);
        _rows[s] = seed;
        _rows[s].NextOnNode = hadRows ? nh : -1;
        _rows[s].NextActive = -1;
        _headByNode[nodeIndex] = s;
        if (!hadRows) LinkActive(nodeIndex);   // first row → the node joins the active chain
        _version++;
        return s;
    }

    /// <summary>Free every row owned by <paramref name="nodeIndex"/> (re-reconcile / unmount): unlink the whole chain
    /// and recycle each slot. No-op if the node owns none.</summary>
    public void ClearNode(int nodeIndex)
    {
        if (!_headByNode.TryGetValue(nodeIndex, out int s)) return;
        _headByNode.Remove(nodeIndex);
        UnlinkActive(nodeIndex);
        _version++;
        while (s >= 0)
        {
            int next = _rows[s].NextOnNode;
            _rows[s] = default;
            _free.Push(s);
            s = next;
        }
    }

    /// <summary>Free a single row, unlinking it from its node chain. Used when one (node,channel) row settles/retires
    /// while the node keeps others.</summary>
    public void Free(int slot)
    {
        int nodeIndex = (int)_rows[slot].Node.Raw.Index;
        if (_headByNode.TryGetValue(nodeIndex, out int head))
        {
            if (head == slot)
            {
                int next = _rows[slot].NextOnNode;
                if (next < 0) { _headByNode.Remove(nodeIndex); UnlinkActive(nodeIndex); }   // last row → node leaves the active chain
                else _headByNode[nodeIndex] = next;
            }
            else
            {
                for (int p = head; p >= 0; p = _rows[p].NextOnNode)
                {
                    if (_rows[p].NextOnNode == slot) { _rows[p].NextOnNode = _rows[slot].NextOnNode; break; }
                }
            }
        }
        _rows[slot] = default;
        _free.Push(slot);
        _version++;
    }
}
