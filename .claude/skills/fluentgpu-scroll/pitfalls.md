# Scroll pitfalls — mechanisms and rules

Each entry: the **mechanism** (why it goes wrong) and the **rule**. Most of these were paid for by the old integrating
kernel and its band-aids (`scroll.md` §14) or by the rework's own fix rounds.

---

## 1. Evaluating at "now" instead of at present time

**Mechanism.** A frame produced now is shown one to three refreshes later (`presentSec = tick + (1 + depth)·refresh`).
A pose evaluated at "now" is stale by that lead on every frame; while the lead varies (depth 1 ↔ 2, a late tick) the
staleness varies, which reads as judder even at a perfect cadence.

**Rule.** Poses, virtualization windows and effect values are evaluated at the frame's PRESENT time: the render poser at
`AppHost.RenderPresentSec(tick)`, the UI frame step at the `RefreshLattice` present. `ScrollHandle.OffsetNow` is for
input-time decisions (what the user grabbed), never for pixels.

## 2. Re-snapping (or never snapping) an effect

**Mechanism.** The content translate is snapped to the device grid once. An effect evaluated at the UNSNAPPED
position, or snapped a second time by its sink, lands up to half a device pixel off the rows it rides — a sticky header
shimmers against its rows at 125 %/175 %.

**Rule.** Effects evaluate at `pSnapped = WindowOrigin − trans` (the poser does this); `ScrollEffectEval.SnapToDevicePixel`
is the ONLY snap; sinks write the value as given. Gate: `gate.scroll.sticky-grid`, `Poser_StickyEffect_SitsOnContentPixelGrid_BitExact`.

## 3. Fighting the coverage clamp

**Mechanism.** A clamp means the plan asked for content the UI had not realized. "Fixing" it by removing the clamp shows
a blank band; "fixing" it by holding the plan back (a speed cap, a hold until realized) changes the feel.

**Rule.** A clamp is a VIRTUALIZATION defect: realize sooner (velocity overscan, `NeedsRealize`) or realize cheaper
(`ListRowEl`, fewer nodes per row). The clamp stays. Watch `TileCensus.CoverageClamps`/`ExposedMissing` (must be 0) and
the probe's clamped poses.

## 4. Double prediction

**Mechanism.** Two stages each compensate for the same latency: a DM sample stamped at the present time AND a ring that
extrapolates past it by a refresh; a composition-delta hint to DirectManipulation AND an engine look-ahead; an app that
adds its own lead to `Offset`. The content runs ahead of the finger, then snaps back when the next real sample lands —
"slippery", overshoot on stop, reversal jitter.

**Rule.** Predict ONCE. The plan is evaluated at present time. A composition-timed stream (DirectManipulation:
`ScrollInputEvent.PresentTimed` → `ContactClock.Present`) was already predicted by the OS, so its ring HOLDS the newest
sample (and each sample is stamped with the present that first shows it — see 22); a device-timed stream (`ContactClock.Device`: touch, pen, the touchpad wheel fallback) gets Android's bounded
resampling only (≤ `min(8 ms, gap/2)`, gap within 2–20 ms). The LSQ velocity never moves the shown position. A new
producer that already composes for the present must set `PresentTimed`; an app never predicts scroll.

## 5. Comparing the present ledger per sample (or against the submit count)

**Mechanism.** A DXGI frame-statistics sample describes the last DISPLAYED present and lags the submit by about a flip.
Comparing it with the engine's own submitted count, or diffing across an idle stretch, invents "drops" and "repeats".

**Rule.** Difference only the paired counters (`PresentCount` with `PresentRefreshCount`) between two samples
(`PresentStatisticsLedger`); credit idle vblanks per present id; a probe `Present` row is CUMULATIVE — difference
consecutive rows, and a delta that spans idle is not a motion sample. Producer-side misses are
`RenderThread.MissedMotionTicks` / `Turn.MissedTicks`, a different number. Gates: `PresentStatisticsLedgerTests.*`,
`ScrollMetricsTests.LedgerEventsCountOnlyFromThePairedCounterLedger`.

## 6. Re-planning from the input's own time

**Mechanism.** The render poser runs ahead of the clock by the present lead, so a notch stamped at `t` arrives after
frames for later presents have ALREADY been posed from the old plan. A curve anchored at `t` catches up several frames of
travel at once (a step) or, on a reversal, steps back past frames the user saw.

**Rule.** Every re-plan that continues the live plan anchors at `max(t, pose floor)` — `ScrollHandle.AnchorAt`,
`PlanAuthor.WheelNotch(shownFloorSec)`. Grabbing moving content (`Stop`, `ContactBeginHere`) holds the SHOWN position
(`DisplayedAt`), not `plan.Eval(t)`. Gates: `WheelNotch_StampedBeforeTheLastPosedPresent_*`, `Handle_Stop_*`,
`Handle_ContactBeginHere_*`.

