using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace FluentGpu.Hosting;

/// <summary>The kinds of <see cref="FrameTrace"/> event rows (one record = one line of the <c>.fgtrace</c> file). The ledger's
/// streams (<c>frame</c>, <c>turn</c>, <c>gpu</c>) are not events: the writer drains them from <see cref="FrameLedger"/>'s rings.
/// The format and every field: <c>scratch/perf3/TRACE-FORMAT.md</c> (lanes SCROLL and MOTION, audit PERF r3).</summary>
public enum TraceKind : byte
{
    /// <summary>A header key changed (<c>meta</c>): <c>Ref0</c> = key, <c>Ref1</c> = value.</summary>
    Meta,
    /// <summary>A user or bench marker (<c>mark</c>): <c>Ref0</c> = code, <c>Ref1</c> = note.</summary>
    Mark,
    /// <summary>One Present of the owning host's primary swapchain (<c>present</c>, render thread).</summary>
    Present,
    /// <summary>A route change (<c>nav</c>, UI thread): <c>L0</c> = id, <c>Ref0</c> = route, <c>Ref1</c> = arg, <c>B0</c> = motion.</summary>
    Nav,
    /// <summary>An animation lifecycle edge (<c>anim</c>): see <see cref="TraceAnimPhase"/>.</summary>
    Anim,
    /// <summary>An image lifecycle edge (<c>img</c>).</summary>
    Img,
    /// <summary>A theme transaction or live theme switch edge (<c>theme</c>).</summary>
    Theme,
    /// <summary>The UI frame's mount census (<c>mount</c>) when a frame mounted components.</summary>
    Mount,
}

/// <summary><c>anim ph=</c>: the lifecycle edges of a structural (pending-start) animation row.</summary>
public enum TraceAnimPhase : byte
{
    /// <summary>UI: a node's enter/exit/reveal rows were seeded (one row per node: the first row's delay and duration).</summary>
    Seed,
    /// <summary>The first frame that poses the row holds it at t = 0: <c>at</c> = the time it is posed for (render: the predicted
    /// present; UI: the frame's clock).</summary>
    Hold,
    /// <summary>The row's start resolved: <c>gap_ms</c> = the real time since the hold, <c>first_ms</c> = the step the row took
    /// (capped to one steady frame when the held frame ran long).</summary>
    Start,
    /// <summary>The row reached its end (<c>elapsed_ms</c>).</summary>
    Done,
    /// <summary>UI, the tick after a seed: the node's key and its nearest keyed ancestor's (<c>key=</c>, <c>under=</c>).</summary>
    Name,
}

/// <summary><c>anim kind=</c>.</summary>
public enum TraceAnimKind : byte { Enter, Exit, Reveal, Other }

/// <summary>One event row: plain fields plus up to two object references (strings, never formatted on the producer). A struct
/// copy into a per-thread ring is the whole record path.</summary>
public struct TraceEvent
{
    public long Qpc;
    public TraceKind Kind;
    public byte B0, B1, B2;
    public int I0, I1, I2, I3;
    public long L0, L1, L2, L3;
    public float F0, F1, F2, F3;
    public object? Ref0, Ref1;
}

/// <summary>
/// <c>--fg frame-trace[=PATH]</c>: one text timeline of what the app did and what reached the glass, cheap enough to leave on in a
/// real session (lanes MOTION and SCROLL, audit PERF r3; format: <c>scratch/perf3/TRACE-FORMAT.md</c>).
/// <para><b>Producers.</b> The frame, turn and GPU rows are <see cref="FrameLedger"/>'s records (the trace turns the ledger on with a
/// small ring); the event rows (<see cref="TraceKind"/>) go into a per-thread single-producer ring of <see cref="TraceEvent"/> POD
/// structs: no lock, no formatting, no allocation after a thread's first record (which allocates its ring once).</para>
/// <para><b>Writer.</b> One background thread (BelowNormal) drains every ring every <see cref="DrainMs"/> ms, formats UTF-8 into a
/// reused buffer and appends it to the file (flushed per drain, so a crash keeps everything up to the last drain). Rows are written
/// per source per drain: a reader sorts by <c>t</c>. A ring the writer could not keep up with loses its oldest rows and says so
/// (<c>drop</c>), never stalls its producer.</para>
/// <para><b>Time.</b> Every time field is trace ms: (QPC − the trace's origin) / QPC frequency × 1000, three decimals.</para>
/// </summary>
public static class FrameTrace
{
    /// <summary>The writer's drain period (ms).</summary>
    public const int DrainMs = 100;
    /// <summary>Ring capacity of a thread that claimed a role (UI, render): 16384 events (power of two).</summary>
    public const int HotRingCapacity = 16384;
    /// <summary>Ring capacity of any other thread (decoders, I/O): 1024 events.</summary>
    public const int ColdRingCapacity = 1024;
    /// <summary>The ledger capacity the trace enables (records; ≈ 68 s of frames at 120 Hz between two drains of 100 ms).</summary>
    public const int LedgerCapacity = 8192;

