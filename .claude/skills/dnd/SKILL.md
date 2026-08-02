---
name: dnd
description: Use when implementing or altering drag & drop anywhere in this repo — drag sources, typed drop targets, the drag chip / preview layer, reorder & sortable lists, declarative insertion, spring-load (dwell-to-open), drop captions and refusal cues, the drop-spotlight scrim, drag thresholds, edge auto-scroll, OS file drop, or the e5dragdrop / e11virt.insertion / sortable gates. Read before adding a draggable surface, a drop destination, an insertion list, or debugging "the drop does nothing" / "the cue is on the wrong row" / "my click got eaten".
---

# Drag & drop — declare intent, never coordinates

Scope: the engine's `Input/Drag*` + the drag columns of `SceneStore`, the `FluentGpu.Controls` facade
(`Drag`/`Drop`/`DragChip`/`DragPreviewLayer`/`InsertionOptions`/`SortableMath`/`Reorderable`), and the Wavee
app wiring under `Features/DragDrop/**`. General engine work: the [fluentgpu](../fluentgpu/SKILL.md) skill.
App architecture: [wavee](../wavee/SKILL.md).

**Canon (contracts live there, not here):** `docs/design/subsystems/input-a11y.md` §12 owns the drag CONTRACT
(`DragLift`, `DragVisualStyle`, `DragSession`/`DragState`, `DropTargetSpec`, spotlight policy, settle, spring-load,
thresholds); `controls.md` §7.4 owns the CONTROLS surface (facade, chip, `InsertionOptions`, `SortableMath`,
`ReorderList.BlockLength`, `Reorderable` policy seams); `gpu-renderer.md` §7.4 owns the BANDS and the scrim pixels.
This skill never restates a struct shape — it tells you which lever to pull and which mistakes have already cost us a
fix round.

> The system was rebuilt in a 10-wave campaign plus four post-feel-test fix rounds (`git log --grep="dnd"`,
> `bc537eabb` → `a81e99f6b`). Everything in [pitfalls.md](pitfalls.md) is a bug we actually shipped and fixed.

---

## Architecture in one page

```
 press ──► L1  DragController                  (Input/DragController.cs — the GESTURE)
           arm (TryArm: walk UP for DragBit, stop at BlocksDragArm; resolve ThresholdMultiplier HERE)
           promote (per-axis 4px box × multiplier, mouse only; arena-governed touch uses its own 8px slop)
           lift    Ghost      → the source row IS the visual (translate + dim + shadow + opacity group + ghost band)
                   Stationary → the row stays, dimmed 0.4 + HitTestVisible cleared; the CHIP is the visual
           settle  Ghost → OnSettle (FLIP)      Stationary → OnStationarySettle(phase, rect) → 250ms window
             │  ActiveLift ── the seam ──►
 dispatcher ├──► L2  DragDropContext            (Input/DragDropContext.cs — the SESSION)
             │     TryBegin  = walk up for the nearest enabled DragSource, PayloadFactory() ONCE
             │     ExternalBegin = the same session for an OS/OLE file drag (Source = scene Root)
             │     Move(hitChain) → FindTarget walk order, per node up the chain:
             │         1. skip Disabled / no spec / kind mismatch
             │         2. remember the first spring host (BEFORE acceptance — a refuser may still spring)
             │         3. Transparent(session)?  → `continue`, as if it declared nothing
             │         4. SpringLoadOnly?        → `continue` (waypoint: never destination, never refusal)
             │         5. CanAccept false?       → record as nearest REFUSER, `continue`
             │         6. → this is OverTarget
             │       refusal is published only when NOTHING accepted; caption resolved after acceptance
             │     Tick(dt) = spring-load dwell + WinUI edge auto-scroll (100px band, 1500→150 px/s, 50ms delay)
             │     SyncSpotlightBeforeRecord() at phase 7.8 = re-collect the spotlight roots EVERY frame
             ▼
        SceneStore  DragGhost / DragOverlay / DragSourceOpacityOverride / SpotlightScrimClip
                    RefreshDropSpotlight(session) → capability test: kind → live+enabled → HIT-REACHABLE
                    (ancestor walk must TERMINATE AT Root) → CanAccept → Transparent → SpotlightWhen
                                                  ▼
 recorder bands (gpu-renderer.md §7.4):  main → orphans → SCRIM+cutouts → drag ghost → overlays → CHIP
```

