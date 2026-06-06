# FluentGpu — Subsystem Design: Live Devtools / Inspector / Profiler (`FluentGpu.Devtools`)

> **Owning assembly:** **`FluentGpu.Devtools`** — a `<FeatureSwitchDefinition>`-gated assembly behind the
> `FluentGpu.EnableDevtools` feature switch (default **false**, trimmed → **0 bytes in release**). Build/trim/footprint
> placement of this assembly is owned by `dsl-aot.md` §3.4/§3.6; **this doc owns its content** (the inspector / profiler /
> time-travel design). The doc that the gap analysis row **L12** points at — "live devtools / inspector / profiler" —
> previously said *defer (fast-follow)*. **That deferral is removed.** Per the directive (TIER-1 premiere, everything in
> core), this is now a fully-specified, buildable core design.
>
> **The one inviolable rule of this subsystem:** devtools is a **read-mostly observer** built on artifacts the engine
> already produces — the **retained SoA `SceneStore`** (`scene-memory.md`), the **immutable triple-buffered `SceneFrame`**
> (`threading-render-seam.md` §2), the **hook-cell zoo + `ComponentTable`** (`reconciler-hooks.md` §3), and the
> **structural ledger / alloc-tripwire** that validation already wires (`validation.md` §3.1/§3.4). Devtools adds **no new
> production column, no new opcode, no new render-thread work, and no per-frame managed allocation in phases 6–13**. Every
> hook devtools needs is a `[Conditional("FG_DEVTOOLS")]`-erased call site (§9). When the switch is off the JIT/AOT
> trimmer deletes the call sites *and* the assembly.

---

## 0. Where this lives, and what it can touch

`FluentGpu.Devtools` references `FluentGpu.Foundation`, `FluentGpu.Scene`, `FluentGpu.Hosting` (for the publisher seam),
`FluentGpu.Hooks`, and `FluentGpu.Validation` (it reuses the `StructuralLedger` type — §6). It is **referenced only by the
host shell** behind the switch; no production assembly references it. It owns:

| Devtools owns (this doc) | Explicitly NOT owned here (referenced) |
|---|---|
| The **observer protocol** (`IDevtoolsObserver` + the in-process `DevtoolsBus`, §1) and the **transport** (in-proc ring + optional named-pipe wire, §8) | The `EnableDevtools` feature-switch + build/trim/footprint placement → `dsl-aot.md` §3.4/§3.6 |
| The **Element/Component tree inspector** projection (`ComponentTreeView`, §2) — props, hook-cell state, keys, `ComponentAnchor` identity | The hook-cell layout / `ComponentTable` / `RenderContext` shapes → `reconciler-hooks.md` §3 (read-only) |
| **Re-render highlighting** — the 3-signal *cause* readout (`RenderCause`, §3) | The 3-signal memo gate itself (`SelfTriggered‖propsChanged‖HasConsumedContextChanged`) → `reconciler-hooks.md` (canon) |
| The **layout overlay** (Bounds/margin/padding/baseline + the dirty-rect/damage overlay, §4) | `Bounds[]`/`WorldTransform[]`/`LayoutAux` columns + `DamageView` → `scene-memory.md`, `threading-render-seam.md` §2 |
| The **scene / DrawList inspector** (SoA column dump, batch counts, PSO dedup, opcode stream, §5) | The opcode payload shapes → `gpu-renderer.md`; the encoding framework → `scene-memory.md` |
| The **per-phase frame timeline / flamegraph** (QPC spans + per-phase alloc tripwire readout, §6) | The `AllocScope`/alloc-tripwire + `StructuralLedger` → `validation.md` §3.1/§3.4 |
| The **a11y / semantics tree viewer** (`SemanticsTreeView`, §7) | The UIA projection / `A11yPresent` merge → `input-a11y.md` §11 |
| **Time-travel** (snapshot/replay of the update queue, §8.3) | The `UpdateQueueSlab` record layout → `scene-memory.md`; lane semantics → `reconciler-hooks.md` §7 |

**The overlay surface is the engine drawing itself.** The layout/damage/re-render overlays (§4) are not a separate window:
they are drawn by the engine's own renderer into a dedicated **devtools overlay layer** (a z-topmost transient overlay,
geometry per `layout.md` §10) emitted *after* the app's DrawList in phase 8. The inspector **panels** (tree, timeline,
flamegraph, time-travel) are an out-of-process companion fed over the wire (§8) — or, in the simplest config, the same
engine renders them into a side panel. Either way devtools authors UI **using FluentGpu itself** (dogfood); there is no
second UI toolkit in the binary.

---

## 1. The observer protocol — `IDevtoolsObserver` + `DevtoolsBus`

Devtools never reaches into engine internals on its own thread. The engine **pushes** typed, POD-or-immutable records to a
single broker, the `DevtoolsBus`, from the thread that owns the data, at `[Conditional("FG_DEVTOOLS")]` call sites the
engine already crosses. Reuse-of-the-`SceneFrame`-snapshot is the load-bearing protocol decision (§1.2).

