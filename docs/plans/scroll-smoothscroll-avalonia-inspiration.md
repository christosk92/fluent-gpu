# SmoothScroll.Avalonia — inspiration for the fluent-gpu scroller

Source: `C:\WAVEE\SmoothScroll.Avalonia` — a clean-room, **fully managed** reimplementation of WinUI's
`InteractionTracker` + `ScrollView` for Avalonia (credits: Meloman19/CompositionScroll, unoplatform/uno). It is an
InteractionTracker **state machine** (`Idle` / `Interacting` / `Inertia` / `CustomAnimation`) with per-input inertia
handlers, a Flutter-derived velocity tracker, elastic overpan, and a physics bounce — i.e. the exact "WinUI feel without
WUC" target fluent-gpu is chasing, built independently.

> **It corroborates Workflow 1.** Two engines arriving at the same constants/mechanism independently is the strongest
> evidence we can get short of on-device. Where SmoothScroll matches our verified plan, treat it as confirmation; where
> it differs, it's a design option to weigh.

---

## Headline insight: the engine-owned path is validated, and the "blocked InteractionTracker" memory is too pessimistic

Our memory `touchpad-scroll-dm-vs-interactiontracker` records that adopting WinUI's `InteractionTracker` is *blocked*
because `VisualInteractionSource` needs Windows.UI.Composition to own the HWND (conflicts with our DComp renderer).
SmoothScroll.Avalonia shows the **math + state machine are fully portable managed C#** — it reimplements IT from scratch
and only "hacks" Avalonia's composition renderer for the *output* plumbing. fluent-gpu already chose the engine-owned
scroller; SmoothScroll is the same bet, executed as a tracker state machine. So the takeaway is **not** "adopt WUC" — it's
"our architecture is right; borrow its proven math and consider its cleaner state-machine structure."

---

## Technique-by-technique mapping

| # | SmoothScroll technique | Constant/formula | fluent-gpu today | Verdict |
|---|---|---|---|---|
| 1 | **Velocity tracker horizon** | `HorizonMilliseconds = 100`, `HistorySize = 20`, degree-2 LSQ | `WindowMs = 50`, `Cap = 8` | **Confirms A2** — widen window to ~100, history to ~12+ |
| 2 | **Velocity linear fallback** | `sampleCount>1 && <3` → return `distance/duration`, never hard-0 | `m<2` → `_vx=_vy=0` (hard zero) | **Confirms A2** — soften the hard-zero |
| 3 | **Gap-break** | `AssumePointerMoveStoppedMilliseconds = 40`: a >40ms inter-sample gap ends the fit window | none (we coast through gaps) | **New idea** — a clean "is the finger still moving?" test |
| 4 | **Pan inertia decay** | `DecayRate = 1 − 0.95 = 0.05`; `minimumVelocity = 30` | `s_tpDecayPerS = 0.05`, `s_tpSettleVel = 30` | **Parity confirmed — do not change** |
| 5 | **Closed-form coast** | `Δ = ((decay^t − 1)·v0)/ln(decay)` | `CoastStep`: `v·(1−decay^dt)/k` | **Identical** (both dt-invariant) |
| 6 | **Finite, precomputed inertia duration** | `TimeToMinimumVelocity = (ln(vMin) − ln(v0))/ln(decay)`, end snaps to `FinalModifiedValue` | per-frame "settle when \|v\|<30" gate (the A-bug) | **Strong alternative** — see "Borrow" §A |
| 7 | **Momentum→bounce handoff** | At edge-crossing latch `v_edge = v0·decay^elapsed`, then critically-damped spring seeded with `v_edge` | settle seeds `OverscrollVel = 0` (the B-bug) | **Confirms B1 exactly** |
| 8 | **Analytic critically-damped spring** | `y(t) = (y0 + (v0 + ωn·y0)·t)·e^(−ωn·t)`; `ωn = 5.8335 / settlingTime` | `StepSpring`: numerical semi-implicit Euler, 16ms substeps, fixed `ω=42` | **Cleaner** — closed-form is exactly dt-invariant; ωn is *adaptive* |
| 9 | **Live overpan elastic curve** | `offset = (d/(d+2000))·2000·0.5` (tension 0.5, ref 2000), asymptote 1000px, **with exact inverse** | `BandFromExcess`: `soft·raw/(raw+soft)`, `soft=2·limit`, cap `0.1·viewport`, **with `ExcessFromBand` inverse** | **Same family** — ours is viewport-relative (better); their separation pattern is worth borrowing |
| 10 | **Operate in un-elastic space** | Interacting-state stores position via `GetOriginalPoint` (un-resist), re-applies `GetElasticPoint` on output | we displace a separate band over the clamped offset | Different decomposition; ours is fine |
| 11 | **Wheel inertia model** | half-life `0.08s` (τ≈0.1155s) exponential; `StopVelocityThreshold=2`; `MaxDuration=1s` | `WheelEaseTau=18ms` TargetChase + `WheelFlingDecayPerS=0.00002` | **Cleaner, coherent** — feeds the accel/decel + consistency axes |
| 12 | **Same-direction velocity accumulation** | wheel: `v += target` if same dir, else reset; `target = (remainingDist + newDelta)/0.25` | we have a `WheelFlingMode` same-dir accumulate, different math | **Refines** our wheel-fling |
| 13 | **Touchpad detection** | precision touchpad ⇔ delta **not** near-integer (`!AreClose(abs,(int)abs)`) | hi-res ⇔ `notch % 120 != 0` | Portable corroborating heuristic |
| 14 | **Touchpad scale** | `TouchpadDeltaScale = 48`; mouse `MouseWheelDeltaScale = 128` | `s_tpScale = 0.08`·tamed-notch; wheel `max(48, 15%·vp)` | Reference magnitudes |
| 15 | **Nested-scroller chaining** | explicit `InteractionChainingMode {Auto,Always,Never}` + `IsAtBoundaryForChaining` (hand off to parent at min/max) | `ScrollableUnderForAxis` binds inner-at-max (the WF1-flagged defect) | **Clean reference** for that out-of-scope defect |
| 16 | **Velocity clamps** | `MinFling=50`, `MaxFling=8000`, `MaxPointerWheelVelocity=8000` | `s_tpMaxVel=3500`, no min-fling on touchpad | Compare ceilings |

