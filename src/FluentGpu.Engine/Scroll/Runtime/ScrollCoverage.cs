using System;
using FluentGpu.Scroll.Effects;

namespace FluentGpu.Scroll.Runtime;

/// <summary>One viewport's realized-content coverage as published by the UI thread's <see cref="Virtualizer"/>
/// (design §2/§5): which content node carries the rows, where the realized window starts/ends in content
/// coordinates, and the effect rows scoped to this viewport. POD — copied by value into the render thread's
/// <see cref="ScrollPoser"/> at adopt time.</summary>
/// <param name="Vp">The viewport's scene node index (the <see cref="ScrollViewportId.Node"/> half of the plan key).</param>
/// <param name="Gen">The viewport node's generation (the <see cref="ScrollViewportId.Gen"/> half).</param>
/// <param name="ContentNodeIndex">The scene node whose compositor translate the poser writes (the scroller's content child).</param>
/// <param name="WindowOrigin">Content offset the realized rows were arranged relative to (<c>OffsetOf(first)</c>); the
/// posed translate is <c>WindowOrigin − p'</c>, a small float relative to this origin, never the raw offset.</param>
/// <param name="Start">Content offset where realized rows begin (<c>OffsetOf(first)</c>).</param>
/// <param name="End">Content offset where realized rows end (<c>OffsetOf(last + 1)</c>).</param>
/// <param name="Viewport">The viewport extent along the scroll axis.</param>
/// <param name="ExtentTotal">The whole content extent along the scroll axis.</param>
/// <param name="Horizontal">True when the scroll axis is X.</param>
/// <param name="EffectStart">Index of this viewport's first row in <see cref="ScrollCoverageTable.Effects"/>.</param>
/// <param name="EffectCount">Number of effect rows scoped to this viewport.</param>
/// <param name="FrameShift">The plan slot's cumulative coordinate-frame shift (<see cref="PlanSlots.FrameShiftOf"/>) this
/// coverage was laid out under. <see cref="WindowOrigin"/>/<see cref="Start"/>/<see cref="End"/> and the effect geometry
/// are in THAT frame; a measured correction that shifts the plan after this row was built (mid-layout, before the next
/// publication is adopted) is compensated by the poser instead of showing as a transient jump of the correction.</param>
public readonly record struct ScrollCoverageRow(
    int Vp,
    uint Gen,
    int ContentNodeIndex,
    double WindowOrigin,
    double Start,
    double End,
    double Viewport,
    double ExtentTotal,
    bool Horizontal,
    int EffectStart,
    int EffectCount,
    double FrameShift)
{
    public ScrollViewportId Id => new(Vp, Gen);
}

/// <summary>One scroll-linked effect binding captured at publish: the target node, the effect and the geometry the
/// UI thread measured for it this frame. Evaluated by the poser against the SAME snapped position as the content.</summary>
public readonly record struct ScrollEffectRow(int NodeIndex, ScrollEffect Effect, EffectGeometry Geometry);

/// <summary>
/// Fixed-capacity value holder for every viewport's <see cref="ScrollCoverageRow"/> plus a shared pool of
/// <see cref="ScrollEffectRow"/>s, filled by the UI thread at publish time and copied (<see cref="CopyFrom"/>, no
/// allocation) into the render thread's <see cref="ScrollPoser"/>. Designed to live inside the published scene
/// snapshot: the publisher owns one instance per snapshot buffer, <c>Clear()</c>s and refills it each frame, and the
/// render thread adopts the snapshot's instance wholesale. Capacity: <see cref="RowCapacity"/> viewports (matches
/// <see cref="PlanSlots.Capacity"/>) and <see cref="EffectCapacity"/> effect rows.
/// </summary>
public sealed class ScrollCoverageTable
{
    public const int RowCapacity = PlanSlots.Capacity;
    public const int EffectCapacity = 512;

    private readonly ScrollCoverageRow[] _rows = new ScrollCoverageRow[RowCapacity];
    private readonly ScrollEffectRow[] _effects = new ScrollEffectRow[EffectCapacity];
    private int _rowCount;
    private int _effectCount;

    public int RowCount => _rowCount;
    public int EffectCount => _effectCount;

    public ReadOnlySpan<ScrollCoverageRow> Rows => new(_rows, 0, _rowCount);
    public ReadOnlySpan<ScrollEffectRow> Effects => new(_effects, 0, _effectCount);

    /// <summary>Row <paramref name="i"/> by reference (read-only).</summary>
    public ref readonly ScrollCoverageRow RowAt(int i) => ref _rows[i];

    /// <summary>Effect row <paramref name="i"/> by reference (read-only).</summary>
    public ref readonly ScrollEffectRow EffectAt(int i) => ref _effects[i];

    /// <summary>Empties the table for a fresh publish. O(1) — rows are overwritten, never zeroed.</summary>
    public void Clear()
    {
        _rowCount = 0;
        _effectCount = 0;
    }

    /// <summary>Appends an effect row to the pool and returns its index, or -1 when the pool is full. Effect rows for
    /// one viewport must be appended contiguously (record <see cref="EffectCount"/> before the first, and pass the
    /// pair as <see cref="ScrollCoverageRow.EffectStart"/>/<see cref="ScrollCoverageRow.EffectCount"/>).</summary>
    public int AddEffect(in ScrollEffectRow row)
    {
        if (_effectCount >= EffectCapacity) return -1;
        _effects[_effectCount] = row;
        return _effectCount++;
    }

    /// <summary>Appends a coverage row and returns its index, or -1 when the table is full.</summary>
    public int AddRow(in ScrollCoverageRow row)
    {
        if (_rowCount >= RowCapacity) return -1;
        _rows[_rowCount] = row;
        return _rowCount++;
    }

    /// <summary>Convenience for the common shape: append <paramref name="effects"/> to the pool, then the row with its
    /// effect range filled in. Effects that do not fit are dropped (the row still publishes).</summary>
    public int AddRow(ScrollCoverageRow row, ReadOnlySpan<ScrollEffectRow> effects)
    {
        int start = _effectCount;
        int added = 0;
        for (int i = 0; i < effects.Length; i++)
            if (AddEffect(in effects[i]) >= 0) added++;
        return AddRow(row with { EffectStart = start, EffectCount = added });
    }

    /// <summary>Finds the row for <paramref name="vp"/>; -1 if absent.</summary>
    public int IndexOf(ScrollViewportId vp)
    {
        for (int i = 0; i < _rowCount; i++)
            if (_rows[i].Vp == vp.Node && _rows[i].Gen == vp.Gen) return i;
        return -1;
    }

    /// <summary>Copies <paramref name="other"/>'s rows and effect pool into this instance without allocating.</summary>
    public void CopyFrom(ScrollCoverageTable other)
    {
        if (ReferenceEquals(other, this)) return;
        Array.Copy(other._rows, _rows, other._rowCount);
        Array.Copy(other._effects, _effects, other._effectCount);
        _rowCount = other._rowCount;
        _effectCount = other._effectCount;
    }
}
