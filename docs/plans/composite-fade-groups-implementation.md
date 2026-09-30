# Composite fade groups: analytic feathers + content-keyed group cache — design

Status: PROPOSED 2026-09-24 (Fable 5.1 design, read-only; awaiting owner go). Companion to
scroll-gpu-retained-tiles-implementation.md and docs/design/subsystems/gpu-renderer.md §13.1.

**Status 2026-09-24 — IMPLEMENTED (code), with the acrylic-hole fix; gates / pixels / bench numbers are recorded by the
landing report.** Owner decisions taken: (1) BOTH — the segment-extent fix and the budget defaults 5.0 × / 48 / 128 MiB
(to be confirmed against the measured `VisibleNeedBytes` of the artist bench; adjust only if the measurement contradicts
it); (2) `GpuKnockouts.GroupFades` is a probe-only identity control; (3) the app guide note (Wavee `docs/guide/scrolling.md`
rule 5, `.claude/skills/wavee/scrolling.md`) — no Wavee scroller combines `AutoEdgeFade` with a fill on the same node.
As-built deltas (gpu-renderer.md §13.1a/§13.1b/§13.1e/§13.1g is the as-built truth):

- **§1 test, own paint.** "every own segment has Commands == 0" is taken as *painted bounds empty*: a segment's
  command count includes scope ops (the viewport's own clip push), its `SliceOpBounds` union does not.
- **§1 feather slots.** An item carries ≤ 2 edge feathers: `Feather` (its own fade, else the outer distributed one) and
  `Feather2`; `CompositeItem.Inherited` counts the distributed ones and `CompositeFrame.ItemInherited` holds their layers
  (the headless model re-emits them as layers). Footprints include a blurred leaf's reach; a group enclosing an acrylic
  backdrop or a video hole keeps the fade a group (its backdrop reads what distribution would change).
- **§2 key.** The group's own alpha / feathers / clip / rounded clip are applied when its surface is DRAWN, so they are
  NOT in `GroupCacheKey` (a fade band ramping near a scroller's end keeps the cached surface); its σ and region size are.
  The footprint region is used when its AREA fits one tile (`GroupCacheKey.MaxFootprintPx`). A group whose enclosed
  items cross an ancestor viewport edge misses while crossing (their clips move relative to it).
- **§3 folded fades.** Not routed onto the content marker's layer: after §1 the only fades that spend the budget are
  fades over their OWN paint, and moving the fade onto the content marker would leave that paint unfaded. Instead
  **nothing is cut inside an inline group layer** (`SceneRecorder` `InlineLayerDepth`; content, bands, thumbs, effects
  all record inline, poses baked — they re-record when a pose moves): exact for every folded opacity / blur / fade group.
- **§3 segment extent.** A non-virtual scroll segment is cut on both axes AND its main-axis origin is floored to the grid
  at its content (like a static segment), so a sliver in the middle of a cell does not charge from the cell's top;
  only a virtual list's segments (`SliceRow.MainAxisGrows`) keep the cross-axis-only rule.
- **Acrylic (the related RCA).** Acrylic always cuts its own slice under a separate `AcrylicSliceCap`; one that still
  cannot be cut records no layer and keeps its FallbackColor plate; the plate fill drops only in an acrylic slice's own
  walk. Gates: `gate.slices.acrylic-never-folds`, the re-stated `gate.overlay.inwindow-acrylic-layer-emitted` /
  `static-chrome-acrylic` / `acrylic-plate-does-not-occlude-itself`, pixels `tile-acrylic-budget-identity`.
- **Group key clips.** An enclosed item's scissor enters the key cut to the surface region (an ancestor viewport's clip
  that the whole region lies inside no longer changes the key as the group moves under it).
- **Measured 2026-09-24 (Release, X1-85, `--scroll-bench artist-bench --vertical --dipPerSec 2000`, 1860×1230):**
  `visibleNeedMaxMiB=37.2` (> the old 32 MiB floor, < the new 48 MiB — decision 1 confirmed, defaults kept);
  GPU whole.mean 2.673 / p95 2.786 ms (plan baseline 4.04 / 5.55; the `--group-fades` control 2.891 / 3.240);
  offscreen 6.03 surfaces/frame (1.03 group at 1.85 Mpx; 4.99 acrylic-backdrop levels, 0 hits); featherItems 6.46;
  DegradedSlices 0, ExposedMissing 0. Arms: `--edge-fades-off` 1.477, `--ab-no-shelf-fades` 2.456,
  `--ab-no-magazine-fade` 1.894, `--ab-no-stickyclip` 1.913 (0.08 tiles rastered/turn vs 4.56).
  **Unresolved (outside this plan's scope): the magazine's `.StickyClip` is a PAINT channel (`EffectChannel.ClipTop` →
  `NodePaint.ClipRect`, `AppHost.ApplyEffectChannel`), so every page-scroll turn re-records the magazine column (≈ 4.2
  Content invalidations, 4.56 tiles rastered per turn), which re-renders its non-distributable fade group and misses the
  dock's backdrop cache every frame. Making ClipTop a composite parameter of the slice marker (like the item band's
  viewport-fixed clip) is the remaining step to the ≤ 1.8 ms target.** `--scroll-bench virtualization --dipPerSec 3000`:
  0.947 ms mean (0.91 before; p50 0.881).
- **Status 2026-09-24 (later) — the unresolved step LANDED, plus three follow-ons (gpu-renderer.md §13.1e is the as-built
  truth).** (1) `.StickyClip` is a composite-time clip on the slice marker (`CompositeSliceFlags.StickyClip`): a page
  scroll records 0 bytes / 0 tiles under the band and the magazine group + dock backdrop hit their caches. (2) The edge
  cue is the analytic feather (`ScrollEdgeCues.Fade` → `AutoEdgeFade`, 40-DIP band; the painted gradient and
  `TryResolveCueSurface` are deleted) and a slice root's own fade is cut as a `Layer` slice. (3) Scroll chrome is drawn
  over the feather (`Chrome` / `Thumb` slices outside it) — the visible thumb had made the virtual list's fade a
  per-frame group. (4) The feather is evaluated only outside its unit interior (`FeatherQuadSplit`), which removed the
  per-pixel feather cost over whole viewports. Measured (Release, X1-85, interleaved against the pre-change tree): see
  the landing report's tables — artist GPU mean ≈ 1.0–1.2 ms (was 2.7), virtualization ≈ the pre-change cost.

## 0. Mechanism, confirmed in code

1. An AutoEdgeFade lives on the scroll VIEWPORT node. SceneRecorder.TryResolveEdgeFade
   (src/FluentGpu.Engine/Render/SceneRecorder.cs:3632) synthesizes the spec from the viewport's
   ScrollState (band = AutoEdgeFadeBand, default 40 DIP, Reconciler.cs:4160), so isEdgeFade is true
   for the viewport node and the layer cut at SceneRecorder.cs:1574-1631 makes the viewport an
   Effect slice with CompositeSliceFlags.Layer (an EdgeFadeLayerCmd). The viewport's own walk then
   cuts its content as a Scroll slice (TryCutTranslation, :1270) — a CompositeSlice marker inside
   the effect slice's arena.
2. A layer slice with a child marker is always a GROUP. SliceRecorder.PlaceChild
   (src/FluentGpu.Engine/Render/SliceRecorder.cs:1097-1110): leaf = _scanMarkCount[child] == 0; if
   (layer.HasLayer && !leaf) → PlanKind.GroupOpen. BuildComposite (:1234) emits CompositeKind.Group;
   D3D12Device.PrepareGroup (src/FluentGpu.Windows/D3D12/D3D12Device.Composite.cs:510-533) leases a
   scratch, CLEARs it, DrawRanges the enclosed items, every turn, no key, no FindRetained. Only
   PrepareLeafBlur (:389) and PrepareBackdrop (:538) are content-keyed.
3. The group's region is the ancestors' composite clip, not the group's footprint. ItemRegion(it,
   halo) = it.Clip ∩ window; a GroupOpen carries g.Clip = clip (the page viewport ∩ the magazine's
   StickyClip half-plane — NodePaint.StickyClipSpan = 1e8). So every shelf group, visible or not,
   leases a viewport-sized scratch and clears it. (Predicts ≈ 10 groups × ~2 Mpx > the measured
   9.6 Mpx; print the per-item OffscreenPx split in the first diagnosis run — gate 0.)
