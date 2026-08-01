using System.Globalization;

namespace Bench.Contracts;

/// <summary>
/// One completed benchmark process, reduced to plain data. The runner projects its <see cref="RunRecord"/> plus the
/// deserialized <see cref="BenchResult"/> into this shape so aggregation stays free of <c>System.Diagnostics.Process</c>
/// and is directly unit-testable.
/// </summary>
public sealed record RunObservation
{
    public required string Scenario { get; init; }
    public required string Framework { get; init; }
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    /// <summary>Runner-side wall clock: process start to the result JSON being observed on disk.</summary>
    public required double ExternalStartToResultMs { get; init; }
    /// <summary>Per-iteration in-app CPU field; null for a failed run.</summary>
    public double[]? CpuWorkMs { get; init; }
    /// <summary>Per-iteration frame time; only meaningful on the cadence pass. Null for a failed run.</summary>
    public double[]? FrameMs { get; init; }
    public long AllocatedBytes { get; init; }
    public int Iterations { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
}

public sealed record AggregatedSummary
{
    public required SummaryRow[] Rows { get; init; }
    public required ScenarioOutcome[] Outcomes { get; init; }
}

/// <summary>
/// Turns raw per-process observations into the publishable summary rows. A scenario is never silently dropped: a row is
/// emitted whenever either framework produced at least one successful run, and the missing side is null with a caveat.
/// </summary>
public static class SummaryAggregation
{
    public const string WinUIFramework = "WinUI 3";
    public const string FluentGpuFramework = "FluentGpu";
    public const string CadencePass = "cadence";

    public const string CpuWorkCaveat =
        "Definitions differ: WinUI = UI-thread mutation + synchronous UpdateLayout only (no render/compose/present); " +
        "FluentGpu = full frame CPU (flush+layout+anim+record+submit, minus fence/present waits)";

    public const string AllocCaveat =
        "UI-thread allocations only; FluentGpu's render thread and WinUI's compositor threads are invisible to " +
        "GC.GetAllocatedBytesForCurrentThread";

    public const string WorkloadDeltaCaveat =
        "derived: scenario minus startup median; isolates workload cost from fixed bring-up";

    public const string ExternalMetric = "process start to result";
    public const string CpuWorkMetric = "CPU work";
    public const string FrameTimeMetric = "frame time";
    public const string AllocMetric = "allocated per operation average";
    public const string WorkloadDeltaMetric = "workload delta p50";

