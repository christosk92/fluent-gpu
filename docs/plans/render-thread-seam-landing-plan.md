# Render-Thread Seam — Landing Scope & Work-Plan

Scoping doc (not canon). The canonical design is `design/subsystems/threading-render-seam.md` (§ refs below).
This plan sizes the work, names the hard problems, the decision points, the build order, and what
"correct" requires — so the seam can be landed in verifiable steps without regressing a working engine.

## 1. Why (the win, and what it is NOT)

Today the whole 13-phase frame runs on the UI thread, and GPU submit/fence/present is charged to it
(`AppHost.cs:36`, `:428-435`). The two blocking GPU-fence waits (`WaitForLatency` + `WaitForFrame`) are
**the bulk of measured "submit"** (`FrameStats.FenceWaitMs`, `D3D12Device.SubmitDrawList:428-435`) — a
sustained GPU stall bounds straight back to the UI thread and drops scroll/animation FPS.

The seam moves record/submit/present onto a render thread so a heavy submit no longer stalls the UI
thread that services input. **This is architectural insurance + a general smoothness win (esp. weak/iGPU),
NOT the fix for the reported lyrics-scroll symptom** — that is already fixed and gate-verified by the blur
cache (Phase 2) + scoped scroll-defer (Phase 3) + acrylic directBB (Phase 4). The seam is "decoupled, not
invincible": a sustained GPU stall still eventually bounds back via backpressure (§11).

## 2. THE decision: Cut A (submit-only) vs Cut B (canon: record + submit)

| | **Cut A — submit-only (proposed v1)** | **Cut B — record + submit (canon)** |
|---|---|---|
| Seam carries | the finished `DrawList` bytes + `FrameInfo` + the frame's pending GPU uploads | a POD `SnapshotColumns` of the scene (§3); record runs on the render thread |
| UI thread keeps | reconcile + layout + anim + **record** (already sub-ms, zero-alloc) | reconcile + layout + anim + **PUBLISH snapshot** |
| Render thread does | submit + present + the fence waits + GPU uploads | **record** + submit + present + uploads |
| Moves the fence-wait stall off UI? | **Yes** (the primary win) | Yes |
| Moves record off UI? | No (record stays; it is not the bottleneck — the stall is in submit) | Yes |
| `SceneRecorder` rewrite? | **No** — record is unchanged; it reads the live store on the UI thread | **Yes, large** — see problem #1 |
| `SnapshotColumns` of the whole scene? | **No** | Yes (§3) |
| Canon-compliant? | No — deviates from Cut B; requires a canon reconciliation (`SPEC-INDEX.md` + `threading-render-seam.md` ownership) | Yes |
| Relative effort / risk | **Medium** / medium | **Large** / high (recorder rewrite dominates) |

**Recommendation: Cut A for v1.** It captures the actual measured win (the fence-wait stall) at a
fraction of the risk, with no `SceneRecorder` rewrite. Adopt Cut B later only if record itself ever shows
on the UI-thread budget. Choosing Cut A means we deviate from the canon's Cut B and must reconcile the
owning docs (`design/subsystems/README.md` ownership map + `SPEC-INDEX.md §2`) and run `check-canon.ps1`.

> **Open decision for the owner:** Cut A (recommended, pragmatic) or strict-canon Cut B?

## 3. Current state — what exists vs greenfield

**Exists (single-thread, working, 533 gates green):**
- The 13-phase loop and the record→skip-submit→submit→present sequence (`AppHost.cs:1210-1256`).
- `D3D12Device` owns every ComPtr + `SubmitDrawList`/`Present`/`WaitForLatency`/`WaitForFrame`/uploads.
- `SceneRecorder.Record` reads the **live** `SceneStore`.
- **Per-frame-banked upload buffers** already exist for the exact CPU↔GPU race the seam faces
  (`SubmitDrawList:457-459`: "Every pipe banks its instance upload buffer by back-buffer index"). This is
  latent seam infrastructure.
- `FrameStats.FenceWaitMs` isolates the stall we are moving.

