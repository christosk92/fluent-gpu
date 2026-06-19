using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Reusable media cards composed from existing primitives. SIZE-REACTIVE: a Shelf card is built at a caller-supplied
// dynamic width (PagedShelf measures the viewport and fills it with equal cards). Every clamped label gets an EXPLICIT
// width (column context) or Grow+Basis=0 (row context) so text NEVER contributes its full single-line width to parent
// measure (Element.cs:443) — that is what made text bleed out of cards and pushed grids past the viewport edge.
public static class MediaCard
{
    public const float QuickW = 64f;     // quick-pick cover edge
    public const float QuickH = 64f;     // quick-pick tile height
    const float ShelfDecodePx = 256f;    // stable across responsive card widths, avoids resize-time redecodes
    const float FabSize  = 44f;
    const float FabInset = 8f;
    const float Pad      = WaveeSpace.S;

    // ── Shelf card: square (album/playlist) or circular (artist) cover, sized to fill `cardW`. ───────────
    public static Element Shelf(Image? cover, string title, string subtitle,
                                Action onClick, Action onPlay, float cardW, bool circular = false)
    {
        float inner = MathF.Max(48f, cardW - 2f * Pad);          // cover edge = card width minus side padding
        float r = circular ? inner / 2f : WaveeRadius.Card;

        var coverStack = ZStack(
            Image(cover?.Url ?? "", ImageFit.Cover, 1f, ShelfDecodePx, r, placeholder: Tok.FillCardDefault),
            new BoxEl
            {
                // FAB positioner only — NO OnClick. Hover/hit is single-node (it doesn't bubble), so a full-cover
                // overlay with a handler would be the hover target over the image and the CARD's HoverScale would
                // never fire when the pointer is over the cover. With no handler here, the hit walks up to the card
                // (which carries OnClick), so hovering the cover scales the card; the FAB keeps its own handler.
                Width = inner, Height = inner, Direction = 1,
                Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                Padding = new Edges4(0f, 0f, FabInset, FabInset),
                Children = [ PlayFab(onPlay) ],
            });

        return new BoxEl
        {
            // No explicit Width: the shelf cell (a column container) cross-stretches the card to the cell's LIVE width.
            // Grow=1 fills the cell's (definite) HEIGHT too, so EVERY card is exactly the cell height → uniform panels
            // regardless of title line-count (a 1-line vs 2-line title no longer yields a shorter/taller card); the
            // content stays top-aligned (cover, then text) with any slack below.
            Direction = 1, Gap = Pad, Grow = 1f,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverScale = 1.02f, PressScale = 0.99f, ClipToBounds = true,
            OnClick = onClick,
            Children =
            [
                coverStack,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        // Explicit Width clamps the run to the card (no overflow, ellipsis at the edge).
                        WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                        WaveeType.TrackMeta(subtitle) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        };
    }

    // The Shelf(...) card height for a given card width — kept in lockstep with Shelf(...) so PagedShelf can size its
    // (cross-axis) viewport to the WIDTH-driven card: top pad + square cover + gap + a fixed 2-line title + 1-line meta
    // block + bottom pad. Generous on the text block so a long wrapped title never clips against the viewport edge.
    public static float ShelfHeight(float cardW)
    {
        float inner = MathF.Max(48f, cardW - 2f * Pad);   // cover edge (matches Shelf)
        // Exact: BodyStrong line-height 20 ×2 (TrackTitle MaxLines 2) + 2 (Gap) + Caption line-height 16 (TrackMeta).
        const float textBlock = 2f * 20f + 2f + 16f;       // = 58
        return Pad + inner + Pad + textBlock + WaveeSpace.M;
    }

    // ── Wide "jump back in" tile: cover + title (fills, ellipsised) + trailing Play FAB ──────────────────
    public static Element QuickPick(Image? cover, string title, Action onClick, Action onPlay)
    {
        return new BoxEl
        {
            Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true, OnClick = onClick,
            Children =
            [
                Image(cover?.Url ?? "", QuickW, QuickH, 0f, placeholder: Tok.FillCardDefault),
                // Grow + Basis=0: take the remaining width (never the title's intrinsic width) → ellipsis, no overflow.
                WaveeType.TrackTitle(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(0f, 0f, WaveeSpace.M, 0f),
                    Children = [ PlayFab(onPlay, 36f) ],
                },
            ],
        };
    }

    // ── Accent Play FAB (own hover/press feedback; click plays) ──────────────────────────────────────────
    static Element PlayFab(Action onPlay, float size = FabSize) => new BoxEl
    {
        Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(size / 2f),
        Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
        Shadow = Elevation.Card, HoverScale = 1.07f, PressScale = 0.92f,
        OnClick = onPlay,
        Children = [ Icon(Icons.Play, size * 0.42f, Tok.TextOnAccentPrimary) ],
    };

    // ── Skeletons (matched layout for StatefulRegion's shimmer → reveal) ─────────────────────────────────
    public static Element ShelfSkeleton(float cardW, bool circular = false)
    {
        float inner = MathF.Max(48f, cardW - 2f * Pad);
        return new BoxEl
        {
            Direction = 1, Width = cardW, Gap = Pad,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
            Children =
            [
                new BoxEl { Width = inner, Height = inner, Fill = Tok.FillCardDefault,
                            Corners = CornerRadius4.All(circular ? inner / 2f : WaveeRadius.Card) },
                new BoxEl { Width = inner * 0.85f, Height = 13f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                new BoxEl { Width = inner * 0.55f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
            ],
        };
    }

    public static Element QuickPickSkeleton() => new BoxEl
    {
        Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary, ClipToBounds = true,
        Children =
        [
            new BoxEl { Width = QuickW, Height = QuickH, Fill = Tok.FillCardDefault },
            new BoxEl { Grow = 1f, Basis = 0f, Height = 13f, Margin = new Edges4(0f, 0f, WaveeSpace.M, 0f),
                        Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
        ],
    };
}
