# Virtualized, size-reactive PagedShelf + viewport-aware virtual-layout seam

- **Date:** 2026-06-18
- **Status:** Approved design — ready for implementation plan
- **Scope:** `FluentGpu.Engine` (layout seam) → `FluentGpu.Controls` (primitive + control) → `Wavee` (app migration) + canon docs
- **Author:** design captured via brainstorming with Christos

## 1. Context & problem

Wavee's Home page (`app/Wavee/Features/Home/HomePage.cs`) hand-rolls a responsive horizontal card rail
(`app/Wavee/Components/PagedShelf.cs`) and feeds it the available width through a page-level signal:

```csharp
readonly Signal<float> _innerW = new(0f);          // measured in OnBoundsChanged, ABOVE the vertical ScrollView
```

HomePage is acting as a **layout broker** for its shelves — measuring width and threading it down as a constructor
signal. This is app-architecture debt: responsive card sizing belongs *inside* a reusable control, not on the page.

Two concrete bugs were also found and fixed in the app-local shelf during triage (these become engine-level golden
checks once the layout moves into the engine — see §7):

1. **Card ballooning** — `Metrics` left `cardW` uncapped when there were too few items to add another column, so a
   wide viewport stretched a handful of cards far past `maxCardW` (3 items @ 1400px → ~458px cards/circles).
2. **Stale page index** — the stored page index wasn't clamped on widen, so widen-then-narrow snapped to a stale page.

The original reason the broker measures *above* the `ScrollView` (a vertical viewport once let content keep its natural
width) is **obsolete**: `FlexLayout.cs:395-398` now makes a vertical viewport with a finite offered width adopt that
width (CSS `overflow-y`). So a control can safely self-measure *inside* the scroller.

## 2. Goals & non-goals

**Goals**
- Remove the `_innerW` broker; make responsive shelves self-contained and reusable.
- Promote a first-class, **virtualized** `PagedShelf` to `FluentGpu.Controls`, plus a generic size-reactive primitive.
- Scale to large collections via real virtualization (recycling), not a finite strip of all cards.
- Keep both triage bug-fixes as permanent engine-level behavior + checks.

**Non-goals**
- No change to the existing six `IVirtualLayout` built-ins or the `IVirtualLayout` signature (the new seam is opt-in).
- No new WinUI control parity claim — `PagedShelf` is a bespoke "improve, don't copy" control (Spotify-style rail);
  it reuses Fluent visual conventions (button hover/press ramps, focus rects, `PipsPager`) but has no 1:1 XAML source.
- No drag-reorder of shelf cards in v1.

## 3. Decisions (from brainstorming)

| Question | Decision |
|---|---|
| Scope | Promote to the control kit (first-class control). |
| What to promote | Both a generic size-reactive primitive **and** the `PagedShelf` control built on it. |
| Virtualization | Virtualized (ItemsView-backed), including the required engine seam. |
| Seam shape | **Opt-in widened seam** (`IViewportVirtualLayout`), mirroring `IMeasuredVirtualLayout`. Non-breaking; the cleaner full-interface change (Approach 2) stays a future option because the opt-in seam is purely additive. Geometry is **overridable** (auto-fill by default; caller can force `perPage`/card width or supply a custom `IVirtualLayout`). |
| Pager affordance | **All modes available and combinable** — chevron buttons, `PipsPager`, hover-edge buttons, and fully custom — each independently stylable via the `TemplateParts`/`::part` pattern. |

## 4. Architecture (3 layers)

### Layer A — Engine seam (`FluentGpu.Engine`)

New opt-in interface in `Scene/VirtualLayout.cs`, layered over `IVirtualLayout` exactly like `IMeasuredVirtualLayout`:

```csharp
/// Opt-in widened seam: the engine feeds the SCROLL-AXIS (main) viewport extent before any geometry call, so a
/// layout can size items to the viewport (e.g. "N equal cards that fill the width"). Stateful + allocation-free,
/// like IMeasuredVirtualLayout. The core IVirtualLayout contract is unchanged.
public interface IViewportVirtualLayout : IVirtualLayout
{
    /// Called by the layout engine before ContentExtent/Window/ItemRect, with the main-axis viewport extent
    /// (width for a horizontal viewport) and the cross size. O(1), allocation-free.
    void SetViewport(float mainExtent, float crossSize);
}
```

