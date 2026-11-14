# Methodology

## Baselines

- FluentGPU: current `src/FluentGpu.Engine`, `Controls`, and Windows backend only. The runner records the commit and a
  SHA-256 of an archived binary patch so dirty engine work is reproducible.
- WinUI 3: the pinned public, production `Microsoft.WindowsAppSDK` NuGet package. A result is publishable only when
  the evidence manifest records its package version and the published `Microsoft.UI.Xaml.dll` hash. This intentionally
  replaces the unreleasable local source snapshot, whose pinned FrameworkUdk package lacks a required public header.
- Both hosts: Windows desktop, 1200 x 720 DIP client area (window-resize: a trajectory around it), .NET 11, ARM64, self-contained NativeAOT, opaque window, no
  Wavee code, network, images, audio, or application services.

## Workloads

| Scenario | Fixed workload | Primary measurements |
|---|---|---|
| Startup | one centered text node; 30 fresh processes | process start to first-result marker, first-present ETW, working set |
| 225 buttons | 15 x 15 stock buttons; 10 fresh processes | cold construction/first presentation, CPU, memory |
| 1,125 text nodes | 25 x 45 fixed-position text runs; 10 fresh processes | cold construction/first presentation, shaping CPU, memory |
| Virtual scroll (1k) | 1,000 rows; each has one rectangle and four text runs; move five rows/operation, reset to row 0 every 100 | CPU p50/p99, presented cadence, allocation/op, realized-set memory |
| Virtual scroll (10k) | identical in every respect except 10,000 rows | same, plus large-list robustness |
| Localized update | one leaf in a 1,000-node fixed tree | transform-only and text/layout subcases, CPU p50/p99, allocation/op |
| Tree churn | alternate two prebuilt 500-node subtrees for 1,000 presented swaps | CPU p50/p99, cadence, allocation/op, memory stability |
| Page navigation | navigate between two structurally different pages, each **built from scratch** per navigation: page A = hero (34 px title, 14 px subtitle, 300x160 accent box) + 6 x 4 grid of 24 cards (rounded container, 96x96 placeholder, 2 text runs) + 40-row three-column list; page B = two-line header + 5 x 8 grid of 40 tiles + 20-row two-column list | CPU p50/p99 (page construction + layout + render), cadence, allocation/op, memory stability |
| Window resize | page A laid out responsively; `SetWindowPos` one corner-drag step per composed frame, 960-1440 x 600-840 DIP, 1,000 steps | share of composed frames whose content matches the window, content lag, resize-to-content latency (desktop duplication) |
| Theme switch | page A, theme-aware on both sides; 200 alternating Light/Dark switches | switch to first fully re-themed composed frame, mixed (half-themed) frames (desktop duplication) |

The FluentGPU scrolling host uses its signals-first bound recycler; WinUI uses a virtualizing `ListView` with a compiled
data template. The static fields in a row are static on both sides; only the row label is data-bound.

`virtual-scroll-1k` and `virtual-scroll-10k` are one scenario at two sizes: same row height (44 px), same row content,
the same five-rows-per-operation `ScrollBy`/`ChangeView` mutation, and the same reset to row 0 every 100 iterations, so
a cycle tops out at row 495 and neither size pins against the end of its list. The row count is the single parameter
(`BenchWorkload.RowsFor`). The 1k size is the like-for-like comparison point and 10k is the large-list gauge; both
frameworks complete both sizes.

### Page navigation: the compound workload class, not another single mutation

The single-mutation scenarios above converge: once a framework can turn one property change into one frame, the frame
rate is the display's, not the framework's, and the interesting differences are pushed into the tails. Where the two
frameworks actually diverge is compound work — the 225-button content build costs WinUI ~91 ms of workload delta against
FluentGPU's ~0.5 ms — and the compound workload a user meets constantly is **navigation**: tearing down one page and
building the next.

