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
    const int ItemCount = 9_000;
    const float RowExtent = 44f;
    const float SampleHeight = 320f;

    // One identity-stable controller is the entire two-way seam: ItemsView publishes its live geometry while the
    // annotated rail sends absolute/relative requests back to that same viewport.
    static readonly AnnotatedScrollBarController _scroll = new();
    static readonly AnnotatedScrollBarLabel[] _labels =
    [
        new(0f, "A"),
        new(1_800f * RowExtent, "F"),
        new(3_600f * RowExtent, "M"),
        new(5_400f * RowExtent, "S"),
        new(7_200f * RowExtent, "Z"),
    ];
    static readonly float[] _ticks = BuildTicks();

    public override Element Render() => GalleryPage.Shell("AnnotatedScrollBar",
        "A scrollbar enhanced with labels/annotations alongside the rail.",
        ExampleCard.Show(BesideContentSample));

    [Sample("An AnnotatedScrollBar controlling a 9,000-row virtual list", Description = "The list publishes live scroll geometry through IScrollController. Drag or click the rail, hover for the detail flag, click ticks, or use the buttons and keyboard.")]
    static Element BesideContent() => VStack(Spacing.M,
        new BoxEl
        {
            Direction = 0,
            Height = SampleHeight,
            Gap = Spacing.M,
            MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f,
                    Basis = 0f,
                    MinWidth = 0f,
                    Height = SampleHeight,
                    Corners = Radii.ControlAll,
                    ClipToBounds = true,
                    Fill = Tok.FillCardSecondary,
                    Children =
                    [
                        ItemsView.Create(ItemCount, Row, RepeatLayout.Stack(RowExtent), new ListOptions
                        {
                            Grow = 1f,
                            ItemText = static i => "Library item " + (i + 1),
                            Scroll = new ScrollOptions
                            {
                                VerticalScrollController = _scroll,
                                SuppressScrollBar = true,
                            },
                        }),
                    ],
                },
                AnnotatedScrollBar.Create(_scroll, new AnnotatedScrollBarOptions
                {
                    Labels = _labels,
                    TickOffsets = _ticks,
                    DetailLabelAtOffset = static offset =>
                    {
                        int index = Math.Clamp((int)(offset / RowExtent), 0, ItemCount - 1);
                        return new AnnotatedScrollBarLabel(index * RowExtent, "Library item " + (index + 1));
                    },
                    Height = SampleHeight,
                }),
            ],
        },
        GalleryPage.LiveText(() =>
            $"offset {_scroll.Offset.Value:0} / {_scroll.MaximumOffset.Value:0}   viewport {_scroll.ViewportLength.Value:0}"));

    static Element Row(int index) => new BoxEl
    {
        Height = RowExtent,
        Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Fill = (index & 1) == 0 ? Tok.FillCardDefault : Tok.FillCardSecondary,
        BorderColor = Tok.StrokeDividerDefault,
        BorderWidth = 1f,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl("Library item " + (index + 1))
            {
                Size = 14f,
                Color = Tok.TextPrimary,
            },
        ],
    };

    static float[] BuildTicks()
    {
        const int stride = 225;
        var ticks = new float[(ItemCount + stride - 1) / stride];
        for (int i = 0; i < ticks.Length; i++) ticks[i] = i * stride * RowExtent;
        return ticks;
    }
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