    private static volatile bool s_enabled;
    private static long s_originQpc;
    private static readonly double s_qpcToMs = 1000.0 / Stopwatch.Frequency;
    private static readonly List<TraceRing> s_rings = new();
    private static readonly object s_gate = new();
    [ThreadStatic] private static TraceRing? t_ring;
    private static Thread? s_writer;
    private static AutoResetEvent? s_stop;
    private static FileStream? s_file;
    private static string? s_path;
    // Writer-thread state.
    private static long s_uiPos, s_renderPos, s_gpuPos;
    private static LedgerUiFrame[] s_uiScratch = [];
    private static LedgerRenderTurn[] s_renderScratch = [];
    private static LedgerGpuFrame[] s_gpuScratch = [];
    private static TraceLine s_line;
    private static long s_drainTicks, s_drains, s_rowsWritten, s_bytesWritten;

    /// <summary>The one check every record path makes.</summary>
    public static bool Enabled => s_enabled;

    /// <summary>The file the trace writes (null while off).</summary>
    public static string? Path => s_path;

    /// <summary>The path <c>--fg frame-trace=PATH</c> asked for; <c>""</c> = the switch without a path (the default folder).</summary>
    public static string? RequestedPath { get; set; }

    /// <summary>Where a path-less <c>--fg frame-trace</c> writes (the app's log folder; the app sets it before the host starts).
    /// Null: the current directory.</summary>
    public static string? DefaultDirectory { get; set; }

    /// <summary>The QPC the trace's t = 0 is (0 while off).</summary>
    public static long OriginQpc => Volatile.Read(ref s_originQpc);

    /// <summary>An absolute time in ms on the QPC base (the render thread's present-time clock: QPC / frequency × 1000) → trace ms.</summary>
    public static double MsFromAbsMs(double absMs) => absMs - s_originQpc * s_qpcToMs;

    /// <summary>QPC → trace ms.</summary>
    public static double Ms(long qpc) => (qpc - s_originQpc) * s_qpcToMs;

