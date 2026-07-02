# Scroll-Feel Rework — phase-tagged input, one integrator, OS-owned PTP momentum

Status: **DECIDED — Phase 0 COMPLETE (GO for DManip), Phases 1–4 not yet landed.**
Phase 0 verdict (2026-07-01, dev machine, ARM64 Win11 26300, probe at `tools/dm-probe`): **cell B
PASS** — with `EnableMouseInPointer(true)` ON: 13/13 `DM_POINTERHITTEST→SetContact` S_OK, full
`READY→RUNNING→INERTIA→READY` cycles incl. 20 INERTIA entries and 7 `INERTIA→RUNNING` re-grabs,
639 content updates at ~15 ms cadence, OS momentum decaying smoothly to 0, engage latency ~2 ms,
`ProcessInput` consumed 0 messages (pump kept anyway — browsers do). **Root-cause revision:** the
39f10bd failure was the integration shape alone (wheel injection instead of `SetContact`);
mouse-in-pointer coexists fine — cells A/C/D/E not needed for the decision.
Deliverable of the 2026-07-01 scroll-feel investigation (7-agent recon over our pipeline, WinUI3
`ScrollPresenter`/`ScrollView` + the dxaml/DManip stack, Chromium/Firefox/iOS/Android/Flutter
physics; two independent design drafts; adversarial judge pass; orchestrator review).
Companion/precedent: `generic-hookable-scroll-engine-design.md` (ScrollBind — untouched by this
rework, §8) and `animation-engine-rework-design.md` (the landing-plan discipline this doc copies).
Canon reconciliation (`design/` owning docs + `check-canon.ps1`) happens per landing phase, not now.

---

## §0 Thesis + feel target

**The complaint** — precision-touchpad (two-finger) and touch scrolling feel bad/not smooth; mouse
wheel is fine — **is architectural, not a tuning miss.** Today a PTP pan reaches us as promoted
`WM_POINTERWHEEL` packets: no gesture phase, no finger-lift signal (`POINTER_FLAG_INCONTACT` is
never set), no reliable device identity. We classify by delta signature, re-shape through two
stacked nonlinear gain curves (Win32 soft-knee ×0.11, then `ShapeTouchpadPacketDelta` keyed off
*frame-coalesced* sums), re-smooth through a 14 ms one-pole low-pass, and guess gesture end with a
120 ms quiet-latch. The content is therefore **never 1:1 with the fingers**, the effective gain is
frame-pacing-dependent, and every push-vs-coast decision is a heuristic. The wheel path feels OK
because it is a short, dt-invariant, closed-form velocity fling with none of that layering.

WinUI3's new `ScrollView` never faces this problem: `ScrollPresenter` writes **no physics at all**
— the OS `InteractionTracker` (compositor-side) consumes PTP + wheel via
`VisualInteractionSourceRedirectionMode::CapableTouchpadAndPointerWheel` and owns contacts, decay,
railing and rubber-band. Chromium and Firefox, who like us own their scroller, both solved PTP the
same way: a **DirectManipulation viewport used purely as an event source** — phases + OS-computed
momentum in, zero pixels moved by DManip. Our own removed DManip attempt (39f10bd) failed on a
different integration shape (wheel *injection*, likely starved by `EnableMouseInPointer`) and is
treated here as a root-caused defect, not a dead end.

**The fix, in one sentence:** one OS-agnostic **phase-tagged scroll contract**
(ScrollBegin/Update/End + MomentumBegin/Update/End) produced by four producers — DManip (Windows
PTP, primary), the touch gesture arena, a hardened wheel-fallback classifier, and later macOS
NSEvent — consumed by **one integrator** (`ScrollAnimator`; the bespoke `TickTouchpad` second
integrator dies), with **contact deltas applied 1:1 raw** and **momentum owned by exactly one
party** per device class.

**Feel target:** WinUI3-ScrollView semantics (hard-stop wheel/keyboard/programmatic at extents,
rubber-band only for contact gestures, additive impulses, seamless velocity-carrying interrupt
handoff) + macOS-grade contact feel (1:1, phase-driven, OS momentum applied verbatim) + our
already-liked wheel and programmatic behavior preserved byte-for-byte.

Honest scope: near-zero-alloc (0 managed alloc phases 6–13 holds throughout); the async render
seam still adds ≥1 present frame to input→photon (measured, not fixed, here — §10); the fallback
path without DManip is *correct* (never double-integrates) but structurally lower-fidelity (no
true finger-lift) — DManip is the ceiling.

---

## §1 The phase-tagged scroll contract (owner: this doc)

Six additive `InputKind` members (Foundation/Events.cs / Seams/Pal/Pal.cs) — the existing
`InputKind.Wheel` stays for detented mouse notches and element-level `OnPointerWheel`:

```
ScrollBegin    = 12   // contact engaged (touch pan claimed / DManip RUNNING entered)
ScrollUpdate   = 13   // contact delta — 1:1 raw application
ScrollEnd      = 14   // contact lifted, no OS momentum follows (hard lift)
MomentumBegin  = 15   // OS inertia starts (DManip INERTIA)
MomentumUpdate = 16   // OS inertia delta — apply verbatim
MomentumEnd    = 17   // inertia finished or cancelled
```

`InputEvent` gains three trailing-optional fields (all existing call-sites unchanged):

```
byte  ScrollPhaseSeq   // monotonic per-gesture packet ordinal (wraps) — loss/ordering diagnostics
long  QpcTicks         // QueryPerformanceCounter stamp of this packet; 0 = headless/frame-clock
byte  DeviceClassRaw   // 0=unset 1=Touchpad(DManip) 2=Touch 3=WheelDetented 4=WheelHiResFallback 5=NSTrackpad(reserved)
```

