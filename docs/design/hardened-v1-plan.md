# FluentGpu — The Hardened v1 (parallel + safe-by-construction)

*Lead-architect plan. Folds the v2 program (render-thread seam, off-thread tessellation/raster, incremental everything) into v1 and closes every risk the [painpoints assessment](./winui-painpoints-assessment.md) named — **without pretending a concurrent, hand-rolled engine is risk-free.** Each hardening was adversarially reviewed; the reviewers graded threading/renderer/alloc/validation `reduces-not-eliminates` and COM `safe-with-fixes`, and those honest verdicts are reflected below.*

## 1. THESIS

Hardened v1 is the lean single-thread v1 with the v2 program folded in **as a build order, not a flag**: a render-thread seam, off-thread tessellation/raster, incremental everything, COM made generated-and-gated, and a validation spine that turns each claim into a CI gate. The discipline that makes it "hardened" rather than "bigger and riskier" is a single rule the five designs converge on: **safety rests on confinement + immutability (single-writer per piece of mutable state, immutable snapshots across threads) — not on a human auditing barriers forever.** Where a hazard reduces to "one thread owns this refcount/slot/column," it is safe-by-construction. Where it reduces to "a fence/epoch/corpus must stay correct," it is honestly labeled MANAGED and backstopped by a CI gate.

The honest one-liner on each pain point, AFTER hardening:

- **GC pressure — was LARGELY-solved, now SOLVED-to-a-bound (grade A-).** Idle is O(0) allocations (memoization + dirty worklist). The edge is O(visibly-changed-subtree) Gen0, never O(tree): component-level memo skip (structural sharing by reference), `UseMemo`/`UseVirtual`, and a per-phase alloc-tripwire gate that fails CI on regression. **Residual:** user closures are irreducible (the programming model), and a legitimate huge-subtree `setState` still mints that subtree in one frame with no GC backstop on the frame thread. Not zero — bounded and CI-enforced.
- **Slow UI thread — was THE problem, now DECOUPLED-not-invincible (grade B+).** Reconcile+layout stay on the UI thread; record/batch/submit/present move to a dedicated render thread reading an immutable `SceneFrame`. A transient GPU hiccup or a busy handler no longer stalls present — the compositor keeps presenting the last good frame. **Residual:** a *sustained* GPU stall (TDR/present-wait) still couples back to the UI thread as a bounded per-frame block of timeout T after buffers exhaust. This is irreducible (you cannot render-ahead infinitely) and is the same ultimate limit WinUI's compositor has.
- **Slowness — was PARTIALLY/TRADED, now FIXED-for-the-common-case, TRADED-honestly-at-the-tail (grade B+).** Steady-state is parallel and incremental: O(change) layout, O(change) record via hardened clean-span reuse, off-thread tessellation with a cache that makes steady state zero pending work. **Residual:** cache-miss frames (first paint, DPI change, newly-revealed vector content) still pay a coordinated cost; and the concurrent seam itself is genuinely harder to keep correct than a single thread.

The bottom-line posture: **this is not a risk-free engine. It is a v1 where every known risk has either a structural mechanism that eliminates it or a CI gate that blocks its regression, and the boundary between the two is drawn explicitly.**

> **Scope note (gap-analysis fold-in).** Orthogonal to the five hardenings, **every gap in [`core-fundamentals-gap-analysis.md`](./core-fundamentals-gap-analysis.md) (Tier-1..Tier-3) is now folded into CORE** — lanes + automatic batching, the Suspense boundary, the external-store consistency contract, the gesture arena, text selection + read-side + editable seam + `ITextRangeProvider`, the overlay/portal manager, RTL layout mirroring, derived-state selectors, springs/retarget/shared-element/motion-tokens, optimistic updates, UIA collection relations + virtualized realization, the control kit, edge localization, the cursor/`Pal.SetCursor` contract, edge-autoscroll, the live devtools inspector, and the app-author test harness (see [`SPEC-INDEX.md`](./SPEC-INDEX.md) §2 and the [`subsystems/`](./subsystems/README.md) ownership map). There is **no v2/deferred/out-of-scope carve-out** for any of them. They are additive subsystem designs that land *within* the §6 build order below (most on the single-thread step 1; the few that touch the seam respect the same race-gate sequencing) — they neither relax nor reorder the threading/safety posture. The residuals that remain are exactly the **physical** ones this plan already names: a sustained GPU stall still bounds back to the UI thread (§4.3); **production safety == CI coverage** (every guard is `[Conditional]`-erased); and the build *order* ships single-thread-correct first, then flips parallelism behind the race gate (§6) — truths, not gaps.

