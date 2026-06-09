using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>ItemsViewSelectionMode</c> (ItemsView.idl:6-12): None / Single / Multiple / Extended.
/// ItemsView defaults to Single (ItemsView.h <c>s_defaultSelectionMode</c>).</summary>
public enum ItemsSelectionMode : byte { None = 0, Single = 1, Multiple = 2, Extended = 3 }

/// <summary>
/// Index/RANGE-based selection state (E11-L3) — WinUI's <c>SelectionModel</c> flattened to one level (our items
/// controls are flat projections; TreeView flattens before selecting). DECOUPLED FROM REALIZATION: selection is a
/// sorted list of disjoint inclusive index ranges, so <see cref="SelectAll"/> over 50k rows stores ONE range and
/// realizes nothing — realized items read their state on prepare via <see cref="IsSelected"/> (O(log ranges)).
///
/// Signals-first: every mutation bumps <see cref="Version"/>; a composing control reads <c>Version.Value</c> in its
/// render so a selection change re-renders exactly that control (which re-skins only its realized window).
/// <see cref="SelectionChanged"/> fires once per mutating call that actually changed state (WinUI SelectionChanged).
///
/// Pointer/keyboard semantics are the verified WinUI selector trio (controls\dev\ItemsView):
/// <see cref="OnInteractedAction"/> / <see cref="OnFocusedAction"/> implement SingleSelector.cpp:25-57,
/// MultipleSelector.cpp:18-92 and ExtendedSelector.cpp:18-83 — anchor+shift ranges, ctrl toggles, focus-follow.
/// </summary>
public sealed class SelectionModel
{
    // Sorted, disjoint, non-adjacent INCLUSIVE ranges. Mutations keep the invariant; reads binary-search.
    private readonly List<(int Start, int End)> _ranges = new();

    /// <summary>Bumped on every actual change — read it in a render to subscribe that component (signals-first).</summary>
    public Signal<int> Version { get; } = new(0);

    /// <summary>Fired once per mutating call that changed the selection (WinUI <c>SelectionModel.SelectionChanged</c>).</summary>
    public Action? SelectionChanged;

    /// <summary>The collection size selection indices are clamped to. Shrinking trims out-of-range selection.</summary>
    public int ItemCount
    {
        get => _itemCount;
        set
        {
            if (value < 0) value = 0;
            if (value == _itemCount) return;
            _itemCount = value;
            if (AnchorIndex >= value) AnchorIndex = -1;
            if (TrimToCount(value)) Notify();
        }
    }
    private int _itemCount;

    /// <summary>The shift-range anchor (WinUI <c>SelectionModel.AnchorIndex</c>). −1 = none. Single item ops move it.</summary>
    public int AnchorIndex { get; set; } = -1;

