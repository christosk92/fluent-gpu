# `ops/diag` — scroll-feel capture

Tooling for one question: **Wavee reports high FPS while scrolling feels wrong — which end is at fault?**

`ops/diag/AGENT.md` is the diagnosis rubric and the owner of the verdict vocabulary. It ships inside every
bundle. This file owns the *mechanism*: what each flag does, what each metric means, and what the numbers cannot
tell you.

---

## Quick start

```powershell
# 0. Read what is already on disk before building anything.
ops\diag\parse-scroll-csv.cmd ops\scratch\hitch-measure-20260723-191423.scroll.csv

# 1. (optional) Prove PresentMon can even see our swapchain on this machine. Wavee must be running and scrolling.
#    Needs elevation OR membership in Performance Log Users — it opens an ETW session. If neither is available,
#    SKIP IT: the launcher records why, and the in-app DXGI/DWM statistics (always compiled in) become the
#    present-side truth. That is a supported configuration; what you lose is the independent witness.
ops\diag\probe-presentmode.cmd

# 2. Capture a guided interactive session (publishes a diag build, prompts you through 7 gestures, packs a bundle).
ops\diag\wavee-scroll-session.cmd -Diag

# 3. The mandatory paired arm — same gestures, plain Release, so observer cost is a number and not an assumption.
ops\diag\wavee-scroll-session.cmd
#    then fold it in — this is what turns observerDeltaVsPlainReleasePct from null into a measurement:
ops\diag\pack-feel-summary.cmd -Session <diag session> -Control <plain-Release session>

# 4. The bisection — the only causal evidence in the kit. Same switches as (2) plus the treatment.
ops\diag\wavee-scroll-session.cmd -Diag -NoImagePump

# Instrument self-test (no human, no gestures): proves the build armed and the pipeline works end to end.
# Produces a bundle stamped synthetic — it can NEVER answer a question about feel.
ops\diag\wavee-scroll-session.cmd -Diag -Unattended

# Re-pack an existing bundle after editing the packager:
ops\diag\pack-feel-summary.cmd -Session ops\diag\sessions\<id>
```

Bundles land in `ops/diag/sessions/`, which is gitignored. The scripts are tracked; the captures are not — a
bundle records an exe hash, a panel, a power state and a human's subjective scores, so it is evidence for one
investigation rather than a repo artifact.

---

## The two pillars

| Pillar | Question | Chain |
| --- | --- | --- |
| **A — glued** | Does the content sit where the finger is? | OS packet → producer queue → engine ring → latch/resample → offset commit |
| **B — steady** | Do photons change on an even cadence? | offset → record DrawList → PUBLISH → render thread submit → Present → vblank |

They stay **structurally separate** in every artifact. Interventions trade one against the other — a pacing queue
improves cadence and worsens latency — so a fused "smoothness score" would score that trade as a wash and hide
both. Never average a pillar-A number with a pillar-B number.

---

## Build gate: `FLUENTGPU_DIAG`

`dotnet publish /p:FluentGpuDiag=true`, or `ops\build\publish-wavee-aot.ps1 -Diag`.

Defined by a `PropertyGroup` in **both** `src/Directory.Build.props` **and** `src/apps/Directory.Build.props` —
`src/apps/` deliberately does not inherit the engine props, and `[Conditional]` erasure is decided by the
**calling** assembly, so the app's own trace call sites stay erased if only the engine gets the symbol.

Without it a publish loses: `scroll.csv` entirely (the ring is `const bool On = false`), the `[renderbudget]`
roster, and the `FG_OPAQUE_WINDOW` A/B. The console streams (`[fps]`, `[scrollperf]`, `[wakediag]`,
`[render-census]`, `[OFFSET-JUMP]`) all still work in plain Release, which is what makes the paired arm possible.

It is **not** `FGGUARD`: the render-seam thread asserts stay erased, so the diag build's threading behaviour
matches Release. It is also distinct from the separately planned `FG_DEVTOOLS` symbol — do not conflate them.

**The diag build is a different binary.** `FG_BIND_CONTRACT` and `FG_BACKWARDS_WRITE` become **default-ON** once
compiled in, and the latter does a subscriber-list scan per signal write. The launcher clears both explicitly;
if you launch by hand, do the same or you are measuring a different app from the one being complained about.

> **Toggling the posture needs `--no-incremental`.** MSBuild's up-to-date check does not notice that
> `/p:FluentGpuDiag` changed, so a plain build after a diag build silently keeps the diag-compiled assemblies.
> The symptom is subtle and looks like a real regression: `gate.arena.alloc-zero` starts failing at a few hundred
> bytes because the trace and budget probes are still compiled in. Always
> `dotnet build src/FluentGpu.slnx --no-incremental` when switching, and re-run the slice before believing a
> zero-alloc failure. The publish script sidesteps this by writing diag output to its own directory tree.

