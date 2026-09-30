# Evidence diagnostics — proving every open visual/scroll issue from recorded evidence

Status: DESIGN (read-only pass, 2026-09-24 evening). Nothing here is built. The builder takes it after its current batch
(§D order). Owner rules honoured throughout: always-on, no env vars, no source-text tests, zero-alloc render thread (fixed
rings, measured), no band-aids (this adds detectors and exports, never behaviour), no A/B asked of the owner, the app is
driven by us.

Reading order for the implementer: `CLAUDE.md` (both repos), `.claude/skills/fluentgpu-scroll/*`, `gpu-renderer.md` §13.1,
`scroll-rework-handoff-2026-09-24.md`, then this file.

---

## 0. The stance

Every issue below has a code-reading hypothesis. None of them gets fixed on the strength of the reading. Each is decided
by (1) a recorded artefact captured in the **real Wavee** at the offending offset/pixel/turn and (2) where the class is
headlessly reproducible, a failing-first VerticalSlice gate. The instrumentation that produces the artefacts stays in the
engine permanently as invariants (stale tiles = 0, exposed missing = 0, …), so the class cannot silently return.

What "evidence" means per artefact:

| Artefact | Answers |
|---|---|
| **Raster ledger** (per tile: what bytes it holds, when, why, at what alpha) + the live **stale-tile invariant** | "Are the pixels on screen the pixels the current stream describes?" — decides #1 and any retained-content bug |
| **Composite item record** (per item per frame: kind, feathers, alpha, clip, sticky, group hit) | "Which item painted where, with which fade/clip?" — decides #2, #3a, #3b, #5 |
| **Pixel query** (`what composited at (x,y)`) | Ties a screenshot pixel to items → tiles → raster frames → node keys, with the exact feather value from the C# feather port |
| **Walk ledger** (per re-recorded slice: why it re-recorded, bytes) + **turn cost row** | "What did this render turn actually do?" — decides #6, attributes spikes |
| **Scroll probe CSV** (exists; +1 row kind) | Motion/latency/pacing — #6 "blocked/delayed", #7 spring-back |
| **Evidence bundle** = screenshot + all of the above + census, keyed by the SAME frame sequence | Time-alignment, so a picker answer and a screenshot describe one present |

---

## 1. Issues, hypotheses, what decides them, what exists today

Code anchors are as of the uncommitted trees on 2026-09-24. Line numbers drift; symbol names don't.

### #1 Artist compact band's tab row — invisible when pinned, full alpha over the hero at rest

**Where.** `waveemusic/src/apps/Wavee/Entities/Artist.Page.cs:598-650` `BandBar` → a `BoxEl` with `.Reveal(start, 44, dy)`
(:648) whose pivot lane is `Detail.Pivot` → `Detail.UI.Hero.cs:752-763` — a nested **horizontal `ScrollEl`**
(`AutoEdgeFade = true, EdgeCues = None`). `Reveal` writes `EffectChannel.Opacity` → `NodePaint.Opacity` on the band
(`fluent-gpu/src/FluentGpu.Engine/Hosting/AppHost.Scroll.cs:516-519`).

**Mechanism as read (refined hypothesis).** The band is NOT an opacity group (`p.OpacityGroup` is only set from
`b.OpacityGroup`, `Reconciler.cs:4701`), so its opacity is multiplied INTO every primitive's colour at record time:
`SceneRecorder.cs:1513 opacity = parentOpacity * ResolveOpacity(...)`. The tab `ScrollEl` content is cut as its own
`SliceKind.Scroll` slice (`SceneRecorder.cs:1300-1397`, walked with `parentOpacity` at :1380). When the band's opacity
changes, the child slice's `spanInputSig` changes (it includes `opacity`, :1846), so the slice is **walked, not kept**
(:1859-1870) — the bytes are re-recorded with the new alpha. But the **damage decision** (:1934 ff.) damages only on
placement / own-box change or own paint dirt; the tab nodes are clean and did not move → **no `SliceDamageRect` is added,
the tiles stay valid with the OLD alpha baked in.** `SliceTable` has no notion of "the bytes changed": `MarkRastered`
(`SliceTable.cs:499`) bumps `ContentEpoch`, `SegmentIdentity` (`SliceRecorder.cs:2149`) hashes node/role/markers/base —
never content. The `_scanHash[s]` (`SliceRecorder.cs:1019`, whole-arena content hash) exists but only feeds
`CompositeHash` (skip-submit), never invalidation.

So the ORIGINAL hypothesis ("kept whole with stale bytes") is refuted by :1859 (the slice IS re-walked); the refined one
("re-walked, bytes changed, no damage, tiles stale") stands. Both symptoms fit: tiles rastered while pinned (alpha 1)
survive the scroll back to rest (band at alpha 0 → tabs at full alpha over the hero); tiles rastered at rest (alpha 0)
survive to the pin (invisible).

**Decides it.** Raster ledger + stale invariant (§A.1). At rest and when pinned, `TileCensus.StaleTiles ≥ 1` on the tab
slice, the ledger showing `RasterHash ≠ WantHash` with `RasterFrame` = a frame from the other state, and the pixel query at
a tab glyph naming that tile → **confirmed**. `StaleTiles = 0` in both states and the query naming a different item →
**refuted**, look at whatever item the query names.

**Headless gate (failing-first).** `gate.slices.inherited-opacity-rerasters`: a parent `BoxEl` with `Opacity` bound to a
signal over a child `ScrollEl`; frame at 1.0, frame at 0.3 → the child slice's visible tiles must be on the raster list
(or its bytes unchanged). Expected to FAIL today. It becomes the regression pin for the eventual fix (likely at
`SceneRecorder.cs:1908-1916` — a `SpanMissExactKey` walk with `recordDirtyBits == 0` must damage the re-recorded span's
bounds — but the fix is NOT part of this plan).

**Exists today.** `TileCensus` (all reasons, but no content check), `SliceRecordStats.Walked/Kept`, `spans` miss counters.
**Missing:** any content-vs-tile comparison, any per-tile history, node names.

### #2 Stray 20×2 DIP accent bar at ≈7 % alpha under the band

**Hypothesis.** The magazine's 24-DIP top feather (`Artist.Page.cs:527`, `Detail.BandLayout.ClipFadeBand = 24`,
`Detail.cs:188`) at 4–6 DIP.

**Alternative from code.** The pivot's active-tab underline is exactly `UnderlineHeight = 2` DIP (`Detail.cs:921`) filled
with the accent, i.e. a 2-DIP-high accent bar of ~label width. If #1 holds, a tab tile rastered mid-ramp at band alpha
≈ 0.07 leaves exactly that bar at 7 %. The 20 DIP width ≈ a short label ("Albums") minus padding.

**Decides it.** Pixel query at the bar: it returns the ordered items covering the pixel with each item's feather value
(`EdgeFeatherMask.Evaluate`, the line-for-line port of the shader) and, for a `Tiles` item, the tile's raster frame and the
alpha baked at raster (ledger). Feather value 0.05–0.10 on the magazine item at that y → hypothesis confirmed. A tab-slice
tile with `RasterAlphaQ8 ≈ 18/255` and stale=1 → it is #1, hypothesis refuted.

### #3a Pinned facet header ("Albums") gets its top 24 DIP feathered

**Hypothesis.** It pins inside the magazine's EdgeFade group.

**Code.** The facet header is `Sticky(pinTop)` (`Artist.Discography.cs:740`, `pinTop = BandLayout.Height = 56`), a child
of the magazine (`Artist.Page.cs:524-533`) whose `EdgeFade(Top, 24)` is live while `compact`. A distributed fade rides
every item below it (`CompositeItem.Inherited`, `Feather`), feather rect = the fade's DeviceRect at its posed offset.
Pinned at y = 56, the header's top 24 DIP lie inside the feather band [56, 80).