`page-navigation` measures exactly that, and only that. Iteration `i` navigates to the detail page when `(i & 1) == 0`
and to the library page otherwise, so every measured iteration is a navigation between two *structurally different*
destinations rather than a property diff of the page already up.

- **The destination tree is constructed inside the measured section on both hosts.** This is deliberately *not*
  `tree-churn`, which alternates two subtrees built once at startup and therefore measures the reconciler's diff of a
  known-shaped swap. Here the element/`UIElement` tree — every container, every text run, every string — is built per
  navigation, the way a real application's navigation builds the page it is navigating to.
- **Nothing is cached, on either side.** The WinUI host does not use a `Frame`: a `Frame` brings a page cache
  (`NavigationCacheMode`) and a `NavigationTransitionInfo` animation, and neither belongs in a measurement of page
  construction. It builds the page as a `UIElement` tree in code and swaps it into a `ContentControl` — the same
  technique its other scenarios use, and strictly stronger than `NavigationCacheMode.Disabled`. `ContentTransitions` is
  explicitly null; the FluentGPU host has no transition either.
- **Every string is stamped with the iteration** (`Card 07 - i0421`, `Tile 23 i0421`, `Track 15 i0421`), so no
  navigation can be served out of a text/shaping cache that a previous navigation populated.
- **The FluentGPU host drives the page identity through a signal read inside the component's `Render`**, per the
  component-props-freeze contract (`docs/design/subsystems/component-props-contract.md`): an iteration passed as a field
  would freeze at mount and no navigation would ever happen.
- Every count, size and string format is a `BenchWorkload.Nav*` constant that both hosts consume — there are no magic
  numbers in either host — and both pages' explicit geometry adds up to the shared 1200 x 720 DIP client area (checked in
  the parity screenshots; the `Bench.Tests` assertions were deleted with every test project on 2026-10-20), so neither
  framework's text metrics can push content out of the viewport.

The one structural asymmetry is unavoidable and is a framework property, not a harness choice: a WinUI `Border` cannot
lay out a stack of children, so a card is `Border` + `StackPanel` where FluentGPU's is a single `BoxEl`. Both hosts
still produce one card with one thumbnail and two text runs, at the same position and the same size.

The usual pass caveat applies unchanged: on `cpu`, FluentGPU times the full frame and WinUI times mutation +
synchronous `UpdateLayout`, so the CPU-work row carries the standing definition-asymmetry footnote; the `cadence` pass
is what gives the honest cross-framework frame time.

The **localized update / transform** subcase moves one leaf through each framework's own property system: FluentGPU sets
the `Transform` prop (`Affine2D.Translation`) and lets its dirty tracking propagate; WinUI toggles `TranslateTransform.X`
on the target `Border`'s `RenderTransform`. It deliberately does **not** set a hand-obtained
`ElementCompositionPreview` `Visual.Offset` — that bypasses XAML entirely and measures the compositor rather than the
framework, which made the WinUI number artificially small in schema-v3 results.

## Desktop-driven scenarios: live window resize and theme switch (added 2026-11-14)

The single-mutation and navigation scenarios time each framework's own frame loop. Two of the pain points desktop users
actually report are not visible there: what a window shows **while it is being resized**, and how long a **Light/Dark
theme switch** takes to reach the screen. Both are measured by `Bench.Contracts/LiveWindowDriver.cs`, the SAME code in
both hosts, running on a background thread of the host process. It drives the window with plain Win32 and judges only
what DWM composed; nothing a framework reports about itself enters these numbers.

**The instrument: DXGI desktop duplication.** `Bench.Contracts/DesktopDuplication.cs` duplicates the monitor the window
is on. Every frame DWM composes arrives once, with the QPC time it was presented (`DXGI_OUTDUPL_FRAME_INFO.LastPresentTime`);
small regions are copied on the GPU into a staging texture and read after the frame is released. A GDI `BitBlt` of the
screen DC was tried first and rejected: on the bench machine each call blocks for one full composition (8.3 ms at
120 Hz, 16.7 ms for a full client), so it could not keep up with one sample per frame and slowed the drive loop to
~24 steps/s.