4. featherItems=0 is a counter gap, not "no feather": _frameFeatherItems++ only in the Tiles/Region
   leaf path (D3D12Device.Composite.cs:696); DrawSurfaceItem → ItemParams applies the feather to a
   group surface without counting it. So fades ARE drawn analytically today — once per group
   surface, after re-rendering the group.
5. Nesting: page-fade group ⊃ magazine-fade group ⊃ 8 shelf-fade groups. PrepareRange recurses
   (:291-297), so a page-scroll tick re-renders all 10 offscreen surfaces even though shelf content
   is byte-identical and its tiles are resident — the 2.99 ms of Offscreen (artist-bench: GPU
   4.04 ms mean, p95 5.55; ~11 offscreen surfaces/frame, 9.6 Mpx).

## 1. Fade → analytic composite-time feather (primary fix)

### Exact condition

For premultiplied source-over, feather(A over B) == feather(A) over feather(B) at a pixel iff
f·αA·αB·(1−f) == 0 — wherever two enclosed items are BOTH non-transparent the feather must be
exactly 0 or 1. (Group path: A f + B f(1−αA) + D(1 − fαA − fαB + fαAαB); distributed:
A f + B f(1−αA f) + D(1−αA f)(1−αB f); B and D terms differ by f αA (1−f)·(…).) Same for
GroupAlpha: distributable only when alpha ≈ 1 (or nothing overlaps). With two nested distributable
fades the item's coverage is the PRODUCT f_page·f_shelf (exact); merging into one EdgeFeather is
NOT exact (curve(min(a,b)) ≠ curve(a)·curve(b) in corners), so an item needs a second feather slot.

