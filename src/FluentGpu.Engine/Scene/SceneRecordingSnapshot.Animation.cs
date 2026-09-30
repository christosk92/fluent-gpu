using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// The render thread's compositor overlay for one publisher slot — the poses
/// <c>FluentGpu.Animation.RenderCompositorAnimations</c> writes ON TOP OF this snapshot's AUTHORED columns while the UI
/// thread is busy, without ever mutating them (<c>gate.compositor-authored-isolation</c>).
///
/// <para><b>Sparse by value, dense only by lookup.</b> The overlay used to be three columns sized at the scene's node
/// high-water — <c>NodePaint[capacity]</c> + <c>InteractionAnim[capacity]</c> + <c>BrushAnim[capacity]</c> plus four
/// per-node epoch stamps: 508 B PER NODE per snapshot, across all three publisher slots. On the 32 768-node scene the
/// native ARM64 tour reached that is 47.6 MiB of overlay for a workload where a few dozen nodes animate at once
/// (`mem.sample` reported 28–61 live animation tracks). It is now a bounded ROW POOL plus a 4-byte node→row lookup:
/// one row per node that actually animates this tick, carrying all three payloads behind presence bits. Same
/// arithmetic at 32 768 nodes and the default 256-row reserve: 509 KiB per snapshot, 1.49 MiB across the three slots
/// (three 4-byte node columns since §13.1 split the pose's own-change stamp out of the ancestor dirty trail).
/// The row pool's size is a function of how many nodes ANIMATE, never of <see cref="Capacity"/> — that is the whole
/// point, and <c>gate.compositor-row-sparse</c> asserts it.</para>
///
/// <para><b>Who may size it, and when.</b> The pool is reserved on the PUBLISHER side at exclusive capture time and is
/// NEVER grown during a compositor tick — the render side allocates nothing (<c>gate.compositor-alloc</c>,
/// <c>gate.compositor-row-alloc</c>). <see cref="ReserveCompositorRows"/> is called by <c>SceneRenderFrame.Capture</c>
/// AFTER that publication's animation descriptions have been captured, so the reserve is the publication's own demand
/// (<c>CompositorAnimationSnapshot.DistinctNodeCount</c>) plus <see cref="CompositorRowHeadroom"/>, floored at
/// <see cref="MinCompositorRows"/> and capped at <see cref="MaxCompositorRows"/>; the reserve only ever grows, like
/// every other buffer on this snapshot. Because the renderer adopts the animation snapshot and the scene snapshot from
/// the SAME slot (<c>AppHost</c>: <c>Adopt(sceneFrame.Animations, sceneFrame.Scene, …)</c>, and an animation-only turn
/// re-ticks that same retained slot), the rows a tick can ask for are exactly the distinct nodes that publication
/// described: on the host path the pool cannot overflow.</para>
///
/// <para><b>The documented overflow fallback.</b> A caller that drives <see cref="BeginCompositorOverlay"/> + a tick
/// against a snapshot whose capture never described those animations — the public API used directly, and
/// <c>gate.compositor-row-overflow</c> — can exhaust the pool. The pool then does not grow, does not allocate and does
/// not throw. Deterministically: the overflowing write lands in <see cref="_overlaySpill"/> (a discard sink seeded from
/// the node's authored row, so a caller's <c>ref</c> writes stay well-defined and are simply not published), the
/// node's ancestor dirty chain is STILL marked so the node re-records, and the peak demand is remembered in
/// <see cref="CompositorRowDemand"/>. The next capture into this snapshot reserves that demand, and from that
/// publication on every value lands. The animation STATE is never lost — trajectories and the feedback poses live in
/// <c>RenderCompositorAnimations</c>, not in the overlay — so an overflow costs at most one tick of one node presented
/// at its authored pose, and it self-heals at the next publication instead of persisting.</para>
/// </summary>
public sealed partial class SceneRecordingSnapshot
{
    /// <summary>One animating node's overlay: all three payloads plus which of them have been seeded this epoch.
    /// Rows are pooled, so the payloads are deliberately NOT cleared on release — <see cref="Have"/> is what makes a
    /// reused row read as empty, and every seeded payload is overwritten from the authored column on first touch.</summary>
    private struct OverlayRow
    {
        public NodePaint Paint;
        public InteractionAnim Interaction;
        public BrushAnim Brush;
        public int Node;    // the scene slot this row is bound to for the current overlay epoch
        public byte Have;   // HavePaint | HaveInteraction | HaveBrush
    }

