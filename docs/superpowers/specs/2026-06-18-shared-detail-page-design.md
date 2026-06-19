# Shared Detail Page (playlist / album / single / liked) — Design

Status: proposed · Date: 2026-06-18 · Surface: `app/Wavee/Features/Detail/` (new) · Engine seam: **none** (app-side only; built on `ItemsView`). The `VirtualListEl`/`ItemsView` `Header`/`Footer` seam is documented as a deferred future cleanup, not built in v1.

---

## 1. Context & problem

The Wavee app shell is real and navigable: `ContentHost` keeps-alive up to 8 pages keyed by `(tabId, route)` and routes `home`/`history` to live components, but **every other route dead-ends in a placeholder** — a centered glyph + hero + "This view arrives in a later pass." (`ContentHost.cs:35-47`). `HomePage` already emits the exact routes a detail page must serve: `go("album:" + uri)`, `go("artist:" + uri)`, `go("pl:" + uri)`, and `go("liked", null)` (`HomePage.cs:37-43`).

The **data layer is ready**. `IMusicLibrary` exposes `GetPlaylistAsync`, `GetAlbumAsync`, `GetArtistAsync`, `GetLikedSongsAsync` (`Library.cs:17-26`), each returning a clean domain record with a `Tracks` list (`Models.cs:17-29`). Playback intents flow through `IPlaybackPlayer` (`Playback.cs:8-23`) and now-playing/shuffle/buffering state is already mirrored into engine signals by `PlaybackBridge` (`PlaybackBridge.cs:27-41`). So the missing piece is purely **the view**: a detail surface that reproduces the WaveeMusic look.

The driving look is four WaveeMusic surfaces — a **playlist**, **album**, **single**, and **Liked Songs** page — that all share **one** two-column layout, differing only in a small, closed set of per-context knobs. This spec proposes reproducing that look as **one** fluent-gpu component, parameterized by a per-context config, not as a port of WaveeMusic's `AlbumPage`/`PlaylistPage`/`TrackDataGrid` class hierarchy.

---

## 2. Goals & non-goals

**Goals**
- Reproduce the *visual* of the WaveeMusic detail page: a fixed-width left metadata rail + an independently scrolling right track area, on an art-derived backdrop wash, with the album-only trailing sections (About-the-artist / Fans-also-like / More-by shelf).
- Do it as **one shared `DetailPage` component** parameterized by a `DetailConfig` value, so playlist / album / single / liked are four config literals over one code path — and a fifth context (podcast, artist-discography) is one more literal.
- Map every piece to a concrete fluent-gpu primitive that already exists; where a primitive is genuinely missing, say so and propose the **minimal** addition.
- Hold the engine's hard contract: **0 per-frame managed allocations on a steady scroll** (T0 static tokens for row fill/foreground; no per-track derived brushes — `virtualization.md §5.3`).

**Non-goals**
- Not a 1:1 port of WaveeMusic's controls (`TrackDataGrid`, `TrackItem`, `ArtistSummaryCard`, …). We match the visuals and pick the cleanest engine design.
- No resizable `GridSplitter` in v1 (the kit has no splitter control; the left width is a per-context constant). A drag-splitter is a later additive control — the app already has a precedent in `Features/Sidebar/SidebarResize.cs`.
- No per-track color hints, no Lottie equalizer, no multi-select command bar, no banner-mode (in-rows scrolling hero), no inline editing of playlist title/description in v1. Each is called out in §9 as a deliberate cut.
- No new playback or library-mutation Core seam beyond what `IPlaybackPlayer` already gives; **like/save and follow have no Core command today** (§9) and are wired as no-op-capable affordances until that seam lands.

---

## 3. The target look

A bounded two-column area inside the content card, on an art-derived backdrop wash:

