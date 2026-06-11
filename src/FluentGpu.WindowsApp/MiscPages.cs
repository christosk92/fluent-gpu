using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

// ── TeachingTip / Popup / ItemsView / TextBlock / Border / AppBarSeparator demo pages (batch 5) ──────────

sealed class TeachingTipPage : Component
{
    public override Element Render()
    {
        var (closed, setClosed) = UseState("—");
        return GalleryPage.Shell("TeachingTip",
            "A non-modal, contextual callout that highlights a feature or teaches the user something.",
            ControlExample.Build("A TeachingTip",
                TeachingTip.Create("Show teaching tip", "Save your work", "Click the disk icon, or press Ctrl+S, to save your changes."),
                description: "With no CloseButtonContent the close moves to the 40×40 header (alternate) close button; the tip is not light-dismiss — only the close button or Escape dismisses it.",
                code: """
                TeachingTip.Create("Show teaching tip", "Save your work",
                    "Click the disk icon, or press Ctrl+S, to save your changes.")
                """),
            ControlExample.Build("A TeachingTip with action and close buttons",
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with buttons",
                    Title = "Try filters",
                    Subtitle = "Narrow results quickly",
                    Body = "Use filters to reduce the list before opening a detail view.",
                    IconGlyph = Icons.Search,
                    ActionButtonContent = "Open filters",
                    ActionButtonIsAccent = true,
                    CloseButtonContent = "Got it",
                    Closed = r => setClosed(r.ToString()),
                }),
                description: "Setting CloseButtonContent moves the close into the footer next to the action button (the WinUI ButtonsStates split).",
                output: BodyStrong($"Closed: {closed}"),
                code: """
                var (closed, setClosed) = UseState("—");

                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with buttons",
                    Title = "Try filters",
                    Subtitle = "Narrow results quickly",
                    Body = "Use filters to reduce the list before opening a detail view.",
                    IconGlyph = Icons.Search,
                    ActionButtonContent = "Open filters",
                    ActionButtonIsAccent = true,
                    CloseButtonContent = "Got it",
                    Closed = r => setClosed(r.ToString()),
                })
                """),
            ControlExample.Build("Hero content and preferred placement",
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with hero content",
                    Title = "Saving automatically",
                    Subtitle = "Your changes sync as you work.",
                    PreferredPlacement = TeachingTip.PlacementMode.Bottom,
                    IsLightDismissEnabled = true,
                    HeroContent = new BoxEl
                    {
                        Grow = 1f, Height = 100f, Fill = Tok.AccentDefault,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.Picture, 36f).Foreground(Tok.TextOnAccentPrimary)],
                    },
                }),
                description: "HeroContent pins a full-bleed banner to the tip's edge; PreferredPlacement picks one of the 13 WinUI placements (the tail re-targets if the positioner flips). IsLightDismissEnabled lets a click outside dismiss it.",
                code: """
                Embed.Comp(() => new TeachingTip
                {
                    TriggerLabel = "Show tip with hero content",
                    Title = "Saving automatically",
                    Subtitle = "Your changes sync as you work.",
                    PreferredPlacement = TeachingTip.PlacementMode.Bottom,
                    IsLightDismissEnabled = true,
                    HeroContent = new BoxEl
                    {
                        Grow = 1f, Height = 100f, Fill = Tok.AccentDefault,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Icon(Icons.Picture, 36f).Foreground(Tok.TextOnAccentPrimary)],
                    },
                })
                """));
    }
}