---

## The most important architectural difference (read before borrowing)

**SmoothScroll's touchpad path does NOT synthesize a coast.** `HandlePrecisionTouchpadScroll` applies each packet as
`BeginUserManipulation → ApplyManipulationDelta(−delta) → CompleteUserManipulation` (the complete transitions to inertia
with **zero** velocity). It **trusts the OS precision-touchpad driver** to deliver the momentum/inertia deltas after lift.
That's why it has no "stutter between bursts" problem — it never tries to bridge a gap.

fluent-gpu deliberately does the opposite: it **synthesizes** the coast from the velocity regression, *because this
device's promoted-wheel deltas are accelerated/bursty and feel wrong applied 1:1* (the whole reason the soft-knee +
low-pass + engine coast exist). So **don't blindly copy the no-coast model** — our synthetic coast is a deliberate
response to a real device defect, and it's what gives us the engine-owned feel. The bug isn't that we coast; it's the
*settle gate* (A) and the *velocity-0 bounce* (B). Borrow the math that makes our coast robust, not the decision to drop it.

(Worth a future experiment: a device-class switch — pass-through 1:1 on well-behaved touchpads, synth-coast on
accelerated ones. Out of scope for the current fixes; recorded as an idea.)

---

## Borrow / Adapt / Skip

### Borrow (high-value, low-risk, reinforces the existing plans)
- **(A2, item 1+2)** Widen `TouchVelocity` to a **100ms horizon** with a **linear fallback** when `2 ≤ samples < 3`
  (return `distance/duration`, don't zero). This is now *doubly* sourced (Workflow 1 research + a shipping
  implementation). Keep it touchpad-private as WF1 specified. SmoothScroll's `HistorySize=20`/degree-2 supports raising
  our `Cap` to ~12–16. Their solver uses `stackalloc` (zero-heap) — our `[InlineArray]` struct is the AOT-equivalent.