**window-resize.** The detail page (page A, iteration 0, built once) is laid out responsively on both hosts: the main
column takes the width left beside the docked 380-DIP side list and the 24 cards re-wrap into it (FluentGPU: a `Wrap` row
with `Gap = 4`; WinUI: a `VariableSizedWrapGrid` with 132 x 134 cells and a -4 right margin, which fits exactly the same
number of cards per row); the hero and list rows are the navigation page's own elements. Around it, a root that fills
the window (FluentGPU: a column flex; WinUI: a Grid with rows 64 / * / 6 and columns * / 6) holds two **size probes** in
the top row and two magenta **edge strips** on the right and bottom edges. The strips are layout children, so they sit
exactly on the edge of whatever size the content was laid out at. The probes paint the laid-out client width and height
in DIP with the frame-ID colour code: FluentGPU from `Viewport.Size` (published by the host before that frame's layout),
WinUI from the root's `SizeChanged` (raised after the layout pass, before that frame renders).

The driver places the window at (32, 32) px, topmost (an occluded pixel would be judged as the framework's; the run
aborts if `WindowFromPoint` finds another window), waits until step 0 (1200 x 720) is on screen and correct, then for
each step k: takes the first composed frame presented after the previous `SetWindowPos` returned, judges it, and calls
`SetWindowPos` (cross-thread, synchronous: the window's UI thread must process the size change, as for any external
resize) to trajectory step k. The trajectory is a corner drag, 960 - 1440 x 600 - 840 DIP at 6 x 3 DIP per step
(9 x 4.5 px at 150 %, ~1,080 px/s at one step per 120 Hz frame), 1,000 measured steps after 60 warmup steps, five runs
per framework, alternating launch order. Each judged frame is classified:

- **in sync**: both probes decode to the size the window has, and the strip colour is on the window's actual right and
  bottom edge (client columns W-6 and W-3 px at five heights; rows H-6 and H-3 at five widths);
- **stretched**: the strips are on the edge but the probes carry an older size (an old frame scaled to the window);
- **unpainted edge**: some edge pixel is pure black or pure white (an area nothing has painted yet);
- **stale**: anything else (old content clipped, or not reaching the edge).

Reported: the share of in-sync frames, the class histogram, **content lag** in frames (the step whose size the content
shows versus the step the window is at; sizes are unique within a half-cycle, so a lag under 80 steps resolves exactly),
**resize-to-content latency** (`SetWindowPos` call to the present time of the first frame laid out at that step or
later; this is `frameMs`), content updates per second, the achieved step rate, and how long `SetWindowPos` blocked the
driver (the time the UI thread took to accept each size).

Two caveats, stated wherever the numbers are used. (1) The resize is programmatic. A user's edge drag runs the OS modal
size loop (`WM_ENTERSIZEMOVE`) on the window's own thread, and frameworks may treat that differently; starting a real drag
needs synthetic input that would move the owner's pointer, so it is not done. The test models programmatic resizes
(snap layouts, window managers, an app resizing itself) and the per-step cost of a size change in each framework.
(2) FluentGPU's main window, by design, defers re-layout during an OS edge drag (`WindowDesc.LiveResize` is off and not
exposed through `AppOptions`), so on the modal-loop path it shows its pre-drag content until mouse-up; the programmatic
path measured here is the one where it does re-lay-out every size it receives.

