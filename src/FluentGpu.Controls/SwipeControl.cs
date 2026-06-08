using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using FluentGpu.Scene;
using System;
using System.Collections.Generic;

namespace FluentGpu.Controls;

/// <summary>One revealed swipe action: an icon glyph, a label, and an optional background colour. WinUI default
/// swipe items are neutral (a subtle control fill with primary text); only an "execute"/destructive item (e.g. Delete)
/// supplies an accent/red <paramref name="Color"/>, which then also flips its content to on-accent (white) text.</summary>
public sealed record SwipeAction(string Glyph, string Label, ColorF? Color = null);

/// <summary>A WinUI SwipeControl: a list item that reveals action buttons on swipe. This self-contained gallery demo has
/// no live gesture — it renders the content cell with the swipe actions already revealed on the trailing (right) edge,
/// so the gallery shows exactly what a swipe would expose.</summary>
public static class SwipeControl
{
    public static BoxEl Create(string content, IReadOnlyList<SwipeAction> actions)
    {
        var children = new List<Element>(actions.Count + 1)
        {
            new BoxEl
            {
                Grow = 1f,
                Padding = new Edges4(16, 14, 16, 14),
                Fill = Tok.FillCardDefault,
                Children = [new TextEl(content) { Size = 14f, Color = Tok.TextPrimary }],
            },
        };

        foreach (var action in actions)
        {
            // Default item: neutral control fill + primary text. A supplied colour marks a destructive/execute item,
            // whose content flips to on-accent (white) text against the bold fill.
            bool destructive = action.Color is not null;
            ColorF fill = action.Color ?? Tok.FillControlTertiary;
            ColorF text = destructive ? Tok.TextOnAccentPrimary : Tok.TextPrimary;
            // WinUI SwipeItemStyle (AppBarButton): MinWidth=68, MinHeight=40, NO corner radius. Pressed state fill =
            // ControlAltFillColorQuaternary (mapped to Tok.FillControlAltQuaternary) — only for the neutral item; a
            // destructive/execute item keeps its bold fill across states.
            children.Add(new BoxEl
            {
                Width = 68f,
                Height = 40f,
                Direction = 1,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Gap = 4f,
                Fill = fill,
                PressedFill = destructive ? fill : Tok.FillControlAltQuaternary,
                Role = AutomationRole.Button,
                Children =
                [
                    new TextEl(action.Glyph) { Size = 16f, Color = text, FontFamily = Theme.IconFont },
                    new TextEl(action.Label) { Size = 12f, Color = text },
                ],
            });
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Stretch,
            Corners = Radii.ControlAll,
            ClipToBounds = true,
            BorderColor = Tok.StrokeCardDefault,
            BorderWidth = 1f,
            Children = children.ToArray(),
        };
    }
}
