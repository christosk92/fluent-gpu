# DnD gate map + the verification loop

Every behaviour below is pinned by a headless VerticalSlice check (no GPU, no window). **Find the gate that already
owns the behaviour you are about to change** — if one exists and you have to weaken it, you are changing a contract
and owe the canon doc an edit too.

## Where the gates live

| Suite file | `--suite` tag | DnD gates |
|---|---|---|
| `src/FluentGpu.VerticalSlice/Suites/ControlsSuite.cs` | `controls` | 51 — `e5dragdrop.*` (47) + `sortable.*` (4) |
| `src/FluentGpu.VerticalSlice/Suites/ScrollSuite.cs` | `scroll` | `e11virt.insertion` / `.previewpos` / `.empty`, the `cp2.*` reorder/displacement family, `e11virt.{prefix-disp,10b}` |
| `src/FluentGpu.VerticalSlice/Suites/TouchSuite.cs` | `touch` | `gate.arena.{reorder-vs-pan,determinism,alloc-zero,dispatch-alloc-zero}` |

ControlsSuite emits them from seven methods: `E5DragDropChecks`, `DragChipChecks`, `DragScrimChecks`,
`DragChipPickupFlashChecks`, `DragScrimVirtualScrollChecks`, `SortableSurfaceChecks`, `SortableMathChecks`.
The `Check(...)` first argument is `"<gate-name> <prose>"` — the leading token is the gate name.

**There is no xunit project for the engine's DnD math.** `SortableMathTests` does not exist; `sortable.*` in
ControlsSuite is the coverage. App-side rules are xunit in `src/apps/Wavee.Tests/`: `WaveeDragRulesTests`,
`PlaylistReorderRulesTests`, `WaveeDragChipModelTests`, `MoveRowsConventionTests`, `PlaylistMoveOpsTests`.
(`AlbumDrawerRowsTests` is skeleton-row counting — not DnD.)

## L1 — gesture, threshold, lifecycle

| Gate | Pins |
|---|---|
| `e5dragdrop.1` | A press that moves exactly +4/+4 (ON the box edge) stays armed and releases as a plain click; zero drag callbacks, transform untouched. |
| `e5dragdrop.threshold` | `ThresholdMultiplier` scales the per-axis MOUSE box per source: at ×2 a 6px gesture still clicks while 10px drags; ×1 promotes at 5–6px unchanged; ≤0 falls back to the base 4px box. |
| `e5dragdrop.2` | Crossing the box promotes the ancestor `CanDrag` row a child press armed: Started → one Delta, child `Pressed` clears, opacity 0.80, `DragShadow`, `HitTestVisible` cleared, transform tracks the pointer. |
| `e5dragdrop.2b` | Release raises no click anywhere, restores every visual channel, and hands `OnSettle` a from→to pair equal to the accumulated delta. |
| `e5dragdrop.3` | `DragEventArgs` semantics: `Total*` from the arming press, `Absolute` raw, `Local` = grab offset on the MOVING box, 50ms-EMA velocity from real timestamps and exactly 0 at `TimestampMs == 0`. |
| `e5dragdrop.4` | Escape and window deactivation both abort: visuals restore, `OnDragCanceled` (never Completed), the eventual release raises no click and no drop. |
| `e5dragdrop.5` | `YieldsToPan` promotion-time arbitration — cross-axis over a genuinely overflowing scrollable yields; along-axis and no-overflow win the drag. |
| `e5dragdrop.style` | `DragSource.Style.Opacity` overrides the 0.80 default. |
| `e5dragdrop.armblock` | `BlocksDragArm` stops `TryArm`'s upward walk at itself; ordinary card content still arms; a barrier that is itself draggable still arms. |
| `e5dragdrop.touch` | An arena-claimed touch reorder drives the FULL L2 session (TryBegin+Move at claim, Move per contact move, TryDrop paired with Complete) and closes session + spotlight on release. |

## L2 — session, discovery, refusal, transparency, spring-load