Distributable(G), per tick, for a LayerKind.EdgeFade layer with BlurSigma == 0 and
GroupAlpha ≥ 0.999:

- every own segment of G's slice has Commands == 0 (a Fill/border/edge-cue on the ScrollEl makes it
  a group; the thumb is a separate slice/child item);
- P = union of the enabled band strips (superset of 0<f<1); placed footprints (Offset(c.Bounds,
  cdx, cdy) ∩ clip, computed in PlaceChild, and parent segment bounds) of enclosed items with
  alpha > 0.001 are pairwise disjoint inside P;
- enclosed items are Tiles/Region leaves or distributable groups (≤ 2 feathers per item; a third ⇒
  group);
- no Backdrop/EraseVideoHole inside G (acrylic inside a fade stays a group).

If it fails on a tick, that tick emits GroupOpen as today (and hits the §2 cache). Routes differ
only by the group scratch's 8-bit quantization (≤ 1/255) — flips are invisible and gated.

### Nested scrollers (shelf inside page)

The shelf viewport stays an Effect slice (empty arena, one marker); its EdgeFadeLayerCmd.DeviceRect
is recorded in the MAGAZINE's pose-free space. PlaceChild already offsets the marker layer by the
containing slot's accumulated delta (pdx = accDx, the page scroll) while the content's own delta
(odx, the shelf scroll) goes only into cdx → the item transform. So the distributed feather rect
follows the page and tiles follow page+shelf — no new pose plumbing. A page scroll changes no
ChromeSig (SliceRecorder.ChromeSig :688) → composite-only turn; a shelf scroll near its ends
re-records the viewport slice (bands ramp over 24 DIP) as today. Precedent: the item band already
composites a Scroll slice as a leaf Tiles item with a feather (SceneRecorder.cs:2550-2559,
ParamsUp|Layer); BuildComposite:1363 resolves a leaf layer into CompositeItem.Feather.

### Sketch — SliceRecorder.PlaceChild/PlaceSlot

