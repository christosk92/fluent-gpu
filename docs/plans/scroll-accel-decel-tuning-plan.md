# Scroll acceleration / deceleration â€” research & tuning plan

> **Scope:** the *felt* accel/decel quality of the engine-owned touchpad/touch/wheel scroller. Every change here is a **value edit via existing (or one new, defaultâ€‘off) env knob** â€” never a logic edit on the hot path, never a perâ€‘control patch. The rubberâ€‘band and stutter fixes from **workflowâ€‘1** are assumed landed; this plan is sequenced *after* them and explicitly builds on the corrected velocity seed they introduce.

---

## TL;DR

The "accel/deceleration is a bit off" complaint resolves to **two felt defects plus one crossâ€‘input wrinkle**, all fixable by dialing existing env knobs (no rebuild):

1. **Acceleration is inverted.** The touchpad softâ€‘knee (`Win32Platform.cs:917â€‘921`) *compresses* fast packets toward a hard perâ€‘packet ceiling of `s_tpMaxRawÂ·s_tpScale = 1600Â·0.08 = 128 DIP`. On this device every real packet (âˆ’400â€¦âˆ’1700) already lives above the `s_tpKnee=240` (~2 detents) knee, so **essentially all scrolling is in the deâ€‘amplified region** â€” the opposite of macOS/iOS/libinput, which *amplify* fast gestures. A hard flick "doesn't throw far enough." **Fix: widen the knee + ceiling + velocity clamp** â†’ `FG_TP_KNEE=600 FG_TP_MAXRAW=2800 FG_TP_MAXVEL=4500`.

2. **Deceleration is ~50% too stiff.** The coast decay is `k = âˆ’ln(0.05) = 3.0/s` (asymptote `vâ‚€/k`); iOSâ€‘normal is `k=2.0/s` and glides ~50% farther. After workflowâ€‘1's velocity fix the coast now *starts* from a correct (higher) velocity, so the stiff decay is the dominant remaining "stops too soon." **Fix: soften** â†’ `FG_TP_DECAY=0.10` (kâ‰ˆ2.30/s, +30% glide), paired with a lower settle floor `FG_TP_SETTLE=12` so the tail fades instead of hitting a 30 px/s wall.

3. **Wheel vs touchpad personality split.** Wheelâ€‘fling decays at `k=10.8/s` (3.6Ã— stiffer than the touchpad's 3.0/s) â€” same perâ€‘notch *distance* but a much shorter *tail*, so switching devices flips snapâ†”glide. **Fix: narrow (don't erase) the gap** â†’ `FG_WHEEL_DECAY=0.01` (kâ‰ˆ4.6/s).

Plus two lowerâ€‘priority items: an optional **deviceâ€‘independent velocityâ€‘keyed gain** (ships **defaultâ€‘off**, the only code edit here, for crossâ€‘touchpad portability) and a **highâ€‘refresh lowâ€‘pass trim** (`FG_TP_SMOOTH_TAU=12`) for the 120Hz dev panel.

