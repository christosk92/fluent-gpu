# Handoff: FluentGpu / Wavee scroll rework (state at 2026-09-25, midday)

> Updated 2026-09-25 midday by session `c7e68925`. The filename keeps its original 2026-09-24 date; the content
> below is current as of this update. See "Done 2026-09-25 (session c7e68925)", "Measured truths" and "Open,
> awaiting owner" in §3 for what changed since the 2026-09-24 evening version.

You are taking over a large, mostly finished rework of the scrolling system in the FluentGpu engine
(`C:\wavee\fluent-gpu`) and its consumer app Wavee (`C:\wavee\waveemusic`). This file is the complete briefing.
Read it end to end before doing anything.

---

## 0. Read first (in this order)

1. **Both `CLAUDE.md` files.** `C:\wavee\fluent-gpu\CLAUDE.md` and `C:\wavee\waveemusic\CLAUDE.md`. The app one lists
   **out-of-scope paths you must never read** (PlayPlay/DRM — the full list is that file's "Out-of-scope paths"
   section; it is not restated here so this public doc never names the fenced material).
2. **The as-built scroll design** (the authoritative doc): `fluent-gpu/docs/design/subsystems/scroll.md`.
3. **The engine agent skill:** `fluent-gpu/.claude/skills/fluentgpu-scroll/`, which has `SKILL.md`,
   `where-to-change-what.md`, `recipes.md`, `pitfalls.md` and `gates.md`. Also the general engine skill
   `fluent-gpu/.claude/skills/fluentgpu/SKILL.md`.
4. **The GPU side:** `fluent-gpu/docs/design/subsystems/gpu-renderer.md` §13.1, the retained tiled composite owner
   section.
5. **The lab:** `fluent-gpu/docs/guide/scroll-lab.md`.
6. **App side:** `waveemusic/.claude/skills/wavee/scrolling.md` and `waveemusic/docs/guide/scrolling.md`.
7. **The plans, as history and rationale** (the docs above describe the as-built state), all in
   `fluent-gpu/docs/plans/`:
   - `scroll-rework-implementation.md`: the approved rework.
   - `scroll-rework-app-migration.md`: the API migration guide.
   - `scroll-gpu-retained-tiles-implementation.md`: the tiles design, with P0–P3 status notes.
   - `scroll-lab-implementation.md`: the lab plan, phases 1/2/2b/3.
   - `scroll-root-cause-2026-09-23.md`: the original root-cause report.

---

## 1. Owner rules (non-negotiable; the owner has been explicit and repeatedly frustrated when these were broken)

- **No band-aids. Fix root causes.** No throttles, ramps, per-frame budgets, hold timers, grace windows or deferrals
  that trade correctness (blank rows, pop-in, lag) for frame time. Never patch a patch. When a fix needs a second fix,
  stop and redesign.
- **Bug reports get a root-cause analysis first. Do NOT jump into code changes.** For each symptom present the
  evidence (logs, recordings, code file:line), your confidence, what evidence is still missing, and questions or
  capture requests. Implement only after the owner says go.
- **Test-first.** Every fix comes with a test or VerticalSlice gate that FAILS on the old code. Report the failing
  output, then the passing output.
- **No A/B tests for the owner.** They explicitly don't want to do A/B comparisons. Prove things objectively with
  recordings, tests and measurements.
- **No environment-variable switches** for behaviour or verification. Use runtime properties, `--fg` command-line
  switches (`Hosting/EngineSwitches.cs`), app settings, or always-on counters. **No source-text tests**: a test never
  reads or greps production source.
- **Zero-alloc hot paths, NativeAOT-safe** (no reflection JSON). Component props freeze at mount, so changing data
  flows through signals.
- **Models:** implementation subagents use **Opus** (`model: opus`); **Fable** only for planning and design. A resumed
  agent keeps its original model.
- **Build discipline:** only ONE agent builds at a time. Others write code or analyse read-only, because parallel
  builds collide on file locks (CS2012; `dotnet build-server shutdown` clears them).
- **Never stop or kill the owner's running Wavee** (or any app they launched). Publish to side folders. Live repro
  runs need the owner's Wavee closed, so ask first.
