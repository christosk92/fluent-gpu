using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Reconciler;
using static FluentGpu.Dsl.Ui;

// The capability gallery — a nav-driven showcase of everything fluent-gpu can do, styled to mirror the WinUI 3 Gallery:
// an integrated title bar + search, a grouped adaptive NavigationView (Expanded → Compact → Minimal), a sliding
// selection indicator, page-transition entrances, and the ControlExample pattern on every demo page.
//   dotnet run --project src/FluentGpu.WindowsApp
sealed class GalleryApp : Component
{
    // Mirrors the WinUI 3 Gallery's shape — Home, Fundamentals, Design, Controls (All + an expanded Basic input group) —
    // with the engine's own capability demos remapped under Fundamentals/Design/Samples. (Accessibility is out of scope.)
    static readonly NavItem[] Items =
    {
        new("welcome", Icons.Home, "Home"),
        new("fundamentals", Icons.Document, "Fundamentals")
        {
            Children =
            [
                new("state", Icons.Refresh, "State & components"),
                new("flex", Icons.Tag, "Flexbox"),
                new("grid", Icons.Grid, "CSS Grid"),
                new("repeater", Icons.List, "ItemsRepeater"),
                new("virtualization", Icons.List, "List virtualization"),
                new("animation", Icons.Movie, "Animation"),
                new("compositor", Icons.Brush, "Compositor"),
                new("scrolling", Icons.Document, "Scrolling"),
            ],
        },
        new("design", Icons.Brush, "Design")
        {
            Children =
            [
                new("typography", Icons.Font, "Typography"),
                new("icons", Icons.Star, "Icons & fonts"),
                new("images", Icons.Picture, "Images"),
            ],
        },
        new("h-controls", "", "Controls", IsHeader: true),
        new("all", Icons.Grid, "All"),
        new("basic-input", Icons.Accept, "Basic input")
        {
            InitiallyExpanded = true,
            Children =
            [
                new("Button", Icons.Accept, "Button"),
                new("DropDownButton", Icons.More, "DropDownButton"),
                new("HyperlinkButton", Icons.Share, "HyperlinkButton"),
                new("RepeatButton", Icons.Refresh, "RepeatButton"),
                new("ToggleButton", Icons.Accept, "ToggleButton"),
                new("SplitButton", Icons.More, "SplitButton"),
                new("ToggleSplitButton", Icons.More, "ToggleSplitButton"),
                new("CheckBox", Icons.Accept, "CheckBox"),
                new("ColorPicker", Icons.Brush, "ColorPicker"),
                new("ComboBox", Icons.List, "ComboBox"),
                new("RadioButton", Icons.FavoriteStar, "RadioButton"),
                new("RatingControl", Icons.Star, "RatingControl"),
                new("Slider", Icons.Volume, "Slider"),
                new("ToggleSwitch", Icons.Settings, "ToggleSwitch"),
            ],
        },
        new("h-samples", "", "Samples", IsHeader: true),
        new("wavee", Icons.MusicNote, "Wavee skeleton"),
    };

    // Maps a Basic-input control key to its bundled WinUI-Gallery tile image (note the casing: "CheckBox" → "Checkbox.png").
    public static readonly (string Key, string Title, string Image)[] BasicInputCatalog =
    {
        ("Button", "Button", "Button.png"),
        ("DropDownButton", "DropDownButton", "DropDownButton.png"),
        ("HyperlinkButton", "HyperlinkButton", "HyperlinkButton.png"),
        ("RepeatButton", "RepeatButton", "RepeatButton.png"),
        ("ToggleButton", "ToggleButton", "ToggleButton.png"),
        ("SplitButton", "SplitButton", "SplitButton.png"),
        ("ToggleSplitButton", "ToggleSplitButton", "ToggleSplitButton.png"),
        ("CheckBox", "CheckBox", "Checkbox.png"),
        ("ColorPicker", "ColorPicker", "ColorPicker.png"),
        ("ComboBox", "ComboBox", "ComboBox.png"),
        ("RadioButton", "RadioButton", "RadioButton.png"),
        ("RatingControl", "RatingControl", "RatingControl.png"),
        ("Slider", "Slider", "Slider.png"),
        ("ToggleSwitch", "ToggleSwitch", "ToggleSwitch.png"),
    };

    // Initial nav page (default = Home). Overridable so the --screenshot harness can deep-link a control page.
    public string InitialPage = "welcome";

    public override Element Render()
    {
        var shell = VStack(0,
            TitleBar(),
            Embed.Comp(() => new NavigationView
            {
                Header = "fluent-gpu",
                Initial = InitialPage,
                Items = Items,
                Content = Page,   // each page is a distinct component type → the reconciler remounts it on navigation
            })
        ) with { Grow = 1 };

        var content = ZStack(shell, DiagnosticsOverlay()) with { Grow = 1 };
        // Host the overlay layer at the top so anchored flyouts (ComboBox/DropDownButton/SplitButton/ColorPicker) work app-wide.
        return Embed.Comp(() => new OverlayHost { Child = content });
    }