**Headline:** the curve *families* are right (closedâ€‘form exponential coast, softâ€‘knee gain). The problem is **the constants are misâ€‘valued, and the gain curve points the wrong way**. All gateâ€‘safe â€” the headless determinism gates lock `ScrollAnimator.FlingDecayPerS` (touch fling) and `CoastStep`, **not** the touchpad `s_tp*` knobs or the softâ€‘knee (which lives in `FluentGpu.Windows`, outside the VerticalSlice's TerraFXâ€‘free closure).

> **Dependency on workflowâ€‘1 (loadâ€‘bearing):** the windowed leastâ€‘squares regression that replaced the poison EMA is already live (`InputDispatcher.cs:2350â€‘2355`, `_tpVelTracker`). This plan tunes the coast that *starts from that seed* â€” so **dial decel last, after the seed is final.** If workflowâ€‘1 further changes the regression window/terminal velocity, reâ€‘dial `FG_TP_DECAY` and the knee width after it lands.

---

## How the best engines shape accel/decel

The reference behaviors the user's "feel" is implicitly benchmarked against:

| Engine | Acceleration (input gain) | Deceleration (coast) | Key constant | Felt tradeâ€‘off |
|---|---|---|---|---|
| **macOS** (IOHIDScrollAcceleration) | Amplifies **exponentially with velocity** (~100:1 fastâ€‘vsâ€‘slow) | velocityâ€‘exponential | velocityâ€‘keyed | precise slow pans + explosive fast throws |
| **iOS** UIScrollView | (input is direct touch) | `v(t+1ms)=vÂ·0.998` â†’ perâ€‘sec survival `0.998Â¹â°â°â°=0.135` â†’ **k=2.0/s, Ï„â‰ˆ500ms**; `decelerationRate.fast`=0.99 â†’ kâ‰ˆ10/s | `0.998` | long floaty glide; "projection" API lands snaps on the *same* curve |
| **Android** OverScroller | (direct touch) | **spline**, distance âˆ `v^1.736` â€” fastâ€‘thenâ€‘slow, **finite duration** (no asymptote crawl) | spline | disproportionately far fast flings; deterministic stop time |
| **Chromium** desktop fling | wheel ease + fling | exponential **kâ‰ˆ5.2/s** (stiffer than iOS) | â€” | crisp, less floaty than iOS |
| **libinput** | flat=1:1; **adaptive** (default, mouse) keys gain off **velocity**; linear=constantâ€‘factor | â€” | velocity trackers | deviceâ€‘independent amplification of fast motion |
| **FluentGpu (today)** | softâ€‘knee **deâ€‘amplifies** raw perâ€‘packet notch (gain â‰¤ 1.0, ceiling 128 DIP) | exponential **k=3.0/s** (touchpad+touch), **k=10.8/s** (wheel) | `s_tpScale/Knee/MaxRaw`, `s_tpDecayPerS` | fast flick feels sluggish + stops sooner than iOS |

**The two takeaways that drive the fix:** (a) bestâ€‘inâ€‘class **amplifies** fast input; FluentGpu **compresses** it â€” invert the gain bias. (b) Bestâ€‘inâ€‘class coast is in the **iOS k=2.0 â€¦ Chromium k=5.2** band; FluentGpu's 3.0 is fine *in family* but stiff â€” soften toward the iOS end. **We keep the exponential coast** (it is the only zeroâ€‘alloc, **dtâ€‘invariant closedâ€‘form** option â€” `CoastStep`'s `dpos = vÂ·(1âˆ’decay^dt)/k`); Android's spline and a superâ€‘linear `v^1.736` coast are **deferred** because they are logic edits that risk frameâ€‘rate independence (see Risks).

---

## Acceleration (inputâ€‘gain)

**Confirmed root cause â€” the softâ€‘knee compresses fast input (inverted gain).** *(verdict: partial â€” mechanism confirmed, knee is the right primary lever, but `s_tpMaxVel` coâ€‘binds the top end earlier than the prose implied.)*

`Win32Platform.cs:917â€‘921`:
```
tamed = a <= s_tpKnee(240) ? a : knee + (maxRawâˆ’knee)Â·(1 âˆ’ e^(âˆ’(aâˆ’knee)/(maxRawâˆ’knee)))
dip   = sign Â· tamed Â· s_tpScale(0.08)
```
This is a **saturating compressor**. Independently reproduced gains (vs linear): `1.000 @ notch=240`, `0.927 @ 600`, `0.687 @ 1600`, `0.431 @ 3400`; `tamed` asymptotes at `maxRaw`, so the perâ€‘packet DIP **hardâ€‘ceilings at `1600Â·0.08 = 128 DIP`** no matter how hard you flick.

**The coupling that makes it worse than it looks** (traced live): `dip â†’ InputDispatcher.cs:2358 _tpPendingDelta â†’ :2363 _tpVelPos â†’ windowed regression â†’ :2402 coast seed`. Because the velocity tracker integrates the **alreadyâ€‘compressed** DIPs, compression suppresses **both** the live gesture distance **and** the coastâ€‘seed velocity â€” a hard flick is short *during* the gesture **and** coasts shorter.

**Refinement from adversarial modeling:** the LSQ terminal velocity = `dip_per_packet/dtÂ·1000`. At a real hardâ€‘flick cadence (8â€‘16 ms spacing) any `notchâ‰¥800` already **saturates the `s_tpMaxVel=3500` clamp** (`:2402`) under *both* current and proposed curves â€” so the knee dominates the **common/moderate** range and liveâ€‘travel, but the **`s_tpMaxVel` clamp coâ€‘binds the extreme top end** (coast asymptote pinned at `3500/3.0 = 1168 px` regardless of knee). Therefore **`FG_TP_MAXVEL` belongs in the default recommendation, not as a fallback.**