    private const byte HavePaint = 1, HaveInteraction = 2, HaveBrush = 4;

    /// <summary>The floor every captured snapshot reserves — comfortably above the measured 28–61 concurrent
    /// animation tracks, so the demand-driven reserve below is a safety net rather than the steady-state path.</summary>
    internal const int MinCompositorRows = 256;

    /// <summary>Slack added to a publication's own animating-node count, so a burst that starts between two captures
    /// still lands in the pool the previous capture reserved.</summary>
    internal const int CompositorRowHeadroom = 32;

    /// <summary>The hard ceiling on the pool — the "bounded" in "bounded slot pool". At 500 B/row this caps the
    /// overlay at ~1.95 MiB of rows per snapshot (5.86 MiB across the three slots) however many nodes a pathological
    /// scene animates at once, versus the 15.9 MiB per snapshot the dense overlay cost at 32 768 nodes. A scene that
    /// genuinely animates more distinct nodes than this in one tick takes the overflow fallback documented above.</summary>
    internal const int MaxCompositorRows = 4096;

    private OverlayRow[] _overlayRows = [];
    private int[] _overlayRow = [];             // node index → row + 1 (0 = this node has no row this epoch)
    private uint[] _overlayDirtyEpoch = [];
    /// <summary>Per node: the epoch in which this node's OWN composited pose last CHANGED.
    /// <para>Distinct from <see cref="_overlayDirtyEpoch"/>, which is the ancestor TRAIL — "something under here moved,
    /// re-walk me, do not replay a stale span containing the child's old pixels". The trail runs to the ROOT by
    /// construction, so feeding it to <see cref="Flags"/> made every ancestor report TransformDirty, and the recorder
    /// damages a TransformDirty node's SubtreeBounds — whose value at the root IS the window. That is why one animated
    /// leaf repainted the whole window, against gpu-renderer.md §13.1's promise that "a spinner repaints a tiny
    /// region". Span reuse still needs the trail and keeps reading it; only the two readers that may PRODUCE repaint
    /// damage read this one.</para></summary>
    private uint[] _overlaySelfEpoch = [];
    private uint _overlayEpoch = 1;
    private int _overlayRowCount;               // rows handed out during the current overlay epoch
    private int _overlayOverflowCount;          // row requests this epoch that found the pool full
    private int _overlayRowDemand;              // sticky peak demand, honoured by the next ReserveCompositorRows
    // The overflow discard sink. Seeded from the authored row so a ref-write against it is well-defined; never read
    // back, because every read path resolves through _overlayRow and an overflowing node has no row.
    private NodePaint _overlaySpill;

    /// <summary>Rows the pool can hold. O(animating nodes), never O(<see cref="Capacity"/>) —
    /// <c>gate.compositor-row-sparse</c>.</summary>
    internal int CompositorRowCapacity => _overlayRows.Length;

    /// <summary>Rows bound to a node during the current overlay epoch (diagnostics/gates).</summary>
    internal int CompositorRowsInUse => _overlayRowCount;

    /// <summary>Row requests during the current overlay epoch that found the pool full (diagnostics/gates). 0 on the
    /// host path by construction; non-zero drives the publisher-side growth described on the type.</summary>
    internal int CompositorRowOverflows => _overlayOverflowCount;

    /// <summary>The peak row demand seen since the last reserve — what the next capture will reserve. Cleared by
    /// <see cref="ReserveCompositorRows"/> once honoured.</summary>
    internal int CompositorRowDemand => _overlayRowDemand;

    /// <summary>Bytes this snapshot's compositor overlay holds: the row pool plus the three 4-byte-per-node side
    /// arrays plus the spill row. The gate compares it against the dense overlay's 508 B/node.</summary>
    internal long CompositorOverlayBytes
        => (long)_overlayRows.Length * Unsafe.SizeOf<OverlayRow>()
           + (long)_overlayRow.Length * sizeof(int)
           + (long)_overlayDirtyEpoch.Length * sizeof(uint)
           + (long)_overlaySelfEpoch.Length * sizeof(uint)
           + Unsafe.SizeOf<NodePaint>();

    /// <summary>Bytes one pooled row costs (diagnostics/gates) — the sum of the three payloads plus the row header.</summary>
    internal static int CompositorRowBytes => Unsafe.SizeOf<OverlayRow>();

