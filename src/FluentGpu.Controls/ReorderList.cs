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
    public int DraggedIndex => _dragged;

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
            if (_target > _dragged) return _starts[_target] + _extents[_target] - _extents[_dragged];
            if (_target < _dragged) return _starts[_target];
            return _starts[_dragged];
        }
    }

    /// <summary>The pending insertion boundary in the list's resting main-axis coordinates. This follows
    /// <see cref="PendingIndex"/> immediately, before the live-reorder dwell promotes it to
    /// <see cref="TargetIndex"/>, so virtualized/list controls can show a deterministic drop cue even while
    /// displaced siblings are still waiting on WinUI's dwell timer.</summary>
    public float PendingInsertionLineOffset
    {
        get
        {
            if (_dragged < 0 || _pending < 0 || (uint)_pending >= (uint)_count) return 0f;
            float pos = _pending > _dragged
                ? _starts[_pending] + _extents[_pending] + _spacing * 0.5f
                : _starts[_pending] - _spacing * 0.5f;
            return MathF.Max(0f, pos);
        }
    }

    /// <summary>Start a reorder for <paramref name="draggedIndex"/> over items with the given resting main-axis
    /// <paramref name="itemExtents"/>, separated by <paramref name="spacing"/> (the container's Gap). Copies the
    /// extents into grow-only storage (no per-drag steady alloc once grown).</summary>
    public void Begin(int draggedIndex, ReadOnlySpan<float> itemExtents, float spacing = 0f)
    {
        if ((uint)draggedIndex >= (uint)itemExtents.Length) { Reset(); return; }
        _count = itemExtents.Length;
        if (_extents.Length < _count)
        {
            int cap = _extents.Length > 0 ? _extents.Length : 8;
            while (cap < _count) cap *= 2;
            _extents = new float[cap];
            _starts = new float[cap];
        }
        float pos = 0f;
        for (int i = 0; i < _count; i++)
        {
            _extents[i] = itemExtents[i];
            _starts[i] = pos;
            pos += itemExtents[i] + spacing;
        }
        _spacing = spacing;
        _dragged = draggedIndex;
        _pending = draggedIndex;
        _target = draggedIndex;
        _dwellRemainingMs = 0f;
    }

    /// <summary>Uniform-extent overload (fixed-row ListView / tab strip): all <paramref name="count"/> items share
    /// <paramref name="itemExtent"/>.</summary>
    public void Begin(int draggedIndex, int count, float itemExtent, float spacing = 0f)
    {
        if ((uint)draggedIndex >= (uint)count) { Reset(); return; }
        _count = count;
        if (_extents.Length < _count)
        {
            int cap = _extents.Length > 0 ? _extents.Length : 8;
            while (cap < _count) cap *= 2;
            _extents = new float[cap];
            _starts = new float[cap];
        }
        for (int i = 0; i < _count; i++)
        {
            _extents[i] = itemExtent;
            _starts[i] = i * (itemExtent + spacing);
        }
        _spacing = spacing;
        _dragged = draggedIndex;
        _pending = draggedIndex;
        _target = draggedIndex;
        _dwellRemainingMs = 0f;
    }

    /// <summary>Recompute the pending slot from the dragged item's accumulated main-axis translation
    /// (<c>DragEventArgs.TotalDy</c> / <c>TotalDx</c>). Midpoints are evaluated in RESTING coordinates, so the hint
    /// motion never feeds back into the slot math. Returns true when the pending slot changed (the dwell re-arms —
    /// ListViewBase_Partial_Reorder.cpp:1068-1074).</summary>
    public bool Update(float dragDelta)
    {
        if (_dragged < 0 || _count == 0) return false;
        float center = _starts[_dragged] + _extents[_dragged] * 0.5f + dragDelta;

        int slot = _dragged;
        if (dragDelta > 0f)
        {
            for (int j = _dragged + 1; j < _count; j++)
            {
                if (center > _starts[j] + _extents[j] * 0.5f) slot = j;
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
        int next = Math.Clamp(_target + delta, 0, _count - 1);
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
        if (_dragged < 0 || _target < 0 || index == _dragged || (uint)index >= (uint)_count) return 0f;
        float shift = _extents[_dragged] + _spacing;
        if (_target > _dragged && index > _dragged && index <= _target) return -shift;
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
            for (int i = _dragged; i < _target; i++) order[i] = i + 1;
        else
            for (int i = _dragged; i > _target; i--) order[i] = i - 1;
        order[_target] = _dragged;
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

    private void Reset()
    {
        _dragged = -1;
        _pending = -1;
        _target = -1;
        _dwellRemainingMs = 0f;
    }
}
