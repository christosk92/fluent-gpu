---
name: fluentgpu
description: Use when building UI with, or editing, the FluentGpu framework (this repo) — the signals-first, NativeAOT, GPU-rendered .NET UI engine. Covers the reactive/signals model, components & hooks, controls, layout, the render pipeline, performance rules, pitfalls, and the where-to-change-what file map.
---

# Working with FluentGpu

FluentGpu is a near-zero-alloc, NativeAOT, D3D12-rendered .NET 10 UI engine. React/Reactor authoring surface
(`Element` records + `Component` + hooks) on a **signals-first (Solid-style) reactive core**. Full guide:
`docs/guide/` (start at `docs/guide/README.md`). This skill is the fast path + the rules that prevent most bugs.

> **✅ Animation engine — REWORKED (landed + verified, 521 VerticalSlice gates green).** Motion is a **signals-first** model: one POD `AnimValue` slab keyed `(node, channel)` driven by the slab scheduler (the class is still named `AnimEngine`) + the analytical closed-form spring (sampled at absolute `t` — dt-deterministic, replaced the sub-stepped Euler) + the complete declarative surface (`Transition`/`WhileHover`/`WhilePressed`/`WhileFocus`/`Enter`/`Exit`/`Stagger`/`Layout`), with **brush/color as just another channel** (the `BrushFade` channel; `BrushTransitionMs` still triggers it) and **reduced-motion as a value, never a `Use*` early-return**. `InteractionAnimator` and the `AdvanceBrushAnims` ticker are **deleted** (subsumed as `HoverFade`/`PressFade`/`BrushFade` side-table channels); `ConnectedAnimation` → `DetachedAnimSlab`/`RecordDetached` rebuild rides `FG_DETACHED_FLY` (default-off = the proven live-overlay path). Implemented design: `docs/plans/animation-engine-rework-design.md`. **Prefer the declarative surface for new motion;** `BrushTransitionMs`/`MotionRecipes.*`/the `Use*` motion hooks still work (repointed at the new engine), so existing controls are unchanged.

## The one mental model

A change reaches pixels through **one mechanism: a signal**. Reading a signal subscribes the current reactive
computation; writing it re-runs only the computations that read it. A component's render is one computation; a
property *binding* is a finer one. **No full-app re-render, no global dirty flag.** Three update paths, cheapest first:

| Mechanism | Re-runs | Cost | For |
|---|---|---|---|
| Binding (`Transform`/`Opacity`/`Fill` set to a Func/signal) | one effect → one node prop | compositor-only (no render/reconcile/layout) | slider scrub, scroll, progress, hover glow |
| Granular re-render (`UseState`/`UseSignal`) | owning component's subtree | render + reconcile + scoped relayout of that subtree | normal state, value-displaying text |
| Reactive control-flow (`Flow.For`/`Flow.Show`) | one boundary effect → keyed diff | structural reconcile of that boundary | dynamic lists / conditionals |

## Rules (these prevent ~90% of bugs)

1. To make something update, a signal it **reads** must change. `.Value` subscribes; `.Peek()` does not.
2. `Component.Render()` re-runs on its **own** state/context only — never because a parent re-rendered.
   **Parent→child data flows via signals or context, never constructor args** (those freeze at mount).