```csharp
namespace FluentGpu.Devtools;

// The engine-side seam. ONE instance, owned by FluentGpu.Hosting, null when the switch is off.
// Every method is invoked only from inside a [Conditional("FG_DEVTOOLS")] guard, so it is erased in release.
public interface IDevtoolsObserver
{
    // ── UI thread (phases 3–7, PUBLISH) ──────────────────────────────────────────
    void OnUpdateEnqueued(in UpdateRecordView rec);                 // phase 3 enqueue (time-travel source, §8.3)
    void OnUpdateQueueDrained(in DrainSummary drain);               // phase 3: lanes flushed, batched count (P1/P2a)
    void OnComponentRendered(in RenderEventView ev);                // phase 4: who rendered + the 3-signal CAUSE (§3)
    void OnComponentSkipped(NodeHandle host, uint gen);             // phase 4: memo-skip (for "why NOT" readout)
    void OnReconcileOutcome(ReconcileOutcomeKind kind, int slices); // phase 5: Complete / YieldCarryForward / DiscardRestart
    void OnPhaseSpan(PhaseId phase, long qpcStart, long qpcEnd, long allocDelta); // §6 timeline + tripwire readout
    // ── PUBLISH(13a), UI thread ──────────────────────────────────────────────────
    void OnFramePublished(ulong publishSeq, in SceneFrame frame);   // §1.2: hands the IMMUTABLE snapshot — the spine of §2/§4/§5
    // ── RENDER thread (phases 8–11) ──────────────────────────────────────────────
    void OnDrawListRecorded(ulong publishSeq, in LedgerView ledger);// §5: batch/PSO/opcode counts (the StructuralLedger, §6)
}

// The broker. Lock-free SPSC-per-producer ring; consumed by the devtools panel thread / wire pump (§8).
public sealed class DevtoolsBus : IDevtoolsObserver { /* §8 */ }
```

### 1.1 Thread-confinement (the non-negotiable)

Devtools obeys the same thread map as everything else (`hardened-v1-plan.md` §2). The observer is called **only** from the
thread that owns the datum:

| Datum | Produced on | How devtools reads it without a race |
|---|---|---|
| Component tree, hook cells, props, render cause, update queue | **UI thread** | Pushed at the phase-3/4/5 call site; copied into the bus ring as a **value snapshot** (no live `RenderContext`/`Element` reference escapes the UI thread) |
| `Bounds`/`WorldTransform`/`DamageView`/SoA columns, batch/PSO/opcode stream | via the **immutable `SceneFrame`** | The bus retains a **reference to the published, sealed `SceneFrame`** (§1.2) — already immutable, already render-readable, never mutated after seal |
| Phase QPC spans + alloc deltas | UI thread (1–7) / render thread (8–11) | Each `OnPhaseSpan` is posted from its owning thread to the bus's MPSC drain ring |

**The render thread still owns every `ComPtr`** (canon, SPEC-INDEX §2). Devtools never touches a `ComPtr`; the DrawList
inspector (§5) reads the **`StructuralLedger`** (a POD CPU recording, `validation.md` §3.4) the render thread fills, not the
live D3D12 command list. No devtools call site allocates managed memory inside phases 6–13 (the records are pre-sized ring
slots; §8).

### 1.2 The protocol reuses the `SceneFrame` snapshot (the key decision)

Devtools does **not** define a parallel snapshot mechanism. The layout overlay (§4), scene/DrawList inspector (§5), and
damage overlay all read **the same immutable, triple-buffered `SceneFrame`** the render thread consumes
(`threading-render-seam.md` §2.1 — `PublishSeq`, `SnapshotColumns`, `DamageView`, `RetainedTableRoots`). At PUBLISH(13a) the
engine calls `OnFramePublished(seq, in frame)`; the bus **pins that slot** by holding the `PublishSeq` and participating in
the **consume-gated quarantine** as a *second logical consumer*:

```csharp
// The publisher already supports N consumers via consume-gated quarantine (threading-render-seam §5).
// Devtools registers as an extra consumer so a pinned-for-inspection slot is NOT recycled out from under it.
// QUARANTINE = RenderInFlightDepth + 1 becomes RenderInFlightDepth + (devtools attached ? 1 : 0) + 1.
public const int DevtoolsRenderInFlightDepth = 1;   // devtools holds at most ONE frame for inspection at a time
```

**This is the single most important reuse claim of the doc:** the layout/damage/scene panels get a coherent, tear-free,
thread-safe view of the frame *for free*, because the engine already produces exactly that artifact for the render thread.
The only cost is one extra quarantine slot while devtools is attached (and devtools is never attached in release). When the
devtools consumer falls behind, it does **DropOldest** on its own ring (newest frame wins, matching the present path) — it
never back-pressures the render thread (`threading-render-seam.md` §10 backpressure rule).