**Decides it.** Composite item record while pinned: the header's item has `Inherited ≥ 1`, `Feather.Rect.Y = 56·s`,
`Feather.BandT = 24·s`, `StickyEngaged = 1`; the pixel query at (header x, 60 DIP) returns feather ≈ 0.17 →
**confirmed**. No inherited feather on the header item → refuted (then the strip is a group route:
`Distributable` failed → look at `GroupCount`/group key rows).

### #3b During the 44-DIP reveal, content is hard-cut at 56 with no feather

**Hypothesis.** The EdgeFade is gated on `_compact` (the sentinel's `Sticky(56, engaged:)`, `Artist.Page.cs:509-510`), not
on the clip's engaged edge.

**Geometry caveat.** The sentinel (0-height, between hero and magazine) and the magazine's `StickyClip(56)` share the same
`NodeY`, and both engage on `offset + 56 − NodeY > 0` (`ScrollEffectEval.cs:36-41, 106-110`), so they flip on the SAME
offset; during the ramp (`offset ∈ [cd−44, cd]`, `cd = heroH − 56`) the magazine clip is NOT engaged. What IS cut at 56
then is either the hero's own `Collapse(Leading)` presented-edge cut (its children are guillotined at the presented
bottom, no feather by design) or the wash's `StickyClip(ClipInset)` (`Artist.Page.cs:540-541`). The hypothesis is likely
refuted; evidence names the real item.

**Decides it.** Bundles at every 4-DIP step of the ramp (12 steps): in each item record, the item(s) with
`Clip.Top == 56·s` and no feather; their node keys say hero / wash / magazine. Plus `StickyEngaged` per item. If the
magazine item shows `Clip.Top = 56·s` with `Feather = none` during the ramp → hypothesis confirmed. If it is the hero's
`Collapse` cut → refuted; a design question (should Collapse feather?) goes to the owner with the frames.

### #4 Lyrics lines blanking (blur-source hypothesis disproven headlessly)

**Code.** Every line row wraps its text in a `dofContent` `BoxEl { Blur = σ }` (`Lyrics.UI.cs:1911-1917`) and the DoF ramp
writes `NodePaint.BlurSigma` per line per frame (`:1060-1101`); the list is `MeasureAll` up to 400 lines,
`AutoEdgeFade = true` (`:884-896`). A per-line self-blur is a foldable effect: past `EffectSliceCap = 16`
(`SliceRecorder.cs:76`) the rest record INLINE (`Folded`) inside the list's scroll-slice tiles, and an inline blur layer
leases a scratch from `SurfacePool` (`ScratchCap = 128`, `SurfacePool.cs:36`; `Lease` returns −1 at :228/:268). What the
tile rasterizer draws when a lease is refused is D3D12-only behaviour the headless model does not have — the disproven
gate (`blur-rows-follow-scroll`, 244 blur items) ran in the model. `RasterDone` faithfulness (`D3D12Device.Composite.cs:
269-271`) counts dropped instances and in-flight images, NOT refused scratches, so such a tile would be marked valid.

**Candidate causes** the evidence must separate: (a) refused inline scratch → text not drawn, tile marked faithful;
(b) `Degraded` slice / `ExposedMissing` (census would show, but nobody looked at the census at the blank instant);
(c) stale tile after a σ/opacity change without damage (same class as #1 — the row's own node is dirty though, so less
likely); (d) the AutoEdgeFade distribution failing (overlapping blur footprints in the band strips) → group route each turn
(costly, not blank).

**Decides it.** The bundle captured at a blank instant (the harness follows playback and captures at every hand-off, and
the `[tiles.stale]` / new `[d3d12.scratch]` log edge triggers one automatically): item record for the lyrics list slice
(Folded count, `GroupCount`, `Inherited`), the D3D12 counters `InlineScratchRefused` (new) and `LastInlineGroups`, the
ledger for the tile under the blank line (`RasterFrame`, `Faithful` bits incl. the new `ScratchRefused` bit), the pixel
query. (a) → `ScratchRefused ≥ 1` on that tile's raster and the query showing the line's tile with no text coverage; then
`RasterDone` must also treat a refused lease as unfaithful (a fix, separate). (c) → stale=1. (b) → census.

**Exists today.** `Folded`, `LastInlineGroups`, `LastOffscreenSplit`, `ExposedMissing`. **Missing:** a refused-lease
counter, per-tile faithfulness detail, and any capture aligned with the blank frame.

### #5 Painted scroll edge cue: wrong colour, unfaded first row (fix in flight — needs before/after evidence)

**Code.** Edge cues resolve through `ScrollEdgeCueResolver` (`Foundation/ScrollEdgeCues.cs:52-59`, default band 40 DIP);
the fade is the viewport's analytic feather (no painted band), the chrome signature at `SceneRecorder.cs:1357-1359`, the
chevrons at `:3871 ff.`.

**Decides it.** A bundle on a long list at rest, before and after the fix: pixel queries down a column at
`viewportTop + {1, 4, 8, 16, 24, 32, 40} DIP`. Expected after: exactly one `Tiles` item under each pixel (the list),
`Inherited = 1` with the viewport's feather, `feather(y)` following `EdgeFeatherMask.Evaluate` (0 at the edge → 1 at 40),
composited colour = tile texel × feather over the parent background — no second item with its own fill. Before: the
query shows the extra painted item (its node key names it) and/or feather = 1 on the first row.
`--repaint-identity` `tile-feather-identity` already holds the shader/port agreement to ≤ 1/255.

### #6 "Scroll sometimes blocked or heavily delayed"; artist-page frame spikes 11–15.5 ms; render p95 8.4 ms

**What exists.** `ScrollProbe` rings (8192 records each, UI + render; `ScrollProbe.cs:111-140`) with Input/Plan/Pose/
Coverage/Extent/Cost/Present/Turn rows; `[render.pace]` once a second (`RenderThread.cs:326`); `frame.slow` /
`frame.slack` / `scroll.frames` / `scroll.burst` (`Diagnostics.Host.cs:270-506`); `TileCensus` per turn;
`GpuFrameCounters`; GPU pass timeline (toggle). Open decision (a) in the handoff (StickyClip `ClipTop` writes
`NodePaint.ClipRect`, `AppHost.Scroll.cs:520-527`, → `RecordRequired` → the magazine column re-records every turn while
pinned) is the standing code explanation for the artist-page spikes.

**Missing.** (i) A per-TURN cost breakdown always on (record / BuildComposite / submit / GPU / tiles rastered / bytes /
slices walked) — today `Cost` rows exist for Composite and TileRaster only, and the record cost is not per turn;
(ii) attribution of a spike to WHICH slices re-recorded and WHY (the walk ledger); (iii) "blocked" needs the pairing
notch → plan → first moved present in one export: the rows exist at Trace; at Summary the Input rows are kept for
touchpad only — wheel notch rows must be kept at Summary too (they are cheap: one row per notch).

**Decides it.** A 60 s wheel/hi-res soak on the artist page (§C.6) → `turns.tsv` p50/p95 of each phase; every turn over
8 ms lists its walked slots (`walks.tsv`: node key, bytes, why) and tiles rastered. Expected if (a) holds: while pinned,
every turn walks the magazine slice (`why = RecordRequired/ClipTop`) with ~4 tiles rastered; composite-only turns stay
≤ 1.5 ms CPU. "Blocked": a notch Input row whose next Plan row is > 1 refresh later, or a Plan whose first Pose is
`Clamped` — the CSV already shows it; the harness script computes the histogram.