**Controls layer.** `DragPreviewLayer` mounts once at the app root, registers itself as `SceneStore.DragOverlay`, and
follows the pointer through a **bound** transform over `UseDragPosition()` — compositor-only, no render, no alloc.
`UseDragState()`'s epoch is **edge-triggered** (begin/end, target, effect, refusal, caption, settle), so the chip
re-renders only when its *content* could differ. `DragChip.Resolve(state => spec)` turns chip DATA into that layer's
element. `ItemsView` + `InsertionOptions` owns every list coordinate; `SortableMath` is the pure geometry behind it;
`Reorderable`/`ReorderList` is the older lift-and-project list (still the sidebar/queue path).

---

## Recipes

All snippets are the shape of real call sites (Wavee paths in [where-to-change-what.md](where-to-change-what.md)).

### 1. Make anything draggable

```csharp
// default: DragLift.Stationary — the row STAYS in its slot at Drag.SourceDimOpacity (0.4), the chip moves.
row with { Draggable = Drag.Source(MyKinds.Resource, () => BuildPayload()) }

// a CLICK-PRIMARY surface (tab, small nav/entity row): widen the MOUSE drag box or clicks get eaten
Drag.Source(MyKinds.Resource, () => payload, thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier)  // = 2f

Drag.SourceHidden(kind, payload)                      // opacity 0 — the vacated slot IS the gap
Drag.Source(kind, payload, lift: DragLift.Ghost)      // the legacy lifted row (+ backplate/shadow/scale)
```

`PayloadFactory` runs **once**, at promotion. `BoxEl.BlocksDragArm = true` on a child (a play FAB, a "…" corner)
stops the arm walk so pressing it is not a handle for dragging the row.

### 2. A typed drop target

```csharp
DropTarget = Drop.Target<TrackPayload>(MyKinds.Resource,
    accepts:        p => Verdict(p) == Refusal.None,       // false ⇒ REFUSAL (owes a reason)
    transparent:    p => IsSameList(p) || IsReadOnlySurface, // ⇒ "none of my business" (no cue at all)
    onDrop:         (p, s) => Deposit(p),
    caption:        p => Strings.AddTo(name),               // applied on Enter AND Over
    refusalCaption: p => "Clear sorting to reorder",        // the reason + the fix
    springLoadMs:   500f, onSpringLoad: (p, s) => Activate(),
    visualPolicy:   DropTargetVisualPolicy.Spotlight);
```

Choose the "no" deliberately: **`accepts:false` = refusal** (the user aimed here; publish `refusalCaption` or they get
nothing) vs **`transparent:true` = pass-through** (the drag is merely crossing; a cue would be an accusation).
`springLoadOnly: true` is the pure waypoint — a surface that navigates on dwell and takes no deposit.
A tab can be **both**: leave `springLoadOnly` false and give it `onSpringLoad` + `onDrop`.

### 3. The chip — declared ONCE per app, as data

```csharp
// app root, top of the root ZStack:
DragPreviewLayer.Of(DragChip.Resolve(state => Unwrap(state.Payload) switch {
    { } p => new DragChipSpec(ArtSource: p.Art, Title: p.Name, Subtitle: p.Sub, Count: p.Tracks?.Count ?? 1),
    _     => DragChipSpec.None,     // ⇒ nothing rendered for that drag
}));
```

The framework renders the card: opaque, ≤`DragChip.MaxWidth` (280), art + title + subtitle (all ellipsized), corner
count badge + two-card stacked backdrop at `Count ≥ 2`, the caption row, a not-allowed glyph while `state.Refused`,
the `+16/+8` cursor offset, a window clamp, and a **pickup FLASH** (4° tilt + 1.02 scale easing to flat in ~150ms).
Never position a preview yourself and never read `state.Position` to place things.

