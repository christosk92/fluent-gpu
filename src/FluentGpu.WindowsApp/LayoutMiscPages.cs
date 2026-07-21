using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Pivot / NumberBox / AppBarToggleButton / CommandBar / Viewbox / ContentDialog demo pages (batch 4) ──────────

[GalleryPage("Pivot", "Pivot", "Navigation", Icon = Icons.Document)]
sealed partial class PivotPage : Component
{
    static readonly string[] Headers = { "All", "Recent", "Favorites" };

    public override Element Render() => GalleryPage.Shell("Pivot",
        "Presents content in a series of large, horizontally-arranged section headers.",
        ExampleCard.Show(TextHeadersSample));

    [Sample("A Pivot with text headers", Description = "The control owns its selection: the selected header turns primary with the 3px accent pipe underneath, and the content region below swaps.")]
    static Element TextHeaders() => new BoxEl
    {
        // Hosted in a fixed-height card so the content region has a bounded area:
        Height = 220, Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
        Children = [Pivot.Create(Headers)],
    };
}

[GalleryPage("NumberBox", "NumberBox", "Text", Icon = Icons.Volume)]
sealed partial class NumberBoxPage : Component
{
    static readonly Signal<double> _plain = new(0.0);
    static readonly Signal<double> _expr = new(double.NaN);
    static readonly Signal<double> _inline = new(0.0);
    static readonly Signal<double> _compact = new(0.0);

    public override Element Render() => GalleryPage.Shell("NumberBox",
        "A text control for numeric input with validation, expression evaluation and optional spin buttons. Spin buttons are hidden by default (WinUI); Inline places up/down repeat buttons at the trailing edge of the field, Compact opens them in a popup while the field is focused.",
        ExampleCard.Show(PlainSample),
        ExampleCard.Show(ExpressionSample),
        ExampleCard.Show(InlineSpinSample),
        ExampleCard.Show(CompactSpinSample));

    [Sample("A NumberBox (editable, no spin buttons — the WinUI default)", Description = "Invalid input reverts on commit (Enter or blur); clearing the field commits NaN and shows the placeholder.")]
    static Element Plain() => VStack(8,
        NumberBox.Create(value: _plain, options: new NumberBox.NumberBoxOptions { PlaceholderText = "Enter a number" }),
        GalleryPage.LiveText(() => double.IsNaN(_plain.Value) ? "—" : $"{_plain.Value:0.##}"));

    [Sample("A NumberBox that evaluates expressions", Description = "Type an arithmetic expression (+ - * / ^ and parentheses) and press Enter — it evaluates on commit.")]
    static Element Expression() => VStack(8,
        NumberBox.Create(value: _expr, options: new NumberBox.NumberBoxOptions { AcceptsExpression = true, PlaceholderText = "1 + 2^2" }),
        GalleryPage.LiveText(() => double.IsNaN(_expr.Value) ? "—" : $"{_expr.Value:0.##}"));

    [Sample("A NumberBox with inline spin buttons", Description = "A 0–100 range: spin buttons disable at the bounds. Up/Down step by SmallChange (1), PageUp/PageDown by LargeChange (10).")]
    static Element InlineSpin() => VStack(8,
        NumberBox.Create(value: _inline, options: new NumberBox.NumberBoxOptions
            { Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline }),
        GalleryPage.LiveText(() => double.IsNaN(_inline.Value) ? "—" : $"{_inline.Value:0.##}"));

    [Sample("A NumberBox with a header, range and compact spin buttons", Description = "Compact mode shows the in-field indicator glyph and opens the up/down buttons in a popup while the field is focused.")]
    static Element CompactSpin() => VStack(8,
        NumberBox.Create(value: _compact, options: new NumberBox.NumberBoxOptions
            { Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
              Header = "Enter an integer:" }),
        GalleryPage.LiveText(() => double.IsNaN(_compact.Value) ? "—" : $"{_compact.Value:0.##}"));
}

