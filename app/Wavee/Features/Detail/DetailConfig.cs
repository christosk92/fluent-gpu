using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// The shared detail surface: ONE component (playlist / album / single / liked) parameterized by a per-context config.
// See docs/superpowers/specs/2026-06-18-shared-detail-page-design.md. This file holds the closed value set — the unified
// view model the rail/rows/trailing read from, and the four DetailConfig literals that flip the per-context knobs.

/// <summary>Which detail surface a route resolves to. (Single is the Album path with ≤2 tracks — resolved post-load.)</summary>
public enum DetailKind { Album, Playlist, Liked }

/// <summary>The left-rail badge row style.</summary>
public enum BadgeStyle { None, TypeYear, OwnerRow }

/// <summary>The heart affordance semantics (no Core mutation command exists yet — optimistic-local until one lands).</summary>
public enum HeartMode { None, Save, Follow }

/// <summary>What the list is sorted by. <see cref="Index"/> = the original (context) order. <see cref="Artist"/> has no
/// column of its own (it's the title subline), so it's offered via the sort menu rather than a clickable header.
/// (Appended in persisted order — the int is stored, so never reorder.)</summary>
public enum SortColumn { Index, Title, Album, Duration, Artist, DateAdded, Plays }

/// <summary>The track-list sort state — persisted per context (each album/playlist remembers its own).</summary>
public readonly record struct TrackSort(SortColumn Column, bool Descending)
{
    public static readonly TrackSort Default = new(SortColumn.Index, false);
}

/// <summary>Combinable quick-filter toggles for the track list (the search query is tracked separately).</summary>
[Flags]
public enum TrackFilterFlags { None = 0, HideExplicit = 1, VideosOnly = 2 }

/// <summary>
/// The unified detail view model — one shape the rail, the track rows, and the trailing sections all read. The loader
/// maps each <see cref="IMusicLibrary"/> domain record (Album / Playlist / liked-songs) onto this, so the view code is
/// context-agnostic and the per-context differences live entirely in <see cref="DetailConfig"/>.
/// </summary>
public sealed record DetailModel(
    string Title,
    Image? Cover,
    string? ContextUri,                  // album/playlist Uri (or the liked collection Uri) — what PlayAsync plays
    string? BadgeType,                   // "ALBUM" / "EP" / "SINGLE" (album/single)
    string? Year,                        // album/single release year
    string? OwnerName,                   // playlist owner
    Image? OwnerImage,                   // playlist owner avatar
    IReadOnlyList<ArtistRef> Artists,    // billed artists (album/single) — also the owner-row name source
    string? Description,                 // playlist description / release blurb
    string MetaLine,                     // "50 songs · 2 hr 59 min · 2024"
    IReadOnlyList<Track> Tracks,
    Artist? AboutArtist,                 // album/single trailing: About-the-artist + More-by shelf (TopAlbums)
    Palette? Palette,                    // art-derived palette (future GetPaletteAsync — null for now)
    bool HasDateAdded = false,           // playlist: any track carries an AddedAt → show the Date-added column + sort
    bool HasAddedBy = false,             // playlist: ≥2 distinct contributors → show the Added-by column (collaborative)
    bool HasVideo = false,               // any track has a video → offer the "Videos only" filter + the row indicator
    AlbumKind ReleaseKind = AlbumKind.Album,   // album path: which release type (drives badge + config)
    IReadOnlyList<Artist>? Fans = null,        // album trailing: "Fans also like" artist chips
    IReadOnlyList<PlaylistSummary>? FeaturedOn = null)   // album trailing: "Featured on" playlist shelf
{
    public static readonly DetailModel Empty = new(
        "", null, null, null, null, null, null,
        Array.Empty<ArtistRef>(), null, "", Array.Empty<Track>(), null, null);
}

