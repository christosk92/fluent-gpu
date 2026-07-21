# Performance rules and zero-alloc boundaries

> **✅ Animation engine — signals-first rework landed + verified.** Relevant for hot paths: motion runs on one `AnimScheduler` with **per-source cadence** (`DisplayRate`/`Hz(n)`/`Driven(signal)`/`OneShot`) and a `min(next-due)` wake — a `Driven` source emits **zero frames when its signal is unchanged** (a paused playhead costs nothing; no global FPS cap). Design, now implemented: [`../../plans/animation-engine-rework-design.md`](../../plans/animation-engine-rework-design.md).

You almost never need this page. FluentGpu is fast *by construction* — the signals-first core means a state change touches only what read it, and the compositor recomposites without relayout. Read [signals, components, and bindings](./signals-components-and-bindings.md) first; it teaches the model. Come **here** when you have a concrete hot path (a slider that drags at 200 Hz, a 100k-row list, a page that stutters on one keystroke) and you want to (a) reach for the cheapest mechanism, (b) draw the **layout boundary** that firewalls a relayout, and (c) *prove* the result with numbers instead of eyeballing it.

The whole page rests on one fact about the frame loop (`src/FluentGpu.Engine/Hosting/AppHost.cs`): **a frame does only as much work as the dirtiest thing that changed.** Three update mechanisms map onto three increasing tiers of work, and your job is to keep each change in the cheapest tier that expresses it.

```csharp
using static FluentGpu.Dsl.Ui;   // VStack, HStack, Text, ScrollView…
using FluentGpu.Hooks;            // Component, Embed, Flow
using FluentGpu.Signals;          // Signal<T>, FloatSignal, Memo<T>, Prop<T>, Prop.Of
using FluentGpu.Controls;         // Button, Slider, Virtual…
using FluentGpu.Foundation;       // Affine2D, NodeFlags, ColorF
```

## The three dirty axes

Every update marks a node along one of **three orthogonal axes** (the `NodeFlags` dirty bits in `src/FluentGpu.Engine/Foundation/NodeFlags.cs`). The axis you hit decides how expensive the frame is. This is the single most useful mental model on this page — the rest is consequences of it.

| Axis (`NodeFlags`) | Set by | What the frame does | Tier |
|---|---|---|---|
| `TransformDirty` / `PaintDirty` | a `Transform`/`Opacity`/`Fill` bind, an animation tick, hover/press | **re-record only** — no reconcile, no layout | cheapest |
| `LayoutDirty` | a component re-render, a `Width`/`Height`/`Text` bind, a structural change | **scoped relayout** of the affected subtree, then re-record | middle |
| (structural) | a mount / remove / reorder in the reconciler | reconcile that subtree + scoped relayout + re-record | most |

They are *orthogonal*: a bound `Transform` write marks `TransformDirty` and nothing else, so the frame skips straight to recording. The loop reflects this directly — `Paint` only runs layout `if (layoutNeeded && !_scene.Root.IsNull)`, where `layoutNeeded = _needFullLayout || reconciled || _scene.AnyLayoutDirty`. A pure transform/paint frame leaves `AnyLayoutDirty == false` and never enters that branch.

The optimization loop, then, is: **find which axis your change hits, and ask whether it could hit a cheaper one.** A slider that re-renders its component every pointer-move is on the `LayoutDirty` (or worse) axis; the same slider bound to a `FloatSignal`→`Transform` is on `TransformDirty`. Same pixels, an order of magnitude less work.

> Full pipeline walkthrough (what records, submits, presents, and when): [rendering-and-performance.md → the frame loop](../../guide/rendering-and-performance.md#the-frame-loop-apphostrunframe--paint). The dirty-axis table there is the canonical version of the one above.

## Granular re-render — one component's subtree, not the app

The middle tier (`LayoutDirty`) is the familiar React path, but *granular*: a `setState`/signal write schedules **only the owning component's render-effect** — not the app, not its parent, not its siblings. On the next flush that one component re-renders, its single subtree reconciles, and it is marked `LayoutDirty`. There is no prop-diffing cascade to walk, because a parent re-render never re-renders its embedded children (they re-render only for *their own* state/context — see [props don't flow through constructors](./signals-components-and-bindings.md)).