### Fix (valueâ€‘only, no rebuild)
```
FG_TP_KNEE=600    # (was 240) â€” notch â‰¤600 now fully linear (gain 0.927 â†’ 1.000)
FG_TP_MAXRAW=2800 # (was 1600) â€” notch=1600: ~88â†’~112 DIP (gain 0.687â†’0.877); notch=3400: ~117â†’~175 DIP
FG_TP_MAXVEL=4500 # (was 3500) â€” coast asymptote 1168px â†’ 1500px; promoted to DEFAULT
# keep FG_TP_SCALE=0.08
```
This raises the perâ€‘packet ceiling **128 â†’ 224 DIP**, so a harder flick genuinely throws farther; the regression integrates the higher DIPs so the coast seed rises in lockstep.

**Onâ€‘device dialâ€‘in** (`FG_SCROLL_LOG=1`, read the `TPPAN dip=` trace at `Win32Platform.cs:925â€‘926`):
- top end overshoots â†’ `FG_TP_SCALE=0.07` (drop *before* lowering MAXVEL);
- to fully disable compression (closest to libinput/macOS amplifyâ€‘fast philosophy, let MAXVEL be the sole ceiling) â†’ `FG_TP_KNEE=9999` + `FG_TP_SCALE=0.06` (keeps precise slow pans tame).

**Research backing:** libinput linear profile "1:1 for regular movement, linear increase to **3.5Ã—** for fast"; macOS "accelerates exponentially based on velocity." FluentGpu's â‰¤1.0 monotone gain is the inverse â€” this fix moves it back toward neutral/amplifying.

**Gateâ€‘safety:** the softâ€‘knee is pure stack scalar arithmetic (`MathF.Abs/Exp/Sign`, env read once at static init) in the PAL message pump â€” **outside frame phases 6â€‘13** and **outside the VerticalSlice compilation closure** (`FG_TP_KNEE/MAXRAW` are read only in `FluentGpu.Windows`; `VerticalSlice` references Engine+Controls, not Windows). `DriveTouchpadPan` feeds DIP straight into the engine, **bypassing** the softâ€‘knee, so no headless gate exercises these constants. Compiled defaults unchanged â‡’ an unsetâ€‘env CI build is bitâ€‘identical. Does **not** touch `ClampTpRaw` (rubberâ€‘band) or the noâ€‘lift coast continuation (stutter fix).

> **Deviceâ€‘independence (HA2) is a separate, optional entry** â€” see *Crossâ€‘input consistency â†’ velocityâ€‘keyed gain*. On *this* calibrated device the compression ceiling dominates; the velocityâ€‘keyed gain earns its keep on *other* touchpads and is a code edit, so it can't ship under the valueâ€‘only constraint here.

---

## Deceleration (coast curve)

**Confirmed root cause â€” coast decay kâ‰ˆ3.0/s is ~50% stiffer than iOSâ€‘normal.** *(verdict: confirmed.)*

`TickTouchpad` noâ€‘packet branch advances the coast via `OverscrollPhysics.CoastStep(ref _tpVel, dtMs, s_tpDecayPerS)` (`InputDispatcher.cs:2409`), `s_tpDecayPerS=0.05` (`:141â€‘142`). `CoastStep` (`OverscrollPhysics.cs:79â€‘88`) is the exact closedâ€‘form `dpos = vÂ·(1âˆ’decay^dt)/k`, `k = âˆ’ln(decay) = 2.996/s`. Asymptote `vâ‚€/k`: **1000 px/s â†’ 333.8 px** (settles 324.2 px at the 30 px/s cutoff â€” matches live gate `coast=324.17px, k=2.996`); 1600 px/s â†’ 534 px asymptote (settles ~524 px).

iOSâ€‘normal `0.998` is **perâ€‘millisecond**, so perâ€‘second survival `0.998Â¹â°â°â° = 0.1351` â†’ **k=2.002/s, Ï„=499ms** â€” iOS coasts **~50% farther** (499 vs 334 px @1000 px/s). After workflowâ€‘1's velocity fix the coast starts from a correct higher velocity, so this stiffness becomes the dominant remaining "stops too soon."

