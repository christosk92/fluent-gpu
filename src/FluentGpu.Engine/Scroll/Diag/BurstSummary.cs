using System.Globalization;

namespace FluentGpu.Scroll.Diag;

/// <summary>One-word verdict for a <see cref="BurstSummary"/>, worst-condition-wins ordering: a clamp is reported
/// even if the burst also happened to be late, because a clamp is the more actionable defect. See
/// <see cref="ScrollProbe.EndBurst"/> for the exact priority.</summary>
public enum ScrollVerdict : byte
{
    /// <summary>No clamps, no jumps, no late presents, and jitter stayed under
    /// <see cref="ScrollProbe.JitterUnevenThreshold"/>.</summary>
    Smooth = 0,
    /// <summary>At least one posed frame was clamped against the viewport's coverage bounds (the plan asked to go
    /// somewhere the virtualizer hadn't realized yet).</summary>
    Clamped = 1,
    /// <summary>At least one measured-extent correction landed a frame late (a visible content jump).</summary>
    Jumped = 2,
    /// <summary>At least one present-to-present gap exceeded 1.5× the burst's median gap.</summary>
    Late = 3,
    /// <summary>No clamp/jump/late-present condition, but the pose stream's jitter exceeded the threshold anyway.</summary>
    Uneven = 4,
}

/// <summary>Aggregate metrics for one measurement burst — everything <see cref="ScrollProbe.EndBurst"/> computes
/// from the probe rings since the previous call. The positional fields are the summary-level numbers; the two
/// per-<see cref="ScrollCostPhase"/> breakdown arrays are additional detail for a Trace-level reader.</summary>
public readonly record struct BurstSummary(
    long Qpc,
    int Notches,
    int Presents,
    int CoverageClamps,
    int ExtentJumps,
    double MaxJitter,
    double AvgJitter,
    int LatePresents,
    double MaxCostMs,
    double AvgCostMs,
    ScrollVerdict Verdict)
{
    /// <summary>Max cost (ms) per <see cref="ScrollCostPhase"/> ordinal, indexed 0..<c>ScrollCostPhase.Count-1</c>.
    /// Always exactly <c>(int)ScrollCostPhase.Count</c> long.</summary>
    public double[] MaxCostMsByPhase { get; init; } = System.Array.Empty<double>();

    /// <summary>Average cost (ms) per <see cref="ScrollCostPhase"/> ordinal — same indexing as
    /// <see cref="MaxCostMsByPhase"/>.</summary>
    public double[] AvgCostMsByPhase { get; init; } = System.Array.Empty<double>();

    /// <summary>Most tiles one composite turn rastered in the burst (<see cref="ScrollCostPhase.TileRaster"/> rows).</summary>
    public int TilesPerFrameMax { get; init; }

    /// <summary>Mean tiles rastered per composite turn in the burst.</summary>
    public double TilesPerFrameAvg { get; init; }

    /// <summary>Visible tiles that composited nothing, summed over the burst's turns — must be 0 (a blank band).</summary>
    public int ExposedTileMissing { get; init; }

    /// <summary>The one-line human summary (allocates — off the hot path by construction, since
    /// <see cref="ScrollProbe.EndBurst"/> itself is not a per-frame call).</summary>
    public string FormatLine()
    {
        var ci = CultureInfo.InvariantCulture;
        return string.Format(ci,
            "burst qpc={0} notches={1} presents={2} clamps={3} jumps={4} late={5} jitter(avg/max)={6:0.###}/{7:0.###} costMs(avg/max)={8:0.###}/{9:0.###} tiles(avg/max)={11:0.##}/{12} exposedMissing={13} verdict={10}",
            Qpc, Notches, Presents, CoverageClamps, ExtentJumps, LatePresents, AvgJitter, MaxJitter, AvgCostMs, MaxCostMs, Verdict,
            TilesPerFrameAvg, TilesPerFrameMax, ExposedTileMissing);
    }
}