- **Nothing is committed.** Commit only when the owner asks. Every modifying `gh` call needs the owner's approval. The
  Wavee CHANGELOG needs issue numbers (`(#n)` bullets plus `Fixes #n`; a release gate checks them). Issues haven't been
  filed yet.

---

## 2. What the system is now (one paragraph; details in `scroll.md`)

- **Motion is an immutable analytic plan `p(t)`** (`Scroll/Motion`: `MotionSeg`, `ScrollPlan`, `PlanAuthor`),
  authored per input.
  - **Wheel:** WinUI's measured cubic, 32 DIP per notch over 0.257 s, starting from the position already shown (the
    pose-floor anchor). Notches accumulate. A hi-res mouse produces fractional notches.
  - **Touchpad (DirectManipulation):** the samples are present-time stamped with no second extrapolation. On lift, a
    least-squares velocity turns into a closed-form Decay fling, with the edge Spring worked out at authoring.
  - **Touch and pen:** Android-style bounded resampling (≤ 8 ms).
  - **Keyboard, thumb and programmatic:** Glide / Hold.
- **Plans travel lock-free** (`Scroll/Runtime/PlanSlots`) to the render thread. There `ScrollPoser` evaluates each
  plan at the predicted **present** time, clamps it to the UI-published **coverage** (so it never shows blank rows),
  snaps once, and poses the content plus every scroll-linked effect (`Scroll/Effects`: Sticky/StickyClip with a scope
  and an `Engaged` signal, Parallax/Fade, Collapse, StretchFromTop, Thumb) from that same value.
- **The UI thread's `Virtualizer` and `IExtentSource`** (Fixed/Measured, double offsets) realize every covered row in
  the same frame. Anchor corrections are applied in the same frame, and `FrameShift` keeps shift and coverage atomic.
  Persistent prefixes are covered from 0 (`ScrollContentPose.CoverageOf`).
- **Rendering is a retained tiled composite** (`Render/Tiles`, `SliceRecorder`, `D3D12Device.Composite`, 1024×512
  tiles). A pure scroll tick records 0 bytes and rasters 0 tiles, only a composite pass. Uploads and baked blur run on
  COPY/COMPUTE queues.
- **Pacing:** one present per compositor tick, decided for the tick the render thread woke on. It never waits for the
  UI. `PresentQueueDepthPolicy` goes to depth 2 only near the GPU budget.
- **Diagnostics are always compiled:**
  - `ScrollProbe` (levels Off/Summary/Trace; schema-2 CSV with a `vp` column; coverage, clamp, plan, extent,
    present and turn rows);
  - `BurstSummary`, `ScrollMetrics` and the render census;
  - GPU pass timing, `[render.pace]`;
  - the Wavee Diagnostics ▸ Scroll and ▸ Tiles cards;
  - the standalone **Scroll Lab** (`src/FluentGpu.ScrollLab`).
- **App surface:** `ScrollHandle` (`ScrollTo/ScrollBy/BringIntoView` with `ScrollMove.Glide|Immediate|Follow`,
  `Restore`), `UseScroll`/`UseScrollProgress`, `ListOptions.MeasureAll`, `ListRowEl` (a one-node row; not yet used by
  Wavee).

---

## 3. Status

### Done (all gates green at the last builder run)
- **Scroll rework Waves 0, 1 and 3:**
  - the old stack is deleted;
  - the new system is wired in the engine and the app;
  - the app is migrated;
  - the missing APIs are added (sticky engaged, collapse, stretch, `UseScroll`, measure-all);
  - Diagnostics cards, docs and skills exist.
- **Retained tiles P0–P3.** The gallery list went from 1.78 to **0.91 ms** GPU per frame, pixel-identical (65/65
  identity scenarios). P3 deleted the canvas/partial-repaint and layered paths and every engine env var.
- **Scroll Lab Phase 1:** surfaces, live tuning, F10 recording and F8 marks, metrics, the GPU pass timeline, and
  per-present pacing attribution.
