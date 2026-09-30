using System;
using System.Diagnostics;
using System.Threading;

namespace FluentGpu.Scroll.Diag;

/// <summary>Device/gesture-source code for <see cref="ScrollProbe.Input"/> (scroll-rework design B.9). Mirrors
/// <c>FluentGpu.Scroll.ScrollSource</c> (design B.2) byte-for-byte on purpose, but is declared independently: the
/// diagnostics layer must not carry a compile dependency on the input/motion assemblies landing in the same wave.
/// If B.2's <c>ScrollSource</c> ever reorders its members, callers cast through this enum's own values, not the
/// other one's — the two are kept in sync by convention, not by reference.</summary>
public enum ScrollSourceCode : byte
{
    MouseWheel = 0,
    MouseWheelHiRes = 1,
    Touchpad = 2,
    Touch = 3,
    Pen = 4,
    Keyboard = 5,
    Thumb = 6,
    Programmatic = 7,
}

/// <summary>Frame-loop phase tag for <see cref="ScrollProbe.Cost"/>. Coarse-grained on purpose — this is a cost
/// *attribution* axis for the burst summary, not a full phase enumeration.</summary>
public enum ScrollCostPhase : byte
{
    Input = 0,
    Plan = 1,
    Virtualize = 2,
    Layout = 3,
    Poser = 4,
    Record = 5,
    /// <summary>Render thread: the backend's tile raster + composite submit for one turn (<see cref="ScrollProbe.RenderCost"/>:
    /// rows = tiles rastered, nodes = their surface KiB).</summary>
    TileRaster = 6,
    /// <summary>Render thread: building the turn's composite plan — slice table flow, needed sets, items (rows = composite
    /// items, nodes = visible tiles that composited nothing, which must be 0).</summary>
    Composite = 7,

    /// <summary>Not a real phase — the array-sizing sentinel. Keep last.</summary>
    Count = 8,
}

/// <summary>What moved a viewport's plan frame on an <see cref="ScrollProbe.Extent"/> row (stored in its <c>B1</c>).</summary>
public enum ProbeExtentCause : byte
{
    /// <summary>One row's measured-extent correction above the anchor (<c>Virtualizer.ApplyMeasured</c>; index = the row).</summary>
    Measured = 0,
    /// <summary>The frame's summed measured shift, applied to the frame's offset by layout (<c>FlexLayout.ArrangeVirtual</c>;
    /// index = the anchor row).</summary>
    FrameShift = 1,
    /// <summary>A structural rebase above the first visible row (<c>ScrollHandle.ShiftFrame</c> — a reorder, an insert).</summary>
    Structural = 2,
}

/// <summary>Byte codes for <see cref="ScrollProbe.Mark"/> — freeform markers with NO string payload (a string arg
/// on a hot-path record call would allocate/box; every other <c>Record*</c> method is pure value types for the same
/// reason). Register a new code here in the same change that adds its emitter.</summary>
public enum ProbeMark : byte
{
    None = 0,
    /// <summary>A burst boundary was drawn (i.e. <see cref="ScrollProbe.EndBurst"/> was called) — useful as a fence
    /// post inside a Trace-level CSV export.</summary>
    BurstBoundary = 1,
    /// <summary>A named feel profile was applied via <c>ScrollTunables.ApplyProfile</c>.</summary>
    ProfileApplied = 2,
    /// <summary>A single tunable was changed via <c>ScrollTunables.Apply</c> (not through a named profile).</summary>
    TunableChanged = 3,
    /// <summary>The user flagged "that felt wrong" (Scroll Lab F8) — analysis attaches it to the nearest worst sample.</summary>
    UserFelt = 4,
    /// <summary>An A/B feel flip (Scroll Lab F9).</summary>
    AbFlip = 5,
    /// <summary>An A/B vote was cast (Scroll Lab 1/2/0).</summary>
    AbVote = 6,
    /// <summary>A recording session began (the fence a session's analysis window starts at).</summary>
    SessionStart = 7,
    /// <summary>A recording session ended.</summary>
    SessionEnd = 8,
}

