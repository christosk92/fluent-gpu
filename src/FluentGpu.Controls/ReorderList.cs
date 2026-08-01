namespace FluentGpu.Controls;

/// <summary>
/// Reorder geometry for a one-axis item strip (the WinUI live-reorder model — ListViewBase_Partial_Reorder.cpp):
/// given the items' resting main-axis extents and the dragged item's accumulated drag delta
/// (<c>DragEventArgs.TotalDy</c> for a vertical list / <c>TotalDx</c> for a horizontal strip), it computes
/// <list type="bullet">
/// <item>the PENDING insertion slot under the pointer (midpoint rule: the dragged item's centre crossing a sibling's
/// midpoint claims its slot — the engine analogue of <c>IItemLookupPanel.GetClosestElementInfo</c> +
/// <c>LiveReorderHelper::MovedItems::GetDragOverIndex</c>, ListViewBase_Partial_Reorder.cpp:984-1063),</item>
/// <item>the dwell-committed TARGET slot — WinUI moves displaced items only after the drag-over index has been stable
/// for the live-reorder timer (200ms list / 300ms grid — LISTVIEW_LIVEREORDER_TIMER / GRIDVIEW_LIVEREORDER_TIMER,
/// ListViewBase_Partial_Reorder.cpp:50-51), and</item>
/// <item>per-sibling displacement hints (<see cref="OffsetFor"/>): items between the dragged slot and the target shift
/// by one full dragged-extent (+spacing), the live-reorder "items part to make room" motion
/// (LiveReorderTimerTickHandler → MoveItemsForLiveReorder, ListViewBase_Partial_Reorder.cpp:2125-2158).</item>
/// </list>
/// Consumers (ListView/GridView/TabView/TreeView item hosts) wire it to the drag lifecycle: <c>Begin</c> in
/// <c>OnDragStarted</c>, <c>Update(e.TotalDy)</c> in <c>OnDragDelta</c>, <c>Advance(dtMs)</c> from the frame clock;
/// when <c>Advance</c> commits a new target, re-render with the children ORDER projected to it
/// (<see cref="ProjectOrder"/> — stable keys keep node identity) and a <c>LayoutTransition</c> (<c>Animate</c>) on
/// every item: displaced siblings genuinely change slots, so the engine's FLIP pipeline animates the
/// part-to-make-room motion, and <c>Input.DragController</c> re-anchors the pointer-held visual to its moved slot (no
/// jump). <see cref="OffsetFor"/> remains for offset-hint consumers — but an authored <c>OffsetX/Y</c> hint must NOT
/// be combined with <c>Animate</c> on the same node: FLIP position tracks own the whole translate channel
/// (<c>CompositeOp.Replace</c> — AnimEngine.ReframePosition) and would stomp the hint at seed and settle.
/// <c>Complete()</c> in <c>OnDragCompleted</c> fires <see cref="OnCommit"/> with the collection move;
/// <c>Cancel()</c> in <c>OnDragCanceled</c> drops every hint. All state is grow-only — steady-state reorder
/// (drag at pointer rate) allocates nothing.
///
/// <para>TWO-DIMENSIONAL MODE (<see cref="Begin2D"/> / <see cref="Update2D"/> / <see cref="OffsetFor2D"/>) absorbs the
/// GridView live-reorder geometry: the pending slot comes from the dragged tile's accumulated (dx,dy) against the grid
/// (row from the vertical stride, column from the realized column width), with the 300ms grid dwell
/// (GRIDVIEW_LIVEREORDER_TIMER = 300ms — ListViewBase_Partial_Reorder.cpp:51) re-armed on every drag-over change. A
/// one-slot grid shift can WRAP A ROW (the trailing tile of a row dropping to the head of the next), so the 2-D
/// displacement hint carries BOTH axes. The dwell ticker (<see cref="Advance"/>), the moved-items projection
/// (<see cref="ProjectOrder"/> — row-major, identical to 1-D), <see cref="Complete"/> and <see cref="Cancel"/> are
/// shared with the 1-D path unchanged. <see cref="Columns"/> = 0 selects the 1-D path; <see cref="Begin2D"/> sets it.</para>
/// </summary>
public sealed class ReorderList
{
    /// <summary>Hint dwell before displaced items shift — LISTVIEW_LIVEREORDER_TIMER = 200ms
    /// (ListViewBase_Partial_Reorder.cpp:50).</summary>
    public const float ListDwellMs = 200f;

