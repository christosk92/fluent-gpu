# `ops/diag` — scroll-feel capture

Tooling for one question: **Wavee reports high FPS while scrolling feels wrong — which end is at fault?**

This file owns the *mechanism*: what each flag does, what each metric means, and what the numbers cannot tell you.
The verdicts themselves are the engine's: `ScrollMetrics` over the `ScrollProbe` stream (Wavee's per-burst
`scroll.burst` log line, the Diagnostics ▸ Scroll card's CSV export, and the Scroll Lab's `metrics.json`). The
ScrollTrace-era packer and its rubric (`pack-feel-summary.ps1`, `AGENT.md`, `feel-summary.json`), the synthetic Wavee
injector (`synthetic-scroll-capture.ps1`) and its A/B reader (`analyze-cadence.py`) were retired with ScrollTrace —
they read `scroll.csv` latency rows and a phase marker that no build writes any more.

---

## Quick start

```powershell
# Capture: publishes a diag build, opens Wavee, waits until YOU close the window.
# Use the app however you want for 10-30 minutes. No gestures, no ratings, no ENTER.
ops\diag\wavee-scroll-session.cmd -Diag

# Same, already published:
ops\diag\wavee-scroll-session.cmd -Diag -SkipPublish

# Before closing, export the probe stream (Diagnostics ▸ Scroll ▸ Export CSV, level Trace) into the session folder;
# then tell an agent the folder — they read console.txt ([fps] / [render.pace] / scroll.burst lines) + the probe CSV.

# Optional paired arm — same free-scroll, plain Release, so observer cost is a number:
ops\diag\wavee-scroll-session.cmd

# Optional A/B arms, each as a SECOND session against a control with otherwise identical switches:
ops\diag\wavee-scroll-session.cmd -Diag -Opaque             # --fg opaque: opaque HWND swapchain instead of DWM Mica
ops\diag\wavee-scroll-session.cmd -Diag -PresentInterval0   # --fg no-vsync: separates the present cap from frame cost
ops\diag\wavee-scroll-session.cmd -Diag -GpuTiming          # --fg gpu-timing: per-pass GPU timeline (real per-frame cost)

# Instrument self-test (no human): proves the build armed and the pipeline works. Never a feel result.
ops\diag\wavee-scroll-session.cmd -Diag -Unattended

# Unattended A/B (synthetic wheel packets, not a human) — the Scroll Lab, one run per build, compare metrics.json:
powershell -File ops\diag\scroll-lab-synthetic.ps1 -ExePath <build A>\FluentGpu.ScrollLab.exe
powershell -File ops\diag\scroll-lab-synthetic.ps1 -ExePath <build B>\FluentGpu.ScrollLab.exe
```

Bundles land in `ops/diag/sessions/`, which is gitignored. The scripts are tracked; the captures are not — a
bundle records an exe hash, a panel, a power state and a free-scroll trace, so it is evidence for one
investigation rather than a repo artifact.

---

## The two pillars

| Pillar | Question | Chain |
| --- | --- | --- |
| **A — glued** | Does the content sit where the finger is? | OS packet → producer queue → engine ring → latch/resample → offset commit |
| **B — steady** | Are submit-confirmed presents evenly paced? | offset → record DrawList → PUBLISH → render thread submit → Present |

They stay **structurally separate** in every artifact. Interventions trade one against the other — a pacing queue
improves cadence and worsens latency — so a fused "smoothness score" would score that trade as a wash and hide
both. Never average a pillar-A number with a pillar-B number.

---

## Build gate: `FLUENTGPU_DIAG`

`dotnet publish /p:FluentGpuDiag=true`, or `ops\build\publish-wavee-aot.ps1 -Diag`.

Defined by a `PropertyGroup` in **both** `src/Directory.Build.props` **and** WaveeMusic's root `Directory.Build.props` —
`src/apps/` deliberately does not inherit the engine props, and `[Conditional]` erasure is decided by the
**calling** assembly, so the app's own trace call sites stay erased if only the engine gets the symbol.

Without it a publish loses the `[renderbudget]` roster (`--fg render` is a no-op: `RenderBudget.CompiledIn` folds to
false) and the DEBUG guards. The `[fps]` line (`--fg fps`), the layout counts (`--fg layout`), the always-on `[wake]`
census and the opaque A/B (`--fg opaque`) all still work in plain Release, which is what makes the paired arm
possible.

