using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The fixed-width left metadata rail (album / single / playlist) in its own vertical scroller. Stack order:
// cover → badges → big hero title → owner/artist row → meta line → CTA cluster → tools/actions → description.
// Every clamped run gets an EXPLICIT Width (= cover edge) so a long title/owner never widens the rail (MediaCard's
// discipline). The cover edge is a constant per config (RailWidth − side padding), so no SizeChanged hack is needed.
static class DetailRail
{
    const float SidePadL = WaveeSpace.L;   // 16
    const float SidePadR = WaveeSpace.S;   // 8
    const float FabSize = 40f;
    // Decode the rail/header cover at the SAME size the Home shelf card uses (MediaCard's ShelfDecodePx, 256) so a Hero
    // fly hands the cover off to the SAME cached texture — pixel-identical, with NO fresh first-visit cover decode (the
    // cold connected-animation spike). Displayed larger (the ~300px rail cover) is a slight, imperceptible upscale.
    const int HeroCoverDecodePx = 256;

    public static float CoverEdge(float railW) => MathF.Max(80f, railW - SidePadL - SidePadR);

    internal static Element HeroArtwork(DetailModel m, float size) =>
        LikedSongsArtwork.IsLikedUri(m.ContextUri) && m.Cover is null
            ? LikedSongsArtwork.Cover(size, WaveeRadius.Card, m.MorphKey)
            : Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, size, size, WaveeRadius.Card, m.MorphKey, decodePx: HeroCoverDecodePx);

    // The side rail: the cover STRETCHES to fill the column width (a big hero — the image is NEVER shrunk for height).
    // The height fit comes from the TEXT — titleSize (the shell lowers it on a short rail; auto-fits down to 18px) and
    // the description's line cap (descMaxLines) — and only then the rail's own scrollbar (last resort).
    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, float railW, float titleSize, int descMaxLines)
    {
        float cover = CoverEdge(railW);
        var kids = new List<Element>(10);

        // Cover (art with a graceful gradient fallback) — stretched to the full column width.
        kids.Add(new BoxEl
        {
            Width = cover, Height = cover, Corners = CornerRadius4.All(WaveeRadius.Card),
            Shadow = Elevation.Card, ClipToBounds = true,
            Children = [HeroArtwork(m, cover)],
        });

        // Badges row.
        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            var pills = new List<Element>(2);
            if (m.BadgeType is { Length: > 0 }) pills.Add(BadgePill(m.BadgeType));
            if (m.Year is { Length: > 0 }) pills.Add(BadgePill(m.Year));
            if (pills.Count > 0)
                kids.Add(new BoxEl { Direction = 0, Gap = WaveeSpace.S, Children = pills.ToArray() });
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            kids.Add(PlaylistOwnerBlock(m, cover));
        }

        // Hero title — a heavy run that AUTO-FITS to the cover width in ≤2 LINES, from titleSize down to 18px. The shell
        // LOWERS titleSize on a SHORT rail so the TEXT gives (never the image). A short name stays big; a long one scales
        // to a compact 2-line block. Natural line height; ellipsis is the last resort.
        kids.Add(WaveeType.PageHero(m.Title) with
        {
            Size = titleSize, MinSize = 18f, Weight = 900, Width = cover, LineHeight = float.NaN,   // font-NATURAL leading: tracks the auto-fit size (a pinned 36 gaps badly once the font shrinks)
            Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
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
            Direction = 0, Wrap = true, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, WaveeSpace.XS, 0f, 0f),
            Children =
            [
                PlayPill(h.Accent, h.PlayAll),
                new BoxEl
                {
                    Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        // Shuffle now lives in the track-list command bar; the rail keeps just the hero Play + save/share.
                        m.ContextUri is { Length: > 0 } saveUri
                            ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize))
                            : Fab(Icons.Heart, () => { }),
                        Fab(Icons.Share, () => Share(m)),
                    ],
                },
            ],
        });

        if (cfg.Content == DetailContent.Tracks)
            kids.Add(ContextActions(m, cfg, h));

        if (cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            kids.Add(AlbumTrailing.ReleasePanel(m, h, outerPadding: false));

        // Description / release blurb — an HTML fragment (links to artists/playlists, bold): parse → rich spans (links
        // accent + clickable via h.Go, bold rendered, entities decoded). Trimmed to descMaxLines (shell lowers it when short).
        if (descMaxLines > 0 && m.Description is { Length: > 0 } desc)
            kids.Add(RichText.Of(desc, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, cover, descMaxLines,
                u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); }));

        var rail = new BoxEl
        {
            Direction = 1, Gap = 14f, Width = railW, Shrink = 0f,
            Padding = new Edges4(SidePadL, WaveeSpace.XXL, SidePadR, WaveeSpace.XXL),
            Children = kids.ToArray(),
        };
        // Own vertical scroller (hidden bar by default) — the LAST resort once the TEXT has shrunk and it still overflows
        // (the image stays full-width; the text gave first).
        return ScrollView(rail) with { Grow = 0f, Shrink = 0f, Width = railW };
    }

    // The header for the VERTICAL (narrow) layout, fixed above the scrolling track list. The cover sits on the LEFT with
    // the metadata (badges/owner, title, artist, meta) BESIDE it (filling the width to the right of the art); the PLAY
    // cluster + the context actions (copy-to-playlist / add-to-queue) stack full-width below. Center-aligned so the cover
    // and the text block balance (only a small symmetric gap, never a big wedge under the cover). The title wraps to
    // ≤3 lines (no truncation). The list's own command bar follows below (in the track list chrome). Drops the pills + description.
    public static Element BuildHeader(DetailModel m, DetailConfig cfg, DetailHandlers h, bool includeReleasePanel = true)
    {
        const float coverSz = 140f;
        var info = new List<Element>(4);

        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            var pills = new List<Element>(2);
            if (m.BadgeType is { Length: > 0 }) pills.Add(BadgePill(m.BadgeType));
            if (m.Year is { Length: > 0 }) pills.Add(BadgePill(m.Year));
            if (pills.Count > 0) info.Add(new BoxEl { Direction = 0, Gap = WaveeSpace.S, Children = pills.ToArray() });
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            info.Add(PlaylistOwnerBlock(m, 600f));
        }

        // Title cross-stretches to the info column's (Grow) width → wraps to it; ≤3 lines avoids truncation.
        info.Add(WaveeType.PageHero(m.Title) with { Size = 28f, Weight = 900, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis });
        if (cfg.Badges == BadgeStyle.TypeYear && m.Artists.Count > 0)
            info.Add(Embed.Comp(() => new ArtistFacePile(m, 600f, h)));
        if (cfg.Badges != BadgeStyle.TypeYear && m.MetaLine is { Length: > 0 })
            info.Add(WaveeType.TrackMeta(m.MetaLine) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        var coverRow = new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,   // center → balanced (no big wedge)
            Children =
            [
                new BoxEl
                {
                    Width = coverSz, Height = coverSz, Corners = CornerRadius4.All(WaveeRadius.Card),
                    Shadow = Elevation.Card, ClipToBounds = true,
                    Children = [HeroArtwork(m, coverSz)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS, Children = info.ToArray() },
            ],
        };

        var headerKids = new List<Element>(4) { coverRow, PlayRow(h, m) };
        if (cfg.Content == DetailContent.Tracks)
            headerKids.Add(ContextActions(m, cfg, h));
        if (includeReleasePanel && cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            headerKids.Add(AlbumTrailing.ReleasePanel(m, h, outerPadding: false));

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M, Shrink = 0f,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.S),
            Children = headerKids.ToArray(),
        };
    }

    // The play cluster for the vertical (narrow) header: Play pill + shuffle / save / share, wrapping as a unit. The list
    // view controls (filter / sort / row size) now live in the track list's own command bar, so this row carries none.
    static Element PlayRow(DetailHandlers h, DetailModel m) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center, Wrap = true,
        Children =
        [
            PlayPill(h.Accent, h.PlayAll),
            // Shuffle lives in the track-list command bar now (see DetailTracks.Toolbar).
            m.ContextUri is { Length: > 0 } saveUri
                ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize))
                : Fab(Icons.Heart, () => { }),
            Fab(Icons.Share, () => Share(m)),
        ],
    };

    // The promoted context actions that replaced the old filter/sort/density pill in the rail: an add/copy-to-playlist
    // button + an "Add to queue" split button whose dropdown chooses Play next (front of queue) or Play after (end).
    // Read-only contexts (followed playlists, Liked) copy; an editable playlist adds. Wraps as a unit on a narrow rail.
    static Element ContextActions(DetailModel m, DetailConfig cfg, DetailHandlers h)
    {
        bool copy = cfg.Heart == HeartMode.Follow || LikedSongsArtwork.IsLikedUri(m.ContextUri);
        string addLabel = Loc.Get(copy ? Strings.Detail.CopyToPlaylist : Strings.Detail.AddToPlaylist);
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Wrap = true,
            Margin = new Edges4(0f, 2f, 0f, 0f),
            Children =
            [
                Button.Standard(addLabel, h.AddToPlaylist),
                SplitButton.Create(
                    Loc.Get(Strings.Detail.AddToQueue),
                    h.AddToQueue,   // primary (main-body) click → append to the end of the queue (= Play after)
                    [
                        new MenuFlyoutItem(Loc.Get(Strings.Detail.PlayNext), Icons.Next, Invoke: h.PlayNext),
                        new MenuFlyoutItem(Loc.Get(Strings.Detail.PlayAfter), Icons.Queue, Invoke: h.AddToQueue),
                    ]),
            ],
        };
    }

    static void Share(DetailModel m)
    {
        if (m.ShareUrl is { Length: > 0 } url) InputHooks.Current.Default.OpenUri?.Invoke(url);
    }

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
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S, MaxWidth = cover,
            Corners = CornerRadius4.All(16f), Padding = new Edges4(2f, 2f, WaveeSpace.S, 2f),
            HoverFill = Tok.FillSubtleSecondary, OnClick = () => h.Go("artist:" + lead.Uri, lead.Name),
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = pile.ToArray() },
                new TextEl(DetailFormat.ArtistNames(m.Artists)) { Size = 14f, Weight = 700, Color = Tok.AccentTextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element PlaylistOwnerBlock(DetailModel m, float cover)
        => ShowCollaborators(m)
            ? Embed.Comp(() => new CollaboratorFacePile(m, cover))
            : OwnerRow(m.OwnerName ?? "", m.OwnerImage, cover);

    static bool ShowCollaborators(DetailModel m)
        => m.Collaborators is { Count: > 0 } members && (m.Capabilities.IsCollaborative || members.Count >= 2);

    static Element OwnerRow(string owner, Image? avatar, float cover) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Children =
        [
            PersonPicture.Create("", 24f, displayName: owner, imageSourcePath: avatar?.Url),
            WaveeType.TrackTitle(owner) with { MaxWidth = cover - 32f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static Element BadgePill(string text) => new BoxEl
    {
        Corners = CornerRadius4.All(14f), Padding = new Edges4(10f, 3f, 10f, 5f),
        Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [new TextEl(text) { Size = 11f, Weight = 600, Color = Tok.TextSecondary, CharSpacing = 40f }],
    };

    static Element PlayPill(ColorF accent, Action onPlay) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(20f), Padding = new Edges4(18f, 10f, 18f, 10f),
        Fill = accent, HoverScale = 1.04f, PressScale = 0.97f, Shadow = Elevation.Card, OnClick = onPlay,
        Children =
        [
            Icon(Icons.Play, 14f, WaveePalette.OnAccent(accent)),
            new TextEl(Loc.Get(Strings.Detail.Play)) { Size = 14f, Weight = 700, Color = WaveePalette.OnAccent(accent) },
        ],
    };

    static Element Fab(string glyph, Action onClick) => new BoxEl
    {
        Width = FabSize, Height = FabSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(FabSize / 2f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        HoverScale = 1.06f, PressScale = 0.94f, OnClick = onClick,
        Children = [Icon(glyph, 16f, Tok.TextSecondary)],
    };

}
