using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentGpu.Render;

namespace FluentGpu.Rhi.D3D12;

// The RASTER COST counters (audit PERF r3 RASTER; IGpuDevice.LastRasterCost): the work drivers of one composite submit's tile
// raster phase, so the per-turn cost model is fitted over ops, passes and pixels, not over a bare tile count. Plain fields on
// the render thread: a running op tally the replay bumps (every replay — tiles, direct regions, inline groups — so the raster
// phase reads it as the difference across RasterTiles), and two QPC spans. Zero allocation.
public sealed unsafe partial class D3D12Device
{
    // Ops the replays decoded since the device was created, by DrawOp (index = opcode; 0 and anything past the table pooled
    // at 0), and the clean subtrees the span index culled whole.
    private readonly long[] _rcOpTally = new long[32];
    private long _rcSpansSkippedTally;
    private long _rcCulledTally;   // primitives the decode-time cull dropped (every replay)
    private long _rcCulled0;

    // This submit's raster phase (reset by RcBeginSubmit).
    private long _rcGlyphTicks, _rcRasterTicks;
    private int _rcRasters, _rcPartial, _rcPasses, _rcGlyphInsts, _rcDraws;
    private long _rcPx;
    private readonly long[] _rcOps0 = new long[32], _rcOpsEnd = new long[32];
    private long _rcSpans0;
    private RasterCost _lastRasterCost;

    /// <inheritdoc/>
    public RasterCost LastRasterCost => _lastRasterCost;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RcOp(DrawOp op)
    {
        uint i = (uint)op;
        _rcOpTally[i < 32u ? i : 0u]++;
    }

    private long _rcSubmitOps0;

    private long _rcTexC0, _rcTexR0;
    private int _rcSeg0, _rcSci0, _rcSegN, _rcSciN;

    private void RcBeginSubmit()
    {
        _rcTexC0 = _surfaces?.TexturesCreated ?? 0;
        _rcTexR0 = _surfaces?.TexturesReleased ?? 0;
        _rcSegN = _rcSciN = 0;
        long all = 0;
        for (int i = 0; i < _rcOpTally.Length; i++) all += _rcOpTally[i];
        _rcSubmitOps0 = all;
        _rcGlyphTicks = _rcRasterTicks = 0;
        _rcRasters = _rcPartial = _rcPasses = _rcGlyphInsts = _rcDraws = 0;
        _rcPx = 0;
    }

    /// <summary>RasterTiles' head: snapshot the running tallies.</summary>
    private long RcRasterBegin()
    {
        Array.Copy(_rcOpTally, _rcOps0, _rcOpTally.Length);
        _rcSpans0 = _rcSpansSkippedTally;
        _rcCulled0 = _rcCulledTally;
        _rcFoot0 = _rcFootSkippedTally;
        _rcPasses = _frameRenderPasses;
        _rcGlyphInsts = _frameGlyphInstanceCount;
        _rcDraws = GpuDrawCount.Frame;
        _rcSeg0 = _frameSegments; _rcSci0 = _frameScissorSets;
        return Stopwatch.GetTimestamp();
    }

    /// <summary>One tile raster encoded (its written pixels).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RcRaster(bool partial, long px)
    {
        _rcRasters++;
        if (partial) _rcPartial++;
        _rcPx += px;
    }

    /// <summary>RasterTiles' tail: the phase's span and the tallies' differences.</summary>
    private void RcRasterEnd(long t0)
    {
        _rcRasterTicks += Stopwatch.GetTimestamp() - t0;
        _rcPasses = _frameRenderPasses - _rcPasses;
        _rcGlyphInsts = _frameGlyphInstanceCount - _rcGlyphInsts;
        _rcDraws = GpuDrawCount.Frame - _rcDraws;
        _rcCulledEnd = _rcCulledTally;
        _rcSegN = _frameSegments - _rcSeg0; _rcSciN = _frameScissorSets - _rcSci0;
        Array.Copy(_rcOpTally, _rcOpsEnd, _rcOpTally.Length);   // the raster phase's ops only (not the composite's replays)
        _rcFootEnd = _rcFootSkippedTally;
    }
    private long _rcCulledEnd, _rcFoot0, _rcFootEnd;

    /// <summary>SubmitComposite's tail: publish <see cref="LastRasterCost"/>.</summary>
    private void RcEndSubmit()
    {
        Span<long> d = stackalloc long[32];
        bool rastered = _rcRasters > 0;
        for (int i = 0; i < d.Length; i++) d[i] = rastered ? _rcOpsEnd[i] - _rcOps0[i] : 0;
        long ops = 0;
        for (int i = 0; i < d.Length; i++) ops += d[i];
        long rects = d[(int)DrawOp.FillRoundRect] + d[(int)DrawOp.DrawRoundRectStroke] + d[(int)DrawOp.DrawShadow]
            + d[(int)DrawOp.DrawGradientRect] + d[(int)DrawOp.DrawGradientStroke] + d[(int)DrawOp.DrawArc]
            + d[(int)DrawOp.DrawPolylineStroke] + d[(int)DrawOp.DrawTabShape] + d[(int)DrawOp.DrawVideo] + d[(int)DrawOp.EraseRoundRect];
        long glyphs = d[(int)DrawOp.DrawGlyphRun] + d[(int)DrawOp.DrawGlyphRunGradient] + d[(int)DrawOp.DrawIconMask];
        long paths = d[(int)DrawOp.FillPath] + d[(int)DrawOp.StrokePath];
        long series = d[(int)DrawOp.DrawSeries] + d[(int)DrawOp.DrawSprites];
        long scopes = d[(int)DrawOp.PushClip] + d[(int)DrawOp.PopClip] + d[(int)DrawOp.PushStencilClip] + d[(int)DrawOp.PopStencilClip]
            + d[(int)DrawOp.PushLayer] + d[(int)DrawOp.PopLayer];
        long all = 0;
        for (int i = 0; i < _rcOpTally.Length; i++) all += _rcOpTally[i];
        double toMs = 1000.0 / Stopwatch.Frequency;
        _lastRasterCost = new RasterCost(
            (float)(_rcGlyphTicks * toMs), (float)(_rcRasterTicks * toMs), _rcRasters, _rcPartial, rastered ? _rcPasses : 0, _rcPx,
            (int)ops, (int)rects, (int)glyphs, (int)d[(int)DrawOp.DrawImage], (int)paths, (int)series, (int)scopes,
            rastered ? (int)(_rcSpansSkippedTally - _rcSpans0) : 0,
            rastered ? _rcGlyphInsts : 0, rastered ? _rcDraws : 0, (int)(all - _rcSubmitOps0),
            rastered ? (int)(_rcCulledEnd - _rcCulled0) : 0, rastered ? (int)(_rcFootEnd - _rcFoot0) : 0,
            (int)((_surfaces?.TexturesCreated ?? 0) - _rcTexC0), (int)((_surfaces?.TexturesReleased ?? 0) - _rcTexR0),
            rastered ? _rcSegN : 0, rastered ? _rcSciN : 0);
    }
}
