# Wavee wheel-scroll feel on FluentGpu — root-cause report (2026-09-23)

Read-only investigation of why a wheel scroll in Wavee feels heavy, delayed and jittery on the Adreno X1-85 / 120 Hz /
150 % machine, and what the design-level fixes are. Nothing was edited; the engine's `lease` VerticalSlice suite was
run once (green, 10 checks) as a landed-state check.

Evidence: `%LOCALAPPDATA%\Wavee\logs\wavee-20260923.log`, session `pid=38856` (`sid=6e57c1fb`, Release engine,
`[d3d12.adapter] … tier=Weak uma=True`, `[d3d12.present] maxFrameLatency=1 buffers=3 waitable=yes slotWait=pre-acquire`,
`[d3d12.display] hz=120.000`, `[compositor-clock] lattice window=8.33 mode=pass`), plus the idle `[wake]` lines of the
earlier sessions `pid=15448` / `pid=7548` the same day. File references are `path:line` in `C:\wavee\fluent-gpu`
unless prefixed `app:` (`C:\wavee\waveemusic\src\apps\Wavee`). Every claim is marked **CONFIRMED** (read in code /
log) or **HYPOTHESIS** (with the measurement that would settle it).

---

## 0. Summary

The scroll path is running a **pre-plan render loop under a post-plan pacing contract**. Two design documents already
name the mechanisms — `docs/plans/compositor-scroll-implementation.md` §0 findings **A** (pacing) and **D** (every
scroll frame is a full UI-thread re-record) — and only its inert Wave S0 (pure classes) has landed:
`PresentCadence.Decide` exists (`Hosting/Threading/PresentCadence.cs`) but `RenderThread.Loop` does not call it;
`RenderScrollLease` / `RetainedScroll` exist but `ScrollKernel.LeasesEnabled` is false everywhere
(`Scroll/ScrollKernel.cs:105,303`); `WindowPacingScheduler` (ISO Phase 2) does not exist in `src/`.

Ranked, with the burst `navId=2` (artist page, 807 UI frames in 6.9 s) as the yardstick:

| # | Root cause | Cost in that burst | Status |
|---|---|---|---|
| 1 | **Two presenters per vblank.** The render thread presents motion re-presents of the *previous* frame between UI publications; a fresh scroll frame that lands behind one waits a whole refresh, and the stale re-present is what the eye sees. | 1101 presents for 807 UI frames and ≤828 vblanks: **338 (31 %) were stale re-presents, ≥273 presents (25 %) were physically undisplayable**, 39 UI frames (5 %) were dropped (DropOldest), and every re-present cost a full re-record + full-window GPU frame (+44 % GPU load on a GPU already at 30-80 % of the refresh). | CONFIRMED (log + `RenderThread.cs:180-213`). Today's `UiProducing()` suppresses the symptom; the design fix is §5.1 of the plan. |
| 2 | **Pipeline depth + sampling time.** Wheel packets are deferred to the tick; the UI samples the kernel at the *production* vblank; the render thread only starts recording after the present slot opens (avg 4-5 ms later); record + GPU is 4.4-8.4 ms on this tier; DWM composes one vblank later. Input-to-glass is 4-5 refreshes (~35-40 ms), and the offset baked into a frame is for a vblank ~2 refreshes earlier than the one it lands on. | `latWaitAvg=3.8-5.0 latWaitMax=9-13`, `freshLongWaits=459/768`, `late=2 ~ frames`, `dtP95≈10 ms` on an 8.33 grid. | Model CONFIRMED from code; the exact ms split is a HYPOTHESIS until a per-frame `[render.pace]` line correlates tick QPC, slot-open QPC, present QPC and DXGI `SyncQPCTime`. |
| 3 | **Every scroll frame is a full-window re-raster and a mostly-full re-record.** Scroll is a UI-thread `LocalTransform` write; the repaint route is `FullDirect` by policy (≥60 % coverage); the per-row translated-copy reuse that should make record O(edge rows) is hitting ~20 % of spans. | `repaintPct=100`, `spansReRecorded=300-626 vs spansReused=80-110`, `record≈1.8 ms` render-side, **`gpu=2.6-6.6 ms`** (real timestamp query) for 60-214 draw nodes. | CONFIRMED (policy, route, transform write). *Why* translated copy misses is a HYPOTHESIS — Release strips the `SpanMiss*` counters; one Debug run answers it. |
| 4 | **Full snapshot capture every scroll frame.** `CanCaptureIncremental` refuses whenever `BulkMutationSeq` advanced, and virtualization mount/rebind (`Reconciler.WriteColumns`) and every layout pass (`FlexLayout.BeginMeasurePass`) advance it. | `capture=0.5-0.9 ms/full:1750-3139` on the UI thread per frame vs `0.2 ms/inc:38` the one frame nothing recycled. | CONFIRMED. Not covered by any plan. |
| 5 | **UI-thread churn and allocation inside scroll frames.** Component re-renders per frame (`NowPlayingOverlayHost×10`, `ToolTip×10`, `SectionsHost` at 516 KB) allocate up to 877 KB in one frame; GC runs mid-gesture. | `hotAllocKB=5649-14593` per burst, **`gc=5/2/1`** (a gen-2 collection inside a 2.5 s scroll), `worst frameMs=5.1-9.3` with `flush=4.1-5.5` → the `~` late frames in `scroll.trace`. | CONFIRMED (log). Which subscriptions fire is a HYPOTHESIS (render census names types, not causes). |
| 6 | **The idle loop runs ~250 turns/s** (`scrollAnim` 85-88 % of turns, `warmCadence` 84-86 %, `frameClockPoller` 28-66 %), presents 92-175 frames/s with nothing visibly changing, and re-records the scene 96 % of turns. | `run=7579 rendered=194 recordOnly=7311 presents=2750` in 30 s idle; `presents=5252` while a track plays. | CONFIRMED (log + `AppHost.ComputeWakeReasons`). Steals CPU and, with playback, a full GPU frame per present. |
| 7 | **The weak GPU tier degrades nothing on the scroll path.** `GpuProfile.IsWeak` only tunes pool idle/eviction cadences; blur/acrylic/clear/scissor run identically to a discrete GPU. | The whole of the 2.6-6.6 ms is undegraded work. | CONFIRMED by sub-investigation (four `IsWeak` sites). |
| 8 | Redundant pacing knobs: `InteractivePresent` (interval 0 + `SuppressLatencyWaitOnce`) is a no-op under the credit discipline and exists only because of #1; three independent wake sources (publish wake, display tick, latency waitable) on one loop. | Complexity, not ms — but it is what makes #1 racy. | CONFIRMED (`RenderFrame.cs:26-33`, `AppHost.cs:1071-1076`). |

