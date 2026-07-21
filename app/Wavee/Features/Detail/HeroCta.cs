using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Shared prominent hero CTA skin. Artist and collection detail surfaces use this single primitive so their
/// Play/Shuffle geometry, motion, typography, and palette transitions cannot drift independently.</summary>
static class HeroCta
{
    public static Element Pill(string glyph, string label, ColorF fill, ColorF foreground, Action onClick,
                               bool balanced = false)
    {
        var pill = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(24f), Padding = new Edges4(22f, 12f, 22f, 12f),
            Fill = fill, BrushTransitionMs = 420f, Shadow = Elevation.Card,
            HoverScale = 1.04f, PressScale = 0.97f, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = onClick,
            Children =
            [
                Icon(glyph, 16f, foreground),
                new TextEl(label) { Size = 15f, Weight = 700, Color = foreground },
            ],
        };
        return balanced
            ? pill with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = 200f }
            : pill;
    }
}
