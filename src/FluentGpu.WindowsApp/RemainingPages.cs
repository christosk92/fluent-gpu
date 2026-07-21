using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── ProgressRing / RelativePanel / VariableSizedWrapGrid / AnnotatedScrollBar / SwipeControl / MediaPlayerElement ──

[GalleryPage("ProgressRing", "ProgressRing", "Status & info")]
[Route("ProgressRing")]
sealed class ProgressRingPage : Component
{
    public override Element Render()
    {
        var active = UseSignal(true);
        var value = UseFloatSignal(0.7f);
        return GalleryPage.Shell("ProgressRing",
            "A circular progress indicator — determinate (a known fraction) or indeterminate (ongoing).",
            ControlExample.Build("An indeterminate ProgressRing",
                HStack(24, ProgressRing.Indeterminate(isActive: active.Value), ToggleSwitch.Create(active, onContent: "Working", offContent: "Do work")),
                output: BodyStrong(active.Value ? "Active" : "Inactive"),
                code: """
                var active = UseSignal(true);

                HStack(24,
                    ProgressRing.Indeterminate(isActive: active.Value),
                    ToggleSwitch.Create(active,
                        onContent: "Working", offContent: "Do work"))
                """),
            ControlExample.Build("A determinate ProgressRing",
                HStack(24, ProgressRing.Determinate(value.Value), Slider.Create(value, length: 200f)),
                output: BodyStrong($"{(int)(value.Value * 100)}%"),
                code: """
                var value = UseFloatSignal(0.7f);

                HStack(24,
                    ProgressRing.Determinate(value.Value),
                    Slider.Create(value, length: 200f))
                """),
            ControlExample.Build("A determinate ProgressRing with a visible track", ProgressRing.Determinate(0.7f, track: Tok.FillControlStrong),
                code: """
                // WinUI's default ring Background is transparent — pass a track color to show the full circle.
                ProgressRing.Determinate(0.7f, track: Tok.FillControlStrong)
                """),
            ControlExample.Build("A ProgressRing with a custom size", ProgressRing.Indeterminate(size: 64f),
                code: """
                ProgressRing.Indeterminate(size: 64f)
                """));
    }
}

[GalleryPage("RelativePanel", "RelativePanel", "Layout")]
[Route("RelativePanel")]
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
            ] },
            code: """
            // Children render at resolved (x, y) offsets within the panel, clipped to its bounds.
            // Chip(...) is any element — here a small bordered card with a label.
            RelativePanel.Create(400, 160, new[]
            {
                new RelativeChild(12, 12, Chip("Top-left")),
                new RelativeChild(280, 12, Chip("Top-right")),
                new RelativeChild(150, 70, Chip("Center")),
                new RelativeChild(12, 118, Chip("Bottom")),
            })
            """),
        ControlExample.Build("Overlap and z-order (later children draw on top)",
            new BoxEl { Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true, Children =
            [
                RelativePanel.Create(240, 120, new[]
                {
                    new RelativeChild(16, 14, Chip("1")),
                    new RelativeChild(40, 36, Chip("2")),
                    new RelativeChild(64, 58, Chip("3")),
                }),
            ] },
            code: """
            // The panel stacks children in declaration order — overlapping children layer back-to-front.
            RelativePanel.Create(240, 120, new[]
            {
                new RelativeChild(16, 14, Chip("1")),
                new RelativeChild(40, 36, Chip("2")),
                new RelativeChild(64, 58, Chip("3")),
            })
            """));
}

[GalleryPage("VariableSizedWrapGrid", "VariableSizedWrapGrid", "Layout")]
[Route("VariableSizedWrapGrid")]
sealed class VariableSizedWrapGridPage : Component
{
    public override Element Render() => GalleryPage.Shell("VariableSizedWrapGrid",
        "A grid that wraps tiles of varying column/row spans.",
        ControlExample.Build("A VariableSizedWrapGrid", VariableSizedWrapGrid.Create(new[]
        {
            new WrapTile("1", 2, 1), new WrapTile("2", 1, 1), new WrapTile("3", 1, 1),
            new WrapTile("4", 1, 1), new WrapTile("5", 1, 1), new WrapTile("6", 2, 1),
        }),
            code: """
            // Default: 60px base cells packed left-to-right into rows of 4 cells (by ColSpan sum).
            VariableSizedWrapGrid.Create(new[]
            {
                new WrapTile("1", 2, 1), new WrapTile("2", 1, 1), new WrapTile("3", 1, 1),
                new WrapTile("4", 1, 1), new WrapTile("5", 1, 1), new WrapTile("6", 2, 1),
            })
            """),
        ControlExample.Build("Row spans with a custom cell size and column count", VariableSizedWrapGrid.Create(new[]
        {
            new WrapTile("Tall", 1, 2), new WrapTile("Wide", 2, 1), new WrapTile("1x1", 1, 1),
            new WrapTile("1x1", 1, 1), new WrapTile("Big", 2, 2),
        }, cell: 72f, columns: 3),
            code: """
            // 72px base cells, rows wrap after 3 cells of width; spans are exact multiples of the cell.
            VariableSizedWrapGrid.Create(new[]
            {
                new WrapTile("Tall", 1, 2), new WrapTile("Wide", 2, 1), new WrapTile("1x1", 1, 1),
                new WrapTile("1x1", 1, 1), new WrapTile("Big", 2, 2),
            }, cell: 72f, columns: 3)
            """));
}

