# FluentGpu ⇄ WaveeMusic — App Requirements & Spec Fold-In

*Lead-architect ruling. Drives both the FluentGpu engine spec and the WaveeMusic rewrite. Decisive throughout; every "ADD" names the type, the hook, the DrawList/RHI/PAL delta, the 13-phase slot, and the assembly. Critiques folded; overclaims corrected against the actual contracts (`architecture-spec.md` §4.5/§4.7/§4.8/§5.1, `subsystems/gpu-renderer.md`, `subsystems/reconciler-hooks.md`, `winui-painpoints-assessment.md`).*

---

## 1. EXECUTIVE SUMMARY

**WaveeMusic as a renderer workload.** WaveeMusic is a Spotify desktop client whose entire visual identity is *media-driven chrome wrapped around two virtualized lists*: a browser-tab shell with a hero carousel and shelf walls (Home/Search/Browse — hundreds of album-art thumbnails), two list engines (`TrackDataGrid` and `TrackListView`) that must scroll 10k–50k+ track/episode rows at 60fps with per-row album art, a now-playing surface that recolors its *entire* backdrop from album-art palette on every track change (editorial 6-layer blurred backdrop + multi-stop gradient washes), synced per-syllable lyrics driven by the **playback clock** (not the frame clock), and externally-decoded **video** (PlayReady `MediaPlayerElement` / WebView2) that must composite as theatre, sidebar, and a detachable mini-PiP. It is simultaneously **media-heavy, list-heavy, theming-heavy, video-bearing, and lyrics-bearing** — the four hardest things a 2D UI engine can be asked to do at once, all on FluentGpu's single-UI-thread v1.

**Headline verdict.** FluentGpu's *spine* already carries WaveeMusic: the Reactor model + keyed reconciler author every screen; the SoA SceneStore + 3-axis dirty + layout-free scroll-as-transform + composition-style animation phase 7 + the gradient atlas + `BrushTable` content-hash dedup + the `IEffectRunner`/LayerPool/offscreen-RT seam + the `Channel`/`IPlatformAppLoop.Post` off-thread edge + window-level Mica-via-DComp are all directly reusable, and color recolor / scroll / animation correctly stay **PaintDirty/TransformDirty, never LayoutDirty**. But four subsystems are **named-but-undesigned gaps** that WaveeMusic load-bears on, and they must be ADDED:

1. **Image & media pipeline** — `DrawImageCmd` exists but there is *no* decode/residency/eviction design, and **the RHI seam has no texture-upload primitive at all** (`UpdateBuffer` is buffer-only). This is the single biggest gap.
2. **Virtualization** — the #1 critical gap; every list and the entire residency/pin budget rest on it, yet only the keyed-LIS *diff* is named as reusable (`reconciler-hooks.md` §765), with no anchoring / variable-height / recycle design.
3. **Theming & accent reactivity** — static 3-blob theme tokens exist; *dynamic per-element album-art-derived brushes* do not.
4. **Backdrop / Effects / Animation** — Mica, in-app Acrylic, editorial backdrop bake, connected/implicit animations, and the **video present-tree** (a multi-visual DComp tree, which the current single-visual §5.1 present path does **not** support) must be ADDED.

Four corrected overclaims gate the verdict: (a) **phases 6–13 are 0-managed-alloc only for record/batch/submit; phase 4 render of any realized list window allocates** `Element` records + a managed `Element[]` child chunk — the honest goal is "0 alloc in 6–13 *paint machinery*, bounded edge alloc in 4–5"; (b) **texture upload requires a new RHI primitive**, it is not a free ride on the `UploadRing`; (c) **video needs a present-tree redesign**, not a fold onto §5.1; (d) **per-frame lyric color must be per-instance glyph data, not BrushHandle re-bake**, or it breaks the clean-span invariant. With those four ADDs, FluentGpu builds WaveeMusic.

---

## 2. SCREEN / WORKLOAD INVENTORY

| Screen / surface (Wavee source) | Renderer demand | Dominant subsystem |
|---|---|---|
| **Shell: browser tabs + sidebar** (`Shell`, `TabBarItem`) | Tab switch = subtree mount/unmount + nav-cache trim (release image surfaces); persistent sidebar player; **window Mica** behind transparent root | Backdrop (Mica/PAL), reconciler |
| **Home / Search / Browse shelf walls** (`HomePage`, `SearchPage`, `ContentCard`, `EditorialHeroCard`, shelves) | 100s of 64–256px thumbnails in horizontally-virtualized shelves; hero carousel parallax; editorial blurred backdrop; circle/rounded art; mosaic 2×2 | Image pipeline + virtualization (2-axis) + backdrop |
| **Playlist / Album** (`TrackDataGrid` `playlist`/`album`, `PlaylistPage`, `AlbumPage`) | 10k+ rows, 64px row art, sticky column header, density (32–76px) rows, shy banner header driven by scroll, palette-tinted page header, footer-as-row | Virtualization (the load-bearing case) + image + theming |
| **Liked Songs** (`LikedSongsView`, 50k) | Largest list; grouping (by album/artist) with variable-height image-bearing group headers; regroup on mode switch | Virtualization (grouping + variable-height) |
| **Artist** (`ArtistPage`) | Top-tracks `ItemsRepeater`, popular-releases, discography grid, pinned card, palette accent from artist image; nav-flicker class of bug on fast nav | Virtualization + theming (cancellation) |
| **Now-Playing / Right panel** (`RightPanelView`, `ExpandedNowPlayingLayout`, `SidebarPlayerWidget`) | Album-art → 6-layer editorial backdrop + multi-stop gradient wash + accent pills + foreground-luma text; **recolor every track change**, cross-faded | Theming (T2 palette) + backdrop bake + image |
| **Synced lyrics** (`Wavee.Controls.Lyrics`, `NowPlayingCanvas`) | DirectWrite line layout; per-syllable color animated by **playback ms**; line scroll; CJK furigana/romanization; optional 3D fan; blurred-art backdrop | Animation (playback clock) + text seam + image backdrop |
| **Video: theatre / sidebar / MiniVideoPlayer PiP** (`MiniVideoPlayer`, `SpotifyVideoPlaybackTarget`, `ActiveVideoSurfaceService`) | Externally-decoded (PlayReady `MediaPlayerElement`) surface composited under our chrome; priority arbitration (theatre>PiP>sidebar); 3-layer crossfade (art→poster→live); PiP drag; persists across nav | Video present-tree (PAL `IVideoPresenter` + multi-visual DComp) |
| **Player bar** (`PlayerBar`) | 1Hz interpolated progress, small art (cross-fade on track change), chapter segments, like/menu | Image + animation (driven clock) |
| **Add-to-playlist / toasts** (`AddToPlaylistBar`, notifications) | In-app **Acrylic** sampling content behind a floating overlay | Backdrop (live-sample blur) |

