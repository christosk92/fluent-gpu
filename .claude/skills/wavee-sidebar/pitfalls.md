# Wavee sidebar — pitfalls and known issues

Every entry below was re-verified against the code after the unification landed. Where the build's own working
notes were wrong, the correction is called out — **trust this file over the handoff notes**.

---

## Localization

### `sidebar.pin.pinTo`, not `sidebar.pin.pin`

The generated table nests a dotted key into static classes, PascalCasing each segment
(`src/FluentGpu.SourceGen/Localization/LocalizationKeysGenerator.cs` → `Identifiers.ToPascal`). A leaf whose
PascalCase name equals its **enclosing group's class name** would emit `public const string Pin` inside
`static class Pin` — a C# error (a member may not have the same name as its enclosing type). The key was therefore
renamed: the live key is `sidebar.pin.pinTo` and the member is `Strings.Sidebar.Pin.PinTo`
(`Actions/PinActions.cs:28`, `:75`; `Actions/Extensibility/BuiltInExtensionTable.cs:247`). **Before naming a key,
check it does not collide with its own group.** There is no comment in the tree recording this — the evidence is
the key spelling itself.

### `_Self` is the generator's *prefix*-collision escape, and the customizer does not use it

When a dotted key is also a **prefix** of another key (`sidebar.customizer.undo` next to
`sidebar.customizer.undo.addSection`), the intermediate node must be both a class and a const, so the generator
emits the const as `_Self` (`LocalizationKeysGenerator.cs:199-207`). That rule genuinely fires for
`sidebar.customizer.undo`.

But `Strings.Sidebar.Customizer.Undo._Self` is **not** what the code uses, and neither is
`Strings.Sidebar.Customizer.Undo.AddSection` — there are zero `Strings.Sidebar.Customizer.*` references in
`src/apps`. The undo *button* label is a hand-written literal const, `CzLoc.Undo = "sidebar.customizer.undo"`
(`Curated/SidebarCustomizerPage.cs:841`), and the *command* labels are literal consts in
`SidebarUndoLabels` (`Wavee.Core/Sidebar/SidebarLayoutCommands.cs:15-37`) because Wavee.Core cannot see the
generated table. That is deliberate and documented at `SidebarCustomizerPage.cs:817-820`. Follow the landed
pattern; do not "fix" it to the generated members.

### Loc file mechanics

- Three files, and only three: `src/apps/Wavee/assets/loc/{en-US.json, nl.json, ko-KR.json}`.
- `nl` and `ko-KR` are **partial overrides** — 495 keys each against en-US's 1278 — resolved by the engine's
  per-key fallback chain (active → parent → default → the key itself, `FluentGpu.Engine/Localization/Localization.cs:212-224`).
  A key missing from `nl`/`ko` is legal; a key missing from **en-US** renders visibly as `[key]`.
- The files are **CRLF, UTF-8 without BOM**. Keep it that way — an editor that "helpfully" adds a BOM or converts
  to LF produces a diff nobody wants to review.
- The shape is nested objects, but **literal dotted keys inside a nested object are also legal** (e.g.
  `"undo.addSection"` sits directly inside the `customizer` object). The generator flattens, then splits on `.`,
  so both spellings produce the same member path.
- Old keys survive in stale `bin/` output. Grep `src/apps/Wavee/assets/loc/`, not the whole tree, when deciding
  whether a key still exists.

---

## Components and reactivity

### Props freeze at mount — that is why the config is all delegates

`SidebarPaneConfig` is built once in `UseMemo(…, DepKey.Empty)` and frozen into the pane's ctor. A **value**
member would pin frame 1's state forever. `Document`, `Input`, `ModeEpoch`, `Head`, `RailFooter` are providers the
pane invokes inside *its* render — which is also the only reason the signals they read subscribe the pane. The mode
components refresh their `_prefs`/`_lib` fields every render and the frozen delegates read those fields, so the
config always sees live services (service instances are reference-stable).

The same trap bites the customizer's controls: a frozen `ComboBox`-style control cannot be told a new value, so
where one is unavoidable the landed workaround is a **remount key** derived from the value. Prefer the controlled
pattern below.

