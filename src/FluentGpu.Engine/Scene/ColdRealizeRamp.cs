using System;

namespace FluentGpu.Scene;

/// <summary>
/// The PURE cold-mount realization-ramp decision: how many rows of a freshly-mounted virtual window may be
/// MATERIALIZED in one frame, and when the ramp is finished. Math only — no scene, no reconciler, no host, no clock —
/// so the whole progression is decided in one testable place instead of inside the realize walk
/// (<c>gate.ramp.*</c> in <c>ScrollSuite</c> is its unit-test table).
///
/// <para><b>Why a ramp exists at all.</b> Virtualization bounds how many rows are ALIVE, not how much a row costs to
/// build. A heavy row (Wavee's track row: ~90 scene nodes and ~50 bound channels) turns a 30-row screenful into a
/// 2 700-node / 1 800-bind single flush — measured 72 ms of flush, nine 8.3 ms frame budgets, on a detail page's first
/// content mount, with every other frame of the same navigation comfortably inside budget. The window is right; the
/// FILL RATE is what has to be bounded. So a cold window is realized as a growing contiguous PREFIX: each frame adds
/// what fits, the viewport publishes only what it actually built, and the untouched tail keeps the extent the measured
/// layout already reserved (so nothing jumps and no row is ever published blank).</para>
///
/// <para><b>Two budgets, both measured, whichever is smaller.</b> A NODE budget (<see cref="NodeBudget"/>) is the
/// portable one: it is what the very first — necessarily unmeasured — grow charges against, one row at a time via
/// <see cref="CanCreateAnother"/>, so a light row still realizes its whole window in the mount frame (byte-identical
/// to the pre-ramp path) while a heavy row stops after a handful. A TIME budget
/// (<see cref="FrameBudgetMs"/> × <see cref="GrowShareOfFrame"/>) then takes over from the previous grow's measured
/// cost, because nodes are only a proxy: the same row costs ~2.4 ms on a process's first content mount and ~0.2 ms
/// once the page's code paths are warm, and the ramp should accelerate as that happens instead of holding a fixed
/// row count. The share is deliberately well under 1: the realize walk is not the whole frame — the mount's bound
/// channels are flushed AFTER it (the reactive flush), then layout, record and submit.</para>
///
/// <para><b>Progress is never zero.</b> <see cref="Target"/> always grows by at least one row when the window is
/// short, so a pathologically expensive row still converges rather than wedging a half-built page; and a caller that
/// must not present a short visible band passes a <c>visibleFloor</c>, which overrides the budget outright (the
/// anti-flicker invariant: a MOVING viewport gets whole rows, and the ramp only ever finishes the halo).</para>
/// </summary>
public static class ColdRealizeRamp
{
    /// <summary>Fresh scene nodes one frame's grow may mount before it stops adding rows. Sized to a screenful of
    /// ordinary rows rather than to a heavy one: the first grow of a light list clears its whole window under this
    /// budget in a single frame (no ramp at all), and a heavy list stops at ~6-7 rows.</summary>
    public const int NodeBudget = 600;
    /// <summary>The frame budget the ramp is sized against (120 Hz-friendly 8.3 ms — the same number the app's own
    /// slow-frame watch uses).</summary>
    public const float FrameBudgetMs = 8.3f;
    /// <summary>The share of <see cref="FrameBudgetMs"/> the realize WALK may spend. The rest of the frame still has
    /// to flush the freshly-mounted binds, lay out, record and submit — and only the walk is measurable from inside
    /// the realizer, so the share is what keeps the whole frame under budget rather than just its realize phase.</summary>
    public const float GrowShareOfFrame = 0.4f;
    /// <summary>A ramp must always progress: one row per frame is the floor even when a single row overruns the
    /// whole budget (a wedged half-built page is strictly worse than one long frame).</summary>
    public const int MinRowsPerFrame = 1;
    /// <summary>Ceiling on a single frame's grow, so an unmeasured/degenerate cost estimate cannot ask for an
    /// unbounded batch. Far above any real viewport's window.</summary>
    public const int MaxRowsPerFrame = 512;

    /// <summary>The realize walk's slice of <see cref="FrameBudgetMs"/>.</summary>
    public static float GrowBudgetMs => FrameBudgetMs * GrowShareOfFrame;

    /// <summary>The FIRST (unmeasured) grow's stop rule, charged one row at a time as rows are built: always allow
    /// the first row, then keep going while this grow's freshly-mounted node count is still under
    /// <see cref="NodeBudget"/>. Self-calibrating — it needs no per-row estimate, which is exactly what a cold
    /// viewport does not have.</summary>
    public static bool CanCreateAnother(int rowsCreated, int nodesMounted)
        => rowsCreated <= 0 || nodesMounted < NodeBudget;

