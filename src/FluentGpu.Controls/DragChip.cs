using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// The DATA an app supplies for the framework-rendered drag chip — never elements, never positions. Populate it from
/// the drag payload in a <see cref="DragChip.Resolve"/> callback; the framework renders the premiere card (opaque, max
/// <see cref="DragChip.MaxWidth"/>, art + title + subtitle, corner count badge, stacked backdrop for multi-drag, pickup
/// tilt, caption row, not-allowed cue, cursor offset and window clamp).
/// </summary>
/// <param name="Art">Optional leading artwork ELEMENT (an <c>ImageEl</c>, an avatar, a glyph tile). Wins over
/// <paramref name="ArtSource"/>.</param>
/// <param name="ArtSource">Optional artwork URI/key — the chip wraps it in a square, rounded, cover-fit image.</param>
/// <param name="Title">Primary line (ellipsized).</param>
/// <param name="Subtitle">Secondary line (ellipsized); omit for a one-line chip.</param>
/// <param name="Count">Number of items being dragged. ≥ 2 adds the corner count badge AND the stacked-card backdrop
/// (Apple's "flocking" look); 0/1 render a single card.</param>
/// <param name="Glyph">Optional leading glyph (Segoe Fluent) used when there is no artwork.</param>
public readonly record struct DragChipSpec(
    Element? Art = null,
    string? ArtSource = null,
    string? Title = null,
    string? Subtitle = null,
    int Count = 1,
    string? Glyph = null)
{
    /// <summary>"Nothing to show for this drag" — <see cref="DragChip.Resolve"/> maps it to a null preview.</summary>
    public static readonly DragChipSpec None = default;

    public bool IsEmpty => Art is null && string.IsNullOrEmpty(ArtSource)
                           && string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Subtitle)
                           && string.IsNullOrEmpty(Glyph);
}

/// <summary>
/// The framework-owned drag chip: ONE premiere preview every app gets by declaring data.
///
/// <code>
/// DragPreviewLayer.Of(DragChip.Resolve(state =&gt; state.Payload switch
/// {
///     TrackPayload p =&gt; new DragChipSpec(ArtSource: p.Art, Title: p.Name, Subtitle: p.Artist, Count: p.Tracks.Count),
///     _              =&gt; DragChipSpec.None,
/// }))
/// </code>
///
/// Rendering follows the researched target spec (Atlassian/Apple/dnd-kit): an OPAQUE compact card capped at
/// <see cref="MaxWidth"/> with a flyout-class shadow, at most three info pieces (art + title + subtitle) all
/// ellipsized, a top-trailing <see cref="InfoBadge"/> count for multi-drag over a two-card stacked backdrop, a ~4°
/// pickup tilt with a 1.02 scale (Trello) faded in by the declarative <c>Enter</c> transition, the target's
/// <see cref="DragState.Caption"/> as a trailing row, and an explicit not-allowed glyph whenever
/// <see cref="DragState.Refused"/> — a kind-compatible surface turned this payload away — so refusals are never silent
/// (hovering nothing at all stays silent, which is what keeps the glyph meaningful).
/// </summary>
public static class DragChip
{
    /// <summary>Card width cap (Atlassian's compact drag preview; a full-width row snapshot is the S1 failure).</summary>
    public const float MaxWidth = 280f;
    /// <summary>Leading artwork edge (square).</summary>
    public const float ArtSize = 40f;
    /// <summary>Pickup tilt in degrees (Trello's drag card).</summary>
    public const float TiltDeg = 4f;
    /// <summary>Pickup scale — the card reads as lifted off the page.</summary>
    public const float PickupScale = 1.02f;
    /// <summary>Per-card offset of the stacked backdrop shown for a multi-item drag.</summary>
    public const float StackOffset = 4f;
    /// <summary>Segoe Fluent "blocked" glyph for a refusal (WinUI's not-allowed drag cursor equivalent).</summary>
    public const string NotAllowedGlyph = "\uE733";   // Segoe Fluent "Blocked"

    /// <summary>Wrap a spec RESOLVER into the <c>Func&lt;DragState, Element?&gt;</c> a <see cref="DragPreviewLayer"/>
    /// consumes. A null / <see cref="DragChipSpec.None"/> spec renders nothing for that drag.</summary>
    public static Func<DragState, Element?> Resolve(Func<DragState, DragChipSpec?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return state =>
        {
            DragChipSpec? spec = resolver(state);
            if (spec is not { } s || s.IsEmpty) return null;
            return Render(s, state);
        };
    }

