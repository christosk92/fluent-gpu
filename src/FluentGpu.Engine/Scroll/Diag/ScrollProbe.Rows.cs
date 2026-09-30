using System;
using System.Threading;

namespace FluentGpu.Scroll.Diag;

/// <summary>The public record-kind tag of a <see cref="ProbeRow"/>. Ordinals match the probe's private ring kinds
/// byte-for-byte (<see cref="Present"/> is the kind <see cref="ScrollProbe.Present"/> adds), so a drained row keeps the
/// exact meaning the recorder gave it.</summary>
public enum ProbeRowKind : byte
{
    Input = 0,
    Plan = 1,
    Pose = 2,
    Coverage = 3,
    Extent = 4,
    Cost = 5,
    Mark = 6,
    Present = 7,
    /// <summary>One render-thread present (<see cref="ScrollProbe.Turn"/>) — the render ring, beside the poses.</summary>
    Turn = 8,
    /// <summary>The COST split of the render turn behind the <see cref="Turn"/> row written just before it (same
    /// <see cref="ProbeRow.TickSeq"/>): record / build / submit / GPU / slot-wait ms, tiles and KiB rastered, slices
    /// walked, items, and whether the turn was composite-only, kept everything, skipped its submit or carried a frame
    /// capture (evidence-diagnostics §A.4; schema 3).</summary>
    TurnCost = 9,
}

