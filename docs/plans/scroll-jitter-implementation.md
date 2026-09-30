# Scroll jitter — touchpad sample race + render-loop catch-up (engine plan)

## Context

The owner reports that scrolling "works but still feels jittery, especially in Wavee". The RCA (2026-09-29) found two engine
mechanisms, both in `..\fluent-gpu` — no app code is involved:

1. **Touchpad (primary, confirmed).** DirectManipulation (DM) samples are produced on the UI thread once per frame and
   stamped `now + clamp(PresentQpc − now, 0, refresh)` ≈ tick + 8 ms. The render thread poses at
   `tick + (1+depth)·refresh` ≈ tick + 16.7 ms, so a `ContactClock.Present` ring is always past its newest sample and
   **holds** it. The UI writes sample *k* within ±0.5 ms of the render thread reading `PlanSlots` on the same tick, so each
   frame shows whichever sample won the race. In the owner's 09-25 captures, 93 % of frames showed the previous tick's
   sample and ~14 % of fast drag frames showed the same-tick one → +2 / 0 sample steps (8 DIP at 1000 DIP/s). DM's own
   per-tick output is regular (speed ratio p5 0.91 / p95 1.36). Wheel and programmatic glides (analytic plans) are
   perfectly regular on the same build (verify glides 09-29: 0 irregular steps).