/// <summary>
/// Scroll-rework diagnostics (scroll-rework design B.9): two fixed-capacity POD rings recording every stage of the
/// scroll pipeline — wheel/touch input, the immutable motion plan, the render-thread pose, virtualizer coverage,
/// measured-extent corrections, and per-phase cost — plus a rolling <see cref="BurstSummary"/> and a CSV export
/// compatible with <c>wheel-curve-probe/analyze.py</c> (pane <c>"Wavee"</c>).
///
/// THREAD RULE: <see cref="Pose"/> is called ONLY from the render thread (it is the render ring's sole producer).
/// Every other <c>Record*</c> method (<see cref="Input"/>, <see cref="Plan"/>, <see cref="Coverage"/>,
/// <see cref="Extent"/>, <see cref="Cost"/>, <see cref="Mark"/>) is called ONLY from the UI thread (the UI ring's
/// sole producer). Each ring is therefore genuinely single-producer and needs no interlocked increment on the write
/// path — only a <see cref="Volatile"/> publish of the write count, so a reader on any other thread (
/// <see cref="EndBurst"/>, <see cref="ExportCsv"/>) observes a consistent prefix. <see cref="Level"/> may be read or
/// written from any thread (a plain volatile byte).
///
/// COST MODEL: at <see cref="ProbeLevel.Off"/> every record method is one volatile byte load and a branch — no ring
/// write, no allocation, ever. At <see cref="ProbeLevel.Summary"/>, methods that Off/Trace already gate further
/// filter to just the rows the summary and the session metrics consume (e.g. <see cref="Input"/> stores notches and
/// contact-stream reports, not the rest).
/// <see cref="ProbeLevel.Trace"/> stores everything. No method allocates on its record path; <see cref="EndBurst"/>
/// and <see cref="ExportCsv"/> are explicitly off the hot path and may allocate.
/// </summary>
public static partial class ScrollProbe
{
    /// <summary>Byte code for <see cref="Input"/>'s <c>phase</c> parameter that identifies a discrete wheel notch
    /// (mirrors <c>FluentGpu.Scroll.ScrollGesture.Notch</c>'s ordinal from design B.2 — kept as a local constant
    /// rather than a reference so this assembly has no compile dependency on that enum). <see cref="ProbeLevel.Summary"/>
    /// keeps the <see cref="Input"/> rows carrying this phase, plus contact-stream rows.</summary>
    public const byte PhaseNotch = 3;

    private const int RingCapacity = 8192;   // power of two: index wraps with a mask, never a modulo
    private const int RingMask = RingCapacity - 1;

    private enum RecKind : byte { Input = 0, Plan = 1, Pose = 2, Coverage = 3, Extent = 4, Cost = 5, Mark = 6 }

    /// <summary>One fixed-size POD slot big enough to hold any <c>Record*</c> call's payload. Field meaning depends
    /// on <see cref="Kind"/> — documented on each emitting method, not here, to avoid the two ever drifting apart.</summary>
    private struct ProbeRecord
    {
        public byte Kind;      // RecKind
        public byte B0;        // source | plan-kind | clamped-flag | anchored-flag | cost-phase | mark-code
        public byte B1;        // Input: phase | Extent: ProbeExtentCause
        public int Vp;
        public int I0;         // Input: release | Coverage: first | Extent: index | Cost: rows
        public int I1;         // Coverage: last  | Cost: nodes
        public long Qpc;       // event qpc, OR (Cost) the elapsed ticks being measured
        public double D0;      // Plan: start | Pose: pos       | Coverage: start  | Extent: delta
        public double D1;      // Plan: dest  | Pose: vel       | Coverage: end
        public double D2;      // Plan: durationS | Pose: plan pos (pre-clamp) | Coverage: origin
        public float F0;       // Input: dx | Pose: snappedTrans
        public float F1;       // Input: dy
        public ulong Seq;      // Plan: seq
    }

    private static byte s_level;   // ProbeLevel, backed by a volatile byte per B.9 ("volatile ProbeLevel Level")

