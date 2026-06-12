# Layout and composition

How you describe *what* is on screen and *where* it sits. This page is the app-author's tour of the
composition surface: the immutable `Element` records, the terse `Ui.*` builders that produce them, the
flexbox and CSS-grid layout knobs, and the fluent modifiers that skin a node. Every name here is the real
surface — copy it straight into a `Render()`.

It assumes you have read the [reactivity guide](../../guide/reactivity.md): a tree is **described**, never
mutated. You build `Element` records in `Render()`/`Setup()` and return the root; the reconciler diffs your
description against the live scene and patches the difference. Layout and paint fall out of that — you never
call a "draw" or "measure" yourself.

> Companion reference: [Elements, layout, controls & theming](../../guide/components-elements-layout.md) (the
> dense table version) and the [pitfalls table](../../guide/pitfalls.md) (symptom → cause → fix). The layout
> and scene contracts these builders compile down to live in the design corpus — start at
> [SPEC-INDEX.md](../../../design/SPEC-INDEX.md) and the [layout subsystem](../../../design/subsystems/layout.md).

## The element zoo

`Element` is an immutable record describing one node — cheap to build, never touches the scene directly. There
are a handful of concrete record types; the `Ui.*` builders are just terse constructors for them. Reach for a
record's object-initializer (`new BoxEl { … }`) when you need a prop the builder doesn't expose; reach for the
builder when you don't.

| Element | What it is | Build it with |
|---|---|---|
| `BoxEl` | container + optional surface (fill / border / shadow / gradient / acrylic) — the workhorse | `VStack` / `HStack` / `Panel` / `ZStack` / `Card` / `Layer` / `Divider` / `Pill`, or `new BoxEl { … }` |
| `TextEl` | one text or glyph run | `Text` / `Heading` / `Icon`, the type-ramp factories (`Body`/`Caption`/`Title`…), or `new TextEl("…")` |
| `SpanTextEl` | a rich-text paragraph of typed inline runs (Run/Bold/Hyperlink) | `new SpanTextEl(spans)` |
| `ImageEl` | async, cached, residency-pinned bitmap (album art) | `Image(src, w, h, …)` or `new ImageEl { … }` |
| `ScrollEl` | a clipping, scrolling viewport over one (oversized) child | `ScrollView(content)` |
| `GridEl` | CSS-grid container (Pixel / Star / Auto tracks) | `Grid` / `UniformGrid` / `AutoGrid` |
| `VirtualListEl` | a virtualized collection (10k+ rows) | `Virtual.List` / `Virtual.Grid` / `Virtual.ListBound`, `Repeater.ItemsRepeater` |
| `ComponentEl` | embeds a child `Component` (its own state + hooks) | `Embed.Comp(() => new C())` |
| `ContextProviderEl` | provides a context value to its subtree | `Ctx.Provide(channel, value, child)` |
| `ShowEl` / `ForEl` | reactive conditional / keyed list — re-run as a boundary, not a re-render | `Flow.Show` / `Flow.For` |

`BoxEl`, `TextEl`, `ImageEl`, `ScrollEl`, and `GridEl` are defined in
[`src/FluentGpu.Engine/Dsl/Element.cs`](../../../src/FluentGpu.Engine/Dsl/Element.cs); the control-flow and context records
live alongside in the same DSL assembly.

A note that saves hours: **a button is a `BoxEl`.** There is no `ButtonEl`. `Button.Accent("Save", onSave)`
returns a `BoxEl` with an `OnClick`, hover/press fills, corners, and an `AutomationRole`. The same is true of
pills, cards, and most controls — they are `BoxEl`/`TextEl` compositions, so every modifier and layout prop
below applies to them too.

## `Ui.*` builders

Add `using static FluentGpu.Dsl.Ui;` and author trees as plain expressions. The builders are in
[`src/FluentGpu.Engine/Dsl/Factories.cs`](../../../src/FluentGpu.Engine/Dsl/Factories.cs) (and the type-ramp text factories in
[`Typography.cs`](../../../src/FluentGpu.Engine/Dsl/Typography.cs)).