    /// <summary>GridView's dwell — GRIDVIEW_LIVEREORDER_TIMER = 300ms (ListViewBase_Partial_Reorder.cpp:51).</summary>
    public const float GridDwellMs = 300f;

    private float[] _starts = [];    // resting main-axis start per item (prefix sums; grow-only)
    private float[] _extents = [];   // main-axis extent per item (grow-only)
    private int _count;
    private float _spacing;
    private int _dragged = -1;
    private int _block = 1;          // contiguous dragged BLOCK length (1 = the classic single-item reorder)
    private int _pending = -1;       // latest computed slot (under the pointer)
    private int _target = -1;        // dwell-committed slot the hints currently show
    private float _dwellRemainingMs;

    /// <summary>Dwell before a new pending slot becomes the shown target (WinUI restarts the timer on every drag-over
    /// index change — ListViewBase_Partial_Reorder.cpp:1068-1074). 0 ⇒ hints follow the pointer immediately.</summary>
    public float DwellMs { get; set; } = ListDwellMs;

    /// <summary>The collection move, fired by <see cref="Complete"/> when the item lands on a new slot:
    /// (fromIndex, toIndex) in the ORIGINAL order — remove at <c>from</c>, insert at <c>to</c>.</summary>
    public Action<int, int>? OnCommit;

    public bool IsActive => _dragged >= 0;
    public int Count => _count;
    /// <summary>The dragged block's FIRST original index (the single dragged item when <see cref="BlockLength"/> is 1).</summary>
    public int DraggedIndex => _dragged;

    /// <summary>How many CONTIGUOUS items are being dragged as one unit (ruling e). 1 (the default, and what every
    /// <c>Begin</c> overload without a block argument sets) is the classic single-item reorder, byte-identical to the
    /// pre-block engine — the block arithmetic below reduces to the old expressions term for term at length 1, and the
    /// existing gates pin that.
    /// <para>Slots are expressed as the block's landing START, so a valid target is 0..<c>Count − BlockLength</c>.
    /// Non-contiguous multi-selection is deliberately NOT this API's job: it rides the insertion view's virtual-removal
    /// math (design ruling a), which needs no notion of a single moving run.</para></summary>
    public int BlockLength => _block;

    /// <summary>Grid column count for the 2-D mode (0 ⇒ 1-D mode). Set by <see cref="Begin2D"/>; the 1-D
    /// <see cref="Begin(int,System.ReadOnlySpan{float},float)"/> overloads leave it 0.</summary>
    public int Columns { get; private set; }

    /// <summary>The latest computed insertion slot under the pointer (becomes <see cref="TargetIndex"/> after the dwell).</summary>
    public int PendingIndex => _pending;

    /// <summary>The dwell-committed slot the displacement hints currently show (= <see cref="DraggedIndex"/> until the
    /// first dwell elapses).</summary>
    public int TargetIndex => _target;

    /// <summary>The resting main-axis start the dragged item will occupy at the current <see cref="TargetIndex"/> —
    /// the settle target for a consumer-drawn drop preview.</summary>
    public float DraggedTargetStart
    {
        get
        {
            if (_dragged < 0 || _target < 0) return 0f;
            if (_target < _dragged) return _starts[_target];
            if (_target == _dragged) return _starts[_dragged];
            // Forward: the block starts where the run it jumped over used to end. At BlockLength 1 the general form
            // below is algebraically the old `_starts[t] + ext[t] − ext[d]`, but not bit-for-bit in float — so the
            // single-item path keeps the exact old expression and the block path is the generalization.
            if (_block <= 1) return _starts[_target] + _extents[_target] - _extents[_dragged];
            return _starts[_dragged] + BoundaryStart(_target + _block) - BoundaryStart(_dragged + _block);
        }
    }

    /// <summary>Resting start of slot <paramref name="i"/>, extended one slot past the end (the append boundary).</summary>
    private float BoundaryStart(int i)
        => i < _count ? _starts[i] : _starts[_count - 1] + _extents[_count - 1] + _spacing;

