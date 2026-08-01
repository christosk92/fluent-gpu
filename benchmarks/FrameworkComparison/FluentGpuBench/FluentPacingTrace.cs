using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Bench.Contracts;
using FluentGpu.Hosting;
using FluentGpu.Rhi;

namespace FluentGpuBench;

/// <summary>
/// Optional, preallocated pacing sample ring for the raw FluentGPU host. Zero managed allocation after
/// <see cref="Begin"/> — correlate mutation/frame-ID with publish, present-ack, UI phases, DXGI/DWM stats, and
/// phase-gate ceiling escapes. Enable with <c>--pacing-trace path.jsonl</c>.
/// </summary>
internal static class FluentPacingTrace
{
    private static Sample[]? _samples;
    private static int _count;
    private static string? _path;
    private static long _gatedAtBegin;
    private static long _escapesAtBegin;

    [StructLayout(LayoutKind.Sequential)]
    private struct Sample
    {
        public long MutationQpc;
        public long PresentAckQpc;
        public int FrameId;
        public ulong PublishSeq;
        public ulong PresentAckSeq;
        public double FlushMs;
        public double LayoutMs;
        public double AnimMs;
        public double RecordMs;
        public double SubmitMs;
        public double FenceWaitMs;
        public double PresentMs;
        public double CpuWorkMs;
        public uint PresentRefreshCount;
        public uint SyncRefreshCount;
        public long RefreshPeriodQpc;
        public uint DwmDroppedDelta;
        public uint DwmMissedDelta;
        public uint DwmLateDelta;
        public double LatencyWaitMs;
        public long PhaseGatedFramesDelta;
        public long PhaseGateCeilingEscapesDelta;
        public byte PresentStatsValid;
    }

    internal static void Begin(string? path, int capacity, AppHost host)
    {
        if (string.IsNullOrWhiteSpace(path) || capacity <= 0) { _samples = null; _path = null; return; }
        _path = Path.GetFullPath(path);
        _samples = new Sample[capacity];
        _count = 0;
        _gatedAtBegin = host.PhaseGatedFrames;
        _escapesAtBegin = host.PhaseGateCeilingEscapes;
    }

    internal static void Record(
        int frameId,
        long mutationQpc,
        long presentAckQpc,
        AppHost host,
        IGpuDevice device,
        in FrameStats stats,
        double cpuWorkMs)
    {
        Sample[]? samples = _samples;
        if (samples is null || _count >= samples.Length) return;

        PresentStats ps = device.LastPresentStats;
        samples[_count++] = new Sample
        {
            MutationQpc = mutationQpc,
            PresentAckQpc = presentAckQpc,
            FrameId = frameId,
            PublishSeq = host.PublishSequence,
            PresentAckSeq = host.LastPresentPublishSeq,
            FlushMs = stats.FlushMs,
            LayoutMs = stats.LayoutMs,
            AnimMs = stats.AnimMs,
            RecordMs = stats.RecordMs,
            SubmitMs = stats.SubmitMs,
            FenceWaitMs = stats.FenceWaitMs,
            PresentMs = stats.PresentMs,
            CpuWorkMs = cpuWorkMs,
            PresentRefreshCount = ps.PresentRefreshCount,
            SyncRefreshCount = ps.SyncRefreshCount,
            RefreshPeriodQpc = ps.RefreshPeriodQpc,
            DwmDroppedDelta = ps.DwmFramesDroppedDelta,
            DwmMissedDelta = ps.DwmFramesMissedDelta,
            DwmLateDelta = ps.DwmFramesLateDelta,
            LatencyWaitMs = ps.LatencyWaitMs,
            PhaseGatedFramesDelta = host.PhaseGatedFrames - _gatedAtBegin,
            PhaseGateCeilingEscapesDelta = host.PhaseGateCeilingEscapes - _escapesAtBegin,
            PresentStatsValid = ps.Valid ? (byte)1 : (byte)0,
        };
    }

    internal static void WriteIfEnabled()
    {
        if (_path is null || _samples is null || _count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var writer = new StreamWriter(_path, append: false);
        writer.NewLine = "\n";
        for (int i = 0; i < _count; i++)
        {
            Sample s = _samples[i];
            writer.Write("{\"frameId\":");
            writer.Write(s.FrameId.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"mutationQpc\":");
            writer.Write(s.MutationQpc.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"presentAckQpc\":");
            writer.Write(s.PresentAckQpc.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"mutationToAckMs\":");
            writer.Write(((s.PresentAckQpc - s.MutationQpc) * 1000d / Stopwatch.Frequency).ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"publishSeq\":");
            writer.Write(s.PublishSeq.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"presentAckSeq\":");
            writer.Write(s.PresentAckSeq.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"flushMs\":");
            writer.Write(s.FlushMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"layoutMs\":");
            writer.Write(s.LayoutMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"animMs\":");
            writer.Write(s.AnimMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"recordMs\":");
            writer.Write(s.RecordMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"submitMs\":");
            writer.Write(s.SubmitMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"fenceWaitMs\":");
            writer.Write(s.FenceWaitMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"presentMs\":");
            writer.Write(s.PresentMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"cpuWorkMs\":");
            writer.Write(s.CpuWorkMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"presentRefreshCount\":");
            writer.Write(s.PresentRefreshCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"syncRefreshCount\":");
            writer.Write(s.SyncRefreshCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"refreshPeriodQpc\":");
            writer.Write(s.RefreshPeriodQpc.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"dwmDroppedDelta\":");
            writer.Write(s.DwmDroppedDelta.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"dwmMissedDelta\":");
            writer.Write(s.DwmMissedDelta.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"dwmLateDelta\":");
            writer.Write(s.DwmLateDelta.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"latencyWaitMs\":");
            writer.Write(s.LatencyWaitMs.ToString("0.####", CultureInfo.InvariantCulture));
            writer.Write(",\"phaseGatedFramesDelta\":");
            writer.Write(s.PhaseGatedFramesDelta.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"phaseGateCeilingEscapesDelta\":");
            writer.Write(s.PhaseGateCeilingEscapesDelta.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"presentStatsValid\":");
            writer.Write(s.PresentStatsValid != 0 ? "true" : "false");
            writer.Write('}');
            writer.WriteLine();
        }
    }
}
