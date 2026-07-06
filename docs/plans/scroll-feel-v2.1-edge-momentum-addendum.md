# Scroll Feel v2.1 — edge / momentum layer addendum

Status: **DECIDED — amends `scroll-feel-rework-v2-design.md` §4.4/§4.5/§7 and supersedes the 2026-07-02 on-device hotfixes where it differs.** Grounds every edge/momentum choice in the reverse-engineered iOS/WebKit/Chromium/Android/Flutter overscroll literature and the DirectManipulation-in-browsers survey (citations inline). The v2 body (single writer, per-frame resample, closed-form fling, the state enum) is unchanged; this addendum only pins the *edge bounce*, the *OS-momentum tail*, the *DM producer geometry*, and the *re-grab* math that the hotfixes left as feel numbers.

On-device (2026-07-02, PTP + DManip): F1 sign, F2 band-freeze on OS tail, F3 wheel-hijack at runway exhaustion, F4 pinch-drift leak, F5 band-zeroed on lift-at-stretch, F6 band-teleport from position seeding, F7 inverse divergence near the asymptote, F8 relatch catch-up burst. The research verdict is blunt: **no shipping system position-seeds a bounce, none stops its producer at the edge, none clamps a per-frame catch-up, and the two browsers never call `SetContentRect` — so F3 and F4 are self-inflicted by our 200k runway, not DM defects.** The decisions below act on that.

---

## A.1 Bounce-from-momentum — velocity-only, WebKit closed form (Decision 1)

> **The edge bounce is a critically-damped (ζ=1) spring seeded VELOCITY-ONLY at the current stretch; position is never touched.** This is the universal pattern: iOS seeds a ζ=1 spring at offset 0 with the velocity at the analytically-solved edge-crossing instant (Lobanov / Apple 9448633); Chromium seeds `initial_stretch = StretchAmount` (current) + `initial_velocity = scroll_velocity` (`elastic_overscroll_controller.cc`); Flutter seeds the spring *at* the extent with the friction curve's `dx`, **position untouched** (`scroll_simulation.dart`); Android `EdgeEffect.onAbsorb` seeds only velocity. F6's position seeding from projected coast `v/k` is the outlier and is deleted.

The engine's `SnapBackOmega = 12.5 rad/s` is not incidental — a critically-damped `v0·t·e^(−ωt)` at ω=12.5 is **algebraically identical** to WebKit's momentum bounce `(v0·a)·t·e^(−(s/p)t)` because `s/p = 20/1.6 = 12.5`. So seeding coupling `γ = a = 0.31` makes our bounce the WebKit exponential *exactly*, and reproduces its stated ~9 px excursion per 1000 px/s. This replaces the ad-hoc `MomentumSpringCoupling = 0.45`.

```
d        = BandAsymptoteFraction · viewport          // 0.15·vp — the rubber-band asymptote (§4.4)
seedVel  = clamp( γ · v_edge , ±Vcap )               // γ = 0.31 (WebKit a); v_edge from §A.2
Vcap     = Cpeak · d · ω · e                          // ω = 12.5, e = 2.71828, Cpeak = 0.6
band(t)  = current stretch, evolved by StepSpring(ω=12.5, ζ=1)   // position seed = current, NOT v/k
peak     = seedVel / (ω · e)   ≤  Cpeak · d           // exact overshoot of v0·t·e^(−ωt) at t = 1/ω
```

`Vcap` is the **exact peak-overshoot bound** (`peak = seedVel/(ω·e)`, the maximum of `v0·t·e^(−ωt)`), so `Cpeak` is literally "deepest the bounce may reach as a fraction of the asymptote." `Cpeak = 0.6` keeps every bounce strictly below `d`, which is what makes A.4's inverse safe. **Honesty:** `γ = 0.31` is fully grounded (WebKit parity); `Cpeak = 0.6` is a feel constant — the soft-spring-into-asymptotic-band composition is *ours*, no shipping system stacks these two, so we keep the simplest value that guarantees `peak < d` rather than invent a pedigree. With `γ = 0.31` the cap only bites on small viewports (at max seed 8000 px/s, natural peak ≈ 73 px), so on a normal viewport the bounce is pure WebKit and the cap is only F7 insurance.

