namespace Wavee.Backend.Audio;

using System;
using System.Runtime.InteropServices;

/// <summary>Desktop client identity for spclient / PlayPlay / client-token. Hardcoded until manifest-driven pins land.</summary>
public sealed record SpotifyRuntimeIdentity(string AppVersion, string ClientVersion, int PlayPlayRequestVersion)
{
    public const string DefaultAppVersion = "129300667";
    public const string DefaultClientVersion = "1.2.93.667.g7b5cc0ce";
    public const string DefaultPlayPlayTokenHex = "025614bf92a6c95e922e466523da4f96";
    public const int DefaultPlayPlayRequestVersion = 5;

    public static readonly byte[] DefaultPlayPlayToken = Convert.FromHexString(DefaultPlayPlayTokenHex);

    public static string AppPlatform =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Win32_ARM64" : "Win32_x86_64";

    public static SpotifyRuntimeIdentity Default { get; } = new(
        DefaultAppVersion, DefaultClientVersion, DefaultPlayPlayRequestVersion);

    /// <summary>spclient / middleware User-Agent.</summary>
    public string UserAgent => $"Spotify/{AppVersion} {AppPlatform}/{OsDescriptor()}";

    /// <summary>clienttoken.spotify.com uses a shorter OS stub: <c>…/0 (PC laptop)</c>.</summary>
    public string ClientTokenUserAgent => $"Spotify/{AppVersion} {AppPlatform}/0 (PC laptop)";

    public string DesktopSemver => ClientVersion.Split('.')[0] + "." + ClientVersion.Split('.')[1] + "." +
                                   ClientVersion.Split('.')[2] + "." + ClientVersion.Split('.')[3].Split('g')[0];

    static string OsDescriptor()
    {
        if (!OperatingSystem.IsWindows()) return Environment.OSVersion.VersionString;
        var v = Environment.OSVersion.Version;
        string archSuffix = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "ARM",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.OSArchitecture.ToString(),
        };
        return $"Windows {v.Major} ({v}; {archSuffix})";
    }
}

/// <summary>Thread-safe holder for the active runtime identity.</summary>
public static class SpotifyRuntimeIdentityHost
{
    static volatile SpotifyRuntimeIdentity _current = SpotifyRuntimeIdentity.Default;
    public static SpotifyRuntimeIdentity Current => _current;
    public static void Apply(SpotifyRuntimeIdentity identity) => _current = identity;
}
