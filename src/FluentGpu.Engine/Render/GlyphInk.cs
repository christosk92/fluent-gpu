using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// GLYPH INK (audit PERF r3 RASTER, item 8: the edge-fade distribution test). A glyph run's recorded footprint is its node
/// box grown by a halo of 1.5 em on every side (RepaintCull.GlyphHalo: a monitored bound for the
/// decode-time cull, because the true extent is data the recorder does not have). The renderer has it: the run's shaped
/// quads, which are everything a replay of the run draws. The device publishes each replayed run's quad extent here, keyed
/// by the run's shaping inputs, RELATIVE to the run's layout origin (Bounds.X, Bounds.Y) — the shape bakes the origin into
/// every quad and nothing else of the origin reaches it — and the slice recorder reads it to test a fade's distribution per
/// painted pixel instead of per box (<c>SliceRecorder.OpBoxesIn</c>). A replay moves a run's quads by at most one device
/// pixel from the shaped ones (the baseline's sub-pixel phase snap, a sub-pixel origin), which <see cref="MarginDip"/>
/// covers; <c>--fg ink-validate</c> checks every replayed run's quads against the extent the table held BEFORE it.
/// Render thread only (one table per thread: a device and its recorder share their render thread); a miss means "use the
/// box". The key is a 64-bit hash of every shaping input — the text, family, size, weight, box width, wrap, trim, line
/// limits, spacing, line height and bounds, span run, typography and the device scale.
/// </summary>
public static class GlyphInk
{
    /// <summary>The slack around a published extent (local DIP): a replay's quads lie within it.</summary>
    public const float MarginDip = 2f;

    private const int Cap = 8192;
    [ThreadStatic] private static Dictionary<ulong, RectF>? t_ink;

    /// <summary>The run's key: its shaping inputs and the device scale it is shaped at.</summary>
    public static ulong Key(in DrawGlyphRunCmd c, float deviceScale)
    {
        ulong h = 0xcbf29ce484222325UL;
        h = Mix(h, (uint)c.Text.Value); h = Mix(h, (uint)c.Family.Value);
        h = Mix(h, Bits(c.FontSize)); h = Mix(h, (uint)c.Weight); h = Mix(h, Bits(c.Bounds.W));
        h = Mix(h, (uint)c.Wrap); h = Mix(h, (uint)c.Trim); h = Mix(h, (uint)c.MaxLines);
        h = Mix(h, Bits(c.CharSpacing)); h = Mix(h, Bits(c.LineHeight)); h = Mix(h, (uint)c.LineStacking);
        h = Mix(h, (uint)c.LineBounds); h = Mix(h, (uint)c.SpanRunId); h = Mix(h, (uint)c.Typography);
        return Mix(h, Bits(deviceScale));
    }

    /// <summary>The device replayed a run: its quads span <paramref name="inkRelative"/> (relative to the layout origin;
    /// empty = it draws nothing).</summary>
    public static void Store(ulong key, in RectF inkRelative)
    {
        var d = t_ink ??= new Dictionary<ulong, RectF>(1024);
        if (d.Count >= Cap && !d.ContainsKey(key)) d.Clear();   // bounded: a miss only costs the box
        d[key] = inkRelative;
    }

    /// <summary>The extent last published for <paramref name="key"/> on this thread.</summary>
    public static bool TryGet(ulong key, out RectF inkRelative)
    {
        inkRelative = default;
        return t_ink is { } d && d.TryGetValue(key, out inkRelative);
    }

    /// <summary>The painted box of a run whose quads span <paramref name="inkRelative"/>, in the run's local space
    /// (margin included; empty when it draws nothing).</summary>
    public static RectF LocalBox(in DrawGlyphRunCmd c, in RectF inkRelative)
        => inkRelative.IsEmpty ? default
            : new RectF(c.Bounds.X + inkRelative.X - MarginDip, c.Bounds.Y + inkRelative.Y - MarginDip,
                inkRelative.W + 2f * MarginDip, inkRelative.H + 2f * MarginDip);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h, uint v) => (h ^ v) * 0x100000001b3UL ^ ((h ^ v) >> 29);
}
