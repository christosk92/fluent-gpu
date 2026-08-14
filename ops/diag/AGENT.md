# How to read this bundle

You are looking at one interactive scroll-feel capture from the Wavee / FluentGpu engine. A copy of this file
ships **inside** every bundle so the interpretation travels with the data.

Read in this order. Steps 1–4 are cheap and each one can end the investigation.

---

## 1. `feel-summary.json` → `validity` — read this before anything else

If `trusted` is `false`, **stop and report `hardFailReasons`.** Do not rank anything, do not open the console, do
not form a hypothesis. A capture whose instrument did not arm produces an empty summary, and an empty summary
reads exactly like "no problems found".

Then check `untrustedReasons`. They do not block a reading, but every absolute millisecond figure is suspect
while any of them are present.

`observerDeltaVsPlainReleasePct` is `null` unless the packager was given the paired plain-Release arm
(`-Control <session>`); `observerControl` then names it and shows both medians. Without that number you cannot
know how much of what you are seeing is the instrument — treat diag-arm milliseconds as **directional** and rank
by relative differences between phases rather than by absolute values. It is a signed percentage on the **median**
frame time, not the mean: frame-time distributions have a long right tail, and one outlier in either arm would
otherwise invent or hide an observer effect. Above roughly 10%, stop quoting absolute milliseconds entirely.

## 2. `reproducedComplaint`

`false` means every scored phase came back 4–5 — **the session did not reproduce the problem.** Report that and
stop. Hunting for a cause in a capture where nothing felt wrong is how a harmless number becomes a "finding".

`null` means nobody scored it, which makes the whole bundle uncorroborated: the numbers describe a session no
human vouched for.

Check `humanObserved` too. If it is `false` — `syntheticPhaseCount > 0`, an `-Unattended` run — **no glued or
steady verdict may be drawn from this bundle at all.** Nobody touched the machine. Such a run exists to prove the
instrument works end to end; it is structurally incapable of answering a question about feel, and treating its
cadence numbers as a feel result would be inventing an observation that was never made.

## 3. `phases[].insufficientData`

Skip those phases entirely. Under 20 warm scroll-active frames, a percentile describes noise. Repetition 1 of
every phase is already excluded as a warm-up — the first pass over a list pays span record, glyph raster, image
decode and PSO warm, and pooling it with warm passes lets the cold pass dominate every verdict.

## 4. `globalVerdict.rankedLikelyContributors`

Ranked by **measured tag frequency among frames that missed their deadline**, not by a fixed list. It is
**multi-label**: one frame legitimately carries several tags and the totals may exceed 100%. That is deliberate —
collapsing to a single winner is how a secondary cause gets a fix it never needed.

`noDominantStage: true` means the tool declines to name a suspect. That is a real answer, not a failure.

**`fixOrder` is a different list from the ranking.** Detection starts at the present side because that is where
the symptom shows. Fixing starts **upstream**: a span re-record storm or an image pump *causes* the downstream
present misses, so fixing the present symptom first treats the wrong end.

---

## The two pillars — never fuse them

| Pillar | Question | Where it lives |
| --- | --- | --- |
| **A — glued** | Does the content sit where the finger is? | `phases[].latency` |
| **B — steady** | Do photons change on an even cadence? | `phases[].cadence` |

Interventions **trade one against the other**: a pacing queue improves cadence and worsens latency. A fused
"smoothness score" would call that trade a wash and hide both. If you find yourself averaging a latency number
with a cadence number, stop.

---

## These numbers do not mean what they look like

- **Loop FPS is not smoothness.** `FrameStats.Fps` is UI-loop cadence and says so in its own doc comment. Under
  the async render seam it can be high while presents are irregular. It is a footnote, not a metric.
- **`present Nfps` is not photon cadence.** It is a trailing-1 s count of sequence advances observed by the UI
  thread, and it is forced to `0.0` whenever no advance is seen inside the window. Build cadence verdicts on the
  `presentD=` **deltas** between adjacent `[fps]` lines, never on a single `present Nfps` reading.
- **`Presented = true` does not mean photons.** It is `!skipSubmit`, decided before the render thread does
  anything. It means "published, not elided".
- **A present stamp is submit-confirmed, not vblank-confirmed.** It says `Present()` returned. Panel scanout
  position, panel response and backlight are all invisible. Say `inputToVblankOfPresent`, never "photon".