    private static readonly ProbeRecord[] s_uiRing = new ProbeRecord[RingCapacity];
    private static long s_uiCount;   // total UI-ring writes ever (monotonic; index = count & RingMask)

    private static readonly ProbeRecord[] s_renderRing = new ProbeRecord[RingCapacity];
    private static long s_renderCount;   // total render-ring writes ever

    private static long s_lastBurstUiCount;
    private static long s_lastBurstRenderCount;

    /// <summary>Current diagnostics granularity. Set from the Diagnostics page; no <c>#if</c>, no environment
    /// variable — always compiled in, in every build.</summary>
    public static ProbeLevel Level
    {
        get => (ProbeLevel)Volatile.Read(ref s_level);
        set => Volatile.Write(ref s_level, (byte)value);
    }

    private static void PushUi(in ProbeRecord r)
    {
        long idx = s_uiCount;   // UI thread is the ring's sole producer — plain read is fine
        s_uiRing[(int)(idx & RingMask)] = r;
        Volatile.Write(ref s_uiCount, idx + 1);   // release: publishes the slot write above before the count is visible
    }

    private static void PushRender(in ProbeRecord r)
    {
        long idx = s_renderCount;   // render thread is the ring's sole producer
        s_renderRing[(int)(idx & RingMask)] = r;
        Volatile.Write(ref s_renderCount, idx + 1);
    }

    // ── record methods (UI thread unless noted) ─────────────────────────────────────────────────────────────────

    /// <summary>A raw scroll input sample or discrete notch: <paramref name="phase"/> is the gesture-phase byte code
    /// (<see cref="PhaseNotch"/> for a wheel notch; other values are producer-defined). <paramref name="dx"/>/
    /// <paramref name="dy"/> are DIP for a sample, notch units for a notch. Recorded at Trace always; at Summary for a
    /// notch (the burst summary's notch count) and for every CONTACT-stream event (<see cref="ScrollSourceCode.Touchpad"/>/
    /// <see cref="ScrollSourceCode.Touch"/>/<see cref="ScrollSourceCode.Pen"/> Begin/Sample/End): a contact's tracking
    /// metrics (finger → pose lag, back-steps while the finger advances, the lift) are defined against the finger's own
    /// stream, so a Summary session without it cannot score a touchpad gesture at all. One row per report — the same
    /// order as the pose rows Summary already keeps. <paramref name="release"/> is a contact End's release verdict byte
    /// (<c>ContactRelease</c>: 0 unknown, 1 moving, 2 stopped — kept generic like <paramref name="phase"/>), 0 otherwise.</summary>
    public static void Input(ScrollSourceCode source, byte phase, long qpc, float dx, float dy, int vp, byte release = 0)
    {
        ProbeLevel lvl = Level;
        if (lvl == ProbeLevel.Off) return;
        if (lvl == ProbeLevel.Summary && phase != PhaseNotch
            && source is not (ScrollSourceCode.Touchpad or ScrollSourceCode.Touch or ScrollSourceCode.Pen)) return;
        PushUi(new ProbeRecord { Kind = (byte)RecKind.Input, B0 = (byte)source, B1 = phase, I0 = release, Qpc = qpc, F0 = dx, F1 = dy, Vp = vp });
    }

    /// <summary>A new/replanned <c>ScrollPlan</c> published for a viewport: <paramref name="kind"/> is the plan's
    /// <c>MotionKind</c>/<c>SegKind</c> byte value (kept generic here — the diagnostics layer does not reference
    /// <c>FluentGpu.Scroll.Motion</c>). Trace-only: the burst summary does not consume plan events.</summary>
    public static void Plan(int vp, ulong seq, byte kind, long qpc, double start, double dest, double durationS)
    {
        if (Level != ProbeLevel.Trace) return;
        PushUi(new ProbeRecord { Kind = (byte)RecKind.Plan, Vp = vp, Seq = seq, B0 = kind, Qpc = qpc, D0 = start, D1 = dest, D2 = durationS });
    }

