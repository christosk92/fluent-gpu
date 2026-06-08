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
    public static Element Shell(string title, string description, params Element[] body)
    {
        var kids = new List<Element> { Heading(title), Body(description).Secondary(), new BoxEl { Height = 8 } };
        kids.AddRange(body);
        return ScrollView(new BoxEl { Direction = 1, Gap = 4f, Padding = Edges4.All(28), Children = kids.ToArray() });
    }

    /// <summary>A bold readout whose text rides a signal-reading thunk — only the text node updates (no page re-render).</summary>
    public static TextEl LiveText(Func<string> text) => new("") { Size = 14f, Bold = true, Color = Tok.TextPrimary, TextBind = text };

    // A clickable tile (image or glyph + title) that navigates the shell. Used by the overview / All pages.
    public static Element Tile(string title, string? image, string? glyph, Action onOpen) => new BoxEl
    {
        Height = 90f, Direction = 0, Gap = 14f, AlignItems = FlexAlign.Center, Padding = Edges4.All(14),
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onOpen,
        Children =
        [
            image is not null
                ? Image(Assets.ControlImage(image), 48f, 48f, 6f)
                : new BoxEl { Width = 48f, Height = 48f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(glyph ?? Icons.Tag, 28f).Foreground(Tok.AccentDefault)] },
            new BoxEl { Grow = 1f, Children = [BodyStrong(title)] },
        ],
    };
}

// ── Overview / category pages ─────────────────────────────────────────────────────
sealed class FundamentalsPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("state", Icons.Refresh, "State & components"), ("flex", Icons.Tag, "Flexbox"), ("grid", Icons.Grid, "CSS Grid"),
        ("repeater", Icons.List, "ItemsRepeater"), ("virtualization", Icons.List, "List virtualization"),
        ("animation", Icons.Movie, "Animation"), ("compositor", Icons.Brush, "Compositor"), ("scrolling", Icons.Document, "Scrolling"),
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

sealed class DesignPage : Component
{
    static readonly (string Key, string Glyph, string Title)[] Items =
    {
        ("typography", Icons.Font, "Typography"), ("icons", Icons.Star, "Iconography"), ("images", Icons.Picture, "Images"),
    };

    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var tiles = new Element[Items.Length];
        for (int i = 0; i < Items.Length; i++) { var d = Items[i]; tiles[i] = GalleryPage.Tile(d.Title, null, d.Glyph, () => navigate(d.Key)); }
        return GalleryPage.Shell("Design", "Design guidance — the Fluent type ramp, iconography, and async imagery.", AutoGrid(300f, 12f, 90f, tiles));
    }
}

sealed class BasicInputOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var cat = GalleryApp.BasicInputCatalog;
        var tiles = new Element[cat.Length];
        for (int i = 0; i < cat.Length; i++) { var c = cat[i]; tiles[i] = GalleryPage.Tile(c.Title, c.Image, null, () => navigate(c.Key)); }
        return GalleryPage.Shell("Basic input", "Buttons, selection, and value controls — the WinUI Gallery Basic input set, built on the engine's controls layer.",
            AutoGrid(300f, 12f, 90f, tiles));
    }
}

sealed class AllControlsPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        var cat = GalleryApp.BasicInputCatalog;
        var tiles = new Element[cat.Length];
        for (int i = 0; i < cat.Length; i++) { var c = cat[i]; tiles[i] = GalleryPage.Tile(c.Title, c.Image, null, () => navigate(c.Key)); }
        return GalleryPage.Shell("All controls", "Every control in the gallery. (Today: the Basic input set — more categories land as the controls layer grows.)",
            AutoGrid(300f, 12f, 90f, tiles));
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
                output: BodyStrong($"Clicks: {clicks}")),
            ControlExample.Build("A disabled button", DisabledButton("Disabled")));
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
                output: BodyStrong($"Chosen: {pick}")));
    }
}

sealed class HyperlinkButtonControlPage : Component
{
    public override Element Render()
    {
        var (clicks, setClicks) = UseState(0);
        return GalleryPage.Shell("HyperlinkButton", "A button that appears as hyperlink text and can navigate.",
            ControlExample.Build("A HyperlinkButton", HyperlinkButton.Create("Open the design docs", () => setClicks(clicks + 1)),
                output: BodyStrong($"Activated {clicks}×")));
    }
}

sealed class RepeatButtonControlPage : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        return GalleryPage.Shell("RepeatButton", "A button that raises Click events repeatedly while it is pressed and held.",
            ControlExample.Build("Hold to increment", HStack(8, RepeatButton.Create("–", () => setCount(count - 1)), RepeatButton.Create("+", () => setCount(count + 1))),
                output: BodyStrong($"Value: {count}")));
    }
}

