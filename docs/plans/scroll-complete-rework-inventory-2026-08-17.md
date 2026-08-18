# Scroll complete-rework inventory — 2026-08-17

Status: **REQUIREMENTS LOCKED (2026-08-17). Inventory is ground truth for the kill list. Replacing design spec not written yet.**

Standing ruling: **delete the existing scroll mechanic and replace it.** The current feel is rejected. This is not a third polish of `ScrollIntegrator`. v2 / v2.1 stay as history; they do **not** constrain the replacement.

Feel target (plain language): **show-my-mom smooth.** Two-finger on a precision touchpad **and** one-finger on a touch screen, the content is glued like Photos / WinUI `ScrollView` / iOS. Those are two hardware paths, not one. Not “fine.” Not “the gates are green.” If she can tell it is our old scroller, it failed.

This document is (1) the **requirements** for that replacement, (2) the as-built map of every current mechanic so we know what to delete, (3) the tiny API we would ship. It synthesizes a six-agent inventory (physics, ScrollBind, controls, layout/render, tests/docs, dead code), a Windows-API pass (DirectInput / DirectComposition / DirectManipulation), and a look at Chromium `cc/input`, Flutter `scroll_*.dart`, WinUI `ScrollPresenter`, and `C:\WAVEE\SmoothScroll.Avalonia`.

Out of scope (not read): `src/apps/.native/**`, `src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`, playplay plans.

---

## 0. Requirements (DECIDED)

User mandate, 2026-08-17: *delete all existing scroll code and come up with a new and improved scroll mechanic that just feels super nice, because right now it’s shit.*

These requirements override the older “C then B / land WIP first / do not write a spec until C vs B” framing in this file.

### R1 — Greenfield. The current mechanic is the bug.

The shipped scroll stack is **not the starting point**. We do not “make single-writer actually true,” retune `ScrollTuning`, or land another v2.2 addendum.

- New runtime, new files, new tests.
- Then **delete** the old feel owner. No dual-path. No `FG_*` kill switch that brings the old integrator back (standing repo law).
- Uncommitted WIP that patches the old path (`PresentUnpacedDetector`, wheel-routing diffs, feel-summary on the v2 integrator) is **not** a prerequisite. Absorb a diag idea if it still applies; do not land it as the next feel fix.

“Delete all existing scroll code” means the **feel owner** — every path that turns HID / timers / `ScrollTo` into `Offset*` / rubber-band / fling. It does **not** mean delete Direct3D, layout, or the window.

### R2 — Feel is the only pass/fail.

Success is a person using Wavee: two-finger Artist, finger Recents, mouse wheel a settings pane. Content is glued. Coast is the OS (touchpad) or a clean engine fling (finger). Slow stop does not fling. A UI hitch does **not** hitch the pixels.

Gates-green while it still feels like today’s scroller = **fail**. Headless tests are necessary; they are not sufficient.

Bar: Photos / WinUI `ScrollView` / iOS on the same hardware. Not “better than last week.”

### R3 — Kill list (the mechanic — gone)

These are the current feel owner. Replacement does not call them.

| Gone | Why |
|------|-----|
| `Animation/ScrollIntegrator.cs` | Phase machine, UI-thread Tick, “single writer” that isn’t |
| `Animation/OverscrollPhysics.cs` | Rubber-band / `CoastStep` as today’s owner |
| `Animation/ScrollTuning.cs` | Feel POD for the rejected integrator |
| `Animation/ScrollIntoView.cs` | Programmatic path that still pokes offset |
| `Animation/ScrollBind.cs`, `ScrollBindEval.cs`, `Dsl/ScrollBindDsl.cs` | Effects layer as shipped; recipes come back on the new runtime, this DSL dump does not |
| Scroll halves of `Input/InputDispatcher.cs` | Intent recorder + bypass writers + under-sampled pan + wheel routing |
| Dual sticky (`ScrollBind` pin **and** Recents `ItemClipTopInset` as a second clip physics) | One sticky mechanism after cutover |
| `ScrollControllerAdapter` | Dead |
| Stale `ScrollAnimator` name / API YAML | Dead |
| Morph / Velocity / SignedPhase / named-timeline / PinKind 2 / unused sinks | Dead surface |
| `VerticalSlice/.../ScrollSuite.cs` as v2 phase-machine gospel | Rewrite against the new runtime; do not port 7.7k lines of old states |
| Feel knobs that change latch (`FG_SCROLL_DIRHYST` and friends) | Diag may stay; behavior knobs do not ship |

DirectManipulation, `WM_POINTER*`, and the compositor clock are **producers / clocks**, not the mechanic. They stay as PAL. The new runtime consumes their events.

### R4 — Keep (infrastructure, not the scroller)

Deleting these would not make scroll nicer; it would delete the engine.

- Content still moves as a **layout-free** `-offset` on the content child’s transform. Relayout is not how lists pan.
- Virtual lists still realize a window (`VirtualRangeDirty` is the UI-thread hop). Fast pan must not wait on row mount for in-window motion.
- PAL: `EnableMouseInPointer`, `WM_POINTER*`, DirectManipulation as **event producer only** (never offset owner). DirectInput stays unused. `InteractionTracker` stays illegal (DComp already owns the HWND).
- `SceneRecorder` clip + overlay scrollbar chrome can stay as *drawing*, rewritten to the new offset.
- Wavee pages keep compiling: `ScrollView` / `Virtual.*` / `ItemsView`, plus the five effects they actually use (stretch, parallax, collapse, sticky, clip-below). Those are **recipes on the new runtime**, not a reason to keep `ScrollBindDsl`.

### R5 — Authoring after the cut is tiny.

One viewport. Lists **are** viewports (wrapping `Virtual.*` in `ScrollView` stays the anti-pattern). One `ScrollController`. Recipes for the five Wavee effects; generic bind is an escape hatch, not the page language. No 40-property `ScrollPresenter`. See §10.

Device class is not a public API. Touchpad vs finger vs wheel is PAL classification into one runtime.

### R6 — Architecture of the replacement (feel implies this).

Approach **A** (WinUI `InteractionTracker` owns pixels) stays **illegal**.

Approach **C** (clean the current UI-thread integrator in place) is **illegal under R1**. That is the code we are deleting.

Approach **B** is the feel architecture: input + integrate + apply transform on the **present / compositor thread**. UI thread hit-tests, latches the target, and realizes virtual windows. In-window motion does not wait on C#. This is the only design that still glides when album art / decode / layout is busy.

We do **not** ship a cleaned UI-thread Tick “for a while.” The new mechanic is B from day one, in new files.

Honest ceiling (corpus law): a **sustained GPU stall** still shows. Mom-smooth is “the list never waits on C#,” not “physics ignores the GPU.”

### R7 — Devices the new mechanic must own.

Three producers, one runtime (see §2.6). Public API stays device-blind.

| Device | Feel contract |
|--------|----------------|
| Precision touchpad | DirectManipulation 1:1 + **OS inertia verbatim**. Fallback `WM_POINTERWHEEL` is correctness only; it does not pass the mom test. Never classify PTP as a detented mouse. |
| Finger | 8 DIP slop; flick is not a click; 1:1 including rubber-band; engine fling from the move stream; under-sampled flick still pans. |
| Mouse wheel | Crit-damped chase; **hard stop** at extents; never a rubber-band. |

