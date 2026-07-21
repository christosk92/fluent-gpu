using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── ProgressRing / RelativePanel / VariableSizedWrapGrid / AnnotatedScrollBar / SwipeControl / MediaPlayerElement ──

[GalleryPage("ProgressRing", "ProgressRing", "Status & info", Icon = Icons.Refresh)]
sealed partial class ProgressRingPage : Component
{
    static readonly Signal<bool> _active = new(true);
    static readonly FloatSignal _value = new(0.7f);

    public override Element Render() => GalleryPage.Shell("ProgressRing",
        "A circular progress indicator — determinate (a known fraction) or indeterminate (ongoing).",
        ExampleCard.Show(IndeterminateSample),
        ExampleCard.Show(DeterminateSample),
        ExampleCard.Show(DeterminateTrackSample),
        ExampleCard.Show(CustomSizeSample));

    [Sample("An indeterminate ProgressRing")]
    static Element Indeterminate() => VStack(8,
        HStack(24,
            ProgressRing.Indeterminate(isActive: _active.Value),
            ToggleSwitch.Create(_active, onContent: "Working", offContent: "Do work")),
        GalleryPage.LiveText(() => _active.Value ? "Active" : "Inactive"));

    [Sample("A determinate ProgressRing")]
    static Element Determinate() => VStack(8,
        HStack(24,
            ProgressRing.Determinate(_value.Value),
            Slider.Create(_value, length: 200f)),
        GalleryPage.LiveText(() => $"{(int)(_value.Value * 100)}%"));

    [Sample("A determinate ProgressRing with a visible track")]
    static Element DeterminateTrack()
    {
        // WinUI's default ring Background is transparent — pass a track color to show the full circle.
        return ProgressRing.Determinate(0.7f, track: Tok.FillControlStrong);
    }

    [Sample("A ProgressRing with a custom size")]
    static Element CustomSize() => ProgressRing.Indeterminate(size: 64f);
}

[GalleryPage("RelativePanel", "RelativePanel", "Layout", Icon = Icons.Grid)]
sealed partial class RelativePanelPage : Component
{
    static Element Chip(string s) => new BoxEl { Padding = new Edges4(12, 8, 12, 8), Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Children = [new TextEl(s) { Size = 13f, Color = Tok.TextPrimary }] };

    public override Element Render() => GalleryPage.Shell("RelativePanel",
        "Positions child elements relative to the panel and to each other.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(OverlapSample));

    [Sample("A RelativePanel")]
    static Element Basic() => new BoxEl
    {
        Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children =
        [
            // Children render at resolved (x, y) offsets within the panel, clipped to its bounds.
            // Chip(...) is any element — here a small bordered card with a label.
            RelativePanel.Create(400, 160, new[]
            {
                new RelativeChild(12, 12, Chip("Top-left")),
                new RelativeChild(280, 12, Chip("Top-right")),
                new RelativeChild(150, 70, Chip("Center")),
                new RelativeChild(12, 118, Chip("Bottom")),
            }),
        ],
    };

    [Sample("Overlap and z-order (later children draw on top)")]
    static Element Overlap() => new BoxEl
    {
        Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children =
        [
            // The panel stacks children in declaration order — overlapping children layer back-to-front.
            RelativePanel.Create(240, 120, new[]
            {
                new RelativeChild(16, 14, Chip("1")),
                new RelativeChild(40, 36, Chip("2")),
                new RelativeChild(64, 58, Chip("3")),
            }),
        ],
    };
}

[GalleryPage("VariableSizedWrapGrid", "VariableSizedWrapGrid", "Layout", Icon = Icons.Grid)]
sealed partial class VariableSizedWrapGridPage : Component
{
    public override Element Render() => GalleryPage.Shell("VariableSizedWrapGrid",
        "A grid that wraps tiles of varying column/row spans.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(SpansSample));

    [Sample("A VariableSizedWrapGrid")]
    static Element Basic()
    {
        // Default: 60px base cells packed left-to-right into rows of 4 cells (by ColSpan sum).
        return VariableSizedWrapGrid.Create(new[]
        {
            new WrapTile("1", 2, 1), new WrapTile("2", 1, 1), new WrapTile("3", 1, 1),
            new WrapTile("4", 1, 1), new WrapTile("5", 1, 1), new WrapTile("6", 2, 1),
        });
    }

    [Sample("Row spans with a custom cell size and column count")]
    static Element Spans()
    {
        // 72px base cells, rows wrap after 3 cells of width; spans are exact multiples of the cell.
        return VariableSizedWrapGrid.Create(new[]
        {
            new WrapTile("Tall", 1, 2), new WrapTile("Wide", 2, 1), new WrapTile("1x1", 1, 1),
            new WrapTile("1x1", 1, 1), new WrapTile("Big", 2, 2),
        }, cell: 72f, columns: 3);
    }
}

[GalleryPage("AnnotatedScrollBar", "AnnotatedScrollBar", "Scrolling", Icon = Icons.More)]
sealed partial class AnnotatedScrollBarPage : Component
{
    // The control is a NARROW (~44px-wide) rail (WinUI AnnotatedScrollBar.xaml:4 MinWidth = LabelsGridMinWidth 44):
    // host it in a fixed-height ROW at the right edge of a content stand-in so it hugs its natural width — never
    // let a stretching column blow it up to the page width.
    static readonly Signal<float> _pos = new(0.2f);