```
┌──────────────────────────── DetailPage (max-width ≈ 1600, centered) ─────────────────────────────┐
│  ▓ backdrop wash (vertical gradient, palette BackgroundDark → transparent, behind both columns) ▓ │
│ ┌── LEFT RAIL (fixed 280 album / 200 playlist) ──┐ ┌── RIGHT AREA (grow=1, own scroll) ─────────┐ │
│ │  ░░░ own ScrollView (vertical, hidden bar) ░░░ │ │  ── sticky chrome ──────────────────────── │ │
│ │  ┌──────────────────────┐                      │ │   toolbar:  [filter][select][sort⌄][view⌄] │ │
│ │  │   square cover ~280   │  (rounded 8, shadow) │ │   header:   # | Track | <ctx> | Duration   │ │
│ │  └──────────────────────┘                      │ │  ── rows (virtualized, uniform height) ──── │ │
│ │  [SINGLE] [2024]        ← album/single only     │ │   1  ♥  🖼  Title          Album     3:21    │ │
│ │  Big BLACK title (wraps ≤3 lines)               │ │   2  ♥  🖼  Title          Album     2:58    │ │
│ │  ◔ owner/artist · linkified name                │ │   ▸ now-playing row → accent title + eq     │ │
│ │  1 song · 4 min · 2024     (caption, secondary) │ │   …                                         │ │
│ │  [▶ Play]  (◯shuffle) (♥) (share)               │ │  ── trailing (album/single only) ────────── │ │
│ │  [Add to playlist] [Add to queue ⌄]             │ │   ABOUT THE ARTIST card                     │ │
│ │  description / ABOUT-THIS-RELEASE card          │ │   Fans also like  ◔ ◔ ◔ ◔ ◔ →               │ │
│ │                                                 │ │   More by <artist>  [□][□][□] ‹ ›           │ │
│ └─────────────────────────────────────────────────┘ └─────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Concrete dimensions, mapped to the app's token layer (`WaveeSpace`/`WaveeRadius`/`WaveeSize`/`Tok`) where one exists:

| Element | Reference value | App token / engine mapping |
| --- | --- | --- |
| Left-rail width | album 280, playlist 200, liked n/a (single-column) | per-context constant in `DetailConfig.RailWidth` (no `WaveeSize` token yet — add `WaveeSize.RailAlbum=280`, `RailPlaylist=200`) |
| Left-rail padding / gap | `16,24,8,24` / Spacing 14 | `Edges4(WaveeSpace.L, WaveeSpace.XXL, WaveeSpace.S, WaveeSpace.XXL)`, `Gap = 14f` (closest tokens: L=16, XXL=24, S=8) |
| Cover | square, corner 8, shadow blur 32 / off 0,12 / α 0.28 | `Image(url, edge, edge, WaveeRadius.Card)` in a `BoxEl{ Corners=Card, Shadow=Elevation.Card, ClipToBounds }` (cover edge = `RailWidth − sidePad`, fixed, no `SizeChanged` hack — width is a constant) |
| Type/year pills | corner 14, pad `10,3,10,5`, 11px SemiBold | `BoxEl{ Corners=All(14), Padding=Edges4(10,3,10,5), Fill, BorderWidth=1 }` + `Caption(...) with { Weight=600 }` |
| Title | Black weight, ≈40px, wrap ≤3 lines, tight tracking | `WaveeType.PageHero(name) with { Weight=900, Wrap=Wrap, MaxLines=3, Trim=CharacterEllipsis, CharSpacing=-30 }` (PageHero=`Ui.Title` 28/36 — see §9: app has no 40px display ramp; we clamp the look via `Weight=900` + `CharSpacing`) |
| Owner/artist row | 24px avatar + 14px SemiBold name | `BoxEl{Direction=0, Gap=WaveeSpace.S}` with a circular `Image(...,24,24,12)` + `BodyStrong` name |
| Meta line | 12px secondary, `·`-joined | `WaveeType.TrackMeta("1 song · 4 min · 2024")` (=`Caption.Secondary`) |
| Play pill | accent, pad `18,10`, corner 20, play glyph + label | `BoxEl{ Corners=All(20), Padding=Edges4(18,10,18,10), Fill=accent, OnClick }` with `Icon(Icons.Play,14)` + `BodyStrong("Play")`; `accent = DetailConfig.Accent` |
| Shuffle / Heart / Share | 40 circle / 32 / 32 | circular `BoxEl` FABs, `Icons.Shuffle`(E8B1) / `Icons.Heart`(EB51) / `Icons.Share`(E72D) (all in `Icons.cs`) |
| Secondary pills | h 36, pad `14,0`, corner 18, 1px border | `BoxEl{ Height=36, Corners=All(18), Padding=Edges4(14,0,14,0), BorderWidth=1, BorderColor=Tok.StrokeControlDefault }` |
| Toolbar buttons | 32×32, corner 4, 14px glyph | `BoxEl{ Width=32,Height=32, Corners=All(4) }` icon buttons; glyphs `Icons.Filter`/`More`(select)/`Sort`/`ChevronDown` |
| Column header | h 40, 12px SemiBold secondary, 1px bottom rule | a `GridEl` with the **same** `TrackSize[]` as the row + `Height=40` + bottom border |
| Track row | density M = 48px; index 52 / like 32 / art 42 (34px thumb) / title `*` / ctx 100–180 / dur 60–76 | `GridEl{ Columns=cfg.Columns, RowHeight=density, Padding=Edges4(WaveeSpace.S,…) }` (closest existing fixed `WaveeSize.TrackRowH=56` — we use the density extent, not that token) |
| About-artist card | corner 12, pad `14,12`, 56 avatar, Follow pill | `Card(...)`-shaped `BoxEl` + circular `Image(...,56,56,28)` |
| Fans-also-like chip | corner 22, 28–32 avatar + name | horizontal `ScrollView` of `BoxEl{ Corners=All(22) }` chips |
| More-by shelf | min 160 / max 200 cards, gap 12, chevron pager | `PagedShelf.Create(...)` with `MediaCard.Shelf` tiles (`HomePage.cs:59-64` recipe) |
| Backdrop wash | vertical gradient, palette dark at ~38–60α top → transparent | `BoxEl{ Gradient=LinearGradient(180, palette.BackgroundDark@0.22, transparent), no OnClick }` behind the row via `ZStack` (the `HomePage` Spotlight recipe, `HomePage.cs:123-125`) |

### Per-context differences

| Aspect | Playlist | Album | Single | Liked Songs |
| --- | --- | --- | --- | --- |
| Layout | two-column | two-column | two-column (== album) | **single-column** (no rail/wash) |
| Rail width | 200 | 280 | 280 | — |
| Badge row | none (owner row) | `ALBUM`/`EP` type pill + year pill | `SINGLE` type pill + year pill | none |
| Owner/artist | 24px avatar + linked owner | billed-artist names | artist name | — |
| Cover | gradient/mosaic art + small logo badge | artwork | artwork | (toolbar-left tile, not a rail) |
| Play accent | `Tok.AccentDefault` (blue) | art-derived palette accent | art-derived | `Tok.AccentDefault` |
| Context column | **Album** (180) + art thumb | **Plays** (100), no thumb | **Plays** (100), no thumb | **Album** (180) + art thumb |
| Title cap | MaxWidth-capped 640 | unbounded (Plays/Dur pin right) | unbounded | capped 640 |
| Artist subline | always shown | hidden (single artist) unless multi-artist | hidden | always shown |
| Trailing sections | "You might also like" shelf (non-owner) | About-artist / Fans-also-like / More-by | == album | none |
| Secondary pills | Copy to playlist / Add to queue ⌄ / (owner) Delete | Add to playlist / Add to queue ⌄ | == album | (none / Add to queue) |
| Heart semantics | follow (hidden for owners) | save album | save | none (every row already liked) |
| Track count (sizing reality) | up to 10k+ → must virtualize | ~10–50 → may render eager | 1–2 | up to 10k+ → must virtualize |

The last row is load-bearing for §5: **the contexts that have trailing sections (album/single) are short; the contexts that are huge (playlist/liked) have no trailing sections.** That asymmetry is what makes the hard problem tractable without an engine change.

---

## 4. Proposed architecture

One component tree, all structural; one value object for the differences.

```
DetailPage  (Component, keyed per route in ContentHost)
 └ StatefulRegion.Single(loadable, shimmer, content)        // skeleton → reveal  (StatefulRegion.cs:27)
    └ DetailShell(model, cfg)
       └ ZStack
          ├ Backdrop      (BoxEl, Gradient, no OnClick)      // art wash, behind everything
          └ Row (BoxEl{Direction=0}, MaxWidth≈1600, AlignSelf center)
             ├ ScrollView(LeftRail)   { Grow=0, Width=cfg.RailWidth }   // OWN scroller
             └ ScrollView(RightArea)  { Grow=1 }                        // OWN scroller
                └ VStack
                   ├ Chrome  (BoxEl{StickyTop=0})            // toolbar + column header, pinned
                   ├ TrackList                                // §5: eager (album) or virtual (playlist)
                   └ TrailingSections (album/single only)
