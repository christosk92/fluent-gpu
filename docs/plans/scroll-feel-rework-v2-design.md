# Scroll Feel Rework v2 — ground-up redesign

Status: **DECIDED — supersedes `docs/plans/scroll-feel-rework-design.md` (2026-07-01) in full and amends `docs/plans/generic-hookable-scroll-engine-design.md` at the offset-write chokepoint.** This is a from-scratch replacement of the shipped scroll algorithm, not a patch series. It keeps the one rule the ScrollBind doc got right ("binds read post-physics, never re-integrate; one integrator") and discards everything the v1 rework left half-built (the `ScrollMode 0/1/2/3` split-ownership integrator, the per-event apply-at-dispatch pipeline, the soft-knee band, the Riemann fling, the dual velocity gate).

Where v1 was wrong and this spec reverses it:
- v1 §6 said "ScrollAnimator becomes an explicit 8-state enum" but shipped `ScrollMode` still `0/1/2/3` with gesture state in `InputDispatcher._sg*` and motion state in `ScrollAnimator` — **two offset writers coordinated by convention.** v2 makes the integrator the *single* offset writer and the dispatcher a pure intent recorder.
- v1 §2/§6 said "the ring = the per-frame resample" — **it is not; summing is not resampling.** v2 adds real frame-time resampling.
- v1 §7 kept `SpringOmega=42`, the soft-knee `BandFromExcess`, and the wheel `v·dt` Riemann fling. v2 replaces all three.
- v1 §5's "zero heuristics" fallback grew a trailing-32 ms gate within days. v2 collapses release-velocity to one window.

---

## 1. Diagnosis — confirmed root causes, ranked

**R1 — Two owners of the offset (the wrong decomposition).** `ScrollState.OffsetX/Y` is written synchronously mid-frame by the dispatcher (`SetScrollOffset`/`ApplyTouchPan`/`ScrollBy`, `InputDispatcher.cs:2868`, reached from `AccumulateContactDelta`/`ApplyMomentumDelta`/scrollbar drag) *and* by `ScrollAnimator.Tick` at phase 7 (`ScrollAnimator.cs:376`), reconciled only by a `ScrollMode` byte, a one-frame `ScrollMoved` pulse, two callbacks, and four hand-zeroing sites. Gesture state lives in `InputDispatcher._sg*`; motion state lives in the animator. Every prior fix edited one owner and silently broke a transition the other assumed. Until the offset has exactly one writer driven by one state enum, all constant-tuning is downstream of an ambiguous state machine and cannot converge. This is the parent defect; R2–R5 are unavoidable beneath it.

