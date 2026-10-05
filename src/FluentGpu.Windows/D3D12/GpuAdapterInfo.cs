using System;
using System.Collections.Generic;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>One enumerable GPU adapter, TerraFX-free for app consumption (Settings &gt; About picker).
/// <see cref="Luid"/> packs the DXGI LUID as <c>(HighPart &lt;&lt; 32) | LowPart</c> — the same packing
/// <see cref="GpuAdapterInfo"/> uses, valid for this boot only (LUIDs are not stable across reboots, so a persisted
/// preference should store <see cref="Name"/> and re-resolve to a LUID at startup).</summary>
public readonly record struct GpuAdapterDesc(string Name, long Luid, ulong DedicatedVideoMemoryBytes, bool IsSoftware, bool IsCurrent);

/// <summary>Process-global identity of the adapter the D3D12 device was created on — published in InitDevice
/// (re-published on recovery), read by sibling device creators that must land on the SAME GPU (the D3D11
/// video-decode device). GpuProfile-shaped, but lives in FluentGpu.Windows because LUID is Windows interop and
/// the Engine stays TerraFX-free. Torn-read-free: both halves pack into ONE long via Volatile.
/// Also carries the user's adapter PREFERENCE (<see cref="PreferredAdapterLuid"/>) and the enumeration the picker
/// lists (<see cref="EnumerateAdapters"/>).</summary>
public static class GpuAdapterInfo
{
    private static long s_adapterLuid;     // (HighPart << 32) | LowPart; 0 = no device yet
    private static long s_preferredLuid;   // 0 = auto (the HIGH_PERFORMANCE walk); else the user-selected adapter
    private static volatile bool s_switchRequested;   // set by RequestAdapterSwitch, test-and-cleared by the UI recover gate

    /// <summary>User-selected adapter for device creation, packed <c>(HighPart &lt;&lt; 32) | LowPart</c>; 0 = auto
    /// (the IDXGIFactory6 HIGH_PERFORMANCE walk). Set BEFORE the first device init from persisted settings, or at
    /// runtime followed by <c>IGpuDevice.InjectDeviceLost()</c> — the controlled reset whose recovery re-runs
    /// InitDevice, which honors this value first. A stale/failed preference (adapter unplugged, driver refused the
    /// device) falls through to the auto walk — never a hard failure.</summary>
    public static long PreferredAdapterLuid
    {
        get => Volatile.Read(ref s_preferredLuid);
        set => Volatile.Write(ref s_preferredLuid, value);
    }

    /// <summary>Live adapter switch from the app (Settings &gt; About picker). Sets the preference and raises a
    /// one-shot flag the AppHost UI recover gate consumes — which drives the SAME rendezvous a device loss takes
    /// (InjectDeviceLost → the render loop's recover gate re-runs InitDevice, honoring the new LUID), avoiding a
    /// UI-thread RemoveDevice race with the render thread's submit/present. Pass 0 to switch back to automatic.</summary>
    public static void RequestAdapterSwitch(long luid)
    {
        PreferredAdapterLuid = luid;
        s_switchRequested = true;
    }

    /// <summary>Test-and-clear the pending live-switch request. Called only from the AppHost UI recover gate
    /// (via the IGpuDevice hook); producer (RequestAdapterSwitch, the picker handler) and consumer are both the
    /// UI thread, so a volatile read-then-clear is race-free.</summary>
    internal static bool ConsumeSwitchRequest()
    {
        if (!s_switchRequested) return false;
        s_switchRequested = false;
        return true;
    }

    internal static void Publish(LUID luid)
        => Volatile.Write(ref s_adapterLuid, ((long)luid.HighPart << 32) | luid.LowPart);

    /// <summary>The render adapter's LUID packed <c>(HighPart &lt;&lt; 32) | LowPart</c>; 0 until a D3D12 device exists. The
    /// packed form lets a host that has no D3D types (the protected-video runtime's adapter provider) pin its own device to the
    /// renderer's adapter.</summary>
    public static long CurrentAdapterLuid => Volatile.Read(ref s_adapterLuid);

    /// <summary>The render adapter's LUID; false until a D3D12 device exists.</summary>
    public static bool TryGetAdapterLuid(out LUID luid)
    {
        long v = Volatile.Read(ref s_adapterLuid);
        luid = default;
        if (v == 0) return false;
        luid.LowPart = unchecked((uint)v);
        luid.HighPart = (int)(v >> 32);
        return true;
    }

    /// <summary>Enumerate the machine's DXGI adapters (hardware AND the software rasterizer, flagged) for a picker
    /// UI. Cold path — one factory create + one walk per call; never call per frame. <see cref="GpuAdapterDesc.IsCurrent"/>
    /// marks the adapter the live device sits on (none marked before first device init).</summary>
    public static unsafe IReadOnlyList<GpuAdapterDesc> EnumerateAdapters()
    {
        var list = new List<GpuAdapterDesc>(4);
        IDXGIFactory4* factory = null;
        if ((int)CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) < 0 || factory == null) return list;
        long current = Volatile.Read(ref s_adapterLuid);
        for (uint i = 0; ; i++)
        {
            IDXGIAdapter1* a = null;
            if ((int)factory->EnumAdapters1(i, &a) < 0 || a == null) break;   // DXGI_ERROR_NOT_FOUND — done
            DXGI_ADAPTER_DESC1 d = default;
            if ((int)a->GetDesc1(&d) >= 0)
            {
                long luid = ((long)d.AdapterLuid.HighPart << 32) | d.AdapterLuid.LowPart;
                ReadOnlySpan<char> s = d.Description;   // [InlineArray(128)] char → implicit span
                int n = s.IndexOf('\0');
                list.Add(new GpuAdapterDesc(
                    new string(n >= 0 ? s[..n] : s),
                    luid,
                    (ulong)d.DedicatedVideoMemory,
                    (d.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0,
                    luid == current && current != 0));
            }
            a->Release();
        }
        factory->Release();
        return list;
    }
}