| Gate | Pins |
|---|---|
| `e5dragdrop.capability` | A `CanAccept`-false inner target is transparent to discovery — only the compatible ancestor enters. |
| `e5dragdrop.parked` | A `KeepAlive`-parked (detached) page publishes no drop targets: reachability is proved by terminating **at Root**, `Move` never throws, and re-appending restores them with no sticky per-node state. |
| `e5dragdrop.facade` | `Drag.Source` defaults (Stationary + 0.4 dim, Ghost opt-in) and `Drop.Target<T>` unwrapping both a direct payload and a `ReorderPayload`, gating on the typed predicate, captioning on enter. |
| `e5dragdrop.prune` | A drag whose node is freed reports `OnAbandoned` → the L2 session closes and the spotlight clears (instead of both leaking for the process lifetime). |
| `e5dragdrop.refusal` | A kind-matched refuser publishes `RefusedTarget` + its caption while entering nothing; empty space publishes neither; the not-allowed glyph keys on `Refused`, not on `DropEffect.None`. |
| `e5dragdrop.transparent` | A `Transparent` target is skipped entirely — no acceptance, no refusal cue — and discovery continues to an accepting ancestor, or to a refusing ancestor whose reason is the one published; `Transparent` false is byte-identical to before. |
| `e5dragdrop.springload` | Dwell fires `OnSpringLoad` exactly once per Enter (re-arms only after a leave), holds `HasActiveWork` so a motionless pointer keeps getting frames, and works for an acceptor, a `CanAccept` refuser (cue unchanged), and a `SpringLoadOnly` waypoint. |
| `e5dragdrop.ext` | The OS `IDropTarget` seam hovers DATA-FREE (empty `FileDropData`, Enter/Over → `Copy`), delivers real paths only at drop, and an off-target Over returns `None` + exactly one `OnLeave`. |

## Spotlight / scrim

| Gate | Pins |
|---|---|
| `e5dragdrop.spotlight` | Only capability-compatible `Spotlight` targets become roots; `Cancel()` clears the flag. |
| `e5dragdrop.scrim.cutout` | Exactly ONE band: an opacity group at `ScrimOpacity` over a flat fill, one rounded ERASE per compatible target using its own radii, none over the refuser, and **no node opacity mutated**. |
| `e5dragdrop.scrim.policy` | A `SpotlightWhen` that refuses this session removes the target; with none left the recorder emits no band at all. |
| `e5dragdrop.scrim.reachable` | A target under a cleared `HitTestVisible` ancestor is dropped; the reachable sibling still gets its cutout. |
| `e5dragdrop.scrim.clip` | `SpotlightScrimClip` scopes the veil AND intersects every cutout, so chrome outside the region stays lit. |
| `e5dragdrop.scrim.band` | Band order: scrim after the whole main pass, before the ghost and DragOverlay bands. |
| `e5dragdrop.scrim.cancel` | Drag-scoped — cancel clears the roots and the next record emits nothing. |
| `e5dragdrop.scrim.alloc` | A drag-move frame with the scrim live is 0-alloc on phases 6–13. |
| `e5dragdrop.scrim.recycled` | Cutouts track the currently-BOUND rows of a recycling virtual list: an offset-driven scroll with no pointer movement re-collects exactly like a pointer move — a slot recycled onto an incompatible item goes dark, one recycled onto a compatible item lights, a row scrolled out leaves no stale cutout. |

## Ghost / lifted visual

| Gate | Pins |
|---|---|
| `e5dragdrop.ghost.layer` | ONE opacity group at the ghost alpha (no per-primitive double blend) with the `Backplate` filled opaquely inside it; restore clears both. |
| `e5dragdrop.ghost.clamp` | The ghost rect is clamped to the scene root on both axes (`restrictToWindowEdges`). |
| `e5dragdrop.reassert` | A mid-drag reconcile does not clobber the ghost for a frame — the host re-asserts translate/opacity/hit-test/`DragGhost` with no pointer move. |
| `e5dragdrop.hidesource` | The destination owns the Stationary source's opacity: the re-assert writes `DragSourceOpacityOverride` instead of the style dim, restores the dim when it clears, and gesture end releases it even if the destination never tore down. |
| `e5dragdrop.animconflict` | A node with a live anim-slab transform/opacity ramp hands BOTH channels to the drag on promotion and takes them back after Complete. |

## Chip / compositor

| Gate | Pins |
|---|---|
| `e5dragdrop.chip.stationary` | A Stationary source is dimmed + hit-test-transparent IN PLACE: never translated, never shadowed, never hoisted; release restores both. |
| `e5dragdrop.chip.compositor` | A drag-move frame with a mounted `DragPreviewLayer` re-renders **0 components** and allocates **0 bytes** on phases 6–13; the epoch bumps only on target/effect/caption edges. |
| `e5dragdrop.chip.band` | The DragOverlay subtree records once, in its own top band, after the main pass AND after the ghost band. |
| `e5dragdrop.chip.clamp` | The chip's measured box stays fully inside the window in the bottom-right corner. |
| `e5dragdrop.chip.survive` | A Stationary drag whose source row is freed stays alive — `PruneDead` reparents the session onto the root and the drop still commits. |
| `e5dragdrop.chip.pickup-flash` | The tilt+scale is a one-shot flash easing to a flat unscaled card within `PickupFlashMs`, seeded once per gesture on a stably-keyed subtree, so caption/target edges neither remount nor replay it. |
| `e5dragdrop.settle.cancel` | Escape mid-insertion tears the whole projection down (gap, preview, hidden rows, spotlight, both sessions) and runs no deposit. |

