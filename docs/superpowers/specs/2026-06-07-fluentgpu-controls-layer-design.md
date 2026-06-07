# FluentGpu.Controls — SDK controls layer + WinUI-sourced styles

**Date:** 2026-06-07
**Status:** Design approved (pending spec review)
**Scope:** Refactor + WinUI styling of the existing controls, **plus** the formerly-deferred work folded
in at the user's direction (no deferral): the gradient elevation-border engine feature (§6.1), crossfade
tuning (§6.2), thumb hover-grow (§6.3), NavView acrylic wiring (§6.4), and the `design/` corpus sync (§7).

## 1. Goal

Today the user-facing controls are split by an implementation accident: `Button` + the misc
controls live in `FluentGpu.Dsl` (because they are pure `BoxEl` factories), while `NavigationView`,
`PageHost`/`Navigator`, `Repeater`, and `Virtual` live in `FluentGpu.Reconciler` (because some are
stateful `Component`s). From an SDK user's view they are all *controls*. This refactor:

1. Introduces a top-of-graph **`FluentGpu.Controls`** assembly so `using FluentGpu.Controls;` is all an
   app needs to compose UI — one namespace, no split.
2. Kills the `Controls.IconButton(...)` stutter by flattening the `Controls` facade into per-control
   classes (`IconButton.Create`, …), matching the existing `Button.Accent`/`Button.Standard` shape.
3. Extends Button's **Style pattern** to every visual control, with every default **sourced from the
   real WinUI 3 resource dictionaries** at `C:\WAVEE\microsoft-ui-xaml`.

### Decisions (locked)
| # | Decision | Choice |
|---|---|---|
| D1 | What moves to `FluentGpu.Controls` | **All composition factories** (`VirtualListEl` stays in Reconciler) |
| D2 | Misc-control naming | **Per-control class + `.Create`** |
| D3 | Migration | **Hard cut** — move types, update all in-repo call sites, delete old definitions, no shims |
| D4 | Packaging | **New `FluentGpu.Controls` assembly** (26 → 27 projects) |
| D5 | Style organization | **Nested `Style` record + `partial` class, one file per control** (`Button.Style`, `Slider.Style`, …) |
| D6 | Style scope | **All 6 visual controls** (Button, IconButton, ToggleButton, Slider, ScrollBar, NavigationView) |
| D7 | IconButton shape | **WinUI rounded-square, r = `Radii.Control` (4)** |
| D8 | Slider thumb | **Add the WinUI thumb** (ring + accent inner dot) |
| D9 | Elevation borders | **Add the gradient-stroke engine feature** (§6.1) — not a flat approximation |
| D10 | Thumb animation | **Slider/ScrollBar become Component-backed internally** (§6.3); public `.Create` API unchanged |

## 2. The assembly move

New project `src/FluentGpu.Controls/FluentGpu.Controls.csproj` (`IsAotCompatible=true`), referencing
**Foundation, Dsl, Hooks, Animation, Scene, Reconciler**. Apps (`VerticalSlice`, `WindowsApp`) add a
`ProjectReference` to it.

### 2.1 File moves — all moved types change to `namespace FluentGpu.Controls`
| From | To | Contents |
|---|---|---|
| `Dsl/Style.cs` | *(deleted; folds into `Controls/Button.cs`)* | `ButtonStyle` → nested `Button.Style` |
| `Dsl/Controls/Button.cs` | `Controls/Button.cs` | `Button` (+ nested `Style`) |
| `Dsl/Controls/Controls.cs` | `Controls/IconButton.cs`, `ToggleButton.cs`, `Slider.cs`, `ScrollBar.cs` | **split**; `Controls` facade **deleted** |
| `Reconciler/NavigationView.cs` | `Controls/NavigationView.cs` | `NavigationView`, `NavItem`, `PaneMode`, internal `NavIndicator` |
| `Reconciler/Navigation.cs` | `Controls/Navigation.cs` | `Route`, `Navigator`, `PageHost`, `Nav` |
| `Reconciler/Repeater.cs` | `Controls/Repeater.cs` | `Repeater`, `RepeatLayout`, `RepeatKind` |
| `Reconciler/Virtual.cs` | **split** | `Virtual` factory → `Controls/Virtual.cs`; **`VirtualListEl` record STAYS** in `Reconciler/VirtualListEl.cs` |

