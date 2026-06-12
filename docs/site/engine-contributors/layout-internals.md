# Layout internals

[← Contributing to the engine](./index.md)

Layout is **phase 6** of the frame loop: between reconcile (which patches the retained scene) and record (which walks it into a DrawList), the layout engine fills every node's `Bounds` — a node-**local** rect in its parent's coordinate space. It is the cleanest seam in the system: `FluentGpu.Layout` references only `Foundation` + `Scene` + the text-measure interface, with **zero** Windows/GPU dependency, so the macOS port is a text-leaf swap with no layout changes.

This page is the as-built tour for someone **changing** the engine. It assumes you have read [rendering & performance](../../guide/rendering-and-performance.md) (the frame pipeline, the three dirty axes, the boundary firewall from above) and want to know how the two descents, the scoped-relayout walk, and the virtualization participant are wired — and exactly which file to open. The architecture **authority** is [`design/subsystems/layout.md`](../../../design/subsystems/layout.md) (and [`virtualization.md`](../../../design/subsystems/virtualization.md)); this page is the working view over the live source and **flags where the live core is narrower than the design of record**. Where they disagree, the source wins.

> **Where it all lives.** `src/FluentGpu.Layout/FlexLayout.cs` (the two descents + grid + scroll + the virtualization arrange) · `src/FluentGpu.Layout/LayoutInvalidator.cs` (the scoped-relayout boundary walk) · `src/FluentGpu.Scene/VirtualLayout.cs` (the `IVirtualLayout` seam + built-in layouts) · `src/FluentGpu.Hosting/AppHost.cs` (the phase-6 call site). Verify in `src/FluentGpu.VerticalSlice/Program.cs`.

---

## Incremental layout (`FlexLayout.Run` full vs `RunSubtree` scoped)

`FlexLayout` ([`FlexLayout.cs`](../../../src/FluentGpu.Layout/FlexLayout.cs)) exposes exactly three public entry points, and the whole incremental story is the choice between them:

```csharp
public void Run(NodeHandle root);                 // content-size the root (golden flexbox checks)
public void Run(NodeHandle root, Size2 window);   // fill the window (the top-level app behavior)
public void RunSubtree(NodeHandle node);          // re-solve ONE subtree against its already-placed bounds
```

- **`Run(root, window)`** is the full-tree solve: a `Measure` descent then an `Arrange` descent from the root. It runs only on **first frame, window resize, DPI change, or a root structural change** — the cases where the available space at the root itself changed.
- **`RunSubtree(node)`** re-solves *only* the subtree rooted at `node`, against the bounds the parent already gave it:

```csharp
public void RunSubtree(NodeHandle node)
{
    if (node.IsNull) return;
    ref LayoutInput li = ref _scene.Layout(node);
    ref RectF b = ref _scene.Bounds(node);
    float w = float.IsNaN(li.Width) ? b.W : li.Width;
    float h = float.IsNaN(li.Height) ? b.H : li.Height;
    Measure(node, w);
    Arrange(node, b.X, b.Y, w, h);
}
```

The key line is that it arranges at `(b.X, b.Y)` — the node's existing origin — so **the parent is never disturbed**. The node keeps its slot; only its descendants are re-measured and re-placed. This is the per-frame-affordable relayout that lets a deep `setState` reflow its own card without re-laying-out the page. (It also accepts a `LayoutInput.Width` override: a `SizeMode.Relayout` animation writes the interpolated width there each tick, and `RunSubtree` honors it over the bounds — that is how smooth size animations reflow their content.)

The phase-6 driver in `AppHost.RunFrame` ([`AppHost.cs`](../../../src/FluentGpu.Hosting/AppHost.cs) ~L427) picks the entry point:

```csharp
bool layoutNeeded = _needFullLayout || reconciled || _scene.AnyLayoutDirty;
if (layoutNeeded && !_scene.Root.IsNull)
{
    if (_needFullLayout || !_everLaidOut)
        _layout.Run(_scene.Root, layoutSize);   // 6 full layout: first frame / resize / DPI / root change
    else
        _invalidator.RunDirty(layoutSize);       // 6 scoped relayout: only dirty subtrees, firewalled at boundaries
    _scene.ClearLayoutDirty();
    // …D1 realize-after-layout loop (virtualization, below)…
}
```

