using System;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Recycling-safe live insertion projection for a vertical ItemsView. The resting item model never swaps during the
/// gesture: rows at or after the slot receive one placement displacement, while the destination draws its preview in
/// the opened gap. Slot changes retarget through ItemsView's existing <c>MotionTok.ItemPlacement</c> seed path.
/// </summary>
public sealed class VirtualInsertionPreviewController
{
    readonly Signal<int> _version;
    int _slot = -1;
    int _firstItemIndex;
    float _extent;

    public VirtualInsertionPreviewController(Signal<int>? sharedVersion = null)
        => _version = sharedVersion ?? new Signal<int>(0);

    public IReadSignal<int> Version => _version;
    public bool Active => _slot >= 0 && _extent > 0f;
    public int Slot => _slot;
    public float Extent => _extent;

    /// <summary>Open or retarget the gap. Returns true only when the published projection changed.</summary>
    public bool Update(int slot, int firstItemIndex, float extent)
    {
        slot = Math.Max(0, slot);
        firstItemIndex = Math.Max(0, firstItemIndex);
        extent = float.IsFinite(extent) ? Math.Max(0f, extent) : 0f;
        if (_slot == slot && _firstItemIndex == firstItemIndex && MathF.Abs(_extent - extent) <= 0.01f)
            return false;
        _slot = extent > 0f ? slot : -1;
        _firstItemIndex = firstItemIndex;
        _extent = extent;
        Bump();
        return true;
    }

    public bool Clear()
    {
        if (_slot < 0 && _extent <= 0f) return false;
        _slot = -1;
        _extent = 0f;
        Bump();
        return true;
    }

    /// <summary>Stable ItemsView displacement callback. Item indices include any persistent-prefix rows.</summary>
    public (float dx, float dy) DisplacementFor(int itemIndex)
        => Active && itemIndex >= _firstItemIndex + _slot ? (0f, _extent) : (0f, 0f);

    void Bump() => _version.Value = _version.Peek() + 1;
}