---

## 3. THE FOUR NEW / EXTENDED SUBSYSTEMS

Each subsystem below states **the requirement → the design (key types + Wavee-dev hooks + DrawList/RHI/PAL deltas + 13-phase placement + assembly) → the zero-alloc/off-thread story → worst-case behavior.** The two genuinely new portable subsystems are the **Media subsystem** (`FluentGpu.Engine/Media/` — image/residency/video-registry/lyrics-layout) and **`FluentGpu.Theme`** (palette extraction + brush derivation); both depend only on `Foundation` (+ RHI/Text *interfaces*), preserving P4 acyclicity, and are referenced only by `Hosting`. Virtualization adds **no new folder** (it lands in the portable `Engine/Layout/`, `Engine/Scene/`, `Engine/Hooks/`, `Engine/Reconciler/`).

---

### 3.1 Image & Media Asset Pipeline

**Requirement.** A `DrawImageCmd` exists with a raw `TextureHandle` and a `Channel<UploadRequest>` is *mentioned*, but there is no decode → residency → eviction → upload design, and **no RHI texture-upload primitive**. WaveeMusic needs: async constrained-to-bucket decode (64/128/256/512, matching `ImageCacheService`), an O(1) `(uri,bucket)` cache with in-flight dedupe, LRU eviction with pin-before-trim (the *exact* self-eviction race WaveeMusic documents), placeholder→image cross-fade, mosaic 2×2, and the small-image atlas that turns a 400-thumbnail Home into ~80 draw calls.

**Design.**

The linchpin (KEEP, verbatim from the Image design, blessed by its critique): `DrawImageCmd` references an **`ImageHandle` → `ImageRealization`** indirection (a Foundation slab `ImageRefTable`, content-epoch-stamped), *never* a raw `TextureHandle` — exactly mirroring `GlyphRunRealization` so the §4.5 clean-span rule (`valid IFF every handle IsLive AND its realization content-epoch is unchanged`) reuses **its existing machinery** with no batcher special-casing.

```csharp
// FluentGpu.Engine/Foundation — ImageRefTable : SlabAllocator<ImageRealization>, HandleKind.ImageRef
public readonly record struct ImageHandle(Handle H);
public enum ImageState : byte { Placeholder, Decoding, Resident, Failed, Evicted }
public struct ImageRealization {
    public TextureHandle Texture; public RectF AtlasUv;        // standalone, or a page of the small-image atlas
    public ushort NaturalW, NaturalH; public ImageState State;
    public byte DecodeBucket;          // 0..3 → 64/128/256/512
    public uint ContentEpoch;          // bumped on (re)resident OR evict → invalidates clean spans, drives cross-fade
    public uint LastUsedFrame; public int PinCount;            // visible nodes pin; pinned ⇒ ineligible for eviction
    public Vector4 PlaceholderRGBA;    // dominant-color tint shown while not Resident (the app's IColorService value)
}
```

**Amended opcode (DrawList).** `DrawImageCmd` gains the indirection + `CornerRadius4 Radii` (circle/rounded art is everywhere) + `float CrossFade` + `byte Stretch` (`UniformToFill|Uniform|None`) + `ClipHandle`. If `State != Resident`, the record-walk emits a `FillRoundRectCmd` with `PlaceholderRGBA` (one quad, zero texture bind); on upload completion `ContentEpoch` bumps → one-node re-record → cross-fade. **Small images (≤128px) pack into a shared `BGRA8` image-atlas page** — this is `gpu-renderer.md` `OQ-4` ("small-image atlasing", a *future* optimization) **promoted to v1** because it is the only thing that hits the "shelf row = 1–2 draws" target. The batcher's UV-resolve gains an `ImageRef` branch (resolve atlas UVs at batch time like glyphs — never baked into the command, reconciling the §9 image-brush "TextureHandle baked" path).

**Hooks the Wavee dev calls** (`FluentGpu.Engine/Media/` thin `UseRef`/`UseMemo`/`UseEffect` compositions, `DepKey`-span deps):

```csharp
ImageBinding UseImage(StringId src, int decodePx, Vector4 placeholderTint = default);  // → ImageHandle + State + NaturalSize
MosaicBinding UseMosaic(StringId mosaicUri);            // 4 independent ImageBindings, laid out as a 2×2 Grid
```

`UseImage` does one cache probe (`ImageCache.GetOrRequest(src, bucket)` returns an `ImageHandle` **synchronously** in `Placeholder` — alloc only on cache-miss slot creation, mount-time). **Decision: mosaic = Grid of 4 `UseImage`, no `DrawMosaicCmd`** — *but* (folding the critique) the four tiles share **one `MosaicGroup` residency unit**: one pin, one priority, one cancel ticket, decoded as a single coalesced worker job → the channel-drop policy drops the whole group or none, so there is never a permanent 3-of-4 broken mosaic.

**The decode/residency subsystem** (`FluentGpu.Engine/Media/`, Hosting owns the worker pool):

- `DecodeScheduler` — bounded `Channel<DecodeRequest>` → N=min(4,cores) workers → `Channel<UploadRequest>`. Workers fetch (disk `wavee-artwork://`, HTTPS `i.scdn.co`), **constrained-decode to the bucket size** via the `IImageCodec` leaf (`FluentGpu.Windows/Wic/`, behind the portable interface), into a **recycled CPU staging slab** (`SlabAllocator<StagingBlock>`). Each request carries a **per-binding request-epoch** (see zero-alloc story) and `Priority {Visible,Overscan,Prefetch}`. `Cancel(ticket)` on row-recycle/unmount; channel overflow drops lowest-priority off-screen requests.
- `ResidencyManager` — LRU by `LastUsedFrame`, **pins protected, eviction runs at frame START** (phase 1), mirroring the glyph-atlas rule. Decision: **LRU, not LFU** (scroll working sets are recency-dominated). Soft budget 192MB / 1024 count, hard 384MB. **Pin-before-admit**: the just-uploaded entry is pinned *before* the trim sweep runs (the documented WaveeMusic self-eviction race, fixed here in the contract). Eviction bumps `ContentEpoch`, sets `Evicted`, and frees the GPU texture through the **existing deferred-delete ring keyed by in-flight fence** (§4.7).

**The RHI delta (REQUIRED, not a fold).** The seam has *no* texture upload. ADD:

```csharp
// ICommandEncoder (Rhi iface) — NEW
void CopyBufferToTexture(BufferHandle staging, TextureHandle dst, in TextureRegion region);
// + a dedicated texture-staging ring in FluentGpu.Windows/D3D12/, MB-sized, fence-gated reset (NOT the instance UploadRing).
```