/// <summary>
/// The closed per-context difference set carried by value (a pure app value — no engine type). The shared
/// <c>DetailPage</c>/<c>DetailShell</c> holds everything structural; this flips the knobs. <see cref="Columns"/> is a
/// SHARED array instance read by both the column header and every row, which is the column-alignment invariant
/// (reference-equal by construction — see <c>DetailTracks</c>).
/// </summary>
public readonly record struct DetailConfig(
    bool TwoColumn,                 // false → single-column (liked): no rail, no backdrop wash
    float RailWidth,
    BadgeStyle Badges,
    bool ShowArtThumb,              // playlist/liked: a small art thumb in the title cell
    bool ShowAlbumColumn,           // playlist/liked: a dedicated Album-name column
    TrackSize[] Columns,            // SHARED by header + rows (the alignment invariant)
    bool CapTitle,                  // playlist/liked: clamp the hero-title width
    ItemsSelectionMode Selection,   // Extended (playlist/album/liked) | None (single)
    bool HasTrailing,               // album/single: About/Fans/More-by (and selects the outer-scroll composition)
    HeartMode Heart,
    bool ShowPlays = false,         // album/single/EP/compilation: a Plays column + per-row video indicator + top-track star
    bool ShowTrackArtist = false)   // show the per-track artist subline (playlist/liked, and compilations — various artists)
{
    // Column track sets. Two shared instances → the header and rows are reference-equal (the alignment invariant).
    // playlist/liked: [ #, TITLE(+thumb+artist), ALBUM, ♥, DUR ]   album/single: [ #, TITLE(+artist), ♥, DUR ]
    // (No "Plays" column: the domain Track carries no play count — a data-driven deviation from the spec's column list.)
    internal static readonly TrackSize[] ListColumns =
        [TrackSize.Px(36), TrackSize.Star(), TrackSize.Px(200), TrackSize.Px(40), TrackSize.Px(64)];
    internal static readonly TrackSize[] AlbumColumns =
        [TrackSize.Px(36), TrackSize.Star(), TrackSize.Px(40), TrackSize.Px(64)];

    public static DetailConfig Playlist => new(
        TwoColumn: true, RailWidth: WaveeSize.RailPlaylist, Badges: BadgeStyle.OwnerRow,
        ShowArtThumb: true, ShowAlbumColumn: true, Columns: ListColumns, CapTitle: true,
        Selection: ItemsSelectionMode.Extended, HasTrailing: false, Heart: HeartMode.Follow, ShowTrackArtist: true);

    public static DetailConfig Album => new(
        TwoColumn: true, RailWidth: WaveeSize.RailAlbum, Badges: BadgeStyle.TypeYear,
        ShowArtThumb: false, ShowAlbumColumn: false, Columns: AlbumColumns, CapTitle: false,
        Selection: ItemsSelectionMode.Extended, HasTrailing: true, Heart: HeartMode.Save, ShowPlays: true);

    // A single == the album surface (trailing sections included) but with no multi-select (1–2 tracks).
    public static DetailConfig Single => Album with { Selection = ItemsSelectionMode.None };

    // A compilation == the album surface but various-artists, so the rows show the per-track artist subline.
    public static DetailConfig Compilation => Album with { ShowTrackArtist = true };

    public static DetailConfig Liked => new(
        TwoColumn: false, RailWidth: 0f, Badges: BadgeStyle.None,
        ShowArtThumb: true, ShowAlbumColumn: true, Columns: ListColumns, CapTitle: true,
        Selection: ItemsSelectionMode.Extended, HasTrailing: false, Heart: HeartMode.None, ShowTrackArtist: true);
}

/// <summary>Shared formatting + small helpers for the detail surface.</summary>
internal static class DetailFormat
{
    /// <summary>Per-track duration "m:ss" (or "h:mm:ss").</summary>
    public static string TrackTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Total-duration phrase "2 hr 59 min" / "47 min".</summary>
    public static string TotalTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        int h = (int)t.TotalHours, m = t.Minutes;
        return h >= 1 ? Strings.Detail.DurationHrMin(h, m) : Strings.Detail.DurationMin(Math.Max(1, m));
    }

    public static long TotalMs(IReadOnlyList<Track> tracks)
    {
        long ms = 0;
        for (int i = 0; i < tracks.Count; i++) ms += tracks[i].DurationMs;
        return ms;
    }

    /// <summary>The Date-added column label: relative for the last week ("Today" / "3 days ago"), else an absolute date.</summary>
    public static string DateAddedLabel(DateTimeOffset? at)
    {
        if (at is not { } d) return "";
        int days = (int)(DateTimeOffset.Now.Date - d.Date).TotalDays;
        return days <= 0 ? Loc.Get(Strings.Detail.Today) : days == 1 ? Loc.Get(Strings.Detail.Yesterday) : days < 7 ? Strings.Detail.DaysAgo(days) : d.ToString("MMM d, yyyy");
    }

    /// <summary>"· "-joined billed-artist names.</summary>
    public static string ArtistNames(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return "";
        if (artists.Count == 1) return artists[0].Name;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(artists[i].Name); }
        return sb.ToString();
    }
}
