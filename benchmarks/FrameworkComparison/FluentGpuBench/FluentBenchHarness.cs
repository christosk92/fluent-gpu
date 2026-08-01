using System.Diagnostics;
using System.Reflection;
using Bench.Contracts;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace FluentGpuBench;

internal static class FluentBenchHarness
{
    internal static bool Run(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        // Harness entry: the device, window and app tree are live and the first frame has not been published yet.
        long harnessEntryQpc = Stopwatch.GetTimestamp();
        BenchOptions options = FluentBenchState.Options;
        var gpu = device as D3D12Device;
        bool rawCpu = !string.Equals(options.Pass, "cadence", StringComparison.OrdinalIgnoreCase);

        void Frame()
        {
            if (rawCpu && gpu is not null)
            {
                gpu.SuppressLatencyWaitOnce();
                gpu.SuppressVsyncOnce();
            }
            host.RunFrame();
        }

        void WaitForPublishedFrame()
        {
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
            while (host.PublishSequence != 0 && host.LastPresentPublishSeq < host.PublishSequence &&
                   Stopwatch.GetTimestamp() < deadline && !window.IsClosed)
                window.WaitForWork(1);
        }

        if (BenchScenarios.IsColdLoad(options.Scenario))
        {
            BenchTrace.Log.PhaseStart("FluentGpu", options.Scenario, "startup", 1, BenchClock.ProcessStartQpc);
            int drivenFrames = 0;
            if (host.PublishSequence == 0) { Frame(); drivenFrames = 1; }
            WaitForPublishedFrame();
            long stop = Stopwatch.GetTimestamp();
            var stats = host.LastStats;
            var marks = new ColdStartMarks
            {
                EngineReadyMs = TicksToMs(harnessEntryQpc - BenchClock.ProcessStartQpc),
                FirstPresentMs = TicksToMs(stop - BenchClock.ProcessStartQpc),
                DrivenFrames = drivenFrames,
            };
            WriteResult(options, BenchClock.ProcessStartQpc, stop,
                [TicksToMs(stop - BenchClock.ProcessStartQpc)], [CpuWork(stats)], stats.HotPhaseAllocBytes,
                "Process module initialization to render-thread acknowledgement of the first published frame. " +
                "engineReadyMs is module init to harness entry (device, window and app tree live, nothing published yet); " +
                $"firstPresentMs is the same anchor to the stop point; diagnostic-driven frames={drivenFrames}.",
                marks);
            return true;
        }

        if (host.PublishSequence == 0) Frame();
        WaitForPublishedFrame();

        for (int i = 0; i < options.WarmupFrames && !window.IsClosed; i++)
        {
            WaitForPublishedFrame();
            FluentBenchState.PaintFrameId(i, Stopwatch.GetTimestamp(), logMutation: false);
            FluentBenchState.Mutate(i);
            Frame();
        }

        // FG_ALLOC_TYPES=1 attribution: AppHost drives AllocTypeProfiler.MaybeReport from its interactive loop, which
        // DiagnosticRun replaces — so the harness has to tick it itself or the profiler never prints. Diagnostic-only:
        // the flag is off in every measured run, and the check is a cached bool.
        bool allocTypes = Environment.GetEnvironmentVariable("FG_ALLOC_TYPES") is "1" or "true";
        long componentRenders = 0;

        FluentPacingTrace.Begin(options.PacingTracePath, options.Iterations, host);
        var frameMs = new double[options.Iterations];
        var cpuMs = new double[options.Iterations];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        BenchTrace.Log.PhaseStart("FluentGpu", options.Scenario, "measure", options.Iterations, start);
        for (int i = 0; i < options.Iterations && !window.IsClosed; i++)
        {
            WaitForPublishedFrame();
            long t0 = Stopwatch.GetTimestamp();
            BenchTrace.Log.MutationStart("FluentGpu", options.Scenario, i, t0);
            FluentBenchState.PaintFrameId(i, t0, logMutation: true);
            FluentBenchState.Mutate(i + options.WarmupFrames);
            Frame();
            if (!rawCpu) WaitForPublishedFrame();
            long t1 = Stopwatch.GetTimestamp();
            BenchTrace.Log.MutationAck("FluentGpu", options.Scenario, i, t1);
            double cpu = CpuWork(host.LastStats);
            frameMs[i] = TicksToMs(t1 - t0);
            cpuMs[i] = cpu;
            FluentPacingTrace.Record(i, t0, t1, host, device, host.LastStats, cpu);
            if (allocTypes) { AllocTypeProfiler.MaybeReport(); componentRenders += host.LastStats.ComponentsRendered; }
        }
        if (allocTypes)
            Console.Error.WriteLine($"[bench] componentRenders={componentRenders} over {options.Iterations} iterations " +
                                    $"({componentRenders / (double)options.Iterations:0.00}/op)");
        long stopQpc = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        FluentPacingTrace.WriteIfEnabled();
        string notes = rawCpu
            ? "Vsync and the swap-chain latency wait were suppressed; cpuWorkMs excludes fence/present waits."
            : "Display-paced cadence pass; frameMs includes presentation pacing.";
        if (host.PhaseGateCeilingEscapes > 0)
            notes += $" phaseGateCeilingEscapes={host.PhaseGateCeilingEscapes}.";
        if (FluentBenchState.ScrollValidityNote() is { } scroll) notes += " " + scroll;
        WriteResult(options, start, stopQpc, frameMs, cpuMs, allocated, notes);
        return true;
    }

    private static double CpuWork(in FrameStats s)
        => s.FlushMs + s.LayoutMs + s.AnimMs + s.RecordMs + Math.Max(0d, s.SubmitMs - s.FenceWaitMs - s.PresentMs);

    private static double TicksToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static void WriteResult(
        BenchOptions options,
        long start,
        long stop,
        double[] frameMs,
        double[] cpuMs,
        long allocated,
        string notes,
        ColdStartMarks? coldStart = null)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        BenchResult result = BenchResult.Create(
            "FluentGpu", version, options, start, stop, frameMs, cpuMs, allocated,
            process.WorkingSet64, process.PrivateMemorySize64, notes, coldStart);
        result.Write(options.OutputPath);
        BenchTrace.Log.PhaseStop("FluentGpu", options.Scenario, "measure", frameMs.Length, stop);
        BenchTrace.Log.ResultWritten("FluentGpu", options.Scenario, options.OutputPath);
    }
}