It is **not** `FGGUARD`: the render-seam thread asserts stay erased, so the diag build's threading behaviour
matches Release. It is also distinct from the separately planned `FG_DEVTOOLS` symbol — do not conflate them.

**The diag build is a different binary.** The `BindContract` and `BackwardsWriteGuard` guards (and the one-surface-
per-player guard) are **default-ON** once compiled in, and `BackwardsWriteGuard` does a subscriber-list scan per
signal write. The launcher turns them off with `--fg no-guards`; if you launch by hand, pass it too or you are
measuring a different app from the one being complained about.

> **Toggling the posture needs `--no-incremental`.** MSBuild's up-to-date check does not notice that
> `/p:FluentGpuDiag` changed, so a plain build after a diag build silently keeps the diag-compiled assemblies.
> The symptom is subtle and looks like a real regression: `gate.arena.alloc-zero` starts failing at a few hundred
> bytes because the trace and budget probes are still compiled in. Always
> `dotnet build src/FluentGpu.slnx --no-incremental` when switching, and re-run the slice before believing a
> zero-alloc failure. The publish script sidesteps this by writing diag output to its own directory tree.

---

## Engine switches — `--fg name[,name...]`

**The engine reads no environment variables.** Its diagnostic switches are one command-line list,
`--fg name[,name...]` (or `--fg=name,...`), applied by `FluentApp` from the host process's own arguments before the
window exists (`FluentGpu.Hosting.EngineSwitches`, `src/FluentGpu.Engine/Hosting/EngineSwitches.cs`). Every FluentApp
host — Wavee, the gallery, the benches — accepts it; an unknown name is reported once on stderr and ignored. The
launcher records the exact list in `manifest.json` (`engineSwitches` = name → reason, `engineSwitchArg` = the literal
`--fg …` argument), so a bundle is never read under the wrong assumption about what was on.

| Switch | Needs `FLUENTGPU_DIAG` | Session default | What it gives |
| --- | --- | --- | --- |
| `fps` | no | **on** | the `[fps]` line: loop + present cadence, per-phase ms, wait kind, seam deltas |
| `layout` | no | **on** | measure/arrange/text-shape printout; without it the `FrameTiming` i1 column is structurally 0 |
| `render` | **yes** | on with `-Diag` | `[renderbudget]` every-frame re-render roster (`RenderBudget`) + the device's submitted-area census |
| `no-guards` | (only meaningful when compiled in) | on with `-Diag` | turns off the default-ON DEBUG guards (`BindContract`, `BackwardsWriteGuard`, one-surface-per-player) — mandatory for any measurement |
| `opaque` | no | `-Opaque` | A/B arm: opaque HWND swapchain instead of DWM Mica. A **behaviour fork** |
| `gpu-timing` | no | `-GpuTiming` | starts with the pass-granular GPU timeline on (`AppHost.GpuPassTimingEnabled`) — extra timestamp queries every frame |
| `no-vsync` | no | `-PresentInterval0` | present at sync-interval 0 (separates the present cap from the frame cost) |
| `diag` | **yes** | **never in a feel session** | engine `Diag` on with its stderr sink — see below |
| `mem` / `mem=N`, `alloc`, `alloc-types` | no | off | memory census every N s (default 5) / allocation probes — separate runs |

