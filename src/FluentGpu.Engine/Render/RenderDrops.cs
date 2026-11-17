namespace FluentGpu.Render;

/// <summary>What a GPU backend was asked to draw and could not, process-wide and cumulative: a visible defect (text, an image,
/// a shape missing from the frame it belonged to), never a cost. Read it before and after an interaction; the inspector's
/// <c>perf report</c> carries the delta and <c>--gate-perf</c> fails on any. Written by the backend that drops (any thread),
/// read by anyone; always on, allocation-free.</summary>
public static class RenderDrops
{
    private static long s_glyphs;

    /// <summary>Glyph quads a frame recorded but did not draw (the frame's glyph instances did not fit any buffer it could have).</summary>
    public static long Glyphs => Volatile.Read(ref s_glyphs);

    /// <summary>A backend dropped <paramref name="count"/> glyph quads from the frame it is recording.</summary>
    public static void AddGlyphs(int count)
    {
        if (count > 0) Interlocked.Add(ref s_glyphs, count);
    }
}
