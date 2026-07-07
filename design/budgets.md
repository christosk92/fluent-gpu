# FluentGpu — Consolidated Resource Budgets

> **Why this exists.** In a NativeAOT engine, native + GPU memory pressure *replaces* GC pressure, but
> budgets were scattered across 20+ docs. This is the single roll-up: every stated runtime budget,
> eviction policy, and failure behavior, plus the **gaps** where a subsystem owns a native/GPU resource
> with no stated budget. Owning docs remain authoritative for the deep design; this table is the index.
> Canonical contracts: [`SPEC-INDEX.md`](./SPEC-INDEX.md). Harvested from the design set: **2026-06-06**.

## 1. Budgets in force

| Subsystem | Resource | Budget / Limit | Eviction policy | Failure behavior | Source |
|---|---|---|---|---|---|
| Backdrop/Effects/Anim | `BackdropRtCache` (final composited RT) | cap-2 LRU; baked ~0.5× downscaled (~4× less RT memory) | LRU; freed via deferred-delete fence ring | RT alloc fail → `BackdropState.Failed`, flat dominant-color fill, logged once | backdrop-effects-animation.md:178,188,194,223,236 |
| Backdrop/Effects/Anim | `BlurredSrcCache` (blurred art source) | cap-4 LRU, keyed (uri,sizeBucket) | LRU | reuses cached source on recolor | backdrop-effects-animation.md:179,189 |
| Backdrop/Effects/Anim | `DetachedAnimSlab` (exit-anim nodes) | hard cap **64** concurrent (typ. 0–3) | grows ×2, never shrinks; at cap, snap oldest exits to end | bounded by 64-cap snap | backdrop-effects-animation.md:472,666 |
| Backdrop/Effects/Anim | phase-13 backdrop bake lane | ~1 bake/frame (low-priority lane) | debounce N changes → 1 bake; `TargetGen` drop-at-publish | stale-gen result dropped | backdrop-effects-animation.md:271,647,727 |
| Backdrop/Effects/Anim | in-app live Acrylic | v1: **at rest only**; scroll-velocity Acrylic = v2 | — | BLOCKED until canvas RT lands → flat tint | backdrop-effects-animation.md:271,661 |
| Foundation | `ObjectPool<T:class>` (RenderContext/HookState/scratch) | **cap 32**, explicit Reset | explicit Reset on return | overflows during list realize → use `ArrayPool<Element>` there | foundations.md:96; scene-memory.md:138; dotnet10:89,112,277 |
| Foundation | `SlabAllocator<T>` (all SoA columns/tables) | growth ×2; **never shrinks** | gen-bump on alloc+free; free-list reuse | footprint stable | foundations.md:791; scene-memory.md:127 |
| Foundation | `ChunkedArena` / `IVirtualMemory` | reserve-then-commit; O(1) add-chunk; **native high-water gates growth** (not GC tripwire) | EWMA high-water reclaim of committed (not reserved) chunks at idle | System-OOM on `Commit` = clean fatal exception, not corruption | scene-memory.md:129,149; pal-rhi.md:547; hardened-v1-plan.md:103,170 |
| Foundation | `Handle` generation | 32-bit; wraps at 2³² (~400 days/slot @ 60fps) | gen reset on wrap | astronomically-low ABA risk accepted | scene-memory.md:114; foundations.md §1.1 |
| GPU Renderer | `DamageAccumulator` / partial present | **≤16 merged IntRects**; full redraw if >16 or >~60% coverage | — | overflow → full-frame redraw | gpu-renderer.md:690 |
| GPU Renderer | DrawList arenas (render-private) | **≥3-deep** ring; pinned arrays | render-local rotation; reset behind fence | overflow → grow to high-water once (old ring deferred-freed); re-record capped once/frame then multi-pass; never truncate | gpu-renderer.md:33,162,746; scene-memory.md:419,469 |
| GPU Renderer | `UploadRing` (instance/vertex/index) | sized to high-water; grown ×2 on overflow, never freed | reset on fence completion | overflow → one-time grow (old ring deferred-freed) | gpu-renderer.md:663,831 |
| GPU Renderer | `LayerPool` (offscreen RT textures) | pooled, power-of-2 size buckets; D3D12MA placed | deferred-release keyed by in-flight fence | Layer RT OOM → degrade group opacity to per-instance alpha (no crash); full-redraw next frame | gpu-renderer.md:470,835 |
| GPU Renderer | frames-in-flight | **2** (configurable 2–3); OQ-8 | rings reset on fence; tables retained | — | gpu-renderer.md:677,861 |
| GPU Renderer | persistent canvas RT | single engine-owned RT | — | full-redraw fallback on >16 rects / >60% / resize / first frame | gpu-renderer.md:687,690 |
| GPU Renderer | sustained GPU stall | bounded per-frame UI block **T = one vsync** after buffers exhaust | — | bounded UI block (irreducible); transient hiccups absorbed | gpu-renderer.md:828 |
| GPU Renderer | tessellation Path lane | tessellation-fraction tripwire on curated corpus | geometry-realization cache LRU by slab pressure | CI fails if exceeded | gpu-renderer.md:327,418 |
| Media Pipeline | `ResidencyManager` resident image memory | **Soft 192 MB / Hard 384 MB**; SoftCount **1024** | LRU; frame-start sweep to <Soft; eligible if `PinCount==0 && Resident && LastUsedFrame < frame−~90`; pin-before-admit | over hard budget at admit → inline emergency trim | media-pipeline.md:259,271 |
| Media Pipeline | per-bucket texture pool | depths 64px×256, 128px×128, 256px×48, 512px×12 + 4 atlas pages (1024²); **≈52 MB** GPU | bucket textures returned to pool on evict behind both fences | exhaustion → cold `CreateTexture` (logged) or emergency evict-then-acquire | media-pipeline.md:318; pal-rhi.md:354 |
| Media Pipeline | `TextureStagingRing` | `StagingBytes = maxUploads × maxBucketBytes × framesInFlight` | bump; reset behind retired fence (phase 13) | one-frame exhaustion → two-lane byte budget; rolls to next frame (cross-fade hides); never sync stall | pal-rhi.md:336; media-pipeline.md:317 |
| Media Pipeline | phase-13 upload (two-lane) | lane A (thumbs ≤128px) high byte budget; lane B (256/512 art+bakes) ~1–2/frame | — | replaces flat "4 textures/frame" | media-pipeline.md:430 |
| Media Pipeline | `DecodeScheduler` channel | bounded `Channel<DecodeRequest>` **cap ~256** | drop lowest-priority off-screen (Prefetch then Overscan), never Visible | priority-drop; `Cancel` on recycle | media-pipeline.md:198,239 |
| Media Pipeline | decode worker count | **N = min(4, cores)** | — | — | media-pipeline.md:161 |
| Media Pipeline | `StagingBlock` slab (CPU pixels) | recycled `SlabAllocator<StagingBlock>`, bucket-sized | recycled after BOTH upload-admit AND palette-extract (2-bit refcount) | only off-loop alloc is OS fetch buffer | media-pipeline.md:200,243,344 |
| Media Pipeline | decode buckets | 4: 64/128/256/512 px (round up) | — | — | media-pipeline.md:89,375 |
| PAL/RHI | worker pool | **N = clamp(ProcessorCount−2, 2..6)**; owned threads | — | — | pal-rhi.md:57; hardened-v1-plan.md:46; threading-render-seam.md:77,678 |
| PAL/RHI | swapchain back buffers | **2** (FLIP_DISCARD); FrameLatencyWaitable | DXGI buffers special-cased in deferred-delete ring | resize CPU-waits only this swapchain's fences; 0-area → clamp 1×1, skip present | pal-rhi.md:226,384,432 |
| PAL/RHI | video surfaces (`VideoSurfaceRegistry`) | **exactly ONE live** (one HW overlay); priority theatre 20 > PiP 10 > sidebar 5 | atomic handoff (new before old hides) | surface lost → SetVisible(false), poster/art fallback | pal-rhi.md:500; media-pipeline.md:507 |
| PAL/RHI | occlusion test-present | `DXGI_PRESENT_TEST` throttled ~1 Hz | — | skip submit+present when occluded/minimized | pal-rhi.md:428 |
| Scene/Memory | `WorldTransform` copy budget | up-to-full-tree **NodeCount × 24 B/frame** during transform anim/scroll (~120 KB @ 5k nodes) | not damaged-only; idle = O(changed) | sub-ms memcpy; accepted | scene-memory.md:676; threading-render-seam.md:293; hardened-v1-plan.md:77 |
| Scene/Memory | `RealizationDirtySet` | O(1) per-layer precomputed sorted handle-set | per-frame; huge layers re-record above threshold | — | scene-memory.md:531 |
| Threading Seam | `SceneFrame` triple-buffer | **3 slots** | both-directions-volatile publish; quarantine reclaim | DropOldest coalesce (newest wins) | threading-render-seam.md:185,238; hardened-v1-plan.md:79 |
| Threading Seam | wakeup channel | `Channel<seq>` **cap 1, DropOldest** (signal only) | DropOldest | write-full benign | threading-render-seam.md:64,194,629 |
| Threading Seam | slot-reuse quarantine | `QUARANTINE = RenderInFlightDepth + 1`; 0 in single-thread step 1 | consume-gated: reclaim only when `_lastConsumedSeq > freedSeq` | early reclaim → `QuarantineLedger` deterministic throw; stall → no reclaim, no UAF | threading-render-seam.md:372; hardened-v1-plan.md:83 |
| Threading Seam | SnapshotColumns arena | pinned; grown on new high-water node count (×2, never shrunk) | old buffer deferred-freed behind consume index | — | threading-render-seam.md:315 |
| Threading Seam | split-resource deferred-delete ring | keyed on max in-flight submit fence AND `_lastConsumedSeq > freedAtSeq` | freed only after both gates clear | — | threading-render-seam.md:503; media-pipeline.md:282 |
| Text | glyph atlas pages | R8 grayscale + BGRA8 color; default **1024×1024** (1 MiB / 4 MiB); 1–3 pages typ.; grow by adding pages | frame-start LRU, live-pinned; `EvictThreshold ≈ 120 frames`; compact if frag >30% | page full + nothing evictable → add page; batch-time UV miss → reserved overflow region (never faults) | text.md:511,528; architecture-spec.md:404 |
| Text | glyph oversized valve | glyph larger than a page → bypass atlas | — | tessellated FillPathCmd via GetGlyphRunOutline | text.md:500,845 |
| Text | Yoga measurement cache | **8-entry ring** per node (`Ring8` `[InlineArray(8)]`) | LRU within ring | — | text.md:35,693; layout.md:35,166 |
| Text | L1 shaped-run + L2 wrap caches | bounded LRU on atlas frame clock | LRU; FontGeneration bump drops stale | — | text.md:616 |
| Text | subpixel positioning | `SubpxX` N=4 phases (≤4× copies); `SubpxY`=1 | quantize VF axes + aggressive LRU | — | text.md:256,491 |
| Text | COM callback GCHandle pool | pooled pinned `GCHandle` via `ObjectPool` **cap-32** | — | — | text.md:374,604 |
| Text | shaper buffer retry | arena-grow retry, **bounded 2 iterations** | — | repeated fail → split run | text.md:451,851 |
| Text | Unicode tables footprint | ~100 KB+ trimmed (CoreText milestone, deferred) | — | — | text.md:733; architecture-spec.md:673 |
| Theming | `PaletteCache` (uri→Palette) | **cap ~64 (~5 KB)**, LRU | LRU by LastUsedFrame | TargetGen-gated publish | theming.md:488 |
| Theming | `BrushCache` (DerivedKey→BrushHandle) | **cap ~256**, LRU | frame-start sweep; live-this-frame pinned; back-ref `MarkPaintDirty` on free | double-fault → render-local epoch degrades to "redraw not corrupt" | theming.md:497,512 |
| Theming | palette accumulator (per worker) | 5-ch × 4096-bucket histogram ≈ **160 KB**, worker-pooled | cleared per job | never frame-arena / cap-32 pool | theming.md:428; media-pipeline.md:344 |
| Theming | derived-brush binding rule | list rows bind **NO** derived brushes (T0 tokens only); only O(1) page surfaces call `UseDerivedBrush` | — | analyzer warns on `UseDerivedBrush` in `RenderItem` | theming.md:573; virtualization.md:354 |
| Virtualization | realize window | `[first,last)` + Overscan (~4 rows/side) + prefetch margin | recycle via FreeNode/CreateNode (no 2nd pool); per-range CTS cancels blown-past | fling → transform-only; boundary = bounded Gen0 delta | virtualization.md:151,169,290 |
| Virtualization | window buffer | `ArrayPool<Element>.Shared` (NOT cap-32 pool — overflows at 40+ rows) | rented + returned each realize | — | virtualization.md:189,521; reconciler-hooks.md:741 |
| Virtualization | `VirtualState` column | slab-backed; O(screens) rows not O(items) | survives frames; reset on host destroy | — | virtualization.md:246 |
| Virtualization | incremental load latency | min **+1 frame** page-arrival → pixels | `Cancel(RangeCts)` drops blown-past; bounded `Channel<PageFetch>` priority-drop | page fetch fail → skeleton stays, error surfaced | virtualization.md:486 |
| Layout | variable-height extent table (Fenwick/BIT) | slab-backed per virtual node; 100k items × 4B = **400 KB** | persists across frames; delta updates | within budget | layout.md:552 |
| Layout/Frame | render priority budget | `RenderPriorityPolicy` **16 ms**; overrun → next frame lower priority (no skip) | — | overrun yields to input pump | foundations.md:658; architecture-spec.md:846; reconciler-hooks.md:556 |
| Input/A11y | pointer flood coalescing | >1 kHz collapsed to latest move per PointerId/frame; wheel deltas accumulate | per-pointer last-move overwrite in place | 1 move + summed wheel per pointer/frame | input-a11y.md:95,567 |
| Input/A11y | hit-test route | `stackalloc NodeHandle[MaxDepth]`, typ. depth **≤64** | — | overflow → arena slice, never heap | input-a11y.md:173 |
| Input/A11y | `UiaClientsAreListening` gate | cached, refreshed on `WM_GETOBJECT` + low-freq timer | no AT → zero NodeProvider objects | — | input-a11y.md:480 |
| Reconciler | setState re-entrancy | `t_rerenderDepth` **cap 50** | — | bounds re-entrancy | reconciler-hooks.md:528 |
| Reconciler | DepKey overflow | inline ≤4 deps (`stackalloc`); >4 → `ArrayPool` (not cap-32 pool) | — | — | reconciler-hooks.md:264 |
| Hardened plan | working-set overhead | **+1–2 MB** (snapshot double-buffers, triple SceneFrame, ≥3 arenas, quarantine ring) | — | — | hardened-v1-plan.md:147 |
| Wavee app | `AudioBodyDiskCache` (encrypted CDN bodies, `%LOCALAPPDATA%\Wavee\Cache\audio`) | user default **4 GB**; max **512** fileId pairs; max-age **30 d** | LRU by `.map` last-access; whole-file eviction (.enc+.map); `Trim` on write + `MemoryGovernor` arena (priority 3) | miss → CDN re-fetch; key-check fail / torn chunk → `Invalidate` + re-fetch | playback-api-caching Phase 6 |
| Wavee app | `LicenseKeyDiskCache` (DPAPI obfuscated keys, `audiokeys.db`) | max **4096** rows; max-age **30 d** | LRU by `saved_at` on insert overflow | stale derive → `Invalidate` + one silent license refetch | playback-api-caching Phase 6 |

