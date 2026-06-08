using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── ProgressRing / RelativePanel / VariableSizedWrapGrid / AnnotatedScrollBar / SwipeControl / MediaPlayerElement ──

sealed class ProgressRingPage : Component
{
    public override Element Render() => GalleryPage.Shell("ProgressRing",
        "A circular progress indicator — determinate (a known fraction) or indeterminate (ongoing).",
        ControlExample.Build("A determinate ProgressRing (70%)", ProgressRing.Determinate(0.7f), output: GalleryPage.LiveText(() => "70%")),
        ControlExample.Build("An indeterminate ProgressRing", ProgressRing.Indeterminate()));
}

sealed class RelativePanelPage : Component
{
    static Element Chip(string s) => new BoxEl { Padding = new Edges4(12, 8, 12, 8), Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Children = [new TextEl(s) { Size = 13f, Color = Tok.TextPrimary }] };
    public override Element Render() => GalleryPage.Shell("RelativePanel",
        "Positions child elements relative to the panel and to each other.",
        ControlExample.Build("A RelativePanel",
            new BoxEl { Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true, Children =
            [
                RelativePanel.Create(400, 160, new[]
                {
                    new RelativeChild(12, 12, Chip("Top-left")),
                    new RelativeChild(280, 12, Chip("Top-right")),
                    new RelativeChild(150, 70, Chip("Center")),
                    new RelativeChild(12, 118, Chip("Bottom")),
                }),
            ] }));
}

sealed class VariableSizedWrapGridPage : Component
{
    public override Element Render() => GalleryPage.Shell("VariableSizedWrapGrid",
        "A grid that wraps tiles of varying column/row spans.",
        ControlExample.Build("A VariableSizedWrapGrid", VariableSizedWrapGrid.Create(new[]
        {
            new WrapTile("1", 2, 1), new WrapTile("2", 1, 1), new WrapTile("3", 1, 1),
            new WrapTile("4", 1, 1), new WrapTile("5", 1, 1), new WrapTile("6", 2, 1),
        })));
}

sealed class AnnotatedScrollBarPage : Component
{
    public override Element Render() => GalleryPage.Shell("AnnotatedScrollBar",
        "A scrollbar enhanced with labels/annotations alongside the rail.",
        ControlExample.Build("An AnnotatedScrollBar", AnnotatedScrollBar.Create(new[]
        {
            ("A", 0.04f), ("F", 0.25f), ("M", 0.5f), ("S", 0.75f), ("Z", 0.96f),
        })));
}

sealed class SwipeControlPage : Component
{
    public override Element Render() => GalleryPage.Shell("SwipeControl",
        "Reveals contextual actions (e.g. archive, delete) by swiping a list item.",
        ControlExample.Build("A SwipeControl (actions revealed)", SwipeControl.Create("Quarterly report.docx", new[]
        {
            new SwipeAction(Icons.Accept, "Archive"),
            new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
        })));
}

sealed class MediaPlayerElementPage : Component
{
    public override Element Render() => GalleryPage.Shell("MediaPlayerElement",
        "Plays video and audio with built-in transport controls.",
        ControlExample.Build("A MediaPlayerElement", MediaPlayerElement.Create(480f)));
}
