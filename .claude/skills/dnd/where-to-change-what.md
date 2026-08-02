# DnD — where to change what

Three layers, and the boundary matters: **Engine** owns the gesture, the session and the scene state; **Controls**
owns the declarative surface and the list geometry; **Wavee** owns payloads, rules and captions. A change that needs a
new lever usually belongs one layer *lower* than the surface it is visible on — but adding a mode-specific branch to
the engine to serve one app surface is the mistake this split exists to prevent.

## Engine — `src/FluentGpu.Engine`

| Task | File |
|---|---|
| The gesture: arm / threshold / promote / lift modes / re-anchor / spring-lag follow / window clamp / settle / `PruneDead` / `SourceRecycled` | `Input/DragController.cs` |
| The session: `TryBegin` / `ExternalBegin` / `Move` + `FindTarget` walk order / `TryDrop` / `Cancel` / refusal publication / spring-load dwell / edge auto-scroll / `SyncSpotlightBeforeRecord` | `Input/DragDropContext.cs` |
| L1↔L2 pairing at every input site (mouse press/move/up, touch arena claim, capture loss, external OLE) | `Input/InputDispatcher.cs` — search `DragDrop.` / `Drag.` |
| The contracts: `DragSource`, `DragVisualStyle` (incl. `Lift`, `Backplate`, `ThresholdMultiplier`), `DropTargetSpec` (incl. `SpotlightWhen`, `RefusalCaption`, `Transparent`, `SpringLoad*`), `DragSession`, `DragState`, `DragSettlePhase`, `DropEffect`, `DropKinds`, `FileDropData`, `DragVisualTok` | `Foundation/Events.cs` |
| Scene state: `DragGhost`, `DragGhostBackplate`, `DragOverlay`, `DragSourceOpacityOverride`, `SpotlightScrimClip`, `RefreshDropSpotlight` + `IsHitReachable`, `DropTargetsVersion`, the drop-target registry | `Scene/SceneStore.cs` |
| Element props: `Draggable`, `DropTarget`, `BlocksDragArm`, the drag lifecycle handlers | `Dsl/Element.cs` |
| `UseDragState()` / `UseDragPosition()` + the host-owned `DragEpoch` / `DragPosX` / `DragPosY` signals | `Hooks/Component.cs`, `Hooks/RenderContext.cs`, `Hooks/Context.cs` |
| The scrim band + cutouts, band ordering, `EraseRoundRectCmd` | `Render/SceneRecorder.cs` (canon: `gpu-renderer.md` §7.4) |

**Windows backend:** the OLE `IDropTarget` CCW (hand-rolled vtable — the source-gen attempt crashed on by-value
`POINTL`) is `src/FluentGpu.Windows/Interop/Win32DropTarget.cs`, feeding `InputHooks.ExternalDrag*`.

## Controls — `src/FluentGpu.Controls`

| Task | File |
|---|---|
| `Drag.Source` / `Drag.SourceHidden` / `Drag.SourceDimOpacity` / `Drag.ClickPrimaryThresholdMultiplier`; `Drop.Target<T>` / `Drop.TryUnwrap<T>` | `DragDropFacade.cs` |
| The standard chip: `DragChipSpec`, `DragChip.Resolve` / `.Render`, the card composition + count badge + stacked backdrop + not-allowed glyph + the pickup-flash constants | `DragChip.cs` |
| The overlay layer: `DragOverlay` registration, the bound follow transform + window clamp, the pickup-flash and settle seeds | `DragPreviewLayer.cs` |
| `InsertionOptions` (the declarative sortable destination) and its place in `ListOptions` | `ListOptions.cs` |
| The pure insertion geometry: `SlotFrom*`, `Plan`, `InsertionPlan` (`GapRows`/`GapExtent`/`DisplacementFor`/`PreviewOffset`), `RemovedBefore`/`IsSource`/`Normalize` | `SortableMath.cs` |
| The live insertion host: the mounted `DropTargetSpec`, slot resolution against measured bands, the gap + accent line + terminal dot, the in-gap preview, source-row hide, deposit/teardown + optimistic-membership handoff (`ItemsViewInsertion`, `ItemsViewInsertionPreview`) | `ItemsView.cs` |
| The lift-and-project list + policy seams (`DragStyle`, `CanAcceptForeign`, `ForeignRefusalCaption`, `ForeignCaption`, `RequireDropOnList`), `Item()` / `List()` / `InsertionLine()`, the keyboard lift | `Reorderable.cs` |
| The reorder math: dwell, midpoint slots, `BlockLength` / `BeginBlock`, `ProjectOrder`, `Move<T>`, `Sample`/`SlotAtOffset`/`BoundaryOffset`, 2-D grid mode | `ReorderList.cs` |
| Self-restyling file/whole-window drop surface (dashed accent ring) | `DropZone.cs` |
| Tab items carrying a `Drag`/`DropTarget` (the whole header is the spring-load hover area) | `TabStrip.cs` |