Nested scroll: inner takes the gesture; at the edge, handoff **during drag**, not only at lift.

### R8 — Cutover is a replace, not a strangler.

After the new runtime is the offset owner, the kill list in R3 is deleted from `src/`. Wavee and the gallery compile only against the new mechanic. No “old integrator for ItemsView, new for ScrollView.”

Headless gates must go green on the **new** runtime (`dotnet build src/FluentGpu.slnx` both configs + `dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`). Pixel feel is the user’s run of Wavee.

### Non-goals

- DirectInput.
- Handing the HWND to WUC / `InteractionTracker`.
- Keeping v2 phase names (`TouchpadTracking`, `WheelAnimating`, …) as a compatibility layer.
- Porting every ScrollBind kind Wavee does not use.
- Claiming zero hitch under a sustained GPU stall.

---

## 0.1 As-built diagnosis (why R1)

The inventory below is **what we are deleting**, not what we are extending. Short version:

**What is already true**

- Scroll is **layout-free**: offset is a `-ScrollOffset` translation on the content child’s `LocalTransform`. In-window motion is compositor-only (no reconcile, no relayout).
- Physics is **engine-owned**. DirectManipulation is an event producer, not an offset owner. DirectComposition presents the swapchain; it does **not** pan list content. DirectInput is not used.
- `ScrollIntegrator` (renamed `ScrollAnimator`) is the intended single writer of `Offset*` / `OverscrollPx` at phase 7.
- Effects are a separate layer: `Element.ScrollBinds` → POD slab → `ScrollBindEval` after physics.
- ~70 VerticalSlice gates pin dt-invariance, resample, overscroll math, virtual re-pin, zero-alloc.

**What is not true, despite the comments**

- **Single-writer is a comment, not a contract.** LazyGrid, ItemsView edge-scroll, LyricsView latch, FlexLayout clamp, TreeView, and `ScrollIntoView` snap still poke `ScrollState.Offset*` directly.
- **Two layers of authoring** (`ScrollEl` vs `VirtualListEl` vs wrapping one in the other), **two sticky-clip mechanisms**, **two scrollbars**, **three contact producers** (finger `WM_POINTER*`, PTP DirectManipulation, PTP `WM_POINTERWHEEL` fallback) that share one misleading phase name (`TouchpadTracking`).
- Wavee uses about **five** ScrollBind effects. The DSL ships Morph / Velocity / SignedPhase / Blur / named timelines / PinKind 2 that nobody authors.
- The public name `ScrollAnimator` is dead. The API YAML still documents it.
- Uncommitted WIP already touches pacing (`PresentUnpacedDetector`), `ScrollTrace`, wheel routing, and `ops/diag`. Under R1 that WIP is **not** the next feel landing; it dies with the old mechanic unless a diag idea is re-homed.

That pile is why it feels shit: three producers, many offset writers, UI-thread Tick, dead DSL. Gates can be green on a scroller nobody would show their mom.

**What “mom-smooth” actually is** is locked in **R2 / R6 / R7**, not in another `ScrollBind` kind. Pipeline:

1. Input and vsync meet on **one clock**.
2. Contact is **resampled** to one sample per frame (summing packets is not resampling).
3. Offset is applied **without the UI thread** when the realized window has not changed (Chromium fast-scroll).
4. DirectManipulation supplies PTP phase + OS inertia **as events**; the **new** runtime still writes offset.
5. Authors see a **tiny** API (R5 / §10).

WinUI gets (3) by handing the HWND to `InteractionTracker`. We cannot — raw DirectComposition already owns the window, and an OS-owned offset would skip `VirtualRangeDirty`. The honest cousin is Chromium `cc/input` (compositor-thread scroll) plus Flutter’s `Idle / Drag / Ballistic` *names*, not Flutter’s UI-thread build-every-frame.

---

## 1. End-to-end data flow (as built)

```
OS HID (mouse / PTP / finger)
  │
  ├─ Finger: WM_POINTERDOWN/MOVE/UP (PT_TOUCH)
  │     → slop 8 DIP → per-node PendingRawOffset + PhaseTouchPan
  │     → lift: engine CoastStep (no OS inertia for touch)
  │
  ├─ PTP primary: DM_POINTERHITTEST + SetContact(PT_TOUCHPAD)
  │     → RUNNING / INERTIA content deltas (device px)
  │     → ScrollBegin/Update/End + Momentum*  (OS owns the coast)
  │
  └─ PTP fallback / mouse: WM_POINTERWHEEL/HWHEEL
        → classifier: hi-res contact vs detented mouse
        → hi-res: silence-lift + engine self-fling
        → mouse: WheelAnimating, hard stop, no rubber-band

InputDispatcher (UI thread, phase 2)
  records intents on ScrollState (PendingTarget / PendingRawOffset / Phase)
  does NOT (in the v2 contract) write Offset — except the bypass list in §8

ScrollIntegrator.Tick (UI thread, phase 7)
  one sample / frame, closed-form physics
  WriteScrollOffset / WriteOverscroll
    → SetScrollOffset → ApplyScrollPosition
         • ContentNode.LocalTransform = -(offset + band)  [± zoom]
         • TransformDirty | PaintDirty   (never LayoutDirty)
         • ScrollBindEval.ApplyContinuous
         • maybe VirtualRangeDirty

FlexLayout.ArrangeViewport (only on relayout)
  publishes ContentW/H, ViewportW/H
  re-clamps Offset
  BakeGeometry + ApplyContinuous

SceneRecorder.Walk
  viewport ClipsToBounds
  content world *= LocalTransform
  overlay scrollbar + edge cues after PopClip

DXGI Present + IDCompositionVisual.SetContent(swapchain)
  DCompositionWaitForCompositorClock paces the UI loop
  AcrylicScrollHold throttles backdrop refresh while scrolling
```

Hit-test inverts the same origin-conjugated `LocalTransform` (`InputDispatcher.StepIntoNode` ↔ `SceneRecorder.Walk`).

---

## 2. Windows APIs — what is in, what is out

### 2.1 DirectInput

**Not in the repo.** No `IDirectInput`, `dinput8`, XInput, or Raw Input HID path. Scroll never goes through the old DirectX input stack. Do not add it.

### 2.2 DirectComposition

**Present path, not scroll-offset path.**

| Call | Role |
|------|------|
| `DCompositionCreateDevice` / `CreateTargetForHwnd` / `Visual.SetContent(swapchain)` | Mica / `WS_EX_NOREDIRECTIONBITMAP` — UI pixels on the HWND |
| `DCompositionWaitForCompositorClock` (`Win32CompositorClock`) | UI-loop display clock (not `IDXGIOutput::WaitForVBlank`) |
| `IDCompositionVisual.SetOffset` | **Video children only** (`DCompVideoPresenter`) — not lists |
| `IDCompositionDevice.Commit` | Flush DComp tree (UI + video) |

