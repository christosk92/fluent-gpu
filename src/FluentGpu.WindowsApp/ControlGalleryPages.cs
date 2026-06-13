using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Shared page scaffolding (WinUI Gallery look) ──────────────────────────────────
static class GalleryPage
{
    /// <summary>Standard page scaffold. <paramref name="title"/> doubles as the PageInfo key (control pages use the
    /// control name); pages whose display title differs from their nav key use <see cref="ShellKeyed"/>.</summary>
    public static Element Shell(string title, string description, params Element[] body)
        => ShellKeyed(title, title, description, body);

    public static Element ShellKeyed(string key, string title, string description, params Element[] body)
    {
        var meta = PageInfo.Find(key);
        var kids = new List<Element> { PageHeader.Build(title, description, meta), new BoxEl { Height = 8 } };
        kids.AddRange(body);
        if (meta is not null && PageInfo.RoutableRelated(meta) is { Length: > 0 } related)
            kids.Add(Embed.Comp(() => new RelatedLinks { Keys = related }));
        return ScrollView(new BoxEl { Direction = 1, Gap = 4f, Padding = Edges4.All(28), Children = kids.ToArray() });
    }

    /// <summary>A bold readout whose text rides a signal-reading thunk — only the text node updates (no page re-render).</summary>
    public static TextEl LiveText(Func<string> text) => new(text) { Size = 14f, Bold = true, Color = Tok.TextPrimary };

    // A clickable tile (image or glyph + title + optional subtitle) that navigates the shell — the WinUI
    // ControlItemTemplate card. Used by the overview / All / home pages.
    public static Element Tile(string title, string? image, string? glyph, Action onOpen, string? subtitle = null) => new BoxEl
    {
        Height = 90f, Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center, Padding = Edges4.All(14),
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onOpen,
        Children =
        [
            image is not null
                ? Image(Assets.ControlImage(image), 48f, 48f, 6f)
                : new BoxEl { Width = 48f, Height = 48f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(glyph ?? Icons.Tag, 28f).Foreground(Tok.AccentDefault)] },
            subtitle is null
                ? new BoxEl { Grow = 1f, Children = [BodyStrong(title)] }
                : new BoxEl
                {
                    Grow = 1f, Direction = 1, Gap = 2f, Justify = FlexJustify.Center,
                    Children = [BodyStrong(title), new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 }],
                },
        ],
    };

    /// <summary>A tile for a registered page key — image/subtitle pulled from the PageInfo registry.</summary>
    public static Element TileFor(string key, Action onOpen)
    {
        var meta = PageInfo.Find(key);
        return Tile(key, meta?.Image, null, onOpen, meta?.Subtitle);
    }

    /// <summary>The tile grid for one nav category (the WinUI category overview page body).</summary>
    public static Element CategoryGrid(string category, Action<string> navigate)
    {
        var keys = GalleryApp.CategoryKeys(category);
        var tiles = new Element[keys.Length];
        for (int i = 0; i < keys.Length; i++) { var k = keys[i]; tiles[i] = TileFor(k, () => navigate(k)); }
        return AutoGrid(300f, 12f, 90f, tiles);
    }
}

// ── Overview / category pages ─────────────────────────────────────────────────────
sealed class FundamentalsPage : Component
{
    // The engine model — kept in lockstep with the "fundamentals" nav group's children (Gallery.Items).
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("state", Icons.Refresh, "State & components"), ("flex", Icons.Tag, "Flexbox"), ("grid", Icons.Grid, "CSS Grid"),
        ("repeater", Icons.List, "ItemsRepeater"), ("virtualization", Icons.List, "List virtualization"),
        ("animation", Icons.Movie, "Animation"), ("compositor", Icons.Brush, "Compositor"),
        ("edge-fade", Icons.Brush, "Edge fade"), ("scrolling", Icons.Document, "Scrolling"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Fundamentals", "The engine model — the React/Reactor surface, layout, virtualization, and the motion/compositor pipeline.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

sealed class PatternsPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("motion-recipes", Icons.Movie, "Motion recipes"), ("async-skeletons", Icons.Refresh, "Async & skeletons"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Patterns", "UX recipes built on the engine — the Expressive Motion Kit and skeleton/shimmer-while-loading.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

sealed class AppServicesPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("localization", Icons.Globe, "Localization"), ("validation-guide", Icons.Accept, "Validation"),
        ("windowsapi", Icons.Globe, "Windows APIs"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("App services", "Engine features WinUI lacks — JSON/ICU localization, signals-native form validation, and the OS-services pillars.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

sealed class DesignPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("typography", Icons.Font, "Typography"), ("icons", Icons.Star, "Iconography"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Design", "Design guidance — the Fluent type ramp and iconography.", AutoGrid(300f, 12f, 90f, tiles));
    }
}

sealed class BasicInputOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Basic input", "Buttons, selection, and value controls — the WinUI Gallery Basic input set, built on the engine's controls layer.",
            GalleryPage.CategoryGrid("Basic input", navigate));
    }
}

sealed class AllControlsPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var sections = new List<Element>();
        foreach (var (title, keys) in GalleryApp.ControlCatalog)
        {
            sections.Add(new BoxEl { Height = 12f });
            sections.Add(Subtitle(title));
            var tiles = new Element[keys.Length];
            for (int i = 0; i < keys.Length; i++) { var k = keys[i]; tiles[i] = GalleryPage.TileFor(k, () => navigate(k)); }
            sections.Add(AutoGrid(300f, 12f, 90f, tiles));
        }
        return GalleryPage.Shell("All controls", "Every control in the gallery, grouped by category.", sections.ToArray());
    }
}