### #7 Touchpad spring-back (fixed earlier — verify)

**What exists.** `gate.touchpad.dm-stream-monotone`, `TouchpadDragMonotonicityTests`, probe contact rows kept at Summary.
**Cannot be synthesized:** a DirectManipulation contact needs the physical touchpad. **No owner A/B needed:** the owner's
normal use with the default probe level (Summary) already records contact + pose rows; the harness's `Read-Bundle.py
--backsteps` over any `scroll-*.csv` counts poses that step against an advancing contact (must be 0) and lift-time
flings from a rested finger (must be 0). One export by the owner after ordinary use suffices — or, once the new build is
what the owner runs, our `wavee://diag?cmd=bundle` sent to THEIR instance (read-only export, §B) fetches it without them
doing anything.

---

## 2. Gap table (what existing diagnostics already give, what is missing)

| Diagnostic | Gives | Missing for the issues |
|---|---|---|
| Scroll probe CSV schema 2 | motion, coverage, clamps, presents, turns (wake/slot/work), touchpad input | per-turn record/build/submit/GPU split; wheel notch rows at Summary; no link to what re-recorded |
| Tiles card / `TileCensus` | counts per reason, exposed-missing, clamps, budget, partition | **stale tiles**; per-tile identity/history; which slice |
| `SliceRecordStats` | walked/kept/effect/folded/bytes | which slots, why walked |
| `GpuFrameCounters` / `OffscreenSplit` | offscreen surfaces, hits, passes | refused scratch leases; per-item detail |
| Scroll Lab recorder | full sessions, metrics — but only in the Lab app | nothing in Wavee; no composite/tile evidence |
| Always-on logs | `frame.slow`, `scroll.burst`, `[render.pace]`, `[pointer]` | no `[tiles.stale]`, no per-node names, no composite facts |
| `--fg dump` | a scene dump (`AppHost.cs:5585`) | not aligned with a frame, not on demand at runtime |
| Screenshot | `Drive-WaveeWindow.ps1 -Out` (PrintWindow) | not the engine's own frame; no frame seq alignment |

---

## A. Engine instrumentation (always-on, zero-alloc, fixed rings)

All rings live in `src/FluentGpu.Engine/Render/Diag/` (new folder; namespace `FluentGpu.Render.Diag`), owned by the
render-side recorder pair (one instance per `SliceRecorder`/`SliceTable` pair — the UI inline pair and the render-thread
pair each get their own; `AppHost` exposes the render pair's). Every struct is unmanaged; every array is sized at
construction; steady state allocates 0 bytes (covered by `gate.tiles.render-alloc-zero`, which already brackets the
composite turn).

### A.1 Raster ledger + the stale-tile invariant

**Content hash per span, folded per tile.** `SliceRecorder.ScanSlot` (`SliceRecorder.cs:1007`) already walks every byte of
a re-recorded arena once and computes `_scanHash[s] = ContentHash(bytes)`. Extend that single pass to hash per LEAF span
of the existing span index (`SliceSpan`, `TileGrid.cs:113`: bounds + byte range + depth ≤ `SpanIndexDepth = 4`) plus one
"residual" hash for bytes outside every leaf. Leaf = an index entry with no other entry inside its byte range (flagged when
the index is built, `SliceRecorder.cs:560-564`). Total hash work stays 1× the arena (the old whole-arena hash becomes the
fold of the parts).

```csharp
// TileGrid.cs — SliceSpan grows one field; unchanged call sites keep the default
public readonly record struct SliceSpan(RectF Bounds, int ByteStart, int ByteLength, int SortStart, int SortCount,
    bool HasMarker, int Depth = 1, ulong Hash = 0, bool IsLeaf = false);

// TileGrid.cs — TileState grows the ledger fields (12 bytes)
public struct TileState
{
    public int Surface; public InvalidationReason Invalid; public uint ContentEpoch; public int LastUsedFrame;
    public byte Order; public float CoveredLo, CoveredHi; public short SurfW, SurfH;
    public ulong RasterHash;     // WantHash at the last completed raster (0 = never)
    public int   RasterFrame;    // table frame of that raster
}
```

**WantHash(tile)** — computed in `SliceRecorder.BuildComposite` right after `table.Request(...)` (`:1933-1934`), for the
segment's REQUESTED tiles only, and only when the slot was walked this pass (`Rec.Walked`; a kept slot's arena is
byte-identical, so its tiles keep their want hash):

```csharp
// SliceRecorder.cs, after table.Request(...) — per requested tile of a WALKED slot
ulong residual = _scanResidualHash[e.Slot];                      // from ScanSlot
for (int t = 0; t < nNeeded; t++)
{
    RectF tilePx = TileGrid.RectOf(_needed[t]);                   // slice px
    ulong h = residual ^ (ulong)e.Segment * 0x9E3779B97F4A7C15UL;
    for (int x = row.SpanIndexStart; x < row.SpanIndexStart + row.SpanIndexCount; x++)
    {
        ref readonly SliceSpan sp = ref _frameSpans[x];
        if (!sp.IsLeaf || !sp.Bounds.Overlaps(tilePx)) continue;
        h = (h ^ sp.Hash) * 1099511628211UL;
    }
    table.SetWantHash(id, _needed[t], h);                         // stores into a parallel ulong[] _want (slab-sized)
}
```

Bound: leaf spans per segment ≤ a few hundred × requested tiles ≤ 8 → ≤ ~4k rect tests per walked segment, 0 on
composite-only turns. `table.SetWantHash` is O(1) (the slab index). When `Resolve` schedules a tile, `TileRaster` carries
the want hash; `MarkRastered` writes `RasterHash = want, RasterFrame = _frame`.

**The invariant**, computed inside `SliceTable.CountExposedMissing`'s existing slab sweep (`SliceTable.cs:531`, one pass
over `_tiles`, already per turn):

```csharp
// SliceTable.cs — in the same loop as CountExposedMissing (one sweep, no second pass)
if (_used[t] && ts.Surface >= 0 && ts.Invalid == InvalidationReason.None && ts.LastUsedFrame == _frame
    && _scheduledFrame[t] != _frame && _want[t] != 0 && ts.RasterHash != _want[t])
{
    _stale++;
    if (_staleCount < StaleListCap) _staleList[_staleCount++] = t;   // StaleListCap = 64 — the census names them
}
```

`TileCensus` gains `StaleTiles` (must be 0). `TileDiagRules.IsWarning` gains it. The gallery census line prints `stale=`.

**Ledger ring (what was rastered, when, why, at what alpha).** Written in `SliceRecorder.EndComposite`
(`:2166-2179`) for every `rasterDone[i] != 0`, so it names exactly the rasters that completed:

```csharp
// Render/Diag/RasterLedger.cs
public struct RasterEntry            // 32 bytes
{
    public int Frame;                // SliceTable.Frame
    public int NodeIndex; public uint Gen;
    public int SliceId;
    public short Tx, Ty; public short W, H;
    public byte Reason;              // InvalidationReason at schedule
    public byte Order;               // 0 visible / 1 ahead / 2 behind
    public byte AlphaQ8;             // the item's composite alpha this turn (0..255) — the inherited (baked) alpha is inside Hash
    public byte Flags;               // bit0 faithful, bit1 scratchRefused (D3D12 sets via RasterDone flags, see A.3)
    public ulong Hash;               // WantHash it was rastered for
}
public sealed class RasterLedger
{
    public const int Capacity = 4096;                       // 128 KiB; ≈ 25 min of a 2.6-tiles/turn scroll at 60 Hz
    private readonly RasterEntry[] _ring = new RasterEntry[Capacity];
    private long _count;                                    // monotonic; readers drain by count like ScrollProbe
    public void Add(in RasterEntry e) { _ring[(int)(_count & (Capacity - 1))] = e; Volatile.Write(ref _count, _count + 1); }
    public int Read(long from, Span<RasterEntry> dst, out long next) { /* zero-alloc drain, same shape as ScrollProbe.ReadRender */ }
}
```

