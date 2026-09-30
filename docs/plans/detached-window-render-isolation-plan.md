# Detached windows: render isolation (crash + pop-out slowdown)

Status: **proposed, awaiting the owner's go** (2026-09-22). Engine work (`src/FluentGpu.Windows/D3D12`,
`src/FluentGpu.Engine/Hosting`). Wavee is the reporter; no app change is required.

## 1. What happened

**Crash.** Wavee 0.3 (Release arm64, build 2de0643 + the uncommitted INCIDENT 2026-09 fix set) died with
`InvalidOperationException: cmdList.Close failed: 0x80070057`, thrown on the render thread from the **main** window's
`SubmitDrawList`. The pop-out's own frames are already survivable (AppHost.cs:1262-1285 logs `[detached] child frame
failed`, and no such line was logged); the main host rethrows by design.

Sequence from the log (session c61e82d2):

1. 22:36:42.8 — pop-out opens (gen 8/9, second monitor, 99.98 Hz vs primary 120 Hz).
2. 22:36:43.8 — a track switch closes it (gen 10).
3. 22:36:44.98 — a new pop-out opens (gen 11).
4. 26 ms later — the main frame's `Close` fails and the process terminates.

A Debug repro (session 1fb88cf0) ran six open/close cycles without failing, which fits a race rather than a
deterministic bad call.

**Slowdown.** With the pop-out open, a playlist scroll ran at **70 fps** (`missedVblanks=5`, `presented=273` for 140
frames). Slow frames report `fenceWait` 6.5-7.7 ms. Without the pop-out, the same pages hold 118-129 fps at <1 ms/frame.

## 2. Educated guess: why it crashed

The device is **one command list, three allocators, one fence**, shared by every swapchain (D3D12Device.cs:72-85,
1400-1411). A swapchain is made current by **copying its fields into device-level working fields**
(`Activate`, D:1673-1695) and copying them back out afterwards (`StoreActive`, D:1697-1715). The copied fields are
`_swapChain`, `_backBuffers[]`, `_rtvHeap`, `_w/_h`, `_frameIndex`, and the per-target stencil DSV.

Nothing locks this state. The render thread records with it, and the UI thread mutates it on two paths:

| Path | Where | Parked? |
|---|---|---|
| Child host constructor → `device.CreateSwapchain(...)` → `InitSwapChain` + **`Activate(target)`** | AppHost.cs:2741 → D:745-752 | **No.** Popups park the render thread around the very same call (AppHost.cs:4792-4815); the detached child does not. |
| Child `Dispose` → `_swapchain.Dispose()` → `DisposeSwapchain` (UI-thread `WaitForGpu`, `ReleaseSwapchainResources`, `ReleaseStencilDsv`, `_activeSwapchain = null`) | AppHost.cs:5957 → D:5698-5763 | **No.** The park at AppHost.cs:5884-5893 ends before this line. |

**Most likely (fits the 26 ms timing):** the new pop-out's `CreateSwapchain` ran `Activate(child)` **while the render
thread was mid-way through recording the main frame**. From that point the main list records against the child's
state:

- **Barriers and copies:** `acrylicTarget = _backBuffers[_frameIndex]` (D:4259 → AcrylicCompositor.cs:972-977) and
  the strip-fade barriers (D:4465/4476) target the **child's** back buffer, in the wrong resource state and at
  main-window coordinates.
- **Canvas freed mid-list:** `_w/_h` flip, so `_acrylic/_opacity.EnsureSize` (D:2067-2068) recreate their canvas at
  child size. The old canvas is released with **no fence** (AcrylicCompositor.cs:397-409), and later commands in the
  same list reference it.
- **Wrong DSV:** `_stencilDsv` flips to the child's, so `EnsureStencilDsv` builds a DSV at child size and binds it
  with the main RTV (D:3127-3203, 3248-3251).
- **Allocator/fence mismatch:** `_frameIndex` flips, so the allocator reset for this frame (D:1847) no longer matches
  the fence slot signalled at its end (D:2315).

Any of these produces an invalid recording, and the release runtime reports it as E_INVALIDARG at `Close`. It only
bites when the render thread is mid-record at the moment of creation; 120 Hz scrolling widens that window, which is
why the Debug repro (slower, less concurrent) survived.

**Also live (latent, from the close 1.2 s earlier):**

- **Frees the main DSV:** `ReleaseStencilDsv` takes its "active target" branch when `_activeSwapchain == child`
  (D:3233). A render-thread `Activate(main)` landing between that check and the release frees the **main** window's
  DSV (D:3209-3221).
- **Stale DSV kept on main:** `_activeSwapchain = null` (D:5710) turns the main frame's `StoreActive` into a no-op, so
  the main target keeps a freed DSV.
- **Fence race:** the UI-thread `WaitForGpu` does `++_fenceValue` non-atomically against the render thread's
  `SignalFrame` (D:5569-5594 vs D:5171-5176), and both share one auto-reset event. Fence values can be mis-ordered,
  image retirements (stamped `_fenceValue+1`, D:1859) can complete early, or one thread can steal the other's wake.

**Why the incident fix didn't close it.** It moved the stencil DSV into per-target *storage* but kept the copy-in /
copy-out model, and it covered only the child's own failing frame (DetachedRenderResilienceSuite.cs:12-13). The
unparked UI-thread mutation is untouched.

## 3. Why the pop-out slows the main window

One render thread runs both windows in series each turn (RenderThread.cs:81-156): main submit + present, then
`DrainChildRenderSources` → child submit + present. The main window then waits on the pop-out in three ways:

- **The pop-out's vblank.** The child's submit blocks in `WaitForLatency` on **its own** frame-latency waitable
  (`MAX_FRAME_LATENCY=1`), on a ~100 Hz monitor (D:1827-1829, 5204-5207). A 120 Hz wait and a 100 Hz wait in series
  give ≈13-14 ms per turn, about 70 fps.
- **The pop-out's GPU work.** The main frame's `WaitForFrame(k)` waits on whoever last used back-buffer index `k`,
  which is often the child's submit from the same turn (D:5178-5200). That becomes a full GPU drain.
- **Extra main-window GPU work.** Every child submit ages the main window's layer pools and rotates its blur-pin ring
  (D:2075-2076, 1898-1899), so the main window re-creates layers it would have kept.

The reported `fenceWait` is also mislabelled: it is the device's last-writer `LastFenceWaitMs` (A:4634, D:5237), usually
the child's latency wait.

## 4. How acclaimed engines do it (C:\WAVEE sources)

- **One shared device, per-window everything else.** The swapchain, depth/stencil, intermediates and size-keyed
  caches belong to the window:
  - gpui: `DirectXResources`, per window (`gpui_windows/src/directx_renderer.rs:70-84`).
  - egui-wgpu: per-viewport `depth_texture_view` / `msaa_texture_view` / `surfaces` (`crates/egui-wgpu/src/winit.rs:37-41`).
  - Chromium viz: a whole `Display` + `DirectRenderer` per widget (`components/viz/service/display/display.h:314-339`).
  - WebRender: a `Renderer` per window, sharing only shaders (`webrender/src/renderer/init.rs:292-296`).
  - bevy: keys its device-wide texture cache by the full descriptor, size included (`texture/texture_cache.rs:31-99`).
- **One encoder per window per frame** on the shared queue: egui (`winit.rs:547, 586-589`); gpui on Metal (a
  `command_queue` per `MetalRenderer`).
- **No window's wait throttles another:**
  - Windows Terminal: a render thread per window, each on its own waitable (`AtlasEngine.r.cpp:374-379, 436-449`).
  - makepad: **one** `MsgWaitForMultipleObjectsEx` over *all* windows' waitables, with per-window credits
    (`platform/src/os/windows/win32_app.rs:520-590, 793-833`; `d3d11.rs:625-725`).
  - Chromium: bounds each window's backlog independently (`display_scheduler.cc:810-832`).
  - The engines that block serially (egui, bevy) let the slowest window set the pace, which is exactly our 70 fps.
- **Teardown on the render thread, after the GPU is done with it:**
  - Flutter posts surface destruction to the raster thread (`flutter_windows_view.cc:95-109`).
  - Terminal/makepad `ClearState`+`Flush` and unregister the pacing handle before release
    (`AtlasEngine.r.cpp:390-410`; `d3d11.rs:1665-1685`).
  - egui frees only after submit (`winit.rs:729-738`).

## 5. The plan

### Phase 0 — close the race (small; ship first)

1. **Every device mutation runs on the render thread.** `CreateSwapchain`, `DisposeSwapchain`, `Resize` and
   `WaitForGpu` stop being callable from the UI thread. `AppHost` routes them through the owning render thread's
   existing rendezvous (`OwningRenderThread.Quiesce()/Resume()` is the minimum; a posted render-thread action is the
   target). Child construction then has the same shape as `OpenPopupWindow`:
   ```csharp
   // AppHost ctor, detached child
   var owner = parentRenderThread;              // the parent's thread this child will ride
   owner?.Quiesce();
   try { _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx, Composited: compositeSwapchain)); }
   finally { owner?.Resume(); }
   ```
   `Dispose` moves `_swapchain.Dispose()` **inside** its existing park (AppHost.cs:5884-5893).
2. **`CreateSwapchain` no longer activates.** Delete `Activate(target)` at D:752. Creation must not change which target
   the device is recording for; the first `SubmitDrawList` for the target activates it, as every other path already
   does.
3. **`ReleaseStencilDsv(target)` always releases the given target's DSV.** Drop the "`_activeSwapchain == target` ⇒
   release the working copy" branch (D:3233). `DisposeSwapchain` never writes `_activeSwapchain = null`; it only drops
   the target from `_swapchains`.
4. **One fence writer.** `_fenceValue` and the fence event are touched only on the render thread. The UI-thread
   `WaitForGpu` becomes a render-thread call under the park, so there is no second `++_fenceValue` and no shared
   auto-reset event.
5. **A thread-ownership tripwire.** `ThreadGuard.AssertRender()` at the top of `Activate`, `StoreActive`,
   `CreateSwapchain`, `DisposeSwapchain`, `Resize`, `SubmitDrawList` and `WaitForGpu`. It is compiled into DEBUG and
   FLUENTGPU_DIAG, so the next UI-thread mutation fails deterministically instead of racing.
6. **Release-build forensics.** Keep an always-on ring of the last 32 recorded op kinds (enum + target id + resource
   id, POD, zero-alloc) in `D3D12Device`. On a `Close` failure, `Diag.Line` it before rethrowing. A shipping crash
   then names the call without the debug layer. The existing `#if DEBUG` debug-layer drain stays.