- **Root fixes, each gated test-first:**
  - wheel: the notch accumulation (the "snail"), hi-res fractional notches and least-squares velocity (the 2,000 DIP
    jumps), pose-floor anchoring (the idle catch-up), and the Ctrl+wheel zoom sign;
  - touchpad: 6 touchpad bugs and the DM double-prediction spring-back;
  - pacing: the cliff (half-fps; the soak presented 99.97 % of ticks), the tick-filter lattice/missed-tick defects, the
    governor exemption, and the present ledger plus the repeats metric (a phantom-drop counting bug);
  - memory and Debug cost: the ImageCache reclaim / O(1) LRU, bounded Debug parity, and the probe double-writer race;
  - layout and paint: the persistent-prefix coverage clamp (collapsed playlists, hidden rows, white area, "blocked"
    scroll, the rail head) and StickyClip no longer reordering paint (the artist hero band).

### Done since this handoff was first written — the three in-flight items all finished
**Orchestration history:** the work-account session `7b1a35fb` hit its usage limit at about 14:25 on 2026-09-24
with two agents mid-edit. It was resumed from the personal account the same day as session `c7e68925`, where
successor agents finished all three items below. The cancelled spin-acceleration change (see Open decisions) was
never implemented — no agent touched it.

1. **Test-flake fixes — done.**
   - Concurrent GC is off (`ConcurrentGarbageCollection=false`) in VerticalSlice, `Engine.Tests`, `Windows.Tests`
     and `Wavee.Tests`. Background gen2 GC had inflated `GC.GetAllocatedBytesForCurrentThread` by up to ~8 KB.
     Under load: before 4/15 failures, after 0/15.
   - `gate.resource.epoch-ordering`: fixed a test race (`Probes.cs` `ResourceProbe`, `HooksSuite.cs`). Forced
     ordering failed the old test 200/200 and passes the new one; 8000 natural runs, 0 failures.
   - Wavee `AudioStreamTests`: `A_slow_range…` now uses a stepped byte-credit link. `An_external_body…` holds the
     CDN across the `LengthKnown` assertion. `A_source_that_throws…` uses the new `ReadThroughStarves` helper —
     the cause was the 250 ms `RefusedBackoffMs` racing a 300 ms `waitMs`; `Starved` means "call again". 40 runs
     idle and under load, 0 failures.
   - Hooks gates `scope-keepalive-parks` and `migration-sweep`: the new `Harness\Asserts.cs`
     `RunFrameToQuiescence` helper. Forcing the reactive slice to yield failed the old gates 30/30.
   - The owner's cancellation was honoured: no agent touched `FeelProfiles.WinUiExact` or `PlanAuthor.AccelFor`.
2. **Fade groups rebuilt every composite turn — fixed.** Design doc
   `composite-fade-groups-implementation.md` (its status note holds the as-built deltas):
   - distributed analytic feathers with a `Feather2` slot; `composite.hlsl` second feather; 56 root constants;
   - content-keyed group cache (`GroupCacheKey.cs`, item clips cut to the region);
   - segment-extent cut (`MainAxisGrows`, virtual lists only);
   - `TileBudget` raised to 5.0x, 48 MiB floor / 128 MiB ceiling; measured `visibleNeedMaxMiB` = 37.2;
   - `GpuKnockouts.GroupFades` (`--group-fades`) is a probe-only identity control;
   - Wavee: no `AutoEdgeFade` scroller has a `Fill` on the same node (all 33 sites scanned); app-guide rule 5
     added to `docs/guide/scrolling.md` and `.claude/skills/wavee/scrolling.md`.
   - Deviations from the design: folded fades are not routed onto the marker's layer (the no-cuts-in-inline-layer
     rule replaces it instead); the group key leaves out alpha, feather and clip; the footprint cap is by area.
     The fade-distribute-identity scene became a faded page plus a separate shelf, because the group-fades
     reference carries its own 2/255 rounding error at nested corners — the exact two-fade product is now covered
     by tile-feather-identity/product instead. **Owner sign-off on this scene change is still owed** (Open
     decisions (d) below).
   - Result: artist-bench GPU mean dropped from 4.0 ms to **2.673 ms** (p95 2.786 ms; plan baseline 4.04 / 5.55) —
     the ≤1.8 ms target was **MISSED**. Full breakdown in §Benches below; (a) in Open decisions names the
     remaining blocker.
