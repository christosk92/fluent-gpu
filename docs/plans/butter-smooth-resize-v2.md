# Butter-smooth resize v2 — code-verified implementation plan

Status: PROPOSED (supersedes the v1 "Butter-smooth resize" draft plan after a five-way code recon).
Scope: `src/FluentGpu.Engine`, `src/FluentGpu.Windows`, `src/FluentGpu.VerticalSlice`, `src/apps/Wavee` (breakpoints only), `design/`.

---

## 0. What the recon changed — read this first

The v1 plan was written as if the composited-defer architecture still had to be built. It doesn't.
**Defer-on-composited is already the shipped default**: `FG_LIVE_MODAL_RESIZE` is a legacy *opt-back-in*
to live per-step resize, not a gate on defer (`Win32Platform.cs:456-458`, `AppHost.cs:238-240`). The
`WM_SIZE`/`WM_TIMER`/`WM_MOVE` composited-modal skips, `DeferModalResize`, `LayoutSizeForFrame`,
`SuppressVsyncOnce`/`SuppressLatencyWaitOnce` on every keep-alive paint (including the settle paint),
the FLIP-skip-on-resize guard, the modal idle-skip with `OnlyAmbientWakeReasons`, and the
`SpanReuseDisabledReason.Resize/ModalPaint` bits **all exist and are live**.

Phase-by-phase disposition of the v1 plan:

| v1 phase | Verdict | Why (evidence) |
| --- | --- | --- |
| P1 defer hardening | **KEEP (small)** — delete the flag, add the missing `Composited` check | Defer exists; but `AppHost.DeferModalResize` (AppHost.cs:2222-2225) omits `Composited` — a real bug, see §1.1 |
| P2 DComp pin-top-left stretch | **CUT the matrix; keep a verification step + optional edge-anchor** | Pin-top-left is already the *default physics* of a composition swapchain — no `SetTransform` exists or is needed (§2) |
| P2 ModalPresentationQueue | **CUT (for now)** | WndProc and the presenting thread are the *same thread* today (no render thread by default); reserve the queue design for the async render thread track (§2.3) |
| P3 settle quality | **CONVERT to gates** — everything it asks for already holds | `SuppressVsyncOnce`+`SuppressLatencyWaitOnce` fire on all keepAlive paints (AppHost.cs:1679, 401-411); FLIP skips on `resized` (AppHost.cs:1385-1394) |
| P3 `HadSlots` remount-no-stagger | **CUT** | Can't work as specified — a tier remount creates a *new* `VirtualEntry`, so `HadSlots` is always false at the moment it matters; visible rows are already guaranteed by `minVisibleTarget` (Reconciler.cs:1363-1376) (§3.2) |
| P4 non-composited 30 Hz | **KEEP, simplified** — timestamp throttle riding the existing 8 ms timer; delete the coalesced-post machinery | §1.3 |
| P4 `RecordPass.ModalLive` | **DEFER pending measurement** | Non-composited = gallery only; Wavee is composited and never live-records mid-drag (§4) |
| P5 render thread always-on | **CUT from this plan** | Force-sync is documented in-code as "zero perf win" (RenderThread.cs:7-17) — UI still blocks in `DrainSync`; the actual win (async) is default-off due to a known DComp dim-composite bug (AppHost.cs:241-252). Separate workstream: root-cause that bug. (§5) |
| P6.1 `InModalResize` freeze | **CUT** | Dead code for Wavee: composited defer means layout (and therefore `OnBoundsChanged`, which fires *inside* Arrange — FlexLayout.cs:129-150) never runs mid-drag. Per-callsite app guards are the per-control-patch anti-pattern; defer is the root fix. |
| P6.2 breakpoint hysteresis | **KEEP** | Cheap, protects the non-modal reflow paths (rail open/close edge, programmatic resize); `DetailShell.ModeFor` already half-does this (540/580 vertical band, DetailShell.cs:73-85) (§6) |
| P7.1 layout width-bucket cache | **CUT** | Three independent fatal flaws: bucket quantization applies a wrong-width layout (up to 31 DIP misfit); no `TreeEpoch` exists to key it (grep: only `DynamicTextEpoch`); replaying a bounds snapshot skips `OnBoundsChanged`/virtual-realize/sticky side effects that only the Arrange pass produces (§7.1) |
| P7.2 damage-only settle record | **CUT** | Self-contradicting: the plan itself sets damage = full viewport on resize, so the prune set is empty by construction; and there is no cached per-node device-bounds column to prune against (device bounds are computed *during* the walk — SceneRecorder.cs:437) (§7.2) |
| P7.3 `DwmFlush` on settle | **KEEP as experiment** | No DwmFlush exists anywhere today; cheap to try, honest about what it can/can't fix (§7.3) |
| P8 WM_SIZING | **FOLD into optional edge-anchor (§2.3)**; cut the layout-warm idea (depended on 7.1) | No WM_SIZING handler exists today |
| P9 gates | **KEEP, renamed** | Convention is `Check("RZ-XX. …", cond, diag)`, not dotted names (Program.cs:5802-5833); plus an audit of existing modal gates (§8) |
| P10 design docs | **KEEP** | §9 |

The result is a much smaller plan: one real bug fix, one deletion, one simplification, one app-side
hardening, gates, and two optional experiments — instead of ten phases.

**Addendum (owner decision)**: every env *behavior* flag in the tree is retired — not just the resize
ones — and new env opt-in flags are permanently banned, with a mechanical ratchet so the ban holds.
See §12; it adjusts §5's disposition of `FG_RENDER_THREAD`/`FG_RENDER_ASYNC` (the env reads go now;
the seam survives as an internal constructor option).

---

## 1. Phase 1 — Delete `FG_LIVE_MODAL_RESIZE`, fix the non-composited defer bug, simplify throttling

### 1.1 The bug the v1 plan almost found

`AppHost.DeferModalResize` (AppHost.cs:2222-2225):

```csharp
private bool DeferModalResize(bool keepAlive)
    => !s_liveModalResize && keepAlive && _window.InModalLoop;
```

