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
| Binding (`TransformBind`/`OpacityBind`/`FillBind`) | one effect → one node prop | compositor-only (no render/reconcile/layout) | slider scrub, scroll, progress, hover glow |
| Granular re-render (`UseState`/`UseSignal`) | owning component's subtree | render + reconcile + scoped relayout of that subtree | normal state, value-displaying text |
| Reactive control-flow (`Flow.For`/`Flow.Show`) | one boundary effect → keyed diff | structural reconcile of that boundary | dynamic lists / conditionals |

## Rules (these prevent ~90% of bugs)

1. To make something update, a signal it **reads** must change. `.Value` subscribes; `.Peek()` does not.
2. `Component.Render()` re-runs on its **own** state/context only — never because a parent re-rendered.
   **Parent→child data flows via signals or context, never constructor args** (those freeze at mount).
3. `ReactiveComponent.Setup()` runs **once**. Show dynamic values via a **binding** (`TextBind = () => sig.Value`),
   never `Ui.Text(sig.Value)`. (#1 signals-native mistake.)
4. A bind thunk must read `.Value` (subscribes), not `.Peek()`.
5. `TransformBind`/`OpacityBind`/`FillBind` = compositor-only. `WidthBind`/`HeightBind`/`TextBind` = scoped relayout.
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

| Area | File |
|---|---|
| Signals runtime | `src/FluentGpu.Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs` |
| Hooks | `src/FluentGpu.Hooks/RenderContext.cs` (impl) + `Component.cs` (surface) |
| Reconcile / render-effects / For/Show / bindings / context | `src/FluentGpu.Reconciler/Reconciler.cs` |
| Element shapes / props / binds | `src/FluentGpu.Dsl/Element.cs`, `ControlFlow.cs`, `Context.cs`, `ComponentEl.cs` |
| DSL helpers / modifiers | `src/FluentGpu.Dsl/Factories.cs`, `Modifiers.cs` |
| Controls | `src/FluentGpu.Controls/*.cs` (composition only) |
| Frame loop / scheduling | `src/FluentGpu.Hosting/AppHost.cs` (`RunFrame`/`Paint`; flush = phase 3) |
| Layout / scoped relayout | `src/FluentGpu.Layout/FlexLayout.cs`, `LayoutInvalidator.cs` |
| Retained scene (SoA, dirty flags) | `src/FluentGpu.Scene/{SceneStore,Columns}.cs` |
| Record → DrawList | `src/FluentGpu.Render/SceneRecorder.cs` |
| Theming tokens | `src/FluentGpu.Dsl/Tokens.cs` (`Tok`), `Theme.cs` |
| Tests | `src/FluentGpu.VerticalSlice/Program.cs` |

Design corpus (architecture authority, canon-gated) is `design/`; as-built reactive model is
`design/subsystems/reconciler-hooks.md §0bis`. After editing `design/*`: `powershell -File design/check-canon.ps1`
(must exit 0). Usage docs go in `docs/`, not `design/`.

## Deeper docs (read for the relevant task)
- `docs/guide/reactivity.md` — signals, hooks, `Component` vs `ReactiveComponent`, bindings, context (the core).
- `docs/guide/components-elements-layout.md` — element zoo, layout, controls, navigation, virtualization, theming.
- `docs/guide/rendering-and-performance.md` — frame pipeline, scoped relayout + boundary firewall, optimization guide.
- `docs/guide/pitfalls.md` — symptom → cause → fix.
