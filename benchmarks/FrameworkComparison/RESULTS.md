# Results — 2026-11-14 (schema v4, runs `2026-11-14-cpu` + `2026-11-14-cadence`)

The current engine re-measured with the 2026-08-01 protocol unchanged: the same nine scenarios with the same rep counts
(startup 30, cold loads 10, everything else 5 fresh processes per framework, alternating launch order, 1,000 measured
iterations after 60 warmup), plus two new desktop-driven scenarios, **theme-switch** and **window-resize** (cadence pass,
5 runs each; see METHODOLOGY "Desktop-driven scenarios"). Both hosts are Release **NativeAOT win-arm64** publishes of
commit `ede9250a`, hash-verified against `publish-evidence.json` before every run.

| Evidence | Value |
|---|---|
| FluentGpu commit | `ede9250a` (bench branch on engine `9c9021a5`, origin/main of 2026-11-14). The archived patch holds only METHODOLOGY.md and the chart script, written while the runs were going: nothing compiled into either host |
| WinUI baseline | `Microsoft.WindowsAppSDK 2.3.1` (public stable), `Microsoft.UI.Xaml.dll` sha256 `81b4a071...` |
| Runtime | .NET 11 RC3 (`11.0.100-rc.3.26522.148`), NativeAOT; the 2026-08-01 data was .NET 10 |
| Runs | `results\2026-11-14-cpu` (160 runs) + `results\2026-11-14-cadence` (180 runs) |
| Outcomes | **339/340 succeeded.** One FluentGpu crash: window-resize rep 2, `0xC0000409`, an unhandled `ResizeBuffers failed: 0x8007000E` (E_OUTOFMEMORY) thrown on the render thread from `AppHost.DrainPopupRenderActions` -> `D3D12Device.Resize`. Retained in the outcomes table; window-resize FluentGpu numbers are from the 4 surviving runs. WinUI: zero failures |
| Execution | One runner invocation per scenario per pass, each under the machine-wide GPU lock, each started only after a check that no build/test process was running; `--aggregate` merged them |
| Machine | Snapdragon X / Adreno X1-85 (driver 31.0.148.0), 2496 x 1664 @ 150 %, 120 Hz, plugged in |

**Parity.** Unchanged hosts for the nine existing scenarios (two drift fixes only: the retired engine phase-gate counters
in the optional pacing trace, and `ListOptions.Overscan`, which the engine removed; the list now uses the engine's
default overscan). Client area measured 1200 x 720 DIP on both hosts in every run; the scroll trajectories are
instrumented identical on both hosts in every run (`offsetChanges=1060 wraps=10 maxOffsetPx=22000`, zero rejected
`ChangeView`). Parity screenshots were retaken on the cadence pass for page-navigation, virtual-scroll-10k, tree-churn
and localized-transform: same structure and content as 2026-08-01. Two standing differences, unchanged since August and
not new: tree-churn and localized-transform lay out their equal-count nodes in a different grid pitch/shade on the two
hosts. The new scenarios' parity screenshots (`*.resize-start|inflight|end.bmp`, `*.theme-dark|light.bmp`) are beside
every raw result: the responsive page wraps to the same cards per row on both hosts, and the two themes match colour
for colour (the light palette and the accent ramp included).

**Noise.** Run-to-run noise on this machine is ~30 %. Absolute cold-start times are 10-20 % higher than in August on
**both** sides (an empty WinUI window 110 -> 132 ms, FluentGpu 109 -> 124 ms) with no change to the WinUI binary; read
cold-start ratios, not absolutes. A difference is claimed below only when the alternating 5-run (or 10/30-run)
distributions agree on its sign.

## 1. Headline candidates, new scenarios

### Theme switch, Light <-> Dark on the detail page (new)

1,000 measured switches per framework (5 runs x 200, alternating direction), each judged on every composed frame.