### Controlled controls derive from the document each render **and** fold `RejectEpoch`

Every property-panel control is *controlled against the document*: the row reads
`prefs.LayoutVersion.Value` **and** `page.RejectEpoch.Value` (`CzRow.Subject`,
`Curated/SidebarCustomizerControls.cs:221-240`), then mirrors the document into its own signal from a
**layout effect** keyed on `CzRow.Epoch(page)` — never from render.

`RejectEpoch` exists because a **rejected** command does not bump `LayoutVersion`. Without the fold, the row never
re-rendered after a rejection, its mirror effect never re-ran, and the control kept showing the value the user
picked while the document still held the old one. With the fold, the control **snaps back** to the truth. (The
build notes described this as "avoiding a snap-back"; the code's intent is the opposite — the snap-back is the
correct behaviour and the fold is what makes it happen. `SidebarCustomizerPage.cs:560-570` bumps it, `:584-589`
clears it.)

### Never write a signal from `Render`

The plan is published to the bound slots as a **plain field** (`SidebarPane.Plan`), not a signal. The only signal
writes in the pane are: `_rowCount` from a `UseLayoutEffect`; a single `_countSeeded` write *before the list
exists* (provably not a backwards write — nothing has read it yet); and `_dispVersion` from `Choreograph`, which
runs inside the plan memo and is read only by the `ItemsView` child that renders after it.

### A bound row is a frozen child — `SubscribeEpoch()` is load-bearing

Re-planning in `SidebarPane` does **not** re-render a realized slot. `SidebarPane.SubscribeEpoch()` reads the
search text, `ModeEpoch`, `LayoutVersion`, `Entries.Version`, `PinsVersion` and `FolderVersion` and returns their
fold so the call cannot be optimised away. Delete it and realized rows keep drawing the previous plan's content
after a library refresh, a customizer edit, a section toggle or a keystroke.

---

## Struct defaults and polarity

`SidebarSourceState` is ordered so `default` is `Ready` (`Data/SidebarRowPlanner.cs:67-69`) — a
`default(SidebarProjectionInput)` must plan real (empty) content, not a screenful of skeletons. The consequence:
a mount with **no binder at all** (a probe / headless mount) would honestly claim "library loaded, and it's empty".
`SidebarPane.Input()` compensates explicitly by forcing `LibraryState`/`TreeState`/`RecentsState`/
`NewReleasesState`/`ConcertsState` to `Pending` when `Prefs?.Binder is null` (`Pane/SidebarPane.cs:384-399`).

The same discipline forced an **inverted flag name**: `SidebarProjectionInput.SuppressTreeCreateRow`
(`SidebarRowPlanner.cs:145-153`) rather than a positive `TreeCreateRow`, because a positional default of `true`
is silently lost on `default(T)` and Classic's document *depends* on that create row existing. There is no field
named `TreeCreateRow`. When you add a bool to a POD input, make **false the landed behaviour**.

---

## C# / engine call-shape traps

