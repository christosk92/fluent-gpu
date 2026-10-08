namespace FluentGpu.Scene;

/// <summary>The presented-flow walk ONE parent runs over its children (SizeMode.FlowReveal): each child draws and
/// hit-tests at its laid-out position plus a running Y shift; the shift grows by every earlier sibling's
/// <see cref="NodePaint.FlowDelta"/>, and a revealing exit orphan pushes the children laid out at/below its old top. Shared
/// verbatim by SceneRecorder.Walk, InputDispatcher.Hit/HitAny and SceneStore.PresentedAbsoluteRect so paint, input and
/// geometry queries agree. POD, allocation-free, O(1) per child.</summary>
internal struct FlowCursor
{
    private float _shift;
    private float _orphanTop, _orphanDelta;
    private bool _orphanPending;
    private bool _active;

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

    /// <summary>Advance over the child at <paramref name="ordinal"/> laid out at <paramref name="childTop"/> (parent-local)
    /// whose own flow delta is <paramref name="childFlowDelta"/>; returns the Y shift to draw / hit-test it at.
    /// <paramref name="clipTop"/>/<paramref name="clipBottom"/> (parent-local) bound it when a reveal band clips it, NaN
    /// otherwise (bands land in Phase 2). Call it for EVERY child in order, including ones the caller then skips.</summary>
    public float Step(int ordinal, float childTop, float childFlowDelta, out float clipTop, out float clipBottom)
    {
        clipTop = clipBottom = float.NaN;
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