    /// <summary>Bytes the DELETED dense overlay would cost for <paramref name="nodeCapacity"/> slots: the three
    /// payload columns plus the four per-node epoch stamps. Gates use it as the before-figure so the saving is
    /// measured from the real struct sizes rather than asserted from a comment.</summary>
    internal static long DenseCompositorOverlayBytes(int nodeCapacity)
        => (long)nodeCapacity * (Unsafe.SizeOf<NodePaint>() + Unsafe.SizeOf<InteractionAnim>()
            + Unsafe.SizeOf<BrushAnim>() + 4 * sizeof(uint));

    private void PrepareCompositorOverlay(int count)
    {
        Grow(ref _overlayRow, count);
        Grow(ref _overlayDirtyEpoch, count);
        Grow(ref _overlaySelfEpoch, count);
        BeginCompositorOverlay();
        // The floor, plus anything a previous tick overflowed on. The publisher raises this again from the captured
        // animation descriptions (SceneRenderFrame.Capture → ReserveCompositorRows) once they exist for this frame.
        ReserveCompositorRows(0);
    }

    /// <summary>
    /// Publisher-side, exclusive-capture-time reserve for the compositor row pool. Called AFTER this publication's
    /// animation descriptions are captured, with the number of distinct nodes they name; a tick can never ask for
    /// more rows than that, so the render thread never has to grow anything. Monotone (the pool is a high-water mark
    /// like every other snapshot buffer) and capped at <see cref="MaxCompositorRows"/>.
    /// </summary>
    internal void ReserveCompositorRows(int nodeDemand)
    {
        int want = Math.Max(MinCompositorRows, Math.Max(nodeDemand + CompositorRowHeadroom, _overlayRowDemand));
        if (want > MaxCompositorRows) want = MaxCompositorRows;
        _overlayRowDemand = 0;
        if (_overlayRows.Length >= want) return;
        Array.Resize(ref _overlayRows, want);
    }

    /// <summary>Render-thread-only start of a pose evaluation. Authored snapshot columns remain unchanged; every row
    /// the previous epoch bound is released in O(rows in use) — no per-node clear, no epoch sweep.</summary>
    internal void BeginCompositorOverlay()
    {
        BeginPosedScroll();
        for (int i = 0; i < _overlayRowCount; i++) _overlayRow[_overlayRows[i].Node] = 0;
        _overlayRowCount = 0;
        _overlayOverflowCount = 0;
        if (++_overlayEpoch != 0) return;
        Array.Clear(_overlayDirtyEpoch);
        Array.Clear(_overlaySelfEpoch);
        _overlayEpoch = 1;
    }

    /// <param name="changed">False when the pose being written is the SAME value this row posed last tick — a row held
    /// by its cadence, or one that is already Done. Such a tick must still write the pose (the node reads its posed
    /// paint, and its span still re-records if something else dirtied it) but must contribute NO repaint band: posing
    /// an unchanged value as a change is what made a 60 Hz marquee damage the window at panel rate.</param>
    internal ref NodePaint CompositorPaint(NodeHandle node, bool changed = true)
    {
        uint index = node.Raw.Index;
        int slot = AcquireOverlayRow(index);
        // Stamp the self epoch only on a row we actually GOT. An overflowing node discards its pose and presents its
        // authored one, which gate.compositor-row-overflow pins as "not self-dirty"; the ancestor trail is still
        // marked, because the subtree must re-record either way.
        if (changed && slot >= 0) MarkCompositorSelfChanged(node); else MarkCompositorDirty(node);
        if (slot < 0)
        {
            _overlaySpill = _paint[index];   // discarded; the node presents its authored pose for this tick
            return ref _overlaySpill;
        }
        ref OverlayRow row = ref _overlayRows[slot];
        if ((row.Have & HavePaint) == 0)
        {
            row.Paint = _paint[index];
            row.Have |= HavePaint;
        }
        return ref row.Paint;
    }