[GalleryPage("AppBarToggleButton", "AppBarToggleButton", "Menus & toolbars", Icon = Icons.Accept)]
sealed partial class AppBarToggleButtonPage : Component
{
    static readonly Signal<bool> _bold = new(true);
    static readonly Signal<bool> _italic = new(false);
    static readonly Signal<bool> _under = new(false);
    static readonly Signal<bool> _disabledOn = new(true);

    public override Element Render() => GalleryPage.Shell("AppBarToggleButton",
        "A two-state command button for a CommandBar (e.g. Bold / Italic / Underline).",
        ExampleCard.Show(ToggleButtonsSample),
        ExampleCard.Show(CompactDisabledSample));

    [Sample("AppBarToggleButtons", Description = "Checked toggles paint the solid accent pill with the accent elevation border.")]
    static Element ToggleButtons() => VStack(8,
        new BoxEl
        {
            Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
            BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Children =
            [
                AppBarToggleButton.Create(Icons.Font, "Bold", isChecked: _bold),
                AppBarToggleButton.Create(Icons.Font, "Italic", isChecked: _italic),
                AppBarToggleButton.Create(Icons.Font, "Underline", isChecked: _under),
            ],
        },
        GalleryPage.LiveText(() => $"Bold {(_bold.Value ? "on" : "off")} · Italic {(_italic.Value ? "on" : "off")} · Underline {(_under.Value ? "on" : "off")}"));

    [Sample("Compact and disabled AppBarToggleButtons", Description = "Compact is the closed-CommandBar 48px icon-only layout. A disabled checked toggle keeps the disabled-accent pill; a disabled unchecked one stays transparent.")]
    static Element CompactDisabled() => new BoxEl
    {
        Direction = 0, Gap = 4, Padding = new Edges4(8, 6, 8, 6), Corners = Radii.OverlayAll, Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children =
        [
            AppBarToggleButton.Create(Icons.Shuffle, "Shuffle", isCompact: true),
            AppBarToggleButton.Create(Icons.Font, "Bold", isChecked: _disabledOn, isEnabled: false),
            AppBarToggleButton.Create(Icons.Font, "Italic", isEnabled: false),
        ],
    };
}

[GalleryPage("CommandBar", "CommandBar", "Menus & toolbars", Icon = Icons.More)]
sealed partial class CommandBarPage : Component
{
    static readonly Signal<string> _last = new("—");

    public override Element Render() => GalleryPage.Shell("CommandBar",
        "A toolbar for exposing common, frequently-used commands — with a … More button that expands the bar and opens the secondary commands as an overflow menu.",
        ExampleCard.Show(PrimarySecondarySample),
        ExampleCard.Show(MinimalSample));

    [Sample("A CommandBar with primary and secondary commands", Description = "The … button expands the closed compact bar to the full-size labeled layout and drops the secondary commands as an overflow menu.")]
    static Element PrimarySecondary()
    {
        var primary = new AppBarCommand[]
        {
            new(Icons.Add, "Add", () => _last.Value = "Add"),
            new(Icons.Tag, "Edit", () => _last.Value = "Edit"),
            new(Icons.Share, "Share", () => _last.Value = "Share"),
            new(Icons.Cancel, "Delete", () => _last.Value = "Delete"),
        };
        var secondary = new AppBarCommand[]
        {
            new(Icons.Settings, "Settings", () => _last.Value = "Settings"),
            AppBarCommand.Separator,
            new(Icons.Copy, "Copy", () => _last.Value = "Copy") { AcceleratorText = "Ctrl+C" },
        };
        return VStack(8,
            new BoxEl { Width = 440, Direction = 1, Children = [CommandBar.Create(primary, secondary)] },
            GalleryPage.LiveText(() => $"Invoked: {_last.Value}"));
    }

    [Sample("Minimal closed display mode", Description = "Closed, the bar is a 24px sliver showing only the … More button; opening it reveals the full-size labeled commands.")]
    static Element Minimal()
    {
        var primary = new AppBarCommand[] { new(Icons.Add, "Add"), new(Icons.Refresh, "Refresh") };
        var secondary = new AppBarCommand[] { new(Icons.Settings, "Settings") };
        // isSticky: true keeps the open bar from light-dismissing.
        return new BoxEl { Width = 440, Direction = 1, Children = [CommandBar.Create(primary, secondary, closedDisplayMode: CommandBarDisplayMode.Minimal)] };
    }
}

