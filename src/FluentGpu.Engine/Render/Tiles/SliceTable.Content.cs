using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render.Tiles;

/// <summary>
/// What a tile's pixels are a function of, as the recorder knows it for ONE segment (gpu-renderer.md §13.1c): a
/// <see cref="Key"/> that changes whenever the segment's bytes or its device grid may have (the arena buffer generation,
/// the segment index, the tile origin, the raster scale — 0 = no content known), and the content hash of exactly what a
/// tile's replay draws (<see cref="Evidence.TileContentHash.TileWant"/>). <see cref="SliceTable.Request{TContent}"/>
/// asks for a tile's want only when the tile is resident + valid and its stored want was computed under a different key,
/// so a composite-only turn (every key unchanged) computes nothing.
/// </summary>
public interface ITileContent
{
    /// <summary>The key the wants of this segment are computed under (0 = none: validity is damage-only).</summary>
    ulong Key { get; }

    /// <summary>The content hash of the tile covering <paramref name="tilePx"/> (slice-space device px; never 0), the
    /// number of ops it folds (<paramref name="ops"/>: what the tile draws) and the part of the tile they paint
    /// (<paramref name="paint"/>, tile px; empty = nothing).</summary>
    ulong Want(in RectF tilePx, out int ops, out RectF paint);
}

/// <summary>No content known (a bare table in a unit test): validity comes from invalidation calls alone.</summary>
public readonly struct NoTileContent : ITileContent
{
    public ulong Key => 0UL;
    public ulong Want(in RectF tilePx, out int ops, out RectF paint) { ops = 0; paint = default; return 0UL; }
}

// CONTENT-DERIVED TILE VALIDITY (gpu-renderer.md §13.1c, issue #1). A tile's pixels are a pure function of the ops its
// replay draws; the table keeps, per SURFACE (a tile's pixels live in its surface, and at most SurfaceCap tiles hold
// pixels), the content hash the stream WANTS for the tile it holds (computed under a key that changes only when the
// segment's bytes or grid do) and the hash its pixels were RASTERED for, with the op count of each. A resident, valid tile
// whose freshly computed want differs from its raster hash is invalid and re-rasters this turn: PrimCount when the number
// of ops it draws changed (a primitive entered or left it: a row realized, parked, moved across the tile), else Content
// (it repainted in place). This is the ONLY per-node source of tile invalidation: the recorder emits no damage rects for
// tiles (a byte change is a want change, exactly per tile). What remains outside it is what no byte describes: the image
// clock, the scale, the grid, the theme or a forced-full repaint, eviction, coverage. Fixed arrays, zero allocation after
// construction.
public sealed partial class SliceTable
{
    private ulong[] _surfRasterHash = [];
    private ulong[] _surfWant = [];
    private ulong[] _surfWantKey = [];
    private int[] _surfWantOps = [], _surfRasterOps = [];
    // The part of its tile each surface paints (tile px), for the want and for the pixels it holds; Full = not known.
    private RectF[] _surfWantPaint = [], _surfRasterPaint = [];
    private static readonly RectF FullPaint = new(0f, 0f, TileGrid.W, TileGrid.H);
    private int _contentChecked, _contentCaught;

    /// <summary>Constructor tail: the per-surface content arrays.</summary>
    private void InitContent()
    {
        _surfRasterHash = new ulong[SurfaceCap];
        _surfWant = new ulong[SurfaceCap];
        _surfWantKey = new ulong[SurfaceCap];
        _surfWantOps = new int[SurfaceCap];
        _surfRasterOps = new int[SurfaceCap];
        _surfWantPaint = new RectF[SurfaceCap];
        _surfRasterPaint = new RectF[SurfaceCap];
    }

    /// <summary>Resident valid tiles whose want was (re)computed this turn (their segment's key changed).</summary>
    public int ContentCheckedThisFrame => _contentChecked;

    /// <summary>Tiles the content check invalidated this turn (Content or PrimCount): rastered for different content than
    /// the stream now wants.</summary>
    public int ContentCaughtThisFrame => _contentCaught;

    /// <summary>The key the want of the tile in <paramref name="surface"/> was last computed under (0 = never).</summary>
    public ulong SurfaceWantKey(int surface) => (uint)surface < (uint)_surfWantKey.Length ? _surfWantKey[surface] : 0UL;