List content does **not** move via DComp `SetOffset`. Offset is a GPU transform inside the DrawList. That is why virtualization can re-realize on the same write.

Windows.UI.Composition is used only for **popup desktop-acrylic** (`CompositionBackdrop.cs`). It is not the scroller. `InteractionTracker` / `VisualInteractionSource` are **rejected**: WUC would need to own the HWND.

### 2.3 DirectManipulation (live)

`src/FluentGpu.Windows/Pal/Win32DirectManipulation.cs`

Pure **phase-event producer** for precision touchpad:

- `IDirectManipulationManager` / `UpdateManager` / `Viewport` / `Content`
- Hand-rolled `IDirectManipulationViewportEventHandler` CCW
- Manual `UpdateManager.Update` on the STA, absolute-deadline paced
- `DM_POINTERHITTEST` → `SetContact` for `PT_TOUCHPAD` only
- Emits the same `ScrollBegin/Update/End` + `MomentumBegin/Update/End` the integrator already consumes
- Recovery ladder: Stop → recycle → session disable, then `WM_POINTERWHEEL` fallback
- Popups never get a producer (fallback only)

Canon (`docs/design/subsystems/input-a11y.md` §7B, `SPEC-INDEX.md`): **no OS object owns a scene transform, viewport offset, clamp, or virtual-window realization.** The deleted `IScrollSource` / `ScrollSourceMux` / `Win32DmScrollSource` design stays gone.

### 2.4 Pointer / wheel / settings

| API | Role |
|-----|------|
| `EnableMouseInPointer(true)` | Process-wide; `WM_MOUSEWHEEL` is retired; everything is `WM_POINTER*` |
| `WM_POINTERDOWN/UP/UPDATE/CAPTURECHANGED` | Touch / pen / mouse contacts |
| `WM_POINTERWHEEL` / `WM_POINTERHWHEEL` | Detented mouse **or** hi-res PTP fallback |
| `GetPointerInfo` / `GetPointerInfoHistory` / `GetPointerDevice` | Device class, QPC, coalescing, mouse-vs-touchpad evidence |
| `SPI_GETWHEELSCROLLLINES/CHARS` | User “lines per notch”; `WHEEL_PAGESCROLL` = one screen |
| DXGI `Present` + `DwmFlush` | Pixels; `PresentUnpacedDetector` catches RDP/Shadow free-runs |

**Not used:** `WM_GESTURE`, `WM_TOUCH`, `RegisterTouchWindow`, `InteractionContext`, `IDXGIOutput::WaitForVBlank` as the UI clock.

### 2.5 Why this split exists

WinUI `ScrollView` never writes physics — `ScrollPresenter` rides compositor-side `InteractionTracker` with `VisualInteractionSourceRedirectionMode::CapableTouchpadAndPointerWheel`. Touchpad never even fires `PointerWheelChanged`.

FluentGpu cannot do that. DComp owns the HWND. Virtual lists must see every offset write (`VirtualRangeDirty`). Headless gates need a deterministic managed integrator. So Windows may supply **intent**; the engine supplies **offset**.

### 2.6 Precision touchpad vs finger touch (two contact paths)

These are **not the same input**. They share one integrator phase name (`TouchpadTracking`) which is the #1 naming trap in this subsystem. Finger touch is `WM_POINTER` contacts. Precision touchpad is either DirectManipulation or promoted `WM_POINTERWHEEL`. Mouse wheel is a third path (`WheelAnimating`). Authors never choose; the PAL classifies.

```
                    ┌─────────────────────────────────────┐
                    │     ScrollIntegrator.Tick           │
                    │  TouchpadTracking / Fling /         │
                    │  Overscroll / SnapBack              │
                    └──────────────▲──────────────────────┘
           intents only            │
     ┌─────────────┬───────────────┴───────────────┐
     │             │                               │
 Finger touch   Touchpad (DM)              Touchpad fallback
 WM_POINTER*    SetContact + INERTIA       WM_POINTERWHEEL
 per-node       singleton resampler        silence-lift + self-fling
 PendingRawOffset  OS momentum verbatim    ImpulseVelocity 40ms
```

#### Finger (direct touch) — `PointerKind.Touch`

Windows messages: `WM_POINTERDOWN / UPDATE / UP / CAPTURECHANGED` (`PT_TOUCH`). **Not** `WM_TOUCH` / `WM_GESTURE`.

Flow (`InputDispatcher`):

1. `TouchDown` hit-tests, finds nearest `Scrollable` ancestor, records `_panAnchorOffset` / `_panAnchorPx`. Also enrolls the gesture arena (Tap vs Pan vs Drag vs Hold).
2. Moves stay a **tap candidate** until travel on the scroll axis ≥ `PanSlopPx` (8 DIP, same as `TouchSlopPx`). Then `ClaimTouchPan`: kills the click, `Pressed → PointerCancel` on the down chain (WinUI contract), starts the pan.
3. `BeginTouchPanTracking` / `RecordTouchPanSample`: writes **per-node** `PendingRawOffset = anchor − panDelta` and `PhaseTouchPan`. **Does not write offset.** Two fingers on two sibling lists stay independent (the PTP path cannot do this — it is a singleton resampler).
4. Phase 7 reads `PendingRawOffset`, splits into clamped offset + rubber-band once.
5. `TouchUp`:
   - Holding the band → `SnapBack`, **no fling**.
   - In-range and `|v| ≥ 50 px/s` → `SeedScrollFling` (engine friction coast, `FlingDecayPerS = 0.05`).
   - Slow stop → `Idle`.
6. **Under-sampled flick:** OS delivered no in-slop move that crossed 8 DIP, but lift is past slop. `CompleteUnderSampledPan` lands `WriteScrollOffset` once (hard clamp, **no band** on this rare path) and may seed a fling. Without this, a quick flick would **click a track** (`up == _down`).

Re-grab over a live `SnapBack` band: **does not** fold the band into the anchor (PTP does). Direct-touch springs the old band to 0 while the new drag starts from the clamped offset. Comment at `BeginTouchPanTracking`: `ExcessFromBand` blows up near the asymptote if re-applied per finger gesture.

Pinch-zoom is a **second contact** over a `Zoomable` viewport (`ScrollEl.Zoomable`), not a scroll producer. DM pinch (`|scale−1| > ε`) is suppressed as pan.

#### Precision touchpad — primary: DirectManipulation

Windows messages: `DM_POINTERHITTEST` (0x0250) → `SetContact(pointerId)` for `PT_TOUCHPAD` only. Mouse/touch/pen hit-tests fall through.

DM `ProcessInput` consumes those packets **before** WndProc, so the wheel fallback never double-processes a live DM gesture.

Flow (`Win32DirectManipulation.HandleContentUpdated`):

