using System;
using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

public sealed partial class SceneStore
{
    /// <summary>SeriesEl's per-node sample payload: sparse (O(series nodes)), grow-only pooled <c>float[]</c> capacity
    /// (never shrinks), keyed by node INDEX — the exact <c>_rowCells</c> discipline (SceneStore.RowCells.cs). A write
    /// is a CAPTURED side-table write: <see cref="NoteCaptureChanged"/> first (P8), then <see cref="MarkRecordDirty(int)"/>
    /// so the recorder re-emits this node's span.</summary>
    private readonly Dictionary<int, (float[]? Arr, int Count)> _seriesSamples = new();

    public void SetSeriesSamples(NodeHandle node, ReadOnlySpan<float> samples)
    {
        int idx = (int)node.Raw.Index;
        NoteCaptureChanged(idx);
        int n = Math.Min(samples.Length, SeriesSpec.MaxSamples);
        if (n == 0)
        {
            if (_seriesSamples.Remove(idx)) MarkRecordDirty(idx);
            return;
        }
        _seriesSamples.TryGetValue(idx, out var slot);
        if (slot.Arr is null || slot.Arr.Length < n) slot.Arr = new float[Math.Max(64, n)];
        samples[..n].CopyTo(slot.Arr);
        slot.Count = n;
        _seriesSamples[idx] = slot;
        MarkRecordDirty(idx);
    }

    public bool TryGetSeriesSamples(NodeHandle h, out ReadOnlySpan<float> samples)
    {
        if (_seriesSamples.TryGetValue((int)h.Raw.Index, out var slot) && slot.Arr is not null)
        {
            samples = slot.Arr.AsSpan(0, slot.Count);
            return true;
        }
        samples = default;
        return false;
    }
}