// ── Basic input — the 14 control demo pages ───────────────────────────────────────
sealed class ButtonControlPage : Component
{
    public override Element Render()
    {
        var (clicks, setClicks) = UseState(0);
        return GalleryPage.Shell("Button", "A control that responds to user input and raises a Click event.",
            ControlExample.Build("A standard & accent button", HStack(8, Button.Standard("Standard", () => setClicks(clicks + 1)), Button.Accent("Accent", () => setClicks(clicks + 1))),
                output: BodyStrong($"Clicks: {clicks}"),
                code: """
                var (clicks, setClicks) = UseState(0);

                HStack(8,
                    Button.Standard("Standard", () => setClicks(clicks + 1)),
                    Button.Accent("Accent", () => setClicks(clicks + 1)))
                """),
            ControlExample.Build("A disabled button", DisabledButton("Disabled"),
                code: """
                Button.Standard("Disabled", () => { }, isEnabled: false)
                """));
    }

    static Element DisabledButton(string label) => new BoxEl
    {
        Direction = 0, Padding = new Edges4(11, 5, 11, 6), MinHeight = 32f, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll,
        Fill = Tok.FillControlDisabled, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
        Children = [new TextEl(label) { Size = 14f, Color = Tok.TextDisabled }],
    };
}

sealed class DropDownButtonControlPage : Component
{
    public override Element Render()
    {
        var (pick, setPick) = UseState("—");
        var items = new List<MenuFlyoutItem>
        {
            new("Small", Icons.Tag, true, () => setPick("Small")),
            new("Medium", Icons.Tag, true, () => setPick("Medium")),
            new("Large", Icons.Tag, true, () => setPick("Large")),
            MenuFlyoutItem.Separator,
            new("Disabled", Icons.Cancel, false, null),
        };
        return GalleryPage.Shell("DropDownButton", "A button that displays a flyout of choices when clicked.",
            ControlExample.Build("A DropDownButton with a menu flyout", DropDownButton.Create("Sizes", items, Icons.Font),
                output: BodyStrong($"Chosen: {pick}"),
                code: """
                var items = new List<MenuFlyoutItem>
                {
                    new("Small", Icons.Tag, true, () => setPick("Small")),
                    new("Medium", Icons.Tag, true, () => setPick("Medium")),
                    new("Large", Icons.Tag, true, () => setPick("Large")),
                    MenuFlyoutItem.Separator,
                    new("Disabled", Icons.Cancel, false, null),
                };

                DropDownButton.Create("Sizes", items, Icons.Font)
                """));
    }
}

sealed class HyperlinkButtonControlPage : Component
{
    public override Element Render()
    {
        var (clicks, setClicks) = UseState(0);
        return GalleryPage.Shell("HyperlinkButton", "A button that appears as hyperlink text and can navigate.",
            ControlExample.Build("A HyperlinkButton", HyperlinkButton.Create("Open the design docs", () => setClicks(clicks + 1)),
                output: BodyStrong($"Activated {clicks}×"),
                code: """
                // Click handler form:
                HyperlinkButton.Create("Open the design docs", () => setClicks(clicks + 1))

                // NavigateUri form (opens in the default browser via the PAL OpenUri seam):
                HyperlinkButton.Create("fluent-gpu on GitHub", "https://github.com/christosk92/fluent-gpu")
                """));
    }
}

