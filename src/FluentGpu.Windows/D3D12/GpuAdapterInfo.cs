using TerraFX.Interop.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Process-global identity of the adapter the D3D12 device was created on — published in InitDevice
/// (re-published on recovery), read by sibling device creators that must land on the SAME GPU (the D3D11
/// video-decode device). GpuProfile-shaped, but lives in FluentGpu.Windows because LUID is Windows interop and
/// the Engine stays TerraFX-free. Torn-read-free: both halves pack into ONE long via Volatile.</summary>
public static class GpuAdapterInfo
{
    private static long s_adapterLuid;   // (HighPart << 32) | LowPart; 0 = no device yet

    internal static void Publish(LUID luid)
        => System.Threading.Volatile.Write(ref s_adapterLuid, ((long)luid.HighPart << 32) | luid.LowPart);

    public static bool TryGetAdapterLuid(out LUID luid)
    {
        long v = System.Threading.Volatile.Read(ref s_adapterLuid);
        luid = default;
        if (v == 0) return false;
        luid.LowPart = unchecked((uint)v);
        luid.HighPart = (int)(v >> 32);
        return true;
    }
}
