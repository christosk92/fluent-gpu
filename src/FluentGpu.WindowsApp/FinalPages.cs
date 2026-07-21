using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── RichTextBlock / Canvas / ToolTip / CommandBarFlyout demo pages ─────────────────

[GalleryPage("RichTextBlock", "RichTextBlock", "Text", Icon = Icons.Font)]
sealed partial class RichTextBlockPage : Component
{
    public override Element Render() => GalleryPage.Shell("RichTextBlock",
        "Displays read-only rich text with multiple paragraphs and inline formatting.",
        ExampleCard.Show(ArticleSample),
        ExampleCard.Show(PlainBlockSample));

    [Sample("An article", Description = "Article prepends a bold 20px heading to the paragraph column.")]
    static Element Article()
    {
        string[] paras =
        {
            "FluentGpu is a from-scratch, GPU-rendered UI engine for .NET — the React/Reactor "
                + "programming model over a custom Direct3D 12 renderer.",
            "Controls are immutable element records composed by stateless factories and components, "
                + "reconciled into a retained scene and recorded to a POD draw-list.",
            "This RichTextBlock is a column of body paragraphs — read-only, formatted text laid out "
                + "by the engine's flex layout.",
        };
        return RichTextBlock.Article("About fluent-gpu", paras);
    }

    [Sample("A plain paragraph block", Description = "Create lays out body paragraphs only — 14px primary text, 10px between paragraphs, capped at 560px wide.")]
    static Element PlainBlock()
    {
        var paras = new[]
        {
            "RichTextBlock displays read-only formatted text as a column of paragraphs.",
            "Each paragraph wraps inside a 560px column with a 10px gap between paragraphs.",
        };
        return RichTextBlock.Create(paras);
    }
}

[GalleryPage("Canvas", "Canvas", "Layout", Icon = Icons.Grid)]
sealed partial class CanvasPage : Component
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
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(OverlapSample));

    [Sample("A Canvas", Description = "Each child is placed at an absolute (X, Y) offset from the canvas's top-left corner; children are clipped to the canvas bounds.")]
    static Element Basic()
    {
        // static Element Dot(ColorF c) => new BoxEl { Width = 48, Height = 48, Corners = Radii.Circle(48), Fill = c };
        return Frame(Canvas.Create(380, 180, new[]
        {
            new CanvasChild(20, 20, Dot(Tok.AccentDefault)),
            new CanvasChild(130, 80, Dot(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
            new CanvasChild(260, 30, Dot(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
            new CanvasChild(90, 120, Dot(ColorF.FromRgba(0xFC, 0xE1, 0x00))),
        }));
    }

    [Sample("Overlapping children (paint order)", Description = "Children paint in list order — later children draw on top of earlier ones. There is no Canvas.ZIndex; reorder the list to change stacking.")]
    static Element Overlap()
    {
        // Later children paint on top of earlier ones.
        return Frame(Canvas.Create(220, 152, new[]
        {
            new CanvasChild(20, 20, Square(Tok.AccentDefault)),
            new CanvasChild(56, 48, Square(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
            new CanvasChild(92, 76, Square(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
        }));
    }
}

[GalleryPage("ToolTip", "ToolTip", "Status & info", Icon = Icons.Document)]
sealed partial class ToolTipPage : Component
{
    static readonly Signal<int> _clicks = new(0);

    static BoxEl Chip(string label) => new()
    {
        Padding = new Edges4(11, 6, 11, 6), Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [new TextEl(label) { Size = 14f, Color = Tok.TextPrimary }],
    };

    public override Element Render() => GalleryPage.Shell("ToolTip",
        "A short description shown in a small popup, anchored to its target.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(OnButtonSample),
        ExampleCard.Show(MousePlacementSample));

    [Sample("A ToolTip (hover the target)", Description = "Hovering the target opens the bubble after the show delay; moving the pointer away closes it.")]
    static Element Basic()
        => ToolTip.Wrap(Chip("Hover over me"), "I am a ToolTip with helpful information.");

    [Sample("A button with a ToolTip", Description = "The wrapper adds no click handler of its own — the wrapped button stays fully interactive.")]
    static Element OnButton() => VStack(8,
        ToolTip.Wrap(Button.Standard("Save", () => _clicks.Value++), "Saves the current document."),
        GalleryPage.LiveText(() => $"Clicks: {_clicks.Value}"));

    [Sample("Mouse placement", Description = "PlacementMode.Mouse opens the bubble at the pointer position (11px below the cursor) instead of centered above the target.")]
    static Element MousePlacement() => Embed.Comp(() => new ToolTip
    {
        Target = Chip("Hover over me (mouse placement)"),
        Text = "I opened at the pointer position.",
        Placement = ToolTipPlacementMode.Mouse,
    });
}

[GalleryPage("CommandBarFlyout", "CommandBarFlyout", "Menus & toolbars", Icon = Icons.More)]
sealed partial class CommandBarFlyoutPage : Component
{
    static readonly Signal<string> _last1 = new("—");
    static readonly Signal<string> _last2 = new("—");

    public override Element Render() => GalleryPage.Shell("CommandBarFlyout",
        "A contextual toolbar of commands, shown in a flyout — a primary icon row plus a … More button that "
        + "expands a secondary overflow menu of labeled rows.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(AlwaysExpandedSample));

    [Sample("A CommandBarFlyout")]
    static Element Basic()
    {
        var primary = new AppBarCommand[]
        {
            new(Icons.Accept, "Accept", () => _last1.Value = "Accept"),
            new(Icons.Share, "Share", () => _last1.Value = "Share"),
            new(Icons.Tag, "Tag", () => _last1.Value = "Tag"),
        };
        var secondary = new AppBarCommand[]
        {
            new(Icons.Settings, "Settings", () => _last1.Value = "Settings"),
            AppBarCommand.Separator,
            new(Icons.Accept, "Show grid", () => _last1.Value = "Show grid",
                Kind: AppBarCommandKind.ToggleButton, IsChecked: true),
        };
        return VStack(8,
            CommandBarFlyout.Create("Commands", primary, secondary),
            GalleryPage.LiveText(() => $"Invoked: {_last1.Value}"));
    }

    [Sample("An always-expanded CommandBarFlyout", Description = "AlwaysExpanded keeps the secondary overflow menu open and hides the … More button.")]
    static Element AlwaysExpanded()
    {
        var primary = new AppBarCommand[]
        {
            new(Icons.Accept, "Accept", () => _last2.Value = "Accept"),
            new(Icons.Share, "Share", () => _last2.Value = "Share"),
            new(Icons.Tag, "Tag", () => _last2.Value = "Tag"),
        };
        var secondary = new AppBarCommand[]
        {
            new(Icons.Settings, "Settings", () => _last2.Value = "Settings"),
            AppBarCommand.Separator,
            new(Icons.Accept, "Show grid", () => _last2.Value = "Show grid",
                Kind: AppBarCommandKind.ToggleButton, IsChecked: true),
        };
        // WinUI V2 AlwaysExpanded: the overflow menu stays open and the … More button is hidden.
        return VStack(8,
            CommandBarFlyout.Create("Commands (always expanded)", primary, secondary, alwaysExpanded: true),
            GalleryPage.LiveText(() => $"Invoked: {_last2.Value}"));
    }
}
