namespace FluentGpu.Rhi;

/// <summary>
/// Where the CPU of one composite submit's TILE RASTER went (audit PERF r3 RASTER): the work drivers of the raster phase,
/// so a per-turn cost model can be fitted over them instead of over "tiles rastered" alone (a tile of three bars and a
/// tile of a whole track list cost the same in that count). Render thread; plain counters, read right after the submit.
/// <list type="bullet">
/// <item><see cref="GlyphPrepMs"/>: the glyph preparation of the streams the turn decodes (shaping, atlas packing).</item>
/// <item><see cref="RasterMs"/>: the encode of every tile raster (barriers, render passes, the replay and its draws).</item>
/// <item><see cref="Rasters"/> / <see cref="Partial"/>: tile rasters encoded, of which partial (sub-tile damage).</item>
/// <item><see cref="Passes"/>: render passes the raster phase opened (inline groups and blur chains included).</item>
/// <item><see cref="Px"/>: pixels the rasters write (a partial raster's damage, else its surface).</item>
/// <item><see cref="Ops"/>: ops the replays decoded, by class: <see cref="Rects"/> (round rects, strokes, shadows,
/// gradients, arcs, polylines, tab shapes, erases), <see cref="Glyphs"/> (glyph runs, gradient runs, icon masks),
/// <see cref="Images"/>, <see cref="Paths"/> (fills, strokes), <see cref="Series"/> (series and sprite chunks),
/// <see cref="Scopes"/> (clip / stencil / layer pushes and pops); <see cref="SpansSkipped"/>: clean subtrees the span
/// index culled whole.</item>
/// <item><see cref="GlyphInstances"/>: glyph quads the rasters emitted; <see cref="Draws"/>: draw calls they issued.</item>
/// <item><see cref="SubmitOps"/>: ops EVERY replay of the submit decoded (the rasters, the offscreen surfaces, the inline
/// segments the composite pass replays into the back buffer); <see cref="Culled"/>: primitives of the rasters the
/// decode-time cull dropped (decoded, then found to miss the tile); <see cref="FootSkipped"/>: primitives the rasters
/// skipped unread by their footprint (they would have been culled).</item>
/// </list>
/// </summary>
public readonly record struct RasterCost(
    float GlyphPrepMs, float RasterMs, int Rasters, int Partial, int Passes, long Px,
    int Ops, int Rects, int Glyphs, int Images, int Paths, int Series, int Scopes, int SpansSkipped,
    int GlyphInstances, int Draws, int SubmitOps = 0, int Culled = 0, int FootSkipped = 0);

public partial interface IGpuDevice
{
    /// <summary>The raster cost of the most recent <see cref="SubmitComposite"/> (render thread). Default: nothing measured.</summary>
    RasterCost LastRasterCost => default;
}
