using System.Runtime.InteropServices;
using FluentGpu.Hosting;
using FluentGpu.Rhi.D3D12;

namespace FluentGpu.Pal.Windows;

/// <summary>The Windows half of the <see cref="FrameLedger"/>: the process's cumulative cycles (<c>QueryProcessCycleTime</c>, every
/// thread — so "other threads" is process minus UI minus render), and the memory sample's platform fields — working set and
/// private commit (<c>GetProcessMemoryInfo</c>, never <c>Process.Refresh</c>), CPU time (<c>GetProcessTimes</c>), the render thread's
/// last DXGI video-memory snapshot and the glyph atlas. Static cached delegates and the process pseudo-handle: a read allocates
/// nothing. Installed by <c>FluentApp.RunCore</c> beside <see cref="Win32ThreadCycles"/>; idempotent.</summary>
internal static unsafe partial class Win32LedgerSampler
{
    private static readonly Func<ulong> s_cycles = ReadProcessCycles;
    private static readonly LedgerPlatformSampler s_sample = Sample;
    private static D3D12Device? s_device;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryProcessCycleTime(nint process, out ulong cycles);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx* counters, uint cb);

    private const nint CurrentProcess = -1;   // GetCurrentProcess()'s pseudo-handle

    private static ulong ReadProcessCycles() => QueryProcessCycleTime(CurrentProcess, out ulong c) ? c : 0UL;

    private static void Sample(ref LedgerPlatformSample s)
    {
        ProcessMemoryCountersEx m = default;
        m.cb = (uint)sizeof(ProcessMemoryCountersEx);
        if (GetProcessMemoryInfo(CurrentProcess, &m, m.cb))
        {
            s.WorkingSetBytes = (long)m.WorkingSetSize;
            s.PrivateBytes = (long)m.PrivateUsage;
        }
        if (GetProcessTimes(CurrentProcess, out _, out _, out long kernel, out long user)) s.ProcessCpuTicksTotal = kernel + user;
        GpuVideoMemorySnapshot vm = D3D12Device.LastVideoMemory;
        if (vm.Valid)
        {
            s.VramLocalBytes = (long)vm.LocalCurrentUsage;
            s.VramNonLocalBytes = (long)vm.NonLocalCurrentUsage;
            s.VramLocalBudgetBytes = (long)vm.LocalBudget;
            s.TrackedGpuBytes = vm.TrackedResourceBytes;
        }
        s.GlyphAtlasBytes = Volatile.Read(ref s_device)?.DiagGlyphAtlasBytes ?? 0;
    }

    /// <summary>Install the process counter + the memory sampler into the ledger seams (the device, when there is one, adds the
    /// glyph atlas). A failing cycle query leaves that seam empty.</summary>
    public static void Install(D3D12Device? device)
    {
        Volatile.Write(ref s_device, device);
        if (FrameLedger.ProcessCycles is null && ReadProcessCycles() != 0) FrameLedger.ProcessCycles = s_cycles;
        FrameLedger.PlatformSampler ??= s_sample;
    }

    /// <summary>The window is gone: forget its device (the seams stay; they read nothing device-bound then).</summary>
    public static void Uninstall() => Volatile.Write(ref s_device, null);
}
