# Contributing to the engine

This is the entry point for **engine contributors** — you're changing FluentGpu's internals (the reconciler, layout,
the scene store, the renderer, the reactive core), not building an app *with* it. If you're here to ship a UI, start at
[Building apps with FluentGpu](../app-authors/index.md) instead.

FluentGpu is a from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI engine for .NET 10: a React/Reactor
authoring surface (immutable `Element` records + `Component` + hooks) over a **signals-first** (Solid-style) reactive
core, patching a retained struct-of-arrays scene that a recorder walks into a GPU command list each frame. Before you
touch anything, internalize the single mechanism the whole engine is built on — it is the same rule app authors learn,
seen from below:

> **Every state change reaches pixels through exactly one mechanism: a *signal*.** A reactive computation that *reads* a
> signal subscribes to it; *writing* the signal re-runs only the computations that read it. A component's render is one
> such computation (subtree granularity); a property binding is another (single scene-node granularity). There is **no
> full-app re-render** and **no global dirty flag** — the engine's job is to keep those re-runs surgical.

Internalize that and the architecture stops being a pile of subsystems and becomes one idea with a data substrate under
it. The reactive core's own header (`src/FluentGpu.Engine/Foundation/Signals/ReactiveCore.cs`) states it plainly: *"This is the
single update mechanism the whole engine is built on — a property binding is an effect at node granularity; a component
re-render is an effect at subtree granularity."*

## How the codebase is organized (the acyclic assembly graph)

The engine is a strict **acyclic** graph of assemblies under `src/`. The dependency direction is load-bearing: an
assembly may only reference ones *below* it, and the build is structured so a leaf (a real Windows backend, or a
headless test backend) can be swapped behind a seam without anything above the seam knowing. The canonical assembly
layout is owned by [`design/foundations.md`](../../../design/foundations.md) (the repo is **4 libraries + 2 analyzers + 2 exes** — the older "18 assemblies" or "29 projects" counts in some prose are stale).

The assemblies you will actually edit, with the one type each is known for:

| Assembly | What lives there | Headline type |
|---|---|---|
| `FluentGpu.Engine` (`Foundation/`) | handles, the four allocators, `ColorF`/`Affine2D`/geometry, `StringTable`, **the Signals reactive core** (`Signals/`) | **`ReactiveCore`** (`Reactive`/`Computation`/`ReactiveRuntime`) |
| `FluentGpu.Engine` (`Dsl/`) | `Element` records, `Ui.*` builders, `Modifiers`, theming tokens (`Tok`/`Theme`), the `Prop<T>` bindable channel | `Element`, `BoxEl`/`TextEl`/`ImageEl` |
| `FluentGpu.Engine` (`Hooks/`) | `Component`, `RenderContext` + the hook cells, `ComponentEl`/`Context`/`ControlFlow` | `Component` |
| `FluentGpu.Engine` (`Reconciler/`) | the heart: render-effects, the keyed positional+type diff, `For`/`Show`, context, bindings, `VirtualListEl` | **`Reconciler`** (`TreeReconciler`) |
| `FluentGpu.Engine` (`Layout/`) | flexbox/grid measure+arrange, the scoped-relayout boundary walk | **`FlexLayout`**, `LayoutInvalidator` |
| `FluentGpu.Engine` (`Scene/`) | the retained SoA tree, the parallel columns, the 3-axis dirty flags, `ImageCache`, `VirtualLayout` | **`SceneStore`** (`ISceneBackend`) |
| `FluentGpu.Engine` (`Render/`) | the record pass — walk the scene, emit the DrawList | **`SceneRecorder`** |
| `FluentGpu.Engine` (`Hosting/`) | the frame loop, `FrameStats`, `FrameDiagnostics` | **`AppHost`** |
| `FluentGpu.Controls` | Button/IconButton/ToggleButton/Slider/ScrollBar/NavigationView/Repeater/Virtual/Navigator — **composition only** | `Button`, `Slider`, … |
| `FluentGpu.Engine` (`Seams/Rhi/`, `Seams/Pal/`, `Seams/Text/`) / `FluentGpu.Windows` | the platform/GPU/text seams (interface-only in Engine); real Windows backends in `FluentGpu.Windows`; headless in `Engine/Headless/` | the seam interfaces |

The six types worth knowing before your first change — each is one stage of the single mechanism above:

- **`ReactiveCore`** (`src/FluentGpu.Engine/Foundation/Signals/ReactiveCore.cs`) — the Solid/Preact-style reactivity graph.
  Signals are observable cells; computations (effects, memos, component render-effects) auto-subscribe to the signals
  they *read* during a run and re-run when one changes. Scheduling is **deferred**: a write marks dependents stale and
  asks the host for a frame; the host drains them once per frame via `ReactiveRuntime.Flush` (phase 3). Tracking state
  is `[ThreadStatic]` because the runtime is UI-thread-confined. This is *the* file for the set→notify→flush contract;
  it must stay allocation-free on the notify path.
