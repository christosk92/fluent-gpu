# FluentGpu — Subsystem Design: Scrolling (motion, posing, virtualization, scroll-linked effects, diagnostics)

> **OWNER, AS-BUILT 2026-09-24.** This is the authoritative design of the engine's scroll system as it exists in
> `src/`. It replaces the pre-rework scroll prose in `input-a11y.md` §7B (the phase-7 integrator, the
> `SetScrollOffset`/`ApplyScrollPosition` chokepoint, `ScrollIntegrator`), `layout.md` §6 (`ScrollIntoView`) and the
> `ScrollBind` / named-scroll-timeline design in `docs/plans/generic-hookable-scroll-engine-design.md`: those describe
> deleted code. The plans that produced this system are history, not canon —
> `docs/plans/scroll-rework-implementation.md` (the rework), `scroll-rework-app-migration.md` (the app surface),
> `scroll-gpu-retained-tiles-implementation.md` (tiles + §F effects) and `scroll-lab-implementation.md` (the lab).
> Where a plan and this doc disagree, this doc describes the code.

**Where it lives.** Portable core: `src/FluentGpu.Engine/Scroll/{Motion,Runtime,Effects,Extent,Diag}/**`. Host wiring:
`Hosting/AppHost.Scroll.cs`, `Hosting/SnapshotScrollPoseSink.cs`, `Hosting/Threading/*`. Input:
`Input/InputDispatcher.Scroll.cs`. Windows producers: `src/FluentGpu.Windows/Pal/{Win32Platform,Win32DirectManipulation,
CompositorTickFilter,Win32CompositorClock}.cs`. Rendering of what scroll poses is owned by
[`gpu-renderer.md` §13.1](./gpu-renderer.md) (retained tiles) and [`scene-memory.md` §4.3b](./scene-memory.md) (the
slice partition) — this doc links there and never restates a tile contract.

**Status.** Everything here is as-built. The touchpad-timing work — the composition-timed DirectManipulation stream
(`ContactClock.Present`), bounded resampling of device-timed contacts, no per-frame zero-delta samples, and the atomic
plan-shift + coverage frame (`ScrollCoverageRow.FrameShift`) — landed on 2026-09-24 while this doc was being written
(§3.1, §4.4, §5.2); it was verified against the tree that day. The 2026-09-29 scroll-jitter pair
(`docs/plans/scroll-jitter-implementation.md`) followed: a composition-timed sample is stamped with the present of the
first render turn that sees it (`ContactStamp`, §4.4), and a clock-paced render turn no longer queues behind a present
that missed its vblank (`SlotCatchUp`, §8). If later edits move it, the code wins and this doc is corrected.

---

## 0. What this doc owns