| Trap | Detail |
|---|---|
| never name a local `from` before a `with` | `from … with` trips the query-expression parser. `SidebarLayoutReducer.DoMoveItem` uses `src`/`to` instead (`Wavee.Core/Sidebar/SidebarLayoutReducer.cs:320`, with the comment). |
| `explicit in` needs an lvalue | `SidebarPaneReorderCommit.Default(prefs, in ctx)` and `descriptor.Execute(services, in binding)` require a local — you cannot pass a `new …(…)` expression by `in`. The descriptor copies its `in` parameter to a local `var b = binding;` before capturing it in a lambda. |
| `TextEl.Weight` is `ushort` | `FluentGpu.Engine/Dsl/Element.cs:509`. A literal (`Weight = 600`) is fine; a **ternary** needs a cast: `Weight = (ushort)(on ? 600 : 400)` — six sidebar call sites do exactly that. |
| `BoxEl.Direction` is `byte` | `Element.cs:81` (0 = row, 1 = column). Ternary ⇒ `Direction = (byte)(vertical ? 1 : 0)` (`Curated/SidebarCustomizerControls.cs:204`). |
| options records are **nested** | `TextBox.TextBoxOptions` (`FluentGpu.Controls/TextBox.cs:27`) and `NumberBox.NumberBoxOptions` (`NumberBox.cs:139`). There is no top-level `TextBoxOptions`. |
| `ContentDialog.PrimaryText`: `null` shows a stray OK | `null` = the localized default label (shown); `""` = **explicitly hidden** (`FluentGpu.Controls/ContentDialog.cs:38-40`, `:205`). A dismiss-only dialog must set `PrimaryText = ""` — `Curated/SidebarItemPickers.cs:43` and `:81` do. |
| `Segmented` paints a plate **and** a pill | Two indicators for one value. Suppress the pill through the control's public `Segmented.PartSelectionPill` seam — the landed `SegmentedNoPill` template at `Curated/SidebarCustomizerControls.cs:287-295` styles it to `Transparent` + `Width = 0` (no engine edit, and the 3-DIP slot stays put so suppressing it costs no relayout). `SelectorBar` is **banned** in the property panel. |
| `IconRef.Font` must be forwarded, or you get tofu | An `IconRef` may name the app-local `WaveeIcons` face (`wavee.playNext` U+E900, `wavee.addToQueue` U+E901). Reading only `.Glyph` resolves those codepoints against Segoe Fluent and renders □. Pass the family through: `Icon(icon.Glyph ?? Icons.More, 14f, …, icon.Font)` (`Curated/SidebarItemPickers.cs:453-463`; also `Pane/SidebarPaneText.cs:207`). |
| artwork needs an explicit `decodePx` | Without the hint an image decodes at its **layout** size with no DPI multiply — visibly blurry on any >1× display. `Shared/SidebarCover.cs:81-85` is the **single** owner of the bucketed ladder (`size <= 32 → 64`, `<= 64 → 128`, else `256`), which also makes the 36-DIP rail tile and the 32-DIP row share one cache entry. `SidebarPaneRail.cs` has no `decodePx` of its own — it delegates to `SidebarCover.Art`. Do not add a second ladder. |

---

## Geometry and virtualization

### One uniform row height per section

`SidebarPaneMetrics.RowHeight(section)` depends on the section's density + **subtitle intent**, never on whether a
given row happens to carry a subtitle. Two consumers demand it: the `RepeatLayout.VariableList` extent, and
`Reorderable`'s slot pitch — `Reorderable.SlotFromPosition` applies a midpoint rule over
`ItemExtent + Spacing` in content space (`FluentGpu.Controls/Reorderable.cs:438-457`). A mixed 40/44 band silently
breaks both.

Nuance: `Reorderable` *does* support variable extents via `ExtentOf` (`Reorderable.cs:95-99`) — the customizer's
outline uses it — but **cross-list insertion math still assumes the uniform `ItemExtent` pitch**. The pane's bands
are uniform-per-section by choice plus that constraint. Also: the pane sets `ShowInsertionLine = false`, because
the built-in insertion line's geometry assumes one uniform pitch *from the list origin*, which a flat plan of
mixed-height rows does not have.

### `SidebarProjectionInput` ALIASES the binder's buffers

`Prefs.Binder.CurrentInput`'s lists point at the binder's reusable rebuild buffers, so a plan built from it is
valid exactly until the next rebuild — which is the `UseMemo` lifetime it is built for. Two consequences:

- **Never** cache a plan or an entry list across rebuilds.
- The expanded pane and the 56-DIP rail must own **separate `SidebarPlanBuffers`** (`_paneBuffers`,
  `_railBuffers`), because a plan aliases its buffers too. Sharing one would have the rail overwrite the pane's plan.

### A flat sort destroys folder adjacency

`SidebarSort.Apply` sorts the whole list by one comparator, so a nested playlist can land **above** the folder that
contains it. V3 re-groups afterwards: `LibraryV3View.Build(published, skip, tree, treeRevision, drillFolderId,
group)` (`Modes/LibraryV3/LibraryV3View.cs:76-118` + `EnsureParentMap`/`BuildBuckets`/`EmitLevel`) orders folders
among their siblings by the active sort and each folder's children by the same sort *within* the folder, rewriting
`Depth`/`SourceOrder`. Its tree input is the binder's — `input.PlaylistTree`, i.e. `Binder.CurrentInput.PlaylistTree`,
threaded through `SidebarPane.Input()` (`Modes/LibraryV3Sidebar.cs:211`) — and the parent map is memoised on
`Binder.Revision`. **Any new tree consumer needs this pass**; it is the precedent to copy, not to re-derive.

