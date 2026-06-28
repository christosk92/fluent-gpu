using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wavee;

// The premium-only gate's WARNING UI. When Wavee refuses to launch on a Spotify Free account, no window/engine is up yet,
// so on Windows we show a parent-less native Win32 message box; on the macOS/Linux port there is no user32, so we fall back
// to stderr (a real UI surface comes with the cross-platform shell).
static class PremiumGate
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    const uint MB_OK = 0x0;
    const uint MB_ICONWARNING = 0x30;

    public static void ShowWarning()
    {
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, Wavee.Backend.SessionGate.WarningBody, Wavee.Backend.SessionGate.WarningTitle, MB_OK | MB_ICONWARNING);
        else
            Console.Error.WriteLine(Wavee.Backend.SessionGate.WarningTitle + ": " + Wavee.Backend.SessionGate.WarningBody);
    }
}