**R2 — Input consumed per-event, never resampled to frame time.** Contact/momentum deltas apply 1:1 the instant they dispatch (`AccumulateContactDelta`→`ApplyGestureOffset`, `InputDispatcher.cs:2670`); the ring only *sums* per frame (`Pal.cs:181`). A ~125 Hz PTP stream against a 60/120 Hz display alternates 1-packet and 2-packet frames, so per-frame displacement varies at constant finger speed — textbook rate-mismatch judder (Mozilla 970751; the exact thing Android `InputTransport`, Chromium `LinearResampling`, and Flutter's resampler all exist to kill). The v1 rework deleted the old 14 ms low-pass and replaced it with nothing. No decay/gain tuning removes a sampling-cadence alias.

**R3 — Fling coast uses a Riemann step, not the closed-form integral.** `ScrollAnimator.cs:277,308` decays `v` then advances `off += v·dt` — a post-decay rectangle rule. `OverscrollPhysics.CoastStep` (`OverscrollPhysics.cs:79`) already computes the exact `Δpos = v·(1−decay^dt)/k` and its own doc says the Riemann form is *not* frame-rate-independent — yet the fling integrator never calls it. Result: the same flick travels different distances on 60 Hz vs 120 Hz panels (~9 % short at 60 Hz for the wheel `k≈10.8/s`), and the wheel promise "one notch coasts one notch" is broken by construction.

**R4 — PTP is delivered by the wrong message and mis-classified.** The only scroll source in the WndProc is promoted `WM_POINTERWHEEL/HWHEEL` (`Win32Platform.cs:945`), the wrong message for touchpad gestures — no phase, no finger-lift, no device identity, no OS inertia. Touchpad-vs-mouse is then guessed from `notch % 120` (`Win32Platform.cs:957`), which mis-routes this hardware's exact-±120 PTP bursts onto the detented-mouse path (4–7× over-travel + a coast the finger never asked for), and lift is guessed from 80 ms of packet silence (`Win32Platform.cs:718`). Per MS Learn only DirectManipulation/InteractionTracker are "enlightened" for PTP; the repo's own `tools/dm-probe` already returned GO (13/13 `SetContact` S_OK, real `RUNNING→INERTIA` momentum) with `EnableMouseInPointer` ON. The fidelity ceiling is reachable and simply unbuilt.

**R5 — Overscroll physics read cheap and hit a wall.** The release spring is `ω=42 rad/s` critically damped (`OverscrollPhysics.cs:26`) — settle ≈95 ms, 2–3× faster than every shipping reference (WebKit λ=12.5, Android ω=24.7, Flutter ω≈14) — the "snappy vs native" complaint. The drag map `min(limit, soft·raw/(raw+soft))` (`OverscrollPhysics.cs:45`) saturates *exactly* at the cap with a slope discontinuity: past 20 % of viewport the content freezes dead under the moving fingers, and `ExcessFromBand`'s early-out (`:57`) is the wrong inverse at saturation, so re-grabbing a stretched edge snaps it back ~1/3. iOS's asymptotic map never stops responding and never reaches its bound.

**R6 — Momentum is binary and uncancellable.** The self-fling seed is gated on *both* a work-energy velocity and a bolted-on trailing-32 ms displacement velocity agreeing in sign (`InputDispatcher.cs:2563`), so driver-thinned final packets read zero and the pan bricks, while otherwise a full fling fires — "sometimes it glides, sometimes it bricks." And `ScrollAnimator.CancelFling` has zero call sites: a mouse click or scrollbar grab over a coasting viewport does not stop it (content drifts under the click; the animator fights the thumb drag).

---

## 2. The model — one integrator, one writer, per-frame consumption

### 2.1 Single-writer invariant (the load-bearing rule)

> **`ScrollState.OffsetX/OffsetY` and `ScrollState.OverscrollPx` are written in exactly one place: `ScrollIntegrator.Tick(dtMs)`, at frame phase 7.**

`ScrollIntegrator` is the renamed, unified `ScrollAnimator` (same class, same file, same slab-armed active-set — see §6). The dispatcher, the scrollbar, and programmatic callers never touch offset/band; they record **intents** onto the target's `ScrollState` intent columns (§2.4) and arm the node. The chokepoint `ApplyScrollPosition` (transform write + `TransformDirty|PaintDirty` + `ScrollBindEval.ApplyContinuous` + virtual re-realize) is called only from inside `Tick`. This makes the "two writers in one frame" bug (R1) structurally impossible and the single-writer gate (§8) trivial: a `[Conditional]` guard flags any `ApplyScrollPosition` call whose stack is not the phase-7 tick.

Latency is unchanged: dispatch (phase 2) records the intent, the tick (phase 7) applies it, both in the same frame before record (phase 8). Contact still lands on-screen the same frame it arrived.

### 2.2 The state enum

`ScrollState.Phase` (a `byte`, replacing the untyped `ScrollMode`; values are compile-time consts):

| State | Entered by | Physics (all in `Tick`) | Offset writer |
|---|---|---|---|
| **`Idle=0`** | settle / clamp / cancel | none; node drops from the active set | — |
| **`TouchpadTracking=1`** | `ScrollBegin` (touch, PTP, fallback, DM `RUNNING`) | resampled contact position → offset 1:1; past-edge → `Overscroll` | tick |
| **`WheelAnimating=2`** | discrete wheel notch, scrollbar track-click, keyboard, **Programmatic** bring-into-view | velocity-preserving critically-damped chase to an accumulated, hard-clamped `TargetX/Y` | tick |
| **`Fling=3`** | touch/PTP-fallback lift `|v|≥seed`; **`OsMomentum` sub-flag** for DM `INERTIA` | exact-integral coast (`CoastStep`); OS-owned sub-mode applies verbatim OS deltas instead | tick |
| **`Overscroll=4`** | `TouchpadTracking` dragged past a content edge | finger drives the iOS rubber-band 1:1 (band only; offset pinned) | tick |
| **`SnapBack=5`** | `Overscroll`/`Fling` release with a live band | critically-damped spring band→0 (`ω=12.5`) | tick |

Auxiliary flags on `ScrollState` (not separate states): `PhaseOsOwned` (Fling consuming OS `INERTIA` deltas verbatim, no decay math), `PhaseProgrammatic` (WheelAnimating seeded by bring-into-view — excluded from `UserScrollActive`), `PhaseWheel` (WheelAnimating driven by a mouse notch — hard-stops at extents, never bands).

Feel-critical transitions (asserted by the §8 matrix gate):
- **Any `PointerDown` over a coasting/animating viewport zeroes its motion first** (`Fling`/`WheelAnimating` → `Idle`, band handed to `SnapBack`). Touch-down, mouse-click, and scrollbar-grab all call `CancelFling`. Fixes R6's dead `CancelFling`.
- **Contact zeroes momentum, never adds** (`ScrollBegin` → velocity=0, re-latch); a re-grab over a live `SnapBack` band folds the band back into the anchor via the *correct* inverse so the stretch continues seamlessly.
- **Wheel/keyboard during a contact or fling** → `CancelGesture()` then seed `WheelAnimating` (synchronous cross-device handoff; the renamed, `_tp*`-free `CancelScrollGestureForWheel`).
- **`INERTIA→RUNNING`** (DM path, fingers re-land mid-coast) → `Fling(OsOwned)` ends, `TouchpadTracking` begins (stop-on-contact).
- **Extent asymmetry:** only contact-descended states (`TouchpadTracking`, `Fling`, `SnapBack`) produce a band; `WheelAnimating` hard-stops at the clamp.
- **Whole-gesture latching:** contact frames and the momentum that follows belong to the target latched at `ScrollBegin` even if the pointer drifts over a sibling (AppKit rule).

### 2.3 Per-frame consumption

All input is *recorded* at dispatch and *consumed* once at phase 7. The integrator, per active node per frame:
1. Drains this node's contact samples (from the ring's side buffer, §3.4) into the gesture resampler.
2. Resamples the contact position to `frameTime − 5 ms` (§4.1) and emits exactly one displacement.
3. Advances the state's physics with the frame `dt` (closed-form, dt-deterministic).
4. Writes offset+band once through the chokepoint.