## 7. Two writers to the offset

**Mechanism.** Any code that moves the offset outside the plan (a layout pass nudging `ScrollState.Offset`, an app writing
a transform on the content node, a control calling `ScrollTo` to "correct" a measured shift) races the poser, which
re-poses from the plan on the next tick — the content jumps back and forth.

**Rule.** Offsets move ONLY through a plan: `ScrollHandle` authors, `Virtualizer.ApplyMeasured` shifts the frame for
measurement, `ScrollHandle.ShiftFrame` for structural changes above the first visible row. A frame shift is not a move:
never express it as `ScrollTo(offset + delta)` (that interrupts the live arc and starts a glide).

## 8. Plan shift and coverage out of step

**Mechanism.** `ApplyMeasured` shifts the plan in `PlanSlots` during UI layout — the render thread sees it on its very
next tick — while the matching `WindowOrigin` / coverage only arrives with the next publication. For that window the
poser evaluates a shifted plan against the old window origin: a one-frame jump of exactly the correction.

**Rule.** Every plan is posed in the frame of the coverage it is posed against: `PlanSlots` keeps each slot's cumulative
`FrameShift` (read with the plan under one seqlock), the coverage row records the shift it was laid out under, and
`ScrollPoser.Tick` subtracts the drift (`frameShift − row.FrameShift`) — scroll.md §5.2. Any new consumer that evaluates
a plan against published coverage must do the same; any new way to move a plan's frame must go through
`PlanSlots.Shift` so the drift is counted. A single-frame jump equal to a measured delta means one of these was bypassed.
Gate: `Poser_PlanShiftNewerThanItsCoverage_PosesInTheCoveragesFrame_UntilTheNextAdopt`.

## 9. `dt` creeping back in

**Mechanism.** "Just integrate the velocity for this one feature" reintroduces state that a skipped, repeated or late
frame corrupts — exactly what dt repair was invented to patch.

**Rule.** If motion needs a new shape, add a closed-form `SegKind` (or compose existing ones) and author it in
`PlanAuthor`. Nothing in `Scroll/Motion` may hold per-tick state. Gate: `Eval_AtSharedTimes_IsIdenticalRegardlessOfSampleOrder`.

## 10. Reading a feel constant at evaluation time

**Mechanism.** A poser or `Eval` that reads `ScrollTunables.Current` changes an in-flight plan when a slider moves: the
curve stops being a function of its authored inputs, and the UI and render posers can disagree for a tick.

**Rule.** Feel is read at AUTHOR time and baked into the plan (`RubberC` is the precedent).

## 11. Coincident packet pairs as velocity

**Mechanism.** Hi-res and touchpad packets often arrive in pairs a fraction of a millisecond apart; a pairwise slope is
20k–130k DIP/s of arrival jitter. Using it for prediction or release flings the list to the end.