| Category | Authoritative here |
|---|---|
| Motion model | `SegKind`, `MotionSeg`, `ScrollPlan`, `ContactRing`, `OverpanPolicy`, `MotionKind`, `PlanAuthor` (every authoring rule), `SnapGrid`/`SnapTargets`, `WheelAccelState`, `KeyMove` |
| Feel | `MotionFeel`, `FeelProfiles`, `ScrollTunables`, `TunableF` |
| Input | `ScrollInputEvent`, `ScrollSource`, `ScrollGesture`, `WheelClassifier`, `WheelDeviceEvidence`, `ScrollRouter` (latch + chain), the DirectManipulation contact-producer contract (`ContactStamp`) |
| Runtime | `PlanSlots` (the seam), `ScrollViewportId`, `ScrollPoser`, `IScrollPoseSink`, `ScrollPoseFeedback`, `ScrollCoverageTable`/`ScrollCoverageRow`/`ScrollEffectRow`, `ScrollHandle` + `ScrollMove` + `ScrollMotionState` + `RestoreLatch`, `ScrollPositionMemory`, `ScrollObservation`, `ScrollCtx`, `SceneScrollExtensions`, `ScrollContentPose`, `Virtualizer`/`RealizeWindow` |
| Extent | `IExtentSource`, `FixedExtent`, `MeasuredExtent`, `VirtualLayoutExtent`, the one extent-write path |
| Effects | `EffectChannel`, `EffectKind`, `CollapseAnchor`, `ScrollEffect`, `ScrollEffectSpec`, `ScrollEffectDsl`, `ScrollEffectEval` (incl. THE snap function), `EffectTransform`, `EffectGeometry` |
| Pacing (scroll's use of it) | the present-time law for scroll, `PresentCadence` as it applies to motion, `MotionTickRun`, the present-slot catch-up (`SlotCatchUp`), the governor exemption for scroll, the pose floor |
| Diagnostics | `ScrollProbe` (levels, rings, row kinds, `Turn`/`Present` rows), `BurstSummary`/`ScrollVerdict`, `ScrollProbeCsv`, `Scroll/Diag/Analysis` (`SessionSeries`, `ScrollMetrics`), the Scroll Lab (`src/FluentGpu.ScrollLab*`) |

**Not owned here (link, never restate):** slices/tiles/`InvalidationReason`/`TileCensus`/composite pass/present rule →
`gpu-renderer.md` §13.1; the slice partition and `CompositeSliceCmd` marker → `scene-memory.md` §4.3b /
`gpu-renderer.md` §3.1; the render-thread seam, publication, snapshot slots → `threading-render-seam.md` §0;
`IRenderDisplayClock` → `pal-rhi.md` §1.1.1; `InputEvent`/`InputEventRing` and the paced-input wait →
`input-a11y.md` §3; the gesture arena that claims a touch pan → `input-a11y.md` §7A; `InputHooks.ZoomWheel` →
`input-a11y.md` §7B; `IScrollController` → `controls.md` §13; the `VirtualListEl` element and `IVirtualLayout` family →
`virtualization.md` + `controls.md`; `DrawList`/`NodePaint` columns (`PresentedH`, `ChildShiftY`, `ClipRect`) →
`scene-memory.md`.

Developer guide: [`docs/guide/scroll-lab.md`](../../guide/scroll-lab.md). Agent skill:
`.claude/skills/fluentgpu-scroll/`.

---

## 1. Principles (the rules everything else follows)

1. **Motion is an immutable analytic plan `p(t)`, evaluated at present time.** Every input authors a brand-new
   `ScrollPlan` — up to four closed-form `MotionSeg` arcs (or, for a live drag, a contact ring). `ScrollPlan.Eval(t)` is a
   pure function of one absolute time. There is **no `dt` anywhere in `Scroll/Motion`**: a skipped, repeated or late
   tick cannot corrupt a position, because nothing is integrated.
2. **The render thread poses.** `ScrollPoser.Tick` on the render thread evaluates every covered viewport's plan at the
   **predicted present time** of the compositor tick it is presenting for and writes the content translate itself. The
   UI thread poses the same plans with the same arithmetic (hit-testing, the published frame, headless) but never owns
   the pixel a user sees during motion.
3. **Coverage clamp ⇒ never blank.** The poser clamps the evaluated position to what the UI thread actually realized
   (`ScrollCoverageRow.Start/End`). A plan that outruns realization is recorded as a *clamp* (a probe row, a tile
   census count), never shown as an empty band.
4. **Exact extent + same-frame anchoring ⇒ never jumps.** One extent source per viewport; a measured correction above
   the anchor row shifts the plan's coordinate frame (`ScrollPlan.Shifted` via `PlanSlots.Shift`) in the same call that
   records it. Re-plans start from the position already shown (the **pose floor**), so a new input never catches up or
   steps back past frames the user has seen.
5. **Doubles in content space, small floats on screen.** Plans, extents and offsets are `double`. The only narrowing
   cast is `(float)(WindowOrigin − shown)` — a translate relative to the realized window's arrange origin — so a 100k-row
   list 4M DIP deep poses exactly.
6. **No throttles, ramps or holds.** Cost is fixed at its source (cheap rows, retained tiles, off-frame uploads), never
   by capping how many rows realize, pausing a blur while scrolling, or delaying input. The one cost knob is a
   velocity-sized overscan in DIP.
7. **Diagnostics always compiled, runtime-switched, no env vars.** `ScrollProbe.Level`, `ScrollTunables`,
   `AppHost.GpuPassTimingEnabled` are runtime data in every build including shipping Release; command-line `--fg`
   switches (`Hosting/EngineSwitches.cs`) cover launch-time toggles. The engine reads no environment variable.

---

## 2. Architecture

```
 UI thread                                                              Render thread (one turn per compositor tick)
 ─────────                                                              ─────────────────────────────────────────────
 Win32 PAL: WM_POINTERWHEEL ─► WheelClassifier ─┐                        tick (CompositorTickFilter lattice)
            DirectManipulation contact producer ┤                          presentSec = tick + (1 + depth)·refresh
            touch/pen pan (gesture arena claim) ┤                          (AppHost.RenderPresentSec)
            keys / scrollbar thumb / app calls ─┘                                  │
          │ ScrollInputEvent (wheel = URGENT sink, synchronous)                    ▼
          ▼                                                              PlanSlots.TryRead(vp)  ── seqlock copy
 InputDispatcher.DispatchScroll                                                   │
   element OnPointerWheel ▸ ZoomWheel ▸ WheelTarget                               ▼
   ScrollRouter.Decide  (latch / chain)                                  p, v = plan.Eval(presentSec)
          │                                                                       │
          ▼                                                              shown = clamp(p, coverage)   (clamp ⇒ probe row)
 ScrollHandle.{Wheel, ContactBeginHere/Delta/End, ThumbTo, Key,                   │
               ScrollTo/ScrollBy/BringIntoView, AutoScroll, Stop}                 ▼
   PlanAuthor.*  (anchored at max(t, pose floor))                        trans = SnapToDevicePixel(WindowOrigin − shown)
          │                                                                       │
          ▼                                                              effects evaluated at pSnapped = WindowOrigin − trans
 PlanSlots.Write(vp, plan) ── lock-free seqlock ─────────────────────►   IScrollPoseSink (SnapshotScrollPoseSink)
   OnWritten → wake render thread + UI frame                               PoseViewport / PoseContent / PoseEffect / PoseTransform
                                                                                  │
 ── UI frame ──                                                                   ▼
 RunScrollFrame(presentSec)   (AppHost.Scroll.cs)                        slices posed as composite parameters
   SetExtent, SettleIfDue, Eval, ApplyFeedback/ApplyShown                  (gpu-renderer.md §13.1: composite-only turn,
   Virtualizer.Plan → NeedsRealize → VirtualRangeDirty                      tiles rastered only where invalid)
 reconcile (realize the window) ▸ layout                                          │
   FlexLayout.ArrangeVirtual: measure → Virtualizer.ApplyMeasured                 ▼
     (correction above anchor ⇒ PlanSlots.Shift same call)               ScrollPoseFeedback ──(seqlock)──► UI:
   WindowOrigin / CoverStart / CoverEnd                                      ScrollHandle.ApplyFeedback → Offset,
 PoseScrollUi: FillScrollCoverage + UI poser (hit-test, published frame)     Motion, AtStart, AtEnd signals
   sticky engaged edges written here (UI thread, pre-publish)
          │
          ▼
 publish (SceneRecordingSnapshot carries ScrollCoverage) ────────────►   adopt coverage on a fresh publication
```

Three things cross the seam and nothing else: **`PlanSlots`** (UI → render, every write, lock-free), the **coverage
table** (UI → render, inside the publication), and **`ScrollPoseFeedback`** (render → UI, per viewport, seqlock).

---

## 3. Motion — `Scroll/Motion/`

### 3.1 Segments and plans

`MotionSeg` (`MotionSeg.cs`) is an immutable POD arc valid on `[T0, T1)` with `Eval(t, out v)` a closed form of absolute
time (a `t < T0` evaluates at `T0`, never backwards):

| `SegKind` | Closed form | Used by |
|---|---|---|
| `Hold` | `p = P0`, `v = 0` | rest, thumb, immediate jump, edge after a coast (`OverpanPolicy.None`) |
| `Cubic` | `p = P0 + R(1.5u − 0.5u³)`, `R = P1−P0`, `u = clamp((t−T0)/D, 0, 1)`, `D = T1−T0` — the front-loaded kick (velocity `1.5R/D` at `u = 0`, exactly 0 at `u = 1`) — plus a C1 blend-in `+ (V0·D − 1.5R)·u(1 − u/ε)³` for `u < ε = K/D` that starts the arc at the carried velocity `V0` and rejoins the kick with matching position, velocity and acceleration (`K = 0` ⇒ the plain cubic) | wheel notch (`PlanAuthor.WheelSeg`) |
| `Decay` | `v = V0·e^(−K·dt)`, `p = P0 + V0/K·(1 − e^(−K·dt))` — asymptote `P0 + V0/K` | fling coast; `Constant` (K → 0) |
| `Spring` | analytic damped spring from `(P0, V0)` to `P1`, ω = `K`, ζ = `Zeta` (under/critical/over branches) | rubber-band release to an edge |
| `Glide` | the `Spring` closed form with ζ pinned to 1 (a distinct tag for classification only) | programmatic moves, keys, snap re-targets |

`ScrollPlan` (`ScrollPlan.cs`) is a `readonly record struct`: `S0..S3` + `Count`, the viewport key `(Vp, Gen)`, a
monotonic `Seq`, the extent `Min`/`Max`, `ViewportExtent`, `RubberC`, the contact's `ContactClock Clock`, `OverpanPolicy`, a
`MotionKind` (`Idle`/`Wheel`/`Drag`/`Fling`/`Programmatic`/`Thumb` — classification only) and a `ContactRing`.
`Eval` picks the segment whose validity covers `t` (or the ring when `Count == 0`), applies the rubber band under
`OverpanPolicy.RubberBand` (`c·excess·vp/(vp + c·excess)`, capped toward `OverpanCapFraction` = 10 % of the viewport),
and reports `settled` only for a `Hold` tail or a finite segment past its end — open-ended `Decay`/`Spring`/`Glide` never
self-settle; `ScrollHandle.SettleIfDue` replaces them with an Idle hold once within `SettleEpsilonDip`/`SettleVelocity`.
`Dest` is the final destination (a `Decay`'s asymptote; the ring's newest position for a live drag).

`ScrollPlan.Shifted(delta)` moves every **position** (segment endpoints, ring samples, `Max`) by `delta` and no time,
velocity or constant: `Shifted(d).Eval(t) == Eval(t) + d` for every `t`. It is a coordinate-frame correction, not motion.

`ContactRing` is an 8-sample POD ring of raw `(t, pos)` for a live drag. Its velocity `V` is **one least-squares slope**
over the samples within `MotionFeel.FlingImpulseWindowS` of the newest, computed once per appended sample
(`WithSample`), and **0 when that horizon spans less than `VelocityMinSpanS`** (hi-res packets arrive in near-coincident
pairs whose pairwise slope is arrival jitter, not finger speed). `V` is the release velocity, the reported contact speed
and the overscan input — it **never drives the shown position** (a velocity estimate that changes between frames would
move the pose by `ΔV·lead`, against the finger whenever it drops). `ContactRing.Eval(t, clock)` interpolates between
samples; past the newest sample it depends on the contact's **`ContactClock`**, fixed at the contact's begin:

- **`ContactClock.Present`** (a composition-timed stream — DirectManipulation): each sample was already composed by the
  OS, and is stamped with the present time of the first render turn that sees it (`ContactStamp.ForFrame` — the next
  tick's present, §4.4), so a tick shows the previous tick's sample and the ring interpolates between REAL samples; past
  the newest it **holds** (only when the UI stalls more than a refresh) — predicting again would double the lead.
- **`ContactClock.Device`** (touch, pen, the touchpad wheel fallback): Android's **bounded resampling** — extrapolate along
  the newest two samples' slope by at most `min(ResampleMaxPredictionS = 8 ms, gap/2)`, and only when that gap is within
  `[ResampleMinDeltaS = 2 ms, ResampleMaxDeltaS = 20 ms]`; otherwise hold. Bounding the prediction to half the sample gap
  bounds how far a fresh sample can pull the shown position back.

`ReportPeriodS` (the mean of the newest ≤ 4 sample intervals) is the stream's own cadence. `TimeShifted`/`PositionShifted`
are the clock-resync and frame-shift corrections.

### 3.2 `PlanAuthor` — the only place plans are built

`PlanAuthor` (`PlanAuthor.cs`) is stateless (aside from the caller-owned `WheelAccelState`): every function takes the
previous plan and an absolute time and returns a new plan with `Seq + 1`. Nothing else constructs a moving plan.

| Author | Rule |
|---|---|
| `WheelNotch(prev, tNotch, notches, feel, ref accel, shownFloorSec)` | One `Cubic` (`WheelSeg`) anchored at `t0 = max(tNotch, shownFloorSec)`, starting at `prev.Eval(t0)`'s position AND velocity (never a jump, never a velocity step). Destination: a LIVE same-direction wheel glide ACCUMULATES (`prev.Dest + notches·WheelNotchDip·accel`); a first notch, a post-settle notch or a reversal re-bases on the displayed position. Clamped to `[Min, Max]`; never overpans. `notches` is fractional for a hi-res wheel (`raw/120`); the spin rate is judged per WHOLE notch (a packet of `|n| < 1` divides its gap by `|n|`). |
| `WheelSeg(prev, t0, dest, feel)` | The one wheel-segment shaper (a notch and a snap re-target of its destination): velocity carried in the notch's direction (0 from rest or against it — a reversal turns at once) blends onto the kick `1.5R/WheelDurationS` over `WheelRiseS`; when the carried velocity already exceeds the kick the same cubic runs over the shorter `1.5R/v` (starts at exactly `v`, arrives sooner, never overshoots). Every same-direction re-plan is therefore C1. |
| `AccelFor(gapS, emaS, feel, out nextEma)` | Spin rate → travel multiplier: an EMA of same-direction notch gaps (`AccelEmaWeight`) mapped to `min(AccelMax, max(AccelRefGapS/ema, min(1, ema/AccelUnityGapS)))` — the full notch at a slow roll (`ema ≥ AccelUnityGapS`), constant speed `WheelNotchDip/AccelUnityGapS` in the middle band, accelerating again for a fast spin; the speed never drops as the spin gets faster. A gap ≥ `AccelResetGapS` resets to 1×. Pure function of device stamps — never a frame clock. |
| `FollowBegin(prev, t, pos, clock, feel)` / `FollowSample` | A drag is a `Count == 0` plan under `OverpanPolicy.RubberBand` carrying the ring and its `ContactClock`; each sample appends and re-estimates `V`. |
| `FollowEnd(prev, tLift, tNow, feel, release)` | Release velocity = the ring's LSQ `V`; **0 when the finger had already stopped**. Who decides "stopped" is the `ContactRelease` the producer put on the End: `Moving` (DM RUNNING→INERTIA) releases `V`, `Stopped` (DM RUNNING→READY) holds, and only `Unknown` (every stream without a verdict) falls back to the time rule — the lift arrives more than `StoppedAfterS` after the newest sample = `clamp(2·ReportPeriodS, ContactStoppedMinS = 20 ms, max(FlingImpulseWindowS, 20 ms))` — two of the stream's own report periods (Android's `ASSUME_POINTER_STOPPED_TIME`, scaled to the cadence), so a pause-then-lift never flings. A Present-clock contact with a verdict is released with `tLift` = its newest REAL sample and `tNow` = the End stamp (`ScrollHandle.ContactEnd`, §4.4). A lift detected late (`tNow > tLift`) is authored from what the drag SHOWS at `tNow` with the velocity decayed to `tNow` — no forward jump. Past an edge → `Spring` back to it through the same band. Below `FlingMinVelocity` → `Hold`. Otherwise `Decay`; if its asymptote crosses an edge the crossing time is **solved analytically at authoring** and a second segment is appended (`Spring` under `RubberBand`, firm `Hold` under `None`). |
| `FollowCancel` | Drops a contact with no fling: in place, or a zero-velocity `Spring` back from an overpan. |
| `Glide(prev, tNow, target, feel)` | One `Glide` (ζ = 1, ω = `GlideOmega`), velocity-continuous from the displayed `(p, v)` at `tNow`. |
| `Immediate` | One `Hold` at the clamped target. |
| `Thumb` | One `Hold` at the pointer-mapped position, re-authored per pointer sample (`MotionKind.Thumb`). |
| `Constant(prev, tNow, velocity)` | Drag-reorder edge auto-scroll: a near-zero-rate `Decay` (K = 1e-9 ⇒ `p = P0 + V0·t`) to the edge, then a `Hold`; crossing time solved at authoring. |
| `Key(prev, tNow, KeyMove, feel, viewport)` | Line = `KeyLineDip`, page = `PageFraction·viewport`, Home/End = `Min`/`Max`; authored as a `Glide`. |

**Snap.** `SnapTargets` (`SnapTargets.cs`) resolves a fling onto a viewport's `SnapGrid` (uniform interval or explicit
points; WinUI mandatory snap points): `ResolveFling` re-authors a freshly authored fling to land exactly on the grid in
finite time; a wheel notch's destination is re-targeted in the notch's direction (same cubic, same anchor/duration —
`ScrollHandle.Wheel`); a sub-threshold lift still glides onto the grid (`ScrollHandle.ContactEnd`). Keyboard and
programmatic moves are never snapped.

### 3.3 Feel as data — `MotionFeel`, `FeelProfiles`, `ScrollTunables`

`MotionFeel` (`MotionFeel.cs`) is ONE POD record of every constant an author needs (25 fields: wheel notch/duration/rise,
spin-acceleration curve (incl. the unity gap), the touchpad wheel-fallback distance, fling decay/min velocity, LSQ horizon + min span, rubber band, spring/glide ω/ζ, virtualization
look-ahead + overscan min/max, key line/page, wheel latch silence, settle epsilons). Values are **read at author time and
baked into the plan** (`RubberC` is the precedent) — never read back out mid-flight. The contact ring's resampling bounds
are fixed constants on `ContactRing`, not feel.

`FeelProfiles`: **`Standard`** (default, 2026-09-25) and **`Glide`** (56 DIP / 0.40 s, the owner's "momentum" tuning).
`Standard` is tuned from the owner's own notch traces, not cloned from a WinUI control: 64 DIP per detented notch on a
0.15 s front-loaded cubic (50 % in 52 ms, 90 % in 109 ms, at rest ~170 ms after the notch), `WheelRiseS` = 12 ms C1
blend-in, and the spin curve `AccelRefGapS` 0.009 / `AccelUnityGapS` 0.050 / `AccelMax` 2 re-derived so the owner's fast
spins (6–17 ms gaps) travel within ±6 % of the previous 32 DIP model (`WheelFeelTraceTests` replays the recorded
stamps). It replaced `WinUiExact` (the measured WinUI 3 `ScrollView`: 32 DIP, 0.257 s, velocity-step re-plans), whose
single notch the owner called "mud". The detented notch is a FIXED distance — never viewport-relative, never
row-snapped — scaled by `SystemParams.WheelScrollLines / 3` (folded into the notch count by the Win32 PAL). A touchpad's
wheel-packet fallback stream uses its own `TouchpadWheelDip` (32 DIP per 120 raw), not the mouse notch.

`ScrollTunables` (`Scroll/Diag/ScrollTunables.cs`) is the live registry: a seqlock over one `MotionFeel` readable from any
thread (`Current`), `Apply`/`ApplyProfile` for the single logical writer, a `Version` counter (no change event),
`ActiveProfileName` ("Custom" after a field edit), per-field slider metadata `All` (`TunableF{Name, Min, Max, Default,
Get, With}` — built once, no reflection) and a hand-written NativeAOT-safe JSON round-trip (`ToJson`/`TryFromJson`).
Every `ScrollHandle` input path and `ScrollRouter` read `ScrollTunables.Current` **at author time**, so an edit lands on
the next notch or lift.

---

## 4. Input — producers, classification, routing

### 4.1 One event shape

`ScrollInputEvent` (`Scroll/Runtime/ScrollInput.cs`): `(ScrollSource, ScrollGesture, long Qpc, Point2 PointerDip, float
Dx, float Dy, uint PointerId, KeyModifiers)` + `LiftDetectedLate` + `PresentTimed` + `Release` (an End's
`ContactRelease` verdict — `Unknown`/`Moving`/`Stopped`, `Scroll/Motion/ContactRelease.cs`) + `ArrivalQpc` (when the
producer observed the event; 0 ⇒ `Qpc` — a composition-timed event's `Qpc` is a present stamp, so latency is measured
from this, §10.5). Sign convention: **positive = toward the content end**.
`Dx`/`Dy` are notch units for `ScrollGesture.Notch` and DIP for `Sample`. `ScrollSource` = `MouseWheel`,
`MouseWheelHiRes`, `Touchpad`, `Touch`, `Pen`, `Keyboard`, `Thumb`, `Programmatic` (byte-mirrored by
`ScrollSourceCode` for the probe). Contact sources run `Begin → Sample* → End`; a wheel emits `Notch` only.

Wheel input is **urgent**: the Win32 PAL delivers it synchronously through `IPlatformWindow.SetScrollInputSink` →
`AppHost.OnUrgentScrollInput` → `InputDispatcher.DispatchScroll`, and the plan write wakes the render thread
(`PlanSlots.OnWritten` → `RenderThread.WakeAsync`) — no UI frame sits between a notch and its first pose.

### 4.2 `WheelClassifier` (pure, per gesture)

Every `WM_POINTERWHEEL` packet outside a live DirectManipulation contact goes through `WheelClassifier.Classify(raw,
evidence, gapMs)` (driven by `Win32Platform.HandlePointerWheel`), latched per gesture: (1) authoritative touchpad
evidence (`WheelDeviceEvidence.PrecisionTouchpad` — PT_TOUCHPAD / DM evidence) ⇒ `Touchpad`; (2) a sub-notch delta
(not a multiple of `DeltaPerNotch` = 120) ⇒ `MouseWheelHiRes`; (3) else `MouseWheel`. Rules 1–2 only escalate; a
positively identified mouse is never read as a touchpad; a gap above `GestureGapMs` = 200 ms re-evaluates from scratch.
Then:

- **Detented wheel** → whole notches via `WheelClassifier.Carryover` (signed remainder kept), × `WheelScrollLines/3`
  (chars/3 horizontally; page mode = the page-equivalent multiplier) → one `Notch` event.
- **Hi-res mouse wheel** → `WheelClassifier.HiResNotch`: every packet a FRACTIONAL notch (`raw/120`) on the same
  accumulating wheel plan. A mouse wheel has no lift — no silence-detected release, no coast.
- **Touchpad outside DM** (e.g. a pan over a popup HWND) → a CONTACT stream: `Begin` on the first packet, one DIP
  `Sample` per packet (`TouchpadWheelSample` = `raw/120 × WheelNotchDip`), `End` on packet silence (a cadence-adaptive
  lift threshold = `clamp(1.4 × median gap, 50, 120)` ms, 80 ms before any cadence is known, checked on a monotonic
  timer), stamped at the LAST packet and flagged `LiftDetectedLate`. Each packet is one device-timed sample; the contact
  runs `ContactClock.Device`, so between packets the ring is shown with the bounded resampling of §3.1 — never free
  extrapolation.
- Ctrl + a hi-res or touchpad stream is the OS's pinch synthesis: consumed, never scrolled (`IsPinchSynthesis`).

### 4.3 DirectManipulation — a contact producer, never a physics owner

`Win32DirectManipulation` (`FluentGpu.Windows/Pal/`) consumes the precision-touchpad contact and re-emits it as
`ScrollSource.Touchpad` events: `Begin` on DM RUNNING, a DIP `Sample` per produced content delta, `End` on the lift. WHICH
callbacks are motion is the pure `DmContactStream` (same folder; `DmContactStreamTests` replay real per-Update scripts):

- **An Update that ends in a non-RUNNING status carries no motion.** DM stops producing content at the physical lift and
  reports the status edge 1–5 frames later; inside that last Update it raises `OnContentUpdated` with its whole-pixel
  snap (a sub-pixel delta) BEFORE `OnViewportStatusChanged`. Content is therefore buffered and delivered as one `Sample`
  when the Update returns, only if the viewport is still RUNNING (`OnUpdateReturned`). The snap — once emitted as a
  sample stamped at the End, which collapsed the ring's velocity and held 9 of 22 fast flicks dead — never reaches the
  engine. This is ordering, not a delta-size threshold.
- **DM decides moving vs stopped.** The viewport is configured `INTERACTION | TRANSLATION_X | TRANSLATION_Y |
  TRANSLATION_INERTIA` so DM's own release decision is visible: RUNNING→INERTIA = released moving
  (`ContactRelease.Moving`), RUNNING→READY = released at rest (`Stopped`), SUSPENDED/DISABLED = `Unknown`. The verdict
  rides the End (`ScrollInputEvent.Release`). DM never owns the coast: an INERTIA viewport is stopped at the next pump
  (`DmStatusEffects.StopAtNextPump`; `Live` stays true until then), and the engine's `ScrollHandle.ContactEnd` authors
  the fling. One `IDirectManipulationUpdateManager::Update` per produced frame
(`UpdateFrame`, from `Win32Platform.PumpScroll` after the display-phase gate) plus an idle MANUALUPDATE drain
(`UpdateIdle`, ~250 ms) while enabled and not live. The ONE rule: a claimed contact (`DM_POINTERHITTEST` →
`SetContact`) that has not reached RUNNING within `EngageTimeoutMs` = 250 ms is released, and the OS then delivers the
gesture as the wheel-path touchpad stream above. While DM is `Live` it owns the touchpad; only a positively identified
physical mouse pre-empts it (`TryStopForPhysicalWheel`, synchronous End). `ScrollProducerLive` keeps the host producing
one frame per refresh while a contact is engaged or pending.

### 4.4 Contact timing — two clocks, one prediction

**Composition-timed (DirectManipulation).** `Win32DirectManipulation.UpdateFrame` hands DM a frame-info composition hint
of `lead = clamp(PresentQpc − now, 0, one refresh)` (whole milliseconds, through the `IDirectManipulationFrameInfoProvider`
CCW, `CompositionDeltaMs`), so DM evaluates the manipulation for that composition instant — DM's own latency
compensation, and nothing else. The STAMP is a separate rule: every contact event the Update produces is stamped
**`ContactStamp.ForFrame(clock, now)` = `clock.PresentQpc + clock.RefreshQpc`** — the present time of the FIRST render
turn guaranteed to see it, the next tick's present (the `RenderPresentSec` law one refresh on; `now` when the clock has
no refresh). The UI writes a frame's sample within ±0.5 ms of the render thread reading `PlanSlots` on the same tick, so
a sample stamped for the tick that reads it was shown or not by whichever side won the race (2026-09-29 RCA: +2/0 sample
steps on ~14 % of fast drag frames). Stamped one tick on, render tick *k* poses at `P(k)` = the stamp of sample *k−1*
and shows it whether or not the UI has written sample *k* yet: the shown position is a function of time, never of thread
order (Gecko APZ's one-frame delay between computing the async transform and compositing it; Flutter's pointer
resampler — present plus a negative offset, interpolate, hold past the newest). Staleness is measured against the
pump's WALL time, not its stamp: an event more than 20 ms (`StaleStampTicks`) after its Update — a status edge raised
inside `ProcessInput` — is stamped now. The headless producer (`HeadlessPlatform.PumpScroll`) stamps its touchpad stream
by the same `ContactStamp` rule. Events carry `ScrollInputEvent.PresentTimed = true` and `ArrivalQpc` (the wall time the
producer emitted it); the dispatcher converts their stamps with `ScrollFrameQpcToSec` (the frame clock IS the plan clock) and opens the
contact on **`ContactClock.Present`**: `ScrollHandle.ContactBeginHere(t, Present)` takes the stamps as they are (no
pose-floor anchor, no slide), `PlaceContactTime` only refuses to go back past the newest sample (a status edge stamped
"now" must not rewind what was shown), and between lattice points the ring interpolates two REAL samples; past the newest
it still holds (only when the UI stalls more than a refresh) — **no second prediction**.

**The Present-clock lift.** The End is stamped at DM's status edge, 1–5 frames after the last movement. With a verdict
(`Release` ≠ `Unknown`), `ScrollHandle.ContactEnd` authors `PlanAuthor.FollowEnd(prev, tLift: the ring's newest REAL
sample, tNow: the End stamp, release)` — the existing late-lift path: the plan holds the newest sample, and a `Moving`
release coasts at the ring's velocity decayed over that latency (no jump, no re-prediction). Both stamps are
`ContactStamp` present times, so the one-tick shift moves `tLift` and `tNow` equally and the decay is unchanged; a
lead-0 End (`TryStopForPhysicalWheel`, stamped now) is placed at the newest sample by `PlaceContactTime`. A verdict-less
End and every device-clock stream keep `tLift = tNow = the End's time` and the `StoppedAfterS` time rule.

**Device-timed (touch, pen, the touchpad wheel fallback).** Stamps are device times: `ContactBeginHere(t, Device)` anchors
the first sample at `max(t, pose floor)` and slides the stream by the same amount; `ResyncContactClock` slides the whole
ring back when a device stamp lands ahead of the plan clock (the spacing — the velocity — is kept); past the newest sample
the ring applies the bounded resampling of §3.1.

**No zero-delta samples.** A contact stream reports MOVEMENT. Neither DM (which raises no content update without
movement) nor the dispatcher synthesizes a sample for a stationary frame: `DispatchContactStream` ignores a report with no
movement on the locked axis. Whether the finger stopped is the producer's verdict at the lift or, without one, the time
rule (`PlanAuthor.StoppedAfterS`), never
a sample stamped with an unchanged position — that would read as a velocity collapse the finger never made.

In the dispatcher (`InputDispatcher.DispatchContactStream`) a contact opens **unrouted**: its axis (dominant axis of the
first movement) and direction decide the scroller, then `ScrollHandle.ContactBeginHere` grabs the SHOWN position and
each sample is `ContactDelta` (accumulated onto the contact origin). A touch/pen pan claimed by the gesture arena drives
the same handle positionally (`PanClaimed`/`PanSample`/`PanEnd`).

### 4.5 `ScrollRouter` — latch and chain (pure, UI thread)

`ScrollRouter.Decide(hitScroller, horizontal, sign, tNow, src, phase, mods)` over an `IScrollerQuery` (the
dispatcher implements `ParentScroller`/`CanMove` from the live plan):

- **Wheel latch** holds until `WheelLatchSilenceS` of notch silence or an axis change; **contact latch** holds
  `Begin → End`. Shift + wheel routes horizontal.
- **Chain to the parent only when the latched child is at its edge AND the gesture BEGAN at that edge in that
  direction** (`_latchEdgeSign`). A shelf that reaches its end mid-spin absorbs the rest of the gesture — no mid-gesture
  hand-off (WinUI shelf rule). A contact whose scroller is pinned at its starting edge hands its deltas up mid-stream
  (`ContactCancel` on the old handle — a hand-off is not a release, it must not fling).
- Keyboard / thumb / programmatic are never latched: the owning scroller, walking up only when it cannot move.

Wheel dispatch order (`DispatchWheelNotch`): element `OnPointerWheel` first-refusal → Ctrl + `InputHooks.ZoomWheel`
(owned by `input-a11y.md` §7B) → `Element.WheelTarget` routing (a header forwarding to its list's handle) → router →
`ScrollHandle.Wheel`.

---

## 5. Runtime — `Scroll/Runtime/`

### 5.1 `PlanSlots` — the seam

`PlanSlots` (`PlanSlots.cs`): a fixed table of `Capacity` = 64 per-viewport slots keyed by `ScrollViewportId(Node,
Gen)` (a stale slot can never be read as a remounted viewport's plan), each a **seqlock** (odd = write in flight).
**The UI thread is the only writer** (`Allocate`/`Release`/`Write`/`Shift`); readers on any thread `TryRead` a never-torn
copy (key first, then the ~600-byte plan only for the match). `Epoch` counts writes; `OnWritten` is the wake hook.
Scans walk `[0, highWater)`, and `Release` pulls the bound back. Zero allocation after construction. `Allocate` refuses
at capacity (the viewport degrades to unposed — never a throw on a hot path).

### 5.2 `ScrollPoser` + coverage

**What coverage is.** `Start`/`End` describe what the UI actually BUILT: for a virtual viewport
`ScrollContentPose.CoverageOf(ext, prefix, FirstRealized, LastRealized)` is the one definition (written by the reconciler
at realize and by `FlexLayout.ArrangeVirtual` after measurement; the slice recorder reads the same `ScrollState` fields).
The realized children are a persistent prefix `[0, prefix)` (retained at every depth) plus the recyclable window
`[FirstRealized, LastRealized)`; when the window starts at or right after the prefix the two are one band from the
content start (`Start = 0`), otherwise `Start = OffsetOf(FirstRealized)`; a window reaching the last item covers through
`Total`. (Publishing `OffsetOf(FirstRealized)` alone clamped every prefixed list — Track.Table's hero + chrome, the
sidebar rail's head — to the prefix height at rest: `gate.scroll.prefix-coverage`.)

`ScrollCoverageTable` (`ScrollCoverage.cs`) holds ≤ 64 `ScrollCoverageRow`s (`Vp, Gen, ContentNodeIndex, WindowOrigin,
Start, End, Viewport, ExtentTotal, Horizontal, EffectStart, EffectCount`) and a pool of ≤ 512 `ScrollEffectRow`s
(`NodeIndex, ScrollEffect, EffectGeometry`). The UI fills it after layout (`AppHost.FillScrollCoverage`, parked
viewports excluded); it rides the publication and the render poser `Adopt`s a private copy on a fresh publication.

`ScrollPoser.Tick(slots, presentSec, dpiScale, sink)` per coverage row:

1. `plan.Eval(presentSec)`.
2. **Coverage clamp:** `lo = Start` (−∞ when coverage starts at 0), `hi = End − Viewport` (+∞ when coverage reaches the
   extent end), so a rubber-band overpan past a true end shows empty space and is **not** a clamp; a clamp inside the
   content is recorded (`ScrollPoseFeedback.Clamped`, `ScrollProbe.Pose(clamped: true)`).
3. `trans = ScrollEffectEval.SnapToDevicePixel((float)(WindowOrigin − shown), dpiScale)` →
   `sink.PoseViewport(vp, shown)` + `sink.PoseContent(content, horizontal, trans, changed)`.
4. Effects evaluate at **`pSnapped = WindowOrigin − trans`** — the position actually painted — so a sticky header and the
   rows it pins over share one pixel grid. Transform-class rows of one node (contiguous by construction) fold into ONE
   `EffectTransform` → `PoseTransform`; other channels → `PoseEffect`.
5. Publishes `ScrollPoseFeedback(Vp, Gen, Shown, Velocity, Settled, Clamped, Kind, PresentSec)` through a per-slot
   seqlock; `HasActive` = any unsettled plan (the host's motion-due term).

`changed` is true only when the value differs from the previous tick or the coverage was just adopted — a settled
viewport re-posed while something else ticks writes but produces no damage (`ScrollPoseDamageTests`).

There are **two posers** with identical arithmetic: the UI poser (`AppHost._uiPoser`, `UiPoseSink` — hit-testing, the
published frame, headless) and the render poser (`AppHost._renderPoser`, `new ScrollPoser(recordsProbePoses: true)`,
`SnapshotScrollPoseSink` over the adopted snapshot's compositor overlay). Only the render poser writes probe pose rows.
Transform-class poses are **composite parameters** of the retained-tile slices (no dirty trail, a pure scroll tick
records 0 bytes); non-transform channels (opacity, clip, presented height, child shift) change recorded bytes, so the
render sink sets `RecordRequired` (see `gpu-renderer.md` §13.1 for what a composite-only turn is).

**Frame rule — plan shift and coverage are atomic.** A measured correction shifts the plan in `PlanSlots` the moment
layout discovers it, while the content the current coverage describes was arranged before that shift and stays on screen
until the next publication is adopted. So every slot carries its cumulative frame shift (`PlanSlots.FrameShiftOf`, read
together with the plan under the same seqlock by `TryRead(vp, out plan, out frameShift)`); `FillScrollCoverage` records
it on the row it builds (`ScrollCoverageRow.FrameShift`); and the poser evaluates **in the coverage's own frame**:
`p = plan.Eval(presentSec) − (frameShift − row.FrameShift)`. From the poser's view a shift and the coverage laid out
under it are never torn apart — no one-frame jump of the correction, no jump back on the adopt.

### 5.3 Present time and the pose floor

- **Present-time law:** `AppHost.RenderPresentSec(tickQpc) = tick + (1 + maxFrameLatency)·refresh` (no clock ⇒ now); the
  UI frame's `presentSec` comes from `RefreshLattice.Build` on the same law. The depth term follows
  `PresentQueueDepthPolicy` (§8).
- **Pose floor:** `AppHost.ScrollShownFloorSec` = the latest present time the render poser has already posed
  (`_renderPosedPresentSec`); if the render thread is parked, one refresh before the next turn's present. Every
  `ScrollHandle` re-plan that continues the live plan is anchored at `max(inputTime, floor)` (`AnchorAt`,
  `PlanAuthor.WheelNotch`'s `shownFloorSec`), so the first new frame shows exactly one refresh of the new curve —
  frames already posed are never caught up or stepped back over. (A composition-timed contact is the one exception: each
  stamp already is the present time of the first render turn that sees it — `ContactStamp.ForFrame`, the next tick's
  present — so it is taken as it is — §4.4.)
- **Plan clock:** `AppHost.ScrollNowSec` (QPC seconds; the deterministic frame clock headless); device stamps convert
  through `ScrollQpcToSec`.

### 5.4 `ScrollHandle` — the one app-facing surface

`ScrollHandle` (`ScrollHandle.cs`) is app-constructible and travels down through `ScrollEl.Handle`,
`VirtualListEl.Handle` or `ScrollOptions.Handle` (else the host mints one). The host `Bind`s it when the viewport node
mounts (`AppHost.ResolveScrollHandle`) and `Unbind`s on unmount (keeping `LastShown`). **Every** input path and
programmatic move authors through `PlanAuthor` with `ScrollTunables.Current` and writes the slot; nothing else writes a
plan (the one exception is the extent write's `PlanSlots.Shift`, §6).

- Signals (UI thread, fed once per UI frame by `ApplyFeedback`/`ApplyShown`): `Offset`, `Motion`
  (`ScrollMotionState(Kind, SpeedDipPerS, UserDriven)`), `AtStart`, `AtEnd`, `CanScroll`, `ExtentSignal`,
  `ViewportSignal`, `MaxOffsetSignal`. Values: `Extent`, `Viewport`, `MaxOffset`, `Plan`, `OffsetNow`, `EvalAt`.
- Programmatic: `ScrollTo(offset, ScrollMove)`, `ScrollBy(delta, move)` (accumulates onto `Dest` for a glide),
  `BringIntoView(itemTop, itemExtent, align, move, margin)` (NaN align = minimal move). `ScrollMove.Glide` (default,
  velocity-continuous), `Immediate` (a `Hold`), **`Follow`** (the same glide, **ignored while a user-driven plan is
  live** — the lyrics follow). A target past today's extent lands at today's max and stays latched until the content
  can hold it; any user input drops the latch.
- Input verbs (host/dispatcher): `Wheel`, `WheelNow`, `ContactBegin`/`ContactBeginHere`/`ContactSample`/`ContactDelta`/
  `ContactEnd(t, detectedAt)`/`ContactCancel`, `ThumbTo`, `Key`, `Stop` (holds where the content WAS SHOWN),
  `AutoScroll`, `ShiftFrame`/`NoteFrameShift`, `SettleIfDue`.
- **Restore:** `Restore(offset)` arms a `RestoreLatch` that resolves as an `Immediate` move once
  `max(0, extent − viewport) ≥ target` (`SetExtent`); user or programmatic input cancels it. A move on an unbound handle
  is latched and applied at bind.

**Scroll memory and scoping.** `ScrollEl.ScrollKey` / `VirtualListEl.ScrollKey` / `ScrollOptions.ScrollKey` is a stable
per-content identity; the host saves the shown offset in `ScrollPositionMemory` (256-entry LRU) when a keyed viewport
unmounts or changes key and `Restore`s it on the next mount under the same key (`AppHost.OnScrollKeyChanged` /
`SaveScrollPosition`, wired from the reconciler). The engine keys by the string alone — **an app that can show the same
content twice (tabs) composes its own scope into the key** (Wavee: `Shell.PageScrollScope`).

**Observation.** `ScrollCtx.Nearest` (`Hooks/ScrollHooks.cs`) is provided by every `ScrollEl`/`VirtualListEl` to its
content. `Component.UseScroll(handle?)` → `ScrollObservation(Offset, Motion, AtStart, AtEnd, Extent, Viewport)` for the
nearest scroller (or `ScrollObservation.None`); `UseScrollProgress(in0, in1, handle?)` → a memoized
`clamp01((offset − in0)/(in1 − in0))`. Read them in a bind or a coarse memo; a render that reads `Offset` re-renders
every moved frame.

**`UseScrollThreshold(enterAt, exitAt, handle?)` — a hysteresis flip over the nearest scroller's offset**
(`Hooks/RenderContext.cs`, pure rule `Scroll/Runtime/ScrollThreshold.cs`): the Wavee Home compact-facet-band pattern —
shown once offset moves strictly past `enterAt`, hidden again once it drops strictly below `exitAt` (staying at rest
while inside `[exitAt, enterAt]`, whichever state is current — the dead band that keeps a wheel-jitter right at the
edge from chattering). `ScrollThreshold.Next(prev, offset, enterAt, exitAt) => prev ? offset >= exitAt : offset >
enterAt` is the pure, engine-free rule the hook threads its own previous flip through (held in a non-reactive `Ref<bool>`,
so it costs no extra signal). It is a `UseScrollProgress` sibling built the same way — `UseComputed` over the offset
signal — so it inherits `Memo<T>`'s push-pull equality cut-off for free: the memo body reruns every moved frame (one
comparison, zero allocation) but only notifies subscribers on an actual flip, so a component bound to it re-renders
twice per crossing (never once per scrolled frame) and a scroller with no threshold subscriber does no extra
per-frame work at all. `enterAt`/`exitAt`/the scroller freeze at mount, like every hook's inputs — remount through a
key to change them. Home's own compact band (E6) calls it as `UseScrollThreshold(64, 56)`.

**Node-level bring-into-view.** `SceneScrollExtensions.BringIntoView(scene, node, align, move, margin)` resolves the
nearest scrolling ancestor and the node's content coordinates (`WindowOrigin + local`, × zoom) and calls the handle;
the explicit-viewport overload serves a composing control. An unrealized virtual index is resolved from the list's layout
model and passed to `ScrollHandle.BringIntoView`/`ScrollTo` directly.

### 5.5 The UI frame step (`AppHost.Scroll.cs`)

`RunScrollFrame(presentSec)` per live, **non-parked** scroll node (a parked KeepAlive page is not evaluated and never
holds the loop awake — `ScrollParkedViewportTests`): `SetExtent` (zoom-scaled), `SetSnap`, `SettleIfDue`, evaluate at
`presentSec`; `shown` = the plan value (rubber-banded plans show their overpan; everything else hard-clamped to
`[0, MaxOffset]`); write `ScrollState.Offset/Velocity/Motion`; feed the handle (render feedback when the render poser
posed it, else the UI evaluation); arm scrollbar chrome; run the virtualizer (§6) and mark `VirtualRangeDirty` when the
realized window no longer covers the present-time window. `AnyUserScrollMoving` (this frame OR last) is the one input to
FLIP suppression (`Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.Scroll, …)`).
`SyncScrollPlansMidFrame` re-evaluates a plan authored mid-frame (a ScrollKey restore, a layout effect's BringIntoView)
so it shows and windows in THIS frame; `RefreshScrollExtents` publishes laid-out geometry before any between-frames plan
is clamped; `PoseScrollUi` fills coverage and runs the UI poser.

---

## 6. Extent and virtualization — `Scroll/Extent/` + `Runtime/Virtualizer.cs`

### 6.1 `IExtentSource`

The one per-viewport answer to "where is item i" (`OffsetOf`) and "which item is at x" (`IndexAt`), in **doubles**, with
estimate-then-correct measurement: `SetMeasured(i, extent, anchorIndex)` returns the delta the correction applied
**above the anchor** (0 at or below it). `Resize` grows at the end only. `OffsetOf(0)` is the first item's own start —
0, or a layout's leading pad (`FillRowVirtualLayout`'s lead inset): every index answers its layout rect, item 0 included,
because the arrange places a row at `OffsetOf(i) − OffsetOf(origin)` and a hard 0 for item 0 put the first row one
gutter before the rest of its column (`VirtualLayoutExtentLeadInsetTests`). The content start is always 0.

| Source | For |
|---|---|
| `FixedExtent(count, stride, leadingPad, trailingPad)` | fixed-stride lists; exact by construction, `SetMeasured` is a no-op |
| `MeasuredExtent(n, estimate)` | variable-height rows: a Fenwick tree of double partial sums, O(log n) offset/index/correct; `SetEstimate` retargets unmeasured rows without moving anchored ones; arrays grow by doubling only |
| `VirtualLayoutExtent(layout, count, cross, horizontal)` | grids, fill-row shelves, grouped lists over an `IVirtualLayout` (`RowStart`/`RowEnd` widen a window to whole rows); measured layouts forward `SetMeasured` |

### 6.2 `Virtualizer` (pure)

- `Plan(ext, p, v, viewport, feel, anchorPrev)` → `RealizeWindow(First, Last, CoverStart, CoverEnd, AnchorIndex,
  VisibleFirst, VisibleLast)`: overscan **ahead** of the motion = `clamp(|v|·LookaheadS, OverscanMinPx, OverscanMaxPx)`,
  behind = `OverscanMinPx`. The anchor is the first FULLY visible row.
- `ScrollContentPose.NeedsRealize(sc, rw)`: the strictly visible band must ALWAYS be realized; the lead band is realized
  the moment it outruns the window; a window wider than needed by more than `max(TrimSlackRows = 4, count/2)` trims. A
  persistent prefix counts as covered; `MeasureAll` re-realizes only when the count changes.
- `ArrangeOriginIndex` keeps the previous arrange-origin row while every realized row stays within
  `MaxLocalExtent` = 16384 DIP of it (retained rows keep their boxes and their recorded spans), else re-centres.
- **`ApplyMeasured(ext, index, extent, anchorIndex, slots, vp)` is THE extent write:** it records the measurement and,
  when the delta is non-zero, `PlanSlots.Shift(vp, delta)` in the same call and a probe `Extent` row. Its caller,
  `FlexLayout.ArrangeVirtual`, sums the shifts, arranges rows relative to `WindowOrigin` as small floats, publishes
  `WindowOrigin`/`CoverStart`/`CoverEnd`, moves this frame's `ScrollState.Offset` by the same shift and calls
  `ScrollHandle.NoteFrameShift` so a live contact's origin moves with the frame.

**Realize-all.** Every row in the planned window realizes in the frame it is needed — there is no per-frame row cap, no
cold ramp, no stagger. Flat rows are cheap by construction (`ListRowEl`: one node per row, ≤ 8 `RowCell`s through the
same glyph/image pipelines, zero allocation on a text change — `gate.listrow.*`); rich rows stay components.

**`MeasureAll`.** `VirtualListEl.MeasureAll` / `ListOptions.MeasureAll` (→ `ScrollState.MeasureAll`) realizes and measures
every item, so a target computed from the layout for a row never on screen is its REAL offset (lyrics). Bounded by the
caller (Wavee caps it at 400 lines).

### 6.3 Structural corrections

`ScrollHandle.ShiftFrame(delta)` is the instant rebase for a structural change above the first visible row (a reorder,
an insert, a drawer measuring open — `ItemsView`, `LazyGrid`): it moves WITH every in-flight arc. It is never a motion
and never a substitute for `ScrollTo`. Like a measured correction it goes through `PlanSlots.Shift`, so the poser's frame
rule (§5.2) keeps it atomic with the coverage.

**A reseed is an extent change and is anchored.** A measured layout whose extents can be rewritten out of band — a
wholesale reseed back to its seeds (`MeasuredStackVirtualLayout.Reseed`, `IAnchoredReseedLayout`) — keeps the extents it
replaced until the engine next touches the viewport: the reconciler's `RealizeWindow` (before it plans the window) or
`FlexLayout.ArrangeVirtual`'s pass 0 (before the measured corrections) takes the rewrite's effect on the anchor row
(`TakeReseedShift`) and shifts the plan frame by it. Unanchored, a reseed moved every row by the seed error with no
shift, and the re-measure that corrected it shifted the plan — a net jump of the whole error with the content standing
still (the Library V3 rail, 2026-09-25: a 101-DIP head seed against a 287-DIP head, +186 DIP on every selection). Gate:
`gate.scroll.reseed-anchored`.

---

## 7. Scroll-linked effects — `Scroll/Effects/`

### 7.1 Model

A `ScrollEffect` is an immutable POD `(EffectChannel Channel, EffectKind Kind, double In0, double In1, float Out0, float
Out1, Easing Ease, float Inset, int ScopeNode)`, evaluated by `ScrollEffectEval.Evaluate(effect, offset, geometry)` —
**one arithmetic expression per kind, plain double math (no FMA), bit-identical on both threads**. `EffectGeometry
(NodeY, NodeH, ScopeEnd, Extent, Viewport, Track, ThumbLen)` is captured in content coordinates at coverage fill.

| `EffectKind` | Channel(s) | Formula |
|---|---|---|
| `Map` | any (`TransX`/`TransY` parallax, `Opacity` fade, `ScaleXY`) | eased, clamped `In0..In1 → Out0..Out1`; a degenerate range writes `Out0` |
| `Sticky` | `TransY` | `clamp(offset + Inset − NodeY, 0, max(0, ScopeEnd − NodeH − NodeY))` — CSS `position: sticky`, releasing at the scope's end |
| `StickyClip` | `ClipTop` | `clamp(offset + Inset − NodeY, 0, NodeH)` — guillotines the node at the sticky line (input over the clipped band is gated too). A COMPOSITE channel when the node is cut as a slice: the clip rides its marker (`CompositeSliceFlags.StickyClip`) and a page scroll writes no bytes (`gpu-renderer.md` §13.1e) |
| `Thumb` | `ThumbPos` | `offset/(Extent − Viewport)·(Track − ThumbLen)`, clamped — the pure formula; the engine's own overlay-scrollbar thumb is a `SliceRole.Thumb` slice posed from the viewport's shown offset (`SliceRecorder`, `PoseKind.Thumb`), and no paint sink writes `ThumbPos` today |
| `Collapse` | `PresentedH` / `ChildShiftY` / `ClipBottom` | presented height `lerp(full, Out1, clamp01((offset − In0)/(In1 − In0)))` (`Out0` NaN ⇒ laid-out height); `ChildShiftY = presentedH − full` (≤ 0); `ClipBottom = presentedH` (the leading anchor's cut) |
| `Stretch` | `ScaleXY` | `1 + (−offset)/NodeH` while rubber-banded past the start, else 1 |

`PresentedH` is read by the recorder (fill + a `ClipToBounds` node's child clip), hit-testing and accessibility, so a
collapsed hero stops taking input below its presented edge **without a relayout**. `ClipBottom` writes
`NodePaint.CollapseCut` — a `ClipRect` open on its top, left and right (the `StickyClipSpan` sentinel) — and the
recorder and hit-testing cut the node's children (paint and input) at the EXACT presented edge only: a leading
collapse needs no `ClipToBounds`, and a child drawing above the node's top (a `.StretchFromTop()` photo in a top
overscroll) keeps those pixels. A `ClipToBounds` node keeps exactly its box clip (the cut adds nothing to it).

### 7.2 Authoring (`ScrollEffectDsl` on any `Element`)

`ScrollEffectSpec(Effect, Scope, Engaged)` is the authoring form stored on `Element.ScrollEffects`:

- `.Sticky(top, scope, engaged:)` — pins `top` DIP below the viewport edge, releasing at the end of the ancestor named
  `scope` (`Element.ScrollScope`), **null scope = the node's parent** (a direct child of the content pins for the whole
  extent). `.StickyClip(inset, engaged:)`. A feather on the cut is an `EdgeFade = new EdgeFadeSpec(Top, band)
  { WhileStuck = true }` on the SAME node: the fade stays in the element tree and the slice recorder applies it exactly
  while the node's sticky clip is engaged, on the render turn that poses the clip (`CompositeSliceFlags.FadeWhileStuck`;
  released, nothing is softened). Never switch a feather off an `engaged:` signal — that re-render lands a publication
  or more after the render-posed clip (`gate.scroll.engaged-feather-composite`).
- `engaged:` is a `Signal<bool>` — CSS `:stuck` as a UI-DECISION edge (input hand-off, a feather's gate). The host writes
  it on the UI thread, before the frame publishes, **only on a flip** (compared against the signal itself; a steady frame
  writes nothing). It is not a pixel channel — a "stuck" look is a `Fade` row at the same offset. For a **`Sticky`** row
  the same flip sets `NodeFlags.StickyPinned | PaintDirty` (a pinned header paints after the siblings that scroll under
  it); a **`StickyClip`** never changes paint order — it guillotines its node in place, so a wash clipped at the band
  stays behind the hero it backs (`ScrollEffectEval.PinsAboveSiblings` is the one decision).
- `.Collapse(over, minH, CollapseAnchor)` — `Leading` = the children stay put and are cut at the presented edge by the
  node's own `ClipBottom` row (the presentation carries its own motion); `Trailing` (default) adds the `ChildShiftY`
  row so the children ride the presented bottom. Pair with `.Sticky(0)` to hold the collapsing node at the top.
  **Do not put `ClipToBounds` on a leading collapse root** whose content stretches: the box clip cuts the stretch
  above the root's top (the dark band over Wavee's artist photo) — the collapse cuts on its own
  (`gate.scroll.stretch-under-collapse`).
- `.StretchFromTop()` — overscroll stretch about the top-centre, composing with a parallax on the same node, and with
  a leading `.Collapse` on an ancestor (the hero recipe: `root.Sticky(0).Collapse(over, minH, Leading)` around a
  `ClipToBounds` photo `{ TransformOriginY = 0 }.StretchFromTop()`, no `ClipToBounds` on the root).
- `.Parallax(in0, in1, out0, out1)`, `.ParallaxY(fraction, overPx)`, `.Fade(in0, in1, from, to)`,
  `.Reveal(start, overPx, dy)`, `.OnScroll(effect[, scope])`, `.OnScroll(spec)`, `.OnScroll(params effects)`.

### 7.3 Composition and the one snap

`EffectTransform(Scale, Tx, Ty, Stretch)` folds a node's transform-class rows per tick — translations add, scales
multiply — and `ToLocal(w, h, originX, originY)` writes ONE matrix (the stretch pivot re-expressed against the node's
authored origin), so a parallax and a stretch on one hero both land. `ScrollEffectEval.SnapToDevicePixel` (banker's
rounding) is **the only snap in the scroll system**: the poser snaps the content translate once; effects derive from the
snapped position; sinks must not snap again.

---

## 8. Pacing — how posed frames reach the glass

- **One present per compositor tick** (`PresentCadence.Decide`, `RenderThread.PresentTurn`): a fresh publication wins;
  otherwise, while motion is due, the render thread **re-presents the retained scene with new poses** (a motion
  present). A turn presents for the tick it woke for (read once, before any blocking); a publication landing after the
  decision waits for the next tick. With no motion live, a publish wake presents immediately. Every present first takes
  the present-slot credit (`IGpuDevice.TryTakePresentSlot(timeoutMs)`, before the frame is chosen; `false` = the slot
  did not open in time and no credit was taken; −1 = the backend's liveness-bounded wait).
- **A paced turn never queues behind a late frame** (`SlotCatchUp`, pure, render-thread owned). A clock-paced turn takes
  the credit with a grace of `SlotCatchUp.GraceFraction` = 0.15 of a refresh (`GraceMs`: 2 ms at 120 Hz). A slot still
  busy after the grace means the previous present missed its vblank and owns this one: the latency semaphore re-signals
  only after the NEXT vblank (+0.2–0.7 ms, measured 2026-09-29), so waiting would present this tick's frame one vblank
  late and every later turn would inherit that phase (runs of 55–86 turns one tick behind, with frames costing 1–3 ms).
  While frames fit `FitFraction` = 0.70 of the refresh (an EMA of render work + retired GPU ms, fed by every presented
  paced turn; the wake lag and slot wait are excluded — in a late run they ARE the lateness, and counting the wake
  lag kept the policy refusing exactly the catch-ups it exists for: live 2026-09-29, costEma 7.6–10.9 ms at GPU ≈ 2.3 ms) the turn presents NOTHING — the tick is not marked presented, so `MotionTickRun` charges it at the next
  present — and the next tick presents on time (Chromium viz "swap throttled"; Gecko "too many pending frames").
  Over-budget frames keep the unbounded wait and `PresentQueueDepthPolicy` owns them (its 0.8 engage sits above the
  0.70 fit), so the half-rate cliff stays closed. A catch-up followed by a busy slot within `HoldTicks` = 2 did not hold:
  the policy backs off for `BackoffTicks` = 120; motion ending clears the phase history (`Break`, the cost EMA is kept).
  Unpaced turns (no motion, or no clock) keep the unbounded wait. A wake that re-runs the turn on a tick already given to
  the late frame (a UI publication landing mid-tick) skips again uncounted, with no slot take and no policy call — a
  retake would be charged as a catch-up that did not hold, or open the slot just past the next vblank (the late phase
  again). The grace is bounded by a high-resolution waitable timer beside the latency waitable (`D3D12Device.
  TryTakePresentSlot`): a plain 2 ms wait timeout rounds to the process timer resolution (15.6 ms by default), which
  would swallow the ~8.5 ms retire the grace exists to detect. It is not a throttle (principle 6): still one present
  per tick — it refuses to queue a frame that could only land late. Counted: `RenderThread.CatchUpSkips` →
  `RenderPaceSnapshot.CatchUpSkips` / `FrameStats.RenderCatchUpSkips`, `[render.pace] catchUp=` (§10.6).
- **The render turn never waits for the UI.** A late UI frame delays new *content* by a tick; scroll poses keep their tick,
  and the coverage clamp means a late frame can never show a blank row. (The handshake that blocked the decision for the
  UI's in-flight frame is deleted.)
- **Missed ticks** are counted by `MotionTickRun` (a paced present for tick n after one for tick m in the same live run
  charges `n − m − 1`; idle stretches are never charged) → `RenderThread.MissedMotionTicks` and the probe's `Turn` row.
- **`PresentQueueDepthPolicy`**: DXGI max frame latency 1 while the GPU has margin; 2 once the GPU-execution EMA reaches
  `EngageFraction` = 0.8 of the refresh for `ConfirmSamples` = 8 fresh samples; back to 1 below `ReleaseFraction` = 0.6.
  The present-time prediction follows the depth in force (`[render.depth]` line on a change).
- **The tick lattice** (`CompositorTickFilter`, the pure filter inside `Win32CompositorClock`): a return closer than half
  the measured period to the last tick is a double tick (ignored, never a latch-off); a failed wait synthesizes one tick
  on the lattice; accepted stamps snap to `prev + period` within 2 ms and resync beyond (or after a > 2-period gap); the
  period is the median of 9 deltas; a window whose monitor is ≥ 1.25× slower than the compositor beat is decimated onto
  its own period (mode flips need 2 agreeing ticks).
- **Governor exemption:** the adaptive-GPU governor may pace ambient work to a steady rate when the GPU cannot sustain
  the panel, but `GpuGovernorWake.NeverPace` includes `WakeReasons.ScrollAnim` and `ScrollProducer` (a live DM contact or
  hi-res gesture before its first plan moves): scroll is never paced. A background window's inactive throttle never
  touches input/scroll frames either.

---

## 9. Rendering (link-only)

A scroll content root is a `SliceKind.Scroll` slice recorded **pose-free**; sticky/parallax roots are translation-only
effect slices; the overlay thumb is a `SliceRole.Thumb` slice; a virtual list's rows are an item `Band` (its
`ItemClipTopInset` clip and feather ride the band's MARKER and apply at the live pose, so the rows record against the
content's own clip, never that viewport-fixed line — a record-time cull there lost every row above a re-centred arrange
origin, `gate.scroll.item-band-deep-rows`); a `StickyClip`
node's band-line clip is a composite-time clip on its slice (0 bytes, 0 tiles per page scroll). Poses are
composite parameters, so **a pure scroll tick records 0 bytes and re-rasters only newly exposed or invalid tiles**; the
needed set is visible tiles first, then an ahead-of-motion band of `|v|·MotionFeel.LookaheadS`, then one row behind; a
main-axis row outside realized coverage is never requested (it agrees with the poser's clamp); a slice that cannot fit
its visible tiles degrades to direct raster, never blank. A viewport's `AutoEdgeFade` (a fade over no paint of the
viewport's own) composites as an analytic feather DISTRIBUTED onto the items below it — no group surface, a nested
shelf's feather riding the page's offset — so a page scroll stays composite-only at the cost of a feather multiply; a
Fill on the scroller itself makes the fade a (retained, content-keyed) group instead (author the background on the
PARENT). **The edge cue IS that feather**: `ScrollEdgeCues.Fade` (the default for a scroller that authors no fade of its
own) resolves to `AutoEdgeFade` with the standard 40-DIP band (`ScrollEdgeCueResolver`) — the content dissolves into
whatever lies behind it; no gradient is painted. The scroller's CHROME (scrollbar rail, arrows, thumb; the
`FadeAndChevron` chevrons) is drawn OVER the feather, never under it, so a visible thumb inside the band does not stop
the fade distributing. The feather's per-pixel evaluation runs only in its bands (`FeatherQuadSplit`). All of that — `SliceTable`, `TileGrid`, `InvalidationReason`,
`TileBudget`, `EdgeFeatherMask`, `TileCensus` (`ExposedMissing` and `CoverageClamps` must be 0), the composite pass, the
whole-frame present — is owned by **[`gpu-renderer.md` §13.1](./gpu-renderer.md)** (partition: `scene-memory.md`
§4.3b; uploads/`UploadBudget`: §13.1f).

---

## 10. Diagnostics — `Scroll/Diag/`

### 10.1 `ScrollProbe`

Two fixed 8192-slot POD rings, each **single-producer**: the **UI ring** (`Input`, `Plan`, `Coverage`, `Extent`, `Cost`,
`Mark`, `Present`) and the **render ring** (`Pose` — the render poser only — `RenderCost`, `Turn`). `ScrollProbe.Level`
(`ProbeLevel`, a volatile byte, any thread):

| Level | Records |
|---|---|
| `Off` | nothing — each record call is one volatile load + a branch |
| `Summary` | notches (`Input` with `PhaseNotch`) — of every wheel source, hi-res included — every `Pose`, non-anchored `Extent` jumps, `Cost`/`RenderCost`, `Mark`, `Present`, `Turn`, `TurnCost` — what `BurstSummary` and a session need |
| `Trace` | everything: every input sample, every authored `Plan` (with its anchor time), `Coverage`, anchored extents |

Row kinds (`ProbeRowKind`, drained as public POD `ProbeRow` by `ReadUi`/`ReadRender(fromCount, span, out next)` —
incremental, zero-alloc, torn-read-guarded): `Input` (source, phase, dx/dy), `Plan` (seq, kind, start, dest, duration),
`Pose` (present QPC, shown pos, vel, clamped, snapped translate, the plan's pre-clamp pos), `Coverage` (the published
`ScrollCoverageRow` — start, end, origin, first, last — recorded by `AppHost.FillScrollCoverage` when a viewport's
coverage changes), `Extent` (index, delta, anchored-same-frame, `ProbeExtentCause` Measured / FrameShift / Structural), `Cost` (`ScrollCostPhase` Input/Plan/Virtualize/Layout/Poser/Record + render-side
`TileRaster` rows = tiles rastered / `Composite` nodes = exposed-missing), `Mark` (`ProbeMark`: `BurstBoundary`,
`ProfileApplied`, `TunableChanged`, `UserFelt`, `AbFlip`, `AbVote`, `SessionStart`, `SessionEnd`), `Present` (UI:
the CUMULATIVE paired-counter present ledger, DWM deltas + sample identity, latency wait, refresh interval, frame ms),
`Turn` (render: one per present — tick QPC + seq, missed ticks charged, wake lag, present-slot wait, work ms, fresh vs
motion), and `TurnCost` (render: after each `Turn`, same tick — record / build / submit / retired-GPU ms, tiles + KiB
rastered, slices walked, items, the flags `compositeOnly` / `keptAll` / `skipSubmit` / `capture`, and the recorder
pass frame `walks.tsv` joins on; recorded at Summary and Trace).

### 10.2 `BurstSummary`

`ScrollProbe.EndBurst(qpc)` consumes both rings since the previous call (off the hot path, allocates): notches, presents,
coverage clamps, extent jumps, per-viewport jitter `σ(Δ²p)/mean|Δp|`, late presents (gap > 1.5× median), cost avg/max
overall and per phase, tiles per turn, exposed-missing tiles. `ScrollVerdict` worst-first: **`Clamped` > `Jumped` >
`Late` > `Uneven` (jitter > 0.20) > `Smooth`**. `FormatLine()` is the one-line log form (Wavee's `scroll.burst`).

### 10.3 CSV

`ScrollProbe.ExportCsv(writer, qpcFreq, refreshHz, dpi)` (the live rings — Wavee's Diagnostics ▸ Scroll ▸ Export CSV)
drains both rings and writes the same file as the row-based overload (a recorded session; optionally one viewport). The
format is `wheel-curve-probe/analyze.py`'s, schema `ScrollProbe.CsvSchema` = **4** (3: `turn_cost`; 4: a touchpad `raw_wheel`
row's note names its contact phase and the End's release — `phase=begin|sample|end[;release=unknown|moving|stopped]` —
so a zero-delta Begin/End row is never read as a sample): `# display_refresh_hz/qpc_frequency/
dpi_scale/mode=wavee/scroll_probe_schema` + `# ui_span_ms`, `# render_span_ms` (each marked wrapped/complete),
`# window_ms`, `# window_dropped`, then `qpc_ms,pane,kind,offset,delta,rendering_time_ms,device,note,vp` — analyze.py's
eight columns first (`pane=Wavee`), the viewport node id LAST (empty on input — recorded before routing; the next plan row
names the viewport — presents, turns, markers). Both rings are merged in time order. Kinds: `frame` (pose; note
`vel;trans;plan;clamped`), `state_changed` (a plan: offset = start, delta = dest − start, note
`plan_kind;kind;seq;start;dest;dur_s`; or `coverage_clamp;plan;shown`), `coverage` (note `start;end;origin;first;last`),
`view_changed` (note `extent;anchored;cause;index`), `notch`, `raw_wheel`, `present`, `turn`, `turn_cost` (schema 3: a
`Turn` row's `TurnCost` sibling; note `tick;record_ms;build_ms;submit_ms;gpu_ms;pass;tiles;kib;walked;items;
composite_only;kept_all;skip;capture`; `rendering_time_ms` = record + build + submit), `marker`, and `scenario_start`
(session overload). **Input rows are timed at arrival:** the probe's `Input` row records `ScrollInputEvent.ArrivalQpc`
(0 ⇒ `Qpc`), so a touchpad `raw_wheel`/`notch` row's `qpc_ms` is when the producer observed the event, not a
DirectManipulation event's present stamp (§4.4) — the column set is unchanged, so the schema is not bumped.
**Time-aligned window:** the live rings wrap independently, so rows older than the latest first row of a ring that
WRAPPED are dropped (before that instant only one ring speaks); a complete ring clips nothing. Pure
tests: `ScrollProbeTests.Csv_*`; end to end: `gate.scroll.probe-export-chain` (asserts schema 4).

### 10.4 Present ledger semantics

`PresentStatisticsLedger` differences **only DXGI's paired counters** (`PresentCount` with `PresentRefreshCount`)
between two samples: `Dropped = max(0, ΔPresent − ΔRefresh)`, `Displayed = ΔPresent − Dropped`, `Repeated = max(0,
ΔRefresh − ΔPresent − idle)`. A sample lags the submit by about a flip, so comparing it to the engine's own submit count
invents drops; idle vblanks (nothing newer existed, or the producer skipped a tick — that is `MissedMotionTicks`) are
banked per present id and credited when the retiring sample arrives. A disjoint sample or one before the first displayed
present resets the baseline. Rows carry cumulative counters — a reader **differences consecutive rows**, and a delta
spanning an idle stretch is not a motion sample.

### 10.5 Analysis, metrics and the lab

`Scroll/Diag/Analysis/SessionSeries.FromRows(ui, render, originQpc, …)` builds one viewport's series (inputs, poses,
presents, turns, plans, markers, pacing) on session seconds; `ScrollMetrics` is the catalog of pure metrics with
green/amber/red verdicts against the `Standard` reference — first-motion latency, notch→pose lag, displacement
irregularity, attested repeats/drops, missed-tick attribution, per-notch curve shape + t50/t90, DIP per notch, sustained
spin speed, stop latency/distance, decay rate. Latency metrics measure from the input's arrival (the `Input` row's
`ArrivalQpc`, else the device stamp — a touchpad `FirstMotionLatency` under-read by ~8 ms before 2026-09-29);
curve metrics from the authored plan's anchor (Trace recordings). Thresholds and the complaint each maps to: `docs/guide/scroll-lab.md`.
The Scroll Lab (`src/FluentGpu.ScrollLab` + `FluentGpu.ScrollLab.Surfaces`) records sessions, live-tunes `MotionFeel`
and renders the verdicts — same guide.

### 10.6 Other always-on instruments

- `FrameStats.RenderCensus` (`RenderFrameCensus`, always-on counters) and the per-component census
  (`AppHost.RenderCensus` runtime toggle → `FrameStats.Census`, the `[render-census]` line); `AppHost.LastTileCensus`.
- GPU pass timing: `AppHost.GpuPassTimingEnabled` (runtime) / `--fg gpu-timing` (launch) →
  `AppHost.CopyGpuPassTimeline` (zero-alloc) with `GpuPassKind` `Uploads`/`BakedBlur`/`Clear`/`Scene`/`GlyphBand`/
  `TileRaster`/`Offscreen`/`Composite`; knockouts in `GpuKnockouts` (`Seams/Rhi/GpuFrameTelemetry.cs`).
- **`[render.pace]`** (render thread, once a second while motion is live): `tick=` (+ticks in window), `fresh=`,
  `motion=`, `skipped=`, `race=`, `missed=` (missed motion ticks), `slotWaitAvg=`/`slotWaitMax=`, `presentLagMax=`,
  `clockPeriod=`, `ignored=` (double ticks), `slotDrops=`, `decimating=`, `depth=`, `governorEma=`, `governor=`,
  `wait=` (the UI loop's last wait kind), `gpuMs=`, `worst(lag= wake= slot= work= run= …)` — the worst present of
  the window split into its wake lateness, slot wait, turn work and the render thread's own CPU run time inside it
  (work without run = blocked in present/fence) — then `catchUp=` (paced turns that presented nothing because the slot
  was still busy past the grace, §8), `costEma=` (the `SlotCatchUp` frame-cost EMA, ms) and `backoff=` (1 while a
  catch-up that did not hold is backing off).
- **`FrameStats.UiGap`** (`UiGapClassifier`, pure): each UI frame's gap since the last one decomposed into the loop's
  requested and blocked waits, messages, input, posted work, cold starts and run/blocked/not-scheduled thread time
  (`ThreadCycles`), with a verdict (`Busy`/`Blocked`/…); apps print it on their slow-frame line.
- **`[scroll.engaged]` / `[scroll.engaged.present]`** (`EngagedCrossings`, `PresentLedger`): the render tick a sticky
  row's engaged edge crossed, the UI frame that flipped its signal, and how many ticks / ms later the first present
  carried it (`ticksAfterCross`, `msAfterCross`).
- `ScrollProbe.ClampedPoses` / `LastClamp` (always on): the count of coverage-clamped poses and the last one's
  viewport, shown and planned offsets — an app's auto evidence capture keys on its delta.
- **`[scroll.jump]`** + `ScrollProbe.Jumps` / `LastJump` (always on; `ScrollJumpRules`, pure; `AppHost.WatchScrollJumps`
  after the UI pose): a viewport AT REST (no user input for `AtRestS` = 0.25 s, a settled plan, on this frame and the
  last) whose anchor row moved on screen (`OffsetOf(anchor) − shown`) by more than `ThresholdDip` = 1 DIP with no new
  programmatic plan in between. The line names the viewport's `ScrollKey`, from/to, the displacement, the cause
  (`extent` — unanchored content change above; `shiftframe` — a frame shift the shown offset did not follow; `coverage`
  — the shown offset bounded away from the plan; `plan` — a re-written plan; `unknown`), the extent before/after, the
  anchor, the realized range and the frame's census `Type:by` pairs; formatted off-thread (the `[tiles.stale]` pattern).
  An anchored correction moves both terms and is never a jump. Evidence only. Gates: `ScrollJumpRulesTests.*`,
  `gate.scroll.reseed-anchored`.
- `--fg` switches (`Hosting/EngineSwitches.cs`): `diag`, `fps`, `alloc`, `alloc-types`, `mem[=N]`, `resize`, `motion`,
  `layout`, `layout-overflow`, `layout-verify`, `render`, `img=FILTER`, `d3d-mem`, `nc`, `dump=MODE`, `shelf`, `morph`,
  `no-guards`, `guards-throw`, `device-lost=N`, `opaque`, `no-precise-wait`, `no-vsync`, `gpu-timing`.

---

## 11. Invariants (each names its gate)

VerticalSlice gates run with `dotnet run --project src/FluentGpu.VerticalSlice -- --suite scroll` (tag `scroll` =
`ScrollSuite` + `ScrollMotionSuite` + `ScrollEffectsSuite`), `--suite tiles` (`TileSuite` + `SliceSuite`), `--suite touch`,
`--suite listrow`. Unit tests live in `src/FluentGpu.Engine.Tests` and `src/FluentGpu.Windows.Tests`.

1. **Motion is a pure function of absolute time; sampling order and rate are irrelevant.** —
   `ScrollMotionTests.Eval_AtSharedTimes_IsIdenticalRegardlessOfSampleOrder`, `Eval_IsContinuousAcrossSegmentBoundaries`,
   `SnapFling_LandsExactlyOnTheGrid_InFiniteTime_AtAnySamplingRate`; `gate.touch4.snap-fling-dt-invariant`,
   `gate.snap.page-glide-dt-invariant`.
2. **Only `PlanAuthor` builds moving plans; only the UI thread writes `PlanSlots`; readers never tear.** —
   `ScrollRuntimeTests.PlanSlots_*` (incl. `_Seqlock_NeverTearsUnderConcurrentWriter`, `_StaleGeneration_DoesNotReadNewViewportsPlan`).
3. **Never blank: the shown position is clamped to realized coverage, and the realized window always covers the visible
   band; coverage describes what is built (a persistent prefix is covered).** —
   `Poser_CoverageClamp_NeverShowsOutsideWindow_AndRecordsClamp`, `Poser_OverpanAtContentStart_IsNotAClamp`,
   `Coverage_APersistentPrefixContiguousWithTheWindow_CoversFromTheContentStart`,
   `Poser_APrefixedListAtRest_IsNotClampedByItsOwnPrefix`; `gate.scroll.no-blank`, `gate.scroll.prefix-coverage`; `gate.tiles.coverage-clamp`, `gate.tiles.no-blank-8000`, `gate.tiles.exposed-only`,
   `gate.tiles.budget-never-drops-visible`.
4. **Never jumps on measurement: a correction above the anchor shifts the plan frame in the same call.** —
   `Virtualizer_ApplyMeasured_AboveAnchor_KeepsAnchorRowScreenPositionUnchanged`, `PlanSlots_Shift_PreservesEvalMinusDelta`,
   `Shifted_TranslatesEvalByDelta`, `Poser_PlanShiftNewerThanItsCoverage_PosesInTheCoveragesFrame_UntilTheNextAdopt`;
   `gate.scroll.anchor-holds`, `gate.scroll.extent-stable`,
   `gate.scroll.explicit-measured-correction-{wheel,programmatic,direct-touch}`, `gate.scroll.anchor-repin-under-gesture`.
5. **Never jumps on input: every re-plan starts at what is shown and never behind the pose floor.** —
   `WheelNotch_StartsExactlyAtTheDisplayedPosition_NoJump`, `WheelNotch_StampedBeforeTheLastPosedPresent_HasNoCatchUpStep`,
   `WheelNotch_ReversalStampedBeforeTheLastPosedPresent_NeverStepsBack`, `Glide_IsVelocityContinuousFromTheHandoff`,
   `Handle_Stop_HoldsWhereTheContentWasShown_NeverStepsBack`, `Handle_ContactBeginHere_GrabsTheShownPosition`,
   `Handle_LiftDetectedAfterSilence_FlingsWithoutAJump`, `Handle_ContactSamplesAheadOfThePlanClock_ResyncKeepsTheDeviceSpacing`,
   `Handle_PresentClockContact_KeepsItsPresentStamps_AndTheLiftNeverRewinds`.
6. **Deep offsets are exact (double plans, float only relative to the window origin).** — `gate.scroll.precision-deep`,
   `gate.scroll.bring-node-deep`, `Eval_AtListMagnitudeOffsets_IsSmoothToWithinOneMicroDip`,
   `Virtualizer_ArrangeOrigin_KeptAcrossWindowShifts_RecentresBeforePrecisionIsAtRisk`.
7. **One snap: effects share the content's pixel grid.** — `Poser_StickyEffect_SitsOnContentPixelGrid_BitExact`;
   `gate.scroll.sticky-grid`.
8. **A wheel notch shows on the very next present and travels exactly `WheelNotchDip`.** — `gate.scroll.notch-to-present`;
   `WheelNotch_FastSpinAccumulates_EveryNotchTravelsItsFullDistance`, `WheelNotch_Reversal_*`, `WheelNotch_AfterTheGlideSettled_*`.
   A same-direction re-plan keeps the velocity on screen (C1), and the owner's fast spins travel what the recorded 32 DIP
   model did (±15 %). — `WheelFeelTraceTests.*` (owner traces + cadence sweep), `WheelNotch_StartsExactlyAtTheDisplayedPosition_NoJump`.
9. **A hi-res wheel is fractional notches: exactly the turned distance, no lift, no coast.** — `gate.scroll.hires-burst`;
   `WheelNotch_HiResPackets_TravelExactlyTheTurnedDistance`; `WheelClassifier_*`.
10. **Contacts predict at most once and release honestly: a composition-timed stream is stamped with the present of the
    first render turn that sees it and never re-predicted, a device stream only by bounded resampling; release velocity
    is LSQ over a real span; a stopped finger never flings.** — `ContactStampTests.*`,
    `TouchpadStampRaceTests.RenderTick_ShowsTheSameSample_WhetherThisTicksSampleLandedBeforeOrAfterItsRead`,
    `gate.touchpad.stamp-race`,
    `Follow_CoincidentPacketPair_DoesNotExtrapolateAtThePairSlope`, `ContactRing_Velocity_OfBunchedPairs_IsTheStreamRate`,
    `FollowEnd_FingerStoppedBeforeTheLift_NeverFlings`, `FollowEnd_ReleaseVelocity_IgnoresACoincidentPairWithNoSpanBehindIt`,
    `FollowEnd_LateLiftAfterACoincidentPair_StartsNearTheStream`,
    `Handle_PresentClockContact_KeepsItsPresentStamps_AndTheLiftNeverRewinds`. A DirectManipulation lift is judged by
    DM's verdict from the newest REAL sample, and DM's READY snap is never a sample. — `DmContactStreamTests.*`
    (Windows.Tests, real per-Update scripts), `FollowEnd_PresentClockMovingRelease_FlingsFromTheNewestRealSample_WithoutAJump`,
    `FollowEnd_StoppedRelease_HoldsWhereTheContactShows`, `FollowEnd_DmReadySnapAsTheNewestSample_HoldsAFastFlick_TheDefectShape`,
    `TouchpadReleaseDispatchTests.*`.
11. **Edges are solved at authoring, never discovered after layout.** — `FlingCrossingAnEdge_RubberBand_ProducesSpring_EndingAtEdge`,
    `FlingCrossingAnEdge_None_ProducesHold_EndingAtEdge`, `FollowEnd_LiftedWhileOverpanned_*`, `FollowCancel_Overpanned_*`,
    `RubberBand_IsMonotoneAndBoundedByTheViewport`; `gate.touch4.overscroll-springback`, `gate.touch4.wheel-hard-clamps-no-band`.
12. **Routing latches per gesture and chains only from a gesture that began at the edge.** — `Router_*` tests;
    `gate.scroll.nested-latch`, `gate.scroll.zero-overflow-latch`.
13. **Zero managed allocation per frame/tick on the scroll path.** — `gate.scroll.100k-flat-zero-alloc`,
    `gate.tiles.alloc-zero`, `gate.tiles.render-alloc-zero`, `gate.touch.fling-alloc-steady-zero`, `gate.listrow.zero-alloc-on-text-change`.
14. **A pure scroll tick records 0 bytes; retained tiles are pixel-identical to a direct raster.** —
    `gate.slices.scroll-tick-zero-bytes`, `gate.tiles.scroll-no-copy`; pixels `tile-scroll-identity` /
    `tile-static-identity` (`FluentGpu.WindowsApp --repaint-identity`).
15. **Scroll effects are bit-identical on both threads and follow the engaged edge exactly once per crossing; only a
    pinned `Sticky` re-orders paint, a `StickyClip` never does.** — `ScrollEffectTests`
    (`PaintOrder_OnlyAStickyPinLiftsItsNodeAboveItsSiblings_AStickyClipNeverDoes`), `ScrollLinkedEffectTests`;
    `gate.scroll-effects.stickyclip-paint-order`, `gate.scroll-effects.engaged-edge`, `.collapse-monotone`,
    `.collapse-trailing`, `.collapse-hit`, `.stretch-from-top`, `.use-scroll`, `.measure-all`.
16. **One present per tick; a fresh publication wins; the turn never waits for the UI; only live-motion ticks are charged
    as missed.** — `RenderThreadLifecycleTests.AtMostOnePresentPerCompositorTick`, `FreshPublicationWinsOverMotionOnTheSameTick`,
    `LatePublicationWaitsForTheNextTick_AndThenWins`, `WithoutMotion_PublishWakePresentsImmediatelyRegardlessOfTheTick`,
    `PresentStatisticsLedgerTests.MotionTickRunChargesOnlyTicksSkippedWhileMotionIsLive`. A late frame costs one vblank:
    a paced turn whose slot is still busy past the grace presents nothing while frames fit, the next tick presents on
    time, and over-budget frames never catch up (full rate kept). — `SlotCatchUpTests.*`,
    `RenderThreadPacingTests.ALateFrameCostsOneVblank_ThenTheLoopIsBackInTheEarlyPhase`,
    `RenderThreadPacingTests.ChronicallyOverBudgetFrames_NeverCatchUp_AndKeepFullRate`,
    `RenderThreadPacingTests.ACatchUpThatDoesNotHold_BacksOff`,
    `RenderThreadLifecycleTests.APacedTurnWhoseSlotStaysBusy_PresentsNothing_AndTheNextTickPresents`.
17. **Scroll is never paced by the GPU governor.** — `GpuGovernorWakeTests.AScrollProducerFrameIsNeverPaced`,
    `AmbientWorkAloneIsPaceable_AndEveryInteractionBitExemptsTheFrame`.
18. **The present ledger compares paired counters only.** — `PresentStatisticsLedgerTests.StatsLaggingOnePresentIsNotADrop`,
    `IdleVblanksAreCreditedWhenTheLaggingSampleShowsThePresent`, `ALateLatchIsOneRepeat`, `TwoPresentsOnOneVblankIsOneDrop`.
19. **The tick lattice is stable under bursts, failures and decimation.** — `CompositorTickFilterTests.*`;
    `RefreshLatticeTests.*`; `PresentQueueDepthPolicyTests.*`.
20. **Parked viewports cost nothing; settled re-poses cause no damage.** — `ScrollParkedViewportTests`,
    `ScrollPoseDamageTests`.
21. **The probe is single-producer per ring and free when Off.** — `ScrollProbeTests.Off_RecordsNothing`,
    `TheRenderPoserIsTheRenderRingsOneProducer_OnePoseRowPerViewportTick`, `AUiPoserTickRecordsNoPoseRow`,
    `Summary_SkipsAnchoredExtentAndNonNotchInput`; metrics `ScrollMetricsTests.*`.

---

## 12. Gate catalogue

| Gate / test | Proves |
|---|---|
| `gate.scroll.no-blank` | the drawn viewport of a 100k-row list is fully covered on every frame of a fast touchpad fling, a 60-notch storm and a reverse fling |
| `gate.scroll.extent-stable` | a measured list corrected mid-motion publishes the layout total as the handle's extent on the same frame; extent only grows as estimates realize |
| `gate.scroll.anchor-holds` | an upward drag realizing estimate-priced rows above tracks the finger 1:1 (≤ 1 device px) |
| `gate.scroll.precision-deep` | 4M DIP deep, 0.37-DIP steps land exactly; one snapped content translation |
| `gate.scroll.bring-node-deep` | node-level `BringIntoView` 4M DIP deep lands exactly through the arrange origin |
| `gate.scroll.100k-flat-zero-alloc` | warm fling over 100k flat rows: 0 bytes in hot phases, zero templates run (slots recycle) |
| `gate.scroll.notch-to-present` | a notch authors synchronously and moves on the very next present; monotone; lands one `WheelNotchDip` in the wheel duration |
| `gate.scroll.hires-burst` | 12 × −40 raw → exactly 4 notches, stops, no reversal, steps ≤ 2× steady |
| `gate.touchpad.stamp-race` (`ScrollMotionSuite`) | a touchpad drag poses the same sequence whether the render read lands before or after the UI's DM write each tick; one sample per frame |
| `gate.scroll.sticky-grid` | pinned header never drifts vs content at 100–175 %, pinned edge on the device grid |
| `gate.scroll.prefix-coverage` | a bound list with a persistent prefix (hero + chrome; a rail head) shows 0 at rest unclamped, realizes viewport + overscan, moves on the first notch, and again after a deep scroll and back |
| `gate.scroll.reseed-anchored` | a wholesale reseed of a measured layout (the V3 rail shape: head seeded 101, measuring 287) never moves the rows on screen; the always-on jump detector counts a bare `ShiftFrame` at rest (cause `shiftframe`) |
| `gate.scroll.probe-export-chain` | the schema-4 CSV of a real host names the viewport on plan/coverage rows; a fast spin's plan chain is continuous (the next burst starts at the previous destination) |
| `gate.hooks.scroll-threshold` (`Suites/ScrollThresholdChecks.cs`) | `UseScrollThreshold` flips only at the enter/exit crossings (never inside the dead band, never once per scrolled frame) and scrolling steady-state inside the band allocates 0 bytes |
| `gate.scroll-effects.stickyclip-paint-order` | an engaged StickyClip keeps its sibling paint order; a pinned Sticky paints after the sibling it pins over |
| `gate.scroll.engaged-feather-composite` (`EngagedFeatherSuite`) | a `WhileStuck` edge fade feathers nothing at rest and feathers the sticky cut in the FIRST composited frame that cuts |
| `gate.scroll.item-band-deep-rows` (`ItemBandDeepSuite`) | after a jump deep into a prefixed, item-band-clipped list (arrange origin re-centred) the composited rows cover the whole band below the inset |
| `VirtualLayoutExtentLeadInsetTests` (Engine.Tests) | `OffsetOf(i)` is item i's layout rect for every i, item 0 included (a lead inset is honoured) |
| `gate.scroll.nested-latch` | contact latches the inner scroller for its life; a gesture beginning at the pinned inner chains to the outer |
| `gate.scroll-effects.*` | engaged edge exactness, collapse monotone / trailing / hit-testing, stretch composition, `UseScroll`/`UseScrollProgress`, `MeasureAll` exact targets |
| `gate.tiles.*`, `gate.slices.*` | retained-tile contracts — owned and described in `gpu-renderer.md` §13.1k |
| `gate.listrow.*` | `ListRowEl` placeholder keeps geometry; a text change allocates 0 |
| `gate.touch*.` / `gate.arena.*` (scroll-relevant) | touch pan vs tap, fling settle and snap, overscroll spring-back, thumb drag, wheel hard clamps |
| `ScrollMotionTests`, `ScrollRuntimeTests`, `ExtentSourceTests`, `ScrollEffectTests`, `ScrollLinkedEffectTests`, `ScrollProbeTests`, `ScrollMetricsTests`, `ScrollParkedViewportTests`, `ScrollPoseDamageTests`, `ContactStampTests`, `TouchpadStampRaceTests`, `RenderThreadLifecycleTests`, `RenderThreadPacingTests`, `SlotCatchUpTests`, `PresentQueueDepthPolicyTests`, `PresentStatisticsLedgerTests`, `RefreshLatticeTests`, `GpuGovernorWakeTests` (Engine.Tests); `CompositorTickFilterTests`, `RenderDisplayClockTests` (Windows.Tests) | the pure contracts above |
| `gate.slices.stickyclip-composite`, `stickyclip-cuts-at-line` | a page scroll under a StickyClip band is composite-only (0 bytes, 0 tiles, its group a cache hit); the composite clip cuts at the exact band line |
| `gate.slices.edge-cue-is-feather`, `chrome-over-feather`, `slice-root-layer` | the default edge cue is the analytic feather on the content item (no painted band); a visible thumb inside the band leaves the fade distributed and the thumb unfeathered; a fading ROOT scroller cuts its fade as a `Layer` slice (no fold) |
| `ScrollEdgeCueResolverTests`, `FeatherQuadSplitTests` (Engine.Tests) | `Fade` → `AutoEdgeFade` + 40-DIP band unless the element authors its own fade; the feather is exactly 1 in its unit interior and the split partitions the quad |
| `--repaint-identity` (`FluentGpu.WindowsApp`) | `tile-static-identity`, `tile-scroll-identity`, `tile-feather-identity` (+ `/product`), `tile-acrylic-budget-identity`, `fade-distribute-identity`, `scroll-edge-identity`, `scroll-chrome-identity`, `stickyclip-identity` (`/group`, `/distributed`), `group-cache-identity` — pixels |
| `--scroll-bench` / `--scroll-soak` (`FluentGpu.WindowsApp`, `Probes/ScrollPerfProbes.cs`) | measurement, not pass/fail: per-frame GPU pass cost at a constant velocity; the sustained-touchpad soak's per-second pacing/memory/thermal lines |

---

## 13. How to change X safely

- **A feel value** — change `FeelProfiles` data (or add a profile), never a literal in an author or the poser. Tune it
  live first (Scroll Lab tuning pane / Wavee Diagnostics ▸ Scroll), record a session before and after, and let
  `ScrollMetrics` judge. Adding a `MotionFeel` field means: the record field, every `FeelProfiles` preset, a `TunableF`
  row in `ScrollTunables.BuildAll`, the JSON round-trip (`Tunables_All_CoversEveryDoubleFieldWithItsDefault`
  enforces it), and reading it only at author time.
- **The wheel curve** — `PlanAuthor.WheelSeg`/`WheelNotch` + `SegKind.Cubic` + `ScrollMetrics.ReferenceCurve` move
  together (the reference curve IS the default feel's notch from rest). Justify a change from recorded notch traces
  (`WheelFeelTraceTests`, the simulation in `C:\WAVEE\wheel-curve-probe\reports\wheel-snappier-2026-09-25`), not
  taste. Keep the anchor rule (`max(tNotch, floor)`), the accumulate/re-base rule and C1 re-plans.
- **A new input source** — emit `ScrollInputEvent`s (Begin/Sample/End or Notch) with a real device stamp; route it
  through `InputDispatcher.DispatchScroll`; never write a plan or an offset from a producer. Add the classifier rule to
  `WheelClassifier` if it arrives as wheel packets, with a `WheelClassifier_*` test.
- **A new effect channel** — `EffectChannel` + the `ScrollEffectEval` formula + a `ScrollEffect` factory + a DSL
  method; if it is transform-class add it to `IsTransformChannel` and `EffectTransform.Add`; otherwise handle it in
  `AppHost.ApplyEffectChannel` (both sinks share it) and remember a non-transform channel makes the render turn record
  (`SnapshotScrollPoseSink.RecordRequired`). Gate both threads' agreement.
- **Virtualization behaviour** — through `Virtualizer`/`IExtentSource`/`NeedsRealize` only; any "realize fewer rows this
  frame" idea is a throttle (§14) — make the row cheaper instead.
- **Pacing** — `PresentCadence`, `MotionTickRun`, `SlotCatchUp`, `PresentQueueDepthPolicy`, `CompositorTickFilter`,
  `GpuGovernorWake` are pure and unit-tested; change the pure class and its tests, then measure with `--scroll-soak` and
  a lab session. The present-time law and the pose floor are load-bearing for every curve metric.
- **Diagnostics** — a new probe row kind needs a `ProbeRowKind` ordinal, a single-producer ring choice, a level rule, the
  CSV mapping and `SessionSeries` ingestion; a new mark needs its `ProbeMark` code in the same change as its emitter.
- Every change: failing-first gate or unit test, Debug + Release build, the full VerticalSlice, and — for feel — a lab
  session compared against its baseline. Canon edits: this doc + `SPEC-INDEX.md` §2 + `check-canon.ps1`.

---

## 14. Deleted — do not reintroduce

| Deleted | Why it must not come back |
|---|---|
| `ScrollKernel` / `ScrollBody` / `ScrollPhysics` per-tick `dt` integration (and `ScrollKernelSuite`, `gate.kernel.*`) | integrated state is corrupted by any skipped, repeated or late tick; a closed form of absolute time cannot be |
| dt repair (`_prevScrollNowQpc` / `_scrollDtRepairs`) | a patch on the symptom of integration; with no `dt` there is nothing to repair |
| First-step caps (limiting how far a new gesture's first posed frame may move) | the pose floor already makes the first frame exactly one refresh of the curve; a cap distorts the curve and adds latency |
| `ColdRealizeRamp` / `StaggerColdRealize` / `LazyGridDrip` / fixed row `Overscan` | per-frame row caps show blank rows on a fast fling; cheap rows + velocity overscan realize the whole window |
| `FrameBudget` (`MotionUiSliceMs`) | a time slice that trades correctness (unrealized rows, late images) for frame time |
| Scroll-keyed holds/throttles (`AcrylicScrollHold`, `SelfBlurHoldAfterScrollTicks`, `DecodeScheduler.ScrollThrottled`, `AncestorScrollLive`, `PeekMainScrollBusy`) | freezing or starving work because "a scroll is live" makes content stale exactly while it is watched; refresh on real damage, bound uploads permanently |
| Strip edge fades | a canvas-era approximation; the analytic `EdgeFeatherMask` in the composite is the one feather |
| The painted edge cue (`TryResolveCueSurface`, the cue gradient, `EdgeCueFadeBit`) | a gradient guessed from an ancestor's colour drew a dark slab over translucent surfaces and at fractional edges; the cue's fade is the viewport's analytic feather |
| Recording a sticky band line into tiles (the `ClipTop` pose as a record-time `ClipRect`) | every page scroll under a pinned band re-recorded and re-rastered the clipped node and missed its group / backdrop caches; the band line is a composite-time clip |
| Persistent canvas RT / partial repaint / translated-copy scroll | a stateful canvas couples scroll to repaint correctness; retained tiles + pose-free slices make a scroll tick record nothing (`gpu-renderer.md` §13.1) |
| The production-wait handshake (render turn blocking for the UI's in-flight frame) | it held motion presents hostage to a late UI frame; the coverage clamp makes presenting without the UI safe |
| The OS-owned scroll source (`IScrollSource` / `ScrollSourceMux` / `Win32DmScrollSource`, DM inertia as the coast, the wedge ladder — `TRANSLATION_INERTIA` is configured today only so DM's release verdict shows; an INERTIA viewport is stopped at the next pump) | an OS physics owner bypasses the engine's clamp, virtualization and headless determinism |
| `ScrollBind` / `ScrollBindDsl` / named scroll timelines / `ScrollIntoView` / `ScrollController` / `OnScrollGeometryChanged` / `Hooks.UseScroll` channel | replaced by `ScrollEffect` rows, `ScrollHandle`, `SceneScrollExtensions` and `UseScroll`/`ScrollCtx` — one surface per job |
| `FlexLayout.PostAnchorShiftAndFrame` + its sub-pixel noise gate | two writers raced the offset; `Virtualizer.ApplyMeasured` is the one extent write |
| Reconciler `ScrollMemory` | scroll position is handle state (`ScrollHandle.Restore` + `ScrollPositionMemory`), not reconciler state |
| Environment-variable switches (`FG_SCROLLLOG`, `FG_DM_*`, scroll-probe and render-census variables) | an ambient variable changes a run nobody can see the cause of; use `ScrollProbe.Level`, `ScrollTunables`, runtime toggles and `--fg` |
| Duration or distance adaptation to input cadence | makes the curve a function of the device's packet rate; the notch is fixed and measured |
| Free LSQ extrapolation of a live contact (`FollowExtrapolateMaxS`) | a velocity estimate that changes between frames moves the shown position by `ΔV·lead` — against the finger whenever it drops; composition-timed streams hold, device streams use bounded resampling |
| Per-frame zero-delta contact samples (the DM "fingers resting" sample) | a sample with an unchanged position reads as a velocity collapse the finger never made; stoppedness is the producer's release verdict, or the `StoppedAfterS` time rule without one |
| DM's READY whole-pixel snap as a `Sample` | it arrives in the lift's Update BEFORE the status edge and was stamped at the End: a ~0 velocity that held fast flicks dead (`DmContactStream` — an Update that ends non-RUNNING carries no motion) |
| Stamping a composition-timed sample for the tick that reads it (`now + lead`) | the UI write races the render read on the same tick; the shown sample flipped between this tick's and the previous (+2/0 steps). `ContactStamp.ForFrame` stamps the next tick's present |
