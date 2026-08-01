using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bench.Contracts;

namespace Bench.Runner;

internal static class Program
{
    private sealed record Options(
        string FluentExe,
        string WinUIExe,
        string Output,
        string Pass,
        string[] Scenarios,
        int Iterations,
        int Warmup,
        int Repetitions,
        int StartupRepetitions,
        int LoadRepetitions,
        int TimeoutSeconds,
        string? BuildEvidence);

    private sealed record Completed(RunRecord Run, BenchResult? Result);

    private static int Main(string[] args)
    {
        try
        {
            Options options = Parse(args);
            Directory.CreateDirectory(options.Output);
            var completed = new List<Completed>();
            foreach (string scenario in options.Scenarios)
            {
                int repetitions = scenario == BenchScenarios.Startup
                    ? options.StartupRepetitions
                    : BenchScenarios.IsColdLoad(scenario) ? options.LoadRepetitions : options.Repetitions;
                for (int repetition = 0; repetition < repetitions; repetition++)
                {
                    bool fluentFirst = (repetition & 1) == 0;
                    if (fluentFirst)
                    {
                        completed.Add(RunOne("FluentGpu", options.FluentExe, scenario, repetition, options));
                        completed.Add(RunOne("WinUI 3", options.WinUIExe, scenario, repetition, options));
                    }
                    else
                    {
                        completed.Add(RunOne("WinUI 3", options.WinUIExe, scenario, repetition, options));
                        completed.Add(RunOne("FluentGpu", options.FluentExe, scenario, repetition, options));
                    }
                }
            }

            WriteEvidence(options, completed);
            int failures = completed.Count(static x => !x.Run.Success);
            Console.WriteLine(failures == 0
                ? $"Completed {completed.Count} runs without a crash."
                : $"Completed {completed.Count} runs with {failures} crash(es)/failure(s); retained in summary.json.");
            return failures == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Completed RunOne(string framework, string executable, string scenario, int repetition, Options options)
    {
        string runId = Guid.NewGuid().ToString("N");
        string safeFramework = framework.StartsWith("WinUI", StringComparison.Ordinal) ? "winui" : "fluentgpu";
        string resultPath = Path.Combine(options.Output, "raw", options.Pass, scenario,
            $"{safeFramework}-{repetition:00}-{runId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(resultPath);
        startInfo.ArgumentList.Add("--iterations");
        startInfo.ArgumentList.Add(options.Iterations.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--warmup");
        startInfo.ArgumentList.Add(options.Warmup.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--pass");
        startInfo.ArgumentList.Add(options.Pass);
        startInfo.ArgumentList.Add("--run-id");
        startInfo.ArgumentList.Add(runId);

        var wall = Stopwatch.StartNew();
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not launch {executable}.");
        double resultObservedMs = 0d;
        var deadline = DateTime.UtcNow.AddSeconds(options.TimeoutSeconds);
        while (!process.HasExited && DateTime.UtcNow < deadline)
        {
            if (resultObservedMs == 0d && File.Exists(resultPath)) resultObservedMs = wall.Elapsed.TotalMilliseconds;
            Thread.Sleep(2);
        }
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        if (resultObservedMs == 0d && File.Exists(resultPath)) resultObservedMs = wall.Elapsed.TotalMilliseconds;

        bool success = process.ExitCode == 0 && File.Exists(resultPath);
        BenchResult? result = null;
        string? failure = null;
        if (success)
        {
            try
            {
                result = JsonSerializer.Deserialize(File.ReadAllText(resultPath), BenchJsonContext.Default.BenchResult)
                    ?? throw new JsonException("Result deserialized to null.");
            }
            catch (Exception ex)
            {
                success = false;
                failure = $"Invalid result: {ex.Message}";
            }
        }
        else
        {
            failure = process.ExitCode == 0
                ? "Process exited without writing a result."
                : $"Process exited with 0x{unchecked((uint)process.ExitCode):X8}.";
        }

        var run = new RunRecord
        {
            Framework = framework,
            Scenario = scenario,
            Pass = options.Pass,
            Repetition = repetition,
            RunId = runId,
            Success = success,
            ExitCode = process.ExitCode,
            ExternalStartToResultMs = resultObservedMs,
            ResultPath = Path.GetRelativePath(options.Output, resultPath),
            Failure = failure,
        };
        Console.WriteLine($"{(success ? "PASS" : "FAIL"),4} {scenario,-22} {framework,-9} rep={repetition:00} " +
            $"start->result={resultObservedMs:0.0} ms exit=0x{unchecked((uint)process.ExitCode):X8}");
        return new Completed(run, result);
    }

    private static void WriteEvidence(Options options, List<Completed> completed)
    {
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string fluentCommit = Git(repo, "rev-parse HEAD").Trim();
        // The executable under test is not only Engine: Controls and the Windows PAL/RHI are part of the measured
        // product, as are these benchmark hosts. Archive every local input that can affect a result.
        string benchmarkPatch = Git(repo,
            "diff --binary HEAD -- src/FluentGpu.Engine src/FluentGpu.Controls src/FluentGpu.Windows benchmarks/FrameworkComparison");
        string patchPath = Path.Combine(options.Output, "fluentgpu-benchmark.patch");
        File.WriteAllText(patchPath, benchmarkPatch);
        string patchHash = Sha256(Encoding.UTF8.GetBytes(benchmarkPatch));
        string winUIBinary = Path.Combine(Path.GetDirectoryName(options.WinUIExe)!, "Microsoft.UI.Xaml.dll");
        string binaryHash = File.Exists(winUIBinary) ? Sha256(File.ReadAllBytes(winUIBinary)) : "missing";
        string? buildEvidencePath = null;
        string winUIBaseline = "unrecorded public package";
        if (options.BuildEvidence is not null)
        {
            if (!File.Exists(options.BuildEvidence))
                throw new FileNotFoundException("Build evidence manifest not found.", options.BuildEvidence);
            using JsonDocument evidence = JsonDocument.Parse(File.ReadAllText(options.BuildEvidence));
            JsonElement package = evidence.RootElement.GetProperty("winUi").GetProperty("package");
            winUIBaseline = package.GetProperty("id").GetString() + " " + package.GetProperty("version").GetString();
            buildEvidencePath = "publish-evidence.json";
            File.Copy(options.BuildEvidence, Path.Combine(options.Output, buildEvidencePath), overwrite: true);
        }

        AggregatedSummary aggregated = SummaryAggregation.Aggregate(Observe(completed), options.Pass);
        var summary = new BenchmarkSummary
        {
            Schema = "fluentgpu-framework-bench-summary/v4",
            GeneratedUtc = DateTimeOffset.UtcNow,
            Machine = Environment.MachineName,
            FluentGpuCommit = fluentCommit,
            FluentGpuBenchmarkPatchSha256 = patchHash,
            WinUIBaseline = winUIBaseline,
            WinUIBinarySha256 = binaryHash,
            BuildEvidencePath = buildEvidencePath,
            Rows = aggregated.Rows,
            Outcomes = aggregated.Outcomes,
            Runs = completed.Select(static x => x.Run).ToArray(),
        };
        File.WriteAllText(Path.Combine(options.Output, "summary.json"),
            JsonSerializer.Serialize(summary, BenchJsonContext.Default.BenchmarkSummary));
        File.WriteAllText(Path.Combine(options.Output, "summary.md"), Markdown(summary));
    }

    /// <summary>Projects the completed processes onto the plain-data shape the (unit-tested) aggregator consumes.</summary>
    private static RunObservation[] Observe(List<Completed> completed) => completed.Select(static x => new RunObservation
    {
        Scenario = x.Run.Scenario,
        Framework = x.Run.Framework,
        Success = x.Run.Success,
        ExitCode = x.Run.ExitCode,
        ExternalStartToResultMs = x.Run.ExternalStartToResultMs,
        CpuWorkMs = x.Result?.CpuWorkMs,
        FrameMs = x.Result?.FrameMs,
        AllocatedBytes = x.Result?.AllocatedBytes ?? 0L,
        Iterations = x.Result?.Iterations ?? 0,
        WorkingSetBytes = x.Result?.WorkingSetBytes ?? 0L,
        PrivateBytes = x.Result?.PrivateBytes ?? 0L,
    }).ToArray();

    private static string Markdown(BenchmarkSummary summary)
    {
        var footnotes = new List<string>();
        var text = new StringBuilder();
        text.AppendLine("# Raw framework comparison").AppendLine();
        text.AppendLine("| Scenario | Metric | Samples | WinUI 3 | FluentGPU | WinUI / FluentGPU | Reduction |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (SummaryRow row in summary.Rows)
        {
            string marker = string.Empty;
            if (row.Caveat is { } caveat)
            {
                int index = footnotes.IndexOf(caveat);
                if (index < 0) { footnotes.Add(caveat); index = footnotes.Count - 1; }
                marker = $"[^{index + 1}]";
            }
            text.Append("| ").Append(row.Scenario).Append(" | ").Append(row.Metric).Append(marker).Append(" | ")
                .Append(row.SampleCount.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Cell(summary, row, row.WinUI, SummaryAggregation.WinUIFramework)).Append(" | ")
                .Append(Cell(summary, row, row.FluentGpu, SummaryAggregation.FluentGpuFramework)).Append(" | ")
                .Append(row.Ratio?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a").Append("x | ")
                .Append(row.ReductionPercent?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a").AppendLine("% |");
        }

        if (footnotes.Count > 0)
        {
            text.AppendLine().AppendLine("### Notes").AppendLine();
            for (int i = 0; i < footnotes.Count; i++)
                text.Append("[^").Append(i + 1).Append("]: ").AppendLine(footnotes[i]);
        }

        int winCrashes = summary.Runs.Count(static x => !x.Success && x.Framework == "WinUI 3");
        int fluentCrashes = summary.Runs.Count(static x => !x.Success && x.Framework == "FluentGpu");
        text.AppendLine().AppendLine("## Reliability").AppendLine()
            .AppendLine("| Raw outcome | WinUI 3 | FluentGPU |")
            .AppendLine("|---|---:|---:|")
            .Append("| Crashes / failed runs | ").Append(winCrashes).Append(" | ").Append(fluentCrashes).AppendLine(" |");

        text.AppendLine().AppendLine("| Scenario | Framework | Attempted | Succeeded | Failure signature |")
            .AppendLine("|---|---|---:|---:|---|");
        foreach (ScenarioOutcome outcome in summary.Outcomes)
            text.Append("| ").Append(outcome.Scenario).Append(" | ").Append(outcome.Framework).Append(" | ")
                .Append(outcome.Attempted.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(outcome.Succeeded.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(outcome.FailureSignature ?? "-").AppendLine(" |");

        text.AppendLine().AppendLine("Yes, crashing is technically an extremely fast way to stop rendering. Failed runs are never included in latency ratios.");
        return text.ToString();

        static string Cell(BenchmarkSummary report, SummaryRow entry, double? value, string framework)
        {
            if (value is { } number)
                return number.ToString("0.###", CultureInfo.InvariantCulture) + " " + entry.Unit;
            ScenarioOutcome? outcome = report.Outcomes.FirstOrDefault(o =>
                o.Scenario == entry.Scenario && o.Framework == framework);
            return outcome is { Attempted: > 0, Succeeded: 0 } ? "CRASHED" : "no data";
        }
    }

    private static string Git(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments}: {error}");
        return output;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Options Parse(string[] args)
    {
        static string? Get(string[] source, string name)
        {
            int i = Array.IndexOf(source, name);
            return i >= 0 && i + 1 < source.Length ? source[i + 1] : null;
        }
        static int Int(string[] source, string name, int fallback)
            => int.TryParse(Get(source, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

        string fluent = Path.GetFullPath(Get(args, "--fluent") ?? throw new ArgumentException("--fluent <exe> is required."));
        string winui = Path.GetFullPath(Get(args, "--winui") ?? throw new ArgumentException("--winui <exe> is required."));
        if (!File.Exists(fluent)) throw new FileNotFoundException("FluentGPU host not found.", fluent);
        if (!File.Exists(winui)) throw new FileNotFoundException("WinUI host not found.", winui);
        string[] scenarios = (Get(args, "--scenarios") ?? string.Join(',', BenchScenarios.All))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string scenario in scenarios)
            if (!BenchScenarios.IsKnown(scenario)) throw new ArgumentException($"Unknown scenario '{scenario}'.");
        return new Options(
            fluent,
            winui,
            Path.GetFullPath(Get(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))),
            Get(args, "--pass") ?? "cpu",
            scenarios,
            Int(args, "--iterations", BenchWorkload.DefaultIterations),
            Int(args, "--warmup", BenchWorkload.DefaultWarmupFrames),
            Int(args, "--repetitions", 5),
            Int(args, "--startup-repetitions", 30),
            Int(args, "--load-repetitions", 10),
            Int(args, "--timeout-seconds", 120),
            Get(args, "--build-evidence") is { } evidence ? Path.GetFullPath(evidence) : null);
    }
}
