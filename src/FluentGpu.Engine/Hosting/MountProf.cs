using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace FluentGpu.Hosting;

/// <summary>
/// Diagnostic-only EXCLUSIVE phase profiler for the page-mount root-cause analysis. Off unless the environment variable
/// <c>FGPU_PROF</c> names a file: then every UI frame whose bucketed time exceeds <c>FGPU_PROF_MIN_MS</c> (default 3)
/// appends one line with the exclusive milliseconds and call counts of each bucket, plus the frame's GC pause and
/// allocation. A bucket is entered/exited around a code region; time is attributed to the INNERMOST open bucket, so
/// the numbers add up to the frame (no double counting through recursion). Cost when off: one static bool read.
/// </summary>
// Allocation-free when off: every buffer below is sized/created only under On (the VerticalSlice alloc-zero gates
// touch this type inside their measured window; a 16 KB stack array there failed them).
public static class MountProf
{
    public static readonly bool On = Environment.GetEnvironmentVariable("FGPU_PROF") is { Length: > 0 };
    static readonly string? s_path = Environment.GetEnvironmentVariable("FGPU_PROF");
    static readonly double s_minMs = double.TryParse(Environment.GetEnvironmentVariable("FGPU_PROF_MIN_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 3.0;

    public const int Frame = 0, Flush = 1, Render = 2, CompSetup = 3, MountSelf = 4, WriteCols = 5, Bind = 6, SceneOps = 7,
        Layout = 8, Measure = 9, TextMeasure = 10, Arrange = 11, Record = 12, Unmount = 13, BindFire = 14, Reconcile = 15,
        RealizeVirtual = 16, Effects = 17, Presence = 18, RecordDirty = 19, Capture = 20, ImagePump = 21, PreRecord = 22, PostRecord = 23, Anim = 24, Count = 25;
    static readonly string[] s_names = !On ? [] : ["frame", "flush", "render", "compSetup", "mount", "writeCols", "bind", "sceneOps",
        "layout", "measure", "textMeasure", "arrange", "record", "unmount", "bindFire", "reconcile", "realizeV", "effects", "presence", "recordDirty", "capture", "imagePump", "preRecord", "postRecord", "anim"];

    static readonly long[] s_ticks = On ? new long[Count] : [];
    static readonly int[] s_calls = On ? new int[Count] : [];
    static readonly int[] s_stack = On ? new int[4096] : [];
    static int s_sp;
    static long s_last;
    static long s_frameStart, s_alloc0;
    static TimeSpan s_pause0;
    static int s_gc0, s_gc1, s_gc2;
    static int s_frame;
    public static int RootSolves, SubtreeSolves, SizeStableProbes;
    public static long RecordDirtyCalls, RecordDirtyIters;
    public static int MeasRoot, MeasSub, MeasProbe;
    static readonly StringBuilder s_sb = On ? new(512) : null!;
    static readonly Dictionary<int, int> s_measPerNode = On ? new() : null!;
    static readonly HashSet<(int, float)> s_measPairs = On ? new() : null!;
    static int s_measDirty;
    public static readonly StringBuilder Events = On ? new() : null!;   // null when off: read only under On
    public static void NoteMeasure(int node, float availW, bool dirty)
    {
        s_measPerNode[node] = s_measPerNode.GetValueOrDefault(node) + 1;
        s_measPairs.Add((node, availW));
        if (dirty) s_measDirty++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Enter(int bucket)
    {
        if (!On) return;
        EnterCore(bucket);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Exit()
    {
        if (!On) return;
        ExitCore();
    }

    static void EnterCore(int bucket)
    {
        long t = Stopwatch.GetTimestamp();
        if (s_sp > 0) s_ticks[s_stack[s_sp - 1]] += t - s_last;
        if (s_sp < s_stack.Length) s_stack[s_sp] = bucket;
        s_sp++;
        s_calls[bucket]++;
        s_last = t;
    }

    static void ExitCore()
    {
        long t = Stopwatch.GetTimestamp();
        if (s_sp > 0)
        {
            s_sp--;
            if (s_sp < s_stack.Length) s_ticks[s_stack[s_sp]] += t - s_last;
        }
        s_last = t;
    }

    public static void FrameBegin()
    {
        if (!On) return;
        Array.Clear(s_ticks); Array.Clear(s_calls);
        Events.Clear();
        s_measPerNode.Clear(); s_measPairs.Clear(); s_measDirty = 0;
        RootSolves = SubtreeSolves = SizeStableProbes = 0; RecordDirtyCalls = RecordDirtyIters = 0; MeasRoot = MeasSub = MeasProbe = 0;
        s_sp = 0;
        s_pause0 = GC.GetTotalPauseDuration();
        s_gc0 = GC.CollectionCount(0); s_gc1 = GC.CollectionCount(1); s_gc2 = GC.CollectionCount(2);
        s_alloc0 = GC.GetAllocatedBytesForCurrentThread();
        s_frameStart = s_last = Stopwatch.GetTimestamp();
        EnterCore(Frame);
    }

    public static void FrameEnd(string extra)
    {
        if (!On) return;
        long t = Stopwatch.GetTimestamp();
        while (s_sp > 0) ExitCore();
        s_frame++;
        double total = (t - s_frameStart) * 1000.0 / Stopwatch.Frequency;
        if (total < s_minMs || s_path is null) return;
        var sb = s_sb.Clear();
        sb.Append("f=").Append(s_frame).Append(" total=").Append(total.ToString("0.00", CultureInfo.InvariantCulture));
        double pause = (GC.GetTotalPauseDuration() - s_pause0).TotalMilliseconds;
        sb.Append(" gcPause=").Append(pause.ToString("0.00", CultureInfo.InvariantCulture))
          .Append(" gc=").Append(GC.CollectionCount(0) - s_gc0).Append('/').Append(GC.CollectionCount(1) - s_gc1).Append('/').Append(GC.CollectionCount(2) - s_gc2)
          .Append(" allocKB=").Append((GC.GetAllocatedBytesForCurrentThread() - s_alloc0) / 1024)
          .Append(" rootSolves=").Append(RootSolves).Append(" subtreeSolves=").Append(SubtreeSolves).Append(" sizeStable=").Append(SizeStableProbes).Append(" rdCalls=").Append(RecordDirtyCalls).Append(" rdIters=").Append(RecordDirtyIters).Append(" measRoot=").Append(MeasRoot).Append(" measSub=").Append(MeasSub).Append(" measProbe=").Append(MeasProbe);
        if (s_measPerNode.Count > 0)
        {
            int g2 = 0, g4 = 0, mx = 0; foreach (var v in s_measPerNode.Values) { if (v >= 2) g2++; if (v >= 4) g4++; if (v > mx) mx = v; }
            sb.Append(" measNodes=").Append(s_measPerNode.Count).Append(" measPairs=").Append(s_measPairs.Count).Append(" meas>=2:").Append(g2).Append(" meas>=4:").Append(g4).Append(" measMax=").Append(mx).Append(" measDirtyPath=").Append(s_measDirty);
        }
        for (int i = 0; i < Count; i++)
        {
            if (s_calls[i] == 0 && s_ticks[i] == 0) continue;
            double ms = s_ticks[i] * 1000.0 / Stopwatch.Frequency;
            sb.Append(' ').Append(s_names[i]).Append('=').Append(ms.ToString("0.00", CultureInfo.InvariantCulture)).Append('/').Append(s_calls[i]);
        }
        if (extra.Length > 0) sb.Append(' ').Append(extra);
        if (Events.Length > 0) sb.Append(" ev:").Append(Events);
        sb.Append('\n');
        try { File.AppendAllText(s_path, sb.ToString()); } catch (IOException) { }
    }
}
