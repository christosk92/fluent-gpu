# DnD pitfalls — the bug classes we actually shipped and fixed

Each entry: the **mechanism** (why it happened, once) and the **rule** (what to do). All of these were found by a
human feel-test after the gates were green, which is the point — these are the failure modes headless checks do not
see until you name them.

---

## 1. Bind-shape flip on a reused node

**Mechanism.** Bind wiring is **mount-only**. A component with two branches — an idle branch that set
`Transform` to a static value and an active branch that set it to a bound `Prop.Of(...)` — reused the same node, so
whichever branch mounted first decided forever whether that channel was bound. The insertion preview mounted idle,
never got its transform bind, and parked the gap card at viewport y = 0 while the line and the gap were elsewhere.
(The `BindContract` DEBUG tripwire had been warning about it in the slice output the whole time.)

**Rule.** A component that can render two shapes of the same node must declare the **same bound channels in BOTH
branches**. Give the channel one bound thunk that reads state live and returns the identity/neutral value while idle —
never `bound in one branch, literal in the other`. Watch the slice output for `bindcontract` warnings.
Gate: `e11virt.insertion.previewpos`.

---

## 2. Per-URI identity where the UI state is per-ROW

**Mechanism.** Expanded-drawer state was keyed by track URI. A playlist can hold the same track twice, so duplicates
expanded together — and the reconciler minted two identical keys for two different rows.

**Rule.** Two identity spaces, and they are not interchangeable. **UI state keys by the membership ROW**
(`MembershipDiff.RowKey`: `ContextUid` primary, `uri#occurrence` fallback, position only when uid-less;
`RowKeyMatches` is the alloc-free comparison for bound re-skin closures). **Data caches key by URI.** A drag payload
addresses rows through row refs (`SourceRows`), never through the URI. When you add per-row UI state next to a
draggable row, ask which space it belongs in before you type the key.

---

## 3. Parked / detached ≠ safe — reachability must terminate at Root

**Mechanism.** The reachability filter walked ancestors and accepted "ran out of parents" as reachable. A
`KeepAlive`-parked page (an inactive tab, which the reconciler parks with `SetSubtreeParked` + `Detach` while
deliberately RETAINING `HitTestVisible`) and an exit orphan both terminate on a null parent while being unhittable.
So background tabs advertised phantom spotlight roots, punched cutouts at stale last-arranged rects, **and** had their
app `CanAccept` lambdas evaluated per frame against parked state — which read to the user as "drag is dead on tab 2".

**Rule.** Reachability is proved by the walk **terminating at `SceneStore.Root`** — the only node the hit test descends
from — with `IsLive` guarded per node (`LiveIndex` throws on a dead handle, and a throw there escapes into
`DragDropContext.Move` and kills the whole gesture instead of filtering one target). Never "no more parents ⇒ fine".
The filter is recomputed per refresh and keeps no sticky exclusion state, so reactivation restores targets by itself.
Gates: `e5dragdrop.parked`, `e5dragdrop.scrim.reachable`.

---

## 4. `DropTargetsVersion` is a HINT, not the authority

**Mechanism.** The spotlight root set was collected only on a `DropTargetsVersion` edge. But the version moves only
when the sparse spec column is **written**, and the signals-first bound realize path recycles a virtualized row by
writing its bind signal (`Reconciler.RebindBoundSlot`) — the node handle and its `DropTargetSpec` instance survive, the
version never moves. The set went stale *in place*: cutouts stayed on the slots that WERE compatible and drifted with
them as the rows underneath changed. A `CanAccept` that reads a signal has the same problem with no virtualization at
all.

**Rule.** A live session re-collects **unconditionally, once per frame**, through
`DragDropContext.SyncSpotlightBeforeRecord()` at **phase 7.8** — after reconcile/layout/realize and the scroll-offset
writes, before record — so the cutouts describe the bindings and geometry *this* frame paints. Never gate
drag-lifetime state on a version that only a column write bumps. Gate: `e5dragdrop.scrim.recycled`.

---

## 5. Per-frame allocation in drag delegates

**Mechanism.** `OnOver`, `SpotlightWhen`, `CanAccept`, `Transparent` and `Caption` read as cold edge callbacks. They
are not. Edge auto-scroll re-projects the current destination once per frame under a **still** pointer, and the
spotlight refresh invokes `CanAccept`/`Transparent`/`SpotlightWhen` on every opt-in target every frame — all inside
the 0-alloc region (phases 6–13).

**Rule.** No interpolation, no LINQ, no `new`, no boxing in any of them. Cache or precompute captions; return a
constant where the reason cannot change mid-gesture. Gates: `e5dragdrop.8b`, `e5dragdrop.scrim.alloc`,
`e5dragdrop.chip.compositor`.

