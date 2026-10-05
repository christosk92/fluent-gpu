using System.Runtime.InteropServices;
using FluentGpu.Rhi;

namespace FluentGpu.Hosting;

// ── the frame ledger's POD records (FrameLedger.cs) ────────────────────────────────────────────────────────────────────
//
// Four streams, one fixed-size unmanaged struct each, so a ring slot is a plain struct copy, a dump is the arrays' raw bytes
// (FrameLedgerFile.cs) and nothing on the record path can allocate. Every instant is a raw QPC stamp (Stopwatch ticks; the
// file header carries the frequency); every CPU figure is a raw CYCLE count (QueryThreadCycleTime / QueryProcessCycleTime,
// converted at export through the ledger's calibrated cycles-per-ms); "Total" fields are the thread's / process's cumulative
// counter at the record's end, so any window's CPU is a difference of two of them and "other threads" is
// process - UI - render over the same window. A zero stamp means "this frame never reached that point". Beside the cycles,
// the UI and render records carry the thread's cumulative CPU time from GetThreadTimes (100 ns, scheduler-tick accounted): a
// cross-check that needs no rate.
// Field order is the file format: append only, and bump FrameLedgerFile.Version when a layout changes.

/// <summary>Where a <see cref="AppHost.RunFrame"/> left: <see cref="Painted"/>, or the early-out that stopped it before Paint.</summary>
public enum LedgerFrameExit : byte
{
    Painted = 0,
    /// <summary>The window closed during the pump.</summary>
    Closed = 1,
    /// <summary>Blocked on a device-loss recovery handshake.</summary>
    Recovering = 2,
    /// <summary>Minimized, hidden, cloaked or covered: the park gate (pump + posts only).</summary>
    Parked = 3,
    /// <summary>No active work and no completed image decode: the idle gate.</summary>
    Idle = 4,
    /// <summary>A wake whose only reason was the video pump, settled without a frame (F098).</summary>
    VideoOnly = 5,
    /// <summary>The production gate: a frame was already produced for this compositor tick.</summary>
    Gated = 6,
}

/// <summary>Per-UI-frame flags (<see cref="LedgerUiFrame.Flags"/>).</summary>
[Flags]
public enum LedgerUiFlags : ushort
{
    None = 0,
    /// <summary>The frame reconciled or laid out (<see cref="FrameStats.Rendered"/>).</summary>
    Rendered = 1,
    /// <summary>The frame was handed on for presentation: submitted inline, or published to the render thread.</summary>
    Presented = 2,
    /// <summary>Every slice was kept: the render side only re-placed retained pixels.</summary>
    CompositeOnly = 4,
    ScrollActive = 8,
    /// <summary>Paint ran inside this RunFrame (its phase stamps are set).</summary>
    Painted = 16,
    /// <summary>The wait before this frame asked for no timeout (blocked until a message).</summary>
    WaitInfinite = 32,
}

/// <summary>What the render thread's present decision did with one turn (<see cref="LedgerRenderTurn.Kind"/>).</summary>
public enum LedgerTurnKind : byte
{
    /// <summary>Nothing for the primary: a bare wake (a child drain, a quiesce nudge, child-only motion).</summary>
    Bare = 0,
    /// <summary>A fresh UI publication was presented.</summary>
    Fresh = 1,
    /// <summary>The retained scene was re-posed and presented (render-side motion).</summary>
    Motion = 2,
    /// <summary>This compositor tick was already presented for: the work waits for the next one.</summary>
    TickSpent = 3,
    /// <summary>The previous present missed its vblank and owns this one (<see cref="Threading.SlotCatchUp"/>).</summary>
    CatchUp = 4,
    /// <summary>The UI asked to park while the present slot was being waited for.</summary>
    Parked = 5,
}

