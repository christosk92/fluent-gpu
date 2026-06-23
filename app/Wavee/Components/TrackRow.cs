using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Which optional columns a track row shows. #, Title and Duration are always present. Cell build order (and the matching
// track widths) is: # · ♥ · (thumb) · Title · Album · AddedBy · DateAdded · Video · Plays · Duration. SHARED by the
// detail TrackList header + every row builder, so the header and the rows stay column-aligned by construction.
internal readonly record struct ColumnSet(bool Album, bool By, bool Date, bool Video, bool Plays, bool Heart, bool Thumb);

// ── the ONE track-row cell, used EVERYWHERE a track is shown (detail list, library pane, artist "Popular", search) ──
// This is the single source of truth for what a track row LOOKS like and how it BEHAVES at rest/hover/now-playing — the
// number↔play/pause transport reveal, the live equalizer, the buffer spinner, the per-row heart, the art thumb, the
// artist/album hyperlinks, the duration/plays cells. Callers vary only the COLUMN SET (what's shown) and the CONTAINER
// (the detail/library bound-selection skin vs. an eager hover row), so every surface renders an identical cell — they
// can never drift, because they all build from here. Pure/diffable (no Animate) → a bound re-render patches in place.
internal static class TrackRow
{
    // Grid-layout constants — SHARED so a row's columns line up under the detail header (the alignment invariant).
    internal const float RowHeight = 48f;            // density M
    internal const float HeaderHeight = 36f;
    internal const float ColGap = WaveeSpace.M;       // shared by header + rows
    internal const float PadX = WaveeSpace.L;         // shared horizontal inset (header chrome padding == row grid padding)
    internal const float RowInset = WaveeSpace.S;     // rounded row-highlight inset (rows pad PadX−RowInset so columns stay header-aligned)
    internal const float ThumbSize = 36f;