`IVirtualLayout`/`StackVirtualLayout`/`GridVirtualLayout` stay in **Scene** (unchanged).

### 2.2 Cycle proof
`Reconciler.cs` references **only `VirtualListEl`** (which stays) — none of the moved types
(`MountVirtual`/`RealizeWindow`/the patch switch). `Controls → Reconciler` is one-way. The only
lower-layer mentions of moved types are **comments** in `Dsl/Icons.cs` and `Dsl/Factories.cs` (updated,
not code). `Component`/`ComponentEl`/`Embed`/`Ctx`/`Context<T>` live in **Hooks** and are untouched.

### 2.3 Internal-access integrity
`NavigationView` + `NavIndicator` move together → `internal NavigationView.IndicatorTarget` stays
intra-assembly. The two `Nav` symbols (nested `NavigationView.Nav` field vs. top-level `Nav` class)
coexist exactly as today.

### 2.4 Call-site updates (hard cut; in-repo only)
- Add `using FluentGpu.Controls;` + a `ProjectReference` to `VerticalSlice` and `WindowsApp`.
- **Namespace-only** (call shape identical): `Button.Accent`, `Virtual.List/Grid/VariableList`,
  `Repeater.ItemsRepeater`, `RepeatLayout.*`, `NavigationView`, `PageHost`, `Navigator`, `Route`,
  `NavItem`, `PaneMode`.
- **Renamed**: `Controls.Slider/ToggleButton/IconButton/ScrollBar(...)` →
  `Slider.Create / ToggleButton.Create / IconButton.Create / ScrollBar.Create(...)`.
- **Signature change** (params folded into `Style`): `IconButton` drops its `size`/`foreground`
  positional args; `ScrollBar` drops its `width` arg. A non-default value migrates to
  `IconButton.DefaultStyle with { Size = N }` (default 36 → just drop the arg).
- Delete old files + the empty `Dsl/Controls/` folder; remove newly-unused `using FluentGpu.Reconciler;`.

## 3. The Style pattern (D5)

Every visual control gets the same five-part shape — nested record + `partial` class, one file:

```csharp
public static partial class Xxx
{
    public sealed record Style { /* tailored knobs; init props; structural defaults only */ }
    public static Style? StyleOverride;                       // global override hook
    public static Style DefaultStyle => StyleOverride ?? new Style { /* …colors from Tok… */ };
    public static BoxEl Create(/*content + callbacks*/, Style? style = null) { var s = style ?? DefaultStyle; … }
}
```

Rules:
- **Colors come from `Tok` in `DefaultStyle`** (override-aware: `Tok.AccentSecondary` already folds the
  live OS accent), not baked into the record — matches today's `ButtonStyle` idiom.
- Appearance/dimension knobs live in `Style`; the factory keeps only content + behavior params + `Style?`.
- `NavigationView` is a `Component`: `public Style? Style` instance field + `static Style? StyleOverride`
  + `static Style DefaultStyle`; `Render()` reads `var s = Style ?? DefaultStyle;`. Content/behavior
  fields (`Items`/`Footer`/`Initial`/`OnSelect`/`Content`/`Header`/`ShowBackButton`/`OnBack`) stay
  instance fields; the dimensional config (`PaneWidth`/`CompactWidth`/thresholds) moves into `Style`.

## 4. Token additions

WinUI extraction confirmed our `Tok` table is a faithful `*_themeresources` port. The one real gap is
`ControlStrongFillColorDefault` (Slider rail + ScrollBar thumb), plus two companions. Add to `TokenSet`
+ `Tok` (getters) + both `BuildDark()`/`BuildLight()`:

| New token | WinUI key | `ColorF` Light | `ColorF` Dark |
|---|---|---|---|
| `FillControlStrong` | `ControlStrongFillColorDefault` | `FromRgba(0x00,0x00,0x00,0x72)` | `FromRgba(0xFF,0xFF,0xFF,0x8B)` |
| `FillControlStrongDisabled` | `ControlStrongFillColorDisabled` | `FromRgba(0x00,0x00,0x00,0x51)` | `FromRgba(0xFF,0xFF,0xFF,0x3F)` |
| `FillControlSolid` | `ControlSolidFillColorDefault` | `FromRgba(0xFF,0xFF,0xFF)` | `FromRgba(0x45,0x45,0x45)` |

`Tok.ScrollThumb` (Dark `#8AFFFFFF` / Light `#72000000`) is ≈`FillControlStrong`; the ScrollBar control
points at the new canonical token and `ScrollThumb` is folded into it (set its Dark alpha to `0x8B`).

## 5. Per-control Style specs (WinUI-sourced)

All hex below resolved from `controls/dev/CommonStyles/*_themeresources.xaml` +
`Common_themeresources_any.xaml` (Default = Dark dict, Light dict). Accent values are runtime
OS-accent aliases via `Tok.Accent*`.

### 5.1 `Button.Style` (unchanged values — already WinUI-exact)
Fields: `Background`, `Foreground`, `Border`, `HoverBackground`, `PressedBackground`,
`BorderWidth = 1`, `CornerRadius = Radii.Control` (4), `Padding = new Edges4(11,5,11,6)`,
`FontSize = 14`, `MinHeight = 32`, `Bold = false`. Two variants (override fields kept):
- `AccentStyle`: Bg `Tok.AccentDefault`, Fg `Tok.TextOnAccentPrimary`, Border `Tok.StrokeControlOnAccentDefault`, Hover `Tok.AccentSecondary`, Pressed `Tok.AccentTertiary`.
- `StandardStyle`: Bg `Tok.FillControlDefault`, Fg `Tok.TextPrimary`, Border `Tok.StrokeControlDefault`, Hover `Tok.FillControlSecondary`, Pressed `Tok.FillControlTertiary`.

(Switch the hardcoded `with { A = 0.9f/0.8f }` hover/pressed to `Tok.AccentSecondary/Tertiary` — same value, override-aware.)

