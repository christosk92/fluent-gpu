using System.Diagnostics;
using System.Globalization;

namespace FluentGpu.Hosting;

/// <summary>
/// A deterministic, side-effect-free snapshot of the engine's live-object census (the counts the MemCensus report
/// prints and the gate diffs). GPU residency is excluded — it lives behind the host's optional gpu hooks and is not
/// reproducible headless. Captured via <see cref="Capture"/>; works on the headless path.
/// </summary>
public readonly struct CensusSnapshot
{
    // scene
    public readonly int SceneLive;
    public readonly int SceneCapacity;
    public readonly int SceneOrphans;
    public readonly int SceneSticky;
    public readonly int SceneScrollState;
    public readonly int SceneBrushAnims;
    // Publisher-owned array payload, not managed heap size or working set. Sparse indices/headers are excluded.
    public readonly int SnapshotSlots;
    public readonly long SnapshotIndexedBytes, SnapshotTextStyleBytes, SnapshotCapacity, SnapshotRequired;
    public readonly int SnapshotReclaims;
    public readonly long SnapshotReclaimedIndexedBytes;
    // strings
    public readonly int StringMap;
    public readonly int StringPendingReclaim;
    public readonly int StringIdHighWater;
    // images
    public readonly int ImageCount;
    public readonly int ImageReady;
    public readonly int ImagePending;
    public readonly long ImageUsedBytes;
    // decode
    public readonly int DecodeInflight;
    public readonly int DecodeCanceledPending;
    // reconciler
    public readonly int Components;
    public readonly int NodeBindings;
    public readonly int VirtualBoundaries;
    public readonly int Providers;
    // anim
    public readonly int AnimTracks;
    /// <summary>Live LOOPING rows at the DISPLAY rate (every loop without an explicit <c>Cadence.At</c>): each keeps the
    /// frame loop at the panel rate for as long as it runs. A steady non-zero value here on an idle page is the leak to
    /// chase.</summary>
    public readonly int AnimDisplayRateLoops;
    public readonly int AnimTransitions;
    public readonly int InteractActive;
    public readonly int ScrollAnimActive;
    // host
    public readonly int PopupWindows;
    // pixel pool (bounded CPU decode/upload buffers)
    public readonly long PixelPoolRetainedBytes;
    public readonly long PixelPoolPeakBytes;
    public readonly long PixelPoolCapBytes;
    // hidden-window memory stage (HiddenMemoryPolicy): 0 = Visible, 1 = Shallow, 2 = Deep released; and whether the host is parked now
    public readonly byte HiddenStage;
    public readonly bool HostParked;

    internal CensusSnapshot(AppHost host)
    {
        var scene = host.Scene;
        SceneLive = scene.LiveCount;
        SceneCapacity = scene.Capacity;
        SceneOrphans = scene.OrphanCount;
        SceneSticky = scene.ScrollEffectCount;
        SceneScrollState = scene.ScrollStateCount;
        SceneBrushAnims = scene.BrushAnimCount;
        var snapshots = host.SceneCapacityCensus;
        SnapshotSlots = snapshots.initializedSlots;
        SnapshotIndexedBytes = snapshots.indexedBytes;
        SnapshotTextStyleBytes = snapshots.textStyleBytes;
        SnapshotCapacity = snapshots.totalCapacity;
        SnapshotRequired = snapshots.highestRequired;
        SnapshotReclaims = host.SceneCapacityReclaims;
        SnapshotReclaimedIndexedBytes = host.ReclaimedSceneIndexedBytes;

        var strings = host.Strings;
        StringMap = strings.MapCount;
        StringPendingReclaim = strings.PendingReclaimCount;
        StringIdHighWater = strings.IdHighWater;

        var images = host.Images;
        ImageCount = images.Count;
        ImageReady = images.ReadyCount;
        ImagePending = images.PendingCount;
        ImageUsedBytes = images.UsedBytes;
        DecodeInflight = images.DecodeInflight;
        DecodeCanceledPending = images.DecodeCanceledPending;

        var rec = host.Reconciler;
        Components = rec.ComponentCount;
        NodeBindings = rec.NodeBindingCount;
        VirtualBoundaries = rec.VirtualBoundaryCount;
        Providers = rec.ProviderCount;

        var anim = host.Animation;
        AnimTracks = anim.TrackCount;
        AnimDisplayRateLoops = anim.DisplayRateLoopCount;
        AnimTransitions = anim.TransitionCount;
        InteractActive = host.InteractionAnimatorCensus;
        ScrollAnimActive = host.ScrollActiveCensus;

        PopupWindows = host.PopupWindows.Count;

        var pixpool = host.PixelPool;
        PixelPoolRetainedBytes = pixpool.RetainedBytes;
        PixelPoolPeakBytes = pixpool.PeakRetainedBytes;
        PixelPoolCapBytes = pixpool.RetainedCapBytes;

        HiddenStage = (byte)host.HiddenStageCensus;
        HostParked = host.IsParked;
    }

    /// <summary>Capture the engine census now. Deterministic and side-effect-free (passive reads only); the next
    /// stage's VerticalSlice checks diff two snapshots. GPU residency is not included (excluded by design).</summary>
    public static CensusSnapshot Capture(AppHost host) => new(host);
}