There is **no `Composited` check**. The window-side `WM_SIZE` gate (`Win32Platform.cs:996-1010`)
checks `_composited && _inMoveSizeLoop`, so a **non-composited** window in a modal edge-drag fires
`PaintRequested` per step — and each of those paints hits `DeferModalResize == true`, so
`EnsureSize` returns false, layout runs at the **stale** `_lastSize`, and the HWND swapchain
(`CreateSwapChainForHwnd` + `DXGI_SCALING_STRETCH`, D3D12Device.cs:523/540) stretches the old-size
buffer to the new window every step. Non-composited windows currently pay full per-step paint cost
*and* get blurry stretched output. The fix is exactly the v1 plan's signature:

```csharp
private bool DeferModalResize(bool keepAlive)
    => keepAlive && _window.InModalLoop && _window.Composited;
```

### 1.2 Exact edits

**`src/FluentGpu.Engine/Seams/Pal/Pal.cs`** — add to `IPlatformWindow` (near `InModalLoop`, line ~458):

```csharp
/// <summary>True when the window's pixels are a DComp flip surface (WS_EX_NOREDIRECTIONBITMAP).
/// Drives the modal-resize policy: composited windows defer GPU resize + relayout to mouse-up
/// (DWM tracks the HWND; content pins top-left); non-composited windows must live-paint (throttled).</summary>
bool Composited => false;

/// <summary>True once the current modal loop has delivered a WM_SIZE (edge resize, not pure move).</summary>
bool SizedInModalLoop => false;
```

(`SizedInModalLoop` is only needed if any consumer materializes — see §6; add it only when used.)

**`src/FluentGpu.Windows/Pal/Win32Platform.cs`**:

1. Delete `s_liveModalResize` (line 458) and every branch on it.
2. `public bool Composited => _composited;` (next to `InModalLoop`, line 584).
3. `WM_SIZE` (lines 996-1010) collapses to:

```csharp
if (_inMoveSizeLoop)
{
    if (_composited) return true;                       // defer: settle paints on WM_EXITSIZEMOVE
    if (!ThrottleModalResizePaint()) PaintRequested?.Invoke();
    return true;
}
PaintRequested?.Invoke();                               // programmatic resize outside the modal loop
return true;
```

4. `WM_TIMER` / `MoveLoopTimerId` (lines 1067-1082) — final form defined in §13.2 (the move-liveness
   phase changes this handler: composited **resize** stays zero-paint, composited **pure move** gets
   throttled ambient ticks, non-composited gets the 30 Hz live throttle):

```csharp
if (_composited && _inMoveSizeLoop && _sizedInMoveSizeLoop) return true;   // composited RESIZE: full defer
if (_inMoveSizeLoop && ThrottleModalTickPaint()) return true;              // move ticks + non-composited live: ≤30 Hz
PaintRequested?.Invoke();
return true;
```

5. Delete `WM_FG_COALESCED_SIZE_PAINT` (const line 169, handler lines 1013-1018) and `_sizePaintPosted`
   (line 455, plus the reset in `WM_EXITSIZEMOVE` line 1064). The trailing-edge paint the posted message
   provided is now guaranteed by the 8 ms `MoveLoopTimerId` timer itself (it fires ≤ 8 ms after the
   throttle window closes) plus the unconditional `WM_EXITSIZEMOVE` settle paint.

6. Add the throttle helper (Engine-testable core, see §8 note on the TerraFX-free closure):

```csharp
private long _lastModalSizePaintMs;
private const int ModalResizeMinIntervalMs = 33;   // ~30 Hz live relayout for redirection-bitmap windows

private bool ThrottleModalResizePaint()   // true = skip this paint
{
    if (!_sizedInMoveSizeLoop) return false;   // pure move: keep-alive cadence unthrottled (idle-skips in AppHost)
    long now = Environment.TickCount64;
    if (now - _lastModalSizePaintMs < ModalResizeMinIntervalMs) return true;
    _lastModalSizePaintMs = now;
    return false;
}
```

   Reset `_lastModalSizePaintMs = 0` in `WM_ENTERSIZEMOVE` so the first step of a new drag paints
   immediately.

**`src/FluentGpu.Engine/Hosting/AppHost.cs`**:

1. Delete `s_liveModalResize` (lines 238-240) — note there are **two independent statics** reading the
   same env var (one per assembly); both go.
2. `DeferModalResize` → the §1.1 signature.

**`src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs`**:

`HeadlessWindow` currently drops `desc.Composited` on the floor. Store it and expose it settable
(same test-seam pattern as `InModalLoop`, line 67-68):

```csharp
public bool Composited { get; set; }   // test seam: model a Mica/DComp window (WindowDesc.Composited)
```

Initialize from `desc.Composited` in the ctor.

### 1.3 Behavior table after Phase 1 (no env flags)

| Window | Move | Edge resize | Mouse-up |
| --- | --- | --- | --- |
| Composited | zero geometry paints (DWM relocates surface); ambient animation ticks ≤30 Hz (§13) | zero paints; content pins top-left, Mica fills the void on grow, DComp target clips on shrink | one settle paint (`WM_EXITSIZEMOVE`, keepAlive, unsynced present) |
| Non-composited | per-step paint (unchanged; cheap — idle-skip eats redundant ones) | ≤ ~30 Hz live relayout+paint (WM_SIZE + timer share one timestamp gate); DWM stretch between paints | already live; settle paint still fires |

### 1.4 Edge cases (all verified against current code)