---

## 2. Component / Element tree inspector (props · hook-cell state · keys)

The retained tree *is* the inspector's data model — there is nothing to reconstruct. The `ComponentTable`
(`reconciler-hooks.md` §3) already holds, per mounted component: the `RenderContext` (its `List<HookCell>`), the last
`Render()` output `Element`, the minting `Element`, the `ComponentKind`, `SelfTriggered`, the `HostNode` `ComponentAnchor`,
and the keyed-LIS identity `(type, key)@position`. Devtools projects this into an immutable view tree at phase 4.

```csharp
public readonly struct ComponentNodeView
{
    public NodeHandle  HostAnchor;     // the ComponentAnchor (stable identity across root-type change)
    public uint        Gen;            // for IsLive validation when the panel later asks to expand
    public StringId    TypeName;       // component type (interned at the edge; no string on the hot path)
    public StringId    Key;            // the reconciler key (StringId.Null == positional)
    public int         DepthInTree;
    public ComponentKind Kind;         // Class | Func | Memo
    public int         ChildCount;     // realized children (virtualization: only realized rows are walked, §2.2)
    public RenderCause LastCause;      // §3 — why it rendered (or 0 == skipped) last frame
    public ushort      PropHashLo;     // cheap props-changed witness (diff vs prev expand)
    public ushort      HookCount;
}

// Hook-cell state is projected per-cell into a tagged, immutable readout (NO live cell reference crosses threads):
public enum HookCellKind : byte { State, Reducer, Ref, Memo, Callback, Effect, LayoutEffect, Context, Resource,
                                  Transition, Optimistic, Derived, DeferredValue }   // mirrors reconciler-hooks §3/§7/§8

public readonly struct HookCellView
{
    public HookCellKind Kind;
    public ushort       OrderIndex;    // positional hook index (rules-of-hooks order; WGPU0005 guarantees stable)
    public DepKey       Dep0;          // first dep as a 16B scalar witness (full dep span on demand, §2.1)
    public StringId     ValueRepr;     // §2.1: a [Conditional]-built debug string of the cell's current value
    public byte         Flags;         // e.g. Effect-is-pending, Transition-isPending, Optimistic-has-rollback
}
```

### 2.1 Reading hook-cell *values* without breaking zero-alloc

The engine's hot path stores hook values as scalars (`DepKey`) and edge GC objects (`GcDepTable`, `Ref<T>`). Devtools must
*display* them but must not allocate on the hot path. The rule:

- **Scalar cells** (`State<int/bool/double>`, `Memo<scalar>`, `DepKey`s) are projected directly into `HookCellView` — no
  alloc, the value *is* a blittable scalar.
- **Reference/boxed values** (`State<T> where T : class`, `Ref<T>`, `GcDepTable` entries) get a `ValueRepr` **only inside a
  `[Conditional("FG_DEVTOOLS")]` formatter** (`IDevtoolsRepr.Format(StringBuilder)` opt-in; default = type name). The
  `ToString`/format call happens on the **devtools panel thread** off a retained value snapshot, never in phase 4. The
  UI-thread push copies only the *handle/slot* of the boxed value into the bus; the formatter dereferences it lazily on the
  consumer side under a `IsLive` re-check. This keeps phases 6–13 alloc-free (the alloc-tripwire, `validation.md` §3.1, must
  stay green even with `FG_DEVTOOLS` on — see §10).

### 2.2 Virtualization-aware (only realized nodes exist)

The tree inspector walks **only realized nodes** (`virtualization.md`) — it shows "row 12 of 50,000 (realized window
10–60)" by reading the `VirtualState` column + `SetSize`/`PositionInSet` (the L6 collection-relation columns,
`scene-memory.md`). Expanding a virtualized item that is **not realized** issues a **realize-on-Navigate** request through
the existing virtualized-provider realization contract (`input-a11y.md` §11, L6) — devtools does not fabricate nodes. This
is the same contract Narrator uses, so the inspector cannot show a node the engine wouldn't also expose to AT.

---

## 3. Re-render highlighting — the 3-signal cause readout

The headline DevX feature: **which components rendered this frame, and *why***. The "why" is exactly the canonical 3-signal
memo gate (`SelfTriggered ‖ propsChanged ‖ HasConsumedContextChanged`, SPEC-INDEX §2 — devtools **references, never
redesigns** it). Devtools captures the *cause* at the single decision site.

