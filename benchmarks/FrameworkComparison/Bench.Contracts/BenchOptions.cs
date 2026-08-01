using System.Globalization;

namespace Bench.Contracts;

public sealed record BenchOptions
{
    public required string Scenario { get; init; }
    public required string OutputPath { get; init; }
    public int Iterations { get; init; } = BenchWorkload.DefaultIterations;
    public int WarmupFrames { get; init; } = BenchWorkload.DefaultWarmupFrames;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string Pass { get; init; } = "in-app";
    /// <summary>Optional JSONL pacing-trace path (FluentGPU host). Null = disabled.</summary>
    public string? PacingTracePath { get; init; }

    public static BenchOptions Parse(string[] args)
    {
        string scenario = Get(args, "--scenario") ?? BenchScenarios.Startup;
        if (!BenchScenarios.IsKnown(scenario))
            throw new ArgumentException($"Unknown scenario '{scenario}'.");

        string output = Get(args, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, $"{scenario}-{Environment.ProcessId}.json");
        string? pacing = Get(args, "--pacing-trace");
        return new BenchOptions
        {
            Scenario = scenario,
            OutputPath = Path.GetFullPath(output),
            Iterations = GetInt(args, "--iterations", BenchWorkload.DefaultIterations, 1, 100_000),
            WarmupFrames = GetInt(args, "--warmup", BenchWorkload.DefaultWarmupFrames, 0, 10_000),
            RunId = Get(args, "--run-id") ?? Guid.NewGuid().ToString("N"),
            Pass = Get(args, "--pass") ?? "in-app",
            PacingTracePath = pacing is null ? null : Path.GetFullPath(pacing),
        };
    }

    private static string? Get(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int GetInt(string[] args, string name, int fallback, int min, int max)
    {
        string? value = Get(args, name);
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException($"{name} requires an integer.");
        return Math.Clamp(parsed, min, max);
    }
}