sealed class ToggleButtonControlPage : Component
{
    public override Element Render()
    {
        var (on, setOn) = UseState(false);
        var (tri, setTri) = UseState(CheckState.Unchecked);
        return GalleryPage.Shell("ToggleButton", "A button that can be switched between two states (or a third, indeterminate, state).",
            ControlExample.Build("Two-state", ToggleButton.Create(on ? "On" : "Off", on, () => setOn(!on)), output: BodyStrong(on ? "On" : "Off")),
            ControlExample.Build("Three-state", ToggleButton.Create($"{tri}", tri, setTri), output: BodyStrong($"{tri}")));
    }
}

sealed class SplitButtonControlPage : Component
{
    public override Element Render()
    {
        var (msg, setMsg) = UseState("—");
        var items = new List<MenuFlyoutItem>
        {
            new("Paste as text", Icons.Document, true, () => setMsg("Paste as text")),
            new("Paste special", Icons.Document, true, () => setMsg("Paste special")),
        };
        return GalleryPage.Shell("SplitButton", "A two-part button: a primary action plus a dropdown of related choices.",
            ControlExample.Build("A SplitButton", SplitButton.Create("Paste", () => setMsg("Paste (primary)"), items, Icons.Document),
                output: BodyStrong(msg)));
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
                output: BodyStrong($"On: {on.Value} · {style}")));
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
            ControlExample.Build("Two-state", CheckBox.Create("I agree", a, () => setA(!a)), output: BodyStrong(a ? "Checked" : "Unchecked")),
            ControlExample.Build("Three-state", CheckBox.Create("Mixed", tri, setTri), output: BodyStrong($"{tri}")),
            ControlExample.Build("Select all (indeterminate parent)",
                VStack(4,
                    CheckBox.Create("Select all", parent, _ => ToggleAll()),
                    new BoxEl { Margin = new Edges4(24, 0, 0, 0), Direction = 1, Gap = 4, Children = [CheckBox.Create("Option A", c1, () => setC1(!c1)), CheckBox.Create("Option B", c2, () => setC2(!c2))] })));
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
                    new BoxEl { Width = 96, Height = 40, Corners = Radii.ControlAll, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f, FillBind = () => color.Value },
                    GalleryPage.LiveText(() => "#" + color.Value.ToHex()))));
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
                output: GalleryPage.LiveText(() => sel.Value >= 0 ? Fonts[sel.Value] : "—")),
            ControlExample.Build("An editable ComboBox", ComboBox.Create(Fonts, editSel, editable: true, text: editText, placeholder: "Type or pick"),
                output: GalleryPage.LiveText(() => editText.Value.Length > 0 ? editText.Value : "—")));
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
                output: BodyStrong($"Selected: {(sel >= 0 ? Options[sel] : "—")}")));
    }
}

sealed class RatingControlControlPage : Component
{
    public override Element Render()
    {
        var val = UseFloatSignal(3f);
        var ro = UseFloatSignal(4f);
        return GalleryPage.Shell("RatingControl", "Lets a user rate with a row of stars, set by click or drag.",
            ControlExample.Build("Interactive", RatingControl.Create(val), output: GalleryPage.LiveText(() => $"{(int)val.Value} / 5")),
            ControlExample.Build("Read-only", RatingControl.Create(ro, readOnly: true)));
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
            ControlExample.Build("A simple slider (0–1, signal-bound)", Slider.Bind(basic), output: GalleryPage.LiveText(() => $"{basic.Value:0.00}")),
            ControlExample.Build("A ranged slider (0–100)", Slider.Ranged(range, setRange, new Slider.Options { Min = 0, Max = 100 }), output: BodyStrong($"{range:0}")),
            ControlExample.Build("Ticks + step (step 10)", Slider.Ranged(ticks, setTicks, new Slider.Options { Min = 0, Max = 100, Step = 10, TickFrequency = 10 }), output: BodyStrong($"{ticks:0}")),
            ControlExample.Build("Vertical", Slider.Ranged(vert, setVert, new Slider.Options { Min = 0, Max = 100, Vertical = true }, length: 160f), output: BodyStrong($"{vert:0}")));
    }
}

sealed class ToggleSwitchControlPage : Component
{
    public override Element Render()
    {
        var (a, setA) = UseState(true);
        var (b, setB) = UseState(false);
        return GalleryPage.Shell("ToggleSwitch", "A switch that a user can turn on and off.",
            ControlExample.Build("A simple ToggleSwitch", ToggleSwitch.Create(a, () => setA(!a)), output: BodyStrong(a ? "On" : "Off")),
            ControlExample.Build("With header + On/Off content", ToggleSwitch.Create(b, () => setB(!b), header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected"),
                output: BodyStrong(b ? "Connected" : "Disconnected")));
    }
}