```csharp
[Flags]
public enum RenderCause : byte
{
    None              = 0,        // memo-skipped this frame (highlight: dimmed / no flash)
    SelfTriggered     = 1 << 0,   // a setState/dispatch on THIS component (carries the originating lane, §6)
    PropsChanged      = 1 << 1,   // DiffProps said the incoming props differ
    ContextChanged    = 1 << 2,   // HasConsumedContextChanged(slot) — carries WHICH Context<T> (ContextId)
    ForcedByParent    = 1 << 3,   // parent re-rendered and this is a non-memo child (traversal scope, NOT a skip input)
    InitialMount      = 1 << 4,
}

public readonly struct RenderEventView
{
    public NodeHandle  HostAnchor;
    public uint        Gen;
    public RenderCause Cause;
    public byte        OriginLane;    // §6 — if SelfTriggered, the lane the originating update carried (urgent/transition)
    public ContextId   ContextCause;  // if ContextChanged, which Context<T> projection flipped (Context<uint>-over-Epoch etc.)
    public long        RenderQpc;     // for the per-component cost column in the flamegraph (§6)
}
```

The reconciler already computes these three booleans to make the skip decision; devtools reads them off the decision frame
with **one `[Conditional]` push** per *rendered* component (skipped components emit the cheaper `OnComponentSkipped`). The
overlay then:

- **flashes** the rendered component's `WorldBounds` rectangle in the devtools overlay layer (§4), color-coded by dominant
  cause (self=blue, props=green, context=amber, forced-by-parent=grey);