```

### 4.1 `DetailPage` (the shared component)

A `Component` (like `HomePage`, `HomePage.cs:18`) that:
1. reads `svc = UseContext(Services.Slot)`, `go = UseContext(HistoryStore.NavCtx)`, `bridge = UseContext(PlaybackBridge.Slot)` — the same trio `HomePage` uses (`HomePage.cs:22-24`);
2. loads via `UseAsyncResource(ct => svc.Library.Get…Async(id, ct), seed)` (`HomePage.cs:27`);
3. wraps the body in `StatefulRegion.Single(loadable, shimmer, content)` for the skeleton→reveal (`HomePage.cs:47-53`, `StatefulRegion.cs:27`);
4. carries a `DetailConfig` chosen by route prefix (§8). The cancellation behavior is free: `UseAsyncResource` + `StatefulRegion` abort the load on unmount, so a fast nav-away cancels in flight.

`DetailPage` is mounted from `ContentHost.PageFor` via `Embed.Comp` inside a keyed `BoxEl` exactly like `home`/`history` (`ContentHost.cs:28-33`), so the existing `KeepAlive` boundary caches it. **Key discipline:** the page Key must include the route arg (`"page:detail:" + r.Name + ":" + r.Arg`) so KeepAlive never shares state across two albums (the slot key already folds the arg in — `ContentHost.cs:23` — but the inner `BoxEl.Key` must too).

### 4.2 `DetailShell` (two-column scaffold)

`ZStack(backdrop, row)` (`ZStack` = `Factories.cs:20`). The `row` is a `BoxEl{ Direction=0, MaxWidth≈1600, AlignSelf=Center }` (clamp + center; the reference's 2400 cap is for a far wider canvas — we use the content-card width). For the **liked** single-column config, `DetailShell` skips the rail and the backdrop and renders only the right area at full width.

### 4.3 `LeftRail`

A `BoxEl{ Direction=1, Gap=14 }` with a fixed `Width = cfg.RailWidth`, wrapped in `ScrollView(rail) with { Grow = 0 }`. Vertical stack order: cover → (badges?) → title → owner/artist → meta → CTA cluster → secondary pills → description / release card. Every clamped text run gets an **explicit `Width`** (= cover edge) so it never contributes its full single-line width to parent measure — the same discipline `MediaCard` documents (`MediaCard.cs:14, 66-68`). Square cover needs **no** `SizeChanged` hack: the rail width is a constant per config, so `cover edge = RailWidth − sidePad` is known at render (the reference's `Height=ActualWidth` trick exists only because of its resizable splitter).

### 4.4 Toolbar + column header (`Chrome`)

Both live in **one** `Chrome` box that is a **FIXED bar above the scroller** (§5), not a scrolling sticky node — it sits at the very top of the right column with nothing above it, so it never moves and the rows scroll beneath it. That is identical to the reference's pinned look but needs no sticky machinery (no `StickyTop`, no `OnPinned`, no engine change): the chrome is just the first child of the right-column `VStack`, above the `ItemsView` (playlist/liked) or above the outer `ScrollView` (album/single). It always carries its toolbar background (it is always "stuck"), which is the simpler, correct default.

The column header is a `GridEl` whose `Columns` is the **exact same `TrackSize[]` array** as the row (shared from `cfg.Columns`), `Height=40`, header labels in `Caption().Secondary()` SemiBold. Sharing the identical array is the invariant that keeps header cells aligned 1:1 with row cells (§10).

Toolbar buttons are 32×32 icon `BoxEl`s (filter/select/sort/view) using `Icons.Filter`, `Icons.More`, `Icons.Sort`, `Icons.ChevronDown` (`Icons.cs`). Sort/view/filter behaviors are stubs in v1 (visual only) — wiring them is incremental and orthogonal to the look.

### 4.5 The track row as a column grid

A row is the **`ItemsView` item template** (`itemTemplate: i => …`) — a `GridEl` (`Element.cs:304-313`) with `cfg.Columns` tracks built from `TrackSize.Px/.Star` (`TrackSize.cs:9-13`):

```
playlist/liked: [Px(52), Px(32), Px(42), Star(), Px(180), Px(60)]   // #, like, art, title*, Album, dur
album/single:   [Px(52), Px(32),         Star(), Px(100), Px(76)]   // #, like, title*, Plays, dur  (no art col)
```

`RowHeight` = the density extent (default M = 48). The title cell is a nested `BoxEl{Direction=1}` — title `WaveeType.TrackTitle` over an artist subline `WaveeType.TrackMeta` (`WaveeType.cs:10,13`); artist is a **subline, not a column** (matches the reference). **Selection + hover ride the `ItemContainerFactory`/`PartDelta` skin** (`ItemsView.cs:55`): the selected/pressed/hovered row gets its fill from the container state, not a hand-rolled handler — `SelectionModel` (Extended) owns the selected set, decoupled from realization. Now-playing is **orthogonal to selection** — read `bridge.CurrentTrack` (`PlaybackBridge.cs:27`); when `row.Id == current.Id`, recolor the title to `Tok.AccentText*` and swap the index for a play/buffering glyph (`Icons.Play` / a `ProgressRing` — no equalizer primitive, §9). DoubleTap/Enter = invoke (play the track via `OnItemInvoked`); Tap with a selection mode active = select. Zebra = alternate `Tok.FillSubtleSecondary` by index parity. **All row fills/foregrounds are T0 static `Tok.*`** — never a per-track palette brush (`virtualization.md §5.3`, §9 risk).

### 4.6 Trailing sections (album/single)

Non-virtualized siblings appended after the track list in the right scroller's VStack:
- **About-the-artist**: a `Card`-shaped `BoxEl` (`Factories.cs:137`) + circular avatar `Image` + eyebrow/name/bio + a Follow pill.
- **Fans also like**: a horizontal `ScrollView(strip, horizontal: true)` of circular-avatar chip `BoxEl`s (`Factories.cs:35`, `Horizontal=true`).
- **More by `<artist>`**: `PagedShelf.Create(count, cardAt: (i,w) => MediaCard.Shelf(...), cardHeight: MediaCard.ShelfHeight, header: …)` — the exact `HomePage` shelf recipe (`HomePage.cs:59-64`; `PagedShelf.cs:64`), which already gives the chevron pager + edge-fade + virtualized horizontal recycle.

### 4.7 Dynamic backdrop

There is **no** `UseDynamicColor`/dominant-color engine primitive — that name is a *concept* in the design corpus (`virtualization.md §5.3`, `theming.md §9`), not a symbol in `src/`. What exists: gradient fills are first-class (`LinearGradient(angle, stops)`, `Factories.cs:178`), and the **playing** track already carries a palette via `bridge.TrackPalette` (`Signal<Palette?>`, `PlaybackBridge.cs:41`), mapped to `ColorF` by `WaveePalette` (`WaveePalette.cs:16-21`). `IMusicLibrary` returns **no per-album palette** (`Models.cs` `Album`/`Playlist` have no `Palette`). So:

- v1 backdrop = `BoxEl{ Gradient = LinearGradient(180, washColor with { A = 0.22f }, ColorF.Transparent) }`, where `washColor` is: (a) `WaveePalette.ToColor(bridge.TrackPalette.Value.BackgroundDark)` **when this detail context is the one currently playing**, else (b) `WaveePalette.Neutral.BackgroundDark` (`WaveePalette.cs:21`) — a correct, flat-neutral wash with no art extraction.
- The proper fix (per-album art-derived palette) is an **additive Core method**, `Task<Palette?> GetPaletteAsync(string coverUrl, ct)` on `IMusicLibrary`, not an engine change. Flagged as a follow-up (§9), because the engine has no image-pixel read-back to extract client-side.

The same `washColor` also drives the **page-scoped Mica tint** (§4.8) — the content-panel wash here and the window-Mica scrim there share one color source.

### 4.8 Page-scoped Mica tint (the window material carries the album color)

The §4.7 wash colors a *panel behind the rows*. This goes one depth further: tint the **window's Mica material itself** while the detail page is open, so the translucent backdrop (blurred wallpaper showing through the app) carries the album/playlist color — and reverts to plain Mica when you leave. **Page-scoped to album/playlist/single detail pages only** (not a global shell setting, not playback-driven): the color is the *page's own art*, and the tint lives exactly as long as that page is the active, visible page.

**Mechanism (canon-blessed, no native interop, no extra GPU pass).** DWM owns the system-Mica tint and exposes no recolor API (`window-backdrop-mica.md:118`; `Win32Theme.cs:117` sets `DWMWA_SYSTEMBACKDROP_TYPE` only). The supported pattern is the one the Mica canon already prescribes for *exactly this WaveeMusic use case* — `window-backdrop-mica.md:119`: **a translucent art-colored scrim quad over the Mica-passthrough region.** Mica (blurred wallpaper) shows through the scrim's low alpha → real, still-translucent Mica that merely carries the album color (not a solid background). The shell already establishes the substrate: `NodeFlags.BackdropPassthrough` ("root passthrough, controls opaque"), and the sidebar/toolbar are already Mica-passthrough (`WaveeShell.cs:137,227`). The scrim is an ordinary low-alpha gradient `BoxEl` (the `theming.md` gradient path), composited over the passthrough region beneath the opaque controls.

**Plumbing (reuses the activation lifecycle — `reconciler-hooks.md §0bis`).**
- A shell-owned ambient **tint signal** — `Signal<ColorF?> ShellTint` published at the shell root (a `Context` slot, like `Services.Slot`/`PlaybackBridge.Slot`). The shell renders the passthrough scrim from it (null ⇒ plain Mica). Value-eq-gated; a theme/animation cross-fade on change is a nicety (`UseSpring` on the scrim opacity), not required for v1.
- `DetailShell` **sets** `ShellTint` to its `washColor` and **clears** it (to null) using the just-shipped activation hooks: set on `UseActivation(onActivated)` / first render, clear on `onDeactivated` AND on unmount (the `UseEffect` cleanup). This is the lifecycle's first real consumer: navigate away or park the tab ⇒ the page goes inactive ⇒ the window reverts to plain Mica; come back ⇒ it re-tints. Liked Songs (single-column, no rail/wash) sets no tint.
- **Color source = the page's art**, so it needs the same additive `IMusicLibrary.GetPaletteAsync` from §4.7/§9 gap 2. Until that lands, the tint falls back to the live-track palette (if this context is playing) or stays null (plain Mica) — the feature degrades to "no tint," never to a wrong color.

**Intensity:** one subtle alpha (≈0.10–0.16 over the passthrough — lower than the §4.7 panel wash, since it must keep Mica reading as Mica). Because it is page-scoped and auto-clearing, **no user setting is needed**; a global "tint window with album art" toggle is an optional later nicety, not part of this feature.

**Honesty:** the tint is only *visible* where Mica is visible — the chrome/margins and any translucent-over-Mica surfaces (the shell already uses `LayerOnMicaBaseAlt`, `WaveeShell.cs:227`). It does not recolor opaque content cards. The richer "whole page glows with the art" look is a function of how translucent the shell surfaces are, which is an existing app-surface choice (`Surfaces.cs`), not this feature.

---

## 5. The virtualized-list-with-trailing-sections decision

This is the one genuinely hard composition. Honest statement of the constraint:

- `VirtualListEl` exposes only `ItemCount`/`RenderItem`/`RowBind`/`Layout` (+ lifecycle hooks) — **no Header/Footer slot** (`VirtualListEl.cs:25-57`).
- `ScrollEl` exposes only `Content` — **no header/footer slot** (`Element.cs:557`).
- `ApplyStickyOffsets` pins a node only when it is inside the **same** scroller's `ContentNode` (`AppHost.cs:1414-1424`). A `VirtualListEl` is *its own* scroll viewport, so a sticky `Chrome` placed as its sibling in an outer VStack would pin against the **outer** scroller while the rows live in the **inner** viewport — the chrome and the rows scroll on different surfaces. Wrong.

So "[sticky chrome] + [virtual rows] + [trailing About/Fans/More-by] in one viewport" is not a drop-in today. Three options:

- **(A) Outer non-virtual `ScrollView` whose content is a VStack of `[Chrome(StickyTop=0)] + [eager track list, Grow=0] + [trailing sections]`.** Clean for sticky (chrome *is* in the outer content → `ApplyStickyOffsets` pins it; rows + trailing scroll under it). But a `Grow=0` `VirtualListEl` measures to its layout's full `ContentExtent` (`ContentExtent(n) = n*Extent`, `VirtualLayout.cs:93`), so it realizes **all** rows — virtualization is defeated. Acceptable only for short lists.
- **(B) One `Grow=1` viewport over `GroupedListVirtualLayout` with a flat-index projection** (`VirtualLayout.cs:328`, `Virtual.GroupedList`, `Virtual.cs:81`): index 0 = the chrome (a measured "header kind", `StickyHeaderIndexAt` pins it — `VirtualLayout.cs:348`), indices 1..N = uniform track rows, indices N+1.. = each trailing card as a measured item. Full virtualization **and** a sticky header in one viewport, **no engine change**. More plumbing (a flat projection + measured layout), but it is the corpus's documented grouping pattern (`virtualization.md §7`).
- **(C) Additive engine seam: `Header`/`Footer` `Element` slots on `VirtualListEl`** (first/last non-recycled children inside the content node, excluded from the windowed range; a `Header` is `StickyTop`-eligible). Cleanest long-term API (mirrors WinUI `ItemsRepeater.Header/Footer`), but it touches the reconciler's realize loop and `ScrollState` content-extent math — a real seam, medium risk.

**Decision — build the track list on `ItemsView` (L3, for selection), and compose it WITHOUT an engine seam.** Two per-context compositions, gated by `cfg`, both using `ItemsView` so selection is uniform.

The track table needs **selection** (the multi-select toolbar affordance; Ctrl/Shift range-select like a file list) and keyboard nav — exactly what `ItemsView` adds over raw `VirtualListEl`: `SelectionModel` (None/Single/Multiple/**Extended**, range-based so *select-all on a 10k playlist realizes nothing* — `SelectionModel.cs:14`), item containers (row selection/hover skin via `ItemContainerFactory`/`PartDelta`), keyboard nav + the shipped animated `BringIntoView`, invoke-vs-select semantics (Tap = select, DoubleTap/Enter = play), optional reorder. So both contexts use `ItemsView` for the rows.

The key insight that removes the need for a `Header`/`Footer` engine seam: **the chrome (toolbar + column header) sits at the very top of the right column with nothing above it, so it doesn't need to scroll-then-stick — it's simply a FIXED bar above the scroller.** A plain sibling, no `StickyTop`, no `OnPinned`, no engine change. The two compositions:

- **Playlist / Liked (huge — the 1000+ case, no trailing sections):**
  `VStack[ chrome (fixed), ItemsView(Grow=1, StackLayout, selectionMode=Extended, keyOf=id) ]`. The `ItemsView` **is** the scroll viewport; rows virtualize + select; the fixed chrome stays put because it's above. Fully virtualized, no nesting, no seam.
- **Album / Single (short ≤~50 + trailing About/Fans/More-by):**
  `VStack[ chrome (fixed), ScrollView(Grow=1)[ VStack[ ItemsView(Grow=0, eager, selectionMode), trailing ] ] ]`. The outer `ScrollView` scrolls rows + trailing together; the `Grow=0` `ItemsView` renders the bounded row set (no internal scroll) with selection intact; the fixed chrome stays above.

Gated purely on `cfg`:
```
cfg.TrailingSections == null  (playlist/liked) → ItemsView IS the scroller (Grow=1, virtualized)
cfg.TrailingSections != null  (album/single)   → outer ScrollView over [ ItemsView(Grow=0 eager) + trailing ]
```

**The one wrinkle (verify in build):** the album path nests a content-sized (`Grow=0`, overflow-free) `ItemsView` inside the outer `ScrollView`. A viewport with no overflow should not capture the wheel (the engine's `ScrollState` pointer-over gating only scrolls when there is overflow), so the outer scroll wins — but this must be confirmed. **Fallback if it captures:** render the album rows as plain `GridEl` rows + a `SelectionModel` wired by hand (album lists are short, so the loss of `ItemsView`'s container plumbing is cheap). Either way, no engine change.

**Deferred — the `Header`/`Footer` seam (the one-uniform-path cleanup), NOT built in v1:**

> Adopting it would collapse the two compositions into one `ItemsView` (always the scroller) with `Header`(chrome) + `Footer`(trailing), virtualized + selectable at any size, no nesting. It is the cleaner long-term API and the only thing that would let a *huge list WITH trailing sections* coexist (no real context needs that today). Shape, if/when adopted: add `Element? Header`/`Element? Footer` to `VirtualListEl` (realized outside the `[first,last)` range — never recycled, never selectable items; `ContentExtent += headerH+footerH`; item rects offset by `headerH`; `Header` `StickyTop`-eligible since it lives inside the scroller's own `ContentNode`), forwarded as `header:`/`footer:` on `ItemsView.Create`, with a VerticalSlice golden check. Bounded reconciler change; deferred until the no-seam split proves limiting.

Also rejected: **the flat-index `GroupedListVirtualLayout` projection** (chrome at item-index 0, trailing as tail measured-items) — virtualizes with no engine change but smears chrome + rows + trailing into one item index space and fights selection (the chrome would have to be special-cased out of every selection op). The fixed-chrome-above composition is strictly cleaner.

---

## 6. Two-column scroll behavior

Two **independent** viewports, each a `ScrollView` with its own `ScrollState` (`Columns.cs` holds per-node scroll state; `ScrollEl`, `Element.cs:553`):

- **Left:** `ScrollView(rail) with { Grow = 0 }`, the rail box `Width = cfg.RailWidth`. A vertical `ScrollEl` is a hard viewport that clips + scrolls only its own content (`Element.cs:548-551`), so the rail scrolls independently by construction. Auto-hide scrollbar (default) matches the reference's hidden bar; do **not** set `AlwaysShowScrollbar`.
- **Right:** `ScrollView(rightArea) with { Grow = 1 }` — fills the remaining width and owns the row + trailing scroll.

Wheel/touch capture is gated per-viewport by the engine's `ScrollState` pointer-over logic, so the rail scrolls only when the pointer is over it. No `GridSplitter` (kit has none) — the 12px divider is omitted and the left width is the per-context constant; the backdrop wash sits behind both as a non-interactive `BoxEl` in the `ZStack`. Bound the whole row with `MaxWidth` and center it (`AlignSelf=Center`).

---

## 7. Data flow + route wiring

### Route wiring (`ContentHost.PageFor`)

Add prefix branches before the placeholder fallback (`ContentHost.cs:25`):

```
r.Name.StartsWith("album:")  → DetailPage(kind: Album,    id: r.Name["album:".Length..],  cfg: DetailConfig.Album)
r.Name.StartsWith("pl:")     → DetailPage(kind: Playlist, id: r.Name["pl:".Length..],     cfg: DetailConfig.Playlist)
r.Name.StartsWith("artist:") → ArtistPage  (out of scope here; placeholder stays)
r.Name == "liked"            → DetailPage(kind: Liked,    id: null,                       cfg: DetailConfig.Liked)
```

A `single:` does **not** exist as a route — a single is the **album** path with `DetailConfig.Single` selected when `album.TrackCount <= 2` (or an explicit type field), matching WaveeMusic where `SINGLE == AlbumPage`. Each branch returns `new BoxEl { Key = "page:detail:" + r.Name, Grow=1, Direction=1, Children=[ Embed.Comp(() => new DetailPage(...)) ] }` — same shape as the `home` branch (`ContentHost.cs:28-29`).

### Load

`DetailPage.Render` calls the matching `IMusicLibrary` read (`Library.cs:17-26`) through `UseAsyncResource`, seeded with an empty model so `StatefulRegion.Single` shows the shimmer first frame, then reveals (`HomePage.cs:47-53`). Models: `Playlist{ OwnerName, Description, Cover, Tracks }`, `Album{ Artists, Year, TrackCount, Cover, Tracks }`, `Track{ Title, Artists, Album, DurationMs, IsExplicit, Image }`, `Artist{ TopAlbums }` for the More-by shelf (`Models.cs:13-29`).

### Play / queue / like

Through `svc.Player` (`IPlaybackPlayer`, `Playback.cs:8-23`):
- **Play (all):** `PlayAsync(contextUri, 0)` — `contextUri` = album/playlist `Uri`; for liked, `"spotify:collection:tracks"` (the same URI `HomePage.cs:37` keys on).
- **Play a row:** `PlayAsync(contextUri, rowIndex)` (preferred — keeps context) or `PlayTrackAsync(track.Uri)`.
- **Shuffle:** `SetShuffleAsync(true)` then `PlayAsync(contextUri, 0)`. Shuffle/now-playing **state** is read back from `bridge.IsShuffle` / `bridge.CurrentTrack` / `bridge.IsBuffering` (`PlaybackBridge.cs:27-41`) — the row recolor and the toggle states subscribe to those signals and re-render only the affected row.
- **Like / save / follow:** **no Core command exists** (`IPlaybackPlayer` has no save/follow; `Playback.cs:8-23`). v1 renders the heart/follow affordance with a local optimistic `Signal<bool>` and a TODO; the proper fix is an additive `ILibraryMutations` interface (app-side, no engine impact) — §9.

---

## 8. Per-context configuration

One `DetailConfig` readonly record struct (a pure app value — **no engine type**) carries the closed difference set. The shared `DetailPage`/`DetailShell` holds everything structural; `DetailConfig` flips the eight knobs:

```csharp
readonly record struct DetailConfig(
    bool          TwoColumn,        // false → liked single-column (no rail, no wash)
    float         RailWidth,        // 280 album/single, 200 playlist, 0 liked
    BadgeStyle    Badges,           // TypeYear (album/single) | OwnerRow (playlist) | None (liked)
    Func<Palette?, ColorF> Accent,  // album/single: p => WaveePalette.Accent(p) ?? Tok.AccentDefault; playlist/liked: _ => Tok.AccentDefault
    TrackSize[]   Columns,          // SHARED by row + header (the alignment invariant, §10)
    bool          CapTitle,         // true playlist/liked (MaxWidth 640) | false album/single (Star unbounded)
    bool          ShowArtistSubline,// true playlist/liked | false album/single (unless multi-artist)
    ItemsSelectionMode Selection,   // Extended (playlist/album/liked — Ctrl/Shift multi-select) | None (single), §5
    Func<DetailModel, Element[]>? TrailingSections,  // album/single → trailing block (selects the album/single composition); null → playlist/liked (ItemsView IS the scroller)
    SecondaryPill[] Pills,          // per-context pill set
    HeartMode     Heart);           // Save | Follow | None