- **`Reconciler`** / `TreeReconciler` (`src/FluentGpu.Engine/Reconciler/Reconciler.cs`) — patches the retained `SceneStore`
  from the immutable `Element` tree. Every component is a reactive render-effect that re-renders and reconciles **only
  its own subtree** when its state/context changes (granular, never the whole app); a reused component on a parent
  re-render is a no-op (it is autonomous). Fine-grained bindings and reactive control-flow (`ShowEl`/`ForEl`) are
  effects too. The keyed positional+type diff is the structural engine underneath `For`/`Show`. Its
  `ConsumeReconciled()` / `ConsumeRenderCount()` are how the harness proves granularity.
- **`SceneStore`** / `ISceneBackend` (`src/FluentGpu.Engine/Scene/SceneStore.cs`) — the struct-of-arrays retained RenderNode
  tree: one spine (generation + free-list) indexes every parallel column (`LayoutInput`, `NodePaint`, `InteractionInfo`,
  `NodeFlags`, bounds, …). The reconciler's *only* window onto it is `ISceneBackend` — **handle-in / handle-out,
  POD-only**. Node identity is a `Handle = {u32 Index, u32 Gen}` (the generation defends against ABA — see
  [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) §2).
- **`FlexLayout`** (`src/FluentGpu.Engine/Layout/FlexLayout.cs`) — flexbox/grid over the SoA columns: a bottom-up `Measure`
  descent then a top-down `Arrange` descent. `Run` does the full tree; **`RunSubtree` re-solves only one subtree against
  its already-placed bounds** — that is the scoped relayout, the thing that keeps a deep change from re-laying-out the
  page. The boundary walk that decides where a scoped relayout stops lives next door in `LayoutInvalidator`.
- **`SceneRecorder`** (`src/FluentGpu.Engine/Render/SceneRecorder.cs`) — phase 8 (record): walks the retained scene and emits
  the DrawList, compositing **like a browser** — each node's geometry is emitted in *local* space with a world transform
  (parent ∘ translate ∘ LocalTransform) and a cumulative opacity, *"so transform/opacity animate without relayout or
  re-record of content."* This is why a bound `Transform`/`Opacity` is compositor-only.
- **`AppHost`** (`src/FluentGpu.Engine/Hosting/AppHost.cs`) — the composition root and the single-UI-thread frame loop.
  `RunFrame()` runs one full frame and returns a `FrameStats`; it drains the reactive runtime once per frame
  (`_runtime.Flush()` is phase 3), runs *scoped* layout only when a reconcile or layout-bind changed something, then
  records. `Paint(int clicks = 0)` is the pump-free half (so the window keeps redrawing during the OS modal
  move/resize loop). `FrameStats` is your instrument panel — `Rendered`, `ComponentsRendered`, and
  `HotPhaseAllocBytes` are the three numbers you assert on.

For the full where-to-change-what table (every hook, every modifier, the rounded-rect pipeline, the theming tokens, the
animation engine), see the guide's [file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what)
— it is the authoritative routing table and it is kept in sync with the source.

## Canon & single-owner discipline (design/ is authority; these docs are the working view)

FluentGpu's corpus is large and heavily cross-referenced, and consistency is enforced by a **strict single-owner
model**. Honor it — the #1 review finding, every time, is the same artifact (an opcode shape, a scene column, a seam, a
hook) defined two ways in two docs.

- **The design corpus is the architecture authority.** It lives under [`design/`](../../../design/README.md). When two
  docs disagree on a cross-cutting contract, [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) is the **precedence
  authority** — for every contract (the handle byte layout, the COM ruling, the threading model, the quarantine
  constant, `DepKey`, the `Prop<T>` surface, …) it names the *one* owning doc and states the current canonical value
  inline, so you never discover a supersession by reading three docs.
- **Every shared artifact has exactly one owner.** [`subsystems/README.md`](../../../design/subsystems/README.md) holds
  the **contract-ownership map**: every DrawList opcode, RHI method, PAL seam, hook, source generator, scene column, and
  assembly maps to its one authoritative doc. Define an artifact in its owner; everywhere else *reference* it — never
  restate a struct shape or an enum. That restatement is exactly the drift the gate and reviews catch.
- **These site docs are the working view, not the authority.** This page and the [guide](../../guide/README.md) are
  task-oriented and source-grounded; they *link into* the canon rather than re-litigating it. The as-built reactive
  model in particular is owned by [`reconciler-hooks.md` §0bis](../../../design/subsystems/reconciler-hooks.md). If a
  doc and the source ever disagree, the source wins and the doc is the bug.
- **Adding a cross-cutting contract** means registering it in `SPEC-INDEX.md` §2 **and** the subsystems ownership map
  *before* two docs can disagree about it, then running the canon gate.

After any edit under `design/`, run the drift gate (it fails if a known-stale/superseded token reappears anywhere in the
live tree; `design/archive/` is excluded):

```powershell
powershell -File design/check-canon.ps1                  # exit 0 = clean
```