/// <summary>What the host's submit did inside a presented turn (<see cref="LedgerRenderTurn.Outcome"/>).</summary>
public enum LedgerTurnOutcome : byte
{
    None = 0,
    /// <summary>A clock turn whose poses did not move: no record, no submit.</summary>
    Elided = 1,
    /// <summary>Only slice poses moved: the retained slices were re-placed, nothing recorded.</summary>
    CompositeOnly = 2,
    /// <summary>The scene was recorded and submitted.</summary>
    Recorded = 3,
    /// <summary>Recorded, but byte-identical with an empty repaint set: submit and Present elided.</summary>
    SkipSubmit = 4,
    /// <summary>A raw draw-list submit (no scene capture).</summary>
    Direct = 5,
}

/// <summary>Render-turn flags (<see cref="LedgerRenderTurn.Flags"/>).</summary>
[Flags]
public enum LedgerTurnFlags : byte
{
    None = 0,
    /// <summary>The repaint set was forced full (<see cref="LedgerRenderTurn.FullReason"/> names why).</summary>
    WholeFrame = 1,
    /// <summary>The turn called Present on the primary swapchain.</summary>
    Presented = 2,
}

/// <summary>One <see cref="AppHost.RunFrame"/> of the ledger's owning host, early-outs included.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LedgerUiFrame
{
    /// <summary>Ledger ordinal (0-based, monotonic across the run).</summary>
    public ulong Seq;
    /// <summary>RunFrame entry / the message pump returned / RunFrame return.</summary>
    public long StartQpc, PumpQpc, EndQpc;
    /// <summary>Paint's phase stamps: frame start, after the reactive flush + reconcile, after layout, after the animation
    /// phase, after the record, after the submit / publish. 0 when Paint did not run.</summary>
    public long PaintQpc, FlushQpc, LayoutQpc, AnimQpc, RecordQpc, SubmitQpc;
    /// <summary>The platform loop's wait BEFORE this frame (<see cref="AppHost.NoteLoopWait"/>); 0 = none noted.</summary>
    public long WaitStartQpc, WaitEndQpc;
    /// <summary>The timeout that wait asked for (ms; -1 = infinite).</summary>
    public int WaitRequestedMs;
    /// <summary><see cref="HostWaitKind"/> of that wait.</summary>
    public byte WaitKind;
    /// <summary><see cref="LedgerFrameExit"/>.</summary>
    public byte Exit;
    /// <summary><see cref="LedgerUiFlags"/>.</summary>
    public ushort Flags;
    /// <summary>The publish sequence this frame handed the render thread (0 = none) - the join key to <see cref="LedgerRenderTurn.PublishSeq"/>.</summary>
    public ulong PublishSeq;
    /// <summary>UI-thread cycles inside RunFrame, and the thread's cumulative count at its end.</summary>
    public ulong UiCycles, UiCyclesTotal;
    /// <summary>The process's cumulative cycles (every thread) at the frame's end.</summary>
    public ulong ProcessCyclesTotal;
    /// <summary>Bytes the UI thread allocated inside RunFrame, and its cumulative count at the end.</summary>
    public long AllocBytes, AllocBytesTotal;
    /// <summary>Cumulative GC pause time (100 ns ticks, <c>GC.GetTotalPauseDuration</c>) and collection counts at the end.</summary>
    public long GcPauseTicksTotal;
    public int Gc0Total, Gc1Total, Gc2Total;
    /// <summary>Record work: nodes visited, nodes drawn, slices, components re-rendered.</summary>
    public int Nodes, DrawNodes, Slices, Components;
    /// <summary>The published repaint set's coverage of the window (0..1; 1 when forced full) and its rect count.</summary>
    public float DamageCoverage;
    public ushort DamageRects;
    /// <summary><see cref="RepaintFullReason"/> of the published repaint set.</summary>
    public byte FullReason;
    public byte Reserved;
    /// <summary>The host's cumulative successful presents and missed vsyncs at the frame's end.</summary>
    public long PresentedTotal, MissedVsyncsTotal;
    /// <summary>The <see cref="WakeReasons"/> the frame's idle decision saw (0 when an earlier gate stopped it) — the wake census.</summary>
    public uint WakeMask;
    public uint Reserved2;
    /// <summary>The UI thread's cumulative CPU time at the frame's end (GetThreadTimes kernel + user, 100 ns; 0 = no platform source).</summary>
    public long UiCpuTimeTotal;
}