### 5.2 `ToggleButton.Style` — **fixes: CornerRadius 6→4, Padding (12,6)→(11,5,11,6), add 1px border**
Fields + defaults: `CornerRadius = Radii.Control` (4), `Padding = new Edges4(11,5,11,6)`,
`MinHeight = 32`, `FontSize = 14`, `BorderWidth = 1`,
`OnBackground = Tok.AccentDefault`, `OnHover = Tok.AccentSecondary`, `OnPressed = Tok.AccentTertiary`,
`OnForeground = Tok.TextOnAccentPrimary`, `OnBorder = Tok.StrokeControlOnAccentDefault`,
`OffBackground = Tok.FillControlDefault`, `OffHover = Tok.FillControlSecondary`,
`OffPressed = Tok.FillControlTertiary`, `OffForeground = Tok.TextPrimary`,
`OffBorder = Tok.StrokeControlDefault`.
Factory: `Create(string label, bool on, Action onToggle, Style? style = null)`.
(WinUI's checked/unchecked borders are elevation *gradients* — see §6; we use the flat resting stop.)

### 5.3 `IconButton.Style` — **fixes: subtle fills, square r=4 (D7), pressed glyph color**
Fields + defaults: `Size = 36`, `GlyphSize = 16` (WinUI icon box, fixed),
`CornerRadius = Radii.Control` (4), `IconFont = Theme.IconFont`, `TransitionMs = Motion.ControlFast` (83),
`Foreground = Tok.TextPrimary` (rest + hover), `PressedForeground = Tok.TextSecondary`,
`Fill = ColorF.Transparent`, `HoverFill = Tok.FillSubtleSecondary`, `PressedFill = Tok.FillSubtleTertiary`.
Factory: `Create(string glyph, Action onClick, Style? style = null)`.

### 5.4 `Slider.Style` — **fixes: rail token, add thumb (D8)**
Fields + defaults: `TrackHeight = 4`, `TrackCornerRadius = 2`, `RailFill = Tok.FillControlStrong`,
`ValueFill = Tok.AccentDefault`, `ThumbDiameter = 18`, `InnerThumbDiameter = 12`,
`ThumbFill = Tok.AccentDefault`, `ThumbRing = Tok.FillControlSolid`,
`ThumbBorder = Tok.StrokeControlDefault`, `ThumbBorderWidth = 1`.
Factory: `Create(float value, Action<float> onChange, float width = 200, float height = 24, Style? style = null)`
(width/height = sizing params). Thumb is centered on `value`; ring + 1px border + accent inner dot.
Inner-dot scale 12→14 hover / →10 pressed via the inner-thumb sub-component (§6.3). Add
`ThumbBorderBrush = Tok-assembled ControlElevationBorderBrush` (§6.1) for the 1px elevation edge.

### 5.5 `ScrollBar.Style` — **fixes: thumb token, MinThumb 24→30, radius 4→3, no hover recolor**
Fields + defaults: `ThumbWidth = 8` (collapsed), `ExpandedWidth = 12`, `MinThumb = 30`,
`CornerRadius = 3`, `Thumb = Tok.FillControlStrong`, `ThumbDisabled = Tok.FillControlStrongDisabled`.
Factory: `Create(float fraction, float position, Action<float> onScroll, float height = 200, Style? style = null)`.
WinUI keeps the thumb color constant (only widens on hover) — no hover recolor; the thumb widens
8→12 on hover via the inner-thumb sub-component (§6.3).

### 5.6 `NavigationView.Style` — **fix: item corner radius 4→8; all else already WinUI-exact**
Dimensions (defaults): `OpenPaneWidth = 320`, `CompactWidth = 48`, `CompactThreshold = 1008`,
`MinimalThreshold = 641`, `TopPaneHeight = 48`, `PaneHeaderRowHeight = 40`, `PaneToggleWidth = 40`,
`PaneToggleHeight = 36`, `ItemHeight = 36`, `ItemOuterHeight = 40`, `ItemMarginX = 4`, `ItemMarginY = 2`,
`HeaderHeight = 36`, `IconColumnWidth = 40`, `IconSize = 16`, `IndicatorW = 3`, `IndicatorH = 16`,
`IndicatorRadius = 2`, **`ItemCornerRadius = Radii.Overlay` (8) ← FIX**, `ContentCorner = new CornerRadius4(8,0,0,0)`.
Colors (defaults): `PaneFill = Tok.AcrylicBase`, `ContentFill = Tok.FillLayerDefault`,
`ContentBorder = Tok.StrokeCardDefault`, `Divider = Tok.StrokeDividerDefault`,
`ItemHoverFill = Tok.FillSubtleSecondary`, `ItemPressedFill = Tok.FillSubtleTertiary`,
`ItemSelectedFill = Tok.FillSubtleSecondary` (WinUI: selected == hover brush),
`Indicator = Tok.AccentDefault`, `TextPrimary = Tok.TextPrimary`, `TextSecondary = Tok.TextSecondary`,
`ToggleHoverFill = Tok.FillSubtleSecondary`, `TogglePressedFill = Tok.FillSubtleTertiary`.

The current public dimensional fields (`PaneWidth`/`CompactWidth`/`CompactThreshold`/`MinimalThreshold`)
move into `Style`; any call site that set them migrates to `Style = NavigationView.DefaultStyle with { … }`.

## 6. Formerly-deferred work — now IN SCOPE (no deferral)

A 5-agent architecture sweep (`gaps-impl-research`, 2026-06-07) found the engine is much further along
than §6's old "honest gaps" assumed. Three of the items are **already built**; the rest is bounded.
Each item below cites its real state and the concrete approach.

### 6.1 Gradient elevation border — **NEW engine feature** (the one heavy item)
WinUI's `ControlElevationBorderBrush` (Button/ToggleButton borders) and the Slider thumb's
`SliderThumbBorderBrush` are 2-stop **vertical gradient** strokes (`ControlStrokeColorSecondary`@0.33 →
`ControlStrokeColorDefault`@1.0; the accent variant flips the stops). Today **every gradient is a fill
clipped by the SDF and every border is a solid color — the two never combine.** Clean path (reuses the
existing `GradientPipeline`, no new pipeline, stride/root-sig unchanged):

1. **DSL** — add `GradientSpec? BorderBrush { get; init; }` to `BoxEl` (`Element.cs`) + a `.BorderBrush(spec, width)` modifier (`Modifiers.cs`). Assemble `Ui.ControlElevationBorderBrush` + accent-flipped variant in `Tokens.cs` from existing `StrokeControl*` tokens via `LinearGradient(90,…)` (`Factories.cs`). Reuse `GradientSpec`/`GradientStop` (`Foundation/Effects.cs`) unchanged.
2. **Scene** — sparse `_borderBrushes` side-table in `SceneStore.cs` (mirror `_gradients`: `Set/TryGet/Clear` + `FreeSubtree` removal). `BorderWidth` stays in the dense `NodePaint` column.
3. **Reconciler** — write `BorderBrush` to the side-table next to the existing gradient write (`Reconciler.cs:~421`).
4. **DrawList** — new `DrawGradientStroke = 11` opcode + writer (mirror `GradientRect`), payload = `DrawGradientRectCmd` + `float StrokeWidth`.
5. **GPU** — add `float Stroke` to `GradientInstance` using a spare pad (`Pad0`, **160-byte stride unchanged**); in the PS swap the fill-coverage line for the stroke-band formula already in `RoundRectPipeline` (`cov = clamp(0.5 - (abs(d) - stroke*0.5)/fw)`) when `stroke>0` — gradient `t`/color math untouched. Decode in `D3D12Device.cs` (+ `StreamHasLayer`/size-skip) and `HeadlessGpuDevice.cs` (golden path).
6. **Recorder** — in the `VisualKind.Box` case, when a border brush exists, emit the gradient-stroke ring at the edge-centered rect (`EmitBorderRing` geometry, inset `bw*0.5`) via the new opcode; composes with the existing gradient-fill branch.

Then `Button.Style`/`ToggleButton.Style`/`Slider.Style.ThumbBorderBrush` reference the assembled
brushes instead of the flat `StrokeControl*` resting stop. **Risks:** use the SDF band consistently (not
the solid two-quad "donut") to avoid double-painting the edge; author the @0.33 stop premultiplied;
keep the recorder lookup 0-alloc (dict `TryGetValue`, the established pattern). Corner radius uses
`radii.x` (uniform) — fine for the 4px control radius. Effort: **Medium (~1–1.5 day)**.

### 6.2 83ms brush crossfade — **ALREADY BUILT; tune + correctness only**
`InputDispatcher` → `OnHover/PressChanged` → `InteractionAnimator` (eases `InteractionAnim.HoverT/PressT`)
→ `SceneRecorder.ResolveSurface` lerps `Fill`/`Border`/`HoverFill`/`PressedFill` at record time — fully
compositor-side, **no component re-render**, already wired in `AppHost`. Fill does **not** snap today.
Residual work:
- **Duration:** retune the `InteractionAnimator` time-constants (currently 40ms/30ms tau) to land WinUI's `Motion.ControlFast` (83 ms). For an exact match, switch the exponential approach to elapsed-time eased keyframes (store start value + start time per row); centralize the 83 ms as the existing `Motion.ControlFast` token, not a magic number.
- **Color correctness:** the crossfade uses `ColorF.Lerp` (straight sRGB), violating the linear-blend/premultiplied color canon. Add a **separate** `ColorF.LerpLinear` (sRGB→linear→lerp→sRGB) in `Geometry.cs` and call it only from `ResolveSurface` — do **not** change the shared `Lerp` (other consumers depend on it).
- **Coverage:** add a headless golden/alloc test that injects a `PointerMove`, wires `OnHoverChanged`→`InteractionAnimator` like `AppHost`, ticks, and asserts the recorded fill at frame 0 / mid / settled — plus 0 managed alloc on the steady tick.
- **Efficiency (optional):** gate the steady-state present so the always-on loop stops re-recording an unchanging frame (`present only when _dirty || _interact.HasActive || _anim/_scrollAnim/_images active`).

Effort: **Small (~0.5–1 day incl. test).**

### 6.3 Thumb hover-grow (slider 12→14/→10, scrollbar 8→12) — **reuse existing spring; no engine change**
The scale machinery exists (`AnimChannel.ScaleX/ScaleY`, `AnimEngine` springs, `MotionHooks.UseHoverScale`),
but only a **stateful Component** path reaches it; the stateless `InteractionAnimator` path drives color
only. **Decision:** make `Slider` and `ScrollBar` internally **Component-backed** — the public
`Slider.Create(...)`/`ScrollBar.Create(...)` returns `Embed.Comp(() => new SliderImpl{…})`; the impl
tracks hover/press (`UseState` flipped by pointer) and renders an **inner thumb sub-component** that
springs `ScaleX/ScaleY` (`UseHoverScale`-style, with a press branch) about its center. Rest size = layout
size; hover/press = a scale ratio (dot 14/12≈1.167, 10/12≈0.833; scrollbar 12/8=1.5). Scale is a
**composited transform → does not change layout or hit-test** (the click rect stays put — correct Fluent
behavior, confirmed: `InputDispatcher.HitTest` reads `Bounds`, never `LocalTransform`). Honors
`Motion.ReducedMotion`. Button/IconButton/ToggleButton stay stateless `BoxEl` (WinUI doesn't scale them;
their hover/press is the §6.2 color crossfade). Effort: **Low.**

### 6.4 Real acrylic on the always-visible NavView pane — **wire existing compositor**
The `AcrylicCompositor` (real per-node frosted glass, `PushLayer/PopLayer`) is built and already used on
the NavView **overlay** pane (`Acrylic = AcrylicSpec.InAppDefault`) and its shadow (`Elevation.Flyout`).
The **expanded/compact** (always-visible) pane currently uses a flat `Tok.AcrylicBase` color. Switch
`NavigationView.Style.PaneFill` to drive a real `AcrylicSpec.InAppDefault` on that pane node too (set via
`SceneStore.SetAcrylic`). **Caveat:** acrylic = one downsample+blur per layer per frame — apply to the
pane only, never per list item. Effort: **Low (wiring).**

### 6.5 ScrollBar control track + hover-thicken — **small recorder additions**
(For the *manual* `ScrollBar.Create` control — distinct from the virtualized-viewport auto-hide overlay,
which is already done via `ScrollAnimator.FadeT` + `EmitScrollbar`.) The 8→12 hover-thicken is §6.3; an
optional WinUI track rect behind the thumb is a `FillRoundRect` (or an acrylic track via `SetAcrylic`).
Effort: **Low.**

## 7. Corpus sync — IN SCOPE (was §8)
The `design/` corpus **already registers** a `FluentGpu.Controls` assembly — but as the *aspirational*
lookless control kit (ControlTemplate/ControlTheme/VisualState, 19 controls). The shipped assembly is the
composition-factory hoist. Edits are **reconciliation, single-owner-safe** (edit existing rows, do not add
duplicates):
- `SPEC-INDEX.md` §2 (the existing "Control kit + devtools assemblies" row): add an "as-shipped" clause (the 6 visual controls + nav/repeater/virtual factories, Style-record styling).
- `subsystems/controls.md`: add the shipped controls to the §13 catalog (IconButton, ToggleButton, NavigationView+NavItem+PaneMode, Navigator, PageHost, Route, Nav, Repeater+RepeatLayout, Virtual factory); add a "Phase 0 / shipping status" note; note `VirtualListEl` stays in Reconciler; **fix the dependency claim** (see ratification below).
- `subsystems/README.md` §2.6 + `dsl-aot.md` §3.4: correct the `FluentGpu.Controls` deps from "Dsl/Hooks/Foundation only" to the real set (**+Reconciler, +Scene, +Animation**).
- `theming.md`: register `FillControlStrong`/`FillControlStrongDisabled`/`FillControlSolid` (+ note `ScrollThumb` folds into `FillControlStrong`) → their WinUI keys. (Leaf token values — no `SPEC-INDEX` §2 row.)
- Assembly count: `architecture-spec.md` §3, `foundations.md`, `README.md` say "18 assemblies" — stale. Restate as "design-core graph (now incl. Animation/Hooks/Controls) + OS/test leaves; 28 projects" and add `Controls` above `Reconciler` in the §3 ASCII graph.
- Run `check-canon.ps1` — **expected PASS**; its 3 rules (handle-layout / COM-blanket / DepKey-union) don't match any new token/assembly/control name. No `canon-allow` comments needed.

> **Ratification (the one genuine architectural delta):** the corpus placed `FluentGpu.Controls` as an
> "app-like leaf" referencing only `Dsl/Hooks/Foundation`. The shipped assembly **must** reference
> `Reconciler` (NavigationView/PageHost are `Component`s; Repeater/Virtual factory need Reconciler types),
> `Scene` (`IVirtualLayout`), and `Animation`. This is **still acyclic** (Reconciler refs only
> `VirtualListEl`, which stays → `Controls → Reconciler` one-way, per §2.2). Relaxing the placement claim
> is recorded explicitly here, not changed silently.

## 8. Implementation phases (sequencing for the plan)
1. **Engine: gradient border** (§6.1) — DSL `BorderBrush` + token brushes → Scene side-table → Reconciler write → `DrawGradientStroke` opcode → `GradientPipeline` stroke band → D3D12 + Headless decode → recorder emit → **headless golden**.
2. **Engine: crossfade correctness** (§6.2) — `ColorF.LerpLinear` + `ResolveSurface` + 83 ms tune + **headless golden**.
3. **Tokens** — `FillControlStrong`/`FillControlStrongDisabled`/`FillControlSolid` + `ControlElevationBorderBrush`(+accent) in `Tokens.cs` (`TokenSet` + getters + `BuildDark/BuildLight`); fold `ScrollThumb`.
4. **The move + new assembly** (§2) — `FluentGpu.Controls.csproj`; move files + namespaces; `VirtualListEl` stays; delete facade; csproj refs.
5. **Style records** (§3, §5) — per-control nested `Style` w/ WinUI values; Slider/ScrollBar become Component-backed for hover-grow (§6.3); borders use `BorderBrush`; Slider gets the thumb.
6. **Call-site hard cut** (§2.4) — `VerticalSlice` + `WindowsApp`; rename facade calls; fold `size`/`width` into `Style`.
7. **NavigationView WinUI polish** (§5.6, §6.4) — item radius 4→8; real acrylic on the always-visible pane.
8. **Corpus sync** (§7) — the doc edits + `check-canon.ps1`.
9. **Verification** (§9).

## 9. Verification
- Build all **28 projects** (the app projects pull everything transitively) — must compile, NativeAOT-clean.
- **VerticalSlice headless golden checks** stay green, **plus new headless goldens**: gradient-stroke
  border (assert a `LastGradients` entry with a stroke width), the crossfade curve (fill at t=0/mid/settled),
  and the thumb scale spring (ScaleX/Y over frames). Assert **0 managed alloc** on the steady animation tick.
- `check-canon.ps1` passes after the §7 corpus edits.

## 10. Residual out of scope
- The full lookless **ControlTemplate/ControlTheme/VisualState kit** (the corpus's larger `controls.md`
  vision) — this change ships the composition-factory hoist + Style records, explicitly noted as "Phase 0".
- Driving `AcrylicSpec.BlurSigma` into the compositor kernel radius (currently a fixed 5-tap) — pre-existing
  engine limitation, unrelated to this change.