    /// <summary>RENDER THREAD ONLY. One posed frame: <paramref name="pos"/>/<paramref name="vel"/> are the plan's
    /// evaluated position/velocity at <paramref name="presentQpc"/>; <paramref name="clamped"/> is true when the
    /// poser clamped the position against the viewport's coverage bounds (feeds <see cref="BurstSummary.CoverageClamps"/>);
    /// <paramref name="snappedTrans"/> is the device-pixel-snapped transform actually painted; <paramref name="planPos"/> is
    /// the plan's own position before the coverage clamp (same frame as <paramref name="pos"/>; NaN = <paramref name="pos"/>),
    /// so a clamped row shows how far the plan ran ahead of what was realized. Recorded at both
    /// Summary and Trace — every metric in <see cref="BurstSummary"/> that isn't notch/extent/cost derived comes
    /// from this stream.</summary>
    public static void Pose(int vp, long presentQpc, double pos, double vel, bool clamped, float snappedTrans, double planPos = double.NaN)
    {
        if (clamped) NoteClampedPose(vp, presentQpc, pos, double.IsNaN(planPos) ? pos : planPos);
        if (Level == ProbeLevel.Off) return;
        PushRender(new ProbeRecord
        {
            Kind = (byte)RecKind.Pose, Vp = vp, Qpc = presentQpc, D0 = pos, D1 = vel,
            D2 = double.IsNaN(planPos) ? pos : planPos,
            B0 = (byte)(clamped ? 1 : 0), F0 = snappedTrans,
        });
    }

    // ── always-on clamp counter (2026-09-25, item G) ──────────────────────────────────────────────────────────────────
    // Every render-poser pose the coverage clamp moved, counted WHATEVER the probe level (the rows above need Summary),
    // with the last one's viewport / present / shown / plan position: what an app's auto evidence bundle triggers on (the
    // first clamp of a burst) before the trace ring rolls. Render thread writes; any thread reads (torn-free longs).
    private static long s_clampedPoses, s_lastClampQpc, s_lastClampShownBits, s_lastClampPlanBits;
    private static int s_lastClampVp;

    /// <summary>Cumulative count of clamped render poses (always on). A reader differences it.</summary>
    public static long ClampedPoses => Volatile.Read(ref s_clampedPoses);

    /// <summary>The most recent clamped pose: viewport node, present QPC, the shown (clamped) and the plan position
    /// (content DIP, the plan's frame).</summary>
    public static (int Vp, long PresentQpc, double Shown, double Plan) LastClamp
        => (Volatile.Read(ref s_lastClampVp), Volatile.Read(ref s_lastClampQpc),
            BitConverter.Int64BitsToDouble(Volatile.Read(ref s_lastClampShownBits)),
            BitConverter.Int64BitsToDouble(Volatile.Read(ref s_lastClampPlanBits)));

    /// <summary>RENDER THREAD. Count one clamped pose (called by <see cref="Pose"/>, and by the render poser whatever the
    /// level). Zero allocation.</summary>
    public static void NoteClampedPose(int vp, long presentQpc, double shown, double plan)
    {
        Volatile.Write(ref s_lastClampVp, vp);
        Volatile.Write(ref s_lastClampQpc, presentQpc);
        Volatile.Write(ref s_lastClampShownBits, BitConverter.DoubleToInt64Bits(shown));
        Volatile.Write(ref s_lastClampPlanBits, BitConverter.DoubleToInt64Bits(plan));
        Interlocked.Increment(ref s_clampedPoses);
    }