**`FlexLayout` wiring.** At each site that drives a fixed-geometry layout, when `sc.Layout is IViewportVirtualLayout vl`,
call `vl.SetViewport(mainExtent, cross)` *before* `ContentExtent`/`ItemRect`/`Window`, where
`mainExtent = horizontal ? innerW : innerH` and `cross = horizontal ? innerH : innerW`:
- `ArrangeVirtualLayout` (`FlexLayout.cs:546`) — `innerW`/`innerH` already in scope.
- the realize/window block in `ArrangeViewport` (`:510-528`).
- the measure-time D1 path (`:417-424`) — pass the best-known main extent (the offered/`DefiniteWidth`); one-frame
  staleness self-corrects through the existing D1 realize-after-layout pass.

This mirrors the `IMeasuredVirtualLayout` routing (detected at `:484`, fed via `SetMeasured` at `:630`).

**New built-in `FillRowVirtualLayout : IViewportVirtualLayout`.** The horizontal "fill the viewport with equal cards"
layout — `HorizontalGridVirtualLayout` with a *computed* item width:

- Construction: `new FillRowVirtualLayout(minCardW, maxCardW, gap, rows = 1, perPageOverride = 0, fixedCardW = 0f)`.
- `SetViewport(main, cross)`: cache `(_main,_cross)`; recompute `_perPage` + `_cardW`.
- Fit (the bug-fixed `Metrics`): `perPage = floor((main+gap)/(minCardW+gap))`, clamped `≥1` and to `count`; grow `perPage`
  while `cardW > maxCardW` and columns remain; **then `cardW = min(cardW, maxCardW)`** (the ballooning fix — safe
  because `cardW>maxCardW` implies the capped row fits). `perPageOverride`/`fixedCardW` bypass the auto-fit.
- `ContentExtent(n, cross)` = `cols*cardW + (cols-1)*gap` along the main axis (`cols = ceil(n/rows)`).
- `ItemRect(i, cross)` = `x = (i/rows)*(cardW+gap)`, `w = cardW`; `y/h` split `cross` into `rows` (like
  `HorizontalGridVirtualLayout`).
- `Window(...)` = column windowing on the computed stride.
- Stateful cache; zero per-call allocation (seam contract).

**Canon obligations.** Register `IViewportVirtualLayout` + `FillRowVirtualLayout` in `design/SPEC-INDEX.md` §2 and the
`design/subsystems/README.md` ownership map (owner: `virtualization.md`); document the seam in
`design/subsystems/virtualization.md`; run `powershell -File design/check-canon.ps1` (must exit 0).

### Layer B — Controls (`FluentGpu.Controls`; TerraFX-free; engine `Tok` only)

**`Responsive` primitive** — the reusable measure mechanism that replaces the broker pattern:
- Hook `UseMeasuredWidth()` → `(IReadSignal<float> Width, Action<RectF> OnBounds)`; the caller wires `OnBounds` to its
  root `BoxEl.OnBoundsChanged` and reads `Width` (subscribes). One-frame fallback before the first bounds report
  (mirrors `AutoSuggestBox._selfW`, `AutoSuggestBox.cs:112-113,504-506`).
- Render-prop wrapper `Responsive.Of(Func<float,Element> build, float fallback = 0f)` for declarative/non-component
  callers — a self-measuring `BoxEl` that rebuilds children when its width changes.

**`PagedShelf` control** = `ItemsView`(horizontal, `FillRowVirtualLayout`) + pager + edge-fade:
- **Public surface (sketch):**
  ```csharp
  PagedShelf.Create(
      int count,
      Func<int, float, Element> cardAt,        // (index, computedCardW) => card  — cardW surfaced from the layout
      string? title = null, Element? header = null,
      float minCardW = 150f, float maxCardW = 200f, float gap = 12f,
      int rows = 1,
      PagerModes pager = PagerModes.Chevrons,  // [Flags] Chevrons | Pips | HoverEdge | Custom (combinable)
      Func<PagerContext, Element>? customPager = null,
      TemplateParts? parts = null,             // ::part styling, see below
      int perPageOverride = 0, float fixedCardW = 0f,
      IVirtualLayout? layoutOverride = null);  // the "with override" escape hatch
  ```
- **Template** keeps `(int index, float cardW) => Element` so `MediaCard.Shelf(..., w)` is unchanged: the control
  surfaces the layout's computed `cardW` as a signal that the template reads.
- **Paging** = animated programmatic scroll via `ItemsViewController` (set the scroll target → `ScrollAnimator` glides),
  replacing the hand-rolled `UseSpring` strip translate in the old app shelf. `canPrev/canNext`, `PipsPager` position,
  and `EdgeFade` all derive from the live scroll offset vs. content/viewport extents.
- **Pager modes** are `[Flags]` and combinable (`Chevrons | Pips`, etc.). `Custom` renders `customPager(ctx)` where
  `PagerContext` exposes `{ Page, PageCount, CanPrev, CanNext, GoTo(int), Prev(), Next() }`.