[GalleryPage("Viewbox", "Viewbox", "Layout", Icon = Icons.Picture)]
sealed partial class ViewboxPage : Component
{
    public override Element Render() => GalleryPage.Shell("Viewbox",
        "Scales its single child up or down to a target size, preserving aspect ratio.",
        ExampleCard.Show(ExplicitFactorSample),
        ExampleCard.Show(StretchModesSample),
        ExampleCard.Show(StretchDirectionSample));

    [Sample("A Viewbox (explicit 1.8× factor)")]
    static Element ExplicitFactor()
    {
        var chip = new BoxEl
        {
            Width = 80, Height = 40, Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl("GPU") { Size = 16f, Bold = true, Color = Tok.TextOnAccentPrimary }],
        };
        return Viewbox.Create(chip, scale: 1.8f);
    }

    [Sample("Stretch modes", Description = "The WinUI-faithful overload computes the scale from the content size and a target box (the 80×40 chip in a 120×80 frame: scaleX 1.5, scaleY 2.0).")]
    static Element StretchModes() => HStack(12,
        // The chip is 80×40; the frame is 120×80 (scaleX 1.5, scaleY 2.0):
        //   Uniform → 1.5× (fit within) · UniformToFill → 2× (fills, clips)
        //   Fill → 1.5×/2× (independent axes) · None → 1×
        StretchTile("Uniform (1.5×)", ViewboxStretch.Uniform),
        StretchTile("UniformToFill (2×, clips)", ViewboxStretch.UniformToFill),
        StretchTile("Fill (1.5× / 2×)", ViewboxStretch.Fill),
        StretchTile("None (1×)", ViewboxStretch.None));

    [Sample("Stretch direction", Description = "StretchDirection constrains which way the scale may go (the 80×40 chip in a 60×30 frame: Uniform scale 0.75).")]
    static Element StretchDirection() => HStack(12,
        // content 80×40 in a 60×30 box → Uniform scale 0.75
        DirectionTile("DownOnly — shrinks to fit", ViewboxStretchDirection.DownOnly),   // shrink allowed
        DirectionTile("UpOnly — never shrinks", ViewboxStretchDirection.UpOnly));       // clamps at 1× — never shrinks

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

[GalleryPage("ContentDialog", "ContentDialog", "Dialogs & flyouts", Icon = Icons.Document)]
sealed partial class ContentDialogPage : Component
{
    static readonly Signal<string> _result = new("—");

    public override Element Render() => GalleryPage.Shell("ContentDialog",
        "A modal dialog that shows contextual information and requires a response.",
        ExampleCard.Show(FullSample),
        ExampleCard.Show(SingleButtonSample));

    [Sample("A ContentDialog", Description = "Enter invokes the accent default (primary) button; Escape closes with ContentDialogResult.None; Tab cycles inside the dialog.")]
    static Element Full() => VStack(8,
        // Full form — observe the WinUI ShowAsync result via Closed:
        Embed.Comp(() => new ContentDialog
        {
            TriggerLabel = "Show dialog",
            Title = "Save your work?",
            Message = "Lorem ipsum dolor sit amet, adipisicing elit.",
            PrimaryText = "Save", SecondaryText = "Don't Save", CloseText = "Cancel",
            Closed = r => _result.Value = r.ToString(),   // Primary / Secondary / None
        }),
        GalleryPage.LiveText(() => $"Result: {_result.Value}"));

    [Sample("A ContentDialog with a single button", Description = "With one command the button right-aligns in the command row (the WinUI single-button column layout).")]
    static Element SingleButton() => ContentDialog.Create("Show dialog", "No internet connection",
        "Check your connection and try again.", "Close");
}

[GalleryPage("text-cat", "Text", "Overview", Hidden = true)]
sealed class TextOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Text", "Text input and display controls.",
            GalleryPage.CategoryGrid("Text", navigate));
    }
}
