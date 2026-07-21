using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── RichTextBlock / Canvas / ToolTip / CommandBarFlyout demo pages ─────────────────

[GalleryPage("RichTextBlock", "RichTextBlock", "Text", Icon = Icons.Font)]
sealed class RichTextBlockPage : Component
{
    static readonly string[] Paras =
    {
        "FluentGpu is a from-scratch, GPU-rendered UI engine for .NET — the React/Reactor "
            + "programming model over a custom Direct3D 12 renderer.",
        "Controls are immutable element records composed by stateless factories and components, "
            + "reconciled into a retained scene and recorded to a POD draw-list.",
        "This RichTextBlock is a column of body paragraphs — read-only, formatted text laid out "
            + "by the engine's flex layout.",
    };

    static readonly string[] Plain =
    {
        "RichTextBlock displays read-only formatted text as a column of paragraphs.",
        "Each paragraph wraps inside a 560px column with a 10px gap between paragraphs.",
    };

    public override Element Render() => GalleryPage.Shell("RichTextBlock",
        "Displays read-only rich text with multiple paragraphs and inline formatting.",
        ExampleCard.Build("An article", RichTextBlock.Article("About fluent-gpu", Paras),
            description: "Article prepends a bold 20px heading to the paragraph column.",
            code: """
            static readonly string[] Paras =
            {
                "FluentGpu is a from-scratch, GPU-rendered UI engine for .NET — the React/Reactor "
                    + "programming model over a custom Direct3D 12 renderer.",
                "Controls are immutable element records composed by stateless factories and components, "
                    + "reconciled into a retained scene and recorded to a POD draw-list.",
                "This RichTextBlock is a column of body paragraphs — read-only, formatted text laid out "
                    + "by the engine's flex layout.",
            };

            RichTextBlock.Article("About fluent-gpu", Paras)
            """),
        ExampleCard.Build("A plain paragraph block", RichTextBlock.Create(Plain),
            description: "Create lays out body paragraphs only — 14px primary text, 10px between paragraphs, capped at 560px wide.",
            code: """
            var paras = new[]
            {
                "RichTextBlock displays read-only formatted text as a column of paragraphs.",
                "Each paragraph wraps inside a 560px column with a 10px gap between paragraphs.",
            };

            RichTextBlock.Create(paras)
            """));
}

[GalleryPage("Canvas", "Canvas", "Layout", Icon = Icons.Grid)]
sealed class CanvasPage : Component
{
    static Element Dot(ColorF c) => new BoxEl { Width = 48, Height = 48, Corners = Radii.Circle(48), Fill = c };
    static Element Square(ColorF c) => new BoxEl { Width = 56, Height = 56, Corners = Radii.ControlAll, Fill = c };

    // Display chrome only — a 1px card border that makes the canvas bounds (and its clip) visible.
    static Element Frame(Element canvas) => new BoxEl
    {
        Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children = [canvas],
    };

