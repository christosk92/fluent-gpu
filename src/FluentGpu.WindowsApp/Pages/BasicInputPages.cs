using System;
using System.Collections.Generic;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Basic input — the 14 control demo pages (WS7 W7.2: never-drift [Sample] + Knobs) ──────────────────────────────
// Each page's examples are [Sample] static methods; ExampleCard.Show mounts the live example over its verbatim source
// (extracted by SampleExtractorGenerator) with an interactive Knobs panel. Knobs are registration-by-label — a sample
// factory is static (can't call Use* hooks), so all interactive state is either a knob signal (Toggle/Slider/Choice/
// Color) or a page-static signal (persists across the fresh sample re-mount). Reading a knob's .Value inside the
// factory re-renders the example on change; passing a value signal to a control binds it (compositor-only). Every
// Basic-input page has ≥1 knobbed example (the W7.2 gate).

[GalleryPage("Button", "Button", "Basic input", Icon = Icons.Accept)]
sealed partial class ButtonControlPage : Component
{
    public override Element Render() => GalleryPage.Shell("Button", "A control that responds to user input and raises a Click event.",
        ExampleCard.Show(InteractiveSample),
        ExampleCard.Show(AppearanceAxisSample),
        ExampleCard.Show(SizeAxisSample),
        ExampleCard.Show(IconSlotSample));

    [Sample("An interactive button", Description = "Appearance, size, and enabled state are live knobs — Button is an element factory, so a knob change re-bakes the tree with no remount.")]
    static Element Interactive(Knobs k)
    {
        var appearance = k.Choice("Appearance", ["Standard", "Accent", "Subtle", "Outline"], 1);
        var size = k.Choice("Size", ["Small", "Medium", "Large"], 1);
        var disabled = k.Toggle("Disabled");
        return Button.Create("Click me", () => { }, (ButtonAppearance)appearance.Value, (ControlSize)size.Value, isEnabled: !disabled.Value);
    }

    [Sample("Appearance axis (Standard / Accent / Subtle / Outline)")]
    static Element AppearanceAxis() => Wrap(8,
        Button.Create("Standard", () => { }, ButtonAppearance.Standard),
        Button.Create("Accent", () => { }, ButtonAppearance.Accent),
        Button.Create("Subtle", () => { }, ButtonAppearance.Subtle),
        Button.Create("Outline", () => { }, ButtonAppearance.Outline));

    [Sample("Size axis (Small / Medium / Large)")]
    static Element SizeAxis() => HStack(8,
        Button.Create("Small", () => { }, ButtonAppearance.Accent, ControlSize.Small),
        Button.Create("Medium", () => { }, ButtonAppearance.Accent, ControlSize.Medium),
        Button.Create("Large", () => { }, ButtonAppearance.Accent, ControlSize.Large));

    [Sample("Leading-icon (glyph) slot")]
    static Element IconSlot() => HStack(8,
        Button.Create("Add item", () => { }, ButtonAppearance.Accent, glyph: Icons.Add),
        Button.Create("Copy", () => { }, ButtonAppearance.Standard, glyph: Icons.Copy));
}

[GalleryPage("DropDownButton", "DropDownButton", "Basic input", Icon = Icons.More)]
sealed partial class DropDownButtonControlPage : Component
{
    static readonly Signal<string> _pick = new("—");

    public override Element Render() => GalleryPage.Shell("DropDownButton", "A button that displays a flyout of choices when clicked.",
        ExampleCard.Show(MenuSample));

    [Sample("A DropDownButton with a menu flyout", Description = "The 'Show selection' knob adds a live readout of the chosen item.")]
    static Element Menu(Knobs k)
    {
        var showSel = k.Toggle("Show selection", true);
        var items = new List<MenuFlyoutItem>
        {
            new("Small", Icons.Tag, true, () => _pick.Value = "Small"),
            new("Medium", Icons.Tag, true, () => _pick.Value = "Medium"),
            new("Large", Icons.Tag, true, () => _pick.Value = "Large"),
            MenuFlyoutItem.Separator,
            new("Disabled", Icons.Cancel, false, null),
        };
        return VStack(8,
            DropDownButton.Create("Sizes", items, Icons.Font),
            showSel.Value ? GalleryPage.LiveText(() => $"Chosen: {_pick.Value}") : new BoxEl());
    }
}

[GalleryPage("HyperlinkButton", "HyperlinkButton", "Basic input", Icon = Icons.Share)]
sealed partial class HyperlinkButtonControlPage : Component
{
    static int _clicks;
    static readonly Signal<int> _count = new(0);