- **A `0` is not "no cost".** Check `reasonNotMeasured` first. Layout counts are 0 without `FG_LAYOUT_DIAG`, GPU
  splits are 0 without `FG_GPU_TIMING`. Reading an unmeasured 0 as "cheap" de-ranks real causes to the bottom.
- **`pace-skip` now fires on the async default too.** It used to be assigned only when the render thread was
  synchronous, so its absence proved nothing and `skipD=` was the only evidence. Since the skip-submit pacing floor
  was extended to async, `pace-skip` means what it says: **the previous frame elided its submit**, and the wait is
  paced by the compositor tick with a 2×refresh wall-clock fallback. A long `pace-skip` run is now the readable form
  of "the scene is not changing"; cross-check it against `skipD=` rather than instead of it.
- **An empty `[render-census]` is not a refutation.** It is suppressed unless flush ≥ 12 ms or components ≥ 25,
  so a shell-wide-but-cheap re-render every frame prints nothing at all.
- **A 60 ambient cap under a 120 Hz panel BEATS** against the vsync-locked present — a software cap below the
  refresh stacks onto vblank quantisation. Read `effectiveKnobs.ambientFps` against `display.panelNominalHz`
  before blaming the present path.
- **The adaptive-fps governor is default ON** and engages on its own when the smoothed fence wait exceeds its
  budget, changing present cadence mid-session. Check `effectiveKnobs.adaptiveFps` before attributing a cadence
  change to anything else.
- **A `PresentMode` change is not a regression.** Windows promotes and demotes composed ↔ independent flip on
  maximize, occlusion and MPO availability, and the two differ by about one refresh of latency. Bucket by mode.
- **`spans=R/B/RR` describes what the engine did, not what the user saw.** Until a content-approximation metric
  exists, span and image buckets rest on proxies.
- **A clean `offsetDiscontinuity` bucket does not mean no non-interactive path jumped.** The offset sensor covers
  the interactive path only; about a dozen other sites write scroll offsets directly (scroll restore, virtualisation
  re-pin, keyless reset, tab/tree/grid resets) and never pass through it.
- **Tracking lag is biased toward zero during fast flings.** Both the OS and the engine's own input ring coalesce
  packets before any delta arithmetic sees them, so a fast fling under-counts. `coalescingBiasNoted` is `true` for
  exactly this reason.
- **Never join on `frame`.** That column counts one paint phase only (loop early-outs never reach it), is written
  without synchronisation, and suppresses no-input micro-frames. Join on `tMs` or on the latency row's publish seq.

---

## The join contract

> A latency sample tagged with publish seq `S` joins the **FIRST** present whose acknowledged seq is `>= S`.

**Never an equal one.** The publisher is DropOldest last-writer-wins, so a published frame may never present at
all. A strict-equality join silently drops exactly the coalesced frames a cadence investigation is about, and the
resulting bucket reads "clean" for the one failure mode it was built to find.

A sample whose publish seq is `0` is a **labelled terminal**: that frame elided its submit, so it could never
present. It is a sample class, not a hole — a hole would make the pacing bucket look clean precisely when pacing
is the fault.

---

## What each `phases[].latency` field actually measures

- **`appliedVsIntendedDip`** — the resampled implied-finger offset minus what actually got applied, edge overscroll
  removed. **Healthy interior tracking is ~0 by construction**, because the applied offset *is* the clamped
  resampled one. That is the point: a non-zero value means the write did not land where the resampler asked, for a
  reason other than the extent — a rejected write, a competing writer, or an anchor that moved underneath.
- **`velocityDipPerMs`** — gesture speed from the two newest contact samples. Structural lag is
  `velocity × (resampleLatencyMs + measuredPresentLatencyMs)`. It is **not a defect**: the resampler deliberately
  targets a point behind frame time. Reconstruct it here rather than expecting the in-frame sensor to report it.
- **`wakeOverheadMs`** — the part of the frame gap the loop was absent *beyond the wait it asked for*. This
  separates **"we woke up late"** (pacing, DVFS) from **"we were slow"**. They are different bugs with different
  fixes. Cross-check against the note-113 rows in `scroll.csv`, which are the coarse hitch-gated form of the same
  discriminator.
