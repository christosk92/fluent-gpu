# The Scroll Lab — measuring scroll feel instead of describing it

> Canon for everything the lab measures: [`../design/subsystems/scroll.md`](../design/subsystems/scroll.md) (§10 owns the
> probe, the metrics and the lab). Agent skill: `.claude/skills/fluentgpu-scroll/`. Plan (history):
> `../plans/scroll-lab-implementation.md`.

"It feels heavy", "it's jittery", "the fling is floaty" are hypotheses. The Scroll Lab turns them into **recorded
sessions, metrics and verdicts** against the default feel's own model (`FeelProfiles.Standard`), and lets you
**live-tune every `MotionFeel` knob** while you scroll. It is a standalone engine exe — no WinAppSDK — built from the
same engine as every FluentGpu app, so what it measures is the engine's real scroll path: the render-thread poser, the
coverage clamp, the retained tiles, the present pacing.

---

## Build, run, publish

```powershell
# iterate (JIT)
dotnet run --project src/FluentGpu.ScrollLab
dotnet run --project src/FluentGpu.ScrollLab -c Release

# the measurement build — NativeAOT, one native exe (use this for numbers you intend to compare)
dotnet publish src/FluentGpu.ScrollLab -c Release -r win-arm64      # or -r win-x64
```

Projects: `src/FluentGpu.ScrollLab` (WinExe, `PublishAot`; refs Engine + Controls + Windows + WindowsApi) and
`src/FluentGpu.ScrollLab.Surfaces` (the surfaces, refs Engine + Controls only — TerraFX-free, so headless suites can mount
them). Both are in `src/FluentGpu.slnx`.

The lab opens its window with the **adaptive GPU governor off and no post-input warm hold** (`Program.cs`:
`AdaptiveGpuPacing = false`, `WarmCadenceMs = 0`) so a capture sees the raw cadence. It attaches to the live host through
`FluentApp.DiagnosticRun` (returning false — the normal interactive loop keeps running) and taps
`FluentApp.FrameCompleted` to drain the probe rings every frame (`Lab/LabHost.cs`). On attach it raises
`ScrollProbe.Level` to at least `Summary` so the HUD has notches and poses.

---

## Screens

