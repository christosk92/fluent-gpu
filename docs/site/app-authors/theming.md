# Theming and tokens

FluentGpu ships a faithful Fluent (WinUI) light/dark palette as a flat table of **semantic tokens** —
`Tok.FillCardDefault`, `Tok.StrokeControlDefault`, `Tok.TextPrimary`, `Tok.AccentDefault`, and the rest. You
read tokens; you do not hard-code colors. A theme swap is then **one pointer write** that re-points the whole
app at the other palette, and the host injects the real OS accent and the Mica window background for you at
startup.

This page is the app-author guide to that surface — the families, how to switch themes, how the accent and
window background are injected, the typeface facade, and the radius/spacing tokens. The token *table itself*
(`src/FluentGpu.Engine/Dsl/Tokens.cs`) is the source of truth for the exact values; this page links every family back
to it rather than restating numbers.

> Prerequisite: the signals mental model in **[signals, components & bindings](./signals-components-and-bindings.md)**
> and the element/control surface in
> **[components, elements & layout](../../guide/components-elements-layout.md)**. Theming layers on top of both.

## Read semantic tokens, never hard-code colors (`Tok.*`)

Every brush an app needs is a static getter on `Tok` (`src/FluentGpu.Engine/Dsl/Tokens.cs`). Use them as `Fill`,
`BorderColor`, text `Color`, etc. — never an inline `ColorF`:

```csharp
using FluentGpu.Dsl;             // Tok, Radii, Theme
using static FluentGpu.Dsl.Ui;

new BoxEl
{
    Padding = Edges4.All(14), Corners = Radii.OverlayAll,
    Fill = Tok.FillCardDefault,                       // card surface
    BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
    HoverFill = Tok.FillCardSecondary,                // hover state
    Children =
    [
        BodyStrong("Now playing"),
        Caption("Aria Vance — Solar Drift").Secondary(),   // .Secondary() == Tok.TextSecondary
    ],
};
```

Why this matters: a token resolves to the value for whichever theme is currently active, so the *same* element
description renders correctly in light and dark. Hard-code a `ColorF` and it is frozen — it looks wrong in the
other theme, and a theme swap can't fix it. This is the single most common theming bug:

| Symptom | Cause | Fix |
|---|---|---|
| Colors look wrong across themes | Hard-coded `ColorF` instead of tokens | Read `Tok.*` (`Tok.TextPrimary`, `Tok.FillCardDefault`); they follow `Tok.Use(theme)`. |

(From **[pitfalls](../../guide/pitfalls.md)** → *Layout & visuals*.)

The terse type-ramp helpers already do this for you — `Ui.Body`, `Ui.BodyStrong`, `Ui.Caption`,
`Ui.Heading`, … default their `Color` to the right text token, and the tier modifiers
(`.Secondary()`, `.Tertiary()`, `.Accent()`, `.OnAccent()`) swap to the sibling token without you naming a
color. Prefer them over `new TextEl(...) { Color = … }` whenever a ramp tier fits. (See the type ramp in
**[components, elements & layout](../../guide/components-elements-layout.md)**.)

## The token families

`Tok` is organized into families. Each getter forwards to the active `TokenSet` (the immutable baked palette
for one theme) at zero per-read cost. The full list with WinUI provenance lives in `Tokens.cs`; the common
ones, by job:

**Fills** — control backgrounds and layered surfaces, cheapest (most translucent) first:

```csharp
Tok.FillControlDefault / Secondary / Tertiary / Disabled   // button/control body + hover/pressed/disabled
Tok.FillSubtleSecondary / FillSubtleTertiary               // "subtle" button hover/pressed (transparent at rest)
Tok.FillCardDefault / FillCardSecondary                    // card surface + hover
Tok.FillLayerDefault                                        // flyout / expander body
Tok.FillSolidBase / FillSolidBaseAlt                        // page background / pane (opaque)
Tok.FillControlStrong                                       // slider rail, scrollbar thumb (the strong fill)
Tok.FillSmoke                                               // dialog / overlay scrim
```

