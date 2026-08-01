using System.Diagnostics.Tracing;

namespace Bench.Contracts;

[EventSource(Name = "FluentGpu-FrameworkComparison")]
public sealed class BenchTrace : EventSource
{
    public static readonly BenchTrace Log = new();

    private BenchTrace() { }

    [Event(1, Level = EventLevel.Informational)]
    public void ProcessReady(string framework, string scenario, string runId) => WriteEvent(1, framework, scenario, runId);

    [Event(2, Level = EventLevel.Informational)]
    public void PhaseStart(string framework, string scenario, string phase, int iterations, long qpc)
        => WriteEvent(2, framework, scenario, phase, iterations, qpc);

    [Event(3, Level = EventLevel.Informational)]
    public void PhaseStop(string framework, string scenario, string phase, int iterations, long qpc)
        => WriteEvent(3, framework, scenario, phase, iterations, qpc);

    [Event(4, Level = EventLevel.Informational)]
    public void ResultWritten(string framework, string scenario, string path) => WriteEvent(4, framework, scenario, path);

    /// <summary>QPC marker immediately before a measured mutation. Correlate with PresentMon --qpc_time CSV.</summary>
    [Event(5, Level = EventLevel.Informational)]
    public void MutationStart(string framework, string scenario, int iteration, long qpc)
        => WriteEvent(5, framework, scenario, iteration, qpc);

    /// <summary>QPC marker after the framework has acknowledged the mutated frame (FluentGPU present ack / WinUI next Rendering).</summary>
    [Event(6, Level = EventLevel.Informational)]
    public void MutationAck(string framework, string scenario, int iteration, long qpc)
        => WriteEvent(6, framework, scenario, iteration, qpc);
}
