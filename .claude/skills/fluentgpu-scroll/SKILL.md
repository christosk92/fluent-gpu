---
name: fluentgpu-scroll
description: Use when changing or debugging ANY scrolling in the FluentGpu engine — wheel / hi-res wheel / touchpad / DirectManipulation / touch pan / fling / rubber band / snap points, ScrollPlan / PlanAuthor / MotionFeel / ScrollTunables, PlanSlots / ScrollPoser / coverage clamp / ScrollHandle (ScrollTo, BringIntoView, Follow, Restore, ScrollKey), ScrollRouter latch/chain, virtualization (Virtualizer, IExtentSource, MeasureAll, ListRowEl rows), scroll-linked effects (Sticky / StickyClip / engaged / Collapse / StretchFromTop / Parallax / Fade), scroll pacing (present per tick, missed ticks, [render.pace]), ScrollProbe / BurstSummary / ScrollMetrics, retained tiles under scroll, or the Scroll Lab. Read before touching src/FluentGpu.Engine/Scroll/**, Hosting/AppHost.Scroll.cs, Input/InputDispatcher.Scroll.cs, the Win32 wheel/DM path, or when a user says scrolling feels heavy, jittery, steppy, floaty, jumps, shows blank rows or lags the finger.
---

# Scrolling in FluentGpu — one plan, posed at present time

Scope: the engine's `src/FluentGpu.Engine/Scroll/{Motion,Runtime,Effects,Extent,Diag}/**`, its host wiring
(`Hosting/AppHost.Scroll.cs`, `SnapshotScrollPoseSink.cs`, `Hosting/Threading/*`), input dispatch
(`Input/InputDispatcher.Scroll.cs`), the Windows producers (`FluentGpu.Windows/Pal/{Win32Platform,Win32DirectManipulation,
CompositorTickFilter,Win32CompositorClock}.cs`) and the Scroll Lab (`src/FluentGpu.ScrollLab*`). General engine work:
the [fluentgpu](../fluentgpu/SKILL.md) skill. Drag auto-scroll and insertion: the [dnd](../dnd/SKILL.md) skill.

**Canon (contracts live there, not here):** `docs/design/subsystems/scroll.md` owns the whole system (motion, input,
runtime, extent, effects, pacing-as-scroll-sees-it, diagnostics, invariants, the deleted list). Tiles/slices/composite
are `gpu-renderer.md` §13.1. Lab usage: `docs/guide/scroll-lab.md`. This skill tells you which lever to pull and which
mistakes have already cost a fix round; it never restates a struct.

> The system is the 2026-09 scroll rework (plans in `docs/plans/scroll-rework-*.md`,
> `scroll-gpu-retained-tiles-implementation.md`, `scroll-lab-implementation.md` — history, not canon). The last touchpad
> pieces landed 2026-09-24: DM samples are composition-timed (`PresentTimed` → `ContactClock.Present`, never re-predicted),
> device-timed contacts use bounded (Android) resampling, no per-frame zero-delta samples exist, and a plan shift is atomic
> with its coverage (`ScrollCoverageRow.FrameShift`). 2026-09-25: DM's READY snap is not motion (`DmContactStream`) and
> DM's release verdict (`ContactRelease`) decides a touchpad lift from the newest real sample. 2026-09-29: a DM sample is
> stamped with the present of the first render turn that sees it (`ContactStamp.ForFrame` — the NEXT tick's present,
> never the tick that reads it), and a clock-paced render turn whose present slot is still busy past a short grace
> presents nothing instead of queueing behind the late frame (`SlotCatchUp`, `IGpuDevice.TryTakePresentSlot`). If the
> code moved since, the code wins — fix `scroll.md` with it.

---

## The mental model (30 seconds)

```
input ──► ScrollRouter ──► ScrollHandle ──► PlanAuthor.* ──► PlanSlots (seqlock) ──► render thread, every tick:
(wheel is URGENT)  latch/chain   the ONE writer   a new immutable     lock-free,           p = plan.Eval(presentSec)
                                                   closed-form plan    UI writes only       shown = clamp(p, coverage)
                                                                                            trans = snap(WindowOrigin − shown)
                                                                                            effects at the SNAPPED position
UI frame: RunScrollFrame (eval at the frame's present) → Virtualizer realizes the window → layout measures →
ApplyMeasured shifts the plan frame IN THE SAME CALL → coverage published → render poser adopts it.
```

Five facts carry everything:

1. **Motion is `p(t)`.** A `ScrollPlan` is ≤ 4 closed-form `MotionSeg`s (`Hold`/`Cubic`/`Decay`/`Spring`/`Glide`) or a
   contact ring. `Eval(t)` is pure. There is **no `dt`** anywhere. Every input makes a NEW plan through `PlanAuthor`.
2. **The render thread poses at the predicted PRESENT time** (`tick + (1 + depth)·refresh`), not "now". The UI thread
   evaluates the same plan with the same arithmetic for hit-testing and the published frame.
3. **Coverage clamp ⇒ never blank.** The poser never shows past what the UI realized; a clamp is a recorded defect
   (probe `Pose.Clamped`, `TileCensus.CoverageClamps`), never an empty band.
4. **Never jumps.** Measured corrections above the anchor shift the plan frame (`ScrollPlan.Shifted` via
   `PlanSlots.Shift`) in the same call; re-plans start at the position already shown (the pose floor).
5. **Doubles in content space; floats only relative to `WindowOrigin`.** Keep positions `double` until the one cast.

---

## Iron rules (the owner's)

1. **No band-aids.** No throttle, ramp, hold, cap, cooldown, "skip this frame while scrolling", noise gate or tuned
   constant that hides a symptom. Find the mechanism and fix it; if the cost is real, make the work cheaper (cheaper rows,
   retained tiles, off-frame uploads). `scroll.md` §14 lists what was deleted for being exactly this — never bring any back.
2. **Test first.** Reproduce the defect as a failing gate (`ScrollMotionSuite`/`ScrollEffectsSuite`/Engine.Tests) or a
   lab session metric BEFORE the fix. A fix without a failing-first gate is not done.
3. **No environment variables.** Diagnostics are runtime data (`ScrollProbe.Level`, `ScrollTunables`,
   `AppHost.GpuPassTimingEnabled`, `AppHost.RenderCensus`) or `--fg` switches (`Hosting/EngineSwitches.cs`). The engine
   reads no env var; don't add one, not even "temporarily".
4. **Zero managed allocation** on the poser tick, the frame step, the probe record paths and every bind/effect body.
   Wire once at mount.
5. **Measure feel with the lab, not with adjectives.** "Feels heavy" is a hypothesis; a recorded session's
   `ScrollMetrics` verdict is evidence. Record before AND after; compare against the `Standard` reference.
6. **Props freeze at mount** (`component-props-contract.md`): a scroller's `Handle`/options set in a propless factory
   are frozen; changing data reaches it through a signal, `ScrollHandle` calls, or a `Key` remount.
7. **One owner per concern:** `PlanAuthor` builds plans, `ScrollHandle` writes them, `Virtualizer.ApplyMeasured` is the
   one extent write, `ScrollEffectEval.SnapToDevicePixel` is the one snap, `ScrollProbe` is the one probe.

---

## Recipes (details + code: [recipes.md](recipes.md))

| Task | Where | Short form |
|---|---|---|
| Add a scroll effect channel | `Scroll/Effects/*`, `AppHost.ApplyEffectChannel` | enum + formula + factory + DSL; transform-class ⇒ `IsTransformChannel` + `EffectTransform.Add`; gate both posers |
| Add a feel knob | `Scroll/Motion/MotionFeel.cs`, `FeelProfiles`, `Scroll/Diag/ScrollTunables.cs` | field + every preset + `TunableF` + JSON; read at AUTHOR time only |
| Change the wheel curve | `PlanAuthor.WheelSeg`/`WheelNotch`/`AccelFor`, `SegKind.Cubic`, `ScrollMetrics.ReferenceCurve` | replay the owner's notch traces (`WheelFeelTraceTests`) first; keep accumulate/re-base, pose-floor anchor, C1 re-plans |
| Add an input source | PAL producer → `ScrollInputEvent` → `InputDispatcher.DispatchScroll` | real device stamps; producers never write plans/offsets |
| Debug jump / jitter / blank / lag | Scroll Lab + `ScrollProbe` Trace | record, read the verdict tile, find the probe rows at the worst sample |
| Add a gate | `src/FluentGpu.VerticalSlice/Suites/Scroll*Suite.cs` or `FluentGpu.Engine.Tests/Scroll*Tests.cs` | failing first; `Check("gate.scroll.<name> <prose>", cond, detail)` |

---

## Run things (exact commands)

```powershell
# Gates (build first; subagents: the orchestrator builds, not you)
dotnet build src/FluentGpu.slnx ; dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice -- --suite scroll          # ScrollSuite + ScrollMotionSuite + ScrollEffectsSuite
dotnet run --project src/FluentGpu.VerticalSlice -- --suite tiles           # TileSuite + SliceSuite
dotnet run --project src/FluentGpu.VerticalSlice -- --suite touch,listrow
dotnet test src/FluentGpu.Engine.Tests --filter "FullyQualifiedName~Scroll|FullyQualifiedName~PresentStatisticsLedger|FullyQualifiedName~PresentQueueDepth|FullyQualifiedName~RefreshLattice|FullyQualifiedName~RenderThreadLifecycle|FullyQualifiedName~RenderThreadPacing|FullyQualifiedName~SlotCatchUp|FullyQualifiedName~ContactStamp|FullyQualifiedName~TouchpadStampRace|FullyQualifiedName~GpuGovernorWake|FullyQualifiedName~ExtentSource"
dotnet test src/FluentGpu.Windows.Tests --filter "FullyQualifiedName~CompositorTickFilter|FullyQualifiedName~RenderDisplayClock|FullyQualifiedName~DmContactStream"

# The Scroll Lab (interactive; F8 felt-wrong, F10 record, Ctrl+T tuning pane, Ctrl+Shift+T tuning window)
dotnet run --project src/FluentGpu.ScrollLab -c Release
dotnet publish src/FluentGpu.ScrollLab -c Release -r win-arm64      # or win-x64 — NativeAOT, the realistic measurement build

# Bench / soak / pixel identity (gallery CLI arms, GPU required)
dotnet run --project src/FluentGpu.WindowsApp -c Release -- --scroll-bench virtualization --dipPerSec 3000 --seconds 10
dotnet run --project src/FluentGpu.WindowsApp -c Release -- --scroll-soak virtualization --scale 0.25
dotnet run --project src/FluentGpu.WindowsApp -c Release -- --repaint-identity
#   knockouts for bench/soak: --edge-fades-off --freeze-uploads --force-full-direct --clear-only ; output --out <dir>
#   (defaults .tmp/scroll-bench, .tmp/scroll-soak, .tmp/repaint-identity)

# Any FluentApp host: launch-time diagnostics
<app> --fg gpu-timing,fps        # full list: src/FluentGpu.Engine/Hosting/EngineSwitches.cs
```

Unattended SendInput A/B: `powershell -File ops\diag\scroll-lab-synthetic.ps1 -ExePath <FluentGpu.ScrollLab.exe>` per
build, then compare the printed `metrics.json` verdicts (wheel only — a touchpad/DM contact cannot be synthesized). The
ScrollTrace-era Wavee injector/packer (`synthetic-scroll-capture.ps1`, `analyze-cadence.py`, `pack-feel-summary.ps1`)
is retired with ScrollTrace.

---

## Deeper files in this skill

- [where-to-change-what.md](where-to-change-what.md) — task → file map (Engine / Windows / Controls / Lab / app surface).
- [recipes.md](recipes.md) — the six recipes with code and the checklist each ends with.
- [pitfalls.md](pitfalls.md) — the mistakes that already cost a fix round, with the mechanism and the rule.
- [gates.md](gates.md) — every scroll gate/test and what it pins; which suite tag runs it.
- Canon: `docs/design/subsystems/scroll.md`; tiles `gpu-renderer.md` §13.1; guide `docs/guide/scroll-lab.md`.
