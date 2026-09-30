# Detached windows: render isolation — implementation plan

Status: **execution-ready, awaiting the owner's go** (2026-09-22). Realizes
[`detached-window-render-isolation-plan.md`](detached-window-render-isolation-plan.md) (the approved-in-principle design,
phases 0–3). Every `file:line` below was re-verified against the **working tree** on 2026-09-22 (the uncommitted
INCIDENT 2026-09 fix set: per-target `StencilDsv` on `D3D12Swapchain`, `StencilDsvPolicy`, `SlotQuarantinePolicy`, the
`AppHost` detached-child failure latch, `DetachedRenderResilienceSuite`). `D:` = `src/FluentGpu.Windows/D3D12/D3D12Device.cs`,
`A:` = `src/FluentGpu.Engine/Hosting/AppHost.cs`, `RT:` = `src/FluentGpu.Engine/Hosting/Threading/RenderThread.cs`.

The plan is written for implementation subagents that have **no other context**: each phase names its files, gives the
code, and states the gate that proves it. Engine rules that bind every change (CLAUDE.md): 0 managed allocations in frame
phases 6–13; the render thread owns every `ComPtr`; the `FluentGpu.VerticalSlice` transitive closure stays TerraFX-free
(pure decision classes live in `FluentGpu.Engine`); **no source-text tests**; no environment-variable behaviour switches;
no legacy paths (delete, don't fork); design docs are canon (§8 lists the docs to reconcile + `check-canon.ps1`).

---

## 1. Verified root cause

### 1.1 The device model as built

One `ID3D12Device`, one direct queue, **one** `ID3D12GraphicsCommandList` (`_cmdList`, D:82, created D:1399-1404),
`FRAME_COUNT = 3` allocators (`_allocators`, D:80, D:1390-1397), **one** fence + one value + one auto-reset event
(`_fence/_fenceValue/_fenceEvent`, D:83-85, D:1406-1411), one `_frameFenceValues[3]` ledger (D:81). Every swapchain
(main window, each popup, each detached child) shares all of it.

Per-target state is stored on `D3D12Swapchain` (D:5895-5932: `SwapChain`, `RtvHeap`, `BackBuffers[3]`,
`FrameLatencyWaitable`, `LatencyCreditHeld`, `W/H/FrameIndex`, DComp visuals, `StencilDsv/StencilDsvHeap/StencilDsvW/H`)
and is **copied into device working fields** by `Activate` (D:1673-1695: `_activeSwapchain, _hwnd, _w, _h, _frameIndex,
_swapChain, _rtvHeap, _frameLatencyWaitable, _hasLatencyWaitable, _swapChainFlags, _tearingSupported, _backBuffers[],
_stencilDsv, _dsvHeap, _stencilW, _stencilH`) and copied back by `StoreActive` (D:1697-1715). Nothing locks this.
`Activate` runs at the top of `SubmitDrawList(target)` (D:1802), `Present` (D:4781), `CaptureBgra` (D:5604), `Resize`
(D:5672 and D:5693), `RecoverDevice` (D:5835) and — the one that matters — at the end of `CreateSwapchain` (D:752).

The recorder reads the working fields throughout the frame: `_backBuffers[_frameIndex]` (D:1872, 4259, 4440, 4465, 4476),
`_rtvHeap` (D:1875), `_w/_h` (35 sites; e.g. D:1913, 1949, 2067-2068, 2907-2908, 2947-2948, 3114-3115),
`_stencilDsv/_dsvHeap/_stencilW/_stencilH` (D:3130-3132, 3184-3201, 3250, 3288), `_frameIndex` (51 sites).

### 1.2 Interleaving A — the reported crash (unparked `CreateSwapchain` mid-record)

UI thread, `OpenDetachedWindow` (A:1680-1765) → `new AppHost(... parentRenderThread: _renderThread)` (A:1754-1755) →
ctor → `device.CreateSwapchain(...)` (A:2741) **with the parent's render thread running**. Only *after* the ctor returns
does `AttachChildRenderSource` park the loop (A:1760 → A:1850-1855, `rt.Quiesce()`). `OpenPopupWindow` parks around the
identical call (A:4792-4815); the child ctor does not.

`CreateSwapchain` (D:724-755) on the UI thread does, in order: `InitSwapChain(target)` (D:749 → D:1417-1506) whose first
statement is `ResetRepaintLedger()` (D:1421 → D:456-464: `_canvasValid = false`, `_lastConsumedSequence = 0`, …) — this
wipes the **main window's** §13.1 canvas ledger while the main frame may be using it; then `_swapchains.Add` (D:750);
then **`Activate(target)`** (D:752).

If the render thread is anywhere inside `SubmitDrawList(main)` between D:1802 and D:2334 when `Activate(child)` lands,
the rest of the main recording runs against the child's copy:

| Working field after `Activate(child)` | Main-frame reader | Effect |
|---|---|---|
| `_stencilDsv = null`, `_dsvHeap = null`, `_stencilW/H = 0` (child has no DSV yet) | `RebindCurrentTarget` → `EnsureStencilDsv(TargetW, TargetH)` (D:3248) on the frame's first `PushStencilClip` | `TargetW/H` = `_targetWidth/_targetHeight` when a viewport is set (D:3114-3115, set from `_w/_h` in `SetFullViewport` D:2907-2908 — already child-sized if the viewport was set after the swap, else main-sized). A DSV is created at the **child's** size (D:3152-3201) and bound with the **main** RTV: `OMSetRenderTargets(1, &rtv, FALSE, &dsv)` (D:3251). |
| `_w/_h` = child size | `_acrylic.EnsureSize(_w,_h)` (D:2067) / `_opacity.EnsureSize` (D:2068), `SetFullViewport` (D:2905-2911), `SetScissorRect` clamps (D:2947-2948), `InflatedRootDamage` (D:427-428), `ReplayCoversRegion` (D:414) | Canvas released **unfenced** and recreated at child size (`AcrylicCompositor.EnsureSize`, AcrylicCompositor.cs:397-408, release at :402) while this list still references the old canvas; viewport/scissor shrink to 480×270. |
| `_backBuffers[]`, `_frameIndex` = child's | strip-fade barriers `Barrier(_backBuffers[_frameIndex], …)` (D:4465, 4476), `acrylicTarget` (D:4259), `StripTargetResource` (D:4440), `SignalFrame(_frameIndex)` (D:2315) | Barriers on the child's back buffer in the wrong state; the fence value is stamped into the child's slot so the main's real slot keeps a stale (lower) value → a later `WaitForFrame` early-outs and an allocator is reset under in-flight GPU work. |
| `_activeSwapchain = child` | `StoreActive()` at D:2334 | The main frame's end-of-frame state (including any DSV it created) is written **into the child object**; the main target keeps stale fields. |

**Which recorded call the runtime rejects at `Close`.** The D3D12 core runtime (no debug layer) performs a small set of
deferred validations during recording and reports them as `E_INVALIDARG` from `ID3D12GraphicsCommandList::Close`
(the debug layer only adds the *message*). The classes reachable from the table above, in order of likelihood:

1. **`OMSetRenderTargets` with an RTV and a DSV of different dimensions** (D:3251) — the exact class the first
   incident already proved empirically in this codebase (A:1264-1268: "a stencil-DSV size mismatch closing the shared
   cmdList with E_INVALIDARG did exactly this"; `DetachedRenderResilienceSuite.cs:12-13`). This needs only a
   `PushStencilClip` in the main stream after the swap, which Wavee's rounded/path clips supply on most pages.
2. A transition barrier whose `pResource` is null (`_backBuffers[i]` nulled by a concurrent `Resize` D:5684 or
   `ReleaseSwapchainResources` D:5744-5751 while `_activeSwapchain == that target`) at D:4465/4476/1873/2255.
3. `ResolveQueryData` / `EndQuery` index drift from the `_frameIndex` flip (D:2261-2264, 2276-2278) — valid ranges,
   so **not** an `E_INVALIDARG` source; listed to close it out.

The 26 ms gap in the log (creation → main `Close` failure) is one main frame at 120 Hz plus the UI-side creation time;
the Debug repro survived six cycles because the Debug build records slower and the window is narrower, not because the
race is absent. The always-on forensic ring (§2.6) turns "most likely (1)" into a named call on the next field crash.

### 1.3 Interleaving B — the close 1.2 s earlier (unparked `DisposeSwapchain`)

Child `AppHost.Dispose` (A:5875-5960): `owner?.Quiesce()` (A:5883-5884) … `owner?.Resume()` (A:5893) — and only
**then** `_swapchain.Dispose()` (A:5957 → `D3D12Swapchain.Dispose` D:6149 → `DisposeSwapchain` D:5698-5711). So on the
UI thread, unparked: `WaitForGpu()` (D:5701) — a second `++_fenceValue` writer (D:5571) against the render thread's
`SignalFrame` (D:5173) plus a shared auto-reset `_fenceEvent` (D:5581/5586 vs D:5189/5191: one thread can consume the
other's wake → an `INFINITE` wait on the render thread); `ReleaseSwapchainResources(child)` → `ReleaseStencilDsv(child)`
(D:5739 → D:3231-3242) which, because every turn ends with the child's `Present` → `StoreActive` leaving
`_activeSwapchain == child`, takes the **no-arg branch** (D:3233) and frees whatever the *working copy* holds at that
instant — the **main window's DSV** if `Activate(main)` (D:1802) ran between the check and the release — and also clears
the live frame's scope stack (`_stencilDepth = 0; _stencilScopeMasked.Clear()`, D:3219-3221) mid-record; finally
`_activeSwapchain = null` (D:5710), which turns the concurrently-recording main frame's `StoreActive` (D:2334) into a
no-op (D:1699), leaking the DSV it created that frame. None of these is the observed exception; all are in the same
26 ms class and go away with the same fix.

### 1.4 Latent, deterministic: `Resize` leaves `target.StencilDsv` dangling (NEW — not in the design plan)

`Resize` (D:5668-5694) runs `Activate(target)` (D:5672) → `ReleaseStencilDsv()` no-arg (D:5675) — which releases the
**working copy** and nulls only the device fields (D:3211-3218), never `target.StencilDsv*` — then, with **no**
`StoreActive`, `Activate(target)` again (D:5693), which reloads `_stencilDsv = target.StencilDsv` (D:1691): the pointer
that was just released. The next `SubmitDrawList` then either (grow) double-releases it at D:1813-1814, or (shrink)
`StencilDsvPolicy.Covers` (D:3132) keeps it and `RebindCurrentTarget` binds a freed DSV through a freed heap
(`_dsvHeap->GetCPUDescriptorHandleForHeapStart()`, D:3250). Reachable on any window that has rendered a stencil scope
and is then resized. Fixed by Phase 0 §2.3.3 (explicit-target release clears both the target and the working copy) and
removed structurally by Phase 1.

### 1.5 Every device entry point and its thread today

| Entry point | Caller(s) | Thread today | Exclusive? | Verdict |
|---|---|---|---|---|
| `CreateSwapchain` D:724 | primary: `AppHost` ctor A:2741 (before the render thread exists, A:3073) | UI | yes (no render thread yet) | OK |
| | **detached child**: same ctor line, A:1754 | UI, **unparked** | **no** | **BUG (§1.2)** |
| | popup: `OpenPopupWindow` A:4807 | UI, parked A:4793/4815 | yes | OK |
| `DisposeSwapchain` D:5698 | **child** `Dispose` A:5957 (reap path A:1792) | UI, **after** the park ended A:5893 | **no** | **BUG (§1.3)** |
| | popup `ClosePopupWindow` A:4896 | UI, parked A:4884/4904 | yes | OK |
| | parent `Dispose` A:5957, `D3D12Device.Dispose` D:5843-5851 | UI, render thread joined A:5881 | yes | OK |
| `Resize` D:5668 | `AppHost` WndProc resize A:5852-5857 (main + child) | UI, parked | yes | OK (but §1.4) |
| | popups: `DrainPopupRenderActions` A:1061-1080 | render | yes | OK |
| | single-thread: A:5861, A:4853 | UI (sole owner) | yes | OK |
| `WaitForGpu` D:5569 | `Resize` D:5673, `CaptureBgra` D:5605/5649, `Dispose` D:5842 | UI parked / joined | yes | OK |
| | `DisposeSwapchain` D:5701 (child) | UI unparked | **no** | **BUG** |
| | `PrepareGlyphs` growth fence D:1731, stencil recreate D:1813, `CreateEngineTestSurfaceHandle` D:1644/1648 | render | yes | OK |
| | `MemoryProbeDrain` (D3D12Device.MemoryProbe.cs:61-66) | the probe's owner thread; standalone device (`AssertMemoryProbeOwner` :22-26 refuses async) | yes | OK |
| `CaptureBgra` D:5601 | `FluentApp` `--screenshot` (FluentApp.cs:741-742, after `QuiesceRenderThread` = thread **disposed**, A:996) ; `RunWithRenderThreadParked` (A:1006-1015; DialogScrollProbe.cs:128, RepaintIdentityProbe.cs:306); standalone probes (SmallTextureProbe :239, SmallImagePoolProbe :143, GlyphCapacityProbe :87, VideoM0.cs:102) | UI, joined/parked/standalone | yes | OK |
| `Present` D:4777 | `SubmitPresentOnRenderThread` A:1251, `RecordPopups` SceneRenderFrame.cs:118; inline A:4486, A:4943 (no render thread) | render / UI-sole | yes | OK (`AssertSubmitThread`) |
| `SubmitDrawList` D:1797 | A:1220, A:1246, SceneRenderFrame.cs:116; inline A:4483, A:4941 | render / UI-sole | yes | OK |
| `WaitForPresentSlot` D:5221 | RT:134 | render | yes | OK |
| `GetVideoPresenter` D:527 | `DrainVideoForPresentTurn` A:1301; inline A:4532 only when no render thread | render / UI-sole | yes | OK (`AssertSubmitThread`) |
| `DrainImageJobs` D:963, `ReclaimCompletedUploads` D:5538 | A:1115, A:1147, A:1208 | render | yes | OK |
| `UploadImage/TryUploadImage/EvictImage` D:2339-2346 | via `ImageUploadQueue` (async) / UI-staged (sync) | render / UI | documented split | OK |
| `RecoverDevice` D:5770 | `RecoverDeviceAfterDump` A:1314 (RT:108 `_recover`); foreground A:1352 (inline path only) | render / UI-sole | yes | OK |
| `EnsureDeviceCreated` D:708 | app boot, before any render thread | UI | yes | OK |
| `NoteIfDeviceLost`/`PollDeviceLost`/`InjectDeviceLost`/`DumpDeviceLostDiagnostics`/`TryGetVramUsage` | UI + render | free-threaded device calls or cached fields | n/a | OK |
| `SuppressLatencyWaitOnce`/`SuppressVsyncOnce`/`HintSettlePresent` D:5547-5554 | `ApplyPresentPacing` A:1021-1025 (render), A:4412 (UI), A:4481-4482 (UI-sole) | plain bools | n/a | OK today; **device-level, so a child's interactive present steals the main's vsync** → per-target in Phase 1 |
| `BindDComp` D:1521, `EnsureDComp` D:1508, `CreateEngineTestSurfaceHandle` D:1560, `VideoPresenter.OnSwapchainRebound` D:1547 | render (`AssertSubmitThread`) | render | yes | OK |
| `MemoryProbe*` (partial), `ProbeSmallTexturePlacement`, `GlyphCapacityProbeState` | standalone single-thread devices in `FluentGpu.WindowsApp/Probes` | probe owner | yes | OK |
| `OnSwapchainRebound` (DCompVideoPresenter.cs:187) | `BindDComp` only | render | yes | OK |

So exactly **two** paths are wrong, both on the detached child: create (A:2741) and dispose (A:5957).

### 1.6 The slowdown (confirmed)

One render thread, in series per turn: `WaitForPresentSlot` on the **primary** waitable (RT:134 → D:5221-5229),
`TryAcquire` + main submit + present (RT:138-144), then `_extraDrain` = `DrainChildRenderSources` (RT:149 → A:1870-1888)
→ child `SubmitPresentOnRenderThread` → `SubmitDrawList(child)` which, because `creditHeld` is primary-only (D:1827),
**blocks in `WaitForLatency` on the child's own waitable** (D:1829 → D:5204-5207, `MAX_FRAME_LATENCY = 1` D:56) — a
~100 Hz wait after a 120 Hz wait ⇒ ≈70 fps. Second coupling: `WaitForFrame(_frameIndex)` (D:1837 → D:5178-5200) waits on
`_frameFenceValues[k]`, stamped by **whoever** last used back-buffer index `k` (D:5175); with both windows presenting
every turn their indices advance in lockstep, so the main's wait is on the child's previous-turn submit — i.e. the
whole previous turn's GPU work. Third: every child submit takes the no-layer branch and runs `_acrylic/_opacity.TickIdle`
(D:2075-2076) and rotates the blur-pin recurrence ring (D:1898-1899) — main-window state. Fourth: `FrameStats.FenceWaitMs`
(A:4634) reads `_device.LastFenceWaitMs` (D:5237-5240), a last-writer field, so the reported 6.5–7.7 ms is usually the
child's latency wait.

### 1.7 What contradicted or extends the design plan

1. **`ThreadGuard.AssertRender()` cannot be placed on `CreateSwapchain/Resize/DisposeSwapchain/WaitForGpu`** as
   written (design §5.5): every legitimate mutation today runs on the *UI thread holding the loop parked* (popups
   A:4793, WndProc resize A:5854, `RunWithRenderThreadParked` A:1011, the screenshot path after the join). The tripwire
   must assert *render ownership* (render thread, or UI thread inside `Quiesce…Resume` / after the join) — §2.1.
2. **Per-target allocators alone do not decouple the main window from the child's GPU work** (design §Phase 1): the
   CPU-written banks — `UploadArena` (D:104, UploadArena.cs:37-39), every pipe's instance bank (`BeginFrame(frameIndex)`
   D:1905-1912), the glyph instance bank (GlyphRenderer.cs:287), the timestamp query banks (D:5258-5260, 5271-5272) — are
   **device-level** and keyed by the same back-buffer index. They need a device-level, fence-keyed **`SubmissionRing`**
   (§3.2); the per-target ledger keys only the target's own back buffers/RTVs. Allocators belong to the ring, not the target.
3. **`Resize` dangles the DSV** (§1.4) — new, deterministic, fixed in Phase 0.
4. **`InitSwapChain` resets the primary's canvas ledger** for *every* target (D:1421) — a popup or child opening forces
   a full repaint of the main window and zeroes `_lastConsumedSequence`. Fixed in Phase 0 (§2.3.2), structurally per-target in Phase 1.
5. `Suppress*Once/HintSettlePresent` are device-level (§1.5) — per-target in Phase 1.
6. `DisposeSwapchain` "never writes `_activeSwapchain = null`" (design §Phase 0 item 3) is moot once the call is parked;
   Phase 0 clears the working ComPtr copies as well (§2.3.4). Phase 1 deletes the working copies.
7. Wavee already whitelists `[detached]` and `[d3d12.debug]` (Platform.Host.cs:881-883); the **new** forensic prefix
   `[d3d12.forensic]` needs one line (§2.5).
8. The forensic ring is 64 entries (power-of-two mask), state-changing ops only (§2.6).

---

## 2. Phase 0 — close the race (ships alone)

### 2.0 Decision: `Quiesce/Resume` for create, park for dispose; posting comes in Phase 3

`RenderThread.Quiesce` (RT:187-194) sets `_resizeQuiesce`, wakes the loop, and blocks on `_resizeIdle` until the loop
reaches its top-of-turn gate (RT:118-127) — i.e. after the current main submit+present **and** `_extraDrain` complete
— then the loop blocks on `_resumeResize`. That is full mutual exclusion over the whole device for the caller, with a
memory barrier both ways (the `AutoResetEvent` pair). It is the primitive popups already use for the identical call
(A:4792-4815), and DXGI/D3D12 objects are free-threaded, so *which* thread creates the swapchain is irrelevant; only
exclusivity matters. `CreateSwapchain` returns a value the ctor needs synchronously, so a posted action would need a
completion handshake that is `Quiesce` by another name. Teardown needs no result and moves to a posted, fence-keyed
retire in Phase 3 (§5.4); for Phase 0 it goes **inside** the park the child's `Dispose` already takes.

`Quiesce` is not re-entrant (a nested call would block forever on `_resizeIdle` while the loop is parked on
`_resumeResize`) — every code block below is written so parks never nest.

### 2.1 `ThreadGuard`: render-ownership token — `src/FluentGpu.Engine/Hosting/Threading/ThreadGuard.cs`

Add after line 37 (`AssertWorkerOrRender`):

```csharp
    // Render OWNERSHIP (not identity): device-mutating calls (swapchain create/resize/dispose, WaitForGpu, capture) are
    // legal on the render thread OR on the UI thread while it holds the render loop parked (RenderThread.Quiesce …
    // Resume) or after it joined the loop (RenderThread.Dispose). Both are "the sole toucher of every ComPtr right
    // now"; SubmitDrawList/Present keep the strict AssertRender. Depth-counted so a park after a join is legal; a
    // nested park is NOT (RenderThread.Quiesce is not re-entrant) and the depth check throws before it can deadlock.
    [ThreadStatic] private static int t_renderOwnerDepth;
    [ThreadStatic] private static bool t_renderOwnerForever;   // set by RenderThread.Dispose on the joining thread

    [Conditional("FGGUARD")]
    public static void EnterRenderOwnership()
    {
        if (t_role != ThreadRole.Ui) ThrowWrongThread(ThreadRole.Ui);
        if (t_renderOwnerDepth != 0 && !t_renderOwnerForever)
            throw new InvalidOperationException("Render ownership is not re-entrant: a nested RenderThread.Quiesce would deadlock.");
        t_renderOwnerDepth++;
    }

    [Conditional("FGGUARD")]
    public static void ExitRenderOwnership()
    {
        if (t_renderOwnerDepth <= 0) throw new InvalidOperationException("ExitRenderOwnership without a matching Enter.");
        t_renderOwnerDepth--;
    }

    /// <summary>The render loop was joined by the current (UI) thread: it owns the device from now on.</summary>
    [Conditional("FGGUARD")]
    public static void AdoptRenderOwnership() { if (t_role == ThreadRole.Ui) t_renderOwnerForever = true; }

    [Conditional("FGGUARD")]
    public static void AssertRenderOwner()
    {
        if (t_role == ThreadRole.Render) return;
        if (t_role == ThreadRole.Ui && (t_renderOwnerForever || t_renderOwnerDepth > 0)) return;
        ThrowWrongThread(ThreadRole.Render);
    }
```

Compile gating: the whole class is `[Conditional("FGGUARD")]` — FGGUARD is defined in **Debug** only
(`src/Directory.Build.props:50-52`), *not* by `FluentGpuDiag` (:58 says so explicitly). Release erases every call and the
`[ThreadStatic]` fields with them. The design plan's "DEBUG and FLUENTGPU_DIAG" is therefore "Debug (FGGUARD)"; the
Release-build proof is the forensic ring (§2.6), not the tripwire.

### 2.2 `RenderThread`: the park hands ownership to the UI — `RT`

```csharp
    public void Quiesce()
    {
        ThreadGuard.AssertUi();
        ThreadGuard.EnterRenderOwnership();   // BEFORE the disposed early-out: a joined loop leaves the UI the owner anyway
        if (_disposed) return;
        Volatile.Write(ref _resizeQuiesce, 1);
        _wake.Set();
        _resizeIdle.WaitOne();
    }

    public void Resume()
    {
        ThreadGuard.AssertUi();
        ThreadGuard.ExitRenderOwnership();
        if (_disposed) return;
        _resumeResize.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _wake.Set();
        _resumeResize.Set();
        _thread.Join();
        ThreadGuard.AdoptRenderOwnership();   // the joining thread is the sole GPU-ComPtr owner from here on (CaptureBgra, Dispose)
        _displayClock?.Dispose();
        _wake.Dispose(); _done.Dispose(); _resizeIdle.Dispose(); _resumeResize.Dispose();
    }
```

`RenderThreadLifecycleTests` (Engine.Tests, :52-56) calls `Quiesce(); Resume();` after `Dispose()` — still legal (Enter
after Adopt is allowed by the `!t_renderOwnerForever` clause).

### 2.3 `D3D12Device` — `D:`

#### 2.3.1 The owner assert (next to `AssertSubmitThread`, D:917-925)

```csharp
    // Device MUTATION tripwire (INCIDENT 2026-09): swapchain create/resize/dispose, WaitForGpu and the capture path
    // are legal on the render thread or on the UI thread that holds the loop parked / joined — never on an unparked
    // UI thread racing the recorder. Same arming as AssertSubmitThread (inert until MarkRenderConfined), same erasure.
    [System.Diagnostics.Conditional("FGGUARD")]
    private void AssertDeviceOwner() { if (_renderConfined) FluentGpu.Hosting.Threading.ThreadGuard.AssertRenderOwner(); }
```

Placement (first statement of each): `CreateSwapchain` (D:724), `Activate` (D:1673), `StoreActive` (D:1697),
`ReleaseStencilDsv(target)` (D:3231), `WaitForGpu` (D:5569), `CaptureBgra` (D:5601), `Resize` (D:5668),
`DisposeSwapchain` (D:5698), `Dispose` (D:5840). `SubmitDrawList`/`Present`/`WaitForPresentSlot`/`DrainImageJobs`/
`GetVideoPresenter`/`RecoverDevice`/`BindDComp` keep `AssertSubmitThread` (strict render).

#### 2.3.2 `CreateSwapchain` no longer activates; `InitSwapChain` no longer wipes the primary's ledger

```csharp
    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        AssertDeviceOwner();
        long bootT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        … (D:727-744 unchanged) …
        bool composited = desc.Composited || (_primarySwapchain is null && _composited);
        var target = new D3D12Swapchain(this, (HWND)desc.PresentTarget.Value,
            (uint)Math.Max(1, (int)desc.SizePx.Width), (uint)Math.Max(1, (int)desc.SizePx.Height), composited,
            desc.DesktopAcrylic, desc.AcrylicTint, desc.CornerRadiusPx, ordinal: (byte)Math.Min(255, ++_swapchainOrdinalSeq));
        InitSwapChain(target);
        _swapchains.Add(target);
        _primarySwapchain ??= target;
        // NO Activate(target) here (INCIDENT 2026-09 §1.2). Creation must not change which target the device is
        // recording for; every consumer of the working fields — SubmitDrawList, Present, Resize, CaptureBgra —
        // Activates its own target first. Verified: nothing reads _w/_h/_backBuffers/_rtvHeap/_stencil* between a
        // CreateSwapchain and the next Activate (SizePx reads _primarySwapchain.SizePx, D:5696).
        _memoryProbeObserver?.Invoke("swapchain-created");
        return target;
    }
    private int _swapchainOrdinalSeq;   // forensic ring target id (§2.6); 0 = none/unknown
```

`InitSwapChain` (D:1421): replace `ResetRepaintLedger();` with

```csharp
        // The §13.1 canvas ledger belongs to the PRIMARY target (popups/children never route through the canvas —
        // D:1915-1936). Resetting it for a secondary swapchain forced a full main-window repaint per flyout/pop-out.
        if (_primarySwapchain is null || ReferenceEquals(target, _primarySwapchain)) ResetRepaintLedger();
```

(`RecoverDevice` calls `InitSwapChain` for every target after its own `ResetRepaintLedger()` at D:5775 — unchanged.)

`D3D12Swapchain` ctor (D:5959) gains `byte ordinal = 0` → `internal readonly byte Ordinal;`.

#### 2.3.3 Explicit-target `ReleaseStencilDsv` (replaces both overloads, D:3205-3242)

```csharp
    // Releases THIS target's DSV + heap and resets its scope state. If the working copy currently mirrors this target
    // (Activate ran for it and no other target has been activated since), the copy is cleared too — the no-arg
    // overload used to release the COPY only and leave target.StencilDsv dangling (Resize D:5675 → D:5693 reload:
    // INCIDENT 2026-09 §1.4). Never call mid-scope (see EnsureStencilDsv's comment); callers: SubmitDrawList's eager
    // stale-size check, Resize, ReleaseSwapchainResources.
    private void ReleaseStencilDsv(D3D12Swapchain target)
    {
        AssertDeviceOwner();
        if (target.StencilDsv != null)
        {
            D3D12MemoryDiagnostics.Release(target.StencilDsv, "StencilClip.Dsv");
            target.StencilDsv->Release();
            target.StencilDsv = null;
        }
        if (target.StencilDsvHeap != null) { target.StencilDsvHeap->Release(); target.StencilDsvHeap = null; }
        target.StencilDsvW = 0; target.StencilDsvH = 0;
        if (ReferenceEquals(target, _activeSwapchain))
        {
            _stencilDsv = null; _dsvHeap = null; _stencilW = 0; _stencilH = 0;
            _stencilDepth = 0; _stencilScopeMasked.Clear(); _stencilDsvBound = false;
        }
    }
```

Call sites: D:1814 `ReleaseStencilDsv();` → `ReleaseStencilDsv(sc);` (the working copy equals `sc`'s at that point, so
the DSV the guard at D:1811 inspected is the one released); D:5675 → `ReleaseStencilDsv(target);`; D:5739 unchanged.
`RecoverDevice` (D:5786-5788) and `Dispose` (D:5875-5877) already rely on the per-target loop — unchanged.

#### 2.3.4 `DisposeSwapchain` / `Resize` / `WaitForGpu`

```csharp
    internal void DisposeSwapchain(D3D12Swapchain target)
    {
        AssertDeviceOwner();   // child teardown now runs under the parent loop's park (AppHost.Dispose, §2.4)
        if (target.Disposed) return;
        if (_device != null) WaitForGpu();
        ReleaseSwapchainResources(target);
        target.VideoPresenter?.Dispose();
        target.VideoPresenter = null;
        _swapchains.Remove(target);
        if (_primarySwapchain == target) _primarySwapchain = null;
        if (ReferenceEquals(_activeSwapchain, target))
        {
            // The working copy mirrors a target whose ComPtrs were just released: drop the copies so no dangling
            // pointer can be read before the next Activate (every consumer Activates first — §2.3.2).
            _activeSwapchain = null;
            _swapChain = null; _rtvHeap = null;
            for (uint i = 0; i < FRAME_COUNT; i++) _backBuffers[i] = null;
            _frameLatencyWaitable = HANDLE.NULL; _hasLatencyWaitable = false;
        }
    }
```

`Resize` (D:5668): add `AssertDeviceOwner();` first; D:5675 becomes `ReleaseStencilDsv(target);` (fixes §1.4). Delete
the write `_backBuffers[i] = null;` at D:5684 is fine to keep (the copy is reloaded at D:5693).

`WaitForGpu` (D:5569): add `AssertDeviceOwner();` first. With every caller in §1.5 now render-owned, `_fenceValue` and
`_fenceEvent` have **one toucher at a time**; the Phase 1 ledger keeps it that way by construction.

Delete the write-only field `_hwnd` (D:93, D:1676) — `Present` reads `target.Hwnd` (D:4786).

#### 2.3.5 `StoreActive` guard

```csharp
    private void StoreActive()
    {
        AssertDeviceOwner();
        if (_activeSwapchain is not { Disposed: false } target) return;
        … (D:1700-1714 unchanged)
    }
```

### 2.4 `AppHost` — `A:`

**Ctor (A:2737-2741):**

```csharp
        // A detached child window must be COMPOSITED … (existing comment) …
        // INCIDENT 2026-09 (docs/plans/detached-window-render-isolation-implementation.md §1.2): a detached child's
        // swapchain used to be created while the PARENT's render thread could be mid-record on the shared device
        // (CreateSwapchain → InitSwapChain → ResetRepaintLedger + Activate). It now runs under the same park
        // OpenPopupWindow uses for the identical call. A null owner means no render thread exists yet (the primary
        // host's own ctor, or a child under a single-thread parent): the UI thread is the sole device owner.
        parentRenderThread?.Quiesce();
        try
        {
            _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx, Composited: compositeSwapchain));
        }
        finally { parentRenderThread?.Resume(); }
```

`OpenDetachedWindow` calls the ctor (A:1754) and then `AttachChildRenderSource` (A:1760) — two *sequential* parks, never
nested.

**`Dispose` (A:5875-5960)** — the swapchain disposals move **inside** the park; children are disposed **before** the
parent's park (each child parks the parent's loop itself, so they must not run inside it):

```csharp
    public void Dispose()
    {
        lock (_coldMaintenanceWakeGate) { _coldMaintenanceStopped = true; _pixelPool.BufferRetained -= OnPixelBufferRetained; }
        _renderThread?.Dispose();   // Step 4: stop + join the fgpu-render thread before tearing down the device it submits to
        // Detached child windows FIRST and OUTSIDE our own park: each child's Dispose parks OUR render thread itself
        // (its OwningRenderThread is _parentRenderThread), and Quiesce is not re-entrant. Our thread is already joined
        // here (above), so those parks are no-ops; on a child host this list is empty.
        for (int i = _detachedHosts.Count - 1; i >= 0; i--) _detachedHosts[i].Dispose();
        _detachedHosts.Clear();
        var owner = OwningRenderThread;
        owner?.Quiesce();
        try
        {
            _renderSeam.InvalidateTarget();
            PurgePopupRenderActions(null);   // the loop is parked (or gone): no drain can be in flight
            _imageQueue?.RemoveSceneReader(this);
            _renderSeam.ReleaseSceneResources();
            _scene.Recording.ReleaseInlineResources();
            for (int i = _popupWindows.Count - 1; i >= 0; i--)
            {
                _popupWindows[i].Swapchain?.Dispose();
                _popupWindows[i].Window.Dispose();
            }
            _popupWindows.Clear();
            // INSIDE the park (INCIDENT 2026-09 §1.3): for a detached child, `owner` is the parent's LIVE render thread
            // and DisposeSwapchain's WaitForGpu/ReleaseStencilDsv must not race its recorder.
            _swapchain.Dispose();
        }
        finally { owner?.Resume(); }
        … (A:5894-5945 — the HostDispatch / activation / SIP / InputHooks / dyn-text unsubscribe block, unchanged, now after the park) …
        if (!_isDetachedChild) _device.Dispose();   // a child shares the parent's device and must not dispose it
        _window.Dispose();
    }
```

`TickDetachedHosts` (A:1769-1798) needs no change: `DetachChildRenderSource(child)` (park/resume) then `child.Dispose()`
(park/resume) — sequential.

### 2.5 Wavee — the one-line app change

`C:\wavee\WaveeMusic\src\apps\Wavee\Platform\Platform.Host.cs`, in `GpuForensic` (:887-908), add one clause:

```csharp
        || s.StartsWith("[d3d12.forensic]", StringComparison.Ordinal)
```

(next to `[d3d12.stall]` at :904). Without it the line routes `Debug` (:850-853) and never reaches `wavee-*.log`. The
`[detached]` and `[d3d12.debug]` prefixes are already `AlwaysOn` (:881-883). The Wavee CHANGELOG bullet for the fix ends
with the issue ref per the app repo's rule.

### 2.6 Always-on forensic ring (Release-build evidence)

**Pure ring (Engine, TerraFX-free, gated headlessly):** `src/FluentGpu.Engine/Rhi/RecordedOpRing.cs`

```csharp
namespace FluentGpu.Rhi;

/// <summary>Which command-list call was recorded; only the STATE-changing classes that the D3D12 core runtime validates
/// at Close (barriers, render-target binding, clears, viewport/scissor, query resolves, list reset/close). Draw runs
/// are self-evidently well-formed and are not logged per run.</summary>
public enum RecordedOp : byte
{
    None = 0, ListReset, Barrier, SetRenderTarget, SetRenderTargetWithDsv, ClearRtv, ClearDsv, Viewport, Scissor,
    StencilDsvCreated, StencilRef, EndQuery, ResolveQuery, CopyTexture, LayerAcquire, LayerRelease, CanvasResized, ListClose,
}

/// <summary>POD, fixed-capacity ring of the last <see cref="Capacity"/> recorded ops. Push is zero-alloc (a struct
/// write); Format allocates and runs only on a Close failure. Lives in Engine so it is unit-testable headlessly;
/// D3D12Device owns one and writes it from its recording chokepoints.</summary>
public sealed class RecordedOpRing
{
    public const int Capacity = 64;   // power of two: the head wraps with a mask, never a modulo
    public struct Entry { public RecordedOp Op; public byte Target; public ushort Aux; public uint A; public uint B; }   // 12 B
    private readonly Entry[] _ring = new Entry[Capacity];
    private int _head;
    private ulong _total;

    public ulong Total => _total;
    public int Count => (int)Math.Min(_total, (ulong)Capacity);

    public void Push(RecordedOp op, byte target, uint a = 0, uint b = 0, ushort aux = 0)
    {
        ref Entry e = ref _ring[_head];
        e.Op = op; e.Target = target; e.Aux = aux; e.A = a; e.B = b;
        _head = (_head + 1) & (Capacity - 1);
        _total++;
    }

    /// <summary>Oldest → newest. Index 0 is the oldest retained entry.</summary>
    public Entry At(int i)
    {
        int n = Count;
        if ((uint)i >= (uint)n) throw new ArgumentOutOfRangeException(nameof(i));
        int start = n < Capacity ? 0 : _head;
        return _ring[(start + i) & (Capacity - 1)];
    }

    /// <summary>One line: <c>ops(N/total)=Op#t:A:B:aux|…</c>. Failure path only (allocates).</summary>
    public void Format(System.Text.StringBuilder sb)
    {
        int n = Count;
        sb.Append("ops(").Append(n).Append('/').Append(_total).Append(")=");
        for (int i = 0; i < n; i++)
        {
            Entry e = At(i);
            if (i != 0) sb.Append('|');
            sb.Append(e.Op).Append('#').Append(e.Target).Append(':').Append(e.A.ToString("X")).Append(':').Append(e.B.ToString("X"));
            if (e.Aux != 0) sb.Append(':').Append(e.Aux);
        }
    }
}
```

**Device side (`D:`):** one field `private readonly RecordedOpRing _recOps = new();` and one inlined helper

```csharp
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Rec(RecordedOp op, uint a = 0, uint b = 0, ushort aux = 0)
        => _recOps.Push(op, _activeSwapchain?.Ordinal ?? 0, a, b, aux);
```

Write sites (all state ops, ~14 lines): `Barrier` D:4772 → `Rec(RecordedOp.Barrier, (uint)(nint)res, ((uint)before << 16) | (uint)after)`;
list reset D:1849 → `Rec(RecordedOp.ListReset, _frameIndex)`; every `OMSetRenderTargets` (D:2077, 3251 → `SetRenderTargetWithDsv, a=(uint)rtv.ptr, b=((uint)_stencilW<<16)|(uint)_stencilH`, 3255, 3918, 4408, 4462, plus the `RebindCurrentTarget` path);
`ClearRenderTargetView` D:2081/3922, `ClearDepthStencilView` D:3288; `SetFullViewport` D:2910 (`a=_w, b=_h`) and
`SetLocalBlurViewport` D:2929; `SetScissorRect` D:2963 (`a=(uint)(left<<16|top)`, `b=(uint)(right<<16|bottom)`);
`EnsureStencilDsv` success D:3201 (`StencilDsvCreated, a=cw, b=ch`); `OMSetStencilRef` D:3324/3348/3370;
`ResolveQueryData` D:2263/2277; `AcrylicCompositor.EnsureSize` caller D:1949/2067 when the size changed (`CanvasResized`);
`_opacity.Acquire` D:4105/4228 (`LayerAcquire, a=slot`); `CopyTextureRegion` in `CaptureBgra` D:5643. Each is a struct
write — no allocation, no branch in the success path beyond the inlined store.

**On `Close` failure (D:2286-2310 becomes):**

```csharp
        HRESULT closeHr = _cmdList->Close();
        if ((int)closeHr < 0)
        {
#if DEBUG
            DrainDebugLayerMessages();
#endif
            // ALWAYS-ON (Release too): name the last 64 recorded state ops before rethrowing. This is the ONLY evidence
            // a shipping crash leaves — the debug layer is #if DEBUG + FG_D3D12_DEBUG and never runs in the field.
            var sb = new System.Text.StringBuilder(2048);
            sb.Append("[d3d12.forensic] cmdList.Close hr=0x").Append(((uint)(int)closeHr).ToString("X8"))
              .Append(" target=").Append(sc.Ordinal).Append(" primary=").Append(ReferenceEquals(sc, _primarySwapchain) ? 1 : 0)
              .Append(" active=").Append(_activeSwapchain?.Ordinal ?? 0)
              .Append(" w=").Append(_w).Append(" h=").Append(_h).Append(" frameIndex=").Append(_frameIndex)
              .Append(" dsv=").Append(_stencilW).Append('x').Append(_stencilH).Append(" depth=").Append(_stencilDepth)
              .Append(" fence=").Append(_fenceValue).Append(' ');
            _recOps.Format(sb);
            Diag.Line(sb.ToString());
            _ = _cmdList->Close();   // D3D12 contract: a failed Close leaves the list OPEN; force it closed (existing comment)
            Check(closeHr, "cmdList.Close");
        }
        Rec(RecordedOp.ListClose);
```

Line format (one line, one string, failure path only):
`[d3d12.forensic] cmdList.Close hr=0x80070057 target=2 primary=0 active=2 w=480 h=270 frameIndex=1 dsv=480x270 depth=1 fence=8812 ops(64/91233)=ListReset#1:1:0|Barrier#1:7FF6A3C0:0->4|SetRenderTarget#1:…|StencilDsvCreated#1:1E0:10E|SetRenderTargetWithDsv#1:…|…`
(`#n` = target ordinal at record time — a mismatch between `target=` and the `#n` of the last ops is the §1.2 signature.)

### 2.7 Phase 0 gates

- Engine.Tests `RenderOwnershipTests` (§7.2) — new.
- VerticalSlice `detached-render` suite: `gate.d3d12.forensic.ring-*` (§7.1) — new; existing `StencilDsvPolicy`/
  `SlotQuarantinePolicy` checks stay.
- Live: `--detached-stress` (§7.3) on the owner's machine (second monitor at a different refresh), Debug + debug layer.
- Wavee: the reported cycle (pop-out on DISPLAY2, track switch closes/reopens) with the app-side whitelist line in.

---

## 3. Phase 1 — per-target recording state

### 3.1 `TargetFrameState` (Windows/D3D12, new file `TargetFrameState.cs`)

Everything a frame records against that is **owned by one target** and can change *during* a submit. Referenced from
`D3D12Swapchain.Frame` (created in the `D3D12Swapchain` ctor — cold path). The public `ISwapchain` surface and the
seqlock samples stay on `D3D12Swapchain`.

```csharp
namespace FluentGpu.Rhi.D3D12;

/// <summary>Per-target recording + present state. Render-owner-confined (ThreadGuard.AssertRenderOwner). NEVER copied
/// into device fields: SubmitDrawList/Present take a reference (D3D12Device._f) for the duration of the call.</summary>
internal sealed unsafe class TargetFrameState
{
    internal readonly D3D12Swapchain Target;
    internal TargetFrameState(D3D12Swapchain target) => Target = target;

    // ── GPU side ─────────────────────────────────────────────────────────────────────────────────────────────────
    internal ID3D12GraphicsCommandList* List;                                   // ONE list per target; Reset with the ring's allocator
    internal readonly ulong[] FenceValues = new ulong[D3D12Device.FRAME_COUNT]; // fence value of the last submit that used back buffer k
    internal ulong LastSubmitFence;                                             // max(FenceValues): the stamp for resize/teardown waits
    internal uint FrameIndex;                                                   // GetCurrentBackBufferIndex() at submit entry (moved from D3D12Swapchain)

    // ── stencil path clip (moved from D3D12Swapchain.StencilDsv* + D3D12Device._stencil*) ────────────────────────
    internal ID3D12Resource* StencilDsv;
    internal ID3D12DescriptorHeap* DsvHeap;
    internal int StencilW, StencilH;
    internal int StencilDepth;
    internal readonly List<bool> StencilScopeMasked = new(8);
    internal bool StencilDsvBound;

    // ── layer pools (lazily created on the first layered submit of THIS target — today only the primary ever is) ──
    internal AcrylicCompositor? Acrylic;
    internal OpacityLayerCompositor? Opacity;
    internal ulong[] CurBlurHashes = new ulong[64], LastBlurHashes = new ulong[64];
    internal int CurBlurHashCount, LastBlurHashCount;

    // ── §13.1 canvas/damage ledger (moved from D3D12Device._canvas*/_last*/_repaintCensus*) ──────────────────────
    internal RepaintLedger Damage;   // struct: CanvasValid, CanvasW/H, LastConsumedSequence, LastClearColor/Valid,
                                     // LastFrameScaleUsed/Valid, CanvasDrawListHash, TextRepaintPending (volatile-read via a method),
                                     // LastRepaintRoute/FullReason/ReplayRectCount/Coverage, the census counters
    // ── present side (moved from D3D12Device) ────────────────────────────────────────────────────────────────────
    internal bool OccludedLatched, LastPresentStoodDown, SkipLatencyOnce, SkipVsyncOnce, HintSettlePresent;
    internal double LastFenceWaitMs, LastLatencyWaitMs;
    internal PresentStats LastPresentStats;
    internal long LastDwmSampleQpc; internal ulong DwmFramesDropped, DwmFramesMissed, DwmFramesLate; internal bool DwmBaselined;
}
```

### 3.2 `SubmissionRing` (Windows/D3D12, new file `SubmissionRing.cs`) — the shared CPU-written banks

```csharp
/// <summary>The device's CPU-written per-submission banks — allocator, UploadArena bank, every pipe's instance bank,
/// the glyph instance bank, the timestamp query banks — keyed by a SUBMISSION counter, not by a back-buffer index.
/// Slot reuse waits on the fence value of the submit that last used that slot (whichever target it was), so two
/// targets presenting every turn are 3 submissions apart on the same slot instead of sharing one index in lockstep.
/// Depth == FrameBankDepth: no bank widens (FrameBankingTests keeps pinning the derived widths).</summary>
internal sealed unsafe class SubmissionRing
{
    internal const int Depth = D3D12Device.FrameBankDepth;
    internal readonly ID3D12CommandAllocator*[] Allocators = new ID3D12CommandAllocator*[Depth];   // moved from D3D12Device._allocators
    internal readonly ulong[] Fence = new ulong[Depth];              // fence value signalled by the submit that last used slot k
    internal readonly D3D12Swapchain?[] Owner = new D3D12Swapchain?[Depth];   // timestamp attribution (replaces _gpuExecutionTsOwner/_gpuTsOwner)
    internal readonly ulong[] OwnerSubmit = new ulong[Depth];       // replaces _gpuExecutionTsOwnerSubmit
    internal readonly bool[] ExecTsPending = new bool[Depth], ProfileTsPending = new bool[Depth];
    internal ulong SubmitCounter;
    internal int NextSlot => (int)(SubmitCounter % (ulong)Depth);
    internal void Stamp(int slot, ulong fence, D3D12Swapchain owner, ulong ownerSubmit)
    { Fence[slot] = fence; Owner[slot] = owner; OwnerSubmit[slot] = ownerSubmit; SubmitCounter++; }
}
```

### 3.3 Field-by-field migration table

Legend — **alias**: assigned once at `BeginTargetFrame(f)` from `f`/`f.Target`, read-only for the submit, never stored
back (allowed because the value cannot change during a submit); **f.**: moves to `TargetFrameState`; **ring**: moves
to `SubmissionRing`; **delete**: no reader after the move.

| Device field (D:) | Writers today | Readers today (representative) | New owner |
|---|---|---|---|
| `_activeSwapchain` :97 | Activate/StoreActive/Dispose | ReleaseStencilDsv, StoreActive | **delete** → `_f` (the current `TargetFrameState`, set by `BeginTargetFrame`) |
| `_hwnd` :93 | Activate | none | **delete** (Phase 0) |
| `_swapChain` :76 | Activate/StoreActive | Submit :1836, Present :4808/4827, SamplePresentStats :5000-5005 | **alias** `_f.Target.SwapChain` (read directly; ~4 sites) |
| `_rtvHeap` :77 | Activate/StoreActive | :1875 | read `_f.Target.RtvHeap` |
| `_backBuffers[]` :79 | Activate/StoreActive/Resize/Dispose/Recover | :1872, 4259, 4440, 4465, 4476, 5607 | read `_f.Target.BackBuffers[_f.FrameIndex]` |
| `_w/_h` :94 | Activate/StoreActive/Resize | 35 sites | **alias** (assigned from `_f.Target.W/H` at entry; Resize is render-owned and never mid-submit) |
| `_frameIndex` :95 | Activate/StoreActive/Submit :1836 | 51 sites | **f.**`FrameIndex` for the ledger; the CPU-bank sites (BeginFrame, query indices, `_sceneCat`, compositor banks) switch to the ring **slot** (`int slot = _ring.NextSlot`) |
| `_frameLatencyWaitable/_hasLatencyWaitable` :86-87 | Activate/StoreActive | WaitForLatency :5206 | **delete** (Phase 2 deletes `WaitForLatency`; until then read `_f.Target.*`) |
| `_swapChainFlags/_tearingSupported` :88-89 | Activate/StoreActive | Present :4826 | read `_f.Target.TearingSupported` |
| `_allocators[]` :80 | InitDevice/Recover/Dispose/MemoryProbe | :1847, 5630 | **ring** `Allocators[slot]` |
| `_frameFenceValues[]` :81 | SignalFrame :5175, Recover :5823 | WaitForFrame :5180 | **f.**`FenceValues[k]` (target) **and** ring `Fence[slot]` |
| `_cmdList` :82 | InitDevice/Recover/MemoryProbe | 107 sites | **alias** `_cmdList = _f.List` at entry (per-target list; `MemoryProbeReplaceCommandList` replaces the PRIMARY target's list) |
| `_fence/_fenceValue/_fenceEvent` :83-85 | render owner only (Phase 0) | everywhere | **stay** (one monotonic device fence) |
| `_stencilDsv/_dsvHeap/_stencilW/_stencilH` :128-130 + `D3D12Swapchain.StencilDsv*` :5926-5928 | Activate/StoreActive/EnsureStencilDsv/Release | :3130-3132, 3184-3201, 3250, 3288, 1811 | **f.**`StencilDsv/DsvHeap/StencilW/StencilH` (the D3D12Swapchain copies are deleted) |
| `_stencilDepth/_stencilScopeMasked/_stencilDsvBound` :134-139 | scope push/pop, Release | :3324, 3348, 3370, 3710 | **f.** |
| `_frameStencilClips/_frameStencilFallback` :140 | per submit | Diag.Set | stay (per-submit scratch) |
| `_acrylic/_opacity` :141-142 | EnsurePipelines :846-847 / Recover | ~60 sites | **f.**`Acrylic/Opacity` — created by `EnsureLayerPools(f)` on the first `layerKind != 0` submit of that target (the stage-6/7 tasks in `EnsurePipelines` D:816-817 move there; `Init(_device, _queue)` unchanged). `TickIdle` :2075-2076 → `_f.Acrylic?.TickIdle(...)` (null on children/popups ⇒ no cross-target aging) |
| `_curBlurHashes/_lastBlurHashes/_cur…Count` :167-169 | rotate :1898-1899, PushLayer | FindPin recurrence | **f.** |
| `_bakedBlur/_bakedBlurQueue/_uploadArena/_glyphs/_imageTextures/_sdf/pipes` | EnsurePipelines | everywhere | **stay** (device-shared; banked by ring slot) |
| `_canvasValid/_canvasW/_canvasH/_lastConsumedSequence/_lastClearColor/_lastClearValid/_lastFrameScaleUsed/_lastFrameScaleValid/_canvasDrawListHash/_lastRepaintRoute/_lastRepaintFullReason/_lastReplayRectCount/_lastRepaintCoverage/_dmg*/_repaintCensus*` :437-455 | Submit, ResetRepaintLedger :456 | Submit, IGpuDevice diag getters :466-480, MemoryProbeWorkload | **f.**`Damage` (`ResetRepaintLedger(f)`); the `IGpuDevice` getters read `_primarySwapchain.Frame.Damage` |
| `_textRepaintPending` :442 (volatile) | Submit :2116/2128/2138 | `TextRepaintPending` :5545 (UI) | **f.** (volatile field inside the struct is illegal → keep as a `volatile bool` on `TargetFrameState`, exposed via `ISwapchain.TextRepaintPending`; `IGpuDevice.TextRepaintPending` deleted; AppHost reads `_swapchain.TextRepaintPending`) |
| `_rootDamageActive/_rootDamage/_cullActive/_cullRect/_layerHaloPx` :328-347 | per submit | per submit | stay (per-submit scratch, reset at entry) |
| `_targetOriginX/Y/_targetWidth/Height` :319-320 | SetFullViewport/SetLocalBlurViewport | TargetW/H, SetScissorRect | stay (per-submit scratch, reset by SetFullViewport) |
| `_occludedLatched/_lastPresentStoodDown` :4943-4944 | Present | Present, `LastPresentStoodDown` :4947 | **f.** (`ISwapchain.LastPresentStoodDown`; `IGpuDevice.LastPresentStoodDown` deleted; AppHost reads `_swapchain.LastPresentStoodDown`) |
| `_skipLatencyOnce/_skipVsyncOnce/_hintSettlePresent` :5235, 5549, 5553 | `IGpuDevice.Suppress*Once/HintSettlePresent` | Submit :1828, Present :4822/4853 | **f.** — the seam methods move to `ISwapchain` (`Rhi.cs:305-317` → new default members on `ISwapchain`; `HeadlessSwapchain` implements them, `HeadlessGpuDevice.HintSettlePresentCount` reads its primary swapchain's counter so `LayoutShellSuite.cs:1337-1338` keeps passing); callers A:1023-1024, A:4412, A:4481-4482 → `_swapchain.*` |
| `_lastFenceWaitMs/_lastLatencyWaitMs` :5237-5238 | Submit :1835/1839, WaitForPresentSlot :5227 | `LastFenceWaitMs` :5240, PresentStats :5066 | **f.** — `ISwapchain.LastFenceWaitMs/LastLatencyWaitMs` (default 0); `IGpuDevice.LastFenceWaitMs` (Rhi.cs:198) deleted; A:4634 and A:2108 read `_swapchain.LastFenceWaitMs` (a plain double, render-written/UI-read gauge — same contract as today) |
| `_lastPresentStats/_lastDwmSampleQpc/_dwmFrames*/_dwmBaselined` :4979-4982 | SamplePresentStats | `LastPresentStats` :4994 | **f.** (`ISwapchain.LastPresentStats`; the 1 Hz DWM sample runs on whichever target presents — counters are main-monitor-global, per the existing comment) |
| `_gpuExecutionTsPending/Owner/OwnerSubmit` :5258-5260, `_gpuTsPending/Owner` :5271-5272, `_sceneCat/_sceneCatCount` :5293-5294 | Submit, ReleaseSwapchainResources :5718-5730 | Collect* | **ring** (`ExecTsPending/ProfileTsPending/Owner/OwnerSubmit`; `_sceneCat[slot]`); `ReleaseSwapchainResources` clears the ring's owner entries naming the target |
| `_frameScale/_imageClockMs` :275-276 | Submit entry | recorder | stay (per-submit scratch) |

**Zero-alloc:** `BeginTargetFrame(TargetFrameState f)` is reference assignment + scalar copies; `TargetFrameState` and
its arrays are created in `CreateSwapchain`/`EnsureLayerPools` (cold). The recorder's hot loops are untouched apart from
the field spelling. The existing alloc tripwire covers phases 6–13 headlessly; the D3D12 path is covered by the
`--detached-stress` allocation counter (§7.3).

### 3.4 `SubmitDrawList(…, target)` — the new entry/exit (replaces D:1797-1850 and D:2312-2334)

```csharp
    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx, ISwapchain target)
    {
        if (target is not D3D12Swapchain sc || sc.Device != this || sc.Disposed)
            throw new InvalidOperationException("Submit target is not a live D3D12 swapchain from this device.");
        AssertSubmitThread();
        TargetFrameState f = sc.Frame;
        BeginTargetFrame(f);                     // _f = f; _cmdList = f.List; _w = sc.W; _h = sc.H; per-submit scratch reset
        if (f.StencilDsv != null && StencilDsvPolicy.NeedsRecreate(f.StencilW, f.StencilH, (int)sc.W, (int)sc.H))
        {
            WaitForFenceValue(f.LastSubmitFence);   // THIS target's in-flight work only — never a device drain
            ReleaseStencilDsv(f);
        }
        long fenceWaitStart = Stopwatch.GetTimestamp();
        // (Phase 2 deletes the latency wait here entirely; until then: pace on THIS target's credit, not the primary's)
        if (f.SkipLatencyOnce) f.SkipLatencyOnce = false;
        else if (!sc.LatencyCreditHeld) WaitForLatency(sc);
        long latencyDone = Stopwatch.GetTimestamp();
        if (!sc.LatencyCreditHeld) f.LastLatencyWaitMs = Stopwatch.GetElapsedTime(fenceWaitStart, latencyDone).TotalMilliseconds;
        f.FrameIndex = sc.SwapChain->GetCurrentBackBufferIndex();
        int slot = _ring.NextSlot;
        // Two waits, both usually no-ops: this target's back buffer k (RTV reuse) and the ring slot (CPU-bank reuse).
        if (!WaitForFenceValue(f.FenceValues[f.FrameIndex]) || !WaitForFenceValue(_ring.Fence[slot]))
            throw new InvalidOperationException("Frame fence did not reach the awaited value; submit cannot reuse its bank.");
        f.LastFenceWaitMs = Stopwatch.GetElapsedTime(fenceWaitStart).TotalMilliseconds;
        _frameScale = ctx.Scale <= 0f ? 1f : ctx.Scale;
        _glyphs!.BeginFrame(slot);
        PrepareGlyphs(drawList);
        EnsureGpuExecutionTiming();
        CollectGpuExecutionTime(slot);
        _gpuTimingFresh = false;
        if (s_gpuTiming) { EnsureGpuTiming(); CollectGpuCategoryTimes(slot); }
        ID3D12CommandAllocator* allocator = _ring.Allocators[slot];
        Check(allocator->Reset(), "allocator.Reset");
        Check(_cmdList->Reset(allocator, null), "cmdList.Reset");
        Rec(RecordedOp.ListReset, (uint)slot, f.FrameIndex);
        InvalidateCmdState();
        … (D:1851-1870 unchanged except `2u * _frameIndex` → `2u * (uint)slot`, `GpuTsPerFrame * _frameIndex` → `* (uint)slot`) …
        ID3D12Resource* backBuffer = sc.BackBuffers[f.FrameIndex];
        Barrier(backBuffer, PRESENT, RENDER_TARGET);
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = sc.RtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += f.FrameIndex * _rtvSize;
        … (per-submit counters reset; `_sceneCatCount[slot] = 0`; the blur ring rotate becomes
            `(f.LastBlurHashes, f.CurBlurHashes) = (f.CurBlurHashes, f.LastBlurHashes); f.LastBlurHashCount = f.CurBlurHashCount; f.CurBlurHashCount = 0;`) …
        _uploadArena!.BeginFrame(slot); _rectPipe!.BeginFrame(slot); … _imagePipe!.BeginFrame(slot);
        … (route select unchanged, with `_acrylic` → `f.Acrylic` after `EnsureLayerPools(f)` on `layerKind != 0`,
            `_canvasValid` → `f.Damage.CanvasValid`, etc.; the no-layer branch: `f.Acrylic?.TickIdle(completed); f.Opacity?.TickIdle(completed);`) …
        Barrier(backBuffer, RENDER_TARGET, PRESENT);
        … (query end/resolve with `slot`; the Close block from §2.6) …
        ID3D12CommandList* execList = (ID3D12CommandList*)_cmdList;
        ID3D12CommandQueueVtbl.ExecuteCommandLists(_queue, 1, (void**)&execList);
        _bakedBlur?.PublishRecorded(_bakedBlurQueue);
        ulong v = SignalNext();                       // ++_fenceValue; queue.Signal(v)  (replaces SignalFrame)
        f.FenceValues[f.FrameIndex] = v;
        f.LastSubmitFence = v;
        ulong gpuExecutionSubmitSequence = sc.NoteGpuSubmit();
        _ring.Stamp(slot, v, sc, gpuExecutionSubmitSequence);
        _ring.ExecTsPending[slot] = gpuExecutionResolved;
        _ring.ProfileTsPending[slot] = gpuProfileResolved;
#if DEBUG
        DrainDebugLayerMessages();
#endif
        sc.PublishRectSubmittedArea(…);
        EndTargetFrame();                             // _f = null!; _cmdList = null (a stray post-submit touch faults loudly in Debug)
    }
```

`WaitForFenceValue(ulong v)` is `WaitForFrame`'s body (D:5178-5200) taking the value instead of the index. `SignalNext()`
is `SignalFrame` minus the ledger write. `WaitForGpu` stays as the device-wide drain for `Dispose`/`CaptureBgra`/probes.

`Present(target)` (D:4777): `BeginTargetFrame(sc.Frame)` instead of `Activate`; `_occludedLatched`… → `f.`; `StandDownPresent`
→ writes `f.LastPresentStoodDown`; `StoreActive()` at D:4870/4964 deleted. `CaptureBgra`: `BeginTargetFrame(_primary.Frame)`,
allocator = `_ring.Allocators[_ring.NextSlot]` after `WaitForGpu()`. `Resize`: `WaitForFenceValue(target.Frame.LastSubmitFence)`
instead of `WaitForGpu()` (pal-rhi.md §5.4 as designed: "CPU-wait only the in-flight fence values that referenced THIS
swapchain"); `ResetRepaintLedger(target.Frame)`; `ReleaseStencilDsv(target.Frame)`; no `Activate`.
`ReleaseSwapchainResources`: releases `Frame.List`, `Frame.StencilDsv/DsvHeap`, `Frame.Acrylic?.Dispose()`,
`Frame.Opacity?.Dispose()`, clears ring owner entries naming the target. `RecoverDevice`: per target `Frame.List`
recreate + `Array.Clear(Frame.FenceValues)`, `Array.Clear(_ring.Fence)`. `MemoryProbe*`: `_allocators[bank]` →
`_ring.Allocators[bank]`, `_cmdList` → `_primarySwapchain!.Frame.List`.

### 3.5 Deletions in Phase 1

`Activate`, `StoreActive`, `_activeSwapchain`, the device working fields listed as **delete/f./ring** above,
`D3D12Swapchain.StencilDsv/StencilDsvHeap/StencilDsvW/StencilDsvH/FrameIndex`, `SignalFrame`, `WaitForFrame(uint)`,
`_gpuExecutionTsOwner*`/`_gpuTsOwner*`, `IGpuDevice.LastFenceWaitMs/LastPresentStoodDown/TextRepaintPending/
SuppressLatencyWaitOnce/SuppressVsyncOnce/HintSettlePresent/LastPresentStats` (moved to `ISwapchain`), the
`ResetRepaintLedger()` no-arg. The Phase 0 `AssertDeviceOwner` sites on `Activate/StoreActive` go with them.

### 3.6 Phase 1 gates

`TargetFenceLedger` (pure, `src/FluentGpu.Engine/Rhi/TargetFenceLedger.cs`): the decision "which fence value must a
target wait on for back buffer k, and which must a ring slot wait on" extracted from §3.4 —

```csharp
public sealed class TargetFenceLedger
{
    private readonly ulong[] _perIndex; public ulong LastSubmit { get; private set; }
    public TargetFenceLedger(int frameCount) => _perIndex = new ulong[frameCount];
    public ulong WaitValueFor(int backBufferIndex) => _perIndex[backBufferIndex];   // 0 = nothing in flight
    public void Stamp(int backBufferIndex, ulong fence) { _perIndex[backBufferIndex] = fence; LastSubmit = fence; }
}
public static class SubmissionRingPolicy
{
    public static int Slot(ulong submitCounter, int depth) => (int)(submitCounter % (ulong)depth);
}
```

Gates (§7.1): a target's wait for index k after another target stamped k is 0 (independent ledgers); the ring slot for
submit n is reused at n+depth; `LastSubmit` is the max stamp. `FrameBankingTests` keeps passing unchanged
(`SubmissionRing.Depth == FrameBankDepth`) plus one assertion `Assert.Equal(D3D12Device.FrameBankDepth, SubmissionRing.Depth)`.

---

## 4. Phase 2 — pacing without cross-window blocking

### 4.1 `WindowPacingScheduler` (pure, `src/FluentGpu.Engine/Hosting/Threading/WindowPacingScheduler.cs`)

```csharp
namespace FluentGpu.Hosting.Threading;

public struct WindowPacingInput
{
    public bool HasPendingFrame;   // seam has an unconsumed publication
    public bool CreditHeld;        // a present-slot credit is in hand (one wait consumed, no Present spent it yet)
    public bool HasWaitable;       // the target exposes a present-slot wait handle
    public bool Occluded;          // minimized/hidden/cloaked (the backend would stand down)
    public bool Failed;            // RenderFailed latch (detached child)
    public bool MotionDue;         // compositor motion tick owed
}

public enum WindowTurnAction : byte { Skip = 0, Present = 1, TickOnly = 2 }

/// <summary>makepad's beat (win32_app.rs:521-590, 793-833): ONE wait over every window's present-slot waitable; a
/// window whose waitable signalled holds a credit and presents; a window without a credit is never waited on alone.</summary>
public static class WindowPacingScheduler
{
    public const int MaxWaitHandles = 64;   // MAXIMUM_WAIT_OBJECTS; WaitHandle.WaitAny throws above it

    /// <summary>Indices of the windows whose waitable belongs in this turn's wait set: has a waitable, holds no credit,
    /// is not occluded/failed, and has something to do (a pending frame or motion due). Returns the count written.</summary>
    public static int BuildWaitSet(ReadOnlySpan<WindowPacingInput> w, Span<int> waitIndices, int reservedHandles)
    {
        int n = 0, cap = MaxWaitHandles - reservedHandles;
        for (int i = 0; i < w.Length && n < cap; i++)
        {
            ref readonly var x = ref w[i];
            if (x.HasWaitable && !x.CreditHeld && !x.Occluded && !x.Failed && (x.HasPendingFrame || x.MotionDue))
                waitIndices[n++] = i;
        }
        return n;
    }

    public static WindowTurnAction Decide(in WindowPacingInput x)
    {
        if (x.Failed) return WindowTurnAction.Skip;
        if (x.Occluded) return x.MotionDue ? WindowTurnAction.TickOnly : WindowTurnAction.Skip;   // never present, never wait
        if (!x.HasPendingFrame) return x.MotionDue ? WindowTurnAction.TickOnly : WindowTurnAction.Skip;
        if (!x.HasWaitable) return WindowTurnAction.Present;          // unpaced target (headless / no waitable): present now
        return x.CreditHeld ? WindowTurnAction.Present : WindowTurnAction.Skip;   // no credit ⇒ skipped THIS turn, re-probed next
    }

    /// <summary>The wait timeout: Infinite when nothing is owed; otherwise the earliest motion deadline, bounded so a
    /// window whose compositor stopped retiring presents (DWM stall, device loss) can never wedge the loop.</summary>
    public static int TimeoutMs(bool anyPendingWithoutCredit, bool anyMotionDue, long nextTickQpc, long nowQpc, long qpcFreq,
                                int stalledProbeMs = 100)
    {
        int motion = anyMotionDue ? Math.Max(0, (int)Math.Ceiling((nextTickQpc - nowQpc) * 1000.0 / qpcFreq)) : System.Threading.Timeout.Infinite;
        if (!anyPendingWithoutCredit) return motion;
        return motion == System.Threading.Timeout.Infinite ? stalledProbeMs : Math.Min(motion, stalledProbeMs);
    }
}
```

Decision table (the gates assert every row):

| pending | credit | waitable | occluded | failed | motion | in wait set | action |
|---|---|---|---|---|---|---|---|
| any | any | any | any | **true** | any | no | Skip |
| any | any | any | **true** | false | true / false | no | TickOnly / Skip |
| false | any | any | false | false | true / false | yes if !credit & waitable (motion) / no | TickOnly / Skip |
| true | **true** | any | false | false | any | no | **Present** |
| true | false | **false** | false | false | any | no | **Present** (unpaced) |
| true | false | true | false | false | any | **yes** | Skip (this turn) |

### 4.2 Seam additions (`src/FluentGpu.Engine/Seams/Rhi/Rhi.cs`, `ISwapchain`)

```csharp
    /// <summary>The present-slot waitable (DXGI frame-latency waitable) as a BCL WaitHandle; null when unpaced.
    /// Render-thread only. One successful wait = one credit; the render loop reports it via NotePresentCreditTaken and
    /// Present spends it.</summary>
    System.Threading.WaitHandle? PresentSlotWaitHandle => null;
    bool PresentCreditHeld => false;
    void NotePresentCreditTaken() { }
    double LastFenceWaitMs => 0; double LastLatencyWaitMs => 0;   // (Phase 1)
```

`D3D12Swapchain`: `PresentSlotWaitHandle` = a `RawWaitHandle` (new file `src/FluentGpu.Windows/D3D12/RawWaitHandle.cs`,
`sealed class RawWaitHandle : WaitHandle { public RawWaitHandle(nint h) => SafeWaitHandle = new SafeWaitHandle(h, ownsHandle: false); }`)
created once in `InitSwapChain` (cold), disposed in `ReleaseSwapchainResources`; `PresentCreditHeld => LatencyCreditHeld`;
`NotePresentCreditTaken() => LatencyCreditHeld = true`. `IGpuDevice.WaitForPresentSlot` (Rhi.cs:106, D:5221-5229) and
`WaitForLatency` (D:5204-5207) are **deleted** — the loop's single wait replaces both; `Present` keeps clearing the credit
(D:4833, now for every target, not only the primary).

### 4.3 `IRenderSource` + the `RenderThread` loop

The loop needs per-window facts today hidden behind `_submitPresent`/`_extraDrain` delegates. Replace the delegate pair
with a small interface AppHost implements (parent and children), registered under `Quiesce` (the existing
`Attach/DetachChildRenderSource` sites, A:1850-1864):

```csharp
public interface IRenderSource
{
    SceneFramePublisher Seam { get; }
    ISwapchain Swapchain { get; }
    bool Occluded { get; }        // UI-written volatile (IsIconic/hidden/cloaked, refreshed on WM_SIZE/WM_SHOWWINDOW/cloak)
    bool RenderFailed { get; }
    bool MotionDue { get; }       // HasOwnRenderMotion()
    long TickPeriodQpc { get; }
    void SubmitPresent(RenderFrame rf);   // ON the render thread — SubmitPresentOnRenderThread
    void Tick();                          // RenderMotion
}
```

`RenderThread` (RT:81-158 → new loop; fields: `IRenderSource[] _sources` (rebuilt on Add/Remove under park — cold),
`WaitHandle[][] _waitSets` (one array per possible count 2..64, allocated on membership change — cold), scratch
`int[64] _waitIdx`, `WindowPacingInput[64] _inputs`, `WindowTurnAction[64] _actions`):

```csharp
    private void Loop()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Render);
        while (true)
        {
            // 1. Facts (volatile reads; zero-alloc)
            bool anyMotion = false, anyPendingNoCredit = false; long nextTick = long.MaxValue;
            for (int i = 0; i < _sources.Length; i++)
            {
                var s = _sources[i]; var sc = s.Swapchain;
                ref var x = ref _inputs[i];
                x.HasPendingFrame = s.Seam.HasPendingFrame; x.CreditHeld = sc.PresentCreditHeld;
                x.HasWaitable = sc.PresentSlotWaitHandle is not null; x.Occluded = s.Occluded; x.Failed = s.RenderFailed;
                x.MotionDue = s.MotionDue;
                anyMotion |= x.MotionDue; if (x.MotionDue) nextTick = Math.Min(nextTick, _nextTick[i]);
                anyPendingNoCredit |= x.HasPendingFrame && x.HasWaitable && !x.CreditHeld && !x.Occluded && !x.Failed;
            }
            // 2. ONE wait over wake + display ticks + every credit-less waitable (makepad's beat)
            int reserved = 1 + (_displayClock is not null ? 1 : 0);
            int n = WindowPacingScheduler.BuildWaitSet(_inputs.AsSpan(0, _sources.Length), _waitIdx, reserved);
            var set = _waitSets[reserved + n]; set[0] = _wake; if (_displayClock is not null) set[1] = _displayClock.Tick;
            for (int i = 0; i < n; i++) set[reserved + i] = _sources[_waitIdx[i]].Swapchain.PresentSlotWaitHandle!;
            _displayClock?.SetActive(anyMotion);
            int timeout = WindowPacingScheduler.TimeoutMs(anyPendingNoCredit, anyMotion, nextTick, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            int signalled = WaitHandle.WaitAny(set, timeout);
            if (signalled >= reserved) _sources[_waitIdx[signalled - reserved]].Swapchain.NotePresentCreditTaken();
            // Collect every OTHER credit that is already available without blocking (WaitAny consumed only the lowest).
            for (int i = 0; i < n; i++) { int si = _waitIdx[i]; var h = _sources[si].Swapchain; if (!h.PresentCreditHeld && h.PresentSlotWaitHandle!.WaitOne(0)) h.NotePresentCreditTaken(); }
            long turnStart = Stopwatch.GetTimestamp();
            long requestedDrain = Volatile.Read(ref _requestedDrains);
            if (!_running) break;
            … (RT:105-127: device-lost recover gate and the quiesce gate — UNCHANGED) …
            // 3. Present whoever is ready; never wait for anyone individually.
            for (int i = 0; i < _sources.Length; i++)
            {
                var s = _sources[i]; _inputs[i].CreditHeld = s.Swapchain.PresentCreditHeld;
                switch (WindowPacingScheduler.Decide(in _inputs[i]))
                {
                    case WindowTurnAction.Present:
                        if (s.Seam.TryAcquire(out var rf)) { s.SubmitPresent(rf); if (i == 0) Volatile.Write(ref _presentAck, rf.PublishSeq); }
                        else if (_inputs[i].MotionDue) s.Tick();
                        break;
                    case WindowTurnAction.TickOnly: s.Tick(); break;
                }
                if (_inputs[i].MotionDue) _nextTick[i] = turnStart + Math.Max(1, s.TickPeriodQpc);
            }
            if (requestedDrain > Volatile.Read(ref _completedDrains)) { Volatile.Write(ref _completedDrains, requestedDrain); _done.Set(); }
        }
        _displayClock?.SetActive(false);
    }
```

Notes for the implementer: (a) `WaitHandle.WaitAny` needs an exact-length array, hence the per-count array cache (cold);
if the `ReadOnlySpan<WaitHandle>` overload is available on the target runtime, use it and drop the cache. (b) `_wake`
stays index 0; the display clock tick, when present, index 1 (a child's own compositor clock is *not* added: child motion
is ticked from the parent's beat, as today — the child's `RefreshLattice` is already built UI-side from its own window's
`Win32CompositorClock`, Win32Platform.cs:1590-1595). (c) 64-handle limit: `BuildWaitSet` caps at `64 - reserved`; a window
beyond the cap behaves as `HasWaitable = false` (presents unpaced) — `Debug.Assert(_sources.Length + reserved <= 64)` and a
one-time `Diag.Line("[render.pacing] wait set capped …")`. (d) Occluded windows are excluded from the wait set, so a
minimized pop-out whose compositor never retires a present can never stall the main window; `Present` still stands down for
it (D:4797-4801) if it is reached through the unpaced row. (e) Device-lost: the recover gate runs before any present
(unchanged); waits are bounded by `TimeoutMs` (≤100 ms whenever anything is pending), so a dead waitable cannot hang the loop.
(f) `DrainSync`/`WakeAsync`/`Quiesce`/`Resume`/`Dispose` are unchanged. (g) `_presentAck` tracks source 0 (the owning host).

`AppHost`: implements `IRenderSource` (`Occluded` = `_window` visibility/iconic state, written on the UI thread from the
window-state edges that already drive `SetWindowActive`; `MotionDue` = `HasOwnRenderMotion()`); `AttachChildRenderSource`
→ `rt.AddSource(child)`, `DetachChildRenderSource` → `rt.RemoveSource(child)` (both under the existing park);
`_childRenderSources`, `DrainChildRenderSources`, `HasRenderMotion` (the folded-over-children form), `RenderMotion` and the
`extraDrain/needsTick/tick/tickPeriod/presentSlotWait` ctor arguments (A:3078-3082) are **deleted**. `RecordPopups`
(SceneRenderFrame.cs:105-131) keeps presenting popups inline from the primary's turn — popups have no latency waitable
credit today either (D:1827 primary-only) and their `WaitForLatency` disappears with §4.2, so they become unpaced
composition presents, which is what a hidden-until-painted flyout wants.

### 4.4 Per-target RefreshLattice / FrameStats

Already per host on the UI side (`RefreshLattice.Build` per `RunFrame`; each window's `Win32CompositorClock` carries its
own `SetWindowPeriodQpc`, D:5087-5095 + Win32CompositorClock.cs:150-153). What changes: `FrameStats.FenceWaitMs`
(A:4634) and `LastGpuFenceWaitMs` (A:2108) read `_swapchain.LastFenceWaitMs` (Phase 1), so a child's latency wait can no
longer masquerade as the main's; `PresentStats.LatencyWaitMs` (D:5066) reads `f.LastLatencyWaitMs`. Wavee's diagnostics
consumers (Diagnostics.Host.cs:556, Diagnostics.Probe.Arms.cs:496) read `FrameStats` and need no change.

---

## 5. Phase 3 — deferred destruction keyed by fence

### 5.1 `RetireLedger` (pure, `src/FluentGpu.Engine/Hosting/Threading/RetireLedger.cs`)

```csharp
/// <summary>Fence-keyed FIFO: an item enqueued with stamp S is reclaimable once the GPU fence completed value ≥ S.
/// Stamps are monotone (they are fence values), so the head is always the oldest and a head-only check is exact.
/// Fixed capacity, zero-alloc (threading-render-seam.md §8 as built).</summary>
public sealed class RetireLedger
{
    private readonly (ulong Stamp, int Item)[] _ring; private int _head, _count;
    public RetireLedger(int capacity) => _ring = new (ulong, int)[Math.Max(8, capacity)];
    public int PendingCount => _count;
    public int Capacity => _ring.Length;
    public bool TryEnqueue(ulong stamp, int item)
    {
        if (_count == _ring.Length) return false;
        _ring[(_head + _count) % _ring.Length] = (stamp, item); _count++; return true;
    }
    public bool TryReclaim(ulong completedFence, out int item)
    {
        if (_count > 0 && completedFence >= _ring[_head].Stamp)
        { item = _ring[_head].Item; _head = (_head + 1) % _ring.Length; _count--; return true; }
        item = 0; return false;
    }
}
```

### 5.2 `D3D12RetireQueue` (Windows/D3D12, new file)

```csharp
internal sealed unsafe class D3D12RetireQueue
{
    internal enum Kind : byte { ComPtr, TrackedResource /* D3D12MemoryDiagnostics.Release first */, CloseHandle }
    private struct Entry { public void* Ptr; public Kind Kind; public string? DiagName; }
    // Derived, never a literal: a closing target retires ≤ EntriesPerTarget objects; RenderInFlightDepth + 1 turns may
    // each close one; MaxTargets bounds the fan-out (main + popups + children the wait set admits).
    internal const int EntriesPerTarget = 16, MaxTargets = 16;
    internal const int Capacity = EntriesPerTarget * (QuarantinePolicy.RenderInFlightDepth + 1) * MaxTargets;   // 512
    private readonly RetireLedger _ledger = new(Capacity);
    private readonly Entry[] _entries = new Entry[Capacity];
    private int _write;
    internal bool Enqueue(ulong stamp, void* ptr, Kind kind, string? diagName)
    {
        if (ptr == null) return true;
        int slot = _write;
        if (!_ledger.TryEnqueue(stamp, slot)) return false;   // caller: WaitForFenceValue(stamp) + release inline (never leak, never free early)
        _entries[slot] = new Entry { Ptr = ptr, Kind = kind, DiagName = diagName };
        _write = (_write + 1) % Capacity;
        return true;
    }
    internal void Drain(ulong completedFence)
    {
        while (_ledger.TryReclaim(completedFence, out int slot))
        {
            ref Entry e = ref _entries[slot];
            switch (e.Kind)
            {
                case Kind.TrackedResource: D3D12MemoryDiagnostics.Release((ID3D12Resource*)e.Ptr, e.DiagName!); IUnknownVtbl.Release(e.Ptr); break;
                case Kind.ComPtr: IUnknownVtbl.Release(e.Ptr); break;
                case Kind.CloseHandle: CloseHandle((HANDLE)e.Ptr); break;
            }
            e = default;
        }
    }
}
```

`D3D12Device`: `_retire.Drain(completed)` once per render turn at the top of `SubmitDrawList` (next to
`ReclaimCompleted`, D:967/1859) and in `ReclaimCompletedUploads` (D:5538, the elided-turn path); `Dispose` does
`WaitForGpu(); _retire.Drain(ulong.MaxValue - 1)` before device release; `RecoverDevice` drains with `ulong.MaxValue - 1`
after the dead device's objects are released (the fence is dead — objects are dropped unconditionally, canon §9.2).

### 5.3 Enqueue sites

- **`DisposeSwapchain(target)`** (render thread, via the posted action §5.4) — no `WaitForGpu`. Stamp =
  `target.Frame.LastSubmitFence`. Order of enqueue (FIFO release order): `VideoPresenter.Dispose()` runs **immediately**
  (DComp objects are not fence-tracked; the presenter detaches its visuals under the still-live root); then
  `DcompVisual`, `DcompRoot`, `DcompTarget` (ComPtr), `BackBuffers[0..2]` (TrackedResource), `RtvHeap` (TrackedResource),
  `Frame.StencilDsv` (TrackedResource), `Frame.DsvHeap`, `Frame.List`, `SwapChain` (ComPtr, **last**), `PresentSlotWaitHandle`
  (disposed immediately — it is a BCL wrapper with `ownsHandle: false`; the OS handle dies with the swapchain).
  `Frame.Acrylic/Opacity` retire through their own `Dispose`, which is rewritten to enqueue each pooled RT/canvas with the
  target's stamp instead of releasing inline (AcrylicCompositor.cs:996-1018, OpacityLayerCompositor.cs:2062+).
  `_swapchains.Remove(target)` and `target.Disposed = true` stay immediate.
- **`AcrylicCompositor.EnsureSize`** (AcrylicCompositor.cs:397-408): `EnsureSize(uint w, uint h, D3D12RetireQueue retire, ulong stamp)`;
  the old canvas goes to `retire.Enqueue(stamp, _canvas, TrackedResource, "Acrylic.Canvas")` with
  `stamp = _fenceValue` (the last signalled value — frame N−1 is the only in-flight user; the current list has not
  referenced the canvas yet because EnsureSize runs before any draw, D:1949/2067). The "size changes only follow a fenced
  Resize" comment and the `WaitForGpu` in `Resize` that it relied on (D:5673) are gone; `Resize` waits on the target's own
  `LastSubmitFence` only (§3.4).
- **`Resize`** old back buffers: released inline **after** `WaitForFenceValue(target.Frame.LastSubmitFence)` (DXGI requires
  zero outstanding references before `ResizeBuffers`, so they cannot be deferred).
- **`ReleaseStencilDsv(f)`** on the stale-size path (D:1811-1815): enqueue with `f.LastSubmitFence` instead of waiting.
- `OpacityLayerCompositor` already fence-gates its `_retired` list (LayerTargetTrim.CanRelease) — unchanged.

### 5.4 Child teardown becomes a posted render action (no park, no synchronous wait)

Generalize the popup mailbox (A:1044-1092): `PopupRenderOp` → `RenderOp { PopupResizeAndChrome, PopupAnimateClose, RetireSwapchain }`,
`PostPopupRenderAction` → `PostRenderAction`, `DrainPopupRenderActions` → `DrainRenderActions` (unchanged position: top
of `SubmitPresentOnRenderThread`, A:1100; under Phase 2 it moves to the top of the loop turn via `IRenderSource.DrainActions()`).
`RetireSwapchain` → `swapchain.Dispose()` on the render thread → `DisposeSwapchain` → §5.3.

`AppHost.Dispose` for a **child** with a live parent thread: `_parentRenderThread.PostRenderAction(RetireSwapchain(_swapchain))`
instead of parking; the park block from §2.4 keeps only the seam/scene releases. When the owning thread is gone (parent
shutting down) the UI thread owns the device and disposes inline (`Drain(ulong.MaxValue-1)` runs in `D3D12Device.Dispose`).
`ClosePopupWindow` (A:4881-4904) likewise posts `RetireSwapchain` + `PurgeRenderActions(swapchain)` **first** (same lock, so
ordering is total) and drops its `Quiesce`. Remaining parks: WndProc `Resize`, `AddSource/RemoveSource`, `Dispose`.

### 5.5 Relation to `QuarantinePolicy.RenderInFlightDepth`

`QuarantineLedger` gates **CPU-slot** reuse on *consumed publications*; `RetireLedger` gates **GPU object** release on
the *fence*. They are the two halves canon §5/§8 already describe. The only shared derivation is the retire queue's capacity
(§5.2): `(RenderInFlightDepth + 1)` turns may each retire a target's worth of objects before the earliest stamp completes.
`SlotQuarantinePolicy` (ImageTextureStore) is unchanged.

---

## 6. Deletions (consolidated, no legacy paths)

Phase 0: `ReleaseStencilDsv()` no-arg; `_hwnd`; `Activate` in `CreateSwapchain`.
Phase 1: `Activate`, `StoreActive`, `_activeSwapchain`, every device working field marked delete/f./ring in §3.3,
`D3D12Swapchain.StencilDsv*`/`FrameIndex`, `SignalFrame`, `WaitForFrame(uint)`, `_gpuExecutionTsOwner*`, `_gpuTsOwner*`,
`_sceneCat` indexing by frame index, `IGpuDevice.{LastFenceWaitMs, LastPresentStoodDown, TextRepaintPending,
SuppressLatencyWaitOnce, SuppressVsyncOnce, HintSettlePresent, LastPresentStats}` (→ `ISwapchain`), `ResetRepaintLedger()` no-arg.
Phase 2: `WaitForLatency`, `WaitForPresentSlot`, `IGpuDevice.WaitForPresentSlot`, `D3D12Swapchain.LatencyCreditHeld`'s
primary-only special-casing (D:1827, D:4833), `RenderThread` ctor args `submitPresent/extraDrain/needsTick/tick/tickPeriod/presentSlotWait`
and `_displayWaits`, `AppHost.{_childRenderSources, DrainChildRenderSources, HasRenderMotion, RenderMotion, RenderPeriodTicks}`.
Phase 3: the `WaitForGpu` calls in `DisposeSwapchain` and `Resize`; the `Quiesce` in `ClosePopupWindow`; the child-Dispose park
around `_swapchain.Dispose()` (Phase 0's) ; `PopupRenderOp/PopupRenderAction` (renamed, not kept alongside).

---

## 7. Gates

### 7.1 VerticalSlice — `detached-render` suite (`src/FluentGpu.VerticalSlice/Suites/DetachedRenderResilienceSuite.cs`; the registry entry at SuiteRegistry.cs:69 already exists)

Add to `Run`: `RecordedOpRingChecks(); TargetFenceLedgerChecks(); WindowPacingSchedulerChecks(); RetireLedgerChecks();`

| Gate name | Assertion |
|---|---|
| `gate.d3d12.forensic.ring-keeps-newest-64` | push 70 entries → `Count == 64`, `At(0).A == 6`, `At(63).A == 69`, `Total == 70` |
| `gate.d3d12.forensic.ring-format-names-target` | push `(SetRenderTargetWithDsv, target 2)` then `(ListClose, target 1)` → `Format` contains `SetRenderTargetWithDsv#2` before `ListClose#1` |
| `gate.d3d12.forensic.push-is-zero-alloc` | `GC.GetAllocatedBytesForCurrentThread()` delta == 0 around 10_000 pushes (the existing tripwire idiom) |
| `gate.render.ledger.own-slots-only` | ledgers A, B (frameCount 3): `A.Stamp(1, 7)` → `B.WaitValueFor(1) == 0`, `A.WaitValueFor(1) == 7`, `A.LastSubmit == 7` |
| `gate.render.ledger.ring-slot-reuse-distance` | `SubmissionRingPolicy.Slot(n, 3) == Slot(n + 3, 3)` and `!= Slot(n + 1, 3)` for n in 0..8 |
| `gate.render.pacing.no-cross-window-wait` | main `{pending, credit}` + child `{pending, no credit, waitable}` → `Decide(main) == Present`, `Decide(child) == Skip`, wait set == `[child]` |
| `gate.render.pacing.credit-once-per-present` | a window with `CreditHeld` is not in the wait set; with `CreditHeld=false` it is |
| `gate.render.pacing.starved-window-reprobed` | `{pending, no credit, waitable}` stays in the wait set on 10 consecutive turns; `TimeoutMs(anyPendingWithoutCredit: true, anyMotionDue: false, …) == 100` |
| `gate.render.pacing.occluded-never-waited` | `{Occluded}` → not in wait set; `Decide == TickOnly` iff `MotionDue` |
| `gate.render.pacing.failed-child-skipped` | `{Failed, pending, credit}` → Skip, not in wait set |
| `gate.render.pacing.unpaced-presents` | `{pending, no waitable}` → Present |
| `gate.render.pacing.idle-blocks-forever` | nothing pending, no motion → `Timeout.Infinite` |
| `gate.render.pacing.wait-set-capped` | 70 windows all eligible, reserved 2 → `BuildWaitSet` returns 62 |
| `gate.render.retire.fifo-by-fence` | enqueue (5,a),(7,b),(7,c) → `TryReclaim(6)` yields a only; `TryReclaim(7)` yields b then c; then false |
| `gate.render.retire.full-refuses` | capacity 8: 9th `TryEnqueue` returns false, `PendingCount == 8` |
| `gate.render.retire.capacity-derived` | `D3D12RetireQueue.Capacity` is Windows-side — gate the *formula* via a pure `RetireCapacityPolicy.For(entriesPerTarget, maxTargets) == entriesPerTarget * (QuarantinePolicy.RenderInFlightDepth + 1) * maxTargets` in Engine |
| existing `gate.d3d12.dsv-per-target`, `gate.image.retire-fence-at-free` | unchanged |

### 7.2 Headless thread-ownership test — `src/FluentGpu.Engine.Tests/RenderOwnershipTests.cs` (xunit; Debug ⇒ FGGUARD)

```csharp
[Fact] public void AssertRenderOwner_ThrowsOnUnparkedUi_PassesUnderQuiesce_AndAfterJoin()
{
    ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
    Assert.Throws<ThreadConfinementViolation>(ThreadGuard.AssertRenderOwner);
    var rt = new RenderThread(new SceneFramePublisher(), _ => { }, async: false);
    rt.Quiesce();   try { ThreadGuard.AssertRenderOwner(); } finally { rt.Resume(); }
    Assert.Throws<ThreadConfinementViolation>(ThreadGuard.AssertRenderOwner);
    rt.Dispose();   ThreadGuard.AssertRenderOwner();   // adopted after the join
    rt.Quiesce(); rt.Resume();                          // still legal after adopt (RenderThreadLifecycleTests does this)
}
[Fact] public void HeadlessDevice_CreateSwapchain_TripsOffTheRenderOwner_WhenConfined()
{
    ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
    var device = new HeadlessGpuDevice(); device.MarkRenderConfined();
    Assert.Throws<ThreadConfinementViolation>(() => device.CreateSwapchain(new SwapchainDesc(default, new Size2(8, 8))));
    var rt = new RenderThread(new SceneFramePublisher(), _ => { }, async: false);
    rt.Quiesce(); try { Assert.NotNull(device.CreateSwapchain(new SwapchainDesc(default, new Size2(8, 8)))); } finally { rt.Resume(); rt.Dispose(); }
}
```

Requires `HeadlessGpuDevice` (Headless/Rhi/HeadlessGpuDevice.cs:164) to implement `MarkRenderConfined()` and open
`CreateSwapchain`/`HeadlessSwapchain.Dispose/Resize` with `if (_renderConfined) ThreadGuard.AssertRenderOwner();` — inert
for every existing headless host (AppHost never arms it on `SingleThread`, A:2708-2712 + A:3073).

### 7.3 Live stress probe — `src/FluentGpu.WindowsApp/Probes/DetachedStressProbe.cs`

Invocation (CLI argument, no environment switch): `dotnet run --project src/FluentGpu.WindowsApp -c Debug -- --detached-stress [outDir] [--cycles 300] [--seed 1]`.
`Program.cs` (next to `--drm-autoplay`, :213, which is the precedent for a CLI flag arming an existing diagnostics env
gate): on `--detached-stress`, `Environment.SetEnvironmentVariable("FG_D3D12_DEBUG", "1")` **before** `FluentApp.Run`
(arms the debug layer + info queue, D:1188-1200/1290-1328 — Debug builds only), set `DetachedStressProbe.Args = …`, and
`FluentApp.DiagnosticRun = DetachedStressProbe.TryRun` (the `SoakProbe.TryRun` slot, Program.cs:195 — chain both:
`h => DetachedStressProbe.TryRun(h,w,d) || SoakProbe.TryRun(h,w,d)`).

The probe (pattern: `SoakProbe.RunSoak`, SoakProbe.cs:44-80, and the scroll drive of `DialogScrollProbe`):

1. `Console.SetError(new TeeWriter(originalStderr, File.CreateText(outDir/"detached-stress.log")))` — the debug layer's
   `[d3d12.debug]` lines (D:494) and `Diag.Line` (Diag.cs:43-47, stderr when no sink) land in the file.
2. Navigate the gallery to a long-list page (`GalleryShell.StressNavKeys`), start a continuous scroll through the scroll
   kernel, and pump `host.RunFrame()` + `window.WaitForWork` as the interactive loop does.
3. Every 50–1500 ms (seeded `Random`), open a detached window via `host.OpenDetachedWindow(new DetachedWindowRequest(
   "stress", new Size2(480, 270), Content: new StressChild()))` — `StressChild` draws a rounded plate, text, and a
   `PushStencilClip` path (the DSV path the incident rode) — place it on the *other* monitor (`DisplayInfo` enumeration →
   `IDetachedVideoWindow.SetBounds` into that monitor's work area, alternating), keep it 50–1500 ms, `Close()`, and let
   `TickDetachedHosts` reap it. Repeat `--cycles` times (default 300).
4. Measure, per 1 s window while a child is open: main-window presented fps (`FrameStats.PresentedFrames` delta),
   `FrameStats.FenceWaitMs` p95, `GC.GetAllocatedBytesForCurrentThread()` delta on the render thread per turn (exposed
   via a probe-only counter on `RenderThread`, sampled through `FrameStats`), the count of `[d3d12.forensic]`,
   `[detached] child frame failed`, `[d3d12.stall]`, and `[d3d12.debug] … ERROR|CORRUPTION` lines (the tee writer counts
   them as they pass).
5. Exit 0 iff: zero debug-layer ERROR/CORRUPTION lines, zero `Close` failures/forensic lines, zero child render failures,
   zero device-lost recoveries, render-thread allocation delta 0 in steady state, and main-window fps ≥ 0.9 × the primary
   monitor's refresh (≥ 108 at 120 Hz) over the scroll windows with a child open on a different-refresh monitor (Phase 2
   criterion; Phase 0/1 runs report it without failing on it). The summary line is `[detached-stress] cycles=300 fps.main.p50=…
   fenceWait.p95=… debugErrors=0 closeFailures=0 childFailures=0 allocBytes=0 verdict=PASS`.

Owner runs it after each wave on the two-monitor setup (120 Hz primary + 99.98 Hz secondary); it is not CI.

### 7.4 Gates that must stay green

`dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED` (zero-alloc phases 6–13, `scroll`/`kernel`
suites, `wake-present`, `damage`, `layerpool`, `detached-render`); `dotnet test src/FluentGpu.Engine.Tests`
(`RenderThreadLifecycleTests`, `RenderLifecycleTests`, `RefreshLatticeTests`, new `RenderOwnershipTests`);
`dotnet test src/FluentGpu.Windows.Tests` (`FrameBankingTests`, `RenderDisplayClockTests`, `PacedInputWaitClassifierTests`);
Debug **and** Release builds clean (`TreatWarningsAsErrors`); `--screenshot` unchanged pixels; `powershell -File docs/design/check-canon.ps1` exit 0.

---

## 8. Design docs to reconcile (canon)

| Doc | Section | Change |
|---|---|---|
| `docs/design/subsystems/pal-rhi.md` | §0 (thread ownership), §2 `ISwapchain` (new members: `PresentSlotWaitHandle`, `PresentCreditHeld`, `NotePresentCreditTaken`, `LastFenceWaitMs`, `Suppress*Once`, `HintSettlePresent`, `LastPresentStats`, `TextRepaintPending`, `LastPresentStoodDown`), §3 (SubmissionRing: allocators/banks keyed by submission, one list per target), §5.3 (per-frame present: credits), §5.4 (resize waits on the target's own `LastSubmitFence` — as designed, now as built), §5.5 multi-window (per-target `TargetFrameState`, `RetireLedger`, the one wait), §9 table row "Swapchain, DComp tree… RENDER" → "render, or the UI thread holding the loop parked/joined (`ThreadGuard.AssertRenderOwner`)" |
| `docs/design/subsystems/threading-render-seam.md` | §1.1 table (the ComPtr row: add the parked-UI owner form; add rows for `TargetFrameState`, `SubmissionRing`, `RetireLedger`), §1.2 `ThreadGuard` (ownership token API), §8 (retire-fence handshake → `RetireLedger`/`D3D12RetireQueue` as built), §11.1 (multi-window wait replaces the single `WaitForPresentSlot`; credits per window), §18 test ledger (add `RenderOwnershipTests`, the pacing gates) |
| `docs/design/subsystems/gpu-renderer.md` | §13 frames-in-flight bullet (banks keyed by `SubmissionRing` slot; back buffers by target index; one list per target), §13.1 (canvas ledger is per primary target) |
| `docs/design/SPEC-INDEX.md` | §2: new row **Multi-window render isolation** (owner `pal-rhi.md` §5.5: TargetFrameState + SubmissionRing + WindowPacingScheduler + RetireLedger + the render-owner rule); amend the render-thread-seam row's summary ("the render thread — or the UI thread holding it parked — owns every ComPtr") |
| `docs/design/subsystems/README.md` | ownership map: `TargetFrameState`/`SubmissionRing`/`RawWaitHandle` → pal-rhi.md; `WindowPacingScheduler`/`RetireLedger`/`RecordedOpRing`/`TargetFenceLedger` → threading-render-seam.md |
| `docs/guide/` (the `fluentgpu` skill file map) | D3D12 "where to change what": target state lives on `TargetFrameState`, banks on `SubmissionRing` |
| `docs/plans/detached-window-render-isolation-plan.md` | status line → "implemented by …-implementation.md"; note §1.7's corrections |

Run `powershell -File docs\design\check-canon.ps1` after every doc edit (the `spotlight-dim`/`bind-props` style rules —
none of the new terms collide, but the gate is the proof).

---

## 9. Execution partition (waves of parallel subagents on disjoint files)

The orchestrator alone builds/tests between waves: `dotnet build src/FluentGpu.slnx` **and** `-c Release`;
`dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`; `dotnet test src/FluentGpu.Engine.Tests`;
`dotnet test src/FluentGpu.Windows.Tests`; then `dotnet build C:\wavee\WaveeMusic\Wavee.slnx` (Debug + Release) whenever a
seam member moved; the owner runs `--detached-stress` and the Wavee cycle. The user's running Debug Wavee locks Debug
outputs (MSB3021/27) — build to a side folder or ask first.

**Merge hazards.** `D3D12Device.cs` (418 KB) and `AppHost.cs` (452 KB) have exactly **one** owner per wave. `Rhi.cs` has
one owner per wave. `DetachedRenderResilienceSuite.cs` has one owner per wave. Pure classes are new files. Agents write
against the APIs *as printed in this plan* so independently-written halves compile together; the orchestrator resolves
signature drift at the wave build, never by a second agent editing the same file.

### Wave 0 — Phase 0 (ships alone)

| Agent | Files (exclusive) | Work |
|---|---|---|
| 0-A | `Engine/Hosting/Threading/ThreadGuard.cs`, `Engine/Hosting/Threading/RenderThread.cs`, `Engine/Headless/Rhi/HeadlessGpuDevice.cs`, `Engine.Tests/RenderOwnershipTests.cs` (new) | §2.1, §2.2, §7.2 |
| 0-B | `Windows/D3D12/D3D12Device.cs`, `Engine/Rhi/RecordedOpRing.cs` (new) | §2.3 (all), §2.6 |
| 0-C | `Engine/Hosting/AppHost.cs`, `VerticalSlice/Suites/DetachedRenderResilienceSuite.cs`, `C:\wavee\WaveeMusic\src\apps\Wavee\Platform\Platform.Host.cs` (+ Wavee CHANGELOG bullet with the issue ref) | §2.4, §2.5, the three `gate.d3d12.forensic.*` checks |
| 0-D | `WindowsApp/Probes/DetachedStressProbe.cs` (new), `WindowsApp/Program.cs` | §7.3 |

Then: build both configs, VerticalSlice, both test projects, Wavee build; owner: `--detached-stress` (Debug, debug layer)
and the Wavee repro cycle. **Phase 0 is releasable here** (Wavee hotfix); Phases 1–3 are one refactor of the target model.

### Wave 1 — Phase 1

Sequenced inside D3D12Device by region (one agent, three commits so a failed build bisects):
1-B1 fields + `TargetFrameState`/`SubmissionRing` + `BeginTargetFrame` + `SubmitDrawList` entry/exit + waits/signal;
1-B2 stencil + layer pools + damage ledger moves; 1-B3 present/stats/probe partials + `RecoverDevice`/`Dispose`/`Resize`.

| Agent | Files | Work |
|---|---|---|
| 1-A | `Engine/Seams/Rhi/Rhi.cs`, `Engine/Headless/Rhi/HeadlessGpuDevice.cs`, `Engine/Rhi/TargetFenceLedger.cs` (new), `VerticalSlice/Suites/DetachedRenderResilienceSuite.cs`, `VerticalSlice/Suites/LayoutShellSuite.cs` (:1337 read) | seam moves (§3.3 rows marked ISwapchain), ledger gates |
| 1-B | `Windows/D3D12/D3D12Device.cs`, `D3D12Device.MemoryProbe.cs`, `D3D12Device.SmallImagePoolProbe.cs`, `D3D12Device.SmallTextureProbe.cs`, `TargetFrameState.cs` (new), `SubmissionRing.cs` (new) | §3.1–3.5 |
| 1-C | `Windows/D3D12/AcrylicCompositor.cs`, `OpacityLayerCompositor.cs`, `BakedBlurCompositor.cs`, `GlyphRenderer.cs`, `UploadArena.cs`, pipelines (`*Pipeline.cs`) | bank parameter semantics → "ring slot" (rename `frameIndex` params/comments; no behaviour change), `EnsureLayerPools` support (no static device singletons assumed) |
| 1-D | `Engine/Hosting/AppHost.cs`, `Engine/Hosting/Threading/SceneRenderFrame.cs`, `Windows.Tests/FrameBankingTests.cs` | call sites: `_swapchain.Suppress*Once/HintSettlePresent/LastFenceWaitMs/TextRepaintPending/LastPresentStoodDown`; the FrameBanking assertion |

### Wave 2 — Phase 2

| Agent | Files | Work |
|---|---|---|
| 2-A | `Engine/Hosting/Threading/WindowPacingScheduler.cs` (new), `Engine/Hosting/Threading/IRenderSource.cs` (new), `VerticalSlice/Suites/DetachedRenderResilienceSuite.cs` | §4.1 + the 8 pacing gates |
| 2-B | `Engine/Hosting/Threading/RenderThread.cs`, `Engine/Hosting/AppHost.cs`, `Engine.Tests/RenderThreadLifecycleTests.cs` | §4.3 loop + `IRenderSource` on AppHost + deletions; tests updated to construct with sources |
| 2-C | `Windows/D3D12/D3D12Device.cs`, `Windows/D3D12/RawWaitHandle.cs` (new), `Engine/Seams/Rhi/Rhi.cs`, `Engine/Headless/Rhi/HeadlessGpuDevice.cs` | §4.2; delete `WaitForLatency`/`WaitForPresentSlot`; credits for every target |

### Wave 3 — Phase 3

| Agent | Files | Work |
|---|---|---|
| 3-A | `Engine/Hosting/Threading/RetireLedger.cs` (new), `Engine/Hosting/Threading/RetireCapacityPolicy.cs` (new), `VerticalSlice/Suites/DetachedRenderResilienceSuite.cs` | §5.1 + retire gates |
| 3-B | `Windows/D3D12/D3D12RetireQueue.cs` (new), `Windows/D3D12/D3D12Device.cs`, `AcrylicCompositor.cs`, `OpacityLayerCompositor.cs` | §5.2, §5.3 |
| 3-C | `Engine/Hosting/AppHost.cs`, `Engine/Hosting/Threading/RenderThread.cs` | §5.4 (mailbox generalization, child/popup teardown posts) |

### Wave 4 — canon + docs (one agent per doc; then `check-canon.ps1`)

`pal-rhi.md`, `threading-render-seam.md`, `gpu-renderer.md`, `SPEC-INDEX.md` + `subsystems/README.md`, the guide file map,
the design plan's status line (§8).

---

## 10. Risks, rollback, open questions

**Risks.** (1) Phase 1 touches ~200 sites in `D3D12Device.cs`; the alias rule (§3.3) keeps most of them a spelling change,
but a missed `_frameIndex` → `slot` at a bank site is a silent CPU/GPU tear — the Debug tripwire cannot see it; the
`--detached-stress` allocation/error counters and `--screenshot` are the evidence. (2) The `SubmissionRing` at depth 3 with
two windows presenting every turn reuses a slot 1.5 turns later; on a GPU more than a turn behind, the main waits on the
child's older submit (bounded by design; widening to 4 costs one more `UploadArena` bank and a `FrameBankingTests` edit).
(3) Phase 2 changes who blocks where: popups become unpaced composition presents (they were paced by the primary's
credit only by accident); verify flyout reveal in the gallery `--screenshot --mica` shot list. (4) `WaitHandle.WaitAny`
over a DXGI handle wrapped with `ownsHandle: false` — the handle dies with the swapchain; the wrapper must be dropped from
the wait set (RemoveSource under park) **before** `DisposeSwapchain` (§5.3 orders it).

**Rollback.** Each phase is one commit series on its own branch; Phase 0 stands alone (revert = the three files + the
Wavee line). Phases 1–3 revert as a unit (the target model is one refactor); Phase 2 can be reverted independently of 3.

**Open questions for the owner (genuinely undecidable here).**
1. Ship Phase 0 as a Wavee 0.3.x hotfix before Phases 1–3 (release cadence), or hold for the full refactor?
2. `SubmissionRing.Depth`: keep 3 (no memory growth, `FrameBankingTests` untouched) or 4 (one more bank ≈ +1/3 of
   `UploadArena.InitialBytes` per process; the main window waits on a submit two turns old instead of 1.5)?
3. Creation stays a synchronous `Quiesce`d UI-thread call (this plan). Should it instead become a posted render-thread
   action with a completion handshake so the WUC `CompositionBackdrop` (InitSwapChain D:1489, created UI-side today) and
   the swapchain are born on the presenting thread? No observed defect motivates it; it would add a rendezvous.
4. Stalled-compositor policy in Phase 2 (`TimeoutMs` re-probe at 100 ms vs today's 1000 ms bounded wait per window): a
   window whose compositor stops retiring presents is simply not presented until its waitable fires — confirm that a
   pop-out on a monitor that is powered off should freeze (not fall back to unpaced presents).
