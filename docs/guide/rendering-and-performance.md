# Rendering pipeline & performance

[← Guide index](./README.md)

## How a change becomes pixels

You return an immutable `Element` tree. The **reconciler** patches it into the retained **`SceneStore`** (a
struct-of-arrays tree of `RenderNode`s). The **layout** engine fills each node's bounds. The **recorder** walks the
scene into a `DrawList` (POD command stream), which the GPU backend submits and presents. Transform/opacity changes
re-record cheap world transforms — no relayout, no re-diff. That's the "composited like a browser" model.

### The frame loop (`AppHost.RunFrame` → `Paint`)

```
1  pump            window → input event ring
2  input dispatch  hit-test, gestures; handlers write signals (schedule effects)
3  reactive flush  ReactiveRuntime.Flush(): re-render dirty components + run bindings   ← the "render+reconcile" phase
   (re-realize virtual windows that crossed a scroll boundary)
6  layout          full Run only on first-frame/resize/DPI/root change; else SCOPED relayout of dirty subtrees
6.5 layout effects UseLayoutEffect (Bounds valid) — seeds animations on nodes
7  animation       AnimEngine.Tick: write transform/opacity/presented-size; FLIP projections (PARENT-RELATIVE: an
                   ancestor reflow moves Animate-nodes rigidly, only LOCAL moves project); SizeMode.Reflow writes the
                   interpolated LAYOUT size + re-solves its boundary scope, so siblings reflow smoothly; smooth scroll
8  record          SceneRecorder walks scene → DrawList (clips, world transforms, glyphs, images)
10 submit          DrawList → GPU command list
11 present         swapchain commit
12 passive effects UseEffect
```

A frame whose only work was transform/paint **binding** writes does **nothing in 3/6** — it skips render, reconcile,
and layout, and just re-records (`FrameStats.Rendered == false`). That's the compositor bypass.

### The retained scene (`SceneStore`, `src/FluentGpu.Engine/Scene/`)

- SoA columns indexed by a generational **handle** `{u32 index, u32 gen}` (a stale handle is detected via `gen`).
- Topology via parent/child/sibling index columns (O(1) navigation). Per-node `Bounds` (local rect), `Paint`
  (`LocalTransform`/`Opacity`/`Fill`/`Text`/…), `LayoutInput` (flex config), and `NodeFlags` (the dirty axes +
  state bits). Sparse side-tables (keyed by node index) hold scroll/grid/shadow/gradient/acrylic/measure-cache rows.
- The recorder composites: `world = parent ∘ translate(pos) ∘ LocalTransform`, cumulative opacity, clip intersection.

## The three dirty axes

Updates mark one of three orthogonal `NodeFlags`, which decide how cheap the frame is:

| Flag | Set by | Frame does |
|---|---|---|
| `TransformDirty` / `PaintDirty` | transform/opacity/fill bind, animation tick, hover/press | re-record only — **no layout** |
| `LayoutDirty` | a component re-render, a width/height/text bind, a structural change | **scoped relayout** of the affected subtree |
| (structural) | mount/remove/reorder in reconcile | reconcile that subtree + scoped relayout |

## Granular re-render