3. **Acrylic missing on flyouts — fixed.**
   - Acrylic always cuts its own slice (`AcrylicSliceCap` 16, separate from `EffectSliceCap`). If it can't be cut,
     it keeps an opaque `FallbackColor` plate (`AcrylicFallbacks` counter) and never leaves a hole.
   - New rule: no slice cuts inside an open inline group layer.
   - New/rewritten gates: `gate.slices.acrylic-never-folds`; the in-window, static-chrome and plate gates now
     require a paired `Backdrop` item.
   - New pixel scenario: `tile-acrylic-budget-identity`.
   - The video symptoms noted alongside the original report (James Arthur ▸ Albums thin strips, the Rex Orange
     County skeleton) were not separately re-tested. The lyrics-blanking hypothesis raised alongside it was
     chased down separately — see next.

**Lyrics blur hypothesis: disproven.** `gate.slices.blur-rows-follow-scroll` PASS (turns=36, compositeOnly=25,
blurItems=244, bad=0). Lyrics blanking stays unexplained — kept as an open finding below.

### Done 2026-09-25 (session c7e68925)
This closes out old Open decisions (a) and (b) above (both superseded — see the trimmed Open list at the end of
this section) and lands several more root fixes plus app-side follow-ups.

- **Sticky clip is composite-time.** `CompositeSliceFlags.StickyClip` and `GpuKnockouts.StickyClipInPaint` move the
  sticky clip off `NodePaint.ClipRect` (which forced a re-record every page-scroll turn) onto the composite pass.
  Gate: `gate.slices.stickyclip-composite`. Artist-bench GPU mean dropped from 2.7 ms to **~1.0–1.3 ms** (the 1.8 ms
  target from the 2026-09-24 handoff is now beaten).
- **The hosted reactive flush runs to quiescence.** `ReactiveSliceMaxMs` is deleted outright (no per-frame
  correctness-for-time trade). New `[signals.runaway]` and `[signals.slow-unit]` logs catch a unit that doesn't
  finish. Gate: `gate.signals.frame-reaches-quiescence`.
- **The edge cue is now the analytic feather.** The old painted gradient is deleted; scroll chrome draws over the
  feather (`SliceRole.Chrome`). A `FeatherQuadSplit` interior/band split was added to support this.
- **Evidence diagnostics implemented.** Design doc `docs/plans/evidence-diagnostics-implementation.md`
  (status: As-built): a raster ledger, a pixel query, evidence bundles, the `wavee://diag` surface, an
  `ops/tools/evidence` harness, and profile-scoped verify instances.
- **Tile validity is content-derived.** `SliceDamageRect`/`AddDamage` are deleted outright. `gate.tiles.stale-zero`
  passes in all suites, and `gate.slices.inherited-opacity-rerasters` is promoted (was probe-only). This fixed the
  artist band tab row (stale tiles under an opacity change).
- **`WhileStuck` feather.** `EdgeFadeSpec.WhileStuck` and `CompositeSliceFlags.FadeWhileStuck`. Gate:
  `gate.scroll.engaged-feather-composite`.
- **Item-band deep rows.** The `SceneRecorder` suffix-row clip bug is fixed. Gate:
  `gate.scroll.item-band-deep-rows`, identity scenario `item-band-identity`. This fixed the blank vertical
  playlist.
- **`VirtualLayoutExtent.OffsetOf` honours the lead inset.** This fixed the Top-tracks row-1 indent.
- **App fixes (Wavee repo):**
  - known negatives for trait kinds, and the chart gate reads `Face|Availability`;
  - fetch due-bucket wake (`AnyDue`);
  - demand dedupe;
  - the scroll-spy has `NoSection`;
  - `Table.Outstanding` per-row count, so the transient chart `Failed` state is gone;
  - track rows no longer re-render on recycle (20.9 KB → 5.4 KB per recycled row; `TrackRowRecycleAllocationTests`)
    — a real down payment on Queued item 3 (Wave 2: `ListRowEl`), though the full one-node-per-row migration is
    still queued.
