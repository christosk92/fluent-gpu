# Phase 2 — Engine: exiting KeepAlive pages are render-frozen snapshots

Repo: `c:\WAVEE\fluent-gpu`. Binding laws: `AGENTS.md`. Do NOT git, stash, commit, build, or run tests. Write code + the gate only.

## Goal
Outgoing KeepAlive pages must not re-render (against foreign routes / unrelated signals) during an animated exit. Parking happens only in `FinishKeepAliveExit`; today `BeginKeepAliveExit` leaves the page live, which is defects B/C/D.

## Files you MAY edit
- `src/FluentGpu.Engine/Reconciler/Reconciler.cs`
- `src/FluentGpu.VerticalSlice/Suites/NavSuite.cs`
- `src/FluentGpu.VerticalSlice/Probes/Probes.cs` (only if you need a freeze-specific probe sibling of `ParkOrderPage`)
- `docs/design/subsystems/reconciler-hooks.md` (one sentence)

Do NOT edit OverlayHost, ScrollKernel, Wavee app files.

## Implementation

### CompEntry (`Reconciler.cs` ~line 36)
Add `public bool ExitFrozen;` orthogonal to `Parked`.

### RunComponent (~961)
Defer when `Parked || ExitFrozen`:
```
if (entry.Parked || entry.ExitFrozen) { entry.DeferredRender = true; return; }
```

### New `SetSubtreeExitFrozen(NodeHandle node, bool frozen, bool budgetReplays = false)`
Walk the subtree like `SetSubtreeParked` (~1578) BUT:
- NO `NodeFlags.Parked` (AnimScheduler must keep ticking exit tracks)
- NO `ActiveSig` flip (`UseActivation` timing stays park/unpark, not freeze)
- NO `Detach`, NO `OnNodeParkedChanged`
- Set `entry.ExitFrozen = frozen`
- If `frozen && entry.QueuedReplay`: cancel queue slot, hand debt back to `DeferredRender` (same as park)
- If `!frozen && entry.DeferredRender`: clear it and `Schedule()` if `!budgetReplays || TakeReplayBudget()`, else queue like unpark
- Recurse children

### Call sites
1. `BeginKeepAliveExit` AFTER `SeedExit` (~1408): `SetSubtreeExitFrozen(entry.Root, frozen: true);`
2. Mid-exit reclaim (`state.ExitingKey == key` branch ~1367-1373): after cancelling exit / restoring hit-test, call `BeginUnparkReplayWindow()` then `SetSubtreeExitFrozen(entry.Root, frozen: false, budgetReplays: true);`
3. `SetSubtreeParked(true)`: also `entry.ExitFrozen = false` (park subsumes freeze; `DeferredRender` debt carries). Do this in the CompEntry block when `parked` is true.

### Docs
`docs/design/subsystems/reconciler-hooks.md` "Transition-aware retained pages" (~211-216): add that the outgoing root is **render-frozen** (component render effects deferred via `CompEntry.ExitFrozen`) for the exit window, while node-level property binds still fire and the AnimScheduler keeps ticking. The 2s wall backstop on `FinalizeKeepAliveTransitions` is belt-and-braces, not the correctness mechanism.

Update `FinalizeKeepAliveTransitions` doc-comment (~1434) to say the freeze is the correctness mechanism; the deadline stays as belt-and-braces.

## Gate: `gate.reconciler.freeze-on-exit` in NavSuite.cs
Register next to `ParkBeforeRenderChecks` in `Run`.

Clone the park-before-render harness (`NavSuite.cs` ~404-460, `ParkOrderPage` in `Probes.cs` ~3229) WITH:
- `KeepAliveOptions(MaxEntries: 2, TransitionFor: (_, _) => MotionRecipes.PageSlideForward)` — Exit MUST be Active so `BeginKeepAliveExit` runs (not immediate park)
- Real `AnimEngine`: `var anim = new AnimEngine(scene); recon.Anim = anim;`
- Pages subscribe to the shared route signal AND an extra `Signal<int> noise` they also read in Render (for "unrelated-signal writes mid-exit")
- `UseActivation` logging: onActivated / onDeactivated each append `"act"` / `"deact"` (or similar) so you can count

Assertions (all in one Check, or split if clearer):
(a) After `route.Value = "b"` + Flush: outgoing page must NOT log `"a@b"` (no render against incoming route). An unrelated `noise.Value++` + Flush mid-exit must NOT produce another outgoing render.
(b) Mid-exit reclaim: set route back to `"a"` while still exiting. Exactly one deferred render of page `a` must run (replay). Incoming `a` is the reclaim, not a remount.
(c) `UseActivation`: onActivated exactly once when `a` first mounts; onDeactivated exactly once when `a` actually parks (FinishKeepAliveExit), NOT at freeze/exit start. When `b` mounts, one onActivated. Reclaim of `a` fires onActivated once more (it was never deactivated during freeze).

If (c) is awkward because reclaim never parked: then onDeactivated count for `a` stays 0 until a DIFFERENT navigation finishes the exit and parks. Design: freeze does not flip ActiveSig, so `a` stays "active" in UseActivation terms during its own exit until FinishKeepAliveExit. The plan says "UseActivation fired exactly once each way" meaning freeze must NOT add extra activation edges — park/unpark remain the only edges. Pin: freeze-on-exit does not call onDeactivated; FinishKeepAliveExit (park) still does.

Wire AnimEngine so SeedExit doesn't NRE (`Anim!.CancelAll` in BeginKeepAliveExit).

Look at `KeepAliveWedgedExitBackstopChecks` / gate.reconciler.keepalive-exit-backstop for Anim + TransitionFor patterns.

## Report
Write `.superpowers/sdd/p2-report.md` with files changed, how freeze differs from park, gate name, concerns. Return status only.
