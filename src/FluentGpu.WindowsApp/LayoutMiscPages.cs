using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Pivot / NumberBox / AppBarToggleButton / CommandBar / Viewbox / ContentDialog demo pages (batch 4) ──────────

[GalleryPage("Pivot", "Pivot", "Navigation")]
[Route("Pivot")]
sealed class PivotPage : Component
{
    static readonly string[] Headers = { "All", "Recent", "Favorites" };

    public override Element Render() => GalleryPage.Shell("Pivot",
        "Presents content in a series of large, horizontally-arranged section headers.",
        ControlExample.Build("A Pivot with text headers",
            new BoxEl { Height = 220, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true, Children = [Pivot.Create(Headers)] },
            description: "The control owns its selection: the selected header turns primary with the 3px accent pipe underneath, and the content region below swaps.",
            code: """
            static readonly string[] Headers = { "All", "Recent", "Favorites" };

            // Hosted in a fixed-height card so the content region has a bounded area:
            new BoxEl { Height = 220, Children = [Pivot.Create(Headers)] }
            """));
}

[GalleryPage("NumberBox", "NumberBox", "Text")]
[Route("NumberBox")]
sealed class NumberBoxPage : Component
{
    public override Element Render()
    {
        var plain = UseSignal(0.0);
        var expr = UseSignal(double.NaN);
        var inline = UseSignal(0.0);
        var compact = UseSignal(0.0);
        return GalleryPage.Shell("NumberBox",
            "A text control for numeric input with validation, expression evaluation and optional spin buttons. Spin buttons are hidden by default (WinUI); Inline places up/down repeat buttons at the trailing edge of the field, Compact opens them in a popup while the field is focused.",
            ControlExample.Build("A NumberBox (editable, no spin buttons — the WinUI default)",
                NumberBox.Create(value: plain, options: new NumberBox.NumberBoxOptions { PlaceholderText = "Enter a number" }),
                description: "Invalid input reverts on commit (Enter or blur); clearing the field commits NaN and shows the placeholder.",
                output: GalleryPage.LiveText(() => double.IsNaN(plain.Value) ? "—" : $"{plain.Value:0.##}"),
                code: """
                var value = UseSignal(0.0);   // caller-owned; NaN = cleared

                NumberBox.Create(value: value, options: new NumberBox.NumberBoxOptions { PlaceholderText = "Enter a number" })
                """),
            ControlExample.Build("A NumberBox that evaluates expressions",
                NumberBox.Create(value: expr, options: new NumberBox.NumberBoxOptions { AcceptsExpression = true, PlaceholderText = "1 + 2^2" }),
                description: "Type an arithmetic expression (+ - * / ^ and parentheses) and press Enter — it evaluates on commit.",
                output: GalleryPage.LiveText(() => double.IsNaN(expr.Value) ? "—" : $"{expr.Value:0.##}"),
                code: """
                var value = UseSignal(double.NaN);

                NumberBox.Create(value: value, options: new NumberBox.NumberBoxOptions { AcceptsExpression = true, PlaceholderText = "1 + 2^2" })
                """),
            ControlExample.Build("A NumberBox with inline spin buttons",
                NumberBox.Create(value: inline, options: new NumberBox.NumberBoxOptions
                    { Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline }),
                description: "A 0–100 range: spin buttons disable at the bounds. Up/Down step by SmallChange (1), PageUp/PageDown by LargeChange (10).",
                output: GalleryPage.LiveText(() => double.IsNaN(inline.Value) ? "—" : $"{inline.Value:0.##}"),
                code: """
                var value = UseSignal(0.0);

                NumberBox.Create(value: value, options: new NumberBox.NumberBoxOptions
                    { Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline })
                """),
            ControlExample.Build("A NumberBox with a header, range and compact spin buttons",
                NumberBox.Create(value: compact, options: new NumberBox.NumberBoxOptions
                    { Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                      Header = "Enter an integer:" }),
                description: "Compact mode shows the in-field indicator glyph and opens the up/down buttons in a popup while the field is focused.",
                output: GalleryPage.LiveText(() => double.IsNaN(compact.Value) ? "—" : $"{compact.Value:0.##}"),
                code: """
                var value = UseSignal(0.0);

                NumberBox.Create(value: value, options: new NumberBox.NumberBoxOptions
                    { Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                      Header = "Enter an integer:" })
                """));
    }
}

