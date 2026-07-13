namespace Wavee.Core;

/// <summary>An artist displayed in a concert lineup. The image and URI are optional because some providers expose
/// billing text before they resolve the artist to a catalog entity.</summary>
public sealed record ConcertArtist(string Name, string? Uri = null, Image? Image = null);

/// <summary>Provider-neutral coordinates for a concert venue or selected discovery location.</summary>
public sealed record GeoCoordinates(double Latitude, double Longitude);

/// <summary>A reusable concert-discovery location. <paramref name="Id"/> is the provider's stable place identifier;
/// it is deliberately opaque so the domain is not tied to GeoNames.</summary>
public sealed record ConcertPlace(
    string Id,
    string Name,
    string? Region = null,
    string? Country = null,
    string? GeoHash = null,
    GeoCoordinates? Coordinates = null);

/// <summary>Normalized availability for an external concert offer.</summary>
public enum ConcertOfferAvailability { Unknown, Available, Unavailable }

/// <summary>An external ticket offer. Provider-specific strings are retained only where the UI needs to describe the
/// offer; navigation always uses <paramref name="Url"/> and never the concert URI.</summary>
public sealed record ConcertOffer(
    string ProviderName,
    string? Url,
    ConcertOfferAvailability Availability = ConcertOfferAvailability.Unknown,
    string? ProviderImageUrl = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    string? Currency = null,
    string? SaleType = null,
    DateTimeOffset? SaleStartsAt = null,
    DateTimeOffset? SaleEndsAt = null,
    bool HasPromoCodes = false,
    bool IsFirstParty = false);

/// <summary>The rich concert-detail projection. The existing <see cref="Concert"/> remains the compact summary used
/// by artist pages and shelves; detail-only fields live here so existing callers stay source-compatible.</summary>
public sealed record ConcertDetails(
    Concert Summary,
    IReadOnlyList<ConcertArtist>? Artists = null,
    IReadOnlyList<ConcertOffer>? Offers = null,
    IReadOnlyList<Concert>? Related = null,
    IReadOnlyList<ConcertConcept>? Concepts = null,
    string? Region = null,
    string? Country = null,
    string? VenueUri = null,
    string? MetroAreaName = null,
    string? MetroAreaId = null,
    GeoCoordinates? Coordinates = null,
    DateTimeOffset? DoorsOpenAt = null,
    string? AgeRestriction = null,
    string? Status = null);

/// <summary>A weighted concept returned for the active concert-discovery location.</summary>
public sealed record ConcertConcept(string Uri, string Name, double Weight = 0d);

/// <summary>The dedicated artist-concerts response. Its concert branches are siblings of <paramref name="Artist"/>,
/// not children of artistUnion/goods as in the older artist-overview response.</summary>
public sealed record ArtistConcertSchedule(
    ArtistRef Artist,
    Image? HeaderImage,
    IReadOnlyList<Concert> Concerts,
    IReadOnlyList<Concert>? Nearby = null,
    string? NearbyLocationName = null);

public enum ConcertFeedSectionKind { Nearby, Recommended, AllEvents }

/// <summary>A provider-neutral section in the Concert Hub. Playlist promotions remain playlist references and are not
/// coerced into fake concerts.</summary>
public sealed record ConcertFeedSection(
    string Key,
    ConcertFeedSectionKind Kind,
    IReadOnlyList<Concert> Concerts,
    IReadOnlyList<PlaylistRef>? PlaylistPromotions = null,
    string? Description = null);

/// <summary>One sequential page of the Concert Hub. <paramref name="PaginationKey"/> is opaque and must be replayed
/// unchanged; callers must not decode it or infer a total count from it.</summary>
public sealed record ConcertFeedPage(
    IReadOnlyList<ConcertFeedSection> Sections,
    string? PaginationKey = null);
