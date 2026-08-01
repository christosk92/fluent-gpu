using Bench.Contracts;
using Xunit;

namespace Bench.Tests;

public sealed class SummaryAggregationTests
{
    private const int StowedException = unchecked((int)0xC000027B);

    private static RunObservation Success(string scenario, string framework, double external,
        double[]? cpu = null, double[]? frame = null, long allocated = 0, int iterations = 1) => new()
    {
        Scenario = scenario,
        Framework = framework,
        Success = true,
        ExitCode = 0,
        ExternalStartToResultMs = external,
        CpuWorkMs = cpu,
        FrameMs = frame,
        AllocatedBytes = allocated,
        Iterations = iterations,
        WorkingSetBytes = 100L * 1048576L,
        PrivateBytes = 120L * 1048576L,
    };

    private static RunObservation Crash(string scenario, string framework, int exitCode = StowedException) => new()
    {
        Scenario = scenario,
        Framework = framework,
        Success = false,
        ExitCode = exitCode,
        ExternalStartToResultMs = 0d,
    };

    [Fact]
    public void OneSidedScenarioStillProducesRowsWithNullSideAndCaveat()
    {
        var runs = new List<RunObservation>();
        for (int i = 0; i < 5; i++)
        {
            runs.Add(Success(BenchScenarios.VirtualScroll10K, SummaryAggregation.FluentGpuFramework, 900d,
                cpu: [1d, 2d, 3d], allocated: 3000, iterations: 3));
            runs.Add(Crash(BenchScenarios.VirtualScroll10K, SummaryAggregation.WinUIFramework));
        }

        AggregatedSummary summary = SummaryAggregation.Aggregate(runs, "cpu");

        SummaryRow cpuRow = Assert.Single(summary.Rows,
            r => r.Scenario == BenchScenarios.VirtualScroll10K && r.Metric == "CPU work p50");
        Assert.Null(cpuRow.WinUI);
        Assert.Equal(2d, cpuRow.FluentGpu!.Value, 6);
        Assert.Null(cpuRow.Ratio);
        Assert.Null(cpuRow.ReductionPercent);
        // The surviving side's observation count, not zero: a missing side must never zero out the sample count.
        Assert.Equal(15, cpuRow.SampleCount);
        Assert.Equal(5, Assert.Single(summary.Rows,
            r => r.Metric == SummaryAggregation.AllocMetric).SampleCount);
        Assert.Contains("WinUI 3 crashed in all 5 runs (0xC000027B)", cpuRow.Caveat!);
        Assert.Contains(SummaryAggregation.CpuWorkCaveat, cpuRow.Caveat!);

        ScenarioOutcome winui = Assert.Single(summary.Outcomes, o => o.Framework == SummaryAggregation.WinUIFramework);
        Assert.Equal(5, winui.Attempted);
        Assert.Equal(0, winui.Succeeded);
        Assert.Equal("0xC000027B x5", winui.FailureSignature);

        ScenarioOutcome fluent = Assert.Single(summary.Outcomes, o => o.Framework == SummaryAggregation.FluentGpuFramework);
        Assert.Equal(5, fluent.Attempted);
        Assert.Equal(5, fluent.Succeeded);
        Assert.Null(fluent.FailureSignature);
    }

    [Fact]
    public void ScenarioWithNoSuccessAtAllStillReportsOutcomesButNoRows()
    {
        var runs = new List<RunObservation>
        {
            Crash(BenchScenarios.TreeChurn, SummaryAggregation.WinUIFramework),
            Crash(BenchScenarios.TreeChurn, SummaryAggregation.FluentGpuFramework, exitCode: 1),
        };

        AggregatedSummary summary = SummaryAggregation.Aggregate(runs, "cpu");

        Assert.Empty(summary.Rows);
        Assert.Equal(2, summary.Outcomes.Length);
        Assert.All(summary.Outcomes, o => Assert.Equal(0, o.Succeeded));
        Assert.Equal("0x00000001 x1",
            Assert.Single(summary.Outcomes, o => o.Framework == SummaryAggregation.FluentGpuFramework).FailureSignature);
    }

    [Fact]
    public void FrameTimeRowsAreEmittedForTheCadencePassOnly()
    {
        var runs = new List<RunObservation>
        {
            Success(BenchScenarios.TreeChurn, SummaryAggregation.WinUIFramework, 500d, cpu: [4d], frame: [8d], iterations: 1),
            Success(BenchScenarios.TreeChurn, SummaryAggregation.FluentGpuFramework, 400d, cpu: [1d], frame: [8d], iterations: 1),
        };

        AggregatedSummary cpuPass = SummaryAggregation.Aggregate(runs, "cpu");
        Assert.DoesNotContain(cpuPass.Rows, r => r.Metric.StartsWith("frame time", StringComparison.Ordinal));

        AggregatedSummary cadencePass = SummaryAggregation.Aggregate(runs, "cadence");
        Assert.Contains(cadencePass.Rows, r => r.Metric == "frame time p50");
        Assert.Contains(cadencePass.Rows, r => r.Metric == "frame time p99");
        Assert.Contains(cadencePass.Rows, r => r.Metric == "frame time max");
    }