**Hash history per slice** (for the log line): a per-slot 4-deep ring of `(hash, frame)` in `SliceRecorder` (`_hashHist`,
32 B per slot, written in `ScanSlot` when `_scanHash` changes).

**Log line (edge-gated, never per frame).** In `AppHost.NoteTileCensus` (`AppHost.cs:1126`): when `StaleTiles > 0` and the
(sliceId, wantHash) pair differs from the last logged one, `Diag.Line("[tiles.stale] slice=… node=… tiles=(tx,ty)… want=… have=… rasterFrame=… hist=h1@f1,h2@f2,…")`.
Wavee's `Platform.Host.cs:878 AlwaysOn()` gets `[tiles.stale]`. The node name resolution happens on the UI-side exporter
(A.5); the line itself carries `node=<index>:<gen>`.

**Gates.**
- `gate.tiles.stale-zero` — a `CompositeInvariants.NoStaleTiles(host)` check called from **every** suite's end-of-scene
  hook (`Harness/HeadlessFixture.cs`), like the acrylic invariants: `LastTileCensus.StaleTiles == 0` after every frame
  a suite runs. Permanent sweep.
- `gate.slices.inherited-opacity-rerasters` (failing-first, TileSuite): the #1 reproduction described above.
- `gate.tiles.ledger-alloc-zero`: the ring writes inside the existing zero-alloc bracket.
- Unit: `RasterLedgerTests.Ring_WrapsAndDrainsInOrder`, `WantHash_IsOrderIndependentAcrossLeafSpans`.

### A.2 Composite item record (per item, per frame)

Captured in `SliceRecorder.AddItem` (`:2002-2014`) into a per-frame fixed table, then (after `Resolve`, when `Direct`
degradation is known, `:1958-1963`) the frame's records are appended to a ring of frames:

```csharp
// Render/Diag/CompositeLedger.cs
public struct ItemRecord                 // 64 bytes
{
    public int NodeIndex; public uint Gen;          // the slice's node (−1 for Group/Backdrop/Erase: the layer's node index instead)
    public int SliceId;
    public byte Kind;                               // CompositeKind
    public byte Inherited;                          // distributed fades carried
    public byte Flags;                              // bit0 hasLayer, bit1 distributed (this item IS a fade that was distributed), bit2 groupHit,
                                                    // bit3 degraded(Direct), bit4 stickyEngaged, bit5 clipBounded, bit6 feather2
    public byte AlphaQ8;
    public short ClipX, ClipY, ClipW, ClipH;        // device px (0,0,0,0 = unbounded)
    public short TransDx, TransDy;                  // whole device px
    public short F1X, F1Y, F1W, F1H;                // Feather rect px
    public byte  F1L, F1T, F1R, F1B;                // Feather bands px (clamped 255)
    public short F2X, F2Y, F2W, F2H;                // Feather2 rect px
    public byte  F2L, F2T, F2R, F2B;
    public float BlurSigma;
    public uint  GroupKeyLo;                        // GroupCacheKey low 32 bits (0 = none)
    public short StickyTopPx;                       // floor(StickyY·s)+round(StickyDy·s), or short.MinValue
    public short GroupCount;
}
public sealed class CompositeLedger
{
    public const int Frames = 16, ItemsPerFrame = 256;                   // 16 × 256 × 64 B = 256 KiB
    public struct FrameHeader { public int Frame; public ulong PublishSeq; public int Items; public long Qpc; public float Scale; public short W, H; }
    // fixed slab: _items[Frames * ItemsPerFrame], _headers[Frames]; write cursor = frame & 15
}
```

`StickyEngaged` per item is taken from the `Plan` (`!float.IsNaN(e.StickyY)`), the group hit from the device's group cache
report (`CompositeRecordKind.PrepareGroup` in headless; `D3D12Device` fills a parallel `Span<byte> ItemFlags` on the
`CompositeFrame`, alongside `RasterDone`). Items beyond 256 are counted in the header (`Dropped`), never allocated.

### A.3 Pixel query — "what composited at window pixel (x, y) this frame"

Pure function over one ledger frame + the `TilePlacement` list of that frame (kept in the same slab: ≤ 512 placements ×
12 B), evaluated on the UI thread (or any thread) from a copied frame:

```csharp
// Render/Diag/PixelQuery.cs (pure; Engine.Tests cover it with hand-built frames)
public readonly record struct PixelHit(int ItemIndex, ItemRecord Item, float Feather1, float Feather2, float Alpha,
    TileKey Tile, int TileRasterFrame, ulong TileRasterHash, bool TileStale, bool Covered);
public static int Query(in CompositeLedger.FrameView f, int px, int py, Span<PixelHit> dst)
{
    int n = 0;
    for (int i = 0; i < f.Items.Length && n < dst.Length; i++)
    {
        ref readonly ItemRecord it = ref f.Items[i];
        if (it.Flags.ClipBounded && !Contains(it.ClipX, it.ClipY, it.ClipW, it.ClipH, px, py)) continue;
        float f1 = it.F1W > 0 ? EdgeFeatherMask.Evaluate(Feather(it, 1), px + 0.5f, py + 0.5f) : 1f;   // the shader's port, Render/Tiles/EdgeFeatherMask.cs
        float f2 = it.F2W > 0 ? EdgeFeatherMask.Evaluate(Feather(it, 2), px + 0.5f, py + 0.5f) : 1f;
        bool covered = true; TileKey key = default; int rf = 0; ulong rh = 0; bool stale = false;
        if (it.Kind is Tiles or Region)
        {
            covered = f.TryPlacementAt(it.SliceId, px - it.TransDx, py - it.TransDy, out key, out rf, out rh, out stale);
            if (!covered) continue;                       // no tile under the pixel: this item paints nothing here
        }
        dst[n++] = new PixelHit(i, it, f1, f2, it.AlphaQ8 / 255f, key, rf, rh, stale, covered);
    }
    return n;                                              // painter order, bottom → top
}
```

Exposed as `AppHost.QueryPixel(int x, int y, Span<PixelHit> dst)` over the most recent frame copy (`lock`, like
`LastTileCensus`), and `AppHost.QueryPixelDip(float, float)` scaling by `DeviceScale`. The Diagnostics page and the
`wavee://diag?cmd=pixel` verb both call it (B).

**D3D12 additions** (`D3D12Device.Composite.cs`): `_frameScratchRefused` counter (incremented where `SurfacePool.Lease`
returns −1 inside a tile pass), published in `GpuFrameCounters` as `ScratchRefused` and OR-ed into the raster's flag byte
(a new `Span<byte> RasterFlags` on `CompositeFrame` parallel to `RasterDone`; bit1 = scratchRefused) so the ledger entry
carries it. Whether a refused lease should also clear `RasterDone` is a behaviour change — recorded as the likely #4 fix,
not done here.

### A.4 Walk ledger + per-turn cost row

**Walk ledger** — at the keep/walk decision (`SceneRecorder.cs:1853-1873`) and the `else if (isSliceSelf)` arm (`:1926`):

