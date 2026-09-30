# Scroll Lab — design and implementation plan

Status: PROPOSED 2026-09-24 (Fable 5.1 plan; awaiting owner go). Companion to `scroll-rework-implementation.md`.
Why: scroll feel is subjective and hard to describe. The lab replaces words with **unbiased baselines + metrics**, and
gives **realtime tuning of every knob** so the right feel is found empirically. Standalone engine exe (no WinAppSDK).

## 0. What exists that the lab stands on (verified against the tree)
- **Live tunables work end to end.** `ScrollTunables.Current` (`Scroll/Diag/ScrollTunables.cs`) is a seqlock over one
  `MotionFeel` (22 fields). `TunableF{Name,Min,Max,Default,Get,With}` metadata is built once (`BuildAll` :235-281), with a
  hand-written Utf8Json round-trip.
  - `ScrollHandle.Wheel/ContactEnd/Key/ScrollTo` read `ScrollTunables.Current` at author time, so a slider write lands on
    the very next notch or lift.
  - `ScrollRouter` reads `WheelLatchSilenceS` per call.
  - `ScrollProbe.Mark(ProfileApplied/TunableChanged)` fences changes in the trace.
- **The probe exists** (`Scroll/Diag/ScrollProbe.cs`):
  - two 8192-slot POD rings (UI and render) holding Input / Plan / Pose / Coverage / Extent / Cost / Mark records;
  - `EndBurst` → `BurstSummary`;
  - `ScrollProbeCsv.ExportCsv` writes the `analyze.py` format, with `pane=Wavee` already known to the probe.
- **Hardware stamps.**
  - Wheel notches carry `POINTER_INFO.PerformanceCount` (`Win32Platform.cs:1295-1300`).
  - Touch and pen carry `QpcTicks` (:2440).
  - DM touchpad samples carry the *pump* QPC, not a hardware stamp (`Win32DirectManipulation.cs:310-317`). This is a
    labelled limitation.
- **Present truth.** `PresentStats` (DXGI PresentRefreshCount/SyncQpc, DWM refresh/vblank, dropped/missed/late) is sampled
  in `D3D12Device.SamplePresentStats` (:5042-5122). It surfaces in `FrameStats.{PresentsDisplayed, PresentsDropped,
  VblanksRepeated, DwmDropped, DwmMissed, DwmLate, LatencyWaitMs}` (`AppHost.cs:4449-4455`) through `FluentApp.FrameCompleted`.
- **Host access.** `FluentApp.DiagnosticRun = (host, window, device) => {…; return false;}` (`FluentApp.cs:201,:432`) hands
  over the live host while the interactive loop continues. `AppHost.OpenDetachedWindow(DetachedWindowRequest(...))`
  (:1639) opens a real second AppHost + swapchain, used for the tuning window.
- **Headless replay seam.** `HeadlessWindow.SendScroll/SendWheelNotch` and `QueueInput(InputEvent.ForScroll(...))`, with
  `ScrollMotionSuite.NotchToPresentChecks` as the template. Headless `ScrollQpcToSec` ignores stamps
  (`AppHost.Scroll.cs:106`), which is fixed by E6.
- **Surfaces and charts.**
  - Lists: `Virtual.List/ListBound/Measured/GroupedList/HorizontalGrid`, and `ListRowEl` + `RowCellBuffer` (see
    `ListRowSuite.cs:37-70`).
  - Scrollers: `ScrollEl{Snap = SnapSpec.Every(h)}`, `.Sticky/.StickyClip`, `PagedShelf.Create<T>`.
  - Charts: `LineChart`, `SparkBars`, `DensityPlot`.
- **Reference probe** `C:\WAVEE\wheel-curve-probe`:
  - records WM_INPUT with RIDEV_INPUTSINK;
  - replays with SendInput on a `timeBeginPeriod(1)` thread;
  - `analyze.py:638-770` holds the metric definitions.

