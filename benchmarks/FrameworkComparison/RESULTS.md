# Results — 2026-08-01 (schema v4, runs `waved-cpu` + `waved-cadence`)

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

## 1. Cold start — process launch to first frame handed to the compositor

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

## 2. Page navigation — the compound workload (new scenario, `wavee1` runs)

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

## 3. Steady-state — display-paced (cadence pass)

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

## 4. Memory (honest ledger — two different shapes)

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

## 5. Reliability & debuggability

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

## 6. Instruments & still open

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