    public override Element Render() => GalleryPage.Shell("Canvas",
        "A panel that positions its children by explicit X/Y coordinates.",
        ExampleCard.Build("A Canvas",
            Frame(Canvas.Create(380, 180, new[]
            {
                new CanvasChild(20, 20, Dot(Tok.AccentDefault)),
                new CanvasChild(130, 80, Dot(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
                new CanvasChild(260, 30, Dot(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
                new CanvasChild(90, 120, Dot(ColorF.FromRgba(0xFC, 0xE1, 0x00))),
            })),
            description: "Each child is placed at an absolute (X, Y) offset from the canvas's top-left corner; children are clipped to the canvas bounds.",
            code: """
            static Element Dot(ColorF c) => new BoxEl { Width = 48, Height = 48, Corners = Radii.Circle(48), Fill = c };

            Canvas.Create(380, 180, new[]
            {
                new CanvasChild(20, 20, Dot(Tok.AccentDefault)),
                new CanvasChild(130, 80, Dot(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
                new CanvasChild(260, 30, Dot(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
                new CanvasChild(90, 120, Dot(ColorF.FromRgba(0xFC, 0xE1, 0x00))),
            })
            """),
        ExampleCard.Build("Overlapping children (paint order)",
            Frame(Canvas.Create(220, 152, new[]
            {
                new CanvasChild(20, 20, Square(Tok.AccentDefault)),
                new CanvasChild(56, 48, Square(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
                new CanvasChild(92, 76, Square(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
            })),
            description: "Children paint in list order — later children draw on top of earlier ones. There is no Canvas.ZIndex; reorder the list to change stacking.",
            code: """
            static Element Square(ColorF c) => new BoxEl { Width = 56, Height = 56, Corners = Radii.ControlAll, Fill = c };

            // Later children paint on top of earlier ones.
            Canvas.Create(220, 152, new[]
            {
                new CanvasChild(20, 20, Square(Tok.AccentDefault)),
                new CanvasChild(56, 48, Square(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
                new CanvasChild(92, 76, Square(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
            })
            """));
}

[GalleryPage("ToolTip", "ToolTip", "Status & info", Icon = Icons.Document)]
sealed class ToolTipPage : Component
{
    static BoxEl Chip(string label) => new()
    {
        Padding = new Edges4(11, 6, 11, 6), Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [new TextEl(label) { Size = 14f, Color = Tok.TextPrimary }],
    };

    public override Element Render()
    {
        var (clicks, setClicks) = UseState(0);
        return GalleryPage.Shell("ToolTip",
            "A short description shown in a small popup, anchored to its target.",
            ExampleCard.Build("A ToolTip (hover the target)",
                ToolTip.Wrap(Chip("Hover over me"), "I am a ToolTip with helpful information."),
                description: "Hovering the target opens the bubble after the show delay; moving the pointer away closes it.",
                code: """
                static BoxEl Chip(string label) => new()
                {
                    Padding = new Edges4(11, 6, 11, 6), Corners = Radii.ControlAll,
                    Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                    Children = [new TextEl(label) { Size = 14f, Color = Tok.TextPrimary }],
                };

                ToolTip.Wrap(Chip("Hover over me"), "I am a ToolTip with helpful information.")
                """),
            ExampleCard.Build("A button with a ToolTip",
                ToolTip.Wrap(Button.Standard("Save", () => setClicks(clicks + 1)), "Saves the current document."),
                description: "The wrapper adds no click handler of its own — the wrapped button stays fully interactive.",
                output: BodyStrong($"Clicks: {clicks}"),
                code: """
                var (clicks, setClicks) = UseState(0);

                ToolTip.Wrap(Button.Standard("Save", () => setClicks(clicks + 1)), "Saves the current document.")
                """),
            ExampleCard.Build("Mouse placement",
                Embed.Comp(() => new ToolTip
                {
                    Target = Chip("Hover over me (mouse placement)"),
                    Text = "I opened at the pointer position.",
                    Placement = ToolTipPlacementMode.Mouse,
                }),
                description: "PlacementMode.Mouse opens the bubble at the pointer position (11px below the cursor) instead of centered above the target.",
                code: """
                Embed.Comp(() => new ToolTip
                {
                    Target = Chip("Hover over me (mouse placement)"),
                    Text = "I opened at the pointer position.",
                    Placement = ToolTipPlacementMode.Mouse,
                })
                """));
    }
}

[GalleryPage("CommandBarFlyout", "CommandBarFlyout", "Menus & toolbars", Icon = Icons.More)]
sealed class CommandBarFlyoutPage : Component
{
    // Each example owns its own output state, so the command sets are built per example around its reporter.
    static (AppBarCommand[] Primary, AppBarCommand[] Secondary) Commands(Action<string> report) =>
    (
        new AppBarCommand[]
        {
            new(Icons.Accept, "Accept", () => report("Accept")),
            new(Icons.Share, "Share", () => report("Share")),
            new(Icons.Tag, "Tag", () => report("Tag")),
        },
        new AppBarCommand[]
        {
            new(Icons.Settings, "Settings", () => report("Settings")),
            AppBarCommand.Separator,
            new(Icons.Accept, "Show grid", () => report("Show grid"),
                Kind: AppBarCommandKind.ToggleButton, IsChecked: true),
        }
    );

    public override Element Render()
    {
        var (last1, setLast1) = UseState("—");
        var (last2, setLast2) = UseState("—");
        var (p1, s1) = Commands(setLast1);
        var (p2, s2) = Commands(setLast2);
        return GalleryPage.Shell("CommandBarFlyout",
            "A contextual toolbar of commands, shown in a flyout — a primary icon row plus a … More button that "
            + "expands a secondary overflow menu of labeled rows.",
            ExampleCard.Build("A CommandBarFlyout", CommandBarFlyout.Create("Commands", p1, s1),
                output: BodyStrong($"Invoked: {last1}"),
                code: """
                var primary = new AppBarCommand[]
                {
                    new(Icons.Accept, "Accept", () => setLast("Accept")),
                    new(Icons.Share, "Share", () => setLast("Share")),
                    new(Icons.Tag, "Tag", () => setLast("Tag")),
                };
                var secondary = new AppBarCommand[]
                {
                    new(Icons.Settings, "Settings", () => setLast("Settings")),
                    AppBarCommand.Separator,
                    new(Icons.Accept, "Show grid", () => setLast("Show grid"),
                        Kind: AppBarCommandKind.ToggleButton, IsChecked: true),
                };

                CommandBarFlyout.Create("Commands", primary, secondary)
                """),
            ExampleCard.Build("An always-expanded CommandBarFlyout",
                CommandBarFlyout.Create("Commands (always expanded)", p2, s2, alwaysExpanded: true),
                description: "AlwaysExpanded keeps the secondary overflow menu open and hides the … More button.",
                output: BodyStrong($"Invoked: {last2}"),
                code: """
                // WinUI V2 AlwaysExpanded: the overflow menu stays open and the … More button is hidden.
                CommandBarFlyout.Create("Commands (always expanded)", primary, secondary, alwaysExpanded: true)
                """));
    }
}