## 1. Project layout
```
src/FluentGpu.ScrollLab/            WinExe, PublishAot, win-arm64|win-x64; refs Engine+Controls+Windows+WindowsApi
  Program.cs                        (none)=lab | --replay <file> [--target self|window:<t>] | --analyze <session> | --serve-edge
  Lab/{LabHost,LabShell,LabState,Hotkeys}.cs      DiagnosticRun attach, FrameCompleted tap, nav, signals, F8/F9/F10/1/2/0
  Tuning/{TuningPanel,AbController,FeelStore}.cs  sliders+NumberBox per TunableF/HostTunableF, presets, diff, A/B+blind, persistence
  Record/{SessionRecorder,SessionWriter,RawWheelSink}.cs
  Replay/{WheelReplayer,TouchReplayer,EngineReplayer}.cs
  Baselines/{EdgeBridge,BaselineImport}.cs + edge/scrollprobe.html
  Analysis/{AnalysisPage,CompareView,HtmlReport}.cs
src/FluentGpu.ScrollLab.Surfaces/   classlib, refs Engine+Controls only (TerraFX-free; shared with VerticalSlice)
  FixedList100k, MeasuredList, ShelvesInPage, StickyPage, SnapPager, EdgeCases
src/FluentGpu.Engine/Scroll/Diag/   HostTunables.cs, ScrollProbe.Rows.cs, ScrollProbe.Present.cs,
                                    Analysis/{ScrollMetrics,SessionSeries,ScrollFixture}.cs
src/FluentGpu.VerticalSlice/Suites/ScrollFixtureSuite.cs + Assets/scroll-fixtures/*.fgs
```
Add both projects to `src/FluentGpu.slnx`.

## 2. Engine changes (generic, no lab types)
- **E1 `HostTunables`/`HostFeel`**: the same seqlock + TunableF + JSON idiom, applied to knobs that aren't `MotionFeel`
  fields. Each one's reader is converted from a hard constant:

  | Knob (default) | Current home |
  |---|---|
  | PresentLeadFrames (1+MaxFrameLatency) | `AppHost.Scroll.cs:480-485` RenderPresentSec |
  | PresentPhaseOffsetMs (0) | none (new term in RenderPresentSec) |
  | WheelGestureGapMs (200) | `WheelClassifier.GestureGapMs` |
  | HiResLiftDefault/Min/MaxMs (80/50/120) + HiResLiftFactor (1.4) | `Win32Platform.AdaptiveLiftMs:1330-1346` |
  | DmEngageTimeoutMs (250) | `Win32DirectManipulation.EngageTimeoutMs` |
  | DmMinTransformDelta | `Win32DirectManipulation.MinTransformDelta` |
  | OverpanCapFraction (0.10) | `ScrollPlan.OverpanCapFraction`; baked into the plan like RubberC |
  | SnapToDevicePixel (on) | `ScrollPoser.Tick:126` |
  | ContactStaleStampMs | `Win32DirectManipulation.StaleStampTicks` |
  | ScrollBarExpandBegin/ContractBegin/FadeMs (400/500/83) | scrollbar chrome |
  | JitterUnevenThreshold (0.20) | `ScrollProbe.JitterUnevenThreshold` |

  MaxFrameLatency stays read-only.
- **E2 raw WM_INPUT wheel capture**: `IPlatformWindow.SetRawWheelCapture(bool)` → RIDEV_INPUTSINK. It records
  `ScrollProbe.RawWheel(qpc, deviceHash, delta, horizontal, flags)` and keeps working while another window (the WinUI
  probe, Edge) is in the foreground, so cross-app baselines share one QPC clock. The device name is resolved once.
- **E3 probe extensions**:
  - new POD kinds `Present(...)` (UI ring, at `AppHost.cs:4449`), `RawWheel`, and `Contact(qpc, pointerId, kind, x, y,
    phase)`;
  - `ProbeMark.UserFelt/AbFlip/AbVote/SessionStart/SessionEnd`;
  - a public `readonly struct ProbeRow` with `ReadUi/ReadRender(fromCount, Span<ProbeRow>, out next)` for incremental
    draining.

  No allocation is added on the record paths.
- **E4 `Scroll/Diag/Analysis`**: `SessionSeries`, `ScrollMetrics` (the §5 catalog as pure functions), and `ScrollFixture`
  (.fgs: `FGS1` header + packed ScrollInputEvent rows + contact frames). These are shared by the lab, VerticalSlice and
  Wavee.
