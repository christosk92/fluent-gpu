# Scroll & record performance fix plan

**Status: PLANNED — not implemented.** Produced 2026-07-09 from a code-level diagnosis of intermittent scroll FPS collapse (worst on the artist page), followed by a multi-agent design pass: one design per fix area (two competing designs + a judge for the span-reuse scoping), each adversarially reviewed against the actual source, then an integration pass for sequencing. Designs B and D were revised once after their reviews found blocking issues; the bodies below are the corrected versions. A, C, E passed review unchanged (minor issues are noted inside each part).

## 1. The problem — diagnosis recap

The engine's scroll fast path (DrawList **span reuse**: memcpy of last frame's recorded subtree bytes, exact or translated) has three **window-global kill switches** in `AppHost.RunFrame`, and the artist page trips all of them at once during a fling:

1. **Any component re-render** ⇒ `reconciled` (AppHost ~1428) ⇒ `SpanReuseDisabledReason.SceneChanged` **and** a layout pass (`layoutNeeded`, ~1432) ⇒ `.Layout` — both disable span reuse for the *entire window* that frame (~1605-1606).
2. The app feeds page scroll into the discography `LazyGrid`s through a 24px-quantized scroll observer + `Signal<float>` (`ArtistPage.cs:71`, `DiscographyPage.cs:124`); `LazyGrid.Render` subscribes to it (`LazyGrid.cs:112-114`) and rebuilds its realized card window — usually to an identical tree. At fling speed a 24px boundary is crossed **every frame**, so every frame reconciles ⇒ full-scene re-record + layout.
3. **Any in-flight image reveal crossfade** ⇒ `.ImageContent` (~1611-1613, one global boolean `ImageCache.HasActiveCrossfades`). Every cover mounts with a ~320ms fade, and scrolling continuously realizes new covers, so on a cold page span reuse is off *continuously*. The global flag is load-bearing today because the fade factor is **baked into the `DrawImage` command at record time** — a reused span would freeze the fade.
4. Full re-records are priced by the **whole page**: `SceneRecorder.Walk` recurses into every child unconditionally (no subtree cull, ~914-928; two span-signature hashes per node), and every artist-page shelf uses `PagedShelf(measured:true)` which realizes **all** cards — thousands of nodes.
5. `AutoEdgeFade = true` on the page ScrollView (`ArtistPage.cs:69`) makes the **full viewport** a `PushEdgeFadeLayer` offscreen RT (clear + redirect + composite) every frame there is overflow — i.e. always — with the hero's 260px fade and per-shelf fades nested inside it. Same pattern on DetailTracks / DiscographyPage / EpisodeList / LibraryPage / NowPlayingPanel / LyricsView / sidebar.

Smooth case: warm cache + slow scroll ⇒ span rebase fast path. Tank case: fling on a not-fully-cached artist ⇒ every frame pays reconcile + layout + full-scene re-record + decode/upload + crossfade repaints + the whole-viewport layer + GC from per-frame element-tree rebuilds.

## 2. The fixes (one line each)

| Area | Fix | Size | Review |
|------|-----|------|--------|
| **A** | LazyGrid re-renders only when its *(first,last) row window* changes (~per 250px, not per 24px): value-gated `Signal<long>` window key fed by a bridge effect; `Render` stops subscribing to the raw offset | S-M | sound |
| **D** | Delete `AutoEdgeFade` on the 5 page scrollers sitting on the opaque `FileArea` plate (free surface-colour `EmitScrollEdgeCues` instead); scissor the remaining true-alpha fade layers' clears to the composite clip **only when `BlurSigma == 0`** | S-M | revised → sound |
| **E** | Recorder subtree cull (skip clean subtrees provably outside the clip, via prior-frame `SubtreeBounds` + pure-translation delta) + measured-shelf virtualization above a realize-all cap + app-side shelf count caps | L | sound |
| **B** | Remove the global `SceneChanged`/`Layout`/`ImageContent` kills: per-node record-dirty carries invalidation; fold presented size (pw/ph) into the span signatures; per-frame fade-mark pass over the reconciler's **existing** `_imageNodes` registry; `FG_SPAN_SHADOW` byte-identity harness before flip | M | revised → sound |
| **C** | Phase 1 merges into B (same image-axis work). Phase 2: lift the fade factor out of the baked `DrawImageCmd` (`FadeStartMs/DurationMs/Easing`, resolved at replay against a clock uniform) so mid-fade spans are *reused*; mandatory `&& !HasActiveCrossfades` skip-submit guard | M-L | sound |

## 3. Landing order

1. Phase 0 — Instrumentation & baseline (no behavior change): wire the FrameStats/SceneRecordStats CSV capture + per-reason histogram and the shared synthetic-artist scene harness; capture the 4-scenario baseline.
2. Phase 1 — A: LazyGrid window-gate (app + FluentGpu.Controls only). Coarsen page-scroll re-render from per-24px to per-row.
3. Phase 2 — D: Edge-fade downgrade on the 5 opaque-plate page scrollers + blur-safe scissored clear (app + small FluentGpu.Windows/Engine helper). GPU-side.
4. Phase 3 — E: Recorder subtree cull (SceneRecorder + SpanTable) + measured-shelf virtualization (PagedShelf) + app-side shelf count caps. Bounds re-record cost to the viewport and cuts node count while the global kills still fire.
5. Phase 4 — B + C-Phase1 (merged): remove the global SceneChanged/Layout/ImageContent kills behind FG_SPAN_SCOPE, add the presented-size (pw/ph) signature fold, build the ONE unified image-fade mark pass + SuppressReveals, and the FG_SPAN_SHADOW byte-identity harness. The big engine change, behind a multi-screen shadow soak.
6. Phase 5 — C-Phase2: replay-resolved reveal factor (DrawImageCmd opcode change + D3D12/headless replay against a clock uniform + the mandatory `&& !HasActiveCrossfades` skip-submit companion). Retires Phase 4's fade-mark pass; fades cost zero re-record. Canon reconcile + check-canon.ps1.

## 4. Phases

### Phase 0 — Instrumentation & baseline

**Contains:** No behavior change. Plumb a DEBUG per-frame CSV capture off the EXISTING counters: FrameStats segment ms (Flush/reconcile, layout full-vs-scoped, record, submit/present — the CPU-vs-GPU split), SpanReuseDisabledReasons as a per-bit frame-fraction histogram, SpansReused/SpansRebased/SpansReRecorded/SpanBytesCopied, NodesVisited, EdgeFadeGroupCount, plus effective FPS / dropped-frame count / p95+p99 frame ms. Build the shared synthetic-artist-like headless scene (LazyGrid + PagedShelf measured shelves + fading covers + AutoEdgeFade over an opaque plate) that phase-0 and every cross-fix gate reuse. Capture 4 scenarios: artist COLD (uncached covers = continuous decode+fade) vs WARM (cached), and FLING (fast) vs SLOW-DRAG (sub-row); secondary discography + detail runs.

**Exit criteria:** A reproducible CSV per scenario exists and the CPU-vs-GPU split is attributed: confirm on artist-cold-fling that ~100% of frames carry SceneChanged AND ImageContent, EdgeFadeGroupCount>=1 every frame, NodesVisited ~= full page node count, SpansReused ~= 0. This 'before' is the denominator each later phase divides down, and the CPU(record/reconcile/layout ms) vs GPU(present ms) ratio decides whether the A-first order holds or D must be front-loaded.

### Phase 1 — A: LazyGrid window-gate

**Contains:** LazyGrid.cs: add Signal<long> _win + static PackKey(View); a UseSignalEffect (placed BEFORE the count==0 early return, structural reads wrapped in Reactive.Untrack) that subscribes to the ancestor offset only and value-gate-writes the packed (first,last,drawerVisible) row-window key; Render reads scrollSig.Peek() (no longer .Value) and subscribes _win.Value. Comment-only edits on ArtistPage.cs/DiscographyPage.cs (the /24f projection is now a write-throttle floor, not the windowing quantum). DEBUG overscan>=1 assert + WAVEE_LAZYGRID_EAGER bisect flag. No engine/data/scene change.

**Exit criteria:** On sub-row (slow-drag) frames, ConsumeReconciled()==false and reconcile ms ~= 0; a row-boundary step yields exactly one reconcile. On artist/discography fling the SceneChanged frame-fraction drops ~10x (from ~1.0 to ~1/rows-crossed). Effective FPS improves iff Phase-0 flagged CPU-reconcile-bound. Bridge effect GC-alloc delta == 0. Note: ImageContent + edge-fade kills still fire every fling frame — A alone does not clean the frame.

### Phase 2 — D: Edge-fade downgrade + blur-safe scissored clear

**Contains:** App: delete AutoEdgeFade=true on the 5 opaque-FileArea page scrollers (ArtistPage:69, DetailTracks:597, DiscographyPage:123, EpisodeList:74, LibraryPage:366) so they fall to the free surface-colour EmitScrollEdgeCues gradient; NowPlayingPanel deliberately NOT downgraded (rail plate may be translucent); Lyrics/Sidebar/hero/shelf fades kept true-alpha. Engine: new TerraFX-free EdgeFadeLayerClear.Compute (full-canvas clear when BlurSigma>0, else CompositeClip*scale box clamped to canvas — a superset of the composite read); optional D3D12_RECT* clearRect on OpacityLayerCompositor.Acquire; D3D12Device PushLayer{EdgeFade} branch calls Compute + stackalloc'd rect + edgeFadeClearPx Diag counter. Reconcile controls.md 8.3 + gpu-renderer.md; check-canon.ps1.

**Exit criteria:** For the 5 downgraded pages: EdgeFadeGroupCount==0, LastLayers has no Kind==EdgeFade, two surface-colour GradientRect cues present with resolved plate colour (alpha>=0.985 asserted). For kept fades: edgeFadeClearPx ~= composite-clip area, never canvas area; a Mode=FadeAndBlur fade keeps fullCanvas==true (blur regression guard). GPU present ms drops by the whole-window RT clear+composite measured in Phase 0. FPS improves iff Phase-0 flagged GPU-present-bound. Independent of A/B/E.

### Phase 3 — E: Subtree cull + measured-shelf virtualization

**Contains:** SceneRecorder.Walk: off-screen clean-subtree cull early-out (gated recordDirtyBits==0 && descendantDirtyBits==0 && pure-translation world delta && prior device SubtreeBounds outside clip), placed adjacent to B's future sig call sites; runs under ANY spanReuseDisabled reason. SpanTable: _culled column, StoreCulled (chains frame-to-frame), TryGetSubtree, and !_culled guard on TryGet/TryGetTranslated. NodesCulled stat threaded to SceneRecordStats/FrameStats. PagedShelf: MeasuredVirtualBody above MeasuredRealizeAllCap (Visible=false zero-extent probe measures a bounded sample, locks height, drives the existing VirtualBody/FillRowVirtualLayout). ArtistPage.Shelves.cs: Math.Min count caps on the cosmetic shelves. Reconcile gpu-renderer.md + SPEC-INDEX 2 sub-clause + virtualization.md/controls.md; check-canon.ps1.

**Exit criteria:** On artist fling (globals STILL firing): NodesVisited drops from ~page node count to ~viewport, NodesCulled>0 every off-screen frame, SpansReRecorded + SpanBytesCopied fall, on-screen DrawList byte-identical to the EnableSubtreeCull=false baseline (cull is byte-neutral). Measured-virtual shelves realize ~one window (bounded node count, not thousands); pager glide + short-shelf realize-all unchanged. Hot-phase alloc == 0. Dirty/scale-escape/scroll-in-recovers safety gates green.

### Phase 4 — B (+ C-Phase1 merged): span-reuse scoping + unified image-fade pass