| Switch to the first fully re-themed composed frame | WinUI 3 | FluentGpu | Ratio |
|---|---:|---:|---:|
| p50 | 24.1 ms (**3 refreshes**) | **8.7 ms (1 refresh)** | **2.8x** |
| p90 | 26.1 ms | **12.4 ms** | 2.1x |
| p99 / max | 33.2 / 43.9 ms | **17.5 / 19.5 ms** | 1.9x / 2.3x |
| per-run p50 (5 runs each) | 24.2, 24.2, 24.2, 24.7, 21.6 | 8.3, 8.9, 8.8, 9.0, 8.8 | no overlap |
| frames composed after the switch still fully in the old theme (total) | 1,244 | 165 | |
| half-themed (mixed) frames, flicker-back after completion, timeouts | 0 / 0 / 0 | 0 / 0 / 0 | |

Both frameworks switch atomically (no half-themed frame on either side); FluentGpu gets the new theme on screen in the
next refresh, WinUI 3 two refreshes later. The WinUI `RequestedTheme` setter itself runs synchronously for 0.72 ms
(p99 2.8 ms) on the UI thread; FluentGpu's switch call is a token swap (3 us) whose work runs in the next frame. Caveat:
FluentGpu's default theme switch is a 250 ms cross-fade; this measures the snap (`RequestThemeTransition(0)`), the
like-for-like of WinUI's instant `RequestedTheme`.

### Live window resize (new): **WinUI 3 wins**

`SetWindowPos` one corner-drag step per composed frame (960-1440 x 600-840 DIP, 6 x 3 DIP per step), 1,000 measured
steps per run. WinUI 5 runs, FluentGpu 4 runs (one crashed, see above).

| Metric | WinUI 3 | FluentGpu | Better |
|---|---:|---:|---|
| Composed frames whose content matches the window (in sync) | 0.6 % | 0.2 % | neither |
| Content lag behind the window, frames p50 / p99 | **2 / 4** | 20 / 78 | **WinUI 10x** |
| Resize step to matching content, p50 / p99 | **29.5 / 48.8 ms** | 577 / 2,452 ms | **WinUI 20x** |
| Content updates per second during the drag (median of runs) | **61** | 3.1 | **WinUI** |
| Resize steps the UI thread accepted per second | **88** | 53 | **WinUI** |
| `SetWindowPos` blocked (UI thread accepting a size), p50 / p99 | **2.3 / 8.8 ms** | 6.0 / 18.6 ms | WinUI |
| Mismatched frames by kind | 44 % unpainted (white/black) edge, 55 % stale | 56 % **stretched** old frame, 44 % stale | |
| Runs completed | 5/5 | **4/5** (ResizeBuffers E_OUTOFMEMORY) | WinUI |

Neither framework puts content of the new size on screen in the same composed frame as the window frame. WinUI 3 keeps
up two frames behind and shows an unpainted strip at the moving edge in 44 % of frames. FluentGpu's opaque HWND
swapchain pays a synchronous `ResizeBuffers` per size it processes (13-66 ms each, in the engine's own `[window.resize]`
log), coalesces the rest, falls ~20 frames behind, and shows the old frame **stretched** to the new window in between.
A diagnostic FluentGpu run on the composited (DComp, Mica-style) window path (`--composited`, one run, not in the
dataset) was worse: 42 frames of lag and no content update at all during the drag. Caveats: the resize is programmatic
(no OS modal size loop; see METHODOLOGY), and a FluentGpu main window deliberately defers re-layout during a user's
edge drag (`LiveResize` off), so a real drag would show its pre-drag content until mouse-up. Not a headline for
FluentGpu: an engine work item (per-step swapchain resize cost, the stretch, the popup-swapchain OOM).

## 2. The 2026-08-01 claims, re-measured

Old = 2026-08-01 (`wavee1` for navigation/allocation, `waved` for cold start/scroll/memory, as published); new =
2026-11-14. Ratio = WinUI / FluentGpu (higher = FluentGpu better, except where noted).

