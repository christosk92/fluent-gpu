using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.MediaSources;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The Spotify media source: pure delegation onto the SAME <see cref="LiveTrackResolver"/> + fast-start
/// instances the session already owns, so routing playback through <see cref="MediaProviderRegistry"/> costs exactly one
/// prefix test and changes no resolve semantics. Owns the whole <c>spotify:</c> namespace — tracks AND episodes ride the
/// same seams (the episode branches live inside the resolver, where per-scheme knowledge belongs).</summary>
public sealed class SpotifyMediaProvider : IPlayableMediaProvider
{
    public const string UriPrefix = "spotify:";

    readonly LiveTrackResolver _resolver;
    readonly IFastTrackResolver _fast;
    readonly IFastTrackWarmer? _warmer;

    public SpotifyMediaProvider(LiveTrackResolver resolver, IFastTrackResolver fast)
    {
        _resolver = resolver;
        _fast = fast;
        _warmer = fast as IFastTrackWarmer;
    }

    public string Id => "spotify";

    public MediaProviderCaps Caps =>
        MediaProviderCaps.PreparedNext | MediaProviderCaps.ConnectPublish | MediaProviderCaps.WireMeta;

    public bool Owns(string playableUri) => playableUri.StartsWith(UriPrefix, StringComparison.Ordinal);

    public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
        => _fast.ResolveFastAsync(track, ct);

    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
        => _resolver.ResolveAsync(track, ct);

    public void Warm(Track track, string reason = "") => _warmer?.Warm(track, reason);

    public async Task<PlaybackTrackMeta?> ResolveWireMetaAsync(Track track, CancellationToken ct = default)
    {
        var m = await _resolver.ResolveMetaAsync(track, ct).ConfigureAwait(false);
        int kbps = m.Fmt switch
        {
            AudioFormat.OggVorbis96 => 96,
            AudioFormat.OggVorbis160 => 160,
            AudioFormat.OggVorbis320 => 320,
            AudioFormat.Flac => 1411,
            AudioFormat.Mp3 => 160,
            _ => 160,
        };
        string fmtLabel = m.Fmt == AudioFormat.Mp3 ? "MP3" : $"Vorbis {kbps} kbps";
        return new PlaybackTrackMeta(m.FileGid, m.FileId, kbps, fmtLabel, m.DurMs);
    }
}