1. Read content transform `(scale, tx, ty)` → content-space `p = −t/s`.
2. First RUNNING update is baseline only (swallows the absolute jump).
3. Deltas → DIP → `ScrollUpdate` (RUNNING) or `MomentumUpdate` (INERTIA).
4. `OnScrollPhase` records onto the **singleton** gesture resampler (`AccumulateContactDelta` → latch after 8 DIP → `OnScrollTrackSample`). Still no offset write.
5. Phase 7 resamples contact to `frameTime − 12ms` (one displacement per vsync).
6. Lift: **no engine self-fling.** OS sends `MomentumBegin/Update/End`. `PhaseOsOwned` — integrator applies OS deltas **verbatim** (no `CoastStep` decay). At an edge, the tick converts residual velocity into `SnapBack` instead of stretching a finger-less band for ~2s.
7. Finger re-lands mid-coast: `INERTIA → RUNNING` → stop OS fling, back to `TouchpadTracking`.

If DM wedges (engage timeout 120 ms, inertia stall 250 ms, silent-owner, SUSPENDED): recovery Stop → recycle → disable. Then the fallback below takes over. Popups **never** get DM.

Physical mouse wheel preempts a live DM gesture (`TryStopForPhysicalWheel`) so a detented notch is not eaten as PTP.

#### Precision touchpad — fallback: promoted `WM_POINTERWHEEL`

When DM is down or the hit-test was rejected, the OS synthesizes `WM_POINTERWHEEL` bursts. Classifier (`Win32Platform`, latch for the gesture):

1. `PointerKind.Touchpad` → hi-res.
2. `rawDelta % 120 ≠ 0` → hi-res (free-spin mice too).
3. Sustained ≥6-packet exact-±120 burst, each &lt;50 ms → one-way demotion to hi-res (this hardware’s PTP often looks like mouse notches).
4. Else → detented **mouse** (`WheelAnimating`, not contact).

Hi-res path emits `ScrollBegin/Update` with linear `raw · 0.11 · (dpi/96)` DIP (OS already applied pointer acceleration). **Lift is guessed:** no `POINTER_FLAG_INCONTACT`. Adaptive silence 50–120 ms (1.4× median gap) fires `ScrollEnd` stamped with the **last packet** QPC, not wall-clock (a two-finger pan moves no cursor, so `GetMessageTime` never advanced — measured 83–843 ms late lift).

At `ScrollEnd`, fallback **does** seed one engine fling from the 40 ms `ImpulseVelocity` window (`WheelHiResFallback` only). A completed tail then silence reads v≈0 — no double inertia. This is structurally **lower fidelity** than DM: no true lift, no OS curve, silence heuristic.

Mis-route of exact-±120 PTP onto the mouse path is the classic “4–7× over-travel + a coast the fingers never asked for.”

#### Same integrator, different feel contracts

| | Finger | Touchpad DM | Touchpad fallback |
|--|--------|-------------|-------------------|
| Latch | Per-node `PendingRawOffset` | Singleton resampler | Same singleton |
| 1:1 while down | Finger px → offset (phase 7) | Resampled DM deltas | Resampled wheel-as-pan |
| Inertia owner | Engine `CoastStep` | **OS** `INERTIA` | Engine `CoastStep` at guessed lift |
| Rubber-band | Yes (contact) | Yes (contact); OS tail → SnapBack at edge | Yes (contact) |
| Re-grab band | Does **not** fold into anchor | Folds band via `ExcessFromBand` | Same as DM path (phase contract) |
| Concurrent lists | Yes (two fingers, two nodes) | One latched gesture | One latched gesture |
| Slop vs tap | 8 DIP; under-sampled flick ≠ click | Axis latch after 8 DIP accum | Same |
| Nested chain | Lift-time fling to outer | Same | Same |
| Drag-time chain to outer | **Not implemented** | **Not implemented** | **Not implemented** |

Public API does **not** expose this. `ScrollView` / `ItemsView` just scroll. Device class is a PAL fact (`ScrollDeviceClass.Touchpad` vs `Touch` vs `WheelDetented` vs `WheelHiResFallback`).

#### Mom-smooth bar for each

- **Touchpad:** DM must stay the producer. Fallback is correct but never “wow.” Resample to vsync. OS inertia verbatim. Never classify PTP as a mouse notch. Physical mouse preempts DM.
- **Finger:** 1:1 after slop, click never fires on a flick, fling from the move-stream (not the up-point), rubber-band that still moves at the cap, re-grab that does not jump. Under-sampled flick path stays — cheap digitizers exist.
- **Both:** one Tick, one offset write, present-thread apply so a decode hitch does not stutter the finger.

---

## 3. Physics / integrator (as built)

Owner files:

| File | Role |
|------|------|
| `src/FluentGpu.Engine/Animation/ScrollIntegrator.cs` | Phase-7 single writer (intended); conscious scrollbar timers |
| `src/FluentGpu.Engine/Animation/OverscrollPhysics.cs` | Rubber-band, `CoastStep`, snap-back spring, content transform |
| `src/FluentGpu.Engine/Animation/ScrollTuning.cs` | One shipping feel POD |
| `src/FluentGpu.Engine/Animation/ScrollIntoView.cs` | Programmatic bring-into-view / `ScrollTo` |
| `src/FluentGpu.Engine/Scene/Columns.cs` | `ScrollState` |
| `src/FluentGpu.Engine/Input/InputDispatcher.cs` | Intent recorder + offset chokepoint |
| `src/FluentGpu.Engine/Input/GestureRecognizer.cs` | Per-pointer FSM + velocity ring |
| `src/FluentGpu.Engine/Hosting/AppHost.cs` | Wiring, `SeedScrollFling`, phase-7 tick, holds, pacing |
| `src/FluentGpu.Engine/Hosting/Threading/PresentUnpacedDetector.cs` | RDP unpaced → software 60 Hz (WIP) |

### 3.1 Phase enum (`ScrollIntegrator`)

| Phase | Entered by | Physics |
|-------|------------|---------|
| `Idle = 0` | settle / clamp / cancel | none |
| `TouchpadTracking = 1` | contact (touch / PTP / fallback / DM RUNNING) | resampled 1:1; past-edge → Overscroll |
| `WheelAnimating = 2` | mouse notch, scrollbar, keyboard, programmatic | crit-damped chase to `PendingTarget*` |
| `Fling = 3` | lift `\|v\| ≥ FlingSeedGate`; `PhaseOsOwned` for DM INERTIA | `CoastStep` exact integral, or OS deltas verbatim |
| `Overscroll = 4` | contact past clamp | iOS rubber-band; offset pinned |
| `SnapBack = 5` | release with live band | spring ω = 12.5 rad/s |

Aux flags: `PhaseOsOwned`, `PhaseProgrammatic`, `PhaseWheel`, `PhaseTouchPan`.

Wheel / keyboard / programmatic **hard-stop at extents** (no band). Contact-descended states rubber-band.

### 3.2 Shipping constants (no `FG_*` feel knobs)

| Constant | Value | Where |
|----------|-------|-------|
| `FlingDecayPerS` | 0.05 / s | `ScrollIntegrator` |
| `FlingMinVelocityPxPerS` | 13 | integrator |
| `FlingSeedGate` | 50 px/s | `ScrollTuning` |
| `FlingMaxVelocityPxPerS` | 8000 | Android max-fling |
| `WheelChaseHalflifeMs` | 40 | `ScrollTuning` |
| `ResampleLatencyMs` | 12 | `ScrollTuning` (was 5; 12 matches ~1.5 PTP periods) |
| `RubberC` | 0.55 | iOS |
| `BandAsymptoteFraction` | 0.15 · viewport | `OverscrollPhysics` |
| `SnapBackOmega` | 12.5 rad/s | WebKit λ |
| Wheel notch | `max(48 DIP, 10%·viewport)` · lines/3 | `ScrollTuning` / `SPEC-INDEX` says 15% — **check on rework** |