---

## Flag table — including how each one is *read*

The read idiom matters: three different conventions are in play, and using the wrong spelling silently disables
a probe, which then reports an empty bucket that looks like a clean result.

| Flag | Read as | Works in plain Release | What it gives |
| --- | --- | --- | --- |
| `FG_FPS_LOG` | `EnvFlag` (`1`/`true`/`on`) | yes | the `[fps]` line: loop + present cadence, per-phase ms, wait kind, seam deltas |
| `FG_SCROLL_PERF` | `EnvFlag` | yes | `[scrollperf]` 1 Hz roll-up — the scroll-bind thrash evidence |
| `FG_WAKE_DIAG` | `EnvFlag` | yes | `[wakediag]` reconciled / layout-only / record-only split + wake-reason roster |
| `FG_RENDER_CENSUS` | `EnvFlag` | yes | `[render-census]` — **suppressed unless flush ≥ 12 ms or comps ≥ 25** |
| `FG_OFFSET_JUMP` | **`== "1"` exactly** | yes | `[OFFSET-JUMP]` large single-write jumps. `true`/`on` silently **disable** it |
| `FG_LAYOUT_DIAG` | `EnvFlag` | yes | measure/arrange/text-shape counts; without it the `FrameTiming` i1 column is structurally 0 |
| `FG_SCROLL_TRACE` | non-empty, `≠ "0"` | **no** | the POD ring. Any value other than `1` is used verbatim **as the output path** |
| `FG_SCROLL_PHASE_FILE` | path | **no** | the capture-protocol phase marker, polled from the host loop |
| `FG_RENDER_DIAG` | `EnvFlag` | **no** | `[renderbudget]` every-frame re-render roster |
| `FG_BIND_CONTRACT` | `EnvFlagDisabled` — **default ON** | **no** | set to `0` for any measurement |
| `FG_BACKWARDS_WRITE` | `EnvFlagDisabled` — **default ON** | **no** | set to `0` for any measurement |
| `FG_OPAQUE_WINDOW` | `EnvFlag` | **no** | A/B arm: opaque HWND swapchain instead of DWM Mica. A **behaviour fork** |
| `FG_BISECT_NO_IMAGE_PUMP` | `EnvFlag` | **no** | BISECTION arm: suppress the phase-7.5 image pump while scroll is active. A **behaviour fork** |
| `FG_GPU_TIMING` | `EnvFlag` | yes | Tier 2. Per-pass GPU attribution at real per-frame cost — see below |
| `FG_DIAG` / `FG_DIAG_CONSOLE` | `EnvFlag` | **no** | **never in a feel session** — see below |

### Flags deliberately excluded from the default set

- **`FG_DIAG` / `FG_DIAG_CONSOLE`.** `Diag.Count`/`Set` concatenate a string and box a value under one
  process-global lock, roughly twenty times per frame inside the submit path — on the **render thread** under the
  async default, contending with UI-thread callers, inside the exact code path being measured. There is no
  events-only mode; the two flags are identical.
- **`FG_GPU_TIMING`.** Up to 256 extra `EndQuery` per frame from the category-boundary timeline, plus a
  fixed-size resolve **every** frame. The boundary count **peaks** on a dense fill → image → glyph list — i.e.
  maximally during the fling being diagnosed. Get GPU busy-vs-wait from PresentMon at zero in-app cost first, and
  turn this on only after the GPU is already implicated.
- **`FG_SCROLL_PRESENT_INTERVAL0`.** Unreachable without `FG_GPU_TIMING` (it is gated on `gpuRenderMs > 0`), so
  it is a paired arm, never an independent switch. The launcher enforces that.
- **`FG_SCROLL_LOG` / `FG_SCROLLLOG`.** Per-event `Console.WriteLine` with `AutoFlush`. Its own class doc warns
  that it perturbs pacing.
- **`dotnet-trace`.** Dominant observer effect. Never in a feel session.

### "Probes are zero-cost when off" — the accurate version

**Erased when compiled out; one well-predicted branch when compiled in and disabled.** True erasure applies to
ScrollTrace, `Diag`, and `RenderBudget` (compile-time `const false` or `[Conditional]`). It does **not** apply to
`FG_OFFSET_JUMP`, `FG_SCROLL_LOG`, `FG_SCROLLLOG` or `FG_SCROLL_PERF`, which are plain `static readonly bool`s
costing one branch per call site in every build.

