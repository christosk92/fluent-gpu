using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Cold copy of <c>IDXGIAdapter3.QueryVideoMemoryInfo</c> plus engine resource tallies. The render thread
/// publishes (it owns the COM adapter pointer); UI / probes read this struct only — no DXGI types cross the seam.
/// </summary>
public readonly struct GpuVideoMemorySnapshot
{
    public ulong LocalCurrentUsage { get; init; }
    public ulong LocalBudget { get; init; }
    public ulong NonLocalCurrentUsage { get; init; }
    public ulong NonLocalBudget { get; init; }
    public long TrackedResourceBytes { get; init; }
    public int TrackedResourceCount { get; init; }
    public int AtlasImages { get; init; }
    public int AtlasPages { get; init; }
    public int CachedGlyphs { get; init; }
    public bool Valid { get; init; }

    // ── Per-window display mode (mixed-refresh-rate audit) ──────────────────────────────────────────────────────
    // Published from D3D12Device.SamplePresentTopology's 1 Hz DWM branch (the same edge that already resolves the
    // present-adapter topology below) via DisplayInfo.ForWindow/ForPrimary — never on the read path, so this stays a
    // plain POD an app settings/diagnostics timer can poll with zero DXGI/user32 calls of its own.
    /// <summary>The active swapchain's monitor device name (e.g. <c>\\.\DISPLAY1</c>), or null until first resolved
    /// (<c>default(GpuVideoMemorySnapshot)</c> — mirrors <see cref="DisplayModeValid"/> being false).</summary>
    public string? MonitorDeviceName { get; init; }
    /// <summary>Active monitor's refresh rate as the raw rational (numerator/denominator) — never collapse to a
    /// nominal integer Hz before storing; see <see cref="MonitorRefreshHz"/> for the derived value.</summary>
    public int MonitorRefreshNumerator { get; init; }
    public int MonitorRefreshDenominator { get; init; }
    /// <summary>QPC-tick period matching <see cref="FluentGpu.Rhi.PresentStats.RefreshPeriodQpc"/> — what the frame
    /// pacer actually consumes. Sourced from this window's monitor when known; DWM-global otherwise.</summary>
    public long MonitorRefreshPeriodQpc { get; init; }
    /// <summary>The PRIMARY monitor's rational refresh rate, recorded beside the active one so a mismatch (the whole
    /// point of this audit — engine pacing used to always follow the primary monitor regardless of which one the
    /// window was actually on) is self-evident to a reader without cross-referencing anything else.</summary>
    public int PrimaryRefreshNumerator { get; init; }
    public int PrimaryRefreshDenominator { get; init; }
    /// <summary>False until <c>DisplayInfo.ForWindow</c> has successfully resolved at least once.</summary>
    public bool DisplayModeValid { get; init; }
    /// <summary>Derived Hz for the active monitor (0 when <see cref="DisplayModeValid"/> is false or the rational is degenerate).</summary>
    public double MonitorRefreshHz => MonitorRefreshDenominator > 0 ? (double)MonitorRefreshNumerator / MonitorRefreshDenominator : 0.0;
    /// <summary>Derived Hz for the primary monitor (0 when the rational is degenerate).</summary>
    public double PrimaryRefreshHz => PrimaryRefreshDenominator > 0 ? (double)PrimaryRefreshNumerator / PrimaryRefreshDenominator : 0.0;

    /// <summary>The swapchain buffer count in flight (constant across swapchains today — <c>D3D12Device.FRAME_COUNT</c>).</summary>
    public uint SwapchainBufferCount { get; init; }
    /// <summary>The <c>IDXGISwapChain2.SetMaximumFrameLatency</c> value applied at creation (<c>D3D12Device.MAX_FRAME_LATENCY</c>).</summary>
    public uint MaxFrameLatency { get; init; }
    /// <summary>Present-adapter topology for the primary swapchain — one of <c>D3D12Device.TopologyOwned</c> /
    /// <c>TopologyCross</c> / <c>TopologyNoOutputs</c> / <c>TopologyUnknown</c> (0, before the first resolve).</summary>
    public int PresentTopologyState { get; init; }
}

