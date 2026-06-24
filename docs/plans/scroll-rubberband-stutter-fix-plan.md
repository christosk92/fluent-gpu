# Scrolling smoothness â€” research findings & fix plan

> Engine-owned touchpad/touch scroller (no DirectManipulation / InteractionTracker / WUC). All fixes land at the engine root, keep the rubber-band, stay zero-alloc in frame phases 6â€“13, and tune via `FG_TP_*` / `FG_OS_*` value-knobs â€” never logic edits. Line numbers are against the live tree at `C:\WAVEE\fluent-gpu\src\â€¦` (the `app/src` paths in the diagnosis inputs were stale; all citations below were re-verified).

## TL;DR

- **Symptom A ("ignores 1â€“2 packets and stops") is the SETTLE gate, not the velocity window.** The settle test at `InputDispatcher.cs:2421` has no minimum quiet-time, so a normal ~60 ms inter-burst gap clears `_tpTarget` mid-gesture; the next packet restarts a fresh gesture from zero momentum â†’ "scroll, freeze, scroll." A frame-driven **quiet-time guard** (`FG_TP_SETTLE_QUIET`, default 90â€“120 ms) fixes it **on its own** â€” simulation showed quiet-only eliminates 7â€“8/8 cadences. The 50 ms velocity window widening is a **secondary smoothness** improvement (continuous inter-burst coast), *not* the root cause it was originally labelled.
- **Symptom B ("rubber-band totally buggy") is a dead, velocity-0 spring release.** The touchpad path never enters `FlingMode`, so `SeedFromEdgeMomentum` is structurally unreachable; the band is released to the spring with **velocity 0** (pure exponential decay, no overshoot) while the touch fling gets a lively momentum-coupled bounce. Fix: **latch the coast velocity at edge entry** and seed `OverscrollVel = _tpEdgeVel * MomentumSpringCoupling` at settle (`FG_TP_FWDBOUNCE`, 200 DIP/s gate). Keep the band â€” make it bounce.
- **Two refuted non-bugs â€” do NOT re-attempt:** the spring does **not** fight the live band (the `Overscrolling` flag gates it correctly, H4 refuted); and there is **no DPI bug** in the deltaâ†’DIP path (the engine is DIP-native, so scroll fraction is already DPI-invariant; the `notch` is an abstract `WHEEL_DELTA` count that must *not* be `/_scale`'d).
- **Secondary feel tunes (deferred, value-only):** spring `FG_OS_OMEGA` 42â†’~70â€“80 (sluggish recoil once the bounce exists); `FG_TP_BAND_HEADROOM` so the touchpad band reaches ~90% of the 10% cap instead of 67%; `FG_TP_SMOOTH_TAU` 14â†’12 for edge-entry start-lag; `FG_TP_COAST_MAXDT` / `FG_TP_SMOOTH_MAXDT` dt sub-caps (inert by default) for GC/realize-stall lurch headroom.
- **One harness blind spot caused all of this to ship:** `DriveTouchpadPan` spaces packets 16 ms apart (gap < window), so the inter-burst freeze is **never exercised**. A new gate that drives packets >WindowMs apart and asserts no mid-stream restart is part of this plan.

---

## How the best engines do it

| Engine | Velocity strategy | Rubber-band / overscroll | Momentum â†’ spring handoff | Key numbers |
|---|---|---|---|---|
| **Chromium** | `VelocityTracker` LSQ regression, `DEFAULT_HORIZON = 100 ms` (spans one bursty gap); `ScrollVelocityTracker` has a single-sample fallback (v = Î”/window) instead of hard-zeroing | `ElasticOverscrollControllerBezier`, resistance â‰ˆ `tanh(2Â·x/bounds)Â·boundary`, cap `kOverscrollBoundaryMultiplier = 0.1` (10% viewport) | Bounce seeded with **residual velocity at GestureScrollEnd**; `kIgnoreForwardBounceVelocityThreshold = 200 px/s` gates whether a forward overshoot fires; the spring runs only *after* gesture end (no fight during drag) | scroll-end latch â‰ˆ 100 ms (`kDefaultWheelScrollLatchingTransactionMs`), reset on every packet; resample latency `-5 ms` |
| **Apple / UIKit** | velocity at lift-off | `f(x,d,c) = (xÂ·dÂ·c)/(d + cÂ·x)`, `c = 0.55` â€” sub-linear, recomputed per-frame against instantaneous distance | bounce is **momentum-coupled** â€” the gesture's terminal velocity carries into the spring ("alive" overshoot) | spring `Î² â‰ˆ 50â€“100 rad/s` |
| **WinUI InteractionTracker** | â€” | DIP/logical space â†’ fraction-invariant across DPI; cap 10% viewport (`ScrollInputHelper.cpp:309`) | critically-damped snap-back | decay `r = 1âˆ’0.95 = 0.05`; min velocity `30` (`ScrollPresenter.cpp:6497`); Composition design-team snap-back period â‰ˆ 50 ms â‡’ Ï‰ â‰ˆ 126 rad/s |
| **Android / Flutter** | `VelocityTracker` degree-2 polynomial fit, `HORIZON = 100 ms` (`_horizonMilliseconds = 100`); extrapolates through gaps; `ASSUME_POINTER_STOPPED_TIME = 40 ms` | â€” | â€” | 100 ms horizon is the cross-engine consensus for spanning one burst gap |

**The two numbers that matter most here:** every production velocity tracker uses a **~100 ms horizon** (ours is 50 ms), and every production overscroll **seeds the bounce with residual velocity** (ours seeds 0). Our feel constants that are *already* at parity and must NOT change: decay `s_tpDecayPerS = 0.05` (= IT `r`), settle velocity `s_tpSettleVel = 30` (= IT min), cap `ViewportLimitFraction = 0.1` (= WinUI), `MomentumSpringCoupling = 0.45`, `SpringDampingRatio = 1` (critically damped).

---

## Symptom A â€” stutter / "ignores 1-2 packets and stops"

### Confirmed root cause: SETTLE gate has no quiet-time (`settle-no-quiet-guard`, P1)

The settle gate, verified at `InputDispatcher.cs:2421`:

```csharp
if (!_tpGotPacket && MathF.Abs(_tpVel) < s_tpSettleVel && MathF.Abs(_tpDemandRaw - _tpAppliedRaw) < 0.5f)
```

has **no minimum quiet-time**. Sharper still: `_tpGotPacket` is cleared at line 2399 *inside* the `if (_tpGotPacket)` block **before** the gate runs, so `!_tpGotPacket` is true even on a frame that just processed a packet.

**Mechanism (verified by faithful sim against live constants):** This device bursts hi-res packets ~60 ms apart. Once `_tpVel` is at/near 0 (vel is identically 0 at every settle in the trace â€” see below) and the one-pole low-pass (`tau = 14 ms`, `kOff â‰ˆ 0.70/frame at 60 Hz`) has driven `_tpAppliedRaw` within 0.5 px of the frozen demand (â‰ˆ3 coast frames), the gate **fires during the inter-burst silence**. It sets `_tpVel = 0`, `_tpTarget = NodeHandle.Null` (2430). The next packet hits the `_tpTarget.IsNull` branch in `PanTouchpad` (2340) â†’ **new gesture**: `_tpDemandRaw = _tpAppliedRaw = startOff`, `_tpVel = 0`, `_tpVelTracker.Reset` (2344â€“2355). Momentum is discarded **every burst** = the exact "scroll a bit, freeze, scroll a bit" reported.

Sim result against the real cadence: baseline restarts = **6/7/8/12** over 8 bursts at 60/80/100/120 ms gaps where 1 is expected. The 50 ms-gap case (gap < window) does **not** freeze â€” confirming the gap-vs-window relationship and why the harness never caught it.

> **Correction to the original diagnosis (load-bearing):** the claim that this "must ship paired with the velocity-window widen (each alone insufficient)" is **backwards**. Sim shows **quiet-only** (window unchanged at 50) fixes 7/8 cadences; quiet=150 fixes 8/8. Window-widening **alone** fixes only ~3/8 (each settleâ†’restart calls `vt.Reset()`, wiping the sampler to 1 in-window sample regardless of window width). The quiet-guard is **sufficient**; the window-widen is a secondary smoothness improvement, not a co-requirement.

### Fix A1 (PRIMARY, sufficient on its own)

`src/FluentGpu.Engine/Input/InputDispatcher.cs`:

1. **New field** (after line 64, beside `_tpGotPacket`):
   ```csharp
   private float _tpQuietMs;  // frame-driven quiet-time since last packet; gates settle so a normal inter-burst gap can't end the gesture (no wall-clock read â€” mirrors _arenaClockUs)
   ```
2. **In `PanTouchpad`, immediately after `_tpGotPacket = true;` (line 2359):** `_tpQuietMs = 0f;`
3. **In `TickTouchpad`'s coast else-branch (after the `CoastStep` call, ~line 2409):** `_tpQuietMs += dtMs;` â€” accumulate **only** on coast frames, never on packet frames (this is what blocks the "settle on a packet frame" case).
4. **Gate settle (line 2421):**
   ```csharp
   if (!_tpGotPacket && _tpQuietMs > s_tpSettleQuietMs && MathF.Abs(_tpVel) < s_tpSettleVel && MathF.Abs(_tpDemandRaw - _tpAppliedRaw) < 0.5f)
   ```
5. **New knob** (near the other `s_tp*` statics, ~line 145):
   ```csharp
   private static readonly float s_tpSettleQuietMs =
       float.TryParse(Environment.GetEnvironmentVariable("FG_TP_SETTLE_QUIET"),
           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
           out float sq) && sq > 0f ? sq : 120f;
   ```
6. **Hygiene** (line 1152 reset block): add `_tpQuietMs = 0f;`.

**Default value choice:** ship **120 ms** (not 90). Sim showed the 120 ms slow-drag case still restarts at quiet=90 but is clean at quietâ‰¥120; some touchpads/DPI burst >90 ms apart. 120 ms is still well below a deliberate human pause. Larger quiet only delays a legitimate gesture-end slightly â€” it can never reintroduce the freeze (safe direction). Mirrors Chromium's `scroll_end_timer` reset-on-every-packet latching (~100 ms).

**Why it's safe:** `_tpQuietMs` is a `float` (no heap); accumulated from `dtMs` (frame-driven, **not** wall-clock) so replay is bit-identical. Verified live: `gate.touchpad.alloc-zero` stays green (the guard keeps the gesture armed *longer* â†’ more coast frames, never fewer); `gate.touchpad.settle-determinism` asserts only replay-equality + `off > 5f`, not an absolute tick count. The aside that the spring "fights" the live band is refuted (`ApplyTouchPan` sets `Overscrolling = true` while band â‰  0, so the animator spring is suppressed during the gesture).

### Fix A2 (SECONDARY â€” continuous inter-burst coast, optional)

Widen the velocity regression so the coast velocity survives a ~60 ms gap and bridges bursts as continuous momentum (with A2, the trace yields a real ~-983 px/s coast; without it, quiet-only holds the gesture alive but at `_tpVel = 0` â†’ glide-pause-glide rather than true coast).

**Critical: make the window TOUCHPAD-PRIVATE.** `TouchVelocity` (the struct at `InputDispatcher.cs:204â€“220`, `WindowMs = 50f` at 217, `Cap = 8` at 216, `[InlineArray(8)]` at 220) backs **three** fields: `_tpVelTracker` (touchpad), `_panVel` (touch fling), and `PointerSlot.PanVel`. A shared `const` widen also widens the touch fling â€” and `gate.touch.flick-decay-settle` asserts velocity-magnitude-dependent outcomes (`boundedShortOfClamp`, `coastedForward`), so a shared widen is **gate-risky**. Keep the touch path on its proven 50 ms.

In `struct TouchVelocity`:
- Add `private float _window;`. Change `Reset(Point2 abs, uint ts)` â†’ `Reset(Point2 abs, uint ts, float windowMs)` storing `_window = windowMs > 0 ? windowMs : s_defaultWindowMs`. In `Recompute` use `_window` (fall back to `s_defaultWindowMs` if 0). **This is mandatory** because `ClearWorkingScalars` does `_tpVelTracker = default` (line 1153) â€” a bare instance field would be wiped to 0 ms (â†’ every sample excluded â†’ permanent vel 0). `Reset` always precedes `Sample`, so carrying the window as a `Reset` parameter is regression-proof.
- `s_defaultWindowMs = 50f` (touch keeps proven behaviour). Touchpad calls `_tpVelTracker.Reset(origin, e.TimestampMs, s_tpVelWindow)` at 2355; touch calls `Reset(pos, ts, 0f)`.
- Knob: `private static readonly float s_tpVelWindow = â€¦FG_TP_VEL_WINDOWâ€¦ : 90f;` (90 ms spans one ~60 ms gap; Android/Flutter/Chromium all use ~100).
- Raise `Cap 8 â†’ 12` **and** the `[InlineArray(8)] â†’ [InlineArray(12)]` literal **in lockstep** (a 90â€“100 ms window at 120â€“144 Hz holds ~12 samples; a mismatch is a compile/index error, not an alloc). Still a value type â†’ 0 heap.
- Soften the hard-zero at line 276: change `if (m < 2) { _vx=0; _vy=0; return; }` â†’ `if (m < 2) return;` (leave the last estimate intact rather than wiping it).

> **A2 is optional and explicitly NOT the symptom-A root cause** â€” the data refutes that framing. Ship A1 first; add A2 for inter-burst smoothness. Note the larger `PointerSlot` (Cap 8â†’12 adds 48 B/slot across `_slots[10]`) must keep `gate.touchpad.alloc-zero` **and** `gate.touch.fling-alloc-steady-zero` at 0 B.

### Refuted symptom-A causes (do not re-attempt)
- **Coarse-timestamp same-tick drop** (`GetMessageTime()` 1 ms + dup-stamp guard) â€” *refuted as a freeze cause*. Dropping a same-tick twin is **protective** against the `denomâ†’0` zero (two identical-time points zero the regression), not a push toward `m<2`. At most a minor terminal-velocity under-read (slightly shorter coast), never the mid-scroll freeze.
- **Frame-starvation half of H3** â€” refuted: `TickTouchpad` invokes `OnScrollArmed` every frame, the loop free-runs, and per-frame packets coalesce into `_tpPendingDelta` (never dropped).

---

## Symptom B â€” buggy rubber-band

**Keep the rubber-band â€” make it bounce.** The band itself is fine; what's broken is that the touchpad release is a **dead exponential return from rest** while the touch fling gets a lively momentum-coupled overshoot.

### Confirmed root cause: velocity-0 spring release (`touchpad-bounce-no-velocity-seed`, P1, verdict CONFIRMED)

Two structural facts, all verified:
1. `InputDispatcher.cs:2425` on settle writes `sc.Overscrolling = false; sc.OverscrollVel = 0f;` **unconditionally** â€” handing the held `OverscrollPx` to the phase-7 spring with **zero** initial velocity.
2. `ApplyTouchPan` re-zeros `sc.OverscrollVel = 0f` on **every** live-band frame (line 2618).
3. The **only** non-zero spring seed is `OverscrollPhysics.SeedFromEdgeMomentum` (`bandVel = v * MomentumSpringCoupling = 0.45`), called from exactly **one** site â€” `ScrollAnimator.cs:295â€“302`, gated `ScrollMode == FlingMode`. The touchpad path sets `ScrollMode = 0` (`InputDispatcher.cs:2346`) and never enters `FlingMode` â†’ **structurally unreachable** on the touchpad path.

`StepSpring` (critically damped, `z = 1`) with `v0 = 0` is pure exponential decay â€” no overshoot. The overshoot term `v0Â·tÂ·exp(-Ï‰t)` that reads as "alive" only fires with non-zero seeded velocity. So **every** touchpad rubber-band is a flat, overdamped return = symptom B.

**Compounding constraint (verified):** settle requires `|_tpVel| < 30` (line 2421), and `CoastStep` decays `_tpVel` every coast frame. So at settle `_tpVel` is < 30 by construction â€” seeding `OverscrollVel = _tpVelÂ·coupling` **at settle** would seed ~13 px/s and fix nothing. **The velocity MUST be latched at edge entry, not at settle.** Numeric trace (decay 0.05, dt 16.67 ms): a fast glide leaves `_tpVel â‰ˆ -2500 px/s` at the last packet; `CoastStep` retains ~95.1%/frame; edge entry (the low-pass drives applied past `max` within 1â€“2 frames) fires while `_tpVel â‰ˆ -2400` â†’ latched, survives the ~80-frame decay to settle â†’ `OverscrollVel = -2400 Ã— 0.45 â‰ˆ -1080 px/s` â†’ overshoot fires.

### Fix B1 (PRIMARY)

`src/FluentGpu.Engine/Input/InputDispatcher.cs`:

1. **New field** (beside the `_tp*` fields, ~line 65):
   ```csharp
   private float _tpEdgeVel;  // coast velocity latched at the instant demand first crossed the edge â€” survives decay-to-settle to seed a lively spring bounce (touch-fling parity)
   ```
2. **New knob** (near the `s_tp*` statics):
   ```csharp
   private static readonly float s_tpFwdBounceVel =
       float.TryParse(Environment.GetEnvironmentVariable("FG_TP_FWDBOUNCE"),
           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
           out float fb) && fb > 0f ? fb : 200f;   // = Chromium kIgnoreForwardBounceVelocityThreshold
   ```
3. **Edge-entry latch in `ApplyTouchPan`** â€” insert immediately after `float oldBand = sc.OverscrollPx;` (line 2614), **before** `sc.OverscrollPx = band;` (2616):
   ```csharp
   if (band != 0f && oldBand == 0f) _tpEdgeVel = _tpVel;   // entering the band â€” latch the live velocity while it is still large
   ```
4. **Settle-time seed in `TickTouchpad`** â€” replace line 2425 (`sc.Overscrolling = false; sc.OverscrollVel = 0f;`) with:
   ```csharp
   sc.Overscrolling = false;
   sc.OverscrollVel = (MathF.Abs(_tpEdgeVel) >= s_tpFwdBounceVel ? _tpEdgeVel : 0f) * OverscrollPhysics.MomentumSpringCoupling;
   _tpEdgeVel = 0f;
   ```
   (keep the existing `OnScrollArmed?.Invoke(_tpTarget);` and the `FG_SCROLL_LOG` line after it.)
5. **Hygiene** (line 1153 reset block): add `_tpEdgeVel = 0f;` so a recycled dispatcher can't carry a stale latch.

**Why NOT `SeedFromEdgeMomentum` on the touchpad path:** it *overwrites* the band via `BandFromExcess(v/k)` â€” at `v â‰ˆ 2400`, `k â‰ˆ 3` the excess saturates to the cap, a visible discontinuity from the physically-correct band the coast already built. The coast band is already at its true displaced value; only the initial **velocity** is missing. Seed `OverscrollVel` only.

**Why it's safe:** two float fields, no per-frame heap. `gate.touchpad.alloc-zero` and `gate.touch4.snap-overscroll-alloc-zero` drive **interior** pans that never reach an edge (`OverscrollPx` stays 0), so the new settle branch (gated `if (sc.OverscrollPx != 0f)`, line 2423) is never taken under test. `StepSpring` still hard-snaps to **exactly 0** (`OverscrollPhysics.cs:120`) for any seeded velocity â†’ every "final band == 0" assertion holds. Feel constants untouched (`MomentumSpringCoupling = 0.45` reused â€” it's the *same* constant the touch fling uses, so the touchpad bounce now matches the touch fling exactly = more WinUI-faithful). Bounce stays capped at the 10%-viewport `BandLimit` (the spring decays an already-band-capped displacement).

