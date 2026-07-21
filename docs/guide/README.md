# FluentGpu — Developer & Agent Guide

> **✅ Animation engine — signals-first rework landed + verified.** The animation engine is one signals-first model: one POD `AnimValue` slab + one `AnimScheduler`/`CadenceClass` + an analytical closed-form spring + a declarative orchestration layer (`Transition`/`While*`/`Enter`/`Exit`/`Stagger`/`Layout` + `UseSpringValue`/`UseAnimatedValue`; reduced-motion-as-a-value). The scheduler class is still named `AnimEngine`, so `AnimEngine` references in this guide stay valid; the old `AnimTrack`/sub-stepped-Euler engine is gone. Verified: 521 VerticalSlice gates green (zero-alloc on phases 6–13 + dt-determinism) and the gallery renders end-to-end. Design, now implemented: [`../plans/animation-engine-rework-design.md`](../plans/animation-engine-rework-design.md) (research: [`../plans/animation-engine-research-dossier.md`](../plans/animation-engine-research-dossier.md)).

A from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI framework for .NET 10. React/Reactor
programming model (immutable `Element` records + `Component` + hooks) on a **signals-first** reactive core, over a
custom Direct3D 12 + DirectWrite renderer behind a swappable seam (headless backends for tests/CI).

This guide is written for **two audiences at once**: human developers building apps, and AI agents editing the
codebase. Sections marked **🤖 AGENT** are rules/maps/tables optimized for fast, correct machine use. Everything
here is grounded in the real API — file paths are clickable anchors to the source of truth.

---

## Read this first: the one mental model

> **A change to state flows to pixels through exactly one mechanism: a *signal*.** Reading a signal *subscribes*
> the current reactive computation; writing a signal *re-runs* only the computations that read it. A component's
> render is one such computation (coarse-grained); a property *binding* is another (fine-grained). There is **no
> full-app re-render** and **no global dirty flag** — updates are surgical.

Three ways an update reaches the screen, cheapest first:

| Mechanism | What re-runs | Cost | Use for |
|---|---|---|---|
| **Binding** (`Transform`/`Opacity`/`Fill` set to a Func/signal) | one effect → one scene node prop | compositor-only: **no render, no reconcile, no layout** | high-frequency scalars: slider scrub, scroll, progress, hover glow |
| **Granular re-render** (`UseState`/`UseSignal`) | the owning component's subtree | render + reconcile that subtree + **scoped** relayout | normal UI state, text that shows a value |
| **Reactive control-flow** (`Flow.For`/`Flow.Show`) | one boundary effect → keyed diff of its children | structural reconcile of that boundary only | dynamic lists / conditionals |

The whole framework is the corollaries of that model. If you internalize the table above, most pitfalls disappear.

---

## 60-second cheat sheet

```csharp
using static FluentGpu.Dsl.Ui;          // VStack, HStack, Text, Heading, Button, Image, Grid, ScrollView…
using FluentGpu.Hooks;                   // Component, Embed, Ctx, Flow
using FluentGpu.Signals;                 // Signal<T>, FloatSignal, Memo<T>
using FluentGpu.Controls;                // Button, Slider, IconButton, NavigationView, Virtual…

// Classic component — re-renders its OWN subtree when its state changes (granular).
sealed class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);                  // read `count` => subscribe; setCount => re-render this component
        return VStack(gap: 8,
            Text($"Count: {count}"),
            Button.Accent("Increment", () => setCount(count + 1)));
    }
}

// Hot path — a signal bound straight to a node transform: NO re-render on drag.
sealed class Volume : Component
{
    public override Element Render()
    {
        var vol = UseFloatSignal(0.5f);                       // persistent scalar signal
        return Slider.Create(vol);                            // drag => vol.Value = x => thumb/fill transform only
    }
}

// Embed a child component; provide/read context; reactive list + conditional.
VStack(gap: 8,
    Embed.Comp(() => new Counter()),
    Ctx.Provide(MyTheme, "dark", Embed.Comp(() => new Child())),
    Flow.For(() => items.Value, x => x.Id, (x, i) => Row(x)),
    Flow.Show(() => open.Value, panel, fallback));
```

Run it (host wiring): see **[getting-started.md](./getting-started.md)**.

---

## 🤖 AGENT — the rules that prevent 90% of bugs

1. **To make something update on screen, a signal it reads must change.** If you set a `Signal`/`UseState` and nothing
   moves, the renderer that should react never *read* it. (Reading `.Peek()` does **not** subscribe; reading `.Value`
   does.)