An idle frame, or a frame whose only work was a bound transform/opacity write, sets nothing `LayoutDirty` and runs **zero** layout.

---

## The flex + grid model (ported Yoga, measure / arrange)

The flex algorithm is the **ported Reactor/Yoga** flexbox, kept numerically faithful but rewritten onto the SoA scene columns instead of a `YogaNode` class graph. The live implementation is two private descents over the scene store:

1. **`Measure(node, availW)`** — bottom-up. Fills each node's `Bounds.W/H` with its base (hypothetical) border-box size: resolve `Width`/`Height`/min/max, measure text leaves, or sum children along the main axis and take the max on the cross axis. Direction `0` = row (main axis = X), `1` = column (main axis = Y).
2. **`Arrange(node, x, y, finalW, finalH)`** — top-down. Distributes free space by `FlexGrow`/`FlexShrink`, positions along the main axis by `Justify`, aligns the cross axis by `AlignItems`/`AlignSelf` (including `Stretch`), applies margins and gap, and writes the final `Bounds`.

The dispatch happens at the top of both `Measure` and `Arrange`: a node with a scroll side-table is a viewport (`MeasureViewport`/`ArrangeViewport`), one with a grid side-table is a grid (`MeasureGrid`/`ArrangeGrid`), a `NodeFlags.ZStack` node overlays its children (`MeasureZStack`/`ArrangeZStack`), and `li.Wrap` routes to the multi-line `MeasureWrap`/`ArrangeWrap`. Everything else is plain flex.

The knobs are the `Foundation` enums ([`LayoutTypes.cs`](../../../src/FluentGpu.Foundation/LayoutTypes.cs)):

```csharp
public enum FlexJustify : byte { Start = 0, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }
public enum FlexAlign   : byte { Auto = 0, Start, Center, End, Stretch }   // Auto on a child = inherit container's AlignItems
```

`Justify` is realized by `Distribute(justify, leftover, n) → (lead, between)` (the leading offset + inter-item spacing), and `AlignSelf == Auto` inherits the container's `AlignItems` at placement time. A subtlety worth knowing before you touch `Arrange`: a row with a definite width pre-computes a `growAvail` (the content width minus the fixed siblings and gaps) and measures grow children against *that*, not the whole row — otherwise a fixed pane plus grow content would wrap to the entire window. A column re-measures stretch children against the **final** cross width during arrange (a `NavigationView` content frame's width is only known after its fixed pane consumed its 320px), so wrapped text breaks at the real frame width.

**Grid is a distinct algorithm, not nested-flex faking.** `MeasureGrid`/`ArrangeGrid` resolve real tracks via `ResolveColumns`, sized by `TrackSize` ([`TrackSize.cs`](../../../src/FluentGpu.Foundation/TrackSize.cs)):

```csharp
public enum TrackKind : byte { Pixel = 0, Star = 1, Auto = 2 }
TrackSize.Px(80)    // fixed px/DIP
TrackSize.Star(2f)  // fractional — shares leftover space by weight
TrackSize.Auto      // sized to its widest cell
```

`true-tracks` means every cell in a column shares that column's resolved width, so the grid stays aligned across rows. `GridColCount` also handles the responsive auto-fill case (`MinColWidth > 0` packs as many equal `1fr` columns as fit, CSS `repeat(auto-fill, minmax(min, 1fr))`). The `ZStack` path (`Ui.ZStack`) overlays children at the origin and honors `AlignSelf` as the child's vertical placement — it is the shell shape behind the gallery (a fixed nav pane plus a grow content frame).

