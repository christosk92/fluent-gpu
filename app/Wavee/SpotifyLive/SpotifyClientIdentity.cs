using System;

namespace Wavee.SpotifyLive;

// ── Desktop-parity / anti-fraud identity (ported VERBATIM — decision #11) ─────────────────────────────────────────────
// The strings Wavee presents to Spotify (DeviceInfo software version, spirc version, private_device_info.platform, the
// app-version/user-agent headers). The server cross-checks them; drift gets the client flagged client-deprecated and
// throttled, and silently breaks Recently Played. Do NOT "tidy" — bump all together against current desktop.
public static class SpotifyClientIdentity
{
    public const string DesktopSemver = "1.2.88.483";
    public const string DesktopBuildSha = "g8aa8628e";
    public const string DeviceSoftwareVersion = DesktopSemver + "." + DesktopBuildSha;   // DeviceInfo.device_software_version
    public const string AppVersionHeader = "128800483";                                  // spotify-app-version header
    public const string AppPlatform = "Win32_x86_64";                                    // app-platform header
    public const string SpircVersion = "3.2.6";                                          // DeviceInfo.spirc_version
    public const string XpuiSnapshotVersion = "xpui-snapshot_2026-05-06_1778061618835_fb3c63a";  // play_origin.feature_version

    /// <summary>private_device_info.platform — the OS descriptor desktop sends.</summary>
    public static string GetPrivateDevicePlatform() => GetOsDescriptor();

    /// <summary>User-Agent for spclient/pathfinder calls: Spotify/{build} {platform}/{os}.</summary>
    public static string GetUserAgent() => $"Spotify/{AppVersionHeader} {AppPlatform}/{GetOsDescriptor()}";

    // Windows: "Windows {major} ({full-version}; {arch})". Spotify always claims x64 (x64[native:ARM] under ARM emulation).
    static string GetOsDescriptor()
    {
        var os = Environment.OSVersion;
        if (!OperatingSystem.IsWindows()) return os.VersionString;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var suffix = arch == System.Runtime.InteropServices.Architecture.Arm64 ? "x64[native:ARM]" : "x64";
        return $"Windows {os.Version.Major} ({os.Version}; {suffix})";
    }
}