/// <summary>One turn of the owning host's render thread (only turns that passed the wait: resize parks and device-loss
/// recoveries are not turns).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LedgerRenderTurn
{
    public ulong Seq;
    /// <summary>The loop began waiting / the turn began (the wait returned) / the present slot was held (0 = none taken) /
    /// the present returned (0 = nothing presented) / the turn ended (post-turn commit done).</summary>
    public long WaitStartQpc, StartQpc, SlotOpenQpc, DoneQpc, EndQpc;
    /// <summary>The compositor tick the turn decided for (0 = unpaced).</summary>
    public long TickSeq, TickQpc;
    /// <summary>The fresh publication presented (0 = a motion re-present or nothing).</summary>
    public ulong PublishSeq;
    /// <summary>The primary swapchain's submit count after the turn (0 = unknown) - the join key to <see cref="LedgerGpuFrame.SubmitSeq"/>.</summary>
    public ulong SubmitSeq;
    /// <summary>Render-thread cycles inside the turn, and the thread's cumulative count at its end.</summary>
    public ulong Cycles, CyclesTotal;
    public long AllocBytes;
    /// <summary><see cref="Threading.PresentSplit"/> of a presented turn (ms).</summary>
    public float StageMs, RecordMs, SubmitMs, FenceMs, LatencyMs, PresentMs, VideoMs;
    /// <summary>The submitted repaint set's coverage of the target (0..1; 1 when forced full).</summary>
    public float DamageCoverage;
    public int TilesRastered;
    /// <summary>Compositor ticks this present skipped (live motion only).</summary>
    public int MissedTicks;
    /// <summary><see cref="LedgerTurnKind"/>, <see cref="LedgerTurnOutcome"/>, <see cref="LedgerTurnFlags"/>, <see cref="RepaintFullReason"/>.</summary>
    public byte Kind, Outcome, Flags, FullReason;
    /// <summary>The render thread's cumulative CPU time at the turn's end (GetThreadTimes, 100 ns; 0 = no platform source).</summary>
    public long CpuTimeTotal;
}

/// <summary>One retired whole-frame GPU timestamp pair of the owning host's swapchain, with the pass timeline of the same
/// submit when <see cref="IGpuDevice.GpuPassTimingEnabled"/> was on.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LedgerGpuFrame
{
    public ulong Seq;
    /// <summary>When the ledger saw the sample (render thread) / when fence retirement made it readable.</summary>
    public long ObservedQpc, PublishedQpc;
    /// <summary>The GPU's begin/end timestamps mapped onto QPC (<c>ID3D12CommandQueue::GetClockCalibration</c>); 0 = unknown.</summary>
    public long GpuStartQpc, GpuEndQpc;
    /// <summary>The swapchain's sample sequence and the submit it measured.</summary>
    public ulong SampleSeq, SubmitSeq;
    /// <summary>Samples the ledger missed since the previous one (a sequence gap: more than one retired between two turns).</summary>
    public int MissedSamples;
    public float GpuMs;
    /// <summary>The pass timeline of the same submit: frame start to end, interval count, intervals folded past the cap.</summary>
    public float PassWholeMs;
    public int PassCount, PassesDropped;
    /// <summary>Summed interval time per <see cref="GpuPassKind"/> (ms).</summary>
    public float UploadsMs, BakedBlurMs, ClearMs, SceneMs, GlyphBandMs, TileRasterMs, OffscreenMs, CompositeMs;

    /// <summary>Add <paramref name="ms"/> to the accumulator of <paramref name="kind"/>.</summary>
    public void AddPassMs(GpuPassKind kind, float ms)
    {
        switch (kind)
        {
            case GpuPassKind.Uploads: UploadsMs += ms; break;
            case GpuPassKind.BakedBlur: BakedBlurMs += ms; break;
            case GpuPassKind.Clear: ClearMs += ms; break;
            case GpuPassKind.Scene: SceneMs += ms; break;
            case GpuPassKind.GlyphBand: GlyphBandMs += ms; break;
            case GpuPassKind.TileRaster: TileRasterMs += ms; break;
            case GpuPassKind.Offscreen: OffscreenMs += ms; break;
            default: CompositeMs += ms; break;
        }
    }
}