> **Adding a flex/grid feature** (a new `Justify` mode, a wrap rule, a track kind) is local to `FlexLayout.cs` plus the `Foundation` enum it reads. `AspectRatio`, percentage lengths (the design's `LengthValue`), absolute/overlay positioning, and `AlignContent` for multi-line cross alignment are in the **design of record** ([`layout.md` §3/§10](../../../design/subsystems/layout.md)) but **not yet in the live core** — the live `LayoutInput` carries plain `float Width/Height` with `NaN` = auto, not the unit-tagged `LengthValue`. Don't restate the design struct as if it ships; add the field, then add the check.

---

## The `MeasureFunc` / text-measure-cache bridge

A text leaf (`VisualKind.Text`) is the one place layout calls **out** of `FluentGpu.Layout`. In the live code that call is `_fonts.Measure(text, style, maxWidth)` through the `IFontSystem` seam — the engine's equivalent of Yoga's per-node `MeasureFunc`, but as a **central dispatch** (no delegate-per-node). It is gated by a per-node **text measure cache** so a scoped relayout skips re-shaping unchanged text:

```csharp
ref TextMeasureCache mc = ref _scene.MeasureCacheRef(node);
if (mc.Valid && mc.Text == paint.Text && mc.MaxW == maxW && mc.Style == li.TextStyle)
{
    w = mc.Size.Width; h = mc.Size.Height;          // cache hit — no reshape
}
else
{
    var m = _fonts.Measure(paint.Text, li.TextStyle, maxW);
    w = m.Size.Width; h = m.Size.Height;
    mc = new TextMeasureCache { Valid = true, Text = paint.Text, Style = li.TextStyle, MaxW = maxW,
        Size = new Size2(w, h), UnderlineY = m.UnderlineY, UnderlineThickness = m.UnderlineThickness, StrikeY = m.StrikeY };
}
```

The cache key is the pure tuple `(text, style, availWidth)`, so it is **self-invalidating**: change any of them and the next measure misses and reshapes. It is a genuine win on the real DirectWrite path (shaping is expensive) and neutral on the headless path (the headless font system measures by a deterministic metric). The cache row also retains the face's decoration metrics (underline/strike positions) so the recorder can place those bars at record time without re-touching the font seam.

`maxW` is `PositiveInfinity` for `TextWrap.NoWrap` and the definite content width otherwise — that is what tells DirectWrite where to break. The seam is the only thing the macOS port reimplements here; everything else is column reads/writes.

---

## Scoped relayout: the `LayoutDirty` worklist and `LayoutInvalidator` up-walk

`SceneStore` keeps a `LayoutDirty` worklist (the nodes a reconcile / width-height / text bind marked this frame). `LayoutInvalidator.RunDirty` ([`LayoutInvalidator.cs`](../../../src/FluentGpu.Layout/LayoutInvalidator.cs)) consumes it: for each dirty node it walks **up** to the nearest layout boundary, dedupes the resulting boundary roots, and re-solves each:

```csharp
public void RunDirty(Size2 window)
{
    var dirty = _scene.LayoutDirtyNodes;
    if (dirty.Count == 0) return;                      // O(0) idle

    _roots.Clear();
    for (int i = 0; i < dirty.Count; i++)
    {
        var n = dirty[i];
        if (!_scene.IsLive(n)) continue;
        var root = FindRelayoutRoot(n);                // up-walk to the firewall
        if (_scene.IsLive(root) && !_roots.Contains(root)) _roots.Add(root);
    }

    for (int i = 0; i < _roots.Count; i++)
    {
        var r = _roots[i];
        if (!_scene.IsLive(r)) continue;
        if (r == _scene.Root) _layout.Run(r, window);  // boundary is the root → solve against the window
        else _layout.RunSubtree(r);                    // else reflow against the node's own bounds
    }
}
```

Cost is **O(change)**, not O(tree): an idle worklist returns immediately, and a localized change re-solves only its boundary subtree. Running an ancestor root and a descendant root in the same pass is harmless (layout is idempotent), so the invalidator keeps it simple and only dedupes exact roots. If a dirty node has **no** bounded ancestor, the up-walk reaches the root and you fall back to a full `Run(root, window)` — still correct, just not scoped. (Note the live invalidator implements the **up-rule** plus the boundary firewall; the design's full two-rule graph with the explicit *down-rule* and Yoga's generation-counter measurement ring is in [`layout.md` §4](../../../design/subsystems/layout.md) and not yet a separate mechanism in the live core — the live down-side is the per-measure recursion plus the text cache.)

---

## The layout-boundary predicate (the firewall) — exact form

The firewall is one predicate. A **layout boundary** is a node whose own size cannot change because of a descendant, so the up-walk stops there and the parent is never disturbed. The live form ([`LayoutInvalidator.cs`](../../../src/FluentGpu.Layout/LayoutInvalidator.cs)):

```csharp
private static bool IsLayoutBoundary(in LayoutInput s, NodeFlags f)
    => !float.IsNaN(s.Width) && !float.IsNaN(s.Height)   // both dimensions explicit (not auto)
    && s.FlexGrow == 0f && s.FlexShrink == 0f            // doesn't stretch/shrink to its parent
    && (f & NodeFlags.ClipsToBounds) != 0;              // contains its own overflow
```

`FindRelayoutRoot` stops at the first of: the scene **root**, a **scroll/virtual viewport** (`_scene.HasScroll(cur)` — always a boundary, see below), or a node satisfying `IsLayoutBoundary`. So the practical rule an engine change must preserve: **a fixed-size, non-flexing, clipped container is a wall.** Give cards, panes, list rows, and fixed regions an explicit `Width`+`Height` and `ClipToBounds = true` and a change inside them stops at their boundary. This is the exact mechanism the "compositor bypass" rests on — without a boundary the up-walk reaches the root and the firewall is gone.

(The design of record adds two more disqualifiers — an `AspectRatio`-coupled node and a percentage-sized node are *not* boundaries — but neither aspect-ratio nor percentage lengths exist in the live `LayoutInput`, so the live predicate is the three clauses above. When those land, this predicate is the one place to extend.)

---

## The scroll-ownership split (Input owns offset, Layout writes ContentSize)

Scrolling is **layout-free**, and the ownership is a hard split you must not blur when touching either side:

- **Layout writes `ContentSize`.** `ArrangeViewport` arranges the scroll content at the **content-box origin** (`padL, padT` in the content's local space) and publishes the arranged extent onto the viewport's `ScrollState` (`sc.ContentW/ContentH`, `sc.ViewportW/ViewportH`). The viewport's own size is its box (explicit/flex), **independent of content** — a `ScrollView` takes the size its parent flex gives it and pushes overflow to the scroll system rather than growing the page to its full content height. (`MeasureViewport` has a `ContentSized` opt-in for popup list presenters that *do* auto-size to rows then clamp by `MaxHeight`.)
- **Input owns the offset.** The `-ScrollOffset` translation is the content node's `LocalTransform`, written by Input (wheel/drag) or Animation (fling) — **never by layout**. That write marks only `TransformDirty`/`PaintDirty`, so a within-content scroll that doesn't change the realized window is a **transform-only frame**: no render, no reconcile, no relayout, just a re-record (and the batcher re-applies the new world transform to the cached quads).

`ArrangeViewport` does re-clamp the offset to the (possibly changed) content on a resize/relayout and reflect it back into the content transform — so a wrapped reflow that shrinks the content doesn't leave the view scrolled past the end — but it reads the offset Input owns; it never authors it. The viewport carries `ClipsToBounds`, so it is both a clip and a layout boundary: realize/derealize of rows can't escape it. The split is pinned in [`layout.md` §6](../../../design/subsystems/layout.md): *Input owns `ScrollOffset`; Layout writes `ContentSize`; Render reads the clip.*

---

## RTL resolution at WriteLayout (logical → physical) — design of record, not yet live

The design of record specifies that **`FlowDirection` (RTL) is resolved logical→physical at the reconciler's `WriteLayout` boundary** — `ResolveLogical` mirrors flex direction, justify/align, and the start/end edges *before* the column is written, so the ported flex core stays purely physical and golden-parity-clean ([`layout.md` §10A](../../../design/subsystems/layout.md)). An engine contributor should know this is the intended seam: RTL is a **reconciler-side rewrite of `LayoutInput`**, not a branch inside `FlexLayout`.

**It is not implemented in the live tree.** There is no `FlowDirection`/`ResolveLogical` in `FluentGpu.Layout` or the reconciler today (the only `Rtl`/`FlowDirection` references in `src/` are in the text itemizer and the slider's directional fill). So `FlexLayout` is physical-only and LTR. If you implement RTL, do it at `WriteLayout` per the design — keep the firewall predicate and the two descents untouched — and add a parity check (a mirrored tree's `Bounds` against the LTR mirror). Until then, do not document RTL as a shipping capability.

---

## The virtualization layout participant (`IVirtualLayout` / `IMeasuredVirtualLayout`)

A virtualized collection is **one** retained viewport node with a window of keyed children; the layout half is a pluggable participant. The seam lives in `src/FluentGpu.Scene/VirtualLayout.cs`:

```csharp
public interface IVirtualLayout
{
    float  ContentExtent(int itemCount, float crossSize);                 // the published ContentSize
    void   Window(int itemCount, float crossSize, float viewportExtent,    // the [first,last) realize range
                  float scrollOffset, int overscan, out int first, out int last);
    RectF  ItemRect(int index, float crossSize);                          // item rect, LOCAL to the content origin
}
```

Every method is **pure, allocation-free arithmetic** returning structs / out-params, so a custom layout costs zero per-frame managed allocation — honoring the core contract. The engine calls it only on realize/arrange frames; a steady in-window scroll is transform-only and never touches it. Built-ins in the same file: `StackVirtualLayout` (uniform 1-D, O(1) windowing — the track-list shape), `GridVirtualLayout` / `HorizontalGridVirtualLayout` (uniform 2-D, virtualized by row/column), `LinedFlowLayout` (the ItemsView photo-wall), `SpanningGridVirtualLayout` (hero-as-first-row spanning).

Variable-extent layouts implement the widened seam:

```csharp
public interface IMeasuredVirtualLayout : IVirtualLayout
{
    void  SetMeasured(int index, float mainExtent, float crossSize);   // estimate-then-correct (O(log n))
    float OffsetOf(int index, float crossSize);                        // prefix sum over corrected extents
    int   IndexAt(float offset, float crossSize);                      // the scroll-anchor candidate
}
```

`ArrangeViewport` dispatches to the right arrange path by feature: `ArrangeVirtualMeasured` (an `IMeasuredVirtualLayout`), `ArrangeVirtualLayout` (a fixed-geometry `IVirtualLayout`), `ArrangeVirtualVariable` (the legacy `Layout == null` Fenwick path), or `ArrangePlainScroll` (no virtualization). The measured/variable paths run **estimate-then-correct with scroll-anchoring**: capture the topmost-visible item and its sub-item offset *before* corrections, measure each realized row, feed the real extent back through `SetMeasured` (O(log n)), then re-pin the offset so a correction to a row **above** the viewport doesn't jump the visible top:

```csharp
float pinned = layout.OffsetOf(anchorIndex, cross) + anchorWithin;   // re-pin after corrections
pinned = Math.Clamp(pinned, 0f, maxOff);
ref ScrollState scw = ref _scene.ScrollRef(node);
if (horizontal) scw.OffsetX = pinned; else scw.OffsetY = pinned;
```

Built-in measured layouts (also in `VirtualLayout.cs`): `MeasuredStackVirtualLayout` (the Fenwick variable list) and `GroupedListVirtualLayout` (group headers as a measured item *kind* plus a `StickyHeaderIndexAt` sticky-pin hook). These are **stateful** (they own an `ExtentTable`) — create one once and reuse it across renders (hoist in a `UseMemo`); the table self-rebuilds only on item-count change.

The realize **window** is `VirtualWindowing.NeedsRealize` plus the `D1` realize-after-layout loop in `AppHost.Paint`: `ArrangeViewport` computes the visible range against the viewport it just published and flags `NodeFlags.VirtualRangeDirty` if the realized window no longer covers it; the host re-realizes + re-runs scoped layout in the **same** frame (bounded to two passes) so the first presented frame shows real rows. The app-facing factories (`Virtual.List`, `Virtual.Grid`, `Virtual.Measured`, `Virtual.ListBound`, …) live in `src/FluentGpu.Controls/Virtual.cs` and just construct a `VirtualListEl` with one of these layouts — the reconciler diffs that element directly.

> **Where to change what.** A new *placement shape* (staggered wall, calendar) ⇒ implement `IVirtualLayout` (or `IMeasuredVirtualLayout` if extents vary) in `VirtualLayout.cs` and expose a `Virtual.*` factory in `Virtual.cs`. The *arrange wiring* (how the engine drives a layout) is `ArrangeViewport` and friends in `FlexLayout.cs`. The hook-level realize/recycle window is the reconciler. The policy split (uniform vs variable, what a regroup invalidates) is owned by [`virtualization.md`](../../../design/subsystems/virtualization.md); the **math** (this seam, the Fenwick offset↔index, anchoring) is owned by [`layout.md` §8](../../../design/subsystems/layout.md). For the app-author view of these factories, see [virtualized lists & ItemsView](../app-authors/virtualized-lists-and-itemsview.md).

---

## Verifying: `FlexChecks`, `WrapChecks`, `GridChecks`, `SizeModeChecks`, `ReflowChecks`

There is no build/test for layout other than the **headless harness** — `src/FluentGpu.VerticalSlice/Program.cs`. After any layout change:

```bash
dotnet build src/FluentGpu.VerticalSlice          # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice # expect: ALL CHECKS PASSED
```

The layout checks build a tree, run a frame headless (no GPU, no window), and assert on post-layout `Bounds` read back from `host.Scene` (`AbsoluteRect`/`Bounds`). The ones to extend when you touch layout, all in `Program.cs`:

| Check group | What it pins | Example assertion |
|---|---|---|
| `FlexChecks` | justify / grow / align / padding+gap | a 300-wide `SpaceBetween` row → children at `x = 0` and `260`; two `Grow=1` → `150`+`150`; `align-items: center` in a 100-tall row → `y = 40` |
| `WrapChecks` / `ConstrainedWrapChecks` | multi-line flow; grow children wrapping text to `availW − fixed siblings` | three 40-wide tiles in a 100-wide wrap row → third tile at `(0, 20)`; the gallery shell's wrapped caption measures to the content frame width |
| `GridChecks` / `AutoGridChecks` / `GridStretchChecks` | star tracks + row-major auto-flow; auto-fill count; stretch-width grid measuring real content height | star columns split width (`x = 0,110,220`, `w = 100`); a stretch-width 8-cell/4-col grid measures `H = 192` so the sibling below doesn't overlap |
| `SizeModeChecks` / `ReflowChecks` | `SizeMode.Relayout`/`Reflow` animations re-solving a boundary scope each tick; settle restores the declared input | a collapsing wrapper eases its height monotonically, the trailing sibling shift rides the interp, and settle restores the declared `NaN`(auto) `LayoutInput.Height` |
| `VirtualChecks` (checks 38–40a) | windowing + recycle at scale; transform-only in-window scroll | 10k×40px rows realize `≥10 && <40` children with `ContentH = N×40`; an in-window wheel is `!Rendered` with the content `LocalTransform.Dy` shifted; a fling keeps live nodes bounded (`< 90`, no leak) |

When you change reactivity-adjacent layout, also assert on `FrameStats`: a bound scroll/animation frame must be `Rendered == false` (compositor bypass), and a steady frame must have `HotPhaseAllocBytes == 0` (the near-zero-alloc contract — phases 6–13 allocate **no** managed memory; a boundary-crossing virtual realize is bounded Gen0 at the reconcile edge, *not* the paint half). Add a `Check("…", condition, detail)` for the exact behavior you changed and call it from `Main`. The harness asserts the **logic and the recorded DrawList**, not on-screen D3D12 pixels — that is a separate manual "needs-pixels" pass on the real Windows path.

---

## Canon link: layout subsystem (design-of-record)

- [`design/subsystems/layout.md`](../../../design/subsystems/layout.md) — the architecture authority for this subsystem: the ported-Yoga 11-step body, the `LayoutInput`/`LayoutAux` hot/cold split + `LayoutCacheEntry` measurement ring, the two-rule invalidation graph, incremental pixel-snap, scroll-as-transform, grid true-tracks, the virtualization participant math, the `MeasureKind` text seam, absolute/overlay positioning, and the RTL `WriteLayout` resolution. Several of these (percentage `LengthValue`, the generation-counter ring, the explicit down-rule, RTL, aspect-ratio) are **design-of-record ahead of the live core** — this page flags those gaps inline; treat the live source as the truth for what ships.
- [`design/subsystems/virtualization.md`](../../../design/subsystems/virtualization.md) — the virtualization runtime authority (the hook trio, recycle-correctness invariants, uniform-vs-variable policy, grouping, the +1-frame incremental-load contract). It **references** `layout.md` for the extent-table mechanics this page describes.
- [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) — the precedence authority when two docs disagree on a cross-cutting contract.
- [`subsystems/README.md`](../../../design/subsystems/README.md) — the contract-ownership map (which doc owns each artifact) and the implementer reading order.

**Next:** [rendering & performance](../../guide/rendering-and-performance.md) for the full frame pipeline · the [file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what) for routing any change to its one owning file.