/// <summary>
/// --fg mem (interval seconds = --fg mem=N, default 5): a low-overhead memory/residency census. The host
/// ticks <see cref="MaybeReport"/> once per frame (a cheap timestamp compare when on; nothing when off). Every
/// interval it prints a compact multi-line "[memcensus]" block to stderr: the managed GC picture
/// (<see cref="GC.GetGCMemoryInfo()"/> heap/committed + collection counts + an allocation rate), the process working
/// set, then the engine census read through the subsystem accessors, and — when the app layer wired them — the GPU
/// residency hooks. Numerics that rise for 3 consecutive samples get an "↑GROW" marker (a leak smell). State is
/// allocation-light: two fixed snapshots + one per-metric streak array, no per-sample dictionaries.
/// </summary>
internal sealed class MemCensus
{
    private readonly AppHost _host;
    private readonly double _intervalSec;
    private long _nextSampleTicks;
    private long _lastAllocBytes;
    private long _lastSampleTicks;

    // Growth tracking: the previous numeric vector + a per-metric "consecutive increases" streak.
    private const int MetricCount = 32;
    private readonly long[] _prev = new long[MetricCount];
    private readonly int[] _grewStreak = new int[MetricCount];
    private bool _havePrev;
    private readonly long[] _cur = new long[MetricCount];   // reused scratch (no per-sample allocation)

    public MemCensus(AppHost host, double intervalSec)
    {
        _host = host;
        _intervalSec = intervalSec < 0.1 ? 0.1 : intervalSec;
    }

