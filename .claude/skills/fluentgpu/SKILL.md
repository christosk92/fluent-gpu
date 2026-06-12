---
name: fluentgpu
description: Use when building UI with, or editing, the FluentGpu framework (this repo) — the signals-first, NativeAOT, GPU-rendered .NET UI engine. Covers the reactive/signals model, components & hooks, controls, layout, the render pipeline, performance rules, pitfalls, and the where-to-change-what file map.
---

# Working with FluentGpu

FluentGpu is a near-zero-alloc, NativeAOT, D3D12-rendered .NET 10 UI engine. React/Reactor authoring surface
(`Element` records + `Component` + hooks) on a **signals-first (Solid-style) reactive core**. Full guide:
`docs/guide/` (start at `docs/guide/README.md`). This skill is the fast path + the rules that prevent most bugs.

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
| Record → DrawList | `src/FluentGpu.Engine/Render/SceneRecorder.cs` |
| Theming tokens | `src/FluentGpu.Engine/Dsl/Tokens.cs` (`Tok`), `Theme.cs` |
| Tests | `src/FluentGpu.VerticalSlice/Program.cs` |

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
- `docs/guide/reactivity.md` — signals, hooks, `Component` vs `ReactiveComponent`, bindings, context (the core).
- `docs/guide/components-elements-layout.md` — element zoo, layout, controls, navigation, virtualization, theming.
- `docs/guide/rendering-and-performance.md` — frame pipeline, scoped relayout + boundary firewall, optimization guide.
- `docs/guide/pitfalls.md` — symptom → cause → fix.
- `docs/guide/control-fidelity.md` — **building WinUI-faithful controls**: exact WinUI template/storyboard/timing-token
  lookup, state-search pitfalls, the logical-state x interaction-state graph, rounded-rect rendering rules,
  `StateBrush`/`InteractionAnimator` visual states, explicit `AnimEngine` timelines like checkmark stroke trim, and the
  empirical verify workflow (golden checks + `--shot` + slow-motion proof). Read before any control parity work.