    /// <summary>Fold this grow's observed node cost into the viewport's per-row estimate. Rounds UP and keeps the
    /// PESSIMISTIC side of the blend, because an under-estimate spends a frame it does not have while an
    /// over-estimate merely costs one extra ramp frame.</summary>
    public static int MeasureNodesPerRow(int nodesMounted, int rowsCreated, int previous)
    {
        if (rowsCreated <= 0 || nodesMounted <= 0) return previous;
        int now = (nodesMounted + rowsCreated - 1) / rowsCreated;
        if (previous <= 0) return now;
        return Math.Max(now, (previous + now + 1) / 2);
    }

    /// <summary>Fold this grow's observed wall cost into the viewport's per-row estimate (same pessimistic blend as
    /// <see cref="MeasureNodesPerRow"/>). This is the term that lets the ramp accelerate as a page's code paths warm
    /// up — a row that cost 2.4 ms on the process's first content mount costs ~0.2 ms later.</summary>
    public static float MeasureMsPerRow(float elapsedMs, int rowsCreated, float previous)
    {
        if (rowsCreated <= 0 || !(elapsedMs > 0f)) return previous;
        float now = elapsedMs / rowsCreated;
        if (!(previous > 0f)) return now;
        return MathF.Max(now, (previous + now) * 0.5f);
    }

    /// <summary>What ONE row is assumed to cost before anything has measured one. A viewport's first grow is the one
    /// the ramp most needs to bound and the one it knows least about — and for a page's track list there is no second
    /// grow to correct it, because the visible band mounts once. Charging the node budget row-by-row during the walk
    /// (<see cref="CanCreateAnother"/>) does not save it either: when the row's content is a COMPONENT the walk mounts
    /// a single node and the ~90 real ones arrive in the flush that mount schedules, so the walk reads a heavy row as
    /// free. So an unmeasured row is assumed to be an ordinary app row rather than assumed free — about a hundred
    /// nodes, which is what Wavee's track row and sidebar row both measure. An over-estimate costs a light list one
    /// extra ramp frame and is then blended away within a few grows; the under-estimate it replaces cost every cold
    /// navigation a 20 ms frame.</summary>
    public const int UnmeasuredNodesPerRow = 100;

    /// <summary>Rows one frame's grow may add, from the measured per-row cost: the SMALLER of the node budget and the
    /// time budget, clamped to <see cref="MinRowsPerFrame"/>..<see cref="MaxRowsPerFrame"/>. An unmeasured term
    /// (≤ 0) does not constrain — the other one, or the ceiling, decides.</summary>
    public static int RowsPerFrame(int nodesPerRow, float msPerRow)
    {
        int byNodes = nodesPerRow > 0 ? NodeBudget / nodesPerRow : MaxRowsPerFrame;
        int byTime = msPerRow > 0f ? (int)(GrowBudgetMs / msPerRow) : MaxRowsPerFrame;
        return Math.Clamp(Math.Min(byNodes, byTime), MinRowsPerFrame, MaxRowsPerFrame);
    }

    /// <summary>The whole per-frame decision: the slot count this viewport may hold after this frame's grow.
    /// <paramref name="realized"/> is what it holds now, <paramref name="window"/> the full desired window,
    /// <paramref name="grewThisFrame"/> true when this viewport already grew in this frame (the realize walk runs
    /// several times per paint — the spread must be per FRAME, not per call), and <paramref name="visibleFloor"/> a
    /// row count the caller refuses to go below whatever the budget says (0 = the budget decides alone).
    /// Never returns less than <paramref name="realized"/>: a ramp only ever grows.</summary>
    public static int Target(int realized, int window, int nodesPerRow, float msPerRow,
                            bool grewThisFrame, int visibleFloor)
    {
        if (window <= 0) return 0;
        if (realized >= window) return window;
        int target = grewThisFrame ? realized : realized + RowsPerFrame(nodesPerRow, msPerRow);
        if (target < visibleFloor) target = visibleFloor;
        if (target <= realized && !grewThisFrame) target = realized + MinRowsPerFrame;
        return Math.Min(window, Math.Max(realized, target));
    }

    /// <summary>Is the ramp still owed rows after a grow that landed <paramref name="materialized"/> of
    /// <paramref name="window"/>? (The viewport stays <c>VirtualRangeDirty</c> and the host stays awake while true.)</summary>
    public static bool Warming(int materialized, int window) => materialized < window;
}