| Metric | Old WinUI | Old FluentGpu | Old ratio | **New WinUI** | **New FluentGpu** | **New ratio** |
|---|---:|---:|---:|---:|---:|---:|
| Navigation frame time p50 (cadence) | 16.69 ms | 8.37 ms | 2.0x | **18.0 ms** | **8.0 ms** | **2.3x** |
| Navigation frame time p90 | 18.19 | 9.12 | 2.0x | 23.4 | 9.4 | 2.5x |
| Navigation frame time p99 / max | 42.9 / 64.3 | 9.97 / 10.9 | 4.3x / 5.9x | 32.3 / 126 | 10.1 / 17.5 | 3.2x / 7.2x |
| CPU per navigation p50 / p99 (cadence) | 6.25 / 20.5 | 1.10 / 2.07 | 5.7x / 9.9x | 6.90 / 14.9 | **0.73** / 1.89 | **9.5x** / 7.9x |
| CPU per navigation p50 / p99 / max (un-paced) | 6.05 / 31.9 / 463.5 | 1.09 / 2.16 / 3.46 | 5.6x / 15x / 134x | 6.17 / **128** / 327 | 0.79 / 1.79 / 4.08 | 7.8x / **72x** / 80x |
| 1,000 navigations, wall clock | 18.7 s | 8.96 s | 2.1x | 18.8 s | 8.9 s | 2.1x |
| Private bytes after 1,000 un-paced navigations | 2,568 MiB | 104 MiB | 25x | 2,609 MiB | 113 MiB | 23x |
| Managed alloc per navigation (UI thread only) | 135 KB | 270 KB | 0.5x (WinUI) | 135 KB | **71 KB** | 1.9x |
| Scroll 1k rows, CPU per frame p50 | 0.92 ms | 0.34 ms | 2.7x | 1.25 | **0.14** | **9.0x** |
| Scroll 10k rows, CPU per frame p50 | 0.98 ms | 0.31 ms | 3.2x | 1.32 | **0.19** | **7.0x** |
| Tree churn (499-node swap), CPU per frame p50 | 2.38 ms | 0.70 ms | 3.4x | 2.72 | 0.73 | 3.7x |
| Cold start, empty window p50 | 109.7 ms | 109.0 ms | 1.0x | 132 | 124 | 1.07x (dead heat) |
| Cold start, empty window p99 | 138.4 | 157.6 | 0.88x (WinUI) | 182 | 239 | 0.76x (WinUI) |
| Cold start, 225 buttons p50 / p99 | 200.9 / 216.2 | 109.5 / 114.2 | 1.8x / 1.9x | 230 / 248 | 125 / 141 | 1.85x / 1.76x |
| Cold start, 1,125 texts p50 | 201.3 | 140.4 | 1.4x | 240 | 126 | **1.9x** |
| Content cost (launch delta), 225 buttons | 91.2 ms | 0.47 ms | 194x | 97.4 | 0.53 | 184x |
| Content cost (launch delta), 1,125 texts | 91.6 ms | 31.4 ms | 2.9x | 107.4 | **1.56** | **69x** |
| Idle memory floor (empty window), WS / private | 74 / 37 MiB | 105 / 88 MiB | WinUI | **75 / 38** | 101 / 91 | **WinUI** (2.4x private) |

