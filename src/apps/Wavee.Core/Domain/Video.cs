namespace Wavee.Core;

// The video↔audio association cache (the data side of music videos). Spotify's extended-metadata VIDEO_ASSOCIATIONS
// (99) / AUDIO_ASSOCIATIONS (98) extensions map a track to its counterpart media + the counterpart's file ids. We
// cache that client-side (persisted, etag-revalidated) so the UI can show a has-video indicator at list scale and a
// future, DRM-gated video route can fetch the file by id. NOTE: video files are DRM-protected and resolve over their
// OWN route — never the audio key / audio storage-resolve path. This record stops at the cached file id.

/// <summary>One media file variant of an association — a 20-byte content file id (hex) plus its quality discriminant
/// and (for video) resolution. <paramref name="Width"/>/<paramref name="Height"/> are 0 when unknown (e.g. audio).</summary>
public sealed record VideoFileRef(string FileIdHex, int Variant, int Width, int Height);

/// <summary>The cached video↔audio association for one entity uri. <paramref name="HasVideo"/> is the list-level
/// indicator (VIDEO_ASSOCIATIONS returned a non-empty payload); <paramref name="CounterpartUri"/> is the paired
/// entity (the video track for an audio track, or vice-versa); <paramref name="Files"/> are the counterpart's file
/// id variants. <paramref name="Etag"/> drives 304 revalidation; <paramref name="FetchedAt"/> +
/// <paramref name="OfflineTtlSeconds"/> drive freshness/offline reuse. A negative result (no video) is cached too,
/// so we stop re-asking.</summary>
public sealed record VideoAssociation(
    string Uri,
    bool HasVideo,
    string? CounterpartUri,
    System.Collections.Generic.IReadOnlyList<VideoFileRef> Files,
    string? Etag,
    System.DateTimeOffset FetchedAt,
    long OfflineTtlSeconds)
{
    public static readonly System.Collections.Generic.IReadOnlyList<VideoFileRef> NoFiles = System.Array.Empty<VideoFileRef>();

    /// <summary>A cached "this entity has no video" result (404), still worth persisting so we don't re-ask every open.</summary>
    public static VideoAssociation None(string uri, string? etag, System.DateTimeOffset fetchedAt, long offlineTtlSeconds)
        => new(uri, false, null, NoFiles, etag, fetchedAt, offlineTtlSeconds);
}