`WheelEaseTauMs = 18` is **superseded** (FlipView projection only). `ResampleMaxPredictionMs` is historical; the resampler no longer predicts.

### 3.3 Wheel vs contact vs programmatic

- **Notch path:** `WheelNotch = rawAmount/120` → DIP distance from viewport + SPI lines. `SmoothScroll=true` (FluentApp default) accumulates `PendingTarget` and arms `WheelAnimating`. `SmoothScroll=false` → synchronous `WriteScrollOffset`.
- **DIP path:** synthetic/headless `ScrollDelta` bypasses notch scaling (keeps gates byte-identical).
- **Hi-res fallback:** linear `raw · 0.11 · (dpi/96)` DIP; lift = adaptive packet silence (50–120 ms). Weaker than DM (no true finger-lift, no OS inertia).
- **Programmatic:** `ScrollIntoView.Bring` / `BringInto` / `ScrollTo`. Animate → `WheelAnimating|PhaseProgrammatic` with distance-derived half-life. Snap → `Offset == Target` same frame. **Documented exception:** `LyricsView.ScrollActiveIntoView` (velocity-continuous, bespoke constants).

### 3.4 Nested chaining

`ScrollEl.Chaining`: Auto / Contain / None. **Lift-time fling hand-off** to `_chainOuter` is implemented. **Drag-time residual hand-off** is explicitly **not** in the integrator (`InputDispatcher` comment at the deleted `ApplyTouchPan` site). Wavee never overrides chaining (all `Auto`).

### 3.5 Open physics risks

1. Dual clocks: integrator `FrameQpcSec` vs DM `_pumpMs` EMA.
2. Headless leaves `FrameQpcSec = 0` → latest-sample fallback.
3. Direct-touch vs PTP band re-grab asymmetry (PTP folds band into anchor; direct-touch does not).
4. Touchpad-coast snap deferred (`OverscrollPhysics` TODO §5f).
5. `PresentUnpacedDetector` has **no VerticalSlice gate**.

---

## 4. ScrollBind / scroll-driven effects (as built)

Design: `docs/plans/generic-hookable-scroll-engine-design.md` (IMPLEMENTED 2026-06-24).

| File | Role |
|------|------|
| `Animation/ScrollBind.cs` | POD row + `ScrollBindTable` slab |
| `Animation/ScrollBindEval.cs` | `ApplyContinuous`, `ApplyPinAndFlagPass`, `BakeGeometry`, `RunObservers` |
| `Dsl/ScrollBindDsl.cs` | Authoring struct + `ScrollRange` + `ScrollChainingMode` |
| `Dsl/Element.cs` | `Element.ScrollBinds` on **every** element type |
| `Reconciler.cs` | `BakeScrollBinds`, named timelines |
| `AppHost.cs` | Phase 7 pin/flags + observers; phase 7.7 continuous heal pass |

**Eval rule:** binds **read post-physics, never re-integrate.** Continuous ops run at `ApplyScrollPosition` and `ArrangeViewport`. Pin ops run in phase 7 (need layout Y).

### 4.1 What Wavee actually authors

| Effect | API used | Where |
|--------|----------|-------|
| Overscroll hero stretch | `StretchFromTop` | `ArtistPage.Hero.cs` |
| Parallax | `From=Offset, To=TransY, Range=Px` | same |
| Collapse | `TransY` + `Opacity` + `PresentedH` | hero root, `DetailTracks` |
| Sticky chrome | `PinTop` + `OnFlag` | sentinel, context band, concert filter |
| Clip under band | `ClipTopAtViewport` | magazine + wash |
| Geometry observer | `OnScrollGeometryChanged` | page offset, Recents sticky, swipe-close, tail |

**Shy header is not a bind kind** — it is PinTop + PresentedH + RevealBinds + ClipTopAtViewport composed by the page.

**Pull-to-refresh is not implemented.** The escape hatch is an observer on `g.Band`.

### 4.2 Implemented but unused (dead surface)

`MorphLeftTo` / `MorphTopTo`, `ScrollChannel.Velocity`, `SignedPhase`, `ScrollRange.Enter` / `Frac` / `Overscroll` as authored ranges, `BindSink.Blur` / `ScaleY` / `ClipBottom`, `FlagBit` non-pin binds, `StuckBottomBit` (never set), `PinKind = 2` bottom-pin (never baked), `ScrollBind.FlagEaseInOut`, named `ScrollTimeline` (VerticalSlice only; Wavee removed it).

`ScrollDemo.cs` / `TrackListDemo` has **no ScrollBinds**.

### 4.3 Dual sticky mechanisms

1. `ScrollBindDsl.PinTop` / `ClipTopAtViewport` (Artist / Detail).
2. `ItemClipTopInset` + `OnScrollGeometryChanged` (Recents overlay — **not** a ScrollBind).

A rework should collapse these to one.

---

## 5. Controls / authoring surfaces (as built)

**Two engine primitives, not one control.**

| Primitive | Factory | Role |
|-----------|---------|------|
| `ScrollEl` (`ElementTypeId = 5`) | `Ui.ScrollView(content, horizontal?)` | Clipped generic viewport |
| `VirtualListEl` (`ElementTypeId = 6`) | `Virtual.*` / `ItemsView` | Self-scrolling virtualized collection |

Wrapping `Virtual.List` / `ItemsView` in `ScrollView` is the known anti-pattern (Home / Sidebar comments).

### 5.1 `ScrollEl` props (defaults)

Vertical; `Grow=1` via factory; `ContentSized=false`; zoom off (0.1–10); `EdgeCues=Auto`; `Chaining=Auto`; `Snap=null` (reconciler does not touch snap fields); `SuppressScrollBar=false`; `AlwaysShowScrollbar=false`; `OnScrollGeometryChanged`; `ScrollKey`; `ScrollTimeline`; `OnRealized`.

### 5.2 Scrollbars — three things named “scrollbar”

| Thing | What it is |
|-------|------------|
| Engine overlay | `SceneRecorder.EmitScrollbar` — auto-hide overlay on the viewport, `FadeT`/`ExpandT` from integrator. **Not layout-reserved.** |
| `ScrollBar` control | Standalone. **Two `Create` overloads** (legacy 2px panning vs full 12px WinUI anatomy). Gallery / gates; **not used in Wavee**. |
| `AnnotatedScrollBar` | Sibling rail + labels. Wavee **Recents only**. Vertical only. |

`IScrollController` is live (ItemsView mux). **`ScrollControllerAdapter.Attach` has zero production callers.**

### 5.3 Keyboard

`ItemsView` and `AnnotatedScrollBar` implement arrows / Page / Home / End. **Plain `ScrollView` has no generic Page/Home handler.**

### 5.4 Wavee usage (short)

