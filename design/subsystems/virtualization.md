# FluentGpu — Subsystem Design: Virtualized Collections (lists, grids, grouping, incremental load)

**Author scope:** ONE subsystem — the realize-window-as-keyed-children virtualization runtime: the
`UseVirtual` / `UseInfiniteCollection` / `UseVisibleRange` hook trio, the `VirtualState` SoA column +
per-range `CancellationTokenSource`, recycling over the slab free-list (`FreeNode`/`CreateNode`, no second
recycle pool), the uniform vs variable-height policy, grouping projection + sticky headers, the
decode→measure feedback-loop break, incremental data load, and the recycle-correctness invariants. It is the
mechanism that makes WaveeMusic's 10k–100k-row lists, the image-residency budget, and the zero-per-frame
paint contract all hold at once.

This doc is the **authority** for: the `UseVirtual`/`UseInfiniteCollection`/`UseVisibleRange` hook semantics,
the `VirtualState` column SEMANTICS + per-range CTS lifecycle, the recycle correctness invariants
(request-epoch survives recycle; clear stale derived-brush/image columns; rows bind NO derived brushes), the
grouping flat-index→(kind,payload) projection, sticky-header pinning policy, the uniform-fast-path vs
variable-height anchoring **policy** (the extent-table *mechanics* are deferred to `layout.md`), the
incremental-load +1-frame-latency contract, the `NodeFlags.VirtualRangeDirty`/`StickyPinned` bits, and the
honest alloc claim. It **references — does not redesign** the shared contracts owned elsewhere:

- **Threading / publish / quarantine / 13-phase→thread map / build order** → `hardened-v1-plan.md` §2/§4.1/§6
  (canonical), `threading-render-seam.md` (concrete topology, `SnapshotColumns`, consume-gated quarantine).
- **Reconciler / hooks / keyed-LIS diff / `ISceneBackend` op set / effect timing / 3-signal memo skip /
  `DepKey`+`GcDepTable`** → `reconciler-hooks.md` (this doc owns only the hook *interaction*; §11 there
  cross-links here).
- **Layout: `LayoutKind`, extent-table mechanics, scroll model (content-box origin + `-ScrollOffset` viewport
  transform), pixel-snap, Grid tracks** → `layout.md` (placeholder; contracts in `architecture-spec.md`
  §5.5). This doc OWNS the *policy* (when uniform vs variable, what an anchor is, what a regroup invalidates);
  `layout.md` OWNS the *math* (`VirtualLayout` two-pass, Fenwick/BIT offset↔index, snap).
- **Media: `UseImage`/`UseMosaic`, `ImageRefTable`/`ImageRealization`, `DecodeScheduler` cancel ticket,
  `ResidencyManager`, the decode-bucket sizes, the small-image atlas** → `media-pipeline.md`
  (`UseVisibleRange` → image warm/cancel consumes these; this doc never touches image pixels).
- **Theming: derived-brush back-ref eviction, the page-level-only `UseDerivedBrush` invariant** → `theming.md`
  §9 (the binding cross-subsystem rule that rows bind no derived brushes).
- **Scene SoA columns, `NodeFlags`, the dirty worklist, clean-span rule, `Mutate()` epoch chokepoint** →
  `architecture-spec.md` §4.4/§4.5/§5.4 (scene-memory doc not yet written), `gpu-renderer.md` (clean-span
  rule + `SortKey`), `foundations.md` (Handle/SlabAllocator/ChunkedArena/ArrayPool/StringId).

---

## 0. The one-sentence thesis

> A virtualized collection is **not a control and not a new frame phase** — it is a single retained node with
> `LayoutKind.VirtualList/VirtualGrid` plus a hook trio that, each frame the visible range is dirty, calls the
> dev's `RenderItem(i)` **only for `[first,last)+overscan`**, mints stable-`ItemKey`'d `Element`s into a
> pooled `Element[]` window, and feeds them straight to the **existing keyed-LIS reconciler** — so recycling
> *is* `FreeNode`/`CreateNode` over the slab free-list, in-window scroll is a phase-7 transform-only frame,
> and the only managed allocation is the bounded-Gen0 phase-4 realize-delta of the rows that just **entered**.

Nothing about the reconciler, the DrawList opcode set, the RHI, or the PAL changes. The entire feature lands
in **Layout** (the `VirtualLayout` participant + extent table), **Scene** (two `NodeFlags` bits + one
`VirtualState` column), **Hooks** (the trio, in `FluentGpu.Reconciler` next to the other hooks), and
**Reconciler** (the keyed-LIS diff it already has). **No new assembly.**

---

## 1. Module placement & the dependency seam (no new assembly)

```
Foundation   ── Handle{u32 index,u32 gen}, NodeHandle, SlabAllocator<T:unmanaged>, ArrayPool<Element>,
             │   ChunkedArena, StringId, NodeFlags, DepKey/DepDeps
Scene        ── SceneStore SoA (+ VirtualState column, + NodeFlags.VirtualRangeDirty/StickyPinned),
             │   ISceneBackend; the VirtualState column SEMANTICS are owned HERE (this doc), the column
             │   STORAGE shape obeys scene-memory's SoA framework
Layout       ── LayoutKind.VirtualList/VirtualGrid + VirtualLayout two-pass + ExtentTable (Fenwick)
             │   [layout.md owns the math; this doc owns the policy that selects the path]
Reconciler   ── Reconciler + ChildReconciler (keyed-LIS, UNCHANGED) + the hook trio
             │   UseVirtual/UseInfiniteCollection/UseVisibleRange (built atop UseRef/UseEffect/UseReducer +
             │   the ported KeyedListDiff/ReactorListState)
Media        ── UseImage/UseMosaic + DecodeScheduler.Cancel + ResidencyManager (UseVisibleRange warms/cancels)
Theme        ── UseDerivedBrush (page-level ONLY; rows never call it — §6 invariant)
Hosting      ── 13-phase FrameLoop driver; the worker-pool + IPlatformAppLoop.Post edge for page fetch
```

**Critical invariant (foundations DAG, reconciler-hooks §1):** the virtualizer is **pure C# over
`ISceneBackend` + `Element` records + hook primitives**. It references no COM, no `ComPtr`, no command list,
no GPU fence; it is render-thread-ready by construction and identical between build-order step 1 (single
thread, quarantine=0) and step 4+ (render thread spawned, quarantine flipped behind the green race gate). The
only seam contract it must honor across the flip is **consume-gated slot reuse** (§5.4). The hooks live in
`FluentGpu.Reconciler` alongside the other hooks (per reconciler-hooks §1: `Media`/`Theme` provide hook
*compositions* on `Foundation` primitives; the virtualization trio is a *core* hook composition, so it is
homed in `Reconciler`, not a feature assembly).