/// <summary>The memory census at <see cref="FrameLedger.MemoryIntervalMs"/> (default 4 Hz), taken on the ledger's own sampler
/// thread so an idle loop blocked in its wait is still sampled.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LedgerMemorySample
{
    public ulong Seq;
    public long Qpc;
    /// <summary>Process working set and private commit (<c>GetProcessMemoryInfo</c>; 0 without a platform sampler).</summary>
    public long WorkingSetBytes, PrivateBytes;
    /// <summary><c>GC.GetTotalMemory(false)</c>; the heap size and committed bytes of the last GC (<c>GC.GetGCMemoryInfo</c>,
    /// refreshed every fourth sample); <c>GC.GetTotalAllocatedBytes(false)</c>.</summary>
    public long ManagedBytes, GcHeapBytes, GcCommittedBytes, TotalAllocatedBytes;
    /// <summary>DXGI <c>QueryVideoMemoryInfo</c> usage (LOCAL / NON_LOCAL), the LOCAL budget, and the engine's tracked D3D12
    /// resource bytes (the render thread's last snapshot; 0 headless).</summary>
    public long VramLocalBytes, VramNonLocalBytes, VramLocalBudgetBytes, TrackedGpuBytes;
    /// <summary>The image cache's decoded bytes (originals + derived) and entry count; the glyph atlas's bytes.</summary>
    public long ImageCacheBytes;
    public int ImageCount;
    public int Reserved;
    public long GlyphAtlasBytes;
    /// <summary>The process's cumulative cycles and CPU time (kernel + user, 100 ns, <c>GetProcessTimes</c>).</summary>
    public ulong ProcessCyclesTotal;
    public long ProcessCpuTicksTotal;
    public long GcPauseTicksTotal;
    public int Gc0Total, Gc1Total, Gc2Total;
    public int Reserved2;
}

/// <summary>The audio health counters (<see cref="FluentGpu.Media.AudioHealth"/>) at the memory cadence: buffer-drained edges (the
/// endpoint buffer was found empty at a write after holding audio at the previous one) apart from app-side xruns (the feed
/// thread's ring ran empty). A drained edge says audio ran out at the endpoint; it does not say whose fault it was.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LedgerAudioSample
{
    public ulong Seq;
    public long Qpc;
    /// <summary>Cumulative: device writes, buffer-drained edges, app-side xrun incidents, frames those xruns lost.</summary>
    public long DeviceWritesTotal, BufferDrainedTotal, XrunsTotal, XrunFramesTotal;
    /// <summary>The lowest endpoint padding (queued frames) any write saw since the previous sample (-1 = no write).</summary>
    public int PaddingMinFrames;
    public int BufferFrames, Rate, Reserved;
}

/// <summary>What the platform fills in for a <see cref="LedgerMemorySample"/> (process memory and times, video memory).</summary>
public struct LedgerPlatformSample
{
    public long WorkingSetBytes, PrivateBytes, ProcessCpuTicksTotal;
    public long VramLocalBytes, VramNonLocalBytes, VramLocalBudgetBytes, TrackedGpuBytes, GlyphAtlasBytes;
}

/// <summary>The platform half of the memory sampler (installed by the Windows backend). Sampler thread; must not allocate.</summary>
public delegate void LedgerPlatformSampler(ref LedgerPlatformSample sample);

/// <summary>Ring positions at an instant (<see cref="FrameLedger.Mark"/>): a window is everything recorded after it.</summary>
public readonly record struct LedgerMark(long Ui, long Render, long Gpu, long Memory, long Qpc, long Audio = 0);