**Order:** (1) S1 pacing — one present per tick from `PresentCadence`, motion folded, `InteractivePresent` deleted —
lands the biggest feel win with no new mechanism; (2) the capture-ledger fix (#4) and the span-miss measurement (#3)
are UI/render-thread CPU wins that need no design decision; (3) S2 retained band (#3's GPU half) and the render-side
lease (#2's sampling half); (4) idle/churn hygiene (#5, #6) and the weak-tier policy (#7) in parallel, app-side mostly.

---

## 1. The pipeline as built (one wheel notch to glass)

```
 vblank k          vblank k+1        vblank k+2        vblank k+3        vblank k+4
 │                 │                 │                 │                 │
 │ wheel WM_POINTERWHEEL arrives anywhere in here — DEFERRABLE, the paced wait pumps it but does not end
 │ (Win32Platform.cs:1697-1712, PacedInputWaitClassifier.IsDeferrable :263-265 includes WmPointerWheel)
 │                 │
 │                 ├─ UI RunFrame on the compositor tick (AppHost.cs:2354-2365 DisplayTick wait; ProductionGateBlocks
 │                 │  :2119-2125 = one frame per TickSeq). FrameClock.FrameQpc = THIS tick (RefreshLattice.Build,
 │                 │  AppHost.cs:3296-3301); kernel ticks at FrameSec = FrameQpc (AppHost.cs:3790,3822,3835) — the
 │                 │  offset is sampled for vblank k+1 and the physics dt is the tick delta.
 │                 │  SceneScrollSink.Apply writes content LocalTransform + TransformDirty|PaintDirty (SceneScrollSink.cs:62-91).
 │                 │  Full snapshot capture 0.5-0.9 ms (#4) → PublishScene → WakeAsync (AppHost.cs:4538).
 │                 │       ↓ ~1-3 ms after the tick
 │                 │  render thread wakes on _wake (RenderThread.cs:130), WaitForPresentSlot (:170-172; D3D12Device.cs:5275-5283)
 │                 │  … blocks until the previous present retired (latWaitAvg 4-5 ms, max 9-13) …
 │                 │                 ├─ slot opens ≈ when DWM has consumed frame k. TryAcquire newest (:180) →
 │                 │                 │  Record 1.8 ms (AppHost.cs:1211 sceneFrame.Record) → SubmitDrawList → Present
 │                 │                 │  interval 0 (InteractivePresent → SkipVsyncOnce, D3D12Device.cs:4866-4877)
 │                 │                 │  GPU executes 2.6-6.6 ms (FullDirect, whole target)
 │                 │                 │                 ├─ DWM composes frame at its next pass if the GPU is done
 │                 │                 │                 │                 ├─ scanout: pixels for offset(k+1) appear
 │                 │                 │                 │                 │  here, 2-3 refreshes after they were
 │                 │                 │                 │                 │  sampled (RefreshLattice assumed +2).
 │
 │ meanwhile, on every tick where render motion is live and no fresh frame is there yet, the loop presents the
 │ PREVIOUS frame again (RenderThread.cs:196-213 → AppHost.RenderMotion :575-581) — a full re-record + GPU frame that
 │ takes the slot the fresh frame was about to take (#1).
```

Two independent numbers in the log pin this model: `presented=1101` vs `frames=807` (§0 #1), and
`freshLongWaits=459/768` — 60 % of fresh presents blocked > 4 ms in `WaitForPresentSlot`, i.e. the UI's publication
routinely exists for half a refresh before the render thread may start on it.

---

## 2. Root cause 1 — two presenters per vblank (the jitter)

### Mechanism (CONFIRMED)

- `RenderThread.Loop` wakes on **three** sources: the UI's publish wake (`_wake`, :130), the display clock tick
  (`_displayWaits`, :122-129, only while `motionDue`), and — implicitly — the latency waitable inside
  `WaitForPresentSlot` (:172). `motionDue = HasRenderMotion()` (:118) is true whenever the render-side compositor slab
  has an active row or an image crossfade is live (`AppHost.HasOwnRenderMotion`, `AppHost.cs:486-510`).
- On a tick wake with motion due and **no fresh publication yet** (the UI is 1-3 ms into producing this tick's frame),
  the branch at :196-213 calls `RenderMotion` → `SubmitPresentOnRenderThread(_activeRenderFrame, motionRepresent:
  true)` (`AppHost.cs:575-581`). That path **re-records the retained scene** (`AppHost.cs:1176-1211`; the record is
  elided only when no pose changed and no crossfade is live, :1185-1196) and submits a full `FullDirect` frame at
  interval 1 (`ApplyPresentPacing` returns early for a re-present, `AppHost.cs:1071-1073`).
- The fresh publication then arrives, the loop wakes, and `WaitForPresentSlot` blocks until *that* stale present
  retires — a full refresh. `raceHits` (:184) counts only the exact same-`TickSeq` ordering, which is why it reads 3
  while `renderMotion` reads 338: the common case is one tick apart, not the same tick.
- Before today's diff the motion tick did not even take the slot credit first (`git diff` of :165-172), so the stale
  frame was chosen *before* blocking in `SubmitDrawList.WaitForLatency` — the plan's finding A verbatim
  (`compositor-scroll-implementation.md:21-31`).

### Cost (log, burst `navId=2`, 6.9 s)

- `presented=1101` on a 120 Hz panel that has ≤ 828 vblanks in 6.9 s → **≥ 273 presents could never be displayed**;
  DWM keeps one per vblank and drops the rest. Present rate 160/s.
- `renderMotion=338` (31 %) — each one a re-record (~1.8 ms render CPU) and a full-window GPU frame on a GPU that
  already spends 2.6-6.6 ms of every 8.3 ms. Extra GPU load ≈ +44 %; GPU contention is the most likely reason the
  *fresh* frame's GPU work misses the compose deadline (`missedVblanks 1-5`, `dtMax 14-17 ms`).
- `renderFresh=768` of 807 UI frames → 39 scroll positions (5 %) were produced and never shown; `gaps` climbed
  45→121 across the burst (`FrameStats.PublicationGaps`).
- Feel: whenever the stale frame takes the vblank the list holds still for one refresh and then jumps two steps —
  `scroll.trace` `late=2`, the `~` marks (`Diagnostics.ScrollTrace.cs:22`), `verdict=present-hitch` (:83).

### What arms render motion during a wheel scroll (partly HYPOTHESIS)

`HasOwnRenderMotion` is armed by (a) `_renderAnimations.HasActive` — any compositor-slab row: TranslateX/Y, ScaleX/Y,
Rotation, Opacity, BlurSigma, HoverFade/PressFade/BrushFade (`Animation/RenderCompositorAnimations.cs`), a `Loop`
row never reports `Done` — or (b) `Images.HasCrossfades`. Candidates during a scroll, ranked by plausibility:

1. **Hover fades on rows sliding under a stationary pointer** — every frame a new row enters the pointer, starting a
   HoverFade row (150-200 ms). HYPOTHESIS; the `[wake] anim=N:…` channel census is emitted only at 30 s idle
   boundaries, never inside `scroll.frames`. Measurement: add the render slab's live-channel census to the
   `scroll.frames` rollup (or to a `[render.pace]` line) and read it during a burst.
2. **The playing track's equalizer** (`app:Platform/Controls.cs:530-600`): a 30 Hz `UseInterval` writing three
   `FloatSignal`s bound through `Transform = Prop.Of(() => Affine2D.Scale(…))` (`:683-696`). Whether that `Prop.Of`
   transform becomes a compositor row or a UI-side paint write decides whether it arms *render* motion. Either way
   its own comment admits "motion IS presents" — it re-presents a full window at 30 Hz while a track plays, and the
   idle census with playback shows `presents=5252` in 30 s (175/s) with `anim=10:…ScaleX*1,ScaleY*1,…` live
   (`pid=7548`). HYPOTHESIS for the scroll bursts (playback state during `pid=38856` is not logged as playing/paused).
3. Cover crossfades — unlikely: `_images.SuppressReveals = scrollActive` (`AppHost.cs:4179`).
4. `CoverShimmer`, `ToolTip`, `NowPlayingOverlayHost` — **not** render-motion sources. The render census counts
   **UI-thread re-renders of a component type in one spike frame**, not live instances
   (`Reconciler.cs:641-713 NoteRenderCensus`); `NowPlayingOverlayHost×10` means ten rebinds re-ran that host's
   `Render` (only the one row whose uri `RelatesTo` the playing track builds the equalizer,
   `app:Platform/Controls.Art.cs:242-262`). They belong to root cause 5, not 1.

### Is `UiProducing()` the fix?

It is a **correct stop-gap and an architectural band-aid**. `RenderThread.cs:73-79,171,190-195` withholds a motion
re-present while the UI published within the last 1.5 refresh periods. It removes the specific race above and the
comment records the measurement honestly. But:

- it is a wall-clock guess ("a publication is imminent") layered on the pre-plan `if/else if` chain, beside — not
  inside — the pure, gated `PresentCadence.Decide` that the plan wrote for exactly this decision (§3.4,
  `compositor-scroll-implementation.md:401-448`; `Hosting/Threading/PresentCadence.cs:19-31`; gate
  `gate.lease.present-once-per-tick`, green);
- it starves *all* render-side motion whenever the UI is producing (a flyout closing over a scrolling page stops
  re-presenting for the gesture), which the UI's publications only cover if the recorder samples that motion at
  record time — true for compositor rows adopted this publication, not for a row that started after it;
- it has no gate (`UiProducing` appears in no suite), and the 1.5-period constant is refresh-relative but otherwise
  untested.

### The principled design (already written — plan §2.3 / §5.1, `compositor-scroll-implementation.md:211-238, 964-995`)

> One turn per compositor tick per window, one present decision per turn: **fresh wins; motion only if nothing fresh
> exists at the tick; a publication that lands after this tick's present waits for the next tick (DropOldest hands the
> newest).** The credit is the throttle, the tick is the phase.

Concretely, the loop's turn is the tick while anything is live (motion rows *or* the scroll lease); the publish wake
no longer presents mid-interval; `PresentCadence.Decide(TickSeq, LastPresentedTickSeq, HasFreshPublication,
MotionDue, CreditHeld, Unpaced)` is the only presenter; `_nextTick`/`_tickPeriod` (`RenderThread.cs:47,218`) and
`InteractivePresent` (§5.2, `:995-1008`) are deleted. With that model the UI frame produced at tick k is presented at
tick k+1 deterministically — no race, no stale frame on the glass, zero undisplayable presents — and `UiProducing()`
becomes unnecessary (its concern is subsumed: at the tick, if a fresh frame exists it wins; if not, the motion
re-present *is* this vblank's frame and the UI's frame takes the next one, which it would have anyway).

**What the plan misses:** it assumes ISO Phase 2's `WindowPacingScheduler`/`IRenderSource` loop exists (§5 header:
"Depends on ISO Phase 2"); it does not. S1 can be landed on today's single-source loop — `PresentCadence` already takes
everything the loop knows (`_displayClock.TickSeq`, `_publisher.HasPendingFrame`, `_needsTick()`, the credit from
`WaitForPresentSlot`). The plan also does not say what the loop does with a publish wake between ticks when *no*
motion is live (the idle-page click): it must still present immediately (that is the `Unpaced`/no-motion arm), so the
tick-only turn applies **only while `MotionDue`** — worth stating explicitly in §5.1.

### Verify

- `scroll.frames`: `presented ≤ vblanks in wallMs` (≤ wallMs/8.33), `renderMotion → 0` during a wheel burst,
  `renderFresh == frames − (frames declined)`, `gaps` flat across a burst, `freshLongWaits/renderFresh` falls to the
  fraction of frames whose record+GPU genuinely overran.
- `scroll.trace`: `late=0`, `liveMissed=0`, verdict `smooth`/`notch-dips` only.
- New always-on `[render.pace]` line (plan §5.3) with attested `PresentStatisticsLedger` figures: `dropped=0`,
  `repeated=` equal to genuine GPU overruns.
- Gates: `gate.lease.present-once-per-tick` (exists), plus an `RenderThreadLifecycleTests` case that a publish
  landing mid-interval with motion due presents exactly once at the next tick.

---

## 3. Root cause 2 — pipeline depth and where the offset is sampled (the heaviness)

### Mechanism (CONFIRMED from code; ms split HYPOTHESIS)

1. **Input deferral, ≤ 1 refresh.** Wheel packets are deferrable (`Win32Platform.cs:263-265`); the paced wait pumps
   them and keeps waiting for the tick (:1697-1712). Plan §5.4 (`:1028-1041`) reverses this ("wheel packets are
   urgent"); §10 Q3 leaves it to the owner. With the render-side lease the notch reaches the kernel without a UI
   frame; without it, urgency only moves the command earlier inside the same tick (production is tick-gated anyway),
   so this is a small, second-order win until the lease lands.
2. **Sampling at production, not at present.** `ScrollClock.FrameSec = FrameQpc` = the production tick
   (`AppHost.cs:3790,3822,3835`); `RefreshLattice.Build` predicts `PresentQpc = FrameQpc + 2·refresh`
   (`RefreshLattice.cs:32-55`, latency 1) but the kernel advances on `FrameSec`, and the real landing is 2-3
   refreshes later (the measured chain below). The plan's §2.3 "sample at the present" (`:222-227`) and
   `RenderScrollClock` (landed, S0) fix this only when the lease runs on the render thread.
3. **Slot-then-produce serialisation.** `WaitForPresentSlot` runs before `TryAcquire` (`RenderThread.cs:170-180`) —
   the Terminal/makepad order, chosen so the presented content is the freshest available when the slot opens. The
   price on this tier: record (1.8 ms) + submit + GPU (2.6-6.6 ms) all start *after* the slot opens, so the frame
   is ready 4.4-8.4 ms into the interval and any overrun lands one vblank late. The interval-0 present does not help:
   with one credit there is never a second queued frame for it to replace (`D3D12Device.cs:4866-4877`; the credit is
   spent at :4880).
4. **Why the slot opens ~4-5 ms after the UI's publish.** The UI publishes ~1-3 ms after the tick; the waitable
   (semaphore, depth 1) re-signals when DWM has *consumed* the previous present, which on a composition swapchain is
   at DWM's compose pass — i.e. tied to the same tick phase the UI produces on, plus DWM's own latency. The measured
   `latWaitAvg 3.8-5.0 ms` (uniform-ish over the interval; `latWaitMax 9-13` = a missed compose) is consistent with
   the slot opening in the second half of the interval. HYPOTHESIS as to the exact phase; the D3D12Device comment
   (`:36-46`) that motivated depth 1 measured the *old* acquire-then-wait order and is not evidence for the current one.
5. **DWM adds one compose + one scanout.** A composition swapchain frame presented in interval k is composed at k+1
   and on the panel at k+2 (`RefreshLattice` encodes exactly this "+2·refresh").

Adding up (typical, not worst): defer ≤ 8.3 + UI 2 + slot wait 4.5 + record/submit 2.5 + GPU 4 + wait-for-compose
(residual) + scanout 8.3 ≈ **30-40 ms, 4-5 refreshes**, versus the plan's target of ≤ 1 + 2 refreshes (§2.4,
`:251-254`). The UI's `dtP95≈10 ms` and `dtMax 14-17 ms` on an 8.33 grid (`scroll.trace`) are the visible edge of
this: the kernel's dt is the tick delta, and a missed tick gives one 16.7 ms step — `~` and `!` frames.

### The correct pacing model

Two consistent options; the plan chose (a) and it is the right one for this engine:

(a) **Render-owned scroll bodies on the compositor tick, sampled at the predicted present** (plan §2.1-2.4, §3.3,
§4.1): the render thread ticks the leased body at `RenderScrollClock.PresentSec`, writes the offset into the overlay
transform, records (or, with S2, composites the retained band) and presents once per tick. The UI is a follower. This
removes the UI frame, the tick-hold and the publish→slot wait from the scroll path: **≤ 1 refresh (command → next
render tick) + 2 (DComp)**. Wheel notches become urgent and ride the lease ring (§2.4).

(b) If (a) is deferred: keep the UI-owned path but make **the present prediction honest** — measure the actual
present phase (DXGI `GetFrameStatistics().SyncQPCTime` is already sampled at 1 Hz, `D3D12Device.cs:5042-5060`) and
feed the kernel `PresentSec` from it instead of `FrameQpc`, so positions are extrapolated to the vblank they land
on. This fixes "behind", not depth.

**What the plan misses:** §2.3 keeps `WaitForPresentSlot` before acquire *and* moves the turn to the tick — with
motion folded (RC1) those two waits are the same event and the loop should wait on **one** of them (the tick, with the
credit as a guard), not both in series; §10 Q4 (tick + 2·refresh vs `IDCompositionDevice::GetFrameStatistics().
nextEstimatedFrameTime`) is undecided and the log has no field to decide it — the `[render.pace]` line should carry
`tickQpc`, `slotOpenQpc`, `presentQpc`, `dwmSyncQpc` per second so `clockSampleSkewMs` becomes measurable on this box.

### Verify

- `[render.pace]` (new): `slotWaitAvg`, `i2p` (input QPC → attested display QPC) p50/p95 — target ≤ 25 ms p95.
- `scroll.trace`: `dtP95 ≤ 8.6`, `dtMax ≤ 9`, `late=0`.
- `gate.lease.sample-at-present`, `gate.lease.wheel-forwarded`, `gate.lease.trajectory-identical` (all exist, green).

---

## 4. Root cause 3 — every scroll frame is a full-window re-raster and a mostly-full re-record

### Mechanism (CONFIRMED)

- The scroll offset is a UI-thread write of the content node's `LocalTransform` + `TransformDirty|PaintDirty`
  (`Scroll/SceneScrollSink.cs:74-88`; `ScrollContentTransform.WriteContentTransform` :43-56). The recorder walks the
  content subtree every frame.
- **Repaint route.** `RepaintPolicy.Decide` sends any frame whose coalesced damage covers ≥ 60 % of the target to
  `FullDirect` (`Seams/Rhi/RepaintPolicy.cs:121,193`), documented as policy: "scroll lands here by policy and
  therefore costs exactly what it did" (`docs/design/subsystems/gpu-renderer.md:1462-1468`). A scrolled viewport's
  translated content damages the whole viewport band (`SceneRecorder.cs:1554-1560`), so a list that fills the page
  is always ≥ 60 %. On `FullDirect` the back buffer is fully cleared and every draw rasterises against a full-target
  scissor (`D3D12Device.cs:2091-2096 / 3950-3956, 2978-2982`). Hence `repaintPct=100` — not a bug, the designed
  fallback. The retained partial-repaint canvas is never engaged for scroll.
- **Span reuse.** The recorder has the right two-tier reuse: exact copy (`SceneRecorder.cs:1509-1528`, keyed on
  `spanInputSig` which mixes the full world transform) and **translated copy** (`:1529-1588`) keyed on `spanMoveSig`
  (translation excluded), gated on `descendantDirtyBits == 0`, `!directMovingScrollContent`, no `RecordDirtyContent`,
  a real `(dx,dy)`, and the translated bounds still inside the clip. For a plain ItemsView row list this is designed to
  be O(edge rows) per frame (`:2938-2947`). The content node itself always falls through (its subtree exceeds the
  viewport clip), which is correct; the rows beneath should copy.
- **Measured:** `spansReused=80-110` vs `spansReRecorded=300-626` per frame — reuse is landing on roughly a fifth of
  span-tracked nodes. `SpansReRecorded++` is per span-tracked node that was walked (`:1454, :2762`), so a row whose root
  fails the gate re-records every descendant span too.
- **Cost:** `record≈1.8 ms` render-side for 500-880 visited nodes (the walk visits every `Visible` node and culls by
  clip *after* visiting, `SceneRecorder.cs:1266-1297, 1914-1921`); `gpu=2.6-6.6 ms` — this **is** the GPU timestamp
  query (`FrameStats.GpuRenderMs ← TryGetGpuRenderSample().ExecutionMs`, `AppHost.cs:2163,4732`), not a CPU wait —
  for a full-window clear + 60-214 draws + 360-660 commands at ~2500×1600 on a UMA Adreno. Weak tier changes none of
  it (RC7).
- **Blur groups:** `blurGroups=1-2 blurHeld=1` on playlist/artist/album pages. `BlurGroupCount` counts self-blur
  nodes (`SceneRecorder.cs:1682-1698`); `blurHeld` = held (not re-Gaussian'd) during the scroll hold window
  (`AppHost.cs:4171-4178`). So the blur is *not* re-run per frame — good — but its source on a list page is not
  identified (no `Blur =` outside `app:Shell/Lyrics.UI.cs`; a `BakedBlur` cover bleed lives in `app:Shell/Deck.Faces.cs:1440`).
  HYPOTHESIS: a lyrics/deck surface stays mounted behind the page. Measurement: log the owning component of the
  `isBlurGroup` node once per burst.
- **Pixel snap** (`Scroll/ScrollSnap.cs`, `ScrollContentTransform.SnappedTranslation`): a pure per-frame translation
  round; `TryTranslationDelta` still resolves a clean `(dx,dy)`, so it does **not** defeat translated copy. Neutral
  for this report (it fixes glyph/rect drift, plan §8 "cause #1").

### Why translated copy misses (HYPOTHESIS — one Debug run decides)

The recorder already has the answer instrumented: `SpanMissMoveGuard / MoveKey / MoveGeometry / MoveClip /
ExactKey / ExactClip / RebaseRejected` (`SceneRecorder.cs:1590-1616`) behind `SpanMissDiagnosticsCompiledIn &&
stats.CollectSpanMisses` — compiled out of the Release engine Wavee ran. Candidates, in order:

1. `descendantDirtyBits != 0` (MoveGuard): a row whose subtree has any record-dirty node — the row under the pointer
   (hover fade), rows rebound by virtualization this frame (their `WriteColumns` marks), rows whose cover image just
   landed, the equalizer row.
2. `spanMoveSig` mismatch (MoveKey): the signature includes `clip`, opacity, scales, `userScrollActive`, focus/text-
   edit state — any of these changing per frame for rows (e.g. a per-row opacity reveal) breaks the key.
3. `RebaseRejected` (`:1583-1586`): a payload the translator cannot shift (an acrylic layer, an unknown opcode) rolls
   the copy back.

Measurement: run the Debug engine (or Release with `FLUENTGPU_DIAG`) with `CollectSpanMisses` for one burst and read the
seven counters; or the existing `--repaint-identity` / `--scroll-pace` probes.

### The principled fix

- **S2 retained content layer** (plan §2.2, `:163-211`; §6 `:1041-1117`): rasterise the content subtree once into a
  pooled band RT (viewport + velocity-skewed overscan, weak-tier cap 1·vp / 8 MiB), and per tick composite the band
  translated (snapped) through the viewport scissor — `PushRetainedLayer/PopRetainedLayer`, admitted to the layered
  `Partial` route beside `Opacity` (`RepaintPolicy.cs:149, 388-414`). A hit frame walks nothing under the viewport and
  the GPU cost becomes one band blit + chrome. The plan's "checkerboard policy" (miss ⇒ re-record the subtree into a
  new band, `Outrun` demotion) is sound. Gates `gate.retained.band-plan` / `gate.retained.eligibility` exist and pass.
- **Independently and first:** fix whatever makes translated copy miss (above) — it is today's per-row cost even
  without S2 and the translate-only fallback the plan keeps for ineligible scrollers (binds inside, acrylic/video,
  zoom ≠ 1).
- **Cull before visiting:** `SceneRecorder.Walk` visits every `Visible` node and culls after (`:1266-1297`). For a
  virtualized list the realized overscan is walked every frame. Pruning the recursion on `deviceBounds ∩ clip == ∅`
  when the subtree carries no blur halo/animation is a render-CPU win independent of S2 (`nodes 500-880` vs `draw
  60-214`).

**What the plan misses:** it treats the `SpansReRecorded` count as the expected fallback cost ("today's per-row
translated-copy re-record") without asking why the translated-copy hit rate is ~20 %; the weak-tier band memory
question (§10 Q1) is still open; and the 60 % `CoverageCutoff` is admitted to be unmeasured (`gpu-renderer.md:1504`).

### Verify

- `scroll.frames`: `spansReused ≫ spansReRecorded` (target: re-recorded ≈ 2 edge rows × spans-per-row), `record ≤ 0.5 ms`,
  `repaintPct` = viewport band share (not 100), `gpu` p95 ≤ 2 ms on the Adreno for a list scroll.
- Debug-only: `SpanMiss*` counters ≈ edge rows only.
- Gates: `DamageSuite`, `gate.retained.*`, `--repaint-identity` scenario `retained-band-straddle` (plan §7), `--scroll-pace`
  `retainedHit ≥ 0.8`.

---

## 5. Root cause 4 — full snapshot capture every scroll frame

### Mechanism (CONFIRMED)

- `SceneRecordingSnapshot.CanCaptureIncremental` refuses whenever `source.BulkMutationSeq > lastCapturedSeq`
  (`Scene/SceneRecordingSnapshot.cs:208-214`); `SceneStore.NoteBulkMutation()` sets `_bulkMutationSeq = _publishSeq + 1`
  (`Scene/SceneStore.Capture.cs:82`) — the "I touched captured columns I cannot enumerate" escape hatch.
- Two unconditional callers sit on the virtualized-scroll path: `FlexLayout.BeginMeasurePass`
  (`Layout/FlexLayout.cs:105`, every layout pass — its own comment assumes "scrolling is deliberately layout-free")
  and `Reconciler.WriteColumns` (`Reconciler/Reconciler.cs:5102`, every mount and every rebind — the recycle path
  `RealizeWindow/RealizeBoundWindow` runs each frame the window slides).
- Result: nearly every wheel frame recycles or measures at least one row → full capture. Log: `capture=0.5-0.9 ms /full:
  1750-3139` nodes on the UI thread per frame (`worst … capture=0.9/full:3139`), against `0.2 ms /inc:38` on the one
  frame nothing recycled (`navId=5`). ~0.5 ms × 120 = 6 % of the UI thread's time during a scroll spent copying
  2-3k unchanged nodes, plus the memory traffic; it is also the phase the design meant to be incremental
  (`threading-render-seam.md §3.4`, `:480-540`, "IMPLEMENTED").

### Fix (not in any plan)

Replace the blanket `NoteBulkMutation()` on the steady-state recycle path with precise per-node `NoteCaptureChanged`
marks for the columns a rebind actually rewrites (`LayoutInput`, `NodePaint`, `InteractionInfo`, text — a bounded,
enumerable set per row), and make `BeginMeasurePass` mark only the subtree it solves (or only when the solve wrote a
column that captures). Keep `NoteBulkMutation` for genuinely unenumerable writes (a full reconcile, a store compact).
`IncrementalCaptureTests.cs` pins the slot-baseline plumbing but has no case for "virtualization recycle during a
coast frame stays incremental" — add it (headless: an ItemsView scrolled one row per frame; assert
`LastCaptureWasIncremental` after the first frame).

### Verify

- `scroll.frames` `worst … capture=…/inc:` on ≥ 95 % of scroll frames; `capture ≤ 0.2 ms`.
- New `IncrementalCaptureTests` case + the existing `SnapshotCapacityReclaimTests`.

---

## 6. Root cause 5 — UI-thread churn and GC inside the gesture (the `~` frames)

### Evidence (CONFIRMED in the log)

- `hotAllocKB=5649` in a 2.5 s burst, `14593` in 6.9 s, `13190` in 1.5 s (`navId=4`: 73 KB/frame average, one frame
  `hotAlloc=877496` with `comps=8 measures=0`); `gc=5/2/1` in `navId=1` (a **gen-2** collection mid-scroll),
  `gc=3/1/0` in `navId=4`.
- `worst frameMs=5.1 flush=4.1 realize=4.1` (navId=1), `frameMs=6.1 flush=5.5 reactive=5.5` (navId=4),
  `frameMs=9.3 layout=6.0` (navId=3, `overBudget=1`) — UI frames over half a refresh, which is exactly when a
  production misses its tick and the kernel's next dt doubles (`scroll.trace` `late=2`, `~`).
- `frame.churn` census during scroll: `LineRow×54 (a=660K)`, `TableHost×1 (c=1.16 ms a=1006K)`,
  `ItemsView×2 (a=583K)`, `PaneSlot×11`, `RailSlot×13`; `worstCensus`: `SectionsHost×1 (a=516K)`,
  `StackHost×2 (a=448K)`, `NowPlayingOverlayHost×10 (a=216K)`, `ToolTip×10 (a=132K)`.

### Mechanism (partly HYPOTHESIS)

The census counts UI re-renders per type per frame. `NowPlayingOverlayHost×10 / ToolTip×10` in a scroll frame means
ten rebinds re-ran those hosts — the `ApplyProps` → `_props.Value = …` push (`app:Platform/Controls.Art.cs:236-240`)
re-renders on every recycle even when the row's uri did not change relation to the playing track; `ToolTip.WrapStable`
re-renders when the play name changes. `SectionsHost`/`StackHost`/`TableHost` allocating 0.5-1 MB in one frame is a
realize batch. Which of these is avoidable needs the per-component allocation census the engine already emits — the
fix is app-side and per component (stable props, `Key` on uri, occurrence keys), per
`docs/design/subsystems/component-props-contract.md`.

### Fix

- Engine: extend `RenderBudget`/the churn census to name the *signal* that woke each re-rendered component in a
  scroll frame (today it names the type). Consider a scroll-time allocation gate in `Wavee.Tests` style: a headless
  ItemsView coast of N frames asserts `HotPhaseAllocBytes` ≤ a budget once realization has settled.
- App: audit the ×10 hosts (`NowPlayingOverlayHost`, `ToolTip`) and the realize-batch allocators; move the
  `RelatesTo` decision to a per-row signal so a rebind to an unrelated uri does not re-render the host.

### Verify

`scroll.frames`: `hotAllocKB` ≤ ~1 MB per burst after the first realize batch, `gc=0/0/0`, `overBudget=0`,
`worst frameMs ≤ 4`; `scroll.trace` `late=0`.

---

## 7. Root cause 6 — the idle loop at ~250 turns/s

### Evidence and mechanism (CONFIRMED, `AppHost.ComputeWakeReasons` `:2566-2649`; `WakeDiagnostics.cs:91,189-193`)

`[wake] 30.0s fps=252.6 run=7579 rendered=194 | reconciled=237 layout=31 recordOnly=7311 | kept: frameNeeded=2591
runtimePending=799 anim=410 scrollAnim=6649 caret=3615 … timer=476 warmCadence=6533 imageReady=336
frameClockPoller=2141 … | sole: … scrollAnim=53 caret=149 warmCadence=23 frameClockPoller=65 | pollers=1:Stepper |
pollersSeen=3:Stepper×1962,TableTicker×14,DebounceTicker×168 | anim=0 … renderMotion=0 presents=2750`.

- `recordOnly=7311` = awake frames that neither reconciled nor laid out (`WakeDiagnostics.cs:91`) — the loop ran the
  paint/capture/publish phase 7311 times in 30 s to show 194 changes; `presents=2750` (92/s) means the render thread
  submitted+presented most of them (the byte-identical skip catches only frames whose stream *and* repaint set are
  empty, `AppHost.ShouldSkipRenderSubmit :1084-1087`). With a track playing (`pid=7548`): `presents=5252` (175/s) —
  above the panel rate, i.e. the RC1 double-present pattern at idle.
- `scrollAnim` 88 % of turns with no scroll: `_scrollKernel.WakeActiveCount>0 || Port.Pending>0 ||
  _scrollRouter.HasPendingFrameDelta || _scrollChrome.NeedsFrame` (`:2592`) folded into one bit. `ScrollKernel`'s
  settle bookkeeping (`ScrollKernel.cs:1403-1423`: `!IsSettled || Parked || RestorePending || EdgeHitPending`) and
  `ScrollBarChrome.NeedsFrame` both carry prior idle-power fixes (WaveeMusic #136) — a body stuck un-settled or a
  chrome that never parks is a regression of one of them. The bit must be split to say which.
- `warmCadence` 84-86 %: `_warmCadenceUntilMs` re-armed on input/publish (`:3341-3343, 3474-3475`) — at this rate
  something re-arms it continuously (the poller below is the obvious suspect).
- `frameClockPoller` 28-66 %: `_frameClockSig.HasSubscribers` (`:2619`); the dominant subscriber is the lyrics
  `Stepper` (`app:Shell/Lyrics.UI.cs:2029-2044`), mounted while `motionLive || cascading || follow != Following`
  (`:2010-2011`) — i.e. in the ordinary "lyrics open, not auto-following" state it ticks every frame forever.
- `caret` 47 %: benign co-occurrence (cadence-paced blink); `anim=0` at sample time; `Stepper×1962/4894`,
  `TableTicker`, `DebounceTicker`, `ExpanderResizeWatcher` are self-unmounting pollers — `Stepper`'s and `TableTicker`'s
  mount gates are broader than "currently animating".

### Does it hurt scroll?

Directly: it burns UI-thread CPU (a paint pass per turn) and, with playback, a full GPU frame per present on the
same queue the scroll frame needs — on a UMA part that is shared bandwidth and thermal headroom. Indirectly: the
equalizer's presents *are* RC1's motion re-presents. It does not by itself cause the scroll jitter; RC1 does.

### Fix

Split `scrollAnim` into its four constituents in the `[wake]` census; make `WakeActiveCount` exclude bodies that are
settled-but-`Parked` unless chrome needs a frame; audit `warmCadence`'s re-arm sites and length; narrow the lyrics
`Stepper` (and `TableTicker`) mount gate to "something is moving"; and route render-owned motion (the equalizer, if it
is a compositor row) through the existing `RenderOwnsCompositor` exclusion (`:2601-2610`) so it does not also wake
the UI. For the equalizer specifically: a 30 Hz full-window present while a track plays is its own admitted cost
(`app:Platform/Controls.cs:557-561`); with S2's band and partial repaint it becomes a 3-bar-sized repaint.

### Verify

`[wake]` at idle: `run ≤ ~10/s` with playback paused, `presents ≤ 30/s` with a track playing (the equalizer's own
rate), `sole: scrollAnim=0`, `pollers=0` when lyrics are static.

---

## 8. Root cause 7 — the weak tier is not a tier on this path

`GpuProfile.IsWeak` (UMA ⇒ Weak, `Foundation/GpuProfile.cs`, `D3D12Device.cs:1358-1371`) gates only pool idle/eviction/
sample cadences (`AcrylicCompositor.cs:493`, `OpacityLayerCompositor.cs:852`, `D3D12Device.cs:4967`,
`GlyphRenderer.cs:215`), a skeleton-motion simplification and the scroll-time upload throttle (`AppHost.cs:4184`).
Nothing lowers blur resolution, drops an effect, or changes the clear/scissor policy. The design intent for acrylic
under scroll velocity was a flat tint until the canvas RT lands (`backdrop-effects-animation.md:271,661`); what
shipped is `AcrylicScrollHold` (1-in-4 re-blur). For a list page today this matters less than RC3 (the blur is held),
but S2's band budget (§2.2: 1·vp ahead / 8 MiB on weak) is the first tier-aware decision on this path and §10 Q1
asks the owner whether to ship the band on UMA at all — the answer should come from the `--scroll-pace` census on this
box, not be guessed.

---

## 9. Root cause 8 — redundant knobs that make RC1 racy

- `RenderFrame.InteractivePresent` → `SuppressVsyncOnce + SuppressLatencyWaitOnce` (`AppHost.cs:1074-1076, 4491-4493`).
  Under the credit discipline the latency skip is already a no-op (`D3D12Device.cs:1829-1831` skips when
  `creditHeld`), and interval 0 with a single in-flight present is indistinguishable from interval 1 on a composition
  swapchain. Its own comment (`RenderFrame.cs:29-33`) says it was restored on 2026-09-23 only because motion ticks
  still present between UI frames — delete it with S1 (plan §5.2).
- The loop waits on three things (publish wake, tick, waitable) with two "one present per tick" guards
  (`_lastMotionTickSeq` for motion, nothing for fresh) and a third heuristic (`UiProducing`). One tick, one credit,
  one decision (`PresentCadence`) replaces all of it.
- `_nextTick`/`_tickPeriod` software period (`RenderThread.cs:47,120,218`) is dead weight once the display clock is
  the turn; keep `TickBackstopMs` as the stalled-compositor backstop only (plan §5.1).

---

## 10. What the existing plans cover, and what they miss

| Plan | Covers | Misses (this report) |
|---|---|---|
| `compositor-scroll-implementation.md` (2026-09-22) | RC1 (§2.3, §3.4, §5.1: one present per tick), RC2 (§2.1-2.4, §3.3, §4.1: render-owned bodies, sample-at-present, urgent wheel), RC3 GPU half (§2.2, §6: retained band), RC8 (§5.2 delete interval 0), attested stats (§3.5, §5.3), the `[render.pace]` line. S0 landed and green. | Depends on ISO Phase 2 that does not exist — S1 must be re-cut onto today's single-source loop. Does not ask why translated-copy reuse hits ~20 % (RC3 record half). Says nothing about RC4 (capture), RC5 (churn/GC), RC6 (idle), RC7 (tier policy beyond the band cap). Leaves §10 Q1/Q3/Q4 open with no log field to decide them. Keeps slot-wait *and* tick as two serial waits. |
| `detached-window-render-isolation-implementation.md` | Per-target `TargetFrameState` (landed), the multi-source loop and `WindowPacingScheduler` (§4, **not landed**). | Its Phase 2 is the missing dependency above; nothing scroll-specific. |
| `threading-render-seam.md` §11.1 | The UI production gate (one frame per tick, landed) and the depth-1 rationale. | The depth-1 measurement predates the wait-first order; the doc still describes the waitable as the only render-side pacer while the code also runs a display-clock turn. Needs the S1 amendment. |
| `gpu-renderer.md` §13.1 | `FullDirect` for scroll "by policy"; canvas/partial for the rest. | The retained band is the partial route for scroll; §13.1 point 4 should reference it once S2 lands and the 60 % cutoff should be measured on this box. |

---

## 11. Order of work

1. **S1 (pacing) on today's loop** — `RenderThread.Loop` turn = tick while `MotionDue` (else publish wake presents
   immediately), `PresentCadence.Decide` the sole presenter, delete `InteractivePresent`/`SupportsCompositedIntervalZero`
   /`_nextTick`, remove `UiProducing()` once the gate below is green, add `PresentStatisticsLedger` + `[render.pace]`.
   Files: `RenderThread.cs`, `RenderFrame.cs`, `SceneFramePublisher.cs`, `AppHost.cs` (pacing rows), `D3D12Device.cs`
   (ledger, gauges), `Rhi.cs`, Wavee `Platform.Host.cs` whitelist + `Diagnostics.Host.cs` fields. Expected: RC1 gone,
   `presented ≤ vblanks`, `renderMotion=0` in bursts, `late=0`.
2. **RC4 capture ledger** (`Reconciler.WriteColumns`, `FlexLayout.BeginMeasurePass`, `SceneStore.Capture.cs`) + the
   new incremental-capture test. Expected: `capture ≤ 0.2 ms /inc:` on scroll frames.
3. **RC3 record half**: one Debug run with `CollectSpanMisses` on this box → fix the dominant miss cause (likely
   descendant dirty bits from hover/rebind, or a `spanMoveSig` component that changes per frame) → also prune the
   recorder walk on clip. Expected: `spansReRecorded` ≈ edge rows, `record ≤ 0.5 ms`.
4. **S1 lease + S2 band** (plan Waves S1-A/B and S2), weak-tier band policy decided from the census. Expected:
   `gpu` p95 ≤ 2 ms, `repaintPct` = band share, `i2p` p95 ≤ 25 ms, `retainedHit ≥ 0.8`.
5. **RC5/RC6 hygiene** (app-side mostly, in parallel with 3-4): split `scrollAnim`, narrow `Stepper`/`TableTicker`,
   stable props on the ×10 hosts, allocation gate. Expected: idle `run ≤ 10/s`, scroll `gc=0/0/0`.

---

## 12. Verification matrix

| Claim | Gate / probe | Log field(s) to read |
|---|---|---|
| One present per vblank | `gate.lease.present-once-per-tick`; new `RenderThreadLifecycleTests` mid-interval-publish case | `scroll.frames`: `presented ≤ wallMs/8.33`, `renderMotion`, `raceHits`, `gaps`; `[render.pace]`: `dropped=0 repeated≈overruns` |
| Latency / phase | `gate.lease.sample-at-present`; `--scroll-pace` probe | `[render.pace]`: `i2p` p50/p95, `slotWaitAvg`, `clockSampleSkewMs` modal share; `scroll.trace`: `dtP95 ≤ 8.6`, `late=0` |
| Record/reuse | Debug `SpanMiss*` counters; `DamageSuite`; `--repaint-identity` | `spansReused/spansReRecorded`, `record`, `nodes` vs `draw` |
| Retained band | `gate.retained.*`; `--repaint-identity retained-band-straddle`; `--scroll-pace retainedHit` | `repaintPct`, `gpu` p95, `retainedHit/Miss` |
| Capture | new `IncrementalCaptureTests` recycle case | `capture=…/inc:` share of scroll frames |
| Churn / GC | scroll-coast allocation gate (headless) | `hotAllocKB`, `gc=a/b/c`, `overBudget`, `worst frameMs`, `frame.churn` census |
| Idle | existing `[wake]` census with `scrollAnim` split | `run`, `recordOnly`, `presents`, `sole:` |
| Tier | `--scroll-pace` census on the Adreno (band bytes, `gexec`) | `[render.pace]` `gpu bytes`, `gpu` |

---

## 13. Open hypotheses and the one measurement each needs

1. Exact phase of the latency-waitable signal vs the compositor tick on this box → `[render.pace]` with
   `tickQpc/slotOpenQpc/presentQpc/dwmSyncQpc`.
2. Which channel arms render motion during a wheel scroll (hover fades vs equalizer vs a transition) → the render
   slab's channel census inside `scroll.frames`.
3. Why translated-copy reuse misses on ~80 % of spans → Debug `SpanMiss*` counters for one burst.
4. What the blur group on list pages is → log the owning component of `isBlurGroup` nodes once per burst.
5. Whether the equalizer's `Prop.Of` transform is a compositor row (render motion) or a UI paint write → read the
   binding path once, or the census in (2).
6. Whether `scrollAnim`'s 88 % idle persistence is a stuck body, `Parked`/`RestorePending`, or chrome → split the bit.