/// <summary>
/// One drained <see cref="ScrollProbe"/> ring record, public and POD (scroll-lab E3): the raw slot fields plus named
/// accessors per <see cref="Kind"/>. Produced by <see cref="ScrollProbe.ReadUi"/>/<see cref="ScrollProbe.ReadRender"/>
/// (incremental, zero-alloc into a caller span) and consumed by the lab's session recorder and
/// <c>Analysis.SessionSeries</c>. Unmanaged on purpose: a session dumps rows as raw bytes and reloads them unchanged.
/// <para>Field map (the emitting <c>ScrollProbe</c> method documents each): Input — <see cref="Source"/>,
/// <see cref="Phase"/>, <see cref="Release"/>, <see cref="Dx"/>/<see cref="Dy"/>; Plan — <see cref="Seq"/>, <see cref="PlanKind"/>,
/// <see cref="PlanStart"/>, <see cref="PlanDest"/>, <see cref="PlanDurationS"/>; Pose — <see cref="Pos"/>, <see cref="Vel"/>,
/// <see cref="Clamped"/>, <see cref="PlanPos"/>, <see cref="SnappedTrans"/>; Coverage — <see cref="CoverStart"/>,
/// <see cref="CoverEnd"/>, <see cref="CoverOrigin"/>, <see cref="CoverFirst"/>/<see cref="CoverLast"/>; Extent —
/// <see cref="ExtentIndex"/>, <see cref="ExtentDelta"/>, <see cref="ExtentAnchored"/>, <see cref="ExtentCause"/>; Mark — <see cref="MarkCode"/>; Present — <see cref="PresentsDisplayed"/>,
/// <see cref="PresentsDropped"/>, <see cref="VblanksRepeated"/>, <see cref="DwmDropped"/>/<see cref="DwmMissed"/>/
/// <see cref="DwmLate"/>, <see cref="LatencyWaitMs"/>, <see cref="RefreshIntervalMs"/>, <see cref="FrameMs"/>,
/// <see cref="Presented"/>, <see cref="PresentStatsValid"/>, <see cref="PairedLedger"/>, <see cref="DwmSeq"/>; Turn —
/// <see cref="TickSeq"/>, <see cref="MissedTicks"/>, <see cref="WakeLagMs"/>, <see cref="SlotWaitMs"/>,
/// <see cref="WorkMs"/>, <see cref="TurnFresh"/>, <see cref="TurnPaced"/>; TurnCost — <see cref="TickSeq"/>,
/// <see cref="TurnRecordMs"/>, <see cref="TurnBuildMs"/>, <see cref="TurnSubmitMs"/>, <see cref="TurnGpuMs"/>,
/// <see cref="TurnPassFrame"/>, <see cref="TurnTilesRastered"/>, <see cref="TurnKiBRastered"/>, <see cref="TurnSlicesWalked"/>,
/// <see cref="TurnItems"/>, <see cref="TurnCostFlags"/>.</para>
/// </summary>
public readonly struct ProbeRow
{
    public readonly ProbeRowKind Kind;
    public readonly byte B0;
    public readonly byte B1;
    public readonly int Vp;
    public readonly int I0;
    public readonly int I1;
    public readonly long Qpc;
    public readonly double D0;
    public readonly double D1;
    public readonly double D2;
    public readonly float F0;
    public readonly float F1;
    public readonly ulong Seq;

    public ProbeRow(ProbeRowKind kind, byte b0, byte b1, int vp, int i0, int i1, long qpc,
                    double d0, double d1, double d2, float f0, float f1, ulong seq)
    {
        Kind = kind; B0 = b0; B1 = b1; Vp = vp; I0 = i0; I1 = i1; Qpc = qpc;
        D0 = d0; D1 = d1; D2 = d2; F0 = f0; F1 = f1; Seq = seq;
    }

    // ── Input ──
    public ScrollSourceCode Source => (ScrollSourceCode)B0;
    public byte Phase => B1;
    public bool IsNotch => Kind == ProbeRowKind.Input && B1 == ScrollProbe.PhaseNotch;
    /// <summary>A contact End's release verdict byte (<c>ContactRelease</c>: 0 unknown, 1 moving, 2 stopped); 0 on every
    /// other input row.</summary>
    public byte Release => (byte)I0;
    public float Dx => F0;
    public float Dy => F1;

    // ── Plan ──
    public byte PlanKind => B0;
    public double PlanStart => D0;
    public double PlanDest => D1;
    public double PlanDurationS => D2;

    // ── Pose ──
    public double Pos => D0;
    public double Vel => D1;
    public bool Clamped => B0 != 0;
    /// <summary>The plan's position before the coverage clamp (plan frame). Rows recorded before it existed carry 0 —
    /// read it only on a <see cref="Clamped"/> row of a recording that has it.</summary>
    public double PlanPos => D2;
    public float SnappedTrans => F0;

    // ── Coverage ──
    public double CoverStart => D0;
    public double CoverEnd => D1;
    public double CoverOrigin => D2;
    public int CoverFirst => I0;
    public int CoverLast => I1;

    // ── Extent ──
    public int ExtentIndex => I0;
    public double ExtentDelta => D0;
    public bool ExtentAnchored => B0 != 0;
    public ProbeExtentCause ExtentCause => (ProbeExtentCause)B1;

    // ── Mark ──
    public ProbeMark MarkCode => (ProbeMark)B0;

    // ── Present ──
    public long PresentsDisplayed => (long)Seq;
    public long PresentsDropped => (long)D0;
    public long VblanksRepeated => (long)D1;
    public double LatencyWaitMs => D2;
    public uint DwmDropped => (uint)I0;
    public uint DwmMissed => (uint)I1;
    public uint DwmLate => (uint)Vp;
    public float RefreshIntervalMs => F0;
    public float FrameMs => F1;
    public bool Presented => (B0 & 1) != 0;
    public bool PresentStatsValid => (B0 & 2) != 0;
    /// <summary>The row's ledger counters come from DXGI's paired counters (<c>PresentStatisticsLedger</c>). A recording
    /// without it predates that ledger: its <see cref="PresentsDropped"/> is the lagging-sample artefact, its
    /// <see cref="VblanksRepeated"/> includes idle and producer-skipped vblanks, and its DWM deltas carry no sample
    /// identity — none of them a display attestation.</summary>
    public bool PairedLedger => (B0 & PresentPairedLedgerFlag) != 0;
    /// <summary>The DWM sample the row's DWM deltas belong to, folded to 1..255 (0 = no sample yet): the deltas are
    /// fresh on a row whose value differs from the previous Present row's.</summary>
    public byte DwmSeq => B1;

    /// <summary><see cref="PairedLedger"/>'s bit in <see cref="B0"/>.</summary>
    public const byte PresentPairedLedgerFlag = 4;

    /// <summary>Folds a DWM sample sequence (0 = none) onto 1..255 so a wrapped value never reads as "none".</summary>
    public static byte DwmSeqByte(uint seq) => seq == 0 ? (byte)0 : (byte)(1 + (seq - 1) % 255);

    // ── Turn ──
    public long TickSeq => (long)Seq;
    public int MissedTicks => I0;
    public double WakeLagMs => D0;
    public double SlotWaitMs => D1;
    public double WorkMs => D2;
    public bool TurnFresh => (B0 & TurnFreshFlag) != 0;
    public bool TurnPaced => (B0 & TurnPacedFlag) != 0;
    public const byte TurnFreshFlag = 1;
    public const byte TurnPacedFlag = 2;

    // ── TurnCost (shares TickSeq with the Turn row it follows) ──
    public float TurnRecordMs => F0;
    public float TurnBuildMs => F1;
    public double TurnSubmitMs => D0;
    public double TurnGpuMs => D1;
    /// <summary>The slice recorder's pass frame that recorded the turn (0 = composite-only): walks.tsv's <c>frame</c>.</summary>
    public uint TurnPassFrame => (uint)D2;
    public int TurnTilesRastered => I0;
    public int TurnKiBRastered => I1;
    public int TurnSlicesWalked => Vp;
    /// <summary>Composite items (clamped to 255).</summary>
    public int TurnItems => B0;
    public byte TurnCostFlags => B1;
    public bool TurnCompositeOnly => (B1 & TurnCostCompositeOnly) != 0;
    public bool TurnKeptAll => (B1 & TurnCostKeptAll) != 0;
    public bool TurnSkipSubmit => (B1 & TurnCostSkipSubmit) != 0;
    public bool TurnCapture => (B1 & TurnCostCapture) != 0;
    /// <summary>The turn recorded nothing and re-placed the retained slices (a scroll tick).</summary>
    public const byte TurnCostCompositeOnly = 1;
    /// <summary>The record pass kept every slice whole (no bytes written).</summary>
    public const byte TurnCostKeptAll = 2;
    /// <summary>The submit was elided (the presented frame was already correct).</summary>
    public const byte TurnCostSkipSubmit = 4;
    /// <summary>The turn completed an armed frame capture (its back-buffer readback stalled it on purpose).</summary>
    public const byte TurnCostCapture = 8;

    // ── factories (the lab/tests build rows by hand; the rings never go through these) ──
    public static ProbeRow ForInput(long qpc, ScrollSourceCode source, byte phase, float dx, float dy, int vp = -1, byte release = 0)
        => new(ProbeRowKind.Input, (byte)source, phase, vp, release, 0, qpc, 0, 0, 0, dx, dy, 0);

    public static ProbeRow ForPose(long presentQpc, int vp, double pos, double vel, bool clamped = false, float snappedTrans = 0f,
                                   double planPos = double.NaN)
        => new(ProbeRowKind.Pose, (byte)(clamped ? 1 : 0), 0, vp, 0, 0, presentQpc, pos, vel,
               double.IsNaN(planPos) ? pos : planPos, snappedTrans, 0, 0);

    public static ProbeRow ForPlan(long qpc, int vp, ulong seq, byte kind, double start, double dest, double durationS = 0.0)
        => new(ProbeRowKind.Plan, kind, 0, vp, 0, 0, qpc, start, dest, durationS, 0, 0, seq);

    public static ProbeRow ForCoverage(long qpc, int vp, double start, double end, double origin, int first, int last)
        => new(ProbeRowKind.Coverage, 0, 0, vp, first, last, qpc, start, end, origin, 0, 0, 0);

    public static ProbeRow ForExtent(long qpc, int vp, int index, double delta, bool anchored,
                                     ProbeExtentCause cause = ProbeExtentCause.Measured)
        => new(ProbeRowKind.Extent, (byte)(anchored ? 1 : 0), (byte)cause, vp, index, 0, qpc, delta, 0, 0, 0, 0, 0);

    public static ProbeRow ForMark(long qpc, ProbeMark code)
        => new(ProbeRowKind.Mark, (byte)code, 0, 0, 0, 0, qpc, 0, 0, 0, 0, 0, 0);

    public static ProbeRow ForPresent(long qpc, long presentsDisplayed, long presentsDropped, long vblanksRepeated,
                                      uint dwmDropped, uint dwmMissed, uint dwmLate, double latencyWaitMs,
                                      float refreshIntervalMs, float frameMs, bool presented, bool statsValid,
                                      uint dwmSampleSeq = 0, bool pairedLedger = true)
        => new(ProbeRowKind.Present, (byte)((presented ? 1 : 0) | (statsValid ? 2 : 0) | (pairedLedger ? PresentPairedLedgerFlag : 0)),
               DwmSeqByte(dwmSampleSeq), (int)dwmLate, (int)dwmDropped,
               (int)dwmMissed, qpc, presentsDropped, vblanksRepeated, latencyWaitMs, refreshIntervalMs, frameMs,
               (ulong)presentsDisplayed);

    public static ProbeRow ForTurn(long tickQpc, long tickSeq, int missedTicks, double wakeLagMs, double slotWaitMs,
                                   double workMs, bool fresh)
        => new(ProbeRowKind.Turn, (byte)((fresh ? TurnFreshFlag : 0) | (tickSeq != 0 ? TurnPacedFlag : 0)), 0, 0,
               missedTicks, 0, tickQpc, wakeLagMs, slotWaitMs, workMs, 0, 0, (ulong)tickSeq);

    public static ProbeRow ForTurnCost(long tickQpc, long tickSeq, float recordMs, float buildMs, double submitMs, double gpuMs,
                                       uint passFrame, int tilesRastered, int kibRastered, int slicesWalked, int items, byte flags)
        => new(ProbeRowKind.TurnCost, (byte)Math.Clamp(items, 0, 255), flags, slicesWalked, tilesRastered, kibRastered, tickQpc,
               submitMs, gpuMs, passFrame, recordMs, buildMs, (ulong)tickSeq);
}

