# FluentGpu Subsystem — Accessible-by-Default Control Kit (`FluentGpu.Controls`)

*Owner doc for the **control kit** (L8) — the accessible-by-default library of composed controls (Button, Checkbox,
RadioGroup, Switch, Slider, ProgressBar/Ring, TextField/TextBox, ComboBox, ListView/GridView, TreeView, Tabs,
Menu/MenuBar/ContextMenu, Dialog/Flyout/Popup, ToolTip, Scrollbar, Expander, InfoBar) **and** the
control-template/styling system that composes a control's visual tree + theme tokens + interaction states. The gap
analysis (L8) said "fold-soon, **after** L1/L2/L3/L4/L6 stabilize" — those seams are now core (text.md §15–18,
input-a11y.md §7A/§11.7–11.9, layout.md §10A/§10B, reconciler-hooks.md §7.1/§9.5/§9.6), so this doc folds the control
kit into **core now**. Every control wires the **real** seams; nothing here is a v2 placeholder.*

*Cross-cutting contracts are OWNED elsewhere and only **referenced** here. This doc owns ONLY: the
`FluentGpu.Controls` assembly, the control-template/styling system (`ControlTemplate`/`VisualStateSet`/`ControlTheme`),
and the per-control composition (which seams each control wires, its UIA pattern, keyboard contract, and a11y
name/role). It introduces **no new SceneStore column, no new DrawList opcode, no new PAL seam, no new RHI method, and
no new engine hook** — by design, the seam program already minted every primitive a control needs. See
`SPEC-INDEX.md` for precedence; `foundations.md`/`architecture-spec.md` §4 for handles/columns/phases;
`reconciler-hooks.md`, `input-a11y.md`, `text.md`, `layout.md`, `theming.md`, `backdrop-effects-animation.md`,
`gpu-renderer.md`, `dsl-aot.md`, `validation.md`.*

---

## 0. Posture & one-line thesis

A FluentGpu control is **not a node type and not a peer object** — it is a **`Component` (reconciler-hooks.md) that
composes primitive Elements (`dsl-aot.md`) and wires the interaction/semantics seams via hooks**. There is exactly
one tree (the retained SoA RenderNode tree); a control is a *render function over props + hooks*, identical in kind to
any app component. This is the single most important consequence of FluentGpu's draw-everything model: because no
native control sits behind anything, a control is "just" the **disciplined composition** of the seams the engine
already owns. `FluentGpu.Controls` is therefore a **leaf assembly** (apps reference it; it references `Dsl`, `Hooks`,
and — *through the portable seams only* — Input/Text/Layout/Animation/Theme).

The hardest correctness facts this doc commits to:

1. **Accessible-by-default is structural, not opt-in.** Every control sets its `A11yInfo.{ControlType, Name,
   Patterns}` + `NodeFlags.{Focusable, HitTestVisible, A11yPresent}` + the keyboard contract **as part of its render**
   — there is no path to instantiate a control that is not name/role/pattern-complete. The DEBUG `AccessibilityScanner`
   (input-a11y.md §11.4 shared auto-name helper / validation.md) lints any control whose `Name` would resolve empty.
2. **A control owns no GPU object, no ComPtr, no native handle.** It writes only Elements; the reconciler writes
   columns; the render thread owns every ComPtr (hardened-v1 §2.1). Controls are 100% portable and run UI-thread
   phases 4–6.5 like any component. macOS recompiles them unchanged.
3. **Controls consume seams; they do not redesign them.** A `ComboBox` opens its dropdown through the **same**
   `OverlayManager` + `OverlayPlacement.Resolve` a `Flyout` uses; a `Slider` drags through the **same** gesture arena
   a selection-drag uses; a `TextBox` edits through the **same** `ITextDocument` the IME drives. One seam, many
   consumers — never a per-control fork.
4. **The styling system is token-driven and zero-opcode.** A control's visual is a `ControlTemplate` (a render
   function) parameterized by `ControlTheme` (a `FrozenDictionary<PartTokenId, *Token>` riding theming.md's `Tok.*`
   machinery). Restyling = supplying a different template/theme via context; it changes **which `Element`s and which
   `BrushHandle`s** are produced, never the renderer.
5. **Interaction state is a derived projection, not stored mutable widget state.** `VisualState` (Rest/Hover/Pressed/
   Focused/Disabled/Checked/…) is computed each render from `UseHover`/`UseFocus`/`UsePressed` + props via
   `UseDerived` (reconciler-hooks.md §8.5) so a state change is a **boxless `DepKey` flip** that re-renders only the
   control, and a pure-hover visual is a `PaintDirty`-only frame (no relayout).
6. **No control hides a user bug.** Following Reactor philosophy: a control surfaces invalid props (a `RadioGroup`
   with duplicate values, a `Slider` with `Min > Max`) via a DEBUG assert / the unhandled sink — it does not silently
   clamp-and-continue in a way that masks the defect.

---

## 1. Assembly, deps, threads

| Concern | Assembly | Deps | Thread | COM? |
|---|---|---|---|---|
| The control kit + control-template/styling system + per-control components | **`FluentGpu.Controls`** (portable) | **As-shipped (Phase 0):** Foundation, Dsl, Hooks, **Animation, Scene, Reconciler** (ratified — see §1.1 + below). *Aspirational lookless kit (future target):* `Dsl`, `Hooks`, `Foundation` + the portable Input/Text/Layout/Animation/Theme seams. | UI thread, phases 4–6.5 | **none** |

> **Dependency-claim ratification (the one genuine architectural delta).** This doc previously placed
> `FluentGpu.Controls` as an "app-like leaf" referencing **only** `Dsl`/`Hooks`/`Foundation`. The **shipped**
> assembly **also** references **`Reconciler`** (NavigationView/PageHost are `Component`s; the Repeater/`Virtual`
> factory needs Reconciler types), **`Scene`** (`IVirtualLayout`), and **`Animation`**. It stays **acyclic**:
> `VirtualListEl` (which `Controls` consumes via the `Virtual` factory) is **declared in `Reconciler`**, so the
> `Controls → Reconciler` edge is one-way with no back-edge. The corrected dependency set is mirrored in `subsystems/README.md` §2.6 and `dsl-aot.md` §3.4. The
> body below (§3–§13) still describes the aspirational lookless kit — see **§1.1** for what actually shipped.

### 1.1 As-shipped (Phase 0): the composition-factory hoist + per-control `Style` records

The shipped `FluentGpu.Controls` is **smaller** than the lookless `ControlTemplate`/`ControlTheme`/`VisualState`
kit the rest of this doc specifies. Phase 0 is a **composition-factory hoist** of the controls that already
existed (formerly split between `Dsl` and `Reconciler`) into one top-of-graph assembly, plus a **per-control
`Style` record** styling pattern. The lookless kit (§3) remains the **stated future target**, not yet built.

**What moved into `FluentGpu.Controls`** (all under `namespace FluentGpu.Controls`):

| Control / type | Form | Notes |
|---|---|---|
| `Button` (+ nested `Button.Style`) | static class; `.Accent`/`.Standard` + `.Create` | the old `ButtonStyle` is now nested `Button.Style` |
| `IconButton` | static class; `.Create` + nested `Style`/`StyleOverride`/`DefaultStyle` | rounded-square r4 (NOT a circle), 16px glyph |
| `ToggleButton` | static class; `.Create` + `Style` | corner 4, padding (11,5,11,6) |
| `Slider` | static class; `.Create` + `Style` | real thumb (ring + accent inner dot); Component-backed internally for hover-grow |
| `ScrollBar` | static class; `.Create` + `Style` | thumb = `FillControlStrong`, min length 30, radius 3; Component-backed internally |
| `NavigationView` (+ `NavItem`, `PaneMode`, internal `NavIndicator`) | stateful `Component` | keeps **public properties** for config (idiomatic) + a `Style` for dimensions/colors — NOT a nested `Style`-only record |
| `Navigator`, `Route`, `PageHost`, `Nav` | navigation Components/factories | moved from `Reconciler/Navigation.cs` |
| `Repeater` (+ `RepeatLayout`, `RepeatKind`) | factory | moved from `Reconciler/Repeater.cs` |
| `Virtual` (factory) | factory that builds `VirtualListEl` | **`VirtualListEl` record STAYS in `Reconciler`** (the reconciler diffs it directly, ElementTypeId 6) — only the `Virtual` factory moved |
| `Icons` (glyph constants) | static class | **moved `Dsl → Controls`** |

The old `Controls` static facade was **deleted** (no `Controls.IconButton(...)` stutter; each control is its own
class with `.Create`). `IVirtualLayout`/`StackVirtualLayout`/`GridVirtualLayout` stay in `Scene` (unchanged).

