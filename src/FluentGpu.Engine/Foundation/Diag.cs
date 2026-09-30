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
/// <c>"FluentGpu.Diagnostics"</c>, the <c>--fg diag</c> command-line switch (<c>EngineSwitches</c>), or by setting
/// <see cref="Enabled"/>. Route output by setting <see cref="Sink"/>.
/// </summary>
public static class Diag
{
#if DEBUG || FLUENTGPU_DIAG
    public const bool CompiledIn = true;
#else
    public const bool CompiledIn = false;
#endif

    /// <summary>The flavor THIS engine assembly was compiled as — "diag" when the DEBUG / FLUENTGPU_DIAG probes are in
    /// (the per-frame incremental-capture parity audit, RenderBudget), "release" otherwise. A
    /// property, not a const, so an app logs the engine it actually loaded: a Release app output once carried a Debug
    /// engine and spent two thirds of every frame in the parity audit (2026-09-17).</summary>
    public static string BuildFlavor => CompiledIn ? "diag" : "release";

    /// <summary>Runtime gate (only consulted when compiled in). Defaults off; the AppContext switch, <c>--fg diag</c> or
    /// code turns it on.</summary>
    public static bool Enabled;

    /// <summary>Where <see cref="Event"/>/<see cref="Dump"/> output goes (e.g. Console.WriteLine, the devtools panel, a log).</summary>
    public static Action<string>? Sink;

    /// <summary>ALWAYS-ON operational line writer — the small set of load-bearing evidence lines
    /// (<c>[device-lost]</c>, <c>[d3d12.adapter]</c>, <c>[d3d12.present]</c>, <c>[video.d3d11]</c>). Deliberately
    /// NOT <c>[Conditional]</c>: these lines are the Release-build proof trail (budgets.md "always-on plain counter"
    /// posture). Routes to <see cref="Sink"/> when a harness installed one, else stderr — never stdout, so app
    /// output stays clean. Callers own the cadence contract: never per-present / per-frame.</summary>
    public static void Line(string line)
    {
        if (Sink is { } sink) sink(line);
        else Console.Error.WriteLine(line);
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Counters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

    static Diag()
    {
        if (CompiledIn && AppContext.TryGetSwitch("FluentGpu.Diagnostics", out bool on)) Enabled = on;
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
        // The ONE gate in the engine that cannot take the two-operand `CompiledIn && Enabled` shape: this method returns
        // a STRING and the compiled-out arm is an early `return`, so adding a non-constant operand would change what it
        // returns. In a plain RELEASE build (neither DEBUG nor FLUENTGPU_DIAG) `CompiledIn` folds to false, the early
        // return is taken unconditionally, and the whole remaining body is genuinely unreachable — that CS0162 is the
        // intended erasure, hence the scoped suppression over the body (not just the guard line).
#pragma warning disable CS0162 // Unreachable code detected — release-only: const CompiledIn == false takes the early return
        if (!CompiledIn) return "(diagnostics compiled out — define FLUENTGPU_DIAG to enable)";
        var sb = new StringBuilder();
        lock (Gate)
        {
            foreach (var kv in Values) sb.Append(kv.Key).Append(" = ").AppendLine(kv.Value);
            foreach (var kv in Counters) sb.Append(kv.Key).Append(" : ").Append(kv.Value).AppendLine();
        }
        return sb.ToString();
#pragma warning restore CS0162
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