    /// <summary>Start tracing into <paramref name="path"/> (null/empty: <see cref="DefaultDirectory"/> +
    /// <c>frame-trace-yyyyMMdd-HHmmss.fgtrace</c>). Turns the ledger on. Idempotent while on. Not a hot path.</summary>
    public static string? Start(string? path, IReadOnlyList<KeyValuePair<string, string>>? header = null)
    {
        lock (s_gate)
        {
            if (s_enabled) return s_path;
            string full;
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    string dir = DefaultDirectory is { Length: > 0 } d ? d : Environment.CurrentDirectory;
                    full = System.IO.Path.Combine(dir, "frame-trace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".fgtrace");
                }
                else full = System.IO.Path.GetFullPath(path);
                if (System.IO.Path.GetDirectoryName(full) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
                s_file = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine("[frame-trace] cannot open the trace file: " + ex.Message);
                return null;
            }
            s_path = full;
            FrameLedger.Enable(LedgerCapacity);
            s_uiPos = FrameLedger.UiCount; s_renderPos = FrameLedger.RenderCount; s_gpuPos = FrameLedger.GpuCount;
            s_uiScratch = new LedgerUiFrame[1024];
            s_renderScratch = new LedgerRenderTurn[1024];
            s_gpuScratch = new LedgerGpuFrame[1024];
            s_line = new TraceLine(1 << 20);
            long origin = Stopwatch.GetTimestamp();
            Volatile.Write(ref s_originQpc, origin);
            var sb = new StringBuilder();
            sb.Append("#fgtrace 1\n");
            sb.Append("#qpc_hz=").Append(Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#origin_qpc=").Append(origin.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#wall_utc=").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#exe=").Append(Environment.ProcessPath ?? "-").Append('\n');
            sb.Append("#args=").Append(string.Join(' ', Environment.GetCommandLineArgs().Skip(1)).Replace('\n', ' ')).Append('\n');
            if (header is not null) foreach (var kv in header) sb.Append('#').Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
            s_file.Write(head);
            s_file.Flush();
            s_bytesWritten = head.Length;
            s_enabled = true;
            var stop = new AutoResetEvent(false);
            s_stop = stop;
            s_writer = new Thread(() => WriterLoop(stop)) { IsBackground = true, Name = "fgpu-frame-trace", Priority = ThreadPriority.BelowNormal };
            s_writer.Start();
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Stop();   // a run that exits without the platform's teardown still closes the file
            Console.Error.WriteLine("[frame-trace] writing " + full);
            return full;
        }
    }

    /// <summary>Stop: a final drain, then close the file (a summary row first). Any thread; not a hot path.</summary>
    public static void Stop()
    {
        Thread? writer;
        lock (s_gate)
        {
            if (!s_enabled) return;
            s_enabled = false;
            writer = s_writer;
            s_writer = null;
            s_stop?.Set();
        }
        writer?.Join(2000);
        lock (s_gate)
        {
            try
            {
                Drain();
                WriteSummary();
                s_file?.Flush();
                s_file?.Dispose();
            }
            catch (IOException) { }
            s_file = null;
            s_stop?.Dispose();
            s_stop = null;
            Console.Error.WriteLine("[frame-trace] closed " + s_path + " (rows=" + s_rowsWritten + " bytes=" + s_bytesWritten + ")");
        }
    }

    // ── producers (any thread; no allocation after the thread's first record) ─────────────────────────────────────────

    /// <summary>The calling thread will record often (the UI or the render thread): its ring is the large one. Call before its first record.</summary>
    public static void ClaimHotThread()
    {
        if (!s_enabled || t_ring is not null) return;
        t_ring = Register(HotRingCapacity, Environment.CurrentManagedThreadId);
    }

    /// <summary>Push one event (its <see cref="TraceEvent.Qpc"/> stamped here when 0).</summary>
    public static void Push(ref TraceEvent e)
    {
        if (!s_enabled) return;
        var ring = t_ring ??= Register(ColdRingCapacity, Environment.CurrentManagedThreadId);
        if (e.Qpc == 0) e.Qpc = Stopwatch.GetTimestamp();
        ring.Push(in e);
    }

    /// <summary>A header key changed (<c>meta</c>).</summary>
    public static void Meta(string key, string value)
    {
        if (!s_enabled) return;
        var e = new TraceEvent { Kind = TraceKind.Meta, Ref0 = key, Ref1 = value };
        Push(ref e);
    }

    /// <summary>A marker (<c>mark code= note=</c>): a bench phase edge, a "felt wrong" key.</summary>
    public static void Mark(string code, string? note = null)
    {
        if (!s_enabled) return;
        var e = new TraceEvent { Kind = TraceKind.Mark, Ref0 = code, Ref1 = note };
        Push(ref e);
    }

    /// <summary>A route change (<c>nav</c>). <paramref name="motion"/>: 0 forward, 1 back, 2 neutral, 255 unknown.</summary>
    public static void Nav(long id, string route, string? arg, byte motion = 255)
    {
        if (!s_enabled) return;
        var e = new TraceEvent { Kind = TraceKind.Nav, L0 = id, Ref0 = route, Ref1 = arg, B0 = motion };
        Push(ref e);
    }

    /// <summary>An animation edge (<c>anim</c>). <paramref name="node"/> = the scene node index, <paramref name="channel"/> = the
    /// AnimChannel (−1 = the node's rows), <paramref name="atMs"/> = the time the edge refers to in trace ms (NaN = the write time).</summary>
    public static void Anim(TraceAnimPhase phase, TraceAnimKind kind, int node, int channel, bool render, double atMs,
        float a = float.NaN, float b = float.NaN, float c = float.NaN, int rows = 0)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Anim, B0 = (byte)phase, B1 = (byte)kind, B2 = render ? (byte)1 : (byte)0,
            I0 = node, I1 = channel, I2 = rows, F0 = a, F1 = b, F2 = c,
            L0 = BitConverter.DoubleToInt64Bits(atMs),
        };
        Push(ref e);
    }

    /// <summary>A theme edge (<c>theme ph= ...</c>): <paramref name="phase"/> is a literal (never formatted).</summary>
    public static void Theme(string phase, string? label = null, float a = float.NaN, float b = float.NaN)
    {
        if (!s_enabled) return;
        var e = new TraceEvent { Kind = TraceKind.Theme, Ref0 = phase, Ref1 = label, F0 = a, F1 = b };
        Push(ref e);
    }

    /// <summary>One present (render thread of the ledger's owner): <paramref name="tick"/>/<paramref name="tickQpc"/> the compositor
    /// tick, <paramref name="doneQpc"/> when Present returned, <paramref name="photonQpc"/> the predicted on-glass time, the DXGI paired
    /// counters as cumulative values.</summary>
    internal static void Present(long tick, long tickQpc, long doneQpc, long photonQpc, ulong publishSeq, long displayed, long dropped, long repeated, int missed)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Present, Qpc = doneQpc != 0 ? doneQpc : Stopwatch.GetTimestamp(), L0 = tick, L1 = tickQpc, L2 = photonQpc,
            L3 = (long)publishSeq, I0 = (int)Math.Min(displayed, int.MaxValue), I1 = (int)Math.Min(dropped, int.MaxValue),
            I2 = (int)Math.Min(repeated, int.MaxValue), I3 = missed,
        };
        Push(ref e);
    }

    /// <summary>A UI frame's mount census (<c>mount</c>): components mounted, nodes created, measures, text shapes.</summary>
    internal static void Mount(int components, int nodesCreated, int measures, int shapes, ulong publishSeq)
    {
        if (!s_enabled) return;
        var e = new TraceEvent { Kind = TraceKind.Mount, I0 = components, I1 = nodesCreated, I2 = measures, I3 = shapes, L0 = (long)publishSeq };
        Push(ref e);
    }

    private static TraceRing Register(int capacity, int threadId)
    {
        var ring = new TraceRing(capacity, threadId);
        lock (s_rings) s_rings.Add(ring);
        return ring;
    }

    // ── the writer ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriterLoop(AutoResetEvent stop)
    {
        while (!stop.WaitOne(DrainMs))
        {
            lock (s_gate)
            {
                if (!s_enabled) break;
                try { Drain(); }
                catch (IOException ex) { Console.Error.WriteLine("[frame-trace] write failed: " + ex.Message); }
            }
        }
    }

    /// <summary>Format everything recorded since the last drain and append it. Writer thread (or Stop), under <see cref="s_gate"/>.</summary>
    private static void Drain()
    {
        if (s_file is not { } file) return;
        long t0 = Stopwatch.GetTimestamp();
        ref TraceLine L = ref s_line;
        L.Reset();
        int n;
        while ((n = FrameLedger.CopyUi(ref s_uiPos, s_uiScratch, out long lostUi)) > 0 || lostUi > 0)
        {
            if (lostUi > 0) WriteDrop(ref L, "ledger.ui", lostUi);
            for (int i = 0; i < n; i++) WriteFrame(ref L, in s_uiScratch[i]);
            FlushIfFull(ref L, file);
            if (n < s_uiScratch.Length) break;
        }
        while ((n = FrameLedger.CopyRender(ref s_renderPos, s_renderScratch, out long lostR)) > 0 || lostR > 0)
        {
            if (lostR > 0) WriteDrop(ref L, "ledger.render", lostR);
            for (int i = 0; i < n; i++) WriteTurn(ref L, in s_renderScratch[i]);
            FlushIfFull(ref L, file);
            if (n < s_renderScratch.Length) break;
        }
        while ((n = FrameLedger.CopyGpu(ref s_gpuPos, s_gpuScratch, out long lostG)) > 0 || lostG > 0)
        {
            if (lostG > 0) WriteDrop(ref L, "ledger.gpu", lostG);
            for (int i = 0; i < n; i++) WriteGpu(ref L, in s_gpuScratch[i]);
            FlushIfFull(ref L, file);
            if (n < s_gpuScratch.Length) break;
        }
        int ringCount;
        lock (s_rings) ringCount = s_rings.Count;
        for (int r = 0; r < ringCount; r++)
        {
            TraceRing ring;
            lock (s_rings) ring = s_rings[r];
            ring.Drain(ref L, file);
        }
        L.FlushTo(file);
        file.Flush();
        s_drainTicks += Stopwatch.GetTimestamp() - t0;
        s_drains++;
    }

    internal static void FlushIfFull(ref TraceLine L, FileStream file)
    {
        if (L.Length > L.Capacity - 8192) L.FlushTo(file);
    }

    private static void WriteSummary()
    {
        if (s_file is not { } file) return;
        ref TraceLine L = ref s_line;
        L.Reset();
        L.Begin(Ms(Stopwatch.GetTimestamp()), "trace"u8);
        L.Num("drains"u8, s_drains);
        L.Ms("drain_ms_total"u8, s_drainTicks * s_qpcToMs);
        L.Ms("drain_ms_avg"u8, s_drains > 0 ? s_drainTicks * s_qpcToMs / s_drains : 0);
        L.Num("rows"u8, s_rowsWritten + 1);
        L.Num("bytes"u8, s_bytesWritten + L.Length);
        L.End();
        L.FlushTo(file);
    }

    internal static void CountRow() => s_rowsWritten++;
    internal static void CountBytes(int bytes) => s_bytesWritten += bytes;

    private static void WriteDrop(ref TraceLine L, string ring, long rows)
    {
        L.Begin(Ms(Stopwatch.GetTimestamp()), "drop"u8);
        L.Str("ring"u8, ring);
        L.Num("rows"u8, rows);
        L.End();
    }

    private static void WriteFrame(ref TraceLine L, in LedgerUiFrame r)
    {
        L.Begin(Ms(r.EndQpc), "frame"u8);
        L.Num("seq"u8, (long)r.Seq);
        L.At("begin"u8, r.StartQpc);
        L.Num("exit"u8, r.Exit);
        if (r.PaintQpc != 0)
        {
            L.At("paint"u8, r.PaintQpc);
            L.At("flush"u8, r.FlushQpc);
            L.At("layout"u8, r.LayoutQpc);
            L.At("anim"u8, r.AnimQpc);
            L.At("rec"u8, r.RecordQpc);
            L.At("commit"u8, r.SubmitQpc);
            L.Num("publish"u8, (long)r.PublishSeq);
        }
        L.Ms("ui_ms"u8, (r.EndQpc - r.StartQpc) * s_qpcToMs);
        if (r.WaitStartQpc != 0) L.Ms("wait_ms"u8, (r.WaitEndQpc - r.WaitStartQpc) * s_qpcToMs);
        L.Num("flags"u8, r.Flags);
        if (r.Components != 0) L.Num("comps"u8, r.Components);
        if (r.ComponentsMounted != 0) L.Num("mounted"u8, r.ComponentsMounted);
        if (r.NodesCreated != 0) L.Num("created"u8, r.NodesCreated);
        if (r.NodesFreed != 0) L.Num("freed"u8, r.NodesFreed);
        if (r.Measures != 0) L.Num("measures"u8, r.Measures);
        if (r.TextShapes != 0) L.Num("shapes"u8, r.TextShapes);
        if (r.ReactiveUnits != 0) L.Num("units"u8, r.ReactiveUnits);
        if (r.BindFires != 0) L.Num("binds"u8, r.BindFires);
        if (r.Nodes != 0) L.Num("nodes"u8, r.Nodes);
        if (r.CaptureMs > 0) L.Ms("capture_ms"u8, r.CaptureMs);
        L.Num("alloc"u8, r.AllocBytes);
        L.Num("gc"u8, r.Gc0Total);
        L.Num("missed_total"u8, r.MissedVsyncsTotal);
        L.End();
    }

    private static void WriteTurn(ref TraceLine L, in LedgerRenderTurn r)
    {
        L.Begin(Ms(r.EndQpc), "turn"u8);
        L.Num("seq"u8, (long)r.Seq);
        L.Num("tick"u8, r.TickSeq);
        if (r.TickQpc != 0) L.At("tickq"u8, r.TickQpc);
        L.Num("kind"u8, r.Kind);
        L.Num("outcome"u8, r.Outcome);
        L.At("start"u8, r.StartQpc);
        if (r.SlotOpenQpc != 0) L.At("slot"u8, r.SlotOpenQpc);
        if (r.DoneQpc != 0) L.At("done"u8, r.DoneQpc);
        L.Ms("wake_ms"u8, r.WaitStartQpc != 0 ? (r.StartQpc - r.WaitStartQpc) * s_qpcToMs : 0);
        L.Ms("work_ms"u8, (r.EndQpc - r.StartQpc) * s_qpcToMs);
        if (r.PublishSeq != 0) L.Num("pub"u8, (long)r.PublishSeq);
        if (r.StageMs > 0) L.Ms("stage_ms"u8, r.StageMs);
        if (r.RecordMs > 0) L.Ms("rec_ms"u8, r.RecordMs);
        if (r.SubmitMs > 0) L.Ms("sub_ms"u8, r.SubmitMs);
        if (r.FenceMs > 0) L.Ms("fence_ms"u8, r.FenceMs);
        if (r.LatencyMs > 0) L.Ms("lat_ms"u8, r.LatencyMs);
        if (r.PresentMs > 0) L.Ms("pres_ms"u8, r.PresentMs);
        if (r.BuildMs > 0) L.Ms("build_ms"u8, r.BuildMs);
        if (r.TilesRastered != 0) L.Num("tiles"u8, r.TilesRastered);
        if (r.DamageCoverage > 0) L.Ms("cov"u8, r.DamageCoverage);
        if (r.MissedTicks != 0) L.Num("missed"u8, r.MissedTicks);
        if (r.SubmitSeq != 0) L.Num("submit"u8, (long)r.SubmitSeq);
        L.Num("flags"u8, r.Flags);
        L.End();
    }

    private static void WriteGpu(ref TraceLine L, in LedgerGpuFrame r)
    {
        L.Begin(Ms(r.ObservedQpc), "gpu"u8);
        L.Num("submit"u8, (long)r.SubmitSeq);
        L.Ms("gpu_ms"u8, r.GpuMs);
        if (r.GpuStartQpc != 0) L.At("gstart"u8, r.GpuStartQpc);
        if (r.GpuEndQpc != 0) L.At("gend"u8, r.GpuEndQpc);
        if (r.MissedSamples != 0) L.Num("missed_samples"u8, r.MissedSamples);
        if (r.PassCount != 0)
        {
            L.Num("passes"u8, r.PassCount);
            L.Ms("tile_ms"u8, r.TileRasterMs);
            L.Ms("comp_ms"u8, r.CompositeMs);
            L.Ms("blur_ms"u8, r.BakedBlurMs);
            L.Ms("off_ms"u8, r.OffscreenMs);
        }
        L.End();
    }

    internal static void WriteEvent(ref TraceLine L, in TraceEvent e)
    {
        switch (e.Kind)
        {
            case TraceKind.Meta:
                L.Begin(Ms(e.Qpc), "meta"u8);
                L.Str((e.Ref0 as string) ?? "-", (e.Ref1 as string) ?? "-");
                break;
            case TraceKind.Mark:
                L.Begin(Ms(e.Qpc), "mark"u8);
                L.Str("code"u8, (e.Ref0 as string) ?? "-");
                if (e.Ref1 is string note) L.Str("note"u8, note);
                break;
            case TraceKind.Present:
                L.Begin(Ms(e.Qpc), "present"u8);
                L.Num("tick"u8, e.L0);
                if (e.L1 != 0) L.At("tickq"u8, e.L1);
                L.At("present"u8, e.Qpc);
                if (e.L2 != 0) L.At("photon"u8, e.L2);
                if (e.L3 != 0) L.Num("pub"u8, e.L3);
                L.Num("displayed"u8, e.I0);
                L.Num("dropped"u8, e.I1);
                L.Num("repeated"u8, e.I2);
                if (e.I3 != 0) L.Num("missed"u8, e.I3);
                break;
            case TraceKind.Nav:
                L.Begin(Ms(e.Qpc), "nav"u8);
                L.Num("id"u8, e.L0);
                L.Str("route"u8, (e.Ref0 as string) ?? "-");
                if (e.Ref1 is string arg) L.Str("arg"u8, arg);
                if (e.B0 != 255) L.Num("motion"u8, e.B0);
                break;
            case TraceKind.Anim:
            {
                L.Begin(Ms(e.Qpc), "anim"u8);
                L.Raw(" ph="u8);
                L.Raw((TraceAnimPhase)e.B0 switch
                {
                    TraceAnimPhase.Seed => "seed"u8, TraceAnimPhase.Hold => "hold"u8, TraceAnimPhase.Start => "start"u8,
                    TraceAnimPhase.Name => "name"u8, _ => "done"u8,
                });
                L.Raw(" kind="u8);
                L.Raw((TraceAnimKind)e.B1 switch
                {
                    TraceAnimKind.Enter => "enter"u8, TraceAnimKind.Exit => "exit"u8, TraceAnimKind.Reveal => "reveal"u8, _ => "other"u8,
                });
                L.Raw(e.B2 != 0 ? " thr=render"u8 : " thr=ui"u8);
                L.Num("node"u8, e.I0);
                if (e.I1 >= 0) L.Num("ch"u8, e.I1);
                if (e.I2 != 0) L.Num("rows"u8, e.I2);
                double at = BitConverter.Int64BitsToDouble(e.L0);
                if (!double.IsNaN(at)) L.Ms("at"u8, at);
                switch ((TraceAnimPhase)e.B0)
                {
                    case TraceAnimPhase.Seed:
                        if (!float.IsNaN(e.F0)) L.Ms("delay_ms"u8, e.F0);
                        if (!float.IsNaN(e.F1)) L.Ms("dur_ms"u8, e.F1);
                        break;
                    case TraceAnimPhase.Hold:
                        if (!float.IsNaN(e.F0)) L.Ms("ref_ms"u8, e.F0);
                        break;
                    case TraceAnimPhase.Start:
                        if (!float.IsNaN(e.F0)) L.Ms("gap_ms"u8, e.F0);
                        if (!float.IsNaN(e.F1)) L.Ms("first_ms"u8, e.F1);
                        break;
                    case TraceAnimPhase.Done:
                        if (!float.IsNaN(e.F0)) L.Ms("elapsed_ms"u8, e.F0);
                        break;
                    case TraceAnimPhase.Name:
                        if (e.Ref0 is string nkey) L.Str("key"u8, nkey);
                        if (e.Ref1 is string nanc) L.Str("under"u8, nanc);
                        break;
                }
                break;
            }
            case TraceKind.Img:
                L.Begin(Ms(e.Qpc), "img"u8);
                L.Num("id"u8, e.I0);
                if (e.Ref0 is string ph) L.Str("ph"u8, ph);
                if (e.I1 != 0 || e.I2 != 0) { L.Num("w"u8, e.I1); L.Num("h"u8, e.I2); }
                L.Ms("fade_ms"u8, e.F0);
                if (!float.IsNaN(e.F1)) L.Ms("since_req_ms"u8, e.F1);
                if (e.Ref1 is string cause) L.Str("cause"u8, cause);
                break;
            case TraceKind.Theme:
                L.Begin(Ms(e.Qpc), "theme"u8);
                L.Str("ph"u8, (e.Ref0 as string) ?? "-");
                if (e.Ref1 is string label) L.Str("label"u8, label);
                if (!float.IsNaN(e.F0)) L.Ms("a"u8, e.F0);
                if (!float.IsNaN(e.F1)) L.Ms("b"u8, e.F1);
                break;
            case TraceKind.Mount:
                L.Begin(Ms(e.Qpc), "mount"u8);
                L.Num("comps"u8, e.I0);
                L.Num("created"u8, e.I1);
                L.Num("measures"u8, e.I2);
                L.Num("shapes"u8, e.I3);
                if (e.L0 != 0) L.Num("pub"u8, e.L0);
                break;
            default:
                L.Begin(Ms(e.Qpc), "unknown"u8);
                L.Num("kind"u8, (int)e.Kind);
                break;
        }
        L.End();
    }

    internal static void WriteThreadDrop(ref TraceLine L, int threadId, long rows)
    {
        L.Begin(Ms(Stopwatch.GetTimestamp()), "drop"u8);
        L.Raw(" ring=thread"u8);
        L.Raw(" tid="u8);
        L.Int(threadId);
        L.Num("rows"u8, rows);
        L.End();
    }
}

