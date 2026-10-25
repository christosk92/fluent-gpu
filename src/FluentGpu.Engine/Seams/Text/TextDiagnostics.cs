namespace FluentGpu.Text;

/// <summary>Text-backend diagnostics switches (set by <c>EngineSwitches</c>; read by the DirectWrite backend).</summary>
public static class TextDiagnostics
{
    /// <summary><c>--fg shape-census</c>: every text layout engine counts its layouts, shape-cache hits and misses (a miss
    /// split into first-ever, already shaped by ANOTHER engine, and re-shaped after its own eviction), the time its misses
    /// spend resolving, itemizing and shaping, the re-wrap time, and the bytes its shape cache retains; each engine prints
    /// one line every 10 s from its own thread. Off: one static bool read per layout.</summary>
    public static bool ShapeCensus;
}