### Phase 1 — per-target recording state (structural)

Replace copy-in/copy-out with an explicit per-target frame context, the gpui/egui/Chromium shape:

```csharp
/// Everything a frame records against, owned by ONE target. Never copied into device fields.
sealed class TargetFrameState
{
    public readonly ID3D12CommandAllocator*[] Allocators = new ID3D12CommandAllocator*[FrameCount];
    public readonly ulong[] FenceValues = new ulong[FrameCount];   // this target's own in-flight ledger
    public ID3D12GraphicsCommandList* List;                        // one list per target
    public uint FrameIndex, W, H;
    public StencilState Stencil;                                   // DSV + heap + depth/scope counters
    public LayerPools Layers;                                      // acrylic canvas, opacity pools, blur-pin ring
    public TimestampBank Timing;                                   // per-target query bank + _sceneCat
    public RepaintLedger Damage;                                   // partial-present ledger
}
```

- **One recording surface per frame.** `SubmitDrawList(…, target)` takes `target.Frame` and threads it through the
  recorder (the `_w/_h/_backBuffers/_stencil*` reads become `f.W`, `f.BackBuffers[f.FrameIndex]`, …).
- **Deletions:** `Activate`/`StoreActive` and the device working fields are **deleted** (no legacy paths).
- **Still shared:** the queue, one monotonic fence, pipelines/root signatures, descriptor heaps, the image store and
  the glyph atlas.
