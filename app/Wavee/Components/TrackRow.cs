using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
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
    internal const float CompactListItemExtent = ItemsView.ListItemExtent;

    // Track row height by density (0 Compact · 1 Default · 2 Cozy · 3 Comfortable).
    internal static float RowHeightFor(int density) => density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => RowHeight };

    // Stream count → "11.8M" / "654.8K".
    internal static string PlaysLabel(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000f:0.#}M" : n >= 1_000 ? $"{n / 1_000f:0.#}K" : n.ToString("N0");

    // The per-row playback state the cell reflects (now-playing equalizer / buffer spinner / top-track star / saved heart).
    internal readonly record struct State(bool IsNow, bool IsPlaying, bool IsBuffering, bool IsTop, bool Saved);

    internal enum ArtCardKind { Grid, Rail }

    internal static State StateOf(PlaybackBridge? bridge, LibraryBridge? lib, Track t,
                                  bool isTop = false, bool extraBuffering = false)
    {
        bool isNow = bridge?.CurrentTrack.Value?.Id == t.Id;
        bool isPlaying = isNow && (bridge?.IsPlaying.Value ?? false);
        bool isBuffering = extraBuffering || (isNow && bridge is not null && bridge.IsBuffering.Value);
        bool saved = t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false);
        return new State(isNow, isPlaying, isBuffering, isTop, saved);
    }

    internal static void TogglePlayPause(PlaybackBridge bridge)
    {
        bool playing = bridge.IsPlaying.Peek();
        bridge.IsPlaying.Value = !playing;
        if (playing) _ = bridge.Player.PauseAsync(); else _ = bridge.Player.ResumeAsync();
    }

    internal static void Invoke(PlaybackBridge? bridge, Track track, Action startDifferent)
    {
        if (bridge is not null && bridge.CurrentTrack.Peek()?.Id == track.Id)
        {
            TogglePlayPause(bridge);
            return;
        }
        startDifferent();
    }

    // Builds the row GRID — ONE source for the live bound rows, the eager rows, AND the skeleton shimmer. The per-track
    // values arrive resolved (t + state flags + the title element), so the caller decides static (shimmer/eager) vs
    // index-signal-bound (detail BoundRowContent) title. Plain/diffable — no Animate — so a re-render patches cells in place.
    internal static Element Grid(Track t, int displayIndex, in State st, ColumnSet set, TrackSize[] tracks, float rowH,
                                 Element title, bool showTrackArtist, Action<string, string?> go,
                                 Action? onPlay = null, Action? onLike = null, Owner? addedByProfile = null)
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
            cells.Add(AddedByCell(t.AddedBy, addedByProfile));
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
        bool oddZebra = zebra && displayIndex % 2 != 0;
        Element title = new TextEl(t.Title)
        {
            Size = 14f, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            MinHeight = rowH, ClipToBounds = true, Margin = new Edges4(RowInset, 0f, RowInset, 0f),
            Corners = CornerRadius4.All(6f),
            // Row fills are neutral translucent overlays in BOTH themes now (white-alpha zebra, ink-alpha hover/press
            // in light; white-alpha in dark) — the preset tint comes from the surfaces beneath, so no theme branch.
            Fill = oddZebra ? WaveeColors.RowZebra : ColorF.Transparent,
            HoverFill = oddZebra ? WaveeColors.RowHoverZebra : WaveeColors.RowHover,
            PressedFill = oddZebra ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed,
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
    // Art-forward track-list cell content for compact bound lists (artist Popular, now-playing queue). Selection,
    // tap/double-tap and keyboard behavior belong to ItemsView + SelectorVisualsBound; this builds only the shared cell.
    internal static Element ArtCard(Track t, in State st, ColumnSet set, Action<string, string?>? go,
                                    Action onPlay, Action? onLike = null, float art = 48f,
                                    bool showArtists = true, bool explicitBadge = false,
                                    bool showDuration = true, ArtCardKind kind = ArtCardKind.Rail,
                                    Action? onAdd = null)
    {
        float radius = kind == ArtCardKind.Grid ? 4f : 5f;
        float fab = Math.Clamp(art * 0.62f, 28f, 36f);
        var meta = new List<Element>(5);

        if (explicitBadge && t.IsExplicit) meta.Add(ExplicitBadge());
        if (set.Video && t.HasVideo)
        {
            if (meta.Count > 0) meta.Add(new TextEl("Â·") { Size = 12f, Color = Tok.TextTertiary });
            meta.Add(Icon(Icons.Movie, 13f, Tok.TextTertiary));
        }
        if (showArtists)
        {
            if (meta.Count > 0) meta.Add(new TextEl("Â·") { Size = 12f, Color = Tok.TextTertiary });
            meta.Add(go is null
                ? new TextEl(DetailFormat.ArtistNames(t.Artists)) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : ArtistLinks(t.Artists, go));
        }

        var textKids = new List<Element>(3)
        {
            new TextEl(t.Title)
            {
                Size = 13f,
                Weight = 600,
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            },
        };
        if (meta.Count > 0)
            textKids.Add(new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = meta.ToArray() });
        if (set.Plays)
            textKids.Add(new TextEl($"{t.PlayCount:N0} plays") { Size = 10f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        var trailing = new List<Element>(3);
        if (onAdd is not null) trailing.Add(AddButton(onAdd));   // recommendation rows: the "+" add-to-playlist button leads the trailing cluster
        if (set.Heart) trailing.Add(Heart(st.Saved, onLike));
        if (showDuration)
            trailing.Add(new BoxEl
            {
                Padding = new Edges4(6f, 0f, 6f, 0f),
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = [new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 12f, Color = Tok.TextSecondary }],
            });

        return new BoxEl
        {
            Direction = 0,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = kind == ArtCardKind.Grid ? 64f : 52f,
            Gap = kind == ArtCardKind.Grid ? 10f : 10f,
            Padding = kind == ArtCardKind.Grid ? new Edges4(4f, 4f, 4f, 4f) : new Edges4(4f, 2f, 4f, 2f),
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = art,
                    Height = art,
                    Shrink = 0f,
                    ZStack = true,
                    ClipToBounds = true,
                    Corners = CornerRadius4.All(radius),
                    Children =
                    [
                        Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, art, art, radius, decodePx: (int)MathF.Max(64f, art * 2f)),
                        st.IsBuffering
                            ? new BoxEl { Width = art, Height = art, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = ColorF.FromRgba(0, 0, 0, 110), Children = [Spinner()] }
                            : Embed.Comp(() => new NowPlayingOverlay(t.Uri, onPlay, fab, cover: true, art, centered: true)).Skeletonized(false),
                    ],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center, Children = textKids.ToArray() },
                .. trailing,
            ],
        };
    }

    internal static BoxEl ArtCardSelectSkin(in RowScope s, Element content, ArtCardKind kind, Func<bool>? showCheckbox = null)
    {
        Func<bool> isSel = s.IsSelected, isEn = s.IsEnabled;
        var interact = s.OnInteraction;
        Action<bool> focusChanged = s.OnFocusChanged;
        Element[] kids = showCheckbox is null
            ? [content]
            :
            [
                SelectorVisualsBound.BoundCheckLane(showCheckbox, isSel, interact, leftMargin: 4f),
                content,
            ];
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = kind == ArtCardKind.Grid ? 66f : 54f,
            Margin = kind == ArtCardKind.Grid ? new Edges4(0f, 1f, 0f, 1f) : new Edges4(4f, 2f, 4f, 2f),
            Corners = CornerRadius4.All(kind == ArtCardKind.Grid ? 6f : 5f),
            ClipToBounds = true,
            Fill = Prop.Of(() => isSel() ? Tok.FillSubtleSecondary : ColorF.Transparent),
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            BorderWidth = 1f,
            BorderColor = ColorF.Transparent,
            HoverBorderColor = Tok.StrokeCardDefault,
            PressScale = 0.99f,
            Opacity = Prop.Of(() => isEn() ? 1f : ItemContainer.DisabledOpacity),
            Focusable = false,
            FocusVisualMargin = Edges4.All(1f),
            Role = AutomationRole.Button,
            OnPointerPressed = args => interact(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = focusChanged,
            OnPointerExit = static () => { },
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
                    Animate = showCheckbox is null ? null : new LayoutTransition(
                        TransitionChannels.Position,
                        TransitionDynamics.Tween(333f, Easing.FluentDecelerate)),
                    Children = kids,
                },
            ],
        };
    }

    internal static Element ExplicitBadge() => new BoxEl
    {
        MinWidth = 13f, Height = 13f, Padding = new Edges4(2f, 0f, 2f, 0f),
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Tok.TextTertiary,
        Opacity = 0.6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl("E") { Size = 8f, Weight = 600, Color = Tok.TextTertiary }],
    };

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

    // The Added-by cell: resolved profile when available, otherwise the raw playlist membership id.
    internal static Element AddedByCell(string? by, Owner? profile = null)
    {
        if (string.IsNullOrEmpty(by)) return new BoxEl();
        string label = profile?.Name is { Length: > 0 } name ? name : by;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = WaveeSpace.S,
            Children =
            [
                PersonPicture.Create("", 22f, displayName: label, imageSourcePath: profile?.Avatar?.Url),
                new TextEl(label) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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

    // The recommendation-row "add to this playlist" button (Spotify's playlist-extender "+"): a bordered round button that
    // leads the trailing cluster, before the duration. Mirrors Heart — a null onAdd yields a non-interactive button.
    internal static Element AddButton(Action? onAdd) => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        HoverScale = 1.06f, PressScale = 0.94f,
        Cursor = onAdd is null ? (CursorId?)null : CursorId.Hand, OnClick = onAdd,
        Children = [Icon(Icons.Add, 15f, Tok.TextPrimary)],
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
