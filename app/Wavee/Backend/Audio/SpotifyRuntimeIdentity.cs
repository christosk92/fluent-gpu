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
        Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_APP_PLATFORM") is { Length: > 0 } platform
            ? platform.Trim()
            : SpotifyRuntimeIdentityHost.RuntimeAppPlatform
              ?? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Win32_ARM64" : "Win32_x86_64");

    public static SpotifyRuntimeIdentity Default { get; } = new(
        DefaultAppVersion, DefaultClientVersion, DefaultPlayPlayRequestVersion);

    public static SpotifyRuntimeIdentity FromSpotifyVersion(string spotifyVersion, int requestVersion) => new(
        AppVersionFromSpotifyVersion(spotifyVersion),
        spotifyVersion.Contains(".g", StringComparison.Ordinal) ? spotifyVersion : spotifyVersion + ".gunknown",
        requestVersion);

    /// <summary>spclient / middleware User-Agent.</summary>
    public string UserAgent =>
        Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_USER_AGENT") is { Length: > 0 } userAgent
            ? userAgent.Trim()
            : $"Spotify/{AppVersion} {AppPlatform}/{OsDescriptor()}";

    /// <summary>clienttoken.spotify.com uses a shorter OS stub: <c>…/0 (PC laptop)</c>.</summary>
    public string ClientTokenUserAgent =>
        Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_CLIENT_TOKEN_USER_AGENT") is { Length: > 0 } userAgent
            ? userAgent.Trim()
            : $"Spotify/{AppVersion} {AppPlatform}/0 (PC laptop)";

    public string DesktopSemver => ClientVersion.Split('.')[0] + "." + ClientVersion.Split('.')[1] + "." +
                                   ClientVersion.Split('.')[2] + "." + ClientVersion.Split('.')[3].Split('g')[0];

    static string AppVersionFromSpotifyVersion(string spotifyVersion)
    {
        var semver = spotifyVersion.Split('g')[0].TrimEnd('.');
        var parts = semver.Split('.');
        if (parts.Length < 4
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)
            || !int.TryParse(parts[3], out var build))
            return DefaultAppVersion;

        return $"{major}{minor}{patch:00}{build:00000}";
    }

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
    static volatile int _runtimeArchitecture = -1;
    public static SpotifyRuntimeIdentity Current => ApplyEnvironmentOverrides(_current);
    internal static string? RuntimeAppPlatform => (Architecture)_runtimeArchitecture switch
    {
        Architecture.Arm64 => "Win32_ARM64",
        Architecture.X64 => "Win32_x86_64",
        _ => null,
    };

    public static void Apply(SpotifyRuntimeIdentity identity) => _current = identity;
    public static void ApplyRuntimeArchitecture(Architecture architecture) => _runtimeArchitecture = (int)architecture;

    public static void ApplyFromManifest(PlayPlayRuntimeManifest manifest)
    {
        var client = manifest.ClientVersion ?? manifest.SpotifyVersion;
        if (!client.Contains(".g", StringComparison.Ordinal))
            client += ".gunknown";
        Apply(new SpotifyRuntimeIdentity(manifest.AppVersion, client, manifest.PlayPlayRequestVersion));
        if (PlayPlayRuntimeManifest.TryParseArch(manifest.Arch, out var arch))
            ApplyRuntimeArchitecture(arch);
    }

    static SpotifyRuntimeIdentity ApplyEnvironmentOverrides(SpotifyRuntimeIdentity identity)
    {
        var appVersion = Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_APP_VERSION");
        var clientVersion = Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_CLIENT_VERSION");
        var requestVersion = Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_REQUEST_VERSION");

        if (string.IsNullOrWhiteSpace(appVersion)
            && string.IsNullOrWhiteSpace(clientVersion)
            && string.IsNullOrWhiteSpace(requestVersion))
            return identity;

        return identity with
        {
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? identity.AppVersion : appVersion.Trim(),
            ClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? identity.ClientVersion : clientVersion.Trim(),
            PlayPlayRequestVersion = int.TryParse(requestVersion, out var v) ? v : identity.PlayPlayRequestVersion,
        };
    }
}