## 2. Budget gaps (own a native/GPU resource, no stated budget/eviction/failure)

These are the resources to put a number, an eviction policy, and a failure behavior on before implementation.

1. **PAL/RHI — PSO cache** (`GraphicsPipelineDesc` hash → `ID3D12PipelineState`): native set pre-warmed, Custom/effect blends warm lazily, but **no cap, eviction, or failure** if many distinct Custom blends accumulate. pal-rhi.md:281; gpu-renderer.md:362.
2. **PAL/RHI — shader-visible CBV/SRV/UAV descriptor heap**: single heap holds atlas/image/glyph SRVs; **no size, growth, or exhaustion behavior** stated (a real D3D12 cap). pal-rhi.md:276.
3. **PAL/RHI — RTV/DSV heaps**: back buffers + LayerPool RTs consume RTV slots; **no descriptor-count budget or overflow behavior**. pal-rhi.md:276.
4. **Text — `GlyphInstanceStore`** (double/triple-buffered glyph instance buffer): named but **no capacity, growth, or overflow budget** (unlike the renderer `UploadRing`). text.md:321,585,600.
5. **Media/Backdrop — `IEffectRunner` blur scratch RTs**: `BackdropRtCache` caps the *output*, but the **blur intermediate RTs have no pool/cap/OOM behavior**. backdrop-effects-animation.md:281.
6. **GPU Renderer — geometry/path realization slab**: LRU-by-slab-pressure named, but **slab cap / pressure threshold + behavior when one path exceeds the slab** unstated. gpu-renderer.md:415.
7. **Scene/Foundation — `StringTable` interning**: append-only `Dictionary<string,int>` + `string[]`, **no eviction, no cap** — unbounded growth if many transient keys are interned (per-track ItemKeys, dynamic a11y names). scene-memory.md:167; foundations.md §1.3.
8. **Reconciler — prev-frame `Element` retention**: per-host-node retained set for diffing; bounded in practice by the realized window but **not stated as a budget/eviction**. architecture-spec.md P6; dotnet10:85.