- **(B1, items 7+8)** The momentum-into-spring bounce: latch the velocity *at edge crossing* (`v_edge = v0·decay^elapsed`)
  and seed the critically-damped spring with it. SmoothScroll's `_initialDampingVelocity` is literally WF1's `_tpEdgeVel`.
  **Plus an upgrade:** consider replacing our numerical `StepSpring` with their **closed-form** solution
  `y(t)=(y0+(v0+ωn·y0)·t)·e^(−ωn·t)` — exactly dt-invariant (no substep loop, friendlier to the dt-invariance gate).

### Adapt (medium-value, needs our-engine fitting; feeds the accel/decel workflow)
- **(item 6)** Precompute a **finite inertia duration** (`TimeToMinimumVelocity`) and snap to the final value at the end,
  instead of the per-frame "settle when |v|<30" gate. This is a *structurally cleaner* alternative to WF1's quiet-guard
  (A1): a deterministic end-time can't "fire in an inter-burst gap" because it isn't a per-frame race. Weigh A1
  (minimal, surgical) vs this (more invasive, more principled) — A1 is the right first ship; this is the longer-term shape.
- **(items 11+12)** Replace our wheel `TargetChase(18ms) + decay 0.00002` with SmoothScroll's **half-life exponential**
  (`τ≈0.1155s`) + **distance-based same-direction accumulation** (`target=(remaining+newDelta)/0.25`). This directly
  addresses the accel/decel workflow's wheel-vs-touchpad **consistency** axis: both inputs become coherent
  exponential-decay inertia, differing only in time-constant (a *deliberate* difference, which answers WF1's open
  question about the `0.00002` vs `0.05` asymmetry — SmoothScroll keeps them different on purpose).
- **(item 9)** Their elastic asymptote is a fixed `1000px` (ref 2000 × tension 0.5); ours is viewport-relative
  (`0.1·viewport`) which is **better** for varying viewport sizes — keep ours. But note `tension=0.5` as a felt
  reference point when tuning `FG_TP_BAND_HEADROOM` / the resistance curve.

### Skip (doesn't fit our model or is Avalonia-specific)
- The Avalonia `ServerObject` / `ServerCompositor` / `Stopwatch`-per-handler plumbing — wall-clock `Stopwatch` would break
  our replay-deterministic, dt-driven gates. Keep our frame-`dtMs`-driven integrators.
- The per-gesture `new VelocityTracker()` + `VelocityEstimate`/`PolynomialFit` heap records — fine for Avalonia, but we
  must keep the zero-alloc struct/`InlineArray` form for phases 6–13.
- The no-coast touchpad philosophy (see the architectural-difference section).

---

## How this feeds the two workflows

- **Workflow 1 (rubber-band + stutter):** items 1,2 reinforce **A2**; items 7,8 reinforce **B1** and offer a closed-form
  spring upgrade. No change to the verified P1 plan — this is corroboration plus two optional refinements.
- **Workflow 2 (accel/decel, running):** items 6, 11, 12 are direct input to the **deceleration-curve** and
  **cross-input consistency** axes. When WF2 returns, reconcile its synthesized curve against SmoothScroll's
  half-life-wheel / 0.95-pan split and the finite-duration inertia. The `0.00002` vs `0.05` asymmetry WF2 was told to
  "confirm is deliberate" — SmoothScroll's answer is **yes, deliberate** (wheel τ≈0.1155s vs pan WinUI-0.95), but our
  current wheel *model* (TargetChase) is the part worth replacing, not the fact that wheel ≠ pan.

## References
- `Helpers/VelocityTracker.cs` — Flutter velocity tracker port (horizon, fallback, LSQ).
- `States/Inertia/ActiveInputInertiaHandler.cs` — pan inertia + the momentum→critically-damped-spring bounce (`DampingHelper`).
- `States/Inertia/PointerWheelInertiaHandler.cs` + `InertiaState.ApplyWheelDelta` — wheel half-life inertia + accumulation.
- `States/Interacting/InteractingState.cs` — live elastic overpan (`GetElasticPoint`/`GetOriginalPoint`, tension 0.5).
- `Source/InputElementInteractionSource.cs` — input ingestion, touchpad detection, chaining.
- Upstream lineage: github.com/Meloman19/CompositionScroll, github.com/unoplatform/uno (Uno's InteractionTracker).