**Curve shape subâ€‘hypothesis (HD1) correctly REFUTED:** iOS uses the *same* velocityâ€‘exponential family â€” parity needs only a **timeâ€‘constant value move**, not an integrator change. The perâ€‘second closedâ€‘form is the only dtâ€‘invariant zeroâ€‘alloc option (a perâ€‘frame `vÂ·=rate` model gives `rate^60` vs `rate^1000` â€” wildly dtâ€‘dependent â€” which is exactly why a spline/perâ€‘frameâ€‘ratio must be rejected here).

### Fix A â€” soften the decay (primary, runtime)
```
FG_TP_DECAY=0.10   # (was 0.05) â†’ k=2.30/s, Ï„=434ms; 1000px/sâ†’~422px (+30%), 1600px/sâ†’~682px (+30%), settle ~1.5s
```
Ladder: `FG_TP_DECAY=0.135` = **exact iOSâ€‘normal** (k=2.0/s, +50%) if 0.10 still feels short; `FG_TP_DECAY=0.079` (k=2.54/s, +18%) if 0.10 overshoots. Dial Â±0.03. `s_tpDecayPerS` has exactly **one** consumer (`:2409`). To bake permanently (only after onâ€‘device confirm): change the literal `0.05f â†’ 0.10f` at `InputDispatcher.cs:142`.

### Fix B â€” lower the settle floor (paired, runtime)
**Confirmedâ€‘partial root cause â€” the 30 px/s settle truncates the tail.** *(verdict: partial â€” real missingâ€‘ramp, but marginal standalone; only becomes felt when paired with the softer decay.)*

The coast ends at the **dual gate** (`:2421`): `|_tpVel| < s_tpSettleVel(30)` **AND** `|_tpDemandRaw âˆ’ _tpAppliedRaw| < 0.5px`. The second condition (the oneâ€‘pole lowâ€‘pass draining demandâ†’applied) keeps the demand advancing several frames past the velocity crossing, so the cut is the **asymptotic remainder (~9.6 px)**, not a hard stop *at* 30 px/s. The rubberâ€‘band spring already has a terminal snapâ€‘home ramp (`OverscrollPhysics.cs:118â€‘120`); the **coast has none**, so the cutoff is the one place the coast can read as a stop. Today the appliedâ€‘velocity step at trip is ~21 px/sâ†’0 (subâ€‘halfâ€‘pixel, barely felt) â€” but at the softer k=2.3 the belowâ€‘settle tail grows to **13 px** and the approach slows, making the wall perceptible.
```
FG_TP_SETTLE=12    # (was 30) â†’ at k=2.3 adds +7.8px and +398ms of sub-pixel taper; ~0.20px/frame @60Hz, clean
```
Floor: `FG_TP_SETTLE=8` if 12 still reads as a wall; do **not** go below ~6 (frames grow without perceptual payoff). **Land B only together with A** â€” on its own it changes the landing by <6 px.

> **Honesty note:** the true rootâ€‘cause fix for the missing terminal taper would be to give the coast a terminal velocity ramp mirroring the spring's snapâ€‘home â€” but that is a **logic edit, out of scope** under the valueâ€‘only constraint. Lowering `FG_TP_SETTLE` alongside the softer decay is the correct gateâ€‘safe move within the constraint.

**Gateâ€‘safety (both A and B, verified by running the harness):** `gate.touchpad.coast-distance` (324.17px) and `gate.touchpad.frame-rate-independence` (spread 0.285px) integrate **`ScrollAnimator.FlingDecayPerS`** (the touchâ€‘fling/snap symbol), **not** `s_tpDecayPerS`/`FG_TP_DECAY` or `s_tpSettleVel`/`FG_TP_SETTLE` (grepâ€‘confirmed zero harness references; `gate.touchpad.coast-distance` even hardcodes a local `settle=30f`). The two liveâ€‘path gates that *do* use the touchpad symbols (`settle-determinism`, `alloc-zero`) assert **replay determinism + bounds**, not magic numbers â€” both stay green at `FG_TP_DECAY=0.10` / `FG_TP_SETTLE=12` (bitâ€‘identical runâ€‘A=runâ€‘B, landing simply shifts further). `508/508` checks pass both ways; 0 B alloc; `CoastStep` closed form unchanged â‡’ dtâ€‘invariance preserved.
> **HARD BOUNDARY:** do **NOT** edit `FlingDecayPerS=0.05f` (`ScrollAnimator.cs:58`) or `FlingMinVelocityPxPerS` (`:61`) â€” they are gateâ€‘locked at 324px and shared with the touch path. This change deliberately **splits the touchpadâ€‘coast k from the touchâ€‘fling k** (acceptable: different input classes, touch path gateâ€‘locked). A unified touch+touchpad feel later would move the golden (out of scope).

