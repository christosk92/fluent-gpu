using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// Opt the process out of EcoQoS execution-speed throttling (playback D2/F4): the decode-ahead producer and the clock
/// thread must not be slowed on battery, or while the window is in the background, on hybrid-core laptops. Scoped to the
/// AUDIO leaf — <see cref="WasapiPcm.CreateBackend"/> calls <see cref="OptOut"/> — not to every FluentGpu app, so a
/// window-only host keeps the OS default. ONE hint: <c>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION</c> is moot
/// without <c>timeBeginPeriod</c> and is deliberately not requested. A failing call (pre-1709 Windows) is not an error.
/// AOT-clean via <c>[LibraryImport]</c> over <c>kernel32.dll</c>.
/// </summary>
internal static unsafe partial class PowerThrottling
{
    // PROCESS_POWER_THROTTLING_STATE { ULONG Version; ULONG ControlMask; ULONG StateMask; } — 12 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint ProcessPowerThrottling = 4;   // PROCESS_INFORMATION_CLASS::ProcessPowerThrottling

    private static int s_done;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetProcessInformation(nint process, uint informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>Take control of EXECUTION_SPEED throttling and switch it OFF for this process. Idempotent: only the first call
    /// per process reaches the OS. Not for the RT path — call it once while the backend is being built.</summary>
    public static void OptOut()
    {
        if (Interlocked.Exchange(ref s_done, 1) != 0) return;
        // A control bit SET with its state bit CLEAR means "never throttle this aspect"; clearing the control bit would hand it back to the OS.
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };
        if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE)) == 0)
            WasapiAudioDevice.FormatSink?.Invoke($"power-throttling opt-out failed (win32 {Marshal.GetLastWin32Error()})");
    }
}
