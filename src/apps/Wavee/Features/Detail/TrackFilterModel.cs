using System;
using Wavee.Core;

namespace Wavee;

/// <summary>Which textual track field the list query searches.</summary>
public enum TrackSearchScope : byte { Everything = 0, Title = 1, Artist = 2, Album = 3 }

/// <summary>Three-way inclusion rule for a boolean track trait: include everything, hide matches, or show only matches.</summary>
public enum TrackTraitMode : byte { All = 0, Hide = 1, Only = 2 }

/// <summary>Combinable binary filters. Three-way traits, ranges, and single-choice facets live on
/// <see cref="TrackFilterState"/>.</summary>
[Flags]
public enum TrackFilterFlags : byte
{
    None = 0,
    LikedOnly = 1,
    PlayableOnly = 2,
}

public enum TrackDurationRange : byte { Any = 0, UnderThreeMinutes = 1, ThreeToFiveMinutes = 2, OverFiveMinutes = 3 }
public enum TrackAddedRange : byte { Any = 0, LastSevenDays = 1, LastThirtyDays = 2, LastSixMonths = 3, LastYear = 4 }
public enum TrackOriginFilter : byte { Any = 0, Streamed = 1, Local = 2 }

/// <summary>The complete transient track-list filter. The default value means no filtering and global text search.</summary>
public readonly record struct TrackFilterState(
    TrackSearchScope SearchScope = TrackSearchScope.Everything,
    TrackTraitMode ExplicitMode = TrackTraitMode.All,
    TrackTraitMode VideoMode = TrackTraitMode.All,
    TrackFilterFlags Flags = TrackFilterFlags.None,
    TrackDurationRange Duration = TrackDurationRange.Any,
    TrackAddedRange Added = TrackAddedRange.Any,
    TrackOriginFilter Origin = TrackOriginFilter.Any)
{
    public static readonly TrackFilterState Default = new();

    public bool LikedOnly => (Flags & TrackFilterFlags.LikedOnly) != 0;
    public bool PlayableOnly => (Flags & TrackFilterFlags.PlayableOnly) != 0;
    public bool IsDefault => Equals(Default);

    /// <summary>Number shown on the Filter affordance. Each binary toggle and each non-default facet counts once.</summary>
    public int ActiveCount
    {
        get
        {
            int n = SearchScope == TrackSearchScope.Everything ? 0 : 1;
            if (ExplicitMode != TrackTraitMode.All) n++;
            if (VideoMode != TrackTraitMode.All) n++;
            if (LikedOnly) n++;
            if (PlayableOnly) n++;
            if (Duration != TrackDurationRange.Any) n++;
            if (Added != TrackAddedRange.Any) n++;
            if (Origin != TrackOriginFilter.Any) n++;
            return n;
        }
    }
}

/// <summary>Pure filter predicate shared by production and headless tests.</summary>
public static class TrackFilterModel
{
    public static bool Matches(
        Track track,
        string query,
        in TrackFilterState filter,
        bool hasVideo,
        bool isSaved,
        DateTimeOffset now)
    {
        if (!MatchesTrait(track.IsExplicit, filter.ExplicitMode)) return false;
        if (!MatchesTrait(hasVideo, filter.VideoMode)) return false;
        if (filter.LikedOnly && !isSaved) return false;
        if (filter.PlayableOnly && track.Availability != Availability.Playable) return false;

        if (filter.Origin == TrackOriginFilter.Streamed && track.Origin != TrackOrigin.Streamed) return false;
        if (filter.Origin == TrackOriginFilter.Local && track.Origin != TrackOrigin.Local) return false;

        if (!MatchesDuration(track.DurationMs, filter.Duration)) return false;
        if (!MatchesAdded(track.AddedAt, filter.Added, now)) return false;
        return query.Length == 0 || MatchesQuery(track, query, filter.SearchScope);
    }

    public static bool MatchesQuery(Track track, string query, TrackSearchScope scope) => scope switch
    {
        TrackSearchScope.Title => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase),
        TrackSearchScope.Artist => ArtistMatches(track, query),
        TrackSearchScope.Album => track.Album.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
        _ => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
             || ArtistMatches(track, query)
             || track.Album.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
    };

    static bool MatchesTrait(bool hasTrait, TrackTraitMode mode) => mode switch
    {
        TrackTraitMode.Hide => !hasTrait,
        TrackTraitMode.Only => hasTrait,
        _ => true,
    };

    static bool MatchesDuration(long durationMs, TrackDurationRange range) => range switch
    {
        TrackDurationRange.UnderThreeMinutes => durationMs < 180_000L,
        TrackDurationRange.ThreeToFiveMinutes => durationMs is >= 180_000L and <= 300_000L,
        TrackDurationRange.OverFiveMinutes => durationMs > 300_000L,
        _ => true,
    };

    static bool MatchesAdded(DateTimeOffset? addedAt, TrackAddedRange range, DateTimeOffset now)
    {
        if (range == TrackAddedRange.Any) return true;
        if (addedAt is null) return false;
        int days = range switch
        {
            TrackAddedRange.LastSevenDays => 7,
            TrackAddedRange.LastThirtyDays => 30,
            TrackAddedRange.LastSixMonths => 180,
            _ => 365,
        };
        return addedAt.Value >= now - TimeSpan.FromDays(days);
    }

    static bool ArtistMatches(Track track, string query)
    {
        for (int i = 0; i < track.Artists.Count; i++)
            if (track.Artists[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