And — folding the fourth critique's CreateTexture-in-hot-window catch — **pre-allocate a pool of textures per bucket at startup**; phase 13 only does `CopyBufferToTexture` into a recycled bucket texture + `ContentEpoch++`. `CreateTexture` (which allocates a `HandleTable` slot + a `ComPtr` root — a managed allocation) **never runs in phases 6–13 steady state**; it is cold-path pool growth only. Texture-pool budget is stated explicitly (Σ bucket² × 4 × pool-depth), so the phase-13 row in the loop table is *honestly* 0-alloc.

**PAL delta:** none for images (codec leaf produces CPU bytes).

**13-phase placement.** P1 `ResidencyManager.EvictSweep` + drain worker-completion flag. P4 `UseImage`/`UseMosaic` cache probes (edge alloc only). P7 `CrossFade` 0→1 on newly-resident (PaintDirty). P8 resolve `ImageHandle→ImageRealization`; resident ⇒ `DrawImageCmd`, else placeholder quad; `ResidencyManager.Touch`; **pin authority lives here** (the node *recorded this frame* is the one that pins — resolving the phase-4-request-vs-phase-8-pin ambiguity the critique raised). P9 atlas instances merge by page. P12 mount pins / unmount cancels+unpins; submit decode requests for newly-visible nodes. **P13 `UploadRing.Drain`: time-sliced `CopyBufferToTexture` into pooled bucket textures, byte-budgeted in two lanes** (small thumbs vs large art/bakes) so a fling realizing ~30 thumbs fills in 1–2 frames while 512px bakes are rate-limited — *not* the unjustified flat "4 textures/frame".

**Zero-alloc / off-thread story.** Decode + fetch are entirely off the UI thread (two bounded `Channel`s + `IPlatformAppLoop.Post<TState>` struct-state marshal — no closure box). The UI thread touches only `ImageRefTable`/RHI/staging-ring, all on the confined thread. The **request-epoch survives slot recycle** (stored in the `UseImage` hook cell via `UseRef`, stamped into `DecodeRequest`/`UploadRequest`): on a residency callback, the result is dropped if the cell's current epoch ≠ the upload's epoch. This defeats the "row recycled to a new track, late callback flashes wrong art" bug — *not* relying on `NodeHandle` generation, which does **not** bump on recycle (only on free). **Palette extraction stays app-side and is fed the worker's CPU staging block** (or an optional 16×16 downsample emitted alongside the bucket decode) — **no GPU `ReadbackImage`** (a readback is a UI-thread device stall and unnecessary since the decoder already holds the pixels).

**Worst case — 10k-track scroll to bottom.** Virtualization realizes ~50 rows; each `UseImage(48→bucket64)` requests at `Visible`, overscan at `Overscan`. Fast scroll: off-screen pending decodes `Cancel`'d on recycle, channel drops overflow; resident 48px thumbs pack into the atlas → visible art = 1–2 draws; 50×64²×4 ≈ 0.8MB, nowhere near budget; upload byte-budgeted at P13 → no jank. **Album-art wall (Home):** atlas + LRU; off-screen shelves evicted, re-requested on return.

---

### 3.2 Virtualized Collections

**Requirement.** The #1 critical gap. Every list (`TrackDataGrid`, `TrackListView`, shelves, Liked Songs 50k) and the *entire* image-residency/pin budget rest on virtualization, yet only the keyed-LIS **diff** is named reusable. WaveeMusic needs 10k–100k items, incremental/lazy load (`LazyTrackItem` skeleton→real), grouping with variable-height image-bearing headers (Liked Songs), and the recycle-correctness the critiques hunt.

**Design.** **Virtualization is a layout participant + a hook trio, not a control and not a new phase.** Core decision (KEEP — blessed): **realize-window-as-keyed-children.** A virtual node has `LayoutKind.VirtualList/VirtualGrid` + a datasource handle; each frame it computes `[first,last)` from `ScrollOffset`+viewport+overscan, calls the dev's `RenderItem(i)` *only* for the window, and hands those to the **existing keyed-LIS reconciler** with stable `ItemKey`s. Recycling **is** `FreeNode`/`CreateNode` over the slab free-list — no second `RecyclePool` layer (avoids WinUI's `ElementPool` COM-detach pain).

```csharp
VirtualHandle UseVirtual<T>(int itemCount, RenderItem<T> renderItem, GetItem<T> getItem,
                            KeyOf<T> keyOf, in VirtualSpec spec, ReadOnlySpan<DepKey> deps);
InfiniteCollection<T> UseInfiniteCollection<T>(int totalCount, FetchPage<T> fetchPage, ...);  // composes UseInfiniteResource
void UseVisibleRange(VirtualHandle v, Action<Range> onChange, ReadOnlySpan<DepKey> deps);     // prefetch/image-warm bridge
```

`RenderItem/GetItem/KeyOf` are static-friendly fn-pointer-shaped delegates (no per-row closure); `ItemKey` is a 16B POD interning into the existing `StringId` key space so the LIS diff is unchanged.

**Honest alloc claim (folded — the central correction).** Within-window scroll that does **not** cross an item boundary is a **transform-only frame** (phase 7, ~0). Crossing a boundary re-renders only the **delta** rows. But the delta's phase-4 render **allocates** Gen0 `Element` records + **one managed `Element[]` window chunk** (`architecture-spec` §5.7 pins child storage to managed `Element[]`; `painpoints` §44 names this exact scenario). So the claim is: **phases 6–13 paint machinery is 0-alloc; phase 4 realize-delta allocates bounded Gen0.** The window buffer uses **`ArrayPool<Element>.Shared`** (sized to window+overscan, reused), **not** the cap-32 `ObjectPool` (the critique's CRITICAL catch: cap-32 *overflows precisely during list realization*, `painpoints` §99). The mitigation is incremental: only ENTER rows re-render; survivors keep their `Element` via component memoization.

