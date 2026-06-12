# AGENTS.md — FluentGpu

Guidance for AI agents (Codex, and others) working in this repo. Claude Code users also have `CLAUDE.md` (design-corpus
discipline) and a `.claude/skills/fluentgpu` skill; the full human/agent guide is in `docs/guide/`.

## What this is

FluentGpu is a from-scratch, near-zero-allocation, NativeAOT, Direct3D 12-rendered UI engine for .NET 10. The
authoring model is React/Reactor (`Element` records + `Component` + hooks) on a **signals-first (Solid-style) reactive
core**. Immutable elements are reconciled into a retained struct-of-arrays scene; a recorder walks it into a GPU
command list each frame. Updates are surgical — **no full-app re-render, no global dirty flag.**

## Build & test (use these exact commands)

There is no single solution build you should rely on; build/run the targets you need.

```bash
# Build + run the headless golden-check harness — THE verification gate (do this after every engine change):
dotnet build src/FluentGpu.VerticalSlice
dotnet run   --project src/FluentGpu.VerticalSlice      # expect: "ALL CHECKS PASSED" (~60 cross-seam checks)

# Build the real Windows app:
dotnet build src/FluentGpu.WindowsApp

# Design-corpus drift gate (only if you edited design/*.md), Windows PowerShell:
powershell -File design/check-canon.ps1                 # must exit 0
```

The harness runs headlessly (no GPU/window), is deterministic, and asserts on the recorded DrawList + post-layout
scene — not pixels. GPU pixel correctness is a separate manual check on the real D3D12 path. **Do not claim a change
works without running the harness and seeing `ALL CHECKS PASSED`.**

## The mental model (read before editing)

A change reaches pixels through **one mechanism: a signal**. Reading a signal (`.Value`) subscribes the current
reactive computation; writing it re-runs only the computations that read it. A component's render is one such
computation; a property *binding* is a finer one. Three update paths, cheapest first:

- **Binding** (`Transform`/`Opacity`/`Fill` set to a Func/signal): one effect → one scene-node prop, **compositor-only** (no
  render/reconcile/layout). For high-frequency scalars (slider, scroll, progress, hover).
- **Granular re-render** (`UseState`/`UseSignal`): re-renders the owning component's subtree + a **scoped** relayout.
- **Reactive control-flow** (`Flow.For`/`Flow.Show`): keyed diff of one boundary's children, no parent re-render.

## Rules that prevent most bugs

1. To make something update, a signal it **reads** must change. `.Value` subscribes; `.Peek()` does not.
2. `Component.Render()` re-runs on its **own** state/context only. **Parent→child data flows via signals or context,
   never constructor args** — constructor values freeze at mount (the factory is not re-invoked on re-render).
3. `ReactiveComponent.Setup()` runs **once**; show dynamic values via a bound prop (`Text = sig` signal-direct, or `Text = Prop.Of(() => …)` for derived text), not
   `Ui.Text(sig.Value)`.
4. Bind thunks must read `.Value`, not `.Peek()`.
5. Every bindable channel is ONE `Prop<T>` prop (value / `Func<T>` via `Prop.Of` / concrete signal). Bound `Transform`/`Opacity`/`Fill` are compositor-only; bound `Width`/`Height`/`Text` trigger scoped
   relayout. Prefer a transform bind for hot values.
6. Never write a signal during render (it loops). Use an event handler or `UseEffect`.
7. Hooks run in stable call order — no hooks inside `if`/loops.
8. Scoped relayout needs a **boundary**: fixed `Width`+`Height` + `ClipToBounds=true` stops relayout escaping to the
   page. Give cards/panes/rows a boundary.
9. **Zero managed allocation in paint phases 6–13.** Wire bindings/effects once at mount; no `new`/box/LINQ/per-call
   closures in a bind thunk or hot effect. The harness asserts `FrameStats.HotPhaseAllocBytes == 0` on steady frames.
10. High-frequency scalar? Bind it (`Slider.Bind(FloatSignal)`), don't `setState` per move.

## Minimal example

```csharp
using static FluentGpu.Dsl.Ui;
using FluentGpu.Hooks; using FluentGpu.Controls;

FluentApp.Run(() => new App());

sealed class App : Component {
    public override Element Render() {
        var (n, setN) = UseState(0);
        return VStack(12, Heading("Hi"), Text($"n={n}"), Button.Accent("+", () => setN(n + 1)));
    }
}
```

## Where to change what

| Area | File(s) |
|---|---|
| Signals runtime | `src/FluentGpu.Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs` |
| Hooks | `src/FluentGpu.Hooks/RenderContext.cs` (impl), `Component.cs` (surface) |
| Reconcile / render-effects / For/Show / bindings / context | `src/FluentGpu.Reconciler/Reconciler.cs` |
| Element shapes / props / binds | `src/FluentGpu.Dsl/Element.cs`, `ControlFlow.cs`, `Context.cs`, `ComponentEl.cs` |
| DSL helpers / modifiers | `src/FluentGpu.Dsl/Factories.cs`, `Modifiers.cs` |
| Controls | `src/FluentGpu.Controls/*.cs` (composition only — no new opcodes/columns) |
| Frame loop / scheduling / compositor frame | `src/FluentGpu.Hosting/AppHost.cs` |
| Layout / scoped relayout | `src/FluentGpu.Layout/FlexLayout.cs`, `LayoutInvalidator.cs` |
| Retained scene (SoA, dirty flags, side-tables) | `src/FluentGpu.Scene/{SceneStore,Columns}.cs` |
| Record → DrawList | `src/FluentGpu.Render/SceneRecorder.cs` |
| Theming tokens | `src/FluentGpu.Dsl/Tokens.cs` (`Tok`), `Theme.cs` |
| Tests / golden checks | `src/FluentGpu.VerticalSlice/Program.cs` |

Adding an `Element` type: assign a free `ElementTypeId`, then handle it in `Reconciler.Mount`/`Update` (and
`ChildrenOf` if it has children).

## Conventions

- .NET 10, C# 14, nullable enabled, `unsafe` allowed, NativeAOT-targeted. No reflection on hot paths; keep new
  code AOT-clean (delegates over reflection). Match the terse, XML-doc-commented style of nearby files.
- Commit/push only when asked. Don't commit build output or diagnostics dumps (`*.dmp`/`*.gcdump` are gitignored).
- The `design/` tree is the canon-gated architecture authority; usage docs live in `docs/`. As-built reactive model:
  `design/subsystems/reconciler-hooks.md §0bis`.

## Deeper docs
`docs/guide/README.md` (hub + file map) → `reactivity.md` (the core) → `components-elements-layout.md` →
`rendering-and-performance.md` → `pitfalls.md` (symptom → cause → fix).
