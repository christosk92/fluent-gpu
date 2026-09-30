# Scroll — where to change what

Layers, and the boundary matters: **Motion** is pure math (no scene, no clock); **Runtime** owns the seam and the
posers; **Hosting** wires them to the frame loop and the render thread; **Input** turns events into handle calls; the
**Windows PAL** produces intent only (it never writes a plan or an offset); **Controls** compose `ScrollHandle`; the
**app** declares effects, keys and handles. A change that needs a new lever usually belongs one layer LOWER than the
surface it shows on — but a mode-specific branch in the engine to serve one app surface is the mistake the split exists
to prevent.

All paths are under `src/`.

## Motion — `FluentGpu.Engine/Scroll/Motion/`

| Task | File |
|---|---|
| A segment's closed form (`Hold`/`Cubic`/`Decay`/`Spring`/`Glide`), `MotionSeg.Eval` | `Motion/MotionSeg.cs` |
| Plan shape, `Eval` segment pick + settled rule, rubber band + `OverpanCapFraction`, `Shifted`, `Dest`, `Idle`; the contact ring (`ContactRing`: LSQ velocity, `ContactClock` Present = hold / Device = bounded Android resampling, `ReportPeriodS`, `TimeShifted`/`PositionShifted`) | `Motion/ScrollPlan.cs` |
| The stopped-finger rule at a lift (`StoppedAfterS`, `ContactStoppedMinS`) and the producer's verdict that overrides it (`FollowEnd(..., ContactRelease)`) | `Motion/PlanAuthor.cs`, `Motion/ContactRelease.cs` |
| Every authoring rule: `WheelNotch` (accumulate / re-base / pose-floor anchor / fractional notches), `WheelSeg` (C1 wheel segment), `AccelFor`, `FollowBegin`/`FollowSample`/`FollowEnd`/`FollowCancel`, `Glide`, `Immediate`, `Thumb`, `Constant`, `Key` | `Motion/PlanAuthor.cs` |
| Snap points on flings / wheel destinations (`SnapGrid`, `SnapTargets.Target`/`ResolveFling`) | `Motion/SnapTargets.cs` |
| Feel constants + the shipped presets (`Standard` default, `Glide`) | `Motion/MotionFeel.cs` |

## Runtime — `FluentGpu.Engine/Scroll/Runtime/`