sealed class RepeatButtonControlPage : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return GalleryPage.Shell("RepeatButton", "A button that raises Click events repeatedly while it is pressed and held.",
            ControlExample.Build("Hold to increment", HStack(8, RepeatButton.Create("–", () => setCount(count - 1)), RepeatButton.Create("+", () => setCount(count + 1))),
                output: BodyStrong($"Value: {count}"),
                code: """
                var (count, setCount) = UseState(0);

                HStack(8,
                    RepeatButton.Create("–", () => setCount(count - 1)),
                    RepeatButton.Create("+", () => setCount(count + 1)))
                """));
    }
}

sealed class ToggleButtonControlPage : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        var (tri, setTri) = UseState(CheckState.Unchecked);
        return GalleryPage.Shell("ToggleButton", "A button that can be switched between two states (or a third, indeterminate, state).",
            ControlExample.Build("Two-state", ToggleButton.Create(on ? "On" : "Off", on, () => setOn(!on)), output: BodyStrong(on ? "On" : "Off"),
                code: """
                var (on, setOn) = UseState(false);

                ToggleButton.Create(on ? "On" : "Off", on, () => setOn(!on))
                """),
            ControlExample.Build("Three-state", ToggleButton.Create($"{tri}", tri, setTri), output: BodyStrong($"{tri}"),
                code: """
                var (tri, setTri) = UseState(CheckState.Unchecked);

                // Clicks cycle Unchecked -> Checked -> Indeterminate.
                ToggleButton.Create($"{tri}", tri, setTri)
                """));
    }
}

sealed class SplitButtonControlPage : Component
{
    public override Element Render()
    {
        var (msg1, setMsg1) = UseState("—");   // each example owns its own output state (was one shared signal → both updated)
        var (msg2, setMsg2) = UseState("—");
        var items = new List<MenuFlyoutItem>
        {
            new("Paste as text", Icons.Document, true, () => setMsg1("Paste as text")),
            new("Paste special", Icons.Document, true, () => setMsg1("Paste special")),
        };
        var colors = new List<MenuFlyoutItem>
        {
            new("Red", null, true, () => setMsg2("Red")),
            new("Green", null, true, () => setMsg2("Green")),
            new("Blue", null, true, () => setMsg2("Blue")),
        };
        return GalleryPage.Shell("SplitButton", "A two-part button: a primary action plus a dropdown of related choices.",
            ControlExample.Build("A SplitButton", SplitButton.Create("Paste", () => setMsg1("Paste (primary)"), items, Icons.Document),
                output: BodyStrong(msg1),
                code: """
                var items = new List<MenuFlyoutItem>
                {
                    new("Paste as text", Icons.Document, true, () => setMsg("Paste as text")),
                    new("Paste special", Icons.Document, true, () => setMsg("Paste special")),
                };

                SplitButton.Create("Paste", () => setMsg("Paste (primary)"), items, Icons.Document)
                """),
            ControlExample.Build("A SplitButton with text", SplitButton.Create("Choose color", () => setMsg2("Choose color"), colors),
                output: BodyStrong(msg2),
                code: """
                SplitButton.Create("Choose color", () => setMsg("Choose color"), colors)
                """));
    }
}

sealed class ToggleSplitButtonControlPage : Component
{
    public override Element Render()
    {
        var on = UseSignal(false);
        var (style, setStyle) = UseState("List");
        var items = new List<MenuFlyoutItem>
        {
            new("Bulleted list", Icons.List, true, () => setStyle("Bulleted")),
            new("Numbered list", Icons.List, true, () => setStyle("Numbered")),
        };
        return GalleryPage.Shell("ToggleSplitButton", "A SplitButton whose primary part toggles on and off.",
            ControlExample.Build("A ToggleSplitButton", ToggleSplitButton.Create("List", on, items, glyph: Icons.List),
                output: BodyStrong($"On: {on.Value} · {style}"),
                code: """
                var on = UseSignal(false);
                var items = new List<MenuFlyoutItem>
                {
                    new("Bulleted list", Icons.List, true, () => setStyle("Bulleted")),
                    new("Numbered list", Icons.List, true, () => setStyle("Numbered")),
                };

                ToggleSplitButton.Create("List", on, items, glyph: Icons.List)
                """));
    }
}