---

## 2. The node model — one retained node, a window of keyed children

A virtualized collection is exactly **one** retained `SceneStore` node:

- `VisualKind = Container` (it draws nothing itself; it is a clip + a viewport).
- `LayoutKind = VirtualList` (1-D, the common case) or `VirtualGrid` (2-D wrap, e.g. an album shelf or the
  artist-discography grid). [`layout.md` owns `LayoutKind`; this doc names the two it adds.]
- `Flags |= ClipsToBounds` (the viewport clips the realized window; offscreen rows are simply not realized).
- A `VirtualState` slab row (§4) hangs off `PayloadRef`/a side slab keyed by node index.

Its **children** are the realized window: one child node per visible item index in `[first, last)` expanded by
overscan, **plus** zero or more group-header nodes (grouping, §7). Each child carries:

- `Key = ItemKey` — a 16-byte POD interning into the existing `StringId` key space (so the keyed-LIS diff is
  byte-for-byte unchanged). The dev supplies `KeyOf<T>` returning a stable per-item identity (a track id, a
  playlist guid); we intern it once per realize.
- The whole subtree the dev's `RenderItem(i)` returns (a row template — text + art `DrawImageCmd` + hit
  target). Survivors across a scroll boundary keep their `Element` via component memoization (the 3-signal
  skip, reconciler-hooks §6.3); only **ENTER** rows are freshly rendered.

The window node's parent is a normal scroller. **Scroll is layout-free** (architecture-spec §5.5,
`layout.md`): layout arranges realized content at the content-box origin and publishes `ContentSize`; the
`-ScrollOffset` translation is the **viewport-child `LocalTransform`** written by Input (phase 2) /
Animation (phase 7), marking `TransformDirty`/`PaintDirty` only — never `LayoutDirty`. The virtualizer reads
`ScrollOffset`/viewport to compute the window; it never writes the offset (Input owns it; Layout writes
`ContentSize`; Render reads the clip — ownership pinned in `layout.md`/architecture-spec §5.5).

**Recycling is `FreeNode`/`CreateNode` over the slab free-list — there is NO second recycle pool.** When an
item scrolls out of overscan, the keyed-LIS diff emits `RemoveChild` + the reconciler runs cleanups
synchronously (reconciler-hooks §4.4) + `DestroyNode` (gen-bump, columns to free list). When an item scrolls
in, the diff emits `CreateNode` + `InsertChild`. The slab free-list satisfies steady-state scroll churn with
**zero array growth** (foundations §1.2 geometric grow, never shrink) once the high-water window size is
reached. This is the explicit avoidance of WinUI's `ElementPool.ForceDetach` COM-detach pain
(architecture-spec §5.4, app-requirements §3.2): a free is a free-list push, not a COM teardown.

---

## 3. The hook trio — `UseVirtual` / `UseInfiniteCollection` / `UseVisibleRange`

All three are fn-pointer-shaped: the user-supplied callbacks are **static-friendly delegates** (no per-row
closure capture), so realizing a window of 40 rows does not mint 40 closures. Deps are `ReadOnlySpan<DepKey>`
(reconciler-hooks §3.2/§3.4) lowered by the source generator to a `stackalloc DepKey[N]` (zero heap ≤4 deps).

```csharp
namespace FluentGpu.Reconciler;   // homed with the other hooks (reconciler-hooks §1)

// fn-pointer-shaped delegates — static-friendly, no per-row closure
public delegate Element RenderItem<T>(int index, in T item);   // dev's row template
public delegate T       GetItem<T>(int index);                 // pulls item i from the dev's backing store
public delegate ItemKey KeyOf<T>(int index, in T item);        // stable identity (16B → StringId space)

[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct ItemKey : IEquatable<ItemKey>           // POD; interns into StringId key space
{
    public readonly long Lo;      // e.g. low 64 bits of a track id hash, or a packed (groupId,localIndex)
    public readonly int  Hi;      // discriminant + high bits; high bit reserved for synthesized positional keys
    public readonly DepTag Kind;  // Track | Header | Skeleton | Positional (so the keyed map can't collide spaces)
    public bool Equals(ItemKey o) => Lo == o.Lo && Hi == o.Hi && Kind == o.Kind;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct VirtualSpec
{
    public LayoutKind Layout;          // VirtualList | VirtualGrid
    public float      ItemExtent;      // uniform fast-path row height/col width; 0 ⇒ MeasureItems=true
    public int        CrossAxisCount;  // VirtualGrid: columns (0 = derive from viewport / item cross size)
    public int        Overscan;        // rows realized beyond the viewport each side (default ~4)
    public ushort     GroupHeaderTypeId;// 0 = no grouping; nonzero forces MeasureItems=true (variable headers)
    public bool       MeasureItems;    // explicit variable-height; auto-forced true when ItemExtent==0 || grouped
}

// ── the trio ─────────────────────────────────────────────────────────────────────
public VirtualHandle UseVirtual<T>(int itemCount, RenderItem<T> renderItem, GetItem<T> getItem,
                                   KeyOf<T> keyOf, in VirtualSpec spec, ReadOnlySpan<DepKey> deps);

public InfiniteCollection<T> UseInfiniteCollection<T>(int totalCount, FetchPage<T> fetchPage,
                                   RenderItem<T> renderItem, KeyOf<T> keyOf, in VirtualSpec spec,
                                   ReadOnlySpan<DepKey> deps);   // composes UseInfiniteResource (§8)

public void UseVisibleRange(VirtualHandle v, RangeChanged onChange, ReadOnlySpan<DepKey> deps); // phase 6.5
```

`VirtualHandle` is a thin `readonly struct` wrapping the `VirtualState` slab handle + the host
`NodeHandle`; it is the token `UseVisibleRange`/`ScrollToIndex` reference.

### 3.1 `UseVirtual<T>` — the realize-window core (UI thread, phases 2/4/5)