```

The track list is always **`ItemsView.Create(trackCount, rowTemplate, StackLayout, selectionMode: cfg.Selection, keyOf: t => t.Id, containerFactory: rowSkin, grow: cfg.TrailingSections is null ? 1f : 0f)`** — `ItemsView` in every context, so selection is uniform. `cfg.TrailingSections` selects the *composition* (§5): `null` ⇒ the `Grow=1` `ItemsView` is the scroller (virtualized); non-null ⇒ a `Grow=0` eager `ItemsView` inside an outer `ScrollView` with the trailing block. The chrome is a fixed bar above, either way.

Four literals (`DetailConfig.Album`, `.Single`, `.Playlist`, `.Liked`) instantiate the page. Adding a fifth context (podcast) is one more literal. This is the concrete "ONE shared component, per-context config" answer.

---

## 9. Engine gaps & risks (honest)

**Gaps (missing primitives — stated, not papered over):**

1. **No `VirtualListEl.Header`/`Footer` slot** (`VirtualListEl.cs:25-57`) / **no `header:`/`footer:` on `ItemsView.Create`** (`ItemsView.cs:201`). **NOT needed in v1** — §5 composes around it with a fixed chrome bar above the scroller (no sticky) + `ItemsView`-as-scroller (playlist/liked) or eager `ItemsView` in an outer `ScrollView` (album/single). The `Header`/`Footer` seam is the documented future cleanup (one uniform path), deferred. *No engine change in v1.* The one thing to verify: a `Grow=0` (overflow-free) `ItemsView` nested in the album's outer `ScrollView` must not capture the wheel; fallback is plain `GridEl` rows + a hand-wired `SelectionModel` (§5).
2. **No per-album art-derived `Palette`** in `IMusicLibrary` (`Models.cs` has no `Album.Palette`; only the live track carries one via `bridge.TrackPalette`). Backdrop falls back to `WaveePalette.Neutral` or the live-track palette. Fix = additive `IMusicLibrary.GetPaletteAsync` (the engine has no pixel read-back for client-side extraction). *Low risk; cosmetic until added.*
3. **No save/follow Core command** on `IPlaybackPlayer` (`Playback.cs:8-23`). Heart/follow are optimistic-local until an additive `ILibraryMutations` lands. *Low risk; app-side only.*
4. **No `GridSplitter`** in the kit → fixed per-context rail width (precedent: `Features/Sidebar/SidebarResize.cs`). *Low risk; cosmetic.*
5. **No equalizer/Lottie** for the now-playing index slot → glyph swap (`Icons.Play`) / `ProgressRing` for buffering. *Low risk; visual nicety.*
6. **No display (~40px) text ramp.** `WaveeType.PageHero` = `Ui.Title` (28/36, `WaveeType.cs:19`). The rail hero overrides `Size = 40, Weight = 900`. **Resolved (engine):** the long-title-trims problem is fixed by an engine **auto-fit** added to `TextEl` — `MinSize` (→ `TextStyle.MinSizeDip`) makes the measure pass shrink the font (40 → 22) to fit `MaxLines = 3` at the cover width, minimizing wraps and avoiding the ellipsis; the chosen size rides `TextMeasureCache.FitSize` to the recorder. Opt-in, so all other text is byte-identical (golden check `AF1`; canon in `text.md` §pipeline-4). *Shipped.*
7. **Density slider** (XS..XL {32,40,48,60,76}) has no built-in control → recreate the `StackVirtualLayout` via `UseMemo` keyed on a density signal (the `PagedShelf.cs:126` hoist pattern). v1 ships fixed M=48; the slider is incremental. *Low risk.*

**Risks (correctness, called out so reviewers can check them):**

- **Seam (C) must keep the windowed item range header/footer-relative.** Item rects offset by `headerH`; `Header`/`Footer` are NOT items (never recycled, never in the `[first,last)` range, never selectable). Get this wrong and either the selection index space is off-by-one or the footer enters the recycle pool. The golden check (§11) pins exactly this. (There is no eager-list path anymore — `ItemsView` virtualizes every context, so the old "realizes all rows" footgun is gone.)
- **Footer + a 1000-row list never coexist:** album/single (footer present) are short; playlist/liked (huge) pass `Footer=null`. So footer realization stays trivial (always-realized tail for short lists). If a future context needs both a footer AND 10k rows, the footer must become window-realized at the tail — out of scope, noted.
- **Per-row derived brushes are forbidden** (`virtualization.md §5.3`): the now-playing accent + zebra **must** use T0 static `Tok.*` and read `bridge.CurrentTrack`, never a per-track palette brush — or scroll-velocity recycle thrashes the brush cache.
- **`:stuck` restyle lands next frame** (signals-first, `AppHost.cs:1441-1443`): design the opaque-fill-when-stuck so a 1-frame translucent flash on first pin is acceptable.
- **Header/row column drift:** the sticky header `GridEl` and the row `GridEl` must share the **exact same** `TrackSize[]` from `cfg.Columns`, or they misalign (§10).
- **Two scrollers both capture wheel:** the rail must scroll only when the pointer is over it — the engine's `ScrollState` pointer-over gating handles this; verify under a fling that crosses the divider.
- **KeepAlive holds up to 8 pages** (`ContentHost.cs:21`): a heavy detail page retains its realized window + images. Verify residency under rapid album→album nav (the §11 manual check).

---

## 10. Edge cases & invariants

- **Empty list:** zero tracks → render `EmptyState.Default()` (the app's existing empty component, used in `HomePage.cs:52`) in place of the rows; the rail + trailing still render.
- **Loading:** `StatefulRegion.Single` shows a shimmer matched to the real layout (a rail skeleton + N row-skeletons sized to `TrackCount` so the reveal doesn't jump), then reveals (`StatefulRegion.cs:27`; `MediaCard` skeleton pattern `MediaCard.cs:121-149`).
- **Long title:** `MaxLines=3, Trim=CharacterEllipsis` on the hero; clamped by an explicit `Width` so it never widens the rail.
- **Long context cell** (album name / play-count): the cell is a fixed `Px` track with `MaxLines=1, Trim=CharacterEllipsis` → ellipsis, never overflow.
- **Now-playing on a recycled row:** the row reads `bridge.CurrentTrack` and compares ids; recycling rebinds the comparison, so the accent moves with the data, not the slot (`virtualization.md §3` — `keyOf` = track id, recycle-safe).
- **Invariant — column alignment:** header and row consume the **same** `cfg.Columns` array instance.
- **Invariant — zero-alloc scroll:** in-window scroll is transform-only (`-ScrollOffset` is the content `LocalTransform`, `Element.cs:549-551`); row fill/foreground are static `Tok.*`; the layout's `Window`/`ItemRect` are pure struct math (`VirtualLayout.cs:9-14`). No per-frame managed allocation on phases 6–13 — the standing engine contract.
- **Invariant — independent scroll:** rail and right area never share a `ScrollState`.

---

## 11. Testing

**VerticalSlice golden checks** (`src/FluentGpu.VerticalSlice/Program.cs`; "ALL CHECKS PASSED" + zero-alloc gate per `design/subsystems/validation.md`):
- **No new engine seam → no new engine golden check is required.** The playlist composition rides the already-tested `ItemsView` + `StackVirtualLayout` + `SelectionModel`; the checks below confirm the app composition holds.
- **Selection-decoupled-from-realization:** `SelectAll()` on a 10k-track model realizes nothing (realized count unchanged), and a scrolled-in row reads `IsSelected` correctly on prepare (exercises `SelectionModel`'s range semantics on the track-list shape).
- **Album nested-scroll behavior:** a `Grow=0` (overflow-free) `ItemsView` inside an outer `ScrollView` does not capture the wheel — the outer scroller moves (the wrinkle in §5). If this fails, switch the album rows to plain `GridEl` + hand-wired `SelectionModel`.
- A **zero-alloc tripwire** over a simulated scroll of the playlist `ItemsView`: `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across a window of steady-scroll frames (the harness already does this for the headless seams).
- A column-alignment unit check: the fixed column-header `GridEl.Columns` is reference-equal to the row's.