- **E5 `Win32Platform.InjectScroll(in ScrollInputEvent)`**: engine-level replay of recorded touchpad / hi-res streams.
- **E6 headless time base**: `AppHost.SetHeadlessTimeBase(qpcOrigin, secOrigin)`, so replay keeps the recorded notch
  spacing (which `AccelFor` depends on).

## 3. Screens
**Surface** (main window; tuning lives in a detached window, or in a right pane via Ctrl+T):
```
┌ Scroll Lab ─────────────────────────────────────────────────────── [REC ●] [A|B: A] [Blind: off] ─┐
│ Surface ▾ [Fixed 100k][Measured][Shelves][Sticky][Snap pager][Edge cases]   Device: Mouse (VID_046D) │
├────────────────────────────────────────────────────────────────────────────┬──────────────────────┤
│  # ▶  art  Title · Artist                         Album          ♡  3:41 │ HUD                   │
│  1    ▒▒   Midnight City · M83                    Hurry Up…         3:41 │ offset  12 345.6 DIP  │
│  …  (ListRowEl rows, 100 000, 56 DIP)                                     │ v  1 240 DIP/s        │
│                                                                            │ kind Wheel  clamps 0  │
│                                                                            │ ▁▂▃▅▇█▇▅▃▂ pose strip │
│                                                                            │ last notch→pose 9.8ms │
├────────────────────────────────────────────────────────────────────────────┴──────────────────────┤
│ F8 felt wrong   F9 flip A/B   F10 rec   1/2/0 vote   Ctrl+T tuning pane   Ctrl+R replay last      │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
```
**Tuning** (detached window):
```
┌ Scroll Lab — Tuning ────────────────────────────────────────┐
│ Preset [WinUiExact ▾]  [Save as…] [Import JSON] [Export JSON]│
│ Editing: (●) A  ( ) B        [Copy A→B] [Swap]  Δ vs preset 3│
│ ── MotionFeel ── one row per TunableF: name, slider, number box, preset value, ★ if changed │
│ ── HostFeel ── same, over HostTunables (MaxFrameLatency read-only) │
│ ── Diff vs WinUiExact ── WheelNotchDip 32→40 …                │
└──────────────────────────────────────────────────────────────┘
```
**Record / Sessions**: Start session with a name, surface, set, probe level and ring drain. The session list offers
Analyze, Replay → self, Replay → window:…, Promote to fixture, and Open dir.
**Analysis**: one verdict tile per symptom (value vs baseline, R/A/G); an offset-vs-time LineChart overlaying lab, WinUI and
model, with notch ticks and F8 marker bands; per-frame displacement bars, a notch-gap histogram and the present cadence;
buttons for Export HTML, Export CSV and Open baseline.
**Compare / Blind A/B**: in blind mode each trial randomly assigns X/Y; F9 toggles between them, and 1/2/0 votes. The screen
shows the tally with a binomial p, which is revealed at N trials, plus metric deltas A−B.