- **Per-part styling** via `TemplateParts` (same pattern as `AutoSuggestBox.Parts`): `PartHeader`, `PartChevronPrev`,
  `PartChevronNext`, `PartPips`, `PartEdgePrev`, `PartEdgeNext`, `PartViewport`, `PartStrip`, `PartCard`.

### Layer C — App migration (`Wavee`)

- `HomePage`: replace both `Embed.Comp(() => new PagedShelf(...))` calls with `PagedShelf.Create(...)`; **delete
  `_innerW`** and the measuring outer `BoxEl` — the page root just wraps `ScrollView(page)`.
- `Spotlight`: wrap in `Responsive.Of(w => Spotlight(arr[0], Play, w))` for its text-clamp width.
- Delete the app-local `app/Wavee/Components/PagedShelf.cs` (`PagedShelf` + `ScrollStrip`). `MediaCard` stays as the
  card template (it already honors `cardW`).

## 5. Data flow

```
window resize
  → ScrollView viewport width changes (FlexLayout: vertical viewport adopts finite width)
  → PagedShelf's horizontal ItemsView viewport (innerW) changes
  → FlexLayout calls FillRowVirtualLayout.SetViewport(innerW, innerH)
  → layout recomputes perPage + cardW (capped) → ContentExtent / ItemRect / Window
  → ItemsView re-realizes the visible card window; control re-publishes cardW signal → cards re-fit
chevron / pip / edge click
  → control computes target page offset → ItemsViewController scroll target → ScrollAnimator glides
  → offset change updates canPrev/canNext + EdgeFade (compositor-level)
```

## 6. Edge cases & invariants

- **Few items** → `cardW` capped at `maxCardW`; strip left-aligns with trailing space; pager hides (`pageCount == 1`).
- **Stale-page jump** is structurally gone — paging is scroll-offset based and `FlexLayout` already re-clamps offset to
  content on relayout (`:496-498`).
- **Before first measure** → fallback width for one frame (primitive + control), then corrects via D1.
- **Zero managed alloc** in frame phases 6–13 preserved: the layout is stateful (no per-call alloc); the control wires
  bindings/effects once at mount.
- **Accessibility**: pager buttons carry `Role` + keyboard activation; `ItemsView` owns item focus/selection/keyboarding.

## 7. Testing

- **VerticalSlice golden checks** (`src/FluentGpu.VerticalSlice/Program.cs`) — the layout now lives in the engine, so
  the triage bugs become unit checks:
  - `FillRowVirtualLayout.SetViewport(main,cross)` then `ContentExtent`/`ItemRect` produce the expected `perPage` +
    `cardW`, including the **`maxCardW` cap** for few-items/wide-viewport (regression check for bug #1).
  - The engine calls `SetViewport` before geometry for an `IViewportVirtualLayout` (seam-routing check).
  - Alloc tripwire stays green across a `SetViewport`→realize cycle.
- **Manual** (`--screenshot` + live): a shelf renders equal cards filling the width; resize re-fits; chevron/pip/edge
  glide; edge-fade tracks position; circular artist cards stay circular at the capped size.

## 8. Implementation layering (plan outline)

Each layer builds + verifies independently before the next:

1. **Engine seam** — `IViewportVirtualLayout` + `FillRowVirtualLayout` + `FlexLayout` `SetViewport` calls; golden checks.
2. **Canon** — `SPEC-INDEX.md` §2, `subsystems/README.md`, `virtualization.md`; `check-canon.ps1` green.
3. **Controls — primitive** — `UseMeasuredWidth` hook + `Responsive.Of`.
4. **Controls — PagedShelf** — ItemsView wiring, pager modes, `TemplateParts`, controller-driven paging, edge-fade.
5. **App migration** — HomePage adopts `PagedShelf.Create`, deletes `_innerW`; Spotlight uses `Responsive`; delete the
   app-local shelf.

## 9. Risks

- **`ItemsViewController` scroll API surface** — paging assumes a programmatic animated scroll-to-offset on the
  horizontal `ItemsView`. If the controller exposes scroll-to-*index* only, the control maps page→first-index→offset.
  (Confirm exact API in the plan; `ScrollAnimator`/`TargetX` is the underlying mechanism.)
- **Measure-time `SetViewport`** — the D1 measure path may call `ContentExtent` before the real width is known; the
  layout must degrade gracefully (use last-known/offered main) and rely on D1 realize-after-layout to correct. Covered
  by the seam-routing check.
- **`MediaCard.Shelf` cell fit** — virtualized cells get a definite width from `ItemRect`; confirm the card's
  width-driven square cover + clamped text behave identically to the strip version (visual diff).