internal static unsafe class D3D12MemoryDiagnostics
{
    private static GpuVideoMemorySnapshot _videoMemory;
    private static readonly object Gate = new();
    private static readonly Dictionary<nuint, Entry> Live = new();
    private static ulong _liveBytes;
    private static ulong _createdBytes;
    private static ulong _releasedBytes;
    private static int _createCount;
    private static int _releaseCount;
    private static int _resizeCount;
    private static readonly bool LogEnabled = Diag.EnvFlag("FG_D3D_MEM") || Diag.EnvFlag("FG_DIAG");

    public static void Track(ID3D12Resource* resource, string name, ulong bytes)
    {
        SetName(resource, name);
        TrackPtr((nuint)(void*)resource, name, bytes);
    }

    /// <summary>Track a descriptor heap (audit gpu mem-01: descriptor heaps were a [d3d-mem]/DiagResourceTotals blind
    /// spot). Same running tally as resources — keyed on the COM pointer, which is unique across all D3D12 objects.</summary>
    public static void Track(ID3D12DescriptorHeap* heap, string name, ulong bytes)
    {
        SetName(heap, name);
        TrackPtr((nuint)(void*)heap, name, bytes);
    }

    private static void SetName(ID3D12Resource* resource, string name)
    {
        if (resource == null) return;
        fixed (char* p = name) _ = resource->SetName(p);
    }

    private static void SetName(ID3D12DescriptorHeap* heap, string name)
    {
        if (heap == null) return;
        fixed (char* p = name) _ = heap->SetName(p);
    }

    private static void TrackPtr(nuint key, string name, ulong bytes)
    {
        if (key == 0) return;
        lock (Gate)
        {
            if (Live.TryGetValue(key, out var old))
                _liveBytes = SubtractSaturating(_liveBytes, old.Bytes);

            Live[key] = new Entry(name, bytes);
            _liveBytes += bytes;
            _createdBytes += bytes;
            _createCount++;
            Log($"create {name} bytes={Format(bytes)} live={Format(_liveBytes)} creates={_createCount} releases={_releaseCount}");
        }
    }

    public static void Release(ID3D12Resource* resource, string fallbackName) => ReleasePtr((nuint)(void*)resource, fallbackName);

    /// <summary>Release-tracking for a descriptor heap (the Track overload's mirror). Keyed on the COM pointer.</summary>
    public static void Release(ID3D12DescriptorHeap* heap, string fallbackName) => ReleasePtr((nuint)(void*)heap, fallbackName);

    private static void ReleasePtr(nuint key, string fallbackName)
    {
        if (key == 0) return;
        lock (Gate)
        {
            if (Live.Remove(key, out var entry))
            {
                _liveBytes = SubtractSaturating(_liveBytes, entry.Bytes);
                _releasedBytes += entry.Bytes;
                _releaseCount++;
                Log($"release {entry.Name} bytes={Format(entry.Bytes)} live={Format(_liveBytes)} creates={_createCount} releases={_releaseCount}");
                return;
            }

            _releaseCount++;
            Log($"release {fallbackName} bytes=unknown live={Format(_liveBytes)} creates={_createCount} releases={_releaseCount}");
        }
    }

    /// <summary>Tracked live D3D12 resource totals (bytes + count) — O(1) read of the running tally maintained at
    /// Track/Release. For the MemCensus sampler (read via <c>D3D12Device.DiagResourceTotals</c>).</summary>
    internal static (long bytes, int count) LiveTotals()
    {
        lock (Gate) return ((long)_liveBytes, Live.Count);
    }

    /// <summary>Last render-thread video-memory snapshot. UI timers copy this POD; they must not call DXGI.</summary>
    internal static GpuVideoMemorySnapshot LastVideoMemory => _videoMemory;