### Fix B2 (SECONDARY â€” band depth, P3)

`band-saturates-below-cap` (verdict PARTIAL â€” real but a "feels shallower," not "buggy"). `ClampTpRaw` clamps raw to `[-bandLimit, maxOff+bandLimit]` with `bandLimit = 0.1Â·viewport`; `BandFromExcess(limit)` = `(2/3)Â·limit` â‡’ the touchpad band tops at **~6.67%** viewport, never the 10% cap the touch path can approach. Widen the demand-clamp headroom using the **gate-pinned exact inverse**:

```csharp
private static readonly float s_tpBandHeadroom =
    float.TryParse(Environment.GetEnvironmentVariable("FG_TP_BAND_HEADROOM"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
        out float bh) && bh > 0f && bh < 1f ? bh : 0.9f;   // guard (0,1) like s_tpDecayPerS
```
In `ClampTpRaw`:
```csharp
float vpExtent  = _tpHoriz ? sc.ViewportW : sc.ViewportH;
float bandLimit = OverscrollPhysics.BandLimit(vpExtent);
float excessMax = OverscrollPhysics.ExcessFromBand(s_tpBandHeadroom * bandLimit, vpExtent);
return Math.Clamp(raw, -excessMax, maxOff + excessMax);
```

**Default 0.90, not 0.98.** At 0.98 the demand ceiling is â‰ˆ1.92Â·limit, adding ~0.92Â·bandLimit (~42 px on a 460 px viewport) of reverse dead-travel before re-entering range. At 0.90 it is â‰ˆ1.286Â·limit (only ~0.29Â·bandLimit extra) while still lifting the ceiling from 67%â†’90% of cap â€” most of the depth gain for a third of the reverse-lag cost. Computing the ceiling via `ExcessFromBand` exercises the **same** pinned inverse the `band-roundtrip` gate protects (cannot drift it); no curve math changes; `< 1.0` so it never tries to exceed the cap.