## 4. Key sketches
```csharp
// Program.cs
FluentApp.DiagnosticRun = (host, window, device) => { LabHost.Attach(host, window, device); return false; };
FluentApp.FrameCompleted += LabHost.OnFrame;
FluentApp.Run(() => new LabShell(), new AppOptions { Title = "Scroll Lab", Width = 1280, Height = 860,
    AdaptiveGpuPacing = false, WarmCadenceMs = 0 });   // raw cadence for capture

// one row per TunableF (mount-frozen fields; signals carry changes)
sealed class TunableRow : Component {
    public TunableF T = default; public Signal<int> Editing = null!;
    public override Element Render() {
        var v = UseFloatSignal((float)T.Get(AbController.Set(Editing.Peek())));
        var n = UseSignal((double)v.Peek());
        UseEffect(() => { double x = n.Value; if (Math.Abs(v.Peek() - x) > 1e-9) v.Value = (float)x; });
        UseEffect(() => { float x = v.Value; AbController.Edit(Editing.Peek(), T, x); });   // → next notch
        return HStack(8, Text(T.Name).Width(180),
            Slider.Create(v, options: new SliderOptions { Minimum = (float)T.Min, Maximum = (float)T.Max, Step = (float)((T.Max - T.Min) / 400) }, length: 220),
            NumberBox.Create(n, options: new NumberBoxOptions { Minimum = T.Min, Maximum = T.Max, Width = 90 }),
            new TextEl(Prop.Of(() => AbController.IsChanged(Editing.Value, T) ? "★" : "")) { Color = Tok.AccentTextPrimary });
    }
}

public sealed class AbController {   // pure; VerticalSlice-testable
    public FeelSet A, B; public bool Blind; int _trial; bool _xIsA; readonly List<(int Trial, bool XIsA, int Vote)> _votes = new();
    public void Flip(long qpc) { Active = !Active; ApplyActive(); ScrollProbe.Mark(qpc, ProbeMark.AbFlip); }
    public void BeginTrial(Random rng) { _xIsA = rng.Next(2) == 0; Active = _xIsA; ApplyActive(); _trial++; }
    public void Vote(int v, long qpc) { _votes.Add((_trial, _xIsA, v)); ScrollProbe.Mark(qpc, ProbeMark.AbVote); }
    public (int A, int B, int Same, double PBinom) Tally() { /* two-sided binomial */ }
}
```
Surfaces:
- **FixedList100k**: `ItemsView.CreateBound(100_000, …ListRowEl…)`, one `RowCellBuffer` per slot, with rows of 56 DIP.
- **MeasuredList**: `Virtual.Measured(2000)` with rows of 40..140 DIP.
- **ShelvesInPage**: six `PagedShelf`s.
- **StickyPage**: five sections.
- **SnapPager**: `SnapSpec.Every(720)`.
- **EdgeCases**: 3 rows, content exactly the size of the viewport, and empty content.

## 5. Metrics catalog (`ScrollMetrics`; QPC seconds, DIP)
Inputs to the formulas:
- notches `n_i`: the RawWheel stamp when present, otherwise the Input stamp;
- poses `(t_k, p_k)`: the render-ring presentQpc and posed position;
- `T_r`: the refresh period.

| Symptom | Metric | Formula | Baseline | Verdict |
|---|---|---|---|---|
| heavy/delayed | first-motion latency | `t_fm − n_0`, first pose with \|p−p(n_0)\|>0.05 | WinUI probe on the same trace | G ≤ base+4 ms, A ≤ +12 |
| heavy/delayed | notch→pose lag | median `min{t_k ≥ n_i} − n_i` | 2 presents | G ≤ 2·T_r, A ≤ 3·T_r |
| jittery/itchy | displacement irregularity | `σ(Δ²p)/mean\|Δp\|` over active frames | own 0.20; WinUI frame_delta_p95 | G < 0.15, A < 0.30 |
| jittery/itchy | attested repeats/drops | one event per missed vblank: the render thread's missed-tick counter (Turn rows, else the frame log, else pose gaps dt > 1.7·median) + ledger display repeats no missed tick within ±2·T_r explains + superseded presents + DWM drops once per sample (2026-09-24: the old Σ double-counted a miss and charged lagging samples and idle vblanks) | 0 during motion | G 0, A ≤ 2 per 10 s |
| steppy/discrete | per-notch curve shape | isolated notches (gap > 300 ms): RMS of p(t)−p(n_i) on 0..0.4 s vs WinUI mean, normalised; t50/t90 | WinUI per-notch curves | G < 5 %, A < 10 % |
| too slow/fast | DIP per notch; sustained spin speed | (p_end−p_start)/N isolated; median \|v\| while gaps < 120 ms | WinUI 32 DIP; WinUI spin scenario | G ±5 %, A ±15 % |
| floaty/abrupt | fling stop | stop latency t_stop−t_lift (2 frames with \|v\| < 1 DIP/frame); stop distance; decay k from ln\|v\| slope | WinUI/Edge on the replayed touch gesture | G within 15 % |
| rubber band | edge overshoot | max(p−Max, Min−p) after lift; return time; v sign changes | wheel: 0; touch: ≤ OverpanCap·vp | R above the cap |
| touchpad lag/slip | tracking error | during Drag `e = p_posed − (origin + Σ contact Δ)`; median/p95\|e\|; lag by cross-correlation | ground truth 0 | G p95 < 2 DIP, A < 6 |
| jump on touch | contact-start discontinuity | \|p(t_begin+)−p(t_begin−)\|, first 3 poses vs finger Δ | 0 | G < 1 DIP |
| pacing | present cadence | PresentRefreshCount deltas ≠ 1 during motion; LatencyWaitMs p95 | hardware stats | G all ones |