[GalleryPage("AppBarToggleButton", "AppBarToggleButton", "Menus & toolbars")]
[Route("AppBarToggleButton")]
sealed class AppBarToggleButtonPage : Component
{
    public override Element Render()
    {
        var bold = UseSignal(true);
        var italic = UseSignal(false);
        var under = UseSignal(false);
        return GalleryPage.Shell("AppBarToggleButton",
            "A two-state command button for a CommandBar (e.g. Bold / Italic / Underline).",
            ControlExample.Build("AppBarToggleButtons",
                new BoxEl
                {
                    Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
                    BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                    Children =
                    [
                        AppBarToggleButton.Create(Icons.Font, "Bold", isChecked: bold),
                        AppBarToggleButton.Create(Icons.Font, "Italic", isChecked: italic),
                        AppBarToggleButton.Create(Icons.Font, "Underline", isChecked: under),
                    ],
                },
                description: "Checked toggles paint the solid accent pill with the accent elevation border.",
                output: BodyStrong($"Bold {(bold.Value ? "on" : "off")} · Italic {(italic.Value ? "on" : "off")} · Underline {(under.Value ? "on" : "off")}"),
                code: """
                var bold = UseSignal(true);

                HStack(4,
                    AppBarToggleButton.Create(Icons.Font, "Bold", isChecked: bold),
                    AppBarToggleButton.Create(Icons.Font, "Italic"),
                    AppBarToggleButton.Create(Icons.Font, "Underline"))
                """),
            ControlExample.Build("Compact and disabled AppBarToggleButtons",
                HStack(4,
                    AppBarToggleButton.Create(Icons.Shuffle, "Shuffle", isCompact: true),
                    AppBarToggleButton.Create(Icons.Font, "Bold", isChecked: UseSignal(true), isEnabled: false),
                    AppBarToggleButton.Create(Icons.Font, "Italic", isEnabled: false)),
                description: "Compact is the closed-CommandBar 48px icon-only layout. A disabled checked toggle keeps the disabled-accent pill; a disabled unchecked one stays transparent.",
                code: """
                HStack(4,
                    AppBarToggleButton.Create(Icons.Shuffle, "Shuffle", isCompact: true),
                    AppBarToggleButton.Create(Icons.Font, "Bold", isChecked: UseSignal(true), isEnabled: false),
                    AppBarToggleButton.Create(Icons.Font, "Italic", isEnabled: false))
                """));
    }
}

