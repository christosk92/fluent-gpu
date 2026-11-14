using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Media;

namespace FluentGpu.Rhi.D3D12;

// ── present stalls placed in time: always-on edge lines written the moment a long wait ENDS ─────────────────────────────────
// The pace line ([render.pace] ... blocker=fence|slot) says how long a present was blocked, but it is written up to a second
// later. These lines are written when the wait itself ends, so the log timestamp is the stall's end and wait= gives its start:
// - fence: the frame-fence wait (WaitForFenceValue), with how far behind the GPU was when the wait began;
// - slot:  the present-slot (frame latency waitable) wait (TryTakeLatencyCredit), only when the slot OPENED (a timed-out liveness
//          take has its own rate-limited line and runs out every turn while the window is hidden; an aborted one is a park);
// - gpu:   a retired whole-frame GPU timestamp pair (our own frame's GPU begin->end). A long fence wait with NO gpu line means our
//          commands sat queued behind someone else's work, not that they ran long.
// - frame fence: OpenSubmit's back-buffer + ring-slot waits together, from one refresh (FenceWaitNoteMs) up, at most one line a
//          second (suppressed= counts the waits skipped since the last line), split buffer=/ring= with the awaited slot's own
//          retired GPU timestamps: gpuQueuedMs large with gpuRunMs small = our commands sat queued behind other work on the GPU;
//          gpuRunMs large = our own frame ran long; sameSubmit=0 with buffer>0 = a back buffer reused before its own last submit
//          finished (a present that did not flip).
// Each carries the video census (bound surfaces, frosted plates over video). Threshold-gated edges: a healthy run prints none,
// and below the threshold nothing is built or allocated. None of them changes a wait, a timeout or the control flow.
public sealed unsafe partial class D3D12Device
{
    private const double PresentStallEdgeMs = 100.0;
    private const double FenceWaitNoteMs = 8.0;   // about one 120 Hz refresh: the pace line's 29/83 ms fence blocks sit below the edge above
    private static long s_lastFrameFenceNoteQpc;
    private static int s_frameFenceNotesSuppressed;

    // Render thread, after both OpenSubmit waits returned (the ring wait retired `slot`, so its whole-frame timestamps are
    // readable). Reads them WITHOUT consuming: CollectGpuExecutionTime still publishes and clears them in BeginRecording.
    private void NoteFrameFenceWait(D3D12Swapchain sc, TargetFrameState f, int slot, long startQpc, long bufferDoneQpc, long ringDoneQpc,
        ulong bufferFence, ulong ringFence, ulong completedAtEntry)
    {
        long now = Stopwatch.GetTimestamp();
        if (s_lastFrameFenceNoteQpc != 0 && now - s_lastFrameFenceNoteQpc < Stopwatch.Frequency) { s_frameFenceNotesSuppressed++; return; }
        int suppressed = s_frameFenceNotesSuppressed;
        s_lastFrameFenceNoteQpc = now;
        s_frameFenceNotesSuppressed = 0;
        double totalMs = Stopwatch.GetElapsedTime(startQpc, ringDoneQpc).TotalMilliseconds;
        double bufferMs = Stopwatch.GetElapsedTime(startQpc, bufferDoneQpc).TotalMilliseconds;
        double ringMs = Stopwatch.GetElapsedTime(bufferDoneQpc, ringDoneQpc).TotalMilliseconds;
        long submitQpc = _ring.SubmitQpc[slot];
        double submittedAgoMs = submitQpc == 0 ? -1.0 : Stopwatch.GetElapsedTime(submitQpc, now).TotalMilliseconds;
        double gpuQueuedMs = -1.0, gpuRunMs = -1.0;
        if (_gpuExecutionTsData != null && _gpuExecutionTsFreq != 0 && _ring.ExecTsPending[slot])
        {
            uint q = GpuExecutionTsPerFrame * (uint)slot;
            ulong begin = _gpuExecutionTsData[q], end = _gpuExecutionTsData[q + 1u];
            if (end > begin)
            {
                gpuRunMs = (end - begin) * 1000.0 / _gpuExecutionTsFreq;
                long beginQpc = GpuTicksToQpc(begin, now);   // 0 when the queue cannot calibrate
                if (beginQpc != 0 && submitQpc != 0) gpuQueuedMs = (beginQpc - submitQpc) * 1000.0 / Stopwatch.Frequency;
            }
        }
        int owner = (int?)_ring.Owner[slot]?.Ordinal ?? -1;
        Diag.Line($"[d3d12.present] frame fence wait={totalMs:F1}ms buffer={bufferMs:F1} ring={ringMs:F1} target={sc.Ordinal} backBuffer={f.FrameIndex} slot={slot} bufferFence={bufferFence} ringFence={ringFence} completedAtEntry={completedAtEntry} sameSubmit={(bufferFence == ringFence ? 1 : 0)} awaited(submit={_ring.OwnerSubmit[slot]} owner={owner} submittedAgoMs={submittedAgoMs:F1} gpuQueuedMs={gpuQueuedMs:F1} gpuRunMs={gpuRunMs:F1}) submitCounter={_ring.SubmitCounter} suppressed={suppressed} boundVideo={MediaCensus.BoundVideoSurfaces} plates={MediaCensus.PlatesOverVideo}");
    }

    private static void NoteFenceWaitEnd(long startQpc, ulong target, ulong completedAtEntry)
    {
        double ms = Stopwatch.GetElapsedTime(startQpc).TotalMilliseconds;
        if (ms < PresentStallEdgeMs) return;
        Diag.Line($"[d3d12.present] fence wait={ms:F1}ms target={target} completedAtEntry={completedAtEntry} behind={target - completedAtEntry} boundVideo={MediaCensus.BoundVideoSurfaces} plates={MediaCensus.PlatesOverVideo}");
    }

    private static void NoteSlotWaitEnd(D3D12Swapchain sc, LatencyWait wait)
    {
        if (wait != LatencyWait.Opened) return;
        double ms = sc.Frame.LastLatencyWaitMs;
        if (ms < PresentStallEdgeMs) return;
        double sinceLastSubmitMs = sc.Frame.LastSubmitQpc == 0 ? -1.0 : Stopwatch.GetElapsedTime(sc.Frame.LastSubmitQpc).TotalMilliseconds;
        Diag.Line($"[d3d12.present] slot wait={ms:F1}ms target={sc.Ordinal} sinceLastSubmitMs={sinceLastSubmitMs:F1} boundVideo={MediaCensus.BoundVideoSurfaces} plates={MediaCensus.PlatesOverVideo}");
    }

    private static void NoteGpuRun(double ms, ulong ownerSubmit)
    {
        if (ms < PresentStallEdgeMs) return;
        Diag.Line($"[d3d12.present] gpu run={ms:F1}ms submit={ownerSubmit}");
    }
}
