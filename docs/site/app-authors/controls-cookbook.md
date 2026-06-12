# Controls cookbook

Recipes for the controls in `FluentGpu.Controls`, grounded in the real surface and the verified gallery snippets.
Every example here mirrors code that ships and runs in the control gallery (`src/FluentGpu.WindowsApp/*`).

This page is task-oriented. For the model behind it — how a change reaches pixels through a signal, and the three
update paths — read the guide first: **[reactivity](../../guide/reactivity.md)** and
**[elements, layout, controls & theming](../../guide/components-elements-layout.md)**. For the parts contract and the
honest WinUI-fidelity notes, see **[control fidelity](../../guide/control-fidelity.md)**. When something misbehaves,
**[pitfalls](../../guide/pitfalls.md)** is the symptom → cause → fix table.

> Authoring imports for every snippet on this page:
> ```csharp
> using FluentGpu.Controls;                 // Button, Slider, ComboBox, …
> using FluentGpu.Dsl;                       // BoxEl, TextEl, Tok, TemplateParts, Edges4, ColorF
> using FluentGpu.Hooks;                     // Component, UseState, UseContext, Embed
> using FluentGpu.Signals;                   // Signal<T>, FloatSignal, Prop
> using static FluentGpu.Dsl.Ui;             // VStack, HStack, Card, Icon, …
> ```

## How controls work

Three facts cover almost everything you'll do with the controls layer.

**Most controls are element-returning factories; a few are `Component`s.** `Button`, `Slider`, `CheckBox`,
`ToggleSwitch`, `IconButton`, `RadioButton`, `ScrollBar` and the rest expose **static factory methods** you call inside
`Render()` — they return an `Element` (usually a `BoxEl`) you drop into your tree. A control that needs cross-render
state of its own (an open popup, a hovered preview, a tooltip overlay) is a `Component` whose factory wraps it in
`Embed.Comp` for you: `ComboBox`, `RatingControl`, `ColorPicker`, `NumberBox`, `DropDownButton`, `SplitButton`,
`DatePicker`, `TimePicker`, `CalendarView`. You still just call `.Create(...)`; the wrapping is internal. The practical
consequence is the universal rule from [pitfalls](../../guide/pitfalls.md): **a control's runtime-changeable inputs flow
through signals**, not constructor args — pass a `Signal<int>`/`FloatSignal` for a value a page reads back, and the
control updates without you re-creating it.

**Every control has a `Style` record + a global override, and reads theme tokens by default.** A control's resting,
hover, pressed and disabled colours all resolve from `Tok.*` in its computed default style, so it themes correctly out
of the box. Three ways to change the look, cheapest first:

```csharp
// 1) Per-instance: pass a Style (or `with`-tweak the default).
var green = Button.AccentStyle with
{
    Background      = ColorF.FromRgba(0x10, 0x7C, 0x10),
    Foreground      = ColorF.FromRgba(255, 255, 255),
    CornerRadius    = 6f,
    HoverBackground = ColorF.FromRgba(0x16, 0x95, 0x16),
};
Button.Accent("Save", OnSave, green);

// 2) Global: replace the default style for ALL instances (set once at startup).
Button.AccentStyleOverride = green;          // every Button.Accent now uses it
Slider.StyleOverride       = mySliderStyle;

// 3) Ad-hoc: chain a modifier — a `with`-copy of that one instance, no Style fork.
Button.Accent("OK", OnOk).Rounded(20);
```

