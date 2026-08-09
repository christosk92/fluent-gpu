namespace Wavee.Core;

/// <summary>Standalone-artist-page header stats (monthly listeners, followers, world rank, top-track play counts,
/// related, pinned, header image, palette). Deliberately separate from <see cref="IMusicLibrary.GetArtistAsync"/> — the
/// Library master-detail surface must never trigger this (it is 100% extended-metadata). Only the standalone
/// <c>ArtistPage</c> reads it, once, lazily; the discography itself now comes over the V4 pipeline.</summary>
public interface IArtistStatsService
{
    /// <summary>Ensure stats are fresh (SWR on <see cref="Artist.FetchedAt"/>); returns the enriched store artist, or
    /// null offline / on failure.</summary>
    Task<Artist?> EnsureStatsAsync(string artistUri, CancellationToken ct = default);
}

/// <summary>The one freshness predicate shared by the stats service and warm UI seeds.</summary>
public static class ArtistStatsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    public static bool IsFresh(Artist? artist, DateTimeOffset now)
    {
        bool hasReleaseFacets = artist?.LatestRelease is not null || artist?.PopularReleases is { Count: > 0 };
        return artist is not null && artist.TopTracks is { Count: > 0 } && hasReleaseFacets
            && now - artist.FetchedAt <= Ttl;
    }

    public static bool IsFresh(Artist? artist) => IsFresh(artist, DateTimeOffset.UtcNow);
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding the UI tree.</summary>
public sealed class SwitchableArtistStatsService : IArtistStatsService
{
    IArtistStatsService _inner;
    public SwitchableArtistStatsService(IArtistStatsService inner) => _inner = inner;
    public void SetInner(IArtistStatsService inner)
        => System.Threading.Volatile.Write(ref _inner, inner ?? throw new ArgumentNullException(nameof(inner)));

    IArtistStatsService Current => System.Threading.Volatile.Read(ref _inner);
    public Task<Artist?> EnsureStatsAsync(string artistUri, CancellationToken ct = default)
        => Current.EnsureStatsAsync(artistUri, ct);
}

/// <summary>Offline/fake fallback: no stats provider → the page renders the V4 artist unchanged.</summary>
public sealed class NullArtistStatsService : IArtistStatsService
{
    public Task<Artist?> EnsureStatsAsync(string artistUri, CancellationToken ct = default) => Task.FromResult<Artist?>(null);
}
