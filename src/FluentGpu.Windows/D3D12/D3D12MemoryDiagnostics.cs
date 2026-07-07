using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;

namespace FluentGpu.Rhi.D3D12;

internal static unsafe class D3D12MemoryDiagnostics
{
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
            var agg = new Dictionary<string, long[]>();   // key -> [bytes, count]
            foreach (var e in Live.Values)
            {
                string key = NameKey(e.Name);
                if (!agg.TryGetValue(key, out var v)) { v = new long[2]; agg[key] = v; }
                v[0] += (long)e.Bytes; v[1]++;
            }
            var rows = new List<KeyValuePair<string, long[]>>(agg);
            rows.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
            Console.Error.WriteLine($"[d3d-mem] === live {label}: total={Format(_liveBytes)} resources={Live.Count} created={Format(_createdBytes)} released={Format(_releasedBytes)} creates={_createCount} releases={_releaseCount} resizes={_resizeCount} ===");
            foreach (var r in rows)
                Console.Error.WriteLine($"[d3d-mem]   {r.Key,-32} {Format((ulong)r.Value[0]),12}  x{r.Value[1]}");
        }
    }

    private static string NameKey(string name)
    {
        int cut = name.Length;
        for (int i = 0; i < name.Length; i++) { char c = name[i]; if (c == ' ' || c == '[') { cut = i; break; } }
        return cut == 0 ? name : name.Substring(0, cut);
    }

    private static ulong SubtractSaturating(ulong value, ulong delta) => value > delta ? value - delta : 0;

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
