using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Hosting;

/// <summary>
/// The per-frame performance ledger: CPU, memory and GPU for EVERY frame, in four preallocated single-producer POD rings
/// (records: FrameLedger.Records.cs; dump + CSV: FrameLedgerFile.cs) — the measuring stick a before/after optimisation is
/// judged by, so it is exact where it can be and cheap everywhere.
/// <list type="bullet">
/// <item><b>UI</b> (<see cref="LedgerUiFrame"/>): one record per <see cref="AppHost.RunFrame"/> of the owning host, early-outs
/// included — QPC stamps (entry, pump, Paint's phases, return, the loop wait before it), the UI thread's cycles and allocated
/// bytes, cumulative GC counts / pause time, the PROCESS's cumulative cycles, the publish sequence and the record's work counts.
/// Producer: the UI thread.</item>
/// <item><b>Render</b> (<see cref="LedgerRenderTurn"/>): one per render-thread turn — stamps, the present split, the thread's cycles
/// and allocations, the presented publication (the join key to the UI stream) and the submit sequence (the join key to GPU).
/// Producer: the render thread.</item>
/// <item><b>GPU</b> (<see cref="LedgerGpuFrame"/>): one per retired whole-frame timestamp pair, with the per-pass timeline of the same
/// submit while pass timing is on. Producer: the render thread (it polls the swapchain after each turn).</item>
/// <item><b>Memory</b> (<see cref="LedgerMemorySample"/>): working set, private bytes, managed heap, VRAM, image cache, glyph atlas at
/// <see cref="MemoryIntervalMs"/> on the ledger's own sampler thread (an idle loop parked in its wait is still sampled).</item>
/// <item><b>Audio</b> (<see cref="LedgerAudioSample"/>): the audio health counters at the same cadence: buffer-drained edges and
/// the window's minimum endpoint padding apart from app-side xruns (<see cref="FluentGpu.Media.AudioHealth"/>).</item>
/// </list>
/// <para><b>Cost.</b> Off (the default): a static bool read per RunFrame and per render turn gates every ledger read and record; what
/// stays unconditional is a handful of plain field stores the frame and the turn leave for it (the pump's end stamp, the exit gate,
/// the wake mask, the present decision's stamps) — no counter read, no syscall. On: a record is a struct copy into a ring slot plus a
/// few counter reads (thread cycles, GetThreadTimes, the thread's allocation counter, QPC, and once per UI frame
/// QueryProcessCycleTime, ~10 µs with ~100 threads, read AFTER the frame's end stamp so it is never inside a measured span); nothing
/// on any record path allocates (pinned by FrameLedgerTests). The rings (~15 MB at the default capacity) and the sampler thread
/// exist only after <see cref="Enable"/>.</para>
/// <para><b>Owner.</b> The ledger follows ONE host: the first non-detached <see cref="AppHost"/> that runs a frame while it is enabled
/// (or the one passed to <see cref="Attach"/>); a pop-out or a second host never writes into the rings, so each stays single-producer.</para>
/// <para><b>Cycles.</b> Per-thread and per-process CPU are CYCLE counts (QueryThreadCycleTime / QueryProcessCycleTime). They become
/// milliseconds through ONE rate, <see cref="CyclesPerMs"/>: the process's cycles per millisecond of process CPU time
/// (Δ QueryProcessCycleTime / Δ GetProcessTimes) since <see cref="Enable"/> — the counter's EFFECTIVE rate at the clocks the cores
/// actually ran (on a DVFS core the counter follows the clock, so a fixed peak rate would under-read every slow-clocked span).
/// Until a second of CPU time has accrued it is a frozen spin calibration at enable. A consumer comparing windows applies one rate
/// to all of them (<see cref="LedgerSnapshot.WithRate"/>); the GetThreadTimes totals in the UI / render records are the rate-free
/// cross-check.</para>
/// <para>Enabled from the command line with <c>--fg ledger</c> or <c>--fg ledger=PATH</c> (FluentApp dumps <c>PATH</c> + one CSV per
/// stream at exit), or programmatically (<see cref="Enable"/> / <see cref="Mark"/> / <see cref="Snapshot"/>, the bench's door).</para>
/// </summary>
public static class FrameLedger
{
    /// <summary>Default UI / render / GPU ring capacity: 32768 records = 136 s at 240 Hz (power of two).</summary>
    public const int DefaultCapacity = 32768;
    /// <summary>Memory ring capacity: 4096 samples = 17 min at 4 Hz.</summary>
    public const int MemoryCapacity = 4096;