---

## What the instrumentation added, and where

Two missed-vsync counts are reported side by side and never averaged: ours, derived from a QPC stamp taken after
`Present()` returns, and the **OS-attested** one from DXGI `PresentRefreshCount` deltas. The attested figure
supersedes ours wherever it exists — it is what the display pipeline actually did, not what our timestamp implies.
It is carried biased by +1 in the trace so "not attested" stays distinguishable from "attested zero missed"; those
are opposite conclusions and a bare 0 would merge them.

| Signal | Where | Why it did not exist before |
| --- | --- | --- |
| `presentQpc` + `presentPublishSeq` | `AppHost.NotePresented` | present had a monotonic **count** but no frame identity, so nothing could say *which* frame's content was on screen |
| `PublishSequence` / `ConsumedSequence` / `RenderPresentAck` | `AppHost` | the publisher's DropOldest drops were counted **nowhere** |
| `FrameStats.ScrollActive` / `.PublishSeq` | `AppHost` | the scroll bit was computed per frame and never surfaced |
| `ScrollTraceKind.Latency` | `Foundation/ScrollTrace.cs` | the one row that carries a frame identity across the render seam |
| the `[scrolltrace] anchor` line + `tMs=` prefix | `ScrollTrace` / `FluentApp` | **no two artifacts shared a clock**; the console streams carried no timestamp at all |
| packed ambient state (`state` column) | `ScrollTrace.SetState` | offline slicing by phase / gesture state / A-B arm, with no per-frame filesystem work |
| `PresentStats` (DXGI + DWM) | `Rhi` seam + `D3D12Device` | the measured refresh period and the vblank-attested cadence had no source at all |
| detented-wheel `QpcTicks` | `Win32Platform` | the wheel path enqueued stamp `0`, silently degrading every wheel latency figure to message time |
| `GenStampQuality` | producers → `ScrollTrace` | so the packager can **refuse** to publish sub-tick percentiles off a `receive`-grade stamp |
| `LatencyWaitMs` split | `D3D12Device` | the frame-latency wait (backpressure) was summed with the fence wait (GPU work); they have opposite fixes |

### Gates

`gate.latency.kind-names-parity` is the important one. `ScrollTrace.FlushLocked` indexes the kind-name table
unguarded, inside a swallow-all `catch` that then zeroes the pending count — so adding a record kind without its
name throws on the first row of that kind, the catch eats it, and **every buffered row of the session is
discarded with no error printed anywhere**. The operator sees a short CSV and concludes "not much happened".

Also `gate.latency.state-pack`, `gate.latency.alloc-zero` and `gate.latency.join-forward`.

---

## Known measurement traps

These have each produced a wrong conclusion before.

- **`frame` is not a join key.** It counts Paint phase 7 only (loop early-outs never reach it), is written
  without synchronisation, and suppresses no-input micro-frames. Join on `tMs` or on the latency row's publish seq.
- **The `frameTiming` i2 column is UNCLAMPED dt.** A minute of idle appears there as a 59-second "frame". The
  committed capture contains exactly one such row. Gate on scroll-active or the percentile is fiction.
- **`2>` loses the `[scrolltrace]` banner**, which goes to *stdout*. The previously committed capture has 476
  `[fps]` lines and zero scrolltrace lines for precisely this reason. Capture both streams.
- **Arming `FG_SCROLL_TRACE` breaks the zero-alloc gates by design.** The ring's idle flush allocates a writer and
  formats strings *inside* the frame. `gate.arena.alloc-zero` fails at ~219 KB on a clean tree with the trace
  armed. Never compare a trace-armed VerticalSlice run against a plain one.
- **`gate.arena.alloc-zero` is intermittently flaky on its own.** Measured on a clean tree at `d082d67`: it failed
  4 of 5 consecutive runs of the same binary, at 2112–2208 bytes, with no code change between runs. **Never treat a
  single failing run as a regression** — run the slice 3–5 times and compare failure *rates* and byte counts
  against a baseline built the same way. The plan's advice to diff against a baseline rather than against an
  absolute pass count exists for exactly this.
- **A missing signal is not a zero.** A capture with no `phase`/`latch` rows is **wheel-only** and says nothing
  about the touchpad path. Reporting "no touchpad problems" from it is a lie of omission.
- **`RenderBudget` is a no-op without `FLUENTGPU_DIAG`**, so an empty roster in a plain-Release bundle is
  `notMeasured`, not a refutation.

---

## Honest error budget — copy this into any report