---

## Crossâ€‘input consistency

### Wheel vs touchpad â€” narrow the personality gap *(verdict: partial â€” mechanism confirmed, "distance mismatch" framing refuted; it is a duration/tailâ€‘shape gap.)*

Touchpad coast (`s_tpDecayPerS=0.05`) and touch fling (`FlingDecayPerS=0.05`) share **k=3.0/s â€” consistent.** The outlier is **wheelâ€‘fling**: `WheelFlingDecayPerS=0.00002` (`ScrollAnimator.cs:76`) â†’ `k=âˆ’ln(0.00002)=10.82/s`, **3.61Ã— stiffer**. Wheel seed is `v0 = deltaÂ·k` (`InputDispatcher.cs:2545`), so **perâ€‘notch DISTANCE = v0/k = delta exactly, for any k** â€” the gap is **DURATION/tailâ€‘shape**: a 48â€‘DIP notch settles in **0.264s** at k=10.8 vs **0.523s** at k=3.0; a touchpad coast glides ~1s+. Wheel brakes crisply, touchpad glides â†’ personality flip on device switch.

> **Corrected fact (do not trust the raw corpus):** the wheel/touchpad gap is *not* a 3.6Ã— distance mismatch â€” distance is kâ€‘invariant. Also note: in the shipping app `SmoothScroll=true` (`FluentApp.cs:111`, `AppHost.cs:458`), so **every wheel notch already routes through WheelFlingMode** (not the crisp `TargetChase`). The change below *does* reâ€‘shape the singleâ€‘notch tail â€” that is intended, not a noâ€‘op.

```
FG_WHEEL_DECAY=0.01   # (was 0.00002) â†’ k=4.61/s; cuts the gap 3.61Ã— â†’ 1.54Ã—
```
A clicky wheel **should** brake faster than a continuous touchpad (Chromium/WinUI/Edge intentionally split wheel ease from fling) â€” the goal is to **narrow, not erase**. `0.01` makes a 48â€‘DIP notch settle in ~0.43s (less abrupt than 0.264s, still crisper than the 0.523s touchpadâ€‘matched glide). If still too abrupt step toward `0.03` (k=3.5) / `0.05` (k=3.0, full match). **Do NOT** touch `WheelEaseTauMs=18` (gateâ€‘locked via `ScrollTuning.WinUiLike`, governs the `SmoothScroll=false`/TargetChase path the gates assert on).

**Gateâ€‘safety:** **no standing gate exercises the WheelFlingMode path** â€” every gate `AppHost` runs `SmoothScroll=false` (the one `SmoothScroll=true` is in the `FG_PROBE=scroll-flicker` manual probe, not a Check). `ScrollTuning` pins `WheelEaseTauMs`/`FlingDecayPerS`, **not** `WheelFlingDecayPerS`. The integrator (`vÂ·=Pow(decay,dtS)`) is the same telescoping geometric form â€” dtâ€‘invariant for any value. Zero alloc (env parsed once). Rubberâ€‘band is touchâ€‘`FlingMode`â€‘only (`ScrollAnimator.cs:295`) so untouched. Reâ€‘run VerticalSlice as cheap insurance.

### Velocityâ€‘keyed gain â€” optional, **defaultâ€‘off**, the only code edit *(verdict: partial â€” real portability defect, fix had two mechanical bugs now corrected.)*

All three gain constants key off **raw |notch| in deviceâ€‘native HID units** (`Win32Platform.cs:152â€‘161`, calibrated for "THIS device's âˆ’400â€¦âˆ’1700"). A touchpad emitting 50â€‘200/packet sits entirely below the knee â†’ completely different feel. Bestâ€‘inâ€‘class keys gain off **velocity in physical units/sec**. The engine already maintains a deviceâ€‘independent regression velocity (`_tpVelTracker.Vx/Vy`) the gain path never consults. **This is a PORTABILITY defect, not a calibratedâ€‘device feel defect** â€” on this device the softâ€‘knee fix dominates; this hardens *other* touchpads.

