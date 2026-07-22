using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Foundation;
using FluentGpu.Text;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class Program
{
    static int Main(string[] args)
    {
        var probe = Environment.GetEnvironmentVariable("FG_PROBE");
        if (probe == "ranged-tooltip") return ProbeDrivers.RangedTooltipFreezeProbe();
        if (probe == "titlebar-resize") return ProbeDrivers.TitleBarResizeProbe();
        if (probe == "scroll-flicker") return ProbeDrivers.ScrollFlickerProbe();

        string? suiteSpec = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--suite" && i + 1 < args.Length) { suiteSpec = args[i + 1]; break; }
            if (args[i].StartsWith("--suite=", StringComparison.Ordinal))
            { suiteSpec = args[i]["--suite=".Length..]; break; }
        }
        suiteSpec ??= Environment.GetEnvironmentVariable("FG_SUITE");

        bool fullRun = string.IsNullOrWhiteSpace(suiteSpec)
            || suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool runCore;
        SuiteEntry[] suites;
        try
        {
            if (fullRun)
            {
                runCore = true;
                suites = SuiteRegistry.All;
            }
            else
            {
                var tags = new HashSet<string>(
                    suiteSpec!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                runCore = tags.Contains("core");
                var known = new HashSet<string>(SuiteRegistry.All.Select(e => e.Tag), StringComparer.OrdinalIgnoreCase);
                known.Add("core");
                known.Add("all");
                foreach (var t in tags)
                {
                    if (!known.Contains(t))
                        throw new ArgumentException($"Unknown suite '{t}'.");
                }
                suites = SuiteRegistry.All.Where(e => tags.Contains(e.Tag)).ToArray();
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Known suites: " + SuiteRegistry.KnownSuitesHelp());
            return 2;
        }

        Console.WriteLine("FluentGpu — minimum vertical slice (headless RHI/PAL/Text)\n");
        var strings = new StringTable();

        if (runCore)
            CoreSuite.Run(strings);
        foreach (var s in suites)
            s.Run(strings);

        Console.WriteLine();
        if (Failures == 0)
        {
            if (!fullRun)
                Console.WriteLine($"ALL CHECKS PASSED (suite={suiteSpec}, {Total} checks)");
            else
                Console.WriteLine($"ALL CHECKS PASSED — the vertical slice exercises every seam end-to-end.{ArenaSummarySuffix()}");
            return 0;
        }
        Console.WriteLine($"{Failures} CHECK(S) FAILED.");
        return 1;
    }
}