**Changes vs code.** `OverscrollPhysics.SeedFromEdgeMomentum` (`OverscrollPhysics.cs:94`) is rewritten to **velocity-only**: leave `bandPx` at the passed-in current stretch, set `bandVel = clamp(0.31·v, ±0.6·d·ω·e)`, never shrink an existing `bandVel`. `MomentumSpringCoupling 0.45 → 0.31`. This single rewrite fixes the audit's live residual — **the engine-fling→clamp path (`ScrollIntegrator.cs:380`) still position-seeds**, so F6 was only fixed on the OS-owned path; unifying `SeedFromEdgeMomentum` fixes both. The OS-owned edge tick (`ScrollIntegrator.cs:489-495`) drops its inlined `vCap`/coupling arithmetic and just calls the rewritten `SeedFromEdgeMomentum`.

**Stays.** ω=12.5, ζ=1, the `StepSpring` closed form, the never-shrink guard, the `sign(v_edge)==sign(excess)` gate, the ≤0.5 DIP / ≤8 px/s early-home snap.

---

## A.2 Edge-crossing tail velocity — sample-timestamp, not per-frame displacement (Decision 2)

> **Velocity at the edge is estimated over sample timestamps, never as this-frame displacement `(xStar − XAppliedLast)/dt`.** WebKit `m_momentumVelocity = eventDelta/eventDt` zeroed after the 100 ms `scrollVelocityZeroingTimeout`; Chromium `delta/dt` with 0.1 s zeroing skipping the first momentum event; Android/Flutter read the analytic friction-curve velocity at the crossing. **None sample per-frame displacement on a no-sample tick** — which is exactly F5's failure (a lift-at-stretch tick has no new sample, `(xStar−XAppliedLast)/dt ≈ 0`, and a 0 seed erased the band).

Decided estimate, from the two timestamped samples the resampler already retains (`_rs.T0,X0,T1,X1`):
```
v_edge = (X1 − X0) / (T1 − T0)                        // sample-timestamp slope (WebKit eventDelta/eventDt)
if (frameT − T1) > AssumeStoppedMs/1000 : v_edge = 0  // stale-tail zeroing (Android 40ms; WebKit uses 100ms)
v_edge = clamp(v_edge, ±FlingMaxVelocityPxPerS)       // ±8000 (Android max-fling), unchanged
```
This is the **same IMPULSE window the fallback fling estimator already uses** (§4.3) — one estimator, one gate. **Honesty:** WebKit's zeroing timeout is 100 ms; we deliberately reuse the fling estimator's `AssumeStoppedMs = 40 ms` (Android) for a single unified gate rather than introduce a second timeout — slightly more aggressive, intentionally consistent.

**Changes vs code.** `ScrollIntegrator.cs:477` — replace `vOs = clamp((xStar − _rs.XAppliedLast)/dtS, ±8000)` with the two-sample slope above (guarded by `_rs.Count ≥ 2` and the stale-tail zero). **Stays.** The ±8000 clamp, the sign/settle gates from §A.1.

---

## A.3 Do NOT stop DM inertia at the edge (Decision 3)

> **Keep the engine-side convert-then-ignore; never call `Viewport.Stop()` at a content edge.** The DManip survey is unambiguous: **neither Chromium nor Firefox stops DM at the real scroll extent** — `Stop()` is called only on destroy / resize / handler-swap; DM coasts freely and the app scroller absorbs. The edge policy is *engine-side ignore*: WebKit sets `m_ignoreMomentumScrolls` once the bounce starts and swallows the rest of the tail until `MomentumEnded`; Chromium enters `kStateMomentumAnimated` and ignores later momentum GSUs. This is precisely F2's landed fix (`PhaseOsOwned` → immediate `SnapBack` at the edge, then leave `TouchpadTracking` so the remaining tail samples are ignored). It is canonical; we keep it verbatim.

