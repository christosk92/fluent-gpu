using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using System;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI AnnotatedScrollBar: a vertical scroll rail with text labels (annotations) positioned alongside it.
/// This is a self-contained visual demo (no live scrolling) — a tall track with an accent thumb, plus a column of
/// labels each placed at its normalized <c>Position01</c> along the rail height. Useful for jump-list / minimap style
/// navigation affordances where each annotation marks a section boundary on the scroll extent.
/// </summary>
public static class AnnotatedScrollBar
{
    private const float RailWidth = 12f;
    private const float TrackWidth = 4f;
    private const float TrackRadius = 4f;
    private const float ThumbWidth = 8f;
    private const float ThumbHeight = 48f;
    private const float ThumbRadius = 8f;
    private const float LabelsWidth = 120f;
    private const float LabelSize = 12f;

    /// <summary>
    /// Builds the annotated scrollbar. <paramref name="annotations"/> are (label, position) pairs where position is in
    /// 0..1 along the rail; <paramref name="height"/> is the rail height in DIPs.
    /// </summary>
    public static BoxEl Create(IReadOnlyList<(string Label, float Position01)> annotations, float height = 280f)
    {
        annotations ??= Array.Empty<(string, float)>();

        // Annotation labels, each absolutely placed in the ZStack column via OffsetY = pos * height.
        var labels = new Element[annotations.Count];
        for (int i = 0; i < annotations.Count; i++)
        {
            var (label, pos01) = annotations[i];
            float pos = pos01 < 0f ? 0f : pos01 > 1f ? 1f : pos01;
            labels[i] = new BoxEl   // OffsetY lives on BoxEl, not TextEl — wrap the label
            {
                OffsetY = pos * height,
                Children = [new TextEl(label ?? "") { Size = LabelSize, Color = Tok.TextSecondary }],
            };
        }

        return new BoxEl
        {
            Direction = 0,            // row: rail | annotations
            Gap = 8f,
            Height = height,
            Role = AutomationRole.ScrollBar,
            Children =
            [
                // The rail: track + thumb overlapping at the origin.
                new BoxEl
                {
                    Width = RailWidth,
                    Height = height,
                    ZStack = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = TrackWidth,
                            Height = height,
                            Corners = Radii.Circle(TrackRadius),
                            Fill = Tok.FillControlStrong,
                            OffsetX = 4f,
                        },
                        new BoxEl
                        {
                            Width = ThumbWidth,
                            Height = ThumbHeight,
                            Corners = Radii.Circle(ThumbRadius),
                            Fill = Tok.AccentDefault,
                            OffsetX = 2f,
                            OffsetY = height * 0.2f,
                        },
                    ],
                },
                // The annotations column: labels stacked, each placed by OffsetY.
                new BoxEl
                {
                    Direction = 1,
                    ZStack = true,
                    Width = LabelsWidth,
                    Height = height,
                    Children = labels,
                },
            ],
        };
    }
}