## Insertion / sortable geometry

| Gate | Suite | Pins |
|---|---|---|
| `sortable.slot` | controls | NN/g centre-crossing against a MEASURED leading extent, clamped to `[0,count]`, with the variable-extent overloads agreeing byte-for-byte with the uniform one when extents are equal. |
| `sortable.gap` | controls | Virtual removal opens an exact `N·extent` same-list gap over a NON-CONTIGUOUS source set with a net-zero content-height delta; hides exactly the sources; positions the preview by the removals above; leaves out-of-range items untouched; caps a cross-list copy at `PreviewCap`. |
| `sortable.empty` | controls | An empty destination still resolves slot 0 with a live gap (the drop appends) while an empty payload or extent-less list stays inert. |
| `sortable.normalize` | controls | The dragged-index set sorts / de-dupes / drops out-of-range in place; the removal queries binary-search that array. |
| `e11virt.insertion` | scroll | The whole declarative destination end to end: slot against the measured prefix leading extent, exact gap with sources hidden, out-of-range rows never moving, preview mounted, drop reporting the RAW slot and holding the gap until the membership handoff closes it and reports the landing. |
| `e11virt.insertion.previewpos` | scroll | The in-gap preview is POSITIONED by its bound transform at the plan's viewport-space gap edge — not merely mounted (the bound/static flip on a reused node). |
| `e11virt.insertion.empty` | scroll | An empty destination accepts at slot 0 instead of silently discarding the drop. |
| `e11virt.prefix-disp` | scroll | Prefix-corrected displacement — a persistent prefix must not drag the sticky hero with the rows. |
| `e5dragdrop.reorder.varextent` | controls | `Reorderable`/`ReorderList` resolve the cross-list slot AND the insertion-line boundary from SAMPLED resting extents, reducing byte-for-byte to the uniform midpoint formula. |

## Reorder list / block commit / policy

| Gate | Pins |
|---|---|
| `e5dragdrop.6` | Midpoint slot math, the 200ms dwell before the target commits, displacement hints, `ProjectOrder`. |
| `e5dragdrop.7` | `Complete()` lands at the LATEST pending slot (hints reset before `OnCommit`); `Move` = RemoveAt+Insert; `Cancel` drops the pending move. |
| `e5dragdrop.7b` | The end-to-end Begin/Update/Complete pipeline commits 0→1 through `OnCommit` + `ReorderList.Move`. |
| `e5dragdrop.block` | `BlockLength 1` is bit-identical to the classic single-item path across every published number; a real block displaces by its whole span, projects to a contiguous remove+insert, clamps to `Count − BlockLength`, commits through `Move<T>(list, from, blockLength, to)`. |
| `e5dragdrop.reorder.policy` | The foreign gate + captions (refusal included), item lift styling, and `RequireDropOnList` committing ONLY on a release over the list — while the list's own payload is always accepted and never captioned. |

## Alloc gates (the ones a careless delegate breaks)

`e5dragdrop.8` (one reused `DragEventArgs` per gesture — 0-alloc dispatch) · `e5dragdrop.8b` (the whole drag frame at
pointer rate, phases 6–13) · `e5dragdrop.chip.compositor` · `e5dragdrop.scrim.alloc` · `cp2.dragalloc` (the
displacement seed is edge-triggered) · `gate.arena.{alloc-zero,dispatch-alloc-zero}`.

These are what break when you put a string interpolation in `Caption`, a LINQ query in `CanAccept`, or a `new` in
`SpotlightWhen` — all of which run **every frame** while a drag is live.

## The verification loop for a DnD change

1. **Write the gate failing-first.** Every fix round in this campaign proved the defect at HEAD (`previewpos` dy=0,
   `parked` root=true, `threshold` clicks=0/starts=1) before fixing it. A gate that never failed proves nothing.
2. **Both configurations.** `dotnet build src/FluentGpu.slnx` *and* `-c Release` — the diag const gates compile a
   different arm and `TreatWarningsAsErrors` makes a Release-only warning a Release-only break.
3. **Full slice.** `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED". Iterate locally with
   `--suite controls` / `--suite scroll` / `--suite touch`, but CI runs everything. Record the gate count in the
   commit message (the campaign's rounds landed at 946 → 950 → 951 → 952).
4. **App tests.** `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` — compare to the recorded pre-existing-failure
   baseline, don't chase someone else's red.
5. **Canon.** Touched a contract? Reconcile the owning doc (`input-a11y.md` §12 / `controls.md` §7.4 /
   `gpu-renderer.md` §7.4) and run `powershell -File docs\design\check-canon.ps1` (must exit 0).
6. **Feel.** The gates cannot see judder, a chip that reads as a misrendered card, or a cue that accuses the wrong
   surface. Three of the four fix rounds came out of a human feel-test — hand the app back for one.
