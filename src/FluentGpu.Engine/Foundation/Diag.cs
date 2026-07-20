using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace FluentGpu.Foundation;

/// <summary>
/// The standardized, reusable engine-wide diagnostics facility. Every subsystem instruments through it the same way:
/// <c>Diag.Count("text.glyph","rasterized")</c>, <c>Diag.Set("d3d12","glyphInstances", n)</c>,
/// <c>Diag.Event("rhi","device-lost")</c>, <c>using (Diag.Time("frame","record")) { … }</c>.
///
/// COST MODEL: the recording methods are <c>[Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]</c>, so on a Release
/// build (with neither symbol defined) the compiler removes the call site AND the argument evaluation entirely — a
/// `Diag.Set("text.atlas","nonZero", ExpensiveScan())` costs literally nothing in production. Define
/// <c>FLUENTGPU_DIAG</c> to keep diagnostics in a Release build; toggle at runtime with the AppContext switch
/// <c>"FluentGpu.Diagnostics"</c> or by setting <see cref="Enabled"/>. Route output by setting <see cref="Sink"/>.
/// </summary>
public static class Diag
{
#if DEBUG || FLUENTGPU_DIAG
    public const bool CompiledIn = true;
#else
    public const bool CompiledIn = false;
#endif

    /// <summary>Runtime gate (only consulted when compiled in). Defaults off unless FG_DIAG is set; AppContext switch overrides.</summary>
    public static bool Enabled = CompiledIn && EnvFlag("FG_DIAG");

    /// <summary>Where <see cref="Event"/>/<see cref="Dump"/> output goes (e.g. Console.WriteLine, the devtools panel, a log).</summary>
    public static Action<string>? Sink;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Counters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

    static Diag()
    {
        if (CompiledIn && AppContext.TryGetSwitch("FluentGpu.Diagnostics", out bool on)) Enabled = on;
    }

    public static bool EnvFlag(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return !value.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("off", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True ONLY when <paramref name="name"/> is EXPLICITLY set to a falsy value (0/false/off/no) — the
    /// kill-switch form for facilities that default ON when compiled in (BindContract, BackwardsWriteGuard). Unset ⇒
    /// false (stays enabled).</summary>
    public static bool EnvFlagDisabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void Count(string category, string key, long delta = 1)
    {
        if (!Enabled) return;
        string k = category + "." + key;
        lock (Gate) { Counters.TryGetValue(k, out long v); Counters[k] = v + delta; }
    }

    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void Set(string category, string key, object? value)
    {
        if (!Enabled) return;
        lock (Gate) Values[category + "." + key] = value?.ToString() ?? "null";
    }

    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void Event(string category, string message)
    {
        if (!Enabled) return;
        Sink?.Invoke($"[{category}] {message}");
    }

    /// <summary>Scoped timing: <c>using (Diag.Time("layout","run")) { … }</c>. Internals elide on Release (const-false branch).</summary>
    public static DiagScope Time(string category, string key) => new(category, key);

    /// <summary>Aggregate snapshot of all values + counters (for the devtools panel / a dump).</summary>
    public static string Snapshot()
    {
        if (!CompiledIn) return "(diagnostics compiled out — define FLUENTGPU_DIAG to enable)";
        var sb = new StringBuilder();
        lock (Gate)
        {
            foreach (var kv in Values) sb.Append(kv.Key).Append(" = ").AppendLine(kv.Value);
            foreach (var kv in Counters) sb.Append(kv.Key).Append(" : ").Append(kv.Value).AppendLine();
        }
        return sb.ToString();
    }

    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void Dump(string? header = null)
    {
        var sink = Sink ?? Console.WriteLine;
        if (header is not null) sink("── diag: " + header + " ──");
        sink(Snapshot());
    }

    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void Reset()
    {
        lock (Gate) { Counters.Clear(); Values.Clear(); }
    }
}

/// <summary>Timing scope from <see cref="Diag.Time"/>. Zero-work on Release (the <c>Diag.CompiledIn</c> const folds out).</summary>
public readonly struct DiagScope : IDisposable
{
    private readonly string _category;
    private readonly string _key;
    private readonly long _start;

    public DiagScope(string category, string key)
    {
        _category = category;
        _key = key;
        _start = Diag.CompiledIn ? Stopwatch.GetTimestamp() : 0;
    }

    public void Dispose()
    {
        if (!Diag.CompiledIn || !Diag.Enabled) return;
        double ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        Diag.Set(_category, _key + ".ms", ms.ToString("0.000"));
    }
}