    public override Element Render() => GalleryPage.Shell("AnnotatedScrollBar",
        "A scrollbar enhanced with labels/annotations alongside the rail.",
        ExampleCard.Show(BesideContentSample));

    [Sample("An AnnotatedScrollBar beside a content region (click, drag, or hold the buttons)")]
    static Element BesideContent() => VStack(8,
        new BoxEl
        {
            Direction = 0, Height = 280f, Gap = 12f,
            Children =
            [
                new BoxEl { Width = 320f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },   // content stand-in
                // The position signal is the live link to the scrolled content: writes move the thumb
                // compositor-instantly; onScroll receives every user scroll (click/drag/button).
                AnnotatedScrollBar.Create(new[]
                {
                    ("A", 0.04f), ("F", 0.25f), ("M", 0.5f), ("S", 0.75f), ("Z", 0.96f),
                }, _pos, (to, _) => _pos.Value = to, height: 280f,
                detailLabel: p => p < 0.125f ? "Artists A–E" : p < 0.375f ? "Artists F–L"
                              : p < 0.625f ? "Artists M–R" : p < 0.875f ? "Artists S–Y" : "Artists Z"),
            ],
        },
        GalleryPage.LiveText(() => "position " + _pos.Value.ToString("0.00")));
}

[GalleryPage("SwipeControl", "SwipeControl", "Menus & toolbars", Icon = Icons.More)]
sealed partial class SwipeControlPage : Component
{
    public override Element Render() => GalleryPage.Shell("SwipeControl",
        "Reveals contextual actions (e.g. archive, delete) by swiping a list item.",
        ExampleCard.Show(RevealItemsSample),
        ExampleCard.Show(ExecuteItemSample));

    [Sample("A SwipeControl with reveal items", Description = "There is no live swipe gesture yet — the demo renders the content cell with its trailing actions already revealed.")]
    static Element RevealItems() => SwipeControl.Create("Quarterly report.docx", new[]
    {
        new SwipeAction(Icons.Accept, "Archive"),   // neutral reveal item
        new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
    });

    [Sample("A SwipeControl with a single execute item")]
    static Element ExecuteItem()
    {
        // A colored action marks a destructive/execute item: bold fill + on-accent (white) content.
        return SwipeControl.Create("Inbox — 14 unread", new[]
        {
            new SwipeAction(Icons.Cancel, "Delete", ColorF.FromRgba(0xC4, 0x2B, 0x1C)),
        });
    }
}

[GalleryPage("MediaPlayerElement", "MediaPlayerElement", "Media", Icon = Icons.Movie, ShotMode = ShotMode.Skip)]
sealed class MediaPlayerElementPage : Component
{
    public override Element Render()
    {
        // The real §4.3 control bound to a player. With no source it degrades to audio-only chrome (poster + transport);
        // the Desktop Video page drives a live MF clear-video surface through the same control.
        var player = UseMediaPlayer();
        return GalleryPage.Shell("MediaPlayerElement",
            "Plays video and audio with built-in transport controls.",
            // sample-drift-risk: the live element needs UseMediaPlayer() — a component-lifetime hook that owns the
            // MediaPlayer for the page's lifetime; a static [Sample] factory cannot create/own a player, so this stays
            // ExampleCard.Build with a hand-written code string.
            ExampleCard.Build("A MediaPlayerElement",
                new BoxEl { Width = 480f, Height = 300f, Children = [Embed.Comp(() => new FluentGpu.Controls.Media.MediaPlayerElement { Player = player })] },
                description: "The real MediaPlayerElement bound to a headless player. With no source it degrades to audio-only chrome; the Desktop Video page drives a live Media Foundation clear-video surface.",
                code: """
                var player = UseMediaPlayer();
                new MediaPlayerElement { Player = player }
                """));
    }
}