sealed class PopupPage : Component
{
    public override Element Render() => GalleryPage.Shell("Popup",
        "Displays content on top of existing content, shown and hidden programmatically.",
        ControlExample.Build("A Popup", Popup.Create("Show popup", "This content is displayed in a popup above the page."),
            description: "Light-dismiss: click the trigger again, or anywhere outside the popup, to close it.",
            code: """
            Popup.Create("Show popup", "This content is displayed in a popup above the page.")
            """),
        ControlExample.Build("A windowed Popup",
            Embed.Comp(() => new Popup
            {
                TriggerLabel = "Show windowed popup",
                Text = "This popup rides its own top-level window and may escape the app window.",
                ShouldConstrainToRootBounds = false,
            }),
            description: "ShouldConstrainToRootBounds = false renders the popup in its own top-level window (the WinUI windowed-popup path); when the platform can't create popup windows it silently falls back to constrained placement.",
            code: """
            Embed.Comp(() => new Popup
            {
                TriggerLabel = "Show windowed popup",
                Text = "This popup rides its own top-level window and may escape the app window.",
                ShouldConstrainToRootBounds = false,
            })
            """));
}

sealed class ItemsViewPage : Component
{
    static readonly string[] Items = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
    static readonly float[] WallAspects = { 1.0f, 1.5f, 0.75f, 1.8f, 1.2f, 0.9f };

    public override Element Render()
    {
        var multi = UseMemo(static () => new SelectionModel());
        var wall = UseMemo(static () => new LinedFlowLayout(
            lineHeight: 72f, aspectRatio: i => WallAspects[i % WallAspects.Length], lineSpacing: 8f, minItemSpacing: 8f));
        var (invoked, setInvoked) = UseState("—");

        return GalleryPage.Shell("ItemsView",
            "A modern, selectable collection control — a flexible grid of items.",
            ControlExample.Build("An ItemsView", ItemsView.Create(Items, columns: 4),
                description: "Single selection (the default): click a tile or arrow-key between them; type to jump by prefix.",
                code: """
                static readonly string[] Items = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };

                ItemsView.Create(Items, columns: 4)
                """),
            ControlExample.Build("Multiple selection",
                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Items[i],
                    grow: 0f),
                description: "SelectionMode.Multiple slides in the ItemContainer checkbox; the SelectionModel stores the selected ranges (Ctrl+A selects all).",
                output: GalleryPage.LiveText(() => { _ = multi.Version.Value; return $"{multi.SelectedCount} selected"; }),
                code: """
                var multi = UseMemo(static () => new SelectionModel());

                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    selectionMode: ItemsSelectionMode.Multiple,
                    selection: multi,
                    itemText: i => Items[i],
                    grow: 0f)
                """),
            ControlExample.Build("Item invocation",
                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    selectionMode: ItemsSelectionMode.None,
                    isItemInvokedEnabled: true,
                    itemInvoked: i => setInvoked(Items[i]),
                    itemText: i => Items[i],
                    grow: 0f),
                description: "With SelectionMode.None and IsItemInvokedEnabled, a tap raises ItemInvoked instead of selecting (the WinUI CanRaiseItemInvoked matrix).",
                output: BodyStrong($"Invoked: {invoked}"),
                code: """
                var (invoked, setInvoked) = UseState("—");

                ItemsView.Create(Items.Length, i => Tile(Items[i]), RepeatLayout.Grid(4, 80f, 8f),
                    selectionMode: ItemsSelectionMode.None,
                    isItemInvokedEnabled: true,
                    itemInvoked: i => setInvoked(Items[i]),
                    itemText: i => Items[i],
                    grow: 0f)
                """),
            ControlExample.Build("The photo wall (LinedFlowLayout)",
                ItemsView.Create(24, WallTile, RepeatLayout.Custom(wall),
                    itemText: i => "Photo " + (i + 1),
                    grow: 0f),
                description: "The WinUI LinedFlowLayout: items flow into uniform-height lines, each item's width = aspect ratio × line height — the gallery photo wall. The layout is stateful, so it is hoisted with UseMemo.",
                code: """
                static readonly float[] WallAspects = { 1.0f, 1.5f, 0.75f, 1.8f, 1.2f, 0.9f };
                var wall = UseMemo(static () => new LinedFlowLayout(
                    lineHeight: 72f, aspectRatio: i => WallAspects[i % WallAspects.Length], lineSpacing: 8f, minItemSpacing: 8f));

                ItemsView.Create(24, WallTile, RepeatLayout.Custom(wall),
                    itemText: i => "Photo " + (i + 1),
                    grow: 0f)
                """));
    }

    static Element Tile(string label) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary }],
    };

    static Element WallTile(int i) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = Tok.FillCardSecondary, Corners = Radii.ControlAll,
        Children = [new TextEl("Photo " + (i + 1)) { Size = 12f, Color = Tok.TextSecondary }],
    };
}