**Contains:** AppHost 1604-1620 restructured ONCE behind FG_SPAN_SCOPE (=0 restores the legacy 3-OR global kill): remove the SceneChanged/Layout/ImageContent global disables; on imageContentChanged frames call ONE image-fade mark pass. Build the shared infra ONCE: an ImageCache active-fade id set (append in NoteCrossfadeDeadline, prune on CrossFadeOf>=1) + a single record-content-only marker (C's Reconciler.MarkImageFadeDirty over the EXISTING _imageNodes registry -> SceneStore.MarkRecordContentDirty) driven by a cached delegate; keep the existing MarkImageDirty(PaintDirty) wired at AppHost:1006 for genuine Ready/Failed flips. SceneRecorder: fold pw/ph into ComputeSpanInputSig + ComputeSpanMoveSig (closes the W/H-stretch gap while position-only reflow still translate-copies). Add C's SuppressReveals (scroll-skip + mount-without-fade). DEBUG FG_SPAN_SHADOW record-twice byte-identity harness (authoritative pass uses spans:null so it never touches the live SpanTable, and forces a full walk that E's cull is inert for). Reconcile gpu-renderer.md/media-pipeline.md/layout.md; check-canon.ps1.

**Exit criteria:** On artist fling: SpanReuseDisabledReasons carries NONE of {SceneChanged, Layout, ImageContent} on non-boundary/fade frames; SpansReused+SpansRebased rise to ~clean-subtree count; record ms on reconcile/fade frames drops toward the reused floor. FG_SPAN_SHADOW byte-identity assert SILENT across artist/discography/detail/now-playing/lyrics/gallery (incl. bound-source stationary fade + reflow-shift battery) WITH E's cull enabled. Fades animate (crossFade advances). Phase-6-13 zero-alloc tripwire green on spanReuseDisabled=None reconcile/fade frames. Skip-submit unaffected (fade byte changes -> dlHash differs -> submits).

### Phase 5 — C-Phase2: replay-resolved reveal factor

**Contains:** DrawImageCmd: replace float CrossFade with FadeStartMs/FadeDurationMs/FadeEasing (~8B growth). SceneRecorder image case (867-885) + RecordDetached (260-275) bake the params via ImageCache.FadeParamsOf. D3D12Device.RecordAll/AddReadyImage + headless RHI resolve CrossFade = Ease(kind, saturate((imageClockMs-start)/dur)) against a per-submit scalar clock (single shared static Ease used both sides). MANDATORY companion: `&& !_images.HasActiveCrossfades` on AppHost:1642 maybeUnchanged (else the now byte-identical fade frame is elided and the fade freezes). Retire Phase 4's per-frame fade-mark pass + SuppressReveals fade-skip (fades now reuse via replay); keep MarkImageDirty for status flips. Behind FG_IMAGE_FADE_GPU soak. Register the opcode shape change under gpu-renderer.md ownership + SPEC-INDEX 2 + media-pipeline.md; check-canon.ps1 exit 0.

**Exit criteria:** A mid-fade cover's span is REUSED (SpansReRecorded attributable to fading covers == 0) while the resolved factor advances monotonically 0->1; skip-submit fires only on truly byte-identical frames and never while HasActiveCrossfades; DrawImageCmd with the new fields still translates under CopySpanFromPriorTranslated; struct-size + headless replay-resolve gates green; check-canon.ps1 exit 0; --screenshot shows fades animating with reuse active.

## 5. Cross-fix interactions (build-once / conflict map)

- SUBSUMPTION (the biggest one): B removes ALL three global kills (SceneChanged+Layout+ImageContent); C-Phase1 removes only ImageContent. Removing the global ImageContent kill REQUIRES a per-node fade mark pass or fades freeze under a clean ancestor — that pass IS C-Phase1's mechanism. So C-Phase1 is not a separate phase; it is the image half of B. Merge them into Phase 4 and build the image-fade dirty pass exactly once. C-Phase2 (replay-resolved) stays a distinct later phase.
- SHARED INFRA to build once (verified there is NO existing per-frame fade list in ImageCache): both B (_activeReveals) and C (_fading) add a per-frame active-fade id list + a mark driver to ImageCache. Build ONE list. Use B's backbone insight (reuse the reconciler's EXISTING _imageNodes registry + the already-wired ImageStatusChanged->MarkImageDirty for Ready/Failed) but C's mark path for the per-frame fade ADVANCE: a record-content-only marker (Reconciler.MarkImageFadeDirty -> SceneStore.MarkRecordContentDirty), because a fade advance is a re-record, not a sticky PaintDirty that perturbs damage/skip logic. Verified: MarkImageDirty at Reconciler.cs:1037 sets PaintDirty; SceneStore.MarkRecordDirty(idx) already defaults to RecordDirtyContent (855), so the wrapper is trivial. Do NOT build a new SceneStore image registry (the resubmitted B design already discarded that).
- FILE CONFLICT — AppHost 1604-1620: B rewrites the whole spanDisable block (gates all three globals behind FG_SPAN_SCOPE + drives the fade pass); C deletes the ImageContent terms + adds the fade pass + SuppressReveals. Same lines. Author ONCE in Phase 4 as B's restructure; C's image specifics fold into B's scoped-image branch. E's NodesCulled FrameStats plumbing is additive elsewhere in the same method (no collision).
- FILE CONFLICT — SceneRecorder.Walk: B adds pw/ph to the sig call sites (449-454) and C-Phase2 rewrites the image case (867-885), while E inserts the cull early-out at ~455-461 immediately after the sigs. Serialize the Walk edits: E lands first (Phase 3), B's sig fold rebases onto E's early-out (Phase 4), C-Phase2's image-case bake lands last (Phase 5). SpanTable is touched by E only (_culled column + guards) — B/C do not touch it.
- FILE CONFLICT — ImageCache.cs: B and C both add fade machinery; merged in Phase 4 (see shared-infra). C-Phase2 additionally adds ClockMs + FadeParamsOf in Phase 5. Reconciler.cs: B needs no change (reuses existing); C adds MarkImageFadeDirty — that one addition is the unified marker kept in Phase 4.
- FREQUENCY vs COST (A vs B): A coarsens LazyGrid re-render ~10x, cutting the FREQUENCY of SceneChanged-armed frames; B scopes the per-frame COST per-node. Complementary. A landing first (Phase 1) reduces B's urgency for the artist fling AND removes a confounding churn source from B's measurement, reinforcing A->B order. BUT A does not touch the ImageContent kill, which fires every fling frame through uncached covers — so the Phase-4 image scoping remains the single most load-bearing CPU fix for the artist fling even after A.
- E WORKS UNDER THE GLOBAL DISABLE (why E precedes B): verified E's cull is gated on recordDirtyBits==0/descendantDirtyBits==0, NOT on spanReuseDisabled, and lives inside `if (spans is not null)` — so it fires even when the global ImageContent/SceneChanged kill is set. E therefore bounds every kill-switch frame to the viewport BEFORE B removes the kills. This makes B less urgent (the kill still fires but costs ~viewport) and is the reason the order is A->D->E->B rather than B early.
- E CHANGES WHAT B MUST PROVE (byte-neutrality): E's cull is byte-neutral by construction — an off-screen subtree emits no DrawList bytes even in a full walk (each node's own overlapsClip is false), so the cull drops only NodesVisited, not emitted bytes. B's FG_SPAN_SHADOW authoritative pass uses spans:null (E's cull is inert there, full walk) and compares bytes/sortkeys against the scoped candidate (E's cull active). Because both emit identical on-screen bytes, byte-identity holds — REQUIREMENT: B's shadow soak MUST run with E's cull enabled, plus a dedicated combined gate. E's own offscreen gate independently proves byte-neutrality vs the cull-off baseline.
- E's !_culled GUARD vs B's REUSE: E adds !_culled to TryGet/TryGetTranslated so a culled prior (no valid bytes) is never memcpy'd on scroll-in. This guard is dormant until B makes reuse reachable on reconcile/fade frames, then becomes load-bearing for scroll-in recovery (first on-screen frame re-records fresh bytes). Land E's guard in Phase 3; B's Phase 4 reuse relies on it.
- C-Phase2 RETIRES Phase 4's fade pass: C-Phase2 lifts the fade factor out of the baked DrawImageCmd so a reused/translated span animates via a replay clock -> the per-frame fade mark pass built in Phase 4 becomes unnecessary and is DELETED (only Ready/Failed MarkImageDirty stays). Phase 5 must RETIRE the fade set/marker, never add a second one — enforce with a code-review gate.
- SKIP-SUBMIT CHAIN: Phase 4 (B) needs NO skip-submit change — a fade advances the crossFade byte in DrawImage, so dlHash differs (verified maybeUnchanged at AppHost:1642) and the frame submits. Phase 5 (C-Phase2) makes the fade byte constant -> byte-identical frame -> skip-submit would freeze the fade -> the `&& !HasActiveCrossfades` guard on maybeUnchanged is MANDATORY and is the single most important Phase-5 companion edit.
- D IS GPU-SIDE, INDEPENDENT: D removes the whole-window offscreen-RT clear/redirect/composite (a GPU present cost) + trims a Push/Pop opcode pair from record (tiny CPU win) + un-nests hero/shelf fades; it does not touch the reuse/reconcile/cull machinery. Only file overlap: ArtistPage.cs / DiscographyPage.cs where A adds a comment near :71 and D deletes AutoEdgeFade at :69 — trivial merge (do A's comment edit and D's deletion in the same touch). Because D is GPU and A/B/E are CPU, Phase 0 MUST separate record/reconcile/layout ms from present ms or D's GPU win and the CPU wins are indistinguishable in raw FPS.
- COUNTER PLUMBING is shared: E adds NodesCulled, D adds edgeFadeClearPx, B/C consume the existing SpanReuseDisabledReasons/SpansReused/etc. All extend SceneRecordStats/FrameStats. Wire the capture surface in Phase 0 and have each phase add its own counter — one coherent stats path, not four.
- RENDER-THREAD SEAM: all five stay compatible — the image-fade marks (Phase 4) and A's bridge effect run UI-side pre-record (phases 3-7), E's cull reads scene+SpanTable and writes DrawList/SpanTable on the record thread it already owns, D's clearRect is a stackalloc POD, and C-Phase2's imageClockMs is a scalar value input (render thread never touches ImageCache). Only caveat: C-Phase2's imageClockMs snapshot point depends on the async frame-packet shape in render-thread-seam-landing-plan.md — confirm before enabling Phase 5 under FG_RENDER_ASYNC; keep Phase 5 sync-correct first.

## 6. Measurement plan

BASELINE (Phase 0), before any fix, over a scripted deterministic run on 4 scenarios chosen to separate the diagnosis's variables: artist COLD (uncached covers = continuous decode+fade, exercises ImageContent) vs WARM (cached, no fades); FLING (fast, the page-scroll observer fires every frame) vs SLOW-DRAG (sub-row, observer quantized). Secondary: discography (same LazyGrid/edge pattern) and detail (bucketed cousin). Log per frame off the EXISTING counters: FrameStats segment ms (Flush/reconcile, layout full-vs-scoped, record, submit/present — this is the mandatory CPU-vs-GPU split, since D wins on present ms while A/B/E win on record/reconcile/layout ms); SpanReuseDisabledReasons as a per-bit frame-fraction histogram; SpansReused/SpansRebased/SpansReRecorded/SpanBytesCopied; NodesVisited; EdgeFadeGroupCount; effective FPS + dropped-frame count + p95/p99 frame ms. Expected 'before' on artist-cold-fling: SceneChanged ~= 1.0 and ImageContent ~= 1.0 frame-fraction, EdgeFadeGroupCount>=1 every frame, NodesVisited ~= full page node count, SpansReused ~= 0. The Phase-0 CPU:GPU present-ms ratio is a GO/NO-GO input to the landing order (if GPU-present-bound, front-load D). PER-PHASE PROOF: A -> on slow-drag the SceneChanged frame-fraction falls from ~1.0 to ~1/rows-crossed (~10x), reconcile ms ~=0 on non-boundary frames, ConsumeReconciled false on sub-row steps; FPS lift iff CPU-reconcile-bound. D -> EdgeFadeGroupCount==0 and edgeFadeClearPx==0 on the 5 downgraded pages (kept fades: edgeFadeClearPx ~= composite-clip area, never canvas), no Kind==EdgeFade layer, GPU present ms drops by the Phase-0 whole-window-RT clear+composite cost; FPS lift iff GPU-present-bound. E -> NodesVisited drops from ~page count to ~viewport, NodesCulled>0 every off-screen frame, SpansReRecorded+SpanBytesCopied fall EVEN while the globals still fire, measured-virtual shelves realize ~one window; record ms on kill frames bounded to viewport. B -> SpanReuseDisabledReasons carries none of {SceneChanged,Layout,ImageContent} on non-boundary/fade frames, SpansReused+SpansRebased rise to ~clean-subtree count, record ms on reconcile/fade frames drops toward the reused floor, FG_SPAN_SHADOW assert silent across all screens (with E on). C-Phase2 -> SpansReRecorded attributable to fading covers == 0, fades animate via the replay clock, skip-submit never fires while HasActiveCrossfades. Because each phase shifts the next phase's baseline (A lowers the SceneChanged fraction B sees), RE-BASELINE all 4 scenarios after every phase and attribute wins by per-reason bucket, not raw FPS. REGRESSION TRIPWIRES left permanently: the phase-6-13 zero-alloc tripwire extended to the scoped-reuse + cull + fade-mark paths; a permanent headless gate.integration.artist-fling-clean asserting no SceneChanged on non-boundary frames (guards A), no ImageContent on fade frames (guards B), EdgeFadeGroupCount==0 for downgraded pages (guards D), NodesCulled>0 (guards E), zero full-record frames + SpansReused>N (capstone); FrameStats/SceneRecordStats kept wired for on-device telemetry; FG_SPAN_SHADOW retained as a DEBUG opt-in bisect.

## 7. Combined gate list

- `gate.lazygrid.window-gate-coarsens-reconcile`: sub-row 24px steps -> ConsumeReconciled()==false; a row-boundary step -> exactly one reconcile (models the phase-7 observer -> next-frame Flush, i.e. two frames per step).
- `gate.lazygrid.window-gate-alloc-zero`: 30 sub-row offset writes drive the bridge effect with GC-alloc delta == 0.
- `gate.lazygrid.window-identical-output`: two offsets in the same row band build a structurally identical subtree (equal realized row count + equal lazy-top/lazy-bottom spacer heights).
- `gate.lazygrid.paging-keeps-ahead`: advancing the window one row calls ensureRange reaching >= overscan rows past the viewport before those rows are visible.
- `gate.lazygrid.expand-rewindows`: writing _expanded with no scroll tick re-renders and places the drawer row in the realized window.
- `gate.lazygrid.overscan-absorbs-24px-floor`: with a 24px-stale offset at a boundary, every visible-viewport row is realized (requires overscanRows>=1 at all call sites).
- `gate.edgefade.downgrade`: overflowing ScrollEl with AutoEdgeFade=false/EdgeCues=Fade over an opaque ancestor -> EdgeFadeGroupCount==0, no Kind==EdgeFade layer, two surface-colour GradientRect cues present with resolved plate colour (assert alpha>=0.985).
- `gate.edgefade.explicit-kept`: the same scroller with AutoEdgeFade=true (Mode=Fade) still emits exactly one Kind==EdgeFade PushLayerCmd with bands matching overflow.
- `gate.edgefade.clear-fade-bounded`: EdgeFadeLayerClear.Compute on a captured Mode=Fade layer returns fullCanvas==false and the CompositeClip*scale box clamped to canvas, a strict sub-rect of the full canvas.
- `gate.edgefade.clear-blur-fullcanvas`: Compute on a BlurSigma>0 (Mode=FadeAndBlur) layer returns fullCanvas==true (the blur-halo regression guard).
- `gate.edgefade.alloc`: recording a downgraded overflowing page twice keeps phase-8 GC-alloc delta at 0.
- `gate.edgefade.clear-scissor-diag (windowed/--screenshot)`: Diag edgeFadeClearPx ~= composite-clip area (not canvas) for a small Mode=Fade sidebar fade (end-to-end plumbing).
- `gate.recorder.subtree-cull-offscreen`: off-screen clean band skipped (NodesCulled>0, NodesVisited drops) EVEN under forced spanReuseDisabled; on-screen DrawList byte-identical to the EnableSubtreeCull=false baseline; 0 hot-phase alloc.
- `gate.recorder.subtree-cull-safety-dirty`: a dirty descendant (fill mutation AND a mount/unmount) inside an off-screen-bounds subtree is NOT culled and pixels match the full walk.
- `gate.recorder.subtree-cull-scale-escape`: a scale-animated subtree growing into the clip fails TryTranslationDelta -> not culled -> drawn.
- `gate.recorder.subtree-cull-scroll-in-recovers`: a culled subtree scrolled back on-screen fully re-records (culled prior rejects byte reuse) with correct pixels.
- `gate.recorder.subtree-cull-firstframe-fallback`: no prior span -> no cull, correct full output.
- `gate.shelf.measured-virtual-bounded`: PagedShelf(measured:true) with 500 items realizes ~one window (bounded node count); viewport height == probe-measured card height matching realize-all within tolerance.
- `gate.shelf.measured-virtual-pager-glide`: chevron-next arms StartBringItemIntoView, realizes the next window, 0 hot-phase alloc on the glide frame.
- `gate.shelf.measured-realizeall-smallcount`: count<=MeasuredRealizeAllCap keeps exact realize-all measured height.
- `gate.spanscope.byte_identity`: scoped record (globals off, only subtree A mutated) is bit-identical (Bytes AND SortKeys) to a spanReuseDisabled=all forced-full record.
- `gate.spanscope.sibling_reuse_on_paint`: mutating one node in subtree A -> SpansReused>0 for clean subtree B, its descendants NOT in NodesVisited (early-return cull).
- `gate.spanscope.layout_stretch_rerecords`: a flex child stretched (only container LayoutDirty) re-records at the NEW width via the pw/ph fold; an unchanged-bounds sibling is reused; stream byte-identical to a forced-full record.
- `gate.spanscope.reflow_shift_translates (discriminator)`: a position-only shifted node lands in SpansRebased (translated, copied Dy == dy), NOT SpansReRecorded — proves the size fold preserves the translated path; plus a partial-clip variant that re-records.
- `gate.spanscope.reorder_painter_order`: reordered same-depth children -> both SpansReused, parent re-recorded, byte-stream order swapped to match a forced-full record.
- `gate.image.fade_stationary_scoped`: an UNBOUND mid-fade image under a clean stationary ancestor re-records every fade frame (crossFade advances to 1) while a sibling stays SpansReused; settled cover joins SpansReused byte-identical (subsumes C's no-global-disable + marks-only-drawing-node).
- `gate.image.fade_bound_source_scoped (discriminator)`: the SAME but with a BOUND Source fed by a signal (ImageId landing through the bind effect + PinImageNode) — the bound cover must re-record every fade frame and NOT freeze.
- `gate.image.ready_flip`: a Pending->Ready flip re-records only the drawing node (existing ImageStatusChanged->MarkImageDirty) and a far sibling reuses; SpanReuseDisabledReasons==None (subsumes C's ready-marks-only-node).
- `gate.image.stagger_independent_settle`: two covers finishing at t1<t2 under one clean ancestor settle independently (A reused at t1 while B re-records to t2).
- `gate.image.scroll-holds-reveal`: with SuppressReveals set, a completing decode yields CrossFadeOf==1 instantly, the active-fade set stays empty, and scrolled content still takes the translated-copy path (SpansRebased>=1).
- `gate.spanscope.clean_window_reconcile_cull`: dirtying ONE far leaf re-records its ancestor chain; a far clean subtree exact-copies via early-return; total NodesVisited far below node count.
- `gate.spanscope.zero_alloc_scoped`: GC-alloc delta == 0 across a frame that reconciles one node AND runs the image-fade mark pass (bound+unbound) AND records with scoped reuse active (extends the phase-6-13 tripwire, folds in C's fade-dirty-zero-alloc).
- `gate.spanscope.shadow_byte_identity`: with FG_SPAN_SHADOW AND E's cull enabled, a mutation battery (paint, W/H stretch, X/Y reflow, reorder, insert, remove, hover step, theme step, sticky-pin flip, stationary unbound fade, stationary bound-source fade, ready-flip) reports zero Bytes/SortKeys mismatch (authoritative pass uses spans:null). The flip-authorization + cull-byte-neutrality gate.
- `gate.img.fade-replay-resolves (Phase 5)`: resolving a DrawImageCmd at two imageClockMs values yields a monotonic 0->1 CrossFade with exact endpoints.
- `gate.img.fade-span-reused-while-fading (Phase 5)`: a mid-fade image's span is reused (SpansReRecorded==0) yet the resolved factor advances, and skip-submit is defeated while HasActiveCrossfades.
- `gate.img.drawimage-translate (Phase 5)`: DrawImageCmd with the new fade fields still translates under CopySpanFromPriorTranslated + a struct-size assertion.
- `CROSS-FIX gate.integration.artist-fling-clean (capstone, A+D+E+B on)`: scroll the synthetic artist page at fling while covers fade — over a window of frames assert SpanReuseDisabledReasons carries none of {SceneChanged,Layout,ImageContent} on non-boundary frames, SpansReused>N, NodesCulled>0, NodesVisited ~= viewport (<< page node count), EdgeFadeGroupCount==0 (downgraded page), ZERO full-record frames, fades animate, hot-phase alloc==0, and the on-screen DrawList is byte-identical to a forced-full (spans:null, cull-off) record.
- `CROSS-FIX gate.integration.fade-free-record (Phase 5 capstone, +C-Phase2)`: the same synthetic fling asserts SpansReRecorded attributable to fading covers == 0 (reused while fading), fades still animate via the replay clock, and skip-submit fires only on byte-identical frames and never while HasActiveCrossfades.

## 8. Risks & checkpoints

- Phase-0 CPU/GPU split could show one axis dominates (e.g. pure GPU present on the artist page at 4K). CHECKPOINT: gate the committed A-first order on the Phase-0 split; front-load D if GPU-present-bound. Do not skip Phase 0 — raw FPS alone cannot attribute the win between the GPU-side (D) and CPU-side (A/B/E) fixes.
- B's shadow harness could trip on a write-site that mutates a record-read column WITHOUT a Mark (stale-span reuse = a frozen region) on a screen not in the synthetic harness. Highest-risk item. CHECKPOINT: a multi-screen FG_SPAN_SHADOW soak (artist/discography/detail/now-playing/lyrics/gallery, incl. bound-source stationary-fade + reflow battery) must be SILENT before flipping FG_SPAN_SCOPE default-on; FG_SPAN_SCOPE=0 is an instant one-line revert.
- B and C's ImageCache fade lists diverge into two mechanisms if landed by different hands (they were designed independently, each adding a per-frame fade list + mark method). CHECKPOINT: Phase 4 builds exactly ONE fade set + ONE record-content-only marker over the existing _imageNodes registry; a code-review gate confirms Phase 5 only RETIRES it and never adds a second.
- E's cull byte-neutrality could break if a composited effect (shadow/edge-fade halo) appears whose device SubtreeBounds is not captured, dropping a real sliver. CHECKPOINT: E's offscreen + safety-dirty gates + the combined shadow-with-E-on gate; note the equivalence that the per-node overlapsClip already drops the same box-gated sliver today, so this is not new breakage.
- A's per-row gate silently mis-windows if any LazyGrid call site has overscanRows<1 (the 24px projection floor makes the gated offset up to 24px stale). CHECKPOINT: DEBUG overscan>=1 assert + A's overscan-absorbs-24px-floor gate; audit every LazyGrid call site.
- A's UseSignalEffect placed AFTER the count==0 early return desyncs the hook cursor (a crash). And if a structural read inside it stays tracked, the effect double-fires. CHECKPOINT: place the effect before the early return and wrap structural reads in Reactive.Untrack; A's coarsen + alloc gates.
- D's downgraded plate could be themed translucent -> the surface-colour cue self-skips -> MISSING edge affordance (never wrong-colour). CHECKPOINT: assert resolved cue-surface alpha>=0.985 in the downgrade gate; verify WaveeColors.FileArea; per-surface one-line revert; NowPlayingPanel deliberately excluded pending a confirmed-opaque rail plate.
- C-Phase2's DrawImageCmd opcode change breaks canon/translate/skip-submit; the `&& !HasActiveCrossfades` skip-submit companion is easy to miss -> frozen fade. CHECKPOINT: land behind FG_IMAGE_FADE_GPU soak; gate.img.fade-span-reused-while-fading + drawimage-translate + a shared static Ease used both recorder- and replay-side; check-canon.ps1 exit 0.
- E's measured-shelf virtualization changes scroll-restore / paging / initial-index behavior on the artist shelves. CHECKPOINT: E's measured-virtual-bounded/pager-glide/realizeall-smallcount gates + an on-device shelf paging + scroll-restore check; the MeasuredRealizeAllCap=int.MaxValue revert restores realize-all.
- C-Phase2's async imageClockMs snapshot depends on the frame-packet shape in render-thread-seam-landing-plan.md, which is not yet fully landed. CHECKPOINT: confirm the packet snapshot point before enabling Phase 5 under FG_RENDER_ASYNC; keep Phase 5 sync-correct first (the clock is a value input, render thread never touches ImageCache).
- Serialized edits to the same hot regions (AppHost 1604-1620, SceneRecorder.Walk 449-461, ImageCache) mean a merge/rebase error could silently reintroduce a global kill or drop the !_culled guard. CHECKPOINT: after each phase, re-run gate.integration.artist-fling-clean and diff SpanReuseDisabledReasons against the prior phase's expected histogram.
- Adversarial reviews returned sound=false for B and D on their PRE-revision designs (B's WriteColumns bound-source hook; D's blur-mode clear-scissor). The resubmitted design_md for each adopts the fix (B reuses _imageNodes; D gates the scissored clear on BlurSigma==0), but an implementer working from the wrong artifact would reintroduce the blocking bug. CHECKPOINT: the bound-source discriminator gate (gate.image.fade_bound_source_scoped) and the blur-fullcanvas gate (gate.edgefade.clear-blur-fullcanvas) are the two must-pass guards that catch exactly those regressions.

## 9. Integration notes (source-verified)

Verified against source before planning: AppHost's three global kills at 1604-1620 and skip-submit maybeUnchanged at 1642-1646; SceneRecorder pw/ph at 431-432 absent from the sigs at 449-454, exact-copy(461)/translated(478) both gated on !spanReuseDisabled, crossFade baked at 871/265; Reconciler _imageNodes(120) + self-compacting MarkImageDirty using PaintDirty(1037-1051) already wired to ImageStatusChanged at 1006-1008; SceneStore.MarkRecordDirty defaults to RecordDirtyContent(855) so C's record-content-only wrapper is a one-liner; ImageCache HasActiveCrossfades(296)/NoteCrossfadeDeadline(300)/CrossFadeOf(287) with NO existing per-frame fade list. Two structural decisions drive the plan: (1) B and C-Phase1 are the same work on the image axis and must be one phase with one shared fade mechanism (reconciler backbone + record-content marker); (2) E must precede B because E's cull runs under the global disable and de-risks/de-urgentizes B, and B's shadow soak must then run with E enabled to prove cull byte-neutrality. Every phase gates behind a DEBUG-only diagnostic/rollback flag (WAVEE_LAZYGRID_EAGER, FG_SPAN_SCOPE, FG_SPAN_SHADOW, FG_IMAGE_FADE_GPU, EnableSubtreeCull const, MeasuredRealizeAllCap) — none is a user-facing quality knob, consistent with the one-experience rule. Canon owners to reconcile before merge: gpu-renderer.md (recorder/DrawList/opcode + cull sub-clause), media-pipeline.md (reveal fade), controls.md 8.3 (AutoEdgeFade/EdgeCue affordance), virtualization.md (measured-virtual + deferred LazySection), layout.md (pw/ph in the sig), SPEC-INDEX 2 (per-node record-dirty refinement + DrawImageCmd shape); run check-canon.ps1 after each doc touch.

---

# Detailed designs

Each part below is the full, self-contained design as produced and adversarially reviewed. Where a review found blocking issues (B, D) the body is the **revised** design that adopts the fix; the review outcome is summarized at the top of each part.

---

# Part A — LazyGrid window-gate: coarsen page-scroll re-render from per-24px to per-row (+ engine LazySection follow-on)

> **Review outcome:** passed adversarial review (sound as written). **Estimate:** S-M for the shipping fix: one control (LazyGrid.cs) + two comment edits + ~7 headless gates; no engine/data/scene changes, trivially reversible. The deferred LazySection engine seam is L (reconciler realize-against-ancestor + a drawer-aware IVirtualLayout + virtualization canon reconcile) and is explicitly out of this change.

**Summary.** LazyGrid.Render subscribes to the page-scroll offset signal (published every 24px by ArtistPage/DiscographyPage OnScrollGeometryChanged) and rebuilds its whole realized window on every change; each rebuild sets `reconciled` in AppHost (~1428), which globally arms SpanReuseDisabledReason.SceneChanged + Layout (AppHost 1605-1606) — the per-frame span-reuse kill at fling. The key insight verified in code: LazyGridMath.Compute's output (FirstRow, LastRow, TopPad, BottomPad, DrawerVisible) is a pure step-function of the row window; sub-row offset changes produce byte-identical trees. So LazyGrid should re-render only when its own (first,last) window changes — ~once per rowH (~250px) instead of every 24px, a ~10x reduction in reconcile frames. The mechanism is a per-instance value-gated `Signal<long>` window key, written from a `UseSignalEffect` that bridges the hot offset into the coarse key (Memo cannot gate — its staleness propagates eagerly; only Signal<T>'s write-equality gates). Windowing-key derivation moves OFF the page (which doesn't know rowH and can't serve multiple grids) INTO LazyGrid, where rowH/sectionTop/overscan live. Structural reactivity (count/width/expand) keeps re-rendering Render directly, so paging, the inline drawer, initialIndex and scroll-restore are unaffected. This ships now (app+Controls only, no engine change); a real engine seam (a "LazySection" that realizes against an ancestor scroll with zero component round-trip) is designed as a deferred follow-on.

# A-lazygrid-windowing — LazyGrid window-gate

## Problem, re-verified against code

- `ArtistPage.cs:71` and `DiscographyPage.cs:124` publish page scroll as
  `OnScrollGeometryChanged = (g => (long)(g.OffsetY / 24f), g => pageScroll.Value = g.OffsetY)`.
  The engine observer (`ScrollBindEval.RunObservers`, phase 7, AppHost:1556) fires the action only when the
  projected `long` key changes → `pageScroll` (a `Signal<float>`) is written once per **24px** of scroll.
- `LazyGrid.Render` (`LazyGrid.cs:112-113`) does `var scrollSig = UseContext(LazyScroll.Slot); float scrollOffset = scrollSig?.Value`.
  Reading `.Value` **subscribes the render-effect** → every 24px write re-renders the LazyGrid → `Reconciler`
  produces a diff → `AppHost:1428 reconciled = true` → `AppHost:1432 layoutNeeded = true` →
  `AppHost:1605-1606 spanDisable |= SceneChanged | Layout`. Global span-reuse kill, every ~24px, i.e. every
  frame at fling. Two-to-three grids (Albums + Singles sections) each do this.
- **Key fact that makes the fix cheap:** `LazyGridMath.Compute` (`LazyGrid.cs:34-53`) derives `first/last` via
  `floor(top/rowH)` and `floor((top+viewportH)/rowH)`, and `TopPad = first*rowH (+drawer)`,
  `BottomPad = contentH - topPad - blockH`. **None of the built tree depends on the continuous offset** — only
  on `(first,last,drawerVisible)`. Sub-row scrolling yields identical output. So a LazyGrid only needs to
  re-render when its row window changes: ~once per `rowH` (~250px on DiscoGrid: `rowExtra = CardChrome+RowGap = 90`,
  cellW≈170 → rowH≈260), a ~10× cut in reconcile frames, and the page-scroll transform (which the engine already
  applies without a component render) carries the visual motion in between.

## Why not just a Memo / a coarser page projection

- **Memo does not gate re-renders.** `Memo<T>.OnStale` (Memo.cs:48-51) marks its subscribers stale **eagerly**
  when any upstream signal changes; the value-equality check in `Recompute` (Memo.cs:43) only updates the memo's
  own cache and runs lazily *after* the downstream render-effect is already scheduled. A `UseComputed(windowKey)`
  read in Render would therefore still re-render on every 24px. Only `Signal<T>.Value`'s setter gates
  (`Signal.cs:34` — `if (_cmp.Equals(_value, value)) return;` **before** `NotifySubscribers`). So the coarsening
  must funnel through a `Signal<long>` written only when the key changes.
- **The projection cannot live on the page.** rowH is derived inside LazyGrid from the measured width
  (responsive columns, `LazyGrid.cs:116-119`); `sectionTop`/`viewportH` come from the live scene
  (`Geometry()`, `LazyGrid.cs:199-212`); a page hosts **multiple** LazyGrids with different `sectionTop` and
  `rowH`. A single page-level `/24f` (or any fixed) quantum cannot express per-grid row windows. The observer
  registry is also **one-observer-per-scroll-node** (`SceneStore.cs:979-985`, keyed by node index; RunObservers
  reads that node's `ScrollState`), so sibling LazyGrids cannot each register their own observer on the shared
  page scroller. **Decision: key derivation belongs in the LazyGrid consumer; the page keeps publishing one raw
  offset signal.**

## Chosen mechanism (ships now — app + Controls only)

Add a per-instance value-gated window-key signal to `LazyGrid`, fed by a hot→coarse bridge effect.

### `src/FluentGpu.Controls/LazyGrid.cs`

1. New field: `readonly Signal<long> _win = new(long.MinValue);` — the coarse re-render trigger. Seed to a
   sentinel so the first computed key always writes.

2. New pure packer (static, alloc-free), reused by both the effect and Render for consistency:
   ```
   static long PackKey(in LazyGridMath.View v) =>
       ((long)(uint)(v.FirstRow + 1) << 40) ^ ((long)(uint)(v.LastRow + 1) << 8) ^ (v.DrawerVisible ? 1L : 0L);
   ```
   (`first/last` are clamped to `[0,totalRows)` or `-1`; the `+1` keeps the empty view `(0,-1)` distinct. 32-bit
   fields are ample — no grid realizes 2^32 rows.)

3. In `Render()`:
   - Change `float scrollOffset = scrollSig?.Value ?? 0f;` → `float scrollOffset = scrollSig?.Peek() ?? 0f;`
     (**stop subscribing** the render-effect to the raw offset).
   - Keep the existing subscriptions that must still re-render Render directly: `count = _count()` (VC.Version),
     `_w.Value` (resize), `expandedIndex = _expanded?.Value` (expand/collapse). These are the *structural*
     dimensions; they change the tree independently of scroll and must re-render.
   - Add one line to subscribe the render-effect to the coarse trigger: `_ = _win.Value;`
   - Everything else (the `LazyGridMath.Compute` call, `_ensureRange`, the children build) is unchanged and runs
     on the recomputed live `view`.

4. Add the bridge effect (mount-once, re-runs when the hot offset changes), placed near the top of `Render`
   after `scrollSig` is resolved, guarded so it installs once:
   ```
   UseSignalEffect(() =>
   {
       float off = scrollSig?.Value ?? 0f;     // SUBSCRIBE to the hot offset here (the only subscription to it)
       // recompute the window from LIVE structural state (Peek — do not subscribe; Render owns those dims)
       int count = _count();                    // Peek-equivalent inside the effect's own tracking scope
       float w = _w.Peek();
       float eff = w > 1f ? w : 900f;
       int cols = Math.Max(1, (int)((eff + _gap) / (_minColW + _gap)));
       float cellW = MathF.Max(_minColW * 0.5f, (eff - (cols - 1) * _gap) / cols);
       float rowH = cellW + _rowExtra;
       int totalRows = count <= 0 ? 0 : (count + cols - 1) / cols;
       int expandedIndex = _expanded?.Peek() ?? -1;
       int expandedRow = expandedIndex >= 0 ? expandedIndex / cols : -1;
       float drawerH = expandedIndex >= 0 && _drawerHeight is { } dh ? dh(expandedIndex) : 0f;
       (float sectionTop, float viewportH) = Geometry();
       var v = LazyGridMath.Compute(off - sectionTop, viewportH, rowH, totalRows, _overscanRows, expandedRow, drawerH);
       _win.Value = PackKey(v);                 // GATED write — notifies only when the row window changes
   });
   ```
   - Note: `_count()` reads `VC.Version.Value`, which would subscribe the effect to Version too. That is benign
     (Version bumps are page-lands, not per-frame) but to keep the effect purely offset-driven, wrap the
     structural reads in `Reactive.Untrack(() => …)` so the effect subscribes to `scrollSig` **only**. Structural
     re-renders are already owned by Render's direct subscriptions.

### Data flow after the change

- Sub-row scroll: observer writes `pageScroll` (24px) → effect runs (phase 3–5, alloc-free) → recomputes key →
  key unchanged → `_win` not written → **no re-render, no reconcile, no layout** → AppHost `reconciled=false` →
  span reuse stays eligible (subject to the sibling areas B/D/E). The page-content `-offset` transform still
  animates the scroll.
- Row-boundary crossing: key changes → `_win.Value` write marks the render-effect stale → drained **in the same
  Flush** (ReactiveRuntime.Flush's transitive loop, ReactiveCore.cs:207) → Render rebuilds the window this frame.
  One reconcile per row, not per 24px.

### What `pageScroll` / `LazyScroll.Slot` become

- `LazyScroll.Slot` keeps its type and role (`Context<IReadSignal<float>?>` = the ancestor raw offset). It is now
  consumed via `.Peek()` in Render + `.Value` inside the LazyGrid bridge effect; it is **no longer a direct
  render subscription**. Update its doc-comment to say the windowing quantum lives in the consumer.
- `ArtistPage.cs:71` / `DiscographyPage.cs:124`: the `/24f` projection stays as a cheap write-throttle floor on
  the signal (bounds signal churn to ~2 writes/frame at fling); it is no longer the windowing quantum. Add a
  comment. **No functional app change is required beyond the doc**, because the consumer coarsens further. The
  24px floor is absorbed by overscan (see edge cases). Optionally the projection could be simplified to a plain
  offset bucket; leaving `/24f` is fine.

### Sibling pages

- `DiscoGrid` (`ArtistPage.AlbumExpand.cs:420-429`) needs **no change** — it drives LazyGrid through delegates and
  already passes `overscanRows: 4` (≥1 row, required — see edge cases).
- `DetailTracks.cs` is **already engine-virtualized** (`ItemsView.CreateBound` → `VirtualListEl`, scroll is
  transform-only). Its `VerticalScrollKey`/`OnVerticalScroll` (`DetailTracks.cs:523-536`) feed
  `_verticalScrollOffset`, which is consumed by `VerticalHeaderOpacity` through `Opacity = Prop.Of(...)` — a
  **node opacity bind, not a component re-render**. It genuinely needs fine granularity for the shy-pill fade, so
  leave it. No LazyGrid-class problem exists there; do not touch it.

## Interaction with span reuse / scroll integrator / reconciler

- **Span reuse (root cause #1/#3):** on non-boundary frames `reconciled` is now false → `SpanReuseDisabledReason`
  loses `SceneChanged` and (if nothing else is dirty) `Layout`. This is the direct lever on the diagnosed
  per-24px kill. It is necessary-not-sufficient: areas B (subtree cull / scrollInMotion), D (image fades) and E
  (edge-fade) must land for a fully clean fling frame; this area removes the app-injected reconcile.
- **Scroll integrator:** unchanged. The integrator writes the content `-offset` transform (phase 7) every frame;
  the observer still fires the raw-offset write; only the *consumer's* re-render is coarsened. No change to
  fling/rubber-band.
- **Reconciler:** fewer diffs. When Render does run (row boundary / expand / resize / page-land) it produces the
  same keyed children (`"lazy-row:"+r`, `"lazy-top"`, `"lazy-bottom"`, drawer keys) as today, so the keyed-LIS
  diff, `OnRealized`/`OnBoundsChanged` capture, and the drawer's stable-key transition are all preserved.

## Edge cases

- **Paging cadence (ensureRange ahead of the window):** `_ensureRange` (LazyGrid.cs:148) still runs each time
  Render runs = each row-window change, with the overscan-inflated `[first*cols, (last+1)*cols)`. Because Render
  fires *on the boundary crossing*, and overscan (`overscanRows` ≥ 2 default, 4 on DiscoGrid) extends the window
  past the viewport, pages are requested before their rows are visible — identical lookahead to today.
- **24px floor vs rowH boundary:** the gate reads an offset that can be up to 24px stale. The true boundary can
  fall between two 24px steps, so the realized window can be off by ≤24px worth of rows at the instant of
  crossing — strictly < 1 row, absorbed by overscan (≥1 row). **Requirement: `overscanRows ≥ 1`** (all call
  sites satisfy this). Document it; add a debug assert.
- **Inline expanded drawer (expandedRow + drawerHeight change the extent):** `_expanded.Value` is a direct Render
  subscription → toggling re-renders Render regardless of the scroll gate. The effect Peeks `_expanded` only to
  keep `_win` coherent. `DrawerVisible` is folded into `PackKey`, so a scroll that brings the drawer into/out of
  the window also re-renders. `BringExpandedIntoView` (LazyGrid.cs:232) still runs from its `UseLayoutEffect`.
- **initialIndex scroll / scroll-restore by route:** driven by `MaybeInitialScroll` (LazyGrid.cs:216) and the
  ScrollState restore latch, both independent of the gate. They fire from Render (which still runs at mount and
  on width/count landing) and from layout effects. On the first real layout, the `_w` write re-renders Render
  (as today) with real geometry, so the initial window is correct one frame after layout — unchanged from
  current timing.
- **Multiple LazyGrids, different sectionTop:** each instance owns its own `_win` + effect and computes its own
  window from its own `Geometry()` — exactly the per-grid correctness a page-level key cannot give.
- **Section tops shifting when a drawer opens ABOVE a grid:** a sibling section's expand shifts this grid's
  `sectionTop` without changing scroll offset. This grid's effect (offset-only subscription) does not re-fire, so
  `_win` lags until the next scroll tick. **This is identical to today's behavior** (today's grid also only
  re-renders on `pageScroll` change), so it is not a regression; and in practice `BringExpandedIntoView` nudges
  the offset on expand, which fires the gate. Documented as a known, pre-existing edge that the engine seam
  (below) eliminates. If tighter correctness is wanted without the seam, the page can bump a shared
  `LazyScroll.LayoutEpoch` `Signal<int>` on any structural page change and have the effect subscribe to it —
  listed as an open question, not required for the fix.
- **Per-render allocations (children List, Row arrays, MediaCard trees, LazyGrid.cs:150-163/174-195):** these are
  phase 3–5 (reconcile-edge) allocations, not phase 6–13, so they never violated the hot-phase gate; the fix cuts
  their **frequency ~10×**, which is the real Gen0-pressure win at fling. Pooling the arrays is a separate,
  optional follow-up.

## Allocation analysis (hot phases)

- The bridge effect body: reads a float, Peeks scalars, calls `Geometry()` (two `AbsoluteRect` reads + a parent
  walk, no alloc), `LazyGridMath.Compute` (returns a `readonly record struct` by value, no alloc), packs a long,
  writes `Signal<long>` (no alloc; `NotifySubscribers` only marks stale). **0 managed bytes.** It runs during
  Flush (phase 3–5), never in phases 6–13, so it is outside the hot-phase tripwire regardless — and is still
  alloc-free.
- On non-boundary scroll frames LazyGrid contributes **no** render/reconcile/layout work, so phases 6–13 record
  the same DrawList as the prior frame → span reuse eligible → 0 managed alloc in the hot phases (the existing
  `gate.scroll.alloc-zero`/`gate.touch.fling-alloc-steady-zero` invariant is preserved and extended to the
  in-page grid case).

## Test / gate plan (headless, `src/FluentGpu.VerticalSlice/Program.cs`)

LazyGrid is in `FluentGpu.Controls` (TerraFX-free) so it is usable in the harness; existing scroll gates
(`gate.scroll.*`) show the pattern (build a scroll host + scene, drive offset, assert on `SceneRecordStats` /
`ConsumeReconciled`).

- **`gate.lazygrid.window-gate-coarsens-reconcile`** — mount a LazyGrid in a headless scroll host with known
  rowH; drive the page offset in 24px steps across less than one row: assert `AppHost.ConsumeReconciled()` is
  false on every sub-row step; drive one step that crosses a row boundary: assert exactly one reconcile.
- **`gate.lazygrid.window-gate-alloc-zero`** — 30 sub-row offset writes: assert `GC.GetAllocatedBytesForCurrentThread()`
  delta == 0 across the bridge-effect runs (the effect stays clean).
- **`gate.lazygrid.window-identical-output`** — at two offsets within the same row band, assert the built subtree
  is structurally identical: same realized row count and equal `lazy-top`/`lazy-bottom` spacer heights (the
  step-function invariant).
- **`gate.lazygrid.paging-keeps-ahead`** — advance the window one row; assert `ensureRange` was called with a
  range whose `last*cols` reaches past the viewport by ≥ overscan rows before those rows enter the viewport.
- **`gate.lazygrid.expand-rewindows`** — with no scroll tick, write `_expanded`; assert Render ran and the drawer
  row is present in the realized window (DrawerVisible folded into the key).
- **`gate.lazygrid.overscan-absorbs-24px-floor`** — with a 24px-stale offset at a boundary, assert the visible
  viewport rows are all realized (overscan ≥ 1 row covers the floor skew).
- **`gate.reuse.lazygrid-scroll-no-global-kill`** (integration) — an artist-page-like scene (grid + edge scene)
  scrolled sub-row: assert `SceneRecordStats.SpanReuseDisabledReasons` does **not** contain `SceneChanged` on the
  non-boundary frames (guards the regression this area targets).

## Rollback story

The change is confined to `LazyGrid.cs` plus comment-only edits on the two pages. Rollback = revert
`LazyGrid.cs`: restore `scrollSig?.Value`, drop `_win`/the bridge effect/`PackKey`. No engine, no data-layer, no
scene-format change; no persisted state; the observer/`LazyScroll.Slot` contracts are untouched. A DEBUG env flag
(`WAVEE_LAZYGRID_EAGER=1`) can force the old direct-subscription path for A/B bisection without a rebuild — a
debug-only diagnostic, not a quality knob (allowed per CLAUDE.md).

---

## Deferred engine seam (follow-on, NOT in this change): `LazySection`

The app fix removes the per-24px reconcile but still reconciles once per row. The end state is **zero** component
round-trip during scroll, matching how `VirtualListEl` already works for self-scrolling lists. The obstacle:
`VirtualListEl` owns its own `ScrollState`; LazyGrid is an in-page section windowing against an **ancestor**
scroll (the SwiftUI `LazyVGrid`-in-`ScrollView` model its own doc cites, LazyGrid.cs:13-15). Design:

- **New engine capability** (owning doc: `design/subsystems/virtualization.md`; register in
  `design/SPEC-INDEX.md` §2 + `design/subsystems/README.md` ownership map; `check-canon.ps1` must pass): a
  virtualized realizer that binds to the **nearest ancestor scroll node** instead of owning a ScrollState — either
  a `VirtualListEl { HostScrollAncestor = true }` mode or a sibling `LazySectionEl`. The reconciler, in
  `ReRealizeVirtuals`/on the ancestor's `NodeFlags.VirtualRangeDirty`, computes the window from the ancestor
  `ScrollState` + the section's content-space top, realizes just that window, and emits leading/trailing spacer
  nodes to reserve the section's extent. Scroll then only sets `VirtualRangeDirty` on the ancestor → the
  reconciler re-realizes section windows with **no component Render**.
- **Layout:** the section uses a pluggable `IVirtualLayout`. The inline drawer is modeled by a small custom
  `IVirtualLayout` (or a `GridVirtualLayout` extension) that inserts one full-width variable-extent band after
  `expandedRow` of height `drawerH` — the extent/window math already in `LazyGridMath.Compute`, moved behind the
  seam. `SpanningGridVirtualLayout`/`IMeasuredVirtualLayout` are the precedents.
- **Data paging / drawer / restore:** `VirtualListEl.OnVisibleRange` → `ensureRange`; `OnItemPrepared/Clearing`
  for the drawer; `ScrollKey` for restore — all already exist on the seam.
- **Gates:** `gate.lazysection.scroll-zero-reconcile` (a full sub-row *and* cross-row scroll realizes windows with
  0 component re-renders and 0 hot-phase alloc), `gate.lazysection.drawer-extent`, `gate.lazysection.ancestor-restore`.
- **Sizing:** L (engine + reconciler + a layout). Recommend shipping the app window-gate first and scheduling the
  seam separately; the app fix is forward-compatible (LazyGrid can later delegate to `LazySectionEl` behind the
  same public surface).

## Part A — open questions

- Should LazyScroll gain a shared `LayoutEpoch` Signal<int> that the page bumps on any structural content-height change, so grids re-window on a sibling drawer-open without a scroll tick? Cheap and eliminates the one stale-tick edge, but adds a page->grid coupling; defer unless the edge is observed.
- Keep the /24f page projection floor, or drop it to a finer/plain offset bucket now that the consumer coarsens? Keeping it bounds signal-write churn; no correctness impact either way.
- Should the per-render children/Row arrays be pooled (ArrayPool / a reused builder) now, or left to the frequency reduction? Frequency drop is ~10x already; pooling is a separate optional win.
- For the follow-on: add a `HostScrollAncestor` mode to VirtualListEl vs a new LazySectionEl primitive? The former reuses the diff/realize plumbing; the latter keeps VirtualListEl's self-scroll contract clean. Needs a virtualization.md owner decision.

## Part A — reviewer notes

**Minor issues (address during implementation):**

- PackKey bit-packing prose is slightly wrong: `(long)(uint)(first+1) << 40` places FirstRow in bits 40-63, capping it at ~2^24 (16M) rows, not the '32-bit fields' the design claims. Practically irrelevant (no grid nears 16M rows) and the XOR is collision-free because the three fields occupy disjoint bit ranges (bit 0 / 8-39 / 40-63), so it behaves identically to OR. Conclusion holds; fix the comment.
- Inconsistency between the effect-body code snippet and the mandated behavior: the snippet computes `_count()`, `_expanded.Peek()`, etc. inline, then only a trailing 'Note' says to wrap structural reads in `Reactive.Untrack`. The implementer MUST actually wrap them (or accept benign, Queued-deduped extra effect runs). The risks section and file-change note do state this, so intent is clear, but the primary snippet should show the Untrack wrapping to avoid a copy-paste that subscribes the effect to VC.Version/_w/_expanded.
- Hook-placement hazard is under-specified in the per-file change note: `UseSignalEffect` MUST be added before the `if (totalRows == 0) { ... return; }` early return at LazyGrid.cs:141, or the hook cursor desyncs between the count==0 and count>0 renders (a crash, not just waste). The design body says 'near the top of Render after scrollSig is resolved' (line ~112-127), which is correct, but the files[] entry should explicitly call out the early-return boundary.
- Data-flow prose ('observer writes pageScroll -> effect runs (phase 3-5)') telescopes a real one-frame pipeline: observers run in phase 7 (after the phase-3 Flush per AppHost ordering), so the offset write in frame N triggers the bridge effect + gated render in frame N+1's Flush. The design's own risk note acknowledges the phase-7 timing, so it is internally consistent, but the coarsening gate (`gate.lazygrid.window-gate-coarsens-reconcile`) harness must model two frames per step, and this lag equals today's behavior (not a regression).
- Height-only viewport resize (viewportH changes, grid width _w unchanged, no scroll tick) will not re-window until the next scroll tick or width change, because the bridge effect is triggered only by offset writes. Verified this is parity with today (current Render also only re-renders on pageScroll/_w/count/_expanded), so it is not a regression — worth an explicit line in the edge-case list alongside the sibling-drawer-above case it already documents.

---

# Part D — Drop AutoEdgeFade on the opaque-plate page scrollers (surface-colour cue instead), and make the remaining true-alpha fades cheaper with a BLUR-SAFE scissored clear

> **Review outcome:** REVISED after adversarial review (blocking issues fixed in this body). **Estimate:** S-M — App side is 5 one-line deletions (NowPlayingPanel intentionally excluded, so 5 not 6). Engine side is a small pure helper (EdgeFadeLayerClear.Compute), one optional Acquire parameter, one PushLayer call-site branch (BlurSigma gate + stackalloc rect), and a diag counter — all allocation-free, no coordinate-system change, no seam change. The M weight is verification: per-surface opaque-plate confirmation (done for the five FileArea pages), the FadeAndBlur regression gate, and the --screenshot fidelity checks. The region-sized RT and the tighter blur-mode clear are deferred to keep this change low-risk.

**Summary.** Each AutoEdgeFade page scroller opens a PushLayer{EdgeFade} that leases a canvas-sized offscreen RT, does a full-canvas transparent clear, redirects the whole page subtree into it, then composites the whole viewport back with a ~40px feather — every frame there is overflow, which on the artist page is always. The middle of that composite is an identity copy; the cost buys a fade over two thin bands. A true alpha fade is only required where the thing behind the band is NOT reproducible at composite time. On the detail pages the scroller sits on the opaque rounded WaveeColors.FileArea card, so the already-wired, zero-layer EmitScrollEdgeCues surface-colour gradient is visually equivalent and free. Primary fix: drop AutoEdgeFade on the five page scrollers that sit on the opaque FileArea plate (ArtistPage, DetailTracks, DiscographyPage, EpisodeList, LibraryPage) so they fall through to the default EdgeCues=Fade cue — no engine change, no offscreen RT. Secondary: for the surfaces that MUST keep true alpha, scissor the layer's clear to the composite-clip box — but ONLY when the layer carries no blur (BlurSigma==0); a blur-mode edge fade keeps today's full-canvas clear because BlurInPlace reads a ±3σ halo that pokes past the composite-clip box. NowPlayingPanel is deliberately NOT downgraded (its rail plate may be translucent).

## 0. What changed vs the reviewed design (and what I rebut)

The adversarial review raised one BLOCKING issue and four MINOR issues. I verified every claim against the code:

- **BLOCKING (blur-mode clear corruption) — CONFIRMED, fix adopted.** `EdgeFadeMode` is `{Fade=0, Blur=1, FadeAndBlur=2}` (Effects.cs:128). `SceneRecorder.cs:581` emits a nonzero `BlurSigma` into the `PushEdgeFadeLayer` whenever `Mode != Fade` (`edgeFade.Mode == EdgeFadeMode.Fade ? 0f : edgeFade.BlurSigma * efsx`). In `D3D12Device.PopLayer` (D3D12Device.cs:1518-1519) `BlurInPlace` runs on the group RT before `EdgeFadeComposite` (1527) whenever `(kind==Blur||kind==EdgeFade) && gl.BlurSigma>0`. `BlurInPlace` scissors to `RegionBox` = `DeviceRect*scale` inflated by `±min(ceil(3σ),32)px` (OpacityLayerCompositor.cs:670-677, 856-859) — a region that extends OUTWARD past `CompositeClip` (which is `⊆ deviceBounds = DeviceRect`). Scissoring the Acquire clear to the composite-clip box would leave that outer halo un-cleared, so the Gaussian would pull stale pooled-slot content into the visible fade. The design's original universal claim "clear-box == composite-read-box, so every sampled texel was cleared" is false for blur mode. **Fix: gate the scissored clear on `BlurSigma == 0f`; a blur-carrying edge fade keeps today's full-canvas clear.** I additionally note (see §2) that NO edge fade in the current app uses blur mode — every `EdgeFadeSpec` uses the default `Mode=Fade` — so this is a latent defect in a *shared* engine primitive rather than live corruption; the fix is still mandatory because the primitive must be correct for all modes and a future blur-mode fade would corrupt silently.
- **MINOR #1 (clear/composite box symmetry not proven) — CONFIRMED, resolved by construction.** I no longer intersect the clear box with `CurrentScissorRect()` at push time. The clear box is `clamp(L.CompositeClip * scale)` to the canvas — a strict SUPERSET of what `EdgeFadeComposite` reads (which is that same box ∩ current scissor). Superset ⇒ every composited texel was cleared, with zero dependency on push/pop scissor-stack symmetry.
- **MINOR #2 (no headless gate for the clear invariant) — CONFIRMED, resolved.** The clear-box *decision* (which rect, and whether to fall back to full canvas) is moved into a TerraFX-free pure helper in `FluentGpu.Engine` that the VerticalSlice harness can call on a captured `PushLayerCmd`. Only the D3D12 `ClearRenderTargetView` plumbing stays in `FluentGpu.Windows`. The correctness-critical geometry is now deterministically gated headlessly.
- **MINOR #3 (NowPlayingPanel line numbers wrong) — CONFIRMED, corrected.** Grep shows the outer-ScrollView `AutoEdgeFade=true` at `NowPlayingPanel.cs:75` (correct) and the section `EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 56f)` at `:183` (NOT 203); there is no fade at 122.
- **MINOR #4 (translucent-plate missing-affordance risk) — CONFIRMED, resolved by narrowing scope.** `RightRail.cs:62` fills the docked panel with `railFill = ColorF.Lerp(WaveeColors.RailOverlay, paletteWash, …)` and `:84` with `WaveeColors.RailOverlay`/`WaveeColors.Sidebar` — `RailOverlay` is an overlay colour that may be translucent, in which case `TryResolveCueSurface` (SceneRecorder.cs:1637) would self-skip and the downgraded rail would lose its affordance. **I therefore removed NowPlayingPanel from the downgrade set** — it keeps its true-alpha AutoEdgeFade (it is a small rail, not an FPS offender, and the clear-scissor already makes it cheap). The five remaining downgrades all sit on the opaque `WaveeColors.FileArea` card (WaveeShell.cs:329), verified below.

## 1. Measured cost shape (verified in code)

Per frame, whenever an edge overflows, for one AutoEdgeFade / explicit-EdgeFade scroller:

**Record (phase 8, UI thread), `SceneRecorder.Walk`:**
- `TryResolveEdgeFade` (SceneRecorder.cs:1604) fires for a `Scrollable` node with `AutoEdgeFade && AutoEdgeFadeBand>0.5f` and synthesizes an `EdgeFadeSpec` from live overflow. `AutoEdgeFadeBand` is a fixed 40f (Reconciler.cs:2324/2354).
- Emits one `PushEdgeFadeLayer` (SceneRecorder.cs:575-581) carrying `deviceBounds` (the FULL viewport), the composite clip (`deviceBounds ∩ clip ∩ authored clip`), per-edge bands, corners, falloff, intensity, and `BlurSigma` (0 for `Mode=Fade`). `stats.EdgeFadeGroupCount++`.
- The recorder then walks the ENTIRE subtree into the DrawList between Push/Pop (no subtree cull — area-B/C problem), so the layer wraps the whole page.

**Execute (submit), `D3D12Device.RenderLayered` (D3D12Device.cs:1410-1536) + `OpacityLayerCompositor`:**
- `PushLayer` → `FlushSegment` (drains the backdrop-so-far), then `Acquire` (OpacityLayerCompositor.cs:529): leases a canvas-sized RT from the pool (steady-state reuse, no allocation), `Barrier`→RENDER_TARGET, `OMSetRenderTargets`, and **`ClearRenderTargetView(rtv, {0,0,0,0}, 0, null)` — clears the WHOLE RT regardless of scissor** (line 574-575). At 4K that is a ~33 MB zero-fill every frame.
- The whole subtree rasterizes into that RT, forcing the layered path and a Flush boundary at push and pop.
- `PopLayer` → `FlushSegment`; if `BlurSigma>0`, `BlurInPlace` (Gaussian, scissored to `RegionBox`); else `BeginRead`; then `EdgeFadeComposite` (OpacityLayerCompositor.cs:613): scissors to `L.CompositeClip*scale ∩ currentScissor`, binds the 16-root-const edge PSO, draws a fullscreen triangle sampling the canvas-sized RT and writing the composite-clip region with `src * feather * groupAlpha` premultiplied-over the target. Over the un-faded middle `feather==1`, i.e. a pure RT→target identity copy.

**Caching:** none for edge fade. The pool reuses RT slots across frames (no per-frame CreateResource); the cross-frame blur-cache pin system (`FindPin`/`RetainPinFromScratch`) is `Kind==Blur`-only (D3D12Device.cs:1424) and keyed on position-independent content, which edge-fade band pixels (anchored to the viewport edge, different content every scroll frame) cannot satisfy — so cross-frame reuse can't help during active scroll. Ruled out.

**Layer-in-layer on the artist page:** the page's AutoEdgeFade layer wraps the whole subtree, and inside it nest (i) the hero's `EdgeFade = EdgeFadeSpec(Bottom, 260f)` (ArtistPage.Hero.cs:113) and (ii) each PagedShelf's horizontal viewport `EdgeFade` (ArtistPage.Shelves.cs:26-27 via DetailTrailing; 36f). Removing the outer page layer deletes the biggest single cost AND un-nests the rest (they become sequential, one reused slot).

## 2. Correctness analysis of the four options

The EdgeFade semantic (Effects.cs; shader in OpacityLayerCompositor): render content in isolation at full alpha, attenuate its premultiplied alpha to 0 over the band, `SourceOver` over whatever is in the target. It dissolves content INTO the real backdrop.

- **(a) Band-strip compositing** (only top/bottom bands through an RT, middle direct to back buffer): the subtree is one contiguous stream spanning the viewport; addressing "band content" requires either replaying the whole subtree twice under complementary scissors (double submission — catastrophic for a thousands-of-node artist page) or rendering everything once to the back buffer and leaving no way to recover the covered backdrop. **Rejected as a general mechanism.**
- **(b) dest-out gradient mask over the back buffer:** once content is composited onto the backdrop the pixel is opaque `over(backdrop, content)`; dest-out can only fade toward the clear colour, not reveal the backdrop. Restoring the backdrop requires re-drawing it — possible only if reproducible; if flat that IS option (c); if Mica/blur it is impossible. **Rejected.**
- **(c) Surface-colour `EmitScrollEdgeCues` gradient** (SceneRecorder.cs:1657): zero new scene nodes, zero managed allocation, no layer, drawn straight to the target as a `GradientRect` (surface→transparent) at each overflowing edge, alpha ramped with per-edge overflow. Visually identical to a true alpha fade IFF the thing directly behind the band is that flat surface colour. `TryResolveCueSurface` (SceneRecorder.cs:1637-1650) resolves the nearest opaque self/ancestor fill (or an acrylic `Fallback`), and self-skips (draws nothing) when no opaque plate exists — so it degrades safe, never wrong-colour. **Primary mechanism for opaque-plate page scrollers.**
- **(d) Cross-frame layer reuse:** ruled out (§1).

**No app edge fade uses blur mode.** Grep of `src/apps/Wavee` shows every `EdgeFadeSpec` construction uses the two/four-arg ctor with the default `Mode = EdgeFadeMode.Fade` (Rail.cs:48, ArtistPage.Hero.cs:113, ArtistPage.Shelves.cs:25, DetailTrailing.cs:26, NowPlayingPanel.cs:183); no `Mode=Blur`/`FadeAndBlur` anywhere. So the blur-mode clear corruption (§0) is latent, but the engine fix must still be blur-correct for the shared primitive.

**Where (c) is correct in this app (DOWNGRADE):** detail/library content scrolls inside the shell's opaque rounded "page" card — `Fill = Prop.Of(() => WaveeColors.FileArea)`, `Corners = All(WaveeRadius.Card)`, `Shadow = Elevation.Card`, `ClipToBounds = true` (WaveeShell.cs:329, hosting `ContentHost` at :335). ArtistPage / DetailTracks / DiscographyPage / EpisodeList / LibraryPage all render inside this ContentHost card, so top/bottom bands sit over the flat opaque FileArea colour; `TryResolveCueSurface` walks up to it and the cue is pixel-honest.

**Where (c) is NOT correct (KEEP true-alpha EdgeFade):**
- **LyricsView (462, 505):** lyrics dissolve into the blurred album-art backdrop — non-flat, non-reproducible. Keep.
- **WaveeSidebar (136):** the nav pane is Mica-passthrough; the code comment calls it the engine's premium true-alpha edge-scroller. Keep.
- **NowPlayingPanel outer scroller (75):** the rail panel fill is `railFill = Lerp(RailOverlay, paletteWash)` (RightRail.cs:62) or `RailOverlay` (RightRail.cs:84) — `RailOverlay` may be translucent, so `TryResolveCueSurface` could self-skip. It is small (~340px rail) and not an FPS offender. Keep true-alpha (made cheap by §3). **This is the correction vs the reviewed design, which downgraded it.**
- **ArtistPage hero bottom 260px (Hero.cs:113):** the hero image dissolves into the content below (the two-column band), not a flat plate. Keep.
- **PagedShelf horizontal viewport fades (ArtistPage.Shelves.cs:26-27, DetailTrailing.cs:27-28, Rail.cs:48) and face piles (ArtistFacePile.cs:174, CollaboratorFacePile.cs:148):** small strips, the legit "fade the viewport, never each row" use. Keep.

## 3. The engine change (BLUR-SAFE scissored clear)

Add one pure geometry helper (engine, TerraFX-free) that decides the clear rect, and one optional clear-rect parameter on `Acquire` (Windows).

**Helper (new), `FluentGpu.Engine/Render` — `EdgeFadeLayerClear.Compute`:**
```
// TerraFX-free; ints only, no allocation. Single source of truth for the clear extent.
public static void Compute(in PushLayerCmd L, float scale, int canvasW, int canvasH,
    out int left, out int top, out int right, out int bottom, out bool fullCanvas)
{
    // A blur-carrying edge fade reads a ±min(ceil(3σ),32)px halo past CompositeClip (BlurInPlace/RegionBox),
    // so a scissored clear would leave that halo sampling stale pooled-slot content. Keep the full-canvas clear.
    if (L.BlurSigma > 0f) { left = top = 0; right = canvasW; bottom = canvasH; fullCanvas = true; return; }
    // Pure Mode=Fade: clear exactly the CompositeClip box (a SUPERSET of what EdgeFadeComposite reads, which is
    // this box ∩ the current scissor). Superset ⇒ every composited texel was cleared, with no dependency on the
    // push/pop scissor-stack state. Clamp to the canvas.
    var cr = L.CompositeClip;
    left   = Math.Clamp((int)MathF.Floor(cr.X * scale), 0, canvasW);
    top    = Math.Clamp((int)MathF.Floor(cr.Y * scale), 0, canvasH);
    right  = Math.Clamp((int)MathF.Ceiling((cr.X + cr.W) * scale), left, canvasW);
    bottom = Math.Clamp((int)MathF.Ceiling((cr.Y + cr.H) * scale), top, canvasH);
    fullCanvas = false;
}
```
This mirrors the box math in `EdgeFadeComposite` (OpacityLayerCompositor.cs:617-620) but WITHOUT the `∩ clip` step, guaranteeing the clear is a superset of the composite read.

**`OpacityLayerCompositor.Acquire` (~529):** add an optional `D3D12_RECT* clearRect = null`. When non-null, `ClearRenderTargetView(rtv, clear, 1, clearRect)`; when null, keep `ClearRenderTargetView(rtv, clear, 0, null)` (full canvas — unchanged for Opacity/Blur callers, whose group content can extend past the layer rect). No pool/descriptor/coordinate change; the RT stays canvas-sized.

**`D3D12Device.cs` PushLayer branch (~1485):** for `L.Kind == (int)LayerKind.EdgeFade`, call `EdgeFadeLayerClear.Compute(in L, _frameScale, (int)_w, (int)_h, out l,out t,out r,out b, out fullCanvas)`. If `fullCanvas` pass `null` to `Acquire` (blur-mode: today's behaviour, provably correct). Else `stackalloc` a `D3D12_RECT{l,t,r,b}` and pass it. Add a `Diag.Set("d3d12","edgeFadeClearPx", …)` accumulator (cleared area) for windowed measurement. Opacity/Blur/Acrylic call sites keep passing `null`.

Note the `EdgeFadeComposite` call site (D3D12Device.cs:1527) is unchanged; it already receives `CurrentScissorRect()` and clips its own draw. Because the clear box is a superset of `L.CompositeClip*scale` and the composite reads `L.CompositeClip*scale ∩ CurrentScissorRect()`, every read texel is inside the cleared box regardless of the scissor stack.

## 4. Per-file changes

### App (primary — drop the flag on opaque-plate page scrollers)
- `src/apps/Wavee/Features/Detail/ArtistPage.cs:69` — delete `AutoEdgeFade = true,` (keep `OnScrollGeometryChanged` on :71). Top FPS offender.
- `src/apps/Wavee/Features/Detail/DetailTracks.cs:597` — delete `AutoEdgeFade = true,`.
- `src/apps/Wavee/Features/Detail/DiscographyPage.cs:123` — delete `AutoEdgeFade = true,`.
- `src/apps/Wavee/Features/Detail/EpisodeList.cs:74` — delete `AutoEdgeFade = true`.
- `src/apps/Wavee/Features/Library/LibraryPage.cs:366` — delete `AutoEdgeFade = true` on the compact-episodes ScrollView.
- **`src/apps/Wavee/Features/Player/NowPlayingPanel.cs` — NO CHANGE.** The outer `AutoEdgeFade = true` (:75) stays true-alpha (rail plate possibly translucent; small; not an FPS offender). The section EdgeFade (:183, `EdgeMask.Bottom, 56f`) is untouched.
- **No change** (keep true alpha): `LyricsView.cs:462/505`, `WaveeSidebar.cs:136`, `ArtistPage.Hero.cs:113`, `ArtistPage.Shelves.cs:26-27` + `DetailTrailing.cs:27-28`, `Rail.cs:48`, `ArtistFacePile.cs:174`, `CollaboratorFacePile.cs:148`.

Optional polish: on the five downgraded surfaces you may set `EdgeCues = ScrollEdgeCues.Fade` explicitly (documents intent), but it is already the resolved default (Reconciler.cs:2318/2350 → `ScrollEdgeCuesDefaults.Default = Fade`) — not required.

### Engine (secondary — blur-safe scissored clear)
- `src/FluentGpu.Engine/Render/EdgeFadeLayerClear.cs` (NEW) — the pure `Compute` helper in §3. TerraFX-free, ints/`in`/`out` only, callable from both `FluentGpu.Windows` and the VerticalSlice harness.
- `src/FluentGpu.Windows/D3D12/OpacityLayerCompositor.cs`, `Acquire` (~529) — optional `D3D12_RECT* clearRect = null`; pass `1, clearRect` to `ClearRenderTargetView` when non-null, else `0, null` (unchanged).
- `src/FluentGpu.Windows/D3D12/D3D12Device.cs`, PushLayer branch (~1485) — for `LayerKind.EdgeFade` call `EdgeFadeLayerClear.Compute`; pass `null` when `fullCanvas` (blur), else a `stackalloc`'d `D3D12_RECT`. Add the `edgeFadeClearPx` Diag counter.

**Deferred (not in this change):** (1) a region-SIZED EdgeFade RT (RT sized to the composite-clip box, subtree redirected via a shifted viewport origin) would also cut VRAM and the middle-copy bandwidth, but needs device-viewport translation of the subtree draws — higher risk, own change. (2) A tighter clear for blur mode (`RegionBox ∪ CompositeClip`, plus the extra ±halo the H-pass reads) — saves bandwidth for a case that does not exist in the app today; not worth the subtlety now. Log both in `gpu-renderer.md`.

### Canon docs
- `design/subsystems/controls.md §8.3` — record the per-surface policy (page-sized scrollers over an opaque plate use the surface-colour cue; true-alpha EdgeFade is reserved for non-reproducible backdrops and small strips; NowPlayingPanel kept true-alpha pending a confirmed opaque rail plate).
- `design/subsystems/gpu-renderer.md` — amend the `PushLayer{EdgeFade}` realization note: the clear is scissored to the composite-clip box for `BlurSigma==0`, and stays full-canvas for `BlurSigma>0` (the blur halo reads past the composite-clip box); log the region-sized RT and the tighter blur clear as deferred.

## 5. Interaction with span reuse / scroll integrator / reconciler
- **Span reuse:** dropping the page layer removes DrawList opcodes (one Push/Pop pair + the forced Flush boundaries), shrinking each re-record and keeping the frame off the layered path when no other layer is present. `EmitScrollEdgeCues` emits `GradientRect` at record time like any primitive and participates in span signatures identically. No change to the canon rule (`IsLive ∧ content-epoch ∧ baked-geometry hash`, single `Mutate()` chokepoint).
- **Scroll integrator:** unchanged. The cue reads live `ScrollState` (offset/content/viewport) at record time; no new signal, no new observer, no new reconcile trigger (unlike the LazyGrid `pageScroll` churn in area C).
- **Reconciler:** `AutoEdgeFade=false` sets `AutoEdgeFadeBand=0` (Reconciler.cs:2324/2354), so `TryResolveEdgeFade` returns false and `EdgeFadeGroupCount` stays 0 for those nodes; `EdgeCueConfig` already carries `Fade` (2318/2350). No reconciler code change.
- **Render-thread seam:** the engine change is entirely `OpacityLayerCompositor`/`D3D12Device` on the render thread; all ComPtrs and the command list stay render-thread-owned. The clear-rect is a `stackalloc` POD passed by pointer within one call; the `Compute` helper is a static over value inputs — no cross-thread state, seam-compatible.

## 6. Allocation analysis (phases 6-13)
- App flag removal: pure deletion; `EmitScrollEdgeCues` is "zero new scene nodes, zero managed allocation" (SceneRecorder.cs:1654) via the pooled DrawList writer. Net phase-8 allocation decreases.
- Engine: `EdgeFadeLayerClear.Compute` is a `static` over `in`/`out` scalars — no capture, no boxing, no allocation. The `D3D12_RECT` is `stackalloc`; `ClearRenderTargetView` with a rect count is a native call. Zero managed allocation on the submit path.
- Verdict: the alloc tripwire (`GC.GetAllocatedBytesForCurrentThread()` delta == 0 on phases 6-13) is preserved on both the headless record path and the D3D12 submit path.

## 7. Test / gate plan (headless, `src/FluentGpu.VerticalSlice/Program.cs`)
Asserts via `HeadlessGpuDevice.LastLayers` (`IReadOnlyList<PushLayerCmd>`) + `SceneRecordStats` + direct calls into `EdgeFadeLayerClear.Compute` on captured `PushLayerCmd`s.
- **gate.edgefade.downgrade** — a vertical `ScrollEl` with overflow, `AutoEdgeFade=false`, `EdgeCues=Fade`, over an opaque-fill ancestor: assert `stats.EdgeFadeGroupCount == 0`, `LastLayers` contains NO `Kind==LayerKind.EdgeFade`, and the DrawList contains the two surface-colour edge-cue `GradientRect` commands (top+bottom) carrying the resolved opaque surface colour.
- **gate.edgefade.explicit-kept** — the same scroller with `AutoEdgeFade=true` (Mode=Fade) still emits exactly one `Kind==EdgeFade` `PushLayerCmd` with `FadeEdges`/bands matching overflow (premium-path regression guard).
- **gate.edgefade.clear-fade-bounded** — for a Mode=Fade EdgeFade layer, call `EdgeFadeLayerClear.Compute` on the captured `PushLayerCmd`: assert `fullCanvas == false` AND the returned rect equals the `CompositeClip*scale` box clamped to canvas AND is a strict sub-rect of the full canvas (proves the clear is bounded, not whole-canvas). This is the deterministic headless replacement for the reviewer's deferred windowed smoke.
- **gate.edgefade.clear-blur-fullcanvas** — synthesize a `PushLayerCmd` with `BlurSigma>0` (Mode=FadeAndBlur) and call `Compute`: assert `fullCanvas == true` and the rect is the full canvas. **This is the BLOCKING-issue regression guard** — it fails if anyone re-scissors the clear for a blur-carrying edge fade.
- **gate.edgefade.alloc** — record a downgraded overflowing page twice; assert phase-8 GC-alloc delta == 0 (named tie-in to the existing tripwire).
- **gate.edgefade.clear-scissor-diag (windowed/`--screenshot`)** — with a small sidebar (Mode=Fade) EdgeFade, `Diag edgeFadeClearPx` ≈ composite-clip area, not canvas area (confirms the D3D12 plumbing honours the helper's decision end-to-end).
- **Visual (`--screenshot`) golden diff (validation.md):** (1) an artist-page-like page over an opaque plate renders the surface-colour cue and is pixel-close to the prior alpha fade (both dissolve into the same flat colour); (2) a lyrics-like scroller over a non-flat backdrop still shows the true alpha fade unchanged; (3) a scene with a Mode=FadeAndBlur edge fade renders identically before/after (blur clear unchanged).

## 8. Canon reconciliation
- Owning docs (SPEC-INDEX.md §2 / subsystems/README.md ownership map): `design/subsystems/controls.md §8.3` owns the AutoEdgeFade-vs-EdgeCue affordance contract — record the per-surface policy above. `design/subsystems/gpu-renderer.md` owns the `PushLayer{EdgeFade}` realization — amend the clear wording (scissored for `BlurSigma==0`, full-canvas for `BlurSigma>0`) and log the deferred items. No canonical VALUE (opcode shape, struct, seam) changes and no superseded token is introduced, so no `design/archive/` move is required.
- Run `powershell -File docs\design\check-canon.ps1` (expect exit 0) after the doc edits.

## 9. Edge cases
- **At-rest (non-scrolling) frames** on downgraded pages lose the true-alpha look and show the surface-colour cue instead; the cue only draws when `content > viewport` (SceneRecorder.cs:1663), so a page that fits shows nothing (correct — no overflow). Deliberate policy call under the one-experience rule.
- **Translucent plate** on a downgraded surface ⇒ `TryResolveCueSurface` self-skips ⇒ a MISSING fade, never a wrong-colour one. The five downgraded surfaces are verified on the opaque FileArea card; NowPlayingPanel (uncertain plate) is intentionally not downgraded.
- **Blur-mode edge fade** (none in app today): `Compute` returns `fullCanvas=true` ⇒ today's exact behaviour ⇒ no regression, no corruption.
- **CompositeClip degenerate** (zero W/H): `EdgeFadeComposite` already early-returns (line 616); `Compute` clamps `right>=left`, `bottom>=top`, so a degenerate clear rect is a no-op clear of the full-canvas-cleared-in-a-prior-lease slot — but the composite also no-ops, so nothing samples it. Safe.

## 10. Rollback story
- **App:** re-add `AutoEdgeFade = true` on any surface that looks wrong (e.g. a plate that turns out translucent, leaving no affordance) — a one-line revert per surface, independent per file. Because the cue self-skips rather than mis-renders, a bad downgrade is a *missing* fade, never a wrong-colour one — safe to ship incrementally.
- **Engine:** passing `null` at the EdgeFade Acquire call site (or having `Compute` always return `fullCanvas=true`) instantly restores today's full-canvas clear with zero other change. The app downgrade and the engine clear-scissor are independent and independently revertible.

## Part D — open questions

- Confirm WaveeColors.FileArea has alpha >= 0.985 (opaque). It is the plate all five downgraded pages dissolve into; if it is ever themed translucent, every downgraded page loses its cue at once. Cheap to assert in the gate.
- Confirm EpisodeList and LibraryPage compact-episodes are always hosted inside the FileArea ContentHost card (not a translucent flyout/rail) in every route that shows them — verified by construction here (they are content-region pages) but worth a live check.
- Should the five downgraded pages opt into FadeAndChevron for a stronger overflow affordance now that the alpha fade is gone, or stay Fade? Taste call for the one-experience owner.
- Should NowPlayingPanel's rail plate be made explicitly opaque (or expose a resolved Fallback) so it too can downgrade later? Would require RightRail.cs:62/84 to guarantee an opaque plate; out of scope here.

## Part D — reviewer notes

**Blocking issues found on the pre-revision design (fixed above; the named gates guard the regression):**

- The secondary engine change (scissor the layer's ClearRenderTargetView to the composite-clip box for L.Kind==EdgeFade) breaks correctness for blur-mode edge fades, and the design's central safety claim — 'clear-box == composite-read-box, so every sampled texel was cleared then written' — is provably false for that mode. Verified path: EdgeFadeMode is {Fade, Blur, FadeAndBlur} (Effects.cs:128). SceneRecorder.cs:581 emits the PushEdgeFadeLayer with a nonzero BlurSigma whenever Mode != Fade. In that case PopLayer calls BlurInPlace on the group RT (D3D12Device.cs:1518-1519) BEFORE EdgeFadeComposite reads it. BlurInPlace's sample/scissor region is RegionBox = DeviceRect inflated by ±min(ceil(3σ),32) px (OpacityLayerCompositor.cs:670-677), which extends beyond the composite-clip box (CompositeClip = deviceBounds ∩ clip ⊆ deviceBounds; the halo sticks out up to 32px past it). The subtree draws into that halo region under the ENCLOSING clip (⊇ box) with SourceOver, but a scissored clear zeroes only the box — so the halo composites over stale content left in the pooled RT slot from a prior frame/layer lease (pool reuse is real: Acquire, OpacityLayerCompositor.cs:529-576). The Gaussian then pulls that garbage into the box that EdgeFadeComposite samples, smearing corruption into the visible fade. This is a correctness break in a SHARED engine primitive backed by an incorrect universal-safety proof; no proposed gate would catch it (gate.edgefade.compositeclip-bounded and the golden all assume Mode=Fade).
  - *Fix adopted:* Only scissor the clear when the EdgeFade layer carries NO blur: in the D3D12Device PushLayer branch, pass the composite-clip clearRect to Acquire iff gl.BlurSigma == 0f, and pass null (full-canvas clear = today's behavior) when BlurSigma > 0f. Alternatively inflate the clear box by the same ±min(ceil(3σ),32)px halo RegionBox uses (and clamp to canvas) so the blur never taps uncleared texels. Add a headless gate asserting the emitted clear/halo relationship for a Mode=FadeAndBlur EdgeFade, not just Mode=Fade. Note this is a targeted guard, not a redesign — the primary app-side fix is independent and fully sound; only the engine clear-scissor as-written is unsafe.

**Minor issues (address during implementation):**

- clear-box == composite-box equality is asserted but not proven: the design computes the clear box at PushLayer time using CurrentScissorRect() and the composite box at PopLayer time using CurrentScissorRect() (D3D12Device.cs:1527). The shared EdgeFadeCompositeBox helper only guarantees identical MATH given identical inputs — it takes the clip as a parameter computed independently at each site. For balanced push/pop the scissor stack should restore to the same enclosing clip, but the design does not establish this; if the two differ the composite could sample outside the cleared box even in the plain-fade case.
- No headless coverage of the actual engine safety property: gate.edgefade.clear-scissor is explicitly deferred to a windowed/--screenshot Diag-counter smoke ('not headless-observable in the RHI-headless seam'). The clear-extent-vs-composite-read invariant — the whole point of the engine change — thus has no deterministic gate in Program.cs, so a future blur-mode edge fade (or a scissor-drift regression) ships untested.
- NowPlayingPanel line references in the section-4 file list are imprecise: it cites 'the hero EdgeFade (line 122)' and 'the section EdgeFade (line 203)', but grep shows the explicit section EdgeFade at NowPlayingPanel.cs:183 (EdgeMask.Bottom, 56f) and no AutoEdgeFade/EdgeFade at 122. The outer-ScrollView deletion target (line 75) is correct; the inner-fade line numbers are wrong.
- Missing-affordance risk on translucent-plate surfaces is flagged as an open question but not verified for NowPlayingPanel's right rail, EpisodeList, and LibraryPage compact-episodes. If any of those scrollers has no opaque self/ancestor fill (e.g. a Mica-passthrough rail), TryResolveCueSurface self-skips (SceneRecorder.cs:1648) and the downgraded surface loses its edge affordance entirely. Safe (never wrong-colour) and per-surface revertible, but a visible regression that the design leaves unconfirmed for 3 of the 6 downgraded surfaces.

---

# Part E — Stop pricing full re-records by the whole page: measured-shelf virtualization + a conservative recorder subtree cull

> **Review outcome:** passed adversarial review (sound as written). **Estimate:** L — the recorder cull is a small code delta but delicate (correctness hinges on the dirty-bit + translation predicate and the culled-prior reuse guard) and needs the full gate battery incl. a golden byte-identical check; the measured-virtual shelf is a moderate self-contained control change reusing VirtualBody; app-side caps are trivial. Most of the size is verification/canon, not lines of code.

**Summary.** Two complementary changes remove the "whole artist page priced on every kill-switch frame" cost. (1) A recorder subtree cull: `SceneRecorder.Walk` currently recurses into every child unconditionally, so an off-screen card subtree is fully walked (and pays two span-signature computations per node) even under a global span-reuse disable. We add an early-out that skips a subtree proven entirely outside the active clip by its PRIOR-frame device-space `SubtreeBounds` (already stored per node in `SpanTable`), translated by this frame's pure-translation world delta — gated by the same cleanliness predicate the translated-span-reuse path uses, so it is safe even when `spanReuseDisabled` is set (an off-screen subtree emits nothing regardless of why reuse was globally killed). This bounds re-record cost to the viewport, not the page. (2) Measured-shelf virtualization: `PagedShelf(measured:true)` realizes ALL cards to get an engine-measured uniform height; we keep the exact realize-all path only for short shelves and, above a threshold, switch to a measured-VIRTUAL body that measures a bounded off-screen sample of cards at the fitted width, locks that height, then drives the existing `FillRowVirtualLayout`/`ItemsView` virtualized strip (recycling, pager glide, edge fade) — so the scene holds ~one window of cards, not thousands. The cheap complement is app-side count caps on the cosmetic artist shelves. The cull bounds RECORD cost; virtualization bounds NODE/LAYOUT/reconcile cost; together the page is no longer priced whole.


# E — Shelf virtualization + recorder subtree cull

## Problem recap (re-verified in code)

- `SceneRecorder.Walk` (`src/FluentGpu.Engine/Render/SceneRecorder.cs`) recurses into **all** children at ~L912-928 with no subtree cull. `overlapsClip` (L438) gates a node's OWN emission (shadow/fill/border/arc/edge-fade/own-visual at L599-660) and there is even a per-node own-draw cull counter (`stats.CulledNodeCount`, L659) — but the **child loop runs regardless**, so an off-screen subtree is fully descended and each node computes `ComputeSpanInputSig` + `ComputeSpanMoveSig` (L449-454).
- The exact/translated span-reuse fast paths (L461-510) that would avoid re-walking a clean subtree are **all gated behind `!spanReuseDisabled`** (L461, L478). On the artist-page fling, `AppHost` sets `spanReuseDisabled` **every frame** (`SceneChanged` from a LazyGrid re-render, `Layout` from the ensuing relayout — AppHost L1604-1606), so the reuse paths are dead and the whole page re-walks.
- `PagedShelf.MeasuredBody` (`src/FluentGpu.Controls/PagedShelf.cs` L200-224) lays out **every** card in one flex row to get the tallest-wins uniform height. `ArtistPage.Shelves.cs` passes `measured:true` for videos, playlists, concerts, merch, gallery, related, fans — so each shelf is O(count) scene nodes, and every kill-switch frame is priced by the full page.

`childClip` is `ancestorClip ∩ thisNode'sClip` only when the node clips (L619-624); a **non-clipping** node lets children escape its box, which is why a cull must use a *subtree* bound that already captures escaping descendants — not the node's own model bounds.

---

## Part 1 — Recorder subtree cull

### Mechanism

`SpanTable` already stores, per node, the prior frame's `World` and `SubtreeBounds` (device-space union of the node's own device bounds plus every emitted descendant's device bounds — set at record time from `result.SubtreeBounds`, `SceneRecorder.cs` L985-995). That is exactly a *conservative, composited* subtree extent: it includes shadow halos, focus rings, edge-fade device rects, and any non-clipped child that drew outside the parent box. We reuse it as the cull bound.

Add, at the top of `Walk` **inside** the existing `if (spans is not null)` block, right after `recordDirtyBits`/`descendantDirtyBits`/`directMovingScrollContent` are computed (currently L455-457) and **before** the exact-copy attempt (L461):

```
// Off-screen clean-subtree cull. An off-screen subtree emits nothing, so skipping its WALK is safe under ANY
// spanReuseDisabled reason (unlike byte reuse). Trust the prior device SubtreeBounds only when the subtree
// geometry is unchanged (clean self + clean descendants) and the world delta is a pure translation.
if (EnableSubtreeCull
    && recordDirtyBits == 0
    && descendantDirtyBits == 0
    && spans.TryGetSubtree((int)node.Raw.Index, node.Raw.Gen, spanFrame, out var priorWorldC, out var priorSubtreeC)
    && TryTranslationDelta(in priorWorldC, in world, out float cdx, out float cdy))
{
    RectF curSubtree = TranslateBounds(priorSubtreeC, cdx, cdy);
    if (!curSubtree.Overlaps(clip))
    {
        spans.StoreCulled((int)node.Raw.Index, node.Raw.Gen, spanFrame, world, curSubtree);
        stats.NodesCulled++;                 // subtree roots skipped
        var culled = new SpanRecordResult();
        culled.Include(curSubtree);          // keep the parent's subtree-bounds chain correct
        return culled;
    }
}
```

Notes on each clause (why it is exactly the safe set):

- **`recordDirtyBits == 0`** — the node's own content (fill/size/paint) is unchanged this frame. A size change sets `LayoutDirty`→`RecordDirtyContent` (`SceneStore.Mark`, L926), so a resized node is never culled.
- **`descendantDirtyBits == 0`** — `RecordDirtyDescendantBits` (up-propagated aggregate, `MarkRecordDirty` L857-884) is zero, i.e. **no descendant** had a content OR transform change: no descendant mounted/unmounted/resized (Content) and none moved by its own transform write (Transform). A scale/opacity/translate animation writes `TransformDirty` every tick (`Mark`, L925), which up-propagates as a descendant-Transform bit — so an animating subtree is never culled. A newly-pinned sticky child or a starting drag ghost writes a transform → dirty → not culled.
- **`TryTranslationDelta`** — the node's own world differs from last frame by a **pure translation** (the scroll delta, or an ancestor-relayout shift). A scale/rotate change fails this test, so a subtree that a scale animation could grow *into* the clip is never culled via a translated bound. This is the same helper the translated-span path uses (L488).
- **`!curSubtree.Overlaps(clip)`** — the translated conservative subtree extent is entirely outside the active clip ⇒ node and every descendant emit nothing ⇒ skip.

The self `TransformDirty` bit on the node itself is *allowed* (the node is the thing translating); it is captured by the world delta. `scrollInMotion` is deliberately **not** a guard: an off-screen card during a fling is the prime target — as it scrolls in, its translated prior bound overlaps `clip`, the cull declines, and the node is walked+emitted normally.

### SpanTable changes (`SpanTable.cs`)

Additive; DrawSpan shape unchanged (it is near-canon).

- Add a `bool[] _culled` column (allocated in ctor, grown in `EnsureCapacity`).
- `Store(...)` sets `_culled[nodeIndex] = false` (a fully recorded node has a valid byte range).
- New `StoreCulled(int nodeIndex, uint gen, uint frameId, Affine2D world, RectF subtreeBounds)`: sets `_gen/_frame/_world/_subtreeBounds` and `_culled = true`, and registers the node as "recorded this frame" (`_frame[idx]=frameId`) so the cull **chains** frame-to-frame in O(1) while the subtree stays off-screen. Byte/sort offsets are left stale and must never be copied.
- New `TryGetSubtree(int nodeIndex, uint gen, uint frameId, out Affine2D world, out RectF subtreeBounds)`: same gen + `_frame == frameId-1` validation as `TryGet`, **ignores `_culled`** (a culled prior still has a valid bound), returns world+bounds only.
- Guard byte reuse: `TryGet` and `TryGetTranslated` return `false` when `_culled[nodeIndex]` (a culled prior has no byte range to memcpy). This is the correctness gate for scroll-in recovery: the first on-screen frame declines the cull (overlap), the reuse paths reject the culled prior, so the node fully **re-records** (fresh bytes, `_culled=false`), and subsequent on-screen frames reuse normally.

### Stats (`RecordAccumulator`/`SceneRecordStats`/`FrameStats`)

Add `NodesCulled` (subtree roots skipped). `NodesVisited` continues to count only walked nodes, so the drop is directly observable. Surface `NodesCulled` on `SceneRecordStats` and thread it to `FrameStats` alongside `NodesVisited`.

### Feature switch / rollback

Gate the early-out on `private const bool EnableSubtreeCull = true;` in `SceneRecorder`. Flipping it to `false` makes `Walk` fall back to full recursion with zero other behavioral change; `StoreCulled`/`TryGetSubtree` are then never called and the `!_culled` reuse guard is inert (nothing is ever culled). This is a compile-time correctness switch, **not** a user-facing quality knob (allowed by the "one experience" rule).

### Why device-space `SpanTable` bounds, not a new scene column

A layout-maintained subtree-bounds column would live in **model/content** space and cannot know per-descendant composited transforms (scale animations, shadow/blur halos, focus rings, edge-fade device rects) — it would **under-cover** and risk clipping real pixels. It also imposes a new up-propagation invariant on every arrange. The `SpanTable` bound is already the exact post-composite device extent, is refreshed every recorded frame, and composes with the existing `TryTranslationDelta`/`TranslateBounds` machinery. The only cost is that a node not recorded last frame (first frame / just-mounted / just-scrolled-in) has no prior bound and falls back to a full walk — which is correct and identical to how span reuse bootstraps (`SpanTable.HasPrior`).

---

## Part 2 — Measured shelves without realizing the whole collection

### Mechanism

Keep `measured:true` meaning "the engine measures the card; you don't pass `cardHeight`", but stop realizing all N. In `PagedShelfCore.Render`, when `_measured`:

- **`count ≤ MeasuredRealizeAllCap`** (default `= 2 * perPageColumns`, min 8): keep today's `MeasuredBody` (realize-all, tallest-wins, exact). For a handful of cards, laying them all out is cheapest and virtualization saves nothing. Regression-safe for short shelves and their `ScrollMeasuredViewport` pager glide.
- **else**: new `MeasuredVirtualBody`:
  1. A `Signal<float> _measuredH` field (init 0), plus `float _measuredForCardW = NaN`.
  2. While `_measuredH.Value <= 0 || _measuredForCardW != cardW`, mount a **measure probe**: an off-record host (`Visible=false`, `Width=0` main-axis so it costs no visible space, but still laid out) containing a bounded sample of real cards — `min(count, MeasuredSampleCap)` cards (default `MeasuredSampleCap = 12`), each `new BoxEl { Direction=1, Width=cardW, Children=[_cardAt(i, cardW)] }` (mirrors the real cell). A `UseLayoutEffect` reads each sample card's measured `scene.Bounds(...).H`, takes the max, writes `_measuredH.Value = max` (grow-only) and `_measuredForCardW = cardW`, then flips a `UseState` that **unmounts the probe** — so steady-state scene holds no probe nodes. Re-armed when `cardW` changes.
     - `Visible=false` ⇒ `Walk` returns immediately (L334), so the probe is never recorded; it is only *measured* by layout. It resolves inside the cold realize-after-layout passes AppHost already runs (L1456-1463), so the first presented frame has a real height.
  3. Once `_measuredH.Value > 0`, render the existing `VirtualBody(perPageItems, cardW, p, w, fade)` with `_cardHeight` internally bound to `_ => _measuredH.Value`. This reuses the entire virtualized path verbatim: `FillRowVirtualLayout`, `ItemsView.Create`, recycling, `_ctl.StartBringItemIntoView` pager glide (L271), `PartViewport` template parts, and `EdgeFadeSpec`.

`VirtualBody` already computes `shelfH = _rows * cardHeight(cardW) + (_rows-1)*_gap` (L273); the only change is that `_cardHeight` may now come from the internal probe rather than the caller.

### Handling the specifics called out

- **SkeletonProxy** (`PagedShelf.Create ... with { SkeletonProxy = ... }`, L91): unchanged. It renders up to 6 real cards during async load, before the component mounts, and is unrelated to the internal probe.
- **Pager glide `StartBringItemIntoView`**: measured-virtual uses `VirtualBody`'s existing `_ctl.StartBringItemIntoView(p*perPageItems,0f,animate:true)` (L271) — full parity with the plain virtualized shelf.
- **TemplateParts on the viewport**: measured-virtual applies `PartViewport` to `VirtualBody`'s `BoxEl` (L293). `ArtistRailEdgeFadeParts` in `ArtistPage.Shelves.cs` sets `EdgeFade` on **both** the `ScrollEl` and `BoxEl` variants (L26-27), so it already works for the virtual body.
- **`cardHeight(cardW)` (width-dependent height)**: measured-virtual measures at the current fitted `cardW` and re-measures on `cardW` change; callers still never pass `cardHeight` for `measured:true`. The plain virtualized path (callers that DO pass `cardHeight`, e.g. `ArtistPopular`) is untouched.
- **Variable-height cards**: for uniform, width-driven cards (every current shelf: square cover + fixed `MaxLines` text) the sample is exact. If a card can exceed the sample (rare wrapping), an optional second-order safety is to also grow `_measuredH` from the live realized window's max card height (grow-only lock) — listed as an open question; not required for the current card set.

### App-side complement (the cheap half) — `ArtistPage.Shelves.cs`

The truly cosmetic, always-short shelves should just **cap their realized count** app-side and stay on the exact realize-all measured path (now cheap because bounded):

- **Cap app-side (bounded realize-all):** on-tour banner (already 1), **music videos**, **playlists**, **upcoming concerts**, **merch**, **gallery** — pass `min(list.Count, Cap)` (Cap ≈ 12–20; Spotify returns ≤ ~10–20 for these). One-line `Math.Min` at each `PagedShelf.Create(count: …)` call plus indexing the capped range.
- **Let virtualize (measured-virtual):** **related / "fans also like"** — these can be 20+ and page the most, so they cross `MeasuredRealizeAllCap` and get the measured-virtual body automatically.

Capping is per-call data trimming, not a quality knob.

### Rollback

Setting `MeasuredRealizeAllCap = int.MaxValue` reverts every `measured:true` shelf to today's realize-all `MeasuredBody`. App-side caps are per-call literals, trivially removed.

---

## Interaction with span reuse / scroll integrator / reconciler

- **Span reuse**: the cull is a strict superset-safe *sibling* of the reuse paths — it runs *before* them and only for off-screen subtrees, using the identical cleanliness predicate + `TryTranslationDelta`. It fires precisely when reuse cannot (global `spanReuseDisabled`), because skipping an off-screen walk is valid regardless of *why* reuse was disabled. The `!_culled` guard prevents a culled prior (no valid bytes) from being memcpy'd on scroll-in.
- **Scroll integrator**: during a fling the content node is `TransformDirty` (self-transform) and off-screen cards are clean; their world delta equals the scroll offset delta (pure translation) → they cull and chain in O(1) per frame. Entering-edge cards fail the overlap test and are walked+emitted correctly. `scrollInMotion` is intentionally not a cull guard (it only blocks byte reuse of *visible* moving content).
- **Reconciler**: virtualization means each measured-virtual shelf realizes ~one window of cards, so far fewer scene nodes for the reconciler to diff/patch and for scoped relayout to solve. The cull additionally makes even a still-realized short shelf's off-screen portion free to record.

## Allocation analysis (phases 6–13)

- Cull path: `SpanTable.TryGetSubtree` (array reads), `TranslateBounds` (returns a struct), `RectF.Overlaps`, `StoreCulled` (array writes into preallocated/`EnsureCapacity`-grown arrays; growth happens outside the steady hot loop), `SpanRecordResult` (struct). **Zero managed allocation** in record. It strictly *reduces* `NodesVisited` and DrawList bytes.
- Measure probe: mounts only on cold frames / `cardW` change (bounded sample), never on steady scroll frames — it is a cold reconcile/layout edge, exactly like today's realize-all mount, and unmounts after resolve. Steady record/scroll frames touch no probe nodes.
- Compatible with the render-thread seam: the cull reads scene + `SpanTable` and writes `DrawList`/`SpanTable`/stats — the same state the recorder already owns on the record thread; no new UI/render crossing.

## Test / gate plan (headless, `FluentGpu.VerticalSlice/Program.cs`)

- `gate.recorder.subtree-cull-offscreen`: a tall column with an on-screen band and a large off-screen band of child boxes; on the steady post-scroll frame, force `spanReuseDisabled` (a sibling re-render) yet assert `NodesCulled > 0`, `NodesVisited` drops by ~the off-screen count, the on-screen band's DrawList is byte-identical to the `EnableSubtreeCull=false` baseline, and `HotPhaseAllocBytes == 0`.
- `gate.recorder.subtree-cull-safety-dirty`: mutate a child fill inside an otherwise off-screen-bounds subtree; assert that subtree is NOT culled (walked, `RecordDirtyContent` present) and pixels match the full walk.
- `gate.recorder.subtree-cull-scale-escape`: a subtree whose root runs a scale animation that grows its bounds into the clip; assert `TryTranslationDelta` fails ⇒ not culled ⇒ drawn.
- `gate.recorder.subtree-cull-scroll-in-recovers`: cull a subtree, then scroll it back on-screen; assert the first on-screen frame re-records it fully (culled prior rejects byte reuse) and pixels are correct (no stale copy).
- `gate.recorder.subtree-cull-firstframe-fallback`: with no prior span (first record), assert no cull and correct full output (bootstrap).
- `gate.shelf.measured-virtual-bounded`: `PagedShelf(measured:true)` with 500 items realizes only ~`perPage+overscan` card subtrees (bounded scene-node count, not 500) and the viewport height equals the probe-measured card height, matching the realize-all height for the same card within tolerance.
- `gate.shelf.measured-virtual-pager-glide`: chevron-next on the measured-virtual shelf arms `StartBringItemIntoView`, realizes the next window, `HotPhaseAllocBytes == 0` on the steady glide frame.
- `gate.shelf.measured-realizeall-smallcount`: `count ≤ MeasuredRealizeAllCap` still uses realize-all measured (exact tallest-wins) — short-shelf regression guard.

## Canon reconciliation

- **`design/subsystems/gpu-renderer.md`** (owner of the recorder/DrawList/span machinery): document the off-screen clean-subtree cull as an extension of the span-reuse family, plus the new `NodesCulled` stat and the `_culled`/`StoreCulled`/`TryGetSubtree` `SpanTable` surface.
- **`design/SPEC-INDEX.md` §2**: add a sub-clause under the clean-span-reuse validity contract — "an off-screen clean subtree (same cleanliness predicate + pure-translation world delta + trustworthy prior device-space `SubtreeBounds`) may be SKIPPED from the walk entirely; a skipped span is non-reusable next frame."
- **`design/subsystems/virtualization.md`** and the `PagedShelf` section of **`design/subsystems/controls.md`**: `measured:true` above `MeasuredRealizeAllCap` now virtualizes with a sample-measured, locked cross-axis height instead of realizing all cards.
- Run **`docs/design/check-canon.ps1`** after the doc edits (exit 0).


## Part E — open questions

- Exact values: MeasuredRealizeAllCap (default 2*perPage, min 8), MeasuredSampleCap (12), and per-shelf app-side caps (12–20) need product sign-off.
- Should measured-virtual also grow _measuredH from the live realized window's tallest card (second-order robustness for variable-height cards), or rely solely on the bounded probe?
- Should the cull additionally hard-exclude any subtree containing a Scrollable descendant as belt-and-suspenders, or is the descendant-transform-dirty gate sufficient (analysis says sufficient — a moving inner scroller marks transform-dirty)?
- Confirm the DrawList prior-frame arena survives one frame for the translated-reuse path only (the cull copies no bytes, so it is unaffected either way) — verify no shared-buffer aliasing when a node transitions culled→recorded.

## Part E — reviewer notes

**Minor issues (address during implementation):**

- Part 2 overclaims cold-frame timing. The probe writes _measuredH.Value in a UseLayoutEffect (DrainLayoutEffects, AppHost L1466), which is a SIGNAL write that schedules a re-render for the NEXT frame — it does NOT resolve within the same frame's realize-after-layout loop (L1456-1463, which only re-runs ReRealizeVirtuals for virtualized viewports, not arbitrary mounted subtrees). So the claim 'the first presented frame has a real height' is false: frame 1 renders only the invisible probe (blank shelf), frame >=2 renders VirtualBody. With _w bootstrapping (0 -> real width via OnBoundsChanged) it can be 2-3 cold frames. Acceptable behind skeletons, but the design must state what renders during the cold frames instead of claiming zero delay.
- Probe space-consumption is mis-specified. The design says 'Visible=false, Width=0 main-axis so it costs no visible space, but still laid out' — but the shelf root is a vertical column (Direction=1), whose MAIN axis is height, not width. A Width=0 probe row still contributes its natural card HEIGHT to the column, inserting a ~cardHeight blank gap for the cold frame(s). The intent (zero space) is fine and trivially achievable (Height=0 overflow-visible host, an absolute/overlay host, or a detached measurement), but as written the probe would flash a gap. Specify the zero-extent host mechanism.
- Cull placement forgoes a signature saving. The early-out is inserted after ComputeSpanInputSig/ComputeSpanMoveSig (L449-454), so every off-screen cull-ROOT still computes two span signatures; only skipped descendants avoid them. The summary's framing ('pays two span-signature computations per node' being eliminated) applies only to descendants. Moving the cull above L449 (it needs only the dirty bits + world, not the sigs) would also save the root's two hashes. Not incorrect, just a left-on-the-table micro-win.
- Cull is not gated on spanStoreOn. When overlays/orphans/drag-ghost/popup/detached are present, spanStoreOn=false (L183) and no node stores its span, yet the design's early-out (gated only on `spans is not null` + EnableSubtreeCull) would still call StoreCulled — writing _frame=thisFrame for culled nodes while walked siblings keep 2-frames-stale entries. Harmless (each node's TryGetSubtree is validated independently by _frame==frameId-1, so mixed staleness only declines culls), but the design should either gate the cull on spanStoreOn or explicitly note the mixed-state is benign.
- Safety-gate coverage gap. gate.recorder.subtree-cull-safety-dirty tests a child FILL mutation but not the MOUNT of a new descendant into an off-screen-bounds subtree. Mount is safe in practice (a new child sets LayoutDirty -> RecordDirtyContent -> up-propagates a descendant-content bit -> parent not culled), but since that up-propagation is the exact invariant the cull's descendant-dirty gate relies on, add an explicit mount/unmount-into-offscreen-subtree gate case.
- Resize-time probe churn. 'Re-armed when cardW changes' means a continuous window-drag resize re-mounts/unmounts the probe on every cardW step, adding bounded reconcile churn to the shelf. Already-expensive (Resize sets spanReuseDisabled + full layout) so not the fling path, but worth debouncing or measuring the sample once per settled width.
- SubtreeBounds excludes shadow/focus/edge-fade halos (result.Include only unions the node box at L440, not the emitted halo rects). This means a subtree whose BOX is offscreen but whose downward shadow sliver pokes into the clip would be culled with its shadow dropped. This is NOT a regression — the existing per-node walk gates shadow on the identical box-overlap `overlapsClip` (L599), so today's recorder already drops that shadow sliver — but the design should state the equivalence explicitly so a future reviewer doesn't read it as new breakage.

---

# Part B — Scope the SceneChanged/Layout/ImageContent span-reuse kill switches to per-node record-dirty — with a presented-size signature fold and a per-frame reveal-mark pass built on the reconciler's EXISTING image-node registry

> **Review outcome:** REVISED after adversarial review (blocking issues fixed in this body). **Estimate:** M — smaller than the reviewed design because the hard part (image scoping) reuses infrastructure that already exists and is already correct (the reconciler's _imageNodes registry + public MarkImageDirty, maintained at both bound and unbound sites and already wired to decode-landing), so no new SceneStore registry, no RegisterImageNode hook, and NO reconciler edit are needed. The release diff is: two MixFloat additions to two sig functions + call sites; a small ImageCache active-reveal id list + one MarkActiveReveals method; an AppHost rewire behind FG_SPAN_SCOPE + a cached delegate. The remaining size is the correctness apparatus — the DEBUG record-twice shadow harness (now correctly isolated via spans:null) and ~12 headless gates (three discriminators, incl. the bound-source stationary-fade case the reviewed design failed) — plus a multi-screen shadow soak before flipping. Not L, because the novel/fragile new-registry work the previous design carried is eliminated."

**Summary.** The engine already gates span reuse per node: an in-place reconciler write marks PaintDirty (Reconciler.cs:2581) and a structural change marks the parent LayoutDirty; both set RecordDirtyContent and up-propagate a self/descendant record-dirty aggregate to the root, which SceneRecorder reads per node and uses to early-return a clean subtree in one Array.Copy without recursing (SceneRecorder.cs:455-476/478-508). Above that sit three window-global kill switches in AppHost (reconciled=>SceneChanged, layoutNeeded=>Layout, image epoch/fade=>ImageContent, AppHost.cs:1605-1613) that blanket-force a full re-record. SceneChanged and Layout are redundant with the per-node marks; removing them needs one closer — the node's own W/H is not in the span signature (only world/X/Y is), so a stretch with unchanged X/Y would reuse a stale span — fixed by a read-only fold of the presented size (pw/ph, already computed at SceneRecorder.cs:431-432) into both signatures, which declines exact+translated for a genuinely resized node while pure position shifts still translate-copy. ImageContent is the hard case: the reveal crossfade advances via a clock (ImageCache.Tick) with no scene mutation, so a scoped ancestor exact-copies and never walks down to the fading image — a signature-only approach FREEZES the fade. The corrected fix reuses infrastructure that already exists and is already correct: the reconciler's _imageNodes registry (imageId->nodes, maintained at BOTH the bound-source effect and the unbound WriteColumns site, plus activation and unmount) and its public self-compacting MarkImageDirty(imageId) — already wired to ImageStatusChanged for decode-landing. We add a small maintained active-reveal id set to ImageCache and, on frames the existing O(1) HasActiveCrossfades/epoch flags already flag, iterate it and call MarkImageDirty per fading id (pruning on settle), forcing each fading image's ancestor chain to re-walk. This dissolves the reviewer's blocking issue (a new node-keyed registry with a fragile hook is not built at all), scopes the scan to the handful of covers actually fading (not all image nodes), and lands behind a record-twice byte-identity shadow assert and a one-line FG_SPAN_SCOPE rollback.

## What changed vs the reviewed design, and why

The adversarial review found one blocking issue and four minor ones. I verified every claim against the code. Verdict:

- **Blocking issue — ACCEPTED as a diagnosis, but the fix is to delete the mechanism, not patch it.** The reviewer is correct that bound-source images (every LazyGrid/artist-page cover) set `ImageId` inside the bind Effect at `Reconciler.cs:1157`, which never re-enters `WriteColumns`; at `WriteColumns` line 2409 where `paint.VisualKind = VisualKind.Image` is assigned, a bound node's `ImageId` is still `0` (the unbound request block at 2421-2432 is gated `if (!im.Source.IsBound)`). So the original design's "register on non-zero `ImageId` at the `WriteColumns` chokepoint" hook would never register bound covers, and their fades would freeze under a clean stationary ancestor — reintroducing the exact Design-2 failure the original correctly refuted. **However, the original design's entire premise — that a *new* image-node registry must be built in `SceneStore` — is wrong.** The reconciler **already owns** a complete, correctly-maintained image-node registry and mark method (verified below), so the corrected design reuses it and builds no new registry. The blocking issue evaporates because the fragile hook does not exist.
- **Minor #1 (shadow-harness `SpanTable` isolation) — ACCEPTED.** `SceneRecorder.Record` calls `spans.BeginFrame(...)` (line 166), which double-buffers the table; a second in-frame pass against the same `_spanTable` corrupts it. Fixed by making the authoritative pass use `spans: null` (a full, reuse-free walk that touches no span table — see §1d).
- **Minor #2 (scan cost O(all image nodes)) — DISSOLVED by the new mechanism.** We iterate only the *active-reveal id set* (the covers currently fading), not every registered image node. On the artist page this is the handful of on-screen covers mid-reveal, not the thousands the PagedShelf realizes.
- **Minor #3 (canon ownership of `ImageCache`) — ACCEPTED and folded into §7.** `ImageCache.cs` physically lives in `src/FluentGpu.Engine/Scene/` (namespace `FluentGpu.Scene`); the doc-reconcile list names both `media-pipeline.md` and `scene-memory.md` and defers the final owner to `design/subsystems/README.md`.
- **Minor #4 (`TextureMs==NaN` predicate edge) — DISSOLVED.** We no longer scan nodes with a `CrossFadeOf < 1` predicate. A reveal id enters the active set exactly when `TextureMs` is *set* (`NoteCrossfadeDeadline`, `ImageCache.cs:300`, called from the LQIP and full-res landing paths at 475), and `CrossFadeOf` returns `1f` for `NaN TextureMs` (line 289), so the not-yet-revealing / evicted / failed windows are handled by the existing `ImageStatusChanged -> MarkImageDirty` (decode landing) and self-prune, respectively.

### The load-bearing discovery: the registry already exists

`grep` for `PinImageNode`/`_imageNodes`/`MarkImageDirty` (all in `Reconciler.cs`) proves it:

- `_imageNodes` — `Dictionary<int, List<NodeHandle>>`, **"imageId -> nodes that pinned it (for status->dirty)"** (Reconciler.cs:120).
- Maintained at **every** image-id assignment site: the **bound-source effect** (`UnpinImageNode`/`PinImageNode`, lines 1156/1158), the **unbound `WriteColumns` case** (2427/2429), the KeepAlive **activation toggle** (`SetSubtreeResourcesActive`, 951/952), and **unmount teardown** (1760). `PinImageNode -> TrackImageNode` (992-999/1015-1024) is idempotent (append-if-absent); `UnpinImageNode -> UntrackImageNode` (1002-1013/1026-1034) removes.
- `public void MarkImageDirty(int imageId)` (1037-1051) marks every live node whose `Paint(node).ImageId == imageId` `PaintDirty`, and **self-compacts** dead/repurposed entries (1043-1046) — precisely the correct predicate and cleanup the original design tried to re-implement in `SceneStore.MarkFadingImages`.
- It is **already wired for decode-landing**: `AppHost.cs:1006-1008` subscribes `_images.ImageStatusChanged += (id,_,_,_) => _reconciler.MarkImageDirty(id)`. So a Ready/Failed flip already marks its nodes today, per-node, with no global flag.

The **only** missing dirtying is the per-frame *crossfade advance*: `ImageCache.Tick(dtMs) => _clockMs += dtMs` (line 282) mutates a clock, not the scene; `CrossFadeOf` reads `_clockMs - e.TextureMs` (287-291). No scene mutation per fade frame is exactly why the global `ImageContent` kill exists (AppHost:1611-1613). The corrected fix closes that one gap by calling the **existing** `MarkImageDirty` for the **currently-fading** ids each fade frame.

---

## 1. Mechanism

### 1a. AppHost — stop globally disabling; drive a scoped reveal-mark pass instead
At `AppHost.cs:1604-1620`, gate the three scoped reasons behind a startup kill-switch and replace the `ImageContent` global with a mark pass over the active reveals:

```csharp
static readonly bool s_spanScope = Environment.GetEnvironmentVariable("FG_SPAN_SCOPE") != "0"; // default ON
// cached once at composition (method-group conversion allocates one delegate, never per-frame):
// _markImageDirty = _reconciler.MarkImageDirty;   // Action<int>

SpanReuseDisabledReason spanDisable = SpanReuseDisabledReason.None;
bool imageFadeActive = _images.HasActiveCrossfades;
bool imageContentChanged = _images.ContentEpoch != _recordedImageContentEpoch || imageFadeActive || _imageCrossfadeWasActive;

if (!s_spanScope)                                       // legacy global behavior — the rollback path
{
    if (reconciled)          spanDisable |= SpanReuseDisabledReason.SceneChanged;
    if (layoutNeeded)        spanDisable |= SpanReuseDisabledReason.Layout;
    if (imageContentChanged) spanDisable |= SpanReuseDisabledReason.ImageContent;
}
else if (imageContentChanged)
{
    // SCOPED: mark only the currently-fading image ids record-dirty (via the reconciler's EXISTING
    // _imageNodes registry + MarkImageDirty), so each fading image's ANCESTOR CHAIN re-walks and the image
    // re-records with the fresh crossfade byte. O(active-reveals) — the covers actually fading — zero-alloc.
    // No-op at rest (imageContentChanged is false). Decode-landing marks already fire via ImageStatusChanged.
    _images.MarkActiveReveals(_markImageDirty);
}

// genuinely global reasons ALWAYS contribute (unchanged):
if (resized) spanDisable |= SpanReuseDisabledReason.Resize;
if (keepAlive && _window.SizedInModalLoop) spanDisable |= SpanReuseDisabledReason.ModalPaint;
if (_popupWindows.Count != 0) spanDisable |= SpanReuseDisabledReason.PopupWindows;
if (_connected.Detached.Count != 0) spanDisable |= SpanReuseDisabledReason.Detached;
```

`reconciled`, `layoutNeeded`, `_recordedImageContentEpoch`, `_imageCrossfadeWasActive` stay computed exactly as today — they still drive layout (`layoutNeeded` at 1432), the settle-frame repaint latch (`_imageCrossfadeWasActive` at 1612/1620), and the skip-submit byte-hash gate (1642-1646). `spanStoreOn` (SceneRecorder.cs:183) never keyed on SceneChanged/Layout/ImageContent, so spans are still *stored*; the change is that they get *reused the same frame*. The `SpanReuseDisabledReason.{SceneChanged,Layout,ImageContent}` enum values remain (rollback path + `SceneRecordStats.SpanReuseDisabledReasons` diagnostics + the shadow harness). The mark pass runs pre-record (phase 6-7), before the `Record` call at 1614 and the `_scene.ClearRecordDirty()` at 1629 — identical timing to the existing `ImageStatusChanged -> MarkImageDirty` path, so it is render-thread-seam-safe (UI-side marks; record consumes).

**Skip-submit interaction (reviewer risk #6):** a pure fade sets no `reconciled`/`layoutNeeded`/`transformWrote`, so `maybeUnchanged` (1642) can be true and the frame is hashed. But the fade advanced the `crossFade` byte inside the image's `DrawImage` command, so `dlHash != _lastPresentedDrawListHash` -> `skipSubmit=false` -> the frame submits. On the settle frame the steady (`crossFade==1`) stream differs from the last presented (`<1`) frame, so it also submits; the frame after is byte-identical and correctly skips. No frozen fade, no wrongful skip.

### 1b. SceneRecorder — fold presented size into both signatures (the layout closer)
`pw`/`ph` are already computed at `SceneRecorder.cs:431-432` (`PresentedW/H` with `b.W/b.H` fallback) — the exact geometry the recorder draws into `local`/`pb` (436/433). Neither `ComputeSpanInputSig` (1002-1035) nor `ComputeSpanMoveSig` (1037-1069) folds them today: they fold `world` via `MixAffine`/`MixAffineLinear`, which carries translation (InputSig) and scale, but **not** the node's own width/height. Add two params and mix:

```csharp
// ComputeSpanInputSig and ComputeSpanMoveSig gain: float pw, float ph
MixFloat(ref h, pw);
MixFloat(ref h, ph);
```

Update the two call sites (449-454) to pass the already-computed `pw, ph`. Position (X/Y) is deliberately **not** added — it lives in `world` (InputSig, via `MixAffine`) and must stay out of MoveSig (`MixAffineLinear` omits translation) so pure translations remain translatable. Effect: a stretched/grown node's InputSig **and** MoveSig differ -> declines both exact (461) and translated (482) -> re-records at the correct size; a position-only shifted node's sigs are unchanged -> still translate-copies (the win over the alternative `SetArrangedBounds` mark, which would set `RecordDirtyContent` and block the translated path at 481). Two scalar `MixFloat`s per node, zero-alloc. Reachability: scoped relayout only runs from `LayoutDirty` roots (`_invalidator.RunDirty`, AppHost:1446), which are `RecordDirtyContent` and re-record; each resized descendant's sig differs so it too re-records and continues walking its children — every resized node is reached.

### 1c. ImageCache — a small maintained active-reveal id set + the mark driver
No new `SceneStore` registry. Add to `ImageCache` a compact id list of reveals in flight, maintained where the crossfade deadline is already tracked:

```csharp
private readonly List<int> _activeReveals = new(16);   // ids with an ENABLED reveal not yet settled (dedup on add)

// in NoteCrossfadeDeadline(Entry e) (line 300), which already runs wherever TextureMs is set (LQIP + line 475):
//   after the existing deadline fold, register the reveal for per-frame marking:
if (e.Transition.Enabled && !float.IsNaN(e.TextureMs) && !_activeRevealsContains(e.Id))
    _activeReveals.Add(e.Id);

/// <summary>Mark every image node holding a still-fading id record-dirty (via the reconciler's registry),
/// then prune settled ids. Called ONCE per frame by the host, only when a fade/decode is in flight.
/// Zero-alloc: index loop + swap-remove; the delegate is cached by the caller.</summary>
public void MarkActiveReveals(System.Action<int> mark)
{
    for (int i = _activeReveals.Count - 1; i >= 0; i--)
    {
        int id = _activeReveals[i];
        mark(id);                                   // re-record this fade frame (records fresh crossFade byte)
        if (CrossFadeOf(new ImageHandle(id)) >= 1f) // settled, evicted, or failed (NaN TextureMs -> 1f) -> stop marking
        {
            _activeReveals[i] = _activeReveals[^1];
            _activeReveals.RemoveAt(_activeReveals.Count - 1);
        }
    }
}
```

`Entry` needs to know its own id for the `NoteCrossfadeDeadline` add — it is keyed by id in `_byId` and already carries `Id` in the code paths (or pass `id` into `NoteCrossfadeDeadline`; the two call sites already have `id` in scope). The **settle frame is natural**: on the frame `crossFade` first reaches `1f`, the id is still in the set, so `mark(id)` records the final steady span *before* the id is pruned; the next frame it is absent, unmarked, and reuse resumes on the correct steady bytes. This precisely mirrors the `imageFadeActive || _imageCrossfadeWasActive` host latch (AppHost:1612) that keeps that settle frame alive. Staggered reveals settle independently — reveal A pruned at t1 while reveal B keeps marking to t2 — strictly better than the global kill, which re-recorded everything until the last fade ended.

**No new bookkeeping on the evict/fail/restart paths.** Those set `TextureMs = NaN` (ImageCache.cs:379/418/467/516); `CrossFadeOf` then returns `1f` -> the id self-prunes on the next `MarkActiveReveals`. A rare straggler (an id that leaves reveal while `HasActiveCrossfades` is already false, so the pass does not run) lingers harmlessly until the next fade frame prunes it; `mark` on a stale id is a no-op if no live node still holds it (`MarkImageDirty` drops it, 1043-1046) or a single harmless re-record at `crossFade==1` if one does. `_activeReveals` is bounded by concurrent reveals (dedup on add).

### 1d. Shadow harness (DEBUG, record-twice byte-identity) — with correct span-table isolation
In `AppHost` (or a `[Conditional("DEBUG")]` helper), when `FG_SPAN_SHADOW=1` and `s_spanScope` and `spanDisable` contains only scoped-eligible reasons (or None): after the real scoped record (candidate, into `_drawList` using the live `_spanTable`), run an **authoritative** pass into a reused DEBUG scratch `DrawList` with **`spans: null`**:

```csharp
// authoritative = a full, reuse-free walk (spans:null => dl.Reset(), no BeginFrame, no table interaction).
var gt = SceneRecorder.Record(_scene, _shadowDrawList, _images, in focus, Tok.ScrollThumb,
        Tok.AcrylicFlyout.Fallback, in textEdit, CollectionsMarshal.AsSpan(_popupSkipRoots),
        holdSelfBlurForAnyUserScroll: /*same*/, spans: null);
Debug.Assert(_drawList.Bytes.SequenceEqual(_shadowDrawList.Bytes)
          && _drawList.SortKeys.SequenceEqual(_shadowDrawList.SortKeys), ShadowDiff(_drawList, _shadowDrawList));
```

Using `spans: null` (not the live table with reasons force-added) sidesteps the reviewer's isolation defect entirely: the authoritative pass calls `dl.Reset()` (SceneRecorder.cs:160), never `BeginFrame`, so it cannot read or clobber the live `_spanTable`'s double-buffer. `spans: null` forces a full walk of every node (no exact/translated eligibility is even evaluated), which is byte-identical to what a globally-disabled frame emits. The scoped candidate must produce those same bytes/sort keys. `ShadowDiff` reports the first differing byte offset and reverse-walks it to the owning node. The scratch `DrawList` is DEBUG-only, capacity-retained, and erased from the shipping AOT binary. This is the flip-authorization gate; its battery MUST include a **stationary bound-source fading cover under a clean ancestor** and a **position-only reflow shift** (§5).

## 2. Interaction with the reuse paths, scroll integrator, reconciler, render-thread seam

- **Exact-copy** (461) becomes reachable on non-scroll reconcile/fade frames: a clean node (`recordDirtyBits==0`, InputSig match) memcpys its prior bytes and early-returns (476) — the subtree cull re-activates. Painter order is preserved because `Walk` visits current child order (914-928) and sort keys are `depth<<32 | subkind` (435) with no sibling ordinal, so a reused span lands in the correct paint slot as long as the parent re-walks. Every reorder/insert/remove marks the parent `LayoutDirty` (Reconciler.cs:506/518/1534), so the parent always re-walks.
- **Translated-copy** (478) becomes reachable on scroll frames where a component elsewhere reconciled: clean siblings (`descendantDirtyBits==0`, `(recordDirtyBits & RecordDirtyContent)==0`, MoveSig match) translate along the scroll delta; the content node stays blocked by `IsDirectMovingScrollContent` (457/480, 1080-1085) so entering/edge rows re-walk; `ClipComplete` + `IsClipComplete(TranslateBounds(...))` still guard partial clips (489-490). The **size fold preserves this path** for position-only shifts.
- **The image mark pass and the translated path.** A fading image is marked `PaintDirty` (`RecordDirtyContent`), which correctly *blocks* translated-copy for that node (481) — a fade must re-record, not translate — and up-propagates descendant-dirty so its ancestor chain declines exact/translated and re-walks down to it (`SceneStore.Mark` -> `MarkRecordDirty` up-propagation). Non-fading siblings under the same ancestor stay clean and reuse. This is exactly the reachability the sig-only approach lacked.
- **StickyPinned:** unchanged. A pin flip only occurs when `p.LocalTransform.Dy` changes (`ScrollBindEval`), which already `Mark(child, PaintDirty)`, up-propagating descendant-dirty so the parent re-walks and re-evaluates the pinned-last split. No new edit; a shadow gate confirms.
- **Scroll integrator:** `MixScrollViewport` (1071-1078) folds Offset/Overscroll into both sigs — unchanged; a viewport re-records its own chrome while content translate-copies.
- **Reconciler:** unchanged. `_reconciled=true` still fires for layout + the skip-submit gate but no longer nukes reuse. **No reconciler edit at all** in this design (the original's `RegisterImageNode` hook is deleted).
- **Render-thread seam:** the size fold is a read-only local computation (safe to move with Record). The reveal-mark pass runs pre-record on the UI thread (phase 6-7), like every existing `Mark`, never mutating dirty arrays during phase 8. `Publish` byte-copy is untouched. The shadow harness is DEBUG-only.

## 3. Correctness enumeration (every event that changes recorded bytes or painter order)

| # | Event | Caught by | Reachable? |
|---|-------|-----------|------------|
| 1 | In-place prop patch (fill/text/corners/border/visualKind/image id/fit) | `Mark(PaintDirty)` @Reconciler.cs:2581 (RecordChanged-gated) | self-dirty |
| 2 | Side-table set/clear (shadow/gradient/acrylic/edge-fade/border-brush) | each `Set*` -> `MarkRecordDirty` | self-dirty |
| 3 | Bind effect (fill/opacity/width/height/textcolor/validation/transform) | each effect `Mark`s (Reconciler.cs:1082-1166) | self-dirty |
| 4 | Hover/press/brush fade advance | `SetInteractT`/`SetBrushAnimT` -> `Mark(PaintDirty)` | self-dirty |
| 5 | Own **W/H** stretch/grow (X/Y unchanged) | **NEW size fold in both sigs (§1b)** | via re-walking dirty `LayoutDirty` root |
| 6 | Own X/Y shift (reflow) | `world`->InputSig (exact declines); MoveSig omits translation -> **translated-copy preserved** | via dirty layout root |
| 7 | Ancestor transform/resize/scroll -> child world/clip/depth | `MixAffine`/`MixRect`/depth in sigs | ancestor re-records -> re-walks |
| 8 | Sibling reorder / child insert / remove | parent `Mark(LayoutDirty)` -> parent re-walks new order | parent dirty |
| 9 | Inherited hover/press/disabled/interactive | `inherited.*` in sigs | hovered ancestor dirty |
| 10 | Focus/textEdit/scrollbar theme colors | Record params in sigs | frame-constant |
| 11 | Scroll offset | `MixScrollViewport` + content-node forced re-record | content dirty |
| 12 | Theme transition | re-render marks each node + Tok params change sigs | self-dirty |
| 13 | **Image crossfade advance** (unbound source, no scene mutation) | **NEW `MarkActiveReveals` -> existing `MarkImageDirty` (§1a/§1c)** | ancestor chain re-walks (image now record-dirty) |
| 14 | **Image crossfade advance, BOUND source** (LazyGrid/artist covers) | **same** — `_imageNodes` is populated by `PinImageNode` in the bound effect (Reconciler.cs:1158), so `MarkImageDirty` reaches it | ancestor chain re-walks |
| 15 | **Image ready/failed flip / restart / reject** | existing `ImageStatusChanged -> MarkImageDirty` (AppHost:1006-1008) | ancestor chain re-walks |
| 16 | StickyPinned flip | existing child `Mark(PaintDirty)` -> parent descendant-dirty | parent re-walks |
| 17 | Reveal `PresentedW/H` / Reflow `ChildShiftX/Y` | `pw/ph` in sigs (§1b) for Reveal; ChildShift rides `childWorld` under the node's own anim mark | animated node dirty |
| 18 | Drag ghost / overlays / orphans / detached / popups / resize / modal / first frame | stay **global** (unchanged, SceneRecorder.cs:167-181) | n/a |

Rows 5-6 (size fold) and 13-15 (reveal/status marks on the existing registry) are the closures; row 14 is the case the reviewer flagged, now covered because the bound-source effect populates `_imageNodes` at line 1158. The mechanism is pixel-exact iff every byte/order change is self-dirty, reached via a re-walking dirty ancestor, or folded into the sig — enforced by the shadow gate.

## 4. Allocation analysis (phases 6-13)

Zero new managed allocations on shipping frames.
- **§1b:** two scalar `MixFloat` per node.
- **§1a:** `_markImageDirty` (`Action<int>`) is a method-group delegate cached once at composition (the existing 1006-1008 handler already allocates one closure at setup — we cache a field instead). `MarkActiveReveals` iterates a `List<int>` by index and swap-removes — no allocation; runs only when `imageContentChanged` (no-op at rest).
- **§1c:** `_activeReveals.Add` happens in `NoteCrossfadeDeadline`, reached from `Pump`/`OnDecodeComplete` (phases 3-5, not 6-13); the `List<int>` grows by doubling only when concurrent reveals exceed the prior high-water — amortized, never steady-state.
- **`MarkImageDirty`** iterates a `List<NodeHandle>` and marks — zero alloc (existing hot-path-safe method).
- **Net:** on a reconcile/scroll/fade frame, strictly fewer nodes take the byte-emitting branch (the subtree cull re-activates) -> less work than today. The phase 6-13 alloc tripwire now runs with `spanReuseDisabled=None` on reconcile/layout/fade frames — a *stricter* test of the reuse path's zero-alloc-ness.
- **Shadow scratch `DrawList`:** DEBUG-only, capacity-retained, outside the release tripwire.

## 5. Test / gate plan (VerticalSlice, headless)

See `gates`. Discriminators: `image_fade_bound_source_scoped` (the exact case the reviewed hook broke — a bound-source cover fed via signal, `ImageId` landing through the 1158 effect, must NOT freeze under a clean stationary ancestor), `image_fade_stationary_scoped` (unbound variant), `reflow_shift_translates` (position-only shifts still translate-copy). `shadow_byte_identity` over a mutation battery (including the bound-source stationary fade) is the flip authorization.

## 6. Staged landing & rollback

1. **Land §1b (size fold) + §1c (`ImageCache` active-reveal set + `MarkActiveReveals`) + §1a mark-pass call, with the globals still ON** (i.e. add the `MarkActiveReveals` call unconditionally but keep the three `spanDisable` ORs). The size fold only adds sig conservatism (output unchanged); the mark pass only adds record-dirt on frames the global kill already forced full re-record (output unchanged). Verify build clean + "ALL CHECKS PASSED" + `SceneRecordStats` sane.
2. **Shadow soak (`FG_SPAN_SHADOW=1`)** across artist / discography / detail / now-playing / lyrics + the gallery. The record-twice byte-identity assert must stay silent (esp. the bound-source stationary-fade + reflow battery).
3. **Flip (`FG_SPAN_SCOPE=1`, default on).** Measure on the artist page: `SpanReuseDisabledReasons` drops SceneChanged/Layout/ImageContent on fling frames; `SpansReused`/`SpansRebased` rise; `NodesVisited`, `SpansReRecorded`, `SpanBytesCopied` fall (cull re-activates).
4. **Bake window** with `FG_SPAN_SCOPE=0` as the one-line total revert (re-ORs the three reasons; the size fold and the reveal set are inert-safe under rollback — the fold only adds conservatism, and the mark pass is skipped in the legacy branch). Remove the flag + shadow scaffold after a release; frame `FG_SPAN_SCOPE` as a bisect/rollback diagnostic, not a quality tier (CLAUDE.md one-experience rule).

## 7. Canon / ownership

- **`design/subsystems/gpu-renderer.md`** (owns the recorder + clean-span-reuse contract): reconcile "when reuse is disabled" from window-global to per-node record-dirty for the SceneChanged/Layout/ImageContent classes, and document that the baked-geometry hash (span signature) now includes the node's own presented size (`pw/ph`). The SPEC-INDEX §2 rule ("clean-span reuse valid IFF handle IsLive AND realization content-epoch unchanged AND baked-geometry hash unchanged, single Mutate() chokepoint") is **refined, not superseded**: "content-epoch unchanged" is explicitly per-node (the record-dirty aggregate), and the image reveal is folded into that per-node channel via the mark pass. No opcode/column/seam shape changes -> no SPEC-INDEX value superseded, no archive move.
- **`design/subsystems/media-pipeline.md`** (candidate owner of `ImageCache`): document the `_activeReveals` set + `MarkActiveReveals`, and that the reveal fade is scoped via the reconciler's **existing** `_imageNodes` registry + `MarkImageDirty`, replacing the global `ImageContent` kill. **Caveat (reviewer minor #3):** `ImageCache.cs` sits in `src/FluentGpu.Engine/Scene/` (namespace `FluentGpu.Scene`); confirm against `design/subsystems/README.md` whether `media-pipeline.md` or `scene-memory.md` is the ownership doc for `ImageCache`, and place the `_activeReveals` prose accordingly (likely `media-pipeline.md` owns the cache behavior; `scene-memory.md` need not co-own since no `SceneStore` registry is added).
- **`design/subsystems/layout.md`**: note `pw/ph` now enters the span signature (no `SetArrangedBounds` change in the primary design).
- **`design/subsystems/reconciler-hooks.md`**: no change (the `_imageNodes`/`MarkImageDirty` contract already exists and is unchanged; only a new *caller* is added).
- Run `docs/design/check-canon.ps1` (exit 0) after the prose edits.

## 8. Edge cases

- **Bound-source cover (the flagged case):** `_imageNodes` is populated by `PinImageNode(node, newId)` inside the bound effect (Reconciler.cs:1158) the moment the signal delivers a non-zero id; `MarkActiveReveals -> MarkImageDirty(id)` then reaches the node every fade frame. No `WriteColumns` hook is needed or added. Covered by the `image_fade_bound_source_scoped` discriminator gate.
- **Image node with children** (badge over a cover): `MarkImageDirty` marks the image node self-dirty -> exact-copy of the subtree declines -> it re-walks; the clean badge child reuses under it.
- **Shared `ImageId` across nodes:** `_imageNodes[id]` holds a `List<NodeHandle>`; `MarkImageDirty` marks all of them and they re-record together while fading — strictly better than the global epoch kill that re-recorded offscreen/non-image nodes too.
- **Reveal that leaves flight (evict/fail/reject/restart):** those set `TextureMs=NaN`; `CrossFadeOf==1f` -> the id self-prunes from `_activeReveals` on the next pass. A straggler that leaves flight while `HasActiveCrossfades` is already false lingers until the next fade frame, then prunes; marking it meanwhile is a no-op or one harmless steady re-record.
- **Node index reused same-frame:** `SpanTable` keys on gen -> gen-mismatch miss -> re-record (unaffected).
- **`PresentedW/H == NaN`:** falls back to `b.W/b.H` (431-432) — the fold hashes the actual drawn geometry.
- **`VisualKind.None` resized:** sig differs -> re-walks (emits no visual, cheap); children reuse if unchanged.

## 9. Rejected / deferred

- **Original design's new `SceneStore` image-node registry + `RegisterImageNode` `WriteColumns` hook:** rejected — the registry already exists in the reconciler and is correctly maintained at both bound and unbound sites; the `WriteColumns` hook could never register bound covers (reviewer's blocking issue). Reusing `_imageNodes` + `MarkImageDirty` is smaller, already-correct, and dissolves the issue.
- **Design 2's image signature fold:** rejected — reachability freeze at rest (a clean ancestor exact-copies and never walks to the fading image).
- **Blanket-X/Y `SetArrangedBounds` `RecordDirtyContent` mark:** rejected as primary — over-invalidates reflow (blocks translated-copy at 481). Retained only as optional defense-in-depth (§files, `FlexLayout.cs`) if the shadow gate ever surfaces an unreachable resize: split it (`RecordDirtyTransform` on X/Y, `RecordDirtyContent` on W/H) against a record-time bounds snapshot.
- **Reveal-as-anim-slab-channel for images:** the canon-aligned long-term alternative (model the reveal as an `AnimValue` channel so `AnimEngine` marks the node dirty on advance, removing `_activeReveals` entirely). Larger change; a follow-up once the mark pass is proven.

## Part B — open questions

- Should the image reveal move into the AnimEngine slab as an AnimValue channel (per docs/plans/animation-engine-rework-design.md) so the scheduler marks the node dirty on advance, eliminating _activeReveals + MarkActiveReveals? Cleaner long-term; recommend the reveal-set + existing MarkImageDirty first.
- Does Entry carry its own Id for the NoteCrossfadeDeadline add, or should NoteCrossfadeDeadline take an explicit int id param? Both call sites have id in scope — confirm the cheapest wiring.
- Ownership doc for ImageCache: check design/subsystems/README.md — ImageCache.cs lives in Scene/ but is behaviorally media-pipeline. Confirm whether _activeReveals prose belongs in media-pipeline.md or scene-memory.md.
- Ship FG_SPAN_SCOPE/FG_SPAN_SHADOW as permanent bisect diagnostics or delete after bake? CLAUDE.md forbids quality-tier knobs; frame as diagnostics/rollback with a removal follow-up.
- Confirm the cached Action<int> _markImageDirty is set after _reconciler is constructed, and consider re-pointing the existing ImageStatusChanged handler (AppHost:1006-1008) at it to drop its setup-time closure.

## Part B — reviewer notes

**Blocking issues found on the pre-revision design (fixed above; the named gates guard the regression):**

- The image-node registry hook, as written, will FREEZE the reveal fades on bound-source images — which are the LazyGrid/artist-page card covers, i.e. the exact primary churn (mechanism #4) the design exists to fix. Both the summary and the Reconciler.cs file-change entry specify registering 'when a node is assigned VisualKind.Image with a NON-ZERO ImageId, at the single WriteColumns chokepoint (the VisualKind.Image case).' But there are TWO ImageId-assignment paths, verified in Reconciler.cs: (1) the WriteColumns ImageEl case sets paint.VisualKind=Image at line 2409, and for UNBOUND sources sets ImageId at 2428; (2) for BOUND sources (ImageEl.Source.IsBound — how every reactive/virtualized cover is fed), ImageId is set/re-set inside a bind Effect at line 1157, which runs on every source-signal change and does NOT re-enter WriteColumns. For a bound image, ImageId is still 0 at line 2409 where VisualKind.Image is assigned, so the 'non-zero ImageId at the WriteColumns chokepoint' predicate skips registration; and because the authoritative re-assignment lives in the effect (1157) that never re-runs WriteColumns, the node is never registered at all. Result: bound covers are absent from _imageNodes, MarkFadingImages never marks them, and under a clean stationary ancestor their fade freezes via the exact-copy early-return (SceneRecorder.cs:476) — the precise failure the design correctly refutes Design 2 for, now reintroduced for the dominant workload. This is the load-bearing, 'L-rated,' novel part of the design (scoped ImageContent) failing on the main case as written.
  - *Fix adopted:* Register keyed on the NODE at the point VisualKind.Image is assigned (Reconciler.cs:2409) REGARDLESS of the current ImageId (the registry is node-keyed and MarkFadingImages already reads live p.ImageId each scan and skips ImageId==0), so a bound node registered once at mount stays covered as its bound ImageId lands/changes via the 1157 effect. Additionally (or alternatively) add the RegisterImageNode call inside the bound-source effect at 1157 alongside the existing PinImageNode/Mark(PaintDirty). Make append idempotent (append-if-absent, as the design already states). This resolves the design's own open-question #2, which pre-flagged exactly this grep; the registry+live-scan architecture already supports the fix — only the hook predicate/site is wrong.

**Minor issues (address during implementation):**

- Shadow harness (DEBUG) span-state isolation is hand-waved. The design says record twice 'into a reused scratch DrawList' but does not isolate the SpanTable. SceneRecorder.Record drives reuse off spans.BeginFrame double-buffering (line 166) + Store/TryGet; running the authoritative (forced-full) pass and the candidate (scoped) pass against the same _spanTable within one frame risks the second pass reading spans the first pass stored, corrupting the byte-identity comparison and letting the gate pass vacuously. The harness needs its own scratch SpanTable (or must compare candidate output against the real prior-frame stream). Fixable, DEBUG-only, does not affect the shipping path.
- MarkFadingImages scans O(all registered image nodes) on every frame with an active fade/decode, independent of how many are actually fading. On the artist page PagedShelf measured:true realizes ALL cards (thousands of image nodes), so this is thousands of dict lookups per fling frame. The design acknowledges this in risks and offers optional viewport/deadline scoping; it is bounded, zero-alloc, and cheaper than today's full re-record, but the scan cost is not free and is worst exactly on the worst-case page. Measure before shipping; consider bounding the registry to Pending/within-reveal-deadline nodes.
- Canon/ownership: ImageCache.cs physically lives in src/FluentGpu.Engine/Scene/ (namespace FluentGpu.Scene), not Media/. This makes the design's open-question #5 (namespace importability of ImageState/ImageCache into SceneStore) already satisfied — SceneStore and ImageCache share FluentGpu.Scene, no new dependency. But the canon section should verify against design/subsystems/README.md that media-pipeline.md is truly the ownership doc for ImageCache given it sits in the Scene subsystem folder; the doc-reconcile list may need scene-memory.md to co-own the image-node registry it introduces there.
- MarkFadingImages predicate edge: CrossFadeOf on an entry with TextureMs==NaN (decoded-but-reveal-not-started, e.g. no LQIP before full-res lands, ImageCache.cs:475) returns Progress(NaN); NaN<1f is false, so the StateOf==Pending clause is the only thing catching not-yet-revealing nodes. Confirm StateOf covers the pre-TextureMs window for every code path (None vs Pending) so a cover that has decoded but not yet started its fade isn't briefly missed. The shadow stationary-fade gate should exercise the TextureMs==NaN start explicitly.

---

# Part C — Stop image reveal crossfades from disabling span reuse window-wide

> **Review outcome:** passed adversarial review (sound as written). **Estimate:** M for Phase 1 (host gating swap + a small ImageCache fade list + two thin marker methods + 5 gates; no wire-format or GPU change, render-seam-neutral). M-L for Phase 2 (DrawImageCmd shape change is a cross-cutting POD touching recorder, D3D12 + headless replay, the async frame packet, skip-submit, canon docs, and 3 gates). Ship Phase 1 first — it alone deletes the window-wide kill switch; Phase 2 is the follow-up that makes fades cost zero re-record.

**Summary.** Today the whole-window `SpanReuseDisabledReason.ImageContent` fires on three global terms in AppHost (ImageCache.ContentEpoch changed, HasActiveCrossfades, or the settle flag). It is load-bearing only because the reveal factor is baked into `DrawImageCmd.CrossFade` at record time, so a reused span would freeze the fade, and because a newly-decoded texture legitimately changes content. Both are already trackable per-node: the Reconciler maintains a live `imageId -> List<NodeHandle>` reverse index (`_imageNodes`) and `MarkImageDirty` already re-records exactly the nodes whose decode completed. Phase 1 deletes all three global terms and replaces them with (a) a per-frame pass that marks only the still-fading nodes record-content-dirty via that reverse index, plus (c) a scroll-active "skip the reveal" policy (mount-without-fade) so a fling does not dirty the moving content and keeps the translated-copy path alive. Content-ready flips stay covered by the existing `MarkImageDirty`. Phase 2 removes even the residual per-node fade re-record by lifting the reveal factor out of the baked command: the command carries `FadeStartMs/DurationMs/Easing` and the D3D12/headless replay resolves the current factor per submit against a per-frame clock uniform, so a reused/translated span animates for free — at which point skip-submit must be defeated while fades are live because the command bytes no longer change frame-to-frame.

## Problem restatement (verified against code)

`AppHost.cs:1611-1613`:
```
bool imageFadeActive = _images.HasActiveCrossfades;
if (_images.ContentEpoch != _recordedImageContentEpoch || imageFadeActive || _imageCrossfadeWasActive)
    spanDisable |= SpanReuseDisabledReason.ImageContent;
```
`ImageContent` is passed as `spanReuseDisabled` into `SceneRecorder.Record` for the **whole window**. When set, both the exact-copy (`SceneRecorder.cs:461`) and translated-copy (`:478`) fast paths are skipped for every node, so every scene node re-records. Three independent global terms trip it:

1. **`ContentEpoch != _recordedImageContentEpoch`** — `ImageCache.ContentEpoch` bumps on every `OnDecodeComplete`, `RestartDecode`, and async rejection. Fling through uncached covers completes decodes continuously ⇒ this bumps every frame regardless of the fade.
2. **`HasActiveCrossfades`** — `_clockMs < _maxCrossfadeDeadlineMs`; true while any reveal is mid-flight. Every mount fires a ~320ms reveal, so scrolling through fresh art keeps this true continuously.
3. **`_imageCrossfadeWasActive`** — the one-frame settle after (2) clears.

Why it is load-bearing today:
- **Fade freeze:** `SceneRecorder.cs:871,884` bake `images.CrossFadeOf(ih)` (a time-varying 0→1) into `DrawImageCmd.CrossFade`. A reused span keeps last frame's bytes, so the fade would stop advancing. The span **input signature does not include the crossfade** (`ComputeSpanInputSig`, `:1002-1035`, references no image state) — so the global flag is the *only* thing forcing fades to re-record.
- **Content change:** a newly-`Ready` texture flips `DrawImageCmd.Ready` 0→1 and changes the UV; that node must genuinely re-record.

Key existing machinery this design reuses (do not rebuild):
- **Reverse index:** `Reconciler._imageNodes : Dictionary<int, List<NodeHandle>>`, maintained by `PinImageNode`/`UnpinImageNode`/`TrackImageNode`/`UntrackImageNode` (`Reconciler.cs:992-1034`). Every pinned on-screen image node is in it.
- **Per-node content dirty:** `Reconciler.MarkImageDirty(int)` (`:1037-1051`) walks `_imageNodes[id]`, prunes dead handles, marks each live node `PaintDirty`. Wired to `ImageCache.ImageStatusChanged` (`AppHost.cs:1006-1011`), which fires on every Ready/Failed transition (`ImageCache.cs:495,425`). `PaintDirty` ⇒ `RecordDirtyContent` via `SceneStore.Mark` (`:926`), and record-dirty is cleared each frame (`ClearRecordDirty`, `:839`), so it forces a **one-frame** re-record of exactly the drawing nodes and their ancestors.
- **Frame order:** `_images.Pump()` (`AppHost.cs:1564`) → `_images.Tick(dtMs)` (`:1565`) → … → record (`:1614`). `MarkImageDirty` fires inside `Pump`, before record. This is where the new fade pass slots in.
- **Replay resolves per submit:** `D3D12Device.AddReadyImage` (`D3D12Device.cs:1064-1072`) rebuilds `ImageInstance` (incl. `CrossFade`) from the command bytes every submit; the HLSL PS (`ImagePipeline.cs:79`) lerps `ph→img` by `crossFade`. Nothing on the GPU is precomputed — this makes Phase 2 cheap.

---

## Phase 1 — per-node fade dirty + scroll-skip, delete the global terms (lands first)

### Mechanism
1. **Delete all three global `ImageContent` terms.** Remove `imageFadeActive`, `_imageCrossfadeWasActive`, and the `ContentEpoch` comparison from the `spanDisable` computation, and remove the `_recordedImageContentEpoch`/`_imageCrossfadeWasActive` fields and their post-record writes. The `ImageContent` reason value stays in the enum (still emitted by the debug rollback, below).
2. **Content-ready:** already handled per-node by `MarkImageDirty` — no change. Verify (gate) that a decode completion marks only the drawing node(s) record-dirty, not the window.
3. **Fade-in-flight (a):** add a per-frame pass, run right after `Tick` (`AppHost.cs:1565`), that marks *only currently-fading nodes* record-content-dirty:
   - `ImageCache` maintains a compact `int[] _fading` + `_fadingCount` of handle ids with a live reveal (`Transition.Enabled && !NaN(TextureMs) && _clockMs < TextureMs+DurationMs`). Append in `NoteCrossfadeDeadline` (the single chokepoint where `TextureMs` is set), guarded by a new `Entry.InFadingList` bool to avoid duplicates.
   - New `ImageCache.MarkActiveFades(Action<int> sink)`: swap-compact `_fading` (drop entries whose entry is gone / disabled / past deadline, clearing `InFadingList`), and invoke `sink(id)` for each survivor. O(active fades), zero-alloc.
   - New `Reconciler.MarkImageFadeDirty(int imageId)`: same walk as `MarkImageDirty` but calls a new **record-content-only** marker (below) instead of `Mark(PaintDirty)` — a fade advance is a re-record, not a sticky paint-state change, and must not perturb `PaintDirty`-driven damage/skip logic.
   - New `SceneStore.MarkRecordContentDirty(NodeHandle)`: thin public wrapper over the private `MarkRecordDirty(idx, RecordDirtyContent)` (`:857`). Up-propagates to root exactly like content dirty (an ancestor span covers the child's bytes), which correctly defeats the ancestor's exact/translated copy on the fading spine while clean siblings still copy.
   - AppHost caches the delegate once: `_markFadeDirty = _reconciler.MarkImageFadeDirty;` (allocated at wiring, not per frame). The pass is `if (!scrollActive) _images.MarkActiveFades(_markFadeDirty);`
4. **Scroll-skip (c):** add `ImageCache.SuppressReveals` (bool). AppHost sets it before `Pump` from the same signal that drives `holdSelfBlurForScroll || _scrollAnim.AnyOffsetWroteThisFrame` (the existing scroll-active gate at `AppHost.cs:1615`). Two effects while set:
   - The Phase-1 fade pass is skipped entirely (step 3 gate above), so the moving content is not dirtied and the translated-copy path (`:478`, needs `descendantDirtyBits==0`) stays available for the scrolled subtree.
   - **Mount-without-fade:** in `OnDecodeComplete`/blur-hash upload, when `SuppressReveals` is set, start the reveal already-complete: set `e.TextureMs = _clockMs - e.Transition.DurationMs` (so `CrossFadeOf` returns 1 immediately and the entry never enters `_fading`). A card that becomes ready mid-fling shows instantly; it still re-records **once** (Ready flip via `MarkImageDirty`) but never joins the per-frame fade set. On settle, in-flight reveals that were already fading resume via the fade pass and re-snap over one frame.

### Interaction with span reuse / scroll / reconciler
- At rest: only fading nodes + their ancestor spines re-record; every other subtree hits exact-copy memcpy (`:461`). This is the whole win — "re-record everything" becomes "re-record the handful of fading cards."
- During user scroll: `scrollInMotion` already disables exact-copy (`:461`); only translated-copy applies. Not dirtying fades (step 4) keeps `descendantDirtyBits==0`, so the scrolled content translates. Nodes that became `Ready` this frame still re-record (unavoidable — new texture) but that set is bounded by decode throughput per frame, not the window.
- Hero zoom (`ArtistPage.Hero.cs`): the hero image node writes its transform every frame (zoom keyframes) ⇒ `TransformDirty` ⇒ already record-dirty; it re-records regardless, so its fade factor is naturally fresh. No special handling.
- Eviction mid-fade: `EvictToBudget` only evicts `Refs==0` (off-screen) entries, so no visible node references an evicted image; the fade set prune drops the id next frame. No change needed.
- Detached/connected-animation images (`SceneRecorder.cs:260-275`, `RecordDetached`) draw outside the normal node walk and already force `SpanReuseDisabledReason.Detached`; untouched by Phase 1.

### Allocation analysis (phases 6-13)
- The fade pass runs at ~7.5 (post-Tick, pre-record). `MarkActiveFades` iterates a pre-grown array and invokes a cached delegate; `MarkImageFadeDirty` walks a pre-grown `List<NodeHandle>`; `MarkRecordContentDirty` does array writes + the existing worklist append (amortized, pre-grown). **Zero managed allocation** on the steady path. `_fading` growth (`Array.Resize`) happens only in `NoteCrossfadeDeadline`, reached from `Pump`/`Request` (already outside the zero-alloc record phases). Net effect on phases 8-13 is *less* work than today.

---

## Phase 2 — replay-resolved reveal factor (lands second, removes residual re-record)

### Mechanism
Lift the factor out of the baked command so a reused span animates for free:
- `DrawImageCmd`: replace `float CrossFade` with `float FadeStartMs, float FadeDurationMs, int FadeEasing`. Same 3 machine words if `CrossFade` (1 float) grows to 3 — accept the ~8B payload growth (documented below).
- `ImageCache` exposes `float ClockMs => _clockMs;` and `bool FadeParamsOf(ImageHandle, out float startMs, out float durMs, out int easing)` (reads `Entry.TextureMs`, `Transition.DurationMs`, an easing enum id; returns false ⇒ no texture ⇒ startMs sentinel).
- `SceneRecorder` image case (`:867-885`) and `RecordDetached` (`:260-275`) bake the params instead of calling `CrossFadeOf`.
- **Replay** (`D3D12Device.RecordAll`) gains a per-submit `float imageClockMs` (set at `BeginFrame` or passed through `SubmitDrawList`). `AddReadyImage` computes `CrossFade = FadeDurationMs <= 0f || IsSentinel(FadeStartMs) ? 1f : Ease(FadeEasing, Saturate((imageClockMs - FadeStartMs)/FadeDurationMs))`. HLSL unchanged. Easing evaluated in managed code (a static `Ease(kind,t)` mirroring `ImageTransition.Progress`); cheap, alloc-free.
- **Render-thread seam:** replay is the render thread under `FG_RENDER_ASYNC`; `imageClockMs` is a scalar snapshotted into the frame packet at hand-off (the render thread never touches `ImageCache`). Compatible with `render-thread-seam-landing-plan.md` (the render thread owns the ComPtrs; the clock is a value input).
- **Companion skip-submit fix (required):** with the factor no longer in the bytes, a pure-fade frame produces a **byte-identical** DrawList, so the `AppHost.cs:1642-1646` skip-submit gate would elide the present and freeze the fade. Add `&& !_images.HasActiveCrossfades` to `maybeUnchanged`. `HasActiveCrossfades` is already O(1). (Phase 1 does not need this — its fade bytes change, so `dlHash` differs naturally.)

Once Phase 2 is proven, **delete the Phase-1 fade pass (step 3) and `SuppressReveals`'s fade-skip** — a mid-fade span is reused/translated with no re-record, and replay animates it. `MarkImageDirty` for the content-ready flip remains (the Ready flag + UV + FadeStart genuinely change the bytes for one frame). Mount-without-fade (step 4) can stay as a policy choice but is no longer a perf necessity.

### Why this respects canon span-reuse validity
Reuse is valid IFF handle `IsLive` ∧ realization content-epoch unchanged ∧ baked-geometry hash unchanged. The reveal factor is **not baked geometry** under Phase 2 — the baked bytes (`FadeStartMs`) are constant across the fade — so a span reused while fading is canon-valid. `Mutate()` remains the single chokepoint.

### Allocation analysis
Replay adds a few float ops per image instance in `AddReadyImage`, which already builds the struct; `imageClockMs` is a scalar uniform. Zero allocation.

---

## Edge cases
- **ContentEpoch paths without MarkImageDirty:** `RestartDecode` (Ready→Pending on retry/device-loss) bumps `ContentEpoch` but the drawn content does not change that frame (texture stays resident / placeholder unchanged); when it completes, `ImageStatusChanged`→`MarkImageDirty` fires. `ReRealizeAllResident` is device-loss, which already triggers a full `MarkAllPaintDirty`. So dropping the global `ContentEpoch` term is safe. Gate 3 asserts this.
- **Untracked on-screen images:** `MarkImageDirty`/`MarkImageFadeDirty` only reach nodes in `_imageNodes`, populated by `PinImageNode` (gated on `IsReachableFromRoot`). Any image drawn without a pin (detached fly, `RecordDetached`) already forces `Detached` span-disable; no regression.
- **First reveal frame:** frame 1 (Ready flip) is covered by `MarkImageDirty`; frames 2..N by the fade pass (Phase 1) or replay (Phase 2). Both cover the whole reveal.
- **Settle after fling:** reveals suppressed during scroll are complete-on-arrival; reveals already in flight when the fling started re-snap over the one settle frame the host already queues (`_frameAfterPaint`).
- **Payload growth (Phase 2):** `DrawImageCmd` gains ~8B; `SpanTable`/`DrawList` are size-agnostic (byte copies). Spans are per-session, so no persisted-format concern across the switch.

---

## Test / gate plan (VerticalSlice, headless)
Phase 1:
1. `gate.img.fade-no-global-disable` — scene with one mid-fade Image node (Fake decoder Ready, clock between TextureMs and deadline) + one plain Box sibling; run the fade pass then record twice. Assert `SpanReuseDisabledReasons` has **no** `ImageContent` bit and the Box sibling's span is reused (`SpansReused>=1`).
2. `gate.img.fade-marks-only-drawing-node` — after `MarkActiveFades`, assert only the image node + ancestors carry `RecordDirtyContent` (`RecordDirtyBits`), the sibling is clean, and on record the sibling copies while the image re-records.
3. `gate.img.ready-marks-only-node` — a decode completion (`Pump`) marks the drawing node record-dirty via `MarkImageDirty` and a far sibling still reuses its span; `SpanReuseDisabledReasons == None`.
4. `gate.img.scroll-holds-reveal` — with `SuppressReveals=true`, a completing decode yields `CrossFadeOf==1` immediately, `_fading` stays empty, and the scrolled content still takes the translated-copy path (`SpansRebased>=1`, `descendantDirty==0`).
5. `gate.img.fade-dirty-zero-alloc` — `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across `MarkActiveFades` over N fading nodes (extends the phase-6-13 tripwire regime).

Phase 2:
6. `gate.img.fade-replay-resolves` — encode a `DrawImageCmd` with `FadeStart/Dur`; resolve the instance at two `imageClockMs` values (headless replay / direct `AddReadyImage` equivalent); assert monotonic 0→1 and endpoints exact.
7. `gate.img.fade-span-reused-while-fading` — under Phase 2, a mid-fade image's span is reused across frames (`SpansReRecorded==0`) yet the resolved factor advances, and skip-submit is defeated while `HasActiveCrossfades`.
8. `gate.img.drawimage-translate` — `DrawImageCmd` with the new fields still translates correctly under `CopySpanFromPriorTranslated` (Transform-only; fade fields untouched) + a struct-size assertion.

Run `dotnet run --project src/FluentGpu.VerticalSlice` ("ALL CHECKS PASSED", zero-alloc gates green) and `--screenshot` on the artist page (fades visibly animate; scroll FPS recovers via `SceneRecordStats.SpanReuseDisabledReasons` no longer showing `ImageContent`, `SpansReused` high).

## Canon
- Phase 1 changes **no** command shape or cross-cutting contract — host gating + a new per-node dirty path only. Add an implementation note to `design/subsystems/media-pipeline.md §7` that the reveal no longer forces a window-wide re-record (it is per-node / replay-resolved). No `check-canon.ps1` token change expected; run it after the doc note.
- Phase 2 changes `DrawImageCmd` (a DrawList opcode POD). Reconcile the owning doc — the DrawImage/`DrawImageCmd` opcode owner in `design/subsystems/README.md` ownership map (gpu-renderer.md) + the reveal semantics in `media-pipeline.md §7` (CrossFade=1 wording) — and register the shape change in `design/SPEC-INDEX.md §2`. Then `powershell -File docs\design\check-canon.ps1` must exit 0.

## Rollback
- Phase 1: debug-only diagnostic env flag `FG_IMAGE_GLOBAL_FADE_DISABLE` (default off) that restores the old three-term global `ImageContent` disable and skips the fade pass — for A/B on a suspected regression. This is a diagnostic, not a quality knob (permitted by the "one experience, no flags" rule). If needed, revert is localized to `AppHost.cs` (re-add the three terms + the two fields).
- Phase 2: during bring-up, gate the replay-resolved path behind debug `FG_IMAGE_FADE_GPU`; recorder emits legacy `CrossFade` when off. After soak, flip default-on and delete the flag + the Phase-1 fade pass. Full revert is `git revert` of the `DrawImageCmd`/recorder/replay/skip-submit changes; no persisted state to migrate (spans are per-session).

## Part C — open questions

- Is ImageCache.Pump (phase 7.5) inside the VerticalSlice alloc tripwire window, or excluded like other cache-mutating phases? The fade pass runs adjacent to it; if Pump is tripwired, _fading growth must be proven to only occur in NoteCrossfadeDeadline off the gated path (believed true, but confirm the harness phase boundaries).
- Confirm the exact scroll-active signal to drive SuppressReveals — reuse holdSelfBlurForScroll || _scrollAnim.AnyOffsetWroteThisFrame (AppHost.cs:1615) or the DManip user-scroll flag? Needs the same source the blur-hold uses so fades and blurs hold consistently.
- Phase 2: where to snapshot imageClockMs for the async seam — at BeginFrame on the device, or carried in the SubmitDrawList frame packet? Depends on the render-thread-seam landing plan's packet shape (not fully read here).

## Part C — reviewer notes

**Minor issues (address during implementation):**

- Zero-alloc phase boundary is misstated. The design says _fading growth (Array.Resize in NoteCrossfadeDeadline) is 'reached from Pump/Request, already outside the zero-alloc record phases.' Verified: NoteCrossfadeDeadline is called from OnDecodeComplete (ImageCache.cs:475), which runs inside _images.Pump() at AppHost.cs:1564 - i.e. phase 7.5, which IS within the phases-6-13 zero-alloc window per CLAUDE.md. The substance still holds (growth is amortized and _fading can be pre-sized to the pinned-image bound, exactly like the existing _recordDirtyWrote worklist in SceneStore.cs:876-880 that already lives in this window and grows via Array.Resize), and the design's own open question #1 flags it. But the stated justification is wrong; the fix is to pre-grow _fading and let the steady-state tripwire pin it, not to claim the append is off the gated path.
- Damage under async partial-present is not addressed. Removing the global ImageContent disable means a fading node now re-records via RecordDirtyContent only (no PaintDirty, no TransformDirty). The translated-copy path adds damage only for TransformDirty nodes (SceneRecorder.cs:497-501); a full re-record's contribution to recordStats.Damage (fed to FrameInfo at AppHost.cs:1666) is not discussed. Under the synchronous path present is always correct (reused spans are copied into a complete drawlist), but for FG_RENDER_ASYNC damage-gated partial present the design should confirm a re-recorded fade spine reports its device bounds as damage so the fade region repaints. Not blocking (async is opt-in, drawlist stays complete), but worth a gate/verify.
- Scroll-active signal timing for SuppressReveals is unconfirmed. The design sets SuppressReveals before Pump (~1564) from holdSelfBlurForScroll || _scrollAnim.AnyOffsetWroteThisFrame. holdSelfBlurForScroll is finalized at line 1554 (fine), and the identical expression is used for the record call at 1615, but the design must confirm AnyOffsetWroteThisFrame is already latched by 1564 (the scroll animator ticks around tAnim at 1562, before Pump, so it likely is). The design flags this as open question #2. Minor wiring detail.
- Frozen-then-snap fade for reveals already in flight when a fling STARTS: during scroll the fade pass is skipped, so a mid-fade card's baked crossfade freezes (reused/translated span) and re-snaps over the one settle frame. Acknowledged as an intended look change with the BlurCachePolicy precedent; confirm on-device.
- Phase 2 easing-drift risk (managed Ease(kind,t) at replay must mirror ImageTransition.Progress since the render thread has no ImageCache access) is real and correctly called out; enforce via a shared single static Ease used by both recorder-side and replay-side, and pin endpoints/monotonicity in gate.img.fade-replay-resolves.