To intentionally mention a superseded form in live prose (e.g. to explain a correction), put
`<!-- canon-allow: reason -->` on that line.

There is also an **honesty discipline** baked into the canon, and edits must preserve it. These are *truths, not gaps to
"fix"*: it is **near-zero-allocation** (not "zero" — zero per-frame managed allocation only on the paint phases 6–13,
bounded Gen0 at the reconcile/render edge); slowness is **decoupled, not invincible** (a sustained GPU stall still bounds
back to the UI thread); **production safety == CI coverage** (the debug guards are `[Conditional]`-erased from the
shipping NativeAOT binary, so a hazard not under a green gate is unguarded at runtime); and the build *order* ships
single-thread-correct first, then flips parallelism on behind a green race gate. The honest scorecard is
[`winui-painpoints-assessment.md`](../../../design/winui-painpoints-assessment.md) — do not dress it up.

## The path through these docs

You do **not** need to read the whole corpus before your first change. Take this path:

1. **Get an app on screen and run the harness — 5 minutes.** Even as an engine contributor, build the minimal app from
   [getting-started.md](../../guide/getting-started.md) and run the verification harness once, so you have a known-green
   baseline before you change anything.
2. **Read the one model from below.** [reactivity.md](../../guide/reactivity.md) — signals, the three update
   mechanisms, the one `Component` model (run-once inferred), bindings, `For`/`Show`, context. You are about to edit the machinery
   that implements this; know its observable contract cold first.
3. **Learn the data substrate and the pipeline.**
   [rendering-and-performance.md](../../guide/rendering-and-performance.md) — the frame pipeline as-built, the SoA scene,
   reconcile, scoped relayout + the boundary firewall, the compositor bypass, and where the zero-alloc guarantee comes
   from. This is the map from the six types above to the frame they cooperate to produce.
4. **Find the one place to change.** The
   [file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what) routes any change to
   its single owning file; the design [subsystem index + ownership map](../../../design/subsystems/README.md) routes any
   *contract* to its single owning doc. Use both — change the owner, reference it elsewhere.
5. **Go deep in the design corpus only for the subsystem you're touching.** Start at
   [`design/README.md`](../../../design/README.md), then the relevant subsystem doc. The corpus has an explicit
   implementer reading order (foundations → threading → COM/AOT → scene-memory → dsl-aot → reconciler-hooks → layout →
   …) in [`subsystems/README.md` §3](../../../design/subsystems/README.md) if you want the full tour.

Building WinUI-faithful controls — matching real WinUI templates, storyboards, and timing tokens — is its own advanced
topic; see [control-fidelity.md](../../guide/control-fidelity.md) and the
[parity audit](../../guide/winui-control-parity-audit.md) before a control sweep.

## The golden rule: verify with the headless harness before claiming done

There is one rule that outranks the others: **verify with the headless harness, never by eye.** The
`FluentGpu.VerticalSlice` is the single source of truth for "does the engine still work" — it runs ~60 end-to-end golden
checks across every seam (layout, reconcile, signals, scroll, virtualization, images, controls, navigation, animation),
with **no GPU and no window**, in seconds, and prints `[PASS]`/`[FAIL]` and `ALL CHECKS PASSED`:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

After **any** engine change, run it and confirm `ALL CHECKS PASSED` before you claim success. It is fast, deterministic,
and catches the cross-seam regressions a focused test won't. It runs headless because the seam is swappable: the
`HeadlessGpuDevice` records the DrawList into inspectable lists (`LastGlyphs`, `LastRects`, `LastGradientStrokes`, …) so
tests assert *what was drawn* without pixels, and `host.Scene` exposes post-layout bounds — see
[getting-started.md → run headless](../../guide/getting-started.md#run-headless-tests--ci--agents) for the exact wiring.

**Add a check** for the behavior you changed by writing a `Check("…", condition, detail)` in
`src/FluentGpu.VerticalSlice/Program.cs` and calling it from `Main`. If you touched reactivity, layout, or the renderer,
assert on `FrameStats` for the interaction you changed:

- `Rendered` — was there a reconcile/layout this frame? For a slider drag or scroll you bound (the compositor-bypass
  path), you want `Rendered == false`.
- `ComponentsRendered` — how many component render-effects ran. This is the granularity proof: a leaf state change must
  not re-render the page.
- `HotPhaseAllocBytes` — managed bytes allocated in the paint half. On a steady frame this **must be 0**; that is the
  near-zero-allocation guarantee, asserted, not asserted-by-vibes.

What the harness does **not** assert is on-screen D3D12 pixels — those are a separate, manual "needs-pixels" pass on the
real Windows path. So "the harness is green" means the logic and the recorded DrawList are correct; it does not by itself
mean a control looks right on screen. Both bars matter; keep them distinct.

---

**Next:** the [file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what) for where to
change what · [rendering & performance](../../guide/rendering-and-performance.md) for the frame pipeline as-built ·
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) for the canonical contracts ·
[`subsystems/README.md`](../../../design/subsystems/README.md) for the contract-ownership map and the implementer
reading order.