    [Fact]
    public void WorkloadDeltaRowsAppearOnlyWhenStartupWasMeasured()
    {
        var withoutStartup = new List<RunObservation>
        {
            Success(BenchScenarios.Buttons225, SummaryAggregation.WinUIFramework, 700d),
            Success(BenchScenarios.Buttons225, SummaryAggregation.FluentGpuFramework, 300d),
        };
        Assert.DoesNotContain(SummaryAggregation.Aggregate(withoutStartup, "cpu").Rows,
            r => r.Metric == SummaryAggregation.WorkloadDeltaMetric);

        var withStartup = new List<RunObservation>
        {
            Success(BenchScenarios.Startup, SummaryAggregation.WinUIFramework, 500d),
            Success(BenchScenarios.Startup, SummaryAggregation.FluentGpuFramework, 200d),
            Success(BenchScenarios.Buttons225, SummaryAggregation.WinUIFramework, 700d),
            Success(BenchScenarios.Buttons225, SummaryAggregation.FluentGpuFramework, 300d),
        };
        AggregatedSummary summary = SummaryAggregation.Aggregate(withStartup, "cpu");

        SummaryRow delta = Assert.Single(summary.Rows, r => r.Metric == SummaryAggregation.WorkloadDeltaMetric);
        Assert.Equal(BenchScenarios.Buttons225, delta.Scenario);
        Assert.Equal(200d, delta.WinUI!.Value, 6);
        Assert.Equal(100d, delta.FluentGpu!.Value, 6);
        Assert.Equal(2d, delta.Ratio!.Value, 6);
        Assert.Equal(SummaryAggregation.WorkloadDeltaCaveat, delta.Caveat);
        // startup itself is never delta'd against itself.
        Assert.DoesNotContain(summary.Rows,
            r => r.Scenario == BenchScenarios.Startup && r.Metric == SummaryAggregation.WorkloadDeltaMetric);
    }

    [Fact]
    public void CpuWorkAndAllocationRowsCarryTheirDefinitionCaveats()
    {
        var runs = new List<RunObservation>
        {
            Success(BenchScenarios.LocalizedText, SummaryAggregation.WinUIFramework, 600d, cpu: [2d, 4d], allocated: 800, iterations: 2),
            Success(BenchScenarios.LocalizedText, SummaryAggregation.FluentGpuFramework, 300d, cpu: [1d, 1d], allocated: 0, iterations: 2),
        };

        AggregatedSummary summary = SummaryAggregation.Aggregate(runs, "cpu");

        Assert.All(summary.Rows.Where(r => r.Metric.StartsWith("CPU work", StringComparison.Ordinal)),
            r => Assert.Equal(SummaryAggregation.CpuWorkCaveat, r.Caveat));

        SummaryRow alloc = Assert.Single(summary.Rows, r => r.Metric == SummaryAggregation.AllocMetric);
        Assert.Equal(SummaryAggregation.AllocCaveat, alloc.Caveat);
        Assert.Equal(400d, alloc.WinUI!.Value, 6);
        Assert.Equal(0d, alloc.FluentGpu!.Value, 6);
        Assert.Null(alloc.Ratio); // dividing by a zero-allocation side is not a ratio.

        // Memory rows are definition-neutral and carry no caveat when both sides ran.
        Assert.All(summary.Rows.Where(r => r.Metric.StartsWith("working set", StringComparison.Ordinal)),
            r => Assert.Null(r.Caveat));
    }

    [Theory]
    [InlineData(0.50, 5.5)]
    [InlineData(0.90, 9.1)]
    [InlineData(0.99, 9.91)]
    public void PercentileMatchesLinearInterpolationOnAKnownArray(double p, double expected)
    {
        double[] values = [10d, 9d, 8d, 7d, 6d, 5d, 4d, 3d, 2d, 1d];
        Assert.Equal(expected, SummaryAggregation.Percentile(values, p), 6);
    }

    [Fact]
    public void DistributionRowsUseTheSameKnownPercentiles()
    {
        double[] ten = [1d, 2d, 3d, 4d, 5d, 6d, 7d, 8d, 9d, 10d];
        var runs = new List<RunObservation>
        {
            Success(BenchScenarios.LocalizedTransform, SummaryAggregation.WinUIFramework, 600d, cpu: ten, iterations: 10),
            Success(BenchScenarios.LocalizedTransform, SummaryAggregation.FluentGpuFramework, 300d, cpu: ten, iterations: 10),
        };

        SummaryRow[] rows = SummaryAggregation.Aggregate(runs, "cpu").Rows;

        Assert.Equal(5.5d, Assert.Single(rows, r => r.Metric == "CPU work p50").WinUI!.Value, 6);
        Assert.Equal(9.1d, Assert.Single(rows, r => r.Metric == "CPU work p90").WinUI!.Value, 6);
        Assert.Equal(9.91d, Assert.Single(rows, r => r.Metric == "CPU work p99").WinUI!.Value, 6);
        Assert.Equal(10d, Assert.Single(rows, r => r.Metric == "CPU work max").WinUI!.Value, 6);
    }
}