- in the tree panel, annotates the node with the decoded cause string (e.g. *"re-rendered: ContextChanged(`ThemeTint`),
  lane=Transition"*), which is the precise diagnostic the gap analysis calls out as the regretted-when-missing feature.

This is also the **memoization-discipline auditor** the gap analysis §3 demands ("the 3-signal skip bounds churn *in
aggregate*, and maintaining that discipline is a standing obligation"): a "forced-by-parent" flood or a coarse
`ContextChanged` storm is now *visible* rather than a silent perf leak.

---

## 4. Layout overlay (Bounds · margins · padding · baselines · dirty-rect/damage)

All geometry comes from the published `SceneFrame` (§1.2): `WorldTransform[]` (composed phase 7), node-LOCAL `Bounds[]`
(canon: `Bounds` is node-local; world rect = `WorldTransform × Bounds`), the `LayoutAux` column (resolved margins/padding —
**physical**, post-RTL-resolution at `WriteLayout`, so the overlay shows the *mirrored* box for RTL, `layout.md` §4.1 / L5),
the baseline carried in `LayoutAux`, and the `DamageView` (≤16 device-space damage rects or a full-frame flag).

```csharp
public readonly struct LayoutOverlayItem
{
    public Affine2D World;          // WorldTransform[i]
    public RectLocal ContentBox;    // Bounds[i] (node-local)
    public Edges4    MarginPhysical;// LayoutAux: resolved PHYSICAL margins (RTL already mirrored)
    public Edges4    PaddingPhysical;
    public float     BaselineY;     // first-baseline offset (LayoutAux)
    public byte      Flags;         // FlowDirection(LTR/RTL), is-overlay, is-layout-boundary
}
```

Overlay render passes (each toggled independently in the panel; each is just engine draw ops in the devtools overlay layer,
emitted in phase 8 *after* the app DrawList, z-topmost):

1. **Box model** — content (solid tint), padding (hatch), margin (translucent), drawn from `ContentBox` inset/outset by the
   physical edges. Correct under RTL automatically because the edges are already physical.
2. **Baselines** — a 1px guide at `BaselineY` per text-bearing node (the read-side from `text.md`).
3. **Damage / dirty-rect overlay** — the `DamageView` device rects, drawn as animated outlines. This is the single most
   valuable perf overlay: it makes **over-invalidation** (a whole-frame damage flag where a 40px rect was expected) visible,
   and it is *exactly* the structural twin of the `validation.md` §3.4 Bounds assertion and §3.6 epoch fault-injection — the
   overlay shows live what those gates assert offline.
4. **3-signal re-render flash** (§3) shares this layer.

Hit-testing in the panel ("click a rect → select that node") reuses the engine's own hit-test over `WorldTransform`/`Bounds`
(`input-a11y.md`); devtools adds no hit-test path.

---

## 5. Scene / DrawList inspector (SoA columns · batch counts · PSO dedup · opcode stream)

Two layers, matching the two artifacts:

**(a) Retained SoA dump (UI-side data, via the `SceneFrame` snapshot).** A column-browser over the `SnapshotColumns` of the
pinned frame: `Handles`, `WorldTransform`, `NodePaint`, `Flags`, plus on-demand expansion of the retained interning tables
(`BrushTable`/`ClipTable`/`GlyphRunTable`/`EffectChainTable` via `RetainedTableRoots`, `scene-memory.md`). Because SoA is
column-major, the dump is a set of `Span<T>` reads — devtools shows, per column, the live count, the dirty-axis state (the
3 dirty axes + arena dirty-worklist, `scene-memory.md`), and per-table **content-hash dedup ratio** (how many logical
brushes collapsed to how many `BrushData` slabs — the dedup win made visible).

**(b) DrawList / batch readout (render-side, via the `StructuralLedger`).** Devtools does **not** parse the live D3D12
command list (that would touch a `ComPtr` on the wrong thread). It reads the **`StructuralLedger`** (`validation.md` §3.4) —
the same CPU recording encoder the headless structural gate uses — which the render thread fills during record/batch
(phases 8–9) and hands over via `OnDrawListRecorded`:

```csharp
public readonly struct LedgerView   // a value-snapshot of validation's StructuralLedger for one frame
{
    public int DrawCalls, BatchCount, BarrierCount, PsoSwitches;
    public ReadOnlySpan<OpcodeStat> OpcodeHistogram;   // per DrawOp: count + bytes (the opcode STREAM, summarized)
    public ReadOnlySpan<BatchStat>  Batches;           // per batch: PSO id, merged-op count, sortkey range, bounds union
}
```

This surfaces: the **opcode stream** (per-`DrawOp` histogram + a scrubbable linear view of the POD opcode arena with its
parallel `ulong[]` sortkeys, `scene-memory.md` encoding framework), **batch counts** and the overlap-grid **merge ratio**,
**PSO dedup** (how many `SetPipeline` switches the batcher avoided — a painter-order break that fragments batches shows here
as a PSO-switch spike, the live twin of `validation.md` §3.4), and **barrier count**. It also flags **clean-span reuse**:
which spans were memcpy-reused vs re-recorded this frame (the `CleanSpanWitness` outcome, `scene-memory.md` / SPEC-INDEX §2)
— making the central reuse optimization observable. New devtools-only opcodes for the overlay layer are **not** added to the
production `DrawOp` enum; the overlay reuses existing fill/stroke/text ops (§9).

---

## 6. Per-phase frame timeline / flamegraph (QPC + per-phase alloc tripwire)

The 13-phase loop is the natural flamegraph spine. Each phase boundary already exists; devtools wraps each in a
`[Conditional("FG_DEVTOOLS")]` QPC span and reads the **per-phase alloc delta** off the same `AllocScope` the alloc-tripwire
uses (`validation.md` §3.1 — `GC.GetAllocatedBytesForCurrentThread()` deltas, per phase, asserted `Δ==0` for phases 6–13).

```csharp
public enum PhaseId : byte { Pump=1, Input=2, UpdateDrain=3, Render=4, Reconcile=5, Layout=6,
                             LayoutEffects=65, Animation=7, Publish=130, Record=8, Batch=9,
                             Submit=10, Present=11, PassiveEffects=12 }
```

The timeline shows, per frame, a stacked bar of phase durations against the 16ms vsync budget (the `RenderPriorityPolicy`
budget, `reconciler-hooks.md` §7 — recast as lane *executor*), with:

- **The alloc tripwire as a per-phase red bar.** If any phase 6–13 shows a non-zero alloc delta, devtools paints it red and
  names the phase — the *live* counterpart to the CI alloc-tripwire. This is how an app author catches a per-frame
  allocation the moment they introduce it, instead of at the next CI run.
- **Lane attribution on phase 3.** The update-drain bar is split by lane (urgent vs transition, P1) and annotated with the
  **automatic-batching fold count** (how many `setState`s folded into this one drained frame, P2a) — read from
  `DrainSummary`. This makes the two phase-3 decisions (lanes + batching) directly observable.
- **Reconcile slicing.** Phase 5 shows `Complete` / `YieldCarryForward(slices=n)` / `DiscardRestart`
  (`threading-render-seam.md` §12) so an author can see when the reconciler carried work across a frame boundary.
- **Render-thread phases (8–11)** are charted on a **second swim-lane** (the render thread), fed by `OnPhaseSpan` posted
  from the render thread — visually surfacing the UI↔render seam and the PUBLISH(13a) handoff.

The flamegraph's per-component cost (§3 `RenderQpc`) rolls up the render-phase time per component subtree, so "this list
item costs 0.4ms × 60 = the frame" is a one-glance read.

---

## 7. A11y / semantics tree viewer

The semantics projection already exists: the UIA topology-walk **merge/compaction** via `A11yPresent`, the listening-gate,
stable `GetRuntimeId`, live regions, the pattern set, and (folded by L6) `SetSize`/`PositionInSet`/`Level` +
`DescribedBy`/`FullDescription`/`FlowsTo` (`input-a11y.md` §11). Devtools projects the **merged** semantics tree (what an AT
client actually sees, not the raw node tree) into a read-only view:

```csharp
public readonly struct SemanticsNodeView
{
    public int       RuntimeId;        // stable GetRuntimeId
    public StringId  ControlType;      // UIA control type (Button/List/ListItem/Text/Menu/Window/ToolTip…)
    public StringId  Name, FullDescription;
    public int       PositionInSet, SetSize, Level;   // L6 collection relations — "track 12 of 50,000"
    public uint      PatternFlags;     // Invoke/Toggle/Value/Text/Scroll/Selection… (the pattern bitset)
    public int       DescribedByRuntimeId, FlowsToRuntimeId;   // L6 relations
    public byte      Flags;            // is-merged-into-parent (compaction), is-live-region, is-modal
}
```

The viewer shows the **compaction** (which raw nodes merged into one `SemanticsNode`) and a **listening-gate indicator**
(green when an AT client is attached, grey when the projection is dormant — the gate that keeps semantics cost zero when no
AT listens). It surfaces the **L6 collection relations** and the **`ITextRangeProvider`** selection/caret state (L1/L3,
`text.md` §8 / `input-a11y.md` §11.4) so an author sees exactly what Narrator would read. Because devtools reads the same
projection AT does, it is also the live counterpart to the CI `ITextRangeProvider` Narrator-conformance gate
(`validation.md` §12.7) and the DEBUG `AccessibilityScanner` lint.

---

## 8. Transport + time-travel (snapshot/replay of the update queue)

### 8.1 In-process bus (the default)

`DevtoolsBus` is a set of **per-producer-thread SPSC ring buffers** (UI ring, render ring) drained by a single consumer
(the panel thread or the wire pump). Records are POD value structs into pre-sized ring slots — **no per-event managed
allocation**, so the bus never perturbs the alloc-tripwire (§10). `SceneFrame` handoff is by **reference into the pinned,
sealed slot** (§1.2), not a copy. Ring overflow = **DropOldest** (devtools is best-effort; it never blocks a producer and
never back-pressures the render thread).

### 8.2 Optional out-of-process wire

For a separate inspector process (the typical DevX shape), the bus pump serializes records over a **named pipe**
(`\\.\pipe\fluentgpu-devtools-<pid>`) using the same POD record layouts (blittable; the wire is `MemoryMarshal.AsBytes`
over the ring slots, length-prefixed). The companion inspector app is itself a FluentGpu app (dogfood). The wire is
**localhost-only, opt-in, dev-machine-only** — there is no wire in release because the assembly is trimmed out. (macOS
boundary, §11: the pipe name is the only platform-specific seam; the record layouts are portable.)

### 8.3 Time-travel — snapshot & replay of the update queue

The phase-3 update queue (`reconciler-hooks.md` §7, backed by `scene-memory.md`'s `UpdateQueueSlab`) is a **per-update
`(slot, updater, lane)` record stream** — which makes it a recordable, replayable log. Devtools captures it:

```csharp
public readonly struct UpdateRecordView   // POD projection of one UpdateQueueSlab record (no captured-closure ref escapes)
{
    public NodeHandle TargetAnchor;
    public uint       Gen;
    public byte       Lane;            // urgent / transition / deferred (P1)
    public ushort     UpdaterId;       // source-gen-assigned id for the updater closure (dsl-aot capture, §9)
    public DepKey     PayloadWitness;  // scalar witness of the update payload (full payload via GcDepTable slot on demand)
    public ulong      EnqueueSeq;      // ordering within and across frames
    public long       EnqueueQpc;
}
```

- **Snapshot** = the ordered `UpdateRecordView[]` log between two PUBLISH ticks, paired with the **published `SceneFrame`
  `PublishSeq`** at each step. Because the `SceneFrame` is already an immutable snapshot, "the scene at update N" is just
  "the frame published after draining through update N" — **time-travel reuses the same snapshot the render thread uses**
  (the §1.2 reuse claim, again).
- **Replay** = feed the recorded `(slot, updater, lane)` log back through the phase-3 drain in a **headless host**
  (`Rhi.Headless`/`Pal.Headless`, `validation.md`), stepping update-by-update or frame-by-frame, and diff the resulting
  published `SceneFrame`/`StructuralLedger` against the recorded one. Determinism is *guaranteed by the same property the
  P4 discard-restart gate relies on*: reconcile is pure and produces a **byte-identical** DrawList for the same input
  schedule (`validation.md` §12.5 / §2.4 `ConcurrentRecord_MatchesSingleThreadedGolden`). Time-travel is therefore not a
  new correctness burden — it *is* the replay leg of an invariant the engine already proves.
- **Scrub** = the panel sets the host to "replay up to update N," and the layout/scene/semantics panels all re-render off
  the resulting pinned `SceneFrame`. State (hook cells) at step N is reconstructed by replay, never by snapshotting live GC
  cells (which would pin the whole heap).

**Limit (stated honestly):** replay reproduces the *update schedule*, not external-world side effects. An effect that hit
the network (phase 6.5/12) is **not** re-run on replay; the recorded effect *result* (if it fed a `setState`) is in the log
as a later update, so the visible state is faithful, but live I/O is not re-issued. This matches React DevTools' time-travel
semantics and is the correct boundary.

---

## 9. Zero-byte-in-release + zero-alloc story (the `EnableDevtools` switch)

- **Erasure.** Every engine-side observer call is wrapped in `[Conditional("FG_DEVTOOLS")]` (and a `static readonly bool
  Devtools.Enabled` guard backed by the `FluentGpu.EnableDevtools` `[FeatureSwitchDefinition]`, `dsl-aot.md` §3.4). With the
  switch off (release default), `[Conditional]` deletes the **call sites** and the trimmer's substitution turns
  `Devtools.Enabled` into a constant `false`, so the dead branches and the entire `FluentGpu.Devtools` assembly are
  trimmed → **0 bytes shipped**, **0 instructions on the hot path**. The footprint `.mstat` ratchet (`validation.md` §3.2)
  enforces this — devtools must not move the release footprint by a single byte.
- **No new production artifacts.** Devtools defines **no new scene column, no new `DrawOp` opcode, no new RHI method, no new
  PAL seam, no new hook.** It is a pure *reader* of `scene-memory.md` columns, the `SceneFrame`, the `ComponentTable`, the
  `StructuralLedger`, and the `UpdateQueueSlab`. (The overlay layer draws with **existing** fill/stroke/text ops into an
  **existing** transient overlay-root, `layout.md` §10.) The one new *seam* is `IDevtoolsObserver`, and it is null/erased in
  release. The `UpdaterId` for time-travel (§8.3) reuses the **source-gen capture id** dsl-aot already assigns to lane/
  transition/Suspense/optimistic updaters (`dsl-aot.md` generators #8–#10) — no new generator.
- **Alloc discipline with the switch ON.** Even in `FG_DEVTOOLS` test builds, the per-frame push path must keep phases 6–13
  alloc-free so the alloc-tripwire (`validation.md` §3.1) stays meaningful: records go into **pre-sized ring slots**;
  reference-value formatting (§2.1) happens **off-thread on the consumer side**; `SceneFrame` handoff is by reference. The
  only permitted allocations are at **attach time** (ring + pipe buffers) and on the **consumer/panel thread**, never inside
  a production phase. A `FGVALIDATE` run with `FG_DEVTOOLS` on asserts the same per-phase `Δ==0` for 6–13.

---

## 10. Failure & edge cases

- **Devtools consumer stalls / detaches mid-frame.** The bus DropOldests; the pinned `SceneFrame` slot is released back to
  the publisher (its quarantine consumer-count decrements). No producer ever blocks. Detaching restores
  `QUARANTINE = RenderInFlightDepth + 1`.
- **Stale handle in a panel.** A panel holds `(NodeHandle, Gen)`; on expand/inspect it re-validates `IsLive` against the
  current store gen. A node freed/recycled since capture shows as *"(retired)"* — never a use-after-free read (the
  generational handle + the §1.2 frame pin make this safe).
- **Type-flip / unmount while inspected.** The `ComponentAnchor` identity is stable across root-type change
  (`reconciler-hooks.md`), so the tree panel keeps selection across a flip and annotates *"type changed A→B (state
  destroyed)"* — the P8 remount-releases-handles event is surfaced, not silently dropped.
- **Virtualized node not realized.** Expanding requests realize-on-Navigate (§2.2); if the provider declines (off-screen,
  budget), the panel shows the collection-relation summary (`PositionInSet/SetSize`) without fabricating a node.
- **Reconcile sliced/discarded while time-travel records.** The update log records the schedule regardless; replay always
  runs to `Complete` (the only PUBLISH-eligible outcome), so a recorded `YieldCarryForward`/`DiscardRestart` replays to the
  same final frame — consistent with the no-mutation-before-PUBLISH invariant (`threading-render-seam.md` §12).
- **Render thread device-lost.** Devtools reads the device-lost reason (already a volatile-published value,
  `threading-render-seam.md`) and freezes the render swim-lane with the reason, rather than reading a torn ledger.
- **Overlay self-perturbation.** The devtools overlay layer is emitted *after* the app DrawList and is **excluded** from the
  app's batch/draw-call/damage stats shown in §5 (the ledger separates the overlay z-layer), so inspecting does not corrupt
  the numbers being inspected (the observer-effect guard).

---

## 11. Cross-platform (macOS) boundary

Devtools is **almost entirely portable** because it reads portable artifacts (the SoA columns, the `SceneFrame`, the
`StructuralLedger`, the update log — all POD/immutable, no platform types). Platform-specific surface, isolated:

- **Transport pipe name** (§8.2): named pipe on Windows; the macOS plan is a Unix domain socket at
  `$TMPDIR/fluentgpu-devtools-<pid>.sock` — same blittable record layouts, swap the endpoint. **Status: Designed**
  (`macos-debt-ledger.md`).
- **A11y viewer** (§7) projects the **UIA** surface on Windows; on macOS it would project the NSAccessibility mapping that
  `input-a11y.md`'s a11y seam targets. The *viewer* is portable; the *projection source* follows the platform a11y seam.
- **QPC** (§6) → `QueryPerformanceCounter` on Windows; `mach_absolute_time` on macOS. Single PAL timing read, already abstracted.

Everything else — tree inspector, re-render cause, layout/damage overlay, scene/DrawList inspector, timeline/flamegraph,
time-travel — is platform-neutral and ships in the same `FluentGpu.Devtools` assembly on both targets.

---

## 12. Tests / gates

Devtools is dev-only, so its gates are **lightweight and CI-portable** (no GPU, no Windows needed for the core):

| Gate | What it asserts | Where |
|---|---|---|
| **Zero-byte-in-release** | `.mstat` footprint with `EnableDevtools=false` is byte-identical to a build with `FluentGpu.Devtools` absent | footprint ratchet, `validation.md` §3.2 |
| **Alloc-neutral-when-on** | per-phase `Δ==0` for phases 6–13 holds with `FG_DEVTOOLS` on (observer pushes don't allocate) | alloc-tripwire, `validation.md` §3.1 |
| **Observer-effect isolation** | the §5 ledger numbers (draw/batch/PSO/damage) are **identical** with devtools attached vs detached (overlay z-layer excluded) | headless structural gate, `validation.md` §3.4 |
| **Time-travel determinism** | replay of a recorded update log produces a **byte-identical** published `SceneFrame`/DrawList to the live recording | reuses `ConcurrentRecord_MatchesSingleThreadedGolden` / P4 discard-restart golden, `validation.md` §12.5 |
| **Cause-readout correctness** | the captured `RenderCause` for each component matches the reconciler's actual skip/render decision over the §7 fault-injection corpus | `validation.md` §7 reconcile corpus |
| **Frame-pin / quarantine** | a pinned-for-inspection `SceneFrame` slot is never recycled (quarantine consumer-count honored); detach restores the constant | `seam.race` swept-params soak, `validation.md` §2.4 |
| **Thread confinement** | every observer push originates on the datum's owning thread; no `ComPtr`/live-`Element`/`RenderContext` reference crosses into devtools | `ThreadGuard` + data-race gate, `validation.md` §3.7 |

These are `[Conditional]`/`FGVALIDATE`-only and never run in the shipping binary, consistent with the safety-floor canon
(SPEC-INDEX §2: *in production, safety == CI coverage*).

---

## Implemented from the gap analysis

| Item | Was | Now (folded into core here) |
|---|---|---|
| **L12 — Live devtools / inspector / profiler** (the new-doc item) | Tier 3, *"Defer (fast-follow)"* — "only the live runtime inspector is missing" | **Designed into core** as `FluentGpu.Devtools`: §2 Element/Component tree inspector (props, hook-cell state, keys, `ComponentAnchor` identity); §3 re-render highlighting via the canonical **3-signal cause** readout; §4 layout overlay (Bounds/margin/padding/baseline) + the **dirty-rect/damage overlay**; §5 scene/DrawList inspector (SoA columns, batch counts, **PSO dedup**, opcode stream, clean-span reuse); §6 per-phase frame **timeline/flamegraph** (QPC + per-phase **alloc-tripwire** readout, lane/batching attribution); §7 a11y/semantics tree viewer; §8.3 **time-travel** (snapshot/replay of the update queue). Built on the `EnableDevtools` switch (0 bytes release, §9), the retained SoA tree, the **reused immutable `SceneFrame` snapshot** (§1.2), and the existing CI structural ledger (§5/§6). |

It also **observes** (read-only, never redesigns) the folded gaps owned elsewhere, surfacing them live: P1/P2a lanes +
auto-batching (§6 phase-3 lane/fold readout), P4/P5 reconcile slicing (§6, §8.3), L1/L3 selection + `ITextRangeProvider`
(§7), L5 RTL physical-box overlay (§4), L6 collection relations (§2.2, §7), P8 type-flip remount (§10).

---

## Manifest

This doc introduces **no new production opcode, scene column, RHI method, PAL seam, or hook** — by design (§9). Its only new
artifact is the **dev-only, release-trimmed** observer seam.

- **NEW (dev-only, erased in release):** `IDevtoolsObserver` + `DevtoolsBus` protocol (§1); the view-projection POD structs
  (`ComponentNodeView`, `HookCellView`, `RenderEventView`/`RenderCause`, `LayoutOverlayItem`, `LedgerView`,
  `SemanticsNodeView`, `UpdateRecordView`, `DrainSummary`, `PhaseId`); the in-proc ring + opt-in localhost named-pipe wire
  (§8); a devtools overlay **z-layer** drawn with **existing** ops (§4/§5). `DevtoolsRenderInFlightDepth = 1` quarantine
  participation (§1.2). All behind `FluentGpu.EnableDevtools` / `[Conditional("FG_DEVTOOLS")]` → 0 bytes release.