A `setState`/signal write schedules **only the owning component's render-effect** (not the app). On flush it
re-renders that component, reconciles its single subtree, and marks it `LayoutDirty`. Sibling components don't run.
Proof: harness check #59 (`componentsRendered == 1` after a leaf setState; sibling/parent counts unchanged). The cost
is O(that component's subtree), not O(app).

## Scoped relayout

Full-tree layout runs only on first frame, window resize, DPI change, or a root structural change. Otherwise layout
is **O(change)**: `SceneStore` keeps a `LayoutDirty` worklist; `LayoutInvalidator` (`src/FluentGpu.Engine/Layout/`) walks each
dirty node **up to the nearest layout boundary** and re-solves only that subtree (`FlexLayout.RunSubtree`).

A **layout boundary** is a node whose own size cannot change because of a descendant, so the walk stops there and the
parent is never disturbed. The predicate (live form of `layout.md §4.3`):

```
boundary  ⟺  Width and Height are both explicit (not NaN/auto)
           ∧ FlexGrow == 0 ∧ FlexShrink == 0
           ∧ ClipToBounds == true                  (or: the node is a scroll/virtual viewport, or the root)
```

> **This is the firewall that keeps a deep `setState` from relayouting the page.** Give your big containers — cards,
> panes, list rows, fixed regions — an explicit `Width`+`Height` and `ClipToBounds = true` so a change inside them
> stops at their boundary. Without a boundary, the up-walk reaches the root and you fall back to a full layout (still
> correct, just not scoped).

A self-invalidating **text measure cache** (per node, keyed by text+style+available-width) lets a scoped relayout skip
re-shaping unchanged text leaves on the real DirectWrite path.

## The compositor bypass (the slider tank, killed)

High-frequency scalars (slider scrub, scroll offset, progress, hover glow) should be **bound to a transform/opacity**,
not pushed through `setState`. A bound write updates one scene-node matrix and marks `TransformDirty` — the frame skips
render/reconcile/layout and just re-records. Proof: harness check #60 — a bound slider drag moves the thumb
(`thumbDx 51→131`) with `renders+0 rendered=False`.

```csharp
var v = UseFloatSignal(0.5f);
Slider.Bind(v);                                   // built-in: thumb offset + fill scale are bound Transforms
// or hand-rolled:
new BoxEl { /*…*/ Transform = Prop.Of(() => Affine2D.Translation(v.Value * trackW, 0f)) };
```

If a value is *also* shown as text ("70%"), let that small label take a scoped re-render (read `v.Value` in a tiny
component, or `Text = Prop.Of(() => $"{v.Value:P0}")`), while the thumb stays on the compositor path — the two are decoupled.

## Zero-allocation discipline (phases 6–13)

The paint half must not allocate managed memory (`FrameStats.HotPhaseAllocBytes == 0`, asserted by the harness). To
stay clean:

- Wire bindings/effects **once at mount**; never `new`/box/LINQ inside a bind thunk or hot effect body.
- Reuse buffers; the engine uses `stackalloc`, pooled arrays, and native-backed arenas on the hot path.
- A signal `set` is allocation-free (subscribers are a pre-sized list). Structural reconcile (mount/remove) may use
  bounded Gen0 — that's the reconcile edge (phase 3–5), not the paint half.

## Optimization decision guide

| Situation | Do this |
|---|---|
| A value changes many times/second (drag, scroll, progress, animation) | bind it (`Transform`/`Opacity` = a Func/signal, compositor-only), or use the animation hooks (`UseSpring`/`UseTransition`) — never `setState` per tick |
| A value that changes occasionally and is shown as text/affects layout | `UseState`/`UseSignal` + read it in render (granular re-render) |
| A long/large list | `Virtual.*` / `Repeater.ItemsRepeater` with a stable `keyOf` (virtualized) |
| A dynamic list/conditional that *isn't* huge | `Flow.For` / `Flow.Show` (restructure with no parent re-render) |
| A deep component whose updates shouldn't relayout the page | wrap it in a fixed-size `ClipToBounds` boundary |
| Motion / enter-exit / hover lift | animation hooks or `BoxEl.Animate` (FLIP), `UseEntrance`/`UseHoverScale` — composited, no re-render |
| Derived value that's expensive | `UseComputed` (reactive, cached) or `UseMemo(deps)` (dep-array) |
| Passing data parent→child | a **signal** or **context** — never a constructor arg (frozen at mount) |

## Measuring

Read `FrameStats` from `RunFrame()`:
- `Rendered == false` on a steady or compositor-only frame → good (no wasted render/layout).
- `ComponentsRendered` should be small (ideally 1) after a localized interaction.
- `HotPhaseAllocBytes == 0` on steady frames → the zero-alloc contract holds.
- Set `FG_DUMP=1` to dump the post-layout scene tree to stderr; `FG_DIAG=1` enables engine diagnostics;
  `FG_SCROLLLOG=1` traces the scrollbar.

Next: **[pitfalls.md](./pitfalls.md)** — symptom → cause → fix.
