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
// Each carries the video census (bound surfaces, frosted plates over video). Threshold-gated edges: a healthy run prints none,
// and below the threshold nothing is built or allocated. None of them changes a wait, a timeout or the control flow.
public sealed unsafe partial class D3D12Device
{
    private const double PresentStallEdgeMs = 100.0;

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