    // Integrated title bar (transparent → window Mica shows through): app identity left, a centered search pill,
    // a right inset clearing the system caption buttons.
    static Element TitleBar() => new BoxEl
    {
        Direction = 0, Height = 48, AlignItems = FlexAlign.Center, Gap = 10, Padding = new Edges4(14, 0, 140, 0),
        Children =
        [
            Icon(Icons.Grid, 16f).Foreground(Tok.AccentDefault),
            new TextEl("fluent-gpu") { Size = 12f, Bold = true, Color = Tok.TextPrimary },
            new BoxEl { Grow = 1 },
            SearchBox(),
            new BoxEl { Grow = 1 },
        ],
    };

    // A search box (visual; text input is a future Text-subsystem capability). Mirrors the WinUI AutoSuggestBox.
    static Element SearchBox() => new BoxEl
    {
        Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, MaxWidth = 460, Grow = 1, Height = 30,
        Padding = new Edges4(11, 0, 11, 0), Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
        HoverFill = Tok.FillControlSecondary,
        Children =
        [
            Icon(Icons.Search, 14f).Foreground(Tok.TextSecondary),
            Caption("Search controls and samples").Tertiary(),
        ],
    };

    static Element DiagnosticsOverlay() => new BoxEl
    {
        Direction = 0,
        Justify = FlexJustify.End,
        AlignItems = FlexAlign.Start,
        HitTestVisible = false,
        Padding = new Edges4(12, 56, 152, 0),
        Children = [Embed.Comp(() => new FrameDiagnosticsHud())],
    };

    static Element Page(string key) => key switch
    {
        // Overview / category pages (WinUI-Gallery shape).
        "fundamentals" => Embed.Comp(() => new FundamentalsPage()),
        "design" => Embed.Comp(() => new DesignPage()),
        "all" => Embed.Comp(() => new AllControlsPage()),
        "basic-input" => Embed.Comp(() => new BasicInputOverviewPage()),

        // Basic input — the 14 control demo pages.
        "Button" => Embed.Comp(() => new ButtonControlPage()),
        "DropDownButton" => Embed.Comp(() => new DropDownButtonControlPage()),
        "HyperlinkButton" => Embed.Comp(() => new HyperlinkButtonControlPage()),
        "RepeatButton" => Embed.Comp(() => new RepeatButtonControlPage()),
        "ToggleButton" => Embed.Comp(() => new ToggleButtonControlPage()),
        "SplitButton" => Embed.Comp(() => new SplitButtonControlPage()),
        "ToggleSplitButton" => Embed.Comp(() => new ToggleSplitButtonControlPage()),
        "CheckBox" => Embed.Comp(() => new CheckBoxControlPage()),
        "ColorPicker" => Embed.Comp(() => new ColorPickerControlPage()),
        "ComboBox" => Embed.Comp(() => new ComboBoxControlPage()),
        "RadioButton" => Embed.Comp(() => new RadioButtonControlPage()),
        "RatingControl" => Embed.Comp(() => new RatingControlControlPage()),
        "Slider" => Embed.Comp(() => new SliderControlPage()),
        "ToggleSwitch" => Embed.Comp(() => new ToggleSwitchControlPage()),

        // Engine capability demos (remapped under Fundamentals / Design).
        "typography" => Embed.Comp(() => new TypographyPage()),
        "icons" => Embed.Comp(() => new IconsPage()),
        "buttons" => Embed.Comp(() => new ButtonsPage()),
        "inputs" => Embed.Comp(() => new InputsPage()),
        "flex" => Embed.Comp(() => new FlexPage()),
        "grid" => Embed.Comp(() => new GridPage()),
        "repeater" => Embed.Comp(() => new RepeaterPage()),
        "images" => Embed.Comp(() => new ImagesPage()),
        "scrolling" => Embed.Comp(() => new ScrollPage()),
        "virtualization" => Embed.Comp(() => new VirtualizationPage()),
        "animation" => Embed.Comp(() => new AnimationPage()),
        "compositor" => Embed.Comp(() => new CompositorPage()),
        "state" => Embed.Comp(() => new StatePage()),
        "wavee" => Embed.Comp(() => new WaveeShell()),
        _ => Embed.Comp(() => new WelcomePage()),
    };
}

sealed class FrameDiagnosticsHud : Component
{
    public override Element Render()
    {
        return new BoxEl
        {
            Direction = 0,
            Gap = 10,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(10, 5, 10, 5),
            MinHeight = 30,
            Fill = ColorF.FromRgba(0x13, 0x15, 0x1A, 0xCC),
            BorderColor = Tok.StrokeSurfaceDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Children =
            [
                Metric("fps", "000", DynamicTextKind.FrameFps, Tok.AccentDefault),
                Metric("cmd", "0000", DynamicTextKind.FrameCommandCount, Tok.TextPrimary),
                Metric("draw", "0000", DynamicTextKind.FrameDrawCount, Tok.TextPrimary),
                Metric("cull", "0000", DynamicTextKind.FrameCullCount, Tok.TextPrimary),
                Metric("ms", "000.0", DynamicTextKind.FrameMs, Tok.TextSecondary),
            ],
        };
    }

    static Element Metric(string label, string placeholder, DynamicTextKind dynamicText, ColorF valueColor) => new BoxEl
    {
        Direction = 0,
        Gap = 4,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
            new TextEl(placeholder) { Size = 12f, Bold = true, Color = valueColor, FontFamily = "Cascadia Code", DynamicText = dynamicText },
        ],
    };
}