- **0–8 ms unmeasured device-to-host latency** on a touchpad at 125 Hz reporting (0–1 ms on a 1000 Hz mouse).
- A present stamp is taken immediately after `Present()` returns: **submit-confirmed, not vblank-confirmed**.
- Even the OS-attested `PresentRefreshCount` is the **vblank / start of scanout** — a pixel at row *Y* on a
  top-down panel lights `(Y / height) × refreshPeriod` later. Panel response, overdrive and backlight are invisible.
- **~1 frame of attribution uncertainty** for a pipelined engine: under async, a present may carry content
  published an unknown number of frames earlier.
- **Tracking lag is biased toward zero during fast flings**: both the OS and the engine's input ring coalesce
  packets before any delta arithmetic sees them.

**Never write "photon".** The measurable quantity is `inputToVblankOfPresent`.

---

## Scope: what these tools do *not* cover

- **Only the interactive offset path is instrumented.** Roughly a dozen sites write `ScrollState.Offset*`
  directly and never reach the chokepoint — scroll restore and clamp, virtualisation anchor re-pin, reconciler
  restore and keyless reset, and the items/grid/shelf/tab/tree control resets. A clean `offsetDiscontinuity`
  bucket is **not** proof that no non-interactive path jumped. A real single-writer chokepoint is a genuine
  refactor and out of scope for diagnostics.
- **`WaveeNavProbe` is a budget/regression harness, not this.** It calls `SuppressLatencyWaitOnce()` +
  `SuppressVsyncOnce()` per measured frame — it deliberately **removes the present path**, which is precisely why
  it structurally cannot see present cadence, DropOldest, or photon smoothness. It answers a different question
  (CPU work cost with presentation suppressed) and keeps its own summary format; `ops/diag/AGENT.md` owns the
  feel-verdict vocabulary.
- **The three `ops/scratch/run-*.bat` files are untracked**, so replacing them with these tracked scripts is not
  "retiring tracked tooling". For the record: `run-wavee-hitch.bat` sets `FG_GPU_TIMING=1` and `FG_DIAG=1` (not
  `FG_MEM_DIAG`) and targets the JIT `bin/` directory rather than a publish; `run-playlist-regression-capture.bat`'s
  dominant observer effect is its `dotnet-trace` wrapper, not its environment.

## `-Unattended` — validates the instrument, never the feel

Runs the phase timeline with no human and no gestures, stamping every phase `synthetic: true` with **null** scores
(null, not 0 — a 0 would average into a "bad" score and manufacture a complaint out of an empty run). The packager
marks the bundle `humanObserved: false` and excludes synthetic phases from the reproduction question entirely.

Use it to answer *"does the toolchain work?"* — did the diag build arm, did the anchor land, did the streams merge,
did the phase marker reach the app, did the packager run. Use it for nothing else. With no input there is no scroll,
so there are no latency rows, and the packager will correctly hard-fail on that: **a hard fail here is the expected
and correct result**, and seeing it is itself the proof that the hard-fail path works.

## Bisection: the only causal evidence here

Every other signal in this kit is a **correlation**. `-NoImagePump` (`FG_BISECT_NO_IMAGE_PUMP`) suppresses the
phase-7.5 image pump while scroll is active — decodes still complete on their workers and are applied the moment
the gesture settles, which is deliberately the shape a real fix would take, so a positive result names an
intervention rather than just an accusation.

Run it as a **second** session with otherwise identical switches, and compare against the control. The bundle is
stamped `validity.isObservation: false` + `validity.bisectionArm`, and the packager refuses to let it be read as a
description of how the app behaves. Before believing a "no change" result, confirm the suppression actually
engaged — a treatment that never fired reads exactly like a treatment that did nothing.

The recommended order, cheapest causal evidence first: **image pump off** → **backdrop off** (`-Opaque`, already
available) → DropOldest→block. Fence any new arm the way these two are fenced.

## Not yet built (named so buckets are not silently built on proxies)

- **Content approximation** — the fraction of visible area presented from a stale span or an undecoded image
  while scroll-active. Until it exists, the span and image buckets rest on *proxies*: span counters describe what
  the engine **did**, not what the user **saw**.
- **`GetPointerInfoHistory` drain** — would remove the coalescing bias in tracking lag. The DirectManipulation
  path, which is the primary touchpad producer on most machines, has no history API at all, so it fixes less than
  it appears to.
- **ETW self-instrumentation** — would give an exact frame-id join with external tooling and fix PresentMon's
  input attribution, which binds input to the *next* present start and therefore mis-attributes for a pipelined
  UI → publish → render-thread engine.
