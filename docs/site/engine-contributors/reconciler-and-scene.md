# Reconciler and the retained scene

[← Contributing to the engine](./index.md)

This page is for engine contributors. It explains how the **reconciler** (`src/FluentGpu.Engine/Reconciler/Reconciler.cs`)
turns an immutable `Element` tree into edits on the retained **struct-of-arrays** scene
(`src/FluentGpu.Engine/Scene/SceneStore.cs`), where exactly to change each behavior, and how to prove a change with the
headless harness. It is the working view; the architecture authority is the design corpus — semantics live in
[`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md), storage lives in
[`scene-memory.md`](../../../design/subsystems/scene-memory.md), and the canonical contract values live in
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md). Those docs own the column shapes and the seam declaration; **this
page never restates them — it links to them.**

If you have not read [rendering-and-performance.md](../../guide/rendering-and-performance.md) yet, read it first. It
gives the frame loop and the three dirty axes from above; this page is the same machinery seen from inside the
reconciler.

## Element tree → reconciler → retained SoA SceneStore

You author an immutable tree of `Element` records (`src/FluentGpu.Engine/Dsl/Element.cs`). The reconciler's job is to make
the retained `SceneStore` *match* that tree with the smallest possible set of edits, and to wire the reactive plumbing
(render-effects, bindings, control-flow boundaries, context signals) so that future changes never have to walk the
whole tree again.

The shipped reconciler is **signals-first** (Solid-style), not React-style re-render-from-root. There is no full-tree
re-render and no global dirty flag. Concretely (`TreeReconciler`):

- **A component is a reactive render-effect.** `MountComponent` wraps `RunComponent` in an `Effect`; the effect
  auto-subscribes to whatever signals/context the component reads during `Render()`. A `setState`/signal write
  schedules **only that component's** render-effect, and on the next `ReactiveRuntime.Flush` (frame phase 3) it
  re-renders and reconciles **just its own subtree** — `ReconcileSingleChild(node, newRendered, entry.Rendered)`.
  Sibling components don't run.
- **A reused component on a parent re-render is a no-op.** In `Update`, when the old and new `ComponentEl` share a
  `ComponentType` and the node is already mounted, the reconciler `return`s immediately — the component is autonomous,
  carrying its props through signals/context rather than the factory closure. (This is why parent→child data flows
  via signals/context, **never** constructor args frozen at mount.)
- **Bindings and control-flow are effects too.** A bound `Prop<T>` channel, a `ShowEl`, and a `ForEl` each become an
  `Effect` registered on the node and disposed when the node unmounts.

The reconciler talks to the store through exactly one narrow seam, `ISceneBackend` — handle-in / handle-out,
POD-only, zero COM. The store implements it directly (`SceneStore : ISceneBackend`). The interface is co-owned with
[`reconciler-hooks.md` §2](../../../design/subsystems/reconciler-hooks.md); the store-side implementation is owned by
[`scene-memory.md`](../../../design/subsystems/scene-memory.md).

> **Note on the doc's broader algorithm.** `reconciler-hooks.md` describes a 4-phase keyed-LIS child diff, a lane
> model, `Suspense`, and a richer `ISceneBackend` (with `MoveChild`/`WriteVisual`/mask diffs). Those are the **design
> target**; the as-built runtime (`§0bis`) is the simpler signals-first form this page documents. When you read the
> two, trust the source for what ships and the doc for where it is going.

## The generational handle `{u32 index, u32 gen}` and topology columns

Every node is a `NodeHandle` wrapping a `Handle = {u32 Index, u32 Gen}` (8 bytes). The `Index` is the slot into every
parallel column; the `Gen` is bumped on **both** allocate and free, so a stale handle (a captured effect closure, an
in-flight reference) fails `IsLive` instead of silently reading a recycled node. This is the ABA defense; the byte
layout is canonical and owned by [`SPEC-INDEX.md` §2](../../../design/SPEC-INDEX.md) (do not re-derive it).

`SceneStore.IsLive` is the one validation every effect body performs before touching the store — you will see
`if (_scene.IsLive(node))` guarding **every** binding effect in `BindNode`, because a bound effect can fire after its
node was freed (the signal outlived the node).

Topology is a doubly-linked sibling list held in `int` index columns (`_parent`, `_firstChild`, `_lastChild`,
`_prevSib`, `_nextSib`, `_childCount`; `0` = none). That makes navigation O(1) (`FirstChild`/`NextSibling`/`Parent`)
and reorder O(1) pointer surgery (`Detach` then `AppendChild`) with the node's handle and all other columns
untouched — the structural win over re-realizing a control. The free-list reuses freed slots (`_freeHead` /
`_nextFree`); `CreateNode` pops a slot and stamps a fresh generation, `FreeSubtree` frees children-first then bumps
the slot's generation and pushes it back.

**Where to change:** node lifecycle, topology, and the generation/free-list spine are all in
`src/FluentGpu.Engine/Scene/SceneStore.cs` (`CreateNode`, `FreeSubtree`, `AppendChild`, `Detach`, `IsLive`). The handle type
itself is in `FluentGpu.Engine` (`Foundation/`).

## Keyed child diff (identity and state preservation by key)

The keyed child diff is the **structural engine** retained underneath everything else — it runs on a component
re-render and behind `Flow.For`/`Flow.Show`. It lives in `TreeReconciler.ReconcileChildren`. It is a positional+type
diff with key matching, not the full LIS the design doc targets, but it preserves the two properties that matter:

- **Keyed children match by `Key`** (the `Element.Key` string), so a reorder reuses the existing node — identity and
  all attached state survive the move. For large child sets (`oldN > 32`) it builds a `Dictionary<string,int>` key map;
  below that it scans. A matched node is `Update`d in place; an unmatched new child is `CreateNode` + `Mount`ed; an
  old node whose key disappeared is `Remove`d.
- **Unkeyed children match positionally** by index (same slot, same `ElementTypeId`).

After matching, every surviving child is `Detach`ed and re-`AppendChild`ed in the new order, so document order in the
scene matches the element order. The diff marks the container `LayoutDirty` when the child set changed **or** when a
*pure reorder* happened (same keys, different order — detected by non-monotonic match order, the `moved` flag):

```csharp
// TreeReconciler.ReconcileChildren — the relayout trigger
if (structural || moved || newN != oldN) _scene.Mark(node, NodeFlags.LayoutDirty);
```

That `moved` branch is load-bearing: a list reverse creates and removes nothing, but the rows still have to move, so a
pure reorder must still relayout. (The harness pins this — see `FlowReorderChecks` below.)

**Where to change:** `TreeReconciler.ReconcileChildren` for the structural diff; `ReconcileSingleChild` for the
single-optional-child case (component output, provider child, `Show` branch).

## Render-effects, For/Show boundary effects, context provision

These three are all effects registered against a host node and torn down on unmount. They are what make updates
surgical.

**Component render-effect.** `MountComponent` creates the `Component` via the `ComponentEl.Factory`, injects the
`RenderContext` (so hooks resolve the runtime, scene, anim engine, image cache, and context), and wraps `RunComponent`
in an `Effect`. `RunComponent` bails if the node died (`if (!_scene.IsLive(node)) return;`), bumps the render count
(the granularity metric `ConsumeRenderCount()` reads), renders, reconciles its single child, mirrors the child's
layout participation onto the anchor (a component anchor is layout-transparent — `MirrorParticipation`), and marks the
rendered child `LayoutDirty` so a re-render that changed subtree size is re-laid-out:

```csharp
// TreeReconciler.RunComponent (abridged)
if (!_scene.IsLive(node)) return;
_renderCount++;
Element newRendered = comp.RenderWithHooks();   // always tracked; a body that reads no signals never subscribes → run-once
ReconcileSingleChild(node, newRendered, entry.Rendered);
MirrorParticipation(node, _scene.FirstChild(node));
entry.Rendered = newRendered;
var child = _scene.FirstChild(node);
if (!child.IsNull) _scene.Mark(child, NodeFlags.LayoutDirty);
```

A `Component.Render()` that reads no signals is the run-once case — it creates no subscriptions, so it is never
re-scheduled and never re-renders; reactivity there comes purely from bindings / `For` / `Show` inside it.

**`Flow.Show` (ShowEl) and `Flow.For` (ForEl)** are autonomous reactive boundaries (`MountShow` / `MountFor`). Each
registers a boundary `Effect` via `AddBinding(node, eff)`. The `Show` effect re-evaluates `When()` and mounts
`Then`/`Else` through `ReconcileSingleChild`; the `For` effect re-evaluates `Count()`/`ItemAt(i)`, keys each row, and
feeds the result to `ReconcileChildren` — so add/remove/reorder go through the same keyed diff with **no parent
re-render**. The `Update` path for both is a `return` (a parent re-render must not disturb them — the effect owns its
subtree). `ShowEl`/`ForEl` are defined in `src/FluentGpu.Engine/Hooks/ControlFlow.cs` (the `Flow.*` factory); the engine
records carry `ElementTypeId` 7 and 10.

**Context provision** (`ContextProviderEl`, `ElementTypeId` 4) stores a `Signal<object?>` per provider node
(`MountProvider` writes `_providerSig[(int)node.Raw.Index]`). A consumer's `UseContext` resolves the nearest provider
by walking ancestors and subscribing — that walk is `ResolveContext`, which climbs `_scene.Parent(...)` looking for a
matching channel, then falls back to host-published ambient signals (`Viewport.Size`, etc.) in `_ambient`. On a
provider re-render with the same channel, `Update` writes the signal's value (`e.Sig.Value = np.Value`), which notifies
**exactly** the subscribed consumers — no context-stack reconstruction, no prop drilling. `Context<T>`/`Ctx.Provide`
live in `src/FluentGpu.Engine/Hooks/Context.cs`.

## Bindings as mount-time effects (the `Prop<T>` wiring)

Every bindable channel is **one** `Prop<T>` taking a value, a `Func<T>` thunk, or a concrete signal (the canonical
surface is owned by [`SPEC-INDEX.md` §2 "Reactive element-prop surface"](../../../design/SPEC-INDEX.md)). The
reconciler wires a **bound** channel into an effect exactly once, at mount, in `TreeReconciler.BindNode`. Each effect
reads whichever payload the channel carries (thunk or signal-direct — one null test per fire), writes **one** scene
column, and marks the matching dirty axis. For example, a bound `BoxEl.Transform`:

```csharp
// TreeReconciler.BindNode — the compositor-bypass path
if (b.Transform.IsBound)
{
    var tb = b.Transform.Thunk; var ts = b.Transform.Signal;
    AddBinding(node, new Effect(Runtime, () =>
    {
        if (_scene.IsLive(node))
        {
            _scene.Paint(node).LocalTransform = tb is not null ? tb() : ts!.Value;
            _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }, owner: null, runNow: true));
}
```

Two contracts make this correct, and you must preserve both when you add a channel:

1. **Bind wiring is mount-only.** A fresh thunk/signal supplied on a re-render is ignored — the signals-first rule is
   *change the signal's value, not the bind* (`bind.mount-only.stale`). `BindNode` runs from `Mount`, never from
   `Update`.
2. **The static value is re-asserted on every reconcile only when `!IsBound`.** In `WriteColumns` you will see this
   guard on every bound channel — e.g. `if (!b.Opacity.IsBound) paint.Opacity = b.Opacity.Value;` and
   `if (!b.Width.IsBound) li.Width = b.Width.Value;`. This is the single chokepoint that fixed the historical Opacity
   `!= 1f` reappear bug and prevents a re-render from clobbering a bound/animated value back to its static.

The dirty axis the bind marks decides how cheap the frame is: `Transform`/`Fill`/`Opacity`/`Color` mark
`TransformDirty`/`PaintDirty` (re-record only — the **compositor bypass**), while `Width`/`Height`/`Text` mark
`LayoutDirty` (scoped relayout). The bound channels today are `BoxEl` `Transform`/`Opacity`/`Fill`/`Width`/`Height`,
`TextEl` `Text`/`Color`, and `ImageEl` `Source`/`Placeholder` — all wired in `BindNode`, all guarded in
`WriteColumns`.

**Where to change:** add the bind effect in `TreeReconciler.BindNode`, add the `!IsBound` static-value guard in the
matching `WriteColumns` arm, and add the `Prop<T>` field to the element record in `src/FluentGpu.Engine/Dsl/` (the record
shape is owned by [`dsl-aot.md`](../../../design/subsystems/dsl-aot.md)). Add a `Check` that asserts the channel
updates one node with `Rendered == false` (compositor path) or with a scoped relayout (layout path).

## The scene columns (Bounds, Paint, LayoutInput, NodeFlags) and sparse side-tables

The store is struct-of-arrays: one spine indexes parallel `T[]` columns, each touched by a *disjoint* set of phases so
a phase streams only the cache lines it needs. The exact column shapes are owned by
[`scene-memory.md` §2.2](../../../design/subsystems/scene-memory.md) and defined in `src/FluentGpu.Engine/Scene/Columns.cs` —
**read them there; this page does not restate the structs.** In brief, the dense per-node columns the reconciler
writes are:

| Column | Type (in `Columns.cs`) | What the reconciler writes | Read by |
|---|---|---|---|
| `Bounds` | `RectF` (LOCAL) | nothing — layout owns it | record, hit-test, `AbsoluteRect` |
| `Paint` | `NodePaint` | `Fill`/`LocalTransform`/`Opacity`/`Corners`/`TextColor`/`Text`/`ImageId`/`VisualKind` | animation (writes transform/opacity), record |
| `LayoutInput` | `LayoutInput` | flex config: `Width`/`Height`/`Direction`/`Gap`/`Padding`/`Margin`/`FlexGrow`/… and `TextStyle` | layout |
| `Interaction` | `InteractionInfo` | `HandlerMask`/`Role`/`Cursor`/`Focusable`/accelerators | hit-test, focus, UIA |
| `Flags` | `NodeFlags` | dirty axes + state bits (`ClipsToBounds`, `WantsPointer`, `Focusable`, …) | every phase pre-filters here |

Most visual richness is **sparse side-tables keyed by node index** — there are O(decorated nodes) of these, not one
per node, so a plain leaf costs nothing. The reconciler writes them through `Set*`/`Clear*` pairs in `WriteColumns`:
scroll/virtual state (`_scroll`), grid specs (`_grids`), shadows, arcs, gradients, border brushes, acrylic, the
eased-interaction row (`_interact`), the text measure cache (`_measureCache`), implicit brush transitions
(`_brushAnims`), span-text runs (`_spanText`), text-edit decoration state, and the drag-source/drop-target specs.
Every one of these is removed in `FreeSubtree`, so unmounting a node reclaims its side-table rows.

`WriteColumns` is the big switch (one arm per element type — `BoxEl`, `ScrollEl`, `VirtualListEl`, `GridEl`,
`PolylineStrokeEl`, `ImageEl`, `TextEl`, `SpanTextEl`). It ends with the universal "this was an update, not a mount"
tail:

```csharp
if (!isMount) { _scene.Mark(node, NodeFlags.PaintDirty); _reconciled = true; }
```

**Where to change:** column writes are `TreeReconciler.WriteColumns`; the column/side-table storage and accessors are
`src/FluentGpu.Engine/Scene/SceneStore.cs` + `Columns.cs`. If you add a new per-node visual, prefer a sparse side-table
(mirror an existing `Set/TryGet/Clear` trio and add its `Remove` to `FreeSubtree`) over widening a dense column — the
dense `NodePaint` is deliberately one cache line.

## The `Mutate()` epoch chokepoint and clean-span validity

The design corpus specifies a single per-realization mutation chokepoint (`Mutate()`) that bumps a content-epoch and a
`CleanSpanWitness` (handle + gen + content-epoch + baked-geometry hash) that validates whether a recorded DrawList
span can be reused. That rule — *a memcpy'd clean span is valid iff every referenced handle `IsLive` **and**
content-epoch unchanged **and** baked geometry matches* — is canonical and owned by
[`SPEC-INDEX.md` §2 "Clean-span validity"](../../../design/SPEC-INDEX.md) and
[`scene-memory.md` §4.4](../../../design/subsystems/scene-memory.md). It is the design-stage hardening for the
render-thread seam.

In the as-built single-thread store the same intent shows up as **content-epoch-by-id discipline**, which you must
respect when you write columns:

- **Text/string ids carry a refcount.** `SetPaintText` interns the new id, `AddRef`s it, and `Release`s the old, so a
  streamed virtual-list string is reclaimed once no live node shows it. A text column write that bypasses
  `SetPaintText` leaks the id.
- **Span runs are keyed by id, and minting a fresh id *is* the cache invalidation.** `WriteColumns`' `SpanTextEl` arm
  only re-registers the shaping overlay when a shaping input actually changed (`SameSpanShaping`); an identical
  re-render keeps the run id, so the measure cache and the renderer's shaped-run cache are never churned.
- **Generation is the liveness epoch.** Because freed slots bump generation, a captured `NodeHandle` that survives a
  free fails `IsLive` — which is why every binding effect re-checks it. When you add an effect that captures a node,
  guard its body with `IsLive`.

When the parallel render-thread seam lands (build-order step 2 — see
[`SPEC-INDEX.md` §2 "Threading / frame phases"](../../../design/SPEC-INDEX.md)), the `Mutate()`/epoch/witness machinery
becomes the formal version of this discipline. Until then, treat "intern + refcount + keep the id stable when nothing
changed" as the rule.

## The `ISceneBackend` op set

`ISceneBackend` (top of `src/FluentGpu.Engine/Scene/SceneStore.cs`) is the **only** surface the reconciler uses to mutate the
retained tree — handle-in / handle-out, POD-only, no `ComPtr`, no `UIElement`, no `System.Object` across it. That
narrowness is what keeps the reconciler platform-independent and lets the render thread own every `ComPtr` two layers
down. The as-built interface is:

```csharp
public interface ISceneBackend
{
    NodeHandle CreateNode(ushort elementTypeId);
    void FreeSubtree(NodeHandle node);
    void AppendChild(NodeHandle parent, NodeHandle child);

    ref LayoutInput Layout(NodeHandle node);
    ref NodePaint Paint(NodeHandle node);
    ref InteractionInfo Interaction(NodeHandle node);
    void SetClickHandler(NodeHandle node, Action? handler);
    Action? GetClickHandler(NodeHandle node);

    void Mark(NodeHandle node, NodeFlags flags);

    NodeHandle FirstChild(NodeHandle node);
    NodeHandle NextSibling(NodeHandle node);
    int ChildCount(NodeHandle node);
}
```

`SceneStore` implements this plus the wider concrete surface the reconciler actually calls (`Detach`, `Parent`,
`ScrollRef`, the `Set*`/`TryGet*`/`Clear*` side-table pairs, `Orphan`/`ReclaimOrphan` for exit animations). The
*interface declaration* is co-owned with [`reconciler-hooks.md` §2](../../../design/subsystems/reconciler-hooks.md);
the *store-side implementation* — the columns those ops write, the growth-safe `ref` returns, the free-list — is owned
by [`scene-memory.md`](../../../design/subsystems/scene-memory.md). The richer doc surface (`MoveChild`,
`WriteVisual`/`WriteLayout` mask diffs, `NodeChildCollection`) is the design target, not yet wired.

**Where to change:** the interface and the `SceneStore` implementation are both in `src/FluentGpu.Engine/Scene/SceneStore.cs`.
Keep it POD-only — a GC reference crossing this seam (beyond the handler-column `Action`s, which live in side arrays
at the edge) breaks the render-thread-confinement contract.

## Verifying: the headless harness

Verify every reconciler change with the headless harness, never by eye — `src/FluentGpu.VerticalSlice/Program.cs`:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

The reconciler/scene behavior on this page is already pinned by named check groups in `Program.cs`. Read them as
executable spec, and add a `Check("…", condition, detail)` next to the closest one when you change behavior:

- **`KeyedChecks`** — keyed reorder preserves node identity (`[a,b,c] → [c,a,b]` reuses the same handles) and removal
  drops only the vanished key. Drives `ReconcileRoot` directly with `BoxEl { Key = … }` children.
- **`FlowChecks`** — `Flow.For`/`Flow.Show` restructure the tree with **no parent re-render** (asserts
  `FlowProbe.Renders == r0` across a count change and a toggle). This is the boundary-effect autonomy contract.
- **`FlowReorderChecks`** — a *pure* `Flow.For` reverse (same keys, same count) preserves the key-matched node **and**
  relayouts it (the `moved` branch in `ReconcileChildren`); asserts the row's bounds actually moved while parent
  renders stayed flat.
- **`EnterExitChecks`** — exit-animation orphaning: removing an animated child keeps it live and drawing
  (`scene.IsOrphan` + `IsLive`), then `ReclaimOrphan` deferred-frees it once the fade settles (the generation bump
  makes the handle dead). This is the `Remove` → `Orphan` path in the reconciler plus `SceneStore`'s orphan list.
- **`NestedChecks`** — a nested component mounts, owns its own state, and a click re-renders only it (`child 0` →
  click → `child 1`) — granular re-render across a component boundary.
- **`ContextChecks`** — a provider value reaches a nested consumer and a change propagates on re-render (`ctx 7` →
  `ctx 8`) — `MountProvider` + `ResolveContext` + the per-provider signal.

When you touch reactivity or the scene, also assert on the `FrameStats` returned by `AppHost.RunFrame()`:

- `Rendered == false` on a steady or compositor-only frame (a bound `Transform`/`Opacity`/`Fill`/`Color` write did no
  reconcile/relayout).
- `ComponentsRendered` small (ideally 1) after a localized interaction — the granularity proof; mirrors
  `TreeReconciler.ConsumeRenderCount()`.
- `HotPhaseAllocBytes == 0` on a steady frame — the near-zero-allocation guarantee. Structural reconcile (mount/remove)
  may use bounded Gen0; that is the reconcile edge (phases 3–5), **not** the paint half.

The harness asserts *what was drawn* (the `HeadlessGpuDevice` records the DrawList) and post-layout bounds via
`host.Scene` — it does **not** assert on-screen D3D12 pixels. "Green harness" means the logic and recorded DrawList are
correct, not that a control looks right on screen; that is a separate manual needs-pixels pass on the Windows path.

## Real example: the patterns the reconciler diffs

These are mined from the gallery (`src/FluentGpu.WindowsApp/GalleryPages.cs`) — the real authoring surface the
reconciler consumes. They are what `Flow.For`, `Flow.Show`, `Ctx.Provide`/`UseContext`, and the bound virtual-row
recycler look like in practice.

A keyed reactive list (`Flow.For`) and a reactive conditional (`Flow.Show`), neither of which re-renders the enclosing
component when the signal changes:

```csharp
var items = UseSignal(new List<string> { "Alpha", "Beta", "Gamma" });

Flow.For(() => items.Value,
         x => x,
         (x, i) => Row(x));   // keyed: moves preserve row state

// mutate by writing a NEW list instance (signal writes are value-equality gated):
var next = new List<string>(items.Peek()); next.Reverse(); items.Value = next;
```

```csharp
var open = UseSignal(false);

VStack(10,
    Button.Standard("Toggle details", () => open.Value = !open.Peek()),
    Flow.Show(() => open.Value, detailsPanel, fallback));   // boundary effect; host does not re-render
```

Context provision + consumption (the provider stores a signal per node; the consumer subscribes):

```csharp
public static readonly Context<int> ThemeLevel = new(1);   // a channel + default

Ctx.Provide(StatePage.ThemeLevel, level, Embed.Comp(() => new Consumer()));

sealed class Consumer : Component
{
    public override Element Render()
    {
        var x = UseContext(StatePage.ThemeLevel);   // reads + subscribes
        return Ui.Text($"level {x}");
    }
}
```

The bound virtual-list row template — built **once** per visible slot with an index *signal*; scrolling rebinds the
slot by writing the signal, so only the bound `Text`/`Fill`/`Source` thunks re-run (no element rebuild, no reconcile,
no node churn — the recycler fast path `RealizeBoundWindow` drives):

```csharp
static Element Row(FluentGpu.Signals.IReadSignal<int> idx) => new BoxEl
{
    Direction = 0, Height = 48f, Gap = 12f, AlignItems = FlexAlign.Center,
    Fill = Prop.Of(() => (idx.Value % 2 == 0) ? RowEven : RowOdd),
    Children =
    [
        new ImageEl
        {
            Width = 32f, Height = 32f, Corners = CornerRadius4.All(8f),
            Source = Prop.Of(() => Cover(idx.Value)), Placeholder = Prop.Of(() => TileTint(idx.Value)),
        },
        new TextEl("") { Size = 14f, Color = Theme.WindowText, Text = Prop.Of(() => $"Item {idx.Value}") },
    ],
};

// in Render():
Virtual.ListBound(100000, 48f, Row) with { Grow = 1f };
```

`VirtualListEl` (`src/FluentGpu.Engine/Reconciler/VirtualListEl.cs`, `ElementTypeId` 6) is the engine primitive the reconciler
diffs directly. Two realize paths exist in `TreeReconciler`: `RealizeWindow` (keyed/unkeyed rows, recycling
scrolled-out nodes via `ReconcileWindow`) and `RealizeBoundWindow` (the `RowBind` slot recycler above). The
variable-extent path uses the Fenwick `ExtentTable` (`src/FluentGpu.Engine/Scene/ExtentTable.cs`) for O(log n)
offset↔index mapping. The user-facing `Virtual.*` factories live in `FluentGpu.Controls`; the seam is owned by
[`virtualization.md`](../../../design/subsystems/virtualization.md) and
[`SPEC-INDEX.md` §2 "Virtualization seam"](../../../design/SPEC-INDEX.md).

## Canon links: semantics vs storage (never restate column shapes)

Keep the single-owner discipline when you edit anything here:

- **Reconciler + hooks semantics** — render-effects, the keyed diff, effect timing, context propagation, the bind
  contracts: [`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md) (the as-built signals-first model
  is `§0bis`).
- **Scene storage** — the SoA spine, the full column catalogue, the side-table placements, the dirty worklist, the
  `Mutate()` chokepoint, the `ISceneBackend` store-side mechanism:
  [`scene-memory.md`](../../../design/subsystems/scene-memory.md).
- **Canonical contract values** — the handle byte layout, clean-span validity, the `Prop<T>` surface, the threading
  model and quarantine constant, the virtualization seam: [`SPEC-INDEX.md` §2](../../../design/SPEC-INDEX.md), the
  precedence authority.
- **Contract-ownership map** — every column, opcode, seam, and hook → its one owning doc:
  [`subsystems/README.md`](../../../design/subsystems/README.md).

Define an artifact in its owner; everywhere else, reference it. Restating a column struct or an enum in two places is
exactly the drift the canon gate and reviews catch.

---

**Next:** [rendering & performance](../../guide/rendering-and-performance.md) for the frame pipeline and the three
dirty axes · [reactivity.md](../../guide/reactivity.md) for the signals model these internals implement ·
the [file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what) for where to change
what · the engine [index](./index.md) for the assembly graph and the harness golden rule.