### Fix B3 (SECONDARY â€” spring stiffness, P3, deferred)

`spring-omega-too-soft` (verdict CONFIRMED â€” secondary). `SpringOmegaRadPerS = 42` (`OverscrollPhysics.cs:26`) settles to 2% in `5.8335/Ï‰ = 139 ms` vs the Windows ~50 ms-period reference (Ï‰ â‰ˆ 126). The recoil lingers â€” **but only audible once B1 makes a bounce exist.** Already an env knob `FG_OS_OMEGA`.

- Land B1 first (no overshoot to feel until then).
- On-device A/B `FG_OS_OMEGA âˆˆ {60,70,80,90}` against a real WinUI ScrollViewer; start ~70, ship-candidate ~70â€“80. **Keep `FG_OS_ZETA = 1`** (the no-overshoot critically-damped contract; also what makes `5.8335/Ï‰` exact). **Ceiling Ï‰ â‰¤ 126** â€” past that the snap-home clause fires almost instantly and the bounce reads as a hard stop (defeats "keep the rubber-band").
- If a new default is chosen, edit only the `42f` literal â€” it propagates structurally to both `ScrollTuning.WinUiLike` and `HeadlessGolden` via the `with` (`ScrollTuning.cs:38/45`), so no second edit, no gate perturbation. Do NOT special-case touchpad vs touch spring rate (that would split HeadlessGolden from WinUiLike).
- Pair-tune `FG_OS_MOMENTUM` (0.45) since on the touch path it sets the seeded `bandVel`.

