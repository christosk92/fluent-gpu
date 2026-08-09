using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The fixed-width left metadata rail (album / single / playlist) in its own vertical scroller. Stack order:
// cover → eyebrow → big hero title → owner/artist row → meta line → CTA cluster → tools/actions → description.
// Every clamped run gets an EXPLICIT Width (= cover edge) so a long title/owner never widens the rail (MediaCard's
// discipline). The cover edge is a constant per config (RailWidth − side padding), so no SizeChanged hack is needed.
static class DetailRail
{
    const float SidePadL = Spacing.L;   // 16
    const float SidePadR = Spacing.S;   // 8
    const float FabSize = 40f;
    // Decode the rail/header cover at the SAME size the Home shelf card uses (MediaCard's ShelfDecodePx, 256) so a Hero
    // fly hands the cover off to the SAME cached texture — pixel-identical, with NO fresh first-visit cover decode (the
    // cold connected-animation spike). Displayed larger (the ~300px rail cover) is a slight, imperceptible upscale.
    const int HeroCoverDecodePx = 256;

    public static float CoverEdge(float railW) => MathF.Max(80f, railW - SidePadL - SidePadR);

    internal static Element HeroArtwork(DetailModel m, float size, float radius = Radii.Card, bool connected = true,
                                        float saturation = 1f, string? morphKey = null,
                                        int decodePx = HeroCoverDecodePx, bool preferLargest = false) =>
        LikedSongsArtwork.IsLikedUri(m.ContextUri) && m.Cover is null
            ? LikedSongsArtwork.Cover(size, radius, morphKey ?? (connected ? m.MorphKey : null))
            : Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, size, size, radius,
                morphKey ?? (connected ? m.MorphKey : null), decodePx: decodePx, saturation: saturation,
                preferLargest: preferLargest);

    // The side rail: the cover STRETCHES to fill the column width (a big hero — the image is NEVER shrunk for height).
    // The height fit comes from the TEXT — titleSize (the shell lowers it on a short rail; auto-fits down to 18px) and
    // the description's line cap (descMaxLines) — and only then the rail's own scrollbar (last resort).
    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, float railW, float titleSize, float titleLineHeight, int descMaxLines, Loadable<DetailModel> modelSource, ActionServices? acts = null)
    {
        float cover = CoverEdge(railW);
        var kids = new List<Element>(10);

        // Cover — click-to-change when metadata is editable; static art otherwise.
        bool editable = m.Capabilities.CanEditMetadata && m.ContextUri is { Length: > 0 };
        kids.Add(new BoxEl
        {
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true,
            // The cover drags the whole entity. On the FRAMING box, not on the editable cover inside it, so the
            // file-drop target that cover owns stays untouched (see WaveeDetailDrag.Hero).
            Draggable = WaveeDetailDrag.Hero(m, acts),
            Children = [editable ? PlaylistInlineEdit.Cover(modelSource, cover) : HeroArtwork(m, cover, saturation: 1.18f)],
        });

        // Identity eyebrow — the type/year fact as ONE tracked-out run, occupying exactly the row the type/year pills
        // held (nothing below it moves). Same treatment as the vertical hero's eyebrow, from the same helper, so the
        // two layouts state the release kind identically. Playlists keep the owner/collaborator block instead.
        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            if (EyebrowText(m, cfg) is { Length: > 0 } eyebrow)
                kids.Add(EyebrowRun(eyebrow) with { Width = cover });
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            kids.Add(PlaylistOwnerBlock(m, cover, modelSource));
        }

        // Hero title — the page's Title/TitleLarge rung, AUTO-FITTING to the cover width in ≤3 LINES down to 18px. The
        // shell picks the rung from the window height (Title 28/36 or TitleLarge 40/52) and hands the paired line height
        // in with it; `float.NaN` here used to mean "whatever the font's natural box is", which is exactly the metric the
        // ramp exists to pin. Weight is the ramp's 600, not the old 900 — PageHero IS Ui.Title, and a 900 override made
        // this the only 900 in the app.
        kids.Add(editable
            ? PlaylistInlineEdit.Title(modelSource, cover, titleSize, lineHeight: titleLineHeight)
            : WaveeType.PageHero(m.Title) with
            {
                Size = titleSize, MinSize = 18f, Weight = 600, Width = cover, LineHeight = titleLineHeight,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            });

        // Billed-artist row (album/single): a STACKED artist face-pile (overlapping avatars + "+N" of the distinct album
        // artists + the billed name) when the album carries artist avatars; else the plain clickable artist names.
        if (cfg.Badges == BadgeStyle.TypeYear && m.Artists.Count > 0)
            kids.Add(Embed.Comp(() => new ArtistFacePile(m, cover, h)));

        // Meta line — albums surface Songs/Length/Released as the bento facts panel below, so an inline line would just
        // duplicate it; only non-album surfaces (playlists / liked) show it here.
        if (cfg.Badges != BadgeStyle.TypeYear && m.MetaLine is { Length: > 0 })
            kids.Add(WaveeType.TrackMeta(m.MetaLine) with { Width = cover, MaxLines = 2, Trim = TextTrim.CharacterEllipsis });

        // CTA cluster: Play pill + a GROUP of shuffle/heart/share FABs. Wrap=true → at a wide rail they're one line; at a
        // narrow rail the FAB group wraps to the next line AS A UNIT (Play above, the three FABs together below) instead
        // of orphaning a single FAB on its own line.
        kids.Add(new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children =
            [
                PlayPill(h.Accent, h.PlayAll),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        // Shuffle now lives in the track-list command bar; the rail keeps just the hero Play + save/share.
                        // A full pre-release has no album to save yet; the entity the collection write accepts is the
                        // prerelease. Swap the heart's TARGET rather than adding a second heart. Falls back to the album
                        // uri whenever the link is absent (offline, unresolved, or already released). Key = the target:
                        // SaveButton's uri freezes at mount.
                        (m.PreReleaseUri ?? m.ContextUri) is { Length: > 0 } saveUri
                            ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize, m.Title)) with { Key = "save:" + saveUri }
                            : Fab(Icons.Heart, () => { }),
                        PlaylistInlineEdit.ShareButton(modelSource),
                        PlaylistInlineEdit.OwnerMenu(modelSource, h),
                    ],
                },
            ],
        });

        if (PreReleaseCard(m, h) is { } countdown) kids.Add(countdown);

        if (cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            kids.Add(AlbumTrailing.ReleasePanel(m, h, outerPadding: false));

        // Description / release blurb — an HTML fragment (links to artists/playlists, bold): parse → rich spans (links
        // accent + clickable via h.Go, bold rendered, entities decoded). Trimmed to descMaxLines (shell lowers it when short).
        if (descMaxLines > 0 && (editable || m.Description is { Length: > 0 }))
            kids.Add(editable
                ? PlaylistInlineEdit.Description(modelSource, cover, descMaxLines, h)
                : RichText.Of(m.Description!, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, cover, descMaxLines,
                    u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); }));

        var rail = new BoxEl
        {
            Direction = 1, Gap = 14f, Width = railW, Shrink = 0f,
            Padding = new Edges4(SidePadL, Spacing.XXL, SidePadR, Spacing.XXL),
            Children = kids.ToArray(),
        };
        // Own vertical scroller (hidden bar by default) — the LAST resort once the TEXT has shrunk and it still overflows
        // (the image stays full-width; the text gave first).
        if (LikedSongsArtwork.IsLikedUri(m.ContextUri))
            return ScrollView(rail) with { Grow = 0f, Shrink = 0f, Width = railW };

        // Match LibraryPage.NavPanel exactly: the contextual left column recedes on FillLayerDefault while the detail
        // rows remain on the base content surface. Liked Songs intentionally keeps its established unlayered treatment.
        return new BoxEl
        {
            Direction = 1, Width = railW, Shrink = 0f,
            ClipToBounds = true, Fill = Tok.FillLayerDefault,
            Children =
            [
                ScrollView(rail) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Width = railW },
            ],
        };
    }

    // Compact identity strip (WP-κ): cover + truncated title + expand control. Collapse no longer vanishes the rail —
    // playlists especially need the title when art is weak/generic. Same HeroArtwork decode path as the full rail so
    // the cover texture stays warm across the detent. expand restores the full rail (persisted by the caller).
    public static Element BuildCompact(DetailModel m, float stripW, Action expand)
    {
        const float pad = Spacing.S;   // 8 — tighter than the full rail; cover still fills the strip
        float cover = MathF.Max(48f, stripW - pad - pad);
        Element coverHit = new BoxEl
        {
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f, OnClick = expand,
            Children = [HeroArtwork(m, cover, saturation: 1.18f)],
        };
        Element title = new TextEl(m.Title)
        {
            Size = 12f, Weight = 600, Color = Tok.TextPrimary,
            Width = cover, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
            Wrap = TextWrap.WrapWholeWords,
        };
        Element expandHit = new BoxEl
        {
            Width = cover, Height = 28f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            HoverFill = Tok.FillSubtleSecondary, OnClick = expand,
            Children = [Icon(Icons.ChevronRight, 14f, Tok.TextSecondary)],
        };
        Element strip = new BoxEl
        {
            Key = "detail-rail-compact",
            Direction = 1, Width = stripW, Shrink = 0f, Gap = Spacing.S,
            Padding = new Edges4(pad, Spacing.L, pad, Spacing.L),
            Fill = Tok.FillLayerDefault, ClipToBounds = true,
            Children =
            [
                coverHit,
                title,
                new BoxEl { Grow = 1f, MinHeight = 0f },   // push the chevron to the foot
                expandHit,
            ],
        };
        return ToolTip.Wrap(strip, m.Title);
    }

    // The header for the VERTICAL (narrow) layout, fixed above the scrolling track list. The cover sits on the LEFT with
    // the metadata (badges/owner, title, artist, meta) BESIDE it (filling the width to the right of the art); the PLAY
    // cluster + the context actions (copy-to-playlist / add-to-queue) stack full-width below. Center-aligned so the cover
    // and the text block balance (only a small symmetric gap, never a big wedge under the cover). The title wraps to
    // ≤3 lines (no truncation). The list's own command bar follows below (in the track list chrome). Drops the description.
    public static Element BuildHeader(DetailModel m, DetailConfig cfg, DetailHandlers h, Loadable<DetailModel> modelSource, bool includeReleasePanel = true, ActionServices? acts = null)
    {
        const float coverSz = 140f;
        var info = new List<Element>(4);

        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            // The info column already clamps (Grow/Basis 0) — the run's MaxLines/ellipsis carries the rest, so it needs
            // no explicit Width here (unlike the fixed-cover-edge rail above).
            if (EyebrowText(m, cfg) is { Length: > 0 } eyebrow) info.Add(EyebrowRun(eyebrow));
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            info.Add(PlaylistOwnerBlock(m, 600f, modelSource));
        }

        bool editable = m.Capabilities.CanEditMetadata && m.ContextUri is { Length: > 0 };

        // Title cross-stretches to the info column's (Grow) width → wraps to it; ≤3 lines avoids truncation.
        info.Add(editable
            // Title (28/36/600) — the same rung and weight as the side rail's hero above. Was a 900 override.
            ? PlaylistInlineEdit.Title(modelSource, 600f, 28f, lineHeight: 36f)
            : WaveeType.PageHero(m.Title) with { Size = 28f, LineHeight = 36f, Weight = 600, Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis });
        if (cfg.Badges == BadgeStyle.TypeYear && m.Artists.Count > 0)
            info.Add(Embed.Comp(() => new ArtistFacePile(m, 600f, h)));
        if (cfg.Badges != BadgeStyle.TypeYear && m.MetaLine is { Length: > 0 })
            info.Add(WaveeType.TrackMeta(m.MetaLine) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        var coverRow = new BoxEl
        {
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,   // center → balanced (no big wedge)
            Children =
            [
                new BoxEl
                {
                    Width = coverSz, Height = coverSz, Corners = CornerRadius4.All(Radii.Card),
                    Shadow = Elevation.Card, ClipToBounds = true,
                    Draggable = WaveeDetailDrag.Hero(m, acts),
                    Children = [editable ? PlaylistInlineEdit.Cover(modelSource, coverSz) : HeroArtwork(m, coverSz)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XS, Children = info.ToArray() },
            ],
        };

        var headerKids = new List<Element>(4) { coverRow, PlayRow(h, m) };
        if (PreReleaseCard(m, h) is { } countdown) headerKids.Add(countdown);
        if (includeReleasePanel && cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            headerKids.Add(AlbumTrailing.ReleasePanel(m, h, outerPadding: false));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.S),
            Children = headerKids.ToArray(),
        };
    }

    /// <summary>The upcoming-release countdown, or null when nothing about this release is still ahead of us.
    ///
    /// The gate is <see cref="DetailModel.UpcomingAt"/>, NOT <c>IsPreRelease</c>: an album can be genuinely upcoming in
    /// three different wire shapes and only one of them sets that flag (a declared <c>preReleaseEndDateTime</c>; a
    /// partly-released album whose remaining rows carry a future live timestamp; a plain future release date with no
    /// prerelease marking at all). <see cref="PreReleaseDerivation"/> owns the precedence between them, so the card
    /// fires for all three and — because each rung is wall-clock checked — for none of them once the record is out.
    ///
    /// A pre-release with no derivable instant still renders nothing: a countdown to an unknown moment is not a
    /// countdown, and the rest of the page already says the album is upcoming.
    ///
    /// Shared by all three mount sites (the two-column rail, the vertical header, and the vertical trailing body) so
    /// the card cannot exist in one layout and be missing in another across a resize.</summary>
    internal static Element? PreReleaseCard(DetailModel m, DetailHandlers h)
        => m.UpcomingAt is { } end
            ? Embed.Comp(() => new PreReleaseCountdown { ReleaseAt = end, Accent = () => h.Accent })
                with { Key = "prerelease:" + m.ContextUri + ":" + end.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;

    // The play cluster for the vertical (narrow) header: Play pill + shuffle / save / share, wrapping as a unit. The list
    // view controls (filter / sort / row size) now live in the track list's own command bar, so this row carries none.
    static Element PlayRow(DetailHandlers h, DetailModel m) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Wrap = true,
        Children =
        [
            PlayPill(h.Accent, h.PlayAll),
            // Shuffle lives in the track-list command bar now (see DetailTracks.Toolbar).
            // Same heart-target swap as the two-column rail above (see the comment there): a full pre-release is saved
            // against its prerelease entity, and the key is the target because SaveButton's uri freezes at mount.
            (m.PreReleaseUri ?? m.ContextUri) is { Length: > 0 } saveUri
                ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize, m.Title)) with { Key = "save:" + saveUri }
                : Fab(Icons.Heart, () => { }),
            Fab(Icons.Share, () => { if (m.ShareUrl is { Length: > 0 } url) InputHooks.Current.Default.OpenUri?.Invoke(url); }),
        ],
    };

    // The billed-artist control: a stacked face-pile (the album's primary artists' avatars, overlapping, capped at 3) +
    // a "+N" badge folding in the rest of the DISTINCT artists across the album's tracks + the billed name. Clickable to
    // the lead artist. Falls back to the plain artist names when the album carries no artist avatars.
    static Element BilledArtists(DetailModel m, float cover, DetailHandlers h)
    {
        var detailed = m.AlbumArtists;
        if (detailed is not { Count: > 0 })
        {
            var only = m.Artists[0];
            return new BoxEl
            {
                Direction = 0, OnClick = () => h.Go("artist:" + only.Uri, only.Name),
                Children = [WaveeType.TrackTitle(DetailFormat.ArtistNames(m.Artists)) with { Width = cover, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            };
        }

        // Distinct artists across the album (primary ∪ all track artists) → the "+N" overflow count.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in detailed) seen.Add(a.Uri);
        foreach (var t in m.Tracks) foreach (var ar in t.Artists) if (ar.Uri.Length > 0) seen.Add(ar.Uri);

        int shown = Math.Min(detailed.Count, 3);
        int extra = seen.Count - shown;
        var lead = detailed[0];

        var pile = new List<Element>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            var a = detailed[i];
            pile.Add(new BoxEl
            {
                Width = 28f, Height = 28f, Shrink = 0f, Corners = CornerRadius4.All(14f), ClipToBounds = true,
                Margin = new Edges4(i == 0 ? 0f : -10f, 0f, 0f, 0f),   // overlap the stack
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 28f, 28f, 14f)],
            });
        }
        if (extra > 0)
            pile.Add(new BoxEl
            {
                Height = 28f, MinWidth = 34f, Shrink = 0f, Corners = CornerRadius4.All(14f), Fill = Tok.FillSubtleTertiary,
                Margin = new Edges4(-10f, 0f, 0f, 0f), Padding = new Edges4(11f, 0f, 8f, 0f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl("+" + extra) { Size = 11f, Weight = 700, Color = Tok.TextSecondary }],
            });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MaxWidth = cover,
            Corners = CornerRadius4.All(16f), Padding = new Edges4(2f, 2f, Spacing.S, 2f),
            HoverFill = Tok.FillSubtleSecondary, OnClick = () => h.Go("artist:" + lead.Uri, lead.Name),
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = pile.ToArray() },
                new TextEl(DetailFormat.ArtistNames(m.Artists)) { Size = 14f, Weight = 700, Color = Tok.AccentTextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    internal static Element PlaylistOwnerBlock(DetailModel m, float cover, Loadable<DetailModel> full)
        => ShowCollaborators(m)
            ? Embed.Comp(() => new CollaboratorFacePile(m, cover, full))
            : PlaylistInlineEdit.OwnerRow(full, cover);

    internal static bool ShowCollaborators(DetailModel m)
        => m.Collaborators is { Count: > 0 } members && (m.Capabilities.IsCollaborative || members.Count >= 2);

    /// <summary>The identity EYEBROW string, for every detail header (this rail, the narrow header, the vertical hero).
    /// ONE composition so the same release can never be worded two ways across a layout cross: "ALBUM · 2019", the kind
    /// alone when the year is unknown (a show, an undated release), the year alone when the kind is, and "" when neither
    /// is known.</summary>
    internal static string EyebrowText(DetailModel m, DetailConfig cfg)
        => cfg.Badges switch
        {
            BadgeStyle.TypeYear => m.BadgeType is { Length: > 0 } type && m.Year is { Length: > 0 } year
                ? type + " · " + year
                : m.BadgeType ?? m.Year ?? "",
            BadgeStyle.OwnerRow => Loc.Get(Strings.Nav.Playlist),
            _ => Loc.Get(Strings.Nav.YourLibrary),
        };

    /// <summary>The eyebrow RUN — small, heavy, tracked-out tertiary metadata on one line. Shared with the vertical
    /// hero: the type/year fact must look identical in both layouts, so the styling has exactly one definition.</summary>
    internal static TextEl EyebrowRun(string text) => new(text)
    {
        // Caption (12/16/600) — the ONE caps-eyebrow rung. Caps and the 40/1000 tracking stay; 11 was off the ramp.
        Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextTertiary, CharSpacing = 40f,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // The WaveeCta media pill on the cover-extracted accent (WaveeCta resolves the WCAG on-fill ink itself).
    static Element PlayPill(ColorF accent, Action onPlay)
        => WaveeCta.Play(accent, onPlay, Loc.Get(Strings.Detail.Play));

    static Element Fab(string glyph, Action onClick) => new BoxEl
    {
        Width = FabSize, Height = FabSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(FabSize / 2f),
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press, OnClick = onClick,
        Children = [Icon(glyph, 16f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

}