---

## 2. THE REVISED THREADING MODEL

### 2.1 Topology

Three thread classes, confinement as the safety primitive — each piece of mutable state has exactly one writer thread.

```
┌─ UI THREAD (the app/Reactor thread) ─────────────────────────────────────────────────┐
│ SOLE WRITER: SceneStore SoA columns · LayoutCache · ComponentTable · HookCells ·       │
│   prev-Element retention · Brush/Clip/GlyphRun INTERN side · skyline atlas PACKING      │
│ RUNS: pump · input-dispatch · hook-flush · render · reconcile(time-sliceable) ·         │
│   layout · layout-effects · animation · PUBLISH(SceneFrame) · passive-effects           │
│ TOUCHES ZERO COM. No ComPtr, no command list, no GPU fence, ever.                       │
└───────────────────────────────────────────────────────────────────────────────────────┘
        │  PUBLISH: fill free triple-buffer slot → Volatile release-store of _publishedIdx
        │           + 4-byte token on Channel<seq> (cap 1, DropOldest = coalesce signal only)
        ▼
┌─ RENDER THREAD (one, dedicated, affinitized; outlives any frame) ──────────────────────┐
│ SOLE WRITER & SOLE COM OWNER: every ComPtr · RhiHandleTable · PSO cache · UPLOAD ring ·  │
│   glyph GPU atlas pages · tessellation slab · GPU fence · deferred-delete ring ·         │
│   swapchain/DComp visual · render-thread-PRIVATE DrawList arenas (>=3)                    │
│ RUNS (on the consumed snapshot): DRAIN(workers, atlas eviction→epoch bump) →             │
│   record(clean-span from its OWN prior arena) → batch → submit → present                 │
│ DETECTS device-lost → Volatile.Write(reason word) → UI thread rendezvous                 │
└───────────────────────────────────────────────────────────────────────────────────────┘
        │  pure jobs (decode/palette/fetch/tessellate-cold/glyph-RASTER); results by handle
        ▼
┌─ WORKER POOL (N = clamp(ProcessorCount-2, 2..6); owned threads, not ThreadPool) ────────┐
│ Pure functions over immutable inputs → POD by handle. Touch NO SceneStore, NO RhiTable,  │
│   NO GPU fence. Each AllocScope-wrapped. Results land in lock-free MPSC ring.            │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

The single most important confinement decision: **the render thread owns every ComPtr.** This retires the §4.1 "COM-under-AOT cross-thread refcount race" fear structurally — there is exactly one thread that can AddRef/Release/Dispose any ComPtr, so refcount races are not "audited," they are impossible. The cost is that the UI thread cannot touch GPU resources; that is the point.

### 2.2 The 13-phase → thread map (final)

| Phase | Thread | Note |
|---|---|---|
| 1 pump | UI | reads device-lost reason word + present-ack seq (single aligned words, Volatile) |
| 2 input-dispatch | UI | hit-tests **last-published-consistent** topology (UI-owned `Bounds` double buffer), never the in-flight snapshot or a half-built reconcile |
| 3 hook-flush | UI | |
| 4 render | UI | `Component.Render()` — gated by memo skip (most subtrees skipped) |
| 5 reconcile | UI | **time-sliceable**; atomic-on-complete (never publishes a half-built tree) |
| 6 layout | UI | incremental, O(change), boundary-firewalled |
| 6.5 layout-effects | UI | |
| 7 animation | UI | writes LocalTransform/Opacity, composes WorldTransform[] |
| **PUBLISH (13a)** | **UI** | the single seam point: copy snapshot columns, seal, release-store, tick quarantine |
| 8 record | **RENDER** | clean-span memcpy from render-thread-PRIVATE prior arena (not UI-aliased) |
| 9 batch | **RENDER** | overlap-grid-bounded; re-applies WorldTransform to cached quads |
| 10 submit | **RENDER** | ExecuteCommandLists → Signal(fence) |
| 11 present | **RENDER** | latency-wait → Present → DComp Commit → Volatile.Write present-ack |
| 12 passive-effects | UI | next frame, for any seq ≤ last-presented (≤1 frame latency) |

### 2.3 The `SceneFrame` snapshot, quarantine, and cross-thread ownership

**Snapshot (Cut B — record runs on the render thread).** `SceneFrame` is POD: a `PublishSeq`, a `DrawListView` (which front bytes), a `DamageView`, **value-copied `SnapshotColumns`** (only the ~120B/node record needs — WorldTransform, Bounds, NodePaintLite, Flags), stable references into append-mostly content-immutable retained tables (Brush/Clip/GlyphRun/TessCache), and the captured-epoch arrays. Columns are copied because the UI thread mutates them next frame; clean nodes' columns memcpy-clean from the back snapshot buffer by the same `IsLive ∧ epoch-unchanged` rule.

**Critical correction folded from the critique:** the WorldTransform copy is **not damaged-only**. An ancestor transform change (scroll/camera) dirties WorldTransform for the whole descendant set while their DrawList spans stay clean (the TransformDirty fast path). So the cost is budgeted as **up-to-full-tree WorldTransform copy (NodeCount × 24B) during any transform animation/scroll** — sub-ms at 5k nodes, but per-frame during a scroll, not "0.1ms damaged-only."

**Publication.** Triple-buffered `SceneFrame[3]` carries the data; one `Volatile.Write(_publishedIdx)` release-store / `Volatile.Read` acquire-load is the happens-before. **Both directions are volatile** (critique fix): the UI thread reads the render-thread-written `_consumeIdx` with acquire before picking a free slot, so it can never overwrite the slot the render thread is reading. `Channel<seq>` cap-1 DropOldest is the wakeup + coalescing signal only, never the data path.

**Quarantine — consume-gated, not publish-gated (the keystone correctness fix).** The original design reclaimed slots by publish count while the proof required consume count — a real UAF under DropOldest coalescing. Final rule:

> A SceneStore slot freed during production of frame `p` is reclaimable only when `_lastConsumedSeq > p`, where the render thread `Volatile.Write`s `_lastConsumedSeq` on each `TryAcquire` and the UI thread `Volatile.Read`s it at quarantine-tick. `QUARANTINE = render_in_flight_depth` is a compile-time-asserted derived constant, not a magic `2`, with `+1` slack as belt-and-suspenders.

**DrawList arenas are render-thread-private and ≥3-deep (the second keystone fix).** The original left the 2-deep arenas swapped/reset by the UI thread while the render thread memcpy'd clean spans from the back arena — a torn-DrawList race the single-thread v1 never had. Final: the render thread owns N≥3 DrawList arenas; record reads its own previous arena; the UI thread never swaps or resets a DrawList arena. The §9 memory accounting reflects 3× arenas.

**Two orthogonal fences, unified for split resources.** Publish-seq quarantine protects CPU slot reuse (UI→render); the GPU fence deferred-delete ring protects GPU resource lifetime (render→GPU). A split resource (brush GPU realization, glyph page) clears **both, ordered**: when the UI frees a handle at publish `p`, the retire-request is tagged `seq=p`; the render thread does not push it to the GPU delete ring until `_lastConsumedSeq > p` AND keys the ring entry on the max in-flight submit fence ≥ p. This closes the premature-GPU-free-across-the-seam window the critique found.

---

## 3. SAFETY-BY-CONSTRUCTION LEDGER

| Risk | Mechanism | SAFE-by-construction OR MANAGED | Residual |
|---|---|---|---|
| **§4.1 hand-rolled COM** | (a) slot/convention generated from machine-harvested `*.comabi.json`, no human types an integer; (b) **all ComPtr confined to render thread** (single-writer refcount); (c) `CcwBox` GCHandle freed only at Interlocked refcount 0; (d) cold COM is `[GeneratedComInterface]`-only by assembly policy | **SAFE** for slot/convention (no human integer), cold surface, and refcount-race (confinement). **MANAGED** for refcount/GCHandle *lifetime* and the DWrite no-retain assumption | Generator/harvester are hand code (triple-oracle + runtime QI-roundtrip CI gate); refcount detection is post-hoc; DWrite multithread no-retain validated by concurrent conformance test, not proven |
| **§4.2 tessellation/batch correctness+perf** | one vetted O(n log n) monotone/trapezoidal sweep (ear-clip deleted); overlap-grid batching O(n·tiles); content-epoch cache key | **SAFE** for complexity bound (no O(n²) path exists). **MANAGED** for geometric robustness (FP degeneracy → watertight-but-wrong) and painter-order | Robust-predicate failures fuzz-gated + differential-rasterizer cross-check; D2D fallback on golden failure |
| **§4.2 AA quality** | AA-fringe→MSAA→D2D ladder; CPU 16× supersampled reference; CIEDE2000 + edge-shift golden gate | **MANAGED** (corpus-gated regression net, not a proof) | Uncovered DPI/rotation/color/script is ungated; WARP-vs-hardware forces perceptual tolerance |
| **§4.3 UI-thread stall** | render-thread seam: present/submit off the UI thread; compositor presents last good frame during a UI pause | **SAFE** for transient hiccup (absorbed). **MANAGED** for sustained stall | Sustained GPU stall → bounded per-frame UI block of timeout T after buffers exhaust; irreducible |
| **§4.3 seam concurrency** | single-writer ThreadGuard (deterministic throw in asserts); consume-gated quarantine; private arenas; both-directions-volatile publish | **SAFE** for confinement violations (deterministic throw, asserts builds). **MANAGED** for the one lock-free fence handoff | No managed TSan — the fence's acquire/release correctness is reviewed + schedule-fuzz-sampled, not proven; asserts erased in shipping AOT (production safety == CI coverage) |
| **§4.4 maturity (claims≠properties)** | trust-ring `[Capability]` attribute + analyzer fence + spike-gated promotion via CI trust-ledger | **MANAGED** (build-enforced at direct call sites; runtime asserts catch forwarding shims in CI) | Transitive forwarding is an analyzer gap (closed by a runtime `IsSpikeCaller` assert in CI, not by the compiler); a weak spike behind a `Proven` ring is a review judgment |
| **§4.5 Element churn** | memo skip (structural sharing) keeping v1's correct per-consumer `contextChanged` gate; `UseVirtual`; per-phase alloc-tripwire + process-wide BDN backstop | **MANAGED** (O(Δ) Gen0, not zero) | Closures irreducible; huge legitimate subtree change still O(subtree) in one frame |
| **§4.6 glyph-atlas epoch / clean-span stale** | single `Mutate()` chokepoint bumps epoch; render-thread-LOCAL epoch validation (epoch travels with the cached span); `RealizationDirtySet` auto-invalidation; content-hash dedup makes brush/clip epochs degenerate to `IsLive`-only; atlas live-pin + overflow region | **SAFE** for brush/clip (degenerate to IsLive). **MANAGED** to "redraw-not-corrupt" for the three in-place-mutated tables (glyph UV, path tess, image atlas) | Validator must capture **baked geometry** too (critique fix), not just handle/epoch — else a layout-bounds-move-without-PaintDirty ships stale; double-fault is the only residual, CI-caught |
| **§4.7 arena cliff** | segmented/reserve-then-commit ChunkedArena (O(1) add-chunk, no copy, native memory → GC never sees it), EWMA high-water reclaim | **SAFE** (no crash on a fixed arena; no LOH/Gen2 copy spike ever) | System-OOM on Commit still fatal (clean exception, not corruption); native counter — not the GC tripwire — must gate chunk growth |

---

## 4. THE FIVE HARDENINGS

### 4.1 Threading — the render-thread seam (lands in `Hosting` + `Render` + `Rhi.D3D12` leaf)

Key types: `SceneFramePublisher` (triple-buffer, both-directions-volatile), `SnapshotColumns` (double-buffered render-owned), `QuarantinePolicy.Quarantine = RenderInFlightDepth` (compile-asserted), `RhiHandleTable.RetireDeferred/DrainRetired` (fence-keyed, render-thread-only ComPtr.Dispose), `ReconcileSlicer` (deadline-driven, atomic-on-complete), `ThreadGuard.AssertWriter` (deterministic throw).

Folded critique fixes: **consume-gated quarantine** (reclaim on `_lastConsumedSeq > freedSeq`, not publish count); **≥3 render-private DrawList arenas** (no cross-thread arena aliasing); **render-frame ordering invariant** — DRAIN(workers) → atlas eviction (bump epochs) → clean-span validation/record, with the eviction liveness set computed from the snapshot's command stream, and **epoch validation render-thread-LOCAL** (compare live epoch against the per-span epoch recorded into the render's own back arena — zero cross-thread epoch staleness); **split-resource retire deferred behind both fences**; **backpressure** = DropOldest coalesce (normal) / bounded T-timeout block then drop-frame (degenerate, named T = one vsync); **`_consumeIdx` volatile**. Time-sliced reconcile defaults to atomic-on-complete on the UI thread; off-thread reconcile is opt-in/spike-gated/MANAGED.

Tests: `Render_NeverObserves_RecycledSlot` (pause render 3+ publishes, assert no reclaim until `_lastConsumedSeq` advances); `ConcurrentRecord_MatchesSingleThreadedGolden` (10⁶-frame schedule-fuzz, byte-identical to single-thread golden); render-thread present-stall bench (assert UI input latency bounded, quarantine non-growing).

### 4.2 COM-safety — reduce the hand-rolled surface to ~3 primitives + 2 build tools

Key generators/types: `FluentGpu.AbiHarvest` (winmd ⨯ ClangSharp → checked-in `*.comabi.json`), `ComInteropGenerator v2` + `ComCalleeGenerator` (consume + CCW vtables from the harvested JSON — no human types a slot or convention), `ComPtr<T>`, `IUnknownOps`, `CcwBox<T>`, `ComTracker`, the `FGCOM####` analyzer, `[GeneratedComInterface]`-only `PlatformIntegration`.

