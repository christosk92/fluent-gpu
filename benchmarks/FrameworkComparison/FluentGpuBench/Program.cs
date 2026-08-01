using Bench.Contracts;
using FluentGpu;

namespace FluentGpuBench;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        BenchOptions options = BenchOptions.Parse(args);
        FluentBenchState.Initialize(options);
        BenchTrace.Log.ProcessReady("FluentGpu", options.Scenario, options.RunId);
        FluentApp.DiagnosticRun = FluentBenchHarness.Run;
        FluentApp.Run(() => new FluentBenchApp(), new AppOptions
        {
            Title = $"FluentGpu benchmark - {options.Scenario}",
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            MinWidth = BenchWorkload.WindowWidth,
            MinHeight = BenchWorkload.WindowHeight,
            Mica = false,
            WarmCadenceMs = 0,
        });
    }
}
