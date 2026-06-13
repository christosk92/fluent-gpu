using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentGpu.Foundation;

namespace FluentGpu.Hosting;

/// <summary>
/// A DEBUG-only render-time tripwire — the render-path twin of the allocation tripwire (<c>FG_ALLOC_DIAG</c>). It
/// surfaces the two failure modes that freeze navigation when a component breaks the render-purity contract:
/// <list type="number">
/// <item><b>A slow <c>Render()</c></b> — blocking/expensive work on the synchronous UI-thread render path (the bug
///   class: a card doing blocking COM/registry/network I/O inside <c>Render()</c>, stalling the first paint).</item>
/// <item><b>A component re-rendering every frame</b> — typically <c>UseContext(FrameClock.Tick)</c> abused to poll,
///   which forces a full re-render + relayout each frame at steady state.</item>
/// </list>
/// <para>
/// Cost discipline (matches <c>Diag</c> / validation.md §0): the entire facility is gated by the const
/// <see cref="CompiledIn"/> (<c>false</c> unless <c>DEBUG</c> or <c>FLUENTGPU_DIAG</c> is defined), so every call site
/// in the hot render path folds away in the shipping AOT binary — zero bytes, zero cost. When compiled in, it is
/// further gated at runtime by <c>FG_RENDER_DIAG=1</c>. Output mirrors <c>FG_ALLOC_DIAG</c>: a once-per-second line on
/// <c>Console.Error</c>. "Production safety == CI coverage": the value here is catching the regression in dev/CI, not
/// in the customer's hands.
/// </para>
/// </summary>
public static class RenderBudget
{
    /// <summary>Compile-time master switch — <c>false</c> in release so <c>if (RenderBudget.CompiledIn) { … }</c> guards
    /// in the render path are dead-code-eliminated (the const folds, exactly like <c>Diag.CompiledIn</c>).</summary>
    public const bool CompiledIn =
#if DEBUG || FLUENTGPU_DIAG
        true;
#else
        false;
#endif

    /// <summary>Runtime gate (only consulted when <see cref="CompiledIn"/>): <c>FG_RENDER_DIAG=1</c> turns the tripwire on.</summary>
    public static bool Enabled = CompiledIn && Diag.EnvFlag("FG_RENDER_DIAG");

    /// <summary>A single <c>Render()</c> slower than this (ms) is flagged as a render-path stall.</summary>
    public static double SlowRenderMs = 2.0;

    /// <summary>A component that re-renders for at least this many CONSECUTIVE frames is flagged as an every-frame
    /// re-render (the <c>FrameClock.Tick</c>-poll signature). ~1s at 60fps.</summary>
    public static int StreakFlagFrames = 60;

    private sealed class Entry
    {
        public int RendersThisFrame;
        public long RenderedOnFrame = -1;   // last frame index this type rendered on
        public int Streak;                  // consecutive frames with at least one render
        public int MaxStreak;
        public double MaxMs;
        public double WorstFrameMs;         // worst single-frame total for this type
        public long TotalCalls;
    }

    private static readonly Dictionary<string, Entry> s_byType = new(StringComparer.Ordinal);
    private static long s_frame;
    private static long s_lastReportTicks;
    private static StringBuilder? s_sb;

    /// <summary>Start timing one component's render. Returns the start timestamp (0 when disabled — the caller passes it
    /// straight back to <see cref="End"/>, so a disabled run is a single bool check).</summary>
    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Record one component's render duration (keyed by its runtime type name).</summary>
    public static void End(object component, long startTimestamp)
    {
        if (!Enabled || startTimestamp == 0L) return;
        double ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        string name = component.GetType().Name;
        if (!s_byType.TryGetValue(name, out var e)) { e = new Entry(); s_byType[name] = e; }
        e.RendersThisFrame++;
        e.TotalCalls++;
        if (ms > e.MaxMs) e.MaxMs = ms;
        if (ms > SlowRenderMs)
            Report($"[renderbudget] SLOW Render(): {name} took {ms:0.0}ms (> {SlowRenderMs:0.0}ms budget) — blocking work on the UI-thread render path?");
    }

    /// <summary>Advance the per-frame bookkeeping (host calls this once per frame). Updates each type's consecutive
    /// re-render streak and flags an every-frame offender the first frame it crosses <see cref="StreakFlagFrames"/>.</summary>
    public static void FrameBoundary()
    {
        if (!Enabled) return;
        s_frame++;
        foreach (var kv in s_byType)
        {
            var e = kv.Value;
            double frameMs = 0;
            if (e.RendersThisFrame > 0)
            {
                e.Streak = (e.RenderedOnFrame == s_frame - 1) ? e.Streak + 1 : 1;
                e.RenderedOnFrame = s_frame;
                if (e.Streak > e.MaxStreak)
                {
                    e.MaxStreak = e.Streak;
                    if (e.Streak == StreakFlagFrames)
                        Report($"[renderbudget] EVERY-FRAME re-render: {kv.Key} has re-rendered {e.Streak} consecutive frames — UseContext(FrameClock.Tick) used to poll? Prefer UsePost()/a binding.");
                }
                frameMs = e.MaxMs;   // coarse: the worst single render observed (per-frame sum not tracked to stay cheap)
                if (frameMs > e.WorstFrameMs) e.WorstFrameMs = frameMs;
                e.RendersThisFrame = 0;
            }
            else
            {
                e.Streak = 0;   // a quiet frame breaks the streak
            }
        }
    }

    /// <summary>Once-per-second summary of the worst offenders (host calls this each frame; it self-throttles).</summary>
    public static void MaybeReport()
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        if (s_lastReportTicks != 0 && now - s_lastReportTicks < Stopwatch.Frequency) return;
        s_lastReportTicks = now;

        s_sb ??= new StringBuilder();
        s_sb.Clear();
        s_sb.Append("[renderbudget]");
        bool any = false;
        foreach (var kv in s_byType)
        {
            var e = kv.Value;
            if (e.MaxStreak >= StreakFlagFrames || e.MaxMs >= SlowRenderMs)
            {
                any = true;
                s_sb.Append(CultureInfo.InvariantCulture, $" | {kv.Key} maxStreak={e.MaxStreak} maxMs={e.MaxMs:0.0}");
            }
        }
        if (any) Report(s_sb.ToString());
    }

    /// <summary>Test/gate accessor: the largest consecutive-render streak observed for <paramref name="typeName"/>
    /// (0 if never seen). The render-budget self-check asserts a well-behaved component stays low and the
    /// <c>FrameClock.Tick</c> offender crosses <see cref="StreakFlagFrames"/>.</summary>
    public static int MaxStreakOf(string typeName) => s_byType.TryGetValue(typeName, out var e) ? e.MaxStreak : 0;

    /// <summary>Test/gate accessor: the slowest single <c>Render()</c> observed for <paramref name="typeName"/> (ms).</summary>
    public static double MaxMsOf(string typeName) => s_byType.TryGetValue(typeName, out var e) ? e.MaxMs : 0.0;

    /// <summary>Reset all accumulators (between gate scenarios).</summary>
    public static void Reset() { s_byType.Clear(); s_frame = 0; s_lastReportTicks = 0; }

    private static void Report(string line)
    {
        if (Diag.Sink is { } sink) sink(line);
        else Console.Error.WriteLine(line);
    }
}