**theme-switch.** The detail page (page A, built once) in the shared 1200 x 720 frame, theme-aware the idiomatic way on
each side: FluentGPU builds it inside a component whose `Render` reads the `Tok` tokens (a theme change re-renders every
mounted component in place); WinUI swaps every surface and text brush for a Style whose setter is a `{ThemeResource}`
from App.xaml `ThemeDictionaries` (Dark = the navigation scenario's colours, Light = the FluentGPU warm palette's light
tokens; the accent is WinUI's own `AccentFillColorDefaultBrush`, the same OS-accent ramp rule FluentGPU follows). The
switch runs on each host's UI thread through its own marshal (`AppHost.Post` / `DispatcherQueue.TryEnqueue`), stamping
QPC immediately before it, and in the same turn paints the switch's id into the frame-ID probe:
FluentGPU `Tok.Use(kind)` + `RequestThemeTransition(0)`; WinUI `root.RequestedTheme = kind`. FluentGPU's default theme
switch is a 250 ms cross-fade; the bench snaps it (0 ms) because WinUI's `RequestedTheme` change is instant and a
cross-fade would be timed as "not finished yet".

Calibration first: each host is switched to Dark and to Light (unmeasured), and six regions are recorded in each (window
background, card, thumbnail, accent hero, the hero title's text block, the side list's text block; the two text regions
are block averages, so they track text colour, not one glyph's anti-aliasing). A region is used only if its two
references differ (L1 >= 24); the run aborts if fewer than four do. Then 200 measured switches (+10 warmup), alternating
direction. A switch is complete at the first composed frame presented after the stamp in which the probe carries its id
and every used region is within a quarter of the reference distance of the new theme. Reported: switch-to-complete
latency (`frameMs`), frames composed after the stamp that were still entirely in the old theme, **mixed frames**
(partly re-themed), frames that fell back out of the new theme within the next six frames, and timeouts (2 s).

Both scenarios are cadence-pass only (the runner skips them on the `cpu` pass): the observation is the composed desktop.
Screenshots (`*.resize-start|inflight|end.bmp`, `*.theme-dark|light.bmp`) are written beside every raw result as parity
evidence, together with the per-frame logs (`*.resize.jsonl`, `*.theme.jsonl`).

## Cold-load anchoring and stop points

Cold-load scenarios (`startup`, `buttons-225`, `text-1125`) are single-shot: `frameMs[0]` is the whole cold path.

- **Start anchor, both hosts:** a `[ModuleInitializer]` (`BenchClock.ProcessStartQpc`) that stamps QPC before any
  framework code runs. It must not be a plain `static readonly` field: `beforefieldinit` defers initialization to first
  use, which for the FluentGPU harness is *after* the engine is already up, silently excluding bring-up. That defect
  existed through schema v3.
- **Stop point, FluentGPU:** the render thread's acknowledgement of the first published frame (submit-confirmed).
- **Stop point, WinUI 3:** the **second** `CompositionTarget.Rendering` callback. The first callback fires *before* the
  frame it belongs to is handed to the compositor, so stopping there is strictly earlier than FluentGPU's stop and is
  not a like-for-like comparison. By the second callback the first frame has been handed over.
- **Sub-marks** (`coldStart` in the result JSON): `engineReadyMs` = anchor to "framework up, nothing presented yet"
  (FluentGPU: harness entry with device, window and tree live; WinUI: the first `Rendering` callback);
  `firstPresentMs` = anchor to the stop point, identical to `frameMs[0]`; `drivenFrames` = frames the harness had to
  pump itself (WinUI is callback-driven, always 0). `engineReadyMs` separates fixed bring-up from first-frame work.

Neither stop point is a *displayed* frame. Use the frame-ID probe or a camera for that claim.

## Passes are not interchangeable

- `cpu`: FluentGPU reports flush + layout + animation + record + command-build time and excludes fence/present waits.
  WinUI reports mutation + synchronous `UpdateLayout`; its asynchronous composition/render CPU must come from ETW.
  These in-app CPU fields are useful within a framework but are not the final cross-framework CPU verdict by themselves.
  Every "CPU work" summary row carries that definition asymmetry as an explicit caveat footnote.
- `cadence`: retains display pacing. PresentMon/WPR determines present-to-present intervals, missed refreshes, displayed
  FPS, and input/update-to-present latency. Never derive cadence from an app callback counter. `frameMs` is aggregated
  into "frame time" rows **only** on this pass; on the `cpu` pass FluentGPU suppresses vsync and the latency wait, so
  its `frameMs` is a loop timing, not a frame time, and is never summarized.
- allocation: managed allocated bytes per operation after warmup. WPR Heap/WinUI XAML traces are separate because the
  native frameworks and compositor allocate outside the managed heap. `GC.GetAllocatedBytesForCurrentThread` sees the
  **UI thread only** on both sides: FluentGPU's render thread and WinUI's compositor threads are invisible to it, and a
  zero on either side is not a claim about total process allocation. Summary alloc rows carry that caveat.

### Raw CPU pass: one iteration per dispatcher callback

Both hosts advance the raw pass one iteration at a time and hop between iterations; the hop is **outside** the timed
section on both sides (FluentGPU excludes its inter-iteration wait for the published frame, WinUI excludes the
`DispatcherQueue` hop). WinUI runs each warmup/measured iteration in its own `TryEnqueue` callback with a single cached
handler instance, so the hop costs no measured allocation. Running the whole pass inside one dispatcher callback (the
schema-v3 behaviour) starves the dispatcher: asynchronous mutations such as `ScrollViewer.ChangeView` stack up across
1,060 iterations instead of being serviced, so the pass is driven one iteration per callback.

### Capture screenshot parity on the `cadence` pass — the WinUI window is blank on the `cpu` pass

On the raw `cpu` pass the WinUI host's window stays **solid white**: no content, and not even the #24211C background.
This is not a defect in any one scenario and not a rendering failure — it reproduces identically on every WinUI
scenario (verified against `tree-churn`, published since schema v3, as well as `page-navigation`). The cause is the
raw pass's own design: `RunRawCpuStep` re-enqueues itself on the `DispatcherQueue` immediately after each iteration, so
the UI thread is saturated with back-to-back normal-priority callbacks for the whole run and XAML's render/compose
callback never gets a turn. Nothing is ever composited, so the window never advances past its initial white surface.

The measurement is unaffected — that pass times mutation + synchronous `UpdateLayout`, and this document already states
that WinUI's composition/render CPU must come from ETW rather than from an in-app field — but it means **a blank WinUI
window on the `cpu` pass is expected and proves nothing either way**. Screenshot parity is therefore only meaningful on
the display-paced `cadence` pass, where both hosts present normally. Verify the gate (background #24211C on both, page
structure visible on both, alternation visible, iteration-stamped strings changing, frame-ID probe decoding on both)
there, and disregard `cpu`-pass pixels.

One capture trap worth recording: `SetForegroundWindow` is refused to a process that does not own the foreground, so a
capture script that relies on it silently photographs whatever app *does* own the foreground while reporting success.
Use `SetWindowPos(HWND_TOPMOST)` and assert that the window under the client centre is the one being measured. Note
that WinUI 3 hosts its XAML in a child HWND, so that occlusion assertion must accept a child of the target window.

### The WinUI host publishes its own XAML assets, and casts resources by QueryInterface

Earlier runs reported `virtual-scroll-10k` as a WinUI crash (`STATUS_STOWED_EXCEPTION`, 0xC000027B, 10/10). That was a
defect in **this harness**, not in WinUI, and it is fixed. Three faults were stacked, each of which surfaces as the same
stowed exit code because it throws inside a XAML callback:

1. `dotnet publish` of an unpackaged WinUI 3 app leaves the app's own XAML assets in the build output: `WinUIBench.pri`
   and the compiled `App.xbf` / `BenchTemplates.xbf` never reached the publish folder. `LoadComponent("ms-appx:///App.xaml")`
   then resolves nothing **without throwing**, so the process ran with an empty `Application.Resources` — no
   `XamlControlsResources` (WinUI's own control styles) and no row template. `WinUIBench.csproj` now publishes them
   explicitly (`PublishAppXamlAssets`).
2. Under NativeAOT, values read out of the WinRT resource map come back as base `DependencyObject` RCWs; the concrete
   runtime class is not recovered, so `(DataTemplate)Application.Current.Resources[key]` throws `InvalidCastException`.
   The host now QueryInterfaces for the projected type.
3. The same applies to the `VisualTreeHelper` walk that finds the `ListView`'s `ScrollViewer`: `is ScrollViewer` is
   false against such an RCW.

Only the scroll scenarios read a resource, which is why they alone crashed while every other WinUI scenario ran — but
those scenarios ran against an application with no style dictionary loaded, so **WinUI numbers taken before this fix are
not comparable to numbers taken after it**. With the fix, WinUI completes the scroll scenario at every row count probed,
from 1,000 to 200,000; there is no row-count breaking point.
- memory: process working set/private bytes, GPU-process shared/dedicated/local/non-local bytes, and adapter-wide deltas
  are separate fields.

## Present / draw / frame-appearance (authoritative for rendering claims)

In-app `frameMs` and `cpuWorkMs` never answer "did the user see the new pixels on time?"

| Question | Best method |
|---|---|
| Why is a frame late? | ETW / WPR |
| Which framework makes updates visible sooner? | Desktop frame-ID capture (`scripts/capture-frame-id.ps1`) |
| What does the user physically see? | High-speed camera |
| Is it smooth? | Present interval p50/p95/p99 + missed-refresh count (PresentMon / WPR) |
| Is rendering GPU-bound? | GPU ETW time / queue depth |
| Is it visually equivalent? | Screenshot / pixel comparison |

`CompositionTarget.Rendering` is useful as a **WinUI scheduling callback**, not proof of a displayed frame. A
60 Hz-looking WinUI host result may be callback cadence rather than a panel limit.

### Frame-ID color probe (mutation → desktop-visible)

Both hosts paint a 48×48 opaque RGB patch at a fixed client position. Each measured mutation encodes its iteration
(losslessly through 16,383 — above the benchmark maximum) into the patch and appends `{iteration,qpc,r,g,b}` to a
`.mutations.jsonl` beside the host JSON.
`Bench.FrameIdCapture` BitBlts that desktop rectangle, decodes the ID, and joins first-seen sample QPC to mutation QPC.

```powershell
.\benchmarks\FrameworkComparison\scripts\capture-frame-id.ps1 `
  -Executable .\artifacts\framework-comparison\publish\FluentGpu\FluentGpuBench.exe `
  -Framework FluentGpu -Scenario virtual-scroll-10k `
  -OutputDirectory .\benchmarks\FrameworkComparison\results\frame-id
```

Use WPR afterward to explain whatever the probe finds (XAML activity, composition, DXGI, CPU). PresentMon remains the
smoothness / GPU-busy instrument where process attribution works; on this machine WinUI scroll hosts have not yet
emitted PresentMon rows for `WinUIBench.exe`, so do not treat missing PresentMon as "WinUI presented nothing."

### PresentMon refresh + missed-vblank rules

- Never assume 99 Hz. Record Windows `EnumDisplaySettings` nominal Hz, measured
  `MsBetweenDisplayChange` p50 (fallback: present p50), PresentMode histogram, and whether nominal conflicts with
  measured cadence (±8%).
- A **120 FPS claim is valid only after that conflict is resolved** against the verified physical display rate.
- Missed vblank: prefer DXGI refresh-count deltas when PresentMon exposes them; otherwise intervals **> 1.5×**
  measured refresh. Ordinary 8.4–9.2 ms jitter around ~8.33 ms is **not** a miss.
- Keep app-owned PresentMon rows (benchmark PID) separate from DWM-global counters; report external DWM/OS stalls
  separately rather than silently excluding them.
- Optional FluentGPU `--pacing-trace path.jsonl` joins mutation/frame-ID, publish/present-ack, UI phases, DXGI/DWM
  stats, and phase-gate ceiling escapes without hot-path allocation after begin.
- Phase-gate wake is an acknowledgement waitable waited with input + HR timer (not message-only). The two-refresh
  liveness escape remains; every escape is counted (`PhaseGateCeilingEscapes`) and must appear in results/traces.

### Smoothness acceptance (verified fixed rate)

At a **verified** 120 Hz (not the conflicted Windows 99 Hz report), accept a smoothness pass only when:

- effective displayed rate ≥ 119.5 FPS;
- present-interval p95 / p99 ≤ 9.6 / 10.5 ms;
- zero engine-correlated missed vblanks;
- no accepted capture has an interval above 1.5 refreshes (12.5 ms).

A zero-miss guarantee is only credible for a controlled, plugged-in performance profile with background workload
recorded — not every busy Windows desktop.

## Integrated-GPU / UMA memory rule

The test machine's Adreno GPU uses unified system memory. Textures, buffers, command data, and cached assets consume the
same physical DRAM pool as CPU pages even when Windows labels them GPU shared/local memory. Process working set and
`GPU Process Memory\Shared Usage` can therefore overlap; **do not add them**. Also report adapter-wide deltas because
WinUI composition resources may be charged to compositor processes rather than the benchmark process. Adapter deltas
are noisier, so capture an idle baseline, alternate framework order, and repeat.

## Summary schema (`fluentgpu-framework-bench-summary/v4`)

Per-process results are `fluentgpu-framework-bench/v2` (adds the optional `coldStart` object above). The summary is v4:

- `rows[].winUI` and `rows[].fluentGpu` are **nullable**. Null means that side had zero successful runs for the
  scenario — a scenario is never silently dropped just because one framework never survived it. `ratio` and
  `reductionPercent` stay null whenever either side is null, and the Markdown prints `CRASHED` (attempted, never
  succeeded) or `no data` (never attempted).
- `rows[].caveat` states what the row does and does not mean; the Markdown renders caveats as footnotes.
- `outcomes[]` is emitted for **every** scenario and framework: `attempted`, `succeeded`, and a `failureSignature`
  (`0xC000027B x5` — the most common failure exit code and its count). This is the honest denominator behind every row.
- Derived `workload delta p50` rows appear for `buttons-225` and `text-1125` when `startup` was measured in the same
  run: scenario external median minus startup external median, per framework. It isolates workload cost from fixed
  bring-up and is explicitly labelled as derived.

Aggregation lives in `Bench.Contracts/SummaryAggregation.cs` (plain data in, rows and outcomes out). Desktop-driven
scenarios add their own rows (the primary latency series, every extra per-step series, and the median over runs of every
per-run scalar) with no CPU-definition caveat: one driver measures both sides.

The suite is run **one runner invocation per scenario** (`--scenarios x --summary-name summary-x`), so a machine-wide
GPU lock can be held per invocation instead of around a whole pass; `--aggregate` then merges every `summary-*.json` of
the output directory into `summary.json`, re-reading each successful run's raw result and keeping every failed run.

## Statistics and publication gate

- Report p50, p90, p99, maximum, allocations/operation, working set, private bytes, GPU shared/local bytes, and failed
  runs. Include sample counts and raw units.
- Ratio is `WinUI / FluentGPU`. Reduction is `(WinUI - FluentGPU) / WinUI x 100%`.
- Do not average unrelated scenarios and do not publish a single overall score.
- Failed/crashed runs stay in the reliability table and never enter latency percentiles. Yes, crashing is an impressively
  low-latency way to stop drawing; it is not a performance win.
- README claims may replace estimates only after Release NativeAOT hashes, workload parity, trace capture, and raw JSON
  have all been checked. A theoretical estimate must remain explicitly labelled and may not be mixed with measured data.