    /// <summary>The coverage a viewport was published with — exactly the row the posers clamp against
    /// (<c>ScrollCoverageRow</c>: <paramref name="start"/>/<paramref name="end"/> in content DIP, the arrange
    /// <paramref name="origin"/>, the realized <paramref name="first"/>/<paramref name="last"/> items); the host records one
    /// row per viewport whenever that coverage CHANGES. <paramref name="qpc"/> 0 = stamp now. Trace-only (a debugging aid
    /// for the virtualizer, not a burst-summary input — the summary's clamp signal comes from <see cref="Pose"/>'s
    /// own <c>clamped</c> flag, which is the value that actually reached the screen).</summary>
    public static void Coverage(int vp, long qpc, double start, double end, double origin, int first, int last)
    {
        if (Level != ProbeLevel.Trace) return;
        PushUi(new ProbeRecord
        {
            Kind = (byte)RecKind.Coverage, Vp = vp, Qpc = qpc != 0 ? qpc : Stopwatch.GetTimestamp(),
            D0 = start, D1 = end, D2 = origin, I0 = first, I1 = last,
        });
    }

    /// <summary>A measured-extent correction at <paramref name="index"/> shifting the coordinate frame by
    /// <paramref name="delta"/>; <paramref name="anchoredSameFrame"/> is true when the correction was folded into
    /// the SAME frame's anchor shift (invisible to the user) and false when it landed a frame late (a visible
    /// "jump" — feeds <see cref="BurstSummary.ExtentJumps"/>). <paramref name="cause"/> names what moved the frame
    /// (<see cref="ProbeExtentCause"/>). <paramref name="qpc"/> 0 = stamp now. At Summary, only non-anchored (jump) rows
    /// are kept.</summary>
    public static void Extent(int vp, long qpc, int index, double delta, bool anchoredSameFrame,
                              ProbeExtentCause cause = ProbeExtentCause.Measured)
    {
        ProbeLevel lvl = Level;
        if (lvl == ProbeLevel.Off) return;
        if (lvl == ProbeLevel.Summary && anchoredSameFrame) return;
        PushUi(new ProbeRecord
        {
            Kind = (byte)RecKind.Extent, Vp = vp, Qpc = qpc != 0 ? qpc : Stopwatch.GetTimestamp(), I0 = index, D0 = delta,
            B0 = (byte)(anchoredSameFrame ? 1 : 0), B1 = (byte)cause,
        });
    }

    /// <summary>Per-phase cost sample: <paramref name="qpcTicks"/> is the ELAPSED ticks spent in <paramref name="phase"/>
    /// this frame (a duration, not a timestamp) for <paramref name="rows"/> rows / <paramref name="nodes"/> nodes
    /// touched. Recorded at both Summary and Trace — feeds <see cref="BurstSummary.MaxCostMs"/>/<c>AvgCostMs</c>
    /// (overall and per-<see cref="ScrollCostPhase"/>).</summary>
    public static void Cost(ScrollCostPhase phase, int vp, long qpcTicks, int rows, int nodes)
    {
        if (Level == ProbeLevel.Off) return;
        PushUi(new ProbeRecord { Kind = (byte)RecKind.Cost, Vp = vp, B0 = (byte)phase, Qpc = qpcTicks, I0 = rows, I1 = nodes });
    }

    /// <summary>A RENDER-THREAD per-phase cost sample (<see cref="ScrollCostPhase.TileRaster"/> /
    /// <see cref="ScrollCostPhase.Composite"/>), recorded on the render ring beside the poses: <paramref name="qpcTicks"/> =
    /// elapsed ticks, <paramref name="rows"/>/<paramref name="nodes"/> = the phase's counts (see each phase). Zero-alloc;
    /// a no-op while the probe is off.</summary>
    public static void RenderCost(ScrollCostPhase phase, long qpcTicks, int rows, int nodes)
    {
        if (Level == ProbeLevel.Off) return;
        PushRender(new ProbeRecord { Kind = (byte)RecKind.Cost, B0 = (byte)phase, Qpc = qpcTicks, I0 = rows, I1 = nodes });
    }

    /// <summary>Freeform marker with a byte code (see <see cref="ProbeMark"/>) — never a string on this hot path.
    /// Recorded at Summary and Trace: markers (session fences, "felt wrong", tunable changes) are part of every
    /// recorded session, whatever its level.</summary>
    public static void Mark(long qpc, ProbeMark code)
    {
        if (Level == ProbeLevel.Off) return;   // Summary AND Trace: markers are part of every session's contract
        PushUi(new ProbeRecord { Kind = (byte)RecKind.Mark, Qpc = qpc, B0 = (byte)code });
    }

