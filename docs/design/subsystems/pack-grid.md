# Pack grid: the bento packer, windowing and reorder

Code: `src/FluentGpu.Engine/Scene/PackLayout.cs` (pure), `src/FluentGpu.Controls/PackGrid.cs`,
`src/FluentGpu.Controls/PackReorder.cs`, `src/FluentGpu.Controls/TileMotion.cs`, `BoxEl.Hold` / `OnHold` /
`OnHoldReleased` (`Dsl/Element.cs`, dispatch in `Input/InputDispatcher.cs`). Gallery: `pack-grid`
(`src/FluentGpu.WindowsApp/Pages/PackGridPage.cs`). First caller: Wavee's pinned Home.

## The packer (`PackLayout.Pack`)

Input: entries in order, `PackMetrics` (columns, row unit, gap, group gap, tool strip), width. Output: a `PackResult`
(grow-only tables, one per grid): a rect, a column span, a row span and a tool strip per entry, and a band table.

Order is the truth. Nothing is back-filled: an entry is never placed ahead of an earlier one.

1. A `GroupHead` starts a group (whole row, `Extent` tall; `Extent` 0 with nothing after it takes no room). `GroupGap` goes
   above every shown head but the first.
2. A `Row` (chrome in a group) and a `Band` (a `full` item, `Extent` DIP or `Rows` rows tall) take the whole row alone.
3. Tiles are cut into bands in reading order. A band takes the next tiles that fit across and is as tall as the tallest of
   them. A run of equal tiles that needs more than one band wraps evenly (five in four columns is three and two).
4. Tiles shorter than the band stack in columns beside the tall ones, sharing the band's height; a few left over join the
   stacks instead of stranding on a band of their own (so a stack member can be shorter than its class: by design).
5. The fill pass: the columns a band has left go one at a time to its narrowest block; a block with a size the user chose
   (no `Auto` flag) waits half a column longer. Tiles only grow.
6. A span wider than the grid is clamped. An `Open` entry is at least `Extent` DIP tall and `OpenMinCols` wide.
7. A tool strip (`ToolStrip` > 0) sits above every block of one tile `ToolStripMinRows` rows or taller, and adds to the band.

Every placed vertical slice (a head, a row, a full band, a band of tiles) is one band of the result, so the band table is a
monotonic partition of the height: `Window` and `HitTest` are binary searches. `Verify` proves the bento: in every band the
column blocks start at 0, follow one gap apart and end at the width, every block spans the band (its members one gap
apart), nothing overlaps, no tile is narrower than its class (nor, standing alone, shorter), reading order is entry order.
DEBUG builds of `PackGrid` run it after every pack and print `[pack] fault ...` to stderr.

The result depends on entries, metrics and width only. Nothing a tile has loaded changes it: that is the no-jump guarantee.

## The grid (`PackGrid.Create`)

