# Scroll recipes

Each recipe ends with a checklist. Every one of them starts the same way: **reproduce first** — a failing gate, a failing
unit test, or a recorded lab session whose metric is red — then change code. Canon for every type named here:
`docs/design/subsystems/scroll.md`.

---

## 1. Add a scroll effect channel

Example: a hypothetical `BlurSigma` channel driven by the offset (the node blurs as it scrolls away).

```csharp
// Scroll/Effects/ScrollEffect.cs — append (never reorder: the byte values are stored)
public enum EffectChannel : byte { TransX, TransY, Opacity, ClipTop, ScaleXY, ThumbPos, PresentedH, ChildShiftY, BlurSigma }

public static ScrollEffect Blur(double in0, double in1, float from, float to)
    => new(EffectChannel.BlurSigma, EffectKind.Map, in0, in1, from, to, Easing.Linear, 0f, 0);

// Scroll/Effects/ScrollEffectSpec.cs — the DSL method
public static T ScrollBlur<T>(this T el, double in0, double in1, float from, float to) where T : Element
    => el.OnScroll(ScrollEffect.Blur(in0, in1, from, to));

// Hosting/AppHost.Scroll.cs — ApplyEffectChannel (BOTH sinks call it: UiPoseSink and SnapshotScrollPoseSink)
case EffectChannel.BlurSigma:
    if (p.BlurSigma == value) return false;   // whatever NodePaint column the channel writes (owned by scene-memory.md)
    p.BlurSigma = value;
    return true;
```

Decide the channel's class first:

- **Transform-class** (it composes into `LocalTransform`): add it to `ScrollEffectEval.IsTransformChannel` and fold it in
  `EffectTransform.Add` (+ `ToLocal` if it needs a pivot). It then rides the slices as a composite parameter — a scroll
  tick records nothing. Never write `LocalTransform` from a second place (one writer per node).
- **Paint-class** (opacity, clip, presented size, blur…): handled in `ApplyEffectChannel`. It changes recorded bytes, so
  the render sink sets `RecordRequired` and that turn cannot be composite-only — acceptable, but know the cost.
- A new FORMULA (not just a channel) is a new `EffectKind` + one case in `ScrollEffectEval.Evaluate` in plain double
  arithmetic (no `Math.FusedMultiplyAdd`, nothing hardware-contingent — both threads must agree bit for bit).

Checklist: failing `ScrollEffectsSuite` gate (`gate.scroll-effects.<name>`) that drives offsets and reads the node's
paint; a `ScrollLinkedEffectTests` case for the formula; a steady frame allocates 0; `scroll.md` §7 table updated;
`scene-memory.md` if a new `NodePaint` column was needed; `check-canon.ps1`.

---

## 2. Add a feel knob

```csharp
// Scroll/Motion/MotionFeel.cs — add the field (positional record: add it at the END to keep call sites readable)
    double SettleVelocity,
    double MyKnobS)                   // what it means, its unit, where it is read

// every preset
public static readonly MotionFeel Standard = new(..., TouchpadWheelDip: 32.0, MyKnobS: 0.05);
// Glide derives with `with` — inherits unless the profile differs

// Scroll/Diag/ScrollTunables.cs — BuildAll(): one TunableF row (slider range = the plausible physical range)
new TunableF("MyKnobS", 0.0, 0.5, static f => f.MyKnobS, static (f, v) => f with { MyKnobS = v }),
// + the hand-written ToJson / TryFromJson field (NativeAOT — no reflection serializer)
```

Read it **only at author time** (`PlanAuthor.*` via `ScrollTunables.Current`, or bake it into the plan like `RubberC`).
Never read a feel field inside `ScrollPoser.Tick` or `ScrollPlan.Eval` — a live edit would then
change a plan mid-flight and the curve would stop being a function of its authored inputs.

Checklist: `Tunables_All_CoversEveryDoubleFieldWithItsDefault` (it pins the field count) and `Tunables_JsonRoundTrip_PreservesEveryField`
pass; the knob shows up in the Scroll Lab tuning panel automatically (it iterates `ScrollTunables.All`) and in Wavee's
Diagnostics ▸ Scroll profile picker via the presets; `scroll.md` §3.3 field count updated.

---

## 3. Change the wheel curve

The default curve (`FeelProfiles.Standard`, 2026-09-25) is tuned from the owner's own notch traces, not cloned from a
WinUI control: 64 DIP per notch, a 0.15 s front-loaded cubic, C1 re-plans, and a spin curve re-derived so fast spins
travel what the old 32 DIP model did. Its justification is a replay, not taste
(`C:\WAVEE\wheel-curve-probe\reports\wheel-snappier-2026-09-25`: `work\wheelsim.py` mirrors `PlanAuthor`). To change it:

1. Replay the recorded notch stamps (`WheelFeelTraceTests` embeds them; new Wavee `scroll-*.csv` exports carry `notch`
   and `state_changed` rows) through the candidate in the simulator: single-notch t50/t90/settle, per-burst totals vs
   the recording, velocity continuity, the cadence sweep.
