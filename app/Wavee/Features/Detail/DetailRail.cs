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

    public static float CoverEdge(float railW) => MathF.Max(80f, railW - SidePadL - SidePadR);

    // The side rail at an ADAPTIVE width (railW): the shell shrinks it as the window narrows (then switches to the
    // vertical header below). The cover + every clamped run derive from railW, so they shrink with it.
    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, float railW)
    {
        float cover = CoverEdge(railW);
        var kids = new List<Element>(10);

        // Cover (art with a graceful gradient fallback) + soft elevation.
        kids.Add(new BoxEl
        {
            Width = cover, Height = cover, Corners = CornerRadius4.All(WaveeRadius.Card),
            Shadow = Elevation.Card, ClipToBounds = true,
            Children = [Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, cover, cover, WaveeRadius.Card)],
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

        // Hero title — a heavy run that AUTO-FITS: starts at ≈40px and shrinks (down to 18px) to fit the cover width in
        // ≤2 LINES, minimizing wraps (engine TextEl.MinSize). A short name stays big (1 line @ 40px); a long one scales
        // down to a compact 2-line block instead of a tall 3-line tower (the reported "font not adapting" → scroller).
        // Natural line height (no fixed LineHeight) so spacing scales with the chosen size; ellipsis is the last resort.
        kids.Add(WaveeType.PageHero(m.Title) with
        {
            Size = 40f, MinSize = 18f, Weight = 900, Width = cover,
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
        });

        // Billed-artist row (album/single) — clickable to the first artist.
        if (cfg.Badges == BadgeStyle.TypeYear && m.Artists.Count > 0)
        {
            var first = m.Artists[0];
            kids.Add(new BoxEl
            {
                Direction = 0, OnClick = () => h.Go("artist:" + first.Uri, first.Name),
                Children = [WaveeType.TrackTitle(DetailFormat.ArtistNames(m.Artists)) with { Width = cover, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            });
        }

        // Meta line.
        if (m.MetaLine is { Length: > 0 })
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
                        Fab(cfg.Heart == HeartMode.Follow ? Icons.Add : Icons.Heart, () => { /* TODO: ILibraryMutations */ }),
                        Fab(Icons.Share, () => { /* TODO: share */ }),
                    ],
                },
            ],
        });

        // Secondary pills — hybrid fit-then-wrap (see SecondaryPills): condense padding to claw back space when the
        // rail is tight, and only drop to the next line when even condensed they won't fit (whole pill → never clips).
        kids.Add(SecondaryPills(
        [
            (cfg.Heart == HeartMode.Follow ? Loc.Get(Strings.Detail.CopyToPlaylist) : Loc.Get(Strings.Detail.AddToPlaylist), () => { /* TODO */ }),
            (Loc.Get(Strings.Detail.AddToQueue), () => { /* TODO */ }),
        ], cover));

        // Description / release blurb.
        if (m.Description is { Length: > 0 } desc)
            kids.Add(WaveeType.TrackMeta(desc) with { Width = cover, Wrap = TextWrap.Wrap, MaxLines = 6, Trim = TextTrim.CharacterEllipsis });

        var rail = new BoxEl
        {
            Direction = 1, Gap = 14f, Width = railW, Shrink = 0f,
            Padding = new Edges4(SidePadL, WaveeSpace.XXL, SidePadR, WaveeSpace.XXL),
            Children = kids.ToArray(),
        };
        // Own vertical scroller (independent of the right area); hidden bar by default (no AlwaysShowScrollbar).
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
            info.Add(WaveeType.TrackTitle(DetailFormat.ArtistNames(m.Artists)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        if (m.MetaLine is { Length: > 0 })
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
                    Children = [Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, coverSz, coverSz, WaveeRadius.Card)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS, Children = info.ToArray() },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M, Shrink = 0f,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.S),
            Children = [coverRow, PlayToolbarRow(cfg, h, m)],
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
                Fab(cfg.Heart == HeartMode.Follow ? Icons.Add : Icons.Heart, () => { /* TODO */ }),
                Fab(Icons.Share, () => { /* TODO */ }),
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
            Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary),
            new TextEl(Loc.Get(Strings.Detail.Play)) { Size = 14f, Weight = 700, Color = Tok.TextOnAccentPrimary },
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

    static Element SecondaryPill(string label, Action onClick, float padX) => new BoxEl
    {
        Direction = 0, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(padX, 0f, padX, 0f), Corners = CornerRadius4.All(18f),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextSecondary }],
    };
}