    private static volatile bool s_enabled;
    private static LedgerRing<LedgerUiFrame>? s_ui;
    private static LedgerRing<LedgerRenderTurn>? s_render;
    private static LedgerRing<LedgerGpuFrame>? s_gpu;
    private static LedgerRing<LedgerMemorySample>? s_memory;
    private static LedgerRing<LedgerAudioSample>? s_audio;
    private static readonly object s_memoryGate = new();
    private static AppHost? s_owner;
    private static long s_enabledQpc;
    private static double s_spinCyclesPerMs;
    private static long s_enabledMemory;
    private static Thread? s_sampler;
    private static AutoResetEvent? s_samplerStop;
    private static int s_samplerRun;
    private static int s_gcInfoTick;
    private static long s_gcHeap, s_gcCommitted;
    private static GpuPassTiming[]? s_passScratch;

    /// <summary>True while the ledger records. The ONE check every hot path makes.</summary>
    public static bool Enabled => s_enabled;

    /// <summary>The file <c>--fg ledger=PATH</c> asked for (null = none, or the switch was given without a path).</summary>
    public static string? DumpPath { get; set; }

    /// <summary>The memory sampler's period (ms). Read when the sampler starts.</summary>
    public static int MemoryIntervalMs { get; set; } = 250;

    /// <summary>The process's cumulative cycle counter (QueryProcessCycleTime), installed by the platform; null reads 0.</summary>
    public static Func<ulong>? ProcessCycles { get; set; }

    /// <summary>The platform half of the memory sample; null leaves those fields 0 (headless).</summary>
    public static LedgerPlatformSampler? PlatformSampler { get; set; }

    /// <summary>The CALLING thread's cumulative CPU time (GetThreadTimes kernel + user, 100 ns), installed by the platform; null reads 0.</summary>
    public static Func<long>? ThreadCpuTime { get; set; }

    /// <summary>The cycles-per-ms the export converts with (see the class remarks): the effective rate since <see cref="Enable"/>,
    /// or the spin calibration before a second of CPU time accrued; 0 = no counter.</summary>
    public static double CyclesPerMs
    {
        get
        {
            if (s_memory is { } ring)
            {
                LedgerMemorySample[] mem;
                lock (s_memoryGate) mem = ring.CopySince(Volatile.Read(ref s_enabledMemory));
                double r = RateBetween(mem, 1000);
                if (double.IsFinite(r)) return r;
            }
            return s_spinCyclesPerMs;
        }
    }

    /// <summary>Δ process cycles / Δ process CPU ms between the first and the last of <paramref name="samples"/>; NaN when the CPU time
    /// between them is under <paramref name="minCpuMs"/> or either counter is missing.</summary>
    public static double RateBetween(ReadOnlySpan<LedgerMemorySample> samples, double minCpuMs)
    {
        if (samples.Length < 2) return double.NaN;
        ref readonly var a = ref samples[0];
        ref readonly var b = ref samples[^1];
        if (a.ProcessCyclesTotal == 0 || a.ProcessCpuTicksTotal == 0 || b.ProcessCyclesTotal <= a.ProcessCyclesTotal) return double.NaN;
        double cpuMs = (b.ProcessCpuTicksTotal - a.ProcessCpuTicksTotal) / 10_000.0;
        return cpuMs >= minCpuMs ? (b.ProcessCyclesTotal - a.ProcessCyclesTotal) / cpuMs : double.NaN;
    }

    /// <summary>The calling thread's cumulative CPU time (100 ns), 0 without a platform source.</summary>
    internal static long ReadThreadCpuTime() => ThreadCpuTime is { } f ? f() : 0L;

    /// <summary>QPC at the last <see cref="Enable"/>.</summary>
    public static long EnabledQpc => Volatile.Read(ref s_enabledQpc);