```csharp
public struct WalkEntry { public int Frame; public int NodeIndex; public uint Gen; public int Bytes; public byte Why; }  // 20 → 24 B
public enum WalkWhy : byte { RecordDirty, SigMiss /* clean, signature changed */, Blocked, KeepDeny, NoPriorSpan, RootTail, PoseLocked }
// ring 2048 × 24 B = 48 KiB; Bytes filled at EndWalk from the arena's BytePosition
```
`SigMiss` is exactly the #1 mechanism; its count per turn is added to `SliceRecordStats.SigMissWalks`.

**Per-turn cost row** — the `ScrollProbe` render ring gets one more kind, always at Summary (schema 3 in
`ScrollProbeCsv`; `ProbeRowKind.TurnCost = 9`), written where the composite turn already writes `RenderCost`
(`AppHost.cs:1116-1119`), fitting the existing 64-B `ProbeRow`:

| Field | Meaning |
|---|---|
| `Seq` | tick seq (joins the `Turn` row) |
| `F0` / `F1` | record ms / BuildComposite ms |
| `D0` / `D1` / `D2` | submit ms / last retired GPU ms / present-slot wait ms |
| `I0` / `I1` | tiles rastered / KiB rastered |
| `Vp` | slices walked (`SliceRecordStats.Walked`) |
| `B0` / `B1` | items count (clamped 255) / flags: bit0 compositeOnly, bit1 keptAll, bit2 skipSubmit |

Wheel notch `Input` rows are kept at Summary (today only contacts are), so "blocked" is answerable from a default-level
export. This is a one-line level change in `ScrollProbe.Input`.

### A.5 Node names resolvable in logs and exports

The render thread only ever stores `(NodeIndex, Gen)`. The UI thread resolves names at export/log time.
`Reconciler.DebugKeyOf` (`Reconciler.cs:747`) covers only keep-alive/relative keys. Add a mount-time side table:

```csharp
// Reconciler.cs — at the keyed mount site: if (element.Key is string k) _scene.DebugKeys[(int)node.Raw.Index] = k;  (UI thread; mounts already allocate)
// SceneStore: internal readonly Dictionary<int, string> DebugKeys = new(1024);  cleared on node free
// AppHost (UI thread): public int DescribeNode(int nodeIndex, uint gen, Span<char> dst)
//   → "<nearest keyed ancestor key>/<child index path>" e.g. "artist-under-band/1/0/3", or "<key>" when the node itself is keyed;
//   "gone:<index>" when not live at that gen.
```
The exporter writes `nodes.tsv` (index, gen, path, element type, bounds) for every node referenced by any exported row.
No per-frame cost; the dictionary write happens once per keyed mount.

### A.6 Frame capture on demand

`AppHost.RequestFrameCapture(string path)` arms a one-shot; the render thread, on the next present it submits, reads the
back buffer back through the same readback the `--repaint-identity` probe uses (`FluentGpu.Windows/D3D12`, the probe's
capture path) and writes a PNG **tagged with the publish seq / table frame** it captured, so the ledger frame, the item
record frame and the pixels are the same present. The readback stalls that one turn (acceptable; it is on demand and the
turn is logged as `capture=1` in its `TurnCost` row).

### A.7 Single instance, profile-scoped

Today `Shell.Host.cs:165 TryAcquire("Wavee", "FluentGpuWindow", payload)` — any second `Wavee.exe` hands off and exits,
whatever its `--profile`. The verify instance must coexist with the owner's:

- App: `InstanceIdRules.For(profileRoot)` (pure, tested) → `"Wavee"` for the default profile, `"Wavee." + FNV(profileRoot)`
  for `--profile <dir>` runs.
- Engine (`SingleInstanceGate.Redirect`, `SingleInstanceGate.cs:138`; `Win32Platform.cs` window creation): the primary
  sets a window property `FluentGpu.InstanceId` (`SetPropW`) on its main window; `Redirect` enumerates windows of class
  `FluentGpuWindow` and targets the one whose property equals its own instance id (falls back to the first window when no
  property matches, today's behaviour). Test: `SingleInstanceGateTests.RedirectTargetsTheWindowOfItsOwnInstance`
  (Windows.Tests, real message-only windows like `WindowsApiSmoke` does).

The harness's own sender never uses the class filter: it finds the hwnd by PID (`EnumWindows` + `GetWindowThreadProcessId`).

---

## B. The evidence bundle

One export, triggered from the Diagnostics page (`Evidence` card) or by `wavee://diag?cmd=bundle&tag=<t>` (WM_COPYDATA),
written under the app's log folder: `%LOCALAPPDATA%\Wavee\logs\evidence\<yyyyMMdd-HHmmss>-<tag>\` (or the `--profile`
folder's `logs`). Written on a worker thread from UI-thread copies (the same pattern as `SaveLyricsBundle`,
`Diagnostics.Host.cs:731`); every writer is hand-written TSV/JSON (NativeAOT-safe, like `ScrollProbeCsv`), UTF-8 no BOM.

```
<folder>/
  meta.json        build, pid, profile, route+arg, navId, publishSeq, tableFrame, qpc, scale, window px, viewports: [{vp, key, offset, extent, viewport}]
  frame.png        the captured present (A.6), same publishSeq
  items.tsv        A.2 records of that frame, painter order, + nodeKey
  ledger.tsv       A.1 ring tail (last 4096 rasters) + stale.tsv (the frame's stale list with want/have/history)
  walks.tsv        A.4 ring tail
  scroll.csv       ScrollProbe export schema 3 (all rows the rings hold; TurnCost joined by seq)
  census.json      TileCensus + GpuFrameCounters + OffscreenSplit + SliceRecordStats of that frame
  nodes.tsv        index, gen, keyPath, type, absRect for every node referenced above
  pixel-<x>-<y>.tsv one per query issued since the last bundle (the picker appends; cmd=pixel writes immediately)
  log-tail.txt     the last 400 lines of wavee-*.log
```

**Alignment rule.** `meta.publishSeq` is the seq of the frame whose items/ledger/pixels are exported; the capture request
and the ledger copy are taken in the same render turn (the render thread copies the ledger frame into the export slab
when it completes the armed capture), so nothing is "the latest of each" — it is one present.

**App-side shape.** `Screens/Diagnostics.Evidence.cs` (card) + `EvidenceBundle.cs` (the writer) + `EvidenceReport` (pure
formatting: TSV rows from records — `EvidenceReportTests`). The `wavee://diag` verbs extend `Shell.DeepLink`
(`Shell.cs:550-584`) with `DeepLinkKind.Diag` (developer-only like `quit`; refused unless developer mode — the verify
profile enables it in its settings file):