**Variable height + anchoring.** Uniform fast path (`MeasureItems=false`): `ContentSize = n×extent`, `VisibleRange` is O(1) arithmetic. **Decision (folded):** track rows are `MinHeight=56` not fixed (wrap/equalizer/density), so **Wavee pins a fixed row height per density at the app layer** to keep the uniform path valid; surfaces that genuinely vary (group headers, hero-as-first-row) use the variable path: a **slab-backed** (NOT arena — the critique's arena/slab contradiction, fixed) per-item extent table with a Fenwick/BIT for O(log n) offset↔index, estimated-then-corrected extents, and **scroll-anchoring on the topmost visible item's `ItemKey`** so an above-viewport correction doesn't jump the viewport. **Decision: `MeasureItems=true` is forced whenever `GroupHeaderTypeId≠0`** (headers are variable). **Critical feedback-loop break:** `UseImage` returns `NaturalSize` but the dev must bind row height to the *fixed decode bucket*, never the late-arriving natural size — otherwise decode→measure→relayout loops.

**Recycle invariants (folded, pinned now regardless of remaining virtualization detail):**
1. List **rows never bind palette-derived brushes** — `WantPalette=false`, `Fill` = T0 static token handles only. Only hero/now-playing/right-panel (O(1) per page) bind `UseDerivedBrush`. (Resolves the §3.3-vs-§3.1 contradiction in the source designs.)
2. On recycle to a new key, the reconciler **re-runs the component** (palette/image deps change → re-request) OR explicitly **clears any derived-brush/image column** on the recycled slot, so no stale hero/art handle survives.
3. Per-range `CancellationTokenSource` in `VirtualState`: when a range exits overscan+prefetch margin, cancel its CTS (page fetch + image decode).

**Grouping.** Headers are logical items in the same flat index space; the **app supplies a flat `index→(kind,payload)` projection** (the virtualizer does not group). A regroup is a `deps` change that invalidates the extent table, resets the anchor to a stable post-regroup key, and re-seeds `ContentSize`. Sticky group-header pin = a **transform write in phase 7** (`min(ScrollOffset, nextHeaderOrigin−headerExtent)`), excluded from clean-span memcpy via `NodeFlags.StickyPinned`.

**DrawList/RHI/PAL deltas:** **none** beyond flags. **Scene/Foundation deltas:** `LayoutKind.VirtualList/VirtualGrid`; `NodeFlags.VirtualRangeDirty (1<<13)` + `StickyPinned (1<<14)` (contiguous free bits; explicitly *not* `NodeFlags.Realized=1<<12`, which means "cached text/path realization" and must not be confused with "row in the realized window" — folded naming fix); a slab-backed `VirtualState` column.

**13-phase placement.** P2 scroll updates `ScrollOffset`; sets `VirtualRangeDirty` only if the offset crossed a boundary. P4 if `VirtualRangeDirty`/datasource-version changed, `RenderItem` the *new* window. P5 keyed-LIS diff → enter `CreateNode`, exit `FreeNode`. P6 `VirtualLayout` two-pass (extents → realized rows). **P6.5** `UseVisibleRange`/`ScrollToIndex` — see the ratification note below. P7 `-ScrollOffset` → viewport `LocalTransform` (transform-only). P8 window subtree records; clean spans memcpy.

**Off-thread / latency.** A parallel `Channel<PageFetch>` rides the existing worker+`Post` edge. **Corrected latency:** a worker page-result `Post` marks dirty + schedules **frame N+1** (no same-frame phase-3 apply — `architecture-spec` §5.4 has no synchronous re-loop in v1). On apply, re-derive the *current* realized window and `PaintDirty` only rows still realized; off-screen fills land in the data slab only. State the +1-frame minimum from page-arrival to pixels.

**Worst case — 10k cold open + fling.** `ContentSize = 10k×56` instantly; ~40 skeleton rows; first page enqueued; page arrives → `Post` → ~40 `IsLoaded` rows `PaintDirty` → re-record 40, memcpy the rest (no reset of 9,960). Fling: in-window frames transform-only; boundary frames diff a 40-key window (~5 enter/exit, slab free-list, 0 paint-alloc); blown-past fetches cancelled. **Shimmer/skeleton = a per-row animated gradient FILL** (gradient-atlas row + animated uv in phase 7), explicitly **not** a `PushLayer` offscreen RT (folded — keeps the zero-offscreen-pass budget).

---

### 3.3 Theming & Accent Reactivity (incl. album-art dynamic color, High Contrast)

**Requirement.** The existing theme seam is *static* (3 baked `[ThemeTokens]` blobs, swap `ThemeContext.Current`). WaveeMusic needs **dynamic, per-element, runtime-computed brushes** — album-art palette → 4-stop hero gradients, right-panel tint stacks, accent-pill foreground-luma — that recolor on every track change, theme-reactive, at 60fps, 0 per-frame alloc, plus live OS accent/HC reactivity.

**Design.** Three brush tiers, **all converging on one `BrushHandle` into the existing `BrushTable`** (content-hash dedup) and the **shared `RGBA16F` gradient atlas** — so the hot paint path (phases 8–10) is **unchanged** and **no new theming DrawList opcode is needed** (KEEP — the load-bearing, verified-correct decision):

| Tier | Source | Reactivity | Assembly | Status |
|---|---|---|---|---|
| T0 static tokens | `Tok.*` baked Light/Dark/HC | theme swap | `Dsl`+`SourceGen` | exists |
| T1 live system | `ISystemColors` PAL seam (accent + HC palette) | OS change | `Pal`+`Hosting` | ADD §below |
| T2 dynamic palette | album-art extraction → derived brush families | per track | **`FluentGpu.Theme`** | ADD |

**T1 — PAL `ISystemColors`** (ADD; `FluentGpu.Windows/Pal/` reads `HKCU\…\Personalize` + `SPI_GETHIGHCONTRAST` + accent registry, once at startup + per `WM_SETTINGCHANGE("ImmersiveColorSet")`, bumping `uint Epoch`). The reactive context is **`Context<uint>` over `Epoch`** (DepKey-projectable, boxless) — **not** `Context<SystemTint>` (folded: a 112B fat struct cannot project into a 16B `DepKey`; that would box). `UseSystemColors()` reads the fat snapshot by value from the stable `ISystemColors` instance; the re-render trigger is the Epoch context change. Flow reuses the existing `WM_SETTINGCHANGE → ThemeChanged WindowEvent → dirty-everything` path; no new phase.

**T2 — `FluentGpu.Theme`** (ADD, portable, deps `Foundation` only): `PaletteExtractor` (worker), `BrushDeriver` (UI thread), `PaletteCache` + `BrushCache`. **Palette model (folded):** *not* a 2-color `{Primary,Accent}` — that throws away the per-theme tier the real `PaletteGradientCompositor`/`RightPanelThemeResolver` need (they use `BackgroundTinted`, and a *Light-tuned and Dark-tuned* tier, to avoid the "collapses to muddy near-black at partial alpha on dark covers" bug `TintColorHelper` exists to dodge). ADD an 80B `Palette {BackgroundDark, BackgroundTintedDark, BackgroundLight, BackgroundTintedLight, Accent}`, both tiers extracted on the worker. Extraction runs on the asset worker off the **CPU staging block** (`stackalloc` is wrong here — the real accumulator is 5 longs × 4096 buckets ≈ 160KB → use a **worker-thread-pooled `long[]`**, reused, off the frame loop). `BrushDeriver` evaluates POD `BrushRecipe` × `Palette` × theme → `BrushHandle` (hero 4-stop gradient baked as one atlas row; pill-foreground = luma>0.63?Black:White; HC → `sys.HcHotlight`, bypassing palette). Recipes are hand-authored `static readonly` POD (AOT-clean).

**Hooks:** `UseTheme()`, `UseSystemColors()`, `UseHighContrast()`, `UseDerivedBrush(in BrushRecipe, in Palette)` (deps `(recipe,palette,theme,epoch)`), `UseDynamicColor(albumArt)` = `UseImage(wantPalette:true)` returning `Palette?`.

**Eviction safety (folded — the load-bearing correction).** The source design wrongly claimed `IsLive` *auto-repairs* clean spans. It does **not** — `IsLive` is a validity *gate*, never a scheduler. ADD the spec's glyph-atlas discipline verbatim for derived brushes: (1) derived-brush eviction runs **only at frame START**, anything referenced by a live command this frame is ineligible; (2) on free/`InvalidateThemeDependent()`, **walk a small `SlabAllocator`-backed `derived-brush→node` back-reference multimap** (only hero/right-panel nodes — tiny) and **`MarkPaintDirty`** each so phase 8 actually re-records. Also ADD the §4.5 clause amendment: *clean span valid IFF every handle IsLive AND, for `GlyphRunRef` and `ImageRef` handles, the realization `ContentEpoch` is unchanged* (this was always implied for glyphs; making it explicit for `ImageRef` is **new clean-span-validator + batcher code**, not a "verbatim reuse").

**13-phase placement.** T1 enters P1 pump → P2 dispatch (token swap + cache invalidate + dirty roots), recomputes P4. T2 publish lands at a phase boundary via `Post`, derives P4, paints P8; **frame-start (P1) derived-brush eviction**. Recolor cross-fade ticks P7 — **opacity-only over two pre-derived endpoint brushes** (folded: derive start+end once, animate `Opacity` between two stacked fills, exactly like `CrossFadeImage`), so there is **no per-tick gradient-atlas re-bake** thrash.

**Cancellation (folded).** `ImageDecodeRequest`/palette request carries a `uint TargetGen` (now-playing track sequence); guard at worker dequeue and at `PaletteCache.Publish` (drop stale). Debounce backdrop bakes (5 skips → 1 bake). This directly fixes the `fix-artist-nav-flicker` class of bug.

**Worst case — rapid track-change recolor.** Each change: 1 worker decode+extract (~0.2ms off-thread, already paying decode), 1 struct-state `Post`, then ~7 `Derive` (cache-miss) → BrushTable interns + 1 atlas bake; cache-hit on a revisited track = instant; only the chrome root subtree is PaintDirty (the 10k grid memcpy's clean). Theming contributes 0 per-frame **except during the 220ms cross-fade, which is opacity-only and re-derives nothing** (corrected claim).

---

### 3.4 Backdrop / Effects / Animation (Mica, Acrylic, editorial backdrop, connected/implicit anim, video chrome)

**Requirement.** Window Mica; in-app Acrylic (toast/add-to-playlist over content); the editorial 6-layer baked backdrop (blur+radial+linear+noise+vignette, LRU 4); connected animation (art flies grid→now-playing); implicit show/hide + hero pop-in + scroll-fade; **video** (theatre/PiP/sidebar) composited from an external `MediaPlayer`.

**Design.** Separate the three meanings of "backdrop" hard (KEEP — verified correct):

1. **Window Mica/Acrylic = PAL, not our pixels.** ADD `IBackdropSource.SetWindowBackdrop(NativeHandle, HostBackdropKind, tint)` (`FluentGpu.Windows/Pal/` → `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` / a DComp backdrop sibling visual below our swapchain visual). Our root clears transparent (premul 0); DWM composes Mica through. **Zero renderer change.** HC → `None` + opaque fill. macOS → `NSVisualEffectView`.

2. **Editorial baked backdrop = persistent baked surface, NOT the per-frame LayerPool** (folded). ADD `UseImageBackdrop(src, palette, sizePx, in BackdropRecipe) → ImageHandle` whose texture is baked via the `IEffectRunner` chain (D2D1 Gaussian + our gradient/SDF passes + the **optional** `Effects.D2D1` ComputeSharp noise shader — flagged as the optional non-portable leaf, carrying the documented straight-vs-premul-alpha trap). Give it its **own small persistent-RT cache** (LRU **cap 2**, bake **downscaled ~0.5×** then upsample — blur is low-frequency), so it cannot starve the shared LayerPool's group-opacity/clip RTs. **Cache the blurred SOURCE separately, keyed `(uri,sizeBucket)`** — a track *recolor that keeps the same art* recomposites only the 5 cheap procedural quads over the already-blurred source; only a genuine **art change** pays the expensive 60-DIP blur. Bake job has a `BakeTicket` with `Cancel`/`SetPriority`; rapid track-changes debounce to one bake. If RT alloc fails → flat dominant-color fill (not the spec's "visually-wrong per-instance alpha").

3. **In-app Acrylic = live-sample blur, as an explicit two-pass FrameGraph step** (folded — the source design's "sample the live canvas RT under the overlay" has a read-after-write hole). ADD: snapshot the behind-region into a small layer RT at the `PushLayer{Effect=AcrylicBlur}` boundary (the existing child-into-layer-RT machinery), barrier to SRV, blur (small sigma, cheap), composite the overlay above. This depends on `OQ-7` resolving to the persistent-canvas-RT path — **mark `UseImageBackdrop`/Acrylic BLOCKED on `OQ-7`** (which I rule in favor of the canvas RT, since it doubles as the partial-present surface and the backdrop source). `VisualKind.Backdrop`/`DrawBackdropCmd` (exist as stubs) carry the live case.

**Connected / implicit animation** — all on the existing AnimTrack/phase 7 (KEEP), with the **lifecycle pinned against the unmount contract** (folded — the source design free-ran a Detached node, but `reconciler-hooks` §4.4 runs hook cleanups *synchronously* on unmount and recycles the handle):
- ADD `UseImplicitTransition(onShow, onHide)`: on unmount, hook cleanups still run synchronously (subscriptions die), but the node's **paint columns + a refcount-pin on its `ImageHandle`** are moved to a dedicated **`DetachedAnim` slab** (not the recycled topology slot); phase 7 advances detached tracks from a separate list; cascade-free + gen-bump at track completion in phase 13. `Scale/Translate` write `LocalTransform` (TransformDirty); `BlurSigma`/gradient-alpha write `EffectAux` (PaintDirty); **never LayoutDirty** (pop-in is `Scale`, not a size change).
- ADD `UseConnectedAnimation`: phase 5 snapshots the source world-rect + **pins the source `ImageHandle` for the animation's lifetime** (`MediaService.Pin`, released on completion/cancel — fixes the fast-track-change "overlay samples a dead slot" bug); phase 6.5 reads dest `Bounds` + seeds a transform track on a transient overlay node owning the pinned texture; superseding navigation cancels the prior track + releases the pin. Concurrent transform tracks on one node (pop-in + connected) compose as a fixed-order matrix multiply.
- ADD `AnimTrack.DrivenClock` (a `ref float` source) for scroll-fade and **playback-synced lyrics** (`IPlaybackClock`): time-driven by playback ms, **never LayoutDirty**.

**Synced lyrics** (over the Text seam, in `FluentGpu.Media.LyricsLayoutEngine`): line layout via `ITextShaper` → cached `GlyphRunRealization`. **Per-syllable color = per-INSTANCE glyph color written by the phase-7 AnimTrack into the instance data at batch time** (folded — *not* a `BrushHandle` re-bake, which would mint a new handle every tick and break the clean-span invariant, nor a gradient-atlas row lerp, which needs the missing texture-upload path). The active line is **PaintDirty + re-records its single glyph run every frame** (trivially within budget — drop the "clean-span reuse for the active line" framing; caching applies to *shaping*, not per-frame instance emission). Line scroll = `LocalTransform` translate-Y. Backdrop = `UseImageBackdrop`. 3D fan = `PushLayer{Effect=Transform3D}` via the optional effects leaf (perspective is out of core 2.5D scope).

**Video — the present-tree redesign (ADD; NOT a fold onto §5.1).** §5.1 builds **one** opaque swapchain visual (`CreateVisual → SetContent(swapchain) → SetRoot`). Video needs a **multi-visual DComp tree**. ADD:
- **PAL `IVideoPresenter`** (`FluentGpu.Windows/Pal/` → DComp; POD `VideoSurfaceId`): `CreateSurface`, `Place(id, deviceRect, opacity, z)`, `SetVisible`, `Destroy`, `GetMediaPlayerSink` (the app binds its `MediaPlayer`/`MediaPlayerElement` to this). FluentGpu **never touches video pixels** (KEEP — the single best decision: the heaviest continuous work is off our single thread by construction, turning v1's worst limitation into a non-issue).
- **Present-tree amendment to §5.1:** root visual with N children — the **UI swapchain visual z-above a video child visual** whose content is the external surface. **Transparency protocol:** the UI back buffer is **cleared transparent (premul 0) in the `DrawVideoCmd.Dst` region** (a hole-punch), so the video child shows through; scrim/transport/rounded-PiP-corners are normal DrawList quads in the topmost UI visual, composited over by z-order. ADD `DrawVideoCmd {VideoSurfaceId Surface; RectF Dst; ImageHandle PosterBlur, AlbumArt; float VideoReady}` with its **own sortkey PassClass ordering the alpha-punch BELOW all chrome**. Art/poster lower layers + the 3-layer crossfade (180/220ms) are phase-7 composition animations.
- **`UseVideoSurface(owner, priority)`** + `VideoSurfaceRegistry` (portable port of `ActiveVideoSurfaceService`: priority-arbitrated single surface, atomic handoff = no black frame; theatre>PiP>sidebar). PiP drag moves the DComp child visual off-loop via `Place`, **with the canvas-RT hole committed in lockstep in the same phase-11 DComp Commit** (folded — the two-clock tear at the PiP edge). Partial present: **re-punch the hole whenever any node overlapping the video rect is in the damage set** (inflate the video node's damage to its own rect).

**Foundation/Scene deltas:** `EffectAux` cold slab column (animatable blur/exposure/tint), `BackdropRef`, `DrawVideoCmd`, `ISceneBackend.WriteAnim`, `AnimTrack.DrivenClock` — **all net-new contract additions** (filed as amendments; *not* "already stubbed" — only `DrawBackdrop`/`PushLayer{Effect}`/`VisualKind.Backdrop`/`NeedsLayer` are the real existing stubs).

**Worst case — 60fps video + animated UI.** Video composites on the OS compositor thread independent of our loop; even if our single UI thread is busy, the committed video keeps presenting. Our per-frame cost: re-record the scrubber (tiny damage), a hole-punch alpha-clear, `Place` transform poke. **No video pixel work, no relayout on our thread.**

---

## 4. NEW PUBLIC API — what the Wavee dev writes

### A playlist screen (10k rows, virtualized, lazy-loaded, palette-tinted page header)

```csharp
sealed class PlaylistScreen : Component {
    public override Element Render(RenderContext ctx) {
        var sortVer = ctx.UseSelector(s => s.Playlist.SortVersion);
        var pal     = ctx.UseDynamicColor(Model.HeaderArtUrl);          // album → Palette? (worker-extracted)
        var header  = ctx.UseDerivedBrush(Recipes.HeaderWash, pal ?? Palette.Neutral);  // ONE derived brush, page-level

        var tracks = ctx.UseInfiniteCollection<TrackVm>(
            totalCount: Model.TrackCount,                                // 10_000
            fetchPage:  Model.FetchTrackPage,                            // worker, batches of ~200
            keyOf:      static (i, in TrackVm t) => new ItemKey(t.Uri),
            renderItem: static (i, in TrackVm t) => t.IsLoaded ? TrackRow(i, in t) : SkeletonRow(i),
            spec: new VirtualSpec {
                Orientation = Vertical,
                EstimatedItemExtent = Model.DensityRowHeight,            // app PINS fixed height per density → uniform O(1)
                MeasureItems = false,
                Overscan = OverscanPolicy.Pixels(800),
                GroupHeaderTypeId = 0,
            },
            deps: ctx.Deps(sortVer, Model.Density));

        ctx.UseVisibleRange(tracks.Handle, static r => ImageWarmer.Warm(r), ctx.Deps());

        return VStack(
            PageHeader(Model.Title).Fill(header),                        // T2 derived (page-level, O(1) — NOT per row)
            StickyHeader(TrackColumnHeader()),                           // sibling of viewport → doesn't scroll
            VirtualViewport(tracks));                                    // the scroll region
    }

    static Element TrackRow(int i, in TrackVm t) {
        var art = UseImage(t.ArtUrl, decodePx: 48);                      // bucket-64, atlas-packed, cross-fades on ready
        return HStack(
            Image(art).CornerRadius(4).Stretch(UniformToFill).Size(40,40),  // ROW art: NO palette brush (invariant 1)
            Text(t.Title).Style(Body).Foreground(Tok.TextPrimary),       // T0 static token only
            Text(t.Duration).Foreground(Tok.TextSecondary));
    }
}
```

### A now-playing screen (palette backdrop, cross-fade recolor, lyrics, connected art, video)

```csharp
sealed class NowPlaying : Component {
    public override Element Render(RenderContext ctx) {
        var track = ctx.UseTrack();
        var art   = ctx.UseImage(track.ArtUrl, 512, placeholderTint: track.PlaceholderColor);
        var pal   = art.Palette ?? Palette.Neutral;

        var backdrop = ctx.UseImageBackdrop(track.ArtUrl, pal, ctx.SurfacePx,    // baked once; cap-2 LRU; recolor reuses blurred src
                          new BackdropRecipe(BlurDip: 60, Exposure: -0.4f, Radial: pal, Linear: pal, Noise: true, Vignette: true));
        var heroPills = ctx.UseDerivedBrush(Recipes.PillFill, pal);
        var connArt   = ctx.UseConnectedAnimation();                            // art flew in from the grid card
        var lyrics    = ctx.UseSyncedLyrics(track.Lyrics, ctx.PlaybackClock);   // per-syllable color on the playback clock
        var video     = ctx.UseVideoSurface(VideoOwner.NowPlaying, priority: 10);

        return ZStack(
            Backdrop(backdrop).Fill(),                                          // DrawImageCmd (baked RT)
            track.HasVideo
                ? VideoQuad(video).Fit(Letterbox).FadeIn(220.ms())             // DrawVideoCmd hole-punch; chrome draws over
                : Image(art).CornerRadius(8).Tag(connArt.Key("art:" + track.Uri)),
            VStack(
                Lyrics(lyrics),                                                 // DrawGlyphRun, per-instance color, phase-7 clock
                Button("Save").Fill(heroPills).Foreground(ctx.UseDerivedBrush(Recipes.PillForeground, pal)))
              .Padding(24))
          .Transition(onShow: stackalloc[] { new TransitionSpec(Opacity, 0, 1, 220.ms(), Easing.Smooth) });  // recolor = opacity crossfade
    }
}
```

---

## 5. SPEC FOLD-IN — precise amendment checklist

References are to `architecture-spec.md` (arch), `subsystems/gpu-renderer.md` (rend), `subsystems/reconciler-hooks.md` (hooks), `foundations.md` (found).

**New assemblies (arch §3 module graph):**
- ADD **Media subsystem** (portable, in `FluentGpu.Engine/Media/`; deps Foundation + Rhi/Text iface) — `ImageCache`, `DecodeScheduler`, `ResidencyManager`, `VideoSurfaceRegistry`, `LyricsLayoutEngine`. New Windows leaf **`FluentGpu.Windows/Wic/`** behind portable `IImageCodec`.
- ADD **`FluentGpu.Theme`** (portable; deps Foundation) — `PaletteExtractor`, `BrushDeriver`, `PaletteCache`, `BrushCache`.
- Both referenced **only by `Hosting`**; acyclicity preserved.

**Foundation tables/POD (found §4.3):**
- ADD `ImageRefTable : SlabAllocator<ImageRealization>` (content-epoch + pin + LRU) using existing `HandleKind.ImageRef`; shared `BGRA8` small-image atlas page.
- ADD `ColorF`, `ThemeKind`, 80B tiered `Palette`, `BrushRecipe`, `DerivedKey`, `ImageDecode{Request,Result}` (with `uint TargetGen`), `ItemKey` (16B), `VirtualState` column, `EffectAux` cold slab, `BackdropRef`.

**DrawList opcodes (rend / found §4.5):**
- AMEND `DrawImageCmd`: `ImageHandle` indirection + `Radii` + `CrossFade` + `Stretch` + `Clip` (was raw `TextureHandle`).
- ADD `DrawVideoCmd` (hole-punch; own sortkey PassClass below chrome).
- NO `DrawMosaicCmd`, NO `DrawLyricsRun` (Grid-of-4 + per-instance glyph color suffice).
- AMEND batcher UV-resolve to add the `ImageRef` branch (atlas UVs resolved at batch time, never baked — reconcile with the §9 image-brush "baked TextureHandle" path).
- PROMOTE `OQ-4` (small-image atlas) from future to **v1 required**.

**Clean-span invariant (arch §4.5):**
- AMEND to: *valid IFF every handle IsLive AND, for `GlyphRunRef` and `ImageRef` handles, the backing realization `ContentEpoch` is unchanged.* This is new clean-span-validator + batcher code, not a verbatim reuse.

**RHI seam (arch §4.7) — REQUIRED:**
- ADD `ICommandEncoder.CopyBufferToTexture(BufferHandle staging, TextureHandle dst, in TextureRegion)` + a dedicated MB-sized **texture-staging ring** in `FluentGpu.Windows/D3D12/`, fence-gated. Stop claiming texture upload rides the instance `UploadRing` (UploadRing = instance/vertex/index only).
- ADD a **startup-allocated per-bucket texture pool**; phase-13 does only `CopyBufferToTexture` into recycled textures (no `CreateTexture` in 6–13).

**PAL seams (arch §4.7 / §5.1):**
- ADD `ISystemColors` (accent + HC + Epoch), `IBackdropSource` (Mica/Acrylic), `IVideoPresenter` (POD `VideoSurfaceId`, DComp sibling-visual placement).
- AMEND present tree §5.1: from one swapchain visual to a **multi-visual DComp tree** (UI swapchain z-above video child) + a **back-buffer transparent-region (premul-0) clear protocol** for `DrawVideoCmd.Dst`.

**Hooks (hooks §765/§766):**
- ADD `UseImage`, `UseMosaic`, `UseImageBackdrop`, `UseVideoSurface`, `UseSyncedLyrics`; `UseVirtual`/`UseInfiniteCollection`/`UseVisibleRange`; `UseTheme`/`UseSystemColors`/`UseHighContrast`/`UseDerivedBrush`/`UseDynamicColor`; `UseImplicitTransition`/`UseConnectedAnimation`.
- The reactive system context is **`Context<uint>` over Epoch**, not `Context<SystemTint>`.
- `UseVirtual` recycling uses the **existing `KeyedListDiff`** retargeted to handles (hooks §765, already named); window buffer = `ArrayPool<Element>` **not** the cap-32 `ObjectPool` (painpoints §99).

**Frame-loop phases (arch §4.8):**
- P1 image/derived-brush **frame-start eviction** + worker-completion drain.
- P7 image cross-fade / lyric playback-clock color / scroll-fade / recolor opacity-crossfade — all Paint/TransformDirty, never LayoutDirty.
- P8 image resolve + **pin authority** (recorded node pins) + video hole-punch quads.
- P11 `IVideoPresenter.Place` + canvas-RT hole committed in the same DComp Commit.
- P13 byte-budgeted two-lane `CopyBufferToTexture` + `ResidencyManager.Admit` (pin-before-trim) + deferred-free evicted textures by fence.
- **Ratify phase 6.5** (hooks §812 still flags it as an "Amendment request" while arch §4.8 lists it as ratified — **resolve in favor of ratified**) and confirm its v1 semantics are *setState→mark-dirty→N+1*, **no synchronous bounded re-loop** (arch §4.8 wins over hooks §347/§781), or re-home `UseVisibleRange` to phase 7.

**Honest-claim edits (painpoints §44/§99):**
- Restate: phases 6–13 are 0-alloc for the **paint machinery**; phase-4 realize-delta allocates bounded Gen0 (`Element` records + one `Element[]` chunk).

**Risk-register additions (painpoints §4):**
- Hand-rolled DComp multi-visual present-tree correctness (transparency/z/hole-punch).
- Texture-staging ring + bucket-pool sizing (capacity-planning cliff).
- Editorial-backdrop persistent-RT memory + the optional ComputeSharp noise leaf (AOT/portability + premul-alpha trap).
- Detached-anim lifetime vs synchronous hook cleanup; connected-anim source-texture pinning.
- Virtualization recycle correctness (request-epoch survives recycle; decode→measure feedback loop).

---

## 6. WORST-CASE & RISKS

| Scenario | Behavior | Mitigation |
|---|---|---|
| **10k-track scroll-to-bottom** | ~50 rows realized; in-window frames transform-only (P7); boundary frames diff a 50-key window + 5 `FreeNode`/`CreateNode`; thumbs atlas-pack to 1–2 draws | Cancel off-screen decodes on recycle; bounded-channel drop lowest-priority; byte-budgeted P13 upload; fixed row height → uniform O(1); **phase-4 Element[] via ArrayPool, only ENTER rows re-render** |
| **Album-art wall (Home, 400 thumbs)** | Horizontally-virtualized shelves; resident small art in atlas; off-screen evicted | LRU + pins; atlas → ~80 draws; `OQ-4` promoted to v1; `ImageLoadingSuspension`-style scroll-restore gate enqueues at low priority |
| **Rapid track-change recolor** | Worker decode+extract (0.2ms off-thread) → `Post` → ~7 derives (cache-hit instant on revisit) → chrome-root PaintDirty only; backdrop recomposites 5 quads over the cached blurred source | `TargetGen` guard drops stale; debounce 5-skips→1-bake; `BakeTicket.Cancel`; recolor = opacity-only crossfade (no atlas thrash); blurred-source cache keyed `(uri,size)` so only art changes pay the 60-DIP blur |
| **60fps video + animated UI** | Video on OS compositor thread, independent of our loop; our cost = scrubber re-record + hole-punch + `Place` poke | External DComp child visual; never enters DrawList/batcher/upload; hole + visual move committed in one DComp Commit; re-punch on overlapping damage; PiP persists across nav as a retained visual |
| **Single-thread v1 implications** | A cold now-playing open (first 512px decode + 60-DIP backdrop bake) is a one-frame hitch; layout/tessellation/record/submit/present all on the UI thread; a busy handler still stalls present (same shape as WinUI, `painpoints` §3.2) | Placeholder-tint → cross-fade hides the cold open; bake is async/budgeted/downscaled; **video is the one place the topology helps** (off-thread by construction); the render-thread seam (quarantine 0→2) is the v2 categorical fix |

---

## 7. WHAT THIS FORCES ABOUT v1 vs v2 — build order

**Fine on single-thread v1 (build first, in this order):**
1. **Image pipeline + the RHI `CopyBufferToTexture` primitive + bucket texture pool.** Nothing renders WaveeMusic without it; decode is already off-thread; upload is a cheap byte-budgeted P13 copy. This is the prerequisite for *every* screen.
2. **Virtualization** (uniform fast path + ArrayPool window + request-epoch recycle). The load-bearing dependency for both lists and the image residency budget. Variable-height/grouping (Liked Songs) is a fast-follow.
3. **Theming T1 + T2** (palette off-thread, brush derivation on-thread, frame-start derived-brush eviction with the back-reference PaintDirty walk). Recolor is opacity-only, fully v1-safe.
4. **Window Mica + window-level chrome** (pure PAL, zero renderer cost).
5. **Implicit/connected animation + scroll-fade + lyrics** (all phase-7 composition + the DetachedAnim slab + per-instance lyric color). The active-line re-record is one tiny node/frame — fine on one thread.
6. **Video present-tree + `IVideoPresenter`.** Needs the §5.1 multi-visual amendment but is otherwise v1-safe *precisely because* the heavy continuous work is off our thread; this is the surface where single-thread v1 is an advantage, not a liability.

**Truly needs the render-thread seam / off-thread work (v2):**
- **Off-thread decode is already v1** (workers), but **off-thread *upload/bake* during a UI-thread stall** needs the render-thread seam: a cold backdrop bake or a burst of texture uploads currently competes for the single DIRECT queue + UI thread. Flipping the slot-reuse quarantine 0→2 and publishing the immutable `SceneFrame` lets record/batch/submit/present (and the upload/bake drain) run while the UI thread reconciles — converting the cold-open hitch and the upload-burst stall from "dropped frame" into "decoupled from present."
- **In-app live Acrylic at scroll velocity** (per-frame behind-region blur) and the **editorial bake** are the heaviest GPU items; they want the render thread before they are stress-safe under simultaneous scroll+video.
- **Time-sliced / off-thread reconcile of huge updates** (a 50k Liked-Songs regroup) is React-Fiber-class v2 work; v1 eats the regroup as one N+1 frame with a skeleton.

**Bottom line for the rewrite:** build the image pipeline + virtualization + theming first (they unblock every screen and are honestly v1-safe), layer animation/lyrics/Mica on top, and treat the video present-tree as a v1 deliverable that happens to be single-thread-friendly. Defer only the render-thread seam — and with it the categorical fix for the cold-open/bake/upload-burst hitches — to v2, exactly where `painpoints` already concentrates the risk.

---

**Grounding (verified against Wavee source):** `composition-image.md` (ImageCacheService LRU 200, pin-before-trim, 64/128/256/512 buckets, CrossFadeImage, mosaic 2×2, ImageLoadingSuspension, nav-cache surface release), `track-and-episode-ui.md` (TrackDataGrid 10k, density rows, LazyTrackItem skeleton, MinHeight=56, grouping, video badge, MiniVideoPlayer), `playback.md` (`MediaPlayerElement`/PlayReady video, `SpotifyVideoPlaybackTarget`, 1Hz interpolated clock), `Wavee.Controls.Lyrics` (Win2D NowPlayingCanvas, ColorThief, ComputeSharp D2D shaders, CJK romanization, SpoutDx cross-process video textures).