[GalleryPage("CommandBar", "CommandBar", "Menus & toolbars")]
[Route("CommandBar")]
sealed class CommandBarPage : Component
{
    public override Element Render()
    {
        var (last, setLast) = UseState("—");
        var primary = new AppBarCommand[]
        {
            new(Icons.Add, "Add", () => setLast("Add")),
            new(Icons.Tag, "Edit", () => setLast("Edit")),
            new(Icons.Share, "Share", () => setLast("Share")),
            new(Icons.Cancel, "Delete", () => setLast("Delete")),
        };
        var secondary = new AppBarCommand[]
        {
            new(Icons.Settings, "Settings", () => setLast("Settings")),
            AppBarCommand.Separator,
            new(Icons.Copy, "Copy", () => setLast("Copy")) { AcceleratorText = "Ctrl+C" },
        };
        var minimalPrimary = new AppBarCommand[] { new(Icons.Add, "Add"), new(Icons.Refresh, "Refresh") };
        var minimalSecondary = new AppBarCommand[] { new(Icons.Settings, "Settings") };
        return GalleryPage.Shell("CommandBar",
            "A toolbar for exposing common, frequently-used commands — with a … More button that expands the bar and opens the secondary commands as an overflow menu.",
            ControlExample.Build("A CommandBar with primary and secondary commands",
                new BoxEl { Width = 440, Direction = 1, Children = [CommandBar.Create(primary, secondary)] },
                description: "The … button expands the closed compact bar to the full-size labeled layout and drops the secondary commands as an overflow menu.",
                output: BodyStrong($"Invoked: {last}"),
                code: """
                var (last, setLast) = UseState("—");
                var primary = new AppBarCommand[]
                {
                    new(Icons.Add, "Add", () => setLast("Add")),
                    new(Icons.Tag, "Edit", () => setLast("Edit")),
                    new(Icons.Share, "Share", () => setLast("Share")),
                    new(Icons.Cancel, "Delete", () => setLast("Delete")),
                };
                var secondary = new AppBarCommand[]
                {
                    new(Icons.Settings, "Settings", () => setLast("Settings")),
                    AppBarCommand.Separator,
                    new(Icons.Copy, "Copy", () => setLast("Copy")) { AcceleratorText = "Ctrl+C" },
                };

                CommandBar.Create(primary, secondary)
                """),
            ControlExample.Build("Minimal closed display mode",
                new BoxEl { Width = 440, Direction = 1, Children = [CommandBar.Create(minimalPrimary, minimalSecondary, closedDisplayMode: CommandBarDisplayMode.Minimal)] },
                description: "Closed, the bar is a 24px sliver showing only the … More button; opening it reveals the full-size labeled commands.",
                code: """
                var primary = new AppBarCommand[] { new(Icons.Add, "Add"), new(Icons.Refresh, "Refresh") };
                var secondary = new AppBarCommand[] { new(Icons.Settings, "Settings") };

                CommandBar.Create(primary, secondary,
                    closedDisplayMode: CommandBarDisplayMode.Minimal)

                // isSticky: true keeps the open bar from light-dismissing.
                """));
    }
}

[GalleryPage("Viewbox", "Viewbox", "Layout")]
[Route("Viewbox")]
sealed class ViewboxPage : Component
{
    public override Element Render() => GalleryPage.Shell("Viewbox",
        "Scales its single child up or down to a target size, preserving aspect ratio.",
        ControlExample.Build("A Viewbox (explicit 1.8× factor)", Viewbox.Create(Chip(), scale: 1.8f),
            code: """
            var chip = new BoxEl
            {
                Width = 80, Height = 40, Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl("GPU") { Size = 16f, Bold = true, Color = Tok.TextOnAccentPrimary }],
            };

            Viewbox.Create(chip, scale: 1.8f)
            """),
        ControlExample.Build("Stretch modes",
            HStack(12,
                StretchTile("Uniform (1.5×)", ViewboxStretch.Uniform),
                StretchTile("UniformToFill (2×, clips)", ViewboxStretch.UniformToFill),
                StretchTile("Fill (1.5× / 2×)", ViewboxStretch.Fill),
                StretchTile("None (1×)", ViewboxStretch.None)),
            description: "The WinUI-faithful overload computes the scale from the content size and a target box (the 80×40 chip in a 120×80 frame: scaleX 1.5, scaleY 2.0).",
            code: """
            // The chip is 80×40; the frame is 120×80 (scaleX 1.5, scaleY 2.0):
            //   Uniform → 1.5× (fit within) · UniformToFill → 2× (fills, clips)
            //   Fill → 1.5×/2× (independent axes) · None → 1×
            Viewbox.Create(chip, contentWidth: 80, contentHeight: 40,
                availableWidth: 120, availableHeight: 80, stretch: ViewboxStretch.Uniform)
            """),
        ControlExample.Build("Stretch direction",
            HStack(12,
                DirectionTile("DownOnly — shrinks to fit", ViewboxStretchDirection.DownOnly),
                DirectionTile("UpOnly — never shrinks", ViewboxStretchDirection.UpOnly)),
            description: "StretchDirection constrains which way the scale may go (the 80×40 chip in a 60×30 frame: Uniform scale 0.75).",
            code: """
            // content 80×40 in a 60×30 box → Uniform scale 0.75
            Viewbox.Create(chip, contentWidth: 80, contentHeight: 40,
                availableWidth: 60, availableHeight: 30,
                stretchDirection: ViewboxStretchDirection.DownOnly)   // shrink allowed

            Viewbox.Create(chip, 80, 40, 60, 30,
                stretchDirection: ViewboxStretchDirection.UpOnly)     // clamps at 1× — never shrinks
            """));

