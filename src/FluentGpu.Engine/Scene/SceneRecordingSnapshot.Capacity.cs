using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Scene;

public sealed partial class SceneRecordingSnapshot
{
    private int _highestCapturedIndex;

    /// <summary>Highest reachable scene index plus one, not the number of live nodes. Sparse high indices need space.</summary>
    internal int RequiredNodeCapacity => Math.Max(16, _highestCapturedIndex + 1);

    /// <summary>Reserved indexed-array payload only, using runtime element sizes. Excludes headers, sparse tables and WS.</summary>
    internal long IndexedCapacityBytes
        => ((long)_handles.Length + _parent.Length + _firstChild.Length + _nextSibling.Length) * Unsafe.SizeOf<NodeHandle>()
           + (long)_bounds.Length * Unsafe.SizeOf<RectF>()
           + (long)_paint.Length * Unsafe.SizeOf<NodePaint>()
           + (long)_interaction.Length * Unsafe.SizeOf<InteractionInfo>()
           + (long)_flags.Length * Unsafe.SizeOf<NodeFlags>()
           + _dirty.Length + _dirtySelf.Length + _dirtyDescendant.Length
           + ((long)_capturedEpoch.Length + _walkedEpoch.Length + _overlayDirtyEpoch.Length + _overlaySelfEpoch.Length) * sizeof(uint)
           + (long)_overlayRow.Length * sizeof(int);

    internal void ReserveNodeCapacity(int capacity)
    {
        EnsureCapacity(Math.Max(16, capacity));
        Grow(ref _overlayRow, Capacity);
        Grow(ref _overlayDirtyEpoch, Capacity);
        Grow(ref _overlaySelfEpoch, Capacity);
    }

    private void EnsureReachableCapacity(uint index)
    {
        if (index >= (uint)Capacity) ReserveNodeCapacity(checked((int)index + 1));
    }
}
