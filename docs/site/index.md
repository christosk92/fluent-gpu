# FluentGpu

A from-scratch, **near-zero-allocation, NativeAOT, GPU-rendered** UI framework for .NET 10. You write UI the way
[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor) does — immutable `Element` records assembled
with a fluent C# DSL, rendered by `Component.Render()`, with co-located hooks and a keyed reconciler — but everything
underneath is ours: a **signals-first** (Solid-style) reactive core that patches a retained struct-of-arrays render
tree, painted on **Direct3D 12 + DirectWrite + DirectComposition** (pure Win32, no WinRT / Windows App SDK), behind a
swappable seam so a macOS backend can drop in later.

> **Status (June 2026): the engine is built and runs.** The reactive core, keyed reconciler, flex/grid layout, the
> control kit, virtualization (10k+ lists), the async image pipeline, theming + Mica, and animation all pass **60+
> end-to-end golden checks** on the headless backends — including a full Wavee-shell acceptance test. The real Windows
> path is wired via `FluentApp.Run`; on-screen pixel polish is the ongoing edge. See [Honest status](#honest-status)
> below.

This is the documentation **hub**. There are two readers here and the site serves both: people **building apps** with
FluentGpu, and people **changing the engine internals**. Pick your path under
[Two paths](#two-paths-app-authors--engine-contributors).

---

## What it is (and isn't)

- **Is:** a **retained-mode, GPU-composited** UI engine. The `Element` tree is a *description* — cheap, immutable; a
  reconciler patches it into a retained SoA scene; a recorder walks that scene into a GPU command list each frame.
  Transform/opacity changes animate by re-recording cheap world transforms — **no relayout, no re-diff**.
- **Isn't:** an immediate-mode UI, and **not "zero allocation" everywhere.** It's **near-zero-allocation**: zero managed
  allocation in the per-frame paint phases (6–13), with bounded Gen0 at the reconcile/edge. Slowness is *decoupled, not
  invincible* — a sustained GPU stall still bounds back to the UI thread.
- **Programming model:** Reactor/React (components + hooks) you already know, with a signals core underneath so the
  default is fast. Stay in the familiar `Component` / `UseState` style (now granular) or go fully signals-native
  (`ReactiveComponent` + bindings) for the hottest paths.

Why bother? WinUI 3 is slow in ways that are *structural*, not tunable — boxed dependency properties, a finalizable COM
object per control, doubled visual+logical trees, single-threaded layout/composition, and a `setState` that fans out to
a broad re-render. FluentGpu attacks the *causes*. The honest scorecard lives in the design corpus
([`winui-painpoints-assessment.md`](../../design/winui-painpoints-assessment.md)); the short version is on the
[repository README](../../README.md).

---

## The one mental model: every update flows through a signal

If you internalize one thing, make it this. **A change to state reaches pixels through exactly one mechanism: a
*signal*.** Reading a signal *subscribes* the current reactive computation; writing a signal *re-runs* only the
computations that read it. A component's render is one such computation (coarse-grained); a property *binding* is another
(fine-grained). There is **no full-app re-render** and **no global dirty flag** — updates are surgical.

There are three ways an update reaches the screen, **cheapest first**:

| Mechanism | What re-runs | Cost | Use for |
|---|---|---|---|
| **Binding** — a `Prop<T>` channel (`Transform` / `Opacity` / `Fill`) set to a `Func<T>` or signal | one effect → one scene-node prop | **compositor-only**: no render, no reconcile, no layout | high-frequency scalars: slider scrub, scroll, progress, hover glow |
| **Granular re-render** — `UseState` / `UseSignal` | the owning component's subtree | render + reconcile that subtree + **scoped** relayout | normal UI state, text that shows a value |
| **Reactive control-flow** — `Flow.For` / `Flow.Show` | one boundary effect → keyed diff of its children | structural reconcile of that boundary only | dynamic lists / conditionals |

Most pitfalls are corollaries of that table. The two that bite first:

- **To make something update on screen, a signal it reads must change.** If you write a signal and nothing moves, the
  thing that should react never *read* it. Reading `.Value` subscribes; reading `.Peek()` does not.
- **Parent → child data flows through signals or context — never constructor args**, which are captured once at mount
  and frozen. Pass a `Signal<T>` (or use context) and have the child read it.

The full treatment — `Component` vs `ReactiveComponent`, bindings, `For`/`Show`, context, the scoped-relayout firewall —
is in the guide's [reactivity page](../guide/reactivity.md), the single most important doc to read.

---

## Two paths: App Authors / Engine Contributors

### For app authors

Building an app is **one call**. `FluentApp.Run(() => new App())` brings up a DPI-aware window, D3D12, Mica + the OS
accent, the font and image systems, and the frame loop — then renders your root `Component`:

```csharp
using FluentGpu;                 // FluentApp
using FluentGpu.Hooks;           // Component, UseState
using static FluentGpu.Dsl.Ui;   // VStack, Heading, Text…
using FluentGpu.Controls;        // Button…

FluentApp.Run(() => new App());

sealed class App : Component
{
    public override Element Render()
    {
        var (n, setN) = UseState(0);                          // reading `n` subscribes THIS component
        return VStack(gap: 12,
            Heading("Hello, FluentGpu"),
            Text($"Clicked {n} times"),
            Button.Accent("Click me", () => setN(n + 1)));    // re-renders only App's subtree
    }
}
```

The signature (`src/FluentGpu.WindowsApp/FluentApp.cs`):

```csharp
public static void Run(Func<Component> root, string title = "FluentGpu",
                       int width = 800, int height = 600, bool mica = true, int frames = -1,
                       string? screenshot = null, bool customFrame = false);
public static void Run<T>(string title = "FluentGpu", int width = 800, int height = 600) where T : Component, new();
```

For a value that changes many times per second — a slider drag, scroll, a progress bar — **bind it** instead of calling
`setState`. A bound channel updates the scene node's transform/paint directly, with no render, reconcile, or layout:

```csharp
using FluentGpu.Signals;   // Signal<T>, FloatSignal, Prop, Prop.Of

var vol = UseFloatSignal(0.5f);
Slider.Bind(vol);          // a drag sets vol.Value = x → thumb/fill transform only
```

When you bind a channel yourself, the thunk must read `.Value` (which subscribes the binding effect) — not `.Peek()`:

```csharp
// derived bind: read .Value inside the thunk so the binding re-runs when the signal changes
new BoxEl { Transform = Prop.Of(() => Affine2D.Translation(vol.Value * 100f, 0f)) };
```

`Prop.Of(...)` is required only for an **inline lambda** (C# cannot chain a lambda conversion into a user conversion); a
concrete signal assigns directly — `Text = sig`. The `Prop<T>` type lives in `src/FluentGpu.Foundation/Signals/Prop.cs`.

**Start here, in order:**

1. [Getting started](../guide/getting-started.md) — the minimal app, the frame loop you call, running headless, the
   verification harness, the assembly layout.
2. [Reactivity](../guide/reactivity.md) — signals, the update model, `UseState`/`UseSignal`/`UseComputed`, effects,
   `Component` vs `ReactiveComponent`, bindings, `For`/`Show`, context. **The most important doc.**
3. [Components, elements & layout](../guide/components-elements-layout.md) — the hooks reference, the element zoo,
   flexbox & grid, modifiers, controls, navigation, virtualization, theming.
4. [Rendering & performance](../guide/rendering-and-performance.md) — the frame pipeline, the SoA scene, reconcile,
   scoped relayout + the boundary firewall, the compositor bypass, zero-alloc, an optimization decision guide.
5. [Pitfalls](../guide/pitfalls.md) — common mistakes as **symptom → cause → fix**. Read it before you debug.

The two mistakes new authors hit most (both in [Pitfalls](../guide/pitfalls.md)): a `ReactiveComponent.Setup()` reads
`sig.Value` once and then shows a stale value forever — fix it with a **bound prop** (`Text = sig`, or
`Text = Prop.Of(() => …)`); and a slider dragged through `setState` tanks FPS — fix it by **binding** the value and
confirming `FrameStats.Rendered == false` during the drag.

### For engine contributors

Changing internals means editing **one owner** and verifying against the headless harness. FluentGpu enforces a strict
single-owner model: every shared artifact (an opcode, a column, a seam, a hook) is defined in exactly one place and
referenced everywhere else. Honor it — the #1 review finding is the same artifact defined two ways in two docs.

**Where to change what** (the full map is in the guide's [hub, "file & ownership map"](../guide/README.md), and the
canonical contract owners are in [`SPEC-INDEX.md`](../../design/SPEC-INDEX.md)):

| If you're changing… | Edit |
|---|---|
| The reactive runtime (signals, effects, scheduler) | `src/FluentGpu.Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs` |
| The unified bindable channel (`Prop<T>`) | `src/FluentGpu.Foundation/Signals/Prop.cs` |
| Hooks (`UseState`/`UseSignal`/`UseContext`/…) | `src/FluentGpu.Hooks/RenderContext.cs` + `Component.cs` |
| Reconcile, render-effects, `For`/`Show`, context, bindings | `src/FluentGpu.Reconciler/Reconciler.cs` |
| Element shapes / props | `src/FluentGpu.Dsl/{Element,ControlFlow,Context,ComponentEl}.cs` |
| DSL helpers (`Ui.*`) / modifiers | `src/FluentGpu.Dsl/{Factories,Modifiers}.cs` |
| Controls (Button/Slider/Nav/Virtual…) | `src/FluentGpu.Controls/*.cs` (composition only — no new opcodes/columns) |
| Rounded-rect / border rendering | `src/FluentGpu.Render/SceneRecorder.cs` + `src/FluentGpu.Rhi.D3D12/*Pipeline.cs` |
| Frame loop, scheduling | `src/FluentGpu.Hosting/AppHost.cs` (`RunFrame`/`Paint`) |
| Layout (flex/grid/measure) | `src/FluentGpu.Layout/{FlexLayout,LayoutInvalidator}.cs` |
| Retained scene (SoA tree, columns, dirty flags) | `src/FluentGpu.Scene/{SceneStore,Columns}.cs` |
| Theming tokens / colors | `src/FluentGpu.Dsl/{Tokens,Theme}.cs` |
| Tests / golden checks | `src/FluentGpu.VerticalSlice/Program.cs` |

**Verify with the headless harness, not by eye.** It is the single source of truth for "does the engine still work" —
~60 cross-seam golden checks, no GPU or window needed:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

Add a check by writing a `Check("…", condition, detail)` in `src/FluentGpu.VerticalSlice/Program.cs` and calling it from
`Main`. If you touched reactivity/layout/render, assert on `FrameStats` — `Rendered`, `ComponentsRendered`, and
`HotPhaseAllocBytes` (which **must be 0** on steady frames) — for the interaction you changed. GPU pixels are *not*
asserted by the harness; that is a separate manual "needs-pixels" pass on the real D3D12 path.

If you edit the design corpus under `design/`, run the canon gate afterward (it fails the build if a superseded token
reappears in the live tree):

```powershell
powershell -File design/check-canon.ps1                  # exit 0 = clean
```

Deeper reading: the guide's [rendering & performance page](../guide/rendering-and-performance.md) for the frame
pipeline as-built, and the design corpus for architecture — start at [`design/README.md`](../../design/README.md), the
[subsystem index + ownership map](../../design/subsystems/README.md), and
[`reconciler-hooks.md`](../../design/subsystems/reconciler-hooks.md) for the as-built signals model (`§0bis`).

---

## API reference & design canon

- **Developer & agent guide** — [`docs/guide/`](../guide/README.md). Task-oriented, source-grounded, the same doc
  serves humans and AI agents (sections marked **🤖 AGENT** are machine-optimized rules and maps).
- **Design corpus** — [`design/`](../../design/README.md). The architecture source-of-truth, canon-gated.
  [`SPEC-INDEX.md`](../../design/SPEC-INDEX.md) is the **precedence authority** (when two docs disagree, it names the one
  owning doc and the current canonical value); [`subsystems/README.md`](../../design/subsystems/README.md) is the
  contract-ownership map; [`architecture-spec.md`](../../design/architecture-spec.md) is the end-to-end engine.
- **The key types**, in one place:
  - `FluentApp.Run` — `src/FluentGpu.WindowsApp/FluentApp.cs`. The batteries-included entry point.
  - `Component` (and `ReactiveComponent`) — `src/FluentGpu.Hooks/Component.cs`. Override `Render()`; it re-runs on its
    own state/context changes only.
  - `Signal<T>` — `src/FluentGpu.Foundation/Signals/Signal.cs` (namespace `FluentGpu.Signals`). The unit of state:
    `.Value` reads **and subscribes** the current computation; `.Peek()` reads without subscribing; setting `.Value`
    notifies subscribers.
  - `Prop<T>` / `Prop.Of` — `src/FluentGpu.Foundation/Signals/Prop.cs`. One unified bindable channel per property,
    accepting a static `T`, a `Func<T>` thunk, or a concrete signal.

> Note: `Signal<T>` and `Prop<T>` are authored from the namespace `FluentGpu.Signals` (the `using FluentGpu.Signals;`
> cheat-sheet import), even though their files live under the `FluentGpu.Foundation` assembly's `Signals/` folder.

---

## Honest status

FluentGpu's docs deliberately don't overclaim, and these are **truths, not gaps to be fixed**:

- **Near-zero-allocation** — *not* "zero". Zero managed allocation only in the per-frame paint phases (6–13); bounded
  Gen0 at the reconcile/render edge. The harness asserts `HotPhaseAllocBytes == 0` on steady frames.
- **Retained-mode & composited** — the `Element` tree is a description; a reconciler patches a retained SoA scene; a
  recorder walks it to a GPU command list. Transform/opacity animate by re-recording world transforms (no relayout, no
  re-diff). This is *not* immediate-mode.
- **Decoupled, not invincible** — fine-grained signals + the compositor bypass keep updates surgical, but a sustained
  GPU stall still bounds back to the UI thread. The engine reduces the *causes* of jank; it does not abolish physics.
- **Production safety == CI coverage** — the debug guards (thread guards, the alloc tripwire, COM/leak gates) are
  `[Conditional]`-erased from the shipping NativeAOT binary. A hazard not covered by a green gate is unguarded at
  runtime. The build order ships single-thread-correct first, then flips parallelism behind a green race gate.

The current edge is on-screen D3D12 pixel polish (the logic is verified headlessly; GPU pixels are a separate manual
pass), the source generators, and the hardening spine. The honest, per-painpoint grading is in
[`winui-painpoints-assessment.md`](../../design/winui-painpoints-assessment.md).

---

**Next:** app authors → [Getting started](../guide/getting-started.md); engine contributors →
[the guide hub's file & ownership map](../guide/README.md) and [`SPEC-INDEX.md`](../../design/SPEC-INDEX.md).