Each metric returns `MetricResult{Name, Value, Baseline, Verdict, Unit, MarkerHits}`. Markers within ±500 ms of the
worst sample are attached.

## 6. Session folder `sessions/<yyyy-MM-dd_HH-mm-ss>-<surface>-<device>/`
- `meta.json`: `{schema:1, startQpc, qpcFrequency, refreshHz, refreshPeriodQpc, dpiScale, monitor, maxFrameLatency,
  deviceKind, rawDevice, surface, feelSet, probeLevel, engineCommit, osBuild}`.
- `feel.json`: `{motion, host, presetBase}`.
- `events.csv`: the ScrollProbeCsv format plus the kinds `raw_wheel`, `present`, `contact` and `marker`. It still loads in
  analyze.py.
- `input.fgs`: the binary fixture. `markers.json`, `votes.json`, `report.html`.

## 7. Baselines
1. **WinUI wheel.** Run the probe while the lab records raw input (INPUTSINK, one clock). `BaselineImport` pairs the
   recordings by raw stamp. `--replay <csv> --target self` injects the identical trace into the lab, and Analysis overlays
   the two.
2. **Edge.** `--serve-edge` runs an HttpListener on 127.0.0.1:47831 that serves `scrollprobe.html`. The page POSTs rAF
   `{t, scrollY}` and wheel `{t, deltaY}` samples, and wall-clock is mapped to QPC with a single anchor (≤1 ms). Replay
   uses `--target window:Edge`. CDP is the fallback.
3. **Touchpad.** There is no synthetic touchpad injection on Windows 11 (`CreateSyntheticPointerDevice` supports
   PT_TOUCH/PT_PEN only). Touchpad is judged live against physics (posed − Σ DM delta). Replay happens at the engine level
   (E5) or headlessly.
4. **Touch.** Record PT_TOUCH contact frames and replay them with `InjectSyntheticPointerInput` onto the lab, the WinUI
   probe and Edge.
5. **Pacing.** The hardware present statistics are the truth.

## 8. Replay matrix
| Input | Lab (self) | WinUI probe | Edge | Headless |
|---|---|---|---|---|
| Mouse wheel (incl. hi-res) | SendInput | yes | yes | SendWheelNotch + E6 |
| Touch | InjectSyntheticPointerInput PT_TOUCH | yes | yes | QueueInput pointer events |
| Touchpad | engine-level only (E5), or live | no | no | QueueInput ForScroll(Touchpad) |
| Keyboard / thumb | SendInput | yes | yes | yes |

## 9. Phases (disjoint files, Opus implementers; the orchestrator builds)
**Phase 1: the smallest useful lab (≈2.4k LOC).**
- 1a: the Surfaces lib (FixedList100k, MeasuredList, EdgeCases) and slnx.
- 1b: the lab csproj, Program, LabHost / LabShell / LabState / Hotkeys.
- 1c: TuningPanel over `ScrollTunables` (presets, save-as, import/export, diff) in a detached window and an in-window pane.
- 1d: minimal E3 (ProbeRow, Read*, Present record, marks), SessionRecorder and SessionWriter.
- 1e: E4 SessionSeries, ScrollMetrics rows 1-6, and an AnalysisPage with a LineChart and verdict tiles.

Acceptance:
- Debug and Release build clean, and VerticalSlice is green.
- A WheelNotchDip change is visible on the next notch in the HUD.
- A 10 s session writes a folder whose `events.csv` loads in analyze.py.
- An F8 marker shows up as a band.
- The verdicts for first-motion, DIP/notch, jitter and stop all appear.

