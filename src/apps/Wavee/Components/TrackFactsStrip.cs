using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The expanded row's DETAILS STRIP: a compact bento of everything the track carries, drawn above the
/// versions section inside the drawer.
///
/// <para>WHY A BENTO AND NOT A LABEL/VALUE GRID. Both were on the table. A two-column grid reads beautifully at 400
/// DIP and turns into a ragged column with an acre of dead space beside it at 1400 — and this strip lives inside a
/// list whose width is the whole point (the table above it is dropping lanes as it narrows). A wrap-grow row of small
/// tiles reflows over the SAME range with no arm: <c>FlexLayout.ArrangeWrap</c> fills each line edge to edge, so nine
/// facts land as one row when the list is wide, three rows when the queue pane is open, and every tile keeps its label
/// glued to its value at both. It is also the shape the app already uses for facts
/// (<c>DetailTrailing.CompactStatTile</c> / the artist profile tiles), one step denser.</para>
///
/// <para>The value ramp is deliberately NOT the release panel's 18/800 display figure: half of these values are prose
/// ("Saturday, 28 September 2024 15:41", an album name, an ISRC), and an 18px single-line tile ellipsised every one of
/// them. 13.5/600 over an 11px label wraps to two lines and still reads as the loud half of the pair.</para>
///
/// <para>Motion: each tile is KEYED BY ITS FACT and fades up; the row staggers them left to right on the house
/// <see cref="WaveeMotion.MastheadStaggerMs"/> cadence, and reduced motion is a VALUE (the stagger reads 0), never a
/// branch that changes what is authored. The tiles also FLIP, so a fact that lands late — kind 222 tempo, kind 185
/// plays — reflows its neighbours smoothly instead of snapping them narrower.</para></summary>
internal static class TrackFactsStrip
{
    /// <summary>Tile geometry. Content-width base + <c>Grow</c> so the wrap-grow row fills its line: a long value keeps
    /// its own width and a short one is stretched to close the line, rather than every tile being forced to the same
    /// <c>Basis=0</c> share and the long ones ellipsising to pay for it.</summary>
    const float LabelSize = 11f;
    const float ValueSize = 13.5f;
    const float ChipHeight = 24f;