**The shipped `Style` pattern (Phase 0).** Each **stateless visual control** exposes a five-part shape:
a nested `Style` record (tailored knobs; structural defaults) + a static `StyleOverride` (global hook) + a
computed `DefaultStyle` (colors resolved from `Tok` so a theme swap re-themes) + a `.Create(content+callbacks,
Style? style = null)` factory. Three override layers, increasing locality: **per-instance** (pass a `Style`,
build via `with`), **global** (set `StyleOverride`), **ad-hoc** (chain modifiers). **Border is ONE knob:**
`BorderBrush` (a `GradientSpec?`) — solid via `GradientSpec.Solid(color)`, gradient via
`GradientSpec.Vertical(a,b)` or the elevation-border token helpers (`Tok.ControlElevationBorder` /
`Tok.AccentControlElevationBorder`, theming.md). **`NavigationView` is the exception** — being a stateful
`Component`, it keeps **public properties** for content/behavior config (idiomatic) plus a `Style` for the
dimensional/color config, rather than a nested-`Style`-record-only shape.

**Accessibility role (SHIPPED, new).** A control is a `BoxEl`, not a nominal type, so it announces its kind via
a new `AutomationRole` enum (in **Foundation**) surfaced through `BoxEl.Role` → the `InteractionInfo.Role` scene
column (input-a11y.md owns the column semantics). Control factories set it: Button/IconButton → `Button`,
ToggleButton → `ToggleButton`, Slider → `Slider`, ScrollBar → `ScrollBar`, NavigationView items →
`NavigationItem`. This is how the future UIA layer / devtools / UI tests read a control's type.

**WinUI fidelity notes (SHIPPED; sourced from the shipped WinUI `generic.xaml` + microsoft-ui-xaml).**
ToggleButton corner 4 + padding (11,5,11,6); IconButton is a rounded-square r4 (NOT a circle) with subtle fills
+ a 16px glyph; Slider has a real thumb (18px ring of `ControlSolidFillColorDefault` + a 1px elevation border +
a 12px accent inner dot); ScrollBar thumb = `ControlStrongFillColorDefault`, min length 30, radius 3;
NavigationView item corner radius = `OverlayCornerRadius` (8).

**Compositor additions (SHIPPED).**
- **WinUI 83ms hover/press brush cross-fade is now correct:** `ColorF.LerpLinear` (linear-light interpolation,
  honoring the linear-blend color contract — SPEC-INDEX §2 color row) is used by `SceneRecorder.ResolveSurface`;
  the `InteractionAnimator` is tuned to ~83ms (WinUI `ControlFasterAnimationDuration`).
- **Interaction-driven composited SCALE:** new `BoxEl.HoverScale`/`PressScale` + `InteractionAnim.HoverScale`/
  `PressScale`; the recorder scales a node about its centre by the eased hover/press of its nearest **interactive
  ancestor** (a slider/scrollbar thumb grows on control hover). **Composited only** — never changes layout or
  hit-test (HitTest reads `Bounds`, never `LocalTransform`).

**Honest constraint (acrylic vs. system Mica).** The engine's in-app acrylic (`AcrylicSpec`) renders the whole
frame through an **opaque** canvas RT, which overrides window transparency and **kills the DWM Mica window
backdrop**. So the NavigationView **EXPANDED (always-visible) pane uses a TRANSPARENT fill** (matching the
shipped WinUI `NavigationViewExpandedPaneBackground = SolidBackgroundFillColorTransparent`) so Mica shows
through; engine acrylic is used only for the **transient OVERLAY/flyout pane** (matching
`NavigationViewDefaultPaneBackground = AcrylicInAppFillColorDefaultBrush`). Real per-node acrylic does not
compose with the system Mica backdrop in this engine — a known limitation, recorded honestly per the project's
honesty discipline.
| Author-facing control entry points (the `Ui.*` factories) | re-exported via `FluentGpu.Dsl` `Ui` partial (so app code writes `Ui.Button(...)` next to `Ui.VStack(...)`) | `Controls` | UI thread, phase 4 | none |

`FluentGpu.Controls` carries `[assembly: DisableRuntimeMarshalling]` (100% blittable composition; no source-gen COM —
all COM lives in the OS leaves the seams already confine). It is **referenced only by apps and by `Dsl`'s `Ui`
re-export** (foundations §7 cycle invariant: `Controls` depends on `Dsl`+`Hooks`+seam interfaces, never on a concrete
OS leaf). The acyclic order is: `Foundation → Dsl/Hooks → (Input/Text/Layout/Animation/Theme ifaces) → Controls →
App`. There is no `Controls → OS-leaf` edge; an OS leaf is reached only via the portable seam interface that
`Input`/`Text`/etc. expose.

**Why this is `safe-by-construction` for confinement:** a control is a component; it writes Elements and declares
hooks. The reconciler (UI thread, phase 5) writes columns; the render thread reads the published `SceneFrame`. A
control never crosses the PUBLISH(13a) seam, never owns a ComPtr, and — because it routes all interaction through the
Input seams (arena/overlay/cursor) and all text through the Text seam — it inherits those subsystems' thread-
confinement proofs unchanged.

---

## 2. Where the control kit lands in the 13-phase loop

Reference: architecture-spec §4.8 + hardened-v1 §2.2. A control is a `Component`, so it lives exactly where every
component lives.

```
 1 pump            [UI]  (Input owns)
 2 input-dispatch  [UI]  (Input owns) — gesture arena resolves the control's gestures; OnClick/Space/Enter/arrows
                          re-enter the control's handler delegates; light-dismiss FSM closes the control's overlays;
                          cursor resolver picks the control's CursorId; edge-autoscroll arms for slider/list drag
 3 hook-flush      [UI]  (Reconciler owns) — the control's setState/UseTransition/UseOptimistic updates fold (one batch)
 4 render          [UI]  Control.Render(): builds the visual tree via its ControlTemplate; declares UseFocus/UseGesture/
                          UseCommand/UseOverlay/UseAnnounce/UsePointerCursor; computes VisualState via UseDerived;
                          edge-localizes any value text via ILocaleFormatter (→ StringId)
 5 reconcile       [UI]  (Reconciler owns) — writes the control's A11yInfo (ControlType/Name/Patterns/PositionInSet/…),
                          FocusNav (TabIndex), InteractionInfo (HitShape/HandlerMask/CursorId), SelectionState (text controls)
 6 layout          [UI]  (Layout owns) — arranges the control's parts; FlowDirection resolved logical→physical at WriteLayout;
                          overlay anchor placement-with-flip for open dropdowns/flyouts/menus
 6.5 layout-effects[UI]  UseFocus(autoFocus) for opened dialogs/menus; scroll-selected-item-into-view; ToolTip/overlay
                          placement reads valid Bounds; ListView ScrollToIndex
 7 animation       [UI]  (Animation owns) — Switch thumb spring, Expander animateContentSize, ProgressRing rotation,
                          checkbox check-draw, flyout reveal — all phase-7 AnimTrack writes (Transform/PaintDirty only)
PUBLISH (13a)      [UI]  SceneFrame sealed
 8-11              [RENDER] record/batch/submit/present — the control's parts record like any node; its focus ring /
                          selection highlight / overlay layers are the opcodes Input/Text already own
12 passive-effects [UI]  UseAnnounce (InfoBar appear, async-action settle) fires UIA Notification for seq ≤ last-presented
```

A control introduces **no new phase work**: it is render (4) + the reconciler/layout/animation/input work the seams
already do. The only control-specific obligation is that its `Render()` is **memo-clean** (3-signal skip,
reconciler-hooks §6.3) so an idle control costs one flag-load, and its hover/press visuals are `PaintDirty`-only.

---

## 3. The control-template / styling system

> This is the doc's first-class contribution: **how a control's visual tree + theme tokens + states compose, and how
> app authors restyle.** It is built entirely on existing machinery — `Component`/`Element` (reconciler-hooks/dsl-aot),
> `Context<T>` (reconciler-hooks §8), the `Tok.*` token + `FrozenDictionary<TokenId,…>` per-theme tables (theming.md
> §2 / dsl-aot token source-gen), `UseDerived` (reconciler-hooks §8.5), and `AnimTrack` motion tokens
> (backdrop-effects-animation.md §5). **No new opcode, no new column, no new hook.**

### 3.1 The three composable layers

A styled control = **Template × Theme × State**, all pure:

| Layer | Type | What it is | Authored by |
|---|---|---|---|
| **Template** | `ControlTemplate<TProps>` = `delegate Element (in TProps p, in ControlContext ctx)` | the **visual tree** (which primitive Elements) — the control's structure | framework (default) or app (override) |
| **Theme** | `ControlTheme` = `FrozenDictionary<PartTokenId, PartToken>` | the **token values** a template reads (brushes, corner radii, paddings, motion tokens) per `ThemeKind` | theming.md `Tok.*` source-gen; app adds/overrides via a derived theme |
| **State** | `VisualState` (a `[Flags] byte`) | the **interaction projection** (Rest/Hover/Pressed/Focused/Disabled/Checked/Indeterminate/Selected/Expanded/Invalid) | computed each render from `UseHover`/`UseFocus`/`UsePressed` + props |

