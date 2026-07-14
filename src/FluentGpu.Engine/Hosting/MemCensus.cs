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
    public readonly int AnimLoopTracks;
    public readonly int AnimTransitions;
    public readonly int InteractActive;
    public readonly int ScrollAnimActive;
    // host
    public readonly int PopupWindows;
    // pixel pool (bounded CPU decode/upload buffers)
    public readonly long PixelPoolRetainedBytes;
    public readonly long PixelPoolPeakBytes;
    public readonly long PixelPoolCapBytes;

    internal CensusSnapshot(AppHost host)
    {
        var scene = host.Scene;
        SceneLive = scene.LiveCount;
        SceneCapacity = scene.Capacity;
        SceneOrphans = scene.OrphanCount;
        SceneSticky = scene.ScrollBindCount;
        SceneScrollState = scene.ScrollStateCount;
        SceneBrushAnims = scene.BrushAnimCount;

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
        AnimLoopTracks = anim.LoopTrackCount;
        AnimTransitions = anim.TransitionCount;
        InteractActive = host.InteractionAnimatorCensus;
        ScrollAnimActive = host.ScrollAnimatorCensus;

        PopupWindows = host.PopupWindows.Count;

        var pixpool = host.PixelPool;
        PixelPoolRetainedBytes = pixpool.RetainedBytes;
        PixelPoolPeakBytes = pixpool.PeakRetainedBytes;
        PixelPoolCapBytes = pixpool.RetainedCapBytes;
    }

    /// <summary>Capture the engine census now. Deterministic and side-effect-free (passive reads only); the next
    /// stage's VerticalSlice checks diff two snapshots. GPU residency is not included (excluded by design).</summary>
    public static CensusSnapshot Capture(AppHost host) => new(host);
}

/// <summary>
/// FG_MEM_DIAG=1 (interval seconds = FG_MEM_DIAG_SEC, default 5): a low-overhead memory/residency census. The host
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
    private const int MetricCount = 28;
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
        var s = new CensusSnapshot(_host);

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
        _cur[k++] = s.AnimLoopTracks;
        _cur[k++] = s.AnimTransitions;
        _cur[k++] = s.InteractActive;
        _cur[k++] = s.ScrollAnimActive;
        _cur[k++] = s.PopupWindows;
        _cur[k++] = workingSet;
        _cur[k++] = s.PixelPoolRetainedBytes;   // growth-tracked; self-quiets at ≤cap
        _cur[k++] = s.PixelPoolPeakBytes;        // growth-tracked; monotone during warmup, then flat (expected)
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
        Line(sb, "  proc    ", $"workingSet={Mb(workingSet)}", 25, 1);
        Line(sb, "  scene   ", $"live={s.SceneLive} cap={s.SceneCapacity} orphans={s.SceneOrphans} sticky={s.SceneSticky} scroll={s.SceneScrollState} brush={s.SceneBrushAnims}", 0, 6);
        Line(sb, "  strings ", $"map={s.StringMap} pendReclaim={s.StringPendingReclaim} idHighWater={s.StringIdHighWater}", 6, 3);
        Line(sb, "  images  ", $"count={s.ImageCount} ready={s.ImageReady} pending={s.ImagePending} used={Mb(s.ImageUsedBytes)}", 9, 4);
        Line(sb, "  decode  ", $"inflight={s.DecodeInflight} canceledPending={s.DecodeCanceledPending}", 13, 2);
        Line(sb, "  recon   ", $"components={s.Components} nodeBindings={s.NodeBindings} virtuals={s.VirtualBoundaries} providers={s.Providers}", 15, 4);
        Line(sb, "  anim    ", $"tracks={s.AnimTracks} loops={s.AnimLoopTracks} transitions={s.AnimTransitions} interact={s.InteractActive} scroll={s.ScrollAnimActive}", 19, 5);
        Line(sb, "  host    ", $"popupWindows={s.PopupWindows}", 24, 1);
        Line(sb, "  pixpool ", $"retained={Mb(s.PixelPoolRetainedBytes)} peak={Mb(s.PixelPoolPeakBytes)} cap={Mb(s.PixelPoolCapBytes)}", 26, 2);

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

    private static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0}MB");
}