    public static AggregatedSummary Aggregate(IReadOnlyList<RunObservation> runs, string pass)
    {
        ArgumentNullException.ThrowIfNull(runs);
        bool cadence = string.Equals(pass, CadencePass, StringComparison.OrdinalIgnoreCase);
        string[] scenarios = runs.Select(static x => x.Scenario).Distinct(StringComparer.Ordinal).ToArray();
        string[] frameworks = runs.Select(static x => x.Framework).Distinct(StringComparer.Ordinal).ToArray();

        var outcomes = new List<ScenarioOutcome>();
        foreach (string scenario in scenarios)
        foreach (string framework in frameworks)
        {
            RunObservation[] attempts = Select(runs, scenario, framework);
            if (attempts.Length == 0) continue;
            outcomes.Add(new ScenarioOutcome
            {
                Scenario = scenario,
                Framework = framework,
                Attempted = attempts.Length,
                Succeeded = attempts.Count(static x => x.Success),
                FailureSignature = Signature(attempts),
            });
        }

        // The workload-delta rows subtract the fixed bring-up cost, so they exist only when startup was measured.
        double? winStartup = null;
        double? fgStartup = null;
        if (scenarios.Contains(BenchScenarios.Startup, StringComparer.Ordinal))
        {
            winStartup = MedianOrNull(Successful(runs, BenchScenarios.Startup, WinUIFramework)
                .Select(static x => x.ExternalStartToResultMs));
            fgStartup = MedianOrNull(Successful(runs, BenchScenarios.Startup, FluentGpuFramework)
                .Select(static x => x.ExternalStartToResultMs));
        }

        var rows = new List<SummaryRow>();
        foreach (string scenario in scenarios)
        {
            RunObservation[] winui = Successful(runs, scenario, WinUIFramework);
            RunObservation[] fluent = Successful(runs, scenario, FluentGpuFramework);
            if (winui.Length == 0 && fluent.Length == 0) continue;

            string? missing = MissingSideCaveat(outcomes, scenario, winui.Length, fluent.Length);

            if (BenchScenarios.IsColdLoad(scenario))
            {
                AddDistribution(rows, scenario, ExternalMetric,
                    winui.Select(static x => x.ExternalStartToResultMs),
                    fluent.Select(static x => x.ExternalStartToResultMs), "ms", missing);
                AddMemory(rows, scenario, winui, fluent, missing);
                AddWorkloadDelta(rows, scenario, winui, fluent, winStartup, fgStartup, missing);
                continue;
            }

            AddDistribution(rows, scenario, CpuWorkMetric,
                winui.SelectMany(static x => x.CpuWorkMs ?? []),
                fluent.SelectMany(static x => x.CpuWorkMs ?? []), "ms", Combine(missing, CpuWorkCaveat));

            // frameMs is a display-paced quantity: aggregating it on the raw CPU pass would publish suppressed-vsync
            // loop timings as if they were frame times.
            if (cadence)
                AddDistribution(rows, scenario, FrameTimeMetric,
                    winui.SelectMany(static x => x.FrameMs ?? []),
                    fluent.SelectMany(static x => x.FrameMs ?? []), "ms", missing);

            AddRow(rows, scenario, AllocMetric,
                MeanOrNull(winui.Where(static x => x.Iterations > 0).Select(static x => (double)x.AllocatedBytes / x.Iterations)),
                MeanOrNull(fluent.Where(static x => x.Iterations > 0).Select(static x => (double)x.AllocatedBytes / x.Iterations)),
                "bytes", SampleCount(winui.Length, fluent.Length), Combine(missing, AllocCaveat));

            AddMemory(rows, scenario, winui, fluent, missing);
        }

        return new AggregatedSummary { Rows = rows.ToArray(), Outcomes = outcomes.ToArray() };
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return double.NaN;
        double position = (sorted.Length - 1) * p;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    /// <summary>Human-readable failure signature for a set of attempts, for example <c>0xC000027B x5</c>.</summary>
    public static string? Signature(IReadOnlyList<RunObservation> attempts)
    {
        var failures = attempts.Where(static x => !x.Success).ToArray();
        if (failures.Length == 0) return null;
        var dominant = failures
            .GroupBy(static x => x.ExitCode)
            .OrderByDescending(static g => g.Count())
            .ThenBy(static g => g.Key)
            .First();
        return string.Create(CultureInfo.InvariantCulture,
            $"0x{unchecked((uint)dominant.Key):X8} x{dominant.Count()}");
    }

    private static void AddMemory(List<SummaryRow> rows, string scenario, RunObservation[] winui, RunObservation[] fluent,
        string? caveat)
    {
        AddDistribution(rows, scenario, "working set",
            winui.Select(static x => x.WorkingSetBytes / 1048576d),
            fluent.Select(static x => x.WorkingSetBytes / 1048576d), "MiB", caveat);
        AddDistribution(rows, scenario, "private bytes",
            winui.Select(static x => x.PrivateBytes / 1048576d),
            fluent.Select(static x => x.PrivateBytes / 1048576d), "MiB", caveat);
    }

    private static void AddWorkloadDelta(List<SummaryRow> rows, string scenario, RunObservation[] winui,
        RunObservation[] fluent, double? winStartup, double? fgStartup, string? caveat)
    {
        if (scenario is not (BenchScenarios.Buttons225 or BenchScenarios.Text1125)) return;
        if (winStartup is null && fgStartup is null) return;
        double? win = Delta(MedianOrNull(winui.Select(static x => x.ExternalStartToResultMs)), winStartup);
        double? fg = Delta(MedianOrNull(fluent.Select(static x => x.ExternalStartToResultMs)), fgStartup);
        if (win is null && fg is null) return;
        AddRow(rows, scenario, WorkloadDeltaMetric, win, fg, "ms", SampleCount(winui.Length, fluent.Length),
            Combine(caveat, WorkloadDeltaCaveat));

        static double? Delta(double? scenarioMedian, double? startupMedian)
            => scenarioMedian is null || startupMedian is null ? null : scenarioMedian - startupMedian;
    }

    private static void AddDistribution(List<SummaryRow> rows, string scenario, string metric,
        IEnumerable<double> winui, IEnumerable<double> fluent, string unit, string? caveat)
    {
        double[] win = winui.Order().ToArray();
        double[] fg = fluent.Order().ToArray();
        int count = SampleCount(win.Length, fg.Length);
        AddRow(rows, scenario, metric + " p50", At(win, 0.50), At(fg, 0.50), unit, count, caveat);
        AddRow(rows, scenario, metric + " p90", At(win, 0.90), At(fg, 0.90), unit, count, caveat);
        AddRow(rows, scenario, metric + " p99", At(win, 0.99), At(fg, 0.99), unit, count, caveat);
        AddRow(rows, scenario, metric + " max", win.Length == 0 ? null : win[^1], fg.Length == 0 ? null : fg[^1],
            unit, count, caveat);

        static double? At(double[] sorted, double p) => sorted.Length == 0 ? null : Percentile(sorted, p);
    }

    private static void AddRow(List<SummaryRow> rows, string scenario, string metric, double? winui, double? fluent,
        string unit, int sampleCount, string? caveat)
    {
        double? ratio = winui is null || fluent is null || fluent.Value == 0d ? null : winui / fluent;
        double? reduction = winui is null || fluent is null || winui.Value == 0d
            ? null
            : (winui - fluent) / winui * 100d;
        rows.Add(new SummaryRow
        {
            Scenario = scenario,
            Metric = metric,
            WinUI = winui,
            FluentGpu = fluent,
            Ratio = ratio,
            ReductionPercent = reduction,
            Unit = unit,
            SampleCount = sampleCount,
            Caveat = caveat,
        });
    }

    /// <summary>Both sides present: the comparable count. One side missing: the count that actually exists.</summary>
    private static int SampleCount(int winui, int fluent)
        => winui == 0 || fluent == 0 ? Math.Max(winui, fluent) : Math.Min(winui, fluent);

    private static string? MissingSideCaveat(List<ScenarioOutcome> outcomes, string scenario, int winui, int fluent)
    {
        if (winui > 0 && fluent > 0) return null;
        string? win = winui == 0 ? Describe(WinUIFramework) : null;
        string? fg = fluent == 0 ? Describe(FluentGpuFramework) : null;
        return Combine(win, fg);

        string Describe(string framework)
        {
            ScenarioOutcome? outcome = outcomes.FirstOrDefault(o =>
                o.Scenario == scenario && string.Equals(o.Framework, framework, StringComparison.Ordinal));
            if (outcome is null || outcome.Attempted == 0) return $"{framework} was not run for this scenario";
            string reason = outcome.FailureSignature is { } signature
                ? $" ({signature.Split(' ')[0]})"
                : string.Empty;
            return string.Create(CultureInfo.InvariantCulture,
                $"{framework} crashed in all {outcome.Attempted} runs{reason}");
        }
    }

    private static string? Combine(string? first, string? second)
        => first is null ? second : second is null ? first : first + "; " + second;

    private static double? MedianOrNull(IEnumerable<double> values)
    {
        double[] materialized = values.ToArray();
        return materialized.Length == 0 ? null : Percentile(materialized, 0.50);
    }

    private static double? MeanOrNull(IEnumerable<double> values)
    {
        double[] materialized = values.ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }

    private static RunObservation[] Select(IReadOnlyList<RunObservation> runs, string scenario, string framework)
        => runs.Where(x => string.Equals(x.Scenario, scenario, StringComparison.Ordinal) &&
                           string.Equals(x.Framework, framework, StringComparison.Ordinal)).ToArray();

    private static RunObservation[] Successful(IReadOnlyList<RunObservation> runs, string scenario, string framework)
        => runs.Where(x => x.Success && string.Equals(x.Scenario, scenario, StringComparison.Ordinal) &&
                           string.Equals(x.Framework, framework, StringComparison.Ordinal)).ToArray();
}
