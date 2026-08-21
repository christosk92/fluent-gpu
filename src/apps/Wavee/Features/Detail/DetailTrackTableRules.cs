namespace Wavee;

/// <summary>What the list is sorted by. <see cref="Index"/> = the original context order. Values are persisted, so new
/// columns append and existing values never move.</summary>
public enum SortColumn { Index, Title, Album, Duration, Artist, DateAdded, Plays }

/// <summary>The track-list sort state persisted per context.</summary>
public readonly record struct DetailTrackSort(SortColumn Column, bool Descending)
{
    public static readonly DetailTrackSort Default = new(SortColumn.Index, false);
}

/// <summary>The identity lanes selected by the app-wide row style. Kept engine-free so the responsive grammar is
/// unit-tested without mounting the detail page.</summary>
internal readonly record struct TrackIdentityColumns(bool Thumb, bool Artist, bool ArtistInTitle);

/// <summary>The trailing lanes selected by a row style. Kept beside the identity decision so the header, rows and
/// tests all agree that Classic folds media facts into Title and exposes commands through one overflow lane.</summary>
internal readonly record struct TrackTrailingColumns(bool Video, bool Actions, bool Expand);

/// <summary>Pure column and sort rules shared by the detail-table renderer and its tests.</summary>
internal static class DetailTrackTableRules
{
    // The Classic Artist lane survives one tier longer than Album and folds below 440 DIP (tier 4).
    internal const int ClassicArtistFoldTier = 4;
    internal const int ClassicInlineVideoDropTier = 4;
    internal const float ClassicHeaderHeight = 32f;

    internal static TrackIdentityColumns IdentityColumns(
        bool classic, bool showArtThumb, bool artworkHidden, bool showTrackArtist, int tier)
    {
        bool artist = classic && showTrackArtist && tier < ClassicArtistFoldTier;
        return new TrackIdentityColumns(
            Thumb: !classic && showArtThumb && !artworkHidden && tier < 5,
            Artist: artist,
            ArtistInTitle: showTrackArtist && !artist);
    }

    /// <summary>Classic keeps density independent, but maps it onto the tighter table ladder shown by the legacy
    /// desktop client. Modern retains the established 40/48/56/64 ladder owned by <c>TrackRow</c>.</summary>
    internal static float RowHeightFor(int density, bool classic) => classic
        ? density switch { 0 => 36f, 2 => 44f, 3 => 48f, _ => 40f }
        : density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => 48f };

    internal static float HeaderHeightFor(bool classic) => classic ? ClassicHeaderHeight : 36f;

    /// <summary>Classic has no dedicated media/disclosure lanes: video is an inline Title fact and versions move into
    /// the single-track overflow menu. The one trailing action lane remains hover-only in the renderer.</summary>
    internal static TrackTrailingColumns TrailingColumns(
        bool classic, bool hasVideo, bool showVersions, int tier)
    {
        bool hasTrailingRoom = tier < 6;
        if (classic) return new TrackTrailingColumns(false, hasTrailingRoom, false);
        bool video = hasVideo && hasTrailingRoom;
        return new TrackTrailingColumns(video, hasTrailingRoom && !video, showVersions && hasTrailingRoom);
    }

    internal static bool ShowClassicInlineVideo(bool classic, bool hasVideo, int tier) =>
        classic && hasVideo && tier < ClassicInlineVideoDropTier;

    internal static bool ShowClassicVersionsMenu(bool classic, bool showVersions, bool singleTrack) =>
        classic && showVersions && singleTrack;

    /// <summary>A Title header owns Artist only while Artist is folded into its metadata subline.</summary>
    internal static bool HeaderActive(SortColumn header, SortColumn active, bool artistColumn) =>
        header == active || (!artistColumn && header == SortColumn.Title && active == SortColumn.Artist);

    /// <summary>Header-click sort cycle. A dedicated Artist lane gets its own ordinary three-state cycle; without that
    /// lane the Title header retains the existing Title/Artist five-state cycle.</summary>
    internal static DetailTrackSort NextSort(DetailTrackSort cur, SortColumn clicked, bool artistColumn)
    {
        if (clicked == SortColumn.Index)
            return cur.Column == SortColumn.Index ? new DetailTrackSort(SortColumn.Index, !cur.Descending) : DetailTrackSort.Default;

        if (clicked == SortColumn.Title && !artistColumn)
        {
            if (cur.Column == SortColumn.Title)
                return cur.Descending ? new DetailTrackSort(SortColumn.Artist, false) : new DetailTrackSort(SortColumn.Title, true);
            if (cur.Column == SortColumn.Artist)
                return cur.Descending ? DetailTrackSort.Default : new DetailTrackSort(SortColumn.Artist, true);
            return new DetailTrackSort(SortColumn.Title, false);
        }

        if (cur.Column == clicked) return cur.Descending ? DetailTrackSort.Default : new DetailTrackSort(clicked, true);
        return new DetailTrackSort(clicked, false);
    }
}
