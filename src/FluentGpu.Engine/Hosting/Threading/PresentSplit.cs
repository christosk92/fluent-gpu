using System;
using System.Globalization;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Where the wall time of ONE present turn went on the render thread (F244). The <c>[render.pace]</c> line's
/// <c>work=</c> is "slot open to done" - the whole of <c>SubmitPresentOnRenderThread</c> - and a worst turn of 100-320 ms with
/// 5 ms of CPU (<c>run=</c>) and 3-9 ms of GPU says the thread was blocked somewhere in it, but not WHERE. This splits it, with
/// plain QPC stamps the host takes between the phases (no allocation, no syscall beyond the timestamp read):
/// <list type="bullet">
/// <item><see cref="StageMs"/> - the popup mailbox drain and the image-upload staging that open the turn.</item>
/// <item><see cref="RecordMs"/> - adopt/tick of the render animations and scroll pose, then the scene record (or the composite-only compose).</item>
/// <item><see cref="SubmitMs"/> - the CPU side of the device submit (command recording and queue submit), net of the two waits below.</item>
/// <item><see cref="FenceMs"/> - the back-buffer / ring-slot frame-fence wait inside the submit (the GPU still owns that buffer).</item>
/// <item><see cref="LatencyMs"/> - a frame-latency waitable wait paid INSIDE the submit (0 when the turn already held its credit).</item>
/// <item><see cref="PresentMs"/> - <c>IDXGISwapChain::Present</c> itself (DWM / the present queue / the driver).</item>
/// <item><see cref="VideoMs"/> - the video hole-punch drain that rides the turn (DComp placement and the settle hint).</item>
/// </list>
/// The present-slot take that precedes the turn (the <c>waitForLatency</c> of the plan) is the <c>slot=</c> figure the pace line
/// already carries; <see cref="Format"/> names the largest of all of them as <c>blocker=</c>, and whatever the phases do not
/// cover (a preempted thread, the feedback publish) as <c>other=</c>. A diagnostic only: never a pacing input.
/// </summary>
public readonly record struct PresentSplit(
    double StageMs, double RecordMs, double SubmitMs, double FenceMs, double LatencyMs, double PresentMs, double VideoMs)
{
    /// <summary>The sum of every measured phase (ms).</summary>
    public double TotalMs => StageMs + RecordMs + SubmitMs + FenceMs + LatencyMs + PresentMs + VideoMs;

    /// <summary>The <c>[render.pace]</c> worst-present section: every phase, the time the phases do not cover
    /// (<paramref name="workMs"/> minus their sum), and the largest of them plus the present-slot take
    /// (<paramref name="slotMs"/>) as <c>blocker=</c> (<c>none</c> when nothing was measured). Report cadence only.</summary>
    internal string Format(double slotMs, double workMs)
    {
        double other = Math.Max(0.0, workMs - TotalMs);
        string blocker = "none";
        double max = 0.0;
        Consider(ref blocker, ref max, "slot", slotMs);
        Consider(ref blocker, ref max, "stage", StageMs);
        Consider(ref blocker, ref max, "record", RecordMs);
        Consider(ref blocker, ref max, "submit", SubmitMs);
        Consider(ref blocker, ref max, "fence", FenceMs);
        Consider(ref blocker, ref max, "latency", LatencyMs);
        Consider(ref blocker, ref max, "present", PresentMs);
        Consider(ref blocker, ref max, "video", VideoMs);
        Consider(ref blocker, ref max, "other", other);
        return string.Create(CultureInfo.InvariantCulture,
            $"stage={StageMs:F2} rec={RecordMs:F2} sub={SubmitMs:F2} fence={FenceMs:F2} lat={LatencyMs:F2} pres={PresentMs:F2} video={VideoMs:F2} other={other:F2} blocker={blocker}");

        static void Consider(ref string blocker, ref double max, string name, double ms)
        {
            if (ms > max) { max = ms; blocker = name; }
        }
    }
}
