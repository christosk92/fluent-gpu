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
    IReadOnlyList<Album>? TopAlbums = null,
    // Artist-detail facets (docs/architecture.md §2 "Album & artist"): monthly listeners, follower count, a short bio,
    // and the verified flag. All defaulted/additive — synthesized by the fake source, mapped where a real source has them.
    long MonthlyListeners = 0, long Followers = 0, string? Bio = null, bool Verified = false);

/// <summary>The release type — drives the detail-page badge, the layout (a single is a one-track release), and whether
/// the track rows show a per-track artist (compilations are various-artists).</summary>
public enum AlbumKind { Single, EP, Album, Compilation }

public sealed record Album(
    string Id, string Uri, string Name, Image? Cover,
    IReadOnlyList<ArtistRef> Artists, int Year, int TrackCount,
    IReadOnlyList<Track>? Tracks = null, AlbumKind Kind = AlbumKind.Album);

public sealed record Track(
    string Id, string Uri, string Title,
    IReadOnlyList<ArtistRef> Artists, AlbumRef Album,
    long DurationMs, bool IsExplicit, Image? Image,
    // Per-playlist membership metadata (null outside a user playlist): when a track was added, and by whom. The detail
    // page surfaces these as optional columns — curated/editorial playlists carry neither.
    DateTimeOffset? AddedAt = null, string? AddedBy = null,
    bool HasVideo = false,    // the track has an accompanying music video (offered as a list filter + a row indicator)
    long PlayCount = 0,       // stream count (album pages show a Plays column; the top-played track gets a star)
    // Source-agnostic seam (see docs/architecture.md §5): which provider this track came from, how it plays, and
    // whether it is playable in this context. Default = a streamed, playable, source-unspecified track.
    TrackOrigin Origin = TrackOrigin.Streamed,
    Availability Availability = Availability.Playable,
    string? Source = null);

/// <summary>How a track plays — streamed from a remote source (CDN) or decoded from a local file. The seam routes
/// playback by this; default is Streamed (the synthetic catalog's shape).</summary>
public enum TrackOrigin { Streamed, Local }

/// <summary>Whether a track can be played in the current context (account tier / market / delisting). First-class so
/// the UI dims+disables rather than failing at play time. Extensible to a reason later.</summary>
public enum Availability { Playable, Unavailable }

/// <summary>A content owner/curator (e.g. a playlist owner) — richer than a bare name; carries an avatar image.</summary>
public sealed record Owner(string Id, string Name, Image? Avatar);

/// <summary>What the current user may do to a playlist (mirrors Spotify's currentUserCapabilities). The UI gates its
/// edit affordances on these — an editorial / not-owned playlist renders read-only. <c>default</c> = no rights.</summary>
public readonly record struct PlaylistCapabilities(
    bool CanView, bool CanEditItems, bool CanEditMetadata, bool IsCollaborative, bool IsOwner);

public sealed record Playlist(
    string Id, string Uri, string Name, string? Description, string OwnerName,
    Image? Cover, int TrackCount, IReadOnlyList<Track>? Tracks = null,
    // Source-agnostic seam (see docs/architecture.md §5): the real owner, the user's capabilities (drives the
    // read-only vs editable UI), the recommender format (daily-mix/editorial/…), and which provider this came from.
    Owner? Owner = null, PlaylistCapabilities Capabilities = default, string? Format = null, string? Source = null);

/// <summary>Album-art-derived palette. Plain ARGB <see cref="uint"/> channels keep Core
/// framework-neutral; the app maps each to its renderer color (ColorF) at the UI boundary.</summary>
public sealed record Palette(uint BackgroundDark, uint TintedDark, uint Light, uint Accent);

public enum QueueBucket { NowPlaying, UserQueue, NextUp }
public sealed record QueueEntry(string EntryId, Track Track, QueueBucket Bucket, bool IsAutoplay);

// ── Podcasts (docs/architecture.md §2 "Podcasts / shows / episodes") ──────────────────────────────────────────────────
/// <summary>A podcast episode. <paramref name="ProgressMs"/> is the resume position (0 = unplayed); a real source also
/// carries paywall/preview, transcripts and chapters — modelled as additive fields when they arrive.</summary>
public sealed record Episode(
    string Id, string Uri, string Title, string ShowName, Image? Image,
    long DurationMs, DateTimeOffset PublishedAt, string? Description = null, long ProgressMs = 0);

/// <summary>A podcast show. Episodes hydrate on the detail read (like an album's tracks).</summary>
public sealed record Show(
    string Id, string Uri, string Name, string Publisher, Image? Cover, string? Description = null,
    IReadOnlyList<Episode>? Episodes = null);