    /// <summary>Total main-axis room the dragged block occupies INCLUDING its inter-item spacing — the amount every
    /// displaced sibling shifts by. Reduces to <c>_extents[_dragged] + _spacing</c> at length 1.</summary>
    private float BlockShift()
    {
        float shift = 0f;
        for (int i = 0; i < _block; i++) shift += _extents[_dragged + i] + _spacing;
        return shift;
    }

    /// <summary>Start a reorder for <paramref name="draggedIndex"/> over items with the given resting main-axis
    /// <paramref name="itemExtents"/>, separated by <paramref name="spacing"/> (the container's Gap). Copies the
    /// extents into grow-only storage (no per-drag steady alloc once grown).</summary>
    public void Begin(int draggedIndex, ReadOnlySpan<float> itemExtents, float spacing = 0f)
    {
        if ((uint)draggedIndex >= (uint)itemExtents.Length) { Reset(); return; }
        Sample(itemExtents, spacing);
        StartDrag(draggedIndex, 1);
    }

    /// <summary>Block overload (ruling e): drag <paramref name="blockLength"/> CONTIGUOUS items starting at
    /// <paramref name="draggedIndex"/> as one unit. <c>blockLength = 1</c> is exactly the overload above.</summary>
    public void Begin(int draggedIndex, int blockLength, ReadOnlySpan<float> itemExtents, float spacing = 0f)
    {
        if (!ValidBlock(draggedIndex, blockLength, itemExtents.Length)) { Reset(); return; }
        Sample(itemExtents, spacing);
        StartDrag(draggedIndex, blockLength);
    }

    /// <summary>Uniform-extent overload (fixed-row ListView / tab strip): all <paramref name="count"/> items share
    /// <paramref name="itemExtent"/>.</summary>
    public void Begin(int draggedIndex, int count, float itemExtent, float spacing = 0f)
    {
        if ((uint)draggedIndex >= (uint)count) { Reset(); return; }
        Sample(count, itemExtent, spacing);
        StartDrag(draggedIndex, 1);
    }

    /// <summary>Uniform-extent BLOCK overload (ruling e) — the fixed-row list / tab-strip shape with a multi-item
    /// selection. Deliberately NOT another <c>Begin</c>: <c>Begin(i, n, extent)</c> and a block overload
    /// <c>Begin(i, block, n, extent)</c> differ only by an int-vs-float third argument, and an integral extent literal
    /// would silently bind to the wrong one.</summary>
    public void BeginBlock(int draggedIndex, int blockLength, int count, float itemExtent, float spacing = 0f)
    {
        if (!ValidBlock(draggedIndex, blockLength, count)) { Reset(); return; }
        Sample(count, itemExtent, spacing);
        StartDrag(draggedIndex, blockLength);
    }

    private static bool ValidBlock(int draggedIndex, int blockLength, int count)
        => draggedIndex >= 0 && blockLength >= 1 && count > 0 && draggedIndex + blockLength <= count;

    /// <summary>Refresh the RESTING geometry table WITHOUT starting a drag — the cross-list hover path, where a
    /// foreign session needs a slot before any local lift exists (and therefore before <see cref="Begin"/> has ever
    /// run). Grow-only, allocation-free once grown; leaves the drag state untouched.</summary>
    public void Sample(ReadOnlySpan<float> itemExtents, float spacing = 0f)
    {
        EnsureCapacity(itemExtents.Length);
        _count = itemExtents.Length;
        float pos = 0f;
        for (int i = 0; i < _count; i++)
        {
            _extents[i] = itemExtents[i];
            _starts[i] = pos;
            pos += itemExtents[i] + spacing;
        }
        _spacing = spacing;
    }

    /// <summary>Uniform-extent <see cref="Sample(ReadOnlySpan{float},float)"/>.</summary>
    public void Sample(int count, float itemExtent, float spacing = 0f)
    {
        count = Math.Max(0, count);
        EnsureCapacity(count);
        _count = count;
        for (int i = 0; i < _count; i++)
        {
            _extents[i] = itemExtent;
            _starts[i] = i * (itemExtent + spacing);
        }
        _spacing = spacing;
    }