    public override Element Render() => GalleryPage.Shell("HyperlinkButton", "A button that appears as hyperlink text and can navigate.",
        ExampleCard.Show(ClickSample),
        ExampleCard.Show(NavigateSample));

    [Sample("A HyperlinkButton", Description = "The 'Show count' knob toggles an activation readout.")]
    static Element Click(Knobs k)
    {
        var showCount = k.Toggle("Show count", true);
        return VStack(8,
            HyperlinkButton.Create("Open the design docs", () => _count.Value = ++_clicks),
            showCount.Value ? GalleryPage.LiveText(() => $"Activated {_count.Value}×") : new BoxEl());
    }

    [Sample("NavigateUri form")]
    static Element Navigate() => HyperlinkButton.Create("fluent-gpu on GitHub", "https://github.com/christosk92/fluent-gpu");
}

[GalleryPage("RepeatButton", "RepeatButton", "Basic input", Icon = Icons.Refresh)]
sealed partial class RepeatButtonControlPage : Component
{
    static readonly Signal<int> _count = new(0);

    public override Element Render() => GalleryPage.Shell("RepeatButton", "A button that raises Click events repeatedly while it is pressed and held.",
        ExampleCard.Show(HoldSample));

    [Sample("Hold to increment", Description = "The 'Step' knob changes the increment each held tick applies (read at click time — no re-render).")]
    static Element Hold(Knobs k)
    {
        var step = k.Choice("Step", ["1", "5", "10"], 0);
        int Step() => step.Value == 2 ? 10 : step.Value == 1 ? 5 : 1;
        return VStack(8,
            HStack(8,
                RepeatButton.Create("–", () => _count.Value -= Step()),
                RepeatButton.Create("+", () => _count.Value += Step())),
            GalleryPage.LiveText(() => $"Value: {_count.Value}"));
    }
}

[GalleryPage("ToggleButton", "ToggleButton", "Basic input", Icon = Icons.Accept)]
sealed partial class ToggleButtonControlPage : Component
{
    static readonly Signal<CheckState> _tri = new(CheckState.Unchecked);

    public override Element Render() => GalleryPage.Shell("ToggleButton", "A button that can be switched between two states (or a third, indeterminate, state).",
        ExampleCard.Show(TwoStateSample),
        ExampleCard.Show(ThreeStateSample));

    [Sample("Two-state", Description = "The 'On' knob and the button share one signal — flip either and both follow.")]
    static Element TwoState(Knobs k)
    {
        var on = k.Toggle("On");
        return ToggleButton.Create(on.Value ? "On" : "Off", on);
    }

    [Sample("Three-state")]
    static Element ThreeState() => VStack(6,
        ToggleButton.Create($"{_tri.Value}", _tri),
        GalleryPage.LiveText(() => $"{_tri.Value}"));
}

[GalleryPage("SplitButton", "SplitButton", "Basic input", Icon = Icons.More)]
sealed partial class SplitButtonControlPage : Component
{
    static readonly Signal<string> _msg = new("—");

    public override Element Render() => GalleryPage.Shell("SplitButton", "A two-part button: a primary action plus a dropdown of related choices.",
        ExampleCard.Show(PasteSample));

    [Sample("A SplitButton", Description = "The 'Show action' knob toggles a readout of the last invoked action.")]
    static Element Paste(Knobs k)
    {
        var showAction = k.Toggle("Show action", true);
        var items = new List<MenuFlyoutItem>
        {
            new("Paste as text", Icons.Document, true, () => _msg.Value = "Paste as text"),
            new("Paste special", Icons.Document, true, () => _msg.Value = "Paste special"),
        };
        return VStack(8,
            SplitButton.Create("Paste", () => _msg.Value = "Paste (primary)", items, Icons.Document),
            showAction.Value ? GalleryPage.LiveText(() => _msg.Value) : new BoxEl());
    }
}

[GalleryPage("ToggleSplitButton", "ToggleSplitButton", "Basic input", Icon = Icons.More)]
sealed partial class ToggleSplitButtonControlPage : Component
{
    static readonly Signal<string> _style = new("List");

    public override Element Render() => GalleryPage.Shell("ToggleSplitButton", "A SplitButton whose primary part toggles on and off.",
        ExampleCard.Show(ListSample));

    [Sample("A ToggleSplitButton", Description = "The 'On' knob and the primary part share one signal.")]
    static Element List(Knobs k)
    {
        var on = k.Toggle("On");
        var items = new List<MenuFlyoutItem>
        {
            new("Bulleted list", Icons.List, true, () => _style.Value = "Bulleted"),
            new("Numbered list", Icons.List, true, () => _style.Value = "Numbered"),
        };
        return VStack(8,
            ToggleSplitButton.Create("List", items, on, glyph: Icons.List),
            GalleryPage.LiveText(() => $"On: {on.Value} · {_style.Value}"));
    }
}

