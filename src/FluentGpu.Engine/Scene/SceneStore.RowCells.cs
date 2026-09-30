using FluentGpu.Foundation;

namespace FluentGpu.Scene;

public sealed partial class SceneStore
{
    /// <summary>ListRowEl's per-node cell payload (scroll-rework Wave 0.E): sparse, O(rows actually mounted) — most
    /// nodes never populate this table. Grow-only pooled <see cref="RowCellRecorded"/>[] capacity (never shrinks),
    /// like <c>_spanText</c>/<c>_textEditSelRects</c>. Keyed by node INDEX (handles carry a generation these tables
    /// don't; freeing must clear the slot — done in <see cref="SetRowCells"/> when the new count is 0, same
    /// discipline as <c>SetSpanText</c>).</summary>
    private readonly Dictionary<int, (RowCellRecorded[]? Arr, int Count, bool Placeholder, ColorF PlaceholderColor)> _rowCells = new();

    /// <summary>Sparse index-resolved cell-click handler (<c>ListRowEl.OnCellClick</c>) — mount-static, rewritten
    /// unconditionally each WriteColumns pass (a plain reference set on an existing dictionary key allocates nothing),
    /// the exact <c>_spanClickHandlers</c> shape. NOT a captured/recorded column (the input dispatcher reads it
    /// straight off this live SceneStore, never off a <see cref="SceneRecordingSnapshot"/>), so writes here don't
    /// need <see cref="NoteCaptureChanged"/>.</summary>
    private readonly Dictionary<int, Action<int>> _rowCellClickHandlers = new();

    public void SetRowCells(NodeHandle node, ReadOnlySpan<RowCellRecorded> cells, bool placeholder, ColorF placeholderColor)
    {
        int idx = (int)node.Raw.Index;
        NoteCaptureChanged(idx);   // P8: write-intent accessor for a captured side table (SceneRecordingSnapshot's own _rowCells column)
        if (cells.Length == 0)
        {
            _rowCells.Remove(idx);
            return;
        }
        _rowCells.TryGetValue(idx, out var slot);
        int n = cells.Length;
        if (slot.Arr is null || slot.Arr.Length < n) slot.Arr = new RowCellRecorded[System.Math.Max(8, n)];
        cells.CopyTo(slot.Arr);
        slot.Count = n;
        slot.Placeholder = placeholder;
        slot.PlaceholderColor = placeholderColor;
        _rowCells[idx] = slot;
    }

    /// <summary>Rebind-only path (<c>Placeholder</c>/<c>PlaceholderColor</c> bound independently of <c>Cells</c>):
    /// flips the recorder's draw mode WITHOUT touching the cell array — a no-op on a node with no cells yet (the
    /// unbound <c>Cells</c> write that follows will carry the current flag forward via <see cref="SetRowCells"/>).</summary>
    public void SetRowPlaceholder(NodeHandle node, bool placeholder, ColorF placeholderColor)
    {
        int idx = (int)node.Raw.Index;
        if (!_rowCells.TryGetValue(idx, out var slot)) return;
        NoteCaptureChanged(idx);   // P8: see SetRowCells
        slot.Placeholder = placeholder;
        slot.PlaceholderColor = placeholderColor;
        _rowCells[idx] = slot;
    }

    public bool TryGetRowCells(NodeHandle h, out ReadOnlySpan<RowCellRecorded> cells, out bool placeholder, out ColorF placeholderColor)
    {
        if (_rowCells.TryGetValue((int)h.Raw.Index, out var slot) && slot.Arr is not null)
        {
            cells = slot.Arr.AsSpan(0, slot.Count);
            placeholder = slot.Placeholder;
            placeholderColor = slot.PlaceholderColor;
            return true;
        }
        cells = default;
        placeholder = false;
        placeholderColor = default;
        return false;
    }

    public void SetRowCellClickHandler(NodeHandle node, Action<int>? handler)
    {
        int idx = (int)node.Raw.Index;
        if (handler is null) _rowCellClickHandlers.Remove(idx);
        else _rowCellClickHandlers[idx] = handler;
    }

    public bool TryGetRowCellClickHandler(NodeHandle h, out Action<int> handler)
        => _rowCellClickHandlers.TryGetValue((int)h.Raw.Index, out handler!);

    /// <summary>FreeSubtreeCore's ListRow teardown: releases every cell's interned Text/FontFamily id (the same
    /// per-field ref-count TextEl's own free path runs, Reconciler.ListRow.cs's WriteRowCells doc comment) and drops
    /// both sparse side-table entries — called only when this node ever carried a row (VisualKind.ListRow), so a
    /// scene with no ListRowEl nodes pays nothing extra on free.</summary>
    private void ReleaseRowCells(int idx)
    {
        if (_rowCells.TryGetValue(idx, out var slot) && slot.Arr is not null && Strings is { } st)
            for (int i = 0; i < slot.Count; i++)
            {
                if (!slot.Arr[i].Text.IsEmpty) st.Release(slot.Arr[i].Text);
                if (!slot.Arr[i].FontFamily.IsEmpty) st.Release(slot.Arr[i].FontFamily);
            }
        _rowCells.Remove(idx);
        _rowCellClickHandlers.Remove(idx);
    }
}