### 4. A sortable / insertable list (the view owns every coordinate)

```csharp
ItemsView.CreateBound(…, new ListOptions {
  Insertion = new InsertionOptions {
    AcceptKinds   = [MyKinds.Resource],
    CanAccept     = CanDrop,                      // false ⇒ refusal → pair with RefusalCaption
    Transparent   = _ => IsReadOnlySurface,       // ⇒ silent pass-through
    IsSameList    = IsSameListDrop,               // move vs copy semantics
    SpotlightWhen = s => !IsSameListDrop(s.Payload),   // never dim the list you are reordering inside
    SourceIndices = DraggedDisplayRows,           // DISPLAY indices in the insertable range; may be non-contiguous
    DraggedCount  = CountOf,                      // cross-list copy size
    Range         = () => (TrackStart, View().Length),  // bound it, or appended rows ride the gap down
    OnDeposit     = (p, slot) => CommitAsync(p, slot),  // Task<bool> = "a mutation was issued"
    Caption       = CaptionAt, RefusalCaption = WhyRefused,
    GapPreview    = (p, _) => PreviewCards(p), PreviewCap = SortableMath.DefaultPreviewCap,
  },
});
```

`slot` is the **raw display slot**, deliberately not pre-corrected for the rows virtual-removal hid above it — a
backend "insert before the row currently at this index" convention already discounts them, and correcting twice moves
the block twice. The record is frozen at mount, so every delegate must read live state, never a captured snapshot.

### 5. A `Reorderable` list (queue / sidebar style)

```csharp
readonly Reorderable _reorder = new(MyKinds.Resource) {
    ItemExtent = RowExtent,
    DragStyle  = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity },
    RequireDropOnList = true,     // dragging a row AWAY must not commit the local move it projected
};
// per render:
_reorder.Scene = …; _reorder.RequestRender = …; _reorder.ItemCount = n; _reorder.ItemOf = i => PayloadFor(i);
_reorder.OnReorder = Move; _reorder.OnCrossCommit = InsertAt;
_reorder.CanAcceptForeign = CanDeposit; _reorder.ForeignRefusalCaption = Why; _reorder.ForeignCaption = What;
// render: _reorder.List(body)  wraps the drop surface;  _reorder.Item(i, content, key)  wraps each row
```

Unset policy seams ⇒ byte-identical to the pre-policy list. Set `DragStyle` **or** you get two visuals for one
gesture (a lifted ghost row *and* a chip). A multi-selection moves as one unit through
`ReorderList.BlockLength` / `BeginBlock` — that is also what the keyboard Alt+Arrow path uses.

### 6. OS file drop

`DropTarget = new DropTargetSpec([DropKinds.Files], …)` — the Windows backend's `Win32DropTarget` opens a normal
session (`ExternalBegin`), so hover Enter/Over/Leave work and the file list is read once at drop
(`(FileDropData)session.Payload`). The OS owns the drag image, so prefer `DropZone.Create(...)` self-restyling over a
chip there.

---

## Iron rules

1. **Declare intent, never coordinates.** If you are computing a pointer-relative position, a row pitch, a leading
   extent or a gap size in app code, you are re-creating the debt `InsertionOptions`/`SortableMath` exists to kill.
2. **Every input path must drive BOTH layers.** L1 (`DragController`) and L2 (`DragDropContext`) are paired at every
   site: arm/promote → `TryBegin` + an immediate `Move`; move → `Drag.Move` then `DragDrop.Move`; up →
   `DragDrop.TryDrop` **first** (so `OnDrop` reads the live session while visuals are still lifted) then
   `Drag.Complete(...)`; cancel → `DragDrop.Cancel()` before `Drag.Cancel()` (so `OnLeave` fires on a live target).
   A new input modality that drives only L1 gets no targets, no captions, no auto-scroll — and leaks the session.
