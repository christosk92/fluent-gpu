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

/// <summary>Tempo bands, in the vocabulary a listener actually uses ("slow", "fast") rather than raw BPM entry. The
/// boundaries are the conventional ones: 90 separates ballad from mid, 120 is the four-on-the-floor line, 140 is where
/// drum-and-bass / hard dance begins. A track with no tempo (no kind-222 payload yet) matches only <see cref="Any"/>,
/// so an un-enriched list is never silently emptied by this filter.</summary>
public enum TrackTempoBand : byte { Any = 0, Under90 = 1, From90To119 = 2, From120To139 = 3, From140AndUp = 4 }

/// <summary>The complete transient track-list filter. The default value means no filtering and global text search.</summary>
public readonly record struct TrackFilterState(
    TrackSearchScope SearchScope = TrackSearchScope.Everything,
    TrackTraitMode ExplicitMode = TrackTraitMode.All,
    TrackTraitMode VideoMode = TrackTraitMode.All,
    TrackFilterFlags Flags = TrackFilterFlags.None,
    TrackDurationRange Duration = TrackDurationRange.Any,
    TrackAddedRange Added = TrackAddedRange.Any,
    TrackOriginFilter Origin = TrackOriginFilter.Any,
    TrackTempoBand Tempo = TrackTempoBand.Any,
    // Camelot code ("8B", "11A"). Null = any key. Matched case-insensitively against Track.CamelotCode, which is the
    // stable DJ notation; the pretty name ("C", "G#") is display only and differs by spelling convention.
    string? CamelotCode = null,
    // Liked Songs content-filter chip: a descriptor tag (kind 6 display name, e.g. "K-Pop"). Exclusive by design —
    // one chip at a time — because the chips are a lens on the list, not a set of accumulating constraints.
    string? Tag = null)
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
            if (Tempo != TrackTempoBand.Any) n++;
            if (!string.IsNullOrEmpty(CamelotCode)) n++;
            if (!string.IsNullOrEmpty(Tag)) n++;
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
        // Only a CONFIRMED unavailable is filtered out. Availability is nullable — null means no response ever stated a
        // verdict — and treating unknown as unplayable would empty the list on every surface that never carries
        // playability at all (cluster, library and extended-metadata writes).
        // The shared IsNotYetOut() predicate coincides with "cannot play" here, and that coincidence is intentional: it
        // adds only the AvailableAt clause, so a region-blocked row (Unavailable, no timestamp) is still hidden, while a
        // row whose release moment has passed under a stale server verdict is KEPT — which is the same release-drop heal
        // the greyed row and the play gate get, reached without a refetch.
        if (filter.PlayableOnly && track.IsNotYetOut()) return false;

        if (filter.Origin == TrackOriginFilter.Streamed && track.Origin != TrackOrigin.Streamed) return false;
        if (filter.Origin == TrackOriginFilter.Local && track.Origin != TrackOrigin.Local) return false;

        if (filter.Tag is { Length: > 0 } tag && !HasTag(track.Tags, tag)) return false;
        if (!MatchesTempo(track.TempoBpm, filter.Tempo)) return false;
        if (filter.CamelotCode is { Length: > 0 } key
            && !string.Equals(track.CamelotCode, key, StringComparison.OrdinalIgnoreCase)) return false;

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

    /// <summary>Tag match. Case-insensitive on the DISPLAY name, which is what the chip bar shows and what the store
    /// holds; the lowercase wire token never reaches the UI, so there is one string to compare, not two.</summary>
    static bool HasTag(IReadOnlyList<string>? tags, string tag)
    {
        if (tags is null) return false;
        for (int i = 0; i < tags.Count; i++)
            if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool MatchesTempo(double? bpm, TrackTempoBand band)
    {
        if (band == TrackTempoBand.Any) return true;
        if (bpm is not { } t || t <= 0d) return false;   // unknown tempo cannot satisfy an explicit band
        return band switch
        {
            TrackTempoBand.Under90 => t < 90d,
            TrackTempoBand.From90To119 => t >= 90d && t < 120d,
            TrackTempoBand.From120To139 => t >= 120d && t < 140d,
            TrackTempoBand.From140AndUp => t >= 140d,
            _ => true,
        };
    }

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