/// <summary>A single-producer ring of <see cref="TraceEvent"/>: the producer writes the slot, then publishes the count (release); the
/// writer copies a slot and re-reads the count to tell a lapped slot from a good one.</summary>
internal sealed class TraceRing(int capacity, int threadId)
{
    private readonly TraceEvent[] _items = new TraceEvent[capacity];
    private readonly int _mask = capacity - 1;
    private long _count;
    private long _read;   // writer only
    public int ThreadId { get; } = threadId;

    public void Push(in TraceEvent e)
    {
        long i = _count;
        _items[(int)(i & _mask)] = e;
        Volatile.Write(ref _count, i + 1);
    }

    /// <summary>Writer: format every row pushed since the last drain.</summary>
    public void Drain(ref TraceLine L, FileStream file)
    {
        long end = Volatile.Read(ref _count);
        long start = _read;
        if (end - start > _items.Length)
        {
            FrameTrace.WriteThreadDrop(ref L, ThreadId, end - start - _items.Length);
            start = end - _items.Length;
        }
        for (long i = start; i < end; i++)
        {
            TraceEvent e = _items[(int)(i & _mask)];
            // Lapped while copying: the producer has since written a newer row into this slot.
            if (Volatile.Read(ref _count) - i > _items.Length) { FrameTrace.WriteThreadDrop(ref L, ThreadId, 1); continue; }
            FrameTrace.WriteEvent(ref L, in e);
            FrameTrace.FlushIfFull(ref L, file);
        }
        _read = end;
    }
}

