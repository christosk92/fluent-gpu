# Contributor Map: Where to Change What

This is the engine-contributor's first stop. You are here to change FluentGpu's **internals**, not to build an app
with it — if you want to build an app, start at [Building apps with FluentGpu](../app-authors/index.md).

FluentGpu enforces a strict **single-owner** model: every shared artifact (a signal primitive, an `Element` shape, a
reconciler path, a scene column, a render opcode, a PAL/RHI/Text seam) is defined in **exactly one place** and
referenced everywhere else. The #1 review finding on this codebase is the same artifact defined two ways in two files.
So before you touch anything, the rule is: **find the one owner, change it there, verify against the headless harness.**

This page maps subsystem → owning file, walks the consolidated assembly graph, and then gives the precise, file-level
"where do I change *this*" for each layer, ending with two recipes (adding an `Element` type; the canon gate after
editing `design/`). The architecture *reasoning* lives in the [design corpus](../../../design/README.md) — this page
links to it rather than restating it.

> The same map exists in compressed form as the guide hub's
> [file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what) and the
> [docs hub's "where to change what" table](../index.md#for-engine-contributors). This page is the long-form version
> with the *why* and the *how to verify*.

---

## The file & ownership map

Each row is **one owner**. Change the artifact there; reference it elsewhere. (Paths are relative to the repo root.)

| Subsystem | The `src/` file that owns it | Key types |
|---|---|---|
| Reactive runtime (signals, effects, scheduler) | `src/FluentGpu.Engine/Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs` | `Reactive`, `Computation`, `ReactiveRuntime`, `Signal<T>`, `FloatSignal`, `Effect`, `Memo<T>` |
| The unified bindable channel | `src/FluentGpu.Engine/Foundation/Signals/Prop.cs` | `Prop<T>`, `Prop.Of` |
| Hooks (`UseState`/`UseSignal`/`UseContext`/…) | `src/FluentGpu.Engine/Hooks/RenderContext.cs` (impl) + `Component.cs` (surface) | `RenderContext`, hook cells |
| Component model | `src/FluentGpu.Engine/Hooks/Component.cs` | `Component`, `ReactiveComponent`, `RunsOnce` |
| Reconcile, render-effects, `For`/`Show`, context, bindings | `src/FluentGpu.Engine/Reconciler/Reconciler.cs` | `TreeReconciler` |
| `Element` shapes / props | `src/FluentGpu.Engine/Dsl/Element.cs`, and `src/FluentGpu.Engine/Hooks/{ComponentEl,Context,ControlFlow}.cs` | `Element`, `BoxEl`, `TextEl`, `GridEl`, `ImageEl`, `ScrollEl`, `ShowEl`, `ForEl`, … |
| DSL helpers (`Ui.*`) / modifiers | `src/FluentGpu.Engine/Dsl/{Factories,Modifiers}.cs` | `Ui`, modifier extensions |
| Controls (Button/Slider/Nav/Virtual…) | `src/FluentGpu.Controls/*.cs` | composition only — **no** new opcode/column/seam |
| Layout (flex/grid/measure) | `src/FluentGpu.Engine/Layout/FlexLayout.cs` | `FlexLayout.Run` / `RunSubtree` |
| Scoped relayout / boundary firewall | `src/FluentGpu.Engine/Layout/LayoutInvalidator.cs` | `LayoutInvalidator`, `IsLayoutBoundary` |
| Retained scene (SoA tree, columns, dirty flags) | `src/FluentGpu.Engine/Scene/{SceneStore,Columns}.cs` | `SceneStore`, `ISceneBackend`, `LayoutInput`, `NodePaint`, `NodeFlags`, `VisualKind` |
| Record → DrawList | `src/FluentGpu.Engine/Render/SceneRecorder.cs` | `SceneRecorder` |
| Rounded-rect / border raster | `src/FluentGpu.Engine/Render/SceneRecorder.cs` + `src/FluentGpu.Windows/D3D12/*Pipeline.cs` | the SDF ring + gradient PSOs |
| Frame loop, scheduling | `src/FluentGpu.Engine/Hosting/AppHost.cs` | `AppHost.RunFrame` / `Paint`, `FrameStats` |
| Theming tokens / colors | `src/FluentGpu.Engine/Dsl/{Tokens,Theme}.cs` | `Tok`, `TokenSet` |
| Tests / golden checks | `src/FluentGpu.VerticalSlice/Program.cs` | `Check(...)` |

The cross-cutting **contract** owners (every opcode, RHI method, PAL seam, hook, source generator, scene column,
assembly → its one authoritative design doc) live in the
[subsystem ownership map](../../../design/subsystems/README.md#2-contract-ownership-map), and the precedence authority
when two docs disagree is [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md). Consult those before any *cross-cutting*
change; this page is for the *code-file* change.

---

## The assembly graph

The solution ([`src/FluentGpu.slnx`](../../../src/FluentGpu.slnx)) is an **acyclic** graph of **4 libraries + 2 analyzers + 2 exes**, grouped into
five solution folders. (`FluentGpu.slnx` is the source of truth.) <!-- canon-allow: superseded assembly count, narrating the old layout -->

**UI-Rendering** — the portable engine core in one library project (`FluentGpu.Engine`):

| Assembly | What's in it (folder) |
|---|---|
| `FluentGpu.Engine` (`Foundation/`) | handles, allocators, `ColorF`/`Affine2D`/geometry, `StringTable`, **the signals reactive core** (`Signals/`) and `Prop<T>` |
| `FluentGpu.Engine` (`Scene/`) | the retained SoA `SceneStore`, the columns, `ImageCache`, `VirtualLayout` |
| `FluentGpu.Engine` (`Layout/`) | `FlexLayout`, `LayoutInvalidator` (scoped relayout), `ExtentTable` |
| `FluentGpu.Engine` (`Render/`) | `SceneRecorder` (scene → DrawList) |
| `FluentGpu.Engine` (`Dsl/`) | `Element` records, `Ui.*` builders, `Modifiers`, theming (`Tok`/`Theme`) |
| `FluentGpu.Engine` (`Hooks/`) | `Component`/`ReactiveComponent`, `RenderContext` + hooks, `ComponentEl`/`Context`/`ControlFlow` |
| `FluentGpu.Engine` (`Reconciler/`) | the reconciler (render-effects, keyed diff, `For`/`Show`, bindings), `VirtualListEl` |
| `FluentGpu.Engine` (`Input/`) | hit-testing, the dispatcher, focus, gestures |
| `FluentGpu.Engine` (`Animation/`) | `AnimEngine`, springs, keyframes, the motion channels |
| `FluentGpu.Engine` (`Hosting/`) | `AppHost` (the frame loop), `FrameStats`, `FrameDiagnostics` |
| `FluentGpu.Engine` (`Media/`) | the image/video pipeline (decode, residency, mosaic) |
| `FluentGpu.Engine` (`Seams/Rhi/`, `Seams/Pal/`, `Seams/Text/`) | the abstract platform/GPU/text boundaries (interface-only, no platform code) |
| `FluentGpu.Engine` (`Headless/Rhi/`, `Headless/Pal/`, `Headless/Text/`) | the GPU/window/font-free fakes the golden-check harness runs against |

**Controls-Windowing** — portable control kit and Windows backend:

| Assembly | What's in it |
|---|---|
| `FluentGpu.Controls` | Button/IconButton/ToggleButton/Slider/ScrollBar/NavigationView/Repeater/Virtual/Navigator — **composition only, refs Engine only, TerraFX-free** |
| `FluentGpu.Windows` (`Pal/`) | `Win32App`/`Win32Window` — the real Win32 PAL (was `FluentGpu.Pal.Windows`) |
| `FluentGpu.Windows` (`D3D12/`) | `D3D12Device` — the Direct3D 12 RHI (was `FluentGpu.Rhi.D3D12`); the ONE TerraFX `PackageReference` lives here |
| `FluentGpu.Windows` (`DirectWrite/`) | `DirectWriteFontSystem` — the text backend (was `FluentGpu.Text.DirectWrite`) |
| `FluentGpu.Windows` (`Wic/`) | `WicImageCodec` — the WIC image codec (was `FluentGpu.Media.Codecs.Wic`) |
| `FluentGpu.Windows` (`Interop/`) | Win32 interop helpers (was `FluentGpu.Win32.Interop`) |
| `FluentGpu.Windows` (`Uia/`) | UIA accessibility (was `FluentGpu.Accessibility.Uia`) |

**Windows-APIs** — OS services scaffold:

| Assembly | What's in it |
|---|---|
| `FluentGpu.WindowsApi` | empty scaffold for Notifications, Credentials, Packaging, Activation — refs Engine only |

**Tooling** — Roslyn source generators (netstandard2.0, cannot merge): `FluentGpu.SourceGen`, `FluentGpu.Interop.SourceGen`.

**Apps** — `FluentGpu.VerticalSlice` (the headless golden-check harness — **your verification target**) and
`FluentGpu.WindowsApp` (the on-screen gallery + `FluentApp.Run`).

The invariant the whole graph protects: **Engine never references a Windows backend.** Code flows Engine (seam interfaces) → `FluentGpu.Windows` leaves; the concrete leaf is injected at composition time in `AppHost` (or by the headless harness). Keep it acyclic — if a feature seems to need an Engine → backend reference, the artifact is in the wrong layer.

---

## Signals & reactive core — `FluentGpu.Engine/Foundation/Signals`

This is the load-bearing centre: **every** update in the engine is one reactive computation re-running because a signal
it read changed. There is no full-app re-render and no global dirty flag.

| File | Owns |
|---|---|
| `ReactiveCore.cs` | `Computation` (the base for effects/memos/render-effects), `ReactiveRuntime` (the per-host scheduler + pending queue + `Flush`), `Reactive.Untrack`/`OnCleanup`, the thread-static `Tracking.Current` |
| `Signal.cs` | `Signal<T>` and `FloatSignal` — observable cells |
| `Effect.cs` | `Effect` (a reactive side-effect; a property *binding* is one) and `ManagedEffect` (the base a component render-effect subclasses) |
| `Memo.cs` | `Memo<T>` — a lazy, cached derived value |
| `Prop.cs` | `Prop<T>` — the one bindable channel a property exposes (value, `Func<T>`, or signal) |

The contracts that constrain edits here (all observable in the source):

- **Reading subscribes; peeking doesn't.** `Signal<T>.Value`'s getter calls `Subscribe()` (links `Tracking.Current`);
  `Peek()` returns the field with no link. Anything that "should update but doesn't" read `.Peek()` where it needed
  `.Value`.
- **Writes are deferred.** `Signal<T>.Value`'s setter calls `NotifySubscribers()` → `MarkStale()` on each subscriber,
  which (for effects) enqueues onto `ReactiveRuntime` — it does **not** run the effect inline. The host drains the
  queue once per frame via `ReactiveRuntime.Flush()` (phase 3). This is what keeps reconcile/layout on their phases and
  the paint half allocation-free.
- **The write path must stay alloc-free.** `NotifySubscribers` iterates the live `_subs` list (no defensive copy);
  `FloatSignal` skips the comparer/boxing indirection entirely so a per-pointer-move scrub write allocates nothing.
  Keep it that way — a steady-frame allocation here trips the harness's `HotPhaseAllocBytes == 0` assertion.
- **Memos are pull-based.** `Memo<T>` recomputes lazily on read when `State == Stale` and only notifies *its*
  subscribers when the cached value actually changes; its `RunStale()` is a no-op (it never self-schedules).
- **Ownership is explicit.** `Computation`'s ctor takes an `owner` and only auto-cascades disposal when one is passed.
  Hook-created computations (memos, bindings) persist across a component's re-renders and are disposed by the
  reconciler on unmount, **not** auto-disposed by the enclosing render-effect's next run.

If you change scheduling semantics (batching, the flush guard, when `FrameRequested` fires), you are changing the
canonical reactive model — mirror it into [`reconciler-hooks.md` §0bis](../../../design/subsystems/reconciler-hooks.md)
(the as-built signals owner) and run the canon gate.

---

## Hooks & components — `FluentGpu.Engine` (`Hooks/`)

The hook surface lives on `Component` (`Component.cs`) and is implemented in `RenderContext` (`RenderContext.cs`).

- **`Component.cs`** is the authoring surface: `UseState`/`UseSignal`/`UseFloatSignal`/`UseComputed`/`UseEffect`/
  `UseLayoutEffect`/`UseReducer`/`UseMemo`/`UseRef`/`UseContext` plus the animation/media hooks, all forwarding to
  `Context`. It also defines `RunsOnce` — `false` for a classic `Component` (its render-effect re-subscribes and
  re-runs on its own state/context changes), sealed `true` on `ReactiveComponent` (the reconciler runs its body
  *untracked*, so it never re-renders; reactivity comes purely from the bindings/`For`/`Show` inside).
- **`RenderContext.cs`** is the per-component hook store: an ordered `List<HookCell>` walked by `_cursor` each render,
  so **hooks must run in stable call order** (no hooks inside `if`/loops — same rule as React). Each hook kind has its
  cell type (`SignalCell<T>`, `FloatSignalCell`, `MemoHookCell<T>`, `EffectCell`, `MemoCell<T>`, `RefHolderCell`,
  `AnimValueCell`). It also carries `PendingEffects` (drained after present, phase 12) and `PendingLayoutEffects`
  (drained after layout, phase 6.5).

A `UseState` is a `Signal<T>` whose only subscriber is *this* component's render-effect — that is *why* `setState`
re-renders only this component's subtree (granular), never the app. Adding a hook means: a new cell type in
`RenderContext.cs`, a method on `RenderContext`, and a thin forwarder on `Component`. Hook *semantics* (DepKey,
memo-skip) are owned by [`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md).

---

## Reconciler, For/Show, bindings, context — `FluentGpu.Engine` (`Reconciler/`)

`TreeReconciler` (in `Reconciler.cs`) is the heart: it patches the retained `SceneStore` from the immutable `Element`
tree. It is signals-first — every component is a reactive render-effect that re-renders + reconciles **only its own
subtree**; bindings and `For`/`Show` are effects too.

The dispatch you'll touch most:

- **`Mount(NodeHandle, Element)`** type-tests the element and routes the structural kinds to their handlers
  (`MountComponent`/`MountProvider`/`MountScroll`/`MountVirtual`/`MountShow`/`MountFor`); everything else goes through
  the generic path: `WriteColumns` → `BindNode` → recurse over `ChildrenOf`.
- **`Update(NodeHandle, Element, Element)`** mirrors `Mount`: a reused component is a **no-op** (it's autonomous — it
  re-renders via its own effect; a parent re-render does not touch it), `For`/`Show` are no-ops on a parent re-render
  (their boundary effect manages children reactively), and the generic path is
  `WriteColumns(node, newEl, isMount:false, oldEl)` → `ReconcileChildren(ChildrenOf(newEl), ChildrenOf(oldEl))`.
- **`WriteColumns(NodeHandle, Element, bool isMount, Element? old)`** is the big `switch (el)` that writes an element's
  fields into the scene's SoA columns (`Paint`, `Layout`, interaction). This is where a new `Element` type's static
  props land.
- **`BindNode(NodeHandle, Element)`** wires a reactive `Effect` for each *bound* channel (`Prop<T>.IsBound`). The
  effect reads the thunk/signal inside itself (so it re-subscribes) and writes one scene field. Note the dirty split
  it encodes: `Transform`/`Opacity`/`Fill` mark `TransformDirty`/`PaintDirty` only (**compositor-only** — no relayout);
  `Width`/`Height` mark `LayoutDirty` (a **scoped** relayout). That split is the whole performance story.
- **`ChildrenOf(Element?)`** returns the positional children of a container (`BoxEl.Children` / `GridEl.Children`),
  empty for leaves.

`ShowEl`/`ForEl` are records in `FluentGpu.Engine/Hooks/ControlFlow.cs` (with the `Flow.Show` / `Flow.For` fluent factory);
the reconciler runs their `When`/`Count`/`ItemAt` thunks inside a boundary effect and re-realizes through the keyed
diff. Context providers publish a `Signal<object?>` per provider node; a consumer resolves by walking ancestors
(`ResolveContext`). The keyed-LIS child reconciler and the `Mutate()` epoch chokepoint are specified in
[`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md).

---

## Element shapes & DSL — `FluentGpu.Engine` (`Dsl/`)

An `Element` (in `Element.cs`) is an **immutable description** of a UI node — cheap to build, never touches the scene
directly. The base carries `Key` and an abstract `ushort ElementTypeId` (a source-gen-style stable id for the
reconciler's integer type-dispatch). The records and their ids today:

| `ElementTypeId` | Record | File |
|---|---|---|
| 1 | `BoxEl` | `FluentGpu.Engine/Dsl/Element.cs` |
| 2 | `TextEl` | `FluentGpu.Engine/Dsl/Element.cs` |
| 3 | `ComponentEl` | `FluentGpu.Engine/Hooks/ComponentEl.cs` |
| 4 | `ContextProviderEl` | `FluentGpu.Engine/Hooks/Context.cs` |
| 5 | `ScrollEl` | `FluentGpu.Engine/Dsl/Element.cs` |
| 6 | `VirtualListEl` | `FluentGpu.Engine/Reconciler/VirtualListEl.cs` |
| 7 | `ShowEl` | `FluentGpu.Engine/Hooks/ControlFlow.cs` |
| 8 | `ImageEl` | `FluentGpu.Engine/Dsl/Element.cs` |
| 9 | `GridEl` | `FluentGpu.Engine/Dsl/Element.cs` |
| 10 | `ForEl` | `FluentGpu.Engine/Hooks/ControlFlow.cs` |
| 11 | `PolylineStrokeEl` | `FluentGpu.Engine/Dsl/Element.cs` |
| 12 | `SpanTextEl` | `FluentGpu.Engine/Dsl/Element.cs` |

Bindable properties on these records are a single `Prop<T>` — e.g. `BoxEl.Fill` is `Prop<ColorF>`, `TextEl.Text` is
`Prop<string>`, `ImageEl.Source` is `Prop<string>`. A `Prop<T>` accepts a static value, a `Func<T>` thunk (via
`Prop.Of(...)` for an inline lambda), or a concrete signal — there is no separate `*Bind` property anymore (the canon
gate enforces that; see below). The fluent builders that construct these records (`VStack`/`HStack`/`Text`/`Grid`/…)
and the chainable modifiers live in `Factories.cs` / `Modifiers.cs` — pure element builders, no scene access.

---

## Controls — `FluentGpu.Controls` (composition only)

Controls are **pure composition**: a `Button`, `Slider`, `NavigationView`, `Virtual`, etc. assemble existing
`Element`s and hooks. The hard rule (asserted by the design ownership map) is that a control adds **no** new DrawList
opcode, **no** new scene column, **no** new PAL/RHI seam, and **no** new hook — if you find yourself wanting one, the
artifact belongs in its owning subsystem (renderer / scene / seam / hooks), and the control consumes it.

Visual-state and motion authoring is done through existing engine primitives, not a hand-rolled state machine:
`BoxEl.{Hover,Pressed}{Fill,BorderColor}` + the eased hover/press progress for ordinary interaction, and the
`FluentGpu.Engine` (`Animation/`) `AnimEngine` (keyframes/springs/stroke-trim) for authored WinUI timelines. Building a
WinUI-faithful control — where to find the exact templates/storyboards/timing tokens and how to verify them — is its
own guide: [control-fidelity.md](../../guide/control-fidelity.md). The control kit's contract is owned by
[`controls.md`](../../../design/subsystems/controls.md).

---

## Layout — `FluentGpu.Engine` (`Layout/`)

`FlexLayout` (in `FlexLayout.cs`) is a flexbox engine over the SoA scene columns: a bottom-up **Measure** descent then
a top-down **Arrange** descent (distribute free space by grow/shrink, position by justify-content, align the cross axis
by align-items/align-self). `Run(root)` content-sizes; `Run(root, window)` fills the window. Grid is the `GridEl` path.

The performance half is `LayoutInvalidator` (in `LayoutInvalidator.cs`): it consumes the scene's `LayoutDirty`
worklist and, for each dirty node, walks **up to the nearest layout boundary** — a fixed-size, non-flexing,
clip-to-bounds container whose own size can't change because of a descendant — then re-solves just that subtree via
`FlexLayout.RunSubtree`. That boundary is the **firewall**: a `setState` deep inside a fixed-size card relayouts only
the card, never the page (`IsLayoutBoundary` is the exact predicate: explicit `Width`+`Height`, zero grow+shrink,
`ClipsToBounds`). With no bounded ancestor it falls back to a full layout. The incremental-layout contract is owned by
[`layout.md`](../../../design/subsystems/layout.md).

---

## Retained scene — `FluentGpu.Engine` (`Scene/`)

`SceneStore` (in `SceneStore.cs`) is the **struct-of-arrays** retained RenderNode tree the reconciler patches: one
spine (a `gen` array + free-list, `Handle = {index, gen}`) indexes all the parallel columns. It implements the narrow
`ISceneBackend` op set (`CreateNode`/`FreeSubtree`/`AppendChild`/`Layout`/`Paint`/`Interaction`/`Mark`/`FirstChild`/…)
— that interface is the reconciler's **only** window onto the tree (handle-in / handle-out, POD-only).

`Columns.cs` defines the column structs and the per-node enums: `LayoutInput` (flex inputs), `NodePaint`,
`InteractionInfo`, `NodeFlags` (the dirty bits: `LayoutDirty`/`PaintDirty`/`TransformDirty`/… plus
`ClipsToBounds`/`Focused`/…), `VisualKind` (`None`/`Box`/`Text`/`Image`/`PolylineStroke`), and the
`TextMeasureCache`. Side-tables keyed by node index hold the sparse rich paint (shadows, gradients, brush anims) and
the managed edge payloads (the click/key handler `Action`s — GC refs live only at the edge, never in a hot column).

Adding a column = a new parallel array on `SceneStore`, grown in lockstep with the spine, written by `WriteColumns` in
the reconciler, and read by the recorder/layout. A *cross-cutting* column (one a snapshot must capture, or a new
opcode reads) is owned by [`scene-memory.md`](../../../design/subsystems/scene-memory.md) — register it there.

---

## Record → DrawList — `FluentGpu.Engine` (`Render/`)

`SceneRecorder` (in `SceneRecorder.cs`) is phase 8 (record): it walks the retained `SceneStore` and emits the DrawList.
It composites **like a browser** — each node's geometry is emitted in LOCAL space with a world transform
(`parent ∘ translate ∘ LocalTransform`, scale/rotate about the node centre) and a cumulative opacity, so
transform/opacity changes animate **without relayout or re-record of content**. Hover/press cross-fade via the eased
interaction row; a focused node gets the dual-stroke focus ring.

The recorder is also where the rounded-rect / border *emission* happens; the *raster* (the hollow SDF ring, gradient
strokes, AA, clip) is the D3D12 pipelines in `src/FluentGpu.Windows/D3D12/*Pipeline.cs`. The DrawList **encoding
framework** (the command header, byte arenas, the parallel sortkey array, the opcode enum, the clean-span+epoch reuse
rule) is owned by [`scene-memory.md`](../../../design/subsystems/scene-memory.md); each opcode's **payload struct
shape** (and the color contract) is owned by [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md). Add an
opcode in those owners; the recorder is the *emit* site, not the definition site.

---

## Frame loop — `FluentGpu.Engine` (`Hosting/`)

`AppHost` (in `AppHost.cs`) is the composition root **and** the single-UI-thread frame loop. `RunFrame()` runs one
full frame and returns `FrameStats`; `Paint(clicks)` is the pump-free half the window's `PaintRequested` callback uses
during the OS modal move/resize loop. The phase order, read straight from `RunFrame`:

- **Phase 3 — `_runtime.Flush()`**: drain the reactive runtime; scheduled render-effects reconcile their subtrees and
  bindings apply.
- **Phase 6 — layout**: `_layout.Run(root, size)` on the first frame / resize / DPI / root change, else
  `_invalidator.RunDirty(size)` for a scoped relayout of just the dirty subtrees.
- **Phase 6.5 — layout effects**: `DrainLayoutEffects()` (Bounds are now valid).
- **Phase 7 — animation**: `_anim.Tick`, the interaction/brush/scroll/repeat/caret tickers — transform/opacity/
  presented-size only, **never** relayout.
- **Then** record (phase 8) → batch/submit/present.

`FrameStats` is the diagnostics surface you assert against in tests: `Rendered` (a reconcile or layout happened this
frame — `false` ⇒ a compositor-only frame), `ComponentsRendered` (granularity metric), and **`HotPhaseAllocBytes`
(must be 0 on steady frames)**, plus the record-phase counts. The canonical 13-phase loop + the threading seam are
owned by [`threading-render-seam.md`](../../../design/subsystems/threading-render-seam.md); `AppHost` is the
single-thread as-built that implements it.

---

## Theming tokens — `FluentGpu.Engine/Dsl/Tokens.cs`

`Tok` (in `Tokens.cs`) is the active design-token table + the theme switch. A `TokenSet` is an immutable, baked
palette of every semantic Fluent brush for one theme (the WinUI `*_themeresources` mirror), built once and never
mutated. Author code reads tokens through the static getters (`Tok.FillCardDefault`, `Tok.TextPrimary`, …) which
resolve against the active `Tok.T` at zero per-read cost.

- **`Tok.Use(ThemeKind)`** re-themes the whole app with **one pointer write** (swaps `T` between the prebuilt
  `Light`/`Dark` sets).
- **`Tok.SetAccent(ColorF?)`** injects the live OS accent (or a developer global override); every accent token
  recomputes from it. `Tok.SetWindowBackground(ColorF)` is the Mica→Transparent override. These are mutable overrides
  *on top of* the set, so they survive a theme swap.

> Note the honest caveat in the source: a `TextEl`/`SpanTextEl` default foreground (`Tok.TextPrimary`) is resolved at
> element **construction**, so a runtime `Tok.Use` switch does not by itself re-render — a live theme switcher must
> force a full re-render or construction-resolved colors go stale.

Add a token = a `required` field on `TokenSet`, a value in both `BuildDark()`/`BuildLight()`, and a static getter on
`Tok`. The reactive/dynamic-color theming subsystem is owned by
[`theming.md`](../../../design/subsystems/theming.md); the slice's `Tok`/`Theme` is the as-built baked-palette layer.

---

## Tests / golden checks — `FluentGpu.VerticalSlice`

The `FluentGpu.VerticalSlice` app is the **single source of truth for "does the engine still work"** — ~60 end-to-end
golden checks across every seam (layout, reconcile, signals, scroll, virtualization, images, controls, navigation,
animation), run entirely on the **headless** backends (no GPU, no window). Run it:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

Each check is a one-liner; the harness prints `[PASS]`/`[FAIL]` and exits non-zero if any failed:

```csharp
static void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok) s_failures++;
}
// …at the end of Main:
//   if (s_failures == 0) { Console.WriteLine("ALL CHECKS PASSED …"); return 0; }
//   Console.WriteLine($"{s_failures} CHECK(S) FAILED."); return 1;
```

Real examples from `Program.cs` show the style — drive the headless `AppHost`, then assert on `host.Scene` bounds or
the `HeadlessGpuDevice`'s recorded `LastGlyphs`/`LastRects`:

```csharp
Check("11. flex-grow splits free space", Near(g0.W, 150) && Near(g1.W, 150) && Near(g1.X, 150), $"w0={g0.W:0.#} x1={g1.X:0.#}");
Check("17. keyed reconcile reorders, preserving identity", reordered, "[a,b,c] → [c,a,b]");
Check("22. opacity timeline samples t0, eases & completes (no relayout)", startOk && midOk && doneOk, $"@50ms={op:0.00}");
```

**Add a check** by writing a `Check("…", condition, detail)` in `src/FluentGpu.VerticalSlice/Program.cs` and calling it
from `Main`. If you touched reactivity/layout/render, also assert on `FrameStats` for the interaction you changed —
`Rendered`, `ComponentsRendered`, and `HotPhaseAllocBytes` (which **must be 0** on steady frames). The headless setup
(swapping in `HeadlessPlatformApp`/`HeadlessWindow`/`HeadlessGpuDevice`/`HeadlessFontSystem`) is shown in
[getting-started.md → Run headless](../../guide/getting-started.md). GPU pixels are **not** asserted here — that is a
separate manual "needs-pixels" pass on the real D3D12 path.

> After any engine change: run the harness and confirm `ALL CHECKS PASSED` before claiming success. It is fast
> (seconds), deterministic, and catches cross-seam regressions a focused test won't. The full validation program
> (spikes + per-PR gates) is owned by [`validation.md`](../../../design/subsystems/validation.md).

---

## Recipe: add a new `Element` type

Walking the existing wiring (above) gives the exact steps. Say you're adding a leaf `MyLeafEl`:

1. **Pick a free `ElementTypeId`.** The ids 1–12 are taken (see the table above); the next free id is **13**. Define
   the record in `FluentGpu.Engine/Dsl/Element.cs` (or the relevant `FluentGpu.Engine/Hooks/*.cs` for a control-flow/context kind),
   override `ElementTypeId => 13`, and expose bindable props as `Prop<T>` (a static value / `Func<T>` / signal — never
   a `*Bind` twin).

   ```csharp
   public sealed record MyLeafEl : Element
   {
       public override ushort ElementTypeId => 13;
       public Prop<ColorF> Tint { get; init; } = Tok.TextPrimary;   // a bindable channel
       public float Size { get; init; } = 14f;
       // …leaf layout participation (Width/Height/Grow/Shrink/Basis/AlignSelf/Margin) like the other leaves…
   }
   ```

2. **Write its columns in the reconciler.** Add a `case MyLeafEl m:` to `WriteColumns` in `FluentGpu.Engine/Reconciler/Reconciler.cs` that sets the
   scene `Paint`/`Layout` fields (and the `VisualKind`) from the element. If the element draws something new, you may
   need a `VisualKind` member — but that, and any new opcode/column, must be added in its scene/renderer owner first
   ([`scene-memory.md`](../../../design/subsystems/scene-memory.md) /
   [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md)).

3. **Bind its reactive channels.** If `MyLeafEl` has bound props, add the `Prop<T>.IsBound` effect wiring to
   `BindNode` (mirror the `BoxEl`/`TextEl` blocks) — mark `PaintDirty`/`TransformDirty` for compositor-only channels,
   `LayoutDirty` for size channels.

4. **Teach the tree-shape helpers, if it's a container.** A leaf needs nothing here. A *container* element must return
   its children from `ChildrenOf` (add a `MyContainerEl c => c.Children` arm) so `Mount`/`ReconcileChildren` recurse,
   and `Update` must route it if it needs special structural handling (like `ScrollEl`/`VirtualListEl`).

5. **Add a factory + a golden check.** Expose a `Ui.MyLeaf(...)` builder in `Factories.cs`, then add a
   `Check("…", …)` in `src/FluentGpu.VerticalSlice/Program.cs` that mounts it headlessly and asserts the recorded output (and `FrameStats` if it
   participates in reactivity). Run the harness.

The source-generator-driven `ElementTypeId` allocation and the keyed reconcile mechanics are specified in
[`dsl-aot.md`](../../../design/subsystems/dsl-aot.md) and
[`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md) — read them before adding a *cross-cutting*
element kind.

---

## The canon gate after editing `design/`

The corpus under [`design/`](../../../design/README.md) is the architecture source-of-truth, and it is **canon-gated**:
a PowerShell drift gate fails the build if a known-stale/superseded token reappears anywhere in the **live** design
tree (`design/archive/` is excluded). After editing any `design/*` doc, run it:

```powershell
powershell -File design/check-canon.ps1                  # exit 0 = clean   (or: pwsh design/check-canon.ps1)
```

It protects the canonical bindings from being silently re-litigated — e.g. `Handle = {u32 index, u32 gen}` (the
24-bit-gen form is superseded), the tiered COM ruling, the pure-scalar `DepKey`, and the **`Prop<T>` channel** (the old
dual `…Bind` property spelling is gone — `TransformBind`/`FillBit`/etc. reappearing anywhere live is a violation). <!-- canon-allow: documents the gate rule itself --> To
intentionally mention a superseded form in live prose (e.g. to explain a correction), put
`<!-- canon-allow: reason -->` on that line. Superseding a value means: add the canonical value to
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md), add a rule to `check-canon.ps1`, **and** move the old doc to
`design/archive/`.

> This gate runs only for `design/` edits. Source changes are verified by the headless harness
> (`dotnet run --project src/FluentGpu.VerticalSlice`), not the canon gate. Most contributions touch one or the other;
> a change that edits both runs both.

---

## See also

- [Building apps with FluentGpu](../app-authors/index.md) — the app-author path (the signals mental model, runnable
  examples, the pitfalls table).
- [Developer & agent guide hub](../../guide/README.md) — the compressed file & ownership map and the agent rules.
- [Getting started](../../guide/getting-started.md) — the frame loop you call, running headless, the assembly layout.
- [Subsystem index + contract ownership map](../../../design/subsystems/README.md) and
  [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) — the cross-cutting contract authorities.
- [`reconciler-hooks.md` §0bis](../../../design/subsystems/reconciler-hooks.md) — the as-built signals-first reactive
  model.
