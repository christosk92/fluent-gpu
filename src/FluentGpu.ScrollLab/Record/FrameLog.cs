using System;
using System.Globalization;
using System.IO;
using FluentGpu.Hosting;
using FluentGpu.Rhi;

namespace FluentGpu.ScrollLab.Record;

/// <summary>
/// One UI frame of a session, from the host's always-on telemetry (no env var, nothing to enable): the render census
/// (<see cref="FrameStats.RenderCensus"/> — repaint damage, span reuse, capture, the device's per-submit counters), the
/// render thread's pacing snapshot (<see cref="AppHost.RenderPace"/> — CUMULATIVE counters, a reader differences two
/// samples), and — while "GPU passes" is on — the summary of the most recently retired pass-granular GPU timeline
/// (<see cref="AppHost.CopyGpuPassTimeline"/>). Unmanaged, so a session dumps and reloads it byte-for-byte.
/// </summary>
public readonly record struct FrameSample(
    long Qpc, float FrameMs,
    float RepaintCoverage, int SpansReused, long SliceBytesRecorded, int SpansReRecorded, int CapturedNodes, float RecordMs, float CaptureMs,
    RepaintRoute Route, int Draws, int PassBreaks, long UploadBytes, int FeatherItems, int OffscreenSurfaces,
    long FreshPresents, long MotionPresents, long SkippedTicks, long RaceHits, long MissedMotionTicks,
    double SlotWaitMaxMs, bool GovernorEngaged, double GpuExecutionMs,
    ulong PassSequence, float PassWholeMs, int PassCount);

/// <summary>The session's growable frame log: one <see cref="FrameSample"/> per UI frame plus, per sample,
/// <see cref="KindCount"/> per-<see cref="GpuPassKind"/> millisecond sums (0 when that frame carried no fresh pass
/// timeline).</summary>
public sealed class FrameLog
{
    public const int KindCount = (int)GpuPassKind.Composite + 1;

    private FrameSample[] _samples = new FrameSample[2048];
    private float[] _passMs = new float[2048 * KindCount];
    private int _count;

    public int Count => _count;

    public void Clear() => _count = 0;

    /// <summary>Appends one frame. <paramref name="kindMs"/> (length <see cref="KindCount"/>) is the fresh pass
    /// timeline's per-kind sums, or empty when this frame brought none.</summary>
    public void Append(long qpc, in FrameStats stats, in RenderPaceSnapshot pace, in GpuPassFrameSummary passes, ReadOnlySpan<float> kindMs)
    {
        if (_count == _samples.Length)
        {
            Array.Resize(ref _samples, _samples.Length * 2);
            Array.Resize(ref _passMs, _samples.Length * KindCount);
        }
        var c = stats.RenderCensus;
        var d = c.Device;
        _samples[_count] = new FrameSample(
            qpc, (float)stats.FrameMs,
            c.RepaintCoverage, c.SpansReused, c.SliceBytesRecorded, c.SpansReRecorded, c.CapturedNodes, c.RecordMs, c.CaptureMs,
            d.Route, d.Draws, d.PassBreaks, d.UploadBytes, d.FeatherItems, d.OffscreenSurfaces,
            pace.FreshPresents, pace.MotionPresents, pace.SkippedTicks, pace.RaceHits, pace.MissedMotionTicks,
            pace.SlotWaitMaxMs, pace.GovernorEngaged, pace.GpuExecutionMs,
            passes.Sequence, kindMs.IsEmpty ? 0f : passes.WholeMs, kindMs.IsEmpty ? 0 : passes.PassCount);
        var dst = _passMs.AsSpan(_count * KindCount, KindCount);
        if (kindMs.IsEmpty) dst.Clear();
        else kindMs.Slice(0, KindCount).CopyTo(dst);
        _count++;
    }

    public FrameSample[] Samples() => _samples.AsSpan(0, _count).ToArray();
    public float[] PassMs() => _passMs.AsSpan(0, _count * KindCount).ToArray();

    /// <summary><c>frames.csv</c>: one row per frame; time is ms since <paramref name="startQpc"/>.</summary>
    public static void WriteCsv(TextWriter w, FrameSample[] samples, float[] passMs, long startQpc, double qpcFrequency)
    {
        var ci = CultureInfo.InvariantCulture;
        w.Write("t_ms,frame_ms,repaint_coverage,spans_reused,slice_bytes_recorded,spans_rerecorded,captured_nodes,record_ms,capture_ms,"
              + "route,draws,pass_breaks,upload_bytes,feather_items,offscreen_surfaces,fresh_presents,motion_presents,skipped_ticks,"
              + "race_hits,missed_motion_ticks,slot_wait_max_ms,governor_engaged,gpu_execution_ms,pass_seq,pass_whole_ms,pass_count");
        for (int k = 0; k < KindCount; k++) w.Write(",pass_ms_" + ((GpuPassKind)k).ToString());
        w.WriteLine();
        for (int i = 0; i < samples.Length; i++)
        {
            ref readonly FrameSample s = ref samples[i];
            w.Write(((s.Qpc - startQpc) * 1000.0 / qpcFrequency).ToString("0.000", ci));
            w.Write(string.Join(',', "",
                s.FrameMs.ToString("0.###", ci), s.RepaintCoverage.ToString("0.####", ci),
                s.SpansReused.ToString(ci), s.SliceBytesRecorded.ToString(ci), s.SpansReRecorded.ToString(ci), s.CapturedNodes.ToString(ci),
                s.RecordMs.ToString("0.###", ci), s.CaptureMs.ToString("0.###", ci),
                s.Route.ToString(), s.Draws.ToString(ci), s.PassBreaks.ToString(ci), s.UploadBytes.ToString(ci),
                s.FeatherItems.ToString(ci), s.OffscreenSurfaces.ToString(ci),
                s.FreshPresents.ToString(ci), s.MotionPresents.ToString(ci), s.SkippedTicks.ToString(ci), s.RaceHits.ToString(ci),
                s.MissedMotionTicks.ToString(ci), s.SlotWaitMaxMs.ToString("0.###", ci), s.GovernorEngaged ? "1" : "0",
                s.GpuExecutionMs.ToString("0.###", ci), s.PassSequence.ToString(ci), s.PassWholeMs.ToString("0.###", ci),
                s.PassCount.ToString(ci)));
            for (int k = 0; k < KindCount; k++)
            {
                w.Write(',');
                int at = i * KindCount + k;
                w.Write(at < passMs.Length ? passMs[at].ToString("0.###", ci) : "0");
            }
            w.WriteLine();
        }
    }
}