**Changes vs code.** None to the mechanism. One hardening: after the tick converts an OS tail to `SnapBack`, explicitly **drop** subsequent `MomentumUpdate` deltas of that stream at the dispatcher (an `_sgTailConverted` latch cleared at `MomentumEnd`/`ScrollBegin`) — today they still deposit resampler samples that go nowhere; dropping them makes "ignore the rest of the tail" match `m_ignoreMomentumScrolls` exactly and saves work. **Stays.** `PhaseOsOwned` (`InputDispatcher.cs:2724`), the `ScrollIntegrator.cs:469-503` conversion, no producer `Stop()`.

---

## A.4 Re-grab of a spring-driven band — fold via the exact inverse, now provably safe (Decision 4)

> **A contact landing on a live `SnapBack` band re-anchors at the CURRENT stretch and drives incrementally from there** — Chromium's `initial_stretch = StretchAmount`, iOS finger-down stop-on-contact. Our realization keeps the resampler's `rawOffset = anchor + Σδ` continuous by folding the current band's implied excess into the anchor: `anchor += ExcessFromBand(band)`. That fold IS "continue from the current stretch" expressed in excess-space, so it is the right mechanism — the only defect was F7, the inverse `x = f·d/(c(d−|f|))` diverging as `|f| → d`.

The fix is structural, not a bigger clamp: **A.1 caps every bounce peak at `Cpeak·d = 0.6·d < d`**, so a re-grab during a bounce evaluates `ExcessFromBand` at most at `0.6·d`, where `ExcessFromBand(0.6d) = 2.7·d` — finite, well inside the valid domain, and the `af ≥ d ⇒ 0.98d` guard (F7 hotfix, `OverscrollPhysics.cs:67`) is **never reached on a bounce**. Pulling back from a `0.6·d` stretch, the band falls responsively (at `excess = d`, band = `0.355·d`); the near-asymptote dead zone the audit flagged only existed because F6 drove the band to `0.96·d`. With velocity-only seeding it cannot get there.

**Changes vs code.** None to `InputDispatcher.cs:2781` (the fold) or the F7 guard — both become correct-and-unreached-on-bounce once A.1 lands. **Honesty:** the research did not surface iOS's exact catch-during-bounce anchor arithmetic; the excess-fold is our engine's realization of the documented "re-anchor at current stretch" and is safe *given* the A.1 cap, which is the honest dependency to state. The F7 divergence guard **stays** as defense-in-depth (unreachable on the bounce path, still correct for any future path that could push a band toward `d`).

---

## A.5 DM producer geometry — match the browsers, delete the runway (Decision 5)

> **Drop `SetContentRect` and the 200k runway; size the viewport to the window client rect; recenter only at `READY`.** Both browsers use a fake viewport = the real window rect and **never call `SetContentRect`** (`direct_manipulation_helper_win.cc`, `DirectManipulationOwner.cpp`); DM translation against the default content is effectively unbounded, so **runway exhaustion never occurs** — which means our F3 (wheel synthesis at exhaustion) *and* F4 (200k-origin amplifying pinch scale-drift into `origin·ds`) are both artifacts of a content rect the reference implementations don't have. Removing it dissolves both root causes at the source instead of defending their symptoms.