Always on, nothing to enable: the `[wake]` census (one line per 30 s: fps, the reconciled / layout-only /
record-only split, `skipMiss`, the kept/sole wake-reason roster), the per-frame `RenderFrameCensus` (repaint set,
span reuse, capture, the device's per-submit counters, the retained-tile census) and the tile census the Wavee
Diagnostics **Tiles** card reads. The per-component `[render-census]` line is the runtime property
`AppHost.RenderCensus` (set in code — the Wavee Diagnostics page), not a switch.

**Retired environment variables** (none is read any more; a leftover in a script does nothing): `FG_FPS_LOG` →
`--fg fps`; `FG_LAYOUT_DIAG` → `--fg layout`; `FG_RENDER_DIAG` → `--fg render`; `FG_BIND_CONTRACT=0` /
`FG_BACKWARDS_WRITE=0` → `--fg no-guards`; `FG_OPAQUE_WINDOW` → `--fg opaque`; `FG_GPU_TIMING` → `--fg gpu-timing`
(or the runtime toggle); `FG_SCROLL_PRESENT_INTERVAL0` → `--fg no-vsync`; `FG_DIAG` / `FG_DIAG_CONSOLE` → `--fg diag`;
`FG_MEM_DIAG` → `--fg mem`. Deleted outright, with no replacement switch: `FG_SCROLL_PERF` (`[scrollperf]`),
`FG_OFFSET_JUMP` (`[OFFSET-JUMP]`), `FG_SCROLL_LOG` / `FG_SCROLLLOG`, `FG_SCROLL_TRACE` and `FG_SCROLL_PHASE_FILE` (the
`ScrollTrace` POD ring is deleted — per-input scroll traces are the Wavee Diagnostics **Scroll** card's CSV export,
`ScrollProbe` level Trace), `FG_BISECT_NO_IMAGE_PUMP` (the `-NoImagePump` arm is gone), and `FG_RENDER_CENSUS` (now
`AppHost.RenderCensus`). `FG_WAKE_DIAG` was retired earlier — the `[wake]` census is always on.

### Switches deliberately excluded from the default set

- **`--fg diag`.** `Diag.Count`/`Set` concatenate a string and box a value under one process-global lock, roughly
  twenty times per frame inside the submit path — on the **render thread** under the async default, contending with
  UI-thread callers, inside the exact code path being measured. There is no events-only mode.
- **`--fg gpu-timing` (per-pass GPU attribution).** The pass-granular GPU timeline is `IGpuDevice.GpuPassTimingEnabled`
  (host: `AppHost.GpuPassTimingEnabled`), read back per retired frame with `ISwapchain.CopyGpuPassTimeline` /
  `AppHost.CopyGpuPassTimeline` (`Seams/Rhi/GpuFrameTelemetry.cs`); `--fg gpu-timing` only starts a run with it on. It
  timestamps only at pass boundaries (never between two draws into one target), but it is still extra queries per
  frame: get GPU busy-vs-wait from PresentMon (or the always-on whole-frame `gexec`) first, and turn it on only after
  the GPU is implicated — mid-session from the Scroll Lab HUD's **GPU passes** switch or the Wavee Diagnostics
  **Tiles** card. The always-on per-submit counters (`FrameStats.RenderCensus.Device`) need no toggle at all.
- **`dotnet-trace`.** Dominant observer effect. Never in a feel session.

### "Probes are zero-cost when off" — the accurate version

**Erased when compiled out; one well-predicted branch when compiled in and disabled.** True erasure applies to
`Diag` and `RenderBudget` (compile-time `const false` or `[Conditional]`). The `EngineSwitches` fields themselves are
plain statics read at their call sites, so `fps` / `layout` / `opaque` cost one branch per site in every build.

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

> **Forward-looking:** today the DirectManipulation touchpad path stamps at `GenStampQuality.Receive` — the pump
> samples on its own rate, quantised against the digitizer rather than timestamped per contact. It is expected to
> reach `GenStampQuality.Hardware` once the Phase-3 Windows-producer rewrite lands, delivering one delta per frame
> stamped at the frame's own contact, not today's pump-rate-quantised sample.
| `LatencyWaitMs` split | `D3D12Device` | the DXGI latency waitable (present backpressure) was hidden inside the broader CPU fence/back-buffer-retirement wait |
| per-swapchain `gexec <ms>#<seq>` | `D3D12Device` → `[fps]` | adaptive pacing and offline analysis no longer mistake CPU fence retirement for current-frame GPU execution; repeated log observations are deduplicated by sequence |
| coherent `rq`, `rareaMp`, `rareaSeq`, `btop` snapshot | swapchain backend → `[fps]` | the async logger could repeat or race mutable render-thread counters; one target-local sequence now identifies the instance/area/top-N payload that the packer deduplicates |

### Gates

`gate.latency.join-forward` (a latency sample joins the FIRST present whose acked publish-seq is ≥ its own, so a
DropOldest-coalesced publish joins forward instead of being dropped) and `gate.latency.probe-alloc-zero`
(`ScrollProbe.Pose` records at the shipping Summary level allocate 0 bytes) — `DiagnosticsSuite`. The
`ScrollTrace`-ring gates (`kind-names-parity`, `state-pack`, `alloc-zero`) went with the ring. The `ScrollTrace` rows in
the table above are historical: the ring is deleted and per-input traces come from `ScrollProbe` (the Wavee
Diagnostics Scroll card's CSV export).

The offline schema gate for the legacy-capture reader (`parse-scroll-csv.ps1`, for ScrollTrace captures taken before
the ring was deleted) is dependency-free and runs under the same Windows PowerShell 5.1 runtime as the capture tools:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\diag\test-feel-diagnostics.ps1
```

It locks JSON array cardinality, targeting-candidate classification and explicit/legacy tracking provenance.

---

## Known measurement traps

These have each produced a wrong conclusion before.

- **`frame` is not a join key.** It counts Paint phase 7 only (loop early-outs never reach it), is written
  without synchronisation, and suppresses no-input micro-frames. Join on `tMs` or on the latency row's publish seq.
- **The `frameTiming` i2 column is UNCLAMPED dt.** A minute of idle appears there as a 59-second "frame". The
  committed capture contains exactly one such row. Gate on scroll-active or the percentile is fiction.
- **`2>` loses the `[scrolltrace]` banner**, which goes to *stdout*. The previously committed capture has 476
  `[fps]` lines and zero scrolltrace lines for precisely this reason. Capture both streams.
- **(Historical) the deleted `ScrollTrace` ring broke the zero-alloc gates by design** — its idle flush allocated a
  writer and formatted strings inside the frame (`gate.arena.alloc-zero` failed at ~219 KB with it armed). Its
  successor, the Wavee Scroll card's `ScrollProbe` Trace level, is an app-side, opt-in capture: still never compare a
  probe-armed run against a plain one.
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
  it structurally cannot see present cadence, DropOldest, or display-side smoothness. It answers a different question
  (CPU work cost with presentation suppressed) and keeps its own summary format; the engine's `ScrollMetrics` owns the
  feel-verdict vocabulary.
- **`ops/scratch/run-wavee-hitch.bat` and `run-playlist-regression-capture.bat` are deleted** — they targeted the
  pre-split `src/apps` path and set only environment variables the engine no longer reads. These tracked scripts
  replace them.

## `-Unattended` — validates the instrument, never the feel

Launches Wavee, idles a few seconds, closes it; the manifest is stamped `captureMode: instrumentCheck`. Use it to
answer *"does the toolchain work?"* — did the diag build arm, did the anchor land, did the streams merge. Use it for
nothing else: with no input there is no scroll, so there is nothing to score.

## Synthetic capture: the Scroll Lab

`scroll-lab-synthetic.ps1` launches the Scroll Lab, records (F10) a ~10 s session driven by `SendInput` wheel packets on
a fixed script — isolated detented notches, a spin, an F8 marker, a reversal, four hi-res bursts — and prints the
session folder (`events.csv` = `ScrollProbe.ExportCsv` rows, `frames.csv`, `markers.json`, `metrics.json`). Two builds
run the same script, so their `metrics.json` compare without gesture variation: tracking AND cadence verdicts, scored by
the engine's `ScrollMetrics` (`docs/guide/scroll-lab.md`). It does not exercise the DirectManipulation touchpad path
(DM contacts are real HID packets and cannot be synthesized) — touchpad feel is judged from live lab sessions, whose
contact Input rows the probe keeps at Summary level.

## Bisection: the only causal evidence here

Every other signal in this kit is a **correlation**. A causal claim needs a second session with ONE subsystem
changed and otherwise identical switches, compared against the control. The image-pump bisection arm
(`-NoImagePump` / `FG_BISECT_NO_IMAGE_PUMP`) is **deleted** — image applies are now metered by the one per-turn upload
budget (`UploadBudget.BytesPerTurn`) and uploads ride the copy queue off the frame — so the arms the launcher still
offers are A/B forks, not suppressions: **backdrop off** (`-Opaque`, `--fg opaque`) and **present cap off**
(`-PresentInterval0`, `--fg no-vsync`). Read `manifest.json`'s `engineSwitches` to confirm which arm a bundle is, and
fence any new arm the same way: one switch, recorded with its reason, compared against a control.

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