sealed class TextBlockPage : Component
{
    const string LongText = "Text wrapping flows this sentence onto as many lines as its 320 epx column needs, breaking between words exactly like a WinUI TextBlock.";

    public override Element Render() => GalleryPage.Shell("TextBlock",
        "Displays read-only, formatted text — the foundation of the WinUI type ramp.",
        ControlExample.Build("A simple TextBlock", TextBlocks.Body("I am a TextBlock."),
            code: """
            TextBlocks.Body("I am a TextBlock.")
            """),
        ControlExample.Build("The type ramp",
            VStack(8,
                TextBlocks.Title("Title"),
                TextBlocks.Subtitle("Subtitle"),
                TextBlocks.BodyStrong("Body strong"),
                TextBlocks.Body("Body — the default paragraph text used across the app."),
                TextBlocks.Caption("Caption — secondary metadata and timestamps.")),
            code: """
            VStack(8,
                TextBlocks.Title("Title"),
                TextBlocks.Subtitle("Subtitle"),
                TextBlocks.BodyStrong("Body strong"),
                TextBlocks.Body("Body — the default paragraph text used across the app."),
                TextBlocks.Caption("Caption — secondary metadata and timestamps."))
            """),
        ControlExample.Build("A customized TextBlock",
            VStack(8,
                new TextEl("I am a styled TextBlock.") { Size = 18f, Bold = true, Color = Tok.AccentDefault },
                new TextEl("Underlined for emphasis.") { Size = 14f, Underline = true, Color = Tok.TextPrimary },
                new TextEl("No longer relevant.") { Size = 14f, Strikethrough = true, Color = Tok.TextSecondary },
                new TextEl("A monospace code run.") { Size = 14f, FontFamily = "Cascadia Code", Color = Tok.TextPrimary }),
            code: """
            VStack(8,
                new TextEl("I am a styled TextBlock.") { Size = 18f, Bold = true, Color = Tok.AccentDefault },
                new TextEl("Underlined for emphasis.") { Size = 14f, Underline = true, Color = Tok.TextPrimary },
                new TextEl("No longer relevant.") { Size = 14f, Strikethrough = true, Color = Tok.TextSecondary },
                new TextEl("A monospace code run.") { Size = 14f, FontFamily = "Cascadia Code", Color = Tok.TextPrimary })
            """),
        ControlExample.Build("Text wrapping and trimming",
            VStack(12,
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap }] },
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }] }),
            description: "The first block wraps freely; MaxLines = 2 clamps the second to two lines.",
            code: """
            const string LongText = "Text wrapping flows this sentence onto as many lines as its 320 epx column needs, breaking between words exactly like a WinUI TextBlock.";

            VStack(12,
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap }] },
                new BoxEl { Width = 320f, Children = [new TextEl(LongText) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }] })
            """));
}

