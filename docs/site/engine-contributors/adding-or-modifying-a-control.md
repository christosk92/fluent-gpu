# Adding or modifying a control

This page is for people **adding a control to, or changing a control in, `src/FluentGpu.Controls/`** so it looks and
moves 1:1 with WinUI 3. If you are *using* the controls in an app, read the app-author
[Controls cookbook](../app-authors/controls-cookbook.md) and the guide's
[Elements, layout, controls & theming](../../guide/components-elements-layout.md) first — they own the authoring
surface. This page covers the rules a control author obeys, where each piece lives in `src/`, and how to prove the
result is faithful with the harness.

Two upstream docs are the source of truth this page operationalizes; keep them open while you work:

- The guide's [Building WinUI-faithful controls](../../guide/control-fidelity.md) — visual-state ramps, the two motion
  engines, the rendering rules, and the template-parts law (with the bugs that taught each rule).
- The guide's [WinUI control parity audit](../../guide/winui-control-parity-audit.md) — the source-backed per-control
  diff and the **acceptance rule** every control patch must satisfy before it is called 1:1.

The architectural authority is the controls subsystem design doc,
[`design/subsystems/controls.md`](../../../design/subsystems/controls.md); the precedence authority for any
cross-cutting contract is [`design/SPEC-INDEX.md`](../../../design/SPEC-INDEX.md).

## The rule: controls are pure composition over the real seams

A FluentGpu control is **not a node type and not a peer object** — it is a render function (a `Component`, or more
commonly a static factory returning an `Element`) that composes primitive Elements and wires the interaction/semantics
seams the engine already owns. There is exactly one retained tree; a control is "just" disciplined composition.

The hard constraint, stated in [`controls.md` §0/§13](../../../design/subsystems/controls.md): a control **introduces
no new `SceneStore` column, no new DrawList opcode, no new PAL seam, no new RHI method, and no new engine hook.** The
seam program already minted every primitive a control needs. If you find yourself reaching for a one-off animation
system or a new render primitive to make a control work, **stop** — that is the signal to add the missing primitive
*once* to the engine (`BoxEl`, `TextEl`, `AnimEngine`, or a popup/focus service) and let every control consume it, not
to fork it inside one control. This is the single most important review gate: the WinUI parity audit's closing rule is
*"if a state cannot be represented without one-off control code, stop and add the shared engine primitive first."*

What a control file in `src/FluentGpu.Controls/*.cs` is allowed to contain:

- `BoxEl`/`TextEl`/`ImageEl` composition (the visual tree),
- a per-control `Style` record (the WinUI theme-resource values, sourced and annotated line-by-line),
- the interaction wiring (`OnClick`, `OnKeyDown`, `OnDrag`, `Role`, `IsEnabled`, …),
- `TemplateParts` plumbing (the one styling door, below),
- and at most a small `internal static class XxxMotion` of WinUI timing/easing constants.

A control owns **no `ComPtr`, no GPU object, no native handle**. It writes Elements (UI thread); the reconciler writes
columns; the render thread owns every `ComPtr`. That is why controls are 100% portable and recompile unchanged for
macOS. See [the seams page](./seams-rhi-pal-text.md) and
[the render pipeline page](./render-pipeline-and-scenerecorder.md) if a control genuinely needs a new primitive — that
work lands *there*, then the control consumes it.

> **Where the code is.** Controls are composition-only: `src/FluentGpu.Controls/*.cs`. The primitives they compose live
> elsewhere and are the place an honest gap gets fixed: Element shapes/props in `src/FluentGpu.Engine/Dsl/Element.cs`; the
> `TemplateParts` door in `src/FluentGpu.Engine/Dsl/TemplateParts.cs`; the interaction animator + authored timelines in
> `src/FluentGpu.Engine/Animation/AnimEngine.cs`; the rounded-rect rasterizer in `src/FluentGpu.Engine/Render/SceneRecorder.cs`;
> theme tokens in `src/FluentGpu.Engine/Dsl/Tokens.cs`. The reactive shell for stateful controls (`Component`, hooks) is
> `src/FluentGpu.Engine/Hooks/`. See the full [Contributor map](./contributor-map.md).