    /// <summary>Publish a numeric snapshot assembled on the render thread (QueryVideoMemoryInfo already ran there).
    /// LiveTotals is lock-protected and O(1); atlas/glyph counts are already-copied integers.</summary>
    internal static void PublishVideoMemory(
        ulong localUsage, ulong localBudget,
        ulong nonLocalUsage, ulong nonLocalBudget,
        int atlasImages, int atlasPages, int cachedGlyphs)
    {
        var (bytes, count) = LiveTotals();
        // `with`, not `new`: this runs on its own ~1/60-presents cadence, independent of PublishDisplayMode's 1 Hz
        // DWM-branch cadence below — a `new` here would zero the display fields on every video-memory refresh.
        _videoMemory = _videoMemory with
        {
            LocalCurrentUsage = localUsage,
            LocalBudget = localBudget,
            NonLocalCurrentUsage = nonLocalUsage,
            NonLocalBudget = nonLocalBudget,
            TrackedResourceBytes = bytes,
            TrackedResourceCount = count,
            AtlasImages = atlasImages,
            AtlasPages = atlasPages,
            CachedGlyphs = cachedGlyphs,
            Valid = true,
        };
    }

    /// <summary>Publish the per-window display-mode + present-topology fields of <see cref="GpuVideoMemorySnapshot"/>.
    /// Called from the render thread's 1 Hz DWM branch (D3D12Device.SamplePresentTopology) on the monitor-change edge
    /// only — never per present. Merges via `with` so it never clobbers the video-memory fields <see
    /// cref="PublishVideoMemory"/> publishes on its own cadence.</summary>
    internal static void PublishDisplayMode(
        string? monitorDeviceName, int refreshNumerator, int refreshDenominator, long refreshPeriodQpc,
        int primaryRefreshNumerator, int primaryRefreshDenominator,
        uint swapchainBufferCount, uint maxFrameLatency, int presentTopologyState, bool displayModeValid)
    {
        _videoMemory = _videoMemory with
        {
            MonitorDeviceName = monitorDeviceName,
            MonitorRefreshNumerator = refreshNumerator,
            MonitorRefreshDenominator = refreshDenominator,
            MonitorRefreshPeriodQpc = refreshPeriodQpc,
            PrimaryRefreshNumerator = primaryRefreshNumerator,
            PrimaryRefreshDenominator = primaryRefreshDenominator,
            SwapchainBufferCount = swapchainBufferCount,
            MaxFrameLatency = maxFrameLatency,
            PresentTopologyState = presentTopologyState,
            DisplayModeValid = displayModeValid,
        };
    }

    public static void Resize(string target, uint width, uint height)
    {
        lock (Gate)
        {
            _resizeCount++;
            Log($"resize {target} {width}x{height} live={Format(_liveBytes)} resizes={_resizeCount}");
        }
    }

    public static void Snapshot(string label)
    {
        lock (Gate)
        {
            Log($"snapshot {label} live={Format(_liveBytes)} created={Format(_createdBytes)} released={Format(_releasedBytes)} resources={Live.Count} creates={_createCount} releases={_releaseCount} resizes={_resizeCount}");
        }
    }

    /// <summary>Live-resource breakdown by name prefix (the part before the first space or '['), largest first. Written
    /// to stderr unconditionally (it's an explicit operator dump, not the gated [d3d-mem] trace) — the empirical "which
    /// resource class is holding the climbing memory" answer for native-RAM leak hunts on UMA hardware.</summary>
    public static void DumpLive(string label)
    {
        lock (Gate)
        {
            var rows = AggregateLiveLocked();
            Console.Error.WriteLine($"[d3d-mem] === live {label}: total={Format(_liveBytes)} resources={Live.Count} created={Format(_createdBytes)} released={Format(_releasedBytes)} creates={_createCount} releases={_releaseCount} resizes={_resizeCount} ===");
            foreach (var r in rows)
                Console.Error.WriteLine($"[d3d-mem]   {r.Key,-32} {Format((ulong)r.Value[0]),12}  x{r.Value[1]}");
        }
    }

