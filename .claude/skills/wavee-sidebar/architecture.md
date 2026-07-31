# Wavee sidebar — architecture

Every path, type and member below exists on disk. Paths are relative to the repo root
(`C:\wavee\fluent-gpu`).

---

## 1. Layer 1 — the document (`src/apps/Wavee.Core/Sidebar/`)

Framework-neutral by construction: no FluentGpu type appears in any shape (glyph *names* live here, glyph
*codepoints* app-side), so `Wavee.Tests` drives it without pulling the engine in.

| File | Owns |
|---|---|
| `SidebarLayoutModel.cs` | The payload model + all per-kind facts. `SidebarSectionKind` (0…12, `Extension = 12`), `SidebarSectionSpec`, `SidebarItemSpec`, `SidebarDisplayOptions`, `SidebarEntityQuery`, `SidebarCustomLayout`, `SidebarExtensionRef`, `SidebarActionBinding`, `SidebarJson`, `SidebarIconNames`, `SidebarIds`, and **`SidebarSectionKinds`** — the single-owner table of per-kind rules (`DefaultTitleLocKey`, `PaletteNameLocKey`, `DefaultDisplay`, `EmptyBehaviorFor`, `AcceptsItems`, `SupportsLibraryQuery`, `RequiresExtensionRef`, `ItemCapacity`, `AllowsDisplayField`, `IsNestable`, `IsKnown`/`MaxKnown`). |
| `SidebarLayoutCommands.cs` | The 18 command records + `SidebarUndoLabels` (20 loc-key consts — hidden/shown and collapsed/expanded each get their own label) + `SidebarCommandResult` + `SidebarRejectReason`. Commands are in-memory only and never serialized, which is why they *are* a record hierarchy while the payload model is not. |
| `SidebarLayoutReducer.cs` | The one pure `(layout, command) → SidebarCommandResult`. Enforces caps (`MaxSections`, `MaxItemsPerSection`, `SidebarExtensionRef.MaxConfigBytes`), depth-1 nesting, query legality repair, icon-name validation. |
| `SidebarUndo.cs` | The 50-step ring of **pre-image snapshots** (not inverse commands): the document is immutable records, so an edit rebuilds only the spine and structurally shares the rest. That is why `ApplyTemplate`/`ResetLayout` need no special machinery. |
| `SidebarTemplates.cs` | The five seed layouts: `Curated` (`"curated"`), `ClassicInspired` (`"classic"`), `V3Inspired` (`"library"`), `Minimal` (`"minimal"`), `Blank` (`"blank"`); `All`; `Build(templateId)` falls back to Curated for an unknown id. |
| `SidebarLayoutCompare.cs` | Structural diff, used by the reducer's NoChange detection and the tests. |

**Why the model is one closed record per section, discriminated by a `Kind` byte** (not a `[JsonDerivedType]`
hierarchy): it must survive AOT source-gen serialization with zero reflection risk, must round-trip a section kind
a *future* build introduces, and the property panel wants one uniform shape to edit.

`JsonElement` has no content equality (record `Equals` would compare the backing document by reference), so
`SidebarExtensionRef`, `SidebarActionBinding` and `SidebarEntityQuery` all declare `Equals`/`GetHashCode` **by
hand** and compare canonical JSON (`SidebarJson.Same`) / ordinal uri lists (`SidebarEntityQuery.SameUris`).
Without that, every load looked like an edit.

---

## 2. Layer 2 — the service (`Features/Sidebar/SidebarPreferences.cs`)

The one owner of sidebar state, provided at the **app root** (`WaveeApp.cs:242`,
`Ctx.Provide(SidebarPreferences.Slot, _services.Sidebar, …)`) and constructed in `App/Services.cs:239`:
`new SidebarPreferences(settings, SidebarLayoutStore.ForApp())`.

Its surface is deliberately **FLAT** (not grouped — `prefs.V3Filter`, never `prefs.V3.Filter`):

- design + geometry: `Design`, `Tiers`, `SwitchDesign(next)`, `Width`, `Collapsed`, `WidthUserSet`,
  `CommitWidthDrag`, `SetCollapsed`, `SetResponsiveWidth`, `ResetWidth`, `SetViewportWidth`
- Classic sections: `ClassicPinnedOpen`, `ClassicLibraryOpen`, `ClassicPlaylistsOpen`,
  `SetClassicSection(ClassicSection, bool open)`
- V3 view state: `V3Filter`, `V3Qualifier`, `V3Sort`, `V3Desc`, `V3View`, `V3GridSize`, `V3SearchOpen`,
  `V3Search` + `SetV3Filter/Qualifier/View/GridSize/Sort/SearchOpen`; local custom order via `CanReorderV3`,
  `V3CustomOrder`, `V3OrderVersion`, `V3RankOf(id)`, `SetV3CustomOrder(orderedIds)`
- folders: `IsFolderExpanded`, `ExpandedFolders`, `FolderVersion`, `SetFolderExpanded`, `ToggleFolder`
- pins (shared across all three designs): `Pins` (`SidebarPinStore`), `PinsVersion`, `IsPinned`, `Pin`,
  `Unpin`, `InsertPin`, `MovePin`, `TouchPin`