```csharp
// Containers
VStack(float gap, params Element[] children)             // column flex (Direction = 1)
HStack(float gap, params Element[] children)             // row flex (Direction = 0)
Panel(Edges4 padding, float gap, params Element[] kids)  // a padded column
ZStack(params Element[] children)                        // overlay at the origin, last child on top

// Fluent surfaces (semantic tokens — see Theming)
Card(params Element[] children)                          // card fill + 1px stroke + overlay corners + padding
Layer(Edges4 pad, params Element[] children)             // flyout/expander body: layer fill + stroke + corners + shadow
Divider(bool vertical = false)                           // 1px hairline, stretches across its parent's cross axis
Pill(string label, bool selected, Action onClick)        // SelectorBar pill (accent when selected)
SectionHeader(string title, string? caption = null)      // a BodyStrong title (+ optional caption) with top rhythm

// Text / glyphs
Heading(string text)                                     // 28pt bold
Text(string text)                                        // 14pt body
Icon(string glyph, float size = 16, ColorF? color = null, string? family = null)  // a FontIcon glyph
// …plus the full Fluent type ramp: Caption / Body / BodyStrong / BodyLarge / Subtitle / Title / TitleLarge / Display

// Media + scroll
Image(string source, float w, float h, float corners = 0, ColorF? placeholder = null,
      string? blurHash = null, ImageTransition? transition = null)
ScrollView(Element content, bool horizontal = false)

// Grids
Grid(TrackSize[] columns, float colGap, float rowGap, float rowHeight, params Element[] children)
UniformGrid(int columns, float gap, float rowHeight, params Element[] children)
AutoGrid(float minColWidth, float gap, float rowHeight, params Element[] children)   // responsive auto-fill

// Gradient specs (apply with .Gradient(...))
LinearGradient(float angleDeg, params GradientStop[] stops)   // angle 0 = left→right, 90 = top→bottom
RadialGradient(params GradientStop[] stops)                   // centre → edge
```