**Manual visual checks** (`dotnet run --project src/FluentGpu.WindowsApp`, and `--screenshot` for deterministic diffs):
- Nav `home → album → playlist → liked` and confirm: two-column for album/playlist, single-column for liked; independent rail scroll; the fixed chrome bar stays put while rows scroll beneath it; multi-select (Ctrl/Shift) works and DoubleTap/Enter plays; now-playing row recolors when that context is playing; the album/single trailing sections scroll below the rows; the More-by shelf pages; the window Mica tints to the page art and reverts on nav-away (§4.8).
- Residency under rapid album→album nav (KeepAlive ≤ 8) — watch working set, confirm no unbounded image growth (the perf-diagnostics tooling in memory covers the method).

---

## 12. Layered implementation plan

1. **Shared component skeleton.** `DetailPage` + `DetailShell` + `DetailConfig` + the `ZStack(backdrop,row)` scaffold + `StatefulRegion` load wiring against `IMusicLibrary`. Backdrop = neutral/live-track gradient. **No engine change.**
2. **Left rail.** Cover + badges + title + owner/artist + meta + CTA cluster + secondary pills + description/release card, all token-mapped (§3), each clamped run explicit-width.
3. **Track list (`ItemsView`) + fixed chrome.** The row item template (`cfg.Columns` `GridEl`); the fixed `Chrome` bar above (toolbar + column-header `GridEl`, no sticky); row hover/selection via the `ItemContainerFactory`/`PartDelta` skin; now-playing accent via `bridge.CurrentTrack`; zebra. Play-on-invoke (DoubleTap/Enter) + multi-select via `SelectionModel` (Extended). **Playlist/liked composition:** `ItemsView(Grow=1)` is the scroller. Verify the album composition's `Grow=0` nested `ItemsView` doesn't capture the wheel (else fall back to `GridEl` rows + hand-wired `SelectionModel`).
4. **Trailing sections + album composition.** About-artist `Card`, Fans-also-like horizontal `ScrollView` chips, More-by `PagedShelf.Create` (`HomePage.cs:59` recipe); wrap `[ ItemsView(Grow=0 eager) + trailing ]` in the outer `ScrollView` for album/single (playlist/liked skip this — `ItemsView` is the scroller).
5. **Route wiring.** `ContentHost.PageFor` prefix branches (`album:`/`pl:`/`liked`) + play/queue wiring to `svc.Player`; single = album path with `DetailConfig.Single`.
6. **Per-context config.** The four `DetailConfig` literals; verify each against the §3 difference table.
7. **Page-scoped Mica tint (§4.8).** The shell `ShellTint` ambient signal + the passthrough scrim quad (`window-backdrop-mica.md:119` pattern); `DetailShell` sets it from `washColor` on activate / clears on deactivate+unmount via the activation lifecycle. Ships with the live-track/neutral fallback; the per-page art color lands with `GetPaletteAsync` (the same follow-up as the §4.7 wash). Canon: update the `window-backdrop-mica.md` tinted-Mica note to point at the as-built `ShellTint` signal.

Follow-ups (post-v1, each additive, none an engine change unless noted): per-album `GetPaletteAsync` (gap 2 — unlocks the true per-page art color for both the §4.7 wash and the §4.8 Mica tint), `ILibraryMutations` for like/follow (gap 3), density slider (gap 7), resizable splitter (gap 4), banner-mode hero, multi-select bar, an optional global "tint window with album art" user setting, and — if one uniform path is wanted — seam (C).