```csharp
UseVirtual(itemCount, renderItem, getItem, keyOf, spec, deps):
    cell = ctx.UseRef<VirtualHandle>();              // persists across renders (mount-only edge alloc)
    if (first render) cell.Value = AllocVirtualState(host, spec);   // slab row
    st = ref backend.GetVirtualState(cell.Value);    // ref into the VirtualState slab column

    // datasource version: if deps changed (itemCount, source identity, spec), bump st.DataVersion,
    // mark VirtualRangeDirty (forces a re-realize even without a scroll boundary cross).
    if (!DepsEqual(st.Deps, deps)) { st.DataVersion++; backend.MarkDirty(host, VirtualRangeDirty); st.Deps.CopyFrom(deps); }

    // Compute the target window from the committed previous-frame ScrollOffset + viewport + overscan.
    // Uniform: O(1) arithmetic (first = floor(off/extent)-overscan ...). Variable: ExtentTable.RangeForOffset
    // (Fenwick O(log n)) — math owned by layout.md; this doc just consumes [first,last).
    (first, last) = ComputeWindow(st, spec, scrollOffset, viewportExtent);

    // Only re-render the window if the range is dirty (boundary crossed) OR the datasource changed.
    if ((backend.Flags(host) & VirtualRangeDirty) != 0 || st.DataVersion != st.RealizedDataVersion) {
        // Window buffer = ArrayPool<Element>, NOT the cap-32 ObjectPool (§9 / foundations).
        var window = ArrayPool<Element>.Shared.Rent(last - first + groupHeaderCount(first,last));
        int w = 0;
        for (int i = first; i < last; i++) {
            // optionally interleave a group header before i (grouping §7) — flat-index projection
            if (IsGroupBoundary(i)) window[w++] = RenderGroupHeader(i);
            var item = getItem(i);                   // dev pulls item (data slab; may be a skeleton — §8)
            var el   = renderItem(i, in item)        // dev's row template (static delegate — no closure)
                         with { Key = intern(keyOf(i, in item)) };   // ItemKey → StringId
            window[w++] = el;
        }
        st.RealizedFirst = first; st.RealizedLast = last; st.RealizedDataVersion = st.DataVersion;
        // Hand the window straight to the EXISTING keyed-LIS reconciler as the host's children (§5).
        ReconcileChildren(host, window.AsSpan(0, w));
        ArrayPool<Element>.Shared.Return(window, clearArray: true);  // refs cleared so GC can collect rows
        backend.ClearFlag(host, VirtualRangeDirty);
    }
    return cell.Value;
```

`ReconcileChildren` is **not new code** — it is the ported `ChildReconciler.ReconcileKeyedMiddle`
(reconciler-hooks §5.4) over a `NodeChildCollection` on the host node, with all scratch on the per-frame
`ChunkedArena`. The window `Element[]` is the `oldChildren`/`newChildren` source the diff already expects
(reconciler-hooks holds prev-frame `Element`s per host node). Because `ItemKey` interns into `StringId`,
`KeyMatch`/`GetKey`/`ComputeLIS` are byte-for-byte unchanged: a scroll that keeps 36 of 40 rows produces a
4-enter/4-exit diff with the surviving 36 as a contiguous LIS run (zero moves).

### 3.2 `UseInfiniteCollection<T>` — incremental load (composes `UseInfiniteResource`)

`UseInfiniteCollection` = `UseVirtual` + a paged data layer (`UseInfiniteResource` from Reactor's
`Hooks/UseInfiniteResource.cs`, reused — reconciler-hooks §16). `totalCount` is known up front (the server
returns a total); `getItem(i)` returns either the loaded item or a **`LazyTrackItem` skeleton sentinel** if
page `floor(i/pageSize)` is not yet resident. The skeleton renders a shimmer row (a per-row animated gradient
**fill**, gradient-atlas row + animated UV in phase 7 — explicitly **not** a `PushLayer` offscreen RT;
app-requirements §3.2 worst-case). When the visible window touches an unloaded page, the layout/visible-range
machinery (§8) dispatches `fetchPage(p)` on the worker edge. See §8 for the +1-frame latency contract.

### 3.3 `UseVisibleRange` — the prefetch / image-warm bridge (phase 6.5, layout effect)

`UseVisibleRange(v, onChange, deps)` is a **`UseLayoutEffect`** (phase 6.5 — `Bounds[]` are valid,
reconciler-hooks §4.1) that fires `onChange(new Range(first, last))` whenever the realized range changes. Its
two consumers:

1. **Media warm/cancel** (media-pipeline §3.1/§5): on range change, the dev (or a built-in adapter) calls
   `UseImage` for the now-visible rows (already happens inside `RenderItem`, but `UseVisibleRange` lets a
   *prefetch* margin beyond overscan warm decode for rows about to enter), and `DecodeScheduler.Cancel(ticket)`
   for rows that left overscan+prefetch margin. The cancel is driven by the per-range CTS (§4.2).
2. **Page prefetch** (§8): `UseInfiniteCollection` wires `onChange` to enqueue `fetchPage` for the page(s) the
   new range touches plus one-ahead.

`UseVisibleRange` runs at **6.5, not 12**, deliberately: scroll-into-view / image-warm must observe the
**arranged** window of *this* frame so warming starts a frame earlier than a passive effect would
(media-pipeline §6.5 row). It is the same phase `ScrollToIndex` uses.

---

## 4. The `VirtualState` column — semantics + per-range cancellation

`VirtualState` is a **slab-backed** (`SlabAllocator<VirtualState>`, unmanaged) SoA side-column keyed by the
host node (one row per virtualized collection — there are O(screens) of these, not O(items), so the slab is
tiny). It is **not** arena-backed: it must survive across frames (it carries the realized range, the data
version, and the live cancel ticket). The **storage shape** obeys scene-memory's SoA framework; the
**semantics below are owned here**.

```csharp
[StructLayout(LayoutKind.Sequential)]                 // unmanaged; SlabAllocator<VirtualState>
public struct VirtualState
{
    public NodeHandle Host;                // the virtual-list container node
    public LayoutKind Layout;              // VirtualList | VirtualGrid (mirrors VirtualSpec.Layout)
    public int        ItemCount;           // total logical items (incl. headers in the flat index space)
    public int        RealizedFirst;       // [first,last) currently realized as children
    public int        RealizedLast;
    public uint       DataVersion;         // bumped on deps change / regroup / page apply
    public uint       RealizedDataVersion; // the DataVersion the realized window reflects
    public float      ItemExtent;          // uniform path; 0 ⇒ variable (extent table, layout.md)
    public int        ExtentTableRef;      // -1 uniform; else index into layout.md's slab-backed ExtentTable
    public int        AnchorIndex;         // topmost visible item index at last commit (scroll anchoring §5)
    public StringId   AnchorKey;           // its ItemKey — survives extent correction / regroup
    public float      AnchorOffsetWithin;  // sub-item offset of the anchor at last commit (px)
    public ushort     RangeCts;            // index into the per-range CTS table (§4.2); 0 = none
    public ushort     PrefetchMargin;      // rows beyond overscan to warm (media) / pages to prefetch (data)
    public uint       GroupVersion;        // bumped on regroup; invalidates the extent table + resets anchor
    public DepDeps    Deps;                // last UseVirtual deps (inline; reconciler-hooks §3.4)
}
```

### 4.1 What each field is FOR (the semantics this doc owns)