    /// <summary>Insertion slot (0..<see cref="Count"/>) for a resting main-axis offset, over the SAMPLED prefix sums —
    /// so a variable-extent list (an <c>ExtentOf</c> consumer) resolves the slot exactly instead of assuming a uniform
    /// pitch (C3). Reduces to the uniform midpoint rule byte-for-byte when the extents are equal.</summary>
    public int SlotAtOffset(float mainOffset)
    {
        if (_count <= 0 || !float.IsFinite(mainOffset) || mainOffset <= _starts[0]) return 0;
        int lo = 0, hi = _count - 1;
        while (lo < hi)                                   // largest i with _starts[i] <= mainOffset
        {
            int mid = (lo + hi + 1) >> 1;
            if (_starts[mid] <= mainOffset) lo = mid; else hi = mid - 1;
        }
        int slot = mainOffset > _starts[lo] + _extents[lo] * 0.5f ? lo + 1 : lo;
        return Math.Clamp(slot, 0, _count);
    }

    /// <summary>Resting main-axis offset of the boundary BEFORE <paramref name="slot"/> (the insertion-line position),
    /// centred in the inter-item gap. Reads the sampled prefix sums — variable extents included.</summary>
    public float BoundaryOffset(int slot)
    {
        if (_count <= 0) return 0f;
        slot = Math.Clamp(slot, 0, _count);
        float pos = slot < _count ? _starts[slot] : _starts[_count - 1] + _extents[_count - 1] + _spacing;
        return pos - _spacing * 0.5f;
    }

    private void EnsureCapacity(int count)
    {
        if (_extents.Length >= count) return;
        int cap = _extents.Length > 0 ? _extents.Length : 8;
        while (cap < count) cap *= 2;
        _extents = new float[cap];
        _starts = new float[cap];
    }

    private void StartDrag(int draggedIndex, int blockLength)
    {
        _dragged = draggedIndex;
        _block = blockLength < 1 ? 1 : blockLength;
        _pending = draggedIndex;
        _target = draggedIndex;
        _dwellRemainingMs = 0f;
        Columns = 0;   // 1-D mode
    }

    /// <summary>Begin a 2-D (grid) reorder for <paramref name="draggedIndex"/> over <paramref name="count"/> tiles laid
    /// out row-major in <paramref name="columns"/> columns (absorbs the GridView live-reorder geometry). No resting
    /// extents are stored — the 2-D slot math is grid-geometric (<see cref="Update2D"/> takes the realized column width
    /// and row stride per move). Sets <see cref="Columns"/> &gt; 0 to select the 2-D path.</summary>
    public void Begin2D(int draggedIndex, int count, int columns)
    {
        if ((uint)draggedIndex >= (uint)count) { Reset(); return; }
        _count = count;
        Columns = Math.Max(1, columns);
        _dragged = draggedIndex;
        _block = 1;   // the grid path is single-tile by construction
        _pending = draggedIndex;
        _target = draggedIndex;
        _dwellRemainingMs = 0f;
    }

    /// <summary>Recompute the 2-D pending slot from the dragged tile's accumulated translation
    /// (<paramref name="totalDx"/>, <paramref name="totalDy"/>): column from <paramref name="colWidth"/>, row from
    /// <paramref name="rowStride"/>, clamped to the grid. Returns true when the pending slot changed (the dwell re-arms
    /// to <see cref="DwellMs"/> — already 300 for the grid preset; ListViewBase_Partial_Reorder.cpp:1068-1074). A
    /// verbatim port of the GridView 2-D update.</summary>
    public bool Update2D(float totalDx, float totalDy, float colWidth, float rowStride)
    {
        if (_dragged < 0) return false;
        int cols = Columns < 1 ? 1 : Columns;
        int row0 = _dragged / cols, col0 = _dragged % cols;
        int col = Math.Clamp(col0 + (int)MathF.Round(totalDx / MathF.Max(1f, colWidth)), 0, cols - 1);
        int row = Math.Max(0, row0 + (int)MathF.Round(totalDy / MathF.Max(1f, rowStride)));
        int slot = Math.Clamp(row * cols + col, 0, _count - 1);
        if (slot == _pending) return false;
        _pending = slot;
        _dwellRemainingMs = DwellMs;   // re-arm on every drag-over change (cpp:1068-1074)
        return true;
    }