2. Data first: `WheelNotchDip`, `WheelDurationS`, `WheelRiseS`, `AccelRefGapS`/`AccelUnityGapS`/`AccelMax` in
   `FeelProfiles`. A new SHAPE changes `SegKind.Cubic` in `MotionSeg.Eval`, `PlanAuthor.WheelSeg` **and**
   `ScrollMetrics.ReferenceCurve` together (the reference is the default feel's notch from rest).
3. Keep the load-bearing rules: a live same-direction glide ACCUMULATES onto `prev.Dest`, everything else re-bases on the
   displayed position; the anchor is `max(tNotch, shownFloorSec)`; a same-direction re-plan starts at the SHOWN
   velocity (`WheelSeg` — C1; a carried velocity above the kick shortens the cubic rather than stepping down).
4. Hi-res wheels ride the same curve in fractional notches — re-run `gate.scroll.hires-burst`.

Checklist: `WheelFeelTraceTests.*`, `ScrollMotionTests.WheelNotch_*`,
`ScrollMetricsTests.ReferenceCurveIsTheDefaultFeelsIsolatedNotch`, `gate.scroll.notch-to-present`,
`gate.scroll.hires-burst`, `gate.scroll.probe-export-chain`; a lab session with isolated notches scores green on
"Per-notch curve shape" and "DIP per notch" against the new reference.

---

## 4. Add an input source

A producer turns device input into `ScrollInputEvent`s and nothing else.

```csharp
// PAL side (FluentGpu.Windows): a contact stream in DIP, positive toward the content end, REAL device stamps
sink(new ScrollInputEvent(ScrollSource.Pen, ScrollGesture.Begin,  qpc, dip, 0f, 0f,  pointerId, mods));
sink(new ScrollInputEvent(ScrollSource.Pen, ScrollGesture.Sample, qpc, dip, dx, dy,  pointerId, mods));
sink(new ScrollInputEvent(ScrollSource.Pen, ScrollGesture.End,    qpc, dip, 0f, 0f,  pointerId, mods));
// or a discrete notch: ScrollGesture.Notch with Dx/Dy in NOTCH units
```

Rules:

- Enqueue through `InputEvent.ForScroll(in e, pointerKind, ms)` (the ring) or, for latency-critical wheel-like input,
  the urgent sink (`IPlatformWindow.SetScrollInputSink` → `AppHost.OnUrgentScrollInput`). Never call a `ScrollHandle`
  from a producer and never write `ScrollState.Offset`.
- Stamp with the device's own time (`POINTER_INFO.PerformanceCount` style); `Qpc = 0` degrades to "now" and every
  latency metric becomes message-time. An End detected by silence sets `LiftDetectedLate = true` and is stamped at the
  LAST packet.
- Pick the contact clock honestly. A producer whose samples are already COMPOSED for a present time (the way
  DirectManipulation evaluates for the frame-info composition hint) stamps each event with the present of the first
  render turn that sees it — `ContactStamp.ForFrame(clock, now)`, the NEXT tick's present, never the tick that reads it
  (pitfalls §22) — sets `ArrivalQpc` to when it observed the event, and sets `PresentTimed = true` on every event →
  `ContactClock.Present` (interpolated between real samples, held past the newest, never re-predicted). Everything
  else is a device stream → `ContactClock.Device` (pose-floor anchor, clock resync, bounded resampling). Mixing them
  double-predicts.
- Never emit a zero-delta sample to say "the finger is resting" — report movement only; the stop is the lift-time rule.
- If the producer KNOWS how the fingers released (DirectManipulation's INERTIA/READY edge), put it on the End
  (`ScrollInputEvent.Release` = `ContactRelease.Moving`/`Stopped`) instead of letting the time rule guess; never emit a
  producer artifact that arrives in the lift's own report (DM's READY snap) as a `Sample` (pitfalls §20).
- If it arrives as wheel packets, classification belongs in `WheelClassifier` (pure, latched per gesture) with a
  `WheelClassifier_*` table test — not in the PAL.
- Routing, latching, chaining and the fling are the engine's (`InputDispatcher.DispatchScroll` → `ScrollRouter` →
  `ScrollHandle`); a producer that needs "its own inertia" is the DirectManipulation mistake that was deleted.
- If the producer must pump per frame, report `ScrollProducerLive` so the host produces a frame per refresh, and make
  sure its frames carry `WakeReasons.ScrollProducer` (the governor never paces those).

Checklist: a `HeadlessScrollProducer`-style scripted stream gate (VerticalSlice) for begin/sample/end + fling; the
`ScrollSource` ↔ `ScrollSourceCode` byte mirror kept in sync; the lab's device kind detection (`SessionWriter.DeviceKind`)
and `SessionSeries` ingestion updated if it is a new source.

---

## 5. Debug a jump / jitter / blank / lag

Never guess from the symptom. Record it.