    // Track row height by density (0 Compact · 1 Default · 2 Cozy · 3 Comfortable).
    internal static float RowHeightFor(int density) => density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => RowHeight };

    // Stream count → "11.8M" / "654.8K".
    internal static string PlaysLabel(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000f:0.#}M" : n >= 1_000 ? $"{n / 1_000f:0.#}K" : n.ToString("N0");

    // The per-row playback state the cell reflects (now-playing equalizer / buffer spinner / top-track star / saved heart).
    internal readonly record struct State(bool IsNow, bool IsPlaying, bool IsBuffering, bool IsTop, bool Saved);

    // Builds the row GRID — ONE source for the live bound rows, the eager rows, AND the skeleton shimmer. The per-track
    // values arrive resolved (t + state flags + the title element), so the caller decides static (shimmer/eager) vs
    // index-signal-bound (detail BoundRowContent) title. Plain/diffable — no Animate — so a re-render patches cells in place.
    internal static Element Grid(Track t, int displayIndex, in State st, ColumnSet set, TrackSize[] tracks, float rowH,
                                 Element title, bool showTrackArtist, Action<string, string?> go,
                                 Action? onPlay = null, Action? onLike = null)
    {
        float thumb = ThumbSize;   // fixed art size → a stable dedicated art column

        var cells = new List<Element>(tracks.Length);

        // # cell: number / live equalizer / fetch spinner at rest; reveals a SINGLE-CLICK play (or pause) button on ROW hover.
        cells.Add(NumberCell(displayIndex, st.IsNow, st.IsPlaying, st.IsBuffering, st.IsTop, onPlay));

        // ♥ — in the left cluster (between # and the art thumb). Filled when saved; click toggles via the caller's bridge.
        if (set.Heart) cells.Add(CenterCell(Heart(st.Saved, onLike)));

        // Art thumb (playlist/liked) gets its OWN column before Title — so the "Title" header aligns over the title TEXT,
        // not the artwork (the WaveeMusic RowArtColDef pattern). Then the title + artist subline (subline hidden on
        // single-artist albums/singles/EPs).
        if (set.Thumb)
            cells.Add(CenterCell(Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, thumb, thumb, WaveeRadius.Control)));
        var titleCol = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
            Children = showTrackArtist
                ? [title, ArtistLinks(t.Artists, go)]
                : [title],
        };
        cells.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = [titleCol] });

        if (set.Album)
            cells.Add(LeftCell(AlbumLink(t.Album, go)));
        if (set.By)
            cells.Add(AddedByCell(t.AddedBy));
        if (set.Date)
            cells.Add(LeftCell(new TextEl(DetailFormat.DateAddedLabel(t.AddedAt)) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }));
        if (set.Video)
            cells.Add(CenterCell(t.HasVideo ? Icon(Icons.Movie, 13f, Tok.TextTertiary) : new BoxEl()));
        if (set.Plays)
            cells.Add(EndCell(new TextEl(PlaysLabel(t.PlayCount)) { Size = 13f, Color = Tok.TextTertiary }));
        cells.Add(EndCell(new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 13f, Color = Tok.TextSecondary }));

        return new GridEl
        {
            Columns = tracks, ColGap = ColGap, RowHeight = rowH, Grow = 1f,   // fill the row skin's content lane
            // Pad PadX − RowInset: with the skin's RowInset margin, columns still start at PadX (header-aligned).
            Padding = new Edges4(PadX - RowInset, 0f, PadX - RowInset, 0f),
            Children = cells.ToArray(),
        };
    }

    // A self-contained, EAGER (non-virtualized) interactive row for small preview lists — artist "Popular", search
    // "Songs". It wraps the SAME cell in a hover container that is the interactive ancestor, so the number↔play/pause
    // transport reveal + the now-playing equalizer + the per-row heart behave EXACTLY like the big virtualized lists; only
    // virtualization + multi-select are dropped (these are short previews). Single-click plays (no multi-select here). The
    // title is a plain now-playing-coloured ellipsis (the marquee is reserved for the full lists' now-playing row).
    internal static Element Row(Track t, int displayIndex, in State st, ColumnSet set, TrackSize[] tracks, float rowH,
                                bool showTrackArtist, Action<string, string?> go, Action onPlay, Action? onLike = null, bool zebra = false)
    {
        bool light = Tok.Theme == ThemeKind.Light;
        Element title = new TextEl(t.Title)
        {
            Size = 14f, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            MinHeight = rowH, ClipToBounds = true, Margin = new Edges4(RowInset, 0f, RowInset, 0f),
            Corners = CornerRadius4.All(6f),
            Fill = zebra && displayIndex % 2 != 0 ? (light ? ColorF.FromRgba(0xF7, 0xF6, 0xF3) : Tok.FillSubtleTertiary) : ColorF.Transparent,
            HoverFill = light ? ColorF.FromRgba(0xEC, 0xE9, 0xE2) : Tok.FillSubtleSecondary,
            PressedFill = light ? ColorF.FromRgba(0xE5, 0xE2, 0xDA) : Tok.FillSubtleTertiary,
            PressScale = 0.985f, BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, OnClick = onPlay,
            // No-op pointer-exit → registers PointerBit so this row is the "interactive ancestor" whose hover progress the
            // # cell inherits (SceneRecorder.TryResolveInteractionProgress) — that's what reveals play/pause on row hover.
            OnPointerExit = static () => { },
            Children = [Grid(t, displayIndex, st, set, tracks, rowH, title, showTrackArtist, go, onPlay, onLike)],
        };
    }

    // The artist subline as inline HYPERLINK spans — one clickable link per artist (each navigates on its own), joined by
    // ", ". The engine resolves the Hand cursor over each link rect and fires its OnClick on release; the press lands on
    // this text leaf (no PressedBit) so clicking an artist navigates WITHOUT playing/selecting the row.
    internal static Element ArtistLinks(IReadOnlyList<ArtistRef> artists, Action<string, string?> go)
    {
        if (artists.Count == 0) return new BoxEl();
        var spans = new TextSpan[artists.Count * 2 - 1];
        int n = 0;
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = artists[i];   // fresh per-iteration capture → each link navigates to its OWN artist
            spans[n++] = new TextSpan(a.Name, OnClick: () => go("artist:" + a.Uri, a.Name));
        }
        return new SpanTextEl(spans)
        {
            Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            MinWidth = 0f,   // the NoWrap names must not inflate the flexible title column
        };
    }

    // The album cell as a single clickable hyperlink (navigates to the album page).
    internal static Element AlbumLink(AlbumRef album, Action<string, string?> go) =>
        new SpanTextEl([new TextSpan(album.Name, OnClick: () => go("album:" + album.Uri, album.Name))])
        {
            Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Grow = 1f, Basis = 0f,
        };

    // The Added-by cell: a small initial-avatar (we carry no avatar URL in the model yet) + the contributor name.
    internal static Element AddedByCell(string? by)
    {
        if (string.IsNullOrEmpty(by)) return new BoxEl();
        string initial = by.Substring(0, 1).ToUpperInvariant();
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = WaveeSpace.S,
            Children =
            [
                new BoxEl
                {
                    Width = 22f, Height = 22f, Shrink = 0f, Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(initial) { Size = 11f, Weight = 600, Color = Tok.TextSecondary }],
                },
                new TextEl(by) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    // The per-row like heart: filled (accent) when the track is in the saved-set, outline otherwise; click toggles it
    // through the caller's LibraryBridge (optimistic). Null onLike (skeleton / overscan rows) → a static, non-interactive heart.
    internal static Element Heart(bool saved, Action? onLike) => new BoxEl
    {
        Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, OnClick = onLike,
        Children = [Icon(saved ? Mdl.HeartFill : Icons.Heart, 14f, saved ? Tok.AccentTextPrimary : Tok.TextTertiary)],
    };

    // The # cell — a small state machine over the playback of THIS track, with the transport button revealed on row hover:
    //   • fetching/buffering → a spinner (shown whether or not you're hovering);
    //   • now-playing + playing → a LIVE animated equalizer at rest, the PAUSE button on hover;
    //   • now-playing + paused  → a settled equalizer at rest, the PLAY button on hover;
    //   • album top track       → the star at rest, the PLAY button on hover;
    //   • otherwise             → the track number at rest, the PLAY button on hover.
    // The number/equalizer layer fades OUT on row hover and the transport layer fades IN — the recorder drives both off
    // the nearest interactive ancestor (the row), so the reveal follows ROW hover, and survives the pointer crossing onto
    // the button. The transport layer is itself the SINGLE-CLICK target (its OnClick + hand cursor); the inner glyph
    // PressScale-pushes on press for a real button feel.
    internal static Element NumberCell(int index, bool isNow, bool isPlaying, bool isBuffering, bool isTop, Action? onPlay = null)
    {
        ColorF accent = Tok.AccentTextPrimary;
        Element rest =
            isBuffering ? Spinner()
            : isNow     ? WaveeEqualizer.Of(isPlaying, accent)
            : isTop     ? Icon(Mdl.FavoriteStarFill, 11f, accent)
            :             new TextEl((index + 1).ToString()) { Size = 13f, Color = Tok.TextTertiary };
        Element transport = isBuffering
            ? Spinner()
            : new BoxEl
            {
                Width = 24f, Height = 24f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                PressScale = 0.86f,   // a real button press-push (the row-driven reveal is the hover cue)
                Children = [Icon(isNow && isPlaying ? Icons.Pause : Icons.Play, 12f, isNow ? accent : Tok.TextPrimary)],
            };
        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                new BoxEl { Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f, Children = [rest] },
                new BoxEl
                {
                    Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Opacity = 0f, HoverOpacity = 1f,
                    OnClick = onPlay, Cursor = onPlay is null ? (CursorId?)null : CursorId.Hand,
                    Children = [transport],
                },
            ],
        };
    }

    // The indeterminate fetch/buffer spinner (WinUI ProgressRing). The now-playing equalizer is the shared WaveeEqualizer.
    internal static Element Spinner() => ProgressRing.Indeterminate(size: 16f, foreground: Tok.AccentTextPrimary);

    // ── cell wrappers (the cell fills its grid rect; these vertical-center + horizontally place the content) ──
    internal static Element CenterCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [content] };
    internal static Element LeftCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Children = [content] };
    internal static Element EndCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Children = [content] };
}