```csharp
namespace FluentGpu.Controls;

// A control template is a pure render function: props + resolved context -> visual Element tree.
// It is a NAMED delegate (not Func) so it is AOT-friendly and storable in a Context column.
public delegate Element ControlTemplate<TProps>(in TProps props, in ControlContext ctx) where TProps : struct;

// Resolved styling context handed to a template: the theme tokens + the current visual state + flow direction.
public readonly ref struct ControlContext
{
    public readonly ControlTheme Theme;          // resolved FrozenDictionary<PartTokenId, PartToken>
    public readonly VisualState  State;          // the interaction projection (this render)
    public readonly FlowDirection Flow;          // resolved LTR/RTL (layout.md §10A) — for logical paddings/icons
    public readonly float        Dpi;            // for any px-snapped part metric
    public PartToken Tok(PartTokenId id) => Theme[id];   // token probe (FrozenDictionary: hash + slot)
}

[Flags] public enum VisualState : byte {
    Rest = 0, Hover = 1<<0, Pressed = 1<<1, Focused = 1<<2, Disabled = 1<<3,
    Checked = 1<<4, Indeterminate = 1<<5, Selected = 1<<6, Expanded = 1<<7,
    // (Invalid/ReadOnly etc. ride the per-control props; the byte holds the 8 universal visual axes)
}
```

`PartToken` is a 16B blittable union over the value kinds a template needs, so the theme table is POD and AOT-trivial:

```csharp
public enum PartTokenKind : byte { Brush, Length, Corners, Thickness, MotionToken, Flag }
[StructLayout(LayoutKind.Sequential)]
public readonly struct PartToken {           // 16B; no GC ref
    public readonly PartTokenKind Kind;
    public readonly BrushHandle   Brush;     // when Kind==Brush — already a theming.md BrushHandle (T0/T1/T2)
    public readonly float         Scalar;    // Length/Thickness
    public readonly CornerRadius4 Corners;   // when Kind==Corners (packed)
    public readonly MotionTokenId Motion;    // when Kind==MotionToken (backdrop-effects-animation §5)
    public readonly uint          Flag;      // Flag bits
}
// PartTokenId is a StringId-interned identity (e.g. "Button.Background.Rest", "Slider.Track.Fill") — the SAME
// interning the Tok.* source-gen uses; theming.md owns the token table machinery, this doc owns the control PART ids.
public readonly struct PartTokenId { public readonly StringId Id; }
```

### 3.2 How a template reads tokens by state (the WinUI "VisualStateManager", de-mutated)

WinUI's `VisualStateManager` is a stateful, animation-driven, mutate-the-tree machine. FluentGpu's is **pure**: the
state is recomputed each render, and the template selects tokens by state with a plain switch. There is no stored
"current visual state" object and no imperative `GoToState` — a state change is a re-render with a different `State`
byte, and the 3-signal memo skip ensures only the affected control re-renders.

```csharp
// The default Button template — illustrative; every control ships one of these.
static Element ButtonTemplate(in ButtonProps p, in ControlContext c)
{
    // State-keyed token selection (pure): pick the brush for the current VisualState.
    BrushHandle bg =
        c.State.Has(VisualState.Disabled) ? c.Tok(Part.Button_Bg_Disabled).Brush :
        c.State.Has(VisualState.Pressed)  ? c.Tok(Part.Button_Bg_Pressed ).Brush :
        c.State.Has(VisualState.Hover)    ? c.Tok(Part.Button_Bg_Hover   ).Brush :
                                            c.Tok(Part.Button_Bg_Rest    ).Brush;
    return Ui.Border(
              Ui.Text(p.Content).Foreground(c.Tok(Part.Button_Fg).Brush)
            ).Background(bg)
             .CornerRadius(c.Tok(Part.Button_Corners).Corners)
             .Padding(ResolveLogicalPadding(c.Tok(Part.Button_Pad).Scalar, c.Flow));   // RTL via layout.md §10A
}
```

- **State transitions that animate** (hover-fade, pressed-scale) are **not** template branches; they are the control
  declaring a `UseImplicitTransition` on the relevant part keyed by `VisualState` (backdrop-effects-animation.md §5
  connected/implicit animation) using a **motion token** from the theme (`c.Tok(Part.Button_HoverMotion).Motion`).
  Reduced-motion (backdrop §5.8) collapses the transition to an instant token swap automatically.
- **Token selection is boxless and memo-clean:** `bg` is a `BrushHandle` (a `Handle`, 8B); the state byte and the
  resolved brush feed `DiffProps` as scalars, so a hover flip re-records a `PaintDirty` span and nothing relayouts.

### 3.3 `ControlTheme` resolution + the restyle path (how app authors restyle)

```csharp
// The ambient styling context. Default supplied at the app root; app overrides by providing a derived theme/template.
public sealed class ControlTheme {
    readonly FrozenDictionary<PartTokenId, PartToken> _tokens;   // built once per (ThemeKind, accent, hc) — theming.md cache
    readonly ThemeKind _kind;
    public PartToken this[PartTokenId id] => _tokens.TryGetValue(id, out var t) ? t : Fallback(id);
    public ControlTheme With(ReadOnlySpan<(PartTokenId, PartToken)> overrides);  // returns a new frozen theme (build-once)
}

// Themes + per-control template overrides ride Context<T> (reconciler-hooks §8) — boxless change-check via ToDepKey.
public static readonly Context<ControlTheme> ControlThemeContext;                // ambient theme
public static readonly Context<TemplateRegistry> TemplateContext;                // ambient template overrides
```

Three restyle granularities, all pure and all over existing machinery:

1. **Token override (most common):** the app provides a `ControlTheme.With([(Part.Button_Bg_Rest, myBrush), …])`
   via `ControlThemeContext`. The default templates read it; only brushes/metrics change. The derived theme is built
   **once** (frozen) and cached keyed by `(ThemeKind, accent, hc)` exactly like theming.md's brush cache — a theme
   swap is a single `UseTheme` epoch bump, and the per-consumer boxless context change-check re-renders only controls
   that read the changed token.
2. **Template override (restructure one control):** the app registers a `ControlTemplate<ButtonProps>` in a
   `TemplateRegistry` provided via `TemplateContext`. A control resolves `ctx.Template ?? DefaultTemplate` at render;
   overriding it swaps the **visual tree** while the control's **behavior** (gestures/keyboard/UIA) stays in the
   component shell (§3.4). This is the "lookless control" pattern: behavior in the component, appearance in the
   template — but with the template a *pure function*, not a mutable XAML tree.