---

## Persistence

- **Persisted enums are append-only.** `SidebarSectionKind`, `SidebarItemTarget`, `SidebarActionTargetMode`,
  `SidebarDensity`, `SidebarPresentation`, `SidebarSortMode`, `SidebarPlaylistQualifier`, `SidebarRecentsSource`,
  `SidebarEmptyBehavior`, `SidebarDisplayField`, `SidebarRejectReason`, `SidebarEntityKind`. Never renumber, never
  reuse a value, and never rename a **wire string** in `SidebarLayoutWire`.
- **Unknown-kind round-trip policy:** an unrecognized `kind` string is preserved as an opaque section blob at its
  original index and re-emitted on the next save; it renders as nothing. Unknown *members* anywhere in the tree
  ride `[JsonExtensionData]` and are re-attached on write, matched by owning id. This only works if
  `SidebarPreferences` keeps threading `SidebarWireCarry` (`ReadCurated` on load, `WriteCurated(_layout, _carry)`
  on every snapshot). An unknown kind nested inside a `CustomGroup` is hoisted to a top-level opaque blob.
- A **missing** `version` is treated as *malformed*, not as v1.
- Over-budget is a **fault**, never a truncation. `SaveFault` does not latch — `Commit()` no-ops while over
  budget, so in-memory state legitimately runs ahead of disk.
- Canonical-JSON equality (`SidebarJson.Canonical`) is **property-order sensitive** by design: a reorder is a real
  change. `GetRawText()` is deliberately not used — it returns the original source span, so a config read back out
  of the indented document would never compare equal to the one that wrote it and every load would look like an
  edit.

---

## Tests

The tests project **source-includes app files one by one** rather than referencing `Wavee`, and deliberately has no
`FluentGpu.Engine`/`FluentGpu.Controls` project reference. So:

- **Included:** `Features/Sidebar/{Data\*.cs (one level — NOT Data\Sources\), Persistence\*.cs, SidebarDesign.cs,
  SidebarDesignGating.cs, SidebarPaneInvariant.cs, SidebarPinStore.cs}`, exactly four hand-picked files from
  otherwise-excluded folders (`Pane\SidebarBuiltInDocuments.cs`, `Modes\LibraryV3\LibraryV3Document.cs`,
  `Modes\LibraryV3\LibraryV3View.cs`, `Curated\SidebarCustomizerLayout.cs`), `Features/Shell/{ShellNav.cs,
  ShellResponsiveLayout.cs}`, `App/{SidebarBootstrap.cs, PlayLogStore.cs}` (via `Link=`), and four
  `Actions/` files.
- **Deliberately NOT included:** the rest of `Pane/`, `Shared/*`, `Modes/CuratedSidebar.cs`,
  `Modes/LibraryV3Sidebar.cs`, the rest of `Modes/LibraryV3/`, the rest of `Curated/`, `SidebarHost.cs`,
  `SidebarPreferences.cs`, `SidebarProjectionBinder.cs`, `SidebarIcons.cs`, `SidebarLayoutMenu.cs`,
  `SidebarOnboardingChrome.cs`, `SidebarDesignPicker.cs`, `SidebarResize.cs`.

**If you put decision logic in a non-included file, it becomes untestable.** That is the whole reason for the
pure/impure split (`SidebarBinderPipeline` under `SidebarProjectionBinder`, `SidebarPaneState` under
`SidebarPreferences`, `SidebarSourceMap` under the adapters, `SidebarCustomizerLayout` under the customizer). Add a
new include only when you must, and add the comment naming the test class that needs it — that is the file's
convention.