/// <summary>The writer's UTF-8 line builder over one reused buffer: <c>t\tkind\tk=v k=v ...\n</c>. No allocation.</summary>
internal struct TraceLine
{
    private readonly byte[] _buf;
    private int _len;
    private bool _first;

    public TraceLine(int capacity) { _buf = new byte[capacity]; _len = 0; _first = true; }

    public readonly int Length => _len;
    public readonly int Capacity => _buf.Length;
    public void Reset() => _len = 0;

    public void FlushTo(FileStream file)
    {
        if (_len == 0) return;
        file.Write(_buf, 0, _len);
        FrameTrace.CountBytes(_len);
        _len = 0;
    }

    public void Begin(double tMs, ReadOnlySpan<byte> kind)
    {
        Fixed(tMs);
        _buf[_len++] = (byte)'\t';
        Raw(kind);
        _buf[_len++] = (byte)'\t';
        _first = true;
    }

    public void End()
    {
        _buf[_len++] = (byte)'\n';
        FrameTrace.CountRow();
    }

    public void Raw(ReadOnlySpan<byte> s)
    {
        // The first field after the tab carries no leading space.
        if (_first && s.Length > 0 && s[0] == (byte)' ') s = s[1..];
        _first = false;
        s.CopyTo(_buf.AsSpan(_len));
        _len += s.Length;
    }