> Note: the "corpus conflict" cited in the input (InteractionTracker-says-parity vs Apple-says-raise) is a **prior-conversation artifact** â€” no such text exists in `design/`. Treat Ï‰ purely as an un-pinned feel value; that's exactly why a blind hard-change is wrong and on-device A/B is right.

### Fix B4 (MINOR â€” edge-entry start lag, P5, deferred & possibly unnecessary)

`edge-entry-applied-lag` (verdict PARTIAL, narrow). `ApplyTouchPan` computes excess from the **low-passed applied** offset (lags demand by ~tau), so at the instant demand crosses the edge applied may still be in-range â†’ `excess=0, band=0` for ~1 frame ("nothing, then it gives"). On *this* device the large per-packet deltas usually carry applied past the edge same-frame, so the hitch is **intermittent**. Likely **masked once B1+B3 land.** If still felt after: lower `FG_TP_SMOOTH_TAU` (line 158) 14â†’**12** (floor 10 â€” below that erodes the de-jitter the filter exists for). Verify with `FG_SCROLL_LOG=1` and count `excess=0 band=0.0` frames at edge entry; **ship no change** if there are zero. Do NOT do the logic option (demand-blend at the edge) â€” crosses the value-not-logic line and risks re-introducing jitter.

### Refuted symptom-B cause (do not re-attempt)
- **H4 spring-fight (double-write):** REFUTED. Execution order is `TickTouchpad` â†’ `_scrollAnim.Tick` (`AppHost.cs:1129â€“1130`); `ApplyTouchPan` sets `Overscrolling = (band != 0f)` unconditionally first; the spring block (`ScrollAnimator.cs:352`) is gated `(OverscrollPx!=0 || OverscrollVel!=0) && !Overscrolling`. Every band-field writer in `src/` was audited â€” exactly **one** writer per frame at every lifecycle point. The literal fight cannot occur. Do not "fix" the `Overscrolling` flag â€” it's correct, and B1 depends on it staying correct.

