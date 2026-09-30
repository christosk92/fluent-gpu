# Compositor-owned scroll: render-thread pacing, present-time sampling, retained content — implementation plan

> **SUPERSEDED (2026-09-23) by [`scroll-rework-implementation.md`](scroll-rework-implementation.md)** — the render-thread
> lease/pacing mechanism here (`RenderScrollLease`, `ScrollLeaseCapture`) is deleted wholesale and replaced by
> `ScrollPoser` reading a stateless `p(t)` plan; retained-content banding is superseded by "cheap rows, realize all."

Status: **execution-ready, awaiting the owner's go** (2026-09-22). Every `file:line` below was re-verified against the
working tree on 2026-09-22. Abbreviations: `A:` = `src/FluentGpu.Engine/Hosting/AppHost.cs`, `RT:` =
`src/FluentGpu.Engine/Hosting/Threading/RenderThread.cs`, `D:` = `src/FluentGpu.Windows/D3D12/D3D12Device.cs`,
`SR:` = `src/FluentGpu.Engine/Render/SceneRecorder.cs`, `K:` = `src/FluentGpu.Engine/Scroll/ScrollKernel.cs`,
`SS:` = `src/FluentGpu.Engine/Scene/SceneRecordingSnapshot.cs` (+ `.Animation.cs`), `W32:` =
`src/FluentGpu.Windows/Pal/Win32Platform.cs`, `ISO:` = `docs/plans/detached-window-render-isolation-implementation.md`.

Written for implementation subagents with **no other context**: each wave names its files, gives the code, and states
the gate that proves it. Engine rules that bind every change (CLAUDE.md): 0 managed allocations in frame phases 6–13
and on every render-thread path this plan adds; the render thread owns every `ComPtr`; the `FluentGpu.VerticalSlice`
transitive closure stays TerraFX-free (every decision class lives in `FluentGpu.Engine`); **no source-text tests**; no
environment-variable behaviour switches; no legacy paths (replace, delete); design docs are canon (§9 lists the docs to
reconcile and `check-canon.ps1` is the proof).

---

## 0. The two findings, restated against the code, and what this plan does about them

**A — pacing.** A scroll frame is published with `interactivePresent = !keepAlive && scrollActive &&
_swapchain.SupportsCompositedIntervalZero` (A:4427) and the render thread turns that into `SuppressVsyncOnce()` **and**
`SuppressLatencyWaitOnce()` (`ApplyPresentPacing`, A:1021-1025; the inline path repeats it at A:4491-4492), so the
present runs at `interval = 0` (D:4856-4861) **and** skips the latency credit (D:1859-1860). `RenderFrame.InteractivePresent`'s
own doc says the opposite — "retain the ordinary frame-latency wait" (`RenderFrame.cs:36-39`). Independently, the render
loop presents motion ticks between UI publishes: a `_needsTick` turn calls `_tick` = `RenderMotion` →
`SubmitPresentOnRenderThread(_activeRenderFrame)` (RT:145, A:533-536) on a `_nextTick` period (RT:150) that is *not*
the compositor tick, so a UI publish and a motion tick land as two presents inside one vblank. DWM keeps the last
present per vblank; the stamp-based `missedVsyncs` (A:5433-5440, A:5606-5612) sees consecutive `Present()` returns
< 1 refresh apart and reads 0. The OS-attested count already exists but is only carried as a diagnostic in the latency
row (`attestedPlus1`, A:5447-5459) and never replaces the stamp-based `FrameStats.MissedVsyncs` (A:276, A:4670).

**D — every scroll frame is a UI-thread frame.** `_scrollKernel.Tick(in scrollClock)` runs inside `Paint` (A:3771) at
`FrameSec = FrameQpc` (A:3726) — the *production* tick, not the present. `SceneScrollSink.Apply` writes the content
child's `LocalTransform` and marks it (`SceneScrollSink.cs:89-91`); the render thread's recorder sees
`TransformDirty` on the content (SR:1392-1393), damages the whole viewport (SR:1596) and re-records/rebases every row
span (SR:1554-1619). Nothing is a cached-layer translate. `TryLease`/`Return` (K:184-212) and `ScrollLeaseCapture`
(SR:83-110, SR:2796-2811) are reserved and unused. Wheel packets are deferrable until the next compositor tick
(`PacedInputWaitClassifier.IsDeferrable`, W32:263-265), and the kernel only sees them at the next produced frame.

**Reference model (verified in the local clones — §1.4).** Chromium samples impl-thread scroll/animation at the
BeginFrame's `frame_time` (`cc/trees/layer_tree_host_impl.cc:843-871`), rounds the drawn scroll translation to device
pixels in the transform tree (`cc/trees/property_tree.cc:913-944`), and draws at most once per BeginFrame
(`components/viz/service/display/display_scheduler.cc:772-808`). Firefox samples APZ at *last compose + vsync
interval* (`gfx/layers/wr/WebRenderBridgeParent.cpp:1133-1152`, `gfx/layers/apz/src/APZSampler.cpp:77-84`), composites
once per vsync (`gfx/layers/ipc/CompositorVsyncScheduler.cpp:113-125, 231-268`), pre-rasters a velocity-sized
displayport (`AsyncPanZoomController.cpp:4638-4666, 4535-4539`) and feeds the async offset back to layout with an origin
that cannot clobber APZ (`APZCCallbackHelper.cpp:382-399`, `nsLayoutUtils.cpp:8629-8640`). WebRender applies a scroll
as a frame-level offset update with no new display list (`render_backend.rs:470-482`) and snaps the offset to device
pixels (`spatial_node.rs:311-321`).

**What this plan does.** (1) The render thread owns Ballistic/Driven (wheel, programmatic, fling-landing) scroll
bodies through the existing lease seam, ticks them on the compositor tick it already subscribes to, samples at the
predicted present time, and writes the offset into a render-thread-owned transform — the compositor overlay the
animation twin already uses (`SS.Animation.cs:171-191`) — so the recorder applies it. (2) A **retained content
layer** (`LayerKind.Retained`): the content subtree is rasterised once into a pooled band-sized RT (viewport + a
velocity-skewed overscan) and each scroll tick composites that RT translated and clipped; the UI thread only
realizes rows ahead, off the scroll's critical path, and adopts one authoritative offset per frame. (3) Exactly one
present per vblank per window: interval-0 presents are deleted, motion ticks and UI publishes fold into one turn,
and the attested DXGI frame statistics replace `missedVsyncs`. (4) Wheel/kernel commands reach the render-thread
body without waiting for a UI frame.