3. **`OnOver` / `SpotlightWhen` / `CanAccept` / `Caption` run inside the 0-alloc frame region** — every frame while a
   drag is live (edge auto-scroll re-projects under a still pointer; the spotlight re-collects at phase 7.8). No
   interpolation, no LINQ, no `new` in them. Cache or precompute strings.
4. **The chip is compositor-driven.** Bind, don't re-render. Do not add anything to the drag epoch that changes per
   move.
5. **A refusal must be published.** `DropEffect.None` means "over nothing" *and* "refused" — only `DragState.Refused`
   distinguishes them, and it only exists when a kind-matched target sets `CanAccept` false. No `refusalCaption` ⇒ the
   feature reads as broken.
6. **Never dim the list the user is reordering inside** — `SpotlightWhen = s => !IsSameList(s.Payload)`.
7. **Touch and mouse have separate slop constants.** `DragThresholdPx` (4px per-axis, scaled by
   `ThresholdMultiplier`) is mouse; `InputDispatcher.TouchSlopPx` (8px radial, arena-arbitrated) is touch. Scaling one
   from the other strands a won arena below promotion.

Then read [pitfalls.md](pitfalls.md) — the bug classes, with the mechanism for each.

---

## Verify

Gates and the per-wave verification loop: [gates.md](gates.md). Short form for any engine/Controls change:

```powershell
dotnet build src/FluentGpu.slnx                 # Debug
dotnet build src/FluentGpu.slnx -c Release      # AND Release — the diag const gates compile a different arm
dotnet run --project src/FluentGpu.VerticalSlice          # "ALL CHECKS PASSED" (full suite; --suite controls|scroll to iterate)
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj       # app-side rules; compare to the recorded baseline
powershell -File docs\design\check-canon.ps1              # after ANY docs/design edit — must exit 0
```

Touching a contract (a `DropTargetSpec` member, a lift mode, a band, the spotlight policy) means reconciling the
owning canon doc in the same change. Add the gate **failing-first** — every fix round in this campaign proved the
defect at HEAD before fixing it, and that is what makes the gate worth keeping.

---

## Known limits (honest; mirrors `input-a11y.md` §12)

- Touch drag of a `RowSwipe`-wrapped row never promotes — the swipe wins the shared arena. Mouse/pen and the keyboard
  move still reach it.
- Touch does not track a `Stationary` source through recycling (the `_touchReorder` branch is gated on the source
  being live); the gesture still completes/cancels correctly, it just stops updating targets.
- The scrim colour is one theme-blind constant (`DragVisualTok.ScrimColor`/`ScrimOpacity`) — a light-theme softening
  needs a host-plumbed colour.
- A virtualized list's insertion addresses only the rows the model holds (Wavee's queue pages ~100), so a queue drag
  cannot reach row 400. The geometry is exact for whatever is exposed.
- Surfaces that never set `DragStyle` keep the historical ghost lift, so two visual languages coexist until every
  surface opts in.
- Unmounting a bound `VirtualListEl` leaks its parked `KeepAlive` slots and their registered drop targets (they were
  detached, so `FreeSubtree` cannot reach them). A registry/memory leak only — the reachability walk stops them
  advertising anything.
- No keyboard drag simulation, by design: the a11y answer is an ordinary command reaching the same mutation seam
  (Alt+Up/Alt+Down, "Move to playlist…").

## Deeper docs

- [pitfalls.md](pitfalls.md) — the shipped-and-fixed bug classes: mechanism + the rule.
- [gates.md](gates.md) — every `e5dragdrop.*` / `e11virt.insertion*` / `sortable.*` gate and what it pins.
- [where-to-change-what.md](where-to-change-what.md) — task → file map across Engine / Controls / Wavee.
- Canon: `docs/design/subsystems/input-a11y.md` §12, `controls.md` §7.4, `gpu-renderer.md` §7.4.
- Developer guide: `docs/guide/components-elements-layout.md` (the DnD + `ItemsView` sections).
