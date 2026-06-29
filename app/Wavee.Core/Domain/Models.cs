namespace Wavee.Core;

// Core domain records. A deliberately small, clean projection of the WaveeMusic domain
// (which today is UI DTOs over proto-backed *CacheEntry storage + a polymorphic ITrackItem).
// Framework-neutral: no FluentGpu / WinUI / Spotify-proto types leak in here.

/// <summary>An image reference (album art, avatar, …). <paramref name="BlurHash"/> backs a cheap placeholder.</summary>
// Url is the single cover. When Url is empty and MosaicTiles carries ≥4 album-cover URLs, renderers compose a 2×2 mosaic
// (a cover-less playlist, the way Spotify does). Carrying the tiles on Image lets every Surfaces.Artwork call site mosaic
// with no per-card plumbing.
public sealed record Image
{
    string _url = "";
    System.Collections.Generic.IReadOnlyList<string>? _mosaicTiles;

    public Image(string Url, int? Width = null, int? Height = null, string? BlurHash = null,
        System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null)
    {
        this.Url = Url;
        this.Width = Width;
        this.Height = Height;
        this.BlurHash = BlurHash;
        this.MosaicTiles = MosaicTiles;
    }

    public string Url
    {
        get => _url;
        init => _url = ImageSource.Normalize(value) ?? "";
    }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? BlurHash { get; init; }

    public System.Collections.Generic.IReadOnlyList<string>? MosaicTiles
    {
        get => _mosaicTiles;
        init => _mosaicTiles = ImageSource.NormalizeAll(value);
    }

    public void Deconstruct(out string Url, out int? Width, out int? Height, out string? BlurHash,
        out System.Collections.Generic.IReadOnlyList<string>? MosaicTiles)
    {
        Url = this.Url;
        Width = this.Width;
        Height = this.Height;
        BlurHash = this.BlurHash;
        MosaicTiles = this.MosaicTiles;
    }
}

public sealed record ArtistRef(string Id, string Uri, string Name);
public sealed record AlbumRef(string Id, string Uri, string Name);

public sealed record Artist(
    string Id, string Uri, string Name, Image? Image,
    IReadOnlyList<Album>? TopAlbums = null,
    // Artist-detail facets (docs/architecture.md §2 "Album & artist"): monthly listeners, follower count, a short bio,
    // and the verified flag. All defaulted/additive — synthesized by the fake source, mapped where a real source has them.
    long MonthlyListeners = 0, long Followers = 0, string? Bio = null, bool Verified = false,
    // The "magazine" facets (the WinUI Spotify-style artist page). All additive/nullable: null/0/empty ⇒ the section
    // hides (the real Spotify export maps what it has; the fake source synthesizes the rest). HeaderImage is a landscape
    // hero backdrop (vs the square avatar Image); TopTracks is the real "Popular" list; Pinned is the hero promo card.
    int WorldRank = 0, Image? HeaderImage = null,
    IReadOnlyList<Track>? TopTracks = null, IReadOnlyList<Album>? AppearsOn = null,
    PinnedItem? Pinned = null, ArtistExtras? Extras = null,
    // Cover-extracted page accent (ARGB; null = none). Drives the artist page wash + Play button + section bars.
    Palette? Palette = null);

/// <summary>The optional "magazine" facet bundle for an artist (concerts, merch, playlists, videos, top cities, links,
/// gallery, related artists, a derived tour banner). Any empty list ⇒ that section is omitted from the page.</summary>
public sealed record ArtistExtras(
    IReadOnlyList<Concert>? Concerts = null,
    IReadOnlyList<MerchItem>? Merch = null,
    IReadOnlyList<PlaylistRef>? Playlists = null,
    IReadOnlyList<MusicVideo>? MusicVideos = null,
    IReadOnlyList<TopCity>? TopCities = null,
    IReadOnlyList<ExternalLink>? ExternalLinks = null,
    IReadOnlyList<Image>? Gallery = null,
    IReadOnlyList<RelatedArtist>? Related = null,
    TourBanner? Tour = null);

/// <summary>A music video on the artist page (16:9 thumbnail + duration). <paramref name="TrackUri"/> plays it.</summary>
public sealed record MusicVideo(string TrackUri, string Title, Image? Thumbnail, long DurationMs, bool IsExplicit = false);