**Phase 2: host knobs, A/B and baselines (≈2.0k).**
- 2a: E1 HostTunables, the 11 readers converted, and gates.
- 2b: AbController with the Compare/Blind screen, `votes.json`, and `gate.scrolllab.ab-tally`.
- 2c: E2 raw capture, RawWheelSink and WheelReplayer.
- 2d: BaselineImport, the Edge bridge, and overlays.
- 2e: the HtmlReport writer in C#. One metric implementation is shared with the gates, and the CSV keeps analyze.py
  available as a cross-check.

Acceptance:
- A 10-trial blind session produces a tally.
- A replayed WinUI trace scores per-notch RMS < 5 % with WinUiExact.
- The Edge overlay works and `report.html` renders.

**Phase 2b: WinUI reference mode + optical measurement (owner request 2026-09-24).**
- **InteractionTracker hook.** `C:\WAVEE\wheel-curve-probe` (WinAppSDK) gains a `--lab-reference` mode.
  - It hosts two things over the same 100k barcoded list: a ScrollView, and a raw `InteractionTracker` +
    `VisualInteractionSource(ManipulationRedirectionMode = CapableTouchpadAndPointerWheel)` on an ExpressionAnimation-driven
    visual (the same wiring as ScrollPresenter.cpp:2791-2800 / :3383).
  - It streams events to the lab over the Edge bridge's local HTTP endpoint, stamped with QPC in the same domain:
    - `ValuesChanged` (Position, RequestId);
    - `InertiaStateEntered` (NaturalRestingPosition, ModifiedRestingPosition, PositionVelocityInPixelsPerSecond, decay
      rate);
    - `IdleStateEntered`, `InteractingStateEntered`, `CustomAnimationStateEntered`.
  - **Tracker parameter sweeps** (PositionInertiaDecayRate, inertia modifiers, snap points) are driven from the lab and
    recorded, so WinUI's own knobs can be mapped onto `MotionFeel`.
- **Optical measurement** (`Measure/OpticalCapture.cs`):
  - Every surface (lab, WinUI reference, Edge page) draws a fixed-column **position barcode** per row: a binary code of the
    row index plus a sub-row phase ramp.
  - The lab captures any target window through **Windows.Graphics.Capture** (`Direct3D11CaptureFramePool`,
    `SystemRelativeTime` = QPC domain) and decodes the displayed offset per captured frame to ≤0.25 px.
  - That makes it one ruler for all three apps, measured on the composed output.
  - Metrics rows 1-6 and 11 are computed from optical series too. The Analysis page overlays engine / WinUI / Edge, and
    flags any disagreement between the engine's own ScrollProbe poses and what the capture observed (a pipeline-truth
    check).
  - Caveat: capture yields composed frames, not raw vblanks. That's what the owner sees, which is the right reference.

**Phase 3: touch, touchpad and pen; the remaining surfaces; fixtures (≈1.8k).**
- 3a: E3 Contact records, TouchReplayer, E5 InjectScroll, EngineReplayer.
- 3b: the ShelvesInPage, StickyPage and SnapPager surfaces.
- 3c: metric rows 7-10.
- 3d: E4 ScrollFixture (.fgs), E6, ScrollFixtureSuite with `*.thresholds.json`, and "Promote to fixture".

Acceptance:
- A touch fling on the lab and on WinUI stops within 15 % of each other.
- A touchpad session reports tracking p95.
- A fixture gate fails when WheelNotchDip is set to 8.

## 10. Risks / open questions
1. DM touchpad timestamps are pump-stamped, not hardware (±1 frame on latency; tracking error is unaffected). The option
   is `GetPointerFrameTouchPadInfo` contact frames as the time reference in Phase 3.
2. `FluentGpu.ScrollLab.Surfaces` becomes a 9th project, so VerticalSlice can mount the same surfaces headlessly
   (recommended over duplicating them).
3. The tuning window uses `OpenDetachedWindow`, which is borderless. An OS frame flag would cost about 10 LOC.
4. Edge time correlation is about ±1 ms. That's fine for curves, stop and t90, and is labelled on latency.
5. Blind A/B works best for small deltas: presets that differ a lot are recognisable.