3. **Full component replacement:** because a control is just a component, an app can ignore `Ui.Button` and write its
   own — it only needs to wire the same seams (this doc's per-control sections are the checklist). The kit's controls
   are the reference implementation, not a sealed hierarchy.

**The behavior/appearance split (the lookless contract).** Every control factory is a thin **behavior shell**
component that: (a) owns the interaction hooks (gesture/focus/keyboard/UIA), (b) computes `VisualState`, (c) resolves
`(template, theme, flow)` from context, (d) calls `template(in props, in ctx)` to get the visual tree, and (e) wires
the visual tree's interactive parts (e.g. a slider thumb) to the shell's gesture/focus state. The shell is fixed
(so accessibility/keyboard can never be lost by restyling); only the template varies.

### 3.4 The behavior shell — `ControlShell` helper

```csharp
// Reusable shell that every control component uses internally. Pure C#; declares the universal seam wiring once.
internal readonly ref struct ControlShell
{
    // Resolves theme+template+flow from context, computes VisualState from hover/focus/pressed + props.
    public static ControlContext Resolve<TProps>(
        VisualState propState,                  // Checked/Disabled/Selected/etc. from props
        out HoverApi hover, out FocusApi focus, out PressedApi pressed)
    {
        var theme = Hooks.UseContext(ControlThemeContext);
        var flow  = Hooks.UseContext(FlowDirectionContext);     // layout.md §10A inherited FlowDirection
        hover     = Hooks.UseHover();                            // input-a11y hover state (PaintDirty-only on change)
        focus     = Hooks.UseFocus();                            // input-a11y FocusEngine
        pressed   = Hooks.UsePressed();                          // arena-resolved press state
        // VisualState is a UseDerived projection: boxless DepKey flip, dependents skip if unchanged (§3.2).
        var state = Hooks.UseDerived(
            () => propState
                | (hover.IsOver   ? VisualState.Hover   : 0)
                | (pressed.IsDown ? VisualState.Pressed : 0)
                | (focus.IsFocused? VisualState.Focused : 0),
            Deps(propState, hover.IsOver, pressed.IsDown, focus.IsFocused));
        return new ControlContext(theme, state, flow, Hooks.UseDpi());
    }
}
```

`UseHover`/`UsePressed` are thin input-a11y hooks: `UseHover` reflects the enter/leave diff (§5.4 input-a11y);
`UsePressed` reflects the **arena-resolved** press (true only once the control's tap recognizer holds provisional
capture, input-a11y §7A.5 — so a press visual doesn't flash for a gesture that loses the arena, e.g. a press that
becomes a scroll-drag). They are declared in `FluentGpu.Hooks` over the Input seams (this doc references them; it does
not own them — if they don't yet exist as named hooks they are trivial `UseState`+`UseGesture` compositions and are
listed in §15 as the only *requested* hook additions, both pure compositions of existing primitives).

---

## 4. The universal control contract (every control obeys)

Each control's section below is specified as a five-tuple. The contract is enforced structurally and gated by
validation.md:

1. **Composition** — the primitive Elements + seams it wires.
2. **UIA pattern + ControlType** — `A11yInfo.{ControlType, Patterns}` (input-a11y §11.4) and the pattern interfaces
   its `NodeProvider` exposes. AT-driven invocation re-enters the same handler delegates as pointer/keyboard (the
   `OnClick = Tapped ∪ Space/Enter ∪ UIA Invoke` rule, input-a11y §6.5).
3. **Keyboard contract** — Tab/Shift-Tab focus order (FocusEngine §8), Space/Enter/arrows/Esc/Home/End/type-ahead.
4. **A11y name/role** — how `get_Name` resolves (explicit `Name` prop → `LabeledBy` → content text → the shared
   auto-name helper, input-a11y §11.4), and `PositionInSet/SizeOfSet/Level` for collections (input-a11y §11.7).
5. **Motion + cursor + RTL** — the phase-7 `AnimTrack`/motion-token used, the `CursorId` it sets (input-a11y §18
   cursor resolver / `Pal.SetCursor`), and its RTL mirroring (layout.md §10A).

The DEBUG `AccessibilityScanner` (validation.md / input-a11y §11.4) asserts (1)/(2)/(4) for every control instance; a
control with an empty resolved `Name` or a missing required pattern is a CI failure, not a runtime degradation.

---

## 5. Primitive interactive controls

### 5.1 Button (exists; formalized)

- **Composition:** `Ui.Border(content)` shell; declares `UseGesture(Tap)` (arena member, input-a11y §7A) +
  `UseFocus()` + the `OnClick` handler. Default template §3.2.
- **UIA:** `ControlType = Button`; `Patterns = Invoke`. `IInvokeProvider.Invoke()` re-enters `OnClick` on the UI
  thread (`UseComThreading`, input-a11y §11.5). A toggle button additionally exposes `Toggle`.
- **Keyboard:** Tab-stop (`IsTabStop`, `TabIndex=0`); **Space** (on key-up) and **Enter** (on key-down) invoke
  (input-a11y §9 built-ins step 5). `OnClick` is the one declaration, three modalities (input-a11y §6.5).
- **Name/role:** `get_Name` = explicit `Name` → content text via the shared helper. Role Button.
- **Motion/cursor/RTL:** hover/pressed implicit transition via `Button.*Motion` token; `CursorId = Arrow` (or `Hand`
  if `IsHyperlink`); padding/content mirror via §10A. Disabled state drops `HitTestVisible` + `IsTabStop` and sets the
  disabled brushes.

### 5.2 Checkbox

- **Composition:** glyph box (a `Ui.Path` checkmark / a dash for indeterminate) + label `Ui.Text`. The check-draw is
  an implicit animation: on `Checked→Rest` the checkmark path animates its stroke via a phase-7 `AnimTrack`
  (motion-token `Checkbox.CheckMotion`); reduced-motion → instant.
- **UIA:** `ControlType = CheckBox`; `Patterns = Toggle`. `IToggleProvider.Toggle()` cycles state and re-enters
  `OnToggle`; `ToggleState` ∈ {Off, On, Indeterminate}.
- **Keyboard:** Tab-stop; **Space** toggles (Off→On→[Indeterminate if `IsThreeState`]→Off). Enter does **not** toggle
  (WinUI parity).
- **Name/role:** label text is the `Name` (or `LabeledBy` the box). Role CheckBox.
- **State:** `VisualState.Checked`/`Indeterminate` are prop-derived; the rest of the shell is §3.4. RTL puts the box
  on the right and label on the left (logical leading-edge box, mirrored at §10A).

### 5.3 RadioButton / RadioGroup

- **Composition:** `RadioGroup<TValue>` is the stateful component owning the selected value; each `RadioButton`
  renders an outer ring + inner dot (the dot scale-springs in on select, `AnimTrack` spring mode, §5.7 backdrop).
  The group provides `Context<RadioGroupState<TValue>>` so each radio reads selection + reports clicks without prop
  drilling.
- **UIA:** radio `ControlType = RadioButton`; `Patterns = SelectionItem`. `ISelectionItemProvider.Select()` selects
  this radio; `get_IsSelected` reflects group state; `get_SelectionContainer` returns the group's `NodeProvider`. The
  group exposes `ControlType = Group` (or `List` with `Selection` pattern, single-select).
- **Keyboard:** the **group is one tab-stop** (roving tabindex): only the selected radio is `IsTabStop=true`; the rest
  are `TabIndex=-1` (programmatic-only, input-a11y §8.1). **Arrow keys** move selection *and* focus among radios
  (XYFocus §8.2 constrained to the group scope), wrapping; **Space** selects the focused radio. RTL flips Left/Right
  arrow direction via the `ResolvedFlowIsRtl` bit (§10A.4).
- **Name/role:** each radio's label is its `Name`; the group's `Name` is its heading/`LabeledBy`. `PositionInSet`/
  `SizeOfSet` set from the radio's index/count in the group (input-a11y §11.7) so Narrator says "2 of 4".
- **Invalid-props:** duplicate `TValue` in a group is a DEBUG assert (no silent dedup).

### 5.4 Switch / ToggleSwitch

- **Composition:** track + thumb; the thumb position is a phase-7 **spring** (`AnimTrack` spring mode, velocity field
  §5.7 backdrop) so a drag-release hands off velocity (no snap). Drag-to-toggle is a gesture-arena `Drag` recognizer
  on the thumb; a tap on the track is a `Tap` recognizer — both are arena members on the same control, teamed
  (input-a11y §7A.3) so the tap and drag don't reject each other before slop decides.
- **UIA:** `ControlType = Button` with `Patterns = Toggle` (WinUI's ToggleSwitch maps to Toggle). On/Off only
  (no indeterminate).
- **Keyboard:** Tab-stop; **Space/Enter** toggle; **Left/Right** arrows set Off/On (mirrored RTL). On-content/off-
  content labels are part of the template.
- **Name/role:** header text is `Name`; the on/off state is announced via `Toggle` + a `UseAnnounce` of the new state.
- **Motion:** the thumb spring + an optional track-color implicit transition. Cursor `Arrow` (thumb shows no special
  cursor; `Hand` is reserved for hyperlinks).

### 5.5 Slider

- **Composition:** track + filled portion + thumb (+ optional tick marks and a value tooltip flyout). Thumb drag is a
  gesture-arena `SelectionDrag`-class `Drag` recognizer; the track is clickable to jump. **Edge-autoscroll is not
  needed** (the slider is bounded), but a slider inside a scroller must **win the arena over the scroller's pan**
  while the thumb is grabbed — the thumb's drag recognizer is innermost so it gets the earliest claim (input-a11y
  §7A.1 innermost-first enrollment) and eager-wins on slop.
- **UIA:** `ControlType = Slider`; `Patterns = RangeValue`. `IRangeValueProvider.{get_Value, SetValue, get_Minimum,
  get_Maximum, get_SmallChange, get_LargeChange}` map to the slider props; `SetValue` re-enters `OnValueChanged`.
- **Keyboard:** Tab-stop; **Left/Right** (and **Down/Up**) = ±`SmallChange`; **PageUp/PageDown** = ±`LargeChange`;
  **Home/End** = Min/Max. RTL flips Left/Right (Up/Down unaffected) via §10A.4.
- **Name/role:** `Name` from header/`LabeledBy`; the **value text is edge-localized** (`ILocaleFormatter.FormatNumber`,
  text.md §19) into a `StringId` so the thumb tooltip and `IRangeValueProvider` agree and locale-format ("3,5" vs
  "3.5"). A `UseAnnounce` reports the value on change (rate-limited).
- **Motion/cursor:** thumb has no implicit position animation while dragging (1:1 to pointer); keyboard steps animate
  via a short `Slider.StepMotion` token. Cursor over the thumb = `Hand`; over the track = `Arrow`.
- **Invalid-props:** `Min > Max` or `Step <= 0` is a DEBUG assert.

### 5.6 ProgressBar / ProgressRing

- **Composition:** determinate ProgressBar = track + filled `FillRoundRect`; indeterminate = a clipped traveling
  segment animated by an `AnimTrack` with a `DrivenClock` (decoupled from frame clock, backdrop §5). ProgressRing = a
  `Ui.Path` arc whose sweep is bound to value (determinate) or whose rotation is a continuous `AnimTrack` loop
  (indeterminate).
- **UIA:** `ControlType = ProgressBar`; `Patterns = RangeValue` (determinate, read-only — `IRangeValueProvider.get_
  IsReadOnly = true`); indeterminate sets `get_Value` to the "indeterminate" UIA convention and exposes no settable
  value. Live-region (`A11yLiveRegion`, input-a11y §11.4) optional so progress milestones announce.
- **Keyboard:** **not** a tab-stop (non-interactive); never focusable.
- **Name/role:** `Name` from `LabeledBy`/context (e.g. "Loading album"). Role ProgressBar.
- **Motion:** the indeterminate animation is `ReducedMotionExempt=false` — reduced-motion replaces the traveling
  animation with a static/pulsing token state (backdrop §5.8). Cursor unaffected; RTL flips the fill origin
  (leading-edge) via §10A.

### 5.7 TextField / TextBox (single- and multi-line) — built on text.md L1

- **Composition:** the control is a thin shell over the **`ITextDocument`** editable seam (text.md §17) — single-line
  (`IsMultiLine=false`, collapses `\n`, scrolls horizontally) and multi-line (`IsMultiLine=true`, hard breaks +
  wrap) are **the same seam, both core**. The shell:
  - allocates one `ITextDocument` (a `SlabAllocator<DocSlot>` entry, text.md §17.1) per editable instance via a
    `UseRef` cell (survives re-render; released on unmount via the §4.4 sync-cleanup contract);
  - declares a **selection-drag gesture team** (input-a11y §7A.3): `Tap` (caret place) + `DoubleTap` (word) +
    `TripleTap` (line) + `Drag` (extend), teamed so they don't fight; selection-drag past the edge arms the **shared
    edge-autoscroll driver** (input-a11y §19 / L11) writing `ScrollOffset`;
  - drives the **`SelectionState` column** (text.md §15) via `ITextDocument.SetSelection` under an edit lock — the
    same column the on-screen highlight (`DrawSelectionRectCmd`, text.md §16 / gpu-renderer §3.1) and
    `ITextRangeProvider` (input-a11y §11.8) read. One read-side, three consumers;
  - sets the IME caret rect via `IImeSession.SetCompositionRect` from `HitTestTextPosition(extent)` (input-a11y §10);
  - renders placeholder text, a clear button (single-line), a header/description, and an error/`InfoBar`-style
    validation message.
- **UIA:** `ControlType = Edit`; `Patterns = Value + Text`. `IValueProvider.{get_Value, SetValue, get_IsReadOnly}`
  over the document; **`ITextProvider`/`ITextRangeProvider`** (input-a11y §11.8) is the full document surface backed
  by the same `ITextReadSide` (text.md §18). `SetValue`/`Select()` go through the document under a lock — AT edits and
  user edits share the transactional buffer (the `ITextStoreACP2`-shaped commit-lock, text.md §17.2).
- **Keyboard:** Tab-stop; full caret nav (arrows / Ctrl+arrows by word via UAX #29 / Home/End / PageUp/Down for
  multi-line); **Shift+nav** extends selection (anchor pinned, extent moves, text.md §15); **Ctrl+A** select-all;
  **Ctrl+C/X/V** via `IClipboard` (input-a11y §12 — `SetText`/`TryGetText`, edge alloc OK); **Ctrl+Z/Y** undo/redo
  via `ITextDocument.Undo/Redo` (text.md §17.4); Enter commits/moves-focus (single-line) or inserts a break
  (multi-line). A composing IME swallows keys (input-a11y §9 step 1). RTL: caret/selection/cursor-arrow mirror via the
  text-layer `FlowDirection` (text.md §8.1) + §10A read-side order.
- **Name/role:** `Name` from header/`LabeledBy`/placeholder fallback; the **value is the document text**, not the
  `Name`. Validation errors set `A11yInfo.{FullDescription, DescribedBy}` (input-a11y §11.7) to the error message and
  flip `VisualState`-equivalent invalid styling; a `UseAnnounce` reports the error. **The full design is now
  `form-validation.md`** (`FluentGpu.Forms`): the field's `Field` prop drives the `BoxEl.Validation` channel (border) +
  `FieldVisuals.MessageRow` (message); the a11y wiring above is that subsystem's activation seam.
- **Motion/cursor:** caret blink is a `DrivenClock` `AnimTrack` (paint-only); **cursor = I-beam** over the text body
  (the I-beam the cursor resolver, input-a11y §18, picks the instant selectable/editable text lands), `Arrow` over
  the chrome. Selection-drag past the edge → edge-autoscroll (L11).
- **macOS:** the same `ITextDocument`/`SelectionState`/`ITextReadSide` feed `NSTextInputClient` + `NSAccessibility`
  text (text.md §15/§18 boundary) — the control recompiles unchanged.

---

## 6. Overlay-hosted controls (over the OverlayManager + arena)

All of these use the **one** overlay manager (input-a11y §1/§4 `OverlayManager`: light-dismiss FSM + z-stack + focus
contain/restore) and the **one** placement math (`OverlayPlacement.Resolve`, layout.md §10B: flip → nudge →
constrain). A control opens an overlay via `UseOverlay` (the hook the overlay manager exposes); it never positions a
popup itself. Opening/closing reveals via a phase-7 `AnimTrack` (flyout fade/scale, motion-token `Overlay.RevealMotion`).

### 6.1 ComboBox / Dropdown

- **Composition:** a `Button`-like header showing the selection + a chevron; on open, a **listbox in an overlay**
  anchored below (flip-to-above near the screen edge via §10B). An **editable** ComboBox embeds a single-line
  `TextField` (§5.7) for filter/type-in. The list is virtualized (`UseVirtual`, §7 / reconciler-hooks §11) for large
  item sets.
- **UIA:** `ControlType = ComboBox`; `Patterns = ExpandCollapse + Selection (+ Value for editable)`. The popup list
  exposes `Selection`/`SelectionItem`; `IExpandCollapseProvider.Expand/Collapse` open/close the overlay (re-entering
  the same open path AT-driven). The overlay's `NodeProvider` is a UIA `List` inside a `Window`/`Menu`-typed overlay
  (input-a11y §11.4 control types).
- **Keyboard:** header Tab-stop; **Space/Enter/Alt+Down** open; in the open list **Up/Down** move highlight,
  **Enter** commits, **Esc** cancels (light-dismiss FSM also closes on outside-press / focus-loss, input-a11y §4/§7A
  step e); **type-ahead** jumps to the first item matching typed prefix (a small ring-buffer match, reset after a
  timeout). Closed ComboBox: Up/Down change selection directly (WinUI parity). Focus is **trapped** in the open
  overlay (FocusEngine `PushScope`, §8.3) and **restored** to the header on close.
- **Name/role:** header `Name` from `LabeledBy`; each item's text is its `Name`; `PositionInSet/SizeOfSet` from the
  virtualizer's logical index/count (input-a11y §11.7) — and `Navigate` past the realized window triggers
  realization-on-navigate (input-a11y §11.9) so an AT can read all 50,000 items.
- **Motion/cursor/RTL:** open reveal motion-token; cursor `Arrow`; RTL anchors the popup to the trailing edge and
  flips the chevron (`AutoMirror`, text.md §19 / layout §10A).

### 6.2 Menu / MenuBar / ContextMenu

- **Composition:** a `MenuFlyout` is an overlay listbox of `MenuItem`s; submenus are **nested overlays** each anchored
  to their parent item (flip side via §10B). A `MenuBar` is a row of top-level menu buttons; a `ContextMenu` opens at
  the pointer (or focus rect for keyboard `Menu`/Shift+F10) via `OverlayPlacement` with an explicit anchor point.
- **UIA:** flyout `ControlType = Menu`; items `ControlType = MenuItem` with `Patterns = Invoke` (leaf) / `ExpandCollapse`
  (submenu) / `Toggle` (checkable) / `SelectionItem` (radio group in menu). `IInvokeProvider.Invoke` re-enters the
  item command (input-a11y §6.5).
- **Keyboard:** **Down/Up** move within a menu (wrapping); **Right** opens a submenu (or moves to next MenuBar menu);
  **Left** closes a submenu (or moves to previous MenuBar menu) — **mirrored RTL** (§10A.4); **Enter/Space** invoke;
  **Esc** closes the current level (light-dismiss FSM); **type-ahead** to items; **accelerators/access keys**
  (Alt+letter) via the `AcceleratorRegistry`/KeyTips overlay (input-a11y §9). Each open menu is its own focus
  sub-scope (`PushScope`); closing restores focus up the chain.
- **Name/role:** item `Name` from its content; accelerator text and toggle/check state announced. Separators are
  `A11yRaw` (decorative, input-a11y §11.3).
- **Motion/cursor/RTL:** submenu reveal motion-token; cursor `Arrow`; submenu-open-delay + close-delay timers run in
  the overlay manager's `OnFrameEnd` (input-a11y §4 tooltip/hover-delay timers).

### 6.3 Dialog / Flyout / Popup

- **Composition:** a **Dialog** is a **modal** overlay (a scrim `DrawScrim` opcode behind it — gpu-renderer §3.6 — +
  a centered content panel) that traps focus (`PushScope`, `NodeFlags.FocusScope`) and sets `IsModal=true`
  (input-a11y §11.4); a **Flyout** is a light-dismiss anchored overlay (non-modal, closes on outside-press/Esc/focus-
  loss); a **Popup** is the raw anchored overlay primitive the others compose.
- **UIA:** Dialog `ControlType = Window` with `Patterns = Window` + `IsModal=true`; Flyout `ControlType = Pane`/`Group`.
  Opening a modal dialog raises the UIA structure-changed + sets `IsTopmost`/`IsModal` so AT announces the modal
  context and confines its reading scope.
- **Keyboard:** focus moves into the dialog on open (`UseFocus(autoFocus)` at 6.5 so it reads valid Bounds, input-a11y
  §8.5); **Tab/Shift+Tab** cycle within the trapped scope (clamped to the top scope, §8.3); **Esc** triggers the
  cancel/close command (dialog only if dismissable; modal flyouts too); **Enter** triggers the default button.
  On close, focus **restores** to the opener.
- **Name/role:** Dialog `Name` from its title; the title is also a `LabeledBy`/`HeadingLevel` so AT announces it.
- **Motion/cursor/RTL:** reveal/dismiss motion-token (scrim fade + panel scale, reduced-motion → instant); cursor
  `Arrow`; content mirrors RTL; buttons in the footer mirror order (§10A).

### 6.4 ToolTip

- **Composition:** a small overlay anchored to its owner, opened on **hover-delay** (timer in the overlay manager's
  `OnFrameEnd`, input-a11y §4) or on keyboard-focus (after a delay), closed on pointer-leave/press/focus-loss.
- **UIA:** `ControlType = ToolTip`; surfaced via the owner's `HelpText`/`get_HelpText` so AT reads it even without
  hover; a transient tooltip raises a UIA `ToolTipOpened` notification.
- **Keyboard:** not focusable; a focused owner shows its tooltip after the focus-delay (keyboard-accessible tooltips).
- **Name/role:** the tooltip text is the owner's `HelpText` (not a separate `Name`).
- **Motion/cursor/RTL:** fast fade-in motion-token; `FlipEnabled=false` is acceptable (tooltips may prefer
  truncation, layout §10B note); cursor unchanged.

---

## 7. Collection & container controls (over virtualization + UIA collection relations)

### 7.1 ListView / GridView (over virtualization)

- **Composition:** a virtualized scroller (`UseVirtual`/`UseInfiniteCollection`, reconciler-hooks §11 / layout §8)
  realizing the window of item containers as **keyed children**; ListView = single-column stack layout, GridView =
  wrap/uniform-grid layout (layout §7). Selection (single/multiple/extended) is a `SelectionModel<TKey>` the list
  owns; item containers read it via context. Item-drag-reorder is a gesture-arena `DragReorder` recognizer that arms
  the **edge-autoscroll driver** past the viewport edge (input-a11y §19 / L11).
- **UIA:** list `ControlType = List`/`DataGrid`; `Patterns = Selection (+ Scroll + Grid for GridView)`;
  item `ControlType = ListItem`/`DataItem` with `Patterns = SelectionItem (+ Invoke if activatable)`. The list is an
  **`IItemContainerProvider`** with the **virtualized-provider realization contract** (input-a11y §11.9):
  `Navigate(NextSibling)`/`FindItemByProperty` past the realized window **causes realization** (posts `ScrollToIndex`,
  pumps one frame, returns the now-materialized neighbor), rate-limited/batched so a `FindAll` scrolls rather than
  instantiating all rows.
- **Keyboard:** the list is **one tab-stop** (roving tabindex over items, input-a11y §8.1); **Up/Down** (and
  **Left/Right** for GridView, mirrored RTL) move focus among items via XYFocus constrained to the list scope;
  **Home/End/PageUp/PageDown** jump (driving `ScrollToIndex` at 6.5); **Space** toggles selection, **Enter** invokes;
  **Ctrl+arrow** moves focus without selecting; **Shift+arrow** extends selection; **Ctrl+A** select-all; **type-ahead**
  jumps to the first item matching the typed prefix.
- **Name/role:** list `Name` from `LabeledBy`; item `Name` from content; **`PositionInSet = logicalIndex+1`,
  `SizeOfSet = totalCount`, `Level`** written by the virtualizer at reconcile (input-a11y §11.7) so Narrator says
  "12 of 50000" — the logical position, correct the instant a row materializes (§11.9). Group headers carry
  `Level`/`HeadingLevel`.
- **Motion/cursor/RTL:** item enter/exit + reorder use `animateItemPlacement` (phase-7 auto-layout-change tween from
  double-buffered prev-frame `WorldBounds[]`, backdrop §5); within-window scroll is transform-only (no relayout);
  cursor `Arrow` (or `Hand` on activatable rows); GridView flow mirrors RTL via §10A.

### 7.2 TreeView

- **Composition:** a virtualized list whose realized items carry **indent + expand/collapse chevron** + a `Level`;
  expand/collapse mutates the flattened visible-node list the virtualizer renders (a tree is projected to a flat
  index space so virtualization §11 is reused unchanged). Selection model as §7.1.
- **UIA:** `ControlType = Tree`; item `ControlType = TreeItem` with `Patterns = ExpandCollapse + SelectionItem`.
  `IExpandCollapseProvider.{Expand, Collapse, get_ExpandCollapseState}` per item.
- **Keyboard:** roving tabindex; **Up/Down** move focus over visible nodes; **Right** expands (or moves to first
  child); **Left** collapses (or moves to parent) — **mirrored RTL** (§10A.4); **Enter/Space** select/invoke;
  **Home/End** to first/last; **type-ahead** over visible nodes; **`*`** expands all siblings (WinUI parity).
- **Name/role:** item `Name` from content; **`Level`** = tree depth (input-a11y §11.7 — "level 3"); `PositionInSet`/
  `SizeOfSet` = position among siblings at that level. Expand/collapse state announced.
- **Motion/cursor/RTL:** expand/collapse uses `animateContentSize` on the subtree region (backdrop §5); chevron
  rotates via a short motion-token; indent direction mirrors RTL; cursor `Arrow`.

### 7.3 Tabs (TabView)

- **Composition:** a header strip of tab buttons (optionally scrollable + an overflow menu over the overlay manager) +
  a content host that shows the selected tab's body. Tab close buttons, add-tab button, and tab drag-reorder
  (gesture-arena `DragReorder` + edge-autoscroll for the strip) are supported. The selected tab's content can be
  wrapped in a `Suspense` boundary (§9.5 reconciler-hooks) so switching to a not-yet-loaded tab keeps the prior tab
  visible during a transition (keep-stale) rather than flashing a skeleton.
- **UIA:** strip `ControlType = Tab`; tab header `ControlType = TabItem` with `Patterns = SelectionItem (+ Invoke)`;
  the content host is the `SelectionItem`'s associated content.
- **Keyboard:** the **strip is one tab-stop** (roving tabindex over headers); **Left/Right** (mirrored RTL) move
  selection+focus among headers, wrapping; **Home/End** to first/last; **Ctrl+Tab/Ctrl+Shift+Tab** cycle tabs;
  **Ctrl+F4/middle-click** close (if closable); **Enter/Space** activate. Focus moves into the content body via Tab
  from the strip.
- **Name/role:** tab header text is its `Name`; `PositionInSet/SizeOfSet` from header index/count (input-a11y §11.7);
  the selected header's `IsSelected`/`SelectionItem` reflects state.
- **Motion/cursor/RTL:** the selection indicator slides between headers via a phase-7 spring (`AnimTrack` spring mode,
  velocity hand-off on rapid switching, backdrop §5.7); content swap uses the Suspense keep-stale + cross-fade; strip
  mirrors RTL; cursor `Arrow` (or `Hand` on headers).

---

## 8. Disclosure & status controls

### 8.1 Expander

- **Composition:** a header (toggle button with a chevron) + a collapsible content region. Expand/collapse animates
  the content height via **`animateContentSize`** (phase-7, seeded from double-buffered prev-frame `WorldBounds[]`,
  backdrop §5) — a real auto-layout-change tween, not a v2 stub.
- **UIA:** header `ControlType = Button`; the expander exposes `Patterns = ExpandCollapse`.
  `IExpandCollapseProvider.{Expand, Collapse, get_ExpandCollapseState}`.
- **Keyboard:** header Tab-stop; **Space/Enter** toggle; **arrows** optional (WinUI Expander toggles on Space/Enter).
- **Name/role:** header text is `Name`; expanded/collapsed state announced. The content region is `A11yPresent` only
  when expanded (collapsed content is `Visible=false` → skipped by the topology walk, input-a11y §11.3).
- **Motion/cursor/RTL:** chevron rotation motion-token + `animateContentSize`; reduced-motion → instant height swap;
  chevron/header mirror RTL; cursor `Arrow`.

### 8.2 InfoBar

- **Composition:** an icon + title + message + optional action button + optional close button, in a colored severity
  surface (Informational/Success/Warning/Error — brushes from theme tokens). Appearance/dismissal animates via a
  short reveal motion-token; multiple InfoBars can stack.
- **UIA:** `ControlType = Group` (or `StatusBar`) and — critically — an **`A11yLiveRegion`** (`NodeFlags 1<<26`,
  input-a11y §11.4): when an InfoBar appears or its message changes, the reconciler's live-region capture raises
  `RaiseLiveRegionChanged`, and `UseAnnounce` fires a UIA Notification (phase 12) so AT announces it without the user
  navigating to it. Severity maps to the live-region politeness (`assertive` for Error, `polite` otherwise).
- **Keyboard:** not a tab-stop itself; the action/close buttons are tab-stops in document order; **Esc** dismisses if
  closable and focus is within.
- **Name/role:** `Name` = title; `FullDescription` = message (input-a11y §11.7) so AT reads both; the severity is
  part of the announced text.
- **Motion/cursor/RTL:** enter/exit reveal motion-token (reduced-motion → instant); icon `AutoMirror` per RTL where
  directional; cursor `Arrow`.

### 8.3 Scrollbar

- **Composition:** track + thumb + (optional) line/page buttons; the thumb drag is a gesture-arena `Drag` recognizer;
  track click pages. The scrollbar **does not own scroll offset** — it reads `ScrollOffset`/`ContentSize` (layout §8
  scroll-ownership split: Input owns offset, Layout writes ContentSize) and writes offset through the **same scroll-
  offset writer** the inertia integrator, edge-autoscroll, and `ScrollToIndex` use (input-a11y §19 — one writer). It
  auto-hides/expands per the conscious-scroll Fluent pattern (a `DrivenClock` fade `AnimTrack`).
- **UIA:** `ControlType = ScrollBar`; `Patterns = RangeValue`. The **scrollable container** (not the scrollbar) is the
  `IScrollProvider` (`Scroll` pattern: `Scroll`, `SetScrollPercent`, `get_HorizontalScrollPercent`, `get_ViewSize`) —
  the scrollbar is the visual; the container is the pattern provider, so AT scrolls via the container.
- **Keyboard:** the scrollbar itself is typically not a tab-stop; scrolling is keyboard-driven via the focused content
  (arrows/PageUp/Down/Home/End route to the container's scroll, or to focus movement that triggers scroll-into-view at
  6.5). A focusable scrollbar (rare) supports arrows = line, PageUp/Down = page, Home/End = extents.
- **Name/role:** orientation in the `Name` ("Vertical scroll bar"); thumb position via `RangeValue`.
- **Motion/cursor/RTL:** auto-hide/expand motion-token; horizontal scrollbar mirrors origin RTL (§10A); cursor `Arrow`
  over track/thumb. Thumb fling uses the inertia integrator (input-a11y §7B), transform-only.

---

## 9. Cross-cutting wiring summary (the seam matrix)

Every control's seam usage at a glance — proving each seam has a control consumer and no control forks a seam:

| Seam (owner) | Controls that consume it |
|---|---|
| **Gesture arena + team** (input-a11y §7A) | every interactive control (Tap); Slider/Switch/Scrollbar/ListView-reorder (Drag); TextBox (selection-drag team) |
| **Overlay manager + placement-flip** (input-a11y §4, layout §10B) | ComboBox, Menu/MenuBar/ContextMenu, Dialog/Flyout/Popup, ToolTip, Tabs-overflow, Slider value-tooltip |
| **Light-dismiss FSM** (input-a11y §7A.e) | ComboBox, Flyout, Menu, ContextMenu, non-modal Popup, ToolTip |
| **Focus trap + restore** (`PushScope`, input-a11y §8.3) | Dialog (modal), ComboBox/Menu open scopes |
| **Roving tabindex + XYFocus** (input-a11y §8.1/§8.2) | RadioGroup, ListView/GridView, TreeView, Tabs, Menu |
| **Selection/editable + SelectionState + ITextDocument** (text.md §15/§17) | TextField/TextBox, editable ComboBox |
| **ITextProvider/ITextRangeProvider** (input-a11y §11.8, text.md §18) | TextField/TextBox |
| **UIA patterns** (input-a11y §11.4) | Invoke(Button/MenuItem), Toggle(Checkbox/Switch/menu-check), Selection/SelectionItem(Radio/List/Tree/Tabs/Combo), RangeValue(Slider/ProgressBar/Scrollbar), ExpandCollapse(Combo/Tree/Expander/submenu), Value+Text(TextBox), Scroll(scroller), Window(Dialog), Grid(GridView) |
| **Collection relations + realization** (input-a11y §11.7/§11.9) | ListView/GridView, TreeView, ComboBox list, RadioGroup, Tabs |
| **Cursor resolver + Pal.SetCursor** (input-a11y §18) | TextBox(I-beam), Slider/Switch/Scrollbar thumbs(Hand where applicable), default Arrow |
| **Edge-autoscroll driver** (input-a11y §19 / L11) | TextBox selection-drag, ListView/Tabs drag-reorder |
| **RTL logical→physical at WriteLayout** (layout §10A) | all controls (paddings/flow/arrow-keys/icon mirror) |
| **Suspense keep-stale + lanes** (reconciler-hooks §9.5/§7.1) | Tabs content swap, async-data ListView, Dialog content load |
| **UseOptimistic + async-action** (reconciler-hooks §9.6) | Switch/Checkbox/like-button optimistic toggle, ListView optimistic reorder |
| **Springs / animateContentSize / item-placement** (backdrop §5/§5.7) | Switch thumb, Tabs indicator, Expander, TreeView expand, ListView reorder |
| **ILocaleFormatter (edge → StringId)** (text.md §19) | Slider value, ProgressBar percent, any value-formatting control |
| **Theme tokens (Tok.*) + UseTheme epoch** (theming §2) | every control (via ControlTheme/PartToken) |

---

## 10. Zero-alloc & thread-confinement story

- **Phase-4 render alloc = the edge only.** A control's `Render()` builds `Element` records (Gen0, bounded — the
  same per-rendered-component churn the 3-signal skip bounds, gap analysis §3) and captures user handler closures at
  the edge (the one GC edge, stored in `HandlerTable`, input-a11y §6.2). Steady-state (idle control, clean memo) the
  control costs one flag-load. Phases 6–13 stay 0-alloc — a control adds no per-frame paint work beyond the opcodes
  the seams already emit.