## Step 1 — read the WHOLE WinUI template

Skipping this is the recurring failure the fidelity guide exists to prevent: skim a summary and you *will* miss a state
(the pressed glyph dim, the disabled stroke recolor, the press-only proto-dot). For every control, parity is checked
against **three** source layers in the local WinUI checkout at `C:\WAVEE\microsoft-ui-xaml`, not just the XAML:

1. **Styling & visual states** — `controls\dev\CommonStyles\<Control>_themeresources*.xaml` (framework controls) or
   `controls\dev\<Control>\*.xaml` (muxcontrols). Read every `VisualState` in every group, every `Storyboard`/`Setter`,
   every brush resource, and any `AnimatedIcon` source.
2. **Behavior** — `controls\dev\<Control>\*.cpp`, `*.h`, `*.idl` (keyboard, focus, selection, repeat timing, parser
   logic).
3. **Generated source** — `controls\dev\Generated\*.properties.*`, especially `TemplateSettings`: many storyboards do
   not hard-code their geometry — they bind to `TemplateSettings` computed in C++ at runtime.

Resolve every `{StaticResource …}` / `{ThemeResource …}`. The shared **timing/easing tokens** almost always live in
`controls\dev\CommonStyles\Common_themeresources_any.xaml`: `ControlNormalAnimationDuration = 250ms`,
`ControlFastAnimationDuration = 167ms`, `ControlFasterAnimationDuration = 83ms`,
`ControlFastOutSlowInKeySpline = 0,0,0,1` (the engine's `Easing.FluentPopOpen`). Colors/sizes bounce through
per-control theme keys; map each to a `Tok.*` token (Dark **and** Light).

The `RadioButton.cs` and `Slider.cs` headers are the model for how to *record* this work: every `Style` field carries
the exact WinUI key and source line it came from (e.g. `DotHoverSize … // RadioButtonCheckGlyphPointerOverSize
(template:180)`). Annotate as you read — the annotations *are* the parity proof a reviewer checks.

Pitfalls that caused real misses (from [control-fidelity §0](../../guide/control-fidelity.md)):

- Don't search only for `Animation` — some state changes are `Setter`s or zero-duration animations.
- Don't search only for `Pressed` — WinUI names cross-product states like `CheckedPressed`, `SelectedPointerOver`,
  `UncheckedDisabled`, or nests `Pressed` in a child style.
- Don't ignore invisible parts — RadioButton's press grow is a *separate* `PressedCheckGlyph` with `Opacity=0` at rest.
- If a `_perf2026` template exists, diff it against the non-perf file and document which one the control follows.

## Step 2 — model visual state as `StateBrush` ramps over orthogonal axes

WinUI enumerates the **cross-product** of state axes — CheckBox = `{Unchecked,Checked,Indeterminate} ×
{Normal,PointerOver,Pressed,Disabled}` = 12 `VisualState`s, each restating several setters. **Do not replicate that.**
That combinatorial restatement is precisely why states get missed.

Instead, model each visual property as a function of the **orthogonal** axes — logical state (checked/selected/…) ×
interaction (hover/press/disabled, which the engine tracks per node) — with a **`StateBrush` ramp** from
`src/FluentGpu.Controls/ControlMotion.cs`:

```csharp
public readonly record struct StateBrush(ColorF Rest, ColorF Hover, ColorF Pressed, ColorF Disabled);
```

- Declare **one ramp per logical state** (CheckBox = 2: an off ramp + an accent ramp), picked by the logical state.
- Wire `Rest/Hover/Pressed` into the element's `Fill`/`HoverFill`/`PressedFill`,
  `BorderColor`/`HoverBorderColor`/`PressedBorderColor`, or `Opacity`/`HoverOpacity`/`PressedOpacity`.
- Put the timing on the *same* element via `HoverDurationMs`, `PressDurationMs`, `HoverEasing`, `PressEasing` — use
  WinUI's tokens when the template does (83 / 167 / 250 ms, usually `Easing.FluentPopOpen`).

The engine's `InteractionAnimator` eases the displayed value toward the current target and retargets interruptibly.
Disabled is a flat resting swap (`StateBrush.Resting(enabled)`) unless WinUI explicitly animates disabled. The target a
node displays falls out of one priority rule (no `GoToState`, no storyboards):

```text
if !enabled:       use logical.Disabled
else if pressed:   use logical.Pressed
else if hovered:   use logical.Hover
else:              use logical.Rest
```

Treat WinUI names like `SelectedPressed` or `UncheckedPointerOver` as *labels for points in this graph*, not separate
code paths: `SelectedPressed` = the selected logical ramp + the pressed interaction target; a `CheckedNormal →
CheckedPressed` change is one interaction-axis retarget the `InteractionAnimator` handles. Focus is another orthogonal
axis — add it explicitly when the control has focus visuals; never fold it into hover or pressed.

`Button.cs` shows the wiring concretely: the `Style` record holds `HoverBackground`/`PressedBackground`/
`DisabledBackground` (+ matching foreground and border-brush legs), and `Build` fans them onto the root box's
`Fill`/`HoverFill`/`PressedFill` and the label's `Color`/`HoverColor`/`PressedColor`/`DisabledColor`. Disabled is a
*logical* swap: `Fill = enabled ? s.Background : s.DisabledBackground`, with `IsEnabled = enabled` arming the engine's
hit-test gate so hover/press never fire while disabled.

> **Honest gap to know about.** `TextEl` color now has the unified `Color` channel plus `HoverColor`/`PressedColor`/
> `DisabledColor` legs (Button's label uses them). The audit's standing cross-cutting note is that *some* text/glyph
> ramps still need inheriting interaction progress from the nearest interactive ancestor; if a control needs a
> per-state text effect that isn't expressible, that is an engine extension (`TextEl`), recorded honestly in the
> [parity audit](../../guide/winui-control-parity-audit.md) — not a one-off in the control.

## Step 3 — sizes, geometry, sub-parts, and glyphs from the template

Pull exact values, never eyeballed ones: box size, glyph size, corner radius, padding, min-height, the precise Segoe
Fluent glyph(s), and any indicator parts. Put them on the `Style` record so they are restylable and reviewable.

`RadioButton.cs` is the reference for a multi-part control with geometry-driven motion. The 20px ring, the 12→14→10px
dot ramp, and the 4px unchecked-pressed proto-dot are all `Style` fields traced to template lines; the press grow/shrink
is expressed as **transform scale**, not a width/height keyframe, so the visual size changes while layout stays put:

```csharp
float hoverScale = s.DotSize > 0f ? s.DotHoverSize / s.DotSize : 1f;   // 14/12
float pressScale = s.DotSize > 0f ? s.DotPressedSize / s.DotSize : 1f; // 10/12
var dot = new BoxEl
{
    Key = "CheckGlyph",
    Width = dotSize, Height = dotSize,
    Corners = Radii.Circle(dotSize),
    Fill = !isEnabled ? s.DotDisabled : s.Dot,
    HoverScale = isEnabled ? hoverScale : 1f,
    PressScale = isEnabled ? pressScale : 1f,
    HoverDurationMs = RadioButtonMotion.ControlNormalMs,   // 250ms (template:255-260)
    PressDurationMs = RadioButtonMotion.ControlNormalMs,   // 250ms (template:292-297)
    HoverEasing = RadioButtonMotion.FastOutSlowIn,
    PressEasing = RadioButtonMotion.FastOutSlowIn,
};
```

`HoverScale`/`PressScale` are **composited-only** — the recorder scales the node about its centre by the eased
hover/press progress of its nearest interactive ancestor; it never changes layout or hit-test (HitTest reads `Bounds`,
never `LocalTransform`). That is why a slider thumb's inner dot grows on *control* hover (the track is the interactive
ancestor): see `Slider.BuildThumb`, where the 12px inner dot rests at the WinUI Normal-state `0.86` scale and eases to
net `1.167`/`0.71` on hover/press.

For the rounded-rect rendering rules that geometry depends on (the hollow-ring border, the `bw/2` corner inset, the
SDF quad inflation — each the fix for a real bug), see [control-fidelity §1](../../guide/control-fidelity.md). Do not
regress them.

## Step 4 — motion ownership: `InteractionAnimator` vs `AnimEngine`

There are two animation systems on purpose, with one ownership rule:

- **`InteractionAnimator` owns hover/press visual states.** Use `BoxEl.{Hover,Pressed}{Fill,BorderColor,Opacity}`,
  `HoverScale`, `PressScale`, and the per-element duration/easing fields. This covers every WinUI state-group
  storyboard that reacts to `PointerOver`/`Pressed`, including child parts that inherit the root's interaction
  progress.
- **`AnimEngine` owns authored timelines.** Use keyframes/channels for explicit non-input timelines: FLIP, reveal,
  stroke trim, path draw-on, open/close, or any `AnimatedIcon` segment with real keyframes. Channels are the
  `AnimChannel` enum (`src/FluentGpu.Engine/Animation/AnimEngine.cs`): `TranslateX/Y`, `ScaleX/Y`, `Rotation`, `Opacity`,
  `SizeW/H`, `StrokeTrimStart/End`, `ClipL/T/R/B`, `LayoutW/H`.
- **Enter/exit presets are lifecycle sugar over `AnimEngine`.** `ControlMotion.IconPop` and `ControlMotion.DotScaleIn`
  apply when the reconciler **inserts/removes a keyed child** — use them *only* when WinUI actually inserts or appears a
  part. (RadioButton's checked glyph is **not** a dot-pop: it is visible immediately and changes size via hover/press,
  which is why `DotScaleIn`'s own doc-comment warns "WinUI RadioButton itself does not use this.")
- **Press-only parts stay in the tree with explicit targets.** RadioButton's `PressedCheckGlyph` is the model: a
  separate `Key` from the checked dot, `Opacity = 0`/`PressedOpacity = 1`, a 4→10 `PressScale`, `PressDurationMs = 167`,
  `PressEasing = FastOutSlowIn`.

The Expander chevron shows an authored timeline driven from a typed computed-settings record. The rotation is owned by
`AnimEngine` (not a static `Rotation`); an `OnRealized` ref captures the chevron node, and a `UseEffect` keyed on `open`
retargets the rotation from the **live** value (never snapping on an interrupted toggle), with WinUI's Lottie spline:

```csharp
anim.Animate(chevronRef.Value, AnimChannel.Rotation, from, to, ChevronMs,
    EasingSpec.CubicBezier(0.167f, 0.167f, 0f, 1f));
```

**Layout-size changes that must reflow neighbours use `SizeMode.Reflow`** — the one *deliberate divergence from WinUI*.
Where WinUI snaps the layout and only translates content (Expander ExpandDown/CollapseUp), FluentGpu eases the space
itself through real boundary-scoped relayout each tick, keeping the WinUI timings/easings exactly (expand 333ms
`KeySpline 0,0,0,1`; collapse 167ms `KeySpline 1,1,0,1` via `ExitDynamics`). The Expander's clip wrapper is the
reference consumer — a `LayoutTransition` with `Size: SizeMode.Reflow` and `Anchor: SizeAnchor.Trailing` on an
always-mounted `ClipToBounds` host, with the declared `Height = open ? float.NaN : 0f` toggle as the whole motion
trigger. `SizeMode` values: `Auto`, `Reveal`, `ScaleCorrect`, `Relayout`, `Reflow` — use `Reflow` only when siblings
must move; for a glyph resize that should *not* reflow, use transform scale (Step 3).

## Step 5 — the `TemplateParts` law

Customization of a control's internals goes through **one generic door** — `TemplateParts`
(`src/FluentGpu.Engine/Dsl/TemplateParts.cs`; CSS `::part` / WinUI lightweight styling, signals-native) — never through
per-control feature props. Every new control obeys these rules:

- **Export `public const string PartXxx` consts** for each named template part — mirror the WinUI template-part
  `x:Name` where an equivalent exists (Expander: `Root`/`Header`/`Chevron`/`Clip`/`Content`) — plus a
  `public TemplateParts? Parts;` field (or a trailing `TemplateParts? parts` factory arg). Document each part's
  **OWNED** props on its const (the props the control re-asserts and a modifier therefore cannot win).
- **Route every named part** through `parts.Apply(PartXxx, el)` after building it, then **re-assert the part's
  mechanics-critical props** with one trailing `with` — click/toggle handlers, reflow `Animate` specs, value-position
  binds, `Key`, `Role`, `TabStop` — and **chain** the ref-capture handler (`OnRealized`) via
  `TemplateParts.Chain`. A modifier can restyle everything but break nothing.
- **`parts.Apply` is type-preserving and null-safe**: it is a no-op (no allocation) when `parts` is null or the part
  has no modifier, and it keeps the *original* element if a modifier changed the record type (parts **style**; content
  **slots** like `Content`/`HeaderContent` **restructure**).
- **New styling knobs are banned.** The review question for any proposed public field: *could the caller write
  `Parts[PartX] = el => el with { … }` instead?* Then they must. Keep only: content **slots** (Element-typed), state
  signals/callbacks, options/config, and the theme token-bundle `Style` record — those are not part-styling.

The re-assert pattern, straight from `Button.Build` (the simplest case):

```csharp
// Parts: restyle anything (fills, corners, padding…); the click mechanics and the label slot always win.
return parts.Apply(PartRoot, root) with { OnClick = onClick, Role = AutomationRole.Button, Children = root.Children };
```

And the chained-ref form, from the Expander chevron (a modifier may add its own `OnRealized`; the control's must still
run to capture the rotation node):

```csharp
var m = cp.Apply(PartChevron, chevron);
chevron = m with { OnRealized = TemplateParts.Chain(chevronCapture, m.OnRealized) };
```

Two more part rules for collections and lists (see [control-fidelity §6](../../guide/control-fidelity.md) for the full
text):

- Part-name strings may match existing reconciler `Key`s for one vocabulary, but `Key` stays a separate identity
  concept — **repeated parts (tab items, tick marks) must never derive their `Key` from a part const.** (Slider's
  `PartTickBar` is applied to *every* mounted tick bar; the marks carry their own positions, not the part name.)
- Per-item **variation** in a virtualized/recycled path rides the **`PartDelta` value seam** (nullable
  `Fill`/`Foreground`/`Opacity`/`Corners`/`Padding`/`Border`/`Glyph` baked into the chrome during construction — zero
  extra allocation, shape-stable), **never** a per-item `TemplateParts` modifier in a recycled scroll path (that is the
  banned hazard; a CI shape-hash + `HotPhaseAllocBytes == 0` check enforce it). List-*uniform* part modifiers are legal
  again via the apply-once prototype cache (`TemplateParts.TryApplyCached`).

> **One transform owner.** Don't put a `ScrollBinds` entry (or a bound `Transform`) on a transform-owned part (e.g. the
> Expander clip mid-reflow, whose const explicitly warns against adding a scroll-driven transform bind there) — a
> scroll bind writes the node's `LocalTransform`, which a reflow already owns, so the two would clobber. A bound prop is
> a `Prop<T>` taking a value, a `Func<T>` (`Prop.Of` for inline lambdas), or a concrete signal — for the bound-prop
> mental model see [Signals internals](./signals-and-reactivity-internals.md) and the app-author
> [bindings page](../app-authors/signals-components-and-bindings.md).

## Step 6 — verify empirically: logs and pixels, not vibes

Evidence before claiming an animation works. Three layers, use all three (from
[control-fidelity §4](../../guide/control-fidelity.md)):

**Headless golden checks** — the deterministic, log-based truth, in `src/FluentGpu.VerticalSlice/Program.cs`:

```text
dotnet build src/FluentGpu.VerticalSlice          # must be clean
dotnet run   --project src/FluentGpu.VerticalSlice # must print "ALL CHECKS PASSED"
```

These are ~60 cross-seam checks with no GPU/window. The pattern: reconcile the control, run a `new FlexLayout(...).Run`,
walk to a node with `Child(scene, node, i)`, `engine.Tick(16f)`, and assert
`scene.Paint(node).Opacity` / `.LocalTransform.M11`. For the **live** path use a real `AppHost` + `ClickNode` and read
the node after the click frame. **A control animation is not done until a headless check proves the node's
opacity/scale/trim goes mid-flight → settled** (existing examples: RadioButton checked/pressed glyph sizes, the
interaction-ramp wiring, the CheckBox checkmark draw-on).

**GPU pixels** — a deterministic `ShotScene` (add per-control shot ids in `src/FluentGpu.WindowsApp/ShotScene.cs`):

```text
dotnet run --project src/FluentGpu.WindowsApp -- --screenshot out\x.png --shot <id> --frames N
```

To prove an animation **actually runs on the GPU**, temporarily slow its duration/spring response and capture an early
frame — a slow draw/grow forces a visibly mid-animation glyph; if it is already settled, the animation is not reaching
the GPU. (Static screenshots can look settled because window init paints/ticks several times before capture.)

**Diagnostics** — `FG_DIAG=1` enables `Diag` (anim seed/retarget events; scene dumps with `FG_DUMP=1`); `FrameStats`
from `RunFrame()` exposes `Rendered`, `ComponentsRendered`, and `HotPhaseAllocBytes` (must be `0` steady). For the
broader verification spine see [Frame pipeline and verification harness](./frame-pipeline-and-verification-harness.md)
and [Validation and gates](./validation-and-gates.md).

> **No "known residuals."** Fix every verification finding (warnings too, not just blockers) — including engine
> primitives like a stroke-glyph dim. A control is not 1:1 with a documented residual still flapping.

## The 6-step control-parity checklist and the acceptance rule

The process, condensed from [control-fidelity §5](../../guide/control-fidelity.md) — follow it, don't shortcut:

1. **Read the WHOLE WinUI template** — every `VisualState`/`Storyboard`/`Setter`, every brush key, sizes/padding/corner,
   the `AnimatedIcon` states; resolve durations/easings in `Common_themeresources_any.xaml` and brushes to `Tok` (Dark +
   Light).
2. **Map the state matrix to ramps** — one `StateBrush` per logical state for fill / stroke / glyph-fg / label-fg
   (rest/hover/pressed/disabled). Note where press *dims*.
3. **Sizes & geometry** — box/glyph size, corner radius, padding, min-height → exact `Style` values.
4. **Sub-parts & glyphs** — the right Segoe Fluent glyph(s), indicator parts, parts that appear on a state.
5. **Motion** — hover/press storyboards → `InteractionAnimator` targets/specs; authored timelines (stroke trim, reveal,
   FLIP, open/close) → `AnimEngine` keyframes/channels; enter/exit presets only for true insert/remove sub-parts.
6. **Verify** — a headless `Check()` for behavior + a key motion assertion; a `--shot` capture diffed against WinUI
   (incl. the slow-motion proof). Save a golden PNG.

**The acceptance rule.** Before marking any control 1:1, the PR must list (from
[the parity audit](../../guide/winui-control-parity-audit.md)):

- Exact WinUI XAML/template files read.
- Exact C++/H/IDL behavior files read, or `framework source not present in controls/dev`.
- Exact generated property/template-setting files read, or `Generated: none`.
- The mapped state matrix: logical state × interaction state × focus/disabled.
- The mapped motion source: `BrushTransition`, storyboard, `TemplateSettings`, animated-icon segment, or no animation.
- A headless vertical-slice check for the changed behavior or animation.
- A live GPU screenshot or slow-motion proof for motion the headless harness cannot prove.

And the load-bearing escalation: **if a state cannot be represented without one-off control code, stop and add the
shared engine primitive first.**

## Pairing the control with a gallery demo page

A control is not finished until it has a demo page in `src/FluentGpu.WindowsApp/` modeled on the WinUI Gallery. The two
pieces are `ControlExample.Build(...)` (`src/FluentGpu.WindowsApp/ControlExample.cs`) — a BodyStrong header + a bordered
example card + optional options/output panel + an optional collapsible **source-code** panel — and the page scaffold
`GalleryPage.Shell(...)` (`src/FluentGpu.WindowsApp/ControlGalleryPages.cs`).

`ControlExample` is a **factory, not a component** on purpose: the live `example` must refresh every render, and
component props are mount-only — a component would freeze the demo. Its signature:

```csharp
public static Element Build(string title, Element example, string? description = null,
    Element? options = null, Element? output = null, string? code = null, FlexAlign exampleAlign = FlexAlign.Start)
```

The `code:` argument is a raw-string snippet shown in the "Source code" expander; the expander reuses the **real**
`Expander` control with its content padding zeroed via the template-parts door (`Parts = new() { [Expander.PartContent]
= p => p with { Padding = Edges4.All(0) } }`) — the same lightweight-styling path an app would use. A representative
page, straight from `ControlGalleryPages.cs`:

```csharp
sealed class ToggleSwitchControlPage : Component
{
    public override Element Render()
    {
        var (a, setA) = UseState(true);
        var (b, setB) = UseState(false);
        return GalleryPage.Shell("ToggleSwitch", "A switch that a user can turn on and off.",
            ControlExample.Build("A simple ToggleSwitch", ToggleSwitch.Create(a, () => setA(!a)),
                output: BodyStrong(a ? "On" : "Off"),
                code: """
                var (a, setA) = UseState(true);

                ToggleSwitch.Create(a, () => setA(!a))
                """),
            ControlExample.Build("With header + On/Off content",
                ToggleSwitch.Create(b, () => setB(!b), header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected"),
                output: BodyStrong(b ? "Connected" : "Disconnected"),
                code: """
                ToggleSwitch.Create(b, () => setB(!b),
                    header: "Wi-Fi", onContent: "Connected", offContent: "Disconnected")
                """));
    }
}
```

Conventions to mirror:

- Use a **signal-bound readout** for hot, per-move values so only the readout updates:
  `GalleryPage.LiveText(() => $"{basic.Value:0.00}")` over `Slider.Bind(basic)` — never re-render the whole page on a
  drag. Use `output: BodyStrong(...)` for low-frequency, re-render-driven readouts.
- Show the *cheapest correct* path in the snippet: the Slider page leads with `Slider.Bind` (compositor-only) and
  annotates *why*, then shows `Slider.Ranged` for the controlled variant.
- One demo card per distinct facet (two-state vs three-state, simple vs header), each with its own `output` and `code`.
- For controls that need a sized frame (SplitView, TabView), wrap the live control in a fixed-size bordered `BoxEl`
  like `NavLayoutPages.cs`'s `Frame(...)`.

Register the page in the gallery's `PageInfo`/catalog so it appears in the nav (the existing pages and
`GalleryApp.ControlCatalog` are the pattern). See the app-author
[Controls cookbook](../app-authors/controls-cookbook.md) for the authoring-side view of these same controls.

## Canon link: the controls subsystem

The authoritative design for the control kit — the posture, the universal five-tuple contract every control obeys
(composition / UIA pattern + ControlType / keyboard / name-role / motion-cursor-RTL), the styling system, and the seam
matrix proving each seam has a control consumer — is
[`design/subsystems/controls.md`](../../../design/subsystems/controls.md). It is a **pure consumer** of the seams the
other subsystem docs own and adds no new opcode/column/seam/hook; when it references a type it uses that owner's
canonical shape. For precedence on any cross-cutting value, consult
[`design/SPEC-INDEX.md`](../../../design/SPEC-INDEX.md); for which doc owns which artifact, the ownership map in
[`design/subsystems/README.md`](../../../design/subsystems/README.md).

Remember the through-line: **a control is the disciplined composition of primitives the engine already owns.** If a
control patch wants a new primitive, that is engine work in `Dsl`/`Animation`/`Render` (and a line in the
[parity audit](../../guide/winui-control-parity-audit.md)'s cross-cutting-gaps table) — done once, consumed everywhere —
never a fork inside one control file.