- **Gates (this session's run):** Engine.Tests 765/765; Windows.Tests 526/526 (sandbox-free); VerticalSlice ALL
  CHECKS PASSED (1605 checks) ×3 consecutive runs; `--repaint-identity` 115/115; Wavee.Tests 11948 passed, 1 known
  order-dependent failure (`AlbumReleaseFactsRulesTests.Length_IsSpelledOnce` — not a regression from this
  session's work, re-run in isolation to confirm before chasing it).
- **arm64 AOT publish:** `C:\wavee\waveemusic\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe`
  (0.3.0-beta.1-dev, PE ARM64).

### Measured truths (2026-09-25)
From the WinUI wheel-feel investigation (separate from the engine work above; feeds Open item (a)):
- WinUI `ScrollView` = **32.00 DIP/notch at every cadence**; there is **no spin acceleration** in any WinUI
  scroller.
- The classic `ScrollViewer` = **98 DIP/notch**, constant and front-loaded (no acceleration either, just a bigger
  fixed step).
- Wavee's `WinUiExact` still applies `AccelFor` (1.25–3.2×) and travels **2.2–2.7× further** than `ScrollView` on
  the same input.
- Report: `C:\WAVEE\wheel-curve-probe\reports\spin-acceleration-2026-09-25\index.html`. Optical meter:
  `C:\WAVEE\wheel-curve-probe\spinmeter` (see Open item (g) for a safety issue with this tool).

### Latest checks (2026-09-24)
- fluent-gpu Debug and Release: 0 errors.
- Engine.Tests 692/692; Windows.Tests 523/523.
- VerticalSlice ALL CHECKS PASSED (1560 checks) on 3 consecutive runs.
- `--repaint-identity` 85/85 identical.
- `check-canon` OK.
- Wavee.slnx Debug and Release: 0 errors.
- Wavee.Tests 11714 passed, 6 skipped.

### Benches (Release, `artist-bench --vertical --dipPerSec 2000`)
**Superseded 2026-09-25:** the StickyClip composite-time fix (see "Done 2026-09-25" above) dropped this to
**~1.0–1.3 ms**, beating the ≤1.8 ms target. Numbers below are the pre-fix 2026-09-24 baseline, kept for the
before/after record.
- after: GPU mean 2.673 ms, p95 2.786 (plan baseline 4.04 / 5.55; target ≤1.8 ms was **MISSED**);
- `--group-fades`: 2.891;
- `--edge-fades-off`: 1.477;
- `--ab-no-stickyclip`: 1.913;
- `--ab-no-magazine-fade`: 1.894;
- `--ab-no-shelf-fades`: 2.456.

Offscreen surfaces: 6.03 per frame (1.03 groups plus 4.99 acrylic-backdrop levels, 0 cache hits). The
virtualization bench mean is 0.947 ms (about 0.91 before). Logs:
`C:\Users\CHRIST~1\AppData\Local\Temp\claude\C--wavee-waveemusic\c7e68925-b096-4f1b-8520-be2a97c84270\scratchpad`
(`vsall1-3.txt`, `prefix.txt`, `ri3.txt`, `bench_*.txt`). New scratch harnesses in the same place: `sx`, `fg` (a
tree copy), `vs_new`.

### Queued (proposed order; ask the owner where marked)
1. **Publish `publish-next7`** once the builder is green. Use the scratchpad `pub.ps1 -Out
   ...\bin\Release\net10.0\win-arm64\publish-next7`, NativeAOT arm64 with symbols. Publish from **PowerShell**: the
   Bash tool reports x64 on this ARM64 box.
2. **Scroll Lab Phase 2 + 2b** (proposed; the owner hasn't answered yet):
   - raw WM_INPUT capture (RIDEV_INPUTSINK) while WinUI or Edge is in front, on one clock;
   - `BaselineImport` of WinUI probe CSVs, replay into WinUI, the lab and Edge, and HTML reports;
   - the WinUI **InteractionTracker reference mode** in `C:\WAVEE\wheel-curve-probe` (`ValuesChanged` and
     `InertiaStateEntered` physics numbers streamed to the lab);
   - **optical measurement** (barcoded rows plus Windows.Graphics.Capture);
   - host tunables (`HostTunables`).

   Then one ~10-minute session with the owner, so all constants come from WinUI's own numbers. The owner asked
   "can we measure WinUI again in labs".
3. **Wave 2: Wavee track rows → `ListRowEl`** (one node per row; `Track.Table.cs` `TableSlot`/`TableRowContent`,
   pooled slots, placeholder-in-paint). Makes "realize everything visible in one frame" cheap on real pages.
   **Partial progress 2026-09-25:** track rows no longer re-render on recycle (20.9 KB → 5.4 KB per recycled row,
   `TrackRowRecycleAllocationTests`) — see "Done 2026-09-25" above. The full one-node-per-row migration is still
   queued.
4. **Measure real Wavee pages** on the tiled path (the original "5 ms" came from Wavee pages: edge fades, blur,
   heroes). Needs a live repro: the owner's Wavee closed, a verify build, deep-link navigation.
5. **Scroll Lab Phase 3:** touch/touchpad metrics (tracking error, jump on touch), the remaining surfaces, and
   `.fgs` regression fixtures.

### Open decisions and findings to keep (do not act without the owner)
Old items (a) StickyClip-as-composite-time-clip and (b) reactive-flush-to-quiescence are **done** — see "Done
2026-09-25" above — and are removed from this list. The wheel-spin-acceleration finding is superseded by
"Measured truths" above and folded into Open item (a) below. Remaining items not superseded by 2026-09-25 work:
- **(c) Two latent wall-clock tests:** `A_starve_is_never_the_end…` and `Every_mirror_refusing…`.
- **(d) Owner sign-off owed** on the fade-distribute-identity scene change (see item 2 in "Done since this
  handoff was first written" above).
- **Present1 dirty rects** are unavailable: FLIP_DISCARD refuses them. The decision was to keep FLIP_DISCARD, so DWM
  composes the full window.
- **Touch and pen now use bounded resampling (≤ 8 ms)**, like the touchpad fallback. This was decided by the
  orchestrator; revisit only if the owner objects.
- **Gallery bench variance** (whole-frame mean 0.9–1.9 ms between identical runs, composite p50 stable at 0.54 ms)
  looked like GPU clock/power (battery, Balanced power plan). Re-measure on AC power.
- **Scroll Lab not built yet:** F9 A/B, 1/2/0 votes and CLI `--analyze` are phase 2. The owner doesn't want to do A/B
  tests personally, so blind A/B is a low priority. (Optical measurement itself has since progressed — see Open
  item (g) below.)

### Open, awaiting owner (2026-09-25)
The current, most-current list — read this before the section above.
- **(a) Which WinUI wheel feel to match** (`ScrollView` 32 DIP/notch vs classic `ScrollViewer` 98 DIP/notch), and
  remove spin acceleration from `WinUiExact`/`AccelFor` either way. See "Measured truths" above for the numbers.
- **(b) The Staging text-arena aliasing data corruption.** Wavee `Entities.cs` Staging Rent/Return pool has no
  ownership check; `Store.cs` `RowWriter.Id` calls `AddText` on the store thread. This corrupts persisted
  `track.image`/`title`/`share_url` etc. The seed call site that triggers it is **not located**. Proposed fix:
  enforce the lease and log a stack trace on a double return, make handed-over staging read-only, and run a
  one-time data repair. Not started — needs owner go-ahead given the "no band-aids" rule and the repair's blast
  radius.
- **(c) Touchpad fling "blocked".** Lift velocity is ~0 or reversed after a 150–215 ms decaying tail
  (`scroll-20260925-110029.csv`). Analysis is running; no root cause yet.
- **(d) Unreliable real thumb-drag grab** in the harness.
- **(e) Row metadata demand should cover the realize window.** Image pop-in: images are requested ~2.2 s after
  rows mount.
- **(f) Freezes of 56–98 ms were not reproduced.** The `UiGap` decomposition is now always-on, so the next
  occurrence should have evidence.
- **(g) Wheel on-screen measurement of Wavee** needs a 45-minute idle machine plus an `IsWindow`/`WindowFromPoint`
  guard in the meter. Incident on 2026-09-25: the meter sent 286 notches into the owner's browser — fix the guard
  before running it unattended again.
- **(h) Three runaway `node shots/take.mjs` processes** loading the CPU were observed, running since 00:26.
  Check whether they're still alive and kill them if they're not doing useful work.
- **(i) The ghost bar under the artist band** is shelf content under the 24-DIP feather (aesthetic, not
  functional).

---

## 4. Evidence and artefacts
- **Owner's logs:** `%LOCALAPPDATA%\Wavee\logs\wavee-YYYYMMDD.log`. Engine `Diag.Line` prefixes must be whitelisted in
  Wavee's `Platform.Host.cs` `AlwaysOn()`, or they never reach this log.
- **Owner's scroll CSVs:** `%LOCALAPPDATA%\Wavee\logs\scroll-*.csv`, from Diagnostics ▸ Scroll ▸ Export CSV; schema 2
  from `publish-next6` on. Touchpad input rows need probe level Summary or higher; contact rows are kept at Summary.
- **Scroll Lab sessions:** `%LOCALAPPDATA%\FluentGpu\ScrollLab\sessions\*`, containing meta/feel/events.csv/frames.*,
  turns.csv, markers, metrics.json and `probe-*.bin` (64-byte `ProbeRow`s).
- **The owner's standalone lab copy:** `C:\wavee\ScrollLab-run\FluentGpu.ScrollLab.exe`. Refresh it only when it isn't
  running.
- **WinUI probe:** `C:\WAVEE\wheel-curve-probe`, with `run.sh`, `analyze.py`, and `captures\` (`fast_bottom`,
  `impatient` and so on in `2026-09-23_02-13-54-guided.csv`).
- **Screenshots and video frames:** `C:\Users\CHRIST~1\AppData\Local\Temp\claude\C--wavee-waveemusic\7b1a35fb-0e28-4fa4-b882-542302005f9a\scratchpad\`
  (`images\`, `vid\sheet_*.png`, `vid\f_*.png`). Scratch scripts in the same folder: `pub.ps1` (publish), `csv*.py`
  (CSV analysis), `lab-run.ps1` (synthetic lab driver).
- **Resumed-session scratchpad (session `c7e68925`, personal account):**
  `C:\Users\CHRIST~1\AppData\Local\Temp\claude\C--wavee-waveemusic\c7e68925-b096-4f1b-8520-be2a97c84270\scratchpad\`
  — bench logs `vsall1-3.txt`, `prefix.txt`, `ri3.txt`, `bench_*.txt`, and the harnesses `sx`, `fg` (a tree copy),
  `vs_new`.
- **Publishes (the owner tests these):** `C:\wavee\waveemusic\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish-next4/5/6`.
  `next6` = the prefix-coverage and StickyClip fixes plus schema-2 export.
- **Snapshot refs** (uncommitted trees, taken with `git stash create` + `update-ref`), in both repos:
  - `refs/snapshots/pre-scroll-rework-20260923`
  - `refs/snapshots/post-wave0-20260923`
  - `refs/snapshots/post-wave1-engine-20260924`
  - `refs/snapshots/post-track1-20260924`
  - `refs/snapshots/post-wave1-app-20260924`
  - `refs/snapshots/pre-tiles-20260924`
  - `refs/snapshots/post-tiles-p1-20260924`
  - `refs/snapshots/post-tiles-p3-20260924`

  Keep taking one before and after each major step.

---

## 5. Commands
```powershell
# engine (C:\wavee\fluent-gpu)
dotnet build src/FluentGpu.slnx -c Debug ; dotnet build src/FluentGpu.slnx -c Release
dotnet test src/FluentGpu.Engine.Tests --blame-hang-timeout 120s ; dotnet test src/FluentGpu.Windows.Tests --blame-hang-timeout 120s
dotnet run --project src/FluentGpu.VerticalSlice -c Debug            # full gates ("ALL CHECKS PASSED (N checks)")
dotnet run --project src/FluentGpu.VerticalSlice -c Debug -- --suite scroll   # or tiles, listrow, touch, hooks...
powershell -File ops/<...>/check-canon.ps1                           # docs canon (find the script path)
# gallery probes (sandbox-free)
FluentGpu.WindowsApp.exe --scroll-bench virtualization --dipPerSec 3000 --seconds 8 --out <dir>   # + --vertical, knockouts --edge-fades-off --freeze-uploads --force-full-direct --clear-only
FluentGpu.WindowsApp.exe --scroll-bench artist-bench --vertical --dipPerSec 2000 --out <dir>
FluentGpu.WindowsApp.exe --scroll-soak virtualization --out <dir>
FluentGpu.WindowsApp.exe --repaint-identity                         # GPU pixel identity (65 scenarios)
# engine switches: --fg fps,layout,diag,gpu-timing,...  (Hosting/EngineSwitches.cs)
# app (C:\wavee\waveemusic)
dotnet build Wavee.slnx -c Debug ; dotnet build Wavee.slnx -c Release ; dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
```

---

## 6. Pitfalls learned the hard way
- **Always ask where a value is evaluated.** A present-time pose and a "now" evaluation disagree, and that
  disagreement causes catch-up steps and back-steps.
- **Snap once.** Effects derive from the snapped content translate; never re-snap.
- **Coverage is the no-blank contract.** Anything publishing coverage (including prefixes, measure-all and slices) must
  describe exactly what was built.
- **Predict once.** DM output is already composition-time; don't extrapolate it again.
- **Present statistics lag a flip.** Never compare submitted vs displayed per sample.
- **Per-thread allocation counters lie under background GC.** Zero-alloc gates need non-concurrent GC.
- **Headless hosts are single-threaded.** Render-thread behaviour needs real-window probes (the gallery bench and soak)
  or pure tests with `FakeDisplayClock` (`RenderThreadLifecycleTests`).
- **Resumed agents keep their model.** To switch to Opus, start a fresh agent with a state brief.
- **The session usage limit kills agents mid-work.** Resume the same agent with SendMessage (it keeps its context)
  and have it re-establish state with `git status` and a build first.

---

## 7. What to do first as the new agent
1. `git -C C:\wavee\fluent-gpu status` and `git -C C:\wavee\waveemusic status`: confirm the trees are **still
   uncommitted** (both repos have large uncommitted trees; nothing is committed except with the owner's explicit
   say) and the snapshot refs exist.
2. Everything under "Done" and "Done since this handoff was first written" in §3, **plus "Done 2026-09-25 (session
   c7e68925)"** (StickyClip composite-time, reactive flush to quiescence, the analytic-feather edge cue, evidence
   diagnostics, content-derived tile validity, `WhileStuck` feather, item-band deep rows, the lead-inset offset
   fix, and the App fixes) is landed. Do not re-do or re-diagnose any of it; verify with a build/test pass instead
   (step 4) and use the gate names given there to confirm.
3. Verify `FeelProfiles.WinUiExact` still has spin acceleration unchanged unless the owner has since answered Open
   item (a) below (which asks to remove it) — check the git diff on that file before assuming either way.
4. Build and run the gates (§5). Expect roughly: Engine.Tests 765, Windows.Tests 526, VerticalSlice 1605 checks
   ×3, `--repaint-identity` 115/115, Wavee.Tests 11948 passed (1 known order-dependent failure,
   `AlbumReleaseFactsRulesTests.Length_IsSpelledOnce` — re-run in isolation before chasing it), artist-bench GPU
   ~1.0–1.3 ms. Report the literal result lines to the owner; investigate any regression from these numbers before
   doing anything else.
5. Read **"Open, awaiting owner (2026-09-25)"** in §3 top to bottom — items (a)–(i) — plus the older still-open
   (c) and (d) directly above it. (a) needs a decision on which WinUI wheel feel to match, before removing spin
   acceleration. (b) is a data-corruption bug with no located repro yet — do not attempt the proposed fix without
   the owner's go given the blast radius of the repair step. (g) and (h) are quick operational checks (a
   safety-guard fix and a possible zombie-process cleanup) that don't need owner sign-off to investigate. Present
   the rest and **wait for the owner's go** before implementing anything in the Queued list.
6. Lyrics-panel blanking is an open, unexplained finding (the blur hypothesis was disproven) — don't assume it's
   fixed just because the acrylic and fade-group work landed.