2. **`Component.Render()` re-runs on its own state/context changes only** — never because a parent re-rendered.
   Parent→child data flows through **signals or context**, *not* through the component's constructor args (those are
   captured once at mount and frozen). See [reactivity.md#props](./reactivity.md#props-dont-flow-through-constructors).
3. **A `Component` whose `Render()` reads no signals runs exactly once.** (Run-once is inferred — there is one
   component model, no `ReactiveComponent`.) Reading `signal.Value` in such a render is a one-time read. To show a
   changing value without re-rendering, use a **bound prop** (`Text = sig`, or `Text = Prop.Of(() => …)` for derived
   text), never `Text(sig.Value.ToString())`. This is the #1 mistake. See [pitfalls.md](./pitfalls.md).
4. **Binding thunks must read `.Value` (not `.Peek()`).** The thunk runs inside the binding effect; `.Value`
   subscribes it so it re-runs. `.Peek()` would wire a binding that never updates.
5. **Bound `Transform`/`Opacity`/`Fill` are compositor-only (no layout).** Bound `Width`/`Height`/`Text`
   trigger a **scoped** relayout. Don't drive a high-frequency value through a layout bind if a transform will do.
6. **Don't put a `setState` in render.** A signal write during render re-schedules the same render → loops. Put it in
   an event handler or `UseEffect`.
7. **Hooks run in stable call order** (no hooks inside `if`/loops) — same rule as React. Cells are slot-indexed.
8. **The scoped-relayout firewall needs a boundary.** A fixed-size, `ClipToBounds=true`, non-flexing container stops
   relayout from escaping. Give big containers (cards, panes) explicit `Width`+`Height`+`ClipToBounds` so a deep
   change can't relayout the page. See [rendering-and-performance.md](./rendering-and-performance.md#scoped-relayout).
9. **Per-frame allocation is forbidden in the paint half (phases 6–13).** Wire bindings/effects once at mount; never
   allocate in a binding thunk or a hot effect body. The harness asserts `HotPhaseAllocBytes == 0` on steady frames.
10. **Verify with the headless harness, not by eye:** `dotnet run --project src/FluentGpu.VerticalSlice`. It exercises
    every seam and prints PASS/FAIL. GPU pixels are not asserted here.

## 🤖 AGENT — file & ownership map (where to change what)

| If you're changing… | Edit | Notes |
|---|---|---|
| The reactive runtime (signals, effects, scheduler) | `src/FluentGpu.Engine/Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs` | AOT-clean; set→notify must stay alloc-free |
| Hooks (`UseState`/`UseSignal`/`UseContext`/…) | `src/FluentGpu.Engine/Hooks/RenderContext.cs` (impl) + `Component.cs` (surface) | stable call-order; hook-cell zoo |
| Component model (`Component`) | `src/FluentGpu.Engine/Hooks/Component.cs` | one base; every render is tracked (run-once is inferred — reads no signals ⇒ never re-runs) |
| Reconcile, render-effects, `For`/`Show`, context, bindings | `src/FluentGpu.Engine/Reconciler/Reconciler.cs` | the heart; render-effects + keyed `ReconcileChildren` |
| Element shapes / props / bindings | `src/FluentGpu.Engine/Dsl/Element.cs`, `ControlFlow.cs`, `Context.cs`, `ComponentEl.cs` | add a free `ElementTypeId`; wire in reconciler `Mount`/`Update` |
| DSL helpers (`Ui.*`) / modifiers | `src/FluentGpu.Engine/Dsl/Factories.cs`, `Modifiers.cs` | pure element builders |
| Controls (Button/Slider/Nav/Virtual…) | `src/FluentGpu.Controls/*.cs` | composition only — no new opcodes/columns. WinUI fidelity: **[control-fidelity.md](./control-fidelity.md)** |
| Control visual state / interaction motion | `StateBrush` ramps + the `HoverFade`/`PressFade` side-table channels (the unified slab; no separate `InteractionAnimator`); `BoxEl.{Hover,Pressed}{Fill,BorderColor,Opacity}` + `{Hover,Press}Scale` + `{Hover,Press}DurationMs/Easing` | model logical state x interaction state, NOT a 12-state matrix; child parts can inherit the clickable ancestor's progress |
| Explicit control timelines | `AnimEngine` (the slab scheduler) keyframes/channels (`Opacity`, transform, stroke trim, FLIP/reveal) + enter/exit presets in `ControlMotion` | use for authored WinUI timelines, draw-on paths, and true insert/remove parts; not for ordinary hover/press |
| Rounded-rect / border rendering | `src/FluentGpu.Engine/Render/SceneRecorder.cs` + `src/FluentGpu.Windows/D3D12/{RoundRect,Gradient}Pipeline.cs` | hollow SDF ring (no donut); `InsetCorners`; quad inflation for stroke band + AA |
| Frame loop, scheduling, compositor frame | `src/FluentGpu.Engine/Hosting/AppHost.cs` | `RunFrame`/`Paint`; `_runtime.Flush()` is phase 3 |
| Layout (flex/grid/measure) | `src/FluentGpu.Engine/Layout/FlexLayout.cs` | `Run` (full) vs `RunSubtree` (scoped) |
| Scoped relayout / boundary firewall | `src/FluentGpu.Engine/Layout/LayoutInvalidator.cs` + `SceneStore` LayoutDirty worklist | up-rule walk to boundary |
| Retained scene (SoA tree, columns, dirty flags) | `src/FluentGpu.Engine/Scene/{SceneStore,Columns}.cs` | handle = `{index, gen}`; side-tables by node index |
| Record → DrawList (the GPU command walk) | `src/FluentGpu.Engine/Render/SceneRecorder.cs` | composites transform/opacity without re-record |
| Theming tokens / colors | `src/FluentGpu.Engine/Dsl/Tokens.cs` (`Tok`), `Theme.cs` | `Tok.Use(ThemeKind)` re-themes in one pointer write |
| Tests / golden checks | `src/FluentGpu.VerticalSlice/Program.cs` | add a `Check(...)`; call it from `Main` |

**Design corpus** (architecture source-of-truth, canon-gated) lives in `docs/design/`. The as-built reactive model is
`docs/design/subsystems/reconciler-hooks.md §0bis`. After editing `docs/design/*`, run `powershell -File docs/design/check-canon.ps1`.

---

## Guide index

1. **[getting-started.md](./getting-started.md)** — minimal app, hosting, the frame loop you call, project layout.
2. **[reactivity.md](./reactivity.md)** — signals, the update model, `UseState`/`UseSignal`/`UseComputed`, effects,
   the one `Component` model (run-once is inferred), bindings, `For`/`Show`, context. *The most important doc.*
3. **[components-elements-layout.md](./components-elements-layout.md)** — hooks reference, the element zoo, flexbox &
   grid, modifiers, controls, navigation, virtualization, theming.
4. **[rendering-and-performance.md](./rendering-and-performance.md)** — the frame pipeline, the SoA scene, reconcile,
   scoped relayout + the boundary firewall, the compositor bypass, zero-alloc, an optimization decision guide.
5. **[pitfalls.md](./pitfalls.md)** — common mistakes as **symptom → cause → fix** (read before debugging).
6. **[control-fidelity.md](./control-fidelity.md)** — building WinUI-faithful controls: where to find the exact WinUI
   templates/storyboards/timing tokens, the logical-state x interaction-state graph, `StateBrush` visual states (the
   `HoverFade`/`PressFade` slab channels), `AnimEngine` authored timelines, and the empirical verify workflow (golden
   checks + `--shot` + slow-motion proof). *Read before the control parity sweep.*
7. **[winui-control-parity-audit.md](./winui-control-parity-audit.md)** - source-backed diff of every FluentGpu
   control against `C:\WAVEE\microsoft-ui-xaml`, including XAML templates, C++ behavior, generated properties, and
   engine blockers.
8. **[motion-recipes.md](./motion-recipes.md)** — the **Expressive Motion Kit**: named transitions adopted from
   transitions.dev (number pop-in, error shake, skeleton reveal, success check, …) on the engine's springs + the new
   per-node self-blur + the expressive curve/token vocabulary. An opt-in app-author palette (controls keep Fluent curves).
9. **[skeleton-loading.md](./skeleton-loading.md)** — native **skeleton/shimmer-while-loading**: ONE UI source, the
   framework derives the shimmer from it, keeps partial-known parts real, shimmers the pending region, and blur-reveal
   swaps to real on load. `Loadable<T>` + `Skel.Region` + `UseResource` (SWR); incremental per-field, onFailed, groups.
10. **[professional-media-lab.md](./professional-media-lab.md)** — the public DASH/HLS/PlayReady fixture catalog,
    behavior-coverage matrix, live diagnostics and on-device media verification pass.
11. **[localizing-the-control-kit.md](./localizing-the-control-kit.md)** — the control kit is a **localizable SDK**:
    the neutral-fallback floor (zero-config = neutral English unchanged), the JSON-source-of-truth + generated
    `Strings` keys + `Loc.Bind`/`Loc.Get`, how an app ships translations, the pseudo-locale QA pass, the CultureInfo-
    derived vs shipped-string split, and the `FGRP008` no-hardcoded-string analyzer.

---

## What this framework is (and isn't)

- **Is:** a retained-mode, GPU-composited UI engine. The `Element` tree is a *description* (cheap, immutable); a
  reconciler patches it into a retained SoA scene; a recorder walks the scene into a GPU command list each frame.
  Transform/opacity changes animate by re-recording cheap world transforms — no relayout, no re-diff.
- **Isn't:** an immediate-mode UI, and not "zero allocation" everywhere — it's **near-zero-allocation**: zero managed
  allocation in the per-frame paint phases (6–13), bounded Gen0 at the reconcile/edge. Slowness is *decoupled, not
  invincible* — a sustained GPU stall still bounds back to the UI thread.
- **Programming model:** Reactor/React (components + hooks) you already know, with a Solid-style signals core
  underneath so the default is fast. One `Component` base: stay in the familiar `Component`/`UseState` style (granular
  re-render) or go fully signals-native (a `Render()` that reads no signals + bindings) for the hottest paths — same
  class, run-once is inferred.
