namespace Wavee.Core;

// Core domain records. A deliberately small, clean projection of the WaveeMusic domain
// (which today is UI DTOs over proto-backed *CacheEntry storage + a polymorphic ITrackItem).
// Framework-neutral: no FluentGpu / WinUI / Spotify-proto types leak in here.

/// <summary>An image reference (album art, avatar, …). <paramref name="BlurHash"/> backs a cheap placeholder.</summary>
public sealed record Image(string Url, int? Width = null, int? Height = null, string? BlurHash = null);

public sealed record ArtistRef(string Id, string Uri, string Name);
public sealed record AlbumRef(string Id, string Uri, string Name);

public sealed record Artist(
    string Id, string Uri, string Name, Image? Image,
    IReadOnlyList<Album>? TopAlbums = null);

public sealed record Album(
    string Id, string Uri, string Name, Image? Cover,
    IReadOnlyList<ArtistRef> Artists, int Year, int TrackCount,
    IReadOnlyList<Track>? Tracks = null);

public sealed record Track(
    string Id, string Uri, string Title,
    IReadOnlyList<ArtistRef> Artists, AlbumRef Album,
    long DurationMs, bool IsExplicit, Image? Image);

public sealed record Playlist(
    string Id, string Uri, string Name, string? Description, string OwnerName,
    Image? Cover, int TrackCount, IReadOnlyList<Track>? Tracks = null);

/// <summary>Album-art-derived palette. Plain ARGB <see cref="uint"/> channels keep Core
/// framework-neutral; the app maps each to its renderer color (ColorF) at the UI boundary.</summary>
public sealed record Palette(uint BackgroundDark, uint TintedDark, uint Light, uint Accent);

public enum QueueBucket { NowPlaying, UserQueue, NextUp }
public sealed record QueueEntry(string EntryId, Track Track, QueueBucket Bucket, bool IsAutoplay);