| Task | File |
|---|---|
| The UI→render plan seam (seqlock slots, capacity 64, `Shift` + the per-slot cumulative `FrameShift`/`FrameShiftOf`, `TryRead(vp, out plan, out frameShift)`, `OnWritten` wake) | `Runtime/PlanSlots.cs` |
| Render/UI posing: evaluation at present time in the coverage's own frame (the drift rule), the coverage clamp, the content translate, effect evaluation at the snapped position, transform folding, feedback | `Runtime/ScrollPoser.cs` |
| Coverage rows (incl. `FrameShift`) + effect rows the UI publishes | `Runtime/ScrollCoverage.cs` |
| The app-facing handle: signals, `ScrollTo`/`ScrollBy`/`BringIntoView`/`ScrollMove.Follow`, `Restore` latch, input verbs, pose-floor `AnchorAt`, per-clock contact placement (`PlaceContactTime`: Present stamps taken as-is, Device stamps resynced), `SettleIfDue` | `Runtime/ScrollHandle.cs` |
| Wheel packet classification (detented / hi-res / touchpad), carryover, pinch synthesis, `HiResNotch`, `TouchpadWheelSample`; `ScrollInputEvent`/`ScrollSource`/`ScrollGesture` (incl. `ArrivalQpc` — latency is measured from it) | `Runtime/ScrollInput.cs` |
| The stamp of a composition-timed contact event (`ContactStamp.ForFrame` = the next tick's present) — used by the DM AND the headless producer | `Runtime/ContactStamp.cs` |
| Which scroller gets an input: latch, chain-from-edge, Shift+wheel horizontal, key mapping | `Runtime/ScrollRouter.cs` |
| Realize window (velocity overscan, anchor row), arrange origin, THE extent write `ApplyMeasured` | `Runtime/Virtualizer.cs` |
| Content transform composition (translate + pinch zoom), `NeedsRealize` (visible band / lead band / trim), snap grid from `ScrollState` | `Runtime/ScrollContentPose.cs` |
| `UseScroll` observation record + `Progress` | `Runtime/ScrollObservation.cs` |
| Node-level `BringIntoView` over a live scene | `Runtime/SceneScrollExtensions.cs` |
| `ScrollKey` → offset memory (LRU 256) | `Runtime/ScrollPositionMemory.cs` |
| Conscious scrollbar chrome FSM (fade/expand/dwell — presentation only, never motion) | `Runtime/ScrollBarChrome.cs` |

## Extent — `FluentGpu.Engine/Scroll/Extent/`

| Task | File |
|---|---|
| The seam | `Extent/IExtentSource.cs` |
| Fixed stride (+ leading/trailing pad) | `Extent/FixedExtent.cs` |
| Variable heights (double Fenwick, estimate-then-correct, `SetEstimate`) | `Extent/MeasuredExtent.cs` |
| Grids / shelves / grouped over `IVirtualLayout` (`RowStart`/`RowEnd`) | `Extent/VirtualLayoutExtent.cs` |
| Where measurements are committed during layout (`ArrangeVirtual`: measure → `ApplyMeasured` → arrange relative to `WindowOrigin` → publish coverage → `NoteFrameShift`) | `FluentGpu.Engine/Layout/FlexLayout.cs` |
| Where the realized window is built (arrange origin chosen at realize) | `FluentGpu.Engine/Reconciler/Reconciler.cs` (`Virtualizer.ArrangeOriginIndex` call) |
| The `ScrollState` column (Offset/Velocity/Motion results, WindowOrigin/Cover*, snap, edge cues, zoom) | `FluentGpu.Engine/Scene/Columns.cs` |

## Effects — `FluentGpu.Engine/Scroll/Effects/`

| Task | File |
|---|---|
| Channels, kinds, `CollapseAnchor`, the `ScrollEffect` record + factories | `Effects/ScrollEffect.cs` |
| The formulas, `IsTransformChannel`, `StickyEngaged`, **the one snap** `SnapToDevicePixel` | `Effects/ScrollEffectEval.cs` |
| Folding a node's transform rows into one matrix (stretch pivot) | `Effects/EffectTransform.cs` |
| Captured geometry | `Effects/EffectGeometry.cs` |
| Authoring spec + DSL (`Sticky`, `StickyClip`, `Collapse`, `StretchFromTop`, `Parallax`, `ParallaxY`, `Fade`, `Reveal`, `OnScroll`) | `Effects/ScrollEffectSpec.cs` |
| `Element.ScrollEffects` / `Element.ScrollScope` / `Element.WheelTarget`; `ScrollEl.ScrollKey`/`Handle`/`Snap` | `FluentGpu.Engine/Dsl/Element.cs` |
| `VirtualListEl.MeasureAll`/`ScrollKey`/`Handle` | `FluentGpu.Engine/Reconciler/VirtualListEl.cs` |
| Flat, cheap rows (`ListRowEl`, `RowCell`, ≤ 8 cells) | `FluentGpu.Engine/Dsl/ListRowEl.cs` |
| `UseScroll`/`UseScrollProgress` surface; `ScrollCtx.Nearest` | `FluentGpu.Engine/Hooks/Component.cs` (+ `RenderContext.cs`), `Hooks/ScrollHooks.cs` |

## Hosting — `FluentGpu.Engine/Hosting/`

| Task | File |
|---|---|
| Handle binding per scroll node, the plan clock, the pose floor, `RunScrollFrame`, `SyncScrollPlansMidFrame`, `FillScrollCoverage` (sticky engaged edges written here), `PoseScrollUi`, `ApplyEffectChannel`/`ApplyEffectTransform`, `RenderPresentSec`, `TickRenderScroll` | `AppHost.Scroll.cs` |
| Render-thread pose sink over the snapshot's compositor overlay (`RecordRequired` for non-transform channels) | `SnapshotScrollPoseSink.cs` |
| Present decision per tick, missed ticks, `[render.pace]`, the per-present `ScrollProbe.Turn` row | `Threading/RenderThread.cs` |
| The pure present verdict | `Threading/PresentCadence.cs` |
| Paced-turn catch-up: grace on the present-slot take, skip while frames fit, back off when it does not hold (`[render.pace] catchUp= costEma= backoff=`) | `Threading/SlotCatchUp.cs` (wired in `RenderThread.PresentTurn`; the seam `IGpuDevice.TryTakePresentSlot` in `Seams/Rhi/Rhi.cs`, D3D12 in `FluentGpu.Windows/D3D12/D3D12Device.cs`) |
| Present-queue depth 1 ↔ 2 from measured GPU margin | `Threading/PresentQueueDepthPolicy.cs` |
| DXGI paired-counter present ledger | `Threading/PresentStatisticsLedger.cs` |
| Missed-motion-tick accounting | `Threading/MotionTickRun.cs` |
| Frame-clock present prediction for the UI frame | `RefreshLattice.cs` |
| Governor may-pace rule (scroll never paced) | `GpuGovernorWake.cs` |
| `--fg` launch switches | `EngineSwitches.cs` |
| Render census (always-on counters) + per-component census toggle | `RenderCensus.cs`, `AppHost.cs` (`RenderCensus`, `LastTileCensus`, `GpuPassTimingEnabled`, `CopyGpuPassTimeline`) |

## Input — `FluentGpu.Engine/Input/`

| Task | File |
|---|---|
| The one scroll entry `DispatchScroll`; wheel order (element wheel → ZoomWheel → WheelTarget → router → handle); unrouted contact open + axis lock + mid-stream chain; touch/pen pan; `ScrollStop`; thumb; keys; `AutoScroll`; pinch `SetZoom`; `IScrollerQuery.CanMove` | `InputDispatcher.Scroll.cs` |
| Scrollbar thumb drag mapping (calls `ThumbSet`) | `InputDispatcher.cs` |
| `InputEvent.ForScroll`, `IPlatformWindow.PumpScroll`/`SetScrollInputSink`/`ScrollProducerLive` | `FluentGpu.Engine/Seams/Pal/Pal.cs` |
| Headless injection (`SendScroll`, `SendWheelNotch`) | `FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs` |

## Windows PAL — `FluentGpu.Windows/Pal/`

| Task | File |
|---|---|
| `WM_POINTERWHEEL`/`HWHEEL` → classifier → detented notch / hi-res fractional notch / touchpad contact stream; silence lift (adaptive 50–120 ms, `LiftTimerId`); `WheelStampQpc`; wheel-lines scaling | `Win32Platform.cs` (`HandlePointerWheel`, `TryEmitFallbackLift`, `EndFallbackContact`, `AdaptiveLiftMs`) |
| DirectManipulation contact producer: engage timeout, one Update per produced frame with the composition hint (`PumpOnce`, the frame-info CCW), `ContactStamp` stamps (the next tick's present; staleness against the pump's wall time) + `ArrivalQpc` + `PresentTimed`, physical-mouse pre-emption, `TRANSLATION_INERTIA` + stop-at-next-pump | `Win32DirectManipulation.cs` |
| WHICH DM callbacks are motion (buffer per Update, deliver iff still RUNNING), Begin/End, the release verdict (INERTIA = moving, READY = stopped) — pure, unit-tested | `DmContactStream.cs` |
| Compositor tick filter (double ticks, synthesized ticks, lattice snap/resync, decimation) | `CompositorTickFilter.cs` |
| The waiter thread + render display clock | `Win32CompositorClock.cs` |

## Rendering — link only (owner `gpu-renderer.md` §13.1)

Slices/tiles/composite: `FluentGpu.Engine/Render/{SceneRecorder,SliceRecorder}.cs`, `Render/Tiles/*`
(`SliceTable`, `TileGrid` + `InvalidationReason`, `TileBudget`, `EdgeFeatherMask`, `SliceOpBounds`, `TileCensus`),
`Seams/Rhi/{Composite,UploadBudget,GpuFrameTelemetry}.cs`, `FluentGpu.Windows/D3D12/{D3D12Device.Composite,SurfacePool,
SliceCompositor,TileRasterizer,UploadQueue}.cs` + `composite.hlsl`. The thumb slice pose: `SliceRecorder.OwnDelta`
(`PoseKind.Thumb`). Don't touch these for a scroll-motion bug; touch them for a raster/composite bug.

## Diagnostics — `FluentGpu.Engine/Scroll/Diag/`

| Task | File |
|---|---|
| Levels | `Diag/ProbeLevel.cs` |
| Rings, record methods, `EndBurst`, `ScrollSourceCode`/`ScrollCostPhase`/`ProbeMark` | `Diag/ScrollProbe.cs` |
| `Present` (UI) + `Turn` (render) rows | `Diag/ScrollProbe.Present.cs` |
| Public `ProbeRow`/`ProbeRowKind`, `ReadUi`/`ReadRender` drains | `Diag/ScrollProbe.Rows.cs` |
| CSV (analyze.py format + lab kinds) | `Diag/ScrollProbeCsv.cs` |
| `BurstSummary` + `ScrollVerdict` | `Diag/BurstSummary.cs` |
| Live feel registry + slider metadata + JSON | `Diag/ScrollTunables.cs` |
| Session series + the metric catalog | `Diag/Analysis/{SessionSeries,ScrollMetrics}.cs` |

## Controls — `FluentGpu.Controls/`

| Task | File |
|---|---|
| Virtualized list over a handle: `ScrollOptions.Handle`/`ScrollKey`, `ShiftFrame` on structural change, `WheelNow` forwarding, `AutoScroll` for drag edges, `ScrollTo` for index targets | `ItemsView.cs`, `ListOptions.cs` (`ScrollOptions`, `ListOptions.MeasureAll`) |
| Grids, shelves | `LazyGrid.cs`, `PagedShelf.cs` |
| Scrollbars / annotated rail | `ScrollBar.cs`, `AnnotatedScrollBar.cs` |
| Two-way controller seam (`controls.md` §13) | `IScrollController.cs` |

## Scroll Lab — `FluentGpu.ScrollLab/` + `FluentGpu.ScrollLab.Surfaces/`

| Task | File |
|---|---|
| Entry, window options (governor off, no warm cadence) | `ScrollLab/Program.cs` |
| Engine attach, per-frame drain, HUD, GPU passes, session meta | `ScrollLab/Lab/LabHost.cs` |
| All lab state + actions (record, mark, apply feel, tuning window) | `ScrollLab/Lab/LabState.cs` |
| Shell, pages, HUD | `ScrollLab/Lab/LabShell.cs` |
| Hotkeys (F8, F10, Ctrl+T, Ctrl+Shift+T) | `ScrollLab/Lab/Hotkeys.cs` |
| Recorder, folder writer/loader, frame log, Record page | `ScrollLab/Record/*` |
| Tuning panel + presets store | `ScrollLab/Tuning/*` |
| Analysis page + result | `ScrollLab/Analysis/*` |
| Surfaces (Fixed 100k, Measured, Edge cases, position barcode, row builders) | `ScrollLab.Surfaces/*` |

## Gallery probes — `FluentGpu.WindowsApp/Probes/`

`ScrollPerfProbes.cs` (`--scroll-bench`, `--scroll-soak`), `RepaintIdentityProbe.cs` (`--repaint-identity`),
`DialogScrollProbe.cs` (`--dialog-scroll-probe`), `SoakProbe.cs` (`--soak`/`--stress-*`, general longevity).

## Tests

VerticalSlice: `FluentGpu.VerticalSlice/Suites/{ScrollMotionSuite,ScrollEffectsSuite,ScrollSuite,TileSuite,SliceSuite,
ListRowSuite,TouchSuite}.cs`; the headless producer `HeadlessScrollProducer` is in `Probes/Probes.cs`. Unit tests:
`FluentGpu.Engine.Tests/{ScrollMotionTests,ScrollRuntimeTests,ExtentSourceTests,ScrollEffectTests,ScrollLinkedEffectTests,
ScrollProbeTests,ScrollMetricsTests,ScrollParkedViewportTests,ScrollPoseDamageTests,ContactStampTests,
TouchpadStampRaceTests,RenderThreadLifecycleTests,RenderThreadPacingTests,SlotCatchUpTests,PresentQueueDepthPolicyTests,PresentStatisticsLedgerTests,RefreshLatticeTests,GpuGovernorWakeTests,CadencePacingTests}.cs`,
`FluentGpu.Windows.Tests/{CompositorTickFilterTests,RenderDisplayClockTests,DmContactStreamTests}.cs`, `FluentGpu.Engine.Tests/TouchpadReleaseDispatchTests.cs`. Map: [gates.md](gates.md).