public static partial class ScrollProbe
{
    /// <summary>Total UI-ring writes ever (monotonic). A reader that wants only records from "now on" starts its
    /// cursor here.</summary>
    public static long UiWriteCount => Volatile.Read(ref s_uiCount);

    /// <summary>Total render-ring writes ever (monotonic).</summary>
    public static long RenderWriteCount => Volatile.Read(ref s_renderCount);

    /// <summary>Ring capacity (records per ring). A reader that falls more than this behind loses the overwritten
    /// records: <see cref="ReadUi"/>/<see cref="ReadRender"/> then resume at the oldest still-retained record.</summary>
    public const int Capacity = RingCapacity;

    /// <summary>Incremental drain of the UI ring: copies up to <c>into.Length</c> records written at or after
    /// <paramref name="fromCount"/> into <paramref name="into"/> and returns how many were copied; <paramref name="next"/>
    /// is the cursor to pass next time. Zero allocation, any thread. When the reader has fallen behind by more than
    /// <see cref="Capacity"/>, the overwritten records are skipped (the gap is <c>(next - returned) - fromCount</c>);
    /// a record the producer may have been overwriting during the copy is dropped rather than returned torn.</summary>
    public static int ReadUi(long fromCount, Span<ProbeRow> into, out long next)
        => Read(s_uiRing, ref s_uiCount, fromCount, into, out next);