| Verb | Effect | Reply |
|---|---|---|
| `wavee://diag?cmd=bundle&tag=x` | export a bundle (arms capture, waits one present) | folder path appended to `logs\evidence\index.txt` and logged `[evidence] bundle=<folder>` |
| `wavee://diag?cmd=pixel&x=..&y=..[&dip=1]` | pixel query on the latest frame | `pixel-x-y.tsv` in the current bundle folder (or `logs\evidence\adhoc\`) |
| `wavee://diag?cmd=scroll&vp=<keyPrefix>&to=<offset>&move=immediate\|glide` | `ScrollHandle.ScrollTo` on the viewport whose `ScrollKey` starts with the prefix | `[evidence] scroll vp=… to=…` |
| `wavee://diag?cmd=vps` | list viewports (key, offset, extent) | `vps.tsv` |
| `wavee://diag?cmd=probe&level=trace\|summary` | set `ScrollProbe.Level` | log line |

Replies are files, not messages: `WM_COPYDATA` is one-way and the sender polls for the file (≤ 2 s). Every verb is a
pure parse (`ShellRoutesTests` gains the table).

**Diagnostics ▸ Evidence card (wireframe).**

```
┌ Evidence ─────────────────────────────────────────────────────────────┐
│ ● Healthy   stale tiles 0 · exposed missing 0 · scratch refused 0      │
│ Pixel  x [ 612 ] y [ 71 ]  (DIP)   [ Query ]   [ Pick with click ]    │
│  #  kind    node                        α     f1     f2    tile  raster │
│  3  Tiles   artist-under-band/1/0       1.00  0.17   —     (0,0) f9123 │
│  2  Region  artist-band/0/1/pivot       0.07  —      —     (0,0) f8811*│   * stale
│  1  Tiles   artist-hero                 1.00  —      —     (0,0) f9120 │
│ [ Export bundle ]  tag [ band-ramp-16 ]       last: logs\evidence\…    │
└────────────────────────────────────────────────────────────────────────┘
```

---

## C. Driver scripts — the scenario harness for the real Wavee

Location: `waveemusic/ops/tools/evidence/` — `Start-VerifyWavee.ps1`, `Send-WaveeDiag.ps1`, `Invoke-Scenario.ps1`,
`Read-Bundle.py`. All run sandbox-free (the GPU is not available inside the sandbox — memory note), from **PowerShell**
(the Bash tool reports x64 on this ARM64 box).

### C.0 Running beside the owner's Wavee (no owner action)

Requires A.7 + the app-side instance id. Then:

```powershell
# Start-VerifyWavee.ps1
$profile = 'C:\wavee\verify-profile'                       # its own store.json, library.db, logs, settings
New-Item -Force -ItemType Directory $profile | Out-Null
Copy-Item "$env:LOCALAPPDATA\Wavee\store.json" $profile -Force   # DPAPI blob, same user → decrypts; gives the signed-in account
# (optional) Copy-Item "$env:LOCALAPPDATA\Wavee\library.db" $profile   # warm cache; leave out for a cold-navigation run
$exe = 'C:\wavee\waveemusic\src\apps\Wavee\bin\verify\evidence\Wavee.exe'       # built by the orchestrator: dotnet build -c Release -o bin\verify\evidence
$p = Start-Process $exe -ArgumentList '--profile', $profile, '--fg', 'gpu-timing' -PassThru
# wait for the hwnd BY PID (never FindWindowW by class: the owner's window has the same class)
$h = Wait-WindowByPid $p.Id 'FluentGpuWindow' 30
[void][D]::MoveWindow($h, 0, 0, 1600, 1000, $true)          # the same monitor every run: positions in bundles are reproducible
```
If the `store.json` copy is refused by the sandbox classifier, that is the one place the owner is needed (a one-time copy).
Until A.7 lands, the harness needs the owner's Wavee closed (it says so and refuses to `Stop-Process`, like
`scroll-capture.ps1`).

`Send-WaveeDiag.ps1 -Hwnd $h -Uri 'wavee://diag?cmd=bundle&tag=x'`: `SendMessageTimeoutW(h, WM_COPYDATA, 0, {dwData =
0x46474143, cbData = 2·len, lpData = utf16})` (`Win32Platform.cs:2064-2083`), then polls `logs\evidence\index.txt` for the
new folder. Deep links for navigation use the same sender (`wavee://open?route=artist&arg=spotify:artist:…`).