```csharp
private bool Distributable(int child, in Plan layer, float scale)
{
    ref Rec c = ref _recs[child];
    if ((LayerKind)layer.Layer.Kind != LayerKind.EdgeFade || layer.Layer.BlurSigma > 0f || layer.Layer.GroupAlpha < 0.999f) return false;
    for (int k = 0; k <= _scanMarkCount[child]; k++) if (_scanSegs[child][k].Commands != 0) return false;
    Span<RectF> bands = stackalloc RectF[4]; int nb = BandStrips(in layer.Layer, bands);
    int n = 0;
    for (int s = c.FirstChild; s >= 0; s = _recs[s].NextSibling) n = CollectFootprints(s, _fp, n, out bool ok); // ok=false ⇒ backdrop/video/3rd feather
    for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) { RectF o = _fp[i].Intersect(_fp[j]); if (o.IsEmpty) continue;
        for (int b = 0; b < nb; b++) if (!o.Intersect(bands[b]).IsEmpty) return false; }
    return true;
}
// PlaceChild:
if (layer.HasLayer && !leaf && !Distributable(child, in layer, scale)) { /* GroupOpen as today */ }
else PlaceSlot(child, scene, cdx, cdy, accDx, accDy, clip, round, roundR, layer, ref repaint);   // leafLayer INHERITS
// PlaceSlot: a segment/child item takes the inherited feather as Feather (or Feather2 if it already has one):
if (leafLayer.HasLayer) { if (!e.HasLayer) { e.HasLayer = true; e.Layer = leafLayer.Layer; } else e.Layer2 = leafLayer.Layer; }
```

CompositeItem gains `EdgeFeather Feather2 = default`; BuildComposite resolves it via LayerParams;
CompositeHash mixes it.

### Backend

SliceCompositor packs Feather2 into K[40..55] (root constants 40 → 56 DWORDs; +1 SRV table =
57 ≤ 64). composite.hlsl Coverage: `if (K[13].y > 0.0) a *= edgeFeather(p, K[10], K[11], K[12],
K[13]);` (intensity 0 ⇒ 1). EdgeFeatherMask stays the single source; TileRasterizer inline-fade
path unchanged. ItemParams sets both; count _frameFeatherItems for every feathered item (leaf AND
surface); add LastGroupSurfaces.

## 2. Content-keyed group cache (for what must remain a group)

Opacity groups (alpha < 1), blur groups, fades that fail the test (fill on the viewport,
overlapping children in the band, inner acrylic), Blur-mode fades.

Key (engine-side so headless computes the same — Render/Tiles/GroupCacheKey.cs, zero-alloc,
FNV/Mix like LeafBlurKey): group alpha/σ/feather(s) with rects RELATIVE to the region origin;
region size; per enclosed item in order: kind, sliceId, (Transform − regionOrigin), alpha, σ,
clip/roundClip/feather rects − origin, BlendCopy; Tiles/Region: every placed tile (Tx,Ty,surface,
serial); nested group: its own key (prepared first — store _itemKey[i]); a Direct or missing item ⇒
not cacheable (BackdropKey's rule).

Page scroll must HIT: today (a) OnSliceGrid floors a Group's region on the WINDOW grid (group
Transform identity) so the origin steps 64 px while content moves 1 px; (b) region = clip ∩ window
changes as the group crosses the window edge. Fix: Group item Transform = Translation(ox +
round(accDx·scale), …) of its effect slice (snap in the group's own space), and region = the
group's placed FOOTPRINT (slice Bounds ∪ children footprints, posed, + blur halo), capped at
TileGrid.W×H; larger groups keep today's clipped region; ItemScissor still clips to it.Clip. A page
scroll then moves origin and every enclosed transform by the same integer delta → identical key →
FindRetained hit; re-render only on own content change (tile re-raster bumps Serial; shelf scroll
changes relative transforms). The page-fade group itself can never hit — §1 removes it.

```csharp
// D3D12Device.Composite.cs — PrepareGroup
PixelRect region = GroupRegion(in frame, i, halo);
ulong key = GroupCacheKey.Compute(in frame, i, region, _serials, _itemKey, out bool cacheable);
if (cacheable && (hit = _surfaces.FindRetained(key, fence, out down)) >= 0) { _frameGroupCacheHits++; _itemSurface[i] = hit; _itemKey[i] = key; return; }
// … render as today …
if (cacheable) _surfaces.Retain(result, key, down);  _itemKey[i] = key;
```

Invalidation implicit (key mismatch) + existing RetainTurns = 30 idle return. Memory: add
SurfacePool.RetainedBytes, TileBudget.RetainedShare (default 0.25 of the tile budget, live-tunable)
enforced in Retain (evict LRU retained first), TileCensus.RetainedBytes + GroupCacheHits;
memory-ceiling gate: ResidentBytes + RetainedBytes ≤ Budget·(1+share). Headless:
CompositeRecordKind.PrepareGroup{Key, Hit}.

## 3. StickyClip / magazine-fade removal, squash rule, budget

Why fewer fades gave MORE surfaces (23): SliceTable.SurfaceExtent
(src/FluentGpu.Engine/Render/Tiles/SliceTable.cs:350-369) cuts a SCROLL segment's tile only on the
cross axis — a vertical scroll segment charges a full 512-px cell per column (1860 wide ⇒ 1024 +
Dim(836) ≈ 3.6 MiB per row); EFFECT segments are cut on both axes. With the magazine fade, the
popular band and 8 shelf headings are 9 small REGION segments of the magazine effect slice
(≈0.3 MiB each). Without it they land in the PAGE SCROLL slice as 9 sliver segments charging whole
cells (≈9×3.6 MiB) → 32 MiB saturates → Resolve degrades slices → degraded segments raster
transient chunks every frame (6 for a viewport-covering segment; PrepareDirect) AND BackdropKey
returns cacheable=false when any item beneath is Direct (:606) so the dock's dual-Kawase chain (5
levels) re-runs every frame: 10 groups + 6 chunks + 5 levels ≈ 23. Removing StickyClip removes the
magazine's ClipRect (AppHost.Scroll.cs:512; MixPaintReveal keys spans on it) changing segment
extents and shelf clips — exact tile delta to be named by gate 0. Base case already 31.6/32 MiB
with 2 degraded slices.