**Rule.** Velocity is the ring's least-squares slope over `FlingImpulseWindowS`, and 0 unless the horizon spans at least
`VelocityMinSpanS`; resampling ignores a newest pair closer than 2 ms. A lift more than `PlanAuthor.StoppedAfterS` (two
of the stream's own report periods, 20 ms floor) after the newest sample carries no momentum — for a stream WITHOUT a
producer verdict (`ContactRelease.Unknown`); a DirectManipulation End carries DM's verdict instead (see 20). Never
synthesize a zero-delta "the finger is resting" sample to express a stop — it reads as a velocity collapse; the verdict or
the time rule at the lift is the stop. Gates: `Follow_CoincidentPacketPair_*`, `FollowEnd_FingerStoppedBeforeTheLift_NeverFlings`.

## 12. Props freeze at mount

**Mechanism.** `Embed.Comp(() => new MyList { Handle = h, Offset = o })` runs once; a later parent render's new values
are discarded. A scroller whose handle, `ScrollKey` or options live in a propless factory never sees the new ones.

**Rule.** Create the `ScrollHandle` once per component (`readonly ScrollHandle _h = new()` or `UseMemo`), drive it by
calls; pass changing data by signal/`[Props]`/context; remount with a `Key` only when the content identity changes (a new
`ScrollKey` then restores its own offset). `ReuseGuard` catches the propless-field case.

## 13. A render that reads `Offset`

**Mechanism.** `UseScroll().Offset.Value` in `Render()` re-renders the component on every moved frame — a full
reconcile per frame of scrolling.

**Rule.** Bind it (`Transform = Prop.Of(() => …Offset.Value…)`), memo it coarsely (`UseScrollProgress`, a derived bool),
or better, declare a `ScrollEffect` row so the render thread poses it with no UI work at all.

## 14. Making the engine wait for the UI (or vice versa)

**Mechanism.** A render turn that waits for the UI's in-flight frame holds every motion present hostage to the slowest UI
frame; a UI that waits for a present ack before producing adds a frame of latency.

**Rule.** One present per tick; a fresh publication wins if it is there, otherwise the retained scene is re-posed. The
coverage clamp is what makes this safe. Gates: `RenderThreadLifecycleTests.*`.

## 15. Tuning by adjective

**Mechanism.** "Feels heavy → shorten the duration" changes a measured curve to chase a symptom that may be latency,
dropped vblanks or a double prediction. Each such constant fixes one complaint and creates another.

**Rule.** Record a lab session, read which METRIC is red, fix that mechanism. A feel change is a `FeelProfiles` data change
justified by a measured reference.

## 16. Recording a scroll-driven value into tiles

**Mechanism.** A pose channel that becomes a record-time value (a `ClipRect`, a painted alpha) re-records its node on
every scroll frame: the tiles re-raster, the groups over them miss their content keys, the backdrops that read them
re-blur. The magazine's `.StickyClip` band line did exactly that (4.6 tiles rastered a turn on the artist page).

**Rule.** A value that changes with the offset is a COMPOSITE parameter of a slice (the band line rides the
`StickyClip` marker; the thumb is a posed slice; a fade is a feather) — never bytes. Gates:
`gate.slices.stickyclip-composite`, `gate.slices.scroll-tick-zero-bytes`.

## 17. Painting an edge cue, or feathering the chrome

**Mechanism.** A painted cue has to guess what is behind the edge (the old gradient took an ancestor's colour — a dark
slab over a translucent card, a hard first row at a fractional edge). And chrome drawn UNDER the feather dissolves at
the very edges it serves, while a visible thumb overlapping the content inside the band stops the fade distributing, so
the viewport re-renders as a group every frame (virtualization: +0.6 ms).

**Rule.** The edge cue IS the analytic feather (`ScrollEdgeCues.Fade` → `AutoEdgeFade`); the scrollbar and chevrons sit
OVER it (`Chrome` / `Thumb` slices placed outside it). Never paint a cue; never put chrome inside a fade. Gates:
`gate.slices.edge-cue-is-feather`, `gate.slices.chrome-over-feather`, `scroll-edge-identity`, `scroll-chrome-identity`.

## 18. Time-slicing the reactive flush

**Mechanism.** A flush that yields at a deadline leaves the UI a frame (or more) behind its own signals — a keep-alive
park or a migration sweep lands late, and harnesses start needing "run until quiescent" helpers to see the truth.

**Rule.** `FlushHosted` drains to quiescence every frame; a slow unit is reported (`[signals.slow-unit]`, always-on) and
fixed at its source. Gates: `gate.signals.frame-reaches-quiescence`, `gate.signals.keepalive-parks-in-one-frame`.

## 19. Deciding a retained tile is valid from damage rects

**Mechanism.** A damage rect is a guess at which pixels a change touched: a dirty node's old ∪ new extent in the slot it
was walked in. It misses every byte change that is not a dirty node's own extent — a nested slice re-walked on a
signature miss with an inherited alpha baked in (the artist band's tab row: tabs at full alpha over the hero at rest,
invisible when pinned), the scrollbar thumb's fill alpha (the damage went to the enclosing slot), an op's cull halo
(glyphs ±18 px) reaching a tile the node's model rect does not — and it over-invalidates wherever a rect crosses a tile
whose ops did not change.

**Rule.** A tile is valid iff the content hash of the ops its replay draws equals the hash it was rastered for
(`SliceTable.Request<TContent>`, gpu-renderer.md §13.1c). Never add a per-node damage rect "so the tile re-rasters":
change the bytes and the want moves by itself. Only what no byte describes gets an explicit invalidation (the image
clock, scale, grid, theme / forced-full, eviction, coverage). Gates: `gate.slices.inherited-opacity-rerasters`, the
`gate.tiles.stale-zero` sweep in every suite, `SliceTableContentTests`.

## 20. DM's READY snap is not motion

**Mechanism.** DirectManipulation stops producing content at the physical lift and reports the status edge 1–5 frames
later. Inside that last Update it raises `OnContentUpdated` with its whole-pixel snap (a sub-pixel delta) BEFORE
`OnViewportStatusChanged` — so the callback still sees RUNNING. Emitted as a `Sample`, the snap sat in the ring at the End
stamp: the 40 ms LSQ read ~0 over the stall and `StoppedAfterS` read the latency as a pause — 9 of 22 fast flicks held
dead (WinUI flung 14 of 14), and back-to-back flicks "caught, then held".

**Rule.** An Update that ends in a non-RUNNING status carries no motion: `DmContactStream` buffers content and delivers it
when the Update returns, only if still RUNNING (ordering, never a delta-size threshold). DM decides moving vs stopped
(`TRANSLATION_INERTIA` configured: RUNNING→INERTIA = `ContactRelease.Moving`, RUNNING→READY = `Stopped`; the INERTIA
viewport is stopped at the next pump — DM never owns the coast). A Present-clock End with a verdict is authored with
`tLift` = the newest REAL sample and `tNow` = the End stamp (`ScrollHandle.ContactEnd` → `FollowEnd`'s late-lift path).
Gates: `DmContactStreamTests.*` (Windows.Tests, real per-Update scripts), `FollowEnd_PresentClockMovingRelease_*`,
`FollowEnd_StoppedRelease_*`, `TouchpadReleaseDispatchTests.*`. Open item: release MAGNITUDE — the ring can read
50–100k DIP/s on a rapid flick (DM pan gain), 20–40k DIP coasts against WinUI's ~10.8k DIP/s plateau; not capped yet.

## 21. A reseed is an extent change

**Mechanism.** Rewriting a measured layout's extents out of band (`MeasuredStackVirtualLayout.Reseed` on a wholesale
republish) moves every row below the first rewritten one with NO plan shift; the next layout re-measures the rows whose
seed was wrong and shifts the plan by the correction. Net: the plan moved by the whole seed error while the content did
not — the Library V3 rail jumped +186 DIP on every selection (a 101-DIP head seed against a 287-DIP head).

**Rule.** Every extent change above the anchor is anchored in the call that makes it visible: measured corrections
(`ApplyMeasured`), structural changes (`ShiftFrame`) and reseeds (`IAnchoredReseedLayout.TakeReseedShift`, taken by
`RealizeWindow` / `ArrangeVirtual` pass 0). A new way to rewrite extents must do the same. And seed what you can exactly:
an analytic seed that matches the measurement never needs a correction. Evidence: the always-on `[scroll.jump]` line
(`ScrollJumpRules`). Gate: `gate.scroll.reseed-anchored`.

## 22. Stamping a composition-timed sample for the tick that reads it

**Mechanism.** DM samples are produced on the UI thread once per frame; the render thread reads `PlanSlots` for the same
tick. The UI write lands within ±0.5 ms of that read, so a sample stamped `now + lead` (≈ the present of the tick that
reads it) is shown or not by whichever thread wins: most frames show the previous tick's sample, some show this tick's —
+2/0 sample steps on ~14 % of fast drag frames (2026-09-29 RCA, 8 DIP at 1000 DIP/s), while DM's own per-tick output
was regular.

**Rule.** Never stamp a composition-timed sample for the tick that reads it — the UI write races the render read. Stamp
the present of the first render turn GUARANTEED to see it: `ContactStamp.ForFrame(clock, now)` = `PresentQpc +
RefreshQpc` (the next tick's present). Tick *k* then poses at the stamp of sample *k−1*, interpolates between real samples
and holds past the newest only when the UI stalls — a function of time, never of thread order (Gecko APZ's one-frame
delay; Flutter's resampler). Any new present-timed producer (and the headless one) goes through `ContactStamp`; the DM
composition hint (`CompositionDeltaMs`) is DM's latency compensation, a separate knob. Measure latency from
`ScrollInputEvent.ArrivalQpc`, never from a present stamp. Gates: `ContactStampTests.*`,
`TouchpadStampRaceTests.RenderTick_ShowsTheSameSample_WhetherThisTicksSampleLandedBeforeOrAfterItsRead`,
`gate.touchpad.stamp-race`.

## 23. Queueing behind a late present (or skipping every busy slot)

**Mechanism.** A present that misses its vblank is retired only at the NEXT vblank (+0.2–0.7 ms), so a paced turn that
waits unbounded for the slot presents one vblank late — and every later turn inherits that phase (runs of 55–86 turns
one tick behind with 1–3 ms frames, 2026-09-29). Skipping unconditionally is the opposite mistake: a GPU costing slightly
more than a refresh would skip every other tick (the half-rate cliff `RenderThreadPacingTests` guards).

**Rule.** A clock-paced turn takes the credit with `TryTakePresentSlot(SlotCatchUp.GraceMs(refresh))`; busy past the
grace ⇒ present nothing while frames fit `SlotCatchUp.FitFraction` of the refresh (the queued frame owns this vblank; the
next tick presents on time), otherwise take the unbounded wait and let `PresentQueueDepthPolicy` own the over-budget
regime; a catch-up that does not hold backs off. Never add a second wait or a skip outside `SlotCatchUp`. Gates:
`SlotCatchUpTests.*`, `RenderThreadPacingTests.*`,
`RenderThreadLifecycleTests.APacedTurnWhoseSlotStaysBusy_PresentsNothing_AndTheNextTickPresents`.
