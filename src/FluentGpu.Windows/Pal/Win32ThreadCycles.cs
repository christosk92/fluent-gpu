using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Pal.Windows;

/// <summary>The Windows half of <see cref="ThreadCycles"/>: <c>QueryThreadCycleTime</c> on the calling thread's
/// pseudo-handle — the CPU cycles the thread has consumed (user + kernel, interrupts excluded). One cached static
/// delegate, so a read allocates nothing. Installed once by <c>FluentApp.RunCore</c>; idempotent.</summary>
internal static partial class Win32ThreadCycles
{
    private static readonly Func<ulong> s_read = Read;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint threadHandle, out ulong cycleTime);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    private static ulong Read() => QueryThreadCycleTime(GetCurrentThread(), out ulong cycles) ? cycles : 0UL;

    /// <summary>Install the counter into the engine seam (probed once: a failing call leaves the seam empty).</summary>
    public static void Install()
    {
        if (ThreadCycles.Source is not null) return;
        if (Read() != 0) ThreadCycles.Source = s_read;
    }
}