- the document: `Layout`, `LayoutVersion`, `Dispatch(command)` (alias `ApplyCurated`), `ApplyTemplateId`,
  `CanUndo`/`CanRedo`/`UndoLabel`/`RedoLabel`/`Undo()`/`Redo()`
- projection: `Entries` (`SidebarEntries` — `Buffer`/`Current`/`Version`/`State`/`QualifiersAvailable`/`PinCount`),
  `Binder`, `PublishFirstSeen`, `FirstSeen`
- health: `PersistenceHealth`, `Fault`/`FaultDetail` (load), `SaveFault`/`SaveFaultDetail` (save, does not latch),
  `DiscardCorruptDocument()`, `Flush()`

Per-mode remembered state (locked decision 3) lives in the pure helper `SidebarPaneState`
(snapshot outgoing / restore incoming) behind `SwitchDesign`; per-design width tiers live in `SidebarDesignInfo`
(`Slug`, `Tiers`, `MountKey`, `FromInt`, `Count`) in `Features/Sidebar/SidebarDesign.cs`.

---

## 3. Layer 3 — persistence (`Features/Sidebar/Persistence/`)

One versioned, source-generated JSON document at
`%LOCALAPPDATA%\Wavee\WaveeMusic\sidebar-layout.json` — beside `history.json` (locked decision 8).

| File | Owns |
|---|---|
| `SidebarLayoutDoc.cs` | The wire DTOs (`SidebarLayoutDocDto`, `SidebarPinDto`, `SidebarV3Dto`, `SidebarCuratedDto`, `SidebarSectionDto`, `SidebarItemDto`, `SidebarDisplayDto`, `SidebarQueryDto`, `SidebarExtensionDto`, `SidebarActionDto`), the AOT context `SidebarLayoutJsonCtx` (camelCase + `WhenWritingNull` + `WriteIndented`, declared on the context so no call site carries loose options), and **`SidebarLayoutWire`** — the one enum ⇄ string translation layer, plus `SidebarWireCarry`/`SidebarCuratedRead` (`ReadCurated`/`WriteCurated`). |
| `SidebarLayoutStore.cs` | `CurrentVersion = 2`, `MaxDocumentBytes = 2 MiB`, `DefaultPath()`, `ForApp()`, atomic temp→`File.Replace` with one rotated `.bak`, load-fault classification (`SidebarLoadFault`) and save-fault classification (`SidebarSaveFault`). |
| `SidebarLayoutMigrations.cs` | `Upgrade(dto)` — v1→v2 is an **identity** migration; an existing document loads unchanged and stamps `"version": 2` on its next ordinary save. |
| `SidebarLayoutDefaults.cs` | `CuratedLayout()` — the fallback document a probe/harness mount with no preference service uses. |

**Forward compatibility is a hard contract**, by two mechanisms:

1. Section kinds are **strings** on the wire (`"pinned"`, `"jumpBackIn"`, …, `"extension"`). An unrecognized kind
   string round-trips *untouched* as an opaque blob at its original index and renders as nothing.
2. Unknown **members** anywhere in the tree are captured by `[JsonExtensionData]` and re-attached on write,
   matched by the owning section/item id.

`SidebarPreferences` must thread the carry: `ReadCurated` on load (`_layout` + `_carry`) and `WriteCurated(_layout,
_carry)` on **every** snapshot. Drop the carry and you silently delete a newer build's data.

Missing `version` ⇒ treated as **malformed** (not v1). `version > CurrentVersion` ⇒ `TooNew`. A corrupt primary
falls back to `.bak`, then to the Curated default *in memory* — the bytes on disk are preserved, and the customizer
surfaces the fault. `SaveFault` does not latch: over-budget `Commit()` no-ops, so in-memory state runs ahead of
disk until the document shrinks.

Scalars (width/collapsed/design/V3 view state/onboarding markers) live in `IAppSettings` under the
`SidebarKeys` table, not in the JSON document. `App/SidebarBootstrap.cs` (`Run(settings)`, called from
`Program.cs:47`, `TargetVersion = 1`) does the fresh-install probe + legacy pane-key migration before `Services`
exists: fresh installs default to **Curated** and see the chooser once; existing installs (library.db /
credentials / onboarding marker) silently stay on **Classic** and never see it.

---

## 4. Layer 4 — the data pipeline (`Features/Sidebar/Data/` + `SidebarProjectionBinder.cs`)

### The engine-free half (source-included by `Wavee.Tests` — `Data\*.cs`, one level deep)