- **`ScrollView`:** Search, Artist, Discography, Concerts, Home (state), Sidebar rail, chips (horizontal + `SuppressScrollBar` + `AutoEdgeFade`), dialogs (`ContentSized` + `MaxHeight`), Lyrics, Now Playing, notifications.
- **`ItemsView` / `Virtual.*`:** Sidebar main list, Library, DetailTracks, Search results, Recents, Home virtual, Lyrics custom, gallery 5k.
- **`LazyGrid`:** in-page window against an **outer** `ScrollView` (Artist / Discography) — a third virtualization shape.
- **`AnnotatedScrollBar`:** Recents only.
- **`ScrollBar` standalone:** none in Wavee.

`Repeater` is a thin factory over `Virtual.*` (third entry point, same primitive). `Virtual.VariableList` is legacy vs `Virtual.Measured` (tests only).

---

## 6. Layout / record / present (as built)

Scroll is **layout-free** (`layout.md` §6 / `Columns.cs` `ScrollState` remarks).

- Layout publishes `Content*` / `Viewport*`, clamps offset on resize, writes content transform after arrange.
- Input/integrator owns `Offset*` / band / zoom / phase.
- Virtualizer owns realized window / anchor / `PendingAnchorShift` (the honest exception to single-writer: a **coordinate-frame shift**, not a new scroll position).

**Compositor-only** when only `TransformDirty` and the virtual window still covers the visible band. **Not** compositor-only on:

- `VirtualRangeDirty` (window escaped)
- Phase 7.6 `ReRealizeVirtuals` catch-up after a fast fling
- D1 realize-after-layout in `ArrangeViewport`
- ScrollBind pin/observer pass (cheap, still UI-thread)

**Backdrop during scroll:** `UserScrollActive` holds self-blur 0.12 s, main scroll hold 0.45 s, `AcrylicScrollHold` refreshes ~every 4 frames, image reveals suppressed. `FG_BISECT_NO_IMAGE_PUMP` is a kill switch for diagnosis.

**Perf residual:** translated-span copy is **blocked** for moving scroll content (correct, costs GPU). `scroll-perf-fix-plan.md` (span reuse, LazyGrid observer, edge-fade RT) is **not implemented**.

---

## 7. Tests, diagnostics, design-doc timeline

### 7.1 Gates (headless)

Primary: `src/FluentGpu.VerticalSlice/Suites/ScrollSuite.cs` (~7.7k lines). Also `TouchSuite`, `DiagnosticsSuite` (ScrollTrace), sticky binds in `AnimSuite`.

Feel/integrator (v2 + v2.1) is actually asserted:

- `gate.scroll.engine-owned-integrator`, `single-writer`, `dt-invariance`, `resample-cadence`, `contact-1to1`
- `coast-distance`, `impulse-velocity`, `overscroll-rational`, `relatch-catchup`
- `pointerdown-cancels`, `transition-matrix`, `alloc-zero`
- virtual re-pin under gesture / measured correction / wheel-chase extent shrink

**Gaps:** real DM on hardware (unit tests + dm-probe only), `PresentUnpacedDetector`, span-reuse, LazyGrid 24px observer, macOS producer, content-approximation metric.

`DirectManipulationPacingTests.cs` is explicit: **not** a VerticalSlice substitute; field substitute is ScrollTrace note 107.

### 7.2 Field feel pipeline (`ops/diag`)

`wavee-scroll-session.ps1` → free-scroll → `scroll.csv` + `feel-summary.json`. Pillar A (finger glue / latency) and Pillar B (cadence) stay separate. Uncommitted expansion: PresentMon join, gexec dedup, latch/wheel-drop targeting.

Diag flags (not feel knobs): `FG_SCROLL_TRACE`, `FG_SCROLL_LOG`, `FG_SCROLLLOG`, `FG_SCROLL_PERF`, `FG_OFFSET_JUMP`, `FG_SCROLL_DIRHYST` (**this one changes latch behavior**), `FG_BISECT_NO_IMAGE_PUMP`, `FG_SCROLL_PRESENT_INTERVAL0`.

Deliberately excluded from feel sessions: `FG_DIAG`, `FG_SCROLL_LOG` (observer effect), `dotnet-trace`.

### 7.3 Plan timeline (do not treat older plans as live)

| Doc | Status vs code |
|-----|----------------|
| `generic-hookable-scroll-engine-design.md` | Landed (ScrollBind) |
| `scroll-feel-rework-design.md` (v1) | Phase 0 only; **superseded by v2** |
| `scroll-feel-rework-v2-design.md` | Core landed; single-writer **not fully true** (see §8) |
| `scroll-feel-v2.1-edge-momentum-addendum.md` | Partially landed (bounce, relatch) |
| `scroll-rubberband-stutter-fix-plan.md` / `scroll-accel-decel-tuning-plan.md` | Pre-v2; `FG_TP_*` / `FG_OS_*` **gone from src** |
| `scroll-perf-fix-plan.md` | **Not implemented** |
| `wavee-scroll-feel-diagnostics-plan.md` | Header says unlanded; `ops/diag` is the practical impl |
| `docs/site/api/FluentGpu.Animation.ScrollAnimator.yml` | **Stale** — class renamed |

`SPEC-INDEX.md` owns: engine-owned scroll, wheel `max(48, 15%·vp)` (code currently 10% — reconcile on rework), `IScrollController`, named timelines, `ScrollIntoView`.

---

## 8. Dead code, dual paths, offset writers

### 8.1 Safe to delete / stop exposing

| Item | Class |
|------|-------|
| `ScrollAnimator` API YAML + census name | STALE DOC / SHADOWED |
| `ScrollControllerAdapter.Attach` | DEAD (0 callers) |
| PinKind 2, Morph*, Velocity/SignedPhase binds, Blur/ScaleY/ClipBottom sinks | DEAD surface |
| `StuckBottomBit` | never computed |
| Plan `FG_TP_*` / `FG_OS_*` / `FG_WHEEL_*` / `FG_DM_*` | not in `src/` |
| `ApplyTouchPan` / `TickTouchpad` / `ShapeTouchpadPacketDelta` | deleted; comments only |
| `WheelEaseTauMs` on integrator path | superseded |

### 8.2 Dual by design (keep one, delete or hide the other)

- Overlay bar vs `ScrollBar` control vs `AnnotatedScrollBar`
- `Virtual.VariableList` vs `Virtual.Measured`
- `Repeater` vs `Virtual.*` vs `ItemsView`
- `LazyGrid` in outer `ScrollView` vs self-scrolling `Virtual.Grid`
- `PinTop`/`ClipTopAtViewport` vs Recents `ItemClipTopInset`
- DM producer vs `WM_POINTERWHEEL` fallback (fallback must stay)

### 8.3 One offset, many writers (the rework blocker)

**Canonical (v2):** `ScrollIntegrator.Tick` → `WriteScrollOffset` / `WriteOverscroll` → `SetScrollOffset` / `ApplyScrollPosition`.

**Intent columns (OK):** `PendingTarget*`, `PendingRawOffset`, `FlingVelocity`, `Phase` — dispatcher records, integrator consumes.

**Direct `Offset*` mutators that bypass the seam:**