The headless harness proves it (check #59): after a leaf `setState`, `ComponentsRendered == 1` and the sibling/parent counts are unchanged. The cost is **O(that component's subtree)**, not O(app).

Two corollaries you can act on:

- **Keep state low.** State at the root makes the *root's* render-effect run on every interaction — the whole tree becomes the subtree. Push state down into the component that actually owns it. (Pitfall: "Whole app re-renders on one interaction".)
- **Split a big page into embedded children.** Each `Embed.Comp(() => new Card())` is its own re-render island. The gallery's **State & components** page (`src/FluentGpu.WindowsApp/GalleryPages.cs`, `StatePage_Chip`) shows three independent chips: clicking one re-renders only that chip — its render counter ticks, the others stay frozen.

```csharp
// Each chip owns its state; a parent re-render never re-renders a child component.
HStack(16,
    Embed.Comp(() => new Chip()),
    Embed.Comp(() => new Chip()),
    Embed.Comp(() => new Chip()))
```

If even one component's subtree is too big to re-render at your update frequency, drop to the cheapest tier — a binding.

## The compositor bypass — the slider tank, killed

The cheapest tier (`TransformDirty`/`PaintDirty`) is the **compositor bypass**: bind a hot scalar to a node *channel* (`Transform`, `Opacity`, or `Fill`) and the write updates one scene-node value and recomposites. No render, no reconcile, no layout. This is the path for anything that changes many times per second — a slider scrub, a scroll offset, a progress fill, a hover glow.

The canonical failure it fixes: a slider that pushes its value through `setState` on every pointer-move re-renders its owning component every frame and tanks FPS. Bind the value instead:

```csharp
var x = UseFloatSignal(0.3f);   // hot scalar → bind it, don't setState per move

Slider.Create(x);               // a drag writes x.Value directly — no setState per pointer-move

new BoxEl
{
    Width = 32, Height = 32,
    Transform = Prop.Of(() => Affine2D.Translation(x.Value * 184f, 0f)),  // reads x.Value ⇒ subscribes; compositor-only
    Fill      = Prop.Of(() => ColorF.Lerp(grey, Tok.AccentDefault, x.Value)),  // compositor-only
};
// elsewhere x.Value = pointerX → both thunks re-run → node transform/fill update → recomposite. NO re-render.
```

`Slider.Create(FloatSignal value, Action<float>? onChange = null, SliderOptions? options = null, …)` (`src/FluentGpu.Controls/Slider.cs`) bound to a `FloatSignal` IS the built-in compositor-path slider: the thumb offset and the fill scale are bound `Transform`s, so dragging is a `TransformDirty` frame (no render/reconcile/layout). The gallery's `StatePage_BindHost` runs exactly this with a live render counter that stays put while the slider moves. Proof is harness check #60: a bound slider drag moves the thumb (`thumbDx 51→131`) with **`renders+0 rendered=False`**.

Two rules make or break the bypass (both enforced by `Prop<T>`):

- **A bind thunk must read `.Value`, not `.Peek()`.** `.Value` subscribes the binding effect so it re-runs on change; `.Peek()` wires a binding that never updates. (Pitfall: "A binding never updates".)
- **Bind the *right* channel.** `Transform`/`Opacity`/`Fill` are compositor-only; `Width`/`Height`/`TextEl.Text` are **not** — they cost a scoped relayout because they change the box or its text metrics. Express a hot change as a transform whenever you can. (The per-channel cost table lives on the [bindings page](./signals-components-and-bindings.md#1-binding--compositor-only-transform--opacity--fill).)

If a value is *also* shown as text (the "70%" next to the slider), let that small label take a scoped re-render while the thumb stays on the compositor path — read `x.Value` in a tiny component, or bind `Text = Prop.Of(() => $"{x.Value:P0}")`. The two are decoupled; only the cheap text node does layout work, never the whole slider.

> The mechanics of `Prop<T>` (value / `Prop.Of` thunk / signal-direct), and why an inline lambda needs `Prop.Of`, are on the [bindings page](./signals-components-and-bindings.md#the-unified-propt-bindable-channel-value--propof-thunk--concrete-signal). This page is about *when* to reach for each tier, not how `Prop<T>` is wired.

## Scoped relayout and the layout-boundary firewall

When you *do* land on the middle tier (a re-render, or a `Width`/`Height`/`Text` bind), the relayout is **scoped, not full**. Full-tree layout runs only on the first frame, a window resize, a DPI change, or a root structural change. Otherwise the engine keeps a `LayoutDirty` worklist and `LayoutInvalidator` (`src/FluentGpu.Engine/Layout/LayoutInvalidator.cs`) walks each dirty node **up to the nearest layout boundary**, then re-solves only that subtree (`FlexLayout.RunSubtree`). The cost is O(change), not O(tree).

A **layout boundary** is a node whose own size cannot change because of a descendant, so the up-walk stops there and the parent is never disturbed. The live predicate (`LayoutInvalidator.IsLayoutBoundary`):

```
boundary  ⟺  Width and Height are both explicit (not NaN/auto)
           ∧ FlexGrow == 0 ∧ FlexShrink == 0
           ∧ ClipToBounds == true                  (or: the node is a scroll/virtual viewport, or the root)
```

**This is the firewall that keeps a deep `setState` from relayouting the page.** Without a boundary, the up-walk reaches the root and you fall back to a full layout — still correct, just not scoped. So give your big containers — cards, panes, list rows, fixed regions — an explicit `Width`+`Height` and `ClipToBounds = true`, and a change inside them stops at their edge:

```csharp
new BoxEl
{
    Width = 360f, Height = 220f,   // explicit, non-flexing…
    ClipToBounds = true,           // …and clipped ⇒ a layout BOUNDARY
    Direction = 1,
    Children = [ /* a setState deep in here relayouts only this card, never the page */ ],
};
```

Note the spelling: the authoring property is **`ClipToBounds`** on `BoxEl` (`src/FluentGpu.Engine/Dsl/Element.cs`); the corresponding `NodeFlags` bit the engine reads is `ClipsToBounds` (with the *s*). A scroll/virtual viewport is *implicitly* a boundary (it clips and offsets by transform), which is why in-window scrolling never relayouts. (Pitfall: "A small change relayouts the whole page".)

A self-invalidating **text measure cache** (per node, keyed by text + style + available width) lets a scoped relayout skip re-shaping unchanged text leaves on the real DirectWrite path — so a re-render that doesn't touch a sibling's text doesn't re-shape it.

## The zero-alloc contract — phases 6–13, `HotPhaseAllocBytes == 0`

The paint half of the frame (phases 6–13: layout → record → submit → present → passive effects) must allocate **zero managed bytes**. `AppHost.Paint` measures it directly — it snapshots `GC.GetAllocatedBytesForCurrentThread()` before the flush and again after present, and reports the delta as `FrameStats.HotPhaseAllocBytes`. The headless harness asserts it is `0` on steady frames.

This is a *contract you can break* from app code, and the way you break it is allocating inside a hot path:

- **Wire bindings and effects once at mount; never allocate in a thunk or hot effect body.** No `new`, no LINQ, no boxing, no per-call closure inside a bind thunk or a hot `UseEffect`. The closure is built once at mount; the thunk then only *reads + writes* existing state.
- **Reuse buffers.** The engine itself uses `stackalloc`, pooled arrays, and native-backed arenas on the hot path; mirror that in your own hot code.
- **A signal `set` is allocation-free** (subscribers are a pre-sized list). Structural reconcile (mount/remove) may use **bounded Gen0** — that is the reconcile edge (phases 3–5), *not* the paint half. This is why the honest claim is **near-zero-allocation**, not "zero": zero per-frame managed alloc on phases 6–13, with bounded Gen0 at the reconcile/render edge.

The classic regression is a **virtualized list that allocates on every scroll frame** — a per-item closure or a per-item modifier rebuilt on each realize. The fix is to vary per-item chrome through *values only* (a pure-value lambda that calls nothing), keeping the recycled-row structure stable. The gallery's `VirtualizationPage` (`Virtual.ListBound`, 100k rows) is the worked example: each visible slot is built **once** with an index *signal*, and scrolling rebinds the slot by writing that signal — so only the bound `Text`/`Fill`/`Source` thunks re-run, with no element rebuild and no allocation.

```csharp
// A recycled, bound row: built once per visible slot; scrolling rewrites idx, re-running only these thunks.
static Element Row(IReadSignal<int> idx) => new BoxEl
{
    Direction = 0, Height = 48f, AlignItems = FlexAlign.Center,
    Fill = Prop.Of(() => (idx.Value % 2 == 0) ? RowEven : RowOdd),   // pure-value thunk, no allocation
    Children =
    [
        new TextEl("") { Text = Prop.Of(() => $"Item {idx.Value}") },
        new ImageEl { Width = 32f, Height = 32f, Source = Prop.Of(() => Cover(idx.Value)) },
    ],
};
// Virtual.ListBound(100000, 48f, Row)
```

> The DrawList is a POD command stream and the scene is struct-of-arrays precisely so the paint half can run without managed allocation. The contract and its rationale are owned by the design corpus: [SPEC-INDEX.md](../../../design/SPEC-INDEX.md) ("0 managed allocations in frame phases 6–13") and [foundations.md](../../../design/foundations.md).

## The optimization decision guide

Reach for the **cheapest** row that expresses your change. Each maps to a dirty axis (or to no work at all).

| Situation | Do this | Tier |
|---|---|---|
| A value changes many times/second (drag, scroll, progress, animation) | **bind it** — `Transform`/`Opacity`/`Fill` = a `Func`/signal (compositor-only), or use the animation hooks (`UseSpring`/`UseTransition`). Never `setState` per tick. | `TransformDirty` |
| A value that changes occasionally and is shown as text / affects layout | `UseState`/`UseSignal` read in `Render()` (granular re-render) | `LayoutDirty` |
| A derived value that's expensive | `UseComputed(() => …)` (reactive, cached `Memo<T>`) or `UseMemo(factory, deps)` (dep-array) — recompute lazily, not every read | n/a |
| A long / large list (thousands of rows) | `Virtual.List` / `Virtual.ListBound` / `Repeater.ItemsRepeater` with a stable key — only the visible window is realized, rows recycle | structural, bounded |
| A dynamic list / conditional that *isn't* huge | `Flow.For` / `Flow.Show` — restructure one boundary with **no parent re-render** | structural, one boundary |
| A deep component whose updates shouldn't relayout the page | wrap it in a fixed-size `ClipToBounds` boundary (above) | firewalls `LayoutDirty` |
| Motion / enter-exit / hover lift | animation hooks or `BoxEl.Animate` (FLIP) — composited, no re-render | `TransformDirty` |
| Passing data parent→child | a **signal** or **context**, never a constructor arg (frozen at mount) | — |

`UseComputed` deserves a callout because it is the cheap fix for "my derived value recomputes too often". It is the Solid `createMemo`: a `Memo<T>` that tracks the signals its function reads, caches the result, and notifies *its* subscribers only when the cached value actually changes — so a chain of derived values doesn't fan out spurious work, and the host that reads it through a bind never re-renders:

```csharp
var a = UseSignal(2);
var b = UseSignal(3);
var product = UseComputed(() => a.Value * b.Value);   // Memo<int>: cached, lazy, recomputes when a or b change

new TextEl("") { Text = Prop.Of(() => $"{a.Value} × {b.Value} = {product.Value}") };   // binds through the memo
```

`Flow.For` and `Flow.Show` are the structural counterpart to a binding — a bind updates one node's *property*; a boundary updates the *set of children* at one position, both without re-rendering the surrounding component. `Flow.For(() => items, x => x.Id, (x, i) => …)` diffs rows by key (moves preserve row state); `Flow.Show(() => when, then, @else)` mounts/unmounts one branch. (Their thunks subscribe via `.Value` exactly like a bind.) Full treatment: [bindings page → reactive control-flow](./signals-components-and-bindings.md#3-reactive-control-flow--flowfor--flowshow).

## Measuring — `FrameStats`, the ambient HUD, and the diag flags

Do not eyeball performance. The engine hands you the exact numbers it uses internally.

### `FrameStats` (returned from every frame, and published as an ambient context)

`AppHost.RunFrame()`/`Paint()` returns a `FrameStats` (`src/FluentGpu.Engine/Hosting/AppHost.cs`). The three fields you assert on:

- **`Rendered`** — `false` on a steady or **compositor-only** frame. A bound-scalar drag should keep this `false`; if it flips `true` every frame, something is re-rendering when it should be binding.
- **`ComponentsRendered`** — how many components re-rendered this frame. After a localized interaction it should be **small (ideally 1)**. A surprisingly large number means state lives too high, or a parent is re-rendering children it shouldn't.
- **`HotPhaseAllocBytes`** — managed bytes allocated in the paint half. **`0`** on steady frames means the zero-alloc contract holds. Non-zero on a steady or scroll frame is a bug — find the allocation.

The same struct is published as the ambient `FrameDiagnostics.Current` context (only while something subscribes, so it is free when idle), so you can build an in-app perf overlay from the very numbers you'd assert in a test:

```csharp
var stats = UseContext(FrameDiagnostics.Current);   // last frame's FrameStats
return new TextEl("")
{
    Text = Prop.Of(() => $"render={stats.Rendered}  comps={stats.ComponentsRendered}  alloc={stats.HotPhaseAllocBytes}  {stats.Fps:0} fps"),
};
```

### Environment flags (stderr / on-screen, no profiler)

All gated through `Diag.EnvFlag` (`src/FluentGpu.Engine/Foundation/Diag.cs`); set them in the environment before you run:

| Flag | Effect |
|---|---|
| `FG_DUMP=1` | dump the **post-layout scene tree** to stderr (bounds, opacity, fill, `clip`/`scroll`/hidden markers) — the first thing to run when "content vanished" or a boundary has the wrong size. `FG_DUMP=all` re-dumps on every reconcile. |
| `FG_DIAG=1` | enable engine diagnostics (also turns on the on-screen HUD and the D3D memory log). |
| `FG_HUD=1` | the on-screen diagnostics HUD (fps / frame-ms / draw + cull counts) without the rest of `FG_DIAG`. |
| `FG_ALLOC_DIAG=1` | **once-per-second per-phase allocation + CPU attribution** to stderr — `pump / dispatch / flush / layout / anim / record / submit / effects` KB/s and ms. This is the tool for pinning a `HotPhaseAllocBytes > 0` to the exact phase (or a worker thread) without a profiler. |
| `FG_SCROLLLOG=1` | trace the scrollbar / scroll offsets (the `[scroll]` lines). |

### The harness (CI, headless, seconds)

After any reactivity/layout/render change, run the cross-seam golden harness — ~60 checks, no GPU or window:

```text
dotnet build src/FluentGpu.VerticalSlice    # clean
dotnet run   --project src/FluentGpu.VerticalSlice    # must print "ALL CHECKS PASSED"
```

If you touched a binding/render path, add a `Check` that asserts the `FrameStats` you expect on the relevant interaction — that is how you *prove* a binding stayed compositor-only rather than quietly re-rendering (checks #59 and #60 are exactly these assertions). Evidence before assertions.

## Pitfalls — symptom → cause → fix (quick scan)

Scan the **Symptom** column for what you're seeing. The full performance + layout pitfall tables (and reactivity, lifecycle, workflow) live in [pitfalls.md](../../guide/pitfalls.md) — this is the performance-shaped subset.

| Symptom | Cause | Fix |
|---|---|---|
| Dragging a slider tanks FPS | a `setState` per pointer-move re-renders the owning component every frame | `Slider.Create(FloatSignal)`, or hand-bind the value to a `Transform`. Confirm `FrameStats.Rendered == false` on drag. |
| A small change relayouts the whole page | no layout boundary above the change → the up-walk reaches the root → full layout | give the enclosing container explicit `Width`+`Height`+`ClipToBounds = true` so it's a boundary. |
| Whole app re-renders on one interaction | state lives too high (at the root), so the root's render-effect runs | move state **down** into the component that owns it; or bind the hot value instead of `setState`. |
| `ComponentsRendered` is much larger than 1 after a leaf change | state is too high, or a value passed by constructor forces a wider re-render | keep state local; pass parent→child data via a signal/context, not a constructor arg. |
| `HotPhaseAllocBytes > 0` (zero-alloc check fails) | allocation inside a bind thunk or hot effect body (`new`, LINQ, boxing, per-call closure) | capture everything once at mount; the thunk must only read + write existing state. Pin the phase with `FG_ALLOC_DIAG=1`. |
| A virtualized list allocates on every scroll frame | a per-item closure or per-item modifier rebuilt on each realize; a per-item lambda calls `new`/LINQ | vary per-item chrome through **values only** (pure-value `Prop.Of` thunks); keep the recycled-row structure stable (no per-item add/remove). |
| List with thousands of items is slow / leaks nodes | not virtualized, or no stable key | `Virtual.List`/`Virtual.ListBound`/`Repeater.ItemsRepeater` with a stable key — only the window is realized, rows recycle. |
| Scroll re-realizes/relayouts every frame | (rare) something marks the viewport dirty each frame — usually the list *component* re-rendering | move the list's state out / bind it; in-window scroll is transform-only by design. |
| A binding never updates | the thunk read `.Peek()` (no subscribe), or read a plain field, not a signal | read `.Value` inside the thunk so the binding effect subscribes. |
| A static `OffsetX`/`Opacity`/`Fill` you set "snaps back" each frame | an animation **or a bound** `Transform`/`Opacity`/`Fill` owns that channel; the reconciler won't also write the static value (so they don't fight) | pick **one** owner per channel — drive the value through the bind/animation, not both. |

## See also

- **[Signals, components, and bindings](./signals-components-and-bindings.md)** — the model this page optimizes: signals, the three update mechanisms, `Prop<T>`, the state hooks. Read it first.
- **[Building apps with FluentGpu](./index.md)** — the 60-second cheat sheet and the 10 rules that prevent 90% of bugs.
- **Guide — [rendering-and-performance.md](../../guide/rendering-and-performance.md)** — the canonical frame-pipeline + scoped-relayout + zero-alloc treatment this page is grounded in.
- **Guide — [pitfalls.md](../../guide/pitfalls.md)** — the complete symptom → cause → fix catalogue.
- **Design corpus** (architecture source-of-truth, canon-gated): [SPEC-INDEX.md](../../../design/SPEC-INDEX.md) for the zero-alloc contract and precedence/ownership, and [subsystems/reconciler-hooks.md](../../../design/subsystems/reconciler-hooks.md) for the as-built reactive runtime.