    /// <summary>The 2-D per-tile displacement hint at the current shown target: a tile in the block between the dragged
    /// slot and the target shifts by ONE slot toward the vacated source (row-major), which can WRAP A ROW — so the hint
    /// carries both axes (<paramref name="dx"/>, <paramref name="dy"/>). The dragged tile and everything outside the
    /// block get (0,0). A verbatim port of the GridView 2-D OffsetFor (ListViewBase_Partial_Reorder.cpp:2125-2158).</summary>
    public void OffsetFor2D(int index, float colWidth, float rowStride, out float dx, out float dy)
    {
        dx = 0f; dy = 0f;
        if (_dragged < 0 || _target < 0 || index == _dragged || (uint)index >= (uint)_count) return;
        int cols = Columns < 1 ? 1 : Columns;
        // Forward drag (target after source): tiles (dragged, target] move back one slot (toward source).
        // Backward drag: tiles [target, dragged) move forward one slot.
        int shifted;
        if (_target > _dragged) { if (index <= _dragged || index > _target) return; shifted = index - 1; }
        else { if (index >= _dragged || index < _target) return; shifted = index + 1; }
        int r0 = index / cols, c0 = index % cols;
        int r1 = shifted / cols, c1 = shifted % cols;
        dx = (c1 - c0) * colWidth;
        dy = (r1 - r0) * rowStride;
    }

    /// <summary>Recompute the pending slot from the dragged item's accumulated main-axis translation
    /// (<c>DragEventArgs.TotalDy</c> / <c>TotalDx</c>). Midpoints are evaluated in RESTING coordinates, so the hint
    /// motion never feeds back into the slot math. Returns true when the pending slot changed (the dwell re-arms —
    /// ListViewBase_Partial_Reorder.cpp:1068-1074).</summary>
    public bool Update(float dragDelta)
    {
        if (_dragged < 0 || _count == 0) return false;
        int last = _dragged + _block - 1;
        // The BLOCK's centre (its own leading start to its own trailing end); at length 1 this is the old expression.
        float center = _block <= 1
            ? _starts[_dragged] + _extents[_dragged] * 0.5f + dragDelta
            : _starts[_dragged] + (_starts[last] + _extents[last] - _starts[_dragged]) * 0.5f + dragDelta;

        int slot = _dragged;
        if (dragDelta > 0f)
        {
            // Passing item j (the first item BEYOND the block) lands the block's start at j − block + 1.
            for (int j = last + 1; j < _count; j++)
            {
                if (center > _starts[j] + _extents[j] * 0.5f) slot = j - _block + 1;
                else break;
            }
        }
        else if (dragDelta < 0f)
        {
            for (int j = _dragged - 1; j >= 0; j--)
            {
                if (center < _starts[j] + _extents[j] * 0.5f) slot = j;
                else break;
            }
        }

        if (slot == _pending) return false;
        _pending = slot;
        _dwellRemainingMs = DwellMs;
        return true;
    }

    /// <summary>Advance the live-reorder dwell (drive from the frame clock while a drag is active). Returns true when
    /// the shown target changed — re-render the siblings with the new <see cref="OffsetFor"/> hints (their
    /// <c>LayoutTransition</c> FLIP animates the shift).</summary>
    public bool Advance(float dtMs)
    {
        if (_dragged < 0 || _pending == _target) return false;
        _dwellRemainingMs -= dtMs;
        if (_dwellRemainingMs > 0f) return false;
        _dwellRemainingMs = 0f;
        _target = _pending;
        return true;
    }

    /// <summary>Keyboard lift-mode move (rbd a11y — E5-L3): shift the shown target directly by <paramref name="delta"/>
    /// slots, clamped to the list, with NO dwell (a deliberate keystroke needs no stabilization timer; pending and
    /// target stay in lockstep so <see cref="Complete"/> commits exactly what the user sees). Returns true when the
    /// shown target changed — re-render with <see cref="ProjectOrder"/> and the FLIP pipeline animates the move.</summary>
    public bool MoveTarget(int delta)
    {
        if (_dragged < 0 || _count == 0 || delta == 0) return false;
        int next = Math.Clamp(_target + delta, 0, _count - _block);
        if (next == _target) return false;
        _target = next;
        _pending = next;
        _dwellRemainingMs = 0f;
        return true;
    }