| File | Owns |
|---|---|
| `SidebarLibraryEntry.cs` | The unified row record every source produces (`Id`, `Uri`, `Kind`, `Name`, `Creator`, `Cover`, `MosaicTiles`, `Depth`, `FolderId`, `SourceOrder`, `SortStamp`, …). `ForRoute(routeKey, name)` mints an app-route row. |
| `SidebarProjection.cs` | `Build(buffer, …)` — the unified projection over `LibraryStore` + `HistoryStore`; `PinsFirst` (returns `PinCount`); `QualifiersAvailable(flavorMask)`; `MatchesQualifier`. |
| `SidebarSort.cs` | The five comparators (`Recents`, `RecentlyAdded`, `Alphabetical`, `Creator`, `CustomOrder`) behind `Apply`. |
| `SidebarSearch.cs` | Normalization + diacritics folding. `InvariantGlobalization=true` ⇒ collator probe + a Latin-1 fold fallback (folding is limited to Latin Extended-A in invariant mode — a stated limitation). |
| `SidebarRecency.cs`, `SidebarFirstSeen.cs`, `SidebarPinId.cs` | Visit recency; the first-projection stamp used as the playlist added-at proxy; the pin-id ⇄ kind/uri scheme. |
| `SidebarRowPlanner.cs` | **The planner.** `SidebarRowKind` (13 kinds), `SidebarRow` (POD — no string is allocated during planning), `SidebarProjectionInput`, `SidebarSourceState`, `SidebarSectionSlice`/`ISidebarSectionSlices`, `SidebarPlanBuffers`, `Build(doc, input, buffers)` and `BuildRail(…)` (`RailTileCap = 40`). |
| `SidebarDataSource.cs` | The contribution contracts: `ISidebarDataSource`, `SidebarDataSourceBase`, `ISidebarContributionHost`, `SidebarContributions`, `SidebarConfigSchema`/`SidebarConfigField`/`SidebarConfigFieldKind`, `SidebarSourceConfig` (never-throwing typed readers over opaque JSON), `SidebarSourceRequest`, `SidebarContributionAvailability`, and the declared-capability enums (`SidebarSourceItemType`/`Filters`/`Sorts`/`Paging`). |
| `SidebarSourceMap.cs` | The pure mappers every adapter is built out of: domain record → `SidebarLibraryEntry`, plus service-health → `SidebarSourceState`. Everything that can be *wrong* lives here so the tests reach it. |
| `SidebarBinderPipeline.cs` | The pure half of the binder: `SidebarBinderTriggers` (12 lanes + `Fold()`), the filter/qualifier/search compaction, the sort + pins-first shaping, contribution resolution (missing/disabled/incompatible verdicts), `SidebarContributionCache` (the last-good snapshot replay — M3's stale-badge seam), `SidebarExtensionSlices`, `SidebarSourceIndex`. |

### The impure orchestrator

`Features/Sidebar/SidebarProjectionBinder.cs` holds the stores, signals and the UI-thread marshaller and **nothing
that decides anything**. It is the missing `Entries` rebuild driver: one unified projection over `LibraryStore`'s
warm cells + `HistoryStore` recency + `PlayLogStore` + the pin store, rebuilt whenever any of them moves; the
published entry list; the first-seen commit; the contribution slices; and `CurrentInput` — the
`SidebarProjectionInput` every pane hands to the planner. `RecencyCap = 40`. All rebuild buffers are allocated once
and reused.

**Why a mounted pump.** A `ReactiveRuntime` is not reachable from a plain service, so a service cannot own an
`Effect` and cannot observe a `Signal<T>`. `binder.MountPoint()` therefore returns a zero-size always-mounted
component that *reads* every trigger signal in its render (subscription only) and calls `Sync()` from a
`UseEffect` keyed on `SidebarBinderTriggers.Fold()`. It is mounted **once at the app root**
(`WaveeShell.cs:782`), not inside the sidebar — the docked pane and the drawer come and go, the projection may not.

### Adapters (`Data/Sources/` — NOT source-included; they hold engine-bound services)

`WaveeBuiltInDataSources.cs` (`RegisterAll`, `Publish`, `Attach`, `ContributionHost`),
`SidebarProjectionSources.cs`, `SidebarFeedSources.cs`, `SidebarPlaybackSources.cs`. Nine first-party sources,
ids in `SidebarContributions`:

`wavee.library` · `wavee.history.visited` · `wavee.history.played` · `wavee.playlistTree` ·
`wavee.artist.topTracks` · `wavee.newReleases` · `wavee.concerts` · `wavee.queue` · `wavee.nowPlaying`

Wiring, verbatim from `App/Services.cs`:

```csharp
:239  Sidebar = new SidebarPreferences(settings, SidebarLayoutStore.ForApp());
:262  PlayLog.Init(PlayLogStore.DefaultPath());
:263  PlayLog.LoadFromDisk();
:264  Playback.AttachPlayLog(PlayLog);
:271  SidebarBinder = new SidebarProjectionBinder(Sidebar, LibraryStore, PlayLog, Playback);
:272  SidebarSources = WaveeBuiltInDataSources.RegisterAll(registrar: null, SidebarBinder, library,
                          ArtistPopularTracks, WhatsNew, Concerts, Playback);
:274  SidebarBinder.UseHost(new WaveeBuiltInDataSources.ContributionHost(SidebarSources), SidebarSources);
:275  Sidebar.Binder = SidebarBinder;
:313  public void RegisterSidebarSources(WaveeExtensionRegistry registry)
:314      => WaveeBuiltInDataSources.Publish(registry, SidebarSources);
```

**Threading:** UI thread only, unsynchronized. An adapter that completes an async fetch **must** marshal back
through the `post` the binder handed it before touching `State` or raising `Changed`. Re-entrancy is fenced: a
source that raises `Changed` mid-rebuild only marks the binder dirty.

---

## 5. Layer 5 — the ONE renderer (`Features/Sidebar/Pane/`)

| File | Owns |
|---|---|
| `SidebarPane.cs` | The component. Subscribes the epochs, plans in a `UseMemo` keyed on `PlanDep`, publishes the plan as a **plain field** to the bound slots, drives the count signal from a layout effect, owns reorder bands + drop-to-pin + the collapse/expand choreography, and hosts the rail. |
| `SidebarPaneSlot.cs` | One bound slot and the whole 13-kind row vocabulary. A Component per slot, because `ItemsView.CreateBound` builds a slot once and recycles it by writing `scope.Index` — a render that reads `Index.Value` re-renders exactly on a recycle. |
| `SidebarPaneRail.cs` | The 56-DIP rail. Content is *data* (`ShowInRail` sections, per `BuildRail`), not code, so Classic's rail and Curated's rail cannot drift. Not virtualized — the planner caps it at 40 tiles. |
| `SidebarPaneText.cs` | The pure display rules: `TitleOf`, per-kind `SubtitleOf`, item lookup, icon fallbacks, the "never render a blank row" degradations. |
| `SidebarPaneInlineControls.cs` | An `EntityList` section's inline kind-chips + sort/view trigger, rendered as header chrome. Every edit rewrites *that section's persisted spec* through `SetQuery`/`SetDisplayOption`, so it is undoable and survives a restart. Suppressed when `ReadOnly`. |
| `SidebarPaneMetrics.cs` | `PanePad (8,8,8,12)`, `PaneInsetH 16`, `RowInset`, `SectionGap`, `HeaderBodyGap 2`, `EmptyHintHeight 32`, `GridCellMax 160`, `RowHeight(section)`, `ArtSize(section)`, `CardHeight`/`CardCover`. |
| `SidebarPaneConfig.cs` | The seam (below) + `SidebarPaneReorder` + `SidebarPaneReorderCommit.Default`. |
| `SidebarBuiltInDocuments.cs` | Classic as a locked document. |

### How a frame flows

1. The pane subscribes document + projection + pin + folder + search + **mode** epochs and re-plans in a `UseMemo`
   keyed on their fold (`PlanDep`). The planner is pure and reuses ONE caller-owned `SidebarPlanBuffers` **per
   pane** — the expanded pane and the rail each own their own instance, because a plan *aliases* its buffers.
2. The plan is published to the bound slots as a **plain field** (never a signal write from `Render` — the
   render-purity rule). Each slot re-reads it at *its* render time and calls `SidebarPane.SubscribeEpoch()` so a
   projection rebuild / customizer edit / section toggle / keystroke re-skins the realized window without the list
   rebuilding.
3. The row **count** is the one thing the frozen-at-mount `ItemsView` cannot read from a field: it rides
   `CountSignal`, written in a layout effect (a render-time write would be a backwards write, and the DEBUG
   `ReuseGuard` explicitly rejects a changed frozen `ItemCount`). The very first frame seeds it once, before the
   list exists — provably not a backwards write because nothing has read it yet.

### `SidebarPaneConfig` — the only mode seam

| Member | Type | Meaning |
|---|---|---|
| `Design` | `SidebarDesign` (required) | Log field + scroll/telemetry identity **only**. The renderer never branches on it. |
| `ScrollKeyPrefix` | `string` (required) | The pane appends `".drawer"` for the drawer mount so the two never fight over one saved offset. |
| `Document` | `Func<SidebarCustomLayout>` (required) | The live document. Invoked inside the pane's render. |
| `Input` | `Func<SidebarProjectionInput, SidebarProjectionInput>?` | Fold the mode's own filter/sort/search state into the planner input. The pane still applies its own search-head override *on top*. |
| `ModeEpoch` | `Func<int>?` | Mode-owned state folded into one int; read in the plan `DepKey` *and* the per-row epoch. Read signals with `.Value` here — that read **is** the subscription. |
| `SetSectionCollapsed` | `Action<string,bool>?` | Where collapse state lives. Null ⇒ non-collapsible headers. |
| `ReadOnly` | `bool` | Suppresses inline `EntityList` controls, the missing-entity "Remove" verb and the empty-pane customize CTA; `Dispatch` becomes a no-op. |
| `SearchHead` | `bool` | Render the pane-owned library-only search box (only when the document actually contains a visible `EntityList`). |
| `Head` | `Func<Element?>?` | Arbitrary mode chrome above the scroll surface (V3's header/toolbar/chips/breadcrumb). Rendered before `SearchHead`. |
| `ShowLayoutMenu` | `bool` (default true) | Hang the quick layout menu off the pane's **first** section header. |
| `RailLayoutMenu` | `bool` (default true) | Put it at the bottom of the rail too. |
| `RailFooter` | `Func<Element?>?` | An extra rail affordance after the planned tiles (Classic's create-playlist "+"). |
| `ActivateFolder` | `Action<string,string>?` | What activating a folder disclosure row does. Null ⇒ toggle the shared folder-expansion state. Replaces both the row's click **and** the expand/collapse verb in its context menu, so the two can never disagree. |
| `IsReorderableSection` | `Func<SidebarSectionKind,bool>?` | Default: `Pinned`/`StaticLinks`/`CustomGroup`. The rootlist is never written (locked decision 9). |
| `CommitReorder` | `Action<SidebarPaneReorder>?` | Null ⇒ `SidebarPaneReorderCommit.Default` (Pinned → the shared pin store; every other reorderable kind → the undoable `MoveItem`). |
| `OnCustomize` | `Action?` | Null ⇒ those surfaces render without their action rather than with a dead one. |
| `OnCreatePlaylist` | `Action?` | Null ⇒ the create row is still planned but inert. |

`SidebarPaneReorder(Section, FromSlot, ToSlot, SlotCount, KeyAt)` carries everything a commit could need and
nothing about the widget: the renderer knows the geometry, only the mode knows where the order *lives*.

### Selection and motion (one mechanism for every mode)

Rows recycle, so a node-handle map keyed by route is not stable and Classic's measured overlay pill
(`SidebarSelectionPill`) cannot work. Selection is drawn **inside** the row: the shared 4-state ramp
(`SidebarEntityRow`) plus a 3×16 accent indicator over the row's own 3-DIP selection gutter, cross-faded on
`MotionTok.ControlFaster`. This matches WinUI's per-`NavigationViewItem` SelectionIndicator.

| Gesture | Token / mechanism |
|---|---|
| design switch | `MotionTok.ControlFast` + `Enter(Opacity: 0)` on the keyed remount (`SidebarHost`) |
| section chevron | `SidebarChevron` — one glyph + animated `Rotation` on `MotionTok.ControlFast` |
| collapse / expand | `SidebarPane.Choreograph` seeds `EntranceOptions.ItemFadeFrom` (fade + 6-DIP rise, 16 ms/row stagger capped at 8) and `ItemFlipFrom` (FLIP glide for displaced rows), then bumps `_dispVersion` |
| reorder | `MotionTok.ItemPlacement` through `Reorderable` (`LiveProject = false`, `ShowInsertionLine = false`, displacement channel) |

Reduced motion is **not** branched on in authoring code — seeds go through the scheduler under a named token,
which reads the preference as a *value*.

### Shared primitives (`Features/Sidebar/Shared/`)

`SidebarEntityRow` (+ **`SidebarRowMetrics`** — the ONE height/art/indent ladder: `ClassicHeight 44`,
`HeightFor(density, hasSubtitle)` = 32/40|44/44|48, `ArtFor` = 20/32/40, `IndentFor(depth)` = 6 + depth×12 capped
at 4) · `SidebarCounts` (**the one quiet badge**: 11f tertiary right-aligned number + a 20×12 shimmer plate while
pending — `InfoBadge.Count` is gone) · `SidebarChevron` · `SidebarCover` (`S20…S64`, `Radius`, `ForEntry`,
`ForPin`, `Art`, `Glyph`, and the bucketed decode ladder) · `SidebarSectionHeader` · `SidebarPinDropZone`
(`RestHeight 56` / `ActiveHeight 72`) · `SidebarRailItem` (`Box 40`, `ArtEdge 36`) · `SidebarSkeletons` ·
`SidebarSelectionPill` (see the deletion candidates in [pitfalls.md](pitfalls.md)).

---

## 6. The three modes

### Classic — a locked built-in document

`SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen)` returns
`SidebarCustomLayout` with `TemplateId = ClassicId = "classic.builtin"` (deliberately *not* in
`SidebarTemplates.All`) and **stable string section ids** — `classic.pinned`, `classic.library`,
`classic.playlists`, `classic.tools` + three dividers. Stable ids matter: the pane keys its reorder bands,
collapse routing and section identity off them, and `ClassicSectionOf(sectionId)` maps a header click back to the
right preference flag with no lookup table.

The section list is today's Classic IA verbatim: Pinned · rule · Your Library (albums/artists/liked/podcasts/local
with quiet counts) · rule · Playlists (artwork + song-count subtitle + the create row) · rule · the header-less
DevTools entry (`api-console`). The Display options are chosen so the ONE shared ladder reproduces Classic's
44-DIP rows exactly:

- Pinned / Playlists → `Entities` (Cozy + Subtitles) ⇒ 44 with 32-DIP artwork.
- Your Library / DevTools → `Shortcuts with { Density = Comfortable }` (no subtitle) ⇒ 44, matching what the
  retired `LibRow`/`DevToolsRow` hard-coded. `Artwork: false` keeps them 16-DIP glyph rows.

Classic's collapse state is **not** document state: three persisted preference flags, so the docked pane and the
drawer agree and the state survives a design round-trip. `WaveeSidebar.SectionEpoch()` folds them into
`ModeEpoch`.

The preservation contract (§3.1.1) is honoured at the **pixel** level, not the implementation level — the user's
explicit choice. Deliberate deviations, stated not hidden: selection is the per-row indicator; the pinned list is
virtualized and therefore uncapped (no "Show all (n)" row); pinned reorder uses the displacement channel rather
than a live projection; the rail order changed (pins → shortcuts → playlists ≤20 → API console → create → menu);
pane padding is fixed chrome (the top 8 DIP no longer scrolls away); the PlaylistTree create affordance is the full
`CreateAction` row (the header "+" is gone, the rail keeps one).

### Library V3 — a synthesized ephemeral document under its chrome

`LibraryV3Document.Build(in LibraryV3DocState)` is **pure and unit-tested**. `TemplateId = "v3.synth"`. Sections,
in order:

1. `"v3.pins"` / `Pinned` — only when `PinsBandVisible` (`HasPins && !Drilled && !Searching`).
2. `"v3.liked"` / `StaticLinks` — only when `LikedVisible`; one `Route` item at `liked` with icon `Heart`.
3. `"v3.library"` / `PlaylistTree` **or** `EntityList` — always. `PlaylistTree` when `FoldersApply` (list view,
   not searching, not drilled, All-or-Playlists filter); `EntityList` otherwise. `EmptyBehavior = HideBody`.

`LibraryV3DocState(Filter, Qualifier, Sort, Descending, View, GridColumns, Searching, DrillFolderId, HasPins,
LikedPinned, QualifiersAvailable)` maps onto `SidebarEntityQuery` (`KindsFor`, `SortFor` — Custom degrades to
Alphabetical outside the Playlists lens, `QualifierFor`) and `SidebarDisplayOptions`
(`PresentationFor`/`DensityFor`/`SubtitlesFor`/`ClampColumns` 2…4). `GridColumns` is *derived* from the measured
pane width, never chosen.

Its `SidebarPaneConfig`: `ScrollKeyPrefix = "sidebar.v3"`, `ReadOnly = true`, `SearchHead = false`,
`ShowLayoutMenu = false` (V3 embeds those rows in its own overflow menu), `Head = ChromeHead`,
`SetSectionCollapsed = null`, `ActivateFolder = session.ActivateFolder`, plus `IsReorderableSection` /
`CommitReorder` for the local custom order and a `RailFooter`. `OnCustomize` is never set — its document is
ephemeral, so there is nothing to customize.

`ShapeInput` sets `Pins`, clears `ExpandedFolders`, sets `SuppressTreeCreateRow = true`, and swaps
`session.View.Rows` into either `PlaylistTree` (grouped) or `Library` (flat) — **two windows over the one
published projection, no logic fork**.

`LibraryV3Session` owns the drill stack (`PushFolder`/`PopFolder`/`ResetDrill`, `DrillVersion`, `NarrowFolders`,
derived `Columns`), `ReadState()`, and the shared commands (`ActivateFolder`, `CreatePlaylist`, `ClearAllFilters`,
`Retry`, `Collapse`, `Expand`). `LibraryV3View` owns the **flat-sort → tree re-grouping** pass (a flat comparator
can put a nested playlist above its own folder), the drill slice, `SameParent`/`KeyAt`, and
`MaterializeOrder` for the local custom order. `LibraryV3Chrome` mounts the header band, toolbar, chip rails,
breadcrumb, retry banner and the actionable empty states through `Config.Head`.

Deliberate V3 losses in the unification (an R3.0 trade): the trailing pin marker, the folder-name caption on
search hits, the pin-band hairline, the grid pin badge, Alt+Shift reorder (the pane's keyboard lift replaces it),
per-view scroll keys (now one), the partial-load shimmer tail. The pin band dissolves while searching or drilled,
and V3 has no empty-pins drop card.

### Wavee Curated — the user's document + the customizer

`CuratedSidebar` is a thin shell: `Document = () => _prefs?.Layout ?? fallback`,
`SetSectionCollapsed = (id, c) => _prefs?.Dispatch(new SetSectionCollapsed(id, c))`, `ReadOnly = false`,
`SearchHead = true`, `OnCustomize`, `OnCreatePlaylist`. **Its ctor signature is frozen** — the customizer's live
preview mounts it (`Curated/SidebarInspector.cs:127`).

The customizer is a full-page route, `SidebarLayoutMenu.CustomizeRoute = "sidebar-customize"`, registered in
`Features/Shell/ContentHost.cs:126-128` and labelled in `Features/Shell/ShellNav.cs:47`. Its pure model
(`Curated/SidebarCustomizerLayout.cs`) is source-included by the tests and owns the tier ladder, the 18-entry
`SidebarPalette.All` table (grouped by `SidebarPaletteGroup`), outline flattening, drag translation and the
opaque-config rewriter (`SidebarConfigJson`). Live thresholds:

```csharp
PaletteWidth 232 · InspectorWidth 320 · PreviewWidth 360 · OutlineMinWidth 320
CanvasEnterW 1320 · FullEnterW 1000 · CompactEnterW 820 · HysteresisDip 24
enum SidebarCustomizerTier : byte { Canvas = 0, Full = 1, Compact = 2, Narrow = 3 }
```

Widening promotes immediately; narrowing needs `HysteresisDip` past the threshold. `PaletteInline(tier) => tier <=
Full`, `InspectorInline(tier) => tier != Narrow`, `PreviewInline(tier) => tier == Canvas`, and Narrow puts the
inspector in a bottom sheet (`SheetHeight`). Extension sections' property controls are **generated** from
`ISidebarDataSource.ConfigSchema` and written back through `SetExtensionConfig`.

---

## 7. The extension-ready layer

Registered contributions are the whole point of M1: **first-party is literally the trusted extension `"wavee"`.**
There is no privileged non-extension registration path.

### The registry

`Actions/Extensibility/WaveeExtensionRegistry.cs` — one registry for every contribution kind
(`WaveeRegistryTable<WaveeActionDescriptor>` + `WaveeRegistryTable<ISidebarDataSource>`).

- `static Context<WaveeExtensionRegistry?> Slot` · `static Current` · `static Build(ActionServices)`
- `Register(extensionId, IWaveeExtension)` / `Register(extensionId, Action<IWaveeExtensionRegistrar>)`
- `RegisterAction` / `RegisterDataSource` (both null-tolerant → a diagnostic, never an NRE)
- `Actions` / `Sources` / `Extensions` / `Diagnostics`
- `TryGetAction(key)` / `TryGetAction(binding)` / `TryGetSource(id)` / `HasAction` / `HasSource` / `KeyOf(binding)`
- `Resolve(services, binding)` → `WaveeActionTargetResolution` · `Execute(services, in binding)` →
  `WaveeActionUnavailable`

**Duplicate policy: first wins.** A second registration under a live key is refused and recorded in
`Diagnostics`; because `BuiltInExtensionTable` runs first, no third party can shadow a first-party contribution.
Nothing is ever *unregistered* — a disabled extension is filtered at the consumption site, and an unresolvable key
yields `WaveeActionUnavailable.ActionMissing` (a visible-but-disabled row with a reason).

**Threading:** UI thread only, unsynchronized, registration-then-read. No lock, no off-thread producer.

### The SDK seam

```csharp
public interface IWaveeExtension { void Register(IWaveeExtensionRegistrar registrar); }

public interface IWaveeExtensionRegistrar
{
    void RegisterAction(WaveeActionDescriptor descriptor);
    void RegisterDataSource(ISidebarDataSource source);
}
```

`Actions/Extensibility/BuiltInExtensionTable.cs` is the hand-written `RegisterAll(registrar, services)` — the M4
source generator emits this same call shape, so nothing needs rework. `ExtensionId =
WaveeExtensionKey.FirstPartyPublisher = "wavee"`. The 13 registered action keys:

`wavee.play` · `wavee.playNext` · `wavee.addToQueue` · `wavee.toggleLike` · `wavee.save` · `wavee.open` ·
`wavee.goToAlbum` · `wavee.goToArtist` · `wavee.copyLink` · `wavee.songRadio` · `wavee.artistRadio` ·
`wavee.pinToSidebar` · `wavee.unpinFromSidebar`

### `WaveeActionDescriptor` vs `AppAction` — different shapes, neither replaces the other

`AppAction` is the **context-menu** model: it acts on a live `ActionTarget` built at menu-open time, and its label
is count-aware. `WaveeActionDescriptor` is the **bound** model: it acts on a persisted `SidebarActionBinding`
whose target is a *mode plus a key*, so it must resolve the target itself, must be able to say **why** it cannot,
and must survive a restart. First-party descriptors therefore *wrap* the existing `ActionId` verbs (recorded in
`LegacyId`, diagnostics only); the `ActionId` enum stays internal and never appears on the wire.

Members: `Key`, `LabelLocKey`, `IconKey` (resolved through `ActionIcons.Resolve` — never a raw glyph),
`AcceptedTargets` (`WaveeActionTargetModes`), `ArgumentSchema` (opaque JSON), `IsEnabled`, `IsChecked`,
`Destructive`, `RequiresConfirmation` + `Confirm{Title,Body,Primary}LocKey`, `RequiredPermissions`
(`WaveePermissions.*` — recorded but **unenforced until M3**), `LegacyId`, `Run(services, binding, resolution)`.
`Resolve(services, binding, peek)` folds *every* reason a row can be disabled into one call, so the row's disabled
state and `Execute`'s refusal can never disagree. `RequiresConfirmation` routes through `SettingsShared.Confirm`
and **refuses** to run when there is no overlay — a null overlay never degrades into an unconfirmed run.

`WaveeActionUnavailable`: `None`, `ModeNotSupported`, `MissingTargetKey`, `NoNowPlaying`, `NoActiveRoute`,
`ActionMissing`, `HostUnavailable`, `NotApplicable`.

### Data sources

`ISidebarDataSource` declares its **capabilities** (`ItemType`, `SupportedFilters`, `SupportedSorts`, `Paging`)
so the customizer offers only facets the source honours rather than offering some and silently ignoring them; its
`ConfigSchema` is what the inspector generates controls from; and its `State`/`StateDetailLocKey`/`NeedsPrompt`
are the health the planner turns into rows. `Fill(into, in request)` appends on the rebuild path — no LINQ, no
closures, no per-row allocation, never a blocking wait.

Health is a plain property + a plain `Changed` event, **not** a `Signal<T>`, because `Data/` must stay engine-free
for the tests. `SidebarDataSourceBase` gives you `SetHealth` (publish + notify) and `SetHealthQuiet` (the only
setter a `Fill` may use — raising `Changed` from inside a rebuild would re-enter the binder).

Degraded-state → row mapping (the platform doc's failure matrix, collapsed into the planner):

| Availability / state | Planner emits |
|---|---|
| `Missing` / `Disabled` / `Incompatible` | one `PromptRow` ("Manage extension"), section keeps its spec |
| `NeedsPrompt` (e.g. Concerts with no location) | one `PromptRow` |
| `Pending` + 0 rows | `Skeleton` rows |
| `Ready` + 0 rows | `Empty` / `CompactHint` per `EmptyBehaviorFor` |
| `Cached` | last-good slice replayed (the M3 stale-badge seam) |

### Two key spaces — do not unify them

| Form | Example | Owner |
|---|---|---|
| **Contribution key** (slash) | `"wavee/artist.topTracks"` | `SidebarExtensionRef.ContributionKey` — the registry lookup key composed from the ref |
| **Source id** (dot) | `"wavee.artist.topTracks"` | `ISidebarDataSource.Id`, composed/split only by `SidebarContributions.SourceId` / `ContributionOf` |
| **Action key** (dot) | `"wavee.play"` | `SidebarActionBinding.ActionKey` = `ProviderId + "." + ActionId`; `WaveeExtensionKey.Compose` |

Both `SourceId` and `Compose` are **idempotent**: an already-fully-qualified id is taken as-is rather than
double-prefixed, so a hand-edited or older document that stored `"wavee.library"` in the contribution slot still
resolves.

`SidebarContributions.WaveeExtensionId` and `WaveeExtensionKey.FirstPartyPublisher` are the same literal `"wavee"`
declared twice. The code's stated reason (`SidebarDataSource.cs:295-300`) is the test build's source-include
direction: `Data/` may not depend on `Actions/Extensibility/`. The single-owner fix it names is to make
`FirstPartyPublisher` alias `WaveeExtensionId` — not the reverse. (Three files from `Actions/Extensibility/` are in
fact source-included today, so the constraint is looser than the comment implies; the fix direction still stands.)

### Budgets

| Budget | Value | Enforced by |
|---|---|---|
| per-section extension config | 64 KiB (`SidebarExtensionRef.MaxConfigBytes`) | the reducer (`ConfigTooLarge`), re-checked at save |
| whole document | 2 MiB (`SidebarLayoutStore.MaxDocumentBytes`) | the write path (a save **fault**, never a truncation) |
| registry key length | 128 (`WaveeExtensionKey.MaxLength`) | `IsValid` — a key is persisted, so unbounded is a document-size hazard |
| uris per include/exclude set | 500 (`MaxUrisPerSet`) | the reducer — **truncate**, not reject |
| sections per document | 40 (`SidebarLayoutReducer.MaxSections`, top level + children) | the reducer (`SectionCapReached`) |
| items per section | 500 (`MaxItemsPerSection`); `EntityEmbed` = 1 | `SidebarSectionKinds.ItemCapacity` |
| rail tiles | 40 (`SidebarRowPlanner.RailTileCap`); tree tiles capped at `RailTreeTiles = 20` per pane | the planner |
| recency feeds | 40 (`SidebarProjectionBinder.RecencyCap`) | the binder |
| undo ring | 50 steps, in memory only | `SidebarUndo` |

### Forward-compat guardrails (so M3–M5 bolt on without rework)

1. Registry interfaces are already the SDK's shape; a sandboxed extension never implements `IWaveeExtension`
   in-process — its manifest contributions are **replayed onto the same registrar** by the host.
2. No `AppActions.All` lookups. Section rendering resolves contribution ids through the host; the planner never
   sees an extension id and never switches on one.
3. Unknown refs / configs / kinds / members round-trip untouched.
4. The binder exposes a per-contribution cached-snapshot seam and per-source health — both already surfaced as
   planner states.
5. Runtime extension state and secrets never enter the layout document.
6. Permissions are declared honestly today and enforced in M3, so the table needs no re-authoring.

**What is NOT built** (and must not be claimed): M3's sandboxed extension host / worker isolation, M4's public SDK
+ the source generator that would emit `BuiltInExtensionTable`, M5's hardening. There is no extensions page —
"Manage extension" navigates to the customizer. `RequiredPermissions` is inert. `ArgumentSchema` is opaque. Nothing
untrusted executes in Wavee, and `docs/plans/wavee/wavee-sidebar-extension-platform.md`'s `## Boundaries` makes
built-in visual completion the gate on further extension-host work.