    /// <summary>Store the want of the tile in <paramref name="surface"/> computed under <paramref name="key"/> (a surface
    /// acquired this turn: it rasters in this very submission, for this want).</summary>
    public void SetSurfaceWant(int surface, ulong key, ulong want, int ops = 0, RectF paint = default)
    {
        if ((uint)surface >= (uint)_surfWant.Length) return;
        _surfWantKey[surface] = key;
        _surfWant[surface] = want;
        _surfWantOps[surface] = ops;
        _surfWantPaint[surface] = key == 0 ? FullPaint : paint;
    }

    /// <summary><paramref name="p"/> with its PAINTED part (<see cref="TilePlacement.PaintX0"/>..): the union of what the
    /// stream now wants the tile to paint and what the pixels its surface holds paint — whichever of the two the composite
    /// samples (a tile rastered in this submission holds the want), nothing it paints is left out. Rounded out to whole
    /// px, cut by the surface.</summary>
    public TilePlacement PlacementPaint(in TilePlacement p)
    {
        int s = p.Surface;
        if ((uint)s >= (uint)_surfWantPaint.Length) return p;
        RectF a = _surfWantPaint[s], b = _surfRasterPaint[s];
        float x0, y0, x1, y1;
        if (a.IsEmpty && b.IsEmpty) { x0 = y0 = x1 = y1 = 0f; }
        else if (a.IsEmpty) { x0 = b.X; y0 = b.Y; x1 = b.Right; y1 = b.Bottom; }
        else if (b.IsEmpty) { x0 = a.X; y0 = a.Y; x1 = a.Right; y1 = a.Bottom; }
        else { x0 = MathF.Min(a.X, b.X); y0 = MathF.Min(a.Y, b.Y); x1 = MathF.Max(a.Right, b.Right); y1 = MathF.Max(a.Bottom, b.Bottom); }
        short px0 = (short)Math.Clamp((int)MathF.Floor(x0), 0, p.W), py0 = (short)Math.Clamp((int)MathF.Floor(y0), 0, p.H);
        short px1 = (short)Math.Clamp((int)MathF.Ceiling(x1), px0, p.W), py1 = (short)Math.Clamp((int)MathF.Ceiling(y1), py0, p.H);
        return p with { PaintX0 = px0, PaintY0 = py0, PaintX1 = px1, PaintY1 = py1 };
    }

    /// <summary>A surface was acquired: it holds no pixels and no want yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ContentAcquired(int surface)
    {
        if ((uint)surface >= (uint)_surfWant.Length) return;
        _surfRasterHash[surface] = 0;
        _surfWant[surface] = 0;
        _surfWantKey[surface] = 0;
        _surfWantOps[surface] = _surfRasterOps[surface] = 0;
        _surfWantPaint[surface] = FullPaint;   // not known until its want is computed
        _surfRasterPaint[surface] = default;   // it holds no pixels
        DamageForget(surface);
    }

    /// <summary>The backend completed a raster into <paramref name="surface"/>: its pixels now hold the current want.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ContentRastered(int surface)
    {
        if ((uint)surface >= (uint)_surfWant.Length) return;
        _surfRasterHash[surface] = _surfWant[surface];
        _surfRasterOps[surface] = _surfWantOps[surface];
        _surfRasterPaint[surface] = _surfWantPaint[surface];
    }

    /// <summary><see cref="Request{TContent}"/>'s content check of slab tile <paramref name="t"/> (resident, valid,
    /// requested this turn): when its want was computed under another key, fold it now and invalidate the tile if its
    /// pixels were rastered for anything else: <see cref="InvalidationReason.PrimCount"/> when the count of ops it draws
    /// changed, else <see cref="InvalidationReason.Content"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckContent<TContent>(int t, in TileKey key, ref TContent content) where TContent : struct, ITileContent
    {
        ulong k = content.Key;
        int s = _tiles[t].Surface;
        if (k == 0 || (uint)s >= (uint)_surfWantKey.Length || _surfWantKey[s] == k) return;
        ulong want = content.Want(TileGrid.TileRect(key.Tx, key.Ty), out int ops, out RectF paint);
        _surfWantKey[s] = k;
        _surfWant[s] = want;
        _surfWantOps[s] = ops;
        _surfWantPaint[s] = paint;
        _contentChecked++;
        if (_surfRasterHash[s] == want) return;
        var reason = ops != _surfRasterOps[s] ? InvalidationReason.PrimCount : InvalidationReason.Content;
        if (Invalidate(t, reason)) _contentCaught++;
    }
}