- `SIZE_MINIMIZED`: already ignored before any modal logic (Win32Platform.cs:984).
- **WM_DPICHANGED mid-drag** (cross-monitor drag): handler updates `_scale`, `SetWindowPos`, then
  `PaintRequested` (lines 1019-1033). Under composited defer that paint hits `DeferModalResize` and
  renders one consistent frame at the *stale* `_lastSize/_lastScale` (LayoutSizeForFrame,
  AppHost.cs:2229-2237); the settle applies everything. **No code change needed** — there is no
  transform to reset (v1's "forces reset + paint" is moot once the stretch matrix is cut).
- **Aero-snap / maximize from drag**: `WM_EXITSIZEMOVE` precedes the maximize `WM_SIZE`; defer is
  poll-late (settle reads `_window.ClientSizePx` fresh in `EnsureSize`), so any odd message ordering
  self-heals at settle. This statelessness is a design strength — say so in the design doc.
- `EnsureSize` deliberately does **not** advance `_lastSize/_lastScale` while deferring
  (AppHost.cs:2252-2255), so the settle diff always fires. Preserve this.

---

## 2. Phase 2 — Drag-visual verification, and the optional left/top edge anchor

### 2.1 Why the v1 stretch matrix is wrong

The v1 plan's `ApplyModalPresentationStretch` computes `sx = presentedW / clientW` — a *counter-scale*
premised on DWM stretching the swapchain content to the new window size. That premise belongs to the
**HWND** swapchain path (`DXGI_SCALING_STRETCH` on `CreateSwapChainForHwnd`). A **composition**
swapchain bound to a DComp visual (`BindDComp`, D3D12Device.cs:600-614 — `SetContent` + `SetRoot` +
one `Commit`, no transform anywhere) displays its buffer 1:1 at the visual origin; the HWND's DComp
target clips it to the client area. On grow: content pinned top-left, Mica (the window is
glass-extended full-client, Win32Theme.cs) fills the uncovered region. On shrink: clipped. That *is*
pin-top-left — it's free, and applying the v1 matrix would have visibly shrunk the content instead.

Confidence: high but empirical — so the first task is a 15-minute verification, not code.

### 2.2 Verification protocol (do this before any Phase 2 code)

Run Wavee (composited) and the gallery (non-composited), screen-record at 60+ fps, and drag each of
the 8 edges/corners plus a titlebar move, with playback active:

1. Composited grow (right/bottom): expect content frozen top-left, Mica void trailing the edge, no stretch.
2. Composited shrink: expect clean clip, no artifacts.
3. Composited **left/top edge drag**: expect the known artifact — content translates with the window
   (window origin moves; content is window-anchored, not screen-anchored).
4. Non-composited: ~30 Hz live relayout with DWM stretch between steps.
5. Mouse-up on each: one settle frame; note the Mica re-derive snap.

If (1)/(2) show anything else (i.e. DWM *does* stretch), only then revisit a corrective transform —
with the measured behavior in hand.

### 2.3 Optional: screen-anchored content on left/top edge drags (ship-if-it-feels-better)

This is the one thing a DComp transform genuinely buys during a deferred drag. When the user drags
the left edge outward by `d` px, the window origin moves left by `d`; pinned-top-left content slides
left with it. Counter-translating the visual by `+d` keeps content screen-stationary (the macOS feel).

Design (only if §2.2 confirms the artifact is objectionable):

- **PAL**: handle `WM_ENTERSIZEMOVE` → stamp `_modalOriginStart = ClientOriginPx`. On `WM_SIZE`
  during composited modal, compute `delta = ClientOriginPx - _modalOriginStart` and invoke a new
  host-wired callback `Action<Point2>? ModalOriginShift { get; set; }` (same wiring pattern as
  `PaintRequested` — the PAL window must **not** hold an `IGpuDevice`; the v1 plan's
  `IPlatformWindow.GpuDevice { get; set; }` inverts the PAL/RHI seam and is rejected).
  `WM_SIZING` (0x0214) is *not* required: the origin delta alone identifies left/top drags.
- **RHI**: two default-no-op members on `IGpuDevice`:

```csharp
/// <summary>During a deferred composited modal resize: translate the presented content so it stays
/// screen-anchored while the window origin moves (left/top edge drags). Presenting-thread-owned.</summary>
void SetModalContentOffset(float xPx, float yPx) { }
void ResetModalContentOffset() { }
```

- **D3D12**: `SetOffsetX/SetOffsetY` on `DcompVisual` + `Commit`. Guard: no-op while
  `DcompBindPending` or the visual is null. `ResetModalContentOffset` zeroes and commits.
- **Wiring**: `AppHost` ctor sets `_window.ModalOriginShift = d => _device.SetModalContentOffset(d.X, d.Y);`
  and calls `_device.ResetModalContentOffset()` inside `EnsureSize` immediately before
  `_swapchain.Resize(s)` — the reset and the settle frame are then atomic from DWM's perspective
  (both land in the settle commit/present).
- **Threading**: today WndProc, the frame loop, and Present all run on the **same thread**
  (FluentApp.Run's loop thread; no render thread by default — AppHost.cs:1044-1053 requires
  `FG_RENDER_THREAD`/`FG_RENDER_ASYNC`). So direct calls are confinement-safe and the v1
  `ModalPresentationQueue` is unnecessary **now**. Record in `threading-render-seam.md`: if/when the
  async render thread ships, these two calls become render-confined and need an SPSC job + explicit
  render-thread wake (a frame-publish-drain is NOT sufficient — no frames publish mid-drag, so jobs
  would starve; this was a latent flaw in the v1 queue design too).
- **Honest caveat**: DWM moves the window immediately; our commit lands one composition frame later →
  ~1 frame of content jitter against the moving left edge. Chromium has the same. Evaluate by feel;
  keep pin-top-left if it doesn't clearly win.

---

## 3. Phase 3 — Settle-frame quality: verify, don't build

### 3.1 Already true (convert each to a gate, §8)

- Settle paint is `keepAlive: true` with `InModalLoop == false` (flags cleared before the
  `PaintRequested` in `WM_EXITSIZEMOVE`, Win32Platform.cs:1058-1066) → `EnsureSize` performs the real
  `WaitForGpu → ResizeBuffers → _needFullLayout` (AppHost.cs:2244-2268; D3D12Device.cs:1837-1861).
- `SuppressVsyncOnce` + `SuppressLatencyWaitOnce` fire on every keepAlive submit, inline or
  render-thread (AppHost.cs:1679 and 401-411 via `RenderFrame.SuppressVsync`).
- FLIP capture is skipped when `resized` (AppHost.cs:1385-1394) — resizes snap by design.
- Span reuse is disabled on the settle frame via both `Resize` and `ModalPaint` bits
  (AppHost.cs:1607-1616); skip-submit (`maybeUnchanged`) requires `!resized && !keepAlive`
  (AppHost.cs:1645) so the settle always presents.

### 3.2 The `HadSlots` idea is rejected — reasoning for the record

A tier change remounts via a keyed diff (`Key = "tier:" + tier`): the old viewport node is removed
and a **new** node mounts → a **new** `VirtualEntry` (keyed by `NodeHandle`, Reconciler.cs:42). A
`HadSlots` flag on the entry is therefore always false exactly when the v1 plan wanted it true.
And it isn't needed: `minVisibleTarget = Math.Clamp(visibleSlots, 0, w)` (Reconciler.cs:1365) already
guarantees every *visible* row realizes on the mount frame even under stagger — only overscan warms
across frames. The existing `RZ-TIER` gate (Program.cs:5802-5806) asserts exactly this. Wavee
additionally opts out entirely (`staggerColdRealize: false`, DetailTracks.cs:284/330/426). No engine
change.

---

## 4. Phase 4 — `RecordPass.ModalLive`: deferred pending measurement

The live-record skip tier (drop shadows / blur / edge-fade during live resize) would only ever run on
**non-composited** windows — Wavee is composited and never records mid-drag. The gallery's scenes are
far lighter than Wavee's 1706-node detail page. Before building a record-pass concept into
`SceneRecorder.Record` (which today has no pass/mode axis — behavior is driven by the
`SpanReuseDisabledReason` mask and per-call booleans, SceneRecorder.cs:154-158), measure a gallery
live-resize with `FG_RESIZE_DIAG` at the new 30 Hz cadence. Only if p95 step cost exceeds ~8 ms:

- Add `RecordPass { Normal, ModalLive, Settle }` as a parameter threaded through `Walk` (bundle into
  the existing walk parameter set; stack-only, zero alloc).
- `ModalLive` skips: `dl.Shadow` (SceneRecorder.cs:599-600), `PushBlurLayer` when
  `BlurCachePolicy != Normal` (586-587), `PushEdgeFadeLayer` (575-581). Acrylic stays (its re-blur
  suppression is already handled by `AcrylicBackdropMath.BackdropReusable`; note the stamp's
  `CanvasW/H` fields force re-blur on every size change anyway — skipping the *layer push* would
  white-flash the backdrop, worse than paying it at 30 Hz).

---

## 5. Phase 5 (v1) — render thread: cut, and what to do instead

Force-sync mode is self-documented as "zero perf win" (RenderThread.cs:7-17): the UI thread blocks in
`DrainSync` until the render thread has presented, so settle latency is identical to inline submit —
it adds a thread hop, a second failure surface (quiesce/resume rendezvous around `ResizeBuffers`,
AppHost.cs:2259-2264), and device confinement asserts, for nothing the user can feel. Spawning it by
default is risk without reward.

The mode that *would* matter for resize (and everything else) is **async** (`FG_RENDER_ASYNC`), which
is deliberately default-off: presenting from the render thread to the DComp-composited swapchain
produces a documented DIM/wrong on-screen composite (AppHost.cs:241-252). The correct successor work
item — **outside this plan** — is root-causing that composite bug (prime suspects: DComp device
bind/commit thread affinity vs. the `BindDComp`-on-presenting-thread contract, D3D12Device.cs:595-614,
and premultiplied-alpha handling). Until then, defer means there are no mid-drag presents to
parallelize anyway.

**Env-flag disposition (per §12)**: `FG_RENDER_THREAD` and `FG_RENDER_ASYNC` are deleted *now*, not
kept dormant. The seam itself must stay testable (the race gates and the future async workstream need
it), so the spawn decision moves to an internal constructor option:

```csharp
internal enum RenderLoopMode : byte { Inline, ForceSync, Async }
// AppHost ctor gains: internal AppHost(..., RenderLoopMode renderLoop = RenderLoopMode.Inline)
```

`FluentApp.Run` always passes the default (`Inline` — today's shipped behavior); the VerticalSlice
exercises `ForceSync`/`Async` directly for the seam gates (it already refs Engine internals via the
same assembly boundary — if not, add `InternalsVisibleTo("FluentGpu.VerticalSlice")`). When the async
composite bug is fixed and its gates are green, flip the *default* to `Async` and delete `Inline` —
one experience, no fork left behind. `WaveeResizeProbe.cs:92` reads `FG_RENDER_ASYNC` today and must
be updated with this change.

---

## 6. Phase 6 — Breakpoint hysteresis (app-side, `src/apps/Wavee`)

Keep hysteresis; cut the `InModalResize` freeze (dead code under composited defer — `OnBoundsChanged`
fires synchronously inside Arrange, FlexLayout.cs:129-150, and Arrange doesn't run mid-drag; the root
fix already covers it, and per-callsite guards are the per-control-patch pattern this codebase
deliberately avoids).

### 6.1 One-function formulation (handles multi-tier jumps, keeps narrowing immediate)

`DetailTracks.cs` — replace `TierFor` (lines 178-181):

```csharp
const float TierHysteresisDip = 24f;
static int NominalTierFor(float w) =>
    w <= 0f ? 0 : w >= 860f ? 0 : w >= 720f ? 1 : w >= 560f ? 2 : w >= 440f ? 3 : w >= 340f ? 4 : 5;

// Narrowing (more columns dropped) applies immediately — it's the safety direction (the self-heal
// clamp depends on it). Widening must clear the breakpoint by the hysteresis band so a width
// oscillating on a boundary can't flip-flop the column-set remount.
static int TierFor(float w, int prev)
{
    if (w <= 0f) return prev;
    int nominal = NominalTierFor(w);
    if (nominal >= prev) return nominal;                 // same or narrower: immediate
    int widened = NominalTierFor(w - TierHysteresisDip); // wider only if it holds at w - H
    return widened < prev ? widened : prev;
}
```

Call sites pass `prev` from the existing `Peek()` reads:
- `OnBoundsChanged` (line 371): `int t = TierFor(r.W, _tier.Peek());`
- rail-unlock flush (line 290): `int t = TierFor(_lastRightW, _tier.Peek());`
- self-heal clamp (line 256): `int fit = TierFor(_lastRightW, tier);` — unchanged semantics, since
  the clamp only acts on `fit > tier` (narrowing) and narrowing bypasses the band.

`DetailShell.cs` — apply the same `- H` trick **only to the 820/660 crossings** inside
`NominalModeFor`; the Vertical enter/exit band (`VerticalEnterW = 540` / `VerticalExitW = 580`,
lines 73-85) already implements asymmetric hysteresis for mode 3 and must be preserved verbatim.
Concretely: in `ModeFor`, when `nominal < currentMode` (widening among modes 0-2), re-evaluate
`NominalModeFor(w - H)` before adopting.

Make both functions `internal static` and unit-test them in `src/apps/Wavee.Tests` (oscillation across
860±24 and 820±24, multi-tier jump-downs, `w <= 0` no-ops, vertical band unchanged) — the
VerticalSlice tests the engine, not Wavee; the app's own test project is the right home.

---

## 7. Phase 7 — Settle-frame cost: measure, then only the honest wins

### 7.0 Measure first (do this before deciding anything else in this section)

`FG_RESIZE_DIAG` / `ReportResizeTick` already instruments keepAlive ticks (AppHost.cs:313-330,
1339-1345). Add one settle-specific stderr line when `resized && keepAlive`: durations for
`EnsureSize` (≈ `WaitForGpu` + `ResizeBuffers`), layout, record, submit+present (the stopwatch stamps
already exist around each phase in `Paint`). Target page: Wavee detail, playback active. This number
decides whether anything below §7.3 is ever revisited.

### 7.1 Layout width-bucket cache — cut (three independent fatal flaws)

1. **Quantization is wrong by construction**: keying on `widthDip / 32` and applying the cached
   bounds means rendering a layout solved for up to 31 DIP away from the actual window width —
   visible misfit at every non-bucket-aligned settle. Exact-width keying would fix correctness but
   shrinks the hit rate to "returned to a previously-seen exact width," a niche win.
2. **The invalidation key doesn't exist**: there is no `TreeEpoch` (grep: the only epoch in
   `Scene/` is `DynamicTextEpoch`, SceneStore.cs:767) and no `ChunkedArena` (columns are flat arrays
   grown by `Array.Resize`). Building a reliable structural epoch means instrumenting every mount /
   unmount / reorder / layout-affecting prop write across the reconciler — a subsystem-scale change
   smuggled in as a cache key.
3. **Layout is not a pure bounds function**: the Arrange pass *fires side effects* — edge-triggered
   `OnBoundsChanged` handlers (which write app signals — the tier/mode system of §6 depends on
   them), `VirtualRangeDirty` marks that drive realize windows (FlexLayout.cs:621), sticky pinning,
   and `TextMeasureCache` fills. Replaying a bounds snapshot skips all of them or requires replaying
   them, at which point you've rebuilt Arrange.

What already mitigates settle layout cost: `TextMeasureCache` persists across frames and hits
whenever a text node's `MaxW` is unchanged (FlexLayout.cs:195-200) — for a width-changing settle most
text remeasures, and that is inherent to the operation, not a caching gap.

### 7.2 Damage-only settle record — cut (self-contradicting)

The v1 plan sets damage = full viewport on the settle frame ("everything potentially moved") and then
prunes subtrees outside the damage — an empty prune set by construction. Independently: pruning by
device bounds requires cached per-node device bounds, and none exist — `SceneStore._bounds` is LOCAL
space; device bounds are computed during the walk itself (SceneRecorder.cs:437), so computing the
prune predicate costs the walk you're trying to skip. The settle record is a full record; the
existing span table then repopulates and every subsequent frame is cheap again.

### 7.3 `DwmFlush` after the settle present — keep as a bounded experiment

No `DwmFlush` exists in the repo today. The settle present is unsynced (`SuppressVsyncOnce`), and the
OS re-derives the Mica backdrop at the new size on its own composition schedule — the one-frame
content/backdrop mismatch is the "Mica snap." `DwmFlush` after the settle present *may* pace the next
frame boundary so fewer mismatched composites are visible (makepad precedent); it cannot make
content+Mica atomic (Mica is OS-side — this remains an explicit non-goal).

Implementation: a default-no-op `IGpuDevice` hint, since `AppHost` (Engine) cannot reference dwmapi:

```csharp
/// <summary>Hint that the next Present is a modal-resize settle frame; the backend may sync with the
/// compositor (DwmFlush) after presenting to reduce backdrop/content mismatch. Default no-op.</summary>
void HintSettlePresent() { }
```

`AppHost.Paint`: call it when `resized && keepAlive` just before submit. `D3D12Device`: set a
one-shot flag; in `Present` (after the successful `Present` call, line ~1698) `DwmFlush()`
(`[LibraryImport("dwmapi")]`, FluentGpu.Windows only). Judge with the §2.2 screen recordings; delete
the hint if it doesn't visibly help — do not let a speculative call ship unmeasured.

---

## 8. Phase 8 — Validation gates (`src/FluentGpu.VerticalSlice/Program.cs`)

House style: `Check("RZ-XX. <description>", cond, diag)` inside a `static void XyzResizeChecks(...)`
registered in the bottom dispatch block (~line 20700). The headless seams needed all exist:
`HeadlessWindow.InModalLoop` / `ClientSizePx` / `Scale` are settable, `PaintRequested` is wired by the
host and directly invokable by the gate; `Composited` becomes settable in Phase 1.

New gates:

| Gate | Assert |
| --- | --- |
| `RZ-DEFER.` | `Composited=true, InModalLoop=true`, grow `ClientSizePx`, invoke `PaintRequested` → viewport signal and root arranged bounds unchanged (probe `OnBoundsChanged` count); then `InModalLoop=false`, invoke again → new size applied, full layout ran |
| `RZ-LIVE.` | Same sequence with `Composited=false` → the keepAlive paint **does** apply the new size (regression gate for the §1.1 bug) |
| `RZ-SETTLE.` | On the settle paint: frame submitted (no skip-submit), a bounds-animated node snapped (no FLIP), span reuse disabled that frame (probe via `SceneRecordStats`/span counters) |
| `RZ-THROTTLE.` | Unit-gate the pure throttle predicate (see below): 10 steps at 8 ms spacing → ≤ 4 paints; step after 40 ms gap → paints |

Throttle testability: `Win32Platform` lives in `FluentGpu.Windows`, which the VerticalSlice **cannot
reference** (TerraFX-free transitive closure — a standing invariant). Hoist the predicate into a tiny
pure static in Engine, e.g. `FluentGpu.Hosting.ModalPaintThrottle.ShouldPaint(long nowMs, ref long lastMs, bool sized, int minIntervalMs)`,
call it from `Win32Window`, gate it headlessly.

**Audit existing gates**: `DetailResizeFlickerChecks` sets `window.InModalLoop = true`
(Program.cs:5828) and today gets defer behavior *without* any Composited bit. After Phase 1 those
paths stop deferring unless the gate also sets `Composited = true`. Review `RZ-MODAL`, `RZ-TIER`,
`RZ1-3`, `RZ-RESP`, `S3`, `SK.h`, `54c` and set `Composited = true` wherever the gate models the Wavee
(Mica) window; leave it false where the gate intends the live path.

Hysteresis tests live in `src/apps/Wavee.Tests` (§6), not the VerticalSlice.

On-device acceptance (manual, from §2.2): detail page, playback active, 16-step edge-drag storm on
each edge + cross-monitor DPI drag + aero-snap release: 0 blank frames, no tier flip-flop, one settle
pop, recorded and eyeballed.

---

## 9. Phase 9 — Design corpus

- `design/subsystems/pal-rhi.md` (owner of both seams): register `IPlatformWindow.Composited`
  (+ `SizedInModalLoop`/`ModalOriginShift` only if §2.3 ships), `IGpuDevice.HintSettlePresent`
  (+ `SetModalContentOffset`/`ResetModalContentOffset` only if §2.3 ships), and the modal policy
  table from §1.3 as the canonical statement (composited = defer + pin-top-left + one settle frame;
  non-composited = 30 Hz throttled live).
- `design/subsystems/threading-render-seam.md`: the WndProc budget invariant (during a composited
  modal loop, WndProc may only cache `_w/_h`, enqueue input, or invoke the origin-shift callback —
  never `ResizeBuffers`/layout), plus the note that today WndProc == UI == presenting thread, and the
  reserved SPSC-with-explicit-wake design for when async rendering makes DComp render-confined
  (frame-publish drain alone starves mid-drag — record this so the v1 queue flaw isn't reintroduced).
- `design/subsystems/validation.md`: the new `RZ-*` gates.
- Register the **no-env-behavior-flags contract** (§12.4) in `design/SPEC-INDEX.md` §2 with
  `design/subsystems/validation.md` as the owning doc (it owns the gate regime; the ratchet script is
  a gate), and add the working rule to `CLAUDE.md`.
- Grep `design/` + `docs/` for `FG_LIVE_MODAL_RESIZE` and update/annotate; then
  `powershell -File docs\design\check-canon.ps1` (exit 0). If any live doc must mention the deleted flag
  historically, use `<!-- canon-allow: superseded flag, removed by butter-smooth-resize-v2 -->`.

---

## 10. Implementation order & effort

1. **§12.5 ratchet gate first** (~1 hour): freeze the current flag inventory as the allowlist so no
   new env read can land while the burn-down proceeds.
2. **Phase 1** (resize flag deletion + `Composited` + throttle) — ~half a day, includes the gate
   audit. This is the only phase with a user-visible bug fix; land it alone,
   `dotnet build src/FluentGpu.slnx` clean + VerticalSlice ALL CHECKS PASSED before anything else.
3. **§12.1 behavior-flag retirement** (render-loop ctor option, vsync API, `FG_ANIM_FPS`/tunables,
   `FG_DETACHED_FLY` promote-or-delete decision run) — ~one day.
4. **§2.2 verification recordings** + **§7.0 settle measurement** — one session, produces the
   evidence that gates every optional item.
5. **§13 move liveness** (ambient animation during titlebar drags) — ~half a day + on-device budget
   measurement; this is the only *perceivable* gap in window moving today.
6. **§6 hysteresis** + Wavee.Tests — ~half a day, independent of everything else.
7. **§8 gates** + **§9 design docs** (incl. registering the no-env-flags contract) — ~one day.
8. **§12.2 probe flags → argv** — mechanical, ~one day, can trail.
9. **§12.3 diagnostics consolidation** — mechanical sweep, do opportunistically; the ratchet keeps it
   honest in the meantime.
10. **Optional, evidence-gated**: §2.3 edge anchor, §7.3 DwmFlush, §4 ModalLive record tier — each
    only if its measurement/recording says so.

## 11. Explicit non-goals (carried + amended)

- Mica backdrop re-derive at settle (OS-side; one-frame artifact documented, `DwmFlush` may soften,
  cannot remove).
- Live composited resize at 60 Hz (reintroduces the 16-52 ms/step synchronized work the defer
  eliminates).
- Undocumented `EnableResizeLayoutSynchronization`; `WM_NCCALCSIZE` noflicker recipes.
- Layout worker pool; removing `WS_EX_NOREDIRECTIONBITMAP`; resize env flags of any kind.
- **New**: force-sync render thread by default; layout snapshot/bucket caches; damage-pruned settle
  records; stretch matrices on the composition swapchain — all cut with reasons in §5/§7.

---

## 12. Env-flag retirement & permanent ban

Owner decision: **every env behavior flag is retired, and new env opt-in flags are banned** — behavior
must be identical for identical inputs regardless of environment. Full inventory (grep of
`Diag.EnvFlag(` + `GetEnvironmentVariable(` across `src/` and `src/apps/Wavee`, 2026-07-08) classifies
~70 reads into three classes with different treatments.

### 12.1 Behavior flags — all deleted (each with an explicit disposition)

| Flag | Site(s) | Disposition |
| --- | --- | --- |
| `FG_LIVE_MODAL_RESIZE` | Win32Platform.cs:458, AppHost.cs:240 | Deleted (Phase 1) |
| `FG_RENDER_THREAD`, `FG_RENDER_ASYNC` | AppHost.cs:244/252 | Deleted → internal `RenderLoopMode` ctor option (§5); update the `FG_RENDER_ASYNC` read in `WaveeResizeProbe.cs:92` |
| `FG_NOVSYNC` | D3D12Device.cs:45 | Deleted → API: `D3D12Device` ctor param / device method; bench probes get the device via the `FluentApp.DiagnosticRun` seam and call it in code |
| `FG_DETACHED_FLY` | ConnectedAnimation.cs:66 | **Decision run, no dormant fork**: enable, run VerticalSlice + visual captures; promote (delete the old path) or delete the detached rebuild. Update the CLAUDE.md sentence that documents it |
| `FG_ANIM_FPS` | AppHost.cs:529, FluentApp.cs:119 | Deleted — this one is an env var abused as an *intra-process parameter channel* (FluentApp checks it to decide whether its own `ambientFps` param may seed the host). The real API already exists: `FluentApp.Run(ambientFps:)` → `AppHost.AmbientAnimationFps`. Default 30 becomes a const |
| `FG_IMG_UPLOADS` | DecodeScheduler.cs:42 | Hardcode 6 |
| `FG_IMAGE_CACHE_MB` | FluentApp.cs:210 | Optional `FluentApp.Run(imageCacheMb:)` param, default 64 |
| `WAVEE_AUDIO_INPROC` / `WAVEE_AUDIO_OOP` | SupervisedAudioHost.cs:53-54, AudioPlaybackStack.cs:57-58 | Hardcode the OOP host (today's default). If support genuinely needs an in-proc escape hatch, it becomes a key in the app's settings store (a real, inspectable setting) — never env |
| `WAVEE_FORCE_FREE` | Program.cs:252 | Delete (test with a real free account) |
| `WAVEE_PB_NOPREVIEW`, `WAVEE_PB_NOMORPH` | WaveeShell.cs:111/113 | Delete — kill-switches for shipped features |
| `WAVEE_PLAYPLAY_*` | Backend/Audio/SpotifyRuntimeIdentity.cs, LiveSessionHost.cs:234 | **Excluded from this plan** — private-runtime config adjacent to the fenced playplay split; decide in that workstream. Carried on the ratchet allowlist with an explicit `# playplay-private` marker |

### 12.2 Probe/harness selectors → command-line args

These select automated test/bench modes, not user behavior, but they still die as env vars. Convert to
argv following the existing `--screenshot <path>` precedent, one small parser per exe:

- **VerticalSlice**: `FG_PROBE` → `--probe=<name>`; `FG_FORCE_DEVICE_LOST` → `--inject-device-lost=<frame>`
  (the `IGpuDevice.InjectDeviceLost` API already exists — the env read in AppHost.cs:144 goes).
- **Gallery** (`FluentGpu.WindowsApp`): `FG_SOAK`, `FG_STRESS_RESIZE`, `FG_STRESS_NAV`,
  `FG_WAKE_AUDIT`, `FG_HUD`, `FLUENTGPU_LOC_CULTURE` → `--soak`, `--stress-resize`, `--stress-nav`,
  `--wake-audit`, `--hud`, `--loc-culture=<tag>`.
- **Wavee**: the whole `WAVEE_*` probe family (`_NAV_PROBE`, `_RESIZE_PROBE`(+`_ROUTE`/`_ARG`),
  `_PERF_BENCH`(+`_BENCH_OUT`+knobs), `_MEM_SOAK`, `_CONN_STRESS`(+`_STRESS_N`/`_NOPACE`),
  `_TRACKLIST_SHOT`, `_HERO_SHOT`, `_LIKED_SHOT`, `_HOME_SCROLL_PROBE`, `_LYRICS_*`, `_PROBE_OUT/W/H`,
  `_PROBE_VSYNC`, `_PB_CAPTURE`, `_ANIM_CAP`, `_FAKE_CHALLENGE`, `_NOWPLAYING_OPEN`,
  `_LYRICS_OPEN/FULLSCREEN`, `_AUDIO_FORMAT_PROBE`) → `--probe=<name>` + `--probe-*` options. Update
  any launch scripts that set these. `_PROBE_VSYNC` becomes a probe option whose effect flows through
  the new vsync API, not env.
- Also grep for **`SetEnvironmentVariable`** — in-process env writes (the `FG_ANIM_FPS` channel
  pattern) are banned the same way.

### 12.3 Observability → the one `FG_DIAG` variable

Pure logging/tracing flags don't fork behavior, but they still proliferate. The `Diag` facility
(Foundation/Diag.cs) already has the right shape: `[Conditional("DEBUG"/"FLUENTGPU_DIAG")]` recording
(erased from release AOT), an `FG_DIAG` master gate, an AppContext switch, and a pluggable `Sink`.
Extend it with categories and collapse everything onto it:

```csharp
// FG_DIAG=1            → everything
// FG_DIAG=resize,move,scroll-trace:C:\out.json,mem:5   → named categories, optional :value
public static bool EnabledFor(string category);        // O(1) frozen-set lookup, seeded once
public static string? CategoryValue(string category);  // the ":value" payload, or null
```

Migration targets (mechanical sweep): `FG_MOVE_DIAG`, `FG_RESIZE_DIAG`, `FG_WAKE_DIAG`,
`FG_MEM_DIAG`(+`_SEC` → `mem:5`), `FG_DL_TRACE`, `FG_ALLOC_DIAG`, `FG_ALLOC_TYPES`, `FG_RENDER_DIAG`,
`FG_LAYOUT_DIAG`, `FG_NC_DIAG`, `FG_FPS_LOG`, `FG_SCROLLLOG`/`FG_SCROLL_LOG`/`FG_SCROLL_TRACE`,
`FG_OFFSET_JUMP`, `FG_MORPH_LOG`, `FG_DUMP`, `FG_DIAG_CONSOLE`, and Wavee's `WAVEE_LYRICS_DEBUG`,
`WAVEE_PLAYERBAR_DIAG`, `WAVEE_AUDIO_RANGE_TRACE` (→ categories `lyrics`, `playerbar`, `audio-range`).
`WAVEE_LOG_LEVEL`/`WAVEE_LOG_FILE_LEVEL` move to the app's settings store (log config is a real user
setting, not a dev toggle). **End state: exactly one env variable in the tree (`FG_DIAG`)** plus the
fenced `WAVEE_PLAYPLAY_*` entries pending their own workstream. A new diagnostic is a new category
string — never a new env var.

### 12.4 The ban (cross-cutting contract — register per §9)

1. Product behavior must be identical for identical inputs regardless of environment. Behavior
   selection lives in `WindowDesc`/API parameters, the app settings store, or code — never env.
2. Experiments land behind VerticalSlice gates with a promote-or-delete decision; no default-off
   dormant forks (`FG_DETACHED_FLY` was the last).
3. Observability goes through `Diag` categories under the single `FG_DIAG` gate, erased from release
   AOT via the existing `[Conditional]` design.
4. Test/bench harness selection is argv.

### 12.5 Enforcement ratchet (land this FIRST)

`tools/check-env-flags.ps1`: scan `src/` + `src/apps/Wavee` (excluding the fenced playplay paths) for
`EnvFlag\(|GetEnvironmentVariable\(|SetEnvironmentVariable\(`; every hit must match a line in
`tools/env-flag-allowlist.txt` (`<path>:<name>` per line). Non-allowlisted hit → exit 1. Wire into CI
next to the build, and into `.githooks/pre-commit` (precedent: the playplay guard). Seed the allowlist
with today's full inventory, then **PRs may only remove lines** — the ratchet makes "no more future
opt-in flags" mechanical rather than aspirational, and lets the burn-down (§12.1 → §12.2 → §12.3)
proceed incrementally without ever regressing.

---

## 13. Butter-smooth window MOVE (titlebar drag)

### 13.1 Status: tracking is already optimal; liveness is the only gap

For a composited window, a titlebar drag is already the best case Windows offers — and it is
*mechanically the same thing macOS does*: the compositor relocates a cached surface. `WM_MOVE` skips
paints entirely (Win32Platform.cs:1093-1102 — "DWM re-composites that surface at the new screen
position… no app repaint is needed"), `WM_TIMER` keep-alives are skipped for composited modal loops
(1067-1082), and measured cost is ~0.2-0.7 ms/step of pure WndProc bookkeeping. Content cannot lag the
cursor because the app isn't in the loop at all. **There is nothing to build for tracking** — if a
drag ever feels unsmooth on-device, that's a measurement task (diag category `move`), not a feature.

The perceivable gap is different: **the content is a frozen frame for the whole drag**. Zero paints
means the playback seek bar, equalizer, shimmer, spinners, and image crossfades all stop until
mouse-up, then jump. macOS keeps these alive because its render tree is compositor-owned and decoupled.
That freeze — not tracking — is what makes a drag feel less alive than macOS.

### 13.2 Fix: throttled ambient ticks during pure move (not resize)

The engine already has everything needed to paint safely from inside the modal loop (`keepAlive`
paints: no latency wait, no vsync block, `EnsureSize` no-ops because the size is unchanged, layout
skipped). The freeze is purely policy — three deliberate gates, each of which distinguishes move from
resize *only after* this change:

1. **`WM_TIMER`** (Win32Platform.cs): stop skipping composited *move* ticks; keep composited *resize*
   fully deferred; throttle everything else — the §1.2 final form:

```csharp
if (_composited && _inMoveSizeLoop && _sizedInMoveSizeLoop) return true;   // composited RESIZE: defer
if (_inMoveSizeLoop && ThrottleModalTickPaint()) return true;              // ≤30 Hz tick budget
PaintRequested?.Invoke();
```

   `ThrottleModalTickPaint` = the §1.2 timestamp helper generalized (33 ms min interval, reset on
   `WM_ENTERSIZEMOVE`). The 8 ms timer is the heartbeat; the throttle caps paints at ~30 Hz.

2. **AppHost modal idle-skip** (AppHost.cs:1368-1373): the ambient-bail currently keys on
   `_window.InModalLoop` — change it to `_window.SizedInModalLoop` so ambient-only wakes (seek
   ticker, brush loops, caret) paint during a pure move but stay bailed during an edge resize. This
   is what justifies adding `IPlatformWindow.SizedInModalLoop` (§1.2; Win32 backs it with the
   existing `_sizedInMoveSizeLoop` field, headless gets a settable property).

3. **Span-reuse relaxation**: `keepAlive` currently ORs `SpanReuseDisabledReason.ModalPaint`
   unconditionally (AppHost.cs:1611), which would make every move tick a full re-record of the scene.
   Gate it on `_window.SizedInModalLoop` instead — during a pure move the scene geometry is
   untouched, so clean spans stay valid and a move tick records only the animated subtrees (the
   whole point of the span table). During a resize the disable stays (geometry mismatch).

Nothing else changes: `WM_MOVE` stays paint-free for composited windows (DWM owns tracking); the
settle paint on `WM_EXITSIZEMOVE` is unchanged; wake reasons like warming virtual lists were already
protected by `OnlyAmbientWakeReasons` and keep painting in both cases.

### 13.3 Budget guard (the reason this was frozen in the first place)

The historical trauma — "564 redundant present-only paints, present-blocked up to 62 ms each"
(AppHost.cs:1347-1367) — was measured on *edge-resize-while-playing*, largely before the
latency-wait/vsync suppression existed, and at unthrottled cadence. The move-tick path is different on
all three axes (no resize, both suppressions on, ≤30 Hz), but the guard must be empirical:

- Instrument (diag category `move`): p50/p95 WndProc time per move tick on the Wavee detail page with
  playback active.
- Acceptance: p95 tick < 4 ms and the drag itself stays visually perfect (the tick must never make
  tracking worse — tracking is DWM's and only slow WndProc can hurt it).
- If the budget fails on-device: first drop the cadence to the ambient-fps pace (`AmbientFrameWaitMs`),
  and if it still fails, revert to frozen-content-during-drag and record why in the design doc. The
  revert is a one-line policy change, not an architecture change.

### 13.4 Gates

| Gate | Assert |
| --- | --- |
| `RZ-MOVE.` | `Composited=true, InModalLoop=true, SizedInModalLoop=false`, ambient anim active → keepAlive paint submits a frame (seek bar advances); span reuse NOT disabled by `ModalPaint` |
| `RZ-MOVE2.` | Same but `SizedInModalLoop=true` → ambient-only wake bails (no submit), span reuse disabled — the resize defer is untouched |

Design-doc registration rides §9 (`pal-rhi.md`: the move policy row in the §1.3 table gains "ambient
ticks ≤30 Hz"; `SizedInModalLoop` becomes a registered seam member).