- **`genStampQuality`** — provenance of the input stamp. Sub-frame latency percentiles are **refused** below
  `hardware`, and on a DirectManipulation touchpad (the primary producer on most machines) the honest grade is
  `receive`: the pump samples at roughly half the digitizer rate.

## What each `phases[].cadence` field actually measures

- **`frameOverrunMs`** — **signed**, against a deadline of `bufferCount × refreshPeriod`, not one 16.7 ms frame.
  With the render-thread seam plus the consume-gated quarantine the pipeline is several frames deep, and measuring
  against a single frame over-reports hitches for this architecture. Read the **distribution including the negative
  tail**: a healthy mean with a bimodal tail is the actual failure mode.
- **`clockSampleSkewMs`** — signed gap between the instant the frame's offset represents and when that frame was
  expected to be displayed. **A consistently non-zero mean is a finding, not noise**: it means the engine animates
  from the wrong instant, which produces the wrong amount of motion per frame with perfect FPS and zero missed
  vsyncs. That is precisely the "high FPS, still feels wrong" shape.
- **`missedVsyncsSum` vs `missedVsyncsMax`** — reported separately on purpose: frequency versus severity. One
  six-slot freeze and six one-slot drops have identical percentiles and feel completely different.
- **`presentIntervalMsMeanPlus2Sd`** — one scalar balancing throughput, outliers and consistency. Read it
  **alongside** the distribution, never instead of it.

---

## If you need to go deeper

1. `scroll.csv` — one row per traced event. Columns `tMs,frame,kind,i0,i1,i2,f0..f5,auxMs,state`. Slice by the
   packed `state` word: phase bits 0–3, gesture 4–5 (0 idle / 1 drag / 2 inertia / 3 settle), coldPass 6,
   repetition 7–10, A/B variant 11–12. `ops/diag/parse-scroll-csv.ps1` summarises it.
2. `console.txt` — `[fps]` per scroll-active frame, plus `[scrollperf]`, `[wakediag]`, `[render-census]`,
   `[renderbudget]`, `[OFFSET-JUMP]`. Every line carries `tMs=` from the same anchor as the CSV.
3. `manifest.json` — build, machine, display, power, and the full environment with each variable tagged
   `overridden` or `explicitlyCleared`.

**Before recommending a second capture**, say which switch would answer the open question and what it costs:
`-GpuTiming` adds real per-frame query overhead that peaks during the very fling being measured; `-Opaque`
removes the Mica composition path as one variable but requires a diag build.

**The bucket list is not a priority order.** `likelyContributorsUnranked` is exactly what its name says. It was
briefly sorted by `taggedFrames`, which was wrong: that field means a different thing per bucket — a peak
binds-per-frame here, a count of phases there, a frame count elsewhere — so sorting compared incommensurable
units and put whichever bucket used the largest one on top. Read each bucket's own evidence string, and take
sequencing from `fixOrder`, which is causal (upstream first) rather than numeric.

**A correlation is not a cause.** The image-decode and span buckets in particular can only be confirmed by
**bisection** — running the identical phase with that subsystem disabled and showing the cadence changes. Say so
rather than promoting a correlation to a verdict.

## Bisection — the only causal evidence in the kit

Check `validity.isObservation`. If `false`, `validity.bisectionArm` names the treatment and this bundle is
**not** a description of how the app behaves. Compare it only against a control captured with otherwise identical
switches, and say which two bundles you are comparing.

The one arm implemented today is **`-NoImagePump`**, which suppresses the phase-7.5 image pump while scroll is
active. It exists because `imageDecodeDuringScroll` is the bucket whose refuter is *defined* as a bisection: the
pump being busy during dropped presents does not make it the cause, and no amount of extra correlation will
separate the two. Read it as:

| Control | `-NoImagePump` arm | Reading |
| --- | --- | --- |
| presents dropped | presents clean | **confirmed** — the pump causes the drops; defer the apply past the gesture |
| presents dropped | presents dropped the same | **refuted** — the pump was merely busy; look upstream |
| presents clean | — | the session did not reproduce; bisecting proves nothing |

Sanity-check the arm actually engaged before trusting a "no change" result: a suppression that never fired reads
identically to "disabling it changed nothing", which is the opposite conclusion. The host counts the skipped pumps
for exactly this reason.

Prefer **one** bisection over five more metrics. A bisection yields a causal claim; metrics yield correlations.