Folded critique fixes (the headline was overclaimed): **downgrade "impossible by construction" → "no human types the slot/convention; build-time cross-checked; runtime-verified in CI-RELEASE against live objects; shipping relies on generator correctness + frozen-COM-ABI contract."** **`AbiVerify` made fail-safe** — never call AddRef/Release through an *unverified* slot; anchor on the ABI-frozen IUnknown slots, use `QueryInterface(ownIID)` pointer-equality first, wrap in a vectored-exception guard. **Promote the live-object QI-roundtrip to a non-suppressible CI-RELEASE gate** (the only header-independent oracle; winmd and ClangSharp share header-level errors so they are NOT independent). **`ComTracker` re-keyed by (pointer, generation)**, not raw nint — mandatory before the seam, else ABA across snapshot recycle gives false-green. **Cross-thread story stated:** all ComPtr render-thread-confined; SceneFrame transfers ownership by Move, never shares a ComPtr by reference; a new FGCOM rule pins each COM object to one thread; DWrite shaping CCWs confined to one thread (factory = SHARED, serialized) with a concurrent conformance test, or off-thread raster is descoped. **Safety floor under schedule pressure named:** if FGCOM analyzers are suppressed, the non-suppressible CI conformance/leak/ABI-golden gate is the floor.

### 4.3 Renderer/tessellation — vetted algorithm + golden gates, off-thread DESCOPED behind the seam

