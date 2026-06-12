# Building WinUI-faithful controls — visual state, motion & the rendering rules

This is the reference for getting a FluentGpu control to **look and move 1:1 with WinUI 3** — for developers and
for agents doing the parity sweep. It captures hard-won rules (and the bugs that taught them). Read it before
touching `src/FluentGpu.Controls/*.cs` or the rounded-rect rendering.

The golden rule that everything else serves: **read the ENTIRE WinUI control template first.** WinUI control visuals
live in `C:\WAVEE\microsoft-ui-xaml\controls\dev\CommonStyles\<Control>_themeresources.xaml` (framework controls) or
`controls\dev\<Control>\` (muxcontrols). Read every `VisualState`, every `Storyboard`, every brush resource, and the
AnimatedIcon sources - *then* map it. Skimming a summary is how states (pressed, the glyph press animation, the
disabled stroke) get missed.

---

## 0. Finding the exact WinUI animation

Use the local WinUI checkout as the source of truth:

- Control templates: `C:\WAVEE\microsoft-ui-xaml\controls\dev\CommonStyles\<Control>_themeresources.xaml` and, when it
  exists, `<Control>_themeresources_perf2026.xaml`. For muxcontrols, start under
  `C:\WAVEE\microsoft-ui-xaml\controls\dev\<Control>\`.
- Shared timing/easing tokens: `controls\dev\CommonStyles\Common_themeresources_any.xaml`. Important keys:
  `ControlNormalAnimationDuration = 250ms`, `ControlFastAnimationDuration = 167ms`,
  `ControlFasterAnimationDuration = 83ms`, `ControlFastOutSlowInKeySpline = 0,0,0,1`.
- AnimatedIcon plumbing: search for `AnimatedIcon.State`, the specific visual source name
  (for example `AnimatedAcceptVisualSource`), and the transition segment name (`NormalOffToNormalOn`,
  `PointerOverToPressed`, etc.). The fallback glyph is not the animation.

Search workflow:

```powershell
rg -n 'VisualState|Storyboard|DoubleAnimation|KeyFrame|AnimatedIcon.State|PointerOver|Pressed|Selected|Checked' `
  C:\WAVEE\microsoft-ui-xaml\controls\dev\CommonStyles\<Control>_themeresources*.xaml

rg -n 'ControlNormalAnimationDuration|ControlFastAnimationDuration|ControlFasterAnimationDuration|KeySpline' `
  C:\WAVEE\microsoft-ui-xaml\controls\dev\CommonStyles\Common_themeresources_any.xaml