The `Ui.*` builders are deliberately terse, but the control-style surfaces (`Card`, `Layer`, `Pill`,
`Divider`, the type ramp) read theme tokens — so they re-skin with the theme instead of baking in a colour. See
[Theming](#theming-read-tokens-never-hard-code-colour) at the end.

A small composed example — a card from the gallery's Home page
([`src/FluentGpu.WindowsApp/Home.cs`](../../../src/FluentGpu.WindowsApp/Home.cs)):

```csharp
new BoxEl
{
    Height = 88, Direction = 0, Gap = 14, AlignItems = FlexAlign.Center, Padding = Edges4.All(14),
    Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
    HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = OnOpen,
    Children =
    [
        Icon(Glyph, 28f).Foreground(Tok.AccentDefault),
        new BoxEl { Direction = 1, Gap = 2, Grow = 1, Children = [BodyStrong(Title), Caption(Subtitle).Secondary()] },
    ],
};
```

## Flexbox layout

Flexbox (a Yoga-style model) is the default layout. A `BoxEl` is a flex container; its `Direction` picks the
main axis, `Justify` distributes children along it, and `AlignItems` aligns them across it. The layout props on
`BoxEl` (all in [`Element.cs`](../../../src/FluentGpu.Engine/Dsl/Element.cs)):

| Prop | Type | Notes |
|---|---|---|
| `Direction` | `byte` | `0` = row, `1` = column (default column). `VStack`/`HStack` set this for you. |
| `Gap` | `float` | inter-child spacing on the main axis |
| `Padding` / `Margin` | `Edges4` | inner / outer thickness — `Edges4.All(v)` or `new Edges4(l, t, r, b)` |
| `Width` / `Height` / `Min*` / `Max*` | `Prop<float>` | DIPs; `NaN` (the default) = auto/unset |
| `Grow` / `Shrink` / `Basis` | `float` | flex coefficients — `Grow = 1` claims free main-axis space |
| `Justify` | `FlexJustify` | `Start` · `Center` · `End` · `SpaceBetween` · `SpaceAround` · `SpaceEvenly` |
| `AlignItems` / `AlignSelf` | `FlexAlign` | `Auto` · `Start` · `Center` · `End` · `Stretch` (cross-axis) |
| `Wrap` | `bool` | wrap children onto multiple lines |
| `ZStack` | `bool` | children overlay at the origin, painted in order (last on top) |
| `ClipToBounds` | `bool` | clip children to the box — **and a layout-boundary signal** (see below) |

`FlexJustify` and `FlexAlign` are byte enums in
[`src/FluentGpu.Engine/Foundation/LayoutTypes.cs`](../../../src/FluentGpu.Engine/Foundation/LayoutTypes.cs). `Edges4` is
`(Left, Top, Right, Bottom)` from [`Geometry.cs`](../../../src/FluentGpu.Engine/Foundation/Geometry.cs).

Each knob in isolation, lifted from the gallery's Flex page
([`GalleryPages.cs`](../../../src/FluentGpu.WindowsApp/GalleryPages.cs)):

```csharp
// justify-content — three tiles, only the main-axis distribution changes
new BoxEl
{
    Direction = 0, Gap = 6f, Grow = 1f,
    Justify = FlexJustify.SpaceBetween,        // or Center / End / SpaceAround / SpaceEvenly
    AlignItems = FlexAlign.Center,
    Children = [Tile(1), Tile(2), Tile(3)],
};

// flex-grow — no fixed widths; free space splits 1 : 2 : 1
new BoxEl
{
    Direction = 0, Gap = 6f, Height = 44, AlignItems = FlexAlign.Stretch,
    Children =
    [
        new BoxEl { Grow = 1f, /* … */ },
        new BoxEl { Grow = 2f, /* … */ },
        new BoxEl { Grow = 1f, /* … */ },
    ],
};

// flex-wrap — a width-constrained row spills onto new lines
new BoxEl
{
    Direction = 0, Wrap = true, Gap = 6f, Width = 240, AlignItems = FlexAlign.Start,
    Children = wrapTiles,
};
```

`Stretch` is the default `AlignItems`, which is why a `Divider()` (a 1px box with `AlignSelf = Stretch`) fills
its parent's cross axis automatically. A child sets `AlignSelf` to opt out of the container's `AlignItems` for
itself.

## CSS Grid

When you need *true* two-dimensional alignment — every cell in a column sharing one measured width, so rows stay
aligned — use `GridEl` instead of nesting flex rows. Columns are typed tracks (`TrackSize`, from
[`src/FluentGpu.Engine/Foundation/TrackSize.cs`](../../../src/FluentGpu.Engine/Foundation/TrackSize.cs)); children flow
row-major into the cells at a uniform row height.

```csharp
TrackSize.Px(80)       // a fixed 80-DIP column
TrackSize.Star(1f)     // a fractional (1fr) column — shares leftover width by weight
TrackSize.Star(2f)     // 2fr — takes twice the share of a 1fr track
TrackSize.Auto         // shrinks to fit its content
```

Three flavours:

```csharp
// Heterogeneous tracks: 80px | 1fr | 2fr | auto
var columns = new[] { TrackSize.Px(80), TrackSize.Star(1f), TrackSize.Star(2f), TrackSize.Auto };
Grid(columns, colGap: 12f, rowGap: 12f, rowHeight: 64f, c0, c1, c2, c3);

// N equal star columns — the common album/artist card grid
UniformGrid(columns: 4, gap: 12f, rowHeight: 90f, cells);

// Responsive auto-fill: as many equal (1fr) columns as fit at >= minColWidth,
// stretched to fill the width with no ragged edge — CSS repeat(auto-fill, minmax(280, 1fr)).
AutoGrid(minColWidth: 280f, gap: 12f, rowHeight: 88f, cards);
```

`AutoGrid` reflows its column count as the available width changes — the Home page uses it so the sample-card
grid re-columns on window resize with no code of yours:

```csharp
AutoGrid(280f, 12f, 88f, cards) with { Padding = new Edges4(36, 0, 36, 36) },
```

A grid resolves its star tracks against a concrete width, so give it (or an ancestor) a width — inside a
`ScrollView` or a sized box it works without one because the viewport supplies the width. The grid contract
(true tracks vs nested-flex faking, width-awareness) is owned by
[the layout subsystem, §7](../../../design/subsystems/layout.md).

## Fluent modifiers

A modifier is a fluent extension method that returns a record `with`-copy — it tweaks one instance without
forking a control's default style. They are in
[`src/FluentGpu.Engine/Dsl/Modifiers.cs`](../../../src/FluentGpu.Engine/Dsl/Modifiers.cs). Because every modifier returns the
same record type, you can chain them, and you can apply them to a control: `Button.Accent("Save", onSave).Rounded(20)`
just rounds that one button.

```csharp
// Box surface + paint
box.Background(c).HoverColor(c).PressedColor(c)        // resting / hover / pressed fill
   .Border(c, width).Rounded(r).Pad(all)              // or .Pad(left, top, right, bottom)
   .Shadow(s).Elevate(preset).Gradient(g)             // rich paint
   .BorderBrush(g, width).Acrylic(a)                  // gradient stroke / frosted glass
// Composited transform + opacity — animate WITHOUT relayout (see Compositor)
   .Offset(dx, dy).Scale(s)  /* or .Scale(sx, sy) */ .Rotate(deg).Alpha(opacity)

// Text
text.Foreground(c).FontSize(px).Strong().Font(family)  // Strong() = SemiBold 600, not Bold 700
    .Wrapped().NoWrap().Trim().Ellipsis().MaxLines(n)
```

`Strong()` is SemiBold (weight 600), matching the WinUI BodyStrong ramp — use `Bold = true` (or
`FontWeight(700)`) for true bold. The text-tier colour helpers (`Secondary()`, `Tertiary()`, `Accent()`,
`Disabled()`) are in [`Typography.cs`](../../../src/FluentGpu.Engine/Dsl/Typography.cs) and read theme tokens, so
prefer `Body("…").Secondary()` over `.Foreground(someGrey)`.

Modifiers in action (the gallery's Typography page):

```csharp
Text("Accent text").Foreground(Theme.Accent).FontSize(18f)
Text("The quick brown fox — Consolas").Font("Consolas").FontSize(16f)
new TextEl("The quick brown fox") { Size = 18f, Color = Theme.WindowText }.Strong()
```

For the rich-paint presets — `Shadow`/`Elevate` take a `ShadowSpec`, and the `Elevation.*` presets
(`Card`, `CardHover`, `Tooltip`, `Flyout`, `Dialog`) live in
[`src/FluentGpu.Engine/Dsl/Elevation.cs`](../../../src/FluentGpu.Engine/Dsl/Elevation.cs):

```csharp
Card(content).Elevate(Elevation.Flyout);                          // a lifted, flyout-band shadow
box.Gradient(LinearGradient(118f,
        new GradientStop(0f, Tok.HeroGradientTop),
        new GradientStop(1f, Tok.HeroGradientBottom)));           // gradient fill (supersedes Fill)
box.Acrylic(new AcrylicSpec(Tok.AccentDefault, tintOpacity: 0.6f,
        blurSigma: 30f, noiseOpacity: 0.02f, luminosityOpacity: 0.8f));  // per-node frosted glass
```

> One generic door, not a styling knob per control. To restyle a *control's internal template part* (an
> Expander header, a slider rail) the path is `Parts`, the signals-native `::part` modifier — not a new
> per-control property. That surface is documented in
> [components-elements-layout.md → Template parts](../../guide/components-elements-layout.md) and
> [control-fidelity.md](../../guide/control-fidelity.md).

## `BoxEl` visuals

A `BoxEl` is both a layout container and a paintable surface. The surface props (all in
[`Element.cs`](../../../src/FluentGpu.Engine/Dsl/Element.cs)):

- **`Fill`** — the resting background. It is a `Prop<ColorF>`: a static colour, a `Func<ColorF>` thunk
  (`Prop.Of(() => …)`), or a concrete signal. **`HoverFill`** / **`PressedFill`** are the eased interaction
  states; the renderer cross-fades between them on pointer enter/press with no work from you (give the box a
  pointer handler so it receives the hover/press flags — most controls already do).
- **`BorderColor`** + **`BorderWidth`**, with optional **`HoverBorderColor`** / **`PressedBorderColor`** (an
  `A == 0` state colour means "auto-lighten/darken the resting border").
- **`Corners`** — a `CornerRadius4` (`(TopLeft, TopRight, BottomRight, BottomLeft)`). Use
  `CornerRadius4.All(r)`, `.Rounded(r)`, or the Fluent ramp `Radii.ControlAll` / `Radii.OverlayAll` /
  `Radii.PillAll` (from [`Radii.cs`](../../../src/FluentGpu.Engine/Dsl/Radii.cs)).
- **`Shadow`** (`ShadowSpec?`) — a soft drop shadow drawn beneath the fill (the `Elevation.*` presets).
- **`Gradient`** (`GradientSpec?`) — a gradient fill that supersedes `Fill` at record time; up to four stops.
- **`BorderBrush`** (`GradientSpec?`) — a gradient stroke (the WinUI control-elevation border); needs
  `BorderWidth > 0`.
- **`Acrylic`** (`AcrylicSpec?`) — a per-node frosted-glass backdrop (blur + tint + noise).

```csharp
// A tile that brightens on hover (the gallery Grid page)
new BoxEl
{
    Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
    Fill = tint with { A = 0.85f },
    HoverFill = tint,                         // eased cross-fade on pointer enter, automatically
    Corners = CornerRadius4.All(8f),
    Children = [new TextEl(number.ToString()) { Size = 20f, Bold = true, Color = ColorF.FromRgba(20, 20, 24) }],
};
```

### Composited transforms (animate with no relayout)

`OffsetX`/`OffsetY`, `ScaleX`/`ScaleY`, `Rotation`, and `Opacity` are **composited** — applied at record time
to the node and its subtree, never touching layout or hit-testing geometry. They are the cheap channel: change
one and the frame is compositor-only (no render, no reconcile, no layout). The `.Offset/.Scale/.Rotate/.Alpha`
modifiers set them.

```csharp
// The gallery Compositor page — static transforms; the Animation page drives them over time.
Tile("45").Rotate(45f)        // rotate about the node's transform origin; siblings don't reflow
Tile("1.3x").Scale(1.3f)      // visual scale only; the layout box is unchanged
Tile("0.3").Alpha(0.3f)       // per-node opacity multiplies into the node's pixels
Tile("-12").Offset(0f, -12f)  // translate the painted node without moving its layout slot
```

For *hot* per-frame values, don't set these statically and re-render each frame — **bind** the channel instead.
`Transform` is a whole-matrix `Prop<Affine2D>` and `Opacity`/`Fill` are bound `Prop<T>` channels: point them at
a `Func` or a signal and writes update just that node's column, with zero render/reconcile/layout. This is the
slider-scrubber fix:

```csharp
var x = UseFloatSignal(0.3f);   // a hot scalar — bind it, don't setState per move

Slider.Bind(x);                 // the drag writes the signal

new BoxEl
{
    Width = 32, Height = 32,
    Transform = Prop.Of(() => Affine2D.Translation(x.Value * 184f, 0f)),  // compositor-only
    Fill      = Prop.Of(() => ColorF.Lerp(grey, Tok.AccentDefault, x.Value)),  // compositor-only
};
```

The binding mechanism, why `.Value` subscribes (and `.Peek()` does not), and the three update tiers are the
subject of the [reactivity guide](../../guide/reactivity.md) — read it before you reach for `setState` on a
drag. The contract that a static value and a bound `Func`/signal flow through the *same* `Prop<T>` channel is
the unified property surface; only one owner may drive a channel at a time (a bound `Transform` and a static
`OffsetX` on the same node fight — see the pitfall below).

## DIP sizing and responsive layout

All sizes are in **device-independent pixels (DIP)**; the host scales by the monitor DPI, so a `Width = 200`
box is 200 DIP on every display. You generally lay out with intrinsic sizing (`Grow`, `AlignItems = Stretch`,
auto `NaN` sizes) and let content drive extent — explicit `Width`/`Height` are for when a box must be a fixed
size or a layout boundary.

For layout that adapts to the window, read the ambient client size. `Viewport.Size` is a context channel (in
[`src/FluentGpu.Engine/Hooks/Context.cs`](../../../src/FluentGpu.Engine/Hooks/Context.cs)) the host pushes each frame:

```csharp
public override Element Render()
{
    var size = UseContext(Viewport.Size);   // Size2 in DIP — reading it re-renders on resize
    bool wide = size.Width >= 1008f;
    return wide ? TwoColumn() : OneColumn();
}
```

`NavigationView` already adapts its display mode (Expanded / Compact / Minimal) off `Viewport.Size` at the
1008 / 641 DIP thresholds, and `AutoGrid` reflows its columns against the available width — so for grids and the
left-nav you rarely need to read the viewport yourself. When you do, remember that reading a context value
*subscribes* this component, so it re-renders on resize (that is the point); keep the work in that `Render()`
cheap.

## `ClipToBounds` as a layout boundary

`ClipToBounds = true` does the obvious thing — it clips children to the box. It also does something
load-bearing for performance: **it marks the box a layout boundary.** When a descendant relayouts, the
invalidation walks *up* until it hits a boundary; without one it can reach the root and relayout the whole page.
A boundary needs a concrete size to clip against, so the recipe is an explicit `Width` + `Height` +
`ClipToBounds = true` on the enclosing container:

```csharp
new BoxEl
{
    Width = 360, Height = 480, ClipToBounds = true,   // a scoped relayout stops here
    Children = [/* a subtree whose changes shouldn't relayout the page */],
};
```

This is exactly the fix for the "a small change relayouts the whole page" pitfall. The full reasoning — scoped
relayout, the up-walk, why a `ScrollView` is already a boundary — belongs to the rendering and performance
guide, [Scoped relayout](../../guide/rendering-and-performance.md#scoped-relayout); this page just flags the
signal so you reach for it while you compose, not after the page feels slow.

## Theming: read tokens, never hard-code colour

The `Ui.*` surfaces and tier helpers read semantic tokens from `Tok` (in
[`src/FluentGpu.Engine/Dsl/Tokens.cs`](../../../src/FluentGpu.Engine/Dsl/Tokens.cs)) so a theme switch re-skins them. Prefer a
token to a literal `ColorF`:

```csharp
Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault,   // not a raw ColorF
Children = [BodyStrong(Title), Caption(Subtitle).Secondary()],     // tier helpers, not .Foreground(grey)
```

Hard-coded colours are the "colours look wrong across themes" pitfall. The token catalogue and theme switching
(`Tok.Use(theme)`, `Tok.SetAccent`) are covered in
[components-elements-layout.md → Theming](../../guide/components-elements-layout.md).

## Gotchas

These are the composition-time failure modes from the [pitfalls table](../../guide/pitfalls.md) — worth
internalising before they bite:

- **Constructor args freeze at mount.** A child `Component`'s public fields are set once, when `Embed.Comp`
  realizes it; a parent re-render does **not** re-invoke the factory. To flow changing data to a child, pass a
  `Signal<T>` or use `Ctx.Provide` + `UseContext` — never a plain field. (See the `SelectorBar` in
  [`Home.cs`](../../../src/FluentGpu.WindowsApp/Home.cs): selection is owned by the page and threaded as
  arguments to a *stateless factory*, precisely because a stateful child would freeze its selection at mount.)
- **One owner per composited channel.** A static `OffsetX`/`ScaleX`/`Opacity` you set will "snap back" each
  frame if an animation or a bound `Transform`/`Opacity` already owns that channel — the reconciler won't also
  write the static value (it would fight the animator). Drive the value through *one* path.
- **Text doesn't wrap by default.** `Wrap = NoWrap` is the default; `text.Wrapped()` needs a width to wrap
  against — an explicit `Width` or a stretching parent.
- **An element needs size + a handler to be clickable.** `HitTestVisible` defaults true, but a zero-size box or
  one with no `OnClick`/`OnPointerDown` won't receive input.
- **Hot values: bind, don't `setState`.** A `setState` per pointer-move re-renders the owning component every
  frame and tanks FPS. Bind the value to `Transform`/`Opacity`/`Fill` (compositor-only) or use
  `Slider.Bind(FloatSignal)`. Confirm `FrameStats.Rendered == false` on the drag.

## Where to go next

- [Reactivity](../../guide/reactivity.md) — the signals mental model; the three update tiers; why bindings are
  the cheapest path. **Read this first if you haven't.**
- [Elements, layout, controls & theming](../../guide/components-elements-layout.md) — the dense reference: the
  full hooks list, the controls surface, virtualization, and theming.
- [Rendering and performance](../../guide/rendering-and-performance.md) — the frame pipeline, scoped relayout,
  and the zero-alloc rules `ClipToBounds` boundaries serve.
- [Pitfalls](../../guide/pitfalls.md) — symptom → cause → fix for every failure mode above.
- Design corpus (contracts, not usage): [SPEC-INDEX.md](../../../design/SPEC-INDEX.md) (precedence authority),
  [the layout subsystem](../../../design/subsystems/layout.md).