Key types: `RenderLane` classifier (rect/rrect/ellipse/capsule/shadow → analytic SDF always; genuine Bezier → tessellated), one monotone/trapezoidal sweep, `PathRealizationKey` (content-epoch in the key; immutable `PathData` with required-epoch ctor → missed bump is a compile error), `OverlapGrid` (O(n·tiles) batching), `BatchStats`, `PsoPermutations` (build-enumerated), `IPrimitiveFallback` (D2D, gated by golden disposition), the golden harness (16× supersampled CPU reference, CIEDE2000 + edge-shift, A/B-vs-DWrite).

Folded critique fixes (the critical sequencing correction): **off-thread tessellation and glyph raster are DESCOPED from the single-thread phase and sequenced behind the §4.1 render-thread seam (quarantine ≥2).** The original claimed they were safe at quarantine=0; they are not — the `DrainAll()` barrier re-imports the UI-thread stall (plus priority inversion), and the cache eviction path is a slab ABA against in-flight readers at quarantine=0. **Until the seam lands, on-UI-thread tessellation with the geometry cache (zero steady-state cost) is the safer choice and ships first.** When off-thread lands: snapshots **copy** verb/point spans into worker-owned arena slices (not `ReadOnlyMemory` views over aliasable arrays — the deep-immutability analyzer cannot prove non-aliasing otherwise); the slab uses the deferred-free fence ring. **Painter-order:** `CanMergePreservingPainterOrder` consults the grid's stored last-writer (covers the three-primitive hole), and both the grid break and the radix stable-sort tie-break derive from the same record-sequence number; status downgraded to "gated + bounded by grid resolution." **PSO claim scoped** to the native fixed set; Custom/effect/FallbackD2D blends are runtime-warmed with a one-time hitch. **"Validated property" renamed "corpus-gated regression net"** with an explicit "uncovered input → ungated" caveat. **Tessellator's two claims separated:** complexity-bound is SAFE-by-construction; geometric correctness is fuzz-gated (watertightness + winding + robust-area cross-check vs an independent scanline rasterizer at higher precision). **D2D fallback is a Windows-only crutch** — a per-primitive Metal-milestone debt list.