    /// <summary>The main-axis displacement hint for sibling <paramref name="index"/> at the current shown target:
    /// items between the dragged slot and the target shift one dragged-extent (+spacing) to make room; everything
    /// else (and the dragged item itself) is 0.</summary>
    public float OffsetFor(int index)
    {
        if (_dragged < 0 || _target < 0 || (uint)index >= (uint)_count) return 0f;
        if (index >= _dragged && index < _dragged + _block) return 0f;   // the block itself never takes a hint
        float shift = BlockShift();
        // Forward: everything from just past the block up to the block's new trailing edge closes the gap it left.
        if (_target > _dragged && index >= _dragged + _block && index <= _target + _block - 1) return -shift;
        if (_target < _dragged && index >= _target && index < _dragged) return shift;
        return 0f;
    }

    /// <summary>Fill <paramref name="order"/> (length ≥ <see cref="Count"/>) with ORIGINAL item indices in the current
    /// dwell-committed projected order: the dragged item occupies <see cref="TargetIndex"/>, everything else keeps its
    /// relative order (the live-reorder "moved items" view — LiveReorderHelper::MovedItems,
    /// ListViewBase_Partial_Reorder.cpp:2125-2157). Re-render the children from this projection (stable keys!) with a
    /// <c>LayoutTransition</c> and the FLIP pipeline animates the displaced siblings. Span-filling — 0 alloc.</summary>
    public void ProjectOrder(Span<int> order)
    {
        for (int i = 0; i < _count; i++) order[i] = i;
        if (_dragged < 0 || _target < 0 || _target == _dragged) return;
        if (_target > _dragged)
            for (int i = _dragged; i < _target; i++) order[i] = i + _block;
        else
            for (int i = _dragged + _block - 1; i >= _target + _block; i--) order[i] = i - _block;
        for (int k = 0; k < _block; k++) order[_target + k] = _dragged + k;
    }

    /// <summary>Finish the reorder at the LATEST pending slot (the release point under the pointer — the dwell shown
    /// state never delays the actual drop). Resets all hints BEFORE firing <see cref="OnCommit"/>, so the commit's
    /// re-render reads zero offsets. Returns the destination index (−1 when idle).</summary>
    public int Complete()
    {
        if (_dragged < 0) return -1;
        int from = _dragged;
        int to = _pending >= 0 ? _pending : from;
        Reset();
        if (to != from) OnCommit?.Invoke(from, to);
        return to;
    }

    /// <summary>Abort the reorder (drag canceled): drop every hint without committing.</summary>
    public void Cancel() => Reset();

    /// <summary>Apply a committed reorder to a collection in place: remove at <paramref name="from"/>, insert at
    /// <paramref name="to"/> — exactly the <see cref="OnCommit"/> payload, and exactly WinUI's drop commit
    /// (ListViewBase::ReorderItemsTo → RemoveAt(realItemIndex) + InsertAt(insertIndex),
    /// ListViewBase_Partial_Reorder.cpp:1536-1537). Out-of-range / no-op indices are ignored. Cold path (one drop).</summary>
    public static void Move<T>(IList<T> list, int from, int to)
    {
        if (from == to || (uint)from >= (uint)list.Count || (uint)to >= (uint)list.Count) return;
        T item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    /// <summary>Block form of <see cref="Move{T}(IList{T},int,int)"/> (ruling e): lift <paramref name="blockLength"/>
    /// contiguous items at <paramref name="from"/> and re-insert them at <paramref name="to"/> — which, exactly like
    /// the single-item form, is the POST-removal index (the same number <see cref="ProjectOrder"/> projects to and
    /// <see cref="Complete"/> commits). <c>blockLength == 1</c> delegates to the single-item overload verbatim.</summary>
    public static void Move<T>(IList<T> list, int from, int blockLength, int to)
    {
        if (blockLength <= 1) { Move(list, from, to); return; }
        if (from == to || from < 0 || from + blockLength > list.Count) return;
        if ((uint)to > (uint)(list.Count - blockLength)) return;
        var lifted = new T[blockLength];
        for (int i = 0; i < blockLength; i++) lifted[i] = list[from + i];
        for (int i = blockLength - 1; i >= 0; i--) list.RemoveAt(from + i);
        for (int i = 0; i < blockLength; i++) list.Insert(to + i, lifted[i]);
    }

    private void Reset()
    {
        _dragged = -1;
        _block = 1;
        _pending = -1;
        _target = -1;
        _dwellRemainingMs = 0f;
        Columns = 0;
    }
}