3. `ReactiveComponent.Setup()` runs **once**. Show dynamic values via a **bound prop** (`Text = sig` signal-direct, or `Text = Prop.Of(() => …)` for derived text),
   never `Ui.Text(sig.Value)`. (#1 signals-native mistake.)
4. A bind thunk must read `.Value` (subscribes), not `.Peek()`.
5. Every bindable channel is ONE `Prop<T>` prop taking a value, a `Func<T>` (`Prop.Of` for inline lambdas), or a concrete signal. Bound `Transform`/`Opacity`/`Fill` = compositor-only; bound `Width`/`Height`/`Text` = scoped relayout.
   Prefer a transform bind for hot values.
6. Never write a signal during render (loops). Use an event handler or `UseEffect`.
7. Hooks run in stable call order (no hooks in `if`/loops).
8. Scoped relayout needs a **boundary**: give big containers explicit `Width`+`Height`+`ClipToBounds=true` so a deep
   change can't relayout the page.
9. Zero managed allocation in paint phases 6–13: wire bindings/effects once at mount; never `new`/box/LINQ in a bind
   thunk or hot effect body.
10. High-frequency scalar (slider/scroll)? Bind it (`Slider.Bind(FloatSignal)`), don't `setState` per move.

## Author UI (cheat sheet)

```csharp
using static FluentGpu.Dsl.Ui;   // VStack, HStack, Text, Heading, Button, Image, Grid, ScrollView
using FluentGpu.Hooks;            // Component, ReactiveComponent, Embed, Ctx, Flow
using FluentGpu.Signals;          // Signal<T>, FloatSignal, Memo<T>
using FluentGpu.Controls;         // Button, Slider, NavigationView, Virtual, …

sealed class Counter : Component {
  public override Element Render() {
    var (n, setN) = UseState(0);                       // reading n subscribes this component
    return VStack(8, Text($"n={n}"), Button.Accent("+", () => setN(n + 1)));
  }
}
// hot path — no re-render on drag:
var vol = UseFloatSignal(0.5f); Slider.Bind(vol);
// compose / context / reactive lists:
Embed.Comp(() => new Counter());
Ctx.Provide(MyCtx, "dark", child);
Flow.For(() => xs.Value.Count, i => Row(xs.Value[i]), keyOf: i => xs.Value[i].Id);
Flow.Show(() => open.Value, panel, fallback);
```

Run an app: `FluentApp.Run(() => new App());` (`src/FluentGpu.WindowsApp/FluentApp.cs`).

## Verify (do this after EVERY engine change — evidence before claiming done)

```bash
dotnet build src/FluentGpu.VerticalSlice          # must be clean
dotnet run   --project src/FluentGpu.VerticalSlice # must print "ALL CHECKS PASSED"
```
The harness (`src/FluentGpu.VerticalSlice/Program.cs`) runs ~60 cross-seam golden checks headlessly (no GPU/window).
Add a check with `Check("…", cond, detail)` and call it from `Main`. GPU pixels are a separate manual check.
Useful: `FrameStats` from `RunFrame()` — `Rendered` (false ⇒ compositor-only), `ComponentsRendered`,
`HotPhaseAllocBytes` (must be 0 steady). Diagnostics: `FG_DUMP=1` (scene dump), `FG_DIAG=1`.

## Where to change what

All engine subsystems now live under the single `src/FluentGpu.Engine` project (one folder per former project, namespaces unchanged); the Windows backend (D3D12/DirectWrite/etc.) is `src/FluentGpu.Windows`.

| Area | File |
|---|---|
| Signals runtime | `src/FluentGpu.Engine/Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs` |
| Hooks | `src/FluentGpu.Engine/Hooks/RenderContext.cs` (impl) + `Component.cs` (surface) |
| Reconcile / render-effects / For/Show / bindings / context | `src/FluentGpu.Engine/Reconciler/Reconciler.cs` |
| Element shapes / props / binds | `src/FluentGpu.Engine/Dsl/Element.cs`; `src/FluentGpu.Engine/Hooks/{ControlFlow,Context,ComponentEl}.cs` |
| DSL helpers / modifiers | `src/FluentGpu.Engine/Dsl/Factories.cs`, `Modifiers.cs` |
| Controls | `src/FluentGpu.Controls/*.cs` (composition only) — WinUI fidelity rules: `docs/guide/control-fidelity.md` |
| Control visual state / motion | `StateBrush` ramps + `InteractionAnimator` progress; `BoxEl.{Hover,Pressed}{Fill,BorderColor,Opacity}` + `{Hover,Press}Scale` + `{Hover,Press}DurationMs/Easing`. Declare targets/specs, NOT a state matrix or per-control runtime |
| Explicit control timelines | `AnimEngine` keyframes/channels (`Opacity`, transform, stroke trim, FLIP/reveal); use for draw-on paths and authored timelines, not hover/press visual states |
| Rounded-rect / border rendering | `src/FluentGpu.Engine/Render/SceneRecorder.cs` + `src/FluentGpu.Windows/D3D12/{RoundRect,Gradient}Pipeline.cs` — hollow SDF ring (no donut), `InsetCorners`, VS quad inflation for stroke band + AA |
| Frame loop / scheduling | `src/FluentGpu.Engine/Hosting/AppHost.cs` (`RunFrame`/`Paint`; flush = phase 3) |
| Layout / scoped relayout | `src/FluentGpu.Engine/Layout/FlexLayout.cs`, `LayoutInvalidator.cs` |
| Retained scene (SoA, dirty flags) | `src/FluentGpu.Engine/Scene/{SceneStore,Columns}.cs` |
| Scroll-driven effects (sticky / overscroll-stretch / parallax / fade / collapse / shy header / pull-to-refresh / scrollbar flags / nested scroll) | author via `Element.ScrollBinds` (a `ScrollBindDsl[]`: `PinTop`/`StretchFromTop`/`{From,To,Range,OutStart,OutEnd}`, `OnFlag`, `OnScrollGeometryChanged`, `Chaining`); engine = the generic zero-alloc binding evaluator `src/FluentGpu.Engine/Animation/{ScrollBind,ScrollBindEval}.cs` + `ScrollState` predicate flags. Design: `docs/plans/generic-hookable-scroll-engine-design.md` |
| Record → DrawList | `src/FluentGpu.Engine/Render/SceneRecorder.cs` |
| Theming tokens + LIVE theme switching (animated, in-place; gotchas) | `src/FluentGpu.Engine/Dsl/Tokens.cs` (`Tok`), `Theme.cs` — **read `theming.md` before any theme work** |
| Tests | `src/FluentGpu.VerticalSlice/Program.cs` |
| Windows OS services | `src/FluentGpu.WindowsApi/*` (pillars, refs Engine only) — see below |

## Windows OS services (`FluentGpu.WindowsApi`)

AOT-clean Win32/WinRT interop behind a small managed surface — **no** WindowsAppSDK NuGet, **no** CsWinRT, **no** `ComWrappers` subclassing, **no** reflection. Ten pillars; common entry points:

- **Notifications** (`Notifications/`) — **fluent toasts, no XML**: `Toast.Create().Title(..).Body(..).Button("Open", b => b.Argument("action","open").Success()).DismissButton().Tag("id").ShowVia(notifier)`. Configurable `ToastButton` (Foreground/Background/`Protocol`/`Dismiss`, `Icon`, `Success`/`Critical`, `Tooltip`, `NextToInput`, `InContextMenu`); `TextBox`/`Selection`/`Header`/`Progress` inputs; `ToastNotifier.Show(builder)` reads the carried `Tag`/`Group`. `BuildXml()` is the raw escape hatch. Register an AUMID + COM activator via `ToastNotifier.Register(clsid, displayName)` first.
- **Storage** (`Storage/`) — `AppDataStore.ForUnpackaged(pub, prod)` typed Get/Set persisted under `HKCU\Software\<pub>\<prod>` (String/Bool/Int/Long/Double/Bytes; Keys/Contains/Remove/Clear; Local/Cache/Temp folders). `SettingsStore.ForUnpackaged(pub, prod, runtime)` wraps it as write-through `Signal<T>` (bind a toggle straight to `settings.Bool("muted")`).
- **Power** (`Power/`) — `PowerSession.KeepAwake(keepDisplayOn)` (disposable), `Suspending`/`Resumed` events, and `PowerSession.ReadPower()` → `PowerStatus` (AC/DC, battery %, charging, energy-saver, est. discharge) via `GetSystemPowerStatus`.
- **Media / SMTC** (`Media/`) — `SystemMediaControls.GetForWindow(hwnd)`: `UpdateDisplay`/`SetPlaybackStatus`/`SetEnabledButtons`/`ButtonPressed` (+ `ButtonDispatcher` to hop the OS-thread callback) and **timeline scrub** `UpdateTimeline(position, end)` / `PlaybackRate`.
- Others: **Credentials** (locker), **Packaging** (identity), **Activation** (protocol/redirect), **Dialogs** (file pickers, owner HWND), **Shell** (taskbar progress/jump list), **Network** (NLM connectivity + change events).
- **OS file/folder drop is engine-level, not a pillar:** a `BoxEl.DropTarget = new DropTargetSpec(new[]{ DropKinds.Files }, OnEnter, OnLeave, OnDrop: s => { var d = (FileDropData)s.Payload!; … })` receives an Explorer drag. The Windows backend auto-registers the top-level window with a hand-vtable OLE `IDropTarget` (`Win32DropTarget` → `InputHooks.ExternalDrag*` → `InputDispatcher` → `DragDropContext`), so the zone gets **live hover** (DragEnter/Over/Leave) + the OS "+Copy" cursor; the file list is read once, at drop. Handlers fire on the UI thread. Gates: `e5dragdrop.ext` + smoke `6c.1`.

## Drag & drop styling (`FluentGpu.Controls.DropZone` / `DragPreviewLayer`)

Style drag/drop without touching the engine internals:
- **Drop zone (Vercel "Drop to Deploy" feel):** `DropZone.Create(accept: new[]{ DropKinds.Files }, onDrop: s => …, content: body)` (or `DropZone.Window(…)` for whole-window). It restyles ITSELF — a cross-faded dashed accent ring + accent glow that fades in while a COMPATIBLE drag is live ("you can drop here") and brightens on hover — with **no second labelled panel** (so the content's own text never doubles). Works for in-app AND OS file drags.
- **Lifted-ghost knobs:** set `DragSource.Style = new DragVisualStyle { Opacity = …, Shadow = …, Scale = … }` on a `BoxEl.Draggable` (default = WinUI 0.80 + flyout shadow). Honored in `DragController`.
- **Custom floating preview (the "cursor tip"):** mount one `DragPreviewLayer.Of(state => state.Kind == "track" ? new Chip(...) : null)` at the app ROOT (top of a root ZStack); it follows the cursor using `UseDragState()`. Set the source's `DragVisualStyle.Opacity = 0` to hide the lifted node and show only the custom preview.
- **Dashed borders anywhere:** `BoxEl.BorderDashOn`/`BorderDashOff` (0 = solid) → `SceneRecorder.StrokeRoundRectDashed`.
- Reactive drag state: `UseDragState()` → `DragState{ Active, Kind, Position, Payload }`, re-renders on drag begin/move/end.

Interop posture is binding (the three allowed modes + the strict NOs): see `docs/plans/wasdk-migration-survey.md`. The live demo of every pillar is `src/FluentGpu.WindowsApp/WindowsApiPage.cs` (one card each).

## WinUI controls, animations, and states

Before changing a control, read the whole WinUI template in `C:\WAVEE\microsoft-ui-xaml`. Framework controls usually
live in `controls\dev\CommonStyles\<Control>_themeresources.xaml`; also check `<Control>_themeresources_perf2026.xaml`
when present. Muxcontrols usually live under `controls\dev\<Control>\`. Shared timing tokens are in
`controls\dev\CommonStyles\Common_themeresources_any.xaml`: `ControlNormalAnimationDuration=250ms`,
`ControlFastAnimationDuration=167ms`, `ControlFasterAnimationDuration=83ms`, and
`ControlFastOutSlowInKeySpline=0,0,0,1`.

Search for `VisualState`, `Storyboard`, `DoubleAnimation`, `KeyFrame`, `Setter`, `AnimatedIcon.State`, `PointerOver`,
`Pressed`, `Selected`, and `Checked`. Pitfalls: `_perf2026` files can differ; `Pressed` may be hidden inside names like
`CheckedPressed` or nested styles; invisible parts with `Opacity=0` can be the actual animation (RadioButton's
`PressedCheckGlyph`); duration/easing/color values are often indirect resources; zero-duration animations and setters
still matter.

State model:

```text
logical axis:     Unselected/Unchecked --click--> Selected/Checked --optional--> Indeterminate
interaction axis: Normal --pointer enter--> PointerOver --pointer down--> Pressed
                  Pressed --pointer up inside--> PointerOver
                  PointerOver --pointer leave--> Normal
                  any enabled state --disabled--> Disabled
```

Map WinUI cross-product names to axes: `SelectedPressed` = selected logical ramp + pressed interaction target.
Hover/press visual states belong in `InteractionAnimator` data (`HoverFill`, `PressedOpacity`, `PressScale`,
durations/easings). Explicit timelines belong in `AnimEngine` (stroke trim, reveal, FLIP, open/close, real AnimatedIcon
segments). Do not add a per-control VisualStateManager or duplicate animation runtime.

Design corpus (architecture authority, canon-gated) is `design/`; as-built reactive model is
`design/subsystems/reconciler-hooks.md §0bis`. After editing `design/*`: `powershell -File design/check-canon.ps1`
(must exit 0). Usage docs go in `docs/`, not `design/`.

## Deeper docs (read for the relevant task)
- `theming.md` (this skill dir) — **how theming + LIVE theme switching work end-to-end**: tokens, the `Epoch`/`RethemeAll`/transition-window mechanism, the OS-follow + persistence wiring, what updates vs what's frozen, and the gotchas (frozen constructor-arg literals, `Flow.For`/bound colors, control `ColorF` props, app-local color constants, Mica/DWM). **Read before any theme work or "X won't change theme" debugging.**
- `docs/guide/reactivity.md` — signals, hooks, `Component` vs `ReactiveComponent`, bindings, context (the core).
- `docs/guide/components-elements-layout.md` — element zoo, layout, controls, navigation, virtualization, theming.
- `docs/guide/rendering-and-performance.md` — frame pipeline, scoped relayout + boundary firewall, optimization guide.
- `docs/guide/pitfalls.md` — symptom → cause → fix.
- `docs/guide/control-fidelity.md` — **building WinUI-faithful controls**: exact WinUI template/storyboard/timing-token
  lookup, state-search pitfalls, the logical-state x interaction-state graph, rounded-rect rendering rules,
  `StateBrush`/`InteractionAnimator` visual states, explicit `AnimEngine` timelines like checkmark stroke trim, and the
  empirical verify workflow (golden checks + `--shot` + slow-motion proof). Read before any control parity work.
- `docs/guide/motion-recipes.md` — the **Expressive Motion Kit** (`MotionRecipes.*`): named transitions adopted from
  transitions.dev (number pop-in, error shake, skeleton reveal, success check, icon swap, badge pop, soft/texts reveal,
  neighbour-falloff hover) on the engine's springs + the new per-node **self-blur** (`BoxEl.Blur`/`AnimChannel.BlurSigma`) +
  the expressive curves (`Easing.SmoothOut`/`Overshoot`/`Pop`) and `Expressive.*` tokens. An OPT-IN app-author palette —
  framework controls keep their Fluent curves; do NOT restyle the control kit with these.
