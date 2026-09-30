# Scroll gate map + the verification loop

Every behaviour below is pinned by a headless VerticalSlice check or a pure unit test. **Find the gate that already owns
the behaviour you are about to change** — if you have to weaken one, you are changing a contract and owe
`docs/design/subsystems/scroll.md` (§11 invariants, §12 catalogue) an edit too.

## Where the gates live

| Suite file (`src/FluentGpu.VerticalSlice/Suites/`) | `--suite` tag | Scroll gates |
|---|---|---|
| `ScrollMotionSuite.cs` | `scroll` | `gate.scroll.{no-blank, extent-stable, anchor-holds, precision-deep, bring-node-deep, 100k-flat-zero-alloc, notch-to-present, hires-burst, sticky-grid, nested-latch, shift-coverage-atomic, prefix-coverage, probe-export-chain}`, `gate.touchpad.dm-stream-monotone`, `gate.touchpad.stamp-race`, `gate.input.ctrl-wheel-zoom-sign` |
| `ScrollEffectsSuite.cs` | `scroll` | `gate.scroll-effects.{engaged-edge, stickyclip-paint-order, collapse-monotone, collapse-trailing, collapse-hit, stretch-from-top, use-scroll, measure-all}`, `gate.scroll.stretch-under-collapse` |
| `ScrollSuite.cs` | `scroll` | controls/virtualization over the new runtime: `gate.scroll.*` (bring-into-view, measured corrections, anchor re-pin, scrollbar dwell, annotated rail, wheel-through-sticky, hover-follows-content, zero-overflow latch…), `gate.snap.*`, `gate.touchpad.axis-from-first-movement`, `gate.list.*`, `gate.virt.*`, the `e11virt.*` / `cp*` families |
| `EngagedFeatherSuite.cs` | `scroll` | `gate.scroll.engaged-feather-composite` |
| `ItemBandDeepSuite.cs` | `scroll` | `gate.scroll.item-band-deep-rows` |
| `TileSuite.cs`, `SliceSuite.cs` | `tiles` | `gate.tiles.*`, `gate.slices.*` (owner: `gpu-renderer.md` §13.1k) |
| `EvidenceSuite.cs` | `tiles` | `gate.tiles.stale-zero` (permanent, brackets every suite), `gate.tiles.ledger-alloc-zero`, `gate.tiles.capture-seq-aligned`, `gate.tiles.item-record-matches-model`, `gate.slices.inherited-opacity-rerasters` (issue #1's regression pin — a plain check since content-derived tile validity) |
| `ListRowSuite.cs` | `listrow` | `gate.listrow.{placeholder-preserves-geometry, zero-alloc-on-text-change}` |
| `TouchSuite.cs` | `touch` | `gate.touch*.*`, `gate.arena.*` (pan claim, fling, snap, overscroll, thumb, pinch) |

`--suite scroll` runs every `scroll`-tagged suite. The `Check(...)` first argument is `"<gate-name> <prose>"` —
the leading token is the gate name. The headless contact producer is `HeadlessScrollProducer` (`Probes/Probes.cs`);
`HeadlessWindow.SendWheelNotch`/`SendScroll` drive the urgent sink.

Unit tests (`dotnet test`): `src/FluentGpu.Engine.Tests/` and `src/FluentGpu.Windows.Tests/` — names below.

## Motion (pure)

| Test | Pins |
|---|---|
| `ScrollMotionTests.Eval_IsContinuousAcrossSegmentBoundaries` / `Eval_AtSharedTimes_IsIdenticalRegardlessOfSampleOrder` | closed forms, no hidden state, sampling order irrelevant |
| `Cubic_EndpointIsExact`, `Decay_AsymptoticDistance_EqualsV0OverK`, `RubberBand_IsMonotoneAndBoundedByTheViewport`, `Eval_AtListMagnitudeOffsets_IsSmoothToWithinOneMicroDip` | segment math, precision at list magnitudes |
| `WheelNotch_StartsExactlyAtTheDisplayedPosition_NoJump`, `_FastSpinAccumulates_EveryNotchTravelsItsFullDistance`, `_Reversal_RebasesOnTheDisplayedPosition_NoJump`, `_AfterTheGlideSettled_RebasesOnTheRestingPosition`, `_HiResPackets_TravelExactlyTheTurnedDistance`, `_StampedBeforeTheLastPosedPresent_HasNoCatchUpStep`, `_ReversalStampedBeforeTheLastPosedPresent_NeverStepsBack` | the wheel model: accumulate / re-base / fractional notches / pose-floor anchor |
| `Follow_CoincidentPacketPair_DoesNotExtrapolateAtThePairSlope`, `ContactRing_Velocity_OfBunchedPairs_IsTheStreamRate`, `ContactRing_TimeShift_KeepsSpacingAndPositions` | the contact ring, its LSQ velocity and its (bounded) resampling |
| `TouchpadDragMonotonicityTests.*` | a touchpad drag never steps back against an advancing finger: DM streams (present-stamped, held, no zero-delta samples) and device streams (bounded Android resampling); the `StoppedAfterS` lift rule |
| `FollowEnd_DmReadySnapAsTheNewestSample_HoldsAFastFlick_TheDefectShape`, `FollowEnd_PresentClockMovingRelease_FlingsFromTheNewestRealSample_WithoutAJump`, `FollowEnd_StoppedRelease_HoldsWhereTheContactShows` | the DM lift over the owner's real contact 66: the producer's verdict decides (`ContactRelease`), a Present-clock release is authored from the newest real sample at the End stamp (no jump); the pinned defect shape |
| `TouchpadReleaseDispatchTests.*` | the same contact through `InputDispatcher.DispatchScroll`: the old stream (snap sample + verdict-less End) holds, the new one (no snap, End `Moving`) flings, `Stopped` holds |
| `FollowEnd_FingerStoppedBeforeTheLift_NeverFlings`, `FollowEnd_LiftedWhileOverpanned_*`, `FollowEnd_LateLiftAfterACoincidentPair_StartsNearTheStream`, `FollowEnd_ReleaseVelocity_IgnoresACoincidentPairWithNoSpanBehindIt`, `FollowCancel_Overpanned_*` | release rules |
| `FlingCrossingAnEdge_RubberBand_ProducesSpring_EndingAtEdge`, `FlingCrossingAnEdge_None_ProducesHold_EndingAtEdge`, `SnapFling_LandsExactlyOnTheGrid_InFiniteTime_AtAnySamplingRate` | edges and snap solved at authoring |
| `Shifted_TranslatesEvalByDelta`, `Glide_IsVelocityContinuousFromTheHandoff` | frame shift is not motion; glides continue velocity |
| `WheelFeelTraceTests.*` | the default wheel feel over the owner's REAL notch stamps (embedded): an isolated notch = 64 DIP, 50 % <= 55 ms, at rest <= 220 ms; the 12 recorded fast spins within ±15 % of the recorded 32 DIP totals (single notch / slow roll travel further); every same-direction re-plan keeps the shown velocity (C1, 1 %); cadence sweep 3–100 ms × 1/3/10/50 notches monotone, bounded, landing exactly; a faster spin never scrolls slower; hi-res fractions scale linearly |

## Runtime (pure)

| Test | Pins |
|---|---|
| `ScrollRuntimeTests.PlanSlots_*` | round trip, scan bound, stale generation, `Shift` = Eval − delta, seqlock never tears, capacity 64 |
| `Poser_CoverageClamp_NeverShowsOutsideWindow_AndRecordsClamp`, `Poser_OverpanAtContentStart_IsNotAClamp`, `Poser_StickyEffect_SitsOnContentPixelGrid_BitExact`, `Poser_HasActive_And_Changed_Semantics`, `Poser_PlanShiftNewerThanItsCoverage_PosesInTheCoveragesFrame_UntilTheNextAdopt` | the poser, incl. the frame rule (shift atomic with coverage) |
| `WheelClassifier_FirstPacketTable`, `_LatchesHiResForTheGesture_AndReleasesAfterGap`, `_TouchpadEvidenceWins_UnlessAMouseWasPositivelySeen`, `_Carryover_EmitsWholeNotchesAndKeepsRemainder` | classification |
| `Router_ShelfReachesEdgeMidSpin_DoesNotHandOff`, `Router_GestureStartedAtEdge_HandsOffToParent_ButReverseGoesBackToShelf`, `Router_ShiftWheel_RoutesHorizontal`, `Router_ContactLatch_HoldsBeginToEnd_ThenReleases`, `Router_Keyboard_NotLatched_WalksUpOnlyWhenPinned` | routing |
| `Handle_ScrollTo_*`, `Handle_Feedback_DrivesOffsetAndMotionSignals`, `Handle_Follow_YieldsToALiveUserPlan`, `Handle_BringIntoView_MinimalMove`, `Handle_Stop_HoldsWhereTheContentWasShown_NeverStepsBack`, `Handle_ContactBeginHere_GrabsTheShownPosition`, `Handle_LiftDetectedAfterSilence_FlingsWithoutAJump`, `Handle_ContactSamplesAheadOfThePlanClock_ResyncKeepsTheDeviceSpacing`, `Handle_PresentClockContact_KeepsItsPresentStamps_AndTheLiftNeverRewinds`, `RestoreLatch_HoldsUntilExtentCanHoldTheTarget` | the handle, incl. both contact clocks |
| `Virtualizer_Window_CoversViewportPlusVelocityOverscan`, `Virtualizer_Anchor_IsTheFirstFullyVisibleRow_*`, `Virtualizer_ArrangeOrigin_KeptAcrossWindowShifts_*`, `Virtualizer_ApplyMeasured_AboveAnchor_KeepsAnchorRowScreenPositionUnchanged`, `CoverageTable_CopyFrom_IsAValueCopy` | virtualization |
| `ExtentSourceTests.*` | Fixed / Measured (Fenwick) / VirtualLayout extent sources |
| `VirtualLayoutExtentLeadInsetTests.*` | `OffsetOf(0)` answers the layout's own rect (a lead inset), like every other index |
| `ScrollEffectTests.*`, `ScrollLinkedEffectTests.*` | effect formulas, folding, dual-thread agreement |
| `ScrollParkedViewportTests.AParkedViewportWithALivePlanIsNotEvaluatedByTheFrameStep`, `ScrollPoseDamageTests.ASettledViewportReposedWithAnUnchangedTranslateContributesNoRepaintBand` | idle cost |

## Pacing (pure)

| Test | Pins |
|---|---|
| `RenderThreadLifecycleTests.AtMostOnePresentPerCompositorTick`, `FreshPublicationWinsOverMotionOnTheSameTick`, `LatePublicationWaitsForTheNextTick_AndThenWins`, `WithoutMotion_PublishWakePresentsImmediatelyRegardlessOfTheTick`, `PresentSlotWait_IsPaidBeforeTheFrameIsChosen_AndOnlyWhenOneIsPending`, `MotionTick_TakesThePresentSlotCredit`, `PublicationArrivingDuringTheSlotWait_WinsOverTheMotionTick` | present per tick, never waiting for the UI |
| `PresentQueueDepthPolicyTests.*` | depth 1 ↔ 2 hysteresis; the prediction follows the depth |
| `RefreshLatticeTests.*` | the frame clock's present prediction and monotonicity |
| `PresentStatisticsLedgerTests.*` (incl. `MotionTickRunChargesOnlyTicksSkippedWhileMotionIsLive`) | the paired-counter ledger, missed-tick runs |
| `GpuGovernorWakeTests.AScrollProducerFrameIsNeverPaced`, `AmbientWorkAloneIsPaceable_AndEveryInteractionBitExemptsTheFrame` | governor exemption |
| `CompositorTickFilterTests.*` (Windows.Tests) | double ticks, bursts, synthesized ticks, lattice snap/resync, decimation |
| `DmContactStreamTests.*` (Windows.Tests) | the DirectManipulation producer's decision over REAL per-Update TpLog scripts: the READY snap (content before the status edge) is never a sample, an INERTIA edge is a `Moving` release that stops DM at the next pump, a READY edge a `Stopped` one; content is delivered when its Update returns |
| `RenderDisplayClockTests.*` (Windows.Tests) | the render display clock |

## Diagnostics (pure)

| Test | Pins |
|---|---|
| `ScrollProbeTests.*`, incl. `TurnCost_*`, `Csv_WritesTheTurnCostRow`, `Csv_Schema4_TouchpadRowsCarryPhaseAndRelease`, `Input_KeepsTheReleaseVerdictOfAnEnd`, `Summary_KeepsDetentedAndHiResNotchRows` | level gating, ring wrap, burst verdicts, tunables JSON/profile/version, one render-ring producer, the `TurnCost` row, the schema-4 CSV (touchpad rows name their phase + the End's release), notch rows kept for every wheel source at Summary |
| `ScrollJumpRulesTests.*` | the always-on jump detector: an anchored correction is no jump, a lost shift is (`plan`), unanchored extent (`extent`), an unfollowed shift (`shiftframe`), a clamp (`coverage`), programmatic moves and sub-pixel motion never are, nothing fires while the user scrolls or within the at-rest window, a re-anchored window is judged by where the previous anchor row sits |
| `ScrollMetricsTests.*` | every lab metric: reference curve, verdict thresholds, attested repeats, missed-tick attribution, plan-anchored curve metrics |

## VerticalSlice — what each scroll gate proves

| Gate | Pins |
|---|---|
| `gate.scroll.no-blank` | 100k list fully covered on every frame of a fast touchpad fling, a 60-notch storm and a reverse fling |
| `gate.scroll.extent-stable` | extent published the same frame; grows only as estimates realize; boxes agree with the extent table |
| `gate.scroll.anchor-holds` | upward drag realizing estimate rows above tracks the finger 1:1 (≤ 1 device px) |
| `gate.scroll.precision-deep` / `bring-node-deep` | 4M DIP deep exactness |
| `gate.scroll.100k-flat-zero-alloc` | 0 hot-phase bytes, zero templates run over a warm 100k fling |
| `gate.scroll.notch-to-present` | synchronous authoring, moves on the NEXT present, monotone, lands exactly one notch |
| `gate.scroll.hires-burst` | fractional notches travel exactly the turned distance, no coast |
| `gate.scroll.sticky-grid` | a pinned header never drifts vs its rows at fractional scales |
| `gate.scroll.nested-latch` | contact latch for the gesture's life; chaining only from a gesture that began at the edge |
| `gate.scroll.explicit-measured-correction-{wheel,programmatic,direct-touch}`, `gate.scroll.anchor-repin-under-gesture` | measured corrections above the viewport during live motion never move what the user sees |
| `gate.scroll.prefix-coverage` | a persistent prefix is covered: a prefixed list (hero + chrome; a rail head) shows 0 at rest, unclamped, and moves on the first notch |
| `gate.scroll.probe-export-chain` | the probe CSV (schema 4, incl. the `TurnCost` row) names viewports; a spin's plan chain is continuous across bursts |
| `gate.scroll.reseed-anchored` | a wholesale reseed of a measured layout (the V3 rail: head seed 101, real 287) never moves the rows on screen; the always-on jump detector counts a bare `ShiftFrame` at rest (cause `shiftframe`) |
| `gate.scroll-effects.stickyclip-paint-order` | StickyClip never re-orders paint; Sticky pins above its sibling |
| `gate.scroll.engaged-feather-composite` | a `WhileStuck` edge fade is off at rest and feathers the sticky cut in the first composited frame that cuts |
| `gate.scroll.item-band-deep-rows` | a deep jump in a prefixed item-band list (arrange origin re-centred) leaves no row gap under the band: rows record against the content clip, the band clip applies at composite |
| `gate.scroll.shift-coverage-atomic` | a render tick between a mid-layout shift and the next adopt poses the old content in its own frame (no transient jump) |
| `gate.touchpad.dm-stream-monotone` | a DM-shaped stream (present-stamped, packetless frames, decelerating) never steps back at a present or a render tick between frames; a rested lift does not fling |
| `gate.touchpad.stamp-race` | a DM-shaped drag poses the same sequence whether the render read lands before or after the UI's DM write each tick (`ContactStamp` — the next tick's present); one sample per frame, irregularity < 0.15 |
| `gate.input.ctrl-wheel-zoom-sign` | Ctrl + a notch rotated away hands `InputHooks.ZoomWheel` a positive count; a tilt never zooms |
| `gate.scroll.bring-into-view` | minimal/aligned/animated bring-into-view |
| `gate.scroll.wheel-through-sticky-overlay` | `Element.WheelTarget` forwards a header's notch to its list as a normal glide |
| `gate.scroll.hover-follows-content(.recycled-slot)` | hover moves with content under a still cursor, 0 alloc |
| `gate.scroll.zero-overflow-latch`, `gate.touchpad.axis-from-first-movement` | routing edge cases |
| `gate.scroll.sb-*` | conscious scrollbar dwells run on host one-shots and produce no idle frames |
| `gate.snap.*` | snap declarations, shelf paging, dt-invariant page glide |
| `gate.scroll-effects.*` | engaged edge exactness, collapse, stretch, `UseScroll`, `MeasureAll` |
| `gate.scroll.stretch-under-collapse` | the hero recipe composes: a leading `Collapse` root WITHOUT `ClipToBounds` lets its `StretchFromTop` photo draw into a top overpan, and still cuts paint + input at the presented edge |
| `gate.touch.flick-decay-settle`, `gate.touch.flick-velocity-windowed`, `gate.touch.fling-alloc-steady-zero`, `gate.touch.thumb-drag`, `gate.touch.tap-vs-pan` | touch pan, fling and thumb |
| `gate.touch4.overscroll-springback`, `gate.touch4.wheel-hard-clamps-no-band`, `gate.touch4.fling-snap-lands-on-snap`, `gate.touch4.snap-fling-dt-invariant`, `gate.touch4.pan-continuation` | rubber band for contacts only, snap flings, pinch → pan |
| `gate.virt.slotPool*`, `gate.list.*` | bound-list slot pools under fling (0 alloc, no cold mounts on reversal) |
| `gate.slices.scroll-tick-zero-bytes`, `gate.tiles.scroll-no-copy`, `gate.tiles.coverage-clamp`, `gate.tiles.no-blank-8000` | the render side of "a scroll tick is composite-only and never blank" |
| `gate.slices.fade-leaf`, `gate.slices.fade-follows-page`, `gate.slices.group-not-rerendered`, `gate.slices.fold-keeps-fade` | AutoEdgeFade scrollers: distributed analytic feathers (no group surface), a shelf feather rides the page with 0 bytes recorded, a remaining group is re-drawn from its retained surface on a page scroll, a folded fade still feathers |
| `gate.slices.acrylic-never-folds`, `gate.slices.blur-rows-follow-scroll`, `gate.tiles.segment-extent`, `gate.tiles.memory-ceiling` | acrylic always cuts its own slice (never a hole); self-blurred rows keep their source across composite-only turns; thin scroll segments are cut on the main axis; resident + retained within budget |
| `gate.slices.inherited-opacity-rerasters`, `gate.tiles.stale-zero`, `SliceTableContentTests.*` | tile validity is CONTENT-derived: a `.Reveal` band's recorded alpha re-rasters its nested tab slice on every step that moves it (and on no other); no suite ever leaves a valid tile rastered for other content; an unchanged segment key folds nothing |
| `gate.slices.stickyclip-composite`, `gate.slices.stickyclip-cuts-at-line` | a page scroll under a `.StickyClip` band is composite-only (0 bytes, 0 tiles) and its group hits its cache; the composite-time clip cuts at the exact band line |
| `gate.slices.edge-cue-is-feather`, `gate.slices.chrome-over-feather`, `gate.slices.slice-root-layer` | the default `EdgeCues` is the analytic feather on the content item, no painted band; a visible thumb inside the band keeps the fade distributed and itself unfeathered; a fading ROOT scroller cuts its fade as a `Layer` slice (no fold, 0 bytes a tick) |
| `gate.signals.frame-reaches-quiescence`, `gate.signals.keepalive-parks-in-one-frame` (in `HooksSuite`) | one `RunFrame` drains every reactive unit, even with an expired frame period — no work carried to a later frame |

## Pixels and measurement (GPU, `FluentGpu.WindowsApp` CLI arms)

| Arm | What |
|---|---|
| `--repaint-identity [outDir]` | `tile-static-identity`, `tile-scroll-identity`, `tile-feather-identity` (+ `/product`), `tile-acrylic-budget-identity`, `stickyclip-identity` (`/group`, `/distributed`), `group-cache-identity` — 0 px differences (feather, `fade-distribute-identity`, `scroll-edge-identity`, `scroll-chrome-identity` ≤ 1/255); 105 checks. A run that fails EVERY scenario from one scale on (even `tile-feather-identity`) is environmental — rerun before debugging |
| `--scroll-bench [page] --dipPerSec N --seconds S [--out dir]` | one constant-velocity plan (`ScrollHandle.AutoScroll`), per-frame UI/record/capture ms + the GPU pass timeline; summary by pass kind |
| `--scroll-soak [page] [--scale x] [--out dir]` | DM-shaped touchpad contacts injected on a 120/20/20/60/20 s timeline; per-second pacing, missed ticks, depth, governor, GC, memory, a CPU thermal canary |
| knockouts `--edge-fades-off --freeze-uploads --force-full-direct --clear-only --group-fades` | A/B arms for bench and soak (`GpuKnockouts`; `--group-fades` = every edge fade as a group surface — the pre-distribution route, an identity/measurement control; `StickyClipInPaint` — probe-only — records a sticky band line into tiles, the identity control for the composite-time clip). The bench's `featherPx=` is the pixels a frame spends in the feather evaluation (bands only) |
| artist-bench A/B arms `--ab-no-shelf-fades --ab-no-magazine-fade --ab-no-stickyclip` | remove ONE feature of the artist page; the bench prints the offscreen split per kind, the slice partition (`effectSlicesMax`, `foldedMax`, `freeFadesMax`, `acrylicSlicesMax`, `acrylicFallbacksMax`), `visibleNeedMaxMiB` vs `budgetMiB` and the group cache |

## The loop

```powershell
dotnet build src/FluentGpu.slnx ; dotnet build src/FluentGpu.slnx -c Release     # both arms (diag const gates differ)
dotnet run --project src/FluentGpu.VerticalSlice -- --suite scroll               # iterate; the FULL suite before claiming done
dotnet run --project src/FluentGpu.VerticalSlice                                 # "ALL CHECKS PASSED"
dotnet test src/FluentGpu.Engine.Tests ; dotnet test src/FluentGpu.Windows.Tests
powershell -File docs\design\check-canon.ps1                                     # after any docs/design edit
```

Add the gate **failing-first**; a feel change also gets a before/after Scroll Lab session (`docs/guide/scroll-lab.md`).