Squash rule: EffectSliceCap = 16 does not fire on the bench (14 candidates) but when it does, a
folded edge fade records an inline PushLayer (SceneRecorder.cs:1797) in the containing arena while
its scroll content is still cut as a slice (the content cut never consults the effect budget), the
tile replay skips the marker, the inline layer wraps nothing → content composites UNFEATHERED.
Wavee's real artist page will cross 16. Fix: a distributable fade needs no effect-slice slot (count
only non-distributable layer slices; decided at record time by the static half of the test: empty
own paint, alpha 1, fade mode); folded fades route onto the content marker's layer, never an inline
PushLayer around a marker; gate it. (Related, found separately 2026-09-24: an ACRYLIC plate folded
the same way becomes a transparent hole — see the acrylic RCA; acrylic must always cut its own
slice.)

Segment fragmentation fix (required): cut scroll-segment surfaces on the main axis at the segment's
painted bounds too (growth is already ValidRectChanged/SliceGeometry, Request:319-331); keep
cross-axis-only only for the coverage-carrying segment of a virtual list (ScrollVp ≥ 0 &&
sc.ItemCount > 0).

Budget, measure-driven: at 1860×1230 the derivation gives 30.5 MiB → the 32 MiB floor; this page's
visible need ≈ 45 MiB (page tiles ≈14, 8 shelves × 3 cols × ~0.9 ≈22, effect regions ≈9). Add
TileCensus.VisibleNeedBytes (Σ SurfW×SurfH×4 over order-0 tiles, resident or not), then defaults
WindowMultiplier 5.0 / Floor 48 MiB / Ceiling 128 MiB, gate VisibleNeedBytes ≤ BudgetBytes on the
bench.

## 4. Code changes, gates, bench

- Seams/Rhi/Composite.cs — CompositeItem.Feather2; doc Group.Transform = the slice's posed offset
  (pal-rhi.md §2.3 + gpu-renderer.md §13.1e; check-canon).
- Render/SliceRecorder.cs — Distributable, BandStrips, CollectFootprints, Plan.Layer2, inherited
  leafLayer through PlaceSlot/PlaceChild, Group item transform, footprint region hint, CompositeHash
  mixes Feather2 + the route decision.