## Wavee app — `src/apps/Wavee`

The app never touches geometry. It owns payloads, rules, captions and commit seams.

| Task | File |
|---|---|
| The one drag kind (`WaveeDragKinds.Resource`), the payload envelope + its factories, `WaveeResourceDrag.Preview` (the single `DragChip.Resolve`), rootlist queries, the deposit/move commit seams (`WaveeResourceDrop.DepositTracksAsync` etc.) | `Features/DragDrop/WaveeResourceDrag.cs` |
| Engine-free decision tables — kind mapping, `PlaylistDropRefusal` + `PlaylistDropRefusalRules.Evaluate/Accepts`, `TabDropRules` | `Features/DragDrop/WaveeDragRules.cs` (unit-tested in `Wavee.Tests/WaveeDragRulesTests.cs`) |
| Chip data resolution (`WaveeResourceKind`, `WaveeDragChipModel.For`) | `Features/DragDrop/WaveeDragChipModel.cs` |
| The cards drawn inside the framework-owned insertion gap | `Features/DragDrop/PlaylistInsertionPreview.cs` |
| The app's ONE `DragPreviewLayer` mount + the scrim clip region + tab drag source (`thresholdMultiplier`) + `TabDropTarget` (deposit arm **and** spring-only arm) + the shell-wide OS file drop | `Features/Shell/WaveeShell.cs` |
| The page-body append target (`PageDropTarget`: transparent for same-list transit and for non-editable album/show surfaces) | `Features/Detail/DetailShell.cs` |
| The one app-side `InsertionOptions` + all its delegates (`DropVerdict`, `DropSourceDisplayRows`, `OriginalInsertionIndex`, `DepositAtAsync`, captions), plus the track-row drag source and the per-row `.mp4` file target | `Features/Detail/DetailTracks.cs` |
| Play-next drop target on the transport centre + the now-playing drag source | `Features/Shell/PlayerBar.cs` |
| Queue `Reorderable` (Stationary style, `RequireDropOnList`, foreign gate/captions) + `RefusingLane` for non-user-owned sections | `Features/Player/QueuePanel.cs` |
| Sidebar: the per-band `Reorderable`s, `ResourceDropSpec` (accept/hover/caption/refusal/spring-load), rootlist placement, the pin drop zone, the entity row's `Draggable` (with `ClickPrimaryThresholdMultiplier`) | `Features/Sidebar/Pane/{SidebarPane,SidebarPaneSlot}.cs`, `Features/Sidebar/Shared/{SidebarEntityRow,SidebarPinDropZone}.cs` — read the `wavee-sidebar` skill first |
| Curated customizer / top-bar reorder (their **own** drag kinds — an outline row is a document section, never an entity) | `Features/Sidebar/Curated/{SidebarOutlineView,SidebarTopBarCard}.cs` |
| Shelf-card and result-row drag sources (home, browse, search, library, artist, concerts, detail trailing) | `Features/{Home,Browse,Search,Library,Concerts}/*.cs`, `Features/Detail/{ArtistPage*,ArtistPopular,DetailTrailing}.cs` |
| Row identity for per-row UI state next to a draggable row (`RowKey` / `RowKeyMatches`) | `Components/MembershipDiff.cs` |

## Canon + tests

| Task | File |
|---|---|
| The drag CONTRACT + all policy + the honest known-limits list | `docs/design/subsystems/input-a11y.md` §12 |
| The CONTROLS surface (facade, chip, `InsertionOptions`, `SortableMath`, `BlockLength`, `Reorderable` policy) | `docs/design/subsystems/controls.md` §7.4 |
| Band order + the scrim/`EraseRoundRectCmd` pixels | `docs/design/subsystems/gpu-renderer.md` §7.4 |
| Precedence + the ownership row for the lift contract | `docs/design/SPEC-INDEX.md` |
| Developer-facing usage | `docs/guide/components-elements-layout.md` (DnD + `ItemsView` sections) |
| Gates | `src/FluentGpu.VerticalSlice/Suites/{ControlsSuite,ScrollSuite,TouchSuite}.cs` — see [gates.md](gates.md) |
| App-rule unit tests | `src/apps/Wavee.Tests/{WaveeDragRules,PlaylistReorderRules,WaveeDragChipModel,MoveRowsConvention,PlaylistMoveOps}Tests.cs` |