**Strokes** — control, card, divider, and surface borders:

```csharp
Tok.StrokeControlDefault / StrokeControlSecondary
Tok.StrokeCardDefault                                       // card ring (used with BorderWidth = 1f)
Tok.StrokeDividerDefault                                    // hairline divider
Tok.StrokeControlStrongDefault                              // checkbox/radio/toggle outer ring
```

**Text** — the foreground hierarchy:

```csharp
Tok.TextPrimary / TextSecondary / TextTertiary / TextDisabled
Tok.TextOnAccentPrimary                                     // text drawn ON an accent fill
```

**Accent** — the system/brand accent, in both *fill* and *text* forms (they differ — see
[Injecting the OS accent](#injecting-the-os-accent-and-window-background-toksetaccent--toksetwindowbackground)):

```csharp
Tok.AccentDefault / AccentSecondary / AccentTertiary        // accent FILL (button, selection bar)
Tok.AccentSubtle                                            // accent @ ~16% (nav selection, info tint)
Tok.AccentTextPrimary / AccentTextSecondary / AccentTextTertiary   // accent TEXT (link labels — readability-adjusted, solid)
```

**Control elevation** — the signature Fluent gradient borders (assembled from the stroke tokens; pass to a
`BoxEl.BorderBrush`, which renders a gradient stroke). These are theme-aware `GradientSpec`s, allocated at
reconcile time, not per frame:

```csharp
Tok.ControlElevationBorder          // WinUI ControlElevationBorderBrush — the standard control border
Tok.AccentControlElevationBorder    // accent buttons / checked toggles
Tok.CircleElevationBorder           // circular knobs/glyphs (toggle knob, radio glyph)
```

**Focus** — the two-tone focus ring:

```csharp
Tok.FocusOuter   // outer stroke
Tok.FocusInner   // inner stroke
Tok.FocusThickness   // == 2f (a const)
```

**Scroll thumb** and **acrylic**:

```csharp
Tok.ScrollThumb           // overlay scrollbar thumb (folds into FillControlStrong)
Tok.AcrylicFlyout         // the default transient-surface acrylic (AcrylicSpec) for flyout-family surfaces
```

There are more — a full severity palette (`Tok.SystemFillCritical/Caution/Success/Attention` + their
`*Background` tints for InfoBar/InfoBadge), text-control chrome (`Tok.TextControlHeaderForeground`,
`Tok.FillControlInputActive`), caption-button reds, and a hero gradient (`Tok.HeroGradientTop/Bottom`). Open
`Tokens.cs` and search the family comment when you need one; each token carries its WinUI key and the exact
light/dark value in a comment.

The hero in the gallery is a good worked example of accent-fed gradient tokens:

```csharp
// src/FluentGpu.WindowsApp/Home.cs — the gradient hero header
new BoxEl
{
    Height = 200, Direction = 1, Justify = FlexJustify.End,
    Gradient = LinearGradient(118f,
        new GradientStop(0f, Tok.HeroGradientTop),       // accent-tinted top
        new GradientStop(1f, Tok.HeroGradientBottom)),   // fades to the window base
    Corners = new(Radii.Overlay, 0f, 0f, 0f),
    Children = [ /* … */ ],
};
```

## Switching themes (`Tok.Use(ThemeKind)` — one pointer write)

`ThemeKind` is `Light` or `Dark`. `Tok.Use` swaps the active `TokenSet` with a single field write — there are
no per-token updates, no allocation, no scan of the tree:

```csharp
Tok.Use(ThemeKind.Dark);    // every Tok.* now resolves against the dark palette
Tok.Use(ThemeKind.Light);   // …and now the light palette
```

That swap re-themes *future* reads. The honest caveat — and it is load-bearing — is **when** those reads
happen. Tokens are resolved at element **construction**, baked into the immutable `Element` records and into
each control's `Style` record. So switching the theme only reaches the screen once the affected components
**re-render and reconstruct their elements** with the new token values. From the engine source
(`src/FluentGpu.Engine/Dsl/Element.cs`, the `TextEl.Color` default):

> Defaults to the live theme's `TextFillColorPrimary` … resolved at element **construction**. NOTE: a runtime
> `Tok.Use` switch does not itself re-render — the theme is set at startup today; a live theme switcher must
> force a full re-render or every construction-resolved color (this default, control `Style` records) goes
> stale.

Practically:

- **Set the theme at startup**, before your first render, and it just works — every element constructs against
  the right palette. This is the common case.
- **A live in-app theme toggle** must call `Tok.Use(...)` *and* trigger a re-render of everything that reads
  tokens. The simplest reliable way is to drive it off state high enough that the affected subtree re-renders
  (e.g. flip a root-level `UseState`/signal that your shell reads, so the whole content subtree reconstructs).
  A `ColorF` already baked into a still-mounted node will not retroactively change — that is the same "snaps
  back / goes stale" shape as the cross-theme pitfall above, and it is by design: the reconciler does not walk
  live nodes rewriting colors.

> Engine internals: the theming subsystem models a theme swap as *consumers re-render → derived brushes
> re-derive against the new `ThemeKind`*, and a color change is always **PaintDirty, never LayoutDirty** (a
> color can't change a measured size). See **[Engine internals](#engine-internals-the-theming-subsystem)**.

## Injecting the OS accent and window background (`Tok.SetAccent` / `Tok.SetWindowBackground`)

Two values sit *on top of* the baked palette as mutable overrides, so they survive a theme swap and let the
host inject live system state:

```csharp
Tok.SetAccent(osAccentColor);     // inject the OS accent (or a brand/global override)
Tok.SetAccent(null);              // clear the override → revert to the theme's default accent
Tok.SetWindowBackground(color);   // the window base (Mica → Transparent, see below)
```

`SetAccent` is special: it doesn't just replace `Tok.AccentDefault`. **Every accent-derived token recomputes**
from the injected color — the secondary/tertiary fills (by reducing alpha), `AccentSubtle`, the hero gradient
top, the severity *Attention* fill, **and** the accent *text* shades. The accent-text recompute is not a simple
alpha reduction: link/label text is shaded (lightened in dark, darkened in light) toward the WinUI accent-text
ramp so it stays readable and **solid** — a translucent link would be wrong. With no override, the exact baked
WinUI default values are used.

The fill-vs-text split is the reason there are two accent families. `Tok.AccentDefault` is the **fill** (button
background, selection bar). `Tok.AccentTextPrimary` is the **text** shade used for hyperlink/accent labels —
distinct, and readability-adjusted. Use the text tokens for accent *text*, the fill tokens for accent
*surfaces*. (The harness verifies a `HyperlinkButton` uses `AccentTextPrimary`, not `AccentDefault`, and that
it tracks an override.)

A self-contained override example, straight from the engine harness
(`src/FluentGpu.VerticalSlice/Program.cs`):

```csharp
Tok.SetAccent(ColorF.FromRgba(0xE0, 0x40, 0x40));   // developer/OS override (red) — all accent tokens go red
// … draw …
Tok.SetAccent(null);                                 // clear the override (revert to theme default)
```

Just like `Tok.Use`, an accent or window-background change only reaches pixels through a re-render of the
elements that read those tokens — set them before first render at startup, or force a re-render if you change
them live.

## How `FluentApp.Run` wires accent + Mica for you

If you start your app with `FluentApp.Run(() => new App())`, you do **not** call `SetAccent` /
`SetWindowBackground` yourself — the batteries-included entry point reads the real OS state and injects it
before your first frame. From `src/FluentGpu.WindowsApp/FluentApp.cs`:

```csharp
// Inside FluentApp.Run — paraphrased:
if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);   // OS accent (Light2 shade)
else if (Win32Theme.Accent() is { } b)  Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);   // fallback to base accent
Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica, customFrame);       // dark titlebar + Mica backdrop
if (mica) Theme.WindowBackground = ColorF.Transparent;                                     // let Mica show through
```

What that does, in token terms:

- `Theme.Accent = …` forwards to `Tok.SetAccent(...)` (see the [facade](#typefaces-and-the-theme-back-compat-facade)
  below), so the live Windows *Settings → Colors* accent feeds every accent token. It reads
  `SystemAccentColorLight2` (the lighter shade WinUI uses for the dark-theme accent fill), falling back to the
  base accent / DWM colorization color.
- `ApplyWindowMaterial(...)` turns on the Windows 11 Mica system backdrop (and the dark titlebar).
- `Theme.WindowBackground = ColorF.Transparent` forwards to `Tok.SetWindowBackground(...)`. Mica is a system
  backdrop that composites *behind* transparent client pixels, so the window base must be transparent for it to
  show. That is why the override exists: the engine sets the window background to transparent so Mica reads
  through.

`FluentApp.Run` reads `AppOptions.Mica` (defaults to `true`); pass `new AppOptions { Mica = false }` for an opaque
window (the backdrop becomes acrylic and the window background stays opaque). The accent injection happens either way.

> Note: `FluentApp.Run` does not set the *theme kind* for you — light vs dark is your call (`Tok.Use(...)`).
> The Mica/titlebar wiring above passes `Theme.Dark` (the current kind) through to the DWM dark-mode attribute,
> so set your theme before `Run` if you want the OS titlebar to match.

## Typefaces and the `Theme` back-compat facade

Typefaces live on `Theme` (the facade), as two plain fields you can assign at startup:

```csharp
Theme.BodyFont = "Segoe UI";              // default text face for every run
Theme.IconFont = "Segoe Fluent Icons";    // symbol face for Icon()/IconButton/NavigationView glyphs
```

`BodyFont` is the default family for text runs; `IconFont` is the glyph face used by `Ui.Icon`, `IconButton`,
and the navigation chrome. (Any single run can still override its family with `.Font("Consolas")` — see the
typography section of **[components, elements & layout](../../guide/components-elements-layout.md)**.)

**`Theme` is a small back-compat facade over `Tok`** (`src/FluentGpu.Engine/Dsl/Theme.cs`). Its flat names forward to
the active `TokenSet`, so a `Tok.Use` swap re-themes them too. The mapping is one-to-one:

| `Theme` (facade) | Forwards to |
|---|---|
| `Theme.Dark` (get/set `bool`) | `Tok.Theme == Dark` / `Tok.Use(...)` |
| `Theme.Accent` (get/set) | `Tok.AccentDefault` / `Tok.SetAccent(...)` |
| `Theme.WindowBackground` (get/set) | `Tok.WindowBackground` / `Tok.SetWindowBackground(...)` |
| `Theme.ControlFill` / `ControlFillHover` / `ControlFillPressed` | `Tok.FillControl{Default,Secondary,Tertiary}` |
| `Theme.ControlBorder` | `Tok.StrokeControlSecondary` |
| `Theme.ControlText` / `WindowText` | `Tok.TextPrimary` |
| `Theme.AccentText` | `Tok.TextOnAccentPrimary` |

You will see `Theme.*` across the gallery (`Theme.WindowText`, `Theme.ControlFill`, `Theme.Accent` are common
in `GalleryPages.cs`) and it is fully supported. **New code should prefer `Tok.*` directly** — it exposes the
full semantic table; the facade is a convenience subset kept for existing call sites and for the host's
accent/Mica setters.

## Radii and spacing tokens

Corner radii are the WinUI Fluent ramp, on `Radii` (`src/FluentGpu.Engine/Dsl/Radii.cs`):

```csharp
Radii.Control   // 4px  — buttons, inputs, small controls (ControlCornerRadius)
Radii.Overlay   // 8px  — cards, flyouts, dialogs, expanders (OverlayCornerRadius)
Radii.Pill      // 16px — pills, badges, the SelectorBar
```

Each has a prebaked `CornerRadius4` so you don't build one per element, plus helpers for the common shapes:

```csharp
Corners = Radii.ControlAll;            // CornerRadius4.All(4)
Corners = Radii.OverlayAll;            // CornerRadius4.All(8)  — the usual card
Corners = Radii.PillAll;               // CornerRadius4.All(16)
Corners = Radii.Circle(40f);           // a 40px tile → radius 20 (a full circle)
Corners = Radii.OverlayTop;            // top corners only (card flush to a footer)
Corners = Radii.OverlayBottom;         // bottom corners only
```

The selector tab in the gallery shows the pill radius in context:

```csharp
// src/FluentGpu.WindowsApp/Home.cs — a SelectorBar tab
new BoxEl
{
    Direction = 0, Gap = 6, AlignItems = FlexAlign.Center, Padding = new Edges4(14, 6, 14, 6),
    Corners = Radii.PillAll, OnClick = () => onSelect(tag),
    Fill = sel ? Tok.AccentSubtle : Tok.FillSubtleTransparent,
    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
    Children =
    [
        Icon(glyph, 14f).Foreground(sel ? Tok.AccentDefault : Tok.TextSecondary),
        new TextEl(label) { Size = 14f, Color = sel ? Tok.TextPrimary : Tok.TextSecondary },
    ],
};
```

**Spacing** is the WinUI Gallery spacing rhythm, on `Spacing` (`src/FluentGpu.Engine/Dsl/Spacing.cs`) — named `const
float` DIP values for page gutters, card spacing, internal padding, and stack gaps:

```csharp
Spacing.PageWide    // 36f — desktop page gutter (the NavigationView content margin)
Spacing.PageNarrow  // 16f — narrow layout
Spacing.Card        // 12f — between cards in a grid
Spacing.Inner       // 16f — card / panel internal padding
Spacing.StackS / StackM / StackL   // 4f / 8f / 12f — stack gaps

Spacing.PagePadWide    // Edges4(36, 24, 36, 36) — the page padding shape
Spacing.PagePadNarrow  // Edges4.All(16)
Spacing.CardPad        // Edges4.All(16) — card internal padding
```

Use them on the layout props you already know — `Gap` (inter-child spacing) and `Padding` / `Margin` (an
`Edges4`). The built-in `Card` builder already pads with `Spacing.CardPad` and gaps with `Spacing.StackM`, so a
plain `Card(...)` is on-rhythm for free:

```csharp
new BoxEl
{
    Direction = 1, Gap = Spacing.StackM, Padding = Spacing.CardPad,
    Children = [ /* … */ ],
};
```

Sizes are device-independent pixels (DIP) and the host scales by monitor DPI. You will also see literal DIP
across the gallery (`Gap = 16f`, `Padding = Edges4.All(24)`) — both are fine; reach for `Spacing.*` when you
want the values to match the Fluent rhythm without memorizing them.

## Engine internals: the theming subsystem

How tokens, the live system accent, and album-art-derived dynamic color all converge on a single brush handle —
the three-tier `BrushDeriver`, the `Context<uint>` epoch reactivity for OS color changes, derived-brush
eviction safety, and the recolor cross-fade — is the engine's job, not the app author's. If you are changing
those internals (not just building an app), read the subsystem design:

- **[Theming subsystem design](../../../design/subsystems/theming.md)** — the authoritative engine spec (three
  brush tiers, `Palette`, `BrushDeriver`, OS reactivity, eviction, where each piece lands in the 13-phase
  loop, and the macOS boundary).
- **[Design corpus index](../../../design/SPEC-INDEX.md)** — the precedence authority for cross-cutting
  contracts.

For app work, the token surface on this page is everything you need.

---

Next: **[components, elements & layout](../../guide/components-elements-layout.md)** for the full element/control
catalog, or **[pitfalls](../../guide/pitfalls.md)** for the symptom → cause → fix table (including the
hard-coded-color trap).