**Greenfield (none exist in `src/` yet):** `ThreadGuard`, `SceneFramePublisher`, `SnapshotColumns` (Cut B
only), `QuarantineLedger`, `DrawListArenaRing`, `RhiHandleTable` retire-fence ring, device-lost rendezvous,
`ReconcileSlicer`/`StoreReadLedger`/`EffectRef` gen-stamp, worker pool. New `FluentGpu.Engine/Hosting/Threading/`.

## 4. The hard problems (ranked by risk)

1. **[Cut B only, biggest] `SceneRecorder` reads live state the snapshot deliberately excludes.**
   `SnapshotColumns` (§3.1) copies Topology/Bounds/NodePaintLite/Flags/PayloadRef/WorldTransform — and
   **explicitly excludes** `InteractionInfo`, scroll state, edge-fade config ("input/UIA read live
   UI-thread state, never the snapshot"). But `Record`/`Walk` read all three: hover/press interaction
   scale (`SceneRecorder.cs:256-262`), scroll state incl. the new `scrollInMotion` defer + scrollbars +
   edge cues (`:695-714`, `:243-258`), and `TryResolveEdgeFade`. **Cut B requires either expanding the
   snapshot to carry these or refactoring the recorder to not need them — a redesign of the snapshot
   contract + the recorder.** (Cut A sidesteps this entirely: record stays on the UI thread reading live
   state.)
2. **ComPtr ownership migration (both cuts).** Canon: the render thread is the ONLY thread that touches a
   ComPtr (`threading-render-seam.md §1.1`, §8). Today the UI thread calls `SubmitDrawList`, image
   decode/upload, `CreateSwapchain`, `Resize`, `WaitForGpu`, device-lost handling — all ComPtr work. These
   move to the render thread or are marshalled to it. Cut A still touches ComPtrs at **record** time
   (glyph shaping/atlas, `EnsureTexture`) — those specific accesses must be made render-safe or moved.
3. **Upload/atlas coordination.** Glyph rasterization + image-texture uploads are produced around record
   (UI) and flushed at submit (render). The per-frame banking (`:457-459`) is the start; needs a
   producer/consumer handoff + the retire-fence (§8) so a texture/atlas page isn't freed while an
   in-flight submit references it.
4. **Resize / swapchain / device-lost across threads (§9).** `ResizeBuffers` and device re-creation are
   render-thread ops; the UI pauses publish and waits on a `Volatile` reason word; a rendezvous protocol
   coordinates re-realization from CPU state.
5. **Present pacing / latency.** `WaitForLatency`/`WaitForFrame` move to the render thread; the UI
   self-throttles via publisher backpressure (§11, cap-1 DropOldest wakeup + the 3-slot triple buffer).

## 5. Build order (canon §19 — single-thread-correct FIRST, flip LAST)

The canon is emphatic: ship single-thread-correct, flip parallelism only behind a green soak.

- **Step 1 — single-thread behind the seam shape.** Build `ThreadGuard`, `SceneFramePublisher`,
  `QuarantineLedger` (+ `SnapshotColumns` for Cut B / triple-buffered `DrawListArenaRing` for Cut A) and
  run them **single-threaded**: the UI thread both `Publish`es and `TryAcquire`s; `Quarantine == 0`
  logically. Land the P3 (`StoreReadLedger`) + P5 (`EffectRef` gen-stamp) invariants. **All 533 gates + the
  new single-pass goldens green.** No behaviour/perf change yet.
- **Step 2 — COM hardening + validation spine** (generated/confined COM already largely present).
- **Step 3 — enable reconcile time-slicing / discard-restart** on the UI thread (still single-thread render consumer).
- **Step 4 — spawn the render thread**, single-consumer, force-sync drain first; UI→render `DrawList`
  arena transfer (Cut A) / snapshot consume (Cut B); wire the retire-fence handshake (§8).
- **Step 5 — FLIP `Quarantine` 0 → `RenderInFlightDepth + 1`** — ONLY after the `seam.race` soak is green
  for the required nightly streak.
- **Steps 6–7 — off-thread tessellation / off-thread reconcile** (optional, later).

## 6. Validation — none of it is headless-only

New gates to author (the headless `VerticalSlice` alone cannot cover concurrency):
- `ConcurrentRecord_MatchesSingleThreadedGolden` — render-thread output **byte-identical** to the
  single-thread golden.
- `ThreadGuard` + `QuarantineLedger` **deterministic-throw silence** under the `seam.race` soak (the soak
  pauses the render thread 3+ publishes and asserts no slot reclaims before `_lastConsumedSeq` advances, §5.3).
- **`seam.race` soak** (nightly, `FGGUARD` build) sweeping channel-capacity + reader-stall as fuzz params
  (§11) — must be GREEN for the required streak before Step 5's flip.
- Present-stall bench: bounded UI input latency, non-growing quarantine.
- **Requires real GPU execution + a soak harness.** Plan for GPU-machine runs; the flip is gated on the soak.

## 7. Effort, risk, sequencing

- **Cut A:** Step 1 foundation (publisher + 3-deep DrawList arena ring + ThreadGuard) is ~1 focused
  session, gate-green, single-thread. Step 4 (spawn render thread for submit/present + upload handoff +
  retire-fence) is the hard session (ComPtr migration + upload coordination). Step 5 flip gated on the
  soak. **~2–3 sessions + GPU/soak validation.** Keeps the engine building + gates green at every step.
- **Cut B:** add the `SnapshotColumns` design + the `SceneRecorder` rewrite (problem #1) on top —
  **multi-session**, recorder rewrite dominates.
- **Non-negotiable:** every step lands single-thread-correct + gates green; the async flip is the LAST
  step and is soak-gated. No step may leave the engine non-building.

## 8. Immediate next actions (once Cut A/B is chosen)

1. Reconcile the canon if Cut A (own the deviation in `SPEC-INDEX.md §2` + `subsystems/README.md`; run `check-canon.ps1`).
2. Create `FluentGpu.Engine/Hosting/Threading/` + land `ThreadGuard` (`[Conditional("FGGUARD")]`, zero-cost
   in Ship) and add `FGGUARD` to the test/CI configs in `src/Directory.Build.props`.
3. Land the Step-1 foundation (publisher + arena ring / snapshot) single-thread; add the
   `ConcurrentRecord` golden (single-pass variant) to `FluentGpu.VerticalSlice`.
4. Author the `seam.race` soak harness (out-of-band from the headless slice; GPU-capable).
5. Steps 4–5 on a GPU machine, behind the green soak.

## 9. Landed (status)

- **Cut A chosen** (submit-only) — owner decision (§2/§39). The seam carries the finished DrawList, not a `SnapshotColumns`; record stays on the UI thread.
- **Step 1 — LANDED (2026-07-01), single-thread, gate-green.** New `src/FluentGpu.Engine/Hosting/Threading/`: `QuarantinePolicy` (derived `Quarantine = RenderInFlightDepth + 1`), `RenderFrame` (Cut A carrier — arena-index + `FrameInfo`), `DrawListArenaRing` (≥3 pinned arenas, geometric grow, zero-alloc steady), `SceneFramePublisher` (triple-buffer + both-directions-volatile + `PickFreeSlot`), `QuarantineLedger` (consume-gated reclaim). Wired into `AppHost.Paint` as a **single-thread pass-through**: record → `WriteFront` copy → `Publish` → `TryAcquire` (same thread) → `SubmitDrawList` from the acquired arena → `Rotate`. Byte-identical to a direct submit; `Quarantine` logically 0. `ThreadGuard` now binds `Ui` at the top of `Paint` (covers the WndProc `PaintRequested` repaint path, not just `RunFrame`). Verified: `gate.seam.publish-consume` golden + **534 VerticalSlice checks green** (zero-alloc tripwire held), real-D3D12 `--screenshot` renders identically through the seam.
- **Step 4 (force-sync) — LANDED (2026-07-01), DEFAULT OFF (`FG_RENDER_THREAD`).** `RenderThread` (a dedicated `fgpu-render` thread bound `Render`) runs SuppressVsync + `SubmitDrawList` + `Present` + `Rotate`; the UI publishes then BLOCKS in `DrainSync` until presented (single-consumer, no overlap yet). `ThreadGuard` binds `Ui` in `Paint` / `Render` on the loop; `RenderFrame.SuppressVsync` carries the keepAlive bit render-side; `AppHost` disposes the thread before the device. Verified: default (thread-off) path unchanged (**534 gates green**); the render-thread path renders **byte-identical** to single-thread on real D3D12 (`--screenshot` MD5-equal). Submit/present + the blocking GPU fence-waits now EXECUTE off the UI thread — but force-sync means **no perf win yet** (the UI still blocks); that is the point of landing it safe first.
- **`seam.race` soak — LANDED as a deterministic real-thread gate (2026-07-01).** `gate.seam.race` (VerticalSlice) spawns a `Render`-bound consumer thread, event-sequences it (no sleeps ⇒ deterministic), and asserts the consume-gated quarantine holds across the cross-thread Volatile publish/consume handshake under a stalled reader — a slot freed at seq p stays quarantined until `LastConsumedSeq` passes `p + Quarantine-1`. This is the in-repo validation of the §5.3 invariant the async flip rests on. (The *nightly, GPU, fuzzed* soak of §6/§11 — sweeping channel-capacity + reader-stall on real hardware over a streak — is still the operational gate for the default-on flip.)
- **The exact async blocker (verified by reading the code, not the plan).** `SceneRecorder` (record) touches ZERO ComPtrs — it emits pure POD (glyph/image by handle); glyph-atlas upload is already render-side inside `SubmitDrawList`. The one remaining UI-thread ComPtr touch on the frame path is **`_device.UploadImage`** (the image pump), plus resize / device-lost / swapchain-create. **Step 5 (the async flip) cannot be safely enabled until `UploadImage` becomes a POD producer→render-thread-consumer handoff** (§4 R0a) — otherwise the UI's pump races the render thread's submit (the concurrent-ComPtr hazard). Force-sync (Step 4) is safe precisely because the two threads never overlap.
- **Step 5 — the async flip: CODE LANDED + FUNCTIONAL + byte-identical-verified (2026-07-01), gated `FG_RENDER_ASYNC`, default-OFF.** `RenderThread` async mode + the non-blocking `WakeAsync` publish are implemented; the arena reuse is safe by construction (the publisher owns 3 per-slot arenas via `PickFreeSlot`, so the UI never writes the slot the render thread reads). The `--screenshot` capture race (a UI-thread `CaptureBgra` that resets the command allocator + touches the fence, racing the render thread) is fixed by `AppHost.QuiesceRenderThread()` — stop+join the render thread so the UI is sole GPU owner before capture. With that, `FG_RENDER_ASYNC=1 --screenshot` renders **byte-identical** to single-thread on real D3D12 (MD5-equal): the async render loop + off-thread present are verified correct.
- **What blocks flipping the DEFAULT on (each would crash the shipping app if skipped, so the default stays off until all land):** (1) **`UploadImage` + `EvictImage` handoff** — the image pump's texture upload/evict are UI-thread ComPtr ops that race the render thread's submit in async; Wavee's album-art decode would crash. FEASIBLE: `DecodeScheduler` already decodes into owned `ArrayPool` buffers (`Done.Buffer`), so the pump can hand the owned buffer to the render thread (drain + UploadImage + return to pool) rather than upload inline. (2) **resize / device-lost rendezvous** (§9) — `ResizeBuffers` / device re-create are UI-thread ComPtr ops that race present; a window resize would crash. (3) the retire-fence for resource freeing (§8). (4) device/swapchain **CREATION** migration. THEN the fuzzed **GPU soak** (§6/§11) green over the required streak. The in-repo `gate.seam.race` validates the quarantine invariant; the nightly GPU soak is the operational gate for the default flip. Force-sync (`FG_RENDER_THREAD=1`) is safe today because the threads never overlap.
