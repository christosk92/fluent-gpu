namespace FluentGpu.Scroll.Diag;

public static partial class ScrollProbe
{
    /// <summary>Ring kind byte for <see cref="Present"/> (the private <c>RecKind</c> stops at Mark = 6; this is the next
    /// ordinal and matches <see cref="ProbeRowKind.Present"/>).</summary>
    private const byte KindPresent = (byte)ProbeRowKind.Present;

    /// <summary>UI THREAD ONLY (the UI ring's producer). One finished frame's present-truth sample, recorded where the
    /// host finalizes <c>FrameStats</c>: the CUMULATIVE swapchain ledger (<paramref name="presentsDisplayed"/>/
    /// <paramref name="presentsDropped"/>/<paramref name="vblanksRepeated"/> — DXGI's paired counters; a reader diffs
    /// consecutive rows), DWM's deltas (<paramref name="dwmDropped"/>/<paramref name="dwmMissed"/>/<paramref name="dwmLate"/>
    /// — non-zero only on the first frame that observes their sample) with that sample's identity
    /// (<paramref name="dwmSampleSeq"/>, stored as <see cref="ProbeRow.DwmSeq"/>), the last present's latency-waitable
    /// block, the measured refresh interval and the UI frame time. <paramref name="qpc"/> is the record time. Recorded at
    /// Summary and Trace; one volatile byte load and a branch at Off. No allocation.</summary>
    public static void Present(long qpc, long presentsDisplayed, long presentsDropped, long vblanksRepeated,
                               uint dwmDropped, uint dwmMissed, uint dwmLate, uint dwmSampleSeq, double latencyWaitMs,
                               double refreshIntervalMs, double frameMs, bool presented, bool statsValid)
    {
        if (Level == ProbeLevel.Off) return;
        PushUi(new ProbeRecord
        {
            Kind = KindPresent,
            B0 = (byte)((presented ? 1 : 0) | (statsValid ? 2 : 0) | ProbeRow.PresentPairedLedgerFlag),
            B1 = ProbeRow.DwmSeqByte(dwmSampleSeq),
            Qpc = qpc,
            Seq = (ulong)presentsDisplayed,
            D0 = presentsDropped,
            D1 = vblanksRepeated,
            D2 = latencyWaitMs,
            I0 = (int)dwmDropped,
            I1 = (int)dwmMissed,
            Vp = (int)dwmLate,
            F0 = (float)refreshIntervalMs,
            F1 = (float)frameMs,
        });
    }

    /// <summary>Ring kind byte for <see cref="Turn"/> (matches <see cref="ProbeRowKind.Turn"/>).</summary>
    private const byte KindTurn = (byte)ProbeRowKind.Turn;

    /// <summary>RENDER THREAD ONLY (the render ring's producer). One present the render thread made, recorded per
    /// present so every missed compositor tick is attributable: <paramref name="tickQpc"/> is the tick the present was
    /// decided for (the turn start on an unpaced present), <paramref name="tickSeq"/> its display-clock sequence (0 =
    /// unpaced), <paramref name="missedTicks"/> the ticks this present charged to the missed-motion-tick counter,
    /// <paramref name="wakeLagMs"/> tick to turn start, <paramref name="slotWaitMs"/> the present-slot (latency waitable)
    /// wait, <paramref name="workMs"/> slot open to present returned (record + submit + back-buffer fence + Present), and
    /// <paramref name="fresh"/> whether it presented a fresh UI publication (false = a motion re-present of the retained
    /// scene). Recorded at Summary and Trace. No allocation.
    /// <para>When the host noted the turn's cost split (<see cref="NoteTurnCost"/>) since the last present, a
    /// <see cref="ProbeRowKind.TurnCost"/> row with the same tick follows this one (evidence-diagnostics §A.4).</para></summary>
    public static void Turn(long tickQpc, long tickSeq, int missedTicks, double wakeLagMs, double slotWaitMs, double workMs, bool fresh)
    {
        if (Level == ProbeLevel.Off) { s_costPending = false; return; }
        PushRender(new ProbeRecord
        {
            Kind = KindTurn,
            B0 = (byte)((fresh ? ProbeRow.TurnFreshFlag : 0) | (tickSeq != 0 ? ProbeRow.TurnPacedFlag : 0)),
            Qpc = tickQpc,
            Seq = (ulong)tickSeq,
            I0 = missedTicks,
            D0 = wakeLagMs,
            D1 = slotWaitMs,
            D2 = workMs,
        });
        if (!s_costPending) return;
        s_costPending = false;
        PushRender(new ProbeRecord
        {
            Kind = KindTurnCost,
            B0 = (byte)Math.Clamp(s_costItems, 0, 255),
            B1 = s_costFlags,
            Vp = s_costWalked,
            I0 = s_costTiles,
            I1 = s_costKiB,
            Qpc = tickQpc,
            Seq = (ulong)tickSeq,
            D0 = s_costSubmitMs,
            D1 = s_costGpuMs,
            D2 = s_costPassFrame,
            F0 = (float)s_costRecordMs,
            F1 = (float)s_costBuildMs,
        });
    }

    /// <summary>Ring kind byte for the <see cref="ProbeRowKind.TurnCost"/> row.</summary>
    private const byte KindTurnCost = (byte)ProbeRowKind.TurnCost;

    // The cost split of the render turn in flight (render thread only: the composite turn notes it, the present that ends
    // the turn writes it out with its tick). Plain statics — zero allocation.
    private static bool s_costPending;
    private static double s_costRecordMs, s_costBuildMs, s_costSubmitMs, s_costGpuMs;
    private static int s_costTiles, s_costKiB, s_costWalked, s_costItems;
    private static double s_costPassFrame;
    private static byte s_costFlags;

    /// <summary>RENDER THREAD ONLY (the composite turn's owner). The cost split of the turn about to present:
    /// <paramref name="recordMs"/> the scene record (0 on a composite-only turn), <paramref name="buildMs"/> the composite
    /// plan (<c>BuildComposite</c>), <paramref name="submitMs"/> the backend submit + raster bookkeeping,
    /// <paramref name="gpuMs"/> the latest RETIRED GPU execution, tiles / KiB rastered, slices walked, composite items,
    /// <see cref="ProbeRow"/>'s <c>TurnCost*</c> flags and <paramref name="passFrame"/> — the slice recorder's pass frame
    /// that recorded the turn (0 on a composite-only turn): the key the evidence bundle's walks.tsv joins on. The
    /// present-slot wait is on the Turn row of the same tick. Kept at Summary and Trace; one volatile load and a branch at Off.</summary>
    public static void NoteTurnCost(double recordMs, double buildMs, double submitMs, double gpuMs, int tilesRastered,
        int kibRastered, int slicesWalked, int items, byte flags, uint passFrame = 0)
    {
        if (Level == ProbeLevel.Off) return;
        s_costPassFrame = passFrame;
        s_costRecordMs = recordMs; s_costBuildMs = buildMs; s_costSubmitMs = submitMs; s_costGpuMs = gpuMs;
        s_costTiles = tilesRastered; s_costKiB = kibRastered; s_costWalked = slicesWalked; s_costItems = items;
        s_costFlags = flags;
        s_costPending = true;
    }
}