[GalleryPage("AnnotatedScrollBar", "AnnotatedScrollBar", "Scrolling")]
[Route("AnnotatedScrollBar")]
sealed class AnnotatedScrollBarPage : Component
{
    public override Element Render()
    {
        // The control is a NARROW (~44px-wide) rail (WinUI AnnotatedScrollBar.xaml:4 MinWidth = LabelsGridMinWidth 44):
        // host it in a fixed-height ROW at the right edge of a content stand-in so it hugs its natural width — never
        // let a stretching column blow it up to the page width.
        var pos = UseSignal(0.2f);
        return GalleryPage.Shell("AnnotatedScrollBar",
            "A scrollbar enhanced with labels/annotations alongside the rail.",
            ControlExample.Build("An AnnotatedScrollBar beside a content region (click, drag, or hold the buttons)",
                new BoxEl
                {
                    Direction = 0, Height = 280f, Gap = 12f,
                    Children =
                    [
                        new BoxEl { Width = 320f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },   // content stand-in
                        AnnotatedScrollBar.Create(new[]
                        {
                            ("A", 0.04f), ("F", 0.25f), ("M", 0.5f), ("S", 0.75f), ("Z", 0.96f),
                        }, pos, (to, _) => pos.Value = to, height: 280f,
                        detailLabel: p => p < 0.125f ? "Artists A–E" : p < 0.375f ? "Artists F–L" : p < 0.625f ? "Artists M–R" : p < 0.875f ? "Artists S–Y" : "Artists Z"),
                    ],
                },
                output: GalleryPage.LiveText(() => "position " + pos.Value.ToString("0.00")),
                code: """
                // The position signal is the live link to the scrolled content: writes move the thumb
                // compositor-instantly; onScroll receives every user scroll (click/drag/button).
                var pos = UseSignal(0.2f);

                AnnotatedScrollBar.Create(new[]
                {
                    ("A", 0.04f), ("F", 0.25f), ("M", 0.5f), ("S", 0.75f), ("Z", 0.96f),
                }, pos, (to, _) => pos.Value = to, height: 280f,
                detailLabel: p => p < 0.125f ? "Artists A–E" : p < 0.375f ? "Artists F–L"
                              : p < 0.625f ? "Artists M–R" : p < 0.875f ? "Artists S–Y" : "Artists Z")
                """));
    }
}

[GalleryPage("SwipeControl", "SwipeControl", "Menus & toolbars")]
[Route("SwipeControl")]
sealed class SwipeControlPage : Component
{
    public override Element Render() => GalleryPage.Shell("SwipeControl",
        "Reveals contextual actions (e.g. archive, delete) by swiping a list item.",
        ControlExample.Build("A SwipeControl with reveal items", SwipeControl.Create("Quarterly report.docx", new[]
        {
            new SwipeAction(Icons.Accept, "Archive"),
            new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
        }),
            description: "There is no live swipe gesture yet — the demo renders the content cell with its trailing actions already revealed.",
            code: """
            SwipeControl.Create("Quarterly report.docx", new[]
            {
                new SwipeAction(Icons.Accept, "Archive"),   // neutral reveal item
                new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
            })
            """),
        ControlExample.Build("A SwipeControl with a single execute item", SwipeControl.Create("Inbox — 14 unread", new[]
        {
            new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
        }),
            code: """
            // A colored action marks a destructive/execute item: bold fill + on-accent (white) content.
            SwipeControl.Create("Inbox — 14 unread", new[]
            {
                new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
            })
            """));
}

[GalleryPage("MediaPlayerElement", "MediaPlayerElement", "Media")]
[Route("MediaPlayerElement")]
sealed class MediaPlayerElementPage : Component
{
    public override Element Render()
    {
        // The real §4.3 control bound to a player. With no source it degrades to audio-only chrome (poster + transport);
        // the Desktop Video page drives a live MF clear-video surface through the same control.
        var player = UseMediaPlayer();
        return GalleryPage.Shell("MediaPlayerElement",
            "Plays video and audio with built-in transport controls.",
            ControlExample.Build("A MediaPlayerElement",
                new BoxEl { Width = 480f, Height = 300f, Children = [Embed.Comp(() => new FluentGpu.Controls.Media.MediaPlayerElement { Player = player })] },
                description: "The real MediaPlayerElement bound to a headless player. With no source it degrades to audio-only chrome; the Desktop Video page drives a live Media Foundation clear-video surface.",
                code: """
                var player = UseMediaPlayer();
                new MediaPlayerElement { Player = player }
                """));
    }
}
