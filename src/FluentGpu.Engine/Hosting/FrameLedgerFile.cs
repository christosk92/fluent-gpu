using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentGpu.Hosting;

/// <summary>A copy of the ledger's four streams over a window (<see cref="FrameLedger.Snapshot"/>), with what it takes to read
/// them: the QPC frequency, the calibrated cycles-per-ms and the window origin. Written as one compact binary file
/// (<see cref="FrameLedgerFile"/>: a header + the records' raw bytes) and exported as one CSV per stream.</summary>
public sealed class LedgerSnapshot
{
    public long QpcFrequency { get; init; }
    public double CyclesPerMs { get; init; }
    public int Processors { get; init; }
    /// <summary>The window's start (QPC): CSV times are milliseconds since it.</summary>
    public long OriginQpc { get; init; }
    /// <summary>When the snapshot was taken (QPC): the window's end.</summary>
    public long EndQpc { get; init; }
    public LedgerUiFrame[] Ui { get; init; } = [];
    public LedgerRenderTurn[] Render { get; init; } = [];
    public LedgerGpuFrame[] Gpu { get; init; } = [];
    public LedgerMemorySample[] Memory { get; init; } = [];
    public LedgerAudioSample[] Audio { get; init; } = [];

    /// <summary>QPC → ms since <see cref="OriginQpc"/> (NaN for an unset stamp).</summary>
    public double Ms(long qpc) => qpc == 0 ? double.NaN : (qpc - OriginQpc) * 1000.0 / QpcFrequency;

    /// <summary>A QPC span in ms.</summary>
    public double SpanMs(long fromQpc, long toQpc) => fromQpc == 0 || toQpc == 0 ? double.NaN : (toQpc - fromQpc) * 1000.0 / QpcFrequency;

    /// <summary>A cycle count in ms at <see cref="CyclesPerMs"/> (NaN without a calibrated rate).</summary>
    public double CyclesMs(ulong cycles) => CyclesPerMs > 0 ? cycles / CyclesPerMs : double.NaN;

    /// <summary>The same records read at another cycles-per-ms (a run applies ONE rate to every window it compares).</summary>
    public LedgerSnapshot WithRate(double cyclesPerMs) => new()
    {
        QpcFrequency = QpcFrequency, CyclesPerMs = cyclesPerMs, Processors = Processors, OriginQpc = OriginQpc, EndQpc = EndQpc,
        Ui = Ui, Render = Render, Gpu = Gpu, Memory = Memory, Audio = Audio,
    };

    /// <summary>The process's cycles per millisecond of process CPU time over this window's memory samples (Δ QueryProcessCycleTime /
    /// Δ GetProcessTimes): the counter's EFFECTIVE rate at the clocks the cores actually ran at. NaN under
    /// <paramref name="minCpuMs"/> of CPU time (the tick-accounted denominator is too coarse below it).</summary>
    public double EffectiveCyclesPerMs(double minCpuMs = 1000) => FrameLedger.RateBetween(Memory, minCpuMs);

    /// <summary>The window's wall time (ms).</summary>
    public double WallMs => (EndQpc - OriginQpc) * 1000.0 / QpcFrequency;

    public void WriteFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        FrameLedgerFile.Write(fs, this);
    }

    public static LedgerSnapshot ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return FrameLedgerFile.Read(fs);
    }

    /// <summary>Write <c>{prefix}-ui.csv</c>, <c>-render.csv</c>, <c>-gpu.csv</c>, <c>-memory.csv</c>, <c>-audio.csv</c> into <paramref name="directory"/>.</summary>
    public void WriteCsv(string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, prefix + "-ui.csv"), FrameLedgerCsv.Ui(this));
        File.WriteAllText(Path.Combine(directory, prefix + "-render.csv"), FrameLedgerCsv.Render(this));
        File.WriteAllText(Path.Combine(directory, prefix + "-gpu.csv"), FrameLedgerCsv.Gpu(this));
        File.WriteAllText(Path.Combine(directory, prefix + "-memory.csv"), FrameLedgerCsv.Memory(this));
        File.WriteAllText(Path.Combine(directory, prefix + "-audio.csv"), FrameLedgerCsv.Audio(this));
    }
}

/// <summary>The ledger's binary file: <c>"FGLEDGER"</c>, a version, the snapshot's scalars, each stream's record size and count
/// (a reader refuses a file whose record sizes differ from its own structs), then the four record arrays' raw bytes,
/// little-endian as they sit in memory. Compact (no text) and lossless; <see cref="LedgerSnapshot.WriteCsv"/> is the readable form.</summary>
public static class FrameLedgerFile
{
    public const int Version = 2;
    private static ReadOnlySpan<byte> Magic => "FGLEDGER"u8;

