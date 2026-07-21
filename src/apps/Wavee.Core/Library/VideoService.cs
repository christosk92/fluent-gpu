namespace Wavee.Core;

/// <summary>The music-video data layer: detect whether tracks have a video and cache the video↔audio file-id map (the
/// counterpart entity uri + its file id variants), client-side and persisted, the same way the rest of extended-metadata
/// is cached. Detection rides the shared extended-metadata transport (VIDEO_ASSOCIATIONS, etag-revalidated). This is the
/// DATA side only — it does not resolve or play video (video files are DRM-protected and resolve over their own route).</summary>
public interface IVideoService
{
    /// <summary>Batch: for a set of track uris, fetch + cache the video association (and flip <c>Track.HasVideo</c> for the
    /// ones that have a video, so the list shows the indicator). Best-effort; never throws into the caller.</summary>
    Task DetectAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default);

    /// <summary>Single: the cached association for a track (has-video + counterpart uri + video file ids), revalidating
    /// over the network if missing/stale. Null when the track has no video / could not be resolved.</summary>
    Task<VideoAssociation?> GetAsync(string trackUri, CancellationToken ct = default);
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding the UI tree
/// (mirrors <see cref="SwitchableAlbumEnrichmentService"/>).</summary>
public sealed class SwitchableVideoService : IVideoService
{
    IVideoService _inner;
    public SwitchableVideoService(IVideoService inner) => _inner = inner;
    public void SetInner(IVideoService inner)
        => System.Threading.Volatile.Write(ref _inner, inner ?? throw new ArgumentNullException(nameof(inner)));

    IVideoService Current => System.Threading.Volatile.Read(ref _inner);
    public Task DetectAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default) => Current.DetectAsync(trackUris, ct);
    public Task<VideoAssociation?> GetAsync(string trackUri, CancellationToken ct = default) => Current.GetAsync(trackUri, ct);
}

/// <summary>Offline/fake fallback: no live transport, so detection is a no-op and lookups are empty.</summary>
public sealed class NoVideoService : IVideoService
{
    public Task DetectAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default) => Task.CompletedTask;
    public Task<VideoAssociation?> GetAsync(string trackUri, CancellationToken ct = default) => Task.FromResult<VideoAssociation?>(null);
}