**Honest limits, stated up front.** Drag (touch, DirectManipulation, hi-res wheel-fallback contact streams) stays
UI-owned: contact samples arrive through the UI message pump and layout `Reclamp` needs the UI (scroll-v3 §6's
"honest ceiling"). Bodies with continuous `ScrollBind` targets *inside* the content, acrylic or video inside the
content, zoom ≠ 1, or a nested scroller in motion take the **translate-only fallback** (render-thread pacing and
present-time sampling, today's per-row translated-copy re-record): same latency win, no GPU saving. Realizing new
rows re-rasters the band once per realize batch (tiles are a follow-up, §10). A sustained GPU stall still bounds back.

---

## 1. Verified as-built facts this plan builds on

### 1.1 Cut B landed: the recorder runs on the render thread from a snapshot, with a render-owned overlay

`docs/plans/render-thread-animation-design.md` (not executed as written) assumed Cut A and proposed "Variant B
compositor groups". Since then the record moved to the render thread: `RenderFrame.HasScene` (`RenderFrame.cs:13-14`),
`SceneFramePublisher.PublishScene` (`SceneFramePublisher.cs:163-216`), `SceneRenderFrame.Record` (`SceneRenderFrame.cs:93-103`)
called from `SubmitPresentOnRenderThread` (A:1160). The render thread owns compositor animation rows
(`RenderCompositorAnimations.Adopt/Tick`, A:1126-1129) and poses them **on top of** the snapshot's authored columns
through a bounded overlay row pool (`SS.Animation.cs:171-191` `CompositorPaint`, `SS:557-563` `Paint` resolves the
overlay, `SS:569-571` `Flags` reports `TransformDirty` from the self epoch, `SS:572-583` dirty trail). Feedback rides a
reverse mailbox (`_recordFeedback`, A:407-408, published at A:1257, imported at A:1318-1344 → `ApplyCompositorFeedback`,
`AnimScheduler.Compositor.cs:126-164`). **This is exactly the seam a render-owned scroll offset needs; the animation
design's §3.2 row "scroll offset … UI … explicitly out of scope" (line 265) is superseded by this plan (§9).**

### 1.2 The render thread already has a display-clock subscription and a per-window credit

`IRenderDisplayClock` (`Pal.cs:612-617`) is created per window (`W32:1590-1595`) from `Win32CompositorClock.CreateRenderSubscription`
(`Win32CompositorClock.cs:162-171`); its event is set on every published tick (`:294-295`); the loop waits on it only
while `motionDue` (RT:86-98). The tick's `TickSeq`/`TickQpc` (`:122-123`) are UI-readable but **not** exposed on the
render subscription. The present-slot credit (`WaitForPresentSlot`, D:5255-5263; `LatencyCreditHeld`, D:6024; spent at
D:4867) is a per-window semaphore with `MAX_FRAME_LATENCY = 1` (D:56, D:1484-1485). Terminal's `AtlasEngine` waits the
waitable then presents `Present1(1, 0)` (`terminal/src/renderer/atlas/AtlasEngine.r.cpp:436-449, 498`); makepad keeps
one credit per swapchain refilled only when a present retires (`makepad/platform/src/os/windows/win32_app.rs:803-831`,
`d3d11.rs:1890-1909`). The isolation plan's Phase 2 (ISO §4) generalises this to a per-window wait set — this plan's
pacing sits on that loop.

### 1.3 The scroll kernel is already thread-agnostic by design

`ScrollKernel` references no scene types (K:7-10); `ScrollCommandPort` is SPSC (`ScrollCommandPort.cs:8-14, 56-79`);
`ScrollBody.Advance` is a pure static per-body step over `ScrollClock` (`ScrollBody.cs:209-371`, `ScrollClock.cs:10`);
`TryLease` bumps `LeaseSeq` and hands a by-value body (K:184-198); `Return` ignores a stale seq (K:202-212). The
per-body command handlers are instance methods on the kernel (K:456-504, 526-543, 1122-1156, 1170-1211, 1213-1235)
that only touch `ref ScrollBody` + `_feel` + `MarkActive/MarkTouched` — extractable into statics (§3.1).

### 1.4 Reference engines (what was and was not verifiable locally)

| Property | Chromium (`C:\WAVEE\chromium-cc-input`, partial) | Firefox (`C:\WAVEE\gecko-dev`, sparse) + WebRender | Others |
|---|---|---|---|
| Compositor thread owns scroll input | `cc/input/input_handler.cc:339-383` `ScrollUpdate` against the impl `ScrollTree`; `property_tree.cc:2371-2395` `ScrollBy` | `APZCTreeManager.cpp:1536-1538` controller-thread assert; `AsyncPanZoomController.cpp:2574-2622, 2734` wheel → `WheelScrollAnimation` | WinUI: wheel redirected to `InteractionTracker` (`ScrollPresenter.cpp:2791-2804`) |
| Sample time = next present | `layer_tree_host_impl.cc:843-871` (`frame_time`), `scheduler.cc:242` deadline = frame_time+interval | `WebRenderBridgeParent.cpp:1133-1152` last compose + vsync; `APZSampler.cpp:77-84` "the next vsync" | Flutter `animator.cc:114-118` vsync target time |
| One draw per vblank | `display_scheduler.cc:772-808`; `external_begin_frame_source_win.cc:48-67` one BeginFrame per vsync | `CompositorVsyncScheduler.cpp:113-125` coalesce, `:231-268` skip if pending | Terminal `AtlasEngine.r.cpp:436-449, 498`; makepad `win32_app.rs:539-557` |
| Snap at draw | `property_tree.cc:913-944` `UpdateSnapping` (round to_screen translation, keep `snap_amount`) | WebRender `spatial_node.rs:311-321` `snap_offset` | — |
| Pre-raster window / checkerboard | `cc/tiles` **absent from the clone** (skewport not citable) | displayport `AsyncPanZoomController.cpp:4638-4666`, skate/stationary multipliers `:4535-4539`; checkerboard events `:5490-5524` | Flutter `viewport.dart:547-561` cacheExtent; WinUI `ViewportManager.cpp:137-149` |
| Offset fed back to layout, one authority | `property_tree.cc:2051-2100` `CollectScrollDeltas` → `layer_tree_host.cc:1205-1233` `ApplyCompositorChanges` | `APZCCallbackHelper.cpp:382-399` `ScrollOrigin::Apz`, `nsLayoutUtils.cpp:8629-8640` cannot clobber APZ | WinUI `ScrollPresenter.cpp:1172-1215` `ValuesChanged` |
| Scroll-only frame without a new display list | — | `render_backend.rs:470-482` `SetScrollOffsets`; `tile_cache.rs:25-36` slices per scroll root | — |

`DCompositionWaitForCompositorClock` is not used by Firefox (its D3D vsync source is `DwmGetCompositionTimingInfo` +
`WaitForVBlank`, `gfxWindowsPlatform.cpp:1747-1830`); this engine already has the compositor clock (`Win32CompositorClock.cs:226-236`),
which is the recommended source on Windows 11 and is what this plan keeps.

---

## 2. Architecture

### 2.1 Ownership after this plan

```
UI thread (RunFrame/Paint)                       │ render thread (one turn per compositor tick while motion is live)
──────────────────────────────────────────────── │ ───────────────────────────────────────────────────────────────────
ScrollInputRouter → ScrollCommandPort ──────────►│ (port drained by the UI kernel as today)
ScrollKernel (authority for Drag + structure)    │ RenderScrollLease (Ballistic/Driven bodies BY VALUE, LeaseSeq-tagged)
  Tick: leased bodies are NOT advanced           │   Tick(presentQpc): ScrollBodyOps.Advance → snapped offset
  forwards WheelNotch/ScrollTo/Cancel/SetFrame ──►│   ← ScrollLeaseRing (SPSC, 256) ── applies ScrollBodyOps.* same code
  grant/continue/revoke ride PublishScene ──────►│   Adopt(ScrollLeaseSnapshot) on a fresh publication
                                                 │   writes CompositorPaint(content).LocalTransform (overlay)
                                                 │   + RenderScrollOffsets[node] (scrollbar/edge-fade/retained band)
SceneScrollSink.Apply(Writer=Lease)  ◄───────────│ RecordingFeedback.ScrollPoses (offset, vel, activity, seq, settled)
  ScrollState result columns (ApplyMotion token) │ SceneRecorder: retained hit → PushRetainedLayer(translate)+Pop, no walk
  SceneStore.Paint(content).LocalTransform,      │                 miss → band re-raster (LocalTransform = −bandStart)
  UNMARKED while leased (hit-test consistency)   │                 fallback → today's translated-copy path
  ScrollBindEval / VirtualRangeDirty / chrome    │ D3D12: LayerKind.Retained → pooled band RT, pin by (LayerId, epoch),
  realize-ahead (async; never on the scroll path)│        CompositeTranslated through the viewport scissor
```

**One authoritative offset per frame.** The render thread produces one `ScrollLeasePose` per leased body per turn and
publishes it in the same reverse mailbox as compositor poses (A:1227-1260). The UI imports it at the top of `RunFrame`
(A:1318) and applies it through the one chokepoint (`ScrollState.ApplyMotion`, `Columns.cs:330-349`) with
`Writer = ScrollWriteSource.Lease` **before** input dispatch, so hit-testing (`InputDispatcher.StepIntoNode`
reads `_scene.Paint(node).LocalTransform`, `InputDispatcher.cs:4034-4063`), `ScrollBindEval.ApplyContinuous`
(`ScrollBindEval.cs:57`), `VirtualWindowing.NeedsRealize` (`VirtualLayout.cs:126`) and `ScrollBarChrome.NotifyMoved`
(`SceneScrollSink.cs:71`) all see the same value in that frame. The visual may lead the UI's hit box by ≤ 1 UI frame
during a lease — the same accepted mismatch as the compositor-animation mirror (`InputDispatcher.cs:4030-4033`) and
Firefox's deliberate one-frame delay (`AsyncPanZoomController.cpp:4916-4952`).

**Render-thread-exact chrome.** The scrollbar thumb (`EmitScrollbar`, SR:3424) and the auto edge fade
(`TryResolveEdgeFade`, SR:3542-3564) are recorded on the render thread; both read `ScrollState.OffsetX/Y` from the
snapshot (SR:3424 signature takes `in ScrollState sc`). They switch to `scene.RenderScrollOffset(node, in sc)` (§4.4),
which returns the lease's offset while a lease is live and the authored one otherwise, so the thumb tracks the
composited content to the tick.

### 2.2 The retained content layer

A scroller is **retained-eligible** (`RetainedScrollPolicy.Eligible`, §4.5) when: it has a live content child; zoom == 1;
no `ScrollBind` target inside the content subtree (`HasContinuousScrollBindInside`, SR:2980-2994, widened to any bind
kind); the previous record's `ScrollLeaseCapture.HasAcrylicOrVideo` is false (SR:2522); no descendant scroller is
leased; the band fits the pool (`LayerTargetBucket.Dim`, `LayerTargetPool.cs:48-55`, ≤ 4096 px per axis and ≤ the
tier budget); the record is not a popup subtree. Ineligible scrollers take the fallback (§4.3).

While retained, the render lease writes the content's overlay `LocalTransform = Translation(−bandStart)` — **constant
for the life of the band** — so the content subtree's world transform is stable, its rows reuse as exact span copies
when the band is re-rastered, and on a **hit** the recorder walks nothing: it emits
`PushRetainedLayer(bandDeviceRect, viewportClip, layerId, epoch, skipBytes, translate)` + `PopRetainedLayer` and adds
`RepaintBand(viewport)` to the repaint region. The per-tick translation lives only in the command; the backend
composites the pinned band RT at `translate` (snapped to device pixels) through the viewport scissor.

```
content extent  ┌───────────────────────────────────────────────────────────────┐
                │        band (retained RT)                                     │
                │   ┌────────────────────────────────────────────┐              │
                │   │ behind │      viewport (clip)      │ ahead │              │
                │   │ 0.25vp │◄──────── translate ──────►│ v·T   │              │
                │   └────────────────────────────────────────────┘              │
                └───────────────────────────────────────────────────────────────┘
   hit:  [off, off+vp] ⊆ band  ∧  content record-dirty bits == 0  ∧  pin (layerId, epoch) resident
   miss: plan a new band (RetainedBandPolicy.Plan) → epoch++ → LocalTransform = −newBandStart → walk the content
         subtree against the band clip → backend rasters into a pooled band RT and pins it
```

**Checkerboard policy (precise).** There is no checkerboard: the engine never draws a blank band where content exists.
When the offset leaves the retained band (`[off, off+vp] ⊄ band`) the recorder **re-records the content subtree for
that frame** into a new band (a miss — one full record of the subtree, exactly today's cost). The new band is skewed
by velocity (`ahead = clamp(|v| · AheadSec, 0.5·vp, MaxAheadVp·vp)`, `behind = 0.25·vp`; `AheadSec = 0.35 s` on a
discrete adapter, `0.20 s` and `MaxAheadVp = 1` on a weak/UMA adapter — `LayerTargetTrim`'s tier, `LayerTargetPool.cs:111-117`).
If a body misses **three times within eight ticks** (the band is being consumed faster than it can be re-rastered),
`RetainedBandPolicy` demotes the body to the translate-only fallback for the rest of the gesture (`Outrun`) — no
raster thrash, no blank frames. Rows that are not realized are simply absent from the tree (the UI realizes ahead
by `VirtualWindowing.DirectionalOverscan`, `VirtualLayout.cs:110-124`, driven by the adopted offset); a band can
therefore be *shorter than requested* at the content's realized edge, and the UI's next publication (new rows ⇒
content record-dirty bits ⇒ epoch bump) re-rasters it. This is Firefox's displayport contract (repaint on content
change, `AsyncPanZoomController.cpp:4778-4807`), not Chromium's tile grid — tiles are the follow-up in §10.

**Damage / partial present.** A hit tick's repaint region is the viewport band (plus whatever else changed); the frame
carries `LayerKind.Retained` so `StreamLayerKind` reports `LayerKindGroups` and the layered route runs
(`RepaintPolicy.LayerKindGroups`, `RepaintPolicy.cs:149`; `WalkLayered`, D:4028). `RepaintStreamSafety.Scan`
(`RepaintPolicy.cs:388, 412-414`) admits `Retained` beside `Opacity`: the composite is a clipped blit inside the clamp
(`CurrentScissorRect`), so the `Partial` route with its ≤2 layered replay rects (`MaxLayeredReplayRects`, `:142`)
stays available — a scroll frame becomes *one* band-sized blit plus the chrome, instead of a full-window re-raster.

### 2.3 Pacing

- **Delete interval-0 for scroll.** `RenderFrame.InteractivePresent` (`RenderFrame.cs:36-39`), the `interactivePresent`
  publish argument (`SceneFramePublisher.cs:91-92, 124, 163-166, 210`, A:4427, A:4438, A:4466), the second arm of
  `ApplyPresentPacing` (A:1024) and the inline copy (A:4492), and `ISwapchain.SupportsCompositedIntervalZero`
  (`Rhi.cs:333-336`, D:6034) are deleted. `SuppressVsyncOnce`/`SuppressLatencyWaitOnce` survive **only** for the modal
  keep-alive path (`rf.SuppressVsync`, A:1023).
- **One turn, one present.** The loop's turn is the compositor tick while any render motion (compositor animation
  rows **or** a scroll lease) is live. A fresh publication that lands mid-interval does not present on its own: the
  pure `PresentCadence.Decide` (§3.4) presents at most once per `TickSeq` per window; the frame waits for the tick
  (DropOldest hands the newest one, `SceneFramePublisher.cs:135-158`). With a credit held (`PresentCreditHeld`, ISO
  §4.2) and `SyncInterval 1` (D:4859 unchanged), the credit and the tick rule together are the "present once per
  vblank" invariant — the credit is the throttle, the tick rule is the phase.
- **Sample at the present.** The render clock is `RefreshLattice.Build(tickAvailable, tickQpc, refresh, now, last,
  seq, maxFrameLatency).PresentQpc` (`RefreshLattice.cs:32-55`: `FrameQpc + (1 + maxFrameLatency)·refresh`, i.e. tick +
  2·refresh at latency 1) — the same pure function the UI uses (A:3235-3236), now evaluated on the render thread from
  the tick it was woken by. `ScrollClock.FrameSec = PresentSec`, `DtSec = PresentSec − lastPresentSec` (first tick after
  wake uses `RefreshSec`, `ScrollBody.cs:212-216`).
- **Measure repeats and drops with DXGI.** `PresentStats` already carries `PresentCount`/`PresentRefreshCount`
  (`PresentStats.cs:33-37`, sampled every primary present at D:4903 → D:5030-5106). The pure `PresentStatisticsLedger`
  (§3.5) turns them into `PresentsDisplayed`, `PresentsDropped` (submitted − displayed) and `VblanksRepeated`
  (ΔPresentRefreshCount − ΔPresentCount). These replace `FrameStats.MissedVsyncs` (A:276, A:4670, A:5531-5612) and the
  stamp-based `missedVsyncs` in `EmitLatencyRow` (A:5432-5440) is deleted in favour of the attested value that method
  already computes (A:5447-5459).
- **Per window.** Pacing state (last presented tick seq, credit, ledger) lives on the isolation plan's
  `TargetFrameState` (ISO §3.1) and is driven per `IRenderSource` in its Phase-2 loop (ISO §4.3).

### 2.4 Input path

Wheel packets become **urgent again** (`PacedInputWaitClassifier.IsDeferrable` drops `WmPointerWheel/HWheel`,
W32:263-265): the paced wait ends, the UI dispatches (`RunFrame` pump → dispatch, A:3250-3251), the router posts
`WheelNotch` to the port (`ScrollInputRouter.cs:519`). Production is still gated to one frame per tick
(`ProductionGateBlocks`, A:2070-2076), so this does not add UI frames — it only moves the command earlier. The UI kernel
drains the port at its next tick as today (K:223-224); a command for a **leased** body is not applied to the UI mirror
but forwarded to `ScrollLeaseRing` (§3.2), which the render thread drains at the top of its next tick (≤ 1 refresh
away). Structural commands (`SetFrame`, `AnchorShift`, `Unbind`, `Park`) are applied to the mirror **and** forwarded;
a takeover (`ContactBegin`, `FrameDelta`, `ThumbSet`, `SetZoom`, `Cancel`, `Unbind`, `Park`) forwards a `Revoke`.
The render thread applies its final tick, reports `Settled/Revoked` in the feedback, and the UI rebases the new
gesture on the adopted offset — **the lease always ends render-side, never with a backward step** (scroll-v3 §6.3).

Input-to-photon during a glide becomes: ≤ 1 refresh (command → next render tick) + 2 refreshes (latency-1 DComp
present) — the UI frame and the tick-hold are off the path. The first notch of a cold gesture still costs one UI
frame (grant rides the publish); the optional render-initiated grant is §10 Q2.

### 2.5 What the UI thread does during a lease

`ScrollKernel.Tick` skips `LeasedOut` bodies (they stay in `_activeList` so `CompactActiveList` keeps them resident,
but `WakeActiveCount` excludes them — K:1402-1420 — so a leased body alone never wakes the UI). The render thread
wakes the UI (`_window.Wake()`, the same nudge as A:1258) on every lease tick that moved; the UI's frame then imports
the pose, applies it, and if `VirtualWindowing.NeedsRealize` says so (`SceneScrollSink.cs:118-122`) marks
`VirtualRangeDirty` and realizes — that publication's new rows dirty the content subtree, the epoch bumps, and the
band re-rasters on the render thread's next tick. The UI's frame is a follower; nothing it does gates a scroll tick.

---

## 3. Pure decision classes (Engine, TerraFX-free, gated headlessly)

### 3.1 `ScrollBodyOps` — the per-body handlers as statics (`src/FluentGpu.Engine/Scroll/ScrollBodyOps.cs`, new)

Extracted **verbatim** from `ScrollKernel` (no behaviour change; the kernel keeps `MarkActive/MarkTouched` wrappers).
Each method below is the body of the named kernel method with `_feel` → `in ScrollFeel feel` and the `MarkActive`/
`MarkTouched` calls returned as flags. Signatures (pinned — both threads compile against them):

```csharp
namespace FluentGpu.Scroll;

/// <summary>The kernel's per-body command handlers as pure statics over <c>ref ScrollBody</c>, so the UI kernel and
/// the render-thread lease (<c>RenderScrollLease</c>) run the SAME code on a leased body. Extracted 1:1 from
/// ScrollKernel (2026-09-22: ApplySetFrame K:456-504, ApplyCancel K:526-543, ApplyWheelNotch K:1122-1156,
/// SetDrivenTarget K:1170-1211, ApplySetVelocity K:1213-1235, ApplyAnchorShift K:645-662, ResolveEdge K:1225-1300 minus
/// the chain hand-off, which stays kernel-only). Returns which lists the caller must update.</summary>
public static class ScrollBodyOps
{
    [Flags] public enum Effect : byte { None = 0, Active = 1, Touched = 2, ActiveAndTouched = 3 }

    public static Effect SetFrame(ref ScrollBody b, in ScrollInput cmd, in ScrollFeel feel, ref int restorePendingDelta);
    public static Effect Cancel(ref ScrollBody b, ref int restorePendingDelta);
    public static Effect WheelNotch(ref ScrollBody b, in ScrollInput cmd, in ScrollFeel feel, ref int restorePendingDelta, ref int wheelNotches);
    public static Effect SetDrivenTarget(ref ScrollBody b, float target, in ScrollInput cmd, in ScrollFeel feel, ref int restorePendingDelta);
    public static Effect SetVelocity(ref ScrollBody b, in ScrollInput cmd);
    public static Effect AnchorShift(ref ScrollBody b, float delta, ref float maxAbsStructuralDip);
    /// <summary>The render-side edge resolution: no chain parent is reachable from a leased copy, so a pinned Ballistic
    /// step resolves immediately to Bounce (contact-descended) or Idle (NoOverscroll) — ScrollBody.Advance's own comment
    /// ("A standalone Advance caller … resolves immediately instead", ScrollBody.cs:262-264).</summary>
    public static void ResolveEdgeStandalone(ref ScrollBody b, in ScrollFeel feel);
}
```

`ScrollKernel.ProcessCommand` (K:318-356) becomes a switch that calls these and applies the returned `Effect`; the
kernel's own methods are deleted (no duplicate). `ScrollBody` gains one field:

```csharp
    /// <summary>True while a render-thread lease owns this body (RenderScrollLease). The UI kernel keeps the slot
    /// resident but neither advances it nor applies time-based commands to it — those are forwarded on the
    /// ScrollLeaseRing. Cleared when the feedback reports Settled/Revoked for the current LeaseSeq.</summary>
    public bool LeasedOut;
```

`ScrollWriteSource` gains `Lease = 3` if not already present (scroll-v3 §2.3 reserved it: `Tick=1, Reclamp=2, Lease=3`).

### 3.2 Lease seam types (`src/FluentGpu.Engine/Scroll/ScrollLease.cs`, new)

```csharp
namespace FluentGpu.Scroll;

public enum ScrollLeaseCommand : byte { None = 0, Grant = 1, Continue = 2, Revoke = 3 }

/// <summary>UI → render, rides PublishScene inside SceneRenderFrame (like CompositorAnimationSnapshot). One entry per
/// leased viewport; the whole desired set replaces membership on every publication (Adopt), exactly the compositor-
/// animation contract. POD; Body.Frame.SnapPoints is the shared immutable array the kernel already treats as such.</summary>
public struct ScrollLeaseEntry
{
    public int NodeIndex; public uint NodeGen;
    public uint Seq;                 // ScrollBody.LeaseSeq at grant — the identity of THIS lease
    public ScrollLeaseCommand Command;
    public ScrollBody Body;          // Grant only: the by-value body from ScrollKernel.TryLease
    public byte Orientation;         // ScrollState.Orientation (0 vertical, 1 horizontal)
    public float ViewportMain, ExtentMain;   // for the render-side clamp (same source as Body.Frame)
    public bool RetainedEligible;    // RetainedScrollPolicy.Eligible evaluated on the UI (binds/zoom/nesting), re-checked render-side for acrylic/video
}

/// <summary>Publisher-slot-owned copy of the desired lease set (SceneRenderFrame.Leases). Grown only at capture.</summary>
public sealed class ScrollLeaseSnapshot
{
    public const int Cap = 32;                       // == SceneRecorder.LeaseCaptureCap
    private readonly ScrollLeaseEntry[] _entries = new ScrollLeaseEntry[Cap];
    public int Count { get; private set; }
    public double CapturedAtMs { get; private set; }
    public ref readonly ScrollLeaseEntry At(int i) => ref _entries[i];
    internal void Begin(double nowMs) { Count = 0; CapturedAtMs = nowMs; }
    internal bool TryAdd(in ScrollLeaseEntry e) { if (Count == Cap) return false; _entries[Count++] = e; return true; }
}

/// <summary>Render → UI, appended to RecordingFeedback after the compositor poses. One per leased body per turn.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct ScrollLeasePose
{
    public int NodeIndex; public uint NodeGen; public uint Seq;
    public float OffsetX, OffsetY, BandX, BandY, VelocityMain, LastReleaseVelocity;
    public ScrollActivity Activity; public ScrollActivityFlags Flags;
    public byte Ended;               // 0 live; 1 Settled (IsSettled); 2 Revoked (final tick applied after a Revoke)
    public byte Retained;            // 1 while the retained band is in use (diagnostics: FrameStats.ScrollRetainedBodies)
    public long PresentQpc;          // the present this pose was sampled for
}

/// <summary>UI → render command ring for bodies already leased: 256 ScrollInput slots, SPSC (Volatile head/tail,
/// the ScrollCommandPort discipline, ScrollCommandPort.cs:22-26). Overflow drops the OLDEST WheelNotch for the same
/// node (a superseded notch is worthless once a newer one exists); Revoke/SetFrame are never dropped (Debug assert).</summary>
public sealed class ScrollLeaseRing
{
    public const int Capacity = 256;
    private readonly ScrollInput[] _ring = new ScrollInput[Capacity];
    private int _head, _tail;
    public int Pending => System.Threading.Volatile.Read(ref _head) - System.Threading.Volatile.Read(ref _tail);
    public void Post(in ScrollInput cmd) { /* mirrors ScrollCommandPort.Post minus the SetFrame dedup */ }
    internal int DrainAll(Span<ScrollInput> buffer) { /* mirrors ScrollCommandPort.DrainAll */ return 0; }
}
```

`ScrollInputKind` gains `Revoke` (a structural kind; `ScrollCommandPort.IsStructural`, `ScrollCommandPort.cs:98-105`,
lists it). The UI kernel never processes `Revoke` itself — it is emitted by the kernel *onto the ring* when a takeover
command arrives for a leased body (§4.2).

### 3.3 `RenderScrollClock` — present-time sampling (`src/FluentGpu.Engine/Hosting/RenderScrollClock.cs`, new)

```csharp
namespace FluentGpu.Hosting;

/// <summary>The render thread's ScrollClock for one lease tick: the present this turn's pixels land on, as predicted by
/// the SAME RefreshLattice.Build the UI uses (RefreshLattice.cs:32-55) from the compositor tick the turn was woken by.
/// dt is the delta of consecutive PRESENT instants (not of wake instants), so a turn that ran late inside its interval
/// still steps exactly one refresh — Firefox's "last compose + vsync" (WebRenderBridgeParent.cpp:1133-1152).</summary>
public static class RenderScrollClock
{
    public static ScrollClock Build(bool tickAvailable, long tickQpc, long refreshQpc, long nowQpc, long lastPresentQpc,
                                    ulong seq, int maxFrameLatency, long qpcFrequency, out long presentQpc)
    {
        var fc = RefreshLattice.Build(tickAvailable, tickQpc, refreshQpc, nowQpc, lastFrameQpc: 0, seq, maxFrameLatency);
        presentQpc = fc.PresentQpc;
        long refresh = refreshQpc > 0 ? refreshQpc : qpcFrequency / 60;
        double presentSec = presentQpc / (double)qpcFrequency;
        float dtSec = lastPresentQpc <= 0 ? 0f : (float)Math.Clamp((presentQpc - lastPresentQpc) / (double)qpcFrequency, 0.0, 0.034);
        return new ScrollClock(presentSec, dtSec, presentSec, refresh / (float)qpcFrequency);
    }
}
```

(`lastFrameQpc: 0` — the render thread never rewinds because it always samples the latest tick; the clamp mirrors
`ScrollBody.Advance`'s own 34 ms clamp, `ScrollBody.cs:211`.)

### 3.4 `PresentCadence` — present once per vblank (`src/FluentGpu.Engine/Hosting/Threading/PresentCadence.cs`, new)

```csharp
namespace FluentGpu.Hosting.Threading;

public struct PresentCadenceInput
{
    public long TickSeq;            // the render display clock's seq at the top of this turn (0 = no clock)
    public long LastPresentedTickSeq;
    public bool HasFreshPublication; // seam has an unconsumed publication
    public bool MotionDue;           // compositor rows or a scroll lease want a tick
    public bool CreditHeld;          // present-slot credit in hand (ISO §4.2)
    public bool Unpaced;             // no waitable / no display clock (headless, RDP): never gate on the tick
}

public enum PresentVerdict : byte { Skip = 0, PresentFresh = 1, PresentMotion = 2 }

/// <summary>ONE present per compositor tick per window. A fresh publication that lands after this tick's present waits
/// for the next tick (DropOldest hands the newest one then — SceneFramePublisher.TryAcquire); a motion tick with no
/// fresh publication re-presents the retained scene with new poses/offsets. Unpaced targets present whenever they
/// have something (the credit alone throttles them).</summary>
public static class PresentCadence
{
    public static PresentVerdict Decide(in PresentCadenceInput x)
    {
        if (!x.HasFreshPublication && !x.MotionDue) return PresentVerdict.Skip;
        if (!x.Unpaced)
        {
            if (!x.CreditHeld) return PresentVerdict.Skip;
            if (x.TickSeq != 0 && x.TickSeq == x.LastPresentedTickSeq) return PresentVerdict.Skip;   // already presented this vblank
        }
        return x.HasFreshPublication ? PresentVerdict.PresentFresh : PresentVerdict.PresentMotion;
    }
}
```

Decision table (each row is a gate, §7.1):

| fresh | motion | credit | tick == last | unpaced | verdict |
|---|---|---|---|---|---|
| f | f | any | any | any | Skip |
| t | any | **f** | any | f | Skip (wait for the credit) |
| t | any | t | **t** | f | Skip (present next tick; newest wins) |
| t | any | t | f | f | PresentFresh |
| f | t | t | f | f | PresentMotion |
| t | any | any | any | **t** | PresentFresh |

### 3.5 `PresentStatisticsLedger` — attested drops/repeats (`src/FluentGpu.Engine/Hosting/Threading/PresentStatisticsLedger.cs`, new)

> **Superseded 2026-09-24 (the code below is the original, wrong ledger).** A frame-statistics sample describes the last
> DISPLAYED present and lags the submit by about one flip, so differencing it against the submitted count turned every
> stale sample into a permanent drop (90–97 % of the Scroll Lab's "Repeats / drops" was this artefact). The ledger now
> compares only DXGI's PAIRED counters: dropped = max(0, ΔPresentCount − ΔPresentRefreshCount), repeated =
> max(0, ΔPresentRefreshCount − ΔPresentCount − idle), where the idle vblanks between two submits are banked under the
> present's id (`GetLastPresentCount`) and credited when a sample shows it displayed; a pre-display sample
> (PresentRefreshCount 0) is no baseline; `PresentsSubmitted` is gone. DWM deltas carry `PresentStats.DwmSampleSeq`
> (FrameStats carries them on the first frame of each sample only), the render thread records a per-present
> `ScrollProbe.Turn` row (tick, missed ticks, wake lag, slot wait, work), and the lab's metric counts one event per missed
> vblank (`ScrollMetrics.RepeatsAndDrops` / `MissedTickAttribution`). The source is the reference; tests:
> `PresentStatisticsLedgerTests`, `ScrollMetricsTests`.

```csharp
namespace FluentGpu.Hosting.Threading;

/// <summary>Differences DXGI frame statistics between two samples (PresentStats.PresentCount / PresentRefreshCount,
/// PresentStats.cs:33-37) against the engine's own submitted-present count. Displayed = ΔPresentCount (presents that
/// reached a vblank); Dropped = submitted − Displayed (replaced before display — the "silent drop" the stamp-based
/// missedVsyncs could not see); Repeated = ΔPresentRefreshCount − ΔPresentCount (vblanks that showed a stale frame).
/// Counters are uint and wrap; a DISJOINT sample (Valid == false) resets the baseline and counts nothing.</summary>
public struct PresentStatisticsLedger
{
    private uint _lastPresentCount, _lastRefreshCount; private long _lastSubmitted; private bool _baselined;
    public long PresentsDisplayed, PresentsDropped, VblanksRepeated;

    public void Observe(bool valid, uint presentCount, uint presentRefreshCount, long submittedPresents)
    {
        if (!valid) { _baselined = false; return; }
        if (_baselined)
        {
            uint dDisplayed = unchecked(presentCount - _lastPresentCount);
            uint dRefresh = unchecked(presentRefreshCount - _lastRefreshCount);
            long dSubmitted = submittedPresents - _lastSubmitted;
            PresentsDisplayed += dDisplayed;
            PresentsDropped += Math.Max(0, dSubmitted - dDisplayed);
            VblanksRepeated += dRefresh > dDisplayed ? dRefresh - dDisplayed : 0;
        }
        _lastPresentCount = presentCount; _lastRefreshCount = presentRefreshCount; _lastSubmitted = submittedPresents; _baselined = true;
    }
}
```

`PresentStats.cs:19` already records the honest limit ("`PresentCount` is documented as NOT the number of `Present()`
calls — a correlation must tolerate holes"); the ledger clamps at 0 and reports deltas, never rates.

### 3.6 `RetainedBandPolicy` and `RetainedScrollPolicy` (`src/FluentGpu.Engine/Render/RetainedScroll.cs`, new)

```csharp
namespace FluentGpu.Render;

public readonly record struct RetainedBand(float Start, float End, ulong Epoch);

/// <summary>Where the retained band sits and when it is replanned. Pure; DIP along the scroll axis.</summary>
public static class RetainedBandPolicy
{
    public const float BehindVp = 0.25f;
    public const float AheadSecStrong = 0.35f, AheadSecWeak = 0.20f;
    public const float MaxAheadVpStrong = 2f, MaxAheadVpWeak = 1f, MinAheadVp = 0.5f;
    public const int OutrunMisses = 3, OutrunWindowTicks = 8;

    /// <summary>[off, off+vp] ⊆ band — the hit test. Half-DIP tolerance for the snapped composite.</summary>
    public static bool Covers(in RetainedBand band, float offset, float viewport)
        => offset >= band.Start - 0.5f && offset + viewport <= band.End + 0.5f;

    public static RetainedBand Plan(float offset, float velocity, float viewport, float contentExtent, bool weakAdapter, ulong nextEpoch)
    {
        float aheadSec = weakAdapter ? AheadSecWeak : AheadSecStrong;
        float maxAhead = (weakAdapter ? MaxAheadVpWeak : MaxAheadVpStrong) * viewport;
        float ahead = Math.Clamp(MathF.Abs(velocity) * aheadSec, MinAheadVp * viewport, maxAhead);
        float behind = BehindVp * viewport;
        // Skew toward the direction of travel; at rest the band is symmetric-ish (min ahead + behind).
        float start = velocity < 0f ? offset - ahead : offset - behind;
        float end   = velocity < 0f ? offset + viewport + behind : offset + viewport + ahead;
        start = MathF.Max(0f, start); end = MathF.Min(contentExtent, end);
        if (end < offset + viewport) end = MathF.Min(contentExtent, offset + viewport);
        return new RetainedBand(start, end, nextEpoch);
    }

    /// <summary>Miss bookkeeping: true when the body has outrun the raster and must fall back to translate-only.</summary>
    public struct OutrunTracker
    {
        private int _misses; private long _windowStartTick;
        public bool NoteMiss(long tick)
        {
            if (tick - _windowStartTick > OutrunWindowTicks) { _windowStartTick = tick; _misses = 0; }
            return ++_misses >= OutrunMisses;
        }
        public void Reset() { _misses = 0; _windowStartTick = 0; }
    }
}

public readonly record struct RetainedEligibilityInput(bool HasContent, bool ZoomIsOne, bool BindTargetInsideContent,
    bool AcrylicOrVideoInsideContent, bool DescendantScrollerLeased, bool InPopupSubtree, int BandWpx, int BandHpx, long BudgetBytes);

public static class RetainedScrollPolicy
{
    public const int MaxDimPx = 4096;
    public static bool Eligible(in RetainedEligibilityInput x)
        => x.HasContent && x.ZoomIsOne && !x.BindTargetInsideContent && !x.AcrylicOrVideoInsideContent
           && !x.DescendantScrollerLeased && !x.InPopupSubtree
           && x.BandWpx > 0 && x.BandHpx > 0 && x.BandWpx <= MaxDimPx && x.BandHpx <= MaxDimPx
           && LayerTargetBucket.Bytes(LayerTargetBucket.Dim(x.BandWpx), LayerTargetBucket.Dim(x.BandHpx)) <= x.BudgetBytes;
}
```

Budget: `BudgetBytes` = 24 MiB on a strong adapter, 8 MiB on a weak one (the same tier `LayerTargetTrim` uses,
`LayerTargetPool.cs:98-101`); at 1195×767 device px a 2-viewport band is ~7 MiB, so the weak cap admits ≤ 1 ahead.

---

## 4. Render-thread lease, feedback, and the recorder

### 4.1 `RenderScrollLease` (`src/FluentGpu.Engine/Scroll/RenderScrollLease.cs`, new) — the render-side twin

Pattern: `RenderCompositorAnimations` (`Animation/RenderCompositorAnimations.cs:11-239`): `Adopt` on a fresh
publication, `Tick` every turn, a fixed feedback array. Zero-alloc after construction: `Cap = ScrollLeaseSnapshot.Cap`.

```csharp
namespace FluentGpu.Scroll;

public sealed class RenderScrollLease
{
    public const int Cap = ScrollLeaseSnapshot.Cap;
    private struct Slot
    {
        public bool Live; public int NodeIndex; public uint NodeGen; public uint Seq;
        public ScrollBody Body; public byte Orientation;
        public bool RetainedEligible, Outrun; public RetainedBand Band; public RetainedBandPolicy.OutrunTracker Outruns;
        public bool RevokePending; public byte Ended;
        public long LastPresentQpc; public float LastOffX, LastOffY;
    }
    private readonly Slot[] _slots = new Slot[Cap];
    private int _count;
    private readonly ScrollLeasePose[] _poses = new ScrollLeasePose[Cap];
    private readonly ScrollInput[] _ringScratch = new ScrollInput[ScrollLeaseRing.Capacity];
    private readonly ScrollFeel _feel;
    private ulong _epochSeq;
    public bool HasActive { get; private set; }
    public bool ChangedThisTick { get; private set; }
    public ReadOnlySpan<ScrollLeasePose> Feedback => _poses.AsSpan(0, _count);
    public RenderScrollLease(in ScrollFeel feel) => _feel = feel;

    /// <summary>Fresh publication: the desired set REPLACES membership. Grant seeds a slot from the by-value body;
    /// Continue keeps the render-side body (the UI's copy is a mirror, never re-adopted); Revoke marks the slot to apply
    /// one final tick and end. A body whose node died in this snapshot ends as Revoked.</summary>
    public void Adopt(ScrollLeaseSnapshot desired, SceneRecordingSnapshot scene)
    {
        for (int i = 0; i < _count; i++) _slots[i].Live = false;
        int n = 0;
        for (int i = 0; i < desired.Count; i++)
        {
            ref readonly var e = ref desired.At(i);
            var handle = new NodeHandle(e.NodeIndex, e.NodeGen);   // gen-checked against the SNAPSHOT, never live SceneStore
            int old = Find(e.NodeIndex, e.Seq);
            if (!scene.IsLive(handle)) { if (old >= 0) _slots[old].Ended = 2; continue; }
            ref Slot s = ref (old >= 0 ? ref _slots[old] : ref _slots[Alloc(ref n)]);
            if (old < 0 || e.Command == ScrollLeaseCommand.Grant)
            {
                s = default; s.NodeIndex = e.NodeIndex; s.NodeGen = e.NodeGen; s.Seq = e.Seq; s.Body = e.Body;
                s.Orientation = e.Orientation; s.RetainedEligible = e.RetainedEligible; s.Band = default;
            }
            s.Live = true;
            if (e.Command == ScrollLeaseCommand.Revoke) s.RevokePending = true;
        }
        Compact();   // drop slots whose Ended was already reported (feedback consumed) — O(Cap)
    }

    /// <summary>Drain forwarded commands (ScrollBodyOps — the SAME handlers the UI kernel runs), advance every live
    /// body to the present instant, resolve edges standalone, and write the overlay transform + offset table.</summary>
    public void Tick(SceneRecordingSnapshot scene, ScrollLeaseRing ring, in ScrollClock clock, long presentQpc, long tickSeq,
                     bool weakAdapter, IRetainedLayerProbe? probe)
    {
        int cmds = ring.DrainAll(_ringScratch);
        for (int c = 0; c < cmds; c++) ApplyForwarded(in _ringScratch[c]);
        ChangedThisTick = false; HasActive = false;
        for (int i = 0; i < _count; i++)
        {
            ref Slot s = ref _slots[i];
            if (!s.Live || s.Ended != 0) continue;
            float before = s.Body.PositionMain;
            ScrollBody.Advance(ref s.Body, in clock, in _feel);
            if (s.Body.EdgeHitPending) ScrollBodyOps.ResolveEdgeStandalone(ref s.Body, in _feel);
            bool moved = s.Body.PositionMain != before || s.Body.BandMain != 0f;
            if (s.RevokePending) { s.Ended = 2; }
            else if (s.Body.IsSettled) { s.Ended = 1; }
            HasActive |= s.Ended == 0;
            ChangedThisTick |= moved || s.Ended != 0;
            WriteOverlay(scene, ref s, moved, tickSeq, weakAdapter, probe);
            s.LastPresentQpc = presentQpc;
            _poses[i] = Pose(in s, presentQpc);
        }
    }

    private void WriteOverlay(SceneRecordingSnapshot scene, ref Slot s, bool moved, long tickSeq, bool weakAdapter, IRetainedLayerProbe? probe)
    {
        var vp = new NodeHandle(s.NodeIndex, s.NodeGen);
        ref readonly var sc = ref scene.ScrollRef(vp);
        var content = sc.ContentNode;
        if (content.IsNull || !scene.IsLive(content)) return;
        bool horizontal = s.Orientation == 1;
        float offset = s.Body.PositionMain, band = s.Body.BandMain;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        float extent = horizontal ? sc.ContentW : sc.ContentH;
        // Retained band: constant −bandStart transform while the band covers the viewport; the per-tick translation
        // rides the PushRetainedLayer command the recorder emits (SceneRecorder, §4.5).
        bool retained = s.RetainedEligible && !s.Outrun && band == 0f;
        if (retained)
        {
            bool dirty = scene.RecordDirtyBits(content) != 0;   // UI content change ⇒ new epoch (re-raster)
            bool covers = RetainedBandPolicy.Covers(in s.Band, offset, viewport);
            if (!covers || dirty || s.Band.Epoch == 0)
            {
                if (!covers && s.Band.Epoch != 0 && s.Outruns.NoteMiss(tickSeq)) { s.Outrun = true; retained = false; }
                else s.Band = RetainedBandPolicy.Plan(offset, s.Body.VelocityMain, viewport, extent, weakAdapter, ++_epochSeq);
            }
        }
        scene.SetRenderScrollOffset(vp, s.Body.OffsetX, s.Body.OffsetY, s.Body.BandX, s.Body.BandY,
            retained ? s.Band : default, retained);
        ref NodePaint cp = ref scene.CompositorPaint(content, changed: moved || !retained || s.Band.Epoch == 0);
        float t = retained ? s.Band.Start : offset + band;
        ScrollContentTransform.WriteContentTransform(ref cp, in scene.Bounds(content), horizontal, retained ? t : offset,
            retained ? 0f : band, 1f, scene.DeviceScale);
    }

    private ScrollLeasePose Pose(in Slot s, long presentQpc) => new()
    {
        NodeIndex = s.NodeIndex, NodeGen = s.NodeGen, Seq = s.Seq,
        OffsetX = s.Body.OffsetX, OffsetY = s.Body.OffsetY, BandX = s.Body.BandX, BandY = s.Body.BandY,
        VelocityMain = s.Body.VelocityMain, LastReleaseVelocity = s.Body.LastReleaseVelocity,
        Activity = s.Body.Activity, Flags = s.Body.Flags, Ended = s.Ended,
        Retained = (byte)(s.RetainedEligible && !s.Outrun ? 1 : 0), PresentQpc = presentQpc,
    };

    private void ApplyForwarded(in ScrollInput cmd)
    {
        int i = FindNode(cmd.Node); if (i < 0) return;
        ref Slot s = ref _slots[i];
        int restore = 0, notches = 0; float sdip = 0f;
        switch (cmd.Kind)
        {
            case ScrollInputKind.WheelNotch: ScrollBodyOps.WheelNotch(ref s.Body, in cmd, in _feel, ref restore, ref notches); break;
            case ScrollInputKind.ScrollTo:   ScrollBodyOps.SetDrivenTarget(ref s.Body, cmd.A, in cmd, in _feel, ref restore); break;
            case ScrollInputKind.ScrollBy:   ScrollBodyOps.SetDrivenTarget(ref s.Body, s.Body.PositionMain + cmd.A, in cmd, in _feel, ref restore); break;
            case ScrollInputKind.SetVelocity: ScrollBodyOps.SetVelocity(ref s.Body, in cmd); break;
            case ScrollInputKind.SetFrame:   ScrollBodyOps.SetFrame(ref s.Body, in cmd, in _feel, ref restore); s.Band = default; break;
            case ScrollInputKind.AnchorShift: ScrollBodyOps.AnchorShift(ref s.Body, cmd.A, ref sdip); s.Band = default; break;
            case ScrollInputKind.Revoke:     s.RevokePending = true; break;
        }
    }
    // Find/FindNode/Alloc/Compact: linear over ≤ Cap slots, allocation-free.
}
```

`WriteContentTransform` is the **same** function the UI sink calls (`ScrollContentTransform.cs:19-43`); the concurrent
snap-at-write change lands there first, so the render overlay and the UI's authored transform snap identically. If
that change moves the snap into a separate helper, both call it.

### 4.2 UI-side kernel changes — `K:`

```csharp
    // ── Lease hand-off (ScrollLeaseSnapshot / ScrollLeaseRing) ──────────────────────────────────────────────────
    private readonly ScrollLeaseRing _leaseRing = new();
    public ScrollLeaseRing LeaseRing => _leaseRing;
    private readonly ScrollLeasePose[] _pendingPoses = new ScrollLeasePose[ScrollLeaseSnapshot.Cap];
    private int _pendingPoseCount;
    public bool LeasesEnabled { get; set; }   // AppHost: a render thread exists AND the display clock is available (never headless)
```

`ProcessCommand` (K:318-356) gains the forwarding prologue — **before** the switch:

```csharp
        if (TryGetSlot(cmd.Node, out int lslot) && _bodies[lslot].LeasedOut)
        {
            switch (cmd.Kind)
            {
                // time-based intents: the render body owns them — forward, do not touch the mirror
                case ScrollInputKind.WheelNotch: case ScrollInputKind.ScrollTo: case ScrollInputKind.ScrollBy:
                case ScrollInputKind.SetVelocity:
                    _leaseRing.Post(in cmd); return;
                // structural: apply to the mirror AND forward (idempotent on both sides)
                case ScrollInputKind.SetFrame: case ScrollInputKind.AnchorShift:
                    _leaseRing.Post(in cmd); break;
                // takeovers end the lease render-side; the mirror is rebased when the final pose arrives
                case ScrollInputKind.ContactBegin: case ScrollInputKind.ContactMove: case ScrollInputKind.FrameDelta:
                case ScrollInputKind.ThumbSet: case ScrollInputKind.SetZoom: case ScrollInputKind.Cancel:
                case ScrollInputKind.Unbind: case ScrollInputKind.Park: case ScrollInputKind.Restore:
                    _leaseRing.Post(ScrollInput.Revoke(cmd.Node, cmd.T));
                    _bodies[lslot].PendingTakeover = cmd;   // replayed by ApplyLeaseFeedback once Ended arrives (one slot, latest wins)
                    return;
            }
        }
```

`Tick` (K:226-280): `if (!b.Bound || b.Parked || b.LeasedOut) continue;` — leased bodies are never advanced on the UI.
`CompactActiveList` (K:1402-1420): `if (!b.Parked && !b.LeasedOut) wake++;`. After the advance loop and before
`EmitTouched`, the grant pass:

```csharp
        if (LeasesEnabled)
            for (int k = 0; k < _activeCount; k++)
            {
                ref ScrollBody b = ref _bodies[_activeList[k]];
                if (!b.Bound || b.Parked || b.LeasedOut || b.RestorePending || b.EdgeHitPending) continue;
                bool leasable = b.Activity == ScrollActivity.Ballistic
                    || (b.Activity == ScrollActivity.Driven && (b.Flags & ScrollActivityFlags.Autoscroll) == 0)
                    || (b.Activity == ScrollActivity.Idle && (b.Flags & ScrollActivityFlags.Bouncing) != 0);
                if (!leasable) continue;
                b.LeaseSeq++; b.LeasedOut = true; b.LeaseCommand = ScrollLeaseCommand.Grant;   // captured by CaptureLeases below
            }
```

Two new kernel entry points, called by `AppHost`:

```csharp
    /// <summary>UI publish: copy the desired lease set into the publisher slot (SceneRenderFrame.Capture). A Grant is
    /// emitted once (the flag flips to Continue after capture); a Revoke is emitted until the body ends.</summary>
    public void CaptureLeases(ScrollLeaseSnapshot target, SceneStore scene, double nowMs)
    {
        target.Begin(nowMs);
        for (int k = 0; k < _activeCount; k++)
        {
            ref ScrollBody b = ref _bodies[_activeList[k]];
            if (!b.Bound || !b.LeasedOut) continue;
            ref readonly var sc = ref scene.ScrollRefByIndex(b.Node);
            var e = new ScrollLeaseEntry { NodeIndex = b.Node, NodeGen = scene.HandleAt(b.Node).Raw.Gen, Seq = b.LeaseSeq,
                Command = b.LeaseCommand, Body = b, Orientation = sc.Orientation,
                ViewportMain = b.Horizontal ? sc.ViewportW : sc.ViewportH, ExtentMain = b.Horizontal ? sc.ContentW : sc.ContentH,
                RetainedEligible = b.RetainedEligible };
            if (!target.TryAdd(in e)) break;
            if (b.LeaseCommand == ScrollLeaseCommand.Grant) b.LeaseCommand = ScrollLeaseCommand.Continue;
        }
    }

    /// <summary>UI import (top of RunFrame, before dispatch): adopt the render thread's poses — one authoritative offset
    /// per body per frame — through the ONE sink chokepoint, Writer = Lease. Stale seqs are ignored (a Return after a
    /// re-grant); an Ended pose clears LeasedOut, replays a pending takeover, and lets the UI kernel own the body again.</summary>
    public void ApplyLeaseFeedback(ReadOnlySpan<ScrollLeasePose> poses)
    {
        _stamp++; _touchedCount = 0;
        foreach (ref readonly var p in poses)
        {
            if (!TryGetSlot(p.NodeIndex, out int slot)) continue;
            ref ScrollBody b = ref _bodies[slot];
            if (!b.LeasedOut || b.LeaseSeq != p.Seq) continue;
            b.OffsetX = p.OffsetX; b.OffsetY = p.OffsetY; b.BandX = p.BandX; b.BandY = p.BandY;
            b.Velocity = p.VelocityMain; b.LastReleaseVelocity = p.LastReleaseVelocity;
            b.Activity = p.Activity; b.Flags = p.Flags;
            if (p.Ended != 0)
            {
                b.LeasedOut = false; b.LeaseCommand = ScrollLeaseCommand.None;
                if (b.Activity != ScrollActivity.Idle && p.Ended == 2) { b.Activity = ScrollActivity.Idle; b.Velocity = 0f; }
                if (b.PendingTakeover.Kind != ScrollInputKind.None) { var t = b.PendingTakeover; b.PendingTakeover = default; ProcessCommand(in t); }
                b.Awake = false;
            }
            MarkTouched(slot);
        }
        EmitTouched(ScrollWriteSource.Lease);
        CompactActiveList();
        FlushPendingFree();
    }
```

`ScrollBody` gains `LeaseCommand` (`ScrollLeaseCommand`), `RetainedEligible` (bool, written by the router/host from
`RetainedScrollPolicy.Eligible` when the body enters motion — the UI knows binds/zoom/nesting; acrylic/video are
re-checked render-side from the recorder's capture) and `PendingTakeover` (`ScrollInput`, 40 B). All POD.

`SceneScrollSink.Apply` (`SceneScrollSink.cs:62-134`): when `w.Writer == ScrollWriteSource.Lease` it writes the
content `LocalTransform` **without** `_scene.Mark(content, TransformDirty | PaintDirty)` (`:91`) — `SceneStore.Paint(h)`
is a plain mutable ref (`SceneStore.cs:803`), so the write is visible to hit-testing and to `ScrollBindEval` on this
thread but produces no record-dirty bit: the render overlay owns the composited transform while the lease is live,
and a marked write here would dirty the content every UI frame and defeat the retained band. Everything else in
`Apply` (token write, chrome, binds, `NeedsRealize` → `VirtualRangeDirty` + wake, trace, controllers) runs unchanged.
On the **Ended** pose the sink is called once more by the kernel's normal `EmitTouched(Tick)` on the next UI tick
(the body is Idle and touched) — a marked write, captured normally, so the authored transform catches up.

### 4.3 `AppHost` — render side (`SubmitPresentOnRenderThread`, A:1094-1262) and UI side

Render side, after the compositor-animation adopt/tick (A:1123-1129):

```csharp
                var sceneFrame = _renderSeam.Scene(rf);
                double nowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                bool fresh = rf.PublishSeq != _lastRecordedScene;
                if (fresh) { _renderAnimations.Adopt(sceneFrame.Animations, sceneFrame.Scene, nowMs); _lastSettledPoseCount = 0;
                             _renderScroll.Adopt(sceneFrame.Leases, sceneFrame.Scene); }
                else _renderAnimations.Tick(sceneFrame.Scene, nowMs);
                // Scroll lease tick — EVERY turn (fresh or not): the lease samples at THIS turn's predicted present.
                var sclock = RenderScrollClock.Build(_renderTick.Available, _renderTick.TickQpc, RefreshPeriodQpcOrDefault(),
                    Stopwatch.GetTimestamp(), _renderScrollLastPresentQpc, (ulong)_renderTick.TickSeq, _maxFrameLatency,
                    Stopwatch.Frequency, out long presentQpc);
                _renderScroll.Tick(sceneFrame.Scene, _scrollKernel.LeaseRing, in sclock, presentQpc, _renderTick.TickSeq,
                    _device.WeakAdapterTier, _device as IRetainedLayerProbe);
                _renderScrollLastPresentQpc = presentQpc;
                _activeRenderFrame = rf; _hasActiveRenderFrame = true;
                if (!fresh && !_renderAnimations.ChangedThisTick && !_renderScroll.ChangedThisTick
                    && !sceneFrame.Images.HasCrossfades(RenderImageClock(rf, sceneFrame)))
                { … existing elide block A:1141-1150 unchanged … }
```

`_renderTick` is the render thread's `DisplayClockSample`, read from `IRenderDisplayClock.TickSeq/TickQpc` (§4.6) at
the top of the turn by `RenderThread` and handed in through `IRenderSource.SubmitPresent(rf, in DisplayClockSample tick)`
(the Phase-2 interface, ISO §4.3, gains the parameter). `HasOwnRenderMotion` (A:444-468): `active = _renderAnimations.HasActive
|| _renderScroll.HasActive || …crossfades`. The feedback block (A:1227-1243) appends the scroll poses after the
compositor poses: `RecordingFeedback` (`SceneRenderFrame.cs:177-186`) gains `public int ScrollPoseCount;` and the byte
layout becomes header | poses | scroll poses; `settledPoses` also counts `Ended != 0` scroll poses so the UI is nudged
(A:1258). Additionally: `if (_renderScroll.ChangedThisTick) _window.Wake();` — the realize-ahead follower wake (§2.5).

UI side: `ImportRecordingFeedback` (A:1318-1344) reads the scroll poses and calls `_scrollKernel.ApplyLeaseFeedback(poses)`
**after** the two per-frame counter bumps, which move from `Paint` (A:3768-3770) to `RunFrame` right after
`ImportRecordingFeedback` (A:3212) — `_scrollSink.FrameIndex++; _scrollChrome.FrameIndex++; _scrollSink.BeginFrame();` —
so a lease write stamps the same `LastMovedFrame` chrome's `Tick` compares against later in this `RunFrame`
(`SceneScrollSink.cs:27-31`). `PublishScene` (A:4435-4438) passes `_scrollKernel` so `SceneRenderFrame.Capture`
(`SceneRenderFrame.cs:29-81`) can `kernel.CaptureLeases(Leases, source, nowMs)` beside `animation.CaptureCompositorAnimations`
(`:47-48`), and `Scene.ReserveCompositorRows(Animations.DistinctNodeCount + Leases.Count)` (`:54`) reserves the overlay
rows the lease's content nodes need — the render thread never grows the pool (`SS.Animation.cs:139-152`).
`_scrollKernel.LeasesEnabled = _asyncActive && _renderThread is not null && _window.DisplayClock.Available` is refreshed at the top of
`Paint` (the headless/SingleThread host never leases — every VerticalSlice kernel/scroll gate keeps its meaning).

### 4.4 Snapshot additions — `SS.Animation.cs`

```csharp
    // ── Render-owned scroll offsets (RenderScrollLease) — read by SceneRecorder for the scrollbar thumb, the edge fade
    //    and the retained band; the content transform itself rides the CompositorPaint overlay. Fixed table, ≤ Cap rows.
    private struct RenderScrollRow { public int Node; public float OffX, OffY, BandX, BandY; public RetainedBand Band; public bool Retained; }
    private readonly RenderScrollRow[] _renderScroll = new RenderScrollRow[ScrollLeaseSnapshot.Cap];
    private int _renderScrollCount;

    internal void BeginRenderScrollEpoch() => _renderScrollCount = 0;   // called from BeginCompositorOverlay
    internal void SetRenderScrollOffset(NodeHandle vp, float offX, float offY, float bandX, float bandY, in RetainedBand band, bool retained)
    {
        for (int i = 0; i < _renderScrollCount; i++) if (_renderScroll[i].Node == (int)vp.Raw.Index) { Write(ref _renderScroll[i]); return; }
        if (_renderScrollCount == _renderScroll.Length) return;   // documented overflow: the authored offset draws this tick
        Write(ref _renderScroll[_renderScrollCount++]);
        void Write(ref RenderScrollRow r) { r.Node = (int)vp.Raw.Index; r.OffX = offX; r.OffY = offY; r.BandX = bandX; r.BandY = bandY; r.Band = band; r.Retained = retained; }
    }
    /// <summary>The offset the recorder must draw chrome against: the lease's while one is live, else the authored one.</summary>
    public bool TryGetRenderScroll(NodeHandle vp, out float offX, out float offY, out RetainedBand band, out bool retained)
    { … linear over ≤ Cap … }
```

`BeginCompositorOverlay` (`SS.Animation.cs:156-165`) calls `BeginRenderScrollEpoch()`. Because `RenderScrollLease.Tick`
runs after `_renderAnimations.Tick`/`Adopt` (which call `BeginCompositorOverlay`), the scroll rows for this epoch are
written after the reset — the ordering the code in §4.3 fixes.

### 4.5 `SceneRecorder` — the retained path and the render offset (`SR:`)

1. **Chrome reads the render offset.** In `WalkCore`, where `scrollState` is read (SR:1383), add
   `bool leased = scene.TryGetRenderScroll(node, out float rOffX, out float rOffY, out var rBand, out bool rRetained);`
   and pass `leased ? rOffX/rOffY : scrollState.OffsetX/Y` into `EmitScrollbar` (SR:3424 — add two `float offX, offY`
   parameters and use them instead of `sc.OffsetX/Y` inside) and into `TryResolveEdgeFade`'s "more content past the
   edge" test (SR:3546-3564 reads `sc.OffsetY`/`OffsetX` — same substitution). `leaseBaselineX/Y` (SR:1408-1409) read
   the same values.
2. **Retained hit/miss at the content child.** Replace the content-child walk (SR:2505-2523) with:

```csharp
                bool isLeaseContentChild = !leaseContentNode.IsNull && !leaseContentCaptured && c == leaseContentNode;
                if (isLeaseContentChild && rRetained && retainedProbe is not null)
                {
                    // Band rect in device space at the content's CURRENT composited position: the overlay transform is
                    // −bandStart, so bandStart maps to the content node's origin; translate = (bandStart − offset)·scale.
                    bool horizontal = scrollState.Orientation == 1;
                    float off = horizontal ? rOffX : rOffY;
                    float scaleAxis = MathF.Abs(horizontal ? activeChildWorld.M11 : activeChildWorld.M22);
                    float bandLenDip = rBand.End - rBand.Start;
                    RectF bandDevice = horizontal
                        ? new RectF(activeChildClip.X + (rBand.Start - off) * scaleAxis, activeChildClip.Y, bandLenDip * scaleAxis, activeChildClip.H)
                        : new RectF(activeChildClip.X, activeChildClip.Y + (rBand.Start - off) * scaleAxis, activeChildClip.W, bandLenDip * scaleAxis);
                    ulong layerId = ((ulong)node.Raw.Index << 32) | node.Raw.Gen;
                    bool hit = retainedProbe.IsResident(layerId, rBand.Epoch) && scene.RecordDirtyBits(c) == 0;
                    int pushAt = dl.BytePosition;
                    dl.PushRetainedLayer(bandDevice, activeChildClip, layerId, rBand.Epoch, translateSnappedPx: SnapTranslate(bandDevice, activeChildClip), key);
                    if (!hit)
                    {
                        // Miss: record the subtree against the BAND clip (rows outside the viewport but inside the band are
                        // recorded, not culled), at the constant −bandStart transform the overlay holds.
                        var childResult = Walk(scene, dl, images, c, activeChildWorld, opacity, depth + 1, bandDevice.Intersect(recordClipForBand),
                            in focus, in textEdit, scrollThumb, scrollTrack, childScaleX, childScaleY, inMotion, globalBlurHold,
                            scrollInMotion, userScrollActive, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                        result.Include(childResult);
                        stats.RetainedMisses++;
                    }
                    else stats.RetainedHits++;
                    dl.PopRetainedLayer(activeChildClip, key);
                    dl.PatchRetainedSkip(pushAt, dl.BytePosition - pushAt);   // SkipBytes = bytes to just past the Pop
                    stats.AddRepaint(RepaintBand(activeChildClip));           // the viewport's pixels moved (hit or miss)
                    result.Include(activeChildClip);
                    leaseContentCaptured = true; leaseHasAcrylicOrVideo = false;   // acrylic/video inside ⇒ not retained-eligible (UI-side gate)
                    continue;
                }
                … existing SR:2506-2523 (fallback: today's translated-copy path) …
```

`recordClipForBand` is the enclosing clip **expanded along the scroll axis to the band** (`activeChildClip` grown to
`bandDevice` on that axis only) so the cull at SR:1914-1920 and the `IsClipComplete` tests keep rows inside the band.
Everything outside the scroller is untouched. `SnapTranslate` rounds `(bandDevice.Y − clip.Y)` (or X) to whole device
px — the composite must land on the pixel grid (Chromium `UpdateSnapping`, WebRender `snap_offset`).

3. **`IRetainedLayerProbe`** (`Rhi.cs`, new interface beside `IGpuDevice`): `bool IsResident(ulong layerId, ulong epoch);`
   `bool WeakAdapterTier { get; }`. `SceneRenderFrame.Record` (`SceneRenderFrame.cs:93-103`) takes it as a parameter
   (`AppHost` passes `_device as IRetainedLayerProbe`, headless passes `null`); `SceneRecordOptions` is unchanged.
4. `IsDirectMovingScrollContent` (SR:2967-2973) and `HasContinuousScrollBindInside` (SR:2980-2994) are unchanged; the
   retained path never reaches them (it returns before the fallback walk).

### 4.6 `IRenderDisplayClock` gains the tick sample — `Pal.cs:612-617`, `Win32CompositorClock.cs:173-200`

```csharp
public interface IRenderDisplayClock : IDisposable
{
    System.Threading.WaitHandle Tick { get; }
    bool IsAvailable { get; }
    void SetActive(bool active);
    /// <summary>The latest published tick (seq + vblank instant, Stopwatch domain) — the same pair the UI reads through
    /// IPlatformWindow.DisplayClock. Render-thread read; Volatile.</summary>
    DisplayClockSample Sample => default;
}
```

`RenderSubscription.Sample => owner.IsAvailable ? new DisplayClockSample(true, owner.TickSeq, owner.TickQpc) : default`
(`Win32CompositorClock.cs:122-123` are already `Volatile.Read`). `RenderDisplayClockTests` gains one fact.

---

## 5. Pacing — `RenderThread`, `D3D12Device`, the seam, the input wait

**Depends on ISO Phase 2 (§4.3 loop, `IRenderSource`, `PresentSlotWaitHandle`/`PresentCreditHeld`) having landed.**

### 5.1 The loop (ISO §4.3 loop + this plan) — `RT:`

Per source, the turn keeps ISO's `WindowPacingScheduler.Decide` for *which* windows may present and adds the
`PresentCadence` verdict for *whether* this tick may present again:

```csharp
            long tickSeq = _displayClock is { IsAvailable: true } dc ? dc.Sample.TickSeq : 0;
            for (int i = 0; i < _sources.Length; i++)
            {
                var s = _sources[i]; _inputs[i].CreditHeld = s.Swapchain.PresentCreditHeld;
                if (WindowPacingScheduler.Decide(in _inputs[i]) == WindowTurnAction.Skip) continue;
                var verdict = PresentCadence.Decide(new PresentCadenceInput
                {
                    TickSeq = tickSeq, LastPresentedTickSeq = _lastPresentedTick[i],
                    HasFreshPublication = s.Seam.HasPendingFrame, MotionDue = _inputs[i].MotionDue,
                    CreditHeld = s.Swapchain.PresentCreditHeld, Unpaced = !_inputs[i].HasWaitable || tickSeq == 0,
                });
                if (verdict == PresentVerdict.Skip) continue;
                var tick = _displayClock?.Sample ?? default;
                if (verdict == PresentVerdict.PresentFresh && s.Seam.TryAcquire(out var rf)) s.SubmitPresent(rf, in tick);
                else s.Tick(in tick);                                   // motion-only: re-present the retained scene
                if (s.Swapchain.PresentedThisTurn) _lastPresentedTick[i] = tickSeq;
            }
```

`ISwapchain.PresentedThisTurn` is a render-thread bool set by `Present` on the fall-through success path (D:4892
`NotePresentedContent()` site) and cleared at the top of the turn — it exists so a stood-down present (occluded,
D:4870-4876) does not consume the tick. The motion-tick period `_nextTick`/`_tickPeriod` (RT:47, RT:150, ISO §4.3 `_nextTick[i]`)
is deleted: motion turns are driven **only** by the display tick; `TimeoutMs`'s motion branch becomes the backstop
(2 refreshes, clamped 8–34 ms — the same `TickBackstopMs` rule as the UI, A:2059-2064) for a stalled compositor.

### 5.2 Delete interval-0 scroll presents

| Site | Change |
|---|---|
| `RenderFrame.cs:36-39` | delete `InteractivePresent` |
| `SceneFramePublisher.cs:91-92, 124, 163-166, 210` | delete the `interactivePresent` parameter/field |
| A:1021-1025 `ApplyPresentPacing` | `if (rf.SuppressVsync) { _swapchain.SuppressVsyncOnce(); _swapchain.SuppressLatencyWaitOnce(); }` — one arm (the ISO Phase-1 spelling on `ISwapchain`) |
| A:4423-4427, A:4438, A:4466, A:4491-4492 | delete `interactivePresent`; the inline path keeps only the `SuppressVsync` arm |
| `Rhi.cs:333-336`, D:6034 | delete `SupportsCompositedIntervalZero` |
| D:4852-4861 | unchanged: interval 1 unless the modal keep-alive `_skipVsyncOnce` (per-target after ISO Phase 1) |

`_vsync` (D:90, the `FG_NOVSYNC` diagnostics flag) is pre-existing and untouched by this plan.

### 5.3 Attested statistics replace `missedVsyncs`

- `TargetFrameState` (ISO §3.1) gains `internal PresentStatisticsLedger PresentLedger; internal long PresentsSubmitted;`.
  `Present` (D:4839) increments `PresentsSubmitted` on the fall-through success path and, after `SamplePresentStats()`
  (D:4903), calls `f.PresentLedger.Observe(f.LastPresentStats.Valid, .PresentCount, .PresentRefreshCount, f.PresentsSubmitted)`.
- `ISwapchain` gains `long PresentsDisplayed => 0; long PresentsDropped => 0; long VblanksRepeated => 0;` (render-written,
  UI-read gauges — the `LastFenceWaitMs` contract, ISO §3.3).
- `FrameStats` (A:150-283): `MissedVsyncs` (A:276) → deleted; add `PresentsDisplayed`, `PresentsDropped`, `VblanksRepeated`
  (all cumulative), plus `ScrollLeasedBodies`, `ScrollRetainedBodies`, `RetainedHits`, `RetainedMisses` (from the
  imported record stats). A:4670 reads the swapchain gauges; `_missedVsyncsTotal` and the stamp math at A:5600-5612
  are deleted (`NotePresented` keeps the stamp, the count, and the publish-seq ack); `MissedVsyncsTotalForTest`
  (A:2655) is deleted with its test use. `EmitLatencyRow` (A:5432-5440): the stamp-based `missedVsyncs` is deleted;
  `missedPacked` carries only the attested value (A:5447-5459 unchanged).
- Wavee: `Diagnostics.Host.cs:371-372, 540` read `MissedVsyncs` → `VblanksRepeated` (the `missedVblanks=` token in
  `nav.frames` is renamed `repeated=` and gains `dropped=`); `Diagnostics.ScrollTrace.cs:4` comment updated.
- One always-on engine line per second while motion is live, `[render.pace] tick=… presents=… displayed=… dropped=…
  repeated=… leases=… retainedHit=… retainedMiss=…` (Diag.Line from the render thread, allocation only on the 1 Hz path
  like `MaybeReportGlitches`, D:5082). Wavee whitelists `[render.pace]` beside `[compositor-clock]`
  (`Platform.Host.cs:891`) — without it the line never reaches `wavee-*.log`.

### 5.4 Wheel packets are urgent — `W32:263-265`, `PacedInputWaitClassifierTests.cs:30-31`

```csharp
    internal static bool IsDeferrable(uint message) => message is
        WmSetCursor or WmNcMouseMove or WmMouseMove or WmNcPointerUpdate or WmPointerUpdate;
```

The class doc (W32:241-252) is rewritten: wheel packets end the wait so the notch reaches the port (and the lease
ring) at once; production stays gated per tick. The two `Assert.True(IsDeferrable(WmPointerWheel/HWheel))` become
`Assert.False`; the `IsMotion` facts stay.

---

## 6. The retained layer in the DrawList and the D3D12 backend

### 6.1 `DrawList.cs`

- `LayerKind` (`:182-205`) gains `Retained = 4`.
- `PushLayerCmd` (`:298-320`) gains three trailing fields: `ulong RetainEpoch = 0, int SkipBytes = 0, float TranslateX = 0f, float TranslateY = 0f`.
  (Opcode-shape authority: `gpu-renderer.md §3.6` — §9.)
- New emitters next to `PushEdgeFadeLayer` (`:693`): `PushRetainedLayer(in RectF bandDevice, in RectF compositeClip, ulong layerId, ulong epoch, in Point2 translateSnappedPx, ulong sortKey)`
  (Kind = Retained, `DeviceRect = bandDevice`, `CompositeClip = compositeClip`, `LayerId = layerId`), `PopRetainedLayer(in RectF compositeClip, ulong sortKey)`
  (a plain `PopLayer`), and `PatchRetainedSkip(int pushByteOffset, int skipBytes)` (in-place write of `SkipBytes`, the
  `PatchOpacityLayerExtent` idiom, `:303-305`).
- `TranslateCopiedSpan` (`:869`) and `TranslateSpan` (`:1001`): the `PushLayer` arm translates `DeviceRect`/`CompositeClip`
  for `Retained` exactly as for `Opacity` (`:1132-1140`), and leaves `TranslateX/Y` untouched (they are relative).

### 6.2 `RepaintPolicy.cs`

`RepaintStreamSafety.Scan` (`:412-414`): admit `(int)LayerKind.Retained` — its composite is a clipped blit inside the
clamp; nesting rules unchanged. `StreamLayerKind` (D:1971) counts `Retained` as `LayerKindGroups`.

### 6.3 `D3D12Device` — `WalkLayered` (D:4028) arms

`PushLayer` (the Opacity arm at D:4307-4309 is the template):

```csharp
                    case (int)LayerKind.Retained:
                    {
                        RECT band = ToPixelRectRoundOut(L.DeviceRect);
                        int rslot = _opacity!.FindRetained(L.LayerId, L.RetainEpoch, (uint)band.W, (uint)band.H, _fenceValue + 1);
                        if (rslot >= 0 && L.SkipBytes > 0)
                        {
                            _opacity.CompositeTranslated(_cmdList, rslot, band, ToPixelRectRoundOut(L.CompositeClip), L.TranslateX, L.TranslateY);
                            p = pushByteOffset + L.SkipBytes;   // skip the (absent) subtree and its PopLayer
                            continue;
                        }
                        rslot = _opacity.AcquireRetained(_cmdList, _fenceValue + 1, (uint)band.W, (uint)band.H, L.LayerId, L.RetainEpoch);
                        _opacityGroups.Add(new LayerGroup(rslot, L, 0UL, default));
                        SetLocalViewport(band);                 // band-local target: content records at −bandStart, so band.X/Y map to (0,0)
                        BindOpacityGroupTarget(_opacityGroups[^1]);
                        break;
                    }
```

`PopLayer` (D:4378-4412): for a `Retained` group, `_opacity.RetainSlot(slot)` (marks `PinHash = LayerId`, stores
`RetainEpoch`, `BlurReady = true`, so `Acquire` never grabs it as a transient — D:926's `PinHash != 0` guard), then
`CompositeTranslated` as above and restore the enclosing target/viewport (`SetFullViewport`/`BindOpacityGroupTarget`).
`SetLocalViewport` is `SetLocalBlurViewport` (D:2929, the region-local viewport the local-blur path already uses,
`TryBeginRegionLocalBlur` D:4175) generalised to any region — no new mechanism.

### 6.4 `OpacityLayerCompositor` — retained pins

Pool entries already carry `PinHash`, `BlurReady`, `PinLayerId`, `PinMintFence`, `LastUseFence`, `IdleFrames`
(`OpacityLayerCompositor.cs:61-74`). Add `public ulong RetainEpoch;`. New members, all allocation-free over the fixed
pool (`MaxPool`):

- `int FindRetained(ulong layerId, ulong epoch, uint w, uint h, ulong frameFence)` — `PinHash == layerId && RetainEpoch == epoch
  && BlurReady && W >= w && H >= h && !InUse` (bucketed dims via `LayerTargetBucket.Dim`); a hit stamps `LastUseFence`/`IdleFrames`
  exactly like `FindPin` (`:1440-1455`).
- `int AcquireRetained(cmd, frameFence, w, h, layerId, epoch)` — best-fit free slot ≥ bucket, cleared over the band, else
  create (cold path — the same `CreateCommittedResource` a canvas lease pays); evicts the previous pin with the same
  `layerId` (one pin per scroller).
- `void RetainSlot(int slot)` and `void CompositeTranslated(cmd, slot, RECT srcBand, RECT clip, float dx, float dy)` — the
  `Composite` quad (`:1004`) with a destination offset and the clip as scissor; sampled 1:1, integer texel fetch (the
  §13.1a route-parity rule: "an integer fetch, never a filtered sample").
- Trim: `LayerTargetTrim.Classify` treats a retained pin as a pin (`PinIdleFrames`) — a scroller idle for the pin window
  loses its band and re-rasters once on the next fling. `IRetainedLayerProbe.IsResident` = `FindRetained(...) >= 0`
  without the stamp (a read-only probe; the recorder runs on the render thread, so this is single-toucher).

### 6.5 Headless

`HeadlessGpuDevice.SubmitDrawList` decodes `Retained` as `Opacity` (renders the subtree, ignores the skip — a headless
recorder never sees a resident pin because `IRetainedLayerProbe` is null there, so `SkipBytes` is always a full
subtree). `HeadlessGpuDevice` does **not** implement the probe; the recorder gates use a `FakeRetainedProbe` in the
suite (§7.1).

---

## 7. Gates

### 7.1 VerticalSlice — new suite `src/FluentGpu.VerticalSlice/Suites/ScrollLeaseSuite.cs`, registered
`new("lease", "lease", ScrollLeaseSuite.Run)` in `SuiteRegistry.cs:41-70` (after `"kernel"`)

| Gate | Assertion |
|---|---|
| `gate.lease.present-once-per-tick` | every row of the §3.4 table; 10 consecutive turns on the same tick with fresh publications present exactly once |
| `gate.lease.present-stats-ledger` | (submitted 10, ΔPresentCount 8, ΔRefresh 12) → displayed 8, dropped 2, repeated 4; uint wrap across 0xFFFF_FFFF; `Valid=false` resets and counts 0 |
| `gate.lease.sample-at-present` | `RenderScrollClock.Build` at tick T, refresh R, latency 1 → `PresentSec == (T + 2R)/f`; dt between consecutive ticks == R; a stale tick (now > T + 2R) re-snaps like `RefreshLattice` (`RefreshLatticeTests.cs:28`) |
| `gate.lease.trajectory-identical` | one `ScrollBody` seeded Ballistic 3000 DIP/s, advanced UI-side by `ScrollKernel.Tick` vs render-side by `RenderScrollLease.Tick` over the same PresentSec sequence → offsets identical to the ulp, same settle tick (`ScrollBody.Advance` is the only integrator) |
| `gate.lease.wheel-forwarded` | a body leased Driven\|Wheel; `WheelNotch` posted to the UI port → the UI mirror is untouched, the ring holds one command, the render tick applies it (target advanced by `PerNotchDip`) |
| `gate.lease.revoke-no-backstep` | Revoke forwarded mid-fling → final pose has `Ended == 2`, `ApplyLeaseFeedback` rebases; the next UI `ContactBegin` anchors at the adopted offset; offset never decreases across the hand-off |
| `gate.lease.feedback-ordering` | the pose applied at the top of `RunFrame` is visible to a synthetic hit-test (`InputDispatcher`) in the same frame; `LastMovedFrame == FrameIndex` in that frame (chrome's test) |
| `gate.lease.grant-rides-publish` | `CaptureLeases` emits Grant once, then Continue; a headless host (`LeasesEnabled == false`) captures 0 entries and ticks on the UI as today |
| `gate.lease.alloc-zero` | 600 render ticks with 4 leased bodies + 50 forwarded notches: `GC.GetAllocatedBytesForCurrentThread()` delta == 0 on the ticking thread; `ScrollKernel.ApplyLeaseFeedback` delta == 0 |
| `gate.lease.overlay-rows-reserved` | `ReserveCompositorRows(anim + leases)` ⇒ `CompositorRowOverflows == 0` for a publication with 32 leases and 256 animated nodes |
| `gate.retained.band-plan` | `Plan`: velocity skew (ahead grows with \|v\|, clamps at Max·vp), symmetric floor at rest, clamped to `[0, extent]`, weak tier caps at 1·vp |
| `gate.retained.hit-miss` | recorder over a 200-row list with a fake probe: miss → subtree recorded once against the band clip, `RetainedMisses == 1`; three later ticks inside the band → `RetainedHits == 3`, no `DrawGlyphRun`/`FillRoundRect` for rows in the stream, `SkipBytes` spans to the Pop; repaint region == viewport band each tick |
| `gate.retained.outrun-fallback` | 3 misses within 8 ticks → `Outrun`, the recorder takes the translated-copy path (`SpansRebased > 0`), no `PushLayer(Retained)` |
| `gate.retained.eligibility` | each `RetainedEligibilityInput` clause flips `Eligible` (binds, acrylic/video, zoom, nested lease, popup, budget) |
| `gate.retained.stream-safe` | `RepaintStreamSafety.Scan` admits a `Retained` group and still refuses Acrylic; `RepaintPolicy.Decide` returns `Partial` for a viewport-band region under `LayerKindGroups` |
| `gate.retained.translate-span` | `TranslateCopiedSpan` over a span containing a `Retained` push moves `DeviceRect`/`CompositeClip` and leaves `TranslateX/Y` |
| `gate.retained.content-dirty-bumps-epoch` | a publication that dirties one row inside the content ⇒ the next tick misses (re-raster) exactly once, then hits |

### 7.2 Existing suites that must stay green

`dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`: `kernel` (every `gate.kernel.*`,
`ScrollKernelSuite.cs`), `scroll` + `scroll-pacing` (`gate.scroll.wheel-notch-dip`), `wake-present`, `touch`, `damage`
(`gate.damage.*`, `gate.repaint.*`), `layerpool`, `diagnostics` (`gate.seam.race`), `anim` + the compositor gates
(`gate.compositor-*`, `CompositorAnimationChecks.cs:56-439`), `detached-render` (ISO), the zero-alloc phases 6–13
tripwire. `dotnet test src/FluentGpu.Engine.Tests`: `RefreshLatticeTests`, `RenderThreadLifecycleTests` (constructor
updated for ISO Phase 2), `RenderLifecycleTests`, `IndependentCompositorTests`, `ScrollKernelWakeCountTests`
(gains: a `LeasedOut` body is excluded from `WakeActiveCount`), `WheelOverscrollTests`, `RenderOwnershipTests`.
`dotnet test src/FluentGpu.Windows.Tests`: `PacedInputWaitClassifierTests` (wheel facts flipped), `RenderDisplayClockTests`
(+ `Sample`), `FrameBankingTests`, `CompositorTickFilterTests`, `DirectManipulationPacingTests`. Debug **and** Release
builds clean; `--screenshot` unchanged pixels; `--repaint-identity` 8/8 plus a new `retained-band-straddle` scenario;
`powershell -File docs/design/check-canon.ps1` exit 0.

### 7.3 Live probe — `src/FluentGpu.WindowsApp/Probes/ScrollPaceProbe.cs` (new), `Program.cs`

Invocation (CLI flag, no environment switch — the `--detached-stress` precedent, ISO §7.3):
`dotnet run --project src/FluentGpu.WindowsApp -c Release -- --scroll-pace [outDir] [--notches 40] [--gapMs 90] [--flings 5]`.

1. Navigate to the long-list gallery page (`GalleryShell.StressNavKeys`), tee `Diag.Line` to `outDir/scroll-pace.log`.
2. Inject wheel packets with `SendInput` at `--gapMs` cadence (the same synthetic path `ops/diag/wavee-scroll-session.ps1`
   uses) and, when a touch digitiser is available, `InjectTouchInput` flings; record each packet's QPC.
3. Per packet: the first present whose `ScrollLeasePose.PresentQpc`-sampled offset differs from the previous one
   (from `FrameStats.ScrollDeltaDip`/the pose feed the probe subscribes to) → `input→present = presentQpc − inputQpc`
   (the DXGI-attested `SyncQpc` of that present, `PresentStats.SyncQpc`, is the photon-side stamp: vblank granularity,
   as `PresentStats.cs:14-24` states).
4. Per 1 s window during motion: `PresentsDisplayed/Dropped/VblanksRepeated` deltas, `ScrollLeasedBodies`,
   `RetainedHits/Misses`, UI frames produced, render-thread `GC.GetAllocatedBytesForCurrentThread()` delta (exposed the
   way ISO §7.3 exposes it), `RecordMs` p95, `GpuRenderMs` p95.
5. Exit 0 iff over the glides: `PresentsDropped == 0`, `VblanksRepeated ≤ 1 %` of vblanks, input→present p95 ≤ 3 refresh
   periods + 1 ms, render-thread allocation delta 0, `RetainedHits ≥ 0.8 · (Hits + Misses)` on the long list, and no
   `[d3d12.forensic]`/device-lost lines. Summary line:
   `[scroll-pace] notches=40 flings=5 i2p.p50=… i2p.p95=… displayed=… dropped=0 repeated=… retainedHit=… uiFrames/s=… allocBytes=0 verdict=PASS`.

Owner runs it on the 120 Hz primary and the 99.98 Hz secondary; it is not CI. Wavee's own `nav.frames`/`scroll.frames`
rollups pick up the renamed fields (§5.3) on the next app build.

---

## 8. Execution partition

The orchestrator alone builds/tests between waves: `dotnet build src/FluentGpu.slnx` **and** `-c Release`;
`dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`; both test projects; `dotnet build
C:\wavee\WaveeMusic\Wavee.slnx` (Debug + Release) whenever a seam member or `FrameStats` moved; the owner runs
`--scroll-pace` and `--repaint-identity`. The user's running Debug Wavee locks Debug outputs (MSB3021/27) — build to a
side folder or ask first.

**Ordering against the concurrent work.** (1) The Firefox-Bezier wheel model (`ScrollPhysics.cs`) and (2) snap-at-write
(`ScrollContentTransform.cs`) + softening removal (`SceneRecorder.cs`) **land first**; Wave S0 rebases on them and
touches neither `ScrollPhysics.cs` nor `ScrollContentTransform.cs`. ISO Waves 0–3 own `D3D12Device.cs`, `AppHost.cs`,
`RenderThread.cs`, `Rhi.cs`, `DetachedRenderResilienceSuite.cs` while they run; this plan's waves that touch those files
(S1, S2, S3) are scheduled **after ISO Wave 3 merges**. S0 is disjoint from every ISO file and runs in parallel with
ISO Waves 1–3.

**Merge hazards.** One owner per wave for `AppHost.cs`, `D3D12Device.cs`, `RenderThread.cs`, `Rhi.cs`, `SceneRecorder.cs`,
`DrawList.cs`, `ScrollKernel.cs`, `OpacityLayerCompositor.cs`. Agents write against the signatures printed here; the
orchestrator resolves drift at the wave build, never by a second agent editing the same file.

### Wave S0 — pure Engine (parallel with ISO Waves 1–3; after (1)+(2))

| Agent | Files (exclusive) | Work |
|---|---|---|
| S0-A | `Engine/Scroll/ScrollKernel.cs`, `Engine/Scroll/ScrollBody.cs`, `Engine/Scroll/ScrollBodyOps.cs` (new), `Engine/Scroll/ScrollInput.cs` (`Revoke` kind), `Engine/Scroll/ScrollCommandPort.cs` (`IsStructural` row) | §3.1 extraction, §4.2 kernel changes (forwarding, grant pass, `CaptureLeases`, `ApplyLeaseFeedback`), no host wiring |
| S0-B | `Engine/Scroll/ScrollLease.cs` (new), `Engine/Scroll/RenderScrollLease.cs` (new), `Engine/Hosting/RenderScrollClock.cs` (new) | §3.2, §4.1, §3.3 (compile against S0-A's pinned signatures; `SceneRecordingSnapshot` members from S1-B are stubbed behind the §4.4 signatures — the orchestrator wires them in S1) |
| S0-C | `Engine/Hosting/Threading/PresentCadence.cs` (new), `Engine/Hosting/Threading/PresentStatisticsLedger.cs` (new), `Engine/Render/RetainedScroll.cs` (new) | §3.4, §3.5, §3.6 |
| S0-D | `VerticalSlice/Suites/ScrollLeaseSuite.cs` (new), `VerticalSlice/Harness/SuiteRegistry.cs`, `Engine.Tests/ScrollKernelWakeCountTests.cs` | every pure gate of §7.1 that needs no recorder/backend (`present-once`, `ledger`, `sample-at-present`, `trajectory-identical`, `wheel-forwarded`, `revoke-no-backstep`, `grant-rides-publish`, `alloc-zero`, `band-plan`, `eligibility`) |

Then: build both configs; VerticalSlice; Engine.Tests. **Nothing behavioural changes yet** (`LeasesEnabled` is false
everywhere).

### Wave S1 — the lease on the render thread + pacing (after ISO Wave 3)

| Agent | Files | Work |
|---|---|---|
| S1-A | `Engine/Hosting/AppHost.cs`, `Engine/Hosting/Threading/SceneRenderFrame.cs`, `Engine/Scroll/SceneScrollSink.cs` | §4.3 (render side + UI side, counter move, `LeasesEnabled`), `Leases` on `SceneRenderFrame`, `RecordingFeedback.ScrollPoseCount`, §5.2 AppHost rows, §5.3 `FrameStats` |
| S1-B | `Engine/Scene/SceneRecordingSnapshot.Animation.cs`, `Engine/Seams/Pal/Pal.cs`, `Windows/Pal/Win32CompositorClock.cs`, `Windows.Tests/RenderDisplayClockTests.cs` | §4.4 offset table, §4.6 `Sample` |
| S1-C | `Engine/Hosting/Threading/RenderThread.cs`, `Engine/Hosting/Threading/RenderFrame.cs`, `Engine/Hosting/Threading/SceneFramePublisher.cs`, `Engine.Tests/RenderThreadLifecycleTests.cs` | §5.1 loop (on the ISO Phase-2 loop), §5.2 seam deletions, `PresentedThisTurn` |
| S1-D | `Windows/D3D12/D3D12Device.cs`, `Windows/D3D12/TargetFrameState.cs`, `Engine/Seams/Rhi/Rhi.cs`, `Engine/Headless/Rhi/HeadlessGpuDevice.cs` | §5.2 device rows, §5.3 ledger + `ISwapchain` gauges + `[render.pace]`, `IRetainedLayerProbe`/`WeakAdapterTier` declarations (implementation in S2) |
| S1-E | `Windows/Pal/Win32Platform.cs`, `Windows.Tests/PacedInputWaitClassifierTests.cs`, `C:\wavee\WaveeMusic\src\apps\Wavee\Platform\Platform.Host.cs`, `…\Screens\Diagnostics.Host.cs`, `…\Screens\Diagnostics.ScrollTrace.cs` (+ CHANGELOG bullet with the issue ref) | §5.4, §5.3 Wavee rows, `[render.pace]` whitelist |

Then: build both configs; VerticalSlice (S0-D gates now run against the wired host: `feedback-ordering`,
`overlay-rows-reserved`); both test projects; Wavee build; owner: `--scroll-pace` (expect `dropped=0`, i2p down by the
UI-frame term, `retainedHit=0` — the band is S2).

### Wave S2 — the retained layer

| Agent | Files | Work |
|---|---|---|
| S2-A | `Engine/Render/DrawList.cs`, `Engine/Seams/Rhi/RepaintPolicy.cs` | §6.1, §6.2 |
| S2-B | `Engine/Render/SceneRecorder.cs`, `Engine/Hosting/Threading/SceneRenderFrame.cs` (`Record` probe parameter only) | §4.5 |
| S2-C | `Windows/D3D12/D3D12Device.cs`, `Windows/D3D12/OpacityLayerCompositor.cs`, `Engine/Render/LayerTargetPool.cs` (trim row) | §6.3, §6.4 |
| S2-D | `Engine/Headless/Rhi/HeadlessGpuDevice.cs`, `VerticalSlice/Suites/ScrollLeaseSuite.cs`, `VerticalSlice/Suites/DamageSuite.cs`, `WindowsApp/Probes/RepaintIdentityScene.cs` + `RepaintIdentityProbe.cs` | §6.5, the `gate.retained.*` recorder/stream gates, the `retained-band-straddle` identity scenario |

Then: build; VerticalSlice; tests; `--repaint-identity`; owner: `--scroll-pace` (expect `retainedHit ≥ 0.8`,
`GpuRenderMs` p95 down on the long list).

### Wave S3 — probe, docs, canon (one agent per file)

`WindowsApp/Probes/ScrollPaceProbe.cs` + `Program.cs` (§7.3); the §9 docs; `check-canon.ps1`.

---

## 9. Design docs to reconcile (canon)

| Doc | Section | Change |
|---|---|---|
| `docs/design/subsystems/threading-render-seam.md` | §1.1 table (`:245-260`) | new rows: *leased `ScrollBody` copies + `RenderScrollLease` slots* → RENDER; *`ScrollLeaseSnapshot`* → UI write / RENDER read at PUBLISH; *`ScrollLeaseRing`* → UI write / RENDER read (SPSC); *`ScrollLeasePose` feedback* → RENDER write / UI read (reverse mailbox); *render scroll offset table + retained bands* → RENDER |
| same | §4 R0–R5 (`:600-628`) | insert **R1.5 SCROLL/COMPOSITOR TICK** (drain the lease ring → advance leased bodies to the present instant → overlay writes) before R2 RECORD |
| same | §11.1 (`:924-1012`) | "the render loop reserves its present slot before acquiring" gains the tick rule: one present per compositor tick per window (`PresentCadence`); the motion-tick period is gone; interval-0 interactive presents are gone; attested `PresentStatisticsLedger` replaces stamp-based missed-vsync accounting |
| same | §14 thread map (`:1559-1578`) | phase 2.5 (scroll kernel tick) splits: UI for Drag/structure, RENDER for leased Ballistic/Driven bodies; phase 11 row gains "one present per tick" |
| `docs/design/subsystems/gpu-renderer.md` | §3.6 opcode shapes; §7.1; §13.1a | `LayerKind.Retained` + the three `PushLayerCmd` fields; the retained band composite (`CompositeTranslated`, integer fetch); *Replay-unsafe streams* row admits `Retained`; §13.1 point 4 "scroll lands here by policy" is no longer true — a retained scroll frame takes `Partial` |
| `docs/design/subsystems/pal-rhi.md` | §2 `ISwapchain`; §0 | `PresentsDisplayed/Dropped/VblanksRepeated` gauges, `PresentedThisTurn`; `IRetainedLayerProbe`; `SupportsCompositedIntervalZero` deleted; `IRenderDisplayClock.Sample` |
| `docs/design/subsystems/scroll.md` (owner doc per scroll-v3 §8; create if still absent) | new §lease | the render lease as built: grant/continue/revoke, `ScrollBodyOps`, present-time sampling, the retained band policy, the one-authoritative-offset rule, the Drag exclusion |
| `docs/design/subsystems/virtualization.md` | realize-ahead | the adopted offset drives realization; a realize batch re-rasters the band once |
| `docs/design/SPEC-INDEX.md` | §2 | new row **Compositor-owned scroll** (owner `scroll.md`: lease seam + `RenderScrollLease` + `RetainedBandPolicy` + `PresentCadence`); amend the render-thread-seam row ("…and the render thread owns leased scroll bodies, ticked on the compositor tick, one present per tick") |
| `docs/design/subsystems/README.md` | ownership map | `ScrollBodyOps`, `ScrollLease*`, `RenderScrollLease`, `RenderScrollClock` → `scroll.md`; `PresentCadence`, `PresentStatisticsLedger` → `threading-render-seam.md`; `RetainedBandPolicy`/`RetainedScrollPolicy`/`LayerKind.Retained` → `gpu-renderer.md` |
| `docs/plans/render-thread-animation-design.md` | status header; §3.2 row "scroll offset" (`:265`); §3.11 | "superseded by `compositor-scroll-implementation.md`: the render-thread record + overlay landed; scroll is a compositor-owned channel through the same overlay" |
| `docs/plans/scroll-v3-plan-2026-08-17.md` | §6, §13.2 item 5 | status: §6 implemented by this plan (the patch-span ticker is replaced by the overlay + retained layer; `TranslateSpan` stays as the fallback's primitive); item 5 "Render thread: unchanged" is superseded |
| `docs/plans/detached-window-render-isolation-implementation.md` | §4.3 loop | pointer to §5.1 of this plan (the tick rule and `SubmitPresent(rf, in tick)`) |
| `docs/guide/` (the `fluentgpu` skill file map) | scroll / render rows | where the lease, the band, the cadence live |

Run `powershell -File docs\design\check-canon.ps1` after every doc edit.

---

## 10. Risks, rollback, open questions

**Risks.**
1. **Retained band memory on UMA adapters.** A 2-viewport band at 1195×767 px is ~7 MiB pinned host memory; the weak-tier
   cap (1·vp ahead, 8 MiB budget) and `LayerTargetTrim`'s pin window bound it, and `RetainedScrollPolicy` refuses the
   band above budget (fallback keeps the latency win). The `--scroll-pace` census (`gpu bytes` breakdown, D:4900) is
   the evidence.
2. **Re-raster frequency on fast virtual lists.** Every realize batch re-rasters the band (content dirty ⇒ epoch bump).
   `DirectionalOverscan` realizes ~100 ms ahead (`VirtualLayout.cs:97`), so at 3000 DIP/s a 40-row batch lands every
   ~4 ticks — one full record each. Bounded by the `Outrun` demotion; tiles (§10 follow-up) remove it.
3. **The counter move (§4.3).** `FrameIndex`/chrome bumps move from `Paint` to `RunFrame`; a declined production bumps
   without a `Paint`. `ScrollBarChrome`'s reveal is armed by `NotifyMoved` (`SceneScrollSink.cs:71`), which is edge-based,
   so a declined frame cannot lose a reveal; `gate.lease.feedback-ordering` pins the equality chrome tests.
4. **Two overlay writers on one node.** A compositor animation row and a scroll lease could both pose the content node;
   `RetainedScrollPolicy` treats an animated content node (`AnimChannel.TranslateX/Y` on the content) as ineligible
   and the lease skips the overlay write when the animation overlay already seeded a row this epoch (`Have & HavePaint`).
5. **Wheel urgency reversal** changes `PacedUrgentBreaks` semantics in Wavee's `scroll.frames` rollup — documented in
   the CHANGELOG bullet; production is still one frame per tick.
6. **`SkipBytes` correctness.** A hit whose pin was trimmed between the recorder's probe and the backend's walk (same
   thread, same turn — impossible by construction, but the backend still guards: `rslot < 0 && SkipBytes > 0` ⇒
   `Diag.Count("d3d12", "retainedProbeMiss")` and draws nothing for the band that frame; the next tick re-records
   because `IsResident` is false).

**Rollback.** S0 is inert (no host wiring) and stands alone. S1 reverts as a unit (kernel forwarding + host + loop +
seam deletions); S2 reverts independently of S1 (the fallback path is S1's steady state). The `MissedVsyncs` rename is
in S1 and reverts with it.

**Open questions for the owner (genuinely undecidable here).**
1. **Weak-tier band policy:** ship the retained band on UMA adapters with the 1·vp / 8 MiB cap, or disable it there
   (translate-only fallback) until the `--scroll-pace` census on the Adreno X1-85 shows the pinned-memory cost?
2. **Render-initiated grant for the first notch:** the first `WheelNotch` of a cold gesture costs one UI frame today
   (the grant rides the publish). A render-initiated grant (the render thread seeds a Driven body from the snapshot's
   `ScrollState` when a forwarded notch names an Idle, eligible body, and the UI adopts it) removes that frame at the
   cost of a second grant path and a race window to specify. Worth it now, or after S2 measurements?
3. **Wheel urgency:** confirm reversing the 2026-09 "wheel packets are deferrable" rule (§5.4). The alternative keeps
   them deferrable and accepts up to one extra refresh on the first notch only (later notches are forwarded on the ring
   from an urgent-free wake anyway).
4. **Present prediction source:** tick + 2·refresh (this plan; the UI's rule) vs `IDCompositionDevice::GetFrameStatistics().nextEstimatedFrameTime`
   read on the render thread. Only a live `clockSampleSkewMs` A/B on the two-monitor setup can decide; the plan keeps
   the seam so it is a one-function swap in `RenderScrollClock`.
5. **Scope confirmation:** Drag (touch/DM/hi-res fallback) stays UI-owned; sticky/collapse bind targets inside a
   scroller's content keep the translate-only fallback (no band carve-outs) in this plan.

**Follow-ups (not in scope, named so they are not re-litigated).** Tiled retained content (Chromium/WebRender tiles)
so a realize batch re-rasters only new tiles; sticky/pinned carve-outs rendered outside the band; Drag on the render
thread via a forwarded contact-sample ring (bounded extrapolation window, scroll-v3 §6's `DragExtrapolateMaxMs`).