    static readonly LayoutTransition TileReflow = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        SizeMode.Reveal);

    /// <summary>Build the strip, or an empty box when the track states nothing at all (a shimmer row, an empty slot).
    ///
    /// <paramref name="go"/> routes the ONE link this strip carries (the album), through the same
    /// <c>RichText.RouteForUri</c> table the row's own album lane and metadata subline use — an episode row's "album"
    /// is its SHOW, and the table is what keeps those two from disagreeing.</summary>
    internal static Element Build(Track track, TrackFactsOptions options, Action<string, string?> go)
    {
        var facts = TrackExpandedFacts.For(track, options);
        if (facts.Count == 0) return new BoxEl();

        var tiles = new List<Element>(facts.Count);
        var chips = new List<Element>(4);

        for (int i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            switch (f.Form)
            {
                case TrackFactForm.Chips:
                    if (f.Chips is { Count: > 0 } tags)
                        for (int t = 0; t < tags.Count; t++)
                            chips.Add(Chip("tag:" + tags[t], tags[t], null, accent: true));
                    break;
                case TrackFactForm.Flag:
                    chips.Add(Chip("flag:" + f.Kind, Loc.Get(TrackExpandedFacts.LabelKey(f.Kind)), FlagGlyph(f.Kind),
                                   accent: false));
                    break;
                case TrackFactForm.Link:
                    tiles.Add(Tile(f.Kind, LinkValue(f, go), pending: false));
                    break;
                default:
                    tiles.Add(Tile(f.Kind, PlainValue(f), pending: f.Form == TrackFactForm.Pending));
                    break;
            }
        }

        // Reduced motion as a VALUE, the house idiom — the authored tree is identical either way.
        float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;
        // One eyebrow per section. The tiles carry their own labels, so this is not there to name the values — it is
        // there so the drawer reads as TWO sections (what this track is, then what forms it comes in) rather than as a
        // wall of tiles that a "Versions and formats" caption appears to head from underneath.
        var body = new List<Element>(3)
        {
            WaveeType.Eyebrow(Loc.Get(Strings.Detail.TrackFacts.Title)) with
            {
                Key = "facts-head", Color = Tok.TextTertiary, Margin = new Edges4(0f, 0f, 0f, 2f),
            },
        };
        if (tiles.Count > 0)
            body.Add(new BoxEl
            {
                Key = "facts-tiles", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                Stagger = stagger, Children = tiles.ToArray(),
            });
        if (chips.Count > 0)
            body.Add(new BoxEl
            {
                Key = "facts-chips", Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                Stagger = stagger, Children = chips.ToArray(),
            });

        return new BoxEl
        {
            Key = "facts", Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
            Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.S),
            Children = body.ToArray(),
        };
    }

    /// <summary>One fact tile: an 11px tertiary label over its value. Keyed by the fact KIND (not by its value), so a
    /// value that changes cross-swaps in place while a fact that ARRIVES fades up as a new tile.</summary>
    static Element Tile(TrackFactKind kind, Element value, bool pending) => new BoxEl
    {
        Key = "fact:" + kind, Enter = DetailRail.FadeUp, Layout = TileReflow,
        Direction = 1, Gap = 1f, Grow = 1f, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = CornerRadius4.All(Radii.Control),
        Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(Loc.Get(TrackExpandedFacts.LabelKey(kind)))
            {
                Size = LabelSize, Weight = 600, CharSpacing = WaveeType.EyebrowTracking,
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            value,
        ],
        // The pending dash is a state, not a value: dimming the whole tile is what tells the reader "we asked and are
        // still waiting" without spending a second line of copy on it.
        Opacity = pending ? 0.6f : 1f,
    };

    static Element PlainValue(in TrackFact f) => new TextEl(f.Value)
    {
        Size = ValueSize, Weight = 600,
        Color = f.Form == TrackFactForm.Pending ? Tok.TextTertiary : Tok.TextPrimary,
        // Two lines, wrapped: an exact "Saturday, 28 September 2024 15:41" is the whole reason this strip exists, and
        // a one-line tile would ellipsise the year off it at every narrow width.
        MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>The album value as a hyperlink span — the existing <c>RichText</c>/<c>SpanTextEl</c> idiom, identical
    /// to <c>TrackRow.AlbumLink</c>, so the strip's album and the row's album lane open the same page.</summary>
    static Element LinkValue(in TrackFact f, Action<string, string?> go)
    {
        string name = f.Value;
        Action? open = null;
        if (RichText.RouteForUri(f.LinkUri) is { } route)
            open = () => go(route, name.Length > 0 ? name : null);
        return new SpanTextEl([new TextSpan(name, OnClick: open)])
        {
            Size = ValueSize, Weight = 600, Color = Tok.TextPrimary,
            MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>A descriptor tag or a flag, as the app's existing pill: the <c>ContentFilterChips</c> shape at the
    /// filter bar's unselected weight. Descriptors take the accent-subtle fill the Liked lens pills use (they are the
    /// SAME concepts those pills filter by); a flag stays neutral, because it is a property of the recording rather
    /// than a concept you can pivot on.</summary>
    static Element Chip(string key, string label, string? glyph, bool accent)
    {
        var kids = new List<Element>(2);
        if (glyph is { Length: > 0 }) kids.Add(Icon(glyph, 11f, Tok.TextSecondary));
        kids.Add(new TextEl(label)
        {
            Size = 12f, Weight = 600, Color = Tok.TextSecondary,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        });
        return new BoxEl
        {
            Key = key, Enter = DetailRail.FadeUp, Layout = TileReflow,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS,
            Height = ChipHeight, Shrink = 1f, MinWidth = 0f,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = CornerRadius4.All(999f),
            Fill = accent ? Tok.AccentSubtle : Tok.FillControlDefault,
            BorderWidth = 1f,
            BorderColor = accent ? Tok.AccentSecondary : Tok.StrokeControlDefault,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The flag's leading glyph. Explicit gets none: its label IS the mark, and the row above already carries
    /// the E badge — a second icon for the same fact is noise.</summary>
    static string? FlagGlyph(TrackFactKind kind) => kind switch
    {
        TrackFactKind.Video => Icons.Movie,
        TrackFactKind.LocalFile => Icons.Folder,
        TrackFactKind.Unavailable => Icons.Info,
        _ => null,
    };
}