    /// <summary>Once-per-frame tick. Cheap timestamp compare; emits + resets only when the interval elapses.</summary>
    public void MaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        if (_nextSampleTicks == 0)
        {
            _nextSampleTicks = now + (long)(_intervalSec * Stopwatch.Frequency);
            _lastAllocBytes = GC.GetTotalAllocatedBytes(precise: false);
            _lastSampleTicks = now;
            return;
        }
        if (now < _nextSampleTicks) return;
        Report(now);
        _nextSampleTicks = now + (long)(_intervalSec * Stopwatch.Frequency);
    }

    private void Report(long now)
    {
        double sec = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
        if (sec <= 0.0) sec = _intervalSec;
        long allocNow = GC.GetTotalAllocatedBytes(precise: false);
        double allocRateKb = (allocNow - _lastAllocBytes) / sec / 1024.0;
        _lastAllocBytes = allocNow;
        _lastSampleTicks = now;

        var gc = GC.GetGCMemoryInfo();
        long workingSet = Environment.WorkingSet;
        long handles = ProcessHandleCount();
        var s = new CensusSnapshot(_host);
        var media = FluentGpu.Media.MediaCensus.Capture();   // F197: the media stack's named owners (engines, protected sessions, prepared, runtime)

        // Pack the growth-tracked numerics into the fixed vector, compute streaks.
        int k = 0;
        _cur[k++] = s.SceneLive;
        _cur[k++] = s.SceneCapacity;
        _cur[k++] = s.SceneOrphans;
        _cur[k++] = s.SceneSticky;
        _cur[k++] = s.SceneScrollState;
        _cur[k++] = s.SceneBrushAnims;
        _cur[k++] = s.StringMap;
        _cur[k++] = s.StringPendingReclaim;
        _cur[k++] = s.StringIdHighWater;
        _cur[k++] = s.ImageCount;
        _cur[k++] = s.ImageReady;
        _cur[k++] = s.ImagePending;
        _cur[k++] = s.ImageUsedBytes;
        _cur[k++] = s.DecodeInflight;
        _cur[k++] = s.DecodeCanceledPending;
        _cur[k++] = s.Components;
        _cur[k++] = s.NodeBindings;
        _cur[k++] = s.VirtualBoundaries;
        _cur[k++] = s.Providers;
        _cur[k++] = s.AnimTracks;
        _cur[k++] = s.AnimDisplayRateLoops;
        _cur[k++] = s.AnimTransitions;
        _cur[k++] = s.InteractActive;
        _cur[k++] = s.ScrollAnimActive;
        _cur[k++] = s.PopupWindows;
        _cur[k++] = workingSet;
        _cur[k++] = handles;                     // growth-tracked: a leaked NT handle (a swap-chain handle per source switch, F198) climbs
        _cur[k++] = s.PixelPoolRetainedBytes;   // growth-tracked; self-quiets at ≤cap
        _cur[k++] = s.PixelPoolPeakBytes;        // growth-tracked; monotone during warmup, then flat (expected)
        _cur[k++] = media.VideoEngines;          // growth-tracked: an engine per rebuild that is never disposed climbs here (F197)
        _cur[k++] = media.ProtectedSessions;
        _cur[k++] = media.ProtectedStoreBytes;
        // k == MetricCount

        if (_havePrev)
            for (int i = 0; i < MetricCount; i++)
                _grewStreak[i] = _cur[i] > _prev[i] ? _grewStreak[i] + 1 : 0;
        Array.Copy(_cur, _prev, MetricCount);
        _havePrev = true;

        var sb = new System.Text.StringBuilder(512);
        sb.Append("[memcensus]\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"  gc      heap={Mb(gc.HeapSizeBytes)} committed={Mb(gc.TotalCommittedBytes)} gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} alloc={allocRateKb:0.0}KB/s\n");
        // Metric slots per line are contiguous in the packed vector (see the assignment order above) — pass a
        // (start,count) range so the grow-flag scan stays allocation-free.
        Line(sb, "  proc    ", $"workingSet={Mb(workingSet)} handles={(handles >= 0 ? handles.ToString(CultureInfo.InvariantCulture) : "n/a")}", 25, 2);
        Line(sb, "  scene   ", $"live={s.SceneLive} cap={s.SceneCapacity} orphans={s.SceneOrphans} sticky={s.SceneSticky} scroll={s.SceneScrollState} brush={s.SceneBrushAnims}", 0, 6);
        Line(sb, "  strings ", $"map={s.StringMap} pendReclaim={s.StringPendingReclaim} idHighWater={s.StringIdHighWater}", 6, 3);
        sb.Append(CultureInfo.InvariantCulture,
            $"  snapshot slots={s.SnapshotSlots} indexedBytes={s.SnapshotIndexedBytes} textStyleBytes={s.SnapshotTextStyleBytes} capacity={s.SnapshotCapacity} required={s.SnapshotRequired} reclaims={s.SnapshotReclaims} reclaimedIndexedBytes={s.SnapshotReclaimedIndexedBytes}\n");
        Line(sb, "  images  ", $"count={s.ImageCount} ready={s.ImageReady} pending={s.ImagePending} used={Mb(s.ImageUsedBytes)}", 9, 4);
        Line(sb, "  decode  ", $"inflight={s.DecodeInflight} canceledPending={s.DecodeCanceledPending}", 13, 2);
        Line(sb, "  recon   ", $"components={s.Components} nodeBindings={s.NodeBindings} virtuals={s.VirtualBoundaries} providers={s.Providers}", 15, 4);
        Line(sb, "  anim    ", $"tracks={s.AnimTracks} displayRateLoops={s.AnimDisplayRateLoops} transitions={s.AnimTransitions} interact={s.InteractActive} scroll={s.ScrollAnimActive}", 19, 5);
        Line(sb, "  host    ", $"popupWindows={s.PopupWindows}", 24, 1);
        Line(sb, "  pixpool ", $"retained={Mb(s.PixelPoolRetainedBytes)} peak={Mb(s.PixelPoolPeakBytes)} cap={Mb(s.PixelPoolCapBytes)}", 27, 2);
        // Named media owners. The protected decode runs inside mfpmp.exe (another process), so none of it is in this line or in the
        // working set above: the segment store is CPU memory in THIS process, the swap chains / D3D11 devices are VRAM on the adapter.
        Line(sb, "  media   ", $"{media.Format()} storeMB={Mb(media.ProtectedStoreBytes)}", 29, 3);

        if (_host.GpuResources is { } gpuRes)
        {
            var (bytes, count) = gpuRes();
            sb.Append(CultureInfo.InvariantCulture, $"  gpu     bytes={Mb(bytes)} count={count}");
            if (_host.GpuDetail is { } detail) { string d = detail(); if (d.Length > 0) { sb.Append(" | "); sb.Append(d); } }
            sb.Append('\n');
        }

        Console.Error.Write(sb.ToString());
    }

    /// <summary>Append one census line, flagging it ↑GROW if any metric in the contiguous slot range
    /// [<paramref name="slotStart"/>, slotStart+<paramref name="slotCount"/>) has risen for ≥3 consecutive samples.</summary>
    private void Line(System.Text.StringBuilder sb, string label, string body, int slotStart, int slotCount)
    {
        sb.Append(label);
        sb.Append(body);
        bool grew = false;
        for (int i = slotStart; i < slotStart + slotCount; i++)
            if (_grewStreak[i] >= 3) { grew = true; break; }
        if (grew) sb.Append(" ↑GROW");
        sb.Append('\n');
    }

    /// <summary>The process's open kernel handle count (<see cref="Process.HandleCount"/>, the quantity Win32
    /// <c>GetProcessHandleCount</c> answers), or -1 where the platform does not answer. Read once per census interval, never per frame.</summary>
    private static long ProcessHandleCount()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.HandleCount;
        }
        catch (Exception) { return -1; }
    }

    private static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0}MB");
}