- **`DataVersion` vs `RealizedDataVersion`** is the *re-realize gate*. A change to `itemCount`, the datasource
  identity, the spec, or a page apply bumps `DataVersion`; phase 4 re-realizes only when
  `DataVersion != RealizedDataVersion` **or** `VirtualRangeDirty` is set. Steady in-window scroll touches
  neither (it is a transform-only frame), so phase 4 does nothing.
- **`RealizedFirst/Last`** is the source of truth for "which item index maps to which child slot," used by the
  keyed diff and by page-apply re-derivation (§8).
- **`Anchor{Index,Key,OffsetWithin}`** is the scroll-anchoring contract (variable path, §5): on an extent
  correction or regroup, the viewport is re-pinned to `AnchorKey`'s new offset so an above-viewport size
  change does not jump the viewport. The anchor is keyed on the **`ItemKey`** (not the index) so it survives
  a regroup that renumbers indices.
- **`ExtentTableRef`** points at layout.md's per-item extent table (Fenwick/BIT, estimated-then-corrected).
  This doc *uses* it (offset↔index, `ContentSize`) but does not implement it.

### 4.2 Per-range `CancellationTokenSource` lifecycle (owned here)

Each `VirtualState` carries **one `RangeCts`** — a token into a pooled CTS table (mirroring
`DecodeScheduler`'s pooled `CancellationTokenSource[]`, media-pipeline §4):

```csharp
internal sealed class RangeCancellation        // one per VirtualState; pooled, reset on host destroy
{
    readonly CancellationTokenSource[] _cts;    // index = RangeCts; reset (not realloc) on recycle
    public ushort Begin();                       // allocate a CTS for the current live range
    public void   Cancel(ushort ticket);         // a range exited overscan+prefetch → cancel its work
    public CancellationToken Token(ushort ticket);
}
```

**Lifecycle:** when a range becomes live (`UseVisibleRange` fires), the page fetches and image decodes it
triggers are tagged with `Token(RangeCts)`. When the range **exits** the overscan+prefetch margin — i.e. a
fling carries the viewport far past it — `Cancel(RangeCts)` cancels the page fetch (the worker dequeues, sees
`IsCancellationRequested`, drops without work — §8) and the image decode (`DecodeScheduler.Cancel(ticket)`,
media-pipeline §4). On host `DestroyNode`, the CTS is reset and returned to the pool (synchronous, in the
reconcile-remove cleanup — reconciler-hooks §4.4). This is the structural answer to "fling past 500 rows":
the blown-past work is **cancelled, not completed** (app-requirements §3.2 worst-case).

> **Note on layering vs Media's cancel ticket.** Media owns a *per-image* `CancelTicket`
> (`DecodeScheduler`); this doc owns a *per-range* `RangeCts` that is the *parent* of the per-image cancels
> for that range. A range cancel cascades to its rows' image cancels — but the deeper backstop is the
> **request-epoch** (§5.1), which drops a late callback even if a cancel races.

---

## 5. Recycle correctness invariants (the part the critiques hunt)

These four invariants are what make `FreeNode`/`CreateNode` recycling safe under fling velocity. Each is
**pinned regardless of remaining virtualization detail** (app-requirements §3.2).

### 5.1 Request-epoch survives recycle (the stale-callback backstop)

A recycled node gets the **same slab slot** as a freed one only after the consume-gated quarantine clears it
(§5.4) — but even within the legal window, a *late async callback* (an image decode or page fetch that
completes after its row recycled to a new track) must not write into the new occupant. The defense is the
**request-epoch carried in a `UseRef` hook cell**, not the `NodeHandle` generation:

- `NodeHandle.gen` bumps on **free**, not on **recycle-to-a-new-key-in-the-same-component-slot**. A row that
  scrolls out and a *different* row that scrolls in get different handles (free + create), so the gen check
  catches a closure that outlived the destroy. But a row component that is *memoized and recycled to a new
  key* (the keyed diff updates it in place) keeps its handle — so gen is the wrong epoch for that case.
- The **request-epoch** (a `uint` in the `UseImage`/page-fetch hook cell, media-pipeline §5/§3.1) is bumped
  every time the row binds a *new* request. A completing decode/fetch carries the epoch it was issued under;
  on `Post`-back to the UI thread, it is dropped if `cell.Epoch != completion.Epoch`. This survives recycle
  because the hook cell persists across renders (it is a `UseRef`), and it is independent of `NodeHandle`
  generation. **This is the load-bearing recycle backstop** (media-pipeline §7.4, app-requirements §3.2.2).

### 5.2 Clear stale derived-brush / image columns on recycle

On recycle to a new key, the reconciler either (a) **re-runs the row component** — deps changed (new track
id / image src / palette), so `UseImage` re-requests and any derived column is rewritten — OR (b) **explicitly
clears** any derived-brush/`ImageHandle` column on the recycled slot so no stale handle survives into the new
occupant's first paint. Because of §6 (rows bind no derived brushes), there is *nothing for Theme to clear*;
the only per-row derived column is the `ImageHandle`, owned by Media, which the request-epoch + re-run already
rebinds. The `WriteVisual`/`WritePayload` mask diff (reconciler-hooks §5.3) naturally overwrites the changed
columns; the explicit-clear path is the belt-and-suspenders for a component that *conditionally* binds an
image (so a row that had art and recycles to a row without art clears the column rather than showing the prior
art).

### 5.3 Rows bind NO derived brushes (the cross-subsystem invariant)

This is **theming.md §9 verbatim**, restated here because it is what keeps recycle O(1) for Theme:

> **List rows MUST NOT bind T2 (album-art) derived brushes.** Row `Fill`/`Foreground` are **T0 static token
> handles only** (`Tok.TextPrimary`, `Tok.SurfaceCard`, …). Only **page-level** surfaces — hero card,
> now-playing backdrop, right panel, the palette-tinted page header — bind `UseDerivedBrush`/`UseDynamicColor`,
> and there are O(1) of those per screen.

If a row bound a per-track derived brush, every recycle would mint/evict derived brushes at scroll velocity —
defeating `BrushCache`, thrashing the derived-brush→node back-ref multimap (theming §8), and risking
stale-brush spans on recycled slots. Keeping rows on theme-stable static tokens means a recycle re-binds only
text content + an `ImageHandle` (Media's residency story), never a derived brush. A row art thumbnail is a
`DrawImageCmd`/`ImageHandle` — **not** a Theme brush; Theme never touches row art. The `WgpuAnalyzer` should
flag `UseDerivedBrush` inside a `RenderItem` body as a warning (build-time enforcement of the invariant).

### 5.4 Consume-gated slot reuse across the render-thread seam

A node slot freed during production of frame `p` is reusable only when `_lastConsumedSeq > p`
(`QUARANTINE = RenderInFlightDepth + 1` (belt-and-suspenders, compile-asserted); hardened §2.3,
threading-render-seam §5.5/reconciler-hooks §5.5). The virtualizer does **not** special-case this — it just
`DestroyNode`s exited rows; the `QuarantineLedger` (Scene) parks the slot until the render thread has consumed
past `p`, so a recycled slot can never be observed by the render thread reading an in-flight snapshot. In
single-thread build-order step 1, quarantine=0 and the case degenerates (UI produces+consumes). The keyed-LIS
diff is correct either way: it never reuses a slot within the same frame it freed it.

---

## 6. Uniform fast path vs variable-height + anchoring (POLICY owned here; mechanics in layout.md)

The single most important *policy* decision (the mechanics — `VirtualLayout` two-pass, the Fenwick offset↔index
table, pixel-snap — are deferred to `layout.md`):

### 6.1 Uniform fast path — `MeasureItems=false`, `ItemExtent>0`

`ContentSize = ItemCount × ItemExtent`; `[first,last)` is **O(1) arithmetic**
(`first = floor(scrollOffset/extent) − overscan`, `last = ceil((scrollOffset+viewport)/extent) + overscan`);
no per-item measurement, no extent table (`ExtentTableRef = -1`). This is the path every WaveeMusic track list
takes.

> **The track-row height decision (folded, app-requirements §3.2):** track rows are authored `MinHeight=56`
> (to allow wrap/equalizer/density), which is *technically variable* — but Wavee **pins a fixed row height per
> density at the app layer** so the uniform path stays valid. The virtualizer's policy: **`ItemExtent` must be
> a fixed, density-derived constant the app supplies; it must never be the late-arriving image `NaturalSize`**
> (see §6.3). A list that genuinely needs intrinsic per-row height opts into the variable path explicitly.

### 6.2 Variable-height path — `MeasureItems=true`, anchoring

Forced whenever `ItemExtent==0` **or** `GroupHeaderTypeId != 0` (headers are variable — image-bearing Liked
Songs group headers, a hero-as-first-row). Mechanics (layout.md): a **slab-backed** per-item extent table
(NOT arena — this is the explicit fix of the source designs' arena/slab contradiction; the table persists
across frames) with a **Fenwick/BIT** for O(log n) offset↔index, **estimated-then-corrected** extents (an
unmeasured item uses an estimate; on realize+measure the table corrects), and **scroll-anchoring on the
topmost visible item's `ItemKey`**:

> **Anchoring policy (owned here):** before applying an extent correction or a regroup, capture
> `Anchor{Index,Key,OffsetWithin}` = the topmost visible item + its sub-item scroll offset. After the
> correction recomputes offsets, re-derive `ScrollOffset` so `AnchorKey` lands at `AnchorOffsetWithin` again.
> This makes an above-viewport extent correction (a header that measured taller than estimated) **not jump the
> viewport** — the user's place in the list is stable even as off-screen estimates settle.

### 6.3 The decode→measure feedback-loop break (owned here, the critical anti-loop)

`UseImage` returns a `NaturalSize` (media-pipeline §6) **only for app-level aspect math** — the dev MUST bind
row height to the **fixed decode bucket**, never the late-arriving natural size. If row height were a function
of the decoded image's natural size:

1. The row realizes at an estimated height → layout → paint.
2. The image decodes (off-thread) → `NaturalSize` arrives a frame later → row re-measures → height changes.
3. The height change shifts every later row → extent-table correction → re-realize the window → new images
   warm at new buckets → decode → **goto 2**.

This is an unbounded decode→measure→relayout loop. The break is structural: **row height is bound to the fixed
decode bucket** (a quantized size class, media-pipeline), so a decode completing **never** changes layout —
the image fills its pre-sized slot (object-fit cover/contain inside a fixed rect). `NaturalSize` is reported
for the dev's aspect math (e.g. a hero image that genuinely wants intrinsic aspect on a *non-virtualized*
surface) and is explicitly forbidden from feeding the layout of a virtualized row (media-pipeline §6 guard,
restated here as a virtualization invariant).

---

## 7. Grouping — flat index → (kind, payload) projection; sticky headers as phase-7 transform

**The virtualizer does not group.** The app supplies a **flat `index → (kind, payload)` projection** over a
single flat index space that interleaves headers and items:

```csharp
public enum ItemKind : byte { Item, GroupHeader }
public delegate (ItemKind kind, int payload) FlatProjection(int flatIndex);
// e.g. Liked Songs grouped by first-letter: flatIndex 0 → (GroupHeader, 'A'),
//      1..k → (Item, trackIndex), k+1 → (GroupHeader, 'B'), ...
```

The dev's `RenderItem`/`RenderGroupHeader`/`KeyOf`/`GetItem` all operate on this flat index. A header is just
another item in the flat space (with `ItemKind.GroupHeader` and `ItemKey.Kind=Header`); the keyed-LIS diff
treats it identically. `GroupHeaderTypeId != 0` forces `MeasureItems=true` (headers are variable).

**A regroup** (the dev changes the grouping key — sort by date vs artist) is a **`deps` change** on
`UseVirtual`:
- It bumps `GroupVersion` + `DataVersion` → `VirtualRangeDirty`.
- It **invalidates the extent table** (layout.md) — the flat index space changed, so cached extents are stale.
- It **resets the anchor to a stable post-regroup key** — pick an `ItemKey` that exists in both groupings (a
  track id is stable across a sort change) so the viewport stays near the same content.
- It **re-seeds `ContentSize`** from the new flat count.

**Sticky group headers** are a **phase-7 transform write**, not a layout change and not a special clip:

```
stickyHeaderLocalTransform.dy = min(scrollOffset, nextHeaderOrigin − headerExtent)
```

The active group's header is pinned to the viewport top (translate-Y) until the next header pushes it up. This
is written in phase 7 (animation/transform), marks `TransformDirty`/`PaintDirty` only (never `LayoutDirty`),
and the pinned header node is flagged `NodeFlags.StickyPinned` so it is **excluded from clean-span memcpy**
(its WorldTransform changes every scroll frame while its DrawList span stays clean — the batcher re-applies
the new transform to the cached quads, gpu-renderer §13 / threading-render-seam §3.2). Without `StickyPinned`
the clean-span reuse would memcpy a stale baked position; with it, the recorder re-records the pinned header's
span each frame the sticky offset moves (trivial — one header).

---

## 8. Incremental data load — the +1-frame latency contract

`UseInfiniteCollection` rides the existing worker + `IPlatformAppLoop.Post<TState>` edge (no new threading):

```
Cold open of a 10k list (app-requirements §3.2 worst-case):
  P4  ContentSize = 10k × 56 computed instantly (uniform path); getItem(i) returns LazyTrackItem skeletons
      for the ~40 window rows (page 0 not yet resident) → ~40 skeleton (shimmer-fill) rows realized.
  P6.5 UseVisibleRange fires → enqueue fetchPage(0) on the worker (tagged Token(RangeCts)).
  ───── worker decodes page 0 (off the frame loop) ─────
  later: worker Post<TState>(pageResult) onto the UI thread → applies into the data slab,
         bumps DataVersion, marks VirtualRangeDirty, RequestFrame(N+1).   ← NO synchronous re-loop
  P4(N+1) DataVersion != RealizedDataVersion → re-realize the CURRENT window (re-derive [first,last) from the
          NOW scroll offset — the user may have scrolled while the page loaded). getItem now returns real items.
  P5(N+1) keyed-LIS diff: the skeleton rows and the real rows share ItemKey (the key is the track id, stable
          across skeleton→real) → the diff UPDATES in place (not enter/exit); only IsLoaded-flipped rows
          PaintDirty; rows that scrolled off are exit-diffed; off-window page rows land in the data slab only.
  P8(N+1) re-record ONLY the ~40 PaintDirty rows; memcpy the 9,960 clean spans (no reset of the whole list).
```

**The contract: minimum +1 frame from page-arrival to pixels.** A worker page-result `Post` marks dirty +
schedules **frame N+1** — there is **no same-frame phase-3 apply** (architecture-spec §5.4 / reconciler-hooks
§4.2: v1 has no synchronous re-loop). On apply, the virtualizer re-derives the *current* realized window
(because the viewport may have moved during the fetch) and `PaintDirty`s only rows still realized; off-screen
fetched items populate the data slab without realizing nodes.

**Backpressure:** `fetchPage` requests ride a bounded `Channel<PageFetch>` with priority-drop of off-screen
requests under a fling (media-pipeline §4 `DecodeScheduler` discipline); `Cancel(RangeCts)` on range-exit
drops blown-past fetches. The skeleton→real transition is a `Post`+N+1 dirty, never a layout change (row height
is the fixed bucket, §6.3), so a page arriving never re-flows the list.

---

## 9. Zero-alloc story (the honest claim)

| Frame kind | Allocation |
|---|---|
| **In-window scroll** (no item boundary crossed) | **Transform-only frame** (phase 7 `-ScrollOffset` write). ~0 alloc. No phase-4 render, no reconcile, no realize. |
| **Boundary cross** (scroll crosses an item edge) | Re-render the **delta** rows only (ENTER rows). Phase-4 realize-delta allocates **bounded Gen0**: `Element` records for entered rows + one pooled `Element[]` window chunk (from `ArrayPool<Element>.Shared`). Survivors keep their `Element` via the 3-signal memo skip. |
| **Paint machinery (phases 6–13)** | **0 managed alloc.** Keyed-LIS scratch is `ChunkedArena` spans (reconciler-hooks §5.4); recycle is slab free-list push/pop; clean rows memcpy their DrawList spans; the window subtree re-records only ENTER/PaintDirty rows. |
| **Page apply (N+1)** | Bounded Gen0 (re-realize the window once). |

**The honest claim (folded — app-requirements §3.2, the central correction):** *phases 6–13 paint machinery
is 0-alloc; phase-4 realize-delta is bounded Gen0 (`Element` records + the pooled `Element[]` window chunk).*
It is **not** zero — it is **O(Δ-entered-rows) Gen0**, CI-gated by the per-phase alloc-tripwire (which asserts
==0 for phases 6–13 and a bounded budget for phase 4 during realize) + the process-wide BDN backstop
(hardened §4.5, validation.md).

**The `ArrayPool<Element>` ruling (CRITICAL — foundations / app-requirements §5 / reconciler-hooks §11).** The
window buffer is rented from `ArrayPool<Element>.Shared`, sized to `window + overscan + headers`, and returned
(cleared) each realize. It is **NOT** the cap-32 `ObjectPool<class>`: the cap-32 pool is an *edge* pool that
would **overflow precisely during list realization** (a 40+-row window exceeds cap 32). Renting from
`ArrayPool` reuses backing arrays across realizes (no churn) and clears refs on return so realized `Element`s
are collectable. This is the single most-cited critique catch (painpoints §99); getting it wrong reintroduces
GC pressure exactly where virtualization was supposed to remove it.

---

## 10. 13-phase placement + thread (canonical map)

All virtualizer work is on the **UI thread** (it mutates `SceneStore`/`VirtualState`/hook cells; it touches no
`ComPtr`/command list/fence — render-thread-ready by confinement, reconciler-hooks §12). Phase numbering is the
canonical 13-phase loop (architecture-spec §4.8; PUBLISH at 13a per hardened §2.2).

| Phase | Thread | Virtualization work |
|---|---|---|
| **1 pump** | UI | (nothing virtualization-specific) |
| **2 input-dispatch** | UI | Scroll/wheel/drag updates `ScrollOffset` (Input owns it). Set `NodeFlags.VirtualRangeDirty` **only if the offset crossed an item boundary** (uniform: `floor` changed; variable: Fenwick range changed). In-window scroll sets nothing here → transform-only frame. |
| **3 hook-flush** | UI | (nothing) |
| **4 render** | UI | If `VirtualRangeDirty` **or** `DataVersion != RealizedDataVersion`: `RenderItem` the new window into the pooled `Element[]` (§3.1). Else skip entirely (memoized). `getItem` may return skeletons (§8). |
| **5 reconcile** | UI | Keyed-LIS diff of the window vs prev children: ENTER → `CreateNode`+`InsertChild`; EXIT → `RemoveChild`+synchronous cleanups+`DestroyNode` (slot reuse consume-gated, §5.4). UPDATE survivors in place via mask diff. |
| **6 layout** | UI | `VirtualLayout` two-pass (layout.md): extents → realized-row arrange. Uniform = O(1) `ContentSize`; variable = Fenwick + anchoring (§6.2). Writes `Bounds[]` (LOCAL) for realized rows + `ContentSize`. |
| **6.5 layout-effects** | UI | `UseVisibleRange.onChange` fires (range valid against arranged `Bounds`); `ScrollToIndex` (a layout effect) seeks. Media warm + page prefetch dispatched here (§3.3). `setState` here ⇒ mark dirty + N+1 (no sync re-loop). |
| **7 animation** | UI | `-ScrollOffset` → viewport-child `LocalTransform` (TransformDirty only — layout-free scroll). Sticky-header pin transform (§7). Skeleton shimmer animated-UV tick. Compose `WorldTransform[]` top-down. |
| **PUBLISH 13a** | UI | `SnapshotColumns.CopyInto` a free `SceneFrame` slot. WorldTransform of the whole realized window is re-copied during a live scroll (the documented up-to-subtree budget, threading-render-seam §3.2). `StickyPinned`/realized rows participate; nothing virtualization-special. |
| **8 record** | RENDER | Walk the realized window; ENTER/PaintDirty rows re-record; clean rows memcpy from the render-private prior arena (`IsLive ∧ epoch ∧ baked-geom hash`, gpu-renderer §13). Sticky-pinned header excluded from memcpy (§7). |
| **9 batch** | RENDER | Radix-sort sortkeys; the batcher re-applies the new `WorldTransform` to cached row quads (TransformDirty fast path — a scroll re-records nothing). |
| **10 submit / 11 present** | RENDER | Unchanged. |
| **12 passive-effects** | UI (N+1) | Page-fetch `Post` results applied here are *not* — they `RequestFrame(N+1)`; passive effects (subscriptions) for the collection run normally. |
| **13 arena swap** | RENDER/UI | Per-frame `ChunkedArena` reset (the keyed-LIS scratch); render-private DrawList arenas swapped by the render thread. |

**The one cross-thread contract:** slot reuse is consume-gated (§5.4). Everything else is single-writer on the
UI thread; the render thread only ever reads the published immutable snapshot.

---

## 11. Failure modes & edge cases

1. **Fling past 500 rows.** In-window frames are transform-only (0 realize). Each boundary frame diffs a
   ~40-key window (~few enter/exit, slab free-list, 0 paint-alloc). Blown-past page fetches + image decodes are
   `Cancel(RangeCts)`'d as ranges exit the prefetch margin (§4.2). No unbounded work; no per-frame realize of
   the whole list.
2. **`ItemCount` shrinks below `RealizedLast`** (items removed under the viewport). `DataVersion` bumps; phase-4
   re-realize clamps `[first,last)` to the new count; exited rows are removed-diffed; the anchor re-pins to
   `AnchorKey` if it still exists, else to the nearest surviving index.
3. **Duplicate `ItemKey`s** (the dev's `KeyOf` is not unique). The keyed map keeps last-write (reconciler-hooks
   §17.7); LIS still produces a valid (suboptimal) move set; no crash; diagnostic logged. Documented as a dev
   bug — `KeyOf` must be a stable unique identity.
4. **Decode→measure→relayout loop** — **prevented** by binding row height to the fixed decode bucket, never
   `NaturalSize` (§6.3). The `WgpuAnalyzer` should warn if a virtualized row's height expression references
   `UseImage(...).NaturalSize`.
5. **Stale async callback into a recycled slot** — dropped by the **request-epoch** in the hook cell (§5.1),
   not by `NodeHandle` generation (which does not bump on in-place recycle).
6. **Regroup mid-scroll.** `GroupVersion` bumps → extent table invalidated, anchor reset to a stable
   cross-grouping `ItemKey`, `ContentSize` re-seeded (§7). The viewport stays near the same content.
7. **Window buffer overflow vs cap-32 pool** — impossible: the window buffer is `ArrayPool<Element>`, never the
   cap-32 `ObjectPool` (§9, the explicit foundations / app-requirements §5 guard).
8. **A row binds a derived brush** (violates §5.3) — recycle would thrash `BrushCache`/the back-ref multimap.
   Caught by the `WgpuAnalyzer` warning; the runtime invariant is that rows carry only T0 static token
   `BrushHandle`s.
9. **`ScrollToIndex` to an unrealized variable-height index.** Uses the extent table's estimated offset to seek
   immediately; on the next frame the realized rows correct extents and the anchor re-pins so the target lands
   precisely (no visible jump because the target itself becomes the anchor).
10. **Empty collection / `ItemCount==0`.** `ContentSize=0`; no children realized; the host node clips an empty
    viewport; no work in any phase.
11. **Page fetch fails** (network error). The skeleton stays; `UseInfiniteResource` surfaces the error to the
    dev's error state (a retry affordance row); the failed range's CTS is not auto-cancelled (retry reuses it).
12. **Sticky header at the very top / bottom of the list.** `min(scrollOffset, nextHeaderOrigin − headerExtent)`
    naturally clamps: at the top the header sits at its own origin; pushed by the next header it slides up
    exactly one header's worth. No special-casing.

---

## 12. NativeAOT implications

- **No per-row closures.** `RenderItem`/`GetItem`/`KeyOf`/`FlatProjection` are fn-pointer-shaped delegates the
  dev supplies once (static methods or cached instance delegates), not minted per realize. Realizing 40 rows
  allocates 40 `Element` records (edge, bounded Gen0) but **0 delegates**.
- **`ItemKey`/`VirtualState`/`VirtualSpec` are blittable `unmanaged`** — `VirtualState` lives in a
  `SlabAllocator<T:unmanaged>`; `ItemKey` interns to `StringId`. No reflection, no boxing.
- **Generic instantiation is bounded.** `UseVirtual<T>`/`UseInfiniteCollection<T>` instantiate per item type
  `T` (finite, compile-known set — `Track`, `Album`, `Artist`, …); the heavy machinery (window realize, keyed
  diff, extent table) is **non-generic** (operates on `Element`/`NodeHandle`/`ItemKey`), so there is no
  generic-explosion of the diff/layout core per `T`.
- **No COM, no `ComPtr`, no command list, no fence** — the subsystem is pure C# over `ISceneBackend` + hooks
  (reconciler-hooks §14/§15). The macOS port ships it unchanged.
- **Source-gen.** Deps spans are lowered by `HookDepsGenerator` (dsl-aot §, reconciler-hooks §3.4); the row
  `Element`'s `Diff`/`MountWriter` are the same source-gen'd per-element switch every node uses.

---

## 13. Cross-platform / macOS boundary

**Everything in this subsystem is portable (zero Windows, zero D3D, zero COM, zero `ComPtr<T>`).** The
virtualizer is pure C# over `ISceneBackend`, `Element` records, the hook primitives, and the layout extent
table — all portable assemblies (`Reconciler`, `Scene`, `Layout`). It depends on Media/Theme only through
their *hooks* (`UseImage`, and the §5.3 *invariant* that rows do not call `UseDerivedBrush`), which are
themselves portable (the OS-specific decode/composite lives below the RHI/PAL/Text seams, ≥2 layers down).

| Concern | Portable? | Notes |
|---|---|---|
| `UseVirtual`/`UseInfiniteCollection`/`UseVisibleRange`, `VirtualState`, the keyed-LIS realize | **Yes** | Pure C# over `ISceneBackend` + `Element` |
| Extent table / `VirtualLayout` two-pass / anchoring | **Yes** | In `Layout` (the cleanest seam in the system — zero OS/GPU dep, architecture-spec §5.5) |
| Scroll input (`ScrollOffset` updates) | **Yes (PAL behind it)** | Wheel/touch/trackpad events arrive as portable `InputEvent`; macOS `NSEvent` momentum scroll maps to the same `-ScrollOffset` transform + inertia integrator (input-a11y) |
| Page fetch off-thread `Post` | **Yes (PAL)** | `IPlatformAppLoop.Post<TState>` — Windows message loop / macOS CFRunLoop, leaf-behind |
| Image warm/cancel via `UseVisibleRange` | **Yes** | `DecodeScheduler.Cancel` is portable; only the WIC/CoreImage codec leaf differs (media-pipeline) |

The macOS/Metal port reimplements only the PAL app-loop + the RHI/Text/codec leaves; the virtualization
subsystem ships **unchanged**. The one portability nuance worth naming: **momentum/inertial scrolling**
differs by platform (Windows precision-touchpad inertia vs macOS native momentum events) — but it is absorbed
entirely by the Input subsystem's inertia integrator feeding the same `ScrollOffset`; the virtualizer only
ever reads the resulting offset and never knows the source.

---

## 14. The SnapshotColumns / column-layout split (stated explicitly, per ownership resolution)

Per the cross-cutting ownership resolution: **threading-render-seam.md owns `SceneFrame`/`SnapshotColumns`/
`CopyInto` (the publisher-side POD); scene-memory owns the COLUMN LAYOUT those snapshot.** This subsystem adds
to neither's *shape* — it adds:
- two `NodeFlags` bits (`VirtualRangeDirty`, `StickyPinned`) read by phase-2/4 (UI) and, for `StickyPinned`,
  by phase-8 record (RENDER, via the snapshotted `Flags` column);
- one `VirtualState` SoA slab column whose **semantics** this doc owns and whose **storage shape** obeys
  scene-memory's SoA framework.

`VirtualState` is **NOT** part of `SnapshotColumns` — it is UI-thread-confined (like `LayoutInput`,
`InteractionInfo`, `A11yInfo`; threading-render-seam §3.1 "what is not copied"). The render thread never reads
`VirtualState`; by the time it records, the realized window is already child nodes with their own snapshotted
`Bounds`/`WorldTransform`/`NodePaint`/`Flags` (the standard ~120B/node copied set). The only virtualization
field the render thread observes is `NodeFlags.StickyPinned` (in the copied `Flags` column), which excludes the
pinned header from clean-span memcpy. So the split is: **virtualizer mutates `VirtualState` + realized child
columns on the UI thread → PUBLISH copies the realized children's standard `SnapshotColumns` → render thread
records them like any other nodes**, with `StickyPinned` the single bit that crosses.

---

## Changed vs the original synthesis

The amendments folded into this actualization (each ratifying a contract or closing a critique finding):

1. **`NodeFlags.VirtualRangeDirty`/`StickyPinned` are distinct bits, NOT the existing `Realized` bit.**
   `NodeFlags.Realized=1<<12` means "has a cached text/path realization still valid" (architecture-spec §2);
   conflating it with "row in the realized window" was the source-design naming bug. `VirtualRangeDirty` /
   `StickyPinned` are contiguous free bits (`1<<13`/`1<<14`, app-requirements §3.2), semantically separate.
2. **Recycling is `FreeNode`/`CreateNode` over the slab free-list — there is NO second `RecyclePool`.** The
   keyed-LIS diff *is* the recycler; the slab free-list *is* the pool. This avoids WinUI's `ElementPool`
   COM-detach pain (architecture-spec §5.4).
3. **The window buffer is `ArrayPool<Element>.Shared`, NEVER the cap-32 `ObjectPool`.** The cap-32 pool
   overflows precisely during list realization (a 40+-row window); this is the critique's CRITICAL catch
   (painpoints §99, app-requirements §5).
4. **Honest alloc claim:** in-window scroll = transform-only (~0); boundary = bounded-Gen0 delta-row render +
   pooled `Element[]` window; phases 6–13 paint machinery 0-alloc; only **ENTER** rows re-render (survivors
   memoized). It is O(Δ) Gen0, not zero — CI-gated (hardened §4.5).
5. **The `VirtualState` column is slab-backed (NOT arena-backed)** — it survives across frames (realized range,
   data version, live cancel ticket, anchor); the source designs' arena/slab contradiction is fixed in favor
   of the slab. Its semantics are owned here; its storage shape obeys scene-memory.
6. **Per-range `CancellationTokenSource` lifecycle** (allocate on live-range, cancel on overscan+prefetch exit,
   reset on host destroy) is the structural fling-cancellation; the **request-epoch in the hook cell** (NOT
   `NodeHandle` generation) is the deeper stale-callback backstop that survives in-place recycle (§5.1).
7. **Uniform vs variable policy owned here; extent-table mechanics deferred to `layout.md`.** Track rows pin a
   fixed app-layer density height to keep the uniform O(1) path valid; variable is forced when grouped or
   `ItemExtent==0`, with slab-backed Fenwick extents + `ItemKey`-anchored scroll correction.
8. **The decode→measure feedback-loop break:** row height binds the fixed decode bucket, never the late
   `NaturalSize` — a decode completing never changes layout, so no decode→measure→relayout loop (§6.3).
9. **Grouping = app-supplied flat `index→(kind,payload)` projection** (the virtualizer does not group); sticky
   headers = a **phase-7 transform** (`min(scrollOffset, nextHeaderOrigin−headerExtent)`) excluded from
   clean-span memcpy via `StickyPinned` — not a layout change, not an offscreen layer.
10. **Incremental load = +1-frame latency**, worker `Post` marks dirty + `RequestFrame(N+1)` (no synchronous
    re-loop — architecture-spec §5.4 / reconciler-hooks §4.2); skeleton→real shares `ItemKey` so it updates in
    place; shimmer is an animated gradient **fill**, not a `PushLayer` RT.
11. **Rows bind NO derived brushes** (theming §9 cross-subsystem invariant): row `Fill`/`Foreground` are T0
    static token handles only; only O(1)-per-screen page-level surfaces call `UseDerivedBrush`. This is what
    keeps recycle O(1) for Theme (no `BrushCache`/back-ref thrash at scroll velocity).
12. **Consume-gated slot reuse** across the render-thread seam (`QUARANTINE = RenderInFlightDepth + 1`
    (belt-and-suspenders, compile-asserted); =0 single-thread step 1): the virtualizer just `DestroyNode`s; the `QuarantineLedger`
    parks the slot. No virtualization special-casing.