    /// <summary>The host the ledger follows (null until one ran a frame).</summary>
    public static AppHost? Owner => Volatile.Read(ref s_owner);

    /// <summary>Allocate the rings (once per capacity), calibrate the cycle rate on the calling thread, start the memory sampler and
    /// begin recording. Idempotent while enabled. Any thread; not a hot path.</summary>
    public static void Enable(int capacity = DefaultCapacity)
    {
        if (s_enabled) return;
        capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(capacity, 64, 1 << 22));
        if (s_ui is null || s_ui.Capacity != capacity)
        {
            s_ui = new LedgerRing<LedgerUiFrame>(capacity);
            s_render = new LedgerRing<LedgerRenderTurn>(capacity);
            s_gpu = new LedgerRing<LedgerGpuFrame>(capacity);
        }
        s_memory ??= new LedgerRing<LedgerMemorySample>(MemoryCapacity);
        s_audio ??= new LedgerRing<LedgerAudioSample>(MemoryCapacity);
        s_passScratch ??= new GpuPassTiming[GpuPassTimeline.MaxPasses];
        s_spinCyclesPerMs = CalibrateSpin();   // frozen for this enable: the fallback until the effective rate exists
        Volatile.Write(ref s_enabledQpc, Stopwatch.GetTimestamp());
        Volatile.Write(ref s_enabledMemory, s_memory.Count);
        s_enabled = true;
        StartSampler();
        SampleMemory();   // a sample at the window's start, whatever the sampler's phase
    }

    /// <summary>Stop recording (the rings keep what they hold until the next <see cref="Enable"/> overwrites it) and release the owner.</summary>
    public static void Disable()
    {
        if (!s_enabled) return;
        s_enabled = false;
        StopSampler();
        Volatile.Write(ref s_owner, null);
    }

    /// <summary>Follow <paramref name="host"/> from now on (replacing any owner). UI thread of that host.</summary>
    public static void Attach(AppHost host) => Volatile.Write(ref s_owner, host);

    /// <summary>The calling host's claim: true when it owns the ledger (claiming it when nobody does).</summary>
    internal static bool TryOwn(AppHost host)
    {
        AppHost? o = Volatile.Read(ref s_owner);
        if (ReferenceEquals(o, host)) return true;
        return o is null && Interlocked.CompareExchange(ref s_owner, host, null) is null;
    }

    internal static bool IsOwner(AppHost host) => ReferenceEquals(Volatile.Read(ref s_owner), host);

    /// <summary>A disposing host lets go (no-op unless it is the owner).</summary>
    internal static void Release(AppHost host) => Interlocked.CompareExchange(ref s_owner, null, host);

    /// <summary>The ring positions now: <see cref="Snapshot"/> from it is the window that starts here.</summary>
    public static LedgerMark Mark()
        => new(s_ui?.Count ?? 0, s_render?.Count ?? 0, s_gpu?.Count ?? 0, s_memory?.Count ?? 0, Stopwatch.GetTimestamp(), s_audio?.Count ?? 0);

    /// <summary>A copy of every record since <paramref name="from"/> (default: everything the rings still hold). Allocates; any
    /// thread. A window longer than a ring's capacity keeps its newest records.</summary>
    public static LedgerSnapshot Snapshot(LedgerMark from = default)
    {
        long origin = from.Qpc != 0 ? from.Qpc : Volatile.Read(ref s_enabledQpc);
        LedgerMemorySample[] mem;
        LedgerAudioSample[] audio;
        lock (s_memoryGate)
        {
            mem = s_memory?.CopySince(from.Memory) ?? [];
            audio = s_audio?.CopySince(from.Audio) ?? [];
        }
        return new LedgerSnapshot
        {
            QpcFrequency = Stopwatch.Frequency,
            CyclesPerMs = CyclesPerMs,
            Processors = Environment.ProcessorCount,
            OriginQpc = origin,
            EndQpc = Stopwatch.GetTimestamp(),
            Ui = s_ui?.CopySince(from.Ui) ?? [],
            Render = s_render?.CopySince(from.Render) ?? [],
            Gpu = s_gpu?.CopySince(from.Gpu) ?? [],
            Memory = mem,
            Audio = audio,
        };
    }

    // ── record paths (zero allocation) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>UI THREAD of the owner. Stamps <see cref="LedgerUiFrame.Seq"/> and pushes.</summary>
    internal static void RecordUi(ref LedgerUiFrame r)
    {
        if (s_ui is not { } ring) return;
        r.Seq = (ulong)ring.Count;
        ring.Push(in r);
    }

    /// <summary>RENDER THREAD of the owner.</summary>
    internal static void RecordRender(ref LedgerRenderTurn r)
    {
        if (s_render is not { } ring) return;
        r.Seq = (ulong)ring.Count;
        ring.Push(in r);
    }

    /// <summary>RENDER THREAD of the owner.</summary>
    internal static void RecordGpu(ref LedgerGpuFrame r)
    {
        if (s_gpu is not { } ring) return;
        r.Seq = (ulong)ring.Count;
        ring.Push(in r);
    }

    /// <summary>The render thread's scratch for one pass-timeline copy (allocated by <see cref="Enable"/>).</summary>
    internal static GpuPassTiming[]? PassScratch => s_passScratch;

    /// <summary>The process's cumulative cycles (0 without a platform counter).</summary>
    internal static ulong ReadProcessCycles() => ProcessCycles is { } f ? f() : 0UL;

    /// <summary>Spin the calling thread three times for 8 ms and keep the highest cycles-per-ms (0 without a counter). Enable only.</summary>
    private static double CalibrateSpin()
    {
        if (!ThreadCycles.Available) return 0;
        double best = 0;
        long spin = Stopwatch.Frequency / 125;   // 8 ms
        for (int i = 0; i < 3; i++)
        {
            ulong c0 = ThreadCycles.Read();
            long t0 = Stopwatch.GetTimestamp(), t1;
            do { t1 = Stopwatch.GetTimestamp(); } while (t1 - t0 < spin);
            ulong c1 = ThreadCycles.Read();
            if (c1 > c0) best = Math.Max(best, (c1 - c0) / ((t1 - t0) * 1000.0 / Stopwatch.Frequency));
        }
        return best;
    }

    // ── the memory sampler ──────────────────────────────────────────────────────────────────────────────────────────

    private static void StartSampler()
    {
        if (MemoryIntervalMs <= 0 || Interlocked.Exchange(ref s_samplerRun, 1) != 0) return;
        var stop = new AutoResetEvent(false);
        s_samplerStop = stop;
        int period = Math.Max(10, MemoryIntervalMs);
        var t = new Thread(() =>
        {
            while (!stop.WaitOne(period))
                if (s_enabled) SampleMemory();
        }) { IsBackground = true, Name = "fgpu-ledger-mem", Priority = ThreadPriority.BelowNormal };
        s_sampler = t;
        t.Start();
    }

    private static void StopSampler()
    {
        if (Interlocked.Exchange(ref s_samplerRun, 0) == 0) return;
        s_samplerStop?.Set();
        s_sampler?.Join(1000);
        s_samplerStop?.Dispose();
        s_samplerStop = null;
        s_sampler = null;
    }

    /// <summary>Take one memory sample now (any thread; serialized). The sampler calls it at <see cref="MemoryIntervalMs"/>.</summary>
    public static void SampleMemory()
    {
        if (s_memory is not { } ring) return;
        lock (s_memoryGate)
        {
            var p = default(LedgerPlatformSample);
            PlatformSampler?.Invoke(ref p);
            // GetGCMemoryInfo allocates its info object (a few hundred bytes), so it runs on every fourth sample only, here on the
            // sampler thread, never on a frame thread.
            if ((s_gcInfoTick++ & 3) == 0)
            {
                GCMemoryInfo gi = GC.GetGCMemoryInfo();
                s_gcHeap = gi.HeapSizeBytes;
                s_gcCommitted = gi.TotalCommittedBytes;
            }
            var images = Volatile.Read(ref s_owner)?.Images;
            var s = new LedgerMemorySample
            {
                Seq = (ulong)ring.Count,
                Qpc = Stopwatch.GetTimestamp(),
                WorkingSetBytes = p.WorkingSetBytes,
                PrivateBytes = p.PrivateBytes,
                ManagedBytes = GC.GetTotalMemory(false),
                GcHeapBytes = s_gcHeap,
                GcCommittedBytes = s_gcCommitted,
                TotalAllocatedBytes = GC.GetTotalAllocatedBytes(false),
                VramLocalBytes = p.VramLocalBytes,
                VramNonLocalBytes = p.VramNonLocalBytes,
                VramLocalBudgetBytes = p.VramLocalBudgetBytes,
                TrackedGpuBytes = p.TrackedGpuBytes,
                ImageCacheBytes = images is null ? 0 : images.UsedBytes + images.DerivedUsedBytes,
                ImageCount = images?.Count ?? 0,
                GlyphAtlasBytes = p.GlyphAtlasBytes,
                ProcessCyclesTotal = ReadProcessCycles(),
                ProcessCpuTicksTotal = p.ProcessCpuTicksTotal,
                GcPauseTicksTotal = GC.GetTotalPauseDuration().Ticks,
                Gc0Total = GC.CollectionCount(0),
                Gc1Total = GC.CollectionCount(1),
                Gc2Total = GC.CollectionCount(2),
            };
            ring.Push(in s);
            if (s_audio is { } audioRing)
            {
                var a = FluentGpu.Media.AudioHealth.Sample();
                var ar = new LedgerAudioSample
                {
                    Seq = (ulong)audioRing.Count, Qpc = s.Qpc, DeviceWritesTotal = a.DeviceWrites, BufferDrainedTotal = a.BufferDrainedEdges,
                    XrunsTotal = a.Xruns, XrunFramesTotal = a.XrunFrames, PaddingMinFrames = a.PaddingMinFrames,
                    BufferFrames = a.BufferFrames, Rate = a.Rate,
                };
                audioRing.Push(in ar);
            }
        }
    }

    /// <summary>Write <see cref="DumpPath"/> (binary, <see cref="FrameLedgerFile"/>) and one CSV per stream beside it. Returns the
    /// path written, null when there is no path or the write failed (reported on stderr). Not a hot path.</summary>
    public static string? DumpIfRequested()
    {
        if (DumpPath is not { Length: > 0 } path || s_ui is null) return null;
        try
        {
            var snap = Snapshot();
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            snap.WriteFile(full);
            snap.WriteCsv(Path.GetDirectoryName(full) ?? ".", Path.GetFileNameWithoutExtension(full));
            Console.Error.WriteLine($"[ledger] wrote {full} (ui={snap.Ui.Length} render={snap.Render.Length} gpu={snap.Gpu.Length} mem={snap.Memory.Length} audio={snap.Audio.Length}) + CSVs");
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine("[ledger] dump failed: " + ex.Message);
            return null;
        }
    }
}

