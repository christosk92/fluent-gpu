# Layout Engine — incremental Yoga + Grid + scroll + virtualization participant

> **Subsystem:** `FluentGpu.Layout` (fully portable; `Foundation` + `Scene` + `Text`-iface only — zero
> Windows/GPU dependency, the cleanest seam in the system).
> **Owner of:** the ported-Yoga flexbox algorithm over a `ref struct LayoutNode` on SoA columns + arena scratch +
> `[InlineArray]` `Ring8`/`Edges4` (bit-for-bit numeric parity, 8-entry measurement cache); **incremental O(change)
> layout** with the boundary firewall + two-rule invalidation graph; incremental pixel-snap; **scroll-as-transform**
> (`-ScrollOffset` `LocalTransform`, layout-free); the **Grid distinct true-tracks** algorithm; the
> `VirtualList`/`VirtualGrid` `LayoutKind` + the extent table (slab-backed Fenwick/BIT) + scroll-anchoring as the
> **layout-participant contract** for virtualization; the **text intrinsic-measure seam** call into `ITextShaper`;
> absolute/overlay positioning.
> **Runs on:** the **UI thread**, phase **6** (with `ScrollToIndex`/`UseVisibleRange` reads in **6.5**). Zero
> per-frame managed allocation.

This doc obeys the cross-cutting contracts in `../foundations.md`, `../architecture-spec.md` §4.4/§4.8/§5.5,
`../hardened-v1-plan.md` §2/§4.4/§7, and `../dotnet10-csharp14-zero-alloc.md` §D. It REFERENCES (never redesigns):
`scene-memory` (the `LayoutInput`/`Bounds` columns + the SoA spine — currently `architecture-spec.md` §4.4 until
that doc lands), `reconciler-hooks.md` (phase-6.5 reads `Bounds`; `ISceneBackend.WriteLayout`/`MarkDirty`; the
`UseVirtual` hook family; the 3-signal memo skip; `SubtreeDirty` is **traversal scope only**), `text.md` (the
`ITextLayoutEngine.Measure` seam; the `static readonly YogaMeasureFunc` + `TextLayoutHandle` user-data slot),
`virtualization` (the hook-level window/recycle logic — not yet written; its layout-participant half is owned here),
`threading-render-seam.md` (`Bounds`/`WorldTransform` are value-copied into `SnapshotColumns` at PUBLISH).

---

## 1. SCOPE, OWNERSHIP, AND THE ONE EXTERNAL CALL

### 1.1 What this subsystem owns

| Owned artifact | One-line | §  |
|---|---|---|
| `FlexLayout.Run` — the ported Yoga `CalculateLayoutImpl` 11-step body | the flexbox engine, verbatim algorithm | §3 |
| `ref struct LayoutNode` | 16B stack view over SoA columns (`Style.X` / `Layout.SetY` → inlinable column accessors) | §2.2 |
| `LayoutInput` (96B hot column) + `LayoutAux` (cold slab) | the `YogaStyle` field set, hot/cold split | §2.1 |
| `LayoutCacheEntry` (slab struct) | `Ring8` 8-entry measurement cache + `CachedLayout` + `RawDim` + snap origins + generation | §2.3 |
| `LayoutScratch` (non-relocating bump region) | per-`Run` child lists + `FlexLine`s, `(offset,len)` into a stable buffer | §3.5 |
| The incremental invalidation graph (boundary firewall + 2 rules) | O(change) re-layout scope | §4 |
| Incremental pixel-snap | per-node `absLeft/absTop` cache, re-snap only the moved subtree | §5 |
| Scroll-as-transform contract | layout publishes `ContentSize`; `-ScrollOffset` is a `LocalTransform`, never relayout | §6 |
| `FluentGpu.Layout.Grid` — distinct true-tracks algorithm | Auto/Pixel/Star track sizing, spanning, placement | §7 |
| `VirtualList`/`VirtualGrid` `LayoutKind` + `ExtentTable` (Fenwick/BIT) + scroll-anchoring | virtualization **as a layout participant** | §8 |
| `MeasureKind` dispatcher + the `ITextShaper`/`ITextLayoutEngine` measure seam call | the only external dependency on the hot path | §9 |
| Absolute / overlay positioning + `FormsContainingBlock` containing-block model | ported `LayoutAbsoluteDescendants` | §10 |

### 1.2 What it explicitly does NOT own (and where it lives)

- `Bounds`/`LayoutInput` **column storage + the SoA spine** → `scene-memory` (`architecture-spec.md` §4.4). Layout
  *writes* `Bounds[]` and `ContentSize`, *reads* `LayoutInput[]`/`LayoutAux`; it does not own the arrays.
- `ISceneBackend.WriteLayout`/`MarkDirty`/`EditLayout` **op semantics** → `reconciler-hooks.md`. The reconciler
  marks `LayoutDirty`/`LayoutSelfDirty`/`LayoutParticipationDirty`; layout *consumes* the dirty worklist.
- The **`UseVirtual`/`UseInfiniteCollection`/`UseVisibleRange` hooks** + realize-window-as-keyed-children +
  recycle/cancel invariants → `reconciler-hooks.md` + `media-pipeline.md` (the hook compositions). This doc owns
  only the **layout-participant** half: the extent table, `VisibleRange` arithmetic, anchoring, two-pass virtual
  layout, and the `LayoutKind` enum.
- **Text shaping/wrap/itemize** → `text.md`. Layout calls `ITextLayoutEngine.Measure` and treats the result as
  opaque metrics; it never sees a `string`, never shapes.