Ship **defaultâ€‘off** (the newâ€‘knobâ€‘gatesâ€‘theâ€‘newâ€‘path discipline keeps it valueâ€‘notâ€‘logic in effect). **Corrected fix** in `InputDispatcher.cs` `_tpGotPacket` branch (the original proposal's insertion point was unimplementable â€” by `:2402` the delta is already folded and zeroed â€” and gained only the demand, leaving the coast unâ€‘gained so a fast flick would *brake at lift*):

```
// EDIT A â€” reorder: refresh velocity FIRST, gain BOTH demand and coast seed
float vClamped = Math.Clamp(_tpHoriz ? _tpVelTracker.Vx : _tpVelTracker.Vy, -s_tpMaxVel, s_tpMaxVel);
float g = Math.Clamp(1f + MathF.Max(0f, MathF.Abs(vClamped) - s_tpVelKnee) * s_tpVelSlope, 1f, s_tpVelMaxGain);
_tpDemandRaw = ClampTpRaw(in sc, _tpDemandRaw + _tpPendingDelta * g);  // gained, captured BEFORE zeroing
_tpPendingDelta = 0f; _tpGotPacket = false;
_tpVel = vClamped * g;   // seed the coast with the SAME gain so the throw doesn't brake at lift

// EDIT B â€” three new static-readonly env knobs (mirror the s_tpScale pattern)
FG_TP_VEL_KNEE     default 800f   (DIP/s below which no amplification)
FG_TP_VEL_SLOPE    default 0.0f   (OFF â€” >0 dials it on)
FG_TP_VEL_MAX_GAIN default 2.0f   (ceiling)
```
With `slope=0`, `gâ‰¡1.0` â†’ byteâ€‘identical to today (zero gate movement). Dialâ€‘in (deviceâ€‘independent, macOSâ€‘like): `FG_TP_VEL_SLOPE=0.0005` â†’ gain `1.0@â‰¤800, 1.2@1200, 1.6@2000, 2.0@â‰¥3000 DIP/s`. Onsetâ€‘noise guard is automatic (regression returns 0 with <2 inâ€‘window samples). **Sequence AFTER or INSTEADâ€‘OF the kneeâ€‘widening** â€” crossâ€‘device hardening layer, not the primary felt fix. Also correct the inline comment: libinput's **adaptive** (not "linear") profile is the velocityâ€‘keyed reference.

**Gateâ€‘safety (slope=0):** `TickTouchpad` is phase 7 (inside the 0â€‘alloc 6â€‘13 scope) but the added ops are scalar JIT intrinsics + 3 cold staticâ€‘readonly env reads â€” no perâ€‘frame heap; `gâ‰¡1.0` â‡’ `_tpDemandRaw`/`_tpVel` byteâ€‘unchanged â‡’ determinism + golden hold. **Must build + reâ€‘run VerticalSlice to confirm the alloc tripwire on the new path** before claiming done. (Note: the gate probes feed 1000 DIP/s, above the 800 knee, so any `slope>0` *would* move the live gates â€” they stay green only at the default, which is exactly why it ships defaultâ€‘off.)

### Highâ€‘refresh lowâ€‘pass trim â€” priority 5 polish *(verdict: partial â€” real highâ€‘refresh latency, but offâ€‘target for the accel/decel complaint and the proposal's kOff numbers were wrong.)*

The touchpad path adds a oneâ€‘pole lowâ€‘pass (`InputDispatcher.cs:2414`, `tau=14ms`) the touch fling lacks â†’ a steady **14ms** trail that the touch fling doesn't have. The msâ€‘lag is constant; "frames behind" grows `0.84@60Hz â†’ 1.68@120Hz â†’ 2.0@144Hz`. The dev panel **is 120Hz** (Snapdragon X), so the premise is real here. **But this changes neither acceleration nor the deceleration law** â€” pure inputâ€‘tracking latency, lowest impact, **sequence LAST.**

> **Caveat (memory item 4):** the 14ms filter was added **deliberately** to deâ€‘jitter this device's pathologically bursty packets â€” `tau` is a bias/variance dial, not pure lag. Tune by feel; do **not** add the filter to the touch fling to "equalize" (logic edit, gate risk).

```
FG_TP_SMOOTH_TAU=12   # (was 14) â€” kOff 0.501@120Hz (â‰ˆ1.44 frames) vs 0.449; keeps >1-frame smoothing for bursts
# drop to 10 (kOff 0.565@120Hz) only if still latent AND no jitter returns
```
*(Corrected numbers: true tau=10 is `0.565@120Hz / 0.501@144Hz`; the proposal's "0.513/0.456" were the tau=12 values.)* Gateâ€‘safe: `s_tpSmoothTau` is not a `ScrollTuning` field; no gate pins it; the coast gates call `CoastStep` filterâ€‘free.

---

## Prioritized change list

| # | File | Change | Knob | Proposed value (was) | Risk |
|---|---|---|---|---|---|
| 1 | `Win32Platform.cs:917â€‘921` | Widen softâ€‘knee so fast packets amplify, not compress | `FG_TP_KNEE` | **600** (240) | Low â€” outside headless closure, valueâ€‘only |
| 1 | `Win32Platform.cs:160` | Raise compression ceiling | `FG_TP_MAXRAW` | **2800** (1600) | Low |
| 1 | `InputDispatcher.cs:151` | Raise velocity clamp (coâ€‘binds top end) | `FG_TP_MAXVEL` | **4500** (3500) | Low â€” reâ€‘measure topâ€‘end throw on device |
| 2 | `InputDispatcher.cs:142` | Soften touchpad coast decay toward iOS | `FG_TP_DECAY` | **0.10** (0.05) â†’ k 2.30/s | Low â€” splits from touchâ€‘fling k (intended) |
| 2 | `InputDispatcher.cs:145` | Lower settle floor for a fading tail (**pair with #2 decay**) | `FG_TP_SETTLE` | **12** (30) | Low â€” marginal standalone |
| 3 | `ScrollAnimator.cs:76` | Narrow wheelâ€‘vsâ€‘touchpad tail gap (don't erase) | `FG_WHEEL_DECAY` | **0.01** (0.00002) â†’ k 4.61/s | Low â€” no gate runs WheelFlingMode |
| 4 | `InputDispatcher.cs` `_tpGotPacket` | **Code edit (defaultâ€‘off):** velocityâ€‘keyed gain, gain BOTH demand+coast | `FG_TP_VEL_SLOPE` (+`_KNEE`,`_MAX_GAIN`) | **0.0=off** (new); 0.0005 on device | Med â€” new hotâ€‘path code; **build+VerticalSlice to confirm alloc=0** |
| 5 | `InputDispatcher.cs:158` | Trim lowâ€‘pass for 120/144Hz parity | `FG_TP_SMOOTH_TAU` | **12** (14) | Low â€” fights deliberate antiâ€‘jitter; tune by feel |

**Onâ€‘device dialâ€‘in sequence (no rebuild, `FG_SCROLL_LOG=1`):**
1. `FG_TP_KNEE=600 FG_TP_MAXRAW=2800 FG_TP_MAXVEL=4500` (fix the inverted gain) â†’
2. `FG_TP_DECAY=0.10` (soften coast â€” *after* #1, the correctedâ€‘velocity coast may already feel closer) â†’
3. `FG_TP_SETTLE=12` (fade the tail) â†’
4. `FG_WHEEL_DECAY=0.01` (wheel parity) â†’
5. `FG_TP_SCALE=0.07` only if topâ€‘end overshoots; `FG_TP_VEL_SLOPE=0.0005` only if fast flicks *still* feel underâ€‘powered; `FG_TP_SMOOTH_TAU=12` last for 120Hz latency.

---

## Verification plan

**Headless (every change, after each edit):**
```
dotnet build src/FluentGpu.slnx                       # clean
dotnet run --project src/FluentGpu.VerticalSlice       # "ALL CHECKS PASSED" (508/508), alloc tripwire 0 B
```
Expected **unchanged at defaults** for #1â€‘3, #5 (envâ€‘only, outside the headless closure / not ScrollTuningâ€‘pinned). For **#4 (velocityâ€‘keyed gain) the build + run is mandatory** â€” it's the only new hotâ€‘path code; confirm `gate.touchpad.alloc-zero` stays `worst==0` and `settle-determinism` lands bitâ€‘identical with `slope=0`. Gates that must stay green and what they actually lock: `gate.touchpad.coast-distance` (324.17px) + `frame-rate-independence` (spread <0.5px) â†’ lock `FlingDecayPerS`+`CoastStep` (untouched); `band-roundtrip` (0.000004px) â†’ rubberâ€‘band (untouched); `settle-determinism` â†’ replay equality (holds for any positive constant).

**Onâ€‘device (`FG_SCROLL_LOG=1`, the only authority on feel â€” all distances above are analytic):**
- `TPPAN dip=` (`Win32Platform.cs:925â€‘926`) â€” confirm a hard flick's perâ€‘packet DIP rises past the old 128 ceiling.
- coast seed `_tpVel` at `:2402` â€” confirm a fast flick approaches/uses the raised `FG_TP_MAXVEL`.
- `TPEND settle off=` (`:2429`) â€” confirm the gesture lands without a visible jerk and glides ~30% farther.
- **Same list, wheel vs touchpad vs touch**, backâ€‘toâ€‘back â€” confirm the coast *tails* read as the same family (not snapâ†”glide).
- **120Hz check** for `FG_TP_SMOOTH_TAU` â€” confirm reduced latency without jitter return.

**Knobâ€‘sweep methodology:** change one knob, relaunch (no rebuild), flick the *same* list at slow/medium/hard speeds, compare against the reference feel (iOSâ€‘normal floaty vs Chromiumâ€‘crisp). Adjust in the documented ladders (Â±0.03 for decay, Â±0.02 for scale). Bake a literal default only after the felt value is confirmed across several sessions.

---

## Risks & open questions

- **All values are analytic, not measured.** No diagnosis measured an actual coast distance on the physical touchpad â€” every figure (324px, 534px, the gain table) is derived from verified formulas. The proposed defaults are **starting points**, dialed on device.
- **Workflowâ€‘1 sequencing is loadâ€‘bearing but unverified here.** The regression is confirmed live, but workflowâ€‘1's plan body/confirmedâ€‘cause list arrived empty. If it changes the regression window or terminal velocity, **reâ€‘dial `FG_TP_DECAY` and the knee width after it lands** â€” do not finalize decel before the seed is final.
- **Kneeâ€‘widen Ã— `s_tpMaxVel` interaction unquantified.** Widening the knee pushes more gestures into the 3500/4500 clamp; the exact sustained notch where the regression crosses the clamp on *this* device is unmeasured â€” whether `FG_TP_MAXVEL` must rise *further* is open. (It is promoted to the default precisely because it coâ€‘binds the top end.)
- **`s_tpMaxVel` is arguably its own topâ€‘end lever**, doubleâ€‘limiting a hard flick (~5278â†’3500 px/s at notch=1600, a ~33% seed cut) independent of the knee â€” given a standalone default (4500) here rather than a buried followâ€‘on.
- **Accelâ†”decel as a *pair* is untested.** The user said "accel/deceleration" as **one** complaint; the felt mismatch may be the **ratio** between rampâ€‘up (gain) and bleedâ€‘off (decay) â€” a highâ€‘gain throw that decays stiffly can feel more "off" than either alone. The dial order is specified but the joint felt target ("does a fast flick feel symmetric?") needs onâ€‘device judgment.
- **Bestâ€‘inâ€‘class technique left on the table â€” superâ€‘linear coast (`distance âˆ v^1.736`, Android).** The engine's coast stays **linear in `vâ‚€`** (`distance = vâ‚€/k`). The kneeâ€‘widen + velocityâ€‘keyed gain amplify fast *input*, but not the coast curve itself. A velocityâ€‘dependent `k` (still closedâ€‘form to stay dtâ€‘invariant) would better match "throw far," but it is a **logic edit** â€” deliberately deferred under the valueâ€‘only constraint. **Flag for a followâ€‘up workflow if valueâ€‘tuning the knee+decay still feels short.**
- **Interâ€‘burst coast microâ€‘stutter (unanalyzed).** The coast seed refreshes only on packet frames; a single packet yields <2 samples â†’ `_tpVel=0` â†’ no coast. The device fires ~60ms bursts midâ€‘scroll â€” if the regression velocity dips between bursts, the *acceleration* of the first coast frame after a gap could microâ€‘stutter independent of the decay shape. Needs an onâ€‘device `_tpVel` trace across an interâ€‘burst gap.
- **Refuted (do not reâ€‘attempt): unbounded wheel velocity.** The wheel seed has no `s_tpMaxVel` equivalent, but interâ€‘notch decay makes it a *converging* geometric steady state; the asymmetry bites only at **â‰¥46 notches/sec sustained** (physically impossible for a detented wheel). A `FG_WHEEL_MAXVEL` clamp would be inert in realistic use and redundant with the `FG_WHEEL_DECAY` fix â€” defensible cheap hardening at most, **not a feel fix.**