/// <summary>A fixed-capacity single-producer ring of unmanaged records: the producer writes the slot, then publishes the count
/// (release); a reader on any thread copies a consistent prefix. Power-of-two capacity, so the index is a mask.</summary>
internal sealed class LedgerRing<T> where T : unmanaged
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _count;

    public LedgerRing(int capacity)
    {
        _items = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _items.Length;

    /// <summary>Records ever pushed (monotonic).</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>PRODUCER THREAD ONLY.</summary>
    public void Push(in T item)
    {
        long i = _count;
        _items[(int)(i & _mask)] = item;
        Volatile.Write(ref _count, i + 1);
    }

    /// <summary>Every record pushed at or after <paramref name="from"/> still in the ring, oldest first. The copy is not atomic
    /// against the producer: if it laps the reader (pushes more than the capacity minus the window while the copy runs) the oldest
    /// copied slots may already hold newer records. A window well inside the capacity (the bench's 10 s against 136 s) never laps.</summary>
    public T[] CopySince(long from)
    {
        long end = Count;
        long start = Math.Max(Math.Max(0, from), end - _items.Length);
        if (end <= start) return [];
        var dst = new T[end - start];
        for (long i = start; i < end; i++) dst[i - start] = _items[(int)(i & _mask)];
        return dst;
    }
}