### 2.4 Intent columns (added to `ScrollState`, POD, zero-alloc)

```
byte  Phase;                 // the §2.2 enum (replaces ScrollMode)
byte  PhaseFlags;            // OsOwned | Programmatic | Wheel bitfield
float PendingTarget;         // absolute target for WheelAnimating/Programmatic/scrollbar (NaN = none)
float FlingVelocity;         // seed/live coast velocity (reused)
float OverscrollPx, OverscrollVel;   // band + band spring velocity (reused)
```
The per-packet contact samples live in the ring's existing velocity side buffer (§3.4), not in the slab — only one gesture is latched at a time, so the resampler state is a **singleton POD in the integrator** (fixed inline arrays), not per-node. No `ScrollState` growth beyond the two small fields above; the old `_tp*` fields are deleted.

---

## 3. Event ingestion

### 3.1 Windows messages consumed

| Message | Meaning | Producer action |
|---|---|---|
| `WM_POINTERWHEEL` (0x024E) | vertical wheel/PTP-promoted (mouse-in-pointer ON) | classify (§3.3) → detented-`Wheel` **or** hi-res `ScrollBegin/Update` |
| `WM_POINTERHWHEEL` (0x024F) | horizontal | same, X axis |
| `WM_POINTERDOWN/UP/UPDATE` (0x0246/47/45) | touch/pen/mouse contacts | touch pan → `ScrollBegin/Update/End`; any down → cancel-coast intent |
| `WM_SETTINGCHANGE` (0x001A) | user changed wheel prefs | re-read `SPI_GETWHEELSCROLLLINES/CHARS` |
| `WM_DPICHANGED` (0x02E0) | DPI change | re-read effective scale for px normalization + device-px rounding |
| *(optional Phase D)* `DM_POINTERHITTEST` (0x0250) | first msg of a touchpad sequence | `viewport.SetContact(pointerId)` for `PT_TOUCHPAD` |

`GET_WHEEL_DELTA_WPARAM` (HIWORD, signed, ×120 units), `LOWORD(wParam)` = pointerId, `lParam` = screen coords (`GET_X/Y_LPARAM`, never LOWORD for multi-monitor negatives). Wheel goes to the focus/hover window; never forward internally.

### 3.2 Normalization to px (DPI + wheel-lines)

Read once at init and on `WM_SETTINGCHANGE`:
- `SPI_GETWHEELSCROLLLINES` → `wheelLines` (default 3; `WHEEL_PAGESCROLL`/`>WHEEL_DELTA` ⇒ page mode).
- `SPI_GETWHEELSCROLLCHARS` → `wheelChars` (horizontal).

**Detented notch → DIP** (WheelAnimating):
```
notches = signedCarryover(rawDelta)          // accumulate rawDelta, emit per 120 crossed, keep signed remainder, reset on direction change
perNotch = max(WheelPerNotchMinDip, WheelPerNotchViewportFrac · viewport)   // 48 DIP or 10% viewport
distance = notches · perNotch · (wheelLines / 3.0)      // page mode ⇒ 0.875 · viewport per notch
```
Signed carryover (per axis, reset on sign flip) is mandatory for sub-120 free-spin wheels — the SumatraPDF #3032 fix. Fixes the "SPI ignored" defect.

**Hi-res / PTP-fallback → DIP** (TouchpadTracking, 1:1):
```
contactDip = rawUnit · HiResUnitDip · (dpi / 96) · UserTouchpadSpeed
```
`HiResUnitDip = 0.11` is documented as the 96-DPI calibration (continuity), now **DPI-scaled** and multiplied by a single user/settings speed (default 1.0). This replaces the frozen one-machine 0.11 (R4). The correct fix — real OS device pixels — arrives with DirectManipulation (§7, Phase D), which returns true translation and drops `HiResUnitDip` entirely.

### 3.3 Touchpad-vs-wheel classification

The chosen heuristic, in priority order, latched per gesture (idle gap `WheelGestureGapMs=200` re-evaluates):

