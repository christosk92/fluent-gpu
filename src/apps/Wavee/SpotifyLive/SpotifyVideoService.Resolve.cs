using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Xm = Wavee.Protocol.ExtendedMetadata;
using M = Wavee.Protocol.Metadata;

namespace Wavee.SpotifyLive;

// Playable-source resolution (the FluentGpu-dependent half of SpotifyVideoService — PopOutVideoSource / DashSourceDescriptor
// / the license relay). Kept in a SEPARATE partial file so the lean, FluentGpu-free Wavee.Tests project (which
// source-includes the base SpotifyVideoService.cs by name) never compiles it.
partial class SpotifyVideoService
{
    /// <summary>Resolve a PLAYABLE video source for <paramref name="trackUri"/> (the pop-out / inline surface consumes it):
    /// the track's own TrackV4 → <c>OriginalVideo[0].Gid</c> (hex) = manifest_id → GET the v9 manifest → if it offers a
    /// PlayReady mp4 profile, a <see cref="PopOutVideoSource.PlayReady"/> (descriptor + license relay); null otherwise
    /// (no video, or Widevine-only — FluentGpu ships PlayReady only, so there is no lane). Self-contained videos resolve
    /// first; a track that carries no video of its own falls back to its VIDEO_ASSOCIATIONS counterpart (the linked video
    /// track's TrackV4). Runtime success additionally depends on the account actually being served PlayReady (confirm with
    /// WAVEE_AUDIO_FORMAT_PROBE=1).</summary>
    public async Task<PopOutVideoSource?> ResolvePlayableAsync(string trackUri, ITransport transport, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || transport is null) return null;

        string? manifestId;
        try
        {
            // Self-contained first: the track's OWN TrackV4 → OriginalVideo[0].Gid.
            manifestId = await ResolveManifestIdFromTrackV4Async(trackUri, ct).ConfigureAwait(false);
            // Fallback: the VIDEO_ASSOCIATIONS counterpart (an audio track linking out to its paired video track).
            if (string.IsNullOrEmpty(manifestId))
                manifestId = await ResolveLinkedManifestIdAsync(trackUri, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("video resolve TrackV4: " + ex.Message); return null; }
        if (string.IsNullOrEmpty(manifestId)) return null;

        var manifest = await SpotifyVideoResolver.ResolveManifestAsync(transport, manifestId, ct).ConfigureAwait(false);
        if (manifest is null || !manifest.HasPlayReadyMp4)
        {
            _log.Info($"video resolve {trackUri}: no PlayReady mp4 (widevine={manifest?.HasWidevine == true})");
            return null;
        }
        var descriptor = manifest.ToDashDescriptor();
        if (descriptor is null) return null;
        var relay = SpotifyLicenseRelay.Create(transport, manifest.LicenseServerEndpoint);
        return PopOutVideoSource.PlayReady(manifestId, descriptor, relay, manifest.LicenseServerEndpoint);
    }

    /// <summary>Fetch <paramref name="uri"/>'s TrackV4 and read <c>OriginalVideo[0].Gid</c> (hex) = manifest_id; null when
    /// the track carries no self-contained video (or the extension is unavailable).</summary>
    private async Task<string?> ResolveManifestIdFromTrackV4Async(string uri, CancellationToken ct)
    {
        var reqs = new (string, Xm.ExtensionKind, string?)[] { (uri, Xm.ExtensionKind.TrackV4, null) };
        var results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);
        if (!results.TryGetValue((uri, Xm.ExtensionKind.TrackV4), out var res) || res.Payload is not { } payload)
            return null;
        var track = M.Track.Parser.ParseFrom(payload);
        return track.OriginalVideo.Count > 0 && track.OriginalVideo[0].Gid.Length > 0
            ? Convert.ToHexStringLower(track.OriginalVideo[0].Gid.Span)
            : null;
    }

    /// <summary>Fallback for a track with no video of its own: fetch its VIDEO_ASSOCIATIONS extension, follow
    /// <c>Association.AssociatedUri</c> (the paired video track) and resolve THAT track's TrackV4 → manifest_id. Mirrors
    /// the proven AudioFormatProbe.ProbeVideoDrmAsync path. null when there is no association or the linked track has no
    /// video.</summary>
    private async Task<string?> ResolveLinkedManifestIdAsync(string trackUri, CancellationToken ct)
    {
        var reqs = new (string, Xm.ExtensionKind, string?)[] { (trackUri, Xm.ExtensionKind.VideoAssociations, null) };
        var results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);
        if (!results.TryGetValue((trackUri, Xm.ExtensionKind.VideoAssociations), out var res) || res.Payload is not { } payload)
            return null;
        var assoc = Xm.VideoAssociations.Parser.ParseFrom(payload);
        var a = assoc.Association;
        if (a is null || !a.HasAssociatedUri || string.IsNullOrEmpty(a.AssociatedUri))
            return null;
        _log.Info($"video resolve {trackUri}: VIDEO_ASSOCIATIONS linked video {a.AssociatedUri}");
        return await ResolveManifestIdFromTrackV4Async(a.AssociatedUri, ct).ConfigureAwait(false);
    }
}