---

## 6. The L1/L2 pairing rule

**Mechanism.** Touch was L1-only: `ClaimTouchReorder` promoted the controller, `TouchMove` never called
`DragDrop.Move`, `TouchUp` completed without `TryDrop`/`Cancel`. Result: a touch drag saw no drop targets, no
captions, no edge auto-scroll — and any session opened another way leaked forever, scrim included.

**Rule.** Every input path drives **both** layers, in this order:
- arm/promote → `DragDrop.TryBegin(Drag.ActiveNode, …, Drag.ActiveLift)` **+ an immediate `Move`**;
- move → `Drag.Move(...)` then `DragDrop.Move(hitChain, …)` (gate the extra hit-test walk on a live session);
- up → `DragDrop.TryDrop(...)` **FIRST** (so `OnDrop` reads the live session while visuals are still lifted), then
  `Drag.Complete(suppressSettle: dropped && !settleGlide, dropped: dropped)`; armed-but-never-promoted → `DragDrop.Cancel()`;
- cancel → `DragDrop.Cancel()` **before** `Drag.Cancel()`, so `OnLeave` fires on a live target while the session
  still exists.

Gate: `e5dragdrop.touch`.

---

## 7. A null insertion index deposits a COPY

**Mechanism.** A tab was made a real drop destination alongside its spring-load navigation. Its deposit path passes
`insertionIndex: null` = append, and the append arm is the **copy** arm. Dropping a playlist's own rows onto its own
tab therefore duplicated the user's rows back into their own playlist.

**Rule.** An append/null-index deposit must **refuse a payload that came from the destination itself** (or pass a real
index and take the move arm). The engine cannot check this — same-list detection is app knowledge
(`SourcePlaylistUri` vs the target uri). Wavee encodes it in `TabDropRules.AcceptsDeposit`, which is engine-free and
unit-tested precisely so this stays a table and not a lambda someone edits.

---

## 8. Two owners of the Stationary source's opacity

**Mechanism.** A same-list insertion performs **virtual removal** — the dragged rows go to opacity 0, they are "in the
chip" — while `DragController.ReassertPresented` re-writes the style's 0.4 dim after every mid-drag reconcile (the
reconciler's `ApplyBox` restores authored values unconditionally). Without a seam the press-source row strobed back to
0.4 every reconcile frame while its siblings stayed hidden.

**Rule.** The **destination** owns the source dim while it hides rows: it sets `SceneStore.DragSourceOpacityOverride`
and clears it when it stops; `ApplyPresented` reads `DragSourceOpacityOverride ?? style.Opacity`. Null (the default
for every other drag) means the source's own style stands. The unconditional clear lives in `DragController.Reset` —
**not** `RestoreVisuals`, which is skipped whenever the source node is already dead, exactly the `SourceRecycled` case
that would strand the override and hide the *next* drag's source row. Gate: `e5dragdrop.hidesource`.

---

## 9. Touch slop and the mouse drag box are separate constants

**Mechanism.** Clicking a tab while the mouse was still moving routinely travelled past the 4px box, promoting a drag
and suppressing the click (intermittent "the tab didn't select"). The obvious fix — widen the box — is wrong if
applied globally: an `arenaGoverned` touch promotion has already cleared the arena's own 8px radial
`InputDispatcher.TouchSlopPx` and won its arbitration, so re-gating it on a widened mouse box strands a won arena
below promotion.

