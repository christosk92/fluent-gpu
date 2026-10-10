namespace FluentGpu.Scene;

/// <summary>The presented-flow walk ONE parent runs over its children (SizeMode.FlowReveal): each child draws and
/// hit-tests at its laid-out position plus a running Y shift; the shift grows by every earlier sibling's
/// <see cref="NodePaint.FlowDelta"/>, and a revealing exit orphan pushes the children laid out at/below its old top. Shared
/// verbatim by SceneRecorder.Walk, InputDispatcher.Hit/HitAny and SceneStore.PresentedAbsoluteRect so paint, input and
/// geometry queries agree. A virtual list's reveal bands ride the same walk: the rows of a band clip to the band's presented
/// height, and the rows after a band shift by its presented-minus-laid-out delta. POD, allocation-free, O(1) per child.</summary>
internal struct FlowCursor
{
    private float _shift;
    private float _orphanTop, _orphanDelta;
    private bool _orphanPending;
    private bool _active;
    // Virtual reveal bands, sorted by First; _next = the first band this walk has not yet passed.
    private RevealBands _bands;
    private int _bandCount, _next, _prefix, _firstRealized;

    /// <summary>False at rest: every <see cref="Step"/> would return 0, so callers skip the walk entirely.</summary>
    public readonly bool Active => _active;

    /// <summary>The cursor for a parent whose paint is <paramref name="p"/>.</summary>
    public static FlowCursor For(in NodePaint p)
    {
        var c = new FlowCursor();
        if ((p.FlowBits & NodePaint.FlowShiftsBit) == 0) return c;
        c._active = true;
        c._orphanPending = (p.FlowBits & NodePaint.FlowOrphanBit) != 0;
        c._orphanTop = p.FlowOrphanTop;
        c._orphanDelta = p.FlowOrphanDelta;
        return c;
    }

    /// <summary>Attach a virtual viewport's live reveal bands (its CONTENT node's walk): the rows of a band clip to the band's
    /// presented height, and every row after a band shifts by its presented-minus-laid-out delta.</summary>
    public void SetBands(in RevealBands bands, byte mask, int prefix, int firstRealized)
    {
        _bandCount = 0;
        for (int i = 0; i < RevealBands.Capacity; i++)
        {
            RevealBand b = bands.Get(i);
            if ((mask & (1 << i)) == 0 || b.Count <= 0 || !(b.Extent > 0f)) continue;
            int j = _bandCount++;
            while (j > 0 && _bands.Get(j - 1).First > b.First) { _bands.Set(j, _bands.Get(j - 1)); j--; }
            _bands.Set(j, in b);
        }
        _prefix = prefix;
        _firstRealized = firstRealized;
        _next = 0;
        if (_bandCount > 0) _active = true;
    }

    private static float BandPresented(in RevealBand b) => float.IsNaN(b.Presented) ? b.Extent : Math.Clamp(b.Presented, 0f, b.Extent);

    /// <summary>Advance over the child at <paramref name="ordinal"/> laid out at <paramref name="childTop"/> (parent-local)
    /// whose own flow delta is <paramref name="childFlowDelta"/>; returns the Y shift to draw / hit-test it at.
    /// <paramref name="clipTop"/>/<paramref name="clipBottom"/> (parent-local) bound it when a reveal band clips it, NaN
    /// otherwise. Call it for EVERY child in order, including ones the caller then skips.</summary>
    public float Step(int ordinal, float childTop, float childFlowDelta, out float clipTop, out float clipBottom)
    {
        clipTop = clipBottom = float.NaN;
        if (_bandCount > 0)
        {
            int logical = ordinal < _prefix ? ordinal : _firstRealized + (ordinal - _prefix);
            while (_next < _bandCount)
            {
                RevealBand passed = _bands.Get(_next);
                if (passed.First + passed.Count > logical) break;
                _shift += BandPresented(in passed) - passed.Extent;
                _next++;
            }
            if (_next < _bandCount)
            {
                RevealBand band = _bands.Get(_next);
                if (logical >= band.First)
                {
                    clipTop = band.Top + _shift;
                    clipBottom = clipTop + BandPresented(in band);
                }
            }
        }
        if (_orphanPending && childTop >= _orphanTop - 0.5f)
        {
            _shift += _orphanDelta;
            _orphanPending = false;
        }
        float s = _shift;
        _shift += childFlowDelta;
        return s;
    }
}