---

## Device-variance & the smoothness ceiling

- **No DPI bug â€” REFUTED.** `Win32Platform.cs` hi-res branch (`dip = sign(notch)Â·tamedÂ·s_tpScale`) correctly omits `/_scale`: the `wParam` wheel delta is an abstract `WHEEL_DELTA` multiple (a rotation/scroll count, **not** a DPI-virtualized screen coordinate â€” unlike `lParam` positions, which `WheelPt` correctly *does* `/_scale`). `ScrollState` is entirely DIP (`Columns.cs:176â€“179`); `WM_DPICHANGED` keeps the apparent DIP size constant. So scroll fraction = `dip/viewportDIP` is **already DPI-invariant** â€” the same finger motion scrolls the same fraction at any DPI. A `pow(_scale, FG_TP_DPI_EXP)` correction would *introduce* DPI dependence (wrong, opposite of WinUI). **Do not add it.**
- **The real device axis is driver acceleration, not display DPI.** The soft-knee (`s_tpKnee=240` linear, `s_tpMaxRaw=1600` asymptote, `s_tpScale=0.08`) tames this machine's huge -400..-3400/packet deltas; a low-accel touchpad's 1..119 deltas sit *below* the knee. These are **already env knobs** (`FG_TP_KNEE`, `FG_TP_MAXRAW`, `FG_TP_SCALE`). Document: high-acceleration devices need `FG_TP_KNEE` raised proportionally (~400â€“600) to keep the linear region over normal-speed scrolling. The widened velocity window (A2) and the quiet-guard (A1) are the cross-device robustness levers â€” make the defaults conservative (`FG_TP_SETTLE_QUIET` ~120 ms tolerates >90 ms bursts).
- **Resampling analogue:** the one-pole low-pass `_tpAppliedRaw += (demand-applied)Â·(1-e^(-dt/tau))` *is* our Chromium-`ScrollPredictor`-equivalent inter-frame smoother. No event-queue resampler is needed.
- **dt-spike lurch (the general ceiling, P4, deferred, inert-by-default):** live dt is wall-clock capped at 34 ms (`FrameTimeSource.cs:55`); at the cap `kOff â‰ˆ 0.91` (near-snap) and `CoastStep` integrates 34 ms in one visible step. A mid-scroll GC/realize stall therefore lurches, and the Resync guard doesn't catch it (`_lastWaitMs != 0` is false mid-scroll). Ship **two dormant value sub-caps** (both default to today's 34 â‡’ behaviorally inert; headless uses `FixedFrameTimeSource(16 ms)` so they never engage under test):
  - `FG_TP_COAST_MAXDT` â€” split the coast dt into â‰¤N-ms substeps before `CoastStep`, mirroring `StepSpring`'s existing 16 ms-substep loop. Mathematically exact (telescopes: `CoastStep(v,h1)+CoastStep(v1,h2) = v0(1-decay^(h1+h2))/k`) so frame-rate-independence is preserved.
  - `FG_TP_SMOOTH_MAXDT` â€” cap only the *filter's* dt: `kDt = MathF.Min(dtMs, s_tpSmoothMaxDt); kOff = 1 - MathF.Exp(-kDt / s_tpSmoothTau);` while demand/coast advance at full dtMs (distance stays correct). **Prefer this over the two-speed-tau** the input proposed: a magnitude-keyed slow tau also lags a *legitimately* large fast-pan, whereas a flat dt cap only limits the spiked frame.