```

Pitfalls that caused real misses:

- Do not search only for `Animation`. Some state changes are `Setter`s, and some are zero-duration animations.
- Do not search only for `Pressed`. WinUI often names cross-product states like `CheckedPressed`,
  `SelectedPointerOver`, `UncheckedPressed`, or puts `Pressed` inside a nested style.
- Do not ignore invisible parts. RadioButton's press grow is a separate `PressedCheckGlyph` with `Opacity=0` at rest,
  then opacity/size animate during `Pressed`.
- Resolve every `{StaticResource ...}` and `{ThemeResource ...}`. Durations/easings usually live in
  `Common_themeresources_any.xaml`; colors/sizes often bounce through per-control theme keys.
- Prefer transform scale in FluentGpu for WinUI width/height keyframes on glyphs/thumbs when the visual size changes
  but layout should not move.
- If there is a `_perf2026` template, compare it with the non-perf file and document which one the control follows.

---

## 1. Rendering rules (the rounded-rect pipeline)

A `BoxEl` rasterizes through one SDF rounded-rect pipeline (`RoundRectPipeline` solid, `GradientPipeline` gradient).
Three rules, each the fix for a real bug — do not regress them:

- **Borders are a hollow SDF ring, never a filled "donut."** `SceneRecorder` always `FillRoundRect`s the *full* interior
  with the fill, then draws ONE ring (`EmitBorderRing` solid / `EmitGradientBorderRing` gradient). The old "fill the
  whole box with the border colour then overlay an inset interior" donut **bled the border through any translucent
  fill** (the unchecked-CheckBox grey-chip). If you see a border-coloured fill, the donut is back.
- **The stroke ring's corner radius shrinks by `bw/2`** (`InsetCorners`) so the ring is *concentric* with the box's
  rounded corner. Skip this and 1px corners read rough/uneven.
- **The SDF quad is inflated by `stroke/2 + AA margin`** in the vertex shader. The bare rect quad clips the outer half
  of the stroke band + its antialiasing feather — invisible on straight edges, but it slices the square quad corner
  through a rounded band → rough corners / rough pill-ends. (`RoundRectPipeline`/`GradientPipeline` `VSMain`.)

Colors are premultiplied + linear-blended (BGRA8). 1px strokes are crisp at any DPI (derivative AA). Strokes sit
INSIDE the bounds (WinUI alignment).

---

## 2. Visual state - ramps over orthogonal axes

WinUI enumerates the **cross-product** of state axes - e.g. CheckBox = `{Unchecked,Checked,Indeterminate} x
{Normal,PointerOver,Pressed,Disabled}` = 12 `VisualState`s, each restating several setters. **Do not replicate that.**
That combinatorial restatement is exactly why states get missed and drift.

Instead, model each visual property as a function of the **orthogonal** axes - logical state (checked/selected/...) x
interaction (hover/press/focus/disabled, which the engine tracks per node) - using a **`StateBrush` ramp**
(`src/FluentGpu.Controls/ControlMotion.cs`):

```csharp
public readonly record struct StateBrush(ColorF Rest, ColorF Hover, ColorF Pressed, ColorF Disabled);
```

- Declare **one ramp per logical state** (CheckBox = 2: an off ramp + an accent ramp), picked by the logical state.
- Wire `Rest/Hover/Pressed` into the element's `Fill/HoverFill/PressedFill`,
  `BorderColor/HoverBorderColor/PressedBorderColor`, or `Opacity/HoverOpacity/PressedOpacity`.
- Put the timing on the same element with `HoverDurationMs`, `PressDurationMs`, `HoverEasing`, and `PressEasing`.
  Use WinUI's shared tokens when the template does: 83ms (`ControlFaster`), 167ms (`ControlFast`), 250ms
  (`ControlNormal`), usually with `ControlFastOutSlowInKeySpline = 0,0,0,1`.
- The engine's `InteractionAnimator` eases the displayed value toward the current target and retargets interruptibly.
  Disabled is a flat swap (`Resting(enabled)`) unless WinUI explicitly animates disabled.

This is the SwiftUI/Compose/Flutter "animate the property toward its target" model. Combinations fall out of boolean
logic - no `GoToState`, no storyboards, no enumerated states. The WinUI *values* are already structured as a ramp
(`AccentDefault/Secondary/Tertiary/Disabled`); you're just naming the ladder instead of pasting it 12x.

**Engine support (use it, it's generic):** every `BoxEl` has hover/pressed targets for fill, border, opacity, and
scale, plus hover/press duration/easing specs. Child template parts inherit a clickable ancestor's interaction progress
unless they declare their own interaction row. That is how a RadioButton root can drive both the checked glyph and the
pressed-only glyph while each part keeps its own target values.

**Known gap:** `TextEl` color is not state-eased (we use the resting glyph/label color). A per-state *text* color
change (e.g. the checked-pressed glyph dim) is a future `TextEl` extension; press feedback today is the box ramp + the
glyph press-scale (below).

---

## 2a. State graph - logical state x interaction state

Treat WinUI names like `SelectedPointerOver`, `CheckedPressed`, and `UncheckedDisabled` as labels for points in this
graph, not as separate code paths:

```text
Logical axis (app/data state)

  Unselected/Unchecked -------- click/toggle --------> Selected/Checked
             |                                           |
             | optional tri-state                         | optional tri-state
             v                                           v
        Indeterminate ------------------------------> Selected/Checked


Interaction axis (input state, overlaid on the current logical state)

              pointer enter                  pointer down
  Normal ----------------------> PointerOver ----------------------> Pressed
    ^                              ^   |                              |
    |                              |   | pointer leave                 | pointer up inside
    | pointer leave / cancel       |   v                              v
    +------------------------------+ Normal <------------------- PointerOver

  Any enabled state -- IsEnabled=false --> Disabled
  Disabled -- IsEnabled=true --> Normal, PointerOver, or Pressed from current input
