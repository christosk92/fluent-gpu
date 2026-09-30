namespace FluentGpu.Render.Tiles;

/// <summary>Where the composite plan places a video hole's <c>EraseVideoHole</c> item relative to the SEGMENT whose
/// stream punched it (gpu-renderer.md §7.3 "Emit order", §13.1e).</summary>
public enum VideoEraseOrder : byte
{
    /// <summary>Before the segment. The erase clears what EARLIER items composited under the hole; then the segment
    /// composites over it — its own tile already holds the hole (the in-stream <c>DrawVideo</c> punched the tile) with every
    /// later op of the same segment (letterbox bars, transport, captions, a mini-player strip or ✕) painted back over it.
    /// Painter order is exact: under-video content erased, same-segment chrome kept, later items over it as before.</summary>
    BeforeSegment,

    /// <summary>After the segment. The in-stream punch hit an inline GROUP layer's scratch surface, not the tile, so the
    /// segment's own content painted before that layer still sits in the tile under the hole — only an erase after the
    /// segment clears it (and the chrome over the hole with it: the target-local limitation, gpu-renderer.md §7.3).</summary>
    AfterSegment,
}

/// <summary>
/// The inline GROUP-layer nesting at one point of a slice arena — the tracker the per-slot scan runs (pure, no
/// allocation). A non-acrylic <c>PushLayer</c> (opacity group, self-blur, edge fade) replayed inside a tile renders into a
/// scratch surface that composites back at its <c>PopLayer</c> (<c>TileRasterizer.OpenInlineLayer</c>), so a
/// <c>DrawVideo</c> inside it erases that scratch; an ACRYLIC <c>PushLayer</c> draws on the tile itself. Past 64 nested
/// layers every layer counts as a group (conservative — the hole's erase then stays after its segment), balanced on pop.
/// </summary>
public struct InlineLayerNesting
{
    private const int TrackedDepth = 64;
    private ulong _groupBits;   // bit d = the layer open at depth d is a group
    private int _depth, _groups;

    /// <summary>A group layer is open here: a hole punched now lands in its scratch, not on the tile.</summary>
    public readonly bool InGroup => _groups > 0;

    /// <summary>Layers open here (groups and acrylic).</summary>
    public readonly int Depth => _depth;

    /// <summary>Group layers open here.</summary>
    public readonly int Groups => _groups;

    /// <summary>A <c>PushLayer</c> of <paramref name="layerKind"/> (<see cref="LayerKind"/>) opened.</summary>
    public void Push(int layerKind)
    {
        bool group = layerKind != (int)LayerKind.Acrylic || _depth >= TrackedDepth;
        if (_depth < TrackedDepth)
            _groupBits = group ? _groupBits | (1UL << _depth) : _groupBits & ~(1UL << _depth);
        if (group) _groups++;
        _depth++;
    }

    /// <summary>A <c>PopLayer</c> closed the innermost open layer (an unbalanced pop — a corrupt stream — is inert).</summary>
    public void Pop()
    {
        if (_depth == 0) return;
        _depth--;
        if (_depth >= TrackedDepth || ((_groupBits >> _depth) & 1UL) != 0) _groups--;
    }
}

/// <summary>The one ordering decision for a video hole's composite erase (gpu-renderer.md §7.3 "Emit order").</summary>
public static class VideoHoleErase
{
    /// <summary>A hole punched on the tile erases BEFORE its segment (the chrome recorded after it survives); one punched
    /// inside an inline group layer erases AFTER it (the tile still holds the segment's earlier content there).</summary>
    public static VideoEraseOrder Order(bool punchedInGroupLayer)
        => punchedInGroupLayer ? VideoEraseOrder.AfterSegment : VideoEraseOrder.BeforeSegment;
}