- **VisualState is boxless.** The `UseDerived` projection (§3.4) compares a `VisualState` byte via `DepKey` — a hover
  flip is an 8-byte compare + a `PaintDirty` re-record, no box, no relayout.
- **Token tables are build-once frozen.** `ControlTheme` is a `FrozenDictionary<PartTokenId, PartToken>` built per
  `(ThemeKind, accent, hc)` and cached exactly like theming.md's brush cache; a theme swap is one epoch bump + a
  boxless per-consumer context change-check (reconciler-hooks §8) re-rendering only controls that read a changed
  token.
- **No control owns a ComPtr / native handle / GPU object.** Controls write Elements (UI thread); the reconciler
  writes columns; the render thread owns every ComPtr (hardened-v1 §2.1). Editable text's `ITextDocument` is UI-thread-
  confined (text.md §17.2); UIA providers are marshaled to the UI thread (`UseComThreading`, input-a11y §11.5). A
  control never crosses PUBLISH(13a).
- **`ThreadGuard.AssertWriter`** (validation.md, `[Conditional]`-erased in shipping AOT) guards the control's column
  writes via the reconciler it already runs in — controls inherit the single-writer proof, they do not add a new
  writer.

---

## 11. Failure / edge cases

- **Disabled control:** drops `HitTestVisible` + `IsTabStop`, sets disabled brushes, and `A11yInfo` exposes
  `IsEnabled=false` so AT announces it; arena recognizers are not enrolled (no member contributed) so a disabled
  control never wins a gesture.
