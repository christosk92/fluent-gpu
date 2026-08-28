# Phase 4 — Engine: scroll restore is a goal, not an event

Repo: `c:\WAVEE\fluent-gpu`. Binding laws: `AGENTS.md`. Do NOT git, stash, commit, build, or run tests. Write code + gates + the plan-doc field note only.

## Goal
`TryApplyRestore` is single-shot: first frame with a viewport clamps against the *current* (often short) extent and clears `RestorePending`. Un-park drip grows extent across frames → paint-then-jump (defect A residual). Restore must stay latched until the extent can hold the saved offset, or a retry deadline, or user/programmatic intent cancels it.

`docs/design/SPEC-INDEX.md` has NO ScrollBody entry — do not add one. `SeedRestore` doc-comment at `Reconciler.cs` ~2050 already states the contract; make `TryApplyRestore` literally fulfill it. Optionally tighten that comment if the retry/deadline is worth one clause.

## Files you MAY edit
- `src/FluentGpu.Engine/Scroll/ScrollBody.cs`
- `src/FluentGpu.Engine/Scroll/ScrollKernel.cs`
- `src/FluentGpu.VerticalSlice/Suites/ScrollKernelSuite.cs`
- `docs/plans/scroll-v3-plan-2026-08-17.md` (field list ~78 + §3.1 Restore note ~112 / the Restore row in the bypass table ~126)
- `src/FluentGpu.Engine/Reconciler/Reconciler.cs` — ONLY the `SeedRestore` xml doc (~2050-2054), no logic

Do NOT edit OverlayHost, NavSuite freeze, Wavee app files.

## Implementation

### ScrollBody
Add `public byte RestoreRetries;` next to `RestorePending`. Stay blittable/POD.

### Constant
`RestoreMaxRetries` ~180 (plan: ~180 Reclamp passes). Put it on ScrollKernel as a private const.

### TryApplyRestore (~380-390)
Today: clamp-and-apply, clear pending, return true.

New:
1. If `!RestorePending` or `ViewportMain <= 0`: return false (unchanged latch-until-geometry).
2. Clamp-and-apply best-effort EVERY retry (`SetOffsetMain` to `clamp(value, 0, maxOff)`).
3. Resolved (clear pending, zero retries, return true) when `maxOff >= value - 0.5f` OR `RestoreRetries >= RestoreMaxRetries`.
4. Else: increment `RestoreRetries` (byte, saturating), **stay latched** (`RestorePending` remains true), return false. `ResolveRestores` already retries each Reclamp.

`value` is the saved main-axis restore (`RestoreX`/`RestoreY`). Compare against `maxOff` as specified.

### ApplyRestore (~354)
Reset `RestoreRetries = 0` when seeding a new restore (so a later Restore command starts a fresh goal). Keep `_restorePendingCount` bookkeeping: increment only when newly pending; decrement only when `TryApplyRestore` returns true (resolved).

### CancelRestore helper
Clear `RestorePending`, `RestoreRetries = 0`, and if it was pending decrement `_restorePendingCount`. Call from:
- `ApplyContactBegin`
- `ApplyThumbSet`
- `ApplyCancel`
- `ApplyWheelNotch`
- `ApplyScrollTo` / `ApplyScrollBy` (the ScrollTo appliers — user/programmatic intent beats a stale restore)

AnchorShift under restore should KEEP the restore (existing code already shifts RestoreX/Y) — do NOT cancel there.

### Existing latch gate
`RestoreLatchUntilExtentCheck` (~429) must stay green: unknown geometry still latches; once extent can hold 250 with viewport 400 / extent 1000, it lands.

## Gates (ScrollKernelSuite, beside RestoreLatchUntilExtentCheck)
Register in the suite `Run`. Names:
- `restore-goal-extent-grows` — seed Restore(250) with a SHORT frame (e.g. extent 200, viewport 400 → maxOff=0). First Reclamp applies 0 best-effort but stays pending. Grow extent (SetFrame extent 1000) + Reclamp → lands at 250, pending cleared. Must NOT re-fire after resolve (a further grow does not jump).
- `restore-cancel-on-input` — seed Restore against short extent (still pending), then ContactBegin (or ThumbSet/Wheel/ScrollTo) → pending cleared, subsequent extent grow does NOT apply the old saved offset.
- `restore-deadline` — seed Restore of a huge offset, keep extent too small for ~180+ Reclamps → eventually resolves (pending false) even though maxOff never fits. Count Reclamps; must bound.

Existing zero-alloc / latch gates stay green. Match the suite's Check() style.

## Docs
`docs/plans/scroll-v3-plan-2026-08-17.md`:
- Field list (~78): add `RestoreRetries`
- Restore description: restore is a goal retried each Reclamp until extent fits, input cancels, or ~180 retries.

Anchor-based restore is DEFERRED — record as a one-line follow-up in the plan doc if there's a natural place; do not implement.

## Report
Write `.superpowers/sdd/p4-report.md` with files changed, retry constant chosen, gate names, concerns. Return status only.
