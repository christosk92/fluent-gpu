using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Media;
using TerraFX.Interop.DirectX;

namespace FluentGpu.Rhi.D3D12;

// ── untracked GPU memory: the adapter's LOCAL usage minus every resource and heap this device tracks ───────────────────────
// What the driver and Media Foundation allocate outside our ledger (command allocators, pipelines, descriptor and query heaps, and
// the video swap chains MF builds and frees on its own). The owner's log shows two events nothing of ours explains: a GPU stall
// ~10 s after an MF engine is paused, and about 268 MiB leaving while the UI idles over a full-screen video (and coming back when
// the chrome shows). Both happen with no UI present to sample on, and a fall was never logged, so:
// - a change of more than 8 MiB between two samples is logged either way, signed, edges only (never per frame);
// - the UI present path samples on its cold cadence as before (every 60th present, every 10th on a weak part);
// - while any video surface is bound (MediaCensus.BoundVideoSurfaces), a 250 ms timer samples too, so an idle UI is no blind spot.
//   It holds its own reference on the adapter, exists only while a surface is bound (created on the present that sees the first
//   one, gone at the first tick or present after the last one), and its tick allocates nothing.
// Each line says how long the UI had not presented (sinceUiPresentMs: an idle UI over the video, or a stall) and how many frosted
// chrome plates were composed over the video (plates: each one a blurred copy of the picture under it).
public sealed unsafe partial class D3D12Device
{
    private const int UntrackedVideoSampleMs = 250;
    private const long UntrackedEdgeBytes = 8L << 20;

    private readonly Lock _untrackedGate = new();
    private long _lastUntracked = long.MinValue;          // under _untrackedGate
    private long _lastUiPresentQpc, _prevUiPresentQpc;    // render thread writes; the timer reads the last one
    private Timer? _untrackedTimer;                       // under _untrackedGate (read without it only to skip the lock)
    private IDXGIAdapter3* _untrackedAdapter;             // the timer's own reference, under _untrackedGate
    private TimerCallback? _untrackedTick;

    /// <summary>Render thread, every UI present (and the bring-up sample): stamp the present and start or stop the video-time
    /// sampler on the bound-surface edge. Lock-free unless the edge moved.</summary>
    private void NoteUiPresentForUntracked()
    {
        _prevUiPresentQpc = _lastUiPresentQpc;
        Volatile.Write(ref _lastUiPresentQpc, Stopwatch.GetTimestamp());
        bool want = MediaCensus.BoundVideoSurfaces > 0;
        if (want == (Volatile.Read(ref _untrackedTimer) is not null)) return;
        lock (_untrackedGate)
        {
            if (!want) { StopUntrackedTimerLocked(); return; }
            if (_untrackedTimer is not null) return;
            EnsureAdapter3();
            if (_adapter3 == null) return;
            _adapter3->AddRef();
            _untrackedAdapter = _adapter3;
            _untrackedTick ??= UntrackedTick;
            _untrackedTimer = new Timer(_untrackedTick, null, UntrackedVideoSampleMs, UntrackedVideoSampleMs);
        }
    }

    private void StopUntrackedTimerLocked()
    {
        if (_untrackedTimer is null) return;
        _untrackedTimer.Dispose();
        _untrackedTimer = null;
        if (_untrackedAdapter != null) { _untrackedAdapter->Release(); _untrackedAdapter = null; }
    }

    // Thread pool, every 250 ms while a video surface is bound.
    private void UntrackedTick(object? _)
    {
        lock (_untrackedGate)
        {
            if (_untrackedTimer is null || _untrackedAdapter == null) return;
            if (MediaCensus.BoundVideoSurfaces <= 0) { StopUntrackedTimerLocked(); return; }
            DXGI_QUERY_VIDEO_MEMORY_INFO local = default;
            if ((int)_untrackedAdapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local) < 0) return;
            long untracked = (long)local.CurrentUsage - D3D12MemoryDiagnostics.LiveTotals().bytes;
            long delta = TakeUntrackedEdgeLocked(untracked);
            if (delta == 0) return;
            Diag.Line($"[d3d12.untracked] {delta / 1048576.0:+0.0;-0.0} MiB now {untracked / 1048576.0:0.0} MiB" +
                $" sinceUiPresentMs={MsSinceQpc(Volatile.Read(ref _lastUiPresentQpc))} plates={MediaCensus.PlatesOverVideo} via=timer");
        }
    }

    /// <summary>Render thread, the UI present path's sample (<see cref="PublishVideoMemorySnapshot"/>): the same edge, with what the
    /// device did in that frame.</summary>
    private void NoteUntrackedGrowth(long localUsage)
    {
        long untracked = localUsage - D3D12MemoryDiagnostics.LiveTotals().bytes;
        long delta;
        lock (_untrackedGate) delta = TakeUntrackedEdgeLocked(untracked);
        if (delta == 0) return;
        Diag.Line($"[d3d12.untracked] {delta / 1048576.0:+0.0;-0.0} MiB now {untracked / 1048576.0:0.0} MiB" +
            $" sinceUiPresentMs={MsSinceQpc(_prevUiPresentQpc)} plates={MediaCensus.PlatesOverVideo} via=present" +
            $" tilesRastered={_frameTilesRastered} passes={_frameRenderPasses} segments={_frameSegments} inlineItems={_frameInlineItems} inlineReopens={_frameInlineReopens} resumes={_frameResumes} inlineGroups={_frameInlineGroups} directRegions={_frameDirectRegions} passDrawsMax={_framePassDrawsMax} passesOver80={_framePassesOverBinning} inlineByKind={string.Join("/", _frameInlineByKind)} inlineBlurred={_frameInlineBlurred} sites={PassSitesLine()}");
    }

    // The change since the previous sample when it is an edge (more than 8 MiB either way), else 0; the sample becomes the new
    // reference either way (a slow drift is not an edge).
    private long TakeUntrackedEdgeLocked(long untracked)
    {
        long last = _lastUntracked;
        _lastUntracked = untracked;
        if (last == long.MinValue) return 0;
        long delta = untracked - last;
        return delta > UntrackedEdgeBytes || delta < -UntrackedEdgeBytes ? delta : 0;
    }

    private static long MsSinceQpc(long qpc)
        => qpc == 0 ? -1 : (Stopwatch.GetTimestamp() - qpc) * 1000 / Stopwatch.Frequency;
}