1. **Authoritative device type when the stack exposes it:** `GetPointerDeviceType(sourceDevice) == POINTER_DEVICE_TYPE_TOUCH_PAD` ⇒ hi-res contact. (`PointerKindOf` already does this; keep it as the first gate.)
2. **Sub-notch granularity:** `rawDelta % 120 ≠ 0` ⇒ hi-res contact (free-spin mice also land here and *should* — they want 1:1-ish, not detents).
3. **Sustained high-cadence ±120 burst:** a run of **≥6** consecutive exact-±120 packets each **<50 ms** apart ⇒ demote to hi-res contact. This is the *corrected* form of the heuristic v1 reverted: the reverted version demoted on a *single* <35 ms gap (a fast mouse flick), which regressed genuine wheels; a real detented wheel cannot physically sustain six sub-50 ms notches, but this hardware's PTP burst produces dozens. The demotion is one-way within a gesture and never applies to notches 1–5, so a mouse flick is never misread.
4. **Otherwise** ⇒ detented mouse (`WheelAnimating`).

**Honest limit:** with promoted `WM_POINTERWHEEL` there is *no* exact discriminator (MS Learn; `GetCurrentInputMessageSource` reports `IMDT_MOUSE` for PTP). This heuristic is best-effort and structurally lower-fidelity. **The only exact answer is capturing `PT_TOUCHPAD` contacts via DirectManipulation, after which any residual `WM_POINTERWHEEL` is genuinely mouse (rule 4 with 100 % confidence)** — the Chromium/Firefox approach. That is the recommended optional Phase D (§7). The heuristic above is the always-compiled fallback and the mouse-wheel path forever.

### 3.4 Per-frame accumulation + timestamps

