# Adreno X1-85 DEVICE_HUNG — fix design (tier-gated, weak/UMA GPUs only)

## Root cause (research + Fable 5 code investigation, captured logs)

Qualcomm Adreno X1-85 (UMA, tile-based, UBWC compression, 128MB dedicated LOCAL segment) repeatedly
`DXGI_ERROR_DEVICE_HUNG` (0x887A0006). A vendor-acknowledged UMD defect in the UBWC/layout-transition path,
triggered by the engine's per-frame texture-upload-then-sample cadence, and — decisively — amplified by the GPU
sitting **permanently 1.5–2× over its 128MB budget** (captured `liveBytes` 192–254MB), forcing constant driver
paging that piles resource-state transitions onto the fragile UMD until it wedges (2s TDR).

Two failure modes, both in the logs:
- **Load spike:** Home-feed load streams uploads at the full at-rest rate (throttle only engages on scroll/nav) →
  initial-load hangs (rec3–5, first ~90s).
- **Sustained over-budget:** total live GPU memory always exceeds 128MB → paging thrash → deep-session hangs
  (rec6 @ frame 9491 +5.5min, rec7 @ 18376 +9.4min).

Budget math @ 1770×1140×1.65 (2921×1881px = 21.97MB/full-canvas surface): swapchain×3 = **66MB** + image cache
**64MB** = 130MB before any OpacityLayer RT (22MB each, MaxPool=32). Recovery is robust (7 hangs, 49min uptime, no
permanent freeze) but each hang = ~2s freeze.

## This tranche (all `GpuProfile.IsWeak`-gated — zero effect on discrete GPUs)

### M0 — tier-aware upload throttle, active during passive load (engine)
- `DecodeScheduler.cs:202-203` cap selection: on `IsWeak`, force cap=1 apply / 512KiB, AND close the size-uncapped
  head exemption (`:222`) so a ~1MiB cover can't land uncapped on Weak.
- `AppHost.cs:3369`: `_images.ScrollThrottled = scrollActive || _navThrottleFrames>0 || (GpuProfile.IsWeak && _device.HasPendingUploads)`.
  (`HasPendingUploads` already exists, used at AppHost.cs:1993.)

### M5 — VRAM-budget-aware eviction + tier-scaled budgets (engine)
- `FluentApp.cs:726` ImageCacheBudgetBytes → **24MB** on Weak (from 64MB). Shrinks steady state AND the
  post-recovery re-realize burst.
- `ImageCache.cs:174` DerivedSoftBudgetBytes → **8MB** on Weak (from 16).
- `OpacityLayerCompositor.cs:46` MaxPool → **8** on Weak (from 32); TrimIdleFrames → ~120 (from 600) so the 22MB
  canvas RTs retire fast.
- Dynamic feedback: `D3D12Device.PublishVideoMemorySnapshot` (:3998) sample every ~10 presents on Weak; expose a
  seam accessor `IGpuDevice.TryGetVramUsage(out long usedBytes, out long budgetBytes)` (default false; D3D12 fills
  from the already-queried LOCAL segment). Host (AppHost) calls a new `ImageCache.EvictToVramPressure(budget, used)`
  that evicts unpinned LRU to `used < budget*0.85`, hysteresis re-arm at 0.90 (no thrash). Target: tracked live < ~110MB.

### M3 — quiesce the loading loop on Weak (engine + app)
- `MotionRecipes.cs:151-156` SkeletonPulse: static/very-slow on Weak.
- `C:\wavee\WaveeMusic\src\apps\Wavee\Design\Surfaces.cs:381,412` CoverShimmer Breathe → Flat on Weak.
- Lets the frame loop idle between decodes so uploads coalesce instead of one-per-frame at max cadence.

## M1 — LANDED, in two stages
- **Stage 1 (shipped): private CPU-visible textures.** `ImageTextureStore` on UMA writes decoded pixels straight into a
  `CUSTOM`(L0/WRITE_BACK) texture with `WriteToSubresource` — no staging buffer, no `CopyTextureRegion`, no
  `COPY_DEST→PSR` barrier. Small images were kept OUT of the shared atlas so every write targeted a brand-new or
  fence-reacquired PRIVATE resource.
- **Stage 2: CPU-written atlas PAGES for ≤128px thumbnails** (`media-pipeline.md` §4.1a — the canon owner). Stage 1's
  cost was one 64 KiB-granular committed resource per thumbnail (a 64² thumb = 16 KiB of pixels in a 64 KiB commit,
  ~4× waste plus ~1000 resident driver resources on a full Home lap). Pages are `ROW_MAJOR` on the same `CUSTOM` heap,
  created in `COMMON`, **persistently mapped** and written with a plain row-by-row memcpy — so `WriteToSubresource`
  is gone from this path entirely (which also retires the LiteRT-suggested `WriteToSubresource` risk for thumbnails),
  and `ROW_MAJOR` is what makes a per-cell write byte-disjoint from the cells the GPU is sampling. Four invariants
  (no barrier / write-after-fence per cell / texel-disjoint == byte-disjoint / published cells immutable) are stated in
  §4.1a and gated headlessly as `gate.imgatlas.*`. `ROW_MAJOR` CPU-writable `TEXTURE2D` is driver-optional, so the
  first page create is a **capability probe**: on failure the session falls back to Stage 1's private textures.
  **Still needs the device run** — no `[d3d12.stall]` / `device-lost recorded=`, and `[d3d-mem]` showing
  `Image.AtlasPage.Uma=…MiB×N` with N in single digits instead of `Image.Texture.Uma×~1000`.

## Deferred (need consent / device validation)
- **UMA-only depth-2 swapchain** — reclaims 22MB (66→44MB), the single biggest memory lever, would nearly guarantee
  under-budget. Reverses the user's "keep 3 everywhere" for UMA only; **flag for the user, do not do unilaterally.**

## Verify
Both configs clean, VerticalSlice ALL CHECKS PASSED, Windows.Tests, canon exit 0. On the Adreno: browse Home ≥10min —
success = no `[d3d12.stall]`, no `device-lost recorded=`, and `PublishVideoMemory CurrentUsage < Budget` throughout.
