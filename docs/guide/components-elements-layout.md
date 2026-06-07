# Elements, layout, controls & theming

[← Guide index](./README.md) · prerequisite: **[reactivity.md](./reactivity.md)**

## The element zoo

`Element` is an immutable record describing a node — cheap to build, never touches the scene directly. You compose
them in `Render()`/`Setup()` and return the root. Build them with the `Ui.*` helpers (terse) or the records directly
(full control).

| Element | Builder | What it is |
|---|---|---|
| `BoxEl` | `VStack`/`HStack`/`Panel`/`ZStack`/`Card`/`Layer`/`Divider`/`Pill` | container + optional surface (fill/border/shadow/gradient/acrylic), the workhorse |
| `TextEl` | `Text`/`Heading`/`Icon` | a text/glyph run |
| `ImageEl` | `Image` | async, cached, residency-pinned bitmap (album art) |
| `ScrollEl` | `ScrollView` | clipping, scrolling viewport over one (oversized) child |
| `GridEl` | `Grid`/`UniformGrid`/`AutoGrid` | CSS-grid container (Pixel/Star/Auto tracks) |
| `VirtualListEl` | `Virtual.List/Grid/VariableList/Custom`, `Repeater.ItemsRepeater` | virtualized collection (10k+ rows) |
| `ComponentEl` | `Embed.Comp(() => new C())` | embeds a child `Component` |
| `ContextProviderEl` | `Ctx.Provide(channel, value, child)` | provides a context value |
| `ShowEl` / `ForEl` | `Flow.Show` / `Flow.For` | reactive conditional / keyed list |

### `Ui.*` builders (`src/FluentGpu.Dsl/Factories.cs`)

```csharp
VStack(float gap, params Element[] children)            // column flex
HStack(float gap, params Element[] children)            // row flex
Panel(Edges4 padding, float gap, params Element[] kids) // padded column
ZStack(params Element[] children)                       // overlay (last on top)
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

### Modifiers (`src/FluentGpu.Dsl/Modifiers.cs`) — fluent per-element overrides

```csharp
box.Background(c).HoverColor(c).PressedColor(c).Border(c, width).Rounded(r).Pad(all) / .Pad(l,t,r,b)
   .Shadow(s).Elevate(preset).Gradient(g).BorderBrush(g, width).Acrylic(a)
   .Offset(dx,dy).Scale(s) / .Scale(sx,sy).Rotate(deg).Alpha(opacity)
text.Foreground(c).FontSize(px).Strong().Font(family).Wrapped().NoWrap().Trim().Ellipsis().MaxLines(n)
```

These return a `with`-copy — they don't fork a control's default style, so e.g. `Button.Accent(...).Rounded(20)`
just tweaks that one instance.

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
| `ZStack` | `bool` | children overlay at the origin, painted in order (last on top) |
| `ClipToBounds` | `bool` | clip children — **and a layout-boundary signal** (see performance doc) |

Sizes are in DIP (device-independent pixels); the host scales by the monitor DPI. `Viewport.Size` (via
`UseContext`) gives the client size in DIP for responsive layout.

## Visuals on a `BoxEl`

`Fill`, `HoverFill`, `PressedFill`, `BorderColor`+`BorderWidth`, `Corners` (`CornerRadius4`), `Shadow` (`ShadowSpec`),
`Gradient` (`GradientSpec`, supersedes `Fill`), `BorderBrush` (gradient stroke — WinUI elevation border), `Acrylic`
(`AcrylicSpec`, per-node frosted glass). Composited (animate with no relayout): `OffsetX/OffsetY`, `ScaleX/ScaleY`,
`Rotation`, `Opacity`, plus interaction-driven `HoverScale`/`PressScale` (eased pop on hover/press).

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
These seed engine tracks on the component's node and animate by re-recording transform/opacity — **never re-render,
never relayout.** Prefer these (or a signal bound to a transform) for motion. `UseAnimatedValue(target, ms, easing)`
exists but steps only when the component re-renders for other reasons — avoid it for real motion.

### Images
```csharp
UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null) // → ImageBinding (load state)
PrefetchImage(string src, int decodePx)                                                                     // warm before it scrolls in
```
`UseImage` subscribes the component to image-status changes (so a spinner→ready transition re-renders just it). To
actually paint, also use `Ui.Image(src, …)`. Album-art decode is off-thread, cached, residency-pinned while on screen.

## Controls (`src/FluentGpu.Controls/`)

Most controls are **element-returning factories** (call them in render); `NavigationView`/`PageHost` are `Component`s.
Every control has a `Style` record + a global `…StyleOverride` and reads theme tokens by default.

```csharp
Button.Accent(string label, Action onClick, Style? style = null)     // primary
Button.Standard(string label, Action onClick, Style? style = null)   // neutral
IconButton.Create(string glyph, Action onClick, Style? style = null)
ToggleButton.Create(string label, bool on, Action onToggle, Style? style = null)

Slider.Create(float value, Action<float> onChange, float w = 200, float h = 24, Style? = null)   // controlled (re-render)
Slider.Bind(FloatSignal value, Action<float>? onChange = null, float w = 200, float h = 24, …)    // signals-native (no re-render) ★
ScrollBar.Create(float fraction, float position, Action<float> onScroll, float h = 200, Style? = null)
AnimatedIcon.Glyph(string glyph, float size = 16, ColorF? color = null, string? font = null, float hoverScale = 1.08f, float pressScale = 0.88f)
```

★ **Prefer `Slider.Bind` for a scrubber.** A drag writes the `FloatSignal` → the thumb/fill composited transforms
update with **zero render/reconcile/layout**. `Slider.Create` is the controlled (React-style) variant that re-renders
the owning component on each move — fine for low-frequency use, but `Bind` is the slider-tank fix.

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
};   // adapts Expanded/Compact/Minimal by Viewport.Size width (1008 / 641 thresholds)
```

### Virtualization (10k+ rows, bounded live nodes)
```csharp
Virtual.List(int itemCount, float itemExtent, Func<int,Element> renderItem, Func<int,string>? keyOf = null, int overscan = 4)
Virtual.Grid(int itemCount, int columns, float itemHeight, float gap, Func<int,Element> renderItem, …)
Virtual.VariableList(int itemCount, float estimatedExtent, Func<int,Element> renderItem, …)   // measured heights + anchoring
Repeater.ItemsRepeater(int count, Func<int,Element> template, in RepeatLayout layout, Func<int,string>? keyOf = null, int overscan = 4)
//   RepeatLayout.Stack(extent, horizontal) / .Grid(cols,h,gap) / .Custom(IVirtualLayout) / .Wrap(gap) / .Inline(...)
```
Only the visible window (+overscan) is realized; scrolling recycles row nodes through the slab free-list. Provide a
stable `keyOf` so row state/identity survives recycling. In-window scroll is transform-only (no realize, no relayout).

## Theming (`Tok` / `Theme`, `src/FluentGpu.Dsl/Tokens.cs`)

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

Next: **[rendering-and-performance.md](./rendering-and-performance.md)** — the frame pipeline and how to keep it fast.