```
┌ Scroll Lab  [Surface|Record|Analysis]              ● REC  [Felt wrong F8][Record F10][Tuning Ctrl+T][Tuning window] ┐
│ [Fixed 100k|Measured|Edge cases]                                                                                     │
│ ┌ surface (one ScrollHandle) ────────────────────────────┐ ┌ HUD ───────────────┐ ┌ tuning pane (Ctrl+T) ──────────┐ │
│ │ ListRowEl rows …                           barcode ▌   │ │ offset / velocity  │ │ preset · knobs · Δ vs preset    │ │
│ └────────────────────────────────────────────────────────┘ └────────────────────┘ └─────────────────────────────────┘ │
│ F8 felt wrong   F10 record/stop   Ctrl+T tuning pane                                               status line        │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Surface** — the scroller under test plus the HUD. Surfaces (`FluentGpu.ScrollLab.Surfaces`):
  - **Fixed 100k** — 100 000 uniform 56-DIP rows through `ItemsView.CreateBound`, each ONE `ListRowEl` (8 cells): the
    Wavee track list at its worst-case length.
  - **Measured** — 2 000 rows of 40–140 DIP, seeded at a 90-DIP estimate and corrected on arrange, so extent corrections
    and anchoring run under real scrolling.
  - **Edge cases** — three rows (no scroll range), content exactly the viewport (max offset 0), and no content.
  Every row draws a **position barcode** (`PositionBarcode.cs`: the row index in binary + a sub-row phase ramp) for the
  planned optical measurement. Switching surface remounts it with a fresh `ScrollHandle`; pages stay mounted
  (KeepAlive), so the surface keeps its position while you are on Record/Analysis.
  **HUD** (bound text, throttled to 20 Hz while moving so the lab does not perturb what it measures): offset, velocity,
  motion kind, notch→pose lag, DIP per notch, notches, coverage clamps, lost probe rows, the active feel (`★ edited`
  when it differs from its preset), a pose strip (|Δp| per frame), and the **GPU passes** toggle with the latest pass
  summary.
- **Record** — session name, probe level (**Summary** = notches, poses, presents; **Trace** = every stage), Start/Stop
  (the same toggle as F10), and the saved sessions newest-first with **Analyze** and **Open dir**. Sessions record
  whatever the Surface page's active surface does.
- **Analysis** — one verdict tile per metric (value, baseline, R/A/G, the F8 markers near its worst sample) and one
  offset-vs-time chart with the wheel notches as marks and the F8 presses as bands.
- **Tuning** — the same panel in the main window (Ctrl+T pane) and in a detached window (Ctrl+Shift+T): preset picker
  (built-ins `Standard`/`Glide` + saved presets), **Save as**, **Import JSON…**, **Export JSON…**, **Reset to
  preset**, one row per `ScrollTunables.All` knob (slider over its range · number box · the preset's value · ★ when
  changed) and a **Δ vs preset** list. Every edit is live on the next notch or lift (`ScrollTunables.Apply`) and fenced in
  the trace with a `TunableChanged` mark (at most one per 100 ms while a slider drags). Presets live in
  `%LOCALAPPDATA%\FluentGpu\ScrollLab\presets\*.json` in exactly `ScrollTunables.ToJson`'s shape.

### Hotkeys

| Key | Action | Status |
|---|---|---|
| **F8** | "That felt wrong" — a `ProbeMark.UserFelt` marker; the analysis attaches it to the nearest worst sample | shipped |
| **F10** | Start / stop recording | shipped |
| **Ctrl+T** | Toggle the in-window tuning pane | shipped |
| **Ctrl+Shift+T** | Open the detached tuning window (falls back to the pane when detached windows are unavailable) | shipped |
| **F9** | Flip A/B feel (`ProbeMark.AbFlip`) | phase 2 — the mark code exists, the key does not |
| **1 / 2 / 0** | Blind A/B vote (`ProbeMark.AbVote`) | phase 2 |

The hotkeys are keyboard accelerators on always-visible toolbar buttons (`Lab/Hotkeys.cs`), so they work while the list,
a slider or a number box has focus.

---

## Recording a session

1. Surface page → pick the surface closest to what you are investigating.
2. Record page → name the session; choose the probe level. **Use Trace for touchpad, touch and pen**: at Summary the
   probe keeps only wheel NOTCH inputs, so a contact session has no input rows (no gestures, no stop/decay metrics, device
   kind "none"). Trace also records every authored plan, which the curve metrics use as the notch's true anchor.
3. Back on Surface, press **F10**, scroll the way that feels wrong, press **F8** at each bad moment, press **F10** again.
4. The session is written and analyzed at once; the Analysis page opens on it.

The recorder raises `ScrollProbe.Level` to the chosen level for the session and restores it afterwards. Record start and
stop drain the rings first, so a session's edges are exact; the rings hold 8192 rows each — the HUD's "lost rows" counts
anything overwritten before a drain (0 in a healthy session).

### Session folder

`%LOCALAPPDATA%\FluentGpu\ScrollLab\sessions\<yyyy-MM-dd_HH-mm-ss>-<surface>-<device>\` (`Record/SessionWriter.cs`;
`<device>` = the dominant input source: `mouse`, `hires`, `touchpad`, `touch`, `pen`, `keyboard`, `thumb`,
`programmatic`, `none`):

| File | Contents |
|---|---|
| `meta.json` | schema 1: name, start/end QPC, duration, QPC frequency, refresh Hz + period, DPI scale, max frame latency, device kind, surface, feel set, preset base, probe level, engine version, OS build, viewport, row counts, lost rows, frame count |
| `feel.json` | `{motion, host, presetBase}` — `motion` is `ScrollTunables.ToJson` of the feel in force |
| `markers.json` | every marker (code, QPC, ms since start) |
| `events.csv` | the `analyze.py` format (`ScrollProbe.ExportCsv` over the drained rows): `frame`, `notch`, `raw_wheel`, `state_changed`, `view_changed`, `present`, `turn`, `marker`, `scenario_start` |
| `probe-ui.bin` / `probe-render.bin` | the raw drained `ProbeRow`s (`FGPR` header + unmanaged rows) — a saved session re-analyzes exactly |
| `frames.csv` + `frames.bin` + `frame-passes.bin` | one row per UI frame: frame ms, repaint coverage, span reuse, slice bytes recorded, record/capture ms, route, draws, pass breaks, upload bytes, feather items, offscreen surfaces, fresh/motion presents, skipped ticks, race hits, missed motion ticks, slot-wait max, governor, GPU execution ms, and (with GPU passes on) the pass timeline per `GpuPassKind` |
| `turns.csv` | one row per render-thread present: `t_ms, tick_seq, missed_ticks, wake_lag_ms, slot_wait_ms, work_ms, span_ms, fresh, paced` |
| `metrics.json` | the verdicts the Analysis page showed (name, value, baseline, verdict, unit, samples, marker hits, detail) |

### Re-analysing a saved session

Record page → **Analyze** on any listed session: `SessionWriter.Load` reads the `.bin` rows back and
`AnalysisResult.From` recomputes every metric with the CURRENT `ScrollMetrics` — so a metric fix re-scores old sessions.
`events.csv` still loads in `C:\WAVEE\wheel-curve-probe\analyze.py` (pane `Wavee`) as an independent cross-check.
(A `--analyze <session>` command line is planned, not built.)

---

## Metrics — what each one means and which complaint it answers

All metrics are pure functions in `src/FluentGpu.Engine/Scroll/Diag/Analysis/ScrollMetrics.cs` over a `SessionSeries`
(times in QPC seconds, distances in DIP; `T_r` = the refresh period). The reference is `FeelProfiles.Standard`
(64 DIP / 0.15 s front-loaded cubic with a 12 ms C1 blend-in; `ScrollMetrics.ReferenceCurve` is its notch from rest). **Latency** metrics measure from the input's device stamp; **curve**
metrics measure from the authored plan's anchor (Trace sessions; a Summary session falls back to the stamp).

| Complaint | Metric | Definition | Green | Amber |
|---|---|---|---|---|
| heavy / delayed | **First-motion latency** | per gesture, first pose moving > 0.05 DIP after the first input; median | ≤ 2·T_r + 4 ms | ≤ 2·T_r + 12 ms |
| heavy / delayed | **Notch→pose lag** | per notch, first pose presented at/after it; median | ≤ 2·T_r | ≤ 3·T_r |
| jittery / itchy | **Displacement irregularity** | σ(Δ²p) / mean\|Δp\| over active frames | < 0.15 | < 0.30 |
| stutter / "drops frames" | **Repeats / drops** | per 10 s of motion: missed compositor ticks (render Turn rows, else the frame counter, else pose gaps) + display repeats no missed tick explains (± 2·T_r) + superseded presents + DWM drops once per sample — paired-counter ledger only | 0 events | ≤ 2 / 10 s |
| why it stuttered | **Missed-tick attribution** (Info) | for each missed tick, the previous turn's dominant cost: slot-wait-bound (compositor latched late), UI-late (fresh frame's record/submit crossed the tick), GPU-bound (motion re-present crossed it), unattributed (wake lag — scheduler/clock) | — | — |
| steppy / "the curve is wrong" | **Per-notch curve shape** | isolated notches (gap > 300 ms): RMS of the normalized displacement vs the reference cubic over 0–0.4 s, % | < 5 % | < 10 % |
| slow start / slow finish | **Notch t50 / t90** (Info) | median time from the plan anchor to 50 % / 90 % of the notch's travel, vs the reference's | — | — |
| too slow / too fast | **DIP per notch** | median travel per isolated notch | within ±5 % of 64 DIP | within ±15 % |
| spinning feels slow/fast | **Sustained spin speed** (Info) | median \|v\| while same-direction notches arrive < 120 ms apart | — | — |
| floaty / abrupt stop | **Stop latency** | per gesture, last input → first of two consecutive frames each moving < 1 DIP per refresh; vs the reference model driven by the same notches | within 15 % | within 35 % |
| overshoot / undershoot | **Stop distance** | \|p(stop) − p(lift)\| vs the reference model (a reference under 2 DIP: ±1 DIP green, ±3 amber) | within 15 % | within 35 % |
| floaty / dead fling | **Decay rate** | least-squares k of ln\|v\| over the post-lift coast; contact gestures vs `FlingDecayPerS` (wheel-only sessions: Info) | within 15 % | within 35 % |

Anything worse than amber is red. Blank rows have no tile of their own because they must never happen: a coverage clamp
appears in the HUD's "coverage clamps", in the Pose rows (`clamped`), and in the Wavee `scroll.burst` verdict
(`Clamped`); `TileCensus.ExposedMissing` / `CoverageClamps` (Wavee Diagnostics ▸ Tiles) are the render-side twins.

---

## Driving input synthetically

**Wheel — SendInput.** A driver thread calls `SendInput` with `MOUSEEVENTF_WHEEL` (or `HWHEEL`) deltas into the focused
lab window: ±120 per packet for a detented wheel, sub-120 deltas for a hi-res wheel. Pace it with
`timeBeginPeriod(1)` and a sleep-then-spin loop for sub-millisecond packet spacing (the approach
`C:\WAVEE\wheel-curve-probe` uses for its replays). Under mouse-in-pointer the packets arrive as `WM_POINTERWHEEL` and
take exactly the engine's wheel path (`WheelClassifier` → detented notch or fractional hi-res notch). Record a lab
session while the driver runs and the verdicts are directly comparable run to run. The tracked driver in this repo is
`ops\diag\scroll-lab-synthetic.ps1`: it launches the lab, focuses it, records (F10) a ~10 s session of isolated notches,
a spin, an F8 marker, a reversal and hi-res bursts through its embedded `SendInput` class (the sleep-then-spin pacer),
and prints the session folder and its `metrics.json` verdicts — run it once per build and compare.
`ops\diag\README.md` documents what a synthetic capture can and cannot answer. An
injected packet may carry no hardware stamp; `Win32Platform.WheelStampQpc` then falls back to the receive time, which
adds message latency to the latency metrics.

**What cannot be synthesized.** A precision touchpad: Windows has no synthetic PT_TOUCHPAD device
(`CreateSyntheticPointerDevice` supports touch and pen only) and DirectManipulation consumes real HID contacts. Touchpad
feel is judged from live sessions (Trace level). Touch can be injected with `InjectSyntheticPointerInput` (planned as a
lab replayer). Inside the engine, headless gates script contacts with `HeadlessScrollProducer`, and the gallery's
`--scroll-soak` injects DM-shaped contacts through `Win32Window.EnqueueExternal` for pacing soaks.

---

## GPU pass timing

The HUD's **GPU passes** toggle (`LabHost.SetGpuPasses` → `AppHost.GpuPassTimingEnabled`, a runtime toggle — never an
environment variable; `--fg gpu-timing` starts a host with it on) records the pass-granular GPU timeline of each retired
frame: `Uploads`, `BakedBlur`, `Clear`, `Scene`, `GlyphBand`, `TileRaster`, `Offscreen`, `Composite`. The HUD shows the
whole-frame ms, the pass count and the four largest kinds; while recording, each frame's per-kind sums land in
`frames.csv` (`pass_ms_<Kind>`) and `frame-passes.bin`. Use it to tell a GPU-bound pacing problem (missed ticks with
long `Composite`/`TileRaster` passes) from a UI-late or compositor-latch one (`turns.csv`). The gallery's
`--scroll-bench` does the same over a constant-velocity plan without a person in the loop.

---

## What is planned

- **Phase 2 — host knobs, A/B, baselines.** `HostTunables` (present lead, phase offset, wheel gesture gap, touchpad
  lift thresholds, DM engage timeout, overpan cap, device snap, scrollbar timings, jitter threshold) with the same
  seqlock + slider + JSON idiom; an A/B controller with blind trials (F9 flip, 1/2/0 votes, binomial tally,
  `votes.json`); raw `WM_INPUT` wheel capture so cross-app baselines share one QPC clock; WinUI and Edge baseline import
  and overlays (`--serve-edge` local bridge); an HTML report; `--replay` / `--analyze` command lines.
- **Phase 2b — WinUI reference mode + optical measurement.** `wheel-curve-probe --lab-reference`: a raw
  `InteractionTracker` + `ScrollView` streaming tracker state to the lab, with parameter sweeps to map WinUI's knobs onto
  `MotionFeel`; optical capture through Windows.Graphics.Capture decoding the position barcode every surface already
  draws, so the lab, WinUI and Edge are measured with one ruler on the composed output, and the engine's own probe poses
  are checked against what reached the screen.
- **Phase 3 — contacts and fixtures.** Contact records, touch replay, engine-level `InjectScroll` replay of recorded
  touchpad/hi-res streams, the ShelvesInPage / StickyPage / SnapPager surfaces, metric rows for edge overshoot, touchpad
  tracking error (posed − Σ contact Δ), contact-start discontinuity and present cadence, `.fgs` fixtures with a
  headless time base and a `ScrollFixtureSuite` gate ("promote to fixture").