- **Control unmounted mid-interaction** (e.g. dropdown closed while an item is mid-press): the gesture arena force-
  closes on `PointerCaptureLost`/topology change (input-a11y §7A.5 / §15), the overlay's focus scope pops and restores
  focus, and any open `ITextDocument` lock is released by the §4.4 sync-cleanup contract; no half-armed recognizer.
- **Editable control freed:** the `ITextDocument` `DocSlot` is released on unmount (sync-cleanup), the `SelectionState`
  column cleared, and the `ElementRef` to it returns null on a stale gen (input-a11y §8.5) — never a UAF.
- **Virtualized collection AT walk** (read-all 50,000 items): realization-on-navigate is rate-limited/batched
  (input-a11y §11.9) and the virtualizer recycles scrolled-out rows, so the realized window stays ~50 live even under
  an AT walk; `PositionInSet` reports the logical position throughout.
- **Restyled control that omits a required part** (e.g. an app template for ComboBox that drops the listbox): the
  behavior shell (§3.4) still wires the seams, so accessibility/keyboard survive; the DEBUG `AccessibilityScanner`
  flags a missing required part / empty `Name`. Behavior is never lost by restyling because behavior lives in the
  shell, not the template.
- **Reduced-motion:** every control's implicit transitions/springs/`animateContentSize` are reduced-motion-aware via
  the backdrop §5.8 projection — they collapse to instant token swaps; no control hard-codes a duration.