- **`WorldTransform[]` composition** (phase 7) and the `LocalTransform` scroll write are *consumed* by layout's
  scroll contract but the writes are owned by Animation/Input (`architecture-spec.md` §5.5: "Input owns
  `ScrollOffset`; Layout writes `ContentSize`; Render reads the clip").

### 1.3 The single seam call

`ITextLayoutEngine.Measure(in TextLayoutRequest, in MeasureConstraints) → TextMeasure` (text.md §3.x) is the **only**
call out of `FluentGpu.Layout` on the hot path. DPI arrives as a plain `float scale` (no PAL/RHI). Everything else
is column reads/writes and arena scratch. This keeps the macOS port (CoreText) a Text-leaf swap with **zero layout
changes** (§11).

---

## 2. THE SUBSTRATE: SoA columns, the LayoutNode view, the cache slab

Per `architecture-spec.md` §3b reuse table: **port the Yoga algorithm body verbatim; rewrite the substrate.** The
class graph (`YogaNode`/`YogaStyle`/`LayoutResults` classes, `List<YogaNode>`, `[ThreadStatic] Stack<>` pool,
`IEnumerable GetLayoutChildren()`, `YogaMeasureFunc` delegate-per-node) is dissolved into SoA columns + a stack view
+ arena scratch + a central dispatcher.

### 2.1 `LayoutInput` (96B hot) + `LayoutAux` (cold slab) — the `YogaStyle` field set

`LayoutInput` is a parallel SoA column (index = node slot) in `SceneStore` (owned by scene-memory; field set owned
here). Exact byte layout asserted via `Unsafe.SizeOf<LayoutInput>() == 96` in a startup check + a `[Conditional]`
test. The ~10% of nodes that use percent-precision min/max, border, or insets indirect to a `LayoutAux` slab; the
common leaf shares a zero-sentinel `LayoutAux` (folds the "~96B vs ~220B" contradiction, `architecture-spec.md` §4.4).

```csharp
namespace FluentGpu.Layout;

[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct LayoutInput
{
    // sizing (LengthValue = {float Value; LengthUnit Unit} — 8B; Undefined == default, see §3.6)
    public LengthValue Width, Height;          // 16
    public LengthValue MinWidth, MinHeight;    // 16
    public LengthValue MaxWidth, MaxHeight;    // 16  (text MaxWidth pre-quantized UP to device-px grid, §9)
    public float       AspectRatio;            // 4   (NaN == none)
    // flex
    public float       FlexGrow, FlexShrink;   // 8
    public LengthValue FlexBasis;              // 8
    public Edges4      Margin;                 // 16  ([InlineArray(4)] float — L,T,R,B; see §2.4)
    // packed enums + gap + the cold ref
    public LayoutPacked Packed;                // 4   (FlexDir|Wrap|JustifyContent|AlignItems|AlignSelf|AlignContent|PositionType|Display|Overflow|BoxSizing — bitfields)
    public ushort      LayoutKind;             // 2   (Flex | Grid | VirtualList | VirtualGrid | Absolute-overlay; §2.5)
    public ushort      AuxRef;                 // 2   (0 = shared zero-sentinel; else index into SlabAllocator<LayoutAux>)
    public float       GapRow, GapColumn;      // 8   (flex `gap`; grid track gap)
} // = 96B exactly
```

```csharp
public struct LayoutAux   // SlabAllocator<LayoutAux>, ~10% of nodes
{
    public Edges4      Padding;                // 16
    public Edges4      Border;                 // 16
    public Edges4      Position;               // 16  (L,T,R,B insets for non-Static)
    public LengthValue MinWidthPct, MinHeightPct, MaxWidthPct, MaxHeightPct; // percent-precision min/max
    public GridRef     Grid;                   // 8   (-> GridDefinition slab; §7) — only set when LayoutKind=Grid
    public VirtualRef  Virtual;                // 8   (-> VirtualState slab; §8) — only set when LayoutKind=VirtualList/Grid
}
```

`LengthValue` numeric parity (folded, `architecture-spec.md` §5.5): `Resolve` divides percent by `/ 100.0f`
(bit-for-bit vs the original Reactor Yoga); **`default(LengthValue)` unambiguously means `Undefined`** — every ported
`float.IsNaN(Value)` check is switched to test `Unit == LengthUnit.Undefined` (the original overloaded NaN-on-`Value`;
we make the unit authoritative so the struct's natural zero is the right sentinel).

### 2.2 `ref struct LayoutNode` — the 16B stack view

`YogaNode` (a heap class) → `ref struct LayoutNode` (16B stack view over the SoA columns). The algorithm body reads
`node.Style.FlexDirection` and writes `node.Layout.SetWidth(w)`; those map to **inlinable column accessors** indexed
by the node's slot. No heap object, no `Dictionary<UIElement,YogaNode>`, no per-node allocation.

```csharp
public readonly ref struct LayoutNode
{
    private readonly LayoutContext _ctx;   // by-ref carrier of column base pointers (a ref struct, §2.6)
    public  readonly int           Slot;   // sparse slab index

    public LayoutNode(in LayoutContext ctx, int slot) { _ctx = ctx; Slot = slot; }

    // ── style getters: inlined column reads (extension members hang Width/IsRow/etc. — C#14 §dotnet10 §D) ──
    public ref readonly LayoutInput Style => ref _ctx.Layout[Slot];
    public ref LayoutResultWrite     Layout => ref _ctx.ResultWriter(Slot);   // SetWidth/SetX/... write Bounds[]
    public ref LayoutCacheEntry      Cache  => ref _ctx.Cache[Slot];

    // ── topology as index loops (no IEnumerable; FirstChild/NextSibling over the Topology column) ──
    public int FirstChild => _ctx.Topo.FirstChild[Slot];
    public int NextSibling(int child) => _ctx.Topo.NextSibling[child];
    public int ChildCount => _ctx.Topo.ChildCount[Slot];

    [MethodImpl(AggressiveInlining)]
    public LayoutNode Child(int slot) => new(_ctx, slot);
}
```

`GetLayoutChildren()` iterators → **index loops** over `FirstChild`/`NextSibling` (the doubly-linked sibling list in
the Topology column). `Passthrough` (`ComponentAnchor`) children are skipped (the anchor is layout-invisible —
`reconciler-hooks.md`).

### 2.3 `LayoutCacheEntry` — field-complete vs `LayoutResults`

The original Yoga carries seven measurement/layout arrays + `_rawDimensions`. We reproduce all of them in one slab
struct (a `SlabAllocator<LayoutCacheEntry>` side-column owned here, parallel to the node slot). **`RawDim` is
included** (the seventh `_rawDimensions` array the original carries — its omission was a parity bug).

```csharp
public struct LayoutCacheEntry
{
    public Ring8 CachedMeasW, CachedMeasH;       // [InlineArray(8)] float — the 8-entry measurement ring
    public Ring8 CachedMeasAvailW, CachedMeasAvailH;
    public CachedLayout CachedLayout;            // last full-layout result keyed by (availW,availH,wMode,hMode)
    public RectF  RawDim;                        // _rawDimensions — pre-rounding measured size
    public float  AbsLeft, AbsTop;               // committed snapped absolute origin (incremental pixel-snap seed, §5)
    public uint   Generation;                    // Yoga generation counter (single-thread uint, no Interlocked, §4.5)
    public ushort ConfigVersion;                 // bump on DPI/scale change forces relayout (§5)
    public byte   CachedCount;                   // ring fill count (0..8)
    public byte   _pad;
}
```

The 8-entry ring is the **measurement cache** (confirmed in the original `LayoutResults.cs`): a measure with
`(availW, wMode, availH, hMode)` matching a ring slot returns the cached size without re-measuring. Cache validity is
gated by `Generation` (a node-tree-wide counter bumped when any style changes — §4.5) and `ConfigVersion` (scale).

### 2.4 `Edges4` / `Ring8` — `[InlineArray]`, zero heap

```csharp
[InlineArray(4)] public struct Edges4 { private float _e0; }   // L,T,R,B
[InlineArray(8)] public struct Ring8  { private float _e0; }   // the measurement ring
```

`Span<float>`-convertible, stack/inline, no `unsafe fixed` (`dotnet10` §D). **Trap honored:** never combine
`Unsafe.SkipInit` with a "read whole span" on a partially-filled `Ring8` — the ring tracks `CachedCount` precisely and
only reads written slots.

### 2.5 `LayoutKind` — the dispatch enum (folded for virtualization)

```csharp
public enum LayoutKind : ushort
{
    Flex = 0,        // default — FlexLayout.Run (§3)
    Grid = 1,        // FluentGpu.Layout.Grid (§7)
    VirtualList = 2, // VirtualLayout two-pass, 1-axis (§8)
    VirtualGrid = 3, // VirtualLayout two-pass, 2-axis (shelf walls)
    // (Stack/VStack are a trivial Flex projection — zero new engine code, arch §5.5)
}
```

`VirtualList`/`VirtualGrid` are net-new `LayoutKind`s (`app-requirements` §3.2 Scene delta). `Absolute`/overlay is NOT
a `LayoutKind` — it is `PositionType` in `LayoutInput.Packed` driving the containing-block model (§10).

### 2.6 `LayoutContext` — the by-ref carrier (no `[ThreadStatic]`)

`[ThreadStatic] Stack<>` pools are gone. `LayoutContext` is a `readonly ref struct` carrying base pointers to the
columns + the live `LayoutScratch` + the `float scale` + the `ITextLayoutEngine` seam. It is constructed once at
`FlexLayout.Run` entry and threaded by `in`/`ref readonly` through the recursion (the same explicit-cursor discipline
the DSL `[FastPath]` builder uses — `architecture-spec.md` §5.7). No global mutable state ⇒ trivially correct under
the future render-thread seam (layout is UI-thread-confined anyway).

---

## 3. THE FLEX ALGORITHM — ported `CalculateLayoutImpl`, verbatim numerics

### 3.1 Single pass (no WinUI two-pass)

**Decision (`architecture-spec.md` §5.5):** single-pass Yoga. The WinUI `FlexPanel` `_arranging`/`LayoutCycle
Exception`/`Dictionary<UIElement,YogaNode>` machinery is eliminated. Measure and arrange happen in **one descent**:
Yoga computes sizes top-down with available-space propagation and writes final positions on the way back up. Results
land in `SceneStore.Bounds[i]` in **LOCAL** space (P8) — `Bounds` is node-local; `LocalTransform` maps local→parent.

### 3.2 The 11 steps (kept, in order)

The ported `CalculateLayoutImpl` keeps the exact 11-step structure of the original:

1. **Cache check** — probe the `Ring8` measurement cache; if `(availW,wMode,availH,hMode)` matches and
   `Generation`/`ConfigVersion` valid, return cached and stop (the incremental win, §4).
2. **Resolve flex direction + available main/cross** from `Style.Packed` and the parent's resolved inner size.
3. **Compute leaf size / measure callback** — if `ChildCount==0` and a `MeasureKind` is set, dispatch to the central
   measurer (§9); else resolve from `Width`/`Height`/`AspectRatio`/min/max.
4. **Determine flex basis** for each child (resolve `FlexBasis`, else main-size, else content via a sub-measure).
5. **Collect flex lines** (`ref struct FlexLine` over arena `Span<int>` child slots; wrap when `FlexWrap` and the
   main axis overflows).
6. **Resolve flexible lengths** — the free-space distribution loop: grow/shrink by `FlexGrow`/`FlexShrink`-weighted
   shares, clamped by min/max, iterated to a fixed point (the original's two-pass freeze-violation loop, kept).
7. **Main-axis alignment** (`JustifyContent`: flex-start/center/end/space-between/around/evenly + `gap`).
8. **Cross-axis alignment** per line (`AlignItems`/`AlignSelf`: stretch/start/center/end/baseline).
9. **Multi-line cross alignment** (`AlignContent`) when wrapped.
10. **Absolute-positioned children** — `LayoutAbsoluteDescendants` against the containing block (§10).
11. **Set final dimensions + position**, write `Layout.SetX/Y/Width/Height` → `Bounds[]`; **§4.5 min** (the
    `roundValueToPixelGrid`/min-content guard the original applies) honored; populate the `Ring8` ring + `RawDim`.

**Baseline** (step 8 `AlignBaseline`): the ported baseline-resolution recursion is kept (a baseline-aligned line
queries each child's baseline via the same `MeasureKind` dispatch — text returns its first-line baseline).

### 3.3 `FlexLine` — arena-backed, ref struct

```csharp
public ref struct FlexLine
{
    public Span<int> Children;     // slots, sliced from LayoutScratch (§3.5)
    public float     MainDim;      // sum of basis + gaps
    public float     CrossDim;     // max cross size
    public int       Count;
    public float     TotalGrow, TotalShrinkScaled;
}
```

`List<YogaNode>` child lists → `arena Span<int>`; `[ThreadStatic] Stack<>` line pool → `ref struct FlexLine` over the
scratch. No managed allocation.

### 3.4 `Stack`/`VStack` projection

`Stack`/`VStack` are not a separate engine: they set `FlexDirection=Row/Column`, `FlexWrap=NoWrap`, default
`AlignItems=Stretch` — a trivial projection onto `FlexLayout.Run` (zero new code, `architecture-spec.md` §5.5).

### 3.5 `LayoutScratch` — the non-relocating bump region (folds the BLOCKER)

The keystone scratch invariant (`architecture-spec.md` §5.5): `LayoutScratch` is a **pre-sized, NON-relocating** bump
region for the duration of **one** `FlexLayout.Run`. It is sized at pass entry from the live-node count (the dirty
subtree's node count for incremental, capacity-rounded). Child lists held **up the recursion spine** stay valid
because the backing buffer never moves during the descent; lists are stored as `(offset, len)` into the stable buffer.

- **Grown only BETWEEN passes, never during** a descent. A defensive grow mid-descent is detectable and **forbidden**:
  a DEBUG assert checks the base pointer is unchanged across the descent (`Debug.Assert(scratch.BasePtr == entryPtr)`).
- This is the one place the `hardened-v1-plan.md` §7 relaxation ("`ChunkedArena` add-chunk mid-pass, addresses
  stable") **interacts** with layout: `LayoutScratch` may be a `ChunkedArena` *as long as add-chunk preserves the
  addresses of already-handed-out spans* (reserve-then-commit / segmented). The non-relocating guarantee is the
  contract; the segmented `ChunkedArena` satisfies it (no copy, native-backed). Plain `byte[]`-arena `LayoutScratch`
  must be pre-sized and may not `Array.Resize` mid-descent.
- Scratch is reset (`head=0`) at each `Run` entry; it is **never cross-frame state**.

### 3.6 Numeric parity (folded, binding)

- `LengthValue.Resolve` uses `/ 100.0f` (bit-for-bit vs source).
- `default(LengthValue)` ≡ `Undefined`; all NaN-on-`Value` checks switched to `Unit == Undefined`.
- The **generation counter detects wrap** via an epoch sweep rather than calling `Interlocked` 2³² times (unreachable);
  plain `uint`, single-thread (§4.5).
- `MaxLayoutDepth = 256` cap kept; the reconciler validates the topology is cycle-free before layout runs (so the cap
  is a guard, not a correctness crutch).
- C#14 compound-assignment trap (`dotnet10` §D): the free-space accumulators use instance `operator +=` on
  `RectAccum`/`float` columns; on phases 6–13 every `+=`/`++` **discards its result** and targets a real variable (a
  SoA column `ref`, never a property getter) — else the compiler silently falls back to the value-returning operator
  and reintroduces the copy.

---

## 4. INCREMENTAL LAYOUT — boundary firewall + two-rule invalidation graph (O(change))

The headline `hardened-v1-plan.md` §4.4 hardening: **O(change) layout via the layout-boundary firewall + a two-rule
invalidation graph**, layered on top of Yoga's generation-counter cache + the `NodeFlags` dirty scope.

### 4.1 Dirty taxonomy (the MAJOR amendment, `architecture-spec.md` §4.4)

The reconciler distinguishes two dirt classes when it writes `LayoutInput` (it computes this from the
source-gen'd `DiffProps` mask in `WriteLayout`):

- **`LayoutSelfDirty` (intrinsic-affecting):** `Width`/`Height`/`Min`/`Max`/`AspectRatio`/content (text/image
  natural size). Changes the node's **own measured size**.
- **`LayoutParticipationDirty` (participation-affecting):** `FlexGrow`/`FlexShrink`/`FlexBasis`/`AlignSelf`/`Margin`/
  `order`/`PositionType`. Changes how the node **participates in its parent's** flex/grid distribution.

**Rule (binding):** participation dirt **always** forces the parent container's flex/grid algorithm to re-run,
regardless of whether the child's measured size changed. Intrinsic dirt that leaves measured size **identical** may
stop at the node (the "measured-size-unchanged ⇒ stop" rule applies **only** to intrinsic dirt).

### 4.2 The two-rule invalidation graph

When a node is marked dirty, invalidation propagates by exactly two rules:

1. **Up-rule (size dependency):** if a node's measured size *changes*, its parent's layout is invalidated (the parent
   may need to re-run free-space distribution). This walks **up** until it hits a **layout boundary** (§4.3) or a node
   whose re-layout produces an unchanged size — whichever comes first.
2. **Down-rule (available-space dependency):** if a node's *available space* changes (parent resized it, or its own
   size constraints changed), its children whose sizes depend on available space (`%`, `stretch`, `FlexGrow`, text
   wrap) are invalidated **down**. Children whose size is fixed/absolute and independent of available space are NOT
   re-measured.

These two rules are the entire incremental story: the dirty worklist (§4.4) seeds them, and they bound the re-layout
to exactly the nodes whose result can change.

### 4.3 The layout-boundary firewall

A **layout boundary** is a node whose size is fixed independent of its children AND whose available space is fixed
independent of its parent — a subtree whose internal changes cannot escape and whose external changes cannot enter:

```csharp
[MethodImpl(AggressiveInlining)]
static bool IsLayoutBoundary(in LayoutInput s) =>
       (s.Width.Unit  != Undefined && s.Width.Unit  != Percent)      // fixed width
    && (s.Height.Unit != Undefined && s.Height.Unit != Percent)      // fixed height
    && s.FlexGrow == 0 && s.FlexShrink == 0                          // doesn't stretch/shrink to parent
    && s.AspectRatio is float.NaN                                     // not aspect-coupled
    && (s.Packed.Overflow != Overflow.Visible);                       // contains its own overflow
```

The up-rule **stops** at a layout boundary: a change inside a fixed-size, non-flexing container cannot change the
container's own size, so the parent never re-runs. This is what makes a `setState` deep inside a fixed-size card
**not** relayout the whole page. **Virtual viewports are always layout boundaries** (§8): the viewport has a fixed
extent and its content is positioned by transform, so realizing/derealizing rows never escapes the viewport.

### 4.4 The dirty worklist (idle = O(0))

The primary mechanism (folded, `architecture-spec.md` §4.4) is an **arena-backed dirty-node worklist** that
`MarkLayoutDirty`/`MarkLayoutSelfDirty`/`MarkLayoutParticipationDirty` append to. Per-frame layout cost is
**O(dirty)**, not O(capacity). Idle frames touch zero nodes (no `Vector256` `FlagsSpan` scan in steady state — that
scan + the `SubtreeLayout` aggregate bit are kept **only** for the full-rebuild/overflow path: first frame, resize,
DPI change, theme-everything-dirty).

```
phase 6 layout (UI):
  if (worklist.IsEmpty && !fullRebuild) return;        // O(0) idle
  scratch.Reset(); sizeFromCount(liveNodes-or-dirtySubtree)
  for each root in dirty-boundary-set (deduped, top-down ordered):
      FlexLayout.Run(root, availFromBounds(root.Parent), scale)   // §3, two-rule scoped
  worklist.Clear()
```

`fullRebuild` is set on `ConfigVersion` change (scale), window resize, or worklist overflow. `SubtreeLayout`
aggregate bits, if maintained at all, are recomputed in a **single bottom-up sweep, never cleared piecemeal**
per-phase (removes the cross-phase clearing hazard, `architecture-spec.md` §4.4).

### 4.5 The generation counter (single-thread, no `Interlocked`)

Yoga's cache invalidation is a tree-wide `uint Generation` bumped whenever any style changes. Single-thread, plain
`uint` (no `Interlocked` — layout is UI-thread-confined). A node's `Ring8`/`CachedLayout` is valid iff
`entry.Generation == treeGeneration` AND `entry.ConfigVersion == scaleConfigVersion`. Wrap (2³² style changes) is
detected by an epoch sweep that resets all entry generations (rather than the unreachable `Interlocked` count).

### 4.6 `SubtreeDirty` is TRAVERSAL SCOPE ONLY (binding, cross-doc)

Per `reconciler-hooks.md` §6.3 + `hardened-v1-plan.md` §4.4: `SubtreeDirty` (the upward-propagated bit) is the
**traversal scope** — it tells the reconciler which subtrees to descend. It is **NEVER** a layout-skip-decision input.
Layout's skip decision is the Yoga cache + the two-rule graph + the boundary firewall, full stop. (This mirrors the
reconciler keystone: a `SubtreeDirty`-only skip would wrongly skip a non-dirty-path consumer.) Layout consumes the
`LayoutDirty`/`LayoutSelfDirty`/`LayoutParticipationDirty` per-node bits + the worklist, not `SubtreeDirty`.

---

## 5. INCREMENTAL PIXEL-SNAP (folds the MAJOR)

Pixel-snap converts Yoga's fractional LOCAL positions to device-pixel-aligned positions so edges are crisp. The
original applies a full-tree snap pass; we make it **incremental**.

- Each node caches its committed snapped absolute origin `(AbsLeft, AbsTop)` in `LayoutCacheEntry` (§2.3).
- After a `FlexLayout.Run` produces fresh LOCAL `Bounds`, the snap pass **re-snaps only the changed-position subtree
  plus its moved later-siblings**, seeded from the parent's **committed** snapped origin (so a child's absolute snap
  point is `parentAbsSnapped + childLocal`, rounded — accumulation error doesn't drift across frames).
- The **exact nearest-rounding expression is reproduced verbatim** from the original (`roundValueToPixelGrid` with the
  scale factor), including the text `hasFractionalWidth` right/bottom **ceil/floor coupling** (text right/bottom edges
  round so the glyph box never clips a sub-pixel of coverage). `Bounds` stays LOCAL; the snap operates on the
  accumulated absolute then the snapped local is `snappedAbs - parentSnappedAbs`.
- **`ConfigVersion` (scale) change forces a full re-snap** (and full relayout): DIP→px conversion happens once at the
  pump boundary and once here at layout→world composition; a DPI change without a client-size change does **not**
  resize the swapchain but **does** bump `ConfigVersion` (`architecture-spec.md` §5.5, §6 data-flow note).
- **Parity gate:** golden `Bounds` tests at scale **1.25 / 1.5 / 1.75** against the original Reactor Yoga snapped
  values (`architecture-spec.md` §5.5, §8 — the primary regression net for this subsystem).

---

## 6. SCROLL AS TRANSFORM (folds the MAJOR — layout-free scrolling)

The load-bearing decision (`architecture-spec.md` §5.5): **scrolling is layout-free.**

1. Layout arranges the scroll content at the **content-box origin** (0,0 in the content's local space) and publishes
   `ContentSize` (the full arranged extent of the content) onto the viewport node.
2. The **`-ScrollOffset` translation is the viewport-child's `LocalTransform`**, written by **Input** (drag/wheel) or
   **Animation** (fling/`ScrollToIndex` smooth) in **phase 2 / phase 7** — NOT by layout.
3. That write marks only **`TransformDirty` + `PaintDirty`** on the viewport-child — **never `LayoutDirty`**. So a
   scroll frame runs zero layout: phase 7 composes `WorldTransform`, phase 8 reuses the clean DrawList spans (a
   `TransformDirty`-only node reuses its span; the batcher re-applies the new `WorldTransform` to the cached quads at
   submit — `gpu-renderer.md` §11.1), phase 11 presents. **A within-content scroll that does not change the realized
   window is a transform-only frame.**

**Ownership (pinned, binding):** Input owns `ScrollOffset`; Layout writes `ContentSize`; Render reads the clip. The
viewport node carries `Overflow != Visible` so it is a clip + a layout boundary (§4.3) — content overflow is clipped,
and the realize/derealize of rows (§8) cannot escape it.

`ScrollOffset` is clamped to `[0, ContentSize - ViewportExtent]` by Input using the `ContentSize` layout published last
frame; layout never sees the offset. The shy/parallax header (WaveeMusic) is a separate transform write driven by the
same `ScrollOffset`, also layout-free.

---

## 7. GRID — distinct true-tracks algorithm (`FluentGpu.Layout.Grid`)

**Decision (`architecture-spec.md` §5.5):** a distinct algorithm, **not** nested-flex faking. Grid shares
`LayoutNode`/`LayoutCacheEntry`/snap/text-measure/`Bounds` with flex, but the track-sizing is its own.

### 7.1 `GridDefinition` — a struct with slab-backed track storage (folded)

`GridDefinition` is a **struct** (NOT a class in `SlabAllocator<T:unmanaged>` — folded correction) with arena/slab-
backed `TrackDef` storage, referenced via `LayoutAux.Grid` (`GridRef`):

```csharp
public struct GridDefinition          // referenced by GridRef, lives in a parallel slab
{
    public TrackSpan Columns;          // (offset,count) into SlabAllocator<TrackDef>
    public TrackSpan Rows;
    public float     RowGap, ColumnGap;
    public byte       AutoFlow;        // Row | Column | RowDense | ColumnDense
    public TrackDef   AutoColumns, AutoRows;   // implicit-track template
}

public struct TrackDef                 // SlabAllocator<TrackDef> (unmanaged)
{
    public TrackSizeKind Kind;         // Auto | Pixel | Star | MinContent | MaxContent | FitContent
    public float         Value;        // px for Pixel; weight for Star; fit-content limit
    public float         MinPx, MaxPx; // resolved bounds (written during sizing)
}
```

### 7.2 The track-sizing algorithm

A faithful CSS-Grid-style track sizing (the "distinct true-tracks" algorithm the spec names):

1. **Placement** — resolve each child's `(rowStart,rowSpan,colStart,colSpan)`; auto-placement cursor honoring
   `AutoFlow` (dense/sparse). Explicit + implicit (auto) tracks materialized into the `TrackDef` slab.
2. **Resolve intrinsic track sizes** — for `Auto`/`MinContent`/`MaxContent`/`FitContent` tracks, measure spanning
   items (via the `MeasureKind` dispatch, §9) and distribute their content size across the tracks they span (the
   span-distribution rule: grow the smallest first, respect `MinContent` floors).
3. **Resolve `Star` (fr) tracks** — distribute the remaining free space (container inner size minus fixed/auto track
   sizes minus gaps) proportionally by `Star` weight; `min-content` of a star track is a floor.
4. **Position items** — each item's cell rect = the summed track offsets + gaps; align within the cell
   (`justify-self`/`align-self`: start/center/end/stretch). Write `Bounds[]` LOCAL.
5. **Pixel-snap + ring populate** as in flex (§5, shared).

`true-tracks` means each track has a real resolved size shared by all items in it (unlike nested-flex faking, where
each row independently re-derives column widths and they don't align). Grid is budgeted as a **separate deliverable
with its own correctness surface** (golden track-resolution tests).

### 7.3 `VirtualGrid` interaction

`VirtualGrid` (§8) reuses the Grid track-sizing for the **cross-axis** (e.g. shelf-wall columns) and the virtual
extent table for the **scroll-axis** — the visible band of rows is realized; each realized row's items are placed by
the shared track algorithm. Track sizing runs once for the viewport width; only the realized rows are positioned.

---

## 8. VIRTUALIZATION — the layout-participant contract

> Per the task ownership split: this doc owns the **layout-participant** half (extent table, visible-range
> arithmetic, anchoring, two-pass virtual layout, the `LayoutKind`). The **hook-level window/recycle logic**
> (`UseVirtual`/`UseInfiniteCollection`/`UseVisibleRange`, realize-window-as-keyed-children, recycle/cancel
> invariants, the +1-frame page-fetch latency) lives in `reconciler-hooks.md` + `media-pipeline.md` —
> **reference `virtualization` (not yet written) for the full hook design.**

### 8.1 Core decision (KEEP — blessed)

**Realize-window-as-keyed-children** (`app-requirements` §3.2). A virtual node has `LayoutKind.VirtualList/VirtualGrid`
+ a datasource handle. Each frame it computes `[first,last)` from `ScrollOffset` + viewport + overscan, the hook calls
the dev's `RenderItem(i)` **only** for the window, and hands those to the **existing keyed-LIS reconciler** with stable
`ItemKey`s. Recycling **is** `FreeNode`/`CreateNode` over the slab free-list — no second `RecyclePool` layer (avoids
WinUI's `ElementPool` COM-detach pain). **This subsystem provides the layout half:** the extent model, the visible-
range computation, and the two-pass arrange.

### 8.2 `VirtualState` — the slab-backed column

```csharp
public struct VirtualState     // SlabAllocator<VirtualState>, referenced by LayoutAux.Virtual
{
    public Orientation Orientation;        // Vertical | Horizontal
    public int    ItemCount;               // total logical items (10k..100k)
    public bool   MeasureItems;            // false = uniform fast path; forced true if GroupHeaderTypeId != 0
    public float  EstimatedItemExtent;     // uniform extent OR the seed estimate for variable
    public float  ContentSize;             // PUBLISHED to the viewport (full scroll extent)
    public float  OverscanPx;              // realize-window margin
    public ExtentTableRef Extents;         // -> ExtentTable (Fenwick/BIT) when MeasureItems (§8.4)
    public int    AnchorIndex;             // topmost-visible logical index (scroll-anchoring, §8.5)
    public StringId AnchorKey;             // the anchor item's ItemKey (stable across regroup)
    public float  AnchorViewportDelta;     // px from viewport top to the anchor item's top
    public ushort GroupHeaderTypeId;       // 0 = flat; non-0 = grouped (forces MeasureItems, §8.6)
    public int    FirstRealized, LastRealized;  // [first,last) realized window (computed P2/P6)
}
```

### 8.3 Uniform fast path (`MeasureItems == false`)

When all items are the same extent (WaveeMusic **pins a fixed row height per density at the app layer** to keep this
valid — `app-requirements` §3.2):

- `ContentSize = ItemCount × EstimatedItemExtent` (computed instantly, O(1)).
- `VisibleRange`: `first = floor((ScrollOffset - Overscan) / extent)`, `last = ceil((ScrollOffset + ViewportExtent +
  Overscan) / extent)`, clamped to `[0, ItemCount)`. **O(1) arithmetic, no per-item work.**
- Each realized row's LOCAL `Bounds.Y = index × extent`. Within-window scroll that doesn't cross an item boundary is a
  **transform-only frame** (§6); crossing a boundary re-renders only the **delta** rows.

**Critical feedback-loop break (binding, `app-requirements` §3.2):** `UseImage` returns `NaturalSize`, but the dev MUST
bind row height to the **fixed decode bucket**, never the late-arriving natural size — otherwise
decode→measure→relayout loops forever. The uniform path's validity depends on this.

### 8.4 Variable path (`MeasureItems == true`) — the extent table (Fenwick/BIT)

For surfaces that genuinely vary (group headers, hero-as-first-row, variable-height image-bearing rows):

- A **slab-backed** (NOT arena — the folded arena/slab contradiction fix) per-item `ExtentTable` with a **Fenwick tree
  / Binary Indexed Tree** for **O(log n) offset↔index** both directions:
  - `OffsetOf(index)` = prefix-sum of extents `[0,index)` — for positioning a realized row.
  - `IndexAt(offset)` = binary search over prefix sums — for computing `first` from `ScrollOffset`.
- **Estimated-then-corrected extents:** unmeasured items use `EstimatedItemExtent`; when an item is realized and
  measured (its real `Bounds.Height` is known after layout), the BIT is updated (`Update(index, realExtent -
  estimate)`, O(log n)) and `ContentSize` is corrected.
- The BIT is slab-backed (`SlabAllocator<float>` segment per virtual node, sized to `ItemCount`); 100k items × 4B =
  400KB, well within budget, and it persists across frames (only deltas update).

### 8.5 Scroll-anchoring (binding)

When an extent correction lands for an item **above** the viewport (its measured extent differed from the estimate),
naively recomputing positions would **jump the viewport**. Scroll-anchoring prevents this:

1. Each frame, record the **topmost visible item's `ItemKey`** (`AnchorKey`) + its pixel delta from the viewport top
   (`AnchorViewportDelta`).
2. After any extent correction, recompute the anchor item's new `OffsetOf(AnchorIndex)`; set `ScrollOffset =
   newAnchorOffset - AnchorViewportDelta` so the anchored item stays visually fixed. The correction absorbs entirely
   into content above the viewport; the user sees no jump.

This is the standard variable-height virtualization correctness mechanism and it is **owned here** (the layout
participant computes the anchor; Input applies the resulting `ScrollOffset` clamp).

### 8.6 Grouping (the virtualizer does NOT group)

Headers are logical items in the **same flat index space**; the **app supplies a flat `index→(kind,payload)`
projection** (`app-requirements` §3.2). The virtualizer never groups. Consequences for layout:

- `MeasureItems = true` is **forced whenever `GroupHeaderTypeId != 0`** (headers are variable-height).
- A **regroup** is a `deps` change (hook side) that **invalidates the extent table**, resets the anchor to a stable
  post-regroup `AnchorKey`, and re-seeds `ContentSize`. Layout treats this as a full virtual re-extent (rebuild the
  BIT from estimates, re-anchor).
- **Sticky group-header pin** = a **transform write in phase 7** (`min(ScrollOffset, nextHeaderOrigin -
  headerExtent)`), excluded from clean-span memcpy via `NodeFlags.StickyPinned (1<<14)`. The header's own layout is
  unchanged; only its `LocalTransform` is pinned. (Layout-free, §6.)

### 8.7 Two-pass virtual layout (phase 6)

`VirtualLayout.Run` is a two-pass arrange (the §8 layout half of the 13-phase placement in `app-requirements` §3.2):

```
P6 VirtualLayout (when LayoutKind in {VirtualList, VirtualGrid}):
  pass 1 (extents):  uniform → ContentSize = n×extent (O(1))
                     variable → ensure BIT seeded; ContentSize = BIT.Total (O(1) read)
                     publish ContentSize on the viewport node
  pass 2 (realized rows): for index in [FirstRealized, LastRealized):
                     row.Bounds.Y = uniform ? index×extent : BIT.OffsetOf(index)   (content-box local)
                     FlexLayout.Run(row, viewportInnerWidth, scale)  // or Grid for VirtualGrid cross-axis
                     if (MeasureItems) BIT.Update(index, row.measuredExtent - prevExtent)  // correct
  (the viewport is a layout boundary §4.3 — realize/derealize never escapes it)
```

### 8.8 13-phase placement (the virtualization timeline, for reference)

| Phase | Action |
|---|---|
| **P2** | scroll updates `ScrollOffset`; sets `VirtualRangeDirty (1<<13)` **only if** the offset crossed an item boundary |
| **P4** | if `VirtualRangeDirty`/datasource-version changed → `RenderItem` the **new** window (hook side; bounded Gen0 + one `ArrayPool<Element>.Shared` chunk — NOT the cap-32 `ObjectPool`) |
| **P5** | keyed-LIS diff → enter `CreateNode`, exit `FreeNode` (slab free-list) |
| **P6** | `VirtualLayout` two-pass (§8.7) — **this subsystem** |
| **P6.5** | `UseVisibleRange`/`ScrollToIndex` read valid `Bounds`/`ContentSize` (`ScrollToIndex` computes target offset from `BIT.OffsetOf` and seeds an Animation track) |
| **P7** | `-ScrollOffset` → viewport `LocalTransform` (transform-only, §6); sticky-header pin transform |
| **P8** | window subtree records; clean spans memcpy |

`NodeFlags.VirtualRangeDirty (1<<13)` and `StickyPinned (1<<14)` are contiguous free bits — explicitly **NOT**
`NodeFlags.Realized (1<<12)` (which means "cached text/path realization," not "row in the realized window" — folded
naming fix, `app-requirements` §3.2).

### 8.9 The honest alloc claim

Within-window scroll = transform-only (phase 7, ~0). Crossing a boundary re-renders only the **delta** rows: phases
6–13 paint machinery is **0-alloc**; **phase 4 realize-delta allocates bounded Gen0** (`Element` records + one managed
`Element[]` window chunk from `ArrayPool<Element>.Shared`, sized to window+overscan, reused). Survivors keep their
`Element` via component memoization (only ENTER rows re-render). The layout half (extent BIT update, range arithmetic,
two-pass arrange) is **0-alloc** (slab BIT + arena scratch). This matches `hardened-v1-plan.md` §3 ledger: "O(Δ) Gen0,
not zero."

---

## 9. THE TEXT INTRINSIC-MEASURE SEAM — `MeasureKind` dispatch into `ITextShaper`

### 9.1 The central dispatcher (no delegate-per-node)

`YogaMeasureFunc` delegate-per-node → a central `MeasureKind` dispatcher keyed by `VisualKind` (`architecture-spec.md`
§5.5). A leaf node with `ChildCount==0` and a measurable `VisualKind` dispatches:

```csharp
[MethodImpl(AggressiveInlining)]
static YogaSize Measure(in LayoutNode n, float availW, MeasureMode wMode,
                                          float availH, MeasureMode hMode)
{
    // ring-cache probe FIRST (§3.2 step 1) — most measures hit the 8-entry ring
    if (n.Cache.TryGetRing(availW, wMode, availH, hMode, out var cached)) return cached;
    var size = (MeasureKind)n.Style.Packed.MeasureKind switch
    {
        MeasureKind.Text   => MeasureText(in n, availW, wMode, availH, hMode),  // §9.2 — the ONLY seam call
        MeasureKind.Image  => MeasureImageBucket(in n),                          // fixed decode bucket (NOT natural)
        MeasureKind.Custom => MeasureCustom(in n, availW, wMode, availH, hMode), // §9.3
        _                  => default,                                            // pure style-sized leaf
    };
    n.Cache.PushRing(availW, wMode, availH, hMode, size);   // populate the ring
    return size;
}
```

The Yoga leaf-sizing seam contract (text.md §33): `YogaSize Measure(node, availW, wMode, availH, hMode)` with modes
`{Undefined, Exactly, AtMost}`, 8-entry `CachedMeasurement` ring. Owned here; the text case calls out.

### 9.2 `MeasureText` — the one external call (folds the BLOCKER)

```csharp
static YogaSize MeasureText(in LayoutNode n, float availW, MeasureMode wMode, float availH, MeasureMode hMode)
{
    var slot = LayoutNodeUserData.Resolve(n.Slot);   // generational TextLayoutHandle (NOT a captured closure)
    var c    = new MeasureConstraints(availW, wMode, availH, hMode);
    TextMeasure m = s_textLayoutEngine.Measure(slot.Request, in c);   // ITextLayoutEngine — text.md §3.x
    return new YogaSize(m.Width, m.Height);   // baseline available via m for AlignBaseline
}
```

**Binding mechanics (folds the BLOCKER, `architecture-spec.md` §5.3 + text.md §33–36):**

- **One `static readonly YogaMeasureFunc s_textMeasure = MeasureTextStatic`** set once at node creation —
  **never** a captured closure. `MeasureTextStatic` recovers the `TextLayoutSlot` via a **generational
  `TextLayoutHandle` in the `LayoutNode` user-data slot** (the layout engine grants `LayoutNode` a handle user-data
  slot — `architecture-spec.md` §5.3 amendment, ratified here).
- **`MaxWidth` is quantized UP to an integer device-pixel grid BEFORE it enters Yoga** (text.md §665) so the 8-entry
  ring sees **stable keys** — drag-resize re-wraps (O(glyphs)) but never re-shapes (one SHAPE per content; re-WRAP on
  constraint change). The quantization happens when the reconciler writes `LayoutInput.MaxWidth` (the value the ring
  keys on is already grid-aligned).
- **Zero-alloc:** `ITextLayoutEngine.Measure` is pure + cached (L1 shaped-run constraint-free, L2 wrap constraint-
  bearing) and fills caller-owned spans; the measure returns a 32-ish-byte `TextMeasure` POD by value. No `string`, no
  allocation crosses the seam.
- The text run reaching layout is a `TextLayoutRequest` carrying the `char[]` reference at the Element edge (the one
  legitimate edge GC ref, text.md §696) — layout holds only the generational handle.

### 9.3 Custom measure callbacks (folded)

Custom measure (a dev-supplied `MeasureFunc`) is a **managed delegate** ⇒ stored in an `ObjectPool`/delegate-array
indexed by `PayloadRef` (NOT `SlabAllocator<unmanaged>` — a delegate is managed, `architecture-spec.md` §5.5).
Invocation is **reentrancy-guarded**: no tree mutation during measure; only the `(availW, wMode, availH, hMode)` struct
goes in and a size struct comes out. A custom measure that mutates the tree is a `[Conditional]` assert failure.

---

## 10. ABSOLUTE / OVERLAY POSITIONING — the containing-block model (folds the MAJOR)

The ported `LayoutAbsoluteDescendants` deep recursion (a **real ported routine, not a flattened loop**) with
accumulated left/top offsets, driven by a containing-block predicate:

```csharp
[MethodImpl(AggressiveInlining)]
static bool FormsContainingBlock(in LayoutInput s) =>
       s.Packed.PositionType != PositionType.Static
    || s.Packed.AlwaysFormsContainingBlock      // transform/filter set (a transformed node clips abs descendants)
    ;
```

- Step 10 of `CalculateLayoutImpl` (§3.2) walks absolute-positioned descendants relative to their nearest
  **containing block** (the nearest ancestor where `FormsContainingBlock` is true, else the layout root).
- The recursion accumulates the left/top offset down to the containing block so an absolutely-positioned node deep in
  the tree resolves its `Position` insets (L/T/R/B from `LayoutAux.Position`) against the right ancestor's content box.
- **Overlay positioning** (popups, flyouts, tooltips, the focus-visual overlay, drag-drop feedback) is absolute
  positioning whose containing block is the **layout root** (or an explicit overlay-root node) — it does not
  participate in flow, does not affect siblings' layout, and is z-ordered above by paint order. Overlays are
  re-measured/arranged only when their own content or anchor changes (a layout boundary at the overlay root keeps page
  changes from re-arranging the overlay and vice-versa, §4.3).
- `Affine2D` is 2×3 (2.5D/perspective explicitly out of scope for v1, `architecture-spec.md` §5.4) — a transform on a
  containing block affects abs-descendant positioning but layout stays affine.

---

## 11. THREAD, PHASE PLACEMENT, ASSEMBLY, ZERO-ALLOC

### 11.1 Thread + phase placement (canonical 13-phase, `hardened-v1-plan.md` §2.2)

Layout is **UI-thread**, **phase 6** (`6.5` for `UseLayoutEffect`/`ScrollToIndex`/`UseVisibleRange` reads that need
valid `Bounds`). The phase→thread map this subsystem touches:

```
... 5 reconcile (UI) → 6 LAYOUT (UI) → 6.5 layout-effects (UI, reads Bounds) → 7 animation (UI) →
PUBLISH 13a (UI: SnapshotColumns value-copy) → 8 record (RENDER) → ...
```

- **Reads:** `LayoutInput[]`/`LayoutAux` (set by phase-5 `WriteLayout`), the dirty worklist (phase 5), `ScrollOffset`/
  `ContentSize` (Input, last frame), `float scale`, the `ITextLayoutEngine` seam.
- **Writes:** `Bounds[]` (LOCAL), `ContentSize` (viewport), `LayoutCacheEntry` (ring/`AbsLeft`/`AbsTop`),
  `VirtualState.{ContentSize,Anchor*,First/LastRealized}`, the extent BIT.
- **The seam:** `Bounds[]` is **value-copied into `SnapshotColumns` at PUBLISH (13a)** by the UI thread
  (`threading-render-seam.md` §3 — `BoundsLocal` is in the copied set). The render thread never reads layout's live
  columns. Phase 6.5 layout effects + phase-2 hit-test read the **UI-owned committed `Bounds`** (the
  `last-published-consistent` double buffer, `threading-render-seam.md` §1) — never the in-flight snapshot.
- **`WorldTransform` consequence (budget, `hardened-v1-plan.md` §2.3):** a scroll/camera transform change dirties
  `WorldTransform` for the **whole descendant set** while their DrawList spans stay clean (the `TransformDirty` fast
  path). So the PUBLISH copy is **up-to-full-tree `WorldTransform` (NodeCount × 24B) during any scroll/transform
  animation** — sub-ms at 5k nodes but per-frame during a scroll. This is the cost of layout-free scrolling and it is
  budgeted, not free.

### 11.2 Single-thread-first (build order)

Per `hardened-v1-plan.md` §6 step 1: layout ships **single-thread-correct first**, behind the snapshot seam *shape*
(UI thread both produces and consumes, quarantine=0). Incremental layout, the boundary firewall, the two-rule graph,
the geometry/ring cache, incremental snap, and the virtual extent table all land here with all CI gates green. The
render-thread seam (step 4) moves only record/batch/submit/present off-thread — **layout never moves off the UI
thread** (it mutates `SceneStore`, which is UI-thread-confined by `hardened-v1-plan.md` §2.1's single-writer rule).

### 11.3 Assembly

`FluentGpu.Layout` — depends on `Scene` (`LayoutInput`/`Bounds`/`LayoutCacheEntry`/`VirtualState` columns + the SoA
spine + `ISceneBackend` consumption surface), `Text` (iface — `ITextLayoutEngine`/`ITextShaper`), `Foundation`
(`Handle`/`SlabAllocator`/`ChunkedArena`/`Affine2D`/`RectF`/`LengthValue`/`Edges4`/`Ring8`/`StringId`). **Zero**
Windows/GPU/`ComPtr` dependency — referenced by `Render` (which reads `Bounds`) and `Reconciler` (which marks dirty +
calls `WriteLayout`). The acyclic-DAG invariant (`architecture-spec.md` §3): `Render → Layout`, `Reconciler → Layout`;
`Layout` depends only inward.

### 11.4 Zero-alloc story

Per-frame layout managed allocations = **0** (`architecture-spec.md` §5.5):

- All scratch is arena-backed (`LayoutScratch` child lists + `FlexLine`s; the non-relocating bump region, §3.5).
- All stable state is slab columns (`LayoutInput`/`LayoutAux`/`LayoutCacheEntry`/`VirtualState`/`GridDefinition`/
  `TrackDef`/extent BIT).
- `[InlineArray]` `Ring8`/`Edges4` are stack/inline (`dotnet10` §D); `LayoutNode`/`FlexLine`/`LayoutContext` are
  `ref struct` (no box; never reach a member through an interface type — boxing hard-error, `dotnet10` §D).
- The text measure seam is pure + cached + span-filled (§9). Custom measure delegates are pooled (edge alloc only).
- The **only** bounded Gen0 in the virtualization path is **phase 4** realize-delta (`Element` records + an
  `ArrayPool<Element>.Shared` window chunk) — that is the reconciler/hook side (§8.9), not layout's phase 6.
- Idle frames are **O(0)** (empty dirty worklist short-circuits, §4.4).

---

## 12. FAILURE MODES

| Mode | Cause | Behavior / mitigation |
|---|---|---|
| **Layout cycle** | a reconcile bug links a node into its own subtree | reconciler validates topology cycle-free before phase 6 (`architecture-spec.md` §5.5); `MaxLayoutDepth=256` cap throws a deterministic `LayoutDepthExceeded` (DEBUG) / clamps (RELEASE) rather than stack-overflow |
| **Scratch relocation mid-descent** | a defensive `Array.Resize` of `LayoutScratch` during a `Run` | structurally forbidden; DEBUG base-pointer assert (`scratch.BasePtr == entryPtr`); `LayoutScratch` is pre-sized from live-node count or a segmented `ChunkedArena` (addresses stable, §3.5) |
| **Pixel-snap drift** | accumulating fractional rounding across frames | seeded from the **committed** parent snapped origin each frame (§5), not from last frame's snapped child — drift cannot accumulate; golden gate at 1.25/1.5/1.75 |
| **Decode→measure→relayout loop** | dev binds row height to `UseImage` natural size instead of the fixed bucket | documented binding contract (§8.3); the uniform virtual path is invalid if violated — a DEBUG lint flags a `LayoutSelfDirty` re-mark triggered by an image natural-size arrival |
| **Viewport jump on extent correction** | a variable-height item above the viewport measured larger than estimate | scroll-anchoring on the topmost `ItemKey` absorbs the correction above the viewport (§8.5) |
| **Stale measurement cache** | style changed but generation not bumped | every `WriteLayout` bumps the tree generation (§4.5); `ConfigVersion` bump on scale; epoch sweep on `uint` wrap; second oracle = the Yoga golden parity test |
| **Ring key thrash on resize** | fractional `MaxWidth` produces a fresh ring key every pixel | `MaxWidth` quantized UP to the device-px grid before entering Yoga (§9.2) — drag-resize is O(glyphs) re-wrap, bounded ring keys |
| **Worklist overflow** | a pathological frame dirties more nodes than the worklist cap | fall back to the full-rebuild path (`Vector256` `FlagsSpan` scan + `SubtreeLayout` bottom-up recompute, §4.4) — correct, just O(capacity) for that frame |
| **Off-thread page arrival** | a worker `Post`s a page mid-frame | marks dirty + schedules **frame N+1** (no synchronous re-loop, `architecture-spec.md` §5.4); on apply, re-derive the current window + `PaintDirty` only still-realized rows; +1-frame minimum page→pixels (§8.8) |
| **Custom measure mutates tree** | a dev `MeasureFunc` calls back into the scene | reentrancy-guarded; `[Conditional]` assert; documented contract (struct in, struct out, §9.3) |

---

## 13. macOS BOUNDARY

Layout is **fully portable** — the cleanest seam in the system (`architecture-spec.md` §5.5). The macOS port ships
`FluentGpu.Layout` **unchanged**. The boundary is exactly two things, both already abstracted:

1. **The text-measure seam** (`ITextLayoutEngine`/`ITextShaper`, §9) — Windows binds it to DWrite
   (`Text.DirectWrite`); macOS binds the **same interface** to CoreText (`Text.CoreText`:
   `CTFontGetGlyphsForCharacters`/`CTLine`/`CTRun` for shaping + line metrics). Layout calls `Measure` and treats the
   returned `TextMeasure` as opaque — it never knows which leaf produced it. (Line-breaking parity is a Text-leaf
   concern, deferred to the CoreText milestone per text.md §671.)
2. **DPI as `float scale`** — Windows derives it from `GetDpiForWindow`/PerMonitorV2 at the pump boundary; macOS from
   the backing-scale-factor. Layout receives a plain `float`; the `ConfigVersion` mechanism (§5) is platform-agnostic.

No `HWND`/`HRESULT`/`ComPtr`/D3D/Metal crosses into `FluentGpu.Layout`. Scroll-as-transform, the extent BIT,
anchoring, Grid, the flex algorithm, pixel-snap, and the containing-block model are all pure C# over SoA columns + the
two seam values. The macOS swap touches the Text leaf and the PAL scale source — **zero layout code**.

---

## 14. CHANGED vs the original synthesis

These are the deltas from the original `architecture-spec.md` §5.5 / `app-requirements` §3.2 prose, folding the
hardenings and resolving the ownership splits the task assigned to this doc:

1. **Incremental layout promoted from "Yoga generation cache + dirty scope" to an explicit boundary firewall + two-rule
   invalidation graph + dirty-node worklist.** The original named "layout cost ∝ dirty subtree" and the
   intrinsic-vs-participation taxonomy; this doc makes the firewall (`IsLayoutBoundary`), the up/down rules, and the
   O(0)-idle worklist the **mechanism** (folds `hardened-v1-plan.md` §4.4 "O(change) layout"), and pins
   `SubtreeDirty` = traversal scope only (never a layout-skip input — the keystone correctness fix from
   `reconciler-hooks.md` §6.3, applied to layout's skip decision).

2. **Virtualization's layout-participant half is fully specified here** (extent table = slab-backed Fenwick/BIT with
   O(log n) offset↔index, scroll-anchoring on the topmost `ItemKey`, two-pass `VirtualLayout`, the `VirtualList`/
   `VirtualGrid` `LayoutKind`, the viewport-is-a-layout-boundary rule). The original `app-requirements` §3.2 named the
   slab BIT + anchoring + the forced `MeasureItems` rule but located the design under "virtualization"; this doc owns
   the layout contract and **references** `virtualization` (not-yet-written) for the hook/recycle half. The
   arena-vs-slab contradiction is resolved to **slab** for the extent table (matches the folded fix).

3. **`LayoutScratch` non-relocating invariant tied to `ChunkedArena`.** The original spec said "pre-sized byte[]
   arena, grown only between passes"; `hardened-v1-plan.md` §7 relaxed Foundation to `ChunkedArena` (add-chunk
   mid-pass, addresses stable). This doc states the **interaction**: `LayoutScratch` may be a segmented `ChunkedArena`
   **iff** add-chunk preserves already-handed-out span addresses — the non-relocating guarantee is the contract the
   arena must satisfy, with a DEBUG base-pointer assert.

4. **Pixel-snap made incremental + seeded from the committed parent origin** (the original named it as a folded MAJOR;
   this doc gives the seed rule that prevents cross-frame drift and ties `ConfigVersion` to the scale-change full
   re-snap).

5. **The `Bounds`/`WorldTransform` PUBLISH budget stated honestly.** The original animation/scroll prose said
   "transform-only, ~0"; folding `threading-render-seam.md` §2.3, this doc records that scroll dirties `WorldTransform`
   for the **whole descendant set** ⇒ the PUBLISH copy is up-to-full-tree × 24B per scroll frame (sub-ms at 5k nodes,
   but per-frame, not "0.1ms damaged-only"). Layout-free relayout, but **not** transform-snapshot-free.

6. **`LayoutInput` field set + `LayoutAux` hot/cold split pinned to 96B**, with `Unsafe.SizeOf` assert, and
   `LayoutCacheEntry` made field-complete vs `LayoutResults` (**`RawDim` included** — the seventh `_rawDimensions`
   array the original omission would have dropped).

7. **`default(LengthValue) == Undefined`** made authoritative (unit-driven, not NaN-on-Value) and the `/ 100.0f`
   percent parity restated as binding — both from the folded numeric-parity amendment.

8. **No contradictions introduced.** Where the older `foundations.md`/`architecture-spec.md` use 12-phase numbering or
   "single UI/render thread," this doc uses the canonical 13-phase + UI-thread-phase-6 placement from
   `hardened-v1-plan.md` §2 (the README supersession order). The scroll-ownership split (Input owns offset, Layout
   writes `ContentSize`, Render reads clip), the `MeasureFunc` static-readonly + handle-user-data mechanism, and the
   `NodeFlags.VirtualRangeDirty/StickyPinned` bit assignments are all carried verbatim from the authoritative sources.
