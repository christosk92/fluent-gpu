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
            MinWidth = options.Scenario == BenchScenarios.WindowResize ? BenchWorkload.ResizeWindowMinWidth : BenchWorkload.WindowWidth,
            MinHeight = options.Scenario == BenchScenarios.WindowResize ? BenchWorkload.ResizeWindowMinHeight : BenchWorkload.WindowHeight,
            Mica = false,
            // Diagnostic only, never passed by the runner: `--composited` puts the window on the DComp flip-surface path a
            // Mica app (Wavee) uses, instead of the suite's opaque HWND swapchain.
            Composited = Array.IndexOf(args, "--composited") >= 0 ? true : null,
            WarmCadenceMs = 0,
        });
    }
}