### 4.4 Alloc/incremental — O(Δ) edge, no arena cliff, airtight epochs, O(change) layout

Key types: `TryMemoSkip` (keeping v1's correct triple gate: `SelfTriggered || propsChanged || HasConsumedContextChanged(slot)`), `ChunkedArena` + `IVirtualMemory` PAL seam, `RealizationCell<T>.Mutate` (the single epoch chokepoint), `RealizationDirtySet`, `CleanSpanWitness`, the layout-boundary firewall + two-rule invalidation graph.

Folded critique fixes (two critical): **do NOT replace the per-consumer `contextChanged` check with a `SubtreeDirty` gate** — that substitution makes v1 *less* safe than the v1 it started from (a context consumer not on a setState path would be wrongly skipped → dropped update). Keep v1's correct three-signal skip; use `SubtreeDirty` only as the traversal scope. **The clean-span validator MUST capture baked geometry**, not just (handle, epoch) — device-space rects live inside command payloads (`FillRoundRectCmd.Rect`, `DrawGlyphRunCmd.BaselineX/Y`), so a Bounds-move-without-PaintDirty currently passes the handle/epoch validator while shipping stale geometry. Fix: witness records a Bounds/rect hash and the validator recomputes dest rects from current `Bounds[]/WorldTransform[]` and asserts equality (or route all Bounds writes through a chokepoint with an asserted post-condition). **Epoch second oracle:** hash the actual realization backing bytes independent of the epoch counter and reference graph (catches forgotten-bump + untracked-reference). **`RealizationDirtySet` made O(1)** via a per-layer precomputed sorted handle-set produced at record time (no per-frame span re-decode); huge layers fall back to re-record above a threshold. **Native high-water counter — not the GC tripwire — gates chunk growth** (the GC byte API cannot see `NativeMemory`/`VirtualAlloc`). **`RealizationCell` downgraded** to "strongly-encapsulated + DEBUG-analyzed" (analyzer is suppressible; pointer arithmetic over the slab is a residual).

### 4.5 Validation — the spine that keeps it true (lands in `FluentGpu.Validation`, `[Conditional]`-erased from shipping AOT)

Two clocks: **SPIKES** (capability gates: `text.conformance`, `com.aot.roundtrip` run against the **PublishAot'd binary**, `render.aa`, `seam.race`) retire §4.4 unknowns once + nightly re-characterization; **GATES** (per-PR regression nets: alloc-tripwire delta==0 hot phases + process-wide BDN backstop, footprint `.mstat` ratchet, headless structural draw-call/batch/Bounds, perceptual golden, COM net-refcount, epoch-invariant) block regressions. Trust-ring `[Capability]` + analyzer (FG0001-3) makes shipping code physically unable to compile against an `Experimental` capability.

Folded critique fixes: **`GC.GetAllocatedBytesForCurrentThread()` does not follow work across the seam** — every fan-out worker is independently AllocScope-wrapped with a per-frame summing aggregator, and the **process-wide BDN gen0/1/2==0 gate is load-bearing** (the per-thread tripwire is only the localizer). **`seam.race` must sweep channel-capacity and reader-stall as fuzz parameters**, not hard-code quarantine=2 — else it validates the off-by-one rather than the relationship; **do not flip 0→2 on the current soak.** **The retire-fence handshake replaces DropOldest's unbounded run-ahead** for slot recycling (mirrors the GPU deferred-delete ring), giving a documented acquire/release happens-before for the one lock-free surface. **ABI generator gets a runtime self-check** (call a known method, assert observed behavior vs the loaded system DLL) — closes the extraction + runtime-ABI gap the SHA cannot. **Trust-ring forwarding gap closed** by a runtime `IsSpikeCaller` assert in asserts builds.

---

## 5. WHAT THIS COSTS v1

**Complexity.** The seam is the single largest concurrency surface in the system — a dedicated render thread, the publisher, consume-gated quarantine, two unified fence systems, device-lost rendezvous, the sliced reconciler. COM adds a harvester + two generators + an 11-rule analyzer + `CcwBox`. Validation adds roughly a subsystem's worth of harness + four curated corpora (text/AA/COM/perf). This is multi-month work on top of an already-large v1.

**Binary/footprint.** Generated `.g.cs` bindings (already budgeted), `__AbiSlots` resolved as CI-only-stripped (not both trimmed-and-RELEASE-enableable — pick CI-RELEASE gate, strip from ship). All asserts/trackers/tripwires are `[Conditional]`-erased → **~zero shipping runtime/footprint cost.** Working set: +1-2 MB (snapshot double-buffers, triple `SceneFrame` slots, ≥3 DrawList arenas, quarantine ring).

**The explicit risk the critics raised: does the concurrent seam make v1 LESS safe than a *correct* single-thread v1?** Honestly: **yes, in two specific senses, and it is bought back as follows.** (1) A concurrency bug is now *possible* where it could not exist single-threaded. Bought back by confinement (single-writer ThreadGuard = deterministic throw), immutable snapshots (no shared mutable hot-path state), consume-gated quarantine + render-private arenas (the two keystone fixes that close the UAF/torn-DrawList the original design actually had), Move-only ComPtr transfer, and generation-keyed leak tracking. (2) The data-race gate is **sampled, not proven** — there is no managed TSan, so the one remaining lock-free fence's correctness rests on review + schedule-fuzzing. Bought back by shrinking the lock-free surface to exactly one named, documented acquire/release fence (the retire-fence, mirroring the proven GPU fence pattern) and making any new lock-free shortcut a standing review red-flag. **Net: the correct single-thread v1 is genuinely safer than the seam until snapshot lifetime, bounded backpressure, consume-gated quarantine, and the retire-fence are all landed and the `seam.race` soak (with swept parameters) is green. That is why §6 ships single-thread first.**

---

## 6. BUILD ORDER (safety is never speculative)

1. **Single-thread v1, correct, behind the snapshot seam shape.** Build `SceneFrame`/`SnapshotColumns`/the publisher and run them **single-threaded** (UI thread produces and consumes; quarantine=0). The seam exists as a data shape, not a thread. Land incremental layout, memo skip (correct 3-signal), the geometry cache, on-UI tessellation, the epoch chokepoint, `CleanSpanWitness` **with baked-geometry capture**, ChunkedArena. All CI gates green here.
2. **COM hardening folded in.** AbiHarvest + generators + `AbiVerify` (fail-safe) + the CI-RELEASE QI-roundtrip gate + generation-keyed `ComTracker`. This is independent of threading and retires the biggest new risk first.
3. **Validation spine non-negotiable.** Trust-ring fence, alloc-tripwire + BDN backstop, footprint ratchet, headless structural + perceptual goldens, epoch fault-injection (inject Bounds-move-without-PaintDirty AND Mutate-without-dirty). These must be green and non-suppressible before any thread is spawned.
4. **Spawn the render thread; keep quarantine=0 logically via single-consumer.** Move record/batch/submit/present to the render thread reading the published snapshot, ComPtr ownership migrated to it. Render-private ≥3 arenas. Retire-fence handshake for slot recycling. Run with the render thread but with no slot reuse in flight yet (force-sync drain).
5. **Flip quarantine 0 → derived depth, under the race gate.** Only after `seam.race` soak (channel-cap + reader-stall swept as fuzz params, ThreadGuard + consume-gated `QuarantineLedger` deterministic-throw, byte-identical-to-single-thread-golden) is green for the required nightly streak. Add the render-thread present-stall bench.
6. **Off-thread tessellation + glyph raster.** Only now (quarantine ≥2 proven) move the pure raster/tessellate stages off-thread with copy-isolated worker arenas + the deferred-free slab ring + per-worker AllocScope. Glyph raster requires the probe→raster→pack→upload re-architecture; budget it, do not assume the current GetOrAdd-at-batch shape.
7. **Off-thread reconcile (opt-in), Metal fallback debt.** Spike-gated, MANAGED, not default.

---

## 7. SPEC AMENDMENTS (checklist to make this the canonical spec)

- **architecture-spec §4.8 frame loop:** re-cut record/batch/submit/present onto the render thread; insert PUBLISH(13a) between phase 7 and 8; phase 12 reads present-ack. Replace the bare quarantine constant `2` with `QUARANTINE = RenderInFlightDepth` (compile-asserted, consume-gated reclaim). State DrawList arenas are render-thread-private and **≥3-deep** (supersedes the 2-deep design). Replace `Channel<SceneFrame>` DropOldest-as-data with: channel = cap-1 wakeup/coalesce signal; data = triple-buffer; **slot recycling = retire-fence handshake** (mirror the deferred-delete ring).
- **architecture-spec §5.4 scene/clean-span:** clean-span validity computed render-thread-LOCAL; witness captures **baked geometry hash** + (handle, gen, epoch); add `RealizationDirtySet` + the independent content-byte oracle; document the TransformDirty-only reuse exclusion (correctness via batcher + golden, not the witness).
- **architecture-spec §5.5/§5.6 layout/reconcile:** add the layout-boundary firewall + two-rule invalidation graph (O(change)); **keep the 3-signal memo skip** (`SelfTriggered || propsChanged || HasConsumedContextChanged`); resolve reconciler-hooks open-question #4 (SubtreeDirty is traversal scope, never a skip-decision input); time-sliced reconcile = atomic-on-complete default.
- **foundations §6 allocators:** supersede single-buffer ArenaAllocator with ChunkedArena + `IVirtualMemory` PAL seam (reserve-then-commit / segmented, no copy, native-backed); relax "grow only between passes" to "add-chunk mid-pass (addresses stable)"; **native high-water counter gates chunk growth, not the GC tripwire.**
- **gpu-renderer §5 tessellation:** delete ear-clipping; one monotone/trapezoidal O(n log n) sweep; separate complexity-bound (SAFE) from geometric-correctness (fuzz-gated + differential rasterizer). §11 AA: fringe→MSAA→D2D ladder gated by golden disposition table. Add `RenderLane` routing + tessellation-fraction tripwire. Add `OverlapGrid` painter-order (consult stored last-writer; derive break + sort from one record-seq). Scope PSO pre-warm to the native set; Custom/effect/D2D warmed lazily.
- **dotnet10 §GC/threading:** both-directions-volatile publish handshake; `System.Threading.Lock` only off-hot-path (device-lost rendezvous, backpressure timeout T = one vsync); epoch reads/writes become acquire/release the moment quarantine ≥2.
- **COM ruling (dotnet10 §4):** input is harvested `*.comabi.json`, not `[VtblIndex]`; `[GeneratedComInterface]`-only `PlatformIntegration`; ComPtr render-thread-confined + Move-only across the seam; `ComTracker` keyed by (pointer, generation); DWrite factory SHARED + shaping CCW thread-confined.
- **painpoints-assessment §6:** fold (a)-(g) in as the §6 build order above; mark §6b off-thread as **sequenced behind §6a**, not concurrent with the single-thread renderer.
- **risk register:** re-grade per §3 ledger; mark COM slot/convention SAFE, cross-thread COM lifetime as the dominant NEW residual, seam data-race as MANAGED-sampled.

---

## 8. THE HONEST BOTTOM LINE

**Is WinUI's slowness fully fixed?** For the common case, yes — and demonstrably (the stall-injection benches prove the decoupling in both states). The UI thread no longer records, batches, submits, or presents; a busy handler or a transient GPU hiccup no longer stalls the screen. **It is not "fully fixed" in the absolute sense:** a sustained GPU stall still couples back to the UI thread as a bounded per-frame block, because you cannot render-ahead forever — the same ultimate limit WinUI's compositor has. So: **decoupled and substantially faster, not invincible.**

**Is the engine safe?** It is *safe-by-construction* exactly where confinement and immutability make a hazard structurally impossible — generated COM slots, all-ComPtr-on-one-thread refcounts, the no-O(n²) tessellation bound, the no-LOH arena, brush/clip epochs degenerating to liveness, and confinement violations throwing deterministically in CI. It is *well-managed* — CI-gated, honestly labeled, not proven — everywhere else: the seam's one lock-free fence (no managed TSan; sampled, not proven), glyph-atlas epoch correctness (tooling-enforced), AA/gamma/text (corpus-gated against DWrite + Unicode conformance), COM refcount *lifetime* (post-hoc detection on a tiny surface). And there is one blunt truth: **production safety equals CI coverage** — every "deterministic throw" guard is `[Conditional]`-erased from the shipping AOT binary, so in the customer's hands those hazards are caught by the corpus the team maintained, not by the running app.

The decisive verdict: **a correct single-thread v1 is safer than the concurrent seam, and that is why we ship it first and flip quarantine only after the race gate is green with swept parameters.** Folded in this order, the hardened v1 is a genuinely fixed, genuinely faster engine whose residual risks are named, bounded, and gated — not a concurrent hand-rolled engine pretending to be risk-free.
