using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee.SpotifyLive;

/// <summary>
/// A resolved video source for the pop-out / inline video surface: EITHER a clear/Canvas URL (played on the clear MF
/// backend) OR a PlayReady DRM descriptor + license relay (played via the native in-process CDM). The Spotify
/// video-resolution layer (Canvas from the feed; PlayReady from <see cref="SpotifyVideoManifest"/> once the probe
/// confirms it) produces this and publishes it on <c>PlaybackBridge.PopOutVideoSource</c>. <see cref="Key"/> is a stable
/// identity (manifest id / URL) so the player remounts cleanly when the source changes.
/// </summary>
public sealed record PopOutVideoSource
{
    /// <summary>Clear/Canvas URL (a plain .mp4 / unencrypted stream). Null for a DRM source.</summary>
    public string? ClearUrl { get; init; }

    /// <summary>Parsed PlayReady descriptor (init/segment addressing + PSSH). Null for a clear source.</summary>
    public DashSourceDescriptor? DrmDescriptor { get; init; }
    /// <summary>The <c>WithDrm</c> license relay (POSTs the CDM challenge to Spotify). Required with a DRM descriptor.</summary>
    public Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay { get; init; }
    /// <summary>Advisory license-server URI carried on the <see cref="DrmConfig"/> (the relay owns the actual POST).</summary>
    public string? LicenseServerUri { get; init; }

    /// <summary>Stable identity for player remount (manifest id or clear URL).</summary>
    public string Key { get; init; } = "";

    public bool IsDrm => DrmDescriptor is not null && LicenseRelay is not null;

    public static PopOutVideoSource Clear(string url) => new() { ClearUrl = url, Key = url };
    public static PopOutVideoSource PlayReady(string manifestId, DashSourceDescriptor descriptor,
        Func<LicenseRequest, ValueTask<LicenseResponse>> relay, string? licenseServerUri)
        => new() { DrmDescriptor = descriptor, LicenseRelay = relay, LicenseServerUri = licenseServerUri, Key = manifestId };
}