- **RTL flip mid-session** (user changes locale): a `FlowDirectionContext` change marks descendants
  `LayoutSelfDirty + ParentMeasureDirty` (layout §10A.5) and re-renders affected controls with mirrored paddings/
  arrow-key directions/icon mirror; the golden-parity gate (§10A.5) asserts the RTL resolution.
- **Invalid props** (Slider Min>Max, RadioGroup dup values, ProgressBar value out of range): DEBUG assert + the
  unhandled sink in dev; in release the control clamps to a defined behavior (documented per control) but the assert
  is the contract — controls do not silently mask user bugs (Reactor philosophy, input-a11y posture).

---

## 12. Cross-platform (macOS) boundary

Nothing in `FluentGpu.Controls` recompiles for macOS — it is 100% portable composition over the portable seams. The
seams swap their OS leaves (the controls do not see the swap):

| Seam consumed by controls | Windows leaf | macOS leaf | What stays portable (the control sees only this) |
|---|---|---|---|
| UIA patterns / providers | `FluentGpu.Windows` Uia/ | `Accessibility.NSAccessibility` | `A11yInfo` columns (ControlType/Patterns/Name/PositionInSet/…), topology walk |
| Editable text / IME | `ITextDocument` + Imm32/TSF | `ITextDocument` + `NSTextInputClient` | `ITextDocument`/`SelectionState`/`ITextReadSide` (text.md §15/§18) |
| Clipboard / drag (TextBox, ListView) | OLE | `NSPasteboard`/`NSDragging*` | `IClipboard`/`IDragDropBackend` (input-a11y §12) |
| Cursor | `Pal.SetCursor` (Win32) | `Pal.SetCursor` (NSCursor) | `CursorId` enum + the resolver route (input-a11y §18) |
| System/accent/HC color tokens | Windows HC tokens | macOS appearance | `ControlTheme`/`PartToken.Brush` via theming.md |
| Overlay window/z | `FluentGpu.Windows` Pal/ DComp visuals | `Pal.Cocoa` layers | `OverlayManager`/`OverlayPlacement` (input-a11y §4, layout §10B) |