/// <summary>An upcoming concert/tour date. <paramref name="Venue"/> is the venue name; <paramref name="City"/> the city.</summary>
public sealed record Concert(string Uri, string? Title, string Venue, string City, DateTimeOffset Date, bool IsFestival = false, bool IsNearUser = false);

/// <summary>A merch product (image + name + display price). <paramref name="ShopUrl"/> opens the external shop.</summary>
public sealed record MerchItem(string Name, string Price, string? Description, Image? Image, string? ShopUrl);

/// <summary>A "Playlists and discovery" entry. <paramref name="Subtitle"/> is the source/owner label (e.g. "Spotify").</summary>
public sealed record PlaylistRef(string Uri, string Name, Image? Cover, string Subtitle);

/// <summary>A "Listened to most in" city. The page draws a proportional bar from <paramref name="Listeners"/>/max.</summary>
public sealed record TopCity(string City, string? Country, long Listeners);

/// <summary>An external/social link. <paramref name="Kind"/> drives the icon glyph.</summary>
public sealed record ExternalLink(string Name, string Url, ExternalLinkKind Kind);
public enum ExternalLinkKind { Generic, Twitter, Instagram, Facebook, YouTube, Wikipedia, TikTok }

/// <summary>A related/"fans also like" artist (carries its own avatar, unlike a bare <see cref="ArtistRef"/>).</summary>
public sealed record RelatedArtist(string Id, string Uri, string Name, Image? Image);

/// <summary>The hero "Pinned" promo card (the artist's spotlighted release + a short comment).</summary>
public sealed record PinnedItem(string Eyebrow, string Title, string Subtitle, string Comment, Image? Cover, string Uri);

/// <summary>The "On tour now" banner copy, derived from the concert list (eyebrow/headline/subline + a live flag).</summary>
public sealed record TourBanner(string Eyebrow, string Headline, string Subline, bool IsLive);

/// <summary>The release type — drives the detail-page badge, the layout (a single is a one-track release), and whether
/// the track rows show a per-track artist (compilations are various-artists).</summary>
public enum AlbumKind { Single, EP, Album, Compilation }

/// <summary>How complete an album read-model is. Summary rows come from search/home, Tracks from extended metadata,
/// and Full from Pathfinder getAlbum. Stores must never replace a higher level with a lower one.</summary>
public enum AlbumHydrationLevel { Summary, Tracks, Full }

public sealed record Album(
    string Id, string Uri, string Name, Image? Cover,
    IReadOnlyList<ArtistRef> Artists, int Year, int TrackCount,
    IReadOnlyList<Track>? Tracks = null, AlbumKind Kind = AlbumKind.Album,
    IReadOnlyList<Album>? MoreByArtist = null,
    // "About this release" facets (getAlbum: label, copyright.items[].text, date.isoString) + the album's primary artists
    // WITH avatars (albumUnion.artists.visuals) for the stacked face-pile header. All additive/nullable.
    string? Label = null, string? Copyright = null, string? ReleaseDate = null,
    IReadOnlyList<Artist>? ArtistsDetailed = null,
    // "Other versions" — alternate editions of THIS album (deluxe/remaster/anniversary), from albumUnion.releases.items.
    IReadOnlyList<Album>? OtherVersions = null,
    // Remaining getAlbum envelope fields used by the release panel and actions.
    string? CourtesyLine = null, string? ReleaseDatePrecision = null, int DiscCount = 1,
    string? ShareUrl = null, bool IsPreRelease = false, DateTimeOffset? PreReleaseEnd = null,
    AlbumHydrationLevel Hydration = AlbumHydrationLevel.Summary,
    // Cover-extracted page accent (ARGB; null = none). Drives the album page wash + Play button + section bars.
    Palette? Palette = null);

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
    Owner? Owner = null, PlaylistCapabilities Capabilities = default, string? Format = null, string? Source = null,
    // Cover-extracted page accent (ARGB; null = none). Drives the playlist page wash + Play button + section bars.
    Palette? Palette = null);

/// <summary>Album-art-derived palette. Plain ARGB <see cref="uint"/> channels keep Core
/// framework-neutral; the app maps each to its renderer color (ColorF) at the UI boundary.</summary>
public sealed record Palette(uint BackgroundDark, uint TintedDark, uint Light, uint Accent);

public enum QueueBucket { NowPlaying, UserQueue, NextUp }
public sealed record QueueEntry(string EntryId, Track Track, QueueBucket Bucket, bool IsAutoplay, string Uid = "");

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