sealed class CheckBoxControlPage : Component
{
    public override Element Render()
    {
        var (a, setA) = UseState(false);
        var (tri, setTri) = UseState(CheckState.Indeterminate);
        // "Select all" — parent reflects the two children.
        var (c1, setC1) = UseState(true);
        var (c2, setC2) = UseState(false);
        var parent = c1 && c2 ? CheckState.Checked : c1 || c2 ? CheckState.Indeterminate : CheckState.Unchecked;
        void ToggleAll() { bool all = !(c1 && c2); setC1(all); setC2(all); }

        return GalleryPage.Shell("CheckBox", "A control for selecting or clearing options — two-state, or three-state with an indeterminate value.",
            ControlExample.Build("Two-state", CheckBox.Create("I agree", a, () => setA(!a)), output: BodyStrong(a ? "Checked" : "Unchecked"),
                code: """
                var (a, setA) = UseState(false);

                CheckBox.Create("I agree", a, () => setA(!a))
                """),
            ControlExample.Build("Three-state", CheckBox.Create("Mixed", tri, setTri), output: BodyStrong($"{tri}"),
                code: """
                var (tri, setTri) = UseState(CheckState.Indeterminate);

                CheckBox.Create("Mixed", tri, setTri)
                """),
            ControlExample.Build("Select all (indeterminate parent)",
                VStack(4,
                    CheckBox.Create("Select all", parent, _ => ToggleAll()),
                    new BoxEl { Margin = new Edges4(24, 0, 0, 0), Direction = 1, Gap = 4, Children = [CheckBox.Create("Option A", c1, () => setC1(!c1)), CheckBox.Create("Option B", c2, () => setC2(!c2))] }),
                code: """
                var (c1, setC1) = UseState(true);
                var (c2, setC2) = UseState(false);
                var parent = c1 && c2 ? CheckState.Checked
                           : c1 || c2 ? CheckState.Indeterminate
                           : CheckState.Unchecked;
                void ToggleAll() { bool all = !(c1 && c2); setC1(all); setC2(all); }

                VStack(4,
                    CheckBox.Create("Select all", parent, _ => ToggleAll()),
                    CheckBox.Create("Option A", c1, () => setC1(!c1)),
                    CheckBox.Create("Option B", c2, () => setC2(!c2)))
                """));
    }
}

sealed class ColorPickerControlPage : Component
{
    public override Element Render()
    {
        var color = UseSignal(ColorF.FromRgba(0x4C, 0xC2, 0xFF));
        return GalleryPage.Shell("ColorPicker", "A spectrum, hue and alpha selector with channel/hex input.",
            ControlExample.Build("A ColorPicker with alpha", ColorPicker.Create(color, alphaEnabled: true),
                output: VStack(6,
                    new BoxEl { Width = 96, Height = 40, Corners = Radii.ControlAll, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f, Fill = color },
                    GalleryPage.LiveText(() => "#" + color.Value.ToHex())),
                code: """
                var color = UseSignal(ColorF.FromRgba(0x4C, 0xC2, 0xFF));

                ColorPicker.Create(color, alphaEnabled: true)

                // The swatch rides a compositor-only fill binding — no re-render while dragging:
                new BoxEl { Width = 96, Height = 40, Fill = color }
                """));
    }
}

sealed class ComboBoxControlPage : Component
{
    static readonly string[] Fonts = { "Segoe UI", "Cascadia Code", "Arial", "Calibri", "Consolas", "Georgia" };

    public override Element Render()
    {
        var sel = UseSignal(0);
        var editSel = UseSignal(-1);
        var editText = UseSignal("");
        return GalleryPage.Shell("ComboBox", "A drop-down list of items a user can select from — optionally with an editable text field.",
            ControlExample.Build("A ComboBox", ComboBox.Create(Fonts, sel, placeholder: "Pick a font"),
                output: GalleryPage.LiveText(() => sel.Value >= 0 ? Fonts[sel.Value] : "—"),
                code: """
                static readonly string[] Fonts = { "Segoe UI", "Cascadia Code", "Arial", "Calibri", "Consolas", "Georgia" };
                var sel = UseSignal(0);

                ComboBox.Create(Fonts, sel, placeholder: "Pick a font")
                """),
            ControlExample.Build("An editable ComboBox", ComboBox.Create(Fonts, editSel, editable: true, text: editText, placeholder: "Type or pick"),
                output: GalleryPage.LiveText(() => editText.Value.Length > 0 ? editText.Value : "—"),
                code: """
                var editSel = UseSignal(-1);
                var editText = UseSignal("");

                ComboBox.Create(Fonts, editSel, editable: true, text: editText, placeholder: "Type or pick")
                """));
    }
}

