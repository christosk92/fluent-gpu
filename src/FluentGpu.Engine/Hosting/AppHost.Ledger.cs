using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Hosting;

// ── the frame ledger's host side (FrameLedger.cs) ──────────────────────────────────────────────────────────────────────
//
// UI: RunFrame wraps RunFrameCore. Off, the wrapper is one static bool read, and RunFrameCore / Paint pay only the plain
// field stores below (the pump's end stamp, the exit gate, the wake mask — values they already hold). On (and this host owns the ledger), the wrapper reads
// the thread's cycles + allocated bytes and QPC around the frame, and RunFrameCore / Paint leave their own stamps in plain
// fields as they go (the pump's end, which early-out stopped the frame, Paint's six phase stamps); the record is assembled
// here from those and the frame's FrameStats. The loop's wait before the frame arrives through NoteLoopWait.
// Render: the render loop hands each turn's own facts to LedgerRenderTurn (render thread), which adds this host's submit
// (the present split, the repaint coverage, the tiles rastered, the outcome) and then polls the swapchain's GPU sample:
// a new sequence becomes a GPU record, with the pass timeline of the same submit while pass timing is on.

public sealed partial class AppHost
{
    /// <summary>Paint's phase stamps for the ledger (QPC): frame start, flush, layout, anim, record, submit.</summary>
    private readonly record struct LedgerPaintStamps(long Start, long Flush, long Layout, long Anim, long Record, long Submit);

    private LedgerFrameExit _ledgerExit;
    private uint _ledgerWake;
    private long _ledgerPumpQpc;
    private LedgerPaintStamps _ledgerPaint;
    private long _ledgerWaitStart, _ledgerWaitEnd;
    private int _ledgerWaitMs;
    private HostWaitKind _ledgerWaitKind;
    // Render thread: this host's side of the current turn, written by SubmitPresentOnRenderThread and consumed (then cleared)
    // by LedgerRenderTurn at the turn's end.
    private LedgerTurnOutcome _ledgerTurnOutcome;
    private float _ledgerTurnCoverage;
    private RepaintFullReason _ledgerTurnFull;
    private int _ledgerTurnTiles;
    private bool _ledgerTurnPresented;
    private ulong _ledgerGpuSampleSeq, _ledgerPassSeq;

    /// <summary>Run one full frame: pump + input, then paint (the reactive flush + layout + record happen in Paint). With the
    /// <see cref="FrameLedger"/> on, the frame is recorded (early-outs included) when this host owns the ledger.</summary>
    public FrameStats RunFrame()
    {
        if (!FrameLedger.Enabled) return RunFrameCore();
        if (_isDetachedChild || !FrameLedger.TryOwn(this)) return RunFrameCore();
        return RunFrameLedgered();
    }

