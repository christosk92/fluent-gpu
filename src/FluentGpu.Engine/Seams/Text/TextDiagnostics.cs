namespace FluentGpu.Text;

/// <summary>Text-backend diagnostics switches (set by <c>EngineSwitches</c>; read by the DirectWrite backend).</summary>
public static class TextDiagnostics
{
    /// <summary><c>--fg shape-census</c>: every text layout engine counts its layouts, shape-cache hits and misses (a miss
    /// split into first-ever, already shaped by ANOTHER engine, and re-shaped after its own eviction), the time its misses
    /// spend resolving, itemizing and shaping, the re-wrap time, and the bytes its shape cache retains; each engine prints
    /// one line every 10 s from its own thread. Off: one static bool read per layout.</summary>
    public static bool ShapeCensus;

    /// <summary><c>--fg shape-validate</c>: every layout whose shape came from the shape store is laid out again by an engine with
    /// no store (a real itemize and shape) and the two are compared field by field (box, metrics, every glyph, cluster and line);
    /// a summary per engine every 5 s, the first mismatches printed with their text.</summary>
    public static bool ShapeValidate;

    /// <summary><c>--fg no-shape-prefetch</c>: the layout pass does not hand first-visit text to the shaping workers (the A/B arm):
    /// every new string shapes inside its measure on the UI thread, as before C4 step 4.</summary>
    public static bool NoShapePrefetch;
}