Decided producer shape (`Win32DirectManipulation.cs`):
- **No `SetContentRect`** (delete `:186-187`), **no** 200k `ContentSize` / centered `ZoomLo/Hi`. Viewport rect follows the window client rect.
- **Reset only at `READY`** (already correct, `:278`) via `ZoomToRect(0,0,w,h, animate=FALSE)`, **skipped when the transform is already identity** (both browsers' `OnViewportStatusChanged`); zero the offset cache so the async identity callback diffs to zero (`_haveBaseline=false` already does this). Never mid-gesture.
- **Delta extraction: keep content-space `p = −t/s`** (`:295-296`). At `s = 1` it equals Firefox's negated raw-translation diff (`last − cur`), i.e. the F1 sign convention, and it stays robust to residual scale drift even without the giant origin — a strict superset of Chromium's raw `(xform[4,5]−last)/DSF`. We *deliberately differ* from the browsers' raw diff here because content-space subsumes their convention and carries the sign fix; documented as intentional.
- **Pinch isolation: keep `|scale−1| > 0.01` ⇒ suppress pan** (`:309`). Matches both browsers classifying pinch-vs-pan purely by a scale epsilon and discarding translation while pinching (Chromium rel-eps 1e-5, Firefox `FuzzyEquals`); our 0.01 is coarser and fine once the origin no longer amplifies drift.

**Changes vs code.** Delete `SetContentRect` + the 200k geometry; drive the viewport/recenter rect off the window rect; add the identity-skip on recenter. **Honesty / sequencing:** our F3 trace observed synthesis at *our* 200k exhaustion; the research asserts the no-content-rect path is unbounded on the browsers' stacks. **Confirm on-device that dropping the content rect yields unbounded translation on our stack before deleting the F3 defense** (§A.7). **Stays.** The 1000×1000 fallback size if a window rect is unavailable; content-space diff; the pinch gate; the wedge watchdog; `RUNNING/INERTIA/READY` → phase-event mapping.

---

## A.6 Hitch / relatch catch-up — honest 1:1, no clamp (Decision 6, resolves F8)

> **Accept the honest 1:1 catch-up; do not spread or clamp a post-hitch delta.** The literature is unanimous: Chromium coalesces GSUs and applies the *summed* delta; resampling only shifts within `kMaxResampleTime = 20 ms` (≈1 vsync) and zeroes a delta only on predicted direction reversal; Android `OverScroller` and Flutter simulations evaluate at *absolute* time (a full jump after a long frame). **No shipping system has a max-per-frame delta clamp.** F8's single −193 DIP apply after a 34 ms hitch is exactly this industry-normal catch-up — the frame legitimately owes that displacement. Inventing a clamp would desync from the finger/OS truth and no reference supports it.

The relatch itself is already correct per the browsers: `INERTIA→RUNNING` is `MomentumEnd` + a fresh `ScrollBegin` **re-baselined from the last offsets, no delta replay** (both status handlers). Our DM producer re-baselines on `RUNNING` (`_haveBaseline=false`, first update emits nothing, `:265`) and the resampler re-anchors at `ScrollBegin` — so the post-hitch delta is measured from the correct base, not replayed. That is the whole fix.

**Changes vs code.** None — F8 moves from "unfixed, policy undecided" to **decided: accept it, it is native behavior**. The existing `dtClampMs = 34` frame-dt clamp and the `ResampleMaxPredictionMs = 8 ms` extrapolation cap (both < one vsync) already bound any *synthesized* motion; genuine owed displacement is applied 1:1. **Stays.** The relatch re-baseline (`Win32DirectManipulation.cs:263-266`), the resampler reset at `ScrollBegin` (`InputDispatcher.cs:2666`).

---

## A.7 Legacy-wheel-synthesis defense — replace the hold-notch with the browser geometry (Decision 7)

> **The real fix for F3 is A.5 (no content rect ⇒ no runway exhaustion ⇒ the OS never synthesizes the ±120 burst).** The hold-one-±120-for-50 ms burst latch was defending a symptom our own runway created; the browsers carry no such defense because the trigger never arises. Once A.5 lands, the latch is dead code.

Decided:
- **Keep** the structural `GestureLive` suppression (`Win32Platform.cs:1208`): while a DM manipulation is live, the hi-res wheel *fallback* is suppressed — "never two producers for one gesture" (§7). Cheap, correct, independent of runway.
- **Delete** the hold-one-notch machinery (`Win32Platform.cs:1175-1195`: `_dmHeldNotch`, `_dmWheelBurstLatch`, `FlushHeldWheelNotch`) **after on-device confirmation** that the A.5 geometry yields unbounded translation and no synthesized burst.
- **Keep** the always-compiled §3.3 classifier (device-type → sub-notch → sustained-burst) as the mouse path forever; rule 4 stays exact while DM owns the contact.

**Honesty / sequencing.** Do not delete the latch blind. Land A.5, run the F3 trace (hard edge flick + a >4 s chained fling), confirm zero synthesized `WM_POINTERWHEEL`, *then* remove the latch. Until then the latch is harmless belt-and-suspenders.

---

## A.8 Amended constants (supersede the hotfix values)

| Constant | Was (hotfix) | Now | Provenance |
|---|---|---|---|
| `MomentumSpringCoupling` (γ) | 0.45 | **0.31** | WebKit `a`; makes our ω=12.5 bounce the WebKit exponential exactly |
| bounce depth cap `Cpeak` | 0.6·d·ω·e (magic) | **0.6·d·ω·e (relabeled)** | exact peak bound `peak=seedVel/(ω·e)`; `Cpeak=0.6` an honest feel constant keeping `peak<d` |
| edge tail velocity | `(xStar−XAppliedLast)/dt` | **`(X1−X0)/(T1−T0)`** | WebKit `eventDelta/eventDt`; sample-timestamp, F5-proof |
| tail zeroing | (none) | **stale > `AssumeStoppedMs` 40 ms ⇒ 0** | Android `ASSUME_POINTER_STOPPED` (WebKit uses 100 ms) |
| `SeedFromEdgeMomentum` | position-seeds `v/k` | **velocity-only, position untouched** | iOS/Chromium/Flutter/Android consensus |
| DM `ContentSize` / `SetContentRect` | 200000 / set | **removed; viewport = window rect** | Chromium/Firefox never `SetContentRect` |
| F3 hold-one-notch latch | active | **delete after A.5 confirmed** | browsers carry no such defense |

Unchanged: `RubberC=0.55`, `d=0.15·vp`, `SnapBackOmega=12.5`, ζ=1, `FlingDecayPerS=0.135`, `FlingMaxVelocityPxPerS=8000`, `FlingSeedGate=50`, `AssumeStoppedMs=40`, `DmPinchScaleEpsilon=0.01`, `dtClampMs=34`.

---

## A.9 Implementation checklist (ordered)

1. Rewrite `OverscrollPhysics.SeedFromEdgeMomentum` velocity-only (position untouched; `bandVel = clamp(0.31·v, ±0.6·d·ω·e)`, never-shrink); set `MomentumSpringCoupling = 0.31`.
2. Point the fling→clamp path (`ScrollIntegrator.cs:380`) and the OS-owned edge tick (`:489-495`) at the rewritten seed — one code path, kills the residual F6 on fallback flings.
3. Switch the edge tail velocity to the two-sample slope `(X1−X0)/(T1−T0)` with the `AssumeStoppedMs` stale-zero (`ScrollIntegrator.cs:477`).
4. Add the `_sgTailConverted` latch: drop `MomentumUpdate` deltas after an edge→`SnapBack` conversion (matches `m_ignoreMomentumScrolls`).
5. DM producer geometry: delete `SetContentRect` + 200k runway, viewport = window rect, recenter-only-at-`READY` with identity-skip (`Win32DirectManipulation.cs`).
6. On-device: run the F3 + F6 traces (hard edge flick, >4 s chained fling, lift-at-stretch); confirm no wheel synthesis and no band teleport.
7. Delete the F3 hold-one-notch latch (`Win32Platform.cs:1175-1195`); keep `GestureLive` suppression and the §3.3 classifier.
8. Extend the headless gates: `gate.scroll.overscroll-rational` asserts bounce `peak ≤ 0.6·d` and re-grab round-trip at a bounce; add a relatch-catch-up gate asserting 1:1 (no clamp) within one vsync.