    private FrameStats RunFrameLedgered()
    {
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        ulong cyclesStart = ThreadCycles.Read();
        long start = Stopwatch.GetTimestamp();
        _ledgerPumpQpc = 0;
        _ledgerPaint = default;
        FrameStats s = RunFrameCore();
        long end = Stopwatch.GetTimestamp();
        ulong cycles = ThreadCycles.Read();
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        long cpuTime = FrameLedger.ReadThreadCpuTime();
        ulong uiCycles = cycles >= cyclesStart && cyclesStart != 0 ? cycles - cyclesStart : 0;
        bool painted = _ledgerPaint.Start != 0;
        var flags = LedgerUiFlags.None;
        if (s.Rendered) flags |= LedgerUiFlags.Rendered;
        if (painted && s.Presented) flags |= LedgerUiFlags.Presented;
        if (painted && s.CompositeOnlyTurn) flags |= LedgerUiFlags.CompositeOnly;
        if (s.ScrollActive) flags |= LedgerUiFlags.ScrollActive;
        if (painted) flags |= LedgerUiFlags.Painted;
        if (_ledgerWaitStart != 0 && _ledgerWaitMs < 0) flags |= LedgerUiFlags.WaitInfinite;
        var r = new LedgerUiFrame
        {
            StartQpc = start, PumpQpc = _ledgerPumpQpc, EndQpc = end,
            PaintQpc = _ledgerPaint.Start, FlushQpc = _ledgerPaint.Flush, LayoutQpc = _ledgerPaint.Layout,
            AnimQpc = _ledgerPaint.Anim, RecordQpc = _ledgerPaint.Record, SubmitQpc = _ledgerPaint.Submit,
            WaitStartQpc = _ledgerWaitStart, WaitEndQpc = _ledgerWaitEnd, WaitRequestedMs = _ledgerWaitMs, WaitKind = (byte)_ledgerWaitKind,
            Exit = (byte)_ledgerExit, Flags = (ushort)flags,
            PublishSeq = painted ? s.PublishSeq : 0,
            // The process counter is read here, after the frame's end stamp and its own cycle read, so its ~10 µs syscall is never
            // inside a measured span (it lands in the gap before the next frame, as part of "other").
            UiCycles = uiCycles, UiCyclesTotal = cycles, ProcessCyclesTotal = FrameLedger.ReadProcessCycles(),
            AllocBytes = alloc - allocStart, AllocBytesTotal = alloc,
            GcPauseTicksTotal = GC.GetTotalPauseDuration().Ticks,
            Gc0Total = GC.CollectionCount(0), Gc1Total = GC.CollectionCount(1), Gc2Total = GC.CollectionCount(2),
            Nodes = s.NodesVisited, DrawNodes = s.DrawNodeCount, Slices = s.Slices.Slices, Components = s.ComponentsRendered,
            DamageCoverage = painted ? s.RepaintCoverage : 0f,
            DamageRects = (ushort)Math.Clamp(s.RepaintRectCount, 0, ushort.MaxValue),
            FullReason = (byte)s.RepaintFullReason,
            PresentedTotal = (long)PresentedSequence, MissedVsyncsTotal = Interlocked.Read(ref _missedVsyncsTotal),
            WakeMask = _ledgerWake, UiCpuTimeTotal = cpuTime,
        };
        FrameLedger.RecordUi(ref r);
        _ledgerWaitStart = _ledgerWaitEnd = 0;
        _ledgerWaitMs = 0;
        return s;
    }

    /// <summary>The platform loop's wait (from <see cref="NoteLoopWait"/>): the first start and the last end since the previous
    /// frame, the timeout and the kind of the last one.</summary>
    private void NoteLedgerWait(long startQpc, long endQpc, int requestedMs)
    {
        if (_ledgerWaitStart == 0) _ledgerWaitStart = startQpc;
        _ledgerWaitEnd = endQpc;
        _ledgerWaitMs = requestedMs;
        _ledgerWaitKind = _lastWaitKind;
    }

    /// <summary>RENDER THREAD: the submit's side of the turn (only while the ledger is on) — the repaint set's coverage of the target
    /// (DIP) and what the submit did with it.</summary>
    private void NoteLedgerSubmit(in RepaintDamageRegion repaint, in FrameInfo info, LedgerTurnOutcome outcome)
    {
        float scale = info.Scale > 0f ? info.Scale : 1f;
        _ledgerTurnCoverage = repaint.Coverage(info.SizePx.Width / scale, info.SizePx.Height / scale);
        _ledgerTurnFull = repaint.FullReason;
        _ledgerTurnOutcome = outcome;
    }

    /// <summary>Test seam: one ledgered turn as the render loop would hand it, with this host's submit side set as
    /// SubmitPresentOnRenderThread would have left it.</summary>
    internal void LedgerTurnForTest(in Threading.LedgerTurnFacts f, bool presented, LedgerTurnOutcome outcome)
    {
        _ledgerTurnPresented = presented;
        _ledgerTurnOutcome = outcome;
        LedgerRenderTurn(in f);
    }

