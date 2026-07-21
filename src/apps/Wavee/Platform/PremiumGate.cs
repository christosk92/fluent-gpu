namespace Wavee;

// The premium-only gate's WARNING UI. When Wavee refuses to launch on a Spotify Free account, no window/engine is up yet,
// so on Windows we show a parent-less native Win32 message box; on the macOS/Linux port there is no user32, so we fall back
// to stderr (a real UI surface comes with the cross-platform shell).
static class PremiumGate
{
    public static void ShowWarning() =>
        StartupNotice.Warning(Wavee.Backend.SessionGate.WarningTitle, Wavee.Backend.SessionGate.WarningBody);
}
