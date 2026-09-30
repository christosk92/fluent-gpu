namespace FluentGpu.Render.Evidence;

/// <summary>Why a slice's arena was RE-RECORDED instead of kept whole (docs/plans/evidence-diagnostics-implementation.md
/// §A.4) — the answer to "what did this render turn actually do". Classified at the recorder's keep/walk decision with
/// read-only probes; the decision itself is unchanged.</summary>
public enum WalkWhy : byte
{
    None = 0,
    /// <summary>The slice root or a descendant carries record-dirty bits (<see cref="WalkEntry.Detail"/> = the bits).</summary>
    RecordDirty = 1,
    /// <summary>Clean subtree, a span stored for the arena's current buffer — but under a different input signature
    /// (world, clip, INHERITED OPACITY, chrome …): the bytes are re-recorded with the new inputs.</summary>
    SigMiss = 2,
    /// <summary>Clean subtree, but no span stored for the arena's current buffer (a first record, or the node's span
    /// belongs to another buffer).</summary>
    NoPriorSpan = 3,
    /// <summary>A span matched but is not the whole arena (bytes around it would be lost by a keep).</summary>
    PartialSpan = 4,
    /// <summary>The node is on a blocked chain this pass (a special-cased visual inside it, a pose the kept bytes cannot
    /// honour, a baked sticky clip that moved).</summary>
    Blocked = 5,
    /// <summary>The recorder denied the keep for this slot (<c>KeepDenySlot</c>).</summary>
    KeepDeny = 6,
    /// <summary>The root slot, denied because a top-band tail rides behind it (orphans, overlays, drag chip …).</summary>
    RootTail = 7,
    /// <summary>Span reuse is off for the whole pass (<see cref="WalkEntry.Detail"/> = the <c>SpanReuseDisabledReason</c> bits).</summary>
    ReuseOff = 8,
    /// <summary>The pass records without a span table (no keep is possible).</summary>
    SpansOff = 9,
    /// <summary>A sub-slice recorded inside its parent's walk (an item band, a pinned band, a scrollbar thumb, chrome).</summary>
    ParentWalked = 10,
    /// <summary>The slice root paints nothing this pass (an invisible root): its arena is reset empty.</summary>
    Invisible = 11,
}

/// <summary>One re-recorded slice (24 bytes): the turn, the node it is cut at, the bytes its arena holds after the walk,
/// why it walked and a why-specific detail.</summary>
public struct WalkEntry
{
    /// <summary>The recorder's pass frame.</summary>
    public int Frame;
    public int NodeIndex;
    public uint Gen;
    /// <summary>Arena bytes after the walk (filled when the walk closes; −1 until then).</summary>
    public int Bytes;
    /// <summary><see cref="WalkWhy.RecordDirty"/>: the record-dirty bits; <see cref="WalkWhy.ReuseOff"/>: the disabled reasons.</summary>
    public uint Detail;
    /// <summary><see cref="WalkWhy"/>.</summary>
    public byte Why;
    /// <summary>The slice role (<c>SliceRole</c>).</summary>
    public byte Role;
}

/// <summary>
/// Why a slice root that could have been KEPT is re-recorded — the recorder's keep test replayed as a classification,
/// in the keep test's own order (it keeps only with span reuse on, the chain unblocked, the subtree record-clean, the slot
/// not denied, and a span for the arena's current buffer under the current signature covering the whole arena). Pure: the
/// scene recorder feeds it the facts it already computed for the decision (docs/plans/evidence-diagnostics-implementation.md §A.4).
/// </summary>
public static class WalkClassifier
{
    /// <param name="spansOn">The pass records with a span table.</param>
    /// <param name="reuseOff">Span reuse is disabled for the pass; <paramref name="reuseReasons"/> = its reasons.</param>
    /// <param name="blocked">The node is on a blocked chain this pass.</param>
    /// <param name="dirtyBits">The node's aggregate record-dirty bits.</param>
    /// <param name="keepDenied">The recorder denied this slot's keep; <paramref name="rootSlot"/> = it is the root slot.</param>
    /// <param name="missClass"><c>SpanTable.ClassifyMiss</c> for the arena's current buffer (0 hit, 1 none, 2 other signature).</param>
    /// <param name="wholeArena">The span that matched covers the whole arena.</param>
    public static WalkWhy Classify(bool spansOn, bool reuseOff, uint reuseReasons, bool blocked, byte dirtyBits, bool keepDenied,
        bool rootSlot, byte missClass, bool wholeArena, out uint detail)
    {
        detail = 0;
        if (!spansOn) return WalkWhy.SpansOff;
        if (reuseOff) { detail = reuseReasons; return WalkWhy.ReuseOff; }
        if (blocked) return WalkWhy.Blocked;
        if (dirtyBits != 0) { detail = dirtyBits; return WalkWhy.RecordDirty; }
        if (keepDenied) return rootSlot ? WalkWhy.RootTail : WalkWhy.KeepDeny;
        if (missClass == 1) return WalkWhy.NoPriorSpan;
        if (missClass == 2) return WalkWhy.SigMiss;
        return wholeArena ? WalkWhy.None : WalkWhy.PartialSpan;
    }
}

/// <summary>The walk ledger: every slice re-record, newest last (2048 × 24 B = 48 KiB).</summary>
public sealed class WalkLedger() : DiagRing<WalkEntry>(WalkLedgerCapacity)
{
    public const int WalkLedgerCapacity = 2048;

    /// <summary>PRODUCER ONLY. Fill the byte count of the walk written at <paramref name="index"/> (still retained).</summary>
    public void SetBytes(long index, int bytes)
    {
        if (TryAt(index, out int slot)) Slot(slot).Bytes = bytes;
    }
}
