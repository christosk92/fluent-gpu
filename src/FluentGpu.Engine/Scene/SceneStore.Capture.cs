using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// Operation ultra-fast GPU engine, P8 — the <b>capture ledger</b>: the store's own record of "which nodes have a
/// captured column that changed since publication N", so <c>SceneRecordingSnapshot.CaptureIncremental</c> can copy
/// O(changed) rows on a coast frame instead of O(reachable) every publication.
/// <para><b>Why this is not just the record-dirty ledger.</b> <c>_recordDirtyStamp</c> answers a RENDERER question
/// ("what must be re-recorded") and is deliberately incomplete for a COPY question: layout writes <c>Bounds</c> for a
/// whole subtree off one dirty ancestor, <c>ClearTransformDirty</c>/<c>ClearRecordDirty</c> mutate the flag and
/// dirty-bit columns with no mark at all, and hover/press/focus flip <c>NodeFlags</c> without a record mark. Every one
/// of those is a captured column, so an incremental capture keyed on record-dirty alone would publish a stale row —
/// the worst possible failure (the render thread paints last frame's geometry and nothing throws).</para>
/// <para><b>The contract.</b> Every mutation of a column <see cref="SceneRecordingSnapshot"/> copies must either
/// (a) call <see cref="NoteCaptureChanged"/> for that node, or (b) be covered by <see cref="NoteBulkMutation"/> —
/// the "I can't enumerate what I touched" escape hatch that forces the NEXT capture of every slot to be a full one.
/// Layout passes and reconciler commits take (b) wholesale (they write half the columns across arbitrary subtrees);
/// scroll/animation/input/side-table writes take (a) precisely. Anything unproven takes (b): a slow full capture is
/// always correct, a wrong incremental one never is.</para>
/// <para>Retention: entries live until <see cref="ClearCaptureLedger"/> is called with a sequence the OLDEST of the
/// publisher's three slots has already captured — see <c>SceneFramePublisher.OldestSlotCaptureSeq</c>. Below that
/// floor the ledger is no longer a complete history, which <see cref="CaptureLedgerFloor"/> reports so a snapshot
/// asking for an older baseline falls back to a full capture instead of guessing.</para>
/// </summary>
public sealed partial class SceneStore
{
    // The publication that first includes each node's most recent captured-column change (same _publishSeq + 1 stamp
    // convention as _recordDirtyStamp: a write made after publication K belongs to K+1).
    private ulong[] _captureStamp = [];
    // The publication a node's slot was (re)allocated in — a recycled index must be copied wholesale, never merged
    // onto its predecessor's rows.
    private ulong[] _createdStamp = [];
    // The compact list of nodes with a live ledger entry (dedup by _inCaptureList, exactly like _recordDirtyWrote).
    private int[] _captureWrote = [];
    private bool[] _inCaptureList = [];
    private int _captureWroteCount;
    private ulong _bulkMutationSeq;
    private ulong _captureLedgerFloor;

    /// <summary>Nodes with a captured-column change still in the ledger. Order is arbitrary; duplicates impossible.</summary>
    internal ReadOnlySpan<int> CaptureChangedNodes => _captureWrote.AsSpan(0, _captureWroteCount);

    /// <summary>The publication a node's most recent captured-column change belongs to (0 = never changed / trimmed).</summary>
    internal ulong CaptureStampAt(int idx)
        => (uint)idx < (uint)_captureStamp.Length ? _captureStamp[idx] : 0UL;

    /// <summary>The publication this slot was (re)allocated in — <c>SceneStore.CreateNode</c>'s own stamp.</summary>
    internal ulong CreatedStampAt(int idx)
        => (uint)idx < (uint)_createdStamp.Length ? _createdStamp[idx] : 0UL;

    /// <summary>The newest publication that carries a mutation the ledger could NOT enumerate (a layout pass, a
    /// reconciler commit, a column resize). A snapshot whose baseline is older than this cannot be updated
    /// incrementally — its missing rows are exactly the ones nobody recorded.</summary>
    internal ulong BulkMutationSeq => _bulkMutationSeq;

