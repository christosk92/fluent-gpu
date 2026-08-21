using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// One tessellated path vertex: position, an AA-fringe coverage value (0 outer edge → 1 fully inside; gpu-renderer.md
/// §5 step 4, "AA-fringe (feather) with a 0→1 coverage vertex attribute, MSAA off"), and a normalized arc-length
/// position along the stroke's contour (0 for a fill vertex — <see cref="PathStroker"/> is the only writer of a
/// non-zero <see cref="S"/>; see its doc for why trim/dash read this as a per-frame shader uniform instead of being a
/// tessellation input). 16 bytes, blittable, no padding.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PathVertex
{
    public float X, Y;
    public float Cov;
    public float S;
}

/// <summary>
/// A tessellated result's location inside the caller-supplied destination spans (gpu-renderer.md §5.1's retained
/// slab, via <see cref="PathRealizationCache"/>) plus its untransformed bounds. <see cref="ArcLenPx"/> is the total
/// contour length in DEVICE pixels for a stroke (0 for a fill) — reported so a caller can drive a draw-on/dash shader
/// uniform without re-walking the geometry (see <see cref="PathStroker"/>).
/// </summary>
public readonly struct PathRef
{
    public readonly int VtxStart, VtxCount, IdxStart, IdxCount;
    public readonly RectF Bounds;
    public readonly float ArcLenPx;

    public PathRef(int vtxStart, int vtxCount, int idxStart, int idxCount, in RectF bounds, float arcLenPx)
    {
        VtxStart = vtxStart; VtxCount = vtxCount; IdxStart = idxStart; IdxCount = idxCount;
        Bounds = bounds; ArcLenPx = arcLenPx;
    }
}

/// <summary>Where a stroke's normalized arc-length attribute (<see cref="PathVertex.S"/>) resets to 0 — the "Individually
/// vs. Simultaneously" trim-space choice (Lottie terminology). <see cref="PerContour"/> (the default, matching
/// Lottie's "Individually") restarts <c>S</c> at 0 for every subpath; <see cref="WholePath"/> runs <c>S</c>
/// continuously across every subpath in the whole <see cref="PathData"/>, scaled by each subpath's share of the
/// total contour length. <b>Additive beyond the fixed canon signature</b> — exposed as an optional parameter on
/// <see cref="PathTessellator.TryTessellateStroke"/> defaulting to <see cref="PerContour"/>, so every existing call
/// shape keeps working.</summary>
public enum PathTrimSpace { PerContour = 0, WholePath = 1 }