- **Fractional-pixel glyph-snap shimmer (P-lowest):** plausible 0.5 device-px baseline jump on the trailing settle frame, unverified; defer. If pursued, `FG_TP_SETTLE_PXSNAP` must snap the **offset only, never the band** (the band gate asserts final = exactly 0).

---

## Prioritized change list

| P | File | Change | Knob (default) | Risk |
|---|---|---|---|---|
| **1** | `InputDispatcher.cs` 2421 + new `_tpQuietMs` field/reset | **Settle quiet-guard** â€” accumulate `_tpQuietMs` on coast, reset on packet, require `> s_tpSettleQuietMs` before settle. **Sufficient fix for symptom A.** | `FG_TP_SETTLE_QUIET` (**120**) | Low â€” float, frame-driven, replay-identical; keeps gesture armed longer |
| **1** | `InputDispatcher.cs` 2614/2425 + new `_tpEdgeVel` field/reset | **Edge-velocity seed** â€” latch `_tpVel` at edge entry; at settle `OverscrollVel = (|edgeVel|â‰¥thr ? edgeVel : 0)Â·0.45`. **Fix for symptom B.** | `FG_TP_FWDBOUNCE` (**200** DIP/s) | Low â€” 2 floats; settle branch dormant under test (interior pans) |
| **2** | `InputDispatcher.cs` `TouchVelocity` 216/217/220/276 + `Reset` signature | **Per-instance velocity window** 50â†’90 (touchpad only; touch stays 50), `Cap`+`InlineArray` 8â†’12, `if(m<2) return;`. Inter-burst coast continuity. | `FG_TP_VEL_WINDOW` (**90**) | Medium â€” must (a) keep window per-instance & re-set in `Reset` (default-wipe bug), (b) update `Cap`+`InlineArray` literal in lockstep, (c) re-run both alloc-zero gates |
| **3** | `InputDispatcher.cs` `ClampTpRaw` | **Band headroom** â€” clamp to `Â±ExcessFromBand(headroomÂ·bandLimit)` so band reaches ~90% of the 10% cap (was 67%). | `FG_TP_BAND_HEADROOM` (**0.90**, guard (0,1)) | Low â€” pure float via gate-pinned inverse; >1.0 must fall back to default |
| **3** | `OverscrollPhysics.cs` 26 (literal only) | **Spring Ï‰** 42â†’~70â€“80 after on-device A/B (keep Î¶=1, ceiling 126). Land *after* B1. | `FG_OS_OMEGA` (A/B; ship-candidate **~70â€“80**) | Low â€” value-only; propagates to both feel profiles via `with`; no gate pins Ï‰ |
| **4** | `InputDispatcher.cs` coast branch + filter | **dt sub-caps** (coast substep + filter dt cap), inert at default 34. | `FG_TP_COAST_MAXDT` (**34**), `FG_TP_SMOOTH_MAXDT` (**34**) | Low â€” dormant by default; coast substep telescopes exactly |
| **5** | `InputDispatcher.cs` 158 (literal only) | **Smooth tau** 14â†’12 *only if* edge-entry hitch persists after B1/B3. | `FG_TP_SMOOTH_TAU` (**12**, floor 10) | Low â€” value-only; verify need with `FG_SCROLL_LOG` first, likely no change |