Forbidden above the seam (architecture-spec §9): no `HWND`/`NSWindow`/`HRESULT`/`ComPtr`/`IRawElementProvider*`/
`NSAccessibility*`/WinRT type appears in a control. A control sees only `Element`, hooks, `Vec2`/`RectDip`/`Size2`,
generational handles, and the portable seam interfaces.

---

## 13. What this subsystem OWNS (authority list)

- **Assembly:** `FluentGpu.Controls` (+ the `Ui.*` control re-export partial on `FluentGpu.Dsl`).
- **Styling system types:** `ControlTemplate<TProps>`, `ControlContext`, `VisualState`, `PartToken`, `PartTokenId`,
  `ControlTheme`, `TemplateRegistry`, `ControlShell`, `ControlThemeContext`/`TemplateContext` (control-scoped
  `Context<T>` instances — the `Context<T>` machinery is owned by reconciler-hooks §8; this doc owns these specific
  instances), and the **control PART-token id catalog** (`Part.Button_Bg_Rest`, `Part.Slider_Track_Fill`, … — the
  StringId-interned ids; theming.md owns the token *table machinery*, this doc owns the *control part ids*).
- **Control components (the kit):** `Button`, `Checkbox`, `RadioButton`/`RadioGroup`, `Switch`, `Slider`,
  `ProgressBar`/`ProgressRing`, `TextField`/`TextBox`, `ComboBox`, `ListView`/`GridView`, `TreeView`, `Tabs`,
  `Menu`/`MenuBar`/`ContextMenu`/`MenuItem`, `Dialog`/`Flyout`/`Popup`, `ToolTip`, `Scrollbar`, `Expander`, `InfoBar`,
  plus their props structs and default templates.
- **The per-control five-tuple contract** (composition / UIA pattern+ControlType / keyboard / name-role / motion-
  cursor-RTL) and the universal control contract (§4) that validation.md gates.

**As-shipped (Phase 0) ownership note.** The shipped assembly (§1.1) owns: the shipped control classes (`Button`/
`IconButton`/`ToggleButton`/`Slider`/`ScrollBar`/`NavigationView`+`NavItem`+`PaneMode`, `Navigator`/`Route`/
`PageHost`/`Nav`, `Repeater`+`RepeatLayout`+`RepeatKind`, the `Virtual` factory, `Icons`), each control's nested
`Style` record + `StyleOverride` + `DefaultStyle`, and the `Style`-override layering. The work also **drove** (but
does NOT own) the new opcode **`DrawGradientStroke`** (owned by scene-memory.md §4.1 / gpu-renderer.md §3.1a) +
the `_borderBrushes` side-table (scene-memory.md) + the `BoxEl.BorderBrush` DSL field, the `AutomationRole` enum
(Foundation) + `InteractionInfo.Role` column (input-a11y.md), `ColorF.LerpLinear` (Foundation/Geometry), and the
`BoxEl.HoverScale`/`PressScale` composited-scale recorder behaviour — registered in their owning docs, referenced
here. `VirtualListEl` stays in `Reconciler`.

**Explicitly NOT owned here (referenced):** SceneStore columns + opcode registration incl. `DrawGradientStroke` +
`_borderBrushes` + `InteractionInfo.Role` (scene-memory.md / input-a11y.md); DrawList
opcode shapes `DrawGradientStrokeCmd`/`DrawFocusRingCmd`/`DrawSelectionRectCmd`/`DrawScrimCmd`/`FillRoundRectCmd` (gpu-renderer.md §3.1/§3.1a/§3.6);
gesture arena / overlay manager / cursor resolver / edge-autoscroll / UIA providers / `ITextRangeProvider` CCW
(input-a11y.md); `SelectionState`/`ITextDocument`/`ITextReadSide`/`ILocaleFormatter` (text.md); RTL resolution +
`OverlayPlacement` geometry + virtualization layout (layout.md); `UseVirtual`/`Suspense`/`UseTransition`/
`UseOptimistic`/`UseDerived`/effect timing/the 3-signal skip (reconciler-hooks.md); springs/`animateContentSize`/
motion tokens (backdrop-effects-animation.md); `Tok.*` token table machinery + theme caches (theming.md); the
source generators + build config (dsl-aot.md); the `AccessibilityScanner` + control test harness (validation.md).
This doc **adds no new opcode, column, PAL seam, RHI method, or engine hook** — it is the disciplined composition of
the primitives those docs already mint.

---

## Implemented from the gap analysis

| Gap (id) | What was deferred | Folded into core here as |
|---|---|---|
| **L8** (Accessible-by-default control kit) | "Primitives only; no Checkbox/Radio/Switch/Slider/ComboBox/TextField/Tabs/Menu/Dialog/Scrollbar; no control-template/styling system. Fold-soon, *after* L1/L2/L3/L4/L6 stabilize." | This entire doc: the **`FluentGpu.Controls`** assembly (§1), the **control-template/styling system** (`ControlTemplate`/`ControlTheme`/`VisualState`/`ControlShell`, the lookless behavior/appearance split, §3), and **every control** (§5–§8) fully wired to the now-core seams (§9 matrix): gesture arena (L2), overlay manager (L4), UIA patterns + collection relations + realization (L3/L6), selection/editable (L1), focus/keyboard (Tab/XY/Space/Enter/arrows/type-ahead), cursor (L10), edge-autoscroll (L11), RTL (L5), Suspense/lanes/optimistic (P1/P2b/P7), springs/content-size (L7), localization (L9). The "defer-after-seams" framing is removed; the kit is core now because the seams it sequenced behind are core. |

This doc consumes (does not re-implement) the core foldings the seam docs landed: **L1/L3** (text.md §15–18,
input-a11y §11.8), **L2** (input-a11y §7A), **L4** (input-a11y §4 + layout §10B), **L5** (layout §10A), **L6**
(input-a11y §11.7/§11.9), **L7** (backdrop §5/§5.7), **L9** (text.md §19), **L10/L11** (input-a11y §18/§19),
**P1/P2a/P2b/P6/P7** (reconciler-hooks §7.1/§8.5/§9.5/§9.6) — each control is the integration test that those seams
demanded, so no control bakes in a display-only / no-arena / direction-blind assumption.

---

## Contradictions

None. This doc introduces no new cross-cutting contract and overrides nothing in SPEC-INDEX.md §2. It is a pure
consumer of the seams the other subsystem docs own; where it references a type (`SelectionState`, `OverlayPlacement`,
`UseVirtual`, `DrawSelectionRectCmd`, `AnimTrack` spring mode, `PartToken`→`BrushHandle`, the UIA pattern set), it
uses the owning doc's canonical shape unchanged. The only *requested* additions are two pure-composition hooks
(`UseHover`, `UsePressed`) over existing Input primitives (§3.4 / §15-equivalent note) — listed as compositions, not
new engine primitives, and homed in `FluentGpu.Hooks` to be ratified by reconciler-hooks/input-a11y if they are not
already present as the trivial `UseGesture`+`UseState` shapes this doc assumes.