    /// <summary>RENDER THREAD (<see cref="Threading.RenderThread.LedgerSink"/>): one turn of this host's loop. Records the turn when
    /// this host owns the ledger, then any GPU sample that retired since the last turn. Zero allocation.</summary>
    private void LedgerRenderTurn(in Threading.LedgerTurnFacts f)
    {
        var outcome = _ledgerTurnOutcome;
        float coverage = _ledgerTurnCoverage;
        var full = _ledgerTurnFull;
        int tiles = _ledgerTurnTiles;
        bool presented = _ledgerTurnPresented;
        _ledgerTurnOutcome = LedgerTurnOutcome.None;
        _ledgerTurnCoverage = 0f;
        _ledgerTurnFull = RepaintFullReason.None;
        _ledgerTurnTiles = 0;
        _ledgerTurnPresented = false;
        if (!FrameLedger.IsOwner(this)) return;
        bool hasSample = _swapchain.TryGetGpuRenderSample(out GpuRenderSample g);
        // The submit this turn made (the join key to the GPU stream): 0 unless the turn actually submitted to this swapchain; an age
        // of ulong.MaxValue means the sample's submit is not comparable with the current one (an invalidated target).
        bool submitted = presented && outcome is LedgerTurnOutcome.Recorded or LedgerTurnOutcome.CompositeOnly or LedgerTurnOutcome.Direct;
        ulong submitSeq = submitted && hasSample && g.SubmitSequence != 0 && g.SubmitAge != ulong.MaxValue ? g.SubmitSequence + g.SubmitAge : 0;
        bool turnPresented = f.Kind is LedgerTurnKind.Fresh or LedgerTurnKind.Motion;
        Threading.PresentSplit split = turnPresented ? _presentSplit : default;
        var flags = LedgerTurnFlags.None;
        if (full != RepaintFullReason.None) flags |= LedgerTurnFlags.WholeFrame;
        if (presented) flags |= LedgerTurnFlags.Presented;
        var r = new LedgerRenderTurn
        {
            WaitStartQpc = f.WaitStartQpc, StartQpc = f.StartQpc, SlotOpenQpc = f.SlotOpenQpc, DoneQpc = f.DoneQpc, EndQpc = f.EndQpc,
            TickSeq = f.TickSeq, TickQpc = f.TickQpc, PublishSeq = f.PublishSeq,
            SubmitSeq = submitSeq, CpuTimeTotal = FrameLedger.ReadThreadCpuTime(),
            Cycles = f.Cycles, CyclesTotal = f.CyclesTotal, AllocBytes = f.AllocBytes,
            StageMs = (float)split.StageMs, RecordMs = (float)split.RecordMs, SubmitMs = (float)split.SubmitMs, FenceMs = (float)split.FenceMs,
            LatencyMs = (float)split.LatencyMs, PresentMs = (float)split.PresentMs, VideoMs = (float)split.VideoMs,
            DamageCoverage = coverage, TilesRastered = tiles, MissedTicks = f.MissedTicks,
            Kind = (byte)f.Kind, Outcome = (byte)outcome, Flags = (byte)flags, FullReason = (byte)full,
        };
        FrameLedger.RecordRender(ref r);

        if (!hasSample || g.Sequence == _ledgerGpuSampleSeq) return;
        ulong prevSeq = _ledgerGpuSampleSeq;
        _ledgerGpuSampleSeq = g.Sequence;
        var gr = new LedgerGpuFrame
        {
            ObservedQpc = Stopwatch.GetTimestamp(), PublishedQpc = g.PublishedQpc, GpuStartQpc = g.GpuStartQpc, GpuEndQpc = g.GpuEndQpc,
            SampleSeq = g.Sequence, SubmitSeq = g.SubmitSequence, GpuMs = (float)g.ExecutionMs,
            MissedSamples = prevSeq != 0 && g.Sequence > prevSeq + 1 ? (int)Math.Min(g.Sequence - prevSeq - 1, int.MaxValue) : 0,
        };
        // The pass timeline retires with the same ring slot's whole-frame pair (D3D12Device.BeginRecording collects both), so a
        // timeline that advanced together with the sample belongs to the same submit.
        if (_device.GpuPassTimingEnabled && FrameLedger.PassScratch is { } passes)
        {
            int n = _swapchain.CopyGpuPassTimeline(passes, out GpuPassFrameSummary ps);
            if (n > 0 && ps.Sequence != _ledgerPassSeq)
            {
                _ledgerPassSeq = ps.Sequence;
                gr.PassWholeMs = ps.WholeMs;
                gr.PassCount = ps.PassCount;
                gr.PassesDropped = ps.PassesDropped;
                for (int i = 0; i < n; i++) gr.AddPassMs(passes[i].Kind, passes[i].Ms);
            }
        }
        FrameLedger.RecordGpu(ref gr);
    }
}