- **Waits:** `WaitForFrame` waits on **the target's own** `FenceValues[k]`, so the main window never drains the
  pop-out's GPU work.
- **Per-target state:** layer pools, `TickIdle` and the blur-pin ring become per target, so a child submit no longer
  ages the main window's layers.

### Phase 2 — pacing without cross-window blocking (the makepad shape)

- **One wait over every window.** The render thread waits once, with
  `WaitForMultipleObjectsEx([mainWaitable, child₁Waitable, …, wakeEvent])`, instead of blocking on each swapchain in
  series.
- **Present whoever is ready.** Each window keeps a per-window latency credit. A window whose waitable signalled
  submits and presents now; one that has not is skipped this turn, never waited on.
- **Pure scheduling decision.** The choice of which windows present this turn moves into an engine-free
  `WindowPacingScheduler`, so it is unit-testable.
- **Each window on its own display.** The child keeps its own `RefreshLattice` for its display (99.98 Hz); the main
  window keeps 120 Hz. `FrameStats.FenceWaitMs` / `LatencyWaitMs` become per target (no last-writer device field).

### Phase 3 — deferred destruction keyed by fence

A closed child's swapchain, DSV, layer pools, allocators and DComp presenter go onto a render-thread
`RetireQueue<(ulong fence, Action release)>`. They are released only once `fence.GetCompletedValue() ≥ stamp`. There
is no synchronous `WaitForGpu` on close, and the canvas release in `AcrylicCompositor.EnsureSize` goes through the same
queue. Keep the existing `SlotQuarantinePolicy`-style derivation (depth from `QuarantinePolicy.RenderInFlightDepth`).