- Render/SceneRecorder.cs — CompositeSliceFlags.DistributeFade = 16 (doesn't spend the effect
  budget); folded fade → marker layer.
- Render/Tiles/GroupCacheKey.cs (new), TileBudget.RetainedShare,
  TileCensus.{VisibleNeedBytes,RetainedBytes,GroupCacheHits}, SliceTable.SurfaceExtent main-axis
  cut.
- Headless/Rhi/HeadlessGpuDevice.Composite.cs — PrepareGroup{Key,Hit}; model Feather2 as a second
  EmitLayer.
- FluentGpu.Windows/D3D12/D3D12Device.Composite.cs — PrepareGroup keyed/retained, GroupRegion,
  feather counting; SurfacePool.cs — RetainedBytes + share cap; SliceCompositor.cs + composite.hlsl
  — Feather2 (56 root constants).
- Seams/Rhi/GpuFrameTelemetry.cs — GpuKnockouts.GroupFades = 16 (forces the group route; identity
  control like ForceFullDirect) + GroupSurfaces/GroupCacheHits counters; ScrollPerfProbes.cs prints
  them and --group-fades.
- FluentGpu.WindowsApp/Probes/RepaintIdentityProbe.cs — fade-distribute-identity,
  group-cache-identity.

Gates (failing first) — SliceSuite fixture: page ScrollEl{AutoEdgeFade} ⊃ 3 horizontal
ScrollEl{AutoEdgeFade} shelves, a 4th shelf with Fill (stays a group), one OpacityGroup over two
overlapping children:

- gate 0 (diagnosis, bench): per-arm TileCensus + SliceRecordStats + per-item OffscreenPx before any
  change.
- gate.slices.fade-leaf; gate.slices.fade-follows-page; gate.slices.group-not-rerendered (op-log Hit
  on page scroll; miss after shelf scroll / hover); gate.slices.fold-keeps-fade (20 fades → folded
  ones still feather); gate.tiles.memory-ceiling with retained bytes; gate.tiles.segment-extent (a
  40-px scroll segment charges ≤ Dim(40) rows); alloc-zero gates unchanged.
- Pixels (--repaint-identity): tile-static/scroll-identity 0 px at 1.0–2.0; fade-distribute-identity
  (distributed vs --group-fades) ≤ 1/255; group-cache-identity (miss vs hit) 0 px;
  tile-feather-identity extended to the product ≤ 1/255.

Bench (Release, X1-85): `dotnet run --project src/FluentGpu.WindowsApp -c Release -- --scroll-bench
artist-bench --vertical --dipPerSec 2000` (+ --group-fades control, --edge-fades-off,
--ab-no-shelf-fades, --ab-no-magazine-fade, --ab-no-stickyclip); `--repaint-identity`. Targets: GPU
mean ≤ 1.8 ms (p95 ≤ 2.6) from 4.04/5.55; offscreen surfaces/frame ≤ 1.0, Mpx ≤ 0.5; featherItems ≥
5; DegradedSlices = 0, ExposedMissing = 0, ResidentBytes ≤ Budget; GroupCacheHits ≥ 0.95 of group
preps on the --group-fades control. Wavee artist page: render-thread costMs max < 4 ms (from
11–15.5), verdict not Late.

## 5. Risks and owner decisions

- Root signature 57 DWORDs (≤ 64), shared by all composite PSOs; verify on Tier-0 render-pass
  devices.
- Per-tick route flips (distributed ↔ group) ≤ 1/255, invisible; each flip re-signs CompositeHash (a
  submit, not a re-raster).
- Footprint-region groups may render pixels outside the window for a partially visible group
  (≤ 2 MiB cap) — accepted for key stability.
- Decision 1: raise budget defaults (5.0× / 48 / 128 MiB) after VisibleNeedBytes is measured — or
  keep 32 MiB with only the segment-extent fix. Recommend both.
- Decision 2: GpuKnockouts.GroupFades is a probe-only measurement/identity control (like
  ForceFullDirect), not a feature switch.
- Decision 3: a Fill on an AutoEdgeFade ScrollEl (Wavee has some) should be authored on the PARENT
  so the shelf distributes — an app-side guide note, not engine magic.