**New regression gate (required â€” the blind spot that let this ship):** extend `DriveTouchpadPan` / add `gate.touchpad.inter-burst-no-restart` that drives â‰¥3 bursts spaced **>WindowMs** apart (~70â€“80 ms, multiple `RunFrame`s per gap) and asserts the gesture does **not** restart mid-stream â€” `TouchpadActive` stays true across the gap, offset advances monotonically with no backward jump, `_tpVel` does not return to 0 between bursts (A2).

---

## Verification plan

**Headless gates to keep green** (`dotnet build src/FluentGpu.slnx` clean â†’ `dotnet run --project src/FluentGpu.VerticalSlice` â†’ "ALL CHECKS PASSED"):
- `gate.touchpad.alloc-zero` **and** `gate.touch.fling-alloc-steady-zero` / `gate.touch4.snap-overscroll-alloc-zero` â€” must stay **0 B** (especially after Cap 8â†’12 grows `PointerSlot`).
- `gate.touchpad.settle-determinism` â€” replay `(off, ticks)` bit-identical; `off > 5f` still holds (the quiet-guard delays settle a few frames â€” confirm coast+quiet completes within the 200/600-frame budgets).
- `gate.touchpad.coast-distance`, `frame-rate-independence` (spread < 0.5), `band-roundtrip` (`ExcessFromBandâˆ˜BandFromExcess` < 0.5 px), `gate.touch4.snap-fling-dt-invariant`, `gate.scroll.overscroll-physics` (final band lands **exactly 0**).
- The **new inter-burst gate** above.