    /// <summary>Ledger entries at or below this sequence have been dropped, so the ledger is only a complete history
    /// of publications ABOVE it. A capture asking for an older baseline must fall back to a full copy.</summary>
    internal ulong CaptureLedgerFloor => _captureLedgerFloor;

    /// <summary>Ledger one node's captured-column change. O(1) — deliberately NOT an ancestor walk (unlike
    /// <c>MarkRecordDirty</c>, whose up-propagation is a RENDERER semantic: an ancestor span covers its descendants).
    /// A copy is per-node, so only the node that actually changed needs an entry.</summary>
    internal void NoteCaptureChanged(int idx)
    {
        if ((uint)idx >= (uint)_high) return;
        if (_captureStamp.Length <= idx) GrowCaptureLedger(idx + 1);
        _captureStamp[idx] = _publishSeq + 1;
        if (_inCaptureList[idx]) return;
        _inCaptureList[idx] = true;
        if (_captureWroteCount == _captureWrote.Length)
            Array.Resize(ref _captureWrote, Math.Max(16, _captureWrote.Length * 2));
        _captureWrote[_captureWroteCount++] = idx;
    }

    /// <summary>Declare that something touched captured columns in a way the ledger cannot enumerate. Every snapshot
    /// whose baseline predates this publication falls back to a full capture. The honest escape hatch: a layout pass
    /// rewrites <c>Bounds</c> across whole subtrees off one dirty ancestor, and a reconciler commit rewrites
    /// <c>LayoutInput</c>/<c>NodePaint</c>/<c>InteractionInfo</c>/<c>NodeFlags</c> across the nodes it patched —
    /// enumerating either precisely would mean auditing hundreds of write sites, and a missed one is a stale-pixel
    /// bug. Coast frames (the case P8 exists for) do neither.</summary>
    public void NoteBulkMutation() => _bulkMutationSeq = _publishSeq + 1;

    /// <summary>Drop ledger entries the OLDEST publisher slot has already captured (stamp ≤ <paramref name="retainSeq"/>)
    /// and raise the floor, so a later capture cannot silently trust a truncated history. O(entries), compacts in place —
    /// the same shape as <c>ClearRecordDirty(ulong)</c>.</summary>
    internal void ClearCaptureLedger(ulong retainSeq)
    {
        if (retainSeq > _captureLedgerFloor) _captureLedgerFloor = retainSeq;
        int kept = 0;
        for (int i = 0; i < _captureWroteCount; i++)
        {
            int idx = _captureWrote[i];
            if ((uint)idx >= (uint)_captureStamp.Length) continue;
            if (_captureStamp[idx] > retainSeq) { _captureWrote[kept++] = idx; continue; }
            _inCaptureList[idx] = false;
        }
        for (int i = kept; i < _captureWroteCount; i++) _captureWrote[i] = 0;
        _captureWroteCount = kept;
    }

    /// <summary>Stamp a freshly allocated slot (called from <c>CreateNode</c>).</summary>
    private void NoteCaptureCreated(int idx)
    {
        if (_createdStamp.Length <= idx) GrowCaptureLedger(idx + 1);
        _createdStamp[idx] = _publishSeq + 1;
    }

    private void GrowCaptureLedger(int need)
    {
        int n = Math.Max(need, Math.Max(16, _captureStamp.Length * 2));
        Array.Resize(ref _captureStamp, n);
        Array.Resize(ref _createdStamp, n);
        Array.Resize(ref _inCaptureList, n);
    }

    /// <summary>Column arrays were reallocated — index identity may not survive a shrink, and a grow leaves the new
    /// tail uninitialized as far as any snapshot is concerned. Either way the ledger cannot describe the delta.</summary>
    private void NoteCaptureColumnsResized(int n)
    {
        if (_captureStamp.Length < n) GrowCaptureLedger(n);
        NoteBulkMutation();
    }
}