| Site | Why |
|------|-----|
| `FlexLayout.ArrangeViewport` | Clamp / restore / anchor pin |
| `Reconciler` | Mount / `ScrollKey` restore / reset |
| `LazyGrid.PreserveColumnAnchor` / `MaybeInitialScroll` | Column anchor, initial scroll |
| `ItemsView.ScrollByDelta` / `CorrectMeasuredExtent` | Drag-reorder edge scroll, measured correction |
| `LyricsView.LatchViewport` | Instant lyrics follow |
| `TreeView` expand | Direct offset |
| `ScrollIntoView` snap branch | `Offset == Target` arrest |

`gate.scroll.single-writer` audits integrator vs `ScrollWriter.Direct` on the chokepoint. It **does not** see layout/virtualization/lyrics pokes. A present-thread Tick is illegal until these are intents or explicitly blessed as coordinate shifts (v2 already blesses `PendingAnchorShift` that way).

### 8.4 Uncommitted WIP (working tree at inventory time)

`AppHost` pacing + `PresentUnpacedDetector`, `ScrollTrace` encodings, `InputDispatcher` `ContainingScrollerForAxis` / phase-fallback wheel, `SceneRecorder`, D3D12/RHI present stats, `FluentApp` diag, `ops/diag/*`, VerticalSlice suite diffs. **Land or rebase before rewriting the integrator.**

---

## 9. Reference systems we looked at

Local trees (no extra clones required for WinUI / Avalonia / Gecko):

| Tree | What to steal | What not to steal |
|------|----------------|-------------------|
| `C:\WAVEE\microsoft-ui-xaml` `ScrollPresenter` | Split: presenter = motion, ScrollView = chrome; `IScrollController`; PTP never on `PointerWheelChanged` | `InteractionTracker` owning pixels |
| `C:\WAVEE\SmoothScroll.Avalonia` | `InteractionTracker` as a **compositor-server object**; `Idle/Interacting/Inertia/CustomAnimation` | WUC expression animations |
| `C:\WAVEE\chromium-cc-input` (`cc/input`, cloned 2026-08-17) | Compositor can scroll without main thread; fling aligned to vsync deadline; `MainThreadScrollingReason` | Browser process topology |
| `C:\WAVEE\flutter-scroll` (`scroll_*.dart`, cloned 2026-08-17) | `Idle / Drag / Ballistic / Driven` activity names; `ScrollPhysics` vs widget split; `ScrollController` as the handle | UI-thread build every frame (still janks on `CustomScrollView`) |
| Android `InputConsumer` resampling | Interpolate/extrapolate to vsync − latency; cap prediction | — |
| CSS scroll-driven animations | `animation-timeline: scroll()` / `view()`; sticky is layout, effects are composite | JS scroll listeners |

---

## 10. Proposed public API (the code authors would write)

Post-cut API under **R5**. Names below are a proposal; confirm or rename before the replacing spec.

Principles:

1. **One motion primitive.** Chrome is a wrapper. Lists **are** viewports.
2. **Recipes for the five Wavee effects.** Generic bind is an escape hatch, not the default.
3. **One `ScrollController`.** Offset is a signal you bind; you never `setState` per pixel.
4. **No phase/intent columns on the public surface.**
5. Integrator internals may keep today’s POD math; authors never see it.

### 10.1 Viewport vs ScrollView (WinUI split, FluentGpu-shaped)

```csharp
// Motion only — today's ScrollEl. No chrome, no keyboard.
Viewport.Create(content, new ViewportOptions
{
    Axis       = Axis.Vertical,       // one axis; nest for the other
    Overscroll = Overscroll.Contact,  // rubber-band for finger/PTP only
    Snap       = Snap.None,           // or Snap.Mandatory(48f)
    Chain      = Chain.Auto,          // CSS overscroll-behavior
    Bar        = Bar.Overlay,         // Overlay | None (Adjacent reserved)
});

// Chrome wrapper — overlay bar + PageUp/Home/End.
ScrollView.Create(content);           // = Viewport + bar + keyboard
ScrollView.Create(content, new ScrollViewOptions
{
    Axis = Axis.Horizontal,
    Bar  = Bar.None,                  // chip rails
    EdgeFade = true,
    ScrollKey = "search:" + query,
});
```

`Ui.ScrollView` can remain as the factory name if we do not want a `Viewport` type in the DSL — but the **split** (motion vs chrome) must exist. Today keyboard lives only on `ItemsView` / `AnnotatedScrollBar`.

### 10.2 Lists are viewports

```csharp
ItemsView.Create(count, TrackRow, layout);          // scrolls itself
Virtual.Measured(count, row, measure);              // advanced L2
// NEVER: ScrollView(ItemsView.Create(...))
```

`LazyGrid` should become either a `Virtual.Grid` (self-scrolling) or a documented “window against parent `ScrollController`” with the parent offset read through the controller — not a third private `ScrollState` poke.

### 10.3 The only public handle

```csharp
sealed class ScrollController
{
    FloatSignal Offset { get; }     // bind it
    float Extent { get; }
    float Viewport { get; }
    bool  UserScrolling { get; }    // today's UserScrollActive — all viewports, not only bind owners

    void ScrollTo(float offset, Animate animate = Animate.Glide);
    void ScrollBy(float delta, Animate animate = Animate.Glide);
    void BringIntoView(NodeHandle node, float align = float.NaN);
}

// In a page:
var page = UseScroll();             // or ScrollView.Controller
page.ScrollTo(0);
```

`IScrollController` (annotated rail) stays as the **sibling chrome** seam. `ScrollControllerAdapter` is deleted; `ItemsView` already muxes this.

### 10.4 Effects — recipes, then escape hatch

Today (Artist hero):

```csharp
ScrollBinds =
[
    new() { StretchFromTop = true },
    new()
    {
        From = ScrollChannel.Offset, To = BindSink.TransY,
        Range = ScrollRange.Px(0f, photoH),
        OutStart = 0f, OutEnd = photoH * PhotoParallaxFraction,
        Ease = Easing.Linear,
    },
];
```

Proposed:

```csharp
var page = UseScroll();

ScrollView(
    VStack(
        ArtistHero()
            .StretchFromTop()
            .ParallaxY(0.4f)
            .Collapse(to: CompactH),

        CompactBar().Sticky(),

        Tracks().ClipBelow(CompactH)
    )
);
```

Engine expansion (still zero-alloc POD under the hood):

| Recipe | Compiles to (today) |
|--------|---------------------|
| `.StretchFromTop()` | `StretchFromTop` closed-form |
| `.ParallaxY(t)` | `Offset → TransY` ranged lerp |
| `.Collapse(to, over)` | `TransY` + `Opacity` + `PresentedH` |
| `.Sticky(top:)` | `PinTop` |
| `.ClipBelow(inset)` | `ClipTopAtViewport` |

Escape hatch for one-offs (keep the generic bind, hide it from Wavee pages):

```csharp
el.Scroll(Offset.Px(0, 200), Opacity = 1f.To(0f));
el.Scroll(Offset.Frac(0, 1), Blur = 0f.To(8f));   // if we keep Blur sink
```