**On-device `FG_SCROLL_LOG=1` traces** (`src/FluentGpu.Engine/Foundation/ScrollLog.cs`):
1. **Symptom A confirmation:** scroll a long list at a deliberate pace; confirm baseline shows `TPEND`/restart churn between bursts, and after A1 the `TPPAN` stream stays one continuous gesture across gaps (no `_tpTarget` clear, no re-`Reset`). Measure the real inter-burst gap (sets whether 90/120/150 ms is right).
2. **Symptom B confirmation:** glide hard into the top/bottom edge; baseline `TPEND` shows `OverscrollVel = 0` â†’ flat spring; after B1 the `TPEND` line reports a **non-zero seed** and the subsequent `spring band=â€¦ v=<nonzero>` shows an overshoot.
3. **Edge-entry lag (B4):** count `excess=0 band=0.0` frames at the instant of edge entry; ship the tau change only if a perceptible hitch remains after B1/B3.

**On-device knob sweep (post-land):**
- `FG_TP_SETTLE_QUIET` âˆˆ {90, 120, 150} â€” pick the smallest that never restarts on the slowest deliberate drag the target devices produce.
- `FG_OS_OMEGA` âˆˆ {60, 70, 80, 90} vs a real WinUI ScrollViewer (Î¶=1 fixed, â‰¤126).
- `FG_TP_BAND_HEADROOM` âˆˆ {0.85, 0.90, 0.95} â€” depth vs reverse dead-zone.
- `FG_TP_VEL_WINDOW` âˆˆ {90, 100, 120} and (if a low-accel touchpad is available) `FG_TP_KNEE` âˆˆ {240, 400, 600}.

---

## Risks & open questions

- **Land the two P1 fixes first and verify on-device they jointly clear both symptoms** â€” A1 (quiet-guard) is the sufficient symptom-A fix; B1 (edge-velocity seed) is the symptom-B fix. A2/B2/B3 are smoothness/feel improvements layered on top, not prerequisites.
- **A2 default-wipe trap (must not be missed):** if the velocity window becomes an instance field, `ClearWorkingScalars`' `= default` zeroes it. The window **must** be (re)established in `Reset` (carry it as a parameter). A bare field initializer is a real regression.
- **Cap 8â†’12 struct-size:** the `[InlineArray]` literal and `const Cap` must move together; re-run both alloc-zero gates and confirm no `stackalloc`/struct-size assumption keyed on the old `PointerSlot` size breaks.
- **`settle-determinism` re-baseline is reasoned, not yet executed** â€” the gate compares two live `Run()`s with no hardcoded literal, so a later settle should shift `ticks` identically on both. **Must actually run it** after the changes to confirm `(off, ticks)` is still bit-identical and `off > 5f`.
- **Inter-burst gap is unmeasured** â€” the 90/120/150 ms window/quiet defaults are best-guesses pending the `FG_SCROLL_LOG` measurement of the real cadence. Also unconfirmed: whether bursts genuinely collide in a single 1 ms `GetMessageTime` tick (gates whether the deferred same-tick timestamp work is worth doing â€” currently judged not).
- **Device variance is inferred, not tested** â€” no second physical touchpad/DPI was exercised. The driver-acceleration knobs (`FG_TP_KNEE`/`FG_TP_MAXRAW`) exist; they need a real low-accel device to validate the documented guidance.
- **Out of scope (recorded so they aren't lost):** nested-scroller handoff (`ScrollableUnderForAxis` tests total overflow, not remaining travel) â€” a real defect, unrelated to A/B; wheel-vs-touchpad coast decay asymmetry (`0.00002` vs `0.05`) â€” confirm it's a deliberate design choice before any "unify the feel" request; glyph-snap settle shimmer â€” deferred until confirmed on-device.