## 6. Gates

Headless can't open a detached child (AppHost.cs:1685) and runs single-threaded, so gates split into two kinds:

- **Pure-policy gates (VerticalSlice `detached-render` suite):**
  - `WindowPacingScheduler`: a 100 Hz child never delays a 120 Hz main turn; each window presents at most once per
    credit; a starved window is re-probed.
  - `TargetFrameLedger`: a target waits only on its own fence slots; a closed target's retire stamps are released only
    after completion.
  - `RetireQueue` ordering.
  - The existing `StencilDsvPolicy` / `SlotQuarantinePolicy` checks.
- **Thread-ownership gate:** a headless test drives `CreateSwapchain` from a non-render thread and expects the DEBUG
  tripwire to fire.
- **Live stress gate** (`FluentGpu.WindowsApp`, a `--detached-stress` probe; not CI):
  - main window scrolling at full rate;
  - open/close a detached child 300× at random 50-1500 ms intervals, alternating monitors;
  - run with the D3D12 debug layer on and stderr captured to a file.
  - Acceptance: zero debug-layer errors, zero `Close` failures, and main-window `scroll.frames` fps ≥ 110 while a child
    presents on a different-refresh monitor.
- **Wavee live check:** reproduce the reported cycle (pop-out on DISPLAY2, track switch closes/reopens it) and read
  `scroll.frames` for the main window with the pop-out open.

## 7. Order and size

| Phase | Size | Fixes |
|---|---|---|
| 0 — render-thread ownership + forensics | ~1-2 days | the crash (race), the latent DSV/fence races |
| 1 — per-target recording state | ~4-6 days | cross-window state leaks; the main window no longer drains child GPU work |
| 2 — multi-object pacing | ~3-4 days | the 70 fps with a pop-out open |
| 3 — fence-keyed retire queue | ~1-2 days | synchronous waits on close, unfenced canvas release |

Phase 0 stands alone and should ship first; Phases 1-3 are one refactor of `D3D12Device`'s target model.