/// <summary>
/// CPU path tessellator (gpu-renderer.md §5): flatten → triangulate/offset → AA-fringe, writing into caller-supplied
/// destination spans with ZERO heap allocation on this struct's own part (the actual algorithms in
/// <see cref="PathFlatten"/>/<see cref="PathSweep"/>/<see cref="PathStroker"/> use small reusable cold-path scratch —
/// this type never runs inside a frame hot phase; see <see cref="PathRealizationCache"/> for why).
///
/// <para><b>Deviation from canon:</b> gpu-renderer.md §5 prints
/// <c>PathTessellator(ArenaAllocator vtxArena, ArenaAllocator idxArena, float deviceScale)</c>. <see cref="ArenaAllocator"/>
/// (<c>Foundation/Allocators.cs</c>) is the PER-FRAME bump allocator whose spans die on <see cref="ArenaAllocator.Reset"/> —
/// exactly what gpu-renderer.md §5.1's RETAINED realization slab forbids (a tessellation must outlive many frames).
/// Taking destination <see cref="Span{T}"/>s directly is strictly more general — it serves both a transient per-frame
/// caller AND <see cref="PathRealizationCache"/>'s retained slab — and is the shape actually used below. A caller
/// measures via a failed <see cref="TryTessellateFill"/>/<see cref="TryTessellateStroke"/> (which reports
/// <see cref="NeededVtx"/>/<see cref="NeededIdx"/>) and retries with bigger spans; this type NEVER throws and NEVER
/// grows storage behind the caller's back.</para>
/// </summary>
public ref struct PathTessellator
{
    private readonly Span<PathVertex> _vtx;
    private readonly Span<uint> _idx;
    private readonly float _deviceScale;

    /// <summary>Set (by the most recent <c>Try*</c> call) to the vertex count actually needed — read this after a
    /// `false` return and retry with a big-enough span.</summary>
    public int NeededVtx { get; private set; }
    /// <summary>Set (by the most recent <c>Try*</c> call) to the index count actually needed.</summary>
    public int NeededIdx { get; private set; }

    public PathTessellator(Span<PathVertex> vtx, Span<uint> idx, float deviceScale)
    {
        _vtx = vtx;
        _idx = idx;
        _deviceScale = deviceScale > 1e-6f ? deviceScale : 1f;
        NeededVtx = 0;
        NeededIdx = 0;
    }

    /// <summary>Tessellate <paramref name="path"/>'s fill under <paramref name="rule"/> into the destination spans this
    /// tessellator was constructed with. False (with <see cref="NeededVtx"/>/<see cref="NeededIdx"/> set) if they are
    /// too small — never throws, never partially writes a result the caller could mistake for complete.</summary>
    public bool TryTessellateFill(PathData path, FillRule rule, out PathRef r)
    {
        if (path is null || path.VerbCount == 0)
        {
            NeededVtx = 0; NeededIdx = 0;
            r = new PathRef(0, 0, 0, 0, default, 0f);
            return true;
        }

        float tol = 0.25f / _deviceScale;
        PathFlatten.Flatten(path, tol, _deviceScale, out var pts, out var starts, out var counts, out _);
        PathSweep.Tessellate(pts, starts, counts, rule, _deviceScale, out var svtx, out var sidx, out var sbounds);
        return Publish(svtx, sidx, sbounds, 0f, out r);
    }

    /// <summary>Tessellate <paramref name="path"/>'s stroke per <paramref name="s"/> into the destination spans.
    /// <paramref name="trimSpace"/> controls where the baked arc-length attribute (<see cref="PathVertex.S"/>) resets
    /// (see <see cref="PathTrimSpace"/> — additive beyond canon's fixed signature, defaults preserve the plain call
    /// shape). False (with <see cref="NeededVtx"/>/<see cref="NeededIdx"/> set) if the destination is too small.</summary>
    public bool TryTessellateStroke(PathData path, in StrokeStyle s, out PathRef r, PathTrimSpace trimSpace = PathTrimSpace.PerContour)
    {
        if (path is null || path.VerbCount == 0 || s.IsNone)
        {
            NeededVtx = 0; NeededIdx = 0;
            r = new PathRef(0, 0, 0, 0, default, 0f);
            return true;
        }

        float tol = 0.25f / _deviceScale;
        PathFlatten.Flatten(path, tol, _deviceScale, out var pts, out var starts, out var counts, out var closed, out _);
        PathStroker.Tessellate(pts, starts, counts, closed, s, _deviceScale, trimSpace, out var svtx, out var sidx, out var sbounds, out float arcLenPx);
        return Publish(svtx, sidx, sbounds, arcLenPx, out r);
    }

    // `scoped` is load-bearing: this is an instance method on a ref struct that HOLDS spans, so without it the
    // compiler must assume the arguments could be stored into _vtx/_idx and refuses the call (CS8350/CS8352). They
    // cannot — the only thing done with them here is CopyTo — and `scoped` is how that promise is stated.
    private bool Publish(scoped ReadOnlySpan<PathVertex> svtx, scoped ReadOnlySpan<uint> sidx, in RectF bounds,
                         float arcLenPx, out PathRef r)
    {
        NeededVtx = svtx.Length;
        NeededIdx = sidx.Length;
        if (svtx.Length > _vtx.Length || sidx.Length > _idx.Length)
        {
            r = new PathRef(0, 0, 0, 0, default, 0f);
            return false;
        }
        svtx.CopyTo(_vtx);
        sidx.CopyTo(_idx);
        r = new PathRef(0, svtx.Length, 0, sidx.Length, bounds, arcLenPx);
        return true;
    }
}