1. **Reproduce in the Scroll Lab** (`dotnet run --project src/FluentGpu.ScrollLab -c Release`, or the NativeAOT publish
   for real numbers). Pick the surface closest to the bug (Fixed 100k / Measured / Edge cases). Record page → probe level
   **Trace** (needed for touchpad input rows and plan anchors) → F10, reproduce, press **F8** the moment it feels wrong,
   F10 to stop.
2. **Read the Analysis page.** The verdict tiles map to complaints (full table in `docs/guide/scroll-lab.md`):
   jump → irregularity + `Jumped`/extent rows; jitter/itchy → displacement irregularity, repeats/drops, missed-tick
   attribution; heavy/delayed → first-motion latency, notch→pose lag; steppy → per-notch curve shape; floaty/abrupt →
   stop latency/distance, decay rate; blank → any clamp (`Clamped` verdict, `TileCensus.ExposedMissing`/`CoverageClamps`).
3. **Go to the rows at the worst sample** (`WorstT`, and the F8 markers attached to it): `probe-ui.bin`/`probe-render.bin`
   re-load exactly; `events.csv` has one row per pose (`frame`), notch, plan (`state_changed`), extent (`view_changed`
   with `anchored=0|1`), present and turn.

| You see | It means | Look at |
|---|---|---|
| `Pose.Clamped` rows / `Clamped` verdict | the plan outran realization | `Virtualizer.Plan` overscan vs velocity, `NeedsRealize`, what made the realize late (a slow row template — make it a `ListRowEl`) |
| an `Extent` row with `anchored=0` / `Jumped` | a correction reached the screen a frame late | who wrote extent outside `Virtualizer.ApplyMeasured`, or the plan-shift/coverage atomicity (scroll.md §5.2) |
| pose `Pos` steps unevenly while `Turn` rows show `MissedTicks > 0` | the glass skipped vblanks | the Turn row's wake lag vs slot wait vs work (`Missed-tick attribution`): scheduler, compositor latch, UI-late or GPU-bound |
| even presents, uneven `Pos` deltas | the MOTION is uneven | plan rows: re-plans every packet? contact samples bunched (coincident pairs)? a double prediction (see pitfalls)? |
| first pose long after the notch | latency | is the notch urgent (`OnUrgentScrollInput`)? the pose floor far ahead (`depth=2` in `[render.pace]`)? |
| a Turn row with `fresh=0` everywhere, `race>0` | UI frames lose the tick to motion re-presents | UI frame cost (`frames.csv`, the render census) |

4. **Write the failing gate** from what the rows showed (the deterministic headless version of the defect), then fix
   the mechanism.
5. **Re-record** the same scenario and compare the metric (and for touchpad, the same physical gesture — it cannot be
   synthesized).

Also useful: Wavee's `scroll.burst` log line (a `BurstSummary` per burst at Summary level), `[render.pace]` once a
second while motion is live, `--fg gpu-timing` / the lab's GPU passes toggle for per-pass GPU cost, `--scroll-soak` for
sustained touchpad pacing/thermal drift.

---

## 6. Add a gate

Headless, deterministic, failing first. Pick the suite by subject: motion/coverage/routing → `ScrollMotionSuite`;
effects → `ScrollEffectsSuite`; controls/virtualization/scrollbars → `ScrollSuite`; tiles/slices → `TileSuite`/`SliceSuite`;
touch/arena → `TouchSuite`. A pure decision (a formula, a classifier, a policy) is an xUnit test in
`FluentGpu.Engine.Tests` instead — never a source-text test.

```csharp
// the shape of gate.scroll.notch-to-present (ScrollMotionSuite.cs)
var window = new HeadlessWindow(new WindowDesc("scroll-motion-mygate", new Size2(640, 480), 1f)); window.Show();
using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new FlatListProbe(100_000));
host.RunFrame();
var vp = host.Scene.Root;
var handle = host.TryGetScrollHandle(vp)!;
window.SendWheelNotch(new Point2(150, 200), 1f);          // urgent sink: the plan exists before any frame runs
host.RunFrame();
host.Scene.TryGetScroll(vp, out var s);                    // ScrollState: Offset / Velocity / Motion / WindowOrigin / Cover*
Check("gate.scroll.my-gate <one sentence of what it proves>", cond, $"detail={...}");
```

Contact streams: `HeadlessScrollProducer` (`Probes/Probes.cs`) scripts touchpad Begin/Sample/End, notches and
pointer-downs with synthetic stamps and steps frames (`Step(dtMs)`). Assert on `ScrollState`, `handle.Plan`,
`host.Scene.Paint(content).LocalTransform`, `host.LastTileCensus`, or the probe rows (`ScrollProbe.ReadRender`).

Checklist: the gate name is `gate.<area>.<name>` as the first token of the `Check` text; it FAILS on the unfixed code;
a zero-alloc arm if the path is per-frame; registered in the suite's `Run` (a new suite also in
`Harness/SuiteRegistry.cs` with its tag); `scroll.md` §11/§12 names it.