Input injection: wheel notches via `mouse_event(MOUSEEVENTF_WHEEL, delta)` after `SetCursorPos` over the page and
`SetForegroundWindow` (foreground is REQUIRED — the engine ignores input while inactive; the memory note); 4-DIP steps as
hi-res packets `delta = −15` (32 DIP/notch → 4 DIP; the classifier latches hi-res for the gesture, each fractional notch
travels exactly its distance — `gate.scroll.hires-burst`). For exact positioning the harness prefers `cmd=scroll …
move=immediate` (the engine's own handle, no input at all). Touchpad/DM: not synthesizable — see #7.

### C.1 Artist band ramp (issues #1, #2, #3a, #3b)

```
1. open   wavee://open?route=artist&arg=spotify:artist:5JZ7CnR6gTvEMKX4g70Amv   (Lauv) ; wait nav.frames for the navId
2. vps    → find vp key starting "artist" ; read heroH from meta (or: offset where the sentinel engages = cd)
3. cd = heroH − 56 ; steps = cd−44, cd−40, …, cd, cd+4 (13 bundles) ; plus rest (0) and pinned (cd+200)
4. for each: cmd=scroll vp=artist to=<o> move=immediate ; sleep 3 presents ; cmd=pixel at the tab-row glyph,
   the accent bar (x,y from the owner's screenshot, rescaled), the "Albums" header top ; cmd=bundle tag=band-<o>
5. Read-Bundle.py band <folders>
```
**Expected numbers.** `StaleTiles`: 0 at every step if #1 is refuted; ≥ 1 on the pivot slice at `o = 0` and `o = cd+200`
if confirmed, with `ledger.RasterFrame` from the opposite state. Pixel at the accent bar: `f1 ∈ [0.05, 0.10]` on the
magazine item (hypothesis #2) OR a stale pivot tile with `AlphaQ8 ≈ 18` (it is #1). "Albums" header while pinned:
`Inherited = 1, F1Y = 56·s, F1T = 24·s` (#3a confirmed). During the ramp: the item(s) with `ClipY = 56·s` and no feather
— its node key decides #3b.

### C.2 Lyrics blanking (#4)

```
1. play   spotify:track:<a track with synced lyrics> (a bare spotify:track: uri starts playback) ; open the rail lyrics
   (Ctrl+K palette "Lyrics" or the rail button by -Click after AppActivate)
2. probe level trace ; every 2 s for 90 s: cmd=bundle tag=lyrics-<n> ; on each `[tiles.stale]` or `[d3d12.scratch]` log
   edge an extra bundle
3. Read-Bundle.py lyrics: for every bundle, the lyrics list slice's items (Folded, GroupCount), ScratchRefused, stale list,
   and a pixel query on each visible line's text row (x = column centre, y = line centre from nodes.tsv)
```
**Expected.** All `ScratchRefused = 0`, `StaleTiles = 0`, every line's pixel covered by the list tile with text glyph
instances → blanking not a raster defect (then it is presentation: opacity/σ values in nodes.tsv). Any nonzero names the
cause.

### C.3 Edge cue before/after (#5)

Long playlist (Q-top 1500, `spotify:playlist:2pnt79m93NytfAj2lByLlQ`), scroll to 800 DIP, bundle; pixel queries at
`viewportTop + {1,4,8,16,24,32,40}` DIP on a row's text; once on the pre-fix build (`bin\verify\prefix`), once post-fix.
**Expected post-fix:** one `Tiles` item per pixel, `Inherited = 1`, `f1(y)` equal to `EdgeFeatherMask.Evaluate` within
1/255, no item with a fill covering the band.

### C.4 Scroll blocked / spikes (#6)

Artist page, 60 s: 20 s wheel notches at 4 Hz, 20 s hi-res bursts, 20 s idle-then-notch pairs (the "blocked" pattern:
a notch after 2 s of rest). Bundle at end; `Read-Bundle.py turns` → p50/p95/p99 of record/build/submit/GPU per turn,
spikes (> 8 ms) joined to `walks.tsv` (which node keys walked, why, bytes) and tiles rastered; notch→plan→first-moved-
present latency histogram; count of notches with no plan within one refresh (must be 0).
**Expected:** composite-only turns ≤ 1.5 ms CPU; while pinned, `walks` shows the magazine slice with `why=RecordDirty`
every turn (open decision (a)) — the spike attribution the owner asked for; anything else is new information.

### C.5 Touchpad spring-back (#7)

`Read-Bundle.py backsteps <scroll-*.csv>`: for each contact (Begin…End), poses whose position moves against the
contact's monotone direction while the contact advances (must be 0), and lifts after `StoppedAfterS` that fling (must
be 0). Source: any owner CSV (`%LOCALAPPDATA%\Wavee\logs\scroll-*.csv`) or a bundle from their instance once it runs the
new build. No A/B; no session asked for.

### C.6 Not disturbing the owner

- The verify instance uses its own profile folder (settings, store, db, logs) and its own instance id; it never touches
  `%LOCALAPPDATA%\Wavee`.
- Scripts refuse to `Stop-Process` anything; the verify instance is closed with `WM_CLOSE` (flushes `session.frames`).
- Sending `wavee://diag?cmd=bundle` to the OWNER's instance is read-only (files under their `logs\evidence`) and is the
  way to collect #7 evidence without asking them — only after their build carries B.
- Foreground: input injection steals foreground for the burst; the harness restores it (`Drive-WaveeWindow.ps1`'s
  AttachThreadInput dance) and runs bursts only while the owner is idle ≥ 4 s (`GetLastInputInfo`), as that script does.

---

## D. Implementation order and ownership

Only the orchestrator builds/tests; subagents write disjoint files. Engine work is verified in `..\fluent-gpu` (Debug +
Release + VerticalSlice + Engine.Tests + Windows.Tests + `check-canon.ps1` after the §13.1j doc edit); app work in
`waveemusic` (Debug + Release + `Wavee.Tests`).

| # | Part | Repo / files | Tests / gates |
|---|---|---|---|
| 1 | **A.1 span hashes, WantHash, `StaleTiles`, ledger ring, `[tiles.stale]`** | Engine: `Render/Tiles/{TileGrid,SliceTable,TileCensus}.cs`, `Render/SliceRecorder.cs` (ScanSlot, BuildComposite, EndComposite), new `Render/Diag/RasterLedger.cs`, `Hosting/AppHost.cs` (NoteTileCensus, `RasterLedger` accessor) | `gate.tiles.stale-zero` (sweep in every suite via `CompositeInvariants.NoStaleTiles`), `gate.slices.inherited-opacity-rerasters` (failing first), `gate.tiles.ledger-alloc-zero`, `RasterLedgerTests`, `WantHashTests` |
| 2 | **A.7 profile-scoped instance** | Engine: `WindowsApi/Activation/SingleInstanceGate.cs`, `Windows/Pal/Win32Platform.cs` (SetPropW). App: `Shell/Shell.Host.cs`, new `Platform/InstanceIdRules.cs` | `SingleInstanceGateTests` (Windows.Tests), `InstanceIdRulesTests` (Wavee.Tests) |
| 3 | **A.2 item record, A.3 pixel query, D3D12 scratch counter** | Engine: new `Render/Diag/{CompositeLedger,PixelQuery}.cs`, `Seams/Rhi/Composite.cs` (`RasterFlags`, `ItemFlags` spans), `Seams/Rhi/GpuFrameTelemetry.cs` (`ScratchRefused`), `Windows/D3D12/D3D12Device.Composite.cs`, `Headless/Rhi/HeadlessGpuDevice.Composite.cs` (fills the flags), `Hosting/AppHost.cs` (`QueryPixel`) | `PixelQueryTests` (feather values vs `EdgeFeatherMask.Evaluate`, painter order, placement lookup), `gate.tiles.item-record-matches-model` (records == headless `DrawItem` records) |
| 4 | **A.4 walk ledger + `TurnCost` row (schema 3), notch rows at Summary** | Engine: `Render/SceneRecorder.cs` (keep site), `Render/SliceRecorder.cs` (`SigMissWalks`), `Scroll/Diag/{ScrollProbe,ScrollProbe.Rows,ScrollProbeCsv}.cs`, `Hosting/AppHost.cs:1116` | `ScrollProbeTests.TurnCost_*`, `gate.scroll.probe-export-chain` extended (schema 3 header), `ScrollMetricsTests` unchanged |
| 5 | **A.5 node keys** | Engine: `Reconciler/Reconciler.cs`, `Scene/SceneStore.cs`, `Hosting/AppHost.cs` (`DescribeNode`) | `Engine.Tests DescribeNode_*` (keyed, unkeyed child path, gone) |
| 6 | **A.6 frame capture** | Engine: `Hosting/AppHost.cs`, `Windows/D3D12/*` (reuse the identity probe's readback), `Headless` (writes no file, records the request) | `gate.tiles.capture-seq-aligned` (headless: the armed capture reports the seq of the frame it hit) |
| 7 | **B card, bundle writer, `wavee://diag` verbs, `[tiles.stale]`/`[evidence]`/`[d3d12.scratch]` in `AlwaysOn`** | App: new `Screens/Diagnostics.Evidence.cs`, `Screens/EvidenceBundle.cs`, `Screens/EvidenceReport.cs`, `Shell/Shell.cs` (DeepLink), `Platform/Platform.Host.cs`, `Screens/Diagnostics.Tiles.cs` (`StaleTiles` row + warning), loc strings | `EvidenceReportTests`, `ShellRoutesTests` (diag verbs), `TileDiagRulesTests` (stale warning) |
| 8 | **C scripts** | App: `ops/tools/evidence/*.ps1`, `Read-Bundle.py`; `ops/release/tests/Evidence.Tests.ps1` (Pester: parsing of a fixture bundle) | Pester on fixture folders |
| 9 | Docs: `gpu-renderer.md` §13.1j (+ ledger/stale/pixel query as owned artefacts), `scroll.md` §10 (TurnCost row), `.claude/skills/fluentgpu-scroll/gates.md`, `waveemusic/.claude/skills/wavee/scrolling.md` (Evidence card, verbs), `docs/guide/scrolling.md` | `check-canon.ps1` |

Order: 1 → 2 → 3 → 7 (minimum to run C.1) → 4 → 5 → 6 → 8 → 9, then the scenarios C.1–C.5 with the verify build, and the
results (bundles + the two failing-first gates' outputs) go to the owner as the root-cause report BEFORE any fix.

---

## E. Render-thread cost budget and how it is measured

| Instrument | When it runs | Cost bound (design) | Memory |
|---|---|---|---|
| Leaf-span hashes + residual | only in `ScanSlot` of a RE-RECORDED slot (already hashes every byte once) | 0 extra bytes hashed; +1 mix per span | +8 B per `SliceSpan` |
| WantHash fold | per requested tile of a WALKED slot | ≤ 4k rect tests per walked segment ≈ 5–10 µs; 0 on composite-only turns | 8 B per tile slab entry (256×64×8 = 128 KiB) |
| Stale sweep | inside the existing `CountExposedMissing` slab loop | +2 compares per used tile (≤ 16k entries, typically < 300 used) | `_staleList[64]` |
| Raster ledger write | per completed raster (2–6 per raster turn) | ~10 ns each | 128 KiB ring |
| Item record | per composite item per turn (~50–200) | one 64-B struct write each ≈ 2–5 µs/turn | 256 KiB slab |
| Walk ledger | per walked slot | ~10 ns | 48 KiB |
| TurnCost row | one `ProbeRow` per turn | ~20 ns | in the existing 8192 ring |
| Hash history | on `_scanHash` change | ~5 ns | 32 B per slot |
| `[tiles.stale]` line | only on a (slice, hash) edge | allocation only on the edge | — |

**Budget:** ≤ 10 µs added to a composite-only turn, ≤ 40 µs to a re-record turn, 0 managed bytes in either. **Measured:**

1. `gate.tiles.render-alloc-zero` / `gate.slices.scroll-tick-zero-bytes` stay at 0 bytes with the rings live.
2. Bench before/after on the same build pair, AC power (the handoff notes battery variance):
   `FluentGpu.WindowsApp.exe --scroll-bench artist-bench --vertical --dipPerSec 2000` and `--scroll-bench virtualization
   --dipPerSec 3000 --seconds 8` — compare the printed per-frame **record ms / capture ms / composite CPU** columns and GPU
   mean/p95 (GPU must be identical: nothing here touches the GPU). Acceptance: CPU per-frame mean delta ≤ 0.05 ms on
   virtualization, ≤ 0.1 ms on artist-bench.
3. `--scroll-soak virtualization` 4 min: no memory growth beyond the fixed rings (the soak prints working set per second).
4. A pure op-count test (not a timing test): `WantHashTests.FoldTouchesEachLeafSpanAtMostOncePerTile` and the
   `SliceTable` sweep count (`StaleSweepVisits == usedTiles`).

---

## F. Owner-facing steps

- **None** for #1–#6 once A.7 lands (the verify instance runs beside theirs from its own profile). One possible exception:
  if the sandbox refuses copying `store.json` into the verify profile, the owner copies it once.
- **#7:** nothing — their existing `scroll-*.csv` exports or, after the next publish, a read-only `cmd=bundle` we send to
  their instance. If neither exists, ask for one Export CSV after ordinary touchpad use (no A/B).
- **Decisions the evidence may hand back:** whether `Collapse(Leading)`'s presented-edge cut should feather (#3b if the
  hero is the cut), and the eventual fixes (#1 damage on signature-miss walks; #4 refused scratch = unfaithful raster) —
  each presented with its bundle and its failing-first gate before implementation.

---

## As built

Deviations from the design above, folded in during implementation:

- Want hash per OP, not per span-index LEAF (a leaf straddling a tile boundary that changes on one side would be a
  false stale); child-slice markers excluded from wants; per-SURFACE ledger storage in `SliceTable` (at most
  `SurfaceCap` entries) instead of 12 extra bytes in every `TileState` of the 16k-entry slab; `SliceSpan` unchanged.
- `CompositeLedger` holds 2 frames (work + published, swapped under a lock) instead of a 16-frame ring; the capture
  copies its turn's frame on the render thread; the picker reads the latest.
- `ItemRecord` stores both `EdgeFeather`s exactly (the pixel query's feather equals the shader's); no `GroupKeyLo`
  (the group-cache outcome is the Hit/Rendered flags).
- `TurnCost` D2 carries the recorder PASS FRAME (the `walks.tsv` join key); the slot wait stays on the `Turn` row.
- Notch rows at Summary: already kept for every source (hi-res included) — pinned by a test, no code change needed.
- The raster ledger logs every SCHEDULED raster with a faithful bit (the unfaithful ones matter for #4).
- A.7 engine side: the instance id travels through `FluentGpu.Pal.InstanceIdentity` (engine; both `WindowsApi` and
  `Windows` reference it) — the gate stamps it, the PAL tags the window; a redirect prefers its own tag, then an
  untagged window, never another instance. App side also: a `--profile` instance never registers protocol handlers,
  start-on-login, the toast activator or the jump list (`InstanceIdRules.OwnsOsIntegration`).
- The evidence gates live in a new `Suites/EvidenceSuite.cs` (tag `tiles`, runs with `--suite tiles`), not in
  `TileSuite.cs`.
- `keyed.tsv` added to the bundle (every keyed node + rect) so scenarios derive the band geometry instead of
  hard-coding it.
- `[evidence]` also routed AlwaysOn (the engine's capture-failure line).
- Corrections applied after the design pass: the engine folder/namespace is **`Render/Evidence/*`**
  (`FluentGpu.Render.Evidence`), not `Render/Diag/*`. The composite item record additionally carries the FEATHER QUAD
  SPLIT interior (`ItemRecord.InteriorL/T/R/B`: the intersection of both feathers' `EdgeFeatherMask.UnitInterior`,
  device px) and the sticky line (`StickyTopPx`, `StickyEngaged` for the `CompositeSliceFlags.StickyClip`
  composite-time clip); `PixelQuery`'s `PixelHit.InFeatherBand` says whether the pixel was drawn through a feathered
  strip or the feather-free interior. Wavee's bundle `items.tsv` gains a `featherInterior` column and `pixel.tsv` a
  `featherPiece` column (`band` / `interior` / `-`). The per-op want hash and the walk ledger cover every slice role
  including `Layer` and `Chrome`. The stale log line `[tiles.stale]` also names `role=` and `seg=`; it is formatted
  off the render thread (a pooled work item), logged once per newly stale slice.
- **Finding at first run:** the permanent `stale-zero` sweep fails in 17 suites. Dominant class: `SliceRole.Thumb`
  (and `Chrome`) slices — the scroll thumb re-records with a new fill alpha under `BeginWalk`, but the only damage
  (`SceneRecorder` node damage, `AddDamage(stats.CurSlot, …)`) goes to the enclosing slot, never the thumb/chrome slot,
  and `SliceTable` has no content-driven invalidation; second class: Main content where the op's conservative ink
  bounds (glyph ±18 px, fill ±2 px) overlap a tile that the node's model-rect damage does not reach (virtualized rows
  unrealizing, 1-px moves). Reported, not fixed (evidence only).
- **#1 fixed (2026-09-25): tile validity is derived from content** (`gpu-renderer.md` §13.1c). The live verify bundles
  (`verify-profile\logs\evidence\20260925-0025*-band-*`, node 1454, rasterFrame 6527, want 2e4e5bda vs raster
  916f22) and the headless sweep (18 failing suites, `.tmp/vs-final-{1,2,3}.txt`) named one mechanism for all three
  manifestations — bytes that changed with no damage rect reaching their own slot (the tab slice's SigMiss re-walk with a
  new baked alpha; the thumb / chrome fill alpha; an op's halo past its node's damage rect). The per-op content table and
  the per-tile wants moved out of the evidence partial into the always-on `SliceRecorder.Content.cs` /
  `SliceTable.Content.cs`: `SliceTable.Request<TContent>` folds a resident valid tile's want when its segment's key moved
  and re-rasters it when the want differs from its raster hash (`PrimCount` if its op count changed, else `Content`);
  `TileCensus.ContentChecked` / `ContentCaught` count the folds. The per-node slice damage machinery
  (`SliceDamageRect`, `AddDamage`/`AddWindowDamage`, `CompositeFrame.SliceDamage`, `SliceRow.DamageStart/Count`) is
  DELETED: with content validity it only over-invalidated (full VerticalSlice run: 40 091 tile rasters without it vs
  42 241 with it, no suite more). The image cross-fade stays a rect invalidation (the one pixel change no byte carries).
  The stale sweep stays as a permanent detector (now 0 in all 32 suites). `gate.slices.inherited-opacity-rerasters` was
  REWORKED first: the old scene's parent opacity became a composite group alpha (and an overflowing tab lane an edge-fade
  group), so it never baked the alpha; the new scene is a `.Reveal` band cut as a translation Effect slice over a nested
  tab scroller that fits its lane, failing with `stepsRastered=0/7 staleTurns=12 sigMissWalks=7` before the fix, passing
  after, and promoted to a plain Check (no open evidence gate remains). Unit: `SliceTableContentTests`.