    /// <summary>A scroll POSE write (content translate, a scroll effect's folded transform): the overlay row WITHOUT the
    /// dirty trail or the self epoch. Since the retained-tile recorder partition a pose is a COMPOSITE parameter — a
    /// translation slice records pose-free and the composite places it (<c>SliceRecorder</c>), and a pose recorded inline
    /// is a BAKED pose the slice recorder checks itself — so posing re-records nothing and damages nothing by itself.
    /// Same row pool and overflow fallback as <see cref="CompositorPaint"/>.</summary>
    internal ref NodePaint CompositorPosePaint(NodeHandle node)
    {
        uint index = node.Raw.Index;
        int slot = AcquireOverlayRow(index);
        if (slot < 0)
        {
            _overlaySpill = _paint[index];   // discarded; the node presents its authored pose for this tick
            return ref _overlaySpill;
        }
        ref OverlayRow row = ref _overlayRows[slot];
        if ((row.Have & HavePaint) == 0)
        {
            row.Paint = _paint[index];
            row.Have |= HavePaint;
        }
        return ref row.Paint;
    }

    /// <param name="changed">See <see cref="CompositorPaint"/> — false re-poses an identical value and damages nothing.</param>
    internal void SetCompositorInteraction(NodeHandle node, bool press, float value, bool changed = true)
    {
        uint index = node.Raw.Index;
        int slot = AcquireOverlayRow(index);
        if (changed && slot >= 0) MarkCompositorSelfChanged(node); else MarkCompositorDirty(node);
        if (slot < 0) return;
        ref OverlayRow row = ref _overlayRows[slot];
        if ((row.Have & HaveInteraction) == 0)
        {
            _interact.TryGet((int)index, out row.Interaction);
            row.Have |= HaveInteraction;
        }
        if (press) row.Interaction.PressT = value; else row.Interaction.HoverT = value;
    }

    /// <param name="changed">See <see cref="CompositorPaint"/> — false re-poses an identical value and damages nothing.</param>
    internal void SetCompositorBrush(NodeHandle node, float value, bool changed = true)
    {
        uint index = node.Raw.Index;
        int slot = AcquireOverlayRow(index);
        if (changed && slot >= 0) MarkCompositorSelfChanged(node); else MarkCompositorDirty(node);
        if (slot < 0) return;
        ref OverlayRow row = ref _overlayRows[slot];
        if ((row.Have & HaveBrush) == 0)
        {
            _brushAnim.TryGet((int)index, out row.Brush);
            row.Have |= HaveBrush;
        }
        row.Brush.T = value;
    }

    /// <summary>This node's row for the current overlay epoch, binding a free one on first touch. -1 means the pool is
    /// full: the caller takes the documented overflow fallback (no growth, no allocation, no throw) and the demand is
    /// remembered for the next publisher-side reserve.</summary>
    private int AcquireOverlayRow(uint index)
    {
        int slot = _overlayRow[index] - 1;
        if (slot >= 0) return slot;
        if (_overlayRowCount == _overlayRows.Length)
        {
            _overlayOverflowCount++;
            NoteOverlayRowDemand();
            return -1;
        }
        slot = _overlayRowCount++;
        _overlayRows[slot].Node = (int)index;
        _overlayRows[slot].Have = 0;
        _overlayRow[index] = slot + 1;
        NoteOverlayRowDemand();
        return slot;
    }

    // Rows in use plus overflowing requests. Repeat requests from the same overflowing node over-count, which is the
    // safe direction for a RESERVE: the next capture asks for at least as many rows as this tick wanted.
    private void NoteOverlayRowDemand()
    {
        int demand = _overlayRowCount + _overlayOverflowCount;
        if (demand > _overlayRowDemand) _overlayRowDemand = demand;
    }

    private void MarkCompositorDirty(NodeHandle node)
    {
        for (var current = node; !current.IsNull; current = Parent(current))
        {
            uint index = current.Raw.Index;
            if (_overlayDirtyEpoch[index] == _overlayEpoch) break;
            _overlayDirtyEpoch[index] = _overlayEpoch;
        }
    }

    /// <summary>Mark this node's OWN pose as changed this epoch — the only claim that may produce a repaint band —
    /// plus the ancestor trail, which every re-record still needs. The pair is the whole of §13.1's fix: the trail
    /// reaches the root by construction, so a node that is merely ON the trail must not be reported as having moved.
    /// A pose that re-poses the same value calls <see cref="MarkCompositorDirty"/> alone.</summary>
    internal void MarkCompositorSelfChanged(NodeHandle node)
    {
        _overlaySelfEpoch[node.Raw.Index] = _overlayEpoch;
        MarkCompositorDirty(node);
    }
}
