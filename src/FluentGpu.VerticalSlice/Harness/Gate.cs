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

/// <summary>Shared check counter + arena summary for the headless golden harness.</summary>
public static class Gate
{
    public static int Failures;
    public static int Total;
    public static readonly Context<int> NumCtx = new(0);

    // Phase-3 (GestureArena) headline-gate evidence for the final summary line.
    public static string? s_arenaDeterminism, s_arenaIntegratorSweep, s_arenaFastpath, s_arenaAllocZero, s_arenaDispatchAllocZero;

    public static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
        Total++;
        if (!ok) Failures++;
    }

    public static string ArenaSummarySuffix()
    {
        if (s_arenaDeterminism is null && s_arenaIntegratorSweep is null && s_arenaFastpath is null && s_arenaAllocZero is null && s_arenaDispatchAllocZero is null)
            return "";
        return $" ({Total} checks; gate.arena.determinism PASS [{s_arenaDeterminism}], "
             + $"gate.arena.determinism.integrator-sweep PASS [{s_arenaIntegratorSweep}], "
             + $"gate.arena.fastpath-sync PASS [{s_arenaFastpath}], "
             + $"gate.arena.alloc-zero PASS [{s_arenaAllocZero}], "
             + $"gate.arena.dispatch-alloc-zero PASS [{s_arenaDispatchAllocZero}].)";
    }
}