[GalleryPage("CheckBox", "CheckBox", "Basic input", Icon = Icons.Accept)]
sealed partial class CheckBoxControlPage : Component
{
    static readonly Signal<CheckState> _tri = new(CheckState.Indeterminate);
    static readonly Signal<bool> _c1 = new(true);
    static readonly Signal<bool> _c2 = new(false);
    static readonly Signal<CheckState> _parent = new(CheckState.Indeterminate);   // stable (bind freezes at mount)

    public override Element Render() => GalleryPage.Shell("CheckBox", "A control for selecting or clearing options — two-state, or three-state with an indeterminate value.",
        ExampleCard.Show(TwoStateSample),
        ExampleCard.Show(ThreeStateSample),
        ExampleCard.Show(SelectAllSample));

    [Sample("Two-state", Description = "The 'Checked' knob and the CheckBox share one signal.")]
    static Element TwoState(Knobs k)
    {
        var isChecked = k.Toggle("Checked");
        return CheckBox.Create("I agree", isChecked);
    }

    [Sample("Three-state")]
    static Element ThreeState() => VStack(6,
        CheckBox.Create("Mixed", _tri),
        GalleryPage.LiveText(() => $"{_tri.Value}"));

    [Sample("Select all (indeterminate parent)", Description = "The parent is a derived tri-state pushed into a controlled signal; a click runs ToggleAll via onChange.")]
    static Element SelectAll()
    {
        _parent.Value = _c1.Value && _c2.Value ? CheckState.Checked
                      : _c1.Value || _c2.Value ? CheckState.Indeterminate
                      : CheckState.Unchecked;
        void ToggleAll() { bool all = !(_c1.Value && _c2.Value); _c1.Value = all; _c2.Value = all; }
        return VStack(4,
            CheckBox.Create("Select all", _parent, _ => ToggleAll()),
            new BoxEl { Margin = new Edges4(24, 0, 0, 0), Direction = 1, Gap = 4, Children = [CheckBox.Create("Option A", _c1), CheckBox.Create("Option B", _c2)] });
    }
}

[GalleryPage("ColorPicker", "ColorPicker", "Basic input", Icon = Icons.Brush)]
sealed partial class ColorPickerControlPage : Component
{
    static readonly Signal<ColorF> _color = new(ColorF.FromRgba(0x4C, 0xC2, 0xFF));

    public override Element Render() => GalleryPage.Shell("ColorPicker", "A spectrum, hue and alpha selector with channel/hex input.",
        ExampleCard.Show(WithAlphaSample));

    [Sample("A ColorPicker with alpha", Description = "The swatch and hex readout ride compositor-only bindings on the picker's color signal — no re-render while dragging.")]
    static Element WithAlpha(Knobs k)
    {
        var showHex = k.Toggle("Show hex", true);
        return HStack(20,
            ColorPicker.Create(_color, alphaEnabled: true),
            VStack(6,
                new BoxEl { Width = 96, Height = 40, Corners = Radii.ControlAll, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f, Fill = _color },
                showHex.Value ? GalleryPage.LiveText(() => "#" + _color.Value.ToHex()) : new BoxEl()));
    }
}

[GalleryPage("ComboBox", "ComboBox", "Basic input", Icon = Icons.List)]
sealed partial class ComboBoxControlPage : Component
{
    static readonly string[] Fonts = { "Segoe UI", "Cascadia Code", "Arial", "Calibri", "Consolas", "Georgia" };
    static readonly Signal<int> _editSel = new(-1);
    static readonly Signal<string> _editText = new("");

    public override Element Render() => GalleryPage.Shell("ComboBox", "A drop-down list of items a user can select from — optionally with an editable text field.",
        ExampleCard.Show(BasicSample),
        ExampleCard.Show(EditableSample));

    [Sample("A ComboBox", Description = "The 'Selection' knob and the ComboBox share one Signal<int> — pick in either.")]
    static Element Basic(Knobs k)
    {
        var sel = k.Choice("Selection", Fonts, 0);
        return VStack(8,
            ComboBox.Create(Fonts, sel, placeholder: "Pick a font"),
            GalleryPage.LiveText(() => sel.Value >= 0 ? Fonts[sel.Value] : "—"));
    }

    [Sample("An editable ComboBox")]
    static Element Editable() => VStack(8,
        ComboBox.Create(Fonts, _editSel, editable: true, text: _editText, placeholder: "Type or pick"),
        GalleryPage.LiveText(() => _editText.Value.Length > 0 ? _editText.Value : "—"));
}

