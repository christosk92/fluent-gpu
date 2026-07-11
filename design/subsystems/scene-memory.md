# FluentGpu — Subsystem Design: Scene (SoA RenderNode store) + Foundation memory model + DrawList encoding framework

**Primary assemblies:** **`FluentGpu.Foundation`** (the `Handle`, the four allocators incl. `ChunkedArena`,
`StringTable`, the typed-handle wrappers, `ImageRefTable` slab) · **`FluentGpu.Scene`** (the SoA `SceneStore`,
the column catalogue, `BrushTable`/`ClipTable`/`GlyphRunTable`/`EffectChainTable`, the dirty worklist, the
`ISceneBackend` impl, the `Mutate()` epoch chokepoint, `CleanSpanWitness`) · **`FluentGpu.Render`** (the
DrawList **encoding framework** — `DrawCmd` header, the render-private byte arenas, the parallel `ulong[]`
sortkeys, the `DrawOp` enum; the *payloads* are owned by `gpu-renderer.md`).

This is the deep, buildable design behind **`architecture-spec.md` §4.1–4.5 + §5.4** and **`foundations.md`
§1–2** and the **`hardened-v1-plan.md` §4.4** alloc/incremental hardening. It is the home for three things the
other subsystem docs all *reference but none own*: the retained node store's exact column layout, the
generational-handle + allocator substrate, and the byte-level DrawList container that the renderer fills and
the threading seam snapshots.

Decisions are stated as **MADE** with the losing option. Open items are `OQ-n`.

---

## 0. What this subsystem owns (authority map), and what it only references

| Category | This doc is authoritative for |
|---|---|
| **Handle** | the generational `Handle {u32 index, u32 gen}` (8B), the typed wrappers (`NodeHandle`/`BrushHandle`/…), the ABA/wrap policy, `IsLive` |
| **Allocators** | `SlabAllocator<T:unmanaged>`, `ObjectPool<class>`, `HandleTable<TResource>`, and **`ChunkedArena`** (reserve-commit / segmented, native-high-water-gated) — which **supersedes** the foundations §1.2 single-buffer `ArenaAllocator` |
| **SceneStore** | the SoA spine (gen + free-list), the **full column catalogue** (Topology, Identity, LayoutInput/LayoutAux, Bounds[LOCAL], NodePaint, InteractionInfo, A11yInfo, FocusNav, NodeFlags + the feature columns `ImageRefTable[ref]`, `EffectAux`, `BackdropRef`, `VirtualState`, **`SelectionState` [L1], `FlowState` [L5], the `A11yRel` collection/relation extension [L6]**) — storage shape of each |
| **Update-queue backing** | the per-component **`UpdateQueueSlab`** column storage shape (the phase-3 lane/update record slab for `reconciler-hooks.md` P1/P2a) — this doc owns the slab placement + record layout; the *lane semantics / drain order* live in `reconciler-hooks.md` |
| **Interning tables** | `StringTable`, `BrushTable`, `ClipTable`, `GlyphRunTable`, `EffectChainTable` — the **content-hash dedup** machinery and the *slab placement*; the SoA spine resizes them in lockstep |
| **Dirty tracking** | the 3 orthogonal dirty axes (Layout/Paint/Transform), the upward `Subtree*` aggregates, and the **arena-backed dirty WORKLIST** (idle frames O(0)) + the `Vector256` flag-scan fallback |
| **DrawList encoding framework** | `DrawCmd` 8-byte header, the `DrawOp` opcode **enum list**, the render-private **≥3-deep byte arenas**, the parallel **`ulong[]` sortkey** arena, the clean-span+epoch reuse **rule statement** |
| **`Mutate()` chokepoint** | the single per-realization mutation chokepoint (epoch bump + `RealizationDirtySet` registration) and the DEBUG **`CleanSpanWitness`** validator (captures handle+gen+content-epoch+baked-geometry hash) |
| **`ISceneBackend`** | the **Scene-side implementation** of the seam (the *interface* is co-owned with `reconciler-hooks.md`): handle-in/handle-out POD, two-phase reconcile (structural then edit), `EditPaint`/`EditLayout`, `NodeChildCollection` incl. `RemoveAt` |

What it **references, never redesigns** (ownership resolutions from the program lead):