Deltas reuse `ScrollDelta`/`ScrollDeltaX` (DIP, "positive = toward content end"). No release
velocity crosses the seam — the integrator computes it (§2; the earlier `Pressure`-slot pun is
rejected). The contract crosses the PAL seam as plain PODs; `FluentGpu.Engine` stays TerraFX-free.

**(phase kind × DeviceClassRaw) → integrator state** (the single mapping table; §6 owns the states):

| Event | Touchpad(DManip) | Touch | WheelHiResFallback | WheelDetented |
|---|---|---|---|---|
| ScrollBegin/Update | ContactPan (1:1) | ContactPan (1:1) | ContactPan (1:1) | — (uses `Wheel`) |
| ScrollEnd | Idle (a hard lift; DManip momentum, if any, arrives as Momentum* instead) | SelfFling if `|v|≥50`, else Idle/OverscrollRelease | SelfFling if `|v|≥50` (an OS tail, having run 1:1 as contact, leaves `v≈0` — see §5) | — |
| MomentumBegin/Update | OsMomentum (verbatim) | — (engine coasts; no Momentum* events cross the seam) | — (fallback never emits Momentum kinds; §5) | — |
| `Wheel` (notch) | — | — | — | WheelFling (unchanged) |

**macOS mapping (future Cocoa producer):** `NSEventPhaseBegan/Changed/Ended|Cancelled` →
ScrollBegin/Update/End; `momentumPhase Began/Changed/Ended` → MomentumBegin/Update/End;
`NSEventPhaseMayBegin` (fingers resting, no motion) → **emit nothing** (wait for Began — do not
invent a seventh kind). `DeviceClassRaw=5`. The contract was shaped so this is a pure relabeling.

A `HeadlessScrollProducer` (Headless/) scripts all six kinds with synthetic `QpcTicks` straight
into the `InputEventRing` — the determinism driver for every gate in §13 (replaces
`DriveTouchpadPan`).

---

## §2 Coalescing + the velocity-timestamp mechanism

**Ring coalescing rule** (`InputEventRing`, Pal.cs): `ScrollUpdate`/`MomentumUpdate` for the same
gesture coalesce exactly like `Wheel` today — deltas summed, **newest** `QpcTicks`/`ScrollPhaseSeq`
kept. `ScrollBegin`/`ScrollEnd`/`MomentumBegin`/`MomentumEnd` **never coalesce** (like Down/Up).
The integrator thus sees exactly one Begin, at most one summed Update, and one End per frame — the
Chromium vsync-coalesce contract, which we already had for wheel and which is the correct per-frame
resample (this is *why* no low-pass is needed on top).