**Rule.** `DragVisualStyle.ThresholdMultiplier` scales the **mouse** per-axis box only, is resolved at **arm** time
(the box is tested in `Move` before `Promote` captures the full style, so it must apply to the first move), and ≤ 0
falls back to the base box. Use `Drag.ClickPrimaryThresholdMultiplier` (= 2, WinUI's own list-item value) on
click-primary surfaces. WinUI scopes its constant to mouse for exactly this reason.
Gate: `e5dragdrop.threshold`.

---

## 10. Refusal vs transparency vs silence — pick deliberately

**Mechanism.** A `CanAccept`-false target is *transparent to discovery* by design (the Flutter `DragTarget`
pass-through), so it never becomes `OverTarget` and none of its handlers fire — which makes a refusal
indistinguishable from empty space. Two opposite bugs followed: surfaces that refused silently ("drag & drop is
broken"), and surfaces that refused *loudly* over scenery the drag was merely crossing (a not-allowed glyph over a
page body during a same-list reorder, or "Can't edit this playlist" on an album page that was never a playlist).

**Rule.** Three distinct answers:
- **accept** — `CanAccept` true.
- **refuse** — `CanAccept` false **plus** `RefusalCaption`. The engine publishes the nearest kind-matched refuser as
  `RefusedTarget` only when *nothing* accepted, and the chip renders the reason next to the not-allowed glyph. Write
  the reason and the fix ("Clear sorting to reorder"), not a restatement.
- **sit it out** — `Transparent(session)` true: skipped before it can be recorded as acceptor *or* refusal candidate,
  and dropped from the spotlight set. This is "none of my business". `SpringLoadOnly` is the same idea for a pure
  dwell waypoint.

`DropEffect.None` covers refusal AND empty space; only `DragState.Refused` distinguishes them, which is why hovering
nothing stays silent and the glyph keeps meaning something. Gates: `e5dragdrop.refusal`, `e5dragdrop.transparent`.

---

## 11. Two visuals for one gesture

**Mechanism.** A `Reorderable` list left `DragStyle` unset (⇒ the historical Ghost lift) in an app that also mounts a
`DragPreviewLayer`: the row lifted *and* the chip drew. Same class: a `Stationary` source in an app with no preview
layer mounted has no moving visual at all.

**Rule.** `DragLift.Stationary` ⟺ a mounted `DragPreviewLayer` whose resolver returns a spec for that payload kind.
Set `Reorderable.DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity }` on
lists in a chip app. `DragChipSpec.None` renders nothing — surfaces whose private kinds resolve to no spec should keep
the ghost lift on purpose.

---

## 12. Committing a reorder the user dragged away from

**Mechanism.** `Reorderable`'s pointer completion historically committed at the dwell slot regardless of where the
gesture ended, so dragging a row *out* to a foreign target also committed the local move that the downward travel had
projected on the way.

**Rule.** Set `RequireDropOnList = true` on any list whose rows are also dragged to foreign destinations. Keyboard
lift/drop is unaffected (it has no pointer release). Gate: `e5dragdrop.reorder.policy`.

---

## 13. Options records freeze at mount

**Mechanism.** `InsertionOptions` (like every `ListOptions` sub-record) is unpacked and frozen into the component at
mount — the component-props contract. A delegate that closed over a snapshot of page state kept answering with frame
1's data for the life of the list.

**Rule.** Every `InsertionOptions` / `Reorderable` delegate must read **live** state (a signal `.Peek()`, a field on
the owning component) rather than a captured value. The gesture-local snapshot the framework *does* take
(`_gFirst/_gCount/_gSameList` at Enter) is deliberate: a mid-drag re-render must not move the range the user is aiming
at out from under the pointer, while the geometry is read live from the layout seam.

---

## 14. `OnDeposit` gets the RAW slot — do not correct it twice

**Mechanism.** The slot handed to `OnDeposit` is the raw display slot the user aimed at, deliberately **not**
pre-corrected for the rows virtual removal hid above it. A backend "insert before the row currently at this index"
convention already discounts them, so subtracting them again in app code moved the block twice.

**Rule.** Convert the display slot to your model's index space once, in one named place (Wavee:
`OriginalInsertionIndex`), and pass the **pre-move** index the backend's move convention expects. Return `true` from
`OnDeposit` only when a mutation was actually issued — that is what promises the membership snapshot the gap is handed
over to; returning `true` optimistically leaves the gap open forever.

---

## 15. A `Draggable`-only child inside a clickable ancestor used to eat the click

**Mechanism.** `DragBit` is in the hit-test **self-hit** mask (`InputDispatcher.Hit`), so a `CanDrag`/`Draggable` node
is hit-testable in its own right — and the **deepest** hit wins. Release used to deliver the click to that raw hit
node only, so a drag-handle child with no `OnClick` (a tab's label lane, a card's title block) swallowed every click
aimed at the row it sits in: press showed nothing, release fired a null handler, the row never activated. The
right-click and middle-release paths already walked ancestors; left activation did not.

**Rule.** Left activation now resolves the **activation owner** — the nearest enabled self-or-ancestor with
`ClickBit` — and fires that node's handler (first owner wins, so a child with its own `OnClick` still beats its
parent and the two never both fire). The mouse same-target check compares resolved owners, so press-on-label →
release-on-padding is one click on the row; touch keeps its strict same-node gate. Press-side visuals still track the
raw hit node. See `input-a11y.md` §6.5; gate `B.4b`.

Still prefer putting `CanDrag`/`Draggable` on the **click-owning node itself** rather than relying on the walk: one
node then owns hit-testing, the pressed visual, the drag arm and the click, and `DragController.TryArm`'s own
walk-up has nothing to disambiguate. That is the sidebar row pattern (`SidebarEntityRow.cs:318-329`). The walk is a
safety net for shapes you do not control, not a licence to scatter drag handles inside clickables.