Shims that make the includes compile: `VirtualCollectionSignalShim.cs` (a trivial `Signal<T>`/`IReadSignal<T>`),
`TestAppSettingsShim.cs` (`IAppSettings`/`SettingKey<T>`/`WaveeSettings`/**`SidebarKeys` mirrored verbatim** from
`Platform/AppSettings.cs` + `MemoryAppSettings`), `Actions/ActionsTestShims.cs`, and an inline
`FluentGpu.Controls` `Icons`/`Route` shim region inside `ShellNavDestTests.cs`. Keep the `SidebarKeys` mirror in
sync by hand.

### Headless animation time does not follow `Thread.Sleep`

`AnimClock.Advance` treats a post-wait resume as idle and advances by the 1/60 quantum
(`AnimClock.DefaultDeltaMs = 1000f/60f` ≈ 16.7 ms), clamping a real wall delta to 1…40 ms otherwise
(`FluentGpu.Engine/Animation/AnimClock.cs:28`, `:39-55`). A `Sleep(110)` frame therefore buys ~16.7 ms of
*animation* time, not 110. Headless hosts additionally use `FixedFrameTimeSource` (16 ms/frame,
`Hosting/FrameTimeSource.cs:18-22`, selected at `AppHost.cs:1717`). **Frames, not sleeps, settle a track** — drive
settle loops by frame count.

---

## Accessibility — the real limits

Do not overclaim. Everything the sidebar exposes is `AutomationRole.Button` / `RadioButton` /
`NavigationItem`; `SidebarSectionHeader` uses `AutomationRole.None`. There is **no** tree/list/group semantics,
and the engine exposes **no live-region or announcement API at all** (zero `Announce`/`LiveRegion` references in
the tree). Consequences:

- A keyboard-lift reorder has **no announcement channel** — the visible position caption
  (`sidebar.pin.position`, rendered by `Curated/SidebarOutlineView.cs:305`) is the only a11y surface for it.
  Documented as such at `SidebarOutlineView.cs:303`.
- Section collapse/expand is not announced, and a virtualized pane exposes no set-size/position-in-set.
- `SidebarPreferences.UndoLabel`/`RedoLabel` are described as "for the tooltip + a11y announcement", but there is
  nothing to announce *to* yet — today they are tooltips.

---

# KNOWN ISSUES (verified, unfixed)

| # | Issue | Evidence |
|---|---|---|
| 1 | **An expanded pinned folder shows no children.** `SidebarRowPlanner.PlanPinned` appends `input.Pins` and emits a `FolderHeader` for a folder, but never reads `input.ExpandedFolders`/`input.PlaylistTree` — so the chevron rotates and nothing appears. The fix is a folder-expansion arm mirroring `PlanPlaylistTree`. The subtree still works inside a `PlaylistTree` section. | `Data/SidebarRowPlanner.cs:314-330` (`PlanPinned`) vs `:373-409` (`PlanPlaylistTree`); the click path is `Pane/SidebarPaneSlot.cs:303-318`. Pre-existing scope, deliberately deferred. |
| 2 | **`SidebarPaneSlot.LibraryEmptyText` reads only the PANE-owned search.** It uses `SidebarPane.SearchText` (`_search.Peek()`), which is session-only and pane-owned. The mode affected is **Library V3**, not Curated: V3 sets `SearchHead = false` and folds the mode-global `prefs.V3Search` in through `Config.Input`, so the planner filters on the query while `SearchText` is `""` — the empty row then says "your library is empty" instead of "no results for {query}". (The handoff notes named Curated; Curated has no mode-global search.) | `Pane/SidebarPaneSlot.cs:771-777`; `Pane/SidebarPane.cs:68-72`, `:405-406`; `Modes/LibraryV3Sidebar.cs:132`. |
| 3 | **`BackCtx` does not exist.** There is no back context anywhere in `src/apps`, and nothing provides one. The customizer's back arrow calls `SidebarCustomizerPage.GoBack()`, which flushes prefs and then walks the **HistoryStore visit log backwards**, skipping the customizer's own route, and re-navigates *forward* to the newest other entry (falling back to `"home"`). It works, at the cost of one forward history step. `WaveeShell.Back` is a private instance method exposed through no context; the unlanded fix is a `Ctx.Provide` of it in `WaveeShell.Render`. | `Curated/SidebarCustomizerPage.cs:298-323`. |
| 4 | **`ActionIcons` has no `Save` and no `Like` constant.** Both save-shaped descriptors use `ActionIcons.Heart`, which resolves stateful (`isChecked ? HeartFill : Heart`). A picker that wants distinct Save vs Like glyphs has none. | `Actions/ActionIcons.cs:13-38`, `:54`; `Actions/Extensibility/BuiltInExtensionTable.cs:117`, `:135`. |
| 5 | **`wavee.toggleLike` / `wavee.save` drop the display name.** Both `Run` lambdas call `lib.ToggleSaved(t.Uri)` and omit `ToggleSaved`'s optional `name`, so the notification-center **activity entry** loses its title (every other call site passes it). The root cause is upstream: `WaveeActionTargetResolution` exposes `Mode`/`Uri`/`RouteKey`/`ContextUri`/`Reason`/`Available`/`ReasonLocKey` and carries **no name at all**, so the adapter has nothing to forward. Fixing it means widening the resolution. | `Actions/Extensibility/BuiltInExtensionTable.cs:125`, `:143`; `App/LibraryBridge.cs:112-123`; `Actions/Extensibility/WaveeActionTargeting.cs:84-116`. Compare `Actions/ContainerActions.cs:46`, `Actions/TrackActions.cs:74`. |

## Deletion candidates (unmounted / unreferenced)

Verified reference counts across all of `src/apps`, including the tests.

| Candidate | Status |
|---|---|
| the `SidebarSelectionPill` **component** | **0** call sites — the measured overlay pill cannot work under recycling, so it is superseded by the in-row indicator. But `SidebarSelectionPill.PillH` (16f) has **3 live references** (`Pane/SidebarPaneSlot.cs:922`, `:927`; `Curated/SidebarOutlineView.cs:454`). Delete the class body, keep `PillH` (or move it), or the build breaks. |
| `SidebarSectionHeader.Section` | **0** call sites (3 doc-comment mentions). `Rule()` and `RevealWrapper()` are called **only** by `Section`, so all three go together (and `Reveal` with them). `Label`, `Header`, `ExplicitDivider` and `Height` **are** live (`Pane/SidebarPaneSlot.cs:63`, `:65`, `:137`) — do not remove the file. |
| `SidebarLayoutMenu.HeaderButton` | **0** invocations, including inside its own file. |
| loc `sidebar.pin.showAll`, `sidebar.pin.showLess` | Present in all three locales, **0** C# references — the pinned list is virtualized and uncapped now, so the overflow row they labelled is gone. Safe to retire. |
| loc `sidebar.pin.position` | **KEEP.** The handoff notes list it as unused; it is not. `CzLoc.Position` (`Curated/SidebarCustomizerPage.cs:890`) → `Curated/SidebarOutlineView.cs:305`, and it is the only a11y surface for a keyboard reorder. |
| loc `sidebar.createPlaylist` | Present in all three locales, **0** C# references. Every call site uses the sibling `sidebar.createPlaylistTooltip` (`Strings.Sidebar.CreatePlaylistTooltip` at `Shared/SidebarPinDropZone.cs:137`, `Pane/SidebarPaneSlot.cs:867`, `Modes/LibraryV3/LibraryV3Chrome.cs:152`). |
| loc `sidebar.createFolder` | Already **gone** from all three source locales (it survives only in stale `bin/` output). |
| `AppActions.All` | Declared at `Actions/AppAction.cs:96-108` with **zero** code references anywhere — every mention is a doc comment. Dead-but-retained. Do not add the first reference; the registry is the path. |
| `SidebarCustomizerPage.cs:24-28` header comment | Stale: it still quotes the old tier thresholds (≥1480 / 1180–1479 / 820–1179 / <820). The live constants are `CanvasEnterW 1320` / `FullEnterW 1000` / `CompactEnterW 820`. Fix the comment, not the constants. |
| loc `player.play` / `player.pause` in `nl`/`ko` | Dead override keys — not present in en-US and referenced nowhere. |
