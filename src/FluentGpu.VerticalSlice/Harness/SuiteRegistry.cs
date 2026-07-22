using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;


namespace FluentGpu.VerticalSlice.Harness;

public readonly record struct SuiteEntry(string Id, string Tag, Action<StringTable> Run);

/// <summary>Explicit ordered suite registry — no reflection (AOT-safe).</summary>
public static class SuiteRegistry
{
    public static readonly SuiteEntry[] All =
    [
        new("geolocation", "geolocation", GeolocationSuite.Run),
        new("layout", "layout", LayoutShellSuite.Run),
        new("hooks", "hooks", HooksSuite.Run),
        new("anim", "anim", AnimSuite.Run),
        new("scroll", "scroll", ScrollSuite.Run),
        new("touch", "touch", TouchSuite.Run),
        new("image", "image", ImageSuite.Run),
        new("controls", "controls", ControlsSuite.Run),
        new("nav", "nav", NavSuite.Run),
        new("overlay", "overlay", OverlaySuite.Run),
        new("text", "text", TextSuite.Run),
        new("diagnostics", "diagnostics", DiagnosticsSuite.Run),
    ];

    public static IEnumerable<SuiteEntry> Filter(string? suiteSpec)
    {
        if (string.IsNullOrWhiteSpace(suiteSpec) || suiteSpec.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        var tags = suiteSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("all")) return All;

        var known = new HashSet<string>(All.Select(e => e.Tag), StringComparer.OrdinalIgnoreCase);
        known.Add("core");
        known.Add("all");
        foreach (var tag in set)
        {
            if (!known.Contains(tag))
                throw new ArgumentException("Unknown suite '" + tag + "'. Known: " + KnownSuitesHelp());
        }

        // core is handled by Program (checks 1-9); filter returns matching registry entries only.
        return All.Where(e => set.Contains(e.Tag));
    }

    public static string KnownSuitesHelp() =>
        "core, all, " + string.Join(", ", All.Select(e => e.Tag).Distinct());
}