- A ZStack box (`IsolateLayout`, `Height` = the pack's height) with one keyed wrapper per realized entry, in entry order
  (tab and narrator order = reading order). A wrapper has explicit `Width`/`Height`, `Margin = (x, y)`, `Start` alignment,
  no grow or shrink and `ClipToBounds`: a fixed-size relayout boundary, so a tile's own re-render never reaches the grid.
- It is not a scroller. It sits in the page's scroller and realizes the bands within `OverscanViewports` of the viewport
  (widened toward the scroll's motion). The grid's top in the scroll content is the sum of layout offsets from the grid up
  to the nearest `Scrollable` node (the scroll is a paint shift, so it is not in them). An auto-tracked effect reads the
  scroller's offset and viewport on scroll frames (arithmetic and binary searches, no allocation) and re-renders the grid
  only when the realized range no longer covers half the overscan, or reaches past two and a half of it.
- Every change of entries, metrics or width re-packs once. Wrappers keep their keys and nodes; their new margins and sizes
  are layout changes the FLIP pass animates (`PackGrid.Reflow`: bounds, 250 ms tween on cubic-bezier(0.25, 1, 0.5, 1),
  `SizeMode.Relayout`, so a tile's own subtree re-solves at the eased size and its content glides with its box; Reveal laid
  the content out at the final size at once and every right-aligned part jumped; reduced motion: a 120 ms fade). A
  column-count change is the same path: no remount at a tier crossing.
- A plain width change (the same entries and metrics at another width: a resize inside the board's column tier) is not a
  re-flow: the tiles follow the width at once, with their content, as the page around them does.
- The wrapper clips at the packed box. `PackGridOptions.InkOutset(item)` lets an item's paint reach beyond it (a full row's
  heading on the page gutter whose display glyph overhangs): the wrapper stays a boundary by `IsolateLayout` and one clip
  layer reaches the outset with negative margins, padding its content back, so box, pack and neighbours are unchanged.
- Leaving the window is not a removal: an item scrolled out of reach renders once more with its exit off and is dropped on
  the next render, so a window edge never leaves an exiting orphan behind (drawn at a stale place, and read by an
  inspector as the tile's rect). `TryNodeRect` answers only for keys realized now. Exits play for keys removed from the list.
- Entering: a key new to the list fades in (`Enter`); a key the window reaches mounts in place; a known key a re-pack brought
  into the window enters from its previous rect (an `EnterExit` offset, opacity 1), so it slides in. On a re-pack every
  item that was realized stays realized for that render, so it slides to its new place instead of vanishing.
- `PackCell.Box` / `Phase` are signals a tile reads to pick its form from the span it got; written on pack edges only.
- `PackGridController`: the live pack, rects, hit test, neighbours, `BringIntoView`, `Focus` (realizes first when needed),
  `FocusedKey`, `Verify`, and `TryNodeRect` / `TryOrigin` for an inspector's "realized rect equals packed rect" check.

## Reorder (`PackReorder<T>`)

- Projection, not mutation: while lifted the grid packs a projected order; the app's list changes only at the drop (one
  `OnCommit(PackMove)`), so a cancel needs no undo. A list that changes under a lift (the app republishing its model
  mid-gesture, a sync adding a row) carries the projection over by key; only a lifted item that left the list ends the lift.
- Pointer: tiles are `Draggable` (mouse past `MouseSlop`; touch after a 500 ms hold, see below). The rule for a hit, the
  concept's `moveRel`: onto a later entry the item lands after it, onto an earlier one before it, onto a group head after it.
  A target triggers once, cannot trigger again until the pointer has left it, never sooner than `DwellMs` after the last (on
  the frame clock), and never without new travel: after a trigger the pointer must move half `MouseSlop` in GRID space. A
  re-flow slides other tiles under a resting pointer and must not walk the order; the edge auto-scroll carries the content
  under a resting pointer (the grid-space point moves) and does walk it, which is how a drag reaches a far place. A 40 ms
  interval re-runs the hit while lifted for that case. A pointer resting within the auto-scroll zone (100 DIP of the
  viewport's edge, `DragDropContext`) therefore keeps scrolling and moving the slot: that is the edge scroll, not the dwell. While
  lifted the re-flow animates during scroll (`AnimateDuringScroll`). `Slot(rect)` draws under the tiles at the packed rect.
- Keyboard: `PackKeyboard.LiftMode` (Space lifts, arrows move, Space/Enter drop, Esc cancels, focus loss drops).
  `MoveBy` and `MoveToward` commit one step at once when nothing is lifted. Left/right: one step in order; up/down: onto the
  `Nearest` entry. After a move the item is brought into view once the slide ends. Plain arrows move focus between tiles;
  modified arrows bubble to the board (a theme's Alt+Shift+arrows reads `FocusedKey`).
- Whose drag: in reorder context (reorder enabled, the tile can lift) the tile wrapper sets `BoxEl.ClaimsDescendantDrag`, so a
  drag that starts on a draggable inside the tile (a track row in a card) moves the TILE, mouse and touch alike, and the
  tile takes a touch hold over context or hold handlers inside it. Out of reorder context (Classic outside Edit Home, Zune
  outside customize) the wrapper is not draggable and inner sources drag as themselves. A `BlocksDragArm` control inside
  the tile (its ⋯ corner) still arms nothing.
- Touch hold on a tile: a move past the slop after the hold lifts it; a release without one raises the tile's context
  request (the menu a right-click opens), or calls `PackGridOptions.OnHoldReleased` when the theme sets it.
- Drag-in: the grid is a drop target for `AcceptKinds`; `Materialize` makes a provisional item that joins the projection
  under the pointer; a release commits through `OnForeignCommit(payload, index)`; leaving removes it.
- Announcements on edges only (`Grab`, `Move` throttled, `Drop`, `Cancel`, `Edge`), composed by the app (`AnnounceText`).

## Press and hold (`BoxEl.Hold`, `OnHold`, `OnHoldReleased`)

- `HoldSpec(TouchMs, MouseMs, SlopDip, DragWindowMs)`; `MouseMs` 0: the mouse does not hold.
- Touch: the arena's Hold member, with the node's own dwell. A node with `OnHold` takes the hold instead of raising its
  context request. A draggable node that holds enrolls no axis-locked drag at the down, so a pan scrolls until the hold
  fires; after it, a move past the slop inside `DragWindowMs` lifts the node's drag. A release without that fires
  `OnHoldReleased`, or, when the node has none, raises its context request (trigger Hold): the touch long-press menu,
  deferred to the release. The press's tap is swallowed. Above the nearest context-or-hold node, a holding node that claims
  its descendants' drags (`ClaimsDescendantDrag`) takes the hold.
- Mouse: a timer on the press (ticked with the gesture arenas; `HasArmedHold` keeps frames coming). A move past the slop
  before the dwell cancels it; after it, a move past the slop arms the drag of the nearest draggable. The click is swallowed.

## Tile motion (2-D)

`TileMotion.Tilt`: a press listener (`HoverScopeTransparent` + `OnPointerPressed`) seeds scale and 1-2 DIP of travel toward
the press point on the tile's own node, and eases back when the pointer is no longer held. `TileMotion.TurnOver`: scale X to
0, swap the face at the half, back to 1; reduced motion cross-fades. The transform is a 2x3 affine: no perspective.