    private void Key(ReadOnlySpan<byte> key)
    {
        if (!_first) _buf[_len++] = (byte)' ';
        _first = false;
        key.CopyTo(_buf.AsSpan(_len));
        _len += key.Length;
        _buf[_len++] = (byte)'=';
    }

    public void Int(long v)
    {
        Utf8Formatter.TryFormat(v, _buf.AsSpan(_len), out int w);
        _len += w;
    }

    private void Fixed(double v)
    {
        if (!double.IsFinite(v)) { "nan"u8.CopyTo(_buf.AsSpan(_len)); _len += 3; return; }
        v.TryFormat(_buf.AsSpan(_len), out int w, "F3", CultureInfo.InvariantCulture);
        _len += w;
    }

    public void Num(ReadOnlySpan<byte> key, long v) { Key(key); Int(v); }

    /// <summary>A value with three decimals (ms, or any fraction).</summary>
    public void Ms(ReadOnlySpan<byte> key, double v)
    {
        if (!double.IsFinite(v)) return;   // missing = omitted
        Key(key);
        Fixed(v);
    }

    /// <summary>A QPC stamp as trace ms.</summary>
    public void At(ReadOnlySpan<byte> key, long qpc)
    {
        if (qpc == 0) return;
        Key(key);
        Fixed(FrameTrace.Ms(qpc));
    }

    public void Str(ReadOnlySpan<byte> key, string value)
    {
        Key(key);
        Text(value);
    }

    /// <summary>A key given as a string (meta rows).</summary>
    public void Str(string key, string value)
    {
        if (!_first) _buf[_len++] = (byte)' ';
        _first = false;
        Text(key);
        _buf[_len++] = (byte)'=';
        Text(value);
    }

    private void Text(string value)
    {
        int max = Math.Min(value.Length, 512);
        int start = _len;
        _len += Encoding.UTF8.GetBytes(value.AsSpan(0, max), _buf.AsSpan(_len));
        // No spaces, tabs or newlines inside a value.
        for (int i = start; i < _len; i++)
            if (_buf[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') _buf[i] = (byte)'_';
    }
}