**The pre-coalesce velocity side ring (the B2 mechanism, spec'd).** Per-frame summing destroys the
per-packet timing the release-velocity estimator needs. Fix: `InputEventRing` gains a fixed POD
side ring of velocity samples that coalescing never touches:

```
struct ScrollVelSample { long QpcTicks; float Delta; byte Axis; byte Device; }   // 14 B packed
// fixed inline array, capacity 64, drop-oldest on overflow (velocity is a trailing estimate)
```

Producers append one sample per **raw contact-phase packet** (`ScrollUpdate` class only; momentum
and wheel packets don't feed release velocity) at enqueue time — before coalescing. The dispatcher
drains the side ring every frame (alongside `Drain()`) into a per-gesture IMPULSE estimator state
(a fixed struct in `InputDispatcher` — the successor of today's `TouchVelocity` 8-slot ring).
Zero allocation; headless-deterministic (scripted stamps).

**IMPULSE estimator (Engine-side, one for all producers — including macOS later):**
`v_i = δ_i / Δt_i` per successive samples; `W = Σ (v_i − v_{i−1})·|v_i|` (first term ½-weighted);
release `v = sign(W)·√(2|W|)`. Horizon `VelHorizonMs ≈ 66` (drop older samples); ≥2 samples
required; if the newest sample is older than `AssumeStoppedMs = 40` at lift → velocity 0 (the
finger stopped before lifting). **The lift timestamp is the gesture's last input stamp, never the
detection wall-clock:** touch uses the real `WM_POINTERUP` QPC; the wheel-fallback synthesizes lift
as the **last packet's** `QpcTicks` (silence is *detected* `HiResLiftMs` later — evaluating the
window at detection time would always trip `AssumeStoppedMs` and zero every fallback fling). Android ships exactly this for the scroll axis because it is
robust to the last-sample noise that corrupts regression fits — the same noise our current 50 ms
LSQ needed the don't-fold-the-up-point hack for. QPC → seconds via a cached `1/QueryPerformanceFrequency`;
non-monotonic/duplicate stamps are skipped (headless 0-stamps ⇒ vacuous zero velocity, preserving
gate determinism).

**Clock split (invariant):** QPC message stamps feed *velocity only*; *position* always advances on
frame dt with closed-form dt-invariant steps and the `dt ≤ 0` Resync bail on **every** scroll tick
(the guard `ScrollAnimator.Tick` already has and `TickTouchpad` fatally lacked — H6). Contact
deltas arriving on a zero-dt frame still fold into the pending sum (distance preserved; only that
packet's velocity sample defers, which IMPULSE tolerates).

---

## §3 The DirectManipulation producer (FluentGpu.Windows, primary Windows PTP)

New `Pal/Win32DirectManipulation.cs` (the 39f10bd filename returns; the *design* does not — the old
one muxed DManip transforms into the scroller, this one is event-source only) +
`directmanipulation.comabi.json` (slot manifest, **placeholders pending AbiHarvest** — do not trust
guessed slot indices). All flat IUnknown COM via the existing hand-vtable calli pattern
(D3D12/DWrite precedent). **Every call on the UI thread.**

**Lifecycle (once per top-level window, after swapchain/DComp init):**

```
CoCreateInstance(CLSID_DirectManipulationManager)          → _mgr
_mgr.GetUpdateManager()                                    → _upd
_mgr.CreateViewport(frameInfo: null, hwnd)                 → _vp
_vp.SetViewportRect(fixed 1000×1000 fake content)          // Chromium rect; reset re-centers after each gesture
_vp.AddConfiguration(TRANSLATION_X | TRANSLATION_Y | TRANSLATION_INERTIA
                   | SCALING | SCALING_INERTIA | INTERACTION)
_vp.SetViewportOptions(MANUALUPDATE)                       // we drive the clock
_vp.AddEventHandler(hwnd, sinkCcw)                         // hand-vtable CCW, 3 methods
_vp.Enable();  _mgr.Activate(hwnd)
```

- **`SCALING` is configured and pinch gestures suppress pan emission entirely** (orchestrator
  review finding, confirmed by the Phase 0 trace): a pinch engages the viewport as a manipulation
  whose *translation* components swing by thousands of px per update (zoom-about-a-point math,
  observed scale 1.13→2.06 with |dx| up to ~1200 px/update) — naive consumption would violently
  scroll. Rule: while a gesture's `|scale − 1| > 0.01`, the producer emits **no pan deltas at all**
  for that gesture (pinch-zoom is future work; the contract gains a scale channel then).
  Rejected: omitting SCALING — pinch contacts would still hit the viewport via `DM_POINTERHITTEST`
  with undefined interpretation.
- **No `RAILS_X/Y`:** railing is owned by the integrator's axis latch (§6) — one owner. DManip
  rails would fight it (and under DManip the gesture arena never sees the PTP contact, so the
  latch is the only railing authority on this path).

**Pump placement (MANUALUPDATE):** inside `Win32Platform.PumpInto` (frame phase 1):
`_mgr.ProcessInput(&msg)` for **every** pumped message in the `PeekMessageW` drain (before
`DispatchMessageW`) — the missing piece of the prior integration — then `_upd.Update(null)`
**once at the end of the drain**. The sink fires synchronously inside `Update`, so its phase PODs
land in the ring in the same frame: pump (1) → dispatch (2) folds deltas → integrator tick (7)
advances physics. Same-frame latency as a wheel notch today, now phase-correct.

**The sink CCW:** `IDirectManipulationViewportEventHandler` (3 methods) exposed exactly like
`Win32DropTargetCcw` — `NativeMemory.Alloc`'d struct, static
`[UnmanagedCallersOnly(CallConvMemberFunction)]` vtable slab, refcount, back-pointer; managed
exceptions swallowed at the boundary. Cold-COM per canon — but note the sink fires **every frame
of a live gesture**, so its thunks write only into preallocated producer state (alloc-zero under a
live gesture is gated, §13).

**Status → phase mapping** (`OnViewportStatusChanged` + `OnContentUpdated` transform diffs):

| Transition | Emit |
|---|---|
| READY/ENABLED → RUNNING | `ScrollBegin` (first diff) |
| RUNNING + content update | `ScrollUpdate` (δ = last − current, **sign verbatim — never re-invert**; the OS already applied natural-direction) |
| RUNNING → INERTIA | `MomentumBegin` |
| INERTIA + content update | `MomentumUpdate` (verbatim) |
| INERTIA → READY | `MomentumEnd`; reset viewport (re-center fake content, zero cached tx/ty) |
| RUNNING → READY (no inertia) | `ScrollEnd` (slow release, hard lift); reset viewport |
| **INERTIA → RUNNING** (fingers re-land mid-coast) | **`MomentumEnd` then `ScrollBegin`** — stop-on-contact (orchestrator review finding; Firefox transitions to eNone then re-pans) |

Transform → DIP: `dip = transformDelta / (dpi/96)` using the window's live DPI (re-read on
`WM_DPICHANGED`), then a single frozen calibration constant `DmDipPerTransformUnit` (default 1.0,
tuned once on real hardware during Phase 0 — Firefox's over-sensitivity bugs 1678771/1199737 are
the cautionary tale). **No knee, no gain curve, no inversion.**

**`DM_POINTERHITTEST` → `SetContact(pointerId)`** in the WndProc hands the PTP contact to DManip —
the browsers' engagement path, which the prior integration never used for touchpad (it injected
`WM_POINTERWHEEL` into `ProcessInput`, which DManip rejects with S_FALSE by design).

**Wedge detection + auto-fallback (behavior, not a flag; edge-triggered single ownership):**
- Init failure (any HRESULT < 0) → `_dmAvailable = false`; the always-compiled wheel classifier
  (§5) is the PTP path.
- Runtime wedge: after `SetContact`, status must leave READY within `DmEngageTimeoutMs = 120` with
  a live contact; a timeout or a `SUSPENDED` read ⇒ `_dmWedged = true` for the gesture: the DManip
  producer **emits `ScrollEnd`/`MomentumEnd` and stops emitting**, `ReleaseAllContacts` +
  `Abandon` + re-`Enable` clears the viewport, and the wheel classifier synthesizes phases only
  for packets **after** the flag flipped (never both owners for one packet — gated, §13).
- `DmWedgeCountToDisable = 3` wedges in a session ⇒ `_dmAvailable = false` for the session.

**Blur/teardown:** on `WindowBlur` during a live gesture → `ReleaseAllContacts`, emit the matching
End kind, reset viewport (the dispatcher's existing blur reset already clears pointer state; the
integrator zeroes via `CancelGesture`). On destroy → `Disable`, `RemoveEventHandler`,
`Deactivate(hwnd)`, release ComPtrs, free the CCW.

---

## §4 Root-cause of the prior failure + the gating probe

**Ranked causes of the 39f10bd failure** (PTP never engaging DM; wheel-injection S_FALSE;
disp=0/SUSPENDED wedges):
1. **`EnableMouseInPointer(true)` starves the DManip PTP channel** (prime suspect — browsers don't
   call it; promoted `WM_POINTERWHEEL` plausibly means the OS already "spent" the PTP gesture, so
   `DM_POINTERHITTEST` never fires; explains why only touchscreen contacts engaged).
2. **Wheel injection was never the path** — DManip drives from *contacts* (`SetContact`), not
   injected wheel messages; S_FALSE was the API working as designed.
3. **Missing per-message `ProcessInput` pumping** — a viewport starved of the general message
   stream can wedge SUSPENDED.
4. Viewport rect/update-clock misuse (lowest — the browsers prove the §3 shape).

**The probe: `dm-probe.exe`** — standalone ~250-line Win32 app (scratch, not shipped): one
`WS_OVERLAPPEDWINDOW`, DManip per §3, sink + WndProc logging every callback/status/message with
QPC stamps. Run the same physical two-finger pan+lift per cell:

| Cell | EnableMouseInPointer | Rect | ProcessInput | Expectation / what it decides |
|---|---|---|---|---|
| A | not called | 1000×1000 | every msg | baseline: browsers' world works on this hardware |
| B | **called(true)** | 1000×1000 | every msg | **the linchpin** — coexistence with our input model |
| C | not called | window-sized | every msg | rect fallback if A/B are rect-sensitive |
| D | called(true) | 1000×1000 | **none** (Update only) | expected FAIL → proves cause #3 |
| E | called(true) | 1000×1000 | every msg + inject WM_POINTERWHEEL | expected S_FALSE → confirms cause #2 was a dead path |

PASS = `DM_POINTERHITTEST` fires; `SetContact` S_OK; READY→RUNNING→INERTIA→READY; smooth sub-line
`OnContentUpdated` deltas during RUNNING + a decaying tail during INERTIA.

**Go/no-go:**
- **B passes** → keep `EnableMouseInPointer` AND ship DManip. Best case; §3 ships as-is.
- **B fails, A passes** → mouse-in-pointer starves DManip (cause #1 confirmed). Ship the §5
  fallback as primary **unless** it misses the feel bar on-device — only then take the
  mouse-in-pointer removal (a contained but real dual-WndProc refactor: legacy `WM_MOUSE*` +
  `TrackMouseEvent` hover + explicit `SetCapture` return for mouse, while touch/pen stay
  `WM_POINTER*`). Build-time decision; never a runtime toggle (the API is process-wide and
  irreversible).
- **A and B both fail** → NO-GO for DManip on this hardware; §5 fallback is the PTP path (it is
  always compiled, so this is safe); DManip remains the documented shape for the macOS-analog
  contract.

**The probe is gating: no physics lands until its verdict is recorded.** Hours of work; flips the
biggest fork before any comabi/gate investment.

---

## §5 The hardened wheel-fallback classifier (always compiled)

The mouse-wheel path (always) and the PTP path when DManip is unavailable/wedged/starved.
Replaces the Win32 soft-knee + `TickTouchpad` machinery entirely.

- **Classifier demoted, not deleted:** `notch % 120 != 0` (latched per gesture, 200 ms
  `WheelGestureGapMs` re-evaluation) splits the stream only when DManip does not own PTP:
  - **Detented mouse** (clean ±120·n with gaps) → existing `InputKind.Wheel`,
    `DeviceClassRaw=WheelDetented` → the liked `WheelFling` path, **unchanged** feel, plus:
    per-notch distance gains a `clamp(lines,1,∞)/3` multiplier from `SPI_GETWHEELSCROLLLINES`
    (read at init + `WM_SETTINGCHANGE`; default 3 ⇒ exactly today's behavior;
    `WHEEL_PAGESCROLL` ⇒ one viewport page). PTP DIP deltas never pass through this multiplier.
  - **Hi-res / PTP-fallback** (sub-120, high-rate) → synthesized phases,
    `DeviceClassRaw=WheelHiResFallback`.
- **Normalization replaces magic:** a signed per-axis raw accumulator (the SumatraPDF-#3032 fix);
  detented notches emit per ±120 crossed; hi-res converts via one frozen constant
  `HiResFallbackDipPerNotch` (`dip = rawΔ/120 · const`) — the soft-knee trio
  (`s_tpScale/s_tpKnee/s_tpMaxRaw`) dies.
- **Phase synthesis:** first hi-res packet after an idle gap → `ScrollBegin`; per-packet
  `ScrollUpdate` (ring-coalesced; velocity side-ring fed); no packet for `AssumeStoppedMs = 40` ×
  ~2 bursts (~80 ms, one constant `HiResLiftMs = 80` derived from the observed ~60 ms inter-burst
  cadence) → `ScrollEnd`.
- **The tail question resolves itself — no classifier (final-review simplification, supersedes the
  judged resume-window rule):** an OS/driver tail, when present, *keeps the packet stream flowing*
  — there is no silence for a lift detector to fire into, so "lift then tail resumes" cannot occur.
  Therefore the fallback applies **every** packet 1:1 under contact semantics (a tail scrolls
  exactly as far as the OS intended), and when true silence finally comes, the trailing-window
  IMPULSE velocity — evaluated at the **last packet's stamp** (§2) — is the tail detector for free:
  a completed OS tail decays to `v ≈ 0` (below the 50 px/s seed gate → **no self-coast, no double
  inertia**), while an abrupt no-tail lift leaves the true hand velocity → exactly **one**
  `SelfFling`. A mid-stream direction reversal naturally yields an opposite-sign (or near-zero)
  velocity, so no stale coast can survive a reversal. Correct in both worlds, zero heuristics, zero
  extra constants. (The recon contradiction — MS docs say no synthesized tail for plain HWNDs, our
  dev machine observed one — becomes moot: the design never needs to know which world it is in.)
- **Pinch synthesis swallowed (orchestrator review):** Ctrl-modified hi-res wheel packets are the
  OS's legacy pinch-zoom synthesis — **consume them, never scroll** (route to a future zoom
  channel; v1 drops them). Late momentum/tail packets ignore modifier changes (the Firefox
  Ctrl-during-momentum zoom bug class).
- Mouse-wheel detented behavior is otherwise untouched — same-direction accumulation, hard-clamp,
  `4500 px/s` cap, `WheelEaseTauMs=18` chase.

---

## §6 The unified integrator (FluentGpu.Engine, `ScrollAnimator`)

**One consumer.** `ScrollState.ScrollMode` becomes an explicit 8-state enum (byte-sized, values
renumbered — `Programmatic` moves 2→6; all references are compile-time consts):

| State | Drive | Physics |
|---|---|---|
| `Idle=0` | — | none |
| `ContactPan=1` | ScrollBegin/Update (touch, DManip PTP, fallback) | **1:1 raw** summed delta/frame; past-edge → band |
| `OsMomentum=2` | MomentumUpdate (DManip INERTIA / detected tail) | **verbatim**, no integrator |
| `SelfFling=3` | touch lift / fallback no-tail lift | closed-form `v·r^t`, `FlingDecayPerS` |
| `WheelFling=4` | mouse notch | closed-form, `WheelFlingDecayPerS` — unchanged |
| `TargetChase=5` | scrollbar track click, keyboard | `WheelEaseTauMs` ease — unchanged |
| `Programmatic=6` | bring-into-view | 95 ms crit-damped spring — unchanged |
| `OverscrollRelease=7` | band return | `StepSpring` ω=42 ζ=1 — unchanged shape |

**Transition rules (the feel-critical ones):**
- **`ContactBegin`:** hit-test **once**, latch `_gestureTarget` + `_gestureDevice`; **zero any
  in-flight momentum on that target** (velocity=0 — cancel means zero, never add); if
  `OverscrollRelease` is live, reconstruct band excess so the finger continues the stretch
  seamlessly. **Axis latch uses a ~4 DIP accumulation window** (orchestrator review): accumulate
  |dx|,|dy| from gesture start until their sum crosses `PanSlopPx=4`, then latch the dominant
  axis for the whole gesture — first-delta latching mislatches diagonal starts. The latch (not
  DManip rails, not the arena) rails the DManip path.
- **Whole-gesture latching:** contact frames AND the momentum that follows belong to the latched
  target even if the pointer drifts over a sibling (AppKit rule).
- **Any `PointerDown` (including a plain mouse click) over a coasting viewport zeroes its
  momentum** (orchestrator review — `ContactBegin` alone doesn't cover clicks; macOS convention,
  checklist #9). Keyboard scroll keys and an opposite-direction wheel likewise cancel-then-act.
- **Wheel during a contact/momentum stream:** `CancelGesture()` (zero + clear latch; the renamed,
  `_tp*`-free `CancelTouchpadForWheel`) then seed `WheelFling` — synchronous cross-device handoff.
- **Additivity:** same-direction wheel notches accumulate velocity (kept, capped 4500); a touch
  re-flick **retargets from residual** — `v0 = max(v_new, v_residual)`, never stacked (iOS/Android
  behavior; Firefox's multiplier defaults neutral).
- **Programmatic interrupt handoff carries velocity** (WinUI shared-Position parity): a
  bring-into-view during any fling seeds the spring with the current velocity; conversely
  `ContactBegin` during `Programmatic` zeroes it (the user always wins).
- **Extent asymmetry (WinUI parity):** `WheelFling`/`TargetChase`/`Programmatic` hard-stop at the
  clamp; only contact-gesture states (`ContactPan`, and `SelfFling`/`OsMomentum` descended from
  one) produce the band.
- **Cancel (`PointerCancel`, window blur, DManip wedge):** treated exactly like End with velocity
  0 + full latch/band reset — next gesture re-latches fresh.

---

## §7 Physics constants (one table; units; formula; provenance)

Feel constants (Engine, frozen — the `FG_WHEEL_DECAY` and `FG_OS_OMEGA/ZETA/MOMENTUM` env reads
are **retired** to plain consts; one shipping feel, no knobs):

| Constant | Value | Units | Feeds | Provenance |
|---|---|---|---|---|
| Contact apply | rule | — | `offset ← clamp(anchor − Σδ, 0, max)`; excess → band | 1:1 contract; ring = the per-frame resample |
| `FlingDecayPerS` | 0.135 | /s survival | `v(t)=v0·r^t` (k≈2.0/s) | iOS `.normal` (0.998/ms); A/B-validated (WinUI-0.05 rejected as "barely any momentum") |
| Fling seed gate | 50 | px/s | `|v|≥50` seeds SelfFling | **KEEP** (already shipping; = Android min-fling) |
| `FlingSettleVelPxPerS` | 13 | px/s | active fling terminates below | **KEEP** (settle ≠ seed; judge M1) |
| `FlingMaxVelocityPxPerS` | 8000 | px/s | seed clamp | NEW; Android max-fling; ~4000 px coast at k=2.0 |
| `WheelFlingDecayPerS` | 0.00002 | /s survival | per-notch coast ≈ notch distance | KEEP; now a plain const |
| Per-notch distance | `max(48, 0.10·vp)·lines/3` | DIP | wheel notch | **ours and liked** (WinUI's actual wheel distance is OS-internal — no parity claim; fix the stale "15%" comment in Pal.cs); `lines` from SPI, PAGESCROLL ⇒ 1 page |
| `WheelFlingMaxVelocityPxPerS` | 4500 | px/s | wheel accumulation cap | KEEP |
| `WheelEaseTauMs` | 18 | ms | TargetChase ease | KEEP |
| `ProgrammaticSpringHalflifeMs` | 95 | ms | crit-damped spring | KEEP |
| Band curve `RubberC` | 0.55 | — | `f(x)=x·d·c/(d+c·x)`, `d=viewport` | iOS rational; **one band for touch AND touchpad** (replaces the velocity-enveloped `_tpVel` band and `BandFromExcess`'s soft-knee; `ExcessFromBand` inverse updated in lockstep) |
| Band cap | 0.10·vp | DIP | saturation asymptote | KEEP |
| `SpringOmegaRadPerS` | 42 (ζ=1) | rad/s | band return `StepSpring` | KEEP (already tuned, dt-deterministic; Firefox's ζ=1.1/k=200 rejected — no benefit, re-gating cost) |
| `AssumeStoppedMs` | 40 | ms | stopped-before-lift check inside IMPULSE (window ends at the last input stamp, §2) | Android `ASSUME_POINTER_STOPPED_TIME` |
| `VelHorizonMs` | ~66 | ms | IMPULSE window | ≈4 frames; Android ~100 shortened |
| `dtClampMs` | 34 | ms | frame dt clamp | KEEP (closed-form coast under-travels during a hitch — the safe direction) |

Behavior thresholds (Windows producer, frozen; not feel knobs):
`DmEngageTimeoutMs=120` (wedge watchdog) · `DmWedgeCountToDisable=3` (session de-rate) ·
`DmDipPerTransformUnit=1.0` (post-DPI calibration, tuned in Phase 0) ·
`HiResFallbackDipPerNotch` (replaces `s_tpScale·120`, tuned in Phase 0) · `HiResLiftMs=80`
(fallback lift; ≈1.3× the ~60 ms burst cadence) · `WheelGestureGapMs=200` (detented gesture
identity — distinct roles: 200 = "same wheel spin?", 40 = "did the finger stop?").

---

## §8 Scene application

- **Single chokepoint retained:** every state funnels through `SetScrollOffset` (absolute) or
  `ApplyTouchPan` (band-splitting 1:1) into `ApplyScrollPosition` — transform write +
  `TransformDirty|PaintDirty` + `ScrollBindEval.ApplyContinuous` + virtual-window re-realize.
  **ScrollBind is untouched** — binds read `ScrollState` post-physics at the chokepoint and never
  re-integrate (generic-hookable-scroll-engine-design §6.7 holds).
- **ScrollState columns:** `ScrollMode` re-documented as the §6 enum (no struct growth); no fields
  added or removed (`FlingVelocity`, `OverscrollPx/Vel`, `Overscrolling` reused). The old `_tp*`
  state lived in `InputDispatcher`, not columns.
- **`UserScrollActive` / `AnyUserScrollActiveThisFrame` semantics preserved EXACTLY** (the lyrics
  DoF recorder-defer gate depends on them; this rework builds on the uncommitted +7/+1 diff, does
  not revert it). Rule: `movingNow && ScrollMode != Programmatic`, where `movingNow` is true for
  `ContactPan`, **`OsMomentum`** (a PTP coast IS user scroll motion — if this is wired as
  "fling-only" the lyrics blur drops during PTP coasts), `SelfFling`, `WheelFling`,
  `OverscrollRelease`; false for `Programmatic`/`Idle`.
- **Subpixel policy (H5):** round the **composed translate** to physical pixels at
  `WriteContentTransform` — `tx = Round((offset+band)·s)/s`, `s` = the content node's effective
  DPI scale (live; re-read on `WM_DPICHANGED`) — keeping all logical state (offset, band, clamps,
  virtualization, ScrollBind) continuous float. **Every same-axis transform written at the
  chokepoint rounds against the same device-pixel origin — including ScrollBind pins** (sticky
  headers), or a 1 px seam shimmers between pinned and scrolled content; gate §13.7 asserts the
  pin case. Rejected: offset-stable rasterization — a text-stack-sized change for the same visual
  result.

---

## §9 Reduced-motion — a value, never a branch

A `ReducedMotion` scalar (existing engine convention) seeds the tuning fields once (the
`_flingDecayPerS`-style seed path); zero hot-path branches:

- Contact 1:1 tracking: **unaffected** (it was never an animation).
- Wheel: `WheelEaseTauMs → ~1 ms` and the notch skips `WheelFling` (teleports its distance).
- SelfFling: seed `v0 × (1 − RM)` — at RM=1 the lift lands where it lifted.
- OsMomentum: applied delta `× (1 − RM)` per frame — at RM=1 momentum deltas contribute 0 (no
  coast, no snap, no branch; the judge-corrected value-lerp, not a truncation branch).
- Band: presence unaffected (direct manipulation, not motion); return ω lerped faster.

---

## §10 Threading + latency

Everything stays UI-thread: pump, DManip `ProcessInput`/`Update`/sink, phase enqueue, dispatch,
physics tick, chokepoint write. The render-thread seam is untouched. One `[Conditional]`
`ScrollLatencyProbe` stamps QPC at (a) packet enqueue, (b) DrawList record, (c) render-thread
`PresentAck` — logging input→record and input→photon. Honest accounting: async present
(`FG_RENDER_ASYNC`, currently default-OFF) adds ≥1 present frame to (c); measured here, not fixed.

---

## §11 Interaction edge-case contract (the sweep — one decision each)

- **Nested-scroll chaining:** v1 = **none**. The gesture is latched to one target at
  `ContactBegin`; a child at extent bands (contact) or stops (wheel) — residual is never handed to
  the parent. (WinUI `ChainingMode` parity is future work; the latch makes chaining a deliberate
  feature, not an accident.)
- **Scrollbar:** thumb-drag = absolute `SetScrollOffset` writes (existing); wheel over the
  scrollbar = the same scroller's wheel path; scrollbar press-hold never enters `ContactPan`.
- **SwipeControl / gesture arena:** touch keeps today's arena competition (`ScrollBegin` fires
  only when the Pan claims at 4 px slop). **DManip PTP bypasses the arena** — the OS owns the
  contact before the arena sees it; swipe-to-reveal remains a touch gesture. Stated, accepted.
- **Popups (`Win32PopupWindow`):** no DManip viewport in v1 — popups fall to the wheel classifier
  (always compiled). A flyout list scrolls fine; it just doesn't get DManip-grade PTP.
- **DPI change mid-gesture:** producer re-reads scale on `WM_DPICHANGED`; a one-frame
  discontinuity mid-fling is accepted (rare).
- **Window blur / `PointerCancel`:** cancel == end with v=0 + full reset; DManip additionally
  `ReleaseAllContacts` + viewport reset (§3).
- **"Scroll inactive windows":** preserved — wheel routes by hover hit-test, nothing requires
  focus; DManip only owns the contacts it was handed. **Observed (Phase 0):** a hovered-but-
  unfocused window receives PTP scrolling as *quantized exact-±120* `WM_POINTERWHEEL` bursts
  (`PT_TOUCHPAD`, 12–30 ms apart) instead of DManip engagement — the fallback classifier must not
  misread such a burst as detented-mouse notch spam (rate-aware: ±120 packets arriving < ~50 ms
  apart from a `PT_TOUCHPAD` source ride the hi-res/contact path, not per-notch WheelFling).
- **Natural-direction inversion:** applied **once**, by the OS/driver — DManip diffs and PTP
  fallback deltas are consumed sign-verbatim; re-inverting is a bug.
- **Pinch:** never scrolls — DManip path discards scale output; fallback swallows Ctrl+hi-res
  wheel synthesis (§5).
- **Fingers resting without motion (fallback limitation):** promoted wheel emits nothing while
  fingers rest, so the fallback cannot stop a `SelfFling` by touch-and-hold (DManip can — RUNNING
  re-enters). Accepted fallback-fidelity gap; documented, not worked around.

---

## §12 Deletion list (single-owner discipline; nothing left armed)

| Dies | Replaced by |
|---|---|
| `InputDispatcher.TickTouchpad` + the `AppHost` phase-7 call | `ScrollAnimator` ContactPan/OsMomentum/SelfFling |
| `PanTouchpad` integrator body + `_tpTarget/_tpHoriz/_tpAppliedRaw/_tpDemandRaw/_tpPendingDelta/_tpGotPacket/_tpQuietMs/_tpVel/_tpBandExcess` | phase events + `_gestureTarget/_gestureDevice` latch + velocity side ring |
| `s_tpSmoothTau` (14 ms low-pass) | nothing — contact is 1:1 (H1 root cause) |
| `s_tpSettleQuietMs` (120 ms quiet-latch) | real End phases; fallback `AssumeStoppedMs`/`HiResLiftMs` (H4) |
| `s_tpVelTauMs/s_tpVelDecayTauMs/s_tpBandEaseTauMs/s_tpBandFullVelPxPerS/s_tpBandHeadroom/ExcessSnapDip` | unified rational-0.55 band + ω=42 return (H10) |
| `ShapeTouchpadPacketDelta` (2nd gain curve) | nothing — 1:1 (H2) |
| Win32 soft-knee trio `s_tpScale/s_tpKnee/s_tpMaxRaw` | DManip DIP diff / `HiResFallbackDipPerNotch` |
| `CancelTouchpadForWheel` | `CancelGesture()` |
| `TouchpadActive` | `GestureActive` |
| `TouchVelocity` 50 ms LSQ | Engine IMPULSE estimator over the QPC side ring |
| `FG_WHEEL_DECAY`, `FG_OS_OMEGA/ZETA/MOMENTUM` env reads | plain consts (no knobs) |
| Stale `Pal.cs` "15%·viewport" wheel comment | corrected to the real formula (honesty fix) |
| Second-integrator touchpad gates (`progressive-packet-curve`, `settle-determinism`, `inter-burst-no-restart`, `edge-tail-no-plateau`) | §13 gates 2/5/6/11 |

---

## §13 Gates (VerticalSlice; all driven by `HeadlessScrollProducer` scripts)

The producer must script, first-class: clean phase sequences, an OS-tail burst with decaying
magnitude after `ScrollEnd`, a mid-gesture "DManip goes silent → fallback resumes" handoff, a
`Cancel` mid-momentum, and per-packet synthetic QPC stamps.

1. `gate.scroll.contact-1to1-tracking-lag` — constant-velocity ContactUpdate stream ⇒
   `|applied − Σδ| ≤ 0.5 DIP` every frame (H1 guard; did not exist).
2. `gate.scroll.dt-invariance-sweep` — same scripted stream at dt ∈ {8,16,33} ms ⇒ same
   trajectory within 1 DIP (H7).
3. `gate.scroll.first-delta-bound` — a 1 DIP first packet moves ≤ a few px; a first notch never
   teleports past its distance.
4. `gate.scroll.momentum-cancel-zeroes` — ContactBegin **and a plain PointerDown click** during
   each momentum state ⇒ velocity == 0 same frame, no residual drift.
5. `gate.scroll.no-double-inertia-os-tail` — a scripted stream ending in a decaying tail then
   silence ⇒ travel == Σ(all packets) exactly (trailing IMPULSE velocity < seed gate ⇒ no coast);
   the same stream cut off abruptly at speed ⇒ Σ(packets) + exactly one closed-form coast; a
   mid-stream reversal ⇒ no coast in the stale direction (supersedes
   `gate.touchpad.os-tail-no-double-inertia`).
6. `gate.scroll.phase-contract-conformance` — latch holds while pointer drifts over a sibling;
   Cancel mid-gesture resets cleanly; INERTIA→RUNNING ⇒ MomentumEnd+ScrollBegin ordering.
7. `gate.scroll.subpixel-stability` — slow sub-pixel pan ⇒ composed device-px translate is
   monotonic whole-px steps while logical float stays continuous; **including a sticky-header
   ScrollBind pin sharing the same origin** (no 1 px seam).
8. `gate.scroll.wheel-regression` — existing wheel suite green + `lines/3` multiplier + PAGESCROLL
   page + Ctrl+hi-res swallowed (pinch never scrolls).
9. `gate.scroll.alloc-zero` — 0 managed bytes across a full contact+momentum+fling+band cycle,
   **including a live DManip-style sink cadence** (the sink fires every gesture frame — hot in
   practice).
10. `gate.scroll.impulse-velocity` — scripted QPC samples ⇒ `sign(W)·√(2|W|)` within ε; gap ≥40 ms
    ⇒ 0; ≥2-sample minimum.
11. `gate.scroll.overscroll-unified` — touch and scripted touchpad-momentum past the same edge ⇒
    identical rational-0.55 band + identical ω=42 return (H10).
12. `gate.scroll.wedge-handoff-no-double-count` — DManip goes silent mid-gesture ⇒ travel ==
    Σ(DManip deltas before wedge) + Σ(fallback deltas after), zero overlap.
13. `gate.scroll.reduced-motion-values` — RM=1 ⇒ wheel teleports, no self-fling, OsMomentum deltas
    ×0, contact 1:1 intact.
14. `gate.touch.flick-decay-settle` (existing) re-asserted with the 8000 px/s max seed still
    settling inside its frame bound.

**On-device recipe (per landing phase):** `FG_SCROLL_LOG` extended to log
`phase | qpcMs | dipDelta | device` per packet (verify a real PTP flick produces
Begin→Update×N→[End | MomentumBegin→Update×N→MomentumEnd], and no self-fling after a momentum
stream); the synchronous tracking-lag overlay (finger-implied Σδ vs applied offset must overlap
during ContactPan — same synchronous-probe pattern as `WAVEE_LYRICS_ADVANCE_PROBE`, avoiding
Timer-vs-RunFrame artifacts); input→photon via the `ScrollLatencyProbe` + the existing
onscreen-capture script (CaptureBgra is blind to the DWM composite).

---

## §14 Landing plan (each phase independently green: clean build + VerticalSlice + on-device)

- **Phase 0 — `dm-probe.exe` (GATING).** Run the §4 matrix on the dev touchpad; record the verdict
  + measured transform-delta scale (calibrates `DmDipPerTransformUnit`) + real packet cadence
  (validates `HiResLiftMs`). No engine change.
- **Phase 1 — Contract + IMPULSE, touch only.** `InputKind` 12–17 + `InputEvent` fields + side
  ring + `HeadlessScrollProducer`; swap touch release velocity LSQ→IMPULSE. Wheel + touchpad
  untouched (old path still armed). Green: gates 10 + existing touch suite; on-device touch feels
  unchanged-or-better. Small, reversible.
- **Phase 2 — The big cut.** Route PTP through the phase producers (DManip primary per Phase 0
  verdict; fallback classifier hardened per §5) into `ScrollAnimator`
  ContactPan/OsMomentum/SelfFling; delete `TickTouchpad` + `_tp*` + both gain curves + the
  quiet-latch. Green: gates 1,2,4,5,6,9,12 + wheel regression; on-device PTP tracks 1:1
  (overlay), momentum matches native apps, pause-resume has no restart hitch.
- **Phase 3 — Unify band + subpixel + wheel-lines + pinch swallow.** Rational-0.55 band both
  devices; chokepoint device-px rounding (incl. pins); SPI `lines/3` + PAGESCROLL; Ctrl+hi-res
  swallow. Green: gates 3,7,8,11; on-device slow-pan shimmer gone.
- **Phase 4 — Reduced-motion values + knob retirement + doc reconciliation.** RM seeds; delete
  `FG_WHEEL_DECAY`/`FG_OS_*`; fix the stale Pal.cs comment; reconcile owning `design/` docs +
  `check-canon.ps1`; update `generic-hookable-scroll-engine-design.md` cross-refs. Green: gate 13 +
  full suite + canon gate.

---

## §15 Open risks (honest)

- ~~Probe cell B is the linchpin~~ — **RESOLVED 2026-07-01: cell B passed** (see Status header);
  mouse-in-pointer stays, DManip ships as primary. The mouse-in-pointer-removal contingency is
  dead. Residual: reproduce the pass once on an x64 test machine when convenient (dev machine is
  ARM64).
- **`DmDipPerTransformUnit` and `HiResFallbackDipPerNotch` are frozen one-machine calibrations** —
  the same critique that sank the old soft-knee. Mitigation: Phase 0 measures real deltas before
  freezing; both are pure linear scales (no shape), so cross-device error is proportional, not
  curve-warping.
- **Truncated OS tails** (a driver that cuts its tail at non-zero velocity) seed a residual
  self-coast on the fallback path — indistinguishable from a genuine lift-at-speed and the right
  guess either way; never *double* momentum. The DManip path has no such ambiguity.
- **Fallback cannot stop-on-touch** during SelfFling (no packets while fingers rest) — accepted
  fidelity gap vs DManip (§11).
- **comabi slot indices are placeholders** until AbiHarvest reconciles them from the winmd chain.
- **ScrollBind pin rounding** shares an origin with content by spec, but any *future* bind that
  writes a same-axis transform outside the chokepoint would bypass the rounding rule — the
  chokepoint remains the single legal writer (enforced by review + gate 7).