sealed class RadioButtonControlPage : Component
{
    static readonly string[] Options = { "Small", "Medium", "Large" };

    public override Element Render()
    {
        var (sel, setSel) = UseState(1);
        return GalleryPage.Shell("RadioButton", "A control that lets a user select a single option from a mutually-exclusive set.",
            ControlExample.Build("A RadioButtons group", RadioButton.Group(Options, sel, setSel),
                output: BodyStrong($"Selected: {(sel >= 0 ? Options[sel] : "—")}"),
                code: """
                static readonly string[] Options = { "Small", "Medium", "Large" };
                var (sel, setSel) = UseState(1);

                RadioButton.Group(Options, sel, setSel)
                """));
    }
}

sealed class RatingControlControlPage : Component
{
    public override Element Render()
    {
        var val = UseFloatSignal(3f);
        var ro = UseFloatSignal(4f);
        return GalleryPage.Shell("RatingControl", "Lets a user rate with a row of stars, set by click or drag.",
            ControlExample.Build("Interactive", RatingControl.Create(val), output: GalleryPage.LiveText(() => $"{(int)val.Value} / 5"),
                code: """
                var val = UseFloatSignal(3f);

                RatingControl.Create(val)
                """),
            ControlExample.Build("Read-only", RatingControl.Create(ro, readOnly: true),
                code: """
                RatingControl.Create(UseFloatSignal(4f), readOnly: true)
                """));
    }
}

sealed class SliderControlPage : Component
{
    public override Element Render()
    {
        var basic = UseFloatSignal(0.4f);
        var (range, setRange) = UseState(50f);
        var (ticks, setTicks) = UseState(40f);
        var (vert, setVert) = UseState(30f);
        return GalleryPage.Shell("Slider", "Selects a value from a range — with optional ticks, step snapping and vertical orientation.",
            ControlExample.Build("A simple slider (0–1, signal-bound)", Slider.Bind(basic), output: GalleryPage.LiveText(() => $"{basic.Value:0.00}"),
                code: """
                // The hot path: bind a FloatSignal — drags update the thumb/track via
                // compositor bindings, with NO component re-render per move.
                var basic = UseFloatSignal(0.4f);

                Slider.Bind(basic)
                """),
            ControlExample.Build("A ranged slider (0–100)", Slider.Ranged(range, setRange, new Slider.Options { Min = 0, Max = 100 }), output: BodyStrong($"{range:0}"),
                code: """
                var (range, setRange) = UseState(50f);

                Slider.Ranged(range, setRange, new Slider.Options { Min = 0, Max = 100 })
                """),
            ControlExample.Build("Ticks + step (step 10)", Slider.Ranged(ticks, setTicks, new Slider.Options { Min = 0, Max = 100, Step = 10, TickFrequency = 10 }), output: BodyStrong($"{ticks:0}"),
                code: """
                Slider.Ranged(ticks, setTicks,
                    new Slider.Options { Min = 0, Max = 100, Step = 10, TickFrequency = 10 })
                """),
            ControlExample.Build("Vertical", Slider.Ranged(vert, setVert, new Slider.Options { Min = 0, Max = 100, Vertical = true }, length: 160f), output: BodyStrong($"{vert:0}"),
                code: """
                Slider.Ranged(vert, setVert,
                    new Slider.Options { Min = 0, Max = 100, Vertical = true }, length: 160f)
                """));
    }
}

sealed class ToggleSwitchControlPage : Component
{
    public override Element Render()
    {
        var (a, setA) = UseState(true);
        var (b, setB) = UseState(false);
        return GalleryPage.Shell("ToggleSwitch", "A switch that a user can turn on and off.",
            ControlExample.Build("A simple ToggleSwitch", ToggleSwitch.Create(a, () => setA(!a)), output: BodyStrong(a ? "On" : "Off"),
                code: """
                var (a, setA) = UseState(true);

                ToggleSwitch.Create(a, () => setA(!a))
                """),
            ControlExample.Build("With header + On/Off content", ToggleSwitch.Create(b, () => setB(!b), header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected"),
                output: BodyStrong(b ? "Connected" : "Disconnected"),
                code: """
                ToggleSwitch.Create(b, () => setB(!b),
                    header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected")
                """));
    }
}