2. **Pacing (secondary, confirmed on today's Release publish).** The render turn wakes on the compositor tick, then blocks
   on the depth-1 frame-latency semaphore. A present that lands after a vblank is retired only at the NEXT vblank (+0.2–
   0.7 ms): in the 09-29 Trace captures every waited slot opened 8.5–9.0 ms after its tick, and no-wait turns presented
   1–2 ms after it. One late frame therefore drops the loop one tick behind and it **never catches up while motion
   continues** — waited runs of 55, 57, 59, 64, 86 consecutive turns with cheap frames (work p50 1.3 ms). By the retire
   timing, every frame of such a run lands one refresh later than an early-phase frame of the same tick (inferred — no
   per-frame display attestation exists yet); entering and leaving a run are visible steps, and
   GPU-heavy pages add repeats on top (owner's Release session: 65 repeated vblanks in 394, `missed=12–44/s`).

Intended outcome: at constant finger speed a touchpad drag advances exactly one DM sample per frame regardless of thread
timing, and one late frame costs exactly one vblank instead of a long run of late presents.

Reference engines (read on disk under `C:\WAVEE`): Gecko APZ enforces "one frame delay between computing the async
transform and compositing it" (`gecko-dev\gfx\layers\apz\src\AsyncPanZoomController.cpp:4935-4947`); Flutter resamples
pointer input at present + a negative offset, interpolates, holds past the newest (`flutter-scroll\...\gestures\
resampler.dart:130-149`, `binding.dart:224`); Chromium viz never draws behind a pending swap — "Swap throttled", no draw,
the held BeginFrame re-issued on the ack (`chromium-cc-input\components\viz\service\display\display_scheduler.cc:724-728,
819-854`, `begin_frame_source.cc:578-607`); Gecko skips the composite on "Too many pending frames" and retries next vsync
(`WebRenderBridgeParent.cpp:2370-2394`).

Working rules that apply: engine changes are made and verified in `..\fluent-gpu` (Debug + Release build, Engine.Tests
Debug + Release, full VerticalSlice); no source-text tests; no env-var switches; zero managed allocation on the frame /
render paths; canon edits go to `docs/design/subsystems/scroll.md` + `SPEC-INDEX.md` §2 + `check-canon.ps1`. The owner
edits `AppHost.cs` concurrently — implementation subagents cite symbols, not line numbers, and touch disjoint files.
Implementation: Opus subagents on disjoint files; only the orchestrator builds/tests. Once approved, this plan is copied to
`..\fluent-gpu\docs\plans\scroll-jitter-implementation.md` as the plan of record.

---

## Part A — stamp each DM sample with the present that first shows it

### A.0 The rule

A composition-timed contact event produced on UI frame *k* is stamped with **the present time of the first render turn
guaranteed to see it — the next tick's present**: `stamp = clock.PresentQpc + clock.RefreshQpc`
(= `P(k+1)` under the same law `RenderPresentSec` uses). Render tick *k* poses at `P(k)` = the stamp of sample *k−1*
exactly, so it shows sample *k−1* whether or not sample *k* has been written yet; between lattice points the ring
interpolates two REAL samples; past the newest it still holds (only when the UI stalls > 1 refresh). This is Gecko's
one-frame delay expressed as a timestamp. Nothing about `ContactRing`, `ContactClock.Present`, `PlanAuthor` or the posers
changes; only the producer's stamp. Latency equals today's typical case (93 % of frames already showed sample *k−1*).

```
            tick k-1         tick k           tick k+1
 UI  write  s(k-1)@P(k)      s(k)@P(k+1)      s(k+1)@P(k+2)        (written ±0.5 ms around each tick)
 render     reads @P(k-1)    reads @P(k)      reads @P(k+1)
 shows      s(k-2)           s(k-1)           s(k)                 ← same result whether s(k) landed before or after
```

The DM composition hint (`CompositionDeltaMs`, DM's own latency compensation) is NOT changed — the hint says what DM
predicts to; the stamp says when the sample is shown.

Consumer check (all verified in code, 2026-09-29): `ContactBeginHere` Present branch has no floor/now clamp and grabs
`DisplayedAt` (render feedback) → no jump; `PlaceContactTime` only refuses to rewind; `FollowEnd` p0 = `Ring.Eval(tNow)` =
the held newest sample = what render tick *j* showed → no jump, and `v0 = V·e^(−k(tNow−tLift))` is unchanged because both
stamps shift equally; `FollowCancel`, chaining (`ContactCancel(t)`+`ContactBeginHere(t)`), router `CanMove(EvalAt(t))`,
`SettleIfDue` (returns early for Drag), `PlanSlots.Shift`/`NoteFrameShift` are time-agnostic or monotone. A lead-0 End
(`TryStopForPhysicalWheel`, stamped now) is clamped to `LastT` by `PlaceContactTime` exactly as today. No app code reads
`PresentTimed`/`ContactClock`/`ScrollInputEvent`.

### A.1 One rule, one place — `ContactStamp`

New `src/FluentGpu.Engine/Scroll/Runtime/ContactStamp.cs` (pure; used by the DM producer AND the headless producer):

```csharp
namespace FluentGpu.Scroll.Runtime;

/// <summary>The plan-time stamp of a COMPOSITION-TIMED contact event (<see cref="ContactClock.Present"/>) produced on a
/// frame whose clock is <paramref name="clock"/>: the present time of the first render turn guaranteed to see it — the NEXT
/// tick's present (<c>PresentQpc + RefreshQpc</c>, the RenderPresentSec law one refresh on). The UI writes a frame's sample
/// within a fraction of a millisecond of the render thread reading PlanSlots on the same tick; stamping it for the tick
/// that reads it made each frame show whichever side won (2026-09-29 RCA: +2/0 sample steps on ~14 % of fast drag
/// frames). Stamped one tick on, render tick k always shows sample k−1 and interpolates between real samples — the
/// shown position is a function of time, never of thread order (Gecko APZ's one-frame delay; Flutter's resampler).
/// Without a known refresh the stamp is <paramref name="nowQpc"/> (PlaceContactTime keeps it monotone).</summary>
public static class ContactStamp
{
    public static long ForFrame(in FluentGpu.Pal.FrameClock clock, long nowQpc)
        => clock.RefreshQpc > 0 && clock.PresentQpc > 0 ? clock.PresentQpc + clock.RefreshQpc : nowQpc;
}
```

Failing-first order: land `ContactStamp` first with TODAY's rule (`now + wholeMs(clamp(PresentQpc − now, 0, refresh))`),
wire both producers to it, add the race test (A.4) and watch it fail; then switch the body to the rule above.

### A.2 Producers

`src/FluentGpu.Windows/Pal/Win32DirectManipulation.cs`:
- `PumpOnce(long nowQpc, long nowMs, double leadMs, long stampQpc)`: `_pumpQpc = stampQpc; _pumpAtQpc = nowQpc;` —
  the hint keeps `leadWholeMs` exactly as today.
- `UpdateFrame(in FrameClock clock)`: `PumpOnce(nowQpc, nowMs, leadMs, ContactStamp.ForFrame(in clock, nowQpc));`
  `UpdateIdle` and `TryStopForPhysicalWheel`: `stampQpc = nowQpc` (no frame clock; unchanged behaviour).
- `OnContactEvent`: staleness measured against the pump's WALL time, not the (future) stamp:
  `long qpc = _pumpQpc; if (_pumpAtQpc == 0 || now - _pumpAtQpc > StaleStampTicks) qpc = now;` and set
  `ArrivalQpc = now` on the event (A.3).
- Fix the doc comments that already disagree with the code: `UpdateFrame` (~197-200) and `Win32Platform.PumpScroll`
  (~1255-1260, "stamped with clock's FrameQpc") → "stamped `ContactStamp.ForFrame` — the next tick's present".

`src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs` `PumpScroll`: touchpad stamp
`long stamp = touch ? clock.FrameQpc : ContactStamp.ForFrame(in clock, clock.NowQpc);` (headless `PresentQpc = frame +
refresh`, so the headless stamp is frame + 2·refresh — the same one-tick-on relation as production). Update its doc comment.

### A.3 Keep latency metrics honest — `ArrivalQpc`

The probe's `Input` row records `e.Qpc` (`InputDispatcher.Scroll.cs` ~120) and `ScrollMetrics.FirstMotionLatency`
measures "from the device stamp". Touchpad stamps are already `now + 8 ms` today (latency under-read by ~8 ms); a next-
present stamp would make it meaningless.
- `src/FluentGpu.Engine/Scroll/Runtime/ScrollInput.cs`: add `public long ArrivalQpc { get; init; }` to
  `ScrollInputEvent` — "when the producer observed the event (QPC); 0 ⇒ same as Qpc. Composition-timed events carry a
  PRESENT stamp in Qpc, so latency is measured from this."
- `InputDispatcher.Scroll.cs`: `ScrollProbe.Input(..., e.ArrivalQpc != 0 ? e.ArrivalQpc : e.Qpc, ...)`.
- DM (`OnContactEvent`) and headless (`clock.NowQpc`) set it. Note the CSV meaning change in scroll.md §10.3 (touchpad
  `raw_wheel`/`notch` rows now carry arrival time); no schema bump needed (column set unchanged).

### A.4 Tests (failing first)

- **`ContactStampTests`** (Engine.Tests, pure): paced clock (tick 1000, refresh 83 333, depth 1 ⇒ Present = tick +
  2·refresh) ⇒ stamp = tick + 3·refresh; `RefreshQpc == 0` ⇒ now; headless clock (`RefreshLattice.Headless`) ⇒ frame +
  2·refresh.
- **`TouchpadStampRaceTests.RenderTick_ShowsTheSameSample_WhetherThisTicksSampleLandedBeforeOrAfterItsRead`**
  (Engine.Tests; `PlanSlots` + `ScrollHandle` bound as in `ScrollRuntimeTests.Handle_*`): for k = 0..119 build the UI
  frame clock (`FrameQpc = k·T`, `PresentQpc = k·T + 2T`, `RefreshQpc = T`), write sample k (position 10·k,
  `ContactBeginHere`/`ContactDelta` with `ContactStamp.ForFrame`); the "render read" is `plan.Eval(k·T + 2T)` taken
  BEFORE or AFTER that write per a seeded random order. Assert, for three seeds + all-before + all-after: the posed
  sequences are identical, `shown(k) == 10·(k−1)` for k ≥ 1, and `ScrollMetrics.DisplacementIrregularity` (pattern:
  `ScrollMetricsTests.IrregularityIsZeroForConstantVelocityAndRedForAlternatingSteps`) is Green (< 0.15; expect 0). On
  today's rule this fails (before-reads hold s(k−1), after-reads show s(k)).
- **VerticalSlice `gate.touchpad.stamp-race`** (`src/FluentGpu.VerticalSlice/Suites/ScrollMotionSuite.cs`, next to
  `TouchpadDmStreamChecks` / `gate.touchpad.dm-stream-monotone`): the same headless AppHost + render-poser model, but
  each frame the render model ticks at the frame's present time either before or after `host.RunFrame()` (seeded);
  assert identical pose sequences across orderings and irregularity < 0.15 over the drag, plus the existing no-back-step
  and 1:1-travel checks. Register in the `scroll-motion` suite.
- **Unchanged and must stay green** (they construct their own stamps, and ring semantics do not change):
  `ScrollRuntimeTests.Handle_PresentClockContact_KeepsItsPresentStamps_AndTheLiftNeverRewinds`,
  `TouchpadDragMonotonicityTests.*` (incl. `Present_clock_shows_the_newest_sample_and_interpolates_between_samples`),
  `ScrollMotionTests` Contact66 `FollowEnd_*`, `TouchpadReleaseDispatchTests.*`, `DmContactStreamTests.*`,
  `gate.touchpad.dm-stream-monotone` (re-run: its final position still equals the full travel once the ring holds).
  Also update the model in `TouchpadDragMonotonicityTests.WorstBackStep` (stamps `t + lead`) to call `ContactStamp` so
  the test models production.

### A.5 Docs (canon)

Replace the "stamped `now + lead` … the ring never predicts past its newest sample — DM already predicted once" wording
with the A.0 rule in: `docs/design/subsystems/scroll.md` §3.1 (~159-164: keep "past the newest it holds", add "each
sample is stamped with the present of the first render turn that sees it, so a tick shows the previous tick's sample and
the ring interpolates between real samples"), §4.4 (~288-302: the stamp is `ContactStamp.ForFrame`; the hint stays the
lead), §5.3 (~407-408), §11 invariant 10 (add `TouchpadStampRaceTests.*`, `gate.touchpad.stamp-race`,
`ContactStampTests.*`), §14 (~936); `SPEC-INDEX.md` ~81; `input-a11y.md` ~691-695; engine skill
`.claude/skills/fluentgpu-scroll/{SKILL.md, pitfalls.md, recipes.md, where-to-change-what.md}` (add the pitfall: "never
stamp a composition-timed sample for the tick that reads it — the UI write races the render read"). Run
`check-canon.ps1`.

---

## Part B — the render loop catches up instead of queueing behind a late frame

### B.0 The mechanism in code

`RenderThread.PresentTurn` (`src/FluentGpu.Engine/Hosting/Threading/RenderThread.cs`): tick read once → `_presentSlotWait()`
(= `D3D12Device.WaitForPresentSlot`, an unbounded `WaitForSingleObject` on the depth-1 latency semaphore) → `PresentCadence.
Decide` → fresh or motion present. When the previous present missed its vblank, the semaphore re-signals only after the
NEXT vblank, so this turn presents ~8.5 ms after its tick, i.e. after the following vblank too — and so does every turn
after it. The loop has no way to drop back to the early phase except an idle tick.

```
 tick k          tick k+1 (V)         tick k+2 (V)         tick k+3
 │ F(k-1) late ─────►│ retires V+0.2
 │                   │ turn k+1: wait ──►│ opens 8.5 ms after its tick, presents F(k+1) after V(k+2)
 │                   │                   │ turn k+2: wait ──► … one tick behind, forever (55–86 turns measured)
 NEW:                │ turn k+1: slot busy past the grace → SKIP (F(k-1) owns this vblank)
                     │                   │ turn k+2: slot free at the tick → presents 1–2 ms in → early phase again
```

Skipping unconditionally would re-open the half-rate cliff `RenderThreadPacingTests` guards (a GPU costing slightly more
than a refresh would skip every other tick). So the skip is **conditional on the frames fitting the early phase**, with a
backoff when a catch-up does not hold. Chromium solves the over-budget case with a deeper queue; here that is the existing
`PresentQueueDepthPolicy` (depth 2 at GPU EMA ≥ 0.8·refresh), which already sits above the catch-up's fit threshold.

### B.1 Seam: a bounded slot take (replaces `WaitForPresentSlot`)

`src/FluentGpu.Engine/Seams/Rhi/Rhi.cs` — replace `void WaitForPresentSlot() { }` outright (no legacy path):

```csharp
/// <summary>Take the primary swapchain's present-slot credit (one Present spends it), waiting at most
/// <paramref name="timeoutMs"/> (−1 = the backend's liveness bound). True when the credit is held (or the backend has no
/// present queue); false when the slot did not open in time — no credit was taken, nothing to undo. Render-thread only.
/// A backend that implements this MUST skip its internal pacing wait while a credit is held.</summary>
bool TryTakePresentSlot(int timeoutMs) => true;
```

`src/FluentGpu.Windows/D3D12/D3D12Device.cs` (replaces `WaitForPresentSlot`, same doc remarks):

```csharp
public bool TryTakePresentSlot(int timeoutMs)
{
    AssertSubmitThread();
    if (_primarySwapchain is not { } sc || sc.Disposed || !sc.HasLatencyWaitable || sc.LatencyCreditHeld) return true;
    long start = System.Diagnostics.Stopwatch.GetTimestamp();
    uint r = WaitForSingleObject(sc.FrameLatencyWaitable, timeoutMs < 0 ? 1000u : (uint)timeoutMs);
    sc.Frame.LastLatencyWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    // Unbounded path keeps today's liveness semantics (a lost device must not wedge the loop: proceed after 1 s).
    if (r != 0 /* WAIT_OBJECT_0 */ && timeoutMs >= 0) return false;
    sc.LatencyCreditHeld = true;
    return true;
}
```

Wiring: `AppHost.cs` (render-thread construction, `presentSlotWait: _device.WaitForPresentSlot`) →
`takePresentSlot: _device.TryTakePresentSlot`. Update the stale remarks at `D3D12Device.cs` (`OpenSubmit` "normally already
paid this wait in WaitForPresentSlot", the `LatencyCreditHeld` field comment) to name `TryTakePresentSlot`.

### B.2 Pure policy: `SlotCatchUp`

New `src/FluentGpu.Engine/Hosting/Threading/SlotCatchUp.cs` (pure, unmanaged struct, zero-alloc, render-thread owned):

```csharp
namespace FluentGpu.Hosting.Threading;

/// <summary>Decides what a CLOCK-PACED render turn does when the present slot is not free shortly after its tick.
/// A busy slot there means the previous present missed its vblank: the semaphore re-signals only after the NEXT vblank,
/// so waiting would present this tick's frame one vblank late — and every later turn inherits that phase (measured
/// 2026-09-29: runs of 55–86 turns one tick behind, frames costing 1–3 ms). While the frames FIT the early phase the turn
/// skips instead (the queued frame is this vblank's frame) and the next tick presents on time — Chromium viz's "swap
/// throttled" rule. Frames that do not fit keep today's wait (skipping them would halve the rate; the present-queue depth
/// policy owns that regime). A catch-up that does not hold backs off.</summary>
public struct SlotCatchUp
{
    /// <summary>How long a paced turn waits for the slot before calling it busy, as a fraction of the refresh. The retire
    /// lands 0.2–0.7 ms after the vblank (09-29 captures); 0.15·8.33 ms rounds up to 2 ms.</summary>
    public const double GraceFraction = 0.15;
    /// <summary>A frame (wake + work + GPU) must cost at most this share of the refresh for the early phase to hold.
    /// Below PresentQueueDepthPolicy.EngageFraction (0.8) so the two regimes never overlap.</summary>
    public const double FitFraction = 0.70;
    /// <summary>A catch-up followed by a busy slot within this many ticks did not hold.</summary>
    public const int HoldTicks = 2;
    /// <summary>Ticks without catch-up after one that did not hold (1 s at 120 Hz).</summary>
    public const int BackoffTicks = 120;
    private const double Alpha = 0.2;

    private double _costEmaMs;
    private bool _seeded;
    private long _lastCatchUpTick, _backoffUntilTick;

    public readonly double CostEmaMs => _costEmaMs;
    public readonly bool BackingOff(long tickSeq) => tickSeq < _backoffUntilTick;

    public static int GraceMs(double refreshMs) => Math.Max(1, (int)Math.Ceiling(refreshMs * GraceFraction));

    /// <summary>One presented paced turn: its wake lag, render work (slot open → present returned) and the latest
    /// retired GPU execution time, all ms.</summary>
    public void Observe(double wakeMs, double workMs, double gpuMs)
    {
        double cost = wakeMs + workMs + gpuMs;
        _costEmaMs = _seeded ? _costEmaMs + Alpha * (cost - _costEmaMs) : cost;
        _seeded = true;
    }

    /// <summary>The slot was busy past the grace on paced tick <paramref name="tickSeq"/>: true ⇒ skip this tick.</summary>
    public bool ShouldSkip(long tickSeq, double refreshMs)
    {
        if (tickSeq == 0 || tickSeq < _backoffUntilTick) return false;
        if (!_seeded || _costEmaMs > FitFraction * refreshMs) return false;
        if (_lastCatchUpTick != 0 && tickSeq - _lastCatchUpTick <= HoldTicks)
        {
            _backoffUntilTick = tickSeq + BackoffTicks;   // the early phase did not hold: stop trying for a while
            return false;
        }
        _lastCatchUpTick = tickSeq;
        return true;
    }

    /// <summary>Motion ended: forget the phase history (the next run starts clean; the cost EMA is kept).</summary>
    public void Break() { _lastCatchUpTick = 0; _backoffUntilTick = 0; }
}
```

### B.3 `RenderThread` wiring

`src/FluentGpu.Engine/Hosting/Threading/RenderThread.cs`:
- Ctor parameter `Action? presentSlotWait` → `Func<int, bool>? takePresentSlot`; field `_takePresentSlot`.
- New fields `SlotCatchUp _catchUp;` and `long _catchUpSkips, _paceCatchUp0;` + `public long CatchUpSkips =>
  Volatile.Read(ref _catchUpSkips);`. `_catchUp.Break()` wherever `_motionRun.Break()` runs.
- In `PresentTurn`, replace the slot block (`slotWait0` … `longWait`) with:

```csharp
long slotWait0 = Stopwatch.GetTimestamp();
bool paced = tickSeq != 0;
double refreshMs = PeriodQpc() * 1000.0 / Stopwatch.Frequency;
bool held = _takePresentSlot?.Invoke(paced ? SlotCatchUp.GraceMs(refreshMs) : -1) ?? true;
if (!held)
{
    // The previous present missed its vblank and owns this one. Skip while frames fit the early phase (the next tick
    // presents on time); otherwise wait as before (the depth policy owns over-budget frames).
    if (_catchUp.ShouldSkip(tickSeq, refreshMs))
    {
        Volatile.Write(ref _catchUpSkips, _catchUpSkips + 1);
        return motion;                        // tick NOT marked presented: MotionTickRun charges it at the next present
                                              // (the next Turn row shows missed=1 — no new probe row needed)
    }
    held = _takePresentSlot!.Invoke(-1);
}
long slotOpen = Stopwatch.GetTimestamp();
// … unchanged: slot-wait accounting, longWait, PresentCadence.Decide, fresh / motion present …
```

- After a paced present, feed the policy (the render thread already has all three numbers):
  `if (paced) _catchUp.Observe((turnStart - tickBase) * toMs, (done - slotOpen) * toMs, _paceHost?.Invoke().GpuExecutionMs ?? 0);`
  (`RenderPaceHostState` is a record struct — no allocation; `SamplePaceHostState` reads `TryGetGpuRenderSample`, a field
  read.)
- `ReportPace`: append ` catchUp={_catchUpSkips - _paceCatchUp0} costEma={_catchUp.CostEmaMs:F2}
  backoff={(_catchUp.BackingOff(tickSeq) ? 1 : 0)}` to the `[render.pace]` line; reset `_paceCatchUp0` with the window.
- Diagnostics: add `CatchUpSkips` to `RenderPaceSnapshot` (`Hosting/RenderCensus.cs`) and fill it where
  `RaceHits`/`SkippedTicks` are filled (`AppHost`'s `RenderPace` getter). No new probe row: the probe's UI ring is
  single-producer (UI thread), and the Turn row of the present after a skip already carries `missed=1`.

App (`C:\wavee\waveemusic`): `src/apps/Wavee/Screens/Diagnostics.Host.cs` — the `scroll.frames` line gains
`catchUps=` next to `raceHits=`/`skippedTicks=` (`Last.RenderCatchUpSkips - First.RenderCatchUpSkips`, plus the field in
whatever snapshot struct feeds `Last`/`First`). The engine-side `[render.pace]` line is already routed to the log.

### B.4 Tests (failing first)

- **`SlotCatchUpTests`** (Engine.Tests, pure): unseeded ⇒ no skip; cheap frames (cost 3 ms @ 8.33) busy ⇒ skip; cost 6.5 ms
  (> 0.7·8.33) ⇒ no skip; skip at tick 10 then busy at tick 12 ⇒ no skip AND backoff until 132 (busy at 131 ⇒ false,
  at 133 cheap ⇒ true); `Break()` clears backoff; `GraceMs(8.333) == 2`, `GraceMs(16.667) == 3`, `GraceMs(4.1667) == 1`.
- **`RenderThreadPacingTests`**: the virtual display gets a model of the latency semaphore instead of a scripted wait: a
  present at virtual time `t` with frame cost `c` retires at `ceil(t + c) + 0.02` (the first vblank after the frame is
  complete, +0.2 ms); `takePresentSlot(timeout)` returns true and advances `Now` to the retire time when that is within
  `timeout` (−1 = unbounded), else advances `Now` by the timeout and returns false. New cases:
  - `ALateFrameCostsOneVblank_ThenTheLoopIsBackInTheEarlyPhase`: 120 ticks at cost 0.3 with one frame at 1.1 ⇒ exactly
    one catch-up skip, and every later turn takes its slot within the grace (fails on today's code: all later turns wait
    ~1 refresh).
  - `ChronicallyOverBudgetFrames_NeverCatchUp_AndKeepFullRate`: cost 1.05 ⇒ `CatchUpSkips == 0`, presents per tick ≈ 1
    (the half-rate cliff stays closed).
  - `ACatchUpThatDoesNotHold_BacksOff`: cost alternating so the early phase cannot hold ⇒ at most one skip per backoff
    window.
  Existing `[Theory]` cases keep their assertions (the harness change is mechanical).
- **`RenderThreadLifecycleTests`**: `presentSlotWait: () => …` → `takePresentSlot: _ => { …; return true; }` in the three
  slot tests; add `APacedTurnWhoseSlotStaysBusy_PresentsNothing_AndTheNextTickPresents` (policy seeded cheap, slot returns
  false once ⇒ 0 presents that turn, `CatchUpSkips == 1`, next DrainSync presents).

### B.5 Docs

- `docs/design/subsystems/scroll.md` §8 Pacing: add the rule after "Every present first takes the present-slot credit":
  "A clock-paced turn takes it with a grace of `SlotCatchUp.GraceFraction` of a refresh; a slot still busy after that means
  the previous present missed its vblank and owns this one — while frames fit `FitFraction` of the refresh the turn
  presents nothing and the next tick presents on time (never queue behind a late frame); over-budget frames keep the
  unbounded wait and the depth policy." Invariant 16 gains `SlotCatchUpTests.*`,
  `RenderThreadPacingTests.ALateFrameCostsOneVblank_…`, `…ChronicallyOverBudgetFrames_…`. §10.6 `[render.pace]` field list
  gains `catchUp=`, `costEma=`, `backoff=`.
- `docs/design/subsystems/threading-render-seam.md` §11.1: fix the stale "tick-only turn … never reserves a slot" and name
  `TryTakePresentSlot` + the catch-up rule. `SPEC-INDEX.md` §2 row for pacing, then `check-canon.ps1`.

---

## Order of work

0. Copy this plan to `..\fluent-gpu\docs\plans\scroll-jitter-implementation.md`. The owner files one GitHub issue
   ("Touchpad scrolling judders; render loop stays one frame late after a late frame") — the `gh` call needs their approval
   (github-triage) — and its number goes on the CHANGELOG bullet ` (#n)` and the commit bodies (`Fixes #n`).
1. **Wave 1 — four Opus subagents, disjoint files, no builds/tests/git:**
   - **A (touchpad):** `Scroll/Runtime/ContactStamp.cs` (new), `Scroll/Runtime/ScrollInput.cs`,
     `Input/InputDispatcher.Scroll.cs`, `FluentGpu.Windows/Pal/Win32DirectManipulation.cs`, `Win32Platform.cs` (doc comment
     only), `Headless/Pal/HeadlessPlatform.cs`, tests `ContactStampTests.cs` + `TouchpadStampRaceTests.cs` (new),
     `TouchpadDragMonotonicityTests.cs` (model helper), `VerticalSlice/Suites/ScrollMotionSuite.cs` (new gate).
     Failing-first sequencing as in A.1 (the orchestrator runs the race test between the two commits of the rule).
   - **B (pacing):** `Seams/Rhi/Rhi.cs`, `FluentGpu.Windows/D3D12/D3D12Device.cs`, `Hosting/Threading/SlotCatchUp.cs` (new),
     `Hosting/Threading/RenderThread.cs`, `Hosting/RenderCensus.cs`, `Hosting/AppHost.cs` (only the render-thread
     construction argument and the `RenderPace` getter field — the owner edits this file concurrently: minimal, symbol-
     anchored edits), tests `SlotCatchUpTests.cs` (new), `RenderThreadPacingTests.cs`, `RenderThreadLifecycleTests.cs`.
   - **Docs:** `scroll.md`, `threading-render-seam.md`, `SPEC-INDEX.md`, `input-a11y.md`, `.claude/skills/fluentgpu-scroll/*`.
   - **App:** `C:\wavee\waveemusic\src\apps\Wavee\Screens\Diagnostics.Host.cs` (`catchUps=` on `scroll.frames`) +
     `CHANGELOG.md` bullet: "Touchpad scrolling no longer judders, and one slow frame no longer leaves scrolling a frame
     behind (#n)".
2. **Wave 2 — orchestrator:** builds, tests, gates (below), fixes fallout, then two commits in `..\fluent-gpu` (A, B) and
   one in the app repo; no push without the owner.

## Verification

Engine (`C:\wavee\fluent-gpu`):
- `dotnet build src/FluentGpu.slnx` and `-c Release` — clean (TreatWarningsAsErrors).
- `dotnet test src/FluentGpu.Engine.Tests` Debug and Release (`--blame-hang-timeout 5m`), and `src/FluentGpu.Windows.Tests`.
  New tests red before / green after: `TouchpadStampRaceTests`, `RenderThreadPacingTests.ALateFrameCostsOneVblank_…`,
  `APacedTurnWhoseSlotStaysBusy_…`; `ChronicallyOverBudgetFrames_…` green before and after (the half-rate guard).
- `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED" (Debug + Release), incl. `--suite scroll-motion`
  (`gate.touchpad.stamp-race`, `gate.touchpad.dm-stream-monotone`), `--suite scroll`, `--suite touch`.
- `check-canon.ps1` clean.

App (`C:\wavee\waveemusic`): `dotnet build Wavee.slnx` Debug + Release; `dotnet test src/apps/Wavee.Tests` Debug +
Release (no Wavee instance running during the suite).

Live, on the Snapdragon (Adreno X1-85, 120 Hz) — Release arm64 publish into a side folder, never the owner's
`bin\Release\…\publish` while it runs:
- **Pacing (scripted, no input):** rerun the 09-29 capture — verify instance via `ops/tools/evidence/Start-VerifyWavee.ps1`,
  `wavee://diag?cmd=probe&level=trace`, six glides each on a playlist and on artist `4LcUpNlXFEleaLlelmkv2R`
  (`cmd=scroll … move=glide`), `cmd=bundle`. Baseline 09-29: slot wait p90 7.2 ms, waited runs of 55–86 consecutive turns,
  6–10 repeats per ~540–790 presents. Expect: no waited run longer than 2 turns while `costEma` < 0.7·refresh,
  `[render.pace] catchUp=` ≈ number of late frames, repeats ≤ baseline, and Turn rows presenting 1–2 ms after the tick.
- **Touchpad (needs the owner's hand — DM cannot be synthesized):** Diagnostics ▸ Scroll ▸ Trace, drag on Home and an
  artist page, Export CSV. Expect: every fast-drag frame shows the previous tick's sample (the 09-25 baseline was 93 % +
  14 % same-tick flips), pose advance of exactly one sample on ≥ 99 % of fast drag frames, and the log's `scroll.burst`
  `jitter` for touchpad bursts (`notches=0`) below 0.20 (today 0.1–3.5). `FirstMotionLatency` now reads from arrival and
  will look ~8 ms longer than before — that is the old under-read disappearing, not a regression.

## Risks and limits

- **Touchpad latency** is unchanged versus today's typical frame (sample *k−1* at tick *k*); the tail where a frame showed
  the same-tick sample disappears. DM's own prediction (`CompositionDeltaMs`) is a separate, later knob.
- **Present-queue depth change mid-gesture** (1↔2) shifts the lattice by a refresh: at most one repeated/doubled sample at
  the change — same as today, and rare (8-sample hysteresis).
- **A UI frame later than one refresh** still shows one hold then one catch-up step (the sample truly does not exist yet).
- **Catch-up false positive** (slot busy for another reason): one skipped tick = one repeated vblank; the backoff stops
  repeats. The grace (2 ms at 120 Hz) covers the measured 0.2–0.7 ms retire offset with margin.
- **Principle 6 ("no throttles or holds")**: the catch-up is not a throttle — still one present per tick; it refuses to
  queue a frame that could only land late, exactly Chromium's swap throttling. Say so in §8.
- **Out of scope (follow-ups):** device-clock contacts (touch, pen, touchpad wheel fallback) have the same sample-and-hold
  shape with bounded extrapolation — unmeasured; per-present display attestation (DXGI `SyncQPCTime` vs the predicted
  present) so the present-time law itself is verified on device; the long-planned single wait over tick + credit.

---

## As implemented (2026-09-29) — deviations from the plan above

- **Same-tick re-run (`RenderThread._catchUpTickSeq`).** A UI publication wakes the render loop mid-tick; after a skip
  the plan's code would have re-taken the slot on the SAME tick, `ShouldSkip` would have read `tick − lastCatchUp = 0 ≤
  HoldTicks` and backed off for 120 ticks — back in the late phase for a second after nearly every late frame. A re-run
  on the skipped tick now returns at once (no slot take, no policy call, uncounted). Pinned by
  `RenderThreadLifecycleTests.AWakeOnTheSkippedTick_SkipsItAgain_WithoutRetakingTheSlot`.
- **High-resolution grace (`D3D12Device.WaitForSlotWithin`).** Nothing in the engine raises the process timer
  resolution, so `WaitForSingleObject(h, 2)` could last ~15.6 ms and swallow the ~8.5 ms retire. The bounded take waits on
  the latency waitable (index 0) and a one-shot `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer; the coarse timeout is only
  a backstop / fallback.
- **`gate.touchpad.dm-stream-monotone`** read its start offset one frame into the gesture, which only equals the rest
  offset under the old stamp; it now baselines on the rest offset before the gesture and compares against the finger's
  travel — valid under either rule.
- **Extra tests** beyond the plan: `ContactStampTests` (depth-2 law, producer-time independence), `SlotCatchUpTests`
  (fit boundary, EMA, hold window), `RenderThreadLifecycleTests.ABusySlotBeforeAnyPacedPresent_KeepsTheWait`.
- Failing-first verified: with the old stamp rule and a never-skipping policy, `TouchpadStampRaceTests`,
  `RenderThreadPacingTests.ALateFrameCostsOneVblank_…`, `…ACatchUpThatDoesNotHold_BacksOff`, the lifecycle busy-slot
  tests and the stamp/policy unit tests fail; `ChronicallyOverBudgetFrames_…` is green both ways (the half-rate guard).
- **Cost excludes wake lag (found live, 2026-09-29 14:12, the owner's Release build).** The first run of the landed code
  on the artist page showed catch-ups working when they fired (`slotWaitAvg` 0.04–0.12 ms after `catchUp=1–2`) but
  trapped windows with `costEma` 7.6–10.9 ms at `gpuMs` ≈ 2.3 and no catch-up: in a late run each turn starts late
  because the previous one ran past the tick, so the wake lag is the lateness itself and inflated the cost exactly when a
  catch-up was needed. `SlotCatchUp.Observe(workMs, gpuMs)` now takes render work + retired GPU only. Same session,
  touchpad `scroll.burst jitter` avg 0.03–0.12 on the artist page (was 0.18–0.98 on the same page in the morning's Debug
  sessions; the Uneven line is 0.20).