    /// <summary>Incremental drain of the render ring — same contract as <see cref="ReadUi"/>.</summary>
    public static int ReadRender(long fromCount, Span<ProbeRow> into, out long next)
        => Read(s_renderRing, ref s_renderCount, fromCount, into, out next);

    private static int Read(ProbeRecord[] ring, ref long countRef, long fromCount, Span<ProbeRow> into, out long next)
    {
        long end = Volatile.Read(ref countRef);
        long start = fromCount;
        if (start < end - RingCapacity) start = end - RingCapacity;
        if (start < 0) start = 0;
        if (start > end) start = end;
        int n = (int)Math.Min(end - start, (long)into.Length);
        for (int i = 0; i < n; i++)
            into[i] = ToRow(in ring[(int)((start + i) & RingMask)]);

        // Torn-read guard: slot j is overwritten by write j + RingCapacity, which may have started (or finished) while we
        // copied. Any copied index j <= end2 - RingCapacity is suspect — drop that prefix.
        long end2 = Volatile.Read(ref countRef);
        long firstSafe = end2 - RingCapacity + 1;
        if (start < firstSafe && n > 0)
        {
            int drop = (int)Math.Min(firstSafe - start, (long)n);
            if (drop < n) into.Slice(drop, n - drop).CopyTo(into);
            n -= drop;
            start += drop;
        }
        next = start + n;
        return n;
    }

    private static ProbeRow ToRow(in ProbeRecord r)
        => new((ProbeRowKind)r.Kind, r.B0, r.B1, r.Vp, r.I0, r.I1, r.Qpc, r.D0, r.D1, r.D2, r.F0, r.F1, r.Seq);
}
