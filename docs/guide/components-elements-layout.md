# Elements, layout, controls & theming

> **✅ Animation engine — signals-first rework landed + verified.** The declarative surface (`Transition`/`WhileHover`/`WhilePressed`/`WhileFocus`/`Enter`/`Exit`/`Stagger`/`Layout` + `UseSpringValue`/`UseAnimatedValue(DepKey)`) is now live, all over the unified `AnimValue` slab + analytical spring. The `Element` animation fields and `UseSpring`/`UseTransition`/`UseKeyframes`/`UseDrivenAnimation` hooks documented below still work (they seed the same slab; `AnimEngine` is its scheduler). Design, now implemented: [`../plans/animation-engine-rework-design.md`](../plans/animation-engine-rework-design.md).

[← Guide index](./README.md) · prerequisite: **[reactivity.md](./reactivity.md)**

## The element zoo

`Element` is an immutable record describing a node — cheap to build, never touches the scene directly. You compose
them in `Render()` and return the root. Build them with the `Ui.*` helpers (terse) or the records directly
(full control).

| Element | Builder | What it is |
|---|---|---|
| `BoxEl` | `VStack`/`HStack`/`Panel`/`ZStack`/`Card`/`Layer`/`Divider`/`Pill` | container + optional surface (fill/border/shadow/gradient/acrylic), the workhorse |
| `TextEl` | `Text`/`Heading`/`Icon` | a text/glyph run |
| `ImageEl` | `Image` | async, cached, residency-pinned bitmap (album art) |
| `ScrollEl` | `ScrollView` | clipping, scrolling viewport over one (oversized) child |
| `GridEl` | `Grid`/`UniformGrid`/`AutoGrid` | CSS-grid container (Pixel/Star/Auto tracks) |
| `VirtualListEl` | `Virtual.List/Grid/VariableList/Custom`, `Repeater.ItemsRepeater` | virtualized collection (10k+ rows) |
| `ComponentEl` | `Embed.Comp(() => new C())`, `Embed.Comp(props, () => new C())` | embeds a child `Component` (2nd form re-pushes live props → `UseProps<T>()`; see [reactivity.md](./reactivity.md#props--re-pushed-to-the-child-embedcompprops-factory)) |
| `ContextProviderEl` | `Ctx.Provide(channel, value, child)` | provides an **ambient** context value (for concrete parent→child data prefer re-pushed props) |
| `ShowEl` / `ForEl` | `Flow.Show` / `Flow.For` | reactive conditional / keyed list |

### `Ui.*` builders (`src/FluentGpu.Engine/Dsl/Factories.cs`)

```csharp
VStack(float gap, params Element[] children)            // column flex
HStack(float gap, params Element[] children)            // row flex
Panel(Edges4 padding, float gap, params Element[] kids) // padded column
ZStack(params Element[] children)                       // overlay (last on top)
Spacer()                                               // flex spacer — grows to eat main-axis slack
Spacer(float px)                                       // rigid fixed gap on the main axis (never grows/shrinks)
Wrap(float gap, params Element[] children)             // horizontal wrap panel (children flow + wrap to lines)
Center(Element child)                                  // centers child on both axes in a box that fills its parent
AspectRatio(float ratio, Element child)                // box sized to width÷height (CSS aspect-ratio); derives the missing extent
Heading(string text)                                   // 28pt bold
Text(string text)                                      // 14pt body
Icon(string glyph, float size = 16, ColorF? color = null, string? family = null)
ScrollView(Element content, bool horizontal = false)
Image(string source, float w, float h, float corners = 0, ColorF? placeholder = null, string? blurHash = null, ImageTransition? transition = null)
Grid(TrackSize[] columns, float colGap, float rowGap, float rowHeight, params Element[] children)
UniformGrid(int columns, float gap, float rowHeight, params Element[] children)
AutoGrid(float minColWidth, float gap, float rowHeight, params Element[] children)   // responsive auto-fill
Card(params Element[] children)                         // Fluent card (fill+stroke+corners+shadow+pad)
Layer(Edges4 pad, params Element[] children)            // flyout/expander surface
Divider(bool vertical = false)                          // 1px hairline
SectionHeader(string title, string? caption = null)
Pill(string label, bool selected, Action onClick)
LinearGradient(float angleDeg, params GradientStop[] stops) / RadialGradient(params GradientStop[] stops)
```

### Modifiers (`src/FluentGpu.Engine/Dsl/Modifiers.cs`) — fluent per-element overrides

```csharp
box.Background(c).HoverColor(c).PressedColor(c).Border(c, width).Rounded(r).Pad(all) / .Pad(l,t,r,b)
   .Shadow(s).Elevate(preset).Gradient(g).BorderBrush(g, width).Acrylic(a)
   .Offset(dx,dy).Scale(s) / .Scale(sx,sy).Rotate(deg).Alpha(opacity)
text.Foreground(c).FontSize(px).Strong().Font(family).Wrapped().NoWrap().Trim().Ellipsis().MaxLines(n)
```

These return a `with`-copy — they don't fork a control's default style, so e.g. `Button.Accent(...).Rounded(20)`
just tweaks that one instance.

### Template parts (`src/FluentGpu.Engine/Dsl/TemplateParts.cs`) — lightweight styling of CONTROL internals

The one generic door into a control's template — CSS `::part` / WinUI lightweight styling, signals-native. Controls
export part-name consts (`Expander.PartHeader/PartChevron/PartClip/PartContent/PartRoot`) and a `Parts` field; app
code layers **any element props** onto any named part with a `with` expression:

```csharp
var stuck = new Signal<bool>(false);
new Expander
{
    Parts = new()
    {
        [Expander.PartHeader] = b => b with
        {
            ScrollBinds =                                      // CSS position:sticky — pin at top: 8px (one generic scroll bind)
            [
                new() { PinTop = 8f,                           //   clamp-to-top at an 8px inset
                        OnFlag = p => stuck.Value = p }        //   the :stuck observable — a PinTop bind's OnFlag fires per pin↔unpin flip
            ],
            Fill = stuck.Value ? Tok.FillSolidBase : b.Fill,   // restyle ANYTHING off the signal — reading subscribes
            BrushTransitionMs = Motion.ControlFast,            // …and the swap cross-fades (implicit brush transition)
        },
        [Expander.PartContent] = c => c with { Padding = Edges4.All(0) },   // edge-to-edge content
    },
}
```

Rules: (1) a modifier is a PURE `with`-copy of its input; (2) signal **reads** inside subscribe the owning control —
the granular `:stuck` restyle loop; never **write** a signal in a modifier, and use bound props (`Fill`/`Transform` set to a Func/signal)
for per-frame-hot values; (3) type-preserving — a modifier that changes the record type is ignored (parts *style*,
content **slots** like `Content`/`HeaderContent` *restructure*); (4) don't reshape `Children`; (5) the control
re-asserts its mechanics-critical props AFTER your modifier (toggle clicks, reflow specs, ref captures — chained, see
each part const's doc for the owned list), so you can restyle everything but break nothing; (6) one transform owner —
don't put a transform-owning `ScrollBinds` entry (a `PinTop` sticky / `StretchFromTop` hero bind) or a bound `Transform`
on a transform-owned part (e.g. the Expander clip mid-reflow). **New per-control
styling knobs are banned**: if a prop's only job is to restyle one template part, it must be a Parts modifier instead.

## Layout

Flexbox (Yoga-style) is the default; CSS-grid is available via `GridEl`. Key `BoxEl` layout props:

| Prop | Type | Notes |
|---|---|---|
| `Direction` | `byte` | 0 = row, 1 = column (default column) |
| `Gap` | `float` | inter-child spacing on the main axis |
| `Padding` / `Margin` | `Edges4` | inner / outer |
| `Width`/`Height`/`MinWidth`/`MinHeight`/`MaxWidth`/`MaxHeight` | `float` | `NaN` = auto/unset |
| `Grow`/`Shrink`/`Basis` | `float` | flex coefficients |
| `Justify` | `FlexJustify` | main-axis distribution |
| `AlignItems` / `AlignSelf` | `FlexAlign` | cross-axis alignment |
| `Wrap` | `bool` | wrap children to lines |
| `AspectRatio` | `float` | width÷height (CSS `aspect-ratio`); `NaN` = off. Derives the missing extent for a fluid box (the `Ui.AspectRatio` helper). One dimension stays unset, so an aspect box is never a layout boundary. |
| `ZStack` | `bool` | children overlay at the origin, painted in order (last on top) |
| `ClipToBounds` | `bool` | clip children — **and a layout-boundary signal** (see performance doc) |

Sizes are in DIP (device-independent pixels); the host scales by the monitor DPI. `Viewport.Size` (via
`UseContext`) gives the client size in DIP for responsive layout.

## Visuals on a `BoxEl`

`Fill`, `HoverFill`, `PressedFill`, `BorderColor`+`BorderWidth`, `Corners` (`CornerRadius4`), `Shadow` (`ShadowSpec`),
`Gradient` (`GradientSpec`, supersedes `Fill`), `BorderBrush` (gradient stroke — WinUI elevation border), `Acrylic`
(`AcrylicSpec`, per-node frosted glass). Composited (animate with no relayout): `OffsetX/OffsetY`, `ScaleX/ScaleY`,
`Rotation`, `Opacity`, plus interaction-driven `HoverScale`/`PressScale` (eased pop on hover/press).

### `.Interactive(recipe)` — the one interactive-styling surface (`src/FluentGpu.Controls/Interaction.cs`)

Instead of hand-wiring `Fill`/`HoverFill`/`PressedFill` + a border ramp + `WhileHover`/`WhilePressed` on every clickable
surface, package them in an `InteractionRecipe` (a value struct) and apply it with one modifier. It's a **HYBRID** —
brushes ride the `BoxEl` field ramp (engine-serviced `HoverFade`/`PressFade`/`BrushFade`), geometry/opacity ride the
declarative `WhileHover`/`WhilePressed` + `Transition` motion token (the `press > focus > hover > rest` resolver):

```csharp
new BoxEl { OnClick = go }.Interactive(Interaction.Subtle);      // transparent → subtle hover/press
new BoxEl { OnClick = go }.Interactive(Interaction.Card);        // card fills + stroke + 0.985 spring press
new BoxEl { OnClick = go }.Interactive(myRecipe, isEnabled: on); // isEnabled=false → Disabled legs, no hover/press

var myRecipe = new InteractionRecipe {                           // build your own
    Fill = new StateBrush(rest, hover, pressed, disabled),
    Stroke = StateBrush.Flat(Tok.StrokeCardDefault), StrokeWidth = 1f,
    HoverScale = 1.04f, PressScale = 0.96f,                       // 1 = none → While* scale
    HoverOpacity = 0.9f,                                         // NaN = none → While* opacity
    BrushMs = 83f, Motion = MotionTokenId.StandardSpring,        // brush cross-fade ms + While* dynamics
};
```

`.Interactive` is a pure `with`-expansion at construction (cold path, zero closures, zero per-frame alloc). Composition
rules it honors:
- **One transform owner.** If the box already carries a **bound `Transform`**, the recipe's `While*` motion half is
  skipped (the bound matrix owns the transform outright — a `While*` scale would fight it). The brush half still applies.
- **No stomping.** A `While*` leg the caller already set is preserved (caller wins), as is a caller-set `Transition`;
  channels the recipe doesn't name are untouched. A recipe with no motion (scales 1, opacities `NaN`) never touches
  `While*`/`Transition` at all.
- **`isEnabled: false`** applies the `Disabled` fill/stroke legs and sets `IsEnabled = false`, so the engine routes no
  hover/press progress (exactly how CheckBox/Button disable their ramps) and the motion half is suppressed.

**Theme-live presets** (get-only, re-read `Tok.*` on every access — a live theme/palette swap re-resolves them):

| Preset | Fill ramp | Stroke | Motion |
|---|---|---|---|
| `Interaction.Subtle` | transparent → `FillSubtleSecondary` → `FillSubtleTertiary` | none | none |
| `Interaction.ListRow` | same as Subtle (separate preset so list tuning can diverge) | none | none |
| `Interaction.Card` | `FillCardDefault` → `FillCardSecondary` | `Flat(StrokeCardDefault)` | `PressScale 0.985` + `StandardSpring` |
| `Interaction.AccentGhost` | transparent → `AccentSubtle` → dimmer `AccentSubtle` | none | none |

> **Presets are an APP-AUTHORING surface. Framework controls keep their own WinUI-exact hand ramps — do NOT restyle a
> control (Button, CheckBox, list item, …) with a preset.** The recipe packages the *app's* clickable surfaces.

## Hooks reference (full)

State/derivation hooks are in **[reactivity.md](./reactivity.md)**. The rest:

### Animation (composited — no re-render, rides the compositor frame)
```csharp
UseSpring(AnimChannel ch, float to, SpringParams spring, params object[] deps)        // springs a channel to `to`
UseTransition(AnimChannel ch, float from, float to, float ms, Easing e, params object[] deps)
UseKeyframes(AnimChannel ch, Keyframe[] keys, float ms, bool loop = false, params object[] deps)
UseDrivenAnimation(AnimChannel ch, Keyframe[] keys, Func<float> source, float min, float max, params object[] deps)
// MotionHooks (extension methods on Component):
this.UseEntrance(float offsetPx = 24, object? key = null)   // Fluent show transition (TranslateY 24→0 + fade)
this.UseHoverScale(bool hovered, float to = 1.02f)          // card/button lift
```
These seed `AnimValue` slab channels on the component's node and animate by re-recording transform/opacity — **never
re-render, never relayout.** Prefer these (or a signal bound to a transform) for motion. `UseAnimatedValue(target, ms,
easing)` returns an eased scalar (lerp a color with it) and `UseSpringValue` its spring-backed sibling; under the
landed slab both advance on the compositor frame, so they are fine for continuous motion too.

### Images
```csharp
UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null) // → ImageBinding (load state)
PrefetchImage(string src, int decodePx)                                                                     // warm before it scrolls in
```
`UseImage` subscribes the component to image-status changes (so a spinner→ready transition re-renders just it). To
actually paint, also use `Ui.Image(src, …)`. Album-art decode is off-thread, cached, residency-pinned while on screen.

## Controls (`src/FluentGpu.Controls/`)

Most controls are **element-returning factories** (call them in render); `NavigationView`/`PageHost` are `Component`s.
Every control has a `Style` record and reads theme tokens by default; most expose a global `…StyleOverride`, while
`Button` exposes the axis-aware `Button.StyleHook` (below).

**One creation idiom.** Every public control exposes exactly ONE canonical `X.Create(…)`; named variants are one-line
forwarders onto it — `Button.Accent/Standard/Subtle/Outline`, `InfoBadge.Dot/Count/Icon` (→ `InfoBadge.Create(kind, …)`),
`ProgressBar`/`ProgressRing`.`Determinate/Indeterminate` (→ `Create(FloatSignal? value = null, …)`, where a **null value =
indeterminate** and a signal = a determinate that tracks it), `NumberBox.CreateWithSpinners`. `Build` is never on the public
surface (`MenuFlyout.Build`→`MenuFlyout.Create`; `ItemContainer`/`CommandBarFlyout` bodies are internal). Controls without a
natural argument list take an **options record**: `NavigationView.Create(NavigationViewOptions)`,
`TitleBar.Create(TitleBarOptions)`, `OverlayHost.Create(Element child)`, and `TextBox`/`NumberBox` `.Create(signal, onChange,
…Options)`. (`NavigationView`/`TitleBar`/`OverlayHost` also keep their property-init fields for the in-repo probes/shells that
compose them directly, but `Create` is the documented path.)

```csharp
// Button = TWO orthogonal axes (Radix/CVA precedent): appearance selects the token ramp, size the geometry.
Button.Create(string label, Action onClick, ButtonAppearance appearance = Standard, ControlSize size = Medium,
              string? glyph = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)  // canonical
Button.Accent / Standard / Subtle / Outline(string label, Action onClick, Style? style = null, …)   // one-line sugar
IconButton.Create(string glyph, Action onClick, Style? style = null, …, ControlSize size = Medium)   // clamps Large→Medium (square)
ToggleButton.Create(string label, Signal<bool>? on = null, Action<bool>? onChange = null, Style? style = null, …, ControlSize size = Medium)
RepeatButton.Create(string label, Action onClick, Style? style = null, …, ControlSize size = Medium)
HyperlinkButton.Create(string text, Action onClick, Style? style = null, …, ControlSize size = Medium)   // no appearance axis (link text, no fill)

Slider.Create(FloatSignal? value = null, Action<float>? onChange = null, SliderOptions? options = null,
              float length = 200, float thickness = 32, Style? = null, bool isEnabled = true, TemplateParts? = null)   // ONE API — signals-native (no re-render) ★
ScrollBar.Create(float fraction, FloatSignal? position = null, Action<float>? onChange = null, float length = 200, …)  // canonical = the full WinUI mouse scrollbar (signals-native); the legacy thin panning indicator is a distinct Create(float,float,Action,…) overload
SplitView.Create(pane, content, Signal<bool>? isPaneOpen = null, Action<bool>? onOpenChanged = null, …)   // controlled two-way pane state; light dismiss writes the signal + fires onOpenChanged
AnimatedIcon.Glyph(string glyph, float size = 16, ColorF? color = null, string? font = null, float hoverScale = 1.08f, float pressScale = 0.88f)
```

**Button variant axes.** `ButtonAppearance { Standard, Accent, Subtle, Outline }` and the kit-shared
`ControlSize { Small, Medium, Large }` are **orthogonal** — they are NOT a flattened product. Appearance dispatches
through one 4-arm `ButtonPalette` switch (the token ramp: fill/foreground/border + BackgroundSizing); size through one
3-arm `ControlMetrics` switch (padding/min-height/font/corner/icon). `Button.DefaultStyle(appearance, size)` composes
them into the same 24-member `Style` record, which survives as the **full-override escape hatch** (pass `style:` to win
outright). Standard/Accent are pixel-identical to before; Subtle uses the WinUI SubtleFillColor* ramp; Outline is a
documented Fluent-2 extension (solid stroke all states). `glyph:` adds a leading icon-font run. To restyle globally,
set `Button.StyleHook = (appearance, size) => …` (return a `Style`, or `null` to fall through to the composed default)
— it replaces the old per-appearance `AccentStyleOverride`/`StandardStyleOverride`. Sibling button-family controls
(`IconButton`/`ToggleButton`/`RepeatButton`/`HyperlinkButton`) adopt `ControlSize` too; a control may **clamp** an axis
value it can't honor (a small per-control table, Radix precedent): `IconButton` clamps `Large`→`Medium` to keep its
square glyph box sane, and `HyperlinkButton` doesn't expose the appearance axis at all (it is link text with no fill
chrome, so Subtle/Outline are meaningless).

### The controlled-input contract (every stateful control)

One uniform contract, so binding any stateful control is the same everywhere:

1. **Signal-in.** The canonical factory takes the controlled value as a **concrete signal** (`Signal<T>` / `FloatSignal`) — e.g. `ToggleSwitch.Create(Signal<bool> isOn)`, `CheckBox.Create(string, Signal<bool>)`, `RadioButtons.Create(items, Signal<int> selectedIndex)`. The control reads that signal *directly* (live) — you do **not** re-render the parent to change the value.
2. **`onChange` sugar.** An optional `Action<T>? onChange = null` runs on user interaction. Order is fixed: the control writes the signal **first**, then invokes `onChange`. A **programmatic** signal write (`sig.Value = …`) re-skins the control with **no** `onChange` echo (and never re-renders the owner) — so app code and user input can't feedback-loop.
3. **Auto-materialize.** Pass no signal and the control creates its own internal one (`isOn = null` ⇒ one code path — "uncontrolled" just means "the control made the signal"). `ToggleSwitch.Create()` toggles on its own; `ToggleSwitch.Create(mySig)` is externally controlled — same code path.
4. **The signal instance freezes at mount** (bind wiring is mount-only). Swap the signal by re-keying the control.
5. **Closed callback-name set:** `onChange` (the controlled value), `onClick` / `onInvoked` (actions), `onCommit` / `onCancel` (editors), `onOpenChanged` (open state). There is no `onToggle` / `onSelect` / `onTextChanged` / `OnValueChanged`; `onChange` receives the NEW value (peek your own signal for the old one). The one documented exception is the **leaf `RadioButton`** (`bool isSelected, Action? onChange`) — the owning group/`RadioButtons` owns the shared selection signal.
```csharp
var on = UseSignal(false);
ToggleSwitch.Create(on, onChange: v => Save(v));   // v is the new value; `on` is live
```

★ **`Slider.Create` is the one slider API** (the old `Slider.Create(float,…)` / `Slider.Bind` / `Slider.Ranged` trio
is gone). It follows the controlled-input contract above: pass a `FloatSignal` (or omit it to auto-materialize), and a
drag writes that signal → the thumb/fill composited transforms update with **zero render/reconcile/layout**, at ANY
range (the slider-tank fix, now the default). `SliderOptions` (null ⇒ 0..1, tooltip enabled) carries range / step
snapping / ticks / vertical / header and the thumb value tooltip — the tooltip binds the same signal, so its readout
updates per-move without a bubble re-render (open/close is per-gesture-edge).

### Navigation
```csharp
var nav = new Navigator(new Route("home"));               // serializable back-stack
new PageHost(nav, route => Pages[route.Name]);            // renders top route; provides Nav.Context
// in any descendant:
var navigator = UseContext(Nav.Context);
navigator?.Push("details", itemId);                       // or .Pop(), .Replace(...)

// or the adaptive left nav:
new NavigationView {
    Items = [new NavItem("home","","Home"), …], Footer = [...], Header = "App",
    Content = key => Pages[key],
    OnSelect = key => { … },
    Navigator = nav,     // optional: select => nav.Replace(new Route(key)); initial selection follows nav.Current
};   // adapts Expanded/Compact/Minimal by Viewport.Size width (1008 / 641 thresholds)
```

#### Registry-driven routing (`RouteRegistry` + `[Route]`)

Instead of a hand-synced page switch, tag each page component with `[Route("key")]` and let the **RouteTableGenerator**
build the table at compile time (zero reflection, AOT-clean). `PageHost.Create(nav, registry)` then resolves the top
route through the registry — with a fallback for unknown keys, `KeepAlive` parking (state survives navigating away and
back), and per-route entrance transitions. One registry is the single source of truth for the page factory, and can
also derive a nav tree (`BuildNavTree`) and a search corpus (`BuildSearchIndex`).

```csharp
// 1. Tag pages (parameterless ctor, or a (Route)/(string) ctor to receive route.Arg):
[Route("home", Title = "Home", Icon = "", Category = "Main")]
sealed class HomePage : Component { public override Element Render() => body; }

[Route("playlist", KeepAlive = true)]
sealed class PlaylistPage : Component {
    public PlaylistPage(string id) { }                 // (string) ctor => gets route.Arg
    public override Element Render() => body;
}

// 2. Build the registry once and host it:
var registry = new RouteRegistry();
Routes.RegisterAll(registry);                          // generated from the [Route] attributes (FluentGpu.Generated)
registry.Add(new RouteDef("about", _ => Embed.Comp(() => new AboutPage())) { KeepAlive = true });  // runtime routes
registry.Fallback = r => Embed.Comp(() => new NotFoundPage());
var nav = new Navigator(new Route("home"));
PageHost.Create(nav, registry);                        // the one-liner router (Nav.Context + KeepAlive + transitions)
```

`RouteDef` carries `{ Title, Icon, Category, Order, ShowInNav, KeepAlive, Transition, SearchTerms }`. `NavTransition` is
`Default | None | Entrance` — `Default`/`Entrance` seed the engine's declarative `Enter`/`Transition` motion tokens on
the page root (a fade + slide-up), `None` snaps. Duplicate keys throw at runtime (`RouteRegistry.Add`) and are a compile
error from the generator (**FGRT001**); a page with no routable ctor is **FGRT002**, a non-`Component` is **FGRT003**.

### Overlays: Popup, Flyout & Toast

All three ride the shared `OverlayHost` (flip/nudge placement, live-anchor follow, focus save/restore, light-dismiss) —
mount one `OverlayHost.Create(root)` near the app root; resolve the service inside with `UseContext(Overlay.Service)`.

```csharp
// Controlled popup — open state owned by a Signal<bool> (the controlled-input contract). Light-dismiss / Escape write
// the signal BACK to false and fire onOpenChanged(false) once; a programmatic close (you set isOpen=false) never echoes.
var open = UseSignal(false);
Popup.Create(Button.Standard("Show", () => open.Value = !open.Value), () => body, open,
             onOpenChanged: v => …, placement: FlyoutPlacement.BottomLeft);

// Event-driven sugar (the ContextMenu.Attach precedent): chains OnClick (never clobbers) to open a content flyout.
var svc = UseContext(Overlay.Service);
Flyout.Attach(myBoxEl, svc, () => body);   // re-click closes via the light-dismiss scrim (no toggle state)
```

**In-app toasts** live in a top-Z lane every `OverlayHost` auto-mounts (dormant when empty); the static API needs no
wiring:

```csharp
ToastHandle h = Toast.Show("Playlist saved.", new ToastOptions {
    Severity = InfoBarSeverity.Success,   // reuses InfoBar's severity visuals (shared SeverityVisuals — can't drift)
    Title = "Saved", DurationMs = 5000,   // 0 = sticky; auto-dismiss rides the host frame-clock timer queue
    ActionLabel = "Undo", OnAction = Undo, Closable = true });
Toast.MaxVisible = 3;   // stacked newest-nearest-edge, 8px gap; overflow waits in a FIFO queue. Toast.Placement = BottomRight.
Toast.CloseAll();       // hovering the strip PAUSES the remaining auto-dismiss time; enter/exit ride the Standard motion tokens.
```

> **Naming:** `FluentGpu.Controls.Toast` is the **in-app** toast (a card in the app window). It is a different type from
> `FluentGpu.WindowsApi.Notifications.Toast`, which raises an **OS notification** (Action Center) — different namespace,
> different surface. Alias one (`using OsToast = FluentGpu.WindowsApi.Notifications.Toast;`) when a file uses both.

**Anchor pinning** (`PopupOptions.PinsAnchor`, default true for anchored flyouts): while such an overlay is open its
anchor's auto-hide *scope* is pinned — auto-hide logic (a chrome idle-hide, a ToolTip dismissal timer) consults
`svc.IsAnchorPinned(scopeNode)` (true when an open pinning overlay is anchored inside that scope) and subscribes to
`svc.PinEpoch` for reactivity, so a picker opened from inside an auto-hiding surface keeps it alive. **Submenu
safe-triangle**: a `MenuFlyout` cascade stays open while the pointer travels from the opening item toward the submenu's
near edge (the WinUI/macOS hover-intent polygon), so a passing sibling hover doesn't close it.

### Virtualization (10k+ rows, bounded live nodes)
Every virtualized layout is a `RepeatLayout` preset; `ItemsView` (below) hosts them all. The low-level substrates:
```csharp
Repeater.ItemsRepeater(int count, Func<int,Element> template, in RepeatLayout layout, Func<int,string>? keyOf = null, int overscan = 4)
//   RepeatLayout.Stack(extent, horizontal) / .Grid(cols,h,gap) / .GridAuto / .GridFit / .Custom(IVirtualLayout)
//     .LinedFlow(lineHeight,…) / .SpanGrid(cols,rowH,gap,spanOf) / .HorizontalGrid(rows,w,gap)
//     .Measured(IMeasuredVirtualLayout) / .VariableList(estimate) / .GroupedList(headerIdx,headerH,itemEstimate)
//     .Wrap(gap) / .Inline(…)   ← non-virtual, for small collections
```
Only the visible window (+overscan) is realized; scrolling recycles row nodes through the slab free-list. Provide a
stable `keyOf` so row state/identity survives recycling. In-window scroll is transform-only (no realize, no relayout).
`Repeater` is the advanced no-selection substrate; most apps want `ItemsView`. (`Virtual.*` are thin low-level
constructors used internally by `Repeater`/`ItemsView`; prefer the `RepeatLayout` presets.)

### Collections — `ItemsView` and its presets
`ItemsView` is the premiere collection control: any `RepeatLayout` × any `SelectionModel` mode × any `SelectorVisual`
chrome × built-in drag-reorder, over one virtualized viewport. The former `ListView`/`GridView` controls are FOLDED
onto it as static presets (the control types no longer exist). The canonical **creation trio** takes the item count/
source + template + layout POSITIONALLY, and everything else in ONE `ListOptions` record:
```csharp
ItemsView.Create(count, itemTemplate, layout, options)                     // templated items (rebuilt per index)
ItemsView.CreateBound(count, rowScope => …, layout, options)               // signals-first bound slots (recycle by index-signal write)
ItemsView.CreateBound<T>(BoundItemsSource<T> items, scope => …, layout, options)   // typed bound slots (ListOptions<T>)
ItemsView.List(items, selectedIndex) / ItemsView.Grid(items, columns: 4)   // sugar presets forwarding to Create
```
`ListOptions` (a plain init-record, unpacked to component fields at factory time — the recycling hot path never reads it):
```csharp
new ListOptions {
  SelectionMode = ItemsSelectionMode.Multiple, Selection = model,     // selection
  IsItemInvokedEnabled = true, OnInvoked = i => …, OnChange = () => …,// invoke + selection-changed callbacks
  ItemText = i => …, IsItemEnabled = i => …, Controller = ctl,        // typeahead / enabled gate / imperative handle
  Overscan = 4, Grow = 1f, KeyOf = i => …, CountSignal = countSig,    // window + flex + keyed diff + reactive count
  Selector = SelectorVisual.AccentPill, ContainerFactory = …,         // item chrome (RenderItem path)
  PartDelta = (i, state) => new PartDelta(Fill: …, Corners: …),       // per-item VALUE variation (0-alloc, shape-stable)
  Transition = ItemCollectionTransition.Default,
  Scroll   = new ScrollOptions   { ScrollKey = …, SuppressScrollBar = …, AutoEdgeFade = …, OnScrollGeometryChanged = … },
  Reorder  = new ReorderOptions  { ItemDisplacement = …, DisplacementVersion = …, DraggedSlot = … },
  Entrance = new EntranceOptions { StaggerColdRealize = …, ItemFlipFrom = …, ItemFadeFrom = … },   // bound path
  // ── virtualization knobs ──
  ContentType   = i => rowKind(i),   // recycle-pool discriminator: heterogeneous rows only rebind within their type pool
  CacheExtentPx = 400f,              // pre-realize margin in PIXELS beyond the viewport (overrides row-based Overscan)
  RepaintBoundary = true,            // per-item paint isolation (IsolateLayout + clip) so an item can't relayout the list
  KeepAlive = i => rows[i].IsEditing,// #5: this row's slot parks HIDDEN off-window instead of recycling (state survives)
  KeepAliveCap = 8,                  // bounded keep-alive bucket (LRU-evicted beyond the cap — no leak)
}
```
- `SelectorVisual` picks the item chrome (AccentPill = the WinUI ListView accent bar; Check = the GridView corner
  check; FullRow = a full-bleed superset; Border = the default `ItemContainer` ring; None = app-drawn). A custom
  `ContainerFactory` overrides the preset.
- **Bound vs templated:** `Create` rebuilds a row Element per index (recycled by a window diff); `CreateBound` builds
  each row ONCE per slot and recycles by writing its index signal — a mid-edit `TextBox`/in-flight `UseResource` in a
  bound row survives recycling, and there is no Enter-transition replay.
- **`KeepAlive` (#5)** — a bound slot for a keep-alive item parks HIDDEN (detached, no layout/paint, effects/animations
  quiesced — the same `Flow.KeepAlive` parking mechanics) instead of index-rebinding when it scrolls off-window, so its
  live state is preserved until it re-enters the window or the bounded bucket (`KeepAliveCap`, default 8) LRU-evicts it.
- **`ContentType` (#16)** — heterogeneous bound rows (e.g. header vs track) only cheap-rebind within their content-type
  pool; a cross-type reuse rebuilds the slot (structure differs), never showing type-B data in a type-A subtree.
- **`CacheExtentPx` (#16)** — pixel pre-realize band; overscan stays row-based by default, this overrides it when set.
- **`RepaintBoundary` (#16)** — wraps each realized item container as a layout/paint boundary.
- Reorder rides the displacement channel (`ReorderOptions`); displaced siblings glide aside via an animated translate
  — a capability WinUI's own ItemsView lacks.

## Theming (`Tok` / `Theme`, `src/FluentGpu.Engine/Dsl/Tokens.cs`)

Read semantic tokens; never hard-code colors:

```csharp
Tok.FillCardDefault, Tok.StrokeControlDefault, Tok.TextPrimary, Tok.AccentDefault,
Tok.FillControlStrong /*slider rail*/, Tok.ScrollThumb, Tok.FocusOuter/FocusInner, Tok.ControlElevationBorder, …
```

```csharp
Tok.Use(ThemeKind.Dark);          // switch theme — one pointer write, re-themes the whole app
Tok.SetAccent(osAccentColor);     // inject the OS accent at startup
Tok.SetWindowBackground(color);   // Mica / window background
Theme.BodyFont = "Segoe UI"; Theme.IconFont = "Segoe Fluent Icons";   // typefaces
```

`Theme.*` is a small back-compat facade forwarding to `Tok`. `FluentApp.Run` already wires the real OS accent + Mica.

The `Tok` color getters are **source-generated** (`TokAccessorGenerator`) from the `TokenSet` fields — a new
semantic brush on `TokenSet` gets its `Tok.*` accessor automatically (no hand-written forward to forget). Only
the getters with logic (accent-aware shades, the memoized elevation-border gradients, `WindowBackground`) stay
hand-written.

**On-media ink + scrim** (theme-invariant — ink over album art / video / dark scrims doesn't follow the theme):

```csharp
Tok.OnMediaPrimary / OnMediaSecondary / OnMediaTertiary   // white-alpha ink ramp (1.0 / 0.80 / 0.60)
Tok.MediaScrim                                             // black-alpha chip/pill/FAB scrim plate (~0.55)
Tok.ScrimBottom / Tok.ScrimTop                            // footer / hero scrim GradientSpecs (transparent↔black)
```

**On-accent contrast** — the legible ink (near-black or white) for text/icons on a solid accent fill, chosen by
the fill's WCAG luminance. `Tok.OnAccent` is **baked at accent-set time** (memoized per `Tok.Epoch`, which
`SetAccent`/`Use` bump), so paint reads a field with **zero per-frame contrast math**. For an arbitrary fill use
`ColorContrast.PickContrast(bg)` (near-black vs white) or `PickContrast(bg, darkInk, lightInk)`.

**Non-color tokens**: `Spacing` is the 4px grid (`XXS=2, XS=4, S=8, M=12, L=16, XL=20, XXL=24, XXXL=32, Gutter=24`)
with the semantic names (`PageWide/PageNarrow/Card/Inner/StackS/StackM/StackL` + `PagePadWide/PagePadNarrow/CardPad`
presets) re-pointed onto it. `Radii` adds `None=0, Card=8, Full=999` alongside `Control=4/Overlay=8/Pill=16`
(`ControlAll/CardAll/OverlayAll/PillAll/FullAll` presets). `Elevation`/`Typography` round out the non-color set.

Next: **[rendering-and-performance.md](./rendering-and-performance.md)** — the frame pipeline and how to keep it fast.
