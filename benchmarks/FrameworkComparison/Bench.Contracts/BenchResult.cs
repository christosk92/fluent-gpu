using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bench.Contracts;

public sealed record BenchResult
{
    public required string Schema { get; init; }
    public required string Framework { get; init; }
    public required string FrameworkVersion { get; init; }
    public required string Scenario { get; init; }
    public required string RunId { get; init; }
    public required string Pass { get; init; }
    public required string Architecture { get; init; }
    public required string Runtime { get; init; }
    public required string OperatingSystem { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required long QpcFrequency { get; init; }
    public required long MeasurementStartQpc { get; init; }
    public required long MeasurementStopQpc { get; init; }
    public required int WarmupFrames { get; init; }
    public required int Iterations { get; init; }
    public required double[] FrameMs { get; init; }
    public required double[] CpuWorkMs { get; init; }
    public required long AllocatedBytes { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long PrivateBytes { get; init; }
    public string? Notes { get; init; }
    /// <summary>Cold-load sub-marks (startup, buttons-225, text-1125). Null for every other scenario.</summary>
    public ColdStartMarks? ColdStart { get; init; }

    public static BenchResult Create(
        string framework,
        string version,
        BenchOptions options,
        long startQpc,
        long stopQpc,
        double[] frameMs,
        double[] cpuWorkMs,
        long allocatedBytes,
        long workingSetBytes,
        long privateBytes,
        string? notes = null,
        ColdStartMarks? coldStart = null) => new()
        {
            Schema = "fluentgpu-framework-bench/v2",
            Framework = framework,
            FrameworkVersion = version,
            Scenario = options.Scenario,
            RunId = options.RunId,
            Pass = options.Pass,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            TimestampUtc = DateTimeOffset.UtcNow,
            QpcFrequency = System.Diagnostics.Stopwatch.Frequency,
            MeasurementStartQpc = startQpc,
            MeasurementStopQpc = stopQpc,
            WarmupFrames = options.WarmupFrames,
            Iterations = frameMs.Length,
            FrameMs = frameMs,
            CpuWorkMs = cpuWorkMs,
            AllocatedBytes = allocatedBytes,
            WorkingSetBytes = workingSetBytes,
            PrivateBytes = privateBytes,
            Notes = notes,
            ColdStart = coldStart,
        };

    public void Write(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, BenchJsonContext.Default.BenchResult));
        File.Move(temp, fullPath, true);
    }
}

/// <summary>
/// Cold-load anchoring sub-marks. Both hosts anchor at process module initialization
/// (<c>[ModuleInitializer]</c>) and stop at their first-frame-handed-to-the-compositor equivalent.
/// </summary>
public sealed record ColdStartMarks
{
    /// <summary>Module init to "framework is up and about to produce a frame" (FluentGPU: harness entry with device and
    /// window live; WinUI: the first <c>CompositionTarget.Rendering</c> callback).</summary>
    public required double? EngineReadyMs { get; init; }
    /// <summary>Module init to the measurement stop point, identical to <c>frameMs[0]</c>.</summary>
    public required double? FirstPresentMs { get; init; }
    /// <summary>Frames the harness had to pump itself to reach the stop point (WinUI is callback-driven: always 0).</summary>
    public required int DrivenFrames { get; init; }
}

public sealed record BenchmarkSummary
{
    public required string Schema { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required string Machine { get; init; }
    public required string FluentGpuCommit { get; init; }
    /// <summary>SHA-256 of <c>fluentgpu-benchmark.patch</c>, covering the engine, controls, Windows backend and hosts.</summary>
    public required string FluentGpuBenchmarkPatchSha256 { get; init; }
    /// <summary>Pinned public Windows App SDK/WinUI baseline, for example <c>Microsoft.WindowsAppSDK 2.3.1</c>.</summary>
    public required string WinUIBaseline { get; init; }
    public required string WinUIBinarySha256 { get; init; }
    /// <summary>Archived release-evidence manifest supplied by the publish step, or null for a diagnostic run.</summary>
    public string? BuildEvidencePath { get; init; }
    public required SummaryRow[] Rows { get; init; }
    /// <summary>Per-scenario, per-framework attempt/success accounting. Emitted even when a framework never succeeded.</summary>
    public required ScenarioOutcome[] Outcomes { get; init; }
    public required RunRecord[] Runs { get; init; }
}

public sealed record SummaryRow
{
    public required string Scenario { get; init; }
    public required string Metric { get; init; }
    /// <summary>Null when WinUI 3 had zero successful runs for this scenario.</summary>
    public required double? WinUI { get; init; }
    /// <summary>Null when FluentGPU had zero successful runs for this scenario.</summary>
    public required double? FluentGpu { get; init; }
    public required double? Ratio { get; init; }
    public required double? ReductionPercent { get; init; }
    public required string Unit { get; init; }
    /// <summary>Number of raw observations represented by this summary value.</summary>
    public required int SampleCount { get; init; }
    /// <summary>What this row does and does not mean; rendered as a footnote in the Markdown report.</summary>
    public string? Caveat { get; init; }
}

public sealed record ScenarioOutcome
{
    public required string Scenario { get; init; }
    public required string Framework { get; init; }
    public required int Attempted { get; init; }
    public required int Succeeded { get; init; }
    /// <summary>Most common failure exit code and its count, for example <c>0xC000027B x5</c>. Null when nothing failed.</summary>
    public required string? FailureSignature { get; init; }
}

public sealed record RunRecord
{
    public required string Framework { get; init; }
    public required string Scenario { get; init; }
    public required string Pass { get; init; }
    public required int Repetition { get; init; }
    public required string RunId { get; init; }
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public required double ExternalStartToResultMs { get; init; }
    public required string ResultPath { get; init; }
    public string? Failure { get; init; }
}

[JsonSerializable(typeof(BenchResult))]
[JsonSerializable(typeof(ColdStartMarks))]
[JsonSerializable(typeof(BenchmarkSummary))]
[JsonSerializable(typeof(SummaryRow))]
[JsonSerializable(typeof(SummaryRow[]))]
[JsonSerializable(typeof(ScenarioOutcome))]
[JsonSerializable(typeof(ScenarioOutcome[]))]
[JsonSerializable(typeof(RunRecord[]))]
[JsonSerializable(typeof(FrameIdVisibilityResult))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class BenchJsonContext : JsonSerializerContext;
