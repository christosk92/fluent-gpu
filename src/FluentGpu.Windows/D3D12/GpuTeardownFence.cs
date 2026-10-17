using System;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Cold teardown fence boundary. The native adapter records removal; tests drive the same drain without a GPU.</summary>
internal interface IGpuTeardownFence
{
    bool IsRemoved();
    ulong CompletedValue();
    void RecordRemoval(int reason);
    int Signal(ulong value);
    int Arm(ulong value);
    uint Wait(int milliseconds);
}

/// <summary>Release requires a healthy fence retirement or confirmed removal. Unexpected errors
/// propagate before any resource release, so a shutdown cannot free a live device's in-flight resources.</summary>
internal static class GpuTeardownFence
{
    internal const int WaitSliceMs = 100;
    private const int Removed = unchecked((int)0x887A0005);

    internal static bool IsRemovalError(int hr) => unchecked((uint)hr) is 0x887A0005u or 0x887A0006u or 0x887A0007u or 0x887A0020u;

    /// <returns>True for healthy retirement, false for device removal (release without signalling/waiting again).</returns>
    internal static bool Drain<T>(ref T fence, ulong value, bool signal) where T : struct, IGpuTeardownFence
    {
        if (fence.IsRemoved()) return false;
        ulong completed = fence.CompletedValue();
        if (RemovedFence(ref fence, completed)) return false;
        if (signal && !Check(ref fence, fence.Signal(value), "queue.Signal(teardown)")) return false;
        if (!signal && value == 0) return true;
        completed = fence.CompletedValue();
        if (RemovedFence(ref fence, completed) || fence.IsRemoved()) return false;
        if (completed >= value) return true;
        if (!Check(ref fence, fence.Arm(value), "SetEventOnCompletion(teardown)")) return false;
        if (fence.IsRemoved()) return false;
        while (true)
        {
            uint wait = fence.Wait(WaitSliceMs);
            completed = fence.CompletedValue();
            if (RemovedFence(ref fence, completed) || fence.IsRemoved()) return false;
            if (wait == uint.MaxValue) throw new InvalidOperationException("WaitForSingleObject(teardown fence) failed.");
            if (completed >= value) return true;
        }
    }

    private static bool RemovedFence<T>(ref T fence, ulong completed) where T : struct, IGpuTeardownFence
    {
        if (completed != ulong.MaxValue) return false;
        fence.RecordRemoval(Removed);
        return true;
    }

    private static bool Check<T>(ref T fence, int hr, string operation) where T : struct, IGpuTeardownFence
    {
        if (hr >= 0) return true;
        if (IsRemovalError(hr)) { fence.RecordRemoval(hr); return false; }
        // GetDeviceRemovedReason can become authoritative between the initial check and the native call.
        if (fence.IsRemoved()) return false;
        throw new InvalidOperationException($"{operation} failed: 0x{unchecked((uint)hr):X8}");
    }
}