What moved: FluentGpu's navigation CPU fell another third (1.10 -> 0.73 ms) and its managed allocation per navigation
fell 270 -> 71 KB (now below WinUI's visible 135 KB, with the counter-visibility caveat below); scroll CPU per frame fell
to 0.14-0.19 ms (7-9x below WinUI); 1,125 text blocks now add 1.6 ms to launch instead of 31 ms. WinUI's un-paced tail
stays pathological (p99 128 ms, worst 327 ms) and its memory still runs away to 2.6 GiB. FluentGpu's own navigation tail
got slightly worse at the very top (cadence max 17.5 ms vs 10.9; one frame; p99 unchanged at 10.1).

Allocation caveat (unchanged): `GC.GetAllocatedBytesForCurrentThread` sees the UI thread only; WinUI's XAML tree is largely
native/COM and invisible to it, so the alloc row is not a total-allocation win for either side.

## 3. Steady state, display-paced (cadence pass)

| Scenario | Metric | WinUI 3 | FluentGpu |
|---|---|---:|---:|
| virtual-scroll-1k | p50 / p90 / p99 / max | 8.33 / 8.55 / 9.24 / 23.9 | **7.66** / 9.06 / 9.55 / **16.8** |
| virtual-scroll-10k | p50 / p90 / p99 / max | 8.33 / 8.58 / 9.25 / **17.8** | 8.32 / 9.19 / 9.78 / 26.7 |
| localized-transform | p50 / p90 / p99 / max | 8.33 / 8.54 / 9.43 / **15.2** | **7.97** / 9.21 / 9.84 / 45.9 |
| localized-text | p50 / p90 / p99 / max | 8.33 / 8.57 / 9.41 / 23.5 | 6.76 / 9.05 / 9.67 / 24.5 |
| tree-churn | p50 / p90 / p99 / max | 8.33 / 8.55 / 9.66 / 20.7 | 8.25 / 9.33 / 9.92 / 19.5 |

Both hold the display cadence at p50 on single-mutation scenarios; WinUI stays ~0.3-0.6 ms tighter at p90/p99, and
worst frames are mixed (FluentGpu has the single worst frame here, 45.9 ms in localized-transform). Unchanged verdict:
trivial mutations are the floor both frameworks clear.

## 4. Memory

| Scenario (cadence, p50) | WinUI WS / PB | FluentGpu WS / PB | Winner |
|---|---:|---:|---|
| startup | **75.0 / 37.8 MiB** | 101 / 90.9 | **WinUI** (idle floor, 2.4x private) |
| buttons-225 | 99.4 / **63.4** | **89.2** / 110 | WinUI on private bytes |
| text-1125 | 155 / 132 | **131 / 122** | FluentGpu |
| localized-transform / -text, tree-churn | 149-153 / 110-112 | **121-125** / 109-113 | WS FluentGpu; private bytes now even (FluentGpu was ~20 % lower in August) |
| virtual-scroll-1k / 10k | **98.7-99.8 / 53.0-53.9** | 157-161 / 144-145 | **WinUI** (2.7x private; FluentGpu up from 110 in August) |
| page-navigation (cadence) | 185 / 151 | **122 / 108** | FluentGpu |
| page-navigation (1,000 un-paced) | 1,444 / 2,609 | **126 / 113** | **FluentGpu 23x** |
| theme-switch | 177 / 130 | **127 / 105** | FluentGpu |
| window-resize | 184 / 136 | **137 / 115** | FluentGpu |

FluentGpu's steady-state private bytes grew since August on the scroll scenarios (110 -> 144 MiB) and on the
fixed-tree scenarios (92 -> 110 MiB); not yet attributed (the engine gained compositor animations, retained tiles and
popup prewarm since then; the startup log shows a parked popup swapchain). An engine follow-up, stated as a regression.

## 5. Reliability

339/340: zero WinUI failures; one FluentGpu crash in the new window-resize scenario (render-thread `ResizeBuffers`
E_OUTOFMEMORY on the popup swapchain path under 120 resizes per second). Every other scenario: zero crashes, both
frameworks, both passes.

## 6. Instruments & still open

- The new desktop-duplication driver reads every composed frame with its present time; it is the instrument the old
  frame-ID BitBlt sampler (~1 ms polling, ~52 % coverage ceiling) could not be. Porting the frame-ID latency capture to it
  would give mutation-to-glass latency for every scenario.
- Displayed-cadence FPS claims still wait on a PresentMon v2 re-capture.
- A busy-UI-thread scenario (does an animation keep its rate while the app does 50 ms of work per second?) was
  considered and not built in this round.

---

# Superseded: results of 2026-08-01 (schema v4, runs `waved-*` + `wavee1-*`)

Kept for reference; superseded by the 2026-11-14 dataset above (same protocol, current engine, .NET 11). The old raw
data lives in the `results\waved-*` and `results\wavee1-*` directories of the checkout that took them.

Both hosts are Release **NativeAOT win-arm64** self-contained publishes, alternating fresh-process reps,
hash-verified against `publish-evidence.json` before every run. **Workload parity is screenshot-verified**, not
assumed: identical physical client (1800×1080 px = 1200×720 DIP @1.5), identical background (#24211C, measured),
identical shared workload constants, row templates binding the same advancing indices, frame-ID probes painting and
decoding on both hosts, scroll offset trajectories instrumented identical (`offsetChanges=1060 wraps=10
maxOffsetPx=22000`, zero rejected), and mutations pixel-verified visible on both sides (transform moves exactly
12 DIP on both; churn alternates its two variants on both). Parity screenshot pairs are archived with the runs.

| Evidence | Value |
|---|---|
| FluentGpu commit | `d83875990074822df22cdfff8704df4a1aaad639` + the archived uncommitted benchmark/engine patch |
| WinUI baseline | `Microsoft.WindowsAppSDK 2.3.1` (public stable) |
| Runs | `results\waved-*` (8 scenarios, 150 runs/pass) + `results\wavee1-*` (9 scenarios incl. page-navigation + the reconciler alloc fix, 160 runs/pass) |
| Outcomes | **620/620 runs succeeded across both datasets. Zero crashes, either framework, every scenario in Rows + Outcomes.** |

Dataset note: the `wavee1` runs were taken with the user's media app playing (ambient load) — absolute cold-start
numbers there run ~10% high **on both frameworks equally** (verified by an A/B recheck; ratios unaffected). Cold-start
absolutes below cite the quiet-machine `waved` runs; page-navigation and allocation numbers cite `wavee1`.

Earlier runs (`wavec-*`, and all schema-v3 results) are **void — do not cite**. The NativeAOT publish had silently
omitted `WinUIBench.pri`/`.xbf`, so WinUI ran with an empty `Application.Resources` (no styles — flattered numbers,
and the sole cause of the previously-reported `0xC000027B` scroll crashes), and WinUI's window was 1200×720
*physical* pixels vs FluentGpu's 1200×720 DIP. Both fixed; everything below is measured post-fix.

### 1. Cold start — process launch to first frame handed to the compositor

External wall clock, symmetric stop points (FluentGpu: render-thread present-ack; WinUI: second
`CompositionTarget.Rendering` tick). CPU pass; cadence pass agrees within noise.

| Scenario | Metric | WinUI 3 | FluentGpu | Verdict |
|---|---|---:|---:|---|
| startup (1 text node) | p50 | 109.7 ms | 109.0 ms | **Dead heat** |
| startup | p99 / max | **138.4 / 143.1** | 157.6 / 158.4 | WinUI tighter tail |
| buttons-225 | p50 | 200.9 | **109.5** | **FluentGpu 1.84×** |
| buttons-225 | p99 / max | 216.2 / 217.1 | **114.2 / 114.5** | **FluentGpu 1.89×** |
| text-1125 | p50 | 201.3 | **140.4** | **FluentGpu 1.43×** |

**Workload delta** (scenario minus startup median — the marginal cost of the *content*):

| Content | WinUI 3 | FluentGpu | Ratio |
|---|---:|---:|---:|
| 225 styled buttons | 91.2 ms | **0.47 ms** | **194×** (cadence pass: 82.5 vs 1.26 = 65×) |
| 1125 text blocks | 91.6 ms | **31.4 ms** | **2.9×** |

FluentGpu's launch is content-insensitive: an empty window and 225 buttons both open in ~109 ms. Engine changes
behind this (shader DXBC disk cache + parallel pipeline init) cut FluentGpu's own cold start 345.8 → ~109 ms; the
one-time first launch on a machine pays ~100 ms of parallel shader compilation, cached thereafter.

### 2. Page navigation — the compound workload (new scenario, `wavee1` runs)

Each iteration navigates between two structurally different realistic pages, built fresh per navigation as real
app navigation does (Page A "detail": hero + 24-card grid + 40-row list, 269 nodes / 170 text runs; Page B
"library": 40 tiles + 20-row list, 155 nodes; iteration-stamped strings so nothing can be cached away; no page
cache, no transitions, either side; alternation + stamping pixel-verified on both hosts).

| Metric (cadence pass) | WinUI 3 | FluentGpu | Ratio |
|---|---:|---:|---:|
| **Frame time p50** | **16.69 ms** | **8.37 ms** | **2.0×** — FluentGpu holds full refresh; WinUI needs exactly two vblanks per navigation, i.e. **half frame rate** |
| Frame time p99 / max | 42.9 / 64.3 ms | **9.97 / 10.9 ms** | 4.3× / 5.9× |
| CPU per navigation p50 | 6.25 ms | **1.10 ms** | **5.7×** |
| CPU per navigation p99 / max | 20.5 / 54.7 ms | **2.07 / 3.62 ms** | 9.9× / 15× |
| Wall clock, 1000 navigations | 18.7 s | **8.96 s** | 2.1× |
| Working set / private bytes p50 | 247.9 / 246.7 MiB | **122.9 / 103.2 MiB** | 2.0× / 2.4× |

Driven flat-out (cpu pass, un-paced): WinUI's CPU tail explodes (p99 31.9 ms, max **463.5 ms**) and its memory
climbs to **1.41 GiB working set / 2.57 GiB private bytes** — navigation outruns its cleanup — while FluentGpu
stays at 3.46 ms max and a flat **123 MiB**. One counter-caveat, stated plainly: FluentGpu shows *higher measured
managed alloc per navigation* (270 KB vs 135–160 KB), because its Element trees are managed objects while WinUI's
XAML tree is largely native/COM and invisible to `GC.GetAllocatedBytesForCurrentThread` — the standing
counter-visibility caveat, not a win for either side.

This is the workload class interactive apps live in: the microbenchmark scenarios converge on vsync because one
mutated node is cheap everywhere; building a real page is where the frameworks diverge, and it matches the
cold-content result (§1: 194× marginal content cost) rather than contradicting it.

### 3. Steady-state — display-paced (cadence pass)

Frame time (mutation → next frame), both sides vsync-paced on the same panel:

| Scenario | Metric | WinUI 3 | FluentGpu |
|---|---|---:|---:|
| virtual-scroll-1k | p50 / p90 / p99 / max | 8.33 / 8.46 / 8.86 / **11.93** | **8.08** / 9.44 / 10.05 / 17.26 |
| virtual-scroll-10k | p50 / p90 / p99 / max | 8.33 / 8.45 / 8.78 / 23.82 | **8.03** / 9.40 / 10.08 / **18.37** |
| localized-transform | p50 / p90 / p99 / max | 8.33 / 8.49 / 9.07 / 24.12 | **8.14** / 9.56 / 10.94 / **14.05** |
| localized-text | p50 / p90 / p99 / max | 8.33 / 8.46 / **9.99** / 196.26¹ | 8.24 / 9.44 / 10.38 / 1344.96¹ |
| tree-churn | p50 / p90 / p99 / max | 8.33 / 8.45 / 9.05 / **12.57** | **8.14** / 9.31 / 10.18 / 14.41 |

Both frameworks hold the display cadence at p50; WinUI is ~1 ms tighter at p90/p99; worst-frame results are mixed.
Notable: **10× the scroll rows costs neither side anything** (1k vs 10k within 0.05 ms) — virtualization works on
both. ¹ localized-text tails: FluentGpu's 1345 ms max is one isolated frame in one rep (next-worst rep max
16.4 ms — environmental stall); WinUI's tail there is *repeated* — 3 of 5 reps exceed 100 ms.

**Per-frame CPU while display-paced** (definition caveat carried on every row: WinUI = mutation + synchronous
`UpdateLayout`, no render/compose; FluentGpu = full frame CPU incl. record + command-build + submit):

| Scenario | WinUI 3 p50 | FluentGpu p50 | Ratio |
|---|---:|---:|---:|
| virtual-scroll-1k | 0.923 ms | **0.340 ms** | **2.7×** — FluentGpu does *more* per tick and still wins |
| virtual-scroll-10k | 0.983 ms | **0.308 ms** | **3.2×** |
| tree-churn (499-node swap) | 2.384 ms | **0.702 ms** | **3.4×** (cpu-pass max: 19.8 vs 2.5 ms = 8.1×) |
| localized-transform / -text | 0.02 / 0.28 ms | 0.75 / 0.91 ms | WinUI smaller span by definition — its render cost is off-thread and unmeasured here; the frame-time table above is the level comparison |

### 4. Memory (honest ledger — two different shapes)

| Scenario (cadence, p50) | WinUI WS / PB | FluentGpu WS / PB | Winner |
|---|---:|---:|---|
| startup | **74.3 / 37.2 MiB** | 105.2 / 87.7 | **WinUI** (the idle floor: 2.4× PB) |
| buttons-225 | **100.6 / 64.9** | 109.9 / 93.2 | WinUI (narrowing) |
| text-1125 | 153.9 / 130.7 | **116.4 / 100.8** | **FluentGpu** |
| localized-transform | 149.7 / 110.7 | **108.9 / 92.5** | **FluentGpu** |
| localized-text | 153.6 / 112.2 | **109.9 / 92.4** | **FluentGpu** |
| tree-churn | 148.8 / 109.4 | **109.6 / 91.9** | **FluentGpu** |
| virtual-scroll-1k/10k | **98.1–98.8 / 52.6–53.4** | 126.7–128.2 / 109.7–110.8 | WinUI |

The shape: WinUI starts light (~74 MiB WS empty) but **grows ~50 MiB under sustained rendering** (compare its
cpu-pass localized-transform WS of 80.7 MiB to 149.7 on cadence); FluentGpu starts at a higher floor (~105 MiB —
a known UMA/runtime characteristic) and **stays flat under load** (±3 MiB between passes). Under sustained
display-paced work, FluentGpu uses ~30% less memory on four of eight scenarios.

**Managed allocation — was a weak spot, now fixed at the engine level** (`wavee1` runs): the investigation found
two real engine bugs — the reconciler heap-allocated ~8.6 KB of unpooled diff-scratch on every reconcile of any
container over 128 children, and programmatic `ScrollBy` re-rendered the entire ItemsView per step. Fixed
(pooled scratch + wake-without-re-render, with a new VerticalSlice regression gate); tree-churn dropped
**9,218 → 658 B/op** (at the harness's own measurement floor — WinUI's 690 B includes the same harness cost, so
this is parity) and scroll **5,179 → 1,307 B/op** (near-parity with WinUI's ~1.1 KB), with scroll CPU *improving*
13–17%. Residual per-op alloc is dominated by the immutable-Element programming model on cold row realize —
by design, documented. Counter caveat both ways: render/compositor-thread and native/COM allocations are
invisible to `GC.GetAllocatedBytesForCurrentThread` on both sides.

### 5. Reliability & debuggability

**620/620 runs succeeded on both frameworks across both datasets.** The previously-published WinUI `0xC000027B` scroll crashes are
**retracted** — they were three stacked harness/publish bugs on our side (missing `.pri`/`.xbf` in the NativeAOT
publish layout; two NativeAOT RCW invalid casts), not a WinUI failure; post-fix WinUI completes 10k and even 200k
rows.

The development-experience finding that *does* stand, now with better evidence: all three unrelated, mundane bugs
produced the **same silent, stackless `STATUS_STOWED_EXCEPTION` failfast** — window black or dead, no managed
exception, no stderr, nothing but a WER entry faulting `Microsoft.UI.Xaml.dll`; telling the three apart required
WER forensics and bisection. The equivalent FluentGpu mistakes surface as named managed exceptions at the fault
site, with stacks. When WinUI's resource dictionary silently failed to load, the framework *kept running* visibly
mis-styled rather than failing loudly — which is how a benchmark shipped flattered numbers unnoticed.

### 6. Instruments & still open

- The frame-ID desktop-pixel probe now decodes on both hosts (it caught the DIP-vs-physical window bug). The
  current sampler's ~1 ms poll granularity caps visible-frame-coverage measurements (~52% ceiling on all runs on
  both hosts — a sampler artifact, **not** a framework ranking); a higher-rate capture is needed before publishing
  mutation→glass latency.
- Displayed-cadence FPS claims still wait on PresentMon v2 re-capture (nominal-vs-measured refresh).
- The tree-churn/scroll alloc items are resolved (§4). Remaining engine notes: the isolated 1.3 s localized-text
  stall (one frame in one rep, environmental); flagged-but-untaken cold-mount levers (Element motion-block
  side-record, RepaintBoundary clone) documented in the Wave E2 report.
- WinUI's cpu-pass windows render blank white by construction (the chained dispatcher loop saturates the UI
  thread, so XAML never composites — pre-existing on every scenario, measurement unaffected since that pass times
  mutation+UpdateLayout only). Visual parity is therefore verified on the cadence pass; documented in METHODOLOGY.
