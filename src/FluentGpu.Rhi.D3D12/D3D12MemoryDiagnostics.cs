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
        if (resource == null) return;
        nuint key = (nuint)(void*)resource;
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

    public static void Release(ID3D12Resource* resource, string fallbackName)
    {
        if (resource == null) return;
        nuint key = (nuint)(void*)resource;
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