- **`gpu-renderer.md`** owns **each DrawList opcode's payload STRUCT SHAPE** (`FillRoundRectCmd`,
  `DrawImageCmd`, `DrawGlyphRunCmd`, `DrawShadowCmd`, …) and the **SortKey 64-bit layout** and the
  authoritative *statement* of the clean-span reuse rule. This doc owns the **encoding framework** that holds
  those payloads — the header, the byte arenas, the parallel sortkey array, the opcode enum, the reuse-rule
  *mechanism* — and references gpu-renderer for the per-opcode fields. (The two are split exactly so this doc
  never restates a payload's fields and gpu-renderer never restates the container.)
- **`threading-render-seam.md`** owns the **`SceneFrame` / `SnapshotColumns` / `CopyInto`** publisher-side POD
  and the publish/quarantine/retire mechanics. **This doc owns the COLUMN LAYOUT those snapshot** — i.e. what
  a `SnapshotColumns` element looks like is *projected from* the columns defined here, but the snapshot
  buffer's lifetime, the triple-buffer, the both-directions-volatile publish, and the consume-gated
  quarantine are the seam doc's. State the split: **Scene owns the live columns; the seam owns the immutable
  copy of the paint-relevant subset.**
- **`media-pipeline.md`** owns `ImageRealization` **semantics** (decode/residency/eviction/`State` transitions,
  pinning) and `ResidencyManager`. This doc owns only the **slab placement** of `ImageRefTable`
  (`SlabAllocator<ImageRealization>` in Foundation, `HandleKind.ImageRef`) and the fact that its `ContentEpoch`
  participates in the clean-span rule like any other realization.
- **`theming.md`** owns `BrushDeriver`, `BrushRecipe`, `DerivedKey`, and the `IBrushSink` derivation flow. This
  doc owns `BrushTable` itself (the SceneStore-adjacent `SlabAllocator<BrushData>` with content-hash dedup);
  `IBrushSink` is implemented over it.
- **`backdrop-effects-animation.md`** owns `EffectAux` **semantics** (which animation channel writes which
  field, the layer re-composite policy) and `EffectChainTable` contents. This doc owns the **column placement**
  (`EffectAux` cold slab + `EffectAuxRef:int` in `NodePaint`; `BackdropRef` side-table).
- **virtualization** (`app-requirements-waveemusic.md` §3.2, doc not yet written) owns `VirtualState`
  **semantics** (anchoring, recycle, per-range CTS). This doc owns the **column placement** (a slab-backed
  `VirtualState` column + the `VirtualRangeDirty`/`StickyPinned` `NodeFlags` bits).
- **`reconciler-hooks.md`** owns the `ISceneBackend` *interface declaration*, the `ChildReconciler` algorithm,
  and the conceptual *home* of the `Mutate()` chokepoint (it is the UI-thread writer). This doc owns the
  Scene-side *mechanism* — the actual columns those ops write, the two-phase growth lock, and the epoch bump.
  **For P1/P2a (lanes + automatic batching):** `reconciler-hooks.md` owns the lane bitmask *values*, the
  `(fiber, updater, lane)` enqueue API (`setState`/`startTransition`/`UseTransition`), the phase-3 drain order,
  and the `RenderPriorityPolicy`-as-lane-executor recast. **This doc owns only the BACKING STORAGE** — the
  per-component `UpdateQueueSlab` shape, the intrusive enqueue-link layout, and its phase-13 reset (§2.8).
- **`text.md`** owns `SelectionState` **semantics** (anchor/extent/affinity meaning, the `GetSelectionRects`
  read-side, the `ITextStoreACP2`-shaped commit-lock). This doc owns the **column placement** (`SelectionState`
  slab + `SelectionRef:int` in `NodePaint`) and registers the `DrawSelectionRectCmd` opcode in the encoding
  framework (§4.1); the rect payload shape + raster are `gpu-renderer.md`'s (§2.7).
- **`layout.md`** owns `FlowDirection` **resolution** (inherited→resolved at the `WriteLayout` boundary; the
  logical→physical mirror of flex direction / justify / align / `Edges4`). This doc owns the **column placement**
  (`FlowState` 4-byte column: inherited request + resolved physical flag) so layout's ported-Yoga core stays
  bit-for-bit physical (§2.7).
- **`input-a11y.md`** owns the `SetSize`/`PositionInSet`/`Level`/`DescribedBy`/`FullDescription` UIA **semantics**
  (provider projection, virtualized-realization-on-Navigate). This doc owns their **column placement** — the
  `A11yRel` cold extension slab referenced from `A11yInfo` (§2.7).

> Cross-cutting contracts (threading, COM, color, RHI/PAL seam, hooks/effect timing, language/AOT) are owned by
> the referenced docs; this doc designs strictly within them and does not relitigate them.

---

## 1. Foundation memory model

### 1.1 `Handle` — the universal hot-path reference (8 bytes)

The canonical handle is **`{u32 index, u32 gen}`** (architecture-spec §4.1; see [`SPEC-INDEX.md`](../SPEC-INDEX.md);
it supersedes the earlier handle form in foundations §1.1). The kind byte is **dropped from the handle bits** and moved to
the typed wrapper's `[Conditional("DEBUG")]` assert (the wrapper statically knows its kind), which buys the
full 32-bit generation back and removes the "park-slot-forever-on-wrap" policy that violated the no-growth
invariant.

```csharp
namespace FluentGpu.Foundation;

[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct Handle : IEquatable<Handle>
{
    public readonly uint Index;          // slot index into the owning slab column (0 = null sentinel)
    public readonly uint Gen;            // 32-bit generation; bumped on alloc AND free (ABA defense)

    public bool   IsNull => Index == 0 && Gen == 0;
    public ulong  Packed => Unsafe.BitCast<Handle, ulong>(this);   // for DepKey/sortkey packing
    public bool   IsLive(uint slotGen) => Gen == slotGen;          // the one validation, in DEBUG hot paths
    public bool   Equals(Handle o) => Index == o.Index && Gen == o.Gen;
    public override int GetHashCode() => (int)(Index * 2654435761u ^ Gen);
    public static readonly Handle Null = default;
}
```

**Typed wrappers** are zero-cost `readonly struct` over one `Handle`, one per `HandleKind`
(`NodeHandle`/`BrushHandle`/`ClipHandle`/`TextureHandle`/`BufferHandle`/`PipelineHandle`/`FontFaceHandle`/
`GlyphRunHandle`/`ImageHandle`/`PathHandle`/`EffectChain`/…). Each carries an `implicit operator Handle` and a
`[Conditional("DEBUG")] AssertKind()` so cross-kind mixups are a compile-or-debug error, never silent.

```csharp
public readonly struct NodeHandle : IEquatable<NodeHandle>
{
    internal readonly Handle H;
    public uint Index => H.Index;  public uint Gen => H.Gen;
    public bool IsNull => H.IsNull;
    [Conditional("DEBUG")] internal void AssertKind() { /* HandleKind.Node, debug-side table */ }
}
```

`HandleKind` survives as a `byte` enum used only by `HandleTable`/debug asserts and by `DepKey`'s `Handle`
discriminator — never packed into the 8 live bytes. **Wrap policy:** generation wraps at 2³² (≈400 days at
60 fps of continuous alloc/free of *one* slot); on the practically-unreachable wrap the slot gen resets and we
accept astronomically-low ABA risk (architecture-spec §4.1). The Layout `LayoutCacheEntry` generation-counter
uses a separate epoch-sweep wrap detector (architecture-spec §5.5) and is not this handle's gen.

### 1.2 The four allocators (and `ChunkedArena` superseding the single-buffer arena)

```
┌─ SlabAllocator<T> where T : unmanaged ─────────────────────────────────────────────────────┐
│ Stable, generationally-versioned storage for long-lived hot structs.                         │
│ Backs: every SceneStore SoA column-set (one shared spine), Brush/Clip/GlyphRun/EffectChain/   │
│   ImageRef tables, AnimTrack slab, LayoutAux/EffectAux/VirtualState cold slabs.               │
│   T[] _items; uint[] _gen; int[] _freeNext; int _freeHead, _count, _capacity;                 │
│   Handle Alloc(in T v);  ref T Get(Handle);  bool TryGet(Handle, out …);  void Free(Handle);   │
│ Growth geometric ×2; NEVER shrinks (footprint stable across frames). gen++ on alloc AND free.  │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
┌─ ChunkedArena  (SUPERSEDES the foundations §1.2 single-buffer ArenaAllocator) ─────────────────┐
│ Per-frame / per-pass scratch, but reserve-commit / SEGMENTED so a grow is O(1) add-chunk —     │
│   no LOH/Gen2 copy spike, no relocation. Native-backed (GC never scans it).                    │
│   AllocSpan<T>(n,align) → bump within the active chunk; overflow → commit next chunk           │
│   (addresses of prior chunks STAY STABLE — held child-list slices remain valid mid-pass).      │
│   int Mark(); void ResetTo(int); void Reset();  // O(1) frame/pass boundary                    │
│ Backs: DSL modifier ops, reconcile keyed-LIS scratch, child-row snapshot spans, layout scratch,│
│   the dirty worklist, the DrawList byte+sortkey arenas (render-private variant).               │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
┌─ ObjectPool<T> where T : class, new()  (cap 32, explicit Reset, no reflection) ────────────────┐
│ Edge GC recycle: RenderContext on unmount, ChildReconciler scratch boxes. Used at the EDGE.     │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
┌─ HandleTable<TResource>  (= SlabAllocator specialization for COM/GPU; lives in the RHI LEAF) ──┐
│ Maps Texture/Buffer/Pipeline/FontFace handles → owned ComPtr<…> + cached descriptors + state.  │
│ The ONLY place GC/COM ownership meets handles. Render-thread-confined (threading §1.1).         │
│ Free runs typed teardown (slot.Res.Dispose) BEFORE slot reclaim; pointer-bearing slabs use a    │
│ side int[] _next free-link (NEVER the intrusive-first-4-bytes trick — it would stomp ComPtr).   │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Why `ChunkedArena` supersedes the single-buffer arena (hardened §4.4, foundations-amendment §7).** The
foundations §1.2 `ArenaAllocator` was a single contiguous `byte[]` with a `Array.Resize` grow — that grow is a
Gen2/LOH copy spike (a frame-time "arena cliff") *and* it relocates the buffer, invalidating any `Span`/offset
held up a recursion spine. `ChunkedArena` reserves a virtual address range and commits chunks on demand
through the **`IVirtualMemory` PAL seam** (owned by `pal-rhi.md`); a grow is `O(1) add-chunk` with **stable
prior-chunk addresses**, so the layout scratch invariant ("child lists held up the recursion spine stay
valid") holds *during* a pass, not only between passes (relaxing architecture-spec §5.5's "grow only between
passes"). **Native high-water counter — not `GC.GetTotalAllocatedBytes` — gates chunk growth**, because the GC
byte API cannot see `VirtualAlloc`/`NativeMemory` (hardened §4.4). EWMA high-water reclaim trims committed (not
reserved) chunks during idle. System-OOM on `Commit` is a clean fatal exception, never corruption (the one
residual; ledger row §4.7 of the plan).

**The intrusive free-list rule (folded BLOCKER).** Pure-numeric POD slabs (Topology, LayoutInput, Bounds,
NodePaint, …) keep the intrusive-first-4-bytes trick (`_freeNext[i]` reuses the slot's low bytes). Any slab
whose element contains a pointer/`ComPtr` (only `HandleTable` resource slots) uses a **separate `int[] _next`**
free-link array. SceneStore columns are all pure POD, so the spine uses the intrusive trick.

### 1.3 Strings & interning (no per-frame string allocation)

`StringId` is a 4-byte interned int. `StringTable` (Foundation) is an append-only
`Dictionary<string,int>` + `string[]` reverse table, populated at the **edge** (theme keys, font family names,
a11y names, attached keys, **Element `Key`s** — the DSL/source-gen interns keys at `Element` construction so
the reconciler never re-probes per keyed child). Hot paths compare `StringId` ints. Text content never reaches
the paint path as `string` — it arrives as a `GlyphRunHandle` (text.md). The interning tables live in
`Foundation` so `Render` can resolve handles without depending on `Scene`/`Reconciler` (keeps the DAG acyclic —
folds FA-3).

---

## 2. The SoA `SceneStore` — one spine, parallel columns

### 2.1 Decision: Struct-of-Arrays, one shared generation/free-list spine

**MADE: SoA, columns in `SlabAllocator`-shaped parallel `T[]` indexed by `NodeHandle.Index`; one gen/free-list
spine indexes all columns.** Losing option: AoS (one ~160B node struct). Justification (architecture-spec §2):
layout, record, dirty-scan, hit-test, and UIA each touch *disjoint* column subsets — the dirty-scan reads only
`Flags[]` and SoA streams just that cache line; AoS would drag a full node through cache for a 4-byte read. SoA
columns are trivially `Span<T>`-iterable, SIMD-friendly (transform compose, bounds union), and resize
independently. One node = one 32-bit slot across all columns; handle validation is one gen check regardless of
column count. **One node store — no WinUI dual visual/logical tree** (P7).

```csharp
namespace FluentGpu.Scene;

public sealed class SceneStore
{
    // ── spine: the SlabAllocator core (intrusive free list; POD) ──
    uint[] _gen;          // [i] bumped on alloc AND free (ABA)
    int[]  _freeNext;     // intrusive free list
    int    _freeHead, _count, _capacity;

    // ── parallel columns (index = NodeHandle.Index); EnsureCapacity resizes ALL in lockstep ──
    Topology[]        _topo;     Identity[]        _id;
    LayoutInput[]     _layIn;    Bounds[]          _bounds;     // Bounds is LOCAL (P8)
    NodePaint[]       _paint;    NodeFlags[]       _flags;
    InteractionInfo[] _hit;                                     // hot-ish (input)
    // cold columns scanned separately / referenced by index (hot/cold split, §2.6):
    A11yInfo[]        _a11y;     FocusNav[]        _focus;
    // feature columns referenced by ref-index from the hot rows (0/-1 sentinel for the common node):
    // LayoutAux, EffectAux, VirtualState live in their own SlabAllocators; NodePaint/LayoutInput carry the ref.

    public NodeHandle Alloc(ushort elementTypeId, VisualKind kind);   // pop free slot, gen++, zero columns
    public void       Free(NodeHandle h);                             // push slot, gen++ (defers reuse to quarantine)
    public ref Topology   Topo(NodeHandle h);    // [MethodImpl(AggressiveInlining)] gen-checked in DEBUG
    public ref NodePaint  Paint(NodeHandle h);
    public ref Bounds     LocalBounds(NodeHandle h);
    // … one ref-returning accessor per column.
}
```

**Slab-growth safety (folded, structural not DEBUG-only).** A `ref T` from a column accessor must never be held
across an `Alloc` that can `Array.Resize`. Enforced by the two-phase reconcile (§5): the **structural sub-phase**
(`_growthLocked=false`, all `CreateNode`/`InsertChild`/`MoveChild`, growth allowed) runs first, then the
**edit sub-phase** (`_growthLocked=true`, all `EditPaint`/`EditLayout`/`WriteX`, non-allocating). `EditPaint`/
`EditLayout` assert the lock; the edit scope **captures+revalidates the backing array reference on Dispose** so
a release build cannot write through a stale `ref`; plus `EnsureCapacity(count + newChildBatch)` before edit
blocks as cheap insurance.

### 2.2 The full column catalogue (each column's storage shape + who owns its SEMANTICS)

| Column | Bytes | Storage shape | Phase that reads it | Semantics owner |
|---|---|---|---|---|
| **Topology** `{Parent, FirstChild, LastChild, PrevSibling, NextSibling, ChildCount}` | 24 | 6× `int` (slot indices; -1 = none) — **doubly-linked** sibling list | reconcile (mutate), layout/record/hit (walk), reverse-z hit via `LastChild→PrevSibling` | **this doc** |
| **Identity** `{Key:StringId, ElementTypeId:ushort, ComponentSlot:int, ElementEpoch:uint, PayloadRef:PayloadRef}` | 16–20 | flat POD | reconcile (type dispatch), record (per-kind opcode) | this doc (layout) / `reconciler-hooks.md` (ComponentSlot meaning) |
| **LayoutInput** (hot) | 96 | flat POD: width/height/min/max, margin/padding `Edges4` (`InlineArray`), flex dir/grow/shrink/basis, align, justify, position-kind, gap, **`LayoutAuxRef:int`** | layout measure inner loop | **layout** (architecture-spec §5.5); this doc owns the byte layout assert |
| **LayoutAux** (cold) | ~120 | separate `SlabAllocator<LayoutAux>`; ~10% of nodes; shared zero-sentinel row 0 | layout measure (border/inset/percent precision) | layout |
| **Bounds** `{X, Y, W, H, flags}` **LOCAL** | ~32 | flat POD; pixel-snapped; **node-LOCAL space** (P8) | layout (write), record/hit/UIA (read) | this doc (placement) / layout (values) |
| **NodePaint** `{LocalTransform:Affine2D(24), Opacity, Fill:BrushHandle, Stroke:BrushHandle, StrokeWidth, Corners:CornerRadius4(16), Clip:ClipHandle, VisualKind:byte, Layer:byte, EffectAuxRef:int}` | 64 (one cache line) | flat POD | animation (write transform/opacity), record (read all) | this doc (layout) / theming (`Fill` derivation) / backdrop (`EffectAuxRef`) |
| **InteractionInfo** `{HitCorners, HandlerMask:ushort, CursorId, HitShape:byte, Role:byte, HitGeometryRef:int}` | 16 | flat POD; `Role` (SHIPPED) = `AutomationRole` enum (Foundation), set by control factories so a `BoxEl` announces its control type | hit-test (hot); UIA/devtools/tests (read `Role`) | `input-a11y.md` (semantics) |
| **A11yInfo** `{Name, AutomationId, HelpText:StringId, ControlType, Patterns:ushort, Heading, Landmark, LabeledBy, LiveSetting}` | 24 | flat POD; **cold** (scanned only when `UiaClientsAreListening`) | UIA only | `input-a11y.md` |
| **FocusNav** `{TabIndex, IsTabStop, XY{L,R,U,D}}` | ~24 | flat POD; cold | focus engine (Tab/arrow nav) | `input-a11y.md` |
| **NodeFlags** | 4 | single 32-bit dirty/state column | **every phase pre-filters on this** | **this doc** |
| **ImageRefTable[ref]** | (4 ref) | `PayloadRef`/`NodePaint` carries an `ImageHandle` into Foundation `ImageRefTable : SlabAllocator<ImageRealization>` | record (resolve→`DrawImageCmd` or placeholder) | this doc (slab placement) / **`media-pipeline.md`** (`ImageRealization`) |
| **EffectAux** (cold) | 32 | `SlabAllocator<EffectAux>`; `EffectAuxRef:int` in `NodePaint`; 0 for common node | animation (write), batch/FrameGraph (read) | this doc (placement) / **`backdrop-effects-animation.md`** (semantics) |
| **BackdropRef** (side) | ~12 | tiny cold side-table `{BakeTicket, ImageHandle baked, BackdropState}` | record (backdrop node) | **`backdrop-effects-animation.md`** |
| **VirtualState** (slab) | — | slab-backed column; per-range CTS, anchor `ItemKey`, extent table ref | layout (virtual list/grid), P2 scroll, P4 window realize | **virtualization** (`app-requirements` §3.2) |
| **SelectionState** (sparse side-table) | 24 | tiny `Dictionary<NodeHandle,Handle>` index → `SlabAllocator<SelectionState>`; **NOT a NodePaint field** (keeps NodePaint at 64B); anchor/extent text-positions + affinity + bake-ticket | record (resolve→`DrawSelectionRectCmd`), input (drag), UIA `ITextRangeProvider` | this doc (placement) / **`text.md`** (semantics, L1) |
| **`_borderBrushes`** (sparse side-table; SHIPPED) | — | sparse map MIRRORING the gradient side-table (`Set`/`TryGet`/`Clear` + `FreeSubtree` removal); holds the per-node `GradientSpec` for the gradient elevation border; `BorderWidth` stays in the dense `NodePaint` column | record (resolve→`DrawGradientStroke`) | this doc (placement) / **`gpu-renderer.md`** (`DrawGradientStrokeCmd` shape + raster) |
| **FlowState** | 4 | flat POD column on the spine: `{Inherited:byte, Resolved:byte, _pad:ushort}`; written at `WriteLayout` | layout (logical→physical mirror), record (RTL overlay placement) | this doc (placement) / **`layout.md`** (resolution, L5) |
| **A11yRel** (cold slab) | 24 | `SlabAllocator<A11yRel>`; `A11yRelRef:int` in `A11yInfo` (0 = shared none-row); `SetSize`/`PositionInSet`/`Level`/`DescribedBy`/`FullDescription`/`FlowsTo` | UIA only (when `UiaClientsAreListening`) | this doc (placement) / **`input-a11y.md`** (semantics, L6) |
| **UpdateQueueSlab** (slab) | per-record 24 | `SlabAllocator<UpdateRecord>` + per-component `UpdateQueueHead:int` head-index; intrusive `NextInQueue` link; lane byte carried | phase 3 hook-flush (drain), phase 5 reconcile (consume) | this doc (placement) / **`reconciler-hooks.md`** (lane semantics, P1/P2a) |

```csharp
[StructLayout(LayoutKind.Sequential, Size = 24)]
public struct Topology { public int Parent, FirstChild, LastChild, PrevSibling, NextSibling, ChildCount; }

[StructLayout(LayoutKind.Sequential, Size = 64)]                 // one cache line
public struct NodePaint {
    public Affine2D LocalTransform;  // 24 — 2x3 row-major; maps local→parent
    public float    Opacity;         // 4
    public BrushHandle Fill, Stroke; // 8 — Null = none
    public float    StrokeWidth;     // 4
    public CornerRadius4 Corners;    // 16
    public ClipHandle Clip;          // 4 — explicit geometry clip; Null = inherit/none
    public byte VisualKind;          // 1 — NodeVisual enum
    public byte Layer;               // 1 — NeedsLayer routing
    public int  EffectAuxRef;        // 4 — -1 none; else EffectAux slab row (backdrop owns semantics)
    public float BlurSigma;          // 4 — per-node self-blur σ px (default 0); animated by AnimChannel.BlurSigma.
                                     //     σ>0 ⇒ recorder wraps the subtree in PushLayer{Blur} (gpu-renderer §7.1);
                                     //     PaintDirty only. Semantics: backdrop-effects-animation.md FA-2 (the
                                     //     Expressive Motion Kit). As-built dense field; the original sketch routed
                                     //     blur through EffectAux, but self-blur is a hot, common, animating channel.
}
```

`Affine2D` is a 2×3 affine (2.5D/perspective out of scope for v1, architecture-spec §5.4). `Bounds` is **LOCAL**
and `LocalTransform` maps local→parent — the composed **`WorldTransform[]`** is derived once top-down in phase 7
(animation) and lives in a `FrameCache` double-buffer (not a SceneStore column; it is per-frame derived state
the seam value-copies — §6.3 here, §3.2 of the seam doc).

### 2.3 `NodeFlags` — the single 32-bit pre-filter column (3 dirty axes + state)

```csharp
[Flags] public enum NodeFlags : uint {
    // 3 orthogonal self dirty axes
    LayoutDirty   = 1u<<0,  PaintDirty   = 1u<<1,  TransformDirty = 1u<<2,
    LayoutSelfDirty          = 1u<<3,   // own intrinsic size-affecting prop changed
    LayoutParticipationDirty = 1u<<4,   // flex-grow/shrink/basis/align-self/margin/order/position changed
    // subtree aggregates (lazy UPWARD propagation; OPTIONAL — see §3)
    SubtreeLayout = 1u<<5,  SubtreePaint = 1u<<6,  SubtreeTransform = 1u<<7,
    // state
    Visible        = 1u<<8,  HitTestVisible = 1u<<9,  ClipsToBounds = 1u<<10, PointerTransparent = 1u<<11,
    WantsPointer   = 1u<<12, WantsKey       = 1u<<13, WantsWheel    = 1u<<14,
    Realized       = 1u<<12, // NOTE: cached text/path realization valid (architecture-spec §2); NOT "row in realized window"
    VirtualRangeDirty = 1u<<13, StickyPinned = 1u<<14,  // virtualization (app-requirements §3.2) — DISTINCT free bits
    Focusable = 1u<<16, IsTabStop = 1u<<17, FocusScope = 1u<<18, IsFocused = 1u<<19,
    Passthrough = 1u<<20,    // ComponentAnchor — skipped by layout/paint/child-snapshot
    A11yPresent = 1u<<24, A11yRaw = 1u<<25, A11yLiveRegion = 1u<<26, A11yOffscreen = 1u<<27,
    Detached = 1u<<28, NewThisFrame = 1u<<29, StaleModifiers = 1u<<30,
}
```

> **Bit-assignment note (folded naming fix).** `Realized=1<<12` (cached text/path realization, architecture-spec
> §2) and `WantsPointer=1<<12` collide in the raw architecture-spec §4.4 listing; the real bit map keeps the
> three input-want bits at 12/13/14 only when `Realized` is **re-homed** to a free high bit, and the
> virtualization additions use **distinct** contiguous free bits `VirtualRangeDirty=1<<13`/`StickyPinned=1<<14`
> only after the want-bits are relocated (`app-requirements` §3.2 explicitly forbids reusing `Realized`).
> `OQ-1`: pin the final 32-bit assignment once layout (`Realized`) and input (want-bits) lock their needs; the
> column is 32-bit and there is room, this is a bookkeeping reconciliation not a design conflict. **The folded
> gap-analysis additions take free high bits:** `HasSelection` (the record walk probes the sparse
> `SelectionState` index for this `VisualKind.Text` node — §2.7/L1) and `RtlResolved` (this node resolved to RTL
> — a record-time overlay-placement fast-path so the layout-mirrored geometry branch is one flag test, not a
> `FlowState` re-read — §2.7/L5). Both are **probe/traversal hints, never skip-decision inputs** (§3). They join
> the same `OQ-1` final-assignment pass; there is ample room in the 32-bit column.

**Dirty taxonomy (folded MAJOR).** The reconciler distinguishes **intrinsic-affecting** dirt
(width/height/min/max/aspect/content → `LayoutSelfDirty`) from **participation-affecting** dirt
(flex-grow/shrink/basis/align-self/margin/order/position → `LayoutParticipationDirty`). Participation dirt
**always** forces the parent container's flex/grid algorithm to re-run regardless of whether the child's
measured size changed; only intrinsic dirt that leaves measured size identical may stop at the node (layout
owns the stop rule). Animation writes **Transform/PaintDirty only, never LayoutDirty** (architecture-spec §4.8).

### 2.4 `VisualKind` and per-kind payload

```csharp
public enum VisualKind : byte {
    Container, RoundRect, Text, Path, Image, Backdrop,
    ComponentAnchor,   // Passthrough zero-cost identity node (reconciler-hooks §5.2)
    Video,             // hole-punch (media-pipeline)
    IconLayer,         // one ThemedIcon vector layer (controls.md): a colorless coverage mask tinted per-instance.
                       //   REUSES the Image payload slot as the interned `IconGeometryTable.Shared` pathId, and `Fill`
                       //   as the theme-live layer tint — no new NodePaint field (the 64B cache line holds). Recorded
                       //   as DrawIconMask (payload: gpu-renderer.md). Framework-owned column doubling; see the note below.
}
```

`Identity.PayloadRef` is a tagged index: `Text→GlyphRunHandle`, `Path→PathHandle`, `Image→ImageHandle`,
`Backdrop→BackdropRef`, else `Null`. The realization *content* lives in the relevant retained table; the node
holds only the handle so a content-epoch bump invalidates clean spans without touching the node row.

> **`VisualKind.IconLayer` reuses the Image payload slot** (the `ImageId` column doubles as the interned
> `IconGeometryTable.Shared` pathId; `Fill` doubles as the resolved, theme-live layer tint) so no new `NodePaint`
> field is added — the 64-byte cache line is preserved. The masks are colorless, so a retheme recolors via the bound
> `Fill` thunk with NO re-raster. `controls.md`/`gpu-renderer.md` own the ThemedIcon semantics + the mask payload;
> this doc owns only the enum entry + the column-doubling rule.

### 2.5 Interning / realization tables (Scene-adjacent slabs, content-hash dedup)

```csharp
// FluentGpu.Scene — all SceneStore-adjacent SlabAllocators; the spine resizes them independently.
BrushTable       : SlabAllocator<BrushData>,        content-hash dedup → stable BrushHandle
ClipTable        : SlabAllocator<ClipData>,         content-hash dedup → stable ClipHandle
GlyphRunTable    : SlabAllocator<GlyphRunRealization>, content-EPOCH stamped (text.md owns the realization)
EffectChainTable : SlabAllocator<EffectChainDesc>   (backdrop owns the desc fields)
// FluentGpu.Foundation:
ImageRefTable    : SlabAllocator<ImageRealization>, HandleKind.ImageRef (media-pipeline owns the realization)
StringTable      : intern ↔ resolve
```

**Content-hash dedup is the precondition that makes clean-span `memcpy` sound for brushes/clips.** Because
identical `BrushData`/`ClipData` returns the same handle and the content cannot change under a stable handle,
a brush/clip "epoch" **degenerates to `IsLive`** (no epoch needed) — SAFE-by-construction (hardened §3 ledger,
threading §7). The structurally-different tables — `GlyphRunTable`, `ImageRefTable`, path-tess, the image atlas
— are **mutated in place** (atlas repack, re-decode, tessellation refresh) and therefore carry a
**`ContentEpoch`** that the clean-span rule checks (§4.4). `BrushData`'s color convention is **straight-alpha
sRGB float4** stored, realized to **linear-premultiplied on the CPU once** per brush change (theming/gpu-renderer
own the realization); the dedup key is the straight-alpha source so two equal authored brushes share a slot.

```csharp
public enum BrushKind : byte { Solid, LinearGradient, RadialGradient, Image }
public struct BrushData {            // gpu-renderer §9 owns the realized GPU fields (GradientTexSlice etc.)
    public BrushKind Kind;
    public ColorF    SolidStraightSrgb;   // dedup/intern key is straight-alpha sRGB (theming-friendly)
    public GradientRef Gradient;          // (offset,count) into a stops side-slab
    public ImageHandle Image;             // image brush → ImageRefTable indirection (gpu-renderer §9)
    public Affine2D  ImageTransform; public byte ExtendMode;
}
public struct ClipData {             // gpu-renderer §6 owns the 3-tier consume
    public ClipKind Kind;            // ScissorRect | SdfRoundRect | StencilPath
    public RectF Rect; public CornerRadius4 Radii; public PathHandle Path; public ClipHandle Parent;
}
```

### 2.6 Hot/cold split (the ~96B-vs-~220B resolution)

`InteractionInfo` is hot-ish (input pre-filters on `HandlerMask`+`NodeFlags`). `A11yInfo`/`FocusNav` are **cold**
(scanned only when an AT is attached / on focus nav) and live on the spine but in separate cache-line groups so
the record/layout walks never touch them. `LayoutAux`/`EffectAux`/`VirtualState`/`SelectionState`/`A11yRel` are
**referenced cold slabs**: the hot row carries a 4-byte ref (`-1`/`0` = shared zero-sentinel for the common leaf,
or a sparse side-index for `SelectionState`), the small fraction of nodes that use them index a side
`SlabAllocator`. This resolves the architecture-spec §4.4 "~96B vs ~220B" contradiction by a ratified hot/cold
split. `FlowState` is the **one exception** — it is a 4-byte *hot-spine* column (not a cold-slab ref) because
RTL resolution is per-node and read by *both* layout and the record-time overlay-placement path; 4 bytes on the
spine costs less than the indirection, and it packs into the same cache-line group as `NodeFlags`.

### 2.7 New feature-column structs (storage shapes; SEMANTICS owned by the feature docs)

These are the SoA column-storage shapes the gap analysis folds into core. **This doc owns only the byte layout
and slab placement**; the cited doc owns every field's meaning, every transition, and the read/write algorithm.

```csharp
namespace FluentGpu.Scene;

// ── L1: text selection (text.md owns semantics; gpu-renderer.md owns DrawSelectionRectCmd raster) ──
[StructLayout(LayoutKind.Sequential, Size = 24)]
public struct SelectionState {            // one per node that HAS a live selection (sparse side-table, §2.6)
    public int   AnchorPos;               // 4  — text position (cluster-map index, text.md §8); -1 = collapsed
    public int   ExtentPos;               // 4  — text position; AnchorPos==ExtentPos ⇒ caret only
    public byte  AnchorAffinity;          // 1  — Upstream|Downstream (BiDi line-boundary disambiguation)
    public byte  ExtentAffinity;          // 1
    public byte  SelectionKind;           // 1  — None|Caret|Range|All (text.md enum)
    public byte  _pad;                    // 1
    public uint  ContentEpoch;            // 4  — bumped by Mutate() when selection geometry rebakes (§4.4)
    public ulong BakedRectsHash;          // 8  — hash of the device-space highlight rects (clean-span witness, §4.4)
}
// Placement: SlabAllocator<SelectionState> + a sparse Dictionary<NodeHandle,Handle> index (selections are
// rare — at most one caret + a few range selections live at once). NOT a NodePaint field (NodePaint stays 64B).
// The record walk probes the index ONLY for VisualKind.Text nodes whose NodeFlags has HasSelection (1<<21).

// ── L5: RTL flow direction (layout.md owns logical→physical resolution at WriteLayout) ──
public enum FlowDirection : byte { Inherit = 0, LeftToRight = 1, RightToLeft = 2 }
[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct FlowState {                 // hot-spine column (see §2.6 exception)
    public FlowDirection Inherited;        // 1 — author request (Inherit = take parent's resolved)
    public FlowDirection Resolved;         // 1 — concrete LTR/RTL after WriteLayout inheritance walk; never Inherit
    public ushort _pad;                    // 2
}
// layout.md reads Resolved to mirror flex main-axis / justify / align / Edges4 start↔end → physical at the
// WriteLayout boundary, so the ported Yoga CalculateLayoutImpl stays bit-for-bit physical (gap §5.5 / L5).

// ── L6: UIA collection relations + descriptions (input-a11y.md owns the provider projection) ──
[StructLayout(LayoutKind.Sequential, Size = 24)]
public struct A11yRel {                   // cold extension of A11yInfo; row 0 = shared "none"
    public int      SetSize;              // 4  — total items in the set (-1 = not in a set)
    public int      PositionInSet;        // 4  — 1-based position (virtualizer supplies index+count, L6)
    public int      Level;                // 4  — tree/heading level (0 = none)
    public StringId FullDescription;      // 4  — UIA FullDescription (interned, edge-formatted, no paint-path string)
    public Handle   DescribedBy;          // 8  — NodeHandle of the describing element (FlowsTo packs alongside via
                                          //      a parallel A11yRel2 row only when needed; common node uses neither)
}
// Placement: SlabAllocator<A11yRel>; A11yInfo gains a 4-byte A11yRelRef:int (0 = shared none-row). Scanned only
// when UiaClientsAreListening, exactly like A11yInfo (§2.6 cold). FlowsTo, when present, is a second optional
// row to avoid widening the common-case struct.
```

`A11yInfo` gains one 4-byte field to reference the extension:

```csharp
// A11yInfo (input-a11y.md owns the existing fields) gains:
public int A11yRelRef;   // 4 — 0 = shared none-row; else A11yRel slab row. A11yInfo grows 24→28B (still cold).
```

> **`NodeFlags` additions (folded into §2.3, see the bit note there):** `HasSelection` (record probes the
> sparse selection index for this Text node) and `RtlResolved` (fast-path: this node and ancestors resolved to
> RTL — lets the record-time overlay-placement path branch without re-reading `FlowState`). Both take free high
> bits per `OQ-1`; they are *traversal/probe hints*, never skip-decision inputs (§3).

### 2.8 `UpdateQueueSlab` — the phase-3 lane/update-queue backing storage (P1/P2a)

`reconciler-hooks.md` is replacing the reserved phase-3 no-op with a real **update queue**: each `setState`
enqueues an `(fiber, updater, lane)` record instead of mutating its cell in place, and the queue drains once per
coalesced frame (automatic batching falls out; lanes order the drain). **This doc owns the slab the records live
in and the per-component head-index — not the lane values or the drain order** (those are `reconciler-hooks.md`).

```csharp
namespace FluentGpu.Scene;   // backing storage only; lane meaning + drain = reconciler-hooks.md

[StructLayout(LayoutKind.Sequential, Size = 24)]
public struct UpdateRecord {
    public int    ComponentSlot;     // 4  — Identity.ComponentSlot of the owning component (back-pointer for drain)
    public int    NextInQueue;       // 4  — intrusive link: next record for THIS component (-1 = tail)
    public byte   Lane;              // 1  — lane bitmask byte (VALUES owned by reconciler-hooks.md P1)
    public byte   Kind;              // 1  — SetState | Dispatch | Optimistic | Transition (reconciler-hooks.md)
    public ushort _pad;             // 2
    public ulong  UpdaterRef;        // 8  — GcDepTable side-index (the updater delegate/value is a GC ref → NOT
                                     //      stored inline; SPEC-INDEX §2 DepKey rule: no GC ref in a blittable slab)
    public uint   EnqueueEpoch;      // 4  — frame ordinal of enqueue (drain-after-complete + carry-forward gate, P5)
    public uint   _resv;            // 4
}

public sealed class UpdateQueueSlab {            // UI-thread-confined; one instance, all components share it
    SlabAllocator<UpdateRecord> _records;        // pooled records, free-listed (zero per-frame growth steady state)
    // per-component head index lives in the component-slot table (reconciler-hooks.md owns ComponentSlotData);
    // this slab exposes the enqueue/drain-walk primitives over the intrusive NextInQueue link:
    public Handle Enqueue(int componentSlot, int prevHeadOrNeg1, byte lane, byte kind, ulong updaterDepRef, uint epoch);
    public ref UpdateRecord Get(Handle r);       // gen-checked in DEBUG
    public void  RetireComponentQueue(int headRecordIndex);   // free-list the whole chain after a complete drain
    public void  ResetFrame();                   // phase 13: free-list any fully-drained chains (O(drained))
}
```

**Why a slab and not a `ChunkedArena`.** Update records can outlive a single frame under carry-forward slicing
(P4/P5): an update enqueued in frame N's partial reconcile must survive into N+1's completion, so the records
need *stable, gen-validated* slots (a `ChunkedArena.Reset` at phase 13 would free them mid-flight). The
`SlabAllocator<UpdateRecord>` gives stable handles + a free list; `EnqueueEpoch` is the gate `reconciler-hooks.md`
uses to assert "drain only after a *complete* reconcile" (P5 carry-forward effect-consistency). **Zero-alloc:**
steady state recycles record slots via the free list (no growth); the GC-ref updater goes through the existing
per-render `GcDepTable` (SPEC-INDEX §2 DepKey rule — never a GC pointer inside the blittable slab), so the slab
stays pure POD and the no-GC-ref-in-slab invariant holds. Lane *values*, the urgent-vs-transition cause mapping,
`startTransition`/`UseTransition`, and `RenderPriorityPolicy`-as-executor are all `reconciler-hooks.md` §7.

---

## 3. Dirty tracking — 3 axes + the arena dirty WORKLIST (idle O(0))

**MADE: the PRIMARY mechanism is an arena-backed dirty-node WORKLIST that `MarkXDirty` appends to** (per-frame
cost = O(dirty), not O(capacity)). The `Subtree*` aggregate bits and the `Vector256`/`Vector128` `FlagsSpan`
SIMD scan are kept **only** for the full-rebuild / overflow path. This makes idle frames **truly O(0)** and
removes the cross-phase aggregate-clearing correctness hazard (aggregates, if maintained at all, are recomputed
in a single bottom-up sweep, never cleared piecemeal per consuming phase). Losing option: scan `Flags[]` every
frame (O(capacity) idle cost — the WinUI-style "walk the world to find the one dirty thing").

```csharp
namespace FluentGpu.Scene;

public sealed class DirtyWorklist                    // arena-backed; UI-thread-owned
{
    // per-axis append-only lists in the per-frame ChunkedArena; deduped via a NewThisFrame-style bit
    ChunkedArena _arena;
    ArenaSpan<int> _layout, _paint, _transform;       // node slot indices touched this frame

    public void MarkLayout(int slot, NodeFlags how);  // append + set Flags + propagate SubtreeLayout upward (lazy, early-out)
    public void MarkPaint(int slot);
    public void MarkTransform(int slot);
    public ReadOnlySpan<int> LayoutDirty => _layout.AsSpan();   // layout consumes the worklist, not Flags[]
    public void Reset() => _arena.Reset();             // O(1) at phase 13
}
```

Upward propagation: `MarkX` walks `Parent` setting the matching `Subtree*` bit, **early-out the instant it is
already set** (so a hot subtree propagates at most once per frame). Crucially, `SubtreeDirty` is **traversal
scope only** — it tells the reconciler/record walk which subtrees to descend, it is **never** a skip-decision
input (the 3-signal memo skip is the reconciler's, `reconciler-hooks.md` §6.3; the keystone correctness fix).
The full-rebuild path (first frame, resize, theme swap, device-lost re-realize) ignores the worklist and runs
the `Vector256` flag scan over the whole `Flags[]` column instead.

13-phase placement of dirty production/consumption: **reconcile (5)** and **animation (7)** produce dirt
(`MarkDirty`); **layout (6)** consumes `LayoutDirty`; **record (8, render thread)** consumes `PaintDirty`/
`TransformDirty` *from the published snapshot's `Flags`*, not the live worklist. The worklist `Reset()` is part
of the UI-thread per-frame arena reset (phase 13 on the single-thread build; independent of the render-thread
DrawList arena rotation — threading §14).

---

## 4. DrawList encoding framework (the byte-level container)

This subsystem owns the **container**; `gpu-renderer.md` owns the **payloads** inside it and the SortKey
**layout**, and `threading-render-seam.md` owns the **arena lifetime/rotation**. The split is exact: anything
about *how a command is laid out in bytes, addressed, and reused* is here; anything about *what a specific
opcode's fields mean* is in gpu-renderer.

### 4.1 Physical format — 8-byte header + fixed POD payload, parallel `ulong[]` sortkeys

```
render-private arena (one set per DrawList arena slot; ring of ≥3, threading §6):
 bytes:    ┌──────────┬─────────────────┬──────────┬─────────────────┬ …
           │ DrawCmd  │ payload (POD)   │ DrawCmd  │ payload (POD)   │
           │ 8B header│ PayloadSz bytes │ 8B header│ …               │
           └──────────┴─────────────────┴──────────┴─────────────────┘
 sortkeys: ┌──────────┬──────────┬ …   (parallel ulong[], index = command ordinal — folds FA-2)
           │  ulong   │  ulong   │
           └──────────┴──────────┘
```

```csharp
namespace FluentGpu.Render;

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct DrawCmd {
    public DrawOp Op;          // 1
    public byte   Flags;       // 1  — AA mode / premultiplied / batcher hints (per-opcode meaning: gpu-renderer)
    public ushort PayloadSz;   // 2  — bytes following the header
    public uint   _resv;       // 4  — the contract's old 32-bit SortKey field, now RESERVED:
                               //      the real 64-bit SortKey lives in the parallel ulong[] (FA-2).
}
```

**The opcode ENUM list (owned here; one entry per payload that gpu-renderer/text/media/input/backdrop define):**

```csharp
public enum DrawOp : byte {
    // rect family (payloads: gpu-renderer.md)
    FillRoundRect, FillRoundRectStroke, DrawShadow, FillGradient,
    DrawGradientStroke,   // = 11 (SHIPPED): a gradient-tinted SDF outline — the WinUI (Accent)ControlElevationBorder.
                          //   The gradient PS is sampled along the local axis and drawn as a stroke band centered on
                          //   the rounded-box edge. REUSES the GradientPipeline (a spare pad float in the 160-byte
                          //   GradientInstance becomes `Stroke`; stride/root-sig unchanged). Shape+raster owned by
                          //   gpu-renderer.md; carried by the sparse `_borderBrushes` side-table (mirrors `_gradients`),
                          //   driven by the `BoxEl.BorderBrush` (`GradientSpec?`) DSL field + `.BorderBrush(spec,width)`.
    // content (payloads: text.md / media-pipeline.md)
    DrawGlyphRun, DrawImage, DrawVideo,
    DrawIconMask,         // a ThemedIcon vector-layer mask (controls.md; shape+raster: gpu-renderer.md DrawIconMaskCmd):
                          //   a CPU-rasterized colorless R8 coverage mask (interned geometry in IconGeometryTable.Shared,
                          //   keyed by PathId), tinted per-instance and drawn through the EXISTING glyph atlas/PSO — no
                          //   new shader/PSO/texture/RHI method. Deliberately NOT the FillPath/StrokePath tessellation
                          //   lane (same non-tessellation-sibling posture as DrawTabShape). Same POD-registration contract.
    // paths (payloads: gpu-renderer.md)
    FillPath, StrokePath,
    // clip / layer / transform stack (payloads: gpu-renderer.md; PushLayer{Effect}: backdrop)
    PushClipRect, PushClipRoundRect, PushStencilClip, PopStencilClip, PopClip,
    PushLayer, PopLayer, PushTransform, PopTransform,
    // overlays (payloads: input-a11y.md / text.md)
    DrawFocusRect, DrawAccessKeyBadge,
    DrawFocusRing,        // FOLDED (L6/L4): the real two-tone Fluent focus ring (shape+raster: gpu-renderer.md
                          //   DrawFocusRing; supersedes the DrawFocusRect placeholder which stays only for the
                          //   simple rectangular debug case). Emitted by the focus engine / overlay manager.
    DrawSelectionRect,    // FOLDED (L1): per-BiDi-fragment text-selection highlight rect (shape+raster:
                          //   gpu-renderer.md DrawSelectionRectCmd; geometry from text.md GetSelectionRects).
    DrawScrim,            // FOLDED (L4): overlay dismiss-layer (modal-dim / transparent light-dismiss /
                          //   blur-promote). Shape+raster owned by gpu-renderer.md DrawScrimCmd; pushed by
                          //   input-a11y.md §8.3 (light-dismiss FSM). Same registration contract as DrawSelectionRect.
    // backdrop stub (payload: backdrop-effects-animation.md)
    DrawBackdrop,
}
```

> The payload structs (`FillRoundRectCmd`, `DrawImageCmd`, `DrawGlyphRunCmd`, `PushLayerCmd`,
> **`DrawSelectionRectCmd`, `DrawFocusRingCmd`**, …) are defined in the owning subsystem docs and **not restated
> here**; this doc only guarantees they are blittable `[StructLayout(Sequential)]` POD with **handle/index
> references only** (no GC pointers), so a `DrawCmd`+payload pair is pure bytes that `memcpy`s safely and crosses
> no GC concern. The encoding framework treats every payload as `PayloadSz` opaque bytes.
>
> **Opcode-registration contract for the folded opcodes.** Registering `DrawSelectionRect`/`DrawFocusRing`/`DrawScrim`
> in the `DrawOp` enum is *this doc's* job (the enum is the framework's); the payload field layout + raster +
> SortKey lane assignment are `gpu-renderer.md`'s. The framework imposes exactly two requirements on the new
> shapes, identical to every existing opcode: (1) blittable POD addressed only by `RectF`/handle/`StringId`
> (so the clean-span `memcpy` and the cross-thread/cross-OS POD-stream boundary stay pure — §10); (2) the
> baked device-space rects live *inside* the payload (e.g. `DrawSelectionRectCmd.Rect`,
> `DrawFocusRingCmd.OuterRect`/`InnerRect`), so the baked-geometry-hash limb of the reuse rule (§4.3 rule 3)
> and the `CleanSpanWitness` (§4.4) cover them with no special-casing. Selection highlights are **overlay
> opcodes** (recorded after content into the overlay z-layer per `gpu-renderer.md` SortKey), so a selection
> change re-records only the overlay span, never the underlying `DrawGlyphRun` span.

### 4.2 The writer and the arenas (zero-alloc, render-private, ≥3-deep)

The DrawList byte and `ulong[]` arenas are backed by `GC.AllocateUninitializedArray<T>(cap, pinned: true)`
(skip the memset at multi-KB sizes; pinned removes GC fix-up before native submit). The recorder writes through
an **`IBufferWriter<byte>` contract over the arena cursor** — never `ArrayBufferWriter` (hidden grow+copy),
never `Pipe`/`ReadOnlySequence`.

```csharp
public ref struct DrawListWriter {
    Span<byte> _bytes; int _byteHead;
    Span<ulong> _keys; int _cmdCount;
    public ref T Append<T>(DrawOp op, ulong sortKey) where T : unmanaged {   // header + payload + key in one
        ref var hdr = ref WriteHeader(op, (ushort)Unsafe.SizeOf<T>());
        _keys[_cmdCount++] = sortKey;
        return ref Unsafe.As<byte, T>(ref _bytes[_byteHead - Unsafe.SizeOf<T>()]);
    }
}
```

**Arena lifetime is the seam doc's, not this one's.** Under the render-thread seam the arenas are
**render-thread-private and ≥3-deep** (threading §6): record reads its own previous arena for clean-span
memcpy; the UI thread never swaps or resets a DrawList arena. In the single-thread build order step 1, the same
ring exists logically (the UI thread produces and consumes) at depth ≥3 still, so the shape is identical and
flipping the thread later is a thread-assignment change, not a format change. This doc fixes the *format* and
the *reuse rule*; the seam fixes *who owns and rotates the ring*.

### 4.3 The reuse rule statement (mechanism here; authoritative statement in gpu-renderer §11.1)

A memcpy'd clean DrawList span is **valid IFF**:

1. **every handle it references `IsLive`** (gen matches the slot), **AND**
2. **for `GlyphRunHandle`, `ImageHandle`, and `SelectionState` (L1) it references, the backing realization's
   `ContentEpoch` is unchanged** (brush/clip handles degenerate to `IsLive`-only via content-hash dedup — §2.5;
   `SelectionState` is in-place-mutated like a glyph run, so it carries an epoch — §2.7/§4.4), **AND**
3. **its baked-geometry hash is unchanged** — device-space rects live *inside* command payloads
   (`FillRoundRectCmd.Rect`, `DrawGlyphRunCmd.Origin`), so a `Bounds`-move-without-`PaintDirty` would otherwise
   pass the handle/epoch check while shipping stale geometry.

Epoch validation is **render-thread-LOCAL** (threading §4 R1 / §7): compare the *live* epoch against the
per-span epoch recorded into the render thread's own back arena — zero cross-thread epoch staleness.
`TransformDirty`-only nodes **reuse** their span; the batcher re-applies the new `WorldTransform[node]` to the
cached instanced quads at submit (no re-record) — composition-style independent animation. This doc owns the two
mechanisms that make (2)+(3) enforceable: the `Mutate()` chokepoint and the `CleanSpanWitness` (§4.4).

### 4.4 `Mutate()` epoch chokepoint + DEBUG `CleanSpanWitness`

**MADE: every write that changes the pixels or geometry of an in-place-mutated realization goes through a single
`Mutate()` chokepoint** that (a) bumps the realization's `ContentEpoch` and (b) registers the handle in the
per-layer `RealizationDirtySet`. There is exactly **one writer** (the UI thread, on admit/evict/refresh
callbacks — `reconciler-hooks.md` homes the call; this doc owns the Scene-side method). A second independent
oracle hashes the actual realization backing bytes (catches a forgotten bump or an untracked reference) in CI
fault-injection (hardened §4.4).

```csharp
// One chokepoint per in-place-mutated table. Example shape (ImageRefTable; GlyphRunTable/path-tess identical):
public bool Mutate(ImageHandle h, in ImageRealization next) {   // UI thread; ThreadGuard.AssertUi()
    ref var slot = ref _slab.Get(h.H);
    slot = next;
    slot.ContentEpoch++;                       // the SINGLE place an epoch is bumped
    _realizationDirty.Register(h.H);           // per-layer set drives clean-span auto-invalidation
    return true;
}
```

`RealizationDirtySet` is made **O(1)** via a per-layer precomputed sorted handle-set produced at record time (no
per-frame span re-decode); huge layers fall back to re-record above a threshold (hardened §4.4).

```csharp
[Conditional("DEBUG")]   // [Conditional("FGWITNESS")] in CI; erased from shipping AOT (production safety == CI coverage)
public static class CleanSpanWitness {
    // recorded at the moment a clean span is written into the back arena:
    public readonly record struct Witness(Handle Node, uint NodeGen, uint ContentEpoch, ulong BakedGeomHash);
    // validation at clean-span-reuse time: recompute dest rects from CURRENT Bounds[]/WorldTransform[] and assert
    public static void Validate(in Witness w, in SceneStore store, in Affine2D world);
}
```

The witness captures **handle + gen + content-epoch + baked-geometry hash** (the four signals the reuse rule
needs); the validator recomputes the device-space dest rect from the *current* `Bounds[]`/`WorldTransform[]` and
asserts equality — so a span whose geometry moved without `PaintDirty` is caught (the Bounds-move blocker,
hardened §4.4). Alternatively all `Bounds` writes route through a chokepoint with an asserted post-condition;
the witness is the belt, the chokepoint the suspenders.

**Folded extension — `SelectionState` baked geometry (L1).** A text-selection highlight (`DrawSelectionRect`
overlay span) bakes its device-space rects from `GetSelectionRects` *plus* the node's `WorldTransform`/`Bounds`,
and its source — the `SelectionState` anchor/extent/affinity — can change *without* the underlying
`GlyphRunHandle`'s `ContentEpoch` moving (selecting different text does not re-shape the run). Two independent
mutation sources therefore feed one baked overlay span, so the witness limbs are extended exactly as for any
in-place-mutated realization, with **no new mechanism**:

1. `SelectionState` carries its **own `ContentEpoch`** (§2.7), bumped only through the same `Mutate()`
   chokepoint (`Mutate(SelectionHandle, in SelectionState)` — UI thread, `ThreadGuard.AssertUi()`), which also
   registers it in the per-layer `RealizationDirtySet`. So an anchor/extent edit auto-invalidates the overlay
   span via the existing rule-2 epoch check — the `DrawGlyphRun` content span underneath stays clean.
2. The witness's existing **baked-geometry-hash** limb (rule 3) already covers the rect-moves-with-`Bounds`
   case for the highlight rects, because the device-space rects live inside `DrawSelectionRectCmd` (§4.1). The
   `SelectionState.BakedRectsHash` field is the value the witness records and the validator recomputes from the
   *current* `GetSelectionRects`×`WorldTransform`.

`CleanSpanWitness.Witness` therefore needs no shape change — `ContentEpoch` already generalizes over
glyph/image/selection realizations, and `BakedGeomHash` already generalizes over content/overlay rects. The
selection case is a **new producer of the same four signals**, not a new witness limb (the design intent of
keeping the witness keyed on `(Node, gen, epoch, bakedHash)` rather than on opcode kind).

---

## 5. `ISceneBackend` — the Scene-side mutation API (the reconciler↔scene bridge)

The *interface* is declared in `FluentGpu.Scene` and co-owned with `reconciler-hooks.md` (which consumes it and
owns `NodeChildCollection`'s usage); this doc owns its **Scene-side implementation** — what columns each op
writes, the two-phase growth lock, and the dirty marking. **Handle-in / handle-out, POD-only, zero COM, zero
GC-ref-into-pool.** No `ComPtr`, no `UIElement`, no `System.Object` ever crosses it — that is what keeps the
reconciler render-thread-agnostic and lets the render thread own every ComPtr (threading §1.1).

```csharp
namespace FluentGpu.Scene;
public interface ISceneBackend
{
    // node lifecycle (structural sub-phase; growth allowed)
    NodeHandle CreateNode(ushort elementTypeId, VisualKind kind);
    void       DestroyNode(NodeHandle node);          // gen++, free columns, release Brush/Clip/Image/PayloadRef rows
    // topology (writes the doubly-linked Topology column)
    void InsertChild(NodeHandle parent, NodeHandle child, int index);
    void MoveChild  (NodeHandle parent, NodeHandle child, int newIndex);   // O(1) pointer splice
    void RemoveChild(NodeHandle parent, NodeHandle child);                 // detach only; caller Destroys
    int  ChildCount (NodeHandle parent);
    NodeChildCollection Children(NodeHandle parent);   // borrowed O(n)-walked-ONCE snapshot span (§5.2)
    // property writes (edit sub-phase; growth locked; mask says which fields are live → zero-alloc diff)
    void WriteVisual (NodeHandle node, in VisualProps p, VisualMask mask); // → NodePaint columns
    void WriteLayout (NodeHandle node, in LayoutInput  p, LayoutMask mask); // → LayoutInput/LayoutAux + FlowState
                                                                            //   (the WriteLayout boundary is where
                                                                            //   FlowDirection inherited→resolved is
                                                                            //   written — layout.md owns the mirror, L5)
    void WriteText   (NodeHandle node, in TextProps    p, TextMask  mask);  // → GlyphRun realization key
    void WritePayload(NodeHandle node, PayloadRef payload);
    void WriteAnim   (NodeHandle node, in AnimWrite anim);                  // → EffectAux / AnimTrack seeds
    // folded feature-column writers (storage here; semantics in the cited doc)
    void WriteFlow     (NodeHandle node, FlowDirection inherited);          // → FlowState.Inherited (L5; resolution
                                                                            //   walk owned by layout.md at WriteLayout)
    void WriteSelection(NodeHandle node, in SelectionState sel);            // → SelectionState slab via Mutate();
                                                                            //   sets/clears HasSelection (L1; text.md)
    void WriteA11yRel  (NodeHandle node, in A11yRel rel);                   // → A11yRel cold slab (L6; input-a11y.md)
    // diff-on-Dispose edit scopes (capture+revalidate backing array on Dispose; assert _growthLocked)
    EditPaintScope  EditPaint(NodeHandle node);    // marks PaintDirty ONLY on a real delta
    EditLayoutScope EditLayout(NodeHandle node);   // marks LayoutDirty/Participation on a real delta
    // dirty marking (writes NodeFlags[]; propagates Subtree* upward — TRAVERSAL SCOPE only)
    void MarkDirty(NodeHandle node, NodeDirty flags);
    // identity columns the diff itself needs
    void SetKey(NodeHandle node, StringId key);  StringId GetKey(NodeHandle node);
    void SetComponentSlot(NodeHandle node, int slot);  int GetComponentSlot(NodeHandle node);
}
```

### 5.1 Two-phase reconcile (the growth-lock contract)

The reconciler runs the seam in **two sub-phases** (architecture-spec §4.2, §5 step 5; reconciler-hooks §5):

- **Structural sub-phase** (`_growthLocked=false`): all `CreateNode`/`InsertChild`/`MoveChild`/`RemoveChild`/
  `DestroyNode`. The slab may `Array.Resize`; no `ref T` is held across these calls. `Children()` snapshots run
  here (after structure settles for the keyed-LIS middle).
- **Edit sub-phase** (`_growthLocked=true`): all `WriteVisual`/`WriteLayout`/`WriteText`/`WritePayload`/
  `EditPaint`/`EditLayout`. **Non-allocating** by construction; `EnsureCapacity(count + newChildBatch)` ran at
  the boundary; `EditPaint`/`EditLayout` assert the lock and revalidate the backing array on Dispose. This is
  the structural (not DEBUG-only) defense against a stale-`ref` write through a resized column.

`EditPaint`/`EditLayout` **diff-on-Dispose** mirror Reactor's `CanSkipUpdate`/`ShallowEquals` short-circuit: an
unchanged node contributes **zero damage** and its DrawList span is `memcpy`'d clean (architecture-spec §5.4).
The mask-based `WriteX` is the only update path — a few `Unsafe`-blitted field stores into the columns at
`node.Index`, no COM readback, no boxing.

### 5.2 `NodeChildCollection` (incl. `RemoveAt`) — borrowed span, not N `ChildAt` walks

The SoA topology is a `Next`-sibling linked list, so N `ChildAt(parent,i)` probes are O(n²). The diff borrows
the whole child row **once** as a `Span<NodeHandle>` from the per-frame `ChunkedArena` (the `Children()` op
walks the sibling cursor a single time); all `Get(i)` are O(1) over the span; mutations write the real topology
columns. The SoA store stays pure (no per-parent `ChildHandle[]` materialization). `ComponentAnchor`
(`Passthrough`) nodes are collapsed out of the snapshot so a component host costs nothing in the child diff.

```csharp
internal ref struct NodeChildCollection {
    readonly ISceneBackend _b;  readonly NodeHandle _parent;  readonly Span<NodeHandle> _snapshot;
    public int Count => _snapshot.Length;                  // O(1)
    public NodeHandle Get(int i) => _snapshot[i];          // O(1) — NOT a sibling re-walk
    public void Insert(int i, NodeHandle child) => _b.InsertChild(_parent, child, i);
    public void Move(int from, int to)          => _b.MoveChild(_parent, _snapshot[from], to);
    public void RemoveAt(int i)                 => _b.RemoveChild(_parent, _snapshot[i]);  // explicit detach (keyed-middle)
    public void Replace(int i, NodeHandle child){ _b.RemoveChild(_parent, _snapshot[i]); _b.InsertChild(_parent, child, i); }
}
```

`RemoveAt(i)` is the explicit detach the keyed-middle uses when a key disappears (the reconciler then runs
cleanups + `DestroyNode`) — it was omitted in the original synthesis's `IChildCollection`; it is mandatory here.

### 5.3 Move / remove semantics on the store

- **Move** rewrites `Next`/`PrevSibling` + parent `FirstChild`/`LastChild`/`ChildCount` — pure index pointer
  surgery; the node's handle, all other columns, and its whole subtree are untouched. O(1), no
  re-realization/re-parenting (the doubly-linked-list win over WinUI's COM child collection).
- **Remove** detaches, the reconciler runs cleanups synchronously, then `DestroyNode` bumps generation and
  frees columns + releases `BrushHandle`/`ClipHandle`/`ImageHandle`/`PayloadRef` back to their tables. The gen
  bump means any stale `NodeHandle` (a captured effect closure, an in-flight snapshot) **fails `IsLive`** on
  next use — the ABA-safe dangling defense. **Slot reuse is consume-gated quarantined** (threading §5): a slot
  freed during production of frame `p` is not reusable until `_lastConsumedSeq > p`. The Scene `Free` only marks
  the slot freed + appends to the `QuarantineLedger`; **`ReclaimSlot` (the actual free-list push) is a separate
  call the ledger makes when the quarantine clears** — Scene exposes `ReclaimSlot(int slabIndex)` for exactly
  this. `QUARANTINE = RenderInFlightDepth + 1` (compile-asserted); = 0 in single-thread build step 1.
- A **`Detached` flag + deferred-free** keep a node alive for exit animations (backdrop/animation
  `DetachedAnim`); cascade-drained if the parent frees first.

---

## 6. Threading split — Scene owns the live columns; the seam owns the immutable copy

This is the explicit split the program lead pinned. **`threading-render-seam.md` owns
`SceneFrame`/`SnapshotColumns`/`CopyInto`** (the publisher-side POD, the triple buffer, the both-directions
volatile publish, the consume-gated quarantine, the retire-fence). **This doc owns the COLUMN LAYOUT those
snapshot.**

### 6.1 What the seam copies (projected from this doc's columns)

The seam's `SnapshotColumns` (threading §3.1) is a **projection** of the columns defined here into the
~120B/node *paint-relevant subset* the render thread needs: `Handles` (identity + `IsLive`),
`WorldTransform` (derived, §6.3), `BoundsLocal` (the `Bounds` column), `NodePaintLite` (the
`Fill`/`Stroke`/`Corners`/`Opacity`/`Clip`/`VisualKind`/`Layer`/`StrokeWidth`/`EffectAuxRef` subset of
`NodePaint`), `Flags` (`NodeFlags`), `PayloadRef`, and `TopologyLite` (`FirstChild`/`NextSibling`/`ChildCount`
for the record walk order). **Folded paint-relevant additions** (because the record walk now emits the new
overlay opcodes and mirrors RTL geometry):
- `FlowState.Resolved` is **copied** (1 byte/node, packed into the `NodePaintLite` group) — the record-time
  overlay-placement path needs the resolved direction; layout already consumed `Inherited`, so only `Resolved`
  crosses (L5).
- `SelectionState` is **copied for the (sparse) set of nodes with `HasSelection`** only — the seam projects the
  live selection side-table into a small `SelectionSnapshot[]` keyed by snapshot node ordinal, so the record
  thread resolves a Text node's highlight rects without touching the UI-thread side-table (L1). Its
  `ContentEpoch` rides along for the render-thread-local epoch check (§4.3 rule 2).

What is **NOT** copied: `LayoutInput`, `LayoutAux`, `A11yInfo`, `FocusNav`, `InteractionInfo`, `A11yRel`,
`UpdateQueueSlab`, `FlowState.Inherited` — those stay UI-thread-confined (layout is done; input/UIA read live
UI-thread state; `A11yRel` is UIA-only and never on the paint path; the update queue is reconcile-only). The
*shape* of each copied element is this doc's; the *copy* and its lifetime are the seam's.

### 6.2 The confinement, stated against this doc's tables

| This doc's mutable state | Sole writer | Cross-thread mechanism (owned by) |
|---|---|---|
| SceneStore columns | UI thread | value-copy at PUBLISH (threading §3) |
| `BrushTable`/`ClipTable`/`GlyphRunTable`/`EffectChainTable`/`ImageRefTable` intern+content side | UI thread (append+`Mutate`) | append-mostly, content-immutable, epoch-stamped; read by handle on render thread (threading §7) |
| `DirtyWorklist` | UI thread | none (UI consumes it in layout; render reads `Flags` from the snapshot) |
| DrawList byte+sortkey arenas | **render thread** | render-private ring ≥3 (threading §6) — Scene defines the **format**, render owns the **arenas** |
| `RealizationDirtySet` / `Mutate()` epoch bump | UI thread | epoch read render-thread-LOCAL (threading §7) |
| `FlowState` (L5) | UI thread (`WriteLayout`) | `Resolved` byte value-copied at PUBLISH; `Inherited` UI-confined |
| `SelectionState` slab + sparse index (L1) | UI thread (`Mutate(SelectionHandle,…)`) | sparse projection value-copied at PUBLISH; `ContentEpoch` read render-thread-LOCAL (§4.3/§4.4) |
| `A11yRel` cold slab (L6) | UI thread (`WriteA11yRel`) | none — UIA reads live UI-thread state (never crosses the seam) |
| `UpdateQueueSlab` (P1/P2a) | UI thread (enqueue+drain) | none — reconcile-only; never crosses the seam |

### 6.3 `WorldTransform[]` — derived, not a column

`WorldTransform[]` is **not** a SceneStore column; it is per-frame derived state composed once top-down in phase
7 (animation) from `LocalTransform`+`Bounds`, kept in a double-buffered `FrameCache` (last frame's
`WorldBounds[]`/`SubtreeWorldBounds[]` retained for damage — architecture-spec §5.4). The seam value-copies it
into `SnapshotColumns.WorldTransform`. **The copy is NOT damaged-only** (threading §3.2): an ancestor transform
change (scroll/pan/parent scale) dirties `WorldTransform` for the whole descendant set while their DrawList
spans stay clean (the `TransformDirty` fast path), so the budget is **up-to-full-tree `NodeCount × 24B` per
frame during a transform animation/scroll** (sub-ms at 5k nodes, but per-frame while live). This doc notes it
because the *column* (`LocalTransform`) is ours; the *copy budget* is the seam's accounting.

---

## 7. 13-phase placement + thread (what this subsystem does each phase)

| Phase | Thread | This subsystem's work |
|---|---|---|
| 1 pump | UI | — |
| 2 input-dispatch | UI | hit-test reads `Bounds`(LOCAL) + `Clip` + `Flags.HitTestVisible` + `InteractionInfo`; reverse-z via `LastChild→PrevSibling`; reads the **last-published-consistent** topology, never an in-flight reconcile (threading §2.2) |
| 3 hook-flush | UI | **`UpdateQueueSlab` is DRAINED here (P1/P2a):** `reconciler-hooks.md` walks each component's `NextInQueue` chain by lane, computes the coalesced next-state, and frees the drained chains (`RetireComponentQueue`); this doc only owns the slab + intrusive link the drain walks. Automatic batching = one drain per coalesced frame |
| 4 render | UI | — (Element world; Scene untouched) |
| **5 reconcile** | **UI** | `ISceneBackend` writes columns: structural sub-phase (`CreateNode`/`InsertChild`/`MoveChild`/`RemoveChild`/`DestroyNode` → quarantine ledger) then edit sub-phase (`WriteX`/`EditPaint`/`EditLayout`/`WriteFlow`/`WriteSelection`/`WriteA11yRel` → `MarkDirty` → `DirtyWorklist`). `WriteLayout` writes `FlowState.Inherited`; `WriteSelection` routes through `Mutate(SelectionHandle,…)` |
| 6 layout | UI | layout reads `LayoutInput`/`LayoutAux`, consumes `DirtyWorklist`/`LayoutDirty`, writes `Bounds` (LOCAL); **resolves `FlowState.Inherited→Resolved`** at the `WriteLayout` boundary and mirrors logical→physical (L5; layout.md owns the mirror, this doc owns the column) |
| 6.5 layout-effects | UI | — (effects read `Bounds`; Scene only serves the columns) |
| 7 animation | UI | writes `LocalTransform`/`Opacity`/`EffectAux` via `WriteAnim`/`EditPaint` → `MarkTransform`/`MarkPaint`; composes derived `WorldTransform[]` |
| **PUBLISH (7.5/13a)** | **UI** | the seam value-copies the paint-relevant column subset (§6.1); `Mutate()` epochs captured; `QuarantineLedger.Tick` → `ReclaimSlot` for cleared slots |
| **8 record** | **RENDER** | `DrawListWriter` walks the snapshot topology, emits `DrawCmd`+payload into the front arena, clean spans memcpy'd from the render-private back arena per §4.3; epoch validation render-thread-local. **Emits the folded overlay opcodes** `DrawSelectionRect` (for snapshot Text nodes with `HasSelection`, from the `SelectionSnapshot` projection) and `DrawFocusRing` (focus engine / overlay manager), each into the overlay z-layer |
| 9 batch | RENDER | reads the parallel `ulong[]` sortkeys this doc's framework produced (the two new opcodes carry an overlay-lane SortKey assigned by gpu-renderer.md) |
| 10 submit | RENDER | — |
| 11 present | RENDER | — |
| 12 passive-effects | UI | — |
| 13 arena swap | UI / RENDER | UI: `DirtyWorklist.Reset()` + per-frame `ChunkedArena.Reset()` + `UpdateQueueSlab.ResetFrame()` (free-list fully-drained chains, O(drained)); RENDER: `DrawListArenaRing.Rotate()` (the DrawList arenas are render-local — threading §14) |

---

## 8. Zero-alloc story (this subsystem's contribution)

- **Steady state, phases 5–13: 0 managed allocations.** SceneStore columns are pre-grown slab `T[]` reused via
  the free list (slot recycling satisfies steady-state mount churn with **zero array growth** — no WinUI
  `ElementPool.ForceDetach` COM-detach pain; `FreeNode` is a free-list push). The `DirtyWorklist`, reconcile
  keyed-LIS scratch, child-row snapshots, and DrawList byte/sortkey arenas are `ChunkedArena`-backed (O(1)
  reset, native-backed, GC-invisible). DrawList arenas are `GC.AllocateUninitializedArray(pinned)`.
- **Edge allocations (named, permitted):** `RenderContext`/`ComponentSlotData` edge objects at mount
  (`ObjectPool`/slab recycled), `Element[]` window chunks during virtualization realize (`ArrayPool<Element>`,
  **not** the cap-32 `ObjectPool` which overflows precisely during list realization — `painpoints` §99). The
  honest bound: **phases 6–13 paint machinery is 0-alloc; phase-4/5 realize-delta allocates bounded Gen0**
  (`Element` records + one window chunk).
- **Growth events (rare, bounded):** slab geometric ×2 (never shrinks); `ChunkedArena` add-chunk is O(1)
  native, no LOH spike, gated by the native high-water counter. Verified by the per-phase alloc-tripwire +
  process-wide BDN gen0/1/2==0 backstop (validation.md) — `GC.GetAllocatedBytesForCurrentThread` does **not**
  follow work across the seam, so the BDN process-wide gate is load-bearing.
- **No `IEnumerable`/LINQ/iterator state machines on per-frame paths** (index loops over `FirstChild`/
  `NextSibling`); no `params object[]`; `ref T Get` mutate-in-place; `[SkipLocalsInit]`; C# 14 SoA
  compound-assignment accumulators for bounds/clip unions (audited so the result is discarded into a real
  variable, else a silent re-alloc); `Unsafe.BitCast` for `Handle↔ulong`.
- **Folded columns hold the same bound.** `SelectionState`/`A11yRel`/`UpdateRecord` are `SlabAllocator<unmanaged>`
  POD — slot recycle via free list, zero per-frame growth in steady state, gen-validated; `FlowState` is a
  pre-grown spine `T[]` resized in lockstep with every other column (no separate growth event). The only
  per-frame *managed* touch on these paths is the existing `GcDepTable` side-buffer for the `UpdateRecord`
  updater delegate (a render-edge concession the DepKey rule already permits, SPEC-INDEX §2), never a slab
  field. The `SelectionState` sparse `Dictionary<NodeHandle,Handle>` index mutates only on
  selection-create/clear (a user gesture, not per-frame), so it never allocates on the paint path.

---

## 9. Failure modes

- **Stale handle (ABA):** gen-bump-on-free + `IsLive` makes any captured `NodeHandle` from a freed slot fail
  validation; in-flight snapshots see a non-validating handle (belt) and quarantine prevents byte reuse
  (suspenders) — threading §5.2.
- **Stale `ref` across a resize:** structurally impossible in the edit sub-phase (growth locked +
  capture-revalidate-on-Dispose); a `CreateNode` in the structural sub-phase never has an outstanding column
  `ref`.
- **Forgotten epoch bump (split resource mutated outside `Mutate()`):** caught by the independent content-byte
  oracle in CI fault-injection; at runtime the render-local epoch compare degrades to "redraw-not-corrupt"
  (worst case one stale-pixel frame), never a crash.
- **Bounds-move-without-PaintDirty:** caught by `CleanSpanWitness`'s baked-geometry hash (the validator
  recomputes the dest rect from current `Bounds[]`/`WorldTransform[]`).
- **Selection-changed-without-glyph-reshape (L1):** the `DrawSelectionRect` overlay span carries
  `SelectionState.ContentEpoch`; an anchor/extent edit bumps it via `Mutate()` and auto-invalidates only the
  overlay span (the underlying `DrawGlyphRun` content span stays clean) — §4.4. A selection whose rects moved
  with the node (scroll) is caught by the same baked-geometry-hash limb as any content rect.
- **Stale selection on a freed/recycled node (L1):** the sparse selection index is keyed by `NodeHandle`; a
  `DestroyNode` gen-bump makes the stale key fail `IsLive`, and `WriteSelection` on recycle clears the index
  entry + `HasSelection` flag — no orphan highlight survives a recycle (mirrors the virtualization
  derived-column-clear rule).
- **`FlowState` unresolved at record (L5):** `Resolved` is written at the phase-6 `WriteLayout` boundary before
  PUBLISH; a node that reached record with `Resolved == Inherit` is a layout-ordering bug, asserted in DEBUG
  (the record overlay-placement path requires a concrete LTR/RTL). Production degrades to LTR (no corruption).
- **`A11yRel` referenced when no AT is listening (L6):** the `A11yRel` slab is scanned only under
  `UiaClientsAreListening` (cold, §2.6); the common-case `A11yRelRef == 0` shared none-row means an
  unannotated node costs zero extra bytes and is never visited on the paint path.
- **`UpdateRecord` orphan under carry-forward (P1/P5):** `EnqueueEpoch` gates the drain so
  `reconciler-hooks.md` consumes a chain only after a *complete* reconcile; a partial-frame (sliced) reconcile
  leaves the chain in the slab (stable gen-validated slots) until completion, and `ResetFrame` frees only
  fully-drained chains — no record is reclaimed mid-flight (the carry-forward consistency contract, P5).
- **Dirty-worklist overflow / full rebuild:** falls back to the `Vector256` `Flags[]` scan over the whole
  column (first frame, resize, theme swap, device-lost re-realize).
- **`ChunkedArena` system-OOM on Commit:** clean fatal exception, not corruption (the named residual).
- **Generation wrap (2³² alloc/free of one slot):** reset slot gen, accept astronomically-low ABA risk
  (≈400 days/slot at 60 fps).
- **Device-lost:** SceneStore SoA is **untouched** — the RHI stores only realizations; every GPU object is
  reconstructible from CPU-side retained data (`BrushTable`/`ClipTable` re-upload, atlas re-rasterize from
  `GlyphRunTable`, images re-decode from `ImageRefTable`), then mark the whole tree dirty via the full-rebuild
  path (threading §9). Handle *values* stay valid; only the native behind each handle is rebuilt.

---

## 10. Cross-platform (macOS) boundary

`FluentGpu.Foundation` and `FluentGpu.Scene` are **100% portable C#**: `Handle`, the four allocators (the
`ChunkedArena` rides the `IVirtualMemory` PAL seam — the only platform touch, and that seam is owned by
`pal-rhi.md`), the SoA columns, the interning/realization tables, the dirty worklist, `ISceneBackend`, the
`Mutate()` chokepoint, and the DrawList **encoding framework** (`DrawCmd` header, opcode enum, byte+sortkey
arenas, the reuse rule) all recompile unchanged on macOS. There is **no** `HWND`/`HRESULT`/`ComPtr`/`ID3D12*`/
`DXGI`/`WM_*`/`DXGI_*` anywhere in these assemblies — they sit *above* the RHI/PAL/Text seams. A Metal/CoreText
backend changes only the leaves; the column layout, the DrawList container, and the snapshot projection are
identical (the seam doc §17 inherits the whole confinement table unchanged). The DrawList POD stream is the
designed render-thread/cross-platform boundary; this doc's job is to keep it pure POD addressed by handle/index
so neither the thread split nor the OS swap touches it.

**The folded columns are equally portable.** `SelectionState`, `FlowState`, `A11yRel`, and `UpdateQueueSlab`
are pure POD `SlabAllocator`/spine columns with **zero** platform types — they recompile unchanged on macoS.
The macOS boundary for the *features* on top of them lives in the owning leaf docs: text-selection IME/commit
binds Imm32 on Windows vs the equivalent on macOS (`text.md`); UIA collection relations bind to UIA on Windows
vs NSAccessibility on macOS (`input-a11y.md`) — but the **column storage this doc defines is the same on both**.
The two new opcodes (`DrawSelectionRect`/`DrawFocusRing`) are POD addressed by `RectF`/handle, so they cross the
render-thread/cross-platform POD boundary exactly like every existing opcode (the focus ring's Fluent two-tone
look is a raster detail owned by `gpu-renderer.md`, identical container on both OSes).

---

## Implemented from the gap analysis

The directive folds every assigned gap into **core** (no v2 deferral). This doc owns the SoA **column storage**
and the **opcode-enum registration**; the *semantics* live in the cited feature docs (this doc references them,
never redesigns them). Folded here:

| Gap | What this doc now defines (storage / registration) | Where | Semantics owner (referenced) |
|---|---|---|---|
| **L1** text selection | `SelectionState` struct (24B) + sparse `Dictionary<NodeHandle,Handle>` index → `SlabAllocator<SelectionState>`; `HasSelection` `NodeFlags` bit; `Mutate(SelectionHandle,…)` chokepoint + `ContentEpoch`; `SelectionState.BakedRectsHash` for the witness; seam projection `SelectionSnapshot[]`; `ISceneBackend.WriteSelection` | §2.2, §2.7, §4.1, §4.3, §4.4, §5, §6.1 | `text.md` (anchor/extent/affinity, `GetSelectionRects`, commit-lock) |
| **L5** RTL flow direction | `FlowDirection` enum + `FlowState` 4B **hot-spine** column `{Inherited, Resolved}`; `RtlResolved` `NodeFlags` bit; `ISceneBackend.WriteFlow`; resolution wired at the phase-6 `WriteLayout` boundary | §2.2, §2.7, §5, §6.1, §7 | `layout.md` (logical→physical mirror, Yoga golden-parity) |
| **L6** UIA collection relations | `A11yRel` cold extension slab (24B) `{SetSize, PositionInSet, Level, FullDescription, DescribedBy(+FlowsTo opt row)}`; `A11yInfo.A11yRelRef:int`; `ISceneBackend.WriteA11yRel` | §2.2, §2.7, §5 | `input-a11y.md` (provider projection, virtualized-realization-on-Navigate) |
| **P1/P2a** lanes + automatic batching | `UpdateRecord` struct (24B) + `UpdateQueueSlab` (`SlabAllocator<UpdateRecord>`, intrusive `NextInQueue`, per-component head-index, `EnqueueEpoch` carry-forward gate); phase-3 drain placement + phase-13 reset | §0, §2.2, §2.8, §7 | `reconciler-hooks.md` (lane *values*, enqueue API, drain order, `RenderPriorityPolicy`-as-executor) |
| **opcodes** | `DrawSelectionRect` + `DrawFocusRing` registered in the `DrawOp` enum; opcode-registration contract (blittable POD, baked rects in payload so the reuse rule + witness cover them); overlay z-layer placement | §4.1, §4.4, §7 | `gpu-renderer.md` (payload field shapes + raster + SortKey lane) |

**No "v2 / deferred / out-of-scope" framing remains for L1/L5/L6/P1/P2a in this doc** — each is a buildable
core column/slab/opcode with its zero-alloc, thread-confinement, failure-mode, and macOS story stated above.
The `CleanSpanWitness`/`ChunkedArena`/epoch/`Mutate()` contracts are **unchanged** — the folded columns reuse
them (selection is a *new producer of the existing four witness signals*, not a new witness limb — §4.4).

---

## 11. Changed vs the original synthesis

- **Handle is `{u32 index, u32 gen}` (kind byte dropped from the bits).** Supersedes foundations §1.1's
  earlier layout; the kind moves to the typed wrapper's DEBUG assert, buying full 32-bit generation
  and removing the park-slot-on-wrap policy (architecture-spec §4.1).
- **`ChunkedArena` SUPERSEDES the single-buffer `ArenaAllocator`** (foundations §1.2): reserve-commit /
  segmented, native-backed (GC-invisible), O(1) add-chunk with stable prior-chunk addresses, **native
  high-water counter gates growth (not the GC tripwire)**. Kills the arena cliff and relaxes "grow only between
  passes" to "add-chunk mid-pass, addresses stable" (hardened §4.4, foundations-amendment §7).
- **Intrusive free-list forbidden for pointer-bearing slabs.** Pure-POD SceneStore columns keep the trick;
  `HandleTable` resource slots use a side `int[] _next` (folds the ComPtr-stomp blocker, architecture-spec §4.2).
- **Dirty tracking is an arena WORKLIST (idle O(0)), not a per-frame `Flags[]` scan.** `Subtree*` aggregates +
  `Vector256` scan kept only for the full-rebuild/overflow path; aggregates recomputed bottom-up, never cleared
  piecemeal (folds the idle-cost + clearing-hazard amendment, architecture-spec §4.4).
- **`SubtreeDirty` is TRAVERSAL SCOPE ONLY** — never a skip-decision input (the 3-signal memo skip is the
  reconciler's; the keystone correctness fix, hardened §4.4 / reconciler-hooks §6.3).
- **Dirty taxonomy split** intrinsic (`LayoutSelfDirty`) vs participation (`LayoutParticipationDirty`); the
  measured-size-unchanged stop applies only to intrinsic dirt (architecture-spec §4.4).
- **Columns ratified as SoA on the shared spine** incl. `InteractionInfo`/`A11yInfo`/`FocusNav`; hot/cold split
  resolves the ~96B-vs-~220B contradiction (`LayoutAux`/`EffectAux`/`VirtualState` as referenced cold slabs).
- **`Bounds` is node-LOCAL; `LocalTransform` maps local→parent; `WorldTransform[]` is derived (not a column).**
  (P8, architecture-spec §5.4.)
- **DrawList SortKey is a parallel `ulong[]` arena**; the 32-bit `DrawCmd.SortKey` field is reserved (folds
  FA-2). DrawList arenas are render-thread-private and **≥3-deep** (was 2-deep, UI-swapped — threading §6).
- **Clean-span reuse rule amended** to `IsLive ∧ ContentEpoch-unchanged (glyph/image) ∧ baked-geometry-hash
  unchanged`, enforced by a single **`Mutate()` chokepoint** + DEBUG **`CleanSpanWitness`** (captures
  handle+gen+epoch+baked-geometry hash); brush/clip epochs degenerate to `IsLive` via content-hash dedup
  (architecture-spec §4.5/§5.4, hardened §4.4).
- **`ISceneBackend` made explicit**: handle-in/handle-out POD, two-phase reconcile (structural then edit) with
  the growth lock + capture-revalidate-on-Dispose, `EditPaint`/`EditLayout` diff-on-Dispose, and
  **`NodeChildCollection` with `RemoveAt`** (was omitted) over a borrowed once-walked child-row span (resolves
  the `ChildAt` O(n²) concern).
- **Slot reuse is consume-gated quarantined**: Scene `Free` marks + ledgers; the separate `ReclaimSlot` push
  happens only when `_lastConsumedSeq > freedSeq` (threading §5).
- **Threading split stated**: this doc owns the live columns + the DrawList **encoding framework**;
  `threading-render-seam.md` owns the `SceneFrame`/`SnapshotColumns`/`CopyInto` POD and the arena ring lifetime;
  `gpu-renderer.md` owns each opcode payload's fields + the SortKey layout. Feature-column semantics deferred to
  media-pipeline (`ImageRealization`), backdrop (`EffectAux`), theming (`BrushDeriver`), virtualization
  (`VirtualState`).
- **Gap-analysis columns folded into CORE (L1/L5/L6/P1/P2a).** Added `SelectionState` (sparse side-table +
  slab + epoch + `HasSelection` bit, L1), `FlowState` (4B hot-spine, `RtlResolved` bit, L5), `A11yRel` (cold
  extension slab + `A11yInfo.A11yRelRef`, L6), and `UpdateQueueSlab` (`SlabAllocator<UpdateRecord>` with
  intrusive enqueue link + `EnqueueEpoch` carry-forward gate, P1/P2a). Registered `DrawSelectionRect` +
  `DrawFocusRing` in the `DrawOp` enum. The witness/`Mutate()`/`ChunkedArena`/epoch contracts are unchanged —
  selection reuses them as a new producer of the existing signals. See **§ Implemented from the gap analysis**.

---

### Cross-references (shared contracts — not duplicated here)
- **Handles / allocators / `ChunkedArena` / `IVirtualMemory` / StringId:** [foundations.md](../foundations.md) §1, [hardened-v1-plan §4.4 + §7](../hardened-v1-plan.md), [pal-rhi.md](./pal-rhi.md) (`IVirtualMemory`)
- **SoA columns / dirty axes / DrawList physical format / clean-span:** [architecture-spec §4.1–4.5, §5.4](../architecture-spec.md)
- **DrawList opcode PAYLOAD structs + SortKey layout + reuse-rule statement:** [gpu-renderer.md](./gpu-renderer.md) §3, §11.1
- **`SceneFrame` / `SnapshotColumns` / `CopyInto` / publish / quarantine / retire-fence / arena ring:** [threading-render-seam.md](./threading-render-seam.md) §2, §3, §5, §6, §8
- **`ISceneBackend` interface decl / `ChildReconciler` / `Mutate()` caller / 3-signal memo skip / effect timing:** [reconciler-hooks.md](./reconciler-hooks.md) §2, §4, §5, §6.3
- **Lane *values* / `(fiber,updater,lane)` enqueue API / phase-3 drain order / `UseTransition` / `RenderPriorityPolicy`-as-executor (P1/P2a — this doc owns only the `UpdateQueueSlab` storage):** [reconciler-hooks.md](./reconciler-hooks.md) §7
- **`SelectionState` semantics / `GetSelectionRects` read-side / `ITextStoreACP2` commit-lock (L1 — this doc owns the column + opcode registration):** [text.md](./text.md) §8
- **`FlowDirection` logical→physical mirror at `WriteLayout` / Yoga golden-parity (L5 — this doc owns the `FlowState` column):** [layout.md](./layout.md) §4.1
- **UIA `SetSize`/`PositionInSet`/`Level`/`DescribedBy`/`FullDescription` provider projection + virtualized-realization (L6 — this doc owns the `A11yRel` slab):** [input-a11y.md](./input-a11y.md) §11
- **`DrawSelectionRectCmd` / `DrawFocusRingCmd` payload shapes + raster + SortKey lane (this doc registers the opcode *enum* entries):** [gpu-renderer.md](./gpu-renderer.md) §3, §11.1
- **`ImageRealization` semantics / residency / pinning:** [media-pipeline.md](./media-pipeline.md) §1
- **`EffectAux` semantics / `EffectChainTable` fields:** [backdrop-effects-animation.md](./backdrop-effects-animation.md) §4
- **`BrushDeriver` / `BrushRecipe` / `IBrushSink`:** [theming.md](./theming.md) §3, §6
- **`VirtualState` semantics / recycle / anchoring:** [app-requirements-waveemusic.md §3.2](../app-requirements-waveemusic.md)
- **.NET 10 / C# 14 zero-alloc + AOT + COM ruling:** [dotnet10-csharp14-zero-alloc.md](../dotnet10-csharp14-zero-alloc.md)