    /// <summary>Render the standard chip for <paramref name="spec"/> against the live <paramref name="state"/> (whose
    /// Caption and Effect supply the caption row and the not-allowed cue). Public so an app can embed the standard chip
    /// inside a bespoke preview instead of reimplementing it.</summary>
    public static Element Render(in DragChipSpec spec, in DragState state)
    {
        bool multi = spec.Count >= 2;
        // The cue fires on an EXPLICIT refusal (a kind-compatible target that said no), never on
        // <see cref="DropEffect.None"/> alone: that value also means "over empty space", and a glyph that shouts
        // "not allowed" at every gap between targets teaches the user to ignore it.
        bool refused = state.Refused;
        string? caption = state.Caption;

        // ── the card itself: opaque surface + flyout-class elevation, capped at MaxWidth ──
        var content = new System.Collections.Generic.List<Element>(3);
        Element? art = spec.Art
                       ?? (string.IsNullOrEmpty(spec.ArtSource)
                           ? (string.IsNullOrEmpty(spec.Glyph) ? null : GlyphTile(spec.Glyph!))
                           : new ImageEl
                           {
                               Source = spec.ArtSource!, Width = ArtSize, Height = ArtSize,
                               Corners = Radii.ControlAll, Fit = ImageFit.Cover, DecodePx = ArtSize * 2f,
                           });
        if (art is not null) content.Add(art);

        var lines = new System.Collections.Generic.List<Element>(3);
        if (!string.IsNullOrEmpty(spec.Title))
            lines.Add(new TextEl(spec.Title!) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 });
        if (!string.IsNullOrEmpty(spec.Subtitle))
            lines.Add(new TextEl(spec.Subtitle!) { Size = 12f, Color = Tok.TextSecondary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 });
        if (!string.IsNullOrEmpty(caption))
            lines.Add(new TextEl(caption!) { Size = 11f, Color = Tok.TextTertiary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 });
        if (lines.Count != 0)
            content.Add(new BoxEl { Direction = 1, Gap = 1f, Grow = 1f, Shrink = 1f, Justify = FlexJustify.Center, Children = lines.ToArray() });

        // Refusal cue: an explicit glyph, not silence (the "cannot drop in this mode" class of bugs).
        if (refused)
            content.Add(new TextEl(NotAllowedGlyph)
            {
                Size = 14f, FontFamily = Theme.IconFont, Color = Tok.SystemFillCritical, AlignSelf = FlexAlign.Center,
            });

        var card = new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(8f, 8f, 12f, 8f),
            MaxWidth = MaxWidth,
            Fill = Tok.FillSolidTertiary,                    // OPAQUE: a translucent chip is the S3 text-overdraw bug
            BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
            Corners = Radii.OverlayAll,
            Shadow = Elevation.Flyout,                       // the lifted-above-the-page band
            HitTestVisible = false,
            Children = content.ToArray(),
        };

        // ── multi-drag: two offset cards BEHIND the real one + a top-trailing count badge ──
        Element body = multi
            ? new BoxEl
            {
                ZStack = true, HitTestVisible = false,
                Children =
                [
                    StackCard(StackOffset * 2f),
                    StackCard(StackOffset),
                    card,
                    new BoxEl
                    {
                        // Corner child: the badge rides the CARD's top-trailing corner, so it can never drift onto the
                        // title the way a cursor-anchored badge did (screenshot S1).
                        Justify = FlexJustify.End, AlignItems = FlexAlign.Start, HitTestVisible = false,
                        Padding = new Edges4(0f, -6f, -6f, 0f),
                        Children = [InfoBadge.Count(spec.Count)],
                    },
                ],
            }
            : card;

        // Pickup: a constant tilt + scale on this node (Trello), with the pop faded/scaled in by the declarative Enter
        // transition on the wrapper — two nodes, because a static decomposed transform and an animated transform
        // channel cannot share one owner.
        var tilted = new BoxEl
        {
            Rotation = TiltDeg, ScaleX = PickupScale, ScaleY = PickupScale,
            HitTestVisible = false,
            Children = [body],
        };
        return new BoxEl
        {
            HitTestVisible = false,
            Enter = new EnterExit(Sx: 0.92f, Sy: 0.92f, Opacity: 0f, Active: true),
            Transition = MotionTok.ItemPlacement,
            Children = [tilted],
        };
    }

    /// <summary>One card of the stacked multi-drag backdrop: the card silhouette, offset down-right and dimmed.</summary>
    private static BoxEl StackCard(float offset) => new()
    {
        OffsetX = offset, OffsetY = offset,
        MinWidth = 96f, MinHeight = ArtSize + 16f,
        Fill = Tok.FillSolidSecondary,
        BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
        Corners = Radii.OverlayAll,
        Opacity = 0.85f,
        HitTestVisible = false,
    };

    private static BoxEl GlyphTile(string glyph) => new()
    {
        Width = ArtSize, Height = ArtSize, Corners = Radii.ControlAll,
        Fill = Tok.FillSubtleSecondary, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
    };
}
