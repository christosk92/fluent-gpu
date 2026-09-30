using System;
using System.Collections.Generic;
using FluentGpu.Render.Evidence;

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>
/// KNOWN-FAILING evidence gates (docs/plans/evidence-diagnostics-implementation.md §0/§D): a failing-first reproduction of
/// an open issue whose fix is deliberately NOT part of the change that adds it — the gate is the issue's evidence and its
/// future regression pin. Its condition is the full, unweakened contract; only its REPORTING differs from
/// <see cref="Gate.Check"/>:
/// <list type="bullet">
/// <item>while it fails it prints <c>[OPEN]</c> with its detail and the issue it pins, is listed in the run's final
/// summary, and does not count as a failure (the run can still read ALL CHECKS PASSED for everything else);</item>
/// <item>the moment it PASSES it is a FAILURE (<c>[FAIL] … known-failing gate passed</c>): the fix landed, so the gate
/// must be promoted to a plain <see cref="Gate.Check"/> in the same change — it can never silently stay marked open.</item>
/// </list>
/// Stale tiles an open gate's own scene produces are its evidence, not the permanent sweep's (<see cref="AcknowledgeStale"/>).
/// </summary>
public static class EvidenceGate
{
    private static readonly List<string> s_open = new();

    /// <summary>Stale-tile turns the open gates' scenes produced (the sweep subtracts them).</summary>
    public static long AcknowledgedStaleTurns { get; private set; }

    /// <summary>The open (still failing) evidence gates of this run, in order: "name — issue".</summary>
    public static IReadOnlyList<string> Open => s_open;

    public static void KnownFailing(string name, bool ok, string detail, string issue)
    {
        if (ok)
        {
            Gate.Check(name + " — this KNOWN-FAILING evidence gate PASSED: the fix for " + issue + " landed; promote it to a plain Check",
                false, detail);
            return;
        }
        Console.WriteLine($"  [OPEN] {name}  ({detail})  — known-failing evidence gate for {issue}: fails until its fix");
        string gate = name.Split(' ')[0];
        s_open.Add(gate + " — " + issue);
    }

    /// <summary>An open gate's scene ended <paramref name="turns"/> composite turns with stale tiles on purpose.</summary>
    public static void AcknowledgeStale(long turns)
    {
        if (turns > 0) AcknowledgedStaleTurns += turns;
    }

    /// <summary>The final summary's suffix naming the open gates ("" when none).</summary>
    public static string SummarySuffix()
        => s_open.Count == 0 ? "" : $" [{s_open.Count} open evidence gate(s): {string.Join("; ", s_open)}]";
}

/// <summary>
/// The permanent stale-tile sweep (<c>gate.tiles.stale-zero</c>, evidence-diagnostics §A.1): no composite turn of ANY
/// suite may end with a stale tile — a valid tile whose pixels the current stream no longer describes. Every host's
/// composite turn tallies on the process-wide <see cref="TileInvariants"/>; the runner calls <see cref="Begin"/> /
/// <see cref="End"/> around each suite and the sweep fails the suite that produced one (minus the turns an open
/// evidence gate acknowledged as its own evidence), naming the offender.
/// </summary>
public static class StaleSweep
{
    private static long s_turns0, s_ack0;

    public static void Begin()
    {
        s_turns0 = TileInvariants.StaleTurns;
        s_ack0 = EvidenceGate.AcknowledgedStaleTurns;
    }

    public static void End(string suite)
    {
        long turns = TileInvariants.StaleTurns - s_turns0;
        long ack = EvidenceGate.AcknowledgedStaleTurns - s_ack0;
        long unexplained = turns - ack;
        var s = TileInvariants.LastStale;
        Gate.Check($"gate.tiles.stale-zero [{suite}] no composite turn ended with a stale tile (a valid tile whose raster hash differs from the content the stream now wants)",
            unexplained <= 0,
            unexplained <= 0 ? $"staleTurns={turns} acknowledgedByOpenGates={ack}"
                : $"staleTurns={turns} acknowledged={ack} last=(frame {s.Frame} slice {s.SliceId} node {s.NodeIndex}:{s.Gen} tile {s.Tx},{s.Ty} want {s.Want:x16} have {s.Have:x16} rasterFrame {s.RasterFrame})");
    }
}