    /// <summary>One-line live breakdown by name prefix, largest first — <c>total=87.1MiB n=64 | Glyph.AtlasTexture=16.0MiB×1
    /// | OpacityLayer.Pool=14.7MiB×4 | …</c>, capped at <paramref name="maxRows"/> classes plus an `other=` remainder.
    /// Same aggregation as <see cref="DumpLive"/>, formatted for a log SINK rather than stderr, so an always-on
    /// one-shot attribution line can be emitted at first present (the whole point: <c>gpu bytes</c> is one number and
    /// cannot say WHICH class holds it — and on UMA every class here is pinned host memory).</summary>
    internal static string BreakdownLine(int maxRows)
    {
        lock (Gate)
        {
            var rows = AggregateLiveLocked();
            var sb = new System.Text.StringBuilder(256);
            sb.Append("total=").Append(Mib(_liveBytes)).Append("MiB n=").Append(Live.Count);
            long other = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i < maxRows) sb.Append(" | ").Append(rows[i].Key).Append('=').Append(Mib((ulong)rows[i].Value[0])).Append("MiB×").Append(rows[i].Value[1]);
                else other += rows[i].Value[0];
            }
            if (other > 0) sb.Append(" | other=").Append(Mib((ulong)other)).Append("MiB");
            return sb.ToString();
        }
    }

    /// <summary>Compact <c>Class:MiB/count,Class:MiB/count,…</c> token — same per-class aggregation as <see
    /// cref="BreakdownLine"/> (largest first) but comma-separated, one-decimal MiB, and no total/other rows, so it
    /// drops into a host log line as a single space-free token (the app's always-on <c>mem.sample</c> gpu section:
    /// `gpu bytes=… resources=… top=…`). Capped at <paramref name="maxRows"/> classes; empty when nothing is tracked
    /// yet. Same O(live-resource-count) cost as <see cref="BreakdownLine"/> — fine at the census sampler's ~5s cadence,
    /// not per frame.</summary>
    internal static string TopClassesLine(int maxRows)
    {
        lock (Gate)
        {
            if (Live.Count == 0) return "";
            var rows = AggregateLiveLocked();
            var sb = new System.Text.StringBuilder(128);
            int n = Math.Min(maxRows, rows.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(rows[i].Key).Append(':').Append(MibOneDecimal((ulong)rows[i].Value[0])).Append('/').Append(rows[i].Value[1]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Live resources aggregated by <see cref="NameKey"/> (bytes, count), largest-bytes first. Caller must
    /// hold <see cref="Gate"/>. Shared by <see cref="BreakdownLine"/>, <see cref="TopClassesLine"/> and <see
    /// cref="DumpLive"/> so the three census views never drift apart.</summary>
    private static List<KeyValuePair<string, long[]>> AggregateLiveLocked()
    {
        var agg = new Dictionary<string, long[]>();
        foreach (var e in Live.Values)
        {
            string key = NameKey(e.Name);
            if (!agg.TryGetValue(key, out var v)) { v = new long[2]; agg[key] = v; }
            v[0] += (long)e.Bytes; v[1]++;
        }
        var rows = new List<KeyValuePair<string, long[]>>(agg);
        rows.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
        return rows;
    }

    private static string NameKey(string name)
    {
        int cut = name.Length;
        for (int i = 0; i < name.Length; i++) { char c = name[i]; if (c == ' ' || c == '[') { cut = i; break; } }
        return cut == 0 ? name : name.Substring(0, cut);
    }

    private static ulong SubtractSaturating(ulong value, ulong delta) => value > delta ? value - delta : 0;

    /// <summary>MiB with two decimals and NO unit/space — <see cref="BreakdownLine"/> appends the unit itself so every
    /// token in that log line stays a single space-free key=value.</summary>
    private static string Mib(ulong bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>MiB with ONE decimal and no unit — <see cref="TopClassesLine"/>'s compact form (matches the app's own
    /// <c>Mb()</c> formatter so the whole mem.sample line reads at consistent precision).</summary>
    private static string MibOneDecimal(ulong bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Format(ulong bytes)
    {
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        if (bytes >= mb) return $"{bytes / mb:0.00} MiB";
        if (bytes >= kb) return $"{bytes / kb:0.00} KiB";
        return $"{bytes} B";
    }

    private static void Log(string message)
    {
        if (LogEnabled) Console.WriteLine("[d3d-mem] " + message);
    }

    private readonly record struct Entry(string Name, ulong Bytes);
}