```

Target selection is a simple priority rule:

```text
if !enabled:        use logical.Disabled
else if pressed:   use logical.Pressed
else if hovered:   use logical.Hover
else:              use logical.Rest
```

Examples:

- `SelectedPressed` = selected logical ramp + pressed interaction target.
- `UncheckedPointerOver` = unchecked logical ramp + hover interaction target.
- `CheckedNormal -> CheckedPressed` is one interaction-axis retarget, usually handled by `InteractionAnimator`.
- `UncheckedPressed -> CheckedPointerOver` can happen on mouse release: the click toggles the logical axis while the
  interaction axis leaves pressed and stays hover. Declare both axes and let the engine retarget.

Focus is another orthogonal axis. Add it explicitly when the engine has focus visuals; do not fold it into hover or
pressed values.

---

## 3. Motion - two engines, one ownership rule

There are two animation systems on purpose, not two competing engines:

- **`InteractionAnimator` owns hover/press visual states.** Use `BoxEl.{Hover,Pressed}{Fill,BorderColor,Opacity}`,
  `HoverScale`, `PressScale`, and the per-element duration/easing fields. This covers WinUI state-group storyboards
  that react to `PointerOver`/`Pressed`, including child parts that inherit the root interaction progress.
- **`AnimEngine` owns authored timelines.** Use keyframes/channels for explicit non-input timelines: FLIP, reveal,
  stroke trim, path draw-on, open/close transitions, or any WinUI AnimatedIcon segment that has real keyframes.
- **Enter/exit presets are lifecycle sugar over `AnimEngine`.** `ControlMotion.IconPop` and `DotScaleIn` apply when
  the reconciler inserts/removes a keyed child. Use them only when WinUI actually inserts/appears a part. RadioButton's
  checked glyph is not a dot-pop; it is visible immediately and changes size through hover/press states.
- **Press-only parts stay in the tree with explicit targets.** RadioButton's `PressedCheckGlyph` is the model:
  separate key from `CheckGlyph`, rest size 4/opacity 0, pressed size 10/opacity 1, `PressDurationMs = 167`, and
  `PressEasing = ControlFastOutSlowIn`.
- **Layout-size changes that must reflow neighbours use `SizeMode.Reflow`** (a `LayoutTransition` on the resizing
  node): the engine eases the LAYOUT height/width through real boundary-scoped relayout each tick, restores the
  declared input at settle, and `SizeAnchor.Trailing` rides the content's end edge on the reveal edge. FLIP
  projections are PARENT-RELATIVE, so everything below moves rigidly while the space animates. This is FluentGpu's
  one **deliberate divergence from WinUI**: where WinUI snaps layout and only translates content (Expander
  ExpandDown/CollapseUp), FluentGpu eases the space itself — keeping the WinUI timings/easings (expand 333ms
  KeySpline 0,0,0,1; collapse 167ms KeySpline 1,1,0,1 via `ExitDynamics`). The Expander is the reference consumer.

This kit is generic: RadioButton, ToggleButton, ComboBox, ToggleSwitch, TabView, NavigationView etc. all declare ramps
and pick interaction targets or explicit timelines. Per-control work = *declare the ramps, template parts, keys, and
timing specs* - which is what reading the WinUI template gives you.

---

## 4. Verify empirically — logs & pixels, not vibes

Three layers; use all three. **Evidence before claiming an animation works.**

- **Headless golden checks** (`src/FluentGpu.VerticalSlice/Program.cs`, `dotnet run` -> "ALL CHECKS PASSED"). This is
  the deterministic, log-based truth. Patterns: reconcile a control, `new FlexLayout(...).Run`, find a node with
  `Child(scene, node, i)`, `engine.Tick(16f)`, assert `scene.Paint(node).Opacity` / `.LocalTransform.M11`. For the
  **live** path use a real `AppHost` + `ClickNode` and read the node after the click frame (see 23h/23h2 for
  RadioButton checked/pressed glyph sizes, 23i for interaction ramp wiring, and 66b for CheckBox draw-on). A control
  animation is not done until a headless check proves the node's opacity/scale/trim goes mid-flight -> settled.
- **GPU pixels** - `dotnet run --project src/FluentGpu.WindowsApp -- --screenshot out\x.png --shot <id>` renders a
  deterministic `ShotScene`. `--frames N` picks the capture frame. **To prove an animation actually runs on the GPU,
  temporarily slow its duration/spring response and capture an early frame** - a slow draw/grow forces a visibly
  mid-animation glyph; if it is already settled, the animation is not reaching the GPU. (Static screenshots can look
  settled because window init can paint/tick several times before capture.) Add per-control shot ids in `ShotScene.cs`.
- **Diagnostics** - `FG_DIAG=1` enables `Diag` (anim seed/retarget events, scene dumps with `FG_DUMP=1`).
  `FrameStats` from `RunFrame()`: `Rendered`, `ComponentsRendered`, `HotPhaseAllocBytes` (0 steady).

Timing note: a sub-300ms WinUI token can still be visually real; screenshot startup can consume enough frames to make
it look instant. Use a deterministic frame clock or temporary slow-motion proof before changing source timings.

---

## 5. The control-parity checklist (the process — follow it, don't shortcut)

1. **Read the WHOLE WinUI template** (`<Control>_themeresources*.xaml`): every `VisualState` in every group, every
   `Storyboard`/`Setter`, every brush key, sizes/padding/corner, the AnimatedIcon states. Resolve duration/easing
   resources in `Common_themeresources_any.xaml` and brush keys to `Tok` tokens (Dark + Light).
2. **Map the state matrix to ramps**: one `StateBrush` per logical state for fill / stroke / glyph-fg / label-fg
   (rest/hover/pressed/disabled). Note where press *dims* (stroke, glyph) — easy to miss.
3. **Sizes/geometry** from the template (box size, glyph size, corner radius, padding, min-height) -> exact values.
4. **Sub-parts & glyphs**: the right Segoe Fluent glyph(s), indicator parts, sub-elements that appear on a state.
5. **Motion**: map hover/press storyboards to `InteractionAnimator` targets/specs; map authored timelines
   (stroke trim, reveal, FLIP, open/close) to `AnimEngine` keyframes/channels; use enter/exit only for true
   insert/remove sub-parts.
6. **Verify**: a headless `Check()` for behavior + a key motion assertion; a `--shot` capture diffed against WinUI
   (incl. the slow-motion proof for any animation). Save a golden PNG.

If you skip step 1 you WILL miss states — that is the recurring failure this guide exists to prevent.

---

## 6. Template parts — one generic door, no styling knobs

Customization of a control's internals goes through `TemplateParts` (`src/FluentGpu.Dsl/TemplateParts.cs`; usage in
`docs/guide/components-elements-layout.md`), never through per-control feature props. Rules for every new control:

- **Export `public const string PartXxx` consts** for each named template part — WinUI template-part `x:Name`s where
  an equivalent exists (Expander: `Root`/`Header`/`Chevron`/`Clip`/`Content`) — plus `public TemplateParts? Parts;`.
- **Route every named part** through `Parts.Apply(PartXxx, el)` after building it, then **re-assert the part's
  mechanics-critical props** with one trailing `with` (click/toggle handlers, reflow `Animate` specs, `*Bind`
  closures, `Key`, `Role`) and **chain** ref-capture handlers (`OnRealized`/`OnPinned`) via `TemplateParts.Chain` —
  a modifier can restyle everything but break nothing. Document each part's OWNED props on its const.
- **New styling knobs are banned.** The review question for any proposed public field: *could the caller write
  `Parts[PartX] = el => el with { … }` instead?* Then they must. Keep: content **slots** (Element-typed), state
  signals/callbacks, options/config, and theme token-bundle `Style` records — those are not part-styling.
- Part-name strings may match existing reconciler `Key`s (e.g. "sb-thumb") for one vocabulary, but `Key` remains a
  separate identity concept — repeated parts (tab items, tick marks) must never derive their `Key` from a part const.
- Virtualized item chrome stays on the `ContainerFactory`/`SelectorVisual` seam for the SKIN, and per-item VARIATION
  rides the `PartDelta` value seam — `Func<int, ItemChromeState, PartDelta>` on `ItemsView` (`PartDelta` = nullable
  `Fill`/`Foreground`/`Opacity`/`Corners`/`Padding`/`Border`/`Glyph`). The delta's values are baked into the chrome
  record DURING construction (a plain `with`-swap into the already-allocated `BoxEl`/`TextEl`), so per-item variation
  costs ZERO extra allocation and is provably shape-stable. Two guards enforce it in CI: a `[Conditional("DEBUG")]`
  shape-hash assert in the realizer (`TreeReconciler`, at the recycle point — hashes element-type + child arity +
  per-child type/Key; erased from the shipping AOT binary) and a steady-scroll `HotPhaseAllocBytes == 0` check
  (`cp2.partdelta.alloc`). The `PartDelta` lambda must be PURE-VALUE — no `new`/box/LINQ/`Animate` per call (those
  allocate; the displacement seed is edge-triggered for exactly this reason). Per-item STRUCTURE uses
  permanently-present invisible parts (`Opacity=0`/`Width=0` flips on always-present keyed children — WinUI's
  `x:DeferLoadStrategy`-vs-`Opacity=0` pattern), NEVER add/remove children (that would trip the shape-hash guard).
  List-UNIFORM `TemplateParts` modifiers are legal again via the apply-once prototype cache
  (`TemplateParts.TryApplyCached`, keyed on the parts epoch — invalidates when the modifier map mutates; no theme-epoch
  term because a theme switch forces full reconstruction); per-item CONTENT differences must use `PartDelta`, never a
  per-item `TemplateParts` modifier in a recycled path.
- If a public factory returns `Embed.Comp(() => new Core { ... })`, every runtime-changeable prop must flow through a
  `Props` record plus `Ctx.Provide`, or through a caller-owned `Signal<T>` read by the component. The factory is not
  re-run when the parent re-renders, so fields set in the object initializer are mount-only seeds. Use frozen fields
  only for `Initial*` values or configuration that is explicitly never expected to change.