    static Element Chip() => new BoxEl
    {
        Width = 80, Height = 40, Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl("GPU") { Size = 16f, Bold = true, Color = Tok.TextOnAccentPrimary }],
    };

    static Element StretchTile(string label, ViewboxStretch stretch) => VStack(6,
        new BoxEl
        {
            Width = 120, Height = 80, Corners = Radii.ControlAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            ClipToBounds = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Viewbox.Create(Chip(), contentWidth: 80, contentHeight: 40, availableWidth: 120, availableHeight: 80, stretch: stretch)],
        },
        Caption(label));

    static Element DirectionTile(string label, ViewboxStretchDirection direction) => VStack(6,
        new BoxEl
        {
            Width = 60, Height = 30, Corners = Radii.ControlAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            ClipToBounds = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Viewbox.Create(Chip(), contentWidth: 80, contentHeight: 40, availableWidth: 60, availableHeight: 30, stretchDirection: direction)],
        },
        Caption(label));
}

[GalleryPage("ContentDialog", "ContentDialog", "Dialogs & flyouts")]
[Route("ContentDialog")]
sealed class ContentDialogPage : Component
{
    public override Element Render()
    {
        var (result, setResult) = UseState("—");
        return GalleryPage.Shell("ContentDialog",
            "A modal dialog that shows contextual information and requires a response.",
            ControlExample.Build("A ContentDialog",
                Embed.Comp(() => new ContentDialog
                {
                    TriggerLabel = "Show dialog",
                    Title = "Save your work?",
                    Message = "Lorem ipsum dolor sit amet, adipisicing elit.",
                    PrimaryText = "Save", SecondaryText = "Don't Save", CloseText = "Cancel",
                    Closed = r => setResult(r.ToString()),
                }),
                description: "Enter invokes the accent default (primary) button; Escape closes with ContentDialogResult.None; Tab cycles inside the dialog.",
                output: BodyStrong($"Result: {result}"),
                code: """
                // Shorthand factory (no result callback):
                ContentDialog.Create("Show dialog", "Save your work?",
                    "Lorem ipsum dolor sit amet, adipisicing elit.",
                    "Save", "Don't Save", "Cancel")

                // Full form — observe the WinUI ShowAsync result via Closed:
                var (result, setResult) = UseState("—");

                Embed.Comp(() => new ContentDialog
                {
                    TriggerLabel = "Show dialog",
                    Title = "Save your work?",
                    Message = "Lorem ipsum dolor sit amet, adipisicing elit.",
                    PrimaryText = "Save", SecondaryText = "Don't Save", CloseText = "Cancel",
                    Closed = r => setResult(r.ToString()),   // Primary / Secondary / None
                })
                """),
            ControlExample.Build("A ContentDialog with a single button",
                ContentDialog.Create("Show dialog", "No internet connection",
                    "Check your connection and try again.", "Close"),
                description: "With one command the button right-aligns in the command row (the WinUI single-button column layout).",
                code: """
                ContentDialog.Create("Show dialog", "No internet connection",
                    "Check your connection and try again.", "Close")
                """));
    }
}

[GalleryPage("text-cat", "Text", "Overview", Hidden = true)]
[Route("text-cat")]
sealed class TextOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Text", "Text input and display controls.",
            GalleryPage.CategoryGrid("Text", navigate));
    }
}