    public static void Write(Stream s, LedgerSnapshot snap)
    {
        using var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(snap.QpcFrequency);
        w.Write(snap.CyclesPerMs);
        w.Write(snap.Processors);
        w.Write(snap.OriginQpc);
        w.Write(snap.EndQpc);
        Header<LedgerUiFrame>(w, snap.Ui.Length);
        Header<LedgerRenderTurn>(w, snap.Render.Length);
        Header<LedgerGpuFrame>(w, snap.Gpu.Length);
        Header<LedgerMemorySample>(w, snap.Memory.Length);
        Header<LedgerAudioSample>(w, snap.Audio.Length);
        w.Flush();
        s.Write(MemoryMarshal.AsBytes(snap.Ui.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(snap.Render.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(snap.Gpu.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(snap.Memory.AsSpan()));
        s.Write(MemoryMarshal.AsBytes(snap.Audio.AsSpan()));
    }

    public static LedgerSnapshot Read(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        if (!r.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("not a frame ledger file");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"frame ledger version {version}, this reader is {Version}");
        long freq = r.ReadInt64();
        double cpm = r.ReadDouble();
        int processors = r.ReadInt32();
        long origin = r.ReadInt64(), end = r.ReadInt64();
        int nUi = Count<LedgerUiFrame>(r), nRender = Count<LedgerRenderTurn>(r), nGpu = Count<LedgerGpuFrame>(r), nMem = Count<LedgerMemorySample>(r),
            nAudio = Count<LedgerAudioSample>(r);
        return new LedgerSnapshot
        {
            QpcFrequency = freq, CyclesPerMs = cpm, Processors = processors, OriginQpc = origin, EndQpc = end,
            Ui = Records<LedgerUiFrame>(s, nUi), Render = Records<LedgerRenderTurn>(s, nRender),
            Gpu = Records<LedgerGpuFrame>(s, nGpu), Memory = Records<LedgerMemorySample>(s, nMem), Audio = Records<LedgerAudioSample>(s, nAudio),
        };
    }

    private static void Header<T>(BinaryWriter w, int count) where T : unmanaged
    {
        w.Write(Unsafe.SizeOf<T>());
        w.Write(count);
    }

    private static int Count<T>(BinaryReader r) where T : unmanaged
    {
        int size = r.ReadInt32(), count = r.ReadInt32();
        if (size != Unsafe.SizeOf<T>()) throw new InvalidDataException($"{typeof(T).Name} is {size} bytes in the file, {Unsafe.SizeOf<T>()} here");
        if (count < 0) throw new InvalidDataException("negative record count");
        return count;
    }

    private static T[] Records<T>(Stream s, int count) where T : unmanaged
    {
        var a = new T[count];
        s.ReadExactly(MemoryMarshal.AsBytes(a.AsSpan()));
        return a;
    }
}

/// <summary>One CSV per ledger stream. Times are ms since the snapshot origin (3 decimals), CPU is ms at the calibrated rate,
/// bytes stay bytes; sequence numbers are raw so the streams join (ui.publishSeq = render.publishSeq, render.submitSeq =
/// gpu.submitSeq).</summary>
public static class FrameLedgerCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Ui(LedgerSnapshot s)
    {
        var sb = new StringBuilder(64 + s.Ui.Length * 220);
        sb.AppendLine("seq,startMs,pumpMs,paintMs,flushMs,layoutMs,animMs,recordMs,submitMs,endMs,frameMs,waitStartMs,waitMs,waitRequestedMs,waitKind,exit,flags,publishSeq,uiCpuMs,processCpuMs,allocBytes,gc0,gc1,gc2,gcPauseMs,nodes,drawNodes,slices,components,damageCoverage,damageRects,fullReason,presentedTotal,missedVsyncsTotal,wakeMask,uiCpuTimeMs");
        LedgerUiFrame prev = default;
        for (int i = 0; i < s.Ui.Length; i++)
        {
            ref readonly var r = ref s.Ui[i];
            // processCpuMs: the process's cycles between the previous frame's end and this one's (the whole interval, every thread).
            double procMs = i > 0 && r.ProcessCyclesTotal >= prev.ProcessCyclesTotal && prev.ProcessCyclesTotal != 0 ? s.CyclesMs(r.ProcessCyclesTotal - prev.ProcessCyclesTotal) : double.NaN;
            sb.Append(r.Seq).Append(',');
            T(sb, s.Ms(r.StartQpc)); T(sb, s.Ms(r.PumpQpc)); T(sb, s.Ms(r.PaintQpc)); T(sb, s.Ms(r.FlushQpc)); T(sb, s.Ms(r.LayoutQpc));
            T(sb, s.Ms(r.AnimQpc)); T(sb, s.Ms(r.RecordQpc)); T(sb, s.Ms(r.SubmitQpc)); T(sb, s.Ms(r.EndQpc)); T(sb, s.SpanMs(r.StartQpc, r.EndQpc));
            T(sb, s.Ms(r.WaitStartQpc)); T(sb, s.SpanMs(r.WaitStartQpc, r.WaitEndQpc));
            sb.Append(r.WaitRequestedMs).Append(',').Append(((HostWaitKind)r.WaitKind).ToString()).Append(',')
              .Append(((LedgerFrameExit)r.Exit).ToString()).Append(',').Append(r.Flags).Append(',').Append(r.PublishSeq).Append(',');
            T(sb, s.CyclesMs(r.UiCycles)); T(sb, procMs);
            sb.Append(r.AllocBytes).Append(',');
            sb.Append(i > 0 ? r.Gc0Total - prev.Gc0Total : 0).Append(',').Append(i > 0 ? r.Gc1Total - prev.Gc1Total : 0).Append(',')
              .Append(i > 0 ? r.Gc2Total - prev.Gc2Total : 0).Append(',');
            T(sb, i > 0 ? (r.GcPauseTicksTotal - prev.GcPauseTicksTotal) / 10_000.0 : 0);
            sb.Append(r.Nodes).Append(',').Append(r.DrawNodes).Append(',').Append(r.Slices).Append(',').Append(r.Components).Append(',');
            T(sb, r.DamageCoverage);
            sb.Append(r.DamageRects).Append(',').Append(((Rhi.RepaintFullReason)r.FullReason).ToString()).Append(',')
              .Append(r.PresentedTotal).Append(',').Append(r.MissedVsyncsTotal).Append(',').Append("0x").Append(r.WakeMask.ToString("X", Inv)).Append(',');
            T(sb, r.UiCpuTimeTotal / 10_000.0, last: true);
            sb.AppendLine();
            prev = r;
        }
        return sb.ToString();
    }

    public static string Render(LedgerSnapshot s)
    {
        var sb = new StringBuilder(64 + s.Render.Length * 200);
        sb.AppendLine("seq,waitStartMs,startMs,slotOpenMs,doneMs,endMs,turnMs,slotWaitMs,workMs,tickSeq,tickMs,kind,outcome,flags,publishSeq,submitSeq,cpuMs,allocBytes,stageMs,recMs,subMs,fenceMs,latMs,presMs,videoMs,damageCoverage,fullReason,tilesRastered,missedTicks,cpuTimeMs");
        foreach (ref readonly var r in s.Render.AsSpan())
        {
            sb.Append(r.Seq).Append(',');
            T(sb, s.Ms(r.WaitStartQpc)); T(sb, s.Ms(r.StartQpc)); T(sb, s.Ms(r.SlotOpenQpc)); T(sb, s.Ms(r.DoneQpc)); T(sb, s.Ms(r.EndQpc));
            T(sb, s.SpanMs(r.StartQpc, r.EndQpc)); T(sb, s.SpanMs(r.StartQpc, r.SlotOpenQpc)); T(sb, s.SpanMs(r.SlotOpenQpc, r.DoneQpc));
            sb.Append(r.TickSeq).Append(','); T(sb, s.Ms(r.TickQpc));
            sb.Append(((LedgerTurnKind)r.Kind).ToString()).Append(',').Append(((LedgerTurnOutcome)r.Outcome).ToString()).Append(',')
              .Append(r.Flags).Append(',').Append(r.PublishSeq).Append(',').Append(r.SubmitSeq).Append(',');
            T(sb, s.CyclesMs(r.Cycles));
            sb.Append(r.AllocBytes).Append(',');
            T(sb, r.StageMs); T(sb, r.RecordMs); T(sb, r.SubmitMs); T(sb, r.FenceMs); T(sb, r.LatencyMs); T(sb, r.PresentMs); T(sb, r.VideoMs);
            T(sb, r.DamageCoverage);
            sb.Append(((Rhi.RepaintFullReason)r.FullReason).ToString()).Append(',').Append(r.TilesRastered).Append(',').Append(r.MissedTicks).Append(',');
            T(sb, r.CpuTimeTotal / 10_000.0, last: true);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Gpu(LedgerSnapshot s)
    {
        var sb = new StringBuilder(64 + s.Gpu.Length * 160);
        sb.AppendLine("seq,observedMs,publishedMs,gpuStartMs,gpuEndMs,sampleSeq,submitSeq,missedSamples,gpuMs,passWholeMs,passCount,passesDropped,uploadsMs,bakedBlurMs,clearMs,sceneMs,glyphBandMs,tileRasterMs,offscreenMs,compositeMs");
        foreach (ref readonly var r in s.Gpu.AsSpan())
        {
            sb.Append(r.Seq).Append(',');
            T(sb, s.Ms(r.ObservedQpc)); T(sb, s.Ms(r.PublishedQpc)); T(sb, s.Ms(r.GpuStartQpc)); T(sb, s.Ms(r.GpuEndQpc));
            sb.Append(r.SampleSeq).Append(',').Append(r.SubmitSeq).Append(',').Append(r.MissedSamples).Append(',');
            T(sb, r.GpuMs); T(sb, r.PassWholeMs);
            sb.Append(r.PassCount).Append(',').Append(r.PassesDropped).Append(',');
            T(sb, r.UploadsMs); T(sb, r.BakedBlurMs); T(sb, r.ClearMs); T(sb, r.SceneMs); T(sb, r.GlyphBandMs); T(sb, r.TileRasterMs);
            T(sb, r.OffscreenMs); T(sb, r.CompositeMs, last: true);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Memory(LedgerSnapshot s)
    {
        var sb = new StringBuilder(64 + s.Memory.Length * 200);
        sb.AppendLine("seq,ms,workingSetBytes,privateBytes,managedBytes,gcHeapBytes,gcCommittedBytes,totalAllocatedBytes,vramLocalBytes,vramNonLocalBytes,vramLocalBudgetBytes,trackedGpuBytes,imageCacheBytes,imageCount,glyphAtlasBytes,processCpuMs,processCyclesMs,gcPauseMs,gc0,gc1,gc2");
        foreach (ref readonly var r in s.Memory.AsSpan())
        {
            sb.Append(r.Seq).Append(','); T(sb, s.Ms(r.Qpc));
            sb.Append(r.WorkingSetBytes).Append(',').Append(r.PrivateBytes).Append(',').Append(r.ManagedBytes).Append(',')
              .Append(r.GcHeapBytes).Append(',').Append(r.GcCommittedBytes).Append(',').Append(r.TotalAllocatedBytes).Append(',')
              .Append(r.VramLocalBytes).Append(',').Append(r.VramNonLocalBytes).Append(',').Append(r.VramLocalBudgetBytes).Append(',')
              .Append(r.TrackedGpuBytes).Append(',').Append(r.ImageCacheBytes).Append(',').Append(r.ImageCount).Append(',')
              .Append(r.GlyphAtlasBytes).Append(',');
            T(sb, r.ProcessCpuTicksTotal / 10_000.0); T(sb, s.CyclesMs(r.ProcessCyclesTotal)); T(sb, r.GcPauseTicksTotal / 10_000.0);
            sb.Append(r.Gc0Total).Append(',').Append(r.Gc1Total).Append(',').Append(r.Gc2Total).AppendLine();
        }
        return sb.ToString();
    }

    public static string Audio(LedgerSnapshot s)
    {
        var sb = new StringBuilder(64 + s.Audio.Length * 80);
        sb.AppendLine("seq,ms,deviceWrites,deviceUnderruns,xruns,xrunFrames,paddingMinFrames,paddingMinMs,bufferFrames,rate");
        LedgerAudioSample prev = default;
        for (int i = 0; i < s.Audio.Length; i++)
        {
            ref readonly var r = ref s.Audio[i];
            sb.Append(r.Seq).Append(','); T(sb, s.Ms(r.Qpc));
            // Deltas since the previous sample (the first row: since the counters began).
            sb.Append(r.DeviceWritesTotal - prev.DeviceWritesTotal).Append(',').Append(r.DeviceUnderrunsTotal - prev.DeviceUnderrunsTotal).Append(',')
              .Append(r.XrunsTotal - prev.XrunsTotal).Append(',').Append(r.XrunFramesTotal - prev.XrunFramesTotal).Append(',')
              .Append(r.PaddingMinFrames).Append(',');
            T(sb, r.PaddingMinFrames >= 0 && r.Rate > 0 ? r.PaddingMinFrames * 1000.0 / r.Rate : double.NaN);
            sb.Append(r.BufferFrames).Append(',').Append(r.Rate).AppendLine();
            prev = r;
        }
        return sb.ToString();
    }

    /// <summary>One numeric cell (3 decimals; empty for NaN) and its separator.</summary>
    private static void T(StringBuilder sb, double v, bool last = false)
    {
        if (double.IsFinite(v)) sb.Append(v.ToString("0.###", Inv));
        if (!last) sb.Append(',');
    }
}
