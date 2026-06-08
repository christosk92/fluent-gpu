using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── TeachingTip / Popup / ItemsView / TextBlock / Border / AppBarSeparator demo pages (batch 5) ──────────

sealed class TeachingTipPage : Component
{
    public override Element Render() => GalleryPage.Shell("TeachingTip",
        "A non-modal, contextual callout that highlights a feature or teaches the user something.",
        ControlExample.Build("A TeachingTip",
            TeachingTip.Create("Show teaching tip", "Save your work", "Click the disk icon, or press Ctrl+S, to save your changes.")));
}

sealed class PopupPage : Component
{
    public override Element Render() => GalleryPage.Shell("Popup",
        "Displays content on top of existing content, shown and hidden programmatically.",
        ControlExample.Build("A Popup", Popup.Create("Show popup", "This content is displayed in a popup above the page.")));
}

sealed class ItemsViewPage : Component
{
    static readonly string[] Items = { "Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8" };
    public override Element Render() => GalleryPage.Shell("ItemsView",
        "A modern, selectable collection control — a flexible grid of items.",
        ControlExample.Build("An ItemsView", ItemsView.Create(Items, columns: 4)));
}

sealed class TextBlockPage : Component
{
    public override Element Render() => GalleryPage.Shell("TextBlock",
        "Displays read-only, formatted text — the foundation of the WinUI type ramp.",
        ControlExample.Build("The type ramp",
            VStack(8,
                TextBlocks.Title("Title"),
                TextBlocks.Subtitle("Subtitle"),
                TextBlocks.BodyStrong("Body strong"),
                TextBlocks.Body("Body — the default paragraph text used across the app."),
                TextBlocks.Caption("Caption — secondary metadata and timestamps."))));
}

sealed class BorderPage : Component
{
    public override Element Render() => GalleryPage.Shell("Border",
        "Draws a border, background, and rounded corners around a single child element.",
        ControlExample.Build("A Border",
            Border.Create(new TextEl("Content inside a Border") { Size = 14f, Color = Tok.TextPrimary }, cornerRadius: 8f, padding: 20f)));
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
            }));
}