Modifiers (`.Background`, `.Rounded`, `.Pad`, `.Foreground`, `.Alpha`, …) return a `with`-copy of the returned element,
so `Button.Accent(...).Rounded(20)` tweaks that one button without disturbing the default style. To restyle a control's
**internals** (its rail, its chevron, its check square) there is exactly one door — `TemplateParts` — covered in
[Restyling a control's internals](#restyling-a-controls-internals-with-templateparts) at the end.

Read `Tok.*` for colours; never hard-code (`Tok.AccentDefault`, `Tok.TextPrimary`, `Tok.FillCardDefault`,
`Tok.StrokeControlDefault`). They follow `Tok.Use(theme)` and the injected OS accent.

---

## Buttons & commands

`Button.Accent` is the primary (accent-filled) button; `Button.Standard` is the neutral one. Both take a label and an
`Action`. Hover/press visual states are automatic — the renderer drives them; there is no extra wiring.

```csharp
var (clicks, setClicks) = UseState(0);

HStack(8,
    Button.Standard("Standard", () => setClicks(clicks + 1)),
    Button.Accent("Accent", () => setClicks(clicks + 1)))
```

**Disabled** is a logical state, not handler-nulling: pass `isEnabled: false`. The engine gate stops hit-test, focus
and keyboard, and the resting fill/foreground swap to the WinUI disabled tokens.

```csharp
Button.Standard("Disabled", () => { }, isEnabled: false)
```

**A custom accent style** — `with`-tweak `Button.AccentStyle` (the green Save button above is the gallery's
`ButtonsPage` example). The `Style` record carries the full WinUI 4-state matrix (`Background`/`HoverBackground`/
`PressedBackground`/`DisabledBackground`, the matching foregrounds, and `BorderBrush` as a gradient), plus geometry
(`CornerRadius`, `Padding`, `MinHeight`, `FontSize`) — restyle as much or as little as you like.

**IconButton** is the compact glyph/transport button (a rounded square, a 16px glyph, subtle fills). Pass a glyph
const from `Icons` and a click handler:

```csharp
HStack(8,
    IconButton.Create(Icons.Previous, OnPrev),
    IconButton.Create(Icons.Play, OnPlay,
        IconButton.DefaultStyle with { Size = 44f, Foreground = Tok.AccentDefault }),
    IconButton.Create(Icons.Next, OnNext),
    IconButton.Create(Icons.More, OnMore))
```

**ToggleButton** is a controlled two- or three-state toggle (accent-filled when on). You own the state; pass it back in
each render. The two-arg overload is two-state; the `CheckState` overload cycles Unchecked → Checked → Indeterminate.

```csharp
// Two-state
var (on, setOn) = UseState(false);
ToggleButton.Create(on ? "On" : "Off", on, () => setOn(!on))

// Three-state (clicks cycle through the indeterminate look)
var (tri, setTri) = UseState(CheckState.Unchecked);
ToggleButton.Create($"{tri}", tri, setTri)
```

---

## DropDownButton, SplitButton, HyperlinkButton, RepeatButton

These four round out the WinUI button family. The menu items for the first two are `MenuFlyoutItem` records
(`Label`, optional `Glyph`, `Enabled`, `Invoke`), with `MenuFlyoutItem.Separator` for a divider.

**DropDownButton** opens a `MenuFlyout` of choices on click. The third positional argument is an optional **leading**
glyph (the chevron is drawn automatically):

```csharp
var (pick, setPick) = UseState("—");
var items = new List<MenuFlyoutItem>
{
    new("Small",  Icons.Tag, true, () => setPick("Small")),
    new("Medium", Icons.Tag, true, () => setPick("Medium")),
    new("Large",  Icons.Tag, true, () => setPick("Large")),
    MenuFlyoutItem.Separator,
    new("Disabled", Icons.Cancel, false, null),
};

DropDownButton.Create("Sizes", items, Icons.Font)
```

**SplitButton** is a two-part button: a primary action plus a dropdown of related choices. The first `Action` is the
primary invoke; the list is the dropdown. (There is also a `ToggleSplitButton` whose primary part toggles on/off,
taking a `Signal<bool>`.)

```csharp
var (msg, setMsg) = UseState("—");
var items = new List<MenuFlyoutItem>
{
    new("Paste as text", Icons.Document, true, () => setMsg("Paste as text")),
    new("Paste special", Icons.Document, true, () => setMsg("Paste special")),
};

SplitButton.Create("Paste", () => setMsg("Paste (primary)"), items, Icons.Document)
```

**HyperlinkButton** is accent-coloured link text with a hand cursor. The click-handler form raises Click; the
`navigateUri` form additionally launches the OS default handler after Click (through the PAL `OpenUri` seam — headless
hosts record the URI instead of launching):

```csharp
// Click-handler form:
HyperlinkButton.Create("Open the design docs", () => setClicks(clicks + 1))

// NavigateUri form (opens in the default browser):
HyperlinkButton.Create("fluent-gpu on GitHub", "https://github.com/christosk92/fluent-gpu")
```

**RepeatButton** raises Click repeatedly while held (WinUI defaults: 500 ms delay, then a 33 ms interval; a held Space
arms the same timer). Same `Create(label, onClick)` shape — the auto-repeat is the whole control:

```csharp
var (count, setCount) = UseState(0);

HStack(8,
    RepeatButton.Create("–", () => setCount(count - 1)),
    RepeatButton.Create("+", () => setCount(count + 1)))
```

---

## Selection: CheckBox, RadioButton, ToggleSwitch

**CheckBox** is controlled — you own the state. The `bool` overload is two-state; the `CheckState` overload is
three-state and cycles Unchecked → Checked → Indeterminate on click.

```csharp
// Two-state
var (a, setA) = UseState(false);
CheckBox.Create("I agree", a, () => setA(!a))

// Three-state
var (tri, setTri) = UseState(CheckState.Indeterminate);
CheckBox.Create("Mixed", tri, setTri)
```

**Select-all** is just the parent reflecting its children — derive the parent's `CheckState` from the child flags and
let its click set them all. This is the gallery's `CheckBoxControlPage` recipe verbatim:

```csharp
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
```

**RadioButton.Group** is the easy path for a mutually-exclusive set: pass the option labels, the selected index, and an
`Action<int>` (single radios exist via `RadioButton.Create`, but a group handles the roving focus and exclusivity for
you). `horizontal: true` lays them in a row.

```csharp
static readonly string[] Options = { "Small", "Medium", "Large" };
var (sel, setSel) = UseState(1);

RadioButton.Group(Options, sel, setSel)
```

**ToggleSwitch** is a controlled on/off switch (drag-to-toggle and tap both work). Optional `header` and
`onContent`/`offContent` side labels:

```csharp
// Simple
var (a, setA) = UseState(true);
ToggleSwitch.Create(a, () => setA(!a))

// With header + On/Off content
ToggleSwitch.Create(b, () => setB(!b),
    header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected")
```

---

## Value inputs: Slider, ScrollBar, RatingControl, ColorPicker, NumberBox

### Slider — prefer `Bind` for scrubbers

The Slider has three factories, and **which one you pick is a performance decision**:

- **`Slider.Bind(FloatSignal value, …)`** — the signals-native, 0..1 hot path. A drag writes the `FloatSignal`, which
  updates only the value-fill's and thumb's composited transforms on the compositor fast path: **zero render, zero
  reconcile, zero relayout per pointer-move.** This is the slider-tank fix. Use it for media seek and volume.
- **`Slider.Create(float value, Action<float> onChange, …)`** — the controlled (React-style) 0..1 variant. Each move
  calls `onChange`, which re-renders the owning component. Fine for low-frequency use; it will tank FPS if dragged hard.
- **`Slider.Ranged(float value, Action<float> onChange, Slider.Options o, …)`** — the full WinUI-parity control over an
  arbitrary range, with step snapping, ticks, vertical orientation, a header, the full keyboard map and the thumb value
  tooltip.

```csharp
// Hot path: bind a FloatSignal — drags update the thumb/track via compositor
// bindings, with NO component re-render per move.
var basic = UseFloatSignal(0.4f);
Slider.Bind(basic)

// Ranged (0–100), controlled
var (range, setRange) = UseState(50f);
Slider.Ranged(range, setRange, new Slider.Options { Min = 0, Max = 100 })

// Ticks + step snapping (step 10)
Slider.Ranged(ticks, setTicks,
    new Slider.Options { Min = 0, Max = 100, Step = 10, TickFrequency = 10 })

// Vertical
Slider.Ranged(vert, setVert,
    new Slider.Options { Min = 0, Max = 100, Vertical = true }, length: 160f)
```

Reading the bound value elsewhere stays compositor-cheap too — drive a readout off the same signal with a bound text
prop so only the text node updates:

```csharp
var basic = UseFloatSignal(0.4f);
VStack(8,
    Slider.Bind(basic),
    new TextEl("") { Text = Prop.Of(() => $"{basic.Value:0.00}") });   // no page re-render per move
```

If a slider drag tanks FPS, the cause is almost always `Slider.Create` (a `setState` per move) — switch to
`Slider.Bind`. See [pitfalls → Dragging a slider tanks FPS](../../guide/pitfalls.md).

### ScrollBar

A standalone scrollbar thumb: `fraction` is the page size (0..1), `position` is the normalized scroll position, and
`onScroll` reports the new position. (Most scrolling uses `Ui.ScrollView`, which clips and offsets by a transform with
no relayout — this control is the bare bar.)

```csharp
var (pos, setPos) = UseState(0f);
ScrollBar.Create(0.3f, pos, setPos, 160f, ScrollBar.DefaultStyle with { ThumbWidth = 10f })
```

### RatingControl

A row of stars set by click or press-and-sweep. The value is a caller-owned `FloatSignal` (so a page can read it);
`-1` means unset. `readOnly: true` shows a fixed rating with no interaction.

```csharp
var val = UseFloatSignal(3f);
RatingControl.Create(val)

// Read-only
RatingControl.Create(UseFloatSignal(4f), readOnly: true)
```

### ColorPicker

A saturation/value spectrum, hue rail, optional alpha rail, preview swatch and Hex/R/G/B fields. The selected colour is
a caller `Signal<ColorF>`. Because it's a signal, you can drive a preview swatch off it with a compositor-only fill
binding — no re-render while dragging:

```csharp
var color = UseSignal(ColorF.FromRgba(0x4C, 0xC2, 0xFF));

VStack(6,
    ColorPicker.Create(color, alphaEnabled: true),
    // The swatch rides a compositor-only fill binding:
    new BoxEl { Width = 96, Height = 40, Corners = Radii.ControlAll, Fill = color });
```

### NumberBox

An editable numeric field. The value is a caller `Signal<double>` (`NaN` = cleared); range, step sizes, a spin-button
placement, expression evaluation and a custom formatter are all parameters.

```csharp
// Plain numeric field (no spinners — the WinUI default)
var qty = UseSignal(1.0);
NumberBox.Create(value: qty, minimum: 0, maximum: 99)

// Inline up/down spinners + Ctrl-expression evaluation
NumberBox.Create(
    value: qty, smallChange: 1, largeChange: 10,
    spinButtonPlacementMode: NumberBoxSpinButtonPlacementMode.Inline,
    acceptsExpression: true)
```

---

## Pickers & combos: ComboBox, CalendarView, DatePicker, TimePicker

**ComboBox** is a closed field that opens a dropdown list. Selection is a caller `Signal<int>` (the **selected index**)
so a page can read it. Pass the items and the index signal:

```csharp
static readonly string[] Fonts = { "Segoe UI", "Cascadia Code", "Arial", "Calibri", "Consolas", "Georgia" };
var sel = UseSignal(0);

ComboBox.Create(Fonts, sel, placeholder: "Pick a font")
```

**Editable ComboBox** adds search-as-you-type, auto-complete and a custom-text path. Pass `editable: true` and a
`Signal<string>` for the live text alongside the index signal:

```csharp
var editSel  = UseSignal(-1);
var editText = UseSignal("");

ComboBox.Create(Fonts, editSel, editable: true, text: editText, placeholder: "Type or pick")
```

**CalendarView** shows a full month and lets the user pick a date. The parameterless `Create()` owns its displayed
month and selected day internally; the `Create(Signal<DateOnly?> selectedDate, …)` overload binds the selection to a
signal and accepts min/max dates, the selection mode, first-day-of-week and display mode. The bordered box is the WinUI
`CalendarViewBorder` chrome:

```csharp
new BoxEl
{
    Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
    Children = [CalendarView.Create()],
}
```

**DatePicker** picks a date with month/day/year columns. The selection is a `Signal<DateOnly?>`. Each column can be
hidden (`dayVisible`/`monthVisible`/`yearVisible`) and the year range constrained (`minYear`/`maxYear`):

```csharp
var date = UseSignal<DateOnly?>(null);

DatePicker.Create(date)                                   // all three columns
DatePicker.Create(noYear, yearVisible: false)             // hide the year column
DatePicker.Create(ranged, minYear: 2020, maxYear: 2030)   // constrain the year range
```

**TimePicker** picks a time with hour/minute/AM-PM columns. The parameterless `Create()` owns its state; the
`Create(Signal<TimeOnly?> selectedTime, …)` overload binds it and accepts a `clockIdentifier` and `minuteIncrement`.
(There's also **CalendarDatePicker** — a field that drops a `CalendarView` in a light-dismiss flyout, taking a
`Signal<DateOnly?> date`.)

```csharp
TimePicker.Create()
```

---

## Text input: TextBox, PasswordBox, AutoSuggestBox

**TextBox** is a single-line field (multi-line with `acceptsReturn: true`). Two-way text is a `Signal<string>`; an
optional `header`/`description`, `maxLength`, `isReadOnly` and an `onCommit` (Enter) callback round it out. Type to
edit; Enter commits, Esc cancels.

```csharp
// Simplest
TextBox.Create("Enter your name")

// Header + description
TextBox.Create("you@example.com", 280f, "Email",
    description: "We'll only use this to contact you.")

// Two-way text with an Enter commit, and a live readout that does NOT re-render the page
var text = UseSignal("");
var (committed, setCommitted) = UseState("—");
VStack(4,
    TextBox.Create("Type, then press Enter", 280f, text: text, onCommit: setCommitted),
    new TextEl("") { Text = text });          // compositor-only text binding — no re-render per keystroke
```

Other useful TextBox parameters from the gallery: `maxLength: 12` (with a live `text.Value.Length` readout),
`isReadOnly: true`, and `beforeTextChanging: s => s.All(char.IsAsciiDigit)` (the gate receives the proposed full text;
returning false rejects the insertion — typing, paste, IME).

**PasswordBox** masks its content. `revealMode` is `Peek` (default — press-and-hold eye while typing), `Hidden` or
`Visible`; `passwordChar` overrides the mask glyph; the live value is a `Signal<string>` via `password:` for validation.

```csharp
PasswordBox.Create()                                                  // Peek reveal, default mask
PasswordBox.Create("Password", 280f, "Password")                      // with a header
PasswordBox.Create("Enter your PIN", 280f, passwordChar: '#')         // custom mask glyph

// Validate on PasswordChanged
var pw = UseSignal("");
PasswordBox.Create("At least 8 characters", 280f, maxLength: 16, password: pw)
```

**AutoSuggestBox** offers a filtered list as the user types. Pass the suggestion source and a placeholder;
`onSuggestionChosen` (arrow-key or row click) and `onQuerySubmitted` (Enter, search button, or row click) are the
events. `queryIcon: null` drops the search button; `textChanged: (q, reason) => …` carries the WinUI change reason
(debounced 150 ms).

```csharp
string[] fruits = { "Apple", "Apricot", "Banana", "Blueberry", "Cherry",
                    "Grape", "Mango", "Orange", "Peach", "Pear" };

AutoSuggestBox.Create(fruits, "Search fruits")

// With events
AutoSuggestBox.Create(fruits, "Type a fruit, then press Enter",
    onSuggestionChosen: setChosen,
    onQuerySubmitted:   setQuery)
```

---

## Restyling a control's internals with TemplateParts

When the `Style` record and modifiers aren't enough — you need to recolour a control's **rail**, its **chevron**, its
**check square**, its **thumb** — there is exactly **one** generic door: `TemplateParts`. It is the CSS `::part` /
WinUI lightweight-styling analogue, signals-native. New per-control styling knobs are banned by design; if a prop's
only job is to restyle one template part, it must be a Parts modifier instead.

Each control exports `PartXxx` name consts and accepts a trailing `TemplateParts? parts`. You layer **any element
props** onto a named part with a `with` expression. For `BoxEl` parts use the object-initializer indexer; for a
non-`BoxEl` part (a `TextEl` glyph, a stroke mark) use `Set<T>`:

```csharp
var parts = new TemplateParts
{
    // A BoxEl part: recolour the slider rail and round it harder.
    [Slider.PartRail] = b => b with { Fill = Tok.FillControlStrong, Corners = CornerRadius4.All(4f) },
};
// A TextEl part: restyle the combo's chevron glyph.
parts.Set<TextEl>(ComboBox.PartChevron, t => t with { Color = Tok.AccentDefault });

Slider.Bind(volume, parts: parts);
ComboBox.Create(items, sel, parts: parts);
```

The contract (from [control fidelity §6](../../guide/control-fidelity.md)):

- A modifier is a **pure `with`-copy** of its input — never write a signal inside one (reads are fine; a read
  subscribes the owning control to that granular restyle).
- It is **type-preserving**: a modifier that changes the record type is ignored. Parts *style*; content **slots**
  (`Content`, `HeaderContent`) *restructure*.
- The control **re-asserts its mechanics-critical props after your modifier** — toggle clicks, value-position geometry,
  ref captures, the scrub handlers. Each part const's doc lists exactly what it owns. So you can restyle everything and
  break nothing, but you cannot, say, steal the value-position transform off a slider's thumb.
- For per-frame-hot values, set a **bound prop** (`Fill`/`Transform` as a `Func`/signal), not a static value.
- **Do not** put a per-item `TemplateParts` modifier in a recycled scroll path — that's the banned virtualization
  hazard. Per-item variation goes through the `PartDelta` value seam instead (see the collections docs in
  [components-elements-layout](../../guide/components-elements-layout.md)).

The part vocabulary is per control — e.g. `Slider.PartContainer/PartRail/PartValueFill/PartThumb/PartInnerDot/PartTickBar`,
`ComboBox.PartField/PartChevron/PartItemRow/PartItemPill`, `CheckBox.PartRoot/PartBox/PartMark/PartLabel`,
`ToggleSwitch.PartRoot/PartTrack/PartKnobHost/PartKnob/…`. Open the control source to see its consts and their owned-prop
notes.

---

## Where each control's verified snippet lives in the gallery

Every recipe above is mined from a runnable gallery page — read these for the full, compiling context (each builds its
demo with `ControlExample.Build(title, body, output:, code:)`):

| Control(s) | Gallery page (`src/FluentGpu.WindowsApp/…`) |
|---|---|
| Button, IconButton, ToggleButton (extended demos) | `GalleryPages.cs` → `ButtonsPage` |
| Button (standard/accent/disabled) | `ControlGalleryPages.cs` → `ButtonControlPage` |
| DropDownButton | `ControlGalleryPages.cs` → `DropDownButtonControlPage` |
| SplitButton, ToggleSplitButton | `ControlGalleryPages.cs` → `SplitButtonControlPage`, `ToggleSplitButtonControlPage` |
| HyperlinkButton | `ControlGalleryPages.cs` → `HyperlinkButtonControlPage` |
| RepeatButton | `ControlGalleryPages.cs` → `RepeatButtonControlPage` |
| ToggleButton | `ControlGalleryPages.cs` → `ToggleButtonControlPage` |
| CheckBox (+ select-all) | `ControlGalleryPages.cs` → `CheckBoxControlPage` |
| RadioButton.Group | `ControlGalleryPages.cs` → `RadioButtonControlPage` |
| ToggleSwitch | `ControlGalleryPages.cs` → `ToggleSwitchControlPage` |
| Slider (Bind/Ranged/ticks/vertical) | `ControlGalleryPages.cs` → `SliderControlPage` |
| Slider.Create + ScrollBar (controlled demos) | `GalleryPages.cs` → `InputsPage` |
| RatingControl | `ControlGalleryPages.cs` → `RatingControlControlPage` |
| ColorPicker | `ControlGalleryPages.cs` → `ColorPickerControlPage` |
| ComboBox (+ editable) | `ControlGalleryPages.cs` → `ComboBoxControlPage` |
| TextBox | `TextPages.cs` → `TextBoxPage` |
| PasswordBox | `TextPages.cs` → `PasswordBoxPage` |
| AutoSuggestBox | `TextPages.cs` → `AutoSuggestBoxPage` |
| CalendarView, DatePicker, TimePicker, CalendarDatePicker | `DateTimePages.cs` |
| MenuBar, AppBarButton (MenuFlyoutItem usage) | `CollectionsMenusPages.cs` |

For the control catalog's deeper design notes and the honest WinUI-fidelity scorecard, see
[control fidelity](../../guide/control-fidelity.md) and the
[WinUI control parity audit](../../guide/winui-control-parity-audit.md). The architecture/canon authority lives in the
design corpus ([SPEC-INDEX](../../../design/SPEC-INDEX.md)).