[GalleryPage("RadioButton", "RadioButton", "Basic input", Icon = Icons.FavoriteStar)]
sealed partial class RadioButtonControlPage : Component
{
    static readonly string[] Options = { "Small", "Medium", "Large" };

    public override Element Render() => GalleryPage.Shell("RadioButton", "A control that lets a user select a single option from a mutually-exclusive set.",
        ExampleCard.Show(GroupSample));

    [Sample("A RadioButtons group", Description = "The 'Selection' knob and the group share one Signal<int>.")]
    static Element Group(Knobs k)
    {
        var sel = k.Choice("Selection", Options, 1);
        return VStack(8,
            RadioButton.Group(Options, sel),
            GalleryPage.LiveText(() => $"Selected: {(sel.Value >= 0 ? Options[sel.Value] : "—")}"));
    }
}

[GalleryPage("RatingControl", "RatingControl", "Basic input", Icon = Icons.Star)]
sealed partial class RatingControlControlPage : Component
{
    static readonly FloatSignal _ro = new(4f);

    public override Element Render() => GalleryPage.Shell("RatingControl", "Lets a user rate with a row of stars, set by click or drag.",
        ExampleCard.Show(InteractiveSample),
        ExampleCard.Show(ReadOnlySample));

    [Sample("Interactive", Description = "The 'Rating' knob slider and the stars share one FloatSignal — drag either.")]
    static Element Interactive(Knobs k)
    {
        var val = k.Slider("Rating", 3f, 0f, 5f, 1f);
        return VStack(8,
            RatingControl.Create(val),
            GalleryPage.LiveText(() => $"{(int)val.Value} / 5"));
    }

    [Sample("Read-only")]
    static Element ReadOnly() => RatingControl.Create(_ro, readOnly: true);
}

[GalleryPage("Slider", "Slider", "Basic input", Icon = Icons.Volume)]
sealed partial class SliderControlPage : Component
{
    static readonly FloatSignal _range = new(50f);
    static readonly FloatSignal _ticks = new(40f);
    static readonly FloatSignal _vert = new(30f);

    public override Element Render() => GalleryPage.Shell("Slider", "Selects a value from a range — with optional ticks, step snapping and vertical orientation.",
        ExampleCard.Show(SignalBoundSample),
        ExampleCard.Show(RangedSample),
        ExampleCard.Show(TicksSample),
        ExampleCard.Show(VerticalSample));

    [Sample("A simple slider (0–1, signal-bound)", Description = "The 'Value' knob slider and the example slider share one FloatSignal — drags update the thumb/track via compositor bindings, no re-render.")]
    static Element SignalBound(Knobs k)
    {
        var value = k.Slider("Value", 0.4f);
        return VStack(8,
            Slider.Create(value),
            GalleryPage.LiveText(() => $"{value.Value:0.00}"));
    }

    [Sample("A ranged slider (0–100)")]
    static Element Ranged() => VStack(8,
        Slider.Create(_range, options: new Slider.SliderOptions { Min = 0, Max = 100 }),
        GalleryPage.LiveText(() => $"{_range.Value:0}"));

    [Sample("Ticks + step (step 10)")]
    static Element Ticks() => VStack(8,
        Slider.Create(_ticks, options: new Slider.SliderOptions { Min = 0, Max = 100, Step = 10, TickFrequency = 10 }),
        GalleryPage.LiveText(() => $"{_ticks.Value:0}"));

    [Sample("Vertical")]
    static Element Vertical() => VStack(8,
        Slider.Create(_vert, options: new Slider.SliderOptions { Min = 0, Max = 100, Vertical = true }, length: 160f),
        GalleryPage.LiveText(() => $"{_vert.Value:0}"));
}

[GalleryPage("ToggleSwitch", "ToggleSwitch", "Basic input", Icon = Icons.Settings)]
sealed partial class ToggleSwitchControlPage : Component
{
    public override Element Render() => GalleryPage.Shell("ToggleSwitch", "A switch that a user can turn on and off.",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(HeaderSample));

    [Sample("A simple ToggleSwitch", Description = "The 'On' knob and the switch share one signal.")]
    static Element Simple(Knobs k)
    {
        var a = k.Toggle("On", true);
        return VStack(8,
            ToggleSwitch.Create(a),
            GalleryPage.LiveText(() => a.Value ? "On" : "Off"));
    }

    [Sample("With header + On/Off content")]
    static Element Header(Knobs k)
    {
        var b = k.Toggle("Wi-Fi");
        return VStack(8,
            ToggleSwitch.Create(b, header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected"),
            GalleryPage.LiveText(() => b.Value ? "Connected" : "Disconnected"));
    }
}
