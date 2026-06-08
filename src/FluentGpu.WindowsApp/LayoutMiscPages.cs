using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Pivot / NumberBox / AppBarToggleButton / CommandBar / Viewbox / ContentDialog demo pages (batch 4) ──────────

sealed class PivotPage : Component
{
    static readonly string[] Headers = { "All", "Recent", "Favorites" };
    public override Element Render() => GalleryPage.Shell("Pivot",
        "Presents content in a series of large, horizontally-arranged section headers.",
        ControlExample.Build("A Pivot",
            new BoxEl { Height = 220, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true, Children = [Pivot.Create(Headers)] }));
}

sealed class NumberBoxPage : Component
{
    public override Element Render() => GalleryPage.Shell("NumberBox",
        "A text control for numeric input, with up/down spin buttons.",
        ControlExample.Build("A NumberBox (spinner)", NumberBox.Create(0, 1)));
}

sealed class AppBarToggleButtonPage : Component
{
    public override Element Render() => GalleryPage.Shell("AppBarToggleButton",
        "A two-state command button for a CommandBar (e.g. Bold / Italic / Underline).",
        ControlExample.Build("AppBarToggleButtons",
            new BoxEl
            {
                Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
                BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    AppBarToggleButton.Create(Icons.Font, "Bold", true),
                    AppBarToggleButton.Create(Icons.Font, "Italic"),
                    AppBarToggleButton.Create(Icons.Font, "Underline"),
                ],
            }));
}

sealed class CommandBarPage : Component
{
    public override Element Render() => GalleryPage.Shell("CommandBar",
        "A toolbar for exposing common, frequently-used commands.",
        ControlExample.Build("A CommandBar", CommandBar.Create(new[]
        {
            new CommandBarButton(Icons.Accept, "Add", () => { }),
            new CommandBarButton(Icons.Tag, "Edit", () => { }),
            new CommandBarButton(Icons.Share, "Share", () => { }),
            new CommandBarButton(Icons.Cancel, "Delete", () => { }),
        })));
}

sealed class ViewboxPage : Component
{
    public override Element Render() => GalleryPage.Shell("Viewbox",
        "Scales its single child up or down to a target size, preserving aspect ratio.",
        ControlExample.Build("A Viewbox (1.8×)", Viewbox.Create(
            new BoxEl { Width = 80, Height = 40, Corners = Radii.ControlAll, Fill = Tok.AccentDefault, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [new TextEl("GPU") { Size = 16f, Bold = true, Color = Tok.TextOnAccentPrimary }] },
            scale: 1.8f)));
}

sealed class ContentDialogPage : Component
{
    public override Element Render() => GalleryPage.Shell("ContentDialog",
        "A modal dialog that shows contextual information and requires a response.",
        ControlExample.Build("A ContentDialog",
            ContentDialog.Create("Delete file", "Delete this file?", "This action can't be undone.", "Delete")));
}

sealed class TextOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Text",
        "Text input and display controls: NumberBox (TextBox, AutoSuggestBox, PasswordBox are in progress).");
}
