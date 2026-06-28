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
// cover → badges → big hero title → owner/artist row → meta line → CTA cluster → secondary pills → description.
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
            Children = [Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, cover, cover, WaveeRadius.Card, m.MorphKey, decodePx: HeroCoverDecodePx)],
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
            kids.Add(OwnerRow(m.OwnerName, cover, h));
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
                        Fab(Icons.Shuffle, h.Shuffle),
                        m.ContextUri is { Length: > 0 } saveUri
                            ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize))
                            : Fab(Icons.Heart, () => { }),
                        Fab(Icons.Share, () => Share(m)),
                    ],
                },
            ],
        });

        // Secondary pills — hybrid fit-then-wrap (see SecondaryPills): condense padding to claw back space when the
        // rail is tight, and only drop to the next line when even condensed they won't fit (whole pill → never clips).
        kids.Add(SecondaryPills(
        [
            (cfg.Heart == HeartMode.Follow ? Loc.Get(Strings.Detail.CopyToPlaylist) : Loc.Get(Strings.Detail.AddToPlaylist), h.AddToPlaylist),
            (Loc.Get(Strings.Detail.AddToQueue), h.AddToQueue),
        ], cover));

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
    // the metadata (badges/owner, title, artist, meta) BESIDE it (filling the width to the right of the art), and the
    // combined PLAY + TOOLBAR row spans full-width below. Center-aligned so the cover and the text block balance (only a
    // small symmetric gap, never a big wedge under the cover). The title wraps to ≤3 lines (no truncation). The track
    // list drops its own toolbar in this mode, so the column header follows directly. Drops the pills + description.
    public static Element BuildHeader(DetailModel m, DetailConfig cfg, DetailHandlers h)
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
            info.Add(OwnerRow(m.OwnerName, 600f, h));
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
                    Children = [Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, coverSz, coverSz, WaveeRadius.Card, m.MorphKey, decodePx: HeroCoverDecodePx)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS, Children = info.ToArray() },
            ],
        };

        var headerKids = new List<Element>(3) { coverRow, PlayToolbarRow(cfg, h, m) };
        if (cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            headerKids.Add(AlbumTrailing.ReleasePanel(m, h, outerPadding: false));

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M, Shrink = 0f,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.S),
            Children = headerKids.ToArray(),
        };
    }

    // Play controls + list toolbar in ONE row: while they fit, play group on the left and the toolbar RIGHT-aligned
    // (a Grow spacer between); when they don't fit, the row stacks and the toolbar drops to the next line LEFT-aligned.
    // (The engine's flex-wrap doesn't grow/justify per wrapped line, so we pick the arrangement by measured width.)
    static Element PlayToolbarRow(DetailConfig cfg, DetailHandlers h, DetailModel m)
    {
        Element Play() => new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Children =
            [
                PlayPill(h.Accent, h.PlayAll),
                Fab(Icons.Shuffle, h.Shuffle),
                m.ContextUri is { Length: > 0 } saveUri
                    ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize))
                    : Fab(Icons.Heart, () => { }),
                Fab(Icons.Share, () => Share(m)),
            ],
        };
        Element Tools() => new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center,
            Children =
            [
                Embed.Comp(() => new FilterButton(h.Query, h.Flags, h.SetFlags, m.HasVideo)),
                Embed.Comp(() => new MoreButton(m, h)),
                Embed.Comp(() => new SortMenuButton(h.Sort, h.SetSort, cfg.ShowAlbumColumn, m.HasDateAdded)),
                Embed.Comp(() => new ListButton(h.Density, h.SetDensity)),
            ],
        };
        const float threshold = 430f;   // play group (~270) + toolbar group (~140) + gap
        return Responsive.Of(w => w >= threshold
            ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = [Play(), new BoxEl { Grow = 1f }, Tools()] }
            : new BoxEl { Direction = 1, Gap = WaveeSpace.M, Children = [Play(), Tools()] },
            fallback: 9999f);   // assume wide on the first frame → one row, corrected on measure
    }

    // List toolbar button (filter / sort / view) — square, 32, used in the vertical header's play+toolbar row.
    static Element ToolBtn(string glyph) => new BoxEl
    {
        Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(WaveeRadius.Control),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => { /* TODO: search-in-list / sort / view (visual stubs in v1) */ },
        Children = [Icon(glyph, 14f, Tok.TextSecondary)],
    };

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

    static Element OwnerRow(string owner, float cover, DetailHandlers h) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = 24f, Height = 24f, Corners = CornerRadius4.All(12f), Fill = Tok.FillSubtleSecondary,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Home, 12f, Tok.TextSecondary)],
            },
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

    // Hybrid fit-then-wrap row: measure the available width and CONDENSE the pills' padding (full → tight) to claw back
    // space so they stay on one line as long as possible; once even the tight pills won't fit, Wrap drops the overflow
    // pill to the next line (a whole pill — text never gets cut off). "Shrink up to a point, then wrap." The width
    // estimate only chooses the padding tier (a cosmetic call); flex-wrap is what guarantees no clipping either way.
    static Element SecondaryPills((string Label, Action OnClick)[] pills, float fallbackW)
    {
        const float gap = WaveeSpace.S, fullPad = 14f, tightPad = 8f, estChar = 7.2f;
        float naturalW = gap * Math.Max(0, pills.Length - 1);
        foreach (var p in pills) naturalW += p.Label.Length * estChar + 2f * fullPad;

        return Responsive.Of(w =>
        {
            float padX = w + 0.5f >= naturalW ? fullPad : tightPad;
            var kids = new Element[pills.Length];
            for (int i = 0; i < pills.Length; i++) kids[i] = SecondaryPill(pills[i].Label, pills[i].OnClick, padX);
            return new BoxEl { Direction = 0, Wrap = true, Gap = gap, AlignItems = FlexAlign.Center, Children = kids };
        }, fallback: fallbackW);
    }

    // A Fluent standard button (rounded-RECT, subtle resting fill + hairline stroke), not a Spotify full-pill outline:
    // ControlFill at rest, a lighter hover, a recessed press — the WinUI secondary-action look.
    static Element SecondaryPill(string label, Action onClick, float padX) => new BoxEl
    {
        Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(padX, 0f, padX, 0f), Corners = CornerRadius4.All(WaveeRadius.Control),
        Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, OnClick = onClick,
        Children = [new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary }],
    };
}