    /// <summary>Selection mode. Coercing to <see cref="ItemsSelectionMode.Single"/> keeps only the first selected
    /// item; None leaves existing (programmatic) selection readable but routes interactions to a null selector.</summary>
    public ItemsSelectionMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            _mode = value;
            if (value == ItemsSelectionMode.Single && SelectedCount > 1)
            {
                int keep = FirstSelectedIndex;
                _ranges.Clear();
                _ranges.Add((keep, keep));
                Notify();
            }
        }
    }
    private ItemsSelectionMode _mode = ItemsSelectionMode.Single;   // ItemsView.h s_defaultSelectionMode = Single

    /// <summary>In Single mode, moving keyboard focus without Ctrl selects the focused item
    /// (WinUI SingleSelector.h <c>m_followFocus</c>, default true).</summary>
    public bool SingleSelectionFollowsFocus { get; set; } = true;

    // ── queries (all allocation-free) ───────────────────────────────────────────────────────────────

    public bool IsSelected(int index)
    {
        int r = FindRangeContaining(index);
        return r >= 0;
    }

    /// <summary>Total selected items (sum over ranges, O(ranges) — never walks indices).</summary>
    public int SelectedCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _ranges.Count; i++) n += _ranges[i].End - _ranges[i].Start + 1;
            return n;
        }
    }

    public int FirstSelectedIndex => _ranges.Count == 0 ? -1 : _ranges[0].Start;
    public int LastSelectedIndex => _ranges.Count == 0 ? -1 : _ranges[^1].End;
    public int RangeCount => _ranges.Count;
    /// <summary>The i-th selected range (inclusive) — for tests/serialization without realizing indices.</summary>
    public (int Start, int End) GetRange(int i) => _ranges[i];

    /// <summary>Append every selected index into <paramref name="into"/> (cleared first). The WinUI
    /// <c>SelectedItems</c> projection — call only when a materialized list is genuinely needed.</summary>
    public void GetSelectedIndices(List<int> into)
    {
        into.Clear();
        for (int r = 0; r < _ranges.Count; r++)
            for (int i = _ranges[r].Start; i <= _ranges[r].End; i++)
                into.Add(i);
    }

    // ── mutations (programmatic API: ItemsView.idl:53-58 Select/Deselect/IsSelected/SelectAll/DeselectAll/InvertSelection) ──

    /// <summary>Select one item. In Single mode this REPLACES the selection (WinUI <c>SingleSelect</c>).</summary>
    public void Select(int index)
    {
        if ((uint)index >= (uint)_itemCount) return;
        bool changed;
        if (_mode == ItemsSelectionMode.Single)
        {
            changed = !(SelectedCount == 1 && _ranges[0].Start == index);
            _ranges.Clear();
            _ranges.Add((index, index));
        }
        else changed = SelectRangeCore(index, index);
        AnchorIndex = index;
        if (changed) Notify();
    }

    public void Deselect(int index)
    {
        if ((uint)index >= (uint)_itemCount) return;
        bool changed = DeselectRangeCore(index, index);
        AnchorIndex = index;
        if (changed) Notify();
    }

    public void Toggle(int index)
    {
        if (IsSelected(index)) Deselect(index);
        else Select(index);
    }

    /// <summary>Select an inclusive index range (clamped). Stores RANGES — O(ranges), realizes nothing.</summary>
    public void SelectRange(int start, int end)
    {
        if (Normalize(ref start, ref end) && SelectRangeCore(start, end)) Notify();
    }

    public void DeselectRange(int start, int end)
    {
        if (Normalize(ref start, ref end) && DeselectRangeCore(start, end)) Notify();
    }

    /// <summary>WinUI <c>SelectRangeFromAnchorTo</c>: select [anchor, index] (order-normalized); anchor stays.</summary>
    public void SelectRangeFromAnchorTo(int index)
    {
        int a = AnchorIndex < 0 ? index : AnchorIndex;
        SelectRange(Math.Min(a, index), Math.Max(a, index));
    }

    /// <summary>WinUI <c>DeselectRangeFromAnchorTo</c>: deselect [anchor, index]; anchor stays.</summary>
    public void DeselectRangeFromAnchorTo(int index)
    {
        int a = AnchorIndex < 0 ? index : AnchorIndex;
        DeselectRange(Math.Min(a, index), Math.Max(a, index));
    }

    /// <summary>ONE stored range regardless of count — select-all over 50k realizes nothing.</summary>
    public void SelectAll()
    {
        if (_itemCount == 0) return;
        bool changed = !(_ranges.Count == 1 && _ranges[0] == (0, _itemCount - 1));
        _ranges.Clear();
        _ranges.Add((0, _itemCount - 1));
        if (changed) Notify();
    }

    public void DeselectAll()
    {
        if (_ranges.Count == 0) return;
        _ranges.Clear();
        Notify();
    }

    /// <summary>Alias for <see cref="DeselectAll"/> (WinUI <c>ClearSelection</c>).</summary>
    public void ClearSelection() => DeselectAll();

    /// <summary>Complement over [0, ItemCount): the gaps become the ranges — O(ranges), realizes nothing.</summary>
    public void InvertSelection()
    {
        if (_itemCount == 0) return;
        var inverted = new List<(int, int)>(_ranges.Count + 1);
        int cursor = 0;
        for (int r = 0; r < _ranges.Count; r++)
        {
            if (_ranges[r].Start > cursor) inverted.Add((cursor, _ranges[r].Start - 1));
            cursor = _ranges[r].End + 1;
        }
        if (cursor <= _itemCount - 1) inverted.Add((cursor, _itemCount - 1));
        _ranges.Clear();
        _ranges.AddRange(inverted);
        Notify();
    }

    // ── WinUI interaction semantics (the ItemsView selector trio, verbatim) ───────────────────────────

    /// <summary>Pointer tap / Space / Enter on item <paramref name="index"/> with the live modifier chord.</summary>
    public void OnInteractedAction(int index, bool ctrl, bool shift)
    {
        if ((uint)index >= (uint)_itemCount) return;
        switch (_mode)
        {
            case ItemsSelectionMode.None:
                return;   // NullSelector — interactions never select

            case ItemsSelectionMode.Single:
                // SingleSelector.cpp:25-44: no Ctrl → select; Ctrl → toggle.
                if (!ctrl) Select(index);
                else if (!IsSelected(index)) Select(index);
                else Deselect(index);
                return;

            case ItemsSelectionMode.Multiple:
                // MultipleSelector.cpp:18-63: Shift → extend/deselect range from anchor by the ANCHOR's state
                // (only when anchor and target state differ); otherwise toggle.
                if (shift && AnchorIndex >= 0)
                {
                    bool anchorSelected = IsSelected(AnchorIndex);
                    bool indexSelected = IsSelected(index);
                    if (anchorSelected != indexSelected)
                    {
                        if (anchorSelected) SelectRangeFromAnchorTo(index);
                        else DeselectRangeFromAnchorTo(index);
                    }
                }
                else if (IsSelected(index)) Deselect(index);
                else Select(index);
                return;

            case ItemsSelectionMode.Extended:
                // ExtendedSelector.cpp:18-53: Shift → replace with anchor range; Ctrl → toggle;
                // plain → clear+select ONLY when interacting with a different (unselected) item.
                if (shift)
                {
                    int anchor = AnchorIndex;
                    if (anchor >= 0)
                    {
                        _ranges.Clear();
                        AnchorIndex = anchor;
                        SelectRangeFromAnchorTo(index);   // notifies (the clear made it a change)
                    }
                }
                else if (ctrl)
                {
                    if (IsSelected(index)) Deselect(index);
                    else Select(index);
                }
                else if (!IsSelected(index))
                {
                    _ranges.Clear();
                    Select(index);
                }
                return;
        }
    }

    /// <summary>Keyboard focus moved to item <paramref name="index"/> (arrows/Home/End/typeahead).</summary>
    public void OnFocusedAction(int index, bool ctrl, bool shift)
    {
        if ((uint)index >= (uint)_itemCount) return;
        switch (_mode)
        {
            case ItemsSelectionMode.None:
                return;

            case ItemsSelectionMode.Single:
                // SingleSelector.cpp:46-57: focus selects unless Ctrl is held (follow-focus).
                if (!ctrl && SingleSelectionFollowsFocus) Select(index);
                return;

            case ItemsSelectionMode.Multiple:
                // MultipleSelector.cpp:65-92: only Shift extends by the anchor's state; plain moves never select.
                if (shift && AnchorIndex >= 0)
                {
                    if (IsSelected(AnchorIndex)) SelectRangeFromAnchorTo(index);
                    else DeselectRangeFromAnchorTo(index);
                }
                return;

            case ItemsSelectionMode.Extended:
                // ExtendedSelector.cpp:55-83: Shift+Ctrl → additive anchor range; Shift → replace with anchor range;
                // plain (no Ctrl) → clear+select; Ctrl alone → focus moves without selection.
                if (shift && ctrl)
                {
                    if (AnchorIndex >= 0) SelectRangeFromAnchorTo(index);
                }
                else if (shift)
                {
                    int anchor = AnchorIndex;
                    if (anchor >= 0)
                    {
                        _ranges.Clear();
                        AnchorIndex = anchor;
                        SelectRangeFromAnchorTo(index);
                    }
                }
                else if (!ctrl)
                {
                    _ranges.Clear();
                    Select(index);
                }
                return;
        }
    }

    // ── range plumbing (sorted + disjoint + non-adjacent invariant) ────────────────────────────────────

    private void Notify()
    {
        Version.Value = Version.Peek() + 1;
        SelectionChanged?.Invoke();
    }

    private bool Normalize(ref int start, ref int end)
    {
        if (start > end) (start, end) = (end, start);
        start = Math.Max(0, start);
        end = Math.Min(_itemCount - 1, end);
        return start <= end;
    }

    /// <summary>Index of the range containing <paramref name="index"/>, or −1 (binary search by Start).</summary>
    private int FindRangeContaining(int index)
    {
        int lo = 0, hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var (s, e) = _ranges[mid];
            if (index < s) hi = mid - 1;
            else if (index > e) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>First range with End ≥ <paramref name="index"/> (the leftmost candidate for overlap scans).</summary>
    private int LowerBound(int index)
    {
        int lo = 0, hi = _ranges.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ranges[mid].End < index) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private bool SelectRangeCore(int start, int end)
    {
        // Merge every range overlapping or ADJACENT to [start,end] into one (start ≥ 0 here, so start−1 is safe).
        int i = LowerBound(start - 1);
        int mergedStart = start, mergedEnd = end;
        int removeFrom = i, removeCount = 0;
        long covered = 0;
        while (i < _ranges.Count && _ranges[i].Start <= end + 1)
        {
            mergedStart = Math.Min(mergedStart, _ranges[i].Start);
            mergedEnd = Math.Max(mergedEnd, _ranges[i].End);
            covered += _ranges[i].End - _ranges[i].Start + 1;
            removeCount++;
            i++;
        }
        bool changed = covered != (long)(mergedEnd - mergedStart + 1);
        if (removeCount > 0) _ranges.RemoveRange(removeFrom, removeCount);
        _ranges.Insert(removeFrom, (mergedStart, mergedEnd));
        return changed;
    }

    private bool DeselectRangeCore(int start, int end)
    {
        int i = LowerBound(start);
        bool changed = false;
        while (i < _ranges.Count && _ranges[i].Start <= end)
        {
            var (s, e) = _ranges[i];
            if (s >= start && e <= end)
            {
                _ranges.RemoveAt(i);              // fully covered → drop
                changed = true;
            }
            else if (s < start && e > end)
            {
                _ranges[i] = (s, start - 1);      // split: keep both flanks
                _ranges.Insert(i + 1, (end + 1, e));
                return true;
            }
            else if (s < start)
            {
                _ranges[i] = (s, start - 1);      // trim the right edge
                changed = true;
                i++;
            }
            else
            {
                _ranges[i] = (end + 1, e);        // trim the left edge
                changed = true;
                i++;
            }
        }
        return changed;
    }

    private bool TrimToCount(int count)
    {
        bool changed = false;
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            if (_ranges[i].Start >= count) { _ranges.RemoveAt(i); changed = true; }
            else if (_ranges[i].End >= count) { _ranges[i] = (_ranges[i].Start, count - 1); changed = true; }
        }
        return changed;
    }
}