sealed class BorderPage : Component
{
    public override Element Render()
    {
        var (t, setT) = UseState(0.4f);
        float thickness = 1f + MathF.Round(t * 5f);

        return GalleryPage.Shell("Border",
            "Draws a border, background, and rounded corners around a single child element.",
            ControlExample.Build("A Border",
                Border.Create(new TextEl("Content inside a Border") { Size = 14f, Color = Tok.TextPrimary }, cornerRadius: 8f, padding: 20f),
                code: """
                Border.Create(new TextEl("Content inside a Border") { Size = 14f, Color = Tok.TextPrimary },
                    cornerRadius: 8f, padding: 20f)
                """),
            ControlExample.Build("Border thickness and color",
                Border.Create(new TextEl("Content inside an accent Border") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: thickness, cornerRadius: 8f, borderColor: Tok.AccentDefault, padding: 20f),
                options: Slider.Create(t, setT, 200f, header: "BorderThickness"),
                output: BodyStrong($"{thickness:0} epx"),
                code: """
                var (t, setT) = UseState(0.4f);
                float thickness = 1f + MathF.Round(t * 5f);

                Border.Create(new TextEl("Content inside an accent Border") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: thickness, cornerRadius: 8f, borderColor: Tok.AccentDefault, padding: 20f)
                """),
            ControlExample.Build("A background with no border",
                Border.Create(new TextEl("Background only") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: 0f, cornerRadius: 8f, background: Tok.FillSubtleSecondary, padding: 20f),
                code: """
                Border.Create(new TextEl("Background only") { Size = 14f, Color = Tok.TextPrimary },
                    borderWidth: 0f, cornerRadius: 8f, background: Tok.FillSubtleSecondary, padding: 20f)
                """));
    }
}

sealed class AppBarSeparatorPage : Component
{
    public override Element Render() => GalleryPage.Shell("AppBarSeparator",
        "A thin vertical divider that separates groups of commands in a CommandBar.",
        ControlExample.Build("Commands with a separator",
            new BoxEl
            {
                Direction = 0, Gap = 4, AlignItems = FlexAlign.Center, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    AppBarButton.Create(Icons.Accept, "Add", () => { }),
                    AppBarButton.Create(Icons.Tag, "Edit", () => { }),
                    AppBarSeparator.Create(),
                    AppBarButton.Create(Icons.Cancel, "Delete", () => { }),
                ],
            },
            description: "The default (FullSize/Compact) orientation: a 1px vertical line stretching the bar height with the 2,8,2,8 inset.",
            code: """
            HStack(4,
                AppBarButton.Create(Icons.Accept, "Add", () => { }),
                AppBarButton.Create(Icons.Tag, "Edit", () => { }),
                AppBarSeparator.Create(),
                AppBarButton.Create(Icons.Cancel, "Delete", () => { }))
            """),
        ControlExample.Build("The Overflow state",
            new BoxEl
            {
                Direction = 1, Width = 220f, Padding = Edges4.All(4), Corners = Radii.OverlayAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    AppBarButton.CreateOverflow(new AppBarCommand(Icons.Copy, "Copy") { AcceleratorText = "Ctrl+C" }, hasToggles: false, hasIcons: true, onInvoke: () => { }),
                    AppBarButton.CreateOverflow(new AppBarCommand(Icons.Tag, "Rename"), hasToggles: false, hasIcons: true, onInvoke: () => { }),
                    AppBarSeparator.Create(overflow: true),
                    AppBarButton.CreateOverflow(new AppBarCommand(Icons.Cancel, "Delete"), hasToggles: false, hasIcons: true, onInvoke: () => { }),
                ],
            },
            description: "In the CommandBar overflow menu the separator flips horizontal (the Overflow visual state): full-width, 1px tall, margin 0,4,0,4.",
            code: """
            VStack(0,
                AppBarButton.CreateOverflow(new AppBarCommand(Icons.Copy, "Copy") { AcceleratorText = "Ctrl+C" }, hasToggles: false, hasIcons: true, onInvoke: () => { }),
                AppBarButton.CreateOverflow(new AppBarCommand(Icons.Tag, "Rename"), hasToggles: false, hasIcons: true, onInvoke: () => { }),
                AppBarSeparator.Create(overflow: true),
                AppBarButton.CreateOverflow(new AppBarCommand(Icons.Cancel, "Delete"), hasToggles: false, hasIcons: true, onInvoke: () => { }))
            """));
}