- **Ring coalescing (unchanged mechanism, corrected keying):** `ScrollUpdate`/`MomentumUpdate` for the same gesture sum deltas per frame, newest stamp survives; `Begin/End/Momentum*` never coalesce. Detented `Wheel` coalesces **by scroller target**, not by exact `PositionPx.Equals` (fixes the R-major defect where a resting-mouse `WM_POINTERUPDATE` split a frame's notches into multiple events, each re-running `CancelScrollGestureForWheel` + two hit-tests). The integrator sees ≤1 Begin, ≤1 summed Update, ≤1 End, and one summed wheel delta per target per frame.
- **Per-packet sample side buffer (the resample + velocity source):** the ring's existing `PushVelocitySample`/`DrainVelocitySamples` (`Pal.cs:206`) is retained and made the *single* source of sub-frame timing. Each contact packet deposits `{pointerId, seq, qpcTicks, cumAxisX, cumAxisY}` (cumulative-within-frame position, as today). **Cross-contamination fix (R-major):** samples are tagged with `pointerId` **and** the monotonic `ScrollPhaseSeq`; the integrator consumes only samples matching the latched gesture and with strictly increasing stamps, so an interleaved event that splits a frame into two `ScrollUpdate`s can never replay a later event's deposits against an earlier base.
- **Timestamp policy:** every packet carries `QpcTicks` (`Stopwatch.GetTimestamp()` on Windows; `0` = headless → frame clock). QPC → seconds via a cached `1/QueryPerformanceFrequency`. **Clock split (invariant):** QPC stamps feed *velocity and resampling only*; *position advances on frame `dt`* with closed-form dt-invariant steps. Non-monotonic/duplicate stamps are skipped (headless 0-stamps ⇒ vacuous, preserving gate determinism). Lift stamp = the last contact packet's QPC (fallback) or the DM `RUNNING→READY/INERTIA` transition (Phase D) — never the detection wall-clock.

---

## 4. Motion math

### 4.1 1:1 tracking with frame-time resampling (TouchpadTracking / touch)

Per frame, for the latched gesture (Chromium `LinearResampling` / Android `InputTransport`):
```
targetT   = frameQpcSec − ResampleLatencyMs/1000          // 5 ms behind frame time
// bracket targetT with the two newest samples s0=(t0,x0), s1=(t1,x1) retained across frames
if targetT ≤ t1:  x* = lerp(x0,x1, (targetT−t0)/(t1−t0))              // interpolate (preferred)
else:             pred = min(targetT−t1, ResampleMaxPredictionMs/1000, 0.5·(t1−t0))
                  x* = x1 + (x1−x0)/(t1−t0) · pred                    // extrapolate, capped
displacement = x* − xAppliedLast ;  xAppliedLast = x*
offset = clamp(anchor + x*, 0, max)                                   // in-range 1:1
excess = (anchor + x*) − clampedTarget                                // past-edge → §4.4 band
```
`ResampleMinDeltaMs=2` guards degenerate sample spacing. Extrapolation is capped at 8 ms *and* 50 % of the last inter-event delta (overprediction causes overshoot jitter). This is the single fix for R2: exactly one displacement per vsync, proportional to `dt`, independent of packet cadence. **No easing, no low-pass during contact** — the resampler *is* the smoothing.

### 4.2 Discrete-wheel-tick curve with retargeting (WheelAnimating)

A wheel notch does **not** fling. It advances a hard-clamped accumulated target and chases it with a velocity-preserving critically-damped spring (Chromium's velocity-matched retarget, expressed as the engine's closed-form spring so it is dt-deterministic):
```
PendingTarget = clamp(PendingTarget + distance, 0, max)     // §3.2; hard-stops at extents
// each tick, critically-damped (ζ=1) closed form toward PendingTarget, carrying velocity:
y  = 2·ln2 / (WheelChaseHalflifeMs/1000)
j0 = off − PendingTarget ;  j1 = vel + j0·y ;  e = exp(−y·dt)
off = e·(j0 + j1·dt) + PendingTarget
vel = e·(vel − j1·y·dt)
```
Because velocity carries across a re-target, rapid notches accumulate distance smoothly with no restart jerk (the property Chromium's `UpdateTarget` preserves). Bigger gaps move faster naturally. This **supersedes v1's `WheelFlingMode`** and, by using a target rather than a `v·dt` integral, eliminates R3 for the wheel entirely and gives free hard-stop-at-extents. The identical machinery serves keyboard, scrollbar track-click, and Programmatic bring-into-view (Programmatic uses `ProgrammaticSpringHalflifeMs=95`).

### 4.3 Velocity tracker + fling decay (Fling)

**Estimator: IMPULSE (work-energy) over one 40 ms window** — Android's scroll-axis strategy, robust to last-sample noise, needs only 2 samples. It replaces v1's dual gate (work-energy + trailing-32 ms), which was a bolt-on the design said should not exist (R6):
```
over samples in the last VelWindowMs (40 ms):  v_i = δ_i/Δt_i
W = ½·v0·|v0| + Σ (v_i − v_{i−1})·|v_i|
release v = sign(W)·√(2|W|)
if newest sample older than AssumeStoppedMs(40) at lift → v = 0
```
The 40 ms window (≈3 samples at ~15 ms cadence) *is* the trailing window — a completed OS/driver tail decays to `v≈0` (below the seed gate ⇒ no double inertia), an abrupt lift keeps true hand speed, a mid-stream reversal yields opposite-sign `v` ⇒ no stale coast. One window, one gate.

**Fling decay — exact integral (fixes R3):**
```
k = −ln(FlingDecayPerS)                                   // ≈2.0/s at 0.135
Δpos = CoastStep(ref v, dt, FlingDecayPerS) = v·(1−decay^dt)/k     // OverscrollPhysics.cs:79
```
The fling integrator calls `CoastStep` (already present, already gated) instead of `off += v·dt`. Total coast telescopes to `v0/k` at any refresh — same distance on 60 Hz and 120 Hz. Seed clamp `FlingMaxVelocityPxPerS=8000`. Snap-retarget math (`natural = off + v/k`) is unchanged and now consistent with the integrator.

**OS-momentum sub-mode (`Fling.OsOwned`, DM path):** OS `INERTIA` deltas apply *verbatim* through the resampled 1:1 path — no decay math, no estimator; the OS computed the curve, the engine is a display.

### 4.4 iOS rubber-band overscroll (Overscroll)

Replace the min()-clamped soft-knee (R5) with the canonical iOS asymptotic map — bounded, never a wall, marginal give never zero:
```
d = BandAsymptoteFraction · viewport            // 0.15 · vp — the asymptote (never reached)
f(x) = (x · d · RubberC) / (d + RubberC · |x|) · sign(x)    // c = 0.55; slope c at 0
```
Applied only to the past-edge excess; in-range is 1:1. One band for touch **and** touchpad (kills v1's velocity-enveloped `_tpVel` band). **Exact inverse for re-grab** (fixes the wrong-inverse snap): `x = f·d / (RubberC·(d − |f|))` — valid for all `|f|<d`, no saturation branch.

### 4.5 Critically-damped snap-back (SnapBack)

`StepSpring` (closed-form ζ=1, `OverscrollPhysics.cs:117`) with `ω = SnapBackOmega = 12.5 rad/s` (was 42). τ=80 ms, settle ≈320 ms — matches WebKit/Chromium Mac (λ=12.5). Seeded with the edge-crossing velocity when a fling hits the clamp (`SeedFromEdgeMomentum`), non-dominant axis zeroed. The early-snap-home guard (≤0.5 DIP, ≤8 px/s) stays.

### 4.6 Tuning table (every constant, one place)

| Constant | Value | Units | State / path | Role & formula | Provenance |
|---|---|---|---|---|---|
| `ResampleLatencyMs` | 5 | ms | TouchpadTracking/touch | resample to `frameT−5ms` | Android/Chromium |
| `ResampleMaxPredictionMs` | 8 | ms | " | extrapolation cap (also ≤50 % last Δ) | Chromium |
| `ResampleMinDeltaMs` | 2 | ms | " | min usable sample spacing | Android |
| `PanSlopPx` | 4 | DIP | latch | axis+target latch after Σ|δ|≥4 | `SM_CXDRAG` |
| `dtClampMs` | 34 | ms | all | frame `dt` clamp (coast under-travels on a hitch — safe) | Gaffer |
| `HiResUnitDip` | 0.11 | DIP/unit @96dpi | fallback contact | `dip = raw·0.11·dpi/96·speed` | continuity; DM removes it |
| `UserTouchpadSpeed` | 1.0 | × | fallback contact | user/settings multiplier | new |
| `WheelPerNotchMinDip` | 48 | DIP | WheelAnimating | `max(48, 0.10·vp)·lines/3` | WinUI line height |
| `WheelPerNotchViewportFrac` | 0.10 | — | " | " | WinUI |
| `WheelChaseHalflifeMs` | 40 | ms | WheelAnimating | velocity-preserving crit-damped chase (~130 ms settle) | Chromium retarget |
| `ProgrammaticSpringHalflifeMs` | 95 | ms | Programmatic | crit-damped bring-into-view | KEEP |
| `WheelGestureGapMs` | 200 | ms | classifier | "same wheel spin?" re-latch | KEEP |
| `FlingDecayPerS` | 0.135 | /s survival | Fling | `Δpos=v·(1−decay^dt)/k`, `k=−ln≈2.0/s` | iOS `.normal` |
| `FlingSeedGate` | 50 | px/s | Fling | `|v|≥50` seeds a coast | Android min-fling |
| `FlingSettleVelPxPerS` | 13 | px/s | Fling | terminates below | KEEP |
| `FlingMaxVelocityPxPerS` | 8000 | px/s | Fling | seed clamp | Android max-fling |
| `VelWindowMs` | 40 | ms | Fling estimator | IMPULSE window (= the trailing window; single gate) | Android IMPULSE |
| `AssumeStoppedMs` | 40 | ms | Fling estimator | newest sample older ⇒ v=0 | Android `ASSUME_POINTER_STOPPED` |
| `RubberC` | 0.55 | — | Overscroll | `f=x·d·c/(d+c·|x|)` | iOS @chpwn |
| `BandAsymptoteFraction` | 0.15 | — | Overscroll | `d = 0.15·vp` (asymptote, never reached) | WinUI-ish cap, no wall |
| `SnapBackOmega` | 12.5 | rad/s | SnapBack | crit-damped (ζ=1), τ=80 ms, settle ≈320 ms | WebKit λ=12.5 |
| `HiResLiftMs` | 80 | ms | fallback lift | silence→`ScrollEnd` (device-adaptive, timer-driven §5) | ~1.3× burst cadence |

Deleted knobs: `FG_WHEEL_DECAY`, `FG_OS_OMEGA/ZETA/MOMENTUM`, `WheelFlingDecayPerS`, `WheelFlingMode`, the soft-knee `soft=2·limit`, `TrailWindowMs`, all `s_tp*`. One shipping feel, no env reads on the hot path.

---

## 5. Frame pacing

**Where in the 13-phase loop:** input is *recorded* at phase 1 (pump → ring + side buffer) and phase 2 (dispatch → intent columns), and *consumed/applied* at **phase 7** in `ScrollIntegrator.Tick` — the single point that resamples, advances physics, and writes offset+band, immediately before layout/record (phase 8). `AppHost.cs:1410` already calls the tick here after `_anim.Tick` and before `ScrollBindEval` pin/observer passes; that ordering is preserved so binds still read post-physics.

**dt policy:** `dtMs = _frameTime.NextDeltaMs()` (vsync/frame-clock delta), clamped to `dtClampMs=34`. The existing `dt ≤ 0` Resync bail on every scroll tick (`ScrollAnimator.cs:229`) is kept and now covers *all* states (the old `TickTouchpad` fatally lacked it). Animation `t` is driven by the frame clock, never wall-clock; a missed present cannot teleport a fling because the closed-form integral is exact for the (clamped) `dt`.

**60 vs 120 Hz:** identical feel by construction. Contact tracking resamples to a fixed 5 ms behind frame time, so per-frame displacement is proportional to `dt` regardless of the 125 Hz packet cadence. Fling/coast use `CoastStep`, whose total distance telescopes to `v0/k` at any `dt`. Snap-back and wheel-chase are closed-form in `dt`. The §8 dt-invariance sweep (8/16/33 ms) pins this within 1 DIP.

---

## 6. What gets deleted vs kept — file by file

**`src/FluentGpu.Engine/Animation/ScrollAnimator.cs` → becomes `ScrollIntegrator` (rename in place; keep the armed-set/conscious-bar machinery).**
- KEEP: `_active`/`_member`/`_parkedActive` set, `Arm`/`Drop`/`SetNodeParked`, the entire conscious-scrollbar state machine, `AnyUserScrollActiveThisFrame`/`UserScrollActive` (semantics preserved exactly — the lyrics DoF gate depends on them; `movingNow` stays true for TouchpadTracking, Fling incl. OsOwned, WheelAnimating, SnapBack; false for Programmatic/Idle).
- REPLACE: the `ScrollMode 0/1/2/3` block with the §2.2 enum; the fling `off += v·dt` (`:308`) with `CoastStep`; `WheelFlingMode`+`WheelFlingDecayPerS`+`FG_WHEEL_DECAY` (`:88,:124,:276`) with the WheelAnimating retarget spring (§4.2).
- ADD: `TouchpadTracking` with the resampler (§4.1); `Fling.OsOwned` verbatim path; consumption of the intent columns; the sole `ApplyScrollPosition` call site; `CancelFling` invoked from every pointer-down (§2.2).
- DELETE: nothing structural; `CancelFling` gains real call sites.

**`src/FluentGpu.Engine/Animation/OverscrollPhysics.cs` — KEEP the file, surgically fix.**
- REPLACE `BandFromExcess` (`:39`) with the iOS rational map (§4.4); rewrite `ExcessFromBand` (`:51`) as the exact inverse (no saturation early-out).
- CHANGE `SpringOmegaRadPerS` 42→12.5, drop the `Env` reads (`:26,:27,:30,:198`) to plain consts.
- KEEP as-is: `CoastStep` (now actually called), `StepSpring`, `SeedFromEdgeMomentum`, `GuardBandSign`, `BandLimit`.
- ADD device-pixel rounding to `WriteContentTransform` (§8): `tx = round((offset+band)·s)/s`, `s` = effective DPI scale; logical state stays float.
- DELETE: `DmScaleAtMaxOverpan`/`ScaleForBand` (touch-scale-overpan, unused on the translation-only path) — optional cleanup.

**`src/FluentGpu.Engine/Animation/ScrollTuning.cs` — KEEP, extend.**
- ADD `WheelLinesMultiplier` plumbing (from SPI), `RubberC`, `BandAsymptoteFraction`, `SnapBackOmega`, `WheelChaseHalflifeMs`, the resample constants. Keep `WinUiLike`/`HeadlessGolden` profiles; `HeadlessGolden` keeps 1-DIP/notch for deterministic wheel arithmetic. Correct the stale "15 %/viewport" comment to `max(48, 10 %·vp)·lines/3`.

**Dispatcher scroll paths (`InputDispatcher.cs`) — demote to intent recorders.**
- KEEP: `OnScrollPhase` switch, the axis-latch window (`AccumulateContactDelta`, `:2599`), hit-test routing (`ScrollAt`/`ScrollAxis`), nested-scroll chaining (`OuterScroller`/`ChainFlingTarget`), scrollbar hit-testing (`TryScrollbarPointerDown`/`DragScrollbar`), the `ImpulseVelocity` struct (retune to one 40 ms window; drop `TrailWindowMs`+dual gate at `:2563`).
- CHANGE: `AccumulateContactDelta`/`ApplyMomentumDelta`/`ApplyGestureOffset` write **intent columns + append samples**, never call `ApplyTouchPan`/`SetScrollOffset`. `ScrollBy` (wheel) sets `PendingTarget` + `WheelAnimating`, never seeds a fling. `SetScrollOffset`/`ApplyTouchPan`/`ApplyScrollPosition` become integrator-internal (the chokepoint); scrollbar drag and programmatic record an absolute-`PendingTarget` intent. `CancelScrollGestureForWheel` → `CancelGesture()`.
- ADD: `CancelFling` on every `PointerDown` (mouse/touch/pen) and at `TryScrollbarPointerDown` entry (fixes R6).
- DELETE (already gone in the tree, keep gone): `TickTouchpad`, `PanTouchpad`, `ShapeTouchpadPacketDelta`, the `s_tp*` block.

**`src/FluentGpu.Windows/Pal/Win32Platform.cs` — producer.**
- KEEP the wheel-fallback producer shape; REPLACE the classifier (`:957`) with §3.3 (device-type first, sub-notch, sustained-burst demotion), the frozen `0.11` with the DPI-scaled `contactDip` (§3.2), and the per-frame-only silence check (`:718`) with a short timer-driven, cadence-adaptive lift (§5 below). ADD `SPI_GETWHEELSCROLLLINES/CHARS` in `ReadSystemParams` + `WM_SETTINGCHANGE` refresh.

**`src/FluentGpu.Engine/Seams/Pal/Pal.cs` — ring.**
- KEEP delta-summing coalescing + the velocity side buffer; CHANGE wheel coalescing to key by scroller target (not exact position); tag side-buffer samples with `ScrollPhaseSeq` (cross-contamination fix).

**Lift adaptivity (interim fallback):** drive the silence check off a short monotonic timer (not only per-frame `PumpInto`), and make `HiResLiftMs` adaptive: `clamp(1.4 × observed median inter-packet gap, 50, 120)`. This bounds the freeze-then-fling hitch on stacks with no OS tail without inventing a lift the pipeline can't observe. The real fix is a phase source (Phase D).

---

## 7. Optional Phase D — DirectManipulation (recommended, clearly marked)

`tools/dm-probe` already returned GO on the dev machine (cell B PASS with `EnableMouseInPointer` ON). DM is the **only** way to eliminate touchpad-vs-wheel guessing (R4) and get true finger-lift + OS-curved momentum. It is optional because the fallback is always-compiled and correct-if-lower-fidelity, but it is the recommended target and everything above is shaped so DM is a *pure producer swap* — it emits the same `ScrollBegin/Update/End` + `MomentumBegin/Update/End` the integrator already consumes.

New `FluentGpu.Windows/Pal/Win32DirectManipulation.cs` (hand-vtable COM, all UI-thread), event-source-only:
- Fake 1000×1000 `MANUALUPDATE` viewport; `AddConfiguration(TRANSLATION_X|Y|INERTIA|SCALING|SCALING_INERTIA|INTERACTION)`; `DM_POINTERHITTEST`→`SetContact` for `PT_TOUCHPAD`; per-message `ProcessInput` + one `Update` per drain.
- `OnViewportStatusChanged`: `RUNNING`→`ScrollBegin`, `RUNNING→INERTIA`→`MomentumBegin`, `INERTIA→READY`→`MomentumEnd`+viewport reset, `INERTIA→RUNNING`→`MomentumEnd`+`ScrollBegin`. `OnContentUpdated`: translation diff → `Scroll/MomentumUpdate` (sign verbatim). `|scale−1|>0.01` ⇒ emit no pan (pinch suppressed). No `RAILS_*` (the integrator's axis latch owns railing).
- Transform→DIP via live DPI + a single frozen `DmDipPerTransformUnit=1.0` (no knee). Wedge watchdog (`DmEngageTimeoutMs=120`, `DmWedgeCountToDisable=3`) falls back to §3.3 edge-triggered (never two owners for one packet). Popups keep the fallback.

When DM owns the contact, §3.3 rule 4 becomes exact: residual `WM_POINTERWHEEL` is genuinely a mouse.

---

## 8. Validation

**ScrollTrace assertions** (reuse `Foundation/ScrollTrace.cs`; add a `Phase` column carrying the §2.2 enum):
- Every offset write records `writer = integrator`; a write with any other writer id fails the trace check.
- Exactly one `ApplyPan`/offset record per active node per frame (no per-event writes).
- `Release` records: `|v|` and the coast that follows telescope to `v0/k` within ε across the dt sweep.
- Latch/GestureEnd ordering: `INERTIA→RUNNING` shows `MomentumEnd` then `ScrollBegin`.

**Headless gates** (`HeadlessScrollProducer` scripts all six kinds with synthetic QPC; all run on `Rhi.Headless`/`Pal.Headless`, 0 managed bytes phases 6–13):
1. `gate.scroll.single-writer` — a full contact+momentum+fling+band+wheel cycle records **exactly one offset-writing code path** (the integrator); a `[Conditional]` guard asserts no `ApplyScrollPosition` executes during phase 2. *Directly pins R1.*
2. `gate.scroll.dt-invariance` — the same scripted stream at `dt∈{8,16,33} ms` yields the same trajectory within 1 DIP (contact, wheel, fling, snap-back). *Pins R2/R3/R5 frame-independence.*
3. `gate.scroll.resample-cadence` — a constant-velocity stream delivered at 1-packet/2-packet alternating cadence produces monotonic, near-constant per-frame displacement (variance below a bound). *Pins R2.*
4. `gate.scroll.contact-1to1` — `|applied − resampled Σδ| ≤ 0.5 DIP` every frame during TouchpadTracking.
5. `gate.scroll.coast-distance` — a wheel notch coasts its exact `perNotch` distance ±0.5 DIP; a touch fling coasts `v0/k` ±1 DIP; both identical at 60/120 Hz. *Pins R3.*
6. `gate.scroll.impulse-velocity` — `sign(W)·√(2|W|)` within ε; gap ≥40 ms ⇒ 0; a decaying tail then silence ⇒ `v<50` (no double inertia); an abrupt lift ⇒ one coast; a reversal ⇒ no stale-direction coast. *Pins R6.*
7. `gate.scroll.overscroll-rational` — band = `x·d·0.55/(d+0.55|x|)` (never reaches `d`, marginal slope >0 everywhere); `ExcessFromBand∘BandFromExcess` round-trips within 0.5 DIP **including at/above the old saturation point** (`x=2·limit`, `x=10·limit`); snap-back settles in 300–360 ms. *Pins R5.*
8. `gate.scroll.pointerdown-cancels` — a mouse click, a touch-down, and a scrollbar grab over a live Fling/WheelAnimating each zero velocity the same frame with no residual drift. *Pins R6.*
9. `gate.scroll.wheel-lines` — `SPI_GETWHEELSCROLLLINES∈{1,3,∞/PAGESCROLL}` scale per-notch distance correctly (page ⇒ 0.875·vp); signed carryover accumulates sub-120.
10. `gate.scroll.subpixel-stability` — a slow sub-pixel pan produces monotonic whole-device-px translate steps while logical float stays continuous, including a ScrollBind sticky-header pin sharing the same origin (no 1 px seam).
11. `gate.scroll.transition-matrix` — assert the legal (from-state × event → to-state) table exhaustively; any undefined transition is a hard failure. *Structural guard for R1.*
12. `gate.scroll.alloc-zero` — 0 managed bytes across the full cycle, including the DM-style sink cadence (Phase D).

**On-device recipe:** `FG_SCROLL_TRACE` logs `phase | qpcMs | dipDelta | device | writer` per packet; the synchronous tracking-lag overlay (finger-implied Σδ vs applied offset must overlap during TouchpadTracking); `ScrollLatencyProbe` for input→photon (honest accounting: async present adds ≥1 frame).

**Constraints honored throughout:** all new state is POD in the slab / fixed inline arrays (zero per-frame managed allocation, phases 6–13), NativeAOT-clean, no new dependencies; DirectManipulation is the only optional addition and is isolated to `FluentGpu.Windows` behind the existing hand-vtable COM pattern (the `FluentGpu.Controls`/VerticalSlice closure stays TerraFX-free).