**Delete from the default authoring vocabulary:** Morph, SignedPhase, Velocity binds, named timelines (until a sibling-backdrop page needs them again), `FlagBit` predicates (use `OnSticky` / `OnMoving` recipes if needed).

### 10.5 Observer (cold)

```csharp
page.OnGeometry(g => { /* change-gated; UI thread; never per-px */ });
```

Same as `OnScrollGeometryChanged`. Pull-to-refresh, if we ever want it, lives here (`g.Band`), not as a new bind kind.

---

## 11. What “show my mom” feels like (acceptance, not API)

She never sees §10. She two-finger flicks Artist on the laptop, then flicks Recents with a finger on a touch screen.

**Precision touchpad (the laptop test):** two fingers on the glass, content is glued. No 1-packet/2-packet stutter. Lift and Windows’ own inertia coasts — not our guessed silence timer. A slow two-finger drag to a stop does **not** fling. If DirectManipulation is wedged, the wheel-fallback still scrolls correctly but will not pass this test (no true lift, no OS curve).

**Finger (the tablet / touch-screen test):** one finger, 8 DIP slop so a tap still taps. Past slop, the row she was pressing does **not** click. Content follows the finger 1:1 including rubber-band at the top. A short flick the digitizer under-sampled still scrolls, never opens the album. Lift coasts on **our** friction (`CoastStep`); there is no OS inertia for `PT_TOUCH`.

| She feels | Engine must |
|-----------|-------------|
| Glued (touchpad) | DM deltas + resample to `frameTime − 12ms` |
| Glued (finger) | Per-node `PendingRawOffset` → one phase-7 write; not per-event `SetScrollOffset` |
| Coast after touchpad lift | DM `INERTIA` verbatim; **no** engine self-fling on that device |
| Coast after finger lift | Engine `CoastStep` from move-stream velocity (not the up-point) |
| Slow stop does not fling | Single 40 ms window; silence → v = 0 |
| Flick is not a click | Slop + under-sampled-pan completion |
| Wheel lands | Crit-damped chase; **hard stop** at extents (never a band) |
| Pull-down stretch | iOS band on **contact only** (finger and PTP); never saturates dead |
| Fast flick, no blank strip | Present-thread transform; UI thread only realizes new rows |
| Nested shelf | Inner takes the gesture; edge hands off **during drag**, not only at lift |

**Tells she must never feel (today’s bugs / residues):** PTP classified as mouse (4–7× travel); flick that sometimes bricks; rubber band freeze; 1-packet/2-packet stutter on 120 Hz; empty rows on fling; UI hitch = scroll hitch; a quick finger flick that selects a track.

Honest ceiling (corpus law): a **sustained GPU stall** still shows. Mom-smooth is “the list never waits on C#,” not “physics ignores the GPU.”

---

## 12. How we would get there (approaches)

Locked by §0. Old “C then B” recommendation is **void**.

### A — WinUI clone (`InteractionTracker` owns pixels)

Butter PTP. **Illegal:** DComp HWND, virtualization, headless determinism. Still rejected.

### B — New runtime, present-thread offset (REQUIRED)

New files. Input + Tick + transform on the present/compositor thread. UI thread hit-tests, latches the target, and realizes windows (“slow scroll”). This is the only path that still glides when C# is busy. Then delete R3.

### C — Clean the current integrator in place

**Illegal under R1.** That is the code we are deleting. A quieter UI-thread Tick still dies when C# hitches, which fails R2.

**Sequence (not a polish ladder):**

1. Write the replacing design spec against §0 + this inventory (next, after one remaining cutover check).
2. Implement the new runtime (B) with new VerticalSlice gates.
3. Point `ScrollView` / `Virtual.*` / `ItemsView` / Wavee recipes at it.
4. Delete the kill list in R3. Wavee compiles only against the new mechanic (R8).

Do not start with a 40-property `ScrollPresenter`. The API stays tiny on purpose (R5).

---

## 13. File map (live code — kill / keep)

As-built owners. R3 deletes the feel column; R4 keeps PAL / layout / virtualize / present. After cutover this table is historical.

| Concern | Owner |
|---------|-------|
| Integrator / phases / conscious bar | `src/FluentGpu.Engine/Animation/ScrollIntegrator.cs` |
| Rubber-band / coast integral / content transform | `Animation/OverscrollPhysics.cs` |
| Feel POD | `Animation/ScrollTuning.cs` |
| Programmatic | `Animation/ScrollIntoView.cs` |
| Binds | `Animation/ScrollBind.cs`, `ScrollBindEval.cs`, `Dsl/ScrollBindDsl.cs` |
| Intents / wheel / touch / chokepoint | `Input/InputDispatcher.cs` |
| Gestures | `Input/GestureRecognizer.cs` |
| `ScrollState` | `Scene/Columns.cs` |
| Layout extents | `Layout/FlexLayout.cs` (`ArrangeViewport`) |
| Record / overlay bar / edge cues | `Render/SceneRecorder.cs` |
| Acrylic hold | `Render/AcrylicScrollHold.cs` |
| Frame loop | `Hosting/AppHost.cs` |
| Unpaced present | `Hosting/Threading/PresentUnpacedDetector.cs` |
| DM producer | `src/FluentGpu.Windows/Pal/Win32DirectManipulation.cs` |
| Wheel / pointer producer | `Pal/Win32Platform.cs` |
| Compositor clock | `Pal/Win32CompositorClock.cs` |
| DComp present | `D3D12/D3D12Device.cs` |
| `ScrollEl` / `Ui.ScrollView` | `Dsl/Element.cs`, `Dsl/Factories.cs` |
| Virtual list | `Reconciler/VirtualListEl.cs`, `Scene/VirtualLayout.cs`, `Controls/Virtual.cs` |
| ItemsView / controller | `Controls/ItemsView.cs`, `ListOptions.cs`, `IScrollController.cs` |
| Bars | `Controls/ScrollBar.cs`, `AnnotatedScrollBar.cs` |
| Trace | `Foundation/ScrollTrace.cs`, `ScrollLog.cs` |
| Gates | `VerticalSlice/Suites/ScrollSuite.cs`, `TouchSuite.cs`, `DiagnosticsSuite.cs` |
| Field capture | `ops/diag/wavee-scroll-session.ps1`, `pack-feel-summary.ps1`, `AGENT.md` |
| Wavee hero | `src/apps/Wavee/Features/Detail/ArtistPage.Hero.cs` |
| Wavee Recents rail | `.../RecentsPage.cs` |
| Lyrics exception | `src/apps/Wavee/Features/Player/LyricsView.cs` |

---

## 14. Decisions and what still blocks the spec

**Closed (2026-08-17):** C vs B. Feel is R2. Architecture is B (R6). In-place C is illegal (R1). Delete the current mechanic (R3). Tiny API (R5). Replace cutover (R8).

**Does not block:** another feel-knob pass on `ScrollTuning`. That file is on the kill list.

**Still before a replacing spec:** confirm the public names in §10 (`ScrollView` / `UseScroll` / recipes) are the ones Wavee will author after the delete — or say if you want different names. Then write the spec against §0, not against v2.