    // ── burst summary ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Jitter (see <see cref="BurstSummary.MaxJitter"/>) above this normalized threshold, with no clamp/jump/
    /// late-present condition already present, earns <see cref="ScrollVerdict.Uneven"/> rather than
    /// <see cref="ScrollVerdict.Smooth"/>. Chosen empirically generous: a perfectly uniform synthetic pose stream
    /// scores ~0; real motion has some non-zero jitter from frame-timing noise alone.</summary>
    internal const double JitterUnevenThreshold = 0.20;

    /// <summary>Close out a measurement burst: consumes every UI/render-ring record written since the previous
    /// <see cref="EndBurst"/> call (or since process start, for the first call) and returns the aggregate
    /// <see cref="BurstSummary"/>. Off the hot path — allocates. Safe to call from any thread; a ring that has
    /// wrapped since the last call silently contributes only its still-retained tail (the same "best effort" the
    /// rings themselves accept for a burst that outruns 8192 records).</summary>
    public static BurstSummary EndBurst(long qpc)
    {
        long uiEnd = Volatile.Read(ref s_uiCount);
        long renderEnd = Volatile.Read(ref s_renderCount);
        long uiStart = Math.Max(s_lastBurstUiCount, uiEnd - RingCapacity);
        long renderStart = Math.Max(s_lastBurstRenderCount, renderEnd - RingCapacity);

        int notches = 0, extentJumps = 0;
        int costPhaseCount = (int)ScrollCostPhase.Count;
        var costSumMs = new double[costPhaseCount];
        var costMaxMs = new double[costPhaseCount];
        var costCount = new int[costPhaseCount];
        double qpcToMs = 1000.0 / Stopwatch.Frequency;

        for (long i = uiStart; i < uiEnd; i++)
        {
            ref ProbeRecord r = ref s_uiRing[(int)(i & RingMask)];
            switch ((RecKind)r.Kind)
            {
                case RecKind.Input:
                    if (r.B1 == PhaseNotch) notches++;
                    break;
                case RecKind.Extent:
                    if (r.B0 == 0) extentJumps++;   // B0 == 0 ⇒ NOT anchored same frame ⇒ a visible jump
                    break;
                case RecKind.Cost:
                    int phase = r.B0;
                    if ((uint)phase < (uint)costPhaseCount)
                    {
                        double ms = r.Qpc * qpcToMs;
                        costSumMs[phase] += ms;
                        costCount[phase]++;
                        if (ms > costMaxMs[phase]) costMaxMs[phase] = ms;
                    }
                    break;
            }
        }

        int presents = 0, coverageClamps = 0, latePresents = 0;
        double maxJitter = 0.0, jitterSum = 0.0;
        int jitterGroups = 0;

        int tileTurns = 0, tilesMax = 0, exposedTileMissing = 0;
        long tilesSum = 0;
        var byVp = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(long Qpc, double Pos)>>();
        for (long i = renderStart; i < renderEnd; i++)
        {
            ref ProbeRecord r = ref s_renderRing[(int)(i & RingMask)];
            if (r.Kind == (byte)RecKind.Cost)
            {
                int phase = r.B0;
                if ((uint)phase < (uint)costPhaseCount)
                {
                    double ms = r.Qpc * qpcToMs;
                    costSumMs[phase] += ms;
                    costCount[phase]++;
                    if (ms > costMaxMs[phase]) costMaxMs[phase] = ms;
                }
                if (phase == (int)ScrollCostPhase.TileRaster)
                {
                    tileTurns++;
                    tilesSum += r.I0;
                    if (r.I0 > tilesMax) tilesMax = r.I0;
                }
                else if (phase == (int)ScrollCostPhase.Composite) exposedTileMissing += r.I1;
                continue;
            }
            if (r.Kind != (byte)RecKind.Pose) continue;   // the render ring also carries per-present Turn rows
            presents++;
            if (r.B0 != 0) coverageClamps++;
            if (!byVp.TryGetValue(r.Vp, out var list))
            {
                list = new System.Collections.Generic.List<(long, double)>();
                byVp[r.Vp] = list;
            }
            list.Add((r.Qpc, r.D0));
        }

        foreach (var kvp in byVp)
        {
            var list = kvp.Value;
            list.Sort(static (a, b) => a.Qpc.CompareTo(b.Qpc));

            if (list.Count >= 2)
            {
                var gaps = new double[list.Count - 1];
                for (int k = 1; k < list.Count; k++) gaps[k - 1] = list[k].Qpc - list[k - 1].Qpc;
                var sortedGaps = (double[])gaps.Clone();
                Array.Sort(sortedGaps);
                double median = sortedGaps[sortedGaps.Length / 2];
                if (median > 0)
                {
                    for (int k = 0; k < gaps.Length; k++)
                        if (gaps[k] > 1.5 * median) latePresents++;
                }
            }

            if (list.Count >= 3)
            {
                var diffs = new double[list.Count - 1];
                for (int k = 1; k < list.Count; k++) diffs[k - 1] = list[k].Pos - list[k - 1].Pos;
                double meanSpeed = 0.0;
                for (int k = 0; k < diffs.Length; k++) meanSpeed += Math.Abs(diffs[k]);
                meanSpeed /= diffs.Length;

                var second = new double[diffs.Length - 1];
                for (int k = 1; k < diffs.Length; k++) second[k - 1] = diffs[k] - diffs[k - 1];
                double meanSecond = 0.0;
                for (int k = 0; k < second.Length; k++) meanSecond += second[k];
                meanSecond /= second.Length;
                double variance = 0.0;
                for (int k = 0; k < second.Length; k++)
                {
                    double d = second[k] - meanSecond;
                    variance += d * d;
                }
                variance /= second.Length;
                double stddev = Math.Sqrt(variance);
                double jitter = stddev / Math.Max(meanSpeed, 1e-6);
                if (jitter > maxJitter) maxJitter = jitter;
                jitterSum += jitter;
                jitterGroups++;
            }
        }
        double avgJitter = jitterGroups > 0 ? jitterSum / jitterGroups : 0.0;

        var maxByPhase = new double[costPhaseCount];
        var avgByPhase = new double[costPhaseCount];
        double maxCostMs = 0.0, costMsSumAll = 0.0;
        int costCountAll = 0;
        for (int p = 0; p < costPhaseCount; p++)
        {
            maxByPhase[p] = costMaxMs[p];
            avgByPhase[p] = costCount[p] > 0 ? costSumMs[p] / costCount[p] : 0.0;
            if (costMaxMs[p] > maxCostMs) maxCostMs = costMaxMs[p];
            costMsSumAll += costSumMs[p];
            costCountAll += costCount[p];
        }
        double avgCostMs = costCountAll > 0 ? costMsSumAll / costCountAll : 0.0;

        ScrollVerdict verdict =
            coverageClamps > 0 ? ScrollVerdict.Clamped :
            extentJumps > 0 ? ScrollVerdict.Jumped :
            latePresents > 0 ? ScrollVerdict.Late :
            maxJitter > JitterUnevenThreshold ? ScrollVerdict.Uneven :
            ScrollVerdict.Smooth;

        s_lastBurstUiCount = uiEnd;
        s_lastBurstRenderCount = renderEnd;

        return new BurstSummary(qpc, notches, presents, coverageClamps, extentJumps, maxJitter, avgJitter,
            latePresents, maxCostMs, avgCostMs, verdict)
        {
            MaxCostMsByPhase = maxByPhase,
            AvgCostMsByPhase = avgByPhase,
            TilesPerFrameMax = tilesMax,
            TilesPerFrameAvg = tileTurns > 0 ? (double)tilesSum / tileTurns : 0.0,
            ExposedTileMissing = exposedTileMissing,
        };
    }
}
